# Startup queued-input replay fix verification

The shipped v0.1.20 core deadlocks when a native turn-start selection is still
pending and a CombatPlayPhaseOnly input is recorded before the choice/resume.
The feeder waited for phase Play before enqueuing that input; native setup was
waiting for the later choice/resume records that the feeder could not reach.

The common feed gate now allows enqueue behind an actually gathering own hook.
The native queue still blocks execution there. Historical order and action IDs
are preserved; ordinary phase readiness and future-turn EndTurn guards remain.
Production changes are confined to `UndoSync/Replay/Replay.cs`. See review.md
for native authority, lifecycle review, rejected approaches and limits.

## Before / after

Game v0.113.1 / d09d48e5; native assembly SHA-256:
`a671e9efb86a7b80405133105665867b053c787670d475b4347cf5ccc15cb5fb`.

- Baseline PID 77233, shipped core
  `a764b6ae018c8a35f8229390cd966ff6b4223b29e6844796b22aa0252e4afc5a`.
  Native Gambling Chip, phase Start, queued Dexterity Potion, real viewport
  confirmation. Stalled at event 1 / UsePotionAction. Six-second bounded
  observation plus source wait-cycle proof, then explicit task-owned cleanup.
  Its harness terminal PASS means EXPECTED DEFECT REPRODUCED, not restore PASS.
- Fixed rendered focus PID 78528, core
  `d21f95ecafe6df140995ac8590772d3bc4d6f854ac38cf92ee3471b0526ec280`.
  Identical seeded target and original native enqueue/resume ID trace.
  Two successful exact restores (native restore+replay totals 728/552 ms),
  visible current hand bindings, potion effect, subsequent card and next turn.
  This small fixture timing is not a general restore-time promise.
- `comparison.json`, baseline/fixed PID logs, run identities and target/trace
  artifacts retain the evidence. Focus screenshot inspected manually.

## Validation

`dotnet build UndoSync -c Release`: PASS, no warnings/errors.
`dotnet build tools/MinimalHarness -c Release --no-restore -p:BuildProjectReferences=false`:
PASS, no warnings/errors. MinimalCheck: five modules, 38 Harmony patch targets,
16 reflection surfaces, manifest/message/initializer checks PASS. These are
static checks; actual native execution is recorded below.

21-case category batch: PASS, 427.716 seconds. Additional rendered three-peer
Fool Ecstasy/Vitriol regression: PASS, 94.386 seconds. All use native Instant
mode and the production replay acceleration, not a simulated game model.
Native C# exceptions, divergence, missing mechanism coverage, unequal target
states or changed action ID traces fail the independent artifact verifier.

The startup use/discard/Skill Potion profiles run in singleplayer and three
real ENet processes. Every multiplayer startup profile restores three times total, once per proposer,
including recorded startup and subsequent potion selections. Each peer checks
the exact historical enqueue/resume IDs/order and saved native target state.
Continued input, next turn, native transport identity, peer online/cursor state
and checksum suppression/reenabling are checked. Original transports remain;
no session replacement or connection recreation was observed.

The final mixed three-peer integration renders all six categories. Host and
client2 screenshots were manually inspected after the third restore: current
hand views, all peers visible, persistent Rolling Boulder visuals and no stale
choice overlay. The extra seven-turn three-peer Fool regression reaches turn 8,
restores with all three proposers, then plays cards and advances to turn 9.

| Case | Peers | PIDs | Verdict |
| --- | --- | --- | --- |
| choice | 1 | 79759 | PASS |
| card | 1 | 80177 | PASS |
| turn-end | 1 | 80358 | PASS |
| turn-start | 1 | 80551 | PASS |
| turn-transform | 1 | 80842 | PASS |
| choice-pending | 1 | 81142 | PASS |
| potion | 1 | 81315 | PASS |
| potion-pending | 1 | 81497 | PASS |
| combat-start | 1 | 81674 | PASS |
| startup-use | 1 | 81852 | PASS |
| startup-discard | 1 | 82166 | PASS |
| startup-select | 1 | 82356 | PASS |
| mixed | 1 | 82532 | PASS |
| potion | 3 | 82927, 82967, 82996 | PASS |
| enemy-replay | 3 | 83242, 83320, 83354 | PASS |
| enemy-choice | 3 | 83603, 83667, 83701 | PASS |
| turn-choice | 3 | 83871, 83946, 83975 | PASS |
| startup-use | 3 | 84146, 84212, 84247 | PASS |
| startup-discard | 3 | 84468, 84540, 84544 | PASS |
| startup-select | 3 | 84769, 84834, 84863 | PASS |
| mixed | 3 | 85089, 85166, 85208 | PASS |
| turn-transform | 3 | 86418, 86488, 86517 | PASS |

These two regression batches cover 22 cases,
40 native game processes,
49 distinct Undo sessions and
95 peer-local verified restores. The focused comparison
adds two successful Undo sessions; the baseline is counted separately as a
reproduced defect. Full PID-bound logs/JSON are in full-regression/ and
ecstasy-mp3/; summary.json and independent artifact rechecks retain totals.

## Restoration and remaining scope

All four test fixture roots' primary and independent ZIP backups were checked.
All original installed mod/test-profile file hashes were independently compared
with the live restored paths: exact matches. No running game, UDP 33771 free.
See independent-cleanup.json and backup-recheck.json. Steam account data was
not part of this temporary test deployment.

The exact user's 36-event plan was not recovered. This is a native reproduction
of the same logged wait cycle, not the exact saved combat. Authenticated
Steam/P2P and every card/character/mode/timing permutation remain untested.
The ending-phase allowance shares the queue-front safety proof but was not
separately forced during phase one/two. The earlier outgoing card-view
NullReference in the same incident PID is separate and remains unresolved;
this fix makes no claim to resolve it. No commit, version bump or release was
performed.
