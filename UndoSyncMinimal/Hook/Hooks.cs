using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MinimalHarness")]

namespace UndoSyncMinimal;

[ModInitializer(nameof(Initialize))]
public static class Hooks
{
    private static ActionQueueSet? _queues;
    private static PlayerChoiceSynchronizer? _choices;
    // Verified in installed v0.111.0 source; no runtime-discovered API names.
    private static readonly FieldInfo ReplayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay");
    private static readonly FieldInfo QueuesField = AccessTools.Field(typeof(ActionQueueSet), "_actionQueues");
    private static readonly FieldInfo VisualQueueField = AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");
    private static readonly string LogPath = Path.Combine(
        OS.GetUserDataDir(), "logs", $"UndoSyncMinimal-{System.Environment.ProcessId}.log");
    private static int _lastCount = -1;
    private static int _idleFrames;

    public static void Initialize()
    {
        new Harmony("com.beomsu.undosyncminimal").PatchAll(typeof(Hooks).Assembly);
        RunManager.Instance.RunStarted += Bind;
        NGame.Instance!.GetTree().ProcessFrame += Tick;
        Log("INIT five-modules");
    }

    internal static void Log(string message)
    {
        string line = $"[UndoSyncMinimal pid={System.Environment.ProcessId}] {message}";
        GD.Print(line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, line + System.Environment.NewLine);
        }
        catch (IOException exception) { GD.PrintErr($"Log write failed: {exception.Message}"); }
    }

    private static void Bind(RunState state)
    {
        Unbind();
        _queues = RunManager.Instance.ActionQueueSet;
        _choices = RunManager.Instance.PlayerChoiceSynchronizer;
        _queues.ActionEnqueued += History.RecordAction;
        _queues.ActionResumed += History.RecordResume;
        _choices.PlayerChoiceReceived += History.RecordChoice;
        Network.Attach(RunManager.Instance.NetService);
        _idleFrames = 0;
        _lastCount = -1;
        Log($"BIND players={state.Players.Count}");
    }

    private static void Unbind()
    {
        if (_queues is not null)
        {
            _queues.ActionEnqueued -= History.RecordAction;
            _queues.ActionResumed -= History.RecordResume;
        }
        if (_choices is not null) _choices.PlayerChoiceReceived -= History.RecordChoice;
        _queues = null;
        _choices = null;
        _idleFrames = 0;
    }

    internal static bool IsIdle() => IsIdleCore(checkTransition: true);
    internal static bool IsReplayIdle() => IsIdleCore(checkTransition: false);

    private static bool IsIdleCore(bool checkTransition)
    {
        var manager = RunManager.Instance;
        var combat = CombatManager.Instance;
        if (!manager.IsInProgress || manager.IsCleaningUp
            || manager.NetService.Type is not (MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Singleplayer
                or MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Host
                or MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
            || !combat.IsInProgress
            || combat.IsPaused || combat.PlayerActionsDisabled
            || combat.EndingPlayerTurnPhaseOne || combat.EndingPlayerTurnPhaseTwo
            || combat.DebugOnlyGetState()?.CurrentSide != CombatSide.Player
            || manager.DebugOnlyGetState()!.Players.Any(p => p.Creature.IsAlive
                && p.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            || manager.ActionQueueSynchronizer.CombatState != ActionSynchronizerCombatState.PlayPhase
            || manager.ActionExecutor.IsPaused || manager.ActionExecutor.IsRunning
            || checkTransition && NGame.Instance?.Transition.InTransition == true) return false;
        // IsEmpty can remain false after cancellation. An executing/choice-waiting
        // action remains in this literal list until PopAction.
        var queues = (IEnumerable)QueuesField.GetValue(manager.ActionQueueSet)!;
        foreach (object queue in queues)
            if (((IList)AccessTools.Field(queue.GetType(), "actions").GetValue(queue)!).Count != 0)
                return false;
        return NCardPlayQueue.Instance is not { } visual
            || ((IList)VisualQueueField.GetValue(visual)!).Count == 0;
    }

    private static void Tick()
    {
        try
        {
            Network.Tick();
            if (!Network.Busy && History.Available && IsIdle())
            {
                _idleFrames = _lastCount == History.Count ? _idleFrames + 1 : 0;
                _lastCount = History.Count;
                if (_idleFrames == 2) History.MarkBoundary();
            }
            else _idleFrames = 0;
            UI.Tick();
        }
        catch (Exception exception)
        {
            Log($"FRAME_FAIL {exception}");
            Network.Fail(exception.Message);
        }
    }

    internal static void InstallWriter(CombatReplay replay) =>
        ReplayField.SetValue(RunManager.Instance.CombatReplayWriter, replay);

    [HarmonyPatch(typeof(CombatReplayWriter), nameof(CombatReplayWriter.RecordInitialState))]
    private static class HeaderPatch
    {
        private static void Postfix(CombatReplayWriter __instance)
        {
            if (!Network.Executing && ReplayField.GetValue(__instance) is CombatReplay replay)
                History.Begin(replay);
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    private static class CleanupPatch
    {
        private static void Prefix()
        {
            if (Network.Executing && !Replay.OwnsCleanup)
                Network.Fail("Run exited during undo.");
            Unbind();
            Network.Detach();
            UI.Close();
            if (!Network.Executing) { History.Clear(); Network.Reset(); }
            Log($"UNBIND replay={Network.Executing}");
        }
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
    private static class InputGate
    {
        private static bool Prefix() => !Network.Busy;
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueueHookAction))]
    private static class HookGate
    {
        private static bool Prefix() => !Network.Executing;
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice))]
    private static class ResumeRequestGate
    {
        private static bool Prefix() => !Network.Executing;
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    private static class InputPatch
    {
        private static void Prefix(InputEvent inputEvent)
        {
            if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Left })
            {
                UI.Open();
                NGame.Instance!.GetViewport().SetInputAsHandled();
            }
        }
    }
    [HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.EnqueueWithoutSynchronizing))]
    private static class EnqueueGate
    {
        private static bool Prefix() => !Network.Executing || Replay.IsInjecting;
    }
    [HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.ResumeActionWithoutSynchronizing))]
    private static class DirectResumeGate
    {
        private static bool Prefix() => !Network.Executing || Replay.IsInjecting;
    }
    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
    private static class ChoiceSendGate
    {
        private static bool Prefix() => !Network.Executing;
    }
    [HarmonyPatch(typeof(CardSelectCmd), "ShouldSelectLocalCard")]
    private static class CardChoiceGate
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Network.Executing) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(RelicSelectCmd), "ShouldSelectLocalRelic")]
    private static class RelicChoiceGate
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Network.Executing) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.WaitForRemoteChoice))]
    private static class ChoiceWaitPatch
    {
        private static bool Prefix(Player player, uint choiceId, ref Task<PlayerChoiceResult> __result)
        {
            if (!Network.Executing) return true;
            __result = Replay.RecordedChoiceAsync(player, choiceId);
            return false;
        }
    }
}
