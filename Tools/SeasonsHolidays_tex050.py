#!/usr/bin/env python3
# Seasons & Holidays 0.5.0: simple flat icons (same style as the older ones: flat colours, dark outline).
import pathlib, math
from PIL import Image, ImageDraw

OUT = pathlib.Path(__file__).resolve().parent.parent / "SeasonsHolidays" / "Textures" / "Things" / "SH"
K = 4                    # supersampling
LINE = (74, 36, 14, 255)
W = 10 * K               # outline width


def canvas(w=256, h=256):
    im = Image.new("RGBA", (w * K, h * K), (0, 0, 0, 0))
    return im, ImageDraw.Draw(im)


def s(*v):
    return [x * K for x in v]


def save(im, name):
    w, h = im.size
    im.resize((w // K, h // K), Image.LANCZOS).save(OUT / f"{name}.png")


def ell(d, box, fill, width=W):
    d.ellipse(s(*box), fill=fill, outline=LINE, width=width)


def rect(d, box, fill, r=0, width=W):
    d.rounded_rectangle(s(*box), radius=r * K, fill=fill, outline=LINE, width=width)


def poly(d, pts, fill, width=W):
    d.polygon([(x * K, y * K) for x, y in pts], fill=fill, outline=LINE)
    d.line([(x * K, y * K) for x, y in pts + [pts[0]]], fill=LINE, width=width, joint="curve")


# mooncake: golden round cake with a pattern
im, d = canvas()
ell(d, (40, 60, 216, 216), (214, 150, 70, 255))
ell(d, (40, 46, 216, 200), (236, 178, 92, 255))
ell(d, (78, 80, 178, 166), (248, 204, 120, 255), 6 * K)
for a in range(8):
    x = 128 + 62 * math.cos(a * math.pi / 4); y = 123 + 62 * math.sin(a * math.pi / 4) * 0.85
    ell(d, (x - 9, y - 9, x + 9, y + 9), (214, 150, 70, 255), 4 * K)
save(im, "Mooncake")

# Diwali sweets: three round sweets on a leaf
im, d = canvas()
poly(d, [(28, 170), (128, 120), (228, 170), (128, 222)], (96, 160, 70, 255))
for (x, y, c) in [(92, 140, (250, 190, 70, 255)), (150, 130, (240, 120, 150, 255)), (122, 172, (250, 230, 200, 255))]:
    ell(d, (x - 34, y - 34, x + 34, y + 34), c)
    ell(d, (x - 12, y - 22, x + 4, y - 8), (255, 255, 255, 160), 0)
save(im, "Sweets")


def mug(name, body, drink, extra):
    im, d = canvas()
    rect(d, (60, 70, 176, 220), body, 18)
    d.arc(s(150, 100, 220, 180), 270, 90, fill=LINE, width=W)
    d.arc(s(150, 100, 220, 180), 270, 90, fill=body, width=W - 4 * K)
    ell(d, (64, 60, 172, 96), drink, 6 * K)
    extra(d)
    save(im, name)


def steam(d):
    for x in (90, 118, 146):
        d.arc(s(x - 10, 14, x + 10, 54), 90, 270, fill=(255, 255, 255, 200), width=5 * K)


mug("MulledWine", (200, 60, 50, 255), (130, 20, 40, 255), lambda d: (steam(d), ell(d, (96, 64, 120, 84), (240, 160, 60, 255), 4 * K)))
# punch: glass bowl
im, d = canvas()
poly(d, [(36, 100), (220, 100), (190, 200), (66, 200)], (255, 150, 120, 200))
rect(d, (100, 196, 156, 226), (210, 220, 230, 255), 6)
ell(d, (36, 86, 220, 116), (240, 90, 90, 255), 6 * K)
for (x, y) in [(90, 92), (150, 96), (122, 104)]:
    ell(d, (x - 12, y - 12, x + 12, y + 12), (250, 210, 80, 255), 4 * K)
d.line(s(170, 40, 150, 104), fill=LINE, width=8 * K)
save(im, "Punch")

# pumpkin pie: a slice-less round pie, top view
im, d = canvas()
ell(d, (28, 52, 228, 220), (222, 170, 100, 255))
ell(d, (52, 72, 204, 200), (214, 112, 40, 255), 6 * K)
for a in range(12):
    x = 128 + 88 * math.cos(a * math.pi / 6); y = 136 + 74 * math.sin(a * math.pi / 6)
    ell(d, (x - 10, y - 10, x + 10, y + 10), (236, 196, 130, 255), 4 * K)
ell(d, (110, 118, 146, 150), (255, 245, 230, 255), 4 * K)
save(im, "PumpkinPie")


def lantern(name, color, size=1.0, stick=False):
    im, d = canvas()
    cx = 128
    if stick:
        d.line(s(40, 230, cx, 40), fill=(120, 80, 40, 255), width=8 * K)
    else:
        d.line(s(cx, 10, cx, 44), fill=LINE, width=6 * K)
    rect(d, (cx - 30, 36, cx + 30, 54), (60, 40, 30, 255), 4)
    ell(d, (cx - 72 * size, 48, cx + 72 * size, 48 + 150 * size), color)
    for o in (-36, 0, 36):
        d.arc(s(cx - 30 + o * 0.4 - 20, 52, cx + 30 + o * 0.4 + 20, 48 + 146 * size), 250, 290, fill=LINE, width=3 * K)
    ell(d, (cx - 26, 90, cx + 26, 150), (255, 230, 150, 200), 0)
    rect(d, (cx - 30, 44 + 150 * size, cx + 30, 60 + 150 * size), (60, 40, 30, 255), 4)
    if not stick:
        d.line(s(cx, 60 + 150 * size, cx, 250), fill=(240, 200, 60, 255), width=6 * K)
    save(im, name)


lantern("RedLantern", (220, 40, 40, 255))
lantern("PaperLantern", (250, 200, 90, 255), 0.8, True)

# diya: clay lamp with a flame
im, d = canvas()
poly(d, [(36, 150), (220, 150), (180, 210), (76, 210)], (186, 96, 50, 255))
ell(d, (36, 132, 220, 168), (210, 120, 60, 255), 6 * K)
ell(d, (108, 64, 148, 140), (255, 200, 60, 255), 6 * K)
ell(d, (118, 96, 138, 136), (255, 250, 200, 255), 0)
save(im, "DiyaLamp")

# ofrenda: stepped altar with marigolds, candles and a skull
im, d = canvas(256, 256)
rect(d, (20, 170, 236, 236), (150, 70, 140, 255), 4)
rect(d, (50, 118, 206, 176), (230, 110, 60, 255), 4)
rect(d, (86, 70, 170, 124), (60, 140, 170, 255), 4)
for x in (36, 70, 186, 220):
    ell(d, (x - 16, 154, x + 16, 186), (250, 160, 30, 255), 5 * K)
for x in (70, 186):
    rect(d, (x - 8, 84, x + 8, 120), (250, 245, 230, 255), 2, 5 * K)
    ell(d, (x - 7, 66, x + 7, 86), (255, 200, 60, 255), 0)
ell(d, (106, 76, 150, 116), (250, 250, 245, 255), 5 * K)
ell(d, (114, 88, 124, 98), LINE, 0); ell(d, (132, 88, 142, 98), LINE, 0)
save(im, "Ofrenda")

# light garland: a drooping wire with coloured bulbs
im, d = canvas(256, 256)
pts = [(x, 70 + 60 * math.sin(math.pi * (x - 16) / 224)) for x in range(16, 241, 4)]
d.line([(x * K, y * K) for x, y in pts], fill=(50, 70, 40, 255), width=6 * K)
cols = [(240, 70, 70, 255), (250, 210, 60, 255), (80, 180, 240, 255), (110, 210, 90, 255), (240, 120, 220, 255)]
for i, x in enumerate(range(30, 240, 30)):
    y = 70 + 60 * math.sin(math.pi * (x - 16) / 224)
    c = cols[i % len(cols)]
    d.ellipse(s(x - 22, y - 2, x + 22, y + 42), fill=(c[0], c[1], c[2], 70))
    ell(d, (x - 11, y + 6, x + 11, y + 34), c, 5 * K)
save(im, "LightGarland")

# Christmas wreath: green ring, red bow, candles
im, d = canvas()
ell(d, (24, 40, 232, 232), (52, 120, 60, 255))
ell(d, (78, 94, 178, 178), (0, 0, 0, 0))
d.ellipse(s(78 + 4, 94 + 4, 178 - 4, 178 - 4), fill=(0, 0, 0, 0))
for a in range(16):
    x = 128 + 78 * math.cos(a * math.pi / 8); y = 136 + 72 * math.sin(a * math.pi / 8)
    ell(d, (x - 13, y - 9, x + 13, y + 9), (78, 150, 70, 255), 4 * K)
for a in (1, 5, 9, 13):
    x = 128 + 78 * math.cos(a * math.pi / 8); y = 136 + 72 * math.sin(a * math.pi / 8)
    ell(d, (x - 10, y - 10, x + 10, y + 10), (210, 40, 40, 255), 4 * K)
poly(d, [(128, 214), (92, 190), (92, 238)], (210, 40, 40, 255))
poly(d, [(128, 214), (164, 190), (164, 238)], (210, 40, 40, 255))
for x in (70, 186):
    rect(d, (x - 9, 20, x + 9, 64), (240, 236, 220, 255), 2, 5 * K)
    ell(d, (x - 7, 2, x + 7, 22), (255, 200, 60, 255), 0)
save(im, "XmasWreath")
print("ok")
