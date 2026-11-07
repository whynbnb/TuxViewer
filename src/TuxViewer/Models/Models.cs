using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using TuxViewer.Services;

namespace TuxViewer.Models;

public enum WindowBackend
{
    Unknown,
    X11,
    Wayland,
}

public enum PickMode
{
    Click,
    Drag,
}

public enum PickCapability
{
    None,
    Focused,
    Cursor,
    Interactive,
}

/// <summary>A single inspected toplevel window, normalised across all providers.</summary>
public sealed class WindowInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }

    /// <summary>Provider-native identifier (Wayland toplevel id, X11 window id, ...).</summary>
    public string? NativeId { get; init; }

    public string Title { get; set; } = "";
    public string? AppId { get; set; }
    public string? WmClass { get; set; }
    public int? Pid { get; set; }

    public WindowBackend Backend { get; set; } = WindowBackend.Unknown;
    public bool IsXWayland { get; set; }
    public bool IsFocused { get; set; }
    public bool IsMapped { get; set; } = true;
    public bool IsOwnWindow { get; set; }
    public bool IsOverrideRedirect { get; set; }
    public bool MergedIntoWayland { get; set; }

    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public int? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? OutputName { get; set; }

    /// <summary>X11 window id when this is an X11 / XWayland window.</summary>
    public ulong? X11WindowId { get; set; }

    /// <summary>X11 id of a companion XWayland window when merged with a compositor toplevel.</summary>
    public ulong? LinkedX11WindowId { get; set; }

    public string? RawJson { get; set; }

    public Dictionary<string, string> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved application icon (desktop entry / icon theme), when available.</summary>
    [JsonIgnore] public Bitmap? Icon { get; set; }
    [JsonIgnore] public bool HasIcon => Icon is not null;
    [JsonIgnore]
    public string IconInitial
    {
        get
        {
            var source = !string.IsNullOrWhiteSpace(Title) ? Title : AppId ?? WmClass ?? "?";
            var trimmed = source.TrimStart();
            return trimmed.Length == 0 ? "?" : char.ToUpperInvariant(trimmed[0]).ToString();
        }
    }

    [JsonIgnore] public bool IsWayland => Backend == WindowBackend.Wayland;
    [JsonIgnore] public bool IsX11 => Backend == WindowBackend.X11;

    [JsonIgnore]
    public string BackendLabel => Backend switch
    {
        WindowBackend.X11 when IsXWayland => "X11 · XWayland",
        WindowBackend.X11 => "X11",
        WindowBackend.Wayland => "Wayland",
        _ => LocalizationService.L("未知", "Unknown"),
    };

    [JsonIgnore]
    public string BackendShort => Backend switch
    {
        WindowBackend.X11 => "X11",
        WindowBackend.Wayland => "WL",
        _ => "?",
    };

    [JsonIgnore]
    public string GeometryText => Width is > 0 && Height is > 0
        ? $"{Width}×{Height}" + (X is not null && Y is not null ? $" @ {X},{Y}" : "")
        : "—";

    [JsonIgnore]
    public string PidText => Pid is > 0 ? $"PID {Pid}" : "PID —";

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var app = !string.IsNullOrWhiteSpace(AppId) ? AppId : WmClass;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(app)) parts.Add(app!);
            if (Pid is > 0) parts.Add($"pid {Pid}");
            parts.Add(SourceName);
            if (!string.IsNullOrWhiteSpace(WorkspaceName)) parts.Add(WorkspaceName!);
            else if (WorkspaceId is not null) parts.Add($"ws {WorkspaceId}");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore] public string FocusGlyph => IsFocused ? "●" : "";

    /// <summary>Re-evaluate language-dependent computed properties after a UI language switch.</summary>
    public void NotifyLanguageChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(BackendLabel), nameof(BackendShort), nameof(PidText), nameof(Subtitle),
                     nameof(IconInitial), nameof(FocusGlyph),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Update a window object in place during a refresh so the list selection and
    /// item containers are not recreated (avoids UI flicker every auto-refresh tick).
    /// </summary>
    public void RefreshFrom(WindowInfo other)
    {
        Title = other.Title;
        AppId = other.AppId;
        WmClass = other.WmClass;
        Pid = other.Pid;
        Backend = other.Backend;
        IsXWayland = other.IsXWayland;
        IsFocused = other.IsFocused;
        IsMapped = other.IsMapped;
        IsOwnWindow = other.IsOwnWindow;
        IsOverrideRedirect = other.IsOverrideRedirect;
        MergedIntoWayland = other.MergedIntoWayland;
        X = other.X;
        Y = other.Y;
        Width = other.Width;
        Height = other.Height;
        WorkspaceId = other.WorkspaceId;
        WorkspaceName = other.WorkspaceName;
        OutputName = other.OutputName;
        X11WindowId = other.X11WindowId;
        LinkedX11WindowId = other.LinkedX11WindowId;
        RawJson = other.RawJson;
        if (other.Icon is not null) Icon = other.Icon;

        Extra.Clear();
        foreach (var (key, value) in other.Extra) Extra[key] = value;

        foreach (var name in new[]
                 {
                     nameof(Title), nameof(AppId), nameof(WmClass), nameof(Pid), nameof(Backend),
                     nameof(IsXWayland), nameof(IsFocused), nameof(IsMapped), nameof(IsOwnWindow),
                     nameof(IsOverrideRedirect), nameof(MergedIntoWayland), nameof(X), nameof(Y),
                     nameof(Width), nameof(Height), nameof(WorkspaceId), nameof(WorkspaceName),
                     nameof(OutputName), nameof(X11WindowId), nameof(LinkedX11WindowId), nameof(RawJson),
                     nameof(IsX11), nameof(IsWayland), nameof(BackendLabel), nameof(BackendShort),
                     nameof(GeometryText), nameof(PidText), nameof(Subtitle), nameof(FocusGlyph),
                     nameof(Icon), nameof(HasIcon), nameof(IconInitial),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

public sealed class WindowNode : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public WindowInfo? Window { get; init; }
    public string? GroupTitle { get; init; }
    public string? GroupSubtitle { get; init; }

    public System.Collections.ObjectModel.ObservableCollection<WindowNode> Children { get; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public bool IsGroup => Window is null;
    public bool IsWindow => Window is not null;
    public string Title => Window?.Title ?? GroupTitle ?? "?";
    public string Subtitle => Window?.Subtitle ?? GroupSubtitle ?? "";
    public Avalonia.Media.Imaging.Bitmap? Icon => Window?.Icon;
    public bool HasIcon => Window?.Icon is not null;
    public string IconInitial => IsGroup ? "▾" : Window?.IconInitial ?? "?";
    public bool IsX11 => Window?.IsX11 == true;
    public bool IsWayland => Window?.IsWayland == true;
    public string BackendShort => Window?.BackendShort ?? "";
    public string BackendLabel => Window?.BackendLabel ?? "";
    public string GeometryText => Window?.GeometryText ?? "";
    public string FocusGlyph => Window?.FocusGlyph ?? "";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InfoRow(
    string label,
    string? value,
    bool mono = false,
    bool wrap = false,
    bool changed = false,
    string? oldValue = null)
{
    public string Label { get; } = label;
    public string Value { get; } = string.IsNullOrWhiteSpace(value) ? "—" : value!;
    public bool IsMono { get; } = mono;
    public bool Wrap { get; } = wrap;
    public bool IsChanged { get; } = changed;
    public string? OldValue { get; } = oldValue;
    public string ChangeText => IsChanged
        ? OldValue is null || OldValue.Length == 0
            ? LocalizationService.L("已变化", "changed")
            : LocalizationService.F("旧值：{0}", "Previous: {0}", OldValue)
        : "";
    public string FontFamily => IsMono
        ? "Cascadia Mono,DejaVu Sans Mono,Noto Sans Mono,monospace"
        : "Inter,Noto Sans CJK SC,Noto Sans SC,sans-serif";
}

public sealed class X11Property
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required int Format { get; init; }
    public required string Value { get; init; }
    public bool IsChanged { get; init; }
    public string? PreviousValue { get; init; }
    public string NameAndType => $"{Name}  [{Type}/{Format}]";
    public string ChangeText => IsChanged
        ? PreviousValue is null || PreviousValue.Length == 0
            ? LocalizationService.L("已变化", "changed")
            : LocalizationService.F("旧值：{0}", "Previous: {0}", Truncate(PreviousValue))
        : "";
    public bool HasChange => IsChanged;

    public X11Property WithChange(bool changed, string? previous) => new()
    {
        Name = Name,
        Type = Type,
        Format = Format,
        Value = Value,
        IsChanged = changed,
        PreviousValue = previous,
    };

    private static string Truncate(string value)
        => value.Length <= 220 ? value : value[..220] + " …";
}

public enum MonitorEventKind
{
    Unknown,
    Property,
    Title,
    Configure,
    Map,
    Unmap,
    Focus,
    Opened,
    Closed,
    Workspace,
    Generic,
}

public sealed class WindowEventRecord
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required string Source { get; init; }
    public string? NativeId { get; init; }
    public string? AppId { get; init; }
    public MonitorEventKind Kind { get; init; }
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string KindText => Kind switch
    {
        MonitorEventKind.Property => "PROPERTY",
        MonitorEventKind.Title => "TITLE",
        MonitorEventKind.Configure => "CONFIGURE",
        MonitorEventKind.Map => "MAP",
        MonitorEventKind.Unmap => "UNMAP",
        MonitorEventKind.Focus => "FOCUS",
        MonitorEventKind.Opened => "OPEN",
        MonitorEventKind.Closed => "CLOSE",
        MonitorEventKind.Workspace => "WORKSPACE",
        MonitorEventKind.Generic => "EVENT",
        _ => "UNKNOWN",
    };
    public string DisplayText => $"{TimeText}  {Source,-8} {KindText,-10} {Summary}";
}


public sealed class X11ChildInfo
{
    public required ulong Id { get; init; }
    public required int Depth { get; init; }
    public string? Title { get; init; }
    public string? ClassName { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Mapped { get; init; }
    public bool OverrideRedirect { get; init; }
    public string Indent => new(' ', Depth * 2);
    public string IdHex => $"0x{Id:x}";
    public string Geometry => $"{Width}×{Height} @ {X},{Y}";
    public string State => !Mapped ? "unmapped" : OverrideRedirect ? "override-redirect" : "mapped";
    public string DisplayText => $"{Indent}0x{Id:x} · {ClassName ?? "?"} · {(string.IsNullOrWhiteSpace(Title) ? LocalizationService.L("(无标题)", "(untitled)") : Title)} · {Geometry} · {State}";
}

public sealed class X11WindowDetails
{
    public ulong WindowId { get; init; }
    public string IdHex => $"0x{WindowId:x}";
    public string? Title { get; init; }
    public string? ClassName { get; init; }
    public string? InstanceName { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BorderWidth { get; init; }
    public int Depth { get; init; }
    public bool Mapped { get; init; }
    public bool OverrideRedirect { get; init; }
    public bool InputOnly { get; init; }
    public string? VisualClass { get; init; }
    public string? WindowType { get; init; }
    public string? WmState { get; init; }
    public string? TransientFor { get; init; }
    public string? Protocols { get; init; }
    public string? WmNormalHints { get; init; }
    public ulong RootWindow { get; init; }
    public ulong ParentWindow { get; init; }
    public int ChildCount { get; init; }
    public string EventMask { get; init; } = "";
    public string? RawTree { get; init; }
    public List<X11Property> Properties { get; init; } = [];
    public List<X11ChildInfo> Children { get; init; } = [];
}

public sealed class ProcessSnapshot
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string? Name { get; init; }
    public string? Exe { get; init; }
    public string? Cmdline { get; init; }
    public string? Cwd { get; init; }
    public string? User { get; init; }
    public string? State { get; init; }
    public long RssKb { get; init; }
    public int Threads { get; init; }
    public string? StartedAt { get; init; }
    public string? Sandbox { get; init; }
    public ToolkitInfo Toolkit { get; init; } = new();
    public BackendEvidence Backend { get; init; } = new();
    public IReadOnlyList<SocketConnection> Sockets { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> KeyLibraries { get; init; } = [];
}

public sealed class ToolkitInfo
{
    public string Name { get; init; } = LocalizationService.L("未知", "Unknown");
    public string? Version { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
}

public sealed class BackendEvidence
{
    public bool HasX11Socket { get; init; }
    public bool HasWaylandSocket { get; init; }
    public bool HasDbusSocket { get; init; }
    public bool HasPipeWireSocket { get; init; }
    public bool HasX11Library { get; init; }
    public bool HasWaylandLibrary { get; init; }
    public IReadOnlyList<string> SocketPaths { get; init; } = [];

    public string Display
    {
        get
        {
            if (HasX11Socket && HasWaylandSocket) return "Wayland + X11";
            if (HasX11Socket) return "X11";
            if (HasWaylandSocket) return "Wayland";
            if (HasX11Library && HasWaylandLibrary) return LocalizationService.L("Wayland + X11（动态库）", "Wayland + X11 (shared libraries)");
            if (HasX11Library) return LocalizationService.L("X11（动态库）", "X11 (shared libraries)");
            if (HasWaylandLibrary) return LocalizationService.L("Wayland（动态库）", "Wayland (shared libraries)");
            return LocalizationService.L("未知", "Unknown");
        }
    }
}

public sealed class SocketConnection
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required string Inode { get; init; }
}

public sealed class ProviderStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Available { get; init; }
    public string Detail { get; init; } = "";
    public PickCapability Pick { get; init; }
    public bool CanFocus { get; init; }
    public bool CanClose { get; init; }
    public bool CanList { get; init; }

    public string PickText => Pick switch
    {
        PickCapability.Interactive => LocalizationService.L("十字准星", "Crosshair"),
        PickCapability.Cursor => LocalizationService.L("指针停留", "Pointer hover"),
        PickCapability.Focused => LocalizationService.L("聚焦拾取", "Focused window"),
        _ => LocalizationService.L("不支持", "Unsupported"),
    };

    public string AvailabilityText => Available
        ? LocalizationService.L("可用", "Available")
        : LocalizationService.L("不可用", "Unavailable");
    public string CapabilityText => LocalizationService.F(
        "列表 {0} · 拾取 {1} · 聚焦 {2} · 关闭 {3}",
        "List {0} · Pick {1} · Focus {2} · Close {3}",
        CanList ? "✓" : "✗", PickText, CanFocus ? "✓" : "✗", CanClose ? "✓" : "✗");
    public string Summary => Available ? $"{Name} — {Detail}" : $"{Name} — {Detail}";
}

public sealed class SessionInfo
{
    public string SessionType { get; set; } = LocalizationService.L("未知", "Unknown");
    public string Desktop { get; set; } = LocalizationService.L("未知", "Unknown");
    public string? WaylandDisplay { get; set; }
    public string? X11Display { get; set; }
    public string? Compositor { get; set; }
    public string? CompositorVersion { get; set; }
    public string? X11WmName { get; set; }
    public bool IsWaylandSession =>
        string.Equals(SessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
        (!string.Equals(SessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrWhiteSpace(WaylandDisplay));
    public bool IsX11Session => !IsWaylandSession && !string.IsNullOrWhiteSpace(X11Display);

    public string SessionSummary => IsWaylandSession
        ? $"Wayland · {Compositor ?? Desktop}"
        : IsX11Session ? $"X11 · {X11WmName ?? Desktop}" : LocalizationService.L("未知会话", "Unknown session");
}

public sealed class ScreenshotResult
{
    public byte[]? Png { get; init; }
    public string Message { get; init; } = "";
    public string Source { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Success => Png is { Length: > 0 };
}

public sealed class AtspiNode : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string Description { get; init; } = "";
    public List<AtspiNode> Children { get; init; } = [];

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public string Title => string.IsNullOrWhiteSpace(Name) ? $"({Role})" : Name;
    public string RoleShort => Role.Length > 22 ? Role[..22] + "…" : Role;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasChildren => Children.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class X11RecordedEvent
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public int Type { get; init; }
    public string TypeName { get; init; } = "";
    public string Category { get; init; } = "Other";
    public ulong Window { get; init; }
    public ulong RootWindow { get; init; }
    public int RootX { get; init; }
    public int RootY { get; init; }
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string WindowHex => $"0x{Window:x}";
    public string DisplayText => $"{TimeText}  {TypeName,-18} {WindowHex,-12} {Summary}";
}

public sealed class OutputInfo
{
    public string Name { get; init; } = "";
    public string? Make { get; init; }
    public string? Model { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Primary { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Scale { get; init; }
    public double? RefreshRate { get; init; }
    public string? Transform { get; init; }
    public string? Mode { get; init; }
    public string? Workspace { get; init; }

    public string Title => Primary ? $"{Name} · primary" : Name;
    public string GeometryText => Width is > 0 && Height is > 0
        ? $"{Width}×{Height}" + (X is not null && Y is not null ? $" @ {X},{Y}" : "")
        : "—";
    public string ScaleText => Scale is > 0 ? $"{Scale:0.##}×" : "—";
    public string RefreshText => RefreshRate is > 0 ? $"{RefreshRate:0.##} Hz" : "—";
    public string ScaleLabel => LocalizationService.F("缩放 {0}", "Scale {0}", ScaleText);
    public string RefreshLabel => LocalizationService.F("刷新率 {0}", "Refresh {0}", RefreshText);
    public string TransformLabel => LocalizationService.F("变换 {0}", "Transform {0}", Transform ?? "—");
    public string ModeLabel => LocalizationService.F("模式 {0}", "Mode {0}", Mode ?? "—");
    public string WorkspaceLabel => LocalizationService.F("工作区 {0}", "Workspace {0}", Workspace ?? "—");
    public string Detail => string.Join(" · ", new[] { Make, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class WorkspaceInfo
{
    public required string Name { get; init; }
    public string? Output { get; init; }
    public bool Active { get; init; }
    public bool Focused { get; init; }
    public int WindowCount { get; init; }
    public string? Layout { get; init; }
    public string DisplayText => $"{Name}{(Active ? " · active" : "")}{(Focused ? " · focused" : "")}";
    public string Subtitle => string.Join(" · ", new[]
    {
        Output,
        WindowCount > 0 ? LocalizationService.F("{0} 个窗口", "{0} windows", WindowCount) : null,
        Layout,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class OutputResult
{
    public IReadOnlyList<OutputInfo> Outputs { get; init; } = [];
    public IReadOnlyList<WorkspaceInfo> Workspaces { get; init; } = [];
    public IReadOnlyList<string> LayoutLines { get; init; } = [];
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
}

public sealed class WindowSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public string WindowId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? AppId { get; set; }
    public int? Pid { get; set; }
    public string Backend { get; set; } = "";
    public string Geometry { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RawJson { get; set; } = "";
    public string? ScreenshotBase64 { get; set; }
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public string DisplayText => $"{Name} · {CapturedAt:MM-dd HH:mm} · {Title}";
    public string Subtitle => $"{Backend} · {Geometry} · PID {Pid?.ToString() ?? "—"}";
}

public sealed class SnapshotDiff
{
    public required string Key { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string State { get; init; } = "changed";
    public bool IsChanged => State == "changed";
    public string DisplayText => State switch
    {
        "added" => $"+ {Key}: {NewValue}",
        "removed" => $"- {Key}: {OldValue}",
        _ => $"~ {Key}: {OldValue} → {NewValue}",
    };
}

public sealed class PickProgress
{    public required string Message { get; init; }
    public WindowInfo? HoverWindow { get; init; }
    public int? Countdown { get; init; }
}

public sealed class PickResult
{
    public WindowInfo? Window { get; init; }
    public string? Message { get; init; }
    public bool Cancelled { get; init; }
}

public sealed class InspectionResult
{
    public required WindowInfo Window { get; init; }
    public ProcessSnapshot? Process { get; init; }
    public X11WindowDetails? X11 { get; init; }
    public string RawJson { get; init; } = "";
    public string Summary { get; init; } = "";
}
