## 2026-11-23: Ecstasy turn-start replay ordering and native card binding

**Final result: PASS for four selected final-build runs, ten native processes,
and ten distinct Undo sessions.** Installed game v0.113.1 / d09d48e5. Tested final
DLL SHA-256: `a764b6ae018c8a35f8229390cd966ff6b4223b29e6844796b22aa0252e4afc5a`.
This report records pre-release validation. v0.1.20 publication is tracked separately
in `dist/UndoSync-v0.1.20-release-receipt.json`; its package retains this exact tested DLL.

The user's PID 79770 failed twice during singleplayer Fool/Aeonglass restoration.
Both logs show future turn-end records executing while still on turn 2, then a
missing combat-card ID 30. Incident snapshots and the current run save were copied
read-only into ignored `dist/incidents/2026-11-23-pid79770`; the live save was not
used as a mutable test profile. Installed Workshop v0.1.19 matched the published
baseline DLL SHA-256 `efd5544939b716a229b0f5c4f7d17a38d715e3f8c2757d42d4b2ca236a3ad8a8`.

Native source confirms that EndPlayerTurnAction does not wait for turn transition,
CurrentSide becomes Player before setup finishes, and the action executor can be
idle while turn-start hooks are still running. Ecstasy transforms Sadness to Joy
in AfterPlayerTurnStart. The former replay feeder checked only side/ending flags
and executor inactivity, so future ordinary actions could enter the paused queue
before setup completed. The native end-turn transition then cancels queued manual
actions; mismatched turn-end records are ignored and later generated cards never
receive their expected IDs. NetCombatCardDb assigns IDs on pile membership changes
and does not remove individual IDs during a combat. Missing-card lookup is a
consequence rather than a reason to invent or remap an ID.

Production changes are confined to `UndoSync/Replay/Replay.cs`:

- Ordinary CombatPlayPhaseOnly records wait for the owner's Play phase and native
  synchronizer PlayPhase as well as the Player side. A future numbered turn-end
  cannot overtake setup. An originally late turn-end keeps native ignore semantics.
- Hook, choice and resume record handling is unchanged, including native
  ReadyToBeginEnemyTurnAction's separate Combat action type. No new proposal/vote
  eligibility checks were added.
- Canceled actions receive one diagnostic with action identity and live phase.
  Native history records before cancellation, so rejecting every canceled input
  would reject valid original prefixes. Actual action exceptions still fail
  immediately; target-state and peer-checksum verification remain required.
- During mounted reconstruction only, disposable NCardTransformShineVfx creation
  is replaced by its verified native UpdateCard helper. The helper still cancels
  a pending card drag/play, rebinds NCard.Model, updates visuals and updates the
  NHandCardHolder. Transformation, pile changes, RNG and generation hooks execute
  normally. Normal gameplay and UI-free replay keep their existing paths.

A focused real rendered test passed before the final separate multiplayer runs.
The native producer uses merchant factories for Ecstasy/Vitriol plus starting
Defends, native Vitriol-generated Sadness, native Ecstasy-generated Joy and seven
real enemy turns. No card IDs, powers, hand contents or delay are injected. Three
players also play naturally generated Slimed instead of assuming a fixed hand.
Every actor actually plays at least four generated Joy cards. Original and fixed
producer traces AND target hashes/counts are identical, independently compared.

Before/after controls:

- Published v0.1.19, singleplayer PID 71230: 35 valid recorded events, then turn
  drift and missing card ID 8. Expected baseline failure, not a passing run.
- Published v0.1.19, three peers PIDs 91346 / 91481 / 91524: 120 valid events;
  future turn ends ignored and clients fail on missing ID 30. Host cancels the
  shared operation. Expected matching baseline failure.
- Readiness-only correction, rendered PID 73718: same 35-event target restored
  twice. Replay work 3021–3048 ms; total core restoration 3698–3721 ms.
- Final build, rendered PID 98197: same target restored twice, replay work
  548–555 ms; total core restoration 1092–1202 ms. Both replay builds force native
  Instant speed, so the core interval measures the actual transformation-shine
  wait reduction. The unmodified baseline did not finish and supplies no valid
  successful-replay speed ratio.

| Selected final run | Native PIDs | Distinct Undo sessions | Verdict |
| --- | --- | ---: | --- |
| fixed-sp | 98197 | 2 | PASS |
| fixed-mp3 | 92926, 93112, 93159 | 3 | PASS |
| extended-mp3 | 94748, 94795, 94839 | 4 | PASS |
| turn-choice-mp3 | 95918, 95961, 96005 | 1 | PASS |

Final three-player 120-event restorations took 2167–2419 ms per peer for the core
restore. RESTORE_BEGIN to native RESUME (including fade/release consensus) took
2527–3794 ms; do not substitute core time for the time until all peers resume.
Singleplayer RESTORE_BEGIN-to-RESUME was 1095–1211 ms. These are bounded local
measurements, not a guarantee for arbitrary history lengths or Steam latency.

All selected participants compare exact native target state, turn number, native
hand-holder/current-model binding, resumed checksums, retained transport and
continued real actions. The seven-turn cases restore twice in singleplayer and
rotate all three proposers in multiplayer, then reach native turn 9. Extended
regression supplies replayed choice records; the turn-choice regression cancels
discarded native live waits and continues after restore. Selected runs have zero
native divergences, skipped-turn diagnostics, unexpected cancellation diagnostics
and managed exception lines. Resource/RID shutdown messages remain in raw logs.

Validation: zero-warning Release builds; metadata-only MinimalCheck verifies 38
Harmony targets and 16 reflection surfaces including the exact static native
transform-update signature. Python syntax/CLI and diff checks pass. Independent
verifier accepts zero sleeps when readiness checks pass synchronously, requires
positive readiness counters and native transform/binding coverage, and rejects
both `[ERROR] System.*` and `ERROR: System.*` diagnostics.

Earlier failed fixture/preflight attempts are excluded and recorded in summary:
missing import/stale harness, overly strict sleep expectation, native Slimed
crowding fixed hands and one pre-replay Godot ScriptManagerBridge startup
KeyNotFoundException. A clean rendered focused rerun replaced the latter; the
startup engine diagnostic was not claimed fixed by this patch.

All eight temporary backup roots were restored and independently checked against
original installed-mod/test-profile hashes. Final process inventory is empty and
UDP 33771 free. Source, per-run harness hashes, PID-bound raw logs, screenshots,
exact producer comparisons, timings, verdicts and cleanup receipts are indexed in
`tools/MinimalHarness/results/2026-11-23/turn-start-fix/summary.json`.

Limits: one content seed and bounded weak encounters. This is a minimized real
native producer for the incident's mechanism, not the full user boss/relic/potion
trace. No exhaustive content fuzz sweep, arbitrary late-input/death/revive matrix,
other-mod compatibility acceptance or separate-computer Steam/P2P verification.

## Final v0.1.20 package smoke

The first viewport-regression run (`undosync-extended-osjistqj`, PIDs 20643/20956/21000) restored and resumed with identical checksum 2934972768, then failed the remote-cursor observation. Native `PeerInputMessage.Mode` is Unreliable and `ShouldBuffer` is false; the helper sent only one mouse sample. The helper now sends native mouse movement every 100 ms until every real peer has observed its cursors, keeping roster, online indicators and finite cursor-position checks. No production code or tested DLL changed.

The repeat (`undosync-extended-kkwj9dh9`, PIDs 28809/28897/28942) passed all three viewport-driven proposals and restores, all remote-cursor checks, continued gameplay and zero native divergences on game v0.113.1. Release manifest 0.1.20, DLL SHA-256 a764b6ae018c8a35f8229390cd966ff6b4223b29e6844796b22aa0252e4afc5a. The original failure remains retained, not counted as a pass.
