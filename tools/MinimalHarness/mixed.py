#!/usr/bin/env python3
"""Native lifecycle and overlapping mixed-case regression runner."""
from pathlib import Path
import argparse,json,shutil,time,subprocess
import runtime
JOBS={
 'lifecycle-sp':(1,'lifecycle','normal',True,[]),
 'enemy-sp':(1,'lifecycle','normal',False,['--minimal-lifecycle-phase=enemy']),
 'enemy-mp3':(3,'lifecycle','normal',False,['--minimal-lifecycle-phase=enemy']),
 'lifecycle-mp3':(3,'lifecycle','normal',True,[]),
 'redraw-sp':(1,'startup-queue','instant',True,['--minimal-character=FOOL','--minimal-startup-discards=5']),
 'redraw-mp3':(3,'startup-queue','instant',False,['--minimal-character=FOOL','--minimal-startup-discards=5']),
 'transform-sp':(1,'startup-transform','instant',False,['--minimal-character=FOOL']),
 'transform-mp3':(3,'startup-transform','instant',False,['--minimal-character=FOOL']),
 'skew-mp3':(3,'peer-skew','normal',True,['--minimal-skew-varied']),
}
def main():
 parser=argparse.ArgumentParser(description=__doc__)
 parser.add_argument('--case',action='append',choices=list(JOBS))
 parser.add_argument('--output',type=Path,required=True)
 parser.add_argument('--no-build',action='store_true',help='Use an explicitly pinned, already built core/harness pair')
 args=parser.parse_args();out=args.output.resolve()
 if out.exists():parser.error('Output already exists; choose a new evidence directory')
 jobs=args.case or list(JOBS)
 runtime.no_game()
 if not args.no_build:
  subprocess.run(['dotnet','build',str(runtime.REPO/'tools/MinimalHarness'),'-c','Release'],check=True,cwd=runtime.REPO)
  subprocess.run(['dotnet','run','--project',str(runtime.REPO/'tools/MinimalCheck'),'-c','Release','--',str(runtime.REPO/'UndoSync'),str(runtime.APP/'Contents/Resources/data_sts2_macos_arm64')],check=True,cwd=runtime.REPO)
 root=runtime.prepare();results=[];started=time.monotonic()
 try:
  out.mkdir(parents=True)
  for label in jobs:
   peers,suite,speed,rendered,extra=JOBS[label];entry={'case':label,'peers':peers,'suite':suite,'rendered':rendered}
   try:
    runtime.run(root,label,peers,suite,headless=not rendered,speed=speed,retained=True,extra_args=extra)
    entry['verdict']='PASS'
   except Exception as error:entry.update(verdict='FAIL',error=str(error));raise
   finally:
    results.append(entry)
    if (root/label).exists():
     shutil.copytree(root/label,out/label)
     entry['run']=json.loads((root/label/'run.json').read_text())
    runtime.save(out/'summary.json',{'root':str(root),'results':results,'requested':jobs,'complete':len(results)==len(jobs) and all(x['verdict']=='PASS' for x in results),'elapsed_seconds':round(time.monotonic()-started,3)})
 finally:
  runtime.restore(root)
  if out.is_dir():shutil.copy2(root/'cleanup.json',out/'cleanup.json')
 print('MIXED_BATCH_PASS cases='+str(len(results)),flush=True)
if __name__=='__main__':main()
