using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using UndoSync;

namespace MinimalHarness;

// Native, bounded gameplay fixtures. Patches only observe public game methods;
// they never replace gameplay, choices, waits, transport or production state.
internal static class CategoryProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "category";
    internal static string Profile => CommandLineHelper.GetValue("minimal-category") ?? "card";
    internal static bool Boss => Active && Profile == "enemy-replay";
    internal static CharacterModel Character => ModelDb.Character<Ironclad>();
    private static bool Mixed => Profile == "mixed";
    private static bool Pending => Profile.EndsWith("-pending");
    private static bool Boulder => Mixed || Profile == "turn-start";
    private static bool Choice => Mixed || Profile.StartsWith("choice");
    private static bool Potions => Mixed || Profile.StartsWith("potion");
    private static bool Barrier => Mixed || Profile == "turn-end";
    private static bool Initial => Mixed || Profile == "combat-start";
    private static string Actor => Harness.Roles[0];
    private static string Chooser => Potions && Harness.Roles.Length > 1 ? Harness.Roles[1] : Actor;
    private static readonly Dictionary<string, int> Live = new(), Replayed = new();
    private static int _targets, _restores, _pending, _choices;
    private static Player Player(string role) => Harness.State.Players[Array.IndexOf(Harness.Roles, role)];
    private static int EnemyHp => CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(c => c.CurrentHp);

    internal static void Unlock()
    {
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless1Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless2Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless3Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless4Epoch>(), EpochState.Revealed);
        SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<Colorless5Epoch>(), EpochState.Revealed);
    }

    internal static async Task Prepare()
    {
        Harness.Require(new[] { "turn-end", "turn-start", "card", "choice", "choice-pending", "potion", "potion-pending", "combat-start", "mixed", "enemy-replay" }.Contains(Profile), "known category fixture");
        foreach (var player in Harness.State.Players)
        {
            var desired = new List<string>();
            if (Boulder) desired.Add("ROLLING_BOULDER");
            if (Barrier) desired.Add("FLAME_BARRIER");
            if (Choice) desired.Add("DISCOVERY");
            if (Profile is "card" or "combat-start" or "enemy-replay" || Potions && !Mixed) desired.Add("ANGER");
            int keep = 5 - desired.Count;
            var defends = player.Deck.Cards.Where(c => c.Id.Entry == "DEFEND_IRONCLAD").Take(keep).ToHashSet();
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Where(c => !defends.Contains(c)).ToArray(), showPreview: false);
            for (int missing = player.Deck.Cards.Count; missing < keep; missing++) desired.Add("DEFEND_IRONCLAD");
            foreach (string id in desired)
            {
                var options = player.Character.CardPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                    .Concat(ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)).Where(c => c.Id.Entry == id).ToArray();
                Harness.Require(options.Length == 1, "FIXTURE_ERROR native unlocked card available " + id);
                var card = CardFactory.CreateForMerchant(player, options, Array.Empty<CardModel>(), options[0].Rarity).Card;
                Harness.Require((await CardPileCmd.Add(card, PileType.Deck)).success, "native merchant acquisition " + id);
            }
            Harness.Require(player.Deck.Cards.Count == 5, "native five-card category deck");
            if (Initial)
            {
                var relic = RelicFactory.PullNextRelicFromBack(player, RelicRarity.Common, r => r is Anchor);
                Harness.Require(relic is Anchor, "FIXTURE_ERROR native relic bag contains Anchor");
                await RelicCmd.Obtain(relic.ToMutable(), player);
            }
            if (Potions)
                foreach (string id in (Pending ? new[] { "SKILL_POTION", "BLOCK_POTION" } : new[] { "ENERGY_POTION", "SKILL_POTION", "BLOCK_POTION" }))
                {
                    PotionModel? selected = null;
                    for (int draw = 0; draw < 512; draw++)
                    {
                        var rolled = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
                        if (rolled.Id.Entry == id) { selected = rolled; break; }
                    }
                    Harness.Require(selected is not null, "FIXTURE_ERROR bounded native potion sampling " + id);
                    Harness.Require((await PotionCmd.TryToProcure(selected!.ToMutable(), player)).success, "native potion procured " + id);
                }
        }
        Harness.Log("CATEGORY_PREPARED profile=" + Profile);
    }

    private static void Observe(string name)
    {
        if (!Active) return;
        var counts = Network.Executing ? Replayed : Live;
        counts[name] = counts.GetValueOrDefault(name) + 1;
        Harness.Log($"CATEGORY_EVENT name={name} replay={Network.Executing} count={counts[name]}");
    }
    [HarmonyPatch(typeof(Anchor), nameof(Anchor.BeforeCombatStart))]
    private static class InitialObserver { private static void Prefix() => Observe("combat-start"); }
    [HarmonyPatch(typeof(FlameBarrierPower), nameof(FlameBarrierPower.AfterSideTurnEnd))]
    private static class EndObserver { private static void Prefix() => Observe("turn-end"); }
    [HarmonyPatch(typeof(RollingBoulderPower), nameof(RollingBoulderPower.AfterPlayerTurnStart))]
    private static class StartObserver { private static void Prefix() => Observe("turn-start"); }
    [HarmonyPatch(typeof(NRollingBoulderVfx), nameof(NRollingBoulderVfx.Create))]
    private static class BoulderObserver { private static void Prefix() => Observe("boulder-vfx"); }

    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeTurn))]
    private static class EnemyObserver { private static void Prefix(Creature __instance) { if (__instance.IsEnemy) Observe("enemy-action"); } }
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    private static class CardObserver { private static void Prefix() => Observe("card"); }
    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
    private static class PotionObserver
    {
        private static void Prefix(PotionModel __instance)
        {
            Observe("potion-use");
            if (__instance.Id.Entry == "ENERGY_POTION") Observe("potion-immediate");
        }
    }
    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.Discard))]
    private static class DiscardObserver { private static void Prefix() => Observe("potion-discard"); }

    private static async Task Select(string label, bool last = false)
    {
        if (Harness.Role != label) return;
        await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
        var screen = (NChooseACardSelectionScreen)NOverlayStack.Instance!.Peek()!;
        ulong ready = Time.GetTicksMsec() + 400;
        await Harness.Wait(() => Time.GetTicksMsec() >= ready);
        var holders = screen.GetNode<Control>("CardRow").GetChildren().OfType<NGridCardHolder>().ToArray();
        var holder = last ? holders.Last() : holders.First();
        await Harness.ClickViaViewport(holder.Hitbox, "category-choice");
        await Harness.Wait(() => NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen);
        _choices++; Harness.RecordSelection();
        Harness.Log("CATEGORY_CHOICE chooser=" + label);
    }

    private static async Task Play(string label, string id, bool selection = false)
    {
        await Harness.Barrier(label + "-ready");
        if (Harness.Role == Actor)
        {
            var player = Player(Actor);
            var card = PileType.Hand.GetPile(player).Cards.First(c => c.Id.Entry == id && c.CanPlay());
            var target = card.TargetType == TargetType.AnyEnemy ? CombatManager.Instance.DebugOnlyGetState()!.HittableEnemies.First() : null;
            Harness.Require(card.TryManualPlay(target), "native category card accepted " + id);
        }
        if (selection) await Select(Actor);
        await Harness.Barrier(label + "-sent");
        await Harness.Idle();
        if (Harness.Role == Actor) Harness.RecordCard();
        await Harness.StateBarrier(label + "-state");
    }

    private static async Task UsePotion(string label, bool finish)
    {
        await Harness.Barrier(label + "-start");
        int cards = PileType.Hand.GetPile(Player(Chooser)).Cards.Count;
        if (Harness.Role == Actor)
            Player(Actor).Potions.Single(p => p.Id.Entry == "SKILL_POTION").EnqueueManualUse(Player(Chooser).Creature);
        if (finish) await Select(Chooser);
        else if (Harness.Role == Chooser)
            await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
        await Harness.Barrier(label + "-choice-observed");
        if (finish)
        {
            await Harness.Idle();
            Harness.Require(!Player(Actor).Potions.Any(p => p.Id.Entry == "SKILL_POTION")
                && PileType.Hand.GetPile(Player(Chooser)).Cards.Count == cards + 1, "native potion consumed and card generated for actual target");
            Harness.Log($"CATEGORY_POTION actor={Actor} chooser={Chooser} branched={Actor != Chooser}");
        }
    }

    private static async Task CheckUi(string label)
    {
        var player = LocalContext.GetMe(Harness.State)!;
        foreach (var card in PileType.Hand.GetPile(player).Cards)
        {
            var holder = NCombatRoom.Instance!.Ui.Hand.GetCardHolder(card);
            Harness.Require(holder?.CardNode?.Model == card && holder.CardNode.IsVisibleInTree(), "category native hand model/holder binding");
        }
        Harness.Require(NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen, "no stale category choice UI");
        if (DisplayServer.GetName() != "headless")
        {
            for (int i = 0; i < 3; i++) await NGame.Instance!.AwaitProcessFrame();
            RenderingServer.ForceDraw();
            Harness.Require(NGame.Instance!.GetViewport().GetTexture().GetImage()
                .SavePng(Path.Combine(Harness.DirectoryPath, $"category-{Profile}-{label}-{Harness.Role}.png")) == Error.Ok, "category native UI captured");
        }
    }

    private static async Task Restore(int target, string expected, int attempt)
    {
        string proposer = Harness.Roles[attempt % Harness.Roles.Length];
        await Harness.Barrier("category-propose-" + attempt);
        if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
        await Harness.Wait(() => Network.Voting);
        if (Harness.Role != proposer)
        {
            await Harness.Wait(() => UI.CurrentPopup is not null);
            await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "category-accept");
        }
        await Harness.Wait(() => !Network.Busy, 60);
        Harness.Require(Network.Status == "Undo complete", "category restore terminal success");
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == expected, "category exact target state and history restored");
        Harness.RecordUndo(); _restores++; _targets++;
        await CheckUi("restore-" + attempt);
        await Harness.StateBarrier("category-restored-" + attempt);
    }

    internal static async Task Run()
    {
        if (Initial) Harness.Require(Harness.State.Players.All(p => p.Creature.Block == 10), "native initial Anchor block exactly ten");
        if (Boss)
        {
            await Play("boss-prefix", "DEFEND_IRONCLAD");
            await Harness.Barrier("boss-end-ready");
            var me = LocalContext.GetMe(Harness.State)!;
            Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
            await Select(Harness.Role, last: true);
            await Harness.Barrier("boss-all-chosen");
            await Harness.Idle();
            Harness.Require(Harness.State.Players.All(p => p.Creature.GetPower<MindRotPower>()?.Amount == 1
                && p.PlayerCombatState!.TurnNumber == 2), "actual boss choice applied Mind Rot and next turn began for every player");
            Harness.Log("CATEGORY_BOSS_CHOICE_COMPLETED native=true");
        }
        else if (Pending)
        {
            await Play("pending-prefix", "DEFEND_IRONCLAD");
        }
        else
        {
            if (Boulder)
            {
                await Play("boulder", "ROLLING_BOULDER");
                int hp = EnemyHp;
                int enemies = CombatManager.Instance.DebugOnlyGetState()!.Enemies.Count;
                await Harness.EndTurnAll("boulder-turn");
                Harness.Require(Player(Actor).Creature.GetPower<RollingBoulderPower>()?.Amount == 10
                    && hp - EnemyHp == 5 * enemies, "native boulder signal damage and post-hook amount executed");
            }
            if (Barrier)
            {
                await Play("flame-barrier", "FLAME_BARRIER");
                Harness.Require(Player(Actor).Creature.GetPower<FlameBarrierPower>() is not null, "native Flame Barrier actually applied");
            }
            if (Choice) await Play("discovery", "DISCOVERY", selection: true);
            if (Potions)
            {
                await Harness.Barrier("instant-potion-ready");
                int energy = Player(Actor).PlayerCombatState!.Energy;
                if (Harness.Role == Actor)
                    Player(Actor).Potions.Single(p => p.Id.Entry == "ENERGY_POTION").EnqueueManualUse(Player(Actor).Creature);
                await Harness.Wait(() => !Player(Actor).Potions.Any(p => p.Id.Entry == "ENERGY_POTION"));
                await Harness.Idle();
                Harness.Require(Player(Actor).PlayerCombatState!.Energy == energy + 2, "native immediate potion consumed and energy increased by two");
                Harness.Log("CATEGORY_POTION_IMMEDIATE native=true");
                await UsePotion("potion", finish: true);
                await Harness.Barrier("discard-potion-start");
                if (Harness.Role == Actor)
                {
                    var player = Player(Actor);
                    int slot = player.PotionSlots.ToList().FindIndex(p => p?.Id.Entry == "BLOCK_POTION");
                    Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(player, (uint)slot, true));
                }
                await Harness.Wait(() => !Player(Actor).Potions.Any(p => p.Id.Entry == "BLOCK_POTION"));
                await Harness.Idle();
                Harness.Log("CATEGORY_POTION_DISCARD native=true");
            }
            if (Barrier)
            {
                await Harness.EndTurnAll("flame-end");
                Harness.Require(Player(Actor).Creature.GetPower<FlameBarrierPower>() is null, "native Flame Barrier expired at enemy-turn end");
            }
            if (Profile is "card" or "combat-start") await Play("anger", "ANGER");
            if (Boulder || Barrier) await Play("post-hook-input", "DEFEND_IRONCLAD");
        }
        int target = History.Count;
        string expected = Harness.Snapshot();
        Harness.Require(target > 0, "nonempty native category prefix");
        await CheckUi("target");
        for (int attempt = 0; attempt < Math.Max(2, Harness.Roles.Length); attempt++)
        {
            if (Pending)
            {
                if (Potions)
                {
                    // Unique test-control barriers per repeated native operation.
                    await UsePotion("pending-potion-" + attempt, finish: false);
                }
                else
                {
                    if (Harness.Role == Actor)
                    {
                        var card = PileType.Hand.GetPile(Player(Actor)).Cards.Single(c => c.Id.Entry == "DISCOVERY");
                        Harness.Require(card.TryManualPlay(null), "native pending Discovery accepted");
                        await Harness.Wait(() => NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen);
                    }
                    await Harness.Barrier("pending-choice-" + attempt);
                }
                _pending++;
                Harness.Log($"CATEGORY_PENDING profile={Profile} attempt={attempt} chooser={Chooser}");
            }
            else await Play("mutation-" + attempt, "DEFEND_IRONCLAD");
            if (attempt == 0)
            {
            var prefix = History.Freeze(target);
            if (Boss) Harness.Require(prefix.events.Count(e => e.eventType == CombatReplayEventType.PlayerChoice) >= Harness.Roles.Length,
                "all actual boss choices are inside the restored historical prefix");
            if (Choice && !Pending || Potions && !Pending)
                Harness.Require(prefix.events.Any(e => e.eventType == CombatReplayEventType.PlayerChoice), "native category choice result recorded");
            if (!Pending && (Choice || Potions) && Harness.Roles.Length > 1 && Actor != Chooser)
                Harness.Require(prefix.events.Any(e => e.eventType == CombatReplayEventType.HookAction)
                    && prefix.events.Any(e => e.eventType == CombatReplayEventType.ResumeAction), "actual other-player choice branch recorded HookAction and ResumeAction");
            }
            await Restore(target, expected, attempt);
        }
        foreach (string required in new[] { Barrier ? "turn-end" : null, Boulder ? "turn-start" : null,
                     Boulder ? "boulder-vfx" : null, Initial ? "combat-start" : null,
                     !Pending && Profile != "potion" ? "card" : null, Potions && !Pending ? "potion-use" : null,
                     Potions && !Pending ? "potion-discard" : null, Potions && !Pending ? "potion-immediate" : null, Mixed || Boss ? "enemy-action" : null }.OfType<string>())
            Harness.Require(Live.GetValueOrDefault(required) > 0 && Replayed.GetValueOrDefault(required) >= Math.Max(2, Harness.Roles.Length),
                "native category path observed live and in both replays: " + required);
        await Play("continued", "DEFEND_IRONCLAD");
        await Harness.EndTurnAll("category-continued-turn");
        if (Harness.Roles.Length > 1) await Harness.CheckConnectedUi("category-final");
        var proof = new { profile = Profile, targets = _targets, restores = _restores, pending = _pending,
            live = Live, replayed = Replayed, choices = _choices, actor = Actor, chooser = Chooser,
            target_events = target, target_state = expected, final_turn = Player(Actor).PlayerCombatState!.TurnNumber };
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"category-proof.{Harness.Role}.json"), JsonSerializer.Serialize(proof));
        Harness.Log("CATEGORY_PASS profile=" + Profile);
        await Harness.Barrier("category-terminal");
    }
}
