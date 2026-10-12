using Microsoft.Extensions.Time.Testing;
using NimPad.Presentation.Feedback;
using NimPad.Presentation.Infrastructure;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Tests.Support;
using Xunit;

namespace NimPad.Presentation.Tests.Feedback;

/// <summary>
/// Timer callbacks (thread-pool threads in the App) reach the view models only through the dispatcher: nothing changes
/// until the queued work runs.
/// </summary>
public sealed class DispatcherMarshalingTests
{
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public int Pending => _queue.Count;

        public bool CheckAccess() => false;

        public void Post(Action action) => _queue.Enqueue(action);

        public Task InvokeAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource();
            Post(() => action().ContinueWith(_ => completion.SetResult(), TaskScheduler.Default));
            return completion.Task;
        }

        public void RunAll()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Fact]
    public void AToastTimesOutThroughTheDispatcher()
    {
        var clock = new FakeTimeProvider(ShellHarness.Start);
        var dispatcher = new QueuedDispatcher();
        var toasts = new ToastHostViewModel(dispatcher, clock);
        var toast = toasts.Show("Link copied");

        clock.Advance(ToastHostViewModel.Lifetime);

        Assert.Contains(toast, toasts.Toasts);
        Assert.Equal(1, dispatcher.Pending);

        dispatcher.RunAll();
        Assert.Empty(toasts.Toasts);
        Assert.True(toast.IsDismissed);
    }

    [Fact]
    public void ADebouncedActionRunsThroughTheDispatcher()
    {
        var clock = new FakeTimeProvider(ShellHarness.Start);
        var dispatcher = new QueuedDispatcher();
        var runs = 0;
        using var debouncer = new Debouncer(clock, dispatcher, TimeSpan.FromMilliseconds(100), () => runs++);

        debouncer.Schedule();
        clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, runs);
        Assert.Equal(1, dispatcher.Pending);

        dispatcher.RunAll();
        Assert.Equal(1, runs);

        // Scheduled again before the queued run executes: only the newest schedule runs.
        debouncer.Schedule();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        debouncer.Schedule();
        dispatcher.RunAll();
        Assert.Equal(1, runs);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        dispatcher.RunAll();
        Assert.Equal(2, runs);
    }
}
