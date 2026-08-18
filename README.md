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

## License

MIT — see [LICENSE](LICENSE). Inspired by the single-player
[UndoAndRedo](https://github.com/luojiesi/SLS2Mods) mod by luojiesi;
this implementation was written from scratch against the game's internals.
