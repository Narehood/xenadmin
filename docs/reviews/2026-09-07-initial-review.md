# Initial Astra code review — 2026-09-07

> Historical snapshot: this document records the initial pre-fix review. See the
> [remediation record](2026-09-07-remediation.md) for subsequent source changes,
> regression coverage, and remaining validation. The line numbers and baseline
> counts below have not been rewritten to describe the modified tree.

## Scope and result

Reviewed `development` at `b01829b8e25c02d48cce7e3cb64897d404bd72cb`, including
the existing uncommitted updater/package-layout changes. This is a targeted
initial review across a repository with roughly 1,700 C# files, not an exhaustive
audit. Priority went to credential/update trust, import/export boundaries,
connection handling, shell rendering/lifecycle, tests, and CI.

Thirteen actionable findings follow. **The initial review itself implemented no fixes.** P1 means
address early because of credential exposure, destructive behavior, privileged
execution, or silent data-integrity failure; P2 means a concrete defect for the
next correction cycle. These are engineering priorities, not CVSS scores.
All findings concern code already in HEAD. References use working-tree line
numbers; user edits to the updater and packaging were preserved.

| ID | Priority | Finding |
| --- | --- | --- |
| R01 | P1 | Main-password mode stores the credential decryption key in settings |
| R02 | P1 | Import cleanup deletes original or preexisting files by name prefix |
| R03 | P1 | Local OVF references escape the selected appliance directory |
| R04 | P1 | Updater can elevate a substituted executable from its writable cache |
| R05 | P2 | A CA-valid replacement certificate bypasses an existing TOFU pin |
| R06 | P2 | Folder manifests can omit the files whose integrity should be checked |
| R07 | P1 | Substring matching can import the wrong virtual disk |
| R08 | P2 | Debug request logging exposes session credentials |
| R09 | P2 | Incremental graph refresh destroys long-range history |
| R10 | P2 | Browsing an OVA scans disk contents on the UI thread |
| R11 | P2 | Inventory updates discard pending ISO selections |
| R12 | P2 | Console retry cooldown can discard the only scheduled retry |
| R13 | P2 | CI can pass after the shared test suite fails |

## Security and data integrity

### R01 — Main-password storage exposes the encryption key

Locations: [SavedServerStore.cs](../../XcpNgCenter.Shell/Services/SavedServerStore.cs),
149–156; [ShellAppSettings.cs](../../XcpNgCenter.Shell/Services/ShellAppSettings.cs),
441–445 and 503–512; [EncryptionUtils.cs](../../XenCenterLib/EncryptionUtils.cs),
126–143. Migration: `SettingsViewModel.cs:484–493`, `MainViewModel.cs:745–764`.

`ProtectPasswordWithMainPassword` passes the main-password hash directly as the
AES key. Settings serialize that exact key as Base64 under `mainPasswordHash`.
Copying `app-settings.json` and `saved-servers.json` is sufficient to decrypt
`mp1:` passwords without the main password, DPAPI, or Linux device key. Enabling
this option replaces normally DPAPI-protected Windows credentials with this
offline-decryptable format.

**Verified:** actual shell classes wrote a synthetic credential/settings pair;
AES decryption using only the two JSON files recovered the synthetic secret.

**Fix:** separate the unlock verifier from the encryption key. Use a salted
password KDF and authenticated encryption; retain the encryption key only in
unlocked session memory. Persist KDF parameters and a distinct verifier or
authenticated check value. Migrate transactionally, preserving recoverability.
Test that persisted metadata alone cannot decrypt, and test failed/cancelled
migrations. Persisting a stronger derived key would still have this flaw.

### R02 — Import cleanup does not establish ownership of files it deletes

Location: [Import.cs](../../XenModel/Actions/OvfActions/Import.cs), 466–471;
temporary names are assigned at 285 and 302.

The `finally` block deletes `sourcefile` whenever its basename starts with
`enc_` or `unc_`. Those names can belong to original user files. It also assigns
a decompression output path before creation succeeds, so a `CreateNew` collision
can cause cleanup to delete the preexisting file that prevented creation.

**Verified using the actual private ImportFile method and disposable files:**
an ordinary `unc_original.vhd` was deleted after parsing failed; a preexisting
`unc_disk.vhd` was deleted after importing `disk.vhd.gz` hit a creation collision.
Fixtures intentionally failed before any host/API operation.

**Fix:** create unique temporary files in a private working directory and track
only files successfully created by this operation. Never infer ownership from
a prefix. Regression tests must prove original sources and colliding existing
files survive success, failure, and cancellation.

### R03 — Local appliance file references are not contained

Locations: [Package.cs](../../XenOvfApi/Package.cs), 154–198;
[Import.cs](../../XenModel/Actions/OvfActions/Import.cs), 258–265.

Folder-package reads, existence checks, manifest reads, and disk import combine
the package folder with an untrusted filename without a containment check.
Relative traversal and rooted paths can select files outside the chosen folder.
Both UI clients reach this shared implementation. Tar extraction's path checks
do not protect local OVF descriptor references.

**Verified:** a parsed OVF referencing `../enc_outside.vhd` passed `OVF.Validate`;
`FindRasdFileName` preserved the path and `HasFile` returned true. Calling the
actual import method then deleted that review-owned outside file through R02.
This demonstrates containment failure and its destructive composition with R02;
no real file or live-host upload was used.

**Fix:** resolve every local reference through a common containment policy
before opening, hashing, importing, or deleting. Reject rooted/traversal paths;
address symlink/reparse escapes as part of the same boundary. Test Windows and
Linux path semantics and a valid nested relative reference.

### R04 — Cached update code is trusted after it becomes mutable

Locations: [ShellUpdateInstaller.cs](../../XcpNgCenter.Shell/Services/ShellUpdateInstaller.cs),
253–278, 297–301, 370–393, 466–473, and 1293–1309.

Initial downloads are digest-verified, but prepared-update reuse checks metadata,
file presence, and the version of `XcpNgCenter.Shell.dll`. It does not authenticate
the executable or dependencies. The cached executable itself becomes the helper
launched with `runas` for a protected installation.

**Threat model:** a process already running unelevated as the same user modifies
staged code between download and restart. The user's later legitimate update
UAC approval elevates that substituted code. This requires local write access
and subsequent user approval; it is not a demonstrated remote exploit or silent
UAC bypass.

**Verified:** actual `TryGetPreparedUpdate` accepted an EXE containing arbitrary
text while a genuine matching shell DLL remained alongside it. Actual
`CreateApplyStartInfo` selected that EXE with `runas`. Nothing was executed or
elevated in the reproduction.

**Fix:** start elevation from trusted installed code, authenticate the package,
and construct/apply it in protected staging before loading any new code. Keep
the verification valid through installation. An unauthenticated hash beside a
writable payload, or an unelevated pre-launch recheck alone, leaves substitution
possible. Add tampered-EXE/dependency and apply-boundary tests, followed by real
Windows UAC/restart testing.

### R05 — Existing pins are skipped when normal chain validation succeeds

Locations: [TofuCertificateValidator.cs](../../XcpNgCenter.Shell/Services/TofuCertificateValidator.cs),
31–32; production [SSL.cs](../../XenAdmin/Network/SSL.cs), 63–67.

Both callbacks accept `SslPolicyErrors.None` before looking up an existing pin.
A previously pinned self-signed host can therefore present a different,
OS-trusted certificate without the configured change warning. A valid matching
certificate from a misissuing or interception CA would bypass the documented
pin-change policy. Ordinary CA validation remains intact.

**Verified:** with a stored pin for synthetic certificate A, actual shell
validation accepted certificate B when passed `SslPolicyErrors.None`. This
tests callback policy, not an intercepted network connection.

**Fix:** enforce an existing pin before the normal trusted-chain fast path.
Define first-use behavior for unpinned CA-valid hosts separately. Test changed
pins with both trusted and untrusted chains in both clients.

### R06 — Folder manifest verification accepts incomplete coverage

Location: [Package.cs](../../XenOvfApi/Package.cs), 178–191.
Caller: [ImportApplianceAction.cs](../../XenModel/Actions/OvfActions/ImportApplianceAction.cs),
104–108. Compare the archive implementation at `Package.cs:359–397`.

`FolderPackage.VerifyManifest` verifies only listed entries. It never requires
the descriptor and referenced disks to be listed, so an empty manifest succeeds
and an omitted disk is never checked. This defeats the user's selected integrity
check. A checksum manifest itself is not proof of publisher authenticity.

**Verified:** actual package verification accepted an empty manifest and a
descriptor-only manifest after the unlisted disk changed.

**Fix:** require complete descriptor/referenced-file coverage, valid unique
entries, and contained paths before checking digests. Test missing disk,
missing descriptor, empty manifest, tampering, and valid packages in both folder
and archive forms.

### R07 — Disk references match substrings instead of identifiers

Location: [OVF.cs](../../XenOvfApi/OVF.cs), 3464–3475 (`FindRasdFileName`).

`diskReference.Contains(vdisk.diskId)` and
`filer.id.Contains(vdisk.fileRef)` return the first partial match. With disk IDs
`disk1` and `disk10`, selecting `ovf:/disk/disk10` can resolve the file belonging
to `disk1`. The appliance can import with incorrect disk contents; both clients
share this resolver.

**Verified:** an actual in-memory OVF fixture requested `disk10` and returned
`wrong.vhd` from `disk1`, rather than `correct.vhd`.

**Fix:** parse the supported disk-reference syntax, then compare complete disk
and file IDs. Test prefix collisions in both ID sets, ordering independence,
and supported legacy reference forms.

### R08 — Debug JSON-RPC logging includes privileged session tokens

Locations: [Session.cs](../../XenModel/XenAPI-Extensions/Session.cs), 75–99;
[JsonRpc.cs](../../XenModel/XenAPI/JsonRpc.cs), 230–239.

Debug builds forward full JSON requests and log positional parameters for event
and task calls. Those arguments include the session opaque reference. A shared
diagnostic log can expose a still-valid management session.

**Scope:** this requires a Debug binary **and** DEBUG logging enabled for the
session logger. The checked-in WinForms config defaults to INFO; Release builds
forward method names only. CI publishes Debug artifacts, so this is a real
diagnostic path, not the default Release behavior. Evidence is the traced
request/logging path; no real credential was logged during this review.

**Fix:** redact structurally before logging, or omit parameters. The existing
`LogRedaction.RedactSecrets` helper has no production callers and its assignment
regex would not cover a positional JSON session token. Add an actual logger
capture test asserting sensitive positional values never reach an appender.

## Performance and behavior

### R09 — Graph refresh replaces long-range history with short-range samples

Locations: [ShellRrdMaintainer.cs](../../XcpNgCenter.Shell/Services/Performance/ShellRrdMaintainer.cs),
100–124 and 215–225; [RrdModels.cs](../../XcpNgCenter.Shell/Services/Performance/RrdModels.cs),
86–97.

Minute/hour/day refreshes use the same ten-minute `interval=5` request as the
five-second archive. Merging those points into a capped coarse archive evicts
older history. The two-hour graph degrades on its first minute refresh; longer
archives have the same defect at their refresh intervals.

**Verified against the compiled shell:** 120 one-minute samples spanning
119 minutes shrank to **9.917 minutes** after merging the current update
request's 120 five-second samples. 110 retained samples were off minute alignment.

**Fix:** request the correct resolution and track a cursor for each archive.
Test retained history horizon, sample spacing, and duplicate handling across
incremental updates. Avoid repeatedly fetching overlapping ten-minute windows
when only new points are needed.

### R10 — OVA browsing performs streaming I/O on the UI thread

Locations: [OvfImportViewModel.cs](../../XcpNgCenter.Shell/ViewModels/OvfImportViewModel.cs),
92–113; [Package.cs](../../XenOvfApi/Package.cs), 271–310;
[TarArchiveIterator.cs](../../XenCenterLib/Archive/TarArchiveIterator.cs), 82 onward.

After the picker returns, `LoadPackage`/`OVF.Validate` synchronously scans every
archive entry, consuming skipped disk data and decompressing gzip content. Large
appliances or slow storage stall the UI before the background import action
starts, without a cancellation path.

**Measured with the compiled package library:** reading metadata from a synthetic
256 MiB gzip appliance took **245.5 ms synchronously**, although the descriptor
was first and the compressed file was only 1,171,514 bytes. This is a local
demonstration of full traversal, not a real-desktop performance benchmark.

**Fix:** load/validate on a worker with cancellation and publish the completed
result on the UI thread. Prevent import against stale/incomplete state. Verify
UI responsiveness with a large appliance on slow storage.

### R11 — Inventory changes overwrite pending ISO selections

Location: [MainViewModel.Actions.cs](../../XcpNgCenter.Shell/ViewModels/MainViewModel.Actions.cs),
410–432. Bindings: `Views/MainWindow.axaml:879,903`.

Refreshing details clears `SelectedConsoleIso`, then restores only the currently
attached ISO. If an inventory event arrives between choosing another ISO and
pressing Attach, the user's pending choice disappears. The event path is
`XenObjectsUpdated` -> tree rebuild -> detail refresh -> `RefreshSelectedVm` ->
`RefreshConsoleIsoOptions`; events from other servers can also refresh details.

**Evidence:** verified control flow and separate selection/Attach bindings; no
live UI reproduction. **Fix:** preserve a pending VDI reference for the same VM
if still available. Reset it on VM change, removal, or completed insertion. Test
an unrelated inventory event between selection and Attach.

### R12 — Retry cooldown can strand a guest console

Location: [MainViewModel.cs](../../XcpNgCenter.Shell/ViewModels/MainViewModel.cs),
1707–1715 and 1757–1762.

The reconnect path posts one immediate callback. If a reconnect fails inside
the 1.5-second cooldown, that callback returns without scheduling a later retry.
The console can remain disconnected until a new inventory event or user refresh.
The heartbeat does not refresh the console; inventory events depend on actual
cache changes, so activity on busy pools can mask the failure.

**Evidence:** traced scheduler, heartbeat, and event paths; existing tests cover
the policy predicate, not scheduling. **Fix:** schedule a cancellable delayed
retry for the remaining cooldown and cancel it on success/selection change/
disposal. Preserve the deliberate no-retry behavior for host control domains.
Use a fake clock/session to test rapid failure without a live host.

## Build and CI

### R13 — A passing second test command can mask the first suite's failure

Location: [test-builds.yml](../../.github/workflows/test-builds.yml), 62–65.

Both `dotnet test` commands run in one default Windows PowerShell step without
checking the first exit code. GitHub's built-in PowerShell wrapper exits with
the last native-command status; a later successful command can therefore hide
the first failure. See the official [shell exit behavior](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#exit-codes-and-error-action-preference).

**Reproduced:** with stop-on-PowerShell-error behavior enabled, a synthetic first
native command exited 1, the second exited 0, and the GitHub-style final exit was
0. This validates command handling locally, not a dispatched workflow run.

**Fix:** put suites in separate steps or explicitly check each exit code. Verify
the step fails when either suite fails. Also give each target framework a unique
TRX filename; this review observed the net8.0 result overwrite the net481 file.

## Maintainability observations

The useful cleanup targets are behavior and boundaries, rather than inferring
which code was AI-written:

- **Tests detached from the feature boundary:** the unused log-redaction helper
  has passing tests, while the actual logger exposes positional tokens. Console
  tests cover a policy helper but miss delayed retry behavior. Credential,
  OVF-manifest, disk-reference, and RRD defects escaped the current suite. Add
  regression tests at the affected boundary while fixing each issue.
- **Concentrated responsibilities:** `MainViewModel` and its partials coordinate
  inventory, selection, credential reconnect, dialogs, VM actions, and console
  lifetime; `ShellUpdateInstaller` combines acquisition, extraction, validation,
  elevation, rollback, and restart. Split along these responsibilities as part of
  the fixes, preserving shared model actions and both UI clients.
- **Redundant refresh work, impact not benchmarked:** inventory events rebuild a
  server's entire tree on the UI thread. The selected-server path refreshes
  details through selection and again explicitly (`MainViewModel.cs:1445–1454`,
  `1864–1871`). Coalesce notifications and remove duplicate work before a broad
  rewrite; profile with a large pool.
- **Framebuffer allocation/lifetime, impact not benchmarked:**
  `AvaloniaRfbFramebuffer.FlushUiSync` copies a full pixel snapshot per
  presentation while holding its lock (about 7.9 MiB for 1920×1080), then copies
  into a bitmap. `DesktopSize` clears old bitmap references without explicit disposal
  (`AvaloniaRfbFramebuffer.cs:214–216`). Check GC/native-resource behavior and UI
  ownership before changing the double-buffering workaround. Permanent leakage
  was not established.
- **Documentation should reflect actual guarantees:** main-password protection,
  verified prepared updates, and strict later-connection pins need the fixes
  above before the current wording accurately describes their security.
  Documented preview omissions such as shell RDP/HA/AD/DR are not counted as bugs.

## Validation and limits

- Shell Release build passed; shell tests **50/50**.
- Shared tests **73/73 net8.0**, **73/73 net481**: **196 passing test executions**
  overall, with shared cases intentionally counted once per framework.
- Locked restores passed. NuGet's vulnerability query included transitive
  dependencies and returned no known vulnerable packages from nuget.org.
- Full solution Release build could not complete: local `aximp.exe` and RDP
  interop prerequisites are absent. The error is `XenAdmin.csproj:75`, not an
  established application compilation regression.
- Proof harnesses used freshly built assemblies and disposable synthetic data
  outside the repository. No real credentials, live host mutations, elevated
  updater launch, Linux runtime test, or full desktop soak was performed.
- Detailed environment, commands, and temporary evidence locations are in the
  [Astra handoff](../ASTRA_HANDOFF.md).

Suggested correction order: R01; R02/R03/R07 together as separately testable
import changes; R04; R05/R06/R08; R13; R09–R12. Preserve the user's pending
portable-layout work when changing the updater.
