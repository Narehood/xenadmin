"""Exercise the package verifier's legal gates before any executable is launched."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parent.parent


class RedistributionNoticeTests(unittest.TestCase):
    def reject(self, change, expected):
        with tempfile.TemporaryDirectory(prefix="xcpng-notice-fixture-") as temporary:
            scratch = Path(temporary)
            payload = scratch / "payload"
            payload.mkdir()
            windows = os.name == "nt"
            executable = "XcpNgCenter.Shell.exe" if windows else "XcpNgCenter.Shell"
            for filename in (executable, "XcpNgCenter.Shell.dll", "INSTALL.TXT", "System.Private.CoreLib.dll",
                             "coreclr.dll" if windows else "libcoreclr.so"):
                (payload / filename).write_bytes(b"fixture: must never execute")
            (payload / executable).chmod(0o755)
            for filename in ("LICENSE", "THIRD-PARTY-NOTICES.txt"):
                (payload / filename).write_bytes((ROOT / filename).read_bytes())
            runtime = {"runtimeOptions": {
                "tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.12"}],
                "configProperties": {"System.StartupHookProvider.IsSupported": False}}}
            (payload / "XcpNgCenter.Shell.runtimeconfig.json").write_text(json.dumps(runtime), encoding="utf-8")
            (payload / "XcpNgCenter.Shell.deps.json").write_text(json.dumps({"libraries": {}}), encoding="utf-8")
            change(payload)
            archive_path = scratch / ("fixture.zip" if windows else "fixture.tar.gz")
            if windows:
                with zipfile.ZipFile(archive_path, "w") as archive:
                    for path in payload.iterdir():
                        archive.write(path, path.name)
            else:
                with tarfile.open(archive_path, "w:gz") as archive:
                    for path in payload.iterdir():
                        archive.add(path, arcname=path.name)
            evidence = scratch / "evidence"
            result = subprocess.run([sys.executable, str(ROOT / "scripts/verify-shell-package.py"),
                                     "--archive", str(archive_path), "--rid", "win-x64" if windows else "linux-x64",
                                     "--evidence-directory", str(evidence)], capture_output=True, text=True, timeout=15)
            self.assertNotEqual(0, result.returncode)
            report = json.loads((evidence / "package-smoke.json").read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            self.assertIn(expected, report["error"])
            self.assertNotIn("helpers", report)

    def test_missing_license(self):
        self.reject(lambda payload: (payload / "LICENSE").unlink(), "Missing archive-root redistribution notice: LICENSE")

    def test_missing_third_party_notices(self):
        self.reject(lambda payload: (payload / "THIRD-PARTY-NOTICES.txt").unlink(),
                    "Missing archive-root redistribution notice: THIRD-PARTY-NOTICES.txt")

    def test_truncated_license(self):
        self.reject(lambda payload: (payload / "LICENSE").write_text("Copyright only", encoding="utf-8"),
                    "Packaged redistribution notice differs from the reviewed source: LICENSE")

    def test_truncated_third_party_notices(self):
        self.reject(lambda payload: (payload / "THIRD-PARTY-NOTICES.txt").write_text("Attribution only", encoding="utf-8"),
                    "Packaged redistribution notice differs from the reviewed source: THIRD-PARTY-NOTICES.txt")

    def test_dependency_without_notices(self):
        self.reject(lambda payload: (payload / "XcpNgCenter.Shell.deps.json").write_text(
            json.dumps({"libraries": {"Synthetic.Unreviewed/999.0.0": {"type": "package"}}}), encoding="utf-8"),
            "No recorded redistribution notice for packaged dependency: Synthetic.Unreviewed/999.0.0")

    def test_runtime_without_notices(self):
        def change(payload):
            path = payload / "XcpNgCenter.Shell.runtimeconfig.json"
            runtime = json.loads(path.read_text(encoding="utf-8"))
            runtime["runtimeOptions"]["includedFrameworks"][0]["version"] = "10.0.999"
            path.write_text(json.dumps(runtime), encoding="utf-8")
        self.reject(change, "Refresh third-party notices for the packaged .NET runtime version.")


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout))
