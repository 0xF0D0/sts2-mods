#!/usr/bin/env python3
"""Task-owned real ENet fixture; never use alongside a user's game instance."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, tempfile, time

REPO = Path(__file__).resolve().parents[2]
APP = Path.home() / 'Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app'
MODS = APP / 'Contents/MacOS/mods'
ACCOUNTS = Path.home() / 'Library/Application Support/SlayTheSpire2/default'
IDS = {'host': 1, 'client': 1001, 'client2': 2001, 'client3': 3001, 'ui': 1}

def save(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')

def hashes(path):
    return {str(p.relative_to(path)): hashlib.sha256(p.read_bytes()).hexdigest() for p in path.rglob('*') if p.is_file()}

def no_game():
    p = subprocess.run(['pgrep', '-fl', 'Slay the Spire 2'], capture_output=True, text=True)
    if p.stdout.strip():
        raise RuntimeError('Existing game/caffeinate process: ' + p.stdout)
    p = subprocess.run(['lsof', '-nP', '-iUDP:33771'], capture_output=True, text=True)
    if p.stdout.strip():
        raise RuntimeError('UDP 33771 is occupied: ' + p.stdout)

def deploy():
    for name, source in [('UndoSync', REPO/'UndoSync'), ('MinimalHarness', REPO/'tools/MinimalHarness')]:
        target = MODS/name
        target.mkdir(exist_ok=True)
        for file in [source/'bin/Release/net9.0'/f'{name}.dll', source/f'{name}.json']:
            shutil.copy2(file, target/file.name)

def prepare():
    no_game()
    root = Path(tempfile.mkdtemp(prefix='undosync-extended-'))
    state = {'backups': {}, 'hashes': {}, 'pids': []}
    for tag, path in [(f'account-{i}', ACCOUNTS/str(i)) for i in IDS.values()] + [(f'mod-{n}', MODS/n) for n in ['UndoSync', 'MinimalHarness']]:
        if tag in state['backups']:
            continue
        state['backups'][tag] = path.exists()
        if path.exists():
            shutil.copytree(path, root/tag)
            state['hashes'][tag] = hashes(path)
    save(root/'fixture.json', state)
    deploy()
    known = [p.name for p in MODS.iterdir() if p.is_dir()]
    order = ['UndoSync', 'MinimalHarness'] + [n for n in known if n not in ['UndoSync', 'MinimalHarness']]
    for identity in set(IDS.values()):
        account = ACCOUNTS/str(identity)
        account.mkdir(exist_ok=True)
        if not (account/'modded').exists():
            shutil.copytree(root/'account-1/modded', account/'modded')
        settings = json.loads((root/'account-1/settings.save').read_text())
        settings.update(seen_ea_disclaimer=True, skip_intro_logo=True, fullscreen=False,
                        window_position={'X': 80, 'Y': 80}, window_size={'X': 1280, 'Y': 720})
        settings['mod_settings'] = {'mods_enabled': True, 'mod_list': [
            {'id': n, 'source': 'mods_directory', 'is_enabled': n in ['UndoSync', 'MinimalHarness']} for n in order
        ] + [{'id': n, 'source': 'steam_workshop', 'is_enabled': False} for n in ['UndoSync', 'SaveMerger']]}
        for name in ['settings.save', 'settings.save.backup']:
            save(account/name, settings)
    print(root, flush=True)

def run(root, label, peers, suite, headless=True, seed='MINIMAL_MP_A'):
    no_game()
    folder = root/label
    folder.mkdir()
    roles = ['ui'] if suite in ('choice-ui', 'picker-ui') else ['host', 'client', 'client2', 'client3'][:peers]
    processes = {}
    record = {'label': label, 'peers': peers, 'suite': suite, 'seed': seed, 'headless': headless, 'processes': {}}
    record['dlls'] = {n: hashes(MODS/n) for n in ['UndoSync', 'MinimalHarness']}
    started = time.monotonic()
    try:
        for role in roles:
            args = ['caffeinate', '-dimsu', str(APP/'Contents/MacOS/Slay the Spire 2'),
                    '--force-steam', 'off', '--clientId', str(IDS[role]), '--minimal-harness='+role,
                    '--minimal-dir='+str(folder), '--minimal-peers='+str(peers), '--minimal-suite='+suite,
                    '--minimal-seed='+seed]
            args += ['--headless'] if headless else ['--windowed', '--resolution', '1280x720']
            with (folder/(role+'-terminal.log')).open('w') as out:
                process = subprocess.Popen(args, cwd=APP/'Contents/MacOS', stdout=out, stderr=subprocess.STDOUT)
            processes[role] = process
            record['processes'][role] = {'pid': process.pid, 'args': args}
            save(folder/'run.json', record)
            print(f'{label} {role} PID {process.pid}', flush=True)
            if role == 'host':
                deadline = time.monotonic()+45
                while 'HOST_LISTENING port=33771' not in (folder/'host-terminal.log').read_text():
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError('Host startup failed; inspect PID-bound terminal log.')
                    time.sleep(.5)
        aborted = False
        while any(p.poll() is None for p in processes.values()):
            if not aborted and any(p.poll() not in (None, 0) for p in processes.values()):
                (folder/'abort').write_text('Peer harness terminal failure; inspect logs.\n')
                aborted = True
            if suite != 'preview' and time.monotonic()-started > 600:
                (folder/'abort').write_text('Parent classified run deadline; inspect current logs.\n')
                raise TimeoutError('Parent run deadline; PID-bound logs retained.')
            time.sleep(1)
    finally:
        for role, process in processes.items():
            if process.poll() is None:
                # Only this fixture's known PID, after the run was classified above.
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait()
            record['processes'][role]['exit'] = process.returncode
            print(f'{label} {role} PID {process.pid} exit {process.returncode}', flush=True)
        record['verdict'] = 'PASS' if len(processes)==len(roles) and all(p.returncode == 0 for p in processes.values()) else 'FAIL'
        save(folder/'run.json', record)
    if suite == 'preview' and record['verdict'] == 'PASS':
        record['verdict'] = 'CLOSED'; save(folder/'run.json', record)
    print(label, record['verdict'], flush=True)

def restore(root):
    no_game()
    state = json.loads((root/'fixture.json').read_text())
    result = {}
    for tag, original in state['backups'].items():
        path = ACCOUNTS/tag.removeprefix('account-') if tag.startswith('account-') else MODS/tag.removeprefix('mod-')
        if path.exists():
            shutil.move(str(path), root/('tested-'+tag))
        if original:
            shutil.copytree(root/tag, path)
            assert hashes(path) == state['hashes'][tag], tag
        else:
            assert not path.exists(), tag
        result[tag] = 'RESTORED'
    result['processes'] = 'NONE'; result['udp33771'] = 'FREE'
    save(root/'cleanup.json', result)
    print(json.dumps(result), flush=True)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['prepare', 'deploy', 'run', 'restore'])
    parser.add_argument('--root', type=Path)
    parser.add_argument('--label', default='extended')
    parser.add_argument('--peers', type=int, default=4, choices=[1,2,3,4])
    parser.add_argument('--suite', default='extended', choices=['basic','extended','choice-ui','picker-ui','preview','regression','vote-loss','replay-loss'])
    parser.add_argument('--visible', action='store_true')
    parser.add_argument('--seed', default='MINIMAL_MP_A')
    args = parser.parse_args()
    if args.command == 'prepare': prepare()
    elif args.command == 'deploy': no_game(); deploy()
    elif args.command == 'run': run(args.root, args.label, args.peers, args.suite, not args.visible, args.seed)
    else: restore(args.root)
