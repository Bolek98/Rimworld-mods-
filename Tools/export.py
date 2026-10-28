import os, numpy as np
from PIL import Image, ImageFilter
import sys, render
if len(sys.argv)>1 and sys.argv[1]=="gen":
    import genbody; render.load=lambda k: genbody.build(k)
from render import *
OUT=sys.argv[2] if len(sys.argv)>2 else "/home/claude/work/private/MisakTabsAnatomy/Anatomy"
CROP=(50,0,802,1224)
PARTS=["head","neck","torso","RArm","RForearm","RHand","LArm","LForearm","LHand","RLeg","RShin","RFoot","LLeg","LShin","LFoot"]
ORG=["brain","Reye","Leye","Rlung","Llung","heart","liver","stomach","Rkidney","Lkidney"]
BONES=["skull","spine","ribs","Rclav","Lclav","pelvis","Rhumerus","Rradius","Rhand","Lhumerus","Lradius","Lhand","Rfemur","Rtibia","Rfoot","Lfemur","Ltibia","Lfoot"]
TAGS=["mouseEars","catEars","wolfEars","foxEars","rabbitEars","elfEars","horns","smallHorns","antlers","antennae","halo","gills","wisp",
      "thinTail","catTail","wolfTail","foxTail","rabbitTail","dragonTail","fishTail","wings","featherWings","batWings","mothWings"]
def fin(im): return im.crop(CROP).resize(((CROP[2]-CROP[0])//2,(CROP[3]-CROP[1])//2),Image.LANCZOS)
def idmap(layers,h,w):
    # layers: list of (id, alpha float array full-res); id map by max alpha, alpha = max
    A=np.zeros((h,w)); I=np.zeros((h,w),np.uint8)
    for i,a in layers:
        m=a>A; I[m]=i; A[m]=a[m]
    # downsample: alpha lanczos, id nearest via block majority of 2x2 (weighted by alpha)
    Ai=Image.fromarray((A*255).astype(np.uint8)); Ii=Image.fromarray(I)
    Ai=Ai.crop(CROP).resize(((CROP[2]-CROP[0])//2,(CROP[3]-CROP[1])//2),Image.LANCZOS)
    Ii=Ii.crop(CROP); a=np.array(Ii); a2=np.maximum(np.maximum(a[0::2,0::2],a[1::2,0::2]),np.maximum(a[0::2,1::2],a[1::2,1::2]))
    # prefer the top-left sample when not zero (keeps borders between parts straight)
    tl=a[0::2,0::2]; a2=np.where(tl>0,tl,a2)
    o=np.zeros(a2.shape+(4,),np.uint8); o[:,:,0]=a2*10; o[:,:,3]=np.array(Ai)
    return Image.fromarray(o)
for kind in ["Male","Female","Fat","Hulk","Kid"]:
    B=Body(kind); h,w=B.h,B.w; d=f"{OUT}/{kind}"; os.makedirs(d,exist_ok=True)
    shade=np.clip(B.a*(1-np.array(Image.fromarray((B.line*255).astype(np.uint8))).astype(float)/255)*1.0,0,1)
    base=Image.new("RGBA",(w,h)); base.alpha_composite(rgba((30,35,42),B.body.astype(float))); base.alpha_composite(rgba((105,116,130),np.clip((B.a-B.line)*.9,0,1)*B.body))
    fin(base).save(f"{d}/base.png")
    li=rgba((255,255,255),np.clip(B.line*1.6,0,1)); g=li.filter(ImageFilter.GaussianBlur(5)); L=Image.new("RGBA",(w,h)); L.alpha_composite(g); L.alpha_composite(li)
    fin(L).save(f"{d}/line.png")
    idmap([(i+1,B.mask(p).astype(float)) for i,p in enumerate(PARTS)],h,w).save(f"{d}/parts.png")
    _,O=view_organs(B,{},[])
    lay=[]
    for i,k in enumerate(ORG):
        v=Vec(w,h); typ,g2=O[k]
        if typ=="ell": v.ell(*g2,fill=(255,255,255,255))
        else: v.poly(g2,fill=(255,255,255,255))
        lay.append((i+1,np.array(v.out())[:,:,3].astype(float)/255))
    idmap(lay,h,w).save(f"{d}/organs.png")
    lay=[]
    for i,k in enumerate(BONES):
        im=np.array(view_bones(B,{},[],only=k)).astype(float)
        lum=(im[:,:,0]*.3+im[:,:,1]*.59+im[:,:,2]*.11)
        lay.append((i+1,im[:,:,3]/255*np.clip((lum-60)/170,0,1)))
    idmap(lay,h,w).save(f"{d}/bones.png")
    pieces=[]
    for t in TAGS:
        for side,beh in (("back",True),("front",False)):
            im=race_features(B,[t],(214,222,232,255),(64,74,86,255),beh)
            if im.getbbox() is None: continue
            f=fin(im); bb=f.getbbox()
            if bb is None: continue
            pieces.append((t+"_"+side,f.crop(bb),bb[0],bb[1]))
    # pack into one atlas (shelf packing), index: name x y w h ox oy
    AW=1024; x=y=rowh=0; place=[]
    for n,im,ox,oy in sorted(pieces,key=lambda p:-p[1].height):
        if x+im.width>AW: x=0; y+=rowh+2; rowh=0
        place.append((n,im,x,y,ox,oy)); x+=im.width+2; rowh=max(rowh,im.height)
    atlas=Image.new("RGBA",(AW,y+rowh))
    with open(f"{d}/features.txt","w") as fh:
        fh.write(f"frame {fin(Image.new('RGBA',(w,h))).width} {fin(Image.new('RGBA',(w,h))).height}\n")
        for n,im,x0,y0,ox,oy in place:
            atlas.paste(im,(x0,y0)); fh.write(f"{n} {x0} {y0} {im.width} {im.height} {ox} {oy}\n")
    atlas.save(f"{d}/features.png")
    print(kind,len(os.listdir(d)))
