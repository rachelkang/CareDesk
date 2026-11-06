using CareDesk.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CareDesk.Desktop;

public partial class ShellViewModel : ObservableObject
{
    private string activeSection = "Messages";
    private string? navigationError;

    public ShellViewModel(MessagesViewModel messages, SchedulingViewModel schedule, ReplyComposerViewModel composer)
    {
        Messages = messages;
        Schedule = schedule;
        Composer = composer;
    }

    public MessagesViewModel Messages { get; }
    public SchedulingViewModel Schedule { get; }
    public ReplyComposerViewModel Composer { get; }
    public string ActiveSection { get => activeSection; private set => SetProperty(ref activeSection, value); }
    public string? NavigationError { get => navigationError; private set => SetProperty(ref navigationError, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Schedule.InitializeAsync(cancellationToken);
        await Messages.InitializeAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task ShowMessagesAsync(CancellationToken cancellationToken)
    {
        if (!await Composer.FlushAsync(cancellationToken)) { NavigationError = Composer.ErrorText; return; }
        await Messages.ReloadSelectedAsync(cancellationToken);
        ActiveSection = "Messages";
        NavigationError = null;
    }

    [RelayCommand]
    private async Task ShowScheduleAsync(CancellationToken cancellationToken)
    {
        if (!await Composer.FlushAsync(cancellationToken)) { NavigationError = Composer.ErrorText; return; }
        await Schedule.RefreshLinkedContextAsync(cancellationToken);
        ActiveSection = "Schedule";
        NavigationError = null;
    }

    [RelayCommand]
    private async Task OpenLinkedAppointmentAsync(CancellationToken cancellationToken)
    {
        var details = Messages.Details;
        if (details?.Appointment is null) { NavigationError = "This conversation has no linked appointment."; return; }
        if (!await Composer.FlushAsync(cancellationToken)) { NavigationError = Composer.ErrorText; return; }
        if (!await Schedule.OpenAppointmentAsync(details.Appointment.Id, details.Request.Id, cancellationToken))
        {
            NavigationError = Schedule.ErrorText ?? "The linked appointment could not be opened.";
            return;
        }
        ActiveSection = "Schedule";
        NavigationError = null;
    }

    [RelayCommand]
    private async Task OpenConversationAsync(CancellationToken cancellationToken)
    {
        var details = Schedule.LinkedContext;
        if (details is null) { NavigationError = "This appointment is not linked to a conversation."; return; }
        if (!await Composer.FlushAsync(cancellationToken)) { NavigationError = Composer.ErrorText; return; }
        if (!await Messages.SelectConversationAsync(details.Conversation.Id, cancellationToken))
        {
            NavigationError = "The linked conversation could not be opened.";
            return;
        }
        ActiveSection = "Messages";
        NavigationError = null;
    }

    public Task<bool> FlushAsync(CancellationToken cancellationToken = default) => Composer.FlushAsync(cancellationToken);
}
