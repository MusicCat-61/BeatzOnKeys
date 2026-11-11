using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
// WinForms is referenced for the tray, so System.Drawing is in scope and Brush is ambiguous.
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using BeatzOnKeys.Core.Updates;
using BeatzOnKeys.UI.Localization;

namespace BeatzOnKeys.UI;

/// <summary>
/// A dialog drawn with the application's own theme, so confirmations and notices match the
/// window instead of switching to the stock Windows chrome (this mirrors MusicCatRGB).
/// </summary>
public partial class MessageDialog : Window
{
    private bool _result;

    private MessageDialog(string message, string title, bool showYesNo)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        if (showYesNo)
        {
            BtnYes.Content = Loc.T("L_Yes");
            BtnNo.Content = Loc.T("L_No");
            BtnNo.Visibility = Visibility.Visible;
        }
        else
        {
            BtnYes.Content = Loc.T("L_Ok");
            BtnNo.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Asks a yes/no question; returns true only for the confirming button.</summary>
    public static bool Ask(Window? owner, string message, string title) =>
        Ask(owner, message, title, null, null);

    /// <summary>
    /// The same question with labels of its own. A dialog that asks for a folder needs a button
    /// that says so, not a generic "Yes".
    /// </summary>
    public static bool Ask(Window? owner, string message, string title, string? yesLabel, string? noLabel)
    {
        var dialog = new MessageDialog(message, title, showYesNo: true);
        if (!string.IsNullOrWhiteSpace(yesLabel)) { dialog.BtnYes.Content = yesLabel; }
        if (!string.IsNullOrWhiteSpace(noLabel)) { dialog.BtnNo.Content = noLabel; }
        dialog.Run(owner);
        return dialog._result;
    }

    /// <summary>Shows a single-button notice.</summary>
    public static void Notify(Window? owner, string message, string title) =>
        Notify(owner, message, title, null);

    /// <summary>
    /// Replaced by the smoke harness so a modal window cannot block an automated run. Null in the
    /// shipped app, where the dialog itself is the behaviour. This mirrors
    /// <see cref="MainViewModel.TikTokServiceFactory"/>: a seam for tests, not a feature.
    /// </summary>
    public static Action<string, string>? NoticePresenter { get; set; }

    /// <summary>
    /// Shows a notice that does not block its caller. Startup hints and the tray explanation are
    /// information, not questions: a modal window there stopped the app from finishing its own
    /// startup until the user dismissed it, which the watchdog caught as a hang.
    /// </summary>
    public static void ShowNotice(Window? owner, string message, string title, string? okLabel)
    {
        if (NoticePresenter is { } presenter)
        {
            presenter(message, title);
            return;
        }

        var dialog = new MessageDialog(message, title, showYesNo: false);
        if (!string.IsNullOrWhiteSpace(okLabel)) { dialog.BtnYes.Content = okLabel; }
        if (owner is not null && owner.IsVisible) { dialog.Owner = owner; }
        else { dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        dialog.Show();
    }

    /// <summary>A single-button notice whose button says what happens next.</summary>
    public static void Notify(Window? owner, string message, string title, string? okLabel)
    {
        if (NoticePresenter is { } presenter)
        {
            presenter(message, title);
            return;
        }

        var dialog = new MessageDialog(message, title, showYesNo: false);
        if (!string.IsNullOrWhiteSpace(okLabel)) { dialog.BtnYes.Content = okLabel; }
        dialog.Run(owner);
    }

    /// <summary>
    /// An update notice with the release notes inside it. Returns true when the user wants the
    /// About window opened, which is where the download and install buttons live.
    /// </summary>
    public static bool Announce(Window? owner, string title, string message, string notes)
    {
        var dialog = CreateAnnouncement(title, message, notes);
        dialog.Run(owner);
        return dialog._result;
    }

    /// <summary>
    /// Builds the update notice without showing it, so a test can inspect and capture it. The
    /// notes are rendered here, which is also where the language is picked.
    /// </summary>
    public static MessageDialog CreateAnnouncement(string title, string message, string notes)
    {
        var dialog = new MessageDialog(message, title, showYesNo: true);
        dialog.BtnYes.Content = Loc.T("L_UpdOpen");
        dialog.BtnNo.Content = Loc.T("L_UpdLater");
        var blocks = ReleaseNotesFormatter.Parse(
            ReleaseNotesFormatter.SelectSection(notes, Loc.CurrentCode));
        if (blocks.Count > 0)
        {
            foreach (var block in blocks) { dialog.NotesPanel.Children.Add(dialog.CreateBlock(block)); }
            dialog.NotesHost.Visibility = Visibility.Visible;
        }

        return dialog;
    }

    /// <summary>
    /// One element per block. Markdown is rendered instead of printed: the dialog used to show
    /// "## English" and "**bold**" literally, which reads like a bug rather than a note.
    /// </summary>
    private FrameworkElement CreateBlock(NoteBlock block)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            FontSize = block.Kind is NoteBlockKind.Heading or NoteBlockKind.Subheading ? 14 : 13.5
        };

        switch (block.Kind)
        {
            case NoteBlockKind.Heading:
                text.FontWeight = FontWeights.SemiBold;
                text.Foreground = (Brush)FindResource("TextPrimaryBrush");
                text.Margin = new Thickness(0, 12, 0, 3);
                break;
            case NoteBlockKind.Subheading:
                text.FontWeight = FontWeights.SemiBold;
                text.Foreground = (Brush)FindResource("TextPrimaryBrush");
                text.Margin = new Thickness(0, 9, 0, 2);
                break;
            case NoteBlockKind.Bullet:
                text.Foreground = (Brush)FindResource("TextSecondaryBrush");
                text.Margin = new Thickness(0, 0, 0, 0);
                break;
            default:
                text.Foreground = (Brush)FindResource("TextSecondaryBrush");
                text.Margin = new Thickness(0, 7, 0, 0);
                break;
        }

        foreach (var run in block.Runs) { text.Inlines.Add(CreateRun(run)); }

        if (block.Kind != NoteBlockKind.Bullet) { return text; }

        // A bullet gets a marker of its own so the wrapped lines stay aligned with the text.
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var marker = new TextBlock
        {
            Text = "•",
            FontSize = 13.5,
            Margin = new Thickness(2, 0, 8, 0),
            Foreground = (Brush)FindResource("AccentBrush")
        };
        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        row.Children.Add(text);
        return row;
    }

    private Run CreateRun(NoteRun run)
    {
        var element = new Run(run.Text);
        if (run.Bold)
        {
            element.FontWeight = FontWeights.SemiBold;
            element.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }

        if (run.Code)
        {
            element.FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI");
            element.Foreground = (Brush)FindResource("AccentBrush");
        }

        return element;
    }


    private void Run(Window? owner)
    {
        // Only attach an owner that is really visible; otherwise centring would be off-screen.
        if (owner is not null && owner.IsVisible)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        ShowDialog();
    }

    private void BtnYes_Click(object sender, RoutedEventArgs eventArgs)
    {
        _result = true;
        Close();
    }

    private void BtnNo_Click(object sender, RoutedEventArgs eventArgs)
    {
        _result = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}