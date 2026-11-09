using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareDesk.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace CareDesk.Infrastructure;

public sealed partial class SqliteAppointmentStore(string databasePath, ClinicClock clock, TimeProvider timeProvider) : IAppointmentStore, IWorkflowStore
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var schema = connection.CreateCommand();
        schema.CommandText = "CREATE TABLE IF NOT EXISTS clinicians (id TEXT PRIMARY KEY, name TEXT NOT NULL); CREATE TABLE IF NOT EXISTS appointments (id TEXT PRIMARY KEY, patient_name TEXT NOT NULL, patient_code TEXT NOT NULL, clinician_id TEXT NOT NULL, clinician_name TEXT NOT NULL, visit_type TEXT NOT NULL, start_utc TEXT NOT NULL, end_utc TEXT NOT NULL, status TEXT NOT NULL);";
        await schema.ExecuteNonQueryAsync(cancellationToken);
        var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var currentVersion = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (currentVersion == 0)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = await ColumnExistsAsync(connection, transaction, "appointments", "patient_request", cancellationToken)
                ? "UPDATE appointments SET patient_request=$message WHERE id=$id AND patient_request IS NULL; PRAGMA user_version=1;"
                : "ALTER TABLE appointments ADD COLUMN patient_request TEXT; UPDATE appointments SET patient_request=$message WHERE id=$id; PRAGMA user_version=1;";
            migration.Parameters.AddWithValue("$message", SeedData.ReschedulingMessage);
            migration.Parameters.AddWithValue("$id", "5c54fa11-3fce-4d43-b79a-5988c620e901");
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            currentVersion = 1;
        }
        if (currentVersion < 2)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            var addPatientId = await ColumnExistsAsync(connection, transaction, "appointments", "patient_id", cancellationToken) ? string.Empty : "ALTER TABLE appointments ADD COLUMN patient_id TEXT;";
            var addRevision = await ColumnExistsAsync(connection, transaction, "appointments", "revision", cancellationToken) ? string.Empty : "ALTER TABLE appointments ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;";
            migration.CommandText = $"""
                {addPatientId}
                {addRevision}
                CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, patient_id TEXT NOT NULL, patient_name TEXT NOT NULL, patient_code TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS scheduling_requests (id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL UNIQUE, appointment_id TEXT, owner_id TEXT, status TEXT NOT NULL, outcome_appointment_revision INTEGER, version INTEGER NOT NULL DEFAULT 0, FOREIGN KEY(conversation_id) REFERENCES conversations(id));
                CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, direction TEXT NOT NULL, body TEXT NOT NULL, created_utc TEXT NOT NULL, author_name TEXT NOT NULL, record_status TEXT NOT NULL, source_draft_id TEXT UNIQUE, FOREIGN KEY(conversation_id) REFERENCES conversations(id));
                CREATE TABLE IF NOT EXISTS reply_drafts (id TEXT PRIMARY KEY, request_id TEXT NOT NULL UNIQUE, body TEXT NOT NULL, kind TEXT NOT NULL, revision INTEGER NOT NULL, edited_by TEXT NOT NULL, updated_utc TEXT NOT NULL, appointment_revision INTEGER, appointment_start_utc TEXT, clinician_name TEXT, has_staff_edits INTEGER NOT NULL, FOREIGN KEY(request_id) REFERENCES scheduling_requests(id));
                CREATE TABLE IF NOT EXISTS conversation_reads (conversation_id TEXT NOT NULL, staff_id TEXT NOT NULL, last_read_utc TEXT NOT NULL, PRIMARY KEY(conversation_id, staff_id));
                UPDATE appointments SET patient_id = CASE id
                    WHEN '5c54fa11-3fce-4d43-b79a-5988c620e901' THEN 'bc34e389-6522-4a2d-bde5-70719846b41f'
                    WHEN '70a383cf-d9a0-4bfc-a4e0-f3c4841b5902' THEN '6d8d8a57-361d-46bc-90ed-639253975ccb'
                    WHEN 'c91ea090-d0f7-461c-b8eb-53c626a2b703' THEN 'aa3db2dc-781c-44db-ac16-6d3380cc63a5'
                    WHEN '991138dd-69f2-4c28-a7a8-808fb3a7d604' THEN '775dcd83-8cb6-41ec-ac5d-e1d94f61b74a'
                    ELSE lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6))) END
                WHERE patient_id IS NULL;
                PRAGMA user_version=2;
                """;
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            currentVersion = 2;
        }
        if (currentVersion < 3)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                UPDATE clinicians SET name='Dr. Andy Sterland' WHERE id='ccad4891-8249-47ea-910e-3c449af50302';
                UPDATE appointments SET clinician_name='Dr. Andy Sterland' WHERE clinician_id='ccad4891-8249-47ea-910e-3c449af50302';
                UPDATE appointments SET patient_name='Harshada Hole' WHERE id='5c54fa11-3fce-4d43-b79a-5988c620e901';
                UPDATE conversations SET patient_name='Harshada Hole' WHERE patient_id='bc34e389-6522-4a2d-bde5-70719846b41f';

                UPDATE conversations
                SET patient_id='aa3db2dc-781c-44db-ac16-6d3380cc63a5', patient_name='Mads Kristensen', patient_code='CD-1056'
                WHERE id='c48ff7d5-0d37-4e79-b7db-887e0708794b'
                  AND EXISTS (SELECT 1 FROM scheduling_requests WHERE id='41c7563c-feda-48d0-8389-92cad01a689d' AND status IN ('Open','InProgress') AND outcome_appointment_revision IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM reply_drafts WHERE request_id='41c7563c-feda-48d0-8389-92cad01a689d')
                  AND NOT EXISTS (SELECT 1 FROM messages WHERE conversation_id='c48ff7d5-0d37-4e79-b7db-887e0708794b' AND direction='Outgoing');
                UPDATE messages
                SET author_name='Mads Kristensen'
                WHERE conversation_id='c48ff7d5-0d37-4e79-b7db-887e0708794b' AND direction='Incoming'
                  AND EXISTS (SELECT 1 FROM conversations WHERE id='c48ff7d5-0d37-4e79-b7db-887e0708794b' AND patient_id='aa3db2dc-781c-44db-ac16-6d3380cc63a5');
                UPDATE scheduling_requests
                SET appointment_id='c91ea090-d0f7-461c-b8eb-53c626a2b703'
                WHERE id='41c7563c-feda-48d0-8389-92cad01a689d'
                  AND EXISTS (SELECT 1 FROM conversations WHERE id='c48ff7d5-0d37-4e79-b7db-887e0708794b' AND patient_id='aa3db2dc-781c-44db-ac16-6d3380cc63a5');
                UPDATE appointments SET patient_request=NULL WHERE id='5c54fa11-3fce-4d43-b79a-5988c620e901';
                UPDATE appointments
                SET patient_request=$message
                WHERE id='c91ea090-d0f7-461c-b8eb-53c626a2b703'
                  AND EXISTS (SELECT 1 FROM scheduling_requests WHERE id='41c7563c-feda-48d0-8389-92cad01a689d' AND appointment_id='c91ea090-d0f7-461c-b8eb-53c626a2b703' AND status IN ('Open','InProgress'));
                PRAGMA user_version=3;
                """;
            migration.Parameters.AddWithValue("$message", SeedData.ReschedulingMessage);
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            currentVersion = 3;
        }
        if (currentVersion < 4)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                UPDATE clinicians SET name='Dr. Andy Sterland' WHERE id='ccad4891-8249-47ea-910e-3c449af50302';
                UPDATE appointments SET clinician_name='Dr. Andy Sterland' WHERE clinician_id='ccad4891-8249-47ea-910e-3c449af50302';
                PRAGMA user_version=4;
                """;
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            currentVersion = 4;
        }
        if (currentVersion < 5)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                UPDATE appointments
                SET clinician_id='ccad4891-8249-47ea-910e-3c449af50302', clinician_name='Dr. Andy Sterland'
                WHERE id='5c54fa11-3fce-4d43-b79a-5988c620e901';
                UPDATE appointments
                SET clinician_id='1e5ab4f0-d911-4e31-beb0-d4e19d665b01', clinician_name='Dr. Dalia Abo Sheasha'
                WHERE id='c91ea090-d0f7-461c-b8eb-53c626a2b703';
                PRAGMA user_version=5;
                """;
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 6)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var previousScheduleDay = new DateOnly(2026, 11, 9);
            var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "SELECT id, start_utc, end_utc FROM appointments";
            var appointmentsToMove = new List<(string Id, DateTimeOffset Start, DateTimeOffset End)>();
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var start = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    if (DateOnly.FromDateTime(clock.ToLocal(start)) != previousScheduleDay) continue;
                    var end = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    appointmentsToMove.Add((reader.GetString(0), start, end));
                }
            }
            foreach (var appointment in appointmentsToMove)
            {
                var localStart = clock.ToLocal(appointment.Start).AddDays(7);
                var localEnd = clock.ToLocal(appointment.End).AddDays(7);
                var start = clock.ToUtc(localStart).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var end = clock.ToUtc(localEnd).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE appointments SET start_utc=$start, end_utc=$end WHERE id=$id; UPDATE reply_drafts SET appointment_start_utc=$start WHERE appointment_start_utc=$previousStart AND request_id IN (SELECT id FROM scheduling_requests WHERE appointment_id=$id);";
                update.Parameters.AddWithValue("$id", appointment.Id);
                update.Parameters.AddWithValue("$start", start);
                update.Parameters.AddWithValue("$end", end);
                update.Parameters.AddWithValue("$previousStart", appointment.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = "PRAGMA user_version=6";
            await migration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM appointments";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            var clinicDate = DateOnly.FromDateTime(clock.ToLocal(timeProvider.GetUtcNow()));
            await ResetAsync(SeedData.Clinicians, SeedData.ForDay(SeedData.DefaultScheduleDay(clinicDate), clock), cancellationToken);
        }
        else
        {
            await SeedMessagingIfEmptyAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Clinician>> GetCliniciansAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id, name FROM clinicians ORDER BY name";
        var values = new List<Clinician>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(new(new(Guid.Parse(reader.GetString(0))), reader.GetString(1)));
        return values;
    }

    public async Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT id, patient_name, patient_code, clinician_id, clinician_name, visit_type, start_utc, end_utc, status, patient_request, patient_id, revision FROM appointments";
        var values = new List<Appointment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new(new(Guid.Parse(reader.GetString(0))), reader.GetString(1), reader.GetString(2), new(Guid.Parse(reader.GetString(3))), reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9))
            {
                PatientId = new(Guid.Parse(reader.GetString(10))),
                Revision = reader.GetInt32(11)
            });
        }
        return values;
    }

    public async Task SaveAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE appointments SET clinician_id=$clinician, clinician_name=$clinicianName, start_utc=$start, end_utc=$end, patient_request=$request, revision=$revision WHERE id=$id AND revision=$expectedRevision";
        command.Parameters.AddWithValue("$request", (object?)appointment.PatientRequest ?? DBNull.Value);
        command.Parameters.AddWithValue("$clinician", appointment.ClinicianId.Value.ToString("D")); command.Parameters.AddWithValue("$clinicianName", appointment.ClinicianName);
        command.Parameters.AddWithValue("$start", appointment.StartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$end", appointment.EndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", appointment.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$revision", appointment.Revision);
        command.Parameters.AddWithValue("$expectedRevision", Math.Max(0, appointment.Revision - 1));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("The selected appointment could not be saved because it changed or no longer exists. Refresh and review it before retrying.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetAsync(IReadOnlyList<Clinician> clinicians, IReadOnlyList<Appointment> appointments, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var clear = connection.CreateCommand(); clear.Transaction = (SqliteTransaction)transaction; clear.CommandText = "DELETE FROM conversation_reads; DELETE FROM reply_drafts; DELETE FROM messages; DELETE FROM scheduling_requests; DELETE FROM conversations; DELETE FROM appointments; DELETE FROM clinicians;"; await clear.ExecuteNonQueryAsync(cancellationToken);
        foreach (var clinician in clinicians)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = "INSERT INTO clinicians (id, name) VALUES ($id, $name)"; command.Parameters.AddWithValue("$id", clinician.Id.Value.ToString("D")); command.Parameters.AddWithValue("$name", clinician.DisplayName); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var appointment in appointments) await InsertAppointmentAsync(connection, (SqliteTransaction)transaction, appointment, cancellationToken);
        await SeedMessagingAsync(connection, (SqliteTransaction)transaction, appointments, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertAppointmentAsync(SqliteConnection connection, SqliteTransaction transaction, Appointment a, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO appointments (id, patient_name, patient_code, clinician_id, clinician_name, visit_type, start_utc, end_utc, status, patient_request, patient_id, revision) VALUES ($id,$patient,$code,$clinician,$clinicianName,$visit,$start,$end,$status,$request,$patientId,$revision)";
        command.Parameters.AddWithValue("$request", (object?)a.PatientRequest ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", a.Id.Value.ToString("D")); command.Parameters.AddWithValue("$patient", a.PatientName); command.Parameters.AddWithValue("$code", a.PatientCode); command.Parameters.AddWithValue("$clinician", a.ClinicianId.Value.ToString("D")); command.Parameters.AddWithValue("$clinicianName", a.ClinicianName); command.Parameters.AddWithValue("$visit", a.VisitType); command.Parameters.AddWithValue("$start", a.StartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$end", a.EndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$status", a.Status); command.Parameters.AddWithValue("$patientId", a.PatientId.Value.ToString("D")); command.Parameters.AddWithValue("$revision", a.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public sealed class SampleReplySuggestionService : IReplySuggestionService
{
    public string ModeLabel => "Sample mode - no AI call";

    public Task<ReplySuggestionResult> PrepareAsync(ReplySuggestionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Message)) return Task.FromResult(ReplySuggestionResult.Failure("This appointment has no patient message to answer.", ModeLabel));
        var text = request.IsCommitted
            ? $"Your appointment has been moved to {request.ProposedLocalStart:dddd, MMMM d, yyyy}, at {request.ProposedLocalStart:h:mm tt} with {request.ClinicianName}. Please let us know if that no longer works for you."
            : $"We can offer {request.ProposedLocalStart:dddd, MMMM d, yyyy}, at {request.ProposedLocalStart:h:mm tt} with {request.ClinicianName}. Would that work for you?";
        return Task.FromResult(ReplySuggestionResult.Success(text, "Sample reply suggestion - no AI call"));
    }
}

public sealed class OpenAiReplySuggestionService(IConfiguration configuration, ILogger<OpenAiReplySuggestionService> logger) : IReplySuggestionService
{
    private readonly string? apiKey = configuration["CAREDESK_AI_API_KEY"];
    private readonly string? model = configuration["CAREDESK_AI_MODEL"];
    private ChatClient? Client => !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(model) ? new ChatClient(model, apiKey) : null;
    public string ModeLabel => "Live mode";

    public async Task<ReplySuggestionResult> PrepareAsync(ReplySuggestionRequest request, CancellationToken cancellationToken)
    {
        var client = Client;
        if (client is null) return ReplySuggestionResult.Failure("Live mode is selected but not configured. Set CAREDESK_AI_API_KEY and CAREDESK_AI_MODEL, then restart the app.", ModeLabel);
        var date = request.ProposedLocalStart.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        var time = request.ProposedLocalStart.ToString("h:mm tt", CultureInfo.CurrentCulture);
        var state = request.IsCommitted
            ? "The appointment change has already been saved. Confirm it without implying delivery."
            : "The appointment has not been changed. Offer this time as a question and do not claim it is booked.";
        var system = "You draft one concise administrative scheduling reply to a patient. Do not provide clinical advice, diagnosis, triage, or urgency classification. Use only the supplied patient message and exact scheduling facts. Do not invent dates, times, clinicians, delivery, or completion. Return JSON with one string property named replyText.";
        var user = $"Patient message: {request.Message}\nWorkflow state: {state}\nExact date: {date}\nExact time: {time}\nExact clinician: {request.ClinicianName}\nClinic time zone: {request.TimeZoneId}\nDuration: {request.DurationMinutes} minutes. Use the exact date, time, and clinician text in the reply.";
        try
        {
            var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
            var completion = await client.CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(user)], options, cancellationToken);
            var raw = completion.Value.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return ReplySuggestionResult.Failure("The AI service returned an empty reply suggestion. Please retry or write the reply manually.", ModeLabel);
            using var json = JsonDocument.Parse(raw);
            if (!json.RootElement.TryGetProperty("replyText", out var replyElement) || replyElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(replyElement.GetString()))
                return ReplySuggestionResult.Failure("The AI service returned an unreadable reply suggestion. Please retry or write the reply manually.", ModeLabel);
            var reply = replyElement.GetString()!.Trim();
            if (!reply.Contains(time, StringComparison.CurrentCultureIgnoreCase) || !reply.Contains(request.ClinicianName, StringComparison.CurrentCultureIgnoreCase))
                return ReplySuggestionResult.Failure("The AI reply omitted required appointment facts. Review the appointment and retry.", ModeLabel);
            if (!request.IsCommitted && (reply.Contains("has been moved", StringComparison.OrdinalIgnoreCase) || reply.Contains("is now scheduled", StringComparison.OrdinalIgnoreCase)))
                return ReplySuggestionResult.Failure("The AI reply incorrectly claimed the appointment was already changed. Review the proposal manually.", ModeLabel);
            return ReplySuggestionResult.Success(reply, "AI reply suggestion · review required");
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The scheduling model returned unreadable reply data for appointment {AppointmentId}", request.Appointment.Id);
            return ReplySuggestionResult.Failure("The AI service returned an unreadable reply suggestion. Please retry or write the reply manually.", ModeLabel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Live reply suggestion failed for appointment {AppointmentId}", request.Appointment.Id);
            return ReplySuggestionResult.Failure("The AI service could not prepare a reply suggestion. Check the connection and configuration, then retry.", ModeLabel);
        }
    }
}

public sealed class SampleDraftService : ISchedulingDraftService
{
    private static readonly Regex TenAm = new(@"\b10(?::00)?\s*(?:a\.?m\.?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public string ModeLabel => "Sample mode - no AI call";

    public Task<DraftResult> PrepareAsync(DraftRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Message)) return Task.FromResult(DraftResult.Failure("This appointment has no patient request to interpret."));
        if (!request.Message.Contains("same day", StringComparison.OrdinalIgnoreCase) || !TenAm.IsMatch(request.Message))
            return Task.FromResult(DraftResult.Failure("Sample mode supports the example request for 10:00 AM on the same day. Edit the booking manually or switch to a configured live mode."));
        var local = request.ReferenceDate.ToDateTime(new TimeOnly(10, 0));
        return Task.FromResult(DraftResult.Success(new(local, null, null, "Sample recommendation - no AI call", null)));
    }
}

public sealed class OpenAiDraftService(IConfiguration configuration, ClinicClock clock, ILogger<OpenAiDraftService> logger) : ISchedulingDraftService
{
    private readonly string? apiKey = configuration["CAREDESK_AI_API_KEY"];
    private readonly string? model = configuration["CAREDESK_AI_MODEL"];
    private ChatClient? Client => !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(model) ? new ChatClient(model, apiKey) : null;
    public string ModeLabel => "Live mode";

    public async Task<DraftResult> PrepareAsync(DraftRequest request, CancellationToken cancellationToken)
    {
        var client = Client;
        if (client is null) return DraftResult.Failure("Live mode is selected but not configured. Set CAREDESK_AI_API_KEY and CAREDESK_AI_MODEL, then restart the app.");
        var system = "You recommend scheduling changes from the selected patient's original rescheduling message. Never save, select a patient, issue SQL, or assess availability. Use only the selected appointment context. Return JSON with requestedLocalStart (yyyy-MM-ddTHH:mm or null), clinicianName (exact supplied name or null), durationMinutes (integer or null), and needsReview (string or null). Null means preserve the current value. If requested details are missing or unsupported, explain in needsReview. Do not invent dates or clinicians.";
        var user = $"Clinic time zone: {request.TimeZoneId}. Reference clinic date: {request.ReferenceDate:yyyy-MM-dd}. Selected patient: {request.Appointment.PatientName}; current clinician: {request.Appointment.ClinicianName}; current start: {clock.ToLocal(request.Appointment.StartUtc):yyyy-MM-ddTHH:mm}; duration: {(request.Appointment.EndUtc - request.Appointment.StartUtc).TotalMinutes} minutes. Clinicians: {string.Join(", ", request.Clinicians.Select(c => c.DisplayName))}. Original message: {request.Message}";
        try
        {
            var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
            var completion = await client.CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(user)], options, cancellationToken);
            var raw = completion.Value.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return DraftResult.Failure("The scheduling service returned an empty draft. Please retry.");
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            DateTime? start = null;
            if (root.TryGetProperty("requestedLocalStart", out var date) && date.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(date.GetString()))
            {
                if (!DateTime.TryParseExact(date.GetString(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return DraftResult.Failure("The scheduling service returned an invalid date and time. Review the proposal manually.");
                start = parsed;
            }
            ClinicianId? clinician = null;
            if (root.TryGetProperty("clinicianName", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            {
                var match = request.Clinicians.FirstOrDefault(c => string.Equals(c.DisplayName, name.GetString(), StringComparison.OrdinalIgnoreCase));
                if (match is null) return DraftResult.Failure("The scheduling service suggested an unrecognized clinician. Review the proposal manually.");
                clinician = match.Id;
            }
            int? duration = root.TryGetProperty("durationMinutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number && minutes.TryGetInt32(out var value) ? value : null;
            var review = root.TryGetProperty("needsReview", out var needsReview) && needsReview.ValueKind == JsonValueKind.String ? needsReview.GetString() : null;
            if (start is null && string.IsNullOrWhiteSpace(review)) return DraftResult.Failure("The request did not provide a date and time. Review the proposal manually.");
            return DraftResult.Success(new(start, clinician, duration, "AI recommendation · review required", review));
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { logger.LogWarning(ex, "The scheduling model returned unreadable draft data"); return DraftResult.Failure("The scheduling service returned an unreadable draft. Please retry or edit manually."); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Live scheduling draft request failed for appointment {AppointmentId}", request.Appointment.Id); return DraftResult.Failure("The scheduling service could not prepare a draft. Check the connection and configuration, then retry."); }
    }
}
