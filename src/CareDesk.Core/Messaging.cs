using System.Globalization;

namespace CareDesk.Core;

public readonly record struct PatientId(Guid Value);
public readonly record struct ConversationId(Guid Value);
public readonly record struct RequestId(Guid Value);
public readonly record struct MessageId(Guid Value);
public readonly record struct DraftId(Guid Value);
public readonly record struct StaffId(Guid Value);

public enum SchedulingWorkStatus
{
    Open,
    InProgress,
    WaitingOnPatient,
    ReplyPending,
    Completed
}

public enum MessageDirection
{
    Incoming,
    Outgoing
}

public enum ReplyDraftKind
{
    Manual,
    AppointmentConfirmation
}

public sealed record StaffMember(StaffId Id, string DisplayName);

public sealed record Conversation(
    ConversationId Id,
    PatientId PatientId,
    string PatientName,
    string PatientCode,
    DateTimeOffset LastActivityUtc);

public sealed record SchedulingRequest(
    RequestId Id,
    ConversationId ConversationId,
    AppointmentId? AppointmentId,
    StaffId? OwnerId,
    SchedulingWorkStatus Status,
    int? OutcomeAppointmentRevision,
    int Version);

public sealed record CommunicationMessage(
    MessageId Id,
    ConversationId ConversationId,
    MessageDirection Direction,
    string Body,
    DateTimeOffset CreatedUtc,
    string AuthorDisplayName,
    string RecordStatus,
    DraftId? SourceDraftId = null);

public sealed record ReplyDraft(
    DraftId Id,
    RequestId RequestId,
    string Body,
    ReplyDraftKind Kind,
    int Revision,
    StaffId EditedBy,
    DateTimeOffset UpdatedUtc,
    int? AppointmentRevision,
    DateTimeOffset? AppointmentStartUtc,
    string? ClinicianName,
    bool HasStaffEdits)
{
    public bool IsStaleFor(Appointment? appointment) =>
        Kind == ReplyDraftKind.AppointmentConfirmation &&
        (appointment is null || AppointmentRevision != appointment.Revision);
}

public sealed record ConversationSummary(
    Conversation Conversation,
    SchedulingRequest Request,
    ReplyDraft? Draft,
    string Preview,
    bool IsUnread,
    string OwnerDisplayName);

public sealed record ConversationDetails(
    Conversation Conversation,
    SchedulingRequest Request,
    ReplyDraft? Draft,
    IReadOnlyList<CommunicationMessage> Messages,
    Appointment? Appointment,
    string OwnerDisplayName);

public sealed record RequestMutation(SchedulingRequest Request, Appointment? Appointment, ReplyDraft? Draft);

public sealed class WorkflowConflictException(string message) : InvalidOperationException(message);

public interface IWorkflowStore
{
    Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(StaffId currentStaffId, CancellationToken cancellationToken);
    Task<ConversationDetails?> GetConversationAsync(ConversationId conversationId, StaffId currentStaffId, CancellationToken cancellationToken);
    Task<ConversationDetails?> GetRequestForAppointmentAsync(AppointmentId appointmentId, RequestId? preferredRequestId, StaffId currentStaffId, CancellationToken cancellationToken);
    Task MarkConversationReadAsync(ConversationId conversationId, StaffId staffId, DateTimeOffset readUtc, CancellationToken cancellationToken);
    Task<SchedulingRequest> UpdateOwnerAsync(RequestId requestId, StaffId? ownerId, SchedulingWorkStatus status, int expectedVersion, CancellationToken cancellationToken);
    Task<ReplyDraft> SaveDraftAsync(ReplyDraft draft, int expectedRequestVersion, int? expectedDraftRevision, CancellationToken cancellationToken);
    Task<RequestMutation> CommitRescheduleAsync(RequestId requestId, int expectedRequestVersion, Appointment appointment, CancellationToken cancellationToken);
    Task<RequestMutation> RecordReplyAsync(RequestId requestId, int expectedRequestVersion, ReplyDraft draft, CommunicationMessage message, SchedulingWorkStatus finalStatus, CancellationToken cancellationToken);
}

public sealed class StaffContext
{
    public StaffContext(StaffMember current, IReadOnlyList<StaffMember> roster)
    {
        Current = current;
        Roster = roster;
    }

    public StaffMember Current { get; }
    public IReadOnlyList<StaffMember> Roster { get; }
    public string DisplayName(StaffId? id) => id is null ? "Unassigned" : Roster.FirstOrDefault(member => member.Id == id.Value)?.DisplayName ?? "Unknown staff member";
}

public sealed class MessagingWorkflowService(
    IWorkflowStore store,
    SchedulingService scheduling,
    ClinicClock clock,
    TimeProvider timeProvider,
    StaffContext staff)
{
    public StaffContext Staff => staff;

    public Task<IReadOnlyList<ConversationSummary>> GetInboxAsync(CancellationToken cancellationToken = default) =>
        store.GetConversationSummariesAsync(staff.Current.Id, cancellationToken);

    public Task<ConversationDetails?> GetConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default) =>
        store.GetConversationAsync(conversationId, staff.Current.Id, cancellationToken);

    public Task<ConversationDetails?> GetRequestForAppointmentAsync(AppointmentId appointmentId, RequestId? preferredRequestId = null, CancellationToken cancellationToken = default) =>
        store.GetRequestForAppointmentAsync(appointmentId, preferredRequestId, staff.Current.Id, cancellationToken);

    public Task MarkReadAsync(ConversationId conversationId, CancellationToken cancellationToken = default) =>
        store.MarkConversationReadAsync(conversationId, staff.Current.Id, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<OperationResult<SchedulingRequest>> AssignToMeAsync(SchedulingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Status == SchedulingWorkStatus.Completed) return OperationResult<SchedulingRequest>.Failure("Completed work cannot be reassigned.");
        if (request.OwnerId == staff.Current.Id) return OperationResult<SchedulingRequest>.Success(request);
        if (request.OwnerId is not null) return OperationResult<SchedulingRequest>.Failure($"This request is assigned to {staff.DisplayName(request.OwnerId)}. Reassign it explicitly before making changes.");
        return await UpdateOwnerAsync(request, staff.Current.Id, SchedulingWorkStatus.InProgress, cancellationToken);
    }

    public async Task<OperationResult<SchedulingRequest>> ReleaseAsync(SchedulingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OwnerId != staff.Current.Id) return OperationResult<SchedulingRequest>.Failure("Only the current owner can release this request.");
        return await UpdateOwnerAsync(request, null, SchedulingWorkStatus.Open, cancellationToken);
    }

    public async Task<OperationResult<SchedulingRequest>> ReassignAsync(SchedulingRequest request, StaffId ownerId, CancellationToken cancellationToken = default)
    {
        if (!staff.Roster.Any(member => member.Id == ownerId)) return OperationResult<SchedulingRequest>.Failure("Choose a staff member from the configured roster.");
        if (request.Status == SchedulingWorkStatus.Completed) return OperationResult<SchedulingRequest>.Failure("Completed work cannot be reassigned.");
        return await UpdateOwnerAsync(request, ownerId, SchedulingWorkStatus.InProgress, cancellationToken);
    }

    public async Task<OperationResult<RequestMutation>> RescheduleAsync(SchedulingRequest request, RescheduleRequest change, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(request);
        if (ownership is not null) return OperationResult<RequestMutation>.Failure(ownership);
        if (request.AppointmentId != change.AppointmentId) return OperationResult<RequestMutation>.Failure("The selected request is not linked to this appointment.");
        var prepared = await scheduling.PrepareRescheduleAsync(change, cancellationToken);
        if (!prepared.Succeeded) return OperationResult<RequestMutation>.Failure(prepared.Error!);
        try
        {
            var committed = await store.CommitRescheduleAsync(request.Id, request.Version, prepared.Value!, cancellationToken);
            return OperationResult<RequestMutation>.Success(committed);
        }
        catch (WorkflowConflictException ex)
        {
            return OperationResult<RequestMutation>.Failure(ex.Message);
        }
    }

    public async Task<OperationResult<ReplyDraft>> OpenManualDraftAsync(ConversationDetails details, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(details.Request);
        if (ownership is not null) return OperationResult<ReplyDraft>.Failure(ownership);
        if (details.Draft is not null) return OperationResult<ReplyDraft>.Success(details.Draft);
        var draft = NewDraft(details.Request.Id, string.Empty, ReplyDraftKind.Manual, null);
        return await SaveNewDraftAsync(details, draft, cancellationToken);
    }

    public async Task<OperationResult<ReplyDraft>> OpenSuggestedManualDraftAsync(ConversationDetails details, string suggestedBody, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(details.Request);
        if (ownership is not null) return OperationResult<ReplyDraft>.Failure(ownership);
        if (details.Draft is not null) return OperationResult<ReplyDraft>.Success(details.Draft);
        if (string.IsNullOrWhiteSpace(suggestedBody)) return OperationResult<ReplyDraft>.Failure("The reply suggestion is empty. Write a reply manually instead.");
        var draft = NewDraft(details.Request.Id, suggestedBody.Trim(), ReplyDraftKind.Manual, null);
        return await SaveNewDraftAsync(details, draft, cancellationToken);
    }

    public async Task<OperationResult<ReplyDraft>> OpenConfirmationDraftAsync(ConversationDetails details, bool replaceEditedStaleDraft = false, CancellationToken cancellationToken = default)
    {
        if (details.Appointment is null) return OperationResult<ReplyDraft>.Failure("The linked appointment is unavailable. Refresh before drafting a confirmation.");
        return await OpenSuggestedConfirmationDraftAsync(details, FormatConfirmation(details.Appointment), replaceEditedStaleDraft, cancellationToken);
    }

    public async Task<OperationResult<ReplyDraft>> OpenSuggestedConfirmationDraftAsync(ConversationDetails details, string suggestedBody, bool replaceEditedStaleDraft = false, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(details.Request);
        if (ownership is not null) return OperationResult<ReplyDraft>.Failure(ownership);
        if (details.Request.Status != SchedulingWorkStatus.ReplyPending || details.Request.OutcomeAppointmentRevision is null)
            return OperationResult<ReplyDraft>.Failure("Confirm the scheduling change first. A confirmation draft is available after the appointment update is saved.");
        if (details.Appointment is null || details.Request.AppointmentId != details.Appointment.Id)
            return OperationResult<ReplyDraft>.Failure("The linked appointment is unavailable. Refresh before drafting a confirmation.");
        if (details.Request.OutcomeAppointmentRevision != details.Appointment.Revision)
            return OperationResult<ReplyDraft>.Failure("The request outcome does not match the saved appointment. Refresh and review the scheduling change.");
        if (string.IsNullOrWhiteSpace(suggestedBody)) return OperationResult<ReplyDraft>.Failure("The reply suggestion is empty. Generate it again or write the reply manually.");

        var existing = details.Draft;
        if (existing is not null && !existing.IsStaleFor(details.Appointment)) return OperationResult<ReplyDraft>.Success(existing);
        if (existing is not null && existing.HasStaffEdits && !replaceEditedStaleDraft)
            return OperationResult<ReplyDraft>.Failure("The saved draft contains staff edits and describes an older appointment. Review it, then choose Update draft to replace those edits with current appointment details.");

        var replacement = existing is null
            ? NewDraft(details.Request.Id, suggestedBody.Trim(), ReplyDraftKind.AppointmentConfirmation, details.Appointment)
            : existing with
            {
                Body = suggestedBody.Trim(),
                Kind = ReplyDraftKind.AppointmentConfirmation,
                AppointmentRevision = details.Appointment.Revision,
                AppointmentStartUtc = details.Appointment.StartUtc,
                ClinicianName = details.Appointment.ClinicianName,
                EditedBy = staff.Current.Id,
                UpdatedUtc = timeProvider.GetUtcNow(),
                HasStaffEdits = false
            };
        try
        {
            var saved = await store.SaveDraftAsync(replacement, details.Request.Version, existing?.Revision, cancellationToken);
            return OperationResult<ReplyDraft>.Success(saved);
        }
        catch (WorkflowConflictException ex)
        {
            return OperationResult<ReplyDraft>.Failure(ex.Message);
        }
    }

    public async Task<OperationResult<ReplyDraft>> SaveDraftAsync(SchedulingRequest request, ReplyDraft draft, string body, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(request);
        if (ownership is not null) return OperationResult<ReplyDraft>.Failure(ownership);
        var updated = draft with { Body = body, EditedBy = staff.Current.Id, UpdatedUtc = timeProvider.GetUtcNow(), HasStaffEdits = true };
        try
        {
            return OperationResult<ReplyDraft>.Success(await store.SaveDraftAsync(updated, request.Version, draft.Revision, cancellationToken));
        }
        catch (WorkflowConflictException ex)
        {
            return OperationResult<ReplyDraft>.Failure(ex.Message);
        }
    }

    public async Task<OperationResult<RequestMutation>> RecordReplyAsync(ConversationDetails details, SchedulingWorkStatus finalStatus, CancellationToken cancellationToken = default)
    {
        var ownership = EnsureOwned(details.Request);
        if (ownership is not null) return OperationResult<RequestMutation>.Failure(ownership);
        if (details.Draft is null || string.IsNullOrWhiteSpace(details.Draft.Body)) return OperationResult<RequestMutation>.Failure("Write and save a reply before recording it.");
        if (details.Draft.IsStaleFor(details.Appointment)) return OperationResult<RequestMutation>.Failure("This confirmation draft describes an older appointment. Update it before recording the reply.");
        if (finalStatus is not (SchedulingWorkStatus.WaitingOnPatient or SchedulingWorkStatus.Completed))
            return OperationResult<RequestMutation>.Failure("Choose whether this reply is waiting on the patient or completes the work.");
        var message = new CommunicationMessage(
            new(details.Draft.Id.Value),
            details.Conversation.Id,
            MessageDirection.Outgoing,
            details.Draft.Body.Trim(),
            timeProvider.GetUtcNow(),
            staff.Current.DisplayName,
            "Recorded locally · not delivered",
            details.Draft.Id);
        try
        {
            return OperationResult<RequestMutation>.Success(await store.RecordReplyAsync(details.Request.Id, details.Request.Version, details.Draft, message, finalStatus, cancellationToken));
        }
        catch (WorkflowConflictException ex)
        {
            return OperationResult<RequestMutation>.Failure(ex.Message);
        }
    }

    private async Task<OperationResult<SchedulingRequest>> UpdateOwnerAsync(SchedulingRequest request, StaffId? ownerId, SchedulingWorkStatus status, CancellationToken cancellationToken)
    {
        try
        {
            return OperationResult<SchedulingRequest>.Success(await store.UpdateOwnerAsync(request.Id, ownerId, status, request.Version, cancellationToken));
        }
        catch (WorkflowConflictException ex)
        {
            return OperationResult<SchedulingRequest>.Failure(ex.Message);
        }
    }

    private async Task<OperationResult<ReplyDraft>> SaveNewDraftAsync(ConversationDetails details, ReplyDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            return OperationResult<ReplyDraft>.Success(await store.SaveDraftAsync(draft, details.Request.Version, null, cancellationToken));
        }
        catch (WorkflowConflictException ex)
        {
            var current = await store.GetConversationAsync(details.Conversation.Id, staff.Current.Id, cancellationToken);
            if (current?.Draft is not null) return OperationResult<ReplyDraft>.Success(current.Draft);
            return OperationResult<ReplyDraft>.Failure(ex.Message);
        }
    }

    private string? EnsureOwned(SchedulingRequest request) => request.OwnerId == staff.Current.Id
        ? null
        : request.OwnerId is null
            ? "Assign this request to yourself before changing its outcome or reply."
            : $"This request is assigned to {staff.DisplayName(request.OwnerId)}. Reassign it explicitly before making changes.";

    private ReplyDraft NewDraft(RequestId requestId, string body, ReplyDraftKind kind, Appointment? appointment) =>
        new(new(Guid.NewGuid()), requestId, body, kind, 0, staff.Current.Id, timeProvider.GetUtcNow(), appointment?.Revision, appointment?.StartUtc, appointment?.ClinicianName, false);

    public string FormatConfirmation(Appointment appointment)
    {
        var local = clock.ToLocal(appointment.StartUtc);
        return string.Format(
            CultureInfo.CurrentCulture,
            "Your appointment has been moved to {0:dddd, MMMM d, yyyy}, at {0:h:mm tt} with {1}. Please let us know if that no longer works for you.",
            local,
            appointment.ClinicianName);
    }
}
