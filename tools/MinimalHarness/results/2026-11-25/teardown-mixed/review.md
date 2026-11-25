# Native teardown ordering review

## Verified failure mechanism

Native `CardPileCmd.DrawInternal` awaits `Add`; Add awaits a visual tween.
`TweenHelper.AwaitFinished` returns OwnerExited when the view leaves the tree.
Add then returns its result. Draw loops to another card while the combat still
reports live. `CreateCardNodeAndUpdateVisuals` dereferences NCombatRoom.Instance.

This is a mod teardown-order defect: native gameplay expects its room to stay
present while its combat is live. The prior replay order removed NRun, awaited
one frame, then canceled/cleaned
its owners. That allows a real pending card tween to resume a live old draw
against a missing room. PID 71367 reproduced the original user's same native
NullReference stack. PID 70177 caught only the earlier shuffle wait and did not
reproduce; that rejected trigger remains recorded, not counted as passing.

## Smallest production correction

Call the native public `CombatManager.Reset(graceful: true)` under the existing
OwnsCleanup notification guard BEFORE removing the old scene. Native Reset
invalidates/cancels the current CombatTurnState before detaching its creatures.
RunManager still retains its original run/players and the existing view is
present for power/creature cleanup. The native tween may then finish/exits, but
Draw now sees a dead combat and stops. RunManager's subsequent normal CleanUp
calls Reset again with no turn state; no second creature detach occurs.

No private native method/reflection string, new wire record, global test mode,
new gameplay readiness restriction or skipped model callback was introduced.
The entire production diff is seven added lines, including comments. Model
replay, mounted replay acceleration, history feeding and network barriers remain
on their existing paths. Unrelated AGENTS.md/census/artwork are preserved.

## Native call sites/lifecycle reviewed

Installed v0.113.1 d09d48e5 source in dist/native-audit/v0.113.1/source:

- CardPileCmd.DrawInternal, Add, CreateCardNodeAndUpdateVisuals.
- TweenHelper.AwaitFinished/AfterResume and NSceneContainer.SetCurrentScene.
- CombatManager.Reset/CancelCombat/DetachCreatures and RunTurnLoopAfter.
- RunManager.CleanUp, original map cleanup and old action executor ownership.
- NCombatRoom/NCombatUi/NPlayerHand/NCard/NCreature exit paths. They release
  selections/subscriptions/tweens; the old RunManager run remains present until
  after view exit, while the old combat has already been canceled.
- GamblingChip.AfterPlayerTurnStart -> CardSelectCmd -> DiscardAndDraw ->
  Shuffle/Draw, and native Fool Vitriol/Ecstasy generation/transformation.
- Native GameAction.CompletionTask/State and ActionQueueSet enqueue observers,
  plus Hook.InputGate and Network.Request/Vote/host vote receipt logging.

## Rejected fixture assumptions

A suspended Draw at hand=0 can still be waiting on shuffle and may terminate
normally. Require a genuinely pending first-card draw (hand 1..4) for that race.
TryManualPlay returning true does not prove enqueue: Hook.InputGate blocks new
requests while Network.Busy. The initial skew fixture incorrectly tried this
and timed out without the final vote. It is retained as fixture failure. The
corrected driver observes a real native action Executing BEFORE requesting
Undo, then proves CompletionTask is still pending when the host has 2/3 votes.

## Limits

The exact supplied 36-event combat is not reconstructed. The earlier card-view
stack is now reproduced through a legal native path, and the repaired lifecycle
is checked before/after. These are local native singleplayer/ENet regressions,
not authenticated Steam/P2P or all cards, modes, encounters and frame schedules.
No general safety claim follows solely from the number of successful runs.
