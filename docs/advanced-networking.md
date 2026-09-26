# Advanced networking in the Avalonia shell

The **Network** tab offers NIC bonding and SR-IOV on connected host/pool
selections, and **Configure host IP…** when a host is selected. These operations
use the existing XenModel actions and XenAPI bindings. A host selection does not
limit a bond or SR-IOV network to that host: those networks apply across the pool.

No live-pool mutation was performed while implementing these features. Complete
the [platform acceptance checklist](platform-acceptance.md) with disposable
hosts before using the new controls on a production pool.

## NIC bonds

Choose **Create bond…**, select unused physical NICs, enter the network name and
MTU, and choose Active-backup, Balance SLB, or LACP with source-MAC or TCP/UDP-port
hashing. LACP requires the vSwitch backend on every host and corresponding switch
configuration. vSwitch pools allow up to four members; a pool containing a bridge
backend allows two. The requested MTU cannot exceed any selected NIC's current
MTU. Automatically adding the bond network to new VMs is optional and initially
off.

Existing bond rows offer **Change bond mode…** and **Remove bond…**. Removal
removes the bond network across the pool and leaves its physical NICs in place.
Ordinary **Edit** still changes the network's existing name, description and
other supported settings; the separate bond controls govern the bond itself.

All affected hosts must be online, enabled and idle, with matching complete NIC
inventory. Create, mode changes and removal reject management, IPv4/IPv6-configured,
cluster, protected, busy, VLAN, tunnel and SR-IOV interfaces. Any VM interface on
an affected network blocks the operation, including an interface on a stopped
VM. Move/remove those dependencies before changing a bond. The shell deliberately
does not migrate live management or guest traffic between interfaces as part of
bonding, change membership in place, or configure a physical switch.

The editor holds the selected NIC references while open. After confirmation,
the action checks the current host/interface/network identities, dependencies,
bond members and mode against the reviewed snapshot before making API calls.
Rejected or failed saves retain the editor's draft. Pool operations are sequential;
failure can leave some hosts changed. Refresh and inspect the actual state before
retrying; no automatic rollback is promised.

## Host IP configuration

Select a host, choose **Configure host IP…**, and select its existing interface.
The editor changes one address family per save and preserves the management
interface and its preferred address family. It supports DHCP/static IPv4 and the
IPv6 modes the selected interface permits. It validates addresses, masks, gateways
and DNS before confirmation and rechecks the original interface configuration on
the action worker.

The dialog identifies disruptive changes and supplies reconnect guidance.
Changing the management address can disconnect the client before it receives a
success response; inspect the host and reconnect using its new address before
retrying. The shell does not automatically reboot a host, change which interface
provides management, or switch the preferred management address family. HA,
cluster, bond-member, tunnel, SR-IOV and other protected/busy interfaces remain
restricted by the editor.

## SR-IOV networks

Choose **Create SR-IOV network…**, select a physical NIC that reports SR-IOV
capability on every host, and enter the network metadata. The reviewed physical
NIC identities must still match when the action runs. Used, protected, bonded or
otherwise dependent interfaces are unavailable. Provisioning uses the existing
pool-wide `CreateSriovAction` and reports whether the server indicates a required
host restart; it never restarts hosts automatically.

An existing SR-IOV network can be removed using its row's **Remove** command only
after every VM interface and dependent VLAN/tunnel has been removed and no restart
is pending. The action validates the logical/physical/SR-IOV relationships again
before invoking shared network removal. Physical NICs are not forgotten. Drivers
can require a planned restart after disabling SR-IOV.

The existing VM interface editor remains responsible for attaching a compatible
VM to the resulting network. Hardware/firmware capability, host driver support,
guest requirements and actual packet forwarding still require live validation.

## Recovering an interrupted pool operation

Bond and SR-IOV operations are separate server calls, not a pool-wide transaction.
The action history and error message are useful evidence, but they do not provide
a complete per-host completion record. No live failure/recovery rehearsal has
been performed. Use the following procedure during disposable-pool acceptance;
it does not add automatic reconciliation or rollback to the shell.

1. Stop further changes to the affected network and retain the action error and
   time, requested configuration, and known host/network/interface identities.
   A timeout or lost response leaves the last operation's outcome **unknown**:
   the server may have applied it. Do not infer that every preceding host
   succeeded, that later hosts are unchanged, or that a failed client action
   rolled back the pool.
2. Restore connectivity and read current server state before another mutation.
   Close the affected editor, disconnect/reconnect the pool connection, and wait
   for inventory to populate. Rebuilding a tree/detail pane, or using the SR-IOV
   editor's **Refresh**, only rereads the client's current cache; it does not by
   itself establish a fresh server read. If the connection or event stream is
   still unreliable, inspect through a working supported administrative client
   or server tools. Include the coordinator and every pool host, even if the
   editor was opened from one host.
3. Record one row per affected host, using host UUID, network UUID and NIC/bond
   identities rather than names alone. Include the requested value, freshly
   observed value, relevant task outcome, and observation time. Keep uncertain
   entries explicit until verified. For bonds, record members, mode, and LACP
   `hashing_algorithm` separately: changing mode and changing hashing are
   separate calls, so one can complete before the other fails. For SR-IOV,
   record whether the network remains, each physical/logical PIF relationship,
   its SR-IOV record, and `requires_reboot`. Check every host rather than using
   the shared network's existence as evidence of complete provisioning.
4. Choose a single intended final configuration and review its dependencies
   before reconciling. A complete, otherwise eligible bond can have mixed modes
   after a failure. Reopen **Change bond mode**, explicitly select the intended
   supported mode/hash, and review the new confirmation against the recorded
   state and physical-switch configuration. The action rejects changes to its
   confirmed snapshot before execution; the editor builds a new snapshot for
   each save, so an old draft or successful validation is not a recovery audit.
   Missing bonds, incomplete membership or protected/dependent interfaces block
   shell changes. Inspect and repair those cases through supported server
   administration tools before returning to the shell.
5. For partial SR-IOV provisioning, do not repeat **Create** to fill in missing
   hosts: existing SR-IOV on a selected NIC blocks that request. A freshly
   inspected partial network can use **Remove** only when every remaining
   logical/physical/SR-IOV relationship passes the removal guards. Move/remove
   VM interfaces, including stopped VMs, and dependent VLANs/tunnels first;
   affected hosts must be online and idle, and a pending restart blocks removal.
   Plan any required restart with the host workload and maintenance procedure.
   If topology is incomplete or removal remains blocked, reconcile the actual
   objects using supported server administration tools; do not bypass the guards
   or forget physical NICs. Early creation cleanup is best effort, and a later
   failure can retain both the network and completed host configurations.
6. After the chosen repair or removal, reread every affected host and compare
   with the intended configuration. Check connectivity and packet forwarding
   and record remaining restart requirements before attaching VMs or starting a
   new pool-wide operation. The shell never restarts hosts automatically.

## Regression coverage and remaining checks

Synthetic cache tests cover valid plans, pool-wide availability, missing/replaced
objects, changed configuration after confirmation, protected interfaces, hidden
dependencies, stopped-VM references, MTU/mode validation, and editor/row state.
An offscreen Windows probe exercised all three actual .NET 10 editor windows:
NIC/family/mode selections and text bindings, management-NIC disablement, save/
cancel enablement, close guards while saving, minimum-size footer visibility,
disconnected-save rejection with retained drafts, and the actual Cancel handlers.
It rendered each at the default and minimum sizes, plus 100/150/200% raster scales.
The probe loaded application styles with isolated temporary settings and a
synthetic disconnected pool; it skipped application bootstrap and live API calls.
The repeatable [UI probe](../tools/AdvancedNetworking.UiProbe/Program.cs) requires
a Windows desktop session. Run these commands from the repository root, checking
each exit status:

```powershell
dotnet restore tools/AdvancedNetworking.UiProbe/AdvancedNetworking.UiProbe.csproj --locked-mode
dotnet run --project tools/AdvancedNetworking.UiProbe/AdvancedNetworking.UiProbe.csproj -c Release --no-restore
```

`tools/AdvancedNetworking.UiProbe/bin/Release/net10.0/` contains `results.log`
and the PNGs. The output directory is ignored by Git. The initial validation
also retained evidence under
`%LOCALAPPDATA%/Temp/xenadmin-advanced-network-ui-20260925`.

These checks do not establish that a physical switch, NIC driver, DHCP server or
guest will behave as expected. Native DPI/desktop behavior on Windows and Linux,
and disposable-pool create/change/remove/reconnect scenarios, remain manual
acceptance work.
