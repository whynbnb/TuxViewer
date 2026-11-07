namespace TuxViewer.Models;

/// <summary>GUI startup actions parsed from the command line or forwarded over IPC.</summary>
public sealed class StartupOptions
{
    public string? Select { get; init; }
    public int? TabIndex { get; init; }
    public bool Capture { get; init; }
    public bool Tree { get; init; }
    public int? GroupModeIndex { get; init; }
    public bool Record { get; init; }
    public bool RecordSelected { get; init; }
    public bool AtspiLoad { get; init; }
    public bool Hotkey { get; init; }
    public PickMode? PickMode { get; init; }
    public bool PickX11 { get; init; }
    public PickMode? PickX11Mode { get; init; }
    public bool NewInstance { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        return new StartupOptions
        {
            Select = GetArg(args, "--select"),
            TabIndex = ReadTabArgument(args),
            Capture = HasArg(args, "--capture"),
            Tree = HasArg(args, "--tree"),
            GroupModeIndex = GetIntArg(args, "--group"),
            Record = HasArg(args, "--record"),
            RecordSelected = HasArg(args, "--record-selected"),
            AtspiLoad = HasArg(args, "--atspi-load"),
            Hotkey = HasArg(args, "--hotkey"),
            PickMode = ParsePickMode(GetArg(args, "--gui-pick")),
            PickX11 = HasArg(args, "--gui-pick-x11"),
            PickX11Mode = ParsePickMode(GetArg(args, "--gui-pick-x11")),
            NewInstance = HasArg(args, "--new-instance"),
        };
    }

    public static PickMode? ParsePickMode(string? value) => value?.ToLowerInvariant() switch
    {
        "drag" => TuxViewer.Models.PickMode.Drag,
        "click" => TuxViewer.Models.PickMode.Click,
        _ => null,
    };

    private static bool HasArg(IReadOnlyList<string> args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetArg(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static int? GetIntArg(IReadOnlyList<string> args, string name)
        => int.TryParse(GetArg(args, name), out var value) ? value : null;

    private static int? ReadTabArgument(IReadOnlyList<string> args)
    {
        var tab = GetArg(args, "--tab");
        return tab?.ToLowerInvariant() switch
        {
            "process" or "proc" => 1,
            "x11" or "properties" or "props" => 2,
            "wayland" or "wl" => 3,
            "env" or "environment" => 4,
            "raw" or "json" => 5,
            "events" or "event" => 6,
            "screenshot" or "shot" => 7,
            "rules" or "rule" => 8,
            "x11events" or "xrecord" or "record" => 9,
            "atspi" or "controls" or "widgets" => 10,
            "hotkey" or "shortcut" => 11,
            "control" or "actions" => 12,
            "report" or "snapshot" => 13,
            "outputs" or "output" or "monitors" => 14,
            "diagnostics" or "diag" => 15,
            null => null,
            _ => 0,
        };
    }
}
