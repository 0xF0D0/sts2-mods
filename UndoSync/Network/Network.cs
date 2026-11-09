using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

public enum UndoMinimalOp { Request, Propose, Vote, Start, Done, Resume, Cancel, StartReceived, Plan, Prepare, Ready, Resumed, Release, Ack, Progress }

// Discovered by the game's GetSubtypesInMods, as in b770 UndoSync's messages.
public struct UndoMinimalMessage : INetMessage
{
    public UndoMinimalOp Op;
    public string Session;
    public int Target;
    public int Steps;
    public ulong Proposer;
    public bool Accepted;
    public uint Checksum;
    public byte[]? Data;
    public bool LastChunk;
    public uint Sequence;
    public string? Progress;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(2); // Same-build wire revision; fail explicitly on incompatible layouts.
        writer.WriteUInt(Sequence);
        writer.WriteEnum(Op); writer.WriteString(Session ?? ""); writer.WriteInt(Target); writer.WriteInt(Steps); writer.WriteULong(Proposer);
        writer.WriteBool(Accepted); writer.WriteUInt(Checksum);
        if (Op is UndoMinimalOp.Progress or UndoMinimalOp.Cancel) writer.WriteString(Progress ?? "");
        if (Op == UndoMinimalOp.Plan)
        {
            writer.WriteInt(Data!.Length); writer.WriteBytes(Data, Data.Length); writer.WriteBool(LastChunk);
        }
    }
    public void Deserialize(PacketReader reader)
    {
        if (reader.ReadInt() != 2) throw new InvalidOperationException("UndoSync protocol mismatch: all players must use the same build.");
        Sequence = reader.ReadUInt();
        Op = reader.ReadEnum<UndoMinimalOp>(); Session = reader.ReadString(); Target = reader.ReadInt(); Steps = reader.ReadInt(); Proposer = reader.ReadULong();
        Accepted = reader.ReadBool(); Checksum = reader.ReadUInt();
        if (Op is UndoMinimalOp.Progress or UndoMinimalOp.Cancel) Progress = reader.ReadString();
        if (Op == UndoMinimalOp.Plan)
        {
            Data = new byte[reader.ReadInt()]; reader.ReadBytes(Data, Data.Length); LastChunk = reader.ReadBool();
        }
    }
}

internal static class Network
{
    private sealed class Operation
    {
        internal required string Id;
        internal required int Target;
        internal required ulong Proposer;
        internal int Steps;
        internal required HashSet<ulong> Peers;
        internal readonly Dictionary<ulong, bool> Votes = new();
        internal readonly Dictionary<ulong, uint> Done = new();
        internal readonly HashSet<ulong> StartReceipts = new();
        internal readonly HashSet<ulong> ResumedPeers = new();
        internal readonly CancellationTokenSource Cancel = new();
        internal CombatReplay? Plan;
        internal byte[]? PlanData;
        internal readonly List<byte> Incoming = new();
        internal readonly HashSet<ulong> ReadyPeers = new();
        internal bool Proposed, Preparing, ReadySent;
        internal bool Started, StartRequested, ResumeRequested, ReleaseRequested, LocalVoted;
        internal string? Failure;
        internal ulong Deadline = Time.GetTicksMsec() + 30_000;
    }

    private static INetGameService? _service;
    private static Operation? _operation;
    private static bool _isHost;
    private sealed class Delivery(UndoMinimalMessage message)
    {
        internal readonly UndoMinimalMessage Message = message;
        internal readonly ulong Created = Time.GetTicksMsec();
        internal ulong LastSent;
        internal int Attempts;
    }
    private sealed class Link
    {
        internal uint Sent, Received;
        internal readonly SortedDictionary<uint, Delivery> Pending = new();
        internal readonly SortedDictionary<uint, UndoMinimalMessage> Buffered = new();
        internal string Progress = "not reported";
        internal ulong LastProgress;
        internal Delivery? Cancellation;
    }
    // Connection-scoped: Release ACKs and duplicate suppression must survive
    // operation completion and run-owner recreation on this SAME service.
    private static readonly Dictionary<ulong, Link> Links = new();
    internal static int PendingDeliveryCount => Links.Values.Sum(link => link.Pending.Count + (link.Cancellation is null ? 0 : 1));
    private static ulong _lastProgress;
    private static string _stage = "idle";
    private static ulong _stageSince;
    internal static void ReportProgress(string stage)
    {
        if (_stage == stage) return;
        _stage = stage;
        _stageSince = Time.GetTicksMsec();
    }
    internal static bool LocalVoted => _operation?.LocalVoted == true;
    internal static int Target => _operation?.Target ?? 0;
    internal static int Steps => _operation?.Steps ?? 0;
    internal static bool Busy => _operation is not null;
    internal static bool Executing => _operation?.Started == true;
    internal static bool Voting => _operation?.Proposed == true;
    internal static bool Preparing => _operation?.Preparing == true;
    internal static bool CanVote => _operation is { Proposed: true, Started: false, LocalVoted: false };
    internal static string Status { get; private set; } = "Left Arrow: undo history";

    internal static void Attach(INetGameService service)
    {
        if (ReferenceEquals(_service, service)) return;
        Detach();
        _service = service;
        _isHost = service.Type is NetGameType.Host or NetGameType.Singleplayer;
        service.RegisterMessageHandler<UndoMinimalMessage>(Receive);
        service.Disconnected += Disconnected;
        if (service is NetHostGameService host) host.ClientDisconnected += ClientDisconnected;
        Hooks.Log($"NETWORK_ATTACH role={service.Type} platform={service.Platform} id={service.NetId}");
    }

    internal static void Detach()
    {
        if (_service is null) return;
        _service.UnregisterMessageHandler<UndoMinimalMessage>(Receive);
        _service.Disconnected -= Disconnected;
        if (_service is NetHostGameService host) host.ClientDisconnected -= ClientDisconnected;
        _service = null;
        Links.Clear();
    }

    private static Operation CreateOperation(string id, int target, ulong proposer) => new()
    {
        Id = id, Target = target, Proposer = proposer,
        Peers = RunManager.Instance.DebugOnlyGetState()!.Players.Select(p => p.NetId).ToHashSet()
    };

    internal static void Request(int target)
    {
        // Only the selected history matters; current combat/input state may
        // differ between peers and is discarded when restoring this prefix.
        if (Busy || _service is null) return;
        if (!History.Targets.Any(t => t.EventCount == target)) return;
        var data = History.Export(target);
        var operation = CreateOperation(Guid.NewGuid().ToString("N"), target, _service.NetId);
        operation.Steps = History.Targets.Count(t => t.EventCount >= target);
        _operation = operation;
        ReportProgress("proposal");
        InstallPlan(operation, data);
        if (_isHost) Propose(operation);
        else
        {
            operation.LocalVoted = true; // Requesting is agreement; no own Accept button.
            Status = "Undo requested from host";
            Send(UndoMinimalOp.Request);
            SendPlan(operation);
        }
    }

    private static void InstallPlan(Operation operation, byte[] data)
    {
        (operation.Plan, _) = History.Import(data);
        operation.PlanData = data;
        Hooks.Log($"PROPOSER_PLAN session={operation.Id} proposer={operation.Proposer} "
            + $"events={operation.Plan.events.Count} bytes={data.Length} sha256={Convert.ToHexString(SHA256.HashData(data))}");
    }

    private static void SendPlan(Operation operation)
    {
        // Small reliable packets work with both native transports, including long histories.
        const int chunkSize = 1024;
        byte[] data = operation.PlanData!;
        for (int offset = 0; offset < data.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, data.Length - offset);
            Queue(new UndoMinimalMessage
            {
                Op = UndoMinimalOp.Plan, Session = operation.Id, Target = operation.Target,
                Proposer = operation.Proposer, Data = data[offset..(offset + count)],
                LastChunk = offset + count == data.Length
            });
        }
        Hooks.Log($"PLAN_SENT session={operation.Id} chunks={(data.Length + chunkSize - 1) / chunkSize}");
    }

    private static void Propose(Operation operation)
    {
        ReportProgress("voting");
        operation.Proposed = true;
        Status = $"Undo to event {operation.Target}: waiting for votes";
        Send(UndoMinimalOp.Propose);
        SendPlan(operation); // Relay the proposer's original bytes, never host-local history.
        if (operation.Proposer == _service!.NetId) Vote(true);
        else ReceiveVote(operation.Proposer, true);
        Hooks.Log($"PROPOSE session={operation.Id} proposer={operation.Proposer} target={operation.Target}");
    }

    internal static void Vote(bool accepted)
    {
        var operation = _operation;
        if (operation is null || !operation.Proposed || operation.Started || operation.LocalVoted || _service is null) return;
        operation.LocalVoted = true;
        Hooks.Log($"VOTE_LOCAL session={operation.Id} accepted={accepted} idle={Hooks.IsIdle()}");
        if (_isHost) ReceiveVote(_service.NetId, accepted);
        else Send(UndoMinimalOp.Vote, accepted);
        Hooks.Log($"VOTE_SUBMITTED session={operation.Id} accepted={accepted}");
    }

    private static void Receive(UndoMinimalMessage message, ulong sender)
    {
        var link = GetLink(sender);
        if (message.Op == UndoMinimalOp.Ack)
        {
            if (message.Sequence == 0 && link.Cancellation?.Message.Session == message.Session)
            {
                Hooks.Log($"CONTROL_ACK session={message.Session} peer={sender} seq=0 op=Cancel attempts={link.Cancellation.Attempts}");
                link.Cancellation = null;
                return;
            }
            foreach (uint sequence in link.Pending.Keys.TakeWhile(n => n <= message.Sequence).ToArray())
            {
                var delivery = link.Pending[sequence];
                Hooks.Log($"CONTROL_ACK session={delivery.Message.Session} peer={sender} seq={sequence} op={delivery.Message.Op} attempts={delivery.Attempts}");
                link.Pending.Remove(sequence);
            }
            return;
        }
        if (message.Op == UndoMinimalOp.Progress)
        {
            if (_operation?.Id == message.Session)
            {
                link.Progress = message.Progress ?? "unknown";
                link.LastProgress = Time.GetTicksMsec();
                Hooks.Log($"PEER_PROGRESS session={message.Session} peer={sender} stage={link.Progress}");
                if (link.Progress.StartsWith("failed: ")) Fail($"Peer {sender}: {link.Progress}", notify: _isHost);
            }
            return;
        }
        // Cancellation bypasses ordered delivery: a failure must not sit behind
        // the very missing packet that caused it. Progress repeats the reason.
        if (message.Op == UndoMinimalOp.Cancel)
        {
            Transmit(new UndoMinimalMessage { Op = UndoMinimalOp.Ack, Session = message.Session }, sender);
            ReceiveCore(message, sender);
            return;
        }
        if (message.Sequence > link.Received)
            link.Buffered.TryAdd(message.Sequence, message);
        else Hooks.Log($"CONTROL_DUPLICATE session={message.Session} peer={sender} seq={message.Sequence} op={message.Op}");
        while (link.Buffered.Remove(link.Received + 1, out var next))
        {
            link.Received++;
            ReceiveCore(next, sender);
        }
        Transmit(new UndoMinimalMessage { Op = UndoMinimalOp.Ack, Sequence = link.Received }, sender);
    }

    private static void ReceiveCore(UndoMinimalMessage message, ulong sender)
    {
        try
        {
            Hooks.Log($"MESSAGE_RECEIVED op={message.Op} sender={sender} session={message.Session}");
            if (message.Op == UndoMinimalOp.Request)
            {
                if (_isHost && !Busy)
                {
                    _operation = CreateOperation(message.Session, message.Target, sender);
                    ReportProgress("receiving-plan");
                    _operation.Steps = message.Steps;
                }
                return;
            }
            if (message.Op == UndoMinimalOp.Propose && !_isHost)
            {
                if (_operation is null)
                    _operation = CreateOperation(message.Session, message.Target, message.Proposer);
                if (_operation.Id != message.Session) return;
                _operation.Proposed = true;
                ReportProgress("voting");
                _operation.Steps = message.Steps;
                if (message.Proposer == _service!.NetId) _operation.LocalVoted = true;
                Status = $"Undo to event {message.Target}: accept or reject";
                return;
            }
            if (_operation is not { } operation || message.Session != operation.Id) return;
            switch (message.Op)
            {
                case UndoMinimalOp.Plan:
                    operation.Incoming.AddRange(message.Data!);
                    if (message.LastChunk)
                    {
                        InstallPlan(operation, operation.Incoming.ToArray());
                        operation.Incoming.Clear();
                        if (_isHost && !operation.Proposed) Propose(operation);
                    }
                    break;
                case UndoMinimalOp.Prepare when !_isHost:
                    ReportProgress("preparing");
                    operation.Preparing = true;
                    operation.Deadline = Time.GetTicksMsec() + 180_000;
                    break;
                case UndoMinimalOp.Ready when _isHost:
                    operation.ReadyPeers.Add(sender); break;
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
                case UndoMinimalOp.Resumed when _isHost && operation.Started:
                    operation.ResumedPeers.Add(sender); break;
                case UndoMinimalOp.Release when !_isHost && operation.Started:
                    operation.ReleaseRequested = true; break;
                case UndoMinimalOp.Cancel:
                    Fail($"Peer {sender}: {message.Progress ?? "Undo cancelled."}", notify: _isHost); break;
            }
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    private static void ReceiveVote(ulong sender, bool accepted)
    {
        var operation = _operation!;
        operation.Votes[sender] = accepted;
        Hooks.Log($"VOTE peer={sender} accepted={accepted} received={operation.Votes.Count}/{operation.Peers.Count}");
        if (!accepted) { Fail("Undo rejected by a player."); return; }
        if (operation.Peers.All(id => operation.Votes.GetValueOrDefault(id)))
        {
            if (!operation.Preparing)
            {
                operation.Preparing = true;
                ReportProgress("preparing");
                operation.Deadline = Time.GetTicksMsec() + 180_000;
                Send(UndoMinimalOp.Prepare);
            }
            Hooks.Log($"ALL_VOTES_ACCEPTED session={operation.Id}");
        }
    }

    internal static void Tick()
    {
        TickDelivery();
        if (_operation is not { } operation) return;
        // NRun normally pumps the service. Keep the SAME connection responsive
        // while the run scene/owners are temporarily absent.
        if (operation.Started && (NRun.Instance is null || !RunManager.Instance.IsInProgress)) _service?.Update();
        if (Time.GetTicksMsec() > operation.Deadline && operation.Failure is null)
        {
            LogWaiting(operation);
            Fail($"Undo timed out at {_stage}.");
        }
        if (!ReferenceEquals(_operation, operation)) return;
        if (operation.Preparing && !operation.ReadySent && operation.Plan is not null)
        {
            operation.ReadySent = true;
            ReportProgress("waiting-start");
            if (_isHost) operation.ReadyPeers.Add(_service!.NetId);
            else Send(UndoMinimalOp.Ready);
            Hooks.Log($"REPLAY_READY session={operation.Id}");
        }
        if (_isHost && operation.Preparing && operation.Peers.All(operation.ReadyPeers.Contains))
            operation.StartRequested = true;
        if (operation.StartRequested && !operation.Started)
        {
            if (operation.Plan is null) { Fail("No frozen replay plan."); return; }
            if (_isHost) Send(UndoMinimalOp.Start);
            operation.Started = true;
            ReportProgress("start-receipts");
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
            ReportProgress("restore");
            uint checksum = await Replay.ExecuteAsync(operation.Plan!, operation.Cancel.Token);
            if (_service is null) throw new InvalidOperationException("Replacement transport missing.");
            operation.Done[_service.NetId] = checksum;
            if (!_isHost) Send(UndoMinimalOp.Done, checksum: checksum);
            Status = "Replay finished: waiting for all players";
            ReportProgress("waiting-done");
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
            // Native loaded-lobby setup can mutate the execution copy. Commit
            // the original proposer bytes, not that live native object.
            var (record, targets) = History.Import(operation.PlanData!);
            ReportProgress("commit");
            History.Commit(record, targets);
            UI.HideRestoring();
            await NGame.Instance!.Transition.FadeIn(0.1f);
            Replay.ResumeNative();
            ReportProgress("waiting-release");
            if (_isHost) operation.ResumedPeers.Add(_service!.NetId);
            else Send(UndoMinimalOp.Resumed);
            await Replay.WaitUntilAsync(() =>
            {
                if (_isHost && operation.Peers.All(operation.ResumedPeers.Contains))
                {
                    Send(UndoMinimalOp.Release);
                    operation.ReleaseRequested = true;
                }
                return operation.ReleaseRequested;
            }, operation.Cancel.Token);
            Status = "Undo complete";
            ReportProgress("complete");
            Hooks.Log($"RESUME session={operation.Id} peers={operation.Peers.Count} checksum={checksum}");
        }
        catch (Exception exception)
        {
            Fail(operation.Failure ?? exception.Message);
            Hooks.Log($"UNDO_FAIL session={operation.Id} {exception}");
            // Keep Executing true until partial reconstruction has been discarded.
            try
            {
                // Give the failure reason a bounded chance to reach peers before
                // native immediate disconnect disposes the sending connection.
                ulong until = Time.GetTicksMsec() + 1000;
                while (Links.Values.Any(link => link.Cancellation is not null)
                    && _service?.IsConnected == true && Time.GetTicksMsec() < until)
                    await NGame.Instance!.AwaitProcessFrame();
                Detach();
                Replay.DisconnectSession();
                if (RunManager.Instance.IsInProgress) RunManager.Instance.CleanUp(graceful: false);
                await NGame.Instance!.ReturnToMainMenu();
            }
            catch (Exception cleanup) { Hooks.Log($"CLEANUP_FAIL {cleanup}"); }
            History.Clear();
        }
        finally
        {
            UI.HideRestoring();
            Replay.Clear();
            operation.Cancel.Dispose();
            if (ReferenceEquals(_operation, operation)) _operation = null;
        }
    }

    internal static void Fail(string reason, bool notify = true)
    {
        if (_operation is not { } operation || operation.Failure is not null) return;
        Status = reason;
        operation.Failure = reason;
        ReportProgress("failed: " + reason);
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
    private static void ClientDisconnected(ulong peer, NetErrorInfo info)
    {
        if (_operation is { } operation && operation.Peers.Contains(peer))
            Fail($"Player {peer} disconnected during {_stage}: {info}");
        Links.Remove(peer);
    }
    private static void Send(UndoMinimalOp op, bool accepted = false, uint checksum = 0)
    {
        var operation = _operation!;
        var message = new UndoMinimalMessage
        {
            Op = op, Session = operation.Id, Target = operation.Target, Proposer = operation.Proposer,
            Steps = operation.Steps, Accepted = accepted, Checksum = checksum, Progress = operation.Failure
        };
        if (op == UndoMinimalOp.Cancel)
        {
            foreach (ulong peer in Recipients()) GetLink(peer).Cancellation = new Delivery(message);
            Flush();
        }
        else Queue(message);
    }

    private static Link GetLink(ulong peer)
    {
        if (!Links.TryGetValue(peer, out var link)) Links[peer] = link = new();
        return link;
    }
    private static IEnumerable<ulong> Recipients() => _service is NetClientGameService client
        ? new[] { client.NetClient!.HostNetId }
        : _operation!.Peers.Where(id => id != _service!.NetId).ToArray();
    private static void Queue(UndoMinimalMessage message)
    {
        foreach (ulong peer in Recipients())
        {
            var link = GetLink(peer);
            var copy = message;
            copy.Sequence = ++link.Sent;
            link.Pending.Add(copy.Sequence, new Delivery(copy));
        }
        Flush();
    }
    private static void Transmit(UndoMinimalMessage message, ulong peer)
    {
        try { _service?.SendMessage(message, peer); }
        catch (Exception error) { Hooks.Log($"CONTROL_SEND_ERROR peer={peer} op={message.Op} seq={message.Sequence} error={error.Message}"); }
    }
    private static void Flush()
    {
        ulong now = Time.GetTicksMsec();
        foreach (var (peer, link) in Links.ToArray())
        {
            if (link.Cancellation is { } cancellation)
            {
                if (now - cancellation.Created > 30_000)
                {
                    Hooks.Log($"CONTROL_GIVE_UP session={cancellation.Message.Session} peer={peer} op=Cancel");
                    link.Cancellation = null;
                }
                else TrySend(cancellation, peer, now);
            }
            // Window permits bulk history without one round trip per 1 KiB.
            foreach (var delivery in link.Pending.Values.Take(16).ToArray())
                TrySend(delivery, peer, now);
        }
    }
    private static void TrySend(Delivery delivery, ulong peer, ulong now)
    {
        if (delivery.Attempts > 0 && now - delivery.LastSent < 500) return;
        delivery.LastSent = now;
        delivery.Attempts++;
        var message = delivery.Message;
        Hooks.Log($"CONTROL_SEND session={message.Session} peer={peer} seq={message.Sequence} op={message.Op} attempt={delivery.Attempts}");
        Transmit(message, peer);
    }
    private static void TickDelivery()
    {
        if (_service is null) return;
        Flush();
        ulong now = Time.GetTicksMsec();
        if (_operation is not { } operation || now - _lastProgress < 2000) return;
        _lastProgress = now;
        string receipts = _isHost ? $"votes={operation.Votes.Count}/{operation.Peers.Count} done={operation.Done.Count}/{operation.Peers.Count}"
            : $"localVoted={operation.LocalVoted} localDone={operation.Done.ContainsKey(_service.NetId)}";
        foreach (ulong peer in Recipients())
            Transmit(new UndoMinimalMessage { Op = UndoMinimalOp.Progress, Session = operation.Id,
                Progress = $"{_stage} stageMs={now - _stageSince} {receipts} {Replay.DescribeProgress()}" }, peer);
        LogWaiting(operation);
    }
    private static void LogWaiting(Operation operation)
    {
        string Missing(IEnumerable<ulong> received) => string.Join(",", operation.Peers.Except(received));
        string links = string.Join(";", Links.Select(pair => $"peer={pair.Key},pending={string.Join(",", pair.Value.Pending.Values.Select(d => d.Message.Op).Distinct())},cancelPending={pair.Value.Cancellation is not null},remote={pair.Value.Progress},ageMs={(pair.Value.LastProgress == 0 ? "unseen" : (Time.GetTicksMsec() - pair.Value.LastProgress).ToString())}"));
        string receipts = _isHost
            ? $"missingVotes=[{Missing(operation.Votes.Keys)}] missingReady=[{Missing(operation.ReadyPeers)}] missingStart=[{Missing(operation.StartReceipts.Append(_service!.NetId))}] missingDone=[{Missing(operation.Done.Keys)}] missingResumed=[{Missing(operation.ResumedPeers)}]"
            : $"localVoted={operation.LocalVoted} localDone={operation.Done.ContainsKey(_service!.NetId)}";
        Hooks.Log($"UNDO_WAIT session={operation.Id} stage={_stage} stageMs={Time.GetTicksMsec() - _stageSince} {receipts} links=[{links}]");
    }
}
