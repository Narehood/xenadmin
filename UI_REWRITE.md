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

## First slices (this PR)

- Brand-first welcome composition (XCP-ng mark, product name, one CTA)
- Design tokens (graphite + brand orange, Outfit type)
- Live connect through `XenModel` (username/password, TOFU TLS pin store, disconnect)
- Public-IP warning + acknowledgement via `HostnameAddressClassifier`
- After connect: pool name + host/VM counts (full tree next)

### How to try the preview

```bash
dotnet run --project XcpNgCenter.Shell -c Release
```

Pins are stored under `%APPDATA%\XCP-ng\XCP-ng Center Shell\known-servers.json` (preview TOFU; silent pin / re-pin, no cert dialog yet).

## Next slices

1. Pool / host / VM tree parity for Infrastructure mode
2. General tab summary
3. Storage / Network tabs
4. Console last (VNC; RDP strategy TBD on Avalonia)

## Non-goals for the preview

- Feature parity with every WinForms wizard
- Reviving `origin/avalonia` as-is
- Dropping WinForms before soak testing the net8 client
