using System.Runtime.InteropServices;
using System.Text;
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
    private readonly Dictionary<(ulong Client, long Sequence), (string Name, ulong Window)> _lastRequests = [];
    private CancellationTokenSource? _cts;
    private XRecordNative.XRecordInterceptProc? _callback;
    private Thread? _thread;
    private IntPtr _display;
    private IntPtr _atomDisplay;
    private IntPtr _range;
    private nuint _context;
    private Action<X11RecordedEvent>? _sink;
    private bool _stopping;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public Func<X11RecordedEvent, bool>? Filter { get; set; }
    public Action<X11ProtocolRecord>? ProtocolSink { get; set; }

    public bool Start(Action<X11RecordedEvent> sink)
    {
        lock (_lock)
        {
            if (IsRunning) return true;
            if (_stopping)
            {
                Error = LocalizationService.L("X11 RECORD 仍在停止中，请稍后重试", "X11 RECORD is still stopping; try again shortly");
                return false;
            }
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

            // Capture all delivered/device events, protocol requests/replies and errors.
            // Core request ranges may be rejected by some servers; retry without them.
            WriteRange(_range, XRecordNative.RangeCoreRequests, 1, 127);
            WriteRange(_range, XRecordNative.RangeCoreReplies, 1, 127);
            WriteExtRange(_range, XRecordNative.RangeExtRequests, 128, 255, 0, ushort.MaxValue);
            WriteExtRange(_range, XRecordNative.RangeExtReplies, 128, 255, 0, ushort.MaxValue);
            WriteRange(_range, XRecordNative.RangeDeliveredEvents, 2, 255);
            WriteRange(_range, XRecordNative.RangeDeviceEvents, 2, 255);
            WriteRange(_range, XRecordNative.RangeErrors, 0, 255);

            var clients = new[] { XRecordNative.XRecordAllClients };
            var ranges = new[] { _range };
            var datumFlags = XRecordNative.XRecordFromServerTime | XRecordNative.XRecordFromClientSequence;
            _context = XRecordNative.XRecordCreateContext(
                _display, datumFlags, clients, 1, ranges, 1);
            if (_context == 0)
            {
                // Some servers reject core request ranges. Fall back to events only.
                XRecordNative.XFree(_range);
                _range = XRecordNative.XRecordAllocRange();
                if (_range == IntPtr.Zero)
                {
                    Error = LocalizationService.L("XRecordAllocRange 失败", "XRecordAllocRange failed");
                    Cleanup();
                    return false;
                }

                WriteRange(_range, XRecordNative.RangeDeliveredEvents, 2, 255);
                WriteRange(_range, XRecordNative.RangeDeviceEvents, 2, 255);
                WriteRange(_range, XRecordNative.RangeErrors, 0, 255);
                ranges = [_range];
                _context = XRecordNative.XRecordCreateContext(
                    _display, datumFlags, clients, 1, ranges, 1);
                if (_context != 0)
                    AppLog.Warn("X11 RECORD request/reply ranges were rejected; recording events only.");
            }

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

    private static void WriteExtRange(
        IntPtr range, int offset, byte firstMajor, byte lastMajor, ushort firstMinor, ushort lastMinor)
    {
        Marshal.WriteByte(range, offset, firstMajor);
        Marshal.WriteByte(range, offset + 1, lastMajor);
        Marshal.WriteInt16(range, offset + 2, unchecked((short)firstMinor));
        Marshal.WriteInt16(range, offset + 4, unchecked((short)lastMinor));
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
            if (swapped) return; // local servers are never byte-swapped; skip exotic remotes

            var clientBase = (ulong)Marshal.ReadInt64(recordedData, XRecordNative.DataIdBase);
            var clientSequence = Marshal.ReadInt64(recordedData, XRecordNative.DataClientSequence);

            if (category == XRecordNative.XRecordFromClient)
            {
                if (ProtocolSink is null || bytes.Length < 4) return;
                ProtocolSink(ParseRequest(bytes, clientBase, clientSequence));
                return;
            }

            if (category != XRecordNative.XRecordFromServer) return;
            if (bytes.Length < 4) return;

            if ((bytes[0] & 0x7f) == 1)
            {
                if (ProtocolSink is null) return;
                ProtocolSink(ParseReply(bytes, clientBase, clientSequence));
                return;
            }

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

    private X11ProtocolRecord ParseRequest(byte[] b, ulong clientBase, long clientSequence)
    {
        var opcode = b[0];
        var name = RequestName(opcode);
        var window = RequestWindow(opcode, b);
        if (_lastRequests.Count > 800) _lastRequests.Clear();
        _lastRequests[(clientBase, clientSequence)] = (name, window);

        return new X11ProtocolRecord
        {
            IsRequest = true,
            Opcode = opcode,
            Name = name,
            ClientBase = clientBase,
            Sequence = clientSequence,
            Window = window,
            Summary = DescribeRequest(opcode, b),
            Hex = Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64))),
        };
    }

    private X11ProtocolRecord ParseReply(byte[] b, ulong clientBase, long clientSequence)
    {
        var sequence = (long)U16(b, 2);
        if (!_lastRequests.TryGetValue((clientBase, sequence), out var request) &&
            clientSequence != sequence)
        {
            _lastRequests.TryGetValue((clientBase, clientSequence), out request);
        }

        var name = string.IsNullOrEmpty(request.Name) ? "Reply" : $"Reply · {request.Name}";
        var summary = DescribeReply(request.Name, b, sequence);
        return new X11ProtocolRecord
        {
            IsRequest = false,
            Opcode = 1,
            Name = name,
            ClientBase = clientBase,
            Sequence = sequence,
            Window = request.Window,
            Summary = summary,
            Hex = Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64))),
        };
    }

    private string DescribeRequest(int opcode, byte[] b)
    {
        switch (opcode)
        {
            case 1: // CreateWindow
                return $"wid=0x{U32(b, 4):x} parent=0x{U32(b, 8):x} {U16(b, 16)}×{U16(b, 18)} @ {I16(b, 20)},{I16(b, 22)}";
            case 2: // ChangeWindowAttributes
                return $"window=0x{U32(b, 4):x} mask=0x{U32(b, 8):x}";
            case 7: // ReparentWindow
                return $"window=0x{U32(b, 4):x} parent=0x{U32(b, 8):x} @ {I16(b, 12)},{I16(b, 14)}";
            case 12: // ConfigureWindow
                return $"window=0x{U32(b, 4):x} mask=0x{U16(b, 8):x}";
            case 16: // InternAtom
            {
                var nameLength = U16(b, 8);
                var atom = nameLength > 0 && b.Length >= 12 + nameLength
                    ? Encoding.ASCII.GetString(b, 12, nameLength)
                    : "";
                return $"name={atom} only_if_exists={b[4]}";
            }
            case 17: // GetAtomName
                return $"atom={AtomName(U32(b, 4))}";
            case 18: // ChangeProperty
            {
                var property = AtomName(U32(b, 8));
                var type = AtomName(U32(b, 12));
                var format = b.Length > 16 ? b[16] : 0;
                var items = b.Length >= 24 ? U32(b, 20) : 0;
                return $"window=0x{U32(b, 4):x} property={property} type={type} format={format} items={items}" +
                       FormatPropertyPreview(type, format, items, b, 24);
            }
            case 19: // DeleteProperty
                return $"window=0x{U32(b, 4):x} property={AtomName(U32(b, 8))}";
            case 20: // GetProperty
                return $"window=0x{U32(b, 4):x} property={AtomName(U32(b, 8))} type={AtomName(U32(b, 12))} offset={U32(b, 16)} length={U32(b, 20)}";
            case 22: // SetSelectionOwner
                return $"owner=0x{U32(b, 4):x} selection={AtomName(U32(b, 8))} time={U32(b, 12)}";
            case 24: // ConvertSelection
                return $"requestor=0x{U32(b, 4):x} selection={AtomName(U32(b, 8))} target={AtomName(U32(b, 12))} property={AtomName(U32(b, 16))}";
            case 25: // SendEvent
            {
                var eventType = b.Length > 16 ? b[16] & 0x7f : 0;
                var detail = eventType == 33 && b.Length >= 28
                    ? $" clientMessage={AtomName(U32(b, 24))} format={(b.Length > 17 ? b[17] : 0)}"
                    : "";
                return $"dest=0x{U32(b, 4):x} propagate={b[8]} mask=0x{U32(b, 12):x} event={TypeName(eventType)}{detail}";
            }
            case 40: // TranslateCoordinates
                return $"src=0x{U32(b, 4):x} dst=0x{U32(b, 8):x} @ {I16(b, 12)},{I16(b, 14)}";
            case 42: // SetInputFocus
                return $"focus=0x{U32(b, 4):x} revert={b[8]} time={U32(b, 12)}";
            case 45: // OpenFont
            case 47: // QueryFont
                return $"font=0x{U32(b, 4):x}";
            case 62: // CopyArea
            case 63: // CopyPlane
                return $"src=0x{U32(b, 4):x} dst=0x{U32(b, 8):x}";
            case 72: // PutImage
            case 73: // GetImage
                return $"drawable=0x{U32(b, 4):x}";
            default:
                var window = RequestWindow(opcode, b);
                return window != 0 ? $"window=0x{window:x}" : "";
        }
    }

    private string DescribeReply(string requestName, byte[] b, long sequence)
    {
        var length = U32(b, 4);
        var summary = $"seq={sequence} len={length}";

        if (requestName == "GetProperty" && b.Length >= 32)
        {
            var type = AtomName(U32(b, 8));
            var format = b[1];
            var items = U32(b, 16);
            summary += $" type={type} format={format} items={items}" +
                       FormatPropertyPreview(type, format, items, b, 32);
        }
        else if (requestName == "GetGeometry" && b.Length >= 24)
        {
            summary += $" root=0x{U32(b, 8):x} {I16(b, 12)}×{I16(b, 14)} @ {I16(b, 16)},{I16(b, 18)} border={I16(b, 20)} depth={b[21]}";
        }
        else if (requestName == "QueryTree" && b.Length >= 20)
        {
            summary += $" root=0x{U32(b, 8):x} parent=0x{U32(b, 12):x} children={U16(b, 16)}";
        }
        else if (requestName == "GetWindowAttributes" && b.Length >= 24)
        {
            summary += $" class={b[8]} map_state={b[26]} override_redirect={b[27]}";
        }

        return summary;
    }

    private static string FormatPropertyPreview(
        string atomName, int format, uint items, byte[] b, int offset)
    {
        if (format == 0 || items == 0 || b.Length <= offset) return "";
        try
        {
            if (format == 8)
            {
                var count = (int)Math.Min(Math.Min(items, 48u), (uint)(b.Length - offset));
                if (count <= 0) return "";
                if (atomName is "STRING" or "UTF8_STRING" or "ATOM_NAME")
                {
                    var text = Encoding.UTF8.GetString(b, offset, count).ReplaceLineEndings(" ");
                    return $" value=\"{text}\"";
                }
                return $" data={Convert.ToHexString(b.AsSpan(offset, count))}";
            }

            if (format == 32)
            {
                var count = (int)Math.Min(Math.Min(items, 8u), (uint)((b.Length - offset) / 4));
                if (count <= 0) return "";
                var values = new List<string>(count);
                for (var i = 0; i < count; i++) values.Add(U32(b, offset + i * 4).ToString());
                return $" values=[{string.Join(", ", values)}]";
            }
        }
        catch { /* malformed optional preview */ }
        return "";
    }

    private static ulong RequestWindow(int opcode, byte[] b) => opcode switch
    {
        16 or 17 or 36 or 37 or 43 or 44 or 45 or 47 or 49 or 50 or 52 or 83 or 85 or 91 or 92 or 97 or 98 or 99 or 101 or 104 or 106 or 108 or 110 or 117 or 119 => 0,
        1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 18 or 19 or 20 or 21 or 22 or 24 or 25 or 26 or 28 or 31 or 33 or 38 or 39 or 40 or 42 or 53 or 54 or 55 or 56 or 60 or 61 or 62 or 63 or 72 or 73 => b.Length >= 8 ? U32(b, 4) : 0,
        _ => b.Length >= 8 ? U32(b, 4) : 0,
    };

    private static string RequestName(int opcode) => opcode switch
    {
        1 => "CreateWindow",
        2 => "ChangeWindowAttributes",
        3 => "GetWindowAttributes",
        4 => "DestroyWindow",
        5 => "DestroySubwindows",
        6 => "ChangeSaveSet",
        7 => "ReparentWindow",
        8 => "MapWindow",
        9 => "MapSubwindows",
        10 => "UnmapWindow",
        11 => "UnmapSubwindows",
        12 => "ConfigureWindow",
        13 => "CirculateWindow",
        14 => "GetGeometry",
        15 => "QueryTree",
        16 => "InternAtom",
        17 => "GetAtomName",
        18 => "ChangeProperty",
        19 => "DeleteProperty",
        20 => "GetProperty",
        21 => "ListProperties",
        22 => "SetSelectionOwner",
        23 => "GetSelectionOwner",
        24 => "ConvertSelection",
        25 => "SendEvent",
        26 => "GrabPointer",
        27 => "UngrabPointer",
        28 => "GrabButton",
        29 => "UngrabButton",
        30 => "ChangeActivePointerGrab",
        31 => "GrabKeyboard",
        32 => "UngrabKeyboard",
        33 => "GrabKey",
        34 => "UngrabKey",
        35 => "AllowEvents",
        36 => "GrabServer",
        37 => "UngrabServer",
        38 => "QueryPointer",
        39 => "GetMotionEvents",
        40 => "TranslateCoordinates",
        41 => "WarpPointer",
        42 => "SetInputFocus",
        43 => "GetInputFocus",
        44 => "QueryKeymap",
        45 => "OpenFont",
        46 => "CloseFont",
        47 => "QueryFont",
        48 => "QueryTextExtents",
        49 => "ListFonts",
        50 => "ListFontsWithInfo",
        51 => "SetFontPath",
        52 => "GetFontPath",
        53 => "CreatePixmap",
        54 => "FreePixmap",
        55 => "CreateGC",
        56 => "ChangeGC",
        57 => "CopyGC",
        58 => "SetDashes",
        59 => "SetClipRectangles",
        60 => "FreeGC",
        61 => "ClearArea",
        62 => "CopyArea",
        63 => "CopyPlane",
        64 => "PolyPoint",
        65 => "PolyLine",
        66 => "PolySegment",
        67 => "PolyRectangle",
        68 => "PolyArc",
        69 => "FillPoly",
        70 => "PolyFillRectangle",
        71 => "PolyFillArc",
        72 => "PutImage",
        73 => "GetImage",
        74 => "PolyText8",
        75 => "PolyText16",
        76 => "ImageText8",
        77 => "ImageText16",
        78 => "CreateColormap",
        79 => "FreeColormap",
        80 => "CopyColormapAndFree",
        81 => "InstallColormap",
        82 => "UninstallColormap",
        83 => "ListInstalledColormaps",
        84 => "AllocColor",
        85 => "AllocNamedColor",
        86 => "AllocColorCells",
        87 => "AllocColorPlanes",
        88 => "FreeColors",
        89 => "StoreColors",
        90 => "StoreNamedColor",
        91 => "QueryColors",
        92 => "LookupColor",
        93 => "CreateCursor",
        94 => "CreateGlyphCursor",
        95 => "FreeCursor",
        96 => "RecolorCursor",
        97 => "QueryBestSize",
        98 => "QueryExtension",
        99 => "ListExtensions",
        100 => "ChangeKeyboardMapping",
        101 => "GetKeyboardMapping",
        102 => "ChangeKeyboardControl",
        103 => "GetKeyboardControl",
        104 => "Bell",
        105 => "ChangePointerControl",
        106 => "GetPointerControl",
        107 => "SetScreenSaver",
        108 => "GetScreenSaver",
        109 => "ChangeHosts",
        110 => "ListHosts",
        111 => "SetAccessControl",
        112 => "SetCloseDownMode",
        113 => "KillClient",
        114 => "RotateProperties",
        115 => "ForceScreenSaver",
        116 => "SetPointerMapping",
        117 => "GetPointerMapping",
        118 => "SetModifierMapping",
        119 => "GetModifierMapping",
        127 => "NoOperation",
        >= 128 => $"Extension {opcode:x2}",
        _ => $"Request {opcode}",
    };

    private static short I16(byte[] b, int offset)
        => (short)(b[offset] | (b[offset + 1] << 8));

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
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_stopping || _cts is null) return;
            _stopping = true;
            IsRunning = false;
            thread = _thread;
            cts = _cts;
        }

        AppLog.Info("X11 RECORD stop: cancelling context");
        try { cts?.Cancel(); } catch { /* ignore */ }

        var joined = true;
        if (thread is not null)
        {
            try { joined = thread.Join(2000); } catch { joined = false; }
        }
        AppLog.Info($"X11 RECORD stop: monitor thread joined={joined}");

        // Cleanup can block on a busy X server. Never hold the UI thread or the
        // monitor lock while calling XRecordDisableContext / XCloseDisplay.
        _ = Task.Run(() =>
        {
            if (!joined)
            {
                try { thread?.Join(10000); } catch { /* ignore */ }
            }
            FinishStop();
        });
    }

    private void FinishStop()
    {
        IntPtr display;
        IntPtr atomDisplay;
        IntPtr range;
        nuint context;
        CancellationTokenSource? cts;

        lock (_lock)
        {
            display = _display;
            atomDisplay = _atomDisplay;
            range = _range;
            context = _context;
            cts = _cts;
            _display = IntPtr.Zero;
            _atomDisplay = IntPtr.Zero;
            _range = IntPtr.Zero;
            _context = 0;
            _thread = null;
            _callback = null;
            IsRunning = false;
        }

        try
        {
            if (context != 0 && display != IntPtr.Zero)
            {
                XRecordNative.XRecordDisableContext(display, context);
                XRecordNative.XFlush(display);
            }
        }
        catch { /* ignore */ }

        if (range != IntPtr.Zero)
        {
            try { XRecordNative.XFree(range); } catch { /* ignore */ }
        }
        if (atomDisplay != IntPtr.Zero)
        {
            try { XRecordNative.XCloseDisplay(atomDisplay); } catch { /* ignore */ }
        }
        if (display != IntPtr.Zero)
        {
            try { XRecordNative.XCloseDisplay(display); } catch { /* ignore */ }
        }

        lock (_lock)
        {
            cts?.Dispose();
            if (ReferenceEquals(_cts, cts)) _cts = null;
            _stopping = false;
        }
        AppLog.Info("X11 RECORD stop: cleanup complete");
    }

    public void Dispose() => Stop();
}
