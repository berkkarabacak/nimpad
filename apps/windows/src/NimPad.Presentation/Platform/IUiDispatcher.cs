namespace NimPad.Presentation.Platform;

/// <summary>
/// The UI thread. Every view-model state change happens on it; Core events (raised on background threads) and timer
/// callbacks are marshaled through it. The App implements it over WPF's <c>Dispatcher</c>.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the calling thread is the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> to run on the UI thread and returns immediately.</summary>
    void Post(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread (inline when already on it) and completes when the task it
    /// returns completes.
    /// </summary>
    Task InvokeAsync(Func<Task> action);
}

/// <summary>Helpers over <see cref="IUiDispatcher"/>.</summary>
public static class UiDispatcherExtensions
{
    /// <summary>Runs <paramref name="action"/> inline when on the UI thread, otherwise posts it.</summary>
    public static void Run(this IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action);
        }
    }
}
