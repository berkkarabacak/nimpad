using System.Diagnostics;
using NimPad.App.Services;
using NimPad.Presentation.Shell;

namespace NimPad.App.Shell;

/// <summary>
/// The startup path of the main window, shared by <c>App</c> and the UI tests: start
/// <see cref="ShellViewModel.InitializeAsync"/> (database, settings, session) without blocking the UI thread, give it a
/// short moment so the window can appear with its theme and tabs already in place, show the window (whatever happens,
/// never a splash), then wait for the data; a failure becomes an error banner in the running window.
/// </summary>
public static class ShellStartup
{
    /// <summary>How long the window waits for the data before it shows anyway.</summary>
    public static readonly TimeSpan DefaultShowDeadline = TimeSpan.FromMilliseconds(400);

    /// <summary>Runs the startup path for <paramref name="window"/>.</summary>
    /// <param name="window">The main window (created, not shown).</param>
    /// <param name="options">What to open (data folder, files).</param>
    /// <param name="show">Whether to show the window (false for a background start).</param>
    /// <param name="showDeadline">How long to wait for the data before showing; <see cref="DefaultShowDeadline"/> by default.</param>
    /// <returns><see langword="true"/> when the data was opened; <see langword="false"/> after a startup failure (shown as a banner).</returns>
    public static async Task<bool> RunAsync(MainWindow window, StartupOptions options, bool show = true, TimeSpan? showDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);
        var clock = Stopwatch.StartNew();
        Task initialize;
        try
        {
            initialize = window.Shell.InitializeAsync(options);
        }
        catch (Exception ex)
        {
            initialize = Task.FromException(ex);
        }

        // Tabs and the right theme first: most profiles open well within the deadline.
        await Task.WhenAny(initialize, Task.Delay(showDeadline ?? DefaultShowDeadline));
        if (initialize.IsCompletedSuccessfully)
        {
            TryApplyTheme(window);
        }

        if (show)
        {
            try
            {
                window.Show();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FileLog.Error("The main window could not be shown.", ex);
                throw;
            }
        }

        try
        {
            await initialize;
            TryApplyTheme(window);
            FileLog.Info($"Started in {clock.ElapsedMilliseconds} ms with {window.Shell.Tabs.Tabs.Count} tab(s).");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Error("NimPad could not open its data.", ex);
            window.ShowStartupFailure(ex);
            return false;
        }
    }

    private static void TryApplyTheme(MainWindow window)
    {
        try
        {
            window.ApplyThemeFromSettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FileLog.Warn("The theme setting could not be applied.", ex);
        }
    }
}
