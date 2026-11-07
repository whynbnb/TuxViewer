using System.Runtime.InteropServices;
using TuxViewer.Interop;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>
/// Full X11 event recorder built on the RECORD extension (libXtst). Unlike XSelectInput it does
/// not need to modify event masks on target windows, so it can observe keyboard, mouse, property,
/// configure, focus and client messages globally within the X server.
/// </summary>
public sealed class X11EventMonitor : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, string> _atomCache = [];
    private readonly Dictionary<int, string> _keyCache = [];
    private CancellationTokenSource? _cts;
    private XRecordNative.XRecordInterceptProc? _callback;
    private Thread? _thread;
    private IntPtr _display;
    private IntPtr _atomDisplay;
    private IntPtr _range;
    private nuint _context;
    private Action<X11RecordedEvent>? _sink;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public Func<X11RecordedEvent, bool>? Filter { get; set; }

    public bool Start(Action<X11RecordedEvent> sink)
    {
        lock (_lock)
        {
            if (IsRunning) return true;
            Error = null;
            _sink = sink;

            _display = XRecordNative.XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                Error = LocalizationService.L("无法连接 X 服务器", "Cannot connect to the X server");
                return false;
            }

            if (XRecordNative.XRecordQueryVersion(_display, out _, out _) == 0)
            {
                Error = LocalizationService.L("X 服务器不支持 RECORD 扩展", "The X server does not support the RECORD extension");
                XRecordNative.XCloseDisplay(_display);
                _display = IntPtr.Zero;
                return false;
            }

            _atomDisplay = XRecordNative.XOpenDisplay(null);
            _range = XRecordNative.XRecordAllocRange();
            if (_range == IntPtr.Zero)
            {
                Error = LocalizationService.L("XRecordAllocRange 失败", "XRecordAllocRange failed");
                Cleanup();
                return false;
            }

            // Capture all delivered/device events and errors. The RECORD protocol
            // rejects ranges with first < 2 unless both ends are zero.
            WriteRange(_range, XRecordNative.RangeDeliveredEvents, 2, 255);
            WriteRange(_range, XRecordNative.RangeDeviceEvents, 2, 255);
            WriteRange(_range, XRecordNative.RangeErrors, 0, 255);

            var clients = new[] { XRecordNative.XRecordAllClients };
            var ranges = new[] { _range };
            _context = XRecordNative.XRecordCreateContext(
                _display, XRecordNative.XRecordFromServer, clients, 1, ranges, 1);
            if (_context == 0)
            {
                Error = LocalizationService.L("XRecordCreateContext 失败（权限或扩展不可用）", "XRecordCreateContext failed (permissions or extension unavailable)");
                Cleanup();
                return false;
            }

            _callback = OnRecord;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TuxViewer XRecord monitor",
            };
            _thread.Start();
            return true;
        }
    }

    private static void WriteRange(IntPtr range, int offset, byte first, byte last)
    {
        Marshal.WriteByte(range, offset, first);
        Marshal.WriteByte(range, offset + 1, last);
    }

    private void Run()
    {
        try
        {
            if (XRecordNative.XRecordEnableContextAsync(_display, _context, _callback!, IntPtr.Zero) == 0)
            {
                Error = LocalizationService.L("XRecordEnableContextAsync 失败", "XRecordEnableContextAsync failed");
                IsRunning = false;
                return;
            }

            var token = _cts!.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Record replies arrive on the same Display; drain them without blocking.
                    if (XRecordNative.XPending(_display) > 0)
                        XRecordNative.XRecordProcessReplies(_display);
                    else
                        Thread.Sleep(8);
                }
                catch
                {
                    Thread.Sleep(50);
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

    private void OnRecord(IntPtr closure, IntPtr recordedData)
    {
        try
        {
            if (recordedData == IntPtr.Zero || _cts?.IsCancellationRequested == true) return;

            var category = Marshal.ReadInt32(recordedData, XRecordNative.DataCategory);
            if (category is XRecordNative.XRecordStartOfData or XRecordNative.XRecordEndOfData) return;

            var swapped = Marshal.ReadInt32(recordedData, XRecordNative.DataClientSwapped) != 0;
            var dataPtr = Marshal.ReadIntPtr(recordedData, XRecordNative.DataPointer);
            var dataLength = (long)Marshal.ReadInt64(recordedData, XRecordNative.DataLength) * 4;
            if (dataPtr == IntPtr.Zero || dataLength <= 0) return;

            var length = (int)Math.Min(dataLength, 4096);
            var bytes = new byte[length];
            Marshal.Copy(dataPtr, bytes, 0, length);

            if (category != XRecordNative.XRecordFromServer) return;
            if (bytes.Length < 4) return;
            if (swapped) return; // local servers are never byte-swapped; skip exotic remotes

            var type = bytes[0] & 0x7f;
            var recorded = Parse(type, bytes);
            if (recorded is null) return;
            if (Filter?.Invoke(recorded) == false) return;
            _sink?.Invoke(recorded);
        }
        catch
        {
            // A malformed record must never break the event stream.
        }
        finally
        {
            if (recordedData != IntPtr.Zero)
                XRecordNative.XRecordFreeData(recordedData);
        }
    }

    private X11RecordedEvent? Parse(int type, byte[] b)
    {
        var name = TypeName(type);
        var category = Category(type);
        var window = EventWindow(type, b);
        var summary = Describe(type, b);
        if (summary.Length == 0) summary = name;
        var detail = Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64)));
        var isDevice = type is 2 or 3 or 4 or 5 or 6 or 7 or 8;

        return new X11RecordedEvent
        {
            Type = type,
            TypeName = name,
            Category = category,
            Window = window,
            RootWindow = isDevice ? U32(b, 8) : 0,
            RootX = isDevice ? U16(b, 20) : 0,
            RootY = isDevice ? U16(b, 22) : 0,
            Summary = summary,
            Detail = detail,
        };
    }

    private string Describe(int type, byte[] b)
    {
        switch (type)
        {
            case 2: // KeyPress
            case 3: // KeyRelease
                return $"{KeyName(b[1])} state=0x{U16(b, 28):x4} root={U32(b, 8):x}";
            case 4: // ButtonPress
            case 5: // ButtonRelease
                return $"button {b[1]} at {U16(b, 20)},{U16(b, 22)} state=0x{U16(b, 28):x4}";
            case 6: // MotionNotify
                return $"pointer {U16(b, 20)},{U16(b, 22)} state=0x{U16(b, 28):x4}";
            case 7: // EnterNotify
            case 8: // LeaveNotify
                return $"mode={b[1]} detail={b[30]} at {U16(b, 20)},{U16(b, 22)}";
            case 9:  // FocusIn
            case 10: // FocusOut
                return $"mode={b[8]} detail={b[9]}";
            case 12: // Expose
                return $"{U16(b, 12)}×{U16(b, 14)} @ {U16(b, 8)},{U16(b, 10)} count={U16(b, 16)}";
            case 16: // CreateNotify
                return $"parent=0x{U32(b, 4):x} {U16(b, 16)}×{U16(b, 18)} @ {U16(b, 12)},{U16(b, 14)}";
            case 17: // DestroyNotify
                return "window destroyed";
            case 18: // UnmapNotify
                return $"unmapped (event=0x{U32(b, 4):x})";
            case 19: // MapNotify
                return $"mapped (event=0x{U32(b, 4):x})";
            case 20: // MapRequest
                return $"map request (parent=0x{U32(b, 4):x})";
            case 21: // ReparentNotify
                return $"parent=0x{U32(b, 12):x} @ {U16(b, 16)},{U16(b, 18)}";
            case 22: // ConfigureNotify
                return $"{U16(b, 16)}×{U16(b, 18)} @ {U16(b, 12)},{U16(b, 14)} border={U16(b, 20)}";
            case 23: // ConfigureRequest
                return $"{U16(b, 16)}×{U16(b, 18)} @ {U16(b, 12)},{U16(b, 14)}";
            case 24: // GravityNotify
                return $"gravity event=0x{U32(b, 4):x}";
            case 25: // ResizeRequest
                return $"request {U16(b, 8)}×{U16(b, 10)}";
            case 26: // CirculateNotify
                return "circulate";
            case 28: // PropertyNotify
                return $"{AtomName(U32(b, 8))} state={(U32(b, 16) == 0 ? "NewValue" : "Deleted")}";
            case 30: // SelectionRequest
            case 31: // SelectionNotify
                return $"selection={AtomName(U32(b, 8))}";
            case 32: // ColormapNotify
                return $"colormap=0x{U32(b, 8):x} new={b[12]} state={b[13]}";
            case 33: // ClientMessage
                return $"{AtomName(U32(b, 8))} format={U32(b, 12)}";
            case 34: // MappingNotify
                return $"request={b[4]} first_keycode={b[5]} count={b[6]}";
            case 35: // GenericEvent
                return $"extension={U16(b, 4)} length={U32(b, 8)}";
            default:
                return "";
        }
    }

    private static ulong EventWindow(int type, byte[] b) => type switch
    {
        2 or 3 or 4 or 5 or 6 or 7 or 8 => U32(b, 12) is var target && target != 0
            ? target
            : U32(b, 16) is var child && child != 0 ? child : U32(b, 8),
        9 or 10 or 28 or 29 or 30 or 31 or 32 or 33 => U32(b, 4),
        16 or 20 => U32(b, 8),
        17 or 18 or 19 or 21 or 22 or 23 or 24 or 25 or 26 or 27 => U32(b, 8),
        _ => b.Length >= 8 ? U32(b, 4) : 0,
    };

    private static ushort U16(byte[] b, int offset)
        => (ushort)(b[offset] | (b[offset + 1] << 8));

    private static uint U32(byte[] b, int offset)
        => (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));

    private string AtomName(uint atom)
    {
        if (atom == 0) return "None";
        if (_atomCache.TryGetValue(atom, out var cached)) return cached;
        var result = $"0x{atom:x}";
        try
        {
            if (_atomDisplay != IntPtr.Zero)
            {
                var pointer = XRecordNative.XGetAtomName(_atomDisplay, (IntPtr)(long)atom);
                if (pointer != IntPtr.Zero)
                {
                    try { result = Marshal.PtrToStringAnsi(pointer) ?? result; }
                    finally { XRecordNative.XFree(pointer); }
                }
            }
        }
        catch { /* ignore */ }
        _atomCache[atom] = result;
        return result;
    }

    private string KeyName(byte keycode)
    {
        if (_keyCache.TryGetValue(keycode, out var cached)) return cached;
        var result = $"keycode {keycode}";
        try
        {
            if (_atomDisplay != IntPtr.Zero)
            {
                var keysym = XRecordNative.XkbKeycodeToKeysym(_atomDisplay, keycode, 0, 0);
                if (keysym != 0)
                {
                    var pointer = XRecordNative.XKeysymToString(keysym);
                    if (pointer != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStringAnsi(pointer);
                        if (!string.IsNullOrWhiteSpace(name))
                            result = $"keycode {keycode} ({name})";
                    }
                }
            }
        }
        catch { /* ignore */ }
        _keyCache[keycode] = result;
        return result;
    }

    private static string Category(int type) => type switch
    {
        2 or 3 => "Key",
        4 or 5 or 6 or 7 or 8 => "Mouse",
        9 or 10 or 11 => "Focus",
        16 or 17 or 18 or 19 or 20 => "Map",
        21 or 22 or 23 or 24 or 25 or 26 or 27 => "Configure",
        28 => "Property",
        33 => "ClientMessage",
        _ => "Other",
    };

    private static string TypeName(int type) => type switch
    {
        2 => "KeyPress",
        3 => "KeyRelease",
        4 => "ButtonPress",
        5 => "ButtonRelease",
        6 => "MotionNotify",
        7 => "EnterNotify",
        8 => "LeaveNotify",
        9 => "FocusIn",
        10 => "FocusOut",
        11 => "KeymapNotify",
        12 => "Expose",
        13 => "GraphicsExpose",
        14 => "NoExpose",
        15 => "VisibilityNotify",
        16 => "CreateNotify",
        17 => "DestroyNotify",
        18 => "UnmapNotify",
        19 => "MapNotify",
        20 => "MapRequest",
        21 => "ReparentNotify",
        22 => "ConfigureNotify",
        23 => "ConfigureRequest",
        24 => "GravityNotify",
        25 => "ResizeRequest",
        26 => "CirculateNotify",
        27 => "CirculateRequest",
        28 => "PropertyNotify",
        29 => "SelectionClear",
        30 => "SelectionRequest",
        31 => "SelectionNotify",
        32 => "ColormapNotify",
        33 => "ClientMessage",
        34 => "MappingNotify",
        35 => "GenericEvent",
        _ => $"Event{type}",
    };

    private void Cleanup()
    {
        if (_context != 0 && _display != IntPtr.Zero)
        {
            try { XRecordNative.XRecordFreeContext(_display, _context); } catch { /* ignore */ }
            _context = 0;
        }
        if (_range != IntPtr.Zero)
        {
            try { XRecordNative.XFree(_range); } catch { /* ignore */ }
            _range = IntPtr.Zero;
        }
        if (_atomDisplay != IntPtr.Zero)
        {
            try { XRecordNative.XCloseDisplay(_atomDisplay); } catch { /* ignore */ }
            _atomDisplay = IntPtr.Zero;
        }
        if (_display != IntPtr.Zero)
        {
            try { XRecordNative.XCloseDisplay(_display); } catch { /* ignore */ }
            _display = IntPtr.Zero;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_cts is null && _display == IntPtr.Zero) return;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _thread?.Join(800); } catch { /* ignore */ }
            _thread = null;
            if (_context != 0 && _display != IntPtr.Zero)
            {
                try
                {
                    XRecordNative.XRecordDisableContext(_display, _context);
                    XRecordNative.XFlush(_display);
                }
                catch { /* ignore */ }
            }
            IsRunning = false;
            Cleanup();
            _cts?.Dispose();
            _cts = null;
            _callback = null;
        }
    }

    public void Dispose() => Stop();
}
