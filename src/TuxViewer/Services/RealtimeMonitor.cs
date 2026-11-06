using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TuxViewer.Interop;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>
/// Watches window state changes without polling. Backends are selected by session:
/// X11 (XSelectInput on a dedicated Display), niri event-stream, Sway subscribe and
/// Hyprland socket2. Events are advisory; the UI still falls back to a slow refresh.
/// </summary>
public sealed class RealtimeMonitor : IDisposable
{
    private readonly List<Backend> _backends = [];
    private CancellationTokenSource? _cts;
    private Action<WindowEventRecord>? _sink;

    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> BackendNames => _backends.Select(b => b.Name).ToList();
    public string StatusText => _backends.Count == 0
        ? "没有可用的实时事件后端（将继续使用轮询）"
        : string.Join(" · ", _backends.Select(b => b.Name));

    public void Start(Action<WindowEventRecord> sink)
    {
        if (IsRunning) return;
        _sink = sink;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            _backends.Add(new X11EventBackend());

        var niriSocket = Environment.GetEnvironmentVariable("NIRI_SOCKET");
        if (!string.IsNullOrWhiteSpace(niriSocket) && CommandRunner.Exists("niri"))
            _backends.Add(new NiriEventBackend());

        var swaySocket = Environment.GetEnvironmentVariable("SWAYSOCK");
        if (!string.IsNullOrWhiteSpace(swaySocket) && CommandRunner.Exists("swaymsg"))
            _backends.Add(new SwayEventBackend());

        var hyprSignature = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (!string.IsNullOrWhiteSpace(hyprSignature))
            _backends.Add(new HyprlandEventBackend(hyprSignature));

        foreach (var backend in _backends)
        {
            try
            {
                backend.Start(Emit, token);
            }
            catch
            {
                backend.Dispose();
            }
        }

        IsRunning = _backends.Count > 0;
    }

    private void Emit(WindowEventRecord record)
    {
        try { _sink?.Invoke(record); }
        catch { /* monitoring must never crash the app */ }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        foreach (var backend in _backends) backend.Dispose();
        _backends.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private abstract class Backend : IDisposable
    {
        public abstract string Name { get; }
        public abstract void Start(Action<WindowEventRecord> emit, CancellationToken token);
        public abstract void Dispose();
    }

    /// <summary>Selects PropertyNotify / StructureNotify / FocusChange on X11 windows.</summary>
    private sealed class X11EventBackend : Backend
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly HashSet<ulong> _watched = [];
        private readonly Dictionary<IntPtr, string> _atomNames = [];
        private IntPtr _display;
        private IntPtr _root;
        private Thread? _thread;
        private Action<WindowEventRecord>? _emit;

        public override string Name => "X11";

        public override void Start(Action<WindowEventRecord> emit, CancellationToken token)
        {
            _emit = emit;
            _display = X11Native.XOpenDisplay(null);
            if (_display == IntPtr.Zero) return;
            _root = X11Native.XDefaultRootWindow(_display);
            X11Native.XSelectInput(
                _display, _root,
                X11Native.SubstructureNotifyMask | X11Native.PropertyChangeMask);
            Rescan();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TuxViewer X11 monitor",
            };
            _thread.Start();
            token.Register(() => { try { _cts.Cancel(); } catch { /* ignore */ } });
        }

        private void Run()
        {
            var lastRescan = DateTime.UtcNow;
            while (!_cts.IsCancellationRequested && _display != IntPtr.Zero)
            {
                try
                {
                    var processed = 0;
                    while (!_cts.IsCancellationRequested && X11Native.XPending(_display) > 0 && processed++ < 256)
                    {
                        if (X11Native.XNextEvent(_display, out var ev) != 0) break;
                        Handle(ev);
                    }

                    if ((DateTime.UtcNow - lastRescan).TotalSeconds >= 2)
                    {
                        Rescan();
                        lastRescan = DateTime.UtcNow;
                    }

                    Thread.Sleep(40);
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void Rescan()
        {
            try
            {
                SelectTree((ulong)(nuint)_root, 0, 0);
            }
            catch { /* ignore */ }
        }

        private int SelectTree(ulong id, int depth, int count)
        {
            if (depth > 4 || count > 1500 || _cts.IsCancellationRequested) return count;
            Select(id);
            if (X11Native.XQueryTree(_display, (IntPtr)(nuint)id, out _, out _, out var children, out var childCount) == 0)
                return count;
            try
            {
                if (children == IntPtr.Zero) return count;
                var ids = new IntPtr[childCount];
                Marshal.Copy(children, ids, 0, (int)childCount);
                foreach (var child in ids)
                    count = SelectTree((ulong)child, depth + 1, count + 1);
            }
            finally
            {
                if (children != IntPtr.Zero) X11Native.XFree(children);
            }
            return count;
        }

        private void Select(ulong id)
        {
            if (id == 0 || !_watched.Add(id)) return;
            X11Native.XSelectInput(
                _display, (IntPtr)(nuint)id,
                X11Native.PropertyChangeMask | X11Native.StructureNotifyMask | X11Native.FocusChangeMask);
        }

        private void Handle(X11Native.XEvent ev)
        {
            switch (ev.Type)
            {
                case X11Native.PropertyNotify:
                {
                    var atom = AtomName((IntPtr)(nint)ev.Atom);
                    if (ev.Window == (nuint)_root)
                    {
                        if (atom == "_NET_ACTIVE_WINDOW")
                            Emit(MonitorEventKind.Focus, "root", null, "活动窗口切换");
                        else if (atom is "_NET_CLIENT_LIST" or "_NET_CLIENT_LIST_STACKING")
                            Emit(MonitorEventKind.Generic, "root", null, $"{atom} 更新");
                        return;
                    }

                    if (IsHighlightWindow((ulong)ev.Window)) return;
                    var kind = atom is "_NET_WM_NAME" or "WM_NAME" ? MonitorEventKind.Title : MonitorEventKind.Property;
                    Emit(kind, $"0x{(ulong)ev.Window:x}", atom, $"{atom} 属性更新");
                    return;
                }
                case X11Native.ConfigureNotify:
                {
                    var id = (ulong)ev.EventWindow;
                    if (IsHighlightWindow(id)) return;
                    Emit(MonitorEventKind.Configure, $"0x{id:x}", null,
                        $"{ev.ConfigureWidth}×{ev.ConfigureHeight} @ {ev.ConfigureX},{ev.ConfigureY}");
                    return;
                }
                case X11Native.MapNotify:
                {
                    var id = (ulong)ev.EventWindow;
                    if (IsHighlightWindow(id)) return;
                    Emit(MonitorEventKind.Map, $"0x{id:x}", null, "窗口已映射");
                    return;
                }
                case X11Native.UnmapNotify:
                {
                    var id = (ulong)ev.EventWindow;
                    if (IsHighlightWindow(id)) return;
                    Emit(MonitorEventKind.Unmap, $"0x{id:x}", null, "窗口已取消映射");
                    return;
                }
                case X11Native.DestroyNotify:
                {
                    var id = (ulong)ev.EventWindow;
                    _watched.Remove(id);
                    Emit(MonitorEventKind.Closed, $"0x{id:x}", null, "窗口已销毁");
                    return;
                }
                case X11Native.CreateNotify:
                {
                    var id = (ulong)ev.EventWindow;
                    Select(id);
                    if (!IsHighlightWindow(id))
                        Emit(MonitorEventKind.Opened, $"0x{id:x}", null, "新窗口");
                    return;
                }
                case X11Native.ReparentNotify:
                {
                    Select((ulong)ev.EventWindow);
                    return;
                }
                case X11Native.FocusIn:
                case X11Native.FocusOut:
                {
                    var id = (ulong)ev.Window;
                    if (IsHighlightWindow(id)) return;
                    Emit(MonitorEventKind.Focus, $"0x{id:x}", null,
                        ev.Type == X11Native.FocusIn ? "获得焦点" : "失去焦点");
                    return;
                }
            }
        }

        private bool IsHighlightWindow(ulong id)
        {
            try
            {
                if (X11Native.XFetchName(_display, (IntPtr)(nuint)id, out var name) == 0 || name == IntPtr.Zero)
                    return false;
                try
                {
                    return string.Equals(Marshal.PtrToStringAnsi(name), "TuxViewer Highlight", StringComparison.Ordinal);
                }
                finally
                {
                    X11Native.XFree(name);
                }
            }
            catch
            {
                return false;
            }
        }

        private string AtomName(IntPtr atom)
        {
            if (atom == IntPtr.Zero) return "None";
            if (_atomNames.TryGetValue(atom, out var cached)) return cached;
            var ptr = X11Native.XGetAtomName(_display, atom);
            if (ptr == IntPtr.Zero) return $"ATOM({atom})";
            try
            {
                var name = Marshal.PtrToStringAnsi(ptr) ?? $"ATOM({atom})";
                _atomNames[atom] = name;
                return name;
            }
            finally
            {
                X11Native.XFree(ptr);
            }
        }

        private void Emit(MonitorEventKind kind, string nativeId, string? appId, string summary)
            => _emit?.Invoke(new WindowEventRecord
            {
                Source = "X11",
                Kind = kind,
                NativeId = nativeId,
                AppId = appId,
                Summary = summary,
            });

        public override void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _thread?.Join(300); } catch { /* ignore */ }
            if (_display != IntPtr.Zero)
            {
                try { X11Native.XCloseDisplay(_display); } catch { /* ignore */ }
                _display = IntPtr.Zero;
            }
            _cts.Dispose();
        }
    }

    private sealed class NiriEventBackend : Backend
    {
        private CancellationTokenSource? _cts;
        public override string Name => "niri";

        public override void Start(Action<WindowEventRecord> emit, CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cts = _cts;
            _ = Task.Run(() => StreamAsync(
                "niri",
                ["msg", "event-stream"],
                line => Parse(line, emit),
                cts.Token), cts.Token);
        }

        private static void Parse(string line, Action<WindowEventRecord> emit)
        {
            var text = line.Trim();
            if (text.Length == 0) return;

            if (text.StartsWith("Window ", StringComparison.Ordinal))
            {
                var rest = text["Window ".Length..];
                var space = rest.IndexOf(' ');
                var change = space > 0 ? rest[..space] : rest;
                var args = space > 0 ? rest[(space + 1)..] : "";
                var id = ExtractValue(args, "id=");
                if (id.Length == 0) return;
                var title = ExtractQuoted(args, "title");
                var appId = ExtractQuoted(args, "app_id");

                var (kind, summary) = change switch
                {
                    "closed" => (MonitorEventKind.Closed, "窗口已关闭"),
                    "focus-changed" => (MonitorEventKind.Focus, "焦点变化"),
                    "layout-changed" => (MonitorEventKind.Configure, "布局 / 几何变化"),
                    "urgent-changed" => (MonitorEventKind.Generic, "紧急状态变化"),
                    "opened-or-changed" => (MonitorEventKind.Opened, "窗口打开或更新"),
                    _ => (MonitorEventKind.Generic, change),
                };
                if (!string.IsNullOrWhiteSpace(title) && kind is MonitorEventKind.Opened or MonitorEventKind.Generic)
                    summary = $"{title}";
                emit(new WindowEventRecord
                {
                    Source = "niri",
                    Kind = kind,
                    NativeId = id,
                    AppId = appId.Length == 0 ? null : appId,
                    Summary = summary,
                });
                return;
            }

            if (text.StartsWith("Workspace ", StringComparison.Ordinal))
            {
                emit(new WindowEventRecord
                {
                    Source = "niri",
                    Kind = MonitorEventKind.Workspace,
                    Summary = text,
                });
            }
            else if (text.StartsWith("Keyboard", StringComparison.Ordinal))
            {
                emit(new WindowEventRecord
                {
                    Source = "niri",
                    Kind = MonitorEventKind.Generic,
                    Summary = text,
                });
            }
        }

        private static string ExtractValue(string text, string key)
        {
            var index = text.IndexOf(key, StringComparison.Ordinal);
            if (index < 0) return "";
            index += key.Length;
            var end = text.IndexOf(' ', index);
            return end < 0 ? text[index..] : text[index..end];
        }

        private static string ExtractQuoted(string text, string key)
        {
            var index = text.IndexOf(key + "=\"", StringComparison.Ordinal);
            if (index < 0) return "";
            index += key.Length + 2;
            var end = text.IndexOf('"', index);
            return end < 0 ? "" : text[index..end];
        }

        public override void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
        }
    }

    private sealed class SwayEventBackend : Backend
    {
        private CancellationTokenSource? _cts;
        public override string Name => "Sway";

        public override void Start(Action<WindowEventRecord> emit, CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cts = _cts;
            _ = Task.Run(() => StreamAsync(
                "swaymsg",
                ["-t", "subscribe", "-m", "[\"window\"]"],
                line => Parse(line, emit),
                cts.Token), cts.Token);
        }

        private static void Parse(string line, Action<WindowEventRecord> emit)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var change = GetString(root, "change") ?? "window";
                var container = root.TryGetProperty("container", out var c) && c.ValueKind == JsonValueKind.Object
                    ? c
                    : root;
                var id = container.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : null;
                var title = GetString(container, "name");
                if (container.TryGetProperty("window_properties", out var props) && props.ValueKind == JsonValueKind.Object)
                    title = GetString(props, "title") ?? title;
                var appId = GetString(container, "app_id");
                var kind = change switch
                {
                    "new" => MonitorEventKind.Opened,
                    "close" => MonitorEventKind.Closed,
                    "focus" => MonitorEventKind.Focus,
                    "title" => MonitorEventKind.Title,
                    "move" or "resize" or "floating" or "fullscreen_mode" => MonitorEventKind.Configure,
                    "urgent" => MonitorEventKind.Generic,
                    _ => MonitorEventKind.Generic,
                };
                emit(new WindowEventRecord
                {
                    Source = "Sway",
                    Kind = kind,
                    NativeId = id,
                    AppId = appId,
                    Summary = $"{change} · {title ?? appId ?? id ?? "窗口"}",
                });
            }
            catch { /* partial or unknown line */ }
        }

        private static string? GetString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        public override void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
        }
    }

    private sealed class HyprlandEventBackend(string signature) : Backend
    {
        private CancellationTokenSource? _cts;
        public override string Name => "Hyprland";

        public override void Start(Action<WindowEventRecord> emit, CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cts = _cts;
            _ = Task.Run(() => ReadSocketAsync(cts.Token, emit), cts.Token);
        }

        private async Task ReadSocketAsync(CancellationToken token, Action<WindowEventRecord> emit)
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                          ?? $"/run/user/{GetUid()}";
            var path = Path.Combine(runtime, "hypr", signature, ".socket2.sock");
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null) break;
                    Parse(line, emit);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* socket is optional */ }
        }

        private static void Parse(string line, Action<WindowEventRecord> emit)
        {
            var separator = line.IndexOf(">>", StringComparison.Ordinal);
            if (separator <= 0) return;
            var name = line[..separator];
            var data = line[(separator + 2)..];
            var parts = data.Split(',');
            var address = parts.Length > 0 ? parts[0] : "";

            var (kind, summary) = name switch
            {
                "openwindow" => (MonitorEventKind.Opened,
                    parts.Length >= 4 ? $"{parts[2]} · {string.Join(',', parts.Skip(3))}" : "窗口打开"),
                "closewindow" => (MonitorEventKind.Closed, "窗口关闭"),
                "activewindow" => (MonitorEventKind.Focus,
                    parts.Length >= 2 ? $"{parts[0]} · {string.Join(',', parts.Skip(1))}" : "焦点变化"),
                "windowtitle" or "windowtitlev2" => (MonitorEventKind.Title,
                    parts.Length >= 2 ? string.Join(',', parts.Skip(1)) : "标题变化"),
                "movewindow" or "movewindowv2" => (MonitorEventKind.Workspace, "窗口移动到工作区"),
                "changefloatingmode" => (MonitorEventKind.Configure, "浮动状态变化"),
                "fullscreen" => (MonitorEventKind.Configure, "全屏状态变化"),
                "minimize" => (MonitorEventKind.Configure, "最小化状态变化"),
                "urgent" => (MonitorEventKind.Generic, "紧急状态变化"),
                _ => (MonitorEventKind.Generic, name),
            };

            emit(new WindowEventRecord
            {
                Source = "Hyprland",
                Kind = kind,
                NativeId = name == "activewindow" ? null : address,
                Summary = summary,
            });
        }

        private static uint GetUid()
        {
            try { return getuid(); }
            catch { return 1000; }
        }

        [DllImport("libc", EntryPoint = "getuid")]
        private static extern uint getuid();

        public override void Dispose()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            _cts?.Dispose();
        }
    }

    private static async Task StreamAsync(
        string executable, string[] args, Action<string> onLine, CancellationToken token)
    {
        if (!CommandRunner.Exists(executable)) return;
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(token);
                    if (line is null) break;
                    onLine(line);
                }
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* event stream is best effort */ }
    }
}
