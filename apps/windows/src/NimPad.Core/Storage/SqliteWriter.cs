using System.Data;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace NimPad.Core.Storage;

/// <summary>
/// The single background writer (architecture.md, "Desktop threading model"): one dedicated thread owns the only
/// write connection and runs queued jobs one at a time, in submission order.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each <see cref="ExecuteAsync{T}(Func{WriteContext, T}, CancellationToken)"/> job runs in its own
/// <c>BEGIN IMMEDIATE</c> transaction: it commits when the job returns and rolls back when it throws, and the
/// exception then faults the returned task.</item>
/// <item>A job may call <see cref="ExecuteAsync{T}(Func{WriteContext, T}, CancellationToken)"/> again. The nested
/// job runs immediately, inline, inside a savepoint of the outer transaction (so it never deadlocks, even when the
/// caller blocks on the returned task); a failing nested job rolls back only its own changes.</item>
/// <item>Awaiting continuations never run on the writer thread.</item>
/// <item><see cref="Dispose"/> completes all queued work before closing the connection.</item>
/// </list>
/// </remarks>
public sealed class SqliteWriter : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Channel<WriteJob> _channel;
    private readonly Thread _thread;
    private WriteContext? _current;
    private int _savepointCounter;
    private int _disposed;

    /// <summary>Takes ownership of an open connection and starts the writer thread.</summary>
    internal SqliteWriter(SqliteConnection connection, string threadName = "NimPad SQLite writer")
    {
        _connection = connection;
        _channel = Channel.CreateUnbounded<WriteJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
        _thread.Start();
    }

    /// <summary>
    /// Raised (on the writer thread) when a callback registered with <see cref="WriteContext.OnCommitted"/> throws.
    /// The commit itself has succeeded at that point.
    /// </summary>
    public event EventHandler<Exception>? CallbackFailed;

    /// <summary>True when the calling code runs on the writer thread (inside a job or a commit callback).</summary>
    public bool IsWriterThread => Thread.CurrentThread == _thread;

    /// <summary>Queues a job that runs in its own <c>BEGIN IMMEDIATE</c> transaction and returns its result.</summary>
    /// <param name="job">Runs on the writer thread. Throwing rolls the transaction back.</param>
    /// <param name="cancellationToken">Cancels the job if it has not started yet.</param>
    /// <returns>A task that completes after the commit, or faults with the job's exception after the rollback.</returns>
    public Task<T> ExecuteAsync<T>(Func<WriteContext, T> job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (IsWriterThread)
        {
            return _current is { } context ? RunNested(context, job) : RunInline(job);
        }

        var item = new TransactionalJob<T>(job, cancellationToken);
        return Enqueue(item) ? item.Completion.Task : Task.FromException<T>(DisposedException());
    }

    /// <inheritdoc cref="ExecuteAsync{T}(Func{WriteContext, T}, CancellationToken)"/>
    public Task ExecuteAsync(Action<WriteContext> job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ExecuteAsync(
            context =>
            {
                job(context);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Queues a job that runs on the write connection without a transaction, for statements that cannot run in one
    /// (<c>VACUUM INTO</c>, WAL checkpoints, <c>PRAGMA optimize</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from inside a transactional job (returned as a faulted task).</exception>
    public Task<T> ExecuteWithoutTransactionAsync<T>(Func<SqliteConnection, T> job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (IsWriterThread)
        {
            if (_current is not null)
            {
                return Task.FromException<T>(new InvalidOperationException(
                    "A non-transactional job cannot run inside a transactional write job."));
            }

            try
            {
                return Task.FromResult(job(_connection));
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var item = new RawJob<T>(job, cancellationToken);
        return Enqueue(item) ? item.Completion.Task : Task.FromException<T>(DisposedException());
    }

    /// <summary>
    /// Blocks until every job queued before this call has finished. Returns immediately when called on the writer
    /// thread (the current job is still running, so waiting would deadlock) or after disposal has drained the queue.
    /// </summary>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <returns><see langword="false"/> when the timeout elapsed first.</returns>
    public bool Flush(TimeSpan timeout)
    {
        if (IsWriterThread)
        {
            return true;
        }

        var marker = new FlushMarker();
        if (!Enqueue(marker))
        {
            // Disposed: the thread drains whatever is left before it exits.
            return _thread.Join(timeout);
        }

        return marker.Completion.Task.Wait(timeout);
    }

    /// <summary>Blocks until every job queued before this call has finished (see <see cref="Flush(TimeSpan)"/>).</summary>
    public void Flush() => Flush(Timeout.InfiniteTimeSpan);

    /// <summary>Completes when every job queued before this call has finished.</summary>
    public Task FlushAsync()
    {
        if (IsWriterThread)
        {
            return Task.CompletedTask;
        }

        var marker = new FlushMarker();
        return Enqueue(marker) ? marker.Completion.Task : Task.Run(() => _thread.Join());
    }

    /// <summary>
    /// Stops accepting jobs, runs everything already queued, then closes the write connection. Blocks until done,
    /// except when called on the writer thread, where it only stops accepting jobs.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        if (!IsWriterThread)
        {
            _thread.Join();
        }
    }

    private static ObjectDisposedException DisposedException() => new(nameof(SqliteWriter));

    private bool Enqueue(WriteJob job) => Volatile.Read(ref _disposed) == 0 && _channel.Writer.TryWrite(job);

    private void Run()
    {
        var reader = _channel.Reader;
        try
        {
            while (true)
            {
                while (reader.TryRead(out var job))
                {
                    job.Execute(this);
                }

                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    break;
                }
            }
        }
        finally
        {
            _connection.Dispose();
        }
    }

    // A top-level job: BEGIN IMMEDIATE, run, COMMIT, then the commit callbacks.
    private T RunTransaction<T>(Func<WriteContext, T> job)
    {
        using var transaction = _connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var context = new WriteContext(_connection, transaction);
        _current = context;
        T result;
        try
        {
            result = job(context);
            transaction.Commit();
        }
        catch
        {
            TryRollback(transaction);
            throw;
        }
        finally
        {
            _current = null;
        }

        foreach (var callback in context.TakeCommitCallbacks())
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                CallbackFailed?.Invoke(this, ex);
            }
        }

        return result;
    }

    // Called on the writer thread outside any transaction (from a commit callback or a non-transactional job).
    private Task<T> RunInline<T>(Func<WriteContext, T> job)
    {
        try
        {
            return Task.FromResult(RunTransaction(job));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    // Called from inside a job: run now, in a savepoint of the current transaction.
    private Task<T> RunNested<T>(WriteContext context, Func<WriteContext, T> job)
    {
        var savepoint = string.Create(CultureInfo.InvariantCulture, $"nimpad_nested_{++_savepointCounter}");
        var callbackMark = context.CommitCallbackCount;
        context.Transaction.Save(savepoint);
        try
        {
            var result = job(context);
            context.Transaction.Release(savepoint);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            context.Transaction.Rollback(savepoint);
            context.Transaction.Release(savepoint);
            context.TruncateCommitCallbacks(callbackMark);
            return Task.FromException<T>(ex);
        }
    }

    private static void TryRollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception)
        {
            // The commit or the job may already have ended the transaction (or the connection is broken); the
            // original exception is the one worth reporting.
        }
    }

    private abstract class WriteJob
    {
        public abstract void Execute(SqliteWriter writer);
    }

    private sealed class TransactionalJob<T>(Func<WriteContext, T> job, CancellationToken cancellationToken) : WriteJob
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute(SqliteWriter writer)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                Completion.TrySetResult(writer.RunTransaction(job));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private sealed class RawJob<T>(Func<SqliteConnection, T> job, CancellationToken cancellationToken) : WriteJob
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute(SqliteWriter writer)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                Completion.TrySetResult(job(writer._connection));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private sealed class FlushMarker : WriteJob
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute(SqliteWriter writer) => Completion.TrySetResult();
    }
}
