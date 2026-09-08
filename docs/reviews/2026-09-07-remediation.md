# Review remediation — 2026-09-07

This records source changes made after the
[initial review](2026-09-07-initial-review.md) of `development` at
`b01829b8e25c02d48cce7e3cb64897d404bd72cb`. That review remains a historical
snapshot. All thirteen findings have corresponding source changes and the
regression coverage described below. The current automated test suites pass as
recorded below; this document does not claim full desktop or installation
validation. The source changes land on `development` as
`0562401744055ecafd704776db8f0fcea8c69d46`.

The user's existing portable-package layout changes in the publishing workflow,
updater, tests, and `XcpNgCenter.Shell/packaging/` were preserved and incorporated.

## Changes and regression coverage

All test links below refer to `XcpNgCenter.Shell.Tests`. These tests can exercise
shared `XenModel`/`XenOvfApi` behavior without building the WinForms RDP surface.
The existing `XenCenterLib.Tests` suite remains required on both target frameworks.

| Finding | Source change | Regression evidence |
| --- | --- | --- |
| R01: persisted main-password key | [MainPasswordProtection](../../XcpNgCenter.Shell/Services/MainPasswordProtection.cs) derives a key using salted PBKDF2-SHA256, separates its verifier from the encryption key, and encrypts passwords using AES-GCM. [MainPasswordVault](../../XcpNgCenter.Shell/Services/MainPasswordVault.cs) owns unlock, migration, and session-key lifetime. Settings/dialog/reconnect paths use the vault. | [MainPasswordVaultTests](../../XcpNgCenter.Shell.Tests/MainPasswordVaultTests.cs): metadata-only decryption attempt, random salts/nonces, ciphertext/tag/nonce tampering, wrong password, enable/change/disable/restart, legacy upgrade, cancellation, failed decryption/commit, stale settings, and concurrent vault writers. |
| R02: import deletes files it does not own | [ImportTemporaryFiles](../../XenModel/Actions/OvfActions/ImportTemporaryFiles.cs) creates unique files with `CreateNew` in the canonical appliance directory and tracks successful creations. [Import](../../XenModel/Actions/OvfActions/Import.cs) cleans only those files, including multiple transformation intermediates. Gzip decompression consumes decrypted output when both transformations apply. | [OvfImportSecurityTests](../../XcpNgCenter.Shell.Tests/OvfImportSecurityTests.cs): original `enc_`/`unc_` files survive failures, a preexisting `unc_` file survives decompression failure, encrypted/compressed ISO transformation succeeds without a host call, and cancellation cleans owned files while preserving the source. |
| R03: local OVF path escape | [OvfFilePath](../../XenOvfApi/OvfFilePath.cs) resolves references inside the appliance directory, rejects rooted/URI/alternate-stream paths, Windows trailing-dot/space aliases, and existing symbolic links/reparse points. Folder reads, existence checks, manifest verification, and disk import use it. | [OvfImportSecurityTests](../../XcpNgCenter.Shell.Tests/OvfImportSecurityTests.cs): traversal with either separator, rooted paths, URI/alternate-stream syntax, Windows aliases, nested valid files, linked directories, and linked package roots. |
| R04: mutable cached updater execution | [ShellUpdateInstaller.Bootstrap](../../XcpNgCenter.Shell/Services/ShellUpdateInstaller.Bootstrap.cs) starts from installed code, fetches fresh release metadata, copies and verifies the archive before extracting a new launch payload, and uses administrator-owned protected staging after elevation. Cached executables and local manifests do not authorize execution. | [ShellUpdateBootstrapTests](../../XcpNgCenter.Shell.Tests/ShellUpdateBootstrapTests.cs) and [ShellUpdateTests](../../XcpNgCenter.Shell.Tests/ShellUpdateTests.cs): installed bootstrap path, substituted cached EXE/dependency/manifest, archive tampering, missing/malformed digest, wrong assembly version, existing launch-file collision, invalid headless apply arguments, and protected-staging ACL descriptor. |
| R05: CA-valid certificate bypasses pin | [Shell validator](../../XcpNgCenter.Shell/Services/TofuCertificateValidator.cs) and [WinForms SSL callback](../../XenAdmin/Network/SSL.cs) check existing pins before the trusted-chain fast path. An unpinned CA-valid host uses OS trust without creating a pin; changes follow configured warning/re-pin policy. Missing certificates fail closed. | [CertificatePinTests](../../XcpNgCenter.Shell.Tests/CertificatePinTests.cs): matching and changed pins with trusted/untrusted chains, shell accept/reject decisions, unpinned CA trust, and missing certificates. WinForms tests compile-link the actual `SSL.cs`, with minimal settings/UI stubs. |
| R06: incomplete folder manifest | [Package verification](../../XenOvfApi/Package.cs) requires the descriptor and every referenced payload, including ordinary `.mf`/`.cert` resources. Folder checks resolve listed paths and reject canonical duplicates; OVA checks skip only the descriptor's actual sibling manifest/certificate. Validation rejects references to those actual metadata files, following [DSP0243 2.0.1 §5.1](https://www.dmtf.org/sites/default/files/standards/documents/DSP0243_2.0.1.pdf). | [OvfImportSecurityTests](../../XcpNgCenter.Shell.Tests/OvfImportSecurityTests.cs): empty/partial/complete/tampered manifests; folder duplicate/outside paths; ordinary `.mf`/`.cert` coverage, tampering and missing files in folder/OVA packages; invalid actual-metadata references; OVA metadata preceding its descriptor. |
| R07: partial disk/file ID matching | [OVF helpers](../../XenOvfApi/OVF.cs) compare complete disk and file IDs, accepting standard `ovf:/disk/<id>` references and bare legacy IDs. [Validation](../../XenOvfApi/Validation.cs) rejects empty or duplicate IDs. Related disk lookup/update/bootability helpers use the same exact matching. | [OvfImportSecurityTests](../../XcpNgCenter.Shell.Tests/OvfImportSecurityTests.cs): `disk1`/`disk10` and `file1`/`file10` collisions, ordering changes, supported reference forms, unsupported prefixes, and duplicate IDs. |
| R08: request diagnostics expose credentials | [JsonRpc.RequestEvent](../../XenModel/XenAPI/JsonRpc.cs) passes only the method name in every build configuration. [Session](../../XenModel/XenAPI-Extensions/Session.cs) logs that name without request parameters. Debug no longer serializes a second complete request for logging. | [JsonRpcLoggingTests](../../XcpNgCenter.Shell.Tests/JsonRpcLoggingTests.cs): a synthetic loopback JSON-RPC request and captured log4net output verify positional credentials do not enter request diagnostics. CI runs the shell suite in both Debug and Release. |
| R09: coarse graph archives lose history | [ShellRrdMaintainer](../../XcpNgCenter.Shell/Services/Performance/ShellRrdMaintainer.cs) requests each archive's own resolution and uses its latest successful sample as the incremental cursor. Failed fetches do not reuse previous responses or advance that cursor; disposed pollers do not publish queued updates. | [PerformancePollingTests](../../XcpNgCenter.Shell.Tests/PerformancePollingTests.cs): archive resolution, retained history, duplicate handling, failed-fetch cursor behavior, and disposal. |
| R10: synchronous OVA browse scan | [OvfPackageLoader](../../XcpNgCenter.Shell/Services/OvfPackageLoader.cs) runs metadata/validation work on a worker. [OvfImportViewModel](../../XcpNgCenter.Shell/ViewModels/OvfImportViewModel.cs) cancels replaced/closed loads and rejects stale results. [CancellableReadStream](../../XenOvfApi/CancellableReadStream.cs) lets archive traversal observe cancellation while consuming skipped content. | [OvfPackageLoadingTests](../../XcpNgCenter.Shell.Tests/OvfPackageLoadingTests.cs): worker execution, cancellation, valid/invalid packages, disposal and stale-result rejection, and archive metadata cancellation. |
| R11: inventory resets pending ISO choice | [ConsoleIsoSelection](../../XcpNgCenter.Shell/Services/ConsoleIsoSelection.cs), called by [MainViewModel.Actions](../../XcpNgCenter.Shell/ViewModels/MainViewModel.Actions.cs), preserves the pending VDI reference for the same VM/connection when refreshing options. Selection resets when its VM/connection changes, the ISO disappears, or insertion finishes. | [ConsoleIsoSelectionTests](../../XcpNgCenter.Shell.Tests/ConsoleIsoSelectionTests.cs): pending choice survives refreshed option objects, removed ISO falls back to the attached disk, and VM/connection changes reset the pending choice. |
| R12: cooldown loses the only console retry | [ConsoleRetryScheduler](../../XcpNgCenter.Shell/Services/ConsoleRetryScheduler.cs) schedules a delayed cancellable attempt and invalidates queued callbacks on cancellation/disposal. [MainViewModel](../../XcpNgCenter.Shell/ViewModels/MainViewModel.cs) uses it for guest consoles while preserving the host control-domain no-retry policy. | [ConsoleRetrySchedulerTests](../../XcpNgCenter.Shell.Tests/ConsoleRetrySchedulerTests.cs): repeated rapid failures retain another attempt, duplicate scheduling is coalesced, queued callbacks become invalid after cancellation, and disposal stops retries. Existing [ConsoleSessionSyncPolicyTests](../../XcpNgCenter.Shell.Tests/ConsoleSessionSyncPolicyTests.cs) retain guest/host policy coverage. |
| R13: CI hides the first failed test command | [test-builds.yml](../../.github/workflows/test-builds.yml) gives shared `net481`, shared `net8.0`, shell Release, and shell Debug separate steps and distinct TRX names. Each step's native exit status now stands independently. | Workflow source inspection; local suite results are recorded separately below. A dispatched GitHub Actions failure-injection run has not been performed. |

Additional R04 coverage checks invalid restart-broker contexts, a bounded wait
for acknowledgement, late acknowledgement, absent acknowledgement, and dead or
wrong broker processes. It also verifies that a preparation failure before the
helper starts removes partial launch files without deleting the downloaded
archive or changing the installation. These cases are in
[ShellUpdateBootstrapTests](../../XcpNgCenter.Shell.Tests/ShellUpdateBootstrapTests.cs)
and [ShellUpdateRestartBrokerTests](../../XcpNgCenter.Shell.Tests/ShellUpdateRestartBrokerTests.cs).

[ShellUpdateMachineTrustTests](../../XcpNgCenter.Shell.Tests/ShellUpdateMachineTrustTests.cs)
exercise the elevated metadata TLS callback using an in-memory custom-root
chain. No certificate stores are modified. The tests reject an untrusted root
even when the supplied/default chain reports success, and cover hostname errors,
missing certificates, and inappropriate certificate usage. Elevated Windows
metadata requests rebuild trust in machine context while preserving the
transport's hostname checks and revocation policy.

Related small corrections remove a duplicate selected-server detail refresh and
dispose the previous framebuffer bitmap when its dimensions change. They do not
establish performance gains under real console or large-pool workloads.

## Credential migration and recovery

`saved-servers.json` becomes a version-2 document containing KDF metadata and all
encrypted entries. PBKDF2-SHA256 uses a random 32-byte salt and 600,000 iterations;
domain-separated HMAC outputs provide the verifier and AES key. New `mp2:` blobs
use AES-GCM with random nonces. The unlocked key remains in session memory and is
zeroed when cleared. The main view model no longer retains the plaintext main
password for the session. Password derivation runs off the UI thread.

An old array/`mp1:` file is migrated after a successful main-password unlock.
Migration decrypts and prepares every entry before replacing the credential file
atomically; it never silently drops an entry that cannot be decrypted. The new
document is authoritative if the process exits before obsolete
`app-settings.json` metadata is cleared. Cleanup retries on restart. Failed or
cancelled writes leave the previous credential file usable. An exclusive lock
file and a comparison against the current protection metadata prevent another
updated app instance from writing ciphertext under a stale key.

This is a one-way format upgrade: older shell builds cannot read the new
document and must not write the same profile after migration. Existing copied
`mp1:` credential/settings pairs or backups remain vulnerable. Legacy CBC data
cannot be authenticated retroactively. Normal device protection remains DPAPI on
Windows or a per-user AES key file on Unix; new Unix key files are private at
creation, and malformed device keys are not replaced with new keys.

## Update trust and remaining runtime work

The user-writable download cache is an input cache. Its availability check is
not installation authorization. The installed bootstrap runs before UI and
user-configured TLS initialization, fetches the exact stable release again, and
takes the asset name, size, and SHA-256 digest from that response. After UAC
elevation it uses the built-in `Narehood/xenadmin` publisher rather than an
inherited repository override. Its Windows HTTPS callback rebuilds the release
server's certificate chain with `X509Chain(true)` in machine context, with system
trust and TLS server-authentication policy. A successful supplied chain does not
bypass that rebuild. Hostname checks and the transport's revocation policy remain
in force; supplied intermediate certificates do not become trust anchors. The
[.NET 8 Windows chain implementation](https://raw.githubusercontent.com/dotnet/runtime/v8.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/X509Certificates/ChainPal.Windows.BuildChain.cs)
selects the machine chain engine for this mode. Ordinary update checks retain
their existing TLS behavior. The bootstrap verifies a newly copied archive in
protected staging before extracting or launching replacement code.

Protected Windows staging is under `Program Files`, with an administrative
owner and a DACL allowing user read/execute but no user modification. The
bootstrap also requires the elevated token's default owner to be Administrators
or System, so the user's unelevated token cannot change child-file permissions
using owner rights. Other owner configurations fail safely. This
check accounts for Windows owners' implicit DACL-edit permission and the token's
role in choosing a new object's owner; see Microsoft's
[object ownership documentation](https://learn.microsoft.com/en-us/windows/win32/secauthz/owner-of-a-new-object).
The apply helper and unelevated restart broker use the verified payload; existing rollback
behavior is retained. Cancelling UAC retains the download for another attempt,
which performs verification again. Non-elevated portable updates retain the
configured repository behavior and require a writable installation directory.
Installation now requires access to fresh GitHub release metadata.

Automatic installation of a custom-repository release is unavailable when
administrator privileges are needed or the app is already elevated. The shell
explains this before requesting UAC and directs the user to the release page for
manual installation. Before an apply helper starts, failed preparation removes
partial launch files and retains a small error marker. Once a helper starts,
failure cleanup preserves its files and any rollback data.

**Transition from older deployed shells:** manually install the first fixed
release to acquire the trusted-bootstrap design. An older running shell still
uses its own vulnerable updater code; publishing a fixed package cannot repair
that old installation path retroactively. Subsequent updates can use the new
bootstrap after its Windows installation behavior has been exercised.

This does not license a package-layout change. The `2026.9.7.1` archives wrapped
the payload in an `XcpNgCenter.Shell/` folder, which every deployed updater
rejects with "The update package is missing required shell files" — the release
was undownloadable rather than merely untrusted. Published archives keep the
shell files at the archive root.

This trust design relies on the installed application, GitHub release/account
integrity, Windows machine trust for elevated metadata requests, and effective
staging ACLs. The tests inspect process
launch arguments, package preparation, and ACL descriptors; they do not exercise
a real UAC prompt, different-account elevation, permission enforcement across
Windows tokens, application replacement, rollback, or restart. Those remain
required Windows installation tests.

## Validation record and limits

Final current-source test results on the Windows development machine:

| Suite/configuration | Result |
| --- | --- |
| `XcpNgCenter.Shell.Tests`, Release, `net8.0` | 168 passed; zero skipped |
| `XcpNgCenter.Shell.Tests`, Debug, `net8.0` | 168 passed; zero skipped |
| `XenCenterLib.Tests`, `net481` | 73 passed |
| `XenCenterLib.Tests`, `net8.0` | 73 passed |

This is 482 passing test executions across configurations/frameworks. Both shell
configurations compile without warnings.
`XenModel` Release `net481`, including `XenOvfApi`, also builds with zero warnings
and zero errors. Self-contained Release publishes for `win-x64` and `linux-x64`
both succeed with zero warnings. Cross-publishing the Linux artifact does not
establish Linux runtime behavior.

A forced locked RID publish initially reported `NU1004`: the existing lockfiles
do not contain the requested runtime-specific dependency graphs. Publishing then
used the same unlocked restore mode as CI. Tracked lockfile bytes were restored
afterward, and the subsequent locked shell-test restore passed. No dependency
lockfile updates are included in this remediation.

The published Windows executable also passed a limited headless startup smoke
check. Its runtime configuration disables .NET startup hooks; with
`DOTNET_STARTUP_HOOKS` set to a deliberately missing DLL in the child environment,
four invalid updater modes each exited promptly with code 1 and displayed no UI.
This checks published startup/error handling, not UAC or a successful update.

A temporary .NET probe then invoked the published checker's
`GetPublishedAssetAsync(1900.1.1.0, true)` against GitHub. The Windows machine-context
TLS handshake succeeded and GitHub returned the expected HTTP 404 for that
deliberately nonexistent release. No package was downloaded, elevation requested,
or certificate store changed. This verifies the real HTTPS authentication path;
it does not exercise installation. The probe and `machine-tls-probe.log` remain
under the temporary review directory listed in the handoff.

- The full WinForms/solution build still needs the Windows SDK/Visual Studio
  `aximp.exe` and RDP interop prerequisites described in the
  [handoff](../ASTRA_HANDOFF.md).
- No live pool operations, real desktop soak, Linux runtime execution, or actual
  UAC/apply/restart installation was performed in this remediation pass.
- Import temporary files stay on the appliance volume, so transformations still
  require suitable free space and write permission there. Reparse checks happen
  before opening files; concurrent replacement by an untrusted local process is
  outside the tested containment guarantee.
- Checksum manifests establish completeness and integrity against the supplied
  digests; they are not publisher signatures.
- Vault crash tests simulate interrupted commit/cleanup and file-write failures.
  They do not establish durability under sudden power loss on every filesystem.
- Scheduling/selection tests use controlled callbacks and synthetic objects;
  real-host RRD behavior, large-appliance responsiveness, pending ISO interaction,
  and rapid console recovery still need desktop validation.

Keep this record and the handoff synchronized when final checks or a remediation
commit become available.
