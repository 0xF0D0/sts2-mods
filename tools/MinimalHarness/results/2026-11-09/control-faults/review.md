# Control and discarded-action fault review

Authority: installed v0.111.0 / 41cef1ea. Same-build cooperative protocol.
No commit, release or Steam upload authorized by this verification task.

## Pre-code triage

1. Old action lifetime. Claim: executor cancellation does not terminate effect
   tasks. Verification: before-old-action / client2 PID 53379 resumes an actual
   native Strike from a presentation wait AFTER reconstruction and raises
   NullReferenceException in CombatHistory.DamageReceived. Restored state and
   continued play still match across all three peers. Classification: valid
   lifetime defect, incident hang causality unclear. Current head: applies.
   Actionability: narrow cancellation of discarded presentation awaits; no claim
   of arbitrary native-task cancellation. Direction: cancel live combat Cmd
   waits at undo teardown so the awaiting old effect unwinds. Side effects:
   discarded effects receive expected OperationCanceledException; do not change
   vote eligibility or wait for live combat to become idle. Auto-fix: no;
   covered by user's request to continue checking/fixing these conditions.

2. Client loss. Claim: host misses individual disconnection. Verification:
   before-disconnect host PID 53524 remains Busy/Executing with connected host
   service after client2 PID 53594 disconnects; explicit baseline cleanup needed.
   Classification: valid. Current head: applies. Actionability: current blocker.
   Direction: observe native ClientDisconnected and fail this operation with
   peer identity. Side effects: before Start unchanged run continues, after
   Start existing failure cleanup applies. Auto-fix: no; authorized scope above.

3. Silent send failure. Claim: one lost Vote/Done has no repair. Verification:
   real ENet send boundary deliberately drops one serialized production message
   (before-vote-send client2 PID 53807, before-done-send client2 PID 54004); peers
   remain Busy. Classification: valid. Current head: applies. Actionability:
   current blocker. Direction: per-recipient sequences, processed ACK, retry,
   duplicate suppression and ordered handling; independent periodic progress
   snapshots identify delivery versus replay waits. Existing operation deadlines
   remain. Side effects: same-build wire change, bounded periodic traffic,
   ACK must not be interpreted as replay success; final checksum still required.
   Auto-fix: no; user explicitly requested checking/retrying missing ACKs.

Baseline fixture PASS means expected defect was observed and task-owned cleanup
completed. It does not mean the core handled the fault correctly. ENet packet
fault injection is not a real Steam SDK rejection or authenticated Steam test.
