using NimPad.Core.Services;
using NimPad.Core.Tests.Support;
using Xunit;

namespace NimPad.Core.Tests.Services;

public class WorkspaceServiceTests
{
    [Fact]
    public async Task FirstRunCreatesMyWorkspaceAndTheDevice()
    {
        using var store = await TestStore.CreateAsync();

        Assert.Equal(WorkspaceService.DefaultWorkspaceName, store.Workspace.Name);
        Assert.Equal("My Workspace", store.Workspace.Name);
        Assert.Equal(TestStore.Start, store.Workspace.CreatedAt);
        Assert.Equal(7, store.Workspace.Id.Value.Version);
        Assert.Equal(1, await store.CountAsync("SELECT count(*) FROM device;"));
        Assert.NotNull(await store.Services.Workspaces.GetDeviceIdAsync());
    }

    [Fact]
    public async Task EnsureIsIdempotentAcrossCallsAndReopens()
    {
        using var temp = new TempDirectory();
        Guid? deviceId;
        NimPad.Core.Domain.Workspace first;
        using (var store = await TestStore.CreateAsync(temp))
        {
            first = store.Workspace;
            deviceId = await store.Services.Workspaces.GetDeviceIdAsync();
            Assert.Equal(first, await store.Services.Workspaces.EnsureDefaultWorkspaceAsync());
        }

        using var reopened = await TestStore.CreateAsync(temp);
        Assert.Equal(first, reopened.Workspace);
        Assert.Equal(deviceId, await reopened.Services.Workspaces.GetDeviceIdAsync());
        Assert.Equal([first], await reopened.Services.Workspaces.GetWorkspacesAsync());
        Assert.Equal(1, await reopened.CountAsync("SELECT count(*) FROM device;"));
    }
}
