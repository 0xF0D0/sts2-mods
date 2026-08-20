# UndoSync

**Multiplayer-capable combat undo** for Slay the Spire 2 (v0.111, Godot 4.5.1 / .NET 9).

- **Left Arrow** = undo
  - Singleplayer: restores immediately
  - Multiplayer: all players vote; unanimous acceptance restores every peer simultaneously
  - The step picker also has a "restart combat" button that jumps straight to the
    start of the fight, through the same vote/restore path as any other step
- Inspired by [luojiesi/SLS2Mods](https://github.com/luojiesi/SLS2Mods) UndoAndRedo
  (single-player only); written from scratch against the game's decompiled
  internals to coexist with the multiplayer synchronization architecture.

## Why a single-player undo mod cannot work in multiplayer

STS2 multiplayer is **deterministic lockstep**: the host only decides action
ordering, and every peer executes the same actions locally. After **every single
GameAction** the full combat state is hashed (XxHash32) and compared against the
host; a mismatch disconnects the client (`ChecksumTracker` →
`NetError.StateDivergence`).

Rewinding state locally therefore gets you kicked on the very next action. Undo
itself has to be a **synchronized protocol**.

## Architecture

| File | Role | Game-version sensitivity |
|---|---|---|
| `StateSnapshot.cs` | Combat model capture/restore (HP, block, powers, card piles, energy, orbs, potions, relics, RNG, monster move machines, combat history), per-player by NetId. Uses the game's own MutableClone/SerializableRng where exposed; reflection for the rest. | **High** — the maintenance hotspot |
| `UiRefresh.cs` | Post-restore visual resync (hand, power icons, potions, pile counters, intents, end-turn/interaction state) | **High** |
| `ChecksumHook.cs` | Snapshot storage keyed by checksum id (the peers' shared logical clock), synchronizer counter rollback, restore fidelity self-check | Medium (mostly public APIs) |
| `UndoProtocol.cs` + `UndoPicker.cs` | 4 custom net messages, vote state machine, timeout, step picker and popups | Low (almost entirely public APIs) |

### Core idea: reuse the game's checksum as a shared clock

`ChecksumTracker.ChecksumGenerated` fires on every peer at the **same logical
moments with the same incrementing id** (after each action + at turn
boundaries). Snapshotting inside that event and keying by checksum id means:

1. "Restore to id 17" refers to the exact same game moment on every peer — no
   clock-synchronization protocol needed.
2. After a restore, the next action's checksum is regenerated **under the reused
   id** and compared across peers again — if the snapshot missed anything the
   peers disagree on, the game itself reports it immediately. **The game's
   divergence detection doubles as this mod's regression test.**

### Restore fidelity self-check

Peer checksum comparison cannot catch *symmetric* omissions (a field missed
identically on every peer still matches). So after every restore the mod
recomputes the game's own checksum payload — `NetFullCombatState.FromRun(rs, null)`
serialized with the game's `PacketWriter`, the exact bytes `ChecksumTracker`
hashes — and compares it against the payload captured at snapshot time. A
match is a **local proof** that every checksummed field was restored
byte-identically, working even in singleplayer.

The comparison is byte-level for a reason: `NetFullCombatState.ToString()`
prints only *counts* for piles/potions/relics/orbs and nothing at all for
per-player `rngSet` / `relicGrabBag`, so an earlier line-diff version of this
check reported PASS while `MaxPotionCount`, `PlayerRng` and `RelicGrabBag` were
never captured at all. On mismatch the mod still runs the line diff to name the
visible differences, and says so explicitly when the lines match but the bytes
don't — i.e. the difference is in a field `ToString()` never prints. (An early
run of this check caught `PlayerCombatState.Phase`, gameplay-relevant state read
by relics like Unceasing Top. Fixed since.)

A later run caught something the line diff couldn't have named as a missing
field, because no field was missing: `StateSnapshot.CopyMutableFields` assigns
every field **by reference**, and four restore paths (`CardModel`, `OrbModel`,
`PotionModel`, `RelicModel`) copy a throwaway clone's fields onto a live
model. The game's own `DeepCloneFields` binds a model's deep sub-objects back
to *the clone* — `CardModel.DeepCloneFields` re-binds `_energyCost`,
`_dynamicVars`, `Enchantment` and `Affliction` to `this` — so after a restore,
a live card's `CardEnergyCost._card` still pointed at the discarded clone.
`CardEnergyCost.GetWithModifiers` reads that back-reference and skips the
global-modifier hook whenever `_card.CombatState == null`, so every global
card-cost modifier (Corruption, anything else routed through
`Hook.ModifyEnergyCostInCombat`) silently vanished on restore. This is a
**symmetric** omission — every peer restores identically wrong — so peer
checksum comparison could never have caught it; only this local byte-exact
check could, and only because `NetFullCombatState.FromRun` writes a card's
`energyCost` at all when the modified cost differs from canonical (a 964-byte
payload against a captured 988). Fixed by
`StateSnapshot.RebindDeepCloneOwnership`, which re-runs the game's own
`DeepCloneFields` on the live model after each `CopyMutableFields`, at all
four sites. A/B proof, same seed, 250 combats each: without the fix, 577
restores / 2 fidelity failures; with it, 575 restores / 0 failures, identical
injected-loadout coverage in both arms.

### Synchronizer bookkeeping rolled back on restore

Beyond the snapshotted game state, the shared ordering counters are rewound
together (reusing the public replay-bootstrap APIs):

- `ActionQueueSet.FastForwardNextActionId`
- `ActionQueueSynchronizer.FastForwardHookId`
- `PlayerChoiceSynchronizer.FastForwardChoiceIds`
- `ChecksumTracker.NextId` (reflection) + its internal
  `_checksums`/`_queuedRemoteChecksums` lists
- `ActionQueueSet._wasReset` (reflection), forced `false` so a restored play
  phase never inherits a "queue was reset" flag from the discarded timeline

`CombatManager`'s per-combat turn coordination (`_turnState`, internal type
`CombatTurnState`) is normalized the same way, in
`StateSnapshot.RestoreTurnCoordination`: `PlayersTakingExtraTurn` is captured
by NetId and rebuilt (it's the one field there that's actually non-empty
mid-play-phase, for the duration of an extra turn), and
`IsEnemyTurnStarted`/`EndingPlayerTurnPhaseOne`/`EndingPlayerTurnPhaseTwo`/
`PlayersReadyToBeginEnemyTurn` are forced back to their always-neutral idle
values — logging loudly if any of them was *not* already neutral, since that
would mean a play-phase checksum fired mid-transition.

Anchor eligibility is also gated on two more play-phase conditions before a
checksum is stored at all (`ChecksumHook.TryStoreSyncPoint`): every player
must actually be in `PlayerTurnPhase.Play` (not still mid turn-start), and no
player may be mid card/potion effect resolution
(`CombatManager.IsExecutingCardOrPotionEffect`) — both catch states the game
would not correctly resume into.

### Protocol (UndoProtocol.cs)

```
Proposer: ← (Left Arrow)
  broadcast UndoProposalMessage { targetChecksumId, proposerNetId }
  → proposer sees   "Waiting for other players... [Cancel]"
  → other peers see "{player} wants to undo the last action. [Reject] [Accept]"
Each peer: UndoVoteMessage { accept } → the HOST tallies
  all accepted → host broadcasts UndoCommitMessage → each peer CommitAsync:
      waits for an idle player play phase, then RestoreTo(id)
  any reject / 30s timeout / proposer cancel → UndoCancelMessage → popups close
```

Messages implement `INetMessage`; `MessageTypes.Initialize()` auto-registers mod
assembly types (an officially supported path). With `affects_gameplay: true`
the connection handshake enforces matching mod lists — a peer without the mod
cannot join at all. Popup text is Korean when the game language is Korean,
English otherwise.

## Surviving game updates

The snapshot enumerates fields by hand, so new game state must be added to it.
Three layers of defense:

1. **`tools/SurfaceCheck`** (static, run after each game update — no game launch
   needed). Checks 1-2 answer *"did the game change?"*; Check 3 answers *"do we
   still capture everything?"* — the second question is the one that let
   `Player.MaxPotionCount`/`PlayerRng`/`RelicGrabBag` go silently missing for a
   long time: a field nobody ever added to the snapshot was already sitting in
   the Check 2 baseline (that check only diffs the game's own field surface,
   not what UndoSync does with it), and a *symmetric* omission — every peer
   missing the same field identically — is invisible to peer checksum
   comparison.
   - **Check 1**: verifies every `AccessTools`/`[HarmonyPatch]` string
     reference in the mod still exists in `sts2.dll` (catches renames/removals
     the compiler cannot).
   - **Check 2**: diffs the instance-field surface of 28 state-bearing types
     against a committed baseline (catches added state before it becomes a
     silent under-restore). `CardEnergyCost`, `EnchantmentModel` and
     `AfflictionModel` are in that surface now (up from 25) — they're
     owner-back-referencing sub-objects that no snapshot field list of its
     own ever watched. Neither this check nor Check 3 can catch a *stale*
     back-reference, though: the field is present and the surface is
     unchanged, only the value's owner is wrong — that's what defense 3
     (below) exists for.
   - **Check 3**: for the 10 types `StateSnapshot` deep-captures, verifies
     every instance field is named in `snapshot-coverage.json` as either
     `captured` (with the code location that reads/writes it) or deliberately
     `ignored` (with a real reason) — failing on any field in neither, and on
     any stale entry naming a field that no longer exists. It also reports the
     backlog of fields whose reason is exactly `"UNREVIEWED"`, so an honest "we
     haven't looked at this yet" always stays visible instead of getting lost.

   ```bash
   cd tools/SurfaceCheck
   dotnet run -- check              # after a game update; exit 1 + report on findings
   dotnet run -- baseline           # after updating the snapshot; refresh the Check 2 field-surface baseline
   dotnet run -- coverage-baseline  # after auditing new/changed fields; top up snapshot-coverage.json
                                     # (preserves every existing entry verbatim, only adds new fields as "UNREVIEWED")
   ```

2. **Restore fidelity self-check** (runtime, every restore): proves the
   checksummed portion of state was restored exactly; failures name the fields.

3. **Headless regression fuzzer (`UndoFuzz.cs`)** (runtime, opt-in only — gated
   entirely behind `--undosync-fuzz`; nothing in the file executes or
   subscribes to anything without that flag). Runs inside the real game
   process with no UI, driving `TestMode` + `RunManager.SetUpTest` +
   `EnterRoomDebug`. Per combat it picks a random character from
   `ModelDb.AllCharacters` (all 5) at ascension 10, a random encounter from
   the act it actually sets (act 0's own `AllRegularEncounters` /
   `AllWeakEncounters` / `AllEliteEncounters` / `AllBossEncounters` — 22 in
   v0.111), records a visited map coord whose `MapPointType` matches that
   encounter's `RoomType`, and injects a random loadout before combat starts:
   0-4 relics, 0-`MaxPotionCount` potions, 0-8 extra deck cards, plus a ~30%
   upgrade roll per deck card. A random `ICardSelector` is installed via
   `CardSelectCmd.UseSelector` so cards/relics that prompt for a card
   selection resolve headlessly instead of blocking forever.

   **Everything injected comes from the game's own factories, never from a
   `ModelDb` catalogue** — `RelicFactory.PullNextRelicFromFront`,
   `PotionFactory.CreateRandomPotionOutOfCombat`,
   `CardFactory.CreateForReward` with `CardCreationOptions.ForRoom`, each fed
   `player.PlayerRng.Rewards`. That is not a stylistic choice: `ModelDb.All*`
   is a catalogue of everything *defined in the game*, not a pool of what
   *this run can produce*, and an earlier version of this fuzzer drew from it
   directly. It handed Ironclad another character's starting relic, fought
   act-3 bosses in act 1, and — via `ModelDb.DebugEnchantments`, whose own
   doc comment says it includes "mock ones for testing" — stapled arbitrary
   enchantments onto arbitrary cards. Those are states the game cannot reach,
   and the failures they produce are not findings. Routing through the
   factories makes rarity rolls, character restrictions, unlocks and
   no-duplicate rules the game's job rather than something this harness
   reimplements (and gets wrong).

   Enchantments are deliberately **not** injected: they have no gameplay pool
   at all. Each one is applied by a specific source that also decides which
   card receives it — `BladeOfInk.OnPlay` enchants only the `Shiv`s it
   creates (BladeOfInk.cs:32-37), and `Shiv` is `TargetType.AnyEnemy`
   (Shiv.cs:54), which is exactly why `Inky.OnPlay`'s use of `cardPlay.Target`
   is safe in the real game. Enchantment capture/restore still gets exercised
   whenever the reward factory happens to deal a card like `BladeOfInk` into
   the deck — that path is authentic; injecting enchantments directly was
   not. It then plays
   random legal cards at random legal targets, ends turns, and at random
   points restores to a random stored sync point. After every restore, beyond
   `ChecksumHook`'s byte-exact restore fidelity, it also checks that the
   driver can still act afterward (the shape an action-id-reuse bug would
   take) and that no `StateSnapshot.Try`/`UiRefresh.Section` catch block
   silently swallowed an exception. Throughput: 250 combats in about 3
   minutes.

   ```bash
   ./"Slay the Spire 2" --undosync-fuzz --undosync-fuzz-count=250 --undosync-fuzz-seed=abtest
   ```

   Logs to the same per-process `UndoSync-<pid>.log`; quits the game when the
   run finishes unless `--undosync-fuzz-noquit` is also passed;
   `--undosync-fuzz-trace` adds per-checksum and action-enqueue/execution
   tracing to the log, for diagnosing drive stalls. Seeds are deterministic
   across processes: the per-combat seed is derived with an
   FNV-1a hash, deliberately not `HashCode.Combine` — .NET randomizes string
   hashing per process, so `HashCode.Combine` gave every run a different
   `pickRng` from the same `--undosync-fuzz-seed`, and the harness's own
   "REPRO: baseSeed=… combatIndex=…" failure line handed back a *different*
   combat on the next run (measured: re-running seed `widen1` after a fix
   turned combat 24 from QUEEN_BOSS/Ironclad into SEAPUNK_NORMAL/Defect).
   FNV-1a over the UTF-16 code units is stable across processes, machines and
   runtimes, so a REPRO line now reproduces the same combat every time.

Known limits: references whose type is only obtainable at runtime (card holder
internals etc.) cannot be verified statically (reported as WARN), and semantic
changes to an *existing* field are invisible to both layers — a surface diff
still ends with reading the decompiled source.

The contract for "what counts as combat state" lives in the game itself:
`NetFullCombatState.FromRun()` is the checksum's field list, and diffing the
model layer (`Core/Combat`, `Core/Entities`) between versions yields the
capture checklist.

## Build / deploy

Most users should just grab the packaged release zip instead of building —
see the root [README's Install section](../README.md#install) for the
download and drop-in steps.

For development:

```bash
dotnet build -c Release   # csproj references the macOS Steam path by default
# deploy: UndoSync.dll + UndoSync.json into <game>/mods/UndoSync/
```

Local two-instance multiplayer testing (no Steam matchmaking; ENet on
127.0.0.1 — requires `steam_appid.txt` next to the executable for direct
launches):

```bash
./"Slay the Spire 2" --fastmp host_standard          # instance 1 (host)
./"Slay the Spire 2" --fastmp join --clientId=1001   # instance 2 (client)
```

Logs: `<godot-user-data>/logs/UndoSync-<pid>.log` (per-process, since local
instances share the user-data directory).

## Known limitations / TODO

- Simultaneous proposals from two peers ignore each other until the 30s
  timeout (inefficient, not a deadlock)
- If another modal is open on a peer, the vote popup cannot show → that peer
  cannot vote → the proposal times out
- A restore exception can leave a partial restore (per-section try/catch) —
  multiplayer catches it via checksum kick; singleplayer only via logs
- Turn-boundary anchors ("After player turn start") defer their snapshot until
  `CombatManager.TurnStarted` fires for the player side — by then every
  player's pre-play-phase hooks have already run, so undoing to a turn start
  no longer rolls back their effects without re-running them
- `NOrbManager` node sync: handled. This was not the cosmetic gap an earlier
  version of this list called it — a desynced orb node layer **throws inside
  `PlayCardAction`**, so the card and energy are spent and the effect never
  lands. Two shapes were observed: `EvokeOrbAnim` matches nodes to models by
  reference identity (`_orbs.Last(n => n.Model == orb)`, NOrbManager.cs:263)
  and throws `Sequence contains no matching element` when no node holds the
  restored model; and `TweenLayout` indexes `_orbs[i]` for `i < OrbQueue.Capacity`
  (NOrbManager.cs:306-327), so a restored capacity larger than the node list
  throws `ArgumentOutOfRangeException` — reachable from *any* card once a power
  like `StormPower` channels through `Hook.AfterCardPlayed`.
  `UiRefresh.SyncOrbNodes` now rebuilds the node list to equal the model after
  every restore: one node per slot, the live restored models handed to the
  first `Orbs.Count` of them in order, empty slots after — the same invariant
  `AddOrbAnim`/`EvokeOrbAnim` maintain. It rebuilds rather than diffs, so orbs
  and capacity growing or shrinking, and being at max capacity, are all covered
  by construction.
- Untested: real Steam matchmaking sessions
- `CombatTurnState.PendingLoss`: handled. `CombatManager.LoseCombat()` can set
  it synchronously mid-action (e.g. lethal self-damage) before the game drains
  it (`CheckWinCondition`/`ProcessPendingLoss`), so `TryStoreSyncPoint` now
  refuses to anchor at all while `CombatManager.IsAboutToLose` is true — a
  lethal action's finished-execution checksum can no longer become an undo
  target while the loss is still only pending. See `snapshot-coverage.json`
  for the full reasoning (including why a restore can't land after the loss
  is processed either).
- `ActionQueueSet._actionsWaitingForResumption` /
  `PlayerChoiceSynchronizer._receivedChoices`: handled. `RestoreTo` now clears
  both unconditionally. A restore only ever runs with every action queue
  empty, so neither list can have a live waiter at that moment — any
  surviving entry is orphan garbage, and leaving it behind is actively
  dangerous (a stale `_actionsWaitingForResumption` entry can collide with an
  action id `RestoreTo` reuses after rewinding `NextActionId`). See
  `ChecksumHook.RestoreTo`'s Change B comment and `snapshot-coverage.json`.
  A non-zero count at restore is logged loudly, since it would mean this
  invariant broke.
- The fuzzer (`UndoFuzz.cs`) has no per-action checksums to compare on the
  headless path by design: `NonInteractiveMode.IsActive` makes
  `ActionExecutor` take a branch that never subscribes `JustBeforeFinished`,
  which is what `RunManager.SendPostActionChecksum` hangs off
  (RunManager.cs:489, :568). The harness generates its own, mirroring
  `SendPostActionChecksum`'s own filter, but the timing differs slightly:
  production fires from inside `GameAction.Execute`'s finally right after
  `State = Finished`; the harness fires after `Execute()` returned and the
  executor called `AfterActionFinished`.
- The fuzzer is singleplayer only — it cannot exercise the vote protocol,
  peer divergence, or a downed-teammate restore.
- **The fuzzer is blind to the node layer, by construction — and that is not
  a cosmetic limitation.** Two independent reasons: `NCreature.Create` returns
  null whenever `TestMode.IsOn` (NCreature.cs:450-455), so no `NCreature` and
  therefore no `NOrbManager` ever exists in a headless run, and every call site
  is null-conditional (`...?.OrbManager?.EvokeOrbAnim(orb)`, OrbCmd.cs:140) and
  silently skipped; and the fuzzer's only assertion is the checksum payload,
  which is model state by definition — per-client node state cannot be in a
  checksum peers are expected to agree on. So a node/model desync passes
  `FIDELITY: PASS` even if the nodes were present. The `NOrbManager` bug above
  is exactly this class, and it kills runs rather than just looking wrong.
  Covering it needs a harness with real nodes (`TestMode` off), not more
  scenarios in this one.
- Fuzzer stalls are self-diagnosing, and the two that were actually observed
  are fixed. The fuzzer mirrors the game's own `Log.Error` into its log (a
  `--undosync-fuzz`-only Harmony prefix, since `CombatManager.RunTurnLoopAfter`
  reports a dead turn loop only through `Log.Error` and Sentry —
  CombatManager.cs:516-528 — with no public flag or event), and dumps the
  executor/queue/turn-coordination state on every stall. That turned ~4% of
  combats stalling for 10s each into two named causes, **both of them the
  harness's own doing**:

  1. `Inky.OnPlay` (Inky.cs) takes `cardPlay.Target` as its target for every
     card whose `TargetType != AllEnemies`, and `PowerCmd.Apply` dereferences
     it (PowerCmd.cs:77). That is safe in the real game, where Inky is only
     ever applied to `Shiv` — the fuzzer was stapling it onto arbitrary cards
     out of `ModelDb.DebugEnchantments`. The throw stranded
     `ActionExecutor.CurrentlyRunningAction` on a finished player-driven
     action, which left
     `CombatManager.WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction`
     (CombatManager.cs:1474-1480) awaiting a `TaskCompletionSource` that only
     fires on the next `AfterActionExecuted`, with the queue already empty —
     stuck in `EndTurnPhaseOne` for good. Fixed by not manufacturing the state
     (see the enchantment note under defense 3).
  2. `FurCoat.BeforeCombatStart` (FurCoat.cs:127-133) reads
     `Owner.RunState.CurrentMapPoint.coord` unguarded; the harness entered
     combat through `EnterRoomDebug` without ever selecting a map point.
     Fixed by recording a visited map coord before the location routers run.

  With both fixed, 250 combats complete cleanly in ~70s with zero captured
  game errors, against 243/250 in ~190s before. As a standing net for any
  future cause, a captured "turn loop died while its combat is in progress"
  now abandons that combat immediately and is reported on its own summary
  line — the game's failure, not an UndoSync finding.
