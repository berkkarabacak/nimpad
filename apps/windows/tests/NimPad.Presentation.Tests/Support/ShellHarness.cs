using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Storage;
using NimPad.Presentation.Shell;
using NimPad.Presentation.Tabs;
using Xunit;

namespace NimPad.Presentation.Tests.Support;

/// <summary>
/// A real <see cref="ShellViewModel"/> over real Core services in a temp data directory, with a fake clock, a test
/// UI thread and fake platform services. Run test bodies on the UI thread with <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// <para>The fake clock is advanced from a thread-pool thread, as <c>TimeProvider.System</c> fires timers in the App,
/// and <see cref="UiThreadGuard"/> records every view-model change raised off the UI thread (a WPF binding would
/// throw); disposing the harness fails the test when there was one.</para>
/// </remarks>
internal sealed class ShellHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 13, 59, 43, 123, TimeSpan.Zero);

    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    private readonly bool _ownsDirectory;
    private readonly bool _ownsUi;
    private readonly ConcurrentDictionary<NodeId, int> _reportedFailures = new();
    private CoreServices? _observedCore;

    private ShellHarness(TempDirectory directory, bool ownsDirectory, FakeTimeProvider clock, TestUiThread ui, bool ownsUi, FakePlatform platform)
    {
        Directory = directory;
        _ownsDirectory = ownsDirectory;
        Clock = clock;
        Ui = ui;
        _ownsUi = ownsUi;
        Platform = platform;
        Guard = new UiThreadGuard(ui);
        Shell = new ShellViewModel(platform.Services, clock, Culture);
        Guard.Watch(Shell);
    }

    /// <summary>Records view-model changes raised off the UI thread.</summary>
    public UiThreadGuard Guard { get; }

    public TempDirectory Directory { get; }

    public FakeTimeProvider Clock { get; }

    public TestUiThread Ui { get; }

    public FakePlatform Platform { get; }

    public ShellViewModel Shell { get; private set; }

    public CoreServices Core => Shell.Core;

    public TabsViewModel Tabs => Shell.Tabs;

    public StartupOptions Options => new() { DataDirectory = Directory.Path };

    /// <summary>Creates the harness and initializes the shell (on the UI thread).</summary>
    public static async Task<ShellHarness> StartAsync(
        TempDirectory? directory = null,
        FakeTimeProvider? clock = null,
        IReadOnlyList<string>? files = null,
        Func<ShellHarness, Task>? beforeInitialize = null)
    {
        var ownsDirectory = directory is null;
        directory ??= new TempDirectory();
        clock ??= new FakeTimeProvider(Start);
        var ui = new TestUiThread();
        var harness = await ui.RunAsync(() => Task.FromResult(new ShellHarness(directory, ownsDirectory, clock, ui, true, new FakePlatform(ui))));
        if (beforeInitialize is not null)
        {
            await beforeInitialize(harness);
        }

        await ui.RunAsync(async () =>
        {
            await harness.Shell.InitializeAsync(harness.Options with { FilesToOpen = files ?? [] });
            harness.ObserveAutosave();
            await harness.SettleAsync();
        });
        return harness;
    }

    // Follows autosave failures after the tabs do (handlers run in subscription order), so SettleAsync can tell a
    // failure the tabs have not heard about yet.
    private void ObserveAutosave()
    {
        if (!Shell.Context.IsReady || ReferenceEquals(_observedCore, Core))
        {
            return;
        }

        _observedCore = Core;
        _reportedFailures.Clear();
        Core.Autosave.SaveFailed += (_, e) => _reportedFailures[e.NodeId] = e.ConsecutiveFailures;
    }

    /// <summary>The shared settings store.</summary>
    public NimPad.Presentation.Settings.SettingsStore SettingsStore() => Shell.SettingsStore;

    /// <summary>Runs a test body on the UI thread.</summary>
    public Task RunAsync(Func<Task> body) => Ui.RunAsync(body);

    /// <summary>
    /// From the UI thread: waits until Core notifications, posted UI work, background view-model work and autosaves
    /// (including the tabs hearing about their outcome) have all finished.
    /// </summary>
    public async Task SettleAsync()
    {
        var deadline = Stopwatch.StartNew();
        var quietRounds = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (Shell.IsInitialized || Shell.Context.IsReady)
            {
                await Core.Notifier.WhenDeliveredAsync();
            }

            await Ui.DrainAsync();
            await Shell.Context.Model.WhenAppliedAsync();
            await Shell.BackgroundWork.WhenIdleAsync(TimeSpan.FromSeconds(10));
            await Ui.DrainAsync();
            if (await AutosavesInFlightAsync())
            {
                quietRounds = 0;
                await Task.Delay(5);
                continue;
            }

            if (Ui.PendingCount == 0 && Shell.BackgroundWork.RunningCount == 0 && !Shell.Context.Model.IsProcessing)
            {
                // Two quiet rounds in a row: a delivered notification or a finished save may have queued new work.
                if (!Shell.Context.IsReady || ++quietRounds == 2)
                {
                    return;
                }
            }
            else
            {
                quietRounds = 0;
            }
        }

        throw new TimeoutException("The shell never settled.");
    }

    // True while a save of an open note runs, or its outcome has not reached the tabs yet. A note that is clean apart
    // from a finished save is awaited through FlushAsync, which returns the in-flight save and completes only after
    // its SaveSucceeded / SaveFailed handlers ran (it starts no new save for a clean note).
    private async Task<bool> AutosavesInFlightAsync()
    {
        if (!Shell.Context.IsReady)
        {
            return false;
        }

        var busy = false;
        foreach (var tab in Shell.Tabs.Tabs.ToList())
        {
            if (tab.NodeId is not { } id || Core.Autosave.GetState(id) is not { } state)
            {
                continue;
            }

            if (state.IsSaving)
            {
                busy = true;
            }
            else if (!state.IsDirty)
            {
                try
                {
                    await Core.Autosave.FlushAsync(id);
                }
                catch (Exception)
                {
                    // A failed save; the tab hears about it through SaveFailed.
                }
            }
            else if (state.ConsecutiveFailures > 0 && _reportedFailures.GetValueOrDefault(id) != state.ConsecutiveFailures)
            {
                busy = true;
            }
        }

        return busy;
    }

    /// <summary>
    /// From the UI thread: advances the fake clock on a thread-pool thread (timers fire there, as in the App) and
    /// settles.
    /// </summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await Task.Run(() => Clock.Advance(by));
        await SettleAsync();
    }

    /// <summary>From the UI thread: shuts the shell down and starts a new one on the same data directory.</summary>
    public async Task RestartAsync(IReadOnlyList<string>? files = null)
    {
        await Shell.ShutdownAsync();
        Shell = new ShellViewModel(Platform.Services, Clock, Culture);
        Guard.Watch(Shell);
        await Shell.InitializeAsync(Options with { FilesToOpen = files ?? [] });
        ObserveAutosave();
        await SettleAsync();
    }

    /// <summary>Runs <paramref name="action"/> against a separate Core over the same data (the shell must be shut down).</summary>
    public async Task<T> RunElsewhereAsync<T>(Func<CoreServices, Task<T>> action)
    {
        var paths = AppPaths.ResolveForCurrentProcess(Directory.Path);
        using var database = LocalDatabase.Open(paths, Clock);
        using var core = CoreServices.Create(database, Clock);
        var result = await action(core);
        await core.Notifier.WhenDeliveredAsync();
        return result;
    }

    /// <summary>Opens a managed tab and attaches a fake buffer with its loaded text (what the App does).</summary>
    public async Task<FakeEditorBuffer> ShowAsync(TabViewModel tab)
    {
        if (tab.Buffer is FakeEditorBuffer existing)
        {
            return existing;
        }

        var text = await tab.LoadAsync();
        var buffer = new FakeEditorBuffer(text);
        tab.AttachBuffer(buffer);
        return buffer;
    }

    /// <summary>Types into a tab's buffer like the App does (edit, then OnBufferChanged).</summary>
    public static void Type(TabViewModel tab, string text)
    {
        var buffer = (FakeEditorBuffer)tab.Buffer!;
        buffer.Append(text);
        tab.OnBufferChanged();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Ui.RunAsync(() => Shell.ShutdownAsync());
        }
        finally
        {
            var errors = Ui.Errors.ToList();
            var offThread = Guard.Violations.ToList();
            if (_ownsUi)
            {
                Ui.Dispose();
            }

            if (_ownsDirectory)
            {
                Directory.Dispose();
            }

            Assert.Empty(errors);
            Assert.Empty(offThread);
        }
    }
}
