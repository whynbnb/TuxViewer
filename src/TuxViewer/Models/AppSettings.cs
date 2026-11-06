namespace TuxViewer.Models;

/// <summary>
/// User preferences persisted to ~/.config/TuxViewer/settings.json.
/// Deliberately excludes session state such as the selected window or tab.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // Window geometry (app frame, not a restored session).
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // Window list filters.
    public int BackendFilterIndex { get; set; }
    public bool HideOwnWindows { get; set; } = true;
    public bool IncludeRawX11 { get; set; }
    public bool IncludeUnmapped { get; set; } = true;

    // Refresh / monitoring preferences.
    public bool AutoRefresh { get; set; } = true;
    public bool RealtimeEnabled { get; set; } = true;

    // Tree view.
    public bool TreeMode { get; set; }
    public int TreeGroupModeIndex { get; set; }

    // Window rule generator options.
    public int RuleCompositorIndex { get; set; }
    public bool RuleFloating { get; set; }
    public bool RuleNoBorder { get; set; }
    public bool RuleFullscreen { get; set; }
    public bool RuleSticky { get; set; }
    public bool RuleMatchTitle { get; set; }
    public string RuleWidth { get; set; } = "";
    public string RuleHeight { get; set; } = "";
    public string RuleWorkspace { get; set; } = "";
    public string RuleOpacity { get; set; } = "";

    // XRecord event filters.
    public bool X11RecordKeys { get; set; } = true;
    public bool X11RecordMouse { get; set; } = true;
    public bool X11RecordConfigure { get; set; } = true;
    public bool X11RecordProperty { get; set; } = true;
    public bool X11RecordFocus { get; set; } = true;
    public bool X11RecordMap { get; set; } = true;
    public bool X11RecordClientMessage { get; set; } = true;
    public bool X11RecordOther { get; set; }
    public bool X11RecordOnlySelected { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
