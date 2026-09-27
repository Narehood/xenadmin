# Retained XenAPI SDK branch review

Reviewed `origin/asv/xsa-498` at
`c530226527b95d1516e24b1d1077d503802c2574` (2026-07-14, "Update the SDK to
26.16.0") against `development` at `6d775291e`. Its merge base is
`ba43130ffb30d05d1eb40187b8ef9f2de111468a`. The branch contributes one commit,
108 changed files, 17,084 insertions and 3,407 deletions relative to that base.

**Disposition: retain the branch as reference; do not merge this snapshot into
the .NET 10 migration.** The security boundary relevant to the branch already
has a local fix, and replacing the generated sources would break application
integration and restore a confirmed credential-logging defect. This review
completes the branch assessment; it does not claim the SDK upgrade is integrated.

## Security findings

[XSA-498 / CVE-2026-42491](https://xenbits.xenproject.org/xsa/advisory-498.html)
concerns C#/PowerShell HTTP transfer connections outside the main RPC channel.
Missing certificate validation can expose session tokens or transfer data to an
interceptor. The advisory calls for rebuilding consumers with corrected SDKs.
The branch adds explicit certificate callbacks to the HTTP helpers.

Current `development` already corrected this path in `17fb049a5`:
[`HTTP.ConnectStream`](../../XenModel/XenAPI/HTTP.cs) passes the destination
hostname to the application's certificate policy and otherwise requires normal
TLS validation. It also authenticates with the actual hostname and the shared
TLS 1.2/1.3 policy. This differs from the vulnerable merge-base implementation,
which returned `true` for every certificate. The September R05 fix additionally
ensures existing pins take precedence over ordinary certificate-chain trust in
both clients. TOFU's first-use policy remains a separate, documented decision.

The retained SDK's `JsonRpc.cs` still invokes `RequestEvent(jsonReq)` in Debug
builds. Our current session logger treats its argument as a method name, so
replacing only that file would write the entire request at INFO level for most
calls, including positional credentials. Replacing the older session extension
as well restores the previously reviewed R08 defect. **Preserve method-name-only
events in every configuration**, not just redaction at a particular subscriber.
`JsonRpcLoggingTests` already exercises the actual wire/event/appender boundary.

New [`HttpTransportCertificateTests`](../../XcpNgCenter.Shell.Tests/HttpTransportCertificateTests.cs)
exercise the real socket/TLS path with disposable loopback certificates:
untrusted certificates without an application policy are rejected; an explicit
policy receives the destination hostname and its accept/reject decision is
honored; an accepted existing pin permits traffic, while a rejected replacement
certificate prevents application data from being sent and retains the old pin.
No host credentials, trust-store installations, or live-pool access are needed.

## Integration and runtime findings

| Area | Concrete incompatibility or work required |
| --- | --- |
| HTTP transfer calls | `HttpGetStream`, `HttpPutStream`, `HttpConnectStream`, `Get`, `Put`, and generated HTTP actions gain a certificate callback argument. Existing `HTTPHelper` callers do not supply it; `ConnectStream` also becomes private. Adapt every transfer/redirect/proxy path together. |
| Modern JSON-RPC transport | `NET8_0_OR_GREATER` selects `HttpClient` on .NET 10. Its validation callback uses `HttpRequestMessage`; the existing shell and WinForms policies resolve `HttpWebRequest` or a hostname string. Add an explicit hostname adapter and test pin behavior before enabling this transport. |
| Failure and resource behavior | The new modern transport uses `SendAsync(...).Result` and creates/disposes a client and handler per call. Existing connection, heartbeat, cancellation and task code catches `WebException`. Audit wrapped exceptions, cancellation, timeouts, proxy defaults, redirect handling, connection reuse and disposal before switching. A compile pass alone cannot establish compatibility. |
| XCP-ng API versions | `ApiVersion.cs` removes `API_2_16` (XCP-ng 8.2), changes later enum ordinals, and removes comparison helpers. Retain XCP-ng mappings and test version negotiation/fallback against supported host versions. |
| Session/application extensions | `Session.Proxy` and `UserAgent` change from static fields to instance properties; `APIVersion` gains a private setter, and other session members/signatures change. Preserve application initialization, duplicated-session ownership, and extensions. |
| Generated API surface | Adds `Driver_variant`, `Host_driver`, `VM_group` and newer platform methods/fields. These are candidates for deliberate regeneration/porting when their features are needed, with old-server unknown-field/method coverage. Their presence does not demonstrate XCP-ng support. |
| Packaging/dependencies | The added standalone `XenServer.csproj` is not the repository's `XenModel` project. It includes its own frameworks, version placeholders and a Newtonsoft.Json 13.0.3 reference; the repository centrally pins 13.0.4. Do not import that package/build setup or downgrade dependencies. Preserve `net481` and `net10.0` in the actual shared project and regenerate its existing lockfile. |

The [official SDK guide](https://docs.xenserver.com/en-us/xenserver/developer/sdk-guide.html)
describes the SDKs and links their API/language documentation. Upstream publishes
[v26.16.0](https://github.com/xapi-project/xen-api/releases/tag/v26.16.0), while the
[release index](https://github.com/xapi-project/xen-api/releases) already lists
later versions. The local commit message is a version claim, not a recorded
artifact checksum or generator revision. Choose and record a source tag,
generator revision and archive checksum for a future refresh, and verify its
XSA patches explicitly rather than assuming the branch name establishes them.

## Safe follow-up sequence

1. Keep the existing transport while landing .NET 10. Run Release **and Debug**
   logging/TLS regressions, both shared target-framework suites, the shell suite
   on Windows and Linux, and both self-contained publishes.
2. In a separate SDK change, regenerate or port the required API additions while
   retaining XCP-ng version mapping and application extension contracts. Exclude
   the standalone SDK project and preserve the method-only diagnostic boundary.
3. Treat any `HttpClient` adoption as an explicit transport change. Cover TOFU
   hostname adaptation, trusted/changed pins, transfer redirects, authenticated
   proxies, timeouts, cancellation, duplicate sessions, response errors and
   event-poll reconnection in both supported runtime families.
4. Before release, perform the documented manual desktop/live-pool acceptance
   tests for login, console, RRD, import/export, migration and connection loss on
   supported XCP-ng versions. No disposable hosts were available for this review;
   loopback tests do not establish those results.

The review does not identify a missing security patch that requires a 108-file
merge now. It establishes the integration work and regression gates required
before the retained SDK can safely replace the current source.
