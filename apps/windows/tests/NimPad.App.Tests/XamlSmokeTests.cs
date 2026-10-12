using Xunit;

namespace NimPad.App.Tests;

public class XamlSmokeTests
{
    [Fact]
    public void MainWindowConstructsOnSta()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WPF runs only on Windows.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
