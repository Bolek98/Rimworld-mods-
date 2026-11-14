import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from scipy import ndimage as ndi
BASE="/mnt/user-data/uploads/294100/3328729902/Textures/HediffTab/Baseliner"
def load(kind):
    im=Image.open(f"{BASE}/{kind}/Outline.png").convert("RGBA")
    a=np.array(im)[:,:,3].astype(float)/255
    lum=np.array(im.convert("L")).astype(float)/255
    line=a*lum
    b=Image.fromarray(((a>0.35)*255).astype(np.uint8)).filter(ImageFilter.MaxFilter(3))
    ImageDraw.floodfill(b,(0,0),128)
    body=(np.array(b)!=128)
    return line,body,a
def runs(row):
    x=np.flatnonzero(np.diff(np.r_[0,row.astype(int),0]))
    return list(zip(x[::2],x[1::2]))
def segment(body):
    h,w=body.shape
    ys=np.flatnonzero(body.any(1)); top,bot=ys[0],ys[-1]; H=bot-top
    widths=body.sum(1)
    cx=int(np.mean(np.flatnonzero(body.any(0))))
    # head: from top until narrowest point (neck) within first 22%
    rng=range(top+int(.06*H),top+int(.22*H))
    neck=min(rng,key=lambda y:widths[y])
    # crotch: lowest row where center column is body, scanning from bottom: first row (from bottom) center is body
    col=body[:,cx]; crotch=bot
    for y in range(bot,top,-1):
        if col[y]: crotch=y;break
    lab=np.zeros((h,w),np.uint8)  # 1 head 2 neck 3 torso 4 armL 5 armR 6 legL 7 legR
    # torso band per row: the run containing cx
    shoulder=None
    rs=runs(body[crotch-3]); hipL=hipR=None
    for a,b in rs:
        if a<=cx<b: hipL,hipR=a,b
    for y in range(top,bot+1):
        rs=runs(body[y])
        if not rs: continue
        if y<neck-int(.015*H): 
            for a,b in rs: lab[y,a:b]=1
            continue
        if y<neck+int(.035*H):
            for a,b in rs: lab[y,a:b]=2
            continue
        if y<=crotch:
            # torso run: contains cx
            for a,b in rs:
                if a<=cx<b: tor=(a,b)
            for a,b in rs:
                if (a,b)==tor:
                    lab[y,a:b]=3
                else:
                    ab=lab[y-1,a:b]; ab=ab[ab>0]
                    if y>crotch-0.08*H and len(ab) and 3 in ab and not (4 in ab or 5 in ab): lab[y,a:b]=3
                    else: lab[y,a:b]=4 if b<=cx else 5
        else:
            # below the crotch: a run continues the label of what it overlaps in the row above
            prev=lab[y-1]
            for a,b in rs:
                above=prev[a:b]; above=above[above>0]
                if len(above):
                    legs=[x for x in above if x in (3,6,7)]
                    v=np.bincount(legs).argmax() if legs else np.bincount(above).argmax()
                    if v==3: v=6 if (a+b)/2<cx else 7
                    lab[y,a:b]=v
                else:
                    lab[y,a:b]=6 if (a+b)/2<cx else 7
    # torso run near shoulders includes arms (merged); split by width profile: arms where torso width > hip width*1.15? use limb separation below armpit
    # find armpit: first row (after neck) with >=3 runs
    armpit=next((y for y in range(neck,crotch) if len(runs(body[y]))>=3),None)
    if armpit:
        # torso x-extent at armpit row from middle run
        rs=runs(body[armpit]); mid=[r for r in rs if r[0]<=cx<r[1]][0]
        for y in range(neck+int(.035*H),armpit):
            row=lab[y]; xs=np.flatnonzero(body[y])
            for x in xs:
                if x<mid[0]: lab[y,x]=4
                elif x>=mid[1]: lab[y,x]=5
    # hands: arm pixels below a fraction
    # subdivide limbs
    sub=np.zeros_like(lab)
    for code,parts in ((4,(8,9,10)),(5,(8,9,10)),(6,(11,12,13)),(7,(11,12,13))):
        ys=np.flatnonzero((lab==code).any(1))
        if len(ys)==0: continue
        y0,y1=ys[0],ys[-1]; L=y1-y0
        cuts=(.45,.80) if code in (4,5) else (.47,.90)
        for y in range(y0,y1+1):
            f=(y-y0)/max(L,1); p=parts[0] if f<cuts[0] else parts[1] if f<cuts[1] else parts[2]
            sub[y][lab[y]==code]=p
    return lab,sub,dict(top=top,bot=bot,neck=neck,crotch=crotch,cx=cx,armpit=armpit,H=H)
if __name__=="__main__":
    import sys
    for k in ["Male","Female","Fat","Hulk","Kid"]:
        line,body,a=load(k); lab,sub,info=segment(body)
        pal=np.array([[0,0,0],[230,90,90],[230,160,90],[90,180,110],[90,140,230],[150,100,230],[230,200,80],[80,200,200]],np.uint8)
        im=Image.fromarray(pal[lab]); im.save(f"/tmp/claude-0/-home-claude/844ef0aa-ca0a-5a4b-97d7-9255bcd47638/scratchpad/sil/seg_{k}.png"); print(k,info)
