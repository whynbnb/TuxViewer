using System.Globalization;
using System.Text;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

/// <summary>
/// GNOME Shell blocks org.gnome.Shell.Introspect for third-party apps, so this provider talks to
/// the small optional extension shipped with TuxViewer.
/// </summary>
public sealed class GnomeProvider : WindowProviderBase
{
    private const string ObjectPath = "/org/gnome/Shell/Extensions/TuxViewer";
    private const string Interface = "org.gnome.Shell.Extensions.TuxViewer";

    private bool _ready;
    private string _probeDetail = "GNOME Shell 扩展尚未初始化";

    public override string Id => "gnome";
    public override string Name => "GNOME Shell 扩展";
    public override bool IsAvailable => _ready;
    public override string Detail => _probeDetail;
    public override PickCapability Pick => PickCapability.Cursor;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (!desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) &&
            !desktop.Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            _probeDetail = "当前不是 GNOME 会话";
            return;
        }
        if (!CommandRunner.Exists("gdbus"))
        {
            _probeDetail = "缺少 gdbus";
            return;
        }

        var result = await CallAsync("ListWindows", [], 2500, cancellationToken);
        if (result is null || !result.Success)
        {
            var enabled = await CommandRunner.TryRunAsync("gnome-extensions", ["list", "--enabled"], 2000, cancellationToken);
            _probeDetail = enabled?.StdOut.Contains("tuxviewer@local", StringComparison.Ordinal) == true
                ? "扩展已启用但 DBus 无响应，请重新登录 GNOME 会话"
                : "需要安装并启用 TuxViewer GNOME 扩展（见“诊断”页）";
            return;
        }

        var json = ParseGVariantString(result.StdOut);
        if (string.IsNullOrWhiteSpace(json))
        {
            _probeDetail = "扩展返回了空数据";
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var count = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            _ready = true;
            _probeDetail = $"GNOME Shell 扩展已连接 · {count} 个窗口";
        }
        catch
        {
            _probeDetail = "扩展输出无法解析";
        }
    }

    public override async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("ListWindows", [], 4000, cancellationToken);
        if (result?.Success != true) return [];
        var json = ParseGVariantString(result.StdOut);
        if (string.IsNullOrWhiteSpace(json)) return [];

        var windows = new List<WindowInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return windows;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var info = ParseWindow(element);
                if (info is not null) windows.Add(info);
            }
        }
        catch { /* ignore */ }
        return windows;
    }

    public override async Task<WindowInfo?> PickAtCursorAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new PickProgress { Message = "GNOME：正在读取指针下的窗口…" });
        var result = await CallAsync("PickWindowAtCursor", [], 4000, cancellationToken);
        if (result?.Success != true) return null;
        var json = ParseGVariantString(result.StdOut);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var window = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault()
                : doc.RootElement;
            return window.ValueKind == JsonValueKind.Object ? ParseWindow(window) : null;
        }
        catch
        {
            return null;
        }
    }

    public override async Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
        => (await GetWindowsAsync(cancellationToken)).FirstOrDefault(w => w.IsFocused);

    public override async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(window.NativeId, out var id)) return false;
        var result = await CallAsync("FocusWindow", [$"uint64 {id}"], 3000, cancellationToken);
        return result?.Success == true && ParseGVariantBool(result.StdOut);
    }

    public override async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(window.NativeId, out var id)) return false;
        var result = await CallAsync("CloseWindow", [$"uint64 {id}"], 3000, cancellationToken);
        return result?.Success == true && ParseGVariantBool(result.StdOut);
    }

    private static WindowInfo? ParseWindow(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetUInt64(out var id)) return null;

        var title = GetString(e, "title");
        var appId = GetString(e, "app_id");
        var wmClass = GetString(e, "wm_class");
        var pid = e.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
        var clientType = e.TryGetProperty("client_type", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
        var isX11 = clientType == 1;
        var focused = e.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True;
        var hidden = e.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;

        var info = new WindowInfo
        {
            Id = $"gnome:{id}",
            SourceId = "gnome",
            SourceName = "GNOME",
            NativeId = id.ToString(CultureInfo.InvariantCulture),
            Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title!,
            AppId = appId,
            WmClass = wmClass,
            Pid = pid,
            Backend = isX11 ? WindowBackend.X11 : WindowBackend.Wayland,
            IsXWayland = isX11,
            IsFocused = focused,
            IsMapped = !hidden,
            X = GetInt(e, "x"),
            Y = GetInt(e, "y"),
            Width = GetInt(e, "width"),
            Height = GetInt(e, "height"),
            WorkspaceId = GetInt(e, "workspace") is >= 0 ? GetInt(e, "workspace") : null,
            RawJson = e.GetRawText(),
        };

        info.Extra["窗口 ID"] = id.ToString(CultureInfo.InvariantCulture);
        info.Extra["Client Type"] = isX11 ? "X11 / XWayland" : "Wayland";
        info.Extra["App ID"] = appId ?? "—";
        info.Extra["WM Class"] = wmClass ?? "—";
        return info;
    }

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static async Task<CommandResult?> CallAsync(
        string method, IReadOnlyList<string> args, int timeoutMs, CancellationToken cancellationToken)
    {
        var all = new List<string>
        {
            "call", "--session", "--dest", "org.gnome.Shell", "--object-path", ObjectPath,
            "--method", $"{Interface}.{method}",
        };
        all.AddRange(args);
        return await CommandRunner.TryRunAsync("gdbus", all, timeoutMs, cancellationToken);
    }

    private static string? ParseGVariantString(string output)
    {
        output = output.Trim();
        if (output.Length == 0) return null;
        var start = output.IndexOf('\'');
        var end = output.LastIndexOf('\'');
        if (start < 0 || end <= start) return null;
        var raw = output[(start + 1)..end];
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(raw[i]);
                continue;
            }
            var next = raw[++i];
            sb.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '\'' => '\'',
                _ => next,
            });
        }
        return sb.ToString();
    }

    private static bool ParseGVariantBool(string output)
        => output.Contains("true", StringComparison.OrdinalIgnoreCase);
}
