# Modernization notes (development branch only)

XCP-ng Center is modernized **only on the `development` branch**.

## Supported workflow

- Integrate and release from `development`.
- GitHub Actions (`.github/workflows/test-builds.yml`) restores packages, builds Release and Debug, runs unit tests, and uploads artifacts.
- Package versions are managed centrally in `Directory.Packages.props`.

## Runtime targeting

| Project | TFMs |
|---------|------|
| CommandLib, XenCenterLib, XenOvfApi, XenModel | `net481;net8.0` |
| XenAdmin (WinForms app) | `net8.0-windows` |

Update downloads and Import Wizard URL fetch use `HttpClient` via `HttpFileDownloader`.

### Plugins (opt-in, IE WebBrowser)

Plugin tabs (`TabPageFeature` / `WebBrowser2`) remain available but **disabled by default**. Enable with registry `EnablePlugins=1`. Credential look-ups no longer use `Application.DoEvents`; IE hosting is retained until a future UI rewrite replaces this surface.

### UI rewrite

Active track: **`XcpNgCenter.Shell`** (Avalonia), documented in [`UI_REWRITE.md`](./UI_REWRITE.md).
Coexists with WinForms `XenAdmin`. Do **not** revive `origin/avalonia` as-is.

**Shipped so far (on `development` / open PRs):** live XenModel connect + TOFU trust dialogs, infrastructure tree, General/Storage/Network/Console (RFB with input/cursor/keysyms), Copy affordances, DPAPI password vault for saved servers, public-IP warning, shutdown cleanup, forget-password / clear-pins controls, CI artifacts `drop-shell-win-x64` and `drop-shell-linux-x64`, **Phase 1 parity** (Logs/Tasks, VM power + New VM + basic edit, ISO attach, New SR ISO/iSCSI/NFS, console pop-out), layout polish (wrap actions, scrollable detail, larger console), **Phase 2 start: Snapshots tab** (list / take disk snapshot / revert / delete).

**Windows Avalonia preview track:** complete. **Phase 1 action parity:** in PR `#20` (`cursor/shell-phase1-parity-abb3`). **Next:** merge Phase 1 → expand Edit/Properties → clone/delete/migrate → more SR types → Linux soak. RDP stays WinForms-only. Details in `UI_REWRITE.md`.

### Still later

- Broader async cleanup / installer CI automation.
- Linux desktop soak for the Avalonia shell (artifact published; runtime soak pending).
- RDP in the Avalonia shell (strategy TBD; WinForms remains available).
- Phase 2+ WinForms wizard/action parity remaining: full VM Properties, migrate/clone/delete, remaining SR types, HA/AD/DR, alerts, graphs.
- Memory/quiesced snapshot types (disk-only shipped first).

## Non-goals

| Track | Status |
|-------|--------|
| `origin/avalonia` | Historical placeholder. Superseded by `XcpNgCenter.Shell`. |
| `master-linux*`, `linux-dev-cocoon` | Abandoned 2019 Mono experiments. Discard. |
| Re-sync from archived Citrix `xenserver/xenadmin` | Historical only. |

## TLS / certificates (XCP-ng self-signed)

XCP-ng ships with **self-signed** management certificates by default. The client must not require a public CA, and must not blindly accept every cert.

Policy implemented in `XenAdmin/Network/SSL.cs` (WinForms) and `XcpNgCenter.Shell` TOFU services (Avalonia):

- **Trust on first use (TOFU):** first connection to a host pins the certificate hash after acceptance.
- **Later connections:** require the pinned hash; changes prompt before re-pinning.
- **No accept-all:** low-level HTTP paths without the app TOFU callback reject untrusted chains instead of returning `true`.
- **Shell:** Avalonia dialogs for first-seen and changed certs (`CertificateTrustWindow`); pins in `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json`.

## Public IP connection warning

When Add Server targets a literal public IP, `PublicIpWarningDialog` warns about exposing the management plane and suggests a VPN/SSH tunnel/private path. Users can proceed or cancel; “don’t show again” and Options → Security control `WarnPublicIpConnection`. Non-IP hostnames are not warned (no DNS resolve in this pass).

## Stale local scripts

`branding-xcp-ng/build.sh` and parts of `brand-to-xcp-ng.sh` describe an obsolete .NET 4.6 / VS2013 / packages-folder flow. The supported build path is SDK-style MSBuild / `dotnet` via GitHub Actions and the [Building wiki](https://github.com/xcp-ng/xenadmin/wiki/Building).
