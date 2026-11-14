"""0.10.0: one anatomy kit (base, line, parts, organs, bones) from a Body – shared by export.py
and racekits.py. A race body has .full (the whole figure with wings, tails …) for the base."""
import os, numpy as np
from PIL import Image, ImageFilter
from render import *

CROP = (50, 0, 802, 1224)
PARTS = ["head", "neck", "torso", "RArm", "RForearm", "RHand", "LArm", "LForearm", "LHand", "RLeg", "RShin", "RFoot", "LLeg", "LShin", "LFoot"]
ORG = ["brain", "Reye", "Leye", "Rlung", "Llung", "heart", "liver", "stomach", "Rkidney", "Lkidney"]
BONES = ["skull", "spine", "ribs", "Rclav", "Lclav", "pelvis", "Rhumerus", "Rradius", "Rhand", "Lhumerus", "Lradius", "Lhand", "Rfemur", "Rtibia", "Rfoot", "Lfemur", "Ltibia", "Lfoot"]

def fin(im): return im.crop(CROP).resize(((CROP[2] - CROP[0]) // 2, (CROP[3] - CROP[1]) // 2), Image.LANCZOS)

def idmap(layers, h, w):
    A = np.zeros((h, w)); I = np.zeros((h, w), np.uint8)
    for i, a in layers:
        m = a > A; I[m] = i; A[m] = a[m]
    Ai = Image.fromarray((A * 255).astype(np.uint8)); Ii = Image.fromarray(I)
    Ai = Ai.crop(CROP).resize(((CROP[2] - CROP[0]) // 2, (CROP[3] - CROP[1]) // 2), Image.LANCZOS)
    Ii = Ii.crop(CROP); a = np.array(Ii)
    a2 = np.maximum(np.maximum(a[0::2, 0::2], a[1::2, 0::2]), np.maximum(a[0::2, 1::2], a[1::2, 1::2]))
    tl = a[0::2, 0::2]; a2 = np.where(tl > 0, tl, a2)
    o = np.zeros(a2.shape + (4,), np.uint8); o[:, :, 0] = a2 * 10; o[:, :, 3] = np.array(Ai)
    return Image.fromarray(o)

def export(B, d):
    os.makedirs(d, exist_ok=True)
    h, w = B.h, B.w
    full = getattr(B, "full", B.body)
    base = Image.new("RGBA", (w, h))
    base.alpha_composite(rgba((30, 35, 42), full.astype(float)))
    base.alpha_composite(rgba((105, 116, 130), np.clip((B.a - B.line) * .9, 0, 1) * full))
    fin(base).save(f"{d}/base.png")
    li = rgba((255, 255, 255), np.clip(B.line * 1.6, 0, 1)); g = li.filter(ImageFilter.GaussianBlur(5))
    L = Image.new("RGBA", (w, h)); L.alpha_composite(g); L.alpha_composite(li)
    fin(L).save(f"{d}/line.png")
    idmap([(i + 1, B.mask(p).astype(float)) for i, p in enumerate(PARTS)], h, w).save(f"{d}/parts.png")
    _, O = view_organs(B, {}, [])
    lay = []
    for i, k in enumerate(ORG):
        v = Vec(w, h); typ, g2 = O[k]
        if typ == "ell": v.ell(*g2, fill=(255, 255, 255, 255))
        else: v.poly(g2, fill=(255, 255, 255, 255))
        lay.append((i + 1, np.array(v.out())[:, :, 3].astype(float) / 255))
    idmap(lay, h, w).save(f"{d}/organs.png")
    lay = []
    for i, k in enumerate(BONES):
        im = np.array(view_bones(B, {}, [], only=k)).astype(float)
        lum = (im[:, :, 0] * .3 + im[:, :, 1] * .59 + im[:, :, 2] * .11)
        lay.append((i + 1, im[:, :, 3] / 255 * np.clip((lum - 60) / 170, 0, 1)))
    idmap(lay, h, w).save(f"{d}/bones.png")
