using System.Windows;

namespace BeatzOnKeys.UI.Theming;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>Swaps Frost palette dictionaries so DynamicResource bindings recolor immediately.</summary>
public static class Theme
{
    private static readonly ResourceDictionary DarkDictionary = Load("Theme.Dark.xaml");
    private static readonly ResourceDictionary LightDictionary = Load("Theme.Light.xaml");
    private static ResourceDictionary? _active;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsLight => Current == AppTheme.Light;

    public static string CurrentCode => IsLight ? "light" : "dark";

    public static event Action? Changed;

    public static AppTheme Parse(string? value) =>
        string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    public static void Apply(AppTheme theme, bool notify = true)
    {
        var application = System.Windows.Application.Current;
        if (application is not null)
        {
            var next = theme == AppTheme.Light ? LightDictionary : DarkDictionary;
            if (!ReferenceEquals(next, _active))
            {
                if (_active is not null)
                {
                    application.Resources.MergedDictionaries.Remove(_active);
                }

                application.Resources.MergedDictionaries.Insert(0, next);
                _active = next;
            }
        }

        Current = theme;
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public static void Toggle() => Apply(IsLight ? AppTheme.Dark : AppTheme.Light);

    private static ResourceDictionary Load(string fileName)
    {
        var assembly = typeof(Theme).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Theming/{fileName}",
                UriKind.Absolute)
        };
    }
}
