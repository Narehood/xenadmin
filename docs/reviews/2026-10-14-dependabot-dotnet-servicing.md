# October 2026 .NET security servicing

Implementation: `429e4c328`, branch `security/dotnet-10-october-servicing`,
targeting `development`.

## Change

GitHub reported 18 open Dependabot alerts (#2–#19), all against implicit runtime
downloads in `XenCenterLib.Tests/XenCenterLib.Tests.csproj`. Every advisory affects
.NET 10.0.0 through 10.0.12 and identifies 10.0.13 as the first patched version.
The [official October release](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.13/10.0.13.md)
and [Microsoft release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
confirm that SDK 10.0.402 includes all three serviced runtimes.

`global.json` now requires SDK 10.0.402, retaining patch-only roll-forward and
excluding previews. Both validation jobs and both publication jobs already
read this pin. No direct runtime PackageReference is added: these runtime packs
are selected by the SDK. The shared test project's explicit Windows RID remains
in place so Windows and Linux restore the same portable dependency graph.

Eight central .NET System/compatibility pins advance to 10.0.13, along with their
transitive dependencies and ten affected portable lockfiles. Other package versions
and calendar project references are preserved. The generator, all 50 application
redistribution identities, bundled runtime notices, current notice fixtures and
active SDK instructions are refreshed. Dated migration/performance evidence
retains its original runtime version.

## Alert coverage

All alerts refer to the same manifest above. The actual restore assets select
exactly `[10.0.13, 10.0.13]` for each alerted runtime package; the host download is
also 10.0.13. Runtime names below abbreviate `Microsoft.*.App.Runtime.win-x86`.

| Alert | Runtime | CVE | Advisory |
| --- | --- | --- | --- |
| #2 | AspNetCore | CVE-2026-70175 | [GHSA-x3vp-85cm-q97r](https://github.com/advisories/GHSA-x3vp-85cm-q97r) |
| #3 | AspNetCore | CVE-2026-70181 | [GHSA-w5qq-qrpp-rf4c](https://github.com/advisories/GHSA-w5qq-qrpp-rf4c) |
| #4 | AspNetCore | CVE-2026-70196 | [GHSA-57xr-hfxx-jw5x](https://github.com/advisories/GHSA-57xr-hfxx-jw5x) |
| #5 | AspNetCore | CVE-2026-70183 | [GHSA-494h-xx7x-5frh](https://github.com/advisories/GHSA-494h-xx7x-5frh) |
| #6 | NETCore | CVE-2026-58280 | [GHSA-mc4q-93x5-f6hj](https://github.com/advisories/GHSA-mc4q-93x5-f6hj) |
| #7 | NETCore | CVE-2026-96233 | [GHSA-87vj-4hqv-22p9](https://github.com/advisories/GHSA-87vj-4hqv-22p9) |
| #8 | NETCore | CVE-2026-69651 | [GHSA-3rxq-g8rj-r2cf](https://github.com/advisories/GHSA-3rxq-g8rj-r2cf) |
| #9 | NETCore | CVE-2026-70197 | [GHSA-4mpw-cpwj-hxxj](https://github.com/advisories/GHSA-4mpw-cpwj-hxxj) |
| #10 | AspNetCore | CVE-2026-96236 | [GHSA-f4g7-52wc-p8x7](https://github.com/advisories/GHSA-f4g7-52wc-p8x7) |
| #11 | AspNetCore | CVE-2026-70188 | [GHSA-fpq8-4vhr-rcxx](https://github.com/advisories/GHSA-fpq8-4vhr-rcxx) |
| #12 | AspNetCore | CVE-2026-88092 | [GHSA-rv87-ccxw-8ggf](https://github.com/advisories/GHSA-rv87-ccxw-8ggf) |
| #13 | NETCore | CVE-2026-66822 | [GHSA-wxxw-5cvc-53j7](https://github.com/advisories/GHSA-wxxw-5cvc-53j7) |
| #14 | WindowsDesktop | CVE-2026-96235 | [GHSA-g44w-h65v-4cw9](https://github.com/advisories/GHSA-g44w-h65v-4cw9) |
| #15 | WindowsDesktop | CVE-2026-96229 | [GHSA-5qr9-xxm9-6cmq](https://github.com/advisories/GHSA-5qr9-xxm9-6cmq) |
| #16 | WindowsDesktop | CVE-2026-96234 | [GHSA-fq6x-pvr9-888r](https://github.com/advisories/GHSA-fq6x-pvr9-888r) |
| #17 | WindowsDesktop | CVE-2026-96232 | [GHSA-f8qh-mqg4-9939](https://github.com/advisories/GHSA-f8qh-mqg4-9939) |
| #18 | WindowsDesktop | CVE-2026-70174 | [GHSA-f749-vhgx-39h6](https://github.com/advisories/GHSA-f749-vhgx-39h6) |
| #19 | WindowsDesktop | CVE-2026-83520 | [GHSA-4w5f-7jp2-w48g](https://github.com/advisories/GHSA-4w5f-7jp2-w48g) |

The saved alert inventory and checked mapping are in
`artifacts/dependabot-servicing-20261014/alerts-before.json` and
`alert-coverage.json`. No alert was dismissed or suppressed.

## Validation

All 27 automated Windows acceptance checks pass on the clean implementation
commit, using the portable SDK archive verified against Microsoft's SHA512:

- Locked solution restore; unchanged portable locks throughout acceptance.
- Full Release and Debug solution builds, each with zero warnings/errors.
- `XcpNgCenter.Shell.Tests`: 1,105 passed in each configuration, no skips.
- `XenCenterLib.Tests`: 113 passed on .NET 10; 112 on Framework 4.8.1, no skips.
- Fourteen notice fixtures, including rejection of a payload carrying 10.0.12.
  Embedded/distributed legal-notice coverage requires the current 10.0.13 notice.
- WinForms resource, lifecycle/designer and archived-plugin checks in both
  configurations; all 12 proxy authentication cases; all six UI probe modes.
- A fresh Windows self-contained publish and archive/startup/helper smoke pass.
  Both `includedFrameworks` and the runtime package identity are 10.0.13.
- NuGet solution vulnerability audit, including transitive packages: no known
  vulnerable packages reported. The separate implicit-download check above
  covers the runtime packs omitted by the ordinary package listing.

Evidence: `artifacts/dependabot-servicing-20261014/acceptance/acceptance.json`,
its logs/TRX files, `package-audit.json`, and `published-runtime.json`.
Package SHA256: `a7d0c9ec9f5bbd3a2bcf9c93e858290ff0195e6103fb1856fbbedcfd8c621fd7`.
Independent dependency review found no substantive defects.

## Remaining limits

The local full builds reuse the existing trusted RDP interop DLLs with
`SkipRdpAxImp=true`; they do not regenerate them with Visual Studio/Windows SDK
tools. Native Linux acceptance and current-head hosted Windows/Linux/CodeQL
checks remain for the PR. Live-pool, physical desktop and actual updater/UAC
acceptance were not performed.

Dependabot closure requires the updated dependency graph on `development` after
merge. Already published self-contained installations carry their old runtime
until an application release is rebuilt, published and installed. Framework-
dependent WinForms users must install .NET Desktop Runtime 10.0.13 or later;
updating this SDK does not service their system runtime. No release was published
as part of this dependency PR.
