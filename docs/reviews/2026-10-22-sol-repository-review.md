# XenAdmin repository review 22 October 2026

## Result

Reviewed `development` at `d831f0b892b3186b79a13bac5aefbde3b6827860`.
The checkout was clean at the start. The findings below describe that reviewed
snapshot. After the user authorized verification and fixes, Sol confirmed all
nine findings and committed corrections on `fix/sol-review-20261022`.
See the verification and remediation record below for current status.

**Nine findings were confirmed: four P1 and five P2.** The most urgent were an
unrequested VM restart from the Properties dialog, import cleanup that targets
preexisting storage, and management session credentials forwarded over a
redirected plaintext connection. The retained WinForms PowerShell plugin path
also interpreted substituted object names as code when that optional feature is
enabled.

The baseline build and existing automated checks were healthy: all **27 Windows
acceptance checks passed**, including **2,435 passing test executions**, with no failed or
skipped tests. Both solution configurations built with zero warnings and errors.
The transitive NuGet vulnerability query returned no known vulnerable packages.
Those results coexisted with the newly reproduced defects; they do not establish
release readiness for the affected workflows.

P1 means address promptly because normal use can interrupt a guest, destroy
data, expose management credentials, or execute unintended code. P2 means a
confirmed correctness or protection weakness for the next correction cycle.
These are engineering priorities, not CVSS scores.

### Findings index

1. **F01 P1:** Saving VM Properties can reduce memory and restart a running guest
   without any field edit.
2. **F02 P1:** Failed appliance import cleanup can destroy preexisting VDIs and
   target an existing ISO image.
3. **F03 P1:** Console HTTP redirects can forward a management session token to
   a different authority over plaintext.
4. **F04 P1:** PowerShell plugin parameters evaluate expressions from substituted
   object labels.
5. **F05 P2:** Snapshot Revert and Delete run directly from a single button click
   without confirmation.
6. **F06 P2:** XVA verification accepts an archive missing blocks listed in its
   checksum table.
7. **F07 P2:** Replacing a support ZIP can retain the old report and its data.
8. **F08 P2:** OVF compression deletes its source before a destination collision
   is resolved.
9. **F09 P2:** WinForms main-password storage derives its encryption key with one
   unsalted SHA-256 operation.

## Verification and remediation

The requested Opus 5.5 verification could not run: one configured provider hit
its rate limit and the other had additional usage disabled. Neither produced
review findings. The user then explicitly authorized Sol to continue. The
verification and changes here are Sol's work; they are not an independent Opus
review. F05 was downgraded to P2 because it concerns missing confirmation for an
explicit destructive action, while F01 can restart a guest after an untouched
Apply.

All nine findings are valid against the reviewed commit. Corrections and
permanent regression coverage are committed for a PR against `development`:

- `e6f409a06`: F01/F05, VM hardware and snapshot safety.
- `3893814a7`: F02, appliance-import storage ownership.
- `e737660ab`: F03, authenticated HTTP redirect policy.
- `782bfa10c`: F06-F08, archive integrity and safe replacement.
- `bd13a703c`: F04/F09, WinForms plugin values and credential migration.

No release or deployment has been made.

| Finding | Implemented correction | Permanent evidence |
| --- | --- | --- |
| F01 | Compare CPU and memory fields with their opening values. Preserve the static memory ceiling for live allocation edits. Refuse memory changes that require a guest restart, and reject stale memory/state at execution. Shared WinForms callers retain their existing restart behavior. | `ReviewVmPropertiesTests`: untouched and name-only Apply, unequal CPU/memory limits, running and halted edits, restart refusal, stale-state rejection. |
| F02 | Track successful VDI creates for the current VM before cache delivery. Both rollback paths delete only owned VDIs, including unattached disks. Existing mapped disks receive no VBD ownership marker. Successfully imported earlier VMs are committed separately. | `ReviewImportOwnershipTests`: existing-disk capacity/upload failure, newly created-disk cleanup, existing ISO/disk preservation, owned disk missing from cache. |
| F03 | Reject TLS downgrades and redirects to new credential recipients before opening their transport. Allow relative/same-authority redirects and HTTPS transfers to members of the authenticated connection's captured host inventory, identified by the exact session token. Rebuild request path and Host for the destination. Proxy redirects fail closed. | `ReviewHttpRedirectTests`: actual CONNECT/GET/PUT redirects, path/Host changes, no connection to rejected authority, TLS policy, matching-token pool inventory and port limits. |
| F04 | Encode the complete PowerShell command as UTF-16 Base64 for process launch. Serialize substituted values as nonexpanding literals, use literal paths, invoke the configured function as one command name, and remove the raw trailing parameter source. | Built WinForms `--review-fixes` probe executes generated code in Windows PowerShell with expression syntax, quotes, smart quotes, newlines, backslashes and Unicode in a label and metadata; exact values survive. |
| F05 | Await a named-VM/named-snapshot confirmation with a data-loss warning. After acceptance re-resolve the original connection and objects, confirm their UUIDs, relationship, power state and allowed operation, then construct the action. | `ReviewSnapshotTests`: real commands remain pending before review; accept, cancel, and changed-target cases for Revert and Delete. |
| F06 | Check every checksum-table declaration is present as well as checking every observed block's hash and table membership. Keep legacy exports without a global table readable. | `ReviewArchiveRegressionTests`: complete, missing, extra, damaged and legacy XVA archives. |
| F07 | Build and finish a fresh ZIP beside its destination, then atomically replace the old report. Cancellation/error cleanup removes the temporary archive and preserves the previous destination. | `ReviewArchiveRegressionTests`: smaller replacement, exact ZIP entry, no stale tail, cancellation and missing-source failure. |
| F08 | Commit transformed output before deleting the source; destination collisions leave both files intact. Use replacement when decompression has no extension change. Propagate truncated-stream failures. | `ReviewArchiveRegressionTests`: compression/decompression collisions, cancellation and content round trip. |
| F09 | Add WinForms `wp2` entries with a random 32-byte salt, PBKDF2-HMAC-SHA256 at 600,000 iterations and separate AES-256-CBC/HMAC-SHA256 keys. Authenticate the version, work factor, salt, IV and ciphertext before decrypting. Derive once per unlocked context and generate a fresh IV for every entry. Migrate a legacy batch only after every entry decrypts and validates; save the settings file atomically. | Dual-framework `PasswordCredentialProtectionTests`: round trip, random salt/IV, wrong password, tampering, work-factor bounds and disposal. Built WinForms probe: real legacy and versioned unlock, bad batch/password, cancelled restore, failed migration/save and new credential writes. |

All **29 Windows automated acceptance checks pass** on the corrected working
tree: zero-warning/error Release and Debug solution builds; **1,134 shell tests
per configuration**, **132 shared .NET 10 tests** and **131 shared Framework
tests**, for **2,531 passing executions**, none failed or skipped. Both built
WinForms review probes pass alongside resource, lifecycle/designer and plugin
archive checks, all six UI modes, proxy authentication and a fresh Windows
package smoke test. Portable locks are unchanged; trusted existing RDP interop
was reused. Evidence: `artifacts/sol-review-fixes-20261022/acceptance-r1/`
(`acceptance.json`, four TRX files, logs, UI records and the package smoke result).
The evidence manifest records the dirty working tree against the reviewed base;
these are local results for the identical implementation patch before commit.
All 23 production/test/tool files were checked byte for byte against that
validated patch during PR preparation. Hosted CI is separate and pending.

Compatibility and remaining limits:

- No real saved profile or pool was used. Tests use synthetic credentials,
  disposable local files, cached inventory and loopback transports. Opus access
  remains unavailable; a second-model review has not occurred.
- `wp2` is a one-way write upgrade: older WinForms builds cannot read migrated
  password-protected session entries. Legacy entries remain readable in this
  build. Existing backups/copies remain weak until separately replaced. Shell
  `mp2` and Windows DPAPI credential modes keep their formats. The existing
  in-memory password hash is only KDF input and is not persisted as a verifier.
- Redirects to hosts absent from the owning authenticated connection's inventory
  are rejected. Pool redirect behavior still needs live-pool acceptance,
  especially installations using host aliases or custom ports.
- A plugin's `function` attribute now denotes a command/function name; argument
  data belongs in its parameter array. Trusted plugin scripts can deliberately
  evaluate data themselves. Optional plugins remain disabled by default.
- Compression commits individual files; an entire multi-file OVF transformation
  and its descriptor save are not a single transaction. A later file or metadata
  failure can leave an earlier successfully transformed file committed. The
  confirmed collision path preserves the source and existing destination.
- Snapshot confirmation offers a warning to create a safety snapshot first; it
  does not create one automatically. Server-side RBAC still decides whether an
  accepted operation is authorized.
- Physical desktop, native Linux, real updater/UAC and live-pool acceptance are
  still pending. The separately reported reboot hang is not resolved here.

## Findings in the reviewed snapshot

### F01 P1 Saving VM Properties can restart a guest without an edit

**Locations:** [VmPropertiesViewModel.cs](../../XcpNgCenter.Shell/ViewModels/VmPropertiesViewModel.cs),
187 and 964–977; [ChangeMemorySettingsAction.cs](../../XenModel/Actions/VM/ChangeMemorySettingsAction.cs),
64–69 and 93–144. Client: Avalonia shell.

The editor initializes its single memory field from `memory_dynamic_max`.
Apply compares that value with both the dynamic and static maxima, then submits
the displayed value as **both** maxima. A VM whose static maximum exceeds its
dynamic maximum therefore receives a memory-change action even when the user
only opens Properties and presses OK. The shared action shuts down a running VM
when the static limit changes, then attempts to start it again. It chooses hard
shutdown when clean shutdown is not advertised as available.

**Reproduced:** a synthetic running VM with an 8 GiB dynamic maximum and 16 GiB
static maximum displayed `8192` MiB. Applying the untouched editor constructed
an action reducing the static maximum to 8 GiB with `_needReboot = true`.
Its required methods included `vm.hard_shutdown` and `vm.start_on`. The probe
intercepted the status callback before any worker started. No guest was stopped.
The CPU/Memory section contains a general reboot hint, but this action also
arises when no CPU or memory edit occurred and another section is displayed.

**Correction and regression coverage:** preserve all four memory bounds unless
the user explicitly changes them. Compare edited values against the opening
draft and revalidate current VM state before execution. Require confirmation
for an actual restart or hard shutdown. Cover unchanged Apply and name-only
edits with unequal dynamic/static maxima, plus explicit memory changes on
running and halted VMs.

### F02 P1 Appliance import cleanup does not establish storage ownership

**Locations:** [Import.cs](../../XenModel/Actions/OvfActions/Import.cs), 403–461
and 1269–1305; [ImportApplianceAction.cs](../../XenModel/Actions/OvfActions/ImportApplianceAction.cs),
135–136. Shared implementation; both clients reach the appliance importer.

When an import maps to an existing VDI, `ImportFile` puts that VDI in `vdiRef`
before checking its capacity. The exception handler destroys every non-null
`vdiRef`, regardless of whether the operation created it. An insufficient-space
validation failure can therefore issue `VDI.destroy` against the selected
existing disk before uploading anything.

The outer `CleanUpVm` routine has the same ownership problem. It collects every
VDI attached to the partially imported VM and attempts to destroy each one after
destroying the VM. It does not distinguish newly created disks from mapped
existing disks or CD media belonging to an ISO library. Actual deletion remains
subject to server permissions, storage capabilities, and in-use protections.

**Reproduced against a loopback XenAPI fixture:** importing a 64-byte synthetic
ISO into a preexisting 16-byte destination raised the capacity error and sent
`VDI.destroy(existing-review-vdi)`. Calling the production VM cleanup with a
preexisting disk and CD image then sent `VM.destroy`,
`VDI.destroy(existing-review-vdi)`, and `VDI.destroy(existing-review-iso)`.
The fixture recorded requests and returned synthetic responses; no pool or real
storage was involved.

**Correction and regression coverage:** keep an operation-owned set of VDIs
successfully created by the import and use it throughout both cleanup paths.
Preserve mapped VDIs and library media on validation failure, upload failure,
cancellation, and later VM-resource failure. Verify that newly created VDIs
still receive cleanup and that an unsuccessful cleanup cannot hide the original
error. The earlier local temporary-file fix does not address this remote
storage ownership problem.

### F03 P1 A console redirect can disclose the management session token

**Locations:** [HTTP.cs](../../XenModel/XenAPI/HTTP.cs), 254–260, 730–752 and
765–783. Shared custom HTTP transport, including both clients' hosted consoles.

The redirect handler opens the URL supplied by `Location` without rejecting an
HTTPS-to-HTTP downgrade or limiting the destination authority. `DoHttp` then
resends the original headers, including `Cookie: session_id=...`, to that new
connection. It also retains the original Host and request path.

**Reproduced with real loopback sockets:** an initial HTTPS server presented
the exact certificate accepted by the fixture's certificate policy, then
returned a 302 to `http://localhost:<different-port>/different-console`.
The plaintext listener received:

```http
CONNECT /original-console HTTP/1.0
Host: 127.0.0.1
Cookie: session_id=synthetic-review-session
```

This requires a redirect from the initially contacted endpoint or control of
its response; it does not independently defeat TLS authentication. Once the
redirect occurs, the forwarded token crosses a weaker transport and potentially
a different trust boundary. The shell's encrypted-transport check for text
paste happens after this credential has already been sent.

**Correction and regression coverage:** reject transport downgrades before
opening the next connection. Authorize any required pool-host redirects using
an explicit destination policy and validate their TLS identity before forwarding
credentials. Build each request from the accepted destination. Cover different
hosts, ports, schemes and paths, redirect loops, rejection without sending a
token, and supported coordinator redirects.

### F04 P1 PowerShell plugin arguments interpret object labels as code

**Locations:** [XenServerPowerShellCmd.cs](../../XenAdmin/Plugins/Cmd/XenServerPowerShellCmd.cs),
116–134 and 160–176; [PowerShellCmd.cs](../../XenAdmin/Plugins/Cmd/PowerShellCmd.cs),
69–78; [ExternalPluginAction.cs](../../XenAdmin/Actions/GUIActions/ExternalPluginAction.cs),
121–126. Client: WinForms, with command plugins enabled.

`ExtraArrayStatement` substitutes object properties into double-quoted
PowerShell source. Its escaping handles backslashes and quotation marks but
leaves dollar signs, subexpressions and PowerShell escapes active. A plugin
parameter such as `{$label}` can therefore turn a server-supplied VM name into
an expression executed on the administrator's workstation when the plugin runs.

**Reproduced:** calling the compiled WinForms method with a synthetic VM label
`$(21+21)` and parameter `{$label}` generated exactly:

```powershell
$ParamArray=@("$(21+21)");
```

Executing this generated array initialization and reading its first value
returned `42`, rather than the literal label. Only this harmless arithmetic
fixture was executed. PowerShell documents evaluation inside double-quoted
strings in its [quoting rules](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_quoting_rules).

**Conditions:** plugins are disabled by default. Exposure requires an enabled
PowerShell command plugin using a substituted property and a user invoking that
command for the affected object. The archived IE tab implementation is not
needed for this path.

**Correction and regression coverage:** pass substituted values as data through
structured parameters or a serialized input channel. Where source literals
remain necessary, use a correct literal encoding for PowerShell. Cover dollar
signs, subexpressions, backticks, quotes and line breaks through actual process
argument construction, including both PowerShell command classes.

### F05 P2 Snapshot Revert and Delete bypass confirmation

**Locations:** [MainWindow.axaml](../../XcpNgCenter.Shell/Views/MainWindow.axaml),
1230–1244; [MainViewModel.Actions.cs](../../XcpNgCenter.Shell/ViewModels/MainViewModel.Actions.cs),
1389–1420. Client: Avalonia shell.

Each snapshot row binds its Revert and Delete buttons directly to synchronous
commands that resolve the snapshot and start the shared action. There is no
confirmation showing the affected guest, loss of current state, or irreversible
deletion. A single click can discard current guest state or delete a recovery
point. The shared revert action can also restore the snapshot's running state.

**Verified:** invoking each production command with synthetic cached objects
immediately reached construction of `VMSnapshotRevertAction` or
`VMSnapshotDeleteAction`. The probe stopped each constructor through the action
notification before a worker or RPC could run. The direct button bindings and
absence of a prompt were checked in source. The WinForms
[revert command](../../XenAdmin/Commands/RevertToSnapshotCommand.cs), 97–131,
already confirms and offers a safety snapshot; its
[delete command](../../XenAdmin/Commands/DeleteSnapshotCommand.cs), 95–115,
also requires confirmation.

**Correction and regression coverage:** add explicit confirmation with the VM
and snapshot identities, and revalidate those identities and current operation
eligibility after the dialog closes. Offer a safety snapshot where supported.
Test cancellation without constructing an action, accepted execution exactly
once, stale/deleted snapshots, and connection or selection changes while the
prompt is open.

### F06 P2 XVA verification ignores missing checksum-table blocks

**Locations:** [export.cs](../../CommandLib/export.cs), 146–160 and 252–256.
Caller: [ExportVmAction.cs](../../XenModel/Actions/VM/ExportVmAction.cs), 180
and 254. Shared XVA export verification.

`compare_tables` iterates the checksums recomputed from encountered data blocks.
It never requires every entry in the archive's `checksum.xml` table to have a
corresponding block. An archive can have a valid end marker, internally correct
remaining blocks, and a declared block missing entirely, yet pass verification.

**Reproduced through the compiled verifier:** a complete two-block archive was
accepted; a second archive with the same two-entry checksum table but only one
payload block was also accepted. A control with a present but modified block
correctly raised `BlockChecksumFailed`. This isolates missing coverage from an
ordinary checksum mismatch. No export or restore from a real VM was performed.

**Correction and regression coverage:** when a checksum table is present,
require equality of the declared and encountered block sets, then compare their
digests. Preserve deliberately supported legacy formats with explicit tests.
Cover missing, extra, duplicate and altered blocks, and both complete and
incomplete archives. Treat a successful checksum check as distinct from a
successful full restore rehearsal.

### F07 P2 Replacing a support ZIP retains the old report

**Location:** [ZipStatusReportAction.cs](../../XenModel/Actions/StatusReport/ZipStatusReportAction.cs),
190–193. Shared status-report packaging, exposed by WinForms.

The ZIP output uses `File.OpenWrite`, which does not truncate an existing file.
If the replacement archive is smaller, old bytes and the old ZIP directory can
remain after the newly written archive. Readers can select the previous
directory and display the old report. Data from a prior support collection also
remains in a file the user expects to contain only the new collection.

**Reproduced using the actual private packaging method:** a 100,152-byte ZIP
containing `old-report.txt` was replaced with a much smaller report containing
`new-report.txt`. The output stayed at 100,152 bytes. A standard ZIP reader
listed `old-report.txt`, and a sentinel from the old payload remained in the
output bytes. The fixture used synthetic report text only.

**Correction and regression coverage:** create a fresh archive in a temporary
file on the destination volume, finish and validate it, then replace the target.
Cover replacing a larger report, cancellation, and packaging failure. Assert
both the new entry set and the absence of previous report bytes.

### F08 P2 OVF compression deletes its source before committing the result

**Location:** [Compression.cs](../../XenOvfApi/Compression.cs), 93–115.
Caller: [ExportApplianceAction.cs](../../XenModel/Actions/OvfActions/ExportApplianceAction.cs),
152–158. Shared compressed OVF export.

After writing a temporary compressed file, `ProcessCompression` deletes the
source and then moves the result to its final name. If that final name already
exists, the move fails. The `finally` block deletes the temporary result too,
leaving neither the source nor its completed replacement.

**Reproduced:** with synthetic `disk.iso` and a preexisting `disk.iso.gz`, calling
`OVF.CompressFiles` raised `IOException`. `disk.iso` was gone, the temporary
compressed file was gone, and only the preexisting destination remained. This
destroys the local input to the transformation. In the application's export
flow that input is an exported working disk; this probe does not establish
deletion of the VM's storage.

**Correction and regression coverage:** establish the destination/overwrite
policy before processing and preserve the source until the replacement is
committed successfully. Cover existing destinations, rename failure,
cancellation and metadata-save failure. Consider the same commit ordering in
the decompression branch, which shares this implementation.

### F09 P2 WinForms main-password storage has a cheap offline guessing path

**Locations:** [Settings.cs](../../XenAdmin/Settings.cs), 161 and 518–532;
[EncryptionUtils.cs](../../XenCenterLib/EncryptionUtils.cs), 69–79, 126–145
and 166–182. Client: WinForms, with saved sessions protected by a main password.

WinForms derives `Program.MainPassword` with one SHA-256 operation over the
UTF-16 password and uses those bytes directly as the AES-CBC key. The random
value stored with the ciphertext is the IV; it does not salt or stretch password
derivation. A copied encrypted session file therefore permits a cheap offline
test for each password guess. This protection mode does not additionally wrap
the credential entry with DPAPI. The ciphertext also has no authentication tag.

**Verified with synthetic data:** the current encryption helper's output was
decrypted using `SHA256(UTF16(password))` and the persisted IV. This establishes
the per-guess derivation and storage format; it is not a claim that an unknown
strong password was recovered. The earlier shell `mp1` issue is separate:
current shell `mp2` storage already uses a salted, iterated derivation and
authenticated encryption.

**Correction and regression coverage:** introduce a versioned WinForms format
using a password KDF with a random salt and a bounded work factor, plus
authenticated encryption. The runtime provides
[PBKDF2 with explicit salt and iterations](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2?view=net-10.0).
Migrate only after successful unlock, preserve recovery on cancellation or write
failure, and test wrong passwords, tampering, legacy reads and transactional
migration. Keep the existing shell format compatible with its own profile.

## Repository coverage

The snapshot contains **2,791 tracked files**, including **1,868 C# files**,
304 C# designer files, 299 resource files and 44 Avalonia views. Review covered
every active project and the repository's build/release infrastructure, with
deeper tracing and runtime probes at storage, credential, transport and
destructive-action boundaries. Generated API bindings, designers, schemas and
resources received structural checks, representative inspection and build/test
validation; this is not a claim of individual manual inspection of every
generated line or a formal security audit.

- **XenAdmin:** startup/configuration, saved-session protection, TLS policy,
  console integration, snapshot commands, retained command plugins, resource and
  designer compatibility. F04, F07 and F09 affect this supported Windows client;
  shared findings also apply where its workflows call the affected libraries.
- **XcpNgCenter.Shell:** bootstrap/settings, main-password vault, update
  acquisition/installation, inventory scheduling/tree reuse, RFB sessions and
  paste, graphs, VM editors/actions, network configuration, HA, AD/RBAC and DR
  review/execution paths. F01 and F05 are shell-specific.
- **XenModel:** shared connection/session ownership, heartbeat and RPC
  transports, transfer HTTP, administrative actions, import/export and cleanup.
  F02 and F03 are shared boundary defects.
- **XenOvfApi:** descriptor/reference resolution, package and manifest checks,
  transformation paths, and disk lookup. F08 remains in export compression.
- **XenCenterLib:** archives, local path validation, encryption, hashing, TLS and
  cancellation helpers. Its legacy encryption contract participates in F09.
- **CommandLib:** XVA framing/checksum verification and stream reads. F06 is in
  its checksum coverage check.
- **XcpNgCenter.Rfb:** protocol setup, buffered reads, encodings, input writes,
  framebuffer handoff and console lifecycle integration. Existing regressions
  passed; no additional independently reproduced finding is asserted here.
- **Tests and tools:** both required test projects, WinForms compatibility
  probes, UI acceptance tools, failure-recording helpers and package smoke
  checks. The nine focused review probes demonstrate gaps beyond the passing
  baseline suite.
- **Build, packaging and CI:** SDK/central package pins, portable lockfiles,
  Windows/Linux workflows, manual release channel selection, package layout,
  bundled-runtime checks, notices and release-note scripts. No new confirmed
  workflow failure was found in this pass.
- **Documentation, branding, installer and legacy assets:** checked current
  support boundaries and acceptance instructions against the projects and
  workflows. The archived IE plugin tabs remain excluded from active builds.
  Historical installer/branding scripts are not the supported SDK build path.

## Earlier review findings

The [September initial review](2026-09-07-initial-review.md) describes a historical
snapshot. Its thirteen findings were checked against the current implementation
and [remediation record](2026-09-07-remediation.md), rather than copied into this
report as open issues.

- The shell vault now separates persisted verification metadata from its
  session-only encryption key and uses the `mp2` format.
- Import's local files use owned temporary paths and contained references;
  folder manifests require payload/descriptor coverage, and disk-file resolution
  compares complete identifiers. F02 concerns remote VDI ownership, and F06
  concerns the separate XVA verifier.
- The updater launches installed bootstrap code and authenticates fresh release
  metadata and a copied archive before replacement code is launched. Existing
  certificate pins are checked before the normal chain-trust fast path.
- RPC logging, graph resolution/history, background appliance loading, pending
  ISO selection and console retry scheduling have their reviewed corrections
  and regression coverage in the current tree.
- CI runs the required suites separately and retains distinct result files, so
  a later successful test command does not mask an earlier suite's failure.

The complete original external proof harnesses were not all rerun. Current
source inspection and the fresh full acceptance run support the dispositions
above; they do not replace the manual acceptance still recorded in the handoff.

## Baseline validation and evidence

Validation ran on Windows `10.0.26300` with SDK **10.0.402** and runtime
**10.0.13**, against the clean reviewed commit before writing this report.

- Release and Debug solution builds: zero warnings and errors.
- `XcpNgCenter.Shell.Tests`: **1,105 passed in Release and 1,105 in Debug**.
- `XenCenterLib.Tests`: **113 passed on net10.0 and 112 on net481**.
- All **27** automated acceptance checks passed, including locked restore,
  script fixtures, 32,138 WinForms resources across 289 sets per configuration,
  349 lifecycle/designer checks per configuration, 23 plugin-archive checks per
  configuration, 12 proxy-authentication cases, six UI probe modes and a freshly
  published self-contained Windows package smoke check.
- Portable lockfiles remained unchanged. Existing trusted RDP interop DLLs were
  reused with `SkipRdpAxImp`; this pass did not regenerate them with AxImp.
- `dotnet list XenAdmin.sln package --vulnerable --include-transitive
  --no-restore --format json` completed successfully and reported no vulnerable
  package entries across the nine solution projects using nuget.org's advisory
  feed. This does not audit every bundled native binary or custom parser.
- **Nine focused review probes confirmed the behaviors described above.** They
  used freshly built assemblies, disposable files, synthetic cached objects and
  loopback servers. The PowerShell reproduction executed only `21+21`.

Local evidence is retained under the ignored directory
`artifacts/repo-review-20261022/`:

- `acceptance/acceptance.json`: source commit, toolchain, checks and lockfile
  preservation; adjacent logs and four TRX files contain full results.
- `package-audit.json`: transitive NuGet advisory response.
- `probes/Program.cs` and `probes/ReviewProbes.csproj`: isolated reproduction
  source, referencing the built Release assemblies without changing application
  code.
- `probe-results.json` and `probe-run.log`: all nine probe outcomes, recorded
  destructive RPC targets, redirect headers and proposed memory action.
- `plugin-generated.ps1` and `plugin-execution.txt`: literal-parameter
  reproduction and observed arithmetic result.

Reproduction commands, using the existing portable SDK on this machine:

```powershell
$reviewSdk = "$env:LOCALAPPDATA/Temp/xenadmin-dotnet-security-20261014/dotnet"
$env:DOTNET_ROOT = $reviewSdk
$env:PATH = "$reviewSdk;$env:PATH"

# Use a fresh evidence directory for any acceptance rerun.
./scripts/Invoke-PlatformAcceptance.ps1 -DotnetPath "$reviewSdk/dotnet.exe" `
  -EvidenceDirectory artifacts/repo-review-rerun -SkipRdpAxImp

dotnet run --project artifacts/repo-review-20261022/probes/ReviewProbes.csproj -c Release
powershell.exe -NoProfile -File artifacts/repo-review-20261022/plugin-generated.ps1
```

The probe source and logs are local review evidence and are not tracked by Git.
These original probes expect the vulnerable behavior on the reviewed commit;
run the permanent regression suites above against the corrected tree instead.

## Original correction order and remaining limits

Address F01 and F02 first because an ordinary Properties save or failed import
can change guest availability or destroy existing data. Correct F03's transport
policy and F04's plugin argument encoding next, followed by F05's snapshot
confirmation and identity checks. F06–F08 belong together in a backup/export
integrity pass. F09 needs a separately reviewed credential migration.

The strongest recurring gaps are **ownership during cleanup**, **preservation
of fields the user did not edit**, and **verification at the final execution
boundary**. Add the proposed regressions at the production editor/action or
stream boundary; tests of detached policy helpers would miss several of these
failures. Keep changes scoped to the responsible components so shared fixes
retain compatibility with both clients and both library target frameworks.

Large view models and the updater still combine several responsibilities.
Extract testable planning and ownership decisions as the findings are fixed;
the evidence does not justify a broad rewrite of the working application.

This Windows pass did not run native Linux execution, real pool mutations,
physical desktop/scaling acceptance, recovery/failover rehearsals, an actual
UAC update/rollback/restart, or Visual Studio designer save/reopen. Prior hosted
Linux and live-release statements in the handoff remain dated evidence. The
reported guest reboot hang remains undiagnosed; F01 establishes a separate
Properties-triggered restart path, not its cause. Signing and installer
modernization remain previously deferred work. The original review phase changed documentation only. The subsequent
verification/fix commits are listed above and prepared for review against
`development`; no release or deployment was made.
