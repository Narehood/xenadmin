"""Verify a trusted, locally built shell archive and run its native executable.

Python 3.12+; Linux desktop checks additionally require Xvfb and xdotool.
This executes the package: do not pass an untrusted downloaded archive.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def stop_process(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)


def wait_window_manager(manager, environment, evidence):
    # Avalonia caches existing X11 atoms during startup. Wait for Openbox to
    # create EWMH metadata (including _NET_WM_PID) before starting the shell.
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        require(manager.poll() is None, "The isolated window manager failed to start.")
        result = subprocess.run(["xprop", "-root", "_NET_SUPPORTING_WM_CHECK"], env=environment,
                                capture_output=True, text=True, timeout=5)
        (evidence / "window-manager-ready.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        ready = re.search(r"window id # (0x[0-9a-fA-F]+)", result.stdout)
        if result.returncode == 0 and ready and int(ready.group(1), 16) != 0:
            return
        time.sleep(0.1)
    raise RuntimeError("The isolated window manager did not publish its X11 readiness property.")


def check_helpers(executable, environment, evidence):
    results = []
    for mode in (
        "--prepare-shell-update",
        "--cleanup-protected-update",
        "--wait-for-shell-update-result",
        "--apply-shell-update",
    ):
        result = subprocess.run(
            [str(executable), mode],
            cwd=executable.parent,
            env=environment,
            capture_output=True,
            text=True,
            timeout=30,
        )
        (evidence / f"{mode[2:]}.log").write_text(
            f"exit_code={result.returncode}\n{result.stdout}\n{result.stderr}",
            encoding="utf-8",
        )
        require(result.returncode == 1, f"{mode} must reject missing arguments with exit 1; got {result.returncode}")
        results.append({"mode": mode, "exit_code": result.returncode})
    return results


def check_linux_desktop(executable, environment, profile, evidence):
    require(sys.platform.startswith("linux"), "Desktop smoke currently requires Linux/X11.")
    require(shutil.which("xdotool"), "Install xdotool to inspect the native main window.")
    require(environment.get("DISPLAY"), "Run the desktop smoke under xvfb-run or an X11 display.")
    trace = profile / "data/XCP-ng/XCP-ng Center Shell/startup-trace.log"
    crash = trace.with_name("startup-crash.log")
    window = None
    with (evidence / "desktop-stdout.log").open("w", encoding="utf-8") as stdout, (evidence / "desktop-stderr.log").open("w", encoding="utf-8") as stderr:
        process = subprocess.Popen([str(executable)], cwd=executable.parent, env=environment, stdout=stdout, stderr=stderr)
        try:
            deadline = time.monotonic() + 45
            inspected = []
            while time.monotonic() < deadline:
                require(process.poll() is None, f"Desktop exited before startup completed: {process.returncode}")
                require(not crash.exists(), "Desktop wrote startup-crash.log.")
                steps = trace.read_text(encoding="utf-8") if trace.exists() else ""
                if "post-window-startup" in steps:
                    found = subprocess.run(
                        ["xdotool", "search", "--onlyvisible", "--pid", str(process.pid)],
                        capture_output=True, text=True, timeout=5,
                    )
                    (evidence / "desktop-search.log").write_text(
                        f"pid={process.pid}\nexit_code={found.returncode}\n{found.stdout}\n{found.stderr}", encoding="utf-8")
                    inspected = []
                    if found.returncode == 0 and found.stdout.strip():
                        for candidate in found.stdout.splitlines():
                            title = subprocess.run(["xdotool", "getwindowname", candidate], capture_output=True, text=True, timeout=5)
                            owner = subprocess.run(["xdotool", "getwindowpid", candidate], capture_output=True, text=True, timeout=5)
                            inspected.append({"window": candidate, "title": title.stdout.strip(), "pid": owner.stdout.strip()})
                            if title.returncode == 0 and title.stdout.strip() == "XCP-NG Center (Unofficial Client)" and owner.stdout.strip() == str(process.pid):
                                window = candidate
                                break
                    (evidence / "desktop-candidates.json").write_text(json.dumps(inspected, indent=2), encoding="utf-8")
                    if window:
                        break
                time.sleep(0.25)
            if not window:
                # Keep hidden windows and foreign/missing PID metadata visible in
                # failure evidence; a startup trace alone never satisfies the gate.
                all_windows = subprocess.run(["xdotool", "search", "--name", ""], capture_output=True, text=True, timeout=5)
                diagnostics = []
                for candidate in all_windows.stdout.splitlines()[:100]:
                    details = {"window": candidate}
                    for command in ("getwindowname", "getwindowpid", "getwindowgeometry"):
                        detail = subprocess.run(["xdotool", command, candidate], capture_output=True, text=True, timeout=5)
                        details[command] = {"exit_code": detail.returncode, "stdout": detail.stdout, "stderr": detail.stderr}
                    diagnostics.append(details)
                (evidence / "desktop-all-windows.json").write_text(json.dumps(diagnostics, indent=2), encoding="utf-8")
            require(window, "The real main window did not become visible after post-window startup.")
            # Catch splash/last-window-close races and immediate post-startup failures.
            time.sleep(3)
            require(process.poll() is None and not crash.exists(), "Desktop failed immediately after startup.")
            geometry = subprocess.run(["xdotool", "getwindowgeometry", window], capture_output=True, text=True, check=True, timeout=5)
            (evidence / "desktop-window.log").write_text(geometry.stdout, encoding="utf-8")
            return {"main_window_visible": True, "post_window_startup": True, "survived_seconds": 3}
        finally:
            stop_process(process)
            for diagnostic in (trace, crash):
                if diagnostic.exists():
                    shutil.copy2(diagnostic, evidence / diagnostic.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--rid", required=True, choices=("win-x64", "linux-x64"))
    parser.add_argument("--evidence-directory", required=True, type=Path)
    parser.add_argument("--desktop", action="store_true", help="Require a visible Linux main window under X11.")
    parser.add_argument("--window-manager", action="store_true", help="Start isolated Openbox for an otherwise bare Xvfb display; use only with --desktop.")
    args = parser.parse_args()
    evidence = args.evidence_directory.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    report = {"rid": args.rid, "archive": args.archive.name, "passed": False}
    try:
        require(os.name == "nt" if args.rid == "win-x64" else sys.platform.startswith("linux"), "Run each package on its native operating system.")
        with args.archive.open("rb") as package:
            report["sha256"] = hashlib.file_digest(package, "sha256").hexdigest()
        with tempfile.TemporaryDirectory(prefix="xcpng-package-smoke-") as temporary:
            scratch = Path(temporary).resolve()
            payload = scratch / "payload"
            payload.mkdir()
            if args.rid == "win-x64":
                with zipfile.ZipFile(args.archive) as archive:
                    archive.extractall(payload)
            else:
                with tarfile.open(args.archive, "r:gz") as archive:
                    archive.extractall(payload, filter="data")

            executable = payload / ("XcpNgCenter.Shell.exe" if args.rid == "win-x64" else "XcpNgCenter.Shell")
            for filename in (executable.name, "XcpNgCenter.Shell.dll", "XcpNgCenter.Shell.deps.json", "XcpNgCenter.Shell.runtimeconfig.json", "INSTALL.TXT", "System.Private.CoreLib.dll", "coreclr.dll" if args.rid == "win-x64" else "libcoreclr.so"):
                require((payload / filename).is_file(), f"Missing archive-root file: {filename}")
            if args.rid == "linux-x64":
                require(executable.stat().st_mode & 0o111 == 0o111, "Archive did not preserve executable permissions.")

            runtime = json.loads((payload / "XcpNgCenter.Shell.runtimeconfig.json").read_text(encoding="utf-8"))["runtimeOptions"]
            require(runtime.get("tfm") == "net10.0", "The package must target net10.0.")
            require("framework" not in runtime and "frameworks" not in runtime, "The package must be self-contained.")
            require(any(item["name"] == "Microsoft.NETCore.App" and item["version"].startswith("10.0.") for item in runtime.get("includedFrameworks", [])), "Missing bundled .NET 10 runtime metadata.")
            require(runtime.get("configProperties", {}).get("System.StartupHookProvider.IsSupported") is False, "Startup hooks must remain disabled in the published app.")
            report["runtime"] = runtime["includedFrameworks"]

            profile = scratch / "profile"
            environment = os.environ.copy()
            for suffix, variable in (("config", "XDG_CONFIG_HOME"), ("cache", "XDG_CACHE_HOME"), ("data", "XDG_DATA_HOME")):
                directory = profile / suffix
                directory.mkdir(parents=True)
                environment[variable] = str(directory)
            # A missing hook would crash the runtime before Main if this security
            # boundary were lost. Use an empty shared-runtime location as well;
            # the self-contained package must load its own bundled runtime.
            environment["DOTNET_STARTUP_HOOKS"] = str(scratch / "missing-startup-hook.dll")
            environment["DOTNET_ROOT"] = str(scratch / "missing-shared-runtime")
            environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
            report["helpers"] = check_helpers(executable, environment, evidence)
            require(not args.window_manager or args.desktop, "--window-manager requires --desktop.")
            if args.desktop:
                if args.window_manager:
                    require(sys.platform.startswith("linux") and shutil.which("openbox") and shutil.which("xprop"), "Install Openbox and x11-utils for the isolated Xvfb desktop.")
                    with (evidence / "window-manager.log").open("w", encoding="utf-8") as manager_log:
                        manager = subprocess.Popen(["openbox", "--sm-disable"], env=environment, stdout=manager_log, stderr=manager_log)
                        try:
                            wait_window_manager(manager, environment, evidence)
                            report["desktop"] = check_linux_desktop(executable, environment, profile, evidence)
                            require(manager.poll() is None, "The isolated window manager exited during the desktop check.")
                        finally:
                            stop_process(manager)
                else:
                    report["desktop"] = check_linux_desktop(executable, environment, profile, evidence)
            report["passed"] = True
    except Exception as error:
        report["error"] = str(error)
        raise
    finally:
        (evidence / "package-smoke.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
