using System.Collections.Concurrent;
using NimPad.Presentation.Platform;

namespace NimPad.Presentation.Tests.Support;

/// <summary>
/// A dedicated "UI thread" with a single-threaded <see cref="SynchronizationContext"/>, like WPF's dispatcher: work
/// posted to it runs one item at a time, in order, and <c>await</c>s inside that work resume on it. Tests run their
/// body with <see cref="RunAsync(Func{Task})"/>, so view models see the same threading as in the App.
/// </summary>
internal sealed class TestUiThread : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Exception> _errors = new();

    public TestUiThread()
    {
        Context = new UiSynchronizationContext(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };
        _thread.Start();
    }

    public SynchronizationContext Context { get; }

    /// <summary>Exceptions thrown by posted work (they would crash the App).</summary>
    public IReadOnlyCollection<Exception> Errors => _errors;

    /// <summary>Items waiting to run.</summary>
    public int PendingCount => _queue.Count;

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Func<Task> action)
    {
        if (CheckAccess())
        {
            return action();
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Runs <paramref name="body"/> on the UI thread and waits for it.</summary>
    public Task RunAsync(Func<Task> body) => InvokeAsync(body);

    /// <summary>Runs <paramref name="body"/> on the UI thread and returns its result.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        var result = default(T)!;
        await InvokeAsync(async () => result = await body());
        return result;
    }

    /// <summary>
    /// From the UI thread: yields until everything posted so far (and whatever that posts) has run.
    /// </summary>
    public async Task DrainAsync()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("Drain from the UI thread.");
        }

        for (var round = 0; round < 10_000; round++)
        {
            var marker = new TaskCompletionSource();
            Post(marker.SetResult);
            await marker.Task;
            if (_queue.Count == 0)
            {
                return;
            }
        }

        throw new TimeoutException("The UI queue never went idle.");
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(Context);
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
            }
        }
    }

    private sealed class UiSynchronizationContext(TestUiThread thread) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => thread.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (thread.CheckAccess())
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            Exception? error = null;
            thread.Post(() =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
            if (error is not null)
            {
                throw error;
            }
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>A dispatcher that runs everything inline on the calling thread (for view models without Core).</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();
}
