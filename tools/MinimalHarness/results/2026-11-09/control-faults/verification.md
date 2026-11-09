# Final verification and residuals

Game: v0.111.0 / 41cef1ea. Core SHA-256:
`b334773af8ab445e0c6a1cb3129d6817bdc12725c1a0c1b0d372c11f4acc3534`.

| Run | Real native PIDs | Elapsed including startup | Result |
| --- | --- | --- | --- |
| accepted-peer-error3 | host 10369, client 10397, client2 10418 | 19.646s | PASS |
| accepted-old-action3 | host 10568, client 10611, client2 10634 | 18.631s | PASS |
| accepted-disconnect3 | host 10766, client 10809, client2 10834 | 16.638s | PASS |
| accepted-control-matrix3 | host 10942, client 10982, client2 11008 | 55.772s | PASS |
| accepted-regression4 | host 11524, client 11569, client2 11590, client3 11594 | 23.181s | PASS |
| accepted-old-action-normal3 | host 12014, client 12062, client2 12066 | 22.64s | PASS |

- Baseline old task resumes a genuine Strike after reconstruction and faults in
  CombatHistory.DamageReceived. Target state still matched: this did NOT prove
  the original reported black-screen hang. Final build cancels the held native
  presentation await; effect body terminates before reconstructed run completes,
  with no old action exception, matching state and continued input. Normal speed
  was separately verified and restored after replay.
- Before-notified-disconnect: host PID 6210 RECEIVED ClientDisconnected for 2001,
  but stayed Busy on the original build. Final accepted disconnect host PID
  10766 terminates with the participant/stage cause and survivors return to menu.
- Before-vote-send and before-done-send deliberately drop one actual serialized
  outgoing native ENet packet. Original core stays Busy until explicit test
  cleanup. Final matrix: 14 faults/14 undos in one connected room, 28 duplicate
  receptions without duplicate application, exact states, drained ACK queues,
  continued native play. Client and host directions, Plan ordering and final
  Release ACK after operation completion are covered.
- Final peer-error: host sees peer 2001 at replay-idle with executor still running
  and missingDone=[2001], while no control ACK is outstanding. Inject a native
  action exception, drop first Cancel: retransmit+receipt succeeds, all three
  peers terminate with the original injected failure cause, no history commit.
- Four peers: four repeated undos, all matching final checksums/states, continued
  native play, original service/transport/lobby, zero native checksums generated
  during suppression, no connection replacement. See per-run summary.json.
- Release build, metadata-only MinimalCheck (18 targets / 14 reflection surfaces),
  Python syntax and diff checks pass. Legacy SurfaceCheck exits 2 because the
  removed StateSnapshot.cs is required; it is NOT a minimal-core pass.

Rejected/incomplete runs retained: after-disconnect (immediate ENet reset produced
no host disconnect callback); final-peer-error3 (test incorrectly expected event
injection stage although the genuine held executor was at final replay-idle).
Corrected scenarios above include explicit native callback/stage witnesses.

Residuals: local ENet, not authenticated Steam acceptance or actual SDK rejection;
Cmd presentation await cancellation does not prove every possible native await
is stopped; permanent communication silence retains existing 30s/180s operation
deadlines and is diagnosed by pending ACKs/last peer progress, not a new timeout.

## Cleanup exception -- not an all-restored result

All game processes exited. Task-created MinimalHarness install and accounts 2001 /
3001 were removed. Exact-hash copies restored the original default/1 saved run,
prefs and progress (12 files). Real Steam account data was not modified. However,
original temporary backup directories were missing files at restore time. Original
local DLL, settings.save/settings.save.backup for default/1 and 1001, and the old
native diagnostic latest.mcr could not be recovered by recorded hash. Their
absence cause is unproven. Current local DLL remains the verified build; default
settings retain test values. Native diagnostic replay original remains unavailable.
User was informed; original-versus-verified local installation choice is pending.
No commit, release or Steam upload.

Restore previously moved the live account before checking the backup. It now
validates ALL backups before changing any installed path; the current missing
fixture was rejected with byte-identical installed files. Future prepare verifies
source/copy/archive hashes before deployment and writes a second gitignored zip
outside OS temp. This does not recover the missing original files retroactively.

## Release authorization

User subsequently requested deployment on 2026-11-09. Release v0.1.14 stages
the exact validated b334773a DLL with the v0.1.14 manifest. Keeping this new
local release supersedes the pending choice to restore the old local DLL; the
missing original test-account settings/diagnostic replay are not claimed restored.
Steam account saves are unchanged. Workshop/GitHub status is tracked separately
in dist/UndoSync-v0.1.14-publication.json.
