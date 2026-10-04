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
