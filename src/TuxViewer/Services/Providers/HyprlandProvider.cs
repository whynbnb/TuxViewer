using System.Globalization;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

public sealed class HyprlandProvider : WindowProviderBase
{
    private readonly string? _instance;
    private string? _version;

    public HyprlandProvider()
    {
        _instance = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
    }

    public override string Id => "hyprland";
    public override string Name => "Hyprland IPC";
    public override bool IsAvailable => CommandRunner.Exists("hyprctl") && !string.IsNullOrWhiteSpace(_instance);
    public override string Detail => IsAvailable
        ? $"Hyprland {_version ?? ""}".Trim() + LocalizationService.L(" · 指针停留拾取", " · pointer-hover picking")
        : LocalizationService.L("未检测到 HYPRLAND_INSTANCE_SIGNATURE", "HYPRLAND_INSTANCE_SIGNATURE not detected");
    public override PickCapability Pick => PickCapability.Cursor;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
    {
        var versionTask = EnsureVersionAsync(cancellationToken);
        var clientsTask = CommandRunner.TryRunAsync("hyprctl", ["-j", "clients"], 4000, cancellationToken);
        var monitorsTask = CommandRunner.TryRunAsync("hyprctl", ["-j", "monitors"], 3000, cancellationToken);
        await Task.WhenAll(versionTask, clientsTask, monitorsTask);

        var monitors = ParseMonitors(monitorsTask.Result?.StdOut);
        var result = new List<WindowInfo>();
        var json = clientsTask.Result?.StdOut;
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var client in doc.RootElement.EnumerateArray())
            {
                var info = ParseClient(client, monitors);
                if (info is not null) result.Add(info);
            }
        }
        catch { /* ignore */ }
        return result;
    }

    public override async Task<WindowInfo?> PickAtCursorAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        var position = await GetCursorPositionAsync(cancellationToken);
        if (position is null) return null;
        progress?.Report(new PickProgress { Message = LocalizationService.F("Hyprland：指针位置 {0},{1}", "Hyprland: pointer at {0},{1}", position.Value.X, position.Value.Y) });

        var windows = await GetWindowsAsync(cancellationToken);
        var hits = windows
            .Where(w => w.IsMapped && w.X is not null && w.Y is not null && w.Width is > 0 && w.Height is > 0)
            .Where(w => position.Value.X >= w.X && position.Value.X < w.X + w.Width &&
                        position.Value.Y >= w.Y && position.Value.Y < w.Y + w.Height)
            .OrderBy(w => (long)(w.Width ?? 0) * (w.Height ?? 0))
            .ToList();
        return hits.FirstOrDefault();
    }

    public override async Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
    {
        var result = await CommandRunner.TryRunAsync("hyprctl", ["-j", "activewindow"], 3000, cancellationToken);
        if (result?.Success != true || string.IsNullOrWhiteSpace(result.StdOut)) return null;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("address", out var address) &&
                address.ValueKind == JsonValueKind.String && address.GetString() is "" or null)
                return null;
            return ParseClient(doc.RootElement, []);
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
            "hyprctl", ["dispatch", "focuswindow", $"address:{window.NativeId}"], 3000, cancellationToken);
        return result?.Success == true;
    }

    public override async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var result = await CommandRunner.TryRunAsync(
            "hyprctl", ["dispatch", "closewindow", $"address:{window.NativeId}"], 3000, cancellationToken);
        return result?.Success == true;
    }

    public override async Task<bool> HighlightAsync(WindowInfo window, int durationMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;

        var previousAddress = "";
        var active = await CommandRunner.TryRunAsync("hyprctl", ["-j", "activewindow"], 2000, cancellationToken);
        if (active?.Success == true)
        {
            try
            {
                using var doc = JsonDocument.Parse(active.StdOut);
                if (doc.RootElement.TryGetProperty("address", out var address) &&
                    address.ValueKind == JsonValueKind.String)
                    previousAddress = address.GetString() ?? "";
            }
            catch { /* ignore */ }
        }

        var focus = await CommandRunner.TryRunAsync(
            "hyprctl", ["dispatch", "focuswindow", $"address:{window.NativeId}"], 3000, cancellationToken);
        if (focus?.Success != true) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(previousAddress) &&
                    !string.Equals(previousAddress, window.NativeId, StringComparison.OrdinalIgnoreCase))
                {
                    await CommandRunner.TryRunAsync(
                        "hyprctl", ["dispatch", "focuswindow", $"address:{previousAddress}"], 3000, CancellationToken.None);
                }
            }
            catch { /* best effort */ }
        });
        return true;
    }

    private async Task<(int X, int Y)?> GetCursorPositionAsync(CancellationToken cancellationToken)
    {
        var json = await CommandRunner.TryRunAsync("hyprctl", ["-j", "cursorpos"], 2000, cancellationToken);
        var text = json?.StdOut;
        if (string.IsNullOrWhiteSpace(text))
        {
            var plain = await CommandRunner.TryRunAsync("hyprctl", ["cursorpos"], 2000, cancellationToken);
            text = plain?.StdOut;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        try
        {
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("x", out var x) && doc.RootElement.TryGetProperty("y", out var y))
                    return (x.GetInt32(), y.GetInt32());
            }
        }
        catch { /* fall through */ }

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var px) && int.TryParse(parts[1], out var py))
            return (px, py);
        return null;
    }

    private static WindowInfo? ParseClient(JsonElement client, Dictionary<int, string> monitors)
    {
        var address = client.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String
            ? addr.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(address)) return null;

        var title = client.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var cls = client.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var initialClass = client.TryGetProperty("initialClass", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
        var pid = client.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
        var xwayland = client.TryGetProperty("xwayland", out var xw) && xw.ValueKind == JsonValueKind.True;
        var mapped = !(client.TryGetProperty("mapped", out var m) && m.ValueKind == JsonValueKind.False);
        var hidden = client.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
        var focused = client.TryGetProperty("focusHistoryID", out var fh) && fh.ValueKind == JsonValueKind.Number && fh.GetInt32() == 0;
        var workspaceId = 0;
        string? workspaceName = null;
        if (client.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object)
        {
            if (ws.TryGetProperty("id", out var wsId) && wsId.ValueKind == JsonValueKind.Number) workspaceId = wsId.GetInt32();
            if (ws.TryGetProperty("name", out var wsName) && wsName.ValueKind == JsonValueKind.String) workspaceName = wsName.GetString();
        }

        int? x = null, y = null, width = null, height = null;
        if (client.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Array && at.GetArrayLength() >= 2)
        {
            x = at[0].GetInt32();
            y = at[1].GetInt32();
        }
        if (client.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Array && size.GetArrayLength() >= 2)
        {
            width = size[0].GetInt32();
            height = size[1].GetInt32();
        }

        var monitorIndex = client.TryGetProperty("monitor", out var mon) && mon.ValueKind == JsonValueKind.Number ? mon.GetInt32() : -1;
        monitors.TryGetValue(monitorIndex, out var monitorName);

        var info = new WindowInfo
        {
            Id = $"hypr:{address}",
            SourceId = "hyprland",
            SourceName = "Hyprland",
            NativeId = address,
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.L("(无标题)", "(untitled)") : title!,
            AppId = cls,
            WmClass = cls,
            Pid = pid,
            Backend = xwayland ? WindowBackend.X11 : WindowBackend.Wayland,
            IsXWayland = xwayland,
            IsFocused = focused,
            IsMapped = mapped && !hidden,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            WorkspaceId = workspaceId == 0 ? null : workspaceId,
            WorkspaceName = workspaceName,
            OutputName = monitorName,
            RawJson = client.GetRawText(),
        };

        info.Extra["地址"] = address;
        info.Extra["Class"] = cls ?? "—";
        if (initialClass is not null) info.Extra["Initial Class"] = initialClass;
        info.Extra["XWayland"] = xwayland ? "是" : "否";
        info.Extra["浮动"] = client.TryGetProperty("floating", out var fl) && fl.ValueKind == JsonValueKind.True ? "是" : "否";
        info.Extra["全屏"] = client.TryGetProperty("fullscreen", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.GetInt32() != 0 ? "是" : "否";
        if (monitorName is not null) info.Extra["输出"] = monitorName;
        if (workspaceName is not null) info.Extra["工作区"] = workspaceName;
        return info;
    }

    private static Dictionary<int, string> ParseMonitors(string? json)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
            var index = 0;
            foreach (var monitor in doc.RootElement.EnumerateArray())
            {
                if (monitor.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    map[index] = n.GetString()!;
                index++;
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null) return;
        var result = await CommandRunner.TryRunAsync("hyprctl", ["-j", "version"], 2000, cancellationToken);
        if (result?.Success != true) return;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            _version = doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : doc.RootElement.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        }
        catch { /* ignore */ }
    }
}
