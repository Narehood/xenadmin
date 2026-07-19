# UI rewrite (Avalonia preview)

This track replaces the Windows 7–era WinForms chrome with a new Avalonia shell.
It is **additive**: the supported production client remains `XenAdmin` until the
rewrite reaches feature parity for your environment.

## Branch basis

Built on top of the modernization stack (CI, security, `HttpClient`, Import Wizard
fixes, plugin DoEvents removal, and `XenAdmin` → `net8.0-windows`).

## Projects

| Project | Role |
|---------|------|
| `XenAdmin` | Current supported WinForms client (`net8.0-windows`) |
| `XcpNgCenter.Shell` | Avalonia preview shell (`net8.0`, Windows-first; Linux later) |

## First slice (this PR)

- Brand-first welcome composition (XCP-ng mark, product name, one CTA)
- Design tokens (graphite + brand orange, Outfit type)
- Add-server flow with public-IP warning via `HostnameAddressClassifier`
- Infrastructure list placeholder (no live xapi session yet)

## Next slices

1. Live connect through `XenModel` (TOFU TLS, session, disconnect)
2. Pool / host / VM tree parity for Infrastructure mode
3. General tab summary
4. Storage / Network tabs
5. Console last (VNC; RDP strategy TBD on Avalonia)

## Non-goals for the preview

- Feature parity with every WinForms wizard
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing the net8 client
