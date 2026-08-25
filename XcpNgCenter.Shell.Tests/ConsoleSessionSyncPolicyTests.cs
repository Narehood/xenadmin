using XenAPI;
using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ConsoleSessionSyncPolicyTests
{
    [Fact]
    public void SessionKey_IncludesDomainIdSoGuestRebootForcesReconnect()
    {
        var before = ConsoleSessionSyncPolicy.SessionKey("pool", "OpaqueRef:old", "https://host/console", 12);
        var after = ConsoleSessionSyncPolicy.SessionKey("pool", "OpaqueRef:old", "https://host/console", 13);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SelectPreferredRfbConsole_UsesLastAdvertisedRfbAfterReboot()
    {
        var stale = new XenAPI.Console
        {
            opaque_ref = "OpaqueRef:old",
            protocol = console_protocol.rfb,
            location = "https://host/console/old"
        };
        var replacement = new XenAPI.Console
        {
            opaque_ref = "OpaqueRef:new",
            protocol = console_protocol.rfb,
            location = "https://host/console/new"
        };
        var serial = new XenAPI.Console { protocol = console_protocol.vt100, location = "https://host/serial" };

        var selected = ConsoleSessionSyncPolicy.SelectPreferredRfbConsole(
            [stale, serial, replacement],
            c => c.protocol == console_protocol.rfb,
            c => c.location);

        Assert.Same(replacement, selected);
    }

    [Fact]
    public void SelectPreferredRfbConsole_IgnoresRfbWithoutLocation()
    {
        var empty = new XenAPI.Console { protocol = console_protocol.rfb, location = "" };
        var ready = new XenAPI.Console { protocol = console_protocol.rfb, location = "https://host/console" };

        var selected = ConsoleSessionSyncPolicy.SelectPreferredRfbConsole(
            [empty, ready],
            c => c.protocol == console_protocol.rfb,
            c => c.location);

        Assert.Same(ready, selected);
    }

    [Theory]
    [InlineData(null, "host|console|loc|1", false, false, false, true)]
    [InlineData("host|other|loc|1", "host|console|loc|1", true, false, false, true)]
    [InlineData("host|console|loc|1", "host|console|loc|1", true, false, false, false)]
    [InlineData("host|console|loc|1", "host|console|loc|1", false, true, false, false)]
    [InlineData("host|console|loc|1", "host|console|loc|1", false, false, false, true)]
    [InlineData("host|console|loc|1", "host|console|loc|1", false, false, true, false)]
    [InlineData("host|console|loc|12", "host|console|loc|13", false, false, false, true)]
    public void ShouldReplaceSession_ReconnectsGuestsButNotHostAfterDrop(
        string? activeKey,
        string candidateKey,
        bool connected,
        bool connecting,
        bool isControlDomain,
        bool expected)
    {
        var replace = ConsoleSessionSyncPolicy.ShouldReplaceSession(
            activeKey,
            candidateKey,
            connected,
            connecting,
            isControlDomain);

        Assert.Equal(expected, replace);
    }

    [Fact]
    public void ShouldReplaceSession_DoesNotRetryHostConsoleAfterDom0TypedReboot()
    {
        const string key = "pool|OpaqueRef:dom0|https://host/console|0";

        var replace = ConsoleSessionSyncPolicy.ShouldReplaceSession(
            key,
            key,
            sessionConnected: false,
            sessionConnecting: false,
            isControlDomain: true);

        Assert.False(replace);
    }

    [Fact]
    public void ShouldReplaceSession_RetriesGuestConsoleAfterAlpineRebootDrop()
    {
        const string key = "pool|OpaqueRef:vm|https://host/console|12";

        var replace = ConsoleSessionSyncPolicy.ShouldReplaceSession(
            key,
            key,
            sessionConnected: false,
            sessionConnecting: false,
            isControlDomain: false);

        Assert.True(replace);
    }
}
