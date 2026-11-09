using HarmonyLib;
using Godot;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace UndoSync;

internal static class Replay
{
    private static readonly Dictionary<(ulong, uint), TaskCompletionSource<NetPlayerChoiceResult>> Choices = new();
    private static readonly List<GameAction> Actions = new();
    private static INetGameService? _transport;
    private static object? _nativeTransport;
    private static string? _lobbyId;
    private static bool _shouldSave;
    internal static bool NativeResumed { get; private set; }
    internal static bool ModelOnly { get; private set; }
    private static int _skippedPileVisuals, _skippedCardNodes, _skippedDamageNumbers, _skippedPreviews, _skippedTransformVisuals;
    internal static bool SuppressNative => Network.Executing && !NativeResumed;
    internal static int PreservedDisconnects { get; private set; }
    internal static int SuppressedChecksumCalls { get; private set; }
    internal static int DroppedChecksumMessages { get; private set; }
    internal static bool PreserveDisconnect(INetGameService service)
    {
        if (!OwnsCleanup || !ReferenceEquals(service, _transport)) return false;
        PreservedDisconnects++;
        Hooks.Log("TRANSPORT_DISCONNECT_SUPPRESSED");
        return true;
    }
    internal static bool AllowMessage(INetMessage message)
    {
        if (!SuppressNative || message is UndoMinimalMessage or HeartbeatRequestMessage or HeartbeatResponseMessage) return true;
        if (message is ChecksumDataMessage or StateDivergenceMessage) DroppedChecksumMessages++;
        Hooks.Log($"REPLAY_MESSAGE_DROPPED type={message.GetType().Name}");
        return false;
    }
    internal static bool AllowChecksum()
    {
        if (!SuppressNative) return true;
        SuppressedChecksumCalls++;
        return false;
    }
    private static object? NativeTransport(INetGameService service) => service switch
    {
        NetHostGameService host => host.NetHost,
        NetClientGameService client => client.NetClient,
        _ => null
    };
    private static void VerifyTransport()
    {
        if (_transport is null) return;
        if (!ReferenceEquals(RunManager.Instance.NetService, _transport)
            || !ReferenceEquals(NativeTransport(_transport), _nativeTransport)
            || !_transport.IsConnected || _transport.GetRawLobbyIdentifier() != _lobbyId)
            throw new InvalidOperationException("Undo replaced or lost the existing multiplayer session.");
        Hooks.Log($"TRANSPORT_RETAINED role={_transport.Type} platform={_transport.Platform} lobby={_lobbyId}");
    }
    internal static void ResumeNative()
    {
        VerifyTransport();
        var manager = RunManager.Instance;
        manager.CombatStateSynchronizer.IsDisabled = false;
        manager.CombatReplayWriter.IsEnabled = true;
        AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(manager, new object[] { _shouldSave });
        manager.ChecksumTracker.IsEnabled = true;
        NativeResumed = true;
        Hooks.Log($"NATIVE_REARMED checksumNextId={manager.ChecksumTracker.NextId}");
    }
    private static bool _injecting;
    internal static bool IsInjecting => _injecting;
    internal static bool OwnsCleanup { get; private set; }
    private static int _choiceWaits;
    private static int _choiceInjections;
    private static CancellationTokenSource _liveWaits = new();
    private static int _liveWaitCount;
    internal static Task GuardLiveWait(Task task)
    {
        if (SuppressNative || !CombatManager.Instance.IsInProgress || task.IsCompleted) return task;
        return Guard(task, _liveWaits.Token);
    }
    internal static string DescribeProgress()
    {
        var manager = RunManager.Instance;
        var combat = CombatManager.Instance;
        if (!manager.IsInProgress) return "run=false";
        return $"run=true combat={combat.IsInProgress} side={combat.DebugOnlyGetState()?.CurrentSide} "
            + $"ending={combat.EndingPlayerTurnPhaseOne}/{combat.EndingPlayerTurnPhaseTwo} "
            + $"sync={manager.ActionQueueSynchronizer.CombatState} executor={manager.ActionExecutor.IsRunning}/{manager.ActionExecutor.IsPaused}";
    }
    private static async Task Guard(Task task, CancellationToken token)
    {
        _liveWaitCount++;
        try { await task.WaitAsync(token); }
        finally { _liveWaitCount--; }
    }
    private static void CancelDiscardedWaits()
    {
        var discarded = _liveWaits;
        _liveWaits = new();
        Hooks.Log($"DISCARDED_WAITS_CANCEL count={_liveWaitCount}");
        discarded.Cancel();
        discarded.Dispose();
    }

    internal static async Task<uint> ExecuteAsync(CombatReplay replay, CancellationToken token)
    {
        var prefs = SaveManager.Instance.PrefsSave;
        var speed = prefs.FastMode;
        try
        {
            return await ExecuteCoreAsync(replay, token);
        }
        finally
        {
            ModelOnly = false;
            prefs.FastMode = speed;
            Hooks.Log($"REPLAY_SPEED_RESTORED mode={speed}");
        }
    }

    private static async Task<uint> ExecuteCoreAsync(CombatReplay replay, CancellationToken token)
    {
        ulong restoreStarted = Time.GetTicksMsec();
        var manager = RunManager.Instance;
        var old = manager.NetService;
        var role = old.Type;
        _shouldSave = manager.ShouldSave;
        NativeResumed = false;
        var players = manager.RunLobby?.Players.ToArray();
        if (role is NetGameType.Host or NetGameType.Client)
        {
            _transport = old;
            _nativeTransport = NativeTransport(old);
            _lobbyId = old.GetRawLobbyIdentifier();
            if (players is null || !players.Select(p => p.id).ToHashSet().SetEquals(replay.serializableRun.Players.Select(p => p.NetId)))
                throw new InvalidOperationException("Existing connected roster differs from replay participants.");
        }
        manager.ChecksumTracker.IsEnabled = false;
        manager.CombatStateSynchronizer.IsDisabled = true;
        old.SetBufferMessages(false); // Drain old native buffered packets through the replay receive gate.
        Choices.Clear(); Actions.Clear(); _choiceWaits = _choiceInjections = 0;
        foreach (var entry in replay.events.Where(e => e.eventType == CombatReplayEventType.PlayerChoice))
            Choices.Add((entry.playerId!.Value, entry.choiceId!.Value), new());
        Hooks.Log($"RESTORE_BEGIN role={role} events={replay.events.Count} speed={SaveManager.Instance.PrefsSave.FastMode}");
        await NGame.Instance!.Transition.FadeOut(0.05f);
        // Keep the normal fade-out path: Instant FadeOut permanently hides the
        // native transition node. Accelerate reconstruction only after it ends.
        Hooks.Log($"REPLAY_SPEED from={SaveManager.Instance.PrefsSave.FastMode} to={FastModeType.Instant}");
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        UI.ShowRestoring();
        // Exit native UI against its original run owners before disposing them.
        NGame.Instance.RootSceneContainer.SetCurrentScene(new Control());
        await Frame(token);
        manager.CombatReplayWriter.IsEnabled = false;
        // Stop the discarded executor instead of waiting for the old combat
        // to become playable/idle. Drain its callbacks before creating owners.
        var oldExecutor = manager.ActionExecutor;
        oldExecutor.Cancel();
        CancelDiscardedWaits();
        OwnsCleanup = true;
        try
        {
            // Normal game cleanup relies on dropping the entire transport for
            // these owners. Retaining it requires their public handler disposal.
            manager.EventSynchronizer.Dispose();
            manager.OneOffSynchronizer.Dispose();
            manager.CombatStateSynchronizer.Dispose();
            manager.InputSynchronizer.Dispose();
            manager.CleanUp(graceful: true);
        }
        finally { OwnsCleanup = false; }
        oldExecutor.Unpause();
        await oldExecutor.FinishedExecutingActions().WaitAsync(token);
        token.ThrowIfCancellationRequested();

        ModelOnly = true;
        _skippedPileVisuals = _skippedCardNodes = _skippedDamageNumbers = _skippedPreviews = _skippedTransformVisuals = 0;
        Hooks.Log("MODEL_REPLAY_BEGIN");
        var state = RunState.FromSerializable(replay.serializableRun);
        Network.ReportProgress("restore-owners");
        if (role == NetGameType.Singleplayer)
        {
            await manager.SetUpSavedSingleplayer(state, replay.serializableRun);
            Network.Attach(manager.NetService);
        }
        else
        {
            // Initialization holder over the already authenticated peers. No
            // Steam lobby creation, transport replacement or join handshake.
            var lobby = new LoadRunLobby(old, new LoadListener(), replay.serializableRun);
            try
            {
                lobby.Players.AddRange(players!.Select(p => new LoadRunLobbyPlayer
                {
                    id = p.id, isModded = p.isModded, isReady = true
                }));
                await manager.SetUpSavedMultiplayer(state, lobby);
                NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, lobby.PlayerIds);
                NGame.Instance.ReactionContainer.InitializeNetworking(old);
            }
            finally { lobby.CleanUp(disconnectSession: false); }
            VerifyTransport();
        }

        token.ThrowIfCancellationRequested();
        // Public setters and private setter verified in RunManager.InitializeShared.
        AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.ShouldSave)).Invoke(manager, new object[] { false });
        manager.ChecksumTracker.IsEnabled = false;
        manager.CombatStateSynchronizer.IsDisabled = true;
        await PreloadManager.LoadRunAssets(state.Players.Select(p => p.Character));
        Network.ReportProgress("restore-assets");
        await PreloadManager.LoadActAssets(state.Act);
        token.ThrowIfCancellationRequested();
        manager.Launch();
        await manager.GenerateMap();
        Network.ReportProgress("restore-room");
        manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
        manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
        manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
        manager.RewardsSetSynchronizer.FastForwardRewardIds(replay.rewardIds);
        Hooks.Log($"RESTORE_SEEDED action={replay.nextActionId} hook={replay.nextHookId}");
        token.ThrowIfCancellationRequested();
        await manager.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(replay.serializableRun.PreFinishedRoom, state));
        await WaitUntilAsync(() => !manager.ActionExecutor.IsPaused, token);

        ulong replayStarted = Time.GetTicksMsec();
        for (int i = 0; i < replay.events.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfActionFailed();
            var entry = replay.events[i];
            Hooks.Log($"REPLAY_EVENT index={i} kind={entry.eventType}");
            Network.ReportProgress($"replay-event:{i}/{replay.events.Count}:{entry.eventType}");
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
        // Drain model callbacks before constructing any native combat UI.
        Network.ReportProgress("replay-idle");
        await WaitUntilAsync(Hooks.IsReplayIdle, token);
        await Frame(token);
        await WaitUntilAsync(Hooks.IsReplayIdle, token);
        ThrowIfActionFailed();
        // Pure hash overload: no native packet, tracked checksum or NextId
        // advance. Native traffic stays suppressed until all Done values match.
        uint checksum = manager.ChecksumTracker.GenerateChecksum(NetFullCombatState.FromRun(state, null));
        ModelOnly = false;
        Network.ReportProgress("restore-ui");
        await UI.AttachRestoredCombatAsync(state, token);
        token.ThrowIfCancellationRequested();
        uint attached = manager.ChecksumTracker.GenerateChecksum(NetFullCombatState.FromRun(state, null));
        if (attached != checksum) throw new InvalidOperationException("Combat UI attachment changed the restored model.");
        Hooks.Log($"MODEL_UI_ATTACHED checksum={checksum} pileVisuals={_skippedPileVisuals} cardNodes={_skippedCardNodes} damageNumbers={_skippedDamageNumbers} previews={_skippedPreviews} transforms={_skippedTransformVisuals}");
        Hooks.Log($"REPLAY_DONE events={replay.events.Count} waits={_choiceWaits} choices={_choiceInjections} checksum={checksum} "
            + $"restoreMs={replayStarted - restoreStarted} replayMs={Time.GetTicksMsec() - replayStarted} totalMs={Time.GetTicksMsec() - restoreStarted}");
        return checksum;
    }

    // These verified native helpers only present model changes. Do not skip
    // CardPileCmd.Add/Shuffle or their model hooks, and do not enable TestMode.
    [HarmonyPatch(typeof(RunManager), "ClearScreens")]
    private static class AbsentScreens
    {
        private static bool Prefix() => !ModelOnly;
    }

    [HarmonyPatch(typeof(CardPileCmd), "CreateCardNodeAndUpdateVisuals")]
    private static class CardVisual
    {
        private static bool Prefix(ref NCard __result)
        {
            if (!ModelOnly) return true;
            _skippedCardNodes++;
            __result = null!;
            return false;
        }
    }

    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.GetTweenForCardsChangingPiles), new[] { typeof(IEnumerable<CardPileAddResult>), typeof(bool) })]
    private static class PileVisuals
    {
        private static bool Prefix(IEnumerable<CardPileAddResult> results, bool fromSilentAdd, ref (Tween?, bool) __result)
        {
            if (!ModelOnly) return true;
            _skippedPileVisuals++;
            // Native silent moves postpone these public events to this helper.
            // Preserve them while omitting card nodes, trails and tween callbacks.
            if (fromSilentAdd)
                foreach (var result in results.Where(result => result.success))
                {
                    result.oldPile?.InvokeCardRemoved(result.cardAdded);
                    result.oldPile?.InvokeCardRemoveFinished();
                    result.oldPile?.InvokeContentsChanged();
                }
            __result = (null, false); // No tween and no animation requiring pacing.
            return false;
        }
    }

    [HarmonyPatch(typeof(NDamageNumVfx), nameof(NDamageNumVfx.Create), new[] { typeof(Vector2), typeof(int) })]
    private static class DamageNumber
    {
        private static bool Prefix(ref NDamageNumVfx __result)
        {
            if (!ModelOnly) return true;
            _skippedDamageNumbers++;
            __result = null!;
            return false;
        }
    }

    [HarmonyPatch(typeof(CardCmd), "PreviewInternal")]
    private static class CardPreview
    {
        private static bool Prefix(ref TaskCompletionSource? __result)
        {
            if (!ModelOnly) return true;
            _skippedPreviews++;
            __result = null; // The native no-preview return value.
            return false;
        }
    }

    private static bool ShowTransformedCard(CardModel card)
    {
        if (!ModelOnly) return LocalContext.IsMine(card);
        _skippedTransformVisuals++;
        return false;
    }

    [HarmonyPatch]
    private static class TransformedCardVisuals
    {
        private static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Transform),
                new[] { typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle) });
            return method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType
                .GetMethod(nameof(IAsyncStateMachine.MoveNext), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var mine = AccessTools.Method(typeof(LocalContext), nameof(LocalContext.IsMine), new[] { typeof(CardModel) });
            int matches = 0;
            foreach (var instruction in instructions)
            {
                // Verified single IsMine call guards only Transform's VFX loop.
                // Keep native transformations, pile order, hooks and removal.
                if (instruction.Calls(mine))
                {
                    instruction.operand = AccessTools.Method(typeof(Replay), nameof(ShowTransformedCard));
                    matches++;
                }
                yield return instruction;
            }
            if (matches != 1) throw new InvalidOperationException("Native card transform visual guard changed.");
        }
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim))]
    private static class CreatureAnimation
    {
        private static bool Prefix(ref Task __result)
        {
            if (!ModelOnly) return true;
            // Native code already returns when the creature node is absent.
            __result = Task.CompletedTask;
            return false;
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
            if (action.Exception is not null)
                throw new InvalidOperationException($"Replayed {action.GetType().Name} {action.Id} failed: {action.Exception.GetBaseException().Message}", action.Exception);
    }
    private static void Inject(Action action)
    {
        _injecting = true;
        try { action(); } finally { _injecting = false; }
    }

    internal static void Clear()
    {
        ModelOnly = false;
        foreach (var choice in Choices.Values) choice.TrySetCanceled();
        Choices.Clear(); Actions.Clear(); _transport = null; _nativeTransport = null; _lobbyId = null; NativeResumed = false;
    }

    internal static void DisconnectSession()
    {
        _transport?.Disconnect(NetError.Quit, now: true);
    }

    private sealed class LoadListener : ILoadRunLobbyListener
    {
        public void BeginRun() { }
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
