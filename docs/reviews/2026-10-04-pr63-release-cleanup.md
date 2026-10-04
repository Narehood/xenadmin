# PR #63 release cleanup

The final pass covers capture/reporting, shared transport, ownership cleanup,
release tooling, dependencies and current acceptance documentation. It continues
[the review follow-up](2026-10-03-pr63-review-followup.md). Before this pass,
[Cursor approved `07ef17a80`](https://github.com/Narehood/xenadmin/pull/63#pullrequestreview-5404154768),
and [CodeRabbit inspected its final delta without actionable findings](https://github.com/Narehood/xenadmin/pull/63#discussion_r4176033465).
Both reviewers must examine the cleanup changes before merge.

Implementation: `00d15d58e`.

## Verified corrections

| Issue | Change | Evidence |
| --- | --- | --- |
| Capture duration included time draining a slow output stream. | Snapshot the duration when collection stops, before draining admitted samples. Compute each serialized sample's byte count once. | A blocked-output regression failed before the fix (546.8 ms reported against a 138 ms upper bound) and passes afterward. `artifacts/pr63-cleanup-regressions/duration-before.trx` and `cleanup-after.trx` record the focused results. |
| Valid JSON scalar/array rows crashed the report reader with a traceback. | Reject non-object records and invalid event/payload shapes with a controlled error. | Four CLI cases reproduced the failure; all nine report fixtures now pass, including boolean event IDs, object payloads and even-sized unsorted percentile data. |
| Report statistics sorted each operation's durations repeatedly. | Sort durations once for median, percentiles and maximum; retain the nearest-rank percentile definition. | Existing statistics and new 100-sample unsorted/even fixtures preserve the results. This is an offline reporting optimization, without a live-pool timing claim. |
| Duplicate release-note pipelines could fail when `head` closed Git's output under `pipefail`. | Use one Python generator with bounded Git output, reachable numeric release tags, UTF-8/LF output and no overwrite. Both release modes share it. | The previous pipeline failed on this repository's history. Six isolated Git-history/CLI fixtures pass, covering 90 commits since a release, unrelated/newer tags, first releases, current-tag exclusion, invalid metadata and output preservation. Windows/Linux acceptance and release preparation run the fixtures. |
| Each modern RPC call allocated a settings object and UTF-8 encoder. | Use a readonly settings value and the shared UTF-8 encoder; keep request and pool semantics. | Existing shared transport contract tests cover both .NET 10 and Framework. No generated API or Framework serialization changes. |

The Windows ACL regression now declares its Windows-only API contract, removing
platform-analysis warnings while retaining its runtime platform guard. Current
handoff material consolidates superseded PR #63 snapshots; detailed dated review
records remain available.

## Dependency maintenance

| Component | Previous | Current |
| --- | --- | --- |
| Avalonia application packages | 11.3.20 | 11.3.22 |
| log4net | 3.4.0 | 3.5.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | 18.10.1 |
| xunit.runner.visualstudio | 2.8.2 | 4.0.0 |

Central versions and all affected solution/probe lockfiles are updated. The
installed .NET SDK 10.0.401 and runtime 10.0.12 already match the active
[.NET 10 release feed](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
The NuGet audit reports no known vulnerabilities in the solution's direct or
transitive packages (`artifacts/pr63-cleanup-vulnerabilities.json`). That report
is dated evidence, not a guarantee against future advisories.

The refreshed notice bundle still covers 48 application packages. Avalonia's
license source is pinned to package commit `627ae9ef921621e27e7aa58df2796fbadb50af88`;
log4net's license and NOTICE use its 3.5.0 source commit
`f7794ae187ab2e228e1754cb478568dd305d1b32`. Both commits were checked against
upstream release tags and package metadata. All 13 notice fixtures pass with the
new exact identities; full license text is unchanged.

Avalonia 12 and a new D-Bus API line remain separate migrations. This release
keeps the existing application API line and security-patched D-Bus pin rather
than adding another platform migration to the candidate.

## Release scope

All 27 local automated acceptance checks pass, with portable lockfiles preserved.
Evidence: `artifacts/pr63-cleanup-complete-20261004`. The runner records
`07ef17a80` plus the cleanup working-tree changes that became `00d15d58e`;
it ran before the code commit.

- Full Release/Debug solution builds: zero warnings and errors.
- Shell Release/Debug: 1,056 tests each; shared .NET 10: 113; Framework: 112.
  No failures or skips.
- WinForms: 32,138 resources in 289 sets, 349 lifecycle/designer and 23 plugin
  archive checks per configuration; 12 loopback proxy authentication cases.
- All six UI modes: networking, connection settings, beta/legal settings,
  access/recovery, modernization/expanded inventory and remote desktop.
- All 13 notice, nine report and six release-note fixtures.
- Fresh self-contained Windows publish and package legal/runtime/helper/startup
  verification. Archive SHA-256:
  `5614f23ea3d68f213cd2d44448a8f31eefd53d91f0ef7c469b1b238946e1bf47`.

Actionlint 1.7.12 validates both changed workflows; the downloaded executable's
archive matches the upstream checksum. External shellcheck/pyflakes integration
was disabled for that workflow syntax/expression check. Release-note generation
also succeeds against the actual repository history without publishing.
Final-head hosted Windows/Linux, CodeQL and both review results are tracked on
[PR #63](https://github.com/Narehood/xenadmin/pull/63) and must pass before merge.
Local builds reuse trusted RDP interop; hosted Windows regenerates it with the SDK.

This is an unsigned release candidate. Installer modernization and signing
remain deferred at the user's direction. There is no disposable XCP-ng pool in
this workspace. Physical Windows/Linux desktops and scaling, live-pool
performance and recovery, guest reboot/native RDP, and actual installation,
UAC/update/rollback/restart remain pending. Automated packaging and synthetic
desktop checks do not establish those outcomes or resolve the reported Debian
reboot incident.
