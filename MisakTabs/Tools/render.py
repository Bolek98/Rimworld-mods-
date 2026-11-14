"""Health-view mockup: body parts by state, organs, bones – drawn on a silhouette.
Own drawings (organs, bones, race features); the base silhouettes come from the
user's installed Nice Health Tab (preview only, not for publishing)."""
import math, numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from seg import load, segment

SS = 2  # supersampling for vector layers
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
FONTB = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

# state colours
C = dict(
    ok=(92, 190, 112), bionic=(70, 200, 255), archotech=(190, 120, 255),
    prosthetic=(176, 124, 72), implant=(70, 200, 255), missing=(235, 70, 60),
    bone=(236, 230, 214), broken=(235, 70, 60), line=(225, 232, 240), bg=(20, 24, 29),
)

def hp_col(hp):
    g, y, r = np.array(C["ok"]), np.array((240, 190, 70)), np.array((235, 70, 60))
    t = 1 - hp
    c = g * (1 - t * 2) + y * t * 2 if t < .5 else y * (2 - t * 2) + r * (t * 2 - 1)
    return tuple(int(v) for v in c)

def rgba(rgb, alpha):
    h, w = alpha.shape
    o = np.zeros((h, w, 4), np.uint8); o[:, :, :3] = rgb; o[:, :, 3] = np.clip(alpha * 255, 0, 255)
    return Image.fromarray(o)

class Vec:
    """vector layer drawn at SS× and scaled down (anti-aliased)"""
    def __init__(s, w, h): s.im = Image.new("RGBA", (w * SS, h * SS)); s.d = ImageDraw.Draw(s.im); s.w, s.h = w, h
    def P(s, pts): return [(x * SS, y * SS) for x, y in pts]
    def line(s, pts, col, w): s.d.line(s.P(pts), fill=col, width=int(w * SS), joint="curve")
    def ell(s, cx, cy, rx, ry, fill=None, outline=None, w=1):
        s.d.ellipse([(cx - rx) * SS, (cy - ry) * SS, (cx + rx) * SS, (cy + ry) * SS], fill=fill, outline=outline, width=int(w * SS))
    def poly(s, pts, fill=None, outline=None, w=1):
        s.d.polygon(s.P(pts), fill=fill)
        if outline: s.d.line(s.P(pts + [pts[0]]), fill=outline, width=int(w * SS), joint="curve")
    def cap(s, a, b, r, col):  # rounded bone segment
        s.line([a, b], col, r * 2); s.ell(*a, r, r, fill=col); s.ell(*b, r, r, fill=col)
    def out(s): return s.im.resize((s.w, s.h), Image.LANCZOS)

def taper(path, radii):
    pts = np.array(path, float); n = len(pts)
    d = np.gradient(pts, axis=0); d /= np.linalg.norm(d, axis=1)[:, None] + 1e-9
    nrm = np.stack([-d[:, 1], d[:, 0]], 1)
    r = np.interp(np.linspace(0, 1, n), np.linspace(0, 1, len(radii)), radii)[:, None]
    left = pts + nrm * r; right = pts - nrm * r
    return [tuple(p) for p in left] + [tuple(p) for p in right[::-1]]

def bezier(p0, p1, p2, n=24):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]) for t in np.linspace(0, 1, n)]

# ---------- geometry from the segmented silhouette ----------
class Body:
    def __init__(s, kind):
        s.line, s.body, s.a = load(kind)
        P = ((170, 30), (170, 170))
        s.line = np.pad(s.line, P); s.body = np.pad(s.body, P); s.a = np.pad(s.a, P)
        s.lab, s.sub, s.i = segment(s.body)
        s.h, s.w = s.body.shape
        i = s.i
        ys = np.flatnonzero((s.lab == 3).any(1))
        s.tTop, s.tBot = ys[0], ys[-1]
        row = np.flatnonzero(s.lab[min(max(i["armpit"] or 0, s.tTop + int((s.tBot - s.tTop) * .25)), s.tBot)] == 3)
        s.tL, s.tR = row[0], row[-1]
        hy = np.flatnonzero((s.lab == 1).any(1)); s.hTop, s.hBot = hy[0], hy[-1]
        hx = np.flatnonzero((s.lab == 1).any(0)); s.hL, s.hR = hx[0], hx[-1]

    def mask(s, key):
        side = {"R": 4, "L": 5}  # viewer-left = pawn's right
        lab, sub = s.lab, s.sub
        m = dict(head=lab == 1, neck=lab == 2, torso=lab == 3)
        if key in m: return m[key]
        sd, part = key[0], key[1:]
        limb = {"Arm": 4, "Forearm": 4, "Hand": 4, "Leg": 6, "Shin": 6, "Foot": 6}[part] + (0 if sd == "R" else 1)
        code = {"Arm": 8, "Forearm": 9, "Hand": 10, "Leg": 11, "Shin": 12, "Foot": 13}[part]
        return (lab == limb) & (sub == code)

    def centerline(s, keys, n=12):
        m = np.zeros_like(s.body)
        for k in keys: m |= s.mask(k)
        ys = np.flatnonzero(m.any(1))
        pts = []
        for y in np.linspace(ys[0], ys[-1], n):
            xs = np.flatnonzero(m[int(y)])
            if len(xs): pts.append(((xs[0] + xs[-1]) / 2, y))
        return pts, (ys[0], ys[-1])

    def width_at(s, keys, frac):
        m = np.zeros_like(s.body)
        for k in keys: m |= s.mask(k)
        ys = np.flatnonzero(m.any(1)); y = int(ys[0] + (ys[-1] - ys[0]) * frac)
        xs = np.flatnonzero(m[y]); return (xs[-1] - xs[0]) if len(xs) else 10

PARTS = ["head", "neck", "torso"] + [s + p for s in "RL" for p in ("Arm", "Forearm", "Hand", "Leg", "Shin", "Foot")]

def hatch(w, h, step=10, angle=1):
    yy, xx = np.mgrid[0:h, 0:w]
    return (((xx + angle * yy) % step) < 2).astype(float)

def circuit(w, h):
    yy, xx = np.mgrid[0:h, 0:w]
    return ((xx % 18 < 2) & (yy % 36 < 24) | (yy % 18 < 2) & (xx % 36 < 22)).astype(float)

def dashed_outline(mask, col):
    m = Image.fromarray((mask * 255).astype(np.uint8))
    edge = np.array(m.filter(ImageFilter.MaxFilter(5))).astype(float) / 255 - mask
    yy, xx = np.mgrid[0:mask.shape[0], 0:mask.shape[1]]
    dash = (((xx + yy) // 9) % 2 == 0)
    return rgba(col, np.clip(edge, 0, 1) * dash)

# ---------- race features (own vector drawings) ----------
def race_features(B, tags, col_line, col_fill, behind):
    v = Vec(B.w, B.h)
    hcx, hw, ht = (B.hL + B.hR) / 2, (B.hR - B.hL), B.hTop
    hh = B.hBot - B.hTop
    lw = 3
    def both(fn):
        fn(-1); fn(1)
    if behind:
        tx, ty = B.i["cx"] + 0, B.i["crotch"] - (B.i["crotch"] - B.tTop) * .12
        def tail(path, radii, fin=False):
            poly = taper(path, radii)
            v.poly(poly, fill=col_fill, outline=col_line, w=lw)
            if fin:
                e = path[-1]; v.poly([(e[0], e[1] - 6), (e[0] - 70, e[1] + 45), (e[0] + 75, e[1] + 30)], fill=col_fill, outline=col_line, w=lw)
        if "thinTail" in tags: tail(bezier((tx, ty), (tx + 160, ty + 60), (tx + 130, ty + 330), 40), [11, 8, 5, 2.5])
        if "catTail" in tags: tail(bezier((tx, ty), (tx + 170, ty + 10), (tx + 150, ty + 270), 40), [18, 16, 15, 13, 10])
        if "wolfTail" in tags: tail(bezier((tx, ty), (tx + 150, ty + 40), (tx + 150, ty + 260), 40), [18, 32, 38, 32, 10])
        if any(t in tags for t in ("foxTail", "fluffyTail")): tail(bezier((tx, ty), (tx + 200, ty - 30), (tx + 175, ty + 260), 40), [18, 48, 62, 52, 12])
        if "rabbitTail" in tags: v.ell(tx + 70, ty + 18, 38, 34, fill=col_fill, outline=col_line, w=lw)
        if "dragonTail" in tags: tail(bezier((tx, ty), (tx + 220, ty + 150), (tx + 170, ty + 390), 40), [36, 26, 14, 3])
        if "fishTail" in tags or "orcaFin" in tags: tail(bezier((tx, ty), (tx + 150, ty + 120), (tx + 140, ty + 300), 40), [30, 20, 9], fin=True)
        for wt in ("wings", "featherWings", "batWings", "mothWings"):
            if wt in tags:
                sy = B.tTop + 30
                def wing(sg):
                    x0 = B.i["cx"] + sg * 30
                    if wt == "batWings":
                        pts = [(x0, sy), (x0 + sg * 230, sy - 150), (x0 + sg * 210, sy + 40), (x0 + sg * 170, sy + 10),
                               (x0 + sg * 150, sy + 120), (x0 + sg * 100, sy + 70), (x0 + sg * 60, sy + 160)]
                    elif wt == "mothWings":
                        pts = bezier((x0, sy), (x0 + sg * 300, sy - 230), (x0 + sg * 200, sy + 40)) + \
                              bezier((x0 + sg * 200, sy + 40), (x0 + sg * 260, sy + 260), (x0, sy + 120))
                    else:
                        pts = bezier((x0, sy), (x0 + sg * 260, sy - 200), (x0 + sg * 220, sy + 120)) + \
                              [(x0 + sg * 170, sy + 200), (x0 + sg * 120, sy + 150), (x0 + sg * 80, sy + 210), (x0 + sg * 40, sy + 120)]
                    v.poly(pts, fill=col_fill, outline=col_line, w=lw)
                    if wt in ("wings", "featherWings"):
                        for k in range(4):
                            a = bezier((x0 + sg * 20, sy + 20), (x0 + sg * 150, sy - 60 + k * 30), (x0 + sg * (200 - k * 35), sy + 120 + k * 22), 10)
                            v.line(a, col_line[:3] + (110,), 2)
                both(wing)
        return v.out()
    # in front / on the head
    if "mouseEars" in tags:
        both(lambda sg: (v.ell(hcx + sg * hw * .55, ht + hh * .18, hw * .32, hw * .32, fill=col_fill, outline=col_line, w=lw),
                         v.ell(hcx + sg * hw * .55, ht + hh * .18, hw * .18, hw * .18, outline=col_line[:3] + (90,), w=2)))
    if "catEars" in tags or "wolfEars" in tags or "foxEars" in tags:
        k = {"catEars": .75, "wolfEars": .85, "foxEars": 1.0}[[t for t in ("catEars", "wolfEars", "foxEars") if t in tags][0]]
        both(lambda sg: v.poly([(hcx + sg * hw * .12, ht + hh * .12), (hcx + sg * hw * .42, ht - hh * k), (hcx + sg * hw * .5, ht + hh * .28)],
                               fill=col_fill, outline=col_line, w=lw))
    if "rabbitEars" in tags:
        both(lambda sg: v.poly(bezier((hcx + sg * hw * .08, ht + 6), (hcx + sg * hw * .45, ht - hh * 2.4), (hcx + sg * hw * .42, ht + 10), 20),
                               fill=col_fill, outline=col_line, w=lw))
    if "longPointyEars" in tags or "elfEars" in tags:
        both(lambda sg: v.poly([(hcx + sg * hw * .46, ht + hh * .38), (hcx + sg * hw * 1.05, ht + hh * .05), (hcx + sg * hw * .5, ht + hh * .6)],
                               fill=col_fill, outline=col_line, w=lw))
    if "horns" in tags or "smallHorns" in tags:
        L = 1.0 if "horns" in tags else .55
        hc = (205, 192, 168, 255)
        for sg in (-1, 1):
            p0 = (hcx + sg * hw * .26, ht + hh * .14)
            path = bezier(p0, (hcx + sg * hw * .95, ht - hh * L * .15), (hcx + sg * hw * .75, ht - hh * L * .75), 18)
            v.poly(taper(path, [hw * .12, hw * .08, 1.5]), fill=hc, outline=col_line, w=2)
    if "antlers" in tags:
        both(lambda sg: (v.line(bezier((hcx + sg * hw * .2, ht + 8), (hcx + sg * hw * .6, ht - hh * .5), (hcx + sg * hw * .9, ht - hh * .9)), col_line, 7),
                         v.line([(hcx + sg * hw * .55, ht - hh * .35), (hcx + sg * hw * .45, ht - hh * .8)], col_line, 6)))
    if "antennae" in tags:
        both(lambda sg: (v.line(bezier((hcx + sg * hw * .12, ht + 6), (hcx + sg * hw * .3, ht - hh * .7), (hcx + sg * hw * .7, ht - hh * .9)), col_line, 4),
                         v.ell(hcx + sg * hw * .7, ht - hh * .9, 7, 7, fill=col_line)))
    if "gills" in tags:
        for sg in (-1, 1):
            for k in range(3):
                a = (-.5 + k * .5)
                p0 = (hcx + sg * hw * .45, ht + hh * (.45 + k * .1))
                p2 = (p0[0] + sg * hw * .65, p0[1] - hh * (.35 - k * .25))
                v.poly(taper(bezier(p0, (p0[0] + sg * hw * .3, p0[1] - 20), p2, 16), [7, 9, 3]), fill=(230, 120, 160, 255), outline=col_line, w=2)
    if "wisp" in tags:
        g = Vec(B.w, B.h); g.ell(hcx + hw * 1.3, ht + hh * .2, 26, 26, fill=(120, 220, 255, 255))
        img_w = g.out().filter(ImageFilter.GaussianBlur(10)); v.im.alpha_composite(img_w.resize(v.im.size))
        v.ell(hcx + hw * 1.3, ht + hh * .2, 12, 12, fill=(220, 250, 255, 255))
    if "halo" in tags:
        v.ell(hcx, ht - hh * .22, hw * .45, hh * .1, outline=(255, 225, 120), w=6)
    return v.out()

# ---------- views ----------
def base_canvas(B):
    return Image.new("RGBA", (B.w, B.h), C["bg"] + (255,))

def draw_outline(B, img, alpha=1.0, glow=True):
    lineI = rgba(C["line"], np.clip(B.line * 1.6 * alpha, 0, 1))
    if glow: img.alpha_composite(lineI.filter(ImageFilter.GaussianBlur(5)))
    img.alpha_composite(lineI)

def view_parts(B, st, tags):
    img = base_canvas(B)
    img.alpha_composite(race_features(B, tags, (210, 218, 228, 255), (60, 70, 82, 255), True))
    h, w = B.h, B.w
    img.alpha_composite(rgba(C["bg"], B.body.astype(float)))
    hat = hatch(w, h); circ = circuit(w, h)
    missing_union = np.zeros((h, w), bool)
    for p in PARTS:
        m = B.mask(p).astype(float)
        s = st.get(p, ("ok", 1.0))
        kind, val = s
        if kind == "ok": img.alpha_composite(rgba(hp_col(val), m * .62))
        elif kind == "missing":
            missing_union |= m > 0
            img.alpha_composite(rgba(C["missing"], m * hat * .45))
            img.alpha_composite(dashed_outline(m, C["missing"]))
        else:
            col = C[kind]
            img.alpha_composite(rgba(col, m * .55))
            pat = circ if kind in ("bionic", "archotech") else hat
            img.alpha_composite(rgba(tuple(min(255, c + 60) for c in col), m * pat * .7))
            if kind == "archotech": img.alpha_composite(rgba(col, m).filter(ImageFilter.GaussianBlur(10)))
    # outline only where the part exists
    li = B.line.copy(); li[ndilate(missing_union, 4)] *= .0
    lineI = rgba(C["line"], np.clip(li * 1.6, 0, 1))
    img.alpha_composite(lineI.filter(ImageFilter.GaussianBlur(5))); img.alpha_composite(lineI)
    # wounds on damaged parts
    v = Vec(w, h); rng = np.random.default_rng(3)
    for p in PARTS:
        kind, val = st.get(p, ("ok", 1.0))
        if kind == "ok" and val < 1:
            ys, xs = np.nonzero(B.mask(p))
            for _ in range(1 + int((1 - val) * 4)):
                k = rng.integers(len(ys)); x, y = xs[k], ys[k]; L = 14
                v.line([(x - L, y - 5), (x + L, y + 5)], (150, 20, 20, 255), 4)
                for q in range(-2, 3): v.line([(x + q * 6 - 2, y + q * 2 - 6), (x + q * 6 + 2, y + q * 2 + 6)], (240, 210, 200, 220), 1.5)
    img.alpha_composite(v.out())
    img.alpha_composite(race_features(B, tags, (210, 218, 228, 255), (60, 70, 82, 255), False))
    return img

def ndilate(m, r):
    return np.array(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(r * 2 + 1))) > 0

def ghost(B, tags, a=.22):
    img = base_canvas(B)
    img.alpha_composite(race_features(B, tags, (110, 120, 132, 255), (38, 44, 52, 255), True))
    img.alpha_composite(rgba(C["bg"], B.body.astype(float)))
    img.alpha_composite(rgba((70, 82, 96), B.body * a))
    draw_outline(B, img, .55, glow=False)
    img.alpha_composite(race_features(B, tags, (110, 120, 132, 255), (38, 44, 52, 255), False))
    return img

def organ_col(st, key, default=(214, 96, 104)):
    kind, val = st.get(key, ("ok", 1.0))
    if kind == "missing": return None, kind
    if kind in ("bionic", "implant", "archotech"): return C[kind], kind
    return (hp_col(val) if val < 1 else default), kind

def view_organs(B, st, tags):
    img = ghost(B, tags)
    v = Vec(B.w, B.h)
    cx = B.i["cx"]; tT, tB = B.tTop, B.tBot; tw = B.tR - B.tL; th = tB - tT
    hcx = (B.hL + B.hR) / 2; hh = B.hBot - B.hTop; hw = B.hR - B.hL
    O = {}
    # brain
    O["brain"] = ("ell", (hcx, B.hTop + hh * .32, hw * .36, hh * .24))
    O["Reye"] = ("ell", (hcx - hw * .2, B.hTop + hh * .58, hw * .11, hh * .065))
    O["Leye"] = ("ell", (hcx + hw * .2, B.hTop + hh * .58, hw * .11, hh * .065))
    def lung(sg):
        return (bezier((cx + sg * tw * .07, tT + th * .1), (cx + sg * tw * .2, tT - th * .02), (cx + sg * tw * .34, tT + th * .14)) +
                bezier((cx + sg * tw * .34, tT + th * .14), (cx + sg * tw * .48, tT + th * .4), (cx + sg * tw * .36, tT + th * .45)) +
                bezier((cx + sg * tw * .36, tT + th * .45), (cx + sg * tw * .2, tT + th * .4), (cx + sg * tw * .07, tT + th * .42)))
    O["Rlung"] = ("poly", lung(-1)); O["Llung"] = ("poly", lung(1))
    O["heart"] = ("poly", bezier((cx - tw * .02, tT + th * .2), (cx + tw * .28, tT + th * .22), (cx + tw * .06, tT + th * .42)) + bezier((cx + tw * .06, tT + th * .42), (cx - tw * .14, tT + th * .3), (cx - tw * .02, tT + th * .2)))
    O["liver"] = ("poly", bezier((cx - tw * .4, tT + th * .5), (cx - tw * .1, tT + th * .4), (cx + tw * .14, tT + th * .47)) +
                  bezier((cx + tw * .14, tT + th * .47), (cx - tw * .05, tT + th * .62), (cx - tw * .36, tT + th * .62)))
    O["stomach"] = ("poly", bezier((cx + tw * .08, tT + th * .5), (cx + tw * .5, tT + th * .44), (cx + tw * .22, tT + th * .68)) + [(cx + tw * .06, tT + th * .62)])
    O["Rkidney"] = ("ell", (cx - tw * .22, tT + th * .7, tw * .07, th * .055))
    O["Lkidney"] = ("ell", (cx + tw * .22, tT + th * .7, tw * .07, th * .055))
    default = dict(brain=(232, 160, 170), Reye=(240, 240, 240), Leye=(240, 240, 240), Rlung=(232, 130, 140), Llung=(232, 130, 140),
                   heart=(214, 60, 70), liver=(150, 60, 50), stomach=(214, 150, 120), Rkidney=(170, 70, 70), Lkidney=(170, 70, 70))
    order = ["Rkidney", "Lkidney", "liver", "stomach", "Rlung", "Llung", "heart", "brain", "Reye", "Leye"]
    glow = Vec(B.w, B.h)
    for k in order:
        typ, g = O[k]
        col, kind = organ_col(st, k, default[k])
        dash = col is None
        fill = None if dash else col + (235,)
        outl = (C["missing"] + (255,)) if dash else (255, 255, 255, 200)
        if typ == "ell":
            v.ell(*g, fill=fill, outline=outl, w=2.5)
            if kind in ("bionic", "implant", "archotech"): glow.ell(*g, fill=col + (255,))
        else:
            v.poly(g, fill=fill, outline=outl, w=2.5)
            if kind in ("bionic", "implant", "archotech"): glow.poly(g, fill=col + (255,))
        if kind in ("bionic", "implant", "archotech"):
            cxo, cyo = (g[0], g[1]) if typ == "ell" else (np.mean([p[0] for p in g]), np.mean([p[1] for p in g]))
            for d in range(-1, 2): v.line([(cxo - 10, cyo + d * 7), (cxo + 10, cyo + d * 7)], (255, 255, 255, 200), 1.5)
        if dash:
            cxo, cyo = (g[0], g[1]) if typ == "ell" else (np.mean([p[0] for p in g]), np.mean([p[1] for p in g]))
            v.line([(cxo - 9, cyo - 9), (cxo + 9, cyo + 9)], C["missing"] + (255,), 3); v.line([(cxo - 9, cyo + 9), (cxo + 9, cyo - 9)], C["missing"] + (255,), 3)
    img.alpha_composite(glow.out().filter(ImageFilter.GaussianBlur(9)))
    img.alpha_composite(v.out())
    return img, O

def view_bones(B, st, tags, only=None):
    img = ghost(B, tags, .16) if only is None else Image.new("RGBA", (B.w, B.h))
    want = lambda k: only is None or only == k
    v = Vec(B.w, B.h)
    BC = C["bone"] + (255,)
    def col(key):
        kind, val = st.get(key, ("ok", 1.0))
        if kind == "missing": return None, kind
        if kind == "broken": return C["broken"] + (255,), kind
        if kind in ("bionic", "archotech", "prosthetic"): return C[kind] + (255,), kind
        return BC, kind
    cx = B.i["cx"]; tT, tB = B.tTop, B.tBot; tw = B.tR - B.tL; th = tB - tT
    hcx = (B.hL + B.hR) / 2; hh = B.hBot - B.hTop; hw = B.hR - B.hL
    cracks = []
    # skull
    c, k = col("skull")
    if c and want("skull"):
        v.ell(hcx, B.hTop + hh * .42, hw * .42, hh * .4, fill=c)
        v.poly([(hcx - hw * .3, B.hTop + hh * .62), (hcx + hw * .3, B.hTop + hh * .62), (hcx + hw * .2, B.hTop + hh * .95), (hcx - hw * .2, B.hTop + hh * .95)], fill=c)
        for sg in (-1, 1): v.ell(hcx + sg * hw * .17, B.hTop + hh * .5, hw * .11, hh * .09, fill=C["bg"] + (255,))
        v.poly([(hcx, B.hTop + hh * .62), (hcx - 6, B.hTop + hh * .72), (hcx + 6, B.hTop + hh * .72)], fill=C["bg"] + (255,))
        if k == "broken": cracks.append((hcx + hw * .1, B.hTop + hh * .2, 28))
    # spine
    c, k = col("spine")
    if c and want("spine"):
        y = B.hBot - 4
        while y < tB - th * .08:
            v.ell(cx, y, 8, 6, fill=c); y += 15
        if k == "broken": cracks.append((cx, tT + th * .6, 20))
    # ribs + sternum
    c, k = col("ribs")
    if c and want("ribs"):
        for r in range(7):
            y = tT + th * (.1 + r * .055)
            for sg in (-1, 1):
                v.line(bezier((cx, y), (cx + sg * tw * .48, y - th * .03), (cx + sg * tw * (.36 - r * .015), y + th * .08), 14), c, 6)
        v.line([(cx, tT + th * .08), (cx, tT + th * .42)], c, 10)
        if k == "broken": cracks.append((cx - tw * .3, tT + th * .26, 22))
    # clavicles
    c, _ = col("clavicle")
    for sg in (-1, 1):
        if c and want("Rclav" if sg < 0 else "Lclav"): v.line(bezier((cx, tT + th * .06), (cx + sg * tw * .25, tT), (cx + sg * tw * .5, tT + th * .03), 10), BC, 9)
    # pelvis
    c, k = col("pelvis")
    if c and want("pelvis"):
        py = B.i["crotch"] - th * .1
        for sg in (-1, 1):
            v.poly(bezier((cx, py - th * .08), (cx + sg * tw * .55, py - th * .2), (cx + sg * tw * .2, py + th * .1), 16) + [(cx, py + th * .04)], fill=c)
            v.ell(cx + sg * tw * .2, py + th * .0, tw * .06, th * .04, fill=C["bg"] + (255,))
        if k == "broken": cracks.append((cx + tw * .2, py, 20))
    # limbs
    for side in "RL":
        for limb, parts, bones in (("arm", ("Arm", "Forearm", "Hand"), ("humerus", "radius", "hand")),
                                   ("leg", ("Leg", "Shin", "Foot"), ("femur", "tibia", "foot"))):
            pts, (y0, y1) = B.centerline([side + p for p in parts], 40)
            if not pts: continue
            def at(f):
                y = y0 + (y1 - y0) * f
                best = min(pts, key=lambda p: abs(p[1] - y)); return best
            arm = limb == "arm"
            cuts = (.03, .43, .48, .78, .82, .97) if arm else (.02, .45, .5, .86, .9, .99)
            wd = B.width_at([side + parts[0]], .3)
            r1 = max(6, wd * (.17 if arm else .12))
            for (a, b), bn, part in zip(((cuts[0], cuts[1]), (cuts[2], cuts[3]), (cuts[4], cuts[5])), bones, parts):
                key = side + bn
                kind, val = st.get(side + part, ("ok", 1))
                if kind == "missing": continue
                c, bk = col(key)
                if kind in ("bionic", "archotech", "prosthetic"): c = C[kind] + (255,)
                if c is None or not want(key): continue
                A, Bp = at(a), at(b)
                if bn in ("hand", "foot"):
                    for q in range(-2, 3):
                        dx = q * r1 * .7
                        v.cap((A[0] + dx * .5, A[1]), (Bp[0] + dx, Bp[1]), r1 * .35, c)
                elif bn in ("radius", "tibia"):
                    v.cap((A[0] - r1 * .5, A[1]), (Bp[0] - r1 * .5, Bp[1]), r1 * .55, c)
                    v.cap((A[0] + r1 * .6, A[1]), (Bp[0] + r1 * .6, Bp[1]), r1 * .4, c)
                else:
                    v.cap(A, Bp, r1 * .6, c); v.ell(*A, r1 * .95, r1 * .85, fill=c); v.ell(*Bp, r1 * .85, r1 * .75, fill=c)
                if bk == "broken": cracks.append(((A[0] + Bp[0]) / 2, (A[1] + Bp[1]) / 2, r1 * 1.6))
    img.alpha_composite(v.out())
    cv = Vec(B.w, B.h)
    for x, y, s in cracks:
        zz = [(x - s, y - s * .3), (x - s * .4, y + s * .2), (x, y - s * .25), (x + s * .4, y + s * .25), (x + s, y - s * .2)]
        cv.line(zz, (20, 0, 0, 255), 5); cv.line(zz, C["broken"] + (255,), 3)
        cv.ell(x, y, s * 1.5, s * 1.5, outline=C["broken"] + (200,), w=2)
    img.alpha_composite(cv.out())
    return img
