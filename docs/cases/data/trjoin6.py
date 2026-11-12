import sys,collections
exec(open('docs/cases/data/trjoin.py').read().split("# RTT samples")[0].replace("print(","(lambda *a,**k:None)("))
p0=int(pings[0][1])
s=srv.get(5,[])
b=collections.defaultdict(list)
for t,inf,p,a in s:
    if t<p0-1.5e9 or t>p0+12e9: continue
    b[int((t-p0)//250e6)].append((a/1000.0,inf,p))
ping_by_bin=collections.defaultdict(list)
for i,t0,r in pings: ping_by_bin[int((t0-p0)//250e6)].append(r)
print("t(s rel. first ping)  rtt p50/max ms   inflight/path KB p50   max ping ms in bin")
for k in sorted(b):
    v=sorted(x[0] for x in b[k]); inf=sorted(x[1] for x in b[k])
    pr=ping_by_bin.get(k,[])
    print(f"{k*0.25:5.2f}  {v[len(v)//2]:6.0f}/{v[-1]:4.0f}   {inf[len(inf)//2]//1024:5d}   {max(pr) if pr else 0:6.0f}  n={len(v)}")
