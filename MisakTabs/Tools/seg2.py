import numpy as np
from seg import runs
def segment2(body):
    h,w=body.shape
    ys=np.flatnonzero(body.any(1)); top,bot=ys[0],ys[-1]; H=bot-top
    widths=body.sum(1)
    cx=int(np.mean(np.flatnonzero(body.any(0))))
    _hy=np.flatnonzero(body.any(1))[0]; _hx=np.flatnonzero(body[_hy+int(.05*(np.flatnonzero(body.any(1))[-1]-_hy))]); cx=int((_hx[0]+_hx[-1])/2) if len(_hx) else cx
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
    if armpit is None:
        # 0.10.0: arms touch the body – find rows lower down where the hands hang free and carry the
        # inner edge of each arm up to the shoulder in a straight line
        Ls=[];Rs=[]
        for y in range(neck+int(.06*H),min(bot,crotch+int(.18*H))):
            rs=runs(body[y]); mid=[r for r in rs if r[0]<=cx<r[1]]
            if len(rs)>=3 and mid:
                lefts=[r for r in rs if r[1]<=mid[0][0]]; rights=[r for r in rs if r[0]>=mid[0][1]]
                if lefts and rights: Ls.append((y,mid[0][0])); Rs.append((y,mid[0][1]))
        if len(Ls)>=4:
            yy=np.array([p[0] for p in Ls],float)
            pl=np.polyfit(yy,[p[1] for p in Ls],1); pr=np.polyfit(yy,[p[1] for p in Rs],1)
            ytop=neck+int(.035*H); ylast=int(yy.max())
            for y in range(ytop,min(ylast,crotch)+1):
                xl=int(np.polyval(pl,y)); xr=int(np.polyval(pr,y))
                # near the shoulder the arm narrows into the body: no cut above the shoulder line
                if y<neck+int(.07*H): xl=min(xl,np.flatnonzero(body[y])[0]+1); xr=max(xr,np.flatnonzero(body[y])[-1])
                for x in np.flatnonzero(body[y]):
                    if x<xl: lab[y,x]=4
                    elif x>=xr: lab[y,x]=5
                    else: lab[y,x]=3
            armpit=None
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
