import sys,bisect,collections,glob
d=sys.argv[1]
def load(f):
    ev=collections.defaultdict(list)
    for line in open(f):
        k,s,p,a,t=line.split(); ev[int(k)].append((int(t),int(s),int(p),int(a)))
    for k in ev: ev[k].sort()
    return ev
srv=load(glob.glob(d+'/t.server.*')[0]); cli=load(glob.glob(d+'/t.client.*')[0])
pings=[tuple(map(float,l.split())) for l in open(d+'/ping')]
def by_step(evs):
    m=collections.defaultdict(list)
    for e in evs: m[e[1]].append(e)
    return m
cSealT=[e[0] for e in cli[1]]
sKnown=by_step(srv[4]); sSealT=[e[0] for e in srv[1]]
sSend=by_step(srv[2]); cArr=by_step(cli[3]); cKnown=by_step(cli[4])
rows=[]
for i,t0,r in pings:
    t0=int(t0); t1=t0+int(r*1e6)
    j=bisect.bisect_left(cSealT,t0)
    if j>=len(cSealT): continue
    cs=cli[1][j]; u=cs[1]
    if u not in sKnown: continue
    sk=sKnown[u][0][0]
    j=bisect.bisect_left(sSealT,sk)
    if j>=len(sSealT): continue
    ss=srv[1][j]; dstep=ss[1]
    snd=sSend.get(dstep,[]); arr=cArr.get(dstep,[]); kn=cKnown.get(dstep,[])
    if not (snd and arr and kn): continue
    ms=lambda a,b:(b-a)/1e6
    rows.append(dict(i=int(i),rtt=r,
      up_seal=ms(t0,cs[0]), up_net=ms(cs[0],sk), echo=ms(sk,ss[0]),
      queue=ms(ss[0],snd[0][0]), path=ms(snd[0][0],arr[0][0]), decode=ms(arr[0][0],kn[0][0]),
      app=ms(kn[0][0],t1), k=ss[3], sends=len(snd), arrived=len(arr), rec=kn[0][3],
      sendpaths=sorted(set(e[2] for e in snd)), arrpath=arr[0][2], arrkind=arr[0][3],
      firstsend_path=snd[0][2]))
rows.sort(key=lambda x:-x['rtt'])
print('matched',len(rows),'of',len(pings))
cols=['rtt','up_seal','up_net','echo','queue','path','decode','app']
print('top 15 slowest:')
for x in rows[:15]:
    print(' '.join(f"{c}={x[c]:.1f}" for c in cols), 'k',x['k'],'sends',x['sends'],x['sendpaths'],'arr',x['arrived'],'first_arr_path',x['arrpath'],'kind',x['arrkind'],'rec',x['rec'])
import statistics as st
print('medians all:',' '.join(f"{c}={st.median([x[c] for x in rows]):.1f}" for c in cols))
# RTT samples on the server (TunPath) around each slow ping's send on each path.
rtt=collections.defaultdict(list)
for t,s,p,a in srv.get(5,[]): rtt[p].append((t,a/1000.0,s))
allr=[a for p in rtt for (_,a,_) in rtt[p]]
if allr:
    allr.sort(); print('server rtt samples n=%d p50=%.1f p90=%.1f p99=%.1f max=%.1f'%(len(allr),allr[len(allr)//2],allr[int(.9*len(allr))],allr[int(.99*len(allr))],allr[-1]))
    print('slow pings: per-path cell delay vs rtt samples in the window [send, send+150ms]')
    for x in rows[:8]:
        pass
    # recompute per cell for the slowest pings
    for i,t0,r in sorted(pings,key=lambda z:-z[2])[:8]:
        t0=int(t0); j=bisect.bisect_left(cSealT,t0); u=cli[1][j][1]; sk=sKnown[u][0][0]
        j=bisect.bisect_left(sSealT,sk); dstep=srv[1][j][1]
        arrs={}
        for (t,s,p,a) in cArr.get(dstep,[]): arrs.setdefault(p,t)
        out=[]
        for (t,s,p,a) in sSend.get(dstep,[]):
            ts=[(tt,rr,inf) for (tt,rr,inf) in rtt[p] if t<=tt<=t+150e6]
            mx=max([rr for _,rr,_ in ts],default=-1)
            out.append(f"p{p}:{'%.0f'%((arrs[p]-t)/1e6) if p in arrs else 'lost'}ms rttmax={mx:.0f} n={len(ts)}")
        print(f"rtt={r:.0f}", ' '.join(out))
