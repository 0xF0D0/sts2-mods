# Undo without current-state readiness checks

User contract: proposals and votes do not depend on input availability, turn state, running actions or peer state equality. Once everyone accepts and has received the proposer record, restore that record. Compare all peers only after replay. Keep the history picker open across remote actions.

## Reproduced before changes

- `before-picker-remote`: the original popup disappeared during a real remote card action. Host PID 69009 failed `same picker survives remote action`; other peers stopped through the parent abort marker.
- `before-ended-voter`: the first undo completed on all three peers. Before the second proposal, client2 PID 69273 used the native end-turn button (`CallReleaseLogic`). All votes reached the host, but client2 stayed input-disabled and never sent Ready. Both other peers sent Ready; the explicit 15-second second-operation deadline failed. This reproduces the reported symptom locally; the new Steam incident log was not supplied.

## Changes

- UI opening/selection and network Request/Ready/Start no longer inspect idle or input availability. The picker no longer closes on remote actions. Removed the pre-start combat-ended cancellation, since the accepted frozen record is authoritative.
- Reconstruction cancels the old native executor, resets the old combat through native cleanup and drains the canceled executor callbacks before initializing new owners. It does not wait for the discarded action to complete normally. Audited native ActionExecutor.Cancel, Unpause and FinishedExecutingActions; RunManager.CleanUp; CombatManager.Reset; PlayerCmd.EndTurn; NEndTurnButton.CallReleaseLogic in installed v0.111.0.
- History boundaries and completed replay still require stable execution for deterministic prefixes/final checksums. These are not pre-undo peer readiness conditions.

## Final binary runtime evidence

Core SHA256: `b4ccf7163cc449956fdd99956cb25471608728a7c63433b035c3ed4630892638`. All five final runs executed this exact binary. Native viewport mouse/key input reached real popup callbacks. Local ENet only; no actual remote Steam multiplayer validation. No commit or publication.

| Run | Verdict | Real peer PIDs | Seconds |
|---|---|---|---|
| before-picker-remote | FAIL | 69009, 69082, 69103 | 18.169 |
| before-ended-voter | FAIL | 69209, 69252, 69273 | 32.694 |
| unconditional-picker-focused3 | PASS | 71530, 71577, 71598 | 17.902 |
| unconditional-ended-voter3 | PASS | 71715, 71758, 71764 | 18.639 |
| unconditional-ended-voter4 | PASS | 71896, 71932, 71936, 71957 | 20.16 |
| unconditional-phases4 | PASS | 72446, 72485, 72509, 72530 | 28.187 |
| unconditional-release-race3 | PASS | 72722, 72764, 72783 | 18.64 |

Final coverage: 11 undo operations across 39 peer reconstructions. Exact target native state and final peer hashes matched. No native divergences, session replacements or opened/closed connections during undo; no native checksum generation during replay.

- Focused picker test held a real remote PlayCardAction task until AFTER undo completed. The same popup survived, its viewport click proposed while the old action was unfinished, restoration completed first, and releasing the discarded continuation did not change the restored state.
- Separate 3- and 4-peer ended-voter runs each completed first and second undo and continued live play.
- Four-peer phase fuzz rotated all proposers, voted during held live actions, delayed replay on another peer, removed one non-proposer recorder and injected stale native checksum/divergence messages. All four exact restores and stale-message isolation probes passed.
- Three-peer release race delayed the real final Release packet until a new native action arrived, recorded that action while the old operation was finishing, and completed the next undo.

Build: `dotnet build tools/MinimalHarness/MinimalHarness.csproj -c Release -warnaserror` passed. MinimalCheck passed all 16 Harmony targets and 14 reflection surfaces. `git diff --check` passed.

PID-bound native/core/harness logs, run metadata, structured peer verdicts and shared verifier summaries are saved alongside this report. Cleanup receipts cover each fixture; original account/mod files were restored and hashes checked after games exited. Unrelated AGENTS.md and assets were preserved.
