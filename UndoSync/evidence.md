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
