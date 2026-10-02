# Archived IE plugin tabs

Archived on 2026-10-01 at the user's request, from the implementation at
`ead8f6c1fcfde3c75eb5cf7eb95ce4ea0c0da949`.

These plugins embedded web pages in WinForms tabs, could replace the standard
console tab, and exposed browser scripting and plugin credential storage.
The source is preserved here for reference. This directory is outside every
project root and is not compiled, embedded as resources, or included in app
packages. There is no setting or registry flag that enables these tabs.

The original paths are preserved below this directory:

- `XenAdmin/Plugins/Features/TabPageFeature.cs`: browser tabs, scripting bridge,
  per-object state and authentication/credential handling.
- `XenAdmin/Core/WebBrowser2.cs`: IE/ActiveX browser wrapper.
- `XenAdmin/Plugins/UI/TabPageCredentialsDialog.*`: credential UI and resources.
- `XenModel/XenAPI-Extensions/Pool.PluginSecrets.cs`: extracted plugin-secret
  helpers from `Pool.cs`, with their original license notice.

The five moved files retain their original bytes and license notices.
`integration-removal.patch` records the removal of the MainWindow, manifest
loader, manager and Pool integration from the active code. It uses zero-context
hunks; inspecting or reversing it with `git apply` requires `--unidiff-zero`.

Menu and command extensions remain in `XenAdmin/Plugins`, with their existing
opt-in policy. Mixed manifests skip `TabPage` entries and preserve valid menu
commands. Tab-only plugins show an archive error and remain disabled. The
standard VM, host and driver-domain console tabs are used normally.
Existing server-side plugin secrets are not read, deleted or migrated by this
change.

Restoration requires deliberate source/project integration and a fresh review
of browser hosting, authentication and supported workflows. Restoring the
files and reversing the integration patch provides historical code, not a
supported browser implementation. A future web integration should start from a
concrete requirement and a modern design.
