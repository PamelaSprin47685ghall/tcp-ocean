#!/usr/bin/env python3
"""Sequential fresh-process, forward/reversed same-input component measurements."""
import argparse, hashlib, json, os, pathlib, platform, statistics, subprocess, time
p=argparse.ArgumentParser()
p.add_argument('--dotnet',required=True);p.add_argument('--bench',required=True)
p.add_argument('--input-root',required=True);p.add_argument('--output',required=True)
p.add_argument('--passes',type=int,default=1000);p.add_argument('--trials',type=int,default=5)
a=p.parse_args();out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=True)
root=pathlib.Path(a.input_root)
inputs=[root/case/(role+'.csv') for case in ['p1-shadow','p2-shadow'] for role in ['client','server']]
stages=['parse-direct','parse-forward','parse-sample']
plan=[(f,s) for f in inputs for s in stages]
env=dict(os.environ,DOTNET_TieredCompilation='0')
manifest={'purpose':'offline post-authentication canonical component decomposition, not original crypto or full Collector cost','platform':platform.platform(),'tiered_compilation':'0','passes':a.passes,'warm_trials':a.trials,'order':[],'binary_sha256':{},'inputs':{}}
for f in pathlib.Path(a.bench).parent.glob('*.dll'):manifest['binary_sha256'][str(f)]=hashlib.sha256(f.read_bytes()).hexdigest()
for f in inputs:manifest['inputs'][str(f)]=hashlib.sha256(f.read_bytes()).hexdigest()
records=[]
for repeat,sequence in enumerate([plan,list(reversed(plan))]):
 for source,stage in sequence:
  label=source.parent.name+'-'+source.stem
  dest=out/f'r{repeat}-{label}-{stage}.json'
  cmd=[a.dotnet,a.bench,str(source),stage,str(dest),str(a.passes),str(a.trials)]
  start=time.time();result=subprocess.run(cmd,env=env,capture_output=True,text=True);end=time.time()
  (out/(dest.stem+'.log')).write_text(result.stdout+result.stderr)
  manifest['order'].append({'repeat':repeat,'input':label,'stage':stage,'command':cmd,'start_unix':start,'end_unix':end,'driver_process_wall_s':end-start,'exit':result.returncode})
  (out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
  if result.returncode:raise SystemExit(f'benchmark failed: {dest}')
  d=json.loads(dest.read_text());d['case']=label;d['repeat']=repeat;records.append(d)
  print(f'{repeat} {label} {stage} verified',flush=True)
summary=[]
for source in inputs:
 label=source.parent.name+'-'+source.stem
 group=[d for d in records if d['case']==label]
 for key in ['CanonicalPlaintextSha256','DirectChecksum','ForwardChecksum','SampleChecksum']:
  assert len({d[key] for d in group})==1,(label,key)
 assert all(d['DirectChecksum']==d['ForwardChecksum'] and d['CollectorExactRows'] for d in group)
 for stage in stages:
  values=[m for d in group for m in d['Measurements'] if m['Stage']==stage and m['Phase']=='warm-batch']
  cpu=[v['ProcessCpuMs']*1e6/(v['RowsPerPass']*v['Passes']) for v in values]
  wall=[v['WallMs']*1e6/(v['RowsPerPass']*v['Passes']) for v in values]
  alloc=[v['ThreadAllocatedBytes']/(v['RowsPerPass']*v['Passes']) for v in values]
  summary.append({'case':label,'stage':stage,'warm_samples':len(values),'cpu_ns_per_report_median':statistics.median(cpu),'cpu_ns_per_report_min':min(cpu),'cpu_ns_per_report_max':max(cpu),'wall_ns_per_report_median':statistics.median(wall),'wall_ns_per_report_min':min(wall),'wall_ns_per_report_max':max(wall),'allocated_bytes_per_report_median':statistics.median(alloc)})
(out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
print('all semantic checksums equal where applicable; raw timings retained',flush=True)
