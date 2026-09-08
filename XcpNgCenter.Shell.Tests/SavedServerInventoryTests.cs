using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public class SavedServerInventoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("mp2:locked-credential")]
    public void RestoresSavedServerWithoutUnlockingOrConnecting(string? credential)
    {
        var server = Assert.Single(SavedServerInventory.Restore(
            [new SavedServerEntry("pool.example:8443", "admin", credential)]));

        Assert.Equal("pool.example:8443", server.Address);
        Assert.Equal("pool.example", server.Hostname);
        Assert.Equal(8443, server.Port);
        Assert.Equal("admin", server.Username);
        Assert.True(server.IsDisconnected);
        Assert.Null(server.Password);
        Assert.Null(server.Connection);
    }

    [Fact]
    public void SkipsInvalidEntriesAndDeduplicatesEndpointsForReconnect()
    {
        var servers = SavedServerInventory.Restore([
            new SavedServerEntry("", "root"),
            new SavedServerEntry("pool.example", "root"),
            new SavedServerEntry("POOL.example:443", "root"),
            new SavedServerEntry("pool.example:8443", "root")]).ToList();

        Assert.Equal(2, servers.Count);
        Assert.Equal(443, servers[0].Port);
        Assert.Equal(8443, servers[1].Port);
    }

    [Fact]
    public void EmptyProfileHasNoInfrastructure() =>
        Assert.Empty(SavedServerInventory.Restore([]));
}
