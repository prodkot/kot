"""Check exported web/WPF tokens offline. Optionally compare every role to upstream CSS."""
import argparse
import hashlib
import json
import math
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"


def read_sheet(path):
    scopes = {":root": {}, ".theme-light": {}}
    css = re.sub(r"/\*.*?\*/", "", path.read_text(encoding="utf-8"), flags=re.S)
    for selector, body in re.findall(r"([^{}]+)\{([^{}]*)\}", css):
        declarations = re.findall(r"--([\w-]+)\s*:\s*([^;}]+)", body)
        if declarations:
            scopes[selector.strip()].update((key, v.strip()) for key, v in declarations)
    return scopes


def rgba(value):
    h = value.lstrip("#").lower()
    if len(h) in (3, 4):
        h = "".join(c * 2 for c in h)
    return h if len(h) == 8 else h + "ff"


def decode(kind, value):
    if kind == "color":
        return rgba(value)
    if kind == "duration":
        return float(value[:-2]) if value.endswith("ms") else float(value.removesuffix("s")) * 1000
    if kind == "length":
        assert value.endswith("px"), value
        return float(value[:-2])
    if kind in ("fontWeight", "ratio"):
        return float(value)
    if kind == "fontStack":
        return [s.strip() for s in value.split(",")]
    if kind == "bezier":
        return list(map(float, re.fullmatch(r"cubic-bezier\(([^)]+)\)", value)[1].split(",")))
    if kind == "outline":
        width, style, name = value.split()
        return {"width": float(width.removesuffix("px")), "style": style,
                "colorRef": re.fullmatch(r"var\(--([\w-]+)\)", name)[1]}
    if kind == "shadow":
        *numbers, color = value.split()
        offsets = [float(x.removesuffix("px")) for x in numbers]
        if len(offsets) == 3:
            offsets.append(0)
        value = rgba(color)
        return dict(zip(["x", "y", "blur", "spread"], offsets), color="#" + value[:6],
                    alpha=int(value[6:], 16) / 255)
    raise ValueError(kind)


def check_css(path, entries):
    scopes = read_sheet(path)
    assert set(scopes[":root"]) == set(entries), (path, set(scopes[":root"]) ^ set(entries))
    light = {k for k, e in entries.items() if "light" in e}
    assert set(scopes[".theme-light"]) == light, (path, light ^ set(scopes[".theme-light"]))
    for scope, slot in [(":root", "value"), (".theme-light", "light")]:
        for key, raw in scopes[scope].items():
            kind, expected = entries[key]["type"], entries[key][slot]
            if kind == "color":
                expected = rgba(expected)
            assert decode(kind, raw) == expected, (path, slot, key, raw, expected)


def check_wpf(path, entries, theme):
    res = {el.attrib[KEY]: el for el in ET.parse(path).getroot()}
    values = {k: e.get("light", e["value"]) if theme == "light" else e["value"] for k,e in entries.items()}
    for key, e in entries.items():
        name = "Kot." + key
        node, kind, v = res[name], e["type"], values[key]
        if kind == "color":
            assert rgba(node.get("Color")) == rgba(v), name
        elif kind in ("length", "ratio", "fontWeight"):
            assert float(node.text) == v, name
            if kind == "fontWeight":
                assert res[name + ".face"].text == "./fonts/#Kot Manrope " + str(v), name
                assert (path.parent / f"fonts/Manrope-{v}.ttf").is_file(), name
            if key.startswith("radius-"):
                assert float(res[name + ".corners"].text) == v, name
        elif kind == "duration":
            h, m, s = map(float, node.text.split(":"))
            assert (h * 3600 + m * 60 + s) * 1000 == v, name
        elif kind == "fontStack":
            assert node.text == "./fonts/#Kot Manrope " + str(values["weight-body"]), name
        elif kind == "bezier":
            assert [float(x) for a in ("ControlPoint1", "ControlPoint2") for x in node.get(a).split(",")] == v, name
        elif kind == "outline":
            assert rgba(node.get("Color")) == rgba(values[v["colorRef"]]), name
            assert float(res[name + ".width"].text) == v["width"], name
        elif kind == "shadow":
            assert rgba(node.get("Color")) == rgba(v["color"]) and float(node.get("Opacity")) == v["alpha"], name
            angle = math.radians(float(node.get("Direction")))
            depth = float(node.get("ShadowDepth"))
            assert math.isclose(depth * math.cos(angle), v["x"], abs_tol=1e-10), name
            assert math.isclose(-depth * math.sin(angle), v["y"], abs_tol=1e-10), name
            assert float(node.get("BlurRadius")) == v["blur"] and v["spread"] == 0, name
    assert Decimal(res["Kot.line-body.at-body"].text) == Decimal(str(values["line-body"])) * Decimal(str(values["font-body"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, help="Current upstream tokens.css")
    args = parser.parse_args()
    catalog = json.loads((ROOT / "assets/tokens.json").read_text(encoding="utf-8"))
    entries = catalog["tokens"]
    if args.source:
        check_css(args.source, entries)
        digest = hashlib.sha256(args.source.read_bytes()).hexdigest()
        if digest != catalog["source"]["sha256"]:
            sys.exit("Values match but upstream file changed. Review the revision and refresh SHA-256.")
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp)
        subprocess.run([sys.executable, str(ROOT / "scripts/export_tokens.py"), str(out)], check=True)
        check_css(out / "web/kot.css", entries)
        for theme in ("dark", "light"):
            check_wpf(out / f"wpf/{theme}.xaml", entries, theme)
    print(f"PASS {len(entries)} roles, light overrides, web and WPF in both themes")


if __name__ == "__main__":
    main()
