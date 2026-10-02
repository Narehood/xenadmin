using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using XenAdmin.Network;
using XenAPI;
using Xunit;
using XenAdmin.Core;

namespace XcpNgCenter.Shell.Tests;

public class RdpClientLauncherTests
{
    [Theory]
    [InlineData("192.0.2.10", "3389", "192.0.2.10:3389")]
    [InlineData(" 2001:db8::10 ", "3390", "[2001:db8::10]:3390")]
    [InlineData("::ffff:192.0.2.10", "1", "192.0.2.10:1")]
    public void EndpointsAreCanonicalAndPortsAreExplicit(string address, string port, string authority)
    {
        Assert.True(RdpEndpoint.TryCreate(address, port, out var endpoint));
        Assert.Equal(authority, endpoint!.Authority);
    }

    [Theory]
    [InlineData("192.0.2.10 /cert:ignore", "3389")]
    [InlineData("rdp://user:password@192.0.2.10", "3389")]
    [InlineData("$(command)", "3389")]
    [InlineData("127.1", "3389")]
    [InlineData("0xC000020A", "3389")]
    [InlineData("192.000.002.010", "3389")]
    [InlineData("127.0.0.1", "3389")]
    [InlineData("::1", "3389")]
    [InlineData("::ffff:127.0.0.1", "3389")]
    [InlineData("::ffff:192.000.002.010", "3389")]
    [InlineData("::ffff:0xC000020A", "3389")]
    [InlineData("::ffff:c000:20a", "3389")]
    [InlineData("::FFFF:192.0.2.10", "3389")]
    [InlineData("0:0:0:0:0:ffff:c000:020a", "3389")]
    [InlineData("0.0.0.0", "3389")]
    [InlineData("::", "3389")]
    [InlineData("224.0.0.1", "3389")]
    [InlineData("255.255.255.255", "3389")]
    [InlineData("ff02::1", "3389")]
    [InlineData("fe80::10", "3389")]
    [InlineData("fe80::10%3", "3389")]
    [InlineData("192.0.2.10", "0")]
    [InlineData("192.0.2.10", "65536")]
    [InlineData("192.0.2.10", "3389 /admin")]
    public void InvalidOrUnsafeEndpointsAreRejected(string address, string port)
    {
        Assert.False(RdpEndpoint.TryCreate(address, port, out var endpoint));
        Assert.Null(endpoint);
    }

    [Theory]
    [InlineData(RdpClientKind.WindowsRemoteDesktop, "192.0.2.10", "/v:192.0.2.10:3389", "/prompt")]
    [InlineData(RdpClientKind.Remmina, "2001:db8::10", "--connect", "rdp://[2001:db8::10]:3389")]
    public void LaunchUsesSeparateArgumentsWithoutCredentialsOrShell(RdpClientKind kind, string address, string first, string second)
    {
        Assert.True(RdpEndpoint.TryCreate(address, "3389", out var endpoint));
        var info = RdpClientLauncher.CreateStartInfo(endpoint!, kind);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { first, second }, info.ArgumentList);
        Assert.Empty(info.Arguments);
        Assert.False(info.RedirectStandardInput);
        Assert.False(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
        Assert.EndsWith(kind == RdpClientKind.Remmina ? "/usr/bin/remmina" : "mstsc.exe", info.FileName);
    }

    [Fact]
    public void GuestSuggestionsFilterNetworkMetadataAndDeduplicateAddresses()
    {
        var connection = new XenConnection();
        connection.Cache.UpdateFrom(connection,
        [new ObjectChange(typeof(VM_guest_metrics), "metrics", new VM_guest_metrics { networks = new()
        {
            ["0/ip"] = "192.0.2.10", ["0/ipv4/0"] = "192.0.2.10",
            ["0/ipv6/0"] = "2001:db8::10", ["1/ip"] = "127.0.0.1",
            ["1/ipv4/0"] = "192.0.2.10 /cert:ignore", ["0/gateway"] = "192.0.2.1"
        } }), new ObjectChange(typeof(VM), "vm", new VM { uuid = "vm-uuid", guest_metrics = new("metrics") })]);
        var vm = connection.Resolve(new XenRef<VM>("vm"));
        Assert.Equal(new[] { "192.0.2.10", "2001:db8::10" }, RdpClientLauncher.GuestAddresses(vm));
        vm.guest_metrics = new("missing");
        Assert.Empty(RdpClientLauncher.GuestAddresses(vm));
    }

    [Fact]
    public void ReviewRejectsReplacedStoppedOrDifferentGuests()
    {
        var connection = new XenConnection();
        connection.Cache.UpdateFrom(connection,
            [new ObjectChange(typeof(VM), "vm", new VM { uuid = "first", power_state = vm_power_state.Running })]);
        var vm = connection.Resolve(new XenRef<VM>("vm"));
        var review = new RdpTargetReview(connection, vm.opaque_ref, vm.uuid);
        Assert.True(review.Matches(vm));
        vm.power_state = vm_power_state.Halted;
        Assert.False(review.Matches(vm));
        vm.power_state = vm_power_state.Running;
        vm.uuid = "replacement";
        Assert.False(review.Matches(vm));
        vm.uuid = "first";
        vm.is_control_domain = true;
        Assert.False(review.Matches(vm));
        vm.is_control_domain = false;
        vm.is_a_template = true;
        Assert.False(review.Matches(vm));
        vm.is_a_template = false;
        vm.is_a_snapshot = true;
        Assert.False(review.Matches(vm));
        vm.is_a_snapshot = false;
        Assert.False(new RdpTargetReview(new XenConnection(), "vm", "first").Matches(vm));
        Assert.False(review.Matches(null));
    }

    [Fact]
    public void NormalizedDestinationNeedsAnotherConfirmation()
    {
        AssertNormalizationNeedsReview("::ffff:192.0.2.10", "3389", "192.0.2.10", "3389", "192.0.2.10:3389");
    }

    [Theory]
    [InlineData(" 192.0.2.10 ", "3389", "192.0.2.10", "3389", "192.0.2.10:3389")]
    [InlineData("2001:0DB8:0:0:0:0:0:10", "3390", "2001:db8::10", "3390", "[2001:db8::10]:3390")]
    [InlineData("192.0.2.10", "03389", "192.0.2.10", "3389", "192.0.2.10:3389")]
    public void AddressOrPortNormalizationNeedsAnotherConfirmation(string address, string port,
        string normalizedAddress, string normalizedPort, string authority)
        => AssertNormalizationNeedsReview(address, port, normalizedAddress, normalizedPort, authority);

    private static void AssertNormalizationNeedsReview(string address, string port,
        string normalizedAddress, string normalizedPort, string authority)
    {
        var launches = new List<RdpEndpoint>();
        var model = new RdpConnectViewModel("Synthetic guest", [], launches.Add) { Address = address, Port = port };
        model.ConnectCommand.Execute(null);
        Assert.Empty(launches);
        Assert.Equal(normalizedAddress, model.Address);
        Assert.Equal(normalizedPort, model.Port);
        Assert.Equal(authority, model.DestinationAuthority);
        Assert.True(model.HasReviewMessage);
        model.ConnectCommand.Execute(null);
        Assert.Equal(authority, Assert.Single(launches).Authority);
    }

    [Fact]
    public void DestinationPreviewUpdatesWithAddressAndPortAndHidesInvalidInput()
    {
        var model = new RdpConnectViewModel("Synthetic guest", [], _ => { });
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(model.HasDestination);
        model.Address = "2001:0DB8::10";
        Assert.Equal("[2001:db8::10]:3389", model.DestinationAuthority);
        Assert.True(model.HasDestination);
        Assert.Contains(nameof(model.DestinationAuthority), changes);
        Assert.Contains(nameof(model.HasDestination), changes);
        changes.Clear();
        model.Port = "3390";
        Assert.Equal("[2001:db8::10]:3390", model.DestinationAuthority);
        Assert.Contains(nameof(model.DestinationAuthority), changes);
        model.Address = "192.0.2.10 /cert:ignore";
        Assert.False(model.HasDestination);
        Assert.Empty(model.DestinationAuthority);
    }

    [Fact]
    public void EditingANormalizedDestinationClearsReviewAndLaunchesOnlyTheEditedEndpoint()
    {
        var launches = new List<RdpEndpoint>();
        var model = new RdpConnectViewModel("Synthetic guest", [], launches.Add) { Address = "::ffff:192.0.2.10" };
        model.ConnectCommand.Execute(null);
        Assert.Empty(launches);
        Assert.True(model.HasReviewMessage);
        model.Address = "192.0.2.11";
        model.Port = "3390";
        Assert.False(model.HasReviewMessage);
        Assert.Equal("192.0.2.11:3390", model.DestinationAuthority);
        model.ConnectCommand.Execute(null);
        Assert.Equal(model.DestinationAuthority, Assert.Single(launches).Authority);
    }

    [Fact]
    public void DialogRequiresAValidReviewedEndpointAndClearsErrorsAfterAnEdit()
    {
        var launches = new List<RdpEndpoint>();
        var model = new RdpConnectViewModel("Synthetic guest", [], launches.Add);
        model.ConnectCommand.Execute(null);
        Assert.True(model.HasError);
        Assert.Empty(launches);
        model.Address = "192.0.2.10";
        Assert.False(model.HasError);
        model.SelectedAddress = null;
        Assert.Equal("192.0.2.10", model.Address);
        model.Port = "65536";
        model.ConnectCommand.Execute(null);
        Assert.True(model.HasError);
        model.Port = "3390";
        model.ConnectCommand.Execute(null);
        Assert.Equal("192.0.2.10:3390", Assert.Single(launches).Authority);
    }
}
