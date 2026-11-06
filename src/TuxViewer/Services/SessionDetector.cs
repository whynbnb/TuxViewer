using System.Diagnostics;
using System.Text.RegularExpressions;
using TuxViewer.Models;

namespace TuxViewer.Services;

public static partial class SessionDetector
{
    private static readonly string[] CompositorNames =
    [
        "niri", "Hyprland", "sway", "kwin_wayland", "kwin_x11", "gnome-shell", "weston",
        "labwc", "wayfire", "river", "cosmic-comp", "mutter", "Xorg", "Xwayland",
        "xfwm4", "openbox", "i3", "bspwm", "dwm", "awesome", "herbstluftwm", "qtile",
        "icewm", "fluxbox", "enlightenment", "marco", "muffin", "cinnamon", "kwin",
        "weston", "gamescope", "sway", "dwl", "miracle-wm", "wayfire",
    ];

    public static SessionInfo Detect(IReadOnlyList<ProviderStatus> statuses, string? x11WmName)
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var x11Display = Environment.GetEnvironmentVariable("DISPLAY");
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
                      ?? Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP")
                      ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
                      ?? "未知";

        var processes = FindCompositorProcesses();
        var compositor = processes.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(compositor))
        {
            compositor = desktop switch
            {
                var d when d.Contains("KDE", StringComparison.OrdinalIgnoreCase) => "KWin",
                var d when d.Contains("GNOME", StringComparison.OrdinalIgnoreCase) => "Mutter",
                var d when d.Contains("niri", StringComparison.OrdinalIgnoreCase) => "niri",
                var d when d.Contains("sway", StringComparison.OrdinalIgnoreCase) => "sway",
                var d when d.Contains("Hyprland", StringComparison.OrdinalIgnoreCase) => "Hyprland",
                _ => null,
            };
        }

        var version = ExtractVersion(statuses);
        if (!string.IsNullOrWhiteSpace(sessionType) &&
            !sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase) &&
            !sessionType.Equals("x11", StringComparison.OrdinalIgnoreCase))
        {
            sessionType = "";
        }

        if (string.IsNullOrWhiteSpace(sessionType))
            sessionType = !string.IsNullOrWhiteSpace(waylandDisplay) ? "wayland" : !string.IsNullOrWhiteSpace(x11Display) ? "x11" : "未知";

        return new SessionInfo
        {
            SessionType = sessionType,
            Desktop = desktop,
            WaylandDisplay = waylandDisplay,
            X11Display = x11Display,
            Compositor = compositor,
            CompositorVersion = version,
            X11WmName = x11WmName,
        };
    }

    private static string? ExtractVersion(IReadOnlyList<ProviderStatus> statuses)
    {
        foreach (var status in statuses.Where(s => s.Available))
        {
            var match = VersionRegex().Match(status.Detail);
            if (match.Success) return match.Value;
        }
        return null;
    }

    public static IReadOnlyList<string> FindCompositorProcesses()
    {
        var found = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (!int.TryParse(name, out _)) continue;
                try
                {
                    var comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    if (CompositorNames.Contains(comm, StringComparer.OrdinalIgnoreCase) && !found.Contains(comm))
                        found.Add(comm);
                }
                catch { /* process disappeared */ }
            }
        }
        catch { /* ignore */ }
        return found
            .OrderBy(name => Array.FindIndex(CompositorNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> GetWaylandProtocolsAsync(CancellationToken cancellationToken)
    {
        var result = await CommandRunner.TryRunAsync("wayland-info", [], 3000, cancellationToken);
        if (result?.Success != true) return [];
        var protocols = new List<string>();
        try
        {
            foreach (var line in result.StdOut.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("interface:", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Regex.Match(trimmed, "'([^']+)'").Groups[1].Value;
                var version = Regex.Match(trimmed, @"version:\s*(\d+)").Groups[1].Value;
                if (name.Length == 0) continue;
                protocols.Add(version.Length > 0 ? $"{name} v{version}" : name);
            }
        }
        catch { /* ignore */ }
        return protocols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();
}
