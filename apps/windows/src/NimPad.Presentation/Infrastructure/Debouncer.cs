using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Infrastructure;

/// <summary>
/// Runs an action on the UI thread once a quiet period has passed since the last <see cref="Schedule"/>. Timing comes
/// from the <see cref="TimeProvider"/>, so tests drive it with a fake clock. Use it from the UI thread.
/// </summary>
internal sealed class Debouncer : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _action;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long _generation;
    private bool _disposed;

    /// <summary>Creates the debouncer; nothing runs until <see cref="Schedule"/>.</summary>
    public Debouncer(TimeProvider clock, IUiDispatcher dispatcher, TimeSpan delay, Action action)
    {
        _clock = clock;
        _dispatcher = dispatcher;
        Delay = delay;
        _action = action;
    }

    /// <summary>The quiet period.</summary>
    public TimeSpan Delay { get; }

    /// <summary>True while a run is scheduled.</summary>
    public bool IsPending { get; private set; }

    /// <summary>(Re)starts the quiet period.</summary>
    public void Schedule()
    {
        ITimer timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _generation++;
            IsPending = true;
            timer = _timer ??= _clock.CreateTimer(static state => ((Debouncer)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // Outside the lock: a fake clock fires due timers synchronously from Change.
        timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Drops the scheduled run, if any.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _generation++;
            IsPending = false;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs the action now (on the calling thread) when a run is scheduled.</summary>
    /// <returns><see langword="true"/> when the action ran.</returns>
    public bool Flush()
    {
        lock (_gate)
        {
            if (!IsPending)
            {
                return false;
            }
        }

        Cancel();
        _action();
        return true;
    }

    /// <summary>Drops a scheduled run and stops the timer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            IsPending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || !IsPending)
            {
                return;
            }

            generation = _generation;
        }

        _dispatcher.Post(() =>
        {
            lock (_gate)
            {
                if (_disposed || !IsPending || generation != _generation)
                {
                    return;
                }

                IsPending = false;
            }

            _action();
        });
    }
}
