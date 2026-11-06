using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace CareDesk.Desktop;

public partial class MainWindow
{
    private UIElement BuildMessagesScreen(ShellViewModel shell)
    {
        var vm = shell.Messages;
        var body = new Grid { DataContext = vm };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.82, GridUnitType.Star), MinWidth = 270 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star), MinWidth = 430 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.02, GridUnitType.Star), MinWidth = 330 });

        var inboxCard = Card();
        Grid.SetColumn(inboxCard, 0);
        body.Children.Add(inboxCard);
        var inbox = new Grid();
        inbox.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inbox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inboxCard.Child = inbox;
        var inboxHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = Button("Refresh", null, false);
        refresh.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RefreshCommand"));
        DockPanel.SetDock(refresh, Dock.Right);
        inboxHeader.Children.Add(refresh);
        var inboxTitle = new StackPanel();
        inboxTitle.Children.Add(T("MESSAGES", 10, FontWeights.Bold, Purple));
        inboxTitle.Children.Add(T("Scheduling inbox", 20, FontWeights.SemiBold));
        inboxHeader.Children.Add(inboxTitle);
        inbox.Children.Add(inboxHeader);
        var conversationList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, ItemTemplate = ConversationTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch, ClipToBounds = true };
        conversationList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        conversationList.ItemContainerStyle = AppointmentItemStyle();
        conversationList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Conversations"));
        conversationList.SetBinding(Selector.SelectedItemProperty, new Binding("SelectedConversation") { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(conversationList, "Scheduling conversations");
        Grid.SetRow(conversationList, 1);
        inbox.Children.Add(conversationList);

        var threadCard = Card();
        Grid.SetColumn(threadCard, 2);
        body.Children.Add(threadCard);
        var thread = new Grid();
        thread.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        thread.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        thread.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        threadCard.Child = thread;
        var threadHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        threadHeader.ColumnDefinitions.Add(new ColumnDefinition());
        threadHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var patient = new StackPanel();
        patient.Children.Add(T("CONVERSATION", 10, FontWeights.Bold, Purple));
        var patientName = T("", 20, FontWeights.SemiBold);
        patientName.SetBinding(TextBlock.TextProperty, new Binding("PatientName"));
        patient.Children.Add(patientName);
        var patientCode = T("", 10, null, Muted);
        patientCode.SetBinding(TextBlock.TextProperty, new Binding("PatientCode"));
        patient.Children.Add(patientCode);
        threadHeader.Children.Add(patient);
        var state = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var status = T("", 11, FontWeights.SemiBold, Purple);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.SetBinding(TextBlock.TextProperty, new Binding("RequestStatus"));
        state.Children.Add(status);
        var owner = T("", 10, null, Muted);
        owner.HorizontalAlignment = HorizontalAlignment.Right;
        owner.SetBinding(TextBlock.TextProperty, new Binding("Owner") { StringFormat = "Owner · {0}" });
        state.Children.Add(owner);
        Grid.SetColumn(state, 1);
        threadHeader.Children.Add(state);
        thread.Children.Add(threadHeader);
        var messageList = new ListBox { BorderThickness = new Thickness(0), Background = Brush("#FBF9FC"), ItemTemplate = MessageTemplate(), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8) };
        messageList.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Messages"));
        AutomationProperties.SetName(messageList, "Chronological message history");
        Grid.SetRow(messageList, 1);
        thread.Children.Add(messageList);
        var threadFooter = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        var loading = new ProgressBar { Height = 3, IsIndeterminate = true, Foreground = Purple };
        loading.SetBinding(VisibilityProperty, new Binding("IsLoading") { Converter = new BooleanVisibilityConverter() });
        threadFooter.Children.Add(loading);
        var error = T("", 11, FontWeights.SemiBold, Error);
        error.TextWrapping = TextWrapping.Wrap;
        error.SetBinding(TextBlock.TextProperty, new Binding("ErrorText"));
        threadFooter.Children.Add(error);
        Grid.SetRow(threadFooter, 2);
        thread.Children.Add(threadFooter);

        var workCard = Card();
        Grid.SetColumn(workCard, 4);
        body.Children.Add(workCard);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        workCard.Child = scroll;
        var work = new StackPanel();
        scroll.Content = work;
        work.Children.Add(T("WORK REQUEST", 10, FontWeights.Bold, Purple));
        work.Children.Add(T("Responsibility", 18, FontWeights.SemiBold));
        var ownership = T("", 10, null, Muted);
        ownership.TextWrapping = TextWrapping.Wrap;
        ownership.Margin = new Thickness(0, 4, 0, 7);
        ownership.SetBinding(TextBlock.TextProperty, new Binding("OwnershipExplanation"));
        work.Children.Add(ownership);
        var ownerActions = new WrapPanel();
        var assignment = Button("", Purple, true);
        assignment.Margin = new Thickness(0, 0, 7, 7);
        assignment.SetBinding(ContentControl.ContentProperty, new Binding("AssignmentActionLabel"));
        assignment.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ToggleAssignmentCommand"));
        assignment.SetBinding(IsEnabledProperty, new Binding("CanToggleAssignment"));
        ownerActions.Children.Add(assignment);
        work.Children.Add(ownerActions);
        var assignee = new ComboBox { DisplayMemberPath = "DisplayName", Margin = new Thickness(0, 0, 0, 6) };
        assignee.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Staff"));
        assignee.SetBinding(Selector.SelectedItemProperty, new Binding("SelectedAssignee") { Mode = BindingMode.TwoWay });
        work.Children.Add(assignee);
        var reassign = Button("Reassign", null, false);
        reassign.HorizontalAlignment = HorizontalAlignment.Left;
        reassign.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("ReassignCommand"));
        reassign.SetBinding(IsEnabledProperty, new Binding("CanReassign"));
        work.Children.Add(reassign);

        work.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 12, 0, 12) });
        work.Children.Add(T("LINKED APPOINTMENT", 10, FontWeights.Bold, Purple));
        var appointment = T("", 12, FontWeights.SemiBold);
        appointment.TextWrapping = TextWrapping.Wrap;
        appointment.Margin = new Thickness(0, 4, 0, 7);
        appointment.SetBinding(TextBlock.TextProperty, new Binding("AppointmentContext"));
        work.Children.Add(appointment);
        var openAppointment = Button("Open in Schedule", null, false);
        openAppointment.HorizontalAlignment = HorizontalAlignment.Left;
        openAppointment.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("OpenLinkedAppointmentCommand") { Source = shell });
        openAppointment.SetBinding(IsEnabledProperty, new Binding("CanOpenAppointment"));
        work.Children.Add(openAppointment);

        work.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 12, 0, 12) });
        work.Children.Add(T("REPLY", 10, FontWeights.Bold, Purple));
        var localNote = T("", 10, null, Muted);
        localNote.TextWrapping = TextWrapping.Wrap;
        localNote.Margin = new Thickness(0, 3, 0, 7);
        localNote.SetBinding(TextBlock.TextProperty, new Binding("LocalDeliveryNote"));
        work.Children.Add(localNote);
        var startReply = Button("Write reply", null, false);
        startReply.HorizontalAlignment = HorizontalAlignment.Left;
        startReply.Margin = new Thickness(0, 0, 0, 7);
        startReply.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("StartReplyCommand"));
        startReply.SetBinding(IsEnabledProperty, new Binding("Composer.IsEditable"));
        work.Children.Add(startReply);
        var composer = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        composer.SetBinding(TextBox.TextProperty, new Binding("Composer.Text") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        composer.SetBinding(IsEnabledProperty, new Binding("Composer.IsEditable"));
        AutomationProperties.SetName(composer, "Unsent reply draft");
        work.Children.Add(composer);
        var draftStatus = T("", 10, null, Muted);
        draftStatus.TextWrapping = TextWrapping.Wrap;
        draftStatus.Margin = new Thickness(0, 4, 0, 0);
        draftStatus.SetBinding(TextBlock.TextProperty, new Binding("Composer.Status"));
        work.Children.Add(draftStatus);
        var draftError = T("", 10, FontWeights.SemiBold, Error);
        draftError.TextWrapping = TextWrapping.Wrap;
        draftError.SetBinding(TextBlock.TextProperty, new Binding("Composer.ErrorText"));
        work.Children.Add(draftError);
        var recordActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var waiting = Button("Record reply · waiting", null, false);
        waiting.Margin = new Thickness(0, 0, 7, 7);
        waiting.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RecordWaitingOnPatientCommand"));
        waiting.SetBinding(IsEnabledProperty, new Binding("Composer.CanRecord"));
        recordActions.Children.Add(waiting);
        var complete = Button("Record reply and complete", Purple, true);
        complete.Margin = new Thickness(0, 0, 0, 7);
        complete.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding("RecordAndCompleteCommand"));
        complete.SetBinding(IsEnabledProperty, new Binding("Composer.CanRecord"));
        recordActions.Children.Add(complete);
        work.Children.Add(recordActions);
        return body;
    }

    private static DataTemplate ConversationTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var top = new FrameworkElementFactory(typeof(Grid));
        var patient = new FrameworkElementFactory(typeof(TextBlock));
        patient.SetBinding(TextBlock.TextProperty, new Binding("Patient"));
        patient.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        top.AppendChild(patient);
        var activity = new FrameworkElementFactory(typeof(TextBlock));
        activity.SetBinding(TextBlock.TextProperty, new Binding("Activity"));
        activity.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        activity.SetValue(TextBlock.ForegroundProperty, Muted);
        activity.SetValue(TextBlock.FontSizeProperty, 10d);
        top.AppendChild(activity);
        stack.AppendChild(top);
        var preview = new FrameworkElementFactory(typeof(TextBlock));
        preview.SetBinding(TextBlock.TextProperty, new Binding("Preview"));
        preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        preview.SetValue(TextBlock.ForegroundProperty, Muted);
        preview.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 4));
        stack.AppendChild(preview);
        var footer = new FrameworkElementFactory(typeof(StackPanel));
        footer.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var unread = new FrameworkElementFactory(typeof(TextBlock));
        unread.SetValue(TextBlock.TextProperty, "●");
        unread.SetValue(TextBlock.ForegroundProperty, Brush("#A52370"));
        unread.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 5, 0));
        unread.SetBinding(VisibilityProperty, new Binding("IsUnread") { Converter = new BooleanVisibilityConverter() });
        footer.AppendChild(unread);
        var status = new FrameworkElementFactory(typeof(TextBlock));
        status.SetBinding(TextBlock.TextProperty, new Binding("Status"));
        status.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        status.SetValue(TextBlock.FontSizeProperty, 10d);
        footer.AppendChild(status);
        var owner = new FrameworkElementFactory(typeof(TextBlock));
        owner.SetBinding(TextBlock.TextProperty, new Binding("Owner") { StringFormat = " · {0}" });
        owner.SetValue(TextBlock.ForegroundProperty, Muted);
        owner.SetValue(TextBlock.FontSizeProperty, 10d);
        footer.AppendChild(owner);
        stack.AppendChild(footer);
        return new DataTemplate { VisualTree = stack };
    }

    private static DataTemplate MessageTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));
        border.SetValue(Border.MarginProperty, new Thickness(0, 3, 0, 3));
        border.SetValue(FrameworkElement.MaxWidthProperty, 390d);
        border.SetBinding(Border.BackgroundProperty, new Binding("IsIncoming") { Converter = new IncomingMessageBrushConverter() });
        border.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding("IsIncoming") { Converter = new IncomingMessageAlignmentConverter() });
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var author = new FrameworkElementFactory(typeof(TextBlock));
        author.SetBinding(TextBlock.TextProperty, new Binding("Author"));
        author.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        author.SetValue(TextBlock.FontSizeProperty, 10d);
        stack.AppendChild(author);
        var body = new FrameworkElementFactory(typeof(TextBlock));
        body.SetBinding(TextBlock.TextProperty, new Binding("Body"));
        body.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        body.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 4));
        stack.AppendChild(body);
        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new MultiBinding { StringFormat = "{0} · {1}", Bindings = { new Binding("Timestamp"), new Binding("RecordStatus") } });
        meta.SetValue(TextBlock.ForegroundProperty, Muted);
        meta.SetValue(TextBlock.FontSizeProperty, 9d);
        meta.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        stack.AppendChild(meta);
        border.AppendChild(stack);
        return new DataTemplate { VisualTree = border };
    }
}

public sealed class IncomingMessageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? MainWindowMessageBrushes.Incoming : MainWindowMessageBrushes.Outgoing;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}

public sealed class IncomingMessageAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}

internal static class MainWindowMessageBrushes
{
    public static Brush Incoming { get; } = new BrushConverter().ConvertFromString("#F1E8F8") as Brush ?? Brushes.Lavender;
    public static Brush Outgoing { get; } = new BrushConverter().ConvertFromString("#E8F4F2") as Brush ?? Brushes.Honeydew;
}
