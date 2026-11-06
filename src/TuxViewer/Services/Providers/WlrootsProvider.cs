using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

/// <summary>
/// Generic fallback for wlroots compositors (labwc, wayfire, river, ...) that provide
/// wlr-foreign-toplevel-management. The protocol exposes only app_id/title, no PID or geometry.
/// </summary>
public sealed class WlrootsProvider : WindowProviderBase
{
    private bool _probed;
    private bool _available;
    private string? _reason;

    public override string Id => "wlroots";
    public override string Name => "wlroots (wlrctl)";
    public override bool IsAvailable => _available;
    public override string Detail => _available
        ? "外接 wlrctl：仅窗口列表（无 PID / 几何，合成器协议限制）"
        : _reason ?? (_probed ? "未检测到 wlrctl 或 wlr-foreign-toplevel 支持" : "未初始化");
    public override PickCapability Pick => PickCapability.None;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _probed = true;
        var otherProvider =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIRI_SOCKET")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWAYSOCK")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")) ||
            (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase) ||
            (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);
        if (otherProvider)
        {
            _reason = "已由 niri / Sway / Hyprland / GNOME / KDE 原生后端接管";
            return;
        }
        if (!CommandRunner.Exists("wlrctl"))
        {
            _reason = "未安装 wlrctl（可选）";
            return;
        }

        var result = await CommandRunner.TryRunAsync("wlrctl", ["toplevel", "list"], 3000, cancellationToken);
        _available = result?.Success == true;
        if (!_available) _reason = "合成器未暴露 wlr-foreign-toplevel-management";
    }

    public override async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
    {
        var result = await CommandRunner.TryRunAsync("wlrctl", ["toplevel", "list"], 4000, cancellationToken);
        if (result?.Success != true) return [];

        var windows = new List<WindowInfo>();
        var index = 0;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            var appId = separator > 0 ? line[..separator].Trim() : "";
            var title = separator > 0 ? line[(separator + 2)..].Trim() : line.Trim();
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(appId)) continue;
            var id = $"wlr:{index++}";
            var info = new WindowInfo
            {
                Id = id,
                SourceId = "wlroots",
                SourceName = "wlrctl",
                NativeId = title.Length > 0 ? title : appId,
                Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title,
                AppId = appId.Length > 0 ? appId : null,
                WmClass = appId.Length > 0 ? appId : null,
                Backend = WindowBackend.Wayland,
                RawJson = "{\"provider\":\"wlrctl\",\"line\":\"" + line.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}",
            };
            info.Extra["App ID"] = appId.Length > 0 ? appId : "—";
            windows.Add(info);
        }
        return windows;
    }

    public override async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
        => await MatchAsync(window, "focus", cancellationToken);

    public override async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
        => await MatchAsync(window, "close", cancellationToken);

    private static async Task<bool> MatchAsync(WindowInfo window, string action, CancellationToken cancellationToken)
    {
        var match = !string.IsNullOrWhiteSpace(window.AppId)
            ? $"app_id:{window.AppId}"
            : $"title:{window.Title}";
        var result = await CommandRunner.TryRunAsync("wlrctl", ["window", action, match], 3000, cancellationToken);
        return result?.Success == true;
    }
}
