using System.Text;
using TuxViewer.Models;

namespace TuxViewer.Services;

public static class ReportService
{
    public static string BuildHtml(
        WindowInfo window, ProcessSnapshot? process, X11WindowDetails? x11,
        string? screenshotDataUri, string rawJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{LocalizationService.L("TuxViewer · 窗口检视报告", "TuxViewer · Window inspection report")}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:system-ui,'Noto Sans CJK SC',sans-serif;background:#0b0e14;color:#dce5f4;margin:32px;line-height:1.55}");
        sb.AppendLine("h1{font-size:24px;margin:0 0 4px}h2{font-size:15px;margin:26px 0 8px;color:#8fa6c9;border-bottom:1px solid #232c3d;padding-bottom:6px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}td{border-bottom:1px solid #1c2433;padding:5px 8px;vertical-align:top}");
        sb.AppendLine("td:first-child{color:#8795ab;width:190px}code,pre{font-family:'DejaVu Sans Mono',monospace;font-size:12px}");
        sb.AppendLine("pre{background:#111722;border:1px solid #232c3d;border-radius:8px;padding:12px;overflow:auto}");
        sb.AppendLine(".shot{max-width:100%;border:1px solid #2a3850;border-radius:8px;background:#000}");
        sb.AppendLine("details{margin-top:18px}summary{cursor:pointer;color:#9bc0ff}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>{Escape(window.Title)}</h1>");
        sb.AppendLine($"<div style=\"color:#7e8ca3\">{Escape(window.BackendLabel)} · {Escape(window.GeometryText)} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}</div>");

        if (!string.IsNullOrWhiteSpace(screenshotDataUri))
            sb.AppendLine($"<h2>{LocalizationService.L("窗口截图", "Window screenshot")}</h2><img class=\"shot\" src=\"{screenshotDataUri}\" alt=\"window screenshot\">");

        sb.AppendLine($"<h2>{LocalizationService.L("窗口", "Window")}</h2><table>");
        Row(sb, LocalizationService.L("应用 ID", "App ID"), window.AppId);
        Row(sb, "WM_CLASS", window.WmClass);
        Row(sb, LocalizationService.L("后端", "Backend"), window.BackendLabel);
        Row(sb, "TuxViewer ID", window.Id);
        Row(sb, LocalizationService.L("原生 ID", "Native ID"), window.NativeId);
        Row(sb, LocalizationService.L("X11 窗口", "X11 window"), window.X11WindowId is not null ? $"0x{window.X11WindowId:x}" : null);
        Row(sb, LocalizationService.L("关联 X11 窗口", "Linked X11 window"), window.LinkedX11WindowId is not null ? $"0x{window.LinkedX11WindowId:x}" : null);
        Row(sb, "PID", window.Pid?.ToString());
        Row(sb, LocalizationService.L("几何", "Geometry"), window.GeometryText);
        Row(sb, LocalizationService.L("工作区 / 输出", "Workspace / output"), $"{window.WorkspaceName ?? window.WorkspaceId?.ToString() ?? "—"} / {window.OutputName ?? "—"}");
        Row(sb, LocalizationService.L("焦点 / 映射", "Focused / mapping"), $"{(window.IsFocused ? LocalizationService.L("是", "Yes") : LocalizationService.L("否", "No"))} / {(window.IsMapped ? LocalizationService.L("已映射", "Mapped") : LocalizationService.L("未映射", "Unmapped"))}");
        foreach (var (key, value) in window.Extra) Row(sb, key, value);
        sb.AppendLine("</table>");

        if (process is not null)
        {
            sb.AppendLine($"<h2>{LocalizationService.L("进程", "Process")}</h2><table>");
            Row(sb, LocalizationService.L("进程名", "Process name"), process.Name);
            Row(sb, "PID / PPID", $"{process.Pid} / {process.ParentPid}");
            Row(sb, LocalizationService.L("用户 / 状态", "User / state"), $"{process.User} / {process.State}");
            Row(sb, LocalizationService.L("可执行文件", "Executable"), process.Exe);
            Row(sb, LocalizationService.L("启动命令", "Command line"), process.Cmdline);
            Row(sb, LocalizationService.L("工作目录", "Working directory"), process.Cwd);
            Row(sb, LocalizationService.L("工具包", "Toolkit"), process.Toolkit.DisplayName);
            Row(sb, LocalizationService.L("客户端连接", "Client connections"), process.Backend.Display);
            Row(sb, LocalizationService.L("沙箱", "Sandbox"), process.Sandbox);
            sb.AppendLine("</table>");
        }

        if (x11 is not null)
        {
            sb.AppendLine($"<h2>{LocalizationService.L("X11 信息", "X11 information")}</h2><table>");
            Row(sb, LocalizationService.L("窗口 ID", "Window ID"), x11.IdHex);
            Row(sb, LocalizationService.L("实例 / 类", "Instance / class"), $"{x11.InstanceName} / {x11.ClassName}");
            Row(sb, "几何", $"{x11.Width}×{x11.Height} @ {x11.X},{x11.Y}");
            Row(sb, LocalizationService.L("窗口类型", "Window type"), x11.WindowType);
            Row(sb, "WM State", x11.WmState);
            Row(sb, LocalizationService.L("事件掩码", "Event mask"), x11.EventMask);
            Row(sb, LocalizationService.L("子窗口", "Child windows"), LocalizationService.F("{0} 个", "{0}", x11.Children.Count));
            sb.AppendLine("</table>");

            sb.AppendLine($"<h2>{LocalizationService.L("X11 属性", "X11 properties")}</h2><table>");
            foreach (var property in x11.Properties)
                Row(sb, $"{property.Name} [{property.Type}/{property.Format}]", property.Value);
            sb.AppendLine("</table>");

            if (x11.Children.Count > 0)
            {
                sb.AppendLine($"<h2>{LocalizationService.L("X11 子窗口", "X11 child windows")}</h2><table>");
                foreach (var child in x11.Children)
                    Row(sb, child.IdHex, $"{child.ClassName ?? "?"} · {child.DisplayText}");
                sb.AppendLine("</table>");
            }
        }

        if (!string.IsNullOrWhiteSpace(rawJson))
            sb.AppendLine($"<details><summary>{LocalizationService.L("完整检视 JSON", "Full inspection JSON")}</summary><pre>{Escape(rawJson)}</pre></details>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public static string BuildMarkdown(
        WindowInfo window, ProcessSnapshot? process, X11WindowDetails? x11,
        string? screenshotFileName, string rawJson)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {window.Title}");
        sb.AppendLine();
        sb.AppendLine($"`{window.BackendLabel}` · `{window.GeometryText}` · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(screenshotFileName))
        {
            sb.AppendLine($"## {LocalizationService.L("窗口截图", "Window screenshot")}");
            sb.AppendLine();
            sb.AppendLine($"![screenshot]({screenshotFileName})");
            sb.AppendLine();
        }

        sb.AppendLine($"## {LocalizationService.L("窗口", "Window")}");
        sb.AppendLine();
        sb.AppendLine(LocalizationService.L("| 项目 | 值 |", "| Item | Value |"));
        sb.AppendLine("|---|---|");
        RowMd(sb, LocalizationService.L("应用 ID", "App ID"), window.AppId);
        RowMd(sb, "WM_CLASS", window.WmClass);
        RowMd(sb, LocalizationService.L("后端", "Backend"), window.BackendLabel);
        RowMd(sb, "TuxViewer ID", window.Id);
        RowMd(sb, LocalizationService.L("原生 ID", "Native ID"), window.NativeId);
        RowMd(sb, LocalizationService.L("X11 窗口", "X11 window"), window.X11WindowId is not null ? $"0x{window.X11WindowId:x}" : null);
        RowMd(sb, "PID", window.Pid?.ToString());
        RowMd(sb, LocalizationService.L("几何", "Geometry"), window.GeometryText);
        RowMd(sb, LocalizationService.L("工作区", "Workspace"), window.WorkspaceName ?? window.WorkspaceId?.ToString());
        RowMd(sb, LocalizationService.L("输出", "Output"), window.OutputName);
        foreach (var (key, value) in window.Extra) RowMd(sb, key, value);
        sb.AppendLine();

        if (process is not null)
        {
            sb.AppendLine($"## {LocalizationService.L("进程", "Process")}");
            sb.AppendLine();
            sb.AppendLine(LocalizationService.L("| 项目 | 值 |", "| Item | Value |"));
            sb.AppendLine("|---|---|");
            RowMd(sb, LocalizationService.L("进程名", "Process name"), process.Name);
            RowMd(sb, "PID / PPID", $"{process.Pid} / {process.ParentPid}");
            RowMd(sb, LocalizationService.L("可执行文件", "Executable"), process.Exe);
            RowMd(sb, LocalizationService.L("启动命令", "Command line"), process.Cmdline);
            RowMd(sb, LocalizationService.L("工具包", "Toolkit"), process.Toolkit.DisplayName);
            RowMd(sb, LocalizationService.L("客户端连接", "Client connections"), process.Backend.Display);
            sb.AppendLine();
        }

        if (x11 is not null)
        {
            sb.AppendLine($"## {LocalizationService.L("X11 属性", "X11 properties")}");
            sb.AppendLine();
            sb.AppendLine(LocalizationService.L("| 属性 | 值 |", "| Property | Value |"));
            sb.AppendLine("|---|---|");
            foreach (var property in x11.Properties)
                RowMd(sb, property.Name, property.Value);
            sb.AppendLine();
        }

        sb.AppendLine($"<details><summary>{LocalizationService.L("完整检视 JSON", "Full inspection JSON")}</summary>");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(rawJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string? value)
        => sb.AppendLine($"<tr><td>{Escape(label)}</td><td>{Escape(string.IsNullOrWhiteSpace(value) ? "—" : value)}</td></tr>");

    private static void RowMd(StringBuilder sb, string label, string? value)
        => sb.AppendLine($"| {EscapeMd(label)} | {EscapeMd(string.IsNullOrWhiteSpace(value) ? "—" : value)} |");

    private static string Escape(string? value)
        => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeMd(string? value)
        => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
