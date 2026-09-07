# Agent starting point

Read [docs/ASTRA_HANDOFF.md](docs/ASTRA_HANDOFF.md) for the architecture, local
build setup, review evidence, and outstanding work from the initial Astra review.
The findings are in [docs/reviews/2026-09-07-initial-review.md](docs/reviews/2026-09-07-initial-review.md).
They describe a dated snapshot; check the current code before treating an issue
as still open.

Repository context:

- `development` is the supported integration branch. See `CONTRIBUTING.md`.
- `XenAdmin` is the supported WinForms client; `XcpNgCenter.Shell` is the
  additive Avalonia preview. Shared changes can affect both clients.
- Shared libraries target `net481;net8.0`. Package versions belong in
  `Directory.Packages.props`; lockfiles are checked in.
- Start with `git status --short` and preserve existing user changes.
- For validation, run both `XenCenterLib.Tests` and `XcpNgCenter.Shell.Tests`.
  Full WinForms builds also need the Windows SDK/Visual Studio RDP interop tools.

Update the handoff when completing a reviewed issue: record the change,
regression coverage, remaining limitations, and relevant commit if one exists.
