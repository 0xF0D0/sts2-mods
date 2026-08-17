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
