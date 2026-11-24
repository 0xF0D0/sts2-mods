#!/usr/bin/env python3
"""Repeatable native category regressions: isolated representatives + mixtures."""
from pathlib import Path
import argparse,json,re,shutil,subprocess,time
import runtime

STARTUP = ['startup-use', 'startup-discard', 'startup-select']
ISOLATED = ['choice', 'card', 'turn-end', 'turn-start', 'turn-transform', 'choice-pending', 'potion', 'potion-pending', 'combat-start'] + STARTUP
CATEGORIES = {'turn-transform':['C2'],'turn-end':['C1'],'turn-start':['C2'],'card':['C3'],'choice':['C3'],
              'choice-pending':['C3'],'potion':['C5'],'potion-pending':['C5'],'combat-start':['C6'],
              'enemy-replay':['C4'],'mixed':['C1','C2','C3','C4','C5','C6']}

def validate_case(folder,case):
    """Additional category-path checks for the reused native fault fixtures."""
    run=json.loads((folder/'run.json').read_text())
    logs={role:(folder/f'{role}-terminal.log').read_text() for role in run['roles']}
    for role,log in logs.items():
        errors=re.findall(r'^(?:\[ERROR\] |ERROR: )(System\.[^\r\n]+)',log,re.MULTILINE)
        if errors:raise RuntimeError(f'{role}: native exceptions: '+ '; '.join(sorted(set(errors))))
        if case=='enemy-choice' and 'KNOWLEDGE_CHOICE_OPEN native=true' not in log:
            raise RuntimeError(f'{role}: native mandatory boss choice not reached')
        if case=='turn-choice' and 'TURN_CHOICE_OPEN card=TOOLS_OF_THE_TRADE' not in log:
            raise RuntimeError(f'{role}: native turn-start choice not reached')
    if case=='enemy-choice':
        completed=[r for r,l in logs.items() if 'KNOWLEDGE_LOCAL_CHOICE_COMPLETED chooser=client remotePending=true' in l]
        if completed!=['client']:raise RuntimeError('Boss-choice completed/pending peer split was not exercised')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--case',action='append',choices=ISOLATED+['mixed','enemy-replay','enemy-choice','turn-choice'])
    parser.add_argument('--peers',type=int,choices=[1,3],help='Filter/override participants; boss/turn pending fixtures require three peers')
    parser.add_argument('--all-peers',action='store_true',help='Also repeat every isolated profile with three peers')
    parser.add_argument('--visible',action='store_true',help='Render all cases; first choice focus and mixed integration always render')
    parser.add_argument('--list',action='store_true')
    parser.add_argument('--no-build',action='store_true')
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    if args.case:
        jobs=[(c,3 if c in ['enemy-choice','turn-choice'] else args.peers or 1) for c in args.case]
        if args.peers==1 and any(c in ['enemy-choice','turn-choice'] for c in args.case):parser.error('Native multiplayer choice fixtures require --peers 3')
    else:
        jobs=[(c,1) for c in ISOLATED]+[('mixed',1),('potion',3),('enemy-replay',3),('enemy-choice',3),('turn-choice',3)]+[(c,3) for c in STARTUP]+[('mixed',3)]
        if args.all_peers:jobs += [(c,3) for c in ISOLATED if c!='potion' and c not in STARTUP]
        if args.peers:jobs=[j for j in jobs if j[1]==args.peers]
    if args.list:
        print(json.dumps([{'case':c,'peers':n} for c,n in jobs],indent=2));return
    if args.output and args.output.exists():parser.error('Output already exists; choose a new evidence folder')
    runtime.no_game()
    if not args.no_build:
        subprocess.run(['dotnet','build',str(runtime.REPO/'tools/MinimalHarness'),'-c','Release','-p:BuildProjectReferences=false'],check=True,cwd=runtime.REPO)
        subprocess.run(['dotnet','run','--project',str(runtime.REPO/'tools/MinimalCheck'),'-c','Release','--',str(runtime.REPO/'UndoSync'),str(runtime.APP/'Contents/Resources/data_sts2_macos_arm64')],check=True,cwd=runtime.REPO)
    root=runtime.prepare();results=[];started=time.monotonic()
    output=args.output or runtime.REPO/'tools/MinimalHarness/results'/time.strftime('%Y-%m-%d')/('categories-'+root.name)
    output=output.resolve()
    print('CATEGORY_BATCH_ROOT '+str(root),flush=True)
    try:
        output.mkdir(parents=True,exist_ok=False)
        for number,(case,peers) in enumerate(jobs):
            label=f'{number:02}-{case}-{peers}p'
            if case=='enemy-choice':suite='fault-knowledge-choice';extra=['--minimal-knowledge-chooser=client']
            elif case=='turn-choice':suite='fault-turn-choice';extra=['--minimal-choice-card=TOOLS_OF_THE_TRADE']
            elif case=='turn-transform':suite='turn-start';extra=['--minimal-character=FOOL']
            elif case in STARTUP:suite='startup-queue';extra=['--minimal-queued-action='+case.removeprefix('startup-'),'--minimal-character=FOOL']
            else:suite='category';extra=['--minimal-category='+case]
            categories=['C2','C5'] if case in STARTUP else CATEGORIES.get(case,['C4'] if case=='enemy-choice' else ['C2'])
            rendered=args.visible or case in ['choice','startup-use'] and peers==1 or case=='mixed' and peers==3
            result={'case':case,'peers':peers,'categories':categories,'suite':suite,'label':label,'rendered':rendered}
            try:
                runtime.run(root,label,peers,suite,headless=not rendered,speed='instant',retained=True,extra_args=extra)
                validate_case(root/label,case)
                result['verdict']='PASS'
            except Exception as error:
                result.update(verdict='FAIL',error=str(error));raise
            finally:
                results.append(result)
                folder=root/label
                if folder.exists():
                    shutil.copytree(folder,output/label)
                    result['run']=json.loads((folder/'run.json').read_text())
                runtime.save(output/'summary.json',{'schema':1,'root':str(root),'results':results,'requested_jobs':[{'case':c,'peers':n} for c,n in jobs],'elapsed_seconds':round(time.monotonic()-started,3),'runtime':'native local; singleplayer/ENet only','complete':len(results)==len(jobs) and all(x['verdict']=='PASS' for x in results)})
    finally:
        runtime.restore(root)
        if output.is_dir():shutil.copy2(root/'cleanup.json',output/'cleanup.json')
        print('CATEGORY_BATCH_EVIDENCE '+str(output),flush=True)
    print(f'CATEGORY_BATCH_PASS cases={len(results)} seconds={time.monotonic()-started:.3f}',flush=True)

if __name__=='__main__':main()
