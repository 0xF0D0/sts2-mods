using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Godot;

namespace UndoSync;

/// <summary>Test-only reliable transport message for the native replay payload probe.</summary>
public record struct NativeReplayPayloadProbeMessage : INetMessage
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public uint targetChecksumId;
    public int eventCount;
    public CombatReplay canonicalReplay;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(eventCount);
        writer.Write(canonicalReplay);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        eventCount = reader.ReadInt();
        canonicalReplay = reader.Read<CombatReplay>();
    }
}

/// <summary>Client-to-host acknowledgement for the native replay payload probe.</summary>
public record struct NativeReplayPayloadProbeAckMessage : INetMessage
{
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => false;

    public uint targetChecksumId;
    public int eventCount;
    public ulong responderNetId;
    public bool accepted;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(eventCount);
        writer.WriteULong(responderNetId);
        writer.WriteBool(accepted);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        eventCount = reader.ReadInt();
        responderNetId = reader.ReadULong();
        accepted = reader.ReadBool();
    }
}

/// <summary>
/// Opt-in native event-journal probe. It seeds the native CombatReplay with one real host-player
/// card input, then uses the same loaded-multiplayer lobby path as the main menu after a full
/// RunManager cleanup and replays the complete native event journal on both peers. This is a
/// test-only probe and never participates in normal undo.
/// Run it with Steam disabled and without --fastmp. The host uses
/// --undosync-mpfuzz --undosync-mpfuzz-role=host --undosync-native-replay-probe; the client uses
/// --undosync-mpfuzz --undosync-mpfuzz-role=client --undosync-mpfuzz-clientid=1001
/// --undosync-native-replay-probe. The client re-entry uses the native JoinFlow and
/// ClientLoadJoin path directly, with a bounded retry for the host-listener startup race; it
/// deliberately bypasses NJoinFriendScreen because that UI's test-only FastMpJoin side effect
/// changes the game profile when a native --clientId is used.
/// Add --undosync-native-replay-probe-payload-roundtrip to additionally send the host's native
/// CombatReplay prefix over the live reliable ENet session, validate it on the client through
/// NativeReplayJournal, and wait for a bounded acknowledgement before the existing replay flow.
/// </summary>
internal static class NativeReplayProbe
{
    private const string ProbeArg = "undosync-native-replay-probe";
    private const string PayloadRoundTripArg = "undosync-native-replay-probe-payload-roundtrip";
    private const string SeedEventsArg = "undosync-native-replay-probe-seed-events";
    private const ushort EnetPort = 33771;
    private static readonly TimeSpan MainMenuTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LobbyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PayloadRoundTripTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    // Per-action seed confirmation is condition-based but intentionally much shorter than the
    // lobby/load cadence: 20ms avoids turning a 1000-event throughput probe into a 200-second
    // benchmark while still allowing the game's frame/network pump to advance between polls.
    private static readonly TimeSpan SeedEventPollInterval = TimeSpan.FromMilliseconds(20);
    private const int MaxNativeJoinAttempts = 3;
    private static readonly TimeSpan NativeJoinRetryDelay = TimeSpan.FromMilliseconds(500);

    // CombatReplayWriter._replay is the native writer field verified in the decompiled source.
    // The object is retained directly; no custom snapshot, translation, or serialization exists.
    private static readonly FieldInfo? FReplay =
        AccessTools.Field(typeof(CombatReplayWriter), "_replay");

    private static readonly MethodInfo? MCompareChecksums =
        AccessTools.Method(typeof(ChecksumTracker), "CompareChecksums");

    private static readonly FieldInfo? FTrackedChecksumData =
        MCompareChecksums?.GetParameters().FirstOrDefault()?.ParameterType is { } trackedType
            ? AccessTools.Field(trackedType, "data")
            : null;

    private static readonly FieldInfo? FChecksumId =
        FTrackedChecksumData?.FieldType is { } checksumType
            ? AccessTools.Field(checksumType, "id")
            : null;

    // NMultiplayerLoadGameScreen._runLobby is the native screen's loaded-lobby instance.
    private static readonly FieldInfo? FLoadLobby =
        AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby");

    // The native screen's button handler sends LobbyPlayerSetReadyMessage through LoadRunLobby.
    private static readonly MethodInfo? MEmbark =
        AccessTools.Method(typeof(NMultiplayerLoadGameScreen), "OnEmbarkPressed");

    private static bool _compareObserverInstalled;
    private static bool _expectedComparisonArmed;
    private static uint _expectedComparisonId;
    private static bool _expectedComparisonObserved;

    internal static bool IsEnabled => CommandLineHelper.HasArg(ProbeArg);
    private static bool PayloadRoundTripEnabled => IsEnabled && CommandLineHelper.HasArg(PayloadRoundTripArg);

    internal static void LogActivationRequirement()
    {
        if (IsEnabled)
        {
            Log.Write("[NativeReplayProbe] M3 activation: omit --clientId and --fastmp. "
                + "The client uses native JoinFlow/ClientLoadJoin directly, bypassing "
                + "NJoinFriendScreen.FastMpJoin to avoid its test-only profile side effect, "
                + "with bounded native join retries for host listener startup.");
            Log.Write($"[NativeReplayProbe] payload round-trip opt-in="
                + $"{CommandLineHelper.HasArg(PayloadRoundTripArg)} "
                + $"arg=--{PayloadRoundTripArg} "
                + "(PacketWriter payload bytes only; not ENet wire bytes).");
        }

        if (IsEnabled && CommandLineHelper.HasArg("fastmp"))
        {
            Log.Write("[NativeReplayProbe] FAIL activation: omit --fastmp. The probe creates its own "
                + "ENet harness; --fastmp triggers the unrelated NMainMenu automatic host/join flow.");
        }
    }

    internal static async Task RunAsync(bool isHost, ulong clientId)
    {
        string role = isHost ? "host" : "client";
        Log.Write($"[NativeReplayProbe] START role={role} requestedClientId={clientId}");

        if (!IsEnabled)
        {
            return;
        }

        if (!TryGetSeedEventCount(out int expectedEvents, out string seedCountError))
        {
            Log.Write($"[NativeReplayProbe] FAIL activation: {seedCountError}; no session was torn down.");
            return;
        }

        Log.Write($"[NativeReplayProbe] M3 seed configuration expectedEvents={expectedEvents} "
            + $"historyLimit=none seedSettlePollMs={SeedEventPollInterval.TotalMilliseconds:F0} "
            + $"payloadRoundTrip={PayloadRoundTripEnabled}");

        if (CommandLineHelper.HasArg("fastmp"))
        {
            Log.Write("[NativeReplayProbe] FAIL activation: --fastmp must be omitted; its NMainMenu "
                + "automatic host/join flow conflicts with the probe's ENet harness. "
                + "No session was torn down.");
            return;
        }

        if (SteamInitializer.Initialized)
        {
            Log.Write("[NativeReplayProbe] FAIL activation: SteamInitializer.Initialized=true. "
                + "M2 supports only Steam-disabled ENet (--force-steam=off); no session was torn down.");
            return;
        }

        InstallChecksumComparisonObserver();

        if (FReplay == null || FLoadLobby == null || MEmbark == null)
        {
            Log.Write($"[NativeReplayProbe] FAIL verified reflection surface "
                + $"replay={FReplay != null} loadLobby={FLoadLobby != null} embark={MEmbark != null}; "
                + "no session was torn down.");
            return;
        }

        NGame game = NGame.Instance
            ?? throw new InvalidOperationException("NGame.Instance was null during native replay probe capture.");

        CombatReplay? replay = FReplay.GetValue(RunManager.Instance.CombatReplayWriter) as CombatReplay;
        if (replay?.serializableRun == null)
        {
            Log.Write("[NativeReplayProbe] FAIL capture: native CombatReplay or start SerializableRun "
                + "was unavailable; no session was torn down.");
            return;
        }

        SerializableRun run = replay.serializableRun;
        ulong originalNetId = RunManager.Instance.NetService.NetId;
        if (!isHost && originalNetId != clientId)
        {
            Log.Write($"[NativeReplayProbe] FAIL client NetId sanity check: "
                + $"capturedOriginalNetId={originalNetId} requestedClientId={clientId}; "
                + "no session was torn down.");
            return;
        }

        ChecksumTracker captureTracker = RunManager.Instance.ChecksumTracker;
        uint captureChecksumBaseline = captureTracker.NextId;
        int captureEventBaseline = replay.events.Count;
        Stopwatch captureStopwatch = Stopwatch.StartNew();
        if (isHost)
        {
            (bool success, string detail) seed = await TrySeedHostCardAsync(replay);
            if (!seed.success)
            {
                captureStopwatch.Stop();
                Log.Write($"[NativeReplayProbe] FAIL capture action: {seed.detail}; no session was torn down.");
                return;
            }

            Log.Write($"[NativeReplayProbe] CAPTURE_ACTION role=host detail={seed.detail} "
                + $"eventsBefore={captureEventBaseline} checksumBaseline={captureChecksumBaseline} "
                + "route=CardModel.TryManualPlay->ActionQueueSynchronizer.RequestEnqueue");

            // TryManualPlay only enqueues. Complete the real card action before issuing the next
            // native action; otherwise CanUndoRedo correctly rejects the next seed while the first
            // PlayCardAction is still executing. Keep this on the dedicated 20ms condition cadence
            // so the capture stopwatch measures actual seed throughput, not an artificial 200ms gap.
            bool initialSettled = await WaitForConditionAsync(
                () => replay.events.Count >= captureEventBaseline + 1
                      && captureTracker.NextId > captureChecksumBaseline
                      && RunManager.Instance.ActionQueueSet is { } initialQueues
                      && UndoSyncMod.IsActionQueueIdle(initialQueues, out _),
                LoadTimeout,
                SeedEventPollInterval);
            if (!initialSettled)
            {
                captureStopwatch.Stop();
                Log.Write($"[NativeReplayProbe] M3_CAPTURE_INITIAL_FAIL "
                    + $"eventsBaseline={captureEventBaseline} checksumBaseline={captureChecksumBaseline} "
                    + $"events={replay.events.Count} checksumNextId={captureTracker.NextId} "
                    + $"blockers={UndoSyncMod.DescribeUndoRedoBlockers()} settlePollMs="
                    + $"{SeedEventPollInterval.TotalMilliseconds:F0}; no session was torn down.");
                return;
            }

            for (int ordinal = 2; ordinal <= expectedEvents; ordinal++)
            {
                if (!await TrySeedAdditionalConsoleEventAsync(
                        replay, captureTracker, RunManager.Instance.NetService.NetId, ordinal))
                {
                    captureStopwatch.Stop();
                    return;
                }

                if (ordinal == 2 || ordinal % 100 == 0 || ordinal == expectedEvents)
                {
                    Log.Write($"[NativeReplayProbe] M3_CAPTURE_PROGRESS role=host ordinal={ordinal} "
                        + $"expectedEvents={expectedEvents} journalCount={replay.events.Count}");
                }
            }
        }

        bool captureReady = await WaitForConditionAsync(
            () => replay.events.Count == expectedEvents
                  && captureTracker.NextId > captureChecksumBaseline
                  && RunManager.Instance.ActionQueueSet is { } captureQueues
                  && UndoSyncMod.IsActionQueueIdle(captureQueues, out _),
            LoadTimeout);
        captureStopwatch.Stop();
        if (!captureReady)
        {
            Log.Write($"[NativeReplayProbe] FAIL M3 capture exact count role={role} "
                + $"expectedEvents={expectedEvents} actualEvents={replay.events.Count} "
                + $"eventsBaseline={captureEventBaseline} checksumNextId={captureTracker.NextId} "
                + $"checksumBaseline={captureChecksumBaseline}; "
                + "no session was torn down.");
            return;
        }

        double captureEventsPerSecond = EventsPerSecond(replay.events.Count, captureStopwatch.Elapsed);
        Log.Write($"[NativeReplayProbe] M3_CAPTURE_COMPLETE role={role} "
            + $"expectedEvents={expectedEvents} actualEvents={replay.events.Count} "
            + $"elapsedMs={captureStopwatch.Elapsed.TotalMilliseconds:F1} "
            + $"eventsPerSecond={captureEventsPerSecond:F2}");
        Log.Write($"[NativeReplayProbe] CAPTURE_READY role={role} "
            + $"events={replay.events.Count} expectedEvents={expectedEvents} "
            + $"newEvents={replay.events.Count - captureEventBaseline} "
            + $"checksumNextId={captureTracker.NextId} checksumBaseline={captureChecksumBaseline} "
            + "queuesIdle=true journal=native-object");

        string originalIds = string.Join(",", run.Players.Select(player => player.NetId));
        Log.Write($"[NativeReplayProbe] CAPTURE nativeCombatReplay=true startSerializableRun=true "
            + $"events={replay.events.Count} checksums={replay.checksumData.Count} "
            + $"originalNetId={originalNetId} originalPlayerNetIds=[{originalIds}]");

        uint payloadTargetChecksumId = 0;
        if (PayloadRoundTripEnabled && isHost)
        {
            if (replay.checksumData.Count == 0)
            {
                Log.Write("[NativeReplayProbe] FAIL payload round-trip capture: native replay "
                    + "contained no checksum data; existing re-entry/replay flow was not started.");
                return;
            }

            // ChecksumTracker.NextId is the next id to allocate, not necessarily an id present in
            // the replay journal. Select the actual final recorded checksum so both the canonical
            // plan and the client validation use a checksum that exists on the wire payload.
            payloadTargetChecksumId = replay.checksumData
                .Max(checksum => checksum.checksumData.id);

            if (!NativeReplayJournal.TryCapturePoint(
                    payloadTargetChecksumId,
                    "native replay payload round-trip",
                    out int payloadEventCount,
                    out string payloadCaptureError))
            {
                Log.Write($"[NativeReplayProbe] FAIL payload round-trip capture: {payloadCaptureError}; "
                    + "existing re-entry/replay flow was not started.");
                return;
            }

            Log.Write($"[NativeReplayProbe] PAYLOAD_CAPTURE_READY targetChecksum={payloadTargetChecksumId} "
                + $"eventCount={payloadEventCount} source=native-CombatReplay "
                + "serializableRun=native");
        }

        // The payload must round-trip while the original native replay is still owned by the
        // live combat. ReturnToMainMenu/RunManager cleanup clears or mutates that object.
        if (PayloadRoundTripEnabled)
        {
            PayloadRoundTripResult payloadResult = await RunPayloadRoundTripAsync(
                isHost,
                payloadTargetChecksumId,
                expectedEvents);
            Log.Write($"[NativeReplayProbe] PAYLOAD_ROUND_TRIP_RESULT role={role} "
                + $"success={payloadResult.Success} targetChecksum={payloadResult.TargetChecksumId} "
                + $"eventCount={payloadResult.EventCount} "
                + $"serializedPayloadBytes={payloadResult.SerializedPayloadBytes} "
                + "byteMeasurement=PacketWriter-payload-not-ENet-wire "
                + $"detail={payloadResult.Detail}");
            if (!payloadResult.Success)
            {
                Log.Write($"[NativeReplayProbe] FAIL payload round-trip role={role}; "
                    + "existing native replay flow was not continued.");
                return;
            }
        }

        try
        {
            Log.Write($"[NativeReplayProbe] TEARDOWN_BEGIN role={role} originalNetId={originalNetId} "
                + "route=NGame.ReturnToMainMenu -> RunManager.CleanUp");
            await game.ReturnToMainMenu();

            if (!await WaitForConditionAsync(
                    () => game.MainMenu != null,
                    MainMenuTimeout))
            {
                Log.Write($"[NativeReplayProbe] FAIL main-menu re-entry timeout role={role}; "
                    + "old multiplayer session was torn down but fresh lobby was not started.");
                return;
            }

            Log.Write($"[NativeReplayProbe] TEARDOWN_COMPLETE role={role} "
                + "mainMenu=true oldRunStateCleared=true");

            if (isHost)
            {
                NMultiplayerSubmenu multiplayer = game.MainMenu!.OpenMultiplayerSubmenu();
                multiplayer.StartHost(run);
                Log.Write($"[NativeReplayProbe] RECREATE_HOST_STARTED transport=ENet "
                    + $"hostEndpoint=127.0.0.1:{EnetPort}");
            }
            else
            {
                JoinFlow? successfulJoinFlow = null;
                JoinResult joinResult = default;
                int attemptsUsed = 0;
                for (int attempt = 1; attempt <= MaxNativeJoinAttempts; attempt++)
                {
                    JoinFlow joinFlow = new JoinFlow(
                        new NetClientGameService(PeerVersionInfo.LocalDefault()));
                    attemptsUsed = attempt;
                    try
                    {
                        Log.Write($"[NativeReplayProbe] CLIENT_JOIN_ATTEMPT attempt={attempt}/"
                            + $"{MaxNativeJoinAttempts} route=JoinFlow.Begin");
                        joinResult = await joinFlow.Begin(
                            new ENetClientConnectionInitializer(originalNetId, "127.0.0.1", EnetPort),
                            game.GetTree());
                        successfulJoinFlow = joinFlow;
                        break;
                    }
                    catch (ClientConnectionFailedException ex)
                    {
                        NetError reason = ex.info.GetReason();
                        DisconnectJoinServiceSafely(joinFlow);
                        Log.Write($"[NativeReplayProbe] CLIENT_JOIN_ATTEMPT_FAILED attempt={attempt}/"
                            + $"{MaxNativeJoinAttempts} reason={reason}");
                        if (attempt == MaxNativeJoinAttempts)
                        {
                            Log.Write($"[NativeReplayProbe] FAIL client native join exhausted "
                                + $"attempts={MaxNativeJoinAttempts} reason={reason}; "
                                + "host listener did not become ready. No load screen was pushed.");
                            return;
                        }

                        await Task.Delay(NativeJoinRetryDelay);
                    }
                    catch
                    {
                        DisconnectJoinServiceSafely(joinFlow);
                        throw;
                    }
                }

                if (successfulJoinFlow == null)
                {
                    Log.Write("[NativeReplayProbe] FAIL client native join produced no result; "
                        + "no load screen was pushed.");
                    return;
                }

                if (joinResult.sessionState != RunSessionState.InLoadedLobby
                    || joinResult.gameMode != GameMode.Standard
                    || !joinResult.loadJoinResponse.HasValue)
                {
                    Log.Write($"[NativeReplayProbe] FAIL client loaded-lobby join result: "
                        + $"sessionState={joinResult.sessionState?.ToString() ?? "<null>"} "
                        + $"gameMode={joinResult.gameMode} "
                        + $"loadJoinResponse={joinResult.loadJoinResponse.HasValue}; "
                        + "expected InLoadedLobby/Standard/response. No load screen was pushed.");
                    DisconnectJoinServiceSafely(successfulJoinFlow);
                    return;
                }

                NMultiplayerLoadGameScreen clientLoadScreen =
                    game.MainMenu!.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                clientLoadScreen.InitializeAsClient(
                    successfulJoinFlow.NetService,
                    joinResult.loadJoinResponse.Value);
                game.MainMenu.SubmenuStack.Push(clientLoadScreen);
                Log.Write($"[NativeReplayProbe] RECREATE_CLIENT_NATIVE_JOIN_STARTED "
                    + $"claimedClientNetId={originalNetId} requestedClientId={clientId} "
                    + $"attemptsUsed={attemptsUsed} "
                    + "route=JoinFlow.Begin+ClientLoadJoin "
                    + "ui=NJoinFriendScreen.bypassed boundedNativeJoinRetry=true");
            }

            NMultiplayerLoadGameScreen loadScreen =
                game.MainMenu!.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
            if (!await WaitForConditionAsync(
                    () => FLoadLobby.GetValue(loadScreen) is LoadRunLobby lobby
                          && lobby.PlayerCount >= 2,
                    LobbyTimeout))
            {
                Log.Write($"[NativeReplayProbe] FAIL loaded-lobby re-entry timeout role={role} "
                    + $"screenBound={FLoadLobby.GetValue(loadScreen) != null}");
                return;
            }

            var loadLobby = (LoadRunLobby)FLoadLobby.GetValue(loadScreen)!;
            string recreatedIds = string.Join(",", loadLobby.PlayerIds);
            ulong recreatedNetId = loadLobby.NetService.NetId;
            Log.Write($"[NativeReplayProbe] LOADED_LOBBY role={role} recreatedNetId={recreatedNetId} "
                + $"players={loadLobby.PlayerCount} playerNetIds=[{recreatedIds}] "
                + "route=LoadRunLobby+ClientLoadJoin");

            MEmbark.Invoke(loadScreen, new object?[] { null });
            Log.Write($"[NativeReplayProbe] READY_SENT role={role} recreatedNetId={recreatedNetId}");

            bool postLoadReady = await WaitForConditionAsync(
                () => IsOfficialPostLoadReady(game),
                LoadTimeout);
            if (!postLoadReady)
            {
                Log.Write($"[NativeReplayProbe] FAIL post-load readiness timeout role={role} "
                    + DescribePostLoadReadiness(game));
                return;
            }

            bool queuesIdle = await WaitForConditionAsync(
                () => RunManager.Instance?.ActionQueueSet is { } queues
                      && UndoSyncMod.IsActionQueueIdle(queues, out _),
                LoadTimeout);
            bool activeRun = RunManager.Instance?.IsInProgress == true;
            bool connected = RunManager.Instance?.NetService.IsConnected == true;
            bool currentState = RunManager.Instance?.DebugOnlyGetState() != null;
            bool runNode = game.CurrentRunNode != null;
            bool combatManager = CombatManager.Instance != null;
            bool combatActive = CombatManager.Instance?.IsInProgress == true;
            bool actionExecutorPaused = RunManager.Instance?.ActionExecutor.IsPaused == true;
            Log.Write($"[NativeReplayProbe] POST_LOAD role={role} activeRun={activeRun} "
                + $"connected={connected} currentState={currentState} "
                + $"setUpSavedMultiplayer=true runNode={runNode} "
                + $"combatManager={combatManager} combatActive={combatActive} "
                + $"actionExecutorPaused={actionExecutorPaused} queuesIdle={queuesIdle} "
                + $"recreatedNetId={RunManager.Instance?.NetService.NetId}");

            if (!postLoadReady || !queuesIdle)
            {
                Log.Write($"[NativeReplayProbe] FAIL post-load confirmation role={role} "
                    + DescribePostLoadReadiness(game)
                    + $" queuesIdle={queuesIdle}; official load or queue-idle invariant was not confirmed.");
                return;
            }

            Log.Write($"[NativeReplayProbe] REPLAY_BEGIN role={role} eventCount={replay.events.Count} "
                + "scope=entire-native-replay-events");
            Stopwatch replayStopwatch = Stopwatch.StartNew();
            bool replayComplete = await ReplayAllEventsAsync(replay);
            bool replayQueuesIdle = replayComplete && await WaitForConditionAsync(
                () => RunManager.Instance.ActionQueueSet is { } replayQueues
                      && UndoSyncMod.IsActionQueueIdle(replayQueues, out _),
                LoadTimeout);
            replayStopwatch.Stop();
            int replayedEvents = replay.events.Count;
            if (!replayComplete || !replayQueuesIdle)
            {
                Log.Write($"[NativeReplayProbe] M3_REPLAY_FAIL role={role} "
                    + $"expectedEvents={expectedEvents} actualEvents={replayedEvents} "
                    + $"replayedEvents={replayedEvents} elapsedMs={replayStopwatch.Elapsed.TotalMilliseconds:F1} "
                    + $"replayedAll={replayComplete} queuesIdle={replayQueuesIdle}; "
                    + "the complete native event journal did not drain.");
                return;
            }

            double replayEventsPerSecond = EventsPerSecond(replayedEvents, replayStopwatch.Elapsed);
            Log.Write($"[NativeReplayProbe] M3_REPLAY_COMPLETE role={role} "
                + $"expectedEvents={expectedEvents} actualEvents={replayedEvents} "
                + $"replayedEvents={replayedEvents} elapsedMs={replayStopwatch.Elapsed.TotalMilliseconds:F1} "
                + $"eventsPerSecond={replayEventsPerSecond:F2}");
            Log.Write($"[NativeReplayProbe] REPLAY_COMPLETE role={role} "
                + $"eventCount={replayedEvents} expectedEvents={expectedEvents} replayedAll=true "
                + "queuesIdle=true");

            bool checkpointGenerated = await GenerateFinalCheckpointAsync(isHost);
            if (!checkpointGenerated)
            {
                Log.Write($"[NativeReplayProbe] FAIL final checkpoint role={role}; "
                    + "normal ChecksumTracker did not produce the required validation evidence.");
                return;
            }

            bool checkpointCompared = !isHost || await WaitForConditionAsync(
                () => _expectedComparisonObserved,
                LoadTimeout);
            if (isHost && !checkpointCompared)
            {
                Log.Write($"[NativeReplayProbe] FAIL checkpoint comparison role={role} "
                    + $"expectedId={_expectedComparisonId}; host observer saw no matching CompareChecksums call.");
                return;
            }

            _expectedComparisonArmed = false;
            Log.Write($"[NativeReplayProbe] SUCCESS role={role} nativeJournalCaptured=true "
                + "officialLoadedMultiplayerReentry=true entireEventPlayback=true "
                + "customSnapshot=false synchronizerReset=false normalChecksumTracker=true "
                + $"checkpointCompared={checkpointCompared} expectedEvents={expectedEvents} "
                + $"capturedEvents={replay.events.Count} replayedEvents={replayedEvents} historyLimit=none");
        }
        catch (Exception ex)
        {
            _expectedComparisonArmed = false;
            Log.Write($"[NativeReplayProbe] FAIL role={role} exception={ex}");
        }
    }

    private static bool TryGetSeedEventCount(out int seedEvents, out string error)
    {
        seedEvents = 1;
        error = string.Empty;
        if (!CommandLineHelper.TryGetValue(SeedEventsArg, out string? rawValue))
        {
            return true;
        }

        if (string.IsNullOrEmpty(rawValue)
            || !int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out seedEvents)
            || seedEvents <= 0)
        {
            seedEvents = 0;
            error = $"--{SeedEventsArg} must be a strict positive decimal integer "
                + $"(received '{rawValue ?? "<missing>"}')";
            return false;
        }

        return true;
    }

    private sealed record PayloadRoundTripResult(
        bool Success,
        string Detail,
        int SerializedPayloadBytes,
        uint TargetChecksumId,
        int EventCount);

    private sealed class PayloadRoundTripState
    {
        internal bool PayloadReceived { get; set; }
        internal bool ClientValidationSucceeded { get; set; }
        internal string ClientValidationDetail { get; set; } = "not-received";
        internal NativeReplayPayloadProbeAckMessage? Ack { get; set; }
        internal ulong AckSenderId { get; set; }
    }

    private static async Task<PayloadRoundTripResult> RunPayloadRoundTripAsync(
        bool isHost,
        uint targetChecksumId,
        int eventCount)
    {
        INetGameService? service = RunManager.Instance?.NetService;
        if (service == null || !service.IsConnected)
        {
            return new PayloadRoundTripResult(
                false, "live ENet service unavailable", 0, targetChecksumId, eventCount);
        }

        if ((isHost && service.Type != NetGameType.Host)
            || (!isHost && service.Type != NetGameType.Client))
        {
            return new PayloadRoundTripResult(
                false, $"role/service mismatch role={(isHost ? "host" : "client")} service={service.Type}",
                0, targetChecksumId, eventCount);
        }

        ulong peerNetId;
        if (isHost)
        {
            var remoteIds = RunManager.Instance.DebugOnlyGetState()?.Players
                .Select(player => player.NetId)
                .Where(id => id != service.NetId)
                .Distinct()
                .ToList() ?? new List<ulong>();
            if (remoteIds.Count != 1)
            {
                return new PayloadRoundTripResult(
                    false, $"expected exactly one remote peer, found {remoteIds.Count}",
                    0, targetChecksumId, eventCount);
            }

            peerNetId = remoteIds[0];
        }
        else if (service is NetClientGameService clientService && clientService.IsConnected)
        {
            peerNetId = clientService.HostNetId;
        }
        else
        {
            return new PayloadRoundTripResult(
                false, "client service did not expose a connected host identity", 0,
                targetChecksumId, eventCount);
        }

        var state = new PayloadRoundTripState();
        bool handlersRegistered = false;
        MessageHandlerDelegate<NativeReplayPayloadProbeAckMessage>? ackHandler = null;
        MessageHandlerDelegate<NativeReplayPayloadProbeMessage>? payloadHandler = null;
        try
        {
            if (isHost)
            {
                ackHandler = (message, senderId) => OnPayloadAck(state, message, senderId);
                service.RegisterMessageHandler(ackHandler);
            }
            else
            {
                payloadHandler = (message, senderId) => OnPayloadReceived(
                    service, state, message, senderId, peerNetId);
                service.RegisterMessageHandler(payloadHandler);
            }

            handlersRegistered = true;

            if (isHost)
            {
                string planError = string.Empty;
                string canonicalError = string.Empty;
                if (!NativeReplayJournal.TryCreateRestorePlan(
                        targetChecksumId,
                        eventCount,
                        out NativeReplayRestorePlan canonicalPlan,
                        out planError)
                    || !NativeReplayJournal.TryCreateCanonicalPrefix(
                        canonicalPlan,
                        out CombatReplay canonicalReplay,
                        out canonicalError))
                {
                    return new PayloadRoundTripResult(
                        false, $"NativeReplayJournal canonical-prefix validation failed: "
                        + $"{planError ?? canonicalError}",
                        0, targetChecksumId, eventCount);
                }

                var message = new NativeReplayPayloadProbeMessage
                {
                    targetChecksumId = targetChecksumId,
                    eventCount = eventCount,
                    canonicalReplay = canonicalReplay
                };
                if (!TryMeasureSerializedPayload(message, out int serializedPayloadBytes, out string sizeError))
                {
                    return new PayloadRoundTripResult(
                        false, $"PacketWriter payload measurement failed: {sizeError}",
                        0, targetChecksumId, eventCount);
                }

                Log.Write($"[NativeReplayProbe] PAYLOAD_SEND role=host sender={service.NetId} "
                    + $"receiver={peerNetId} targetChecksum={targetChecksumId} eventCount={eventCount} "
                    + $"serializedPayloadBytes={serializedPayloadBytes} "
                    + "source=NativeReplayJournal.TryCreateRestorePlan");
                service.SendMessage(message, peerNetId);

                bool acked = await WaitForConditionAsync(
                    () => state.Ack.HasValue,
                    PayloadRoundTripTimeout);
                if (!acked || !state.Ack.HasValue)
                {
                    return new PayloadRoundTripResult(
                        false, $"ack timeout after {PayloadRoundTripTimeout.TotalSeconds:0}s",
                        serializedPayloadBytes, targetChecksumId, eventCount);
                }

                NativeReplayPayloadProbeAckMessage ack = state.Ack.Value;
                bool senderMatches = state.AckSenderId == peerNetId;
                bool responderMatches = ack.responderNetId == peerNetId;
                bool payloadMatches = ack.targetChecksumId == targetChecksumId
                                      && ack.eventCount == eventCount;
                string detail = $"ackSender={state.AckSenderId} responder={ack.responderNetId} "
                    + $"accepted={ack.accepted} senderMatches={senderMatches} "
                    + $"responderMatches={responderMatches} payloadMatches={payloadMatches}";
                return new PayloadRoundTripResult(
                    senderMatches && responderMatches && payloadMatches && ack.accepted,
                    detail,
                    serializedPayloadBytes,
                    targetChecksumId,
                    eventCount);
            }

            bool received = await WaitForConditionAsync(
                () => state.PayloadReceived,
                PayloadRoundTripTimeout);
            return new PayloadRoundTripResult(
                received && state.ClientValidationSucceeded,
                $"sender={peerNetId} validation={state.ClientValidationSucceeded} detail={state.ClientValidationDetail}",
                0,
                targetChecksumId,
                eventCount);
        }
        catch (Exception ex)
        {
            return new PayloadRoundTripResult(
                false, $"exception={ex.Message}", 0, targetChecksumId, eventCount);
        }
        finally
        {
            if (handlersRegistered)
            {
                try
                {
                    if (isHost)
                    {
                        if (ackHandler != null)
                        {
                            service.UnregisterMessageHandler(ackHandler);
                        }
                    }
                    else
                    {
                        if (payloadHandler != null)
                        {
                            service.UnregisterMessageHandler(payloadHandler);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[NativeReplayProbe] WARN payload handler cleanup failed: {ex.Message}");
                }
            }
        }
    }

    private static void OnPayloadAck(
        PayloadRoundTripState state,
        NativeReplayPayloadProbeAckMessage message,
        ulong senderId)
    {
        if (state.Ack.HasValue)
        {
            return;
        }

        state.AckSenderId = senderId;
        state.Ack = message;
    }

    private static void OnPayloadReceived(
        INetGameService service,
        PayloadRoundTripState state,
        NativeReplayPayloadProbeMessage message,
        ulong senderId,
        ulong expectedHostNetId)
    {
        if (state.PayloadReceived)
        {
            return;
        }

        if (senderId != expectedHostNetId)
        {
            Log.Write($"[NativeReplayProbe] PAYLOAD_REJECT sender={senderId} "
                + $"expectedHost={expectedHostNetId} reason=transport-sender-mismatch");
            return;
        }

        bool accepted = false;
        string validationDetail;
        try
        {
            accepted = NativeReplayJournal.TryCreateRestorePlan(
                message.canonicalReplay,
                message.targetChecksumId,
                out _,
                out validationDetail);
            bool eventCountMatches = message.canonicalReplay.events.Count == message.eventCount;
            if (!eventCountMatches)
            {
                validationDetail = $"native event count {message.canonicalReplay.events.Count} "
                    + $"did not match advertised count {message.eventCount}";
            }
            accepted &= eventCountMatches;
        }
        catch (Exception ex)
        {
            validationDetail = $"exception={ex.Message}";
        }

        state.ClientValidationSucceeded = accepted;
        state.ClientValidationDetail = validationDetail;
        state.PayloadReceived = true;
        service.SendMessage(new NativeReplayPayloadProbeAckMessage
        {
            targetChecksumId = message.targetChecksumId,
            eventCount = message.eventCount,
            responderNetId = service.NetId,
            accepted = accepted
        });
        Log.Write($"[NativeReplayProbe] PAYLOAD_RECEIVED role=client sender={senderId} "
            + $"targetChecksum={message.targetChecksumId} eventCount={message.eventCount} "
            + $"validated={accepted} detail={validationDetail}");
    }

    private static bool TryMeasureSerializedPayload(
        NativeReplayPayloadProbeMessage message,
        out int byteCount,
        out string error)
    {
        try
        {
            var writer = new PacketWriter { WarnOnGrow = false };
            message.Serialize(writer);
            byteCount = writer.BytePosition;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            byteCount = 0;
            error = ex.Message;
            return false;
        }
    }

    private static async Task<bool> TrySeedAdditionalConsoleEventAsync(
        CombatReplay replay,
        ChecksumTracker tracker,
        ulong localNetId,
        int ordinal)
    {
        int eventBaseline = replay.events.Count;
        uint checksumBaseline = tracker.NextId;
        RunState? liveRun = RunManager.Instance.DebugOnlyGetState();
        Player? hostPlayer = liveRun?.GetPlayer(localNetId);
        string phase = hostPlayer?.PlayerCombatState?.Phase.ToString() ?? "null";
        bool combatActive = CombatManager.Instance?.IsInProgress == true;
        bool canUndoRedo = UndoSyncMod.CanUndoRedo();
        if (hostPlayer == null
            || !combatActive
            || hostPlayer.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || !canUndoRedo)
        {
            Log.Write($"[NativeReplayProbe] M3_CAPTURE_SEED_FAIL ordinal={ordinal} "
                + $"eventsBaseline={eventBaseline} checksumBaseline={checksumBaseline} "
                + $"events={replay.events.Count} checksumNextId={tracker.NextId} "
                + $"phase={phase} combatActive={combatActive} canUndoRedo={canUndoRedo} "
                + $"blockers={UndoSyncMod.DescribeUndoRedoBlockers()}; host player was not ready");
            return false;
        }

        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(hostPlayer, "energy 0", inCombat: true));
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayProbe] M3_CAPTURE_SEED_FAIL ordinal={ordinal} "
                + $"eventsBaseline={eventBaseline} checksumBaseline={checksumBaseline} "
                + $"events={replay.events.Count} checksumNextId={tracker.NextId} "
                + $"phase={hostPlayer.PlayerCombatState?.Phase.ToString() ?? "null"} "
                + $"blockers={UndoSyncMod.DescribeUndoRedoBlockers()} exception={ex.Message}");
            return false;
        }

        // This is deliberately condition-based. The normal 200ms cadence is reserved for
        // lobby/load waits; each native seed action gets the dedicated 20ms settle cadence above.
        bool settled = await WaitForConditionAsync(
            () => replay.events.Count >= eventBaseline + 1
                  && tracker.NextId > checksumBaseline
                  && RunManager.Instance.ActionQueueSet is { } queues
                  && UndoSyncMod.IsActionQueueIdle(queues, out _),
            LoadTimeout,
            SeedEventPollInterval);
        if (!settled)
        {
            Log.Write($"[NativeReplayProbe] M3_CAPTURE_SEED_FAIL ordinal={ordinal} "
                + $"eventsBaseline={eventBaseline} checksumBaseline={checksumBaseline} "
                + $"events={replay.events.Count} checksumNextId={tracker.NextId} "
                + $"phase={hostPlayer.PlayerCombatState?.Phase.ToString() ?? "null"} "
                + $"blockers={UndoSyncMod.DescribeUndoRedoBlockers()} settlePollMs="
                + $"{SeedEventPollInterval.TotalMilliseconds:F0}");
            return false;
        }

        return true;
    }

    private static double EventsPerSecond(int eventCount, TimeSpan elapsed)
    {
        return eventCount / Math.Max(elapsed.TotalSeconds, 0.000001d);
    }

    private static bool IsOfficialPostLoadReady(NGame game)
    {
        RunManager? runManager = RunManager.Instance;
        return runManager is { IsInProgress: true }
               && runManager.NetService.IsConnected
               && runManager.DebugOnlyGetState() != null
               && game.CurrentRunNode != null
               && CombatManager.Instance?.IsInProgress == true
               && !runManager.ActionExecutor.IsPaused;
    }

    private static string DescribePostLoadReadiness(NGame game)
    {
        RunManager? runManager = RunManager.Instance;
        bool activeRun = runManager?.IsInProgress == true;
        bool connected = runManager?.NetService.IsConnected == true;
        bool currentState = runManager?.DebugOnlyGetState() != null;
        bool runNode = game.CurrentRunNode != null;
        bool combatManager = CombatManager.Instance != null;
        bool combatActive = CombatManager.Instance?.IsInProgress == true;
        bool actionExecutorPaused = runManager?.ActionExecutor.IsPaused == true;
        var missing = new List<string>();
        if (!activeRun) missing.Add("activeRun");
        if (!connected) missing.Add("connected");
        if (!currentState) missing.Add("currentState");
        if (!runNode) missing.Add("runNode");
        if (!combatManager) missing.Add("combatManager");
        if (!combatActive) missing.Add("combatActive");
        if (actionExecutorPaused) missing.Add("actionExecutorPaused");
        return $"activeRun={activeRun} connected={connected} currentState={currentState} "
            + $"runNode={runNode} combatManager={combatManager} combatActive={combatActive} "
            + $"actionExecutorPaused={actionExecutorPaused} missing=[{string.Join(",", missing)}]";
    }

    private static async Task<(bool success, string detail)> TrySeedHostCardAsync(CombatReplay replay)
    {
        ulong localNetId = RunManager.Instance.NetService.NetId;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + LoadTimeout;
        Player? hostPlayer = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            RunState? liveRun = RunManager.Instance.DebugOnlyGetState();
            hostPlayer = liveRun?.GetPlayer(localNetId);
            if (CombatManager.Instance.IsInProgress
                && hostPlayer?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && UndoSyncMod.CanUndoRedo())
            {
                CardPile? readyHand = hostPlayer == null ? null : PileType.Hand.GetPile(hostPlayer);
                Log.Write($"[NativeReplayProbe] CAPTURE_ACTION_READY role=host "
                    + $"phase={hostPlayer.PlayerCombatState?.Phase.ToString() ?? "null"} "
                    + $"handCount={readyHand?.Cards.Count ?? -1} "
                    + $"blockers={UndoSyncMod.DescribeUndoRedoBlockers()}");
                break;
            }

            await Task.Delay(PollInterval);
        }

        RunState? timeoutRun = RunManager.Instance.DebugOnlyGetState();
        hostPlayer ??= timeoutRun?.GetPlayer(localNetId);
        CardPile? hand = hostPlayer == null ? null : PileType.Hand.GetPile(hostPlayer);
        if (hostPlayer == null
            || !CombatManager.Instance.IsInProgress
            || hostPlayer.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || !UndoSyncMod.CanUndoRedo())
        {
            string phase = hostPlayer?.PlayerCombatState?.Phase.ToString() ?? "null";
            int handCount = hand?.Cards.Count ?? -1;
            string blockers = UndoSyncMod.DescribeUndoRedoBlockers();
            string detail = $"seed readiness timeout phase={phase} handCount={handCount} blockers={blockers}";
            Log.Write($"[NativeReplayProbe] CAPTURE_ACTION_TIMEOUT role=host {detail}");
            return (false, detail);
        }

        if (hand == null)
        {
            return (false, "host Player has no hand pile after CAPTURE_ACTION_READY");
        }

        foreach (CardModel card in hand.Cards)
        {
            if (!card.CanPlay(out _, out _))
            {
                continue;
            }

            Creature? target = card.TargetType == TargetType.AnyEnemy
                ? card.CombatState?.HittableEnemies.FirstOrDefault()
                : null;
            try
            {
                if (card.TryManualPlay(target))
                {
                    string detail = $"card={card.Id.Entry} target={(target == null ? "none" : "valid")} "
                        + $"eventsBefore={replay.events.Count}";
                    return (true, detail);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayProbe] WARN host card candidate rejected card={card.Id.Entry} "
                    + $"exception={ex.Message}");
            }
        }

        return (false, "no playable host hand card with a valid current target");
    }

    private static async Task<bool> ReplayAllEventsAsync(CombatReplay replay)
    {
        CombatReplayWriter writer = RunManager.Instance.CombatReplayWriter;
        bool priorWriterEnabled = writer.IsEnabled;
        writer.IsEnabled = false;
        try
        {
            foreach (CombatReplayEvent replayEvent in replay.events)
            {
                switch (replayEvent.eventType)
                {
                    case CombatReplayEventType.GameAction:
                        while (CombatManager.Instance.EndingPlayerTurnPhaseOne
                               || CombatManager.Instance.EndingPlayerTurnPhaseTwo)
                        {
                            await NGame.Instance.AwaitProcessFrame();
                        }

                        RunState replayRun = RunManager.Instance.DebugOnlyGetState()
                            ?? throw new InvalidOperationException("RunState disappeared during native replay.");
                        Player replayPlayer = replayRun.GetPlayer(replayEvent.playerId!.Value)
                            ?? throw new InvalidOperationException(
                                $"Replay player {replayEvent.playerId.Value} was not present in current RunState.");
                        GameAction action = replayEvent.action?.ToGameAction(replayPlayer)
                            ?? throw new InvalidOperationException("GameAction replay event had no native action.");
                        if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                        {
                            while (CombatManager.Instance.DebugOnlyGetState().CurrentSide == CombatSide.Enemy)
                            {
                                await NGame.Instance.AwaitProcessFrame();
                            }
                        }

                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                        if (action is EndPlayerTurnAction || action is ReadyToBeginEnemyTurnAction)
                        {
                            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                        }

                        break;

                    case CombatReplayEventType.HookAction:
                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                            RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                                replayEvent.hookId!.Value,
                                replayEvent.playerId!.Value,
                                replayEvent.gameActionType!.Value));
                        break;

                    case CombatReplayEventType.ResumeAction:
                        RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(
                            replayEvent.actionId!.Value);
                        break;

                    case CombatReplayEventType.PlayerChoice:
                        RunState choiceRun = RunManager.Instance.DebugOnlyGetState()
                            ?? throw new InvalidOperationException("RunState disappeared during replay choice.");
                        Player choicePlayer = choiceRun.GetPlayer(replayEvent.playerId!.Value)
                            ?? throw new InvalidOperationException(
                                $"Replay choice player {replayEvent.playerId.Value} was not present in current RunState.");
                        RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(
                            choicePlayer,
                            replayEvent.choiceId!.Value,
                            replayEvent.playerChoiceResult!.Value);
                        break;

                    default:
                        throw new InvalidEnumArgumentException(
                            "eventType", (int)replayEvent.eventType, typeof(CombatReplayEventType));
                }
            }

            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            return true;
        }
        finally
        {
            writer.IsEnabled = priorWriterEnabled;
        }
    }

    private static async Task<bool> GenerateFinalCheckpointAsync(bool isHost)
    {
        ChecksumTracker tracker = RunManager.Instance.ChecksumTracker;
        if (!tracker.IsEnabled || !RunManager.Instance.NetService.IsConnected)
        {
            Log.Write($"[NativeReplayProbe] FAIL checkpoint precondition role={(isHost ? "host" : "client")} "
                + $"trackerEnabled={tracker.IsEnabled} connected={RunManager.Instance.NetService.IsConnected}");
            return false;
        }

        uint expectedId = tracker.NextId;
        if (isHost)
        {
            _expectedComparisonObserved = false;
            _expectedComparisonId = expectedId;
            _expectedComparisonArmed = true;
        }

        NetChecksumData generated = tracker.GenerateChecksum("native replay probe final checkpoint", null);
        Log.Write($"[NativeReplayProbe] CHECKPOINT_GENERATED role={(isHost ? "host" : "client")} "
            + $"id={generated.id} expectedId={expectedId} "
            + $"sent={RunManager.Instance.NetService.Type == NetGameType.Client} "
            + $"connected={RunManager.Instance.NetService.IsConnected}");
        await Task.Yield();
        return generated.id == expectedId && RunManager.Instance.NetService.IsConnected;
    }

    private static void InstallChecksumComparisonObserver()
    {
        if (_compareObserverInstalled)
        {
            return;
        }

        if (MCompareChecksums == null || FTrackedChecksumData == null || FChecksumId == null)
        {
            Log.Write("[NativeReplayProbe] FAIL verified reflection surface "
                + $"compareChecksums={MCompareChecksums != null} trackedData={FTrackedChecksumData != null} "
                + $"checksumId={FChecksumId != null}; final comparison cannot be observed.");
            return;
        }

        try
        {
            var harmony = new Harmony("undosync.native-replay-probe");
            var postfix = new HarmonyMethod(
                AccessTools.Method(typeof(NativeReplayProbe), nameof(OnCompareChecksumsObserved)));
            harmony.Patch(MCompareChecksums, postfix: postfix);
            _compareObserverInstalled = true;
            Log.Write("[NativeReplayProbe] Installed passive ChecksumTracker.CompareChecksums observer.");
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayProbe] FAIL checksum observer install: {ex.Message}");
        }
    }

    private static void OnCompareChecksumsObserved(object __0)
    {
        try
        {
            if (!_expectedComparisonArmed || __0 == null)
            {
                return;
            }

            object? data = FTrackedChecksumData!.GetValue(__0);
            object? id = data == null ? null : FChecksumId!.GetValue(data);
            if (id is uint comparedId && comparedId == _expectedComparisonId)
            {
                _expectedComparisonObserved = true;
                _expectedComparisonArmed = false;
                Log.Write($"[NativeReplayProbe] CHECKPOINT_COMPARED role=host id={comparedId} observer=passive");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayProbe] WARN checkpoint observer failed: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        TimeSpan cadence = pollInterval ?? PollInterval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[NativeReplayProbe] condition poll exception={ex.Message}");
                return false;
            }

            await Task.Delay(cadence);
        }

        return false;
    }

    private static void DisconnectJoinServiceSafely(JoinFlow joinFlow)
    {
        if (!joinFlow.NetService.IsConnected)
        {
            return;
        }

        try
        {
            joinFlow.NetService.Disconnect(NetError.InternalError, now: true);
            Log.Write("[NativeReplayProbe] CLIENT_JOIN_SERVICE_DISCONNECTED reason=InternalError");
        }
        catch (Exception ex)
        {
            Log.Write($"[NativeReplayProbe] WARN client join service disconnect failed: {ex.Message}");
        }
    }
}
