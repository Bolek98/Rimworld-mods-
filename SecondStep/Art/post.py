# 512 render -> 128 RimWorld texture with a dark outline
from PIL import Image, ImageFilter
import sys, os
src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst+'/Worn', exist_ok=True)
for f in os.listdir(src):
    if not f.endswith('.png'): continue
    im = Image.open(os.path.join(src, f)).convert('RGBA').resize((128,128), Image.LANCZOS)
    a = im.split()[3].point(lambda v: 255 if v > 40 else 0)
    grown = a.filter(ImageFilter.MaxFilter(3))
    out = Image.new('RGBA', im.size, (0,0,0,0))
    edge = Image.new('RGBA', im.size, (28,26,24,255))
    out.paste(edge, (0,0), grown)
    out.alpha_composite(im)
    sub = 'Worn' if '_' in f else ''
    out.save(os.path.join(dst, sub, f))
print('ok')
