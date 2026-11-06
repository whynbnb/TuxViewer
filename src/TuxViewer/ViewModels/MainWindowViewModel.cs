using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
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

    private readonly RealtimeMonitor _monitor = new();
    private readonly Dictionary<string, string> _lastOverviewSnapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastX11RowSnapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastX11PropertySnapshot = new(StringComparer.Ordinal);
    private CancellationTokenSource? _eventDebounceCts;
    private string _lastDiffWindowId = "";
    private bool _realtimeEnabled = true;
    private string _realtimeSummary = "未启动";
    private int _eventCount;
    private string _eventCountText = "0 条事件";
    private int _changeGeneration;
    private Bitmap? _screenshotImage;
    private byte[]? _screenshotPng;
    private string _screenshotStatus = "";
    private string _screenshotSource = "";
    private bool _hasScreenshot;
    private int _ruleCompositorIndex;
    private bool _ruleFloating;
    private bool _ruleNoBorder;
    private bool _ruleFullscreen;
    private bool _ruleSticky;
    private bool _ruleMatchTitle;
    private string _ruleWidth = "";
    private string _ruleHeight = "";
    private string _ruleWorkspace = "";
    private string _ruleOpacity = "";
    private string _generatedRule = "";
    private bool _treeMode;
    private int _treeGroupModeIndex;
    private WindowNode? _selectedTreeNode;
    private string _lockedSelectionId = "";
    private bool _onlySelectedPid;
    private int? _pidFilter;

    private readonly X11EventMonitor _x11EventMonitor = new();
    private bool _x11RecordEnabled;
    private bool _x11RecordOnlySelected;
    private bool _x11RecordKeys = true;
    private bool _x11RecordMouse = true;
    private bool _x11RecordConfigure = true;
    private bool _x11RecordProperty = true;
    private bool _x11RecordFocus = true;
    private bool _x11RecordMap = true;
    private bool _x11RecordClientMessage = true;
    private bool _x11RecordOther;
    private string _x11RecordStatus = "未启动";
    private int _x11EventCount;
    private string _x11EventCountText = "0 条事件";
    private ulong? _selectedX11Id;
    private (int X, int Y, int W, int H)? _selectedX11Rect;

    public MainWindowViewModel(InspectorService service)
    {
        _service = service;
        BackendFilters = ["全部后端", "X11（含 XWayland）", "Wayland 原生", "仅 XWayland", "仅 X11 原生"];
        RuleCompositors = ["niri", "Sway", "Hyprland"];
        TreeGroupModes = ["按应用", "按进程", "按工作区", "平铺"];
    }

    public ObservableCollection<WindowInfo> Windows { get; } = [];
    public ObservableCollection<WindowNode> WindowTree { get; } = [];
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
    public ObservableCollection<InfoRow> RealtimeRows { get; } = [];
    public ObservableCollection<string> WaylandProtocols { get; } = [];
    public ObservableCollection<WindowEventRecord> Events { get; } = [];
    public ObservableCollection<X11RecordedEvent> X11Events { get; } = [];
    public IReadOnlyList<string> BackendFilters { get; }
    public IReadOnlyList<string> RuleCompositors { get; }
    public IReadOnlyList<string> TreeGroupModes { get; }
    public string? InitialSelection { get; set; }
    public bool AutoCapture { get; set; }
    public bool AutoRecord { get; set; }

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
            var previousId = _selectedWindow?.Id;
            var sameWindow = value is not null && string.Equals(previousId, value.Id, StringComparison.Ordinal);
            _selectedWindow = value;
            _selectedX11Id = value?.X11WindowId ?? value?.LinkedX11WindowId;
            _selectedX11Rect = value is { SourceId: "x11", X: int sx, Y: int sy, Width: int sw, Height: int sh }
                ? (sx, sy, sw, sh)
                : null;
            if (value is not null && _allWindows.Any(w => string.Equals(w.Id, value.Id, StringComparison.Ordinal)))
                _lockedSelectionId = "";
            if (!sameWindow)
            {
                _lastDiffWindowId = value?.Id ?? "";
                _lastOverviewSnapshot.Clear();
                _lastX11RowSnapshot.Clear();
                _lastX11PropertySnapshot.Clear();
                ResetScreenshot();
                RegenerateRule();
            }
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
        set
        {
            if (!Set(ref _autoRefresh, value)) return;
            StatusText = value
                ? _monitor.IsRunning ? "自动刷新已开启（实时事件 + 15 秒兜底）" : "自动刷新已开启（每 3 秒）"
                : "自动刷新已关闭";
        }
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

    public bool RealtimeEnabled
    {
        get => _realtimeEnabled;
        set
        {
            if (!Set(ref _realtimeEnabled, value)) return;
            StatusText = value ? "实时监控已开启" : "实时监控已暂停（仅保留自动刷新）";
        }
    }

    public string RealtimeSummary
    {
        get => _realtimeSummary;
        private set => Set(ref _realtimeSummary, value);
    }

    public string EventCountText
    {
        get => _eventCountText;
        private set => Set(ref _eventCountText, value);
    }

    public bool HasEvents => Events.Count > 0;
    public bool HasX11Events => X11Events.Count > 0;

    public bool X11RecordEnabled
    {
        get => _x11RecordEnabled;
        private set
        {
            if (!Set(ref _x11RecordEnabled, value)) return;
            OnPropertyChanged(nameof(X11RecordButtonText));
        }
    }

    public string X11RecordButtonText => _x11RecordEnabled ? "停止录制" : "开始录制";

    public string X11RecordStatus
    {
        get => _x11RecordStatus;
        private set => Set(ref _x11RecordStatus, value);
    }

    public string X11EventCountText
    {
        get => _x11EventCountText;
        private set => Set(ref _x11EventCountText, value);
    }

    public bool X11RecordOnlySelected
    {
        get => _x11RecordOnlySelected;
        set => Set(ref _x11RecordOnlySelected, value);
    }

    public bool X11RecordKeys
    {
        get => _x11RecordKeys;
        set => Set(ref _x11RecordKeys, value);
    }

    public bool X11RecordMouse
    {
        get => _x11RecordMouse;
        set => Set(ref _x11RecordMouse, value);
    }

    public bool X11RecordConfigure
    {
        get => _x11RecordConfigure;
        set => Set(ref _x11RecordConfigure, value);
    }

    public bool X11RecordProperty
    {
        get => _x11RecordProperty;
        set => Set(ref _x11RecordProperty, value);
    }

    public bool X11RecordFocus
    {
        get => _x11RecordFocus;
        set => Set(ref _x11RecordFocus, value);
    }

    public bool X11RecordMap
    {
        get => _x11RecordMap;
        set => Set(ref _x11RecordMap, value);
    }

    public bool X11RecordClientMessage
    {
        get => _x11RecordClientMessage;
        set => Set(ref _x11RecordClientMessage, value);
    }

    public bool X11RecordOther
    {
        get => _x11RecordOther;
        set => Set(ref _x11RecordOther, value);
    }

    public bool TreeMode
    {
        get => _treeMode;
        set
        {
            if (!Set(ref _treeMode, value)) return;
            if (value) BuildTree();
            else _selectedTreeNode = null;
        }
    }

    public int TreeGroupModeIndex
    {
        get => _treeGroupModeIndex;
        set { if (Set(ref _treeGroupModeIndex, value)) BuildTree(); }
    }

    public WindowNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (ReferenceEquals(_selectedTreeNode, value)) return;
            _selectedTreeNode = value;
            OnPropertyChanged();
            if (value?.Window is not { } window) return;
            _lockedSelectionId = _allWindows.Any(w => string.Equals(w.Id, window.Id, StringComparison.Ordinal))
                ? ""
                : window.Id;
            SelectedWindow = window;
        }
    }

    public bool OnlySelectedPid
    {
        get => _onlySelectedPid;
        set
        {
            if (!Set(ref _onlySelectedPid, value)) return;
            _pidFilter = value ? SelectedWindow?.Pid : null;
            ApplyFilter();
        }
    }

    public int RuleCompositorIndex
    {
        get => _ruleCompositorIndex;
        set { if (Set(ref _ruleCompositorIndex, value)) RegenerateRule(); }
    }

    public bool RuleFloating
    {
        get => _ruleFloating;
        set { if (Set(ref _ruleFloating, value)) RegenerateRule(); }
    }

    public bool RuleNoBorder
    {
        get => _ruleNoBorder;
        set { if (Set(ref _ruleNoBorder, value)) RegenerateRule(); }
    }

    public bool RuleFullscreen
    {
        get => _ruleFullscreen;
        set { if (Set(ref _ruleFullscreen, value)) RegenerateRule(); }
    }

    public bool RuleSticky
    {
        get => _ruleSticky;
        set { if (Set(ref _ruleSticky, value)) RegenerateRule(); }
    }

    public bool RuleMatchTitle
    {
        get => _ruleMatchTitle;
        set { if (Set(ref _ruleMatchTitle, value)) RegenerateRule(); }
    }

    public string RuleWidth
    {
        get => _ruleWidth;
        set { if (Set(ref _ruleWidth, value)) RegenerateRule(); }
    }

    public string RuleHeight
    {
        get => _ruleHeight;
        set { if (Set(ref _ruleHeight, value)) RegenerateRule(); }
    }

    public string RuleWorkspace
    {
        get => _ruleWorkspace;
        set { if (Set(ref _ruleWorkspace, value)) RegenerateRule(); }
    }

    public string RuleOpacity
    {
        get => _ruleOpacity;
        set { if (Set(ref _ruleOpacity, value)) RegenerateRule(); }
    }

    public string GeneratedRule
    {
        get => _generatedRule;
        private set => Set(ref _generatedRule, value);
    }

    public Bitmap? ScreenshotImage
    {
        get => _screenshotImage;
        private set => Set(ref _screenshotImage, value);
    }

    public bool HasScreenshot
    {
        get => _hasScreenshot;
        private set => Set(ref _hasScreenshot, value);
    }

    public string ScreenshotStatus
    {
        get => _screenshotStatus;
        private set => Set(ref _screenshotStatus, value);
    }

    public string ScreenshotSource
    {
        get => _screenshotSource;
        private set => Set(ref _screenshotSource, value);
    }

    public byte[]? ScreenshotPng => _screenshotPng;

    public string AutoRefreshBadge => _monitor.IsRunning ? "AUTO 15s" : "AUTO 3s";

    public bool IsRealtimeActive => _monitor.IsRunning;

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
            StartMonitoring();
            StartAutoRefresh();
            if (AutoCapture) await CaptureScreenshotAsync();
            if (AutoRecord) await ToggleX11RecordAsync();
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

    public async Task CaptureScreenshotAsync()
    {
        if (SelectedWindow is null) return;
        var window = SelectedWindow;
        ScreenshotStatus = "正在截图…";
        ScreenshotSource = "";
        try
        {
            var result = await _service.CaptureScreenshotAsync(window, CancellationToken.None);
            if (SelectedWindow?.Id != window.Id) return;
            if (!result.Success)
            {
                ScreenshotStatus = result.Message;
                return;
            }

            using var stream = new MemoryStream(result.Png!);
            var bitmap = new Bitmap(stream);
            _screenshotPng = result.Png;
            ScreenshotImage = bitmap;
            HasScreenshot = true;
            ScreenshotSource = result.Source;
            ScreenshotStatus = $"{result.Message} · {result.Width}×{result.Height}";
            StatusText = $"已截图：{window.Title} · {ScreenshotStatus}";
            SelectedTabIndex = 7;
        }
        catch (Exception ex)
        {
            ScreenshotStatus = $"截图失败：{ex.Message}";
            StatusText = ScreenshotStatus;
        }
    }

    private void ResetScreenshot()
    {
        _screenshotPng = null;
        ScreenshotImage = null;
        HasScreenshot = false;
        ScreenshotStatus = "";
        ScreenshotSource = "";
    }

    public void RegenerateRule()
    {
        var window = SelectedWindow;
        if (window is null)
        {
            GeneratedRule = "# 先选择一个窗口";
            return;
        }

        var compositor = RuleCompositors[Math.Clamp(_ruleCompositorIndex, 0, RuleCompositors.Count - 1)];
        GeneratedRule = RuleGenerator.Generate(compositor, window, new WindowRuleOptions
        {
            Floating = _ruleFloating,
            NoBorder = _ruleNoBorder,
            Fullscreen = _ruleFullscreen,
            Sticky = _ruleSticky,
            MatchTitle = _ruleMatchTitle,
            Width = ParsePositiveInt(_ruleWidth),
            Height = ParsePositiveInt(_ruleHeight),
            Workspace = string.IsNullOrWhiteSpace(_ruleWorkspace) ? null : _ruleWorkspace.Trim(),
            Opacity = double.TryParse(_ruleOpacity, out var opacity) && opacity is > 0 and < 1 ? opacity : null,
        });
    }

    private static int? ParsePositiveInt(string text)
        => int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;

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
                RealtimeRows.Clear();
                RealtimeRows.Add(new InfoRow("状态", _monitor.IsRunning ? "运行中" : "未运行"));
                RealtimeRows.Add(new InfoRow("后端", _monitor.StatusText, wrap: true));
                RealtimeRows.Add(new InfoRow("事件数量", EventCountText));
                RealtimeRows.Add(new InfoRow("兜底刷新", _monitor.IsRunning ? "15 秒（实时事件优先）" : "3 秒轮询"));
                RealtimeRows.Add(new InfoRow("说明", "X11 使用独立 Display 连接选择 PropertyChange / StructureNotify / FocusChange；Wayland 使用合成器事件流。", wrap: true));
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

    private void StartMonitoring()
    {
        try
        {
            _monitor.Start(OnRealtimeEvent);
            RealtimeSummary = _monitor.IsRunning ? _monitor.StatusText : "不可用";
            OnPropertyChanged(nameof(AutoRefreshBadge));
            OnPropertyChanged(nameof(IsRealtimeActive));
        }
        catch (Exception ex)
        {
            RealtimeSummary = $"启动失败：{ex.Message}";
        }
    }

    private void OnRealtimeEvent(WindowEventRecord record)
        => Dispatcher.UIThread.Post(() => HandleRealtimeEvent(record));

    private void HandleRealtimeEvent(WindowEventRecord record)
    {
        if (!RealtimeEnabled) return;

        Events.Insert(0, record);
        while (Events.Count > 800) Events.RemoveAt(Events.Count - 1);
        _eventCount++;
        EventCountText = $"{_eventCount} 条事件";
        OnPropertyChanged(nameof(HasEvents));

        var selected = SelectedWindow;
        var affectsSelected = selected is not null && MatchesRealtime(selected, record);

        _eventDebounceCts?.Cancel();
        _eventDebounceCts = new CancellationTokenSource();
        var token = _eventDebounceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(affectsSelected ? 160 : 380, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (token.IsCancellationRequested || !RealtimeEnabled) return;
                await RefreshAsync(showBusy: false);
                if (token.IsCancellationRequested) return;
                var current = SelectedWindow;
                if (affectsSelected && current is not null && MatchesRealtime(current, record))
                    await InspectSelectedAsync(current, diff: true);
            });
        }, token);
    }

    private static bool MatchesRealtime(WindowInfo window, WindowEventRecord record)
    {
        if (string.IsNullOrEmpty(record.NativeId) || record.NativeId == "root")
            return record.Kind == MonitorEventKind.Focus;

        if (record.Source == "X11")
        {
            var id = ParseX11Id(record.NativeId);
            if (id is null) return false;
            if (window.X11WindowId == id || window.LinkedX11WindowId == id) return true;
            return ParseX11Id(window.NativeId) == id;
        }

        return string.Equals(window.SourceId, record.Source, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(window.NativeId, record.NativeId, StringComparison.Ordinal);
    }

    private static ulong? ParseX11Id(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    private void StartAutoRefresh()
    {
        _refreshTimer ??= new DispatcherTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(_monitor.IsRunning ? 15 : 3);
        _refreshTimer.Tick -= OnAutoRefreshTick;
        _refreshTimer.Tick += OnAutoRefreshTick;
        _refreshTimer.Start();
    }

    public async Task ToggleX11RecordAsync()
    {
        if (_x11EventMonitor.IsRunning)
        {
            _x11EventMonitor.Stop();
            X11RecordEnabled = false;
            X11RecordStatus = "已停止";
            StatusText = "X11 RECORD 事件监视已停止";
            return;
        }

        _x11EventMonitor.Filter = PassesX11Filter;
        var ok = _x11EventMonitor.Start(OnX11Recorded);
        X11RecordEnabled = ok;
        X11RecordStatus = ok
            ? _x11RecordOnlySelected
                ? "录制中 · 仅选中窗口（RECORD 扩展）"
                : "录制中 · 全局（RECORD 扩展）"
            : $"启动失败：{_x11EventMonitor.Error}";
        StatusText = ok ? "X11 RECORD 事件监视已启动" : X11RecordStatus;
    }

    private void OnX11Recorded(X11RecordedEvent recorded)
        => Dispatcher.UIThread.Post(() => AddX11Event(recorded));

    private void AddX11Event(X11RecordedEvent recorded)
    {
        X11Events.Insert(0, recorded);
        while (X11Events.Count > 2000) X11Events.RemoveAt(X11Events.Count - 1);
        _x11EventCount++;
        X11EventCountText = $"{_x11EventCount} 条事件";
        OnPropertyChanged(nameof(HasX11Events));
    }

    private bool PassesX11Filter(X11RecordedEvent recorded)
    {
        var enabled = recorded.Category switch
        {
            "Key" => _x11RecordKeys,
            "Mouse" => _x11RecordMouse,
            "Configure" => _x11RecordConfigure,
            "Property" => _x11RecordProperty,
            "Focus" => _x11RecordFocus,
            "Map" => _x11RecordMap,
            "ClientMessage" => _x11RecordClientMessage,
            _ => _x11RecordOther,
        };
        if (!enabled) return false;
        if (!_x11RecordOnlySelected) return true;
        var selected = _selectedX11Id;
        if (selected is null) return false;
        if (recorded.Window == selected.Value) return true;
        if (_selectedX11Rect is { } rect && recorded.Category is "Mouse")
        {
            if (recorded.RootX >= rect.X && recorded.RootX < rect.X + rect.W &&
                recorded.RootY >= rect.Y && recorded.RootY < rect.Y + rect.H)
                return true;
        }
        // Raw device key events do not carry a target window; use focus as a proxy.
        return recorded.Category == "Key" && _selectedWindow?.IsFocused == true;
    }

    public void ClearX11Events()
    {
        X11Events.Clear();
        _x11EventCount = 0;
        X11EventCountText = "0 条事件";
        OnPropertyChanged(nameof(HasX11Events));
    }

    public string BuildX11EventsText()
    {
        if (X11Events.Count == 0) return "TuxViewer X11 RECORD 事件（空）";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TuxViewer X11 RECORD 事件 · {X11Events.Count} 条 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('─', 60));
        foreach (var record in X11Events.Reverse())
            sb.AppendLine(record.DisplayText);
        return sb.ToString();
    }

    public void ClearEvents()
    {
        Events.Clear();
        _eventCount = 0;
        EventCountText = "0 条事件";
        OnPropertyChanged(nameof(HasEvents));
    }

    public string BuildEventsText()
    {
        if (Events.Count == 0) return "TuxViewer 实时事件（空）";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TuxViewer 实时事件 · {Events.Count} 条 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('─', 60));
        foreach (var record in Events.Reverse())
            sb.AppendLine(record.DisplayText);
        return sb.ToString();
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
        _eventDebounceCts?.Cancel();
        _monitor.Dispose();
        _x11EventMonitor.Dispose();
        _eventDebounceCts?.Dispose();
    }

    private async Task InspectSelectedAsync(WindowInfo window, bool diff = false)
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
                var changed = false;
                changed |= BuildOverview(result, diff);
                BuildWayland(result.Window);
                BuildProcess(result);
                changed |= BuildX11(result.X11, diff);
                BuildSockets(result.Process);
                OnPropertyChanged(nameof(ProcessHeader));
                RegenerateRule();
                if (changed) ScheduleChangeClear();
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

    private void ScheduleChangeClear()
    {
        var generation = ++_changeGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3600);
            }
            catch { return; }
            if (generation != _changeGeneration) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _changeGeneration) return;
                if (SelectedWindow is { } window)
                    _ = InspectSelectedAsync(window, diff: true);
            });
        });
    }

    private bool BuildOverview(InspectionResult result, bool diff)
    {
        var w = result.Window;
        var rows = new List<InfoRow>();

        void Add(string label, string? value, bool mono = false, bool wrap = false)
        {
            var display = string.IsNullOrWhiteSpace(value) ? "—" : value!;
            string? old = null;
            var changed = diff && _lastOverviewSnapshot.TryGetValue(label, out old) &&
                          !string.Equals(old, display, StringComparison.Ordinal);
            rows.Add(new InfoRow(label, value, mono, wrap, changed, changed ? old : null));
        }

        Add("标题", w.Title, wrap: true);
        Add("应用 ID", w.AppId);
        Add("WM_CLASS", w.WmClass);
        Add("后端", w.BackendLabel);
        Add("是否 X11 应用", w.Backend == WindowBackend.X11
            ? (w.IsXWayland ? "是 · XWayland 客户端" : "是 · 原生 X11 客户端")
            : "否 · Wayland 原生客户端");
        Add("来源", w.SourceName);
        Add("TuxViewer ID", w.Id, mono: true);
        Add("原生 ID", w.NativeId, mono: true);
        var effectiveX11Id = w.X11WindowId ?? w.LinkedX11WindowId;
        Add("X11 窗口", effectiveX11Id is not null ? $"0x{effectiveX11Id:x}" : null, mono: true);
        if (w.X11WindowId is not null && w.LinkedX11WindowId is not null)
            Add("关联 X11 窗口", $"0x{w.LinkedX11WindowId:x}", mono: true);
        Add("PID", w.Pid?.ToString());
        Add(w.Backend == WindowBackend.X11 && Session.IsWaylandSession ? "几何（合成器）" : "几何", w.GeometryText, mono: true);
        Add("工作区", w.WorkspaceName ?? w.WorkspaceId?.ToString());
        Add("输出", w.OutputName);
        Add("焦点", w.IsFocused ? "是" : "否");
        Add("映射状态", w.IsMapped ? "已映射" : "未映射 / 最小化");
        Add("Override Redirect", w.IsOverrideRedirect ? "是" : "否");
        foreach (var (key, value) in w.Extra)
        {
            if (rows.Any(r => r.Label == key)) continue;
            Add(key, value, wrap: value.Length > 60);
        }

        OverviewRows.Clear();
        foreach (var row in rows) OverviewRows.Add(row);
        _lastOverviewSnapshot.Clear();
        foreach (var row in rows) _lastOverviewSnapshot[row.Label] = row.Value;
        return rows.Any(r => r.IsChanged);
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

    private bool BuildX11(X11WindowDetails? x11, bool diff)
    {
        X11Rows.Clear();
        X11Properties.Clear();
        X11Children.Clear();
        if (x11 is null) return false;

        var rows = new List<InfoRow>();
        void Add(string label, string? value, bool mono = false, bool wrap = false)
        {
            var display = string.IsNullOrWhiteSpace(value) ? "—" : value!;
            string? old = null;
            var changed = diff && _lastX11RowSnapshot.TryGetValue(label, out old) &&
                          !string.Equals(old, display, StringComparison.Ordinal);
            rows.Add(new InfoRow(label, value, mono, wrap, changed, changed ? old : null));
        }

        Add("窗口 ID", x11.IdHex, mono: true);
        Add("标题", x11.Title, wrap: true);
        Add("实例 / 类", $"{x11.InstanceName} / {x11.ClassName}", mono: true);
        Add("几何 (X11 坐标)", $"{x11.Width}×{x11.Height} @ {x11.X},{x11.Y}", mono: true);
        if (Session.IsWaylandSession)
            Add("坐标提示", "XWayland 的 X11 坐标可能经过缩放或偏移，和合成器布局不一致", wrap: true);
        Add("边框 / 深度", $"{x11.BorderWidth} / {x11.Depth}");
        Add("映射", x11.Mapped ? "viewable" : "unmapped");
        Add("Override Redirect", x11.OverrideRedirect ? "是" : "否");
        Add("Input Only", x11.InputOnly ? "是" : "否");
        Add("窗口类型", x11.WindowType);
        Add("WM State", x11.WmState);
        Add("Transient For", x11.TransientFor, mono: true);
        Add("协议", x11.Protocols, wrap: true);
        Add("WM Normal Hints", x11.WmNormalHints, mono: true, wrap: true);
        Add("Root / Parent", $"{x11.RootWindow} / {x11.ParentWindow}", mono: true);
        Add("直接子窗口", x11.ChildCount.ToString());
        Add("事件掩码", x11.EventMask, mono: true, wrap: true);

        foreach (var row in rows) X11Rows.Add(row);
        _lastX11RowSnapshot.Clear();
        foreach (var row in rows) _lastX11RowSnapshot[row.Label] = row.Value;

        var anyChanged = rows.Any(r => r.IsChanged);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in x11.Properties)
        {
            var changed = false;
            string? oldValue = null;
            if (diff && _lastX11PropertySnapshot.TryGetValue(property.Name, out oldValue))
                changed = !string.Equals(oldValue, property.Value, StringComparison.Ordinal);
            anyChanged |= changed;
            X11Properties.Add(changed ? property.WithChange(true, oldValue) : property);
            current[property.Name] = property.Value;
        }
        _lastX11PropertySnapshot.Clear();
        foreach (var (key, value) in current) _lastX11PropertySnapshot[key] = value;

        foreach (var child in x11.Children) X11Children.Add(child);
        return anyChanged;
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
        if (_pidFilter is int pidFilter) filtered = filtered.Where(w => w.Pid == pidFilter);

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

        var keepLocked = TreeMode && selected is not null &&
                         string.Equals(selected.Id, _lockedSelectionId, StringComparison.Ordinal);
        if (selected is not null && !Windows.Contains(selected) && !keepLocked)
            SelectedWindow = null;

        if (TreeMode) BuildTree();
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

    private int _treeChildBudget;

    public void BuildTree()
    {
        if (!TreeMode)
        {
            WindowTree.Clear();
            return;
        }

        var known = AllNodes(WindowTree)
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().IsExpanded, StringComparer.Ordinal);
        var selectedId = SelectedWindow?.Id;
        _treeChildBudget = 40;

        var roots = new List<WindowNode>();
        if (TreeGroupModeIndex == 3)
        {
            foreach (var window in Windows) roots.Add(BuildWindowNode(window));
        }
        else
        {
            foreach (var group in Windows
                         .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var groupNode = new WindowNode
                {
                    Id = $"group:{group.Key}",
                    GroupTitle = group.Key,
                    GroupSubtitle = $"{group.Count()} 个窗口",
                    IsExpanded = true,
                };
                foreach (var window in group) groupNode.Children.Add(BuildWindowNode(window));
                roots.Add(groupNode);
            }
        }

        WindowTree.Clear();
        foreach (var root in roots) WindowTree.Add(root);
        foreach (var node in AllNodes(WindowTree))
            node.IsExpanded = known.TryGetValue(node.Id, out var expanded)
                ? expanded
                : node.IsGroup || node.Children.Count > 0;

        _selectedTreeNode = AllNodes(WindowTree)
            .FirstOrDefault(n => n.Window is { } w && string.Equals(w.Id, selectedId, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SelectedTreeNode));
    }

    private WindowNode BuildWindowNode(WindowInfo window)
    {
        var node = new WindowNode { Id = window.Id, Window = window };
        var x11Id = window.X11WindowId ?? window.LinkedX11WindowId;
        if (x11Id is null || _treeChildBudget-- <= 0) return node;
        if (_service.Registry.X11?.IsAvailable != true) return node;

        IReadOnlyList<X11ChildInfo> children;
        try { children = _service.Registry.X11.Inspector.GetChildrenLight(x11Id.Value); }
        catch { return node; }
        if (children.Count == 0) return node;

        var stack = new List<WindowNode>();
        foreach (var child in children)
        {
            var childWindow = new WindowInfo
            {
                Id = $"x11:0x{child.Id:x}",
                SourceId = "x11",
                SourceName = "X11 · 子窗口",
                NativeId = $"0x{child.Id:x}",
                Title = string.IsNullOrWhiteSpace(child.Title)
                    ? $"{child.ClassName ?? "子窗口"} 0x{child.Id:x}"
                    : child.Title,
                AppId = child.ClassName,
                WmClass = child.ClassName,
                Pid = window.Pid,
                Backend = WindowBackend.X11,
                IsXWayland = window.IsXWayland,
                IsMapped = child.Mapped,
                IsOverrideRedirect = child.OverrideRedirect,
                X = child.X,
                Y = child.Y,
                Width = child.Width,
                Height = child.Height,
                X11WindowId = child.Id,
            };
            var childNode = new WindowNode { Id = childWindow.Id, Window = childWindow };

            while (stack.Count > child.Depth) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : node;
            parent.Children.Add(childNode);
            stack.Add(childNode);
        }

        if (node.Children.Count > 0)
            node.IsExpanded = false;
        return node;
    }

    private string GroupKey(WindowInfo window) => TreeGroupModeIndex switch
    {
        1 => window.Pid is int pid ? $"PID {pid}" : "未知 PID",
        2 => window.WorkspaceName
             ?? (window.WorkspaceId is int id ? $"工作区 {id}" : "未知工作区"),
        _ => !string.IsNullOrWhiteSpace(window.AppId)
            ? window.AppId!
            : !string.IsNullOrWhiteSpace(window.WmClass) ? window.WmClass! : window.SourceName,
    };

    private static IEnumerable<WindowNode> AllNodes(IEnumerable<WindowNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Children)) yield return child;
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
