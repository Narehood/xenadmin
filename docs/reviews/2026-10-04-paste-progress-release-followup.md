# Paste status follow-up before beta publication (2026-10-04)

The user authorized merging PRs [#63](https://github.com/Narehood/xenadmin/pull/63)
and [#64](https://github.com/Narehood/xenadmin/pull/64) and publishing a new build.
They were merged in that order, preserving the reviewed commit ancestry:
`c03404bcc22f0744b9ce0711be61640de94b88a2` and
`021fbce8d7a2c234c262fa7974897f1240afa73f`. The latter source tree exactly matches
the reviewed #64 head. The beta branch was advanced without rewriting history.

## Fresh build finding

The [merged beta validation](https://github.com/Narehood/xenadmin/actions/runs/37216915576)
passed Linux acceptance, the Windows builds, both shared-library suites and the
Release shell suite. Windows Debug reported one failure among 1,056 shell cases:
`ClipboardReadsAreExplicitAndSendingUsesReviewedSnapshotThenClearsDraft` expected
the final `Text sent` message but observed the queued progress message for three
sent characters. Publication was held. The merged source's
[CodeQL checks](https://github.com/Narehood/xenadmin/actions/runs/37216912255) passed.

`Progress<int>` schedules its callbacks on the captured synchronization context,
or the thread pool when no context exists. The view model checked the active
operation before writing progress, but published the terminal message before
retiring the operation. A callback could consequently replace a completed or
failed operation's result. A reentrant property-change notification can expose
this ordering even with a serialized UI context.

## Fix and regression evidence

Implementation: `260accfdc`. Progress eligibility and the full status write now
share a lock with operation retirement and terminal publication. Success,
cancellation and failure retire progress before notifying observers of the
result. Disposal uses the same ordering gate for its state transition, then
cancels outside the lock. UI-context scheduling, delivery counts and draft
cleanup retain their existing behavior.

Five new deterministic cases hold progress reports in a controlled context.
Three drain them reentrantly from the terminal property notification; all three
failed before the fix with the progress messages for three, two and one sent
characters. The other cases cover delayed reports during a later clipboard
operation and after disposal. The focused Debug suite passes all 59 cases,
with zero build warnings or errors. Evidence:
`artifacts/paste-progress-before-20261004/paste-progress-before.trx` and
`artifacts/paste-progress-after-20261004/paste-progress-after.trx`.

An independent agent reviewed the actual patch and regressions without editing
or building; no findings remain. Complete acceptance is tracked in
`artifacts/paste-progress-complete-20261004` and the follow-up PR. Current-head
Windows/Linux CI and CodeQL, plus package verification for the exact beta
downloads, are required before publication.

Physical desktop/scaling, actual updater/UAC/rollback/restart, native RDP/guest
reboot and live-pool acceptance remain pending. The next published build remains
an unsigned beta; installer modernization and signing remain deferred.
