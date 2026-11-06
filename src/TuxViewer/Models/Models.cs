using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace TuxViewer.Models;

public enum WindowBackend
{
    Unknown,
    X11,
    Wayland,
}

public enum PickCapability
{
    None,
    Focused,
    Cursor,
    Interactive,
}

/// <summary>A single inspected toplevel window, normalised across all providers.</summary>
public sealed class WindowInfo
{
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

    [JsonIgnore] public bool IsWayland => Backend == WindowBackend.Wayland;
    [JsonIgnore] public bool IsX11 => Backend == WindowBackend.X11;

    [JsonIgnore]
    public string BackendLabel => Backend switch
    {
        WindowBackend.X11 when IsXWayland => "X11 · XWayland",
        WindowBackend.X11 => "X11",
        WindowBackend.Wayland => "Wayland",
        _ => "未知",
    };

    [JsonIgnore]
    public string BackendShort => Backend switch
    {
        WindowBackend.X11 when IsXWayland => "XW",
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
}

public sealed class InfoRow(string label, string? value, bool mono = false, bool wrap = false)
{
    public string Label { get; } = label;
    public string Value { get; } = string.IsNullOrWhiteSpace(value) ? "—" : value!;
    public bool IsMono { get; } = mono;
    public bool Wrap { get; } = wrap;
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
    public string NameAndType => $"{Name}  [{Type}/{Format}]";
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
    public string DisplayText => $"{Indent}0x{Id:x} · {ClassName ?? "?"} · {(string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title)} · {Geometry} · {State}";
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
    public string Name { get; init; } = "未知";
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
            if (HasX11Library && HasWaylandLibrary) return "Wayland + X11（动态库）";
            if (HasX11Library) return "X11（动态库）";
            if (HasWaylandLibrary) return "Wayland（动态库）";
            return "未知";
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
        PickCapability.Interactive => "十字准星",
        PickCapability.Cursor => "指针停留",
        PickCapability.Focused => "聚焦拾取",
        _ => "不支持",
    };

    public string AvailabilityText => Available ? "可用" : "不可用";
    public string CapabilityText => $"列表 {(CanList ? "✓" : "✗")} · 拾取 {PickText} · 聚焦 {(CanFocus ? "✓" : "✗")} · 关闭 {(CanClose ? "✓" : "✗")}";
    public string Summary => Available ? $"{Name} — {Detail}" : $"{Name} — {Detail}";
}

public sealed class SessionInfo
{
    public string SessionType { get; set; } = "未知";
    public string Desktop { get; set; } = "未知";
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
        : IsX11Session ? $"X11 · {X11WmName ?? Desktop}" : "未知会话";
}

public sealed class PickProgress
{
    public required string Message { get; init; }
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
