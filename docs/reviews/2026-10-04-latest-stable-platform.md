# Latest stable platform migration

This follows the unsigned beta from [PR #63](https://github.com/Narehood/xenadmin/pull/63),
source `c9911533c36745cd6f471a45633b05fcbaecb1ee`. The user reported that the beta
seems to work and requested continued migration to current stable versions.
That feedback does not complete the outstanding manual acceptance checklist.
The published beta is unchanged; this work is on `modernization/latest-stable-platform`.

Implementation: `afcff7dd1ce252357fe22b1f9df5d91f9ecb519d`.
[PR #64](https://github.com/Narehood/xenadmin/pull/64) targets `development` and
depends on PR #63. Its focused
[implementation diff](https://github.com/Narehood/xenadmin/compare/c9911533c36745cd6f471a45633b05fcbaecb1ee...1d996b5079680224c41f39d80c9a9bf6ae69fa93)
excludes the parent's already reviewed changes. Merge the parent first; the
follow-up's overall PR diff then reduces to this migration.

## Dependency scope

Versions were checked against the NuGet stable feed and upstream release metadata
on 2026-10-04. Central package versions, application/test/probe lockfiles, and
the redistribution notice bundle are refreshed together.

| Component | Previous | Current |
| --- | --- | --- |
| Avalonia application packages | 11.3.22 | 12.1.3 |
| SkiaSharp and native assets | 2.88.9 | 4.153.1 |
| HarfBuzzSharp and native assets | 8.3.1.1 | 14.2.1.301 |
| Tmds.DBus.Protocol | 0.21.3 | 0.95.1 |
| xUnit | `xunit` 2.9.3 | `xunit.v3.mtp-off` 4.0.1 |
| Artifact download action | v7 | v8 (current release 8.0.1) |
| GitHub release action | v2 | v3 (current release 3.0.3) |

[Avalonia 12.1.3](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.3)
supports the shell's existing .NET 10 target. Its
[migration guide](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)
informs the focus, decoration, clipboard, placeholder, text-shaping and dispatcher
changes below. [SkiaSharp 4.153.1](https://github.com/mono/SkiaSharp/releases/tag/v4.153.1)
and the corresponding native assets are centrally pinned together; this is a
tested renderer upgrade above Avalonia's minimum dependency, not an upstream
guarantee about every graphics driver. The
[D-Bus package](https://www.nuget.org/packages/Tmds.DBus.Protocol/0.95.1)
matches the newer API line consumed by Avalonia 12.

The new [xUnit release](https://xunit.net/releases/v3/4.0.1) is product xUnit v3
with package version 4.0.1. The
[MTP-off variant](https://xunit.net/releases/v3/4.0.0) retains existing VSTest,
TRX, and multi-framework test commands on .NET 10. Both test projects are now
executables. Cancellation-aware test calls use the runner's cancellation token;
tests that assert cancellation keep their own explicit tokens. Windows-only
attributes preserve caller file/line metadata and their existing skip rules.

Framework compatibility dependencies are centrally pinned to their current
stable releases: Microsoft.Bcl.AsyncInterfaces, System.Collections.Immutable,
System.IO.Pipelines and System.Reflection.Metadata 10.0.12; System.Buffers and
System.Numerics.Vectors 4.6.1; System.Memory 4.6.3;
System.Runtime.CompilerServices.Unsafe 6.1.2; System.Threading.Tasks.Extensions
4.6.3; System.ValueTuple 4.6.2. The test platform object model is 18.10.1.
Shared libraries retain `net481;net10.0` and their compatibility API paths.

The SDK 10.0.401, runtime/System packages 10.0.12, log4net 3.5.0, JSON/archive
libraries, CommunityToolkit.Mvvm 8.4.2 and other dependencies already match their
latest stable versions. Older version numbers for DiscUtils, lzo.net and
lzfse-net reflect the current upstream releases, not missed updates.
The [.NET 10 feed](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
was checked without enabling preview versions. The publication actions use the
current [download action](https://github.com/actions/download-artifact/releases/tag/v8.0.1)
and [release action](https://github.com/softprops/action-gh-release/releases/tag/v3.0.3)
API lines on hosted runners.

## Migration and regression coverage

- Console focus callbacks use `FocusChangedEventArgs`; releasing input uses
  `Focus(null)`. The update button retains tab-navigation behavior.
- Splash decoration uses `WindowDecorations`, clipboard text uses the extension
  namespace, and all 81 textbox placeholders use `PlaceholderText`.
- The standalone Skia probe explicitly configures HarfBuzz. The rendering tests
  use Avalonia's public `HeadlessUnitTestSession` with real Skia rendering,
  isolated application/dispatcher state per test, and async fixture disposal.
  Existing pixel, clipping, allocation, inventory and layout assertions remain.
- UI probe screenshots explicitly use `PngBitmapEncoderOptions.Default` instead
  of the obsolete save overload. The rebuilt beta/legal settings probe passes
  all 24 checks and writes its rendered PNG evidence without build warnings.
- Avalonia.Headless.XUnit 12.1.3 targets the xUnit 3.2.2 discovery ABI and raises
  `MissingMethodException` with xUnit 4.0.1. The public
  [headless session API](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)
  avoids that incompatible adapter while retaining both current stable libraries.
  Only test projects reference Avalonia.Headless; application packages exclude it.
- The exact notice bundle covers 50 application packages, with full upstream
  license/NOTICE text retained. Avalonia, MicroCom and D-Bus fallback sources use
  immutable commits from package metadata. Notice fixtures retain their name and
  version prefix rejection checks against the new identities.

The initial full run exposed missing text-shaping setup; the second exposed
dispatcher ownership in the old rendering fixture. Both failed-before cases
are recorded under `artifacts/latest-stable-complete-20261004` and its `-r2`
directory. All 43 focused rendering cases pass after migration in
`artifacts/latest-stable-headless-session-tests-r2.log`.

## Acceptance and remaining work

All 27 local automated checks pass in
`artifacts/latest-stable-complete-20261004-r3`, with portable locks preserved.
The manifest records `c9911533c` plus the migration working tree that became
`afcff7dd1`. The final probe-only PNG API adjustment is verified separately in
`artifacts/latest-stable-png-api-20261004` and its `-r2.log`.

- Full solution Release/Debug builds: zero warnings or errors.
- Shell Release/Debug: 1,056 tests each; shared .NET 10: 113; Framework: 112.
  No failures or skips.
- WinForms: all 32,138 resources in 289 sets, 349 lifecycle/designer and 23 plugin
  archive checks per configuration; 12 loopback proxy authentication checks.
- All six Windows UI modes pass: 26 networking, four connection settings,
  24 beta/legal, 98 access/recovery, 378 modernization and 12 RDP checks.
- All 13 redistribution, seven release-note and nine performance-report fixtures.
- Fresh self-contained Windows package passes exact legal identities, runtime,
  hostile startup-hook boundary, helper rejection and desktop startup checks.
  Archive SHA-256:
  `e100f3de872775005142958e7788cbb27a27e079dfaa2879158b39392e70c80e`.

Final solution audits report no outdated stable packages and no known NuGet
vulnerabilities, including transitive packages, in
`artifacts/latest-stable-outdated-final.json` and
`artifacts/latest-stable-vulnerable-final.json`. The three probe graphs also have
no outdated packages (`latest-stable-outdated-*.json`). This is dated feed evidence,
not a guarantee against future advisories. Actionlint 1.7.12 validates the
workflows; external shellcheck/pyflakes integrations were disabled.
Hosted Windows/Linux and CodeQL checks run on the upgrade PR; their current-head
results, including native Linux package execution, are required before merge.

The first [hosted Linux run](https://github.com/Narehood/xenadmin/actions/runs/37212380398)
passed both shell configurations (1,050 tests plus three intentional Windows
skips each), then failed the shared locked restore with NU1004. The SDK infers
`win-x86` for Framework executables only on Windows. Commit `1d996b507` explicitly
includes that RID in `RuntimeIdentifiers` on every host, keeping the restore
graph portable and retaining the existing AnyCPU executable behavior. Both
shared suites pass again locally, with the checked-in lockfile unchanged:
`artifacts/latest-stable-rid-fix-r2`. Fresh native Linux CI verifies the fix.

CodeRabbit skipped its initial review because the combined unmerged-parent diff
has 164 files (its limit is 100), and it also reported unavailable review
capacity. Its success status does not establish completed review. Cursor review
and all new findings are tracked on PR #64.

Physical Windows/Linux desktops and display scaling, actual installation and
updater/UAC/rollback/restart, native RDP/guest reboot, and live-pool performance,
HA, AD and DR acceptance remain pending. There is no disposable pool in this
workspace. Trusted existing RDP interop is reused locally; hosted Windows builds
regenerate it. Signing and installer redesign remain deferred. No release is
published by this migration PR.
