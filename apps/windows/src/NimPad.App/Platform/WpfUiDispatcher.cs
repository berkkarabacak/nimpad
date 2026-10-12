using System.Windows.Threading;
using NimPad.Presentation.Platform;

namespace NimPad.App.Platform;

/// <summary>
/// <see cref="IUiDispatcher"/> over WPF's <see cref="Dispatcher"/>: <see cref="Post"/> queues at normal priority
/// (<c>BeginInvoke</c>), <see cref="InvokeAsync"/> runs inline on the UI thread and otherwise queues and unwraps.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Wraps <paramref name="dispatcher"/> (the UI thread's).</summary>
    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <summary>The wrapped dispatcher.</summary>
    public Dispatcher Dispatcher => _dispatcher;

    /// <inheritdoc/>
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <inheritdoc/>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.HasShutdownStarted)
        {
            // The app is closing; nothing on the UI thread will run any more.
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    /// <inheritdoc/>
    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            return action();
        }

        if (_dispatcher.HasShutdownStarted)
        {
            return Task.FromCanceled(new CancellationToken(canceled: true));
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.Unwrap();
    }
}
