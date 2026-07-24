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

**Versioning:** `year.month.day.revision` from `Directory.Build.props` (UTC date; `BuildRevision` defaults to `0`, CI sets `-p:BuildRevision=$GITHUB_RUN_NUMBER`).

Update downloads and Import Wizard URL fetch use `HttpClient` via `HttpFileDownloader`.

### Client updates (Avalonia shell)

`XcpNgCenter.Shell` checks GitHub Releases (`Narehood/xenadmin` by default; override with `XCPNG_UPDATE_GITHUB_REPO=owner/name`) a few seconds after launch. When a newer tag/version is found, a bottom-right banner offers **View release** / **Dismiss** (dismiss is remembered per version). Draft and prerelease tags are ignored. Publish with the manual workflow **Publish Shell Release** (`.github/workflows/publish-shell-release.yml`) or `gh release create vYYYY.M.D.N`. The soak/CI binary must stamp a **lower** `year.month.day.revision` than the release tag for the banner to appear. On Linux, **View release** uses `xdg-open`.

### Plugins (opt-in, IE WebBrowser)

Plugin tabs (`TabPageFeature` / `WebBrowser2`) remain available but **disabled by default**. Enable with registry `EnablePlugins=1`. Credential look-ups no longer use `Application.DoEvents`; IE hosting is retained until a future UI rewrite replaces this surface.

### UI rewrite

Active track: **`XcpNgCenter.Shell`** (Avalonia), documented in [`UI_REWRITE.md`](./UI_REWRITE.md).
Coexists with WinForms `XenAdmin`. Do **not** revive `origin/avalonia` as-is.

**On `development`:** Phase 1–2 parity + migrate/move harden + VM chrome + Import/Export (XVA/OVF) + global Alerts + Performance graphs + calendar versioning + GitHub update banner + Settings/About + auto-reconnect + Linux soak hardening (RFB cursor alpha, file pickers, XDG paths, `xdg-open`, Publish Shell Release). Production remains WinForms until soak is clean.

**Next:** Continue Linux/Windows desktop soak against real pools → exercise update banner against published `vYYYY.M.D.N` → later HA/AD/DR. RDP stays WinForms-only.

### Still later

- Broader async cleanup / installer CI automation.
- Memory/quiesced snapshot types (disk-only shipped first; `System.Drawing.Common` is Windows-only on .NET 8).
- RDP in the Avalonia shell (strategy TBD; WinForms remains available).
- HA/AD/DR wizards (alert fix-link for HA still points users to WinForms).

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
- **Shell:** Avalonia dialogs for first-seen and changed certs (`CertificateTrustWindow`); pins in `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json` (Windows) or `~/.config/XCP-ng/XCP-ng Center Shell/` (Linux).

## Public IP connection warning

When Add Server targets a literal public IP, `PublicIpWarningDialog` warns about exposing the management plane and suggests a VPN/SSH tunnel/private path. Users can proceed or cancel; “don’t show again” and Options → Security control `WarnPublicIpConnection`. Non-IP hostnames are not warned (no DNS resolve in this pass).

## Stale local scripts

`branding-xcp-ng/build.sh` and parts of `brand-to-xcp-ng.sh` describe an obsolete .NET 4.6 / VS2013 / packages-folder flow. The supported build path is SDK-style MSBuild / `dotnet` via GitHub Actions and the [Building wiki](https://github.com/xcp-ng/xenadmin/wiki/Building).
