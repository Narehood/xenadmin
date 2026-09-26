using System.Reflection;
using XenAdmin.Core;
using XenAdmin.Network;
using XenAPI;
using XcpNgCenter.Shell.Alerts;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class HaAlertNavigationTests : IDisposable
{
    private readonly Action<Pool>? _previousOpen = ShellAlertFixActions.OpenHaConfiguration;
    private readonly Action<string>? _previousStatus = ShellAlertFixActions.ReportStatus;

    [Theory]
    [InlineData("HA_HEARTBEAT_APPROACHING_TIMEOUT")]
    [InlineData("HA_HOST_FAILED")]
    [InlineData("HA_HOST_WAS_FENCED")]
    [InlineData("HA_NETWORK_BONDING_ERROR")]
    [InlineData("HA_POOL_DROP_IN_PLAN_EXISTS_FOR")]
    [InlineData("HA_POOL_OVERCOMMITTED")]
    [InlineData("HA_PROTECTED_VM_RESTART_FAILED")]
    [InlineData("HA_STATEFILE_APPROACHING_TIMEOUT")]
    [InlineData("HA_STATEFILE_LOST")]
    [InlineData("HA_XAPI_HEALTHCHECK_APPROACHING_TIMEOUT")]
    public void HaFixOpensTheAlertsPool(string messageType)
    {
        var source = new Fixture();
        var other = new Fixture();
        Pool? opened = other.Pool;
        ShellAlertFixActions.OpenHaConfiguration = pool => opened = pool;
        var alert = source.Alert(messageType);

        Assert.NotNull(alert.FixLinkAction);
        alert.FixLinkAction();

        Assert.Same(source.Pool, opened);
        Assert.Same(source.Connection, opened!.Connection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacedPoolCannotRedirectAnExistingAlert(bool replaceUuid)
    {
        var source = new Fixture();
        var alert = source.Alert("HA_POOL_OVERCOMMITTED");
        var opened = false;
        string? status = null;
        ShellAlertFixActions.OpenHaConfiguration = _ => opened = true;
        ShellAlertFixActions.ReportStatus = message => status = message;
        if (replaceUuid) source.Pool.uuid = "replacement-uuid";
        else source.Pool.opaque_ref = "replacement-ref";

        alert.FixLinkAction();

        Assert.False(opened);
        Assert.Contains("no longer available", status);
    }

    [Fact]
    public void DisconnectedAlertRequestsReconnectWithoutOpeningAnEditor()
    {
        var source = new Fixture(connected: false);
        var opened = false;
        string? status = null;
        ShellAlertFixActions.OpenHaConfiguration = _ => opened = true;
        ShellAlertFixActions.ReportStatus = message => status = message;

        source.Alert("HA_POOL_OVERCOMMITTED").FixLinkAction();

        Assert.False(opened);
        Assert.Contains("Reconnect", status);
    }

    [Fact]
    public void MissingWindowReportsUnavailableWithoutMutatingPool()
    {
        var source = new Fixture();
        string? status = null;
        ShellAlertFixActions.OpenHaConfiguration = null;
        ShellAlertFixActions.ReportStatus = message => status = message;

        source.Alert("HA_POOL_OVERCOMMITTED").FixLinkAction();

        Assert.Contains("not available in this window", status);
        Assert.True(source.Pool.ha_enabled);
    }

    public void Dispose()
    {
        ShellAlertFixActions.OpenHaConfiguration = _previousOpen;
        ShellAlertFixActions.ReportStatus = _previousStatus;
    }

    private sealed class Fixture
    {
        public XenConnection Connection { get; } = new();
        public Pool Pool { get; }

        public Fixture(bool connected = true)
        {
            Pool = Add("pool-ref", new Pool { uuid = "pool-uuid", name_label = "Alert pool", ha_enabled = true });
            if (!connected) return;
            var field = typeof(XenConnection).GetField("connectTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(field.FieldType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: ["127.0.0.1", 1], culture: null)!;
            field.FieldType.GetField("Connected")!.SetValue(state, true);
            field.FieldType.GetField("Session")!.SetValue(state, new Session("http://127.0.0.1:1/") { opaque_ref = "alert-session" });
            field.SetValue(Connection, state);
        }

        public ShellMessageAlert Alert(string type) => new(Add("message-ref", new Message
        {
            uuid = "alert-uuid", name = type, cls = cls.Pool, obj_uuid = Pool.uuid,
            body = "1", timestamp = DateTime.UtcNow
        }));

        private T Add<T>(string reference, T value) where T : XenObject<T>
        {
            Connection.Cache.UpdateFrom(Connection, [new ObjectChange(typeof(T), reference, value)]);
            return Connection.Resolve(new XenRef<T>(reference));
        }
    }
}
