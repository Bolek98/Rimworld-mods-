"""Bodies from generated flat illustrations (own, not from another mod): mask + soft inner lines,
fitted into the same 512x1024 frame as before (head top ~75, feet ~945)."""
import numpy as np
from PIL import Image, ImageFilter, ImageDraw
from scipy import ndimage as ndi
W,H=512,1024
G="/home/claude/work/silhouettes/gen/"
def mask_of(im):
    a=np.array(im.convert("RGB")).astype(int)
    m=(np.abs(a-255).sum(2)>36)
    m=ndi.binary_fill_holes(ndi.binary_closing(m,iterations=2))
    lab,n=ndi.label(m); sizes=ndi.sum(m,lab,range(1,n+1)); m=lab==(np.argmax(sizes)+1)
    return m, a
def fit(m, gray, top=75, bot=945, xscale=1.0, headscale=1.0):
    ys=np.flatnonzero(m.any(1)); xs=np.flatnonzero(m.any(0))
    m=m[ys[0]:ys[-1]+1, xs[0]:xs[-1]+1]; gray=gray[ys[0]:ys[-1]+1, xs[0]:xs[-1]+1]
    h,w=m.shape; s=(bot-top)/h
    nw=max(1,int(w*s*xscale)); nh=bot-top
    M=Image.fromarray((m*255).astype(np.uint8)).resize((nw,nh),Image.LANCZOS)
    Gi=Image.fromarray(gray.astype(np.uint8)).resize((nw,nh),Image.LANCZOS)
    cm=Image.new("L",(W,H)); cg=Image.new("L",(W,H),255)
    x0=(W-nw)//2; cm.paste(M,(x0,top)); cg.paste(Gi,(x0,top))
    return np.array(cm).astype(float)/255, np.array(cg).astype(float)
def build_from(img, **kw):
    m,a=mask_of(img)
    gray=(a[:,:,0]*.3+a[:,:,1]*.59+a[:,:,2]*.11)
    am,g=fit(m,gray,**kw)
    body=am>0.5
    # smooth the edge a little
    sm=np.array(Image.fromarray((am*255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.2))).astype(float)/255
    body=sm>0.5
    B=Image.fromarray((body*255).astype(np.uint8))
    edge=np.array(B.filter(ImageFilter.MaxFilter(5))).astype(float)/255-np.array(B.filter(ImageFilter.MinFilter(3))).astype(float)/255
    edge=np.array(Image.fromarray((np.clip(edge,0,1)*255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(.7))).astype(float)/255
    # inner structure: soft edges of the illustration's tones (folds, belly, muscles), faint
    gx=ndi.sobel(ndi.gaussian_filter(g,1.5),1); gy=ndi.sobel(ndi.gaussian_filter(g,1.5),0)
    inner=np.clip(np.hypot(gx,gy)/120,0,1)*ndi.binary_erosion(body,iterations=6)*0.2
    line=np.clip(edge+inner,0,1)
    shade=np.clip(1-ndi.distance_transform_edt(body)/16,0,1)*body*0.4
    return line, body, np.maximum(line,shade)
def build(kind):
    if kind in ("Male","Female","Fat"): return build_from(Image.open(G+kind+".webp"))
    if kind=="Hulk": return build_from(Image.open(G+"Male.webp"), top=50, bot=985, xscale=1.16)
    if kind=="Kid":  return build_from(Image.open(G+"Female.webp"), top=150, bot=945, xscale=1.08)
if __name__=="__main__":
    ims=[]
    for k in ["Male","Female","Fat","Hulk","Kid"]:
        line,body,a=build(k)
        c=Image.new("RGBA",(W,H),(30,34,40,255))
        c.alpha_composite(Image.fromarray(np.dstack([np.full((H,W,3),[70,80,92],np.uint8),(body*255).astype(np.uint8)])))
        c.alpha_composite(Image.fromarray(np.dstack([np.full((H,W,3),255,np.uint8),(line*255).astype(np.uint8)])))
        ims.append(c.resize((256,512)))
    out=Image.new("RGB",(256*5,512)); [out.paste(im,(i*256,0)) for i,im in enumerate(ims)]
    out.save("/tmp/claude-0/-home-claude/844ef0aa-ca0a-5a4b-97d7-9255bcd47638/scratchpad/sil/gen_bodies.png")
