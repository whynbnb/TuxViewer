using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuxViewer.Models;

namespace TuxViewer.Services;

public static partial class OutputService
{
    public static async Task<OutputResult> GetAsync(ProviderRegistry registry, CancellationToken cancellationToken)
    {
        if (await TryNiriAsync(cancellationToken) is { Outputs.Count: > 0 } niri)
            return AddX11Note(niri, registry);

        if (await TrySwayAsync(cancellationToken) is { Outputs.Count: > 0 } sway)
            return AddX11Note(sway, registry);

        if (await TryHyprlandAsync(cancellationToken) is { Outputs.Count: > 0 } hypr)
            return AddX11Note(hypr, registry);

        return AddX11Note(await TryXrandrAsync(cancellationToken), registry);
    }

    private static OutputResult AddX11Note(OutputResult result, ProviderRegistry registry)
    {
        var root = registry.X11?.Inspector.GetRootGeometry();
        if (root is null) return result;
        var logical = result.Outputs.FirstOrDefault(o => o.Width is > 0 && o.Height is > 0);
        var text = $"X11/XWayland 根窗口：{root.Value.Width}×{root.Value.Height}";
        if (logical is not null)
            text += $" · 合成器逻辑输出：{logical.Width}×{logical.Height} @ {logical.ScaleText}";
        if (logical?.Scale is > 0 && root.Value.Width != logical.Width)
            text += " · X11 坐标可能经过缩放，拾取 / 截图时以 X11 根窗口为准";
        return new OutputResult
        {
            Outputs = result.Outputs,
            Workspaces = result.Workspaces,
            LayoutLines = result.LayoutLines,
            Source = result.Source,
            Note = string.IsNullOrWhiteSpace(result.Note) ? text : $"{result.Note}\n{text}",
        };
    }

    private static async Task<OutputResult> TryNiriAsync(CancellationToken cancellationToken)
    {
        if (!CommandRunner.Exists("niri") || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIRI_SOCKET")))
            return new OutputResult();

        var outputsTask = CommandRunner.TryRunAsync("niri", ["msg", "--json", "outputs"], 4000, cancellationToken);
        var workspacesTask = CommandRunner.TryRunAsync("niri", ["msg", "--json", "workspaces"], 4000, cancellationToken);
        var windowsTask = CommandRunner.TryRunAsync("niri", ["msg", "--json", "windows"], 4000, cancellationToken);
        await Task.WhenAll(outputsTask, workspacesTask, windowsTask);

        var outputs = new List<OutputInfo>();
        try
        {
            var json = outputsTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        var o = property.Value;
                        var logical = o.TryGetProperty("logical", out var logicalElement) && logicalElement.ValueKind == JsonValueKind.Object
                            ? logicalElement
                            : default;
                        var modeIndex = o.TryGetProperty("current_mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.Number
                            ? modeEl.GetInt32()
                            : -1;
                        double? refresh = null;
                        string? modeText = null;
                        if (o.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array &&
                            modeIndex >= 0 && modeIndex < modes.GetArrayLength())
                        {
                            var mode = modes[modeIndex];
                            if (mode.TryGetProperty("refresh_rate", out var refreshEl) && refreshEl.ValueKind == JsonValueKind.Number)
                                refresh = refreshEl.GetInt32() / 1000.0;
                            if (mode.TryGetProperty("width", out var mw) && mode.TryGetProperty("height", out var mh))
                                modeText = $"{mw.GetInt32()}×{mh.GetInt32()}";
                        }

                        outputs.Add(new OutputInfo
                        {
                            Name = GetString(o, "name") ?? property.Name,
                            Make = GetString(o, "make"),
                            Model = GetString(o, "model"),
                            X = GetInt(logical, "x"),
                            Y = GetInt(logical, "y"),
                            Width = GetInt(logical, "width"),
                            Height = GetInt(logical, "height"),
                            Scale = GetDouble(logical, "scale"),
                            Transform = GetString(logical, "transform"),
                            RefreshRate = refresh,
                            Mode = modeText,
                        });
                    }
                }
            }
        }
        catch { /* ignore */ }

        var workspaces = new List<WorkspaceInfo>();
        var workspaceMap = new Dictionary<int, (string Name, string? Output, bool Active, bool Focused)>();
        try
        {
            var json = workspacesTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ws in doc.RootElement.EnumerateArray())
                    {
                        var id = ws.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                        var idx = ws.TryGetProperty("idx", out var idxEl) ? idxEl.GetInt32() : 0;
                        var name = GetString(ws, "name");
                        var display = string.IsNullOrWhiteSpace(name) ? $"ws {idx}" : name!;
                        var output = GetString(ws, "output");
                        var active = ws.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True;
                        var focused = ws.TryGetProperty("is_focused", out var f) && f.ValueKind == JsonValueKind.True;
                        if (id >= 0) workspaceMap[id] = (display, output, active, focused);
                        workspaces.Add(new WorkspaceInfo
                        {
                            Name = display,
                            Output = output,
                            Active = active,
                            Focused = focused,
                        });
                    }
                }
            }
        }
        catch { /* ignore */ }

        var layoutLines = new List<string>();
        try
        {
            var json = windowsTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var grouped = new Dictionary<int, List<(string Title, string? App, int? Col, int? Row, string? Size, bool Floating)>>();
                    foreach (var window in doc.RootElement.EnumerateArray())
                    {
                        var workspaceId = window.TryGetProperty("workspace_id", out var w) && w.ValueKind == JsonValueKind.Number
                            ? w.GetInt32()
                            : -1;
                        if (workspaceId < 0) continue;
                        int? col = null, row = null;
                        string? size = null;
                        if (window.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
                        {
                            if (layout.TryGetProperty("pos_in_scrolling_layout", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 2)
                            {
                                col = (int)Math.Round(pos[0].GetDouble());
                                row = (int)Math.Round(pos[1].GetDouble());
                            }
                            if (layout.TryGetProperty("window_size", out var ws) && ws.ValueKind == JsonValueKind.Array && ws.GetArrayLength() >= 2)
                                size = $"{ws[0]:0}×{ws[1]:0}";
                        }
                        var floating = window.TryGetProperty("is_floating", out var fl) && fl.ValueKind == JsonValueKind.True;
                        var title = GetString(window, "title") ?? "(无标题)";
                        var app = GetString(window, "app_id");
                        if (!grouped.TryGetValue(workspaceId, out var list))
                            grouped[workspaceId] = list = [];
                        list.Add((title, app, col, row, size, floating));
                    }

                    foreach (var (id, items) in grouped.OrderBy(kv => workspaceMap.TryGetValue(kv.Key, out var ws) ? ws.Name : kv.Key.ToString(), StringComparer.OrdinalIgnoreCase))
                    {
                        var ws = workspaceMap.TryGetValue(id, out var info) ? info : (Name: $"ws {id}", Output: null, Active: false, Focused: false);
                        layoutLines.Add($"{ws.Name}{(ws.Active ? " · active" : "")}{(ws.Focused ? " · focused" : "")} ({ws.Output ?? "?"})");
                        foreach (var group in items.Where(i => !i.Floating).GroupBy(i => i.Col ?? -1).OrderBy(g => g.Key))
                        {
                            var parts = group.Select(i => $"{i.Title}{(i.App is not null ? $" [{i.App}]" : "")}{(i.Size is not null ? $" {i.Size}" : "")}");
                            layoutLines.Add($"  col {group.Key}: {string.Join(", ", parts)}");
                        }
                        var floating = items.Where(i => i.Floating).ToList();
                        if (floating.Count > 0)
                            layoutLines.Add($"  floating: {string.Join(", ", floating.Select(i => i.Title))}");
                    }
                }
            }
        }
        catch { /* ignore */ }

        foreach (var workspace in workspaces)
        {
            if (layoutLines.Any(line => line.StartsWith(workspace.Name, StringComparison.OrdinalIgnoreCase))) continue;
            layoutLines.Add($"{workspace.DisplayText} ({workspace.Output ?? "?"})");
        }

        return new OutputResult
        {
            Outputs = outputs.OrderBy(o => o.X ?? 0).ThenBy(o => o.Y ?? 0).ToList(),
            Workspaces = workspaces,
            LayoutLines = layoutLines,
            Source = "niri IPC",
            Note = outputs.Count == 0 ? "niri 没有返回输出信息" : "",
        };
    }

    private static async Task<OutputResult> TrySwayAsync(CancellationToken cancellationToken)
    {
        if (!CommandRunner.Exists("swaymsg") || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWAYSOCK")))
            return new OutputResult();

        var outputsTask = CommandRunner.TryRunAsync("swaymsg", ["-t", "get_outputs", "-r"], 4000, cancellationToken);
        var workspacesTask = CommandRunner.TryRunAsync("swaymsg", ["-t", "get_workspaces", "-r"], 4000, cancellationToken);
        await Task.WhenAll(outputsTask, workspacesTask);

        var outputs = new List<OutputInfo>();
        try
        {
            var json = outputsTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var output in doc.RootElement.EnumerateArray())
                    {
                        var rect = output.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
                        var modeIndex = output.TryGetProperty("current_mode", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : -1;
                        double? refresh = null;
                        string? modeText = null;
                        if (output.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array &&
                            modeIndex >= 0 && modeIndex < modes.GetArrayLength())
                        {
                            var mode = modes[modeIndex];
                            if (mode.TryGetProperty("refresh", out var refreshEl) && refreshEl.ValueKind == JsonValueKind.Number)
                                refresh = refreshEl.GetInt32() / 1000.0;
                            if (mode.TryGetProperty("width", out var mw) && mode.TryGetProperty("height", out var mh))
                                modeText = $"{mw.GetInt32()}×{mh.GetInt32()}";
                        }
                        outputs.Add(new OutputInfo
                        {
                            Name = GetString(output, "name") ?? "?",
                            Make = GetString(output, "make"),
                            Model = GetString(output, "model"),
                            Enabled = !(output.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False),
                            Primary = output.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True,
                            X = GetInt(rect, "x"),
                            Y = GetInt(rect, "y"),
                            Width = GetInt(rect, "width"),
                            Height = GetInt(rect, "height"),
                            Scale = GetDouble(output, "scale"),
                            Transform = GetString(output, "transform"),
                            RefreshRate = refresh,
                            Mode = modeText,
                        });
                    }
                }
            }
        }
        catch { /* ignore */ }

        var workspaces = new List<WorkspaceInfo>();
        var lines = new List<string>();
        try
        {
            var json = workspacesTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ws in doc.RootElement.EnumerateArray())
                    {
                        var name = GetString(ws, "name") ?? GetInt(ws, "num")?.ToString() ?? "?";
                        var output = GetString(ws, "output");
                        var focused = ws.TryGetProperty("focused", out var f) && f.ValueKind == JsonValueKind.True;
                        var active = ws.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True;
                        workspaces.Add(new WorkspaceInfo { Name = name, Output = output, Active = active, Focused = focused });
                        lines.Add($"{name}{(focused ? " · focused" : "")} ({output ?? "?"})");
                    }
                }
            }
        }
        catch { /* ignore */ }

        return new OutputResult
        {
            Outputs = outputs.OrderBy(o => o.X ?? 0).ThenBy(o => o.Y ?? 0).ToList(),
            Workspaces = workspaces,
            LayoutLines = lines,
            Source = "Sway IPC",
        };
    }

    private static async Task<OutputResult> TryHyprlandAsync(CancellationToken cancellationToken)
    {
        if (!CommandRunner.Exists("hyprctl") || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE")))
            return new OutputResult();

        var monitorsTask = CommandRunner.TryRunAsync("hyprctl", ["-j", "monitors"], 4000, cancellationToken);
        var workspacesTask = CommandRunner.TryRunAsync("hyprctl", ["-j", "workspaces"], 4000, cancellationToken);
        await Task.WhenAll(monitorsTask, workspacesTask);

        var outputs = new List<OutputInfo>();
        try
        {
            var json = monitorsTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var monitor in doc.RootElement.EnumerateArray())
                    {
                        var activeWorkspace = monitor.TryGetProperty("activeWorkspace", out var aw) && aw.ValueKind == JsonValueKind.Object
                            ? GetString(aw, "name")
                            : null;
                        outputs.Add(new OutputInfo
                        {
                            Name = GetString(monitor, "name") ?? "?",
                            Model = GetString(monitor, "description"),
                            Primary = monitor.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True,
                            X = GetInt(monitor, "x"),
                            Y = GetInt(monitor, "y"),
                            Width = GetInt(monitor, "width"),
                            Height = GetInt(monitor, "height"),
                            Scale = GetDouble(monitor, "scale"),
                            Transform = GetInt(monitor, "transform")?.ToString(CultureInfo.InvariantCulture),
                            RefreshRate = GetDouble(monitor, "refreshRate"),
                            Workspace = activeWorkspace,
                        });
                    }
                }
            }
        }
        catch { /* ignore */ }

        var workspaces = new List<WorkspaceInfo>();
        var lines = new List<string>();
        try
        {
            var json = workspacesTask.Result?.StdOut;
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ws in doc.RootElement.EnumerateArray())
                    {
                        var name = GetString(ws, "name") ?? GetInt(ws, "id")?.ToString() ?? "?";
                        var output = GetString(ws, "monitor");
                        var windows = GetInt(ws, "windows") ?? 0;
                        var focused = ws.TryGetProperty("hasfullscreen", out _) && false; // filled from monitor focus below
                        workspaces.Add(new WorkspaceInfo { Name = name, Output = output, WindowCount = windows });
                        lines.Add($"{name} ({output ?? "?"}) · {windows} 个窗口");
                    }
                }
            }
        }
        catch { /* ignore */ }

        return new OutputResult
        {
            Outputs = outputs.OrderBy(o => o.X ?? 0).ThenBy(o => o.Y ?? 0).ToList(),
            Workspaces = workspaces,
            LayoutLines = lines,
            Source = "Hyprland IPC",
        };
    }

    private static async Task<OutputResult> TryXrandrAsync(CancellationToken cancellationToken)
    {
        if (!CommandRunner.Exists("xrandr"))
            return new OutputResult { Source = "X11", Note = "没有可用的输出后端（缺少 xrandr / niri / swaymsg / hyprctl）" };

        var listTask = CommandRunner.TryRunAsync("xrandr", ["--listmonitors"], 4000, cancellationToken);
        var queryTask = CommandRunner.TryRunAsync("xrandr", ["--query"], 4000, cancellationToken);
        await Task.WhenAll(listTask, queryTask);

        var outputs = new List<OutputInfo>();
        var refreshByOutput = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var query = queryTask.Result?.StdOut ?? "";
            string? current = null;
            foreach (var line in query.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !char.IsWhiteSpace(line[0]) && trimmed.Contains(" connected", StringComparison.OrdinalIgnoreCase))
                {
                    current = trimmed.Split(' ')[0];
                }
                else if (current is not null && line.StartsWith("   ", StringComparison.Ordinal) && trimmed.Contains('*'))
                {
                    var match = RefreshRegex().Match(trimmed);
                    if (match.Success && double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
                        refreshByOutput[current] = hz;
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            var text = listTask.Result?.StdOut ?? "";
            foreach (var line in text.Split('\n'))
            {
                var match = MonitorRegex().Match(line);
                if (!match.Success) continue;
                var name = match.Groups[2].Value;
                var width = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                var height = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                var x = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
                var y = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
                refreshByOutput.TryGetValue(name, out var refresh);
                outputs.Add(new OutputInfo
                {
                    Name = name,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height,
                    Primary = match.Groups[1].Value.Contains('*'),
                    RefreshRate = refresh > 0 ? refresh : null,
                });
            }
        }
        catch { /* ignore */ }

        return new OutputResult
        {
            Outputs = outputs,
            Source = "xrandr",
            Note = outputs.Count == 0 ? "xrandr 没有返回显示器信息" : "",
        };
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    [GeneratedRegex(@"^\s*\d+:\s+([+*]*)(\S+)\s+(\d+)/\d+x(\d+)/\d+\+(\d+)\+(\d+)")]
    private static partial Regex MonitorRegex();

    [GeneratedRegex(@"(\d+)x(\d+)\s+([\d.]+)\*")]
    private static partial Regex RefreshRegex();
}
