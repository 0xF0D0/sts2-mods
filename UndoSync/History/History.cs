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

    private static bool CanRecord => Available && !Replay.SuppressNative && CombatManager.Instance.IsInProgress;
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
        // Only the proposer freezes a prefix. Other peers receive this record.
        var replay = Unpack<CombatReplay>(_header);
        replay.events = Events.Take(count).Select(Unpack<CombatReplayEvent>).ToList();
        replay.checksumData.Clear();
        return replay;
    }

    internal static byte[] Export(int count)
    {
        var writer = new PacketWriter();
        writer.Write(Freeze(count));
        var targets = Boundaries.Where(t => t.EventCount <= count).ToArray();
        writer.WriteInt(targets.Length);
        foreach (var target in targets)
        {
            writer.WriteInt(target.EventCount);
            writer.WriteString(target.Label);
        }
        return writer.Buffer[..writer.BytePosition];
    }

    internal static (CombatReplay Replay, Target[] Targets) Import(byte[] bytes)
    {
        var reader = new PacketReader(); reader.Reset(bytes);
        var replay = reader.Read<CombatReplay>();
        var targets = new Target[reader.ReadInt()];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = new Target(reader.ReadInt(), reader.ReadString());
        return (replay, targets);
    }

    internal static void Commit(CombatReplay replay, IReadOnlyList<Target> targets)
    {
        var header = Unpack<CombatReplay>(Pack(replay));
        header.events.Clear(); header.checksumData.Clear();
        _header = Pack(header);
        Events.Clear();
        Events.AddRange(replay.events.Select(Pack));
        Boundaries.Clear(); Boundaries.AddRange(targets);
        Hooks.InstallWriter(Unpack<CombatReplay>(Pack(replay)));
        Hooks.Log($"HISTORY_COMMIT events={Count} targets={Boundaries.Count} source=proposer");
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
