using XenAdmin.Actions;
using XenAdmin.Network;
using XenAPI;

namespace XcpNgCenter.Shell.Services;

/// <summary>Revalidates the confirmed topology on the action worker before constructing shared actions.</summary>
public sealed class ShellBondAction : AsyncAction
{
    private readonly Action<Session> _run;

    private ShellBondAction(IXenConnection connection, string title, Action<Session> run, params string[] methods)
        : base(connection, title)
    {
        _run = run;
        ApiMethodsToRoleCheck.AddRange(methods);
        ApiMethodsToRoleCheck.AddRange(Role.CommonSessionApiList);
        ApiMethodsToRoleCheck.AddRange(Role.CommonTaskApiList);
    }

    protected override void Run()
    {
        if (!Connection.IsConnected) throw new InvalidOperationException("The server disconnected. Reconnect and try again.");
        _run(Session);
        Description = "Bond changes completed.";
    }

    public static ShellBondAction Create(IXenConnection connection, BondCreateRequest request, string snapshot) =>
        new(connection, "Create NIC bond", session =>
        {
            var plan = BondManagement.PlanCreate(connection, request);
            BondManagement.RequireUnchanged(snapshot, plan.Snapshot);
            new CreateBondAction(connection, request.Name.Trim(), plan.CoordinatorMembers.ToList(), request.Automatic,
                plan.Mtu, request.Mode.Mode, request.Mode.Hashing).RunSync(session);
        }, "host.management_reconfigure", "network.create", "network.destroy", "network.remove_from_other_config",
            "pif.reconfigure_ip", "pif.plug", "bond.create", "bond.destroy");

    public static ShellBondAction SetMode(IXenConnection connection, string networkReference, BondModeOption mode, string snapshot) =>
        new(connection, "Change NIC bond mode", session =>
        {
            var plan = BondManagement.PlanExisting(connection, networkReference, mode);
            BondManagement.RequireUnchanged(snapshot, plan.Snapshot);
            foreach (var bond in plan.Bonds)
            {
                if (bond.mode != mode.Mode)
                    new DelegatedAsyncAction(connection, "Change bond mode", "Changing bond mode", "Bond mode changed",
                        s => Bond.set_mode(s, bond.opaque_ref, mode.Mode), true, "bond.set_mode").RunSync(session);
                if (mode.Mode == bond_mode.lacp && bond.HashingAlgoritm() != mode.Hashing)
                    new DelegatedAsyncAction(connection, "Change LACP hashing", "Changing LACP hashing", "LACP hashing changed",
                        s => Bond.set_property(s, bond.opaque_ref, "hashing_algorithm", Bond.HashingAlgoritmToString(mode.Hashing)),
                        true, "bond.set_property").RunSync(session);
            }
        }, "bond.set_mode", "bond.set_property");

    public static ShellBondAction Remove(IXenConnection connection, string networkReference, string snapshot) =>
        new(connection, "Remove NIC bond", session =>
        {
            var plan = BondManagement.PlanExisting(connection, networkReference);
            BondManagement.RequireUnchanged(snapshot, plan.Snapshot);
            new DestroyBondAction(plan.Bonds[0]).RunSync(session);
        }, "host.management_reconfigure", "network.destroy", "vif.plug", "vif.unplug", "pif.reconfigure_ip", "pif.plug", "bond.destroy");
}
