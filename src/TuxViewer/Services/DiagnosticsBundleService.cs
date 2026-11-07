using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services;

public sealed class DiagnosticsBundleData
{
    public required SessionInfo Session { get; init; }
    public required IReadOnlyList<ProviderStatus> Providers { get; init; }
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
    public WindowInfo? SelectedWindow { get; init; }
    public InspectionResult? Inspection { get; init; }
    public required AppSettings Settings { get; init; }
    public string SettingsPath { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string CountsText { get; init; } = "";
    public IReadOnlyList<InfoRow> SessionRows { get; init; } = [];
    public IReadOnlyList<InfoRow> RealtimeRows { get; init; } = [];
    public byte[]? ScreenshotPng { get; init; }
    public string? EventRecordingPath { get; init; }
}

/// <summary>
/// Builds a self-contained zip for bug reports: session, provider state, logs,
/// window JSON, selected inspection, settings, environment and a screenshot.
/// </summary>
public static class DiagnosticsBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] CommandNames =
    [
        "niri", "swaymsg", "hyprctl", "gdbus", "grim", "gnome-screenshot",
        "xrandr", "xprop", "xwininfo", "xdpyinfo", "python3", "wayland-info",
        "wlrctl", "xdotool", "ydotool", "wtype", "xeyes",
    ];

    private static readonly string[] EnvironmentNames =
    [
        "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "DESKTOP_SESSION",
        "WAYLAND_DISPLAY", "DISPLAY", "NIRI_SOCKET", "SWAYSOCK", "HYPRLAND_INSTANCE_SIGNATURE",
        "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
        "LANG", "LC_ALL", "LC_MESSAGES",
    ];

    public static string DirectoryPath
    {
        get
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "TuxViewer", "diagnostics");
        }
    }

    public static async Task<string> ExportAsync(DiagnosticsBundleData data, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
        var fileName = $"tuxviewer-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip";
        var path = Path.Combine(DirectoryPath, fileName);
        var files = new List<string>
        {
            "README.txt", "manifest.json", "session.json", "providers.json", "windows.json",
            "selected-window.json", "settings.json", "diagnostics.txt", "logs.txt",
            "environment.txt", "commands.txt",
        };
        if (data.ScreenshotPng is { Length: > 0 }) files.Add("screenshot.png");
        if (!string.IsNullOrWhiteSpace(data.EventRecordingPath) && File.Exists(data.EventRecordingPath))
            files.Add("event-recording.jsonl");

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        await AddTextAsync(zip, "README.txt", BuildReadme(data), cancellationToken);
        await AddJsonAsync(zip, "manifest.json", BuildManifest(data, files), cancellationToken);
        await AddJsonAsync(zip, "session.json", data.Session, cancellationToken);
        await AddJsonAsync(zip, "providers.json", data.Providers, cancellationToken);
        await AddJsonAsync(zip, "windows.json", data.Windows, cancellationToken);

        await AddJsonAsync(zip, "selected-window.json", new
        {
            window = data.SelectedWindow,
            summary = data.Inspection?.Summary,
            rawJson = data.Inspection?.RawJson,
            process = data.Inspection?.Process,
            x11 = data.Inspection?.X11,
        }, cancellationToken);

        await AddJsonAsync(zip, "settings.json", data.Settings, cancellationToken);
        await AddTextAsync(zip, "diagnostics.txt", BuildDiagnosticsText(data), cancellationToken);
        await AddTextAsync(zip, "logs.txt", AppLog.Dump(), cancellationToken);
        await AddTextAsync(zip, "environment.txt", BuildEnvironmentText(), cancellationToken);
        await AddTextAsync(zip, "commands.txt", BuildCommandsText(), cancellationToken);

        if (data.ScreenshotPng is { Length: > 0 } png)
            await AddBytesAsync(zip, "screenshot.png", png, cancellationToken);

        if (!string.IsNullOrWhiteSpace(data.EventRecordingPath) && File.Exists(data.EventRecordingPath))
        {
            var bytes = await File.ReadAllBytesAsync(data.EventRecordingPath, cancellationToken);
            await AddBytesAsync(zip, "event-recording.jsonl", bytes, cancellationToken);
        }

        return path;
    }

    private static string BuildReadme(DiagnosticsBundleData data) => $"""
TuxViewer diagnostics bundle / TuxViewer 诊断包
================================================

Created / 生成时间 : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
App version       : {AppVersion()}
Session           : {data.Session.SessionType} · {data.Session.Desktop}
Status            : {data.StatusText}

Files / 文件
------------
README.txt             This file / 本说明
manifest.json          Environment and bundle metadata / 环境与包元数据
session.json           Detected session details / 会话检测结果
providers.json         Window backend availability / 窗口后端可用性
windows.json           All enumerated windows / 全部窗口
selected-window.json   Selected window + inspection JSON / 选中窗口与检视 JSON
settings.json          Persisted TuxViewer settings / 持久化设置
diagnostics.txt        Human-readable session, provider and UI state / 可读诊断信息
logs.txt               Recent runtime log messages / 最近运行日志
environment.txt        Relevant environment variables / 相关环境变量
commands.txt           Presence of useful tools / 常用工具检测
screenshot.png         Selected window screenshot, when available / 可用时的选中窗口截图
event-recording.jsonl  Current event recording, when available / 可用时的事件录制文件

Privacy / 隐私
--------------
This bundle can contain window titles, application IDs, process paths and
working directories. Review it before publishing a bug report.
此诊断包可能包含窗口标题、应用 ID、进程路径和工作目录。发布前请自行检查。
""";

    private static object BuildManifest(DiagnosticsBundleData data, IReadOnlyList<string> files) => new
    {
        bundle = "TuxViewer diagnostics",
        createdAt = DateTimeOffset.Now,
        appVersion = AppVersion(),
        framework = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        language = LocalizationService.LanguageCode,
        sessionType = data.Session.SessionType,
        desktop = data.Session.Desktop,
        compositor = data.Session.Compositor,
        compositorVersion = data.Session.CompositorVersion,
        waylandDisplay = data.Session.WaylandDisplay,
        x11Display = data.Session.X11Display,
        windowCount = data.Windows.Count,
        selectedWindowId = data.SelectedWindow?.Id,
        settingsPath = data.SettingsPath,
        diagnosticsDirectory = DirectoryPath,
        snapshotDirectory = SnapshotService.Directory,
        counts = data.CountsText,
        status = data.StatusText,
        logEntries = AppLog.Snapshot().Count,
        hasScreenshot = data.ScreenshotPng is { Length: > 0 },
        files,
    };

    private static string BuildDiagnosticsText(DiagnosticsBundleData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TuxViewer diagnostics");
        sb.AppendLine($"Generated : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Version   : {AppVersion()}");
        sb.AppendLine($"Status    : {data.StatusText}");
        sb.AppendLine($"Counts    : {data.CountsText}");
        sb.AppendLine();

        sb.AppendLine("== Session ==");
        foreach (var row in data.SessionRows)
            sb.AppendLine($"{row.Label}: {row.Value}");
        sb.AppendLine();

        sb.AppendLine("== Live monitoring ==");
        foreach (var row in data.RealtimeRows)
            sb.AppendLine($"{row.Label}: {row.Value}");
        sb.AppendLine();

        sb.AppendLine("== Providers ==");
        foreach (var provider in data.Providers)
        {
            sb.AppendLine($"{provider.Name} [{provider.Id}]");
            sb.AppendLine($"  available={provider.Available} list={provider.CanList} focus={provider.CanFocus} close={provider.CanClose} pick={provider.Pick}");
            sb.AppendLine($"  detail={provider.Detail}");
        }
        sb.AppendLine();

        sb.AppendLine("== Selected window ==");
        if (data.SelectedWindow is { } window)
        {
            sb.AppendLine($"id={window.Id} native={window.NativeId} title={window.Title}");
            sb.AppendLine($"appId={window.AppId} wmClass={window.WmClass} pid={window.Pid}");
            sb.AppendLine($"backend={window.BackendLabel} geometry={window.GeometryText}");
            sb.AppendLine($"workspace={window.WorkspaceName ?? window.WorkspaceId?.ToString()} output={window.OutputName}");
        }
        else
        {
            sb.AppendLine("(none)");
        }

        return sb.ToString();
    }

    private static string BuildEnvironmentText()
    {
        var sb = new StringBuilder();
        foreach (var name in EnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) sb.AppendLine($"{name}={value}");
        }
        return sb.ToString();
    }

    private static string BuildCommandsText()
    {
        var sb = new StringBuilder();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var command in CommandNames)
        {
            var found = directories
                .Select(directory => Path.Combine(directory, command))
                .FirstOrDefault(File.Exists);
            sb.AppendLine($"{command,-32} {(found is null ? "not found" : found)}");
        }
        return sb.ToString();
    }

    private static string AppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static async Task AddTextAsync(
        ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static Task AddJsonAsync(
        ZipArchive zip, string name, object value, CancellationToken cancellationToken)
        => AddTextAsync(zip, name, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static async Task AddBytesAsync(
        ZipArchive zip, string name, byte[] bytes, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
