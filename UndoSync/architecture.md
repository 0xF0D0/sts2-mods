# UndoSync

## Purpose and scope

Rebuild b770's combat undo as exactly five production modules: Hook, History,
Replay, Network, UI. Same-build, cooperative players only. No malicious-client
model, reconnect recovery, redo, persisted history, extensibility framework, or
compatibility with old UndoSync wire messages. Steam and ENet multiplayer retain their original connected service, native
transport and lobby identity throughout successful undo. ENet uses port 33771
only for initial localhost harness connection. The production project, assembly, namespace and manifest are `UndoSync`.
The validated minimal implementation replaces the former snapshot implementation.

## Owners and calls

| Module | Owns | Entry points and dependencies |
| --- | --- | --- |
| Hook | Mod initialization, native subscriptions/patches, idle detection, replay input suppression, PID log | Binds native events to History; binds Network at RunStarted; installs UI frame/input callbacks. |
| History | Native replay header, serialized events, stable undo boundaries, proposer record | Begin/Record/MarkBoundary/Freeze/Export/Import/Commit; native packet serialization only. Commit adopts the proposer's header, prefix and boundaries. Never restores or sends messages. |
| Replay | Run-owner reconstruction over the retained connection, deterministic event playback, historical choices | ExecuteAsync(CombatReplay); calls Network.Attach on replacement service; Hook suppresses native senders while playback runs. |
| Network | Proposer record relay, unanimous votes, preparation, operation lifetime, done/checksum/resume/rearmed/release barriers, wire message | Request/Vote/Attach/Tick; relays one proposer record, invokes Replay, commits History only after every peer finishes with the same checksum. |
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
  CleanUp normally disconnects the service. Preserve that exact service during
  replay-owned cleanup and initialize run owners with a local LoadRunLobby holder
  over the current real roster. Successful undo does not call JoinFlow.
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
2. Left Arrow selects an earlier boundary from the proposer's history. Only the
   proposer exports its native room-entry header, selected event prefix and undo
   boundaries. A client uploads that record to the host; the host relays its
   original bytes with the proposal to all peers. Reliable 1 KiB data chunks
   keep records independent of a transport's individual packet-size limits.
   Keep the picker open when remote actions arrive. Opening/selecting history
   does not require current input to be enabled or combat to be idle. Block new
   player actions while voting; discard the old timeline once everyone agrees.
   UI votes are recorded and sent immediately, independent of idle state or local
   history availability. The requester is already counted as agreeing. The UI
   proposal count also comes from the proposer, not each peer's local targets.
3. All votes accepted: host broadcasts Prepare. Each peer reports Ready only
   after the proposer record has arrived. Ready does not inspect current combat,
   turn, action queues or input availability. No vote-time state/checksum equality
   is required. Host sends Start and collects
   StartReceived receipts over the existing reliable connection before clearing
   old run owners. Keep the protocol handler attached throughout reconstruction.
4. Preserve the exact INetGameService, native NetHost/NetClient and lobby ID.
   During Replay-owned native CleanUp, suppress Disconnect only for that captured
   service. Explicitly dispose EventSynchronizer, OneOffSynchronizer,
   CombatStateSynchronizer and InputSynchronizer in addition to native cleanup's
   owner list. Cancel the old executor and detach its callbacks before creating
   replacement run owners; do not wait for the old action to finish normally.
   Initialize a saved run using the current real roster and a local
   LoadRunLobby holder on the SAME service. There is no new Steam lobby, join,
   reconnect, or load-lobby network handshake. Pump heartbeats/control while the
   old scene is absent; rebind new UI/run owners before entering combat.
5. Restore initial action/hook/choice/reward IDs and inject recorded events.
   Until every peer has finished, suppress native checksum generation and all
   incoming native messages except heartbeat and UndoSync control. This isolates
   old action/choice/sync/checksum packets as well as received divergence reports.
   Historical choices come from the recorded prefix, not live UI.
6. Compute a pure native NetFullCombatState hash without sending/tracking a native
   checksum or advancing NextId. All Done hashes must match. Host then sends
   Resume. Adopt the proposer's original immutable bytes and target boundaries.
   Re-enable native owners and receiving only after final comparison. Each peer
   sends Resumed. Host sends Release after every peer is rearmed; only then is
   player input unblocked. Live incoming actions after rearm must enter history
   even while the local Release continuation is pending.
7. Normal cleanup unbinds subscriptions and disconnects. Failed reconstruction
   closes the partial run/session and returns to menu; successful reconstruction
   proves reference/lobby equality instead of replacing the transport.

Only one operation exists. A request ID distinguishes late ordinary messages.
Before Start, rejection/timeout cancels and resumes the unchanged run. After
Start, failure/timeout ends the reconstructed run and returns to the menu; it
must not expose a partial replay as playable. No rollback/retry framework.
Native checksum generation and receive processing are isolated during
reconstruction. Each new tracker stays at NextId zero; final comparison uses a
pure hash. Ordinary tracking resumes only after all peers finish. The existing
transport and its heartbeat/quality tracker remain alive.

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
- Initial decision (superseded by retained-session revision): existing-transport reuse rejected after source review: native cleanup leaves
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

- Promotion: minimal implementation now owns UndoSync/. Legacy fuzz drivers are
  preserved under tools/LegacyFuzz and excluded from production compilation.

## Retained-session revision, 2026-11-08

This supersedes the earlier reconnect decision: native CleanUp assumes transport
ownership and omits some native handler disposal. The minimal replay now explicitly
owns that additional disposal, so it can retain the service safely. New verified
patch surfaces are NetHostGameService.Disconnect, NetClientGameService.Disconnect,
NetMessageBus.SendMessageToAllHandlers and the string/GameAction GenerateChecksum
overload. Runtime evidence and Steam limitations are recorded in evidence.md.
