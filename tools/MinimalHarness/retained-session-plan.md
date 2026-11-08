# Retained multiplayer session replay

Authorized 2026-11-08: keep the real room/connections during undo, isolate native
checksums throughout replay, verify and publish only after the gates pass.
Production stays Hook/History/Replay/Network/UI. Preserve unrelated AGENTS/assets.

## Contract and native authority

Replay owns reconstruction with the SAME INetGameService and underlying host/
client transport. No Disconnect/StartHost/JoinFlow during successful undo. Native
RunManager.CleanUp disposes most run owners and disconnects: narrowly suppress
that Disconnect for the captured service only inside Replay's own cleanup.
Explicitly dispose EventSynchronizer, OneOffSynchronizer, CombatStateSynchronizer
and InputSynchronizer, which native CleanUp omits because normal teardown drops
the entire service. Public Dispose handlers were audited in installed v0.111.0.

Rehydrate LoadRunLobby locally using the existing real RunLobby roster and same
service; this is an initialization holder, not a new network lobby or fake peer.
SetUpSavedMultiplayer creates the new run owners; no native join/ready exchange.
Keep UndoSync's control handler and heartbeat pumping across recreation.

During Started through final Resume, discard native messages except heartbeat
and UndoSync control, suppress native checksum generation, and keep combat sync
and native recording disabled. This blocks stale checksums/choices/actions from
the old timeline. Native reliable gameplay messages and our Ready/Start receipts
use reliable ordered transport; capture receipts before clearing old owners.
Compute final hash with the pure NetFullCombatState overload (no send/NextId).
All Done hashes must match. Resume prepares each peer for native incoming data;
Resumed acknowledgements ensure EVERY peer is prepared before host Release
unblocks local input. Heartbeats and genuine disconnect detection stay active.

## Scope

Replay/Replay.cs: lifecycle, captured service, extra Dispose, hash/rearm, audit log.
Hook/Hooks.cs: exact-service cleanup Disconnect gates, native receive/checksum
gates, keep control attached during owned cleanup. Verify every patch target.
Network/Network.cs: Resumed/Release barrier and temporary ownerless service pump.
No malicious peers, recovery framework, snapshot copier, or sixth module.

## Gates

1. Release build + MinimalCheck; focused actual viewport UI restore.
2. Separate real 3/4-peer ENet phase integration: same service/transport/lobby ID,
zero connection changes and extra handshakes; exact states/checksums, repeated
undos and continued input. Actual native stale checksum/divergence messages must
be observed dropped; GenerateChecksum must not advance NextId during replay.
3. Focused real Steam one-host lifecycle check if safe without modifying user
cloud saves; one account cannot claim real Steam multi-peer acceptance.
4. Only after gates: version/package, commit/release/Workshop under current user
publication authorization. Preserve approved preview/title/description.

## No-go

No success with replaced connections, stale handlers, native checksums applied
while replaying, missing peer verdicts, or mismatch. Do not launch over a user's
game. Back up/restore local fixtures and verify hashes. No claim of actual Steam
multi-peer proof without real peers. Classify every stall before termination.

## Progress

- [x] Native API and lifecycle audit.
- [x] Implementation and static checks.
- [x] Focused viewport run.
- [x] Real 3/4-peer retained-session/checksum integration.
- [ ] Steam lifetime check / explicit remaining limitation.
- [ ] Conditional publication.

### Evidence / discoveries

Release build + 16-target MinimalCheck passed. First focused UI run PID 35563
used an older test DLL after a harness compile error; UI completed but retained
instrumentation was missing, so it was NOT accepted. Corrected harness focused
run PID 36149 passed. All fixtures restored by hash.

Real retained3 PIDs 36744/36786/36808 and retained4 PIDs
36981/37007/37026/37049 passed. Zero service replacements, opened/closed
connections or native checksums generated during replay on every peer. The
checksum fault probe sent real old checksum/divergence messages over native ENet:
12 drops in 3-peer run, 24 in 4-peer run. All final target states/checksums and
continued gameplay matched. Run durations: 23.663s and 28.172s respectively.
Additional Defect-host/Silent-client viewport regression PIDs
42032/42085/42089 passed three restores. Extended 3-peer PIDs
42399/42482/42501 passed four restores including all four event types, historical
choices/hooks/resumes, rejection, vote timeout, enemy-turn transition and later
input. These are local ENet results, not Steam multiplayer claims.

Steam single-host attempt PID 39369 failed before harness initialization while
Steam itself shut down. No core replay ran. Native logs show disconnected Steam
IPC and an initialization failure. User confirmed another PC is using the same
account. Do not restart/use that account, run Steam tests or publish through its
uploader until coordinated availability. Local/account/mod backups restored.
Steam-only storage isolation uses the game's public SaveManager injection and
real GodotFileIo on a task-owned clone; TestMode stays OFF. No fake transport,
checksum substitute or synthetic Steam ID is used.

Code inspection found a further retained-session boundary to exercise: after
native rearm but before a delayed Release reaches a client, a newly live remote
action must be accepted AND recorded. A focused actual-packet-latency regression
is being added before accepting that history handoff.

Release-race before case PIDs 45877/45921/45941 reproduced the history loss. The
last client's actual Release packet was delayed until a real host action arrived
over the retained connection; state applied but History.Count remained at the
restored prefix because CanRecord checked operation lifetime instead of replay
suppression. The run failed explicitly on client2, then parent aborted peers.
Fix: history/header recording follows Replay.SuppressNative, so live events are
recorded after native rearm while local input still awaits Release. No private
production fields, fake participants or replacement transport were used.

The exact release-latency case passed after the history gating fix (PIDs
47349/47390/47409). The late client recorded the first live remote action while
its local operation was still pending, then exported/replayed that prefix in a
second successful undo. Final four-peer phase run PIDs
47643/47679/47698/47721 passed all four operations and checksum isolation probes.
The old transport's native buffered messages are also drained through the replay
receive gate before old owner disposal. Candidate manifest is 0.1.11; publication
is not yet performed while the Steam account is in use elsewhere.

Final package candidate v0.1.11 matches the runtime-verified DLL SHA256
f927fc6a3d80d5311e090945aadf71a087f6a44c7c34de6eb7d9492f2f6a5f23. Final release-race3 and phases4 cases passed; source and artifacts are
retained. User explicitly confirmed the other PC is still using Steam and said
"배포는 기다려". Do not publish, upload, restart/use Steam or claim Steam
acceptance until the user reports availability. Local package is
dist/UndoSync-v0.1.11.zip, with candidate status in its JSON sidecar.

Publication authorization resumed: the user explicitly requested deploying the
locally verified candidate for their direct testing. Steam multi-peer acceptance
remains unverified; do not launch a game on the account in use elsewhere. Publish
the already tested package, with no title/description/image changes. This later
request supersedes the previous deployment hold, not the evidence limitations.
