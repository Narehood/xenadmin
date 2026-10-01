# Remaining modernization implementation and acceptance

The October 1 work closes the WinForms metadata audit, implements measured
inventory refresh improvements and provides an external RDP adapter. It does
not close deployment acceptance or every legacy feature gap. The supported
integration branch is `development`; WinForms remains the production client.

## Desktop and release acceptance

Use disposable installation directories/profiles and pools to complete
[platform acceptance](platform-acceptance.md): physical Windows/Linux scaling,
multi-server/console interaction, updater download/apply/UAC/rollback/restart,
network reconnect, HA/AD recovery and halted DR restoration/cleanup. Add native
RDP guest login, missing-client/plugin behavior and cancellation. Capture live
tree event rates, detail refresh and fully expanded layout before further
performance work. Hosted Windows/Linux automation must pass on each PR head.

The intermittent Windows blocking notification remains a separate investigation:
obtain the exact notification/event and affected file before choosing a fix.
Unsigned build artifacts still need a release signing/provenance strategy;
directory exclusions are not evidence that all Windows app controls allow them.
No protection setting is disabled by these changes.

## Plugin and external-tool strategy

There is no repository deployment inventory identifying required plugins or
tools. Retain the WinForms implementations until concrete requirements exist.
The provisional platform strategy is capability-specific adapters:

| Capability | Proposed shell path | Evidence required to implement |
| --- | --- | --- |
| RDP | Installed native client; implemented in this batch | Windows/Linux guest acceptance; embedded hosting only if a deployment requires it |
| Plugin web page | Reviewed HTTP(S) link opened in the user's browser | Actual plugin manifest/URL substitutions, authentication requirements and supported workflow; no hypervisor session secrets in URLs |
| Embedded plugin integration | Modern browser component only for a demonstrated integration requirement | Supported Windows/Linux runtime, isolation, navigation/origin policy and credential bridge review; IE/ActiveX assumptions are not carried over automatically |
| SSH/other external tool | Explicit adapter with reviewed object/endpoint and separately supplied arguments | Required executable/protocol, native credential UI, executable discovery and early failure/cancellation behavior |
| Arbitrary legacy command template | Retain WinForms path pending a requirements/security review | Installed template formats, variable expansion, trust boundary and credential/logging behavior |

Adapters must re-resolve the selected object/connection before launch, validate
targets by protocol, avoid shell interpretation, and keep passwords/session
tokens out of command lines. A generic plugin runner is not implemented merely
to advertise parity. The RDP adapter provides the first concrete pattern.

## Extended disaster recovery sequence

The current [DR editor](disaster-recovery.md) recovers eligible standalone VMs
halted onto already attached storage and records cleanup receipts. Live recovery
and replica binding must be proven before widening eligibility. Keep source
fencing and exclusive recovery maintenance explicit: API validation and writes
are separate requests and cannot promise atomic no-overwrite behavior.

| Slice | Review/execution design | Required validation before enabling |
| --- | --- | --- |
| Appliance groups | Inspect the complete appliance and members; review every disk/network mapping, UUID conflict and start dependency; adapt shared `DrRecoverAction` while keeping all members halted | Complete/partial appliance recovery, existing appliance/member conflicts, uncertain task outcome, changed membership, permissions and receipt-scoped cleanup without deleting disks |
| Snapshot chains | Inspect parent/child and disk relationships as one graph; prove every required replica identity and account for imported snapshot records | Broken/incomplete chains, UUID collisions, changed lineage, missing replica disks, partial import and cleanup preserving VDIs |
| Special hardware | Separate capability-specific plans for vTPM, GPU, USB and passthrough; validate destination identity and server support rather than removing the current exclusion | Actual destination hardware/key/state compatibility, server errors and recoverable partial results on disposable deployments |
| Running rehearsal | Separate explicit start after halted recovery and fresh topology review; require proven isolated replica storage, isolated NICs, source fencing and autostart disabled | Guest boot/application integrity, absence of production traffic/writes, start interruption and uncertain outcomes, orderly shutdown and complete receipt-scoped cleanup |

Shared foundations are `XenModel/Actions/DR/DrRecoverAction.cs`,
`StartVMsAndAppliancesAction.cs` and the existing metadata-session workflow.
Their broad start/destroy operations must not be reused without the shell's
identity, isolation and cleanup protections. Each slice needs worker/RPC tests,
editor validation and a disposable live exercise; widening a filter alone does
not complete recovery support.

Suitable disposable hosts/machines were previously unavailable and manual
testing was deferred. These rows remain pending, with their blockers recorded;
automated/synthetic evidence does not establish guest failover or production
readiness. No release is published by this implementation batch.
