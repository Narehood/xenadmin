# Beta updates and manual releases

In the Avalonia shell, open **Settings → About** and enable **Receive beta
updates**, then **Check for updates**. The setting is saved immediately for the
current user and survives restart. It controls which published packages are
offered; it does not check out source or build a Git branch on the user's machine.

Regular releases are the default. Beta users receive the highest newer version
among published beta and regular releases. Drafts are never offered. Beta offers
and their release notes are labelled `(beta)`. Turning the toggle off clears the
old offer and selects regular releases again. It keeps the installed version:
after installing a beta, the next regular release must have a higher version.
Dismissed updates are remembered separately for each channel.

The toggle is disabled during a check, download, or installation confirmation.
Changing channels invalidates previous offers and prepared downloads; cached
files cannot be installed unless the selected channel offers that release again.
Both channels require the existing platform package and GitHub SHA-256 digest.
Installation authenticates the release again, with explicit beta opt-in carried
through Windows elevation. Elevated installs retain the fixed trusted publisher.

## Publishing a beta

Publication is manual. Pushes and pull requests to `beta` run Test Builds and do
not create releases. Keep experimental changes on `beta`, then bring tested
changes into `development` through a PR.

1. Merge the channel support and workflow into `development`. When starting beta
   work, create `beta` from that updated branch if it does not already exist.
   Push the changes to test to `beta` and wait for Test Builds to pass.
2. In **Actions → Publish Shell Release → Run workflow**, select **beta**, enable
   **prerelease**, and leave **attach_artifacts** enabled for installable builds.
3. Choose a revision from 0–65534 that produces a version greater than all
   existing shell release tags and the binaries being tested. The tag is
   `vYYYY.M.D.N`, using the workflow's UTC date. Beta and regular releases share
   this numeric sequence; do not add a `-beta` suffix or reuse an existing tag.
4. Run the workflow. It builds and checks Windows and Linux packages before
   creating the GitHub prerelease and attaching the archives. Enable the toggle
   on test installations, check for updates, and review the beta version offered.

To publish a regular release, select **development** with **prerelease disabled**.
The workflow rejects other branch/channel combinations. It serializes release
runs, rejects reused or older versions, and pins the same version across both
platforms even when a build crosses UTC midnight. A notes-only release has no
automatic-install package; testers must use **View release**.

GitHub requires a manually dispatched workflow to exist on the repository's
default branch. Keep release workflow changes merged there before publishing
from beta: GitHub also requires extra workflow permissions when creating releases
for commits whose workflow files differ from the default branch. This workflow
uses the existing `GITHUB_TOKEN`, without adding a personal token. See GitHub's
[manual workflow guide](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)
and [release API permissions](https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28#create-a-release).

## Manual acceptance

With disposable Windows and Linux installations, check the default channel,
enable beta, restart, and verify that the choice persists. Publish a higher beta
version manually and confirm that only opted-in clients offer it. Review the
notes, download, restart and verify the installed version, including Windows UAC
from a protected installation. Switch back and verify that no downgrade is
offered, then publish a still newer regular release and verify both channels
receive it. Also check offline errors, dismissal independence, and changing the
channel after a download. These live install checks remain manual; no release
or pool mutation is performed by the automated regression suite.
