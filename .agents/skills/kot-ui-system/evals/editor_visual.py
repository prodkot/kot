"""Verify the Windows editor's web surface using its exported resources."""
import argparse
import json
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[1]
EDITOR = ROOT / "examples/windows-editor"


def color(value):
    h = value.lstrip("#")
    if len(h) == 3:
        h = "".join(c * 2 for c in h)
    return "rgb(" + ", ".join(str(int(h[i:i+2], 16)) for i in (0, 2, 4)) + ")"


def verify(page, tokens, scale, images):
    page.goto((EDITOR / "index.html").as_uri())
    page.evaluate("document.fonts.ready")
    session = page.context.new_cdp_session(page)
    session.send("DOM.enable")
    session.send("CSS.enable")
    doc = session.send("DOM.getDocument")["root"]["nodeId"]
    for selector in ("#card-text", "#time"):
        node = session.send("DOM.querySelector", {"nodeId":doc,"selector":selector})["nodeId"]
        fonts = session.send("CSS.getPlatformFontsForNode", {"nodeId":node})["fonts"]
        assert fonts and all(f["isCustomFont"] and "Manrope" in f["familyName"] for f in fonts), fonts
    bounds = None
    export = page.locator("#export")
    for theme in ("dark", "light"):
        def val(name):
            entry = tokens[name]
            return entry.get("light", entry["value"]) if theme == "light" else entry["value"]

        if theme == "light":
            page.locator("#theme").click()
        expect(page.locator("body")).to_have_css("background-color", color(val("bg")))
        expect(page.locator(".timeline")).to_have_css("background-color", color(val("surface")))
        expect(export).to_have_css("background-color", color(val("accent")))
        expect(export).to_have_css("color", color(val("ink")))
        expect(export).to_have_css("font-weight", str(val("weight-emphasis")))
        expect(export).to_have_css("font-size", f"{val('font-control')}px")
        expect(export).to_have_css("border-radius", f"{val('radius-compact')}px")
        expect(export).to_have_css("height", f"{val('control-height')}px")
        expect(page.locator("body")).to_have_css("font-weight", str(val("weight-body")))
        page.locator(".clip").first.click()
        expect(page.locator("#prev")).to_be_disabled()
        expect(page.locator("#prev")).to_have_css("opacity", str(val("opacity-disabled")))
        page.locator(".clip").last.click()
        expect(page.locator("#next")).to_be_disabled()
        page.locator(".clip").nth(1).click()
        expect(page.locator(".clip").nth(1)).to_have_attribute("aria-pressed","true")
        expect(page.locator("#card-text")).to_have_text("Линии города")
        expect(page.locator("#time")).to_have_text("00:03")
        page.locator("#theme").focus()
        page.keyboard.press("Tab")
        expect(export).to_be_focused()
        expect(export).to_have_css("outline-color", color(val("detail")))
        expect(export).to_have_css("outline-width", f"{val('focus-ring')['width']}px")
        page.keyboard.press("Enter")
        expect(page.locator("dialog")).to_be_visible()
        expect(page.locator("dialog")).to_have_css("padding-top", f"{val('dialog-pad')}px")
        page.keyboard.press("Escape")
        expect(export).to_be_focused()
        previous = page.locator("#prev")
        previous.hover()
        expect(previous).to_have_css("background-color", color(val("raised")))
        page.mouse.down()
        if page.evaluate("!matchMedia('(prefers-reduced-motion: reduce)').matches"):
            page.wait_for_function("""s => {
              const t=getComputedStyle(document.querySelector('#prev')).transform;
              return t !== 'none' && Math.abs(new DOMMatrix(t).a-s)<0.00001;
            }""", arg=val("scale-press")).dispose()
        else:
            expect(previous).to_have_css("transform", "none")
        page.mouse.up()
        page.locator(".clip").nth(1).click()
        page.mouse.move(0,0)
        # Capture the layout only after the pointer interaction has settled.
        expect(page.locator(".clip").nth(1)).to_have_css("transform", "none")
        positions = [page.locator(x).bounding_box() for x in (".monitor",".transport",".timeline","#export")]
        if bounds is not None:
            assert positions == bounds, (positions, bounds)
        bounds = positions
        if images:
            page.screenshot(path=images / f"{theme}-{scale}.png")
    page.locator("#export").click()
    with page.expect_download() as item:
        page.locator("#save").click()
    assert [s["seconds"] for s in json.loads(Path(item.value.path()).read_text())] == [3,5,4]
    session.detach()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--screenshots", type=Path)
    args = parser.parse_args()
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    if not (EDITOR / "KotTheme/web/kot.css").is_file():
        parser.error("Run python scripts/export_tokens.py examples/windows-editor/KotTheme first")
    tokens = json.loads((ROOT / "assets/tokens.json").read_text(encoding="utf-8"))["tokens"]
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        for scale, width, motion in [(1, 780, "no-preference"), (1.25, 520, "no-preference"), (1.5, 660, "reduce")]:
            context = browser.new_context(viewport={"width":width,"height":500}, device_scale_factor=scale,
                                          reduced_motion=motion, color_scheme="dark")
            try:
                verify(context.new_page(), tokens, scale, args.screenshots)
            finally:
                context.close()
        browser.close()
    print("PASS both themes, loaded fonts, geometry, focus, selection, press, disabled, download and 3 scales")


if __name__ == "__main__":
    main()
