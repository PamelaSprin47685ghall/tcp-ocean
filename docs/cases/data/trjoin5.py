import sys,bisect,collections
exec(open('docs/cases/data/trjoin.py').read().split("# RTT samples")[0].replace("print(","(lambda *a,**k:None)("))
per=[];mins=[];bulk_t=[]
for i,t0,r in pings:
    t0=int(t0); j=bisect.bisect_left(cSealT,t0); u=cli[1][j][1]
    if u not in sKnown: continue
    sk=sKnown[u][0][0]; j=bisect.bisect_left(sSealT,sk); dstep=srv[1][j][1]
    arrs={}
    for (t,s,p,a) in cArr.get(dstep,[]): arrs.setdefault(p,t)
    ow=[(arrs[p]-t)/1e6 for (t,s,p,a) in sSend.get(dstep,[]) if p in arrs]
    if not ow: continue
    per+=ow; mins.append((int(i),min(ow),len(ow),r))
q=lambda L,p: sorted(L)[int(p*(len(L)-1))]
print("per-cell owd: n=%d p50=%.0f p75=%.0f p90=%.0f p99=%.0f; >60ms: %.0f%%"%(len(per),q(per,.5),q(per,.75),q(per,.9),q(per,.99),100*sum(x>60 for x in per)/len(per)))
m=[x[1] for x in mins]
print("first-arrival owd per ping: p50=%.0f p90=%.0f p99=%.0f; >60ms: %.0f%%"%(q(m,.5),q(m,.9),q(m,.99),100*sum(x>60 for x in m)/len(m)))
print("slow pings (>60ms first arrival) by ping index:", [x[0] for x in mins if x[1]>60])
