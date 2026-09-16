using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using HarmonyLib;

namespace UndoSync;

public record struct NativeReplayReadyMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong reporterNetId;
    public int generation;
    public uint checkpointId;
    public uint checkpointHash;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(reporterNetId);
        writer.WriteInt(generation);
        writer.WriteUInt(checkpointId);
        writer.WriteUInt(checkpointHash);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        reporterNetId = reader.ReadULong();
        generation = reader.ReadInt();
        checkpointId = reader.ReadUInt();
        checkpointHash = reader.ReadUInt();
    }
}

/// <summary>
/// Reliable first-phase notification that this peer has completed native rejoin, replay, executor
/// drain, and replay-prefix installation. The host uses these notifications to establish the
/// checkpoint boundary; this message deliberately carries no checkpoint id or hash.
/// </summary>
public record struct NativeReplayRestoreReadyMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int generation;
    public int eventCount;
    public ulong reporterNetId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(generation);
        writer.WriteInt(eventCount);
        writer.WriteULong(reporterNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        generation = reader.ReadInt();
        eventCount = reader.ReadInt();
        reporterNetId = reader.ReadULong();
    }
}

/// <summary>
/// Host-controlled request that starts checkpoint generation. This handler is registered on every
/// peer before the first local restore checkpoint is generated; the request therefore defines the
/// checkpoint boundary instead of relying on two independently-timed local calls.
/// </summary>
public record struct NativeReplayCheckpointRequestMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int generation;
    public int eventCount;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(generation);
        writer.WriteInt(eventCount);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        generation = reader.ReadInt();
        eventCount = reader.ReadInt();
    }
}

public record struct NativeReplayCheckpointApprovedMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int generation;
    public uint checkpointId;
    public uint checkpointHash;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(generation);
        writer.WriteUInt(checkpointId);
        writer.WriteUInt(checkpointHash);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        generation = reader.ReadInt();
        checkpointId = reader.ReadUInt();
        checkpointHash = reader.ReadUInt();
    }
}

public record struct NativeReplayResumeMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int generation;
    public uint checkpointId;
    public uint checkpointHash;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(generation);
        writer.WriteUInt(checkpointId);
        writer.WriteUInt(checkpointHash);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        generation = reader.ReadInt();
        checkpointId = reader.ReadUInt();
        checkpointHash = reader.ReadUInt();
    }
}

/// <summary>
/// Client acknowledgement for a validated resume-preparation message.  The client remains action
/// gated until the host has received this from every peer and broadcasts NativeReplayReleaseMessage.
/// </summary>
public record struct NativeReplayResumeAckMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong reporterNetId;
    public int generation;
    public uint checkpointId;
    public uint checkpointHash;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(reporterNetId);
        writer.WriteInt(generation);
        writer.WriteUInt(checkpointId);
        writer.WriteUInt(checkpointHash);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        reporterNetId = reader.ReadULong();
        generation = reader.ReadInt();
        checkpointId = reader.ReadUInt();
        checkpointHash = reader.ReadUInt();
    }
}

/// <summary>Host's final all-peers-armed release.  This is the only message that unlocks actions.</summary>
public record struct NativeReplayReleaseMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int generation;
    public uint checkpointId;
    public uint checkpointHash;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(generation);
        writer.WriteUInt(checkpointId);
        writer.WriteUInt(checkpointHash);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        generation = reader.ReadInt();
        checkpointId = reader.ReadUInt();
        checkpointHash = reader.ReadUInt();
    }
}

internal sealed record NativeReplayCompletionResult(bool Success, string Detail);

internal sealed record NativeReplayCheckpointResult(
    bool Success,
    string Detail,
    NetChecksumData? Checkpoint,
    int Generation);

internal static class NativeReplayCompletion
{
    private static readonly TimeSpan QuorumTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadyRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResumeAckRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly object Gate = new();
    private static readonly MethodInfo? PlayerActionsDisabledSetter =
        AccessTools.PropertySetter(typeof(CombatManager), "PlayerActionsDisabled");
    private static CompletionSession? _activeSession;
    private static PreCheckpointMonitor? _preCheckpointMonitor;
    private static NativeReplayRestorePlan? _preCheckpointPlan;
    private static ChecksumTracker? _preCheckpointOldTracker;
    private static string? _preCheckpointFailure;

    /// <summary>
    /// Arms monitoring across the service teardown. The old tracker is deliberately retained only
    /// as an identity sentinel: ChecksumHook attaches the monitor when it observes the replacement
    /// tracker, before the new combat's automatic turn-start checksum can be emitted.
    /// </summary>
    internal static void ArmPreCheckpointMonitoring(
        NativeReplayRestorePlan plan,
        ChecksumTracker? oldTracker)
    {
        lock (Gate)
        {
            DetachPreCheckpointMonitorLocked();
            _preCheckpointPlan = plan;
            _preCheckpointOldTracker = oldTracker;
            _preCheckpointFailure = null;
        }

        Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_ARMED target={plan.TargetChecksumId} "
            + $"events={plan.EventCount}.");
    }

    /// <summary>Called by ChecksumHook immediately after a replacement tracker is subscribed.</summary>
    internal static void AttachPreCheckpointMonitoring(RunManager? runManager, ChecksumTracker tracker)
    {
        if (runManager?.NetService is not { } service || !service.IsConnected)
        {
            return;
        }

        PreCheckpointMonitor? monitor = null;
        lock (Gate)
        {
            if (_preCheckpointPlan == null
                || _preCheckpointFailure != null
                || !NativeReplayActionGate.IsActive
                || ReferenceEquals(tracker, _preCheckpointOldTracker))
            {
                return;
            }

            if (_preCheckpointMonitor != null && ReferenceEquals(_preCheckpointMonitor.ChecksumTracker, tracker))
            {
                return;
            }

            DetachPreCheckpointMonitorLocked();
            monitor = new PreCheckpointMonitor(service, runManager.RunLobby, tracker, _preCheckpointPlan);
            _preCheckpointMonitor = monitor;
        }

        monitor.Attach();
        Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_MONITOR_ATTACHED role={service.Type} "
            + $"netId={service.NetId} target={monitor.Plan.TargetChecksumId} events={monitor.Plan.EventCount}.");
    }

    internal static bool TryGetPreCheckpointFailure(out string detail)
    {
        lock (Gate)
        {
            detail = _preCheckpointFailure ?? "";
            return _preCheckpointFailure != null;
        }
    }

    internal static void CancelPreCheckpointMonitoring(string reason)
    {
        lock (Gate)
        {
            DetachPreCheckpointMonitorLocked();
            _preCheckpointPlan = null;
            _preCheckpointOldTracker = null;
        }

        Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_MONITOR_CANCELLED reason={reason}.");
    }

    /// <summary>
    /// Registers the checkpoint protocol before any peer calls GenerateChecksum. The host sends the
    /// request, every peer generates its local restored checkpoint exactly once, and the host only
    /// approves the boundary after all reported id/hash pairs match. The returned session is then
    /// replaced by WaitForQuorumAsync for the final resume-ack/release handshake.
    /// </summary>
    internal static async Task<NativeReplayCheckpointResult> PrepareCheckpointAsync(
        NativeReplayRestorePlan plan)
    {
        if (plan is null)
        {
            return new NativeReplayCheckpointResult(false, "Replay checkpoint plan was null.", null, 0);
        }

        RunManager? runManager = RunManager.Instance;
        INetGameService? service = runManager?.NetService;
        if (service == null || !service.IsConnected)
        {
            return new NativeReplayCheckpointResult(false, "The new multiplayer network service was unavailable.", null, 0);
        }

        if (service.Type != NetGameType.Host && service.Type != NetGameType.Client)
        {
            return new NativeReplayCheckpointResult(false, $"Checkpoint protocol requires a host or client, not {service.Type}.", null, 0);
        }

        HashSet<ulong> expectedPlayerIds = GetCurrentPlayerIds(runManager!);
        if (expectedPlayerIds.Count == 0)
        {
            return new NativeReplayCheckpointResult(false, "The new RunState contained no players for the checkpoint quorum.", null, 0);
        }

        ulong localNetId;
        try
        {
            localNetId = service.NetId;
        }
        catch (Exception ex)
        {
            return new NativeReplayCheckpointResult(false, $"Could not read the checkpoint network identity: {Describe(ex)}", null, 0);
        }

        if (!expectedPlayerIds.Contains(localNetId))
        {
            return new NativeReplayCheckpointResult(false, $"The local network identity {localNetId} was absent from the checkpoint RunState.", null, 0);
        }

        int generation = NativeReplayActionGate.Generation;
        if (!NativeReplayActionGate.IsActive)
        {
            return new NativeReplayCheckpointResult(false, "The native replay barrier was not active while registering the checkpoint protocol.", null, generation);
        }

        var session = new CheckpointProtocolSession(
            service,
            runManager!.RunLobby,
            plan,
            expectedPlayerIds,
            localNetId,
            generation,
            runManager!.ChecksumTracker);

        lock (Gate)
        {
            if (_activeSession != null)
            {
                UnregisterHandlers(_activeSession);
                _activeSession = null;
            }

            RegisterCheckpointHandlers(session);
        }

        Log.Write($"[NativeReplayCompletion] Checkpoint protocol registered role={service.Type} "
            + $"netId={localNetId} generation={generation} target={plan.TargetChecksumId} "
            + $"events={plan.EventCount} expected={string.Join(',', expectedPlayerIds.OrderBy(id => id))}.");

        NativeReplayCheckpointResult result;
        try
        {
            result = await session.RunAsync();
        }
        catch (Exception ex)
        {
            session.Fail($"Checkpoint protocol failed: {Describe(ex)}");
            result = await session.Completion.Task;
        }
        finally
        {
            lock (Gate)
            {
                UnregisterCheckpointHandlers(session);
            }
        }

        return result;
    }

    internal static async Task<NativeReplayCompletionResult> WaitForQuorumAsync(
        NativeReplayRestorePlan plan,
        NetChecksumData checkpoint)
    {
        if (plan is null)
        {
            return Failure("Replay completion plan was null.");
        }

        lock (Gate)
        {
            if (_preCheckpointFailure != null)
            {
                return Failure($"Native replay failed before completion setup: {_preCheckpointFailure}");
            }

            DetachPreCheckpointMonitorLocked();
            _preCheckpointPlan = null;
            _preCheckpointOldTracker = null;
        }

        RunManager? runManager = RunManager.Instance;
        INetGameService? service = runManager?.NetService;
        if (service == null || !service.IsConnected)
        {
            return Failure("The new multiplayer network service was unavailable.");
        }

        if (service.Type != NetGameType.Host && service.Type != NetGameType.Client)
        {
            return Failure($"Replay completion requires a host or client, not {service.Type}.");
        }

        HashSet<ulong> expectedPlayerIds = GetCurrentPlayerIds(runManager!);
        if (expectedPlayerIds.Count == 0)
        {
            return Failure("The new RunState contained no players for the replay quorum.");
        }

        ulong localNetId;
        try
        {
            localNetId = service.NetId;
        }
        catch (Exception ex)
        {
            return Failure($"Could not read the new local network identity: {Describe(ex)}");
        }

        if (!expectedPlayerIds.Contains(localNetId))
        {
            return Failure($"The local network identity {localNetId} was absent from the new RunState.");
        }

        var session = new CompletionSession(
            service,
            runManager!.RunLobby,
            plan,
            expectedPlayerIds,
            localNetId,
            NativeReplayActionGate.Generation,
            checkpoint,
            runManager!.ChecksumTracker);

        lock (Gate)
        {
            if (_activeSession != null)
            {
                UnregisterHandlers(_activeSession);
            }

            _activeSession = session;
            RegisterHandlers(session);
        }

        Log.Write($"[NativeReplayCompletion] Registered on new service role={service.Type} "
            + $"netId={localNetId} target={plan.TargetChecksumId} events={plan.EventCount} "
            + $"checkpoint={checkpoint.id}/{checkpoint.checksum} "
            + $"expected={string.Join(',', expectedPlayerIds.OrderBy(id => id))}.");

        try
        {
            if (service.Type == NetGameType.Host)
            {
                session.ReadyPlayerIds.Add(localNetId);
                TryBroadcastResume(session);
            }
            else
            {
                session.SendReady(attempt: 1, initial: true);
                session.StartReadyRetryLoop();
            }

            Task timeout = Task.Delay(QuorumTimeout);
            Task completed = await Task.WhenAny(session.Completion.Task, timeout);
            if (completed != session.Completion.Task)
            {
                session.Fail($"Replay completion quorum timed out after {QuorumTimeout.TotalMinutes:0} minutes.");
            }

            return await session.Completion.Task;
        }
        catch (Exception ex)
        {
            session.Fail($"Replay completion setup failed: {Describe(ex)}");
            return await session.Completion.Task;
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    UnregisterHandlers(session);
                    _activeSession = null;
                }
            }
        }
    }

    private static HashSet<ulong> GetCurrentPlayerIds(RunManager runManager)
    {
        return runManager.DebugOnlyGetState()?.Players
            .Select(player => player.NetId)
            .ToHashSet() ?? new HashSet<ulong>();
    }

    private static void RegisterHandlers(CompletionSession session)
    {
        session.Service.RegisterMessageHandler<NativeReplayReadyMessage>(session.OnReady);
        session.Service.RegisterMessageHandler<NativeReplayResumeMessage>(session.OnResume);
        session.Service.RegisterMessageHandler<NativeReplayResumeAckMessage>(session.OnResumeAck);
        session.Service.RegisterMessageHandler<NativeReplayReleaseMessage>(session.OnRelease);
        session.Service.Disconnected += session.OnDisconnected;
        SubscribeRemoteDisconnect(session.RunLobby, session.OnRemotePlayerDisconnected);
        session.ChecksumTracker.StateDiverged += session.OnStateDiverged;
    }

    private static void RegisterCheckpointHandlers(CheckpointProtocolSession session)
    {
        session.Service.RegisterMessageHandler<NativeReplayRestoreReadyMessage>(session.OnRestoreReady);
        session.Service.RegisterMessageHandler<NativeReplayCheckpointRequestMessage>(session.OnRequest);
        session.Service.RegisterMessageHandler<NativeReplayCheckpointApprovedMessage>(session.OnApproved);
        session.Service.RegisterMessageHandler<NativeReplayReadyMessage>(session.OnReady);
        session.Service.Disconnected += session.OnDisconnected;
        SubscribeRemoteDisconnect(session.RunLobby, session.OnRemotePlayerDisconnected);
        session.ChecksumTracker.StateDiverged += session.OnStateDiverged;
    }

    private static void UnregisterCheckpointHandlers(CheckpointProtocolSession session)
    {
        session.StopRestoreReadyRetries("checkpoint handlers unregistered");
        session.StopRequestRetries("checkpoint handlers unregistered");
        try
        {
            session.Service.UnregisterMessageHandler<NativeReplayRestoreReadyMessage>(session.OnRestoreReady);
            session.Service.UnregisterMessageHandler<NativeReplayCheckpointRequestMessage>(session.OnRequest);
            session.Service.UnregisterMessageHandler<NativeReplayCheckpointApprovedMessage>(session.OnApproved);
            session.Service.UnregisterMessageHandler<NativeReplayReadyMessage>(session.OnReady);
            session.Service.Disconnected -= session.OnDisconnected;
            UnsubscribeRemoteDisconnect(session.RunLobby, session.OnRemotePlayerDisconnected);
            session.ChecksumTracker.StateDiverged -= session.OnStateDiverged;
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayCompletion] Checkpoint handler cleanup failed: {Describe(ex)}");
        }
    }

    private static void UnregisterHandlers(CompletionSession session)
    {
        session.StopReadyRetries("handlers unregistered");
        session.StopResumeAckRetries("handlers unregistered");
        try
        {
            session.Service.UnregisterMessageHandler<NativeReplayReadyMessage>(session.OnReady);
            session.Service.UnregisterMessageHandler<NativeReplayResumeMessage>(session.OnResume);
            session.Service.UnregisterMessageHandler<NativeReplayResumeAckMessage>(session.OnResumeAck);
            session.Service.UnregisterMessageHandler<NativeReplayReleaseMessage>(session.OnRelease);
            session.Service.Disconnected -= session.OnDisconnected;
            UnsubscribeRemoteDisconnect(session.RunLobby, session.OnRemotePlayerDisconnected);
            session.ChecksumTracker.StateDiverged -= session.OnStateDiverged;
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayCompletion] Handler cleanup failed: {Describe(ex)}");
        }
    }

    private static void SubscribeRemoteDisconnect(RunLobby? runLobby, Action<ulong> handler)
    {
        if (runLobby != null)
        {
            runLobby.RemotePlayerDisconnected += handler;
        }
    }

    private static void UnsubscribeRemoteDisconnect(RunLobby? runLobby, Action<ulong> handler)
    {
        if (runLobby != null)
        {
            runLobby.RemotePlayerDisconnected -= handler;
        }
    }

    private static void DetachPreCheckpointMonitorLocked()
    {
        _preCheckpointMonitor?.Detach();
        _preCheckpointMonitor = null;
    }

    private static void ReportPreCheckpointFailure(PreCheckpointMonitor monitor, string detail)
    {
        bool accepted = false;
        lock (Gate)
        {
            if (ReferenceEquals(_preCheckpointMonitor, monitor) && _preCheckpointFailure == null)
            {
                _preCheckpointFailure = detail;
                DetachPreCheckpointMonitorLocked();
                accepted = true;
            }
        }

        if (!accepted)
        {
            return;
        }

        Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_FAIL target={monitor.Plan.TargetChecksumId}: {detail}");
        _ = NativeReplayRestorer.RecoverToMainMenuAsync(
            $"pre-checkpoint failure target={monitor.Plan.TargetChecksumId}: {detail}");
    }

    private sealed class CheckpointProtocolSession
    {
        internal readonly INetGameService Service;
        internal readonly RunLobby? RunLobby;
        internal readonly NativeReplayRestorePlan Plan;
        internal readonly HashSet<ulong> ExpectedPlayerIds;
        internal readonly ulong LocalNetId;
        internal readonly int Generation;
        internal readonly ChecksumTracker ChecksumTracker;
        internal readonly HashSet<ulong> RestoreReadyPlayerIds = new();
        internal readonly HashSet<ulong> ReportedPlayerIds = new();
        internal readonly TaskCompletionSource<NativeReplayCheckpointResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _gate = new();
        private NetChecksumData? _localCheckpoint;
        private bool _terminal;
        private bool _checkpointRequestStarted;
        private bool _restoreReadyRetryStopRequested;
        private Task? _restoreReadyRetryTask;
        private bool _requestRetryStopRequested;
        private Task? _requestRetryTask;

        internal CheckpointProtocolSession(
            INetGameService service,
            RunLobby? runLobby,
            NativeReplayRestorePlan plan,
            HashSet<ulong> expectedPlayerIds,
            ulong localNetId,
            int generation,
            ChecksumTracker checksumTracker)
        {
            Service = service;
            RunLobby = runLobby;
            Plan = plan;
            ExpectedPlayerIds = expectedPlayerIds;
            LocalNetId = localNetId;
            Generation = generation;
            ChecksumTracker = checksumTracker;
        }

        internal async Task<NativeReplayCheckpointResult> RunAsync()
        {
            if (Service.Type == NetGameType.Host)
            {
                RecordLocalRestoreReady();
            }
            else
            {
                SendRestoreReady(attempt: 1, initial: true);
                StartRestoreReadyRetryLoop();
            }

            Task timeout = Task.Delay(QuorumTimeout);
            Task completed = await Task.WhenAny(Completion.Task, timeout);
            if (completed != Completion.Task)
            {
                Fail($"Native replay checkpoint quorum timed out after {QuorumTimeout.TotalMinutes:0} minutes.");
            }

            return await Completion.Task;
        }

        private void RecordLocalRestoreReady()
        {
            lock (_gate)
            {
                if (_terminal)
                {
                    return;
                }

                RestoreReadyPlayerIds.Add(LocalNetId);
            }

            Log.Write($"[NativeReplayCompletion] Restore-ready recorded locally netId={LocalNetId} "
                + $"generation={Generation} target={Plan.TargetChecksumId} events={Plan.EventCount} "
                + $"count={RestoreReadyPlayerIds.Count}/{ExpectedPlayerIds.Count}.");
            TryStartCheckpointRequest();
        }

        private void SendRestoreReady(int attempt, bool initial = false)
        {
            Service.SendMessage(new NativeReplayRestoreReadyMessage
            {
                targetChecksumId = Plan.TargetChecksumId,
                generation = Generation,
                eventCount = Plan.EventCount,
                reporterNetId = LocalNetId
            });
            Log.Write($"[NativeReplayCompletion] {(initial ? "Restore-ready sent" : "RESTORE_READY_RETRY_SENT")} "
                + $"netId={LocalNetId} generation={Generation} target={Plan.TargetChecksumId} "
                + $"events={Plan.EventCount} attempt={attempt}.");
        }

        internal void OnRestoreReady(NativeReplayRestoreReadyMessage message, ulong senderId)
        {
            if (Service.Type != NetGameType.Host || IsTerminal)
            {
                return;
            }

            if (senderId != message.reporterNetId
                || !ExpectedPlayerIds.Contains(senderId)
                || message.targetChecksumId != Plan.TargetChecksumId
                || message.generation != Generation
                || message.eventCount != Plan.EventCount)
            {
                Fail($"Restore-ready mismatch from {senderId}: reporter={message.reporterNetId} "
                    + $"target={message.targetChecksumId}/{Plan.TargetChecksumId} "
                    + $"generation={message.generation}/{Generation} "
                    + $"events={message.eventCount}/{Plan.EventCount}.");
                return;
            }

            lock (_gate)
            {
                RestoreReadyPlayerIds.Add(senderId);
            }
            Log.Write($"[NativeReplayCompletion] Restore-ready received from {senderId} "
                + $"generation={Generation} target={Plan.TargetChecksumId} events={Plan.EventCount} "
                + $"count={RestoreReadyPlayerIds.Count}/{ExpectedPlayerIds.Count}.");
            TryStartCheckpointRequest();
        }

        private void TryStartCheckpointRequest()
        {
            if (Service.Type != NetGameType.Host)
            {
                return;
            }

            lock (_gate)
            {
                if (_terminal
                    || _checkpointRequestStarted
                    || !RestoreReadyPlayerIds.IsSupersetOf(ExpectedPlayerIds))
                {
                    return;
                }

                _checkpointRequestStarted = true;
            }

            Log.Write($"[NativeReplayCompletion] Restore-ready full generation={Generation} "
                + $"target={Plan.TargetChecksumId} events={Plan.EventCount} "
                + $"count={RestoreReadyPlayerIds.Count}/{ExpectedPlayerIds.Count}.");
            try
            {
                // The request is sent before this peer generates its own checksum. This ordering is
                // the protocol boundary: clients cannot generate a local checkpoint opportunistically.
                SendRequest(attempt: 1);
                GenerateLocalCheckpoint("host checkpoint request");
                StartRequestRetryLoop();
            }
            catch (Exception ex)
            {
                Fail($"Could not start checkpoint generation after restore-ready quorum: {Describe(ex)}");
            }
        }

        private void SendRequest(int attempt)
        {
            Service.SendMessage(new NativeReplayCheckpointRequestMessage
            {
                targetChecksumId = Plan.TargetChecksumId,
                generation = Generation,
                eventCount = Plan.EventCount
            });
            Log.Write($"[NativeReplayCompletion] {(attempt == 1 ? "Checkpoint request sent" : "CHECKPOINT_REQUEST_RETRY_SENT")} "
                + $"generation={Generation} target={Plan.TargetChecksumId} events={Plan.EventCount} attempt={attempt}.");
        }

        private void GenerateLocalCheckpoint(string source)
        {
            NetChecksumData? existingCheckpoint;
            lock (_gate)
            {
                if (_terminal)
                {
                    return;
                }

                existingCheckpoint = _localCheckpoint;
            }

            if (existingCheckpoint.HasValue)
            {
                if (Service.Type == NetGameType.Client)
                {
                    SendCheckpointReport(retry: true);
                }

                return;
            }

            try
            {
                NetChecksumData checkpoint = RunManager.Instance!.ChecksumTracker.GenerateChecksum(
                    "UndoSync native replay restore",
                    null);
                lock (_gate)
                {
                    if (_terminal)
                    {
                        return;
                    }

                    _localCheckpoint = checkpoint;
                    ReportedPlayerIds.Add(LocalNetId);
                }

                Log.Write($"[NativeReplayCompletion] Checkpoint generated source={source} "
                    + $"generation={Generation} netId={LocalNetId} id={checkpoint.id} hash={checkpoint.checksum}.");

                if (Service.Type == NetGameType.Host)
                {
                    TryApproveCheckpoint();
                }
                else
                {
                    SendCheckpointReport();
                }
            }
            catch (Exception ex)
            {
                Fail($"Could not generate the restored checkpoint: {Describe(ex)}");
            }
        }

        private void SendCheckpointReport(bool retry = false)
        {
            NetChecksumData? checkpoint;
            lock (_gate)
            {
                checkpoint = _localCheckpoint;
            }

            if (!checkpoint.HasValue || IsTerminal)
            {
                return;
            }

            Service.SendMessage(new NativeReplayReadyMessage
            {
                targetChecksumId = Plan.TargetChecksumId,
                reporterNetId = LocalNetId,
                generation = Generation,
                checkpointId = checkpoint.Value.id,
                checkpointHash = checkpoint.Value.checksum
            });
            Log.Write($"[NativeReplayCompletion] {(retry ? "CHECKPOINT_REPORT_RETRY_SENT" : "Checkpoint report sent")} netId={LocalNetId} "
                + $"generation={Generation} id={checkpoint.Value.id} hash={checkpoint.Value.checksum}.");
        }

        private void TryApproveCheckpoint()
        {
            NetChecksumData? checkpoint;
            lock (_gate)
            {
                if (_terminal
                    || !_localCheckpoint.HasValue
                    || !ReportedPlayerIds.IsSupersetOf(ExpectedPlayerIds))
                {
                    return;
                }

                checkpoint = _localCheckpoint;
            }

            try
            {
                Service.SendMessage(new NativeReplayCheckpointApprovedMessage
                {
                    targetChecksumId = Plan.TargetChecksumId,
                    generation = Generation,
                    checkpointId = checkpoint!.Value.id,
                    checkpointHash = checkpoint.Value.checksum
                });
                Log.Write($"[NativeReplayCompletion] Host approved checkpoint generation={Generation} "
                    + $"target={Plan.TargetChecksumId} id={checkpoint.Value.id} hash={checkpoint.Value.checksum} "
                    + $"reports={ReportedPlayerIds.Count}/{ExpectedPlayerIds.Count}.");
                Succeed(checkpoint.Value, "host received every matching checkpoint report");
            }
            catch (Exception ex)
            {
                Fail($"Host could not broadcast checkpoint approval: {Describe(ex)}");
            }
        }

        internal void OnRequest(NativeReplayCheckpointRequestMessage message, ulong senderId)
        {
            if (Service.Type != NetGameType.Client || IsTerminal)
            {
                return;
            }

            ulong hostNetId;
            try
            {
                hostNetId = ((NetClientGameService)Service).HostNetId;
            }
            catch (Exception ex)
            {
                Fail($"Could not validate the checkpoint request sender: {Describe(ex)}");
                return;
            }

            if (senderId != hostNetId)
            {
                Log.Write($"[NativeReplayCompletion] Ignored checkpoint request from non-host {senderId}; host={hostNetId}.");
                return;
            }

            if (message.targetChecksumId != Plan.TargetChecksumId
                || message.generation != Generation
                || message.eventCount != Plan.EventCount)
            {
                Fail($"Checkpoint request mismatch: target={message.targetChecksumId}/{Plan.TargetChecksumId} "
                    + $"generation={message.generation}/{Generation} events={message.eventCount}/{Plan.EventCount}.");
                return;
            }

            StopRestoreReadyRetries("host checkpoint request received");
            Log.Write($"[NativeReplayCompletion] Checkpoint request received from host={senderId} "
                + $"generation={Generation} target={Plan.TargetChecksumId}.");
            GenerateLocalCheckpoint("host checkpoint request received");
        }

        internal void OnReady(NativeReplayReadyMessage message, ulong senderId)
        {
            if (Service.Type != NetGameType.Host || IsTerminal)
            {
                return;
            }

            NetChecksumData? checkpoint;
            lock (_gate)
            {
                checkpoint = _localCheckpoint;
            }

            if (senderId != message.reporterNetId
                || !ExpectedPlayerIds.Contains(senderId)
                || message.targetChecksumId != Plan.TargetChecksumId
                || message.generation != Generation
                || !checkpoint.HasValue
                || message.checkpointId != checkpoint.Value.id
                || message.checkpointHash != checkpoint.Value.checksum)
            {
                Fail($"Checkpoint report mismatch from {senderId}: target={message.targetChecksumId}/{Plan.TargetChecksumId} "
                    + $"generation={message.generation}/{Generation} checkpoint="
                    + $"{message.checkpointId}/{message.checkpointHash} expected="
                    + $"{checkpoint?.id.ToString() ?? "<none>"}/{checkpoint?.checksum.ToString() ?? "<none>"}.");
                return;
            }

            lock (_gate)
            {
                ReportedPlayerIds.Add(senderId);
            }
            Log.Write($"[NativeReplayCompletion] Checkpoint report received from {senderId} "
                + $"generation={Generation} count={ReportedPlayerIds.Count}/{ExpectedPlayerIds.Count}.");
            TryApproveCheckpoint();
        }

        internal void OnApproved(NativeReplayCheckpointApprovedMessage message, ulong senderId)
        {
            if (Service.Type != NetGameType.Client || IsTerminal)
            {
                return;
            }

            ulong hostNetId;
            try
            {
                hostNetId = ((NetClientGameService)Service).HostNetId;
            }
            catch (Exception ex)
            {
                Fail($"Could not validate checkpoint approval sender: {Describe(ex)}");
                return;
            }

            NetChecksumData? checkpoint;
            lock (_gate)
            {
                checkpoint = _localCheckpoint;
            }

            if (senderId != hostNetId
                || !checkpoint.HasValue
                || message.targetChecksumId != Plan.TargetChecksumId
                || message.generation != Generation
                || message.checkpointId != checkpoint.Value.id
                || message.checkpointHash != checkpoint.Value.checksum)
            {
                Fail($"Checkpoint approval mismatch: sender={senderId}/{hostNetId} target="
                    + $"{message.targetChecksumId}/{Plan.TargetChecksumId} generation="
                    + $"{message.generation}/{Generation} checkpoint={message.checkpointId}/{message.checkpointHash} "
                    + $"local={checkpoint?.id.ToString() ?? "<none>"}/{checkpoint?.checksum.ToString() ?? "<none>"}.");
                return;
            }

            Log.Write($"[NativeReplayCompletion] Checkpoint approval received from host={senderId} "
                + $"generation={Generation} id={checkpoint.Value.id} hash={checkpoint.Value.checksum}.");
            Succeed(checkpoint.Value, "host approved matching checkpoints");
        }

        internal void StartRestoreReadyRetryLoop()
        {
            lock (_gate)
            {
                if (_restoreReadyRetryTask != null)
                {
                    return;
                }

                _restoreReadyRetryTask = RetryRestoreReadyUntilTerminalAsync();
            }

            Log.Write($"[NativeReplayCompletion] RESTORE_READY_RETRY_START netId={LocalNetId} "
                + $"generation={Generation} target={Plan.TargetChecksumId} "
                + $"interval={ReadyRetryInterval.TotalMilliseconds:0}ms.");
        }

        internal void StopRestoreReadyRetries(string reason)
        {
            lock (_gate)
            {
                if (_restoreReadyRetryStopRequested)
                {
                    return;
                }

                _restoreReadyRetryStopRequested = true;
            }

            Log.Write($"[NativeReplayCompletion] RESTORE_READY_RETRY_STOP netId={LocalNetId} "
                + $"generation={Generation} target={Plan.TargetChecksumId} reason={reason}.");
        }

        private async Task RetryRestoreReadyUntilTerminalAsync()
        {
            var sw = Stopwatch.StartNew();
            int attempt = 1;
            try
            {
                while (sw.Elapsed < QuorumTimeout && !ShouldStopRestoreReadyRetries())
                {
                    await Task.Delay(ReadyRetryInterval);
                    if (ShouldStopRestoreReadyRetries())
                    {
                        break;
                    }

                    attempt++;
                    try
                    {
                        SendRestoreReady(attempt);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[NativeReplayCompletion] RESTORE_READY_RETRY_SEND_FAILED "
                            + $"netId={LocalNetId} generation={Generation} attempt={attempt}: {Describe(ex)}");
                    }
                }

                if (!ShouldStopRestoreReadyRetries())
                {
                    StopRestoreReadyRetries("retry timeout");
                    Fail($"Replay restore-ready retry timed out after {QuorumTimeout.TotalMinutes:0} minutes.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayCompletion] RESTORE_READY_RETRY_ERROR netId={LocalNetId} "
                    + $"generation={Generation} target={Plan.TargetChecksumId}: {Describe(ex)}.");
                StopRestoreReadyRetries("retry loop error");
            }
        }

        internal void StartRequestRetryLoop()
        {
            lock (_gate)
            {
                if (_requestRetryTask != null)
                {
                    return;
                }

                _requestRetryTask = RetryRequestsUntilTerminalAsync();
            }
        }

        private async Task RetryRequestsUntilTerminalAsync()
        {
            var sw = Stopwatch.StartNew();
            int attempt = 1;
            try
            {
                while (sw.Elapsed < QuorumTimeout && !ShouldStopRequestRetries())
                {
                    await Task.Delay(ReadyRetryInterval);
                    if (ShouldStopRequestRetries())
                    {
                        break;
                    }

                    attempt++;
                    try
                    {
                        SendRequest(attempt);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[NativeReplayCompletion] CHECKPOINT_REQUEST_RETRY_SEND_FAILED "
                            + $"generation={Generation} attempt={attempt}: {Describe(ex)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayCompletion] CHECKPOINT_REQUEST_RETRY_ERROR generation={Generation}: {Describe(ex)}");
            }
        }

        internal void StopRequestRetries(string reason)
        {
            lock (_gate)
            {
                if (_requestRetryStopRequested)
                {
                    return;
                }

                _requestRetryStopRequested = true;
            }

            Log.Write($"[NativeReplayCompletion] CHECKPOINT_REQUEST_RETRY_STOP generation={Generation} reason={reason}.");
        }

        private bool ShouldStopRequestRetries()
        {
            lock (_gate)
            {
                return _requestRetryStopRequested || _terminal;
            }
        }

        private bool ShouldStopRestoreReadyRetries()
        {
            lock (_gate)
            {
                return _restoreReadyRetryStopRequested || _terminal || _checkpointRequestStarted;
            }
        }

        private bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _terminal;
                }
            }
        }

        private void Succeed(NetChecksumData checkpoint, string detail)
        {
            lock (_gate)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Completion.TrySetResult(new NativeReplayCheckpointResult(true, detail, checkpoint, Generation));
        }

        internal void Fail(string detail)
        {
            lock (_gate)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Log.Write($"[NativeReplayCompletion] CHECKPOINT_FAIL generation={Generation}: {detail}");
            Completion.TrySetResult(new NativeReplayCheckpointResult(false, detail, null, Generation));
        }

        internal void OnDisconnected(NetErrorInfo error) =>
            Fail($"Replay service disconnected during checkpoint protocol: {error}.");

        internal void OnRemotePlayerDisconnected(ulong playerId)
        {
            if (playerId == LocalNetId || !ExpectedPlayerIds.Contains(playerId) || IsTerminal)
            {
                return;
            }

            Log.Write($"[NativeReplayCompletion] REMOTE_PLAYER_DISCONNECTED peer={playerId} "
                + $"phase=checkpoint generation={Generation} target={Plan.TargetChecksumId} "
                + $"events={Plan.EventCount}.");
            Fail($"Remote player {playerId} disconnected during replay checkpoint.");
        }

        internal void OnStateDiverged(ulong remoteId, NetFullCombatState _) =>
            Fail($"Native checksum diverged with peer {remoteId} during checkpoint protocol.");
    }

    private static void HandleReady(CompletionSession session, NativeReplayReadyMessage message, ulong senderId)
    {
        if (session.Service.Type != NetGameType.Host || session.IsTerminal)
        {
            return;
        }

        if (senderId != message.reporterNetId)
        {
            Log.Write($"[NativeReplayCompletion] Ignored ready with sender mismatch "
                + $"transport={senderId} reporter={message.reporterNetId}.");
            return;
        }

        if (message.targetChecksumId != session.Plan.TargetChecksumId)
        {
            Log.Write($"[NativeReplayCompletion] Ignored ready mismatch from {senderId}: "
                + $"target={message.targetChecksumId}/{session.Plan.TargetChecksumId} "
                + $"localEvents={session.Plan.EventCount}.");
            return;
        }

        if (message.generation != session.Generation)
        {
            Log.Write($"[NativeReplayCompletion] Ignored ready generation mismatch from {senderId}: "
                + $"generation={message.generation}/{session.Generation}.");
            return;
        }

        if (!session.ExpectedPlayerIds.Contains(senderId))
        {
            Log.Write($"[NativeReplayCompletion] Ignored ready from unexpected player {senderId}.");
            return;
        }

        if (message.checkpointId != session.Checkpoint.id
            || message.checkpointHash != session.Checkpoint.checksum)
        {
            session.Fail($"Restore checkpoint mismatch from {senderId}: remote="
                + $"{message.checkpointId}/{message.checkpointHash}, host="
                + $"{session.Checkpoint.id}/{session.Checkpoint.checksum}.");
            return;
        }

        session.ReadyPlayerIds.Add(senderId);
        Log.Write($"[NativeReplayCompletion] Ready received from {senderId} "
            + $"checkpoint={message.checkpointId}/{message.checkpointHash} "
            + $"count={session.ReadyPlayerIds.Count}/{session.ExpectedPlayerIds.Count}.");
        TryBroadcastResume(session);
    }

    private static void HandleResume(CompletionSession session, NativeReplayResumeMessage message, ulong senderId)
    {
        if (session.Service.Type != NetGameType.Client || session.IsTerminal)
        {
            return;
        }

        ulong hostNetId;
        try
        {
            hostNetId = ((NetClientGameService)session.Service).HostNetId;
        }
        catch (Exception ex)
        {
            session.Fail($"Could not validate the resume sender: {Describe(ex)}");
            return;
        }

        if (senderId != hostNetId)
        {
            Log.Write($"[NativeReplayCompletion] Ignored resume from non-host {senderId}; host={hostNetId}.");
            return;
        }

        if (message.targetChecksumId != session.Plan.TargetChecksumId)
        {
            session.Fail($"Resume payload mismatch: target={message.targetChecksumId}/"
                + $"{session.Plan.TargetChecksumId}, localEvents={session.Plan.EventCount}.");
            return;
        }

        if (message.generation != session.Generation)
        {
            session.Fail($"Resume generation mismatch: host={message.generation}/{session.Generation}.");
            return;
        }

        if (message.checkpointId != session.Checkpoint.id
            || message.checkpointHash != session.Checkpoint.checksum)
        {
            session.Fail($"Resume checkpoint mismatch: host={message.checkpointId}/{message.checkpointHash}, "
                + $"local={session.Checkpoint.id}/{session.Checkpoint.checksum}.");
            return;
        }

        session.StopReadyRetries("host resume received");
        session.SendResumeAck(attempt: 1, initial: true);
        session.StartResumeAckRetryLoop();
    }

    private static void TryBroadcastResume(CompletionSession session)
    {
        if (session.Service.Type != NetGameType.Host
            || session.IsTerminal
            || session.ResumeBroadcastSent
            || !session.ReadyPlayerIds.IsSupersetOf(session.ExpectedPlayerIds))
        {
            return;
        }

        session.ResumeBroadcastSent = true;
        session.ResumeAcknowledgedPlayerIds.Add(session.LocalNetId);
        var message = new NativeReplayResumeMessage
        {
            targetChecksumId = session.Plan.TargetChecksumId,
            generation = session.Generation,
            checkpointId = session.Checkpoint.id,
            checkpointHash = session.Checkpoint.checksum
        };

        try
        {
            session.Service.SendMessage(message);
            Log.Write($"[NativeReplayCompletion] Host broadcast resume-prepare for target={message.targetChecksumId} "
                + $"checkpoint={message.checkpointId}/{message.checkpointHash}; waiting for peer acknowledgements.");
            TryBroadcastRelease(session);
        }
        catch (Exception ex)
        {
            session.Fail($"Host could not broadcast replay resume: {Describe(ex)}");
        }
    }

    private static void HandleResumeAck(CompletionSession session, NativeReplayResumeAckMessage message, ulong senderId)
    {
        if (session.Service.Type != NetGameType.Host || session.IsTerminal)
        {
            return;
        }

        if (senderId != message.reporterNetId)
        {
            Log.Write($"[NativeReplayCompletion] Ignored resume ack with sender mismatch "
                + $"transport={senderId} reporter={message.reporterNetId}.");
            return;
        }

        if (!session.ExpectedPlayerIds.Contains(senderId))
        {
            Log.Write($"[NativeReplayCompletion] Ignored resume ack from unexpected player {senderId}.");
            return;
        }

        if (message.targetChecksumId != session.Plan.TargetChecksumId
            || message.generation != session.Generation
            || message.checkpointId != session.Checkpoint.id
            || message.checkpointHash != session.Checkpoint.checksum)
        {
            session.Fail($"Resume acknowledgement mismatch from {senderId}: target="
                + $"{message.targetChecksumId}/{session.Plan.TargetChecksumId}, checkpoint="
                + $"{message.checkpointId}/{message.checkpointHash}, expected="
                + $"{session.Checkpoint.id}/{session.Checkpoint.checksum}.");
            return;
        }

        session.ResumeAcknowledgedPlayerIds.Add(senderId);
        Log.Write($"[NativeReplayCompletion] Resume acknowledgement received from {senderId} "
            + $"count={session.ResumeAcknowledgedPlayerIds.Count}/{session.ExpectedPlayerIds.Count}.");
        TryBroadcastRelease(session);
    }

    private static void TryBroadcastRelease(CompletionSession session)
    {
        if (session.Service.Type != NetGameType.Host
            || session.IsTerminal
            || session.ReleaseBroadcastSent
            || !session.ResumeAcknowledgedPlayerIds.IsSupersetOf(session.ExpectedPlayerIds))
        {
            return;
        }

        session.ReleaseBroadcastSent = true;
        var message = new NativeReplayReleaseMessage
        {
            targetChecksumId = session.Plan.TargetChecksumId,
            generation = session.Generation,
            checkpointId = session.Checkpoint.id,
            checkpointHash = session.Checkpoint.checksum
        };

        try
        {
            session.Service.SendMessage(message);
            Log.Write($"[NativeReplayCompletion] Host broadcast input release target={message.targetChecksumId} "
                + $"checkpoint={message.checkpointId}/{message.checkpointHash}.");
            _ = ReleaseAndCompleteAsync(session, "host received every resume acknowledgement");
        }
        catch (Exception ex)
        {
            session.Fail($"Host could not broadcast replay input release: {Describe(ex)}");
        }
    }

    private static void HandleRelease(CompletionSession session, NativeReplayReleaseMessage message, ulong senderId)
    {
        if (session.Service.Type != NetGameType.Client || session.IsTerminal)
        {
            return;
        }

        ulong hostNetId;
        try
        {
            hostNetId = ((NetClientGameService)session.Service).HostNetId;
        }
        catch (Exception ex)
        {
            session.Fail($"Could not validate the input-release sender: {Describe(ex)}");
            return;
        }

        if (senderId != hostNetId)
        {
            Log.Write($"[NativeReplayCompletion] Ignored input release from non-host {senderId}; host={hostNetId}.");
            return;
        }

        if (message.targetChecksumId != session.Plan.TargetChecksumId
            || message.generation != session.Generation
            || message.checkpointId != session.Checkpoint.id
            || message.checkpointHash != session.Checkpoint.checksum)
        {
            session.Fail($"Input-release payload mismatch: target={message.targetChecksumId}/"
                + $"{session.Plan.TargetChecksumId}, checkpoint={message.checkpointId}/{message.checkpointHash}, "
                + $"local={session.Checkpoint.id}/{session.Checkpoint.checksum}.");
            return;
        }

        session.StopResumeAckRetries("host input release received");
        _ = ReleaseAndCompleteAsync(session, "client received host input release");
    }

    private static async Task ReleaseAndCompleteAsync(CompletionSession session, string reason)
    {
        if (!session.TryBeginRelease())
        {
            return;
        }

        try
        {
            if (PlayerActionsDisabledSetter == null)
            {
                session.Fail("The game PlayerActionsDisabled setter was unavailable.");
                return;
            }

            PlayerActionsDisabledSetter.Invoke(CombatManager.Instance, new object?[] { false });
            NativeReplayActionGate.Release("checkpoint quorum approved by host");
            Log.Write($"[NativeReplayCompletion] Input released: {reason}; checkpoint="
                + $"{session.Checkpoint.id}/{session.Checkpoint.checksum}.");
        }
        catch (Exception ex)
        {
            session.Fail($"Could not release CombatManager.PlayerActionsDisabled: {Describe(ex)}");
            return;
        }

        string detail = reason;
        try
        {
            await NGame.Instance!.Transition.FadeIn();
            Log.Write("[NativeReplayCompletion] FadeIn completed after successful resume.");
        }
        catch (Exception ex)
        {
            detail += $"; FadeIn failed after input release: {Describe(ex)}";
            Log.Write($"[NativeReplayCompletion] FAIL: {detail}");
            session.Fail(detail);
            _ = NativeReplayRestorer.RecoverToMainMenuAsync(detail);
            return;
        }

        session.Succeed(detail);
    }

    private static NativeReplayCompletionResult Failure(string detail)
    {
        Log.Write($"[NativeReplayCompletion] FAIL: {detail}");
        return new NativeReplayCompletionResult(false, detail);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private sealed class PreCheckpointMonitor
    {
        internal readonly INetGameService Service;
        internal readonly RunLobby? RunLobby;
        internal readonly ChecksumTracker ChecksumTracker;
        internal readonly NativeReplayRestorePlan Plan;

        internal PreCheckpointMonitor(
            INetGameService service,
            RunLobby? runLobby,
            ChecksumTracker checksumTracker,
            NativeReplayRestorePlan plan)
        {
            Service = service;
            RunLobby = runLobby;
            ChecksumTracker = checksumTracker;
            Plan = plan;
        }

        internal void Attach()
        {
            Service.Disconnected += OnDisconnected;
            SubscribeRemoteDisconnect(RunLobby, OnRemotePlayerDisconnected);
            ChecksumTracker.StateDiverged += OnStateDiverged;
        }

        internal void Detach()
        {
            try
            {
                Service.Disconnected -= OnDisconnected;
                UnsubscribeRemoteDisconnect(RunLobby, OnRemotePlayerDisconnected);
                ChecksumTracker.StateDiverged -= OnStateDiverged;
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_MONITOR_CLEANUP_ERROR: {Describe(ex)}");
            }
        }

        private void OnDisconnected(NetErrorInfo error) =>
            ReportPreCheckpointFailure(this, $"New replay service disconnected before checkpoint setup: {error}.");

        private void OnRemotePlayerDisconnected(ulong playerId)
        {
            Log.Write($"[NativeReplayCompletion] PRECHECKPOINT_REMOTE_PLAYER_DISCONNECTED peer={playerId} "
                + $"target={Plan.TargetChecksumId} events={Plan.EventCount}.");
            ReportPreCheckpointFailure(this, $"Remote player {playerId} disconnected before checkpoint setup.");
        }

        private void OnStateDiverged(ulong remoteId, NetFullCombatState _) =>
            ReportPreCheckpointFailure(this, $"Native automatic checksum diverged with peer {remoteId} before checkpoint setup.");
    }

    private sealed class CompletionSession
    {
        internal readonly INetGameService Service;
        internal readonly RunLobby? RunLobby;
        internal readonly NativeReplayRestorePlan Plan;
        internal readonly HashSet<ulong> ExpectedPlayerIds;
        internal readonly ulong LocalNetId;
        internal readonly int Generation;
        internal readonly NetChecksumData Checkpoint;
        internal readonly ChecksumTracker ChecksumTracker;
        internal readonly HashSet<ulong> ReadyPlayerIds = new();
        internal readonly HashSet<ulong> ResumeAcknowledgedPlayerIds = new();
        internal readonly TaskCompletionSource<NativeReplayCompletionResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ResumeBroadcastSent;
        internal bool ReleaseBroadcastSent;

        private bool _releaseStarted;
        private bool _terminal;
        private bool _readyRetryStopRequested;
        private Task? _readyRetryTask;
        private bool _resumeAckRetryStopRequested;
        private Task? _resumeAckRetryTask;

        internal CompletionSession(
            INetGameService service,
            RunLobby? runLobby,
            NativeReplayRestorePlan plan,
            HashSet<ulong> expectedPlayerIds,
            ulong localNetId,
            int generation,
            NetChecksumData checkpoint,
            ChecksumTracker checksumTracker)
        {
            Service = service;
            RunLobby = runLobby;
            Plan = plan;
            ExpectedPlayerIds = expectedPlayerIds;
            LocalNetId = localNetId;
            Generation = generation;
            Checkpoint = checkpoint;
            ChecksumTracker = checksumTracker;
        }

        internal bool IsTerminal
        {
            get
            {
                lock (this)
                {
                    return _terminal;
                }
            }
        }

        internal bool TryBeginRelease()
        {
            lock (this)
            {
                if (_terminal || _releaseStarted)
                {
                    return false;
                }

                _releaseStarted = true;
                return true;
            }
        }

        internal void SendReady(int attempt, bool initial = false)
        {
            Service.SendMessage(new NativeReplayReadyMessage
            {
                targetChecksumId = Plan.TargetChecksumId,
                reporterNetId = LocalNetId,
                generation = Generation,
                checkpointId = Checkpoint.id,
                checkpointHash = Checkpoint.checksum
            });
            Log.Write($"[NativeReplayCompletion] {(initial ? "Ready sent" : "READY_RETRY_SENT")} "
                + $"netId={LocalNetId} attempt={attempt} target={Plan.TargetChecksumId} "
                + $"events={Plan.EventCount} checkpoint={Checkpoint.id}/{Checkpoint.checksum}.");
        }

        internal void StartReadyRetryLoop()
        {
            lock (this)
            {
                if (_readyRetryTask != null)
                {
                    return;
                }

                _readyRetryTask = RetryReadyUntilTerminalAsync();
            }

            Log.Write($"[NativeReplayCompletion] READY_RETRY_START netId={LocalNetId} "
                + $"target={Plan.TargetChecksumId} interval={ReadyRetryInterval.TotalMilliseconds:0}ms.");
        }

        internal void SendResumeAck(int attempt, bool initial = false)
        {
            Service.SendMessage(new NativeReplayResumeAckMessage
            {
                targetChecksumId = Plan.TargetChecksumId,
                reporterNetId = LocalNetId,
                generation = Generation,
                checkpointId = Checkpoint.id,
                checkpointHash = Checkpoint.checksum
            });
            Log.Write($"[NativeReplayCompletion] {(initial ? "Resume acknowledgement sent" : "RESUME_ACK_RETRY_SENT")} "
                + $"netId={LocalNetId} attempt={attempt} target={Plan.TargetChecksumId} "
                + $"checkpoint={Checkpoint.id}/{Checkpoint.checksum}.");
        }

        internal void StartResumeAckRetryLoop()
        {
            lock (this)
            {
                if (_resumeAckRetryTask != null)
                {
                    return;
                }

                _resumeAckRetryTask = RetryResumeAckUntilTerminalAsync();
            }

            Log.Write($"[NativeReplayCompletion] RESUME_ACK_RETRY_START netId={LocalNetId} "
                + $"target={Plan.TargetChecksumId} interval={ResumeAckRetryInterval.TotalMilliseconds:0}ms.");
        }

        internal void StopReadyRetries(string reason)
        {
            lock (this)
            {
                if (_readyRetryStopRequested)
                {
                    return;
                }

                _readyRetryStopRequested = true;
            }

            Log.Write($"[NativeReplayCompletion] READY_RETRY_STOP netId={LocalNetId} "
                + $"target={Plan.TargetChecksumId} reason={reason}.");
        }

        internal void StopResumeAckRetries(string reason)
        {
            lock (this)
            {
                if (_resumeAckRetryStopRequested)
                {
                    return;
                }

                _resumeAckRetryStopRequested = true;
            }

            Log.Write($"[NativeReplayCompletion] RESUME_ACK_RETRY_STOP netId={LocalNetId} "
                + $"target={Plan.TargetChecksumId} reason={reason}.");
        }

        private async Task RetryReadyUntilTerminalAsync()
        {
            var sw = Stopwatch.StartNew();
            int attempt = 1;
            try
            {
                while (sw.Elapsed < QuorumTimeout && !ShouldStopReadyRetries())
                {
                    await WaitForRetryIntervalAsync();
                    if (ShouldStopReadyRetries())
                    {
                        break;
                    }

                    attempt++;
                    try
                    {
                        SendReady(attempt);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[NativeReplayCompletion] READY_RETRY_SEND_FAILED netId={LocalNetId} "
                            + $"attempt={attempt} target={Plan.TargetChecksumId}: {Describe(ex)}.");
                    }
                }

                if (!ShouldStopReadyRetries())
                {
                    StopReadyRetries("retry timeout");
                    Fail($"Replay Ready retry timed out after {QuorumTimeout.TotalMinutes:0} minutes.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayCompletion] READY_RETRY_ERROR netId={LocalNetId} "
                    + $"target={Plan.TargetChecksumId}: {Describe(ex)}.");
                StopReadyRetries("retry loop error");
            }
        }

        private async Task RetryResumeAckUntilTerminalAsync()
        {
            var sw = Stopwatch.StartNew();
            int attempt = 1;
            try
            {
                while (sw.Elapsed < QuorumTimeout && !ShouldStopResumeAckRetries())
                {
                    await Task.Delay(ResumeAckRetryInterval);
                    if (ShouldStopResumeAckRetries())
                    {
                        break;
                    }

                    attempt++;
                    try
                    {
                        SendResumeAck(attempt);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[NativeReplayCompletion] RESUME_ACK_RETRY_SEND_FAILED netId={LocalNetId} "
                            + $"attempt={attempt} target={Plan.TargetChecksumId}: {Describe(ex)}.");
                    }
                }

                if (!ShouldStopResumeAckRetries())
                {
                    StopResumeAckRetries("retry timeout");
                    Fail($"Replay resume acknowledgement retry timed out after {QuorumTimeout.TotalMinutes:0} minutes.");
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayCompletion] RESUME_ACK_RETRY_ERROR netId={LocalNetId} "
                    + $"target={Plan.TargetChecksumId}: {Describe(ex)}.");
                StopResumeAckRetries("retry loop error");
            }
        }

        private async Task WaitForRetryIntervalAsync()
        {
            await Task.Delay(ReadyRetryInterval);
        }

        private bool ShouldStopReadyRetries()
        {
            lock (this)
            {
                return _readyRetryStopRequested || _terminal || _releaseStarted;
            }
        }

        private bool ShouldStopResumeAckRetries()
        {
            lock (this)
            {
                return _resumeAckRetryStopRequested || _terminal || _releaseStarted;
            }
        }

        internal void Succeed(string detail)
        {
            lock (this)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Completion.TrySetResult(new NativeReplayCompletionResult(true, detail));
        }

        internal void Fail(string detail)
        {
            lock (this)
            {
                if (_terminal)
                {
                    return;
                }

                _terminal = true;
            }

            Log.Write($"[NativeReplayCompletion] FAIL: {detail}");
            Completion.TrySetResult(new NativeReplayCompletionResult(false, detail));
        }

        internal void OnReady(NativeReplayReadyMessage message, ulong senderId)
        {
            HandleReady(this, message, senderId);
        }

        internal void OnResume(NativeReplayResumeMessage message, ulong senderId)
        {
            HandleResume(this, message, senderId);
        }

        internal void OnResumeAck(NativeReplayResumeAckMessage message, ulong senderId)
        {
            HandleResumeAck(this, message, senderId);
        }

        internal void OnRelease(NativeReplayReleaseMessage message, ulong senderId)
        {
            HandleRelease(this, message, senderId);
        }

        internal void OnDisconnected(NetErrorInfo error)
        {
            Fail($"New replay service disconnected before completion: {error}.");
        }

        internal void OnRemotePlayerDisconnected(ulong playerId)
        {
            if (playerId == LocalNetId || !ExpectedPlayerIds.Contains(playerId) || IsTerminal)
            {
                return;
            }

            Log.Write($"[NativeReplayCompletion] REMOTE_PLAYER_DISCONNECTED peer={playerId} "
                + $"phase=completion generation={Generation} target={Plan.TargetChecksumId} "
                + $"checkpoint={Checkpoint.id}/{Checkpoint.checksum}.");
            Fail($"Remote player {playerId} disconnected during replay completion.");
        }

        internal void OnStateDiverged(ulong remoteId, NetFullCombatState _)
        {
            Fail($"Native replay checkpoint diverged with peer {remoteId} before completion.");
        }
    }
}
