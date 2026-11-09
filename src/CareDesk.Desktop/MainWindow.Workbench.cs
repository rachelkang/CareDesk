using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CareDesk.Core;
using CareDesk.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CareDesk.Desktop;

public partial class MainWindow
{
    private IHost? host;
    private ShellViewModel? shellViewModel;
    private bool closeApproved;
    private bool workbenchReady;
    private static readonly Brush Ink = Brush("#241A32");
    private static readonly Brush Muted = Brush("#5D536A");
    private static readonly Brush Line = Brush("#E6DFEC");
    private static readonly Brush Purple = Brush("#6334A0");
    private static readonly Brush DeepPurple = Brush("#3D205E");
    private static readonly Brush Canvas = Brush("#F7F4FA");
    private static readonly Brush PaleLavender = Brush("#F1E8F8");
    private static readonly Brush Error = Brush("#A32929");

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (workbenchReady) return;
        workbenchReady = true;
        try
        {
            Title = "CareDesk | Visual Studio Community Clinic";
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13; Background = Canvas; Foreground = Ink;
            Width = 1240; Height = 760; MinWidth = 1100; MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddUserSecrets<App>();
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            builder.Services.AddSingleton(new ClinicClock(zone));
            builder.Services.AddSingleton(TimeProvider.System);
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CareDesk", "caredesk.db");
            builder.Services.AddSingleton<SqliteAppointmentStore>(sp => new SqliteAppointmentStore(dbPath, sp.GetRequiredService<ClinicClock>(), sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<IAppointmentStore>(sp => sp.GetRequiredService<SqliteAppointmentStore>());
            builder.Services.AddSingleton<IWorkflowStore>(sp => sp.GetRequiredService<SqliteAppointmentStore>());
            builder.Services.AddSingleton<SchedulingService>();
            builder.Services.AddSingleton(new StaffContext(MessagingSeedData.CurrentStaff, MessagingSeedData.Staff));
            builder.Services.AddSingleton<MessagingWorkflowService>();
            builder.Services.AddSingleton<ISchedulingDraftService, SampleDraftService>();
            builder.Services.AddSingleton<ISchedulingDraftService, OpenAiDraftService>();
            builder.Services.AddSingleton<IReplySuggestionService, SampleReplySuggestionService>();
            builder.Services.AddSingleton<IReplySuggestionService, OpenAiReplySuggestionService>();
            builder.Services.AddSingleton<ReplyComposerViewModel>();
            builder.Services.AddSingleton<MessagesViewModel>();
            builder.Services.AddSingleton(sp => new SchedulingViewModel(sp.GetRequiredService<SchedulingService>(), sp.GetRequiredService<IAppointmentStore>(), sp.GetServices<ISchedulingDraftService>().ToArray(), sp.GetRequiredService<ClinicClock>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SchedulingViewModel>>(), sp.GetRequiredService<MessagingWorkflowService>(), sp.GetRequiredService<ReplyComposerViewModel>(), sp.GetServices<IReplySuggestionService>().ToArray()));
            builder.Services.AddSingleton<ShellViewModel>();
            host = builder.Build();
            shellViewModel = host.Services.GetRequiredService<ShellViewModel>();
            DataContext = shellViewModel;
            Content = BuildScreen(shellViewModel);
            await shellViewModel.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            host?.Services.GetService<ILogger<MainWindow>>()?.LogCritical(ex, "CareDesk could not start");
            MessageBox.Show(this, "CareDesk could not start. Check the local application data folder and restart the application.", "CareDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closeApproved && shellViewModel is not null)
        {
            e.Cancel = true;
            if (!await shellViewModel.FlushAsync())
            {
                MessageBox.Show(this, shellViewModel.Composer.ErrorText ?? "The reply draft could not be saved. Resolve the save error before closing CareDesk.", "Draft not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            closeApproved = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        host?.Dispose();
        base.OnClosed(e);
    }

    private UIElement BuildScreen(ShellViewModel shell)
    {
        var vm = shell.Schedule;
        FrameworkElement? manualChangeTarget = null;
        Control? manualChangeFocus = null;
        ApplyControlStyles();
        var assistance = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        assistance.SetBinding(VisibilityProperty, new Binding("HasRequestAssistance") { Converter = new BooleanVisibilityConverter() });
        var root = new Grid { Background = Canvas, Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Border { Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 8, 6) };
        var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(BrandMark());
        var brandText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; brandText.Children.Add(T("CareDesk", 17, FontWeights.Bold)); brandText.Children.Add(T("Visual Studio Community Clinic", 11, null, Muted)); brand.Children.Add(brandText); head.Children.Add(brand);
        var sectionNav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var messagesNav = Button("Messages", Purple, true);
        messagesNav.Margin = new Thickness(0, 0, 6, 0);
        messagesNav.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ShowMessagesCommand") { Source = shell });
        var scheduleNav = Button("Schedule", null, false);
        scheduleNav.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ShowScheduleCommand") { Source = shell });
        sectionNav.Children.Add(messagesNav);
        sectionNav.Children.Add(scheduleNav);
        Grid.SetColumn(sectionNav, 1);
        head.Children.Add(sectionNav);
        var synthetic = new Border { Background = Brush("#FCEAF4"), BorderBrush = Brush("#EFC8DE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Child = T("SYNTHETIC DATA", 10, FontWeights.Bold, Brush("#712353")) }; Grid.SetColumn(synthetic, 3); head.Children.Add(synthetic);
        var menu = new Menu { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var sampleMenu = new MenuItem { Header = "Sample data" };
        sampleMenu.Items.Add(MenuAction("Reset sample data…", async () =>
        {
            if (MessageBox.Show(this, "Restore the original synthetic schedule, conversations, ownership, drafts, and recorded replies? Saved changes in this application will be replaced.", "Reset sample data", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await vm.ResetAsync();
                await shell.Messages.InitializeAsync(CancellationToken.None);
            }
        }));
        menu.Items.Add(sampleMenu); Grid.SetColumn(menu, 4); head.Children.Add(menu); header.Child = head; root.Children.Add(header);

        var body = new Grid { DataContext = vm }; body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.92, GridUnitType.Star), MinWidth = 330 }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.48, GridUnitType.Star), MinWidth = 555 }); Grid.SetRow(body, 2); root.Children.Add(body);
        var agendaBorder = Card(); Grid.SetColumn(agendaBorder, 0); body.Children.Add(agendaBorder);
        var agenda = new Grid(); agenda.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); agenda.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); agenda.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); agenda.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); agendaBorder.Child = agenda;
        var heading = new DockPanel();
        var refreshAgenda = Button("Refresh agenda", null, false);
        refreshAgenda.HorizontalAlignment = HorizontalAlignment.Right;
        refreshAgenda.VerticalAlignment = VerticalAlignment.Center;
        refreshAgenda.ToolTip = "Reload saved appointments and patient requests";
        refreshAgenda.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RefreshAgendaCommand"));
        DockPanel.SetDock(refreshAgenda, Dock.Right);
        heading.Children.Add(refreshAgenda);
        var agendaTitle = new StackPanel(); agendaTitle.Children.Add(T("DAILY AGENDA", 10, FontWeights.Bold, Purple)); agendaTitle.Children.Add(T("Appointments", 20, FontWeights.SemiBold)); heading.Children.Add(agendaTitle);
        agenda.Children.Add(heading);
        var dateBar = new Grid { Margin = new Thickness(0, 9, 0, 7) }; dateBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); dateBar.ColumnDefinitions.Add(new ColumnDefinition()); dateBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(dateBar, 1); agenda.Children.Add(dateBar);
        var prev = Button("‹", null, false); prev.Width = 34; prev.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("PreviousDayCommand")); AutomationProperties.SetName(prev, "Previous day"); dateBar.Children.Add(prev);
        var day = T("", 12, FontWeights.SemiBold); day.HorizontalAlignment = HorizontalAlignment.Center; day.VerticalAlignment = VerticalAlignment.Center; day.SetBinding(TextBlock.TextProperty, new Binding("CurrentDayLabel")); Grid.SetColumn(day, 1); dateBar.Children.Add(day);
        var next = Button("›", null, false); next.Width = 34; next.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("NextDayCommand")); AutomationProperties.SetName(next, "Next day"); Grid.SetColumn(next, 2); dateBar.Children.Add(next);
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, ItemTemplate = AppointmentTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch,  };
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Continue);
        list.ItemContainerStyle = AppointmentItemStyle();
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Appointments")); list.SetBinding(Selector.SelectedItemProperty, new Binding("SelectedRow") { Mode = BindingMode.TwoWay }); Grid.SetRow(list, 2); agenda.Children.Add(list);
        var empty = T("No appointments are scheduled for this day.", 12, null, Muted); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextWrapping = TextWrapping.Wrap; empty.SetBinding(VisibilityProperty, new Binding("Appointments.Count") { Converter = new CountVisibilityConverter() }); Grid.SetRow(empty, 2); agenda.Children.Add(empty);
        var zoneLabel = T("", 10, null, Muted); zoneLabel.SetBinding(TextBlock.TextProperty, new Binding("TimeZoneLabel")); zoneLabel.Margin = new Thickness(0, 6, 0, 0); Grid.SetRow(zoneLabel, 3); agenda.Children.Add(zoneLabel);

        var detailsBorder = Card(); detailsBorder.Padding = new Thickness(15, 11, 15, 10); Grid.SetColumn(detailsBorder, 2); body.Children.Add(detailsBorder);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; detailsBorder.Child = scroll;
        var details = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; scroll.Content = details;
        var detailTitle = new Grid(); detailTitle.ColumnDefinitions.Add(new ColumnDefinition()); detailTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var titleStack = new StackPanel(); titleStack.Children.Add(T("RESCHEDULE APPOINTMENT", 10, FontWeights.Bold, Purple)); titleStack.Children.Add(T("Review details", 20, FontWeights.SemiBold)); detailTitle.Children.Add(titleStack); var code = T("", 11, null, Muted); code.VerticalAlignment = VerticalAlignment.Center; code.SetBinding(TextBlock.TextProperty, new Binding("CurrentPatientCode")); Grid.SetColumn(code, 1); detailTitle.Children.Add(code); details.Children.Add(detailTitle);
        var summary = new Grid { Margin = new Thickness(0, 8, 0, 8), Background = Brush("#FBF9FC") }; summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) }); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); summary.Children.Add(SummaryCell("PATIENT", "CurrentPatient", 0)); summary.Children.Add(SummaryCell("CURRENT CLINICIAN", "CurrentClinician", 1)); summary.Children.Add(SummaryCell("CURRENT TIME", "CurrentInterval", 2)); details.Children.Add(new Border { Background = Brush("#FBF9FC"), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 6, 9, 6), Child = summary });
        var requestBadge = new ContentControl { ContentTemplate = RequestBadgeTemplate() };
        requestBadge.SetBinding(ContentControl.ContentProperty, new Binding("."));
        assistance.Children.Add(requestBadge);
        var requestMeta = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        var requestStatus = T("", 11, FontWeights.SemiBold, Purple);
        requestStatus.SetBinding(TextBlock.TextProperty, new Binding("RequestStatus"));
        requestStatus.Margin = new Thickness(0, 0, 12, 0);
        requestMeta.Children.Add(requestStatus);
        var requestOwner = T("", 11, null, Muted);
        requestOwner.SetBinding(TextBlock.TextProperty, new Binding("RequestOwner") { StringFormat = "Owner · {0}" });
        requestMeta.Children.Add(requestOwner);
        assistance.Children.Add(requestMeta);
        var ownershipHelp = T("", 10, null, Muted);
        ownershipHelp.TextWrapping = TextWrapping.Wrap;
        ownershipHelp.SetBinding(TextBlock.TextProperty, new Binding("OwnershipExplanation"));
        assistance.Children.Add(ownershipHelp);
        var ownershipActions = new WrapPanel { Margin = new Thickness(0, 5, 0, 5) };
        var assignment = Button("", null, false);
        assignment.Margin = new Thickness(0, 0, 8, 0);
        assignment.SetBinding(ContentControl.ContentProperty, new Binding("AssignmentActionLabel"));
        assignment.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ToggleRequestAssignmentCommand"));
        assignment.SetBinding(IsEnabledProperty, new Binding("CanToggleAssignment"));
        ownershipActions.Children.Add(assignment);
        var openConversation = Button("Open in Messages", null, false);
        openConversation.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("OpenConversationCommand") { Source = shell });
        ownershipActions.Children.Add(openConversation);
        assistance.Children.Add(ownershipActions);
        var originalMessage = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#FBF9FC"), Margin = new Thickness(0, 6, 0, 12) };
        AutomationProperties.SetName(originalMessage, "Original patient message");
        originalMessage.SetBinding(TextBox.TextProperty, new Binding("PatientRequest") { Mode = BindingMode.OneWay });
        assistance.Children.Add(originalMessage);
        assistance.Children.Add(T("Suggested actions", 14, FontWeights.SemiBold));
        var suggested = new StackPanel();
        var appointmentTitle = T("Update appointment", 11, FontWeights.Bold, Purple);
        var recommendation = T("", 13, FontWeights.SemiBold);
        recommendation.TextWrapping = TextWrapping.Wrap;
        recommendation.SetBinding(TextBlock.TextProperty, new Binding("SuggestedAction"));
        suggested.Children.Add(recommendation);
        var source = T("", 10, null, Muted);
        source.Margin = new Thickness(0, 5, 0, 0);
        source.TextWrapping = TextWrapping.Wrap;
        source.SetBinding(TextBlock.TextProperty, new Binding("DraftStatus"));
        suggested.Children.Add(source);
        var suggestionActions = new WrapPanel();
        var update = Button("Update", Purple, true);
        update.Margin = new Thickness(0, 0, 8, 0);
        update.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("AcceptSuggestedUpdateCommand"));
        update.SetBinding(IsEnabledProperty, new Binding("CanAcceptSuggestedUpdate"));
        suggestionActions.Children.Add(update);
        var updateDifferently = Button("Update differently", null, false);
        updateDifferently.Margin = new Thickness(0, 0, 8, 0);
        updateDifferently.SetBinding(IsEnabledProperty, new Binding("CanEditAppointment"));
        updateDifferently.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("BeginManualUpdateCommand"));
        updateDifferently.Click += (_, _) => Dispatcher.BeginInvoke(() => { manualChangeTarget?.BringIntoView(); manualChangeFocus?.Focus(); });
        suggestionActions.Children.Add(updateDifferently);
        var rejectAppointment = Button("Reject", null, false);
        rejectAppointment.Margin = new Thickness(0, 0, 8, 0);
        rejectAppointment.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RejectAppointmentSuggestionCommand"));
        suggestionActions.Children.Add(rejectAppointment);
        var mode = new ComboBox { Width = 138, ItemsSource = new[] { "Sample mode", "Live mode" }, ToolTip = "Recommendation source" };
        AutomationProperties.SetName(mode, "Recommendation source");
        mode.SetBinding(Selector.SelectedItemProperty, new Binding("Mode") { Mode = BindingMode.TwoWay });
        suggestionActions.Children.Add(mode);
        suggested.Children.Add(suggestionActions);
        var appointmentSuggestionCard = SuggestionCard(appointmentTitle, suggested, "AppointmentActionOutcome", "IsAppointmentSuggestionActive", PaleLavender);
        appointmentSuggestionCard.SetBinding(VisibilityProperty, new Binding("HasAppointmentSuggestion") { Converter = new BooleanVisibilityConverter() });
        assistance.Children.Add(appointmentSuggestionCard);
        var requestNote = T("Update applies and saves this suggestion. Update differently opens the manual appointment fields. Neither action records a patient reply.", 10, null, Muted);
        requestNote.TextWrapping = TextWrapping.Wrap;
        requestNote.Margin = new Thickness(0, 5, 0, 0);
        requestNote.SetBinding(VisibilityProperty, new Binding("ShowAppointmentSuggestionHelp") { Converter = new BooleanVisibilityConverter() });
        assistance.Children.Add(requestNote);
        var replySuggestion = new StackPanel();
        var replyTitle = T("", 11, FontWeights.Bold, Purple);
        replyTitle.SetBinding(TextBlock.TextProperty, new Binding("ReplySuggestionTitle"));
        var replyText = T("", 12, FontWeights.SemiBold);
        replyText.TextWrapping = TextWrapping.Wrap;
        replyText.Margin = new Thickness(0, 4, 0, 4);
        replyText.SetBinding(TextBlock.TextProperty, new Binding("SuggestedReplyText"));
        replySuggestion.Children.Add(replyText);
        var replyHelp = T("", 10, null, Muted);
        replyHelp.TextWrapping = TextWrapping.Wrap;
        replyHelp.SetBinding(TextBlock.TextProperty, new Binding("ReplySuggestionStatus"));
        replySuggestion.Children.Add(replyHelp);
        var replyActions = new WrapPanel();
        var recordReply = Button("Record message", Purple, true);
        recordReply.Margin = new Thickness(0, 0, 8, 0);
        recordReply.ToolTip = "Record locally; no patient delivery is connected";
        recordReply.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RecordSuggestedReplyCommand"));
        recordReply.SetBinding(IsEnabledProperty, new Binding("CanUseReplySuggestion"));
        replyActions.Children.Add(recordReply);
        var editReply = Button("Edit draft", null, false);
        editReply.Margin = new Thickness(0, 0, 8, 0);
        editReply.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("EditSuggestedReplyCommand"));
        editReply.SetBinding(IsEnabledProperty, new Binding("CanUseReplySuggestion"));
        replyActions.Children.Add(editReply);
        var rejectReply = Button("Reject", null, false);
        rejectReply.Margin = new Thickness(0, 0, 8, 0);
        rejectReply.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RejectReplySuggestionCommand"));
        replyActions.Children.Add(rejectReply);
        var updateDraft = Button("Update draft", null, false);
        updateDraft.Margin = new Thickness(0, 0, 8, 0);
        updateDraft.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("UpdateStaleDraftCommand"));
        updateDraft.SetBinding(IsEnabledProperty, new Binding("Composer.IsStale"));
        replySuggestion.Children.Add(replyActions);
        var replyGenerating = new ProgressBar { Height = 3, IsIndeterminate = true, Foreground = Purple, Margin = new Thickness(0, 6, 0, 0) };
        replyGenerating.SetBinding(VisibilityProperty, new Binding("IsPreparingReplySuggestion") { Converter = new BooleanVisibilityConverter() });
        replySuggestion.Children.Add(replyGenerating);
        var replySuggestionCard = SuggestionCard(replyTitle, replySuggestion, "ReplyActionOutcome", "IsReplySuggestionActive", Brush("#FCEAF4"));
        replySuggestionCard.SetBinding(VisibilityProperty, new Binding("HasReplySuggestion") { Converter = new BooleanVisibilityConverter() });
        assistance.Children.Add(replySuggestionCard);
        var replyComposer = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 7, 0, 4) };
        replyComposer.SetBinding(TextBox.TextProperty, new Binding("Composer.Text") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        replyComposer.SetBinding(IsEnabledProperty, new Binding("Composer.IsEditable"));
        replyComposer.SetBinding(VisibilityProperty, new Binding("Composer.HasDraft") { Converter = new BooleanVisibilityConverter() });
        AutomationProperties.SetName(replyComposer, "Saved unsent patient reply draft");
        assistance.Children.Add(replyComposer);
        var composerActions = new WrapPanel();
        composerActions.SetBinding(VisibilityProperty, new Binding("Composer.HasDraft") { Converter = new BooleanVisibilityConverter() });
        var recordDraft = Button("Record draft", Purple, true);
        recordDraft.Margin = new Thickness(0, 0, 8, 0);
        recordDraft.ToolTip = "Record locally; no patient delivery is connected";
        recordDraft.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RecordReplyDraftCommand"));
        recordDraft.SetBinding(IsEnabledProperty, new Binding("Composer.CanRecord"));
        composerActions.Children.Add(recordDraft);
        composerActions.Children.Add(updateDraft);
        assistance.Children.Add(composerActions);
        var replyStatus = T("", 10, null, Muted);
        replyStatus.TextWrapping = TextWrapping.Wrap;
        replyStatus.SetBinding(TextBlock.TextProperty, new Binding("Composer.Status"));
        assistance.Children.Add(replyStatus);
        var replyError = T("", 10, FontWeights.SemiBold, Error);
        replyError.TextWrapping = TextWrapping.Wrap;
        replyError.SetBinding(TextBlock.TextProperty, new Binding("Composer.ErrorText"));
        assistance.Children.Add(replyError);
        var generating = new ProgressBar { Height = 3, IsIndeterminate = true, Foreground = Purple, Margin = new Thickness(0, 6, 0, 0) };
        generating.SetBinding(VisibilityProperty, new Binding("IsPreparingSuggestion") { Converter = new BooleanVisibilityConverter() });
        suggested.Children.Add(generating);
        details.Children.Add(assistance);
        details.Children.Add(T("Change appointment", 14, FontWeights.SemiBold));
        var editHelp = T("Review the updated details below before saving this booking.", 11, null, Muted);
        editHelp.TextWrapping = TextWrapping.Wrap;
        details.Children.Add(editHelp);
        var form = new Grid { Margin = new Thickness(0, 5, 0, 0) }; form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.18, GridUnitType.Star) }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.08, GridUnitType.Star) }); form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.72, GridUnitType.Star) });
        var clinician = new ComboBox { DisplayMemberPath = "DisplayName", Margin = new Thickness(0, 0, 7, 0) }; clinician.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Clinicians")); clinician.SetBinding(Selector.SelectedItemProperty, new Binding("ProposedClinician") { Mode = BindingMode.TwoWay }); form.Children.Add(Field("CLINICIAN", clinician, 0));
        manualChangeTarget = form;
        manualChangeFocus = clinician;
        var date = new DatePicker { Margin = new Thickness(0, 0, 7, 0) }; date.SetBinding(DatePicker.SelectedDateProperty, new Binding("ProposedDate") { Mode = BindingMode.TwoWay }); form.Children.Add(Field("DATE · CLINIC TIME", date, 1));
        var time = new TextBox { Margin = new Thickness(0, 0, 7, 0), ToolTip = "For example, 11:00 AM" }; time.SetBinding(TextBox.TextProperty, new Binding("ProposedTime") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); form.Children.Add(Field("START TIME", time, 2));
        var duration = new ComboBox { ItemsSource = new[] { 15, 30, 45, 60 } }; duration.SetBinding(Selector.SelectedItemProperty, new Binding("ProposedDuration") { Mode = BindingMode.TwoWay }); form.Children.Add(Field("DURATION · MIN", duration, 3)); details.Children.Add(form);
        form.SetBinding(IsEnabledProperty, new Binding("AreManualAppointmentFieldsEnabled"));
        var review = T("", 11, FontWeights.SemiBold, Error); review.TextWrapping = TextWrapping.Wrap; review.Margin = new Thickness(0, 3, 0, 0); review.SetBinding(TextBlock.TextProperty, new Binding("ReviewNote")); details.Children.Add(review);
        var comparison = new Grid { Margin = new Thickness(0, 7, 0, 0), Background = Brush("#F7F0FA") }; comparison.ColumnDefinitions.Add(new ColumnDefinition()); comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); comparison.ColumnDefinitions.Add(new ColumnDefinition()); comparison.Children.Add(CompareCell("SAVED BOOKING", "CurrentInterval", "CurrentClinician", 0)); var arrow = T("→", 19, FontWeights.SemiBold, Purple); arrow.Margin = new Thickness(10, 0, 10, 0); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 1); comparison.Children.Add(arrow); comparison.Children.Add(CompareCell("AFTER SAVING", "ProposedInterval", "ProposedClinician.DisplayName", 2)); var compareBorder = new Border { Background = Brush("#F7F0FA"), BorderBrush = Brush("#E9D9F0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Child = comparison }; compareBorder.SetBinding(VisibilityProperty, new Binding("IsDirty") { Converter = new BooleanVisibilityConverter() }); details.Children.Add(compareBorder);
        var error = T("", 11, FontWeights.SemiBold, Error); error.TextWrapping = TextWrapping.Wrap; error.Margin = new Thickness(0, 5, 0, 0); error.SetBinding(TextBlock.TextProperty, new Binding("ErrorText")); details.Children.Add(error);
        var saveRow = new Grid { Margin = new Thickness(0, 6, 0, 0) }; saveRow.ColumnDefinitions.Add(new ColumnDefinition()); saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var note = new StackPanel(); note.Children.Add(T("Clinic time · Eastern Time", 10, null, Muted)); note.Children.Add(T("Save changes is only for manual appointment edits.", 10, null, Muted)); saveRow.Children.Add(note); var confirm = Button("Save changes", Purple, true); confirm.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ConfirmRescheduleCommand")); confirm.SetBinding(IsEnabledProperty, new Binding("CanSaveChanges")); Grid.SetColumn(confirm, 1); saveRow.Children.Add(confirm); details.Children.Add(saveRow);
        var progress = new ProgressBar { Height = 3, IsIndeterminate = true, Foreground = Purple, Margin = new Thickness(0, 5, 0, 0) }; progress.SetBinding(VisibilityProperty, new Binding("IsBusy") { Converter = new BooleanVisibilityConverter() }); details.Children.Add(progress);
        var messagesScreen = BuildMessagesScreen(shell);
        Grid.SetRow(messagesScreen, 2);
        root.Children.Add(messagesScreen);
        void UpdateSection()
        {
            var showMessages = shell.ActiveSection == "Messages";
            messagesScreen.Visibility = showMessages ? Visibility.Visible : Visibility.Collapsed;
            body.Visibility = showMessages ? Visibility.Collapsed : Visibility.Visible;
            messagesNav.Background = showMessages ? Purple : Brushes.White;
            messagesNav.Foreground = showMessages ? Brushes.White : Ink;
            scheduleNav.Background = showMessages ? Brushes.White : Purple;
            scheduleNav.Foreground = showMessages ? Ink : Brushes.White;
        }
        shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ShellViewModel.ActiveSection)) UpdateSection(); };
        UpdateSection();
        return root;
    }

    private void ApplyControlStyles()
    {
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonChrome = new FrameworkElementFactory(typeof(Border));
        buttonChrome.Name = "Chrome";
        buttonChrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        buttonChrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        buttonChrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        buttonChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
        buttonContent.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        buttonContent.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        buttonContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        buttonContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonContent.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        buttonChrome.AppendChild(buttonContent);
        buttonTemplate.VisualTree = buttonChrome;
        var buttonHover = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
        buttonHover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#C42D87"), "Chrome"));
        buttonTemplate.Triggers.Add(buttonHover);
        var buttonFocus = new Trigger { Property = System.Windows.Controls.Button.IsKeyboardFocusedProperty, Value = true };
        buttonFocus.Setters.Add(new Setter(Border.BorderBrushProperty, Purple, "Chrome"));
        buttonFocus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
        buttonTemplate.Triggers.Add(buttonFocus);
        var buttonPressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
        buttonPressed.Setters.Add(new Setter(Border.OpacityProperty, 0.88, "Chrome"));
        buttonTemplate.Triggers.Add(buttonPressed);
        var buttonDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        buttonDisabled.Setters.Add(new Setter(Border.OpacityProperty, 0.52, "Chrome"));
        buttonTemplate.Triggers.Add(buttonDisabled);
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
        button.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        button.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        button.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        button.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
        Resources[typeof(Button)] = button;

        var inputTemplate = new ControlTemplate(typeof(TextBox));
        var inputChrome = new FrameworkElementFactory(typeof(Border));
        inputChrome.Name = "Chrome";
        inputChrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        inputChrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        inputChrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        inputChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var editor = new FrameworkElementFactory(typeof(ScrollViewer));
        editor.Name = "PART_ContentHost";
        editor.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        inputChrome.AppendChild(editor);
        inputTemplate.VisualTree = inputChrome;
        var inputFocus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        inputFocus.Setters.Add(new Setter(Border.BorderBrushProperty, Purple, "Chrome"));
        inputFocus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
        inputTemplate.Triggers.Add(inputFocus);
        var input = new Style(typeof(TextBox));
        input.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        input.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        input.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        input.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        input.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        input.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        input.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        input.Setters.Add(new Setter(TextBox.SelectionBrushProperty, Brush("#F1B5D6")));
        input.Setters.Add(new Setter(TextBox.CaretBrushProperty, Purple));
        input.Setters.Add(new Setter(Control.TemplateProperty, inputTemplate));
        Resources[typeof(TextBox)] = input;

        var combo = new Style(typeof(ComboBox));
        combo.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        combo.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        combo.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
        combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        var comboTemplate = new ControlTemplate(typeof(ComboBox));
        var comboChrome = new FrameworkElementFactory(typeof(Border));
        comboChrome.Name = "ComboChrome";
        comboChrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        comboChrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        comboChrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        comboChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var comboParts = new FrameworkElementFactory(typeof(DockPanel));
        var dropToggle = new FrameworkElementFactory(typeof(ToggleButton));
        dropToggle.Name = "DropDownToggle";
        dropToggle.SetValue(DockPanel.DockProperty, Dock.Right);
        dropToggle.SetValue(Control.WidthProperty, 28d);
        dropToggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        dropToggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        dropToggle.SetValue(Control.ForegroundProperty, Muted);
        dropToggle.SetValue(ContentControl.ContentProperty, "▾");
        dropToggle.SetValue(Control.FocusableProperty, false);
        dropToggle.SetValue(ButtonBase.ClickModeProperty, ClickMode.Press);
        dropToggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Mode = BindingMode.TwoWay });
        comboParts.AppendChild(dropToggle);
        var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter));
        selectedContent.Name = "SelectionSite";
        selectedContent.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
        selectedContent.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        selectedContent.SetValue(ContentPresenter.ContentTemplateSelectorProperty, new TemplateBindingExtension(ItemsControl.ItemTemplateSelectorProperty));
        selectedContent.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        selectedContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        comboParts.AppendChild(selectedContent);
        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.FocusableProperty, false);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var popupChrome = new FrameworkElementFactory(typeof(Border));
        popupChrome.SetValue(Border.BackgroundProperty, Brushes.White);
        popupChrome.SetValue(Border.BorderBrushProperty, Line);
        popupChrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popupChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var popupScroll = new FrameworkElementFactory(typeof(ScrollViewer));
        popupScroll.Name = "PART_ScrollViewer";
        popupScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        popupScroll.SetValue(FrameworkElement.MaxHeightProperty, 260d);
        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        itemsPresenter.Name = "ItemsPresenter";
        popupScroll.AppendChild(itemsPresenter);
        popupChrome.AppendChild(popupScroll);
        popup.AppendChild(popupChrome);
        comboParts.AppendChild(popup);
        comboChrome.AppendChild(comboParts);
        comboTemplate.VisualTree = comboChrome;
        var comboFocus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        comboFocus.Setters.Add(new Setter(Border.BorderBrushProperty, Purple, "ComboChrome"));
        comboTemplate.Triggers.Add(comboFocus);
        var comboOpen = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
        comboOpen.Setters.Add(new Setter(Border.BorderBrushProperty, Purple, "ComboChrome"));
        comboTemplate.Triggers.Add(comboOpen);
        var comboDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        comboDisabled.Setters.Add(new Setter(Border.OpacityProperty, 0.6, "ComboChrome"));
        comboTemplate.Triggers.Add(comboDisabled);
        combo.Setters.Add(new Setter(Control.TemplateProperty, comboTemplate));
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
        var itemTemplate = new ControlTemplate(typeof(ComboBoxItem));
        var itemChrome = new FrameworkElementFactory(typeof(Border));
        itemChrome.Name = "ItemChrome";
        itemChrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        itemChrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var itemPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        itemPresenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        itemChrome.AppendChild(itemPresenter);
        itemTemplate.VisualTree = itemChrome;
        var itemSelected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
        itemSelected.Setters.Add(new Setter(Border.BackgroundProperty, PaleLavender, "ItemChrome"));
        itemTemplate.Triggers.Add(itemSelected);
        var itemHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        itemHover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#F1F7F6"), "ItemChrome"));
        itemTemplate.Triggers.Add(itemHover);
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        Resources[typeof(ComboBoxItem)] = itemStyle;
        Resources[typeof(ComboBox)] = combo;
        var picker = new Style(typeof(DatePicker));
        picker.Setters.Add(new Setter(Control.MinHeightProperty, 34d));
        Resources[typeof(DatePicker)] = picker;
    }

    private static Border BrandMark()
    {
        static LinearGradientBrush RibbonGradient(Point start, Point end, params (double Offset, string Color)[] stops)
        {
            var gradient = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
            foreach (var stop in stops)
                gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stop.Color), stop.Offset));
            return gradient;
        }

        const string backRibbon = "M241.24 39.158C238.4 37.998 235.28 37.318 232 37.318C224 37.318 216.92 41.238 212.56 47.238L212.48 47.358L192 75.598L154 127.998L114.28 182.758L95.52 208.638L95.4399 208.758C91.0799 214.758 84 218.678 76 218.678C72.16 218.678 68.52 217.758 65.28 216.158C65.2 216.118 65.12 216.078 65.08 216.038L13.5601 189.598C16.7201 191.118 20.28 191.998 24 191.998C32.44 191.998 39.84 187.638 44.12 181.078L52 168.958L78.64 127.998L114.28 73.238L147.88 21.598C152.16 14.998 159.56 10.678 168 10.678C171.28 10.678 174.4 11.318 177.24 12.518L241.24 39.158Z";
        const string frontRibbon = "M241.24 216.838L177.24 243.478C174.4 244.678 171.28 245.318 168 245.318C159.56 245.318 152.16 240.998 147.88 234.398L114.28 182.758L78.64 127.998L52 87.0381L44.12 74.9181C39.84 68.3581 32.44 63.9981 24 63.9981C20.28 63.9981 16.7201 64.8781 13.5601 66.3981L65.08 39.9581C65.12 39.9181 65.2 39.8781 65.28 39.8381C68.52 38.2381 72.16 37.3181 76 37.3181C84 37.3181 91.0799 41.2381 95.4399 47.2381L95.52 47.3581L114.28 73.2381L154 127.998L192 180.398L212.48 208.638L212.56 208.758C216.92 214.758 224 218.678 232 218.678C235.28 218.678 238.4 217.998 241.24 216.838Z";
        var artwork = new Canvas { Width = 256, Height = 256 };
        artwork.Children.Add(MarkPath("M65.08 216.038L13.5601 189.598L13.08 189.358C5.31996 185.398 0 177.318 0 167.998V87.998C0 78.678 5.31996 70.598 13.08 66.638L13.5601 66.398L65.08 39.958C57.32 43.958 52 51.998 52 61.318V194.678C52 203.998 57.32 212.038 65.08 216.038Z",
            RibbonGradient(new Point(-24.2354, 216.038), new Point(83.5542, 158.686), (.0725439, "#85148A"), (.296162, "#7F21A9"), (.661273, "#5B2AB5"), (.854932, "#4B20A0"))));
        artwork.Children.Add(MarkPath(backRibbon,
            RibbonGradient(new Point(165.436, 2.67797), new Point(55.8073, 233.437), (0, "#4B20A0"), (.265936, "#6D37CD"), (.7848, "#7A41DC"), (1, "#CA59F7"))));
        var shadow = RibbonGradient(new Point(142.065, 110.678), new Point(63.0371, 167.208), (0, "#0030116E"), (.164241, "#B330116E"), (.712141, "#30116E"), (1, "#0030116E"));
        shadow.Opacity = .3;
        artwork.Children.Add(MarkPath(backRibbon, shadow));
        artwork.Children.Add(MarkPath(frontRibbon,
            RibbonGradient(new Point(44.8258, 37.3181), new Point(164.911, 271.838), (.0374343, "#FFBAC7"), (.187907, "#EA71EF"), (.349289, "#CA59F7"), (.618324, "#8B52F4"), (1, "#5750E2"))));
        var depth = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 1, RadiusY = 1,
            Transform = new MatrixTransform(215.747, 86.6666, -197.178, 492.004, -3.69969, 149.318)
        };
        depth.GradientStops.Add(new GradientStop(Color.FromArgb(0, 139, 82, 244), .611142));
        depth.GradientStops.Add(new GradientStop(Color.FromRgb(48, 17, 110), .977982));
        artwork.Children.Add(MarkPath(frontRibbon, depth));
        artwork.Children.Add(MarkPath("M256 61.3181V194.678C256 204.638 249.92 213.198 241.24 216.838L177.24 243.478C185.92 239.878 192 231.318 192 221.318V34.6781C192 24.6781 185.92 16.1181 177.24 12.5181L241.24 39.1581C249.92 42.7981 256 51.3581 256 61.3181Z",
            RibbonGradient(new Point(216.62, -10.6204), new Point(216.62, 274.614), (0, "#FFBAC7"), (.291496, "#EA71EF"), (.675458, "#8B52F4"), (.916582, "#5750E2"))));

        var layers = new Grid();
        layers.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M20,36 C17,33 4,25 4,16 C4,9 13,6 20,14 C27,6 36,9 36,16 C36,25 23,34 20,36 Z"),
            Fill = Brushes.White,
            RenderTransform = new TranslateTransform(0, -2.5)
        });
        layers.Children.Add(new Viewbox { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = artwork });
        return new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Child = layers,
            Background = Brush("#EF91BE")
        };
    }

    private static System.Windows.Shapes.Path MarkPath(string geometry, Brush fill) => new() { Data = Geometry.Parse(geometry), Fill = fill };

    private static Border Card() => new() { Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(13) };
    private static Border SuggestionCard(TextBlock title, StackPanel body, string outcomeProperty, string activeProperty, Brush background)
    {
        var inactive = new DataTrigger { Binding = new Binding(activeProperty), Value = false };
        inactive.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#F2F2F4")));
        var cardStyle = new Style(typeof(Border));
        cardStyle.Setters.Add(new Setter(Border.BackgroundProperty, background));
        cardStyle.Triggers.Add(inactive);
        title.ClearValue(TextBlock.ForegroundProperty);
        var titleStyle = new Style(typeof(TextBlock));
        titleStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Purple));
        var muted = new DataTrigger { Binding = new Binding(activeProperty), Value = false };
        muted.Setters.Add(new Setter(TextBlock.ForegroundProperty, Muted));
        titleStyle.Triggers.Add(muted);
        title.Style = titleStyle;
        var label = T("", 10, FontWeights.Bold, Brush("#216441"));
        label.SetBinding(TextBlock.TextProperty, new Binding(outcomeProperty));
        var badgeStyle = new Style(typeof(Border));
        badgeStyle.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#DDF2E5")));
        badgeStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var completed = new DataTrigger { Binding = new Binding(activeProperty), Value = false };
        completed.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        badgeStyle.Triggers.Add(completed);
        var rejected = new DataTrigger { Binding = new Binding(outcomeProperty), Value = "REJECTED" };
        rejected.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#FBE3DC")));
        badgeStyle.Triggers.Add(rejected);
        var labelStyle = new Style(typeof(TextBlock));
        label.ClearValue(TextBlock.ForegroundProperty);
        labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush("#216441")));
        var rejectedLabel = new DataTrigger { Binding = new Binding(outcomeProperty), Value = "REJECTED" };
        rejectedLabel.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush("#943F2B")));
        labelStyle.Triggers.Add(rejectedLabel);
        label.Style = labelStyle;
        var badge = new Border { Style = badgeStyle, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(10, 0, 0, 0), Child = label };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        body.Margin = new Thickness(0, 4, 0, 0);
        body.SetBinding(VisibilityProperty, new Binding(activeProperty) { Converter = new BooleanVisibilityConverter() });
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(body);
        return new Border { Style = cardStyle, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 6), Child = content };
    }
    private static TextBlock T(string text, double size = 13, FontWeight? weight = null, Brush? color = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = color ?? Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private static Brush Brush(string hex) => new BrushConverter().ConvertFromString(hex) as Brush ?? Brushes.Black;
    private static Button Button(string text, Brush? fill, bool primary) => new() { Content = text, Background = fill ?? Brushes.White, Foreground = primary ? Brushes.White : Ink, BorderBrush = fill ?? Line, BorderThickness = new Thickness(1), Padding = new Thickness(11, 6, 11, 6), MinHeight = 32, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
    private static MenuItem MenuAction(string title, Action run) { var item = new MenuItem { Header = title }; item.Click += (_, _) => run(); return item; }
    private static Border Field(string label, Control control, int column) { var stack = new StackPanel(); stack.Children.Add(T(label, 10, FontWeights.SemiBold, Muted)); stack.Children.Add(control); var border = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 0) }; Grid.SetColumn(border, column); return border; }
    private static Border SummaryCell(string label, string binding, int column) { var stack = new StackPanel(); stack.Children.Add(T(label, 9, FontWeights.SemiBold, Muted)); var value = T("", 12, FontWeights.SemiBold); value.SetBinding(TextBlock.TextProperty, new Binding(binding)); stack.Children.Add(value); var border = new Border { Padding = new Thickness(3), Child = stack }; Grid.SetColumn(border, column); return border; }
    private static StackPanel CompareCell(string title, string first, string second, int column) { var stack = new StackPanel(); stack.Children.Add(T(title, 9, FontWeights.SemiBold, Muted)); var a = T("", 12, FontWeights.SemiBold); a.SetBinding(TextBlock.TextProperty, new Binding(first)); stack.Children.Add(a); var b = T("", 10, null, Muted); b.SetBinding(TextBlock.TextProperty, new Binding(second)); stack.Children.Add(b); Grid.SetColumn(stack, column); return stack; }
    private static DataTemplate RequestBadgeTemplate()
    {
        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(Border.BackgroundProperty, Brush("#FCEAF4"));
        badge.SetValue(Border.BorderBrushProperty, Brush("#EFC8DE"));
        badge.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        badge.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        badge.SetValue(FrameworkElement.ToolTipProperty, "Patient rescheduling request — needs attention");
        badge.SetValue(AutomationProperties.NameProperty, "Patient request — needs attention");
        badge.SetBinding(VisibilityProperty, new Binding("HasPatientRequest") { Converter = new BooleanVisibilityConverter() });
        var content = new FrameworkElementFactory(typeof(StackPanel));
        content.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.TextProperty, "\uE8F2");
        icon.SetValue(TextBlock.ForegroundProperty, Brush("#A52370"));
        icon.SetValue(TextBlock.FontSizeProperty, 14d);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        content.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextProperty, "Patient request");
        label.SetValue(TextBlock.ForegroundProperty, Brush("#712353"));
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetValue(TextBlock.FontSizeProperty, 11d);
        content.AppendChild(label);
        badge.AppendChild(content);
        return new DataTemplate { VisualTree = badge };
    }

    private static DataTemplate AppointmentTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(".") { Converter = new AppointmentRowConverter() });
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.PaddingProperty, new Thickness(2));
        row.AppendChild(text);
        var badge = new FrameworkElementFactory(typeof(ContentControl));
        badge.SetBinding(ContentControl.ContentProperty, new Binding("."));
        badge.SetValue(ContentControl.ContentTemplateProperty, RequestBadgeTemplate());
        row.AppendChild(badge);
        return new DataTemplate { VisualTree = row };
    }

    private static Style AppointmentItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 2, 0, 2)));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "SelectionBorder";
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.White);
        borderFactory.SetValue(Border.BorderBrushProperty, Line);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 6, 8, 6));
        borderFactory.AppendChild(presenter);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = borderFactory };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, PaleLavender, "SelectionBorder"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Purple, "SelectionBorder"));
        selected.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "SelectionBorder"));
        template.Triggers.Add(selected);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}

public sealed class BooleanVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is Visibility.Visible;
}
public sealed class CountVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}

public sealed class AppointmentRowConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is AppointmentRow row ? $"{row.Time}   {row.Patient}\n{row.Clinician} · {row.Visit} · {row.Status}" : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}
