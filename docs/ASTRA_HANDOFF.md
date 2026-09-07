# Astra / Astro handoff

Last updated: 2026-09-07. Initial review followed by a remediation pass.

## Start here

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

Install the first fixed release manually on older deployed shells: their
existing updater cannot acquire the new bootstrap safely by changing only the
package it downloads. For custom repositories, administrator/elevated automatic
installation is disabled with release-page guidance before UAC; use a manual
installation for those builds.

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
