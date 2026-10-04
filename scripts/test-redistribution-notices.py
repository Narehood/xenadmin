"""Exercise the package verifier's legal gates before any executable is launched."""

import json
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def load_script(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


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

    def test_dependency_version_prefix_is_not_a_recorded_identity(self):
        self.reject(lambda payload: (payload / "XcpNgCenter.Shell.deps.json").write_text(
            json.dumps({"libraries": {"System.Management/10.0.1": {"type": "package"}}}), encoding="utf-8"),
            "No recorded redistribution notice for packaged dependency: System.Management/10.0.1")

    def test_dependency_name_suffix_is_not_a_recorded_identity(self):
        self.reject(lambda payload: (payload / "XcpNgCenter.Shell.deps.json").write_text(
            json.dumps({"libraries": {"valonia/12.1.3": {"type": "package"}}}), encoding="utf-8"),
            "No recorded redistribution notice for packaged dependency: valonia/12.1.3")

    def test_runtime_version_prefix_is_not_a_recorded_identity(self):
        def change(payload):
            path = payload / "XcpNgCenter.Shell.runtimeconfig.json"
            runtime = json.loads(path.read_text(encoding="utf-8"))
            runtime["runtimeOptions"]["includedFrameworks"][0]["version"] = "10.0.1"
            path.write_text(json.dumps(runtime), encoding="utf-8")
        self.reject(change, "Refresh third-party notices for the packaged .NET runtime version.")

    def test_exact_recorded_identities_are_accepted_case_insensitively(self):
        verifier = load_script("verify-shell-package")
        packages, runtimes = verifier.recorded_notice_identities((ROOT / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8"))
        self.assertEqual(50, len(packages))
        self.assertIn(("avalonia", "12.1.3"), packages)
        self.assertIn(("10.0.12", "LICENSE.TXT"), runtimes)
        self.assertIn(("10.0.12", "THIRD-PARTY-NOTICES.TXT"), runtimes)


class NoticeGeneratorTests(unittest.TestCase):
    def test_generator_uses_restore_selected_package_folders_for_packages_and_runtime(self):
        generator = load_script("generate-third-party-notices")
        with tempfile.TemporaryDirectory(prefix="xcpng-notice-generator-") as temporary:
            root = Path(temporary)
            cache = root / "configured-package-cache"
            package = cache / "synthetic.package/1.0.0"
            package.mkdir(parents=True)
            (package / "synthetic.package.nuspec").write_text(
                "<package><metadata><copyright>Synthetic copyright</copyright></metadata></package>", encoding="utf-8")
            (package / "LICENSE").write_text("Synthetic package terms", encoding="utf-8")
            runtime = cache / "microsoft.netcore.app.runtime.win-x64/10.0.12"
            runtime.mkdir(parents=True)
            for filename in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
                (runtime / filename).write_text("Synthetic runtime " + filename, encoding="utf-8")
            for project in ("XenAdmin", "XcpNgCenter.Shell", "XenCenterLib", "XenModel", "XenOvfApi", "CommandLib"):
                assets = root / project / "obj/project.assets.json"
                assets.parent.mkdir(parents=True)
                assets.write_text(json.dumps({
                    "libraries": {"Synthetic.Package/1.0.0": {"type": "package", "path": "synthetic.package/1.0.0"}},
                    "packageFolders": {str(root / "missing-cache"): {}, str(cache): {}}}), encoding="utf-8")
            resource = root / "XenAdmin/Dialogs/LegalNoticesDialog.resx"
            resource.parent.mkdir(parents=True)
            resource.write_text('<root><data name="textBox1.Text"><value>Synthetic legacy terms</value></data></root>', encoding="utf-8")
            with patch.object(generator, "ROOT", root), patch.object(generator, "download", return_value="Synthetic font terms") as download:
                generator.main()
            bundle = (root / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8")
            self.assertIn("Synthetic.Package/1.0.0", bundle)
            self.assertIn("Synthetic package terms", bundle)
            self.assertIn("Synthetic runtime LICENSE.TXT", bundle)
            self.assertRegex(download.call_args.args[0], r"google/fonts/[0-9a-f]{40}/ofl/outfit/OFL.txt$")

    def test_missing_restored_package_has_clear_error(self):
        generator = load_script("generate-third-party-notices")
        with tempfile.TemporaryDirectory(prefix="xcpng-notice-missing-") as temporary:
            with self.assertRaisesRegex(RuntimeError, "Restore the pinned package.*synthetic/1.0.0"):
                generator.package_directory("synthetic/1.0.0", [temporary])

    def test_fallbacks_use_immutable_reviewed_sources(self):
        generator = load_script("generate-third-party-notices")
        for name in ("lzo.net", "microcom.runtime"):
            self.assertRegex(generator.FALLBACKS[name], r"/[0-9a-f]{40}/LICENSE$")


if __name__ == "__main__":
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout))
