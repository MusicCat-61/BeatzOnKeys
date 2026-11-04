using System.Globalization;
using System.Windows;

namespace BeatzOnKeys.UI.Localization;

public enum AppLanguage
{
    En,
    Ru
}

/// <summary>Runtime EN/RU resource dictionary switching.</summary>
public static class Loc
{
    private static readonly ResourceDictionary EnglishDictionary = Load("Strings.en.xaml");
    private static readonly ResourceDictionary RussianDictionary = Load("Strings.ru.xaml");
    private static ResourceDictionary? _active;

    public static AppLanguage Current { get; private set; } = AppLanguage.En;

    public static string CurrentCode => Current == AppLanguage.Ru ? "ru" : "en";

    public static event Action? LanguageChanged;

    public static void Initialize(string? savedCode)
    {
        var language = string.IsNullOrWhiteSpace(savedCode) ? DetectSystemLanguage() : Parse(savedCode);
        SetLanguage(language, notify: false);
    }

    public static AppLanguage Parse(string? value) =>
        string.Equals(value, "ru", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Ru : AppLanguage.En;

    public static AppLanguage DetectSystemLanguage()
    {
        return IsRussian(CultureInfo.CurrentUICulture) ||
               IsRussian(CultureInfo.InstalledUICulture) ||
               IsRussian(CultureInfo.CurrentCulture)
            ? AppLanguage.Ru
            : AppLanguage.En;
    }

    public static void SetLanguage(AppLanguage language, bool notify = true)
    {
        var application = System.Windows.Application.Current;
        if (application is not null)
        {
            var next = language == AppLanguage.Ru ? RussianDictionary : EnglishDictionary;
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

        Current = language;
        var culture = CultureInfo.GetCultureInfo(language == AppLanguage.Ru ? "ru-RU" : "en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    public static string T(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(key), arguments);

    private static bool IsRussian(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    private static ResourceDictionary Load(string fileName)
    {
        var assembly = typeof(Loc).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Localization/{fileName}",
                UriKind.Absolute)
        };
    }
}
