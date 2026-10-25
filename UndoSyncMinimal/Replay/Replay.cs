using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSyncMinimal;

internal static class Replay
{
    private static readonly Dictionary<(ulong, uint), TaskCompletionSource<NetPlayerChoiceResult>> Choices = new();
    private static readonly List<GameAction> Actions = new();
    private static INetGameService? _replacement;
    private static bool _injecting;
    internal static bool IsInjecting => _injecting;
    internal static bool OwnsCleanup { get; private set; }
    private static int _choiceWaits;
    private static int _choiceInjections;

    internal static async Task<uint> ExecuteAsync(CombatReplay replay, CancellationToken token)
    {
        var manager = RunManager.Instance;
        var old = manager.NetService;
        var role = old.Type;
        var platform = old.Platform;
        ulong local = old.NetId;
        ulong host = old is NetClientGameService client ? client.HostNetId : local;
        bool shouldSave = manager.ShouldSave;
        ulong[] roster = replay.serializableRun.Players.Select(p => p.NetId).ToArray();
        Choices.Clear(); Actions.Clear(); _choiceWaits = _choiceInjections = 0;
        foreach (var entry in replay.events.Where(e => e.eventType == CombatReplayEventType.PlayerChoice))
            Choices.Add((entry.playerId!.Value, entry.choiceId!.Value), new());
        Hooks.Log($"RESTORE_BEGIN role={role} events={replay.events.Count}");
        await NGame.Instance!.Transition.FadeOut(0.05f);
        // Exit native UI against its original run owners before disposing them.
        NGame.Instance.RootSceneContainer.SetCurrentScene(new Control());
        await Frame(token);
        Network.Detach();
        manager.CombatReplayWriter.IsEnabled = false;
        OwnsCleanup = true;
        try { manager.CleanUp(graceful: true); }
        finally { OwnsCleanup = false; }
        token.ThrowIfCancellationRequested();

        var state = RunState.FromSerializable(replay.serializableRun);
        if (role == NetGameType.Singleplayer)
        {
            await manager.SetUpSavedSingleplayer(state, replay.serializableRun);
            _replacement = manager.NetService;
            Network.Attach(_replacement);
        }
        else
        {
            LoadRunLobby? lobby = null;
            var listener = new LoadListener();
            try
            {
                if (role == NetGameType.Host)
                {
                    var service = new NetHostGameService(PeerVersionInfo.LocalDefault());
                    _replacement = service;
                    NetErrorInfo? error = platform == PlatformType.Steam
                        ? await service.StartSteamHost(4)
                        : service.StartENetHost(33771, 4);
                    if (error.HasValue) throw new InvalidOperationException($"Host start failed: {error}");
                    Network.Attach(service);
                    lobby = new LoadRunLobby(service, listener, replay.serializableRun);
                    lobby.AddLocalHostPlayer();
                    Hooks.Log("RESTORE_HOST_LISTENING");
                }
                else
                {
                    lobby = await JoinAsync(local, host, platform, listener, token);
                    state = RunState.FromSerializable(lobby.Run);
                }
                // Same native setup as NMultiplayerLoadGameScreen.AfterMultiplayerStarted.
                NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, lobby.PlayerIds);
                NGame.Instance.ReactionContainer.InitializeNetworking(lobby.NetService);
                await WaitUntilAsync(() => roster.ToHashSet().SetEquals(lobby.PlayerIds), token, lobby.NetService);
                token.ThrowIfCancellationRequested();
                lobby.SetReady(true);
                await WaitUntilAsync(() => listener.Begun, token, lobby.NetService);
                if (lobby.NetService.NetId != local) throw new InvalidOperationException("Player identity changed.");
                await manager.SetUpSavedMultiplayer(state, lobby);
            }
            finally { lobby?.CleanUp(disconnectSession: false); }
        }

        token.ThrowIfCancellationRequested();
        // Public setters and private setter verified in RunManager.InitializeShared.
        AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(manager, new object[] { false });
        manager.ChecksumTracker.IsEnabled = false;
        manager.CombatStateSynchronizer.IsDisabled = true;
        await PreloadManager.LoadRunAssets(state.Players.Select(p => p.Character));
        await PreloadManager.LoadActAssets(state.Act);
        token.ThrowIfCancellationRequested();
        manager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(state));
        await manager.GenerateMap();
        manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        Hooks.Log($"RESTORE_SEEDED action={replay.nextActionId} hook={replay.nextHookId}");
        token.ThrowIfCancellationRequested();
        await manager.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(replay.serializableRun.PreFinishedRoom, state));
        await WaitUntilAsync(() => !manager.ActionExecutor.IsPaused, token);

        for (int i = 0; i < replay.events.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfActionFailed();
            var entry = replay.events[i];
            Hooks.Log($"REPLAY_EVENT index={i} kind={entry.eventType}");
            switch (entry.eventType)
            {
                case CombatReplayEventType.GameAction:
                    await WaitUntilAsync(() => !CombatManager.Instance.EndingPlayerTurnPhaseOne
                        && !CombatManager.Instance.EndingPlayerTurnPhaseTwo, token);
                    var action = entry.action!.ToGameAction(state.GetPlayer(entry.playerId!.Value)
                        ?? throw new InvalidOperationException("Replay player missing."));
                    if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                        await WaitUntilAsync(() => CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player, token);
                    Inject(() => manager.ActionQueueSet.EnqueueWithoutSynchronizing(action));
                    Actions.Add(action);
                    if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction)
                        await WaitUntilAsync(() => !manager.ActionExecutor.IsRunning, token);
                    break;
                case CombatReplayEventType.HookAction:
                    var hook = manager.ActionQueueSynchronizer.GetHookActionForId(
                        entry.hookId!.Value, entry.playerId!.Value, entry.gameActionType!.Value);
                    Inject(() => manager.ActionQueueSet.EnqueueWithoutSynchronizing(hook));
                    Actions.Add(hook);
                    break;
                case CombatReplayEventType.ResumeAction:
                    Inject(() => manager.ActionQueueSet.ResumeActionWithoutSynchronizing(entry.actionId!.Value));
                    break;
                case CombatReplayEventType.PlayerChoice:
                    Choices[(entry.playerId!.Value, entry.choiceId!.Value)].SetResult(entry.playerChoiceResult!.Value);
                    _choiceInjections++;
                    break;
                default: throw new InvalidOperationException($"Unknown replay event {entry.eventType}");
            }
        }
        // Two idle frames ensure callbacks and the UI play queue have drained.
        await WaitUntilAsync(Hooks.IsReplayIdle, token);
        await Frame(token);
        await WaitUntilAsync(Hooks.IsReplayIdle, token);
        ThrowIfActionFailed();
        manager.CombatStateSynchronizer.IsDisabled = false;
        manager.CombatReplayWriter.IsEnabled = true;
        AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(manager, new object[] { shouldSave });
        manager.ChecksumTracker.IsEnabled = true;
        uint checksum = manager.ChecksumTracker.GenerateChecksum("UndoSyncMinimal replay complete", null).checksum;
        Hooks.Log($"REPLAY_DONE events={replay.events.Count} waits={_choiceWaits} choices={_choiceInjections} checksum={checksum}");
        return checksum;
    }

    private static async Task<LoadRunLobby> JoinAsync(ulong local, ulong host, PlatformType platform,
        LoadListener listener, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var service = new NetClientGameService(PeerVersionInfo.LocalDefault());
            _replacement = service;
            var flow = new JoinFlow(service);
            using var registration = token.Register(() => flow.CancelToken.Cancel());
            try
            {
                IClientConnectionInitializer initializer = platform == PlatformType.Steam
                    ? SteamClientConnectionInitializer.FromPlayer(host)
                    : new ENetClientConnectionInitializer(local, "127.0.0.1", 33771);
                var result = await flow.Begin(initializer, NGame.Instance!.GetTree());
                token.ThrowIfCancellationRequested();
                if (result.sessionState != RunSessionState.InLoadedLobby || !result.loadJoinResponse.HasValue)
                    throw new InvalidOperationException("Host did not open a saved-run lobby.");
                Network.Attach(service);
                return new LoadRunLobby(service, listener, result.loadJoinResponse.Value);
            }
            catch (Exception exception) when (!token.IsCancellationRequested && attempt < 3)
            {
                service.Disconnect(NetError.CancelledJoin, now: true);
                Hooks.Log($"RESTORE_JOIN_RETRY attempt={attempt} error={exception.Message}");
                await Task.Delay(500, token);
            }
            finally { flow.CancelToken.Dispose(); }
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token, INetGameService? pump = null)
    {
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            ThrowIfActionFailed();
            if (pump is not null)
            {
                if (!pump.IsConnected) throw new InvalidOperationException("Restore transport disconnected.");
                pump.Update();
            }
            await Frame(token);
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task Frame(CancellationToken token) => await NGame.Instance!.AwaitProcessFrame(token);
    private static void ThrowIfActionFailed()
    {
        foreach (var action in Actions)
            if (action.Exception is not null) throw new InvalidOperationException("Replayed action failed.", action.Exception);
    }
    private static void Inject(Action action)
    {
        _injecting = true;
        try { action(); } finally { _injecting = false; }
    }

    internal static void Clear()
    {
        foreach (var choice in Choices.Values) choice.TrySetCanceled();
        Choices.Clear(); Actions.Clear(); _replacement = null;
    }

    internal static void DisconnectReplacement()
    {
        _replacement?.Disconnect(NetError.Quit, now: true);
    }

    private sealed class LoadListener : ILoadRunLobbyListener
    {
        internal bool Begun;
        public void BeginRun() => Begun = true;
        public void PlayerConnected(LoadRunLobbyPlayer player) { }
        public void PlayerReadyChanged(ulong playerId) { }
        public Task<bool> ShouldAllowRunToBegin() => Task.FromResult(true);
        public void RemotePlayerDisconnected(ulong playerId) => Network.Fail($"Player {playerId} disconnected while loading.");
        public void LocalPlayerDisconnected(NetErrorInfo info) => Network.Fail($"Disconnected while loading: {info}");
    }

    internal static async Task<PlayerChoiceResult> RecordedChoiceAsync(Player player, uint choiceId)
    {
        _choiceWaits++;
        if (!Choices.TryGetValue((player.NetId, choiceId), out var choice))
            throw new InvalidOperationException($"Missing historical choice {player.NetId}/{choiceId}.");
        var result = await choice.Task;
        return PlayerChoiceResult.FromNetData(player, RunManager.Instance.DebugOnlyGetState()!, result);
    }
}
