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
    repository = source.parent
    # Never overwrite source files, including another subtree of this repository.
    if repository.is_relative_to(destination) or (destination.is_relative_to(repository)
            and not destination.is_relative_to(repository / "artifacts")):
        raise ValueError("Use artifacts/ or an output folder outside the repository")
    destination.mkdir(parents=True, exist_ok=True)
    for name in ("index.html", "style.css", "app.js"):
        shutil.copy2(source / name, destination / name)
    shutil.copy2(source.parent / "LICENSE.txt", destination / "LICENSE.txt")
    shutil.copytree(source / "assets", destination / "assets", dirs_exist_ok=True)
    client = repository / "Windows" / "ui"
    shutil.copy2(client / "fonts/Manrope.ttf", destination / "assets/Manrope.ttf")
    shutil.copy2(repository / "licenses/Manrope-OFL.txt", destination / "assets/Manrope-OFL.txt")
    demo_version = re.search(r'<Version>([^<]+)</Version>', (repository / 'Windows/Kot.Windows.csproj').read_text()).group(1)
    landing = destination / 'index.html'
    landing.write_text(landing.read_text(encoding='utf-8').replace('{{demo-version}}', demo_version), encoding='utf-8')
    # Reuse the real client UI in an isolated browser demo, with a mock WebView bridge.
    demo = destination / "demo"
    demo.mkdir(exist_ok=True)
    markup = (client / "index.html").read_text(encoding="utf-8")
    font = base64.b64encode((client / "fonts/Manrope.ttf").read_bytes()).decode("ascii")
    markup = markup.replace('url("fonts/Manrope.ttf")', f'url("data:font/ttf;base64,{font}")')
    markup = markup.replace("style-src 'unsafe-inline'", "style-src 'self' 'unsafe-inline'")
    markup = re.sub(r"connect-src [^;]+", "connect-src 'none'", markup)
    markup = re.sub(r"media-src [^;]+", "media-src 'self'", markup)
    markup = markup.replace("font-src 'self'", "font-src data:")
    markup = markup.replace('</head>', '<link rel="stylesheet" href="demo.css">\n</head>')
    markup = markup.replace('<body', f'<body data-demo-version="{html.escape(demo_version, quote=True)}"', 1)
    markup = markup.replace('<script src="app.js"></script>', '<script src="bridge.js"></script>\n<script src="client.js"></script>')
    (demo / "index.html").write_text(markup, encoding="utf-8")
    shutil.copy2(client / "app.js", demo / "client.js")
    shutil.copy2(client / "client-tools.js", demo / "client-tools.js")
    shutil.copy2(client / "services.js", demo / "services.js")
    shutil.copy2(client / "about.js", demo / "about.js")
    shutil.copy2(client / "astra.jpg", demo / "astra.jpg")
    shutil.copy2(client / "brand-icon.svg", demo / "brand-icon.svg")
    shutil.copy2(client / "brand-icon-light.svg", demo / "brand-icon-light.svg")
    shutil.copy2(client / "startup.js", demo / "startup.js")
    shutil.copytree(client / "sounds", demo / "sounds", dirs_exist_ok=True)
    shutil.copytree(client / "flags", demo / "flags", dirs_exist_ok=True)
    shutil.copy2(source.parent / "licenses/flag-icons-MIT.txt", demo / "flags/LICENSE.txt")
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
