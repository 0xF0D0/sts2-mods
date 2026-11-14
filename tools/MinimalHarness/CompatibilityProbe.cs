using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using UndoSync;

namespace MinimalHarness;

// Constructs legal native card/deck states, observes production UndoSync unchanged.
internal static class CompatibilityProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "compatibility";
    internal static GameMode Mode => CommandLineHelper.GetValue("minimal-mode") == "blitz" ? GameMode.Blitz : GameMode.Standard;
    private static string Profile => CommandLineHelper.GetValue("minimal-profile") ?? "arrows";
    private static string Actor => Harness.Roles[0];
    private static bool _nativeRewards;
    internal static IDisposable? ChoiceScope;
    internal static bool WantsSelector => Active && !_nativeRewards;
    internal static void PrepareUnlocks()
    {
        // Remote lobby players use their own account's native unlock list.
        foreach (string epoch in new[] { EpochModel.GetId<Fool2Epoch>(), EpochModel.GetId<Fool5Epoch>(), EpochModel.GetId<Fool7Epoch>() })
            SaveManager.Instance.ObtainEpochOverride(epoch, EpochState.Revealed);
        if (Mode == GameMode.Blitz) SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<BlitzEpoch>(), EpochState.Revealed);
    }
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (var node in Descendants(child)) yield return node;
        }
    }
    internal static async Task Prepare()
    {
        string[] cards = Profile switch
        {
            "arrows" => ["QUIVER", "QUICK_SHOT", "QUICK_SHOT", "RECALL", "DEFEND_FOOL"],
            "death" => ["QUIVER", "QUICK_SHOT", "QUICK_SHOT", "STRIKE_FOOL", "STRIKE_FOOL"],
            "destroy" => ["QUIETUS", "QUICK_SHOT", "DEFEND_FOOL", "DEFEND_FOOL", "STRIKE_FOOL"],
            "symbols" => ["WAH_CHO_KI", "DEFEND_FOOL", "DEFEND_FOOL", "STRIKE_FOOL", "STRIKE_FOOL"],
            _ => throw new InvalidOperationException("Unknown compatibility fixture")
        };
        foreach (var player in Harness.State.Players)
        {
            var remaining = cards.ToList();
            var remove = new List<CardModel>();
            foreach (var card in player.Deck.Cards)
            {
                if (remaining.Remove(card.Id.Entry)) continue;
                remove.Add(card);
            }
            await CardPileCmd.RemoveFromDeck(remove.ToArray(), showPreview: false);
            foreach (string id in remaining)
            {
                var option = player.Character.CardPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint).Single(c => c.Id.Entry == id);
                var card = CardFactory.CreateForMerchant(player, [option], Array.Empty<CardModel>(), option.Rarity).Card;
                Harness.Require((await CardPileCmd.Add(card, PileType.Deck)).success, "native factory deck card " + id);
            }
        }
        Harness.Log($"COMPAT_FIXTURE mode={Mode} profile={Profile} players={Harness.State.Players.Count}");
    }
    private static async Task Play(string label, string role, string id)
    {
        await Harness.Barrier(label + "-ready");
        if (Harness.Role == role)
        {
            var player = LocalContext.GetMe(Harness.State)!;
            var card = PileType.Hand.GetPile(player).Cards.First(c => c.Id.Entry == id && c.CanPlay());
            var target = card.TargetType == TargetType.AnyEnemy ? CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First() : null;
            bool done = false;
            var executor = Harness.Manager.ActionExecutor;
            void Finished(GameAction action) { if (action is PlayCardAction play && play.OwnerId == player.NetId) done = true; }
            executor.AfterActionExecuted += Finished;
            try
            {
                Harness.Require(card.TryManualPlay(target), "native compatibility card played " + id);
                await Harness.Wait(() => done);
            }
            finally { executor.AfterActionExecuted -= Finished; }
            Harness.RecordCard();
        }
        await Harness.Barrier(label + "-played");
        await SettleCombat();
    }

    private static async Task SettleCombat()
    {
        // Victory may finish between two peers reaching the post-action barrier.
        // An idle player phase will never occur after combat has ended.
        int settled = 0;
        await Harness.Wait(() =>
        {
            settled = !CombatManager.Instance.IsInProgress || Hooks.IsIdle() ? settled + 1 : 0;
            return settled >= 5;
        });
    }
    internal static string SpecialState()
    {
        return JsonSerializer.Serialize(Harness.State.Players.Select(p => new
        {
            id = p.NetId,
            arrows = p.PlayerCombatState!.Arrows.Select(a => new { status = a.Status.ToString(), a.DestroyAfterImpaled }),
            symbols = new[] {CardTag.Wah, CardTag.Cho, CardTag.Ki}.Where(t => MelodyCmd.PlayedThisTurn(p, t)).Select(t => t.ToString()),
            impaled = CombatManager.Instance.DebugOnlyGetState()!.Enemies.SelectMany(e => e.GetPowerInstances<ImpaledPower>())
                .Where(power => power.Applier == p.Creature).Select(power => new { power.Amount, power.HitCounter, arrows = power.Arrows.Count })
        }));
    }
    internal static void CheckUi()
    {
        var player = LocalContext.GetMe(Harness.State)!;
        var nodes = Descendants(NCombatRoom.Instance!).ToArray();
        var arrows = nodes.OfType<NFoolArrowUi>().ToArray();
        Harness.Require(arrows.Length == player.PlayerCombatState!.Arrows.Count,
            "native Fool arrow UI slots match model after scene reconstruction");
        foreach (var arrow in arrows)
        {
            Harness.Require(player.PlayerCombatState.Arrows.Contains(arrow.ArrowSlot), "native arrow UI bound to replacement slot owner");
            Harness.Require(arrow.IsVisibleInTree() == (arrow.ArrowSlot.Status != ArrowStatus.Destroyed), "native arrow visibility matches slot status");
        }
        var symbols = nodes.OfType<NFoolCombatSymbol>().Where(n => n.IsVisibleInTree()).Select(n => n.symbolType).Order().ToArray();
        var expected = new[] {CardTag.Wah, CardTag.Cho, CardTag.Ki}.Where(t => MelodyCmd.PlayedThisTurn(player, t)).Order().ToArray();
        Harness.Require(symbols.SequenceEqual(expected), "native Fool symbol UI matches actual melody history");
        int impaled = CombatManager.Instance.DebugOnlyGetState()!.Enemies.SelectMany(e => e.GetPowerInstances<ImpaledPower>()).Sum(p => p.Arrows.Count);
        Harness.Require(nodes.OfType<NImpaledArrowVfx>().Count(n => !n.IsQueuedForDeletion()) == impaled,
            "persistent impaled arrow visuals match native power arrows");
        Harness.Log($"COMPAT_UI profile={Profile} arrows={arrows.Length} symbols={symbols.Length} impaledVfx={nodes.OfType<NImpaledArrowVfx>().Count()}");
    }
    private static async Task Picture(string label)
    {
        for (int i = 0; i < 20; i++) await NGame.Instance!.AwaitProcessFrame();
        CheckUi();
        if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"compat-{Profile}-{Harness.Role}-{label}.png"));
    }
    internal static async Task Run()
    {
        Harness.Require(Harness.State.GameMode == Mode && (Mode != GameMode.Blitz || Harness.State.Map is BlitzActMap), "native requested mode and map reached combat");
        await Harness.StateBarrier("compat-opening");
        if (Profile is "arrows" or "death")
        {
            await Play("quiver", Actor, "QUIVER");
            await Play("shot1", Actor, "QUICK_SHOT");
            await Play("shot2", Actor, "QUICK_SHOT");
            Harness.Require(Harness.State.Players[0].PlayerCombatState!.Arrows.Count == 2
                && Harness.State.Players[0].PlayerCombatState!.AvailableArrowCount == 0, "two native arrow slots shot and retained");
            if (Profile == "death")
            {
                var enemies = CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.ToArray();
                Harness.Require(enemies.Length > 1 && enemies[0].CurrentHp <= 12,
                    "native encounter supports arrow-owner death without ending combat");
            }
        }
        else if (Profile == "destroy")
        {
            await Play("quietus", Actor, "QUIETUS");
            await Play("destroy-shot", Actor, "QUICK_SHOT");
            Harness.Require(Harness.State.Players[0].PlayerCombatState!.Arrows.Any(a => a.Status == ArrowStatus.Destroyed), "Quietus actually destroyed native arrow");
        }
        else
        {
            await Play("melodies", Actor, "WAH_CHO_KI");
            Harness.Require(MelodyCmd.MelodiesThisTurn(Harness.State.Players[0]) == 3, "all three native melody symbols reached");
        }
        int target = History.Count;
        string expected = Harness.Snapshot(), special = SpecialState();
        var coord = Harness.State.CurrentMapCoord;
        await Picture("target");
        for (int attempt = 0; attempt < Math.Max(2, Harness.Roles.Length); attempt++)
        {
            var victim = CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First();
            await Play("mutate" + attempt, Actor, Profile == "arrows" ? "RECALL" : Profile == "death" ? "STRIKE_FOOL" : "DEFEND_FOOL");
            if (Profile == "death")
            {
                await Play("kill" + attempt, Actor, "STRIKE_FOOL");
                Harness.Require(victim.IsDead && CombatManager.Instance.IsInProgress
                    && Harness.State.Players[0].PlayerCombatState!.AvailableArrowCount == 2,
                    "actual native target death returned both arrows while combat remained active");
                Harness.Log("COMPAT_ARROW_TARGET_DIED arrowsRetrieved=2");
            }
            if (Profile == "arrows") Harness.Require(Harness.State.Players[0].PlayerCombatState!.AvailableArrowCount == 2, "Recall actually retrieved both arrows");
            string proposer = Harness.Roles[attempt % Harness.Roles.Length];
            await Harness.Barrier("undo-ready" + attempt);
            if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
            await Harness.Wait(() => Network.Voting);
            if (Harness.Role != proposer)
            {
                await Harness.Wait(() => UI.CurrentPopup is not null);
                await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "compat-accept");
            }
            await Harness.Wait(() => !Network.Busy, 60);
            Harness.Require(Network.Status == "Undo complete", "compatibility replay terminal success");
            await Harness.Idle();
            Harness.Require(History.Count == target && Harness.Snapshot() == expected && SpecialState() == special,
                "exact native and additional arrow/melody state restored");
            Harness.Require(Harness.State.GameMode == Mode && Harness.State.CurrentMapCoord == coord
                && (Mode != GameMode.Blitz || Harness.State.Map is BlitzActMap), "mode, map type and room coordinate preserved");
            Harness.RecordUndo();
            await Picture("restored" + attempt);
            await Harness.StateBarrier("compat-restored" + attempt);
        }
        await Play("continued", Actor, Profile == "death" ? "STRIKE_FOOL" : "DEFEND_FOOL");
        await Harness.StateBarrier("compat-continued");
        if (CommandLineHelper.HasArg("minimal-progression"))
        {
            try { await ProgressBlitz(); }
            catch
            {
                if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
                    NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, "progression-failed-" + Harness.Role + ".png"));
                throw;
            }
        }
        await Harness.Barrier("compat-terminal");
    }
    private static async Task ProgressBlitz()
    {
        Harness.Require(Mode == GameMode.Blitz, "progression probe is Blitz");
        _nativeRewards = true;
        ChoiceScope?.Dispose();
        // These fixtures reach rewards through card damage and native enemy turns.
        // No debug damage, forced victory or direct room transition is used.
        for (int room = 0; room < 2; room++)
        {
            var oldCoord = Harness.State.CurrentMapCoord;
            int turns = 0;
            while (CombatManager.Instance.IsInProgress)
            {
                Harness.Require(turns++ < 10, "bounded native combat progression");
                foreach (string role in Harness.Roles)
                {
                    for (int step = 0; step < 10 && CombatManager.Instance.IsInProgress; step++)
                    {
                        await Harness.Barrier($"progress-{room}-{turns}-{role}-{step}");
                        var player = Harness.State.Players[Array.IndexOf(Harness.Roles, role)];
                        var card = PileType.Hand.GetPile(player).Cards.OrderBy(c => c.Id.Entry == "DEFEND_FOOL" ? 1 : 0).FirstOrDefault(c => c.CanPlay()
                            && (c.Id.Entry is "STRIKE_FOOL" or "WAH_CHO_KI" or "DEFEND_FOOL"
                                || c.Id.Entry == "QUICK_SHOT" && player.PlayerCombatState!.AvailableArrowCount > 0));
                        if (card is null) break;
                        await Play($"progress-play-{room}-{turns}-{role}-{step}", role, card.Id.Entry);
                    }
                    if (!CombatManager.Instance.IsInProgress) break;
                }
                if (!CombatManager.Instance.IsInProgress) break;
                int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
                await Harness.Barrier($"progress-endturn-{room}-{turns}");
                await Harness.ClickViaViewport(NCombatRoom.Instance!.Ui.EndTurnButton, "compat-progression-endturn");
                await Harness.Wait(() => !CombatManager.Instance.IsInProgress
                    || CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round && Hooks.IsIdle(), 60);
                await SettleCombat();
            }
            await Harness.Wait(() => Descendants(NRun.Instance!).OfType<NRewardsScreen>().Any(n => n.IsVisibleInTree()), 60);
            var rewards = Descendants(NRun.Instance!).OfType<NRewardsScreen>().Single(n => n.IsVisibleInTree());
            var button = Descendants(rewards).OfType<NRewardButton>().First(n => n.Reward is CardReward);
            var reward = (CardReward)button.Reward!;
            Harness.Require(reward.Cards.Count() == 3, "native Blitz combat generated three card reward options");
            int deckSize = LocalContext.GetMe(Harness.State)!.Deck.Cards.Count;
            await ClickSettled(button, "compat-open-card-reward");
            await Harness.Wait(() => Descendants(NRun.Instance!).OfType<NCardRewardSelectionScreen>().Any(n => n.IsVisibleInTree()));
            var select = Descendants(NRun.Instance!).OfType<NCardRewardSelectionScreen>().Single(n => n.IsVisibleInTree());
            var holder = Descendants(select).OfType<NGridCardHolder>().First();
            await ClickSettled(holder.Hitbox, "compat-take-card-reward");
            await Harness.Wait(() => LocalContext.GetMe(Harness.State)!.Deck.Cards.Count == deckSize + 1);
            await Harness.Barrier("progress-rewards-taken-" + room);
            await Harness.Wait(() => Harness.State.Players.All(p => p.Deck.Cards.Count == deckSize + 1));
            string decks = JsonSerializer.Serialize(Harness.State.Players.Select(p => new
            {
                p.NetId, cards = p.Deck.Cards.Select(c => new { id = c.Id.ToString(), c.CurrentUpgradeLevel })
            }));
            string key = "progress-decks-" + room;
            await Harness.TestControl.Put(key + "." + Harness.Role, decks);
            await Harness.Wait(() => Harness.Roles.All(r => Harness.TestControl.Read(key + "." + r) is not null));
            Harness.Require(Harness.Roles.All(r => Harness.TestControl.Read(key + "." + r) == decks),
                "native reward deck contents and upgrades agree on all peers");
            // Take the only first-fight reward; other reward kinds may be skipped normally.
            await ClickSettled(rewards.GetNode<NProceedButton>("ProceedButton"), "compat-proceed-next-room");
            await Harness.Wait(() => Harness.State.CurrentMapCoord != oldCoord && !Harness.Manager.IsChangingRooms, 60);
            if (room == 0)
            {
                await Harness.Wait(() => CombatManager.Instance.IsInProgress && Hooks.IsIdle(), 60);
                await Harness.Idle();
                await Harness.StateBarrier("blitz-next-combat");
                Harness.Require(Harness.State.CurrentMapCoord!.Value.row == oldCoord!.Value.row + 1, "Blitz advanced exactly one room after restored combat");
            }
            else
            {
                Harness.Require(Descendants(NRun.Instance!).OfType<NBlitzMerchantRoom>().Any(n => n.IsInsideTree()),
                    "native Blitz merchant room reached after second combat");
                Harness.Require(Harness.State.CurrentMapCoord!.Value.row == oldCoord!.Value.row + 1, "Blitz advanced once to merchant");
                await Harness.StateBarrier("blitz-merchant-reached");
                for (int i = 0; i < 20; i++) await NGame.Instance!.AwaitProcessFrame();
                if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
                    NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, "blitz-merchant-" + Harness.Role + ".png"));
            }
            Harness.Log($"BLITZ_PROGRESSION room={room} from={oldCoord} to={Harness.State.CurrentMapCoord} rewardTaken=true");
        }
    }

    private static async Task ClickSettled(Control control, string label)
    {
        Vector2 previous = new(float.NaN, float.NaN);
        int stable = 0;
        await Harness.Wait(() =>
        {
            Vector2 center = control.GetGlobalTransformWithCanvas() * (control.Size * 0.5f);
            stable = control.GetViewport().GetVisibleRect().HasPoint(center) && center.DistanceTo(previous) < 0.05f ? stable + 1 : 0;
            previous = center;
            return stable >= 4;
        });
        await Harness.ClickViaViewport(control, label);
    }

    internal sealed class Selector : ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var selected = options.Take(Math.Max(1, minSelect)).ToList();
            Harness.Require(selected.Count <= maxSelect && selected.All(c => c.Pile?.Type == PileType.Discard),
                "Recall fixture selects native discard-pile combat cards");
            var player = LocalContext.GetMe(Harness.State)!;
            uint id = Harness.Manager.PlayerChoiceSynchronizer.ChoiceIds[Harness.State.GetPlayerSlotIndex(player)] - 1;
            await NGame.Instance!.AwaitProcessFrame();
            Harness.Manager.PlayerChoiceSynchronizer.SyncLocalChoice(player, id, PlayerChoiceResult.FromMutableCombatCards(selected));
            Harness.RecordSelection();
            Harness.Log("COMPAT_RECALL_CHOICE encoding=MutableCombatCards count=" + selected.Count);
            return selected;
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("Compatibility rewards require actual native viewport input");
    }
}
