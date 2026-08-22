using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

[ModInitializer("Initialize")]
public static class UndoSyncMod
{
    /// <summary>Set while a restore is writing state, so capture hooks stay quiet.</summary>
    internal static bool IsRestoring;

    /// <summary>Process-wide count of every time <see cref="IsActionQueueIdle"/> observed
    /// ActionQueueSet.IsEmpty (ActionQueueSet.cs:65-75) report false while the literal queued-action
    /// count it read via reflection was 0 — i.e. every time the ActionQueueSet.IsEmpty staleness defect
    /// (see IsActionQueueIdle's own doc comment) was actually caught happening, not merely possible.
    /// Never reset per-combat (same convention as UiRefresh.OrbInvariantViolationCount /
    /// StateSnapshot.ShadowContainersCopied) — callers that want a per-combat figure snapshot this
    /// before and diff after, the way MpFuzz.cs already does for those two counters.
    /// </summary>
    internal static int StaleIsEmptyObservations;

    /// <summary>Bounds the "[UndoSync] stale ActionQueueSet.IsEmpty" log line in
    /// <see cref="IsActionQueueIdle"/> to once per combat — <see cref="StaleIsEmptyObservations"/> keeps
    /// counting every occurrence regardless, since CanUndoRedo/DescribeUndoRedoBlockers can both be
    /// polled many times per second while the game is stuck. Internal (not private): PatchCombatReset
    /// below resets this. Reset there because it patches CombatManager.Reset(bool) (CombatManager.cs:
    /// 1184 — "Reset the combat manager to prepare for the next combat").</summary>
    internal static bool _loggedStaleIsEmptyThisCombat;

    public static void Initialize()
    {
        var harmony = new Harmony("com.beomsu.undosync");
        harmony.PatchAll(typeof(UndoSyncMod).Assembly);
        Log.Write("UndoSync initialized (Left Arrow = undo)");

        // Dormant unless --undosync-fuzz is on the command line; see UndoFuzz.cs. Called here
        // (mod init, called from ModManager.Initialize inside NGame.GameStartup — NGame.cs's
        // OneTimeInitialization.ExecuteVeryEarly, awaited at the top of GameStartup) rather than
        // patched onto some later hook, because UndoFuzz itself awaits NGame.Instance.GameStartupComplete
        // before doing anything — the least intrusive place to kick that wait off is right where the
        // mod already gets control.
        UndoFuzz.MaybeStart();

        // Dormant unless --undosync-mpfuzz is on the command line; see MpFuzz.cs. Same call-site
        // reasoning as UndoFuzz.MaybeStart() immediately above — MpFuzz itself awaits
        // NGame.Instance.GameStartupComplete before doing anything.
        MpFuzz.MaybeStart();
    }

    internal static CombatState? GetCombatState() =>
        CombatManager.Instance?.DebugOnlyGetState();

    private static readonly FieldInfo? FPlayQueue =
        AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");

    /// <summary>Reflection handle for ActionQueueSet's private field `_isInCombat` (bool,
    /// ActionQueueSet.cs:56) — the field that gates two of GetReadyAction's silent front-of-queue skips
    /// (ActionQueueSet.cs:200 "in combat but action is NonCombat", :205-215 "not in combat but action is
    /// Combat/CombatPlayPhaseOnly"). ActionQueueSet is public and known at compile time, so — like
    /// FPlayQueue immediately above — this can be a plain static readonly field rather than the
    /// runtime-typed lookup TryGetLiteralQueuedActionCounts needs for the private nested ActionQueue
    /// type. Used only by IsActionQueueIdle's non-idle detail string, to tell those two skip reasons
    /// apart from the other two GetReadyAction skip reasons (isPaused+CombatPlayPhaseOnly at :218, and
    /// GatheringPlayerChoice at :228) without another experiment round trip.</summary>
    private static readonly FieldInfo? FIsInCombat =
        AccessTools.Field(typeof(ActionQueueSet), "_isInCombat");

    /// <summary>Caps each queued action's ToString() in <see cref="DescribeQueuedAction"/> to this many
    /// characters (plus a "… (truncated)" suffix when clipped) — same truncate-with-suffix convention as
    /// UndoFuzz.RecordGameError (UndoFuzz.cs:794). Without this, a queue that has piled up many actions
    /// (or one whose ToString() override embeds a long nested description, e.g. PlayCardAction.ToString()
    /// interpolating a CardModel, PlayCardAction.cs:141-144) could blow the stall-diagnostic detail string
    /// out to an unbounded single log line.</summary>
    private const int MaxActionDescLength = 100;

    /// <summary>
    /// Formats one queued GameAction for the stall diagnostic built by
    /// <see cref="TryGetLiteralQueuedActionCounts"/>: `Id`, `State`, `ActionType` — all public on
    /// GameAction (GameAction.cs:56, :46, :54 respectively; `OwnerId` is covered separately, by the
    /// owning queue's own ownerId field) — plus the action's own ToString() truncated to
    /// <see cref="MaxActionDescLength"/> chars. `Id` is `uint?` (GameAction.cs:56, set once by
    /// OnEnqueued when the action is first enqueued) and renders as "?" rather than throwing if null.
    /// ToString() itself is wrapped separately: it is a virtual call into arbitrary subclass code (e.g.
    /// PlayCardAction.ToString(), PlayCardAction.cs:141-144, which dereferences NetCombatCard) that this
    /// diagnostic must survive even if the action is in some half-formed state — degrading that one
    /// action's description rather than losing the whole diagnostic to
    /// TryGetLiteralQueuedActionCounts's outer catch.
    /// </summary>
    private static string DescribeQueuedAction(GameAction action)
    {
        string desc;
        try
        {
            desc = action.ToString() ?? "";
        }
        catch (System.Exception ex)
        {
            desc = $"<ToString threw {ex.GetType().Name}>";
        }
        if (desc.Length > MaxActionDescLength)
            desc = desc.Substring(0, MaxActionDescLength) + "… (truncated)";
        return $"id={action.Id?.ToString() ?? "?"} state={action.State} type={action.ActionType} {desc}";
    }

    /// <summary>
    /// Reads the literal number of queued actions across every player's queue in
    /// <paramref name="queues"/>, bypassing ActionQueueSet.IsEmpty entirely. Reuses UndoFuzz's
    /// FActionQueues/GetActionQueueElementField reflection handles (widened from private to internal
    /// for this — see UndoFuzz.cs:1622-1671) rather than re-declaring a second copy of the same
    /// AccessTools.Field calls: ActionQueueSet._actionQueues is `private readonly List&lt;ActionQueue&gt;`
    /// (ActionQueueSet.cs:48), and its element type `ActionQueue` is itself a private nested class
    /// (ActionQueueSet.cs:22-37) that cannot be named with typeof(...) from outside ActionQueueSet, so
    /// its fields can only be reached off a runtime instance's GetType(). `actions` (ActionQueueSet.cs:
    /// 24) is declared `List&lt;GameAction&gt;`, and GameAction itself IS public (GameAction.cs:34), so
    /// once that list is reached via reflection its elements need no further reflection to read — they
    /// are cast directly to GameAction below, for DescribeQueuedAction.
    ///
    /// Why this is needed at all: ActionQueueSet.IsEmpty (ActionQueueSet.cs:65-75) only reports whether
    /// a TaskCompletionSource has been completed. That completion source is completed by
    /// CheckIfQueuesEmpty() (ActionQueueSet.cs:620-635), which IS called from PopAction (:600-602, the
    /// "action finished executing normally" exit), CombatEnded (:403), CancelNonExecutingActionsForPlayer
    /// (:460), and CancelNonExecutingActionsOfType (:493) — but NOT from
    /// StartCancellingAllPlayerDrivenCombatActions (:331-349), which cancels and removes actions from
    /// every queue on every EndTurnPhaseOne transition (ActionQueueSynchronizer.cs:129-131), nor from the
    /// Canceled-head drop inside GetReadyAction (:190-193). Either of those two paths can remove the LAST
    /// queued action from every queue while IsEmpty stays false, permanently, until some later
    /// enqueue+pop cycle happens to complete a freshly re-armed completion source
    /// (EnqueueWithoutSynchronizing, :103-108) — i.e. IsEmpty can go permanently stale exactly on the
    /// end-of-turn path every combat takes.
    ///
    /// Returns null (never throws) if the _actionQueues field, or either per-element field
    /// (`ownerId`/`actions`), is missing or not the expected shape on any queue — callers must fall back
    /// to ActionQueueSet.IsEmpty in that case rather than trust a partial count. The richer per-owner
    /// `actionDetail` piece degrades independently and more granularly: if a given queue's `isPaused`/
    /// `isCancellingPlayerDrivenCombatActions` fields (ActionQueueSet.cs:34, :30) are unreadable, only
    /// THAT owner's segment in `actionDetail` falls back to the same plain "owner{id}={count}" shape
    /// `breakdown` always uses, rather than losing the count for every queue.
    /// </summary>
    /// <returns>(total queued-action count, per-owner breakdown formatted as "owner{id}={count}" pairs
    /// separated by spaces, e.g. "owner1=2 owner1001=1" — unchanged from before, still what the stale-
    /// IsEmpty log below uses, per-owner detail formatted as "owner{id}[paused={bool}
    /// cancelling={bool}]={action; action}" pairs separated by ", ", each action described by
    /// <see cref="DescribeQueuedAction"/> and empty braces when the queue holds nothing), or null if
    /// reflection failed.</returns>
    internal static (int total, string breakdown, string actionDetail)? TryGetLiteralQueuedActionCounts(ActionQueueSet queues)
    {
        try
        {
            if (UndoFuzz.FActionQueues?.GetValue(queues) is not System.Collections.IEnumerable rawQueues)
                return null;

            int total = 0;
            var parts = new List<string>();
            var detailParts = new List<string>();
            foreach (var q in rawQueues)
            {
                if (q == null) continue;
                var t = q.GetType();
                object? ownerIdVal = UndoFuzz.GetActionQueueElementField(t, "ownerId")?.GetValue(q);
                object? actionsVal = UndoFuzz.GetActionQueueElementField(t, "actions")?.GetValue(q);
                if (actionsVal is not System.Collections.IEnumerable actionsEnumerable)
                    return null; // unexpected shape on this queue - do not report a partial count

                int count = 0;
                var actionDescs = new List<string>();
                foreach (var a in actionsEnumerable)
                {
                    count++;
                    actionDescs.Add(a is GameAction ga ? DescribeQueuedAction(ga) : "?");
                }
                total += count;
                string ownerLabel = ownerIdVal?.ToString() ?? "?";
                parts.Add($"owner{ownerLabel}={count}");

                object? isPausedVal = UndoFuzz.GetActionQueueElementField(t, "isPaused")?.GetValue(q);
                object? isCancellingVal =
                    UndoFuzz.GetActionQueueElementField(t, "isCancellingPlayerDrivenCombatActions")?.GetValue(q);
                detailParts.Add(isPausedVal is bool paused && isCancellingVal is bool cancelling
                    ? $"owner{ownerLabel}[paused={paused} cancelling={cancelling}]={{{string.Join("; ", actionDescs)}}}"
                    : $"owner{ownerLabel}={count}");
            }
            return (total, string.Join(" ", parts), string.Join(", ", detailParts));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ground-truth idle check for <paramref name="queues"/>, preferred over ActionQueueSet.IsEmpty
    /// alone: idle means the literal queued-action count from
    /// <see cref="TryGetLiteralQueuedActionCounts"/> is exactly 0. This is safe because an executing
    /// action is only removed from its queue by PopAction when it finishes (ActionQueueSet.cs:560-580,
    /// via the completion callback wired up in OnEnqueued), so it is still counted while running; and an
    /// action paused for player choice stays in its queue in state GatheringPlayerChoice
    /// (PauseActionForPlayerChoice, ActionQueueSet.cs:261-269; GetReadyAction's own check at :223-227),
    /// so it is likewise still counted. A literal count of 0 therefore means nothing is queued,
    /// executing, or awaiting a player choice — see TryGetLiteralQueuedActionCounts's own doc comment for
    /// why ActionQueueSet.IsEmpty cannot be trusted to mean the same thing.
    ///
    /// Falls back to queues.IsEmpty when reflection fails (TryGetLiteralQueuedActionCounts returns
    /// null), and never throws.
    ///
    /// When IsEmpty disagrees with the literal count — IsEmpty reports non-empty while literal is 0 —
    /// that is the ActionQueueSet.IsEmpty staleness defect happening live, right now, on this call. Each
    /// occurrence bumps <see cref="StaleIsEmptyObservations"/>; the first one THIS combat also logs a
    /// line naming the mechanism (bounded by <see cref="_loggedStaleIsEmptyThisCombat"/>, reset by
    /// PatchCombatReset below) so a run can prove the mechanism actually fired, not merely that it could.
    /// </summary>
    /// <param name="queues">RunManager.Instance.ActionQueueSet — already null-checked by the caller.</param>
    /// <param name="detail">
    /// "" when idle. Otherwise a blocker token naming EVERY signal GetReadyAction's four silent
    /// front-of-queue skips (ActionQueueSet.cs:179-245: :200 in-combat/NonCombat, :205-215 not-in-combat/
    /// Combat-or-CombatPlayPhaseOnly, :218 isPaused+CombatPlayPhaseOnly, :228 GatheringPlayerChoice)
    /// need to be told apart from the log alone: IsEmpty, ActionQueueSet._isInCombat, the literal count,
    /// and — per owner — isPaused/isCancellingPlayerDrivenCombatActions plus every queued action's Id/
    /// State/ActionType/ToString() (via TryGetLiteralQueuedActionCounts's `actionDetail`). Example:
    /// "actionQueue not empty (IsEmpty=False, inCombat=True, literal=1: owner1[paused=False
    /// cancelling=False]={id=45 state=GatheringPlayerChoice type=CombatPlayPhaseOnly PlayCardAction
    /// card: CARD.X index: 2 targetid: 7}, owner1001[paused=False cancelling=False]={})". If reflection
    /// for the per-owner detail failed, that owner's segment falls back to "owner{id}={count}"; if
    /// _isInCombat itself is unreadable, inCombat renders as "?"; if the whole literal count is
    /// unavailable, the token falls back to naming IsEmpty alone.
    /// </param>
    internal static bool IsActionQueueIdle(ActionQueueSet queues, out string detail)
    {
        bool isEmpty = queues.IsEmpty;
        var literal = TryGetLiteralQueuedActionCounts(queues);

        if (literal == null)
        {
            detail = isEmpty ? "" : "actionQueue not empty (IsEmpty=false, literal unavailable — reflection failed)";
            return isEmpty;
        }

        var (total, breakdown, actionDetail) = literal.Value;
        bool idle = total == 0;

        if (!isEmpty && idle)
        {
            StaleIsEmptyObservations++;
            if (!_loggedStaleIsEmptyThisCombat)
            {
                _loggedStaleIsEmptyThisCombat = true;
                Log.Write("[UndoSync] stale ActionQueueSet.IsEmpty: IsEmpty=false while the literal "
                    + $"queued-action count is 0 ({(breakdown.Length == 0 ? "no queues" : breakdown)}). "
                    + "StartCancellingAllPlayerDrivenCombatActions (ActionQueueSet.cs:331-349, runs on "
                    + "every EndTurnPhaseOne transition, ActionQueueSynchronizer.cs:129-131) removed the "
                    + "last queued action without calling CheckIfQueuesEmpty (ActionQueueSet.cs:620-635) "
                    + "— only PopAction (:600-602) reliably reaches it on the common path. "
                    + $"StaleIsEmptyObservations={StaleIsEmptyObservations} this process.");
            }
        }

        if (idle)
        {
            detail = "";
        }
        else
        {
            // FIsInCombat: ActionQueueSet._isInCombat (ActionQueueSet.cs:56), read independently of
            // TryGetLiteralQueuedActionCounts's own reflection — a failure here only blanks this one
            // token, it cannot cost the count or the per-action detail computed above.
            string inCombatDesc = FIsInCombat?.GetValue(queues) is bool inCombat ? inCombat.ToString() : "?";
            // actionDetail is empty only when there were no queues at all, in which case total is also
            // 0 and idle is already true above — this fallback to the plain owner=count breakdown is
            // therefore unreachable in practice, kept only so a future change to that invariant degrades
            // instead of printing a blank per-action section.
            string ownerSection = actionDetail.Length == 0 ? breakdown : actionDetail;
            detail = $"actionQueue not empty (IsEmpty={isEmpty}, inCombat={inCombatDesc}, literal={total}: {ownerSection})";
        }
        return idle;
    }

    /// <summary>
    /// Undo may only start from an idle player play phase: restoring while actions
    /// or the enemy turn are executing leaves their async flows running against
    /// rewound state.
    /// </summary>
    internal static bool CanUndoRedo()
    {
        if (IsRestoring) return false;

        var cm = CombatManager.Instance;
        var cs = GetCombatState();
        if (cm == null || cs == null || !cm.IsInProgress) return false;
        if (cs.CurrentSide != CombatSide.Player) return false;

        var syncr = RunManager.Instance?.ActionQueueSynchronizer;
        if (syncr == null || syncr.CombatState != ActionSynchronizerCombatState.PlayPhase) return false;
        if (NGame.Instance?.Transition?.InTransition == true) return false;

        var queues = RunManager.Instance?.ActionQueueSet;
        if (queues == null || !IsActionQueueIdle(queues, out _)) return false;

        // the visual card-play queue drains slightly behind the action queue
        if (NCardPlayQueue.Instance is { } playQueue &&
            FPlayQueue?.GetValue(playQueue) is System.Collections.IList pending && pending.Count > 0)
            return false;

        return true;
    }

    /// <summary>
    /// Diagnostic twin of <see cref="CanUndoRedo"/>: re-checks the exact same conditions, in the exact
    /// same order, using the exact same field/property accesses (including the FPlayQueue reflection
    /// field for the NCardPlayQueue visual queue) — but instead of returning false on the first
    /// blocking condition, it keeps going and appends a token for every condition that is currently
    /// blocking, so the two methods can never disagree about what they inspect.
    ///
    /// Exists because a stall that only ever logs "stuck waiting for idle player Play phase" gives no
    /// way to tell WHICH of CanUndoRedo's several conditions was still false when the wait gave up —
    /// this method is the log's answer to "stuck on what?" (see UndoFuzz.WaitForIdleOurTurnAsync,
    /// which calls this on timeout).
    /// </summary>
    internal static string DescribeUndoRedoBlockers()
    {
        var blockers = new List<string>();

        if (IsRestoring) blockers.Add("IsRestoring");

        var cm = CombatManager.Instance;
        var cs = GetCombatState();
        if (cm == null) blockers.Add("CombatManager null");
        if (cs == null) blockers.Add("CombatState null");
        if (cm != null && !cm.IsInProgress) blockers.Add("combat not in progress");
        if (cs != null && cs.CurrentSide != CombatSide.Player) blockers.Add($"side={cs.CurrentSide}");

        var syncr = RunManager.Instance?.ActionQueueSynchronizer;
        if (syncr == null) blockers.Add("synchronizer null");
        if (syncr != null && syncr.CombatState != ActionSynchronizerCombatState.PlayPhase) blockers.Add($"syncState={syncr.CombatState}");
        if (NGame.Instance?.Transition?.InTransition == true) blockers.Add("in transition");

        var queues = RunManager.Instance?.ActionQueueSet;
        if (queues == null) blockers.Add("actionQueues null");
        else if (!IsActionQueueIdle(queues, out var queueDetail)) blockers.Add(queueDetail);

        // the visual card-play queue drains slightly behind the action queue
        if (NCardPlayQueue.Instance is { } playQueue &&
            FPlayQueue?.GetValue(playQueue) is System.Collections.IList pending && pending.Count > 0)
            blockers.Add($"cardPlayVisualQueue={pending.Count}");

        return blockers.Count == 0 ? "none (CanUndoRedo would return true)" : string.Join(", ", blockers);
    }
}

// Combat over — snapshots and any in-flight proposal are meaningless now.
[HarmonyPatch(typeof(CombatManager), "Reset", new[] { typeof(bool) })]
public static class PatchCombatReset
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ChecksumHook.ClearSyncPoints();
        UndoProtocol.ResetOnCombatEnd();
        // Bounds UndoSyncMod.IsActionQueueIdle's "[UndoSync] stale ActionQueueSet.IsEmpty" log line to
        // once per combat rather than once per process.
        UndoSyncMod._loggedStaleIsEmptyThisCombat = false;
    }
}
