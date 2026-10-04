# PR #63 review follow-up

Implementation: `18b4052b18e5610b4b1248e82995e8f349e2a389`.

The completed CodeRabbit review and two Cursor runs reviewed `e1ebe63c1` in
[PR #63](https://github.com/Narehood/xenadmin/pull/63). Their 13 threads describe
12 distinct issues; the revocation finding appears in both Cursor runs. Each
finding was verified against the implementation before changing it.

## Dispositions and regression coverage

| Finding | Correction and coverage |
| --- | --- |
| Notice generation assumes the default NuGet cache | Resolve each package from its project's restored `packageFolders`, including configured caches. Fixtures cover a custom cache and a missing package. |
| Notice sources follow moving branches | Pin lzo.net, MicroCom and Outfit license sources to reviewed immutable commits. Regeneration changes only the three source URLs; license text is unchanged. |
| Notice checks accept name/version prefixes | Parse exact recorded package and runtime identities. Fixtures reject shorter versions and suffixed names and retain positive coverage for all 48 application packages. |
| Invalid capture arguments or output errors escape startup | The desktop entry wrapper reports argument, I/O and access errors to stderr and returns 1 before starting the desktop. Regressions cover invalid options and preserving an existing output file. Actual compiled CLI invocations also return 1 without an unhandled exception. |
| Capture writer failure escapes normal shutdown | Catch the capture's shutdown `IOException`, report it and return 1. A failing output stream exercises the actual writer; a separate desktop exception remains observable. |
| RBAC actions dispose the main connection client | Track borrowed session identity and release only the final owned session, including retry replacements. Both shared frameworks exercise RBAC success/failure, retry, and caller-supplied sessions using loopback RPC. |
| Modern RPC forces online revocation and unlimited connections | Snapshot the application's explicit revocation setting; use `NoCheck` by default and `Online` when enabled. Cap each pool at 20 connections. Actual TLS callbacks observe both policies; 25 concurrent requests exercise the cap and cancellation while queued. |
| Unclassified HTTP errors cause immediate heartbeat disconnect | Preserve proxy/unknown statuses, classify incomplete bodies as receive failures and invalid protocol responses separately. Tests feed translated exceptions through heartbeat failure handling: proxy/unknown errors get the existing second chance. |
| Managed client finalizer and null-session sudo ownership | Remove the finalizer; null-session elevation results and retry-created sessions have explicit ownership. Regressions verify disposal without logging out a copied pool token, and preservation of supplied sessions. |
| Successful elevation retains the startup cancellation token | Clear the client token before returning a successful session. Actual TLS elevation followed by cancelling/disposal of the attempt token still permits a generated synchronous call and owned-session logout. |
| Empty HTTP 200 responses dereference null | Reject null deserialized responses with `WebExceptionStatus.ServerProtocolViolation`. Actual empty loopback responses cover JSON-RPC v1 and v2. |
| Deadline cancellation before RFB startup leaves connecting state set | Current-generation cancellation enters existing failure cleanup; a failed completion also ends startup, clears state and tears down the transport. Tests pause the actual RFB client constructor after tunnel completion, exercise timeout/Stop races and verify retry eligibility and stale-status protection. |

## Validation

The complete platform acceptance run passes all 26 checks using the portable
.NET 10 SDK and Python 3.12. Evidence:
`artifacts/pr63-review-acceptance-20261003/acceptance.json`.

- Release and Debug complete solution builds pass.
- Shell tests: 1,047 passing in each configuration, no failures or skips.
- Shared tests: 105 passing on .NET 10 and 104 on .NET Framework 4.8.1.
- WinForms: 32,138 resources in 289 sets, 349 lifecycle/designer and 23 archive
  checks per configuration; all 12 proxy checks pass.
- All six UI modes pass: networking 26, connection settings 4, beta/legal 24,
  access/recovery 98, modernization 378 and Remote Desktop 12 checks.
- All 13 notice and seven performance-report fixtures pass. Fresh Windows
  publish/package legal and native startup checks pass. Archive SHA-256:
  `bf1c318e5b8e823d13e2f7df41f46de917a6cb247a4d9ecc5534914024420126`.
- Portable lockfiles are unchanged. Whitespace validation passes with
  `core.whitespace=cr-at-eol`, preserving the upstream notice line endings.

The acceptance manifest records the previous commit plus the follow-up working
tree because validation ran before committing these fixes. Hosted CI must
validate the final PR commit. Trusted local RDP interop was reused; hosted Windows
generates fresh interop with its SDK.

## Remaining limits

The actual TLS loopback tests verify default/explicit revocation policies and
accepted self-signed certificates. They do not install a new OS-trusted root or
simulate an unreachable CRL for such a root.

No disposable pool is available. Live timings and login/proxy/event/reconnect/
import/export/migration acceptance remain pending. Physical desktop, reboot,
RDP guest and actual MSI/updater/UAC/rollback acceptance remain pending. Installer
modernization and signing are deferred at the user's direction. Console startup
hardening does not establish the cause or resolution of the Debian reboot hang.

## Second Cursor pass: remaining session owners

The [follow-up Cursor review](https://github.com/Narehood/xenadmin/pull/63#pullrequestreview-5403972306)
confirmed the preceding corrections at `16380cca9` and raised one additional
ownership thread. Implementation: `5f6ffd530`.

- Retry helpers release each replaced owned client, log out independent elevated
  sessions, and preserve the main connection and caller-borrowed sessions. The
  action's session follows the replacement immediately; cached cancellation
  clients are discarded when their copied login handle changes.
- The connection worker disposes its event-next client on every exit. Patch
  download/upload phases release their temporary clients and restore the original
  action session/connection, so subsequent work and final cleanup retain their owner.
- Folder actions release every cached cross-connection client on success or
  failure. Migration, patch lookup, host/PBD plugging, search, supplemental-pack
  and other action-local clients have explicit bounded lifetimes. Cross-pool and
  certificate-reconnect replacements also release their previous clients.
- Preview access, HA and recovery reads release their duplicate transports.
  Recovery also releases its metadata client, including partial setup failure.
  WinForms prechecks, HA calculations, disk/RDP helpers and host diagnosis release
  their short-lived clients. A host menu owns its shared client until closure or
  disposal; active RPC leases retain their transport until calls finish.

All 11 focused action-ownership cases pass. Added loopback regressions exercise
two consecutive retry failures with both main and caller-borrowed sessions,
disposal of both replacements and cached cancellation clients, exact logout
requests for independent elevated handles, and folder-cache cleanup on success
and failure. Duplicates never log out the pool token. The shared implementation
retains C# 7.3 compatibility for the Framework target.

All 26 full local acceptance checks pass on the complete follow-up source:
Release/Debug solution builds, 1,052 shell tests in each configuration, 110 shared
.NET 10 and 109 shared Framework tests, all WinForms/proxy checks, all six UI
modes and fresh Windows package/startup/legal verification. No tests fail or
skip. Evidence: `artifacts/pr63-ownership-complete-20261003`. The archive SHA-256
is `ebe7570ef13c13a06ea82bc87ec6e4164ba698e4777493ca398bc13497d6eddc`.
Portable lockfiles are unchanged. As in the preceding pass, the manifest records
the previous head plus working-tree changes because the run began before commit.
Final-head hosted CI and bot review remain required. The remaining limits above
are unchanged.

## Third Cursor pass: WinForms metadata database cleanup

The [next Cursor review](https://github.com/Narehood/xenadmin/pull/63#pullrequestreview-5404063156)
confirmed the ownership audit at `17850c27a` and identified the successful
`Session.get_record` metadata client as a remaining WinForms owner gap.
CodeRabbit completed its review through that head with no actionable findings.
Implementation: `dd963ddba`.

WinForms storage loading and recovery completion now close the metadata login
and dispose its independently owned client in a `finally`, including failed
logout. A precheck group owns its metadata session through cancellation and
errors, and closes the preceding session before changing VDIs. Opening a new
VDI uses the current check's VDI. Partial setup closes a known metadata handle
through the caller's live client without disposing or logging out the caller's
pool session. Preview cleanup remains unchanged.

All 14 focused ownership cases pass. Two added loopback cases obtain a
successful `Session.get_record` client, exercise successful and failed logout,
assert that subsequent calls reject the disposed metadata client, and verify
that the borrowed pool client remains usable. A third case covers partial setup,
verifying the exact independent handle passed to logout and the caller's
pool token. The existing live-pool and desktop limits above remain unchanged.

All 26 full local acceptance checks pass: both solution configurations, 1,055
shell tests per configuration, 113 shared .NET 10 and 112 Framework tests,
WinForms resources/lifecycle/archive/proxy checks, all six UI modes and fresh
Windows package/startup/legal verification. No tests fail or skip. Evidence:
`artifacts/pr63-metadata-acceptance-20261003`; archive SHA-256:
`a55900c3a41ea239b18a6dc13124c2a9a746797ebe94f267b1c97ec9be545811`.
Portable lockfiles are unchanged. The manifest records `17850c27a` and the
metadata working-tree fixes because the run began before committing them.
Trusted local RDP interop was reused. Hosted CI and both reviewers must validate
the final PR head.
