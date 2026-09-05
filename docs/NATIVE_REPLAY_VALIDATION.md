# Native save and replay validation

This experiment starts from commit `4522939` on `experiment/native-replay-undo`.
It uses the separate `UndoReplayLab` diagnostic mod. It does not replace the
installed `UndoSync` or add a player-facing undo button.

Status of the initial branch upload: prototype builds successfully; real game
execution is still pending. No runtime phase is claimed to pass at this stage.

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

The two earlier confirmed log causes remain recorded in
[ROOT_CAUSE_REVIEW.md](ROOT_CAUSE_REVIEW.md). This experiment does not relabel
those causes as fixed, or establish the precise cause of the reported potion
divergence.
