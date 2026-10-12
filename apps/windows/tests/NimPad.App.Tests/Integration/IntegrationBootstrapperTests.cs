using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using NimPad.App.Integration;
using NimPad.App.Tests.Settings;
using NimPad.App.Tests.Themes;
using NimPad.Presentation.Integration;
using NimPad.Presentation.Shell;
using Xunit;

namespace NimPad.App.Tests.Integration;

/// <summary>
/// The bootstrapper end to end: a real shell over a temp data directory, a shown window, and a second "launch" on
/// its own thread forwarding over a uniquely named pipe. Nothing is registered with Windows (no hotkey, no restart).
/// </summary>
public sealed class IntegrationBootstrapperTests
{
    [Fact]
    public void ASecondLaunchOpensItsFilesInTheRunningNimPadAndExits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        using var data = new DataDirectory();
        var file = data.WriteFile("forwarded.txt", "hello");
        var options = TestOptions();

        StaAsync.Run(async () =>
        {
            using var primary = new IntegrationBootstrapper(AppCommandLine.Parse(["--data-dir", data.Path]), options);
            Assert.True(primary.TryBecomePrimaryInstance());
            Assert.True(primary.IsPrimaryInstance);
            var (shell, window) = await StartShellAsync(primary, data.Path);
            try
            {
                primary.Attach(window, shell);

                var secondLaunch = await OnOwnThreadAsync(() =>
                {
                    using var secondary = new IntegrationBootstrapper(AppCommandLine.Parse(["--data-dir", data.Path, file]), options);
                    return secondary.TryBecomePrimaryInstance();
                });

                Assert.False(secondLaunch);
                await StaAsync.WaitUntilAsync(
                    () => shell.Tabs.SelectedTab?.FilePath == file,
                    "the forwarded file never opened");
                Assert.Single(shell.Tabs.Tabs, tab => tab.FilePath == file);
            }
            finally
            {
                await shell.ShutdownAsync();
                window.Close();
            }
        });
    }

    [Fact]
    public void FilesForwardedBeforeTheShellIsReadyOpenOnceItIs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        using var data = new DataDirectory();
        var file = data.WriteFile("early.md", "# early");
        var options = TestOptions();

        StaAsync.Run(async () =>
        {
            using var primary = new IntegrationBootstrapper(AppCommandLine.Parse(["--data-dir", data.Path]), options);
            Assert.True(primary.TryBecomePrimaryInstance());

            // The pipe listens from the start; the message waits until Attach and the initialized shell.
            Assert.False(await OnOwnThreadAsync(() =>
            {
                using var secondary = new IntegrationBootstrapper(AppCommandLine.Parse([file, "--background"]), options);
                return secondary.TryBecomePrimaryInstance();
            }));

            var platform = new TestPlatform(primary.GlobalHotkey);
            var shell = new ShellViewModel(platform.Services);
            var window = OffscreenWindow();
            window.Show();
            try
            {
                primary.Attach(window, shell);
                await Task.Delay(200);
                Assert.Empty(shell.Tabs.Tabs);

                await shell.InitializeAsync(new StartupOptions { DataDirectory = data.Path });
                await StaAsync.WaitUntilAsync(() => shell.Tabs.Tabs.Any(tab => tab.FilePath == file), "the early file never opened");
            }
            finally
            {
                await shell.ShutdownAsync();
                window.Close();
            }
        });
    }

    [Fact]
    public void TheGlobalShortcutBringsNimPadForwardOrCompletesARunningCapture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        using var data = new DataDirectory();
        var focusRequests = 0;
        var options = TestOptions() with { SingleInstance = false, FocusEditor = _ => focusRequests++ };

        StaAsync.Run(async () =>
        {
            using var primary = new IntegrationBootstrapper(AppCommandLine.Parse(["--data-dir", data.Path, "--no-single-instance"]), options);
            Assert.True(primary.TryBecomePrimaryInstance());
            var (shell, window) = await StartShellAsync(primary, data.Path);
            try
            {
                primary.Attach(window, shell);
                Assert.Equal("Ctrl+Alt+N", primary.GlobalHotkey.Gesture);

                window.WindowState = WindowState.Minimized;
                primary.OnHotkeyPressed(primary.GlobalHotkey, EventArgs.Empty);
                await StaAsync.WaitUntilAsync(() => focusRequests == 1, "the editor was not focused");
                Assert.Equal(WindowState.Normal, window.WindowState);

                // While Settings captures a new shortcut, Windows delivers the current one as the hotkey.
                shell.Settings.BeginShortcutCapture();
                primary.OnHotkeyPressed(primary.GlobalHotkey, EventArgs.Empty);
                Assert.False(shell.Settings.IsCapturingShortcut);
                Assert.Equal("Ctrl+Alt+N", shell.Settings.GlobalShortcut);
                await Task.Delay(100);
                Assert.Equal(1, focusRequests);
            }
            finally
            {
                await shell.ShutdownAsync();
                window.Close();
            }
        });
    }

    [Fact]
    public void WithoutSingleInstanceEveryLaunchRuns()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var options = TestOptions() with { SingleInstance = false };
        Sta.Run(() =>
        {
            using var first = new IntegrationBootstrapper(AppCommandLine.Parse(["--no-single-instance"]), options);
            using var second = new IntegrationBootstrapper(AppCommandLine.Parse(["--no-single-instance"]), options);

            Assert.True(first.TryBecomePrimaryInstance());
            Assert.True(second.TryBecomePrimaryInstance());
            Assert.False(IntegrationOptions.FromCommandLine(AppCommandLine.Parse(["--no-single-instance", "--no-hotkey"])).SingleInstance);
            Assert.False(IntegrationOptions.FromCommandLine(AppCommandLine.Parse(["--no-hotkey"])).RegisterGlobalHotkey);
            Assert.True(IntegrationOptions.FromCommandLine(AppCommandLine.Parse([])).RegisterRestart);
        });
    }

    private static IntegrationOptions TestOptions() => new()
    {
        InstanceName = "NimPad-test-" + Guid.NewGuid().ToString("N"),
        RegisterGlobalHotkey = false,
        RegisterRestart = false,
        ForwardTimeout = TimeSpan.FromSeconds(15),
    };

    private static async Task<(ShellViewModel Shell, Window Window)> StartShellAsync(IntegrationBootstrapper integration, string dataDirectory)
    {
        var platform = new TestPlatform(integration.GlobalHotkey);
        var shell = new ShellViewModel(platform.Services);
        var window = OffscreenWindow();
        window.Show();
        await shell.InitializeAsync(new StartupOptions { DataDirectory = dataDirectory });
        return (shell, window);
    }

    private static Window OffscreenWindow() => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -20000,
        Top = -20000,
        Width = 800,
        Height = 600,
        ShowActivated = false,
        ShowInTaskbar = false,
    };

    // Another launch: its own thread (its own process in real life), so it has its own mutex ownership.
    private static Task<bool> OnOwnThreadAsync(Func<bool> launch)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(launch());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class DataDirectory : IDisposable
    {
        public DataDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nimpad-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string name, string text)
        {
            var folder = System.IO.Path.Combine(Path, "files");
            Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, name);
            File.WriteAllText(file, text);
            return file;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A file still in use; the temp folder is cleaned up eventually.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
