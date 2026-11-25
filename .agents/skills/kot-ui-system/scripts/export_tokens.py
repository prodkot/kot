"""Export the bundled catalog to portable web assets and WPF resource dictionaries."""
import argparse
import json
import math
import shutil
import xml.etree.ElementTree as ET
from datetime import timedelta
from decimal import Decimal
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PRESENTATION = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XAML = "http://schemas.microsoft.com/winfx/2006/xaml"
SYSTEM = "clr-namespace:System;assembly=mscorlib"
ET.register_namespace("", PRESENTATION)
ET.register_namespace("x", XAML)
ET.register_namespace("sys", SYSTEM)


def css_value(kind, value):
    if kind == "length":
        return f"{value}px"
    if kind == "duration":
        return f"{value}ms"
    if kind == "fontStack":
        return ", ".join(value)
    if kind == "bezier":
        return "cubic-bezier(" + ",".join(map(str, value)) + ")"
    if kind == "outline":
        return f"{value['width']}px {value['style']} var(--{value['colorRef']})"
    if kind == "shadow":
        alpha = round(value["alpha"] * 255)
        return f"{value['x']}px {value['y']}px {value['blur']}px {value['spread']}px {value['color']}{alpha:02x}"
    if kind in ("color", "fontWeight", "ratio"):
        return str(value)
    raise ValueError(kind)


def export_web(tokens, dest):
    sheet = ['@font-face { font-family: Manrope; src: url("fonts/Manrope.ttf") format("truetype"); font-weight: 200 800; font-style: normal; }']
    for selector, theme, slot in [(":root", "dark", "value"), (".theme-light", "light", "light")]:
        sheet.extend([selector + " {", f"  color-scheme: {theme};"])
        for name, entry in tokens.items():
            if slot in entry:
                sheet.append(f"  --{name}: {css_value(entry['type'], entry[slot])};")
        sheet.append("}")
    dest.mkdir(parents=True, exist_ok=True)
    (dest / "kot.css").write_text("\n".join(sheet) + "\n", encoding="utf-8")
    copy_fonts(dest, ["Manrope.ttf"])


def copy_fonts(dest, names):
    fonts = dest / "fonts"
    fonts.mkdir(exist_ok=True)
    for name in [*names, "Manrope-OFL.txt"]:
        shutil.copyfile(ROOT / "assets/fonts" / name, fonts / name)


def export_wpf(tokens, dest, theme):
    doc = ET.Element(f"{{{PRESENTATION}}}ResourceDictionary")
    values = {k: v.get("light", v["value"]) if theme == "light" else v["value"] for k, v in tokens.items()}

    def add(tag, key, text=None, **attributes):
        ns = SYSTEM if tag == "Double" else PRESENTATION
        el = ET.SubElement(doc, f"{{{ns}}}{tag}", {f"{{{XAML}}}Key": "Kot." + key, **attributes})
        if text is not None:
            el.text = str(text)

    for name, entry in tokens.items():
        kind, v = entry["type"], values[name]
        if kind == "color":
            add("SolidColorBrush", name, Color=v)
        elif kind == "length":
            add("Double", name, v)
            if name.startswith("radius-"):
                add("CornerRadius", name + ".corners", v)
            if name.startswith("space-") or name in ("dialog-pad", "section-gap"):
                add("Thickness", name + ".inset", v)
        elif kind == "ratio":
            add("Double", name, v)
        elif kind == "fontStack":
            if v[0] != "Manrope":
                raise ValueError("Repackage the native fonts for the specified font family")
            add("FontFamily", name, "./fonts/#Kot Manrope " + str(values["weight-body"]))
        elif kind == "fontWeight":
            add("FontWeight", name, v)
            add("FontFamily", name + ".face", f"./fonts/#Kot Manrope {v}")
        elif kind == "duration":
            add("Duration", name, str(timedelta(milliseconds=v)))
        elif kind == "bezier":
            add("KeySpline", name, ControlPoint1=f"{v[0]},{v[1]}", ControlPoint2=f"{v[2]},{v[3]}")
        elif kind == "outline":
            if v["style"] != "solid":
                raise ValueError("Unsupported native outline style")
            add("SolidColorBrush", name, Color=values[v["colorRef"]])
            add("Thickness", name + ".width", v["width"])
        elif kind == "shadow":
            if v["spread"]:
                raise ValueError("WPF DropShadowEffect cannot express spread")
            angle = -math.degrees(math.atan2(v["y"], v["x"])) % 360
            add("DropShadowEffect", name, Color=v["color"], Opacity=str(v["alpha"]),
                BlurRadius=str(v["blur"]), ShadowDepth=f"{math.hypot(v['x'], v['y']):g}", Direction=f"{angle:g}")
        else:
            raise ValueError(kind)
    add("Double", "line-body.at-body", Decimal(str(values["line-body"])) * Decimal(str(values["font-body"])))
    ET.indent(doc, space="  ")
    dest.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(doc).write(dest / f"{theme}.xaml", encoding="utf-8", xml_declaration=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="Directory for web/ and wpf/")
    args = parser.parse_args()
    tokens = json.loads((ROOT / "assets/tokens.json").read_text(encoding="utf-8"))["tokens"]
    export_web(tokens, args.destination / "web")
    for theme in ("dark", "light"):
        export_wpf(tokens, args.destination / "wpf", theme)
    faces = [f"Manrope-{e['value']}.ttf" for e in tokens.values() if e["type"] == "fontWeight"]
    copy_fonts(args.destination / "wpf", faces)


if __name__ == "__main__":
    main()
