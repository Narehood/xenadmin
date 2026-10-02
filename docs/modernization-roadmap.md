# Modernization milestones

This sequence follows the September 25, 2026 decision to prioritize .NET 10 and
include NIC bonding, host management-IP configuration, and SR-IOV in the same
implementation effort. The supported integration branch remains `development`;
WinForms remains the production client while the Avalonia shell completes its
desktop and live-pool acceptance work.

## Current implementation

| Workstream | Deliverable | Acceptance gate |
| --- | --- | --- |
| Runtime | .NET 10 clients and shared modern targets; retain shared `net481` compatibility | Locked restores, both test suites/frameworks, WinForms builds with SDK/RDP prerequisites, Windows/Linux self-contained packages |
| Native Linux | Linux shell/shared tests and package validation | Linux CI execution; publish success alone is insufficient |
| Advanced networking | NIC bond creation/mode changes/removal, host IP configuration, SR-IOV provisioning/removal | Capability/topology validation, exact object identity, worker revalidation, dependency protection, visible partial-failure/reconnect guidance |
| Performance | Reproducible pool/console probes, faster CopyRect and inventory placement, coalesced refresh and retained tree containers | [Recorded before/after evidence](performance-baseline.md), pixel/topology/state regressions and production tree-layout checks; real event/detail-pane traces remain pending |
| WinForms designer | Completed metadata audit, byte-correct spinner replay and local Enabled reset; WFO1000 suppression removed | 333 descriptor/lifecycle checks per configuration and full resource loading; actual Visual Studio save/reopen remains pending |
| Remote Desktop | Reviewed guest IP/port launch through Windows Remote Desktop or system Remmina | [RDP plan and behavior](remote-desktop.md), endpoint/identity checks and actual review-dialog coverage; native client/guest login remains pending |
| Legacy browser plugins | IE tabs, scripting/authentication and credential UI [archived outside builds](../legacy/disabled-features/ie-plugin-tabs/README.md) at the user's request; menu commands remain opt-in | Compiled-type/resource exclusion and actual manifest-loader regressions for tab-only, mixed and menu-only plugins |
| Graph editor | Add/remove/reorder graphs and sources, compatible saved layouts, retained missing sources, isolated Cancel, honest ranges and UTC history | [Graph editor](graph-editor.md), worker/RPC and draft regressions, long-range/DST fixtures; live data and physical desktop acceptance remain pending |
| Pool HA | Enable/disable, heartbeat selection, failover-capacity and VM restart-policy review through shared HA actions | [HA management](ha-management.md), server prerequisite checks, reviewed identity/configuration guards and partial-result handling; live failover and recovery remain pending |
| AD and RBAC | Domain join/leave with local root recovery; directory user/group and role management through shared actions | [Directory access](ad-rbac-management.md), fresh permission and identity checks, credential/log regressions, restricted-user and partial-operation coverage; live directory/pool acceptance remains pending |
| Disaster recovery | Inspect attached recovery metadata, match replicated SR identities, map networks and restore eligible standalone VMs halted; temporary metadata rehearsal and guarded cleanup | [Disaster recovery](disaster-recovery.md), worker/RPC and editor coverage; actual replicated storage and guest recovery remain pending |
| Console investigation | Correct fragmented RFB padding reads with real-client protocol regressions | [Reboot investigation](reboot-hang-investigation.md); the originally reported hang still needs client/guest evidence |
| SDK review | Assess the retained XenAPI update separately | Preserve transport, TLS, redaction, action, and import security fixes before any SDK integration |
| Real installation/desktop | Repeatable [platform acceptance checklist](platform-acceptance.md) | Manual Windows UAC/rollback/restart, Linux desktop/updater, and live-pool checks |

No suitable disposable hosts or machines were available during implementation.
The user explicitly deferred that manual testing. Automated and synthetic checks
can support code review; they do not mark those manual gates complete. Do not
promote the preview to production parity or publish a production-readiness claim
based only on this PR.

SR-IOV provisioning selects the same capable physical NIC and NIC model across
the current pool, respects server feature restrictions, and excludes management,
IP, cluster, bond, VLAN, tunnel, and VM-used interfaces. Provisioning delegates to
the shared coordinator-first action. The UI records the reviewed NIC identities;
changes while confirmation is open must force a refresh instead of changing the
operation's targets. The final server status reports pending host restarts;
restart is never automatic. Removing an unused network validates only its exact
logical SR-IOV PIFs before invoking the shared removal action. Physical NICs are
retained, dependent interfaces block removal, and already-pending restart states
must be resolved first.

This uses the server's advertised capabilities and supported API; it does not
promise support for every XCP-ng hardware/firmware combination. The
[XAPI network_sriov contract](https://xapi-project.github.io/new-docs/xen-api/classes/network_sriov/index.html)
defines the physical/logical mapping and restart state. The
[upstream networking guide](https://docs.xenserver.com/en-us/xenserver/8/networking/manage.html#use-sr-iov-enabled-nics)
documents NIC model consistency, driver-dependent restart requirements, and VM
operation limits. Hardware, guest drivers, and actual behavior still require
acceptance testing on the target deployment.

## Remaining parity and acceptance

There is no deployment usage telemetry in the repository, so the order below is
a provisional engineering priority, not a claim about which workflows users use
most. Reorder when a concrete deployment requirement is supplied.

| Order | Gap and existing foundation | Required outcome before shipping |
| --- | --- | --- |
| 1 | Deployment acceptance for the implemented workflows | Complete the physical desktop, updater/UAC, networking, HA, AD/RBAC and DR checks using disposable environments; diagnose the reported reboot incident |
| 2 | Extended DR scope beyond halted standalone VM recovery | Design appliance/snapshot/hardware-device recovery and a running guest rehearsal with proven storage isolation; retain the existing WinForms/server workflows in the meantime |
| 3 | Windows-specific menu/command extensions and external tools | Follow the [remaining parity plan](modernization-remaining-plan.md); identify required deployed commands/tools and adapt their capabilities with explicit reviewed targets |

Each feature should adapt the corresponding `XenModel/Actions` implementation.
Keep the user-visible plan separate from action execution and resolve current
objects again at execution time. Host/network identity and current permissions
must still match after a dialog or confirmation. Validation must include actual
failure recovery, not just successful wizard completion.

The [September 27 execution plan](modernization-execution-plan.md) records the
parallel implementation and integrated validation of access and recovery. A
halted metadata rehearsal checks only metadata restoration and cleanup; it does
not establish disk replication correctness, guest boot, or application failover.

The shell now supports external RDP. Embedded RDP and legacy Windows-specific
tool templates retain their WinForms path. IE-backed plugin tabs are archived
and excluded from all builds. The
[remaining parity plan](modernization-remaining-plan.md) records their platform
strategy and the gates for extended DR. Further async/layout work should follow
measured live UI traces using the [baseline harness](performance-baseline.md).
