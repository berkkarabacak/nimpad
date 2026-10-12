using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Shell;

namespace NimPad.App.Integration;

/// <summary>
/// Wires NimPad into Windows: single instance (mutex + current-user pipe), the global shortcut, start with Windows,
/// external-file watching and restart registration. The App calls it from <c>App.xaml.cs</c> (see
/// <c>Integration/README.md</c> for the exact lines):
/// <list type="number">
/// <item><see cref="TryBecomePrimaryInstance"/> before the main window exists; <see langword="false"/> means the
/// files were forwarded to the running NimPad and this process should shut down.</item>
/// <item><see cref="Autostart"/> and <see cref="GlobalHotkey"/> go into <c>PlatformServices</c>.</item>
/// <item><see cref="Attach"/> after the window is shown and <c>ShellViewModel.InitializeAsync</c> completed.</item>
/// <item><see cref="Dispose"/> on exit (on the UI thread).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IntegrationBootstrapper : IDisposable
{
    private readonly ConcurrentQueue<SingleInstanceMessage> _inbox = new();
    private readonly Dispatcher _dispatcher;
    private readonly string _launchDirectory;
    private SingleInstance? _instance;
    private Window? _window;
    private ShellViewModel? _shell;
    private ExternalFileWatcher? _watcher;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the bootstrapper on the UI thread (for example at the start of <c>App.OnStartup</c>).</summary>
    /// <param name="commandLine">The parsed command line.</param>
    /// <param name="options">What to turn on; <see cref="IntegrationOptions.FromCommandLine"/> by default.</param>
    public IntegrationBootstrapper(AppCommandLine commandLine, IntegrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        CommandLine = commandLine;
        Options = options ?? IntegrationOptions.FromCommandLine(commandLine);
        _dispatcher = Dispatcher.CurrentDispatcher;

        // File dialogs may change the current directory later; launch-relative paths use the one at startup.
        _launchDirectory = Environment.CurrentDirectory;
        GlobalHotkey = new GlobalHotkeyService(Options.RegisterGlobalHotkey);
        GlobalHotkey.Pressed += OnHotkeyPressed;
        Autostart = new AutostartService();
    }

    /// <summary>The command line this launch was started with.</summary>
    public AppCommandLine CommandLine { get; }

    /// <summary>What is turned on.</summary>
    public IntegrationOptions Options { get; }

    /// <summary>Start with Windows, for <c>PlatformServices.Autostart</c>.</summary>
    public AutostartService Autostart { get; }

    /// <summary>The global shortcut, for <c>PlatformServices.GlobalHotkey</c>.</summary>
    public GlobalHotkeyService GlobalHotkey { get; }

    /// <summary>True when this process is the running NimPad (always true with <c>--no-single-instance</c>).</summary>
    public bool IsPrimaryInstance { get; private set; }

    /// <summary>
    /// Decides whether this launch runs. The first launch becomes the primary instance and starts listening at once
    /// (files forwarded before <see cref="Attach"/> wait in a queue). A later launch sends its files, resolved
    /// against its own current directory, to the primary (which comes to the front unless this launch has
    /// <c>--background</c>) and returns <see langword="false"/>: shut down without showing a window. If the primary
    /// does not answer and has meanwhile exited, this launch takes over instead.
    /// </summary>
    public bool TryBecomePrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimaryInstance)
        {
            return true;
        }

        if (!Options.SingleInstance)
        {
            IsPrimaryInstance = true;
            return true;
        }

        try
        {
            _instance ??= new SingleInstance(Options.InstanceName ?? SingleInstance.GetDefaultName(CommandLine.DataDirectory));
            if (!_instance.TryAcquire())
            {
                var message = new SingleInstanceMessage(CommandLine.ResolveFiles(_launchDirectory), Activate: !CommandLine.Background);
                if (_instance.TryForward(message, Options.ForwardTimeout))
                {
                    Trace.TraceInformation($"Forwarded {message.Files.Count} file(s) to the running NimPad.");
                    return false;
                }

                // The primary did not answer: if it has exited meanwhile, take over; otherwise give up quietly.
                if (!_instance.TryAcquire())
                {
                    Trace.TraceError("NimPad is already running but did not respond; this launch exits.");
                    return false;
                }
            }

            _instance.StartServer(OnMessageReceived);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            // The check itself failed (for example no SID): running twice is better than not running.
            Trace.TraceError($"The single-instance check failed; NimPad runs without it: {ex}");
        }

        IsPrimaryInstance = true;
        return true;
    }

    /// <summary>
    /// Connects the integration to the shown main window and the shell: the global shortcut brings the window
    /// forward, forwarded launches open their files, external files are watched, and the restart is registered. Call
    /// it on the UI thread after <c>ShellViewModel.InitializeAsync</c> completed (calling it earlier is safe: the parts
    /// that need the initialized shell wait for it).
    /// </summary>
    public void Attach(Window mainWindow, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(shell);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null)
        {
            throw new InvalidOperationException("The integration is already attached.");
        }

        _window = mainWindow;
        _shell = shell;
        if (Options.RegisterRestart)
        {
            RestartRegistration.Register(CommandLine.BuildRestartArguments(_launchDirectory));
        }

        if (shell.IsInitialized)
        {
            Start();
        }
        else
        {
            shell.PropertyChanged += OnShellPropertyChanged;
        }
    }

    /// <summary>Brings the main window forward and focuses the editor (the global shortcut, a forwarded launch).</summary>
    public void BringToFront()
    {
        if (_window is not { } window || _disposed)
        {
            return;
        }

        try
        {
            WindowActivator.BringToFront(window);
            _dispatcher.InvokeAsync(
                () =>
                {
                    if (Options.FocusEditor is { } focus)
                    {
                        focus(window);
                    }
                    else
                    {
                        WindowActivator.FocusEditor(window);
                    }
                },
                DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Bringing NimPad to the front failed: {ex}");
        }
    }

    /// <summary>Stops listening, releases the single-instance mutex, unregisters the shortcut and stops the file watchers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_shell is { } shell)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
        }

        GlobalHotkey.Pressed -= OnHotkeyPressed;
        Run(() => _watcher?.Dispose(), "stop the file watchers");
        Run(GlobalHotkey.Dispose, "unregister the global shortcut");
        Run(() => _instance?.Dispose(), "stop the single-instance pipe");
        _watcher = null;
        _instance = null;

        static void Run(Action action, string description)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Could not {description}: {ex.Message}");
            }
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsInitialized) && _shell is { IsInitialized: true } shell)
        {
            shell.PropertyChanged -= OnShellPropertyChanged;
            Start();
        }
    }

    // On the UI thread, once the shell is initialized.
    private void Start()
    {
        if (_started || _disposed || _shell is not { } shell)
        {
            return;
        }

        _started = true;
        if (Options.WatchExternalFiles)
        {
            try
            {
                _watcher = new ExternalFileWatcher(shell);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Watching external files failed to start: {ex}");
            }
        }

        DeliverPending();
    }

    // On the pipe's task: queue, then hand over to the UI thread.
    private void OnMessageReceived(SingleInstanceMessage message)
    {
        _inbox.Enqueue(message);
        _dispatcher.InvokeAsync(DeliverPending, DispatcherPriority.Normal);
    }

    private void DeliverPending()
    {
        if (_disposed || !_started || _shell is not { } shell || shell.IsShuttingDown)
        {
            return;
        }

        while (_inbox.TryDequeue(out var message))
        {
            try
            {
                shell.OpenFiles(message.Files);
                if (message.Activate)
                {
                    BringToFront();
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Opening forwarded files failed: {ex}");
            }
        }
    }

    /// <summary>The global shortcut was pressed (internal for tests).</summary>
    internal void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // Pressing the current shortcut while Settings captures a new one keeps it (Windows takes the keys first).
        if (_shell?.Settings is { IsCapturingShortcut: true } settings)
        {
            settings.CompleteShortcutCapture(settings.GlobalShortcut);
            return;
        }

        BringToFront();
    }
}
