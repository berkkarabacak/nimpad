using System.Windows;
using System.Windows.Media;
using NimPad.App.Themes;
using NimPad.App.Theming;
using Xunit;
using ThemeMode = NimPad.App.Theming.ThemeMode;

namespace NimPad.App.Tests.Themes;

/// <summary>ThemeService keeps the token set first in the merged dictionaries and swaps it in place.</summary>
public class ThemeServiceTests
{
    [Fact]
    public void ApplySwapsTheFirstMergedDictionaryAndKeepsTheOthers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var resources = new ResourceDictionary();
            var other = new ResourceDictionary();
            resources.MergedDictionaries.Add(other);
            using var watcher = new SystemThemeWatcher(() => false, listen: false);
            using var service = new ThemeService(resources, watcher) { UpdateWindowFrames = false };
            var changes = 0;
            service.ThemeChanged += (_, _) => changes++;

            service.Apply(ThemeMode.Light);
            Assert.IsType<TokensLight>(resources.MergedDictionaries[0]);
            Assert.Same(other, resources.MergedDictionaries[1]);
            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.False(service.IsDark);
            Assert.Equal(1, changes);
            Assert.Equal(Accent(dark: false), resources["AccentColor"]);

            service.Apply(ThemeMode.Dark);
            Assert.IsType<TokensDark>(resources.MergedDictionaries[0]);
            Assert.Same(other, resources.MergedDictionaries[1]);
            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.True(service.IsDark);
            Assert.Equal(ThemeMode.Dark, service.Mode);
            Assert.Equal(2, changes);
            Assert.Equal(Accent(dark: true), resources["AccentColor"]);
            Assert.Equal("Dark", resources["ThemeName"]);

            service.Apply(ThemeMode.Dark);
            Assert.Equal(2, changes);

            service.Apply(ThemeMode.Light);
            Assert.IsType<TokensLight>(resources.MergedDictionaries[0]);
            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.Equal(3, changes);
        });
    }

    [Fact]
    public void ApplyReplacesATokenSetMergedByXaml()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var resources = new ResourceDictionary();
            ThemeService.InstallDesignSystem(resources, dark: false);
            Assert.IsType<TokensLight>(resources.MergedDictionaries[0]);
            Assert.IsType<DesignSystem>(resources.MergedDictionaries[1]);

            using var watcher = new SystemThemeWatcher(() => true, listen: false);
            using var service = new ThemeService(resources, watcher) { UpdateWindowFrames = false };
            service.Apply(ThemeMode.System);
            Assert.IsType<TokensDark>(resources.MergedDictionaries[0]);
            Assert.IsType<DesignSystem>(resources.MergedDictionaries[1]);
            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.IsType<Style>(resources["PrimaryButton"]);
        });
    }

    [Fact]
    public void SystemModeFollowsWindowsUntilAnotherModeIsChosen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var systemDark = false;
            var resources = new ResourceDictionary();
            using var watcher = new SystemThemeWatcher(() => systemDark, listen: false);
            using var service = new ThemeService(resources, watcher) { UpdateWindowFrames = false };
            var changes = 0;
            service.ThemeChanged += (_, _) => changes++;

            service.Apply(ThemeMode.System);
            Assert.False(service.IsDark);

            systemDark = true;
            watcher.Refresh();
            Assert.True(watcher.IsDarkMode);
            Assert.True(service.IsDark);
            Assert.IsType<TokensDark>(resources.MergedDictionaries[0]);
            Assert.Equal(2, changes);

            service.Apply(ThemeMode.Light);
            systemDark = false;
            watcher.Refresh();
            systemDark = true;
            watcher.Refresh();
            Assert.False(service.IsDark);
            Assert.IsType<TokensLight>(resources.MergedDictionaries[0]);
        });
    }

    [Fact]
    public void DisposeStopsFollowingWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Sta.Run(() =>
        {
            var systemDark = false;
            var resources = new ResourceDictionary();
            using var watcher = new SystemThemeWatcher(() => systemDark, listen: false);
            var service = new ThemeService(resources, watcher) { UpdateWindowFrames = false };
            service.Apply(ThemeMode.System);
            service.Dispose();

            systemDark = true;
            watcher.Refresh();
            Assert.IsType<TokensLight>(resources.MergedDictionaries[0]);
            Assert.Throws<ObjectDisposedException>(() => service.Apply(ThemeMode.Dark));
        });
    }

    [Fact]
    public void WatcherReadsTheRegistryWithoutThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);

        // Whatever the machine's setting is, reading it must not throw (missing key = light).
        _ = SystemThemeWatcher.ReadSystemIsDark();
        Sta.Run(() =>
        {
            using var watcher = new SystemThemeWatcher();
            Assert.Equal(SystemThemeWatcher.ReadSystemIsDark(), watcher.IsDarkMode);
        });
    }

    [Fact]
    public void ImmersiveDarkModeIsANoOpWithoutAWindowHandle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        Assert.False(WindowThemeHelper.ApplyImmersiveDarkMode(IntPtr.Zero, dark: true));
    }

    private static Color Accent(bool dark) =>
        TokenTests.ParseToken(dark ? TokenTests.Expected["Accent"].Dark : TokenTests.Expected["Accent"].Light);
}
