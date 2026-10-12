"""Render the canonical abstract brand into both Windows icon themes."""
import base64
from pathlib import Path
import struct
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
variants = [
    ('dark', 'kot.ico', 'brand-icon.svg'),
    ('light', 'kot-light.ico', 'brand-icon-light.svg'),
]


def render(page, theme, icon_name, svg_name):
    svg = (ROOT / f'Brand/kot-abstract-{theme}.svg').read_bytes()
    (ROOT / 'Windows/ui' / svg_name).write_bytes(svg)
    if theme == 'dark':
        (ROOT / 'site/assets/favicon.svg').write_bytes(svg)
    source = 'data:image/svg+xml;base64,' + base64.b64encode(svg).decode()
    frames = []
    for size in [16, 24, 32, 48, 64, 128, 256]:
        rendered = page.evaluate('''async({source,size})=>{
          const image=new Image();image.src=source;await image.decode();
          const canvas=document.createElement('canvas');canvas.width=canvas.height=size;
          const ctx=canvas.getContext('2d');ctx.drawImage(image,0,0,size,size);
          return {pixels:Array.from(ctx.getImageData(0,0,size,size).data),png:canvas.toDataURL('image/png').split(',')[1]};
        }''', {'source': source, 'size': size})
        if size == 256:
            payload = base64.b64decode(rendered['png'])
            if theme == 'light':
                (ROOT / 'Windows/ui/brand-icon-light.png').write_bytes(payload)
        else:
            rgba = bytes(rendered['pixels'])
            row_bytes = size * 4
            pixels = bytearray()
            mask = bytearray()
            mask_bytes = ((size + 31) // 32) * 4
            for y in reversed(range(size)):
                row = rgba[y * row_bytes:(y + 1) * row_bytes]
                and_row = bytearray(mask_bytes)
                for x in range(size):
                    r, g, b, a = row[x * 4:x * 4 + 4]
                    pixels.extend([b, g, r, a])
                    if a == 0:
                        and_row[x // 8] |= 1 << (7 - x % 8)
                mask.extend(and_row)
            header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
            payload = header + pixels + mask
        frames.append((size, payload))
    offset = 6 + len(frames) * 16
    directory = bytearray(struct.pack('<HHH', 0, 1, len(frames)))
    for size, payload in frames:
        directory.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    (ROOT / 'Windows' / icon_name).write_bytes(directory + b''.join(payload for _, payload in frames))

with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page()
        for variant in variants:
            render(page, *variant)
    finally:
        browser.close()
print('Both abstract icon themes rendered at 16, 24, 32, 48, 64, 128 and 256 pixels.')
