using System.Text;
using System.Text.RegularExpressions;
using TuxViewer.Models;

namespace TuxViewer.Services;

public sealed class WindowRuleOptions
{
    public bool Floating { get; set; }
    public bool NoBorder { get; set; }
    public bool Fullscreen { get; set; }
    public bool Sticky { get; set; }
    public bool MatchTitle { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Workspace { get; set; }
    public double? Opacity { get; set; }
}

/// <summary>Generates compositor window rules for the selected window.</summary>
public static class RuleGenerator
{
    public static string Generate(string compositor, WindowInfo window, WindowRuleOptions options)
    {
        var appId = !string.IsNullOrWhiteSpace(window.AppId) ? window.AppId! : window.WmClass ?? window.Title;
        var title = window.Title;
        return compositor.ToLowerInvariant() switch
        {
            "niri" => Niri(appId, title, options),
            "sway" => Sway(appId, title, options),
            "hyprland" => Hyprland(appId, title, options),
            _ => $"# 未知合成器：{compositor}\n",
        };
    }

    private static string Niri(string appId, string title, WindowRuleOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TuxViewer · niri window-rule");
        sb.AppendLine("# 添加到 ~/.config/niri/config.kdl");
        sb.AppendLine("window-rule {");
        sb.AppendLine($"    match app-id=\"^{Regex.Escape(appId)}$\"");
        if (options.MatchTitle && !string.IsNullOrWhiteSpace(title))
            sb.AppendLine($"    match title=\"^{Regex.Escape(title)}$\"");
        if (options.Floating) sb.AppendLine("    open-floating true");
        if (options.Fullscreen) sb.AppendLine("    open-fullscreen true");
        if (options.Width is > 0) sb.AppendLine($"    default-column-width {{ fixed {options.Width}; }}");
        if (options.Height is > 0) sb.AppendLine($"    default-window-height {{ fixed {options.Height}; }}");
        if (!string.IsNullOrWhiteSpace(options.Workspace))
            sb.AppendLine($"    open-on-workspace \"{EscapeKdl(options.Workspace)}\"");
        if (options.NoBorder) sb.AppendLine("    border { off; }");
        if (options.Opacity is > 0 and < 1)
            sb.AppendLine($"    opacity {options.Opacity:0.00}");
        if (options.Sticky)
            sb.AppendLine("    // niri 没有 sticky 规则；可使用窗口菜单或工作区固定");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Sway(string appId, string title, WindowRuleOptions options)
    {
        var criteria = new StringBuilder($"[app_id=\"{appId}\"");
        if (options.MatchTitle && !string.IsNullOrWhiteSpace(title))
            criteria.Append($" title=\"{title}\"");
        criteria.Append(']');

        var commands = new List<string>();
        if (options.Floating) commands.Add("floating enable");
        if (options.Fullscreen) commands.Add("fullscreen enable");
        if (options.NoBorder) commands.Add("border none");
        if (options.Sticky) commands.Add("sticky enable");
        if (options.Width is > 0 || options.Height is > 0)
            commands.Add($"resize set {options.Width ?? 0} {options.Height ?? 0}");
        if (!string.IsNullOrWhiteSpace(options.Workspace))
            commands.Add($"move to workspace \"{options.Workspace}\"");
        if (options.Opacity is > 0 and < 1)
            commands.Add($"opacity {options.Opacity:0.00}");
        if (commands.Count == 0) commands.Add("noop");

        var sb = new StringBuilder();
        sb.AppendLine("# TuxViewer · Sway for_window");
        sb.AppendLine("# 添加到 ~/.config/sway/config 并执行 swaymsg reload");
        sb.AppendLine($"for_window {criteria} {string.Join(", ", commands)}");
        return sb.ToString();
    }

    private static string Hyprland(string appId, string title, WindowRuleOptions options)
    {
        var criteria = new StringBuilder($"class:^({Regex.Escape(appId)})$");
        if (options.MatchTitle && !string.IsNullOrWhiteSpace(title))
            criteria.Append($",title:^({Regex.Escape(title)})$");

        var sb = new StringBuilder();
        sb.AppendLine("# TuxViewer · Hyprland windowrulev2");
        sb.AppendLine("# 添加到 ~/.config/hypr/hyprland.conf 并执行 hyprctl reload");
        if (options.Floating) sb.AppendLine($"windowrulev2 = float,{criteria}");
        if (options.Fullscreen) sb.AppendLine($"windowrulev2 = fullscreen,{criteria}");
        if (options.NoBorder) sb.AppendLine($"windowrulev2 = noborder,{criteria}");
        if (options.Sticky) sb.AppendLine($"windowrulev2 = pin,{criteria}");
        if (options.Width is > 0 || options.Height is > 0)
            sb.AppendLine($"windowrulev2 = size {options.Width ?? 0} {options.Height ?? 0},{criteria}");
        if (!string.IsNullOrWhiteSpace(options.Workspace))
            sb.AppendLine($"windowrulev2 = workspace {options.Workspace} silent,{criteria}");
        if (options.Opacity is > 0 and < 1)
            sb.AppendLine($"windowrulev2 = opacity {options.Opacity:0.00} {options.Opacity:0.00},{criteria}");
        if (sb.ToString().Split('\n').Length <= 2)
            sb.AppendLine($"# 未选择任何规则选项；示例：windowrulev2 = float,{criteria}");
        return sb.ToString();
    }

    private static string EscapeKdl(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
