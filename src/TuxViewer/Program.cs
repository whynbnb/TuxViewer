using System.Text.Json;
using Avalonia;
using TuxViewer.Models;
using TuxViewer.Services;

namespace TuxViewer;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("""
                TuxViewer — Linux 桌面环境 / 窗口检视器

                用法:
                  TuxViewer [--select <id|app_id|标题>] [--capture] [--tree] [--group 0-3]
                            [--record] [--record-selected] [--atspi-load] [--hotkey]
                            [--gui-pick <click|drag>] [--gui-pick-x11 <click|drag>]
                            [--tab <overview|process|x11|wayland|env|raw|events|screenshot|rules|xrecord|atspi|hotkey|control|report|outputs|diagnostics>]
                            [--new-instance]
                  TuxViewer --diagnose [--json] [--detail <id|app_id|标题>] [--highlight]
                  TuxViewer --pick-x11 <click|drag> [--json]
                  TuxViewer --screenshot <id|app_id|标题> [--out <file.png>]
                  TuxViewer --export <html|md|json> <id|app_id|标题> [--out <file>]
                  TuxViewer --snapshot <id|app_id|标题> [--name <名称>] [--out <file.json>]
                  TuxViewer --install-gnome-extension

                GUI 模式下默认只允许一个实例；再次启动会把参数转发给已运行实例。
                使用 --new-instance 可强制打开新窗口。--gui-pick / --gui-pick-x11
                适合放在合成器快捷键中，唤起已运行实例并开始拾取。

                环境变量:
                  TUXVIEWER_BACKEND=wayland|x11   强制 Avalonia 窗口后端（默认 Wayland 优先，失败回退 X11）
                  TUXVIEWER_X11_DEBUG=1           输出 X11 调试日志

                快捷键:
                  F2 点击拾取   F3 拖动拾取   Shift+F2/F3 强制 X11   F5 刷新   Esc 取消
                """);
            return 0;
        }

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
            return RunDiagnoseAsync(args).GetAwaiter().GetResult();

        if (args.Contains("--pick-x11", StringComparer.OrdinalIgnoreCase))
            return RunPickX11Async(args).GetAwaiter().GetResult();

        if (args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase))
            return RunScreenshotAsync(args).GetAwaiter().GetResult();

        if (args.Contains("--export", StringComparer.OrdinalIgnoreCase))
            return RunExportAsync(args).GetAwaiter().GetResult();

        if (args.Contains("--snapshot", StringComparer.OrdinalIgnoreCase))
            return RunSnapshotAsync(args).GetAwaiter().GetResult();

        if (args.Contains("--install-gnome-extension", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(GnomeExtensionInstaller.InstallAsync(CancellationToken.None).GetAwaiter().GetResult());
            return 0;
        }

        SingleInstanceService? instance = null;
        try
        {
            instance = SingleInstanceService.Acquire(args, out var forwarded);
            if (forwarded) return 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Single instance startup failed: {ex.Message}");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        finally
        {
            instance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // TUXVIEWER_BACKEND=x11 forces the X11 backend (useful on XWayland-only setups);
        // TUXVIEWER_BACKEND=wayland forces native Wayland; default prefers Wayland with fallback.
        var backend = Environment.GetEnvironmentVariable("TUXVIEWER_BACKEND")?.Trim().ToLowerInvariant();
        return backend switch
        {
            "x11" => builder,
            "wayland" => builder.UseWayland(),
            _ => builder.UseWaylandWithFallback(),
        };
    }

    private static async Task<int> RunDiagnoseAsync(string[] args)
    {
        IconService.Enabled = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var service = new InspectorService();
        try
        {
            await service.InitializeAsync(cts.Token);
            var windows = await service.RefreshAsync(cts.Token);
            var protocols = await service.GetWaylandProtocolsAsync(cts.Token);

            if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    session = new
                    {
                        type = service.Registry.Session.SessionType,
                        desktop = service.Registry.Session.Desktop,
                        compositor = service.Registry.Session.Compositor,
                        compositor_version = service.Registry.Session.CompositorVersion,
                        wayland_display = service.Registry.Session.WaylandDisplay,
                        x11_display = service.Registry.Session.X11Display,
                        x11_wm = service.Registry.Session.X11WmName,
                    },
                    providers = service.Registry.GetStatuses(),
                    wayland_protocols = protocols,
                    windows,
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                return 0;
            }

            Console.WriteLine($"TuxViewer diagnostics");
            Console.WriteLine($"  session      : {service.Registry.Session.SessionType}");
            Console.WriteLine($"  desktop      : {service.Registry.Session.Desktop}");
            Console.WriteLine($"  compositor   : {service.Registry.Session.Compositor} {service.Registry.Session.CompositorVersion}");
            Console.WriteLine($"  wayland      : {service.Registry.Session.WaylandDisplay}");
            Console.WriteLine($"  x11          : {service.Registry.Session.X11Display} (WM: {service.Registry.Session.X11WmName})");
            Console.WriteLine("  providers:");
            foreach (var status in service.Registry.GetStatuses())
                Console.WriteLine($"    [{(status.Available ? "x" : " ")}] {status.Name,-22} {status.Detail}");
            Console.WriteLine($"  windows      : {windows.Count}");
            foreach (var window in windows)
            {
                Console.WriteLine(
                    $"    {window.BackendShort,-3} pid={window.Pid?.ToString() ?? "-",-7} " +
                    $"{window.GeometryText,-22} {window.SourceName,-10} {window.Title}");
            }

            var detailArg = GetArg(args, "--detail");
            if (detailArg is not null)
            {
                var window = windows.FirstOrDefault(w =>
                                 string.Equals(w.Id, detailArg, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(w.NativeId, detailArg, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(w.AppId, detailArg, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(w.Title, detailArg, StringComparison.OrdinalIgnoreCase))
                             ?? windows.FirstOrDefault(w => w.Title.Contains(detailArg, StringComparison.OrdinalIgnoreCase));
                if (window is null)
                {
                    Console.Error.WriteLine($"No window matched '{detailArg}'");
                    return 2;
                }
                var inspection = await service.InspectAsync(window, cts.Token);
                Console.WriteLine(inspection.RawJson);
                if (args.Contains("--highlight", StringComparer.OrdinalIgnoreCase))
                {
                    service.Highlight(window);
                    await Task.Delay(2500, cts.Token);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunPickX11Async(string[] args)
    {
        IconService.Enabled = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var service = new InspectorService();
        var modeText = GetArg(args, "--pick-x11") ?? "click";
        var mode = string.Equals(modeText, "drag", StringComparison.OrdinalIgnoreCase)
            ? PickMode.Drag
            : PickMode.Click;

        await service.InitializeAsync(cts.Token);
        if (service.Registry.X11?.IsAvailable != true)
        {
            Console.Error.WriteLine("X11 / XWayland 不可用");
            return 2;
        }

        Console.Error.WriteLine(mode == PickMode.Drag
            ? "X11 拖动拾取：按住左键拖动，松开选择；右键 / Esc 取消"
            : "X11 点击拾取：单选目标窗口；右键 / Esc 取消");
        var progress = new Progress<PickProgress>(p => Console.Error.WriteLine(p.Message));
        var result = await service.PickAsync(progress, cts.Token, mode, x11Only: true);
        if (result.Window is null)
        {
            Console.Error.WriteLine(result.Message ?? "未选择窗口");
            return 3;
        }

        var inspection = await service.InspectAsync(result.Window, cts.Token);
        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(inspection.RawJson);
        }
        else
        {
            Console.WriteLine(inspection.Summary);
        }
        return 0;
    }

    private static async Task<int> RunScreenshotAsync(string[] args)
    {
        IconService.Enabled = false;
        var target = GetArg(args, "--screenshot");
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法：TuxViewer --screenshot <id|app_id|标题> [--out <file.png>]");
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var service = new InspectorService();
        await service.InitializeAsync(cts.Token);
        var windows = await service.RefreshAsync(cts.Token);
        var window = windows.FirstOrDefault(w =>
                         string.Equals(w.Id, target, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(w.NativeId, target, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(w.AppId, target, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(w.Title, target, StringComparison.OrdinalIgnoreCase))
                     ?? windows.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (window is null)
        {
            Console.Error.WriteLine($"No window matched '{target}'");
            return 3;
        }

        if (Environment.GetEnvironmentVariable("TUXVIEWER_SHOT_DEBUG") == "1")
            Console.Error.WriteLine(
                $"window={window.Id} source={window.SourceId} native={window.NativeId} " +
                $"x11={window.X11WindowId} linked={window.LinkedX11WindowId} " +
                $"x11Available={service.Registry.X11?.IsAvailable}");

        var result = await service.CaptureScreenshotAsync(window, cts.Token);
        if (!result.Success)
        {
            Console.Error.WriteLine(result.Message);
            return 4;
        }

        var output = GetArg(args, "--out") ?? "tuxviewer-screenshot.png";
        await File.WriteAllBytesAsync(output, result.Png!, cts.Token);
        Console.WriteLine($"{result.Message} · {result.Width}×{result.Height} · {result.Source}");
        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }

    private static async Task<int> RunExportAsync(string[] args)
    {
        IconService.Enabled = false;
        var index = Array.FindIndex(args, a => string.Equals(a, "--export", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= args.Length)
        {
            Console.Error.WriteLine("用法：TuxViewer --export <html|md|json> <id|app_id|标题> [--out <file>]");
            return 2;
        }

        var format = args[index + 1].ToLowerInvariant();
        var target = args[index + 2];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var service = new InspectorService();
        await service.InitializeAsync(cts.Token);
        var window = FindWindow(await service.RefreshAsync(cts.Token), target);
        if (window is null)
        {
            Console.Error.WriteLine($"No window matched '{target}'");
            return 3;
        }

        var inspection = await service.InspectAsync(window, cts.Token);
        var output = GetArg(args, "--out") ?? format switch
        {
            "html" => "tuxviewer-report.html",
            "md" or "markdown" => "tuxviewer-report.md",
            _ => "tuxviewer-report.json",
        };

        if (format is "html")
        {
            var screenshot = await service.CaptureScreenshotAsync(window, cts.Token);
            var dataUri = screenshot.Success ? "data:image/png;base64," + Convert.ToBase64String(screenshot.Png!) : null;
            var html = ReportService.BuildHtml(window, inspection.Process, inspection.X11, dataUri, inspection.RawJson);
            await File.WriteAllTextAsync(output, html, cts.Token);
        }
        else if (format is "md" or "markdown")
        {
            var screenshot = await service.CaptureScreenshotAsync(window, cts.Token);
            var imageName = Path.GetFileNameWithoutExtension(output) + ".png";
            var markdown = ReportService.BuildMarkdown(
                window, inspection.Process, inspection.X11,
                screenshot.Success ? imageName : null, inspection.RawJson);
            await File.WriteAllTextAsync(output, markdown, cts.Token);
            if (screenshot.Success)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? Directory.GetCurrentDirectory();
                await File.WriteAllBytesAsync(Path.Combine(directory, imageName), screenshot.Png!, cts.Token);
            }
        }
        else
        {
            await File.WriteAllTextAsync(output, inspection.RawJson, cts.Token);
        }

        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }

    private static async Task<int> RunSnapshotAsync(string[] args)
    {
        IconService.Enabled = false;
        var target = GetArg(args, "--snapshot");
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("用法：TuxViewer --snapshot <id|app_id|标题> [--name <名称>] [--out <file.json>]");
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var service = new InspectorService();
        await service.InitializeAsync(cts.Token);
        var window = FindWindow(await service.RefreshAsync(cts.Token), target);
        if (window is null)
        {
            Console.Error.WriteLine($"No window matched '{target}'");
            return 3;
        }

        var inspection = await service.InspectAsync(window, cts.Token);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["概览/标题"] = window.Title,
            ["概览/应用 ID"] = window.AppId ?? window.WmClass ?? "—",
            ["概览/后端"] = window.BackendLabel,
            ["概览/几何"] = window.GeometryText,
            ["概览/工作区"] = window.WorkspaceName ?? window.WorkspaceId?.ToString() ?? "—",
            ["概览/输出"] = window.OutputName ?? "—",
            ["概览/焦点"] = window.IsFocused ? "是" : "否",
        };
        if (inspection.Process is { } process)
        {
            values["进程/进程名"] = process.Name ?? "—";
            values["进程/PID"] = process.Pid.ToString();
            values["进程/可执行文件"] = process.Exe ?? "—";
            values["进程/启动命令"] = process.Cmdline ?? "—";
            values["进程/工具包"] = process.Toolkit.DisplayName;
        }
        if (inspection.X11 is { } x11)
        {
            values["X11/窗口 ID"] = x11.IdHex;
            values["X11/实例 / 类"] = $"{x11.InstanceName} / {x11.ClassName}";
            values["X11/几何"] = $"{x11.Width}×{x11.Height} @ {x11.X},{x11.Y}";
            foreach (var property in x11.Properties) values[$"属性/{property.Name}"] = property.Value;
        }

        var screenshot = await service.CaptureScreenshotAsync(window, cts.Token);
        var snapshot = new WindowSnapshot
        {
            Name = GetArg(args, "--name") ?? $"{window.Title} {DateTime.Now:MM-dd HH:mm}",
            WindowId = window.Id,
            Title = window.Title,
            AppId = window.AppId,
            Pid = window.Pid,
            Backend = window.BackendLabel,
            Geometry = window.GeometryText,
            Summary = inspection.Summary,
            RawJson = inspection.RawJson,
            Values = values,
            ScreenshotBase64 = screenshot.Success ? Convert.ToBase64String(screenshot.Png!) : null,
        };
        await SnapshotService.SaveAsync(snapshot, cts.Token);
        Console.WriteLine(Path.Combine(SnapshotService.Directory, $"{snapshot.Id}.json"));
        return 0;
    }

    private static WindowInfo? FindWindow(IReadOnlyList<WindowInfo> windows, string target)
        => windows.FirstOrDefault(w =>
               string.Equals(w.Id, target, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(w.NativeId, target, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(w.AppId, target, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(w.Title, target, StringComparison.OrdinalIgnoreCase))
           ?? windows.FirstOrDefault(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
