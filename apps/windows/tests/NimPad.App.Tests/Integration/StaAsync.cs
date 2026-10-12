using System.Diagnostics;
using System.Windows.Threading;
using NimPad.App.Tests.Themes;

namespace NimPad.App.Tests.Integration;

/// <summary>
/// Runs an async test body on an STA thread (<see cref="Sta.Run"/>) whose WPF dispatcher pumps until the body
/// completes, with a <see cref="DispatcherSynchronizationContext"/> so awaits resume on that thread, as in the App.
/// </summary>
internal static class StaAsync
{
    public static void Run(Func<Task> body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        Sta.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = body();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            using var watchdog = new Timer(_ => frame.Continue = false, null, timeout ?? TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            Dispatcher.PushFrame(frame);
            if (!task.IsCompleted)
            {
                throw new TimeoutException("The test body did not finish in time.");
            }

            task.GetAwaiter().GetResult();
        });
    }

    /// <summary>From the STA thread: keeps the dispatcher running until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (clock.Elapsed > limit)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(20);
        }
    }
}
