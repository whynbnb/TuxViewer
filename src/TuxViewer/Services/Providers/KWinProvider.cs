using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

/// <summary>
/// KWin exposes an interactive window picker over D-Bus. It intentionally does not expose a
/// window list to unprivileged clients, so this provider is pick-only.
/// </summary>
public sealed partial class KWinProvider : WindowProviderBase
{
    private readonly string? _qdbus;

    public KWinProvider()
    {
        _qdbus = CommandRunner.Exists("qdbus6") ? "qdbus6" : CommandRunner.Exists("qdbus") ? "qdbus" : null;
    }

    public override string Id => "kwin";
    public override string Name => "KWin (KDE)";
    public override bool IsAvailable
    {
        get
        {
            var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "") + " " +
                          (Environment.GetEnvironmentVariable("DESKTOP_SESSION") ?? "");
            var isKde = desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) ||
                        desktop.Contains("Plasma", StringComparison.OrdinalIgnoreCase);
            return isKde && _qdbus is not null &&
                   !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        }
    }

    public override string Detail => IsAvailable
        ? "KWin · 仅交互式十字准星（KWin 不提供窗口列表 DBus API）"
        : "未检测到 KDE Plasma 会话";
    public override PickCapability Pick => PickCapability.Interactive;
    public override bool CanList => false;

    public override Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WindowInfo>>([]);

    public override async Task<WindowInfo?> PickInteractiveAsync(IProgress<PickProgress>? progress, PickMode mode, CancellationToken cancellationToken)
    {
        if (_qdbus is null) return null;
        progress?.Report(new PickProgress
        {
            Message = mode == PickMode.Drag
                ? "KWin 原生拾取器只支持点击：移动准星并单击目标窗口（Esc 取消）"
                : "KWin：移动十字准星并单击目标窗口（Esc 取消）",
        });

        var result = await CommandRunner.TryRunAsync(
            _qdbus, ["org.kde.KWin", "/KWin", "org.kde.KWin.queryWindowInfo"], 180_000, cancellationToken);
        if (result is null) return null;

        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            if (result.StdErr.Contains("UserCancel", StringComparison.OrdinalIgnoreCase) ||
                result.StdErr.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                return null;

            // Only retry another DBus client when the method itself was unavailable.
            var methodMissing = result.StdErr.Contains("No such method", StringComparison.OrdinalIgnoreCase) ||
                                result.StdErr.Contains("UnknownMethod", StringComparison.OrdinalIgnoreCase) ||
                                result.StdErr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
            if (!methodMissing) return null;

            var gdbus = await CommandRunner.TryRunAsync(
                "gdbus",
                ["call", "--session", "--dest", "org.kde.KWin", "--object-path", "/KWin",
                 "--method", "org.kde.KWin.queryWindowInfo"],
                180_000, cancellationToken);
            if (gdbus is null || !gdbus.Success || string.IsNullOrWhiteSpace(gdbus.StdOut)) return null;
            result = gdbus;
        }

        var map = ParseVariantMap(result.StdOut);
        if (map.Count == 0) return null;

        var uuid = Get(map, "uuid") ?? Guid.NewGuid().ToString();
        var title = Get(map, "caption") ?? Get(map, "resourceName");
        var cls = Get(map, "resourceClass") ?? Get(map, "resourceName");
        var pid = ParseInt(Get(map, "pid")) ?? ParseInt(Get(map, "PID"));
        pid ??= ProcessResolver.FindPid(Get(map, "resourceName"), cls);
        var x = ParseInt(Get(map, "x"));
        var y = ParseInt(Get(map, "y"));
        var width = ParseInt(Get(map, "width"));
        var height = ParseInt(Get(map, "height"));
        var minimized = string.Equals(Get(map, "minimized"), "true", StringComparison.OrdinalIgnoreCase);
        var active = string.Equals(Get(map, "active"), "true", StringComparison.OrdinalIgnoreCase);

        var info = new WindowInfo
        {
            Id = $"kwin:{uuid}",
            SourceId = "kwin",
            SourceName = "KWin",
            NativeId = uuid,
            Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title!,
            AppId = cls,
            WmClass = cls,
            Pid = pid,
            Backend = WindowBackend.Wayland,
            IsFocused = active,
            IsMapped = !minimized,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            RawJson = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }),
        };

        info.Extra["UUID"] = uuid;
        foreach (var (key, value) in map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            info.Extra[key] = value;

        progress?.Report(new PickProgress { Message = $"KWin：已选择 {info.Title}", HoverWindow = info });
        return info;
    }

    private static Dictionary<string, string> ParseVariantMap(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        // gdbus: {'caption': <'...'>, 'pid': <123>, ...}
        foreach (Match match in GVariantPairRegex().Matches(text))
        {
            var key = match.Groups[1].Value.Trim();
            var value = CleanVariant(match.Groups[2].Value);
            if (key.Length > 0) map[key] = value;
        }
        if (map.Count > 0) return map;

        // qdbus: key: value, one entry per line.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            if (key.Length == 0 || key.Contains(' ')) continue;
            var value = CleanVariant(trimmed[(idx + 1)..]);
            map[key] = value;
        }
        return map;
    }

    private static string CleanVariant(string value)
    {
        value = value.Trim();
        if (value.StartsWith('<') && value.EndsWith('>')) value = value[1..^1].Trim();
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
            value = value[1..^1];
        return value.Replace("\\'", "'").Trim();
    }

    private static string? Get(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    [GeneratedRegex(@"'([^']+)'\s*:\s*<([^>]*)>")]
    private static partial Regex GVariantPairRegex();
}
