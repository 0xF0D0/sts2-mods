# Production UI-free replay integration

Implemented in the existing Replay and UI modules; the five-module structure is
unchanged. Production reconstructs and replays without NRun/NCombatRoom, then
creates and binds one native combat view. Native actions, RNG, damage, pile hooks,
recorded choices, retained transport, final checksum consensus and release remain
in production. The harness supplies fixtures, inputs, declared timing faults and
read-only observations; its former model/UI/TestMode replacement adapters have
been removed.

Native authority: installed STS2 v0.111.0 / 41cef1ea, main assembly hash 1172974615.
Fresh source audit: /tmp/undosync-model-replay-audit. Candidate UndoSync SHA256:
`efa9c8c914fe0401fc22eee5809ca7cce7a582b91f0e2b938427ca2e9b34ea30`.

## Checks

Release build: PASS, zero warnings/errors. MinimalCheck: PASS, exactly five
modules, 26 native Harmony targets, injected parameters and 15 reflection surfaces.
See static-check.txt. git diff --check passed. Runtime acceptance is separate.

## Runtime matrix

All listed runs reached terminal PASS and exited. Each directory retains
run.json (version, hashes, arguments, PIDs), PID logs, terminal logs and verdicts.
Final successful multiplayer runs also have independent summary.json verification.
The committed evidence includes PID-bound harness logs, run/observer/verdict JSON,
summaries and comparisons. Original verbose native terminal logs, raw snapshots
and inspected PNGs remain in the local run folders; they are not release assets.
Focused UI candidates were validated before integration; hashes for each stage
are explicit in run.json. Final-* and generated/transform runs use the final DLL.

| Run | PIDs | Observed mechanism |
| --- | --- | --- |
| focused-ui | 15791 | Rendered native hand, HP, intents, end-turn; two undos and continued play/actual End Turn. |
| focused-map-orbs | 19397 | Native Zap -> two lightning orbs, two undos, slot/model identity checks, actual Dualcast after undo, enemy turn, map button/open/close. PNGs inspected. |
| focused-generated | 24580 | Native merchant Anger; replay executes damage, generates a duplicate and suppresses its preview (counter=1 per undo); exact state and hand/piles, continued play/map/end-turn. PNG inspected. |
| focused-transform | 25924 | Native merchant Primal Force transforms a starter Strike into Giant Rock in hand. Native transform visual guard executes (counter=1 per undo), target and rendered hand agree. Two undos, continued play/map/end-turn. PNG inspected. |
| final-mixed3 | 26946 / 26987 / 27010 | Mixed characters, Toolbox native choices, all four event kinds, enemy turn, two undos, native continued client attack and unchanged room/transport. |
| final-timing-fuzz3-0 | 27308 / 27353 / 27376 | Native factory loadout + declared live/replay holds, delayed votes, missing recorder, stale native checksum probe; each peer proposes an undo. |
| final-timing-fuzz3-1 | 27574 / 27616 / 27622 | Independent content/schedule seed; same timing/recorder/checksum mechanisms. |
| final-timing-fuzz3-2 | 27831 / 27875 / 27896 | Third content/schedule seed; same timing/recorder/checksum mechanisms. |
| final-failure3 | 28094 / 28136 / 28160 | Declared effect failure on client2 while host waits for Done; progress identifies stalled peer, cancellation propagates, all peers return to menu and ModelOnly resets. |
| final-500 | 28364 / 28406 / 28427 | Exactly 500 native actions per peer without combat UI; one native UI attachment, checksum consensus, retained room and continued native host play. |

Fuzz scope: **3 seeds, 9 distinct three-peer undo operations**, not a 100-run
campaign. Aggregated injected-mechanism coverage: {"busy_votes": 9, "preparing_before_release": 9, "skew_holds": 9, "recorder_faults": 9}.
All successful final multiplayer runs report zero native divergences, session
replacements, new connections or closed connections. The failure-injection case
intentionally ends the session and is accepted only for its expected failure path.

## Actual 500-action comparison

Same native factory Finesse fixture and seed as the released v0.1.14 baseline.
Event bytes and target native-state SHA256 are identical on all six participants.
One measured run per version, three real local headless ENet processes each.
Normal preference before voting and after restore. Times exclude preparation and
voting and include owner rebuild, playback, late UI, consensus and input release.

| Role | Baseline v0.1.14 input-ready seconds | Candidate core seconds | Candidate input-ready seconds |
| --- | ---: | ---: | ---: |
| host | 158.960 | 0.657 | 0.959 |
| client | 159.020 | 0.771 | 1.024 |
| client2 | 159.016 | 0.754 | 1.024 |

Max input-ready: **159.020s -> 1.024s**, **155.3x** for this trace.
Final checksum on every peer: 172059493. Every observer reports
500 actual UI-free actions with TestMode off and one completed attachment.
The final host's native visual counters include 1,504 pile-helper calls and
500 manual-card-node calls, proving the presentation gates executed.
Event SHA256: `2DD190AF5686039ED6A70C60516D49E37CB8B1B418CB88C3D29C9F46CD6D86BB`.
Target SHA256: `986CC0861F182E414DCC80FB2498AB0304F7612C818C7C6C777501B6C628AF0D`.
This is a single bounded Finesse workload, not a general performance guarantee.

## Source and lifecycle review

- Replay.ModelOnly is bounded to owner setup/model playback and reset by finally
  and Clear on failures. Old UI is torn down against its original owners first.
- Pile presentation skips retain native silent-move notifications in their exact
  order. Native Add/Shuffle, gameplay RNG and AfterCardChangedPiles remain intact.
- PreviewInternal uses the native supported null result. Transform's single
  IsMine(CardModel) call is a presentation-only loop guard in its async MoveNext;
  a bounded transpiler substitutes a replay predicate there only. Native model
  transform/pile order/hooks/removal still execute. Patch installation requires
  exactly one matching call. Live play calls the original ownership predicate.
- Late UI initializes Activate/background/hand/end-turn before active-screen
  update. Native orb slots bind the actual current queue models in order; actual
  channel/evoke behavior was exercised. The new map is initialized from the
  already generated map and is usable from the native top bar.
- No global CombatSetUp/TurnStarted event is raised and SetUpCombat is not called
  twice. Native checksums before/after UI binding must agree; peer Done consensus
  and native rearm are unchanged. Engine time scale and TestMode are untouched.
- New reflection/scene paths were verified against installed native source and
  checked in runtime: OnTurnStarted, Transform's compiler state machine,
  PreviewInternal, ClearScreens, CreateCardNodeAndUpdateVisuals, %Orbs and map nodes.

## Log classification and limits

Logs are not error-free. Native cleanup emits Local player not found in combat
from CombatStateTracker during discarded-creature removal; the same message is
present in the original v0.1.14 baseline. It precedes replacement owners and is
not an action exception or a failed restore in these accepted cases. No claim
that this older diagnostic was fixed is made.

Native NScreenShake.Shake/Rumble logs Missing screenShake target when the combat
UI is intentionally absent. Fresh source confirms that branch only logs and
omits the camera effect; it does not throw or change combat. These are retained
known presentation diagnostics, not silently counted as an error-free run.
Injected native effect failures appear only in the declared failure scenario.

Coverage is bounded to the listed cards, loadouts, valid native first-act
encounters, choices/turns and timing scenarios. Not every card/relic/potion,
encounter, death/reward transition, other mod, or remote Steam/P2P path is covered.
NetFullCombatState is the native projection used for hashing, not a serialization
of every private runtime field. No independent background context/atomic swap or
complete checkpoint importer was added.

## Cleanup and publication

All game processes exited before restoration. Original local installed mod and
default test accounts were restored and file hashes verified; temporary new test
accounts and MinimalHarness were removed. UDP 33771 is free. cleanup.json records
the checks; installed DLL remains original v0.1.14 SHA256 b334773a... .

No commit, GitHub release, Steam upload, or unrelated AGENTS.md edit was made.
