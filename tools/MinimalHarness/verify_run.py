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
