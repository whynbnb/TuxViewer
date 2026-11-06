using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TuxViewer.Interop;
using TuxViewer.Models;

namespace TuxViewer.Services;

/// <summary>Low-level X11 inspector. All calls on one shared Display are serialized.</summary>
public sealed class X11Inspector : IDisposable
{
    private static readonly X11Native.XErrorHandlerDelegate ErrorHandler = static (_, _) => 0;
    private static readonly bool DebugX11 = Environment.GetEnvironmentVariable("TUXVIEWER_X11_DEBUG") == "1";

    private static void Dbg(string message)
    {
        if (DebugX11) Console.Error.WriteLine($"[x11] {message}");
    }
    private static int _errorHandlerInstalled;

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IntPtr> _atoms = new(StringComparer.Ordinal);
    private readonly IntPtr _crosshair;
    private readonly bool _isWaylandSession;

    private IntPtr _overlay;
    private CancellationTokenSource? _overlayCts;
    private int _overlayGeneration;
    private bool _disposed;

    public string? DisplayName { get; }
    public string? Error { get; }
    public bool IsAvailable => _display != IntPtr.Zero;

    public X11Inspector(bool isWaylandSession)
    {
        _isWaylandSession = isWaylandSession;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        try
        {
            if (Interlocked.Exchange(ref _errorHandlerInstalled, 1) == 0)
                X11Native.XSetErrorHandler(ErrorHandler);

            var display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(display))
            {
                Error = "DISPLAY 未设置";
                return;
            }

            _display = X11Native.XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                Error = $"无法连接 X 服务器 ({display})";
                return;
            }

            _root = X11Native.XDefaultRootWindow(_display);
            DisplayName = Marshal.PtrToStringAnsi(X11Native.XDisplayString(_display));
            _crosshair = X11Native.XCreateFontCursor(_display, X11Native.XCCrosshair);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public string? GetWmName()
    {
        if (!IsAvailable) return null;
        lock (_lock)
        {
            try
            {
                var check = GetWindowPropertyValue((ulong)(nuint)_root, "_NET_SUPPORTING_WM_CHECK");
                if (check == 0) return null;
                return GetTextProperty(check, "_NET_WM_NAME") ?? GetTextProperty(check, "WM_NAME");
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        if (!IsAvailable) return [];
        lock (_lock)
        {
            try
            {
                var managed = GetWindowListProperty((ulong)(nuint)_root, "_NET_CLIENT_LIST").ToHashSet();
                var stacking = GetWindowListProperty((ulong)(nuint)_root, "_NET_CLIENT_LIST_STACKING");
                var rootChildren = GetChildren(_root);

                var ordered = new List<ulong>();
                if (stacking.Count > 0)
                {
                    for (var i = stacking.Count - 1; i >= 0; i--) ordered.Add(stacking[i]);
                }
                else
                {
                    for (var i = rootChildren.Count - 1; i >= 0; i--) ordered.Add(rootChildren[i]);
                }

                var seen = new HashSet<ulong>();
                var result = new List<WindowInfo>();
                foreach (var id in ordered)
                {
                    if (!seen.Add(id)) continue;
                    var info = BuildWindowInfo(id, managed.Contains(id), managed);
                    if (info is not null) result.Add(info);
                }

                foreach (var id in rootChildren)
                {
                    if (!seen.Add(id)) continue;
                    var info = BuildWindowInfo(id, managed.Contains(id), managed);
                    if (info is not null) result.Add(info);
                }

                return result;
            }
            catch
            {
                return [];
            }
        }
    }

    private WindowInfo? BuildWindowInfo(ulong id, bool managed, HashSet<ulong> managedSet)
    {
        var window = (IntPtr)(nuint)id;
        if (X11Native.XGetWindowAttributes(_display, window, out var attrs) == 0) return null;
        if (attrs.Class == X11Native.InputOnly) return null;

        var title = GetTextProperty(id, "_NET_WM_NAME") ?? GetTextProperty(id, "WM_NAME");
        var (instance, cls) = GetClassHint(id);
        var mapped = attrs.MapState == X11Native.IsViewable;
        var overrideRedirect = attrs.OverrideRedirect != 0;

        if (!mapped && !managed && !overrideRedirect) return null;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(cls)) return null;
        if (title == "TuxViewer Highlight") return null;
        if (attrs.Width <= 2 || attrs.Height <= 2) return null;

        var propertyPid = GetPid(id);
        var pid = propertyPid ?? ProcessResolver.FindPid(instance, cls);
        var (absX, absY) = TranslateToRoot(id);
        var workspaceRaw = GetCardinalProperty(id, "_NET_WM_DESKTOP");
        int? workspace = workspaceRaw is not null ? (int)workspaceRaw.Value : null;
        var type = JoinAtomProperty(id, "_NET_WM_WINDOW_TYPE");
        var state = JoinAtomProperty(id, "_NET_WM_STATE");

        var info = new WindowInfo
        {
            Id = $"x11:0x{id:x}",
            SourceId = "x11",
            SourceName = "X11",
            NativeId = $"0x{id:x}",
            Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title!,
            AppId = cls,
            WmClass = cls,
            Pid = pid,
            Backend = WindowBackend.X11,
            IsXWayland = _isWaylandSession,
            IsMapped = mapped,
            IsOwnWindow = pid == Environment.ProcessId,
            IsOverrideRedirect = overrideRedirect,
            X = absX,
            Y = absY,
            Width = attrs.Width,
            Height = attrs.Height,
            WorkspaceId = workspace,
            X11WindowId = id,
            RawJson = JsonSerializer.Serialize(new
            {
                provider = "x11",
                window = $"0x{id:x}",
                title,
                instance,
                @class = cls,
                pid,
                geometry = new { x = absX, y = absY, width = attrs.Width, height = attrs.Height },
                mapped,
                override_redirect = overrideRedirect,
                window_type = type,
                wm_state = state,
                desktop = workspace,
                xwayland = _isWaylandSession,
            }),
        };

        if (propertyPid is null && pid is not null) info.Extra["PID 来源"] = "WM_CLASS ↔ 进程名匹配";
        info.Extra["窗口 ID"] = $"0x{id:x}";
        info.Extra["实例名"] = instance ?? "—";
        info.Extra["窗口类"] = cls ?? "—";
        info.Extra["映射状态"] = mapped ? "viewable" : "unmapped";
        if (overrideRedirect) info.Extra["Override Redirect"] = "是";
        if (!string.IsNullOrWhiteSpace(type)) info.Extra["窗口类型"] = type!;
        if (!string.IsNullOrWhiteSpace(state)) info.Extra["WM State"] = state!;
        if (workspace is not null) info.Extra["工作区"] = workspace.ToString()!;
        return info;
    }

    public WindowInfo? GetActiveWindow()
    {
        if (!IsAvailable) return null;
        lock (_lock)
        {
            try
            {
                var id = GetWindowPropertyValue((ulong)(nuint)_root, "_NET_ACTIVE_WINDOW");
                if (id == 0)
                {
                    if (X11Native.XGetInputFocus(_display, out var focus, out _) == 0 || focus == IntPtr.Zero)
                        return null;
                    id = FindToplevel((ulong)focus);
                }

                var managed = GetWindowListProperty((ulong)(nuint)_root, "_NET_CLIENT_LIST").ToHashSet();
                return BuildWindowInfo(id, managed.Contains(id), managed);
            }
            catch
            {
                return null;
            }
        }
    }

    public X11WindowDetails? GetDetails(ulong id)
    {
        if (!IsAvailable) return null;
        lock (_lock)
        {
            try
            {
                var window = (IntPtr)(nuint)id;
                if (X11Native.XGetWindowAttributes(_display, window, out var attrs) == 0) return null;
                var title = GetTextProperty(id, "_NET_WM_NAME") ?? GetTextProperty(id, "WM_NAME");
                var (instance, cls) = GetClassHint(id);
                var (absX, absY) = TranslateToRoot(id);
                X11Native.XQueryTree(_display, window, out var root, out var parent, out var childrenPtr, out var childCount);
                if (childrenPtr != IntPtr.Zero) X11Native.XFree(childrenPtr);

                Dbg($"details start 0x{id:x}");
                var properties = GetProperties(id);
                Dbg("properties done");
                var children = new List<X11ChildInfo>();
                CollectChildren(id, 0, children, 300);
                Dbg("children done");
                var hints = GetNormalHints(id);
                Dbg("hints done");
                var transient = GetTransientFor(id);
                var protocols = GetProtocols(id);
                Dbg("protocols done");

                var details = new X11WindowDetails
                {
                    WindowId = id,
                    Title = title,
                    ClassName = cls,
                    InstanceName = instance,
                    X = absX,
                    Y = absY,
                    Width = attrs.Width,
                    Height = attrs.Height,
                    BorderWidth = attrs.BorderWidth,
                    Depth = attrs.Depth,
                    Mapped = attrs.MapState == X11Native.IsViewable,
                    OverrideRedirect = attrs.OverrideRedirect != 0,
                    InputOnly = attrs.Class == X11Native.InputOnly,
                    WindowType = JoinAtomProperty(id, "_NET_WM_WINDOW_TYPE"),
                    WmState = JoinAtomProperty(id, "_NET_WM_STATE"),
                    TransientFor = transient != 0 ? $"0x{transient:x}" : null,
                    Protocols = protocols,
                    WmNormalHints = hints,
                    RootWindow = (ulong)root,
                    ParentWindow = (ulong)parent,
                    ChildCount = (int)childCount,
                    EventMask = DescribeEventMask(attrs.YourEventMask),
                    Properties = properties,
                    Children = children,
                };
                return details;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Run an interactive X11 crosshair pick. Returns the clicked toplevel window id.</summary>
    public ulong? PickInteractive(IProgress<PickProgress>? progress, PickMode mode, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return null;
        lock (_lock)
        {
            var grabbedPointer = false;
            var grabbedKeyboard = false;
            var cursor = _crosshair;
            var pressed = false;

            try
            {
                var grab = X11Native.XGrabPointer(
                    _display, _root, false,
                    X11Native.ButtonPressMask | X11Native.ButtonReleaseMask,
                    X11Native.GrabModeAsync, X11Native.GrabModeAsync, IntPtr.Zero, cursor, X11Native.CurrentTime);
                grabbedPointer = grab == 0;
                Dbg($"XGrabPointer status={grab}");
                if (!grabbedPointer)
                {
                    progress?.Report(new PickProgress { Message = "X11 指针抓取失败（Wayland 下通常只支持 X11 窗口）" });
                    return null;
                }

                grabbedKeyboard = X11Native.XGrabKeyboard(
                    _display, _root, false, X11Native.GrabModeAsync, X11Native.GrabModeAsync, X11Native.CurrentTime) == 0;

                var escapeKey = X11Native.XKeysymToKeycode(_display, 0xff1b);
                var lastReport = DateTime.MinValue;
                var lastHover = 0UL;

                progress?.Report(new PickProgress
                {
                    Message = mode == PickMode.Drag
                        ? "X11 拖动拾取：在目标窗口按住左键拖动，松开选择 · 右键 / Esc 取消"
                        : "X11 点击拾取：移动准星，单击目标窗口 · 右键 / Esc 取消",
                });

                while (!cancellationToken.IsCancellationRequested)
                {
                    while (X11Native.XCheckMaskEvent(
                               _display,
                               X11Native.ButtonPressMask | X11Native.ButtonReleaseMask | X11Native.KeyPressMask,
                               out var ev) != 0)
                    {
                        if (ev.Type == X11Native.ButtonPress)
                        {
                            Dbg($"pick ButtonPress button={ev.KeyOrButton} root={ev.RootX},{ev.RootY}");
                            if (ev.KeyOrButton == 3)
                            {
                                HideHighlightUnlocked();
                                return null;
                            }
                            if (ev.KeyOrButton != 1) continue;

                            if (mode == PickMode.Click)
                                return FinishPick(progress, GetPickTarget(lastHover));

                            pressed = true;
                        }
                        else if (ev.Type == X11Native.ButtonRelease)
                        {
                            Dbg($"pick ButtonRelease button={ev.KeyOrButton} root={ev.RootX},{ev.RootY}");
                            if (ev.KeyOrButton != 1) continue;
                            if (mode == PickMode.Drag && pressed)
                                return FinishPick(progress, GetPickTarget(lastHover));
                            pressed = false;
                        }
                        else if (ev.Type == X11Native.KeyPress && ev.KeyOrButton == escapeKey)
                        {
                            HideHighlightUnlocked();
                            return null;
                        }
                    }

                    X11Native.XQueryPointer(
                        _display, _root, out _, out _, out _, out _, out _, out _, out _);
                    var hover = FindToplevel(GetPickTarget(lastHover));
                    var hoverChanged = hover != lastHover;

                    if (hoverChanged)
                    {
                        lastHover = hover;
                        if (hover != 0)
                            ShowHighlightUnlocked(hover, 1400);
                        else
                            HideHighlightUnlocked();
                    }

                    if (hover != 0 && (hoverChanged || (DateTime.UtcNow - lastReport).TotalMilliseconds > 400))
                    {
                        lastReport = DateTime.UtcNow;
                        var title = GetTextProperty(hover, "_NET_WM_NAME") ?? GetTextProperty(hover, "WM_NAME") ?? "";
                        var (_, cls) = GetClassHint(hover);
                        var (hx, hy) = TranslateToRoot(hover);
                        X11Native.XGetWindowAttributes(_display, (IntPtr)(nuint)hover, out var ha);
                        progress?.Report(new PickProgress
                        {
                            Message = $"X11 {(mode == PickMode.Drag ? "拖动" : "点击")}：0x{hover:x} · {cls ?? "?"} · {title}",
                            HoverWindow = new WindowInfo
                            {
                                Id = $"x11:0x{hover:x}",
                                SourceId = "x11",
                                SourceName = "X11",
                                Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title,
                                AppId = cls,
                                WmClass = cls,
                                Pid = GetPid(hover),
                                Backend = WindowBackend.X11,
                                IsXWayland = _isWaylandSession,
                                X = hx,
                                Y = hy,
                                Width = ha.Width,
                                Height = ha.Height,
                                X11WindowId = hover,
                            },
                        });
                    }

                    Thread.Sleep(16);
                }

                HideHighlightUnlocked();
                return null;
            }
            catch (OperationCanceledException)
            {
                HideHighlightUnlocked();
                return null;
            }
            catch
            {
                HideHighlightUnlocked();
                return null;
            }
            finally
            {
                if (grabbedKeyboard) X11Native.XUngrabKeyboard(_display, X11Native.CurrentTime);
                if (grabbedPointer) X11Native.XUngrabPointer(_display, X11Native.CurrentTime);
                X11Native.XSync(_display, false);
            }
        }
    }

    private ulong GetPickTarget(ulong fallback)
    {
        var deep = (ulong)(nuint)FindDeepestWindow(_root);
        return _overlay != IntPtr.Zero && deep == (ulong)_overlay ? fallback : deep;
    }

    private ulong? FinishPick(IProgress<PickProgress>? progress, ulong deepWindow)
    {
        if (deepWindow == 0)
        {
            progress?.Report(new PickProgress { Message = "指针下没有可选择的窗口" });
            return null;
        }

        var target = FindToplevel(deepWindow);
        if (target == 0) return null;
        var title = GetTextProperty(target, "_NET_WM_NAME") ?? GetTextProperty(target, "WM_NAME") ?? "";
        var (_, cls) = GetClassHint(target);
        var (tx, ty) = TranslateToRoot(target);
        X11Native.XGetWindowAttributes(_display, (IntPtr)(nuint)target, out var ta);
        ShowHighlightUnlocked(target, 2600);
        progress?.Report(new PickProgress
        {
            Message = $"已选择 X11 窗口 0x{target:x}",
            HoverWindow = new WindowInfo
            {
                Id = $"x11:0x{target:x}",
                SourceId = "x11",
                SourceName = "X11",
                Title = string.IsNullOrWhiteSpace(title) ? "(无标题)" : title,
                AppId = cls,
                WmClass = cls,
                Pid = GetPid(target),
                Backend = WindowBackend.X11,
                IsXWayland = _isWaylandSession,
                X = tx,
                Y = ty,
                Width = ta.Width,
                Height = ta.Height,
                X11WindowId = target,
            },
        });
        return target;
    }

    public void Focus(ulong id)
    {
        if (!IsAvailable) return;
        lock (_lock)
        {
            try
            {
                var window = (IntPtr)(nuint)id;
                X11Native.XRaiseWindow(_display, window);
                X11Native.XSetInputFocus(_display, window, 2, X11Native.CurrentTime);
                X11Native.XFlush(_display);
            }
            catch { /* ignore */ }
        }
    }

    public void Close(ulong id)
    {
        if (!IsAvailable) return;
        lock (_lock)
        {
            try
            {
                var message = new X11Native.XClientMessageEvent
                {
                    Type = X11Native.ClientMessage,
                    Display = _display,
                    Window = (nuint)id,
                    MessageType = (nuint)Atom("_NET_CLOSE_WINDOW"),
                    Format = 32,
                    Data1 = 0,
                    Data2 = 2,
                    Data3 = 0,
                    Data4 = 0,
                    Data5 = 0,
                };
                X11Native.XSendEvent(
                    _display, _root, false,
                    X11Native.SubstructureRedirectMask | X11Native.SubstructureNotifyMask,
                    ref message);
                X11Native.XFlush(_display);
            }
            catch { /* ignore */ }
        }
    }

    public void Highlight(ulong id, int durationMs = 2200)
    {
        if (!IsAvailable) return;
        lock (_lock) ShowHighlightUnlocked(id, durationMs);
    }

    private void ShowHighlightUnlocked(ulong id, int durationMs)
    {
        try
        {
            var (x, y) = TranslateToRoot(id);
            if (X11Native.XGetWindowAttributes(_display, (IntPtr)(nuint)id, out var attrs) == 0) return;
            var width = (uint)Math.Max(1, attrs.Width);
            var height = (uint)Math.Max(1, attrs.Height);

            if (_overlay == IntPtr.Zero)
            {
                var set = new X11Native.XSetWindowAttributes
                {
                    BackgroundPixel = 0xFF4F8CFF,
                    OverrideRedirect = 1,
                };
                _overlay = X11Native.XCreateWindow(
                    _display, _root, x, y, width, height, 0,
                    X11Native.CopyFromParent, X11Native.InputOutput, IntPtr.Zero,
                    X11Native.CWBackPixel | X11Native.CWOverrideRedirect, ref set);
                if (_overlay == IntPtr.Zero) return;
                X11Native.XStoreName(_display, _overlay, "TuxViewer Highlight");
            }

            X11Native.XMoveResizeWindow(_display, _overlay, x, y, width, height);
            var thickness = (ushort)Math.Clamp(Math.Min(width, height) / 60, 3, 8);
            var rects = new[]
            {
                new X11Native.XRectangle { X = 0, Y = 0, Width = (ushort)width, Height = thickness },
                new X11Native.XRectangle { X = 0, Y = (short)(height - thickness), Width = (ushort)width, Height = thickness },
                new X11Native.XRectangle { X = 0, Y = 0, Width = thickness, Height = (ushort)height },
                new X11Native.XRectangle { X = (short)(width - thickness), Y = 0, Width = thickness, Height = (ushort)height },
            };
            X11Native.XShapeCombineRectangles(
                _display, _overlay, X11Native.ShapeBounding, 0, 0, rects, rects.Length,
                X11Native.ShapeSet, X11Native.Unsorted);
            X11Native.XMapRaised(_display, _overlay);
            X11Native.XFlush(_display);

            _overlayCts?.Cancel();
            _overlayCts = new CancellationTokenSource();
            var token = _overlayCts.Token;
            var generation = ++_overlayGeneration;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(durationMs, token);
                    lock (_lock)
                    {
                        if (_disposed || generation != _overlayGeneration) return;
                        if (_overlay != IntPtr.Zero) X11Native.XUnmapWindow(_display, _overlay);
                        X11Native.XFlush(_display);
                    }
                }
                catch { /* cancelled */ }
            }, token);
        }
        catch { /* ignore */ }
    }

    public void HideHighlight()
    {
        if (!IsAvailable) return;
        lock (_lock) HideHighlightUnlocked();
    }

    private void HideHighlightUnlocked()
    {
        try
        {
            _overlayCts?.Cancel();
            _overlayGeneration++;
            if (_overlay != IntPtr.Zero) X11Native.XUnmapWindow(_display, _overlay);
            X11Native.XFlush(_display);
        }
        catch { /* ignore */ }
    }

    private List<X11Property> GetProperties(ulong id)
    {
        var result = new List<X11Property>();
        var window = (IntPtr)(nuint)id;
        var propsPtr = X11Native.XListProperties(_display, window, out var count);
        if (propsPtr == IntPtr.Zero)
            return result;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var atom = (IntPtr)(nuint)Marshal.ReadIntPtr(propsPtr, i * IntPtr.Size);
                Dbg($"atom index {i}");
                var namePtr = X11Native.XGetAtomName(_display, atom);
                var name = Marshal.PtrToStringAnsi(namePtr) ?? $"ATOM({atom})";
                Dbg($"prop name {name}");
                if (namePtr != IntPtr.Zero) X11Native.XFree(namePtr);
                try
                {
                    if (name == "_NET_WM_ICON")
                    {
                        result.Add(new X11Property { Name = name, Type = "CARDINAL", Format = 32, Value = "(图标 ARGB 数据，已省略)" });
                        continue;
                    }

                    var bytes = GetPropertyBytes(id, name, out var typeAtom, out var format, out var items);
                    if (bytes is null) continue;
                    var typePtr = typeAtom != IntPtr.Zero ? X11Native.XGetAtomName(_display, typeAtom) : IntPtr.Zero;
                    var typeName = typePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(typePtr) ?? "?" : "?";
                    if (typePtr != IntPtr.Zero) X11Native.XFree(typePtr);
                    Dbg($"prop {name} type={typeName} fmt={format} items={items} bytes={bytes.Length}");
                    result.Add(new X11Property
                    {
                        Name = name,
                        Type = typeName,
                        Format = format,
                        Value = FormatProperty(name, typeName, format, bytes, items),
                    });
                }
                catch { /* skip malformed */ }
            }
        }
        finally
        {
            Dbg("freeing props list");
            X11Native.XFree(propsPtr);
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string FormatProperty(string name, string typeName, int format, byte[] bytes, int items)
    {
        try
        {
            if (name == "_NET_WM_ICON") return "(图标 ARGB 数据，已省略)";
            if (format == 8)
            {
                var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                text = new string(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? '·' : c).ToArray());
                return text.Length > 3000 ? text[..3000] + " …" : text;
            }

            if (format == 32)
            {
                var values = new uint[items];
                Buffer.BlockCopy(bytes, 0, values, 0, Math.Min(bytes.Length, items * 4));
                if (typeName is "ATOM" or "_NET_WM_WINDOW_TYPE")
                    return string.Join(", ", values.Select(v => GetAtomName((IntPtr)v)));
                if (typeName == "WINDOW")
                    return string.Join(", ", values.Select(v => $"0x{v:x}"));
                var ints = values.Select(v => unchecked((int)v)).ToList();
                return string.Join(", ", ints.Take(128)) + (ints.Count > 128 ? $" … ({ints.Count} 项)" : "");
            }

            if (format == 16)
            {
                var values = new ushort[items];
                Buffer.BlockCopy(bytes, 0, values, 0, Math.Min(bytes.Length, items * 2));
                return string.Join(", ", values.Take(128)) + (values.Length > 128 ? $" … ({values.Length} 项)" : "");
            }

            return $"({bytes.Length} bytes)";
        }
        catch
        {
            return $"({bytes.Length} bytes)";
        }
    }

    private string GetAtomName(IntPtr atom)
    {
        if (atom == IntPtr.Zero) return "None";
        var ptr = X11Native.XGetAtomName(_display, atom);
        if (ptr == IntPtr.Zero) return $"ATOM({atom})";
        try { return Marshal.PtrToStringAnsi(ptr) ?? "?"; }
        finally { X11Native.XFree(ptr); }
    }

    private void CollectChildren(ulong id, int depth, List<X11ChildInfo> output, int max)
    {
        if (depth > 5 || output.Count >= max) return;
        var children = GetChildren((IntPtr)(nuint)id);
        foreach (var child in children)
        {
            if (output.Count >= max) return;
            var window = (IntPtr)(nuint)child;
            if (X11Native.XGetWindowAttributes(_display, window, out var attrs) == 0) continue;
            var title = GetTextProperty(child, "_NET_WM_NAME") ?? GetTextProperty(child, "WM_NAME");
            var (_, cls) = GetClassHint(child);
            var (absX, absY) = TranslateToRoot(child);
            output.Add(new X11ChildInfo
            {
                Id = child,
                Depth = depth,
                Title = title,
                ClassName = cls,
                X = absX,
                Y = absY,
                Width = attrs.Width,
                Height = attrs.Height,
                Mapped = attrs.MapState == X11Native.IsViewable,
                OverrideRedirect = attrs.OverrideRedirect != 0,
            });
            CollectChildren(child, depth + 1, output, max);
        }
    }

    private string? GetNormalHints(ulong id)
    {
        try
        {
            if (X11Native.XGetWMNormalHints(_display, (IntPtr)(nuint)id, out var hints, out _) == 0) return null;
            return $"flags=0x{hints.Flags:x} pos=({hints.X},{hints.Y}) size={hints.Width}x{hints.Height} " +
                   $"min={hints.MinWidth}x{hints.MinHeight} max={hints.MaxWidth}x{hints.MaxHeight} " +
                   $"base={hints.BaseWidth}x{hints.BaseHeight} inc={hints.WidthInc}x{hints.HeightInc} gravity={hints.WinGravity}";
        }
        catch
        {
            return null;
        }
    }

    private ulong GetTransientFor(ulong id)
    {
        try
        {
            return X11Native.XGetTransientForHint(_display, (IntPtr)(nuint)id, out var transient) != 0
                ? (ulong)transient
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string? GetProtocols(ulong id)
    {
        try
        {
            if (X11Native.XGetWMProtocols(_display, (IntPtr)(nuint)id, out var ptr, out var count) == 0 || ptr == IntPtr.Zero)
                return null;
            try
            {
                var atoms = new IntPtr[count];
                Marshal.Copy(ptr, atoms, 0, count);
                return string.Join(", ", atoms.Select(GetAtomName));
            }
            finally
            {
                X11Native.XFree(ptr);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeEventMask(nint mask)
    {
        var value = mask.ToInt64();
        var parts = new List<string>();
        void Add(long flag, string name) { if ((value & flag) != 0) parts.Add(name); }
        Add(1L << 0, "KeyPress");
        Add(1L << 1, "KeyRelease");
        Add(1L << 2, "ButtonPress");
        Add(1L << 3, "ButtonRelease");
        Add(1L << 4, "EnterWindow");
        Add(1L << 5, "LeaveWindow");
        Add(1L << 6, "PointerMotion");
        Add(1L << 7, "PointerMotionHint");
        Add(1L << 8, "Button1Motion");
        Add(1L << 9, "Button2Motion");
        Add(1L << 10, "Button3Motion");
        Add(1L << 11, "Button4Motion");
        Add(1L << 12, "Button5Motion");
        Add(1L << 13, "ButtonMotion");
        Add(1L << 14, "KeymapState");
        Add(1L << 15, "Exposure");
        Add(1L << 16, "VisibilityChange");
        Add(1L << 17, "StructureNotify");
        Add(1L << 18, "ResizeRedirect");
        Add(1L << 19, "SubstructureNotify");
        Add(1L << 20, "SubstructureRedirect");
        Add(1L << 21, "FocusChange");
        Add(1L << 22, "PropertyChange");
        Add(1L << 23, "ColormapChange");
        Add(1L << 24, "OwnerGrabButton");
        return parts.Count == 0 ? "0" : string.Join(" | ", parts);
    }

    private (int X, int Y) TranslateToRoot(ulong id)
    {
        try
        {
            if (X11Native.XTranslateCoordinates(
                    _display, (IntPtr)(nuint)id, _root, 0, 0, out var x, out var y, out _) != 0)
                return (x, y);
        }
        catch { /* ignore */ }
        return (0, 0);
    }

    private List<ulong> GetChildren(IntPtr window)
    {
        var result = new List<ulong>();
        if (X11Native.XQueryTree(_display, window, out _, out _, out var children, out var count) == 0)
            return result;
        try
        {
            if (children == IntPtr.Zero || count == 0) return result;
            var ids = new IntPtr[count];
            Marshal.Copy(children, ids, 0, (int)count);
            foreach (var id in ids) result.Add((ulong)id);
        }
        finally
        {
            if (children != IntPtr.Zero) X11Native.XFree(children);
        }
        return result;
    }

    private IntPtr FindDeepestWindow(IntPtr start)
    {
        var current = start;
        for (var i = 0; i < 48; i++)
        {
            if (X11Native.XQueryPointer(
                    _display, current, out _, out var child, out _, out _, out _, out _, out _) == 0)
                break;
            if (child == IntPtr.Zero) break;
            current = child;
        }
        return current;
    }

    private ulong FindToplevel(ulong id)
    {
        var current = id;
        for (var i = 0; i < 64; i++)
        {
            if (X11Native.XQueryTree(_display, (IntPtr)(nuint)current, out _, out var parent, out var children, out _) == 0)
                break;
            if (children != IntPtr.Zero) X11Native.XFree(children);
            if (parent == IntPtr.Zero || parent == _root) return current;
            current = (ulong)parent;
        }
        return current;
    }

    private uint? GetCardinalProperty(ulong id, string name)
    {
        var bytes = GetPropertyBytes(id, name, out _, out var format, out var items);
        if (bytes is null || format != 32 || items < 1 || bytes.Length < 4) return null;
        return BitConverter.ToUInt32(bytes, 0);
    }

    private int? GetPid(ulong id)
    {
        var value = GetCardinalProperty(id, "_NET_WM_PID");
        return value is > 0 and <= int.MaxValue ? (int)value.Value : null;
    }

    private List<ulong> GetWindowListProperty(ulong window, string name)
    {
        var result = new List<ulong>();
        var bytes = GetPropertyBytes(window, name, out _, out var format, out var items);
        if (bytes is null || format != 32 || items == 0) return result;
        for (var i = 0; i < items && i * 4 + 4 <= bytes.Length; i++)
            result.Add(BitConverter.ToUInt32(bytes, i * 4));
        return result;
    }

    private ulong GetWindowPropertyValue(ulong window, string name)
    {
        var bytes = GetPropertyBytes(window, name, out _, out var format, out var items);
        if (bytes is null || format != 32 || items == 0 || bytes.Length < 4) return 0;
        return BitConverter.ToUInt32(bytes, 0);
    }

    private string? JoinAtomProperty(ulong id, string name)
    {
        var bytes = GetPropertyBytes(id, name, out var type, out var format, out var items);
        if (bytes is null || format != 32 || items == 0) return null;
        var values = new List<string>();
        for (var i = 0; i < items && i * 4 + 4 <= bytes.Length; i++)
        {
            var atom = (IntPtr)BitConverter.ToInt32(bytes, i * 4);
            values.Add(GetAtomName(atom));
        }
        return values.Count == 0 ? null : string.Join(", ", values);
    }

    private string? GetTextProperty(ulong id, string name)
    {
        var bytes = GetPropertyBytes(id, name, out _, out var format, out var items);
        if (bytes is null || format != 8 || items == 0) return null;
        var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private (string? Instance, string? Class) GetClassHint(ulong id)
    {
        try
        {
            if (X11Native.XGetClassHint(_display, (IntPtr)(nuint)id, out var hint) == 0) return (null, null);
            try
            {
                var instance = hint.ResName != IntPtr.Zero ? Marshal.PtrToStringAnsi(hint.ResName) : null;
                var cls = hint.ResClass != IntPtr.Zero ? Marshal.PtrToStringAnsi(hint.ResClass) : null;
                return (instance, cls);
            }
            finally
            {
                if (hint.ResName != IntPtr.Zero) X11Native.XFree(hint.ResName);
                if (hint.ResClass != IntPtr.Zero) X11Native.XFree(hint.ResClass);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private byte[]? GetPropertyBytes(ulong id, string name, out IntPtr actualType, out int format, out int items)
    {
        actualType = IntPtr.Zero;
        format = 0;
        items = 0;
        try
        {
            var atom = Atom(name, onlyIfExists: true);
            if (atom == IntPtr.Zero) return null;

            var status = X11Native.XGetWindowProperty(
                _display, (IntPtr)(nuint)id, atom, 0, 262144, false, IntPtr.Zero,
                out var type, out var fmt, out var count, out _, out var prop);

            actualType = type;
            format = fmt;
            items = (int)Math.Min(count, int.MaxValue);
            if (status != 0)
            {
                if (prop != IntPtr.Zero) X11Native.XFree(prop);
                return null;
            }
            if (prop == IntPtr.Zero) return null;
            try
            {
                var byteCount = fmt switch
                {
                    8 => (int)count,
                    16 => checked((int)count * 2),
                    32 => checked((int)count * 4),
                    _ => 0,
                };
                if (byteCount <= 0) return [];
                var buffer = new byte[byteCount];
                Marshal.Copy(prop, buffer, 0, byteCount);
                return buffer;
            }
            finally
            {
                X11Native.XFree(prop);
            }
        }
        catch
        {
            return null;
        }
    }

    private IntPtr Atom(string name, bool onlyIfExists = false)
    {
        lock (_atoms)
        {
            if (_atoms.TryGetValue(name, out var atom) && atom != IntPtr.Zero) return atom;
            atom = X11Native.XInternAtom(_display, name, onlyIfExists);
            if (atom != IntPtr.Zero) _atoms[name] = atom;
            return atom;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            try
            {
                _overlayCts?.Cancel();
                if (_overlay != IntPtr.Zero)
                {
                    X11Native.XDestroyWindow(_display, _overlay);
                    _overlay = IntPtr.Zero;
                }
                if (_crosshair != IntPtr.Zero) X11Native.XFreeCursor(_display, _crosshair);
                if (_display != IntPtr.Zero) X11Native.XCloseDisplay(_display);
            }
            catch { /* ignore */ }
        }
        _overlayCts?.Dispose();
    }
}
