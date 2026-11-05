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