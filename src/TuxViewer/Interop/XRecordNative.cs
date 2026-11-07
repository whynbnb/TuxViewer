using System.Runtime.InteropServices;

namespace TuxViewer.Interop;

/// <summary>libXtst XRecord bindings. XRecord reads core events without touching targets' masks.</summary>
internal static class XRecordNative
{
    private const string X11 = "libX11.so.6";
    private const string XTst = "libXtst.so.6";

    public const int XRecordFromServer = 0;
    public const int XRecordFromClient = 1;
    public const int XRecordClientStarted = 2;
    public const int XRecordClientDied = 3;
    public const int XRecordStartOfData = 4;
    public const int XRecordEndOfData = 5;
    public const nuint XRecordAllClients = 3;

    public const int XRecordFromServerTime = 0x01;
    public const int XRecordFromClientSequence = 0x04;

    // XRecordRange offsets (64-bit, verified with offsetof on the system headers).
    public const int RangeCoreRequests = 0;
    public const int RangeCoreReplies = 2;
    public const int RangeExtRequests = 4;
    public const int RangeExtReplies = 10;
    public const int RangeDeliveredEvents = 16;
    public const int RangeDeviceEvents = 18;
    public const int RangeErrors = 20;

    // XRecordInterceptData offsets.
    public const int DataIdBase = 0;
    public const int DataServerTime = 8;
    public const int DataClientSequence = 16;
    public const int DataCategory = 24;
    public const int DataClientSwapped = 28;
    public const int DataPointer = 32;
    public const int DataLength = 40;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void XRecordInterceptProc(IntPtr closure, IntPtr recordedData);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFlush(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSync(IntPtr display, bool discard);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XPending(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFree(IntPtr data);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XKeysymToString(nuint keysym);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XRecordQueryVersion(IntPtr display, out int major, out int minor);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XRecordAllocRange();

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint XRecordCreateContext(
        IntPtr display, int datumFlags, nuint[] clients, int nclients, IntPtr[] ranges, int nranges);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XRecordEnableContextAsync(
        IntPtr display, nuint context, XRecordInterceptProc callback, IntPtr closure);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern void XRecordProcessReplies(IntPtr display);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XRecordDisableContext(IntPtr display, nuint context);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XRecordFreeContext(IntPtr display, nuint context);

    [DllImport(XTst, CallingConvention = CallingConvention.Cdecl)]
    public static extern void XRecordFreeData(IntPtr recordedData);
}
