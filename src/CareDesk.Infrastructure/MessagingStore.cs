using System.Globalization;
using CareDesk.Core;
using Microsoft.Data.Sqlite;

namespace CareDesk.Infrastructure;

public sealed partial class SqliteAppointmentStore
{
    public async Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(StaffId currentStaffId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.patient_id, c.patient_name, c.patient_code,
                   r.id, r.appointment_id, r.owner_id, r.status, r.outcome_appointment_revision, r.version,
                   d.id, d.body, d.kind, d.revision, d.edited_by, d.updated_utc, d.appointment_revision, d.appointment_start_utc, d.clinician_name, d.has_staff_edits,
                   COALESCE((SELECT body FROM messages WHERE conversation_id=c.id ORDER BY created_utc DESC LIMIT 1), ''),
                   COALESCE((SELECT MAX(created_utc) FROM messages WHERE conversation_id=c.id), '1970-01-01T00:00:00.0000000+00:00'),
                   CASE WHEN EXISTS (
                       SELECT 1 FROM messages m
                       LEFT JOIN conversation_reads cr ON cr.conversation_id=c.id AND cr.staff_id=$staff
                       WHERE m.conversation_id=c.id AND m.direction='Incoming' AND (cr.last_read_utc IS NULL OR m.created_utc > cr.last_read_utc)
                   ) THEN 1 ELSE 0 END
            FROM conversations c
            JOIN scheduling_requests r ON r.conversation_id=c.id
            LEFT JOIN reply_drafts d ON d.request_id=r.id
            ORDER BY CASE r.status WHEN 'Open' THEN 0 WHEN 'InProgress' THEN 1 WHEN 'WaitingOnPatient' THEN 2 WHEN 'ReplyPending' THEN 3 ELSE 4 END,
                     COALESCE((SELECT MAX(created_utc) FROM messages WHERE conversation_id=c.id), '') DESC
            """;
        command.Parameters.AddWithValue("$staff", currentStaffId.Value.ToString("D"));
        var values = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var conversationId = new ConversationId(Guid.Parse(reader.GetString(0)));
            var lastActivity = ParseInstant(reader.GetString(21));
            var conversation = new Conversation(conversationId, new(Guid.Parse(reader.GetString(1))), reader.GetString(2), reader.GetString(3), lastActivity);
            var request = ReadRequest(reader, 4, conversationId);
            var draft = reader.IsDBNull(10) ? null : ReadDraft(reader, 10, request.Id);
            values.Add(new(conversation, request, draft, reader.GetString(20), reader.GetInt32(22) == 1, OwnerDisplayName(request.OwnerId)));
        }
        return values;
    }

    public async Task<ConversationDetails?> GetConversationAsync(ConversationId conversationId, StaffId currentStaffId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadConversationAsync(connection, null, conversationId, currentStaffId, cancellationToken);
    }

    public async Task<ConversationDetails?> GetRequestForAppointmentAsync(AppointmentId appointmentId, RequestId? preferredRequestId, StaffId currentStaffId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = preferredRequestId is null
            ? "SELECT conversation_id FROM scheduling_requests WHERE appointment_id=$appointment ORDER BY CASE status WHEN 'Completed' THEN 1 ELSE 0 END, version DESC LIMIT 1"
            : "SELECT conversation_id FROM scheduling_requests WHERE appointment_id=$appointment AND id=$request LIMIT 1";
        command.Parameters.AddWithValue("$appointment", appointmentId.Value.ToString("D"));
        if (preferredRequestId is { } requestId) command.Parameters.AddWithValue("$request", requestId.Value.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string id) return null;
        return await LoadConversationAsync(connection, null, new(Guid.Parse(id)), currentStaffId, cancellationToken);
    }

    public async Task MarkConversationReadAsync(ConversationId conversationId, StaffId staffId, DateTimeOffset readUtc, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_reads (conversation_id, staff_id, last_read_utc) VALUES ($conversation,$staff,$read)
            ON CONFLICT(conversation_id, staff_id) DO UPDATE SET last_read_utc=excluded.last_read_utc
            """;
        command.Parameters.AddWithValue("$conversation", conversationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$staff", staffId.Value.ToString("D"));
        command.Parameters.AddWithValue("$read", FormatInstant(readUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SchedulingRequest> UpdateOwnerAsync(RequestId requestId, StaffId? ownerId, SchedulingWorkStatus status, int expectedVersion, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduling_requests SET owner_id=$owner, status=$status, version=version+1 WHERE id=$id AND version=$version";
        command.Parameters.AddWithValue("$owner", ownerId is null ? DBNull.Value : ownerId.Value.Value.ToString("D"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict();
        return (await LoadRequestAsync(connection, null, requestId, cancellationToken))!;
    }

    public async Task<ReplyDraft> SaveDraftAsync(ReplyDraft draft, int expectedRequestVersion, int? expectedDraftRevision, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureRequestVersionAsync(connection, transaction, draft.RequestId, expectedRequestVersion, cancellationToken);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedDraftRevision is null)
        {
            command.CommandText = """
                INSERT INTO reply_drafts (id, request_id, body, kind, revision, edited_by, updated_utc, appointment_revision, appointment_start_utc, clinician_name, has_staff_edits)
                VALUES ($id,$request,$body,$kind,0,$editor,$updated,$appointmentRevision,$appointmentStart,$clinician,$staffEdits)
                ON CONFLICT(request_id) DO NOTHING
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE reply_drafts SET body=$body, kind=$kind, revision=revision+1, edited_by=$editor, updated_utc=$updated,
                    appointment_revision=$appointmentRevision, appointment_start_utc=$appointmentStart, clinician_name=$clinician, has_staff_edits=$staffEdits
                WHERE request_id=$request AND id=$id AND revision=$expectedDraftRevision
                """;
            command.Parameters.AddWithValue("$expectedDraftRevision", expectedDraftRevision.Value);
        }
        AddDraftParameters(command, draft);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict("The reply draft changed in another view. Reload it and review your unsaved text before retrying.");
        var saved = await LoadDraftAsync(connection, transaction, draft.RequestId, cancellationToken) ?? throw new InvalidOperationException("The reply draft could not be reloaded after saving.");
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<RequestMutation> CommitRescheduleAsync(RequestId requestId, int expectedRequestVersion, Appointment appointment, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var booking = connection.CreateCommand();
        booking.Transaction = transaction;
        booking.CommandText = """
            UPDATE appointments SET clinician_id=$clinician, clinician_name=$clinicianName, start_utc=$start, end_utc=$end,
                patient_request=NULL, revision=$revision
            WHERE id=$id AND revision=$expectedRevision
            """;
        booking.Parameters.AddWithValue("$clinician", appointment.ClinicianId.Value.ToString("D"));
        booking.Parameters.AddWithValue("$clinicianName", appointment.ClinicianName);
        booking.Parameters.AddWithValue("$start", FormatInstant(appointment.StartUtc));
        booking.Parameters.AddWithValue("$end", FormatInstant(appointment.EndUtc));
        booking.Parameters.AddWithValue("$revision", appointment.Revision);
        booking.Parameters.AddWithValue("$expectedRevision", Math.Max(0, appointment.Revision - 1));
        booking.Parameters.AddWithValue("$id", appointment.Id.Value.ToString("D"));
        if (await booking.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict("The appointment changed in another view. Refresh it before saving this request.");

        var request = connection.CreateCommand();
        request.Transaction = transaction;
        request.CommandText = """
            UPDATE scheduling_requests SET status='ReplyPending', outcome_appointment_revision=$appointmentRevision, version=version+1
            WHERE id=$id AND version=$version AND appointment_id=$appointment
            """;
        request.Parameters.AddWithValue("$appointmentRevision", appointment.Revision);
        request.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        request.Parameters.AddWithValue("$version", expectedRequestVersion);
        request.Parameters.AddWithValue("$appointment", appointment.Id.Value.ToString("D"));
        if (await request.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict();
        var updatedRequest = (await LoadRequestAsync(connection, transaction, requestId, cancellationToken))!;
        var draft = await LoadDraftAsync(connection, transaction, requestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(updatedRequest, appointment, draft);
    }

    public async Task<RequestMutation> RecordReplyAsync(RequestId requestId, int expectedRequestVersion, ReplyDraft draft, CommunicationMessage message, SchedulingWorkStatus finalStatus, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var duplicate = connection.CreateCommand();
        duplicate.Transaction = transaction;
        duplicate.CommandText = "SELECT COUNT(*) FROM messages WHERE source_draft_id=$draft";
        duplicate.Parameters.AddWithValue("$draft", draft.Id.Value.ToString("D"));
        if (Convert.ToInt64(await duplicate.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
        {
            var current = await LoadRequestAsync(connection, transaction, requestId, cancellationToken) ?? throw new InvalidOperationException("The request no longer exists.");
            await transaction.CommitAsync(cancellationToken);
            return new(current, null, null);
        }

        await EnsureRequestVersionAsync(connection, transaction, requestId, expectedRequestVersion, cancellationToken);
        var currentDraft = await LoadDraftAsync(connection, transaction, requestId, cancellationToken);
        if (currentDraft is null || currentDraft.Id != draft.Id || currentDraft.Revision != draft.Revision) throw Conflict("The reply draft changed in another view. Reload it before recording the reply.");

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO messages (id, conversation_id, direction, body, created_utc, author_name, record_status, source_draft_id) SELECT $id, conversation_id, $direction, $body, $created, $author, $recordStatus, $sourceDraft FROM scheduling_requests WHERE id=$request";
        insert.Parameters.AddWithValue("$id", message.Id.Value.ToString("D"));
        insert.Parameters.AddWithValue("$direction", message.Direction.ToString());
        insert.Parameters.AddWithValue("$body", message.Body);
        insert.Parameters.AddWithValue("$created", FormatInstant(message.CreatedUtc));
        insert.Parameters.AddWithValue("$author", message.AuthorDisplayName);
        insert.Parameters.AddWithValue("$recordStatus", message.RecordStatus);
        insert.Parameters.AddWithValue("$sourceDraft", draft.Id.Value.ToString("D"));
        insert.Parameters.AddWithValue("$request", requestId.Value.ToString("D"));
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("The reply could not be recorded because the request no longer exists.");

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM reply_drafts WHERE id=$id AND revision=$revision";
        delete.Parameters.AddWithValue("$id", draft.Id.Value.ToString("D"));
        delete.Parameters.AddWithValue("$revision", draft.Revision);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict("The reply draft changed before it could be recorded.");

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE scheduling_requests SET status=$status, version=version+1 WHERE id=$id AND version=$version";
        update.Parameters.AddWithValue("$status", finalStatus.ToString());
        update.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        update.Parameters.AddWithValue("$version", expectedRequestVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw Conflict();
        var updatedRequest = (await LoadRequestAsync(connection, transaction, requestId, cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return new(updatedRequest, null, null);
    }

    private async Task SeedMessagingIfEmptyAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM conversations";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0) return;
        var appointments = await GetAppointmentsAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await SeedMessagingAsync(connection, transaction, appointments, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SeedMessagingAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Appointment> appointments, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var mads = appointments.FirstOrDefault(a => a.Id.Value == Guid.Parse("c91ea090-d0f7-461c-b8eb-53c626a2b703"));
        var anson = appointments.FirstOrDefault(a => a.Id.Value == Guid.Parse("70a383cf-d9a0-4bfc-a4e0-f3c4841b5902"));
        var jason = appointments.FirstOrDefault(a => a.Id.Value == Guid.Parse("991138dd-69f2-4c28-a7a8-808fb3a7d604"));
        if (mads is not null)
        {
            await InsertConversationAsync(connection, transaction, MessagingSeedData.MadsConversationId, MessagingSeedData.MadsRequestId, mads, null, SchedulingWorkStatus.Open, cancellationToken);
            await InsertMessageAsync(connection, transaction, new(Guid.Parse("3b930c38-0d7e-4d8c-a7bb-2f4d57c06d14")), MessagingSeedData.MadsConversationId, MessageDirection.Incoming, SeedData.ReschedulingMessage, now.AddMinutes(-42), mads.PatientName, "Received locally", null, cancellationToken);
        }
        if (anson is not null)
        {
            await InsertConversationAsync(connection, transaction, MessagingSeedData.AnsonConversationId, MessagingSeedData.AnsonRequestId, anson, MessagingSeedData.CurrentStaff.Id, SchedulingWorkStatus.InProgress, cancellationToken);
            await InsertMessageAsync(connection, transaction, new(Guid.Parse("4655416d-04b4-4c3b-8b2d-b27c94e8b3fd")), MessagingSeedData.AnsonConversationId, MessageDirection.Incoming, "Could we move this appointment to later in the day? Which afternoon times work?", now.AddHours(-3), anson.PatientName, "Received locally", null, cancellationToken);
        }
        if (jason is not null)
        {
            await InsertConversationAsync(connection, transaction, MessagingSeedData.JasonConversationId, MessagingSeedData.JasonRequestId, jason, MessagingSeedData.SecondStaff.Id, SchedulingWorkStatus.Completed, cancellationToken);
            await InsertMessageAsync(connection, transaction, new(Guid.Parse("83483df4-f2f6-47d3-b9a8-71f88cba9a25")), MessagingSeedData.JasonConversationId, MessageDirection.Incoming, "Please confirm the appointment time on my schedule.", now.AddDays(-1), jason.PatientName, "Received locally", null, cancellationToken);
            await InsertMessageAsync(connection, transaction, new(Guid.Parse("f267414c-a24a-4b8c-89e9-596120e137c0")), MessagingSeedData.JasonConversationId, MessageDirection.Outgoing, "Your appointment remains scheduled as shown. Please contact the clinic if you need a change.", now.AddDays(-1).AddMinutes(12), MessagingSeedData.SecondStaff.DisplayName, "Recorded locally · not delivered", null, cancellationToken);
        }
    }

    private static async Task InsertConversationAsync(SqliteConnection connection, SqliteTransaction transaction, ConversationId conversationId, RequestId requestId, Appointment appointment, StaffId? ownerId, SchedulingWorkStatus status, CancellationToken cancellationToken)
    {
        var conversation = connection.CreateCommand();
        conversation.Transaction = transaction;
        conversation.CommandText = "INSERT INTO conversations (id, patient_id, patient_name, patient_code) VALUES ($id,$patientId,$patient,$code)";
        conversation.Parameters.AddWithValue("$id", conversationId.Value.ToString("D"));
        conversation.Parameters.AddWithValue("$patientId", appointment.PatientId.Value.ToString("D"));
        conversation.Parameters.AddWithValue("$patient", appointment.PatientName);
        conversation.Parameters.AddWithValue("$code", appointment.PatientCode);
        await conversation.ExecuteNonQueryAsync(cancellationToken);
        var request = connection.CreateCommand();
        request.Transaction = transaction;
        request.CommandText = "INSERT INTO scheduling_requests (id, conversation_id, appointment_id, owner_id, status, outcome_appointment_revision, version) VALUES ($id,$conversation,$appointment,$owner,$status,NULL,0)";
        request.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        request.Parameters.AddWithValue("$conversation", conversationId.Value.ToString("D"));
        request.Parameters.AddWithValue("$appointment", appointment.Id.Value.ToString("D"));
        request.Parameters.AddWithValue("$owner", ownerId is null ? DBNull.Value : ownerId.Value.Value.ToString("D"));
        request.Parameters.AddWithValue("$status", status.ToString());
        await request.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMessageAsync(SqliteConnection connection, SqliteTransaction transaction, MessageId messageId, ConversationId conversationId, MessageDirection direction, string body, DateTimeOffset createdUtc, string author, string recordStatus, DraftId? sourceDraftId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO messages (id, conversation_id, direction, body, created_utc, author_name, record_status, source_draft_id) VALUES ($id,$conversation,$direction,$body,$created,$author,$status,$draft)";
        command.Parameters.AddWithValue("$id", messageId.Value.ToString("D"));
        command.Parameters.AddWithValue("$conversation", conversationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$direction", direction.ToString());
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created", FormatInstant(createdUtc));
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$status", recordStatus);
        command.Parameters.AddWithValue("$draft", sourceDraftId is null ? DBNull.Value : sourceDraftId.Value.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ConversationDetails?> LoadConversationAsync(SqliteConnection connection, SqliteTransaction? transaction, ConversationId conversationId, StaffId currentStaffId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.patient_id, c.patient_name, c.patient_code,
                   r.id, r.appointment_id, r.owner_id, r.status, r.outcome_appointment_revision, r.version,
                   d.id, d.body, d.kind, d.revision, d.edited_by, d.updated_utc, d.appointment_revision, d.appointment_start_utc, d.clinician_name, d.has_staff_edits
            FROM conversations c
            JOIN scheduling_requests r ON r.conversation_id=c.id
            LEFT JOIN reply_drafts d ON d.request_id=r.id
            WHERE c.id=$id
            """;
        command.Parameters.AddWithValue("$id", conversationId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = ReadRequest(reader, 3, conversationId);
        var draft = reader.IsDBNull(9) ? null : ReadDraft(reader, 9, request.Id);
        var patientId = new PatientId(Guid.Parse(reader.GetString(0)));
        var patientName = reader.GetString(1);
        var patientCode = reader.GetString(2);
        await reader.DisposeAsync();

        var messages = new List<CommunicationMessage>();
        var messageCommand = connection.CreateCommand();
        messageCommand.Transaction = transaction;
        messageCommand.CommandText = "SELECT id, direction, body, created_utc, author_name, record_status, source_draft_id FROM messages WHERE conversation_id=$id ORDER BY created_utc, id";
        messageCommand.Parameters.AddWithValue("$id", conversationId.Value.ToString("D"));
        await using var messageReader = await messageCommand.ExecuteReaderAsync(cancellationToken);
        while (await messageReader.ReadAsync(cancellationToken))
        {
            messages.Add(new(new(Guid.Parse(messageReader.GetString(0))), conversationId, Enum.Parse<MessageDirection>(messageReader.GetString(1)), messageReader.GetString(2), ParseInstant(messageReader.GetString(3)), messageReader.GetString(4), messageReader.GetString(5), messageReader.IsDBNull(6) ? null : new(Guid.Parse(messageReader.GetString(6)))));
        }
        var lastActivity = messages.Count == 0 ? timeProvider.GetUtcNow() : messages[^1].CreatedUtc;
        Appointment? appointment = null;
        if (request.AppointmentId is { } appointmentId) appointment = (await LoadAppointmentAsync(connection, transaction, appointmentId, cancellationToken));
        return new(new(conversationId, patientId, patientName, patientCode, lastActivity), request, draft, messages, appointment, OwnerDisplayName(request.OwnerId));
    }

    private static SchedulingRequest ReadRequest(SqliteDataReader reader, int offset, ConversationId conversationId) =>
        new(new(Guid.Parse(reader.GetString(offset))), conversationId,
            reader.IsDBNull(offset + 1) ? null : new(Guid.Parse(reader.GetString(offset + 1))),
            reader.IsDBNull(offset + 2) ? null : new(Guid.Parse(reader.GetString(offset + 2))),
            Enum.Parse<SchedulingWorkStatus>(reader.GetString(offset + 3)),
            reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
            reader.GetInt32(offset + 5));

    private static ReplyDraft ReadDraft(SqliteDataReader reader, int offset, RequestId requestId) =>
        new(new(Guid.Parse(reader.GetString(offset))), requestId, reader.GetString(offset + 1), Enum.Parse<ReplyDraftKind>(reader.GetString(offset + 2)), reader.GetInt32(offset + 3), new(Guid.Parse(reader.GetString(offset + 4))), ParseInstant(reader.GetString(offset + 5)), reader.IsDBNull(offset + 6) ? null : reader.GetInt32(offset + 6), reader.IsDBNull(offset + 7) ? null : ParseInstant(reader.GetString(offset + 7)), reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8), reader.GetInt32(offset + 9) == 1);

    private static async Task<SchedulingRequest?> LoadRequestAsync(SqliteConnection connection, SqliteTransaction? transaction, RequestId requestId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT conversation_id, appointment_id, owner_id, status, outcome_appointment_revision, version FROM scheduling_requests WHERE id=$id";
        command.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(requestId, new(Guid.Parse(reader.GetString(0))), reader.IsDBNull(1) ? null : new(Guid.Parse(reader.GetString(1))), reader.IsDBNull(2) ? null : new(Guid.Parse(reader.GetString(2))), Enum.Parse<SchedulingWorkStatus>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetInt32(5));
    }

    private static async Task<ReplyDraft?> LoadDraftAsync(SqliteConnection connection, SqliteTransaction? transaction, RequestId requestId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, body, kind, revision, edited_by, updated_utc, appointment_revision, appointment_start_utc, clinician_name, has_staff_edits FROM reply_drafts WHERE request_id=$request";
        command.Parameters.AddWithValue("$request", requestId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDraft(reader, 0, requestId) : null;
    }

    private static async Task<Appointment?> LoadAppointmentAsync(SqliteConnection connection, SqliteTransaction? transaction, AppointmentId appointmentId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT patient_name, patient_code, clinician_id, clinician_name, visit_type, start_utc, end_utc, status, patient_request, patient_id, revision FROM appointments WHERE id=$id";
        command.Parameters.AddWithValue("$id", appointmentId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(appointmentId, reader.GetString(0), reader.GetString(1), new(Guid.Parse(reader.GetString(2))), reader.GetString(3), reader.GetString(4), ParseInstant(reader.GetString(5)), ParseInstant(reader.GetString(6)), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)) { PatientId = new(Guid.Parse(reader.GetString(9))), Revision = reader.GetInt32(10) };
    }

    private static async Task EnsureRequestVersionAsync(SqliteConnection connection, SqliteTransaction transaction, RequestId requestId, int expectedVersion, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM scheduling_requests WHERE id=$id";
        command.Parameters.AddWithValue("$id", requestId.Value.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || Convert.ToInt32(value, CultureInfo.InvariantCulture) != expectedVersion) throw Conflict();
    }

    private static void AddDraftParameters(SqliteCommand command, ReplyDraft draft)
    {
        command.Parameters.AddWithValue("$id", draft.Id.Value.ToString("D"));
        command.Parameters.AddWithValue("$request", draft.RequestId.Value.ToString("D"));
        command.Parameters.AddWithValue("$body", draft.Body);
        command.Parameters.AddWithValue("$kind", draft.Kind.ToString());
        command.Parameters.AddWithValue("$editor", draft.EditedBy.Value.ToString("D"));
        command.Parameters.AddWithValue("$updated", FormatInstant(draft.UpdatedUtc));
        command.Parameters.AddWithValue("$appointmentRevision", draft.AppointmentRevision is null ? DBNull.Value : draft.AppointmentRevision.Value);
        command.Parameters.AddWithValue("$appointmentStart", draft.AppointmentStartUtc is null ? DBNull.Value : FormatInstant(draft.AppointmentStartUtc.Value));
        command.Parameters.AddWithValue("$clinician", (object?)draft.ClinicianName ?? DBNull.Value);
        command.Parameters.AddWithValue("$staffEdits", draft.HasStaffEdits ? 1 : 0);
    }

    private static string OwnerDisplayName(StaffId? ownerId) => ownerId is null
        ? "Unassigned"
        : MessagingSeedData.Staff.FirstOrDefault(member => member.Id == ownerId.Value)?.DisplayName ?? "Unknown staff member";

    private static WorkflowConflictException Conflict(string message = "This request changed in another view. Reload it and review the latest owner, status, and draft before retrying.") => new(message);
    private static string FormatInstant(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseInstant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
