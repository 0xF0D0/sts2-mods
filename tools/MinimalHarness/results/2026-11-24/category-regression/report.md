# Category regression results — 2026-11-24

**PASS: 15 distinct default case variants, 16 selected runs, 28 native processes,
34 distinct Undo sessions and 62 peer-local reconstructions.** The extra run is
a separate three-peer mixed integration after adding the completed-boss case.

Game: v0.113.1 / d09d48e5. Production v0.1.20 core remains byte-identical:
`a764b6ae018c8a35f8229390cd966ff6b4223b29e6844796b22aa0252e4afc5a`.

Run `python3 tools/MinimalHarness/categories.py` from the repository root.
Case contract and fixture brief: [CATEGORIES.md](../../../CATEGORIES.md).

## Selected evidence

| Case | Peers / native PIDs | Seconds | Undo sessions | Verdict |
| --- | --- | ---: | ---: | --- |
| choice | 1 / 73682 | 22.363 | 2 | PASS |
| card | 1 / 74147 | 12.567 | 2 | PASS |
| turn-end | 1 / 74407 | 12.578 | 2 | PASS |
| turn-start | 1 / 74668 | 20.614 | 2 | PASS |
| choice-pending | 1 / 75090 | 12.581 | 2 | PASS |
| potion | 1 / 75358 | 12.581 | 2 | PASS |
| potion-pending | 1 / 75627 | 12.575 | 2 | PASS |
| combat-start | 1 / 75892 | 12.580 | 2 | PASS |
| mixed | 1 / 76151 | 27.632 | 2 | PASS |
| potion | 3 / 76753, 76845, 76893 | 22.149 | 3 | PASS |
| enemy-choice | 3 / 77235, 77332, 77335 | 19.142 | 1 | PASS |
| turn-choice | 3 / 77639, 77729, 77772 | 19.146 | 1 | PASS |
| mixed | 3 / 78024, 78153, 78160 | 64.844 | 3 | PASS |
| enemy-replay | 3 / 80756, 80855, 80898 | 43.009 | 3 | PASS |
| mixed | 3 / 82553, 82651, 82694 | 65.831 | 3 | PASS |
| turn-transform | 1 / 85448 | 22.396 | 2 | PASS |

## Mechanisms and review

- Isolated C1–C6 representatives, recorded and pending card/potion choices,
  other-player choice branching, potion use/discard, initial relic reconstruction
  and ordinary enemy actions all executed through native gameplay paths.
- Rolling Boulder actually invoked its VFX and dealt native damage. Ecstasy
  additionally generated/transformed real Sadness/Joy over seven turns, restored
  twice and continued to turn nine. No model/ID/RNG/power/HP results were injected.
- Completed Knowledge Demon choices: all three players clicked Mind Rot. Every
  replay contained ten native events, three historical choices and three choice
  waits; all three restorations matched checksum 2180512702 and the full target.
  The separate pending-boss case retained a completed client versus pending peers.
- New category profiles restore twice in singleplayer and three times with
  rotating real proposers in multiplayer. Native result checksums and exact
  target/final states agree; online/cursor ownership and real subsequent input
  and next-turn progression pass. State divergences and native C# exceptions: 0.
- Independent verifier re-read every selected PID-bound log and structured proof
  with native viewport input required, plus mandatory-choice path checks.
- Parent inspected the focused Discovery restored hand (PID 69035), all three
  mixed restored peer views (78024/78153/78160), and the completed-boss restored
  host (80756). Later integration repeats the full mixed route on the extended
  harness. These are view/binding observations, not pixel comparisons.
- Main 13-case batch: 272.974 seconds (~4m33s). Summed measured duration of the
  15 distinct default cases, including the two later supplements: 336.757 seconds
  (~5m37s). This sum is not a separately measured single invocation of the
  expanded default command. Final mixed integration took 65.831 seconds;
  consult per-case run.json for exact process wall time.
- Metadata check passes: five production modules, 38 targets, 16 reflection
  surfaces. The checker now permits the existing audit-document directory only
  while it contains no .cs/.csproj/.dll; production module constraints remain.
- Harness Release build: zero warnings/errors. Python syntax/CLI list and
  whitespace checks pass. Production modules and version manifest are unchanged.

## Failed development attempts and cleanup

Initial missing import was a build failure before that binary launched. Focused
PID 67260 then exposed a fixture error: Freeze(currentCount) correctly rejects
a target at the present end of history. Inspect the prefix only after a genuine
mutation. The first batch potion case restored twice but incorrectly required
card-body coverage from its potion-only prefix; remove that unrelated condition
and explicitly test immediate Energy Potion effects as well. These attempts are
excluded from selected PASS totals; raw failure logs remain in full-01 and the
original temporary roots. No production bug fix was needed for these results.

All seven temporary backup roots, including failed/development runs, have native
cleanup receipts and an independent comparison of every live installed-mod/test
account file hash against both the primary backup and ZIP archive. Original
directory existence also matches. Final inventory: no game processes, UDP33771
free. No user Steam account profiles, published content, commits or releases
were changed by this test task. Raw engine resource/RID shutdown diagnostics
remain in logs; the zero-exception claim is specifically native C# exceptions.

## Limits

This is native unit-style category regression, not a mock-engine unit test or
an exhaustive card/character/encounter/timing fuzzer. These fixtures use Standard
mode, Ironclad plus the existing Fool transformation regression, one native weak
encounter seed and the selected native boss. Optional --all-peers extra isolated
combinations were not all executed. Steam/P2P and third-party mod interactions
were not tested. New entry paths require updating the source census/cases; a
green result cannot prove every future card implementation safe.

Full process arguments, DLL hashes, native counters, state hashes, verdicts and
independent cleanup receipts are in [summary.json](summary.json) and the linked
per-run folders.
