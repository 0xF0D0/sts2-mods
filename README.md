# sts2-mods

Mods for Slay the Spire 2 (Godot 4.5.1 / .NET 9 / HarmonyX).

| Mod | Description |
|---|---|
| [UndoSync](UndoSync/) | Multiplayer-capable combat undo (Left Arrow). Instant restore in singleplayer; vote-based simultaneous restore across all peers in multiplayer. |
| [PeerView](PeerView/) | Read-only spectate mode for multiplayer combat: click an ally to see their hand, piles, and deck through the vanilla UI. Only the viewer needs it installed. |

## Install

1. Download the latest `<Mod>-v<version>.zip` for the mod(s) you want from this
   repo's [Releases](https://github.com/0xF0D0/sts2-mods/releases) page.
2. Close the game if it's running.
3. Extract the zip into the game's `mods/` folder, which sits next to the game
   executable. On macOS that's (verified path):
   `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`
   On other platforms, it's the `mods/` folder next to the executable.
4. You should end up with `mods/UndoSync/UndoSync.dll` +
   `mods/UndoSync/UndoSync.json` (and/or the `PeerView` equivalents) — the
   folder name has to match the mod id.
5. Launch the game. The first launch after installing a new mod shows a
   one-time mod-consent prompt.

**Multiplayer note (UndoSync only):** UndoSync declares `affects_gameplay:
true`, so the connection handshake enforces matching mod lists — every player
in the session needs UndoSync installed, and a peer without it cannot join.
PeerView is UI-only (`affects_gameplay: false`) and does not have this
requirement.

## Building (from source)

```bash
dotnet build -c Release          # run inside each mod directory; the csproj
                                 # references the macOS Steam install by default
# deploy: copy <ModId>.dll + <ModId>.json into <game>/mods/<ModId>/
```

Building from source needs the game's `sts2.dll` (and a couple of other
reference DLLs); the csproj points at the macOS Steam install by default and
can be overridden with the `STS2DataDir` MSBuild property.

See [UndoSync/README.md](UndoSync/README.md) for the architecture write-up,
local two-instance multiplayer testing, and the game-update compatibility tooling.

### Releasing

`./tools/package.sh` builds both mods and produces `dist/<Mod>-v<version>.zip`
(pass mod names as arguments to package only some of them). Attach the
resulting zip to a GitHub Release tagged `<Mod>-v<version>`.

## Native save/load and replay experiment

The `experiment/native-replay-undo` branch investigates rebuilding a fresh run
from an immutable native starting save, then replaying the recorded actions up
to a safe decision point. All players' actions, choices, hook actions and
resumptions matter; replaying only the local player's clicks is insufficient.
The existing UndoSync implementation remains available while this direction is
validated in the separate `UndoReplayLab` diagnostic mod.

The first checks cover potion-generated card choices and RNG state, permanent
card growth, and whether a new action executes after reconstruction. A matching
network checksum alone is not proof that all gameplay state was restored.
Live multiplayer handoff, UI reconstruction and external mod side effects need
their own runtime evidence. The game's replay networking service is not a
drop-in replacement for an active multiplayer connection.

Implementation is delegated to Luna; design, game API investigation and review
are handled separately. On 2026-09-06 KST, the isolated engine test passed two
fresh reconstructions of the same combat. Each replay executed four actions,
including two potion choices and their resumptions. Potion options, run RNG,
Genetic Algorithm's permanent deck growth and the combat packet matched the
original. This is a single-player model test; normal input after replay, UI,
hook-action coverage and live multiplayer ordering remain unverified.

See the [runtime evidence and limits](docs/NATIVE_REPLAY_VALIDATION.md),
[architecture and root-cause record](docs/ROOT_CAUSE_REVIEW.md) and
[isolated Windows test launcher](tools/ReplayLab/README.md).
Experimental results are not a claim of production-ready undo.

## License

MIT — see [LICENSE](LICENSE). Inspired by the single-player
[UndoAndRedo](https://github.com/luojiesi/SLS2Mods) mod by luojiesi;
this implementation was written from scratch against the game's internals.
