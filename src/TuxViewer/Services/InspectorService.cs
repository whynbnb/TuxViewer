using System.Text;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services;

public sealed class InspectorService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ProviderRegistry Registry { get; } = new();
    public ProcessInspector ProcessInspector { get; } = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Registry.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<WindowInfo>> RefreshAsync(CancellationToken cancellationToken, bool includeRawX11 = false)
        => Registry.GetWindowsAsync(cancellationToken, includeRawX11);

    public Task<PickResult> PickAsync(
        IProgress<PickProgress>? progress,
        CancellationToken cancellationToken,
        PickMode mode = PickMode.Click,
        bool x11Only = false)
        => Registry.PickAsync(progress, cancellationToken, mode, x11Only);

    public Task<PickResult> PickFocusedAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
        => Registry.PickFocusedAsync(progress, cancellationToken);

    public async Task<InspectionResult> InspectAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        var process = window.Pid is > 0
            ? await Task.Run(() => ProcessInspector.Inspect(window.Pid.Value), cancellationToken)
            : null;

        X11WindowDetails? x11 = null;
        var x11Id = window.LinkedX11WindowId ?? window.X11WindowId;
        if (x11Id is not null && Registry.X11?.IsAvailable == true)
            x11 = await Task.Run(() => Registry.X11.Inspector.GetDetails(x11Id.Value), cancellationToken);

        if (x11 is not null && window.Pid is null && x11.Properties.Count > 0)
        {
            var pidProperty = x11.Properties.FirstOrDefault(p => p.Name == "_NET_WM_PID");
            if (pidProperty is not null && int.TryParse(pidProperty.Value, out var pid) && pid > 0)
                process ??= await Task.Run(() => ProcessInspector.Inspect(pid), cancellationToken);
        }

        var rawJson = BuildRawJson(window, process, x11);
        var summary = BuildSummary(window, process, x11);

        return new InspectionResult
        {
            Window = window,
            Process = process,
            X11 = x11,
            RawJson = rawJson,
            Summary = summary,
        };
    }

    public async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        var result = await Registry.FocusAsync(window, cancellationToken);
        return result;
    }

    public async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
        => await Registry.CloseAsync(window, cancellationToken);

    public Task<bool> HighlightAsync(WindowInfo window, int durationMs, CancellationToken cancellationToken)
        => Registry.HighlightAsync(window, durationMs, cancellationToken);

    public bool Highlight(WindowInfo window)
    {
        var id = window.LinkedX11WindowId ?? window.X11WindowId;
        if (id is null || Registry.X11?.IsAvailable != true) return false;
        Registry.X11.Inspector.Highlight(id.Value);
        return true;
    }

    public void HideHighlight() => Registry.X11?.Inspector.HideHighlight();

    public Task<IReadOnlyList<string>> GetWaylandProtocolsAsync(CancellationToken cancellationToken)
        => SessionDetector.GetWaylandProtocolsAsync(cancellationToken);

    public static string BuildRawJson(WindowInfo window, ProcessSnapshot? process, X11WindowDetails? x11)
    {
        object? nativeRaw = null;
        if (!string.IsNullOrWhiteSpace(window.RawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(window.RawJson);
                nativeRaw = doc.RootElement.Clone();
            }
            catch
            {
                nativeRaw = window.RawJson;
            }
        }

        var payload = new
        {
            inspector = "TuxViewer",
            inspected_at = DateTimeOffset.Now.ToString("O"),
            window = new
            {
                id = window.Id,
                native_id = window.NativeId,
                source = window.SourceName,
                title = window.Title,
                app_id = window.AppId,
                wm_class = window.WmClass,
                pid = window.Pid,
                backend = window.Backend.ToString(),
                is_xwayland = window.IsXWayland,
                focused = window.IsFocused,
                mapped = window.IsMapped,
                own_window = window.IsOwnWindow,
                override_redirect = window.IsOverrideRedirect,
                geometry = new { x = window.X, y = window.Y, width = window.Width, height = window.Height },
                workspace = window.WorkspaceName ?? window.WorkspaceId?.ToString(),
                output = window.OutputName,
                x11_window = window.X11WindowId is not null ? $"0x{window.X11WindowId:x}" : null,
                linked_x11_window = window.LinkedX11WindowId is not null ? $"0x{window.LinkedX11WindowId:x}" : null,
                extra = window.Extra,
                native = nativeRaw,
            },
            process = process is null
                ? null
                : new
                {
                    pid = process.Pid,
                    parent_pid = process.ParentPid,
                    name = process.Name,
                    exe = process.Exe,
                    cmdline = process.Cmdline,
                    cwd = process.Cwd,
                    user = process.User,
                    state = process.State,
                    rss_kb = process.RssKb,
                    threads = process.Threads,
                    started_at = process.StartedAt,
                    sandbox = process.Sandbox,
                    toolkit = new
                    {
                        name = process.Toolkit.Name,
                        version = process.Toolkit.Version,
                        display = process.Toolkit.DisplayName,
                        evidence = process.Toolkit.Evidence,
                    },
                    backend = new
                    {
                        display = process.Backend.Display,
                        x11_socket = process.Backend.HasX11Socket,
                        wayland_socket = process.Backend.HasWaylandSocket,
                        dbus_socket = process.Backend.HasDbusSocket,
                        pipewire_socket = process.Backend.HasPipeWireSocket,
                        socket_paths = process.Backend.SocketPaths,
                    },
                    sockets = process.Sockets.Select(s => new { kind = s.Kind, path = s.Path, inode = s.Inode }),
                    environment = process.Environment,
                    libraries = process.KeyLibraries,
                },
            x11 = x11 is null
                ? null
                : new
                {
                    id = x11.IdHex,
                    title = x11.Title,
                    instance = x11.InstanceName,
                    @class = x11.ClassName,
                    geometry = new { x = x11.X, y = x11.Y, width = x11.Width, height = x11.Height },
                    border_width = x11.BorderWidth,
                    depth = x11.Depth,
                    mapped = x11.Mapped,
                    override_redirect = x11.OverrideRedirect,
                    input_only = x11.InputOnly,
                    window_type = x11.WindowType,
                    wm_state = x11.WmState,
                    transient_for = x11.TransientFor,
                    protocols = x11.Protocols,
                    wm_normal_hints = x11.WmNormalHints,
                    root = $"0x{x11.RootWindow:x}",
                    parent = $"0x{x11.ParentWindow:x}",
                    event_mask = x11.EventMask,
                    properties = x11.Properties.Select(p => new { p.Name, p.Type, p.Format, p.Value }),
                    children = x11.Children.Select(c => new
                    {
                        id = c.IdHex,
                        title = c.Title,
                        @class = c.ClassName,
                        c.X,
                        c.Y,
                        c.Width,
                        c.Height,
                        c.Mapped,
                        c.OverrideRedirect,
                    }),
                },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildSummary(WindowInfo window, ProcessSnapshot? process, X11WindowDetails? x11)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TuxViewer · Linux 窗口检视");
        sb.AppendLine(new string('─', 34));
        sb.AppendLine($"标题       : {window.Title}");
        sb.AppendLine($"应用 ID    : {window.AppId ?? window.WmClass ?? "—"}");
        sb.AppendLine($"后端       : {window.BackendLabel}{(window.IsXWayland ? "（XWayland 客户端）" : "")}");
        sb.AppendLine($"来源       : {window.SourceName} · {window.Id}");
        sb.AppendLine($"窗口 ID    : {window.NativeId ?? "—"}{(window.X11WindowId is not null ? $" / X11 0x{window.X11WindowId:x}" : "")}");
        sb.AppendLine($"PID        : {window.Pid?.ToString() ?? "—"}{(process?.Name is not null ? $" ({process.Name})" : "")}");
        sb.AppendLine($"几何       : {window.GeometryText}");
        sb.AppendLine($"工作区     : {window.WorkspaceName ?? window.WorkspaceId?.ToString() ?? "—"}");
        sb.AppendLine($"输出       : {window.OutputName ?? "—"}");
        sb.AppendLine($"焦点       : {(window.IsFocused ? "是" : "否")}");
        if (process is not null)
        {
            sb.AppendLine($"工具包     : {process.Toolkit.DisplayName}");
            sb.AppendLine($"客户端连接 : {process.Backend.Display}");
            sb.AppendLine($"可执行文件 : {process.Exe ?? "—"}");
            sb.AppendLine($"启动命令   : {process.Cmdline ?? "—"}");
            if (process.Sandbox is not null) sb.AppendLine($"沙箱       : {process.Sandbox}");
        }
        if (x11 is not null)
        {
            sb.AppendLine($"X11 属性   : {x11.Properties.Count} 项 · 子窗口 {x11.Children.Count} 个");
            sb.AppendLine($"窗口类型   : {x11.WindowType ?? "—"}");
            sb.AppendLine($"WM State   : {x11.WmState ?? "—"}");
        }
        return sb.ToString();
    }

    public void Dispose() => Registry.Dispose();
}

public static class GnomeExtensionInstaller
{
    private const string ExtensionUuid = "tuxviewer@local";

    public static async Task<string> InstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var target = Path.Combine(home, ".local", "share", "gnome-shell", "extensions", ExtensionUuid);
            Directory.CreateDirectory(target);

            var assembly = typeof(GnomeExtensionInstaller).Assembly;
            var resources = assembly.GetManifestResourceNames()
                .Where(n => n.Contains("gnome", StringComparison.OrdinalIgnoreCase) &&
                            (n.EndsWith("extension.js", StringComparison.Ordinal) ||
                             n.EndsWith("metadata.json", StringComparison.Ordinal)))
                .ToList();
            if (resources.Count == 0)
                return "安装包中缺少 GNOME 扩展资源。";

            foreach (var resource in resources)
            {
                var fileName = resource.EndsWith("extension.js", StringComparison.Ordinal)
                    ? "extension.js"
                    : resource.EndsWith("metadata.json", StringComparison.Ordinal)
                        ? "metadata.json"
                        : null;
                if (fileName is null) continue;

                await using var source = assembly.GetManifestResourceStream(resource);
                if (source is null) continue;
                await using var destination = File.Create(Path.Combine(target, fileName));
                await source.CopyToAsync(destination, cancellationToken);
            }

            var result = await CommandRunner.TryRunAsync(
                "gnome-extensions", ["enable", ExtensionUuid], 4000, cancellationToken);
            return result?.Success == true
                ? $"已安装并启用 GNOME 扩展：{target}\nWayland 会话可能需要注销并重新登录后生效。"
                : $"扩展已复制到 {target}\n启用失败：{result?.StdErr ?? "请运行 gnome-extensions enable " + ExtensionUuid}";
        }
        catch (Exception ex)
        {
            return $"安装失败：{ex.Message}";
        }
    }
}
