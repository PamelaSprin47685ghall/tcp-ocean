import sys,bisect,collections
exec(open('docs/cases/data/trjoin2.py').read().replace("print(","(lambda *a,**k:None)("))
rd=collections.defaultdict(list)
for t,s,p,a in cli.get(6,[]): rd[p].append((t,s))
for p in rd: rd[p].sort()
allv=sorted(s for p in rd for _,s in rd[p])
print("client readable bytes per read: n=%d p50=%d p90=%d p99=%d max=%d"%(len(allv),allv[len(allv)//2],allv[int(.9*len(allv))],allv[int(.99*len(allv))],allv[-1]))
print("slowest pings: per path  cell_owd / readable bytes at reads in [send+15ms, arrival] (max)")
for i,t0,r in sorted(pings,key=lambda z:-z[2])[:10]:
    t0=int(t0); j=bisect.bisect_left(cSealT,t0); u=cli[1][j][1]; sk=sKnown[u][0][0]
    j=bisect.bisect_left(sSealT,sk); dstep=srv[1][j][1]
    arrs={}
    for (t,s,p,a) in cArr.get(dstep,[]): arrs.setdefault(p,t)
    out=[]
    for (t,s,p,a) in sSend.get(dstep,[]):
        if p not in arrs: out.append(f"p{p}:lost"); continue
        L=rd[p]; j=bisect.bisect_left(L,(t+15e6,0)); mx=0; n=0
        while j<len(L) and L[j][0]<=arrs[p]: mx=max(mx,L[j][1]); n+=1; j+=1
        out.append(f"p{p}:{(arrs[p]-t)/1e6:.0f}ms/max{mx//1024}K n{n}")
    print(f"rtt={r:.0f}",' '.join(out))
