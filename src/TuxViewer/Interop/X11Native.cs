using System.Runtime.InteropServices;

namespace TuxViewer.Interop;

internal static class X11Native
{
    private const string X11 = "libX11.so.6";
    private const string XExt = "libXext.so.6";

    public const int IsUnmapped = 0;
    public const int IsUnviewable = 1;
    public const int IsViewable = 2;

    public const int InputOutput = 1;
    public const int InputOnly = 2;

    public const int KeyPress = 2;
    public const int KeyRelease = 3;
    public const int ButtonPress = 4;
    public const int ButtonRelease = 5;
    public const int FocusIn = 9;
    public const int FocusOut = 10;
    public const int CreateNotify = 16;
    public const int DestroyNotify = 17;
    public const int UnmapNotify = 18;
    public const int MapNotify = 19;
    public const int ReparentNotify = 21;
    public const int ConfigureNotify = 22;
    public const int PropertyNotify = 28;
    public const int ClientMessage = 33;

    public const long KeyPressMask = 1L << 0;
    public const long ButtonPressMask = 1L << 2;
    public const long ButtonReleaseMask = 1L << 3;
    public const long PointerMotionMask = 1L << 6;
    public const long StructureNotifyMask = 1L << 17;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long SubstructureRedirectMask = 1L << 20;
    public const long FocusChangeMask = 1L << 21;
    public const long PropertyChangeMask = 1L << 22;

    public const int GrabModeAsync = 1;
    public const long AnyPropertyType = 0;
    public const int PropModeReplace = 0;
    public const ulong CurrentTime = 0;
    public const int ShapeBounding = 0;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;
    public const uint XCCrosshair = 34;
    public const uint XCLeftPtr = 68;
    public const uint CWBackPixel = 1 << 1;
    public const uint CWOverrideRedirect = 1 << 9;
    public const int CopyFromParent = 0;
    public const ulong AllPlanes = ~0UL;
    public const int ZPixmap = 2;

    public const int XA_ATOM = 4;
    public const int XA_CARDINAL = 6;
    public const int XA_STRING = 31;
    public const int XA_WINDOW = 33;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int XErrorHandlerDelegate(IntPtr display, IntPtr errorEvent);

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X, Y, Width, Height;
        public int BorderWidth, Depth;
        public IntPtr Visual;
        public nuint Root;
        public int Class;
        public int BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled;
        public int MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSizeHints
    {
        public long Flags;
        public int X, Y, Width, Height;
        public int MinWidth, MinHeight, MaxWidth, MaxHeight;
        public int WidthInc, HeightInc;
        public int MinAspectX, MinAspectY, MaxAspectX, MaxAspectY;
        public int BaseWidth, BaseHeight;
        public int WinGravity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClassHint
    {
        public IntPtr ResName;
        public IntPtr ResClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr BackgroundPixmap;
        public nuint BackgroundPixel;
        public IntPtr BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public long EventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Colormap;
        public IntPtr Cursor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public struct XEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ulong Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public IntPtr Display;
        [FieldOffset(32)] public nuint Window;
        [FieldOffset(40)] public nuint Root;
        [FieldOffset(48)] public nuint Subwindow;
        [FieldOffset(56)] public ulong Time;
        [FieldOffset(64)] public int X;
        [FieldOffset(68)] public int Y;
        [FieldOffset(72)] public int RootX;
        [FieldOffset(76)] public int RootY;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint KeyOrButton;
        [FieldOffset(88)] public int SameScreen;

        // PropertyNotify: atom @40, time @48, state @56.
        [FieldOffset(40)] public nuint Atom;
        [FieldOffset(48)] public ulong PropertyTime;
        [FieldOffset(56)] public int NotifyState;

        // ConfigureNotify / CreateNotify / DestroyNotify / ReparentNotify.
        [FieldOffset(40)] public nuint EventWindow;
        [FieldOffset(48)] public int ConfigureX;
        [FieldOffset(52)] public int ConfigureY;
        [FieldOffset(56)] public int ConfigureWidth;
        [FieldOffset(60)] public int ConfigureHeight;
        [FieldOffset(64)] public int ConfigureBorderWidth;
        [FieldOffset(72)] public nuint ConfigureAbove;
        [FieldOffset(80)] public int ConfigureOverrideRedirect;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClientMessageEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public nint Data1, Data2, Data3, Data4, Data5;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short X, Y;
        public ushort Width, Height;
    }

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XDefaultScreen(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XDisplayString(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XServerVendor(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XVendorRelease(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XQueryTree(
        IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr parentReturn,
        out IntPtr childrenReturn, out uint childCountReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFree(IntPtr data);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetGeometry(
        IntPtr display, IntPtr drawable, out IntPtr rootReturn,
        out int xReturn, out int yReturn, out uint widthReturn, out uint heightReturn,
        out uint borderWidthReturn, out uint depthReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XTranslateCoordinates(
        IntPtr display, IntPtr source, IntPtr destination, int sourceX, int sourceY,
        out int destX, out int destY, out IntPtr childReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr nameReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XStoreName(IntPtr display, IntPtr window, string windowName);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetClassHint(IntPtr display, IntPtr window, out XClassHint classHint);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, long longOffset, long longLength,
        bool delete, IntPtr requiredType, out IntPtr actualTypeReturn, out int actualFormatReturn,
        out ulong itemCountReturn, out ulong bytesAfterReturn, out IntPtr propertyReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XListProperties(IntPtr display, IntPtr window, out int countReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetWMNormalHints(IntPtr display, IntPtr window, out XSizeHints hints, out long suppliedReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetTransientForHint(IntPtr display, IntPtr window, out IntPtr transientForReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetWMProtocols(IntPtr display, IntPtr window, out IntPtr protocolsReturn, out int countReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGrabPointer(
        IntPtr display, IntPtr grabWindow, bool ownerEvents, long eventMask,
        int pointerMode, int keyboardMode, IntPtr confineTo, IntPtr cursor, ulong time);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XUngrabPointer(IntPtr display, ulong time);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGrabKeyboard(
        IntPtr display, IntPtr grabWindow, bool ownerEvents, int pointerMode, int keyboardMode, ulong time);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XUngrabKeyboard(IntPtr display, ulong time);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGrabKey(
        IntPtr display, int keycode, uint modifiers, IntPtr grabWindow,
        bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint XStringToKeysym(string name);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XQueryPointer(
        IntPtr display, IntPtr window, out IntPtr rootReturn, out IntPtr childReturn,
        out int rootXReturn, out int rootYReturn, out int winXReturn, out int winYReturn,
        out uint maskReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XCreateFontCursor(IntPtr display, uint shape);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFreeCursor(IntPtr display, IntPtr cursor);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XCheckMaskEvent(IntPtr display, long eventMask, out XEvent eventReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XPending(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XNextEvent(IntPtr display, out XEvent eventReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XKillClient(IntPtr display, IntPtr resource);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format,
        int mode, byte[] data, int nelements);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XGetImage(
        IntPtr display, IntPtr drawable, int x, int y, uint width, uint height,
        ulong planeMask, int format);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XDestroyImage(IntPtr image);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, long eventMask, ref XClientMessageEvent eventSend);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XFlush(IntPtr display);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSync(IntPtr display, bool discard);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, nuint borderPixel, nuint backgroundPixel);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XCreateWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint windowClass, IntPtr visual, uint valueMask, ref XSetWindowAttributes attributes);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSetWindowBackground(IntPtr display, IntPtr window, nuint backgroundPixel);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSetWindowBackgroundPixmap(IntPtr display, IntPtr window, IntPtr pixmap);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XMapRaised(IntPtr display, IntPtr window);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XUnmapWindow(IntPtr display, IntPtr window);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, ulong time);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern int XGetInputFocus(IntPtr display, out IntPtr focusReturn, out int revertToReturn);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr XSetErrorHandler(XErrorHandlerDelegate handler);

    [DllImport(X11, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint XKeysymToKeycode(IntPtr display, nuint keysym);

    [DllImport(XExt, CallingConvention = CallingConvention.Cdecl)]
    public static extern void XShapeCombineRectangles(
        IntPtr display, IntPtr dest, int destKind, int xOffset, int yOffset,
        XRectangle[] rectangles, int count, int operation, int ordering);

    [DllImport(XExt, CallingConvention = CallingConvention.Cdecl)]
    public static extern void XShapeCombineMask(
        IntPtr display, IntPtr dest, int destKind, int xOffset, int yOffset, IntPtr source, int operation);
}
