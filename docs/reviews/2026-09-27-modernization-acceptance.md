# Access and recovery validation — 2026-09-27

This initial snapshot is followed by the
[PR review and acceptance rerun](2026-09-27-pr51-54-review.md), including native
Linux runner evidence and the expanded 98-check window probe.

This pass starts from merged `development` commit `123679abd` and implements the
[four-workstream plan](../modernization-execution-plan.md). Three implementation
agents handled console investigation, AD/RBAC and DR; the coordinator integrated
navigation, actual-window probes, documentation and acceptance tooling. The
console agent also independently reviewed the acceptance runner and AD/DR code.

## Review revisions

The work is split into four draft PRs. Merge in the order shown, retargeting each
remaining layer to `development` after its base lands. Each layer receives the
normal Windows/Linux Test Builds workflow; the linked Checks pages show the
current hosted results rather than treating local results as native Linux proof.

| Change | Implementation commit | Review and hosted checks |
| --- | --- | --- |
| Console padding | `a296d4feb` | [PR #51](https://github.com/Narehood/xenadmin/pull/51), [checks](https://github.com/Narehood/xenadmin/pull/51/checks) |
| Directory access/RBAC | `bb2fcb431` | [PR #52](https://github.com/Narehood/xenadmin/pull/52), [checks](https://github.com/Narehood/xenadmin/pull/52/checks) |
| Halted recovery | `54e174aa8` | [PR #53](https://github.com/Narehood/xenadmin/pull/53), [checks](https://github.com/Narehood/xenadmin/pull/53/checks) |
| Combined acceptance | `221bd8525` | [PR #54](https://github.com/Narehood/xenadmin/pull/54), [checks](https://github.com/Narehood/xenadmin/pull/54/checks) |

Later documentation-only commits add these review references without changing
the implementation or local package recorded below. No release was published.

## Local automated results

`scripts/Invoke-PlatformAcceptance.ps1` completed successfully using SDK
10.0.401 and bundled runtime 10.0.12 on Windows 11 (10.0.26200). Full source was
validated together before splitting the changes into review commits. The local
manifest records the starting commit and modified/untracked file list; hosted
checks validate the committed revisions separately.

| Check | Result |
| --- | --- |
| Locked solution restore | Passed |
| Release and Debug solution builds | Passed; existing 28 Windows ACL analyzer warnings per configuration, no errors |
| Shell tests | 828 passed in Release, 828 in Debug; none failed or skipped |
| Shared tests | 73 passed on net481, 73 on net10.0; none failed or skipped |
| New behavior regressions | 11 RFB, 53 AD/RBAC, 51 DR cases included in the shell totals |
| WinForms compatibility | 32,240 resources in 290 sets loaded in each configuration; settings initialization passed |
| Proxy authentication | All 12 existing tunnel/HttpWebRequest cases passed |
| AD/DR actual windows | 53 binding, interaction, confirmation, busy/close, failure and layout checks passed |
| Existing network and settings windows | Networking, connection settings and beta settings probes passed |
| Windows self-contained ZIP | Published and executed successfully; all four malformed updater modes exited 1, with startup hooks disabled |
| Checked-in portable lockfiles | Hashes unchanged throughout acceptance |

The actual windows ran offscreen with synthetic inventory/delegates and isolated
settings. Screenshots include default/minimum layouts rendered at 1×/1.5×/2×;
these are not physical desktop/DPI tests. Main-window menu integration was
compiled and reviewed; the window probe directly instantiates the editor windows.

Local RDP interop DLLs are the unchanged trusted artifacts retained from the
previous CI pass. `-SkipRdpAxImp` was explicitly selected because AxImp is absent;
the manifest records both DLL hashes. Hosted Windows builds must generate the
interop normally. No SDK, registry, profile or server settings were installed
or changed by this pass.

Local evidence is in ignored `artifacts/access-recovery-acceptance`: command logs,
TRX files, screenshots, `acceptance.json`, native smoke JSON and the Windows ZIP.
Package SHA-256:
`8bfc8f88c104801324f3f157a9df43cc1b4c73e30c2a940d82b34cd740f77be4`.
This is a local validation build, not a published release.

## Review findings resolved during implementation

- Fragmented RFB padding desynchronized actual client parsing. Six of eleven new
  cases failed before correction; all eleven pass afterward.
- Switching from an existing AD subject to Add could inherit unrelated unchanged
  role validation. Operation-scoped selection and a regression now prevent it.
- AD role grants now reread the pool/domain/role definitions and exact created
  subject after creation. Unknown or changed default roles stop further grants.
- DR permission checks now query the server again, including before later VMs.
  Replica attachment, VDI identity and network configuration are revalidated.
- Suspended/unknown states and SRs still owned by a DR task are rejected before
  recovery. Actual imported disks are checked against reviewed target VDIs.
- Rehearsal cleanup receipts require successful verified mappings and unchanged
  VM/attachment configuration. Partial or concurrent changes require inspection.
  Import-created networks are reported and preserved, not silently deleted.
- The acceptance runner records nonzero exits and preflight/log failures, keeps
  per-scenario UI evidence, verifies locks even on failure and restores its process
  environment. Its PowerShell parser and isolated error fixtures passed on 5.1;
  Linux execution remains for native CI.

## Remaining acceptance

The user confirmed no disposable environments are available. Physical desktop,
real writable/protected update installation, UAC cancellation/different-account
elevation, rollback/unelevated restart, live networking/HA, directory join and
restricted-user recovery, and actual DR storage/guest recovery remain **pending**.
The [platform checklist](../platform-acceptance.md) retains those gates.

The original Debian 12 reboot incident remains unresolved. The known facts are
Windows 11, a reboot initiated over SSH with `shutdown -r now`, loss of SSH and
subsequent unresponsiveness requiring forced shutdown. Console state, last guest
message and client version are unavailable. The reproduced parsing defect is not
claimed to explain that incident; see the [investigation](../reboot-hang-investigation.md).

The new DR rehearsal is deliberately a halted metadata workflow. It does not
establish successful boot, application failover, disk replication integrity or
running-guest storage isolation. Concurrent administrators remain outside the
atomicity of separate XenAPI reads/writes. WinForms remains the supported client.
