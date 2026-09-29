# .NET 10 migration

The WinForms client targets `net10.0-windows`; Avalonia, RFB and shell tests target
`net10.0`. Shared libraries and their tests retain `net481` alongside `net10.0`.
`global.json` selects SDK 10.0.401 with patch-only roll-forward and no previews.
The System packages are centrally pinned to 10.0.12; package lockfiles contain
the new portable dependency graphs. WinForms uses the configuration, permission
and resource assemblies supplied by its desktop framework instead of redundant
direct package references. The portable graph still pins ConfigurationManager
for consumers that need it through a transitive dependency.
The shared `net481` graph explicitly references the centrally pinned Framework
reference-assembly package. Otherwise the SDK adds it only when a targeting pack
is absent, making locked restores differ between developer and hosted machines.

.NET 8 support ends November 10, 2026. .NET 10 LTS is supported through
November 14, 2028. Self-contained releases carry their own runtime, so servicing
requires publishing updated application packages, even on machines with an
updated system runtime. See Microsoft's [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Compatibility changes

- Rename a lambda parameter in a property accessor that conflicts with the new
  C# 14 `field` keyword. The shell's HTTP user agent now reports its actual runtime.
- Load certificates with `X509CertificateLoader` on modern .NET; the shared OVF
  library keeps its .NET Framework implementation. Authenticode signer extraction
  retains its existing API because the new loader does not extract MSI signers.
- Drain the OVF password-check CryptoStream through EOF, validating final padding
  instead of assuming one read returns all plaintext. Regression cases cover valid
  and wrong passwords and truncated ciphertext with a matching plaintext prefix.
- Read all RFB padding bytes in the classic client. A fragmented-stream probe
  reproduced the old desynchronization and now verifies complete consumption.
- Remove WinForms' unsupported AuthenticationManager module registration. The
  shared HTTP tunnel still receives the selected Basic/Digest mode and the session
  proxy. Modern .NET's HTTP handler negotiates HTTP authentication independently;
  process-wide AuthenticationManager registration cannot constrain it.
  This is not a newly removed constraint: the [.NET 8 implementation](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Net.Requests/src/System/Net/AuthenticationManager.cs)
  already returned an empty module list and did not register authentication
  handlers. [.NET 10](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Requests/src/System/Net/AuthenticationManager.cs)
  retains that behavior. The legacy Basic/Digest selection controls the custom
  transfer tunnel; it is not a client-wide restriction on proxy negotiation.
  Both clients now label this choice as transfer tunnel authentication and explain
  that server API requests and downloads negotiate independently and may use
  another method. This preserves existing transport behavior while making the
  setting's scope explicit before users choose a method.
- Retain the existing application TOFU callbacks while the shared XenAPI transport
  still uses HttpWebRequest. Narrow obsolete-API suppressions identify this boundary;
  migration does not replace certificate validation with permissive defaults.

## Proxy authentication evidence

`tools/WinForms.CompatibilityProbe` has an optional `--proxy-auth` mode. It runs
the real shared `XenAPI.HTTP.ConnectStream` and a .NET `HttpWebRequest` against a
loopback proxy with synthetic credentials. The proxy never forwards traffic or
resolves the example destination, and the probe logs only scheme names. All 12
combinations passed on .NET 10.0.12:

| Proxy offers | Selected setting | Transfer tunnel | HTTP handler |
| --- | --- | --- | --- |
| Basic | Basic | Basic | Basic |
| Basic | Digest | Rejects without authenticating | Basic |
| Digest | Basic | Rejects without authenticating | Digest |
| Digest | Digest | Digest | Digest |
| Basic and Digest | Basic | Basic | Digest |
| Basic and Digest | Digest | Digest | Digest |

Reproduce after building WinForms:

```powershell
dotnet run --project tools/WinForms.CompatibilityProbe -c Release -- `
  "XenAdmin/bin/Release/net10.0-windows/XCP-ng Center.dll" --proxy-auth
```

The same probe's `--connection-layout` mode constructs the actual WinForms
connection settings control without loading saved settings. It verifies and
renders the explanation at 486px (the options scroll minimum), 534px (default),
and 590px page widths. The explanation wraps completely at each width.
`tools/AdvancedNetworking.UiProbe --connection-settings` similarly checks the
actual shell settings window at 760x650 and its 440x400 minimum; all four
visibility/wrapping checks pass. Both layout modes write screenshots under their
ignored build output directories. Physical DPI, desktop behavior and enterprise
proxy interoperability remain manual checks.

## WinForms resources and designer metadata

No BinaryFormatter compatibility package or unsafe serialization switch is enabled.
`tools/WinForms.CompatibilityProbe` loads every embedded resource from the actual
built client, including legacy ImageList, ListView and ActiveX data, then checks
Settings type initialization and fragmented RFB reads. This detects runtime resource
failures that a compilation alone would miss. It does not instantiate every dialog
or open a live RDP session.

The September 28 maintenance batch reduces missing designer-serialization metadata
from 323 to 312 WFO1000 diagnostics. `CustomTreeView`, `FlickerFreePanel`, and the
three click-through strips now declare their actual constructor defaults. The
storage picker's four overrides and pool/host picker's two overrides declare
their own defaults too. Edited values remain designer-visible; no generated
forms or resources were rewritten.
WFO1000 remains suppressed in `XenAdmin.csproj` until the remaining properties
receive a semantic audit; changing them in bulk could change generated forms.

The September 29 batch reduces the remaining diagnostics from 312 to **302**
with ten explicit defaults. These preserve the existing constructor values:

| Control | Audited properties and defaults |
| --- | --- |
| `SectionHeaderLabel` | `LabelHorizontalAlignment`: Left; `LabelText`: null; `LabelPadding`: zero; `LineLocation`: Top; `LineColor`: Black; `LinePadding`: zero; `UseMnemonic`: false; `FocusControl`: null |
| `DecentGroupBox` | `Text`: null, matching its raw caption rather than the inherited empty-string default |
| `DateTimeMinutes15` | `Value`: January 1, 1970 at midnight, matching the constructor and snapshot schedule designer |

An explicitly empty caption remains an edit from null and survives designer
replay. Header text, padding, alignment, mnemonic and focus-target properties
retain their localizable metadata. Focus references remain designer-visible.
Snapshot-time defaults use invariant type conversion; existing quarter-hour
correction and its event guard are unchanged. No generated designer code or
resources were rewritten.

The expanded compatibility probe passes **197 checks** on Release and Debug;
30 assertions fail against the pre-change client. Its 87 additional checks cover
default omission, edited-value replay and reset, null versus empty captions,
ampersand escaping, existing header resources and snapshot time under en-US,
fr-FR and tr-TR cultures. Unsuppressed analyzer inventories confirm exactly ten
diagnostics removed and none added. Both solution builds, required test suites
and full resource probes pass; evidence is under ignored
`artifacts/winforms-designer-labels/`. Existing WFO1000 suppression remains for
the 302 unaudited properties. Descriptor/resource probes do not establish an
actual Visual Studio designer save/reopen round trip or physical desktop behavior.

The following combo-box/grid-editor batch reduces the inventory from 302 to
**297 diagnostics**. `EnableableComboBox.Enabled` remains visible and localizable,
but protected serialization/reset hooks restore the framework's local-state
semantics for both the combo box and its derived editor. The serialization hook
uses the `Control.Enabled` descriptor, which distinguishes an explicit local
disable from an effective disable inherited from a parent. Reset uses the custom
setter so that the enabled background is restored too. Adding a constant
`DefaultValue(true)` would instead serialize the parent's effective false value.

Six `IDataGridViewEditingControl` properties are explicitly non-browsable and
hidden from designer serialization: `EditingControlFormattedValue`,
`EditingControlRowIndex`, `EditingControlDataGridView`, `EditingControlValueChanged`,
`RepositionEditingControlOnValueChange` and `EditingPanelCursor`. Their runtime
getters/setters and selection notification are unchanged. The four writable
properties and Enabled account for the five removed diagnostics; the two
read-only properties complete the runtime contract audit.

The probe adds 33 checks and now passes **230 checks** per WinForms configuration;
16 assertions fail on the preceding client. It covers inherited versus explicit
disabled state, descriptor reset and replay, both control types, runtime metadata,
and actual grid owner/row initialization, dirty notification and item commit
through the production cell parser. Builds, full required suites and resource
loading pass with unchanged portable lockfiles and RDP interop. Evidence is under
ignored `artifacts/winforms-designer-editors/`; actual Visual Studio round trips
and physical desktop acceptance remain outstanding.

Memory-spinner Increment is deferred for its own semantic audit. Existing
generated forms assign it, and its display-unit getter and byte/GB-dependent
setter are not a symmetric designer value; marking it hidden would remove
existing serialized assignments without establishing equivalent initialization.
The remaining WFO1000 suppression covers **297 unaudited properties**.

The eight obsolete Form closing overrides now use `OnFormClosing`/`OnFormClosed`.
Password/HA listeners and action cancellation remain intact when a close is
cancelled. The folder dialog now calls base cleanup, releasing its owner registry
entry. Base cleanup runs before `FormClosed` observers. Main-window final
`Application.Exit()` bypasses the task-cancellation wait as before, including the
existing timeout path and settings-save failure exit.

The review follow-up guarantees base close processing through `finally` even
when dialog cleanup fails. Owner focus follows base processing and is skipped
during application/owner/Windows shutdown and owner disposal. Reentrant owner
disposal does not escape focus restoration; other exceptions remain observable.

`tools/WinForms.CompatibilityProbe --lifecycle-designer` exercises actual controls
and window handles without loading profiles or contacting a server. The September
28 probe's 110 checks
cover designer omission/edit/reset/replay, both picker overrides, cancelled
and accepted dialog closes, registry cleanup and the main-window early exit
guards. The initial 75-check probe had 29 failures against the pre-change client;
six additional failures reproduced inherited pool/host picker defaults that
would omit edited values. The final probe passes in Release and Debug. Windows
CI and the acceptance runner execute this mode for both configurations. The
review adds fault injection for cleanup, owner-focus shutdown guards, and an
isolated poison history entry that detects task-scan fall-through before settings
access. Removing the main-window exit guard was mutation-tested and fails this
probe. Main-window guards use an instance without its constructor to avoid
profile loading; certificate/subject dialogs
have no running actions. Active server-action cancellation, modal focus, complete
application shutdown and Visual Studio designer round trips remain manual checks.

See Microsoft's [WFO1000 guidance](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/compiler-messages/wfo1000)
and [resource migration guidance](https://learn.microsoft.com/en-us/dotnet/standard/serialization/binaryformatter-migration-guide/winforms-applications).

```powershell
dotnet run --project tools/WinForms.CompatibilityProbe -c Release -- `
  "XenAdmin/bin/Release/net10.0-windows/XCP-ng Center.dll" --lifecycle-designer
```

## Reproducing validation

Use the pinned SDK. On Windows with the documented RDP/Windows SDK prerequisites:

```powershell
dotnet restore XenAdmin.sln --locked-mode
dotnet build XenAdmin.sln -c Release --no-restore
dotnet build XenAdmin.sln -c Debug --no-restore
dotnet test XenCenterLib.Tests -c Release --no-build --no-restore
dotnet test XcpNgCenter.Shell.Tests -c Release --no-build --no-restore
dotnet test XcpNgCenter.Shell.Tests -c Debug --no-build --no-restore
dotnet run --project tools/WinForms.CompatibilityProbe -c Release -p:RestoreLockedMode=true -- "XenAdmin/bin/Release/net10.0-windows/XCP-ng Center.dll"
```

Native Linux CI runs portable shared tests and both shell configurations, then
launches the published package under Xvfb. Windows and Linux package validation
exercise invalid updater invocations with startup hooks disabled. The shared
publish script puts RID-specific lockfiles under each project's ignored `obj`
directory and checks that the committed portable lockfiles remain unchanged.

Real UAC, update rollback/restart, live XCP-ng mutations and physical desktop
interaction remain the user's manual gates in the [acceptance checklist](platform-acceptance.md).
Use disposable profiles and hosts for those operations. The checked-in probes
do not establish production-pool readiness by themselves.
