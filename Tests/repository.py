"""Check release consistency and source hygiene before CI or packaging."""
from pathlib import Path
from urllib.parse import unquote, urlsplit
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def check():
    errors = []

    def require(condition, message):
        if not condition:
            errors.append(message)

    def read(name):
        return (ROOT / name).read_text(encoding="utf-8-sig")

    version = re.search(r'Version\s*=\s*"(\d+\.\d+\.\d+)"', read("Core/ClientIdentity.cs")).group(1)
    project = ET.fromstring(read("Windows/Kot.Windows.csproj"))
    require(project.findtext("PropertyGroup/Version") == version, "Windows project version differs")
    manifest = ET.fromstring(read("Windows/app.manifest"))
    require(manifest.find("{urn:schemas-microsoft-com:asm.v1}assemblyIdentity").get("version") == version + ".0",
            "Windows manifest version differs")
    require(re.search(r'!define VERSION "([^"]+)"', read("Installer/Kot.nsi")).group(1) == version,
            "Installer version differs")
    require(re.search(r'id="titleVersion"[^>]*>([^<]+)', read("Windows/ui/index.html")).group(1) == version,
            "UI fallback version differs")
    history = re.split(r"(?m)^## ", read("CHANGELOG.md"))[1]
    require(history.splitlines()[0] == version, "Changelog must start with the current version")
    require(history.split("\n", 1)[1].strip() == read("Release/NOTES.md").strip(),
            "Release notes differ from the current changelog entry")
    sdk = json.loads(read("global.json"))["sdk"]["version"]
    require(bool(re.fullmatch(r"10\.\d+\.\d+", sdk)), "Pin a stable .NET 10 SDK")
    for package in project.findall("ItemGroup/PackageReference"):
        require(bool(re.fullmatch(r"\d+(?:\.\d+){2,3}", package.get("Version", ""))),
                f"Pin a stable package version: {package.get('Include')}")
        require(f"{package.get('Include')} {package.get('Version')}" in read("THIRD-PARTY.txt"),
                f"Update component notice: {package.get('Include')}")

    if (ROOT / ".git").exists() and shutil.which("git"):
        tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
        pending = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
    else:  # GitHub's source ZIP has no Git metadata or command-line dependency.
        excluded = {"bin", "obj", "__pycache__", ".build-cache", ".tools", ".local", "artifacts", "generated"}
        tracked = [str(p.relative_to(ROOT)) for p in ROOT.rglob("*") if p.is_file()
                   and not any(part in excluded or part.startswith("publish-") for part in p.relative_to(ROOT).parts)]
        pending = []
    files = sorted({name for name in tracked + pending if name and (ROOT / name).is_file()})
    for name in files:
        path = ROOT / name
        require(path.suffix.lower() not in {".exe", ".dll", ".zip", ".pem", ".pfx", ".key", ".pyc"},
                f"Binary build output or private key in source: {name}")
        require(not any(part in {"bin", "obj", "__pycache__", ".build-cache", "artifacts", "generated"}
                        for part in path.relative_to(ROOT).parts), f"Generated file in source: {name}")
        if path.suffix == ".md":
            for target in re.findall(r"\]\(([^)]+)\)", read(name)):
                target = target.split(' "', 1)[0].strip("<>")
                url = urlsplit(target)
                if url.scheme or url.netloc or not url.path:
                    continue
                resolved = (path.parent / unquote(url.path)).resolve()
                require(resolved.is_relative_to(ROOT) and resolved.exists(), f"Broken local link: {name}: {target}")
        if name.startswith(".github/workflows/") and path.suffix in {".yml", ".yaml"}:
            for action in re.findall(r"(?m)^\s*(?:-\s*)?uses:\s*(\S+)", read(name)):
                require(action.startswith("./") or bool(re.fullmatch(r"[^@]+@[0-9a-f]{40}", action)),
                        f"Unpinned GitHub Action: {name}: {action}")
            if "actions/setup-dotnet@" in read(name):
                require("global-json-file: global.json" in read(name), f"SDK selection must use global.json: {name}")

    for name in ("LICENSE.txt", "THIRD-PARTY.txt", "SECURITY.md", "licenses/GPL-3.0.txt",
                 "licenses/Manrope-OFL.txt", "licenses/flag-icons-MIT.txt", "licenses/sing-box-Go-NOTICES.txt",
                 "upstream/sing-box-1.14.2-source.tar.gz"):
        require((ROOT / name).is_file(), f"Required notice or corresponding source missing: {name}")
    require(not (ROOT / "site/assets/Manrope.ttf").exists(), "Site must use the canonical client font")
    if errors:
        raise ValueError("\n".join(errors))
    print(f"PASS repository: {version}, {len(files)} source files, versions, links, notices and pinned actions")


if __name__ == "__main__":
    try:
        check()
    except (ValueError, KeyError, AttributeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
