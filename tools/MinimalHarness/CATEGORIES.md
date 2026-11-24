# Repeatable combat category regressions

Run from the repository root:

```sh
python3 tools/MinimalHarness/categories.py
```

The command checks for existing game processes/ENet port conflicts, builds the
harness, checks the production native surfaces, then executes named native game
cases with Instant speed. It stops on the first failure, saves PID/hash/coverage
proofs and restores the exact previous local mods/test-profile files in `finally`.
An existing output directory is rejected before temporary installation changes.

This is a unit-style **case interface around the real game**, not a mock-engine
unit test. Engine startup, native frames, actual choice UI and multiplayer peers
remain real. It does not run in wall-clock normal animation speed; it uses native
Instant mode plus the shipped replay acceleration. Durations are measured in
`summary.json`, rather than promised independent of startup/hardware.

## Default case list

| Case | Isolated category / scope | Mechanism and assertion |
| --- | --- | --- |
| choice, 1p, rendered | C3 selection, focused UI gate | Real Discovery screen click, recorded choice, two exact restores and visible current hand bindings |
| card, 1p | C3 immediate | Native Anger play and generated card state survive two restores |
| turn-end, 1p | C1 | Real Flame Barrier power, native enemy action and removal at side-turn end; observer fires live and during both replays |
| turn-start, 1p | C2 | Actual Rolling Boulder VFX signal damage, power amount increment and subsequent input; both replay executions observed |
| turn-transform, 1p | C2 historical regression | Existing seven-turn native Fool Ecstasy/Vitriol fixture, actual generated Joy plays, card-ID/model binding and repeated Undo |
| choice-pending, 1p | C3 pending discard | Open a real Discovery screen, Undo before choosing; repeat, restore exact earlier prefix and continue |
| potion, 1p | C5 | Energy Potion consumes and adds two energy, Skill Potion opens native card selection, Block Potion discarded through native action |
| potion-pending, 1p | C5 pending discard | Skill Potion choice remains open while the preceding native target is restored twice |
| combat-start, 1p | C6 | Anchor from the native relic bag applies exactly ten initial block; nonempty Anger prefix replays with repeated native initialization |
| startup-use / startup-discard / startup-select, 1p | C2 + C5 input during setup | Native Gambling Chip hand selection remains pending in phase Start when potion use/discard is queued; preserve Hook → Input → Choice → Resume and exact native enqueue/resume IDs across two restores. Skill Potion adds a second selection after the startup choice |
| mixed, 1p | C1–C6 ordinary-enemy composition | Anchor + Rolling Boulder + Flame Barrier + Discovery + immediate/choice/discard potion actions across two real enemy turns |
| potion, 3p | C5 actor/chooser separation | Host throws Skill Potion at client; native HookAction and ResumeAction appear in the recorded prefix; all three proposers restore |
| enemy-replay, 3p | C4 completed selection | All three players choose Mind Rot through native UI; the three recorded results replay in each of three rotating-proposer restores |
| enemy-choice, 3p | C4 pending mandatory selection | Existing Knowledge Demon fixture: actual boss move, client has chosen while the other screens remain open, Undo unwinds the discarded enemy turn |
| turn-choice, 3p | C2 mandatory selection | Existing Tools of the Trade fixture: all real turn-start hand selectors remain open during Undo |
| startup-use / startup-discard / startup-select, 3p | C2 + C5 input during setup | Each peer queues its native input behind its own pending startup hook; all three proposers restore the identical prefix with exact IDs, then continue playing |
| mixed, 3p, rendered | C1–C6 composition integration | All six categories, native other-player potion branch, three rotating proposers, hand/online/cursor bindings, continued card and next turn |

The mixed case's C4 representative is ordinary enemy behavior. Completed and
pending mandatory boss selections have separate three-peer fixtures; it is not claimed covered in
the same mixed combat. Categories remain six; pending/branching are variants.

Each new profile uses a five-card deck constructed with native deck removals
and merchant card factories, plus starting Defends. Colorless unlocks are enabled
through the native epoch API on backed-up temporary profiles. Anchor is pulled
from the real filtered Common relic bag. Potions are sampled from the actual
unlocked native potion factory until the named item is returned (bounded at 512
rolls per potion), then procured normally; RNG and rejected draws are not reset.
No HP, power, card identity, RNG state, hand order or replay result is injected.
The seeded native Act's first Monster encounter is used; no catalogue-only monster
combination is fabricated. Boss/turn-choice cases retain their established native
acquisition/encounter routes.

## Shared pass conditions

- Native target mechanism executes, not just its setup or a quiet run.
- A nonempty genuine prefix is replayed and matches the producer's full native
  target state/hash and history length after every restoration.
- Historical choices use the production recorded-result path. Completed choices
  are clicked through native viewport UI; pending fixtures leave real screens open.
- Side-effect counters are read-only Harmony observers on the harness side;
  they do not skip, replace, delay or mutate native production execution.
- Native hand models remain bound to their current holder/view; no old selector
  remains attached. Rendered profiles save screenshots for parent inspection.
- Native C# exception lines, mismatches, missing path markers or unknown coverage
  fail the independent verifier. Process exit zero alone is insufficient.
- Real post-restore card input and next-turn progression work. Three-peer profiles
  retain the native transport and compare actual result checksums, final states,
  online status and remote cursors.
- Repeated Undo is twice in singleplayer, three times with all three proposers in
  the new three-peer category profiles. Existing pending boss/turn fixtures each
  perform their own one-Undo teardown check.

## Targeted and extended commands

```sh
python3 tools/MinimalHarness/categories.py --list
python3 tools/MinimalHarness/categories.py --case turn-start
python3 tools/MinimalHarness/categories.py --case potion --peers 3
python3 tools/MinimalHarness/categories.py --case mixed --peers 3 --visible
python3 tools/MinimalHarness/categories.py --all-peers
```

`--all-peers` adds three-peer runs of every other isolated profile. `--no-build`
is only for a deliberately pinned, already built core/harness pair. The default
three-peer pending enemy/turn fixtures reuse FaultScenarios, not synthetic peers.
The default is 21 cases. `startup-use` also renders its singleplayer focus.
Use `--case startup-use --peers 3 --visible` for rendered multiplayer inspection.
Never run multiplayer jobs concurrently: the native test port and profiles are
exclusive. The ordinary runner checks this before each launch.

## Updating the game

Record the new game version/DLL hash, run this command, and compare the per-case
results. A compile, patch-target, acquisition-pool or mechanism assertion failure
must be investigated against the new native source; do not relax it into a pass.
Use `tools/ReplayAudit` when an update adds a genuinely new entry/selection path.
These are fixed representative category regressions, not all cards, every boss,
every character/mode or every timing combination. A green result does not prove
new content cannot introduce a new path. Actual Steam/P2P remains separate.

The startup fixture observes public queue callbacks only. It does not supply
choice results, action IDs, hook contexts or artificial readiness. Native input
is enqueued through the normal synchronizer; real viewport clicks complete both
selectors. `--minimal-expect=baseline` is only a bounded defect reproduction:
its terminal success means the old stall was observed and deliberately cleaned
up, not that restoration passed. Do not pass it to the ordinary success verifier.

No production change, commit or release is part of adding/running this suite.

## First recorded execution

On v0.113.1 / d09d48e5 with shipped v0.1.20 core: all 15 default case variants
passed. There were 16 selected executions (one additional integration repeat),
28 native processes and 34 distinct Undo sessions, with zero native C# exceptions
or state divergences. Measured duration sum for the 15 unique cases was about
5m37s across a main batch and supplements; no claim of a separately measured
single expanded-default invocation. See the
[full report](results/2026-11-24/category-regression/report.md).
