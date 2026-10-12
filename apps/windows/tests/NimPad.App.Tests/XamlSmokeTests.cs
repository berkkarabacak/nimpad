using NimPad.App.Platform;
using NimPad.App.Shell;
using NimPad.App.Tests.Shell;
using NimPad.App.Tests.Themes;
using NimPad.App.Theming;
using NimPad.Presentation.Shell;
using Xunit;

namespace NimPad.App.Tests;

public class XamlSmokeTests
{
    [Fact]
    public void MainWindowConstructsOnSta()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs only on Windows.");
        Sta.Run(() =>
        {
            // Without App.xaml resources: the window only uses DynamicResource for design-system keys.
            using var watcher = new SystemThemeWatcher(() => false, listen: false);
            var dialogs = new DialogService();
            var shell = new ShellViewModel(ShellTestWindow.CreatePlatform(dialogs, watcher));
            var window = new MainWindow(shell, new MainWindowOptions { Dialogs = dialogs });
            ThemeService.InstallDesignSystem(window.Resources, dark: false);
            Assert.Same(shell, window.DataContext);
            Assert.Equal("NimPad", window.Title);
        });
    }
}
