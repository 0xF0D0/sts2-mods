using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using UndoSync;

namespace MinimalHarness;

// Legal native merchant acquisition; Sadness/Joy are generated ONLY by native
// Vitriol/Ecstasy. No injected pauses, choice results, HP, card IDs or powers.
internal static class TurnStartProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "turn-start";
    private static readonly List<string> Trace = new();
    private static int _joys;
    private static readonly Dictionary<ulong, int> JoysByPlayer = new();
    private static bool CanPlay(string role, string id) => PileType.Hand.GetPile(Harness.State.Players[Array.IndexOf(Harness.Roles, role)])
        .Cards.Any(c => c.Id.Entry == id && c.CanPlay());
    internal static async Task Prepare()
    {
        foreach (var player in Harness.State.Players)
        {
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Where(c => c.Id.Entry != "DEFEND_FOOL").ToArray(), showPreview: false);
            // Retain three native starting Defends, remove the fourth natively.
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Skip(3).ToArray(), showPreview: false);
            foreach (string id in new[] { "ECSTASY", "VITRIOL" })
            {
                var options = player.Character.CardPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint).Where(c => c.Id.Entry == id).ToArray();
                Harness.Require(options.Length == 1, "FIXTURE_ERROR: native unlocked merchant pool contains " + id);
                var card = CardFactory.CreateForMerchant(player, options, Array.Empty<CardModel>(), options[0].Rarity).Card;
                Harness.Require((await CardPileCmd.Add(card, PileType.Deck)).success, "native merchant acquired " + id);
            }
        }
    }
    private static async Task Play(string label, string role, string id)
    {
        await Harness.Barrier(label + "-ready");
        var actor = Harness.State.Players[Array.IndexOf(Harness.Roles, role)];
        var card = PileType.Hand.GetPile(actor).Cards.FirstOrDefault(c => c.Id.Entry == id && c.CanPlay());
        Harness.Require(card is not null, "native playable probe card " + id);
        Trace.Add($"{actor.PlayerCombatState!.TurnNumber}:{role}:{id}");
        if (Harness.Role == role)
        {
            bool finished = false;
            void Done(GameAction action) { if (action is PlayCardAction p && p.OwnerId == actor.NetId) finished = true; }
            Harness.Manager.ActionExecutor.AfterActionExecuted += Done;
            try
            {
                var target = card!.TargetType == TargetType.AnyEnemy ? CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First() : null;
                Harness.Require(card.TryManualPlay(target), "native probe card accepted " + id);
                await Harness.Wait(() => finished);
                Harness.RecordCard();
            }
            finally { Harness.Manager.ActionExecutor.AfterActionExecuted -= Done; }
        }
        await Harness.Barrier(label + "-played");
        await Harness.Idle();
        if (id == "JOY") { _joys++; JoysByPlayer[actor.NetId] = JoysByPlayer.GetValueOrDefault(actor.NetId) + 1; }
    }
    private static async Task Picture(string label)
    {
        var me = LocalContext.GetMe(Harness.State)!;
        foreach (var card in PileType.Hand.GetPile(me).Cards)
        {
            var holder = NCombatRoom.Instance!.Ui.Hand.GetCardHolder(card);
            Harness.Require(holder?.CardNode?.Model == card && holder.CardNode.IsVisibleInTree(),
                "native hand holder is bound to current transformed card model");
        }
        Harness.Log("TURN_START_UI currentModelBindings=" + PileType.Hand.GetPile(me).Cards.Count);

        if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 20; i++) await NGame.Instance!.AwaitProcessFrame();
            RenderingServer.ForceDraw();
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"turn-start-{label}-{Harness.Role}.png"));
        }
    }
    private static string AvailableCard(string role) => PileType.Hand.GetPile(Harness.State.Players[Array.IndexOf(Harness.Roles, role)])
        .Cards.First(c => c.CanPlay()).Id.Entry;

    internal static async Task Run()
    {
        for (int round = 1; round <= 7; round++)
        {
            var generatedFor = new List<ulong>();
            foreach (string role in Harness.Roles)
            {
                var actor = Harness.State.Players[Array.IndexOf(Harness.Roles, role)];
                Harness.Log($"TURN_START_HAND round={round} actor={role} cards={string.Join(",", PileType.Hand.GetPile(actor).Cards.Select(c => c.Id.Entry))}");
                if (round == 1) await Play($"turn-{round}-{role}-power", role, "ECSTASY");
                else if (CanPlay(role, "JOY")) await Play($"turn-{round}-{role}-joy", role, "JOY");
                if (CanPlay(role, "VITRIOL"))
                {
                    await Play($"turn-{round}-{role}-vitriol", role, "VITRIOL");
                    generatedFor.Add(actor.NetId);
                }
                else Trace.Add($"{round}:{role}:VITRIOL_NOT_DRAWN");
                // Enemy-generated Slimed legitimately crowds out deck cards.
                // Exhaust it through native play instead of assuming a fixed hand.
                for (int status = 0; status < 2 && CanPlay(role, "SLIMED"); status++)
                    await Play($"turn-{round}-{role}-slimed-{status}", role, "SLIMED");
                if (CanPlay(role, "DEFEND_FOOL")) await Play($"turn-{round}-{role}-block", role, "DEFEND_FOOL");
            }
            await Harness.EndTurnAll("probe-end-" + round);
            Harness.Require(Harness.State.Players.All(p => p.PlayerCombatState!.TurnNumber == round + 1
                && p.Creature.GetPower<EcstasyPower>()?.Amount == 1
                && (!generatedFor.Contains(p.NetId) || PileType.Hand.GetPile(p).Cards.Any(c => c.Id.Entry == "JOY"))), "native Ecstasy turn-start transformation completed");
        }
        Harness.Require(Harness.State.Players.All(p => JoysByPlayer.GetValueOrDefault(p.NetId) >= 4), "at least four native generated Joy cards played per actor despite enemy status draws");
        int target = History.Count;
        string expected = Harness.Snapshot();
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"turn-start-trace.{Harness.Role}.json"), JsonSerializer.Serialize(Trace));
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"turn-start-target.{Harness.Role}.json"), JsonSerializer.Serialize(new { events = target, state = expected, turn = 8, joys = _joys, joys_by_player = JoysByPlayer }));
        await Picture("target");
        for (int attempt = 0; attempt < Math.Max(2, Harness.Roles.Length); attempt++)
        {
            await Play("mutate-" + attempt, Harness.Roles[0], AvailableCard(Harness.Roles[0]));
            string proposer = Harness.Roles[attempt % Harness.Roles.Length];
            await Harness.Barrier("probe-propose-" + attempt);
            if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
            await Harness.Wait(() => Network.Voting);
            if (Harness.Role != proposer)
            {
                await Harness.Wait(() => UI.CurrentPopup is not null);
                await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "turn-start-accept");
            }
            await Harness.Wait(() => !Network.Busy, 80);
            Harness.Require(Network.Status == "Undo complete", "multi-turn transformation replay completed");
            await Harness.Idle();
            Harness.Require(History.Count == target && Harness.Snapshot() == expected
                && Harness.State.Players.All(p => p.PlayerCombatState!.TurnNumber == 8), "exact seven-turn native transformation state restored");
            Harness.RecordUndo();
            await Picture("restored" + attempt);
            await Harness.StateBarrier("probe-restored-" + attempt);
        }
        await Play("continued-card", Harness.Roles[0], AvailableCard(Harness.Roles[0]));
        await Harness.EndTurnAll("probe-continued");
        Harness.Require(Harness.State.Players.All(p => p.PlayerCombatState!.TurnNumber == 9), "next native turn works after restoration");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"turn-start-proof.{Harness.Role}.json"), JsonSerializer.Serialize(new { turns = 7, replay_events = target, generated_joys_played = _joys, generated_joys_by_player = JoysByPlayer, final_turn = 9 }));
        await Harness.Barrier("probe-terminal");
    }
}
