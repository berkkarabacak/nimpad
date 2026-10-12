using NimPad.App.Integration;
using NimPad.App.Tests.Themes;
using NimPad.Presentation.Integration;
using Xunit;

namespace NimPad.App.Tests.Integration;

public sealed class RestartRegistrationTests
{
    [Fact]
    public void RegistersTheRestartArgumentsAndUnregisters()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);
        var arguments = AppCommandLine.Parse(["--data-dir", @"C:\Temp\nimpad data", "--no-hotkey"]).BuildRestartArguments(@"C:\");
        Assert.Equal("--restarted --data-dir \"C:\\Temp\\nimpad data\" --no-hotkey", arguments);
        try
        {
            Assert.True(RestartRegistration.Register(arguments));
        }
        finally
        {
            // Never leave the test host registered for a restart.
            Assert.True(RestartRegistration.Unregister());
        }
    }

    [Fact]
    public void RefusesArgumentsWindowsCannotStore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), Sta.WindowsOnly);

        Assert.False(RestartRegistration.Register(new string('x', RestartRegistration.MaxArgumentsLength)));
    }
}
