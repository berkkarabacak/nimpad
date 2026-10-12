using System.Diagnostics;
using NimPad.Core.Domain;
using NimPad.Core.Ids;

namespace NimPad.Core.Services;

/// <summary>
/// The live text of an open document, as the editor sees it. Implemented by the App over AvalonEdit's
/// <c>TextDocument</c>.
/// </summary>
public interface IAutosaveSource
{
    /// <summary>
    /// A counter that increases with every change of the text (never decreases). Must be safe to read from any
    /// thread.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// The current text. Called from any thread (a timer thread, or the thread that calls
    /// <see cref="AutosaveScheduler.FlushAsync"/>); the App marshals to the UI thread when it is not already on it.
    /// The scheduler reads <see cref="Version"/> before calling this, so a change racing with the snapshot can only
    /// make the saved text newer than the version recorded as saved, never older.
    /// </summary>
    string GetSnapshot();
}

/// <summary>Raised after an autosave committed.</summary>
public sealed class AutosaveSucceededEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    public AutosaveSucceededEventArgs(NodeId nodeId, long version, DocumentSaveResult result)
    {
        NodeId = nodeId;
        Version = version;
        Result = result;
    }

    /// <summary>The document.</summary>
    public NodeId NodeId { get; }

    /// <summary>The source version that is now durable.</summary>
    public long Version { get; }

    /// <summary>What the save did (it may have been a no-op because the text matched).</summary>
    public DocumentSaveResult Result { get; }
}

/// <summary>
/// Raised after an autosave failed. The document stays dirty; it is retried after <see cref="RetryDelay"/> unless the
/// failure is one a retry cannot fix (<see cref="WillRetry"/> is <see langword="false"/>).
/// </summary>
public sealed class AutosaveFailedEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    public AutosaveFailedEventArgs(NodeId nodeId, Exception error, int consecutiveFailures, TimeSpan retryDelay, bool willRetry = true)
    {
        NodeId = nodeId;
        Error = error;
        ConsecutiveFailures = consecutiveFailures;
        RetryDelay = retryDelay;
        WillRetry = willRetry;
    }

    /// <summary>The document.</summary>
    public NodeId NodeId { get; }

    /// <summary>Why the save failed.</summary>
    public Exception Error { get; }

    /// <summary>How many saves of this document have failed in a row (1 for the first failure).</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>When the next automatic attempt happens, relative to the failure (zero when <see cref="WillRetry"/> is false).</summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>
    /// <see langword="false"/> when saving stopped until the App acts: the note was changed elsewhere
    /// (<see cref="DocumentConflictException"/>; resolve it, then call <see cref="AutosaveScheduler.Rebase"/>) or it no
    /// longer exists (<see cref="NodeNotFoundException"/>, for example it was deleted forever; offer to keep the text
    /// elsewhere, then <see cref="AutosaveScheduler.Remove"/> it).
    /// </summary>
    public bool WillRetry { get; }
}

/// <summary>A snapshot of one tracked document's autosave state.</summary>
/// <param name="NodeId">The document.</param>
/// <param name="CurrentVersion">The newest source version reported through <see cref="AutosaveScheduler.NotifyChanged"/>.</param>
/// <param name="SavedVersion">The newest source version known to be durable, or <see langword="null"/> before the first save.</param>
/// <param name="IsSaving">Whether a save of the document is in progress.</param>
/// <param name="ConsecutiveFailures">How many saves have failed in a row (0 after a success).</param>
/// <param name="NextSaveAt">When the next automatic save is due, or <see langword="null"/> when none is scheduled.</param>
/// <param name="BaseChecksum">
/// The checksum of the stored text the editor's text is based on (what the next save expects to replace), or
/// <see langword="null"/> when unknown.
/// </param>
/// <param name="IsHalted">
/// Whether automatic saving stopped after a failure a retry cannot fix (see <see cref="AutosaveFailedEventArgs.WillRetry"/>).
/// </param>
public sealed record AutosaveState(
    NodeId NodeId,
    long CurrentVersion,
    long? SavedVersion,
    bool IsSaving,
    int ConsecutiveFailures,
    DateTimeOffset? NextSaveAt,
    string? BaseChecksum = null,
    bool IsHalted = false)
{
    /// <summary>True when the document has changes that are not durable yet.</summary>
    public bool IsDirty => SavedVersion is not { } saved || CurrentVersion > saved;
}

/// <summary>
/// Saves open documents automatically (architecture.md, "Local-first data flow"): 400 ms after the last change, and
/// at most 1000 ms after the first unsaved change while the user keeps typing.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dirty state is tracked by source version, not a flag: a document is dirty while the newest reported version
/// is greater than the newest saved one, so changes made while a save is running keep it dirty.</item>
/// <item>Saves of the same document never overlap; different documents may save concurrently (the single writer
/// still orders the transactions).</item>
/// <item>A failed save keeps the document dirty and is retried after 1 s, 2 s, 4 s, … up to 30 s; while it is
/// failing, typing does not bring the next attempt forward. <see cref="FlushAsync"/> always tries immediately.</item>
/// <item>Each save names the stored text it replaces (the checksum the editor loaded, then the one its last save
/// stored), so a change made elsewhere in the meantime is never overwritten: the save fails with
/// <see cref="DocumentConflictException"/>. That failure, and a note that no longer exists, stop automatic saving
/// (no retry could succeed) and keep the document dirty until the App resolves it.</item>
/// <item>All timing comes from the <see cref="TimeProvider"/>, so tests drive it with a fake clock. Every member is
/// thread-safe. Events are raised on background threads.</item>
/// </list>
/// </remarks>
public sealed class AutosaveScheduler : IDisposable
{
    /// <summary>The quiet period after the last change before a save starts.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>The longest a change waits for a save while the user keeps typing.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMilliseconds(1000);

    /// <summary>The delay before the first retry of a failed save; it doubles with each further failure.</summary>
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest delay between retries of a failing save.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IDocumentContentSaver _saver;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<NodeId, Entry> _entries = [];
    private bool _disposed;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="saver">Saves the text, normally the <see cref="DocumentService"/>.</param>
    /// <param name="timeProvider">The clock driving the timers; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="debounce">The quiet period after the last change; <see cref="DefaultDebounce"/> by default.</param>
    /// <param name="maxWait">The longest wait after the first unsaved change; <see cref="DefaultMaxWait"/> by default.</param>
    public AutosaveScheduler(IDocumentContentSaver saver, TimeProvider? timeProvider = null, TimeSpan? debounce = null, TimeSpan? maxWait = null)
    {
        ArgumentNullException.ThrowIfNull(saver);
        _saver = saver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Debounce = debounce ?? DefaultDebounce;
        MaxWait = maxWait ?? DefaultMaxWait;
        ArgumentOutOfRangeException.ThrowIfLessThan(Debounce, TimeSpan.Zero, nameof(debounce));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWait, Debounce, nameof(maxWait));
    }

    /// <summary>The quiet period after the last change before a save starts.</summary>
    public TimeSpan Debounce { get; }

    /// <summary>The longest a change waits for a save while the user keeps typing.</summary>
    public TimeSpan MaxWait { get; }

    /// <summary>Raised after a save committed (on a background thread).</summary>
    public event EventHandler<AutosaveSucceededEventArgs>? SaveSucceeded;

    /// <summary>Raised after a save failed (on a background thread); the document stays dirty and is retried.</summary>
    public event EventHandler<AutosaveFailedEventArgs>? SaveFailed;

    /// <summary>
    /// Starts tracking a document whose text was just loaded from the store: its current version counts as saved, and
    /// <paramref name="baseChecksum"/> is the stored text it is based on. Optional; <see cref="NotifyChanged"/> tracks
    /// unknown documents too (treating them as dirty, with no base until their first save).
    /// </summary>
    /// <remarks>
    /// Tracking a document that is already tracked only replaces its source: its unsaved changes stay unsaved and its
    /// base stays the one its last save established (a stale <paramref name="baseChecksum"/> is ignored). To start
    /// over after reloading the note, <see cref="Remove"/> it first.
    /// </remarks>
    /// <param name="nodeId">The document.</param>
    /// <param name="source">The live text.</param>
    /// <param name="baseChecksum">The <see cref="DocumentContent.Checksum"/> of the loaded text, or <see langword="null"/> to save unconditionally until the first save.</param>
    public void Track(NodeId nodeId, IAutosaveSource source, string? baseChecksum = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var version = source.Version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var isNew = !_entries.TryGetValue(nodeId, out var existing) || existing.Removed;
            var entry = GetOrCreateLocked(nodeId);
            entry.Source = source;
            entry.CurrentVersion = Math.Max(entry.CurrentVersion, version);
            if (isNew)
            {
                entry.SavedVersion = version;
                entry.BaseChecksum = baseChecksum;
            }

            ScheduleLocked(entry);
        }
    }

    /// <summary>
    /// Resolves a halted document by keeping the editor's text (after a <see cref="DocumentConflictException"/>, once
    /// the user chose their version): later saves replace the stored text with checksum
    /// <paramref name="baseChecksum"/> (normally <see cref="DocumentConflictException.StoredChecksum"/>), automatic
    /// saving resumes and a dirty document is saved right away.
    /// </summary>
    /// <returns><see langword="true"/> when the document is tracked.</returns>
    public bool Rebase(NodeId nodeId, string baseChecksum)
    {
        ArgumentNullException.ThrowIfNull(baseChecksum);
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out var entry) || entry.Removed)
            {
                return false;
            }

            entry.BaseChecksum = baseChecksum;
            entry.Halted = false;
            entry.Failures = 0;
            entry.RetryAt = null;
            if (entry.IsDirty)
            {
                // Normally still set from the failed save, which makes the next save due at once.
                var now = _timeProvider.GetUtcNow();
                entry.FirstUnsavedChangeAt ??= now;
                entry.LastChangeAt ??= now;
            }

            ScheduleLocked(entry);
            return true;
        }
    }

    /// <summary>
    /// Reports a change of a document's text (call it from the editor's change event). Schedules the save; cheap
    /// enough to call on every keystroke.
    /// </summary>
    /// <remarks>
    /// A new source object for the same document must continue its version sequence. To start over (for example
    /// after reloading the note), <see cref="Remove"/> the document and <see cref="Track"/> it again.
    /// </remarks>
    /// <param name="nodeId">The document.</param>
    /// <param name="source">The live text; the newest one passed is used for the next save.</param>
    public void NotifyChanged(NodeId nodeId, IAutosaveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var version = source.Version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            var entry = GetOrCreateLocked(nodeId);
            entry.Source = source;
            entry.CurrentVersion = Math.Max(entry.CurrentVersion, version);
            entry.LastChangeAt = now;
            entry.FirstUnsavedChangeAt ??= now;
            ScheduleLocked(entry);
        }
    }

    /// <summary>True when the document has changes that are not durable yet (false for untracked documents).</summary>
    public bool IsDirty(NodeId nodeId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(nodeId, out var entry) && !entry.Removed && entry.IsDirty;
        }
    }

    /// <summary>The documents that have changes that are not durable yet.</summary>
    public IReadOnlyList<NodeId> GetDirtyDocuments()
    {
        lock (_gate)
        {
            return _entries.Values.Where(entry => !entry.Removed && entry.IsDirty).Select(entry => entry.NodeId).ToList();
        }
    }

    /// <summary>The autosave state of a tracked document, or <see langword="null"/> when it is not tracked.</summary>
    public AutosaveState? GetState(NodeId nodeId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(nodeId, out var entry) && !entry.Removed
                ? new AutosaveState(nodeId, entry.CurrentVersion, entry.SavedVersion, entry.ActiveSaves > 0, entry.Failures, entry.DueAt, entry.BaseChecksum, entry.Halted)
                : null;
        }
    }

    /// <summary>
    /// Saves the document now, if it is dirty (for closing a tab). The snapshot is taken on the calling thread
    /// before this method first yields, so calling it on the UI thread never needs the UI thread again. Completes
    /// when everything up to the call is durable, including a save that was already running.
    /// </summary>
    /// <exception cref="Exception">The save failed; the document stays dirty and a retry is scheduled.</exception>
    public Task FlushAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out entry) || entry.Removed)
            {
                return Task.CompletedTask;
            }
        }

        return FlushEntryAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Saves every dirty document now (shutdown, session end, crash handlers). Snapshots are taken on the calling
    /// thread; the saves run concurrently. Never throws for save failures.
    /// </summary>
    /// <param name="timeout">How long to wait (measured with the scheduler's <see cref="TimeProvider"/>).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns><see langword="true"/> when every document was saved within <paramref name="timeout"/>.</returns>
    public async Task<bool> FlushAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        List<Entry> entries;
        lock (_gate)
        {
            entries = _entries.Values.Where(entry => !entry.Removed && entry.IsDirty).ToList();
        }

        var flushes = new List<Task<bool>>(entries.Count);
        foreach (var entry in entries)
        {
            flushes.Add(Succeeds(FlushEntryAsync(entry, CancellationToken.None)));
        }

        try
        {
            var results = await Task.WhenAll(flushes).WaitAsync(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            return results.All(saved => saved);
        }
        catch (TimeoutException)
        {
            return false;
        }

        static async Task<bool> Succeeds(Task flush)
        {
            try
            {
                await flush.ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                // Reported through SaveFailed.
                return false;
            }
        }
    }

    /// <summary>
    /// Stops tracking a document without saving it (call <see cref="FlushAsync"/> first when its changes matter). A
    /// save that is already running completes.
    /// </summary>
    /// <returns><see langword="true"/> when the document was tracked.</returns>
    public bool Remove(NodeId nodeId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out var entry) || entry.Removed)
            {
                return false;
            }

            entry.Removed = true;
            entry.Source = null;
            entry.DueAt = null;
            entry.Timer?.Dispose();
            entry.Timer = null;
            if (entry.ActiveSaves == 0)
            {
                _entries.Remove(nodeId);
            }

            return true;
        }
    }

    /// <summary>Stops all timers. Unsaved changes are not saved: call <see cref="FlushAllAsync"/> first.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Timer?.Dispose();
                entry.Timer = null;
                entry.DueAt = null;
            }
        }
    }

    /// <summary>The delay before retry number <paramref name="failures"/>: 1 s, 2 s, 4 s, … capped at 30 s.</summary>
    public static TimeSpan RetryDelayFor(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        var exponent = Math.Min(failures - 1, 16);
        var delay = TimeSpan.FromTicks(InitialRetryDelay.Ticks << exponent);
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    // ------------------------------------------------------------------ internals

    private Entry GetOrCreateLocked(NodeId nodeId)
    {
        if (_entries.TryGetValue(nodeId, out var entry))
        {
            if (entry.Removed)
            {
                // Tracked again while a save from before the removal is still running: reuse the entry so the
                // two saves stay serialized, but forget what the old session knew. The new generation keeps the
                // old save's outcome from being taken for the new text's.
                entry.Removed = false;
                entry.Generation++;
                entry.SavedVersion = null;
                entry.CurrentVersion = 0;
                entry.Failures = 0;
                entry.RetryAt = null;
                entry.FirstUnsavedChangeAt = null;
                entry.BaseChecksum = null;
                entry.Halted = false;
            }

            return entry;
        }

        entry = new Entry(nodeId);
        _entries[nodeId] = entry;
        return entry;
    }

    // Arms (or disarms) the entry's timer for its next automatic save.
    private void ScheduleLocked(Entry entry)
    {
        if (_disposed || entry.Removed)
        {
            return;
        }

        if (!entry.IsDirty || entry.Halted || entry.FirstUnsavedChangeAt is not { } firstChange)
        {
            // Clean, halted until the App resolves it, or every change is covered by a save in progress (its
            // completion reschedules).
            entry.DueAt = null;
            entry.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var lastChange = entry.LastChangeAt ?? firstChange;
        var due = Min(lastChange + Debounce, firstChange + MaxWait);
        if (entry.RetryAt is { } retryAt && retryAt > due)
        {
            due = retryAt;
        }

        entry.DueAt = due;
        var delay = due - _timeProvider.GetUtcNow();
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        entry.Timer ??= _timeProvider.CreateTimer(static state => ((TimerState)state!).Fire(), new TimerState(this, entry), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        entry.Timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed || entry.Removed || entry.DueAt is null)
            {
                return;
            }

            entry.DueAt = null;
        }

        // A timer save reports failures through SaveFailed and its own retry; nothing awaits it.
        _ = SaveAutomaticallyAsync(entry);
    }

    private async Task SaveAutomaticallyAsync(Entry entry)
    {
        try
        {
            await FlushEntryAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported through SaveFailed, and a retry is scheduled.
        }
    }

    // Takes the snapshot synchronously on the calling thread and queues its save behind the document's earlier
    // saves. The returned task completes when that save (or, for a clean document, the save in progress) is done.
    private Task FlushEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        Task pending;
        try
        {
            pending = SnapshotAndQueue(entry);
        }
        catch (Exception ex)
        {
            // GetSnapshot failed: treat it like a failed save (the document stays dirty and is retried).
            OnSaveFailed(entry, null, _timeProvider.GetUtcNow(), ex);
            return Task.FromException(ex);
        }

        return cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;
    }

    private Task SnapshotAndQueue(Entry entry)
    {
        IAutosaveSource source;
        int generation;
        lock (_gate)
        {
            if (entry.Removed || entry.Source is null)
            {
                return Task.CompletedTask;
            }

            if (!entry.IsDirty)
            {
                return InFlightLocked(entry);
            }

            source = entry.Source;
            generation = entry.Generation;
        }

        // Version first: a change that lands between the two reads makes the text newer, never older, than the
        // version recorded as saved.
        var version = source.Version;
        lock (_gate)
        {
            if (entry.SavedVersion is { } saved && version <= saved)
            {
                return InFlightLocked(entry);
            }
        }

        var text = source.GetSnapshot();
        var snapshot = new Snapshot(generation, version, text, _timeProvider.GetUtcNow());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            if (generation == entry.Generation && version >= entry.CurrentVersion)
            {
                entry.CurrentVersion = version;
                // Every reported change is in this snapshot; the next change opens a new debounce window.
                entry.FirstUnsavedChangeAt = null;
                ScheduleLocked(entry);
            }

            // Saves of one document run strictly one after another, in the order their snapshots were queued.
            previous = entry.Tail;
            entry.Tail = completion.Task;
            entry.ActiveSaves++;
        }

        _ = SaveAfterAsync(entry, previous, snapshot, completion);
        return completion.Task;
    }

    // The save in progress, for a document that is clean apart from it (a finished save's outcome is history).
    private static Task InFlightLocked(Entry entry) => entry.Tail.IsCompleted ? Task.CompletedTask : entry.Tail;

    // Runs after the document's previous save; records the outcome and raises the event before completing, so a
    // caller that awaits the flush sees the final state.
    private async Task SaveAfterAsync(Entry entry, Task previous, Snapshot snapshot, TaskCompletionSource completion)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The earlier save reported its own failure; this snapshot is newer and is saved regardless.
        }

        bool superseded;
        string? baseChecksum;
        lock (_gate)
        {
            var current = snapshot.Generation == entry.Generation;
            superseded = current && entry.SavedVersion is { } saved && snapshot.Version <= saved;
            // The previous save of this document has completed, so the base is the text it stored. A snapshot from
            // before the document was removed and tracked again has no base of its own any more.
            baseChecksum = current ? entry.BaseChecksum : null;
        }

        DocumentSaveResult? result = null;
        Exception? failure = null;
        if (!superseded)
        {
            try
            {
                result = await _saver.SaveContentAsync(entry.NodeId, snapshot.Text, baseChecksum, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        lock (_gate)
        {
            if (result is not null && snapshot.Generation == entry.Generation)
            {
                if (entry.SavedVersion is not { } savedBefore || snapshot.Version > savedBefore)
                {
                    entry.SavedVersion = snapshot.Version;
                }

                entry.BaseChecksum = result.Checksum;
                entry.Failures = 0;
                entry.RetryAt = null;
                entry.Halted = false;
                ScheduleLocked(entry);
            }

            ReleaseActiveSaveLocked(entry);
        }

        if (failure is not null)
        {
            OnSaveFailed(entry, snapshot.Generation, snapshot.TakenAt, failure);
            completion.TrySetException(failure);
            return;
        }

        if (result is not null)
        {
            Raise(SaveSucceeded, new AutosaveSucceededEventArgs(entry.NodeId, snapshot.Version, result));
        }

        completion.TrySetResult();
    }

    // generation is null when the snapshot itself failed (it belongs to the current generation then). A save from
    // before the document was removed and tracked again is reported but leaves the new state alone. A conflict or a
    // note that no longer exists halts automatic saving: retrying could only fail again.
    private void OnSaveFailed(Entry entry, int? generation, DateTimeOffset snapshotTakenAt, Exception error)
    {
        var failures = 1;
        var retryDelay = TimeSpan.Zero;
        var willRetry = error is not (DocumentConflictException or NodeNotFoundException);
        lock (_gate)
        {
            if (generation is not { } owner || owner == entry.Generation)
            {
                failures = ++entry.Failures;
                if (willRetry)
                {
                    retryDelay = RetryDelayFor(failures);
                    entry.RetryAt = _timeProvider.GetUtcNow() + retryDelay;
                }
                else
                {
                    entry.Halted = true;
                    entry.RetryAt = null;
                }

                // The failed snapshot's changes need a save again.
                entry.FirstUnsavedChangeAt ??= snapshotTakenAt;
                entry.LastChangeAt ??= snapshotTakenAt;
                ScheduleLocked(entry);
            }
        }

        Raise(SaveFailed, new AutosaveFailedEventArgs(entry.NodeId, error, failures, retryDelay, willRetry));
    }

    private void ReleaseActiveSaveLocked(Entry entry)
    {
        entry.ActiveSaves--;
        if (entry.Removed && entry.ActiveSaves == 0 && _entries.TryGetValue(entry.NodeId, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(entry.NodeId);
        }
    }

    private void Raise<T>(EventHandler<T>? handlers, T args)
        where T : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"An autosave event handler failed: {ex}");
            }
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private sealed record Snapshot(int Generation, long Version, string Text, DateTimeOffset TakenAt);

    private sealed class TimerState(AutosaveScheduler scheduler, Entry entry)
    {
        public void Fire() => scheduler.OnTimer(entry);
    }

    // Mutable per-document state, guarded by _gate.
    private sealed class Entry(NodeId nodeId)
    {
        public NodeId NodeId { get; } = nodeId;

        // The last queued save; the next one starts after it.
        public Task Tail { get; set; } = Task.CompletedTask;

        public IAutosaveSource? Source { get; set; }

        // Incremented when a removed document is tracked again.
        public int Generation { get; set; }

        public long CurrentVersion { get; set; }

        public long? SavedVersion { get; set; }

        // The first change not covered by a snapshot taken for saving; null when there is none.
        public DateTimeOffset? FirstUnsavedChangeAt { get; set; }

        public DateTimeOffset? LastChangeAt { get; set; }

        public DateTimeOffset? RetryAt { get; set; }

        public DateTimeOffset? DueAt { get; set; }

        public int Failures { get; set; }

        // The checksum of the stored text the source's text is based on; null when unknown.
        public string? BaseChecksum { get; set; }

        // Automatic saving stopped after a failure a retry cannot fix; FlushAsync still tries.
        public bool Halted { get; set; }

        // Queued or running saves.
        public int ActiveSaves { get; set; }

        public bool Removed { get; set; }

        public ITimer? Timer { get; set; }

        public bool IsDirty => SavedVersion is not { } saved || CurrentVersion > saved;
    }
}
