using TuxViewer.Models;

namespace TuxViewer.Services.Providers;

public interface IWindowProvider
{
    string Id { get; }
    string Name { get; }
    bool IsAvailable { get; }
    string Detail { get; }
    PickCapability Pick { get; }
    bool CanList { get; }
    bool CanFocus { get; }
    bool CanClose { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken);

    /// <summary>Compositor-native interactive pick (crosshair).</summary>
    Task<WindowInfo?> PickInteractiveAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Pick the window under the pointer, called after a countdown.</summary>
    Task<WindowInfo?> PickAtCursorAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken);

    Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken);

    Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken);
    Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken);
}

public abstract class WindowProviderBase : IWindowProvider
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract bool IsAvailable { get; }
    public abstract string Detail { get; }
    public virtual PickCapability Pick => PickCapability.None;
    public virtual bool CanList => true;
    public virtual bool CanFocus => false;
    public virtual bool CanClose => false;

    public virtual Task InitializeAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public abstract Task<IReadOnlyList<WindowInfo>> GetWindowsAsync(CancellationToken cancellationToken);

    public virtual Task<WindowInfo?> PickInteractiveAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
        => Task.FromResult<WindowInfo?>(null);

    public virtual Task<WindowInfo?> PickAtCursorAsync(IProgress<PickProgress>? progress, CancellationToken cancellationToken)
        => Task.FromResult<WindowInfo?>(null);

    public virtual Task<WindowInfo?> GetFocusedWindowAsync(CancellationToken cancellationToken)
        => Task.FromResult<WindowInfo?>(null);

    public virtual Task<bool> FocusAsync(WindowInfo window, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public virtual Task<bool> CloseAsync(WindowInfo window, CancellationToken cancellationToken)
        => Task.FromResult(false);

}

public static class WindowProviderExtensions
{
    public static ProviderStatus ToStatus(this IWindowProvider provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        Available = provider.IsAvailable,
        Detail = provider.Detail,
        Pick = provider.Pick,
        CanFocus = provider.CanFocus,
        CanClose = provider.CanClose,
        CanList = provider.CanList,
    };
}
