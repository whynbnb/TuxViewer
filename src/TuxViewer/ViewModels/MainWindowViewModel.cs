using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    private bool _useRegexSearch;
    private string _pidFilterText = "";
    private string _windowIdFilterText = "";
    private string _workspaceFilterText = "";
    private string _filterPresetName = "";
    private int _selectedFilterPresetIndex = -1;
    private string _filterError = "";
    private string _filterSummaryText = "";
    private string _statusText = L("正在初始化…", "Initializing…");
    private string _sessionSummary = L("检测会话…", "Detecting session…");
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
    private string _realtimeSummary = L("未启动", "Not started");
    private int _eventCount;
    private string _eventCountText = L("0 条事件", "0 events");
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
    private string _x11RecordStatus = L("未启动", "Not started");
    private int _x11EventCount;
    private string _x11EventCountText = L("0 条事件", "0 events");
    private ulong? _selectedX11Id;
    private (int X, int Y, int W, int H)? _selectedX11Rect;
    private readonly List<X11ProtocolRecord> _x11ProtocolAll = [];
    private readonly ConcurrentQueue<X11ProtocolRecord> _x11ProtocolQueue = new();
    private DispatcherTimer? _x11ProtocolTimer;
    private string _x11ProtocolFilterText = "";
    private bool _x11ProtocolShowRequests = true;
    private bool _x11ProtocolShowReplies = true;
    private int _x11ProtocolCount;
    private string _x11ProtocolCountText = L("0 条请求 / 回复", "0 requests / replies");
    private string _atspiStatus = L("尚未加载", "Not loaded");
    private string _atspiCountText = "";

    private readonly GlobalHotkeyService _globalHotkey = new();
    private bool _globalHotkeyEnabled;
    private string _globalHotkeyStatus = L("未启用", "Disabled");

    private string _controlStatus = "";
    private double _opacityValue = 100;
    private string _geoX = "";
    private string _geoY = "";
    private string _geoW = "";
    private string _geoH = "";
    private bool _forceKillArmed;
    private CancellationTokenSource? _forceKillResetCts;

    private List<X11Property> _x11PropertiesAll = [];
    private string _propertyFilterText = "";
    private string _snapshotName = "";
    private WindowSnapshot? _selectedSnapshot;
    private string _snapshotStatus = "";
    private string _snapshotDiffText = "";
    private string _reportStatus = "";
    private string _outputStatus = L("尚未读取", "Not loaded");
    private string _outputNote = "";
    private string _outputSource = "";
    private string _diagnosticsBundleStatus = "";
    private bool _exportingDiagnosticsBundle;

    private readonly SettingsService _settingsService;
    private bool _suppressSettingsSave;

    public MainWindowViewModel(InspectorService service, SettingsService settingsService)
    {
        _service = service;
        _settingsService = settingsService;
        BackendFilters = [];
        RuleCompositors = ["niri", "Sway", "Hyprland"];
        TreeGroupModes = [];
        RebuildLocalizedOptions();
        LoadFilterPresets(settingsService.Settings.FilterPresets);
        ApplySettings(settingsService.Settings);
        PropertyChanged += OnPersistedPropertyChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
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
    public ObservableCollection<X11ProtocolRecord> X11ProtocolRecords { get; } = [];
    public ObservableCollection<AtspiNode> AtspiTree { get; } = [];
    public ObservableCollection<WindowSnapshot> Snapshots { get; } = [];
    public ObservableCollection<OutputInfo> Outputs { get; } = [];
    public ObservableCollection<WorkspaceInfo> OutputWorkspaces { get; } = [];
    public ObservableCollection<string> OutputLayoutLines { get; } = [];
    public ObservableCollection<FilterPreset> FilterPresets { get; } = [];
    public ObservableCollection<string> BackendFilters { get; }
    public IReadOnlyList<string> RuleCompositors { get; }
    public ObservableCollection<string> TreeGroupModes { get; }
    public string? InitialSelection { get; set; }
    public bool AutoCapture { get; set; }
    public bool AutoRecord { get; set; }
    public bool AutoLoadAtspi { get; set; }
    public bool AutoHotkey { get; set; }
    public PickMode? AutoPickMode { get; set; }
    public bool AutoPickX11 { get; set; }
    public PickMode? AutoPickX11Mode { get; set; }

    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task InitializationTask => _initialization.Task;

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
                ResetAtspi();
                ResetControlFields(value);
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

    public string SelectedTitle => SelectedWindow?.Title ?? L("选择一个窗口", "Select a window");
    public string SelectedSubtitle => SelectedWindow?.Subtitle ?? L("从左侧列表或十字准星开始检视", "Pick a window from the list or with the crosshair");
    public string SelectedBackend => SelectedWindow?.BackendLabel ?? "—";
    public string SelectedId => SelectedWindow?.NativeId ?? "—";
    public string SelectedPid => SelectedWindow?.PidText ?? "—";

    public string FilterText
    {
        get => _filterText;
        set { if (Set(ref _filterText, value)) ApplyFilter(); }
    }

    public bool UseRegexSearch
    {
        get => _useRegexSearch;
        set { if (Set(ref _useRegexSearch, value)) ApplyFilter(); }
    }

    public string PidFilterText
    {
        get => _pidFilterText;
        set { if (Set(ref _pidFilterText, value)) ApplyFilter(); }
    }

    public string WindowIdFilterText
    {
        get => _windowIdFilterText;
        set { if (Set(ref _windowIdFilterText, value)) ApplyFilter(); }
    }

    public string WorkspaceFilterText
    {
        get => _workspaceFilterText;
        set { if (Set(ref _workspaceFilterText, value)) ApplyFilter(); }
    }

    public string FilterPresetName
    {
        get => _filterPresetName;
        set => Set(ref _filterPresetName, value);
    }

    public int SelectedFilterPresetIndex
    {
        get => _selectedFilterPresetIndex;
        set => Set(ref _selectedFilterPresetIndex, value);
    }

    public string FilterError
    {
        get => _filterError;
        private set
        {
            if (!Set(ref _filterError, value)) return;
            OnPropertyChanged(nameof(HasFilterError));
        }
    }

    public bool HasFilterError => !string.IsNullOrEmpty(_filterError);

    public string FilterSummaryText
    {
        get => _filterSummaryText;
        private set => Set(ref _filterSummaryText, value);
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
                ? _monitor.IsRunning
                    ? L("自动刷新已开启（实时事件 + 15 秒兜底）", "Auto refresh on (live events + 15 s fallback)")
                    : L("自动刷新已开启（每 3 秒）", "Auto refresh on (every 3 s)")
                : L("自动刷新已关闭", "Auto refresh off");
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
        private set
        {
            if (!Set(ref _statusText, value)) return;
            AppLog.Info(value);
        }
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
        private set
        {
            if (!Set(ref _lastUpdatedText, value)) return;
            OnPropertyChanged(nameof(UpdatedText));
        }
    }

    public string UpdatedText => LocalizationService.F("更新 {0}", "Updated {0}", LastUpdatedText);

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
            OnPropertyChanged(nameof(X11PropertyCountText));
            OnPropertyChanged(nameof(X11ChildCountText));
        }
    }

    public string X11PropertyCountText => X11Details is null
        ? ""
        : LocalizationService.F("{0} 个 X11 属性（点击复制）", "{0} X11 properties (click to copy)",
            X11Details.Properties.Count);

    public string X11ChildCountText => X11Details is null
        ? ""
        : LocalizationService.F("{0} 个子窗口", "{0} child windows", X11Details.Children.Count);

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
        ? L("无进程信息（可能是 KWin 拾取窗口或权限不足）", "No process information (KWin-picked window or insufficient permissions)")
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
            StatusText = value
                ? L("实时监控已开启", "Live monitoring enabled")
                : L("实时监控已暂停（仅保留自动刷新）", "Live monitoring paused (auto refresh only)");
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

    public string AtspiStatus
    {
        get => _atspiStatus;
        private set => Set(ref _atspiStatus, value);
    }

    public string AtspiCountText
    {
        get => _atspiCountText;
        private set => Set(ref _atspiCountText, value);
    }

    public bool HasAtspiTree => AtspiTree.Count > 0;

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        private set
        {
            if (!Set(ref _globalHotkeyEnabled, value)) return;
            OnPropertyChanged(nameof(GlobalHotkeyButtonText));
        }
    }

    public string GlobalHotkeyButtonText => _globalHotkeyEnabled
        ? L("停用全局热键", "Disable global hotkey")
        : L("启用全局热键", "Enable global hotkey");

    public string GlobalHotkeyStatus
    {
        get => _globalHotkeyStatus;
        private set => Set(ref _globalHotkeyStatus, value);
    }

    public string HotkeyConfig => GlobalHotkeyService.BuildCompositorConfig();

    public string ControlStatus
    {
        get => _controlStatus;
        private set => Set(ref _controlStatus, value);
    }

    public double OpacityValue
    {
        get => _opacityValue;
        set => Set(ref _opacityValue, value);
    }

    public string GeoX
    {
        get => _geoX;
        set => Set(ref _geoX, value);
    }

    public string GeoY
    {
        get => _geoY;
        set => Set(ref _geoY, value);
    }

    public string GeoW
    {
        get => _geoW;
        set => Set(ref _geoW, value);
    }

    public string GeoH
    {
        get => _geoH;
        set => Set(ref _geoH, value);
    }

    public string ForceKillButtonText => _forceKillArmed
        ? L("再次点击确认强制结束", "Click again to force kill")
        : L("强制结束客户端", "Force kill client");

    public string ProcessExeText => Process?.Exe ?? "—";
    public string ProcessCmdlineText => Process?.Cmdline ?? "—";
    public string ProcessPidText => SelectedWindow?.Pid?.ToString() ?? "—";

    public string PropertyFilterText
    {
        get => _propertyFilterText;
        set { if (Set(ref _propertyFilterText, value)) ApplyPropertyFilter(); }
    }

    public string SnapshotName
    {
        get => _snapshotName;
        set => Set(ref _snapshotName, value);
    }

    public WindowSnapshot? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set => Set(ref _selectedSnapshot, value);
    }

    public string SnapshotStatus
    {
        get => _snapshotStatus;
        private set => Set(ref _snapshotStatus, value);
    }

    public string SnapshotDiffText
    {
        get => _snapshotDiffText;
        private set
        {
            if (Set(ref _snapshotDiffText, value))
                OnPropertyChanged(nameof(HasSnapshotDiff));
        }
    }

    public string ReportStatus
    {
        get => _reportStatus;
        private set => Set(ref _reportStatus, value);
    }

    public string OutputStatus
    {
        get => _outputStatus;
        private set => Set(ref _outputStatus, value);
    }

    public string OutputNote
    {
        get => _outputNote;
        private set => Set(ref _outputNote, value);
    }

    public string OutputSource
    {
        get => _outputSource;
        private set => Set(ref _outputSource, value);
    }

    public string DiagnosticsBundleStatus
    {
        get => _diagnosticsBundleStatus;
        private set
        {
            if (!Set(ref _diagnosticsBundleStatus, value)) return;
            OnPropertyChanged(nameof(HasDiagnosticsBundleStatus));
        }
    }

    public bool HasDiagnosticsBundleStatus => !string.IsNullOrEmpty(_diagnosticsBundleStatus);

    public bool IsExportingDiagnosticsBundle
    {
        get => _exportingDiagnosticsBundle;
        private set => Set(ref _exportingDiagnosticsBundle, value);
    }

    public bool HasOutputs => Outputs.Count > 0;

    public bool HasSnapshots => Snapshots.Count > 0;
    public bool HasSnapshotDiff => !string.IsNullOrWhiteSpace(SnapshotDiffText);

    public bool X11RecordEnabled
    {
        get => _x11RecordEnabled;
        private set
        {
            if (!Set(ref _x11RecordEnabled, value)) return;
            OnPropertyChanged(nameof(X11RecordButtonText));
        }
    }

    public string X11RecordButtonText => _x11RecordEnabled
        ? L("停止录制", "Stop recording")
        : L("开始录制", "Start recording");

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

    public string X11ProtocolFilterText
    {
        get => _x11ProtocolFilterText;
        set { if (Set(ref _x11ProtocolFilterText, value)) ApplyX11ProtocolFilter(); }
    }

    public bool X11ProtocolShowRequests
    {
        get => _x11ProtocolShowRequests;
        set { if (Set(ref _x11ProtocolShowRequests, value)) ApplyX11ProtocolFilter(); }
    }

    public bool X11ProtocolShowReplies
    {
        get => _x11ProtocolShowReplies;
        set { if (Set(ref _x11ProtocolShowReplies, value)) ApplyX11ProtocolFilter(); }
    }

    public string X11ProtocolCountText
    {
        get => _x11ProtocolCountText;
        private set => Set(ref _x11ProtocolCountText, value);
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
        private set
        {
            if (!Set(ref _screenshotSource, value)) return;
            OnPropertyChanged(nameof(ScreenshotSourceText));
        }
    }

    public string ScreenshotSourceText =>
        LocalizationService.F("来源：{0}", "Source: {0}", ScreenshotSource);

    public byte[]? ScreenshotPng => _screenshotPng;

    public string AutoRefreshBadge => _monitor.IsRunning ? "AUTO 15s" : "AUTO 3s";

    public bool IsRealtimeActive => _monitor.IsRunning;

    public SessionInfo Session => _service.Registry.Session;

    public string SettingsFilePath => _settingsService.FilePath;

    public string LanguageButtonText => LocalizationService.IsEnglish ? "中文" : "EN";

    public string LanguageToolTip => LocalizationService.L("切换到 English", "Switch to 中文");

    public void ToggleLanguage()
    {
        LocalizationService.SetLanguage(LocalizationService.IsEnglish ? "zh" : "en");
        _settingsService.Update(settings => settings.Language = LocalizationService.LanguageCode);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildLocalizedOptions();
        OnPropertyChanged(nameof(LanguageButtonText));
        OnPropertyChanged(nameof(LanguageToolTip));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(ProcessHeader));
        OnPropertyChanged(nameof(AutoRefreshBadge));
        OnPropertyChanged(nameof(GlobalHotkeyButtonText));
        OnPropertyChanged(nameof(HotkeyConfig));
        OnPropertyChanged(nameof(ForceKillButtonText));
        OnPropertyChanged(nameof(X11RecordButtonText));
        OnPropertyChanged(nameof(X11RecordStatus));
        OnPropertyChanged(nameof(X11EventCountText));
        OnPropertyChanged(nameof(X11ProtocolCountText));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(RealtimeSummary));
        OnPropertyChanged(nameof(AtspiStatus));
        OnPropertyChanged(nameof(AtspiCountText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(ScreenshotStatus));
        OnPropertyChanged(nameof(ScreenshotSourceText));
        OnPropertyChanged(nameof(X11PropertyCountText));
        OnPropertyChanged(nameof(X11ChildCountText));
        OnPropertyChanged(nameof(SnapshotStatus));
        OnPropertyChanged(nameof(SnapshotDiffText));
        OnPropertyChanged(nameof(ReportStatus));
        OnPropertyChanged(nameof(OutputStatus));
        OnPropertyChanged(nameof(OutputNote));
        OnPropertyChanged(nameof(OutputSource));
        OnPropertyChanged(nameof(FilterError));
        OnPropertyChanged(nameof(FilterSummaryText));

        foreach (var window in _allWindows) window.NotifyLanguageChanged();
        _lastOverviewSnapshot.Clear();
        _lastX11RowSnapshot.Clear();
        if (TreeMode) BuildTree();
        UpdateCounts();
        UpdateSessionUi();
        RefreshDiagnostics();
        _ = LoadOutputsAsync();
        if (SelectedWindow is { } selected) _ = InspectSelectedAsync(selected);
        StatusText = LocalizationService.L("界面语言已切换为中文", "Interface language switched to English");
    }

    public async Task InitializeAsync()
    {
        _suppressSettingsSave = true;
        try
        {
            await _service.InitializeAsync(CancellationToken.None);
            await RefreshAsync();
            if (!string.IsNullOrWhiteSpace(InitialSelection))
                await SelectByArgumentAsync(InitialSelection);
            await LoadDiagnosticsAsync();
            LoadSnapshots();
            await LoadOutputsAsync();
            StartMonitoring();
            StartAutoRefresh();
            if (AutoCapture) await CaptureScreenshotAsync();
            if (AutoRecord) await ToggleX11RecordAsync();
            if (AutoLoadAtspi) await LoadAtspiAsync();
            if (AutoHotkey) ToggleGlobalHotkey();
            if (AutoPickMode is { } pickMode || AutoPickX11)
                await PickAsync(x11Only: AutoPickX11, mode: AutoPickX11 ? AutoPickX11Mode ?? PickMode.Click : AutoPickMode ?? PickMode.Click);
            StatusText = L("就绪 · 点击“十字准星拾取”或从列表选择窗口", "Ready · use the crosshair or pick a window from the list");
        }
        catch (Exception ex)
        {
            StatusText = LF("初始化失败：{0}", "Initialization failed: {0}", ex.Message);
        }
        finally
        {
            _suppressSettingsSave = false;
            _initialization.TrySetResult();
        }
    }

    /// <summary>Select a window by id, native id, app id or title (used by CLI / IPC).</summary>
    public async Task SelectByArgumentAsync(string argument)
    {
        if (_allWindows.Count == 0) await RefreshAsync(showBusy: false);

        var match = _allWindows.FirstOrDefault(w =>
                        string.Equals(w.Id, argument, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.NativeId, argument, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.AppId, argument, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(w.Title, argument, StringComparison.OrdinalIgnoreCase))
                    ?? _allWindows.FirstOrDefault(w =>
                        w.Title.Contains(argument, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            StatusText = LF("未找到窗口：{0}", "No window matched: {0}", argument);
            return;
        }

        if (!Windows.Contains(match))
        {
            HideOwnWindows = false;
            IncludeRawX11 = true;
            ApplyFilter();
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Id == match.Id) ?? match;
    }

    /// <summary>Apply actions forwarded by a second process through single-instance IPC.</summary>
    public async Task ApplyStartupOptionsAsync(StartupOptions options)
    {
        await InitializationTask;

        if (options.TabIndex is int tab) SelectedTabIndex = tab;
        if (options.Tree) TreeMode = true;
        if (options.GroupModeIndex is int group) TreeGroupModeIndex = Math.Clamp(group, 0, 3);
        if (options.RecordSelected) X11RecordOnlySelected = true;

        if (!string.IsNullOrWhiteSpace(options.Select))
            await SelectByArgumentAsync(options.Select);

        if (options.Capture) await CaptureScreenshotAsync();
        if (options.Record && !X11RecordEnabled) await ToggleX11RecordAsync();
        if (options.AtspiLoad) await LoadAtspiAsync();
        if (options.Hotkey && !GlobalHotkeyEnabled) ToggleGlobalHotkey();

        if (options.PickX11)
            await PickAsync(x11Only: true, mode: options.PickX11Mode ?? options.PickMode ?? PickMode.Click);
        else if (options.PickMode is { } pickMode)
            await PickAsync(mode: pickMode);

        StatusText = L("已处理来自另一个实例的请求", "Handled request from another instance");
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
            StatusText = LF("刷新失败：{0}", "Refresh failed: {0}", ex.Message);
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
            ? LF("X11 {0}拾取：右键 / Esc 取消", "X11 {0} pick: right-click / Esc to cancel",
                pickMode == PickMode.Drag ? L("拖动", "drag") : L("点击", "click"))
            : pickMode == PickMode.Drag
                ? L("拖动准星到目标窗口并松开…", "Drag the crosshair onto a target window and release…")
                : L("正在启动点击拾取器…", "Starting the click picker…");
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
                PickBanner = result.Message ?? L("未选择窗口", "No window selected");
                StatusText = result.Cancelled
            ? L("已取消拾取", "Pick cancelled")
            : result.Message ?? L("拾取失败", "Pick failed");
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
            PickBanner = LF("已选择：{0}", "Selected: {0}", selected.Title);
            StatusText = $"{result.Message} · {selected.BackendLabel} · {selected.GeometryText}";
            UpdateCounts();
            _ = HighlightPickedAsync(selected, reportStatus: false);
        }
        catch (OperationCanceledException)
        {
            PickBanner = L("已取消拾取", "Pick cancelled");
            StatusText = L("已取消拾取", "Pick cancelled");
        }
        catch (Exception ex)
        {
            PickBanner = LF("拾取失败：{0}", "Pick failed: {0}", ex.Message);
            StatusText = LF("拾取失败：{0}", "Pick failed: {0}", ex.Message);
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
        PickBanner = L("读取当前焦点窗口…", "Reading the focused window…");
        try
        {
            var result = await _service.PickFocusedAsync(null, _pickCts.Token);
            if (result.Window is null)
            {
                StatusText = result.Message ?? L("无法读取焦点窗口", "Unable to read the focused window");
                return;
            }
            await RefreshAsync();
            var picked = _allWindows.FirstOrDefault(w => MatchPicked(w, result.Window)) ?? result.Window;
            if (!_allWindows.Contains(picked)) _allWindows.Add(picked);
            ApplyFilter();
            var selected = Windows.FirstOrDefault(w => w.Id == picked.Id) ?? picked;
            SelectedWindow = selected;
            StatusText = LF("当前焦点：{0}", "Current focus: {0}", selected.Title);
            PickBanner = LF("当前焦点：{0}", "Current focus: {0}", selected.Title);
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
        PickBanner = L("已请求取消…", "Cancelling…");
    }

    public async Task FocusSelectedAsync()
    {
        if (SelectedWindow is null) return;
        var ok = await _service.FocusAsync(SelectedWindow, CancellationToken.None);
        StatusText = ok
            ? LF("已聚焦：{0}", "Focused: {0}", SelectedWindow.Title)
            : L("该后端不支持聚焦操作", "This backend does not support focusing");
    }

    public async Task CloseSelectedAsync()
    {
        if (SelectedWindow is null) return;
        var ok = await _service.CloseAsync(SelectedWindow, CancellationToken.None);
        StatusText = ok
            ? LF("已发送关闭请求：{0}", "Close request sent: {0}", SelectedWindow.Title)
            : L("该后端不支持关闭操作", "This backend does not support closing");
        if (ok) await Task.Delay(300);
        if (ok) await RefreshAsync();
    }

    public async Task HighlightSelectedAsync()
    {
        if (SelectedWindow is null) return;
        await HighlightPickedAsync(SelectedWindow, reportStatus: true);
    }

    public async Task CaptureScreenshotAsync(bool activateTab = true)
    {
        if (SelectedWindow is null) return;
        var window = SelectedWindow;
        ScreenshotStatus = L("正在截图…", "Capturing…");
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
            StatusText = LF("已截图：{0} · {1}", "Screenshot captured: {0} · {1}", window.Title, ScreenshotStatus);
            if (activateTab) SelectedTabIndex = 7;
        }
        catch (Exception ex)
        {
            ScreenshotStatus = LF("截图失败：{0}", "Screenshot failed: {0}", ex.Message);
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
            GeneratedRule = L("# 先选择一个窗口", "# Select a window first");
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
                ? LF("已请求高亮 {0}（约 2.6 秒）", "Highlight requested for {0} (about 2.6 s)", window.Title)
                : L("当前后端不支持全局高亮，可用“聚焦”替代", "This backend does not support global highlighting; use “Focus” instead");
        }
        catch
        {
            if (reportStatus) StatusText = L("高亮请求失败", "Highlight request failed");
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
                SessionRows.Add(new InfoRow(L("会话类型", "Session type"), s.SessionType));
                SessionRows.Add(new InfoRow(L("桌面环境", "Desktop"), s.Desktop));
                SessionRows.Add(new InfoRow(L("Wayland 显示", "Wayland display"), s.WaylandDisplay));
                SessionRows.Add(new InfoRow(L("X11 显示", "X11 display"), s.X11Display));
                SessionRows.Add(new InfoRow(L("合成器", "Compositor"), s.Compositor, true));
                SessionRows.Add(new InfoRow(L("合成器版本", "Compositor version"), s.CompositorVersion));
                SessionRows.Add(new InfoRow("X11 WM", s.X11WmName));
                SessionRows.Add(new InfoRow(L("X11 屏幕", "X11 screen"), _service.Registry.X11?.Inspector.DisplayName, true));
                SessionRows.Add(new InfoRow("XDG_CURRENT_DESKTOP", Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")));
                SessionRows.Add(new InfoRow("XDG_SESSION_TYPE", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")));
                SessionRows.Add(new InfoRow("DBUS_SESSION_BUS_ADDRESS", Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"), true));
                SessionRows.Add(new InfoRow(L("进程 socket 检测", "Process socket detection"), L("通过 /proc/<pid>/fd + /proc/net/unix 判断 X11/Wayland 连接", "Detect X11/Wayland connections via /proc/<pid>/fd + /proc/net/unix"), wrap: true));
                SessionRows.Add(new InfoRow(L("设置文件", "Settings file"), _settingsService.FilePath, mono: true, wrap: true));
                SessionRows.Add(new InfoRow(L("设置持久化", "Settings persistence"), L("窗口尺寸 / 筛选 / 开关 / 规则 / XRecord 过滤自动保存；不恢复最后选中的窗口或 Tab", "Window size / filters / toggles / rules / XRecord filters are saved automatically; the last selected window and tab are not restored"), wrap: true));
                RealtimeRows.Clear();
                RealtimeRows.Add(new InfoRow(L("状态", "Status"), _monitor.IsRunning ? L("运行中", "Running") : L("未运行", "Stopped")));
                RealtimeRows.Add(new InfoRow(L("后端", "Backend"), _monitor.StatusText, wrap: true));
                RealtimeRows.Add(new InfoRow(L("事件数量", "Event count"), EventCountText));
                RealtimeRows.Add(new InfoRow(L("兜底刷新", "Fallback refresh"), _monitor.IsRunning ? L("15 秒（实时事件优先）", "15 s (live events preferred)") : L("3 秒轮询", "3 s polling")));
                RealtimeRows.Add(new InfoRow(L("说明", "Notes"), L("X11 使用独立 Display 连接选择 PropertyChange / StructureNotify / FocusChange；Wayland 使用合成器事件流。", "X11 uses a separate Display connection selecting PropertyChange / StructureNotify / FocusChange; Wayland uses compositor event streams."), wrap: true));
                WaylandProtocols.Clear();
                foreach (var protocol in protocols) WaylandProtocols.Add(protocol);
                UpdateSessionUi();
            });
        }
        catch { /* diagnostics is best effort */ }
    }

    public async Task InstallGnomeExtensionAsync()
    {
        GnomeInstallStatus = L("正在安装…", "Installing…");
        GnomeInstallStatus = await GnomeExtensionInstaller.InstallAsync(CancellationToken.None);
        await LoadDiagnosticsAsync();
    }

    public void RefreshDiagnostics() => _ = LoadDiagnosticsAsync();

    public async Task ExportDiagnosticsBundleAsync()
    {
        if (IsExportingDiagnosticsBundle) return;
        IsExportingDiagnosticsBundle = true;
        DiagnosticsBundleStatus = L("正在生成诊断包…", "Building diagnostics bundle…");
        try
        {
            if (!_isRefreshing && !IsBusy) await RefreshAsync(showBusy: false);
            await LoadDiagnosticsAsync();

            var inspection = SelectedWindow is { } selected
                ? new InspectionResult
                {
                    Window = selected,
                    Process = Process,
                    X11 = X11Details,
                    RawJson = RawJson,
                    Summary = Summary,
                }
                : null;

            var data = new DiagnosticsBundleData
            {
                Session = _service.Registry.Session,
                Providers = _service.Registry.GetStatuses(),
                Windows = _allWindows.ToList(),
                SelectedWindow = SelectedWindow,
                Inspection = inspection,
                Settings = _settingsService.Settings.Clone(),
                SettingsPath = _settingsService.FilePath,
                StatusText = StatusText,
                CountsText = CountsText,
                SessionRows = SessionRows.ToList(),
                RealtimeRows = RealtimeRows.ToList(),
                ScreenshotPng = _screenshotPng,
            };

            var path = await DiagnosticsBundleService.ExportAsync(data, CancellationToken.None);
            DiagnosticsBundleStatus = LF("已导出诊断包：{0}", "Diagnostics bundle exported: {0}", path);
            StatusText = DiagnosticsBundleStatus;
            AppLog.Info($"Diagnostics bundle exported: {path}");
        }
        catch (Exception ex)
        {
            DiagnosticsBundleStatus = LF("导出诊断包失败：{0}", "Failed to export diagnostics bundle: {0}", ex.Message);
            StatusText = DiagnosticsBundleStatus;
            AppLog.Error($"Diagnostics bundle export failed: {ex.Message}");
        }
        finally
        {
            IsExportingDiagnosticsBundle = false;
        }
    }

    private void StartMonitoring()
    {
        try
        {
            _monitor.Start(OnRealtimeEvent);
            RealtimeSummary = _monitor.IsRunning ? _monitor.StatusText : L("不可用", "Unavailable");
            OnPropertyChanged(nameof(AutoRefreshBadge));
            OnPropertyChanged(nameof(IsRealtimeActive));
        }
        catch (Exception ex)
        {
            RealtimeSummary = LF("启动失败：{0}", "Start failed: {0}", ex.Message);
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
        EventCountText = LF("{0} 条事件", "{0} events", _eventCount);
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
            StopX11ProtocolTimer();
            FlushX11ProtocolQueue();
            X11RecordEnabled = false;
            X11RecordStatus = L("已停止", "Stopped");
            StatusText = L("X11 RECORD 事件监视已停止", "X11 RECORD event monitoring stopped");
            return;
        }

        _x11EventMonitor.Filter = PassesX11Filter;
        _x11EventMonitor.ProtocolSink = OnX11ProtocolRecorded;
        var ok = _x11EventMonitor.Start(OnX11Recorded);
        X11RecordEnabled = ok;
        if (ok) StartX11ProtocolTimer();
        X11RecordStatus = ok
            ? _x11RecordOnlySelected
                ? L("录制中 · 仅选中窗口（RECORD 扩展）", "Recording · selected window only (RECORD)")
                : L("录制中 · 全局（RECORD 扩展）", "Recording · global (RECORD)")
            : LF("启动失败：{0}", "Start failed: {0}", _x11EventMonitor.Error);
        StatusText = ok ? L("X11 RECORD 事件监视已启动", "X11 RECORD event monitoring started") : X11RecordStatus;
    }

    private void StartX11ProtocolTimer()
    {
        _x11ProtocolTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _x11ProtocolTimer.Tick -= OnX11ProtocolTick;
        _x11ProtocolTimer.Tick += OnX11ProtocolTick;
        _x11ProtocolTimer.Start();
    }

    private void StopX11ProtocolTimer() => _x11ProtocolTimer?.Stop();

    private void OnX11ProtocolTick(object? sender, EventArgs e) => FlushX11ProtocolQueue();

    private void OnX11ProtocolRecorded(X11ProtocolRecord record)
        => _x11ProtocolQueue.Enqueue(record);

    private void FlushX11ProtocolQueue()
    {
        var added = 0;
        while (added < 1000 && _x11ProtocolQueue.TryDequeue(out var record))
        {
            _x11ProtocolAll.Add(record);
            added++;
        }

        if (added == 0) return;
        _x11ProtocolCount += added;
        if (_x11ProtocolAll.Count > 3000)
            _x11ProtocolAll.RemoveRange(0, _x11ProtocolAll.Count - 3000);
        X11ProtocolCountText = LF("{0} 条请求 / 回复", "{0} requests / replies", _x11ProtocolCount);
        ApplyX11ProtocolFilter();
    }

    private void ApplyX11ProtocolFilter()
    {
        var query = _x11ProtocolFilterText.Trim();
        X11ProtocolRecords.Clear();
        for (var i = _x11ProtocolAll.Count - 1; i >= 0; i--)
        {
            var record = _x11ProtocolAll[i];
            if (record.IsRequest && !_x11ProtocolShowRequests) continue;
            if (!record.IsRequest && !_x11ProtocolShowReplies) continue;
            if (query.Length > 0 &&
                !Contains(record.DisplayText, query) &&
                !Contains(record.Name, query) &&
                !Contains(record.Summary, query) &&
                !Contains(record.WindowHex, query) &&
                !Contains(record.ClientHex, query))
                continue;

            X11ProtocolRecords.Add(record);
            if (X11ProtocolRecords.Count >= 2000) break;
        }
    }

    public void ClearX11Protocol()
    {
        while (_x11ProtocolQueue.TryDequeue(out _)) { }
        _x11ProtocolAll.Clear();
        X11ProtocolRecords.Clear();
        _x11ProtocolCount = 0;
        X11ProtocolCountText = L("0 条请求 / 回复", "0 requests / replies");
    }

    public string BuildX11ProtocolText()
    {
        var sb = new System.Text.StringBuilder();
        if (X11ProtocolRecords.Count == 0)
            return L("TuxViewer X11 请求 / 回复（空）", "TuxViewer X11 requests / replies (empty)");

        sb.AppendLine(LF("TuxViewer X11 请求 / 回复 · {0} 条 · {1}",
            "TuxViewer X11 requests / replies · {0} · {1}",
            X11ProtocolRecords.Count, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine(new string('─', 60));
        foreach (var record in X11ProtocolRecords.Reverse())
        {
            sb.AppendLine(record.DisplayText);
            if (!string.IsNullOrWhiteSpace(record.Hex)) sb.AppendLine($"  {record.Hex}");
        }
        return sb.ToString();
    }

    private void OnX11Recorded(X11RecordedEvent recorded)
        => Dispatcher.UIThread.Post(() => AddX11Event(recorded));

    private void AddX11Event(X11RecordedEvent recorded)
    {
        X11Events.Insert(0, recorded);
        while (X11Events.Count > 2000) X11Events.RemoveAt(X11Events.Count - 1);
        _x11EventCount++;
        X11EventCountText = LF("{0} 条事件", "{0} events", _x11EventCount);
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

    public async Task LoadAtspiAsync()
    {
        if (SelectedWindow is null)
        {
            AtspiStatus = L("先选择一个窗口", "Select a window first");
            return;
        }

        var window = SelectedWindow;
        AtspiStatus = L("正在读取 AT-SPI 控件树…", "Reading the AT-SPI control tree…");
        try
        {
            var result = await AtspiService.LoadAsync(window, CancellationToken.None);
            if (SelectedWindow?.Id != window.Id) return;
            AtspiTree.Clear();
            foreach (var root in result.Roots) AtspiTree.Add(root);
            AtspiStatus = result.Message;
            AtspiCountText = result.Success ? LF("{0} 个控件", "{0} controls", result.NodeCount) : "";
            OnPropertyChanged(nameof(HasAtspiTree));
        }
        catch (Exception ex)
        {
            AtspiStatus = LF("读取失败：{0}", "Read failed: {0}", ex.Message);
        }
    }

    public void ResetAtspi()
    {
        AtspiTree.Clear();
        AtspiStatus = L("尚未加载", "Not loaded");
        AtspiCountText = "";
        OnPropertyChanged(nameof(HasAtspiTree));
    }

    public string BuildAtspiText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L("TuxViewer · AT-SPI 控件树", "TuxViewer · AT-SPI control tree"));
        sb.AppendLine(new string('─', 50));
        foreach (var root in AtspiTree) AppendAtspi(sb, root, 0);
        return sb.ToString();
    }

    private static void AppendAtspi(System.Text.StringBuilder sb, AtspiNode node, int depth)
    {
        sb.Append(new string(' ', depth * 2));
        sb.Append(node.Role.Length > 0 ? $"[{node.Role}] " : "");
        sb.AppendLine(node.Title);
        foreach (var child in node.Children) AppendAtspi(sb, child, depth + 1);
    }

    public void ClearX11Events()
    {
        X11Events.Clear();
        _x11EventCount = 0;
        X11EventCountText = L("0 条事件", "0 events");
        OnPropertyChanged(nameof(HasX11Events));
    }

    public string BuildX11EventsText()
    {
        if (X11Events.Count == 0) return L("TuxViewer X11 RECORD 事件（空）", "TuxViewer X11 RECORD events (empty)");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(LF("TuxViewer X11 RECORD 事件 · {0} 条 · {1}", "TuxViewer X11 RECORD events · {0} · {1}", X11Events.Count, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine(new string('─', 60));
        foreach (var record in X11Events.Reverse())
            sb.AppendLine(record.DisplayText);
        return sb.ToString();
    }

    public void ToggleGlobalHotkey()
    {
        if (_globalHotkey.IsRunning)
        {
            _globalHotkey.Stop();
            GlobalHotkeyEnabled = false;
            GlobalHotkeyStatus = L("已停用", "Disabled");
            StatusText = L("全局拾取热键已停用", "Global pick hotkey disabled");
            return;
        }

        var ok = _globalHotkey.Start(OnGlobalHotkeyPressed);
        GlobalHotkeyEnabled = ok;
        GlobalHotkeyStatus = ok
            ? L("已注册 Ctrl+Alt+X（XGrabKey，仅 X11 / 可收到 X11 按键的会话）", "Registered Ctrl+Alt+X (XGrabKey; X11 sessions or sessions that deliver X11 keys)")
            : LF("注册失败：{0}", "Registration failed: {0}", _globalHotkey.Error);
        StatusText = ok
            ? L("全局拾取热键已注册：Ctrl+Alt+X", "Global pick hotkey registered: Ctrl+Alt+X")
            : GlobalHotkeyStatus;
    }

    private void OnGlobalHotkeyPressed()
        => Dispatcher.UIThread.Post(() =>
        {
            if (IsPicking) return;
            StatusText = L("全局拾取热键：启动点击拾取", "Global hotkey: starting click pick");
            _ = PickAsync(mode: PickMode.Click);
        });

    public string BuildHotkeyText() => HotkeyConfig;

    private void ResetControlFields(WindowInfo? window)
    {
        _forceKillArmed = false;
        _forceKillResetCts?.Cancel();
        OpacityValue = 100;
        GeoX = window?.X?.ToString() ?? "";
        GeoY = window?.Y?.ToString() ?? "";
        GeoW = window?.Width?.ToString() ?? "";
        GeoH = window?.Height?.ToString() ?? "";
        ControlStatus = "";
        OnPropertyChanged(nameof(ForceKillButtonText));
    }

    private async Task RunControlAsync(Func<Task<(bool Ok, string Message)>> action, bool refresh = true)
    {
        if (SelectedWindow is null) return;
        try
        {
            var (ok, message) = await action();
            ControlStatus = message;
            StatusText = message;
            if (ok && refresh) await RefreshAsync(showBusy: false);
        }
        catch (Exception ex)
        {
            ControlStatus = LF("操作失败：{0}", "Action failed: {0}", ex.Message);
            StatusText = ControlStatus;
        }
    }

    public Task SetAboveSelectedAsync(bool below = false, bool clear = false)
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;
        return RunControlAsync(() => _service.Registry.SetAboveAsync(window, below, clear, CancellationToken.None));
    }

    public Task SetFullscreenSelectedAsync(bool enabled)
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;
        return RunControlAsync(() => _service.Registry.SetFullscreenAsync(window, enabled, CancellationToken.None));
    }

    public Task SetStickySelectedAsync(bool enabled)
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;
        return RunControlAsync(() => _service.Registry.SetStickyAsync(window, enabled, CancellationToken.None));
    }

    public Task ToggleFloatingSelectedAsync()
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;
        return RunControlAsync(() => _service.Registry.ToggleFloatingAsync(window, CancellationToken.None));
    }

    public async Task ApplyOpacitySelectedAsync()
    {
        if (SelectedWindow is not { } window) return;
        var opacity = Math.Clamp(OpacityValue / 100.0, 0, 1);
        await RunControlAsync(() => _service.Registry.SetOpacityAsync(window, opacity, CancellationToken.None));
    }

    public Task ClearOpacitySelectedAsync()
    {
        if (SelectedWindow is not { } window) return Task.CompletedTask;
        return RunControlAsync(() => _service.Registry.ClearOpacityAsync(window, CancellationToken.None));
    }

    public async Task ApplyGeometrySelectedAsync()
    {
        if (SelectedWindow is not { } window) return;
        var x = int.TryParse(GeoX, out var parsedX) ? parsedX : window.X ?? 0;
        var y = int.TryParse(GeoY, out var parsedY) ? parsedY : window.Y ?? 0;
        var w = int.TryParse(GeoW, out var parsedW) ? parsedW : window.Width ?? 0;
        var h = int.TryParse(GeoH, out var parsedH) ? parsedH : window.Height ?? 0;
        await RunControlAsync(() => _service.Registry.MoveResizeAsync(window, x, y, w, h, CancellationToken.None));
    }

    public async Task ForceKillSelectedAsync()
    {
        if (SelectedWindow is not { } window) return;
        if (!_forceKillArmed)
        {
            _forceKillArmed = true;
            OnPropertyChanged(nameof(ForceKillButtonText));
            ControlStatus = L("危险操作：再次点击“再次点击确认强制结束”将直接终止 X11 客户端", "Dangerous: clicking “Click again to force kill” again will terminate the X11 client immediately");
            _forceKillResetCts?.Cancel();
            _forceKillResetCts = new CancellationTokenSource();
            var token = _forceKillResetCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(5000, token); } catch { return; }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_forceKillArmed) return;
                    _forceKillArmed = false;
                    OnPropertyChanged(nameof(ForceKillButtonText));
                    ControlStatus = L("已取消强制结束", "Force kill cancelled");
                });
            });
            return;
        }

        _forceKillArmed = false;
        OnPropertyChanged(nameof(ForceKillButtonText));
        await RunControlAsync(() => _service.Registry.KillAsync(window, CancellationToken.None));
    }

    public async Task OpenExeFolderAsync()
    {
        var exe = Process?.Exe;
        if (string.IsNullOrWhiteSpace(exe) && SelectedWindow?.Pid is int pid)
        {
            try { exe = new FileInfo($"/proc/{pid}/exe").LinkTarget; } catch { /* ignore */ }
        }
        if (string.IsNullOrWhiteSpace(exe))
        {
            ControlStatus = L("没有可执行文件路径", "No executable path available");
            return;
        }
        var directory = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(directory))
        {
            ControlStatus = LF("无法解析目录：{0}", "Cannot resolve directory: {0}", exe);
            return;
        }
        var result = await CommandRunner.TryRunAsync("xdg-open", [directory], 4000, CancellationToken.None);
        ControlStatus = result?.Success == true
            ? LF("已打开 {0}", "Opened {0}", directory)
            : LF("打开目录失败：{0}", "Failed to open directory: {0}", result?.StdErr);
    }

    public string BuildProcessText()
    {
        var process = Process;
        if (process is null) return SelectedWindow?.Pid?.ToString() ?? "";
        return string.Join('\n',
            $"PID: {process.Pid}",
            $"PPID: {process.ParentPid}",
            $"Name: {process.Name}",
            $"Exe: {process.Exe}",
            $"Cmdline: {process.Cmdline}",
            $"Cwd: {process.Cwd}");
    }

    private void ApplyPropertyFilter()
    {
        var query = _propertyFilterText.Trim();
        X11Properties.Clear();
        foreach (var property in _x11PropertiesAll)
        {
            if (query.Length == 0 ||
                property.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                property.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                property.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                X11Properties.Add(property);
            }
        }
    }

    public void LoadSnapshots()
    {
        Snapshots.Clear();
        foreach (var snapshot in SnapshotService.LoadAll()) Snapshots.Add(snapshot);
        OnPropertyChanged(nameof(HasSnapshots));
    }

    private WindowSnapshot? BuildCurrentSnapshot(string name, bool includeScreenshot)
    {
        if (SelectedWindow is not { } window) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _lastOverviewSnapshot) values[$"overview/{key}"] = value;
        foreach (var (key, value) in _lastX11RowSnapshot) values[$"x11/{key}"] = value;
        foreach (var property in _x11PropertiesAll) values[$"property/{property.Name}"] = property.Value;
        foreach (var row in ProcessRows) values[$"process/{row.Label}"] = row.Value;

        return new WindowSnapshot
        {
            Name = name,
            WindowId = window.Id,
            Title = window.Title,
            AppId = window.AppId,
            Pid = window.Pid,
            Backend = window.BackendLabel,
            Geometry = window.GeometryText,
            Summary = Summary,
            RawJson = RawJson,
            Values = values,
            ScreenshotBase64 = includeScreenshot && ScreenshotPng is { Length: > 0 } png
                ? Convert.ToBase64String(png)
                : null,
        };
    }

    public async Task SaveSnapshotAsync()
    {
        if (SelectedWindow is null)
        {
            SnapshotStatus = L("先选择一个窗口", "Select a window first");
            return;
        }

        var name = string.IsNullOrWhiteSpace(SnapshotName)
            ? $"{SelectedWindow.Title} {DateTime.Now:MM-dd HH:mm}"
            : SnapshotName.Trim();
        if (ScreenshotPng is not { Length: > 0 })
            await EnsureScreenshotAsync();
        var snapshot = BuildCurrentSnapshot(name, includeScreenshot: true);
        if (snapshot is null) return;
        try
        {
            await SnapshotService.SaveAsync(snapshot, CancellationToken.None);
            SnapshotStatus = LF("已保存快照：{0}", "Snapshot saved: {0}", name);
            SnapshotName = "";
            LoadSnapshots();
            SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Id == snapshot.Id);
        }
        catch (Exception ex)
        {
            SnapshotStatus = LF("保存快照失败：{0}", "Failed to save snapshot: {0}", ex.Message);
        }
    }

    public void CompareSelectedSnapshot()
    {
        if (SelectedSnapshot is null)
        {
            SnapshotStatus = L("先选择一个快照", "Select a snapshot first");
            return;
        }
        if (SelectedWindow is null)
        {
            SnapshotStatus = L("先选择一个窗口", "Select a window first");
            return;
        }

        var current = BuildCurrentSnapshot(L("当前", "Current"), includeScreenshot: false);
        if (current is null) return;
        var diffs = SnapshotService.Compare(SelectedSnapshot, current);
        if (diffs.Count == 0)
        {
            SnapshotDiffText = L("没有差异：当前窗口与快照一致。", "No differences: the current window matches the snapshot.");
            SnapshotStatus = L("对比完成：无差异", "Comparison complete: no differences");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(LF("与快照“{0}”对比 · {1} 处差异", "Compared with snapshot “{0}” · {1} differences", SelectedSnapshot.Name, diffs.Count));
        sb.AppendLine(new string('─', 50));
        foreach (var diff in diffs) sb.AppendLine(diff.DisplayText);
        SnapshotDiffText = sb.ToString();
        SnapshotStatus = LF("对比完成：{0} 处差异", "Comparison complete: {0} differences", diffs.Count);
        OnPropertyChanged(nameof(HasSnapshotDiff));
    }

    public void DeleteSelectedSnapshot()
    {
        if (SelectedSnapshot is not { } snapshot) return;
        SnapshotService.Delete(snapshot);
        SnapshotStatus = LF("已删除快照：{0}", "Snapshot deleted: {0}", snapshot.Name);
        SelectedSnapshot = null;
        LoadSnapshots();
    }

    public void SetReportStatus(string message) => ReportStatus = message;

    public async Task<string> BuildReportHtmlAsync()
    {
        if (SelectedWindow is not { } window) return "";
        await EnsureScreenshotAsync();
        var dataUri = ScreenshotPng is { Length: > 0 } png
            ? "data:image/png;base64," + Convert.ToBase64String(png)
            : null;
        var html = ReportService.BuildHtml(window, Process, X11Details, dataUri, RawJson);
        ReportStatus = L("HTML 报告已生成", "HTML report generated");
        return html;
    }

    public async Task<string> BuildReportMarkdownAsync(string imageFileName)
    {
        if (SelectedWindow is not { } window) return "";
        await EnsureScreenshotAsync();
        var image = ScreenshotPng is { Length: > 0 } ? imageFileName : null;
        var markdown = ReportService.BuildMarkdown(window, Process, X11Details, image, RawJson);
        ReportStatus = L("Markdown 报告已生成", "Markdown report generated");
        return markdown;
    }

    public async Task EnsureScreenshotAsync()
    {
        if (ScreenshotPng is { Length: > 0 }) return;
        if (SelectedWindow is null) return;
        await CaptureScreenshotAsync(activateTab: false);
    }

    public async Task LoadOutputsAsync()
    {
        try
        {
            var result = await OutputService.GetAsync(_service.Registry, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Outputs.Clear();
                foreach (var output in result.Outputs) Outputs.Add(output);
                OutputWorkspaces.Clear();
                foreach (var workspace in result.Workspaces) OutputWorkspaces.Add(workspace);
                OutputLayoutLines.Clear();
                foreach (var line in result.LayoutLines) OutputLayoutLines.Add(line);
                OutputSource = result.Source;
                OutputStatus = result.Outputs.Count == 0
                    ? LF("{0} · 未检测到输出", "{0} · no outputs detected", result.Source)
                    : LF("{0} · {1} 个输出 · {2} 个工作区", "{0} · {1} outputs · {2} workspaces",
                        result.Source, result.Outputs.Count, result.Workspaces.Count);
                OutputNote = result.Note;
                OnPropertyChanged(nameof(HasOutputs));
            });
        }
        catch (Exception ex)
        {
            OutputStatus = LF("读取输出失败：{0}", "Failed to read outputs: {0}", ex.Message);
        }
    }

    public void ClearEvents()
    {
        Events.Clear();
        _eventCount = 0;
        EventCountText = L("0 条事件", "0 events");
        OnPropertyChanged(nameof(HasEvents));
    }

    public string BuildEventsText()
    {
        if (Events.Count == 0) return L("TuxViewer 实时事件（空）", "TuxViewer live events (empty)");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(LF("TuxViewer 实时事件 · {0} 条 · {1}", "TuxViewer live events · {0} · {1}", Events.Count, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
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
        _x11ProtocolTimer?.Stop();
        _pickCts?.Cancel();
        _refreshCts?.Cancel();
        _inspectCts?.Cancel();
        _eventDebounceCts?.Cancel();
        _monitor.Dispose();
        _x11EventMonitor.Dispose();
        _globalHotkey.Dispose();
        _forceKillResetCts?.Cancel();
        _forceKillResetCts?.Dispose();
        _eventDebounceCts?.Dispose();
        PersistNow();
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
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = LF("读取详情失败：{0}", "Failed to read details: {0}", ex.Message));
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

        Add(L("标题", "Title"), w.Title, wrap: true);
        Add(L("应用 ID", "App ID"), w.AppId);
        Add("WM_CLASS", w.WmClass);
        Add(L("后端", "Backend"), w.BackendLabel);
        Add(L("是否 X11 应用", "Is X11 app"), w.Backend == WindowBackend.X11
            ? (w.IsXWayland
                ? L("是 · XWayland 客户端", "Yes · XWayland client")
                : L("是 · 原生 X11 客户端", "Yes · native X11 client"))
            : L("否 · Wayland 原生客户端", "No · Wayland-native client"));
        Add(L("来源", "Source"), w.SourceName);
        Add("TuxViewer ID", w.Id, mono: true);
        Add(L("原生 ID", "Native ID"), w.NativeId, mono: true);
        var effectiveX11Id = w.X11WindowId ?? w.LinkedX11WindowId;
        Add(L("X11 窗口", "X11 window"), effectiveX11Id is not null ? $"0x{effectiveX11Id:x}" : null, mono: true);
        if (w.X11WindowId is not null && w.LinkedX11WindowId is not null)
            Add(L("关联 X11 窗口", "Linked X11 window"), $"0x{w.LinkedX11WindowId:x}", mono: true);
        Add("PID", w.Pid?.ToString());
        Add(w.Backend == WindowBackend.X11 && Session.IsWaylandSession
            ? L("几何（合成器）", "Geometry (compositor)")
            : L("几何", "Geometry"), w.GeometryText, mono: true);
        Add(L("工作区", "Workspace"), w.WorkspaceName ?? w.WorkspaceId?.ToString());
        Add(L("输出", "Output"), w.OutputName);
        Add(L("焦点", "Focused"), w.IsFocused ? L("是", "Yes") : L("否", "No"));
        Add(L("映射状态", "Mapping"), w.IsMapped ? L("已映射", "Mapped") : L("未映射 / 最小化", "Unmapped / minimized"));
        Add("Override Redirect", w.IsOverrideRedirect ? L("是", "Yes") : L("否", "No"));
        foreach (var (key, value) in w.Extra)
        {
            var label = LocalizeExtraKey(key);
            if (rows.Any(r => r.Label == label)) continue;
            Add(label, LocalizeExtraValue(value), wrap: value.Length > 60);
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
            WaylandRows.Add(new InfoRow(LocalizeExtraKey(key), LocalizeExtraValue(value), wrap: value.Length > 60));
    }

    private static string LocalizeExtraKey(string key) => key switch
    {
        "窗口 ID" => L("窗口 ID", "Window ID"),
        "节点 ID" => L("节点 ID", "Node ID"),
        "实例名" => L("实例名", "Instance name"),
        "窗口类" => L("窗口类", "Window class"),
        "PID 来源" => L("PID 来源", "PID source"),
        "映射状态" => L("映射状态", "Mapping"),
        "窗口类型" => L("窗口类型", "Window type"),
        "X11 窗口 ID" => L("X11 窗口 ID", "X11 window ID"),
        "浮动" => L("浮动", "Floating"),
        "紧急" => L("紧急", "Urgent"),
        "全屏" => L("全屏", "Fullscreen"),
        "瓦片尺寸" => L("瓦片尺寸", "Tile size"),
        "布局位置" => L("布局位置", "Layout position"),
        "最近聚焦" => L("最近聚焦", "Last focused"),
        "工作区" => L("工作区", "Workspace"),
        "输出" => L("输出", "Output"),
        "地址" => L("地址", "Address"),
        "XWayland" => "XWayland",
        _ => key,
    };

    private static string LocalizeExtraValue(string value)
    {
        if (value == "是") return L("是", "Yes");
        if (value == "否") return L("否", "No");
        const string recentPrefix = "合成器启动后 ";
        return value.StartsWith(recentPrefix, StringComparison.Ordinal)
            ? L("合成器启动后 ", "Since compositor start ") + value[recentPrefix.Length..]
            : value;
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
            ProcessRows.Add(new InfoRow(L("状态", "Status"), L("没有可用的 /proc 信息（PID 为空或权限不足）", "No /proc information available (empty PID or insufficient permissions)"), wrap: true));
            return;
        }

        ProcessRows.Add(new InfoRow(L("进程名", "Process name"), p.Name));
        ProcessRows.Add(new InfoRow("PID / PPID", $"{p.Pid} / {p.ParentPid}"));
        ProcessRows.Add(new InfoRow(L("用户", "User"), p.User));
        ProcessRows.Add(new InfoRow(L("状态", "Status"), p.State));
        ProcessRows.Add(new InfoRow(L("可执行文件", "Executable"), p.Exe, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow(L("启动命令", "Command line"), p.Cmdline, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow(L("工作目录", "Working directory"), p.Cwd, mono: true, wrap: true));
        ProcessRows.Add(new InfoRow(L("启动时间", "Started at"), p.StartedAt));
        ProcessRows.Add(new InfoRow(L("内存 / 线程", "Memory / threads"), $"{p.RssKb / 1024.0:0.#} MiB / {p.Threads}"));
        ProcessRows.Add(new InfoRow(L("工具包", "Toolkit"), p.Toolkit.DisplayName));
        ProcessRows.Add(new InfoRow(L("客户端连接", "Client connections"), p.Backend.Display));
        ProcessRows.Add(new InfoRow(L("沙箱", "Sandbox"), p.Sandbox));
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
        _x11PropertiesAll = [];
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

        Add(L("窗口 ID", "Window ID"), x11.IdHex, mono: true);
        Add(L("标题", "Title"), x11.Title, wrap: true);
        Add(L("实例 / 类", "Instance / class"), $"{x11.InstanceName} / {x11.ClassName}", mono: true);
        Add(L("几何 (X11 坐标)", "Geometry (X11 coords)"), $"{x11.Width}×{x11.Height} @ {x11.X},{x11.Y}", mono: true);
        if (Session.IsWaylandSession)
            Add(L("坐标提示", "Coordinate note"), L("XWayland 的 X11 坐标可能经过缩放或偏移，和合成器布局不一致", "XWayland X11 coordinates may be scaled or offset and differ from the compositor layout"), wrap: true);
        Add(L("边框 / 深度", "Border / depth"), $"{x11.BorderWidth} / {x11.Depth}");
        Add(L("映射", "Mapping"), x11.Mapped ? "viewable" : "unmapped");
        Add("Override Redirect", x11.OverrideRedirect ? L("是", "Yes") : L("否", "No"));
        Add("Input Only", x11.InputOnly ? L("是", "Yes") : L("否", "No"));
        Add(L("窗口类型", "Window type"), x11.WindowType);
        Add("WM State", x11.WmState);
        Add("Transient For", x11.TransientFor, mono: true);
        Add(L("协议", "Protocols"), x11.Protocols, wrap: true);
        Add("WM Normal Hints", x11.WmNormalHints, mono: true, wrap: true);
        Add("Root / Parent", $"{x11.RootWindow} / {x11.ParentWindow}", mono: true);
        Add(L("直接子窗口", "Direct children"), x11.ChildCount.ToString());
        Add(L("事件掩码", "Event mask"), x11.EventMask, mono: true, wrap: true);

        foreach (var row in rows) X11Rows.Add(row);
        _lastX11RowSnapshot.Clear();
        foreach (var row in rows) _lastX11RowSnapshot[row.Label] = row.Value;

        var anyChanged = rows.Any(r => r.IsChanged);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var properties = new List<X11Property>();
        foreach (var property in x11.Properties)
        {
            var changed = false;
            string? oldValue = null;
            if (diff && _lastX11PropertySnapshot.TryGetValue(property.Name, out oldValue))
                changed = !string.Equals(oldValue, property.Value, StringComparison.Ordinal);
            anyChanged |= changed;
            properties.Add(changed ? property.WithChange(true, oldValue) : property);
            current[property.Name] = property.Value;
        }
        _lastX11PropertySnapshot.Clear();
        foreach (var (key, value) in current) _lastX11PropertySnapshot[key] = value;
        _x11PropertiesAll = properties;
        ApplyPropertyFilter();

        foreach (var child in x11.Children) X11Children.Add(child);
        return anyChanged;
    }

    private void BuildSockets(ProcessSnapshot? process)
    {
        SocketRows.Clear();
        if (process is null) return;
        SocketRows.Add(new InfoRow("X11 socket", process.Backend.HasX11Socket ? L("已连接", "Connected") : L("未连接", "Not connected")));
        SocketRows.Add(new InfoRow("Wayland socket", process.Backend.HasWaylandSocket ? L("已连接", "Connected") : L("未连接", "Not connected")));
        SocketRows.Add(new InfoRow("D-Bus socket", process.Backend.HasDbusSocket ? L("已连接", "Connected") : L("未连接", "Not connected")));
        SocketRows.Add(new InfoRow("PipeWire socket", process.Backend.HasPipeWireSocket ? L("已连接", "Connected") : L("未连接", "Not connected")));
        foreach (var socket in process.Sockets)
            SocketRows.Add(new InfoRow(socket.Kind, $"{socket.Path}  (inode {socket.Inode})", mono: true, wrap: true));
    }

    private void ApplyFilter()
    {
        var query = _filterText.Trim();
        var errors = new List<string>();
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

        var pidTokens = SplitFilterTokens(_pidFilterText);
        if (pidTokens.Count > 0)
        {
            var pids = new HashSet<int>();
            foreach (var token in pidTokens)
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0)
                    pids.Add(pid);
                else
                    errors.Add(LF("PID “{0}” 不是有效整数", "PID “{0}” is not a valid integer", token));
            }
            if (pids.Count > 0)
                filtered = filtered.Where(w => w.Pid is int pid && pids.Contains(pid));
        }

        var windowTokens = SplitFilterTokens(_windowIdFilterText);
        if (windowTokens.Count > 0)
            filtered = filtered.Where(w => MatchesWindowId(w, windowTokens));

        var workspaceTokens = SplitFilterTokens(_workspaceFilterText);
        if (workspaceTokens.Count > 0)
            filtered = filtered.Where(w => MatchesWorkspace(w, workspaceTokens));

        if (query.Length > 0)
        {
            if (UseRegexSearch)
            {
                Regex? regex = null;
                try
                {
                    regex = new Regex(
                        query,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    errors.Add(LF("正则表达式无效：{0}", "Invalid regular expression: {0}", ex.Message));
                }

                if (regex is not null)
                {
                    try
                    {
                        var compiled = regex;
                        filtered = filtered.Where(w => compiled.IsMatch(BuildSearchHaystack(w))).ToList();
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        errors.Add(L("正则匹配超时（250 ms），请简化表达式", "Regex match timed out (250 ms); simplify the expression"));
                    }
                }
            }
            else
            {
                filtered = filtered.Where(w =>
                    BuildSearchHaystack(w).Contains(query, StringComparison.OrdinalIgnoreCase));
            }
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

        FilterError = errors.Count == 0 ? "" : string.Join(" · ", errors);
        FilterSummaryText = list.Count == _allWindows.Count && errors.Count == 0
            ? ""
            : $"{list.Count} / {_allWindows.Count}";
    }

    private static string BuildSearchHaystack(WindowInfo window)
    {
        var builder = new System.Text.StringBuilder(128);
        void Add(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            builder.Append(value);
            builder.Append('\n');
        }

        Add(window.Title);
        Add(window.AppId);
        Add(window.WmClass);
        Add(window.Pid?.ToString(CultureInfo.InvariantCulture));
        Add(window.NativeId);
        Add(window.Id);
        Add(window.SourceName);
        Add(window.WorkspaceName);
        Add(window.WorkspaceId?.ToString(CultureInfo.InvariantCulture));
        Add(window.OutputName);
        Add(window.BackendLabel);
        foreach (var pair in window.Extra)
        {
            Add(pair.Key);
            Add(pair.Value);
        }
        return builder.ToString();
    }

    private static List<string> SplitFilterTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text
            .Split([',', ';', '，', '；', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToList();
    }

    private static bool MatchesWindowId(WindowInfo window, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            var raw = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;

            if (ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                if (window.X11WindowId == hex || window.LinkedX11WindowId == hex) return true;
                if (string.Equals(window.NativeId, $"0x{hex:x}", StringComparison.OrdinalIgnoreCase)) return true;
            }

            if (ulong.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            {
                if (window.X11WindowId == dec || window.LinkedX11WindowId == dec) return true;
            }

            if (Contains(window.NativeId, token) || Contains(window.Id, token)) return true;
        }
        return false;
    }

    private static bool MatchesWorkspace(WindowInfo window, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (string.Equals(window.WorkspaceName, token, StringComparison.OrdinalIgnoreCase)) return true;
            if (Contains(window.WorkspaceName, token)) return true;
            if (window.WorkspaceId is int id &&
                (id.ToString(CultureInfo.InvariantCulture) == token ||
                 L($"工作区 {id}", $"Workspace {id}").Contains(token, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private void RebuildLocalizedOptions()
    {
        var backend = Math.Clamp(_backendFilterIndex, 0, 4);
        var group = Math.Clamp(_treeGroupModeIndex, 0, 3);

        BackendFilters.Clear();
        BackendFilters.Add(LocalizationService.L("全部后端", "All backends"));
        BackendFilters.Add(LocalizationService.L("X11（含 XWayland）", "X11 (incl. XWayland)"));
        BackendFilters.Add(LocalizationService.L("Wayland 原生", "Wayland native"));
        BackendFilters.Add(LocalizationService.L("仅 XWayland", "XWayland only"));
        BackendFilters.Add(LocalizationService.L("仅 X11 原生", "X11 native only"));

        TreeGroupModes.Clear();
        TreeGroupModes.Add(LocalizationService.L("按应用", "By app"));
        TreeGroupModes.Add(LocalizationService.L("按进程", "By process"));
        TreeGroupModes.Add(LocalizationService.L("按工作区", "By workspace"));
        TreeGroupModes.Add(LocalizationService.L("平铺", "Flat"));

        _backendFilterIndex = backend;
        _treeGroupModeIndex = group;
        OnPropertyChanged(nameof(BackendFilterIndex));
        OnPropertyChanged(nameof(TreeGroupModeIndex));
    }

    private void LoadFilterPresets(IEnumerable<FilterPreset> presets)
    {
        FilterPresets.Clear();
        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name)) continue;
            FilterPresets.Add(preset.Clone());
        }
    }

    public void SaveFilterPreset()
    {
        var name = FilterPresetName.Trim();
        if (name.Length == 0) name = LF("筛选 {0}", "Filter {0}", FilterPresets.Count + 1);

        var preset = new FilterPreset
        {
            Name = name,
            Search = _filterText,
            UseRegex = _useRegexSearch,
            BackendFilterIndex = _backendFilterIndex,
            Pid = _pidFilterText,
            WindowId = _windowIdFilterText,
            Workspace = _workspaceFilterText,
            HideOwnWindows = _hideOwnWindows,
            IncludeRawX11 = _includeRawX11,
            IncludeUnmapped = _includeUnmapped,
        };

        var index = -1;
        for (var i = 0; i < FilterPresets.Count; i++)
        {
            if (!string.Equals(FilterPresets[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }

        if (index >= 0) FilterPresets[index] = preset;
        else
        {
            FilterPresets.Add(preset);
            index = FilterPresets.Count - 1;
        }

        SelectedFilterPresetIndex = index;
        FilterPresetName = "";
        SaveFilterPresets();
        StatusText = LF("已保存筛选预设“{0}”", "Filter preset saved: “{0}”", name);
    }

    public void ApplySelectedFilterPreset()
    {
        if (_selectedFilterPresetIndex < 0 || _selectedFilterPresetIndex >= FilterPresets.Count) return;
        ApplyFilterPreset(FilterPresets[_selectedFilterPresetIndex]);
    }

    public void DeleteSelectedFilterPreset()
    {
        if (_selectedFilterPresetIndex < 0 || _selectedFilterPresetIndex >= FilterPresets.Count) return;
        var name = FilterPresets[_selectedFilterPresetIndex].Name;
        FilterPresets.RemoveAt(_selectedFilterPresetIndex);
        SelectedFilterPresetIndex = FilterPresets.Count == 0
            ? -1
            : Math.Min(_selectedFilterPresetIndex, FilterPresets.Count - 1);
        SaveFilterPresets();
        StatusText = LF("已删除筛选预设“{0}”", "Filter preset deleted: “{0}”", name);
    }

    public void ResetFilters()
    {
        _filterText = "";
        _useRegexSearch = false;
        _pidFilterText = "";
        _windowIdFilterText = "";
        _workspaceFilterText = "";
        _backendFilterIndex = 0;
        _hideOwnWindows = true;
        _includeRawX11 = false;
        _includeUnmapped = true;
        _onlySelectedPid = false;
        _pidFilter = null;
        _selectedFilterPresetIndex = -1;

        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(UseRegexSearch));
        OnPropertyChanged(nameof(PidFilterText));
        OnPropertyChanged(nameof(WindowIdFilterText));
        OnPropertyChanged(nameof(WorkspaceFilterText));
        OnPropertyChanged(nameof(BackendFilterIndex));
        OnPropertyChanged(nameof(HideOwnWindows));
        OnPropertyChanged(nameof(IncludeRawX11));
        OnPropertyChanged(nameof(IncludeUnmapped));
        OnPropertyChanged(nameof(OnlySelectedPid));
        OnPropertyChanged(nameof(SelectedFilterPresetIndex));
        ApplyFilter();
        StatusText = L("已清除窗口筛选", "Window filters cleared");
    }

    private void ApplyFilterPreset(FilterPreset preset)
    {
        _filterText = preset.Search;
        _useRegexSearch = preset.UseRegex;
        _pidFilterText = preset.Pid;
        _windowIdFilterText = preset.WindowId;
        _workspaceFilterText = preset.Workspace;
        _backendFilterIndex = Math.Clamp(preset.BackendFilterIndex, 0, 4);
        _hideOwnWindows = preset.HideOwnWindows;
        _includeRawX11 = preset.IncludeRawX11;
        _includeUnmapped = preset.IncludeUnmapped;
        _selectedFilterPresetIndex = FilterPresets.IndexOf(preset);

        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(UseRegexSearch));
        OnPropertyChanged(nameof(PidFilterText));
        OnPropertyChanged(nameof(WindowIdFilterText));
        OnPropertyChanged(nameof(WorkspaceFilterText));
        OnPropertyChanged(nameof(BackendFilterIndex));
        OnPropertyChanged(nameof(HideOwnWindows));
        OnPropertyChanged(nameof(IncludeRawX11));
        OnPropertyChanged(nameof(IncludeUnmapped));
        OnPropertyChanged(nameof(SelectedFilterPresetIndex));
        ApplyFilter();
        StatusText = LF("已应用筛选预设“{0}”", "Applied filter preset: “{0}”", preset.Name);
    }

    private void SaveFilterPresets()
    {
        _settingsService.Update(settings =>
            settings.FilterPresets = FilterPresets.Select(preset => preset.Clone()).ToList());
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
                    GroupSubtitle = LF("{0} 个窗口", "{0} windows", group.Count()),
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
                SourceName = L("X11 · 子窗口", "X11 · child window"),
                NativeId = $"0x{child.Id:x}",
                Title = string.IsNullOrWhiteSpace(child.Title)
                    ? $"{child.ClassName ?? L("子窗口", "child")} 0x{child.Id:x}"
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
        1 => window.Pid is int pid ? $"PID {pid}" : L("未知 PID", "Unknown PID"),
        2 => window.WorkspaceName
             ?? (window.WorkspaceId is int id ? L($"工作区 {id}", $"Workspace {id}") : L("未知工作区", "Unknown workspace")),
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
        CountsText = LF("{0} 个窗口 · X11 {1} · Wayland {2} · XWayland {3} · 本应用 {4}",
            "{0} windows · X11 {1} · Wayland {2} · XWayland {3} · this app {4}",
            visible.Count, x11, wayland, xwayland, own);
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

    private static readonly HashSet<string> PersistedProperties = new(StringComparer.Ordinal)
    {
        nameof(BackendFilterIndex), nameof(HideOwnWindows), nameof(IncludeRawX11), nameof(IncludeUnmapped),
        nameof(UseRegexSearch),
        nameof(AutoRefresh), nameof(RealtimeEnabled), nameof(TreeMode), nameof(TreeGroupModeIndex),
        nameof(RuleCompositorIndex), nameof(RuleFloating), nameof(RuleNoBorder), nameof(RuleFullscreen),
        nameof(RuleSticky), nameof(RuleMatchTitle), nameof(RuleWidth), nameof(RuleHeight),
        nameof(RuleWorkspace), nameof(RuleOpacity), nameof(X11RecordKeys), nameof(X11RecordMouse),
        nameof(X11RecordConfigure), nameof(X11RecordProperty), nameof(X11RecordFocus), nameof(X11RecordMap),
        nameof(X11RecordClientMessage), nameof(X11RecordOther), nameof(X11RecordOnlySelected),
    };

    private void ApplySettings(AppSettings settings)
    {
        _suppressSettingsSave = true;
        try
        {
            _backendFilterIndex = Math.Clamp(settings.BackendFilterIndex, 0, 4);
            _hideOwnWindows = settings.HideOwnWindows;
            _includeRawX11 = settings.IncludeRawX11;
            _includeUnmapped = settings.IncludeUnmapped;
            _useRegexSearch = settings.UseRegexSearch;
            _autoRefresh = settings.AutoRefresh;
            _realtimeEnabled = settings.RealtimeEnabled;
            _treeMode = settings.TreeMode;
            _treeGroupModeIndex = Math.Clamp(settings.TreeGroupModeIndex, 0, 3);
            _ruleCompositorIndex = Math.Clamp(settings.RuleCompositorIndex, 0, 2);
            _ruleFloating = settings.RuleFloating;
            _ruleNoBorder = settings.RuleNoBorder;
            _ruleFullscreen = settings.RuleFullscreen;
            _ruleSticky = settings.RuleSticky;
            _ruleMatchTitle = settings.RuleMatchTitle;
            _ruleWidth = settings.RuleWidth;
            _ruleHeight = settings.RuleHeight;
            _ruleWorkspace = settings.RuleWorkspace;
            _ruleOpacity = settings.RuleOpacity;
            _x11RecordKeys = settings.X11RecordKeys;
            _x11RecordMouse = settings.X11RecordMouse;
            _x11RecordConfigure = settings.X11RecordConfigure;
            _x11RecordProperty = settings.X11RecordProperty;
            _x11RecordFocus = settings.X11RecordFocus;
            _x11RecordMap = settings.X11RecordMap;
            _x11RecordClientMessage = settings.X11RecordClientMessage;
            _x11RecordOther = settings.X11RecordOther;
            _x11RecordOnlySelected = settings.X11RecordOnlySelected;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void CaptureSettings(AppSettings settings)
    {
        settings.BackendFilterIndex = _backendFilterIndex;
        settings.HideOwnWindows = _hideOwnWindows;
        settings.IncludeRawX11 = _includeRawX11;
        settings.IncludeUnmapped = _includeUnmapped;
        settings.UseRegexSearch = _useRegexSearch;
        settings.AutoRefresh = _autoRefresh;
        settings.RealtimeEnabled = _realtimeEnabled;
        settings.TreeMode = _treeMode;
        settings.TreeGroupModeIndex = _treeGroupModeIndex;
        settings.RuleCompositorIndex = _ruleCompositorIndex;
        settings.RuleFloating = _ruleFloating;
        settings.RuleNoBorder = _ruleNoBorder;
        settings.RuleFullscreen = _ruleFullscreen;
        settings.RuleSticky = _ruleSticky;
        settings.RuleMatchTitle = _ruleMatchTitle;
        settings.RuleWidth = _ruleWidth;
        settings.RuleHeight = _ruleHeight;
        settings.RuleWorkspace = _ruleWorkspace;
        settings.RuleOpacity = _ruleOpacity;
        settings.X11RecordKeys = _x11RecordKeys;
        settings.X11RecordMouse = _x11RecordMouse;
        settings.X11RecordConfigure = _x11RecordConfigure;
        settings.X11RecordProperty = _x11RecordProperty;
        settings.X11RecordFocus = _x11RecordFocus;
        settings.X11RecordMap = _x11RecordMap;
        settings.X11RecordClientMessage = _x11RecordClientMessage;
        settings.X11RecordOther = _x11RecordOther;
        settings.X11RecordOnlySelected = _x11RecordOnlySelected;
    }

    private void OnPersistedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSettingsSave || e.PropertyName is null) return;
        if (!PersistedProperties.Contains(e.PropertyName)) return;
        _settingsService.Update(CaptureSettings);
    }

    public void PersistNow()
    {
        _settingsService.Update(CaptureSettings);
        _settingsService.Flush();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string L(string chinese, string english) => LocalizationService.L(chinese, english);

    private static string LF(string chineseFormat, string englishFormat, params object?[] args)
        => LocalizationService.F(chineseFormat, englishFormat, args);

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
