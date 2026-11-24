# Review of the common replay feeder change

## Source-derived safety

- With no own pending hook choice, the new play-phase predicate is equivalent
  to the previous predicate: side Player, phase Play, synchronizer PlayPhase,
  and recorded EndTurn turnNumber reached. No normal readiness gate is removed.
- `ActionQueueSet.PauseActionForPlayerChoice` requires the gathering action at
  its owner queue front. `GetReadyAction` skips a gathering front, so an input
  queued behind that exact owner cannot execute ahead of the choice.
- `GenericHookGameAction` must actually reach `GatheringPlayerChoice`; a mere
  prefed placeholder or unfinished arbitrary turn callback does not qualify.
- Enqueue, PlayerChoice and Resume records remain in their historical order.
  Native Enqueue allocates an ID even when later canceled; native Resume
  allocates another ID. Neither is deferred, reordered or fabricated.
- The future-turn EndTurn guard remains independent of the choice allowance.
  Ecstasy/Vitriol regressions check that subsequent turns are not skipped.
- No potion-specific production branch, new event kind, reflection member,
  wire-format change, hook patch or sixth module was introduced.

Native authority: installed v0.113.1 `ActionQueueSet.cs`,
`HookPlayerChoiceContext.cs`, `GenericHookGameAction.cs`, `CombatManager.cs`,
`GamblingChip.cs`, `PotionModel.cs` and native hand/potion UI call sites in
`dist/native-audit/v0.113.1/source/`.

## Limits

The private user log has the matching event-1/UsePotionAction/phase-Start stall
and Gambling Chip gameplay. The user's exact 36-event plan was not recovered.
The native fixture reconstructs a valid ordering with that same wait cycle;
it does not claim to replay the user's exact saved combat. Steam/P2P has not
been tested. The ending-phase allowance has the same native queue-front safety
proof, but the dedicated new regression targets phase Start rather than a
separately constructed phase-one/two hook choice. An earlier card-view NullReference in the same user PID is a
separate incident, not established as fixed by this queue change.
