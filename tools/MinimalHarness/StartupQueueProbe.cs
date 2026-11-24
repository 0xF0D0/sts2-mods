using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using UndoSync;

namespace MinimalHarness;

// A legal native ordering: startup hand choice -> enqueue input while paused ->
// choose -> resume. Observation uses public queue events, never modifies actions.
internal static class StartupQueueProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "startup-queue";
    private static bool Baseline => CommandLineHelper.GetValue("minimal-expect") == "baseline";
    private static string Mode => CommandLineHelper.GetValue("minimal-queued-action") ?? "use";
    private static readonly List<string> Original = new(), Replayed = new();
    private static readonly List<GameAction> Observed = new();
    private static bool _capture = true;
    private static Player Me => LocalContext.GetMe(Harness.State)!;
    private static string Prefix => "startup-queue-";
    private static string PotionId => Mode == "select" ? "SKILL_POTION" : "DEXTERITY_POTION";
    private static string CoreLog => Path.Combine(OS.GetUserDataDir(), "logs", $"UndoSync-{System.Environment.ProcessId}.log");

    internal static void Attach()
    {
        if (!Active) return;
        var queue = Harness.Manager.ActionQueueSet;
        void Record(string line)
        {
            if (!ReferenceEquals(queue, Harness.Manager.ActionQueueSet) || !History.Available || !CombatManager.Instance.IsInProgress) return;
            if (Network.Executing) Replayed.Add(line);
            else if (_capture) Original.Add(line);
            Harness.Log($"QUEUE_TRACE replay={Network.Executing} detail={line}");
        }
        queue.ActionEnqueued += action =>
        {
            if (!Network.Executing) Observed.Add(action);
            Record($"enqueue:{action.GetType().Name}:{action.OwnerId}:{action.Id}");
        };
        queue.ActionResumed += id => Record($"resume:{id}");
    }

    internal static async Task Prepare()
    {
        Harness.Require(Mode is "use" or "discard" or "select", "known queued-input mode");
        foreach (var player in Harness.State.Players)
        {
            // Five starting cards guarantee a normal playable post-selection hand.
            var keep = player.Deck.Cards.Where(c => c.Id.Entry.StartsWith("DEFEND_")).Take(3)
                .Concat(player.Deck.Cards.Where(c => c.Id.Entry.StartsWith("STRIKE_")).Take(2)).ToHashSet();
            Harness.Require(keep.Count == 5, "FIXTURE_ERROR five native starting cards");
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Where(c => !keep.Contains(c)).ToArray(), showPreview: false);
            var relic = RelicFactory.PullNextRelicFromBack(player, RelicRarity.Rare, r => r is GamblingChip);
            Harness.Require(relic is GamblingChip, "FIXTURE_ERROR native bag contains Gambling Chip");
            await RelicCmd.Obtain(relic.ToMutable(), player);
            foreach (string id in new[] { PotionId, "BLOCK_POTION" })
            {
                PotionModel? selected = null;
                for (int i = 0; i < 512; i++)
                {
                    var option = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
                    if (option.Id.Entry == id) { selected = option; break; }
                }
                Harness.Require(selected is not null, "FIXTURE_ERROR bounded native potion sample " + id);
                Harness.Require((await PotionCmd.TryToProcure(selected!.ToMutable(), player)).success, "native startup potion procured " + id);
            }
        }
    }

    // Called before the harness's ordinary Idle gate: this native hand selector
    // is intentionally part of turn setup, so Idle cannot become true yet.
    internal static async Task CompleteOpening()
    {
        await Harness.Wait(() => NCombatRoom.Instance?.Ui.Hand.IsInCardSelection == true
            && Observed.OfType<GenericHookGameAction>().Any(h => h.OwnerId == Me.NetId && h.State == GameActionState.GatheringPlayerChoice));
        Harness.Require(Me.PlayerCombatState!.Phase == PlayerTurnPhase.Start && Me.CanUseOrRemovePotions,
            "actual native startup choice permits potion input while phase Start");
        await Harness.Barrier(Prefix + "choice-open");
        if (Mode == "discard")
        {
            int slot = Me.PotionSlots.ToList().FindIndex(p => p?.Id.Entry == "BLOCK_POTION");
            Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(Me, (uint)slot, true));
        }
        else Me.Potions.Single(p => p.Id.Entry == PotionId).EnqueueManualUse(Me.Creature);
        await Harness.Wait(() => Observed.Any(a => a.OwnerId == Me.NetId && (Mode == "discard" ? a is DiscardPotionGameAction : a is UsePotionAction)));
        Harness.Require(NCombatRoom.Instance!.Ui.Hand.IsInCardSelection && Me.PlayerCombatState.Phase == PlayerTurnPhase.Start,
            "input really queued before startup selection completed");
        Harness.Log($"STARTUP_INPUT_QUEUED mode={Mode} phase={Me.PlayerCombatState.Phase}");
        await Harness.Barrier(Prefix + "all-input-queued");
        await Harness.ClickViaViewport(NCombatRoom.Instance.Ui.Hand.GetNode<NConfirmButton>("%SelectModeConfirmButton"), "startup-confirm-zero-discards");
        Harness.RecordSelection();
        if (Mode == "select")
        {
            await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
            var screen = (NChooseACardSelectionScreen)NOverlayStack.Instance!.Peek()!;
            ulong ready = Time.GetTicksMsec() + 400;
            await Harness.Wait(() => Time.GetTicksMsec() >= ready);
            var holder = screen.GetNode<Control>("CardRow").GetChildren().OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder>().First();
            await Harness.ClickViaViewport(holder.Hitbox, "queued-potion-choice");
            Harness.RecordSelection();
        }
        await Harness.Barrier(Prefix + "all-choices-completed");
    }

    internal static async Task Run()
    {
        Harness.Require(Harness.State.Players.All(p => Mode == "discard"
            ? !p.Potions.Any(x => x.Id.Entry == "BLOCK_POTION")
            : !p.Potions.Any(x => x.Id.Entry == PotionId)), "queued native input actually executed after choice");
        if (Mode == "use") Harness.Require(Harness.State.Players.All(p => p.Creature.GetPower<DexterityPower>()?.Amount == 2), "queued potion really applied two Dexterity");
        await Harness.Actor(Prefix + "target-card", Harness.Roles[0]);
        int target = History.Count;
        string expected = Harness.Snapshot();
        _capture = false;
        string[] expectedTrace = Original.ToArray();
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"startup-original.{Harness.Role}.json"), JsonSerializer.Serialize(expectedTrace));
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"startup-target.{Harness.Role}.json"), JsonSerializer.Serialize(new { target, expected, mode = Mode }));
        for (int attempt = 0; attempt < Math.Max(2, Harness.Roles.Length); attempt++)
        {
            await Harness.Actor(Prefix + "mutate-" + attempt, Harness.Roles[0]);
            var prefix = History.Freeze(target);
            int hook = prefix.events.FindIndex(e => e.eventType == CombatReplayEventType.HookAction);
            int input = prefix.events.FindIndex(e => e.eventType == CombatReplayEventType.GameAction && (Mode == "discard" ? e.action is NetDiscardPotionGameAction : e.action is NetUsePotionAction));
            int choice = prefix.events.FindIndex(e => e.eventType == CombatReplayEventType.PlayerChoice);
            Harness.Require(hook >= 0 && input > hook && choice > input && prefix.events.Skip(choice).Any(e => e.eventType == CombatReplayEventType.ResumeAction),
                "real recorded order is Hook -> queued input -> Choice -> Resume");
            Replayed.Clear();
            await Harness.Barrier(Prefix + "propose-" + attempt);
            string proposer = Harness.Roles[attempt % Harness.Roles.Length];
            if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
            await Harness.Wait(() => Network.Voting);
            if (Harness.Role != proposer)
            {
                await Harness.Wait(() => UI.CurrentPopup is not null);
                await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "startup-accept");
            }
            if (Baseline)
            {
                ulong deadline = Time.GetTicksMsec() + 6000;
                await Harness.Wait(() => !Network.Busy || Time.GetTicksMsec() >= deadline);
                string log = File.ReadAllText(CoreLog);
                Harness.Require(Network.Busy && Me.PlayerCombatState!.Phase == PlayerTurnPhase.Start
                    && Replayed.Any(x => x.Contains("GenericHookGameAction"))
                    && !Replayed.Any(x => x.Contains(Mode == "discard" ? "DiscardPotionGameAction" : "UsePotionAction"))
                    && log.Contains("REPLAY_TURN_WAIT"), "BASELINE_CONFIRMED native startup hook starved by queued-input readiness wait");
                Harness.Log("STARTUP_BASELINE_STALL diagnostic=" + Replay.DescribeProgress());
                Network.Fail("Task-owned confirmed startup-queue baseline cleanup.");
                await Harness.Wait(() => !Network.Busy && !Harness.Manager.IsInProgress);
                return;
            }
            await Harness.Wait(() => !Network.Busy, 45);
            Harness.Require(Network.Status == "Undo complete", "startup queued-input restoration complete");
            await Harness.Idle();
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "startup target state and history exact");
            Harness.Require(Replayed.SequenceEqual(expectedTrace), "native enqueue/resume action IDs and order preserved exactly");
            File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"startup-replay-{attempt}.{Harness.Role}.json"), JsonSerializer.Serialize(Replayed));
            foreach (var card in PileType.Hand.GetPile(Me).Cards)
            {
                var holder = NCombatRoom.Instance!.Ui.Hand.GetCardHolder(card);
                Harness.Require(holder?.CardNode?.Model == card && holder.CardNode.IsVisibleInTree(), "startup current hand model binding");
            }
            Harness.Require(!NCombatRoom.Instance!.Ui.Hand.IsInCardSelection && NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen,
                "startup replay leaves no native choice UI");
            Harness.RecordUndo();
            if (DisplayServer.GetName() != "headless")
            {
                RenderingServer.ForceDraw();
                NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"startup-restored-{attempt}-{Harness.Role}.png"));
            }
            await Harness.StateBarrier(Prefix + "restored-" + attempt);
        }
        await Harness.Actor(Prefix + "continued", Harness.Roles[0]);
        await Harness.EndTurnAll(Prefix + "next-turn");
        if (Harness.Roles.Length > 1) await Harness.CheckConnectedUi(Prefix + "final");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"startup-proof.{Harness.Role}.json"), JsonSerializer.Serialize(new { mode = Mode, target, expected, trace_checks = Math.Max(2, Harness.Roles.Length), native_input_while_start = true }));
        Harness.Log("STARTUP_QUEUE_PASS mode=" + Mode);
        await Harness.Barrier(Prefix + "terminal");
    }
}
