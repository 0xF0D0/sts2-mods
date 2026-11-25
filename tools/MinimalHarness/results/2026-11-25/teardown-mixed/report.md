# Outgoing card-view lifecycle fix and mixed validation

## Outcome

The earlier user-log card-view NullReference is now reproduced, attributed to
mod teardown ordering, fixed locally and verified against the same real native
first-card draw path. All 30 selected fixed cases passed:
9 new overlap/composition cases plus the full existing 21-case category suite.
There were 56 successful native game processes,
71 distinct Undo sessions and 141
peer-local target-checked restores. Failed/rejected fixture attempts are retained
and excluded. No commit, version bump, push or release was performed during validation.
Subsequent authorized v0.1.22 publication is tracked separately in
`dist/UndoSync-v0.1.22-release-receipt.json`, retaining the exact tested DLL.

Game v0.113.1 / d09d48e5, native assembly SHA-256:
`a671e9efb86a7b80405133105665867b053c787670d475b4347cf5ccc15cb5fb`.
Fixed core SHA-256 for EVERY successful case:
`361ffc2b6ee80c4ba0ae3af53338915a8868283055e70b5c10c9a3450860648d`.

## Before / after and cause

- Shipped v0.1.21 core `d21f95ecafe6df140995ac8590772d3bc4d6f854ac38cf92ee3471b0526ec280`,
  baseline PID 71367. A native turn-start Draw was suspended with one card
  already in hand. The old scene exited while the combat was still live;
  CreateCardNodeAndUpdateVisuals then accessed a missing NCombatRoom and the
  old native turn loop faulted with the same NullReference stack as the user.
  The new replay itself still completed, consistent with the supplied incident.
  Terminal baseline success means EXPECTED DEFECT REPRODUCED, not restore clean.
- Fixed rendered PID 73442. Same first-card draw trigger, same seed/native target
  checksum 1821899888, and the first five full native snapshots are byte-identical
  to the baseline. Three consecutive restores had oldCombatLive=False BEFORE
  view removal, zero old detached-view accesses, old turn tasks terminal without
  unexpected exception, exact restored target, correct native hand bindings,
  continued card input and next turn. Native restore/replay totals: [651, 645, 587] ms.
- Native Tween.AwaitFinished returns on owner exit. Add returns its result;
  DrawInternal can then continue to the next card while the old combat is live.
  Our old order removed the scene and awaited a frame before cancel/reset.
  This was a mod lifecycle error, not evidence that ordinary gameplay produced
  an invalid game state or that the native game itself was defective.
- The production correction is seven added lines in Replay.cs: public native
  CombatManager.Reset(true), under the existing OwnsCleanup notification guard,
  BEFORE scene removal. Native reset invalidates the old turn before creature
  detach; the old RunManager run/views are still present for cleanup. Native
  scene exit then cannot resume a live old Draw. See review.md/production.patch.

## New combinations actually executed

1. Pending first-card turn-start draw interrupted by Undo: singleplayer focus
   and three-peer rendered integration, three restores each.
2. Actual pending enemy TakeTurn interrupted by Undo: singleplayer and three
   peers, three restores each. Actual pending tasks/current side are observed.
3. Gambling Chip discards all five cards, genuinely shuffles and redraws them,
   with potion input enqueued before confirmation: singleplayer and three peers.
   Native live/replayed DiscardAndDraw/Shuffle counters, enqueue/resume IDs/order,
   exact full target state, current native hand views and continuation checked.
4. Gambling Chip nonempty redraw/shuffle + queued potion + seven native Fool
   Ecstasy/Vitriol turns: singleplayer and three peers. Actual generated Joy
   plays and native transform bindings verified; restore turn 8 and continue to
   turn 9. This is all in the SAME combat and replay prefix.
5. Fool + Ironclad + Silent three-peer composition. All open Skill Potion
   choices; host and player 3 finish, player 2 remains in native selection.
   Player 3 starts a native Strike before the proposal. The host's 2/3 vote
   receipt is read back, and the final voter verifies the native action's real
   CompletionTask is still pending. All three proposers restore native targets
   13 -> 14 -> 13, proving both later and earlier target restoration. Full states,
   potion inventory, absence of stale selection UI, hand model/view binding,
   transport/online/cursors, post-restore card and next turn are checked.

Only read-only native observers and ordinary game/production Request/Vote APIs
schedule these cases. No replaced tasks, artificial native pauses, test mode,
fake peer, injected choice/HP/RNG/card identity or production-private-state
mutation. Hand/potion choices use real viewport events and native callbacks.
Normal speed is used where genuine ongoing work must still be pending; replay
retains its production acceleration. Remaining profiles use native Instant.

## Integration and static verification

The existing full 21-case categories.py batch passed after the focused tests,
including completed/pending boss selections, pending Tools of the Trade,
card/potion choices, all three startup input variants, six-category mixture and
rendered three-peer final integration. Raw results are in full-regression/.
New profiles are recorded across focused-draw/, redraw-focus/, mixed-01/ (six
successful cases) and skew-02/; they were not one single successful default
nine-case batch invocation. The independent verifier rechecked all 30 artifacts.

Release core build and final harness build passed with zero warnings/errors.
MinimalCheck passed: exactly five modules, 38 Harmony targets, 16 reflection
surfaces. Python syntax and git diff whitespace checks passed. Static checks
are separate from the actual native runs below.

Parent inspected rendered first-card before/after/fixed hand evidence, full
Gambling redraw focus, heterogeneous host/chooser after the third restore, and
final mixed integration screenshot. Persistent effects/current views remained;
no stale native selection overlay remained. Pixel-perfect appearance was not
an acceptance criterion. Successful logs had zero native C# exceptions or
state divergences; native engine shutdown RID warnings remain in raw logs and
are not represented as clean renderer shutdowns.

## Executions

| Case | Peers | PIDs | Distinct Undo sessions | Verdict |
| --- | --- | --- | --- | --- |
| focused-draw | 1 | 73442 | 3 | PASS |
| redraw-focus/redraw-sp | 1 | 77008 | 2 | PASS |
| skew-02/skew-mp3 | 3 | 85523, 85630, 85632 | 3 | PASS |
| mixed-01/enemy-sp | 1 | 78194 | 3 | PASS |
| mixed-01/enemy-mp3 | 3 | 78568, 78631, 78633 | 3 | PASS |
| mixed-01/lifecycle-mp3 | 3 | 79117, 79186, 79215 | 3 | PASS |
| mixed-01/redraw-mp3 | 3 | 80279, 80346, 80376 | 3 | PASS |
| mixed-01/transform-sp | 1 | 80600 | 2 | PASS |
| mixed-01/transform-mp3 | 3 | 80886, 80948, 80950 | 3 | PASS |
| full-regression/00-choice-1p | 1 | 86779 | 2 | PASS |
| full-regression/01-card-1p | 1 | 87096 | 2 | PASS |
| full-regression/02-turn-end-1p | 1 | 87269 | 2 | PASS |
| full-regression/03-turn-start-1p | 1 | 87446 | 2 | PASS |
| full-regression/04-turn-transform-1p | 1 | 87739 | 2 | PASS |
| full-regression/05-choice-pending-1p | 1 | 88021 | 2 | PASS |
| full-regression/06-potion-1p | 1 | 88195 | 2 | PASS |
| full-regression/07-potion-pending-1p | 1 | 88369 | 2 | PASS |
| full-regression/08-combat-start-1p | 1 | 88545 | 2 | PASS |
| full-regression/09-startup-use-1p | 1 | 88763 | 2 | PASS |
| full-regression/10-startup-discard-1p | 1 | 89412 | 2 | PASS |
| full-regression/11-startup-select-1p | 1 | 89606 | 2 | PASS |
| full-regression/12-mixed-1p | 1 | 89779 | 2 | PASS |
| full-regression/13-potion-3p | 3 | 90174, 90236, 90238 | 3 | PASS |
| full-regression/14-enemy-replay-3p | 3 | 90472, 90534, 90563 | 3 | PASS |
| full-regression/15-enemy-choice-3p | 3 | 90790, 90857, 90859 | 1 | PASS |
| full-regression/16-turn-choice-3p | 3 | 91065, 91126, 91130 | 1 | PASS |
| full-regression/17-startup-use-3p | 3 | 91325, 91388, 91418 | 3 | PASS |
| full-regression/18-startup-discard-3p | 3 | 91612, 91681, 91710 | 3 | PASS |
| full-regression/19-startup-select-3p | 3 | 91904, 91967, 91996 | 3 | PASS |
| full-regression/20-mixed-3p | 3 | 92221, 92284, 92313 | 3 | PASS |

## Rejected and failed attempts

- PID 70177: a genuinely pending Draw was still in its shuffle await (hand=0).
  It terminated normally after teardown, with no late view access. This did
  NOT reproduce the defect and is not a passing fixed-case result.
- PIDs 81632/81700/81729: the first skew fixture incorrectly attempted new input
  while Network.Busy. Existing Hook.InputGate rejects it; TryManualPlay's true
  return did not prove enqueue. The fixture waited for an action that never
  entered the native queue and withheld the final vote; no restore occurred.
  This is fixture scheduling failure, not evidence of missing vote packets.
  The corrected run observes a genuine queued/Executing native action BEFORE
  proposal and proves it is unfinished at the final vote. The original failed
  run remains in mixed-01/skew-mp3 and is excluded from totals.

## Cleanup, scope and repeat command

All seven fixture roots had independently checked primary/ZIP backups. After
all games exited, live installed mod and temporary test-profile file hashes were
compared against EVERY original manifest: exact matches. No game process or
UDP 33771 listener remains. See independent-cleanup.json and backup-recheck.json.
Actual Steam account data was not part of the temporary deployment.

Repeat the existing category suite with categories.py, and the nine overlap
profiles with mixed.py (see tools/MinimalHarness/MIXED.md). Both use native
factories/valid encounter pools and reject missing coverage. These bounded
singleplayer/local ENet tests do not establish authenticated Steam/P2P, every
card/character/mode/encounter, all possible frame orderings, or direct replay of
the original user's full 36-event combat. The previously unresolved card-view
exception is resolved by this fix. This report records validation before the
subsequently authorized v0.1.22 publication.
