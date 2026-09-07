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

`XcpNgCenter.Shell` checks GitHub Releases (`Narehood/xenadmin` by default) a few seconds after launch. A newer release can be downloaded into the user's non-roaming cache after validating its GitHub SHA-256 digest. Restart begins with the installed executable, which fetches the exact release metadata again and verifies a fresh archive copy before extracting or launching replacement code. Cached executables and local cache manifests do not authorize installation.

Protected Windows installations request UAC approval for that installed bootstrap. Elevated preparation uses the built-in `Narehood/xenadmin` publisher, rebuilds metadata HTTPS certificate trust in Windows machine context, and creates administrator-owned staging with user read/execute access. `XCPNG_UPDATE_GITHUB_REPO=owner/name` applies only to update checks and non-elevated preparation. The verified helper applies files with rollback protection, and a broker is intended to reopen the client with the original user's normal token. Cancelling UAC retains the download for a later attempt, which verifies it again. Installation requires fresh GitHub metadata, so it cannot complete offline. Real UAC, rollback, token/ACL, and restart behavior still requires the tests listed in the [remediation record](docs/reviews/2026-09-07-remediation.md).

**View release** and remembered **Dismiss** options remain available; unsupported platforms or non-portable launch layouts fall back to the release page. Draft and prerelease tags are ignored. Publish complete assets with the manual workflow **Publish Shell Release** (`.github/workflows/publish-shell-release.yml`). The running binary must stamp a lower `year.month.day.revision` than the release tag for the banner to appear.

Install the first release containing this bootstrap manually when upgrading an older deployed shell; that older executable still runs its original updater. Custom-repository builds also require manual installation when administrator privileges are needed or the app is elevated. The shell directs these builds to the release page before requesting UAC.

### Plugins (opt-in, IE WebBrowser)

Plugin tabs (`TabPageFeature` / `WebBrowser2`) remain available but **disabled by default**. Enable with registry `EnablePlugins=1`. Credential look-ups no longer use `Application.DoEvents`; IE hosting is retained until a future UI rewrite replaces this surface.

### UI rewrite

Active track: **`XcpNgCenter.Shell`** (Avalonia), documented in [`UI_REWRITE.md`](./UI_REWRITE.md).
Coexists with WinForms `XenAdmin`. Do **not** revive `origin/avalonia` as-is.

**On `development`:** Phase 1–2 parity + migrate/move harden + VM chrome + comprehensive host/VM Properties (custom fields, alerts, power, GPU, clustering/NRPE, cloud config) + Import/Export (XVA/OVF) + global Alerts + Performance graphs + calendar versioning + GitHub updates with installation-time package verification + persisted General/Connection/Display/Security/Confirmations/Privacy settings + Linux soak hardening (RFB cursor alpha, file pickers, XDG paths, Publish Shell Release). Production remains WinForms until soak is clean.

**Next:** Continue Linux/Windows desktop soak against real pools → exercise download/apply/restart updates between published `vYYYY.M.D.N` builds → later HA/AD/DR. RDP stays WinForms-only.

### Still later

- Broader async cleanup / installer CI automation.
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

- **Trust on first use (TOFU):** an unpinned host with an untrusted chain is pinned after acceptance. An unpinned CA-valid host uses OS trust without creating a pin.
- **Later connections:** an existing pin is checked even when the new certificate has a valid CA chain. Changes follow the configured warning/re-pin policy; disabling change warnings permits silent re-pinning.
- **No accept-all:** low-level HTTP paths without the app TOFU callback reject untrusted chains instead of returning `true`.
- **Shell:** Avalonia dialogs for first-seen and changed certs (`CertificateTrustWindow`); pins in `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json` (Windows) or `~/.config/XCP-ng/XCP-ng Center Shell/` (Linux). Settings can disable either prompt independently while retaining pinning; secure prompting remains the default.

## Public IP connection warning

When Add Server targets a literal public IP, `PublicIpWarningDialog` warns about exposing the management plane and suggests a VPN/SSH tunnel/private path. Users can proceed or cancel; “don’t show again” and Options → Security control `WarnPublicIpConnection`. Non-IP hostnames are not warned (no DNS resolve in this pass).

The Avalonia shell applies the same literal-public-IP guard to both its welcome connection form and its separate Add Server dialog. Saved-server reconnects are treated as previously accepted, and the warning can be controlled from Settings → Security.

## Stale local scripts

`branding-xcp-ng/build.sh` and parts of `brand-to-xcp-ng.sh` describe an obsolete .NET 4.6 / VS2013 / packages-folder flow. The supported build path is SDK-style MSBuild / `dotnet` via GitHub Actions and the [Building wiki](https://github.com/xcp-ng/xenadmin/wiki/Building).
