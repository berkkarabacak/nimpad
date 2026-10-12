using System.IO;
using Microsoft.Win32;
using NimPad.App.Integration;
using NimPad.App.Tests.Themes;
using Xunit;

namespace NimPad.App.Tests.Integration;

/// <summary>The real HKCU Run key, under a test-only value name (the real "NimPad" value is never touched).</summary>
public sealed class AutostartServiceTests
{
    [Fact]
    public void TurningOnWritesTheRunValueAndTurningOffRemovesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = TestValueName();
        var exe = Path.Combine(Path.GetTempPath(), "Portable NimPad", "NimPad.exe");
        var service = new AutostartService(exe, name);
        try
        {
            Assert.False(service.IsEnabled);

            service.Set(true);

            Assert.Equal($"\"{exe}\" --background", ReadRun(name));
            Assert.True(service.IsEnabled);

            service.Set(false);

            Assert.Null(ReadRun(name));
            Assert.False(service.IsEnabled);
            service.Set(false);
            Assert.False(service.IsEnabled);
        }
        finally
        {
            Cleanup(name);
        }
    }

    [Fact]
    public void AnEntryForAnotherCopyOrTurnedOffInTaskManagerCountsAsOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var name = TestValueName();
        var exe = Path.Combine(Path.GetTempPath(), "NimPad", "NimPad.exe");
        var service = new AutostartService(exe, name);
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(AutostartService.RunKeyPath, writable: true))
            {
                run.SetValue(name, "\"C:\\Old place\\NimPad.exe\" --background");
            }

            Assert.False(service.IsEnabled);

            service.Set(true);
            using (var approved = Registry.CurrentUser.CreateSubKey(AutostartService.StartupApprovedKeyPath, writable: true))
            {
                approved.SetValue(name, new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            }

            Assert.False(service.IsEnabled);

            // Turning it on in NimPad clears Task Manager's mark, so the switch means what it shows.
            service.Set(true);
            Assert.True(service.IsEnabled);
            using (var approved = Registry.CurrentUser.OpenSubKey(AutostartService.StartupApprovedKeyPath))
            {
                Assert.Null(approved?.GetValue(name));
            }
        }
        finally
        {
            Cleanup(name);
        }
    }

    [Fact]
    public void TheDefaultServiceTargetsThisExecutableUnderTheNameNimPad()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var service = new AutostartService();

        Assert.Equal(AutostartService.DefaultValueName, service.ValueName);
        Assert.Equal("NimPad", service.ValueName);
        Assert.True(Path.IsPathFullyQualified(service.ExecutablePath));
        Assert.EndsWith(".exe", service.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"\"{service.ExecutablePath}\" --background", service.Command);
    }

    private static string TestValueName() => "NimPadTest-" + Guid.NewGuid().ToString("N");

    private static string? ReadRun(string name)
    {
        using var run = Registry.CurrentUser.OpenSubKey(AutostartService.RunKeyPath);
        return run?.GetValue(name) as string;
    }

    private static void Cleanup(string name)
    {
        using (var run = Registry.CurrentUser.OpenSubKey(AutostartService.RunKeyPath, writable: true))
        {
            run?.DeleteValue(name, throwOnMissingValue: false);
        }

        using var approved = Registry.CurrentUser.OpenSubKey(AutostartService.StartupApprovedKeyPath, writable: true);
        approved?.DeleteValue(name, throwOnMissingValue: false);
    }
}
