# Access, recovery, and platform validation

Started 2026-09-27 from `development` commit `123679abd` (PR #50 merged).
The working tree was clean. This pass implements the four follow-up workstreams
requested by the user, with three implementation agents and an integration and
acceptance coordinator.

## Work and completion criteria

| Workstream | Implementation and evidence required | Environment-dependent gate |
| --- | --- | --- |
| Reboot/console investigation | Trace guest reboot and console reconnection, reproduce concrete transport/lifecycle defects, add regression coverage, and record what the evidence proves | Reproduce the reported hang with the affected client/guest and independently check guest responsiveness |
| AD and RBAC | Shell domain join/leave and subject/role editor using shared actions; review current identities and permissions again before execution; preserve administrative recovery and redact credentials; exercise success, rejection, and partial failures | Disposable domain and pool validation with restricted users and interrupted operations |
| Disaster recovery | Inspect metadata, review VM/storage/network targets, distinguish recovery/rehearsal, track partial completion and cleanup through shared actions; exercise worker and editor boundaries | Recovery and cleanup using expendable replicated storage and VMs |
| Platform acceptance | Integrated locked builds, both test suites, Windows package execution, actual editor layout/interaction probes, and native Linux CI; update the acceptance evidence | Physical desktop behavior, real update/UAC/rollback/restart, live networking and HA failover |

## Integration order

1. Implement the console, AD, and DR changes in separate files and review each
   against the existing shared action semantics. The coordinator owns shared
   navigation and central documentation.
2. Run focused regressions for each workstream, then the complete shell suites
   in Release/Debug and shared tests on `net481` and `net10.0`. Serialize local
   builds because all agents share project output directories.
3. Exercise the actual new windows with synthetic inventory and isolated
   profiles. Build WinForms using its documented RDP prerequisites, package the
   Windows shell, and run the packaged helper/runtime checks.
4. Record results, source revisions, package hashes, and remaining limitations
   in the handoff and platform acceptance record. Use hosted CI for native Linux
   execution; cross-publishing on Windows is not Linux acceptance.

## Acceptance boundaries

The user confirmed during this pass that no disposable environments are
available. The reboot report involves Windows 11 and a Debian 12 guest becoming
unresponsive after `shutdown -r now` over SSH; the client build and console state
are unknown. The [investigation](reboot-hang-investigation.md) separates those
observations from the reproduced console parsing defect. Missing environments remain **pending**;
unit tests and offscreen windows do not establish live failover or successful
installation. WinForms remains the supported production client.

Progress and final evidence are recorded in [the handoff](ASTRA_HANDOFF.md) and
[platform acceptance](platform-acceptance.md). The older September handoff
sections are historical snapshots, including their .NET 8 targets and PR status.
