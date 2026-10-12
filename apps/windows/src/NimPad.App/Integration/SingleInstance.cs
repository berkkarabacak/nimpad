using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using NimPad.Core.Storage;
using NimPad.Presentation.Integration;

namespace NimPad.App.Integration;

/// <summary>
/// One NimPad per user and profile: a per-user mutex decides which launch is the primary instance, and the primary
/// listens on the named pipe <c>NimPad-&lt;user SID&gt;</c> (<see cref="SingleInstanceProtocol.GetInstanceName"/>),
/// whose access control admits only the current user. A later launch forwards its files over the pipe
/// (<see cref="TryForward"/>) and exits.
/// </summary>
/// <remarks>
/// <para>The mutex is <c>Local\&lt;name&gt;</c>; ownership (not mere existence) makes the primary, so a crashed primary
/// (abandoned mutex) is replaced by the next launch. Release happens in <see cref="Dispose"/>, on the thread that
/// acquired it (the UI thread).</para>
/// <para>The server keeps one listening instance at all times (the next one is created before a connected one is
/// served), creates the first with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> and gives the current user the only access
/// rule (<see cref="CreatePipeSecurity"/>), so no other user can connect or squat on the name. The client connects with
/// <see cref="PipeOptions.CurrentUserOnly"/>, which checks that the server runs as the same user. Every pipe error is
/// logged and never crashes either side.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    /// <summary>How long a forwarding launch waits for the primary to answer.</summary>
    public static readonly TimeSpan DefaultForwardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the primary waits for a connected client to send its message.</summary>
    public static readonly TimeSpan ServerReadTimeout = TimeSpan.FromSeconds(5);

    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;
    private bool _disposed;

    /// <summary>Uses <paramref name="name"/> for the pipe and <c>Local\</c> + name for the mutex.</summary>
    public SingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            throw new ArgumentException("The instance name cannot contain path separators.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The pipe name.</summary>
    public string Name { get; }

    /// <summary>The mutex name.</summary>
    public string MutexName => @"Local\" + Name;

    /// <summary>True once <see cref="TryAcquire"/> made this the primary instance.</summary>
    public bool IsPrimary => _ownsMutex;

    /// <summary>True while the pipe server runs.</summary>
    public bool IsListening => _serverTask is { IsCompleted: false };

    /// <summary>
    /// The instance name for this user and the profile <paramref name="dataDirectory"/> selects (the <c>--data-dir</c>
    /// value, or <see langword="null"/> for the default or portable location).
    /// </summary>
    public static string GetDefaultName(string? dataDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        return SingleInstanceProtocol.GetInstanceName(sid, AppPaths.ResolveForCurrentProcess(dataDirectory));
    }

    /// <summary>
    /// The pipe's security for <paramref name="identity"/>: the only access entry allows its user, inherited entries
    /// are blocked, and the owner is the token's default owner (the user, or the Administrators group for an elevated
    /// administrator), which is what a client's <see cref="PipeOptions.CurrentUserOnly"/> check compares with.
    /// </summary>
    public static PipeSecurity CreatePipeSecurity(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var user = identity.User ?? throw new InvalidOperationException("The current user has no SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity.Owner ?? user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Tries to become the primary instance (owns the mutex; an abandoned mutex left by a crash counts as acquired).
    /// If the mutex cannot be opened at all (another program holds the name with other access rights), this launch
    /// runs as the primary without the check rather than not at all.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex)
        {
            return true;
        }

        try
        {
            _mutex ??= new Mutex(initiallyOwned: false, MutexName);
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                Trace.TraceWarning("The previous NimPad instance ended without releasing its mutex; taking over.");
                _ownsMutex = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            Trace.TraceError($"The single-instance mutex {MutexName} cannot be used ({ex.Message}); running without it.");
            _mutex?.Dispose();
            _mutex = null;
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    /// <summary>
    /// Starts accepting forwarded launches on a background task (primary instance only). <paramref name="onMessage"/>
    /// runs on that task for each valid message, before the sender gets its acknowledgement: keep it short (queue the
    /// message and post to the UI thread).
    /// </summary>
    public void StartServer(Action<SingleInstanceMessage> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance listens.");
        }

        if (_serverTask is not null)
        {
            return;
        }

        _serverCancellation = new CancellationTokenSource();
        var token = _serverCancellation.Token;
        _serverTask = Task.Run(() => RunServerAsync(onMessage, token), CancellationToken.None);
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the primary instance and waits for its acknowledgement. Before sending, it
    /// lets the primary take the foreground (<c>AllowSetForegroundWindow</c>), which only this launch, started by the
    /// user, may grant.
    /// </summary>
    /// <returns><see langword="true"/> when the primary took the message.</returns>
    public bool TryForward(SingleInstanceMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Off the caller's synchronization context (the UI thread at startup), so nothing can deadlock.
        return Task.Run(() => ForwardAsync(message, timeout)).GetAwaiter().GetResult();
    }

    /// <summary>Stops the server and releases the mutex (call on the thread that acquired it).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_serverCancellation is { } cancellation)
        {
            cancellation.Cancel();
            try
            {
                _serverTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Logged by the server loop.
            }

            cancellation.Dispose();
        }

        if (_mutex is { } mutex)
        {
            if (_ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException ex)
                {
                    // Released from another thread: closing the handle abandons it, which the next launch accepts.
                    Trace.TraceWarning($"The single-instance mutex was not released: {ex.Message}");
                }
            }

            mutex.Dispose();
        }

        _ownsMutex = false;
    }

    /// <summary>Creates one listening instance of the pipe with the current-user-only security.</summary>
    internal NamedPipeServerStream CreateServer(bool first)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            Name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            CreatePipeSecurity(identity));
    }

    private async Task<bool> ForwardAsync(SingleInstanceMessage message, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverProcessId))
            {
                NativeMethods.AllowSetForegroundWindow(serverProcessId);
            }

            await SingleInstanceProtocol.WriteAsync(client, message, cancellation.Token).ConfigureAwait(false);
            var answer = new byte[1];
            var read = await client.ReadAsync(answer, cancellation.Token).ConfigureAwait(false);
            if (read == 1 && answer[0] == SingleInstanceProtocol.Acknowledgement)
            {
                return true;
            }

            Trace.TraceWarning("The running NimPad closed the pipe without acknowledging the forwarded launch.");
            return false;
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning($"The running NimPad did not answer within {timeout.TotalSeconds:0.#} s.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            Trace.TraceWarning($"Forwarding to the running NimPad failed: {ex.Message}");
            return false;
        }
    }

    private async Task RunServerAsync(Action<SingleInstanceMessage> onMessage, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? listener = null;
        var first = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    listener ??= CreateServer(first);
                    first = false;
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    // The next instance exists before this one is served, so the name is never free to squat on.
                    var connected = listener;
                    listener = null;
                    try
                    {
                        listener = CreateServer(first: false);
                    }
                    catch
                    {
                        await connected.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    await ServeAsync(connected, onMessage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (first)
                {
                    // The name is taken by someone else: do not listen on a pipe we did not create first.
                    Trace.TraceError($"The single-instance pipe {Name} could not be created: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"The single-instance pipe failed; retrying: {ex.Message}");
                    listener?.Dispose();
                    listener = null;
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            listener?.Dispose();
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream connected, Action<SingleInstanceMessage> onMessage, CancellationToken cancellationToken)
    {
        await using (connected.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ServerReadTimeout);
            try
            {
                var message = await SingleInstanceProtocol.ReadAsync(connected, timeout.Token).ConfigureAwait(false);
                onMessage(message);
                await connected.WriteAsync(new[] { SingleInstanceProtocol.Acknowledgement }, timeout.Token).ConfigureAwait(false);
                await connected.FlushAsync(timeout.Token).ConfigureAwait(false);

                // Wait for the sender to hang up, so the acknowledgement is read before this end closes.
                var rest = new byte[1];
                while (await connected.ReadAsync(rest, timeout.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning("A forwarded launch did not finish its message in time.");
            }
            catch (InvalidDataException ex)
            {
                Trace.TraceWarning($"Ignored an invalid single-instance message: {ex.Message}");
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"A forwarded launch disconnected: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceError($"Handling a forwarded launch failed: {ex}");
            }
        }
    }
}
