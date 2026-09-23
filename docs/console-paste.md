# Paste text into an Awa console

The Avalonia Awa app has a **Paste text…** button on VM and host console
toolbars, including popped-out and full-screen consoles.

1. Focus the intended terminal or input field inside the console.
2. Copy text locally, then click **Paste text…**. Check the destination name,
   server, and UUID in the dialog. This click reads one clipboard snapshot.
3. Select **Show and edit text** to inspect or edit the draft. Clipboard text is
   hidden initially. **Load clipboard** explicitly replaces the draft with a
   fresh snapshot; subsequent clipboard changes do not change the draft.
   A failed, cancelled, or oversized read keeps the existing draft. Pasting into
   the editor replaces its selection only when the complete result fits the
   limit; oversized input is rejected in full instead of truncated.
4. If the draft contains line breaks or tabs, review the counts and explicitly
   allow Enter and Tab keys. They can execute commands, trigger completion, or
   move focus. Editing or reloading resets this acknowledgement.
5. Click **Send text**, then check the console. No additional Enter is appended.
   **Stop** or closing the dialog cancels the remaining text. Already sent text
   cannot be recalled, and an in-flight character may still arrive.
   A rejection before any write keeps the draft and reports that nothing was
   sent. A partial send reports the number of characters written; if a write
   failed, the following character's delivery may be uncertain. Check the
   console before retrying. Partial or uncertain sends clear the draft to avoid
   replaying it.

This types text as paced RFB key presses, so it also works with host terminals
and VM consoles that have no clipboard service. It does not require guest tools
or invoke SSH, shell commands, or guest APIs. The wire format follows the
[RFB KeyEvent specification](https://github.com/rfbproto/rfbproto/blob/master/rfbproto.rst#keyevent).

## Security and limits

- Paste requires an authenticated, encrypted TLS console stream, checked after
  HTTP CONNECT redirects. It uses the existing certificate validation and pinning
  policy. Disconnected or unencrypted consoles have paste disabled.
- A draft is bound to one console transport generation. A disconnect, selection
  change, reboot, or reconnect invalidates it. It never resumes automatically or
  sends the remaining text to a replacement connection.
- Each request is limited to 4,096 characters. Printable ASCII is supported;
  CRLF and CR line endings become one Enter each, and Tab is explicitly gated.
  Escape, backspace, DEL, other hidden controls, and non-ASCII text are rejected
  before any text is sent. Unsupported text is never silently replaced or
  truncated. Guest keyboard layouts, Caps Lock, and application focus can affect
  the result; test the layout in a harmless field before pasting important text.
- Other local console input is suppressed during transmission. Each character
  uses a paired key-down/key-up message, stale modifiers are released at the
  start, and network writes have a five-second timeout. A write failure drops the
  transport, preventing later traffic from retrying a partial buffered paste.
- There is no background clipboard monitoring, guest-to-local clipboard sync,
  clipboard history, payload logging, or draft persistence in the app. Drafts
  are cleared after successful, partial, or uncertain transmission or on closing;
  failures before any write preserve the draft. Editor undo is disabled. Managed
  strings cannot be guaranteed to be immediately erased from process memory.
  The local system clipboard is left unchanged; guest applications can display
  or retain text that they receive.

The classic WinForms client is unchanged. Unicode paste, files/images, native
guest clipboard synchronization, and bracketed terminal paste are not supported
by this feature.

## Verification

`ConsolePasteTests` covers validation, Enter/Tab acknowledgement, exact RFB bytes,
no appended Enter, cancellation, failed writes, input exclusion, stale transport
generations, draft disposal, explicit clipboard reads, snapshot isolation, and
exception-message privacy. Run both repository suites with locked restores as
described in `ASTRA_HANDOFF.md`.

Before release, exercise a live host terminal, a Linux VM, and a Windows VM on
Windows and Linux desktops. Include punctuation/keyboard layouts, login fields,
multiline commands with trailing line breaks, cancellation, dropped connections,
reboots, and VM switching with the dialog open. Automated synthetic transport
tests do not establish delivery or keyboard-layout fidelity in real guests.
