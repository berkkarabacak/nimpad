using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using NimPad.App.Platform;
using NimPad.App.Services;
using NimPad.App.Shell;
using NimPad.App.Theming;
using NimPad.Core.Storage;
using NimPad.Presentation.Platform;
using NimPad.Presentation.Shell;
using AppThemeMode = NimPad.App.Theming.ThemeMode;

namespace NimPad.App;

/// <summary>
/// The composition root: parses the command line (<see cref="StartupArgs"/>), starts the log, applies the theme before
/// any window shows, builds the <see cref="ShellViewModel"/> over the WPF platform services and the main window, and
/// runs <see cref="ShellStartup"/> (the window appears at once; the data loads without blocking the UI thread). Every
/// step is guarded: a failure becomes an error banner (or, before a window exists, a message box) instead of a crash.
/// Crashes and session end flush the notes (<see cref="ShellViewModel.EmergencyFlush"/>); exit waits, bounded, for
/// <see cref="ShellViewModel.ShutdownAsync"/>.
/// </summary>
/// <remarks>
/// Other features plug in without editing this file: implement the <see cref="ConfigureIntegrations"/> partial method in
/// another <c>partial class App</c> file and fill <see cref="AppIntegrations"/> (single instance and activation, the
/// global shortcut, start with Windows, the Settings window).
/// </remarks>
public partial class App : Application
{
    private const int MaxRecoverableErrors = 5;
    private static readonly TimeSpan RecoverableErrorWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitSaveTimeout = TimeSpan.FromSeconds(8);

    private readonly AppIntegrations _integrations = new();
    private readonly Queue<DateTime> _recentErrors = new();
    private FileLog? _log;
    private FileLog.TraceListener? _traceListener;
    private ThemeService? _themes;
    private SystemThemeWatcher? _ownWatcher;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private int _handlingError;

    /// <summary>The parsed command line.</summary>
    public StartupArgs Arguments { get; private set; } = StartupArgs.Empty;

    /// <summary>The integrations in use (filled by <see cref="ConfigureIntegrations"/>).</summary>
    public AppIntegrations Integrations => _integrations;

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookUnhandledExceptions();
        Arguments = StartupArgs.Parse(e.Args);
        try
        {
            ConfigureIntegrations(_integrations);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"Configuring the app integrations failed: {ex}");
        }

        if (!Arguments.NoSingleInstance && _integrations.Activation is { } activation)
        {
            try
            {
                if (!activation.TryBecomePrimaryInstance(Arguments))
                {
                    // The running NimPad took the arguments.
                    Shutdown(0);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.TraceError($"The single-instance check failed; starting normally: {ex}");
            }
        }

        var paths = TryResolvePaths();
        StartLog(paths);
        _themes = CreateThemeService();

        var dialogs = new DialogService();
        try
        {
            _shell = new ShellViewModel(CreatePlatform(dialogs));
            _window = new MainWindow(_shell, new MainWindowOptions
            {
                Themes = _themes,
                Dialogs = dialogs,
                Placement = paths is null ? null : WindowPlacementStore.ForProfile(paths),
                SettingsWindow = _integrations.SettingsWindow,
            });
            MainWindow = _window;
        }
        catch (Exception ex)
        {
            FileLog.Error("The main window could not be created.", ex);
            ShowFatalMessage("NimPad couldn't start.", ex);
            Shutdown(1);
            return;
        }

        try
        {
            _integrations.Activation?.Attach(_window, _shell);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("Attaching the activation integration failed.", ex);
        }

        // A background start (with Windows) stays hidden only when something can bring the window forward later.
        var show = !(Arguments.Background && _integrations.Activation is not null);
        _ = StartAsync(_window, show);
    }

    /// <inheritdoc/>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        FileLog.Info($"Windows is ending the session ({e.ReasonSessionEnding}); saving.");
        Flush();
    }

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_shell is { } shell && _window is not { IsCloseConfirmed: true })
            {
                // The window did not close through its own path (session end, Shutdown()): save, bounded.
                if (!RunWithTimeout(shell.ShutdownAsync(), ExitSaveTimeout))
                {
                    FileLog.Warn("Saving at exit did not finish in time; flushing what can be flushed.");
                    shell.EmergencyFlush();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("Saving at exit failed.", ex);
            _shell?.EmergencyFlush();
        }

        try
        {
            _integrations.Activation?.Detach();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Warn("Detaching the activation integration failed.", ex);
        }

        _themes?.Dispose();
        _ownWatcher?.Dispose();
        FileLog.Info($"Exited with code {e.ApplicationExitCode}.");
        if (_traceListener is not null)
        {
            Trace.Listeners.Remove(_traceListener);
        }

        _log?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Fills <paramref name="integrations"/>; implemented by the features that plug into the shell.</summary>
    partial void ConfigureIntegrations(AppIntegrations integrations);

    private static void ShowFatalMessage(string title, Exception error)
    {
        try
        {
            MessageBox.Show(
                $"{title}\n\n{error.Message}\n\nDetails are in the log folder.",
                "NimPad",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"The error message could not be shown: {ex}");
        }
    }

    private static bool IsRecoverable(Exception error) =>
        error is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException
            or SEHException or InvalidProgramException or BadImageFormatException or TypeInitializationException);

    private async Task StartAsync(MainWindow window, bool show)
    {
        try
        {
            await ShellStartup.RunAsync(window, new StartupOptions { DataDirectory = Arguments.DataDirectory, FilesToOpen = Arguments.Files }, show);
        }
        catch (Exception ex)
        {
            FileLog.Error("Starting NimPad failed.", ex);
            if (!window.IsVisible)
            {
                ShowFatalMessage("NimPad couldn't start.", ex);
                Shutdown(1);
            }
            else
            {
                window.ShowStartupFailure(ex);
            }
        }
    }

    private AppPaths? TryResolvePaths()
    {
        try
        {
            return AppPaths.ResolveForCurrentProcess(Arguments.DataDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException or System.Security.SecurityException)
        {
            Trace.TraceError($"The data folder could not be resolved: {ex}");
            return null;
        }
    }

    private void StartLog(AppPaths? paths)
    {
        if (paths is null)
        {
            return;
        }

        try
        {
            _log = new FileLog(paths.LogsDir);
            FileLog.Current = _log;
            _traceListener = new FileLog.TraceListener(_log);
            Trace.Listeners.Add(_traceListener);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"The log could not be started: {ex}");
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        FileLog.Info(
            $"NimPad {version} starting on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}); " +
            $"data {paths.Source}{(paths.Source == DataRootSource.Override ? " " + paths.DataRoot : string.Empty)}; " +
            $"single instance {(Arguments.NoSingleInstance ? "off" : "on")}, global shortcut {(Arguments.NoHotkey ? "off" : "on")}, " +
            $"background {(Arguments.Background ? "yes" : "no")}, {Arguments.Files.Count} file(s) to open.");
        foreach (var problem in Arguments.Problems)
        {
            FileLog.Warn(problem);
        }

        if (Arguments.UnknownOptions.Count > 0)
        {
            FileLog.Warn("Ignored unknown options: " + string.Join(" ", Arguments.UnknownOptions));
        }
    }

    private ThemeService? CreateThemeService()
    {
        try
        {
            var themes = new ThemeService();

            // The saved theme is applied as soon as the settings are read (before the window shows when they load in
            // time); until then follow Windows.
            themes.Apply(AppThemeMode.System);
            return themes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("The theme service could not start; the light theme stays.", ex);
            return null;
        }
    }

    private PlatformServices CreatePlatform(DialogService dialogs)
    {
        var dispatcher = Dispatcher;
        SystemThemeWatcher watcher;
        if (_themes is not null)
        {
            watcher = _themes.SystemTheme;
        }
        else
        {
            watcher = new SystemThemeWatcher();
            _ownWatcher = watcher;
        }

        return new PlatformServices
        {
            Dispatcher = new WpfUiDispatcher(dispatcher),
            Clipboard = new ClipboardService(dispatcher),
            FileDialogs = new FileDialogService(),
            Dialogs = dialogs,
            SystemTheme = new SystemThemeProvider(watcher),
            Autostart = CreateAutostart(),
            GlobalHotkey = CreateGlobalHotkey(),
            Fonts = new FontCatalog(),
            Shell = new ShellLauncher(),
        };
    }

    private IGlobalHotkeyService CreateGlobalHotkey()
    {
        if (Arguments.NoHotkey)
        {
            return new DisabledGlobalHotkeyService("--no-hotkey was passed");
        }

        try
        {
            if (_integrations.GlobalHotkey?.Invoke(Arguments) is { } service)
            {
                return service;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("The global shortcut service could not be created.", ex);
        }

        return new DisabledGlobalHotkeyService("no global shortcut service is configured in this build");
    }

    private IAutostartService CreateAutostart()
    {
        try
        {
            if (_integrations.Autostart?.Invoke() is { } service)
            {
                return service;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Error("The start-with-Windows service could not be created.", ex);
        }

        return new UnavailableAutostartService();
    }

    // ------------------------------------------------------------------ crashes

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Error("Unhandled exception on the UI thread.", e.Exception);
        if (Interlocked.Exchange(ref _handlingError, 1) == 1)
        {
            // An error while handling an error: let it end the process (the notes were just flushed).
            return;
        }

        try
        {
            Flush();
            if (IsRecoverable(e.Exception) && !TooManyRecentErrors())
            {
                e.Handled = true;
                _window?.ShowUnexpectedError();
            }
            else
            {
                FileLog.Error("The error is not recoverable; NimPad closes.");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Handling an unhandled exception failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _handlingError, 0);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        FileLog.Error($"Unhandled exception (terminating: {e.IsTerminating}).", e.ExceptionObject as Exception);
        Flush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLog.Error("A background task failed without being observed.", e.Exception);
        e.SetObserved();
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (TooManyRecentErrors())
            {
                return;
            }

            Flush();
            _window?.ShowUnexpectedError();
        }));
    }

    private bool TooManyRecentErrors()
    {
        var now = DateTime.UtcNow;
        lock (_recentErrors)
        {
            _recentErrors.Enqueue(now);
            while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > RecoverableErrorWindow)
            {
                _recentErrors.Dequeue();
            }

            return _recentErrors.Count > MaxRecoverableErrors;
        }
    }

    // Best effort, bounded: on the UI thread directly; from another thread through the dispatcher, or directly when the
    // UI thread does not respond.
    private void Flush()
    {
        if (_shell is not { } shell)
        {
            return;
        }

        try
        {
            if (Dispatcher.CheckAccess() || Dispatcher.HasShutdownFinished)
            {
                shell.EmergencyFlush();
                return;
            }

            var operation = Dispatcher.InvokeAsync(() => shell.EmergencyFlush(), DispatcherPriority.Send);
            if (operation.Wait(TimeSpan.FromSeconds(4)) != DispatcherOperationStatus.Completed && operation.Abort())
            {
                // The UI thread never got to it: flush from here.
                shell.EmergencyFlush();
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("The emergency flush failed.", ex);
        }
    }

    // Waits for a task while the dispatcher keeps running (its continuations need the UI thread), at most `timeout`.
    private bool RunWithTimeout(Task task, TimeSpan timeout)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(timeout, DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher);
            task.ContinueWith(_ => frame.Continue = false, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
            }
        }

        if (task.IsFaulted)
        {
            FileLog.Error("Saving at exit failed.", task.Exception);
        }

        return task.IsCompletedSuccessfully;
    }
}
