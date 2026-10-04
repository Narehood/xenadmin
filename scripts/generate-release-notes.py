#!/usr/bin/env python3
"""Generate shell release notes from reachable version tags and commit subjects."""
import argparse
from pathlib import Path
import re
import subprocess


VERSION = re.compile(r"\d+\.\d+\.\d+\.\d+")


def git(repository, *arguments):
    return subprocess.run(["git", "-C", str(repository), *arguments], check=True,
                          capture_output=True, text=True, encoding="utf-8").stdout.strip()


def generate(repository, version, codename="Awa", notes_only=False):
    if not VERSION.fullmatch(version) or any(character in codename for character in "\r\n"):
        raise ValueError("Use a four-part version and a single-line codename")
    codename = codename.strip() or "Awa"
    tags = git(repository, "tag", "--merged", "HEAD", "--sort=-version:refname", "-l", "v*").splitlines()
    requested_version = tuple(map(int, version.split(".")))
    previous = next((tag for tag in tags if VERSION.fullmatch(tag[1:])
                     and tuple(map(int, tag[1:].split("."))) < requested_version), None)
    revision_range = f"{previous}..HEAD" if previous else "HEAD"
    limit = 80 if previous else 40
    subjects = git(repository, "log", revision_range, "--no-merges", f"--max-count={limit}", "--format=%s")
    lines = [f"## {version} ({codename})", "",
             f"Changes since `{previous}`:" if previous else "Changes in this build:", ""]
    lines.extend(f"- {subject}" for subject in subjects.splitlines() if subject.strip())
    if previous:
        count = git(repository, "rev-list", "--count", "--no-merges", revision_range)
        lines.extend(["", f"_{count} commit(s) since {previous}._"])
    if notes_only:
        lines.extend(["", "CI artifacts: download `drop-shell-linux-x64` / `drop-shell-win-x64` "
                      "from the Test Builds workflow on the branch used for this release."])
    else:
        lines.extend(["", "## Downloads", "", f"- `XcpNgCenter.Shell-win-x64-{version}.zip`",
                      f"- `XcpNgCenter.Shell-linux-x64-{version}.tar.gz`"])
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--version", required=True)
    parser.add_argument("--codename", default="Awa")
    parser.add_argument("--notes-only", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        notes = generate(arguments.repository, arguments.version, arguments.codename, arguments.notes_only)
        with arguments.output.open("x", encoding="utf-8", newline="\n") as output:
            output.write(notes)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release notes could not be generated: {error}\n")


if __name__ == "__main__":
    main()
