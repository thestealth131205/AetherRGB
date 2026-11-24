#!/usr/bin/env python3
"""Erzeugt das AetherRGB-Logo (assets/AetherRGB.ico + PNGs).

Motiv: Haus-Umriss mit Rot→Grün→Blau-Verlauf auf schwarzem Grund, darin drei sich
additiv überlagernde Lichtkreise (R, G, B → Weiß in der Mitte). Wird mit 4× Supersampling
gezeichnet und pro Icon-Größe einzeln herunterskaliert, damit auch 16 px scharf bleiben.

Aufruf: python3 tools/make_icon.py   (braucht Pillow)
"""
import math
import os
from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "AetherRGB.App", "Assets")
S = 1024  # Master-Auflösung


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def rgb_gradient(size):
    """Waagerechter Verlauf über die Hausbreite: Rot (links) → Grün (Mitte) → Blau (rechts)."""
    w = h = size
    grad = Image.new("RGB", (w, h))
    px = grad.load()
    stops = [(255, 35, 55), (35, 230, 85), (40, 105, 255)]
    for y in range(h):
        for x in range(w):
            t = min(1.0, max(0.0, (x / (w - 1) - 0.2) / 0.6 + (y / (h - 1) - 0.5) * 0.15))
            if t < 0.5:
                px[x, y] = lerp(stops[0], stops[1], t / 0.5)
            else:
                px[x, y] = lerp(stops[1], stops[2], (t - 0.5) / 0.5)
    return grad


def house_mask(size, stroke, simple):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    c = size / 2
    roof_top = size * (0.17 if simple else 0.19)
    eave_y = size * 0.47
    left, right = size * 0.15, size * 0.85
    wall_l, wall_r = size * 0.25, size * 0.75
    bottom = size * 0.83
    pts = [(left, eave_y), (c, roof_top), (right, eave_y)]
    d.line(pts, fill=255, width=stroke, joint="curve")
    for p in (pts[0], pts[2]):
        d.ellipse([p[0] - stroke / 2, p[1] - stroke / 2, p[0] + stroke / 2, p[1] + stroke / 2], fill=255)
    d.line([(wall_l, eave_y - stroke * 0.3), (wall_l, bottom), (wall_r, bottom), (wall_r, eave_y - stroke * 0.3)],
           fill=255, width=stroke, joint="curve")
    return m


def rgb_circles(size, simple):
    """Drei additive Lichtkreise im Haus."""
    img = Image.new("RGB", (size, size), (0, 0, 0))
    cx, cy = size * 0.5, size * 0.625
    r = size * (0.115 if not simple else 0.13)
    off = r * 0.62
    cols = [(255, 30, 50), (30, 235, 80), (40, 100, 255)]
    for i, col in enumerate(cols):
        a = math.radians(-90 + i * 120)
        x, y = cx + off * math.cos(a), cy + off * math.sin(a) * 0.95
        layer = Image.new("RGB", (size, size), (0, 0, 0))
        ImageDraw.Draw(layer).ellipse([x - r, y - r, x + r, y + r], fill=col)
        img = ImageChops.add(img, layer)
    return img


def render(px):
    simple = px <= 32
    size = max(px * 4, 256)
    # Hintergrund: schwarzes abgerundetes Quadrat
    bg_mask = Image.new("L", (size, size), 0)
    radius = int(size * (0.16 if not simple else 0.12))
    ImageDraw.Draw(bg_mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    canvas = Image.new("RGB", (size, size), (0, 0, 0))

    stroke = int(size * (0.085 if not simple else 0.11))
    hm = house_mask(size, stroke, simple)
    grad = rgb_gradient(size)

    if not simple:  # weicher Glow um den Umriss
        glow = Image.composite(grad, Image.new("RGB", (size, size)), hm.filter(ImageFilter.GaussianBlur(size * 0.035)))
        glow = glow.point(lambda v: int(v * 0.55))
        canvas = ImageChops.add(canvas, glow)
    canvas = Image.composite(grad, canvas, hm)

    circles = rgb_circles(size, simple)
    if not simple:
        canvas = ImageChops.add(canvas, circles.filter(ImageFilter.GaussianBlur(size * 0.03)).point(lambda v: int(v * 0.45)))
    canvas = ImageChops.lighter(canvas, circles)

    out = canvas.convert("RGBA")
    out.putalpha(bg_mask)
    return out.resize((px, px), Image.LANCZOS)


def main():
    os.makedirs(OUT, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = {s: render(s) for s in sizes}
    imgs[256].save(os.path.join(OUT, "AetherRGB.ico"), format="ICO",
                   sizes=[(s, s) for s in sizes], append_images=[imgs[s] for s in sizes[:-1]])
    render(512).save(os.path.join(OUT, "AetherRGB-512.png"))
    imgs[64].save(os.path.join(OUT, "AetherRGB-64.png"))
    print("geschrieben nach", OUT)


if __name__ == "__main__":
    main()
