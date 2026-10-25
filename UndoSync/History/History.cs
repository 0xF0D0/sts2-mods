using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace UndoSync;

internal static class History
{
    internal sealed record Target(int EventCount, string Label);
    private static byte[]? _header;
    private static readonly List<byte[]> Events = new();
    private static readonly List<Target> Boundaries = new();
    internal static int Count => Events.Count;
    internal static bool Available => _header is not null;
    internal static IReadOnlyList<Target> Targets => Boundaries.Where(t => t.EventCount < Count).Reverse().ToArray();

    internal static void Begin(CombatReplay replay)
    {
        Clear();
        try { _header = Pack(replay); Hooks.Log("HISTORY_BEGIN"); }
        catch (Exception exception) { Invalidate(exception); }
    }

    internal static void Clear() { _header = null; Events.Clear(); Boundaries.Clear(); }

    internal static void MarkBoundary()
    {
        if (Boundaries.Count > 0 && Boundaries[^1].EventCount == Count) return;
        int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        Boundaries.Add(new Target(Count, $"Turn {round} · event {Count}"));
        Hooks.Log($"BOUNDARY events={Count}");
    }

    internal static void RecordAction(GameAction action)
    {
        if (!CanRecord) return;
        try
        {
            CombatReplayEvent entry = action is GenericHookGameAction hook
                ? new() { eventType = CombatReplayEventType.HookAction, playerId = hook.OwnerId,
                    hookId = hook.HookId, gameActionType = hook.ActionType }
                : action.RecordableToReplay
                    ? new() { eventType = CombatReplayEventType.GameAction, playerId = action.OwnerId,
                        action = action.ToNetAction() }
                    : throw new InvalidOperationException($"Unrecordable action {action.GetType().Name}");
            Events.Add(Pack(entry));
        }
        catch (Exception exception) { Invalidate(exception); }
    }

    internal static void RecordResume(uint id) => Record(new CombatReplayEvent
        { eventType = CombatReplayEventType.ResumeAction, actionId = id });

    internal static void RecordChoice(Player player, uint id, NetPlayerChoiceResult result) =>
        Record(new CombatReplayEvent { eventType = CombatReplayEventType.PlayerChoice,
            playerId = player.NetId, choiceId = id, playerChoiceResult = result });

    private static bool CanRecord => Available && !Network.Executing && CombatManager.Instance.IsInProgress;
    private static void Record(CombatReplayEvent entry)
    {
        if (!CanRecord) return;
        try { Events.Add(Pack(entry)); }
        catch (Exception exception) { Invalidate(exception); }
    }

    // Read-only UI metadata from native recorded actions; no extra snapshot store.
    internal static string DescribeRange(int first, int end)
    {
        foreach (var bytes in Events.Skip(first).Take(end - first))
        {
            var entry = Unpack<CombatReplayEvent>(bytes);
            if (entry.action is NetPlayCardAction play) return $"PlayCardAction card: {play.modelId}";
            if (entry.action is not null) return entry.action.GetType().Name;
        }
        return "After player turn start";
    }

    internal static CombatReplay Freeze(int count)
    {
        if (_header is null || count < 0 || count >= Count)
            throw new InvalidOperationException("Undo target is no longer available.");
        // Host owns the stable boundary. Remote peers may have received a burst
        // of events without observing the same two idle frames locally.
        var replay = Unpack<CombatReplay>(_header);
        replay.events = Events.Take(count).Select(Unpack<CombatReplayEvent>).ToList();
        replay.checksumData.Clear();
        return replay;
    }

    internal static void Commit(CombatReplay replay)
    {
        int count = replay.events.Count;
        Events.RemoveRange(count, Count - count);
        Boundaries.RemoveAll(t => t.EventCount > count);
        Hooks.InstallWriter(Unpack<CombatReplay>(Pack(replay)));
        Hooks.Log($"HISTORY_COMMIT events={count}");
    }

    private static byte[] Pack<T>(T value) where T : IPacketSerializable
    {
        var writer = new PacketWriter(); writer.Write(value);
        return writer.Buffer[..writer.BytePosition];
    }

    private static T Unpack<T>(byte[] bytes) where T : IPacketSerializable, new()
    {
        var reader = new PacketReader(); reader.Reset(bytes); return reader.Read<T>();
    }

    private static void Invalidate(Exception exception)
    {
        Clear(); Hooks.Log($"HISTORY_FAIL {exception}");
        if (Network.Busy) Network.Fail("History capture failed.");
    }
}
