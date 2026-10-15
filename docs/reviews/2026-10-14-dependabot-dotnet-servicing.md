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

All alerts refer to the same manifest above. A forced locked restore using SDK
10.0.402 produces the following excerpt from
`XenCenterLib.Tests/obj/project.assets.json`, at `project.frameworks.net10.0`:

```json
{
  "frameworkReferences": {
    "Microsoft.NETCore.App": {
      "privateAssets": "all"
    }
  },
  "downloadDependencies": [
    {
      "name": "Microsoft.AspNetCore.App.Runtime.win-x86",
      "version": "[10.0.13, 10.0.13]"
    },
    {
      "name": "Microsoft.NETCore.App.Host.win-x86",
      "version": "[10.0.13, 10.0.13]"
    },
    {
      "name": "Microsoft.NETCore.App.Runtime.win-x86",
      "version": "[10.0.13, 10.0.13]"
    },
    {
      "name": "Microsoft.WindowsDesktop.App.Runtime.win-x86",
      "version": "[10.0.13, 10.0.13]"
    }
  ]
}
```

The project references only the .NET shared framework. All three alerted runtime
families appear in its separate package-download graph. The SDK processes known
frameworks for `RuntimeIdentifiers` and can download packs for potential
transitive references without using their assets. See
[SDK 10.0.402's RID download loop](https://github.com/dotnet/sdk/blob/v10.0.402/src/Tasks/Microsoft.NET.Build.Tasks/ProcessFrameworkReferences.cs#L360-L380)
and [download conditions](https://github.com/dotnet/sdk/blob/v10.0.402/src/Tasks/Microsoft.NET.Build.Tasks/ProcessFrameworkReferences.cs#L693-L699).
This verifies the alerted package downloads; it does not claim the test executable
uses ASP.NET Core or Windows Desktop.

Reproduce from the repository root, with the pinned SDK on PATH:

```powershell
dotnet restore XenCenterLib.Tests/XenCenterLib.Tests.csproj --locked-mode --force
python -c "import json; d=json.load(open('XenCenterLib.Tests/obj/project.assets.json', encoding='utf-8-sig')); f=d['project']['frameworks']['net10.0']; print(json.dumps({k:f[k] for k in ('frameworkReferences', 'downloadDependencies')}, indent=2))"
```

Each alerted runtime package and the host download is exactly 10.0.13. Runtime
names below abbreviate `Microsoft.*.App.Runtime.win-x86`.

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
tools. Hosted Windows, native Linux acceptance, CodeQL and dependency submission
passed on `754ef3aab`; later PR heads need their own hosted checks. Live-pool,
physical desktop and actual updater/UAC acceptance were not performed.

Dependabot closure requires the updated dependency graph on `development` after
merge. Already published self-contained installations carry their old runtime
until an application release is rebuilt, published and installed. Framework-
dependent WinForms users must install .NET Desktop Runtime 10.0.13 or later;
updating this SDK does not service their system runtime. No release was published
as part of this dependency PR.

## PR review disposition

[CodeRabbit's runtime-coverage comment](https://github.com/Narehood/xenadmin/pull/69#discussion_r4277894281)
correctly identifies the project's sole framework reference, but its conclusion
about runtime downloads is contradicted by the forced restore and the SDK source
above. The report now includes the exact excerpt and reproduction commands so
coverage can be reviewed without the local artifact directory. Dependency pins
and application code need no correction for this finding.

CodeRabbit's general docstring-coverage warning is advisory. It reports existing
helpers and test methods touched by version updates; the repository does not
require its suggested coverage threshold.
Cursor's review approved the dependency implementation without findings. The
[Windows/Linux validation](https://github.com/Narehood/xenadmin/actions/runs/38662182335),
[CodeQL](https://github.com/Narehood/xenadmin/actions/runs/38662180189) and
[dependency submission](https://github.com/Narehood/xenadmin/actions/runs/38662157648)
all passed on the reviewed head `754ef3aab`.

Review verification reran both required suites against the unchanged application
binaries: 1,105 Release shell tests, 113 shared .NET 10 tests and 112 Framework
tests passed without failures or skips. The forced locked restore preserved
all portable lockfiles, and the documented JSON excerpt matches its fresh assets
exactly. Evidence: `artifacts/dependabot-pr69-review`.
