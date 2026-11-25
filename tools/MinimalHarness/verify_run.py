#!/usr/bin/env python3
"""Evidence verdicts; process exit 0 alone is never acceptance."""
from pathlib import Path
import argparse
import json
import re


def verify(folder, require_input=None, require_transport=None):
    folder = Path(folder)
    issues, verdicts, sessions = [], {}, {}

    def issue(kind, message):
        issues.append({'kind': kind, 'message': message})

    try:
        run = json.loads((folder / 'run.json').read_text())
    except (OSError, ValueError) as error:
        return {'schema': 1, 'verdict': 'INFRA_ERROR', 'issues': [{'kind': 'INFRA_ERROR', 'message': str(error)}]}
    roles = run.get('roles') or (['ui'] if run['suite'] in ('viewport-ui', 'choice-ui', 'picker-ui', 'localization-ui')
                                 else ['host', 'client', 'client2', 'client3'][:run['peers']])
    expected_transport = require_transport or run.get('transport', 'enet')
    if roles == ['ui'] and require_transport is None:
        expected_transport = 'singleplayer'
    expected_core = run['dlls']['UndoSync']['UndoSync.dll']
    expected_harness = run['dlls']['MinimalHarness']['MinimalHarness.dll']
    if set(run['processes']) != set(roles):
        issue('INFRA_ERROR', 'Expected participant set differs from launched processes.')
    for role in roles:
        process = run['processes'].get(role)
        path = folder / f'verdict.{role}.json'
        if process is None or not path.exists():
            issue('INFRA_ERROR', f'{role}: missing process or structured verdict')
            continue
        try:
            value = json.loads(path.read_text())
            verdicts[role] = value
            log = (folder / f'{role}-terminal.log').read_text()
        except (OSError, ValueError) as error:
            issue('INFRA_ERROR', f'{role}: {error}')
            continue
        if value.get('pid') != process['pid'] or value.get('role') != role:
            issue('INFRA_ERROR', f'{role}: stale/mismatched PID or role')
        if process.get('exit') != 0 or value.get('verdict') != 'PASS':
            failure = value.get('failure') or ''
            kind = 'INFRA_ERROR' if 'FIXTURE_ERROR' in failure or 'TRACE_ERROR' in failure else 'UNCOVERED' if 'UNCOVERED' in failure else 'FAIL'
            issue(kind, f'{role}: process/scenario failed: {failure}')
        if value.get('core_sha256') != expected_core or value.get('harness_sha256') != expected_harness:
            issue('INFRA_ERROR', f'{role}: executed binary hash mismatch')
        if value.get('transport') != expected_transport:
            issue('INFRA_ERROR', f'{role}: observed transport {value.get("transport")} != {expected_transport}')
        if value.get('divergences', -1) != 0:
            issue('FAIL', f'{role}: native divergence counter not zero')
        if run['suite'] in ('fuzz', 'wuppy', 'turn-start', 'category', 'startup-queue', 'startup-transform', 'peer-skew', 'lifecycle'):
            native_exceptions = re.findall(r'^(?:\[ERROR\] |ERROR: )(System\.[^\r\n]+)', log, re.MULTILINE)
            if native_exceptions:
                issue('FAIL', f'{role}: native runtime exceptions ({len(native_exceptions)}): '
                      + '; '.join(sorted(set(native_exceptions))))
        if value.get('undos', 0) < 1 or value.get('target_checks', 0) < 1:
            issue('UNCOVERED', f'{role}: no completed target-state-checked undo')
        for marker in ('RESTORE_BEGIN', 'REPLAY_DONE', 'HISTORY_COMMIT', 'RESUME session='):
            if marker not in log:
                issue('UNCOVERED', f'{role}: mechanism marker absent: {marker}')
        if require_input == 'viewport' and (value.get('viewport_inputs', 0) < 1 or value.get('native_callbacks', 0) < 1):
            issue('UNCOVERED', f'{role}: viewport input did not reach a native callback')
        for session, peers, checksum in re.findall(r'RESUME session=(\w+) peers=(\d+) checksum=(\d+)', log):
            if int(peers) != len(roles):
                issue('FAIL', f'{role}: resume participant count differs')
            sessions.setdefault(session, {})[role] = checksum
        if expected_transport == 'steam':
            attaches = re.findall(r'NETWORK_ATTACH role=\w+ platform=(\w+)', log)
            if not attaches or any(platform != 'Steam' for platform in attaches):
                issue('INFRA_ERROR', f'{role}: original/replacement Steam transport not established')
    if not sessions:
        issue('UNCOVERED', 'No resumed replay session.')
    for session, checksums in sessions.items():
        if set(checksums) != set(roles) or len(set(checksums.values())) != 1:
            issue('FAIL', f'{session}: missing peer or final checksum mismatch')
    if not any(v.get('post_replay_actions', 0) > 0 for v in verdicts.values()):
        issue('UNCOVERED', 'No real post-replay action completed.')
    final_states = [v.get('final_state_sha256') for v in verdicts.values()]
    if len(final_states) != len(roles) or any(not s for s in final_states) or len(set(final_states)) != 1:
        issue('FAIL', 'Final native states missing or unequal.')
    if expected_transport == 'steam':
        ids = [v.get('net_id') for v in verdicts.values()]
        if len(set(ids)) != len(roles) or any(not str(i).isdigit() or int(i) < 2**32 for i in ids):
            issue('INFRA_ERROR', 'Distinct authenticated Steam identities were not established.')
    content_coverage = None
    if run['suite'] == 'fuzz':
        try:
            fixture = json.loads((folder/'content-fixture.json').read_text())
            resolved = json.loads((folder/'fixture-resolved.json').read_text())
            for role in roles:
                if json.loads((folder/f'content-fixture.{role}.json').read_text()) != fixture:
                    issue('INFRA_ERROR', f'{role}: fixture specification differs')
            traces = {role: [json.loads(line) for line in (folder/f'trace.{role}.jsonl').read_text().splitlines()] for role in roles}
            for role, steps in traces.items():
                targets = [x for x in steps if x['Kind'] == 'target']
                if len(targets) != len(roles): issue('UNCOVERED', f'{role}: proposer rotation incomplete')
                if not any(x['Kind'] == 'play' and x['Label'] == 'continued' for x in steps):
                    issue('UNCOVERED', f'{role}: continued action absent from trace')
            host_targets = [x for x in traces[roles[0]] if x['Kind'] == 'target']
            for role in roles[1:]:
                if [x for x in traces[role] if x['Kind'] == 'target'] != host_targets:
                    issue('FAIL', f'{role}: target trace differs')
            content_coverage = {
                'content_seed': fixture['ContentSeed'], 'schedule_seed': run.get('schedule_seed'),
                'resolved_factory_steps': len(resolved),
                'models': sorted(set(x.split('=', 2)[-1] for x in resolved)),
                'played_card_models': sorted(set(x['Model'] for steps in traces.values() for x in steps if x['Kind'] == 'play')),
                'live_choices': sum(v.get('selections', 0) for v in verdicts.values()),
                'replayed_event_counts': [int(n) for role in roles for n in re.findall(r'REPLAY_DONE events=(\d+)', (folder/f'{role}-terminal.log').read_text())]
            }
        except (OSError, ValueError, KeyError) as error:
            issue('INFRA_ERROR', 'Fuzz artifacts missing/invalid: ' + str(error))
    if run['suite'] == 'category':
        proofs = []
        for role in roles:
            try:
                proof = json.loads((folder/f'category-proof.{role}.json').read_text())
                proofs.append(proof)
                profile = proof['profile']
                expected = max(2, len(roles))
                if proof['restores'] != expected or proof['targets'] != expected or proof['target_events'] < 1:
                    issue('UNCOVERED', f'{role}: category repeated nonempty target restoration missing')
                if profile.endswith('-pending') and proof['pending'] != expected:
                    issue('UNCOVERED', f'{role}: actual pending native choice not exercised on every restore')
                required = ['card'] if not profile.endswith('-pending') and profile != 'potion' else []
                if profile in ('turn-end', 'mixed'): required += ['turn-end']
                if profile in ('turn-start', 'mixed'): required += ['turn-start', 'boulder-vfx']
                if profile in ('combat-start', 'mixed'): required += ['combat-start']
                if profile in ('potion', 'mixed'): required += ['potion-use', 'potion-discard', 'potion-immediate']
                if profile in ('mixed', 'enemy-replay'): required += ['enemy-action']
                for key in required:
                    if proof['live'].get(key, 0) < 1 or proof['replayed'].get(key, 0) < expected:
                        issue('UNCOVERED', f'{role}: actual native {key} absent live or replay')
                if profile == 'enemy-replay':
                    boss_log = (folder/f'{role}-terminal.log').read_text()
                    if proof['choices'] != 1 or 'CATEGORY_BOSS_CHOICE_COMPLETED native=true' not in boss_log or 'all actual boss choices are inside the restored historical prefix' not in boss_log:
                        issue('UNCOVERED', f'{role}: actual completed mandatory boss choice was not recorded')
                    counts = [int(n) for n in re.findall(r'REPLAY_DONE .*?choices=(\d+)', boss_log)]
                    if len(counts) != expected or any(n < len(roles) for n in counts):
                        issue('UNCOVERED', f'{role}: historical boss choices not replayed on every restore')
                if 'CATEGORY_PASS profile=' + profile not in (folder/f'{role}-terminal.log').read_text():
                    issue('UNCOVERED', f'{role}: category terminal mechanism marker absent')
            except (OSError, ValueError, KeyError) as error:
                issue('INFRA_ERROR', f'{role}: category proof missing/invalid: {error}')
        if proofs and len({(p['profile'], p['target_events'], p['target_state']) for p in proofs}) != 1:
            issue('FAIL', 'Category target/profile differs across peers')
        if proofs and proofs[0]['profile'] in ('choice', 'potion', 'mixed', 'enemy-replay') and sum(p['choices'] for p in proofs) < 1:
            issue('UNCOVERED', 'No native viewport card choice completed')
        if len(roles) > 1 and proofs and proofs[0]['profile'] in ('potion', 'mixed') and any(p['actor'] == p['chooser'] for p in proofs):
            issue('UNCOVERED', 'Other-player potion choice branch not reached')
    if run['suite'] == 'lifecycle':
        for role in roles:
            try:
                proof = json.loads((folder/f'lifecycle-proof.{role}.json').read_text())
                log = (folder/f'{role}-terminal.log').read_text()
                if proof['checks'] < 3 or proof['late_views'] or proof['live_detach']:
                    issue('FAIL', f'{role}: old native work outlived view teardown')
                if log.count('LIFECYCLE_TRIGGER hand=') < proof['checks'] or log.count('LIFECYCLE_DETACH oldCombatLive=False') < proof['checks']:
                    issue('UNCOVERED', f'{role}: pending draw or canceled-before-detach mechanism absent')
                for attempt in range(proof['checks']):
                    if (folder/f'lifecycle-restored-{attempt}.state.{role}').read_text() != proof['expected']:
                        issue('FAIL', f'{role}: lifecycle restored state mismatch')
                if 'LIFECYCLE_PASS' not in log: issue('UNCOVERED', f'{role}: lifecycle terminal marker absent')
            except (OSError, ValueError, KeyError) as error:
                issue('INFRA_ERROR', f'{role}: lifecycle proof missing/invalid: {error}')
    if run['suite'] == 'startup-queue':
        proofs = []
        for role in roles:
            try:
                proof = json.loads((folder/f'startup-proof.{role}.json').read_text())
                proofs.append(proof)
                target = json.loads((folder/f'startup-target.{role}.json').read_text())
                if (proof['target'], proof['expected'], proof['mode']) != (target['target'], target['expected'], target['mode']):
                    issue('FAIL', f'{role}: startup proof differs from original target')
                original = json.loads((folder/f'startup-original.{role}.json').read_text())
                log = (folder/f'{role}-terminal.log').read_text()
                expected = max(2, len(roles))
                if proof['trace_checks'] != expected or proof['target'] < 1 or not proof['native_input_while_start']:
                    issue('UNCOVERED', f'{role}: startup queued input/repeated restoration absent')
                for attempt in range(expected):
                    trace = json.loads((folder/f'startup-replay-{attempt}.{role}.json').read_text())
                    if trace != original:
                        issue('FAIL', f'{role}: native action IDs/order differ on restore {attempt}')
                    if (folder/f'startup-queue-restored-{attempt}.state.{role}').read_text() != target['expected']:
                        issue('FAIL', f'{role}: saved native target state differs on restore {attempt}')
                if log.count('REPLAY_QUEUED_BEHIND_CHOICE ') < expected:
                    issue('UNCOVERED', f'{role}: startup choice queue bypass not exercised on every restore')
                for marker in ('STARTUP_INPUT_QUEUED mode='+proof['mode']+' phase=Start',
                               'real recorded order is Hook -> queued input -> Choice -> Resume',
                               'STARTUP_QUEUE_PASS mode='+proof['mode']):
                    if marker not in log: issue('UNCOVERED', f'{role}: absent startup mechanism: {marker}')
                if any(x in log for x in ('Ignoring end turn action.', 'was in state Canceled!', 'Could not map ID', 'REPLAY_ACTION_CANCELED ')):
                    issue('FAIL', f'{role}: skipped turn, canceled action or unmapped card during startup replay')
            except (OSError, ValueError, KeyError) as error:
                issue('INFRA_ERROR', f'{role}: startup proof missing/invalid: {error}')
        if proofs and len({(p['mode'], p['target'], p['expected']) for p in proofs}) != 1:
            issue('FAIL', 'Startup queued-input target differs across peers')
    if run['suite'] in ('turn-start','startup-transform'):
        for role in roles:
            try:
                proof = json.loads((folder/f'turn-start-proof.{role}.json').read_text())
                target = json.loads((folder/f'turn-start-target.{role}.json').read_text())
                if proof['turns'] != 7 or proof['final_turn'] != 9 or proof['replay_events'] != target['events'] or target['turn'] != 8:
                    issue('UNCOVERED', f'{role}: multi-turn target or continuation incomplete')
                if len(target.get('joys_by_player', {})) != len(roles) or any(n < 4 for n in target['joys_by_player'].values()):
                    issue('UNCOVERED', f'{role}: native Joy generation missing for an actor')
                if target['joys'] < 4 * len(roles):
                    issue('UNCOVERED', f'{role}: native Ecstasy transformations were not exercised')
                log = (folder/f'{role}-terminal.log').read_text()
                if any(x in log for x in ('Ignoring end turn action.', 'was in state Canceled!', 'Could not map ID', 'REPLAY_ACTION_CANCELED ')):
                    issue('FAIL', f'{role}: skipped turn, canceled action or unmapped card during replay')
                checks = [int(n) for n in re.findall(r'REPLAY_DONE .*?turnChecks=(\d+)', log)]
                observed_waits = log.count('REPLAY_TURN_READY ')
                if not (len(checks) == verdicts[role]['undos'] and all(n >= 21 * len(roles) for n in checks)) and observed_waits < 6 * verdicts[role]['undos']:
                    issue('UNCOVERED', f'{role}: readiness checks were not observed on each restored turn')
                # No sleep is required when the native setup has already finished.
                # Fast visual binding can make the same readiness gate synchronous.
                if 'transforms=' in log:
                    transforms = [int(n) for n in re.findall(r'MOUNTED_UI_READY .*?transforms=(\d+)', log)]
                    if len(transforms) != verdicts[role]['undos'] or any(n < 4 for n in transforms):
                        issue('UNCOVERED', f'{role}: native transformed-card visual bindings were not exercised')
            except (OSError, ValueError, KeyError) as error:
                issue('INFRA_ERROR', f'{role}: turn-start proof missing/invalid: {error}')
    if run['suite'] == 'peer-skew':
        votes=0
        for role in roles:
            try:
                proof=json.loads((folder/f'skew-proof.{role}.json').read_text())
                if proof['characters']!=['FOOL','IRONCLAD','SILENT'] or proof['restores']!=3:
                    issue('UNCOVERED',f'{role}: heterogeneous participant/proposer rotation absent')
                votes+=proof['pending_votes']
                log=(folder/f'{role}-terminal.log').read_text()
                if log.count('PEER_SKEW_BEFORE_FINAL actionPending=true')!=3 or 'PEER_SKEW_PASS' not in log:
                    issue('UNCOVERED',f'{role}: partial-choice/action overlap absent')
                varied=any('--minimal-skew-varied' in proc['args'] for proc in run['processes'].values())
                if varied and (len(proof.get('targets',[]))!=3 or len({t['target'] for t in proof['targets']})<2):
                    issue('UNCOVERED',f'{role}: different earlier/later targets were not exercised')
                for attempt in range(3):
                    expected=proof['targets'][attempt]['expected'] if varied else proof['expected']
                    if (folder/f'skew-restored-{attempt}.state.{role}').read_text()!=expected:
                        issue('FAIL',f'{role}: heterogeneous restored target differs')
            except (OSError,ValueError,KeyError) as error:issue('INFRA_ERROR',f'{role}: skew proof missing/invalid: {error}')
        if votes!=3:issue('UNCOVERED','Final vote while native action pending was not observed on each restore')
    if run['suite'] in ('startup-transform','peer-skew') or run['suite'] == 'startup-queue' and any('--minimal-startup-discards=' in a and not a.endswith('=0') for proc in run['processes'].values() for a in proc['args']):
        for role in roles:
            try:
                proof=json.loads((folder/f'startup-composition.{role}.json').read_text())
                repeats=max(2,len(roles)); peers=len(roles)
                if proof['discards']<1 or proof['live_redraws']<peers or proof['live_shuffles']<peers or proof['replay_redraws']<peers*repeats or proof['replay_shuffles']<peers*repeats:
                    issue('UNCOVERED',f'{role}: actual startup discard/redraw/shuffle composition absent')
            except (OSError,ValueError,KeyError) as error: issue('INFRA_ERROR',f'{role}: startup composition proof invalid: {error}')
    if run['suite'] == 'wuppy':
        for role in roles:
            try:
                proof = json.loads((folder/f'wuppy-proof.{role}.json').read_text())
                if proof['damage_turns'] < 1 or proof['deaths'] < max(2, len(roles)) + 1 or proof['restores'] != max(2, len(roles)) + 1:
                    issue('UNCOVERED', f'{role}: Wuppy damage/death/repeated restore not executed')
                if not all(proof.get(k) for k in ('native_event', 'native_enemy_damage', 'next_combat', 'owner_ui_checks')):
                    issue('UNCOVERED', f'{role}: Wuppy lifecycle coverage incomplete')
                for label in ('opening', 'target', 'partial-target', 'partial-restored', 'next-combat'):
                    pets = json.loads((folder/f'wuppy-{label}.{role}.json').read_text())
                    if len(pets) != len(roles) or any(p['count'] != 1 or p['owner'] != p['NetId'] or p['hp'] != p['CurrentHp'] or p['max'] != proof['max_hp'] for p in pets):
                        issue('FAIL', f'{role}: Wuppy owner/count/HP mismatch at {label}')
            except (OSError, ValueError, KeyError) as error:
                issue('INFRA_ERROR', f'{role}: Wuppy proof missing/invalid: {error}')
    if run.get('phases'):
        for key in ('busy_votes', 'preparing_before_release', 'skew_holds', 'recorder_faults'):
            observed = sum(v.get('phase_coverage', {}).get(key, 0) for v in verdicts.values())
            if observed != len(roles):
                issue('UNCOVERED', f'Phase condition {key}: observed {observed}, required {len(roles)}')
    if run.get('retained_session'):
        for role, value in verdicts.items():
            for key in ('session_replacements', 'connections_opened', 'connections_closed', 'native_checksums_during_replay'):
                if key not in value: issue('INFRA_ERROR', f'{role}: harness lacks retained-session instrumentation: {key}')
                elif value[key] != 0: issue('FAIL', f'{role}: {key} must remain zero')
            if value.get('suppressed_checksum_calls', 0) < 1:
                issue('UNCOVERED', f'{role}: no actual native checksum generation probe suppressed')
            if expected_transport != 'singleplayer' and value.get('preserved_disconnects', 0) != value.get('undos', -1):
                issue('FAIL', f'{role}: original transport not preserved across every undo')
            log = (folder/f'{role}-terminal.log').read_text()
            if expected_transport != 'singleplayer' and log.count('TRANSPORT_RETAINED ') < value.get('undos', 0):
                issue('UNCOVERED', f'{role}: retained native transport/lobby not observed')
            if 'RESTORE_BEGIN' in log:
                replay_log = log.split('RESTORE_BEGIN', 1)[1].split('HARNESS_PASS', 1)[0]
                if any(marker in replay_log for marker in ('RESTORE_HOST_LISTENING', 'RESTORE_JOIN_RETRY', '[HandshakeManager] Got handshake')):
                    issue('FAIL', f'{role}: unexpected connection recreation during undo')
            if run.get('phases') and (value.get('dropped_checksum_messages', 0) < 1 or log.count('CHECKSUM_ISOLATION_PROBE_PASS') != len(roles)):
                issue('UNCOVERED', f'{role}: stale native checksum receive isolation not proved')
    kinds = {i['kind'] for i in issues}
    result = next((k for k in ('INFRA_ERROR', 'FAIL', 'UNCOVERED') if k in kinds), 'PASS')
    return {'schema': 1, 'verdict': result, 'transport': expected_transport, 'suite': run['suite'],
            'core_sha256': expected_core, 'harness_sha256': expected_harness,
            'roles': roles, 'participants': verdicts, 'sessions': sessions, 'issues': issues,
            'input_requirement': require_input, 'content_coverage': content_coverage}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    parser.add_argument('--require-input', choices=['viewport'])
    parser.add_argument('--require-transport', choices=['enet', 'steam', 'singleplayer'])
    args = parser.parse_args()
    report = verify(args.folder, args.require_input, args.require_transport)
    (args.folder / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report['verdict'] == 'PASS' else 1)
