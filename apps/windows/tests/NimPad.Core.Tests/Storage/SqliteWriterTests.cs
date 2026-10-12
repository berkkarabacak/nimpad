using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public sealed class SqliteWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly LocalDatabase _database;

    public SqliteWriterTests()
    {
        _database = LocalDatabase.Open(_temp.Combine("nimpad.db"), _temp.Combine("backups"));
        _database.Writer.ExecuteAsync(context =>
            context.Execute("CREATE TABLE test_log (id INTEGER PRIMARY KEY, value TEXT NOT NULL);")).GetAwaiter().GetResult();
    }

    private SqliteWriter Writer => _database.Writer;

    public void Dispose()
    {
        _database.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task CommittedWritesAreVisibleToReaders()
    {
        var rows = await Writer.ExecuteAsync(context => context.Execute("INSERT INTO test_log (value) VALUES ('a'), ('b');"));

        Assert.Equal(2, rows);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task AFailingJobRollsBackAndSurfacesTheException()
    {
        var failure = new InvalidOperationException("boom");
        var task = Writer.ExecuteAsync<int>(context =>
        {
            context.Execute("INSERT INTO test_log (value) VALUES ('rolled back');");
            throw failure;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Same(failure, error);
        Assert.Equal(0, await CountAsync());

        // The writer keeps working after a failure.
        await Writer.ExecuteAsync(context => context.Execute("INSERT INTO test_log (value) VALUES ('after');"));
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task ASqlErrorRollsBackEarlierStatementsOfTheJob()
    {
        var task = Writer.ExecuteAsync(context =>
        {
            context.Execute("INSERT INTO test_log (value) VALUES ('first');");
            context.Execute("INSERT INTO test_log (value) VALUES (NULL);");
        });

        await Assert.ThrowsAsync<SqliteException>(() => task);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task JobsRunOneAtATimeInSubmissionOrderOnOneDedicatedThread()
    {
        var threads = new ConcurrentBag<int>();
        var testThread = Environment.CurrentManagedThreadId;
        var tasks = Enumerable.Range(0, 200)
            .Select(i => Writer.ExecuteAsync(context =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                Assert.True(Writer.IsWriterThread);
                context.Execute("INSERT INTO test_log (value) VALUES ($v);", ("$v", i.ToString("D3")));
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Single(threads.Distinct());
        Assert.NotEqual(testThread, threads.First());
        Assert.False(Writer.IsWriterThread);
        var values = await ValuesAsync();
        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString("D3")), values);
    }

    [Fact]
    public async Task ConcurrentCallersNeverLoseUpdates()
    {
        await Writer.ExecuteAsync(context => context.Execute("INSERT INTO test_log (id, value) VALUES (1, '0');"));
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await Writer.ExecuteAsync(context =>
                {
                    var current = int.Parse((string)context.Scalar("SELECT value FROM test_log WHERE id = 1;")!);
                    context.Execute("UPDATE test_log SET value = $v WHERE id = 1;", ("$v", (current + 1).ToString()));
                });
            }
        }));
        await Task.WhenAll(callers);

        Assert.Equal(["400"], await ValuesAsync());
    }

    [Fact]
    public async Task JobsRunInsideBeginImmediate()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _database.DatabasePath, Pooling = false }.ToString();
        var otherWriterBlocked = await Writer.ExecuteAsync(context =>
        {
            // Nothing read or written yet: a deferred BEGIN would hold no lock at this point, only BEGIN IMMEDIATE does.
            using var other = new SqliteConnection(connectionString);
            other.Open();
            other.ExecuteNonQuery("PRAGMA busy_timeout = 0;");
            using var command = other.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE;";
            command.CommandTimeout = 1; // seconds; 0 would mean "wait forever"
            try
            {
                command.ExecuteNonQuery();
                other.ExecuteNonQuery("ROLLBACK;");
                return false;
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) == 5)
            {
                return true; // SQLITE_BUSY: the job's transaction already holds the write lock.
            }
        });

        Assert.True(otherWriterBlocked);
    }

    [Fact]
    public async Task NestedCallsRunInlineWithoutDeadlock()
    {
        var result = await Writer.ExecuteAsync(context =>
        {
            context.Execute("INSERT INTO test_log (value) VALUES ('outer');");
            // Blocking on a nested job from inside a job must not deadlock: it runs inline in a savepoint.
            var inner = Writer.ExecuteAsync(innerContext =>
            {
                innerContext.Execute("INSERT INTO test_log (value) VALUES ('inner');");
                return 42;
            });
            Assert.True(inner.IsCompletedSuccessfully);
            return inner.GetAwaiter().GetResult();
        });

        Assert.Equal(42, result);
        Assert.Equal(["outer", "inner"], await ValuesAsync());
    }

    [Fact]
    public async Task AFailingNestedJobRollsBackOnlyItsOwnChanges()
    {
        var committed = new List<string>();
        await Writer.ExecuteAsync(context =>
        {
            context.Execute("INSERT INTO test_log (value) VALUES ('outer');");
            context.OnCommitted(() => committed.Add("outer"));
            var inner = Writer.ExecuteAsync(innerContext =>
            {
                innerContext.Execute("INSERT INTO test_log (value) VALUES ('inner');");
                innerContext.OnCommitted(() => committed.Add("inner"));
                throw new InvalidOperationException("inner failed");
            });
            Assert.True(inner.IsFaulted);
            Assert.IsType<InvalidOperationException>(inner.Exception!.InnerException);
        });

        Assert.Equal(["outer"], await ValuesAsync());
        Assert.Equal(["outer"], committed);
    }

    [Fact]
    public async Task CommitCallbacksRunOnlyAfterCommit()
    {
        var events = new ConcurrentQueue<string>();
        await Writer.ExecuteAsync(context =>
        {
            context.OnCommitted(() => events.Enqueue("committed"));
            events.Enqueue("job");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer.ExecuteAsync(context =>
        {
            context.OnCommitted(() => events.Enqueue("never"));
            throw new InvalidOperationException();
        }));

        Assert.Equal(["job", "committed"], events);
    }

    [Fact]
    public async Task AThrowingCommitCallbackDoesNotFailTheCommittedJob()
    {
        Exception? reported = null;
        Writer.CallbackFailed += (_, ex) => reported = ex;
        await Writer.ExecuteAsync(context =>
        {
            context.Execute("INSERT INTO test_log (value) VALUES ('kept');");
            context.OnCommitted(() => throw new InvalidOperationException("callback"));
        });

        Assert.Equal("callback", reported?.Message);
        Assert.Equal(["kept"], await ValuesAsync());
    }

    [Fact]
    public async Task FlushWaitsForQueuedJobsAndIsANoOpOnTheWriterThread()
    {
        var gate = new ManualResetEventSlim();
        var slow = Writer.ExecuteAsync(context =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            context.Execute("INSERT INTO test_log (value) VALUES ('slow');");
        });
        var queued = Writer.ExecuteAsync(context => context.Execute("INSERT INTO test_log (value) VALUES ('queued');"));

        Assert.False(Writer.Flush(TimeSpan.FromMilliseconds(100)));
        gate.Set();
        Assert.True(Writer.Flush(TimeSpan.FromSeconds(10)));
        Assert.True(slow.IsCompletedSuccessfully);
        Assert.True(queued.IsCompletedSuccessfully);

        var flushedInline = await Writer.ExecuteAsync(_ => Writer.Flush(TimeSpan.FromSeconds(1)));
        Assert.True(flushedInline);
        await Writer.FlushAsync();
        Assert.Equal(["slow", "queued"], await ValuesAsync());
    }

    [Fact]
    public async Task ContinuationsDoNotRunOnTheWriterThread()
    {
        // No synchronization context inside Task.Run, so only RunContinuationsAsynchronously keeps the continuation
        // off the writer thread.
        var continuationOnWriter = await Task.Run(async () =>
        {
            await Writer.ExecuteAsync(_ => 0);
            return Writer.IsWriterThread;
        });

        Assert.False(continuationOnWriter);
    }

    [Fact]
    public async Task CancelledJobsDoNotRun()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var task = Writer.ExecuteAsync(context => context.Execute("INSERT INTO test_log (value) VALUES ('no');"), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task NonTransactionalJobsAreRejectedInsideATransaction()
    {
        var inner = await Writer.ExecuteAsync(_ => Writer.ExecuteWithoutTransactionAsync(_ => 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inner);

        Assert.Equal(1, await Writer.ExecuteWithoutTransactionAsync(connection => 1));
    }

    [Fact]
    public async Task DisposeCompletesQueuedWorkThenRejectsNewJobs()
    {
        using var temp = new TempDirectory();
        var database = LocalDatabase.Open(temp.Combine("nimpad.db"), temp.Combine("backups"));
        var tasks = Enumerable.Range(0, 50)
            .Select(i => database.Writer.ExecuteAsync(context =>
            {
                Thread.Sleep(1);
                context.Execute("INSERT INTO ui_state (key, value, updated_at) VALUES ($k, '1', 'x');", ("$k", $"k{i}"));
            }))
            .ToArray();

        database.Dispose();

        Assert.All(tasks, task => Assert.True(task.IsCompletedSuccessfully));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.Writer.ExecuteAsync(_ => 1));
        Assert.True(database.Writer.Flush(TimeSpan.FromSeconds(1)));

        using var reopened = LocalDatabase.Open(temp.Combine("nimpad.db"), temp.Combine("backups"));
        Assert.Equal(50, await reopened.Readers.ExecuteAsync(connection => (long)connection.ExecuteScalar("SELECT count(*) FROM ui_state;")!));
    }

    private Task<long> CountAsync() =>
        _database.Readers.ExecuteAsync(connection => (long)connection.ExecuteScalar("SELECT count(*) FROM test_log;")!);

    private Task<List<string>> ValuesAsync() =>
        _database.Readers.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM test_log ORDER BY id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                values.Add(reader.GetString(0));
            }

            return values;
        });
}
