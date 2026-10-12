using System.IO;
using NimPad.App.Shell;
using Xunit;

namespace NimPad.App.Tests.Shell;

public class WindowPlacementStoreTests
{
    [Fact]
    public void RoundTripsTheBounds()
    {
        using var data = new TempDataDir();
        var store = new WindowPlacementStore(Path.Combine(data.Path, "profile", WindowPlacementStore.FileName));
        Assert.Null(store.Load());
        var bounds = new WindowBounds(100, 80, 1220, 800, IsMaximized: true);
        Assert.True(store.Save(bounds));
        Assert.Equal(bounds, store.Load());
        Assert.DoesNotContain("IsValid", File.ReadAllText(store.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresDamagedOrTinyBounds()
    {
        using var data = new TempDataDir();
        var path = Path.Combine(data.Path, WindowPlacementStore.FileName);
        var store = new WindowPlacementStore(path);
        File.WriteAllText(path, "{ not json");
        Assert.Null(store.Load());
        Assert.False(store.Save(new WindowBounds(0, 0, 10, 10, IsMaximized: false)));
        File.WriteAllText(path, """{"Left":0,"Top":0,"Right":20,"Bottom":20,"IsMaximized":false}""");
        Assert.Null(store.Load());
    }

    [Fact]
    public void AppliesAndCapturesThroughTheWindowHandle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), UiThread.WindowsOnly);
        UiThread.Run(async () =>
        {
            var window = new System.Windows.Window { ShowActivated = false, ShowInTaskbar = false, Width = 500, Height = 400 };
            var applied = false;
            var bounds = new WindowBounds(120, 140, 920, 740, IsMaximized: false);
            window.SourceInitialized += (_, _) => applied = WindowPlacementStore.Apply(window, bounds);
            window.Show();
            try
            {
                await UiThread.SettleAsync();
                Assert.True(applied);
                var captured = WindowPlacementStore.Capture(window);
                Assert.Equal(bounds, captured);

                // Bounds on no monitor at all are ignored.
                Assert.False(WindowPlacementStore.Apply(window, new WindowBounds(-90000, -90000, -89000, -89000, IsMaximized: false)));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
