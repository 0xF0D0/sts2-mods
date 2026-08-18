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
   - **Check 2**: diffs the instance-field surface of 25 state-bearing types
     against a committed baseline (catches added state before it becomes a
     silent under-restore).
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
- Orb visuals are not rebuilt after restore (model state is; display catches
  up on the next orb event) — none of the base test characters use orbs
- Untested: real Steam matchmaking sessions
