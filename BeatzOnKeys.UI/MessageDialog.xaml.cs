using System.Windows;
using System.Windows.Input;
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
    public static bool Ask(Window? owner, string message, string title)
    {
        var dialog = new MessageDialog(message, title, showYesNo: true);
        dialog.Run(owner);
        return dialog._result;
    }

    /// <summary>Shows a single-button notice.</summary>
    public static void Notify(Window? owner, string message, string title)
    {
        var dialog = new MessageDialog(message, title, showYesNo: false);
        dialog.Run(owner);
    }

    /// <summary>
    /// An update notice with the release notes inside it. Returns true when the user wants the
    /// About window opened, which is where the download and install buttons live.
    /// </summary>
    public static bool Announce(Window? owner, string title, string message, string notes)
    {
        var dialog = new MessageDialog(message, title, showYesNo: true);
        dialog.BtnYes.Content = Loc.T("L_UpdOpen");
        dialog.BtnNo.Content = Loc.T("L_UpdLater");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            dialog.NotesText.Text = TrimNotes(notes);
            dialog.NotesHost.Visibility = Visibility.Visible;
        }

        dialog.Run(owner);
        return dialog._result;
    }

    /// <summary>
    /// The release body is Markdown with asset tables; the dialog shows the prose only, because a
    /// table of download links reads badly in a small window.
    /// </summary>
    private static string TrimNotes(string notes)
    {
        var kept = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('|')) { continue; }
            if (line.StartsWith("```", StringComparison.Ordinal)) { continue; }
            kept.Add(line.Replace("**", string.Empty).Replace("`", string.Empty));
        }

        var text = string.Join('\n', kept).Trim();
        return text.Length > 2000 ? text[..2000] + "…" : text;
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
