using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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
        if (queues == null || !queues.IsEmpty) return false;

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
        if (queues != null && !queues.IsEmpty) blockers.Add("actionQueue not empty");

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
    }
}
