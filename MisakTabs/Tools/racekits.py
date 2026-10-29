"""0.10.0: race figures from the user's generated pictures (Grok), one kit per race / sex / age.
The full silhouette (with wings, tails, ears …) is the drawn figure; the body alone (for body parts,
organs and bones) is the part of it that lies on a matching human figure (Clovek_F / _M / _Kid …),
so wings and tails don't confuse the arm and leg detection.

usage: python3 racekits.py <folder with Race_X.jpg> <out Anatomy folder> [names…]"""
import os, sys, glob
import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage as ndi
import render
from render import *

W, H = 512, 1024          # frame of the body pictures before padding (as genbody)
PAD = 170                 # Body pads by 170 on every side

def mask_of(im):
    a = np.array(im.convert("RGB")).astype(int)
    m = np.abs(a - 255).sum(2) > 45
    m = ndi.binary_opening(m, iterations=1)
    m = ndi.binary_fill_holes(ndi.binary_closing(m, iterations=3))
    lab, n = ndi.label(m)
    sizes = ndi.sum(m, lab, range(1, n + 1))
    m = lab == (np.argmax(sizes) + 1)
    m = np.array(Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(2))).astype(float) / 255 > .5
    gray = a[:, :, 0] * .3 + a[:, :, 1] * .59 + a[:, :, 2] * .11
    return m, gray

def fit(m, gray, top, bot, maxw):
    """into the padded frame (W+2PAD × H+2PAD); figures wider than maxw get smaller"""
    ys = np.flatnonzero(m.any(1)); xs = np.flatnonzero(m.any(0))
    m = m[ys[0]:ys[-1] + 1, xs[0]:xs[-1] + 1]; gray = gray[ys[0]:ys[-1] + 1, xs[0]:xs[-1] + 1]
    h, w = m.shape
    s = (bot - top) / h
    if w * s > maxw: s = maxw / w
    nw, nh = max(1, int(w * s)), max(1, int(h * s))
    FW, FH = W + 2 * PAD, H + 2 * PAD
    M = Image.fromarray((m * 255).astype(np.uint8)).resize((nw, nh), Image.LANCZOS)
    G = Image.fromarray(gray.astype(np.uint8)).resize((nw, nh), Image.LANCZOS)
    cm = Image.new("L", (FW, FH)); cg = Image.new("L", (FW, FH), 255)
    x0 = (FW - nw) // 2; y0 = PAD + bot - nh      # feet stay on the floor line
    cm.paste(M, (x0, y0)); cg.paste(G, (x0, y0))
    return np.array(cm).astype(float) / 255, np.array(cg).astype(float)

def line_of(am, g):
    sm = np.array(Image.fromarray((am * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))).astype(float) / 255
    body = sm > .5
    B = Image.fromarray((body * 255).astype(np.uint8))
    edge = np.array(B.filter(ImageFilter.MaxFilter(5))).astype(float) / 255 - np.array(B.filter(ImageFilter.MinFilter(3))).astype(float) / 255
    edge = np.array(Image.fromarray((np.clip(edge, 0, 1) * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(.7))).astype(float) / 255
    gx = ndi.sobel(ndi.gaussian_filter(g, 1.5), 1); gy = ndi.sobel(ndi.gaussian_filter(g, 1.5), 0)
    inner = np.clip(np.hypot(gx, gy) / 120, 0, 1) * ndi.binary_erosion(body, iterations=6) * 0.2
    line = np.clip(edge + inner, 0, 1)
    shade = np.clip(1 - ndi.distance_transform_edt(body) / 16, 0, 1) * body * 0.4
    return line, body, np.maximum(line, shade)

def frame_for(name):
    kid = name.endswith("_Kid")
    return (150, 945) if kid else (75, 945)

GRAY = {}   # name -> grey picture in the padded frame (for skin masks)

def build(path):
    name = os.path.basename(path).rsplit(".", 1)[0]
    top, bot = frame_for(name)
    m, g = mask_of(Image.open(path))
    am, gg = fit(m, g, top, bot, maxw=720)
    GRAY[name] = gg
    return line_of(am, gg)

# 0.10.1: races whose dark wings lie behind the arms – the body is found by its grey
# (wings are much darker), so the arms get their parts even when they lie over the wings.
SKIN_RACES = ("Outerm",)

def skin_of(full, gray):
    gs = ndi.gaussian_filter(gray, 3)
    FH = full.shape[0]
    core = full[int(FH * .35):int(FH * .55)]
    vals = gs[int(FH * .35):int(FH * .55)][core]
    med = np.median(vals[vals > np.percentile(vals, 40)])
    sk = full & (gs > med - 40) & (gs < med + 45)
    sk = ndi.binary_opening(sk, iterations=2)
    sk = ndi.binary_fill_holes(ndi.binary_closing(sk, iterations=2))
    lab, n = ndi.label(sk)
    sizes = ndi.sum(sk, lab, range(1, n + 1))
    return lab == (np.argmax(sizes) + 1)

def refine_arms(lab, skin):
    """every skin pixel outside head/neck/torso/legs that touches an arm becomes arm; then each
    arm is cut by distance from the shoulder: 45 % upper arm, 35 % forearm, rest hand"""
    out = lab.copy()
    out[np.isin(lab, (4, 5, 6, 7, 8, 9))] = 0          # arms are rebuilt from the skin
    body = np.isin(lab, (1, 2, 3, 10, 11, 12, 13, 14, 15))
    legs = ndi.binary_dilation(np.isin(lab, (10, 11, 12, 13, 14, 15)), iterations=5)
    free = ndi.binary_opening(skin & ~body & ~legs, iterations=3)   # no thin strips along the legs or wing edges
    comp, n = ndi.label(free)
    for first in (4, 7):
        arm = np.isin(lab, (first, first + 1, first + 2)) & skin
        ids = np.unique(comp[arm & (comp > 0)])
        region = np.isin(comp, ids[ids > 0])
        if not region.any(): continue
        torso = ndi.binary_dilation(lab == 3, iterations=4)
        ys = np.flatnonzero(region.any(1))
        seed = region & torso
        seed[ys[0] + int((ys[-1] - ys[0]) * .2):] = False      # only the shoulder, not the whole side
        if not seed.any():
            seed = region.copy(); seed[ys[0] + 6:] = False
        if not seed.any(): continue
        # geodesic distance inside the arm region
        dist = np.full(lab.shape, np.inf); dist[seed] = 0
        frontier = seed.copy(); d = 0
        while frontier.any() and d < 2000:
            d += 1
            nxt = ndi.binary_dilation(frontier) & region & np.isinf(dist)
            dist[nxt] = d; frontier = nxt
        dmax = dist[region & np.isfinite(dist)].max()
        r = region & np.isfinite(dist)
        out[r] = np.where(dist[r] < .45 * dmax, first, np.where(dist[r] < .8 * dmax, first + 1, first + 2))
    return out

KITS = "/home/claude/rimworld-mods-/MisakTabs/Anatomy"

def kind_for(name):
    race, var = name.split("_", 1)
    if var.startswith("Kid"): return "Kid"
    if race == "Clovek":
        return {"F": "Female", "M": "Male", "Fat": "Fat", "Hulk": "Hulk", "Thin": "Male"}[var]
    if race == "Mugirl": return "Fat"
    if race == "Tailin": return "Hulk"
    if race == "Nivarian" and var.startswith("M"): return "Hulk"
    return "Male" if var.startswith("M") else "Female"

_tl = {}
def template_labels(kind):
    """the part ids of a standard figure in the padded frame (852 × 1364)"""
    if kind in _tl: return _tl[kind]
    p = np.array(Image.open(f"{KITS}/{kind}/parts.png"))
    ids = np.where(p[:, :, 3] > 100, np.round(p[:, :, 0] / 10), 0).astype(np.uint8)
    up = np.array(Image.fromarray(ids).resize((ids.shape[1] * 2, ids.shape[0] * 2), Image.NEAREST))
    FW, FH = W + 2 * PAD, H + 2 * PAD
    L = np.zeros((FH, FW), np.uint8)
    h, w = up.shape
    L[0:min(h, FH), 50:50 + min(w, FW - 50)] = up[:min(h, FH), :min(w, FW - 50)]
    _tl[kind] = L
    return L

def warp(L, sx, sy, dx, floor):
    """scale the label map around the figure's centre column (x) and the floor line (y), shift by dx"""
    FH, FW = L.shape
    ys, xs = np.nonzero(L)
    cx = (xs.min() + xs.max()) / 2
    Y, X = np.mgrid[0:FH, 0:FW]
    srcx = np.round((X - dx - cx) / sx + cx).astype(int)
    srcy = np.round((Y - floor) / sy + floor).astype(int)
    ok = (srcx >= 0) & (srcx < FW) & (srcy >= 0) & (srcy < FH)
    o = np.zeros_like(L)
    o[ok] = L[srcy[ok], srcx[ok]]
    return o

def fit_labels(full, kind):
    """the standard figure's parts laid onto the race figure: best scale/shift (searched at quarter
    size), then every body pixel takes the part of the nearest template pixel. Pixels far from the
    template (wings, tails, big ears) stay without a part – they are drawn but not coloured."""
    L = template_labels(kind)
    floor = PAD + 945
    q = 4
    Lq = L[::q, ::q]; fq = full[::q, ::q]
    fl = floor / q
    # the race figure's body column: middle of the legs/hips rows
    rows = fq[int((PAD + 500) / q):int((PAD + 930) / q)]
    fcx = np.median(np.nonzero(rows)[1]) if rows.any() else np.mean(np.nonzero(fq)[1])
    best, bp = -1e18, None
    for sy in np.arange(0.78, 1.09, 0.03):
        for sx in np.arange(0.80, 1.55, 0.05):
            T = warp(Lq, sx, sy, 0, fl) > 0
            tx = np.flatnonzero(T.any(0))
            if not len(tx): continue
            tr = T[int((PAD + 500) / q):int((PAD + 930) / q)]
            tcx = np.median(np.nonzero(tr)[1]) if tr.any() else (tx.min() + tx.max()) / 2
            for dx in range(-12, 13, 2):
                Ts = np.roll(T, int(round(fcx - tcx)) + dx, axis=1)
                inter = (Ts & fq).sum(); out = (Ts & ~fq).sum(); miss = 0
                score = inter - 3.0 * out
                if score > best: best, bp = score, (sx, sy, (int(round(fcx - tcx)) + dx) * q)
    sx, sy, sh = bp
    T = np.roll(warp(L, sx, sy, 0, floor), sh, axis=1)
    dist, (iy, ix) = ndi.distance_transform_edt(T == 0, return_indices=True)
    lab = np.where(full & (dist <= 30), T[iy, ix], 0).astype(np.uint8)
    return lab

def ids_to_body(ids):
    """ids (1 head … 15 left foot) → seg-style lab/sub + info for render's geometry"""
    lab = np.zeros_like(ids); sub = np.zeros_like(ids)
    lab[ids == 1] = 1; lab[ids == 2] = 2; lab[ids == 3] = 3
    for base, code, subs in ((4, 4, (8, 9, 10)), (7, 5, (8, 9, 10)), (10, 6, (11, 12, 13)), (13, 7, (11, 12, 13))):
        for k in range(3):
            m = ids == base + k
            lab[m] = code; sub[m] = subs[k]
    body = ids > 0
    ys = np.flatnonzero(body.any(1)); top, bot = ys[0], ys[-1]
    ny = np.flatnonzero((ids == 2).any(1)); neck = int(ny.mean()) if len(ny) else top + int(.15 * (bot - top))
    ty = np.flatnonzero((ids == 3).any(1)); crotch = int(ty.max()) if len(ty) else top + int(.5 * (bot - top))
    tx = np.flatnonzero((ids == 3).any(0)); cx = int((tx.min() + tx.max()) / 2) if len(tx) else int(np.mean(np.flatnonzero(body.any(0))))
    ay = np.flatnonzero(((ids >= 4) & (ids <= 9)).any(1))
    armpit = None
    for y in range(neck, crotch):
        if ((ids[y] >= 4) & (ids[y] <= 9)).any() and (ids[y] == 3).any():
            row = ids[y]
            # first row where arm and torso pixels are not touching on both sides
            armpit = y if armpit is None else armpit
    return lab, sub, dict(top=top, bot=bot, neck=neck, crotch=crotch, cx=cx, armpit=armpit, H=bot - top)

class RaceBody(Body):
    def __init__(s, line, full, a, ids):
        s.line, s.a = line, a
        s.body = ids > 0
        s.lab, s.sub, s.i = ids_to_body(ids)
        s.h, s.w = s.body.shape
        i = s.i
        ys = np.flatnonzero((s.lab == 3).any(1))
        s.tTop, s.tBot = ys[0], ys[-1]
        row = np.flatnonzero(s.lab[min(max(i["armpit"] or 0, s.tTop + int((s.tBot - s.tTop) * .25)), s.tBot)] == 3)
        s.tL, s.tR = row[0], row[-1]
        hy = np.flatnonzero((s.lab == 1).any(1)); s.hTop, s.hBot = hy[0], hy[-1]
        hx = np.flatnonzero((s.lab == 1).any(0)); s.hL, s.hR = hx[0], hx[-1]
        s.full = full

if __name__ == "__main__":
    src, out = sys.argv[1], sys.argv[2]
    names = sys.argv[3:]
    files = sorted(glob.glob(src + "/*.jpg"))
    files = [f for f in files if "_bad" not in f and "_v1" not in f and (not names or os.path.basename(f)[:-4] in names)]
    built = {}
    for f in files:
        built[os.path.basename(f)[:-4]] = build(f)
    import export_kit
    for n, (line, full, a) in built.items():
        race = n.split("_")[0]
        if race in SKIN_RACES:
            skin = skin_of(full, GRAY[n])
            ids = refine_arms(fit_labels(full, kind_for(n)), skin)
        else:
            ids = fit_labels(full, kind_for(n))
        B = RaceBody(line, full, a, ids)
        export_kit.export(B, f"{out}/{n}")
        print(n, "ok")
