using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

public sealed class X11WindowProvider(X11Inspector inspector, bool isWaylandSession) : WindowProviderBase
{
    public X11Inspector Inspector { get; } = inspector;

    public override string Id => "x11";
    public override string Name => "X11 / XWayland";
    public override bool IsAvailable => Inspector.IsAvailable;
    public override string Detail => IsAvailable
        ? $"{Inspector.DisplayName} · {(isWaylandSession ? "XWayland" : "原生 X11")} · {Inspector.GetWmName() ?? "WM 未知"}"
        : Inspector.Error ?? "X11 不可用";
    public override PickCapability Pick => PickCapability.Interactive;
    public override bool CanFocus => true;
    public override bool CanClose => true;

    public override Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Inspector.GetWindows();
        }, cancellationToken);

    public override async Task<WindowInfo?> PickInteractiveAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
    {
        var id = await Task.Run(() => Inspector.PickInteractive(progress, cancellationToken), cancellationToken);
        if (id is null) return null;

        var windows = await Task.Run(Inspector.GetWindows, cancellationToken);
        var match = windows.FirstOrDefault(w => w.X11WindowId == id);
        if (match is not null) return match;

        var details = await Task.Run(() => Inspector.GetDetails(id.Value), cancellationToken);
        if (details is null) return null;
        return new WindowInfo
        {
            Id = $"x11:0x{id:x}",
            SourceId = "x11",
            SourceName = "X11",
            NativeId = $"0x{id:x}",
            Title = string.IsNullOrWhiteSpace(details.Title) ? "(无标题)" : details.Title!,
            AppId = details.ClassName,
            WmClass = details.ClassName,
            Backend = WindowBackend.X11,
            IsXWayland = isWaylandSession,
            IsMapped = details.Mapped,
            X = details.X,
            Y = details.Y,
            Width = details.Width,
            Height = details.Height,
            X11WindowId = id,
            RawJson = details.RawTree,
        };
    }

    public override Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Inspector.GetActiveWindow();
        }, cancellationToken);

    public override Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (window.X11WindowId is null) return false;
            Inspector.Focus(window.X11WindowId.Value);
            return true;
        }, cancellationToken);

    public override Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            if (window.X11WindowId is null) return false;
            Inspector.Close(window.X11WindowId.Value);
            return true;
        }, cancellationToken);
}
