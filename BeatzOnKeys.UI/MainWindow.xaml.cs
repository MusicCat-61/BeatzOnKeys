using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BeatzOnKeys.UI.Interop;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;
using BeatzOnKeys.UI.ViewModels;
using Microsoft.Win32;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BeatzOnKeys.UI;

public partial class MainWindow : Window
{
    private bool _micaApplied;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.FilePickerRequested += OpenFilePicker;
        viewModel.UserMessageRequested += ShowUserMessage;
        Theme.Changed += OnThemeChanged;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Kept for callers that force a real close; the App flag now drives the decision.</summary>
    public bool AllowClose { get; set; }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        _micaApplied = WindowBackdrop.TryApplyMica(this, !Theme.IsLight);
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!App.IsExplicitExit && !AllowClose)
        {
            // Closing hides to the tray, exactly like MusicCatRGB: playback and typing continue.
            eventArgs.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(eventArgs);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        ViewModel.FilePickerRequested -= OpenFilePicker;
        ViewModel.UserMessageRequested -= ShowUserMessage;
        Theme.Changed -= OnThemeChanged;
        base.OnClosed(eventArgs);
    }

    private void OpenFilePicker()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("L_FileDialogTitle"),
            Filter = Loc.T("L_FileDialogFilter"),
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.AddFiles(dialog.FileNames);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (eventArgs.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button may have been released between the event and DragMove.
            }
        }
    }

    private void MinimizeClick(object sender, RoutedEventArgs eventArgs) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs eventArgs) => ToggleMaximize();

    private void Window_StateChanged(object? sender, EventArgs eventArgs)
    {
        if (MaximizeIcon is not null)
        {
            MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    private void CloseClick(object sender, RoutedEventArgs eventArgs) => HideToTray();

    private void AboutClick(object sender, RoutedEventArgs eventArgs) =>
        ((App)System.Windows.Application.Current).ShowAboutWindow();

    private void GuideClick(object sender, RoutedEventArgs eventArgs) =>
        ((App)System.Windows.Application.Current).ShowGuideWindow();

    /// <summary>A chip expands its section and scrolls it into view, so nothing stays hidden.</summary>
    private void VolumeChipClick(object sender, RoutedEventArgs eventArgs) => ShowSection(MainViewModel.SectionVolume, VolumeSection);

    private void ReactionChipClick(object sender, RoutedEventArgs eventArgs) => ShowSection(MainViewModel.SectionReaction, ReactionSection);

    private void BehaviorChipClick(object sender, RoutedEventArgs eventArgs) => ShowSection(MainViewModel.SectionBehavior, BehaviorSection);

    private void ShowSection(string section, FrameworkElement target)
    {
        if (DataContext is not MainViewModel viewModel) { return; }
        viewModel.ShowSection(section);
        target.BringIntoView();
    }

    private void VolumeHeaderClick(object sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainViewModel)?.ToggleSection(MainViewModel.SectionVolume);

    private void ReactionHeaderClick(object sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainViewModel)?.ToggleSection(MainViewModel.SectionReaction);

    private void BehaviorHeaderClick(object sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainViewModel)?.ToggleSection(MainViewModel.SectionBehavior);

    /// <summary>F1 opens the reference without disturbing playback or the playlist.</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.F1)
        {
            return;
        }

        eventArgs.Handled = true;
        GuideClick(sender, eventArgs);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void HideToTray() => ((App)System.Windows.Application.Current).HideMainWindow();

    private async void ResetClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.CanReset)
        {
            return;
        }

        var answer = MessageDialog.Ask(this, Loc.T("L_ResetMessage"), Loc.T("L_ResetTitle"));
        if (answer)
        {
            await ViewModel.ResetAsync().ConfigureAwait(true);
        }
    }

    private async void ClearPlaylistClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.CanClearPlaylist)
        {
            return;
        }

        var answer = MessageDialog.Ask(this, Loc.T("L_ClearPlaylistMessage"), Loc.T("L_ClearPlaylistTitle"));
        if (answer)
        {
            await ViewModel.ClearPlaylistAsync().ConfigureAwait(true);
        }
    }

    private void DropBorder_DragEnter(object sender, DragEventArgs eventArgs) => UpdateDropState(eventArgs, active: true);

    private void DropBorder_DragOver(object sender, DragEventArgs eventArgs) => UpdateDropState(eventArgs, active: true);

    private void DropBorder_DragLeave(object sender, DragEventArgs eventArgs)
    {
        RestoreDropState();
        eventArgs.Handled = true;
    }

    private void DropBorder_Drop(object sender, DragEventArgs eventArgs)
    {
        RestoreDropState();
        if (eventArgs.Data.GetDataPresent(DataFormats.FileDrop) &&
            eventArgs.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ViewModel.AddFiles(files);
            eventArgs.Effects = DragDropEffects.Copy;
        }
        else
        {
            eventArgs.Effects = DragDropEffects.None;
        }

        eventArgs.Handled = true;
    }

    private void UpdateDropState(DragEventArgs eventArgs, bool active)
    {
        var acceptsFiles = eventArgs.Data.GetDataPresent(DataFormats.FileDrop);
        eventArgs.Effects = acceptsFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (acceptsFiles && active)
        {
            DropBorder.BorderThickness = new Thickness(2);
            DropBorder.Opacity = 0.86;
        }

        eventArgs.Handled = true;
    }

    private void RestoreDropState()
    {
        DropBorder.BorderThickness = new Thickness(1);
        DropBorder.Opacity = 1;
        DropBorder.SetResourceReference(Border.BackgroundProperty, "DropAreaBrush");
        DropBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
    }

    private void ShowUserMessage(string message)
    {
        MessageDialog.Notify(this, message, Loc.T("L_AppName"));
    }

    private void OnThemeChanged()
    {
        WindowBackdrop.SetDarkMode(this, !Theme.IsLight);
        if (!_micaApplied)
        {
            SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        }
    }
}
