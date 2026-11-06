using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using TuxViewer.Models;
using TuxViewer.Services;

namespace TuxViewer.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly InspectorService _service;
    private readonly List<WindowInfo> _allWindows = [];
    private CancellationTokenSource? _inspectCts;
    private CancellationTokenSource? _pickCts;
    private CancellationTokenSource? _refreshCts;
    private WindowInfo? _selectedWindow;
    private bool _suppressInspect;
    private string _filterText = "";
    private string _statusText = "正在初始化…";
    private string _sessionSummary = "检测会话…";
    private string _countsText = "";
    private string _lastUpdatedText = "";
    private bool _isBusy;
    private bool _isPicking;
    private bool _autoRefresh = true;
    private DispatcherTimer? _refreshTimer;
    private bool _hideOwnWindows = true;
    private bool _includeRawX11;
    private bool _includeUnmapped = true;
    private int _backendFilterIndex;
    private int _selectedTabIndex;
    private string _pickBanner = "";
    private int? _pickCountdown;
    private bool _hasSelection;
    private string _rawJson = "";
    private string _nativeJson = "";
    private string _summary = "";
    private X11WindowDetails? _x11Details;
    private ProcessSnapshot? _process;
    private string _gnomeInstallStatus = "";
    private bool _isRefreshing;
    private bool _syncingWindows;

    public MainWindowViewModel(InspectorService service)
    {
        _service = service;
        BackendFilters = ["全部后端", "X11（含 XWayland）", "Wayland 原生", "仅 XWayland", "仅 X11 原生"];
    }

    public ObservableCollection<WindowInfo> Windows { get; } = [];
    public ObservableCollection<InfoRow> OverviewRows { get; } = [];
    public ObservableCollection<InfoRow> ProcessRows { get; } = [];
    public ObservableCollection<InfoRow> EnvironmentRows { get; } = [];
    public ObservableCollection<InfoRow> SocketRows { get; } = [];
    public ObservableCollection<InfoRow> X11Rows { get; } = [];
    public ObservableCollection<InfoRow> WaylandRows { get; } = [];
    public ObservableCollection<X11Property> X11Properties { get; } = [];
    public ObservableCollection<X11ChildInfo> X11Children { get; } = [];
    public ObservableCollection<string> ToolkitEvidence { get; } = [];
    public ObservableCollection<string> KeyLibraries { get; } = [];
    public ObservableCollection<ProviderStatus> Diagnostics { get; } = [];
    public ObservableCollection<InfoRow> SessionRows { get; } = [];
    public ObservableCollection<string> WaylandProtocols { get; } = [];
    public IReadOnlyList<string> BackendFilters { get; }
    public string? InitialSelection { get; set; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => Set(ref _selectedTabIndex, value);
    }

    public WindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (_syncingWindows && value is null) return;
            if (ReferenceEquals(_selectedWindow, value)) return;
            _selectedWindow = value;
            OnPropertyChanged();
            HasSelection = value is not null;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedBackend));
            OnPropertyChanged(nameof(SelectedId));
            OnPropertyChanged(nameof(SelectedPid));
            if (!_suppressInspect && value is not null)
                _ = InspectSelectedAsync(value);
        }
    }

    public string SelectedTitle => SelectedWindow?.Title ?? "选择一个窗口";
    public string SelectedSubtitle => SelectedWindow?.Subtitle ?? "从左侧列表或十字准星开始检视";
    public string SelectedBackend => SelectedWindow?.BackendLabel ?? "—";
    public string SelectedId => SelectedWindow?.NativeId ?? "—";
    public string SelectedPid => SelectedWindow?.PidText ?? "—";

    public string FilterText
    {
        get => _filterText;
        set { if (Set(ref _filterText, value)) ApplyFilter(); }
    }

    public int BackendFilterIndex
    {
        get => _backendFilterIndex;
        set { if (Set(ref _backendFilterIndex, value)) ApplyFilter(); }
    }

    public bool HideOwnWindows
    {
        get => _hideOwnWindows;
        set { if (Set(ref _hideOwnWindows, value)) ApplyFilter(); }
    }

    public bool IncludeRawX11
    {
        get => _includeRawX11;
        set { if (Set(ref _includeRawX11, value)) ApplyFilter(); }
    }

    public bool IncludeUnmapped
    {
        get => _includeUnmapped;
        set { if (Set(ref _includeUnmapped, value)) ApplyFilter(); }
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set { if (Set(ref _autoRefresh, value)) StatusText = value ? "自动刷新已开启（每 3 秒）" : "自动刷新已关闭"; }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy && !IsPicking;

    public bool IsPicking
    {
        get => _isPicking;
        private set
        {
            if (!Set(ref _isPicking, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public string PickBanner
    {
        get => _pickBanner;
        private set => Set(ref _pickBanner, value);
    }

    public int? PickCountdown
    {
        get => _pickCountdown;
        private set
        {
            if (!Set(ref _pickCountdown, value)) return;
            OnPropertyChanged(nameof(HasPickCountdown));
        }
    }

    public bool HasPickCountdown => PickCountdown is not null;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => Set(ref _sessionSummary, value);
    }

    public string CountsText
    {
        get => _countsText;
        private set => Set(ref _countsText, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => Set(ref _lastUpdatedText, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    public string RawJson
    {
        get => _rawJson;
        private set => Set(ref _rawJson, value);
    }

    public string NativeJson
    {
        get => _nativeJson;
        private set => Set(ref _nativeJson, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public X11WindowDetails? X11Details
    {
        get => _x11Details;
        private set
        {
            if (!Set(ref _x11Details, value)) return;
            OnPropertyChanged(nameof(HasX11Details));
        }
    }

    public ProcessSnapshot? Process
    {
        get => _process;
        private set
        {
            if (!Set(ref _process, value)) return;
            OnPropertyChanged(nameof(HasProcess));
            OnPropertyChanged(nameof(ProcessHeader));
        }
    }

    public bool HasX11Details => X11Details is not null;
    public bool HasProcess => Process is not null;
    public string ProcessHeader => Process is null
        ? "无进程信息（可能是 KWin 拾取窗口或权限不足）"
        : $"{Process.Name} · PID {Process.Pid} · {Process.Toolkit.DisplayName}";

    public string GnomeInstallStatus
    {
        get => _gnomeInstallStatus;
        private set => Set(ref _gnomeInstallStatus, value);
    }

    public SessionInfo Session => _service.Registry.Session;

    public async Task InitializeAsync()
    {
        try
        {
            await _service.InitializeAsync(CancellationToken.None);
            await RefreshAsync();
            if (!string.IsNullOrWhiteSpace(InitialSelection))
            {
                var match = _allWindows.FirstOrDefault(w =>
                                string.Equals(w.Id, InitialSelection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(w.NativeId, InitialSelection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(w.AppId, InitialSelection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(w.Title, InitialSelection, StringComparison.OrdinalIgnoreCase))
                            ?? _allWindows.FirstOrDefault(w =>
                                w.Title.Contains(InitialSelection, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    if (!Windows.Contains(match))
                    {
                        HideOwnWindows = false;
                        IncludeRawX11 = true;
                        ApplyFilter();
                    }
                    SelectedWindow = Windows.FirstOrDefault(w => w.Id == match.Id) ?? match;
                }
            }
            await LoadDiagnosticsAsync();
            StartAutoRefresh();
            StatusText = "就绪 · 点击“十字准星拾取”或从列表选择窗口";
        }
        catch (Exception ex)
        {
            StatusText = $"初始化失败：{ex.Message}";
        }
    }

    public async Task RefreshAsync(bool showBusy = true)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        try
        {
            if (showBusy) IsBusy = true;
            var windows = await _service.RefreshAsync(token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allWindows.Clear();
                _allWindows.AddRange(windows);
                ApplyFilter();

                UpdateCounts();
                UpdateSessionUi();
                LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            if (showBusy) IsBusy = false;
            _isRefreshing = false;
        }
    }

    public async Task PickAsync(bool x11Only = false, PickMode? mode = null)
    {
        if (IsPicking) return;
        var pickMode = mode ?? PickMode.Click;
        _pickCts?.Cancel();
        _pickCts = new CancellationTokenSource();
        var cts = _pickCts;
        IsPicking = true;
        PickBanner = x11Only
            ? $"X11 {(pickMode == PickMode.Drag ? "拖动" : "点击")}拾取：右键 / Esc 取消"
            : pickMode == PickMode.Drag
                ? "拖动准星到目标窗口并松开…"
                : "正在启动点击拾取器…";
        PickCountdown = null;

        var progress = new Progress<PickProgress>(p =>
        {
            PickBanner = p.Message;
            PickCountdown = p.Countdown;
        });

        try
        {
            var result = await _service.PickAsync(progress, cts.Token, pickMode, x11Only);
            PickCountdown = null;
            if (result.Window is null)
            {
                PickBanner = result.Message ?? "未选择窗口";
                StatusText = result.Cancelled ? "已取消拾取" : result.Message ?? "拾取失败";
                await Task.Delay(1200);
                return;
            }

            await RefreshAsync();
            var picked = _allWindows.FirstOrDefault(w => MatchPicked(w, result.Window))
                         ?? result.Window;
            if (!_allWindows.Contains(picked)) _allWindows.Add(picked);
            ApplyFilter();
            _suppressInspect = false;
            var selected = Windows.FirstOrDefault(w => w.Id == picked.Id) ?? picked;
            SelectedWindow = selected;
            PickBanner = $"已选择：{selected.Title}";
            StatusText = $"{result.Message} · {selected.BackendLabel} · {selected.GeometryText}";
            UpdateCounts();
            _ = HighlightPickedAsync(selected, reportStatus: false);
        }
        catch (OperationCanceledException)
        {
            PickBanner = "已取消拾取";
            StatusText = "已取消拾取";
        }
        catch (Exception ex)
        {
            PickBanner = $"拾取失败：{ex.Message}";
            StatusText = $"拾取失败：{ex.Message}";
        }
        finally
        {
            IsPicking = false;
        }
    }

    public async Task PickFocusedAsync()
    {
        if (IsPicking) return;
        _pickCts?.Cancel();
        _pickCts = new CancellationTokenSource();
        IsPicking = true;
        PickBanner = "读取当前焦点窗口…";
        try
        {
            var result = await _service.PickFocusedAsync(null, _pickCts.Token);
            if (result.Window is null)
            {
                StatusText = result.Message ?? "无法读取焦点窗口";
                return;
            }
            await RefreshAsync();
            var picked = _allWindows.FirstOrDefault(w => MatchPicked(w, result.Window)) ?? result.Window;
            if (!_allWindows.Contains(picked)) _allWindows.Add(picked);
            ApplyFilter();
            var selected = Windows.FirstOrDefault(w => w.Id == picked.Id) ?? picked;
            SelectedWindow = selected;
            StatusText = $"当前焦点：{selected.Title}";
            PickBanner = $"当前焦点：{selected.Title}";
            _ = HighlightPickedAsync(selected, reportStatus: false);
        }
        finally
        {
            IsPicking = false;
        }
    }

    public void CancelPick()
    {
        _pickCts?.Cancel();
        PickBanner = "已请求取消…";
    }

    public async Task FocusSelectedAsync()
    {
        if (SelectedWindow is null) return;
        var ok = await _service.FocusAsync(SelectedWindow, CancellationToken.None);
        StatusText = ok ? $"已聚焦：{SelectedWindow.Title}" : "该后端不支持聚焦操作";
    }

    public async Task CloseSelectedAsync()
    {
        if (SelectedWindow is null) return;
        var ok = await _service.CloseAsync(SelectedWindow, CancellationToken.None);
        StatusText = ok ? $"已发送关闭请求：{SelectedWindow.Title}" : "该后端不支持关闭操作";
        if (ok) await Task.Delay(300);
        if (ok) await RefreshAsync();
    }

    public async Task HighlightSelectedAsync()
    {
        if (SelectedWindow is null) return;
        await HighlightPickedAsync(SelectedWindow, reportStatus: true);
    }

    private async Task HighlightPickedAsync(WindowInfo window, bool reportStatus)
    {
        try
        {
            var ok = await _service.HighlightAsync(window, 2600, CancellationToken.None);
            if (!reportStatus) return;
            StatusText = ok
                ? $"已请求高亮 {window.Title}（约 2.6 秒）"
                : "当前后端不支持全局高亮，可用“聚焦”替代";
        }
        catch
        {
            if (reportStatus) StatusText = "高亮请求失败";
        }
    }

    public async Task LoadDiagnosticsAsync()
    {
        try
        {
            var protocols = await _service.GetWaylandProtocolsAsync(CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Diagnostics.Clear();
                foreach (var status in _service.Registry.GetStatuses()) Diagnostics.Add(status);
                SessionRows.Clear();
                var s = _service.Registry.Session;
                SessionRows.Add(new InfoRow("会话类型", s.SessionType));
                SessionRows.Add(new InfoRow("桌面环境", s.Desktop));
                SessionRows.Add(new InfoRow("Wayland 显示", s.WaylandDisplay));
                SessionRows.Add(new InfoRow("X11 显示", s.X11Display));
                SessionRows.Add(new InfoRow("合成器", s.Compositor, true));
                SessionRows.Add(new InfoRow("合成器版本", s.CompositorVersion));
                SessionRows.Add(new InfoRow("X11 WM", s.X11WmName));
                SessionRows.Add(new InfoRow("X11 屏幕", _service.Registry.X11?.Inspector.DisplayName, true));
                SessionRows.Add(new InfoRow("XDG_CURRENT_DESKTOP", Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")));
                SessionRows.Add(new InfoRow("XDG_SESSION_TYPE", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")));
                SessionRows.Add(new InfoRow("DBUS_SESSION_BUS_ADDRESS", Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"), true));
                SessionRows.Add(new InfoRow("进程 socket 检测", "通过 /proc/<pid>/fd + /proc/net/unix 判断 X11/Wayland 连接", wrap: true));
                WaylandProtocols.Clear();
                foreach (var protocol in protocols) WaylandProtocols.Add(protocol);
                UpdateSessionUi();
            });
        }
        catch { /* diagnostics is best effort */ }
    }

    public async Task InstallGnomeExtensionAsync()
    {
        GnomeInstallStatus = "正在安装…";
        GnomeInstallStatus = await GnomeExtensionInstaller.InstallAsync(CancellationToken.None);
        await LoadDiagnosticsAsync();
    }

    public void RefreshDiagnostics() => _ = LoadDiagnosticsAsync();

    private void StartAutoRefresh()
    {
        _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick -= OnAutoRefreshTick;
        _refreshTimer.Tick += OnAutoRefreshTick;
        _refreshTimer.Start();
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (!AutoRefresh || IsPicking || _isRefreshing || IsBusy) return;
        await RefreshAsync(showBusy: false);
    }

    public void Shutdown()
    {
        _refreshTimer?.Stop();
        _pickCts?.Cancel();
        _refreshCts?.Cancel();
        _inspectCts?.Cancel();
    }

    private async Task InspectSelectedAsync(WindowInfo window)
    {
        _inspectCts?.Cancel();
        _inspectCts = new CancellationTokenSource();
        var token = _inspectCts.Token;
        var id = window.Id;
        IsBusy = true;
        try
        {
            var result = await _service.InspectAsync(window, token);
            if (token.IsCancellationRequested || SelectedWindow?.Id != id) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedWindow?.Id != id) return;
                Process = result.Process;
                X11Details = result.X11;
                RawJson = result.RawJson;
                NativeJson = PrettyJson(result.Window.RawJson);
                Summary = result.Summary;
                BuildOverview(result);
                BuildWayland(result.Window);
                BuildProcess(result);
                BuildX11(result.X11);
                BuildSockets(result.Process);
                OnPropertyChanged(nameof(ProcessHeader));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"读取详情失败：{ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested) IsBusy = false;
        }
    }

    private void BuildOverview(InspectionResult result)
    {
        var w = result.Window;
        OverviewRows.Clear();
        OverviewRows.Add(new InfoRow("标题", w.Title, wrap: true));
        OverviewRows.Add(new InfoRow("应用 ID", w.AppId));
        OverviewRows.Add(new InfoRow("WM_CLASS", w.WmClass));
        OverviewRows.Add(new InfoRow("后端", w.BackendLabel));
        OverviewRows.Add(new InfoRow("是否 X11 应用", w.Backend == WindowBackend.X11
            ? (w.IsXWayland ? "是 · XWayland 客户端" : "是 · 原生 X11 客户端")
            : "否 · Wayland 原生客户端"));
        OverviewRows.Add(new InfoRow("来源", w.SourceName));
        OverviewRows.Add(new InfoRow("TuxViewer ID", w.Id, mono: true));
        OverviewRows.Add(new InfoRow("原生 ID", w.NativeId, mono: true));
        var effectiveX11Id = w.X11WindowId ?? w.LinkedX11WindowId;
        OverviewRows.Add(new InfoRow("X11 窗口", effectiveX11Id is not null ? $"0x{effectiveX11Id:x}" : null, mono: true));
        if (w.X11WindowId is not null && w.LinkedX11WindowId is not null)
            OverviewRows.Add(new InfoRow("关联 X11 窗口", $"0x{w.LinkedX11WindowId:x}", mono: true));
        OverviewRows.Add(new InfoRow("PID", w.Pid?.ToString()));
        OverviewRows.Add(new InfoRow(w.Backend == WindowBackend.X11 && Session.IsWaylandSession ? "几何（合成器）" : "几何", w.GeometryText, mono: true));
        OverviewRows.Add(new InfoRow("工作区", w.WorkspaceName ?? w.WorkspaceId?.ToString()));
        OverviewRows.Add(new InfoRow("输出", w.OutputName));
        OverviewRows.Add(new InfoRow("焦点", w.IsFocused ? "是" : "否"));
        OverviewRows.Add(new InfoRow("映射状态", w.IsMapped ? "已映射" : "未映射 / 最小化"));
        OverviewRows.Add(new InfoRow("Override Redirect", w.IsOverrideRedirect ? "是" : "否"));
        foreach (var (key, value) in w.Extra)
        {
            if (OverviewRows.Any(r => r.Label == key)) continue;
            OverviewRows.Add(new InfoRow(key, value, wrap: value.Length > 60));
        }
    }

    private void BuildWayland(WindowInfo window)
    {
        WaylandRows.Clear();
        foreach (var (key, value) in window.Extra)
            WaylandRows.Add(new InfoRow(key, value, wrap: value.Length > 60));
    }

    private void BuildProcess(InspectionResult result)
    {
        ProcessRows.Clear();
        ToolkitEvidence.Clear();
        KeyLibraries.Clear();
        EnvironmentRows.Clear();
        var p = result.Process;
        if (p is null)
        {
            ProcessRows.Add(new InfoRow("状态", "没有可用的 /proc 信息（PID 为空或权限不足）", wrap: true));
            return;
        }

        ProcessRows.Add(new InfoRow("进程名", p.Name));
        ProcessRows.Add(new InfoRow("PID / PPID", $"{p.Pid} / {p.ParentPid}"));
        ProcessRows.Add(new InfoRow("用户", p.User));
        ProcessRows.Add(new InfoRow("状态", p.State));
        ProcessRows.Add(new InfoRow("可执行文件", p.Exe, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow("启动命令", p.Cmdline, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow("工作目录", p.Cwd, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow("启动时间", p.StartedAt));
        ProcessRows.Add(new InfoRow("内存 / 线程", $"{p.RssKb / 1024.0:0.#} MiB / {p.Threads}"));
        ProcessRows.Add(new InfoRow("工具包", p.Toolkit.DisplayName));
        ProcessRows.Add(new InfoRow("客户端连接", p.Backend.Display));
        ProcessRows.Add(new InfoRow("沙箱", p.Sandbox));
        foreach (var evidence in p.Toolkit.Evidence) ToolkitEvidence.Add(evidence);
        foreach (var lib in p.KeyLibraries) KeyLibraries.Add(lib);

        var priority =
            new[] { "WAYLAND_DISPLAY", "DISPLAY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "GDK_BACKEND",
                    "QT_QPA_PLATFORM", "SDL_VIDEODRIVER", "ELECTRON_OZONE_PLATFORM_HINT", "MOZ_ENABLE_WAYLAND",
                    "XDG_SESSION_DESKTOP", "FLATPAK_ID", "SNAP", "SNAP_NAME", "APPIMAGE" };
        var sortedEnv = p.Environment
            .OrderBy(kv => Array.IndexOf(priority, kv.Key) is var idx && idx >= 0 ? idx : 100)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in sortedEnv)
            EnvironmentRows.Add(new InfoRow(key, value, mono: true, wrap: value.Length > 80));
    }

    private void BuildX11(X11WindowDetails? x11)
    {
        X11Rows.Clear();
        X11Properties.Clear();
        X11Children.Clear();
        if (x11 is null) return;
        X11Rows.Add(new InfoRow("窗口 ID", x11.IdHex, mono: true));
        X11Rows.Add(new InfoRow("标题", x11.Title, wrap: true));
        X11Rows.Add(new InfoRow("实例 / 类", $"{x11.InstanceName} / {x11.ClassName}", mono: true));
        X11Rows.Add(new InfoRow("几何 (X11 坐标)", $"{x11.Width}×{x11.Height} @ {x11.X},{x11.Y}", mono: true));
        if (Session.IsWaylandSession)
            X11Rows.Add(new InfoRow("坐标提示", "XWayland 的 X11 坐标可能经过缩放或偏移，和合成器布局不一致", wrap: true));
        X11Rows.Add(new InfoRow("边框 / 深度", $"{x11.BorderWidth} / {x11.Depth}"));
        X11Rows.Add(new InfoRow("映射", x11.Mapped ? "viewable" : "unmapped"));
        X11Rows.Add(new InfoRow("Override Redirect", x11.OverrideRedirect ? "是" : "否"));
        X11Rows.Add(new InfoRow("Input Only", x11.InputOnly ? "是" : "否"));
        X11Rows.Add(new InfoRow("窗口类型", x11.WindowType));
        X11Rows.Add(new InfoRow("WM State", x11.WmState));
        X11Rows.Add(new InfoRow("Transient For", x11.TransientFor, mono: true));
        X11Rows.Add(new InfoRow("协议", x11.Protocols, wrap: true));
        X11Rows.Add(new InfoRow("WM Normal Hints", x11.WmNormalHints, mono: true, wrap: true));
        X11Rows.Add(new InfoRow("Root / Parent", $"{x11.RootWindow} / {x11.ParentWindow}", mono: true));
        X11Rows.Add(new InfoRow("直接子窗口", x11.ChildCount.ToString()));
        X11Rows.Add(new InfoRow("事件掩码", x11.EventMask, mono: true, wrap: true));
        foreach (var property in x11.Properties) X11Properties.Add(property);
        foreach (var child in x11.Children) X11Children.Add(child);
    }

    private void BuildSockets(ProcessSnapshot? process)
    {
        SocketRows.Clear();
        if (process is null) return;
        SocketRows.Add(new InfoRow("X11 socket", process.Backend.HasX11Socket ? "已连接" : "未连接"));
        SocketRows.Add(new InfoRow("Wayland socket", process.Backend.HasWaylandSocket ? "已连接" : "未连接"));
        SocketRows.Add(new InfoRow("D-Bus socket", process.Backend.HasDbusSocket ? "已连接" : "未连接"));
        SocketRows.Add(new InfoRow("PipeWire socket", process.Backend.HasPipeWireSocket ? "已连接" : "未连接"));
        foreach (var socket in process.Sockets)
            SocketRows.Add(new InfoRow(socket.Kind, $"{socket.Path}  (inode {socket.Inode})", mono: true, wrap: true));
    }

    private void ApplyFilter()
    {
        var query = _filterText.Trim();
        IEnumerable<WindowInfo> filtered = _allWindows;

        if (HideOwnWindows) filtered = filtered.Where(w => !w.IsOwnWindow);
        if (!IncludeRawX11) filtered = filtered.Where(w => !w.MergedIntoWayland);
        if (!IncludeUnmapped) filtered = filtered.Where(w => w.IsMapped);

        filtered = BackendFilterIndex switch
        {
            1 => filtered.Where(w => w.Backend == WindowBackend.X11),
            2 => filtered.Where(w => w.Backend == WindowBackend.Wayland),
            3 => filtered.Where(w => w.IsXWayland),
            4 => filtered.Where(w => w.Backend == WindowBackend.X11 && !w.IsXWayland),
            _ => filtered,
        };

        if (query.Length > 0)
        {
            filtered = filtered.Where(w =>
                Contains(w.Title, query) || Contains(w.AppId, query) || Contains(w.WmClass, query) ||
                Contains(w.Pid?.ToString(), query) || Contains(w.NativeId, query) ||
                Contains(w.SourceName, query) || Contains(w.WorkspaceName, query) ||
                Contains(w.BackendLabel, query));
        }

        var list = filtered.ToList();
        var selected = SelectedWindow;

        _syncingWindows = true;
        try
        {
            SyncWindows(list);
        }
        finally
        {
            _syncingWindows = false;
        }

        if (selected is not null && !Windows.Contains(selected))
            SelectedWindow = null;

        UpdateCounts();
    }

    private void SyncWindows(List<WindowInfo> desired)
    {
        var desiredIds = desired.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

        // Remove only windows that disappeared; never clear the whole collection.
        for (var i = Windows.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(Windows[i].Id)) Windows.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            var existingIndex = -1;
            for (var j = i; j < Windows.Count; j++)
            {
                if (!string.Equals(Windows[j].Id, target.Id, StringComparison.Ordinal)) continue;
                existingIndex = j;
                break;
            }

            if (existingIndex >= 0)
            {
                var existing = Windows[existingIndex];
                existing.RefreshFrom(target);
                if (existingIndex != i) Windows.Move(existingIndex, i);
            }
            else
            {
                Windows.Insert(i, target);
            }
        }
    }

    private void UpdateCounts()
    {
        var visible = _allWindows.Where(w => !w.MergedIntoWayland || IncludeRawX11).ToList();
        var x11 = visible.Count(w => w.Backend == WindowBackend.X11 && !w.IsXWayland);
        var wayland = visible.Count(w => w.Backend == WindowBackend.Wayland);
        var xwayland = visible.Count(w => w.IsXWayland);
        var own = visible.Count(w => w.IsOwnWindow);
        CountsText = $"{visible.Count} 个窗口 · X11 {x11} · Wayland {wayland} · XWayland {xwayland} · 本应用 {own}";
        OnPropertyChanged(nameof(Session));
    }

    private void UpdateSessionUi()
    {
        var s = _service.Registry.Session;
        SessionSummary = s.SessionSummary;
        OnPropertyChanged(nameof(Session));
    }

    private static bool MatchPicked(WindowInfo candidate, WindowInfo picked)
    {
        if (NormalizeId(candidate.Id) == NormalizeId(picked.Id)) return true;
        if (picked.X11WindowId is not null &&
            (candidate.X11WindowId == picked.X11WindowId || candidate.LinkedX11WindowId == picked.X11WindowId))
            return true;
        if (picked.Pid is not null && candidate.Pid == picked.Pid)
        {
            var a = NormalizeTitle(candidate.Title);
            var b = NormalizeTitle(picked.Title);
            if (a == b && a.Length > 0) return true;
            if (string.Equals(candidate.AppId, picked.AppId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(candidate.AppId))
                return true;
        }
        return false;
    }

    private static string PrettyJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch
        {
            return raw;
        }
    }

    private static string NormalizeId(string id) => id.Trim().ToLowerInvariant();
    private static string NormalizeTitle(string? title)
        => string.Join(' ', (title ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
