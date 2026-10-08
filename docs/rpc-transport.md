# Shared JSON-RPC transport

The generated XenAPI method signatures remain synchronous. Their transport is
separate from request/response serialization, so both clients and shared actions
retain the existing API surface and XenAPI `Failure` descriptions.

On .NET 10, each `JsonRpcClient` owns a reusable `HttpClient`/`SocketsHttpHandler`
pool. Duplicate clients have separate pools, including heartbeat and event-poll
sessions. Proxy, cookies, certificate callback, redirect policy and connection
group changes replace the pool; existing calls finish on their original pool.
Timeout changes apply per request. Connections expire after five minutes and
idle connections after one minute. Explicit cookie containers retain their
existing sharing contract; clients without one do not retain response cookies.
Each modern pool permits at most 20 connections per server. RPC revocation
checks follow the application's explicit `CheckCertificateRevocationList`
policy; the default remains `NoCheck`, matching the Framework RPC path.

The TLS adapter gives the application policy the actual TLS destination
hostname, including redirects. Existing pins still take precedence over OS
chain trust. Without an application policy, normal TLS validation applies.
Proxy credentials remain with the configured proxy. Failed HTTP responses and
transport errors preserve the `WebException` contract; heartbeat can inspect
HTTP status from both old and new responses. The transport adds no application
retry loop for a failed mutation. HTTP proxy authentication and enabled redirects
retain their protocol behavior.
Empty responses are `ServerProtocolViolation`. Proxy-tunnel and unknown HTTP
errors retain distinct statuses so the heartbeat gives them its existing second
chance rather than classifying every unrecognized error as a broken connection.

`CallAsync<T>(method, JToken parameters, CancellationToken)` provides an async
entry point for callers with explicit JSON parameters. Set a dedicated client's
`CancellationToken` to cancel generated synchronous calls. Deadlines cover
request transmission, response headers and streamed JSON response reading;
explicit cancellation remains `OperationCanceledException`, while an elapsed
deadline is `WebExceptionStatus.Timeout`. Async continuations do not require a
UI synchronization context. Generated synchronous wrappers still block their
calling thread and should stay on the existing action/connection workers.

The October 2 lifecycle follow-up uses the async transport for heartbeat and
graph metadata polling. Heartbeat exposes `Completion`/`StopAsync`; stopping
cancels both the RPC and its 15-second delay, and releases only the duplicate's
transport. Intentional cancellation does not interrupt or log out the pool.
Its typed adapter retains the generated host-time converter outside generated
SDK files. Graph polling exposes `Completion`/`DisposeAsync`, cancels its
metadata RPC and five-second delay, closes blocked GET/XML transport, and drops
queued UI updates after disposal. The October 8 follow-up reads RRD XML nodes
and text content asynchronously, including initial history and incremental
updates. Body waits release their worker, while disposal closes blocked reads
and waits for the polling loop to finish. Connection/proxy/TLS/response-header
setup still uses the shared synchronous HTTP helper on a worker. Each body read
enforces that transport's configured receive timeout, closing only the failed
fetch on expiry so polling can continue. Progress resets the idle window; there
is no overall body deadline. Streams without timeout support keep their existing
behavior. Certificate, proxy-authentication and redirect contracts remain in effect.

WinForms console elevation accepts a per-attempt token through the concrete
`XenConnection` overload. Failed login/setup attempts release their transport
and log out a known token created by that attempt. Existing interface signatures
are retained. A successful login clears the attempt token before returning its
session; console tunnel cancellation still uses the startup guard's token.
Administrative action execution and the
main connection/event worker are still synchronous; cancelling a client request
is not proof that a server mutation was cancelled.

`net481` retains `HttpWebRequest` with the same async entry point and explicit
deadline/cancellation handling. The loopback contract tests run on both target
frameworks. Action-owned and logged-out clients release their local transport;
disposal never logs out a copied pool session token. Borrowed `RunSync` sessions
remain owned by their caller. Disposing a client retires its pool after any
active request finishes; cancel a request explicitly when immediate interruption
is required.
RBAC-authorized actions also preserve the main session they borrow. Sessions
created from a null-session elevation result or during retry are owned by the
action and released explicitly. Managed HTTP pools have no client finalizer.
Each retry releases its owned predecessor before dropping the reference and
logs out independent elevated handles. Replacing the action's login also retires
its cached cancellation client. Event-next, folder caches, patch phases and
short-lived management/precheck clients release their transport on exit. Host
menus own their shared client until close or disposal; already active calls
finish through their retained transport lease.

`Session.get_record` for a metadata database returns a separately owned client.
WinForms recovery closes that metadata login and disposes its client in a
`finally`, including failed logout, precheck cancellation and VDI changes.
Partial setup closes a known metadata handle through the caller's live client;
that caller remains borrowed. Preview recovery already follows this ownership
contract.

The retained SDK branch is not merged by this change. Live server login,
proxy deployment, event polling/reconnect, import/export and migration still
require the [platform acceptance](platform-acceptance.md) exercises.
