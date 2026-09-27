# Pool high availability in the Avalonia preview

The shell adds pool HA configuration alongside the supported WinForms workflow:
enable HA using a selected heartbeat storage repository, review VM restart
policies and host-failure tolerance, apply changes to an enabled pool, and disable
HA normally. Open **High availability** from a connected pool or host. HA alert
fix links open the alert's original pool, independently of the current tree
selection.

This remains preview functionality. No disposable pool was available, and the
user deferred real failover and recovery tests. Automated validation does not
establish that a particular pool, storage system, or network can recover from a
host failure. Record that evidence using the manual checks below and the
[platform acceptance checklist](platform-acceptance.md).

## What the review means

HA restarts eligible VMs after a host failure; guest execution is interrupted.
XCP-ng supports two-host pools but recommends at least three. Reliable operation
depends on both storage and network heartbeat paths, and an isolated host can
self-fence. Current upstream guidance recommends a heartbeat SR of at least
4 GiB and includes iSCSI, NFS, Fibre Channel, and XOSTOR. A separate SR is optional.
These are deployment considerations, not a substitute for the server's SR
suitability check. See the [XCP-ng HA guide](https://docs.xcp-ng.org/management/ha/).

Heartbeat storage is assessed through the existing server API, rather than a
client-maintained storage-type allowlist. `SR.assert_can_host_ha_statefile`
reports whether the selected SR can host the statefile and supplies a failure
reason when it cannot. A previous successful scan does not guarantee the SR is
still usable at execution time. See the
[SR API contract](https://xapi-project.github.io/xen-api/classes/sr.html#assert_can_host_ha_statefile).

VM eligibility also requires server assessment. A restart policy alone does not
prove that a VM can run on another host. Shared disks, accessible pool networks,
attached media, and devices such as SR-IOV, USB, and vGPU can affect agility.
Review the reported reason instead of changing a failed VM automatically to a
different restart policy.

Configured tolerance, the current plan, and the maximum for a proposed
configuration are different values. Capacity review uses the proposed protected
VM set and the shared capacity helper, including an applicable HCI limit. It
retains other VMs' protection requirements; best-effort and do-not-restart
policies do not reserve guaranteed failover capacity. A zero-host tolerance
does not guarantee survival of a host failure. The
[pool API](https://xapi-project.github.io/xen-api/classes/pool.html#ha_compute_hypothetical_max_host_failures_to_tolerate)
defines the hypothetical calculation.

## Review and apply

The editor keeps its draft separate from the server configuration. Review the
heartbeat choice, VM policies, proposed tolerance, and server results before
applying. Changing the draft requires another review. Closing without applying
does not submit a configuration change. Startup order and start delay are shown
for review and preserved exactly; this editor changes restart policy rather than
those startup settings. Unknown or legacy policies require an explicit supported
selection before enablement or configuration can proceed. Normal disable retains
the original policies and ignores unrelated draft edits.

The read-only server review can be cancelled. Cancellation waits for its active
server request to finish, then discards the result. The editor stays open while
a confirmed change runs. Review cancellation applies only to the read-only
checks, not to a server enable or disable task.

The operation stays bound to the pool that was opened. The action checks current
identities, configuration, permissions, and relevant prerequisites before it
delegates to the shared HA actions. Reconnects, removed or replaced objects, and
configuration changes can require reopening and reviewing the current pool.
Do not interpret a disabled Apply button or a rejected stale review as evidence
that HA is disabled.

Enablement requires live hosts and usable heartbeat storage. Updating VM
priorities also depends on the shared action's database synchronization. Normal
disable has a separate validation path; an unhealthy host or insufficient
failover capacity must not prevent the user from requesting that HA be stopped.
The shell invokes the normal pool disable API and does not run emergency host
commands, force a coordinator change, or delete heartbeat VDIs.

For enablement and configuration, this preview additionally requires all pool
hosts to be enabled and idle, matching platform versions, complete static
management addressing, and no pool upgrade or secret rotation in progress.
These are the shell's preflight constraints. They are deliberately separate from
the recovery-oriented normal disable path. VM operations still in progress and
unknown VM states must be resolved before changing protection settings; a
suspended or paused VM does not by itself prevent review of the pool.

## Failure and recovery

Shared `EnableHAAction` updates supplied VM startup settings and pool tolerance
before requesting enablement. `SetHaPrioritiesAction` removes protection first,
sets tolerance, adds protection, and synchronizes the database. These operations
are not a transaction. A failure can leave some requested settings applied.

`DisableHAAction` waits for the server's asynchronous disable operation. The
server can continue resolving a partial disable after the client loses its
connection. It also distinguishes clean shutdown through a working statefile
from recovery without one. See the
[XAPI HA design](https://xapi-project.github.io/features/HA/HA.html#disabling-ha).

After failure, cancellation, or a lost response:

1. Reconnect to the pool and inspect the task and current HA state.
2. Compare actual tolerance and VM restart policies with the intended changes.
   Check each affected host and heartbeat SR before retrying.
3. If HA is still being enabled or disabled, wait for or investigate that server
   operation. Do not start an overlapping operation based on the dialog closing.
4. Reopen the editor and review a fresh draft only after reconciling the result.
   For a pool that cannot recover normally, use the deployment's administrative
   recovery procedure and the [upstream troubleshooting guide](https://docs.xcp-ng.org/troubleshooting/troubleshooting-ha/).

There is no automatic rollback or durable recovery journal in this milestone.
The action history and server state are the evidence for determining what
completed.

### Reconcile intermediate states

Before applying, record the pool UUID, coordinator, original and intended HA
state/tolerance, heartbeat SR/VDI identities, and each affected VM's UUID, raw
restart policy, start order and delay. Include hidden VMs in the protection
review. Keep this record outside the dialog: the shell does not persist its
draft as a recovery journal.

After an interrupted change, use a freshly connected client or the existing
authenticated administration tools to read the actual pool and VM records.
Record the task reference/status when available, pool `ha_enabled`,
`ha_host_failures_to_tolerate`, `ha_plan_exists_for`, `ha_overcommitted`, and
`ha_statefiles`, plus member and heartbeat-storage health. Compare each VM's
current policy/order/delay with both the original and intended values. Do not
infer an outcome from the last progress message, a missing task record, or the
absence of a response: the server may have accepted that request.

The stages below follow the actual shared
[enable](../XenModel/Actions/Pool/EnableHAAction.cs),
[configure](../XenModel/Actions/VM/SetHaPrioritiesAction.cs), and
[disable](../XenModel/Actions/Pool/DisableHAAction.cs) actions. Several stages may
be indistinguishable after a disconnect; reconcile every applicable row.

| Interrupted stage | Possible intermediate state | Required reconciliation |
| --- | --- | --- |
| Read-only review or worker preflight | This attempt has issued no configuration writes. Another client or an earlier attempt may still have changed the pool. | Refresh and compare current state. Reopen a stale editor; do not treat failed review as proof that HA is disabled or that earlier changes were undone. |
| Enable: VM writes, then pool tolerance | Some changed VMs may have their new policy while others retain the original value; tolerance may also have changed while HA remains disabled. For each VM, the shared action writes policy, order, then delay separately. The shell submits the captured order/delay unchanged. | Compare every affected VM and the pool tolerance, including the request whose response was lost. These settings alone do not establish HA protection. Once no operation remains active, either finish the intended enablement or restore the recorded settings through a newly reviewed configuration. |
| Enable: server task submission or completion | VM settings and tolerance may be applied, while HA enablement is active, failed, successful, or unconfirmed. | Check the task, current pool state, member health and resolved heartbeat VDIs/SRs. If the intended state is already established, record it rather than enabling again. Investigate an active or inconsistent transition before any retry; a pool flag alone is insufficient evidence of member/heartbeat health. |
| Configure: policies changing to Best effort or Do not restart | Some VMs may have lost guaranteed restart protection before the tolerance write or any requested Restart additions. | Identify exactly which VMs now lack their former policy and compare the current plan with the requested protection. Do not assume planned Restart additions compensate for those changes. Recompute capacity for a fresh draft before completing or restoring policies. |
| Configure: tolerance write, then Restart policies | Tolerance may be new while only a subset of the requested Restart policies has been applied. Each VM's policy/order/delay writes remain separate. | Read tolerance and every affected VM, then check current plan/overcommit status. Preserve valid changes made by other administrators. Review the remaining intended changes, or the recorded original configuration, against current agility and capacity before applying. |
| Configure: database synchronization | All requested settings may be visible on the coordinator, but completion of the final synchronization to pool members is unconfirmed. | Inspect the synchronization task and member health. Matching UI values do not prove synchronization completed. If no settings differ, the shell disables Apply and has no synchronization-only command; have the pool administrator verify or repair synchronization through the supported administration/recovery procedure. Do not toggle policies or HA solely to enable Apply. |
| Disable: server task submission or completion | HA may still be enabled, disabling, disabled, or inconsistent across members after an interrupted server operation. The client action does not change VM restart-policy values. | Verify task outcome and pool/member state before treating automatic recovery as stopped. If normal disable is still needed and no transition remains active, reopen and review Disable. Do not interpret retained VM policy values as proof HA is enabled, or a lost response as proof it is disabled. Use the deployment's administrative recovery procedure when normal pool access/recovery is unavailable. |

Only start another mutation after accounting for active tasks and reconciling
the actual state. Restoring original values is a new change requiring current
capacity, eligibility and permission checks; it is not an automatic rollback.
Unknown outcomes or missing original values require administrator investigation.
The shell does not issue emergency host commands or delete statefiles as part
of reconciliation. Real interruption/recovery tests for these stages remain
manual acceptance work.

## Automated validation

The HA milestone passes 681 shell tests in each of Windows Release and Debug.
The 83 new HA cases cover backend and loopback server behavior (49), editor
draft/review/confirmation behavior (20), and original-pool alert routing (14).
The loopback tests run the shared enable, configure, and disable actions through
successful task completion and cleanup as well as rejected and lost responses.
They also verify that missing or revoked nested permissions prevent mutation.
All 73 shared tests pass on both `net481` and `net10.0`.

An actual-window probe passed 24 interaction and layout checks at default and
minimum sizes, including cancellation, declined confirmation, failed apply,
busy close guards, and a scrollable confirmation for 200 VM changes. Its
temporary harness, results and screenshots are in ignored
`artifacts/ha-editor-ui-probe`. These synthetic checks do not exercise a real
host failure or establish deployment readiness. See PR #50 for hosted results
on the exact implementation commit.

## Manual acceptance still required

Use an expendable pool and VMs for disruptive tests. Record versions, host/SR
identities, configuration, task results, and observed VM outcomes.

| Scenario | Expected evidence |
| --- | --- |
| Enable and reopen | Review a suitable heartbeat SR, choose policies and tolerance, enable, and confirm the resulting configuration in both clients. |
| VM eligibility | Review a VM with local storage, local media, unavailable networking, or an unsupported device. Confirm the server reason is shown and guaranteed restart is not silently substituted. |
| Capacity changes | Change protected VMs and tolerance; verify draft changes invalidate the review and insufficient capacity prevents applying an unsupported guarantee. |
| Roles and stale state | Exercise a restricted account and change the pool, host, SR, or VM state while the editor is open. Confirm rejected operations do not overwrite newer configuration. |
| Normal disable | Disable HA and verify the server task and pool/host state. Repeat with degraded capacity or an unavailable member where the deployment supports normal disable. |
| Partial or unconfirmed result | Interrupt a disposable test at a controlled point; reconcile actual policies, tolerance, and HA state before retrying. A lost client response must not be reported as success. |
| Real failover | In the disposable pool only, follow a controlled host-failure procedure and verify guest restart, surviving capacity, coordinator behavior, and heartbeat recovery. |
| Desktop behavior | Check scrolling, keyboard navigation, confirmation, Cancel, and error visibility on Windows/Linux at the supported scaling settings. |
