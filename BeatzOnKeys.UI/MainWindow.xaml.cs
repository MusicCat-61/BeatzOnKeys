using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BeatzOnKeys.Core.Services;
using BeatzOnKeys.UI.Interop;
using BeatzOnKeys.UI.Localization;
using BeatzOnKeys.UI.Theming;
using BeatzOnKeys.UI.ViewModels;
using Microsoft.Win32;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BeatzOnKeys.UI;

public partial class MainWindow : Window
{
    private bool _micaApplied;

    /// <summary>Recording starts after the first layout: the startup size is not a user choice.</summary>
    private bool _sizeTracking;

    /// <summary>The height the settings panel fitted for itself, so it is not remembered as a choice.</summary>
    private double _contentFittedHeight = double.NaN;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.FilePickerRequested += OpenFilePicker;
        viewModel.UserMessageRequested += ShowUserMessage;
        // The view model knows which button deletes what; the window owns the dialog, so both
        // clear buttons ask the same question in the same place.
        viewModel.YouTube.ConfirmRequested =
            (message, title) => MessageDialog.Ask(this, message, title);
        Theme.Changed += OnThemeChanged;
        // The remembered size is applied before the window is ever shown, so it cannot appear at one
        // size and jump to another a moment later.
        ApplySavedSize();
        Loaded += (_, _) =>
        {
            FitToContent();
            _sizeTracking = true;
        };
        SizeChanged += MainWindow_SizeChanged;
        viewModel.FitRequested += FitToContent;
    }

    /// <summary>
    /// Restores the size the user last left. Only the size is remembered, never the position: the
    /// window still opens in the middle of the screen, and the work area wins over a stale value
    /// remembered on a larger monitor.
    /// </summary>
    private void ApplySavedSize()
    {
        if (ViewModel.WindowWidth <= 0 || ViewModel.WindowHeight <= 0) { return; }
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(ViewModel.WindowWidth, MinWidth, Math.Max(MinWidth, workArea.Width));
        Height = Math.Clamp(ViewModel.WindowHeight, MinHeight, Math.Max(MinHeight, workArea.Height));
    }

    /// <summary>
    /// Only a normal window has a size the user chose: a maximised window reports the screen and a
    /// minimised one a strip, and neither is worth remembering. The height this window gave itself
    /// to fit the settings panel is skipped for the same reason.
    /// </summary>
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        if (!_sizeTracking || WindowState != WindowState.Normal) { return; }
        // A height that matches the one the settings panel asked for is the window growing itself,
        // not a decision of the user. The mark is cleared either way, so a stale value can never
        // swallow a later real resize.
        var fitted = !double.IsNaN(_contentFittedHeight) && Math.Abs(Height - _contentFittedHeight) < 1;
        _contentFittedHeight = double.NaN;
        if (fitted) { return; }

        ViewModel.RememberWindowSize(ActualWidth, ActualHeight);
    }

    /// <summary>
    /// Grows the window to whatever the settings column needs at the current font metrics, so a
    /// larger system font never arrives half-cut behind a scrollbar. The size is clamped to the
    /// work area and never shrinks below the XAML minimum; the user can still resize afterwards.
    /// </summary>
    private void FitToContent()
    {
        if (WindowState != WindowState.Normal) { return; }
        // A collapsed page measures to zero, so fitting only means something while it is shown;
        // the XAML default height already covers the panel and the page scrolls beyond that.
        if (ViewModel?.IsSettingsOpen != true) { return; }
        var content = SettingsScroller.Content as FrameworkElement;
        if (content is null) { return; }

        // Measure the settings card at the width it actually gets, without the scroll viewer.
        // The settings page stays collapsed until the user opens it, so its scroll viewer has no
        // width at startup; measuring with that NaN throws, hence the fallbacks below.
        var available = new Size(SettingsScroller.ActualWidth, double.PositiveInfinity);
        if (double.IsNaN(available.Width) || double.IsInfinity(available.Width) || available.Width <= 0)
        {
            available.Width = SettingsScroller.Width;
        }

        if (double.IsNaN(available.Width) || double.IsInfinity(available.Width) || available.Width <= 0)
        {
            // The card caps itself at 560, so that is the honest fallback.
            available.Width = 560;
        }
        content.Measure(available);
        var neededContent = content.DesiredSize.Height;

        // Header + body margins + footer, matching the three row definitions in XAML.
        const double chrome = 56 + 58 + 12;
        var workArea = SystemParameters.WorkArea;
        var wanted = Math.Clamp(neededContent + chrome, MinHeight, Math.Max(MinHeight, workArea.Height * 0.92));

        // Only ever grow: opening the panel must not shrink a window the user already sized, and
        // the panel scrolls on its own when the window is shorter than its content.
        if (wanted <= Height + 1) { return; }
        Height = Math.Round(wanted);
        // Remember which height the panel asked for, so the resize handler does not mistake the
        // window's own growth for a size the user chose.
        _contentFittedHeight = Height;
        AppLogger.Info("ui", $"Window fitted to content: settings need {neededContent:F0} px, height set to {Height:F0} px.");
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

    // ---- YouTube list drag-and-drop ---------------------------------------------------------
    // The same model as the playlist: one drag format, an insertion line, and moves that go through
    // the view model so the persisted order follows.

    private const string YouTubeDragFormat = "BeatzOnKeys.YouTubeTrack";

    private Point _youTubeDragOrigin;
    private YouTubeTrackViewModel? _youTubeDragCandidate;
    private bool _youTubeDragging;

    private void YouTubeList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        _youTubeDragging = false;
        _youTubeDragOrigin = eventArgs.GetPosition(this);
        _youTubeDragCandidate = YouTubeRowAt(eventArgs.OriginalSource as DependencyObject);
    }

    private void YouTubeList_PreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || _youTubeDragCandidate is null || _youTubeDragging) { return; }
        var moved = eventArgs.GetPosition(this) - _youTubeDragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var track = _youTubeDragCandidate;
        _youTubeDragCandidate = null;
        _youTubeDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(YouTubeDragFormat, track), DragDropEffects.Move);
        }
        finally
        {
            ViewModel.YouTube.SetDropTarget(null, above: true);
            _youTubeDragging = false;
        }
    }

    private void YouTubeList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var candidate = _youTubeDragCandidate;
        var dragged = _youTubeDragging;
        _youTubeDragCandidate = null;
        _youTubeDragging = false;

        // A click that did not turn into a drag plays the row, exactly like the local playlist.
        // Buttons inside the row handle their own clicks, so the arrows and the bin are unaffected.
        if (dragged || candidate is null || candidate is { CanPlay: false }) { return; }
        if (eventArgs.OriginalSource is DependencyObject source && IsInsideButton(source)) { return; }
        ViewModel.YouTube.SelectTrack(candidate);
        ViewModel.YouTube.PlayCommand.Execute(candidate);
    }

    /// <summary>True when the click came from a button, so it must not also select and play.</summary>
    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) { return true; }
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void YouTubeList_DragOver(object sender, DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.GetDataPresent(YouTubeDragFormat))
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        var row = YouTubeRowAt(eventArgs.OriginalSource as DependencyObject);
        if (row is null)
        {
            ViewModel.YouTube.SetDropTarget(null, above: true);
        }
        else
        {
            var container = ContainerAt(eventArgs.OriginalSource as DependencyObject);
            var above = container is null || eventArgs.GetPosition(container).Y < container.ActualHeight / 2;
            ViewModel.YouTube.SetDropTarget(row, above);
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    private void YouTubeList_DragLeave(object sender, DragEventArgs eventArgs) =>
        ViewModel.YouTube.SetDropTarget(null, above: true);

    private void YouTubeList_Drop(object sender, DragEventArgs eventArgs)
    {
        ViewModel.YouTube.SetDropTarget(null, above: true);
        if (!eventArgs.Data.GetDataPresent(YouTubeDragFormat) ||
            eventArgs.Data.GetData(YouTubeDragFormat) is not YouTubeTrackViewModel dragged)
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        var list = ViewModel.YouTube.Downloaded;
        var row = YouTubeRowAt(eventArgs.OriginalSource as DependencyObject);
        var target = row ?? list.LastOrDefault();
        if (target is not null)
        {
            var from = list.IndexOf(dragged);
            var targetIndex = list.IndexOf(target);
            var above = row is not null &&
                        ContainerAt(eventArgs.OriginalSource as DependencyObject) is { } container &&
                        eventArgs.GetPosition(container).Y < container.ActualHeight / 2;
            if (row is not null && !above) { targetIndex++; }
            if (from >= 0 && from < targetIndex) { targetIndex--; }
            ViewModel.YouTube.MoveTrackTo(dragged, Math.Clamp(targetIndex, 0, list.Count - 1));
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    // --- Клик по строке запускает трек: список TikTok, как и список скачанных YouTube, ---
    // --- не держит кнопку прослушивания внутри строки.                                  ---

    private YouTubeTrackViewModel? _tikTokClickCandidate;

    private void TikTokList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        _tikTokDragging = false;
        _tikTokDragOrigin = eventArgs.GetPosition(this);
        _tikTokClickCandidate = TikTokRowAt(eventArgs.OriginalSource as DependencyObject);
        _tikTokDragCandidate = _tikTokClickCandidate;
    }

    private void TikTokList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var candidate = _tikTokClickCandidate;
        var dragged = _tikTokDragging;
        _tikTokClickCandidate = null;
        _tikTokDragCandidate = null;

        // Кнопки внутри строки — карандаш, стрелки и корзина — обрабатывают свой клик сами, иначе
        // нажатие на корзину заодно запускало бы трек. Перетаскивание тоже не играет строку.
        if (dragged || candidate is null || candidate is { CanPlay: false }) { return; }
        if (eventArgs.OriginalSource is DependencyObject source && IsInsideButton(source)) { return; }
        ViewModel.TikTok.SelectTrack(candidate);
        ViewModel.TikTok.PlayCommand.Execute(candidate);
    }

    // ---- TikTok list drag-and-drop ------------------------------------------------------------
    // The same model as the local playlist and the downloaded YouTube list: one drag format, an
    // insertion line, and moves that go through the view model so the saved order follows.

    private const string TikTokDragFormat = "BeatzOnKeys.TikTokTrack";

    private Point _tikTokDragOrigin;
    private YouTubeTrackViewModel? _tikTokDragCandidate;
    private bool _tikTokDragging;

    private void TikTokList_PreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || _tikTokDragCandidate is null || _tikTokDragging) { return; }
        var moved = eventArgs.GetPosition(this) - _tikTokDragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var track = _tikTokDragCandidate;
        _tikTokDragCandidate = null;
        // A row being renamed keeps its pointer: dragging it away mid-edit would throw the typing out.
        if (track.IsEditing) { return; }
        _tikTokDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TikTokDragFormat, track), DragDropEffects.Move);
        }
        finally
        {
            ViewModel.TikTok.SetDropTarget(null, above: true);
            _tikTokDragging = false;
        }
    }

    private void TikTokList_DragOver(object sender, DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.GetDataPresent(TikTokDragFormat))
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        var row = TikTokRowAt(eventArgs.OriginalSource as DependencyObject);
        if (row is null)
        {
            ViewModel.TikTok.SetDropTarget(null, above: true);
        }
        else
        {
            var container = ContainerAt(eventArgs.OriginalSource as DependencyObject);
            var above = container is null || eventArgs.GetPosition(container).Y < container.ActualHeight / 2;
            ViewModel.TikTok.SetDropTarget(row, above);
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    private void TikTokList_DragLeave(object sender, DragEventArgs eventArgs) =>
        ViewModel.TikTok.SetDropTarget(null, above: true);

    private void TikTokList_Drop(object sender, DragEventArgs eventArgs)
    {
        ViewModel.TikTok.SetDropTarget(null, above: true);
        if (!eventArgs.Data.GetDataPresent(TikTokDragFormat) ||
            eventArgs.Data.GetData(TikTokDragFormat) is not YouTubeTrackViewModel dragged)
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        // Indexes come from the real collection, not from the filtered view: the row object is the
        // same one, so a filtered list still moves the track to the place the user pointed at.
        var list = ViewModel.TikTok.Tracks;
        var row = TikTokRowAt(eventArgs.OriginalSource as DependencyObject);
        var target = row ?? list.LastOrDefault();
        if (target is not null)
        {
            var from = list.IndexOf(dragged);
            var targetIndex = list.IndexOf(target);
            var above = row is not null &&
                        ContainerAt(eventArgs.OriginalSource as DependencyObject) is { } container &&
                        eventArgs.GetPosition(container).Y < container.ActualHeight / 2;
            if (row is not null && !above) { targetIndex++; }
            if (from >= 0 && from < targetIndex) { targetIndex--; }
            ViewModel.TikTok.MoveTrackTo(dragged, Math.Clamp(targetIndex, 0, list.Count - 1));
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    // ---- Renaming a TikTok row ----------------------------------------------------------------

    /// <summary>The pencil opens the field; the pointer goes straight into it, ready to type.</summary>
    private void TikTokEditClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: YouTubeTrackViewModel row }) { return; }
        row.BeginEdit();
        var box = ContainerAt(sender as DependencyObject) is { } container
            ? DescendantsOfType<System.Windows.Controls.TextBox>(container).FirstOrDefault(candidate => candidate.Name == "RenameBox")
            : null;
        if (box is null) { return; }
        box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>Enter keeps the name, Escape gives the old one back; both close the field.</summary>
    private void RenameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: YouTubeTrackViewModel row }) { return; }
        if (eventArgs.Key == Key.Enter)
        {
            eventArgs.Handled = true;
            row.CommitEdit();
        }
        else if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            row.CancelEdit();
        }
    }

    /// <summary>A click elsewhere is a decision too, so it keeps what was typed.</summary>
    private void RenameBox_LostFocus(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { DataContext: YouTubeTrackViewModel row } && row.IsEditing) { row.CommitEdit(); }
    }

    private static YouTubeTrackViewModel? TikTokRowAt(DependencyObject? source) =>
        ContainerAt(source)?.DataContext as YouTubeTrackViewModel;

    private static YouTubeTrackViewModel? YouTubeRowAt(DependencyObject? source) =>
        ContainerAt(source)?.DataContext as YouTubeTrackViewModel;

    /// <summary>
    /// The row container under the pointer. Taken from the visual tree rather than from a list
    /// lookup, because the lists now show a filtered copy and an item index would mean nothing.
    /// </summary>
    private static ListBoxItem? ContainerAt(DependencyObject? source)
    {
        var element = source;
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }

    private static IEnumerable<T> DescendantsOfType<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { yield return match; }
            foreach (var nested in DescendantsOfType<T>(child)) { yield return nested; }
        }
    }
    private void ShowUserMessage(string message)
    {
        MessageDialog.Notify(this, message, Loc.T("L_AppName"));
    }

    // ---- Playlist drag-and-drop -------------------------------------------------------------
    // A row is dragged with the left button held; the target row shows a line where the drop will
    // land. The move goes through the view model, so playback order follows the new layout and the
    // playing track is never restarted.

    private const string PlaylistDragFormat = "BeatzOnKeys.PlaylistTrack";

    private Point _dragOrigin;
    private TrackViewModel? _dragCandidate;
    private bool _dragging;

    private void Playlist_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        _dragging = false;
        _dragOrigin = eventArgs.GetPosition(this);
        _dragCandidate = RowAt(eventArgs.OriginalSource as DependencyObject);
    }

    private void Playlist_PreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || _dragging) { return; }
        var moved = eventArgs.GetPosition(this) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var track = _dragCandidate;
        _dragCandidate = null;
        if (!ViewModel.CanEditPlaylist) { return; }
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(PlaylistDragFormat, track), DragDropEffects.Move);
        }
        finally
        {
            ViewModel.SetDropTarget(null, above: true);
            _dragging = false;
        }
    }

    private void Playlist_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var candidate = _dragCandidate;
        var dragged = _dragging;
        _dragCandidate = null;
        _dragging = false;

        // A click that did not turn into a drag plays the row, exactly like the YouTube and TikTok
        // lists. Buttons inside the row handle their own clicks, so the arrows and the bin are
        // unaffected.
        if (dragged || candidate is null || candidate is { IsAvailable: false }) { return; }
        if (eventArgs.OriginalSource is DependencyObject source && IsInsideButton(source)) { return; }
        _ = ViewModel.PlayPlaylistTrackAsync(candidate);
    }

    private void Playlist_DragOver(object sender, DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.GetDataPresent(PlaylistDragFormat) || !ViewModel.CanEditPlaylist)
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        var row = RowAt(eventArgs.OriginalSource as DependencyObject);
        if (row is null)
        {
            ViewModel.SetDropTarget(null, above: true);
        }
        else
        {
            ViewModel.SetDropTarget(row, IsAbove(row, eventArgs));
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    private void Playlist_DragLeave(object sender, DragEventArgs eventArgs) =>
        ViewModel.SetDropTarget(null, above: true);

    private void Playlist_Drop(object sender, DragEventArgs eventArgs)
    {
        ViewModel.SetDropTarget(null, above: true);
        if (!eventArgs.Data.GetDataPresent(PlaylistDragFormat) ||
            eventArgs.Data.GetData(PlaylistDragFormat) is not TrackViewModel dragged)
        {
            eventArgs.Effects = DragDropEffects.None;
            eventArgs.Handled = true;
            return;
        }

        var row = RowAt(eventArgs.OriginalSource as DependencyObject);
        var last = ViewModel.Tracks.LastOrDefault();
        var target = row ?? last;
        if (target is not null)
        {
            var from = ViewModel.Tracks.IndexOf(dragged);
            var targetIndex = ViewModel.Tracks.IndexOf(target);
            // Dropping on empty space means "to the end"; the upper half inserts before the row.
            var above = row is not null && IsAbove(row, eventArgs);
            if (row is not null && !above) { targetIndex++; }
            if (from >= 0 && from < targetIndex) { targetIndex--; }
            ViewModel.MoveTrackTo(dragged, Math.Clamp(targetIndex, 0, ViewModel.Tracks.Count - 1));
        }

        eventArgs.Effects = DragDropEffects.Move;
        eventArgs.Handled = true;
    }

    /// <summary>True when the pointer sits in the upper half of the row, meaning "insert before".</summary>
    private bool IsAbove(TrackViewModel row, DragEventArgs eventArgs)
    {
        var container = ContainerOf(row);
        return container is null || eventArgs.GetPosition(container).Y < container.ActualHeight / 2;
    }

    private static TrackViewModel? RowAt(DependencyObject? source)
    {
        var element = source;
        while (element is not null and not ListBoxItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return (element as ListBoxItem)?.DataContext as TrackViewModel;
    }

    private ListBoxItem? ContainerOf(TrackViewModel track) =>
        FindDescendant<ListBox>(this)?.ItemContainerGenerator.ContainerFromItem(track) as ListBoxItem;

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { return match; }
            var nested = FindDescendant<T>(child);
            if (nested is not null) { return nested; }
        }

        return null;
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
