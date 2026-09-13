using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
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

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(reporterNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        reporterNetId = reader.ReadULong();
    }
}

public record struct NativeReplayResumeMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
    }
}

internal sealed record NativeReplayCompletionResult(bool Success, string Detail);

internal static class NativeReplayCompletion
{
    private static readonly TimeSpan QuorumTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadyRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly object Gate = new();
    private static readonly MethodInfo? PlayerActionsDisabledSetter =
        AccessTools.PropertySetter(typeof(CombatManager), "PlayerActionsDisabled");
    private static CompletionSession? _activeSession;

    internal static async Task<NativeReplayCompletionResult> WaitForQuorumAsync(
        NativeReplayRestorePlan plan)
    {
        if (plan is null)
        {
            return Failure("Replay completion plan was null.");
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
            plan,
            expectedPlayerIds,
            localNetId);

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
    }

    private static void UnregisterHandlers(CompletionSession session)
    {
        session.StopReadyRetries("handlers unregistered");
        try
        {
            session.Service.UnregisterMessageHandler<NativeReplayReadyMessage>(session.OnReady);
            session.Service.UnregisterMessageHandler<NativeReplayResumeMessage>(session.OnResume);
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayCompletion] Handler cleanup failed: {Describe(ex)}");
        }
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

        if (!session.ExpectedPlayerIds.Contains(senderId))
        {
            Log.Write($"[NativeReplayCompletion] Ignored ready from unexpected player {senderId}.");
            return;
        }

        session.ReadyPlayerIds.Add(senderId);
        Log.Write($"[NativeReplayCompletion] Ready received from {senderId} "
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

        session.StopReadyRetries("host resume received");
        _ = ReleaseAndCompleteAsync(session, "client received host resume");
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
        var message = new NativeReplayResumeMessage
        {
            targetChecksumId = session.Plan.TargetChecksumId
        };

        try
        {
            session.Service.SendMessage(message);
            Log.Write($"[NativeReplayCompletion] Host broadcast resume for target={message.targetChecksumId} "
                + $"localEvents={session.Plan.EventCount}; releasing host input.");
            _ = ReleaseAndCompleteAsync(session, "host reached replay completion quorum");
        }
        catch (Exception ex)
        {
            session.Fail($"Host could not broadcast replay resume: {Describe(ex)}");
        }
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
            Log.Write($"[NativeReplayCompletion] Input released: {reason}.");
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
            detail += $"; FadeIn failed but input was released: {Describe(ex)}";
            Log.Write($"[NativeReplayCompletion] {detail}");
        }

        session.Succeed(detail);
    }

    private static NativeReplayCompletionResult Failure(string detail)
    {
        Log.Write($"[NativeReplayCompletion] FAIL: {detail}");
        return new NativeReplayCompletionResult(false, detail);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private sealed class CompletionSession
    {
        internal readonly INetGameService Service;
        internal readonly NativeReplayRestorePlan Plan;
        internal readonly HashSet<ulong> ExpectedPlayerIds;
        internal readonly ulong LocalNetId;
        internal readonly HashSet<ulong> ReadyPlayerIds = new();
        internal readonly TaskCompletionSource<NativeReplayCompletionResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ResumeBroadcastSent;

        private bool _releaseStarted;
        private bool _terminal;
        private bool _readyRetryStopRequested;
        private Task? _readyRetryTask;

        internal CompletionSession(
            INetGameService service,
            NativeReplayRestorePlan plan,
            HashSet<ulong> expectedPlayerIds,
            ulong localNetId)
        {
            Service = service;
            Plan = plan;
            ExpectedPlayerIds = expectedPlayerIds;
            LocalNetId = localNetId;
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
                reporterNetId = LocalNetId
            });
            Log.Write($"[NativeReplayCompletion] {(initial ? "Ready sent" : "READY_RETRY_SENT")} "
                + $"netId={LocalNetId} attempt={attempt} target={Plan.TargetChecksumId} "
                + $"events={Plan.EventCount}.");
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
    }
}
