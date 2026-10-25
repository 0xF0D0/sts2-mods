using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

public enum UndoMinimalOp { Request, Propose, Vote, Start, Done, Resume, Cancel, StartReceived }

// Discovered by the game's GetSubtypesInMods, as in b770 UndoSync's messages.
public struct UndoMinimalMessage : INetMessage
{
    public UndoMinimalOp Op;
    public string Session;
    public int Target;
    public ulong Proposer;
    public bool Accepted;
    public uint Checksum;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteEnum(Op); writer.WriteString(Session ?? ""); writer.WriteInt(Target); writer.WriteULong(Proposer);
        writer.WriteBool(Accepted); writer.WriteUInt(Checksum);
    }
    public void Deserialize(PacketReader reader)
    {
        Op = reader.ReadEnum<UndoMinimalOp>(); Session = reader.ReadString(); Target = reader.ReadInt(); Proposer = reader.ReadULong();
        Accepted = reader.ReadBool(); Checksum = reader.ReadUInt();
    }
}

internal static class Network
{
    private sealed class Operation
    {
        internal required string Id;
        internal required int Target;
        internal required ulong Proposer;
        internal required HashSet<ulong> Peers;
        internal readonly Dictionary<ulong, bool> Votes = new();
        internal readonly Dictionary<ulong, uint> Done = new();
        internal readonly HashSet<ulong> StartReceipts = new();
        internal readonly CancellationTokenSource Cancel = new();
        internal CombatReplay? Plan;
        internal bool Started, StartRequested, ResumeRequested, LocalVoted;
        internal string? Failure;
        internal ulong Deadline = Time.GetTicksMsec() + 30_000;
    }

    private static INetGameService? _service;
    private static Operation? _operation;
    private static bool _isHost;
    internal static bool LocalVoted => _operation?.LocalVoted == true;
    internal static int Target => _operation?.Target ?? 0;
    internal static bool Busy => _operation is not null;
    internal static bool Executing => _operation?.Started == true;
    internal static bool CanVote => _operation is { Started: false, LocalVoted: false } && Hooks.IsIdle();
    internal static string Status { get; private set; } = "Left Arrow: undo history";

    internal static void Attach(INetGameService service)
    {
        if (ReferenceEquals(_service, service)) return;
        Detach();
        _service = service;
        _isHost = service.Type is NetGameType.Host or NetGameType.Singleplayer;
        service.RegisterMessageHandler<UndoMinimalMessage>(Receive);
        service.Disconnected += Disconnected;
        Hooks.Log($"NETWORK_ATTACH role={service.Type} id={service.NetId}");
    }

    internal static void Detach()
    {
        if (_service is null) return;
        _service.UnregisterMessageHandler<UndoMinimalMessage>(Receive);
        _service.Disconnected -= Disconnected;
        _service = null;
    }

    internal static void Request(int target)
    {
        if (Busy || _service is null || !Hooks.IsIdle()) return;
        if (_isHost) Propose(target, _service.NetId);
        else { _service.SendMessage(new UndoMinimalMessage { Op = UndoMinimalOp.Request, Target = target }); Status = "Undo requested from host"; }
    }

    private static void Propose(int target, ulong proposer)
    {
        if (Busy || _service is null || !Hooks.IsIdle()) return;
        if (!History.Targets.Any(t => t.EventCount == target)) return;
        var plan = History.Freeze(target);
        _operation = new Operation
        {
            Id = Guid.NewGuid().ToString("N"), Target = target, Proposer = proposer,
            Peers = RunManager.Instance.DebugOnlyGetState()!.Players.Select(p => p.NetId).ToHashSet(), Plan = plan
        };
        Status = $"Undo to event {target}: waiting for votes";
        Send(UndoMinimalOp.Propose);
        if (proposer == _service.NetId) Vote(true);
        else ReceiveVote(proposer, true);
        Hooks.Log($"PROPOSE session={_operation.Id} target={target}");
    }

    internal static void Vote(bool accepted)
    {
        var operation = _operation;
        if (operation is null || operation.Started || operation.LocalVoted || _service is null) return;
        if (accepted && !Hooks.IsIdle()) return;
        try
        {
            if (accepted)
            {
                operation.Plan ??= History.Freeze(operation.Target);
            }
        }
        catch (Exception exception)
        {
            Fail($"Replay plan unavailable: {exception.Message}");
            return;
        }
        operation.LocalVoted = true;
        if (_isHost) ReceiveVote(_service.NetId, accepted);
        else Send(UndoMinimalOp.Vote, accepted);
    }

    private static void Receive(UndoMinimalMessage message, ulong sender)
    {
        try
        {
            if (message.Op == UndoMinimalOp.Request)
            {
                if (_isHost) Propose(message.Target, sender);
                return;
            }
            if (message.Op == UndoMinimalOp.Propose && !_isHost && !Busy)
            {
                _operation = new Operation
                {
                    Id = message.Session, Target = message.Target, Proposer = message.Proposer,
                    Peers = RunManager.Instance.DebugOnlyGetState()!.Players.Select(p => p.NetId).ToHashSet()
                };
                Status = $"Undo to event {message.Target}: accept or reject";
                if (message.Proposer == _service!.NetId) Vote(true);
                return;
            }
            if (_operation is not { } operation || message.Session != operation.Id) return;
            switch (message.Op)
            {
                case UndoMinimalOp.Vote when _isHost && !operation.Started:
                    ReceiveVote(sender, message.Accepted); break;
                case UndoMinimalOp.Start when !_isHost && !operation.Started:
                    operation.StartRequested = true; break;
                case UndoMinimalOp.StartReceived when _isHost:
                    operation.StartReceipts.Add(sender); break;
                case UndoMinimalOp.Done when _isHost:
                    operation.Done[sender] = message.Checksum; break;
                case UndoMinimalOp.Resume when !_isHost && operation.Started:
                    operation.ResumeRequested = true; break;
                case UndoMinimalOp.Cancel:
                    Fail("Undo cancelled by a peer.", notify: false); break;
            }
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    private static void ReceiveVote(ulong sender, bool accepted)
    {
        var operation = _operation!;
        operation.Votes[sender] = accepted;
        Hooks.Log($"VOTE peer={sender} accepted={accepted}");
        if (!accepted) { Fail("Undo rejected by a player."); return; }
        if (operation.Peers.All(id => operation.Votes.GetValueOrDefault(id)))
            operation.StartRequested = true;
    }

    internal static void Tick()
    {
        if (_operation is not { } operation) return;
        if (!operation.Started && !CombatManager.Instance.IsInProgress)
            Fail("Combat ended before undo began.");
        if (!ReferenceEquals(_operation, operation)) return;
        if (Time.GetTicksMsec() > operation.Deadline && operation.Failure is null) Fail("Undo timed out.");
        if (!ReferenceEquals(_operation, operation)) return;
        if (operation.StartRequested && !operation.Started)
        {
            if (operation.Plan is null) { Fail("No frozen replay plan."); return; }
            if (_isHost) Send(UndoMinimalOp.Start);
            operation.Started = true;
            operation.Deadline = Time.GetTicksMsec() + 180_000;
            UI.Close();
            _ = ExecuteAsync(operation);
        }
    }

    private static async Task ExecuteAsync(Operation operation)
    {
        try
        {
            if (_isHost)
            {
                await Replay.WaitUntilAsync(() => operation.Peers.All(id => id == _service!.NetId
                    || operation.StartReceipts.Contains(id)), operation.Cancel.Token);
                Hooks.Log($"START_RECEIVED_ALL session={operation.Id}");
            }
            else Send(UndoMinimalOp.StartReceived);
            Status = "Rebuilding and replaying…";
            uint checksum = await Replay.ExecuteAsync(operation.Plan!, operation.Cancel.Token);
            if (_service is null) throw new InvalidOperationException("Replacement transport missing.");
            operation.Done[_service.NetId] = checksum;
            if (!_isHost) Send(UndoMinimalOp.Done, checksum: checksum);
            Status = "Replay finished: waiting for all players";
            await Replay.WaitUntilAsync(() =>
            {
                if (_isHost && operation.Peers.All(operation.Done.ContainsKey))
                {
                    if (operation.Done.Values.Distinct().Count() != 1)
                        throw new InvalidOperationException("Replay checksums differ.");
                    Send(UndoMinimalOp.Resume);
                    operation.ResumeRequested = true;
                }
                return operation.ResumeRequested;
            }, operation.Cancel.Token);
            operation.Cancel.Token.ThrowIfCancellationRequested();
            History.Commit(operation.Plan!);
            await NGame.Instance!.Transition.FadeIn(0.1f);
            Status = "Undo complete";
            Hooks.Log($"RESUME session={operation.Id} peers={operation.Peers.Count} checksum={checksum}");
        }
        catch (Exception exception)
        {
            Fail(operation.Failure ?? exception.Message);
            Hooks.Log($"UNDO_FAIL session={operation.Id} {exception}");
            // Keep Executing true until partial reconstruction has been discarded.
            try
            {
                Detach();
                Replay.DisconnectReplacement();
                if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp(graceful: false);
                await NGame.Instance!.ReturnToMainMenu();
            }
            catch (Exception cleanup) { Hooks.Log($"CLEANUP_FAIL {cleanup}"); }
            History.Clear();
        }
        finally
        {
            Replay.Clear();
            operation.Cancel.Dispose();
            if (ReferenceEquals(_operation, operation)) _operation = null;
        }
    }

    internal static void Fail(string reason, bool notify = true)
    {
        Status = reason;
        if (_operation is not { } operation || operation.Failure is not null) return;
        operation.Failure = reason;
        Hooks.Log($"CANCEL session={operation.Id} started={operation.Started} reason={reason}");
        if (notify && _service?.IsConnected == true)
        {
            try { Send(UndoMinimalOp.Cancel); }
            catch (Exception exception) { Hooks.Log($"CANCEL_SEND_FAIL {exception.Message}"); }
        }
        operation.Cancel.Cancel();
        if (!operation.Started)
        {
            operation.Cancel.Dispose();
            _operation = null;
        }
    }

    internal static void Reset()
    {
        if (_operation is { } operation)
        {
            operation.Cancel.Cancel();
            operation.Cancel.Dispose();
        }
        _operation = null;
        Status = "Left Arrow: undo history";
    }

    private static void Disconnected(NetErrorInfo info) => Fail($"Disconnected: {info}", notify: false);
    private static void Send(UndoMinimalOp op, bool accepted = false, uint checksum = 0)
    {
        var operation = _operation!;
        _service!.SendMessage(new UndoMinimalMessage
        {
            Op = op, Session = operation.Id, Target = operation.Target, Proposer = operation.Proposer,
            Accepted = accepted, Checksum = checksum
        });
    }
}
