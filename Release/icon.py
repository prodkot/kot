"""Render the site's original SVG into Windows ICO frames (no redrawing)."""
import base64
from pathlib import Path
import struct
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
svg = (ROOT / 'site/assets/favicon.svg').read_bytes()
(ROOT / 'Windows/ui/brand-icon.svg').write_bytes(svg)
source = 'data:image/svg+xml;base64,' + base64.b64encode(svg).decode()
frames = []
with sync_playwright() as p:
    browser = p.chromium.launch(args=['--no-sandbox'])
    try:
        page = browser.new_page()
        for size in [16, 24, 32, 48, 64, 128, 256]:
            rendered = page.evaluate('''async({source,size})=>{
              const image=new Image();image.src=source;await image.decode();
              const canvas=document.createElement('canvas');canvas.width=canvas.height=size;
              const ctx=canvas.getContext('2d');ctx.drawImage(image,0,0,size,size);
              return {pixels:Array.from(ctx.getImageData(0,0,size,size).data),png:canvas.toDataURL('image/png').split(',')[1]};
            }''', {'source': source, 'size': size})
            if size == 256:
                payload = base64.b64decode(rendered['png'])
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
    finally:
        browser.close()
offset = 6 + len(frames) * 16
directory = bytearray(struct.pack('<HHH', 0, 1, len(frames)))
for size, payload in frames:
    directory.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
    offset += len(payload)
(ROOT / 'Windows/kot.ico').write_bytes(directory + b''.join(payload for _, payload in frames))
print('Site icon rendered at 16, 24, 32, 48, 64, 128 and 256 pixels.')
