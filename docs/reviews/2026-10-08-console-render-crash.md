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
`DrawingContext.PushRenderOptions`. The constructor keeps aliased edges and
leaves visual interpolation unspecified so it cannot override the per-image
filter. Whole physical-pixel scales use nearest-neighbor filtering; shrinking
uses high-quality filtering, verified by the pixel regressions below.
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

## PR review filtering correction

Cursor's filtering finding was valid. Avalonia 12.1.3's NuGet metadata identifies
source commit `8eeda4f6f546165b3f72e63c9f42247abb306905`. Its
[compositor](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionVisual/ServerCompositionVisual.Render.cs#L139)
pushes the visual's options before replaying its drawing. The Skia
[drawing context](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/src/Skia/Avalonia.Skia/DrawingContextImpl.cs#L700)
merges options using
[MergeWith](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/src/Avalonia.Base/Media/RenderOptions.cs#L133),
which retains interpolation that is already specified. The initial crash fix
left `None` on the control, blocking the draw's `HighQuality` choice. A uniform
frame could not distinguish those filters.

Correction: `52084ad84` removes that constructor setting while preserving
`EdgeMode.Aliased` and scoped draw options. Six additional rooted-compositor
pixel cases use alternating black/white columns. Four shrinking cases at
100%, 125%, 150% and 200% display scaling produce only black before the correction
and blended gray afterward. Two whole-physical-pixel cases, including fractional
display DPI, retain exact black/white pixels. Rendering must leave the visual's
interpolation unchanged and retain aliased edges. Four cases fail before the
correction; all 11 console cases pass afterward. Evidence:
`artifacts/pr68-filtering-review-20261008`.

All 27 fresh acceptance checks pass on clean corrected source `52084ad84`:
zero-warning Release/Debug solution builds, 1,105 shell tests per configuration,
113 .NET 10 and 112 Framework shared tests, WinForms/proxy checks, all six UI
probes and a fresh Windows package smoke pass. Portable locks are preserved.
Evidence: `artifacts/pr68-filtering-acceptance-20261008/acceptance.json`.
The corrected package SHA-256 is
`2cc9a3353cc3f0bc4a61ffad2f8c7d3421a32b03cbcc0bfad40988b21b03deff`.
A native Windows probe loads the extracted package's shell assembly and passes
120 frame/cursor updates and repeated resizes. Its receipt records that exact
assembly path and digest: `artifacts/pr68-filtering-review-20261008/native-package-probe.json`.
It uses synthetic data and no saved profile or pool connection. The initial
acceptance below remains evidence for `9f5f0d051`; both acceptance ZIPs use local
revision `0` and are not published releases. Current-head hosted checks and
review remain pending before merge/publication.

## Acceptance and recovery

All 27 automated platform acceptance checks pass on the clean initial crash-fix
commit `9f5f0d051`: zero-warning Release/Debug solution builds, 1,099 shell tests in each
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
