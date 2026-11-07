using System.Text.Json;
using TuxViewer.Models;

namespace TuxViewer.Services;

public sealed class AtspiResult
{
    public bool Success { get; init; }
    public bool Matched { get; init; }
    public int AppCount { get; init; }
    public int NodeCount { get; init; }
    public IReadOnlyList<AtspiNode> Roots { get; init; } = [];
    public string Message { get; init; } = "";
}

/// <summary>
/// Reads the AT-SPI2 accessibility tree through python3-gi (Atspi). The tree is optional:
/// applications only appear when their toolkit accessibility bridge is enabled.
/// </summary>
public static class AtspiService
{
    private const string Script = """
import gi, json, sys
gi.require_version('Atspi', '2.0')
from gi.repository import Atspi

title = (sys.argv[1] if len(sys.argv) > 1 else '').strip().lower()
app_id = (sys.argv[2] if len(sys.argv) > 2 else '').strip().lower()
max_depth = int(sys.argv[3]) if len(sys.argv) > 3 else 5
max_nodes = int(sys.argv[4]) if len(sys.argv) > 4 else 500

Atspi.init()
desktop = Atspi.get_desktop(0)
counter = [0]

def text(obj, method):
    try:
        value = getattr(obj, method)()
        return value or ''
    except Exception:
        return ''

def walk(acc, depth):
    counter[0] += 1
    node = {
        'name': text(acc, 'get_name'),
        'role': text(acc, 'get_role_name'),
        'description': text(acc, 'get_description'),
        'children': [],
    }
    if depth < max_depth and counter[0] < max_nodes:
        try:
            count = acc.get_child_count()
        except Exception:
            count = 0
        for i in range(count):
            if counter[0] >= max_nodes:
                break
            try:
                child = acc.get_child_at_index(i)
            except Exception:
                child = None
            if child is not None:
                node['children'].append(walk(child, depth + 1))
    return node

apps = []
try:
    for i in range(desktop.get_child_count()):
        acc = desktop.get_child_at_index(i)
        if acc is None:
            continue
        frames = []
        try:
            for j in range(acc.get_child_count()):
                frame = acc.get_child_at_index(j)
                if frame is not None:
                    frames.append(frame)
        except Exception:
            pass
        apps.append((acc, text(acc, 'get_name'), frames))
except Exception:
    pass

best = None
best_score = -1
for acc, name, frames in apps:
    score = 0
    normalized = name.strip().lower()
    if app_id and normalized == app_id:
        score = max(score, 60)
    elif app_id and app_id in normalized:
        score = max(score, 20)
    if title:
        for frame in frames:
            frame_name = text(frame, 'get_name').strip().lower()
            if frame_name == title:
                score = max(score, 100)
            elif title and (title in frame_name or frame_name in title):
                score = max(score, 50)
    if score > best_score:
        best_score = score
        best = (acc, name, frames)

matched = best is not None and best_score > 0
roots = []
if matched:
    roots.append(walk(best[0], 0))
else:
    for acc, name, frames in apps[:20]:
        roots.append(walk(acc, 0))

def count(nodes):
    total = 0
    for node in nodes:
        total += 1 + count(node['children'])
    return total

print(json.dumps({
    'matched': matched,
    'apps': len(apps),
    'nodes': count(roots),
    'roots': roots,
}, ensure_ascii=False))
""";

    public static async Task<AtspiResult> LoadAsync(WindowInfo window, CancellationToken cancellationToken)
    {
        if (!CommandRunner.Exists("python3"))
            return new AtspiResult { Message = LocalizationService.L("缺少 python3，无法读取 AT-SPI", "python3 is missing; cannot read AT-SPI") };

        var probe = await CommandRunner.TryRunAsync(
            "python3",
            ["-c", "import gi; gi.require_version('Atspi','2.0'); from gi.repository import Atspi; print('ok')"],
            4000, cancellationToken);
        if (probe?.Success != true || !probe.StdOut.Contains("ok", StringComparison.Ordinal))
            return new AtspiResult
            {
                Message = LocalizationService.L("缺少 python3-gi / Atspi 绑定（Arch: pacman -S python-gobject at-spi2-core）", "python3-gi / Atspi bindings are missing (Arch: pacman -S python-gobject at-spi2-core)"),
            };

        var args = new[]
        {
            "-c", Script,
            window.Title ?? "",
            window.AppId ?? window.WmClass ?? "",
            "5",
            "500",
        };
        var result = await CommandRunner.TryRunAsync("python3", args, 20_000, cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.StdOut))
            return new AtspiResult { Message = result?.StdErr ?? LocalizationService.L("AT-SPI 没有返回数据", "AT-SPI returned no data") };

        var json = result.StdOut.Trim();
        var start = json.IndexOf('{');
        if (start > 0) json = json[start..];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var matched = root.TryGetProperty("matched", out var m) && m.ValueKind == JsonValueKind.True;
            var apps = root.TryGetProperty("apps", out var a) && a.ValueKind == JsonValueKind.Number
                ? a.GetInt32()
                : 0;
            var nodes = root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt32()
                : 0;
            var roots = new List<AtspiNode>();
            if (root.TryGetProperty("roots", out var rootsElement) && rootsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in rootsElement.EnumerateArray())
                {
                    var node = ParseNode(element, 0);
                    if (node is not null) roots.Add(node);
                }
            }

            return new AtspiResult
            {
                Success = true,
                Matched = matched,
                AppCount = apps,
                NodeCount = nodes,
                Roots = roots,
                Message = matched
                    ? LocalizationService.F("已匹配选中窗口 · {0} 个控件 · {1} 个应用", "Matched the selected window · {0} controls · {1} apps", nodes, apps)
                    : apps == 0
                        ? LocalizationService.L("AT-SPI 桌面没有注册任何应用（请开启工具包无障碍桥）", "No apps are registered with the AT-SPI desktop (enable the toolkit accessibility bridge)")
                        : LocalizationService.F("未精确匹配窗口，已显示全部 {0} 个应用 · {1} 个控件", "No exact window match; showing all {0} apps · {1} controls", apps, nodes),
            };
        }
        catch (Exception ex)
        {
            return new AtspiResult { Message = LocalizationService.F("AT-SPI 输出解析失败：{0}", "Failed to parse AT-SPI output: {0}", ex.Message) };
        }
    }

    private static AtspiNode? ParseNode(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 8) return null;
        var children = new List<AtspiNode>();
        if (element.TryGetProperty("children", out var childElement) &&
            childElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childElement.EnumerateArray())
            {
                var node = ParseNode(child, depth + 1);
                if (node is not null) children.Add(node);
            }
        }

        return new AtspiNode
        {
            Name = GetString(element, "name"),
            Role = GetString(element, "role"),
            Description = GetString(element, "description"),
            Children = children,
            IsExpanded = depth < 1,
        };
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
