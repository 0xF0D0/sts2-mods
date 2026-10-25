# Preserved legacy UndoSync fuzz drivers

`UndoFuzz.cs` and `MpFuzz.cs` are the original drivers moved from `UndoSync/`
when its production implementation was replaced by the minimal core.
They retain their original source, including seeded randomized deck/relic/potion/
upgrade coverage. `legacy-architecture.md` preserves their former core context.

These files have no project and are excluded from production compilation.
They depend on removed snapshot/protocol APIs and have **not** been ported to
native minimal replay. Do not claim their old results validate the current core.
The active real-process harness is `tools/MinimalHarness`.
