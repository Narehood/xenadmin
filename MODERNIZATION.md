# Modernization notes (development branch only)

XCP-ng Center is modernized **only on the `development` branch**.

## Supported workflow

- Integrate and release from `development`.
- GitHub Actions (`.github/workflows/test-builds.yml`) restores packages, builds Release and Debug, runs unit tests, and uploads artifacts.
- Package versions are managed centrally in `Directory.Packages.props`.

## Non-goals

These tracks are **out of scope** unless separately funded and restarted from current `development`:

| Track | Status |
|-------|--------|
| `origin/avalonia` | Placeholder only (README note). Do not revive. |
| `master-linux*`, `linux-dev-cocoon` | Abandoned 2019 Mono experiments. Discard. |
| Full UI rewrite (Avalonia / Qt / Electron / MAUI) | Not part of maintenance modernization. |
| Re-sync from archived Citrix `xenserver/xenadmin` | Historical only. |

## TLS / certificates (XCP-ng self-signed)

XCP-ng ships with **self-signed** management certificates by default. The client must not require a public CA, and must not blindly accept every cert.

Policy implemented in `XenAdmin/Network/SSL.cs`:

- **Trust on first use (TOFU):** first connection to a host pins the certificate hash (optionally after a warning).
- **Later connections:** require the pinned hash; changes prompt (or follow Security options).
- **No accept-all:** low-level HTTP paths without the app TOFU callback reject untrusted chains instead of returning `true`.

## Deferred product UX

A **public IP connection warning** (suggesting a tunnel/VPN) is planned as a follow-up. The hostname/IP classifier and Add Server pre-connect seam are in place so that work can ship as a small later PR without another architecture pass.

## Stale local scripts

`branding-xcp-ng/build.sh` and parts of `brand-to-xcp-ng.sh` describe an obsolete .NET 4.6 / VS2013 / packages-folder flow. The supported build path is SDK-style MSBuild / `dotnet` via GitHub Actions and the [Building wiki](https://github.com/xcp-ng/xenadmin/wiki/Building).
