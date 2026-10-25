# UndoSync

Cooperative combat undo, implemented by five modules: **Hook, History, Replay,
Network and UI**. This is the validated minimal implementation promoted into the
original `UndoSync` project; the assembly, namespace, mod ID and install folder
are all `UndoSync`. This update is version `0.1.8`.

During idle player combat, press **Left Arrow**, select a native card-history
tile, and request undo. The requester is already counted as agreeing; other
players explicitly accept or reject. After unanimous agreement, native run
reconstruction/replay restores the selected prefix. The original UndoSync card
picker, pagination and confirmation/voting popups are reused.

History lives only in memory. Native multiplayer connections are recreated
when restoring; Steam uses the host's Steam identity, while ENet restore currently
uses localhost port 33771 for local validation. All peers must use the same build.
No malicious-client handling, redo or reconnection recovery is included.

```sh
dotnet build UndoSync/UndoSync.csproj -c Release -warnaserror
dotnet run --project tools/MinimalCheck -c Release -- UndoSync \
  "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
```

Install `UndoSync/bin/Release/net9.0/UndoSync.dll` and `UndoSync/UndoSync.json`
under the game's `mods/UndoSync/` folder. Disable the former prototype
`UndoSyncMinimal` and duplicate Workshop/local copies when verifying the build.
`tools/package.sh UndoSync` already packages this project and identity.

See [architecture.md](architecture.md) for ownership and lifecycle, and
[evidence.md](evidence.md) for runtime evidence and remaining gaps. Prior
prototype artifacts identify their original DLLs and must not be described as
runs of the promoted assembly.

`tools/MinimalHarness` targets this production project. The former broad fuzz
drivers are preserved in [tools/LegacyFuzz](../tools/LegacyFuzz/README.md); they
still depend on the retired snapshot core and are not runnable against this
implementation. The older `tools/SurfaceCheck` also targets that retired core;
use `tools/MinimalCheck` for this implementation.
