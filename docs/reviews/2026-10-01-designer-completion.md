# WinForms designer metadata completion

The audit starts from `development` at `1c1ab84b3`, after the previous control,
label, time and combo-box batches. All 297 remaining WFO1000 declarations now
have an explicit serialization policy; `XenAdmin.csproj` no longer suppresses
the diagnostic. A new unaudited writable designer property will fail the build.

| Disposition | Declarations | Reason and examples |
| --- | ---: | --- |
| Hidden | 244 | Runtime-only connections, models, delegates, actions, wizard inputs/results, credentials, console transports, graph archive controllers and selected graph state |
| Visible | 35 | Existing designer references/settings or resource-backed values: graph navigator/key/event references and UUID list, tab selection/style settings, menu commands, query filters and sorting, notification counts with setter side effects |
| Verified default | 16 | Constructor-backed values: graph timing/labels, transparent panel colors, selection flags, grid expanders, tab alignment/multiline, menu images/ratings and search height |
| Serialization/reset hooks | 2 | `MemorySpinner.Increment` configured byte input; `DataGridViewEx.Enabled` framework local state plus custom style reset |

Defaults were checked against actual backing fields and constructors. Dynamic
style defaults and mutable collection/reference settings remain visible instead
of receiving guessed constants. The audit checked typed generated assignments;
the hidden properties previously assigned there only receive null runtime state.
Their constructors/null guards were reviewed. Generated designer source and
resource files are unchanged.

The spinner previously returned the display step while accepting byte inputs.
For example, a two-MiB step in MB mode returned `2`; replaying that value made
the setter treat it as two bytes. The getter now returns the explicitly supplied
input. An unset value is omitted; Reset restores the original numeric step of
one and clears the input. Existing explicit `0.1D` GB designer assignments remain
visible and replayable. Runtime GB threshold rules and initialization remain
unchanged. There are no production callers reading Increment as a display step.

The grid Enabled hook delegates omission to the `Control.Enabled` descriptor.
This preserves a locally enabled grid under a disabled parent, while retaining
an explicit local disable. Reset runs the custom setter and restores its enabled
column-header style; it does not replace that behavior with a constant default.

The lifecycle/designer probe grows from 239 to 333 passing checks on both client
configurations. The new spinner checks first reproduced 12 failures against the
old binary. Descriptor edit/reset/replay runs under en-US, fr-FR and tr-TR;
additional checks cover graph references and the existing UUID resource,
constructor defaults, grid local Enabled state and credential metadata. Full
resource probes read 32,240 resources in 290 sets. Locked restore and both
solution builds pass without WFO1000; portable package lockfiles are unchanged.

Local builds reuse unchanged trusted RDP interop with `SkipRdpAxImp=true`.
Evidence lives under ignored `artifacts/modernization-20261001/`. Existing ACL
analyzer warnings remain. Descriptor replay and resource loading do not establish
a Visual Studio designer save/reopen round trip or complete physical desktop
acceptance. Those checks remain in the [acceptance checklist](../platform-acceptance.md).
