# sts2-mods

Mods for Slay the Spire 2 (Godot 4.5.1 / .NET 9 / HarmonyX).

| Mod | Description |
|---|---|
| [UndoSync](UndoSync/) | Multiplayer-capable combat undo (Left Arrow). Instant restore in singleplayer; vote-based simultaneous restore across all peers in multiplayer. |
| [PeerView](PeerView/) | Read-only spectate mode for multiplayer combat: click an ally to see their hand, piles, and deck through the vanilla UI. Only the viewer needs it installed. |

## Building

```bash
dotnet build -c Release          # run inside each mod directory; the csproj
                                 # references the macOS Steam install by default
# deploy: copy <ModId>.dll + <ModId>.json into <game>/mods/<ModId>/
```

See [UndoSync/README.md](UndoSync/README.md) for the architecture write-up,
local two-instance multiplayer testing, and the game-update compatibility tooling.

## License

MIT — see [LICENSE](LICENSE). Inspired by the single-player
[UndoAndRedo](https://github.com/luojiesi/SLS2Mods) mod by luojiesi;
this implementation was written from scratch against the game's internals.
