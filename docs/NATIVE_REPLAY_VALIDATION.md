# Native save and replay validation

This experiment starts from commit `4522939` on `experiment/native-replay-undo`.
It uses the separate `UndoReplayLab` diagnostic mod. It does not replace the
installed `UndoSync` or add a player-facing undo button.

Status: the bounded single-player engine experiment passed on 2026-09-06 KST.
Live multiplayer and ordinary player input after replay are not implemented.

## Question being tested

Can the engine reconstruct the same combat from an immutable native starting
save and an ordered record of inputs, without copying individual combat fields
back onto existing objects?

The checkpoint must precede combat initialization. Starting a combat consumes
randomness; saving after that initialization and then initializing again is not
the same starting condition. Loading also must use the saved-run initialization
path: `SetUpTest` initializes a new run and can consume additional randomness or
apply new-run hooks.

The replay input includes choices, hook actions and resumptions as well as card
and potion actions. A queue cannot be required to become empty before supplying
the choice that its current action is waiting for.

Every reconstruction must deserialize fresh save objects. Retaining the same
mutable `SerializableRun` object would reintroduce checkpoint aliasing.

## Evidence requirements

- Actual game engine execution, identified by process ID, binary hashes and logs.
- Original inputs recorded and reconstructed inputs actually executed; zero
  executions do not pass.
- Exact generated potion choices, relevant RNG state, permanent deck growth,
  and subsequent action behavior checked separately.
- Explicit failures and unexecuted cases. A network checksum match is only a
  check of that payload's contents, not all future gameplay.
- Repeated reconstruction from the same starting bytes to test that later play
  cannot mutate the retained checkpoint.

The global `CardSelectCmd` test selector bypasses the choice synchronization
path. Tests using that shortcut cannot establish that choices and resumptions
are replayable. A UI substitute used for automated input must preserve those
engine calls and be identified in the result.

## Baseline checks

On 2026-09-05, the unchanged `UndoSync` from this branch's base built against the
installed Windows game with 0 errors and 4 existing nullable warnings. The
installed game assembly is v0.111.0, commit `41cef1ea`, SHA-256
`0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`.

`SurfaceCheck` passed its five static checks: 115 reflection/patch references,
28 state type surfaces, coverage for 12 tracked types, 203 copied fields, and 41
checksum payload fields. Its committed project targets .NET 10, while this
machine has SDK 9.0.317. The check was run from a temporary copy with only the
target framework changed to .NET 9; its `Program.cs` and the branch's ledgers
were unchanged. These are baseline compatibility results, not replay results
or proof that the existing snapshot is complete.

## Runtime attempts

The first isolated run, PID `6060`, loaded the lab and confirmed its custom
user-data directory. It executed Attack Potion, Skill Potion, Genetic
Algorithm and Defend, recording 8 replay events: 4 actions, 2 choices and 2
resumptions. The permanent Genetic Algorithm deck card gained 3 block.

Reconstruction failed in `RoomSet.FromSave` because the test bootstrap had not
generated the act room sets. This is a harness setup failure; it is not evidence
that the engine's normal save/load cannot reconstruct a valid game. No replay
events executed on that attempt. Its evidence is retained locally under
`%LOCALAPPDATA%/Temp/Sts2UndoReplayLab/bcd9f2080f2047a0b83171fb86b2ba3e/`.

Review also established that the normal `WriteReplay` export anonymizes player
IDs. Mixing that exported event stream with the original save's IDs is invalid.
The identity-preserving experiment instead freezes the engine's raw replay
packet. The lab's narrow reflection access to `CombatReplayWriter._replay` and
the `RunManager.ShouldSave` setter was verified against the installed assembly.
Neither access copies combat fields back onto existing models.

A second run, PID `14372`, reconstructed the native save but exposed a separate
test integration requirement: replay networking has no multiplayer lobby, so
the lab must disable the room-entry network synchronization barrier, as
`SetUpTest` already does. This does not disable the lab's local state comparison
and is not a multiplayer synchronization test.

PID `1800` then completed both replays with matching results. Review found that
the hardcoded test map coordinate was discarded when loading the saved map,
and that the launcher still compared totals as if only one replay had run.
The final run below uses a valid native map coordinate and checks each replay
separately as well as their combined counts.

### Final executed result

PID `17348`, 2026-09-06 00:05 KST, exited with code 0. The launcher accepted
its process identity, isolated user-data path and report contract. The lab
assembly SHA-256 is
`DF43EC1D563ABA82C9138546CB257871DAEDE8AAF53350B6B5A2D62D02FE838A`.
The controlled setup uses Defect, a five-card deck, Attack Potion, Skill Potion,
the fixed seed `undo-replay-lab-native-seed` and `CUBEX_CONSTRUCT_NORMAL`.

| Check | Original | Each of two reconstructions |
| --- | --- | --- |
| Recorded / replayed events | 8 | 8 |
| Finished card and potion actions | 4 | 4 |
| Card selection events | 2 | 2 |
| Action resumptions | 2 | 2 |
| Canceled actions | 0 | 0 |
| Genetic Algorithm permanent growth | +3 block | +3 block |
| Map coordinate | (3, 0) | (3, 0) |
| Potion candidate lists and selected cards | Original sequence | Identical |
| Native run RNG after both potions | Original digest | Identical |
| Native combat packet after all four actions | Original digest | Identical |
| Frozen checkpoint bytes | Original SHA-256 | Unchanged |

The actual selections were `ROCKET_PUNCH` from
`ROCKET_PUNCH,SWEEPING_BEAM,FTL`, and `CHARGE_BATTERY` from
`CHARGE_BATTERY,BOOST_AWAY,MULTI_CAST`, on the original run and both replays.
The local automated selector supplies the same engine choice-sync call as the
UI; replay consumes recorded choices through the engine's remote-choice path.
There are no extra, unrecorded random-generation probes in this final sequence.

The [captured report](validation/native-replay-20260906.json) retains the actual
phase results, counters and hashes; only machine-local directory paths were
removed. Full local evidence is retained under
`%LOCALAPPDATA%/Temp/Sts2UndoReplayLab/b75faa878023405fbccdcb1e802b2a5a/`.
The game still emits headless startup/exit resource warnings; the pass above
concerns the explicitly measured behavior, not a warning-free application exit.

This establishes feasibility for the tested reconstruction path. It does not
establish complete undo coverage across cards, encounters or multiplayer.

## Boundaries of the experiment

Headless `TestMode` does not create the normal combat UI. Orb animations and
input controls need a separate UI run. Single-player reconstruction does not
validate a live multiplayer handoff or remote action ordering.

`NetReplayGameService` does not send network messages, and the standard
`RequestEnqueue` switch does not accept fresh actions in replay mode. Executing
another action directly through its queue demonstrates queue execution only;
it does not prove that ordinary player input can resume. `RunManager.CleanUp`
also disconnects the current network service. A working live handoff therefore
needs separate design and evidence.

## Multiplayer ordering and choices

The inspected engine routes client action requests through the host's
`ActionQueueSynchronizer`; accepted actions are placed in the ordered action
queues. `CombatReplayWriter` records queue insertions, choice results and
resumptions. Choices carry player and choice IDs; a generated-card selection
contains the chosen index. The candidate list must therefore be regenerated
identically. A valid chosen index alone cannot prove the same card was chosen.

The current lab records one local player's actual engine events. It has not
compared host and client logs. Remaining multiplayer cases include simultaneous
actions, a peer acting first, actions interleaved while another player chooses,
death or disconnection during that interval, and replay-to-live continuation.
The two executed selections establish the card-choice path in this setup, not
all selection types. The hook-action dispatch branch exists but no hook action
was executed in this controlled sequence.

The two earlier confirmed log causes remain recorded in
[ROOT_CAUSE_REVIEW.md](ROOT_CAUSE_REVIEW.md). This experiment does not relabel
those causes as fixed, or establish the precise cause of the reported potion
divergence.
