# Astra / Astro handoff

Last updated: 2026-10-03 (local date). Initial review, remediation, and modernization follow-up.

## Start here

### PR #63 WinForms metadata database owners (2026-10-03)

Implementation: `dd963ddba`. Cursor confirmed the preceding ownership fixes and
found that WinForms recovery logged out successful metadata sessions without
disposing their independent RPC clients. Storage loading, recovery completion
and prechecks now close metadata sessions with client disposal in a `finally`.
Precheck cancellation, errors and VDI changes release the current owner; partial
setup closes a known independent handle through the borrowed caller. Preview
cleanup remains unchanged. See the [follow-up record](reviews/2026-10-03-pr63-review-followup.md)
and [RPC ownership contract](rpc-transport.md).

All 14 focused ownership cases pass, covering successful `get_record`, successful
and failed logout, partial setup and preservation of the borrowed pool client.
The complete Release/Debug shell suites pass 1,055 tests each; shared .NET 10
passes 113 and Framework passes 112, with no failures or skips. All 26 complete
acceptance checks pass, including both solution builds, WinForms/proxy checks,
all six UI modes and fresh Windows package/startup/legal verification. Evidence:
`artifacts/pr63-metadata-acceptance-20261003`; portable lockfiles are unchanged.
Hosted CI and review must also validate the final PR head. The live-pool,
physical desktop/reboot/RDP and installer/signing limits below remain unchanged.

### PR #63 remaining session owners (2026-10-03)

Implementation: `5f6ffd530`. The second Cursor pass confirmed the prior
fixes and found remaining clients abandoned during repeated retries and by
short-lived session owners. Retry cleanup now releases every owned predecessor,
logs out independent elevated handles, preserves borrowed main/caller clients,
and replaces cached cancellation clients when their login handle changes.
Event-next, patch phases, folder caches, action-local and preview management
clients now have explicit cleanup. WinForms prechecks, HA/diagnosis and disk/RDP
helpers also release their clients; host menus own their shared pool until close
or disposal. Patch phases restore their original action session and connection.

See the [follow-up record](reviews/2026-10-03-pr63-review-followup.md) for the
ownership audit and regressions. All 11 focused ownership cases pass, including
two consecutive failures, caller/main preservation, elevated logout and folder
cache cleanup. All 26 full acceptance checks pass: both solution builds, 1,052
shell tests per configuration, 110 shared .NET 10 and 109 shared Framework tests,
WinForms/proxy checks, all six UI modes and fresh Windows package/startup/legal
verification. Evidence: `artifacts/pr63-ownership-complete-20261003`; portable
lockfiles are unchanged. Final-head hosted checks and bot review remain required.
The live-pool, physical desktop/reboot/RDP and installer/signing limits below
remain unchanged.

### PR #63 bot review fixes (2026-10-03)

Implementation: `18b4052b1`, in [PR #63](https://github.com/Narehood/xenadmin/pull/63).
CodeRabbit and both completed Cursor runs raised 13 threads covering 12 distinct
issues. All were verified against the code and corrected. See the
[review dispositions and validation](reviews/2026-10-03-pr63-review-followup.md).

Actions preserve borrowed RBAC/caller sessions and explicitly release owned
retry/elevation clients. Successful elevation clears the startup token before
returning; managed RPC clients no longer have a finalizer. Modern RPC pools cap
connections at 20, follow the application's explicit revocation policy, preserve
heartbeat retry statuses and reject empty successful bodies as protocol errors.
Console deadline cancellation between tunnel completion and RFB startup now
clears connecting state, while stale generations leave status untouched.
Capture argument/output errors and shutdown write failures report clear errors
with nonzero exit codes. Notice generation follows configured restore caches
and immutable upstream sources; verification compares exact package/runtime
identities instead of accepting version prefixes.

All 26 local acceptance checks pass on the reviewed follow-up source: full
Release/Debug solution builds, 1,047 shell tests per configuration, 105 shared
.NET 10 and 104 shared Framework tests, all WinForms resources and lifecycle
checks, 12 proxy checks, all six UI modes, 13 notice fixtures, seven report
fixtures and fresh Windows packaging/startup/legal verification. Evidence is in
`artifacts/pr63-review-acceptance-20261003`; portable lockfiles are unchanged.
Trusted local RDP interop was reused; final-head hosted Windows/Linux and bot
reviews must also pass. TLS regressions observe the actual loopback chain policy;
an OS-trusted certificate with an unreachable CRL was not provisioned.

No disposable pool is available, so live performance and pool acceptance remain
pending. Physical desktop/reboot/RDP guest and MSI/updater/UAC/rollback acceptance
also remain pending. Installer modernization and signing stay deferred.

### Async polling, WinForms startup and capture workflow (2026-10-02)

The user selected remaining items 1, 2 and 4, deferred installer work and
confirmed that no disposable pool is available. Implementation:

- `71cca31ed`: cancellable task-based heartbeat and graph polling, completion
  handles, retained time conversion/wire shape, local duplicate cleanup and
  cancellable elevation login.
- `e8e944992`: shared startup guard in both clients; WinForms login/tunnel/RFB
  deadlines, cancellation of retries and port discovery, stale-handoff guards,
  immediate failed-stream cleanup and owned elevated-session logout.
- `f9ad9b530`: bounded opt-in performance capture, offline statistics/coverage,
  overload/completeness checks and synthetic Linux desktop CLI capture.
- `528082be5`: serialize capture admission with queue completion so shutdown
  cannot publish a footer before dropped-sample accounting. Footers include
  observed counts; the reader rejects inconsistent loss totals. Concurrent
  producer/shutdown regression, all eight capture cases and seven report
  fixtures pass. Full shell suites pass again with 1,021 cases in each
  configuration; both shared frameworks pass all 96 cases. Follow-up logs are
  `artifacts/capture-shutdown-*` and `artifacts/performance-capture-shutdown-tests.log`.

See the [implementation/review record](reviews/2026-10-02-async-winforms-performance.md),
[capture instructions](performance-capture.md) and updated [RPC contract](rpc-transport.md).
Both solution configurations and full suites pass: 1,020 shell cases per
configuration, 96 shared cases per framework, 349 WinForms lifecycle/designer
and 23 archive checks per configuration, all resources and 12 proxy cases.
All 26 local acceptance gates pass at `f9ad9b530`: both complete solution builds,
both suites, WinForms/proxy checks, all six UI modes, notice fixtures and fresh
Windows publish/package execution. Evidence is in
`artifacts/async-winforms-capture-final-20261002`; the archive SHA-256 is
`d2d0682063e001637cbc4f17b6b15c4ac0a8d9296164bc7dcd8c3a8b26dc3f96`.
Portable lockfiles are unchanged. Trusted local RDP interop was reused; hosted
Windows/Linux and CodeQL must pass on the final PR head. The capture deadline
test asserts parsed frame dimensions, avoiding accidental matches in timestamps.

Main connection/event and administrative action workers remain synchronous;
RRD HTTP/XML parsing remains synchronous during active phases. Live timings,
physical desktop/reboot/RDP and installation/update acceptance remain pending.
Installer modernization and signing are deferred at the user's direction.

### Legal notices, console startup, RPC and performance (2026-10-02)

The user selected console reliability, shared transport modernization and
performance instrumentation, and requested restoration of the legal notices.
Release signing is deferred at the user's direction; no signing account is
available. The branch starts from merged #62 (`84cb0b57c`). Implementation:

- `6896346c0`: complete offline license/third-party text in both clients and
  ordinary build/publish artifacts; package legal gates and six rejected fixtures.
- `0da95103b`: reusable, isolated .NET 10 RPC pools, cancellable async entry point,
  retained synchronous/generated and Framework contracts, TOFU/proxy adaptation,
  and owned transport cleanup.
- `0e1381a9d`: bounded/cancellable console startup with an idle-safe handshake
  deadline; opt-in inventory/detail/console performance events and expanded-tree
  measurements.

See the [implementation and validation record](reviews/2026-10-02-legal-console-rpc-performance.md),
[legal maintenance](legal-notices.md), [RPC contract](rpc-transport.md),
[console limits](reboot-hang-investigation.md) and
[performance evidence](performance-baseline.md). The original `LICENSE` remains
unchanged. The notice bundle covers 48 pinned application packages, legacy
source/assets, Outfit and the bundled .NET runtime, including log4net's upstream
`NOTICE`. Both native legal controls retain complete selectable text offline.

Local acceptance passes all 25 automated checks: Release/Debug full solution
builds, 1,005 shell cases per configuration, 88 shared cases per framework,
339 lifecycle/designer and 23 archive cases per WinForms configuration, all
32,138 resources in 289 sets, proxy checks, six UI modes, Windows package legal
validation/startup and unchanged portable locks during validation. The added
shared test project reference intentionally updates only its dependency graph;
central package versions are unchanged. After pinning the DiscUtils license
source to its package commit and tightening the viewport assertion, focused
checks pass again: both full builds, both offline-text tests, both native legal
dialog probes, 24 About checks at three render scales, six rejected notice
fixtures and a freshly published/verified Windows archive.

Trusted local RDP interop was reused. Hosted Windows/Linux and CodeQL checks
must pass on the PR head. Physical desktops, actual MSI installation, Visual
Studio/RDP interop generation, updater/UAC/rollback/restart, RDP guest login and
live-pool/login/proxy/event/reconnect/import/export/migration acceptance remain
pending. Startup hardening does not establish the cause or resolution of the
reported Debian reboot hang. Generated sync RPC calls still need worker threads;
traces and expanded-tree numbers do not establish live-pool performance.

### PR #62 Cursor review fixes (2026-10-01)

Implementation: `2523d9f84`, in [PR #62](https://github.com/Narehood/xenadmin/pull/62).
The later Cursor review identified two valid issues in the consolidated head.
Their [dispositions and regression evidence](reviews/2026-10-01-pr62-review-followup.md)
supersede the earlier consolidation snapshot that had no code findings.

A failed inventory rebuild is now caught/logged per connection, while the rest
of the batch proceeds. Failed work stays pending until another notification
schedules a turn; newer notifications win, and persistent failures do not
create automatic retry loops. Mapped IPv6 input now requires canonical spelling
before mapping. The RDP dialog displays the exact authority used by the client,
updates normalized address/port fields and stays open for another confirmation.
Editing fields refreshes the preview and clears the previous review notice.

The initial regressions reproduced ten failures; all 47 focused cases now pass.
Full local acceptance passes all 24 automated checks: locked restore, both full
solution builds, 977 shell tests per configuration, 73 shared tests per framework,
333 lifecycle/designer and 23 archive checks per WinForms configuration, all
32,138 resources in 289 sets, proxy checks, six UI modes, Windows package
validation/startup and unchanged portable lockfiles. The actual RDP dialog now
passes 12 checks at three render scales. Trusted local RDP interop was reused.
Check current-head Windows/Linux and CodeQL results before merge. CodeRabbit's
file/capacity skip remains a review availability limit; Cursor review sign-off
remains the reviewer's decision. Physical desktop, Visual Studio, updater/UAC,
actual RDP guest login and live-pool acceptance remain pending.

### Consolidated modernization final review (2026-10-01)

The user requested one final-review PR and asked for comment resolution before
consolidation. All comments, submitted reviews and inline threads on #58?#61
were checked first: each PR has only a CodeRabbit notice that draft review was
skipped; there are no code findings or unresolved threads. See the
[comment dispositions and consolidation record](reviews/2026-10-01-pr58-61-consolidation.md).
A bot success status does not establish that those draft changes were reviewed.

[PR #62](https://github.com/Narehood/xenadmin/pull/62), branch
`modernization/final-review`, contains all six commits from the stack and targets
`development` directly. Rebase onto current integration head
`1c1ab84b3facad3de087c971a8d1be68cb214e69` is already up to date; there are no
implementation conflicts or omissions. Original branches/commits are preserved.
The combined PR replaces the old per-layer merge sequence below and is ready
for final review. CodeRabbit attempted review but skipped it because 184 files
exceed its 100-file limit and review capacity is unavailable; it produced no
code findings. Its success status is not a completed review or approval.

Fresh combined local acceptance passes all 24 checks: both full solution
configurations, both required test suites (963 shell cases per configuration,
73 shared cases per framework), 333 lifecycle/designer and 23 archive checks
per configuration, all resources, proxy and six UI modes, package validation
and startup. Portable lockfiles are unchanged; trusted local RDP interop was
reused. Source comparison confirms only handoff/review documentation changed
in consolidation; a focused source/coverage review found no new correction.
Check hosted Windows/Linux and CodeQL results on the current #62 head before
merge. Original PRs are superseded by #62 and their branches remain available.
No integration merge, release or live-pool operation is part of consolidation.

### IE plugin tabs archived outside builds (2026-10-01)

Implementation: `56cfa2be6`, submitted as
[original PR #61](https://github.com/Narehood/xenadmin/pull/61).
Now included in the consolidated final-review branch described above.

The user chose to remove IE browser plugin tabs from the shipped clients while
preserving their source. The browser host, tab/scripting/authentication bridge,
credential dialog and resources are moved byte for byte to
[the archive](../legacy/disabled-features/ie-plugin-tabs/README.md), outside every
project root. Pool plugin-secret helpers are extracted there too; the archive
also records the removed MainWindow, loader, manager and Pool integration.
No build/package contains this implementation and no registry flag enables it.
This supersedes the earlier plan to retain the WinForms IE path.

The separate menu/command extension system keeps its existing opt-in policy.
Mixed manifests skip archived tabs and preserve valid menu commands; tab-only
plugins remain disabled with a visible archive error. Normal VM, host and
driver-domain console tabs no longer have plugin replacements. Existing
server-side secrets are untouched; no live pool/profile was used for validation.
Restoring browser integration requires deliberate source integration and a new
design/review for an actual requirement; archived code is reference only.

The native archive probe rejects the pre-archive binary and passes **23 checks
per configuration** on the new Release/Debug builds: compiled type/resource and
integration exclusions, absent pool-secret helpers, tab-only errors, retained
mixed/menu/grouped commands and malformed-feature rejection. CI and platform
acceptance run this mode. MSBuild item inspection confirms archive exclusion.
The full local acceptance pass succeeds: locked restore, both solution builds,
**963 shell tests per configuration**, **73 shared tests per framework**,
333 lifecycle/designer checks per configuration, **32,138 resources in 289
active sets**, proxy checks, all six UI modes, Windows package validation and
startup. Seven acceptance failure/coverage fixtures pass; portable lockfiles
and trusted reused local RDP interop remain unchanged. Physical desktop,
Visual Studio and live-pool release acceptance remain pending; check hosted
Windows/Linux results on the current PR head before merge.

### External Remote Desktop and remaining parity strategy (2026-10-01)

Implementation: `66e23e5f9`, originally stacked after #59 in PR #60.
Now included in the consolidated final-review branch described above.

The VM Console toolbar now offers **Remote Desktop…** for a connected running
guest. A review dialog suggests validated guest-reported addresses and accepts
a manual IPv4/IPv6 address and port. The selected VM, cache identity, connection
and power state are checked again after review. Windows launches system
`mstsc.exe` with a credential prompt; Linux launches system Remmina using a
credential-free RDP URI. Arguments are separate, no shell is involved, and no
hypervisor credentials or certificate-bypass flags are passed.

Twenty-seven endpoint/identity/draft checks and seven actual RDP dialog checks
pass. Windows CI and local acceptance run the new dialog mode. Missing clients
produce visible guidance. Actual Windows/Linux guest login, IPv6 reachability
and Linux Remmina/plugin availability remain manual checks. The shell connects
directly to the guest; embedded RDP and hypervisor tunneling are not implemented.
See [Remote Desktop](remote-desktop.md).

The integrated local acceptance pass succeeds: locked restore, both solution
builds, **963 shell tests per configuration**, **73 shared tests per framework**,
333 lifecycle/designer checks per WinForms configuration, full resources,
proxy/editor probes and Windows self-contained package validation/smoke.
After separating the new probe modes, the inventory mode passes 189 checks and
the RDP mode passes seven; acceptance failure/coverage fixtures were rerun.
Portable lockfiles and trusted reused RDP interop are unchanged. Hosted Linux
and Windows results must be checked on the PR heads before merge.

The [remaining parity plan](modernization-remaining-plan.md) defines the plugin/
external-tool strategy and staged DR scope. The subsequent IE archive decision
supersedes the original plugin-tab retention plan; arbitrary legacy tool
templates retain their supported WinForms path. Live halted recovery,
source fencing and storage isolation must be proven before adding appliance,
snapshot, special-hardware or running-guest recovery. No production pool, user
profile, external RDP client or release was changed during validation.

### Inventory refresh responsiveness (2026-10-01)

Implementation: `7819fcd1c`, submitted as
[original PR #59](https://github.com/Narehood/xenadmin/pull/59).
Now included in the consolidated final-review branch described above.

Inventory notifications now coalesce by connection into one UI refresh per
dispatcher turn. Notifications arriving during a refresh schedule another turn.
Queued callbacks retain their originating connection; disposed, removed and
replaced server connections cannot rebuild stale inventory.

The builder resolves VM/SR placement once per object. The tree updater retains
existing nodes and containers, updates changed metadata, moves/adds/removes
children without a collection Reset, and detaches migrated objects before
attaching them to another parent. The main view model still refreshes details
when the selected node instance stays the same. Selected guests keep their
identity; their destination path expands after a move.

Twenty focused inventory/scheduler/updater checks pass. The production TreeView
probe measures both replacement and reuse with the same current builder: a
64-host/5,000-VM refresh falls from **143.9 ms to 22.1 ms** median in the final
synthetic run. It verifies burst coalescing, changed metadata, selection,
collapsed hosts and migration. The [performance record](performance-baseline.md)
retains all samples and limitations. Windows CI and platform acceptance now run
the inventory layout probe. Live event streams, detail-pane costs, fully expanded
large trees and physical desktop responsiveness remain acceptance work.

### WinForms designer audit completion (2026-10-01)

Implementation: `c26bd791e`, submitted as
[original PR #58](https://github.com/Narehood/xenadmin/pull/58).
Now included in the consolidated final-review branch described above.

All **297 remaining WFO1000 declarations** have been audited against their
constructors, setters, callers and generated designer assignments. The project
suppression is removed. Runtime models, actions, connections, wizard results and
credentials are explicitly hidden; existing graph/control references and
resource-backed settings remain visible. Sixteen verified defaults and two
serialization/reset hook pairs complete the inventory. Generated designer files
and resources are unchanged. See the [audit record](reviews/2026-10-01-designer-completion.md).

`MemorySpinner.Increment` now returns its configured byte input so designer
replay cannot feed a displayed MB step back into the byte-based setter. Explicit
legacy GB assignments remain serializable. Reset restores the original numeric
step and clears the configured input. `DataGridViewEx.Enabled` uses framework
local-state omission and its custom setter on Reset, preserving inherited
disabled-parent behavior and the enabled header style.

The lifecycle/designer probe grows from 239 to **333 passing checks** on Release
and Debug. The spinner regression first reproduced 12 failures against the old
binary. New checks cover byte replay, GB thresholds, reset, inherited Enabled,
verified defaults, graph references/resources and hidden credential metadata.
Both solution builds, full resource probes (**32,240 resources in 290 sets**) and
locked restore pass; there are no WFO1000 diagnostics. Local builds reuse the
unchanged trusted RDP interop via `SkipRdpAxImp=true`; existing ACL warnings
remain. Evidence is under ignored `artifacts/modernization-20261001/`.

Actual Visual Studio designer save/reopen, active shutdown, physical installation
and live-pool acceptance remain pending. This closes the metadata audit, not
those manual acceptance gates. Earlier sections below describe dated snapshots.

### PR #56 / #57 review follow-up (2026-09-29)

Reviewed the comments against `3a27e4087` (#56) and `47859834d` (#57).
Cursor approved both; neither PR has inline review threads or actionable code
findings. CodeRabbit completed #56 with only its advisory XML-doc coverage
warning; the repository does not mandate that threshold. Its #57 review was
skipped because the stacked base is not the default branch, so its successful
status does not represent a completed review. Hosted Windows/Linux validation
passes on both reviewed heads; #56 also has passing CodeQL. The
[review record](reviews/2026-09-29-pr56-57-review.md) records the dispositions,
regression evidence and remaining manual checks. This follow-up changes
documentation only. The merge order remains #56, then retarget #57 to
`development`.

### Combo-box and grid-editor designer maintenance (2026-09-29)

Implementation: `1c061292e`, submitted as
[PR #57](https://github.com/Narehood/xenadmin/pull/57), stacked on PR #56.
Merge PR #56 first, then retarget PR #57 to `development` before merging.

This batch follows the labels/time implementation `6037faaf6` in
[PR #56](https://github.com/Narehood/xenadmin/pull/56). `EnableableComboBox.Enabled`
now delegates designer omission to the framework's local enabled-state
descriptor, rather than persisting a disabled parent's effective value. Its
protected Reset hook uses the custom setter to restore the enabled background;
both hooks remain available to the derived grid editor. Enabled remains an
editable, localizable setting.

The grid editor explicitly hides its formatted value, row index, owning grid,
dirty flag, repositioning policy and cursor from designer serialization and
property browsing. The runtime `IDataGridViewEditingControl` contract is
unchanged. The actual production grid cell/editor still initializes its owner
and row, signals dirty state after a selection and commits the selected item.
Generated designer code and resources are unchanged.

The analyzer inventory falls from **302 to 297** WFO1000 diagnostics, with five
removed and none added. The existing compatibility probe adds 42 checks for
local/inherited Enabled behavior, reset/replay, hidden runtime metadata and the
real grid edit/commit path, including the derived networking combo box. The
initial 230-check probe reproduced 16 failures before correction; the final
**239 checks** pass against Release and Debug. Locked restore, both
solution builds, **923 shell tests per configuration**, **73 shared tests per
framework**, and **32,240 resources in 290 sets** pass. Existing ACL analyzer
warnings remain. Local builds reuse unchanged trusted RDP interop with
`SkipRdpAxImp=true`; portable lockfiles are unchanged. Evidence is under ignored
`artifacts/winforms-designer-editors/`.

Memory-spinner Increment remains unaudited: generated forms already assign it,
and its getter exposes display units while its setter interprets byte increments
or derives a GB step. It cannot simply be hidden as runtime-only state. Actual
Visual Studio save/reopen, physical desktop/installation and live-pool acceptance,
and the reported reboot investigation remain pending. No release or live pool
operation was performed.

### Designer labels and snapshot time maintenance (2026-09-29)

Implementation: `6037faaf6`, submitted as
[PR #56](https://github.com/Narehood/xenadmin/pull/56).

This bounded follow-up starts from `development` at `d346ca5e5`, which merged
PR #55. Ten explicit defaults cover the eight `SectionHeaderLabel` properties,
`DecentGroupBox.Text`, and `DateTimeMinutes15.Value`. Defaults match the existing
constructors, including null captions and the snapshot picker's 1970 midnight
value. An intentionally empty group caption now remains serializable instead
of being omitted by the inherited empty-string default. Localizable metadata,
focus-control references, mnemonic escaping and quarter-hour correction remain
intact. Generated designer code and resource files are unchanged.

An unsuppressed analyzer audit reduces WFO1000 diagnostics from 312 to **302**,
with no new diagnostics. The existing `--lifecycle-designer` probe grows from
110 to **197 checks**; 30 assertions fail on the pre-change client, and all checks
pass on both Release and Debug afterward. Coverage includes omission/edit/reset/
replay, null versus empty text, existing header resources, focus references,
ampersand handling and snapshot-time defaults under three cultures.

Locked restore and both solution configurations pass. Shell tests pass **923
cases per configuration**; shared tests pass **73 cases on each framework**.
Both WinForms builds load all **32,240 resources across 290 sets**. Local builds
reuse unchanged trusted RDP interop with `SkipRdpAxImp=true`; the existing ACL
analyzer warnings remain. Evidence is under ignored
`artifacts/winforms-designer-labels/`; portable lockfiles are unchanged.

The audit and remaining limitations are recorded in the
[migration maintenance record](dotnet10-migration.md#winforms-resources-and-designer-metadata).
Actual Visual Studio designer round trips, physical desktop/installation checks,
live-pool acceptance and the reported reboot investigation remain pending. No
release or live pool operation was performed.

### PR #55 review follow-up (2026-09-28)

The three Cursor findings on `dda0e24fd` are addressed in `288667ca3`. Dialog cleanup
now reaches `base.OnFormClosed` through `finally`, including when an action or HA
teardown fails. HA detaches both listeners before stopping its worker. Registry
cleanup still precedes observers; owner focus now follows base close processing.
The wizard and dialog share a focus guard for application exit, owner closure,
Windows shutdown, and disposed/disposing owners. Reentrant owner disposal is
handled without hiding other exceptions.

The lifecycle probe now passes 110 checks in Release and Debug. Fault injection
reproduced three failing cleanup assertions before the fix. The main-window
probe seeds an isolated poison history entry: removing the ApplicationExitCall
guard was mutation-tested and now fails at the task scan before settings access.
Production source/binaries were restored afterward. No production test flag,
real action, profile load or live RPC was introduced. Both solution builds and
required test suites pass; the detailed evidence, review dispositions and
remaining manual limits are in the [PR #55 review](reviews/2026-09-28-pr55-review.md).
Hosted Windows/Linux CI and all CodeQL analyses passed on `288667ca3`.

### WinForms lifecycle and designer maintenance (2026-09-28)

Initial implementation: `6ac156e32`, with picker inheritance follow-up in
[PR #55](https://github.com/Narehood/xenadmin/pull/55).

PR #54 merged as `ab95c9239`; the resulting stable release is
[2026.9.27.209](https://github.com/Narehood/xenadmin/releases/tag/v2026.9.27.209).
With no disposable acceptance environments available, the next implemented batch
maintains the supported WinForms client. All eight obsolete closing overrides
now use current Form events. Cleanup waits for an accepted close, the folder
dialog calls base cleanup to release its owner registration, and modern
`FormClosed` observers see completed base cleanup. Main-window final
`Application.Exit()` retains its bypass of the cancellation wait, including its
timeout and settings-save failure paths; listener cancellation is respected.

Seventeen explicit defaults cover the custom tree, both picker overrides, panel
borders and three click-through strips. Eleven diagnostics are eliminated;
312 WFO1000 diagnostics still require individual review and remain suppressed.
Generated designer code and resource files are unchanged. See the
[migration maintenance record](dotnet10-migration.md#winforms-resources-and-designer-metadata).

The `--lifecycle-designer` compatibility probe runs in Windows CI and local
acceptance for Release and Debug. At `dda0e24fd`, it passed 93 checks per WinForms
configuration; the review follow-up above expands this to 110. Coverage includes
real control descriptors, HWND close/cancel
paths, owner/connection registry cleanup and guarded main-window exit paths.
The initial 75-check probe reproduced 29 failures on the old client, including
listener detachment on cancelled password close and the folder registry leak.
Six further failures caught pool/host picker overrides that need their own
defaults to preserve edited values equal to the base tree's defaults.
Both solution configurations build; all 923 shell tests pass per configuration
and all 73 shared tests pass on each framework. Both WinForms builds load all
32,240 resources across 290 sets. Local builds reuse trusted unchanged RDP interop
with `SkipRdpAxImp=true`; hosted Windows builds generate it normally. Evidence is
under ignored `artifacts/winforms-*` paths. Existing ACL test warnings remain.

This probe does not load user profiles or run live server actions. Main-window
guards omit the constructor; certificate/subject dialogs use idle action fields.
Full shutdown with running tasks, modal owner focus, Visual Studio designer round
trips, physical desktop/UAC and live AD/DR acceptance remain pending. The reported
Debian reboot hang remains undiagnosed. No pool operation or new release was run
for this maintenance batch. Continue the remaining designer audit in bounded
batches; obtain disposable infrastructure before extending live recovery scope.

### Review and merge follow-up (2026-09-27)

Cursor corrections are committed in `5db40713c` (directory access) and
`b115e790b` (recovery), integrated into the acceptance layer. Combined validation
passes 923 shell tests and 73 shared tests on each framework. The actual AD/DR
probe now passes 98 checks through production button bindings, verifies cleared
credential textboxes, and asserts default/minimum client and layout dimensions.
Probe-only finite maxima avoid the Windows small-desktop tracking limit without
changing desktop resolution. A pre-fix probe reproduced the missing credential
re-review guard. Seven isolated acceptance-runner failure/coverage fixtures pass
on PowerShell 5.1 and now run in native Windows/Linux CI. The runner rejects
missing/empty/failed UI logs and records Linux editor probes explicitly as skipped.
Full local Windows acceptance and hosted Windows sizing evidence now pass. The
complete runner also passed native Linux acceptance at `e6f6c8bd1`; its manifest
records Windows-only skips explicitly. Windows executes all applicable cases.
A hosted test-listener cancellation race is fixed in `39987f6db`, with ten repeated
affected-suite runs passing. See the
[review follow-up](reviews/2026-09-27-pr51-54-review.md) for exact results, hashes,
review dispositions and merge decisions.

PR #51 merged into `development` as `7134737d9` after Cursor approved it and
CodeRabbit completed review. CodeRabbit's sole finding incorrectly treated the
PR #50 baseline as padding-fix attribution; repository/GitHub evidence confirmed
the baseline, and CodeRabbit withdrew the finding. The user subsequently reported
completing their own Grok reviews and explicitly waived the pending CodeRabbit
reviews. All eighteen Cursor threads are addressed. PR #52 merged as `541e1e792`
and PR #53 as `33873e93f`, after green current-head CI and retargeting in order.
PR #54 is the final acceptance/documentation layer and now targets `development`.

Independent review reproduced two concurrent-edit defects. Commit `0ca061020`
keeps AD's reviewed role baseline immutable across cache events during final
server reads; its new race test failed before the fix and all 54 AD tests pass.
Commit `a6e66d8c5` checks the full DR cleanup receipt before every deletion and
advances expected state only for its own confirmed removals; nine concurrency
cases failed before the fix and all 60 DR tests pass. Separate read/write calls
still cannot provide atomic exclusion of another administrator. Guides document
that remaining limit. The Cursor corrections and regression evidence are below.

### Reviewed recovery and directory corrections (2026-09-27)

Cursor findings now have regression coverage for legitimate directional XAPI
device-model normalization, dictionary/set ordering, stable replica/network
approval across multiple recovered VMs, and refusal of automatic-power-on VMs
before import. Confirmation lists fresh per-VM storage and network names/UUIDs
and network isolation; the report identifies observed imports and all rehearsal
records remaining after cleanup failure. The shell suppresses the inner shared
recovery History entry while WinForms retains its existing default behavior.

All 94 focused DR tests pass. Before correction, the first 14 review regressions
and the next 11 cases failed; three wire-order cases also reproduced the old hash
failure. The prior cleanup concurrency correction is `a6e66d8c5`. Failed identity,
configuration or disk validation still forbids automatic NIC changes or cleanup:
the report directs manual inspection/isolation of importer-chosen networks.
No live storage/VM recovery was attempted; separate RPC validation and mutation
still cannot exclude concurrent administrative changes.


Pre-merge Cursor review corrections now bind the nonsecret directory account and
leave-machine-account cleanup choice to review; passwords remain excluded from
drafts and hashes. Typed outcomes distinguish pre-write refusal from a possibly
partial mutation. Shared join/leave failures preserve allowlisted API codes,
locally resolved host names, RBAC formatting, cancellation, and the WinForms
credential-retry type without retaining provider details or inner exceptions.
Group access changes revoke direct sessions and discovered transitive members;
enumeration, membership, or logout failure prevents a success report.

The preparatory-leave regression failed before correction. All 105 AD tests and
829 shell tests pass, plus 73 shared tests on each of `net481` and `net10.0`.
The shared `XenModel` net481 build passes without warnings. The earlier mutable
role-baseline fix is commit `0ca061020`. Live domain recovery remains pending;
session enumeration cannot exclude a concurrent login or directory membership
change, so the guide requires independent verification after the operation.


### Directory access, recovery, and console follow-up (2026-09-27)

PR #50 was merged to `development` as `123679abd`. Its earlier "unmerged"
references below describe the September 26 snapshot. This follow-up starts from
that commit; the [execution plan](modernization-execution-plan.md) records the
three implementation agents and integration/acceptance coordination requested
by the user.

The shell adds [AD and RBAC management](ad-rbac-management.md) through the shared
join/leave and subject/role actions. Domain changes use local root; an authorized
directory administrator can manage other subjects and groups with fresh
permission checks. Reviews pin pool/host/subject/role identities and the resolved
directory SID. The worker rechecks state before mutation and again between
subject creation and role assignment. Credentials are cleared from editors and
actions; shared join/leave errors discard credential-bearing server text before
logging. The guide explains root recovery and intermediate partial outcomes.

[Disaster recovery](disaster-recovery.md) adds metadata discovery and inspection,
reviewed storage/network assignments, halted standalone VM recovery and halted
metadata rehearsal. Source storage UUID and disk identity are checked against
existing attached replicas. Unsupported suspension, snapshots, appliances and
special devices require other recovery workflows. Cleanup applies only to
reviewed unchanged rehearsal VM records and preserves disks; any import-created
networks require separate inspection. No guest is automatically started. Separate
API calls cannot guarantee isolation from concurrent administrators.

The [reboot investigation](reboot-hang-investigation.md) records a confirmed
fragmented-padding defect in the shell's RFB reader, with six failing regressions
before the fix and eleven passing afterward. The actual reported incident is
still unresolved: from Windows 11, the user ran `shutdown -r now` over SSH on a
Debian 12 VM; SSH disconnected and the guest became unresponsive until a forced
shutdown. The client build, last console message and whether the console was
open are unknown. The parsing fix is not claimed to explain that incident.

`Invoke-PlatformAcceptance.ps1` now runs local native builds, both test suites,
actual editor probes and package smoke checks, recording command exits, TRX,
source state, package digest and preserved lockfile hashes. Windows CI also runs
the actual AD/DR windows with synthetic data. The user reconfirmed that no
disposable environments are available. Physical desktop, real UAC/update
installation, live AD/DR/networking/HA and the reported reboot diagnosis remain
pending in [platform acceptance](platform-acceptance.md); WinForms remains the
supported production client.

Initial local integrated acceptance passed: Release/Debug solution builds; **828 shell
tests in each configuration**; **73 shared tests on each of net481/net10.0**;
32,240 WinForms resources in 290 sets in each configuration; all 12 proxy cases;
and **53 actual AD/DR window checks**, alongside existing network/settings probes.
The 115 new tests comprise 11 RFB, 53 AD and 51 DR cases. The Windows package
passes all four helper/runtime smoke modes using bundled .NET 10.0.12, and
portable lockfiles are unchanged. Existing ACL analyzer warnings remain. Local
WinForms builds reused unchanged trusted RDP interop with `SkipRdpAxImp=true`;
hosted Windows CI must generate it normally. Evidence, package hash and the
remaining manual gates are in the [acceptance record](reviews/2026-09-27-modernization-acceptance.md).
The older .NET 8 project/build sections remain historical.

Review stack, in merge order: [#51 console](https://github.com/Narehood/xenadmin/pull/51)
(`a296d4feb`), [#52 directory access](https://github.com/Narehood/xenadmin/pull/52)
(`bb2fcb431`), [#53 recovery](https://github.com/Narehood/xenadmin/pull/53)
(`54e174aa8`), and [#54 acceptance](https://github.com/Narehood/xenadmin/pull/54)
(`221bd8525`, followed by documentation references). These were initially published as draft PRs;
each layer has native Windows/Linux checks. See the acceptance record for links
to current hosted results and retarget remaining layers to `development` as
their bases merge. No release or live pool operation was performed.

### Beta release review follow-up (2026-09-26)

The completed review of beta commit `98ee3d166` found no new application defect.
Its publishing-authority suggestion was checked against live, read-only GitHub
settings: `development` is unprotected, no repository rulesets or environments
exist, and beta has not been created. The collaborator API reports one admin;
default workflow tokens are read-only and cannot approve PRs. The
[beta guide](beta-updates.md#publishing-authority) records this dated evidence and
the continuing trust in repository writers for both release channels.

The release workflow now gives preparation, compilation and package-smoke jobs
read-only tokens, reserving `contents: write` for its two publishing jobs. This
narrows unnecessary access without changing release behavior or repository rules.
No new approval flow, release or branch was created. Review dispositions are in
the [review log](reviews/2026-09-25-pr50-review.md#beta-channel-review-2026-09-26).
The effective permissions for all five jobs were verified with steps/triggers
unchanged. Release reruns passed all 713 shell tests and 73 shared tests on each
framework using unchanged binaries; hosted results are in the PR. All five prior
inline threads remain resolved. The development-build VM reboot
hang reported separately is still undiagnosed, pending guest/client details and
whether SSH or another console remains responsive; this change does not fix it.

Hosted Linux validation of `dee6d9022` then exposed an overly strict pre-existing
CopyRect allocation test: 7,976 bytes across ten copies failed an exact-zero
assertion. Its guard now allows 64 KiB total, below 1% of a single 8.3 MB rectangle
buffer, preserving detection of the intended regression with room for small
runtime/profiler overhead. Production framebuffer code and pixel tests are
unchanged; the allocation source was not instrumented. Final results are in the PR.
After correcting the assertion, local Release and Debug each pass 713 shell
tests. The recorded performance baseline still shows the old rectangle-buffer
implementation exceeding this test's budget in a single copy.

### Manual beta update channel (2026-09-26)

PR #50 adds **Settings → About → Receive beta updates** to the Avalonia shell.
Regular releases remain the default. The persisted opt-in includes newer GitHub
prereleases and regular releases, with beta labels and independent dismissal
history. Switching channels clears prior offers and prepared-install state;
it never downgrades the installed version. Checks, downloads and installation
confirmation prevent concurrent channel changes. Installer metadata retains the
release kind, and the installed bootstrap carries explicit beta authorization
across UAC while preserving fresh publisher/digest verification.

The [beta guide](beta-updates.md) describes the manual publishing and test flow.
Test Builds now covers `beta`; publication remains `workflow_dispatch` only.
Publish Shell Release requires `beta` plus prerelease, or `development` plus a
regular release. A shared preparation job validates unique increasing numeric
tags and passes one UTC version to both platform packages. Merge the workflow
support to the default branch before starting beta releases. No release was
published, no beta branch was created, and PR #50 remains unmerged.

Local validation: Release and Debug each pass 713 shell tests, including 29 new
channel cases covering legacy/invalid settings, persistence, per-channel
dismissal, paginated release selection, beta notes, regular promotion, downgrade
prevention, offline errors, cache-kind mismatch, fresh publisher authentication,
elevation arguments and busy-state guards. All 73 shared tests pass on each of
`net481`/`net10.0`. The real Settings window passes 12 binding/layout checks at
760×650 and 440×400 (`AdvancedNetworking.UiProbe --beta-settings`). Thirteen
isolated workflow-validation scenarios pass. A Windows package built through
`Publish-Shell.ps1 -ReleaseVersion 2026.9.25.42` has that exact assembly version
and passes the packaged helper/runtime smoke checks; lockfiles are unchanged.
Hosted results are recorded in the PR. Live beta publication, end-to-end update
installation, UAC and subsequent return to a newer regular release remain manual
acceptance on disposable machines, as requested by the user.

### Proxy probe cancellation follow-up (2026-09-26)

Hosted validation of documentation commit `b43769825` exposed an intermittent
Windows shutdown failure in the existing loopback proxy probe: a cancelled
pending accept raised `SocketException`/`OperationAborted` (995). The probe now
accepts that specific error only when its cancellation token was requested;
unexpected socket errors still fail. Application proxy behavior is unchanged.
Three regressions compile the real probe source into the shell test suite and
exercise 128 pending-accept cancellation/close cycles plus an unexpected listener
abort. The 12 actual proxy authentication cases also pass locally. Release and
Debug each pass 684 shell tests; the shared suites pass 73 tests on each of
`net481`/`net10.0`. Hosted results are recorded in the PR; the HA/networking
recovery limitations below remain unchanged.

### HA review follow-up (2026-09-26)

Rechecked PR #50's completed review against HA commit `9815110b3`. There are no
new inline findings; the five earlier threads remain resolved. The latest
summary's request for recovery guidance at each intermediate HA state is valid.
The [HA guide](ha-management.md#reconcile-intermediate-states) now maps the shared
actions' actual write order to possible partial outcomes and required checks,
including policy removal before additions, changed tolerance, unconfirmed
database synchronization, and interrupted enable/disable tasks. It describes
capturing original/intended values and explicitly distinguishes matching UI
values from confirmed member synchronization.

This is a documentation change; runtime behavior and regression coverage from
`9815110b3` are retained. Local Release validation passed all 681 shell tests and
all 73 shared tests on each of `net481`/`net10.0`, using the unchanged binaries.
No automatic rollback or recovery journal was
added, and live interruption/failover validation remains manual. The generic
docstring score identifies no further concrete defect. Review dispositions are
recorded in the [review log](reviews/2026-09-25-pr50-review.md#ha-milestone-review-2026-09-26).

### Pool HA follow-up (2026-09-26)

Following graph milestone `d3b8c9a61`, [PR #50](https://github.com/Narehood/xenadmin/pull/50)
adds [pool high availability](ha-management.md) before merge. Open the editor
from a pool/host's General tab or context menu, or from an HA alert. Alert links
retain the original pool identity and cannot redirect to a different selection
or a replaced pool. Enable HA with reviewed heartbeat storage, configure VM
restart policies and failure tolerance, or disable normally. Existing startup
order/delay are preserved; unknown policies require an explicit supported choice.

Read-only server review checks storage suitability, VM agility and hypothetical
failover capacity. Draft changes invalidate approval. The action rechecks the
reviewed pool/inventory and permissions before using shared `EnableHAAction`,
`SetHaPrioritiesAction`, or `DisableHAAction`. Normal disable has separate gates
so unhealthy hosts, licensing or heartbeat/capacity problems do not block the
recovery request. Changes are sequential, with no automatic rollback or retry;
lost responses require inspecting actual server state and reopening the editor.
Long confirmation messages scroll while their buttons remain available.

HA-milestone validation: 681 shell tests passed in each of Release/Debug,
including 83 new HA cases (49 backend/loopback RPC, 20 editor, 14 alert routing).
Coverage includes successful shared enable/configure/disable task completion,
task cleanup, revoked nested permissions, cancellation, stale configuration,
capacity/agility failures and partial or unconfirmed mutation outcomes. All 73
shared tests passed on each of `net481`/`net10.0`. An actual-window probe passed
24 interaction/layout checks at default/minimum sizes, including a scrolling
confirmation containing 200 VM changes. The temporary harness and screenshots
are in ignored `artifacts/ha-editor-ui-probe`. Existing Windows ACL analyzer
warnings remain; no new production or test warnings were introduced.

The [roadmap](modernization-roadmap.md) now lists AD/RBAC and DR as subsequent
feature work. Live enable/failover/recovery and desktop acceptance remain pending
the user's manual test environment; automated coverage does not establish those
deployment outcomes. Hosted results for this follow-up are recorded in the PR.

### Graph editor follow-up (2026-09-25)

Following review update `adcb021c4` on [PR #50](https://github.com/Narehood/xenadmin/pull/50),
the next implemented milestone is the [performance graph editor](graph-editor.md).
The Performance tab now opens an isolated draft for adding/removing/reordering
graphs and sources, changing titles, and saving compatible layouts. Missing
sources remain visible and persist; Cancel and window close do not save. The
save worker copies the draft, declares `pool.set_gui_config`, checks reviewed
identities/layout against cache and server, and merges only exact target keys
into fresh server configuration. Separate get/set calls are not atomic across
different clients, and a lost save response can leave an uncertain outcome.

Week/year selections no longer show short archives under long-range labels.
RRD sample IDs and polling cursors keep UTC ticks, with local conversion only at
chart display, preventing shifted history and duplicate-hour loss across DST.
No persisted layout format or server-side source recording policy changes.
The [roadmap](modernization-roadmap.md) now lists HA, AD/RBAC and DR as subsequent
milestones. Live graph history and the user's other manual acceptance remain
pending; this follow-up does not complete those deployment gates.

Graph-milestone validation: 598 shell tests passed in each of Release/Debug;
73 shared tests passed on each of `net481`/`net10.0`. The 70 new graph cases cover
layout storage/RPC behavior (36), editor drafts and binding feedback (18), and
history/DST behavior (16). An isolated actual-window probe passed 20 keyboard,
layout, failure/retry, save/close, and Cancel checks at default/minimum sizes.
Evidence and its temporary harness are in ignored `artifacts/graph-editor-ui-probe`.
Existing Windows ACL analyzer warnings remain; the shell builds cleanly.

### PR #50 comment follow-up (2026-09-25)

The [review record](reviews/2026-09-25-pr50-review.md) maps every inline finding
and the recovery/docstring suggestions to its disposition. The bond-mode and
IPv4-disable fixes were already present in `7cb543d9c`; both were rechecked.
The Basic/Digest options now explain their transfer-tunnel scope in both client
settings pages. A reproducible, isolated loopback proxy probe covers both
selections against Basic-only, Digest-only and combined challenges for the real
custom tunnel and `HttpWebRequest` (12 cases). The handler uses Digest when both
are available but can use Basic when that is the only challenge even if the
tunnel setting is Digest. Windows CI now runs that probe.

The proposed management-IP flag inversion was not adopted: the shared action
uses the same negation so a replaced management address does not enable repeated
reconnects to remembered endpoints. Four new IPv6 worker/RPC cases verify the
flag during mutation, submitted values, cleanup and error propagation. Added a
concrete per-host recovery procedure for partial bond/SR-IOV changes. Automatic
rollback and a durable action journal remain unimplemented; live failure/recovery
and the user's other manual acceptance gates remain pending.

Validation of review update `adcb021c4`: 528 shell tests passed in each of Release/Debug, including 72
host-IP cases; 73 shared tests passed on each of `net481`/`net10.0`; all 12
loopback proxy cases passed on .NET 10.0.12. WinForms Release built using the
existing RDP interop; 32,240 resources in 290 sets loaded. Actual settings layouts
passed at three WinForms widths and two shell sizes. Existing warnings remain.

### .NET 10 and advanced networking (2026-09-25, snapshot `7cb543d9c`)

Implementation: [PR #50](https://github.com/Narehood/xenadmin/pull/50), initial
commit `9d107d46b`. Check the PR's current hosted results before merging.

The modernization follow-up targets .NET 10 with SDK 10.0.401 pinned in
`global.json`, while preserving shared `net481` compatibility. System packages
are centrally pinned to 10.0.12 and portable lockfiles are refreshed. The
[migration record](dotnet10-migration.md) explains certificate loading, proxy
registration cleanup, the C# 14 accessor fix, complete OVF password-check reads,
and fragmented classic-RFB padding reads. No unsafe BinaryFormatter switch or
compatibility package is enabled. Legacy WFO1000 designer diagnostics are
suppressed only in WinForms pending a separate metadata audit.

The shell adds [advanced networking](advanced-networking.md): pool-wide NIC
bonds (create, change mode, remove), host IPv4/IPv6 configuration, and SR-IOV
provisioning/removal. Plans validate exact identities, current topology,
capabilities and dependencies again on the action worker. Shared XenModel
actions remain the execution path where available. Management bond changes,
dependent interfaces and other unsupported combinations remain blocked; changing
a management IP requires explicit disruption confirmation and manual reconnect.
Pool-wide failure can leave partial changes, so inspect actual server state
before retrying. No live pool operations were performed.

Native Linux CI now tests both shell configurations and portable shared code,
publishes a self-contained archive, verifies updater helper rejection with
startup hooks disabled, and starts the actual main window under Xvfb. Windows
CI retains full Release/Debug WinForms builds and adds runtime resource probes.
The publish script uses ignored RID-specific locks and preserves the committed
portable graphs. The release workflow uses the same package checks.

The [performance baseline](performance-baseline.md) records repeatable synthetic
inventory and console measurements. Overlap-safe CopyRect row copies reduce the
measured 4K scroll median from 98.5 ms to 5.24 ms and managed allocations from
33.2 MB to 208 bytes including presentation; pixel and allocation regressions
cover clipping and overlap. Real network/GPU/desktop timing remains unmeasured.

The retained `asv/xsa-498` SDK update was reviewed rather than merged wholesale:
it changes transport contracts and removes the XCP-ng 2.16 API mapping. Four
loopback TLS cases preserve the current certificate-validation boundary; see
the [SDK compatibility review](reviews/2026-09-25-sdk-compatibility.md).

Validation of `7cb543d9c`, before review update `adcb021c4`: full local
Release/Debug solution builds; 524 shell tests in each;
73 shared tests on each of net481/net10.0 in both configurations; all 32,239
WinForms resources in 290 sets load on .NET 10.0.12 in both configurations;
Settings initialization and fragmented RFB reads pass. The local machine lacks
AxImp, so its builds reused the unchanged RDP interop DLLs from the trusted
development CI artifact with `SkipRdpAxImp=true`; hosted Windows CI must rebuild
them normally. The NuGet transitive vulnerability audit reported no advisories.
An isolated offscreen probe exercised all three actual editor windows; native
DPI and physical desktop behavior still need acceptance testing.
The local self-contained Windows ZIP also passed all four malformed-updater
invocations with startup hooks disabled, using bundled .NET 10.0.12; SHA-256
`8cf6bafdf10dc04de96c4f559c2a6f762fe63ef540f4feeccca942b557ac9d03`.
Portable lockfile hashes were unchanged by publishing.
The first hosted pass exposed an environment-dependent implicit `net481`
reference-assembly dependency. It is now explicit and centrally pinned so
installed targeting packs do not change the locked dependency graph. Native
Linux also exposed old updater tests using Windows-only path literals; those
fixtures now use native paths; only Windows-specific checks are explicitly
skipped on Linux, without relaxing updater validation.
Review follow-up preserves unrecognized/mixed bond modes until an explicit
supported choice and sends genuinely empty address fields when disabling IPv4.
Four loopback worker/RPC regressions verify the latter, including failure cleanup,
stale identity rejection and no retry; six bond cases verify safe selection.
Windows hosted builds, resources, tests and packaged execution passed. Linux
tests and package helpers passed; its desktop smoke now initializes Openbox's
EWMH metadata before Avalonia so PID-based window discovery works on private
Xvfb displays. The final hosted pass remains visible in the PR checks.

The user has no disposable pool/VM/test machines and explicitly deferred live
and UAC checks. Keep those gates pending in [platform acceptance](platform-acceptance.md).
The [roadmap](modernization-roadmap.md) prioritizes graph editing and HA/AD/DR
after acceptance; WinForms remains the supported production client. Earlier
dated sections below are historical snapshots, including their .NET 8 counts.

Branch cleanup removed only reviewed redundant branches (9 local, 14 remote).
The local audit and verified pre-cleanup bundle remain in
`.git/branch-cleanup-20260925-205344/`; five unique remote topics were retained.

### Awa console fold (2026-09-23)

The embedded console host was fixed at 600px inside the detail scroll viewer. At the
default 1280×800 window that fold is shorter, so the bottom of the guest screen
sat below the visible area. With scale-to-fit on, the host matches the scroll
viewport and follows window resizes. With scale-to-fit off, the host grows to the
1:1 desktop height in DIPs (at least the viewport) so the same scroller can reach
the rest of the frame. It recomputes when the setting, desktop size, or DPI changes.
Object, power state, and endpoint details stay below the fold.

The default build codename is `Awa` in `Directory.Build.props` and the publish
workflow. Splash and Settings → About read that stamp. Previously published Sandy
releases keep the Sandy name.

Validation: **325 shell tests** and **73 shared tests on each of net481/net8.0**.
An offscreen layout of the main window at 1280×800 and 1280×700 showed the console
host bottom flush with the scroll viewport (578px, then 478px). Live guest consoles
remain a manual check.

### Sandy appearance release (2026-09-22)

Merged [PR #47](https://github.com/Narehood/xenadmin/pull/47) into `development`
as `2edd2edbac72bf7e8417eb994d5e3a162da43fe6` and published
[Sandy 2026.9.22.2](https://github.com/Narehood/xenadmin/releases/tag/v2026.9.22.2)
from that commit via [release run 35754905742](https://github.com/Narehood/xenadmin/actions/runs/35754905742).
The tag resolves to the merged commit. The release is stable and is the latest
published build. Revision `2` was required because `v2026.9.22.1` was already
published earlier the same UTC day.

The merge commit's build-and-test, CodeQL, and dependency-submission checks
passed before publish. Downloaded both public Windows/Linux assets and verified
their GitHub SHA-256 digests (`50384eff75516a0a36fd563e58b9c09de240567a7947ec156bb36a7455aa43e5`
and `4520962ed3c532a651760a3ff8197fe6c98b507f7d9599f931713a03af667a57`). Both
archives keep the shell, `INSTALL.TXT`, and runtime files at the archive root.
The Linux executable is mode `0755`. Both packaged `XcpNgCenter.Shell.dll`
files report file version `2026.9.22.2` and include the `Sandy` codename.
Informational version is `2026.9.22.2+2edd2edb-dirty`; the displayed calendar
version stays the four-part number. Light-theme chart readability, OS theme
changes in System mode, and Linux desktop interaction remain manual checks.

### Settings tabs and appearance (2026-09-22)

Opened [PR #47](https://github.com/Narehood/xenadmin/pull/47) from implementation
commit `c61aba71790a6104a20df77386ce80b17ae58557` against `development`.

The Avalonia Settings window now uses 14px tab labels and a single horizontal
header row. All seven tabs fit at the default 760px width; narrower windows
scroll the headers. Display has a scrollable Appearance section with Dark,
Light, and System themes, an opaque RGB/HSV/palette color picker, and Reset
appearance. Preferences apply immediately and persist in `app-settings.json`;
existing profiles keep the original dark/orange defaults. Accent previews update
live while picker changes are debounced into one durable write after interaction;
application shutdown flushes any remaining change.

`ShellThemeManager` updates live resources and Fluent accents before the splash
opens and when settings or the system theme change. Shell windows, dialogs,
gradients, and chart chrome use the live palette. Accent labels, button text,
and performance-chart series ink adapt for contrast. Series strokes, fills, and
hover markers use `ReadableSeriesColor`, which keeps a configured hue and shifts
it only enough to read against the plot. Tooltip text is still adjusted against
the tooltip surface. Console framebuffer colors retain their meaning. The
classic WinForms client is unchanged.

PR #47 review: the chart comment cited the plot fill, but the missing contrast
adjustment was the two `ColorHex` parses used for pens and hover markers. Light
`BgElevated` is under 3:1 against every configured series color, so that change
stays. The docstring-coverage warning scored 0% because all 41 touched functions
were skipped as unsupported. C# XML docs cannot move that check, so those
methods were left as they were. `ReadableSeriesColor` has a summary because the
contrast shift is not obvious at the call site.

Validation: **317 shell Release tests**, including 21 appearance cases,
and **73 shared tests on each of net481/net8.0**, with locked restores. Coverage
includes older/invalid preferences, reopen/reset, preserving unrelated settings,
opaque color normalization, debounced and shutdown-flushed persistence, contrast
for extreme accents in both themes, and chart series ink against light and dark
plot surfaces.
An offscreen Windows probe exercised actual Settings and picker popup bindings,
live main-window/gradient/chart updates, reset, default/minimum-size header
layout and scrolling, and rendered at 100/150/200%. Evidence and probe source:
`%LOCALAPPDATA%/Temp/sandy-appearance-check` (`bin/Release/net8.0/results.log`
and PNGs). It used synthetic settings and no live servers. System mode was
checked for platform delegation; changing the OS theme and Linux desktop
interaction remain manual checks. That probe predates the series-ink adjustment,
so light-theme chart readability still needs a visual check. Existing Windows
ACL test analyzer warnings remain.

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
