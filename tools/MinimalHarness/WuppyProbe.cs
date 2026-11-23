using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using UndoSync;
using WuppyRelic = MegaCrit.Sts2.Core.Models.Relics.Wuppy;
using WuppyMonster = MegaCrit.Sts2.Core.Models.Monsters.Wuppy;

namespace MinimalHarness;

// Event acquisition and damage use native shared-event messages / enemy turns.
// Debug room entry selects an allowed native event and later exercises room
// teardown/recreate; it does not claim normal map travel or reward coverage.
internal static class WuppyProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "wuppy";
    internal static IEnumerable<ActModel> Acts => ActModel.GetDefaultList().Select(a => Active && a.Index == 0 ? ModelDb.Act<Underdocks>() : a);
    internal static void PrepareUnlocks() => SaveManager.Instance.ObtainEpochOverride(EpochModel.GetId<UnderdocksEpoch>(), EpochState.Revealed);
    private static int Branch => CommandLineHelper.GetValue("minimal-wuppy-branch") == "immediate" ? 0 : 1;
    private static int MaxHp => Branch == 0 ? 60 : 30;
    private static int _damageTurns, _deaths, _restores, _partialTurns;
    private static string PetState() => JsonSerializer.Serialize(Harness.State.Players.Select(p =>
    {
        var relic = p.Relics.OfType<WuppyRelic>().Single();
        var pet = relic.CombatWuppy!;
        return new { p.NetId, relic.CurrentHp, relic.MaxHp, hp = pet.CurrentHp, max = pet.MaxHp,
            owner = pet.PetOwner!.NetId, count = p.PlayerCombatState!.Pets.Count(c => c.Monster is WuppyMonster) };
    }));

    internal static async Task Prepare()
    {
        var nativeEvent = ModelDb.Event<WuppyInTrouble>();
        Harness.Require(nativeEvent.IsAllowed(Harness.State), "native Wuppy event eligibility");
        Harness.Require(Harness.State.Acts[Harness.State.CurrentActIndex].GetRoomsDebug().events.Any(e => e.Id == nativeEvent.Id),
            "FIXTURE_ERROR: Wuppy belongs to current native generated event pool");
        var point = Harness.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Unknown)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Harness.Manager.EnterMapCoordDebug(point.coord, RoomType.Event, MapPointType.Unknown, nativeEvent, showTransition: false);
        Harness.Require(Harness.Manager.EventSynchronizer.GetLocalEvent() is WuppyInTrouble, "actual native Wuppy event entered");
        await Harness.Barrier("wuppy-event-entered");
        Harness.Manager.EventSynchronizer.ChooseLocalOption(Branch);
        await Harness.Wait(() => Harness.Manager.EventSynchronizer.GetLocalEvent().IsFinished);
        await Harness.Manager.EventSynchronizer.AwaitPendingOptionTasks();
        Harness.Require(Harness.State.Players.All(p => p.Relics.OfType<WuppyRelic>().Single() is { } r
            && r.CurrentHp == MaxHp && r.MaxHp == MaxHp), "native event awarded correct Wuppy HP to every player");
        Harness.Require(Branch != 0 || Harness.State.Players.All(p => p.Deck.Cards.Any(c => c.Id.Entry == "INJURY")),
            "immediate rescue produced its native Injury cost");
        Harness.Log($"WUPPY_EVENT branch={Branch} maxHp={MaxHp} players={Harness.State.Players.Count} nativeVote=true");
        await Harness.Barrier("wuppy-event-complete");
    }

    private static async Task Observe(string label)
    {
        for (int i = 0; i < 12; i++) await NGame.Instance!.AwaitProcessFrame();
        var room = NCombatRoom.Instance!;
        Harness.Require(room.CreatureNodes.Count(n => n.Entity.Monster is WuppyMonster) == Harness.State.Players.Count,
            "one native Wuppy view per player, no duplicates");
        foreach (var player in Harness.State.Players)
        {
            var relic = player.Relics.OfType<WuppyRelic>().Single();
            var pets = player.PlayerCombatState!.Pets.Where(c => c.Monster is WuppyMonster).ToArray();
            Harness.Require(pets.Length == 1 && pets[0] == relic.CombatWuppy && pets[0].PetOwner == player,
                "replacement relic, pet collection and owner refer to the same creature");
            Harness.Require(relic.CurrentHp == pets[0].CurrentHp && relic.MaxHp == pets[0].MaxHp && relic.MaxHp == MaxHp,
                "saved Wuppy HP and combat HP match");
            var node = room.GetCreatureNode(pets[0]);
            Harness.Require(node is not null && node.Entity == pets[0] && !node.IsQueuedForDeletion(), "native pet view bound to current model");
            if (pets[0].IsAlive) Harness.Require(node!.IsVisibleInTree(), "living restored Wuppy is visible");
        }
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"wuppy-{label}.{Harness.Role}.json"), PetState());
        if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"wuppy-{label}-{Harness.Role}.png"));
        Harness.Log($"WUPPY_OBSERVED label={label} state={PetState()}");
        await Harness.StateBarrier("wuppy-" + label);
    }

    private static async Task EndUntilDeath(string label)
    {
        for (int turn = 0; Harness.State.Players.Any(p => p.Relics.OfType<WuppyRelic>().Single().CurrentHp > 0); turn++)
        {
            Harness.Require(turn < 20 && CombatManager.Instance.IsInProgress, "bounded normal enemy attacks reach Wuppy death");
            int before = Harness.State.Players.Sum(p => p.Relics.OfType<WuppyRelic>().Single().CurrentHp);
            await Harness.EndTurnAll($"wuppy-{label}-{turn}");
            int after = Harness.State.Players.Sum(p => p.Relics.OfType<WuppyRelic>().Single().CurrentHp);
            if (after < before) _damageTurns++;
        }
        _deaths++;
        Harness.Require(_damageTurns > 0, "native enemy attacks actually damaged Wuppy");
        await Observe(label + "-dead");
    }

    private static async Task Restore(int target, string native, string pets, string proposer, string label)
    {
        await Harness.Barrier(label + "-ready");
        if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
        await Harness.Wait(() => Network.Voting);
        if (Harness.Role != proposer)
        {
            await Harness.Wait(() => UI.CurrentPopup is not null);
            await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "wuppy-accept");
        }
        await Harness.Wait(() => !Network.Busy, 60);
        Harness.Require(Network.Status == "Undo complete", "Wuppy undo completed");
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == native && PetState() == pets,
            "exact native state and explicit Wuppy owner/HP restored");
        Harness.RecordUndo();
        _restores++;
        await Observe(label);
    }

    private static async Task MutateCard(string label)
    {
        await Harness.Barrier("wuppy-mutate-" + label);
        if (Harness.Role == Harness.Roles[0]) await Harness.PlayOne();
        await Harness.Barrier("wuppy-mutated-" + label);
        await Harness.Idle();
    }

    internal static async Task Run()
    {
        await Observe("opening");
        // A real card supplies a selectable history row before any enemy damage.
        await Harness.Barrier("wuppy-first-card");
        if (Harness.Role == Harness.Roles[0]) await Harness.PlayOne();
        await Harness.Barrier("wuppy-first-card-done");
        await Harness.Idle();
        int target = History.Count;
        string native = Harness.Snapshot(), pets = PetState();
        Harness.Require(target > 0, "selectable real-card history prefix exists");
        await Observe("target");
        for (int attempt = 0; attempt < Math.Max(2, Harness.Roles.Length); attempt++)
        {
            await MutateCard("attempt" + attempt);
            await EndUntilDeath("attempt" + attempt);
            await Restore(target, native, pets, Harness.Roles[attempt % Harness.Roles.Length], "restored" + attempt);
        }
        // Save a damaged (not full-HP) target too, rather than only testing reset.
        while (Harness.State.Players.All(p => p.Relics.OfType<WuppyRelic>().Single().CurrentHp == MaxHp))
        {
            Harness.Require(_partialTurns < 20, "bounded native partial damage");
            await Harness.EndTurnAll("wuppy-partial-target-turn-" + _partialTurns++);
        }
        int partial = History.Count;
        string partialNative = Harness.Snapshot(), partialPets = PetState();
        Harness.Require(Harness.State.Players.Any(p => p.Relics.OfType<WuppyRelic>().Single().CurrentHp is > 0 && p.Relics.OfType<WuppyRelic>().Single().CurrentHp < MaxHp),
            "native attacks produced a living damaged Wuppy target");
        await Observe("partial-target");
        await MutateCard("partial");
        await EndUntilDeath("partial");
        await Restore(partial, partialNative, partialPets, Harness.Roles[^1], "partial-restored");
        await Harness.Barrier("wuppy-continued-ready");
        if (Harness.Role == Harness.Roles[0]) await Harness.PlayOne();
        await Harness.Barrier("wuppy-continued-done");
        await Harness.Idle();
        string carried = PetState();
        var next = Harness.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster && p.coord != Harness.State.CurrentMapCoord)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Harness.Manager.EnterMapCoordDebug(next.coord, RoomType.Monster, MapPointType.Monster, showTransition: false);
        await Harness.Idle();
        Harness.Require(PetState() == carried, "native next combat reconstructed saved Wuppy HP and owners");
        await Observe("next-combat");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"wuppy-proof.{Harness.Role}.json"), JsonSerializer.Serialize(new
            { branch = Branch, max_hp = MaxHp, damage_turns = _damageTurns, deaths = _deaths, restores = _restores,
                native_event = true, native_enemy_damage = true, next_combat = true, owner_ui_checks = true }));
        Harness.Log($"WUPPY_PASS branch={Branch} damageTurns={_damageTurns} deaths={_deaths} restores={_restores}");
        await Harness.Barrier("wuppy-terminal");
    }
}
