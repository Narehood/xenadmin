# Disaster recovery in the Avalonia preview

The pool/host **Disaster recovery…** command discovers and inspects current
recovery metadata on already attached shared storage, reviews individual VM
recovery, restores VM records halted, and maps their NICs. WinForms remains the
supported client and the recovery path for appliance groups and more complex
metadata.

This milestone is a metadata recovery workflow. It does not replicate storage,
attach unknown SAN/iSCSI devices, fence the source pool, boot recovered guests,
or establish that a real failover works. Live validation remains pending a
disposable source/recovery environment.

## Prepare and inspect

1. Connect to the intended recovery pool. Attach the metadata SR and replicated
   VM storage through the existing storage workflow. Keep guest workloads fenced
   on the original pool before any subsequent guest start.
2. Open **Disaster recovery…** and select **Discover metadata**. Discovery lists
   latest metadata VDIs in the connection inventory on attached shared SRs.
   A newly attached replica may need an inventory refresh before it appears.
3. Select a database and **Inspect metadata**. The editor opens a metadata
   database session, reads its source pool/VM/storage/network inventory, and
   closes that session. An open or read failure is reported; partial reads are
   never accepted as a complete inspection. The destination login is borrowed
   and is not logged out.
4. Select the VMs. Appliance members, snapshot chains, suspended or unknown VM
   power states, vTPM/vGPU/USB/passthrough devices are shown with their reason and
   require an appropriate existing recovery procedure. Suspended recovery can
   preserve saved state, so it is excluded from this editor's halted workflow.
   VMs with `other_config[auto_poweron]=true` are also excluded, even if pool
   autostart is currently disabled. Disable that setting in the source and refresh
   recovery metadata before using this workflow. An unexpected enabled flag after
   import also stops the operation before NIC mapping or cleanup authorization.

## Map and review

Storage choices are restricted to attached shared replicas carrying the source
SR UUID. For each data disk, review requires one available target VDI at the
source storage location and with matching virtual size. It captures that
target VDI's reference, UUID, SR, location and relevant disk attributes. The
editor cannot move/copy recovery disks onto arbitrary storage. SRs still owned
by a `DR_task` must first be permanently attached through the existing WinForms
workflow.

Select a destination for every source network used by the selected VMs. NIC
choices are explicit. Recovery keeps MAC addresses and maps NICs by device slot
after importing each halted VM. A metadata rehearsal offers only empty internal
networks with no physical interfaces or other VM NICs.

**Review** checks fresh server inventory and current session permissions,
rejects existing destination VM UUIDs, requires destination HA to be disabled,
checks attached replicas and network identities, and calls the server's recovery
precheck. Changing a selection, mapping or mode invalidates review. The worker
opens the metadata again and verifies the reviewed source, request and target
before the first recovery. Storage, disk identities, network configuration and
permissions are checked again before each later VM.

The confirmation lists each VM's source and destination SR/network names and
UUIDs, plus whether its network has physical interfaces or existing VM NICs.
Those details come from the reviewed destination inventory. Missing targets
block confirmation. Storage checks retain SR type, sharing, backend and attachment
configuration; network checks retain bridge, MTU, locking, configuration and
physical/VM interface membership. Map and set ordering and unrelated SR capacity
accounting do not invalidate an otherwise unchanged review.

The upstream [VM recovery API](https://xapi-project.github.io/xen-api/classes/vm.html)
does not accept a general SR mapping. Its
[metadata importer](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/import.ml)
can resolve disks through other mechanisms and can introduce network records.
Consequently, after import this client independently checks every recovered data
disk against its reviewed target VDI before mapping NICs or authorizing cleanup.
New network records observed during the operation are reported with their names,
UUIDs and references for separate inspection; they are preserved by VM cleanup.

The importer can initially place NICs on an existing network matched by name or
bridge. It can also normalize the VM's device-model setting. The client accepts
only XAPI's documented `qemu-trad` to `qemu-upstream-compat` upgrade, or addition
of `qemu-upstream-compat` for an HVM VM without that setting; other configuration
checks remain in force. See the upstream
[device-model helper](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/xapi_vm_helpers.ml)
and [default values](https://github.com/xapi-project/xen-api/blob/master/ocaml/xapi/vm_platform.ml).
These accepted imports proceed through disk validation and the reviewed NIC
mapping before they receive rehearsal cleanup authorization.

An identity, configuration or disk check failure does **not** authorize further
NIC changes or automatic deletion. Its report gives the observed VM UUID,
reference and power state and warns that importer-chosen NIC networks may still
be live. Inspect the VM and isolate its NICs manually before any start. A halted
record alone does not establish that the client owns a safe rehearsal VM.

## Recovery and metadata rehearsal

**Recovery** restores the selected VM metadata and applies NIC mappings. Every
VM remains halted. Inspect the report, VM hardware, disk bindings, network
isolation, source fencing and storage replication before starting VMs separately.
Recovery does not grant rehearsal cleanup authorization.

**Metadata rehearsal** performs the same restore to reviewed internal networks
and records cleanup receipts for successfully restored and mapped VMs. It never
boots a VM. This avoids running a second guest against replicated production
disks, but also means it does **not** validate guest boot, service availability,
failover timing, disk consistency, or application recovery. A boot rehearsal
requires separately prepared isolated writable storage and remains a manual
acceptance task.

The shared `DrRecoverAction` now accepts an optional `force` flag. The shell uses
`false`; WinForms retains its previous default of `true`. A missing metadata
session now fails explicitly rather than reporting success without recovery.
The shell suppresses the nested import action's History entry; the outer action
reports import, disk validation and NIC mapping as one result. Standalone
WinForms recovery actions retain their History entries.

## Results and cleanup

Recovery runs sequentially and stops at the first failure. The report identifies
completed, failed and unattempted VMs and is retained in action History. A failed
or lost response can mean the server committed work the client cannot prove.
The window disables another recovery attempt after execution starts. It performs
no automatic retry, rollback, VM start or VDI deletion.

**Clean up rehearsal** requires explicit confirmation and a receipt created by
this window. It checks the original VM reference/UUID, complete captured VM and
attachment configuration, halted state, and absence of snapshots. Receipts are
issued only after source configuration, disk identity and all NIC mappings are
verified; failed or concurrently edited imports require manual inspection.
Cleanup removes the VM's VIFs, VBDs and VM record, preserving the actual VDIs and
network records. It never shuts down a running VM. A changed VM blocks cleanup;
an interrupted cleanup stops and requires manual inspection, with no retry.
Before each attachment deletion and the final VM deletion, cleanup checks the
complete remaining VM and attachment configuration again. It accounts only for
its own confirmed deletions; newly added attachments or other configuration
changes stop cleanup before the next deletion.

Closing the window does not undo a rehearsal. The history report includes VM
UUIDs and references, including every remaining cleanup receipt after a partial
or rejected cleanup; inspect those objects to perform manual cleanup if the window is closed
or the application exits. In a partial result:

- Confirm whether each reported UUID exists and whether a server task is still
  running. A lost response is not evidence that recovery failed.
- Inspect power state, disk identities, NIC destinations and any newly observed
  network records. Keep uncertain VMs halted.
- Remove only positively identified rehearsal VM records and attachments,
  preserving storage VDIs. Never remove a network solely because it appeared
  during recovery; another administrator may have created it.
- Reinspect source metadata and fresh destination inventory before a new attempt.
  Existing VM UUIDs must be reconciled explicitly, outside this editor.

## Limits and validation

Identity checks and writes are separate API requests. They are not a distributed
lock against another administrator. In particular `VM.recover(force=false)`
does not offer atomic create-if-absent semantics: a concurrent VM with an older
version could still be replaced between the UUID check and recovery. Coordinate
exclusive recovery maintenance, especially around UUIDs and replica attachment.
The client does not claim an atomic no-overwrite guarantee. Cleanup also cannot
make its final identity checks and deletions atomic with other clients.

Metadata-session logout is attempted after every successful open, including
inspection/review failure. A failed logout is surfaced; the remote session may
remain until server expiry. No automatic authentication or mutation retry is
added. Recovery metadata freshness means the server's latest flag and inspected
contents, not an independently established replication recovery point objective.

Automated tests cover metadata discovery and session lifetime, safe request
mapping, fresh permissions, reviewed source/target changes, shared recovery task
completion, disk binding checks, halted NIC mapping, partial or uncertain
outcomes, imported-network reporting, receipt-gated cleanup, stale or active VM
rejection, concurrent attachment/configuration changes during cleanup, draft
invalidation, explicit mapping confirmation and busy-state behavior. Import
normalization and map ordering, multi-VM recovery with existing destination NICs
and distinct disks, autostart rejection, nested History suppression and complete
manual cleanup reports also have regression coverage. The actual
Avalonia window is exercised by
`tools/AdvancedNetworking.UiProbe --access-recovery` at default and minimum sizes.
These checks do not substitute for a disposable live recovery exercise.
