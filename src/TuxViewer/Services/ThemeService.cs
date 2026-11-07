using Avalonia;
using Avalonia.Styling;

namespace TuxViewer.Services;

/// <summary>
/// Applies the requested theme variant. "system" follows the desktop and is
/// the default so light/dark switching feels native on every desktop.
/// </summary>
public static class ThemeService
{
    public static string CurrentCode { get; private set; } = "system";

    public static void Initialize(string? code) => SetTheme(code, log: false);

    public static void SetTheme(string? code, bool log = true)
    {
        CurrentCode = Normalize(code);
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = CurrentCode switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        if (log) AppLog.Info($"Theme changed to {CurrentCode}");
    }

    public static string Normalize(string? code) => (code ?? "system").Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system",
    };
}
