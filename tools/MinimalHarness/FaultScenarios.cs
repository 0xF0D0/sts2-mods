using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using UndoSync;

namespace MinimalHarness;

// Deliberate faults at real native async/send boundaries. No production private
// state, synthetic participant, replacement vote, transport or checksum.
internal static class FaultScenarios
{
    private static string Suite => CommandLineHelper.GetValue("minimal-suite") ?? "";
    private static bool Baseline => CommandLineHelper.GetValue("minimal-expect") == "baseline";
    private static string Last => Harness.Roles[^1];
    private static TestControl Control => Harness.TestControl;
    private static bool _armed, _held, _released, _oldFinished;
    private static GameAction? _oldAction;
    private static int _drops;
    private static int _round;
    private static string? _matrixOp, _matrixSide, _matrixOpening;
    private static ulong _firstDrop;
    private static string Tag(string name) => $"fault-{_round}-{name}";
    private static readonly NetMessageBus Decoder = new(new PacketReader(), new PacketWriter());

    private static string? ChoiceCard => CommandLineHelper.GetValue("minimal-choice-card");
    internal static CharacterModel? ChoiceCharacter => Suite != "fault-turn-choice" ? null
        : ChoiceCard is "TYRANNY" or "FOREGONE_CONCLUSION" ? ModelDb.Character<Regent>() : ModelDb.Character<Silent>();

    internal static void PrepareChoiceUnlocks()
    {
        if (ChoiceCard == "ENTROPY")
            SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless1Epoch>(), EpochState.Revealed);
    }

    internal static async Task PrepareTurnChoice()
    {
        foreach (var player in Harness.State.Players)
        {
            var keep = player.Deck.Cards.Where(card => card.Id.Entry.StartsWith("DEFEND_")).Take(4).ToHashSet();
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Where(card => !keep.Contains(card)).ToArray(), showPreview: false);
            var pool = ChoiceCard is "ENTROPY" or "STRATAGEM" ? ModelDb.CardPool<ColorlessCardPool>() : player.Character.CardPool;
            var option = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .Single(card => card.Id.Entry == ChoiceCard);
            var card = CardFactory.CreateForMerchant(player, new[] { option }, Array.Empty<CardModel>(), option.Rarity).Card;
            Harness.Require((await CardPileCmd.Add(card, PileType.Deck)).success, "native shop choice card acquired: " + ChoiceCard);
        }
    }

    private static async Task TurnChoice()
    {
        await Harness.Actor("turn-choice-prefix", "host");
        int target = History.Count;
        string expected = Harness.Snapshot();
        foreach (string role in Harness.Roles)
        {
            if (Harness.Role == role)
            {
                var card = PileType.Hand.GetPile(LocalContext.GetMe(Harness.State)!).Cards.Single(card => card.Id.Entry == ChoiceCard);
                bool finished = false;
                var executor = Harness.Manager.ActionExecutor;
                void Done(GameAction action) { if (action is PlayCardAction play && play.CardModelId == card.Id && play.OwnerId == card.Owner.NetId) finished = true; }
                executor.AfterActionExecuted += Done;
                try
                {
                    Harness.Require(card.TryManualPlay(null), "native turn-start choice card played");
                    await Harness.Wait(() => finished);
                }
                finally { executor.AfterActionExecuted -= Done; }
                Harness.RecordCard();
            }
            await Harness.Barrier("turn-choice-card-" + role);
            await Harness.Idle();
        }
        var outgoing = (Task)AccessTools.PropertyGetter(typeof(CombatManager), "DebugOnlyCurrentTurnLoopTask")
            .Invoke(CombatManager.Instance, null)!;
        await Harness.Barrier("turn-choice-end-turn");
        var player = LocalContext.GetMe(Harness.State)!;
        Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
        await Harness.Wait(() => NPlayerHand.Instance?.IsInCardSelection == true
            || NOverlayStack.Instance?.Peek() is NCombatPileCardSelectScreen, 30);
        Harness.Log("TURN_CHOICE_OPEN card=" + ChoiceCard);
        await Harness.Barrier("turn-choice-open");
        if (Harness.Role == "host") await Harness.RequestViaViewport(target);
        await Harness.Wait(() => Network.Busy && Network.Voting && UI.CurrentPopup is not null);
        await Harness.Barrier("turn-choice-voting");
        if (Harness.Role != "host") await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "accept-turn-choice");
        await Harness.Wait(() => !Network.Busy, 30);
        Harness.Require(Network.Status == "Undo complete" && outgoing.IsCompleted, "pending turn-start choices unwound before restored play");
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == expected, "turn-start choice undo restored exact native target");
        Harness.RecordUndo();
        await Harness.StateBarrier("turn-choice-restored");
        await Harness.Actor("turn-choice-continued", "client");
        await Harness.StateBarrier("turn-choice-final");
        await Harness.Barrier("turn-choice-terminal");
    }

    private static async Task KnowledgeChoice()
    {
        await Harness.Actor("knowledge-prefix", "host", attack: true);
        int target = History.Count;
        string expected = Harness.Snapshot();
        await Harness.Actor("knowledge-extra", "client", attack: true);
        var outgoing = (Task)AccessTools.PropertyGetter(typeof(CombatManager), "DebugOnlyCurrentTurnLoopTask")
            .Invoke(CombatManager.Instance, null)!;
        await Harness.Barrier("knowledge-end-turn");
        var player = LocalContext.GetMe(Harness.State)!;
        Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
        await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen, 30);
        Harness.Log("KNOWLEDGE_CHOICE_OPEN native=true");
        await Harness.Barrier("knowledge-choice-open");
        string chooser = CommandLineHelper.GetValue("minimal-knowledge-chooser") ?? "host";
        Harness.Require(chooser == "none" || Harness.Roles.Contains(chooser), "valid Knowledge Demon completed-choice role");
        if (DisplayServer.GetName() != "headless")
        {
            RenderingServer.ForceDraw();
            Harness.Require(NGame.Instance!.GetViewport().GetTexture().GetImage()
                .SavePng(Path.Combine(Harness.DirectoryPath, "knowledge-choice-" + Harness.Role + ".png")) == Error.Ok,
                "captured native Knowledge Demon choice screen");
        }
        if (Harness.Role == chooser)
        {
            await FramesFor(400);
            var screen = (NChooseACardSelectionScreen)NOverlayStack.Instance!.Peek()!;
            var holder = screen.GetNode<Control>("CardRow").GetChildren().OfType<NGridCardHolder>().First();
            var viewport = NGame.Instance!.GetViewport();
            Vector2 position = holder.GetGlobalRect().GetCenter();
            viewport.PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            await NGame.Instance.AwaitProcessFrame();
            viewport.PushInput(new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await Harness.Wait(() => NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen);
            Harness.Log("KNOWLEDGE_LOCAL_CHOICE_COMPLETED chooser=" + chooser + " remotePending=true");
            await Control.Put("knowledge-one-chosen", "true");
        }
        if (chooser != "none") await Harness.Wait(() => Control.Read("knowledge-one-chosen") is not null);
        if (Harness.Role != chooser)
            Harness.Require(NOverlayStack.Instance!.Peek() is NChooseACardSelectionScreen, "remote native curse choice remains open");
        await Harness.Barrier("knowledge-before-request");
        if (Harness.Role == "host") await Harness.RequestViaViewport(target);
        await Harness.Wait(() => Network.Busy && Network.Voting && UI.CurrentPopup is not null);
        await Harness.Barrier("knowledge-vote-ready");
        if (Harness.Role != "host")
            await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "accept-knowledge-choice");
        if (Baseline)
        {
            if (Harness.Role == "host")
            {
                await FramesFor(2500);
                Harness.Require(Network.Busy && !outgoing.IsCompleted, "BASELINE_GAP discarded enemy turn still awaits a remote choice");
                Harness.Log("KNOWLEDGE_BASELINE_STALLED oldTurnCompleted=false");
                Network.Fail("Task-owned knowledge-choice baseline cleanup.");
            }
            await Harness.Wait(() => !Network.Busy && !Harness.Manager.IsInProgress, 15);
            return;
        }
        await Harness.Wait(() => !Network.Busy, 30);
        Harness.Require(Network.Status == "Undo complete" && outgoing.IsCompleted, "discarded enemy turn completed before restored play");
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == expected, "knowledge choice undo restored exact native target");
        Harness.RecordUndo();
        await Harness.StateBarrier("knowledge-restored");
        await Harness.Actor("knowledge-continued", "client", attack: true);
        await Harness.StateBarrier("knowledge-final");
        await Harness.Barrier("knowledge-terminal");
    }

    internal static async Task Run(string suite)
    {
        if (suite == "fault-turn-choice")
        {
            await TurnChoice();
            return;
        }
        if (suite == "fault-knowledge-choice")
        {
            await KnowledgeChoice();
            return;
        }
        if (suite == "fault-send-matrix")
        {
            _matrixOpening = Harness.Snapshot();
            foreach (var (op, side) in new[] {
                ("Vote", "client"), ("Ready", "client"), ("StartReceived", "client"),
                ("Done", "client"), ("Resumed", "client"), ("Ack", "client"),
                ("Request", "client"), ("Plan", "client"),
                ("Propose", "host"), ("Plan", "host"), ("Prepare", "host"),
                ("Start", "host"), ("Resume", "host"), ("Release", "host") })
            {
                _matrixOp = op; _matrixSide = side; _drops = 0; _firstDrop = 0;
                Harness.Log($"FAULT_MATRIX_CASE op={op} side={side}");
                await Run("fault-send");
            }
            return;
        }
        _round++;
        await Harness.StateBarrier(Tag("opening"));
        await Harness.Actor(Tag("prefix"), "host");
        int target = History.Count;
        string expected = Harness.Snapshot();
        if (_matrixOpening is not null) { target = 0; expected = _matrixOpening; }
        await Harness.Actor(Tag("after-target"), "client");
        await Harness.Barrier(Tag("ready"));
        if (suite == "fault-old-action")
        {
            if (Harness.Role == Last) _armed = true;
            await Harness.Barrier("old-armed");
            if (Harness.Role == Last)
            {
                var player = LocalContext.GetMe(Harness.State)!;
                var card = PileType.Hand.GetPile(player).Cards.First(c => c.CanPlay() && c.TargetType == TargetType.AnyEnemy);
                Harness.Require(card.TryManualPlay(CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(e => e.IsAlive)), "real attack accepted");
            }
            await Harness.Wait(() => Control.Read("old-effect-held") is not null);
        }
        string requester = _matrixSide == "client" && _matrixOp is "Request" or "Plan" ? Last : "host";
        if (Harness.Role == requester) Network.Request(target);
        await Harness.Wait(() => Network.Voting);
        Network.Vote(true);
        if (suite == "fault-replay-error")
        {
            await Harness.Wait(() => Control.Read("replay-effect-held") is not null);
            if (Harness.Role == "host")
            {
                await FramesFor(2500);
                string log = File.ReadAllText(Path.Combine(Harness.DirectoryPath, "host-terminal.log"));
                Harness.Require(log.Contains("PEER_PROGRESS") && log.Contains("peer=2001 stage=replay-idle")
                    && log.Contains("missingDone=[2001]"), "stalled peer reports its replay stage while host identifies missing Done");
                await Control.Put("release-error", "true");
            }
            await Harness.Wait(() => !Network.Busy && !Harness.Manager.IsInProgress, 25);
            Harness.Require(!UndoSync.Replay.ModelOnly, "failed replay leaves model-only presentation mode");
            Harness.Require(Network.Status.Contains("Injected native replay effect failure"), "all peers preserve original failure reason: " + Network.Status);
            Harness.Log("FAULT_TERMINAL " + Network.Status);
        }
        else if (suite == "fault-disconnect")
        {
            // All peers have Started, but hold the dropped peer's actual replay
            // action so it cannot contribute Done before the disconnect.
            await Harness.Wait(() => Control.Read("replay-effect-held") is not null);
            if (Harness.Role == Last)
            {
                Harness.Log("FAULT_REAL_DISCONNECT during replay");
                Harness.Manager.NetService.Disconnect(NetError.Quit, now: false);
                _released = true;
            }
            if (Baseline)
            {
                if (Harness.Role == "host")
                {
                    await FramesFor(3000);
                    Harness.Require(Network.Busy && Network.Executing && Harness.Manager.NetService.IsConnected,
                        "BASELINE_GAP host waits for disconnected participant while host service stays connected");
                    Network.Fail("Task-owned baseline disconnect cleanup.");
                }
            }
            await Harness.Wait(() => !Network.Busy && !Harness.Manager.IsInProgress, 25);
            Harness.Require(Network.Status != "Undo complete", "disconnect never commits partial replay");
            Harness.Log("FAULT_TERMINAL " + Network.Status);
        }
        else if (suite == "fault-send" && Baseline)
        {
            await Harness.Wait(() => Control.Read(Tag("send-dropped")) is not null);
            await FramesFor(3000);
            Harness.Require(Network.Busy, "BASELINE_GAP dropped control message leaves operation waiting");
            await Harness.Barrier(Tag("baseline-observed"));
            if (Harness.Role == "host") Network.Fail("Task-owned baseline send cleanup.");
            await Harness.Wait(() => !Network.Busy, 25);
        }
        else
        {
            await Harness.Wait(() => !Network.Busy, 40);
            Harness.Require(Network.Status == "Undo complete", "fault recovery terminal resume: " + Network.Status);
            await Harness.Wait(() => Network.PendingDeliveryCount == 0, 10);
            await Harness.Idle();
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "exact target after fault");
            Harness.RecordUndo();
            await Harness.Barrier(Tag("restored"));
            if (suite == "fault-old-action")
            {
                if (Harness.Role == Last)
                {
                    Harness.Require(_held, "genuine mid-effect hold executed");
                    if (Baseline) Harness.Require(!_oldFinished, "old effect task outlives executor and reconstruction");
                    else Harness.Require(_oldFinished, "discarded effect unwound before new run completes");
                    _released = true;
                    await Harness.Wait(() => _oldFinished, 15);
                    Harness.Log($"OLD_EFFECT_OBSERVED exception={_oldAction?.Exception} history={History.Count}");
                }
                await Harness.Barrier("old-released");
                await FramesFor(500);
                bool unchanged = Harness.Snapshot() == expected && History.Count == target && Hooks.IsIdle();
                Harness.Log("OLD_EFFECT_RESULT unchanged=" + unchanged);
                if (!Baseline) Harness.Require(unchanged, "discarded native effect cannot alter restored run");
            }
            if (suite == "fault-send") Harness.Require(Control.Read(Tag("send-dropped")) is not null, "real native send fault executed");
            await Harness.Actor(Tag("continued"), "host");
            await Harness.StateBarrier(Tag("continued"));
        }
        await Harness.Barrier(Tag("terminal"));
    }

    private static async Task FramesFor(ulong milliseconds)
    {
        ulong start = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - start < milliseconds) await NGame.Instance!.AwaitProcessFrame();
    }

    [HarmonyPatch(typeof(Cmd), nameof(Cmd.CustomScaledWait))]
    private static class MidEffectHold
    {
        private static void Postfix(ref Task __result)
        {
            if (Harness.Role != Last || _held) return;
            bool old = Suite == "fault-old-action" && _armed && !Network.Busy;
            bool replay = Suite is "fault-disconnect" or "fault-replay-error" && Network.Executing;
            if ((!old && !replay) || Harness.Manager.ActionExecutor.CurrentlyRunningAction is not PlayCardAction action) return;
            _held = true;
            _oldAction = action;
            Harness.Log("FAULT_MID_EFFECT_HOLD before native card body completes replay=" + replay);
            __result = Hold(__result, replay);
        }
        private static async Task Hold(Task original, bool replay)
        {
            await original;
            await Control.Put(replay ? "replay-effect-held" : "old-effect-held", "true");
            if (Suite == "fault-replay-error")
            {
                await Harness.Wait(() => Control.Read("release-error") is not null, 20);
                throw new InvalidOperationException("Injected native replay effect failure");
            }
            await Harness.Wait(() => _released, 40);
            Harness.Log("FAULT_MID_EFFECT_RELEASE");
        }
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    private static class ObserveOldEffect
    {
        private static void Postfix(PlayCardAction __instance, ref Task __result)
        {
            if (Suite == "fault-old-action" && ReferenceEquals(__instance, _oldAction)) __result = Observe(__result);
        }
        private static async Task Observe(Task original)
        {
            try { await original; }
            finally { _oldFinished = true; Harness.Log("OLD_EFFECT_BODY_TERMINATED"); }
        }
    }

    [HarmonyPatch(typeof(ENetClient), nameof(ENetClient.SendMessageToHost))]
    private static class DropNativeSend
    {
        private static bool Prefix(byte[] bytes, int length)
        {
            if (Suite is not ("fault-send" or "fault-send-matrix" or "fault-replay-error") || Harness.Role != Last
                || (_matrixSide ?? CommandLineHelper.GetValue("minimal-drop-side")) == "host") return true;
            return Keep(bytes, length);
        }
    }

    [HarmonyPatch(typeof(ENetHost), nameof(ENetHost.SendMessageToClient))]
    private static class DropHostSend
    {
        private static bool Prefix(ulong peerId, byte[] bytes, int length)
        {
            if (Suite is not ("fault-send" or "fault-send-matrix") || Harness.Role != "host"
                || (_matrixSide ?? CommandLineHelper.GetValue("minimal-drop-side")) != "host"
                || peerId != Harness.State.Players.Last().NetId) return true;
            return Keep(bytes, length);
        }
    }

    private static bool Keep(byte[] bytes, int length)
    {
            if (!Decoder.TryDeserializeMessage(bytes[..length], out var decoded, out _) || decoded is not UndoMinimalMessage message) return true;
            string op = Suite == "fault-replay-error" ? "Cancel" : _matrixOp ?? CommandLineHelper.GetValue("minimal-fault-op") ?? "Done";
            if (message.Op.ToString() != op) return true;
            if (_matrixOp == "Ack")
            {
                if (_firstDrop == 0) _firstDrop = Time.GetTicksMsec();
                if (Time.GetTicksMsec() - _firstDrop >= 1200) return true;
            }
            else if (_drops >= int.Parse(CommandLineHelper.GetValue("minimal-drop-count") ?? "1")) return true;
            _drops++;
            Harness.Log($"FAULT_NATIVE_SEND_DROPPED op={message.Op} count={_drops}");
            File.WriteAllText(Path.Combine(Harness.DirectoryPath, Tag("send-dropped")), message.Op.ToString());
            return false; // Same visible contract as Steam's log-only send rejection.
    }
}
