using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace NimPad.App.Tests.Shell;

/// <summary>
/// Runs an async WPF test on a fresh STA thread with a running dispatcher (awaits resume on it, like in the app) and
/// rethrows its failure on the test thread.
/// </summary>
internal static class UiThread
{
    public const string WindowsOnly = "WPF runs only on Windows.";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public static void Run(Func<Task> test, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Task? running = null;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    running = test();
                }
                catch (Exception ex)
                {
                    running = Task.FromException(ex);
                }

                running.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            }));
            var timer = new DispatcherTimer(
                timeout ?? DefaultTimeout,
                DispatcherPriority.Send,
                (_, _) =>
                {
                    failure ??= new TimeoutException("The UI test did not finish in time.");
                    frame.Continue = false;
                },
                dispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
            if (failure is null && running is { IsFaulted: true, Exception: { } error })
            {
                failure = error.InnerExceptions.Count == 1 ? error.InnerException : error;
            }
            else if (failure is null && running is { IsCanceled: true })
            {
                failure = new TaskCanceledException("The UI test was cancelled.");
            }

            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Lets the dispatcher run until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(20)))
            {
                Assert.Fail("Timed out waiting until " + description + ".");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Lets layout, rendering and every queued UI work item run.</summary>
    public static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}

/// <summary>Finds elements by automation ID in the visual tree and in the UI Automation tree (through the peers).</summary>
internal static class Find
{
    /// <summary>Every element of the visual tree under <paramref name="root"/> with <paramref name="automationId"/>.</summary>
    public static List<FrameworkElement> AllById(DependencyObject root, string automationId)
    {
        var found = new List<FrameworkElement>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement element && AutomationProperties.GetAutomationId(element) == automationId)
            {
                found.Add(element);
            }

            var count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
            for (var i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>The single element with <paramref name="automationId"/> (fails when missing).</summary>
    public static T ById<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
    {
        var found = AllById(root, automationId).OfType<T>().ToList();
        Assert.True(found.Count > 0, $"No {typeof(T).Name} with AutomationId {automationId}.");
        return found[0];
    }

    /// <summary>The UI Automation tree under <paramref name="root"/>, as WPF exposes it to screen readers and UI tests.</summary>
    public static List<AutomationPeer> Peers(UIElement root)
    {
        var all = new List<AutomationPeer>();
        var peer = UIElementAutomationPeer.CreatePeerForElement(root);
        if (peer is not null)
        {
            Walk(peer);
        }

        return all;

        void Walk(AutomationPeer node)
        {
            all.Add(node);
            foreach (var child in node.GetChildren() ?? [])
            {
                Walk(child);
            }
        }
    }

    /// <summary>The automation peer with <paramref name="automationId"/> (fails when the tree has none).</summary>
    public static AutomationPeer Peer(UIElement root, string automationId)
    {
        var peer = Peers(root).FirstOrDefault(candidate => candidate.GetAutomationId() == automationId);
        Assert.True(peer is not null, $"The automation tree has no element with AutomationId {automationId}.");
        return peer!;
    }
}

/// <summary>A temporary data folder for one test.</summary>
internal sealed class TempDataDir : IDisposable
{
    public TempDataDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nimpad-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // SQLite may release its files a moment after the database closed.
                Thread.Sleep(100);
            }
        }
    }
}
