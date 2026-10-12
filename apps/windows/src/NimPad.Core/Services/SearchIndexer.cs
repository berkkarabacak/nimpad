using System.Diagnostics;
using NimPad.Core.Storage;

namespace NimPad.Core.Services;

/// <summary>
/// Indexes, in the background, the bodies of documents too large to index inside their save
/// (<c>document_contents.fts_dirty = 1</c>; see <see cref="DocumentService"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every committed creation or content change restarts an idle timer (<see cref="IdleDelay"/>, driven by the
/// <see cref="TimeProvider"/>); when writes have been quiet that long, the indexer catches up. A large note is
/// therefore tokenized once after typing pauses, not on every autosave.</item>
/// <item><see cref="Start"/> schedules a catch-up for documents left flagged by an earlier session.</item>
/// <item>Each document is indexed in its own writer job, so autosaves of other documents slot in between.</item>
/// <item>Search results for a flagged document come from its previous indexed text until it is caught up.</item>
/// </list>
/// </remarks>
public sealed class SearchIndexer : IDisposable
{
    /// <summary>How long writes must be quiet before flagged documents are indexed, by default.</summary>
    public static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromSeconds(2);

    private readonly LocalDatabase _database;
    private readonly IWorkspaceChangeNotifier _notifier;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _gate = new();
    private Task _running = Task.CompletedTask;
    private bool _rerun;
    private bool _disposed;

    /// <summary>Creates the indexer and starts listening for changes (call <see cref="Start"/> to also catch up at startup).</summary>
    /// <param name="database">The local database.</param>
    /// <param name="notifier">The committed-change notifier the services publish to.</param>
    /// <param name="timeProvider">The clock driving the idle timer; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="idleDelay">The quiet period before indexing; <see cref="DefaultIdleDelay"/> by default.</param>
    public SearchIndexer(LocalDatabase database, IWorkspaceChangeNotifier notifier, TimeProvider? timeProvider = null, TimeSpan? idleDelay = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(notifier);
        IdleDelay = idleDelay ?? DefaultIdleDelay;
        ArgumentOutOfRangeException.ThrowIfLessThan(IdleDelay, TimeSpan.Zero, nameof(idleDelay));
        _database = database;
        _notifier = notifier;
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => OnIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _notifier.Changed += OnWorkspaceChanged;
    }

    /// <summary>The quiet period before flagged documents are indexed.</summary>
    public TimeSpan IdleDelay { get; }

    /// <summary>Raised (on a background thread) when indexing a document failed; it stays flagged and is retried later.</summary>
    public event EventHandler<Exception>? IndexingFailed;

    /// <summary>Raised (on a background thread) after a catch-up indexed at least one document; the argument is the count.</summary>
    public event EventHandler<int>? CaughtUp;

    /// <summary>Schedules a catch-up after <see cref="IdleDelay"/> for documents flagged by an earlier session.</summary>
    public void Start() => ScheduleCatchUp();

    /// <summary>(Re)starts the idle timer; the catch-up runs once nothing else has called this for <see cref="IdleDelay"/>.</summary>
    public void ScheduleCatchUp()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(IdleDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Indexes every flagged document now, one writer job each.</summary>
    /// <returns>How many documents were indexed.</returns>
    public async Task<int> CatchUpAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        var count = 0;
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            var indexed = await _database.Writer.ExecuteAsync(
                context => FtsIndex.NextDirty(context) is { } nodeId && FtsIndex.Reindex(context, nodeId),
                linked.Token).ConfigureAwait(false);
            if (!indexed)
            {
                break;
            }

            count++;
        }

        if (count > 0)
        {
            CaughtUp?.Invoke(this, count);
        }

        return count;
    }

    /// <summary>How many documents are waiting to be indexed.</summary>
    public Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
        _database.Readers.ExecuteAsync(
            connection => Convert.ToInt32(
                connection.ExecuteScalar("SELECT count(*) FROM document_contents WHERE fts_dirty = 1;"),
                System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);

    /// <summary>Stops listening and cancels pending work; waits briefly for a document being indexed.</summary>
    public void Dispose()
    {
        Task running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = _running;
        }

        _notifier.Changed -= OnWorkspaceChanged;
        _timer.Dispose();
        _disposing.Cancel();
        try
        {
            running.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Failures were reported through IndexingFailed; cancellation is expected here.
        }

        _disposing.Dispose();
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        foreach (var change in e.Changes)
        {
            if (change.Kind is NodeChangeKind.ContentChanged or NodeChangeKind.Created)
            {
                ScheduleCatchUp();
                return;
            }
        }
    }

    private void OnIdle()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_running.IsCompleted)
            {
                _rerun = true;
                return;
            }

            _running = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await CatchUpAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The database closed during shutdown; flagged documents are indexed on the next start.
                return;
            }
            catch (Exception ex)
            {
                if (IndexingFailed is { } handler)
                {
                    handler(this, ex);
                }
                else
                {
                    Trace.TraceError($"Background search indexing failed: {ex}");
                }
            }

            lock (_gate)
            {
                if (!_rerun || _disposed)
                {
                    return;
                }

                _rerun = false;
            }
        }
    }
}
