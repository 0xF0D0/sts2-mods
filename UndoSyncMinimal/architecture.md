# UndoSyncMinimal

## Purpose and scope

Rebuild b770's combat undo as exactly five production modules: Hook, History,
Replay, Network, UI. Same-build, cooperative players only. No malicious-client
model, reconnect recovery, redo, persisted history, extensibility framework, or
compatibility with old UndoSync wire messages. Steam multiplayer uses native
host identity; ENet restore is limited to the localhost harness on port 33771. The old `UndoSync/` project remains
untouched. The new assembly and manifest are `UndoSyncMinimal`.

## Owners and calls

| Module | Owns | Entry points and dependencies |
| --- | --- | --- |
| Hook | Mod initialization, native subscriptions/patches, idle detection, replay input suppression, PID log | Binds native events to History; binds Network at RunStarted; installs UI frame/input callbacks. |
| History | Native replay header, serialized events, stable undo boundaries, frozen replay prefix | Begin/Record/MarkBoundary/Freeze/Commit; native packet serialization only. Never restores or sends messages. |
| Replay | Run teardown/recreate, saved multiplayer lobby, deterministic event playback, historical choices | ExecuteAsync(CombatReplay); calls Network.Attach on replacement service; Hook suppresses native senders while playback runs. |
| Network | Proposal, unanimous votes, chosen target, operation lifetime, done/checksum/resume barrier, wire message | Request/Vote/Attach/Tick; freezes History, invokes Replay, commits History only after every peer finishes with the same checksum. |
| UI | Left-arrow picker, vote buttons, progress/error display | Reads History targets and Network status; calls Request/Vote. No history mutation or native restore logic. |

These are concrete owners, not five interface hierarchies. Bootstrap/logging live
in Hook, wire types in Network, and lobby/choice helpers in Replay. Helpers inside
these directories do not create additional modules. Native callbacks depend on
History/Network; Network drives Replay; Replay's Attach callback installs the new
transport without starting another operation. All run transitions stay on the
Godot thread.

## Native authority

Source audited from installed sts2.dll v0.111.0 / 41cef1ea, decompiled to
`/tmp/undosync-minimal-source` (external evidence, not vendored source):

- CombatReplayWriter.RecordInitialState builds the header before room entry;
  `_replay` is private. Record native actions, hooks, resumes and choices in the
  same shape as its event handlers. Serialize immediately to detach mutable data.
- RunManager.SetUpSavedSingleplayer/SetUpSavedMultiplayer rebuild native owners;
  CleanUp disconnects the old service. Use the game's LoadRunLobby and JoinFlow
  for multiplayer, preserving player IDs and the old host's platform identity.
- NMultiplayerTest.RunReplay establishes ordering: launch/create NRun/generate
  map, fast-forward action/hook/choice/reward IDs, load the saved room, dispatch
  the four event kinds. FastForward* APIs are public; no counter-field reflection.
- CardSelectCmd.ShouldSelectLocalCard and RelicSelectCmd.ShouldSelectLocalRelic
  are private local-UI decisions. During replay force remote selection and use
  recorded choices, including a singleplayer WaitForRemoteChoice replacement
  because native singleplayer explicitly rejects that wait.
- ActionQueueSet.IsEmpty can be stale after end-turn cancellation. Inspect its
  verified `_actionQueues`/nested `actions` list when deciding a stable boundary.
  Never count an executing or choice-waiting action as idle.

## Flow and lifecycle

1. Capture the native room-entry header. Record native events only during live
   combat. Mark boundaries only at idle player PlayPhase (two unchanged frames).
2. Left Arrow selects an earlier boundary. A client requests it from the host;
   the host broadcasts a proposal. Block new player actions while voting; allow
   already-running actions to drain. Each peer explicitly accepts/rejects in UI and freezes its own prefix.
   Votes reflect user choice only; no automatic record-comparison rejection. The host
   owns the target boundary; peers need the matching prefix, not an identical
   locally observed idle-frame boundary.
3. All votes accepted: host sends Start and waits for each client to acknowledge
   at its replay-start frame. Clients flush that acknowledgement during graceful
   teardown; the host must not close their old connection before that point.
   Keep the frozen plan and participant
   IDs across teardown. Unregister the old protocol handler before cleanup.
   Recreate singleplayer or native loaded multiplayer lobby, attach protocol
   handler to the replacement service, ready all original players and launch.
4. Restore initial IDs before room entry. Suppress automatically generated
   hook/resume sends and player input; inject recorded events locally. Suppress
   historical selection UI. Wait for a stable, idle player boundary.
5. Every peer reports its native final checksum. Host compares all values and
   broadcasts Resume only when all match. Commit the prefix, truncate future
   targets and re-enable input. A subsequent undo reuses the original header.
6. Normal cleanup unbinds subscriptions and clears history. Rebuild cleanup
   preserves frozen history and operation state, then rebinds the new owners.

Only one operation exists. A request ID distinguishes late ordinary messages.
Before Start, rejection/timeout cancels and resumes the unchanged run. After
Start, failure/timeout ends the reconstructed run and returns to the menu; it
must not expose a partial replay as playable. No rollback/retry framework.
Checksums are disabled during reconstruction; each fresh tracker starts at zero,
then generates one final checksum and resumes ordinary native tracking. The new
transport has no old native checksum traffic.

## Hazards and gates

| Hazard | Owner | Gate |
| --- | --- | --- |
| Capture during replay | Hook/History | All recording hooks ignore Executing. |
| Partial target/queued choice | History/Hook | Only stable idle boundaries selectable; frozen prefix does not change during vote. |
| Native send duplicates replay injection | Hook/Replay | Block natural sender methods, permit only replay-owned direct injections. |
| Run owners disposed and recreated | Hook/Replay | Detach old subscriptions, attach new protocol early, preserve operation separately. |
| Player selection UI during historical choices | Replay | Replay-only local-selection override and recorded wait tasks. |
| Peer fails or checksum differs | Network | No Resume; terminal failure and cleanup. |
| Runtime evidence absent | Parent | Build/static pass never means replay/UI/multiplayer verified. |

## Execution and acceptance

- M1: This document and the five owners' contracts precede implementation.
- M2: Implement only `Hook/Hooks.cs`, `History/History.cs`, `Replay/Replay.cs`,
  `Network/Network.cs`, `UI/UI.cs`, project and manifest. Build Release; review
  every patch target and restore call against the installed source.
- M3 (initial authorization): `dotnet build UndoSyncMinimal/UndoSyncMinimal.csproj
  -c Release`, `git diff --check`, manifest parse, five-module structural check,
  and source/API audit. No deployment, game launch or installed settings changes.
- Later runtime acceptance: enable only one UndoSync implementation at a time.
  Focused real-game picker/vote/choice-UI test first,
  then separate full singleplayer and two-real-peer integration tests. Prove
  restore, truncation, second undo, post-undo input, rejection and failure;
  record both PIDs/logs/checksums and terminal verdicts. No stub-peer acceptance.

Stop implementation if native authority cannot be established or a sixth owner
is needed. Re-enter by reading this file, AGENTS.md, git status and evidence.md;
continue the first unfinished gate. Do not commit or publish without a request.

## Decisions and progress

- 2026-10-25: Five concrete modules; b770 is source reference, not architecture.
- Existing-transport reuse rejected after source review: native cleanup leaves
  owner-specific handler lifetimes to run disposal. Native reconnect path avoids
  a second transport-lifetime mechanism.
- Initial turn was limited to build/static validation. On 2026-10-25 the user
  closed the game and authorized focused UI and real two-peer integration tests.
  `tools/MinimalHarness` uses declared internal APIs through a friend assembly;
  it does not mutate private mod state or replace the transport. Native FTUE
  prompts are disabled only in that test fixture. Back up and restore both local
  test accounts because native saved-run setup can persist reload counters.
- M1, M2 and the authorized M3 build/static gate complete. Evidence and runtime
  exclusions are recorded separately in evidence.md.

- M4: Focused native UI run, followed by a separate two-real-peer ENet harness
  run (reject, host undo, client undo, second replay and continued input). Require
  PID-bound terminal verdicts and restore installed mods/accounts after exit.

- M4 bounded ENet integration complete: mp2 host 91219/client 91247 both exit 0,
  two undos and continued input pass. StartReceived teardown race fixed from
  mp1 failure. Native UI picker/restore observed; layout work explicitly deferred
  by the user. Broader runtime exclusions and cleanup evidence: evidence.md.

- M5 authorized: extend runtime validation through four real peers, choices,
  hooks/resumes, turn transitions, repeated undo and ordinary failure paths.
  Execution matrix and gates: tools/MinimalHarness/validation-plan.md. UI layout
  remains deferred; Steam/remote-machine coverage needs distinct real peers.

- Extended validation: user clarified that only a user's vote may reject undo.
  Removed the added history-fingerprint vote check and wire field. A real
  transport/replay failure remains an execution error, never a synthesized vote.
  Native choice receipts may interleave differently across peers legitimately.

- UI corrected per user instruction: replace the temporary panel with b770's
  actual UndoPicker native popup/card strip, pagination, restart confirmation,
  pooled NCard cleanup and native vote/wait popup. UI-local PickerPoint adapts
  read-only History metadata; no old snapshot/protocol module is imported.
  User-only vote semantics remain. MinimalCheck covers all 14 reflection
  surfaces, including copied UI fields verified in installed source.
- Focused legacy-UI run PID 98777 exit 0 PASS: real Left key opened the original
  card picker; actual restart and confirmation buttons executed restore, exact
  native opening state matched, modal backstop cleared and combat visible.
- Interactive three-peer preview currently open: host 98985, client 99002,
  client2 99003. Each reports PREVIEW_READY players=3 and native combat nodes.
  Harness only boots the native room; no automatic plays/votes/quit in preview.
  This open user session is not a terminal integration verdict.
  Temporary default account/mod backup and run logs:
  /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-nme6cxrp.
  Earlier manual Steam UI settings backup (restore after game exit):
  /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-ui-preview-jzi1x83g.
