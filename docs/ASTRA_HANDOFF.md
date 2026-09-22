# Astra / Astro handoff

Last updated: 2026-09-22. Initial review, remediation, and shell follow-up fixes.

## Start here

### Sandy console paste release (2026-09-22)

Merged [PR #46](https://github.com/Narehood/xenadmin/pull/46) into `development`
as `7d232efb936c81b919e65c6c8f631ed7f9c0eb2e` and published
[Sandy 2026.9.22.1](https://github.com/Narehood/xenadmin/releases/tag/v2026.9.22.1)
from that commit via [release run 35724382382](https://github.com/Narehood/xenadmin/actions/runs/35724382382).
The tag resolves to the merged commit and the release is the latest stable build.

[CI run 35724354529](https://github.com/Narehood/xenadmin/actions/runs/35724354529)
passed full Release/Debug builds, both self-contained platform publishes, **296
shell tests in each configuration**, and **73 shared tests on each framework**
(738 passing executions, none failed or skipped). CodeQL actions/C# analysis and
dependency submission also passed for the merged commit. Existing Windows ACL
test analyzer warnings remain.

Downloaded both public Windows/Linux assets and verified their GitHub SHA-256
digests, required files at the archive root, and self-contained runtime payloads.
The Linux executable retains mode `0755`. Metadata inspection of both packaged
shell assemblies confirmed version `2026.9.22.1`, codename `Sandy`, and the console
paste window/view-model types. Release notes describe the feature, limits, and
platform downloads. Verification scripts, release metadata, downloads, and results:
`%LOCALAPPDATA%/Temp/sandy-console-release-check` (`verify.py`, `Program.cs`, and
`v2026.9.22.1/verification.json`).

All Cursor review threads have recorded dispositions and are resolved. The earlier
regression coverage and Windows desktop checks remain documented below. Live
guest/host paste, guest keyboard layouts, and Linux desktop interaction remain
manual validation limits; package inspection does not claim to exercise them.

### Cursor console paste review recheck (2026-09-22)

Rechecked all five Cursor inline comments in [PR #46](https://github.com/Narehood/xenadmin/pull/46)
against `08bccac17eb750706f6e45216ece23763ea6ff1e`. No additional code defects
were confirmed. The failed-read draft loss, rejected-send draft loss/status, and
editor size-limit findings remain fixed by `8310bd7f14d3e9ac0f05bd775b065b27b4656581`.
Both duplicate-dialog comments remain unconfirmed: the shared async command
disables all bound paste buttons throughout the awaited dialog lifetime.

Rebuilt and reran the Windows desktop probe against this head. Actual main and
pop-out button clicks could not open a second dialog; buttons stayed disabled
after Send and re-enabled after Close. Native paste/typing limits, rejected
binding restoration, and selection/caret checks also passed. Fresh evidence is
`%LOCALAPPDATA%/Temp/sandy-console-paste-review/bin/Release/net8.0/review-20260922.log`.
The probe uses synthetic clipboard data and transport, leaving the system
clipboard untouched. Locked Release suites passed again: **296 shell tests**
(54 paste cases), **73 shared tests on net481**, and **73 on net8.0**. Existing
Windows ACL analyzer warnings and live guest/host/Linux validation limits remain.
Only the review record changed in this follow-up.

### Console paste completion review follow-up (2026-09-21)

Confirmed the [final-character cancellation finding](https://github.com/Narehood/xenadmin/pull/46#discussion_r4057919290)
against PR head `8310bd7f14d3e9ac0f05bd775b065b27b4656581`. The sender performed
a cancellable pacing delay after its last successful write, allowing cancellation
to turn a completed paste into a misleading "Paste stopped" result. Pacing now
runs only between characters; final progress and delivery tracking still update
before completion. Cancellation before a remaining character is unchanged.

Five new regression cases exercise caller/connection cancellation on final
progress for single-character, longer, and CRLF-normalized input, plus successful
dialog status and draft cleanup when cancellation arrives on the final write.
All five cases failed before the fix and pass afterward. Locked Release validation:
**296 shell tests passed** (including 54 paste cases), plus **73 shared tests on
net481** and **73 on net8.0**. Existing Windows ACL analyzer warnings remain.
The earlier findings remain fixed or unconfirmed as recorded below; no changes
were made to toolbar ownership, clipboard scope, retry behavior, or WinForms.
Live guest/host delivery and Linux desktop checks remain outstanding.

### Console paste PR review verification (2026-09-20)

Checked all [PR #46 review findings](https://github.com/Narehood/xenadmin/pull/46#pullrequestreview-5261117169)
against the actual PR head `f421dfd56612c8c45e4651a5f626c3cfae5c5b8c` before
editing. Confirmed three defects with five failing regression cases: failed,
cancelled, and oversized clipboard loads erased the reviewed draft; rejected
sends before the first write also erased it; and the editor accepted text beyond
the cap. Only those defects were changed.

Clipboard loads now replace the draft only after a usable snapshot arrives;
failed reads preserve text, visibility, and Enter/Tab consent. Sending tracks
completed characters synchronously rather than relying on queued UI progress.
Rejection/cancellation before any write preserves the draft and reports nothing
sent. Partial sends retain their count even after later connection notifications.
An attempted write that throws can have partially reached the guest, so it still
clears the draft and reports uncertain delivery; zero completed keys alone is
not proof of no transmission. The session's final pre-write guard explicitly
distinguishes a rejection that has not attempted network output.

The editor enforces 4,096 characters for typing, native paste, and binding updates.
Native paste is intercepted before Avalonia can truncate it; oversized snapshots
or combined drafts are refused without modifying the prior draft. Selection and
caret behavior is preserved for accepted paste. Empty successful Load clipboard
still explicitly replaces the draft; empty editor paste leaves it unchanged.

The duplicate-dialog report was **not confirmed**: the generated
`AsyncRelayCommand` already disables execution for the entire awaited
`ShowDialog` lifetime. An offscreen Windows probe on the original PR head opened
the actual dialog from a popped-out console, verified that main/pop-out buttons
were disabled, and invoked their click handlers without creating another dialog.
They stayed disabled after Send and re-enabled after Close. Source search found
no other callers bypassing `CanExecute`. Toolbar visibility and command behavior
were therefore left unchanged. The generic docstring-coverage warning and the
review's informational residual notes did not establish additional defects.

Validation after the fixes: 291 shell Release tests passed (17 additional review
regression cases, 49 paste cases total); shared tests passed 73 on net481 and 73
on net8.0. Both suites used locked restores. Existing Windows ACL test analyzer
warnings remain. Coverage includes failed/cancelled/oversized reads, no-write
rejections, uncertain first writes, retained partial counts after disconnect,
reload consent, and bounded editor selection replacement.

Evidence: `%LOCALAPPDATA%/Temp/sandy-console-paste-review`, with original
`Program.baseline.txt` / `bin/Release/net8.0/baseline.log` and updated `Program.cs`
/ `review.log`. The updated desktop probe also verified oversized native paste,
combined-length rejection, typing without truncation, restored editor bindings,
and selection/caret updates. Both probes use synthetic clipboard providers and
leave the real system clipboard untouched. Live guest/host and Linux limitations
from the feature handoff remain.

### Secure console text paste (2026-09-20)

The Sandy Avalonia app now offers **Paste text…** for VM/host consoles and the
pop-out/full-screen toolbar. See [usage and limits](console-paste.md). The classic
WinForms client is unchanged. Paste reads the local clipboard only after an
explicit request, hides the draft until revealed, and sends a reviewed snapshot
as paced RFB keystrokes. It accepts up to 4,096 ASCII characters, rejects hidden
controls/non-ASCII without replacement or truncation, and requires explicit
Enter/Tab acknowledgement. It never appends Enter or retries sent text.

`ConsolePasteTarget` binds the draft to a captured transport generation. The
session requires an authenticated/encrypted `SslStream` after HTTP redirects,
blocks concurrent local input while sending, and drops the transport on write
failure. Stop now invalidates generations, and stale connection callbacks cannot
mark a replacement session connected. Existing guest/host retry policy remains.
Successful, partial, or uncertain sends and closing clear the draft; no-write
rejections preserve it. Editor undo is disabled; payloads and provider
exception details are not logged, persisted, or copied back to the local
clipboard. Managed-memory erasure and guest application history are not promised.

Validation: 274 shell Release tests passed, including 32 new paste cases; shared
tests passed 73 on net481 and 73 on net8.0 with locked restores. Existing Windows
ACL analyzer warnings remain. Synthetic streams cover real RFB bytes, write
failure, cancellation, input suppression, transport replacement, and disposal;
view-model tests cover clipboard snapshots, validation, consent, and privacy.
An offscreen Windows harness exercised the actual dialog open/close handlers,
bindings, hidden preview, disabled undo, Enter consent, exact text output, and
draft clearing; rendered it at 100/150/200% and minimum size; and checked the
host/VM pop-out toolbar at minimum size. Evidence:
`%LOCALAPPDATA%/Temp/sandy-console-paste-check` (`Program.cs`, and
`bin/Release/net8.0/results.log` plus PNGs). The harness used synthetic clipboard
providers and transports, leaving the real system clipboard untouched.
Live VM/host delivery, guest keyboard layouts, and Linux desktop interaction
remain manual validation requirements. Full WinForms builds and platform
publishes run in PR CI.

### Sandy network management (2026-09-13)

Merged via [PR #45](https://github.com/Narehood/xenadmin/pull/45) as
`5a7f1ad1def40e10ce64eb73bc97fb236d8a10f7` on `development`. PR CI run
`34764152831` passed full Release/Debug builds, both self-contained platform
publishes, 242 shell tests in each configuration, and 73 shared tests on each
framework (630 executions). CodeQL actions/C# analysis also passed.

Published [Sandy 2026.9.13.1](https://github.com/Narehood/xenadmin/releases/tag/v2026.9.13.1)
from that merged commit via release run `34764406166`. Both Windows/Linux
downloads matched their GitHub SHA-256 digests and contained the required files
at the archive root. Linux executable permissions were retained. Reading each
packaged shell assembly confirmed version `2026.9.13.1` and codename `Sandy`.
Verification script and downloaded evidence:
`%LOCALAPPDATA%/Temp/sandy-network-release-check`.

The Avalonia Network tab now exposes host/pool network creation, editing, and
removal, plus VM interface add/edit/remove/connect/disconnect. The network editor
supports private networks and VLANs on an existing physical NIC or bond, VLAN IDs
(including supported VLAN 0), uplink changes, names/descriptions/tags, automatic
inclusion on new VMs, and MTU. The VM editor selects an existing network/VLAN,
generates or sets a MAC, and configures bandwidth limits in KB/s.

`NetworkManagement` validates current cache objects and builds descriptors;
`ShellNetworkAction` rechecks on the action worker and delegates to the existing
NetworkAction, SaveChangesAction, UnplugPlugNetworkAction, and VIF actions.
Dialogs retain drafts during inventory updates, keep failures visible, and
report when hot-plug requires a VM shutdown/start. Unchanged VIF settings do not
replace the interface. Row commands retain their original connection/object
identity and resolve it again after confirmation. Advanced VIF settings and
unknown QoS parameters survive edits. Dictionary copies are modified before
assignment because generated equal-value setters otherwise retain shared data.

Management, IPv4/IPv6-configured, disallow-unplug, and cluster PIFs are protected
from topology changes/removal; active VM interfaces block VLAN/MTU changes.
Network removal requires no VIF references, including stopped VMs. Physical,
bond, tunnel, and SR-IOV topology changes are not offered. Those operations and
host management-IP reconfiguration remain in WinForms. This adds VLAN and guest
interface management without claiming parity with every legacy network wizard.

Validation: 242 shell Release tests and 73 shared tests on each of net481/net8.0
passed with locked restores. Thirty-three new cases cover VLAN/MTU bounds,
coordinator uplinks and pool-wide duplicates, protection changes after opening an
editor, VM state/operation/limit checks, missing targets, MAC/QoS validation,
descriptor isolation, advanced settings, no-op VIF edits, and row action policy.
An offscreen Windows harness exercised the actual editors and Network tab at
100/150/200% and minimum window sizes, command/target bindings, busy enablement,
protected removal, save-time close guards, and textbox-to-request updates.
The harness also ran successful/failing asynchronous actions and verified final
status delivery before the editor resumes. Completed action subscriptions are
removed so task history does not retain closed editor view models.
Evidence: `%LOCALAPPDATA%/Temp/sandy-network-check` (`Program.cs` and
`bin/Release/net8.0/results.log`, PNGs). Existing Windows ACL test analyzer
warnings remain. No live pool mutations or Linux desktop interaction were
exercised. Shared actions perform sequential API calls; a partial failure can
leave some settings changed or an old VIF removed, which the editors explain.
Full WinForms builds and both platform publishes are validated in GitHub CI.

### Sandy branding and memory performance (2026-09-11)

Merged via [PR #44](https://github.com/Narehood/xenadmin/pull/44) as
`6aa06d0469679e1cb029d118ca5867bc6da97104` on `development`. PR CI run
`34618281274` passed full Release/Debug builds, both self-contained platform
publishes, 209 shell tests in each configuration, and 73 shared tests on each
framework (564 executions). CodeQL actions/C# analysis and automated PR checks
also passed.

Published [Sandy 2026.9.11.2](https://github.com/Narehood/xenadmin/releases/tag/v2026.9.11.2)
from `9684c1d0520b96c9051c4dc21a9a329c97e020fe` via release run `34618991501`.
Both Windows/Linux downloads were checked against their GitHub SHA-256 digests
and required archive-root files. Reading each packaged shell assembly confirmed
version `2026.9.11.2` and codename `Sandy`.

The build codename defaults to `Sandy` in MSBuild and the release workflow. The
shell window/About/project title is `XCP-NG Center (Unofficial Client)` and the
sidebar footer is `Unofficial Client`.

Memory archives now retain total and free values independently, normalized to
bytes. Previously the parser replaced free with used, discarded total, and the
chart auto-scaled to the usage peak. It also paired columns by name/position,
which could match another object's data or depend on column order. The graph now
derives used only from the same object's matching timestamps, draws Used/Free/Total,
and scales to the highest recorded capacity in the selected interval. Axis,
summary, and hover labels use binary memory units (GiB/MiB). VM allocation in
bytes is paired with guest free memory in KiB; without guest data, only allocation
is displayed. Missing/invalid pairs do not fabricate usage. Missing total data
leaves a correctly labelled free-only graph with no claimed capacity.

Saved memory layouts also receive the corrected series. Saving a layout writes
only real RRD source names, deduplicating the derived used/free source so it stays
compatible with WinForms layouts. Shared WinForms graph code is unchanged.

Validation: 209 shell Release tests and 73 shared tests on each of net481/net8.0
passed with locked restores. Seventeen new regression cases cover 32 GiB capacity,
RRD column order, all four intervals, saved-layout round trips, VM unit conversion,
missing guest/total data, invalid samples, object/timestamp isolation, historical
allocation changes, and CPU percentage scaling. An offscreen desktop harness
checked the main/sidebar/About labels and Sandy splash/build name, and rendered
the actual memory chart at 100/150/200% scale, with filled and hover variants.
Evidence: `%LOCALAPPDATA%/Temp/sandy-memory-check` (`Program.cs` and
`bin/Release/net8.0/results.log`, `memory-*.png`). Existing Windows ACL test analyzer
warnings remain. Live host/guest RRD traffic and Linux desktop rendering still
need manual testing; full WinForms builds and platform publishes run in CI.

### Circular update progress and standalone inventory (2026-09-11)

Merged via [PR #43](https://github.com/Narehood/xenadmin/pull/43) as
`af4dc70f673a14fd4eaeb7320523a24e84184922` on `development`. PR CI run
`34614312352` passed full Release/Debug builds, both self-contained platform
publishes, 192 shell tests in each configuration, and 73 shared tests on each
framework. CodeQL actions/C# analysis and the automated PR review also passed.

The compact update button now displays a 34-pixel progress ring inside its
36-pixel footprint. Download percentage fills the ring clockwise; checking,
unknown-length downloads, and preparation/verification use a rotating arc.
Completion hides the ring. The overlay passes pointer input through, and the
existing hover panel retains its detailed progress and release notes. The icon
padding also now accounts for the button border. A retry resets stale progress.

Infrastructure uses the shared visible-pool rule (`Helpers.GetPool`) to distinguish
an unnamed standalone pool-of-one from a visible pool. A standalone connection
returns the actual host as its root, with real VMs (including stopped VMs without
a home host) and visible storage directly underneath. It retains host identity,
status, privacy formatting, and selection/action resolution. Named one-host pools
and all multi-host pools keep the cluster/host layout and existing VM/SR placement.

Validation: 192 shell Release tests passed; shared tests passed 73 on net481 and
73 on net8.0, all with locked restores. Seven new regression cases exercise the
actual tree builder with synthetic caches and real status icons: standalone
running/stopped VMs and VM filtering, local/shared storage without duplicates,
named one-host and named/unnamed multi-host pools, topology changes preserving
object identity, and an empty cache. An offscreen desktop harness rendered the
actual update control in six states at 100/125/150/200% scale and checked geometry,
animation, live progress bindings, completion, and popup/input behavior. Evidence:
`%LOCALAPPDATA%/Temp/shell-ring-host-check` (`Program.cs`, and `bin/Release/net8.0`
containing `results.log` and `ring-*.png`). Existing Windows ACL test analyzer
warnings remain. Live server connections, real update installation, and Linux
desktop interaction still require manual testing; GitHub CI supplies the full
WinForms builds and both platform publishes.

### Saved-server startup and update hover follow-up (2026-09-08)

The main view model now restores saved-server metadata and selects the first
inventory placeholder before the main window is shown. Empty profiles retain
the welcome page; saved servers appear even without a password, with a locked
vault, or with auto-reconnect disabled. No credentials are decrypted or network
connections started by restoration. The existing post-window unlock/reconnect
path reuses the restored nodes. Invalid addresses are skipped and equivalent
host/port entries are deduplicated.

The update popup keeps light dismissal but passes overlay input through to its
trigger. Previously the overlay intercepted the stationary pointer, causing
trigger exit, delayed close, trigger entry, and repeated reopening. Keyboard
focus on the trigger also now keeps the panel open; focus loss schedules closure.

Validation: 185 shell Release tests; 73 shared tests on each of net481 and net8.0.
Four new regression cases cover metadata-only restoration with missing/locked
credentials, endpoint deduplication/invalid entries, and empty profiles. A desktop
harness checked the actual main window before/after Show with disposable empty,
saved, and locked profiles (auto-reconnect disabled). A five-second stationary
hover reproduced 15+ opens with the previous overlay behavior and exactly one
open/zero closes with the fix; entering notes kept the popup open and leaving
closed it. Harness/evidence: `%LOCALAPPDATA%/Temp/xcpng-update-restart-fix/ui`,
`%TEMP%/hover-regression.ps1`, `hover-events-*.log`, `startup-probe-results.log`.
No live pool reconnection or Linux desktop interaction was exercised. Existing
Windows ACL test analyzer warnings remain. Full platform builds run in GitHub CI.

### Update restart and compact control follow-up (2026-09-08)

Merged via [PR #41](https://github.com/Narehood/xenadmin/pull/41) as
`7bc16a686` on `development`. CI run `34178319089` passed full Release/Debug
builds, both self-contained platform publishes, shared tests on both frameworks,
and shell tests in Release and Debug. CodeQL actions/C# analysis also passed.
The release workflow for ALCYONE `2026.9.8.2` targets that merged source.

The reported source build was DRAGON `2026.8.30.8` (`b01829b8e`). Its updater
launches downloaded code from `<installation hash>/v<version>/payload`. The new
bootstrap accepts only separately authenticated `launch-<guid>` or protected
launch directories. Previously both new helper modes rejected DRAGON's arguments
silently after DRAGON had already exited. Do not fix this by accepting the old
writable cache as trusted installation input.

The staged executable now recognizes that legacy invocation for a visible
manual-upgrade explanation. It can reopen the original unelevated application
when its live parent executable establishes the installation path; it never
installs legacy cached code or restarts the full app using an elevated token.
DRAGON still needs a one-time manual extraction into a new folder to acquire the
trusted bootstrap. If its parent exits before it can be identified, the guidance
window remains but the user must reopen the application themselves.

For modern updates the original application waits for the broker's initialized
signal and the apply helper's validated-context signal before acknowledging the
broker and shutting down. A separate unelevated progress window survives the main
app, reports installation/restart status, and retains errors. Immediate process
exit after relaunch is treated as failure. This checks early process survival,
not successful login or complete desktop initialization.

The update notification is now a 36-pixel sidebar button inspired by the user's
T3 Code screenshots and its `SidebarUpdatePill` / `SidebarUpdateReleaseNotes`
components. Clicking checks again; an indicator marks updates/errors. Hover or
keyboard focus opens a scrollable panel containing all stable release notes
between the current and offered versions. GitHub history is paginated, sorted by
version, and deduplicated. Failure to load history preserves the offer and latest
notes with an explanation. Text is rendered without remote HTML/images. Downloads
stay in this panel and no longer automatically open a restart confirmation;
restart requires an explicit action.

Validation: 181 shell Release tests passed; shared tests passed 73 on net481 and
73 on net8.0. New tests cover helper readiness, legacy path recognition, paginated
history, ordering/deduplication/filtering, history failure, and readable notes.
Existing Windows ACL tests emit CA1416 analyzer warnings on this local build.
Published Windows helper probes verified the legacy guidance/recovery and the
modern broker's readiness, status window, and relaunch using disposable stub
installations. A separate desktop harness verified the actual compact control's
hover panel and multiple versions via UI Automation and a screenshot. Evidence:
`%LOCALAPPDATA%/Temp/xcpng-update-restart-fix` (`probe.ps1`, `ui-probe.ps1`,
`update-control.png`). The probes do not exercise real UAC, protected file
replacement, a live pool, or Linux execution; those limitations remain.

Read the [remediation record](reviews/2026-09-07-remediation.md) for the current
source changes, regression coverage, migration details, and remaining validation.
All thirteen initial findings now have corresponding source fixes on
`development`. Shell tests pass in Release and Debug, and shared tests pass on
both target frameworks; counts are below. Landed on `development` as
`0562401744055ecafd704776db8f0fcea8c69d46`. Desktop, live-pool, Linux runtime,
and real UAC installation checks have not been completed by this pass.

The [initial review](reviews/2026-09-07-initial-review.md) preserves the original
priorities and reproduction evidence as a dated snapshot. Its open-findings
language and baseline test counts describe the pre-remediation tree.

Reviewed branch: `development`.
Reviewed HEAD: `b01829b8e25c02d48cce7e3cb64897d404bd72cb`.

Existing user work at the start of the review:

- `.github/workflows/publish-shell-release.yml`
- `XcpNgCenter.Shell/Services/ShellUpdateInstaller.cs`
- `XcpNgCenter.Shell.Tests/ShellUpdateTests.cs`
- Untracked `XcpNgCenter.Shell/packaging/INSTALL.TXT`

Those edits wrap published portable applications in an application subfolder,
add installation instructions, and normalize the layout when staging updates.
They were preserved. The findings concern code already present at HEAD, including
the updater's existing trust design; they were also checked against the working
tree. Line numbers in the review refer to that working tree.

## Project map

| Project | Responsibility | Target |
| --- | --- | --- |
| `XenAdmin` | Supported WinForms UI, RDP/ActiveX, plugins, Windows settings | `net8.0-windows` |
| `XcpNgCenter.Shell` | Avalonia UI, view models, shell preferences/TOFU, updater | `net8.0` |
| `XcpNgCenter.Rfb` | WinForms-independent RFB protocol client | `net8.0` |
| `XenModel` | XenAPI bindings/extensions, connection/cache/events, administrative actions | `net481;net8.0` |
| `XenOvfApi` | OVF descriptors, package validation, manifests, appliance utilities | `net481;net8.0` |
| `XenCenterLib` | Archive, encryption, HTTP and other shared helpers | `net481;net8.0` |
| `CommandLib` | Shared command abstractions | `net481;net8.0` |
| `XenCenterLib.Tests` | Shared helper and security regression tests | `net481;net8.0` |
| `XcpNgCenter.Shell.Tests` | Updates, credentials/TLS, shared OVF/import security, logging, graphs, UI state/scheduling | `net8.0` |

The shell references shared libraries and RFB, not the WinForms project. Many
types in `XenModel` still use the `XenAdmin` namespace. API-generated code and
designer/schema files account for part of the repository's size; distinguish
them from manually maintained orchestration when planning cleanup.

Useful paths and flows:

- Shell startup: `Program.cs`, `App.axaml.cs`, `Services/ShellBootstrap.cs`.
  Bootstrap supplies the shared configuration provider, UI synchronization,
  proxy policy, TLS callback, and action history.
- Inventory: `XenModel/Network/XenConnection.cs` updates its cache and emits
  events; shell `MainViewModel.cs` rebuilds infrastructure and detail panes.
  `MainViewModel.Actions.cs` contains selection-dependent administrative actions.
- Mutations: shell view models adapt shared `XenModel/Actions` using
  `Services/ShellActionRunner.cs`. Review shared action semantics before adding
  a second implementation of a host/VM operation.
- Credentials: `MainPasswordVault` owns migration and the unlocked session key;
  `MainPasswordProtection` implements PBKDF2-SHA256/AES-GCM. `SavedServerStore`
  atomically stores metadata and ciphertext together. Legacy `mp1:` credentials
  migrate on successful unlock; obsolete settings are cleared afterward. The
  new `mp2:` document is a one-way upgrade, and old copied files remain exposed.
  Normal Windows storage uses DPAPI; Unix uses a private local device key.
- TLS: shell `TofuCertificateValidator`/`TofuCertificateStore` and WinForms
  `XenAdmin/Network/SSL.cs` remain separate policy implementations. Both enforce
  existing pins before accepting a CA-valid chain. Unpinned CA-valid hosts use
  OS trust without creating a TOFU pin.
- Import: shell `OvfImportViewModel` -> `XenOvfApi/Package.cs` and `OVF.Validate`
  -> shared `ImportApplianceAction` -> `Actions/OvfActions/Import.cs`.
  `OvfFilePath` contains local references; `ImportTemporaryFiles` tracks only
  files the operation created in the appliance directory. Manifests require
  descriptor/payload coverage, including ordinary `.mf`/`.cert` resources;
  references to the descriptor's actual sibling manifest/certificate are invalid
  (R06; DSP0243 §5.1 source in the remediation record). Disk/file lookups use complete IDs.
  `OvfPackageLoader` moves shell metadata loading off the UI thread.
- Console: `HostedConsoleSession`, `AvaloniaRfbFramebuffer`, `RfbConsoleView`,
  and `XcpNgCenter.Rfb`. Preserve the intentional distinction between guest
  retries and host control-domain consoles (R12).
- Graphs: `Services/Performance/ShellRrdMaintainer.cs`, `RrdModels.cs`,
  `PerformanceGraphBuilder.cs`, and `MainViewModel.AlertsGraphs.cs` (R09).
- Updates: `ShellGitHubUpdateChecker`, `ShellUpdateInstaller` and its
  `.Bootstrap.cs` partial, `MainViewModel.Updates.cs`, and the publishing workflow.
  Installed code fetches fresh release metadata and verifies a copied archive
  before launching replacement code. Elevated Windows preparation uses
  administrator-owned staging and the built-in `Narehood/xenadmin` publisher;
  its elevated metadata HTTPS callback rebuilds certificate trust using the
  Windows machine chain engine. Repository overrides apply only to
  non-elevated preparation/checks. Runtime
  version must be lower than the release version to test an update offer.

## Build and verification

Versions are centralized in `Directory.Packages.props`. Calendar assembly
versions come from `Directory.Build.props`; `BuildRevision` defaults to zero.
Use the supported `development` workflow, not historical Avalonia/Mono branches
or old branding build scripts. See `README.md`, `MODERNIZATION.md`, and
`UI_REWRITE.md` for feature scope and desktop-soak priorities.

With a .NET 8 SDK on PATH, run these commands individually and check each exit
code (R13 describes why combining them carelessly in PowerShell is unsafe):

```powershell
dotnet test XcpNgCenter.Shell.Tests/XcpNgCenter.Shell.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet test XenCenterLib.Tests/XenCenterLib.Tests.csproj -c Release -p:RestoreLockedMode=true
dotnet build XenAdmin.sln -c Release -p:RestoreLockedMode=true
dotnet list XenAdmin.sln package --vulnerable --include-transitive
```

This Windows machine did not have `dotnet` on PATH. A portable .NET **8.0.424**
SDK was downloaded using Microsoft's installer to:

`C:\Users\Michael\AppData\Local\Temp\xenadmin-astra-review-20260907\dotnet\dotnet.exe`

No permanent PATH or system SDK installation was made. This temporary path may
be removed later; check availability before using it.

Initial-review baseline, before the source fixes:

- Shell Release build passed; shell tests: **50 passed**.
- Shared tests: **73 passed on net8.0**, **73 passed on net481**.
- Full solution Release build stopped at `XenAdmin.csproj:75` because
  `aximp.exe` is absent. Install the Windows SDK/VS desktop prerequisites before
  claiming a full build. `SkipRdpAxImp=true` also requires existing interop DLLs;
  they were absent from `XenAdmin/RDP` here.
- NuGet vulnerability query included transitive packages and reported no known
  vulnerable packages. This does not audit the bundled runtime or prove parser
  safety.
- No live pool operations, desktop soak, Linux runtime checks, or actual UAC
  update installation were performed.

Temporary evidence is under the same `xenadmin-astra-review-20260907` directory:

| Directory/file | Contents |
| --- | --- |
| `shell-security-harness/` | Actual shell APIs: JSON credential recovery, mutable cached EXE acceptance, CA-valid pin bypass |
| `shared-security/` | Actual OVF/import APIs: source deletion, path escape, incomplete manifest, disk-ID collision |
| `shell-repro/` | Actual graph merge horizon and synchronous OVA scan measurements |
| `results/` | TRX output (shared frameworks used the same filename; the net8.0 TRX overwrote net481) |
| `package-audit.json` | NuGet vulnerability query output |
| `solution-build.log` | Full solution build and missing-aximp error |
| `machine-tls-probe/`, `machine-tls-probe.log` | Remediation: published checker authenticates GitHub with Windows machine-context TLS; nonexistent release returns expected HTTP 404 |

The harnesses used freshly compiled assemblies and disposable synthetic data.
They are temporary review evidence, not checked-in regression tests. The review
records inputs and outcomes so future agents can recreate meaningful tests even
after temporary files disappear. Do not run file-deletion reproductions against
real appliances or credentials.

## Remediation verification and next work

The [remediation record](reviews/2026-09-07-remediation.md) maps R01–R13 to source
and regression tests. Current-source results are **168 shell tests passed in
Release**, **168 passed in Debug**, **73 shared tests passed on net481**, and
**73 passed on net8.0**: 482 passing executions. Neither shell configuration
has compiler warnings, failures, or skipped tests. `XenModel` Release `net481`,
including `XenOvfApi`, builds with zero
warnings and errors. Self-contained Release `win-x64` and `linux-x64` publishes
also pass with zero warnings. These used CI's unlocked RID restore mode after a
forced locked publish reported missing RID graphs (`NU1004`); tracked lockfiles
were restored afterward and locked shell-test restore passes. Linux execution
remains untested. The earlier baseline remains historical evidence only. CI now
runs each shared framework and each shell configuration in a separate step with
its own TRX filename.

The published Windows executable's four invalid updater modes also exited with
code 1 and no UI when the child environment pointed `DOTNET_STARTUP_HOOKS` at a
missing DLL. Startup hooks are disabled in the runtime configuration. This was a
headless startup/error-path check, not an installation or UAC test.

The refreshed published checker's machine-context HTTPS path also authenticated
GitHub in a read-only probe and received the expected HTTP 404 for the nonexistent
release `1900.1.1.0`. No asset download, elevation, or certificate-store changes
occurred. The probe/log locations are in the evidence table above.

Complete real Windows update testing, including protected installation folders,
UAC cancellation, different-account elevation, staged-file permissions, rollback,
and an unelevated restart. Also exercise the writable portable path on Linux.
Fresh release authentication requires network access during installation.

Keep the shell files at the root of every published archive. Older deployed
shells validate the archive root, so a wrapped payload fails their preparation
with "The update package is missing required shell files" and strands them.
Their existing updater still cannot acquire the new bootstrap safely by changing
only the package it downloads, so prefer a manual install for that first hop.
For custom repositories, administrator/elevated automatic installation is
disabled with release-page guidance before UAC; use a manual installation for
those builds.

Use disposable profiles/appliances for main-password migration and import tests.
Do not open a migrated profile with an older shell that cannot understand the
new document. File replacement tests establish process-crash recovery, not
power-loss durability on every filesystem. Local OVF reparse checks do not
prevent an untrusted process from concurrently replacing the checked directory.

Continue desktop soak for large OVA loading/cancellation, long-range RRD history,
pending ISO selection across inventory events, and repeated guest-console
failures without new inventory events. Install the documented RDP prerequisites
before claiming a complete WinForms build. Profile large pools and real console
activity before broader performance refactoring.
