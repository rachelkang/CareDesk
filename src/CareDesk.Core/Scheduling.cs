using System.Globalization;

namespace CareDesk.Core;

public readonly record struct ClinicianId(Guid Value);
public readonly record struct AppointmentId(Guid Value);

public sealed record Clinician(ClinicianId Id, string DisplayName);
public sealed record Appointment(AppointmentId Id, string PatientName, string PatientCode, ClinicianId ClinicianId, string ClinicianName, string VisitType, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Status, string? PatientRequest = null)
{
    public PatientId PatientId { get; init; }
    public int Revision { get; init; }
}
public sealed record RescheduleRequest(AppointmentId AppointmentId, ClinicianId ClinicianId, DateTime LocalStart, int DurationMinutes);
public sealed record DraftRequest(Appointment Appointment, string Message, DateOnly ReferenceDate, string TimeZoneId, IReadOnlyList<Clinician> Clinicians);
public sealed record SchedulingDraft(DateTime? LocalStart, ClinicianId? ClinicianId, int? DurationMinutes, string Source, string? NeedsReview);
public sealed record ReplySuggestionRequest(Appointment Appointment, string Message, DateTime ProposedLocalStart, string ClinicianName, int DurationMinutes, bool IsCommitted, string TimeZoneId);
public sealed record ReplySuggestionResult(string? Text, string Source, string? Error)
{
    public bool Succeeded => Text is not null && Error is null;
    public static ReplySuggestionResult Success(string text, string source) => new(text, source, null);
    public static ReplySuggestionResult Failure(string error, string source) => new(null, source, error);
}
public sealed record DraftResult(SchedulingDraft? Draft, string? Error)
{
    public bool Succeeded => Draft is not null;
    public static DraftResult Success(SchedulingDraft draft) => new(draft, null);
    public static DraftResult Failure(string error) => new(null, error);
}
public sealed record OperationResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
    public static OperationResult<T> Success(T value) => new(value, null);
    public static OperationResult<T> Failure(string error) => new(default, error);
}

public interface IAppointmentStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Clinician>> GetCliniciansAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(CancellationToken cancellationToken);
    Task SaveAsync(Appointment appointment, CancellationToken cancellationToken);
    Task ResetAsync(IReadOnlyList<Clinician> clinicians, IReadOnlyList<Appointment> appointments, CancellationToken cancellationToken);
}

public interface ISchedulingDraftService
{
    string ModeLabel { get; }
    Task<DraftResult> PrepareAsync(DraftRequest request, CancellationToken cancellationToken);
}

public interface IReplySuggestionService
{
    string ModeLabel { get; }
    Task<ReplySuggestionResult> PrepareAsync(ReplySuggestionRequest request, CancellationToken cancellationToken);
}

public sealed class ClinicClock
{
    public ClinicClock(TimeZoneInfo timeZone) => TimeZone = timeZone;
    public TimeZoneInfo TimeZone { get; }
    public DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime;
    public DateTimeOffset ToUtc(DateTime local)
    {
        var wallTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(wallTime)) throw new ArgumentException("That local time does not exist in the clinic time zone. Choose another time.", nameof(local));
        if (TimeZone.IsAmbiguousTime(wallTime)) throw new ArgumentException("That local time occurs twice in the clinic time zone. Choose an unambiguous time.", nameof(local));
        return TimeZoneInfo.ConvertTimeToUtc(wallTime, TimeZone);
    }
}

public sealed class SchedulingService(IAppointmentStore store, ClinicClock clock, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<Clinician>> GetCliniciansAsync(CancellationToken cancellationToken = default) => store.GetCliniciansAsync(cancellationToken);

    public async Task<IReadOnlyList<Appointment>> GetAppointmentsForDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var appointments = await store.GetAppointmentsAsync(cancellationToken);
        return appointments.Where(a => DateOnly.FromDateTime(clock.ToLocal(a.StartUtc)) == day).OrderBy(a => a.StartUtc).ThenBy(a => a.PatientName, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<OperationResult<Appointment>> RescheduleAsync(RescheduleRequest request, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareRescheduleAsync(request, cancellationToken);
        if (!prepared.Succeeded) return prepared;
        await store.SaveAsync(prepared.Value!, cancellationToken);
        return prepared;
    }

    public async Task<OperationResult<Appointment>> PrepareRescheduleAsync(RescheduleRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AppointmentId.Value == Guid.Empty) return OperationResult<Appointment>.Failure("Select an appointment before rescheduling.");
        var all = await store.GetAppointmentsAsync(cancellationToken);
        var current = all.FirstOrDefault(a => a.Id == request.AppointmentId);
        if (current is null) return OperationResult<Appointment>.Failure("This appointment is no longer available. Refresh the agenda and select it again.");
        if (current.Status is not "Scheduled") return OperationResult<Appointment>.Failure("Only scheduled appointments can be rescheduled.");
        var clinicians = await store.GetCliniciansAsync(cancellationToken);
        var clinician = clinicians.FirstOrDefault(c => c.Id == request.ClinicianId);
        if (clinician is null) return OperationResult<Appointment>.Failure("Choose a clinician from the list.");
        if (string.IsNullOrWhiteSpace(current.PatientName) || string.IsNullOrWhiteSpace(current.VisitType)) return OperationResult<Appointment>.Failure("Appointment details are incomplete. Refresh the agenda before continuing.");
        if (request.DurationMinutes is < 5 or > 240) return OperationResult<Appointment>.Failure("Duration must be between 5 and 240 minutes.");
        if (request.LocalStart == default) return OperationResult<Appointment>.Failure("Choose a date and start time.");
        DateTimeOffset start;
        try { start = clock.ToUtc(request.LocalStart); }
        catch (ArgumentException ex) { return OperationResult<Appointment>.Failure(ex.Message); }
        if (start <= timeProvider.GetUtcNow()) return OperationResult<Appointment>.Failure("Choose a future date and time.");
        DateTimeOffset end;
        try { end = start.AddMinutes(request.DurationMinutes); }
        catch (ArgumentOutOfRangeException) { return OperationResult<Appointment>.Failure("The proposed interval is not valid."); }
        var updated = current with { ClinicianId = clinician.Id, ClinicianName = clinician.DisplayName, StartUtc = start, EndUtc = end, PatientRequest = null, Revision = current.Revision + 1 };
        return OperationResult<Appointment>.Success(updated);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var clinicians = SeedData.Clinicians;
        var clinicToday = DateOnly.FromDateTime(clock.ToLocal(timeProvider.GetUtcNow()));
        var appointments = SeedData.ForDay(SeedData.DefaultScheduleDay(clinicToday), clock);
        await store.ResetAsync(clinicians, appointments, cancellationToken);
    }
}

public static class SeedData
{
    public const string ReschedulingMessage = "Hi! Could you move my appointment to 10:00 AM on the same day? I'd like to keep the same doctor. Thank you!";

    public static DateOnly DefaultScheduleDay(DateOnly clinicDate)
    {
        var day = clinicDate.AddDays(1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        return day.AddDays(7);
    }

    public static IReadOnlyList<Clinician> Clinicians { get; } =
    [
        new(new(Guid.Parse("1e5ab4f0-d911-4e31-beb0-d4e19d665b01")), "Dr. Dalia Abo Sheasha"),
        new(new(Guid.Parse("ccad4891-8249-47ea-910e-3c449af50302")), "Dr. Andy Sterland"),
        new(new(Guid.Parse("e6a438dd-b2d0-47d1-82fb-88e8bcdf1403")), "Dr. Jeff Kelly")
    ];

    public static IReadOnlyList<Appointment> ForDay(DateOnly day, ClinicClock clock)
    {
        Appointment Make(string id, string patientId, string patient, string code, int clinician, string visit, int hour, int minute, int duration) =>
            new(new(Guid.Parse(id)), patient, code, Clinicians[clinician].Id, Clinicians[clinician].DisplayName, visit,
                clock.ToUtc(day.ToDateTime(new TimeOnly(hour, minute))), clock.ToUtc(day.ToDateTime(new TimeOnly(hour, minute)).AddMinutes(duration)), "Scheduled")
            { PatientId = new(Guid.Parse(patientId)) };
        return
        [
            Make("5c54fa11-3fce-4d43-b79a-5988c620e901", "bc34e389-6522-4a2d-bde5-70719846b41f", "Harshada Hole", "CD-1048", 1, "Follow-up", 13, 30, 30),
            Make("70a383cf-d9a0-4bfc-a4e0-f3c4841b5902", "6d8d8a57-361d-46bc-90ed-639253975ccb", "Anson Horton", "CD-1052", 0, "Consultation", 10, 0, 30),
            Make("c91ea090-d0f7-461c-b8eb-53c626a2b703", "aa3db2dc-781c-44db-ac16-6d3380cc63a5", "Mads Kristensen", "CD-1056", 0, "Follow-up", 9, 0, 30) with { PatientRequest = ReschedulingMessage },
            Make("991138dd-69f2-4c28-a7a8-808fb3a7d604", "775dcd83-8cb6-41ec-ac5d-e1d94f61b74a", "Jason Chlus", "CD-1061", 2, "Consultation", 15, 0, 45)
        ];
    }
}
