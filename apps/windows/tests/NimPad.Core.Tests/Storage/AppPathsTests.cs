using NimPad.Core.Storage;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Storage;

public class AppPathsTests
{
    private static readonly string ExeDir = Path.Combine(Path.GetTempPath(), "nimpad-app");
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "localappdata");

    [Fact]
    public void DefaultsToLocalAppData()
    {
        var paths = AppPaths.Resolve(null, ExeDir, LocalAppData, _ => false);

        Assert.Equal(DataRootSource.LocalAppData, paths.Source);
        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine(LocalAppData, "NimPad"), paths.DataRoot);
        Assert.Equal(Path.Combine(LocalAppData, "NimPad", "profiles", "local"), paths.ProfileDir);
        Assert.Equal(Path.Combine(LocalAppData, "NimPad", "profiles", "local", "nimpad.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(LocalAppData, "NimPad", "profiles", "local", "backups"), paths.BackupsDir);
        Assert.Equal(Path.Combine(LocalAppData, "NimPad", "logs"), paths.LogsDir);
    }

    [Fact]
    public void PortableMarkerNextToExecutableSelectsDataFolder()
    {
        var checkedPaths = new List<string>();
        var paths = AppPaths.Resolve(null, ExeDir, LocalAppData, path =>
        {
            checkedPaths.Add(path);
            return path == Path.Combine(ExeDir, "portable.txt");
        });

        Assert.Equal(DataRootSource.Portable, paths.Source);
        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(ExeDir, "data"), paths.DataRoot);
        Assert.Equal(Path.Combine(ExeDir, "data", "profiles", "local", "nimpad.db"), paths.DatabasePath);
        Assert.Equal([Path.Combine(ExeDir, "portable.txt")], checkedPaths);
    }

    [Fact]
    public void OverrideWinsOverPortableMode()
    {
        var overrideDir = Path.Combine(Path.GetTempPath(), "nimpad-override");
        var paths = AppPaths.Resolve(overrideDir, ExeDir, LocalAppData, _ => true);

        Assert.Equal(DataRootSource.Override, paths.Source);
        Assert.Equal(overrideDir, paths.DataRoot);
        Assert.Equal(Path.Combine(overrideDir, "profiles", "local", "backups"), paths.BackupsDir);
    }

    [Fact]
    public void RelativeOverrideIsMadeAbsoluteAndBlankIsIgnored()
    {
        var relative = AppPaths.Resolve("test-data", ExeDir, LocalAppData, _ => false);
        Assert.Equal(Path.GetFullPath("test-data"), relative.DataRoot);
        Assert.True(Path.IsPathRooted(relative.DataRoot));

        var blank = AppPaths.Resolve("   ", ExeDir, LocalAppData, _ => false);
        Assert.Equal(DataRootSource.LocalAppData, blank.Source);
    }

    [Fact]
    public void UsesTheRealFileSystemByDefault()
    {
        using var temp = new TempDirectory();
        Assert.Equal(DataRootSource.LocalAppData, AppPaths.Resolve(null, temp.Path, LocalAppData).Source);

        File.WriteAllText(temp.Combine("portable.txt"), string.Empty);
        var paths = AppPaths.Resolve(null, temp.Path, LocalAppData);
        Assert.Equal(DataRootSource.Portable, paths.Source);

        paths.EnsureDirectories();
        Assert.True(Directory.Exists(paths.ProfileDir));
        Assert.True(Directory.Exists(paths.BackupsDir));
        Assert.True(Directory.Exists(paths.LogsDir));
    }

    [Fact]
    public void ResolvesForTheCurrentProcess()
    {
        var paths = AppPaths.ResolveForCurrentProcess(null);
        Assert.True(Path.IsPathRooted(paths.DataRoot));
        Assert.EndsWith(Path.Combine("profiles", "local", "nimpad.db"), paths.DatabasePath);
    }
}
