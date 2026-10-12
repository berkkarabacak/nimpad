using System.Diagnostics;

namespace NimPad.Presentation.Infrastructure;

/// <summary>
/// Keeps track of fire-and-forget work started by the view models (debounced saves, background refreshes) so that
/// shutdown, and tests, can wait until it has finished. Failures are logged, never rethrown.
/// </summary>
public sealed class BackgroundWork
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _running = [];

    /// <summary>Raised (on the thread that observed it) when tracked work failed.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>How many tracked tasks are still running.</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>Tracks <paramref name="task"/> until it completes.</summary>
    /// <param name="task">The work.</param>
    /// <param name="description">What it does, for the log.</param>
    public void Track(Task task, string description)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
        {
            Observe(task, description);
            return;
        }

        lock (_gate)
        {
            _running.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _running.Remove(completed);
                }

                Observe(completed, description);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Completes when no tracked work is running (including work started while waiting).</summary>
    /// <param name="timeout">Stop waiting after this long (real time); <see langword="null"/> waits indefinitely.</param>
    /// <returns><see langword="true"/> when everything finished.</returns>
    public async Task<bool> WhenIdleAsync(TimeSpan? timeout = null)
    {
        var deadline = timeout is { } limit ? Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                return true;
            }

            var remaining = deadline == long.MaxValue
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(Math.Max(0, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
            if (remaining == TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await Task.WhenAll(running).WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception)
            {
                // Logged by Observe; keep waiting for the rest.
            }
        }
    }

    private void Observe(Task task, string description)
    {
        if (task.IsFaulted && task.Exception is { } exception)
        {
            var error = exception.InnerExceptions.Count == 1 ? exception.InnerException! : exception;
            Trace.TraceError($"Background work failed ({description}): {error}");
            Failed?.Invoke(this, error);
        }
    }
}
