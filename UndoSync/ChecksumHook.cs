using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// One restorable point in time, keyed by the game's own checksum id.
/// The checksum id sequence is identical on every peer (deterministic execution),
/// so "restore to id N" refers to the exact same game moment everywhere.
/// </summary>
internal sealed class SyncPoint
{
    public uint ChecksumId;
    public string Context = "";
    public int EventCount;

}

internal static class ChecksumHook
{
    // Sorted by checksum id (ascending). Native journal history is unbounded for the
    // current combat, so every eligible point remains until combat cleanup.
    private static readonly SortedList<uint, SyncPoint> SyncPoints = new();

    // Metadata reference for picker UI. The same object also lives in SyncPoints; it
    // contains no custom game state or counter copies.
    private static SyncPoint? _combatStartPoint;

    // Armed only after a native restore plan has been created and before the old run is
    // torn down. This is metadata, not a saved game state: it tells the tracker transition
    // which existing journal prefix is still valid after official re-entry. It must survive
    // CombatManager.Reset during teardown, but is consumed only after EnsureSubscribed has
    // applied it to the newly-created tracker.
    private static NativeRestoreBranchTarget? _pendingNativeRestoreTarget;
    private static bool _preserveCombatStartAnchorForReplay;
    private static bool _replayCombatSetUpObserved;

    private readonly record struct NativeRestoreBranchTarget(uint ChecksumId, int EventCount);

    private static ChecksumTracker? _subscribedTracker;
    private static CombatManager? _subscribedCombatManager;

    // Set when "After player turn start" fires (OnChecksumGenerated) and consumed when
    // CombatManager.TurnStarted fires for the player side (OnTurnStarted) — see the doc
    // comment on OnChecksumGenerated for why the capture itself is deferred to that point.
    private static uint? _pendingTurnStartId;
    private static string _pendingTurnStartContext = "";


    // ---- Fuzz-only divergence diagnostics (--undosync-mpfuzz) --------------------------------
    // See MpFuzz.cs's own top-of-file doc comment for the harness this instruments, and
    // MpFuzz.OnStateDivergenceObserved for where a divergence is first detected. That detection path
    // only ever learns THAT a checksum id diverged and the remote peer's checksum HASH
    // (StateDivergenceMessage.senderChecksum, ChecksumTracker.cs:136/209-214) — never what THIS
    // peer's own state at that id actually contained, and never anything to diff against the other
    // peer's own log. Everything below exists to answer "what did MY side's state at id N look like"
    // on demand, so the same question can be answered on both peers and the two logs diffed by hand.
    // Gated on MpFuzzInstrumentationEnabled everywhere it does any work — see each member's own doc
    // comment for exactly where — so a normal game (no --undosync-mpfuzz) never allocates
    // DivergenceRing, never pays for AppendDivergenceRingEntry's extra SerializeCurrentState() call,
    // and never emits a "[MpFuzz][diag]" line.

    /// <summary>
    /// True only when --undosync-mpfuzz was passed on the command line. Computed once at type-init
    /// time straight off CommandLineHelper.HasArg — safe this early, since CommandLineHelper's own
    /// static constructor reads Godot's OS.GetCmdlineArgs() eagerly (CommandLineHelper.cs), independent
    /// of any game/mod init ordering. Every member in this section checks this before doing anything.
    /// </summary>
    private static readonly bool MpFuzzInstrumentationEnabled = CommandLineHelper.HasArg("undosync-mpfuzz");

    /// <summary>Bound on <see cref="DivergenceRing"/> — see AppendDivergenceRingEntry's own doc
    /// comment for why every checksum id (not just TryStoreSyncPoint's sync-point-eligible subset)
    /// needs covering, and why 30 is enough trailing context for that without growing the ring
    /// unbounded across a long fuzz combat.</summary>
    private const int MaxDivergenceRingEntries = 30;

    /// <summary>
    /// Fuzz-only ring of the last <see cref="MaxDivergenceRingEntries"/> checksum ids ChecksumTracker
    /// generated on THIS peer. Sorted ascending by id, trimmed the same RemoveAt(0)-once-over-bound
    /// way <see cref="SyncPoints"/> is. Populated by AppendDivergenceRingEntry, read by
    /// DumpDivergenceDiagnostics.
    /// </summary>
    private static readonly SortedList<uint, ChecksumSnapshot> DivergenceRing = new();

    /// <summary>
    /// One ChecksumTracker.ChecksumGenerated (ChecksumTracker.cs:63) firing, captured into
    /// <see cref="DivergenceRing"/>. StateDump is just the fullState object the event already handed
    /// us (cheap — no recompute). ByteLength/ByteHash instead reuse SerializeCurrentState (below) —
    /// the SAME serialization VerifyRestoreFidelity already relies on for its own byte-exact
    /// comparison — rather than a second serializer.
    /// </summary>
    private sealed class ChecksumSnapshot
    {
        public string Context = "";
        public string StateDump = "";
        public int ByteLength;
        public uint ByteHash;
    }

    internal static void EnsureSubscribed()
    {
        var tracker = RunManager.Instance?.ChecksumTracker;
        if (tracker != null && !ReferenceEquals(tracker, _subscribedTracker))
        {
            if (_subscribedTracker != null)
                _subscribedTracker.ChecksumGenerated -= OnChecksumGenerated;
            tracker.ChecksumGenerated += OnChecksumGenerated;
            _subscribedTracker = tracker;

            if (_pendingNativeRestoreTarget is { } pending)
            {
                RetainNativeBranch(pending, "new ChecksumTracker subscription");
                // Consume this only after RetainNativeBranch has run. A failed/partial
                // transition must not silently turn the next tracker into a fresh history.
                _pendingNativeRestoreTarget = null;
                if (_replayCombatSetUpObserved)
                    _preserveCombatStartAnchorForReplay = false;
                _replayCombatSetUpObserved = false;
                Log.Write($"[ChecksumHook] Applied pending native restore branch at checksum={pending.ChecksumId} events={pending.EventCount}; retained={SyncPoints.Count} point(s).");
            }
            else
            {
                SyncPoints.Clear();
                _combatStartPoint = null;
                Log.Write($"[ChecksumHook] Subscribed to ChecksumTracker and cleared ordinary history. net={RunManager.Instance?.NetService?.Type} netId={RunManager.Instance?.NetService?.NetId}");
            }
        }

        // CombatManager.Instance (CombatManager.cs:91) is a static singleton, but guard anyway
        // since EnsureSubscribed can run before it exists.
        var cm = CombatManager.Instance;
        if (cm != null && !ReferenceEquals(cm, _subscribedCombatManager))
        {
            if (_subscribedCombatManager != null)
            {
                _subscribedCombatManager.TurnStarted -= OnTurnStarted;
                _subscribedCombatManager.CombatSetUp -= OnCombatSetUp;
            }
            cm.TurnStarted += OnTurnStarted;
            // CombatSetUp (CombatManager.cs:245, invoked at :467, end of SetUpCombat :444-468 —
            // once per combat, before the separate AfterCombatRoomLoaded :470-475 starts the
            // turn loop) is when we drop the previous combat's start anchor so
            // TryGetCombatStart can't return a stale target into a new combat.
            cm.CombatSetUp += OnCombatSetUp;
            _subscribedCombatManager = cm;
            Log.Write("[ChecksumHook] Subscribed to CombatManager.TurnStarted + CombatSetUp.");
        }
    }

    /// <summary>
    /// Fires on every peer at the same logical moments (after each game action + turn
    /// boundaries), with the same incrementing id. We snapshot the full combat state
    /// and the synchronizer counters so both can be rolled back together.
    /// Only play-phase moments are stored — same policy as the original single-player
    /// mod, which only ever snapshotted player-initiated actions during the play phase.
    ///
    /// The turn-start boundary ("After player turn start", CombatManager.cs:797) is special:
    /// it fires BEFORE RunAutoPrePlayPhase (:859-869) has run the play-phase entry hooks
    /// (Hook.AfterAutoPrePlayPhaseEntered, Hook.cs:940) and BEFORE the synchronizer flips to
    /// PlayPhase (:830-832). Capturing right here would restore into PlayerTurnPhase.Start with
    /// those hooks never re-run for the turn (confirmed via a NetFullCombatState.ToString() diff:
    /// "Phase: Play" vs "Phase: Start" was the only line that differed). So for this context we
    /// don't capture at all — we remember the id and capture later, in OnTurnStarted, once
    /// CombatManager.TurnStarted fires for the player side (:834), which happens only after every
    /// player's RunAutoPrePlayPhase has completed. Storing under the SAME checksum id keeps
    /// "restore to id N" resolving to the same id on every peer.
    /// </summary>
    private static void OnChecksumGenerated(NetChecksumData data, string context, NetFullCombatState fullState)
    {
        try
        {
            if (UndoSyncMod.IsRestoring) return;

            // Fuzz-only trace: the filters below return silently, so without this there is no way to
            // tell "the game never generated this checksum" from "we saw it and dropped it".
            if (UndoFuzz.TraceChecksums)
                Log.Write($"[Fuzz][trace] checksum id={data.id} context='{context}'");

            // Fuzz-only (MpFuzzInstrumentationEnabled): capture EVERY checksum id into the
            // diagnostic ring, before any of the sync-point eligibility filters below run — a
            // divergence can land on an id TryStoreSyncPoint itself would have skipped (the
            // A0/A1/A2 gates inside it, a non-player-decision context, a non-PlayPhase moment,
            // etc.), and DivergenceRing exists specifically so THAT id can still be dumped if a
            // divergence is later reported for it. See DumpDivergenceDiagnostics.
            if (MpFuzzInstrumentationEnabled)
                AppendDivergenceRingEntry(data.id, context, fullState);

            var cs = UndoSyncMod.GetCombatState();
            if (cs == null || cs.CurrentSide != CombatSide.Player) return;
            var syncr = RunManager.Instance?.ActionQueueSynchronizer;
            if (syncr == null) return;

            if (context.StartsWith("After player turn start"))
            {
                if (_pendingTurnStartId.HasValue)
                    Log.Write($"[ChecksumHook] turn-start id={data.id} arrived while id={_pendingTurnStartId.Value} was still pending (TurnStarted never fired for it) — dropping the stale pending id.");
                _pendingTurnStartId = data.id;
                _pendingTurnStartContext = context;
                Log.Write($"[ChecksumHook] Deferred turn-start capture for id={data.id} ({context}) until CombatManager.TurnStarted.");
                return;
            }

            // A non-turn-start checksum arriving while a turn-start anchor is still pending
            // means TurnStarted never fired for it (e.g. combat ended, or every player was
            // already ready to end turn — CombatManager.cs:806-812). Drop it so a later turn's
            // TurnStarted can't store this now-stale id.
            if (_pendingTurnStartId.HasValue)
            {
                Log.Write($"[ChecksumHook] Dropping pending turn-start id={_pendingTurnStartId.Value} — checksum id={data.id} ({context}) arrived first, so TurnStarted never fired for the pending anchor.");
                _pendingTurnStartId = null;
                _pendingTurnStartContext = "";
            }

            if (syncr.CombatState != ActionSynchronizerCombatState.PlayPhase) return;

            // Only the player's own deliberate moves are worth rewinding to. Relic and
            // power triggers run as their own GameActions (GenericHookGameAction) and
            // would otherwise flood the picker with steps nobody wants to undo to —
            // "just before my relic fired" is not a decision the player made.
            if (!IsPlayerDecision(context)) return;

            TryStoreSyncPoint(data.id, context);
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] OnChecksumGenerated ERROR: {ex}");
        }
    }

    /// <summary>
    /// CombatManager.TurnStarted (CombatManager.cs:273) fires for both sides (:834 player,
    /// :840 enemy). This is the deferred capture point for the turn-start checksum id recorded
    /// by OnChecksumGenerated above: by the time it fires for the player side, every player's
    /// RunAutoPrePlayPhase has completed (Phase == Play, synchronizer already in PlayPhase), so
    /// capturing here anchors the turn-start undo target at a point the game will actually
    /// replay identically.
    /// </summary>
    private static void OnTurnStarted(CombatState cs)
    {
        try
        {
            if (UndoSyncMod.IsRestoring) return;
            if (!_pendingTurnStartId.HasValue) return;
            if (cs.CurrentSide != CombatSide.Player) return; // enemy-side TurnStarted — not ours

            var id = _pendingTurnStartId.Value;
            var context = _pendingTurnStartContext;
            _pendingTurnStartId = null;
            _pendingTurnStartContext = "";
            TryStoreSyncPoint(id, context);
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] OnTurnStarted ERROR: {ex}");
        }
    }

    /// <summary>
    /// CombatManager.CombatSetUp (CombatManager.cs:245/:467) fires once per combat, before
    /// any checksum for the new combat is generated. Clears the previous combat's start
    /// anchor so it can't be mistaken for the new combat's — TryStoreSyncPoint below
    /// re-captures it from the new combat's first turn-start anchor.
    /// </summary>
    private static void OnCombatSetUp(CombatState cs)
    {
        try
        {
            if (_preserveCombatStartAnchorForReplay)
            {
                // Official replay re-entry raises CombatSetUp before the first new native
                // checksum. Keep the retained anchor. The flag is independent of the
                // pending target because the new tracker subscription may happen before or
                // after this callback; a normal combat never sets it.
                var currentTracker = RunManager.Instance?.ChecksumTracker;
                bool newTrackerAlreadyExists = currentTracker != null
                    && !ReferenceEquals(currentTracker, _subscribedTracker);
                if (_pendingNativeRestoreTarget == null && !newTrackerAlreadyExists)
                    _preserveCombatStartAnchorForReplay = false;
                if (newTrackerAlreadyExists || _pendingNativeRestoreTarget == null)
                    _replayCombatSetUpObserved = true;
                Log.Write("[ChecksumHook] CombatSetUp during pending native replay restore — retained combat-start anchor.");
            }
            else
            {
                _combatStartPoint = null;
                Log.Write("[ChecksumHook] CombatSetUp — cleared combat-start anchor.");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] OnCombatSetUp ERROR: {ex}");
        }
    }

    /// <summary>
    /// Captures a sync point under the given checksum id/context. Shared by the immediate-capture
    /// path (OnChecksumGenerated, for in-play-phase player decisions) and the deferred turn-start
    /// path (OnTurnStarted). The native replay writer is the sole source of restorable history;
    /// this class stores only the event-list index needed to select a prefix later.
    ///
    /// Anchor eligibility gate: even a checksum that passed every filter above (player side,
    /// PlayPhase, IsPlayerDecision) is not automatically safe to anchor on. All conditions below
    /// read game logic state at a checksum moment — never local input/UI/frame timing — so they
    /// evaluate identically on every peer, and living here (rather than in the two callers) means
    /// neither capture path can bypass them.
    /// </summary>
    private static void TryStoreSyncPoint(uint checksumId, string context)
    {
        var rm = RunManager.Instance;
        var syncr = rm?.ActionQueueSynchronizer;
        if (rm == null || syncr == null)
        {
            Log.Write($"[ChecksumHook] id={checksumId} TryStoreSyncPoint: RunManager/synchronizer unavailable, skipping");
            return;
        }

        var cs = UndoSyncMod.GetCombatState();
        if (cs == null)
        {
            Log.Write($"[ChecksumHook] id={checksumId} TryStoreSyncPoint: no combat state, skipping");
            return;
        }

        // A0: combat must not be facing a pending loss. CombatManager.LoseCombat()
        // (CombatManager.cs:1262-1270) does NOT end combat immediately — it only sets
        // CombatTurnState.PendingLoss, "to avoid race conditions where effects try to run
        // after IsInProgress is false" (LoseCombat's own doc comment). The actual loss
        // processing (IsInProgress = false, CombatEnded fired) happens later, in
        // ProcessPendingLoss (:1274-1284), called only from CheckWinCondition (:1387-1392) at
        // the next safe point. So a lethal action's finished-execution checksum can fire while
        // the loss is still only pending — anchoring there would store an undo target that
        // looks like ongoing combat but is already doomed. IsAboutToLose (CombatManager.cs:192,
        // public property, no reflection: `_turnState?.PendingLoss != null`) reads that same
        // game-logic state, so — like A1/A2 below — it evaluates identically on every peer.
        if (CombatManager.Instance.IsAboutToLose)
        {
            Log.Write($"[ChecksumHook] id={checksumId} ({context}) SKIPPED — combat loss pending (IsAboutToLose)");
            return;
        }

        foreach (var player in cs.Players)
        {
            // A1: every player must actually be in the play phase. The turn-start checksum
            // ("After player turn start") fires BEFORE RunAutoPrePlayPhase (CombatManager.cs:
            // 859-869) has run the play-phase entry hooks and moved the player Start -> AutoPrePlay
            // -> Play for every player (CombatManager.cs:862/865) — a snapshot taken while ANOTHER
            // player is still mid turn-start would restore into that half-entered state, and the
            // game will not re-run those hooks for it.
            if (player.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            {
                Log.Write($"[ChecksumHook] id={checksumId} ({context}) SKIPPED — player {player.NetId} not in Play phase (phase={player.PlayerCombatState?.Phase.ToString() ?? "none"})");
                return;
            }

            // A2: no player may be mid card/potion effect resolution — including nested
            // auto-plays (e.g. a Sly card auto-played when discarded), per
            // IsExecutingCardOrPotionEffect's own doc comment. Public method, no reflection
            // (CombatManager.cs:368-371, backed by _cardOrPotionEffectDepth at :56/:387/:398).
            // Anchoring mid-resolution would restore into a partially-resolved effect.
            if (CombatManager.Instance.IsExecutingCardOrPotionEffect(player))
            {
                Log.Write($"[ChecksumHook] id={checksumId} ({context}) SKIPPED — player {player.NetId} mid card/potion effect resolution");
                return;
            }
        }

        if (!NativeReplayJournal.TryCapturePoint(checksumId, context, out var eventCount, out var detail))
        {
            Log.Write($"[ChecksumHook] id={checksumId} native replay capture failed, skipping: {detail}");
            return;
        }

        var sp = new SyncPoint
        {
            ChecksumId = checksumId,
            Context = context,
            EventCount = eventCount,
        };
        SyncPoints[checksumId] = sp;
        Log.Write($"[ChecksumHook] Stored native sync point id={checksumId} ({context}) | events={sp.EventCount} | total={SyncPoints.Count}");

        // Keep a metadata reference for the picker; the point remains in the unbounded list too.
        if (_combatStartPoint == null && context.StartsWith("After player turn start"))
        {
            _combatStartPoint = sp;
            Log.Write($"[ChecksumHook] Captured combat-start anchor id={checksumId}.");
        }
    }

    /// <summary>
    /// The game's own checksum payload for the current state: NetFullCombatState.FromRun(rs, null)
    /// serialized with the game's PacketWriter. This is exactly what ChecksumTracker hashes, so
    /// comparing these bytes proves a restore is byte-identical across EVERY checksummed field —
    /// including the ones ToString() never prints (per-player rngSet, relicGrabBag, pile/potion/
    /// relic contents rather than their counts).
    /// justFinishedAction is always passed as null on both sides so the embedded
    /// lastExecutedActionId/lastExecutedHookId can't make two equal states compare unequal.
    /// </summary>
    private static byte[]? SerializeCurrentState()
    {
        try
        {
            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return null;
            var state = NetFullCombatState.FromRun(rs, null);
            var writer = new PacketWriter { WarnOnGrow = false };
            state.Serialize(writer);
            var bytes = new byte[writer.BytePosition];
            Array.Copy(writer.Buffer, bytes, bytes.Length);
            return bytes;
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] SerializeCurrentState ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fuzz-only (MpFuzzInstrumentationEnabled — checked by the caller, OnChecksumGenerated, before
    /// this is even invoked): records one ChecksumTracker.ChecksumGenerated firing into
    /// DivergenceRing, keyed by checksum id. ByteLength/ByteHash reuse SerializeCurrentState rather
    /// than a second serializer — see ChecksumSnapshot's own doc comment for why that matters.
    /// </summary>
    /// <summary>
    /// Dumps every entry currently in <see cref="DivergenceRing"/>. Called at combat end under
    /// --undosync-mpfuzz so BOTH peers' state dumps exist for the same ids, which is what actually
    /// locates a divergence: the per-peer stream hashes say WHICH id first disagrees, and only a
    /// line-by-line diff of the two peers' dumps at that id says WHAT disagrees. Dumping only on
    /// the divergence notification is not enough — the host is frequently the peer that detects,
    /// and it never receives a notification about itself, so one side's dump would be missing.
    /// </summary>
    internal static void DumpDivergenceRing(string role)
    {
        if (!MpFuzzInstrumentationEnabled) return;
        try
        {
            foreach (var kv in DivergenceRing)
            {
                Log.Write($"[MpFuzz][ring] role={role} id={kv.Key} hash={kv.Value.ByteHash} "
                    + $"len={kv.Value.ByteLength} context='{kv.Value.Context}'");
                var lines = (kv.Value.StateDump ?? "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    Log.Write($"[MpFuzz][ring] role={role} id={kv.Key} line={i}: {lines[i].TrimEnd()}");
            }
            Log.Write($"[MpFuzz][ring] role={role} END ({DivergenceRing.Count} id(s))");
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz][ring] role={role} DumpDivergenceRing ERROR: {ex.Message}");
        }
    }

    private static void AppendDivergenceRingEntry(uint id, string context, NetFullCombatState fullState)
    {
        try
        {
            var bytes = SerializeCurrentState();
            var entry = new ChecksumSnapshot
            {
                Context = context,
                StateDump = fullState.ToString() ?? "",
                ByteLength = bytes?.Length ?? 0,
                ByteHash = bytes != null ? HashBytesFnv1a(bytes) : 0,
            };
            DivergenceRing[id] = entry;
            while (DivergenceRing.Count > MaxDivergenceRingEntries)
                DivergenceRing.RemoveAt(0);

            // One greppable line per RAW checksum, before any sync-point filtering, so the two
            // peers' streams can be diffed directly. This is what tells the two candidate causes of
            // a divergence apart, which log lines from our own filtered sync points cannot:
            //   - same id, DIFFERENT context  => the peers attached the id to different actions,
            //     i.e. a protocol/ordering problem after the restore.
            //   - same id, SAME context, different hash => the peers restored to states that are
            //     not byte-identical to EACH OTHER, even though each matched its own snapshot —
            //     the local fidelity check compares a peer against itself and is blind to this.
            Log.Write($"[MpFuzz][stream] id={id} hash={entry.ByteHash} len={entry.ByteLength} context='{context}'");
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz][diag] AppendDivergenceRingEntry ERROR (id={id}): {ex.Message}");
        }
    }

    /// <summary>
    /// Fuzz-only: stable 32-bit fingerprint of a byte payload, for cheap cross-peer/cross-log
    /// comparison — NOT the game's own checksum algorithm (ChecksumTracker.GenerateChecksum uses
    /// XxHash32 from the System.IO.Hashing package, which this project does not reference; see
    /// UndoSync.csproj's Reference list). FNV-1a, same algorithm and "stable across processes,
    /// machines and runtimes" reasoning as UndoFuzz.DeterministicSeed (UndoFuzz.cs) already uses for
    /// its own seed derivation — applied here to raw bytes instead of a string's UTF-16 code units.
    /// </summary>
    private static uint HashBytesFnv1a(byte[] bytes)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    /// <summary>
    /// Fuzz-only (MpFuzzInstrumentationEnabled — checked first thing, so this is always a safe cheap
    /// no-op to call from a normal game): full diagnostic dump for one checksum divergence, called by
    /// MpFuzz.OnStateDivergenceObserved the moment ChecksumTracker.OnReceivedStateDivergenceMessage
    /// fires on this peer (ChecksumTracker.cs:136). That handshake alone only tells this peer THAT a
    /// checksum id diverged and the remote peer's checksum HASH (StateDivergenceMessage.senderChecksum
    /// — see ChecksumTracker.cs:209-214 for how it's populated on the sending side); it says nothing
    /// about what THIS peer's own state at that id contained. This looks that up in DivergenceRing and
    /// writes it all out.
    ///
    /// Log line prefixes, for mechanical extraction:
    ///   "[MpFuzz][diag] SUMMARY id=..."           — one line: role/netId, the diverging id, the
    ///                                                remote checksum, this peer's local payload byte
    ///                                                length + hash, and the three counters below.
    ///   "[MpFuzz][diag] STATE id=... line=N: ..." — one line per line of this peer's full local
    ///                                                NetFullCombatState.ToString() dump for id N.
    ///   "[MpFuzz][diag] STATE id=... END (...)"   — closes the STATE block (line count, for a
    ///                                                mechanical "did I get all of them" check).
    /// </summary>
    internal static void DumpDivergenceDiagnostics(uint checksumId, ulong remotePeerId, uint remoteChecksum)
    {
        if (!MpFuzzInstrumentationEnabled) return;
        try
        {
            var rm = RunManager.Instance;
            ulong myNetId = rm?.NetService?.NetId ?? 0;
            uint localNextActionId = rm?.ActionQueueSet?.NextActionId ?? 0;
            uint localNextHookId = rm?.ActionQueueSynchronizer?.NextHookId ?? 0;
            uint trackerNextId = rm?.ChecksumTracker?.NextId ?? 0;

            if (!DivergenceRing.TryGetValue(checksumId, out var snap))
            {
                Log.Write($"[MpFuzz][diag] SUMMARY id={checksumId} role={rm?.NetService?.Type} netId={myNetId} "
                    + $"remotePeerId={remotePeerId} remoteChecksum={remoteChecksum} "
                    + "localPayload=UNAVAILABLE (id fell out of the diagnostic ring, or was never captured) "
                    + $"localNextActionId={localNextActionId} localNextHookId={localNextHookId} trackerNextId={trackerNextId}");
                return;
            }

            Log.Write($"[MpFuzz][diag] SUMMARY id={checksumId} role={rm?.NetService?.Type} netId={myNetId} "
                + $"remotePeerId={remotePeerId} remoteChecksum={remoteChecksum} "
                + $"localByteLength={snap.ByteLength} localByteHash={snap.ByteHash} localContext='{snap.Context}' "
                + $"localNextActionId={localNextActionId} localNextHookId={localNextHookId} trackerNextId={trackerNextId}");

            var lines = snap.StateDump.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                Log.Write($"[MpFuzz][diag] STATE id={checksumId} line={i}: {lines[i]}");
            Log.Write($"[MpFuzz][diag] STATE id={checksumId} END ({lines.Length} lines)");
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz][diag] DumpDivergenceDiagnostics ERROR (id={checksumId}): {ex.Message}");
        }
    }

    // Kept as a source-compatible diagnostic for the legacy fuzz harness. Native restore
    // completion is verified by the final multiplayer checksum, not by a local snapshot diff.
    [Obsolete("Native replay restore is asynchronous; use NativeReplayRestorer and its final checksum result.")]
    internal static bool LastRestoreFidelityOk => false;

    /// <summary>
    /// True when the checksum context names an action the player actively chose.
    /// Context format is "finished action execution {action}", and each action's
    /// ToString starts with its type name (PlayCardAction / UsePotionAction /
    /// NetDiscardPotionGameAction), so a substring test is enough.
    /// </summary>
    private static bool IsPlayerDecision(string context) =>
        context.Contains("PlayCardAction")
        || context.Contains("UsePotionAction")
        || context.Contains("DiscardPotionGameAction");

    internal static void ClearSyncPoints()
    {
        if (_pendingNativeRestoreTarget is { } pending)
        {
            // CombatManager.Reset is part of the official teardown used by native replay
            // restore. Preserve only the already-recorded branch metadata; no game state,
            // snapshot, or native counter is captured here. EnsureSubscribed consumes the
            // marker after the new tracker has been observed and applies the same pruning.
            RetainNativeBranch(pending, "combat reset during native replay restore");
            _pendingTurnStartId = null;
            _pendingTurnStartContext = "";
            DivergenceRing.Clear();
            Log.Write($"[ChecksumHook] Combat reset during pending native replay restore — preserved native branch metadata through teardown; retained={SyncPoints.Count} point(s).");
            return;
        }

        SyncPoints.Clear();
        _pendingTurnStartId = null;
        _pendingTurnStartContext = "";
        _combatStartPoint = null;
        _preserveCombatStartAnchorForReplay = false;
        _replayCombatSetUpObserved = false;
        // Fuzz-only ring (see its own doc comment) — always safe to clear even when
        // MpFuzzInstrumentationEnabled is false, since it's then always already empty.
        DivergenceRing.Clear();
    }

    /// <summary>
    /// The undo target = second-newest sync point (the newest describes the current
    /// state; the one before it is the state before the last action). The stored id
    /// sequences are identical on every peer, so this resolves to the same id everywhere.
    /// </summary>
    internal static bool TryGetUndoTarget(out SyncPoint target)
    {
        if (SyncPoints.Count < 2)
        {
            target = null!;
            return false;
        }
        target = SyncPoints.Values[SyncPoints.Count - 2];
        return true;
    }

    // The native journal is unbounded for the current combat, so this fallback is only a
    // defensive metadata lookup and is not a trim-survival path.
    internal static bool HasSyncPoint(uint id) =>
        SyncPoints.ContainsKey(id) || (_combatStartPoint != null && _combatStartPoint.ChecksumId == id);

    /// <summary>All stored sync points, newest first (index 0 = current state).</summary>
    internal static List<SyncPoint> SyncPointsNewestFirst()
    {
        var list = new List<SyncPoint>(SyncPoints.Values);
        list.Reverse();
        return list;
    }

    // Keep the metadata fallback for callers that hold the combat-start reference.
    internal static bool TryGetSyncPoint(uint id, out SyncPoint sp)
    {
        if (SyncPoints.TryGetValue(id, out var found))
        {
            sp = found;
            return true;
        }
        if (_combatStartPoint != null && _combatStartPoint.ChecksumId == id)
        {
            sp = _combatStartPoint;
            return true;
        }
        sp = null!;
        return false;
    }

    /// <summary>
    /// The combat-start anchor, when there's actually somewhere to restart TO: null until the
    /// first turn-start anchor has been captured, and also withheld once it's already the
    /// newest stored point (restoring to "now" is pointless — same moment the picker is
    /// already showing as "current state").
    /// </summary>
    internal static bool TryGetCombatStart(out SyncPoint sp)
    {
        if (_combatStartPoint != null
            && (SyncPoints.Count == 0 || SyncPoints.Keys[SyncPoints.Count - 1] != _combatStartPoint.ChecksumId))
        {
            sp = _combatStartPoint;
            return true;
        }
        sp = null!;
        return false;
    }

    internal static bool TryCreateNativeRestorePlan(
        SyncPoint sp,
        out NativeReplayRestorePlan plan,
        out string detail)
    {
        if (sp == null)
        {
            plan = null!;
            detail = "Sync point was null.";
            return false;
        }

        if (!NativeReplayJournal.TryCreateRestorePlan(
            sp.ChecksumId,
            sp.EventCount,
            out plan,
            out detail))
        {
            return false;
        }

        // Arm before NativeReplayRestorer tears down the current run. The only state kept
        // for the transition is the selected native-journal boundary; the native replay
        // plan itself owns the replay data and no custom snapshot is retained here.
        _pendingNativeRestoreTarget = new NativeRestoreBranchTarget(
            plan.TargetChecksumId,
            plan.EventCount);
        _preserveCombatStartAnchorForReplay = true;
        _replayCombatSetUpObserved = false;
        RetainNativeBranch(_pendingNativeRestoreTarget.Value, "native restore plan creation");
        Log.Write($"[ChecksumHook] Armed native restore branch target checksum={plan.TargetChecksumId} events={plan.EventCount}; retained={SyncPoints.Count} point(s).");
        return true;
    }

    /// <summary>
    /// Retains only metadata that belongs to the selected native replay prefix. This is
    /// deliberately called both before teardown and after the new tracker is subscribed:
    /// the first call makes CombatManager.Reset harmless, while the second call establishes
    /// the final branch boundary for the new combat session before new checkpoints append.
    /// </summary>
    private static void RetainNativeBranch(
        NativeRestoreBranchTarget target,
        string reason)
    {
        var stale = SyncPoints
            .Where(pair => pair.Value.EventCount > target.EventCount
                || pair.Key > target.ChecksumId)
            .Select(pair => pair.Key)
            .ToList();

        foreach (uint checksumId in stale)
            SyncPoints.Remove(checksumId);

        if (_combatStartPoint != null
            && (_combatStartPoint.EventCount > target.EventCount
                || _combatStartPoint.ChecksumId > target.ChecksumId))
        {
            _combatStartPoint = null;
        }

        Log.Write($"[ChecksumHook] Retained native branch ({reason}) through checksum={target.ChecksumId}, events={target.EventCount}; removed={stale.Count}, remaining={SyncPoints.Count}.");
    }

    [Obsolete("Use the asynchronous native replay restore flow via NativeReplayRestorer.")]
    internal static void RestoreTo(SyncPoint sp)
    {
        throw new InvalidOperationException(
            "Direct RestoreTo is not supported; use the asynchronous native replay restore flow via NativeReplayRestorer.");
    }
}

// Subscribe as soon as a run's ChecksumTracker/NetService exists (every peer, every run).
[HarmonyPatch(typeof(RunManager), "InitializeShared")]
public static class PatchRunManagerInitializeShared
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        try
        {
            ChecksumHook.EnsureSubscribed();
            UndoProtocol.EnsureHandlersRegistered();
        }
        catch (Exception ex) { Log.Write($"[ChecksumHook] subscribe ERROR: {ex}"); }
    }
}

// Left Arrow = undo. Singleplayer restores immediately; multiplayer starts a vote.
[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchUndoSyncInput
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode != Key.Left)
            return;
        try { UndoProtocol.RequestUndo(); }
        catch (Exception ex) { Log.Write($"[ChecksumHook] undo key ERROR: {ex}"); }
    }
}
