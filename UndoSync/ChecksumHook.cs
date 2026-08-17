using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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
}

internal static class ChecksumHook
{
    private const int MaxSyncPoints = 50;

    // Sorted by checksum id (ascending). Newest snapshots at the end.
    private static readonly SortedList<uint, SyncPoint> SyncPoints = new();

    private static ChecksumTracker? _subscribedTracker;

    private static readonly System.Reflection.PropertyInfo? TrackerNextIdProp =
        AccessTools.Property(typeof(ChecksumTracker), "NextId");
    private static readonly System.Reflection.FieldInfo? TrackerChecksumsField =
        AccessTools.Field(typeof(ChecksumTracker), "_checksums");
    private static readonly System.Reflection.FieldInfo? TrackerQueuedRemoteField =
        AccessTools.Field(typeof(ChecksumTracker), "_queuedRemoteChecksums");

    internal static void EnsureSubscribed()
    {
        var tracker = RunManager.Instance?.ChecksumTracker;
        if (tracker == null || ReferenceEquals(tracker, _subscribedTracker))
            return;
        if (_subscribedTracker != null)
            _subscribedTracker.ChecksumGenerated -= OnChecksumGenerated;
        tracker.ChecksumGenerated += OnChecksumGenerated;
        _subscribedTracker = tracker;
        SyncPoints.Clear();
        Log.Write($"[ChecksumHook] Subscribed to ChecksumTracker. net={RunManager.Instance?.NetService?.Type} netId={RunManager.Instance?.NetService?.NetId}");
    }

    /// <summary>
    /// Fires on every peer at the same logical moments (after each game action + turn
    /// boundaries), with the same incrementing id. We snapshot the full combat state
    /// and the synchronizer counters so both can be rolled back together.
    /// Only play-phase moments are stored — same policy as the original single-player
    /// mod, which only ever snapshotted player-initiated actions during the play phase.
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
            // The turn-start boundary checksum can fire a beat before the synchronizer
            // flips to PlayPhase (slower with more peers) — it is still the same
            // logical moment on every peer, and it is the anchor that lets the first
            // action of a turn be undone.
            bool turnStartAnchor = context.StartsWith("After player turn start");
            if (!turnStartAnchor && syncr.CombatState != ActionSynchronizerCombatState.PlayPhase) return;

            var snapshot = StateSnapshot.Capture();
            if (snapshot == null || snapshot.IsFailed)
            {
                Log.Write($"[ChecksumHook] id={data.id} capture failed, skipping");
                return;
            }

            var sp = new SyncPoint
            {
                ChecksumId = data.id,
                Context = context,
                Snapshot = snapshot,
                NextActionId = RunManager.Instance!.ActionQueueSet.NextActionId,
                NextHookId = syncr.NextHookId,
                ChoiceIds = new List<uint>(RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds),
                StateDump = fullState.ToString(),
            };
            SyncPoints[data.id] = sp;
            while (SyncPoints.Count > MaxSyncPoints)
                SyncPoints.RemoveAt(0);
            Log.Write($"[ChecksumHook] Stored sync point id={data.id} ({context}) | actionId={sp.NextActionId} hookId={sp.NextHookId} choiceIds=[{string.Join(",", sp.ChoiceIds)}] | total={SyncPoints.Count}");
        }
        catch (Exception ex)
        {
            Log.Write($"[ChecksumHook] OnChecksumGenerated ERROR: {ex}");
        }
    }

    private static void VerifyRestoreFidelity(SyncPoint sp)
    {
        try
        {
            if (string.IsNullOrEmpty(sp.StateDump)) return;
            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return;
            var now = NetFullCombatState.FromRun(rs, null).ToString();

            // The captured dump embeds the just-finished action id while the recompute
            // passes action=null, so "Last executed ..." lines legitimately differ — excluded.
            static string[] Filter(string dump) =>
            dump.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("Last executed ")).ToArray();

            var captured = Filter(sp.StateDump);
            var restored = Filter(now);
            if (captured.SequenceEqual(restored))
            {
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

    internal static void ClearSyncPoints() => SyncPoints.Clear();

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

    internal static bool HasSyncPoint(uint id) => SyncPoints.ContainsKey(id);

    /// <summary>All stored sync points, newest first (index 0 = current state).</summary>
    internal static List<SyncPoint> SyncPointsNewestFirst()
    {
        var list = new List<SyncPoint>(SyncPoints.Values);
        list.Reverse();
        return list;
    }

    internal static bool TryGetSyncPoint(uint id, out SyncPoint sp)
    {
        if (SyncPoints.TryGetValue(id, out var found))
        {
            sp = found;
            return true;
        }
        sp = null!;
        return false;
    }

    internal static void RestoreTo(SyncPoint sp)
    {
        Log.Write($">>> [ChecksumHook] RESTORE to checksum id={sp.ChecksumId} ({sp.Context})");
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

            Log.Write($">>> [ChecksumHook] RESTORE complete. nextChecksumId={sp.ChecksumId + 1} nextActionId={sp.NextActionId} nextHookId={sp.NextHookId} | remaining sync points={SyncPoints.Count}");
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
