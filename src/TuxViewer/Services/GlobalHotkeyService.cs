using System.Runtime.InteropServices;
using TuxViewer.Interop;

namespace TuxViewer.Services;

/// <summary>
/// Registers a system-wide pick shortcut through XGrabKey on a dedicated X11 Display.
/// On Wayland this only reaches XWayland when the compositor routes the key to X11;
/// the UI therefore also provides native compositor keybinding snippets.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ShiftMask = 1;
    private const uint LockMask = 2;
    private const uint ControlMask = 4;
    private const uint Mod1Mask = 8;
    private const uint Mod2Mask = 16;
    private const uint Mod4Mask = 64;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private IntPtr _display;
    private IntPtr _root;
    private int _keycode;
    private uint _modifiers;
    private Action? _sink;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public string ShortcutText => "Ctrl+Alt+X";

    public bool Start(Action sink)
    {
        lock (_lock)
        {
            if (IsRunning) return true;
            Error = null;
            _sink = sink;
            _display = X11Native.XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                Error = "无法连接 X 服务器";
                return false;
            }

            _root = X11Native.XDefaultRootWindow(_display);
            var keysym = X11Native.XStringToKeysym("x");
            _keycode = keysym == 0 ? 0 : (int)X11Native.XKeysymToKeycode(_display, keysym);
            if (_keycode == 0)
            {
                Error = "无法解析快捷键 X";
                Cleanup();
                return false;
            }

            _modifiers = ControlMask | Mod1Mask;
            // Grab the usual lock-key variants so CapsLock / NumLock do not break the shortcut.
            foreach (var extra in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
                X11Native.XGrabKey(_display, _keycode, _modifiers | extra, _root, false, 1, 1);
            X11Native.XSync(_display, false);

            _cts = new CancellationTokenSource();
            IsRunning = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TuxViewer global hotkey",
            };
            _thread.Start();
            return true;
        }
    }

    private void Run()
    {
        try
        {
            var token = _cts!.Token;
            while (!token.IsCancellationRequested && _display != IntPtr.Zero)
            {
                try
                {
                    if (X11Native.XPending(_display) > 0)
                    {
                        if (X11Native.XNextEvent(_display, out var ev) == 0 &&
                            ev.Type == X11Native.KeyPress)
                        {
                            _sink?.Invoke();
                        }
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _thread?.Join(400); } catch { /* ignore */ }
            _thread = null;
            if (_display != IntPtr.Zero && _keycode != 0)
            {
                try
                {
                    foreach (var extra in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })
                        X11Native.XUngrabKey(_display, _keycode, _modifiers | extra, _root);
                    X11Native.XFlush(_display);
                }
                catch { /* ignore */ }
            }
            IsRunning = false;
            Cleanup();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cleanup()
    {
        if (_display != IntPtr.Zero)
        {
            try { X11Native.XCloseDisplay(_display); } catch { /* ignore */ }
            _display = IntPtr.Zero;
        }
    }

    public static string BuildCompositorConfig()
    {
        return """
# TuxViewer · 全局拾取热键（复制对应合成器的一行）

# niri (~/.config/niri/config.kdl)
binds {
    Mod+Shift+X { spawn "tuxviewer" "--pick-x11" "click"; }
}

# Sway (~/.config/sway/config)
bindsym $mod+Shift+x exec tuxviewer --pick-x11 click

# Hyprland (~/.config/hypr/hyprland.conf)
bind = SUPER SHIFT, X, exec, tuxviewer --pick-x11 click

# 说明：tuxviewer 是 scripts/install.sh 安装的命令；也可以换成 TuxViewer 可执行文件路径。
# 若使用 X11 会话，也可以直接在 TuxViewer 中启用下方 XGrabKey 全局热键。
""";
    }

    public void Dispose() => Stop();
}
