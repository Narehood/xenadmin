"""Refresh redistribution notices from restored application packages and upstream licenses.

Run after a locked restore. The generated file is checked in: builds need no
network access or Python to preserve their legal notices.
"""

import json
from pathlib import Path
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
FALLBACKS = {
    "avalonia": "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.20/licence.md",
    "discutils": "https://raw.githubusercontent.com/DiscUtils/DiscUtils/59d7cadab839c6d8dfcf52f8be5efe6d2ced190f/LICENSE.txt",
    "log4net": "https://raw.githubusercontent.com/apache/logging-log4net/rel/3.4.0/LICENSE",
    "lzfse-net": "https://raw.githubusercontent.com/quamotion/lzfse-net/2e86a8f485fc4624d0c49e54c4050db527710704/LICENSE",
    "lzo.net": "https://raw.githubusercontent.com/zivillian/lzo.net/9c803ebc3d04ecf91035acd8c91ccdc6de043221/LICENSE",
    "microcom.runtime": "https://raw.githubusercontent.com/kekekeks/MicroCom/4b8a38f773c109bad558ee3713d9f16d80776e42/LICENSE",
    "sharpziplib": "https://raw.githubusercontent.com/icsharpcode/SharpZipLib/v1.4.2/LICENSE.txt",
    "tmds.dbus.protocol": "https://raw.githubusercontent.com/tmds/Tmds.DBus/8cb04f66c330b64244e996ff68f685f27f7381a0/COPYING",
    "system": "https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/LICENSE.TXT",
    "microsoft": "https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/LICENSE.TXT",
}


def download(url):
    with urllib.request.urlopen(url, timeout=30) as response:
        return response.read().decode("utf-8-sig")


def package_directory(package_path, folders):
    for folder in folders:
        directory = Path(folder) / package_path
        if directory.is_dir():
            return directory
    raise RuntimeError(f"Restore the pinned package before regeneration: {package_path}")


def main():
    packages = {}
    package_folders = {}
    for project in ("XenAdmin", "XcpNgCenter.Shell", "XenCenterLib", "XenModel", "XenOvfApi", "CommandLib"):
        assets = json.loads((ROOT / project / "obj/project.assets.json").read_text(encoding="utf-8"))
        package_folders.update(assets["packageFolders"])
        for name, library in assets["libraries"].items():
            if library["type"] != "package" or name.lower().startswith(("microsoft.netframework.referenceassemblies", "avalonia.buildservices")):
                continue
            packages[name] = package_directory(library["path"], assets["packageFolders"])
    sections = []
    sources = {}
    notices = {}
    for name, directory in sorted(packages.items(), key=lambda item: item[0].lower()):
        metadata = next(node for node in ET.parse(next(directory.glob("*.nuspec"))).getroot() if node.tag.endswith("metadata"))
        fields = {node.tag.split("}")[-1]: node.text for node in metadata}
        texts = []
        for path in sorted(directory.rglob("*")):
            if path.is_file() and any(word in path.name.lower() for word in ("license", "notice", "copying")):
                texts.append(("NuGet package: " + str(path.relative_to(directory)), path.read_text(encoding="utf-8-sig")))
        if not any("license" in title.lower() for title, _ in texts):
            key = next((key for key in FALLBACKS if name.lower().startswith(key)), None)
            if key is None:
                raise RuntimeError(f"No reviewed license source for {name}")
            url = FALLBACKS[key]
            texts.insert(0, (url, sources.setdefault(url, download(url)) if url not in sources else sources[url]))
        if name.lower().startswith("log4net/"):
            # The package omits the upstream NOTICE. Apache-2.0 section 4(d)
            # also requires these attribution notices in a redistribution.
            url = "https://raw.githubusercontent.com/apache/logging-log4net/rel/3.4.0/NOTICE"
            texts.append((url, download(url)))
        references = []
        for title, body in texts:
            body = body.strip()
            number = notices.setdefault(body, len(notices) + 1)
            references.append(f"Notice {number}: {title}")
        sections.append("\n".join((name, fields.get("copyright", ""), *references)))

    legacy = ET.parse(ROOT / "XenAdmin/Dialogs/LegalNoticesDialog.resx").getroot().find("data[@name='textBox1.Text']/value").text
    # Preserve notices for retained source/assets; current package licenses above
    # supersede historical descriptions of older package versions.
    sections.append("Historical source and asset notices\nCurrent package licenses above apply to the pinned binaries.\n\n" + legacy)
    url = "https://raw.githubusercontent.com/google/fonts/29e94d990c84a54f83644e7c43f42dfc9e1a4ac7/ofl/outfit/OFL.txt"
    sections.append("Outfit fonts\n" + url + "\n\n" + download(url))
    runtime = package_directory("microsoft.netcore.app.runtime.win-x64/10.0.12", package_folders)
    for filename in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        path = runtime / filename
        if not path.is_file():
            raise RuntimeError(f"Restore the pinned Windows runtime package before regeneration: {path}")
        body = path.read_text(encoding="utf-8-sig").strip()
        number = notices.setdefault(body, len(notices) + 1)
        sections.append(f"Bundled .NET runtime 10.0.12: {filename}\nNotice {number}")
    sections.extend(f"Notice {number}\n\n{body}" for body, number in notices.items())
    (ROOT / "THIRD-PARTY-NOTICES.txt").write_text(
        "Third-party redistribution notices\n\nApplication license: see LICENSE.\n"
        "Some notices cover multiple platforms and retained source assets.\n\n"
        + "\n\n" + ("\n\n" + "=" * 72 + "\n\n").join(sections) + "\n", encoding="utf-8")
    print(f"Recorded notices for {len(packages)} pinned packages, legacy assets, Outfit and the bundled runtime.")


if __name__ == "__main__":
    main()
