using TuxViewer.Models;
using TuxViewer.Services.Providers;

namespace TuxViewer.Services;

public sealed class ProviderRegistry : IDisposable
{
    private readonly List<IWindowProvider> _providers = [];
    private readonly Dictionary<string, WindowInfo> _pinned = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public X11WindowProvider? X11 { get; private set; }
    public IReadOnlyList<IWindowProvider> Providers => _providers;
    public SessionInfo Session { get; private set; } = new();
    public bool IsWaylandSession { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        IsWaylandSession =
            string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

        _providers.Add(new NiriProvider());
        _providers.Add(new SwayProvider());
        _providers.Add(new HyprlandProvider());
        _providers.Add(new GnomeProvider());
        _providers.Add(new KWinProvider());
        _providers.Add(new WlrootsProvider());

        foreach (var provider in _providers)
        {
            try
            {
                await provider.InitializeAsync(cancellationToken);
            }
            catch { /* provider remains unavailable */ }
        }

        X11 = new X11WindowProvider(new X11Inspector(IsWaylandSession), IsWaylandSession);
        _providers.Add(X11);

        UpdateSession();
    }

    public IReadOnlyList<ProviderStatus> GetStatuses()
        => _providers.Select(p => p.ToStatus()).ToList();

    public void UpdateSession()
    {
        var wm = X11?.IsAvailable == true ? X11.Inspector.GetWmName() : null;
        Session = SessionDetector.Detect(GetStatuses(), wm);
    }

    public async Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken, bool includeRawX11 = false)
    {
        var tasks = _providers
            .Where(p => p.IsAvailable && p.CanList)
            .Select(async provider =>
            {
                try
                {
                    return await provider.GetWindowsAsync(cancellationToken);
                }
                catch
                {
                    return (IReadOnlyList<WindowInfo>)[];
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks);
        var all = new List<WindowInfo>();
        foreach (var group in results) all.AddRange(group);
        all.RemoveAll(w => w.Title == "TuxViewer Highlight");

        lock (_lock)
        {
            foreach (var pinned in _pinned.Values)
            {
                if (!all.Any(w => w.Id == pinned.Id)) all.Add(pinned);
            }
        }

        foreach (var window in all)
            window.IsOwnWindow = window.Pid == Environment.ProcessId;

        if (IsWaylandSession) MergeXWayland(all);

        var ordered = all
            .OrderByDescending(w => w.IsFocused)
            .ThenBy(w => SourceRank(w.SourceId))
            .ThenBy(w => w.IsOwnWindow)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!includeRawX11)
            ordered = ordered.Where(w => !w.MergedIntoWayland).ToList();

        UpdateSession();
        return ordered;
    }

    public async Task<PickResult> PickAsync(
        IProgress<PickProgress>? progress,
        CancellationToken cancellationToken,
        PickMode mode = PickMode.Click,
        bool x11Only = false)
    {
        if (x11Only)
        {
            if (X11?.IsAvailable != true)
                return new PickResult { Message = "X11 / XWayland 不可用" };
            var picked = await X11.PickInteractiveAsync(progress, mode, cancellationToken);
            return await FinishPickAsync(picked, "X11", progress, cancellationToken);
        }

        var interactive = new List<IWindowProvider>();
        if (Session.IsX11Session && X11 is not null) interactive.Add(X11);
        if (IsWaylandSession)
        {
            AddIfAvailable(interactive, "niri");
            AddIfAvailable(interactive, "kwin");
        }

        foreach (var provider in interactive)
        {
            var picked = await provider.PickInteractiveAsync(progress, mode, cancellationToken);
            if (picked is not null) return await FinishPickAsync(picked, provider.Name, progress, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return new PickResult { Cancelled = true, Message = "已取消拾取" };
            // A native picker returned no window: usually a compositor-side cancel.
            if (provider.Pick == PickCapability.Interactive)
                return new PickResult { Cancelled = true, Message = "已取消拾取" };
        }

        var cursorProviders = _providers.Where(p => p.IsAvailable && p.Pick == PickCapability.Cursor).ToList();
        foreach (var provider in cursorProviders)
        {
            await CountdownAsync(progress, mode == PickMode.Drag
                ? "按住并拖动准星到目标窗口上方，松手后读取"
                : "请将鼠标指针移到目标窗口上方", cancellationToken);
            var picked = await provider.PickAtCursorAsync(progress, cancellationToken);
            if (picked is not null) return await FinishPickAsync(picked, provider.Name, progress, cancellationToken);
        }

        var focusedProviders = _providers.Where(p => p.IsAvailable && p.Pick == PickCapability.Focused).ToList();
        foreach (var provider in focusedProviders)
        {
            await CountdownAsync(progress, mode == PickMode.Drag
                ? "拖动准星到目标窗口，并保持该窗口获得焦点"
                : "请点击目标窗口使其获得焦点（不要切回本窗口）", cancellationToken);
            var picked = await provider.GetFocusedWindowAsync(cancellationToken);
            if (picked is not null) return await FinishPickAsync(picked, provider.Name, progress, cancellationToken);
        }

        if (X11?.IsAvailable == true)
        {
            progress?.Report(new PickProgress { Message = "回退到 X11 十字准星（仅能选择 X11 / XWayland 窗口）" });
            var picked = await X11.PickInteractiveAsync(progress, mode, cancellationToken);
            if (picked is not null) return await FinishPickAsync(picked, "X11", progress, cancellationToken);
        }

        return new PickResult
        {
            Cancelled = cancellationToken.IsCancellationRequested,
            Message = cancellationToken.IsCancellationRequested
                ? "已取消拾取"
                : "没有可用的拾取后端。请查看“诊断”页。",
        };
    }

    public async Task<PickResult> PickFocusedAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        var providers = _providers
            .Where(p => p.IsAvailable && p.Pick is PickCapability.Focused or PickCapability.Cursor or PickCapability.Interactive)
            .OrderByDescending(p => p.Pick == PickCapability.Interactive)
            .ToList();
        foreach (var provider in providers)
        {
            var picked = await provider.GetFocusedWindowAsync(cancellationToken);
            if (picked is not null) return await FinishPickAsync(picked, provider.Name, progress, cancellationToken);
        }
        return new PickResult { Message = "无法读取当前焦点窗口" };
    }

    private async Task<PickResult> FinishPickAsync(
        WindowInfo? picked, string providerName, IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        if (picked is null) return new PickResult { Message = "未选择窗口" };
        lock (_lock) _pinned[picked.Id] = picked;
        progress?.Report(new PickProgress { Message = $"{providerName}：已选择 {picked.Title}", HoverWindow = picked });
        return new PickResult { Window = picked, Message = $"已选择 {picked.Title}" };
    }

    private static async Task CountdownAsync(IProgress<PickProgress>? progress, string instruction, CancellationToken cancellationToken)
    {
        for (var i = 3; i >= 1; i--)
        {
            progress?.Report(new PickProgress
            {
                Message = $"{instruction} · {i} 秒后读取",
                Countdown = i,
            });
            await Task.Delay(1000, cancellationToken);
        }
    }

    private void AddIfAvailable(List<IWindowProvider> list, string id)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == id && p.IsAvailable);
        if (provider is not null) list.Add(provider);
    }

    public async Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == window.SourceId && p.CanFocus);
        if (provider is not null && await provider.FocusAsync(window, cancellationToken)) return true;
        if (window.LinkedX11WindowId is not null && X11 is not null)
            return await X11.FocusAsync(window, cancellationToken);
        return false;
    }

    public async Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == window.SourceId && p.CanClose);
        if (provider is not null && await provider.CloseAsync(window, cancellationToken)) return true;
        if (window.LinkedX11WindowId is not null && X11 is not null)
            return await X11.CloseAsync(window, cancellationToken);
        return false;
    }

    public async Task<bool> HighlightAsync(WindowInfo window, int durationMs, CancellationToken cancellationToken)
    {
        // X11/XWayland windows can get a precise shaped-border overlay, even when the
        // compositor toplevel is a native Wayland window (the X11 id was merged in).
        var x11Id = window.X11WindowId ?? window.LinkedX11WindowId;
        if (x11Id is not null && X11?.IsAvailable == true)
        {
            X11.Inspector.Highlight(x11Id.Value, durationMs);
            return true;
        }

        var provider = _providers.FirstOrDefault(p => p.Id == window.SourceId);
        if (provider is not null && await provider.HighlightAsync(window, durationMs, cancellationToken)) return true;
        return false;
    }

    private static void MergeXWayland(List<WindowInfo> all)
    {
        var x11Windows = all
            .Where(w => w.SourceId == "x11" && w.X11WindowId is not null)
            .ToList();
        if (x11Windows.Count == 0) return;

        var claimed = new HashSet<ulong>();
        var compositorWindows = all
            .Where(w => w.SourceId != "x11")
            .ToList();

        foreach (var window in compositorWindows)
        {
            WindowInfo? match = null;
            if (window.X11WindowId is ulong directId)
                match = x11Windows.FirstOrDefault(x => x.X11WindowId == directId);

            if (match is null)
            {
                var candidates = x11Windows.Where(x => !claimed.Contains(x.X11WindowId!.Value)).ToList();
                var title = Normalize(window.Title);
                match = candidates.FirstOrDefault(x => Normalize(x.Title) == title && title.Length > 0);
                if (match is null && !string.IsNullOrWhiteSpace(window.AppId))
                {
                    match = candidates.FirstOrDefault(x =>
                        string.Equals(x.WmClass, window.AppId, StringComparison.OrdinalIgnoreCase));
                }
                if (match is null && window.Pid is not null)
                {
                    var byPid = candidates.Where(x => x.Pid == window.Pid).ToList();
                    if (byPid.Count == 1) match = byPid[0];
                }
            }

            if (match is null) continue;
            claimed.Add(match.X11WindowId!.Value);
            window.IsXWayland = true;
            window.Backend = WindowBackend.X11;
            window.LinkedX11WindowId = match.X11WindowId;
            if (match.Pid is > 0) window.Pid = match.Pid;
            window.Extra["X11 窗口 ID"] = $"0x{match.X11WindowId:x}";
            match.MergedIntoWayland = true;
        }
    }

    private static string Normalize(string? value)
        => string.Join(' ', (value ?? "").Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim().ToLowerInvariant();

    private static int SourceRank(string source) => source switch
    {
        "niri" => 0,
        "sway" => 1,
        "hyprland" => 2,
        "gnome" => 3,
        "kwin" => 4,
        "wlroots" => 5,
        "x11" => 6,
        _ => 9,
    };

    public void Dispose()
    {
        X11?.Inspector.Dispose();
    }
}
