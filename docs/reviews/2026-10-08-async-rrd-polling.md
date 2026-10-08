# Async RRD body polling and branch cleanup (2026-10-08)

The user requested local and GitHub branch cleanup, then selected further async
cleanup. Implementation: `c1b1ab355`, based on `development` at `2d15539bb`.

## Behavior and boundary

Initial graph history and incremental archive polls now await RRD XML node reads
and text-content reads. A slow body suspends the operation instead of holding a
worker in synchronous network reads. Polls remain sequential, keep their archive
resolution/cursor rules, and publish through the existing UI dispatcher.

Disposal closes blocked body reads, waits for a started poller's completion and
drops fetched or queued results after retirement. Duplicate metadata clients
remain separately owned; cancelling graphs preserves the pool connection.
Fetch diagnostics retain elapsed time but always report allocation as unknown:
even a continuation on the original thread can include unrelated allocations
during an await.

Connection, custom proxy authentication, TLS and response-header setup retain
the shared synchronous `HTTP.HttpGetStream` helper on a worker. This pass keeps
that cross-client policy boundary intact. Main connection/event and
administrative action workers also retain their synchronous compatibility path.
No dependency versions, lockfiles or generated XenAPI code were changed.

## Regression evidence

Both fragmented-body cases failed against the original synchronous implementation
in `artifacts/async-rrd-before-20261008/rrd-before.trx`. Their streams allow async
reads and reject synchronous reads, including 20,000-character text values split
into one-byte and 4,096-byte fragments.

All 33 focused polling/history/diagnostics cases pass in
`artifacts/async-rrd-focused-20261008-r2/rrd-focused.trx`. Coverage includes async
body parsing, cancellation before the first node and inside an unfinished text
value, started-poller completion, late awaited results, metadata client ownership,
idle cancellation, queued result retirement, independent archive resolution and
cursors, week/year history, leap day and DST transitions. The capture assertion
checks actual RRD fetch timings with unknown allocation.

## Acceptance and remaining work

Full automated acceptance is recorded in
`artifacts/async-rrd-complete-20261008/acceptance.json`.
All 27 checks passed, with portable lockfiles preserved. The source manifest
records implementation `c1b1ab355`; a later, separately requested reboot-caption
fix was present for the final UI/package phases and is submitted independently.

- Full solution Release/Debug builds: zero warnings and errors.
- Shell Release/Debug: 1,067 tests each; shared .NET 10: 113; Framework: 112.
  No failures or skips.
- WinForms: all 32,138 resources in 289 sets, 349 lifecycle/designer and 23 plugin
  archive checks per configuration; all 12 proxy authentication checks.
- All six UI modes: 26 networking, four connection settings, 24 beta/legal,
  98 access/recovery, 378 modernization and 12 Remote Desktop checks.
- Fresh self-contained Windows package: legal identities, runtime, helper
  rejection and desktop startup checks pass. Archive SHA-256:
  `16b7857205cc388d027f537fde6c1ae95a9513f246c62de5bbbd169776cf6802`.

Two additional cases exercise the production full and incremental inspectors
with async-only, one-byte-fragment streams and long source names. Both shell
configurations pass all 1,069 tests after adding those boundary checks, with no
warnings, failures or skips. Final TRX evidence is in
`artifacts/async-rrd-final-20261008-r2`.

Local WinForms builds reused the unchanged trusted RDP interop with
`SkipRdpAxImp=true`. Current-head native Windows/Linux CI and CodeQL remain the
hosted validation gates for the follow-up PR.

Physical Windows/Linux desktop and scaling, live graph timings and pool recovery,
actual updater/UAC/rollback/restart, native RDP and the reported guest reboot
incident remain manual acceptance work. Installer modernization and signing
remain deferred. Synthetic cancellation and history tests do not establish live
pool performance or guest recovery.

## Branch cleanup

All 16 local feature branch tips were contained in `development` at
`2d15539bbcca3db84373b74d1aaa83066e164ea1`, matched GitHub and had no open PR
using them as head or base. An atomic remote deletion used an expected-tip lease
for every ref; local deletion used `git branch -d`. The exact names and tips are
retained in `artifacts/branch-cleanup-20261008/integrated-branches.json`.

The removed branches were the two `fix/` branches for console paste completion
and fragmented RFB padding, and the 14 `modernization/` branches for platform
readiness, directory access, disaster recovery, access/recovery readiness,
WinForms lifecycle/designer/labels/editors/completion, inventory refresh,
external RDP, IE plugin archival, final review, legal/console/transport/performance
and latest-stable platform migration. Their commits remain reachable through
the integration history, including the superseded #58–#61 stack.

`artifacts/pr52-review` and `artifacts/pr53-review` were clean and are detached
at `39987f6db` and `aa2da2aeb`; their checkout and ignored evidence files remain.
The integration and beta branches, historical `2020.03` branch and the following
unmerged remote branches were preserved:

- `asv/xsa-498`: one commit outside `development`.
- `feature-date-time-regional-setting`: one commit outside `development`.
- `feature-show-graphdata-in-mbits`: two commits outside `development`.
- `feature-test-export-serverside-zstd`: 124 commits outside `development`.

The repository fetch configuration follows `development` only. Inventory and
pruning therefore explicitly fetched all remote branch refs; the fetch
configuration itself was preserved.

The current release status was also reconciled: PR #65 is merged, `beta` and
`development` share `2d15539bb`, and
[beta v2026.10.4.249](https://github.com/Narehood/xenadmin/releases/tag/v2026.10.4.249)
was published on October 4. Source-head Test Builds, CodeQL and the publication
workflow all succeeded. The handoff retains the older publication-hold snapshot
and now leads with the verified current status.
