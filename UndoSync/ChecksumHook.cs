using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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
    public StateSnapshot Snapshot = null!;
    public uint NextActionId;
    public uint NextHookId;
    public List<uint> ChoiceIds = new();

    /// <summary>Game state dump at capture time (NetFullCombatState.ToString) — for restore fidelity verification.</summary>
    public string StateDump = "";

    /// <summary>
    /// The exact bytes ChecksumTracker hashes for this moment (see ChecksumHook.SerializeCurrentState).
    /// StateDump stays the human-readable report used on mismatch; this is what actually proves
    /// byte-exact fidelity, since StateDump's ToString() omits several checksummed fields entirely.
    /// </summary>
    public byte[]? StateBytes;
}

internal static class ChecksumHook
{
    private const int MaxSyncPoints = 50;

    // Sorted by checksum id (ascending). Newest snapshots at the end.
    private static readonly SortedList<uint, SyncPoint> SyncPoints = new();

    // The combat's first turn-start anchor, kept outside SyncPoints so it survives
    // MaxSyncPoints trimming in a long combat — without this, "restart combat" would
    // silently resolve to whatever's now the oldest surviving point instead of the
    // actual start. Captured once in TryStoreSyncPoint, cleared on CombatSetUp (new
    // combat) and in ClearSyncPoints.
    private static SyncPoint? _combatStartPoint;

    private static ChecksumTracker? _subscribedTracker;
    private static CombatManager? _subscribedCombatManager;

    // Set when "After player turn start" fires (OnChecksumGenerated) and consumed when
    // CombatManager.TurnStarted fires for the player side (OnTurnStarted) — see the doc
    // comment on OnChecksumGenerated for why the capture itself is deferred to that point.
    private static uint? _pendingTurnStartId;
    private static string _pendingTurnStartContext = "";

    private static readonly System.Reflection.PropertyInfo? TrackerNextIdProp =
        AccessTools.Property(typeof(ChecksumTracker), "NextId");
    private static readonly System.Reflection.FieldInfo? TrackerChecksumsField =
        AccessTools.Field(typeof(ChecksumTracker), "_checksums");
    private static readonly System.Reflection.FieldInfo? TrackerQueuedRemoteField =
        AccessTools.Field(typeof(ChecksumTracker), "_queuedRemoteChecksums");

    // Change C: ActionQueueSet._wasReset (ActionQueueSet.cs:63) gates PopAction (:562) into a
    // no-op. Normalized alongside the other synchronizer counters in RestoreTo — see the
    // comment there for why it can only ever be false at a restore point.
    private static readonly System.Reflection.FieldInfo? ActionQueueSetWasResetField =
        AccessTools.Field(typeof(ActionQueueSet), "_wasReset");

    // Change B: both buffer cross-peer messages that arrive before the code that consumes
    // them is ready. Cleared unconditionally in RestoreTo — see the comment there for why an
    // idle-queue restore can never leave a live waiter behind in either one.
    private static readonly System.Reflection.FieldInfo? ActionQueueSetActionsWaitingForResumptionField =
        AccessTools.Field(typeof(ActionQueueSet), "_actionsWaitingForResumption");
    private static readonly System.Reflection.FieldInfo? PlayerChoiceSynchronizerReceivedChoicesField =
        AccessTools.Field(typeof(PlayerChoiceSynchronizer), "_receivedChoices");

    internal static void EnsureSubscribed()
    {
        var tracker = RunManager.Instance?.ChecksumTracker;
        if (tracker != null && !ReferenceEquals(tracker, _subscribedTracker))
        {
            if (_subscribedTracker != null)
                _subscribedTracker.ChecksumGenerated -= OnChecksumGenerated;
            tracker.ChecksumGenerated += OnChecksumGenerated;
            _subscribedTracker = tracker;
            SyncPoints.Clear();
            Log.Write($"[ChecksumHook] Subscribed to ChecksumTracker. net={RunManager.Instance?.NetService?.Type} netId={RunManager.Instance?.NetService?.NetId}");
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
            _combatStartPoint = null;
            Log.Write("[ChecksumHook] CombatSetUp — cleared combat-start anchor.");
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] OnCombatSetUp ERROR: {ex}");
        }
    }

    /// <summary>
    /// Captures a sync point under the given checksum id/context: game state, synchronizer
    /// counters, and both state dumps. Shared by the immediate-capture path
    /// (OnChecksumGenerated, for in-play-phase player decisions) and the deferred turn-start
    /// path (OnTurnStarted). StateDump is always recomputed here the same way
    /// SerializeCurrentState computes StateBytes — NetFullCombatState.FromRun(rs, null).ToString(),
    /// justFinishedAction always null (see SerializeCurrentState's doc comment for why) — rather
    /// than trusting a caller-supplied NetFullCombatState, so both capture paths are consistent.
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

        var snapshot = StateSnapshot.Capture();
        if (snapshot == null || snapshot.IsFailed)
        {
            Log.Write($"[ChecksumHook] id={checksumId} capture failed, skipping");
            return;
        }

        var rs = rm.DebugOnlyGetState();
        string stateDump = rs != null ? NetFullCombatState.FromRun(rs, null).ToString() : "";

        var sp = new SyncPoint
        {
            ChecksumId = checksumId,
            Context = context,
            Snapshot = snapshot,
            NextActionId = rm.ActionQueueSet.NextActionId,
            NextHookId = syncr.NextHookId,
            ChoiceIds = new List<uint>(rm.PlayerChoiceSynchronizer.ChoiceIds),
            StateDump = stateDump,
            StateBytes = SerializeCurrentState(),
        };
        SyncPoints[checksumId] = sp;
        while (SyncPoints.Count > MaxSyncPoints)
            SyncPoints.RemoveAt(0);
        Log.Write($"[ChecksumHook] Stored sync point id={checksumId} ({context}) | actionId={sp.NextActionId} hookId={sp.NextHookId} choiceIds=[{string.Join(",", sp.ChoiceIds)}] | total={SyncPoints.Count}");

        // The first turn-start anchor of a combat is the earliest restorable moment — capture
        // it once and keep it outside SyncPoints (see the field's doc comment) so MaxSyncPoints
        // trimming in a long combat can't lose it.
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
    /// Why this needs two comparisons: NetFullCombatState.ToString() (NetFullCombatState.cs:537-640)
    /// prints only COUNTS for piles/potions/relics/orbs and nothing at all for rngSet/relicGrabBag,
    /// while the multiplayer checksum hashes the full Serialize() output. So a self-check built only
    /// on ToString compares a strict subset of the checksummed state — it can (and did: this is the
    /// root cause behind the missing maxPotionCount/rngSet/relicGrabBag capture in StateSnapshot)
    /// report PASS while whole checksummed fields are silently unrestored. The byte comparison below
    /// is authoritative; the ToString diff is kept only as a human-readable "what looks different"
    /// report for the fields it can see.
    /// </summary>
    private static void VerifyRestoreFidelity(SyncPoint sp)
    {
        try
        {
            if (string.IsNullOrEmpty(sp.StateDump)) return;
            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return;

            var nowBytes = SerializeCurrentState();
            bool byteFail = false;
            if (sp.StateBytes != null && nowBytes != null)
            {
                if (sp.StateBytes.AsSpan().SequenceEqual(nowBytes))
                {
                    Log.Write($"[ChecksumHook] RESTORE FIDELITY: PASS — checksum payload byte-identical ({nowBytes.Length} bytes, id={sp.ChecksumId})");
                    return;
                }
                byteFail = true;
                Log.Write($"[ChecksumHook] RESTORE FIDELITY: FAIL — checksum payload differs (captured {sp.StateBytes.Length} bytes vs restored {nowBytes.Length} bytes, id={sp.ChecksumId}); falling back to the ToString field diff below to name what looks different.");
            }
            else
            {
                Log.Write($"[ChecksumHook] RESTORE FIDELITY: byte payload unavailable (captured={sp.StateBytes != null}, restored={nowBytes != null}, id={sp.ChecksumId}) — falling back to ToString comparison only.");
            }

            var now = NetFullCombatState.FromRun(rs, null).ToString();

            // The captured dump embeds the just-finished action id while the recompute
            // passes action=null, so "Last executed ..." lines legitimately differ — excluded.
            static string[] Filter(string dump) =>
            dump.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("Last executed ")).ToArray();

            var captured = Filter(sp.StateDump);
            var restored = Filter(now);
            if (captured.SequenceEqual(restored))
            {
                if (byteFail)
                    Log.Write($"[ChecksumHook] RESTORE FIDELITY: ToString diff found NO differences (id={sp.ChecksumId}) even though the byte payload disagrees above — the real mismatch is in a field ToString() never prints (rngSet / relicGrabBag / pile-potion-relic contents rather than counts).");
                else
                    Log.Write($"[ChecksumHook] RESTORE FIDELITY: PASS — all checksummed state matches capture (id={sp.ChecksumId})");
                return;
            }
            Log.Write($"[ChecksumHook] RESTORE FIDELITY: FAIL — state differs from capture! (id={sp.ChecksumId}, {captured.Length} vs {restored.Length} lines)");
            foreach (var line in captured.Except(restored).Take(20))
                Log.Write($"    captured-only: {line.TrimEnd()}");
            foreach (var line in restored.Except(captured).Take(20))
                Log.Write($"    restored-only: {line.TrimEnd()}");
            // Same line multiset but different order/counts → show positional mismatches.
            int shown = 0;
            for (int i = 0; i < Math.Min(captured.Length, restored.Length) && shown < 12; i++)
            {
                if (captured[i] != restored[i])
                {
                    Log.Write($"    [{i}] captured: {captured[i].TrimEnd()}");
                    Log.Write($"    [{i}] restored: {restored[i].TrimEnd()}");
                    shown++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] fidelity check ERROR: {ex.Message}");
        }
    }

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
        SyncPoints.Clear();
        _pendingTurnStartId = null;
        _pendingTurnStartContext = "";
        _combatStartPoint = null;
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

    // Falls back to _combatStartPoint: by the time the restart-combat button is pressed in a
    // long combat, that anchor may already have been trimmed out of SyncPoints (MaxSyncPoints)
    // even though it is still the vote/restore target UndoProtocol resolves by id.
    internal static bool HasSyncPoint(uint id) =>
        SyncPoints.ContainsKey(id) || (_combatStartPoint != null && _combatStartPoint.ChecksumId == id);

    /// <summary>All stored sync points, newest first (index 0 = current state).</summary>
    internal static List<SyncPoint> SyncPointsNewestFirst()
    {
        var list = new List<SyncPoint>(SyncPoints.Values);
        list.Reverse();
        return list;
    }

    // See HasSyncPoint: same _combatStartPoint fallback, for the same reason.
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

    internal static void RestoreTo(SyncPoint sp)
    {
        Log.Write($">>> [ChecksumHook] RESTORE to checksum id={sp.ChecksumId} ({sp.Context})");
        // A restore invalidates any not-yet-fired turn-start anchor — the turn it belonged to
        // is being rewound away.
        _pendingTurnStartId = null;
        _pendingTurnStartContext = "";
        UndoSyncMod.IsRestoring = true;
        try
        {
            // 1. Game state
            sp.Snapshot.Restore();

            // 2. Synchronizer counters — roll the shared logical clocks back so the
            //    next action/hook/choice/checksum gets the same id on every peer.
            var rm = RunManager.Instance!;
            rm.ActionQueueSet.FastForwardNextActionId(sp.NextActionId);
            rm.ActionQueueSynchronizer.FastForwardHookId(sp.NextHookId);
            rm.PlayerChoiceSynchronizer.FastForwardChoiceIds(new List<uint>(sp.ChoiceIds));

            // 2b. Drop cross-peer message buffers that belong to the timeline this restore
            // discards. Both are safe to clear unconditionally: RestoreTo only ever runs once
            // ActionQueueSet.IsEmpty is true (UndoProtocol.CommitAsync's gate), which means no
            // action anywhere is executing OR paused mid-choice — so neither list can have a
            // live waiter at this moment.
            //  - _actionsWaitingForResumption (ActionQueueSet.cs:50): entries are {oldId, newId}
            //    pairs queued by ResumeActionWithoutSynchronizing (:499-520) when a resume
            //    message arrives before the action it targets has reached
            //    PauseActionForPlayerChoice (:261-269) — the only consumer, matched by
            //    action.Id while that action still sits at the front of its owner's queue. An
            //    action with an unconsumed entry is therefore still occupying its queue's front
            //    slot, which would keep IsEmpty false — so any entry surviving to an idle
            //    restore belongs to an action already gone from every queue: orphan garbage,
            //    not a live pending resume. Leaving it behind is actively dangerous rather than
            //    merely inert — FastForwardNextActionId above rewinds _nextId, so the orphan's
            //    stored newId will be handed out again to an unrelated future action.
            //  - PlayerChoiceSynchronizer._receivedChoices (PlayerChoiceSynchronizer.cs:38):
            //    entries come from WaitForRemoteChoice (:114-147, awaits the entry's
            //    TaskCompletionSource from inside an executing action — ruled out at an idle
            //    restore by the same queue-occupancy argument above) or OnReceivePlayerChoice
            //    (:168-190, inserts an already-completed entry — SetResult already called —
            //    when a choice result arrives before anyone is waiting). Only the latter kind
            //    can survive to an idle restore, and it has no waiter to strand.
            // Cleared via reflection (both are List<T>, i.e. IList) since neither type exposes a
            // public Clear. A non-zero count means the "idle queue at restore" invariant this
            // reasoning rests on didn't hold — evidence worth logging loudly, not silence.
            var actionsWaitingForResumption = ActionQueueSetActionsWaitingForResumptionField?.GetValue(rm.ActionQueueSet) as System.Collections.IList;
            var receivedChoices = PlayerChoiceSynchronizerReceivedChoicesField?.GetValue(rm.PlayerChoiceSynchronizer) as System.Collections.IList;
            int clearedResumptions = actionsWaitingForResumption?.Count ?? 0;
            int clearedChoices = receivedChoices?.Count ?? 0;
            if (clearedResumptions > 0)
                Log.Write($">>> [ChecksumHook] RestoreTo: expected empty, found ActionQueueSet._actionsWaitingForResumption with {clearedResumptions} entry(ies) at an idle-queue restore — clearing (see RestoreTo's Change B comment for why this should be unreachable).");
            if (clearedChoices > 0)
                Log.Write($">>> [ChecksumHook] RestoreTo: expected empty, found PlayerChoiceSynchronizer._receivedChoices with {clearedChoices} entry(ies) at an idle-queue restore — clearing (see RestoreTo's Change B comment for why this should be unreachable).");
            actionsWaitingForResumption?.Clear();
            receivedChoices?.Clear();

            // ActionQueueSet._wasReset (ActionQueueSet.cs:63) is set true ONLY by Reset()
            // (:437), and Reset() is called from exactly one place in the whole game assembly —
            // RunManager.CleanUp (RunManager.cs:1557), which also empties _actionQueues
            // wholesale. A restore always lands on a still-live, mid-combat play phase with
            // real per-player queues, so _wasReset can only be false there — but nothing else
            // forces it back after a restore, so force it explicitly rather than trust it
            // survived the discarded timeline correctly. Left true, it would make PopAction
            // (:562) silently no-op for every action executed after the restore.
            if (ActionQueueSetWasResetField != null)
                ActionQueueSetWasResetField.SetValue(rm.ActionQueueSet, false);

            // 3. Checksum tracker: next checksum reuses id ChecksumId+1, and stale
            //    tracked entries must go or the host would compare a reused id against
            //    a pre-restore state. (Queued remotes too — safe while idle.)
            var tracker = rm.ChecksumTracker;
            TrackerNextIdProp?.SetValue(tracker, sp.ChecksumId + 1);
            (TrackerChecksumsField?.GetValue(tracker) as System.Collections.IList)?.Clear();
            (TrackerQueuedRemoteField?.GetValue(tracker) as System.Collections.IList)?.Clear();

            // 4. Fidelity self-check: recompute the game's own state digest and compare
            //    with what was captured. PASS = every checksummed field restored
            //    byte-identically — a local proof, works in singleplayer too. FAIL logs
            //    exactly which lines differ (= what the snapshot is missing).
            VerifyRestoreFidelity(sp);

            // 5. Drop sync points that are now in the future.
            var stale = SyncPoints.Keys.Where(k => k > sp.ChecksumId).ToList();
            foreach (var k in stale)
                SyncPoints.Remove(k);

            // 6. UI
            var cs = UndoSyncMod.GetCombatState();
            if (cs != null)
                UiRefresh.RefreshAll(cs);

            Log.Write($">>> [ChecksumHook] RESTORE complete. nextChecksumId={sp.ChecksumId + 1} nextActionId={sp.NextActionId} nextHookId={sp.NextHookId} clearedResumptions={clearedResumptions} clearedChoices={clearedChoices} | remaining sync points={SyncPoints.Count}");
        }
        catch (Exception ex)
        {
            Log.Write($">>> [ChecksumHook] RESTORE ERROR: {ex}");
        }
        finally
        {
            UndoSyncMod.IsRestoring = false;
        }
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
