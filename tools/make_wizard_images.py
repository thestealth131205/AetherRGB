#!/usr/bin/env python3
"""Erzeugt die Bilder links im Installer (Inno Setup) aus den Screenshots in docs/screenshots.

- wizard-editor-*.bmp  → Willkommensseite (Effekt-Editor mit Frames)
- wizard-finish-*.bmp  → Abschlussseite (Effekte & Layout, Radialer Puls)

Hochformat 164×314 (100 %) plus 1,5×/2× für hohe DPI; Inno wählt die passende Größe.
BMP (24 Bit), weil das jede Inno-Setup-6-Version lädt.

Aufruf: python3 tools/make_wizard_images.py   (braucht Pillow)
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHOTS = os.path.join(ROOT, "docs", "screenshots")
LOGO = os.path.join(ROOT, "src", "AetherRGB.App", "Assets", "AetherRGB-512.png")
OUT = os.path.join(ROOT, "installer")
FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"

BG = (8, 8, 12)
ACCENT = (124, 92, 255)  # Violett wie in der App
BASE_W, BASE_H = 164, 314
SCALES = (1, 1.5, 2)
M = 4  # Master = 4× (656×1256), danach pro Größe herunterskalieren

IMAGES = {
    # Name: (Screenshot, [Ausschnitte (l, o, r, u), untereinander], Überschrift, Unterzeile)
    "editor": ("effekt-editor-frames.png", [(525, 110, 1072, 600), (88, 742, 440, 820)],
               "Effekt-Editor", "Malen · Frames · Animation"),
    "finish": ("effekte-radialer-puls.png", [(395, 218, 990, 516), (1160, 60, 1450, 400)],
               "Effekte & Layout", "Geräte platzieren · Live-Vorschau"),
}


def master(shot, boxes, title, subtitle):
    W, H = BASE_W * M, BASE_H * M
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    pad = 12 * M

    # Logo + Schriftzug oben
    logo = Image.open(LOGO).convert("RGBA").resize((34 * M, 34 * M), Image.LANCZOS)
    img.paste(logo, (pad, pad), logo)
    d.text((pad + 40 * M, pad + 17 * M), "AetherRGB", font=ImageFont.truetype(FONT_BOLD, 17 * M),
           fill=(240, 240, 245), anchor="lm")

    # Überschrift
    y = pad + 34 * M + 16 * M
    d.text((pad, y), title, font=ImageFont.truetype(FONT_BOLD, 13 * M), fill=(240, 240, 245))
    y += 18 * M
    d.text((pad, y), subtitle, font=ImageFont.truetype(FONT, 8 * M), fill=(150, 150, 165))
    y += 16 * M

    # Screenshot-Ausschnitte mit violettem Rahmen untereinander, gemeinsam auf die Resthöhe eingepasst
    src = Image.open(os.path.join(SHOTS, shot)).convert("RGB")
    crops = [src.crop(b) for b in boxes]
    gap, b = 8 * M, 1 * M
    avail = H - y - pad
    cw = W - 2 * pad
    heights = [c.height * cw / c.width for c in crops]
    k = min(1.0, (avail - gap * (len(crops) - 1)) / sum(heights))
    top = y + (avail - (sum(heights) * k + gap * (len(crops) - 1))) / 2
    for c, ch in zip(crops, heights):
        w, h = round(cw * k), round(ch * k)
        x, t = (W - w) // 2, round(top)
        d.rounded_rectangle((x - b, t - b, x + w + b - 1, t + h + b - 1), radius=3 * M, fill=ACCENT)
        img.paste(c.resize((w, h), Image.LANCZOS), (x, t))
        top += h + gap
    return img


def main():
    for name, args in IMAGES.items():
        m = master(*args)
        for s in SCALES:
            size = (round(BASE_W * s), round(BASE_H * s))
            path = os.path.join(OUT, f"wizard-{name}-{size[0]}.bmp")
            m.resize(size, Image.LANCZOS).save(path, "BMP")
            print(path, size)


if __name__ == "__main__":
    main()
