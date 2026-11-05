using System.Windows;
using System.Windows.Input;
using BeatzOnKeys.UI.Localization;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace BeatzOnKeys.UI;

/// <summary>
/// The in-app reference: every button, slider, switch, tray action, status, and path, in plain
/// words. The content is rebuilt from the localization dictionaries, so switching the language
/// updates the whole guide immediately.
/// </summary>
public partial class GuideWindow : Window
{
    public GuideWindow()
    {
        InitializeComponent();
        Loc.LanguageChanged += Rebuild;
        Rebuild();
    }

    private void Rebuild() => SectionsHost.ItemsSource = BuildSections();

    private static IReadOnlyList<GuideSection> BuildSections() =>
    [
        Section("Guide_SecQuick",
            ("Guide_Q1T", "Guide_Q1X"),
            ("Guide_Q2T", "Guide_Q2X"),
            ("Guide_Q3T", "Guide_Q3X"),
            ("Guide_Q4T", "Guide_Q4X"),
            ("Guide_Q5T", "Guide_Q5X"),
            ("Guide_Q6T", "Guide_Q6X")),

        Section("Guide_SecControls",
            ("Guide_StartT", "Guide_StartX"),
            ("Guide_StopT", "Guide_StopX"),
            ("Guide_ResetT", "Guide_ResetX"),
            ("Guide_AddT", "Guide_AddX"),
            ("Guide_RemoveT", "Guide_RemoveX"),
            ("Guide_GuideT", "Guide_GuideX"),
            ("Guide_LogsT", "Guide_LogsX"),
            ("Guide_AboutT", "Guide_AboutX"),
            ("Guide_LangT", "Guide_LangX"),
            ("Guide_ThemeT", "Guide_ThemeX"),
            ("Guide_WinT", "Guide_WinX")),

        Section("Guide_SecSliders",
            ("Guide_MaxT", "Guide_MaxX"),
            ("Guide_MinT", "Guide_MinX"),
            ("Guide_IdleT", "Guide_IdleX"),
            ("Guide_ProgT", "Guide_ProgX"),
            ("Guide_SysVolT", "Guide_SysVolX")),

        Section("Guide_SecSwitches",
            ("Guide_PauseT", "Guide_PauseX"),
            ("Guide_AutoT", "Guide_AutoX"),
            ("Guide_LoopT", "Guide_LoopX"),
            ("Guide_MatrixT", "Guide_MatrixX")),

        Section("Guide_SecTray",
            ("Guide_TrayIconT", "Guide_TrayIconX"),
            ("Guide_TrayMenuT", "Guide_TrayMenuX"),
            ("Guide_TrayLaunchT", "Guide_TrayLaunchX")),

        Section("Guide_SecStatus",
            ("Guide_StatusT", "Guide_StatusX")),

        Section("Guide_SecKeys",
            ("Guide_KeysT", "Guide_KeysX")),

        Section("Guide_SecPrivacy",
            ("Guide_PrivacyT", "Guide_PrivacyX")),

        Section("Guide_SecTrouble",
            ("Guide_NoSoundT", "Guide_NoSoundX"),
            ("Guide_QuietT", "Guide_QuietX"),
            ("Guide_PausesT", "Guide_PausesX"),
            ("Guide_HiddenT", "Guide_HiddenX"),
            ("Guide_EndT", "Guide_EndX"),
            ("Guide_NotSavedT", "Guide_NotSavedX")),

        Section("Guide_SecFiles",
            ("Guide_FilesT", "Guide_FilesX"))
    ];

    private static GuideSection Section(string titleKey, params (string TitleKey, string TextKey)[] items) =>
        new(Loc.T(titleKey),
            items.Select(item => new GuideItem(Loc.T(item.TitleKey), Loc.T(item.TextKey))).ToArray());

    private void Close_Click(object sender, RoutedEventArgs eventArgs) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        Loc.LanguageChanged -= Rebuild;
        base.OnClosed(eventArgs);
    }

    private sealed record GuideSection(string Title, IReadOnlyList<GuideItem> Items);

    private sealed record GuideItem(string Title, string Text);
}