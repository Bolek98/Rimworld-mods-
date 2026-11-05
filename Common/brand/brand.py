"""Stamp the [B98] badge (Czech flag + B98) into a mod's About/Preview.png.
The unstamped original is kept in Common/brand/originals/<ModDir>.png, so stamping again
(or after a new render) never doubles the badge.  Usage: brand.py <ModDir> [<ModDir> ...] | --all"""
import sys, os, glob
from PIL import Image, ImageDraw, ImageFont, ImageFilter
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ORIG = os.path.join(ROOT, 'Common', 'brand', 'originals')
FONT = next((f for f in ['/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf',
             '/usr/share/fonts/TTF/DejaVuSans-Bold.ttf'] if os.path.exists(f)), None)

def flag(w, h):
    f = Image.new('RGBA', (w, h), (255, 255, 255, 255))
    d = ImageDraw.Draw(f)
    d.rectangle([0, h // 2, w, h], fill=(215, 20, 26, 255))
    d.polygon([(0, 0), (w // 2, h // 2), (0, h)], fill=(17, 69, 126, 255))
    return f

def badge(scale):
    fh = int(28 * scale); fw = int(fh * 1.5)
    font = ImageFont.truetype(FONT, int(25 * scale)) if FONT else ImageFont.load_default()
    tw = int(ImageDraw.Draw(Image.new('RGBA', (1, 1))).textlength('B98', font=font))
    pad = int(10 * scale); gap = int(10 * scale)
    W = pad + fw + gap + tw + pad; H = fh + 2 * pad
    b = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(b)
    d.rounded_rectangle([0, 0, W - 1, H - 1], radius=int(10 * scale), fill=(12, 14, 20, 200), outline=(255, 255, 255, 60), width=max(1, int(2 * scale)))
    fl = flag(fw, fh); m = Image.new('L', (fw, fh), 0); ImageDraw.Draw(m).rounded_rectangle([0, 0, fw - 1, fh - 1], radius=int(4 * scale), fill=255)
    b.paste(fl, (pad, pad), m)
    d.text((pad + fw + gap, H // 2), 'B98', font=font, fill=(255, 255, 255, 235), anchor='lm')
    return b

def stamp(mod):
    p = os.path.join(ROOT, mod, 'About', 'Preview.png')
    if not os.path.exists(p): return False
    key = mod.replace('/', '_').replace('\\', '_')
    o = os.path.join(ORIG, key + '.png')
    os.makedirs(ORIG, exist_ok=True)
    # a stamped file ends with the marker; anything else is a fresh render and becomes the new original
    if not open(p, 'rb').read().endswith(b'B98BRAND') or not os.path.exists(o):
        Image.open(p).save(o)
    im = Image.open(o).convert('RGBA')
    sc = im.width / 1280
    b = badge(sc)
    sh = Image.new('RGBA', im.size, (0, 0, 0, 0))
    m = int(22 * sc)
    # the calmest corner (least detail under the badge): top right first, then bottom right, top left
    from PIL import ImageStat
    cands = [(im.width - b.width - m, m), (im.width - b.width - m, im.height - b.height - m), (m, m), (m, im.height - b.height - m)]
    def busy(xy):
        box = (xy[0] - 6, xy[1] - 6, xy[0] + b.width + 6, xy[1] + b.height + 6)
        return sum(ImageStat.Stat(im.crop(box).convert('L')).stddev)
    # 1 consistent place (top left) unless something is drawn there – then the calmest other corner
    order = [cands[2], cands[0], cands[1], cands[3]]
    pos = order[0] if busy(order[0]) < 30 else min(order, key=busy)
    shadow = Image.new('RGBA', (b.width + 20, b.height + 20), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([10, 10, b.width + 9, b.height + 9], radius=int(12 * sc), fill=(0, 0, 0, 120))
    shadow = shadow.filter(ImageFilter.GaussianBlur(6))
    im.alpha_composite(shadow, (pos[0] - 10 + 3, pos[1] - 10 + 4))
    im.alpha_composite(b, pos)
    im.convert('RGB').save(p, optimize=True)
    with open(p, 'ab') as f: f.write(b'B98BRAND')
    return True

if __name__ == '__main__':
    mods = sys.argv[1:]
    if mods == ['--all']:
        mods = sorted(os.path.relpath(os.path.dirname(os.path.dirname(x)), ROOT) for x in glob.glob(os.path.join(ROOT, '**', 'About', 'Preview.png'), recursive=True) if '/obj/' not in x)
    for m in mods:
        print(m, 'ok' if stamp(m) else 'no preview')
