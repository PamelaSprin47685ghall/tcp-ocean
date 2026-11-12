import sys,bisect,collections,glob
exec(open('docs/cases/data/trjoin.py').read().split("# RTT samples")[0].replace("print(","(lambda *a,**k:None)(") )
rtt=collections.defaultdict(list)
for t,s,p,a in srv.get(5,[]): rtt[p].append((t-a*1000,a/1000.0))   # (segment send time, rtt ms)
for p in rtt: rtt[p].sort()
def near(p,t,w=3e6):
    L=rtt[p]; j=bisect.bisect_left(L,(t-w,0)); out=[]
    while j<len(L) and L[j][0]<=t+w: out.append(L[j][1]); j+=1
    return out
print("slowest pings: per path  cell_owd / rtt of segments sent within 3 ms of the cell (min..max)")
excess=[]
for i,t0,r in sorted(pings,key=lambda z:-z[2])[:12]:
    t0=int(t0); j=bisect.bisect_left(cSealT,t0); u=cli[1][j][1]; sk=sKnown[u][0][0]
    j=bisect.bisect_left(sSealT,sk); dstep=srv[1][j][1]
    arrs={}
    for (t,s,p,a) in cArr.get(dstep,[]): arrs.setdefault(p,t)
    out=[]
    for (t,s,p,a) in sSend.get(dstep,[]):
        n=near(p,t)
        owd=(arrs[p]-t)/1e6 if p in arrs else None
        if n and owd: excess.append(owd-(min(n)-20))
        out.append(f"p{p}:{'%.0f'%owd if owd else 'lost'}/{'%.0f..%.0f'%(min(n),max(n)) if n else '-'}")
    print(f"rtt={r:.0f}",' '.join(out))
excess.sort()
print("cell owd minus (min nearby rtt - 20ms up): median %.0f ms, min %.0f, max %.0f (n=%d)"%(excess[len(excess)//2],excess[0],excess[-1],len(excess)))
