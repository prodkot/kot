"""Check release provenance, packaged assets and portable relative URLs."""
from copy import deepcopy
from html.parser import HTMLParser
from pathlib import Path
import json
import tempfile
import unittest
from build import release_metadata, build

ROOT = Path(__file__).resolve().parent
VERSION = "0.5.1"
NAME = f"Kot-Setup-{VERSION}-Windows-x64.exe"
URL = f"https://github.com/prodkot/kot/releases/download/v{VERSION}/{NAME}"
RELEASE = {"tag_name": f"v{VERSION}", "draft": False, "prerelease": False,
           "assets": [{"name": NAME, "browser_download_url": URL, "size": 123456}]}


class SiteTests(unittest.TestCase):
    def test_download_provenance(self):
        self.assertEqual(release_metadata(RELEASE)["download"], URL)
        for patch in ({"draft": True}, {"prerelease": True}, {"tag_name": "v1/../../bad"},
                      {"assets": []}, {"assets": RELEASE["assets"] * 2}):
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                release_metadata(RELEASE | patch)
        for patch in ({"browser_download_url": "https://example.org/setup.exe"},
                      {"browser_download_url": URL.replace("prodkot/kot", "someone/kot")},
                      {"size": -1}, {"size": True}, {"size": 0}, {"name": "portable.zip"}):
            release = deepcopy(RELEASE)
            release["assets"][0].update(patch)
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                release_metadata(release)

    def test_package_is_self_contained(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "site"
            build(dest, release_metadata(RELEASE))
            self.assertEqual(json.loads((dest / "release.json").read_text())["download"], URL)
            self.assertFalse((dest / "build.py").exists())
            self.assertTrue((dest / "assets/Manrope-OFL.txt").is_file())
            references, ids, fragments = [], set(), []

            class References(HTMLParser):
                def handle_starttag(self, tag, attrs):
                    attrs = dict(attrs)
                    if "id" in attrs:
                        if attrs["id"] in ids:
                            raise ValueError("Duplicate element ID")
                        ids.add(attrs["id"])
                    for attr in ("src", "href"):
                        value = attrs.get(attr, "")
                        if value.startswith("#") and len(value) > 1:
                            fragments.append(value[1:])
                        elif value and not value.startswith(("https://", "#")):
                            references.append(value)
            References().feed((dest / "index.html").read_text(encoding="utf-8"))
            for ref in references:
                self.assertTrue((dest / ref).is_file(), ref)
            for fragment in fragments:
                self.assertIn(fragment, ids)

    def test_demo_reuses_client_with_network_disabled(self):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory) / "site"
            build(dest, release_metadata(RELEASE))
            demo = (dest / "demo/index.html").read_text(encoding="utf-8")
            self.assertIn("connect-src 'none'", demo)
            self.assertIn("form-action 'none'", demo)
            self.assertIn("frame-src 'none'", demo)
            self.assertIn("font-src data:", demo)
            self.assertIn('data-demo-version="0.5.1"', demo)
            self.assertIn("data:font/ttf;base64,", demo)
            self.assertNotIn('fonts/Manrope.ttf', demo)
            self.assertLess(demo.index('src="bridge.js"'), demo.index('src="client.js"'))
            self.assertEqual((dest / "demo/client.js").read_bytes(), (ROOT.parent / "Windows/ui/app.js").read_bytes())
            self.assertTrue((dest / "demo/demo.css").is_file())
            self.assertTrue((dest / "demo/bridge.js").is_file())
            self.assertIn('sandbox="allow-scripts allow-forms"', (dest / "index.html").read_text(encoding="utf-8"))
            self.assertFalse((dest / "Windows").exists())
            self.assertFalse((dest / "demo/backup.json").exists())


if __name__ == "__main__":
    unittest.main()
