using Microsoft.Extensions.Time.Testing;
using NimPad.Core.Domain;
using NimPad.Core.Ids;
using NimPad.Core.Services;
using NimPad.Core.Sync;
using NimPad.Core.Tests.Support;
using NimPad.Core.Text;
using Xunit;

namespace NimPad.Core.Tests.Services;

public sealed class AutosaveSchedulerTests : IDisposable
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _clock = new(TestStore.Start);
    private readonly FakeSaver _saver;
    private readonly AutosaveScheduler _scheduler;
    private readonly NodeId _id = NodeId.New();
    private readonly FakeSource _source = new();

    public AutosaveSchedulerTests()
    {
        _saver = new FakeSaver(_clock);
        _scheduler = new AutosaveScheduler(_saver, _clock);
    }

    public void Dispose() => _scheduler.Dispose();

    // ------------------------------------------------------------ timing

    [Fact]
    public void SavesFourHundredMillisecondsAfterTheLastChange()
    {
        Type("a");
        _clock.Advance(399 * Ms);
        Assert.Empty(_saver.Saves);
        Assert.True(_scheduler.IsDirty(_id));

        _clock.Advance(Ms);

        var save = Assert.Single(_saver.Saves);
        Assert.Equal("a", save.Text);
        Assert.Equal(TestStore.Start.AddMilliseconds(400), save.At);
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public void EachChangeRestartsTheDebounce()
    {
        Type("a");
        _clock.Advance(300 * Ms);
        Type("b");
        _clock.Advance(300 * Ms);
        Assert.Empty(_saver.Saves);

        _clock.Advance(100 * Ms);

        Assert.Equal(["ab"], _saver.Saves.Select(save => save.Text));
        Assert.Equal(TestStore.Start.AddMilliseconds(700), _saver.Saves[0].At);
    }

    [Fact]
    public void ContinuousTypingSavesAtMostOneSecondAfterTheFirstUnsavedChange()
    {
        for (var i = 0; i < 15; i++)
        {
            Type(((char)('a' + i)).ToString());
            _clock.Advance(100 * Ms);
        }

        // First save at 1000 ms (max wait); the next window opens with the change typed at 1000 ms and the last
        // change is at 1400 ms, so the debounce (1800 ms) comes before its max wait (2000 ms).
        Assert.Equal([TestStore.Start.AddMilliseconds(1000)], _saver.Saves.Select(save => save.At));
        Assert.Equal("abcdefghij", _saver.Saves[0].Text);

        _clock.Advance(300 * Ms);

        Assert.Equal(2, _saver.Saves.Count);
        Assert.Equal(TestStore.Start.AddMilliseconds(1800), _saver.Saves[1].At);
        Assert.Equal("abcdefghijklmno", _saver.Saves[1].Text);
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public void TrackedDocumentsStartClean()
    {
        _source.Set("loaded text");
        _scheduler.Track(_id, _source);

        Assert.False(_scheduler.IsDirty(_id));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(_saver.Saves);

        Type("!");
        _clock.Advance(400 * Ms);
        Assert.Equal(["loaded text!"], _saver.Saves.Select(save => save.Text));
    }

    [Fact]
    public async Task TrackingADocumentAgainNeverMarksItsUnsavedChangesAsSaved()
    {
        _source.Set("loaded");
        _scheduler.Track(_id, _source, "sha256:loaded");
        Type(" and edited");

        // The App re-attaches the same editor buffer (a restored tab activated, a document re-bound).
        _scheduler.Track(_id, _source, "sha256:stale");

        Assert.True(_scheduler.IsDirty(_id));
        Assert.Equal("sha256:loaded", _scheduler.GetState(_id)!.BaseChecksum);
        Assert.True(await _scheduler.FlushAllAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["loaded and edited"], _saver.Saves.Select(save => save.Text));
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public async Task EachSaveNamesTheStoredTextItReplaces()
    {
        _source.Set("loaded");
        _scheduler.Track(_id, _source, "sha256:loaded");
        Type(" one");
        _clock.Advance(400 * Ms);
        await SettledAsync();
        Type(" two");
        _clock.Advance(400 * Ms);
        await SettledAsync();

        Assert.Equal(["sha256:loaded", ContentCanonicalizer.Checksum("loaded one")], _saver.ExpectedChecksums);
        Assert.Equal(ContentCanonicalizer.Checksum("loaded one two"), _scheduler.GetState(_id)!.BaseChecksum);
    }

    [Fact]
    public void UntrackedDocumentsAreNotDirty()
    {
        Assert.False(_scheduler.IsDirty(NodeId.New()));
        Assert.Null(_scheduler.GetState(NodeId.New()));
        Assert.Empty(_scheduler.GetDirtyDocuments());
    }

    // ------------------------------------------------------------ dirty tracking and serialization

    [Fact]
    public async Task ChangesMadeDuringASaveKeepTheDocumentDirty()
    {
        var gate = _saver.Block();
        Type("one");
        _clock.Advance(400 * Ms);
        Assert.True(_scheduler.GetState(_id)!.IsSaving);

        Type(" two");
        gate.Release();
        await SettledAsync();

        var state = _scheduler.GetState(_id)!;
        Assert.True(state.IsDirty);
        Assert.Equal(1, state.SavedVersion);
        Assert.Equal(2, state.CurrentVersion);
        Assert.Equal(TestStore.Start.AddMilliseconds(800), state.NextSaveAt);

        _clock.Advance(400 * Ms);
        await SettledAsync();

        Assert.Equal(["one", "one two"], _saver.Saves.Select(save => save.Text));
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public async Task NeverRunsTwoSavesOfOneDocumentAtOnce()
    {
        var first = _saver.Block();
        Type("1");
        _clock.Advance(400 * Ms);
        Type("2");
        _clock.Advance(400 * Ms);
        Type("3");
        var flush = _scheduler.FlushAsync(_id);

        Assert.Equal(1, _saver.Started);
        Assert.False(flush.IsCompleted);

        first.Release();
        await flush;
        await _saver.WhenIdleAsync();

        Assert.Equal(1, _saver.MaxConcurrent);
        Assert.Equal(["1", "12", "123"], _saver.Saves.Select(save => save.Text));
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public async Task ChangesRacingWithTheSnapshotAreNotMarkedSaved()
    {
        // The editor changes the text while the snapshot is taken: the version is read first, so the newer text is
        // saved under the older version and the document stays dirty for the change it has not been told about.
        Type("a");
        _source.OnSnapshot = () =>
        {
            _source.OnSnapshot = null;
            _source.Append("b");
            _scheduler.NotifyChanged(_id, _source);
        };

        await _scheduler.FlushAsync(_id);

        Assert.Equal("ab", _saver.Saves.Single().Text);
        Assert.True(_scheduler.IsDirty(_id));
        _clock.Advance(400 * Ms);
        Assert.Equal(2, _saver.Saves.Count);
        Assert.False(_scheduler.IsDirty(_id));
    }

    // ------------------------------------------------------------ failures

    [Fact]
    public void FailedSavesStayDirtyAndRetryWithBackoff()
    {
        var failures = new List<AutosaveFailedEventArgs>();
        var successes = new List<AutosaveSucceededEventArgs>();
        _scheduler.SaveFailed += (_, e) => failures.Add(e);
        _scheduler.SaveSucceeded += (_, e) => successes.Add(e);
        _saver.FailNext(3);

        Type("x");
        _clock.Advance(400 * Ms);
        Assert.Equal(1, _saver.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(failures).RetryDelay);
        Assert.True(_scheduler.IsDirty(_id));
        Assert.Equal(TestStore.Start.AddMilliseconds(1400), _scheduler.GetState(_id)!.NextSaveAt);

        // Typing while the save is failing does not bring the retry forward.
        _clock.Advance(500 * Ms);
        Type("y");
        _clock.Advance(499 * Ms);
        Assert.Equal(1, _saver.Attempts);

        _clock.Advance(Ms); // 1400 ms: retry 1 fails, next in 2 s
        Assert.Equal(2, _saver.Attempts);
        _clock.Advance(TimeSpan.FromSeconds(2)); // 3400 ms: retry 2 fails, next in 4 s
        Assert.Equal(3, _saver.Attempts);
        _clock.Advance(TimeSpan.FromMilliseconds(3999));
        Assert.Equal(3, _saver.Attempts);
        _clock.Advance(Ms); // 7400 ms: retry 3 succeeds

        Assert.Equal(4, _saver.Attempts);
        Assert.Equal([1, 2, 3], failures.Select(failure => failure.ConsecutiveFailures));
        Assert.Equal([1, 2, 4], failures.Select(failure => (int)failure.RetryDelay.TotalSeconds));
        Assert.All(failures, failure => Assert.IsType<IOException>(failure.Error));
        var success = Assert.Single(successes);
        Assert.Equal("xy", _saver.Saves.Single().Text);
        Assert.Equal(2, success.Version);
        Assert.False(_scheduler.IsDirty(_id));
        Assert.Equal(0, _scheduler.GetState(_id)!.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void RetryDelaysDoubleUpToThirtySeconds(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), AutosaveScheduler.RetryDelayFor(failures));
    }

    [Fact]
    public async Task FlushReportsAFailureAndKeepsRetrying()
    {
        _saver.FailNext(1);
        Type("x");

        await Assert.ThrowsAsync<IOException>(() => _scheduler.FlushAsync(_id));

        Assert.True(_scheduler.IsDirty(_id));
        Assert.Equal(TestStore.Start.AddSeconds(1), _scheduler.GetState(_id)!.NextSaveAt);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["x"], _saver.Saves.Select(save => save.Text));
        Assert.False(_scheduler.IsDirty(_id));
    }

    [Fact]
    public void ASnapshotThatThrowsCountsAsAFailedSave()
    {
        var failures = new List<AutosaveFailedEventArgs>();
        _scheduler.SaveFailed += (_, e) => failures.Add(e);
        Type("x");
        _source.OnSnapshot = () => throw new InvalidOperationException("editor gone");

        _clock.Advance(400 * Ms);

        Assert.IsType<InvalidOperationException>(Assert.Single(failures).Error);
        Assert.True(_scheduler.IsDirty(_id));
        _source.OnSnapshot = null;
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["x"], _saver.Saves.Select(save => save.Text));
    }

    // ------------------------------------------------------------ flush

    [Fact]
    public async Task FlushSavesNowAndSnapshotsOnTheCallingThread()
    {
        Type("urgent");
        var callerThread = Environment.CurrentManagedThreadId;

        await _scheduler.FlushAsync(_id);

        Assert.Equal(["urgent"], _saver.Saves.Select(save => save.Text));
        Assert.Equal(TestStore.Start, _saver.Saves[0].At);
        Assert.Equal(callerThread, _source.LastSnapshotThread);
        Assert.False(_scheduler.IsDirty(_id));

        // Nothing left for the timer.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(_saver.Saves);
    }

    [Fact]
    public async Task FlushWaitsForTheSaveInProgress()
    {
        var gate = _saver.Block();
        Type("x");
        _clock.Advance(400 * Ms);

        var flush = _scheduler.FlushAsync(_id);
        Assert.False(flush.IsCompleted);

        gate.Release();
        await flush;
        Assert.Single(_saver.Saves);
        Assert.False(_scheduler.IsDirty(_id));
        await _scheduler.FlushAsync(NodeId.New());
    }

    [Fact]
    public async Task FlushAllSavesEveryDirtyDocument()
    {
        var sources = Enumerable.Range(0, 3).Select(_ => (Id: NodeId.New(), Source: new FakeSource())).ToList();
        foreach (var (id, source) in sources)
        {
            source.Append(id.ToString());
            _scheduler.NotifyChanged(id, source);
        }

        Assert.Equal(3, _scheduler.GetDirtyDocuments().Count);
        Assert.True(await _scheduler.FlushAllAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(sources.Select(item => item.Id.ToString()).Order(), _saver.Saves.Select(save => save.Text).Order());
        Assert.Empty(_scheduler.GetDirtyDocuments());
    }

    [Fact]
    public async Task FlushAllGivesUpAfterTheTimeout()
    {
        var gate = _saver.Block();
        Type("stuck");

        var flush = _scheduler.FlushAllAsync(TimeSpan.FromSeconds(2));
        Assert.False(flush.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(await flush);
        gate.Release();
        await _saver.WhenIdleAsync();
    }

    [Fact]
    public async Task FlushAllReportsFailuresWithoutThrowing()
    {
        _saver.FailNext(1);
        Type("x");

        Assert.False(await _scheduler.FlushAllAsync(TimeSpan.FromSeconds(5)));
        Assert.True(_scheduler.IsDirty(_id));
    }

    // ------------------------------------------------------------ removal

    [Fact]
    public void RemovedDocumentsAreNoLongerSaved()
    {
        Type("discard me");

        Assert.True(_scheduler.Remove(_id));
        Assert.False(_scheduler.Remove(_id));
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(_saver.Saves);
        Assert.False(_scheduler.IsDirty(_id));
        Assert.Null(_scheduler.GetState(_id));
    }

    [Fact]
    public async Task ADocumentTrackedAgainWaitsForTheSaveFromBeforeItsRemoval()
    {
        var gate = _saver.Block();
        Type("old");
        _clock.Advance(400 * Ms);
        _scheduler.Remove(_id);

        var reopened = new FakeSource();
        reopened.Append("new");
        _scheduler.NotifyChanged(_id, reopened);
        var flush = _scheduler.FlushAsync(_id);
        Assert.Equal(1, _saver.Started);

        gate.Release();
        await flush;
        Assert.Equal(1, _saver.MaxConcurrent);
        Assert.Equal(["old", "new"], _saver.Saves.Select(save => save.Text));
    }

    [Fact]
    public void DisposeStopsTheTimers()
    {
        Type("x");
        _scheduler.Dispose();
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(_saver.Saves);
        Assert.Throws<ObjectDisposedException>(() => _scheduler.NotifyChanged(_id, _source));
    }

    // ------------------------------------------------------------ with the real DocumentService

    [Fact]
    public async Task SavesThroughTheDocumentService()
    {
        using var store = await TestStore.CreateAsync();
        var note = await store.Nodes.CreateDocumentAsync(store.Workspace.Id, null, "typed.md");
        using var scheduler = new AutosaveScheduler(store.Documents, store.Clock);
        var saved = new TaskCompletionSource<AutosaveSucceededEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.SaveSucceeded += (_, e) => saved.TrySetResult(e);
        var source = new FakeSource();
        scheduler.Track(note.Id, source);

        source.Append("# Hello\r\nworld");
        scheduler.NotifyChanged(note.Id, source);
        store.Clock.Advance(TimeSpan.FromMilliseconds(400));
        var result = await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Result.Changed);
        Assert.Equal(1, result.Version);
        Assert.Equal("# Hello\nworld", (await store.Documents.GetContentAsync(note.Id)).Text);
        var create = Assert.Single(await store.Queue.GetForNodeAsync(note.Id));
        Assert.Equal(PendingOperationKind.Create, create.Kind);
        Assert.Equal(ContentCanonicalizer.Checksum("# Hello\nworld"), create.Payload["checksum"]!.GetValue<string>());

        source.Append("!");
        scheduler.NotifyChanged(note.Id, source);
        Assert.True(await scheduler.FlushAllAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("# Hello\nworld!", (await store.Documents.GetContentAsync(note.Id)).Text);
    }

    [Fact]
    public async Task TrackingAgainWithTheRealServiceSavesTheEditsInsteadOfDroppingThem()
    {
        using var store = await TestStore.CreateAsync();
        var note = await store.Nodes.CreateDocumentAsync(store.Workspace.Id, null, "draft.md");
        using var scheduler = new AutosaveScheduler(store.Documents, store.Clock);
        var source = new FakeSource();
        var loaded = await store.Documents.GetContentAsync(note.Id);
        scheduler.Track(note.Id, source, loaded.Checksum);
        source.Append("typed");
        scheduler.NotifyChanged(note.Id, source);

        scheduler.Track(note.Id, source, loaded.Checksum);

        Assert.True(scheduler.IsDirty(note.Id));
        Assert.True(await scheduler.FlushAllAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("typed", (await store.Documents.GetContentAsync(note.Id)).Text);
    }

    [Fact]
    public async Task AChangeMadeElsewhereIsNeverOverwrittenAndHaltsSavingUntilRebased()
    {
        using var store = await TestStore.CreateAsync();
        var note = await store.Nodes.CreateDocumentAsync(store.Workspace.Id, null, "shared.md", initialText: "v1");
        using var scheduler = new AutosaveScheduler(store.Documents, store.Clock);
        var failures = new List<AutosaveFailedEventArgs>();
        scheduler.SaveFailed += (_, e) =>
        {
            lock (failures)
            {
                failures.Add(e);
            }
        };
        var source = new FakeSource();
        source.Set("v1");
        scheduler.Track(note.Id, source, (await store.Documents.GetContentAsync(note.Id)).Checksum);

        // Another writer (in Phase 2: a change pulled from another device) replaces the text, then the user types.
        await store.Documents.SaveContentAsync(note.Id, "changed elsewhere");
        source.Append(" and mine");
        scheduler.NotifyChanged(note.Id, source);
        store.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await WaitUntilAsync(() => scheduler.GetState(note.Id) is { IsHalted: true, IsSaving: false });

        var failure = Assert.Single(failures);
        var conflict = Assert.IsType<DocumentConflictException>(failure.Error);
        Assert.False(failure.WillRetry);
        Assert.Equal(ContentCanonicalizer.Checksum("changed elsewhere"), conflict.StoredChecksum);
        Assert.True(scheduler.IsDirty(note.Id));
        Assert.Null(scheduler.GetState(note.Id)!.NextSaveAt);
        store.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(failures);
        Assert.Equal("changed elsewhere", (await store.Documents.GetContentAsync(note.Id)).Text);
        Assert.False(await scheduler.FlushAllAsync(TimeSpan.FromSeconds(10)));

        // The user keeps their version.
        Assert.True(scheduler.Rebase(note.Id, conflict.StoredChecksum));
        await WaitUntilAsync(() => !scheduler.IsDirty(note.Id));

        Assert.Equal("v1 and mine", (await store.Documents.GetContentAsync(note.Id)).Text);
        Assert.False(scheduler.GetState(note.Id)!.IsHalted);
    }

    [Fact]
    public async Task ANoteDeletedForeverStopsTheRetriesAndStaysDirty()
    {
        using var store = await TestStore.CreateAsync();
        var note = await store.Nodes.CreateDocumentAsync(store.Workspace.Id, null, "doomed.md");
        using var scheduler = new AutosaveScheduler(store.Documents, store.Clock);
        var failures = 0;
        var last = default(AutosaveFailedEventArgs);
        scheduler.SaveFailed += (_, e) =>
        {
            Interlocked.Increment(ref failures);
            last = e;
        };
        var source = new FakeSource();
        scheduler.Track(note.Id, source, (await store.Documents.GetContentAsync(note.Id)).Checksum);
        source.Append("unsaved words");
        scheduler.NotifyChanged(note.Id, source);
        await store.Nodes.DeleteForeverAsync([note.Id]);

        store.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await WaitUntilAsync(() => scheduler.GetState(note.Id) is { IsHalted: true, IsSaving: false });
        for (var i = 0; i < 10; i++)
        {
            store.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.IsType<NodeNotFoundException>(last!.Error);
        Assert.False(last.WillRetry);
        Assert.True(scheduler.IsDirty(note.Id));
        Assert.Equal([note.Id], scheduler.GetDirtyDocuments());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true.");
            await Task.Delay(1);
        }
    }

    // Waits (in real time) until the scheduler has recorded the outcome of every save of the document.
    private async Task SettledAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_scheduler.GetState(_id) is { IsSaving: true })
        {
            Assert.True(DateTime.UtcNow < deadline, "The save did not finish.");
            await Task.Delay(1);
        }
    }

    private void Type(string text)
    {
        _source.Append(text);
        _scheduler.NotifyChanged(_id, _source);
    }

    /// <summary>An editor buffer: a version counter and the text.</summary>
    private sealed class FakeSource : IAutosaveSource
    {
        private readonly Lock _gate = new();
        private string _text = string.Empty;
        private long _version;

        public Action? OnSnapshot { get; set; }

        public int LastSnapshotThread { get; private set; }

        public long Version
        {
            get
            {
                lock (_gate)
                {
                    return _version;
                }
            }
        }

        public void Append(string text)
        {
            lock (_gate)
            {
                _text += text;
                _version++;
            }
        }

        public void Set(string text)
        {
            lock (_gate)
            {
                _text = text;
                _version++;
            }
        }

        public string GetSnapshot()
        {
            LastSnapshotThread = Environment.CurrentManagedThreadId;
            OnSnapshot?.Invoke();
            lock (_gate)
            {
                return _text;
            }
        }
    }

    /// <summary>Records saves; can block them (to observe overlap) or fail them.</summary>
    private sealed class FakeSaver(TimeProvider clock) : IDocumentContentSaver
    {
        private readonly Lock _gate = new();
        private readonly Queue<TaskCompletionSource> _blocks = new();
        private readonly List<Task> _running = [];
        private int _failuresLeft;
        private int _concurrent;

        public List<(string Text, DateTimeOffset At)> Saves { get; } = [];

        public List<string?> ExpectedChecksums { get; } = [];

        public int Attempts { get; private set; }

        public int Started { get; private set; }

        public int MaxConcurrent { get; private set; }

        public Gate Block()
        {
            var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _blocks.Enqueue(block);
            }

            return new Gate(block);
        }

        public void FailNext(int count) => _failuresLeft = count;

        public Task WhenIdleAsync()
        {
            lock (_gate)
            {
                return Task.WhenAll(_running.ToArray());
            }
        }

        public Task<DocumentSaveResult> SaveContentAsync(NodeId nodeId, string text, string? expectedChecksum, CancellationToken cancellationToken = default)
        {
            Task<DocumentSaveResult> task;
            lock (_gate)
            {
                ExpectedChecksums.Add(expectedChecksum);
                Attempts++;
                Started++;
                _concurrent++;
                MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
                var block = _blocks.Count > 0 ? _blocks.Dequeue() : null;
                task = RunAsync(nodeId, text, block?.Task);
                _running.Add(task);
            }

            return task;
        }

        private async Task<DocumentSaveResult> RunAsync(NodeId nodeId, string text, Task? block)
        {
            try
            {
                if (block is not null)
                {
                    await block;
                }

                if (_failuresLeft > 0)
                {
                    _failuresLeft--;
                    throw new IOException("disk full");
                }

                lock (_gate)
                {
                    Saves.Add((text, clock.GetUtcNow()));
                }

                return new DocumentSaveResult(nodeId, true, ContentCanonicalizer.Checksum(text), Saves.Count + 1, clock.GetUtcNow());
            }
            finally
            {
                lock (_gate)
                {
                    _concurrent--;
                }
            }
        }

        public sealed class Gate(TaskCompletionSource block)
        {
            public void Release() => block.TrySetResult();
        }
    }
}
