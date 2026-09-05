# UndoSync root-cause record — 2026-09-05

Scope: analysis only. User requested retaining the two confirmed causes and reviewing whether the snapshot architecture can reproduce future gameplay. No implementation changes are authorized by this record.

## Evidence baseline

- Installed mod: `UndoSync` 0.1.5, DLL last written 2026-09-04 22:51:16, 294912 bytes.
- Game: v0.111.0, commit `41cef1ea`.
- This workspace's original source predates the installed binary. Conclusions below were checked against decompiled installed DLLs, not inferred from the old source.
- Logs: `%APPDATA%/SlayTheSpire2/logs/UndoSync-14640.log`, `UndoSync-25000.log`, `godot2026-09-05T01.07.10.log`, and `godot.log` (session ending 2026-09-05 02:30).

## Confirmed cause 1: dead player blocks all snapshot capture

Installed `ChecksumHook.TryStoreSyncPoint` requires every member of `combatState.Players` to have `PlayerCombatState.Phase == Play`. It does not distinguish dead players.

Game `CombatManager.StartTurn` sets player phases to Start but runs `RunAutoPrePlayPhase` only for living players taking the turn. A dead player's Start phase is therefore compatible with living players continuing to play.

- 2026-09-04 23:28:18–23:28:59: 23 consecutive snapshot rejections after peer death (`UndoSync-14640.log:2148`).
- 2026-09-05 02:18:11–02:25:05: no new snapshots, 144 phase rejections and 5 effect-in-progress rejections (`UndoSync-25000.log:3930`). Peer death occurs just before checksum 2273 (`godot.log:63207`). Capture resumes in the next combat.

This establishes persistent rejection by the capture gate, not corruption of the snapshot collection.

## Confirmed cause 2: separately retained combat-start snapshot is excluded from picker eligibility

Combat start is retained separately as `_combatStartPoint`. Ordinary history has a 50-entry limit. `RestoreTo` removes ordinary entries after the target but does not insert a separately retained target back into ordinary history. `SyncPointsNewestFirst` returns only ordinary history. `UndoPicker.Open` returns for fewer than two entries before consulting `TryGetCombatStart`.

- 23:17:53: restart to separately retained id 219 leaves ordinary history at zero.
- 23:17:58 and 23:18:04: new snapshots 345 and 346 are stored, proving capture resumed.
- 23:18:09: undo to 345 leaves one ordinary entry.
- 23:18:10–23:18:28: picker reports `nothing to undo (sync points=1)` 14 times, although combat start remains separately retained (`UndoSync-14640.log:1535`, `:1604`).

This is disagreement between retained restore targets and UI eligibility; it is not evidence that capture permanently stopped.

## Additional observation, not a third confirmed persistent-failure cause

Capture also rejects an action when any player is still executing a card/potion effect. At 23:19:06 the peer's potion action was received and completed while the host was still choosing cards; its checkpoint was skipped. Capture resumed at 23:19:08 after the host's selection. Skipped checkpoints are not retried individually.

Peer-first action is not itself disallowed: at 23:26:42 a peer's first card was stored while the host had not played a card (`UndoSync-14640.log:2030`). All 42 combat setups in these two mod logs have a combat-start capture. A failure exclusively on another PC cannot be established from host-only logs.

## Architecture review: installed 0.1.5

Verdict: snapshot-based undo is viable in principle, but this implementation does not establish a complete, repeatable rewind. The concrete problems below concern the definition of captured state, isolation of saved objects, and the meaning of verification. They are broader than adding a few missing card fields. This is an architecture assessment, not a proposed implementation or a claim that every undo fails.

The two confirmed log causes above remain distinct. Neither, by itself, proves that snapshot-based undo is the wrong approach. The additional findings below are from static analysis of the installed binaries; they are not newly reproduced gameplay incidents.

### Evidence identity

- Installed UndoSync.dll SHA256: `CFAD9DDDCB8B5A87B568C7D8B50D1819A38F3CE609A662E8F29776F28A5F7055`.
- Installed sts2.dll SHA256: `0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9`.
- Decompiled evidence directory: `C:/Users/goodb/AppData/Local/Temp/sts2-root-cause-20260905/`. Mod types are in `mod/`; game types in `game/`. Filenames are full type names followed by `.decompiled.cs`.
- Workspace README and coverage manifest were used only to examine the earlier design assumptions, not to substitute for the installed binary's behavior. Changes being developed on another machine were not assessed.

### 1. Capture scope excludes real consequences of combat

Installed `StateSnapshot.CapturePlayer` captures cards from `player.PlayerCombatState.AllPiles` (lines 562–567). `RestoreCardFields` restores those combat cards (996–1008). There is no corresponding capture/restore of the permanent deck's card state. `_deckVersion` is excluded by `CopySkip` (262); preserving this reference does not restore the referenced object's fields.

Concrete counterexample in installed game: `MegaCrit.Sts2.Core.Models.Cards.GeneticAlgorithm.OnPlay` increases both the combat card and its permanent `DeckVersion` (68–69). `BuffFromPlay` changes `IncreasedBlock` and `CurrentBlock`, both saved properties. Undo can therefore rewind the combat copy while leaving the permanent copy's growth in place. This is a static counterexample to complete state coverage, not a claim that this card appeared in the user's reported potion incident.

The old workspace `tools/SurfaceCheck/snapshot-coverage.json:87` deliberately excludes `Player.Deck` as run-level state and a write-once reference. That justification conflates an unchanged reference with unchanged contents, and assumes that combat has no relevant effects outside combat-owned objects.

### 2. A restored snapshot can share mutable saved objects with live play

Capture creates independent DynamicVarSet objects for powers, potions, and relics. Restore then assigns those saved objects directly into live models:

- `StateSnapshot:730`: saved power `DynamicVarsClone` assigned directly to the live power.
- `StateSnapshot:1073`: saved potion DynamicVarSet assigned directly to the live potion.
- `StateSnapshot:1133`: saved relic `DynamicVarsClone` assigned directly to the live relic.

The first restore thus makes the snapshot and live model share the same mutable object. A later mutation of a contained DynamicVar changes the saved object as well. Restoring that same checkpoint again can reuse its modified contents. The earlier `Pristine`/ownership-rebinding calls do not remove this alias, because the explicit DynamicVarSet assignment happens afterward.

`DynamicVarSet` exposes mutable DynamicVar values and mutating operations (`ClearPreview`, `FinalizeUpgrade`, etc.). This is a confirmed aliasing path in code. Whether a particular potion/relic/power mutates a gameplay-relevant value along a particular run still depends on its behavior. It does not establish the cause of changed card-generation choices.

The generic `Shadow` path is also not a recursive object-graph snapshot: array and collection copies are shallow, readonly fields are skipped, and failed copy-constructor attempts explicitly retain shared references (`StateSnapshot:305–352`). No such copy failure was found in the two reviewed mod logs; absence of a failure log does not establish isolation of nested contents.

### 3. The game's clone API is not an exact combat checkpoint API

`AbstractModel.MutableClone` uses MemberwiseClone, followed by the type's `DeepCloneFields` and `AfterCloned` (171–195). What gets cloned or reset depends on the model.

For example, `CardModel.AfterCloned` resets target/play index/deck association/removal state and clears events (1219 onward). `PowerModel.DeepCloneFields` initializes fresh internal data, and `AfterCloned` clears ownership/events (582 onward). UndoSync uses a separate Shadow mechanism for power internal data, so the latter is evidence about the clone API's semantics, not an accusation that UndoSync blindly resets every power through this method.

Generic cloning therefore helps copy some data but does not, by itself, certify complete capture, reference isolation, or valid future execution. Earlier statements that it structurally prevents all omissions were too strong.

### 4. FIDELITY PASS has a narrower meaning than complete restoration

`ChecksumHook.VerifyRestoreFidelity` compares serialized `NetFullCombatState.FromRun(runState, null)` with the captured bytes (368 onward). All 17 restore checks in the two reviewed mod logs passed this byte comparison.

The game payload includes all run RNG streams, but is not a complete snapshot of the world:

- PowerState contains only model id and amount (`NetFullCombatState:79–96`), not arbitrary private power data or DynamicVars.
- PotionState contains only model id (339–352).
- PlayerState contains combat piles, not permanent deck contents (137 onward; population at 441 onward).
- Execution continuations, UI bindings, and the full combat history are not represented by this payload.

Consequently, both peers can agree, and the local restore can report PASS, while omitted state still changes subsequent play. The GeneticAlgorithm permanent-deck example falls outside this check. A shared mutable snapshot can also be corrupted in fields the payload never records.

Verification occurs before `UiRefresh.RefreshAll` (`ChecksumHook:563–573`), so it also does not certify the state after all UI refresh work.

There is a separate failure-semantics weakness: `StateSnapshot.Try` catches section exceptions and continues (683–695); `RestoreTo` records fidelity in a flag, but `UndoProtocol.CommitAsync` calls `SendRestoreAck` after `RestoreTo` without checking that flag (991–994). Thus an acknowledgment is not a proof of complete successful restoration. The reviewed sessions did not log a restore-section error or fidelity failure; this is a structural observation, not the diagnosed cause of those sessions' symptoms.

### 5. Potion RNG: saved correctly in the inspected path; reported divergence not isolated

Both installed AttackPotion and SkillPotion use `player.RunState.Rng.CombatCardGeneration` to choose the offered cards (their `OnUse`, 26–28). This RNG stream is shared by players in the same run; it is not a separate stream per player or per potion.

Installed `StateSnapshot` captures the run RNG dictionary with `Rng.ToSerializable` (399–408) and restores each existing RNG with `LoadFromSerializable` (1181 onward). SerializableRng contains the counter and all four 64-bit generator state words. `Rng.LoadFromSerializable` and `MegaRandom.Reinitialise` restore those values without reseeding from time. The byte-identical fidelity payload includes these run RNG values.

This is substantial evidence against asserting that the current mod simply forgot to save the potion RNG. It proves equality at the comparison instant for the reviewed restores, not equality of all later calls before the next potion roll.

CardFactory.GetDistinctForCombat also depends on the eligible card sequence and its order. In normal gameplay, the same RNG state, same candidate sequence, and same intervening consumption produce the same draw. Repeating one's own visible actions alone does not establish those conditions in multiplayer.

Observed ordering example in `godot2026-09-05T01.07.10.log` (game log clock UTC; local time is +9 hours):

| Local time | Observed use order | Evidence |
| --- | --- | --- |
| Sep 4 23:13:43–45 | Peer SkillPotion, then host AttackPotion | 11081, 11104 |
| Sep 4 after restart to id 219 at 23:17:53 | Host AttackPotion at 23:17:59, without repeating the earlier peer potion first | 13685; host chooses none at 13710 |
| Sep 4 after run reload, 23:18:50–53 | Host AttackPotion, then peer SkillPotion | 13867, 13889 |

Chosen cards differ across the first and third sequences, but these are different global orders, and the third sequence is a run reload rather than an identical undo replay. These lines log selected cards, not a paired record of all three offered options and the generation RNG state. They cannot confirm or refute the user's separate report of changed options after genuinely identical actions.

The specific potion incident remains unisolated. A changed seed, post-restore RNG consumption, a changed candidate sequence, or a different checkpoint must not be promoted to its confirmed root cause from these logs alone.

### Overall assessment and limits

A useful snapshot must preserve everything that determines future outcomes in the supported scope, remain unchanged while play continues, and restore a coherent state from which execution can resume. The inspected code demonstrably falls short on scope and saved-object isolation, and its success check covers a smaller set of state than the user expects undo to restore.

The model capture, RNG serialization, and multiplayer quiescence protocol are useful existing components. Their presence does not resolve the coverage and isolation findings. Replacing individual copies with generic deep clones would not automatically resolve external state, cloning semantics, execution state, or event/UI relationships either.

Only local binary decompilation, existing logs, and design-document inspection were performed. No game/mod implementation, configuration, save, or deployment was changed, and the new static findings were not exercised in a live run. No fix plan is included, consistent with the user's analysis-only scope.

## Follow-up: alternatives to manual mid-combat snapshots

The user subsequently asked whether a fundamentally less omission-prone approach exists before making fixes. This section evaluates that alternative; no implementation or live replay was performed. The installed mod hash remains unchanged.

### Preferred direction for investigation: native starting save plus deterministic event replay

Retain an immutable serialized starting save at a boundary the game's own load path can reconstruct. To undo, create a fresh run/combat state through that path and execute the recorded event prefix up to the chosen safe decision boundary. The reconstructed world becomes the continuation state; copying it back into the old world field by field would reintroduce the original coverage problem.

The starting point is still a checkpoint. The architectural difference is using the game's save/load contract at its supported boundary and regenerating transient combat state through ordinary game logic, instead of maintaining a bespoke inverse operation for every mutable combat object. A seed alone is not a sufficient starting save.

For example, after actions A, B, C, undoing C reconstructs the starting state and runs A then B. Card calculations, power effects, orb changes, RNG consumption, and permanent card growth follow their original game code. This removes many opportunities to forget their inverse mutations. It does not prove determinism or universal mod compatibility.

### Concrete support in the installed game

All source references below use the installed-game decompilation directory recorded above.

- `CombatReplay` contains `SerializableRun`, starting action/hook/choice/reward/checksum identifiers, replay events, and recorded checksum data.
- `CombatReplayWriter:44–46` subscribes to action enqueue, action resume, and received player choices. Its event types include ordinary actions, hook actions, resumes, and choices (93, 106, 121, 137). This is richer than a list of cards clicked.
- `RunManager:905` records the initial run before rolling/creating/entering the map room. It has another recording path at 1146 for debug room entry. This is not evidence that every special event-combat boundary is independently reloadable.
- `SerializablePlayer:38–58` includes the permanent deck, relics, potions, player RNG, and extra fields. `SerializableRun` additionally carries run RNG, odds, modifiers, map history, and other run data.
- `RunManager.SetUpReplay:413` initializes replay mode. `InitializeShared:476–479` creates fresh action queues, executor, action synchronizer, and choice synchronizer.
- `NGame.LoadRun:1164` creates a new run scene and calls the game's map/room loading path.

These are real recording, data, and initialization facilities. Their presence does not establish that a ready-to-use, arbitrary-stop, multiplayer undo API exists.

### Where complexity remains

1. **A stable, sufficient start:** `ToSave` returns an object graph, and some fields retain references to nested data. The retained checkpoint must actually be isolated by serialization, and the boundary must include the context required by special/event fights. Official saves can also omit arbitrary third-party static or external state.
2. **Complete event ordering:** replay must preserve all players' action, choice, hook, and resume ordering. Logging only the local player's actions misses the shared potion RNG issue. A safe stopping point is not necessarily a single event-list index, since actions can pause and interleave.
3. **Replay-to-live transition:** the inspected `NetReplayGameService` uses `NetGameType.Replay`; sending messages and registering handlers are no-ops. `RunManager.CleanUp:1606` disconnects the network. Those stock paths cannot simply be called and assumed to preserve an active multiplayer session. Continuing from a replay prefix requires explicit lifecycle/network integration.
4. **Side effects and speed:** repeated reconstruction must not accidentally duplicate persistent progress, saves, or external mod effects. The game replay setup itself passes `shouldSave: true`. Fast replay must also preserve logical ordering while reducing presentation delays. No latency estimate has been measured.
5. **Evidence of equivalence:** reproducing recorded actions and generated choices is a stronger behavioral check than comparing one restored payload, but the existing checksum's omissions still apply. A matching checksum alone must not be promoted to a complete proof.

Thus the expected benefit is concentrating correctness work in starting-state reconstruction, event recording/replay, and lifecycle handoff, while allowing the original game rules to calculate consequences. It is not eliminating all possible bugs.

### Other alternatives

- Replacing selective snapshots with generic deep cloning reduces some manual field listing but retains the difficulties of object ownership, native scene resources, running tasks, events, and state outside the cloned graph. It is not the strongest answer to the user's current concern.
- Whole-process or virtual-machine rollback covers more memory but also rewinds transport/process state that remote peers have not rewound. It is not a practical drop-in mod architecture for an ongoing multiplayer session.
- A full combat restart needs only reconstruction of the starting state, without the replay prefix, so it is the simpler operation within this direction. Native reconstruction compatibility still has to be established.

General determinism requirement checked against primary technical explanation: Glenn Fiedler, [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/). Matching starting conditions and ordered inputs only reproduce identical results when the simulation is deterministic; this source is general background, not evidence about StS2's specific replay implementation.
