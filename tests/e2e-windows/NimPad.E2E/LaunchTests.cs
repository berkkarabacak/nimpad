using Xunit;

namespace NimPad.E2E;

public class LaunchTests
{
    [Fact]
    public void Placeholder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UI automation runs only on Windows.");
    }
}
