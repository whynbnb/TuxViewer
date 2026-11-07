using System.Globalization;
using Avalonia;

namespace TuxViewer.Services;

/// <summary>
/// Runtime UI language switch. Static XAML strings are looked up with
/// <c>DynamicResource</c>; strings generated in code use <see cref="L"/> / <see cref="F"/>.
/// </summary>
public static class LocalizationService
{
    private static bool _initialized;

    public static bool IsEnglish { get; private set; }
    public static string LanguageCode => IsEnglish ? "en" : "zh";

    /// <summary>Raised after the language changes so generated content can rebuild.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Initialise from the persisted setting. An empty/"auto" value picks the
    /// language from LC_ALL / LC_MESSAGES / LANG.
    /// </summary>
    public static void Initialize(string? code)
    {
        var normalized = (code ?? "auto").Trim().ToLowerInvariant();
        IsEnglish = normalized switch
        {
            "en" => true,
            "zh" => false,
            _ => !IsChineseLocale(),
        };
        ApplyResources();
        _initialized = true;
    }

    public static void SetLanguage(string code)
    {
        var normalized = (code ?? "auto").Trim().ToLowerInvariant();
        var english = normalized switch
        {
            "en" => true,
            "zh" => false,
            _ => !IsChineseLocale(),
        };

        if (_initialized && english == IsEnglish)
        {
            ApplyResources();
            return;
        }

        IsEnglish = english;
        ApplyResources();
        _initialized = true;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Pick the Chinese or English variant of an inline string.</summary>
    public static string L(string chinese, string english) => IsEnglish ? english : chinese;

    /// <summary>Pick the Chinese or English format string and format it.</summary>
    public static string F(string chineseFormat, string englishFormat, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, IsEnglish ? englishFormat : chineseFormat, args);

    private static void ApplyResources()
    {
        var application = Application.Current;
        if (application is null) return;

        var table = IsEnglish ? LocalizationResources.English : LocalizationResources.Chinese;
        foreach (var (key, value) in table)
            application.Resources[key] = value;
    }

    private static bool IsChineseLocale()
    {
        var locale = Environment.GetEnvironmentVariable("LC_ALL");
        if (string.IsNullOrWhiteSpace(locale)) locale = Environment.GetEnvironmentVariable("LC_MESSAGES");
        if (string.IsNullOrWhiteSpace(locale)) locale = Environment.GetEnvironmentVariable("LANG");
        return locale is not null &&
               locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}
