# Development-build reboot investigation

Investigation date: 2026-09-27. Starting source: `development` at `123679abd`.

The reported VM reboot hang remains **unconfirmed**. A reproducible Avalonia
console parsing defect was found and fixed in this pass. There is no evidence
yet that this defect explains the reported incident or that the guest operating
system itself stops running. No live guest or host was rebooted in this pass.

The user's additional incident details are: the client machine runs Windows 11;
the guest runs Debian 12; `shutdown -r now` was issued through SSH into the
guest. Reboot begins, SSH disconnects, and the guest then appears unresponsive
until a forced shutdown. The exact client/build, whether its console was open,
and the final shutdown/boot console line are no longer known. There is no second-
console observation or disposable environment available for reproduction.
An SSH disconnect is expected during reboot and does not locate the later
failure. These details narrow the trigger but do not distinguish a guest shutdown
stall, a boot/network failure, or a stale console image. The RFB padding fix must
not be represented as resolving this report.

## Reproduced defect and fix

`XcpNgCenter.Rfb/RfbStream.cs` consumed protocol padding with a single stream
read. A TCP/TLS stream can return fewer bytes than requested. The next field then
starts inside the padding, corrupting the RFB pixel-format/desktop-name handshake
or a later server clipboard message. The equivalent classic WinForms reader had
already been corrected during the .NET 10 migration; the separate Avalonia
reader still had the defect.

The shell reader now uses its existing `ReadFully` helper for padding. This keeps
subsequent fields aligned and reports a disconnect if the peer closes partway
through the padding. It does not change VM reboot requests or host power actions.

The regression fixture drives the actual RFB client with a scripted duplex
stream, using 1-, 2-, 7-byte and unfragmented reads. It verifies the handshake,
a boot-style desktop resize, server clipboard parsing, a subsequent visible
pixel, full-frame requests after resizing, EOF reporting, and cancellation of a
blocked established-console read. Separate cases cover fragmented and truncated
padding directly. These are deterministic protocol tests, not a simulated
guest operating system or a live network/pool test.

Before the fix, six of eleven new cases failed: both short-padding cases, all
three truncated-padding cases, and the one-byte full-client case. After the
fix, all eleven pass. The one-byte full-client failure was a protocol exception;
the corrected client reads both frames before the intended EOF. Local before/
after TRX evidence is under ignored `artifacts/reboot-console-probe/TestResults`.
That temporary project links the checked-in test source and real RFB project;
it allowed reproducing the defect while other modernization files were being
implemented. The ordinary shell suite includes these cases automatically:

```powershell
dotnet test XcpNgCenter.Shell.Tests/XcpNgCenter.Shell.Tests.csproj -c Release -p:RestoreLockedMode=true --filter FullyQualifiedName~RfbReconnectTests
```

Existing guest retry and host safety rules are retained. A guest transport drop
continues to use the cancellable 1.5-second retry scheduler; changes to the
console identity or domain ID replace the session. An unchanged host control-
domain target is not automatically retried after a drop. Host reboot/shutdown
actions close the console, and host power-progress/offline states suppress live
console targets. The existing policy/scheduler tests remain regression coverage
for these rules.

## Evidence needed for the reported incident

Use an expendable guest and retain independent host access for this procedure.
Record the following with timestamps and time zone:

1. Client identity: WinForms or Avalonia, exact About version/build commit, OS,
   and whether the console is embedded, popped out, or full-screen.
2. Pool/host version, guest OS/kernel, installed guest tools, and whether the
   request came from the shell's normal Reboot, Force reboot, the guest itself,
   or another administrator/client. Record the last visible console text.
3. The shell operation's task result and elapsed time. Record the VM power state,
   domain ID, and whether its advertised console reference/location changes.
   These management values alone do not prove the guest has completed booting.
4. Independently check guest responsiveness through an existing SSH session or
   a harmless application request. Check a second console only after recording
   the shell symptom; another console connection may itself change the outcome.
   Ping alone does not establish OS/application health.
5. On the next controlled attempt, select a pool or another object first so the
   affected VM's shell console closes. Repeat the same normal reboot operation
   and compare the result. Do not change both reboot method and console state in
   the same comparison. Record whether closing the affected console changes an
   already-stalled operation.

Interpretation:

| Observation | Next investigation |
| --- | --- |
| Guest SSH/application is healthy, shell image remains frozen | Compare console target/domain ID, resize/update parsing, reconnect status and another viewer; this is evidence of a console problem. |
| Guest and an independent viewer stall at the same boot point | Collect guest shutdown/boot logs and the server task outcome before attributing the problem to the client. |
| Reboot only stalls while the shell console is attached | Capture before/after timing and transport/host diagnostics; establish whether this is a guest console or a host control-domain console. |
| Shell stays at Connecting without an error | Capture the stage of the tunnel/RFB handshake; see the remaining limitation below. |
| Task fails or never completes but the guest is responsive | Inspect the reported server task and VM state separately from display health; do not infer guest failure from the progress indicator. |

Capture status text and relevant guest/server task diagnostics, omitting
passwords, session cookies/tokens and clipboard contents. Console URLs may
contain identifiers; redact them before sharing while preserving whether they
changed. The shell's startup trace is not an RFB lifecycle log. This change does
not enable raw packet or parameter logging.

## Remaining limitations

A peer that keeps its connection open but sends no handshake data can leave the
current synchronous HTTP CONNECT/RFB startup waiting. The hosted path requests
no read timeout and does not expose the transport until CONNECT returns.
Selection changes can close an established RFB stream, as tested here, but this
does not establish cancellation of the earlier CONNECT phase. A general idle
read timeout would also disconnect a healthy unchanged guest display, so no such
timeout was added as a speculative reboot fix. If the incident is a persistent
Connecting state, reproduce that precise phase and add a bounded handshake with
its own cancellation tests.

The client path was also checked for display-induced backpressure. RFB decoding
runs on a background thread and framebuffer presentation is posted to the UI
dispatcher; decoding does not synchronously wait for each frame to be displayed.
The hosted stream has a five-second write timeout when the transport supports
timeouts. Closing an established RFB stream aborts the underlying transport
without flushing buffered writes; the regression above verifies release of a
blocked read. This source tracing does not establish the behavior of a real
hypervisor console proxy or prove that an attached console cannot affect guest
shutdown. No deterministic client-side deadlock that explains the Debian 12
incident was demonstrated.

The protocol tests do not establish live guest reboot recovery, host shutdown
behavior, rendering/GPU behavior, or the cause of the reported hang. Complete the
guest/host console row in [platform acceptance](platform-acceptance.md) using the
evidence above before closing the incident.
