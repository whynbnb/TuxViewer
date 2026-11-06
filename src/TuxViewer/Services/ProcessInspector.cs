using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TuxViewer.Models;

namespace TuxViewer.Services;

public sealed class ProcessInspector
{
    private readonly Dictionary<int, CacheEntry> _cache = new();
    private readonly Lock _lock = new();

    public ProcessSnapshot Inspect(int pid, int cacheSeconds = 2)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(pid, out var cached) && (DateTime.UtcNow - cached.ReadAt).TotalSeconds < cacheSeconds)
                return cached.Snapshot;
        }

        var snapshot = Build(pid);
        lock (_lock) _cache[pid] = new CacheEntry(snapshot, DateTime.UtcNow);
        return snapshot;
    }

    private static ProcessSnapshot Build(int pid)
    {
        var env = ReadEnvironment(pid);
        var maps = ReadMaps(pid);
        var cmdline = ReadCmdline(pid);
        var sockets = ReadSockets(pid);
        var backend = new BackendEvidence
        {
            HasX11Socket = sockets.Any(s => s.Kind == "X11"),
            HasWaylandSocket = sockets.Any(s => s.Kind == "Wayland"),
            HasDbusSocket = sockets.Any(s => s.Kind == "D-Bus"),
            HasPipeWireSocket = sockets.Any(s => s.Kind == "PipeWire"),
            HasX11Library = maps.Any(n => n.StartsWith("libX11.so", StringComparison.OrdinalIgnoreCase) ||
                                          n.StartsWith("libxcb.so", StringComparison.OrdinalIgnoreCase)),
            HasWaylandLibrary = maps.Any(n => n.StartsWith("libwayland-client", StringComparison.OrdinalIgnoreCase)),
            SocketPaths = sockets.Select(s => s.Path).Where(p => p.Length > 0).Distinct().Take(24).ToList(),
        };
        var toolkit = ToolkitDetector.Detect(maps, cmdline, env, backend);
        var status = ReadStatus(pid);

        return new ProcessSnapshot
        {
            Pid = pid,
            ParentPid = status.ParentPid,
            Name = status.Name,
            Exe = SafeLink($"/proc/{pid}/exe"),
            Cmdline = cmdline,
            Cwd = SafeLink($"/proc/{pid}/cwd"),
            User = status.User,
            State = status.State,
            RssKb = status.RssKb,
            Threads = status.Threads,
            StartedAt = status.StartedAt,
            Sandbox = DetectSandbox(env),
            Toolkit = toolkit,
            Backend = backend,
            Sockets = sockets,
            Environment = env,
            KeyLibraries = maps.Where(IsInterestingLibrary)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .Cast<string>()
                .ToList(),
        };
    }

    public static string? SafeLink(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch { return null; }
    }

    private static string? ReadCmdline(int pid)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
            if (bytes.Length == 0) return null;
            var text = Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
            return text.Length > 4000 ? text[..4000] + "…" : text;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(int pid)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{pid}/environ");
            var text = Encoding.UTF8.GetString(bytes);
            foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = entry.IndexOf('=');
                if (idx <= 0) continue;
                result[entry[..idx]] = entry[(idx + 1)..];
            }
        }
        catch { /* permission denied for other users */ }
        return result;
    }

    private static HashSet<string> ReadMaps(int pid)
    {
        var libs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
            {
                var idx = line.IndexOf(" /", StringComparison.Ordinal);
                if (idx < 0) continue;
                var path = line[(idx + 1)..].Trim();
                if (path.Length == 0 || path.StartsWith('[')) continue;
                path = path.Split(" (deleted)", 2)[0];
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name)) libs.Add(name);
            }
        }
        catch { /* ignore */ }
        return libs;
    }

    private static bool IsInterestingLibrary(string name)
    {
        string[] keys =
        [
            "gtk", "gdk", "qt", "qml", "x11", "xcb", "wayland", "sdl", "glfw", "egl", "libgl",
            "vulkan", "electron", "cef", "xul", "flutter", "avalonia", "skia", "wine", "jvm",
            "awt", "tk", "wx", "mesa", "pipewire", "dbus", "gbm", "drm", "adwaita", "gtk-layer",
            "winit", "libcosmic", "libffm", "libgtk-4", "libgtk-3", "libxt", "libxmu",
            "libxaw", "libxm", "libfltk", "libfox",
        ];
        var lower = name.ToLowerInvariant();
        return keys.Any(lower.Contains);
    }

    private static (string Name, int ParentPid, string? User, string? State, long RssKb, int Threads, string? StartedAt)
        ReadStatus(int pid)
    {
        string? name = null, user = null, state = null, startedAt = null;
        var parentPid = 0;
        long rssKb = 0;
        var threads = 0;
        var uid = "";
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("Name:")) name = line[5..].Trim();
                else if (line.StartsWith("PPid:")) int.TryParse(line[5..].Trim(), out parentPid);
                else if (line.StartsWith("Uid:")) uid = line[4..].Trim().Split('\t', ' ')[0];
                else if (line.StartsWith("State:")) state = line[6..].Trim();
                else if (line.StartsWith("VmRSS:")) long.TryParse(line[6..].Replace(" kB", "").Trim(), out rssKb);
                else if (line.StartsWith("Threads:")) int.TryParse(line[8..].Trim(), out threads);
            }
            user = ResolveUser(uid);
        }
        catch { /* ignore */ }

        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            if (close > 0)
            {
                var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // fields[0] = state (field 3), fields[19] = starttime (field 22)
                if (fields.Length > 19 && ulong.TryParse(fields[19], out var startTicks))
                {
                    var uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);
                    var seconds = uptime - startTicks / 100.0;
                    if (seconds >= 0) startedAt = DateTime.Now.AddSeconds(-seconds).ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
        }
        catch { /* ignore */ }

        return (name ?? "?", parentPid, user, state, rssKb, threads, startedAt);
    }

    private static string? ResolveUser(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length > 2 && parts[2] == uid) return parts[0];
            }
        }
        catch { /* ignore */ }
        return uid;
    }

    private static string? DetectSandbox(IReadOnlyDictionary<string, string> env)
    {
        if (env.TryGetValue("FLATPAK_ID", out var flatpak)) return $"Flatpak · {flatpak}";
        if (env.TryGetValue("SNAP_NAME", out var snap)) return $"Snap · {snap}";
        if (env.TryGetValue("SNAP", out _)) return "Snap";
        if (env.TryGetValue("APPIMAGE", out var appImage)) return $"AppImage · {Path.GetFileName(appImage)}";
        if (env.TryGetValue("container", out var container) && container is "flatpak" or "snap") return container;
        return null;
    }

    private static IReadOnlyList<SocketConnection> ReadSockets(int pid)
    {
        var inodePaths = ReadUnixSocketTable();
        var peers = ReadSsPeerMap(pid);
        var result = new List<SocketConnection>();
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; }
                catch { continue; }
                if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                var inode = target[8..].TrimEnd(']');
                inodePaths.TryGetValue(inode, out var ownPath);
                peers.TryGetValue(inode, out var peerInode);
                inodePaths.TryGetValue(peerInode ?? "", out var peerPath);
                var path = !string.IsNullOrEmpty(ownPath) ? ownPath : peerPath ?? "";
                var kind = ClassifySocket(path) ?? ClassifySocket(peerPath ?? "");
                if (kind is null) continue;
                result.Add(new SocketConnection { Kind = kind, Path = path, Inode = inode });
            }
        }
        catch { /* ignore */ }

        return result
            .GroupBy(s => (s.Kind, s.Path, s.Inode))
            .Select(g => g.First())
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Regex PidRegex = new(@"pid=(\d+)", RegexOptions.Compiled);
    private static readonly Lock SsLock = new();
    private static DateTime _ssCacheAt = DateTime.MinValue;
    private static Dictionary<int, Dictionary<string, string>> _ssCache = new();

    /// <summary>
    /// The client end of a Wayland socket is usually unnamed. `ss -x -p` exposes the peer inode;
    /// resolving that inode through /proc/net/unix gives us the actual wayland-* / bus path.
    /// </summary>
    private static Dictionary<string, string> ReadSsPeerMap(int pid)
    {
        lock (SsLock)
        {
            if ((DateTime.UtcNow - _ssCacheAt).TotalSeconds > 3)
            {
                _ssCache = BuildSsCache();
                _ssCacheAt = DateTime.UtcNow;
            }
            return _ssCache.TryGetValue(pid, out var map)
                ? map
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<int, Dictionary<string, string>> BuildSsCache()
    {
        var result = new Dictionary<int, Dictionary<string, string>>();
        if (!CommandRunner.Exists("ss")) return result;
        try
        {
            var psi = new ProcessStartInfo("ss")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("-n");
            using var process = Process.Start(psi);
            if (process is null) return result;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500)) return result;

            foreach (var line in output.Split('\n'))
            {
                var usersIndex = line.IndexOf("users:(", StringComparison.Ordinal);
                if (usersIndex < 0) continue;
                var before = line[..usersIndex];
                var numbers = before.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => ulong.TryParse(t, out _))
                    .ToList();
                if (numbers.Count < 2) continue;
                var localInode = numbers[^2];
                var peerInode = numbers[^1];
                if (peerInode == "0") continue;

                foreach (Match match in PidRegex.Matches(line[usersIndex..]))
                {
                    if (!int.TryParse(match.Groups[1].Value, out var linePid)) continue;
                    if (!result.TryGetValue(linePid, out var map))
                        result[linePid] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                    map[localInode] = peerInode;
                }
            }
        }
        catch { /* ignore */ }
        return result;
    }

    private static string? ClassifySocket(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var lower = path.ToLowerInvariant();
        if (lower.Contains(".x11-unix") || lower.Contains("/x11/")) return "X11";
        if (lower.Contains("wayland-")) return "Wayland";
        if (lower.Equals("/run/user") || lower.EndsWith("/bus") || lower.Contains("/bus/") || lower.Contains("dbus")) return "D-Bus";
        if (lower.Contains("pipewire")) return "PipeWire";
        if (lower.Contains("pulse")) return "PulseAudio";
        if (lower.Contains("xwayland")) return "X11";
        return null;
    }

    private static Dictionary<string, string> ReadUnixSocketTable()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/net/unix").Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 7) continue;
                var inode = parts[6];
                var path = parts.Length >= 8 ? string.Join(' ', parts[7..]) : "";
                map[inode] = path;
            }
        }
        catch { /* ignore */ }
        return map;
    }

    private sealed record CacheEntry(ProcessSnapshot Snapshot, DateTime ReadAt);
}

public static class ToolkitDetector
{
    public static ToolkitInfo Detect(
        IReadOnlySet<string> libraries,
        string? cmdline,
        IReadOnlyDictionary<string, string> env,
        BackendEvidence backend)
    {
        var evidence = new List<string>();
        var names = libraries.Select(l => l.ToLowerInvariant()).ToList();
        var cmd = (cmdline ?? "").ToLowerInvariant();

        bool Has(params string[] needles) => needles.Any(n => names.Any(x => x.Contains(n)));

        string? result = null;
        string? version = null;

        void Pick(string name, string? ver = null)
        {
            result ??= name;
            version ??= ver;
        }

        if (Has("libavalonianative", "avalonia") || cmd.Contains("avalonia"))
        {
            Pick("Avalonia");
            evidence.Add("libAvaloniaNative.so");
        }
        if (Has("libelectron", "electron") || cmd.Contains("electron"))
        {
            Pick("Electron");
            evidence.Add("libelectron.so");
        }
        if (Has("libcef", "chromium"))
        {
            Pick("Chromium / CEF");
            evidence.Add("libcef.so");
        }
        if (Has("libxul", "firefox") || cmd.Contains("firefox"))
        {
            Pick("Firefox / Gecko");
            evidence.Add("libxul.so");
        }
        if (Has("libflutter"))
        {
            Pick("Flutter");
            evidence.Add("libflutter_linux_gtk.so");
        }
        if (Has("libqt6core", "libqt6gui"))
        {
            Pick("Qt 6");
            evidence.Add("libQt6Core.so");
        }
        else if (Has("libqt5core", "libqt5gui"))
        {
            Pick("Qt 5");
            evidence.Add("libQt5Core.so");
        }
        if (Has("libgtk-4"))
        {
            Pick("GTK 4");
            evidence.Add("libgtk-4.so");
        }
        else if (Has("libgtk-3"))
        {
            Pick("GTK 3");
            evidence.Add("libgtk-3.so");
        }
        else if (Has("libgtk-x11-2.0", "libgtk-2"))
        {
            Pick("GTK 2");
            evidence.Add("libgtk-x11-2.0.so");
        }
        if (Has("libadwaita"))
        {
            evidence.Add("libadwaita (GNOME)");
            if (result is "GTK 4") result = "GTK 4 / libadwaita";
        }
        if (Has("libawt_xawt", "libjvm"))
        {
            Pick("Java AWT / Swing");
            evidence.Add("libawt_xawt.so");
        }
        if (Has("libwine", "ntdll.so"))
        {
            Pick("Wine");
            evidence.Add("libwine");
        }
        if (Has("libsdl3"))
        {
            Pick("SDL3");
            evidence.Add("libSDL3.so");
        }
        else if (Has("libsdl2"))
        {
            Pick("SDL2");
            evidence.Add("libSDL2-2.0.so");
        }
        if (Has("libglfw"))
        {
            Pick("GLFW");
            evidence.Add("libglfw.so");
        }
        if (Has("libtk8", "libtcl8"))
        {
            Pick("Tk");
            evidence.Add("libtk8.so");
        }
        if (Has("libwx_gtk", "libwx_base"))
        {
            Pick("wxWidgets");
            evidence.Add("libwx_gtk3u_core");
        }
        if (Has("libecore_evas", "libelementary"))
        {
            Pick("Enlightenment / EFL");
            evidence.Add("libecore_evas.so");
        }
        if (Has("libxaw", "libxt.so", "libxmu"))
        {
            Pick("X11 / Xt (Athena)");
            evidence.Add("libXt.so / libXaw");
        }
        if (Has("libxm.so"))
        {
            Pick("Motif");
            evidence.Add("libXm.so");
        }
        if (Has("libfltk"))
        {
            Pick("FLTK");
            evidence.Add("libfltk.so");
        }
        if (Has("libfox-"))
        {
            Pick("FOX Toolkit");
            evidence.Add("libfox");
        }
        if (Has("libwinit") || (backend.HasWaylandSocket && result is null && cmd.Contains("cargo")))
        {
            Pick("Rust / winit");
            evidence.Add("libwinit");
        }
        if (result is null && (backend.HasWaylandSocket || backend.HasWaylandLibrary) &&
            !(backend.HasX11Socket || backend.HasX11Library))
        {
            Pick("Wayland 客户端（工具包未知）");
            evidence.Add("加载 libwayland-client，未发现常见工具包");
        }
        if (result is null && (backend.HasX11Socket || backend.HasX11Library) &&
            !(backend.HasWaylandSocket || backend.HasWaylandLibrary))
        {
            Pick("X11 客户端（工具包未知）");
            evidence.Add("加载 libX11 / libxcb，未发现常见工具包");
        }

        var hints = new List<string>();
        AddEnvHint(env, "GDK_BACKEND", hints);
        AddEnvHint(env, "QT_QPA_PLATFORM", hints);
        AddEnvHint(env, "SDL_VIDEODRIVER", hints);
        AddEnvHint(env, "ELECTRON_OZONE_PLATFORM_HINT", hints);
        AddEnvHint(env, "MOZ_ENABLE_WAYLAND", hints);
        AddEnvHint(env, "CLUTTER_BACKEND", hints);
        evidence.AddRange(hints);

        if (backend.HasX11Socket) evidence.Add("已连接 X11 / XWayland socket");
        else if (backend.HasX11Library) evidence.Add("加载 libX11");
        if (backend.HasWaylandSocket) evidence.Add("已连接 Wayland socket");
        else if (backend.HasWaylandLibrary) evidence.Add("加载 libwayland-client");
        if (backend.HasPipeWireSocket) evidence.Add("已连接 PipeWire (屏幕共享/捕获)");

        if (result is null)
        {
            if (backend.HasX11Socket && !backend.HasWaylandSocket)
            {
                Pick("X11 客户端 (工具包未知)");
            }
            else if (backend.HasWaylandSocket)
            {
                Pick("Wayland 客户端 (工具包未知)");
            }
            else
            {
                Pick("未知");
            }
        }

        return new ToolkitInfo
        {
            Name = result ?? "未知",
            Version = version,
            Evidence = evidence.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
        };
    }

    private static void AddEnvHint(IReadOnlyDictionary<string, string> env, string key, List<string> hints)
    {
        if (env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            hints.Add($"{key}={value}");
    }
}
