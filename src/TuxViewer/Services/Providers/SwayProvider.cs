using System.Globalization;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

public sealed class SwayProvider : WindowProviderBase
{
    private readonly string? _socket;
    private string? _version;

    public SwayProvider()
    {
        _socket = Environment.GetEnvironmentVariable("SWAYSOCK");
    }

    public override string Id => "sway";
    public override string Name => "Sway IPC";
    public override bool IsAvailable => CommandRunner.Exists("swaymsg") && !string.IsNullOrWhiteSpace(_socket);
    public override string Detail => IsAvailable
        ? $"sway {_version ?? ""}".Trim() + LocalizationService.L(" · XWayland 标记", " · XWayland tag")
        : LocalizationService.L("未检测到 SWAYSOCK", "SWAYSOCK not detected");
    public override PickCapability Pick => PickCapability.Focused;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
    {
        var versionTask = EnsureVersionAsync(cancellationToken);
        var treeTask = CommandRunner.TryRunAsync("swaymsg", ["-t", "get_tree", "-r"], 4000, cancellationToken);
        await Task.WhenAll(versionTask, treeTask);
        var stdout = treeTask.Result?.StdOut;
        var result = new List<WindowInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return result;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            Walk(doc.RootElement, null, null, result);
        }
        catch { /* ignore */ }
        return result;
    }

    public override async Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
    {
        var windows = await GetWindowsAsync(cancellationToken);
        return windows.FirstOrDefault(w => w.IsFocused);
    }

    public override async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var result = await CommandRunner.TryRunAsync(
            "swaymsg", [$"[con_id={window.NativeId}]", "focus"], 3000, cancellationToken);
        return result?.Success == true;
    }

    public override async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var result = await CommandRunner.TryRunAsync(
            "swaymsg", [$"[con_id={window.NativeId}]", "kill"], 3000, cancellationToken);
        return result?.Success == true;
    }

    public override async Task<bool> HighlightAsync(WindowInfo window, int durationMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(window.NativeId)) return false;
        var enable = await CommandRunner.TryRunAsync(
            "swaymsg", [$"[con_id={window.NativeId}]", "urgent", "enable"], 3000, cancellationToken);
        if (enable?.Success != true) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(durationMs, CancellationToken.None);
                await CommandRunner.TryRunAsync(
                    "swaymsg", [$"[con_id={window.NativeId}]", "urgent", "disable"], 3000, CancellationToken.None);
            }
            catch { /* best effort */ }
        });
        return true;
    }

    private static void Walk(JsonElement node, string? workspace, string? output, List<WindowInfo> result)
    {
        var type = GetString(node, "type");
        if (type == "workspace") workspace = GetString(node, "name") ?? workspace;
        if (type == "output") output = GetString(node, "name") ?? output;

        var hasWindow = node.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number;
        var appId = GetString(node, "app_id");
        var hasProps = node.TryGetProperty("window_properties", out var props) && props.ValueKind == JsonValueKind.Object;
        if (!hasWindow && string.IsNullOrWhiteSpace(appId) && !hasProps)
        {
            if (node.TryGetProperty("nodes", out var childNodes) && childNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in childNodes.EnumerateArray()) Walk(child, workspace, output, result);
            }
            if (node.TryGetProperty("floating_nodes", out var floatingNodes) && floatingNodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in floatingNodes.EnumerateArray()) Walk(child, workspace, output, result);
            }
            return;
        }

        var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : 0;
        var pid = hasWindow ? pidEl.GetInt32() : (int?)null;
        var shell = GetString(node, "shell");
        var x11Window = node.TryGetProperty("window", out var winEl) && winEl.ValueKind == JsonValueKind.Number
            ? winEl.GetUInt64()
            : (ulong?)null;
        var isXWayland = string.Equals(shell, "xwayland", StringComparison.OrdinalIgnoreCase)
                         || (x11Window is > 0 && string.IsNullOrWhiteSpace(appId));

        var title = GetString(node, "name");
        string? cls = appId;
        string? instance = null;
        if (props.ValueKind == JsonValueKind.Object)
        {
            title = GetString(props, "title") ?? title;
            cls = GetString(props, "class") ?? cls;
            instance = GetString(props, "instance");
        }

        var (x, y, width, height) = ReadRect(node);
        var focused = node.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True;
        var visible = !(node.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.False);
        var floating = type == "floating_con" || (node.TryGetProperty("floating", out var fl) && fl.ValueKind == JsonValueKind.True);

        var info = new WindowInfo
        {
            Id = $"sway:{nodeId}",
            SourceId = "sway",
            SourceName = "sway",
            NativeId = nodeId.ToString(CultureInfo.InvariantCulture),
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.L("(无标题)", "(untitled)") : title!,
            AppId = cls,
            WmClass = cls,
            Pid = pid,
            Backend = isXWayland ? WindowBackend.X11 : WindowBackend.Wayland,
            IsXWayland = isXWayland,
            IsFocused = focused,
            IsMapped = visible,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            WorkspaceName = workspace,
            OutputName = output,
            X11WindowId = isXWayland && x11Window is > 0 ? x11Window : null,
            RawJson = node.GetRawText(),
        };

        info.Extra["节点 ID"] = nodeId.ToString(CultureInfo.InvariantCulture);
        info.Extra["Shell"] = shell ?? (isXWayland ? "xwayland" : "xdg_shell");
        info.Extra["App ID / Class"] = cls ?? "—";
        if (instance is not null) info.Extra["实例名"] = instance;
        if (x11Window is > 0) info.Extra["X11 窗口 ID"] = $"0x{x11Window:x}";
        info.Extra["浮动"] = floating ? "是" : "否";
        if (workspace is not null) info.Extra["工作区"] = workspace;
        if (output is not null) info.Extra["输出"] = output;
        result.Add(info);

        foreach (var key in new[] { "nodes", "floating_nodes" })
        {
            if (!node.TryGetProperty(key, out var children) || children.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in children.EnumerateArray()) Walk(child, workspace, output, result);
        }
    }

    private static (int? X, int? Y, int? Width, int? Height) ReadRect(JsonElement node)
    {
        if (!node.TryGetProperty("rect", out var rect) || rect.ValueKind != JsonValueKind.Object)
            return (null, null, null, null);
        return (
            rect.TryGetProperty("x", out var x) ? x.GetInt32() : null,
            rect.TryGetProperty("y", out var y) ? y.GetInt32() : null,
            rect.TryGetProperty("width", out var w) ? w.GetInt32() : null,
            rect.TryGetProperty("height", out var h) ? h.GetInt32() : null);
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null) return;
        var result = await CommandRunner.TryRunAsync("swaymsg", ["-t", "get_version", "-r"], 2000, cancellationToken);
        if (result?.Success != true) return;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            _version = doc.RootElement.TryGetProperty("human_readable", out var hr)
                ? hr.GetString()
                : $"{doc.RootElement.GetProperty("major").GetInt32()}.{doc.RootElement.GetProperty("minor").GetInt32()}";
        }
        catch { /* ignore */ }
    }
}
