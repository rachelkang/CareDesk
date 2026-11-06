using CareDesk.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CareDesk.Desktop;

public sealed class ReplyComposerViewModel : ObservableObject
{
    private readonly MessagingWorkflowService workflow;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ReplyComposerViewModel> logger;
    private CancellationTokenSource? autosaveCancellation;
    private ConversationDetails? details;
    private ReplyDraft? draft;
    private string text = string.Empty;
    private string status = "No draft";
    private string? errorText;
    private bool isSaving;
    private bool isDirty;
    private bool suppressAutosave;

    public ReplyComposerViewModel(MessagingWorkflowService workflow, TimeProvider timeProvider, ILogger<ReplyComposerViewModel> logger)
    {
        this.workflow = workflow;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<OperationResult<ReplyDraft>> OpenSuggestedConfirmationAsync(ConversationDetails value, string suggestedBody, bool replaceEditedStaleDraft, CancellationToken cancellationToken = default)
    {
        if (!await LoadAsync(value, cancellationToken)) return OperationResult<ReplyDraft>.Failure(ErrorText ?? "The current draft could not be saved.");
        try
        {
            var result = await workflow.OpenSuggestedConfirmationDraftAsync(value, suggestedBody, replaceEditedStaleDraft, cancellationToken);
            if (result.Succeeded) ApplyDraft(value, result.Value!); else ErrorText = result.Error;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not open an AI confirmation draft for request {RequestId}", value.Request.Id);
            ErrorText = "The appointment is saved, but the AI reply draft could not be created. Communication is still pending.";
            return OperationResult<ReplyDraft>.Failure(ErrorText);
        }
    }

    public async Task<OperationResult<ReplyDraft>> OpenSuggestedManualAsync(ConversationDetails value, string suggestedBody, CancellationToken cancellationToken = default)
    {
        if (!await LoadAsync(value, cancellationToken)) return OperationResult<ReplyDraft>.Failure(ErrorText ?? "The current draft could not be saved.");
        try
        {
            var result = await workflow.OpenSuggestedManualDraftAsync(value, suggestedBody, cancellationToken);
            if (result.Succeeded) ApplyDraft(value, result.Value!); else ErrorText = result.Error;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not open a suggested reply draft for request {RequestId}", value.Request.Id);
            ErrorText = "The suggested reply could not be created. No message was recorded.";
            return OperationResult<ReplyDraft>.Failure(ErrorText);
        }
    }

    public event EventHandler? Changed;

    public ConversationDetails? Details => details;
    public ReplyDraft? Draft => draft;
    public string Text
    {
        get => text;
        set
        {
            if (!SetProperty(ref text, value)) return;
            if (!suppressAutosave && draft is not null)
            {
                IsDirty = true;
                QueueAutosave();
            }
            OnPropertyChanged(nameof(CanRecord));
        }
    }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string? ErrorText { get => errorText; private set => SetProperty(ref errorText, value); }
    public bool IsSaving { get => isSaving; private set => SetProperty(ref isSaving, value); }
    public bool IsDirty { get => isDirty; private set => SetProperty(ref isDirty, value); }
    public bool HasDraft => draft is not null;
    public bool IsEditable => details?.Request.OwnerId == workflow.Staff.Current.Id && details.Request.Status != SchedulingWorkStatus.Completed;
    public bool IsStale => draft?.IsStaleFor(details?.Appointment) == true;
    public bool CanRecord => HasDraft && IsEditable && !IsSaving && !IsStale && !string.IsNullOrWhiteSpace(Text);

    public async Task<bool> LoadAsync(ConversationDetails? value, CancellationToken cancellationToken = default)
    {
        if (details?.Request.Id == value?.Request.Id && draft?.Revision == value?.Draft?.Revision) return true;
        if (!await FlushAsync(cancellationToken)) return false;
        autosaveCancellation?.Cancel();
        details = value;
        draft = value?.Draft;
        SetText(draft?.Body ?? string.Empty);
        ErrorText = null;
        IsDirty = false;
        UpdateStatus();
        NotifyState();
        return true;
    }

    public async Task<OperationResult<ReplyDraft>> OpenManualAsync(ConversationDetails value, CancellationToken cancellationToken = default)
    {
        if (!await LoadAsync(value, cancellationToken)) return OperationResult<ReplyDraft>.Failure(ErrorText ?? "The current draft could not be saved.");
        try
        {
            var result = await workflow.OpenManualDraftAsync(value, cancellationToken);
            if (result.Succeeded) ApplyDraft(value, result.Value!); else ErrorText = result.Error;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not open a manual reply draft for request {RequestId}", value.Request.Id);
            ErrorText = "The reply draft could not be created. No message was recorded.";
            return OperationResult<ReplyDraft>.Failure(ErrorText);
        }
    }

    public async Task<OperationResult<ReplyDraft>> OpenConfirmationAsync(ConversationDetails value, bool replaceEditedStaleDraft, CancellationToken cancellationToken = default)
    {
        if (!await LoadAsync(value, cancellationToken)) return OperationResult<ReplyDraft>.Failure(ErrorText ?? "The current draft could not be saved.");
        try
        {
            var result = await workflow.OpenConfirmationDraftAsync(value, replaceEditedStaleDraft, cancellationToken);
            if (result.Succeeded) ApplyDraft(value, result.Value!); else ErrorText = result.Error;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not open a confirmation draft for request {RequestId}", value.Request.Id);
            ErrorText = "The appointment is saved, but the reply draft could not be created. Communication is still pending.";
            return OperationResult<ReplyDraft>.Failure(ErrorText);
        }
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        autosaveCancellation?.Cancel();
        autosaveCancellation = null;
        if (!IsDirty || draft is null || details is null) return true;
        return await SaveAsync(cancellationToken);
    }

    public async Task<OperationResult<RequestMutation>> RecordAsync(SchedulingWorkStatus finalStatus, CancellationToken cancellationToken = default)
    {
        if (!await FlushAsync(cancellationToken)) return OperationResult<RequestMutation>.Failure(ErrorText ?? "The draft could not be saved.");
        if (details is null) return OperationResult<RequestMutation>.Failure("Select a conversation before recording a reply.");
        OperationResult<RequestMutation> result;
        try
        {
            result = await workflow.RecordReplyAsync(details, finalStatus, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record reply for request {RequestId}", details.Request.Id);
            ErrorText = "The reply was not recorded. The saved draft and unfinished request were retained.";
            Status = "Reply not recorded";
            return OperationResult<RequestMutation>.Failure(ErrorText);
        }
        if (!result.Succeeded)
        {
            ErrorText = result.Error;
            return result;
        }
        var reloaded = await workflow.GetConversationAsync(details.Conversation.Id, cancellationToken);
        await LoadAsync(reloaded, cancellationToken);
        Status = "Reply recorded locally · not delivered";
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void QueueAutosave()
    {
        autosaveCancellation?.Cancel();
        var pending = new CancellationTokenSource();
        autosaveCancellation = pending;
        _ = AutosaveAsync(pending);
    }

    private async Task AutosaveAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(450), timeProvider, pending.Token);
            if (autosaveCancellation == pending) await SaveAsync(pending.Token);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        finally
        {
            if (autosaveCancellation == pending) autosaveCancellation = null;
            pending.Dispose();
        }
    }

    private async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (draft is null || details is null) return true;
        IsSaving = true;
        ErrorText = null;
        Status = "Saving draft…";
        try
        {
            var result = await workflow.SaveDraftAsync(details.Request, draft, Text, cancellationToken);
            if (!result.Succeeded)
            {
                ErrorText = result.Error;
                Status = "Draft not saved · review required";
                return false;
            }
            draft = result.Value!;
            details = details with { Draft = draft };
            IsDirty = false;
            Status = IsStale ? "Saved draft · appointment details changed" : "Draft saved";
            NotifyState();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Draft save canceled";
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save reply draft for request {RequestId}", details.Request.Id);
            ErrorText = "The draft could not be saved. Your text is still here; retry before navigating away.";
            Status = "Draft not saved";
            return false;
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(CanRecord));
        }
    }

    private void ApplyDraft(ConversationDetails value, ReplyDraft valueDraft)
    {
        details = value with { Draft = valueDraft };
        draft = valueDraft;
        SetText(valueDraft.Body);
        IsDirty = false;
        ErrorText = null;
        UpdateStatus();
        NotifyState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetText(string value)
    {
        suppressAutosave = true;
        Text = value;
        suppressAutosave = false;
    }

    private void UpdateStatus()
    {
        Status = draft is null
            ? "No unsent draft"
            : IsStale
                ? "Saved draft · appointment details changed · update required"
                : "Saved unsent draft";
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(CanRecord));
    }
}
