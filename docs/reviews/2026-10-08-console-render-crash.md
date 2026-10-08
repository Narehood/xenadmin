# Shell console render crash

The user reported that opening a VM console in regular release
`v2026.10.8.256` terminated the shell without a dialog, followed by another exit
a few seconds after reopening. The affected client was `XcpNgCenter.Shell.exe`
on another Windows computer, installed through the shell updater.

Implementation: `9f5f0d05144ee8bcd3a95926ab8a71972e9f3985`, in
[PR #68](https://github.com/Narehood/xenadmin/pull/68).

## Confirmed cause

The user's .NET Runtime event reports `InvalidOperationException: Visual was
invalidated during the render pass`, from
`RfbConsoleView.TryGetDisplayRect` changing the control's bitmap interpolation
mode while `Render` runs. An offscreen Windows desktop probe reproduced the
same exception and compositor stack on release source `ca412a4c8`.

A console that must shrink uses fractional scaling. Switching its visual's
render options to high-quality interpolation invalidates the visual during
the compositor update, which Avalonia 12 rejects. The exception escapes the
desktop loop; it occurs after the shell's guarded startup sequence. Saved-server
automatic reconnection can reach the same console-rendering path on reopening.
The user's event confirms the render failure; local testing uses synthetic
frames and does not reproduce their live saved-server startup.

## Change and regression coverage

Display-rectangle calculation now returns the interpolation mode without
changing visual state. `Render` scopes that mode to `DrawImage` with
`DrawingContext.PushRenderOptions`. Whole physical-pixel scales still use
nearest-neighbor filtering and shrinking still uses high-quality filtering.
Pointer coordinates, DPI calculations, transport and framebuffer ownership
retain their existing behavior. Both embedded and detached consoles use the
same control.

Five new regression cases render a rooted console through Avalonia's compositor
with real Skia drawing. They cover the first downscaled frame at 100%, 125%,
150% and 200% display scaling, plus desktop-size and fit/native transitions in
one window. All five fail before the fix with the user's exact exception and
pass afterward. They capture a rendered frame and check that drawing leaves
the visual's interpolation setting unchanged.

An additional offscreen native Windows probe passes 120 frame/cursor updates
and repeated desktop resizes after the fix; it fails before the fix. Existing
framebuffer tests alone did not exercise the rooted console compositor path.
Evidence: `artifacts/console-crash-investigation-20261008`.

## Acceptance and recovery

All 27 automated platform acceptance checks pass on the clean implementation
commit: zero-warning Release/Debug solution builds, 1,099 shell tests in each
configuration, 113 shared .NET 10 tests and 112 Framework tests, WinForms
resources/lifecycle/plugin checks in both configurations, proxy authentication,
six UI probe modes and a fresh Windows package smoke pass. Portable locks stay
unchanged. Evidence: `artifacts/console-crash-acceptance-20261008/acceptance.json`.
The verified package SHA-256 is
`f2f3768289ab8f2bd690af32457d90868c61fa757f03ce49d3b5641739f41ff2`.
This local package uses revision `0` for acceptance and is not a published
replacement for the affected release. Current-head hosted Windows/Linux,
CodeQL and PR review results still need checking before merge/publication.

The user can temporarily prevent
automatic reconnection by backing up the roaming shell `app-settings.json`,
then setting `autoReconnectSavedServers` to `false` and
`lastSelectedDetailTab` to `0`. Saved servers and credentials need not be
removed. This is a recovery workaround, not a console-rendering fix.

Reopening with the user's saved profile and using their VM console still need
confirmation on the affected computer. Physical display/driver, live-pool,
updater/UAC/rollback/restart and guest reboot acceptance remain manual. Signing
and installer modernization remain deferred.
