#!/usr/bin/env python3
"""Release-note regressions using an isolated Git history."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("notes", Path(__file__).with_name("generate-release-notes.py"))
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)


class ReleaseNotesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="xcp-release-notes-")
        cls.addClassCleanup(cls.directory.cleanup)
        cls.repository = Path(cls.directory.name)
        notes.git(cls.repository, "init", "--initial-branch=development")
        commits = []
        for number in range(1, 92):
            message = f"Change {number} — regression fixture".encode("utf-8")
            commits.append(f"commit refs/heads/development\nmark :{number}\n"
                           f"committer Synthetic <synthetic@example.invalid> {1700000000 + number} +0000\n"
                           f"data {len(message)}\n".encode("ascii") + message + b"\n\n")
        subprocess.run(["git", "-C", str(cls.repository), "fast-import", "--quiet"],
                       input=b"".join(commits), check=True, capture_output=True)
        notes.git(cls.repository, "tag", "v2026.9.1.1", "HEAD~90")
        notes.git(cls.repository, "tag", "vNext-24307")
        # A newer numeric tag on a detached history must not define this branch's notes.
        foreign = subprocess.run(["git", "-C", str(cls.repository), "commit-tree", "HEAD^{tree}"],
                                 input="Foreign release\n", text=True, check=True, capture_output=True,
                                 env={**os.environ, "GIT_AUTHOR_NAME": "Synthetic",
                                      "GIT_AUTHOR_EMAIL": "synthetic@example.invalid",
                                      "GIT_COMMITTER_NAME": "Synthetic",
                                      "GIT_COMMITTER_EMAIL": "synthetic@example.invalid"}).stdout.strip()
        notes.git(cls.repository, "tag", "v2026.9.30.1", foreign)

    def test_long_history_caps_subjects_without_truncating_the_git_process(self):
        result = notes.generate(self.repository, "2026.10.4.1", "Awa")
        subjects = [line for line in result.splitlines() if line.startswith("- Change")]
        self.assertEqual(80, len(subjects))
        self.assertIn("Change 91 — regression fixture", subjects[0])
        self.assertIn("_90 commit(s) since v2026.9.1.1._", result)
        self.assertNotIn("Foreign release", result)
        self.assertIn("XcpNgCenter.Shell-linux-x64-2026.10.4.1.tar.gz", result)

    def test_notes_only_uses_the_same_history_and_points_to_ci_artifacts(self):
        result = notes.generate(self.repository, "2026.10.4.1", notes_only=True)
        self.assertIn("Changes since `v2026.9.1.1`:", result)
        self.assertIn("drop-shell-linux-x64", result)
        self.assertNotIn("## Downloads", result)

    def test_first_release_uses_a_bounded_history(self):
        with tempfile.TemporaryDirectory(prefix="xcp-first-release-") as directory:
            notes.git(directory, "clone", "--no-tags", "--quiet", str(self.repository), ".")
            result = notes.generate(directory, "2026.10.4.1")
            self.assertIn("Changes in this build:", result)
            self.assertEqual(40, sum(line.startswith("- Change") for line in result.splitlines()))

    def test_release_version_tag_is_excluded(self):
        result = notes.generate(self.repository, "2026.9.1.1")
        self.assertIn("Changes in this build:", result)

    def test_invalid_metadata_is_rejected(self):
        for version, codename in (("2026.10.4", "Awa"), ("2026.10.4.1", "Awa\nInjected")):
            with self.subTest(version=version, codename=codename), self.assertRaises(ValueError):
                notes.generate(self.repository, version, codename)

    def test_cli_writes_utf8_notes_and_preserves_an_existing_output(self):
        with tempfile.TemporaryDirectory(prefix="xcp-release-output-") as directory:
            output = Path(directory, "release-notes.md")
            command = [sys.executable, str(Path(notes.__file__)), "--repository", str(self.repository),
                       "--version", "2026.10.4.1", "--codename", "Awa", "--output", str(output)]
            first = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, first.returncode, first.stderr)
            content = output.read_bytes()
            self.assertIn("— regression fixture", content.decode("utf-8"))
            self.assertNotIn(b"\r\n", content)
            again = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(1, again.returncode)
            self.assertIn("Release notes could not be generated:", again.stderr)
            self.assertEqual(content, output.read_bytes())


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout))
