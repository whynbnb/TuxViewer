using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>
/// Resolves application icons from .desktop files and the icon theme on disk.
/// Results are cached; icons are loaded once and shared by all windows of an app.
/// </summary>
public static class IconService
{
    public static bool Enabled { get; set; } = true;

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DesktopByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DesktopByWmClass = new(StringComparer.OrdinalIgnoreCase);
    private static bool _desktopIndexBuilt;

    private static readonly string[] SizeDirectories =
    [
        "256x256", "128x128", "96x96", "64x64", "48x48", "512x512", "1024x1024",
        "32x32", "24x24", "22x22", "16x16",
    ];

    public static void Enrich(IReadOnlyList<WindowInfo> windows)
    {
        if (!Enabled) return;
        lock (Sync)
        {
            if (!_desktopIndexBuilt) BuildDesktopIndex();
            foreach (var window in windows)
            {
                var key = CacheKey(window);
                if (Cache.TryGetValue(key, out var cached))
                {
                    window.Icon = cached;
                    continue;
                }

                Bitmap? icon = null;
                try
                {
                    icon = LoadForWindow(window);
                }
                catch { /* icon is optional */ }
                Cache[key] = icon;
                window.Icon = icon;
            }
        }
    }

    private static string CacheKey(WindowInfo window)
    {
        var key = !string.IsNullOrWhiteSpace(window.AppId) ? window.AppId : window.WmClass;
        if (string.IsNullOrWhiteSpace(key)) key = window.Title;
        return key!.Trim().ToLowerInvariant();
    }

    private static Bitmap? LoadForWindow(WindowInfo window)
    {
        var iconName = FindIconName(window);
        if (!string.IsNullOrWhiteSpace(iconName))
        {
            var bitmap = LoadIconByName(iconName!);
            if (bitmap is not null) return bitmap;
        }

        // TuxViewer may not be installed yet; use the icon embedded in the app.
        var key = CacheKey(window);
        if (key.Contains("tuxviewer", StringComparison.Ordinal))
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://TuxViewer/Assets/tuxviewer.png"));
                return new Bitmap(stream);
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private static string? FindIconName(WindowInfo window)
    {
        foreach (var candidate in new[] { window.AppId, window.WmClass })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var key = candidate.Trim();
            if (DesktopByName.TryGetValue(key, out var byName)) return byName;
            if (DesktopByWmClass.TryGetValue(key, out var byWm)) return byWm;
        }
        return null;
    }

    private static void BuildDesktopIndex()
    {
        _desktopIndexBuilt = true;
        foreach (var dir in ApplicationDirectories())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var values = ReadDesktopEntry(file);
                        if (!values.TryGetValue("Icon", out var icon) || string.IsNullOrWhiteSpace(icon)) continue;
                        var baseName = Path.GetFileNameWithoutExtension(file);
                        DesktopByName.TryAdd(baseName, icon);
                        if (values.TryGetValue("StartupWMClass", out var wmClass) && !string.IsNullOrWhiteSpace(wmClass))
                            DesktopByWmClass.TryAdd(wmClass, icon);
                    }
                    catch { /* skip malformed desktop file */ }
                }
            }
            catch { /* directory missing */ }
        }
    }

    private static Dictionary<string, string> ReadDesktopEntry(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inEntry = false;
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                if (inEntry) break;
                inEntry = string.Equals(trimmed, "[Desktop Entry]", StringComparison.Ordinal);
                continue;
            }
            if (!inEntry || trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            result.TryAdd(trimmed[..idx].Trim(), trimmed[(idx + 1)..].Trim());
        }
        return result;
    }

    private static IEnumerable<string> ApplicationDirectories()
    {
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "share", "applications"));
        dirs.Add(Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "applications"));

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs))
            dataDirs = "/usr/local/share:/usr/share";
        foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            dirs.Add(Path.Combine(dir, "applications"));

        dirs.Add("/var/lib/flatpak/exports/share/applications");
        dirs.Add("/var/lib/snapd/desktop/applications");
        return dirs.Distinct();
    }

    private static IEnumerable<string> IconRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>
        {
            Path.Combine(home, ".local", "share", "icons"),
            Path.Combine(home, ".icons"),
            Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "icons"),
            "/var/lib/flatpak/exports/share/icons",
            "/var/lib/snapd/desktop/icons",
        };

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs))
            dataDirs = "/usr/local/share:/usr/share";
        foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            roots.Add(Path.Combine(dir, "icons"));

        roots.Add("/usr/share/pixmaps");
        return roots.Distinct();
    }

    private static Bitmap? LoadIconByName(string icon)
    {
        icon = icon.Trim();
        if (icon.Length == 0) return null;

        if (Path.IsPathRooted(icon))
            return File.Exists(icon) ? LoadBitmap(icon) : null;

        if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            icon.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            icon.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var root in IconRoots())
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, icon, SearchOption.AllDirectories))
                        return LoadBitmap(file);
                }
                catch { /* ignore */ }
            }
            return null;
        }

        foreach (var root in IconRoots())
        {
            if (!Directory.Exists(root)) continue;

            // Prefer the standard hicolor theme.
            foreach (var size in SizeDirectories)
            {
                var path = Path.Combine(root, "hicolor", size, "apps", icon + ".png");
                if (File.Exists(path)) return LoadBitmap(path);
            }

            // Then scan installed themes (Papirus/Adwaita/Breeze/Yaru/...).
            try
            {
                foreach (var theme in Directory.EnumerateDirectories(root))
                {
                    foreach (var size in SizeDirectories)
                    {
                        var path = Path.Combine(theme, size, "apps", icon + ".png");
                        if (File.Exists(path)) return LoadBitmap(path);
                    }
                }
            }
            catch { /* ignore */ }

            var pixmap = Path.Combine(root, icon + ".png");
            if (File.Exists(pixmap)) return LoadBitmap(pixmap);
        }

        return null;
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
