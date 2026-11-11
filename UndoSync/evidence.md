# Static validation — 2026-10-25

## Result

M2 implementation and M3 build/static gate passed. Runtime acceptance is NOT
claimed. The user explicitly limited this turn to build/static verification.

Production code: 987 lines across exactly five module directories, each with
one C# file. All native Harmony patches live in Hook; Replay owns reconstruction
and historical choice tasks. `tools/MinimalCheck` is a standalone metadata tool,
not a runtime module or dependency of the mod.

Built DLL SHA-256: `7c06857f46d4872c44bdd2d2fd82504f91d295a0a49dbc5b4085c5c6650f6d5e`.

## Commands and inspected evidence

From the undosync-minimal worktree:

```sh
dotnet build UndoSyncMinimal/UndoSyncMinimal.csproj -c Release -warnaserror
dotnet run --project tools/MinimalCheck -c Release -- UndoSyncMinimal \
  "/Users/kimbeomsu/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
git diff --check
```

- Release build: exit 0, zero warnings, zero errors.
- Metadata tool: exit 0. Checked all 12 patch targets are unambiguous, named
  parameters and result/instance injection types agree, the three private fields,
  the ShouldSave setter and nested queue.actions field exist, and the native
  wire message and initializer are discoverable.
- Parsed manifest identity and checked exactly Hook, History, Replay, Network,
  UI as production module directories. New text files checked for trailing
  whitespace and final newlines independently of tracked-only git diff.
- Inspected build and metadata outputs in
  `/tmp/undosync-minimal-static-20261025/build.log` and `metadata.log`.
- V1-only SurfaceCheck was intentionally excluded from this new-project gate.

## Source and lifecycle review

Authority: installed v0.111.0 / 41cef1ea, decompiled source in
`/tmp/undosync-minimal-source`.

Checked RecordInitialState/record handlers, EnqueueWithoutSynchronizing and
resume ordering, literal queue lifetime, native RunReplay counter seeding and
four-event dispatcher, saved-run setup and cleanup, LoadRunLobby readiness,
JoinFlow handler registration/teardown, local card/relic choice decisions and
singleplayer's remote-choice rejection. Checked the native message subtype
catalogue includes concrete mod INetMessage implementations.

Reviewed normal cleanup versus owned replay cleanup, new native-owner binding,
protocol detach/reattach, cancellation and failed-rebuild cleanup, prefix commit
and later undo, and incoming proposals while the local picker is open. The host
owns stable targets; peers validate the prefix without requiring identical local
idle-frame sampling. Host cleanup is graceful so queued Start is flushed before
the native transport is destroyed; delivery timing still needs a real-peer test.

Rejected: copying b770's module graph; preserving the old connection through
native cleanup (would require another lifetime/handler mechanism); treating
compilation, a mock peer, or a silent process as runtime acceptance.

## Not covered

No game launch/termination, temporary deployment, installed-mod replacement or
settings changes. No commits or releases. Existing AGENTS.md edits preserved.
No Harmony patch application, packet serialization execution, native UI layout,
actual replay/choice execution, second undo, multiplayer delivery/checksum barrier,
timeouts or post-undo input were executed. No live before/after result exists.

The next gate is focused native UI validation, then a separate real-game
integration run with explicit terminal verdicts and peer-specific PID/log evidence.
Only one UndoSync implementation may be enabled for that later run. ENet restore
currently targets the localhost harness; Steam uses the captured host identity.


# Runtime validation — 2026-10-25

This section supersedes the earlier build-only boundary. The user authorized
real-game testing, then explicitly deferred UI layout work and asked to proceed
to multiplayer. No commit/release was authorized or created.

## Scope and native UI

- Game: installed v0.111.0 / 41cef1ea, native ENet port 33771. TestMode remained
  off and ordinary native checksums stayed enabled outside replay.
- Focused native UI run PID **90464**, exit **0**, harness PASS: actual CUA Left
  key opened the picker; an actual mouse click on event 0 triggered undo.
  Hand, energy and block visibly restored; serialized NetFullCombatState matched
  the opening state. This proves that interaction/restore path only.
- The picker clips lower buttons. A layout edit was started, but the user
  deferred it; it was removed from active source/deployment and retained only at
  /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-runtime-wyv927q9/UI-layout-deferred.cs. UI was newly written in this project,
  not copied from b770. Full layout/vote/choice UI acceptance remains open.

## Reproducible failure and narrow fix

Before: **mp1**, host PID **90865**, client PID **90917**, both exited **1**.
Both entered real combat, played a card and rejected an undo without changing
native state. On the next accepted undo, the host sent Start and immediately
closed its old connection. The client received Start but deferred replay until
the next frame; Disconnected arrived first, cancelled the still-unstarted
operation and exited. Host's replacement lobby then waited without its client.
The host harness ended with an explicit host-undo timeout verdict.

Fix: Network waits for StartReceived from every client before host teardown.
Each client sends that acknowledgement at its actual replay-start frame, then
uses native graceful cleanup to flush it. The host freezes its roster across
that expected disconnection. No new production module or malicious-client
handling was introduced.

## After: real two-peer integration PASS

The fixed source passed Release build with warnings as errors using
'dotnet build tools/MinimalHarness/MinimalHarness.csproj -c Release -warnaserror'.
After runtime cleanup, tools/MinimalCheck passed all 12 native patch targets,
reflection surfaces and five-module structure again; git diff --check passed.

Run **mp2**:

| Peer | Game PID | Exit | Terminal verdict |
| --- | --- | --- | --- |
| Host | 91219 | 0 | HARNESS_PASS cards=3 undos=2 divergences=0 |
| Client | 91247 | 0 | HARNESS_PASS cards=3 undos=2 divergences=0 |

Both used native StartRunLobby, player factories, generated act encounter pool
and real ENet transport. The encounter was NIBBITS_WEAK. File markers coordinate
harness phases only; they do not simulate peers, carry production undo traffic,
supply votes or mutate private production state. Requests and votes use the
declared mod APIs; native manual play drives actual game actions.

Observed and checked:

- Both peers enter combat with enabled native checksums.
- Reject proposal: before/after NetFullCombatState unchanged on both peers.
- Host-requested undo: replay prefix **1**, both checksums **2878827343**;
  both serialized native states exactly match the saved target state, history
  count truncated to 1.
- Both play another card. Client-requested second undo: replay prefix **2**,
  both checksums **4131693616**; both native states match the second saved
  target, history count truncated to 2.
- Both play after the second replay; final native state hashes match and
  each peer executed three local card plays total. Zero native StateDiverged
  callbacks across initial and recreated owners.
- StartReceived barrier explicitly executed for both accepted operations.

Tested core DLL SHA-256:
bf3026e89e16ffabd916994e74e5ad53a8916abdf71c80af9e1db6013b116332

Harness DLL SHA-256:
748553eab4f503dcbfc48982676f2fd9c52eebaf27f64710675949d5514b6958

## Artifacts and cleanup

Raw launch logs, launch arguments, exit codes, backed-up installs/accounts and
fixture scripts: /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-runtime-wyv927q9

PID-bound harness logs, filtered core logs and cleanup proof are retained in
tools/MinimalHarness/results/2026-10-25/ in this checkout. Full raw terminal logs
are mp2-host-terminal.log, mp2-client-terminal.log, and ui-terminal.log in the raw
directory. Native logs also remain in the game's user-data logs directory as
UndoSyncMinimal-<pid>.log.

After all task game/caffeinate processes exited, test mod directories were moved
to the artifact backup and prior installed copies restored if present. Both
complete default local accounts **1** and **1001**, including settings, backups
and native saved-run writes, were restored and compared file-by-file by SHA-256
against pre-test backups. No game process remains; UDP 33771 is free. See the
retained cleanup.json.

The passing run still emits native renderer/resource-leak diagnostics during
application shutdown after the harness verdict. These were not investigated;
this result does not certify resource cleanup or leak freedom. No mod
FRAME_FAIL/HISTORY_FAIL/UNDO_FAIL/CLEANUP_FAIL or native state divergence was
observed in the passing run.

## Remaining untested surfaces

Steam transport and remote machines; mid-card selection/hook/resume events
(passing replay choice counters were zero); cross-turn/enemy-turn replay;
long sessions/full combat completion; timeout and mid-replay peer-loss handling;
complete picker/vote UI layout and historical choice UI suppression. Basic
ENet two-peer undo is verified; exhaustive multiplayer correctness is not.


# User-reported live UI/replay regression — 2026-10-25

Before: interactive preview3, PIDs 98985/99002/99003. User manually played cards
and proposed from client2. Initial event-0 replay reached RESUME on all peers,
then host logged NRemoteMouseCursorContainer.GetCursorPosition null exceptions.
A later attack/undo entered RESTORE_HOST_LISTENING; clients logged handshake
retry and no terminal RESUME before the user closed all three games. Old combat
nodes remained visible while old peers disconnected. Client proposal identity
was absent from the wire, so its own user was shown an Accept popup. This
invalidates treating the earlier state-only tests as complete UI acceptance.

Source authority: NMultiplayerLoadGameScreen.AfterMultiplayerStarted binds
RemoteCursorContainer and ReactionContainer to the loaded lobby; Replay and the
manual harness startup omitted that setup. NRun._Process and combat UI belonged
to the disposed run while waiting for a new lobby. NTransition.FadeOut keeps
InTransition=true until FadeIn (important for replay-only readiness).

Changes: remove the old scene while its owners still exist, before native
cleanup; bind native cursor/reaction UI to the loaded lobby, preserve black
transition until the resume barrier, and use gameplay readiness separately from
visual transition readiness during replay. Record proposer identity; its request
is its affirmative vote, and its native popup only waits. No fingerprint vote
check, extra module, fake peer or alternate replay path added.

Focused UI check: regression3-01, PIDs 21065/21081/21082. Actual CUA screenshot
showed host proposer with native 'Undo Proposed' / waiting text / Cancel only,
no Accept. This inspection run then hit the real 30s voting timeout while the
harness waited for visual approval; it is not a replay pass. regression3-02
PIDs 21857/21869/21870 exposed the FadeOut/IsIdle transition deadlock; classified
and aborted with explicit marker, then corrected using verified NTransition
behavior. Failures retained alongside success.

After: regression3-03, PIDs **22577/22588/22589**, all exit **0**, HARNESS_PASS.
Visible real-game processes, TestMode off, real ENet. Each of three participants
requested an undo after a real native attack reduced enemy HP. On all three
replays, exact NetFullCombatState and original HP matched and history truncated.
After every replay and a final client2 attack, checked actual RunLobby connected
IDs, each native avatar's DisconnectedIndicator hidden, each remote cursor node
present and GetCursorPosition finite. Every proposer rendered the waiting popup;
other two participants could vote. Zero StateDiverged callbacks and no
NullReferenceException/FRAME_FAIL/HISTORY_FAIL/UNDO_FAIL during the passing run.
Native shutdown resource diagnostics remain outside this result.

Final core SHA-256: 729c03b7d423fc108230016958c46f31b750506956eeec152db94d61600bd104
Release warnings-as-errors build and MinimalCheck (12 patches, 14 reflection
surfaces, five modules) passed; git diff --check passed.
Logs and exact run arguments are retained under
`tools/MinimalHarness/results/2026-10-25/ui-regression/`; raw terminal logs and
snapshots remain in
/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-nme6cxrp.

Interactive preview3-fixed is reopened on this tested build, with normal starter
decks, three visible titled windows, Korean locale, no automatic plays/votes or
quit. It remains an active user session; fixture/account/settings restoration is
pending its closure. Do not call that interactive preview a terminal test pass.
No commit/release. Steam P2P/separate computers and broad loadout fuzzing remain
unverified; old fuzzers are preserved but not ported.


The user manually exercised preview3-fixed and confirmed it now works before
requesting this commit. This adds human confirmation of the visible local ENet
flow; it does not establish Steam P2P or separate-machine transport coverage.
The three preview processes remain open for the user; no launch/termination or
installed-file change is part of the commit operation.


# Promoted production identity: UndoSync

The user requested replacing the old UndoSync implementation with the validated
minimal core. The production project, namespace, DLL, manifest and install ID
are now UndoSync. Prototype UndoSyncMinimal sources were removed. All five
production module files were compared against commit 13bd339 after namespace,
Harmony ID and log identity substitution: no other behavior changed. The
existing manifest version was retained; no release or follow-up commit created.
Legacy UndoFuzz/MpFuzz sources are preserved outside production compilation in
tools/LegacyFuzz, unported. Active harness and metadata checker target UndoSync.

Release warnings-as-errors build and metadata-only check passed (five modules,
12 patches, 14 reflection surfaces). Source scans found no active prototype
assembly or namespace references. git diff --check passed.

Real visible three-process run promoted3: host PID 27649, client 27680, client2
27681, all exit 0/HARNESS_PASS, each undos=3 and divergences=0. Each requester
already agreed, native waiting popup only; other participants explicitly voted.
Actual attacks reduced enemy HP before undo; full native state/HP/history were
restored each time; native avatar online indicators and remote cursors checked
on all peers; post-replay attack synchronized. All replay-completion checksums
were 4058208785. No NullReferenceException, FRAME_FAIL, HISTORY_FAIL or UNDO_FAIL
in that run. Native shutdown resource diagnostics remain outside the result.

Core DLL SHA-256: 384c359b88376546275d24eaea18c0e608a80854a78319bcedbd3ccb07ebb829

Retained run/role/core logs and cleanup proof:
tools/MinimalHarness/results/2026-10-25/promoted3/.
Raw logs, snapshots and backup fixture:
/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-63y3_cb5.
After all games exited, prior UndoSync/MinimalHarness installations and default
accounts 1/1001/2001/3001 were restored with SHA-256 comparison. Earlier manual
Steam settings and prototype install backup were restored before this run.
No game process remains and UDP 33771 is free. Existing AGENTS.md edits remain
outside this migration. Steam P2P, four-player completion and broad loadout
fuzzing remain unverified.


# v0.1.8 release packaging

User explicitly requested running the update distribution workflow. Manifest
version advanced from 0.1.7 to 0.1.8. tools/package.sh UndoSync completed and
MinimalCheck passed again. ZIP contains only UndoSync/UndoSync.dll and
UndoSync/UndoSync.json; manifest identity/version validated. DLL bytes match the
promoted3 tested DLL (384c359b88376546275d24eaea18c0e608a80854a78319bcedbd3ccb07ebb829).
ZIP SHA-256: f93062dc14c132d6e98ccfa5e40315fe0ccdaa1cfe4492be8b02e64bd56dac33.
Steam P2P, four-player completion and broad loadout fuzzing exclusions apply to
this release as well. No additional gameplay code changed during packaging.

# 2026-10-27: deliberately different native replay speeds

Run `speed-skew3`, visible local ENet, game v0.111.0 / 41cef1ea.
PIDs host 3625, client 3652, client2 3653: all HARNESS_PASS and exit 0.
Core source unchanged from HEAD a9a63aa; rebuilt DLL SHA-256:
68b1d9cd63a8d6c5833869168006802d194cc6b188af1303dc66c00d04b367ad.
This run used the rebuild, not the byte-identical published ZIP DLL.
Build with warnings as errors and MinimalCheck both passed.

Harness-only PlayCardAction.ExecuteAction task wrapper held the real action
executor unfinished for 8017 ms on client2, then 8026 ms on host. In each case
both faster real peers reached the production Done/Resume barrier while the
slow action was still unfinished; Busy/Executing stayed true and history was
not yet committed. No production fields or transport were substituted.
All three reported checksum 534479772 on both replays, restored the exact
preselected full native state, enemy HP and history length, and continued a
real attack afterward with equal state and zero native divergences. Native
roster/avatar-online/cursor bindings were checked after both rebuilds.
Direct input attempts during the waiting interval were not part of this run.

Retained evidence: tools/MinimalHarness/results/2026-10-27/speed-skew3/.
Full raw logs/snapshots and fixture:
/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-ydgdyb_9.
Cleanup restored all four accounts and both mod installations, with original
file hashes verified; no game process remains and UDP 33771 is free.
Scope: local three-peer ENet with an eight-second native-action delay;
cross-machine Steam P2P, packet impairment and four players remain untested.

# 2026-10-27: English and Chinese localization

UI uses the game's native LocManager language codes: eng (English fallback),
zhs (Simplified Chinese), zht (Traditional Chinese), kor (Korean retained).
All 19 keys share the native LocString path, including picker labels, page
numbers, system action tiles, restart confirmation and proposal/vote/wait UI.
Production changes are confined to UI; five modules remain.

Game v0.111.0 / 41cef1ea. Core DLL SHA-256:
c2cb948c3c52609a2a1eb556e101180652768a4d6528e0386309799907e92c49.
Build with warnings as errors, MinimalCheck and diff whitespace checks passed.
Focused visible native UI run localization-ui-02, PID 60569: PASS, exit 0.
Switched all four languages through native SetLanguage, opened the real picker,
checked native key formatting, used the restart button's normal Released signal,
verified confirmation and cancellation, and captured eight native screenshots.
English and Chinese screenshots were visually reviewed for glyphs and layout.

Separate integration localization-mp3-02: host 60865 (eng), client 60876 (zhs),
client2 60877 (zht), all HARNESS_PASS and exit 0. Each peer proposed undo once.
Captured localized native proposal/wait popups; all three restores matched full
native state, HP and history, then a real attack continued with equal states.
Zero native divergences; avatars online and remote cursors bound after replay.

Earlier attempts are retained, not counted as passes: localization-ui PID 60420
stalled in the harness FramePostDraw wait (replaced with explicit redraw);
localization-mp3 PIDs 60652/60666/60667 failed due to colliding Actor/StateBarrier
file-marker names after all three successful restores. StateBarrier now uses a
distinct state-ready marker, and the complete integration rerun passed.
Native cleanup logs include Local player not found in combat exceptions, also
confirmed in the pre-localization speed-skew3 raw logs; this change does not
claim to resolve that existing cleanup diagnostic.

Evidence and screenshots: tools/MinimalHarness/results/2026-10-27/localization/.
Full raw logs/fixtures: /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-g9hzl90o.
All game processes exited; prior mod installs and four accounts restored with
original hashes verified; UDP 33771 free. No release or Workshop upload performed.
Four-player and cross-machine Steam P2P remain outside this localization test.

# v0.1.9 release packaging

User requested GitHub release and Steam Workshop distribution. Manifest and
README advanced to 0.1.9. tools/package.sh UndoSync and MinimalCheck passed.
ZIP contains only the mod DLL/manifest, and its DLL is byte-identical to the
localized native UI and three-peer tested build:
c2cb948c3c52609a2a1eb556e101180652768a4d6528e0386309799907e92c49.
ZIP SHA-256: 8f82eba2ef5ab582e893c7e588c78b7f2cf3698ab1e5716f57be9754c8794029.
Publication receipts and the previous Workshop workspace backup are retained
under /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-release-v0.1.9-94d76z2_.
Upload success and subscriber refresh must be verified separately.

# 2026-11-08: native Accept could silently discard a vote while not idle

Report: three Steam players saw proposal/vote UI; the last participant accepted,
but vote/wait popups and combat stayed unchanged. User has no incident logs.
The exact cause of that Steam session is therefore not established.

Verified common-path defect: Network.Vote(true) returned before setting LocalVoted
or sending Vote whenever Hooks.IsIdle() was false. Propose is only host-idle
checked; a client may still be finishing an earlier native action. UI displays
the proposal regardless of that client's idle state. Earlier regressions used
direct Network.Vote calls after Idle and did not exercise this timing window.

Before/after reproduction uses three real local ENet processes, game v0.111.0 /
41cef1ea. A normal client2 card is played before the proposal; a harness-only
PlayCardAction task wrapper holds completion for 8 seconds on that client while
the host finishes normally. Other votes and the final vote use real native
NPopupYesNoButton Released callbacks. No production private state is mutated.

Valid before: vote-busy-before-03, PIDs 12548/12587/12588, FAIL as expected:
LIVE_ACTION_HOLD_BEGIN, non-idle last voter, then native Accept leaves LocalVoted
false. Host recorded only votes 1 and 1001; no vote from 2001 and no replay start.
Earlier two attempts are classified as invalid fixtures: new input was requested
after the proposal and correctly blocked by the existing input gate. Their logs
are retained and do not count as the defect reproduction.

Fix is confined to Network: retain acceptance immediately, freeze/submit on the
next idle tick, and wait for local idle before starting reconstruction. Reject
still sends immediately. Added receipt/local-vote/wait/submission/vote-count
markers and platform identity for future incident diagnosis. Wire format and
the five-module architecture remain unchanged.

Focused after: vote-busy-after, PIDs 13168/13207/13208, all HARNESS_PASS and exit 0.
LAST_ACCEPT_WHILE_BUSY retained=true -> LIVE_ACTION_HOLD_END -> VOTE_SUBMITTED
-> ALL_VOTES_ACCEPTED -> all three RESUME with checksum 4058208785. Exact combat
state/history restored; online avatars/cursors and continued real attack passed.

Separate full native-button integration: vote-ui-regression3, PIDs
13980/14017/14018, all HARNESS_PASS and exit 0. Every participant proposed once;
other players accepted through native popup buttons. Three restores matched HP,
full native state/history and checksum 4058208785, with continued attack and
zero native divergences. Build with warnings as errors and MinimalCheck passed.
Tested fixed core DLL SHA-256:
b5e274e9996b934945d9bded502daa9b480bf432d5e511b786cd5996ab81f52c.

Evidence: tools/MinimalHarness/results/2026-11-08/vote-idle/.
Raw logs/fixtures: /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-0jp9f0i4.
Prior installed mods and all four accounts restored with original file hashes;
all games exited and UDP 33771 free. Existing native cleanup diagnostics remain
outside this change. No commit, release or Workshop update was made for this fix.
Cross-machine Steam P2P and the user's exact incident remain unverified.

# 2026-11-08: proposer record, immediate votes and separate preparation

User approved using the proposer's record, not the host's. This supersedes the
earlier pending-vote fix: acceptance/rejection is now sent immediately even if
the voter is not idle. Only execution readiness waits for ongoing actions.

Only the proposer exports the native combat-start header, selected event prefix
and earlier undo boundaries. A remote proposer sends that record to the host;
the host forwards the exact bytes to all clients in reliable 1 KiB chunks.
There is no vote-time history/state/checksum equality requirement. Proposal
step counts also come from the proposer. After unanimous votes, Prepare/Ready
separately waits for record arrival and local idle, then the existing Start and
StartReceived barrier runs. Final native checksum equality remains mandatory.
After Resume every recorder adopts the original proposer header/events/targets.
Native execution objects may be mutated; commit decodes fresh original bytes.
Wire messages changed: every participant must run this same new build.

Game v0.111.0 / 41cef1ea; tested core DLL SHA-256:
93461e829142ff633366d1fab4ae26466a5ec29f6e42867bf298015bf031b907.
All final runs below use this same core and three actual local ENet processes:

- vote-immediate-ready3-02: PIDs 24302/24345/24346, all HARNESS_PASS, exit 0.
  A real native card task was held for 8 seconds on client2. Native Accept
  reached the host and all votes were collected before that task finished;
  only Ready waited. Replay, exact state/history and continued attack passed.
- proposer-record3-02: PIDs 24829/24868/24869, all HARNESS_PASS, exit 0.
  Each player proposed once. As an intentional recorder fault, the other two
  peers' local History was cleared while the native combat remained intact.
  Native proposal/wait UI and button callbacks worked without those local
  records. All peers restored the proposer's nonempty prefix, preserved earlier
  undo points and re-exported byte-identical proposer data after continued play.
  The host was itself a history-free recipient for both client proposals.
- proposer-extended3-03: PIDs 27657/27716/27717, all HARNESS_PASS, exit 0.
  Native GameAction/HookAction/ResumeAction/PlayerChoice coverage: 3/3/5/5 in the
  first target prefix. Rejection and a real 30-second withheld-vote timeout left
  combat unchanged. Every player requested undo, followed by an additional
  cross-enemy-turn replay and continued native play. Four undos per peer,
  no replay-time live selection prompts, zero native divergences.

For every final-run session, an artifact check confirms all three real peers
logged the same proposer payload SHA-256. Multi-chunk sending and native
reassembly executed. Native UI screenshots were retained and visually reviewed.
Build with warnings as errors, MinimalCheck and diff whitespace checks passed.

Retained failed attempts are classified, not counted as passes. proposer-record3
restored combat correctly but caught a host-only future-export discrepancy when
committing the native execution object; fresh decoding of original bytes fixed
the repeated same-case check. Initial extended runs failed before any proposal:
the harness first asserted idle after a blind delay, then compared concurrent
snapshots before two remote actions had reached a client. Idle now requires five
consecutive idle frames, and concurrent comparisons wait for all own-completion
markers plus the common received-event count. Concurrent input remains enabled.

Evidence: tools/MinimalHarness/results/2026-11-08/proposer-record/.
Raw logs/backups: /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-gzkqlzsb.
Cleanup restored installed mods and all four accounts with original file hashes;
all game processes exited and UDP 33771 free. Existing native teardown diagnostics
remain outside this change. No commit, release or Workshop update was performed.
Steam P2P across separate computers, four peers and a deliberately mismatched
final-checksum fault were not live-tested; the final checksum guard was retained.

## 2026-11-08 additional timing sweep and release authorization

User ended the planned 100-case timing sweep after the current case and requested
deployment. Nine 3-peer cases (TIMING-SWEEP-M3-000..008, fixed CONTENT-B) passed,
27 undo operations total. All proposers rotated, busy last UI votes were retained,
Prepare preceded held-action drain, slow replay peers reached Done before Resume,
full native target states/checksums matched and post-replay gameplay continued.
Each of the four phase coverage counters totals 27. No production failure was
observed in those nine cases. The planned 50/50 batch was not completed; no new
4-peer batch case was run. Previous four-peer M3 evidence remains separate.

Artifacts: tools/MinimalHarness/results/2026-11-08/timing-sweep/. Every case has
PID-bound run/verdict/log/trace records. Task dispatcher was stopped after the
last fully launched case exited 0; installed account/mod hashes were restored.
The parent process exits via a deliberate KeyboardInterrupt after case completion;
this is recorded as STOPPED_BY_USER, not a test failure or completed 100-case run.

Release v0.1.10 contains the proposer-authoritative plan relay, immediate votes,
separate Ready barrier and final checksum enforcement already covered by these
runs. Core DLL remains SHA256
93461e829142ff633366d1fab4ae26466a5ec29f6e42867bf298015bf031b907; the manifest
version changes only. All multiplayer participants must update together. Actual
cross-machine Steam execution is unverified. ENet retry latency is deferred.

## Retained-session candidate v0.1.11 (not published)

Current candidate SHA256: f927fc6a3d80d5311e090945aadf71a087f6a44c7c34de6eb7d9492f2f6a5f23. Successful undo keeps the same real native
service/transport and room ID. Native generation and receive checks are isolated
until all final hashes match; the Resumed/Release barrier rearms all receivers
before input. Additional native owners are disposed before run reconstruction.

Final candidate release-race3 PIDs 50017/50039/50060 passed two operations. The
late client's actual Release packet was delayed while a new live host action
arrived. Before the history-gating correction, PIDs 45877/45921/45941 failed on
missing history; afterward the action was recorded and the late peer's exported
prefix replayed successfully. Final candidate phases4 PIDs
50312/50331/50352/50373 passed four operations with busy last votes, held replay
completion and real stale checksum/divergence packet probes. Every peer reported
zero service replacements, connection opens/closes and native checksums generated
during replay. Final target/checksums and continued gameplay matched.

Additional earlier retained-source checks covered focused viewport UI, mixed
Defect/Silent 3-peer replay, and extended choices/hooks/resumes, enemy turn,
rejection and timeout. Build and 16-target MinimalCheck passed. Artifacts under
tools/MinimalHarness/results/2026-11-08/retained-session/. All test fixtures were
restored by hashes and no game remains.

Steam single-host attempt PID 39369 ended during initial Steam/mod startup when
the Steam client shut down; no replay ran and it is NOT acceptance evidence. User
confirmed the account is in use on another PC and explicitly asked deployment to
wait. Steam final verification and publication are pending. Existing public
release remains v0.1.10; v0.1.11 exists only as a local candidate package.

The user subsequently authorized publishing the ENet-verified v0.1.11 candidate
for direct testing. No additional Steam gameplay acceptance is claimed. Package
DLL SHA256 remains identical to the final retained-session test build above.

## UI-free model replay integration, 2026-11-09

User authorized moving the successful model-only prototype into production.
Replay and UI implement the change within the existing five modules. Native
run/combat UI is absent during recorded action playback and created once at the
end. No global TestMode, engine speed change or transport replacement is used.
Historical choices, native model effects and final checksum consensus remain.

Candidate DLL SHA256:
`efa9c8c914fe0401fc22eee5809ca7cce7a582b91f0e2b938427ca2e9b34ea30`.
Build and MinimalCheck pass (26 patch targets, 15 reflection surfaces). Focused
rendered tests cover repeat undo, real viewport input, generated Anger cards,
Primal Force hand transformation, map display, current orb bindings and native
Dualcast after undo. The former harness implementation adapters were removed;
the model helper now observes production execution rather than replacing it.

Final three-native-peer integration includes Toolbox choices/all four replay
event kinds/enemy turns, three content/timing-fuzz seeds with nine distinct
three-peer undo operations, and declared replay failure/cancellation cleanup.
All final scenarios reach terminal PASS. Successful operations report no state
divergences, session replacements or reconnects. Failure cleanup resets ModelOnly.

Final 500-action run PIDs 28364/28406/28427: exact same event bytes and target
native state as released v0.1.14 baseline; checksum 172059493 on all peers.
Maximum input-ready time is 1.024s versus 159.020s in the baseline. One measured
Finesse trace per variant, not an all-content performance guarantee. Per-peer
logs, hashes, counters, screenshots, before/after measurements, log diagnostic
classification and untested surfaces are recorded in
`tools/MinimalHarness/results/2026-11-09/model-native/report.md`.

Installed mod/default test accounts restored to original verified hashes after
all games exited; no harness/process/33771 listener remains. The original
installed v0.1.14 remains installed. No commit or publication was requested or
performed. Remote Steam/P2P and exhaustive content coverage remain unverified.

The user subsequently authorized releasing this implementation as v0.1.15.
The release ZIP and staged Workshop payload were checked against the exact
tested DLL SHA256 above. Only the external manifest version changed; packaging
did not change the DLL. Release notes retain the bounded ENet measurement and
Steam gameplay exclusions. The existing Workshop item and approved image are
preserved.

## Replay presentation cases, 2026-11-09

Game v0.111.0 / 41cef1ea. A Windows Steam client (PID 20248) began restoring
67 events. The first event replayed `Rupture` and failed in
`CardModel.PlayPowerCardFlyVfx → PileTypeExtensions.GetTargetPosition`.
The host acknowledged the client's cancellation, and the run disconnected.
The same log contains three successful single-player undos. Two replayed zero
events and the third replayed 11 events without `Rupture`. The v0.1.15 DLL
reproduced the same exception in a rendered single-player game (PID 19508).

| Case | Missing view dependency | Scoped change and runtime evidence |
| --- | --- | --- |
| `Rupture` and other power cards | The common fly effect reads the play container. | Skip `PlayPowerCardFlyVfx` during model replay. `Rupture` applied once per restore. Two restores each in rendered single-player (PID 19946) and ENet 3-player (20304/20356/20376). |
| `Spoils of Battle` / Forge | `PreviewSovereignBlade` accesses the local hand and the run's preview containers. `Spoils of Battle` was later in the reported Steam trace. | Skip the preview and keep native blade generation and damage updates. Rendered single-player reproduced the exception (24886), then restored a single 16-damage blade twice (26062). |
| `Bouncing Flask` | Its throw effect reads creature positions before applying poison. | Suppress the visual branch and keep target RNG and poison application. Rendered single-player reproduced the exception (25330), then restored nine total poison twice (26454). |
| `Hand of Greed` | Its gold effect looks up the enemy's position before the attack. | Suppress the position lookup. Rendered single-player reproduced the exception (25581), then restored damage against a surviving enemy twice (26785). The fatal gold reward branch still needs a runtime case. |
| Background music | Leaving the old run stopped and reset the active track. | Keep the live controller during active combat reconstruction. FMOD reported the same event playing in two single-player and two three-peer ENet restores. A separate cancellation run stopped the detached event. |

The broader candidate DLL has SHA-256
`58fa3e7091351031b42943f82a82c9aa6c2e8675450fa68ffe6d7a169a46a769`.
The first three-peer Forge fixture stopped before combat because the test
accounts lacked the `Spoils of Battle` unlock. The second used the unlocked
`Wrought in War`, which calls the same Forge helper. ENet peers
27851/27893/27912 restored a 17-damage blade twice, with checksum 2553020705
on all peers. Per peer and restore, `MODEL_UI_ATTACHED` recorded one skipped
Forge preview and six skipped one-shot sounds. The same connection was retained,
the peers continued into the next turn and all three exited 0. The installed
mod and test account hashes were restored, and port 33771 was free.

### Source findings pending runtime verification

| Path | What still needs a replay fixture |
| --- | --- |
| `Nightmare.OnPlay` | The local branch attaches effects to the current run view after resolving the card selection. Exercise the recorded selection and verify the selected card is still copied. |
| `Fabricator` bot summons and `ToughEgg` hatch positioning | Spawn callbacks read combat nodes to place the new creature. Verify spawn state and the restored room. |
| `SoulNexus` death and `InfestedPower` phase transition | Death or phase callbacks access creature animation or the run music controller. Verify the model effects and the resulting phase. |
| `SandpitPower` | Position and tween code is interleaved with power callbacks and removal-triggered kills. Verify the visual boundary, decrement and kill behavior. |
| Potion use and recorded choices | The shared potion wrapper can instantiate a throw effect while the room is absent. Attack and Colorless Potion use the native recorded-choice path in source. Verify consumption, selection and generated card state. |

The rendered regression harness uses `model-probe` and its
`--minimal-probe-power` or `--minimal-probe-visual=<CARD_ID>` game argument
through `tools/MinimalHarness/runtime.py`'s `run(..., extra_args=...)`.
Require the expected presentation bypass in `MODEL_UI_ATTACHED`, the exact
target state and a terminal verdict after continued play. The original Steam
incident is available as a text log. Its full 67-event run, real Steam
multiplayer on the candidate and persistent encounter visuals remain unverified.

## Mounted combat replay prototype, 2026-11-09

Game v0.111.0 / 41cef1ea. The prototype is selected with
`--undosync-mounted-replay`; ordinary launches use the model-only path.
Candidate SHA-256
`88184410529c207333052340e04251b6595c1ebd07f655216c525d6c1ce5f72d`.
Release build and MinimalCheck passed with 35 patch targets. The fixture
root is `/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-r90evv68`.

| Run | PIDs | Observed result |
| --- | --- | --- |
| `mounted-forge` | 38270 | Rendered single-player PASS. Two restores rebuilt the 16-damage blade and attached the native orbit node behind the overlay. The restored screenshots show the blade and no Battle Start banner. Continued through a native enemy turn. |
| `mounted-sandpit` | 38585 | Visual assertion FAIL. Replaying The Insatiable's first enemy turn and Frantic Escape completed with checksum 429179996 and the expected Sandpit amount. Player X after a 650 ms settle was 546.34 versus 541.67 before undo. An earlier build also differed at the same target. |
| `mounted-500` | 38881 / 38921 / 38938 | Three headless ENet processes PASS with a retained room, checksum 172059493 and continued native play. Pile tweens completed 1001 / 502 / 502 times. Maximum time to input release was 5.580 s. |

The default launch also passed two rendered Forge undos on the same DLL
(PID 39357). Both used the existing model-only UI attachment path.

The benchmark event bytes and target-state digest match both prior 500-action
builds. Maximum input-ready times were 159.020 s in v0.1.14 with UI and
ordinary tweens, 1.024 s in the v0.1.15 model-only path and 5.580 s in the
mounted prototype. The three prototype times were 5.510 / 5.580 / 5.575 s.
Optional fixture acceleration shortened live capture and was disabled on all
peers before voting and measurement. The fixture recorded real actions.

The mounted run reported more renderer resource allocations at exit than the
archived model-only run. It also used accelerated fixture capture, so the
shutdown logs alone do not separate preparation from replay allocations.
Exact boss position, custom boss music continuity, the full original incident
and real Steam multiplayer remain open. The fixture restored all installed
mod and test account hashes after exit and left UDP 33771 free.

### Mounted replay acceptance follow-up

The acceptance check compares the boss power, its visible native icon, the
native combat state and continued play. Exact screen coordinates are outside
this check. In rendered single-player PID 41889, The Insatiable's Sandpit
icon stayed bound to the restored power across two undos and the next enemy
turn completed. The captures also showed replay-only card previews briefly
covering the restored room.

Both 500-action resource runs used the same accelerated capture procedure and
a three-second settle on either side of the measured replay. Their input
events and target hashes matched the original benchmark. Same core DLL as
above. Source logs are in
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-myityoqu`.

| Run | PIDs | Input ready max | Host mesh allocations reported at exit | Host orphans before capture / before replay / after settle |
| --- | --- | --- | --- | --- |
| `model-resource-500` | 42299 / 42346 / 42365 | 0.885 s | 522 | 1013 / 17061 / 17079 |
| `mounted-resource-500` | 42616 / 42642 / 42672 | 5.596 s | 499 | 1013 / 17061 / 17061 |

Most orphan nodes were already present before rewind. Both modes emitted
similar headless renderer warnings at exit. After settling, node counts and
resource counts were similar across modes. The mounted host still had more
live objects (70232 versus 53930). These single-rewind runs leave long-session
object accumulation unresolved.

The follow-up preview guard uses the native no-preview result during mounted
replay. Candidate SHA-256
`326331193109e639cdbb337f9b2871d01b248c470f70391483cdee862092b147`.
Rendered boss PID 43401 passed two restores with the visible Sandpit icon,
six suppressed card previews per restore, and a subsequent native enemy turn.
The restored capture shows the unobstructed combat room. Rendered mixed
ENet PIDs 43815/43858/43863 then passed two 25-event restores with five recorded
choices each, enemy turns and checksum 2212431815 on every peer. The real
connection was retained. Build, metadata and fixture cleanup passed.
PID-bound logs, screenshots and verdicts are in
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-mpxlj8i6`.

### Mounted replay as the default

The default now mounts the native combat view. The diagnostic
`--undosync-model-only-replay` flag selects the prior path.

- In the previous mounted build, boss PID 47397 restarted The Insatiable's
  custom track on both undos. FMOD reported a different event instance and
  21/69 and 24/72 sampled frames with the original event inactive.
- The default candidate skips the duplicate startup call only when it reuses
  a controller from the same encounter track. Boss PID 48080 retained the
  same active FMOD event on both undos, with zero inactive samples and
  a moving playback timeline. The Sandpit icon and continued play passed.
- Default mounted ENet PIDs 48500/48540/48559 and model-only control PIDs
  49196/49233/49252 each completed five exact 500-action undos on one room.
  Maximum times were 5.672 s and 0.904 s respectively. Settled mounted host
  objects rose from 70303 to 72056 while resource, node and orphan counts
  stayed constant. The model-only host count fell from 53881 to 53704.
- A separate five-undo mounted test applied managed GC after attempts one
  and five. Host PID 49804 still rose from 54040 to 55709 after collection.
  Client increases were 42 and 41 objects. The initial trail-only mitigation
  reduced the maximum timing to 4.124 s but increased the host's collected
  count by 4042 across the same span. That build was held from release.

- The trail mitigation with explicit pile completion notifications and a
  frame yield every 32 events passed the rendered boss check (PID 52882)
  and five exact ENet 500-action undos (53413/53451/53453). The maximum
  input-ready time was 4.573 s. Host object count after managed GC still
  rose from 54561 to 56915 (+2354) between attempts one and five.
  Scene node and resource counts stayed flat. Client post-GC object
  counts fell. This candidate also failed the repeated resource check.
  Detailed logs and verdicts are under
  `/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-voywkxbo`.

The two trail mitigation variants were discarded from source. The mounted
default and same-track music guard remain in the working candidate. The
staged v0.1.16 package was held, and no Workshop upload ran.

The later v0.1.16 Workshop attempt staged the exact boss and five-undo PASS
DLL `3f4ce8876375ff31efe0fd064ea84e2279fc19c8cabf5140e0dd26f4b84df4cf`.
Steam reported `Logged In Elsewhere` at the first attempt, and both attempts
failed during existing item lookup. The package and approved image hashes
matched. The Workshop item received no upload.

The authorized retry published v0.1.16 to Steam Workshop item 3802124329
on 2026-11-10 at 00:02:42 KST. Steam reported new content manifest
1457304975765740686 and an OK upload result. The uploaded DLL matched the
verified build and used the existing approved preview. This confirms
publication. A fresh subscriber download and live Steam multiplayer
remain unverified.

### v0.1.16 post-release timing fuzz

The release package and every test process reported core SHA-256
`3f4ce8876375ff31efe0fd064ea84e2279fc19c8cabf5140e0dd26f4b84df4cf`.
Three headless ENet peers on STS2 v0.111.0 (41cef1ea) ran each
`MODEL_CONTENT_0..2` / `MODEL_TIMING_0..2` pair with mixed characters and
native viewport votes. Every peer proposed one undo per seed.

| Run | Host / client / client2 PID | Checksum at resume | Verdict |
| --- | --- | --- | --- |
| `v016-timing-fuzz3-0` | 72794 / 72818 / 72839 | 91014394 | PASS |
| `v016-timing-fuzz3-1` | 73437 / 73474 / 73498 | 1583777179 | PASS |
| `v016-timing-fuzz3-2` | 73697 / 73722 / 73746 | 2937397972 | PASS |

All nine undos exercised a busy last voter, host prepare before the held
action finished, one slow replay peer, a peer without a local action record,
and stale native checksum suppression. All peers logged `MOUNTED_UI_READY`,
matched the target state and the resume checksum, and retained the original
connection. Each seed completed a further native card play and ended with
matching peer state digests and zero divergences.

PID-bound logs and independent `summary.json` verdicts are under
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-p1h43jee`
and
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-1qov2voj`.
Both fixtures restored the local mod and test account files, exited every
process, and left UDP 33771 free. This matrix covers local three-peer ENet.
Four-peer timing fuzz and live Steam multiplayer remain unverified on this DLL.

### v0.1.16 100-seed ENet timing sweep

On 2026-11-10 the same release DLL completed 100 distinct resolved schedules
(`V016_CAMPAIGN_TIMING_000` through `099`) across the three mixed-character
fixtures above. Three native headless processes ran each case in Instant mode
with TestMode off. Each peer proposed one undo. All 300 undos returned to the
recorded target, matched the final native checksum on all three peers and
preserved the original connection. The four phase counters each reached 300.
All peers observed stale-checksum isolation on every undo, and each seed
completed further native card play. The sweep played 20 distinct card models.
The restored prefix was one card play in every case. Live card selections
occurred, while none were in the replay prefix. Mean passing case wall time
was 23.148 seconds, including process startup and fixture preparation.

There were 103 attempts for 100 seeds. Two seeds accounted for three failed
attempts. The original traces exactly matched the successful reruns up to
the point where the old tests aborted.

| Seed | Failed attempt PIDs (host / client / client2) | Cause and result |
| --- | --- | --- |
| `010` | 77949 / 77968 / 77989 | Before voting, the state barrier compared the acting peer after Survivor finished with remote peers still moving it from Play to Discard. Wait for the remote peers' native queues to settle while the acting peer remains held. The same seed then passed all three undos. |
| `013` | 78587 / 78625 / 78642 and 80066 / 80111 / 80130 | The test observer threw inside the replay completion task after Dualcast. Native `NOrbManager.EvokeOrbAnim` leaves the outgoing scene node until its 0.25-second fade callback. Check the settled orb view after protocol release and before the next action. The same seed then passed all three undos with matching orb views. |

The harness built with zero warnings or errors. The release DLL SHA-256
remained `3f4ce8876375ff31efe0fd064ea84e2279fc19c8cabf5140e0dd26f4b84df4cf`.
All three fixtures restored the original installed files, exited all test
processes and freed UDP 33771. Per-attempt PIDs, traces and terminal logs are
indexed by `campaign.json` in the `undosync-extended-acivg820`,
`undosync-extended-5avihvid` and `undosync-extended-vn5hr0gc` directories under
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/`. The last directory also
contains `audit.json`, the aggregate across all three fixtures. The sweep
covered three-peer local ENet. Four-peer timing and live Steam multiplayer
remain outside this run.

### Hide individual End Turn entries in the picker

The picker omits ranges described as EndPlayerTurnAction, including cancellation
of that action. It computes each range before filtering so the remaining card
tiles keep their original target event counts. Native history and replay events
continue through the same code paths.

On 2026-11-11, rendered single-player before/after runs on v0.111.0 / 41cef1ea
played a card, advanced the enemy turn, played again, and selected the original
card target through viewport input. Baseline PID 77472 showed targets 3, 1, 0,
with target 1 labeled End Turn. Candidate PID 78892 showed only card targets 3
and 0. Both restored the exact opening state and continued play. Screenshots
were inspected. The first baseline attempt (PID 75751) clicked the oldest tile
outside the scroll viewport and failed before restoration. The driver now
scrolls the selected tile into view before clicking it.

The candidate DLL SHA-256 is
`e1ed7090680965385de6a5e33e63d748fcd506b05311e3b700d1b99224a782db`.
A subsequent rendered ENet integration used PIDs 80001 / 80134 / 80191 and
seed REPLAY_SPEED_A with mixed characters. The host and client2 each selected
a visible picker target and restored the same 25-event prefix containing the
enemy-turn transition and recorded choices. Both undos reached checksum
668155198 on every peer, with zero divergences and subsequent native card
play. Release builds, MinimalCheck (36 patch targets, 15 reflection surfaces)
and whitespace checks passed. Each fixture restored installed mod/account
hashes and left no game process or UDP 33771 listener.

PID-bound logs, screenshots and terminal PASS summaries are in
`undosync-extended-3wkp5owx`, `undosync-extended-br557t6t` and
`undosync-extended-69ed3tx0` under
`/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/`.

### v0.1.17 saved for a later release

On 2026-11-12 the manifest was advanced to 0.1.17 and the local package
`dist/UndoSync-v0.1.17.zip` was prepared. Its DLL SHA-256 matches the
rendered picker and three-peer integration candidate above. Release builds,
MinimalCheck and package-content/hash checks passed. The version is saved
with local tag `UndoSync-v0.1.17` for the user's later publication.
