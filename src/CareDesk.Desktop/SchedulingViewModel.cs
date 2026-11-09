using System.Collections.ObjectModel;
using System.Globalization;
using CareDesk.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CareDesk.Desktop;

public sealed record AppointmentRow(Appointment Appointment, string Time, string Patient, string Clinician, string Visit, string Status, string Initials)
{
    public bool HasPatientRequest => !string.IsNullOrWhiteSpace(Appointment.PatientRequest);
    public override string ToString() => $"{Time} · {Patient} · {Clinician} · {Visit} · {Status}{(HasPatientRequest ? " · Patient request — needs attention" : string.Empty)}";
}

public partial class SchedulingViewModel : ObservableObject
{
    private readonly SchedulingService scheduling;
    private readonly IAppointmentStore store;
    private readonly IReadOnlyList<ISchedulingDraftService> draftServices;
    private readonly IReadOnlyList<IReplySuggestionService> replySuggestionServices;
    private readonly ClinicClock clock;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SchedulingViewModel> logger;
    private readonly MessagingWorkflowService? workflow;
    private readonly ReplyComposerViewModel? composer;
    private CancellationTokenSource? draftCancellation;
    private CancellationTokenSource? replySuggestionCancellation;
    private CancellationTokenSource? contextCancellation;
    private Appointment? selected;
    private ConversationDetails? linkedContext;
    private RequestId? preferredRequestId;
    private string proposedTime = "09:00 AM";
    private DateTime proposedDate = DateTime.Today;
    private Clinician? proposedClinician;
    private int proposedDuration = 30;
    private SchedulingDraft? suggestion;
    private string suggestedAction = string.Empty;
    private string suggestedReplyText = string.Empty;
    private string replySuggestionStatus = string.Empty;
    private bool isPreparingSuggestion;
    private bool isPreparingReplySuggestion;
    private string? errorText;
    private string? reviewNote;
    private string draftStatus = string.Empty;
    private string mode = "Sample mode";
    private bool isDirty;
    private bool isBusy;
    private bool isInitialized;
    private bool suppressDayRefresh;
    private readonly Dictionary<AppointmentId, string> appointmentActionOutcomes = [];
    private readonly Dictionary<AppointmentId, string> replyActionOutcomes = [];
    private bool suppressReplySuggestionRefresh;
    private bool isManualAppointmentEdit;
    private SchedulingDraft? conflictingSuggestion;

    public SchedulingViewModel(SchedulingService scheduling, IAppointmentStore store, IReadOnlyList<ISchedulingDraftService> draftServices, ClinicClock clock, TimeProvider timeProvider, ILogger<SchedulingViewModel> logger)
        : this(scheduling, store, draftServices, clock, timeProvider, logger, null, null, null)
    {
    }

    public SchedulingViewModel(SchedulingService scheduling, IAppointmentStore store, IReadOnlyList<ISchedulingDraftService> draftServices, ClinicClock clock, TimeProvider timeProvider, ILogger<SchedulingViewModel> logger, MessagingWorkflowService? workflow, ReplyComposerViewModel? composer, IReadOnlyList<IReplySuggestionService>? replySuggestionServices = null)
    {
        this.scheduling = scheduling; this.store = store; this.draftServices = draftServices; this.clock = clock; this.timeProvider = timeProvider; this.logger = logger;
        this.workflow = workflow; this.composer = composer;
        this.replySuggestionServices = replySuggestionServices ?? [];
        Clinicians = []; Appointments = [];
        if (composer is not null) composer.Changed += (_, _) =>
        {
            if (linkedContext is not null && composer.Details is { } details && details.Request.Id == linkedContext.Request.Id && details.Messages.Count(message => message.Direction == MessageDirection.Outgoing) > linkedContext.Messages.Count(message => message.Direction == MessageDirection.Outgoing))
            {
                linkedContext = details;
                SetActionOutcome(appointment: false, "RECORDED");
            }
            NotifyWorkflowContext();
        };
    }

    public ObservableCollection<Clinician> Clinicians { get; }
    public ObservableCollection<AppointmentRow> Appointments { get; }
    public string ClinicName => "Visual Studio Community Clinic";
    public string TimeZoneLabel => $"Clinic time · {clock.TimeZone.StandardName}";
    public string SyntheticLabel => "SYNTHETIC DATA";
    public string ModeStatus => Mode == "Sample mode" ? "Sample mode - no AI call" : "Live mode · configured endpoint required";
    public string CurrentDayLabel => SelectedDay.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
    public string CurrentTime => selected is null ? "—" : clock.ToLocal(selected.StartUtc).ToString("h:mm tt", CultureInfo.CurrentCulture);
    public string CurrentPatient => selected?.PatientName ?? "Select an appointment";
    public string CurrentPatientCode => selected?.PatientCode ?? "";
    public string CurrentClinician => selected?.ClinicianName ?? "—";
    public string CurrentVisit => selected?.VisitType ?? "—";
    public string CurrentInterval => selected is null ? "—" : $"{clock.ToLocal(selected.StartUtc):h:mm tt} – {clock.ToLocal(selected.EndUtc):h:mm tt}";
    public string ProposedInterval => DateTime.TryParseExact(ProposedTime, ["h:mm tt", "hh:mm tt", "H:mm"], CultureInfo.CurrentCulture, DateTimeStyles.None, out var time) ? $"{ProposedDate:MMM d} · {time:h:mm tt} – {time.AddMinutes(ProposedDuration):h:mm tt}" : "Enter a valid start time";
    public bool HasSelection => selected is not null;
    public string PatientRequest => linkedContext?.Messages.LastOrDefault(message => message.Direction == MessageDirection.Incoming)?.Body ?? selected?.PatientRequest ?? string.Empty;
    public bool HasPatientRequest => !string.IsNullOrWhiteSpace(PatientRequest);
    public bool HasRequestAssistance => HasPatientRequest || !IsAppointmentSuggestionActive || !IsReplySuggestionActive;
    public bool HasWorkflowRequest => linkedContext is not null;
    public ConversationDetails? LinkedContext => linkedContext;
    public ReplyComposerViewModel? Composer => composer;
    public string RequestStatus => linkedContext is null ? "No linked request" : FormatStatus(linkedContext.Request.Status);
    public string RequestOwner => linkedContext?.OwnerDisplayName ?? "—";
    public string OwnershipExplanation => linkedContext is null
        ? "This appointment is not linked to a scheduling conversation."
        : linkedContext.Request.OwnerId == workflow?.Staff.Current.Id
            ? "Assigned to you"
            : linkedContext.Request.OwnerId is null
                ? "Unassigned · claim this request before changing its outcome or reply"
                : $"Read-only · assigned to {linkedContext.OwnerDisplayName}";
    public string AssignmentActionLabel => linkedContext?.Request.OwnerId == workflow?.Staff.Current.Id ? "Unassign" : "Assign to me";
    public bool CanToggleAssignment => linkedContext is not null && linkedContext.Request.Status != SchedulingWorkStatus.Completed && (linkedContext.Request.OwnerId is null || linkedContext.Request.OwnerId == workflow?.Staff.Current.Id);
    public bool CanEditAppointment => selected is not null && (linkedContext is null || linkedContext.Request.OwnerId == workflow?.Staff.Current.Id);
    public bool IsManualAppointmentEdit { get => isManualAppointmentEdit; private set { if (SetProperty(ref isManualAppointmentEdit, value)) { OnPropertyChanged(nameof(AreManualAppointmentFieldsEnabled)); OnPropertyChanged(nameof(CanSaveChanges)); } } }
    public bool AreManualAppointmentFieldsEnabled => CanEditAppointment;
    public bool CanSaveChanges => IsDirty && CanEditAppointment && !IsBusy;
    public bool CanAcceptSuggestedUpdate => CanApplySuggestion && CanEditAppointment;
    public string AppointmentActionOutcome => selected is not null && appointmentActionOutcomes.TryGetValue(selected.Id, out var outcome) ? outcome : string.Empty;
    public string ReplyActionOutcome => selected is not null && replyActionOutcomes.TryGetValue(selected.Id, out var outcome) ? outcome : string.Empty;
    public bool IsAppointmentSuggestionActive => string.IsNullOrEmpty(AppointmentActionOutcome);
    public bool IsReplySuggestionActive => string.IsNullOrEmpty(ReplyActionOutcome);
    public bool HasAppointmentSuggestion => conflictingSuggestion is null && (!IsAppointmentSuggestionActive || !string.IsNullOrWhiteSpace(SuggestedAction));
    public bool ShowAppointmentSuggestionHelp => HasAppointmentSuggestion && IsAppointmentSuggestionActive;
    public bool HasReplySuggestion => !IsReplySuggestionActive || (linkedContext is not null && linkedContext.Request.Status != SchedulingWorkStatus.Completed && (IsPreparingReplySuggestion || !string.IsNullOrWhiteSpace(SuggestedReplyText) || !string.IsNullOrWhiteSpace(ReplySuggestionStatus)));
    public bool CanUseReplySuggestion => IsReplySuggestionActive && HasReplySuggestion && !IsPreparingReplySuggestion && !string.IsNullOrWhiteSpace(SuggestedReplyText) && linkedContext?.Request.OwnerId == workflow?.Staff.Current.Id && composer is not null;
    public string ReplySuggestionTitle => conflictingSuggestion is not null
        ? "Reply to patient about unavailable time"
        : linkedContext?.Request.Status == SchedulingWorkStatus.ReplyPending
        ? "Reply to patient confirming new time"
        : "Reply to patient suggesting a different time";
    public string SuggestedReplyText { get => suggestedReplyText; private set { if (SetProperty(ref suggestedReplyText, value)) { OnPropertyChanged(nameof(HasReplySuggestion)); OnPropertyChanged(nameof(CanUseReplySuggestion)); } } }
    public string ReplySuggestionStatus { get => replySuggestionStatus; private set { if (SetProperty(ref replySuggestionStatus, value)) OnPropertyChanged(nameof(HasReplySuggestion)); } }
    public bool CanApplySuggestion => conflictingSuggestion is null && IsAppointmentSuggestionActive && suggestion?.LocalStart is not null && string.IsNullOrWhiteSpace(suggestion.NeedsReview) && !IsPreparingSuggestion && !IsBusy;
    public string SuggestedAction { get => suggestedAction; private set { if (SetProperty(ref suggestedAction, value)) { OnPropertyChanged(nameof(HasAppointmentSuggestion)); OnPropertyChanged(nameof(ShowAppointmentSuggestionHelp)); } } }
    public bool IsPreparingSuggestion { get => isPreparingSuggestion; private set { if (SetProperty(ref isPreparingSuggestion, value)) { OnPropertyChanged(nameof(CanApplySuggestion)); OnPropertyChanged(nameof(CanAcceptSuggestedUpdate)); } } }
    public bool IsPreparingReplySuggestion { get => isPreparingReplySuggestion; private set { if (SetProperty(ref isPreparingReplySuggestion, value)) { OnPropertyChanged(nameof(HasReplySuggestion)); OnPropertyChanged(nameof(CanUseReplySuggestion)); } } }
    public string EmptyMessage => Appointments.Count == 0 ? "No appointments are scheduled for this day." : "";

    [ObservableProperty] private DateTime selectedDay = DateTime.Today;
    [ObservableProperty] private AppointmentRow? selectedRow;
    [ObservableProperty] private bool isLoading;

    public string ProposedTime { get => proposedTime; set { if (SetProperty(ref proposedTime, value)) OnProposalChanged(); } }
    public DateTime ProposedDate { get => proposedDate; set { if (SetProperty(ref proposedDate, value)) OnProposalChanged(); } }
    public Clinician? ProposedClinician { get => proposedClinician; set { if (SetProperty(ref proposedClinician, value)) OnProposalChanged(); } }
    public int ProposedDuration { get => proposedDuration; set { if (SetProperty(ref proposedDuration, value)) OnProposalChanged(); } }
    public string? ErrorText { get => errorText; private set => SetProperty(ref errorText, value); }
    public string? ReviewNote { get => reviewNote; private set => SetProperty(ref reviewNote, value); }
    public string DraftStatus { get => draftStatus; private set => SetProperty(ref draftStatus, value); }
    public string Mode { get => mode; set { if (SetProperty(ref mode, value)) { OnPropertyChanged(nameof(ModeStatus)); if (HasPatientRequest) { PrepareDraftCommand.Execute(null); PrepareReplySuggestionCommand.Execute(null); } } } }
    public bool IsDirty { get => isDirty; private set { if (SetProperty(ref isDirty, value)) OnPropertyChanged(nameof(CanSaveChanges)); } }
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) { OnPropertyChanged(nameof(CanApplySuggestion)); OnPropertyChanged(nameof(CanAcceptSuggestedUpdate)); OnPropertyChanged(nameof(CanSaveChanges)); } } }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        Clinicians.Clear(); foreach (var clinician in await scheduling.GetCliniciansAsync(cancellationToken)) Clinicians.Add(clinician);
        var all = await store.GetAppointmentsAsync(cancellationToken);
        if (all.Count == 0) { await scheduling.ResetAsync(cancellationToken); all = await store.GetAppointmentsAsync(cancellationToken); }
        var first = all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.PatientRequest)) ?? all.OrderBy(a => a.StartUtc).FirstOrDefault();
        if (first is not null) SelectedDay = clock.ToLocal(first.StartUtc).Date;
        isInitialized = true;
        await RefreshAsync(first?.Id, cancellationToken);
    }

    partial void OnSelectedDayChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CurrentDayLabel));
        if (isInitialized && !suppressDayRefresh) _ = RefreshAsync(selected?.Id, CancellationToken.None);
    }

    partial void OnSelectedRowChanged(AppointmentRow? value)
    {
        if (value is not null && selected == value.Appointment) return;
        ClearSuggestion();
        contextCancellation?.Cancel();
        contextCancellation = null;
        replySuggestionCancellation?.Cancel();
        replySuggestionCancellation = null;
        linkedContext = null;
        IsManualAppointmentEdit = false;
        SuggestedReplyText = string.Empty;
        ReplySuggestionStatus = string.Empty;
        selected = value?.Appointment;
        IsManualAppointmentEdit = AppointmentActionOutcome == "EDITING";
        ErrorText = null; ReviewNote = null; DraftStatus = string.Empty; IsDirty = false;
        if (selected is not null)
        {
            ProposedDate = clock.ToLocal(selected.StartUtc).Date;
            ProposedTime = clock.ToLocal(selected.StartUtc).ToString("h:mm tt", CultureInfo.CurrentCulture);
            ProposedDuration = (int)(selected.EndUtc - selected.StartUtc).TotalMinutes;
            ProposedClinician = Clinicians.FirstOrDefault(c => c.Id == selected.ClinicianId);
        }
        IsDirty = false;
        NotifyCurrent();
        NotifyWorkflowContext();
        if (selected is not null && workflow is not null) _ = LoadWorkflowContextAsync(selected.Id, preferredRequestId, CancellationToken.None);
        preferredRequestId = null;
        if (HasPatientRequest && IsAppointmentSuggestionActive) PrepareDraftCommand.Execute(null);
    }

    private void OnProposalChanged()
    {
        IsDirty = selected is not null && (ProposedDate.Date != clock.ToLocal(selected.StartUtc).Date || !string.Equals(ProposedTime.Trim(), clock.ToLocal(selected.StartUtc).ToString("h:mm tt", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase) || ProposedDuration != (int)(selected.EndUtc - selected.StartUtc).TotalMinutes || ProposedClinician?.Id != selected.ClinicianId);
        OnPropertyChanged(nameof(ProposedInterval));
        OnPropertyChanged(nameof(CanSaveChanges));
        ErrorText = null;
        ReviewNote = null;
        if (!suppressReplySuggestionRefresh && linkedContext is not null)
        {
            SetActionOutcome(appointment: false, string.Empty);
            QueueReplySuggestionRefresh();
        }
    }

    private void NotifyCurrent()
    {
        OnPropertyChanged(nameof(CurrentTime)); OnPropertyChanged(nameof(CurrentPatient)); OnPropertyChanged(nameof(CurrentPatientCode)); OnPropertyChanged(nameof(CurrentClinician)); OnPropertyChanged(nameof(CurrentVisit)); OnPropertyChanged(nameof(CurrentInterval)); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(PatientRequest)); OnPropertyChanged(nameof(HasPatientRequest));
    }

    private void NotifyWorkflowContext()
    {
        OnPropertyChanged(nameof(AppointmentActionOutcome));
        OnPropertyChanged(nameof(ReplyActionOutcome));
        OnPropertyChanged(nameof(IsAppointmentSuggestionActive));
        OnPropertyChanged(nameof(IsReplySuggestionActive));
        OnPropertyChanged(nameof(HasRequestAssistance));
        OnPropertyChanged(nameof(CanApplySuggestion));
        OnPropertyChanged(nameof(HasWorkflowRequest));
        OnPropertyChanged(nameof(LinkedContext));
        OnPropertyChanged(nameof(Composer));
        OnPropertyChanged(nameof(RequestStatus));
        OnPropertyChanged(nameof(RequestOwner));
        OnPropertyChanged(nameof(OwnershipExplanation));
        OnPropertyChanged(nameof(AssignmentActionLabel));
        OnPropertyChanged(nameof(CanToggleAssignment));
        OnPropertyChanged(nameof(CanEditAppointment));
        OnPropertyChanged(nameof(AreManualAppointmentFieldsEnabled));
        OnPropertyChanged(nameof(CanSaveChanges));
        OnPropertyChanged(nameof(CanAcceptSuggestedUpdate));
        OnPropertyChanged(nameof(HasAppointmentSuggestion));
        OnPropertyChanged(nameof(ShowAppointmentSuggestionHelp));
        OnPropertyChanged(nameof(HasReplySuggestion));
        OnPropertyChanged(nameof(CanUseReplySuggestion));
        OnPropertyChanged(nameof(ReplySuggestionTitle));
        OnPropertyChanged(nameof(SuggestedReplyText));
        OnPropertyChanged(nameof(ReplySuggestionStatus));
        OnPropertyChanged(nameof(PatientRequest));
        OnPropertyChanged(nameof(HasPatientRequest));
    }

    private async Task LoadWorkflowContextAsync(AppointmentId appointmentId, RequestId? requestId, CancellationToken cancellationToken)
    {
        if (workflow is null) return;
        contextCancellation?.Cancel();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        contextCancellation = pending;
        try
        {
            var loaded = await workflow.GetRequestForAppointmentAsync(appointmentId, requestId, pending.Token);
            if (pending.IsCancellationRequested || contextCancellation != pending || selected?.Id != appointmentId) return;
            linkedContext = loaded;
            if (loaded?.Request.OutcomeAppointmentRevision is not null)
                SetActionOutcome(appointment: true, "UPDATED");
            if (loaded?.Request.Status == SchedulingWorkStatus.Completed || (loaded?.Request.Status == SchedulingWorkStatus.WaitingOnPatient && loaded.Draft is null && loaded.Messages.Any(message => message.Direction == MessageDirection.Outgoing)))
                SetActionOutcome(appointment: false, "RECORDED");
            if (composer is not null && !await composer.LoadAsync(loaded, pending.Token))
            {
                ErrorText = composer.ErrorText ?? "The previous reply draft could not be saved, so messaging context was not changed.";
                linkedContext = null;
            }
            NotifyWorkflowContext();
            if (HasPatientRequest && IsAppointmentSuggestionActive && string.IsNullOrWhiteSpace(SuggestedAction)) PrepareDraftCommand.Execute(null);
            if (HasPatientRequest) PrepareReplySuggestionCommand.Execute(null);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load scheduling request for appointment {AppointmentId}", appointmentId);
            if (contextCancellation == pending) ErrorText = "The linked conversation could not be loaded. The appointment remains available for direct scheduling work.";
        }
        finally
        {
            if (contextCancellation == pending) contextCancellation = null;
        }
    }

    public async Task<bool> OpenAppointmentAsync(AppointmentId appointmentId, RequestId requestId, CancellationToken cancellationToken = default)
    {
        var all = await store.GetAppointmentsAsync(cancellationToken);
        var appointment = all.FirstOrDefault(value => value.Id == appointmentId);
        if (appointment is null) { ErrorText = "The linked appointment is no longer available."; return false; }
        preferredRequestId = requestId;
        var day = clock.ToLocal(appointment.StartUtc).Date;
        if (SelectedDay.Date != day)
        {
            suppressDayRefresh = true;
            SelectedDay = day;
            suppressDayRefresh = false;
        }
        await RefreshAsync(appointmentId, cancellationToken);
        if (selected?.Id != appointmentId) { ErrorText = "The linked appointment could not be selected."; return false; }
        await LoadWorkflowContextAsync(appointmentId, requestId, cancellationToken);
        return linkedContext?.Request.Id == requestId;
    }

    public async Task RefreshLinkedContextAsync(CancellationToken cancellationToken = default)
    {
        if (selected is null || linkedContext is null) return;
        await LoadWorkflowContextAsync(selected.Id, linkedContext.Request.Id, cancellationToken);
    }

    private async Task RefreshAsync(AppointmentId? preserve, CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            var rows = await scheduling.GetAppointmentsForDayAsync(DateOnly.FromDateTime(SelectedDay), cancellationToken);
            SelectedRow = null;
            Appointments.Clear();
            foreach (var a in rows)
            {
                var local = clock.ToLocal(a.StartUtc);
                Appointments.Add(new(a, local.ToString("h:mm tt", CultureInfo.CurrentCulture), a.PatientName, a.ClinicianName, a.VisitType, a.Status, string.Concat(a.PatientName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n[0]))));
            }
            OnPropertyChanged(nameof(EmptyMessage));
            selected = null;
            SelectedRow = preserve is { } id ? Appointments.FirstOrDefault(r => r.Appointment.Id == id) : null;
            if (SelectedRow is null && Appointments.Count > 0) SelectedRow = Appointments[0];
            if (SelectedRow is null) { selected = null; NotifyCurrent(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not load agenda for {ClinicDate}", SelectedDay.Date);
            ErrorText = "The agenda could not be loaded. Check local storage and try again.";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RefreshAgendaAsync(CancellationToken cancellationToken)
    {
        if (IsDirty) { ErrorText = "Save your changes or select an appointment again before refreshing the agenda."; return; }
        await RefreshAsync(selected?.Id, cancellationToken);
    }

    [RelayCommand]
    private void PreviousDay() => SelectedDay = SelectedDay.AddDays(-1);
    [RelayCommand]
    private void NextDay() => SelectedDay = SelectedDay.AddDays(1);
    private void ClearSuggestion()
    {
        draftCancellation?.Cancel();
        draftCancellation = null;
        suggestion = null;
        conflictingSuggestion = null;
        SuggestedAction = string.Empty;
        IsPreparingSuggestion = false;
        OnPropertyChanged(nameof(HasAppointmentSuggestion));
        OnPropertyChanged(nameof(ShowAppointmentSuggestionHelp));
        OnPropertyChanged(nameof(HasReplySuggestion));
        OnPropertyChanged(nameof(ReplySuggestionTitle));
        OnPropertyChanged(nameof(CanUseReplySuggestion));
        OnPropertyChanged(nameof(CanApplySuggestion));
        OnPropertyChanged(nameof(CanAcceptSuggestedUpdate));
    }

    private async Task<bool> HasBookingConflictAsync(Appointment appointment, SchedulingDraft draft, CancellationToken cancellationToken)
    {
        var start = clock.ToUtc(draft.LocalStart!.Value);
        var end = start.AddMinutes(draft.DurationMinutes!.Value);
        var appointments = await store.GetAppointmentsAsync(cancellationToken);
        return appointments.Any(other => other.Id != appointment.Id && other.Status == "Scheduled" && other.ClinicianId == draft.ClinicianId && other.StartUtc < end && start < other.EndUtc);
    }

    private void RemoveConflictingRescheduleSuggestion(SchedulingDraft draft)
    {
        conflictingSuggestion = draft;
        suggestion = null;
        SuggestedAction = string.Empty;
        replySuggestionCancellation?.Cancel();
        replySuggestionCancellation = null;
        IsPreparingReplySuggestion = false;
        SuggestedReplyText = string.Empty;
        ReplySuggestionStatus = string.Empty;
        ErrorText = "The requested time conflicts with another booking. The current appointment has not been changed.";
        NotifyWorkflowContext();
        PrepareReplySuggestionCommand.Execute(null);
    }

    private void QueueReplySuggestionRefresh()
    {
        _ = PrepareReplySuggestionCoreAsync(debounce: true, CancellationToken.None);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task PrepareReplySuggestionAsync(CancellationToken cancellationToken) => PrepareReplySuggestionCoreAsync(debounce: false, cancellationToken);

    private async Task PrepareReplySuggestionCoreAsync(bool debounce, CancellationToken cancellationToken)
    {
        if (!IsReplySuggestionActive) return;
        replySuggestionCancellation?.Cancel();
        var appointment = selected;
        var context = linkedContext;
        if (appointment is null || context is null || string.IsNullOrWhiteSpace(PatientRequest) || replySuggestionServices.Count < 2)
        {
            replySuggestionCancellation = null;
            SuggestedReplyText = string.Empty;
            ReplySuggestionStatus = string.Empty;
            IsPreparingReplySuggestion = false;
            return;
        }
        if (conflictingSuggestion is { LocalStart: { } unavailableStart })
        {
            replySuggestionCancellation = null;
            IsPreparingReplySuggestion = false;
            var currentStart = clock.ToLocal(appointment.StartUtc);
            SuggestedReplyText = $"The requested time on {unavailableStart:dddd, MMMM d, yyyy} at {unavailableStart:h:mm tt} is unavailable. Your current appointment on {currentStart:dddd, MMMM d, yyyy} at {currentStart:h:mm tt} with {appointment.ClinicianName} remains unchanged.";
            ReplySuggestionStatus = "Scheduling availability · no AI call · current appointment unchanged";
            return;
        }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replySuggestionCancellation = pending;
        IsPreparingReplySuggestion = true;
        SuggestedReplyText = string.Empty;
        ReplySuggestionStatus = Mode == "Sample mode" ? "Preparing sample reply suggestion…" : "Preparing AI reply suggestion…";
        try
        {
            if (debounce) await Task.Delay(TimeSpan.FromMilliseconds(350), timeProvider, pending.Token);
            if (!TryCreateReplySuggestionRequest(appointment, context, out var request, out var error))
            {
                ReplySuggestionStatus = error;
                return;
            }
            var service = replySuggestionServices[Mode == "Live mode" ? 1 : 0];
            var result = await service.PrepareAsync(request!, pending.Token);
            if (pending.IsCancellationRequested || replySuggestionCancellation != pending || selected?.Id != appointment.Id || linkedContext?.Request.Id != context.Request.Id) return;
            if (!result.Succeeded)
            {
                SuggestedReplyText = string.Empty;
                ReplySuggestionStatus = result.Error ?? "No reply suggestion was returned. Write the reply manually.";
                return;
            }
            SuggestedReplyText = result.Text!;
            ReplySuggestionStatus = context.Request.Status == SchedulingWorkStatus.ReplyPending
                ? $"{result.Source} · based on the saved appointment"
                : $"{result.Source} · based on the patient message and proposed schedule";
            OnPropertyChanged(nameof(HasReplySuggestion));
            OnPropertyChanged(nameof(CanUseReplySuggestion));
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reply suggestion failed for request {RequestId}", context.Request.Id);
            if (replySuggestionCancellation == pending)
            {
                SuggestedReplyText = string.Empty;
                ReplySuggestionStatus = "The reply suggestion could not be prepared. Write the reply manually or retry after reviewing the schedule.";
            }
        }
        finally
        {
            if (replySuggestionCancellation == pending)
            {
                replySuggestionCancellation = null;
                IsPreparingReplySuggestion = false;
            }
        }
    }

    private bool TryCreateReplySuggestionRequest(Appointment appointment, ConversationDetails context, out ReplySuggestionRequest? request, out string error)
    {
        var committed = context.Request.Status == SchedulingWorkStatus.ReplyPending;
        DateTime localStart;
        string clinicianName;
        int duration;
        if (committed && context.Appointment is { } saved)
        {
            localStart = clock.ToLocal(saved.StartUtc);
            clinicianName = saved.ClinicianName;
            duration = (int)(saved.EndUtc - saved.StartUtc).TotalMinutes;
        }
        else if (!IsDirty && suggestion is { LocalStart: { } suggestedStart })
        {
            localStart = suggestedStart;
            clinicianName = suggestion.ClinicianId is { } clinicianId
                ? Clinicians.FirstOrDefault(value => value.Id == clinicianId)?.DisplayName ?? appointment.ClinicianName
                : appointment.ClinicianName;
            duration = suggestion.DurationMinutes ?? (int)(appointment.EndUtc - appointment.StartUtc).TotalMinutes;
        }
        else
        {
            if (!DateTime.TryParseExact(ProposedTime.Trim(), ["h:mm tt", "hh:mm tt", "H:mm"], CultureInfo.CurrentCulture, DateTimeStyles.None, out var time) || ProposedClinician is null)
            {
                request = null;
                error = "Enter a valid proposed date, time, and clinician to update the reply suggestion.";
                return false;
            }
            localStart = DateTime.SpecifyKind(ProposedDate.Date.Add(time.TimeOfDay), DateTimeKind.Unspecified);
            clinicianName = ProposedClinician.DisplayName;
            duration = ProposedDuration;
        }
        request = new(appointment, PatientRequest, localStart, clinicianName, duration, committed, clock.TimeZone.Id);
        error = string.Empty;
        return true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PrepareDraftAsync(CancellationToken cancellationToken)
    {
        if (AppointmentActionOutcome is "UPDATED" or "REJECTED") return;
        SetActionOutcome(appointment: true, string.Empty);
        ClearSuggestion();
        var appointment = selected;
        var patientRequest = PatientRequest;
        if (appointment is null || string.IsNullOrWhiteSpace(patientRequest)) { ErrorText = "Select an appointment with a patient request."; return; }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        draftCancellation = pending;
        IsPreparingSuggestion = true;
        SuggestedAction = Mode == "Sample mode" ? "Preparing sample recommendation…" : "Preparing AI recommendation…";
        DraftStatus = ModeStatus;
        try
        {
            var service = draftServices[Mode == "Live mode" ? 1 : 0];
            var result = await service.PrepareAsync(new(appointment, patientRequest, DateOnly.FromDateTime(clock.ToLocal(appointment.StartUtc)), clock.TimeZone.Id, Clinicians.ToArray()), pending.Token);
            if (pending.IsCancellationRequested || draftCancellation != pending || selected?.Id != appointment.Id) return;
            if (!result.Succeeded) { SuggestedAction = result.Error ?? "No recommendation was returned. Retry or edit manually."; DraftStatus = $"{service.ModeLabel} · recommendation unavailable"; return; }
            var draft = result.Draft!;
            if (draft.LocalStart is not { } start || !string.IsNullOrWhiteSpace(draft.NeedsReview))
            {
                SuggestedAction = draft.NeedsReview ?? "The request needs clarification. Review the message and edit manually.";
                DraftStatus = $"{draft.Source} · staff review needed";
                return;
            }
            var clinician = draft.ClinicianId is { } id ? Clinicians.FirstOrDefault(c => c.Id == id) : Clinicians.FirstOrDefault(c => c.Id == appointment.ClinicianId);
            var duration = draft.DurationMinutes ?? (int)(appointment.EndUtc - appointment.StartUtc).TotalMinutes;
            if (clinician is null || duration is < 5 or > 240)
            {
                SuggestedAction = "The recommendation contains an invalid clinician or duration. Retry or edit manually.";
                DraftStatus = $"{draft.Source} · staff review needed";
                return;
            }
            draft = draft with { ClinicianId = clinician.Id, DurationMinutes = duration };
            var conflicts = await HasBookingConflictAsync(appointment, draft, pending.Token);
            if (pending.IsCancellationRequested || draftCancellation != pending || selected?.Id != appointment.Id) return;
            if (conflicts)
            {
                RemoveConflictingRescheduleSuggestion(draft);
                DraftStatus = $"{draft.Source} · requested time unavailable";
                return;
            }
            suggestion = draft;
            SuggestedAction = $"Reschedule to {start:dddd, MMMM d, yyyy} at {start:h:mm tt} with {clinician.DisplayName} ({duration} minutes).";
            DraftStatus = $"{draft.Source} · not saved";
            PrepareReplySuggestionCommand.Execute(null);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recommendation failed for appointment {AppointmentId}", appointment.Id);
            if (draftCancellation == pending) { SuggestedAction = "A recommendation could not be prepared. Retry or edit the booking manually."; DraftStatus = "Recommendation unavailable"; }
        }
        finally
        {
            if (draftCancellation == pending) { draftCancellation = null; IsPreparingSuggestion = false; OnPropertyChanged(nameof(CanApplySuggestion)); }
        }
    }

    [RelayCommand]
    private void ApplySuggestion()
    {
        if (!CanApplySuggestion || suggestion is not { LocalStart: { } start } draft) { ErrorText = "Wait for a complete recommendation before applying it."; return; }
        suppressReplySuggestionRefresh = true;
        try
        {
            ProposedDate = start.Date;
            ProposedTime = start.ToString("h:mm tt", CultureInfo.CurrentCulture);
            if (draft.ClinicianId is { } id) ProposedClinician = Clinicians.First(c => c.Id == id);
            if (draft.DurationMinutes is { } duration) ProposedDuration = duration;
        }
        finally { suppressReplySuggestionRefresh = false; }
        DraftStatus = $"{draft.Source} · applied to fields, not saved";
        PrepareReplySuggestionCommand.Execute(null);
    }

    [RelayCommand]
    private async Task AcceptSuggestedUpdateAsync(CancellationToken cancellationToken)
    {
        if (!CanAcceptSuggestedUpdate) { ErrorText = "Wait for a complete appointment suggestion before updating."; return; }
        var appointment = selected!;
        var draft = suggestion!;
        try
        {
            var conflicts = await HasBookingConflictAsync(appointment, draft, cancellationToken);
            if (selected?.Id != appointment.Id || suggestion != draft || !CanAcceptSuggestedUpdate) return;
            if (conflicts) { RemoveConflictingRescheduleSuggestion(draft); return; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not check requested time for appointment {AppointmentId}", appointment.Id);
            if (selected?.Id == appointment.Id) ErrorText = "The requested time could not be checked. The current appointment has not been changed. Refresh the agenda and retry.";
            return;
        }
        IsManualAppointmentEdit = false;
        ApplySuggestion();
        await ConfirmRescheduleAsync(cancellationToken);
    }

    [RelayCommand]
    private void BeginManualUpdate()
    {
        if (!CanEditAppointment) { ErrorText = OwnershipExplanation; return; }
        IsManualAppointmentEdit = true;
        SetActionOutcome(appointment: true, "EDITING");
        ErrorText = null;
    }

    [RelayCommand]
    private void RejectAppointmentSuggestion()
    {
        ClearSuggestion();
        SetActionOutcome(appointment: true, "REJECTED");
        DraftStatus = "Appointment suggestion dismissed";
        OnPropertyChanged(nameof(HasAppointmentSuggestion));
        SetActionOutcome(appointment: false, string.Empty);
        PrepareReplySuggestionCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ConfirmRescheduleAsync(CancellationToken cancellationToken)
    {
        if (selected is null) { ErrorText = "Select an appointment before rescheduling."; return; }
        if (!string.IsNullOrWhiteSpace(ReviewNote)) { ErrorText = "Review the draft and update the proposed fields before confirming."; return; }
        if (!Clinicians.Any(c => c.Id == ProposedClinician?.Id)) { ErrorText = "Choose a clinician from the list."; return; }
        if (!DateTime.TryParseExact(ProposedTime.Trim(), ["h:mm tt", "hh:mm tt", "H:mm"], CultureInfo.CurrentCulture, DateTimeStyles.None, out var time)) { ErrorText = "Enter a start time such as 11:00 AM."; return; }
        var local = DateTime.SpecifyKind(ProposedDate.Date.Add(time.TimeOfDay), DateTimeKind.Unspecified);
        IsBusy = true; ErrorText = null;
        try
        {
            Appointment saved;
            RequestId? committedRequestId = null;
            if (linkedContext is not null && workflow is not null)
            {
                committedRequestId = linkedContext.Request.Id;
                var workflowResult = await workflow.RescheduleAsync(linkedContext.Request, new(selected.Id, ProposedClinician!.Id, local, ProposedDuration), cancellationToken);
                if (!workflowResult.Succeeded) { ErrorText = workflowResult.Error; return; }
                saved = workflowResult.Value!.Appointment!;
            }
            else
            {
                var result = await scheduling.RescheduleAsync(new(selected.Id, ProposedClinician!.Id, local, ProposedDuration), cancellationToken);
                if (!result.Succeeded) { ErrorText = result.Error; return; }
                saved = result.Value!;
            }
            SetActionOutcome(appointment: true, "UPDATED");
            SetActionOutcome(appointment: false, string.Empty);
            var savedDay = clock.ToLocal(saved.StartUtc).Date;
            if (SelectedDay.Date != savedDay) { suppressDayRefresh = true; SelectedDay = savedDay; suppressDayRefresh = false; }
            await RefreshAsync(saved.Id, cancellationToken);
            if (committedRequestId is { } requestId) await LoadWorkflowContextAsync(saved.Id, requestId, cancellationToken);
            IsManualAppointmentEdit = false;
            IsDirty = false; ReviewNote = null; DraftStatus = committedRequestId is null ? "Appointment updated" : "Appointment updated · reply pending";
            NotifyWorkflowContext();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not save appointment {AppointmentId}", selected.Id);
            ErrorText = "The reschedule could not be saved. Your proposed values are still available to retry.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AssignRequestToMeAsync(CancellationToken cancellationToken)
    {
        if (linkedContext is null || workflow is null) return;
        var result = await workflow.AssignToMeAsync(linkedContext.Request, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        await LoadWorkflowContextAsync(linkedContext.Appointment!.Id, linkedContext.Request.Id, cancellationToken);
    }

    [RelayCommand]
    private async Task ReleaseRequestAsync(CancellationToken cancellationToken)
    {
        if (linkedContext is null || workflow is null) return;
        var appointmentId = linkedContext.Request.AppointmentId;
        var requestId = linkedContext.Request.Id;
        var result = await workflow.ReleaseAsync(linkedContext.Request, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        if (appointmentId is { } id) await LoadWorkflowContextAsync(id, requestId, cancellationToken);
    }

    [RelayCommand]
    private async Task ToggleRequestAssignmentAsync(CancellationToken cancellationToken)
    {
        if (linkedContext is null || workflow is null) return;
        if (linkedContext.Request.OwnerId == workflow.Staff.Current.Id) await ReleaseRequestAsync(cancellationToken);
        else await AssignRequestToMeAsync(cancellationToken);
    }

    private async Task<OperationResult<ReplyDraft>> OpenSuggestedReplyDraftAsync(CancellationToken cancellationToken)
    {
        if (linkedContext is null || composer is null) return OperationResult<ReplyDraft>.Failure("This appointment is not linked to a scheduling conversation.");
        if (ReplyActionOutcome is "REJECTED" or "RECORDED") return OperationResult<ReplyDraft>.Failure("This reply suggestion has already been acted on.");
        if (ReplyActionOutcome == "DRAFT" && composer.Draft is { } existingDraft) return OperationResult<ReplyDraft>.Success(existingDraft);
        if (string.IsNullOrWhiteSpace(SuggestedReplyText)) return OperationResult<ReplyDraft>.Failure("Generate a reply suggestion before continuing.");
        var result = linkedContext.Request.Status == SchedulingWorkStatus.ReplyPending
            ? await composer.OpenSuggestedConfirmationAsync(linkedContext, SuggestedReplyText, replaceEditedStaleDraft: false, cancellationToken)
            : await composer.OpenSuggestedManualAsync(linkedContext, SuggestedReplyText, cancellationToken);
        if (!result.Succeeded) return result;
        linkedContext = linkedContext with { Draft = result.Value };
        ErrorText = null;
        NotifyWorkflowContext();
        return result;
    }

    [RelayCommand]
    private async Task EditSuggestedReplyAsync(CancellationToken cancellationToken)
    {
        var result = await OpenSuggestedReplyDraftAsync(cancellationToken);
        if (!result.Succeeded) ErrorText = result.Error;
        else SetActionOutcome(appointment: false, "DRAFT");
    }

    [RelayCommand]
    private async Task RecordSuggestedReplyAsync(CancellationToken cancellationToken)
    {
        var draft = await OpenSuggestedReplyDraftAsync(cancellationToken);
        if (!draft.Succeeded || composer is null || linkedContext is null) { ErrorText = draft.Error; return; }
        await RecordReplyDraftAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task RecordReplyDraftAsync(CancellationToken cancellationToken)
    {
        if (composer is null || linkedContext is null) { ErrorText = "Open a linked conversation before recording a reply draft."; return; }
        var finalStatus = linkedContext.Request.Status == SchedulingWorkStatus.ReplyPending
            ? SchedulingWorkStatus.Completed
            : SchedulingWorkStatus.WaitingOnPatient;
        var result = await composer.RecordAsync(finalStatus, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        SetActionOutcome(appointment: false, "RECORDED");
        linkedContext = await workflow!.GetConversationAsync(linkedContext.Conversation.Id, cancellationToken);
        await composer.LoadAsync(linkedContext, cancellationToken);
        NotifyWorkflowContext();
    }

    [RelayCommand]
    private void RejectReplySuggestion()
    {
        replySuggestionCancellation?.Cancel();
        replySuggestionCancellation = null;
        IsPreparingReplySuggestion = false;
        SetActionOutcome(appointment: false, "REJECTED");
        OnPropertyChanged(nameof(HasReplySuggestion));
        OnPropertyChanged(nameof(CanUseReplySuggestion));
    }

    [RelayCommand]
    private async Task UpdateStaleDraftAsync(CancellationToken cancellationToken)
    {
        if (linkedContext is null || composer is null) return;
        if (string.IsNullOrWhiteSpace(SuggestedReplyText)) await PrepareReplySuggestionCoreAsync(debounce: false, cancellationToken);
        var result = await composer.OpenSuggestedConfirmationAsync(linkedContext, SuggestedReplyText, replaceEditedStaleDraft: true, cancellationToken);
        if (!result.Succeeded) { ErrorText = result.Error; return; }
        linkedContext = linkedContext with { Draft = result.Value };
        ErrorText = null;
        SetActionOutcome(appointment: false, "DRAFT");
        NotifyWorkflowContext();
    }

    private void SetActionOutcome(bool appointment, string outcome)
    {
        if (selected is null) return;
        var outcomes = appointment ? appointmentActionOutcomes : replyActionOutcomes;
        if (string.IsNullOrEmpty(outcome)) outcomes.Remove(selected.Id);
        else outcomes[selected.Id] = outcome;
        if (!appointment && !string.IsNullOrEmpty(outcome))
        {
            replySuggestionCancellation?.Cancel();
            replySuggestionCancellation = null;
            IsPreparingReplySuggestion = false;
        }
        NotifyWorkflowContext();
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true; ErrorText = null;
        try { ClearSuggestion(); await scheduling.ResetAsync(cancellationToken); appointmentActionOutcomes.Clear(); replyActionOutcomes.Clear(); Mode = "Sample mode"; DraftStatus = string.Empty; ReviewNote = null; IsManualAppointmentEdit = false; IsDirty = false; var all = await store.GetAppointmentsAsync(cancellationToken); var first = all.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.PatientRequest)) ?? all.OrderBy(a => a.StartUtc).First(); var firstDay = clock.ToLocal(first.StartUtc).Date; if (SelectedDay.Date != firstDay) { suppressDayRefresh = true; SelectedDay = firstDay; suppressDayRefresh = false; } await RefreshAsync(first.Id, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Could not restore synthetic appointments"); ErrorText = "Sample data could not be restored. Your saved appointments are unchanged."; }
        finally { IsBusy = false; }
    }

    private static string FormatStatus(SchedulingWorkStatus status) => status switch
    {
        SchedulingWorkStatus.InProgress => "In progress",
        SchedulingWorkStatus.WaitingOnPatient => "Waiting on patient",
        SchedulingWorkStatus.ReplyPending => "Reply pending",
        _ => status.ToString()
    };
}
