using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace UndoSync;

internal sealed record NativeReplayRestoreResult(bool Success, string Detail);

/// <summary>
/// Rebuilds a multiplayer run through the game's loaded-lobby path and replays the
/// native CombatReplay event journal. This coordinator deliberately does not reset
/// any native synchronizer or checksum counters.
/// </summary>
internal static class NativeReplayRestorer
{
    private const ushort EnetPort = 33771;
    private const int MaxJoinAttempts = 3;
    private const float ReplayFadeDuration = 0.2f;
    private static readonly TimeSpan JoinRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MainMenuTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LobbyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HostServiceObservationWindow = TimeSpan.FromSeconds(10);
    private static int NativeReplayChoiceSuppressionCount;
    private static int NativeReplayReadyActionSuppressionCount;

    private static readonly FieldInfo? LoadRunLobbyField =
        AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby");

    private static readonly FieldInfo? DailyLobbyField =
        AccessTools.Field(typeof(NDailyRunLoadScreen), "_lobby");

    private static readonly FieldInfo? CustomLobbyField =
        AccessTools.Field(typeof(NCustomRunLoadScreen), "_lobby");

    private static readonly MethodInfo? PlayerActionsDisabledSetter =
        AccessTools.PropertySetter(typeof(CombatManager), "PlayerActionsDisabled");

    private static readonly MethodInfo? StandardEmbark =
        AccessTools.Method(typeof(NMultiplayerLoadGameScreen), "OnEmbarkPressed");

    private static readonly MethodInfo? DailyEmbark =
        AccessTools.Method(typeof(NDailyRunLoadScreen), "OnEmbarkPressed");

    private static readonly MethodInfo? CustomEmbark =
        AccessTools.Method(typeof(NCustomRunLoadScreen), "OnEmbarkPressed");

    internal static async Task<NativeReplayRestoreResult> RestoreMultiplayerAsync(
        NativeReplayRestorePlan plan)
    {
        if (plan is null)
        {
            return Failure("Restore plan was null.");
        }

        if (plan.InitialRun is null || plan.Replay is null || plan.Replay.events is null)
        {
            return Failure("Restore plan did not contain a native replay and initial run.");
        }

        if (plan.EventCount < 0 || plan.EventCount > plan.Replay.events.Count)
        {
            return Failure($"Replay event prefix {plan.EventCount} was outside 0..{plan.Replay.events.Count}.");
        }

        RunManager? runManager = RunManager.Instance;
        if (runManager?.NetService is not { } oldNetService)
        {
            return Failure("The current multiplayer network service was unavailable.");
        }

        NetGameType oldRole = oldNetService.Type;
        PlatformType oldPlatform = oldNetService.Platform;
        ulong localNetId;
        ulong? hostNetId = null;
        try
        {
            localNetId = oldNetService.NetId;
            if (oldRole == NetGameType.Client)
            {
                if (oldNetService is not NetClientGameService clientService)
                {
                    return Failure("The current client service was not NetClientGameService.");
                }

                hostNetId = clientService.HostNetId;
            }
        }
        catch (Exception ex)
        {
            return Failure($"Could not capture the current network identity: {Describe(ex)}");
        }

        if (oldRole != NetGameType.Host && oldRole != NetGameType.Client)
        {
            return Failure($"Native multiplayer restore requires a host or client, not {oldRole}.");
        }

        if (oldPlatform != PlatformType.None && oldPlatform != PlatformType.Steam)
        {
            return Failure($"Native multiplayer restore does not support platform {oldPlatform}.");
        }

        LogRestore(
            $"captured old role={oldRole} platform={oldPlatform} localNetId={localNetId} "
            + $"hostNetId={hostNetId?.ToString() ?? "none"}");

        try
        {
            await NGame.Instance.ReturnToMainMenu();
            LogRestore("after ReturnToMainMenu");
            if (!await WaitForConditionAsync(
                    () => NGame.Instance.MainMenu != null,
                    MainMenuTimeout))
            {
                return Failure("The official ReturnToMainMenu path did not reach the main menu.");
            }

            JoinFlow? joinFlow = null;
            if (oldRole == NetGameType.Host)
            {
                NativeReplayRestoreResult? hostStartFailure =
                    await StartHostForRestoreAsync(oldPlatform, plan.InitialRun);
                if (hostStartFailure != null)
                {
                    return hostStartFailure;
                }
            }
            else
            {
                if (!hostNetId.HasValue)
                {
                    return Failure("The client had no captured HostNetId.");
                }

                JoinResult joinResult;
                (joinFlow, joinResult) = await JoinLoadedLobbyAsync(
                    oldPlatform,
                    localNetId,
                    hostNetId.Value);
                if (joinResult.sessionState != RunSessionState.InLoadedLobby
                    || !joinResult.loadJoinResponse.HasValue
                    || joinResult.gameMode != plan.InitialRun.GameMode)
                {
                    DisconnectJoinService(joinFlow);
                    return Failure(
                        $"Loaded-lobby join returned state={joinResult.sessionState?.ToString() ?? "<null>"}, "
                        + $"mode={joinResult.gameMode}, expected={plan.InitialRun.GameMode}.");
                }

                InitializeClientLoadScreen(
                    plan.InitialRun.GameMode,
                    joinFlow,
                    joinResult.loadJoinResponse.Value);
            }

            object loadScreen = await WaitForLoadScreenAsync(plan.InitialRun.GameMode);
            LoadRunLobby lobby = await WaitForLoadedLobbyAsync(
                loadScreen,
                plan.InitialRun.Players.Count);
            InvokeEmbarkPressed(plan.InitialRun.GameMode, loadScreen);

            if (!await WaitForConditionAsync(
                    () => IsOfficialPostLoadReady(NGame.Instance),
                    LoadTimeout))
            {
                return Failure("The official multiplayer load completed without reaching combat readiness.");
            }

            if (!await WaitForConditionAsync(
                    () => RunManager.Instance?.ActionQueueSet is { } queues
                          && UndoSyncMod.IsActionQueueIdle(queues, out _),
                    LoadTimeout))
            {
                return Failure("The official load reached combat but the literal action queues did not become idle.");
            }

            // Match the official replay setup after the loaded run is ready. These
            // counters must follow the journal before ResumeAction/PlayerChoice
            // events are dispatched against the recreated run. Do not load replay
            // checksums here: LoadReplayChecksums installs the replay checksum
            // validator, which is correct for NetReplayGameService but would report
            // false replay divergence during a live Host/Client rejoin.
            RunManager.Instance.ActionQueueSet.FastForwardNextActionId(plan.Replay.nextActionId);
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(plan.Replay.nextHookId);
            RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds(plan.Replay.choiceIds);
            RunManager.Instance.RewardsSetSynchronizer.FastForwardRewardIds(plan.Replay.rewardIds);

            // Keep the native transition black until NativeReplayCompletion observes the
            // final-checksum quorum and performs the matching FadeIn.
            await NGame.Instance.Transition.FadeOut(ReplayFadeDuration);

            if (!SetPlayerActionsDisabled())
            {
                return Failure("Could not set the new CombatManager.PlayerActionsDisabled flag.");
            }

            {
                using ReplayChoiceSuppressionScope suppressionScope =
                    new ReplayChoiceSuppressionScope();
                using ReplayReadyActionSuppressionScope readyActionSuppressionScope =
                    new ReplayReadyActionSuppressionScope();
                bool replayed = await ReplayEventsAsync(plan.Replay, plan.EventCount);
                if (!replayed)
                {
                    return Failure($"Native replay failed while processing the first {plan.EventCount} events.");
                }

                // ReplayEventsAsync can finish dispatching before the executor has
                // started the newly enqueued action. Yield once so
                // FinishedExecutingActions observes that work, then keep both
                // suppression scopes alive until the executor and literal queues
                // have fully drained. In particular, a replayed PlayerChoice must
                // complete before normal input/UI is released.
                await NGame.Instance.AwaitProcessFrame();
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                if (!await WaitForConditionAsync(
                        () => RunManager.Instance?.ActionQueueSet is { } queues
                              && UndoSyncMod.IsActionQueueIdle(queues, out _)
                              && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null,
                        LoadTimeout))
                {
                    return Failure(
                        "Native replay completed its dispatch but the action executor and literal action queues did not drain.");
                }
            }

            if (!NativeReplayJournal.TryInstallPrefix(plan, out string installDetail))
            {
                return Failure($"Could not install the native replay prefix: {installDetail}");
            }

            RunManager.Instance.ChecksumTracker.GenerateChecksum(
                "UndoSync native replay restore",
                null);

            return new NativeReplayRestoreResult(
                true,
                $"Restored role={oldRole} platform={oldPlatform} localNetId={localNetId} "
                + $"hostNetId={hostNetId?.ToString() ?? "none"} mode={plan.InitialRun.GameMode} "
                + $"players={plan.InitialRun.Players.Count} events={plan.EventCount}; "
                + "native replay prefix installed, final checksum generated, player actions remain disabled.");
        }
        catch (Exception ex)
        {
            return Failure($"Native multiplayer restore failed: {Describe(ex)}");
        }
    }

    private static async Task<NativeReplayRestoreResult?> StartHostForRestoreAsync(
        PlatformType platform,
        SerializableRun run)
    {
        NetHostGameService netService = new NetHostGameService(PeerVersionInfo.LocalDefault());
        NetErrorInfo? netErrorInfo = platform == PlatformType.Steam
            ? await netService.StartSteamHost(4)
            : netService.StartENetHost(EnetPort, 4);
        LogRestore($"host transport selected platform={platform} service={netService.GetType().FullName}");
        if (netErrorInfo.HasValue)
        {
            return Failure(
                $"Native multiplayer host start failed on platform {platform}: "
                + netErrorInfo.Value.GetErrorString());
        }

        NSubmenuStack stack = NGame.Instance.MainMenu!.SubmenuStack;
        switch (run.GameMode)
        {
            case GameMode.Daily:
            {
                NDailyRunLoadScreen screen = stack.GetSubmenuType<NDailyRunLoadScreen>();
                screen.InitializeAsHost(netService, run);
                stack.Push(screen);
                break;
            }
            case GameMode.Custom:
            {
                NCustomRunLoadScreen screen = stack.GetSubmenuType<NCustomRunLoadScreen>();
                screen.InitializeAsHost(netService, run);
                stack.Push(screen);
                break;
            }
            default:
            {
                NMultiplayerLoadGameScreen screen = stack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                screen.InitializeAsHost(netService, run);
                stack.Push(screen);
                break;
            }
        }

        _ = ObserveCreatedHostServiceAsync(platform);
        return null;
    }

    private static async Task<(JoinFlow flow, JoinResult result)> JoinLoadedLobbyAsync(
        PlatformType platform,
        ulong localNetId,
        ulong hostNetId)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaxJoinAttempts; attempt++)
        {
            JoinFlow flow = new JoinFlow(new NetClientGameService(PeerVersionInfo.LocalDefault()));
            try
            {
                IClientConnectionInitializer initializer = platform == PlatformType.Steam
                    ? SteamClientConnectionInitializer.FromPlayer(hostNetId)
                    : new ENetClientConnectionInitializer(localNetId, "127.0.0.1", EnetPort);
                string target = platform == PlatformType.Steam
                    ? $"Steam host id={hostNetId}"
                    : $"ENet endpoint=127.0.0.1:{EnetPort} local id={localNetId}";
                LogRestore(
                    $"join attempt {attempt}/{MaxJoinAttempts} start initializer={initializer.GetType().FullName} "
                    + $"target={target}");
                JoinResult result = await flow.Begin(initializer, NGame.Instance.GetTree());
                LogRestore(
                    $"join attempt {attempt}/{MaxJoinAttempts} success state={result.sessionState} "
                    + $"mode={result.gameMode} loadJoinResponse={result.loadJoinResponse.HasValue}");
                return (flow, result);
            }
            catch (ClientConnectionFailedException ex)
            {
                lastException = ex;
                LogRestore(
                    $"join attempt {attempt}/{MaxJoinAttempts} ClientConnectionFailedException detail={Describe(ex)}");
                DisconnectJoinService(flow);
                if (attempt < MaxJoinAttempts)
                {
                    await Task.Delay(JoinRetryDelay);
                }
            }
            catch (Exception ex)
            {
                DisconnectJoinService(flow);
                throw new InvalidOperationException(
                    $"Join attempt {attempt}/{MaxJoinAttempts} failed: {Describe(ex)}", ex);
            }
        }

        throw new InvalidOperationException(
            $"Loaded-lobby join exhausted {MaxJoinAttempts} attempts: {Describe(lastException)}",
            lastException);
    }

    private static void InitializeClientLoadScreen(
        GameMode gameMode,
        JoinFlow joinFlow,
        ClientLoadJoinResponseMessage message)
    {
        switch (gameMode)
        {
            case GameMode.Daily:
            {
                NDailyRunLoadScreen screen =
                    NGame.Instance.MainMenu!.SubmenuStack.GetSubmenuType<NDailyRunLoadScreen>();
                screen.InitializeAsClient(joinFlow.NetService, message);
                NGame.Instance.MainMenu.SubmenuStack.Push(screen);
                return;
            }
            case GameMode.Custom:
            {
                NCustomRunLoadScreen screen =
                    NGame.Instance.MainMenu!.SubmenuStack.GetSubmenuType<NCustomRunLoadScreen>();
                screen.InitializeAsClient(joinFlow.NetService, message);
                NGame.Instance.MainMenu.SubmenuStack.Push(screen);
                return;
            }
            case GameMode.Standard:
            {
                NMultiplayerLoadGameScreen screen =
                    NGame.Instance.MainMenu!.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                screen.InitializeAsClient(joinFlow.NetService, message);
                NGame.Instance.MainMenu.SubmenuStack.Push(screen);
                return;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(gameMode), gameMode, "Invalid multiplayer game mode.");
        }
    }

    private static async Task<object> WaitForLoadScreenAsync(GameMode gameMode)
    {
        object? screen = null;
        bool ready = await WaitForConditionAsync(
            () => (screen = GetLoadScreen(gameMode)) != null && GetLobby(screen) != null,
            LobbyTimeout);
        if (!ready || screen == null)
        {
            throw new TimeoutException($"The {gameMode} native loaded-lobby screen did not initialize.");
        }

        return screen;
    }

    private static async Task<LoadRunLobby> WaitForLoadedLobbyAsync(object screen, int expectedPlayers)
    {
        if (expectedPlayers <= 0)
        {
            throw new InvalidOperationException("The restore run had no saved players.");
        }

        LoadRunLobby? lobby = null;
        bool ready = await WaitForConditionAsync(
            () => (lobby = GetLobby(screen)) != null
                  && lobby.PlayerCount >= expectedPlayers,
            LobbyTimeout);
        if (!ready || lobby == null)
        {
            int actualPlayers = GetLobby(screen)?.PlayerCount ?? 0;
            throw new TimeoutException(
                $"Loaded lobby did not reach saved-player quorum: {actualPlayers}/{expectedPlayers}.");
        }

        return lobby;
    }

    private static object? GetLoadScreen(GameMode gameMode)
    {
        if (NGame.Instance.MainMenu == null)
        {
            return null;
        }

        return gameMode switch
        {
            GameMode.Daily => NGame.Instance.MainMenu.SubmenuStack.GetSubmenuType<NDailyRunLoadScreen>(),
            GameMode.Custom => NGame.Instance.MainMenu.SubmenuStack.GetSubmenuType<NCustomRunLoadScreen>(),
            GameMode.Standard => NGame.Instance.MainMenu.SubmenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>(),
            _ => throw new ArgumentOutOfRangeException(nameof(gameMode), gameMode, "Invalid multiplayer game mode.")
        };
    }

    private static LoadRunLobby? GetLobby(object screen)
    {
        FieldInfo? field = screen switch
        {
            NMultiplayerLoadGameScreen => LoadRunLobbyField,
            NDailyRunLoadScreen => DailyLobbyField,
            NCustomRunLoadScreen => CustomLobbyField,
            _ => null
        };
        return field?.GetValue(screen) as LoadRunLobby;
    }

    private static void InvokeEmbarkPressed(GameMode gameMode, object screen)
    {
        MethodInfo? method = gameMode switch
        {
            GameMode.Daily => DailyEmbark,
            GameMode.Custom => CustomEmbark,
            GameMode.Standard => StandardEmbark,
            _ => null
        };
        if (method == null)
        {
            throw new MissingMethodException(screen.GetType().FullName, "OnEmbarkPressed");
        }

        method.Invoke(screen, new object?[] { null });
    }

    private static bool IsOfficialPostLoadReady(NGame game)
    {
        RunManager? runManager = RunManager.Instance;
        return runManager is { IsInProgress: true }
               && runManager.NetService.IsConnected
               && runManager.DebugOnlyGetState() != null
               && game.CurrentRunNode != null
               && CombatManager.Instance.IsInProgress
               && !runManager.ActionExecutor.IsPaused;
    }

    private static bool SetPlayerActionsDisabled()
    {
        if (PlayerActionsDisabledSetter == null || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        PlayerActionsDisabledSetter.Invoke(CombatManager.Instance, new object?[] { true });
        return CombatManager.Instance.PlayerActionsDisabled;
    }

    private static async Task<bool> ReplayEventsAsync(CombatReplay replay, int eventCount)
    {
        CombatReplayWriter writer = RunManager.Instance.CombatReplayWriter;
        bool priorWriterEnabled = writer.IsEnabled;
        bool diagnosticEnabled = CommandLineHelper.HasArg("undosync-mpfuzz-propose-after-player-choice");
        writer.IsEnabled = false;
        try
        {
            for (int index = 0; index < eventCount; index++)
            {
                CombatReplayEvent replayEvent = replay.events[index];
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

                        if (diagnosticEnabled)
                        {
                            Log.Write(
                                $"[NativeReplayRestorer] replay dispatch index={index} "
                                + $"eventType={replayEvent.eventType} actionType={action.GetType().Name} "
                                + $"ownerId={action.OwnerId}");
                        }

                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                        if (action is EndPlayerTurnAction || action is ReadyToBeginEnemyTurnAction)
                        {
                            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                        }

                        break;

                    case CombatReplayEventType.HookAction:
                        if (diagnosticEnabled)
                        {
                            Log.Write(
                                $"[NativeReplayRestorer] replay dispatch index={index} "
                                + $"eventType={replayEvent.eventType}");
                        }

                        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                            RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                                replayEvent.hookId!.Value,
                                replayEvent.playerId!.Value,
                                replayEvent.gameActionType!.Value));
                        break;

                    case CombatReplayEventType.ResumeAction:
                        if (diagnosticEnabled)
                        {
                            Log.Write(
                                $"[NativeReplayRestorer] replay dispatch index={index} "
                                + $"eventType={replayEvent.eventType} actionId={replayEvent.actionId!.Value}");
                        }

                        RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(
                            replayEvent.actionId!.Value);
                        break;

                    case CombatReplayEventType.PlayerChoice:
                        RunState choiceRun = RunManager.Instance.DebugOnlyGetState()
                            ?? throw new InvalidOperationException("RunState disappeared during replay choice.");
                        Player choicePlayer = choiceRun.GetPlayer(replayEvent.playerId!.Value)
                            ?? throw new InvalidOperationException(
                                $"Replay choice player {replayEvent.playerId.Value} was not present in current RunState.");
                        if (diagnosticEnabled)
                        {
                            Log.Write(
                                $"[NativeReplayRestorer] replay dispatch index={index} "
                                + $"eventType={replayEvent.eventType} playerId={replayEvent.playerId.Value} "
                                + $"choiceId={replayEvent.choiceId.Value}");
                        }

                        RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(
                            choicePlayer,
                            replayEvent.choiceId!.Value,
                            replayEvent.playerChoiceResult!.Value);
                        break;

                    default:
                        throw new InvalidEnumArgumentException(
                            "eventType",
                            (int)replayEvent.eventType,
                            typeof(CombatReplayEventType));
                }
            }

            if (diagnosticEnabled)
            {
                Log.Write($"[NativeReplayRestorer] replay event loop finished processedCount={eventCount}");
            }

            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            return true;
        }
        finally
        {
            writer.IsEnabled = priorWriterEnabled;
        }
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            await Task.Delay(PollInterval);
        }

        return false;
    }

    private static void DisconnectJoinService(JoinFlow? flow)
    {
        if (flow == null || !flow.NetService.IsConnected)
        {
            return;
        }

        try
        {
            flow.NetService.Disconnect(NetError.InternalError, now: true);
        }
        catch
        {
            // The join flow may already have disposed its transport after a failed attempt.
        }
    }

    private static async Task ObserveCreatedHostServiceAsync(PlatformType expectedPlatform)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < HostServiceObservationWindow)
        {
            try
            {
                RunManager? runManager = RunManager.Instance;
                if (runManager?.NetService is { } service)
                {
                    LogRestore(
                        $"host service observed type={service.GetType().FullName} platform={service.Platform} "
                        + $"netType={service.Type} isHostService={service is NetHostGameService} "
                        + $"expectedPlatform={expectedPlatform}");
                    if (service is NetHostGameService)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogRestore($"host service observation error detail={Describe(ex)}");
            }

            await Task.Delay(PollInterval);
        }

        LogRestore("host service observation bounded window expired without host service");
    }

    private static NativeReplayRestoreResult Failure(string detail)
    {
        return new NativeReplayRestoreResult(false, detail);
    }

    private static string Describe(Exception? exception)
    {
        if (exception == null)
        {
            return "unknown error";
        }

        Exception current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private static void LogRestore(string message) => Log.Write($"[NativeReplayRestore] {message}");

    private sealed class ReplayChoiceSuppressionScope : IDisposable
    {
        private bool _disposed;

        internal ReplayChoiceSuppressionScope()
        {
            Interlocked.Increment(ref NativeReplayChoiceSuppressionCount);
            LogRestore("native replay card-choice suppression entered");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            int remaining = Interlocked.Decrement(ref NativeReplayChoiceSuppressionCount);
            LogRestore(
                $"native replay card-choice suppression exited remaining={remaining}");
        }
    }

    private sealed class ReplayReadyActionSuppressionScope : IDisposable
    {
        private bool _disposed;

        internal ReplayReadyActionSuppressionScope()
        {
            Interlocked.Increment(ref NativeReplayReadyActionSuppressionCount);
            LogRestore("native replay ready-action suppression entered");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            int remaining = Interlocked.Decrement(ref NativeReplayReadyActionSuppressionCount);
            LogRestore(
                $"native replay ready-action suppression exited remaining={remaining}");
        }
    }

    [HarmonyPatch(typeof(CardSelectCmd), "ShouldSelectLocalCard", new[] { typeof(Player) })]
    private static class CardSelectCmdReplayChoicePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (Volatile.Read(ref NativeReplayChoiceSuppressionCount) == 0)
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(
        typeof(ActionQueueSynchronizer),
        nameof(ActionQueueSynchronizer.RequestEnqueue),
        new[] { typeof(GameAction) })]
    private static class ActionQueueSynchronizerReplayReadyActionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameAction action)
        {
            if (Volatile.Read(ref NativeReplayReadyActionSuppressionCount) == 0
                || action is not ReadyToBeginEnemyTurnAction)
            {
                return true;
            }

            LogRestore(
                $"native replay ready-action suppression suppressed action type={action.GetType().FullName}");
            return false;
        }
    }
}
