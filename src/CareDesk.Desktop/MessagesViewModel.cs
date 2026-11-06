using System.Collections.ObjectModel;
using System.Globalization;
using CareDesk.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CareDesk.Desktop;

public sealed record ConversationRow(ConversationSummary Summary, string Activity, string Status, string Owner)
{
    public string Patient => Summary.Conversation.PatientName;
    public string Preview => Summary.Preview;
    public bool IsUnread => Summary.IsUnread;
}

public sealed record MessageRow(CommunicationMessage Message, string Timestamp)
{
    public bool IsIncoming => Message.Direction == MessageDirection.Incoming;
    public string Author => Message.AuthorDisplayName;
    public string Body => Message.Body;
    public string RecordStatus => Message.RecordStatus;
}

public partial class MessagesViewModel : ObservableObject
{
    private readonly MessagingWorkflowService workflow;
    private readonly ReplyComposerViewModel composer;
    private readonly ClinicClock clock;
    private readonly ILogger<MessagesViewModel> logger;
    private CancellationTokenSource? selectionCancellation;
    private ConversationRow? selectedConversation;
    private ConversationDetails? details;
    private string? errorText;
    private bool isLoading;
    private bool suppressSelection;
    private StaffMember? selectedAssignee;

    public MessagesViewModel(MessagingWorkflowService workflow, ReplyComposerViewModel composer, ClinicClock clock, ILogger<MessagesViewModel> logger)
    {
        this.workflow = workflow;
        this.composer = composer;
        this.clock = clock;
        this.logger = logger;
        Conversations = [];
        Messages = [];
        Staff = new ObservableCollection<StaffMember>(workflow.Staff.Roster);
        composer.Changed += (_, _) => _ = ReloadSelectedAsync();
    }

    public ObservableCollection<ConversationRow> Conversations { get; }
    public ObservableCollection<MessageRow> Messages { get; }
    public ObservableCollection<StaffMember> Staff { get; }
    public ReplyComposerViewModel Composer => composer;
    public ConversationDetails? Details => details;
    public bool HasSelection => details is not null;
    public string PatientName => details?.Conversation.PatientName ?? "Select a conversation";
    public string PatientCode => details?.Conversation.PatientCode ?? string.Empty;
    public string RequestStatus => details is null ? "—" : FormatStatus(details.Request.Status);
    public string Owner => details?.OwnerDisplayName ?? "—";
    public string AppointmentContext => details?.Appointment is null
        ? "No linked appointment"
        : $"{clock.ToLocal(details.Appointment.StartUtc):ddd, MMM d · h:mm tt} · {details.Appointment.ClinicianName}";
    public string LocalDeliveryNote => "Replies are recorded in this local prototype and are not delivered to patients.";
    public string AssignmentActionLabel => details?.Request.OwnerId == workflow.Staff.Current.Id ? "Unassign" : "Assign to me";
    public bool CanToggleAssignment => details is not null && details.Request.Status != SchedulingWorkStatus.Completed && (details.Request.OwnerId is null || details.Request.OwnerId == workflow.Staff.Current.Id);
    public bool CanReassign => details is not null && details.Request.Status != SchedulingWorkStatus.Completed;
    public bool CanOpenAppointment => details?.Appointment is not null;
    public string OwnershipExplanation => details is null
        ? string.Empty
        : details.Request.OwnerId == workflow.Staff.Current.Id
            ? "Assigned to you"
            : details.Request.OwnerId is null
                ? "Unassigned · claim this request to edit its scheduling outcome or reply"
                : $"Read-only · assigned to {details.OwnerDisplayName}";

    public ConversationRow? SelectedConversation
    {
        get => selectedConversation;
        set
        {
            if (suppressSelection || ReferenceEquals(selectedConversation, value)) return;
            var previous = selectedConversation;
            selectedConversation = value;
            OnPropertyChanged();
            _ = SelectAsync(value, previous);
        }
    }

    public StaffMember? SelectedAssignee
    {
        get => selectedAssignee;
        set => SetProperty(ref selectedAssignee, value);
    }

    public string? ErrorText { get => errorText; private set => SetProperty(ref errorText, value); }
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshInboxAsync(null, cancellationToken);
        var first = Conversations.FirstOrDefault(row => row.Summary.Request.Status != SchedulingWorkStatus.Completed) ?? Conversations.FirstOrDefault();
        if (first is not null) await SelectConversationAsync(first.Summary.Conversation.Id, cancellationToken);
    }

    public async Task<bool> SelectConversationAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var row = Conversations.FirstOrDefault(item => item.Summary.Conversation.Id == conversationId);
        if (row is null)
        {
            await RefreshInboxAsync(conversationId, cancellationToken);
            row = Conversations.FirstOrDefault(item => item.Summary.Conversation.Id == conversationId);
        }
        if (row is null) return false;
        var previous = selectedConversation;
        suppressSelection = true;
        selectedConversation = row;
        OnPropertyChanged(nameof(SelectedConversation));
        suppressSelection = false;
        await SelectAsync(row, previous);
        return details?.Conversation.Id == conversationId;
    }

    public async Task RefreshInboxAsync(ConversationId? preserve, CancellationToken cancellationToken = default)
    {
        var summaries = await workflow.GetInboxAsync(cancellationToken);
        suppressSelection = true;
        try
        {
            Conversations.Clear();
            foreach (var summary in summaries)
            {
                Conversations.Add(new(summary, FormatActivity(summary.Conversation.LastActivityUtc), FormatStatus(summary.Request.Status), summary.OwnerDisplayName));
            }
            selectedConversation = preserve is { } id ? Conversations.FirstOrDefault(row => row.Summary.Conversation.Id == id) : null;
            OnPropertyChanged(nameof(SelectedConversation));
        }
        finally
        {
            suppressSelection = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken) => await ReloadSelectedAsync(cancellationToken);

    [RelayCommand]
    private async Task AssignToMeAsync(CancellationToken cancellationToken)
    {
        if (details is null) return;
        var result = await workflow.AssignToMeAsync(details.Request, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        await ReloadSelectedAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ToggleAssignmentAsync(CancellationToken cancellationToken)
    {
        if (details is null) return;
        if (details.Request.OwnerId == workflow.Staff.Current.Id) await ReleaseAsync(cancellationToken);
        else await AssignToMeAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (details is null) return;
        var result = await workflow.ReleaseAsync(details.Request, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        await ReloadSelectedAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ReassignAsync(CancellationToken cancellationToken)
    {
        if (details is null || SelectedAssignee is null) { ErrorText = "Choose a staff member before reassigning."; return; }
        var result = await workflow.ReassignAsync(details.Request, SelectedAssignee.Id, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        await ReloadSelectedAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task StartReplyAsync(CancellationToken cancellationToken)
    {
        if (details is null) return;
        var result = await composer.OpenManualAsync(details, cancellationToken);
        if (!result.Succeeded) ErrorText = result.Error;
        else await ReloadSelectedAsync(cancellationToken, reloadComposer: false);
    }

    [RelayCommand]
    private async Task RecordWaitingOnPatientAsync(CancellationToken cancellationToken)
    {
        var result = await composer.RecordAsync(SchedulingWorkStatus.WaitingOnPatient, cancellationToken);
        if (!result.Succeeded) ErrorText = result.Error;
        else await ReloadSelectedAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task RecordAndCompleteAsync(CancellationToken cancellationToken)
    {
        var result = await composer.RecordAsync(SchedulingWorkStatus.Completed, cancellationToken);
        if (!result.Succeeded) ErrorText = result.Error;
        else await ReloadSelectedAsync(cancellationToken);
    }

    public async Task ReloadSelectedAsync(CancellationToken cancellationToken = default, bool reloadComposer = true)
    {
        if (selectedConversation is null) return;
        var conversationId = selectedConversation.Summary.Conversation.Id;
        var loaded = await workflow.GetConversationAsync(conversationId, cancellationToken);
        if (loaded is null) { ErrorText = "This conversation is no longer available."; return; }
        ApplyDetails(loaded);
        if (reloadComposer) await composer.LoadAsync(loaded, cancellationToken);
        await RefreshInboxAsync(conversationId, cancellationToken);
    }

    private async Task SelectAsync(ConversationRow? value, ConversationRow? previous)
    {
        selectionCancellation?.Cancel();
        if (value is null)
        {
            details = null;
            Messages.Clear();
            NotifyDetails();
            return;
        }
        using var pending = new CancellationTokenSource();
        selectionCancellation = pending;
        IsLoading = true;
        ErrorText = null;
        try
        {
            var loaded = await workflow.GetConversationAsync(value.Summary.Conversation.Id, pending.Token);
            if (pending.IsCancellationRequested || selectionCancellation != pending) return;
            if (loaded is null) throw new InvalidOperationException("The selected conversation no longer exists.");
            if (!await composer.LoadAsync(loaded, pending.Token))
            {
                RevertSelection(previous);
                ErrorText = composer.ErrorText ?? "The current draft could not be saved, so the conversation was not changed.";
                return;
            }
            ApplyDetails(loaded);
            await workflow.MarkReadAsync(loaded.Conversation.Id, pending.Token);
            await RefreshInboxAsync(loaded.Conversation.Id, pending.Token);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load conversation {ConversationId}", value.Summary.Conversation.Id);
            RevertSelection(previous);
            ErrorText = "The conversation could not be loaded. The previous selection was kept.";
        }
        finally
        {
            if (selectionCancellation == pending) { selectionCancellation = null; IsLoading = false; }
        }
    }

    private void ApplyDetails(ConversationDetails value)
    {
        details = value;
        Messages.Clear();
        foreach (var message in value.Messages)
        {
            var local = clock.ToLocal(message.CreatedUtc);
            Messages.Add(new(message, local.ToString("ddd, MMM d · h:mm tt", CultureInfo.CurrentCulture)));
        }
        SelectedAssignee = value.Request.OwnerId is { } ownerId ? Staff.FirstOrDefault(member => member.Id == ownerId) : null;
        ErrorText = null;
        NotifyDetails();
    }

    private void RevertSelection(ConversationRow? previous)
    {
        suppressSelection = true;
        selectedConversation = previous;
        OnPropertyChanged(nameof(SelectedConversation));
        suppressSelection = false;
    }

    private void NotifyDetails()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(PatientName));
        OnPropertyChanged(nameof(PatientCode));
        OnPropertyChanged(nameof(RequestStatus));
        OnPropertyChanged(nameof(Owner));
        OnPropertyChanged(nameof(AppointmentContext));
        OnPropertyChanged(nameof(AssignmentActionLabel));
        OnPropertyChanged(nameof(CanToggleAssignment));
        OnPropertyChanged(nameof(CanReassign));
        OnPropertyChanged(nameof(CanOpenAppointment));
        OnPropertyChanged(nameof(OwnershipExplanation));
    }

    private string FormatActivity(DateTimeOffset value)
    {
        var local = clock.ToLocal(value);
        return local.Date == clock.ToLocal(DateTimeOffset.Now).Date ? local.ToString("h:mm tt", CultureInfo.CurrentCulture) : local.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    private static string FormatStatus(SchedulingWorkStatus status) => status switch
    {
        SchedulingWorkStatus.InProgress => "In progress",
        SchedulingWorkStatus.WaitingOnPatient => "Waiting on patient",
        SchedulingWorkStatus.ReplyPending => "Reply pending",
        _ => status.ToString()
    };
}
