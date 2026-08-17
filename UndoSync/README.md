# UndoSync

**Multiplayer-capable combat undo** for Slay the Spire 2 (v0.111, Godot 4.5.1 / .NET 9).

- **Left Arrow** = undo
  - Singleplayer: restores immediately
  - Multiplayer: all players vote; unanimous acceptance restores every peer simultaneously
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
recomputes the game's own state digest (`NetFullCombatState`) and compares it
line-by-line against the dump captured at snapshot time — a **local proof**
that every checksummed field was restored byte-identically, working even in
singleplayer. On mismatch it logs exactly which lines differ. (Its first run
caught a real omission: `PlayerCombatState.Phase`, gameplay-relevant state read
by relics like Unceasing Top. Fixed since.)

### Synchronizer bookkeeping rolled back on restore

Beyond the snapshotted game state, the shared ordering counters are rewound
together (reusing the public replay-bootstrap APIs):

- `ActionQueueSet.FastForwardNextActionId`
- `ActionQueueSynchronizer.FastForwardHookId`
- `PlayerChoiceSynchronizer.FastForwardChoiceIds`
- `ChecksumTracker.NextId` (reflection) + its internal
  `_checksums`/`_queuedRemoteChecksums` lists

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
Two layers of defense:

1. **`tools/SurfaceCheck`** (static, run after each game update — no game launch
   needed):
   - verifies every `AccessTools`/`[HarmonyPatch]` string reference in the mod
     still exists in `sts2.dll` (catches renames/removals the compiler cannot),
   - diffs the instance-field surface of 25 state-bearing types against a
     committed baseline (catches added state before it becomes a silent
     under-restore).

   ```bash
   cd tools/SurfaceCheck
   dotnet run -- check      # after a game update; exit 1 + report on findings
   dotnet run -- baseline   # after updating the snapshot; refresh the baseline
   ```

2. **Restore fidelity self-check** (runtime, every restore): proves the
   checksummed portion of state was restored exactly; failures name the fields.

Known limits: references whose type is only obtainable at runtime (card holder
internals etc.) cannot be verified statically (reported as WARN), and semantic
changes to an *existing* field are invisible to both layers — a surface diff
still ends with reading the decompiled source.

The contract for "what counts as combat state" lives in the game itself:
`NetFullCombatState.FromRun()` is the checksum's field list, and diffing the
model layer (`Core/Combat`, `Core/Entities`) between versions yields the
capture checklist.

## Build / deploy

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
- Turn-boundary anchors ("After player turn start") predate the pre-play-phase
  hooks, so undoing to them rolls back hook effects without re-running them —
  harmless for the base kit, edge cases with turn-start-hook content
  (candidate fix: pre-action anchors)
- Orb visuals are not rebuilt after restore (model state is; display catches
  up on the next orb event) — none of the base test characters use orbs
- Untested: real Steam matchmaking sessions
