#!/usr/bin/env python3
"""Sequential real-game fuzz sweeps. Every failed case is retained; no retry-to-green."""
from pathlib import Path
import argparse
import json
import subprocess
import sys
import runtime


def main():
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest='command', required=True)
    run = sub.add_parser('run')
    run.add_argument('--transport', choices=['enet', 'singleplayer'])
    run.add_argument('--peers', type=int, choices=[1, 2, 3, 4], default=1)
    run.add_argument('--cases', type=int, default=1)
    run.add_argument('--content-seed', required=True)
    run.add_argument('--schedule-seed', required=True)
    run.add_argument('--phases', action='store_true')
    run.add_argument('--speed', choices=['instant', 'normal'], default='instant')
    run.add_argument('--character', choices=['FOOL'])
    run.add_argument('--mode', choices=['standard', 'blitz'], default='standard')
    run.add_argument('--vary-content', action='store_true', help='Vary content as well as timing when using --phases.')
    run.add_argument('--prefer-content', action='store_true', help='Randomly prefer playable non-basic cards in the native hand.')
    run.add_argument('--repeat', type=int, default=1, help='Repeat each exact seed pair, retaining separate runs.')
    repro = sub.add_parser('repro')
    repro.add_argument('--trace', type=Path, required=True, help='Captured case directory or trace.host.jsonl.')
    args = p.parse_args()
    source = None
    if args.command == 'repro':
        source = args.trace.resolve()
        if source.is_file(): source = source.parent
        saved = json.loads((source/'run.json').read_text())
        if saved['suite'] != 'fuzz' or saved['transport'] not in ('enet', 'singleplayer'):
            p.error('This runner reproduces captured native singleplayer/ENet fuzz cases only.')
        jobs = [(saved['seed'], saved['schedule_seed'], saved['peers'], 'repro')]
        original_args = next(iter(saved['processes'].values()))['args']
        extra_args = [a for a in original_args if a.startswith(('--minimal-character=', '--minimal-mode=')) or a == '--minimal-prefer-content']
    else:
        transport = args.transport or ('singleplayer' if args.peers == 1 else 'enet')
        if (transport == 'singleplayer') != (args.peers == 1):
            p.error('singleplayer requires peers=1; enet requires peers=2/3/4.')
        if args.phases and args.peers == 1:
            p.error('--phases requires real multiplayer peers.')
        if not 1 <= args.cases <= 1000 or not 1 <= args.repeat <= 10:
            p.error('cases must be 1..1000; repeat must be 1..10.')
        extra_args = ['--minimal-mode='+args.mode]
        if args.character: extra_args.append('--minimal-character='+args.character)
        if args.prefer_content: extra_args.append('--minimal-prefer-content')
        jobs = [(args.content_seed + (f'-{i}' if args.cases > 1 and (not args.phases or args.vary_content) else ''),
                 args.schedule_seed + (f'-{i}' if args.cases > 1 else ''), args.peers, f'case-{i:04}-repeat-{r}')
                for i in range(args.cases) for r in range(args.repeat)]
    runtime.no_game()
    subprocess.run(['dotnet', 'build', 'tools/MinimalHarness', '-c', 'Release', '-warnaserror'], cwd=runtime.REPO, check=True)
    root = runtime.prepare()
    outcomes = []
    try:
        for content, schedule, peers, label in jobs:
            try:
                speed = saved.get('speed', 'instant') if source else args.speed
                phases = saved.get('phases', False) if source else args.phases
                runtime.run(root, label, peers, 'fuzz', seed=content, schedule_seed=schedule,
                            repro=source, speed=speed, phases=phases, retained=True, extra_args=extra_args)
                if not source and args.repeat > 1:
                    previous = root/(label.rsplit('-repeat-', 1)[0]+'-repeat-0')
                    if previous != root/label:
                        for filename in ['content-fixture.json', 'fixture-resolved.json'] + [f'trace.{role}.jsonl' for role in (['ui'] if peers == 1 else ['host','client','client2','client3'][:peers])]:
                            if (previous/filename).read_bytes() != (root/label/filename).read_bytes():
                                raise RuntimeError('NONDETERMINISTIC_TRACE: ' + filename)
                outcomes.append({'case': label, 'verdict': 'PASS'})
            except Exception as error:
                report = runtime.verify(root/label, require_input='viewport')
                if report['verdict'] == 'PASS':
                    report['verdict'] = 'FAIL'
                    report['issues'].append({'kind': 'FAIL', 'message': str(error)})
                runtime.save(root/label/'summary.json', report)
                outcomes.append({'case': label, 'verdict': report['verdict'] if report['verdict'] != 'PASS' else 'FAIL', 'error': str(error)})
                # Preserve first failure; no silent skip or resampling.
                break
    finally:
        runtime.restore(root)
        runtime.save(root/'batch.json', {'schema': 1, 'outcomes': outcomes, 'repro': str(source) if source else None})
        print('ARTIFACT_ROOT=' + str(root), flush=True)
    return 0 if len(outcomes) == len(jobs) and all(x['verdict'] == 'PASS' for x in outcomes) else 1


if __name__ == '__main__':
    sys.exit(main())
