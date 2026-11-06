using System.Globalization;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

public sealed class NiriProvider : WindowProviderBase
{
    private readonly string? _socket;
    private string? _version;

    public NiriProvider()
    {
        _socket = Environment.GetEnvironmentVariable("NIRI_SOCKET");
    }

    public override string Id => "niri";
    public override string Name => "niri IPC";
    public override bool IsAvailable => CommandRunner.Exists("niri") && !string.IsNullOrWhiteSpace(_socket);
    public override string Detail => IsAvailable
        ? $"niri {_version ?? ""}".Trim() + " · 原生 Wayland + XWayland"
        : "未检测到 NIRI_SOCKET";
    public override PickCapability Pick => PickCapability.Interactive;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
    {
        var versionTask = EnsureVersionAsync(cancellationToken);
        var windowsTask = CommandRunner.TryRunAsync("niri", ["msg", "--json", "windows"], 4000, cancellationToken);
        var workspacesTask = CommandRunner.TryRunAsync("niri", ["msg", "--json", "workspaces"], 4000, cancellationToken);
        await Task.WhenAll(versionTask, windowsTask, workspacesTask);

        var wsMap = ParseWorkspaces(workspacesTask.Result?.StdOut);
        var result = new List<WindowInfo>();
        var stdout = windowsTask.Result?.StdOut;
        if (string.IsNullOrWhiteSpace(stdout)) return result;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var window = ParseWindow(element, wsMap);
                if (window is not null) result.Add(window);
            }
        }
        catch { /* provider changed output */ }

        return result;
    }

    public override async Task<WindowInfo?> PickInteractiveAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new PickProgress { Message = "niri：移动十字准星并点击目标窗口（Esc 取消）" });
        var result = await CommandRunner.TryRunAsync(
            "niri", ["msg", "--json", "pick-window"], 180_000, cancellationToken);

        if (result is null || !result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var element = Unwrap(doc.RootElement);
            if (element.ValueKind != JsonValueKind.Object) return null;
            var window = ParseWindow(element, []);
            if (window is not null)
                progress?.Report(new PickProgress { Message = $"niri：已选择 {window.Title}", HoverWindow = window });
            return window;
        }
        catch
        {
            return null;
        }
    }

    public override async Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
    {
        var result = await CommandRunner.TryRunAsync(
            "niri", ["msg", "--json", "focused-window"], 3000, cancellationToken);
        if (result is null || !result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var element = Unwrap(doc.RootElement);
            return element.ValueKind == JsonValueKind.Object ? ParseWindow(element, []) : null;
        }
        catch
        {
            return null;
        }
    }

    public override async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var result = await CommandRunner.TryRunAsync(
            "niri", ["msg", "action", "focus-window", "--id", window.NativeId], 3000, cancellationToken);
        return result?.Success == true;
    }

    public override async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var result = await CommandRunner.TryRunAsync(
            "niri", ["msg", "action", "close-window", "--id", window.NativeId], 3000, cancellationToken);
        return result?.Success == true;
    }

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null) return;
        var result = await CommandRunner.TryRunAsync(
            "niri", ["msg", "--json", "version"], 2000, cancellationToken);
        if (result?.Success != true) return;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (doc.RootElement.TryGetProperty("compositor", out var compositor))
                _version = compositor.GetString();
        }
        catch { /* ignore */ }
    }

    private static Dictionary<int, (string? Name, int Idx, string? Output)> ParseWorkspaces(string? json)
    {
        var map = new Dictionary<int, (string?, int, string?)>();
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            foreach (var ws in doc.RootElement.EnumerateArray())
            {
                var id = ws.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                if (id < 0) continue;
                var name = ws.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var idx = ws.TryGetProperty("idx", out var idxEl) ? idxEl.GetInt32() : 0;
                var output = ws.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
                map[id] = (name, idx, output);
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root;
        foreach (var name in new[] { "Ok", "ok", "window", "Window" })
        {
            if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object)
                return inner;
        }
        return root;
    }

    private static WindowInfo? ParseWindow(JsonElement e, Dictionary<int, (string? Name, int Idx, string? Output)> workspaces)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return null;

        var title = e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var appId = e.TryGetProperty("app_id", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        var pid = e.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
        var workspaceId = e.TryGetProperty("workspace_id", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : (int?)null;
        var focused = e.TryGetProperty("is_focused", out var f) && f.ValueKind is JsonValueKind.True;
        var floating = e.TryGetProperty("is_floating", out var fl) && fl.ValueKind is JsonValueKind.True;
        var urgent = e.TryGetProperty("is_urgent", out var ur) && ur.ValueKind is JsonValueKind.True;

        int? width = null, height = null;
        string? tileSize = null, pos = null;
        if (e.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            if (layout.TryGetProperty("window_size", out var size) && size.ValueKind == JsonValueKind.Array && size.GetArrayLength() >= 2)
            {
                width = (int)Math.Round(size[0].GetDouble());
                height = (int)Math.Round(size[1].GetDouble());
            }
            if (layout.TryGetProperty("tile_size", out var tile) && tile.ValueKind == JsonValueKind.Array && tile.GetArrayLength() >= 2)
                tileSize = $"{tile[0]:0.#}×{tile[1]:0.#}";
            if (layout.TryGetProperty("pos_in_scrolling_layout", out var posEl) && posEl.ValueKind == JsonValueKind.Array && posEl.GetArrayLength() >= 2)
                pos = $"{posEl[0]},{posEl[1]}";
        }

        var (wsName, wsIdx, output) = workspaceId is not null && workspaces.TryGetValue(workspaceId.Value, out var ws)
            ? ws
            : (null, 0, null);

        var info = new WindowInfo
        {
            Id = $"niri:{id}",
            SourceId = "niri",
            SourceName = "niri",
            NativeId = id.ToString(CultureInfo.InvariantCulture),
            Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title!,
            AppId = appId,
            WmClass = appId,
            Pid = pid,
            Backend = WindowBackend.Wayland,
            IsMapped = true,
            IsFocused = focused,
            Width = width,
            Height = height,
            WorkspaceId = workspaceId,
            WorkspaceName = !string.IsNullOrWhiteSpace(wsName) ? wsName : workspaceId is not null ? $"ws {wsIdx}" : null,
            OutputName = output,
            RawJson = e.GetRawText(),
        };

        info.Extra["窗口 ID"] = id.ToString(CultureInfo.InvariantCulture);
        info.Extra["App ID"] = appId ?? "—";
        info.Extra["浮动"] = floating ? "是" : "否";
        info.Extra["紧急"] = urgent ? "是" : "否";
        if (tileSize is not null) info.Extra["瓦片尺寸"] = tileSize;
        if (pos is not null) info.Extra["布局位置"] = pos;
        if (e.TryGetProperty("focus_timestamp", out var ft) && ft.ValueKind == JsonValueKind.Object
            && ft.TryGetProperty("secs", out var secs) && secs.ValueKind == JsonValueKind.Number)
        {
            var age = TimeSpan.FromSeconds(secs.GetInt64());
            info.Extra["最近聚焦"] = $"合成器启动后 {age.TotalHours:00}:{age.Minutes:00}:{age.Seconds:00}";
        }
        return info;
    }
}
