# UndoSync verification implementation plan

Status: execution authorized, 2026-11-08. M1-M3 passed; M4 Steam execution is resource-blocked. The user confirmed
one PC and one Steam account. Steam execution requires additional independently
authenticated nodes; it cannot be accepted using the current single account.

## Purpose

Make failures reproducible before a user discovers them: restore content fuzzing,
add phase/timing fuzzing, and run the same scenarios through actual native Steam
connections. Keep the five production modules and current proposer-authority
contract. This plan changes test infrastructure, not the undo algorithm.

## Context and orientation

Workspace: /Users/kimbeomsu/.codex/worktrees/undosync-minimal/sts2-my-mod.
Production: UndoSync/{Hook,History,Replay,Network,UI}. Existing pending edits and
untracked assets belong to preceding tasks and must be preserved.

- tools/MinimalHarness/Harness.cs boots real games, drives native actions/UI
  callbacks and emits PID-bound verdicts. runtime.py launches local macOS ENet
  peers with --force-steam off and synthetic client IDs. Mark/Barrier are local
  files; they cannot coordinate independent machines unchanged.
- tools/LegacyFuzz/{UndoFuzz.cs,MpFuzz.cs} are archived, excluded from compilation
  and tied to removed core APIs. Port only audited generation/driver logic, not
  their TestMode/snapshot/checksum substitutes or private-state mutation.
- Verified installed native source: v0.111.0 / 41cef1ea. NetHostGameService has
  StartSteamHost and GetRawLobbyIdentifier; the existing production Replay uses
  SteamClientConnectionInitializer.FromPlayer and JoinFlow.Begin. SteamHost.NetId
  comes from SteamUser.GetSteamID. Re-audit installed versions before execution.
- Existing tests demonstrate one discarded-vote failure and proposer-record
  repair, but are neither broad randomized coverage nor a Steam validation lane.

## Architecture hazard scan

| Hazard | Owner | Required handling / stop condition |
| --- | --- | --- |
| Tests call internals instead of human paths | Harness/FuzzDriver | Proposals use Left-Arrow/picker input and votes use native buttons. Callback-level and viewport-input coverage are labeled separately. No direct Network.Vote in the release regression lane. |
| Idle gating hides vote timing | FuzzDriver | Only proposer target selection requires an eligible point. Never wait for voter idle before Accept. Count acceptance received while that voter is still busy. |
| Invalid catalog combinations masquerade as bugs | FuzzCases | Use audited game factories, character/unlock pools and valid act encounter pools. Label recorder/latency faults explicitly; fixture failure is not a game defect or passing fuzz case. |
| Same seed is not a reproducible schedule | FuzzDriver | Separate content seed and schedule seed; record resolved fixtures, selected actions/choices, phase triggers and delays. Repro consumes the saved trace rather than resampling. |
| Test control channel replaces gameplay | TestControl/coordinator | Only schedule/barrier/status/artifact traffic. Votes, record transfer, replay-start, Done/checksum and reconnect must go through production/native Steam. |
| Steam silently falls back to ENet | Harness/worker | Steam lane must observe PlatformType.Steam, distinct real Steam IDs and native lobby/connection markers, including replacement services. Otherwise fail infrastructure preflight. |
| Remote launch uses wrong OS session | worker | Start worker from an already logged-in graphical user session with Steam running; do not assume an SSH/service-session launch can reach that Steam session. |
| Packaged build differs from tested build | runner/verifier | Record and compare DLL/manifest/harness hashes before launch and again in the report. Evidence is transport- and build-specific. |
| Quiet or incomplete case appears green | verifier | PASS requires proposal, votes, actual replay, post-replay action and terminal verdict from every peer. Missing mechanism coverage is UNCOVERED, not PASS. |
| User games/settings damaged | worker/runtime | Preflight, backup/hash verification, task-owned PIDs only, bounded waits, classify stalls, restore after every process exits. One ENet job on 33771. |

## Non-negotiables and scope

No fake Steam IDs, transport stubs, private production-state mutation, partial
production classes, checksum replacement, or forced success. Do not remove the
post-replay checksum guard. Keep TestMode off for native multiplayer acceptance;
do not import the legacy synthetic checksum workaround. No production architecture refactor or new core
module. No credentials automation, account creation, purchases, releases or
Workshop/image updates are authorized by this plan.

Generation is seeded coverage of factory-valid loadouts, not a claim that every
synthetic combination was reached by a complete ordinary campaign. Persist the
construction path. A failed fixture acquisition fails setup; never swallow it
and claim the requested content was exercised.

## Files and ownership

| Path | Concrete responsibility |
| --- | --- |
| tools/MinimalHarness/Harness.cs | Existing lifecycle plus explicit transport selection, fixture preparation hooks and calls into the fuzz driver. Expose only small declared test helpers; no partial/private production sharing. |
| tools/MinimalHarness/FuzzCases.cs (new) | Native factory-backed loadout/encounter generation and a serializable resolved case specification. |
| tools/MinimalHarness/FuzzDriver.cs (new) | Normal gameplay driver, seeded phase schedules, UI requests/votes, target-state oracle and trace/repro. |
| tools/MinimalHarness/TestControl.cs (new) | Existing local-file coordination or explicit HTTP test-control client. Neither backend implements game traffic. |
| tools/MinimalHarness/fuzz.py (new) | Seed sweeps, batch limits, reproduction selection and artifact collection, reusing runtime.py locally. |
| tools/MinimalHarness/coordinator.py (new) | Small private-test-network HTTP controller for run IDs, artifacts, phase barriers and results. No publicly exposed service. |
| tools/MinimalHarness/worker.py (new) | Runs inside each Steam desktop session; downloads/verifies one test bundle, preserves installation, launches/monitors its own game, uploads artifacts and restores prior files. |
| tools/MinimalHarness/verify_run.py (new) | One verdict implementation consumed by local and Steam runners; emits machine-readable summary and failed coverage. |
| tools/MinimalHarness/runtime.py | Preserve local ENet lane; reject contradictory Steam/offline/synthetic-ID options. |

Avoid a new generic framework. These are test-only helpers in the existing test
assembly and small runner scripts. Execute sequentially; do not run concurrent
jobs against the same nodes or ENet port.

## Exposure matrix

| Surface | Exposure | Gate |
| --- | --- | --- |
| Harness helpers/fuzz modes | Test DLL with explicit role/suite flags only | Normal game launch stays dormant; no test mode in shipped mod payload. |
| Steam workers/control endpoint | Explicitly enrolled desktop sessions/private test network | Three distinct authenticated peers and build checks before acceptance. |
| Release verifier | Explicit local command consuming retained reports | Missing lane evidence stays missing; this command never publishes. |

## Milestones and gates

### M1: Make regressions and verdicts transport-independent

Work: add TestControl.cs and verify_run.py; replace local Mark/Barrier I/O behind
that test helper. Keep the current proposer-record and delayed-voter scenarios.
Add native viewport-input coverage for opening the picker/selecting a target and
accept/reject, in addition to callback-level probes. No production private calls.

Evidence: build Release with warnings as errors, MinimalCheck, then a focused UI
run and a separate full three-process ENet run. Save input events, native button
callback markers, operation IDs, before/target/after states and all peer exits.
Verifier must reject a captured no-op/empty replay report and a missing-peer report.
This unblocks both fuzzing and distributed control; it does not prove Steam.

### M2: Restore content fuzzing with reproducible traces

Work: port audited portions of SetUpRandomLoadoutAsync and ResolveEncounterPool
into FuzzCases.cs. Inspect installed factory APIs and call sites first. Generate
relics through RelicFactory, potions through PotionFactory, reward cards through
CardFactory/room-appropriate options and upgrades through native upgrade rules.
Use actual act encounter pools and normal run/load-lobby lifecycle.

The resolved fixture must be the same across peers. Use the existing native
serialized-run/lobby path for sharing generated run data where practical; audit
setup order before wiring factories into BootAsync. Do not independently roll
loadouts per peer. Setup consistency failure is FIXTURE_ERROR, not an undo bug.

FuzzDriver records an eligible proposer's complete target state, plays generated
legal actions through the game's native sender/UI paths, requests undo, verifies
full target state and cross-peer checksum equality, then plays again. A case
without a completed undo and post-undo action is not a successful undo fuzz case.
Persist fixture.json, trace.jsonl, target/replay digests, exceptions and counters.

Evidence: deterministic known seeds twice with identical resolved fixture/action
trace, a saved failing trace replay, plus initial 2/3/4-peer ENet sweeps. Report
actual covered model/action/event counts, not just requested seed count. M3 may
start after deterministic reproduction works; do not run a huge sweep first.

### M3: Fuzz phase order rather than sleep randomly

Work: FuzzDriver schedules per-peer actions from observed local phases such as
card queued/executing, choice pending, proposal visible, vote submitted, Ready
pending and replay executing. Rotate proposer and last voter. Add bounded
native-task holds (as in the current real-action fixture), varied acceptance
order, replay-speed skew and explicit recorder-absence fault cases. Sample
wait durations separately from content. No fake vote/checksum delivery.

For each scheduled condition require an observed marker: e.g. last vote received
before its sender's held action completes, then replay starts only after Ready.
If a trigger was never reached, emit UNCOVERED and exclude it from pass counts.
A captured trace includes the resolved phase triggers so reruns are interpretable.

Evidence: known pre-fix discarded-vote case fails on the captured baseline;
current code passes it through native UI. Run independent content and schedule
seeds. Use a baseline-compatible test build; a missing API or launch failure is
an infrastructure error, not the expected discarded-vote failure. Automatic
minimization is a later optimization; initial replay must
reproduce the full saved trace before any shrinking is trusted.

### M4: Add a real Steam lane using the same scenarios

Work: Harness.BootAsync branches on --minimal-transport=steam. Host uses native
StartSteamHost; clients use native JoinFlow and SteamClientConnectionInitializer
with the real host identity. Do not pass --force-steam off or synthetic clientId.
Publish host Steam ID/lobby identifier to test control; client workers start
only after the real host listening marker. Production replay still creates its
own replacement Steam services and reconnects through the existing native path.

Run coordinator.py on the private test network. Each enrolled worker starts in
its real Steam desktop session and has explicit game/mod/settings paths in a
local config. The coordinator distributes identical build/scenario bundles and
collects status/logs; it never accepts or restores on behalf of a game peer.

Prerequisite: at least three separately authenticated Steam execution
environments/accounts with access to the game and a working native join path.
Physical machines are straightforward; any VM must first demonstrate actual
Steam/game/graphics operation. Do not promise three clients under one account.
Account login/Steam Guard remains user-controlled. No enrollment exists yet.

Evidence gates: first real two-peer Steam smoke, then three-peer known-failure
regressions, proposer rotation and fuzz batches. Require Steam platform/identity
and native connection markers before and after reconstruction. Same-LAN Steam
is labeled as such; it does not establish separate-network/relay coverage.
A three-peer report requires three real peer verdicts, never just host success.

### M5: Make release evidence consumable by packaging

Work: verify_run.py emits build hashes, transport, game version, case/seed,
participant IDs/PIDs, mechanism counters, states/checksums, stdout/artifact paths
and explicit PASS/FAIL/INFRA_ERROR/UNCOVERED. Runner returns nonzero on required
failed/incomplete cases. Add a tools/MinimalHarness/check_release.py entry point
that verifies the requested matrix against the exact package DLL/manifest hashes.
Packaging can run independently, but must not be labeled Steam-verified when a
required Steam report is absent. Publishing remains a separately authorized step.

Evidence: intentionally missing Steam evidence and a mismatched package hash
both fail the release check; captured passing reports from the exact build pass.
Never decide readiness from a quiet log or number of seeds alone.

## Validation and acceptance

Commands below are proposed interfaces, NOT currently implemented commands:

    python3 tools/MinimalHarness/fuzz.py run --transport enet --peers 3 --cases 20 --content-seed LOADOUT-A --schedule-seed TIMING-A
    python3 tools/MinimalHarness/fuzz.py repro --trace <artifact-directory>/trace.jsonl
    python3 tools/MinimalHarness/worker.py --config <local-node-config.json>
    python3 tools/MinimalHarness/coordinator.py run --config <test-nodes.json> --transport steam --peers 3 --suite regression
    python3 tools/MinimalHarness/check_release.py --package <release.zip> --reports <artifact-directory>

Start with a small deterministic corpus and explicit required timing cells,
then expand seeds after reproduction and coverage accounting pass. A timeout
needs current phase/queue/connection evidence and classification, not an
automatic retry that silently replaces failure with success. Every failed case
is retained; only reproduce or rerun with a distinct run ID.

Artifacts: results/<run-id>/{manifest.json,summary.json,fixture.json,trace.jsonl,
<role>/game.log,<role>/mod.log,<role>/screenshots/,<role>/verdict.json,cleanup.json}.
The manifest maps logical role to real Steam ID, PID, game version, transport,
build hashes and content/schedule seed. Summaries separately report content,
protocol timing, native UI input and Steam transport coverage.

## No-go conditions

Do not claim a lane passed if a target mechanism never executed, peers were
missing, Steam fell back to another transport, build hashes differ, fixture
provenance is unresolved, a real user game exists, or terminal artifacts/logs
contradict the result. Do not import retired production abstractions to make
old fuzz files compile. Unexpected native errors require classification and
before/after comparison, not blanket suppression.

## Re-entry protocol

Read this plan, AGENTS.md, current architecture/evidence, git status and the last
run manifest. Recheck installed game version and selected native API call sites.
Resume the first unpassed milestone. Do not repeat an accepted run without a
code change, failure or unresolved concern. Before launching inspect processes,
ports, Steam/desktop state and fixture backup; restore and verify afterward.

## Progress and decision log

- [x] Audited current harness, runner and archived fuzz entry points.
- [x] Recorded this proposed contract; no implementation or Steam enrollment.
- [x] M1 common control/verdicts and native input regression.
- [x] M2 factory-backed content fuzzing and deterministic repro.
- [x] M3 phase/timing fuzzing and coverage accounting.
- [ ] M4 enrolled Steam workers and real transport runs.
- [ ] M5 exact-build release evidence check.

Decisions: keep production unchanged; extend one test assembly; reuse generators
only after native source audit; retain fast ENet and real Steam lanes as separate
evidence; use desktop-session workers rather than assuming remote service launch
can access Steam. Separate test control from game traffic. No delegation or
parallel runtime jobs are required.

Steam account/lobby limitation source:
https://partner.steamgames.com/doc/features/multiplayer/matchmaking
Native transport design source: installed SteamHost/SteamClient and existing
UndoSync/Replay/Replay.cs. Steam enrollment/OS capabilities are unverified inputs,
not assumptions that may be reported as passing execution.

## Execution evidence and discoveries

M1: added TestControl (atomic local markers, explicit async HTTP client contract),
structured peer verdicts and verify_run.py. Actual viewport key/mouse events now
open the picker, click its native tile and click native vote buttons; this lane
does not call Network.Request/Vote or emit Released directly. Node transforms
provide coordinates. Native callback observers confirm the input reached targets.

Build with warnings as errors and MinimalCheck passed. Focused viewport-ui-02
PID 79158 passed with one undo, two viewport inputs and one native callback.
Separate viewport-regression3 PIDs 80141/80187/80206 all passed: three restored
targets each, four viewport inputs and three native callbacks per peer, matching
final states/checksums and post-replay input. Evidence verifier accepted both.
Captured reports with no replay, no viewport input, a wrong build hash, missing
peer or wrong requested transport were rejected as UNCOVERED/INFRA_ERROR.
Evidence: results/2026-11-08/testing-upgrade/m1/ and raw fixture
/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-5gslat5d.
Installed mods/accounts were restored with file hash checks; no game remains.

Discovery: first focused launch loaded MinimalHarness before UndoSync and failed
initializer JIT resolution. No test ran; it was classified INFRA_ERROR. The native
ModManifest dependency schema was audited and the test manifest now explicitly
depends on UndoSync. The runner detects initializer failures without waiting for
the entire scenario deadline. This is a test packaging fix, not a core change.
HTTP coordination is implemented but distributed acceptance remains an M4 gate.

M2 implementation detail (minor): factories run before native combat header,
after Launch/NRun/SetActInternal so acquisition hooks/UI have valid owners. Host
alone resolves counts and upgrade slots. Every replica executes the same native
factory step to advance its replicated RNG/grab bags, checks the host-resolved
model ID before acquisition, and fails FIXTURE_ERROR on any discrepancy. This
retains native AfterObtained/procure/add/upgrade effects without independent peer
loadout selection or an extra reconstructed-lobby lifecycle. The native seeded
Monster map/encounter sequence supplies encounter variation; Elite/Boss coverage
is not claimed in this initial corpus. Content and schedule seeds are separate.

M2 initial case CONTENT-A/SCHEDULE-A (2 peers), PIDs 90455/90484: two viewport
undos, nonempty replay prefix, exact target comparisons, native checksums and
continued action passed. Repeat/repro and wider peer counts are still pending.
Native cleanup logs the existing CombatStateTracker/LocalContext.GetMe error
while Reset removes powers; the identical stack exists in the M1 captured runs.
It does not abort replay or prevent matching final states. This preexisting
teardown error remains recorded, not suppressed or classified as a new fixture
failure. Godot headless RID/resource exit warnings are retained in raw logs.

M2 deterministic gate: CONTENT-A/SCHEDULE-A 2-peer repeats PIDs
90455/90484 and 90784/90827 had byte-identical content-fixture.json,
fixture-resolved.json and both per-peer trace files. Saved-trace run
PIDs 91327/91367 passed using persisted decisions without resampling. Cleanup
hashes verified after both batches. These satisfy the deterministic success
reproduction prerequisite; explicit failing-trace and 3/4-peer cells remain.

Static tooling discovery: tools/SurfaceCheck still requires removed
StateSnapshot.cs/CopySkip and exits 2 before its legacy ledger checks. It is not
a passing minimal-core check. The applicable tools/MinimalCheck passed all five
modules, 12 native Harmony targets and 14 reflection surfaces; Release build has
zero warnings/errors. No obsolete snapshot code or ledger is imported to make
the old checker green.

User steering, 2026-11-08: prioritize fast CLI verification; real-time gameplay
speed is too slow for bulk fuzzing. Audited PrefsSave.FastMode public setter and
Cmd.Wait/CustomScaledWait native call paths. Fuzz defaults to FastModeType.Instant,
which natively skips gameplay animation waits, while TestMode remains OFF.
Normal speed remains an explicit separate regression option. Record speed and
wall-clock duration; do not imply headless alone accelerates simulation. This is
a test-speed implementation choice within M2, not altered production behavior.

3/4-peer content cells passed: CONTENT-B/SCHEDULE-B PIDs 91835/91857/91881
(three undos, nine nonempty replay executions), CONTENT-C/SCHEDULE-C PIDs
92277/92317/92322/92344 (four undos, sixteen replay executions, one live Survivor
choice captured and replayed). Actual played models and factory resolutions are
in each summary.json, not inferred from requested seeds. Fixture root:
/var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-s4y0ed7i.

Speed profiling: identical CONTENT-B/SCHEDULE-B three-peer runs measured
Instant 59.790s vs Normal 45.735s. File-marker timings isolate ~14s vs ~7s per
undo. Instant client logs show one native handshake timeout and
RESTORE_JOIN_RETRY per reconstruction; Normal succeeded first attempt. Animation
wait elimination alone does not make total multiplayer execution faster. Do not
hide this reconnect race with a test-control join gate. Add a clearly labeled
single-process native Instant content lane for bulk fixture/action exploration;
retain actual 2/3/4-peer ENet phase regressions separately. This responds to the
user's fast-CLI requirement without calling singleplayer multiplayer evidence.

M2 gate passed. An explicitly corrupted saved action trace failed with
TRACE_ERROR actual hand matches resolved action (PIDs 94507/94531), then replay
of that captured failing run failed at the same boundary (PIDs 95634/95670).
This is a declared repro-input fault, not an alleged production/game defect.
Both runs returned nonzero and restored fixture hashes. Single-process native
Instant content run PID 95521 passed in 11.561s, with real viewport undo,
nonempty event replay, exact state restoration and continued play. An initial
singleplayer helper incorrectly checked RunLobby, was classified a harness
NullReference failure (PID 95235), and was narrowly corrected to check connected
rosters only for multiplayer. Artifacts remain separate; no failed run replaced.
M3 may now start. Fast bulk lane defaults to one native process; peers=2/3/4
remain explicit actual ENet lanes. Actual Steam is still unverified.

M3 implementation/evidence: native PlayCardAction tasks are held by phase markers,
not eight-second sleeps. A recorded schedule chooses last voter, vote order,
0..6 frame acceptance delays, slow replay peer and explicit absent recorder.
The host releases the live task only after actual Preparing; the replay hold
releases only after every other peer reports its native Done wait. Coverage
counters must equal proposer count or the verifier returns UNCOVERED.

CONTENT-B/PHASE-A 3-peer PIDs 96901/96942/96946 passed all three rounds with
busy votes, Preparing before drain, replay speed skew and recorder absence.
CONTENT-C/PHASE-B 4-peer PIDs 97821/97840/97859/97880 passed all four rounds:
each phase counter totaled four; final states/checksums and continued action
matched. Duration 79.126s, including native reconnect retries. No Steam claim.

Baseline comparison: a separately built temporary copy reintroduced only the
exact `if (accepted && !Hooks.IsIdle()) return;` Vote condition from 80e255d.
It preserves current harness APIs and isolates the discarded-vote bug; it is
not the complete old release. PIDs 97647/97685/97704 failed immediately after
native viewport callback when LocalVoted stayed false. Current 3/4-peer builds
passed that same condition. Main production sources/DLL hash remain unchanged.
Evidence/provenance: results/2026-11-08/testing-upgrade/m3/.

Runner discovery: a parallel metadata-only MinimalCheck command contained the
game data path, so broad pgrep output falsely blocked one preflight before any
deployment/game launch. Runtime still runs pgrep first but now confirms the
actual executable name (or caffeinate wrapping that executable) before treating
it as a game. It does not ignore real game instances. Sequential repro resumed.

M3 saved-schedule repro PIDs 99403/99444/99448 passed all three recorded rounds
and consumed the entire trace, with matching target/final state and zero native
divergences. All processes exited 0; installed mod/account hashes restored.
Release build (warnings as errors), MinimalCheck and git diff --check passed.

Remaining: M4 worker/coordinator implementation/enrollment and real Steam
acceptance, then M5 exact-package release gate. The user's one PC/one Steam
account cannot meet M4's distinct authenticated-peer prerequisite; no real
Steam test, worker enrollment, release claim, commit or publication occurred.
The current work is complete through local M3, not the full Steam plan.

## Current authorization: multiplayer timing sweep

The user removed M5/release-gate automation from scope, deferred ENet reconnect
latency optimization, and deferred Steam work. Do not implement those while
executing this sweep. Content fuzz volume is also dropped. The approved task is
50 real 3-peer ENet cases plus 50 real 4-peer cases: fixed content per roster,
independent timing seeds, every proposer per case (350 undo operations total).
Require target-state equality, native all-peer checksum/Resume, continued input,
terminal verdicts, observed busy-vote/Ready/skew counters and cleanup. Preserve
and reproduce a failing trace before deciding its cause; no retry-to-green.
The existing phase driver supplies real held native tasks, viewport proposal/
votes, varied accept order/frame delay, slow-peer Done barriers and explicit
recorder absence. Do not call this Steam, arbitrary packet latency/loss, or
complete protocol-state-space coverage. Keep production code unchanged unless
a separately established bug/fix scope is authorized.

Minor runner correction: --phases --cases now retains the content seed and
varies only the schedule seed. Previously both seed strings received a suffix.
All case manifests still retain explicit content/schedule values.

The user stopped the 100-case sweep after current case mp3-008 and explicitly
requested deployment. Final sweep result is STOPPED_BY_USER: nine passing 3-peer
cases, 27 operations, zero failed cases, cleanup hashes restored. No new 4-peer
sweep began. Release-gate automation stays removed; Steam test-environment work
and reconnect optimization stay deferred. Release publication is now authorized
by this later user instruction, superseding this plan's earlier no-release scope.
