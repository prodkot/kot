"""Build the static site with the latest published Setup, using GitHub release JSON."""
import argparse
import base64
import html
import json
import re
import shutil
from pathlib import Path


def release_metadata(release):
    tag = release.get("tag_name", "")
    if not isinstance(tag, str) or not re.fullmatch(r"v\d+\.\d+\.\d+", tag):
        raise ValueError("Expected a versioned stable release")
    if release.get("draft") or release.get("prerelease"):
        raise ValueError("Draft and prerelease downloads are not published on the site")
    version = tag[1:]
    name = f"Kot-Setup-{version}-Windows-x64.exe"
    url = f"https://github.com/prodkot/kot/releases/download/{tag}/{name}"
    matches = [asset for asset in release.get("assets", []) if asset.get("name") == name]
    if len(matches) != 1:
        raise ValueError("Expected exactly one matching Setup")
    asset = matches[0]
    size = asset.get("size")
    if asset.get("browser_download_url") != url or type(size) is not int or size <= 0:
        raise ValueError("Invalid Setup URL or size")
    return {"version": version, "download": url, "size": size}


def build(destination, release):
    source = Path(__file__).resolve().parent
    destination = Path(destination).resolve()
    if destination == source or source.is_relative_to(destination):
        raise ValueError("Use a separate output folder")
    destination.mkdir(parents=True, exist_ok=True)
    for name in ("index.html", "style.css", "app.js"):
        shutil.copy2(source / name, destination / name)
    shutil.copy2(source.parent / "LICENSE.txt", destination / "LICENSE.txt")
    shutil.copytree(source / "assets", destination / "assets", dirs_exist_ok=True)
    # Reuse the real client UI in an isolated browser demo, with a mock WebView bridge.
    demo = destination / "demo"
    demo.mkdir(exist_ok=True)
    client = source.parent / "Windows" / "ui"
    markup = (client / "index.html").read_text(encoding="utf-8")
    font = base64.b64encode((source / "assets/Manrope.ttf").read_bytes()).decode("ascii")
    markup = markup.replace('url("fonts/Manrope.ttf")', f'url("data:font/ttf;base64,{font}")')
    markup = markup.replace("style-src 'unsafe-inline'", "style-src 'self' 'unsafe-inline'")
    markup = markup.replace("font-src 'self'", "font-src data:")
    markup = markup.replace('</head>', '<link rel="stylesheet" href="demo.css">\n</head>')
    markup = markup.replace('<body', f'<body data-demo-version="{html.escape(release["version"], quote=True)}"', 1)
    markup = markup.replace('<script src="app.js"></script>', '<script src="bridge.js"></script>\n<script src="client.js"></script>')
    (demo / "index.html").write_text(markup, encoding="utf-8")
    shutil.copy2(client / "app.js", demo / "client.js")
    for name in ("bridge.js", "demo.css"):
        shutil.copy2(source / "demo" / name, demo / name)
    (destination / "release.json").write_text(json.dumps(release, ensure_ascii=False) + "\n", encoding="utf-8")
    (destination / ".nojekyll").touch()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--release", type=Path, help="Response from GitHub's latest release API")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    metadata = release_metadata(json.loads(args.release.read_text(encoding="utf-8-sig"))) if args.release else json.loads((Path(__file__).resolve().parent / "release.json").read_text(encoding="utf-8"))
    # Validate the checked-in preview metadata through the same provenance rules.
    if not args.release:
        metadata = release_metadata({"tag_name": "v" + metadata["version"], "assets": [
            {"name": f"Kot-Setup-{metadata['version']}-Windows-x64.exe", "browser_download_url": metadata["download"], "size": metadata["size"]}
        ]})
    build(args.output, metadata)
    print(f"Site ready: kot. {metadata['version']}")
