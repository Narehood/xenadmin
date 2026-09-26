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
| Performance | Reproducible large-pool/console probes and measured CopyRect improvement | [Recorded before/after evidence](performance-baseline.md), overlap/clipping pixel regressions, unchanged presentation behavior |
| Graph editor | Add/remove/reorder graphs and sources, compatible saved layouts, retained missing sources, isolated Cancel, honest ranges and UTC history | [Graph editor](graph-editor.md), worker/RPC and draft regressions, long-range/DST fixtures; live data and physical desktop acceptance remain pending |
| Pool HA | Enable/disable, heartbeat selection, failover-capacity and VM restart-policy review through shared HA actions | [HA management](ha-management.md), server prerequisite checks, reviewed identity/configuration guards and partial-result handling; live failover and recovery remain pending |
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

## Subsequent feature parity

There is no deployment usage telemetry in the repository, so the order below is
a provisional engineering priority, not a claim about which workflows users use
most. Reorder when a concrete deployment requirement is supplied.

| Order | Gap and existing foundation | Required outcome before shipping |
| --- | --- | --- |
| 1 | AD and RBAC: shared enable/disable and subject/role actions exist | Domain join/leave plus subject/role management; redact credentials, preserve a tested administrative recovery path, validate restricted-user behavior and partial failures |
| 2 | DR: shared metadata/recovery actions exist without a shell workflow | Discover and inspect recovery metadata before any mutation; map SRs/networks, distinguish recovery/rehearsal, report partial completion, and verify recovery and cleanup using disposable storage/VMs |

Each feature should adapt the corresponding `XenModel/Actions` implementation.
Keep the user-visible plan separate from action execution and resolve current
objects again at execution time. Host/network identity and current permissions
must still match after a dialog or confirmation. Validation must include actual
failure recovery, not just successful wizard completion.

RDP, IE-backed plugin tabs, and Windows-specific external tools retain their
WinForms path. Their platform strategy is a separate design decision. Broad
async rewrites and large-pool tree refactoring should follow measured UI traces,
using the [baseline harness](performance-baseline.md) to establish a repeatable
comparison before changes.
