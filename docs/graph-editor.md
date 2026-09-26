# Performance graph editor

Select a connected host or running VM, open **Performance**, and choose
**Edit graphs…**. Add or remove graphs, change their titles, and move them up or
down. Within each graph, select recorded data sources and use the source controls
to add, remove, or reorder them. Each graph uses one vertical scale, so group
sources with compatible units.

**Save layout** applies the draft to the selected host or VM. **Cancel**, Escape,
and closing the window discard the draft without changing the saved layout.
The editor retains the draft after a rejected or failed save. Editing and closing
are disabled while a save is in progress. A layout needs at least one graph and
each graph needs at least one data source; blank titles receive a default name.

The source list comes from the selected object's retained RRD archives. A saved
source that is no longer available stays in its graph and is identified in both
the editor and performance view. Missing sources and empty time ranges do not
silently replace the user's layout with defaults. Memory keeps the existing
Used/Free/Total group; selecting it does not independently configure those
derived traces. Removing a source or graph does not stop server-side recording
or delete historical samples.

## Saved layout compatibility

Layouts retain the WinForms-compatible `XenCenter.GraphLayout` and `GraphName`
entries in `pool.gui_config`, scoped to the host/VM UUID. The editor keeps graph
and source order and preserves unavailable source identifiers. Existing layouts
continue to load; no local preference migration is required.

The save action copies the submitted draft, declares the required pool permission,
and checks the reviewed target, pool identities, and layout again on its worker.
It reads current server GUI configuration before merging only the exact keys
for this target, preserving unrelated settings and other objects' layouts.
Changes detected since the editor opened are rejected with reopen guidance.
XenAPI exposes separate get/set calls, so another client's write between those
calls cannot be detected atomically. A failed response may have followed a
successful server write; inspect the current layout before retrying.

## Historical data

The selected range uses its own archive: five-second, one-minute, one-hour, or
one-day samples. A missing week/year archive is shown without data rather than
substituting short-range points under a long-range label. Graph snapshots do not
share mutable point lists with subsequent polling updates.

RRD sample identities and polling cursors now remain in UTC. Chart labels convert
to local time only when displayed. This preserves both samples in the repeated
autumn hour and prevents spring transitions from shifting the rest of a week or
year archive. All timestamps remain in memory; this does not change the saved
layout format.

## Acceptance

Regression coverage exercises layout round-trips, missing sources, exact key
ownership, stale target/configuration rejection, RPC failures, isolated drafts,
ordering, Cancel, and save-time guards. Offline full/incremental RRD fixtures
cover long ranges, leap day, both daylight-saving transitions, and polling
cursors through the repeated hour.

Local validation passed 598 shell tests in each of Release and Debug, plus 73
shared tests on each framework. An isolated probe of the actual editor passed
20 keyboard, binding, layout and save/cancel checks at 740×720 and 500×500.
It reproduced and verified the fix for transient selection clearing during
Avalonia list moves. The temporary harness, log and screenshots are retained in
the ignored `artifacts/graph-editor-ui-probe` directory.

Live long-range RRD data and physical desktop behavior remain part of the user's
[manual platform acceptance](platform-acceptance.md). No disposable test pool
was available during implementation.
