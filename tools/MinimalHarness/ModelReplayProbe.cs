using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.TestSupport;
using UndoSync;

namespace MinimalHarness;

// Observes the production implementation only. This harness contains no UI,
// TestMode, damage, pile or restore adapters; all behavior lives in UndoSync.
internal static class ModelReplayProbe
{
    private static int _actions, _attachments;
    private static bool ProbeOrbs => CommandLineHelper.HasArg("minimal-probe-orbs");
    private static bool ProbeGenerated => CommandLineHelper.HasArg("minimal-probe-generated");
    private static bool ProbeTransform => CommandLineHelper.HasArg("minimal-probe-transform");
    private static bool ProbeFool => CommandLineHelper.GetValue("minimal-character") == "FOOL";

    internal static bool ProbeBoss => CommandLineHelper.HasArg("minimal-probe-boss");
    private static bool Mounted => !CommandLineHelper.HasArg("undosync-model-only-replay");
    private static string? VisualCard => CommandLineHelper.GetValue("minimal-probe-visual");
    private static string? FixtureCard => CommandLineHelper.HasArg("minimal-probe-power") ? "RUPTURE" : VisualCard;
    internal static CharacterModel? FixtureCharacter => FixtureCard switch
    {
        "SPOILS_OF_BATTLE" or "WROUGHT_IN_WAR" => ModelDb.Character<Regent>(),
        "BOUNCING_FLASK" => ModelDb.Character<Silent>(),
        "RUPTURE" or "HAND_OF_GREED" => ModelDb.Character<Ironclad>(),
        null => null,
        _ => throw new InvalidOperationException("Unknown visual replay fixture: " + FixtureCard)
    };

    internal static async Task PrepareFixture()
    {
        foreach (var player in Harness.State.Players)
        {
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.Where(c => !c.Id.Entry.StartsWith("DEFEND_")).ToArray(), showPreview: false);
            var pool = FixtureCard == "HAND_OF_GREED" ? ModelDb.CardPool<ColorlessCardPool>() : player.Character.CardPool;
            var option = pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
                .Single(card => card.Id.Entry == FixtureCard);
            var card = CardFactory.CreateForMerchant(player, new[] { option }, Array.Empty<CardModel>(), option.Rarity).Card;
            Harness.Require((await CardPileCmd.Add(card, PileType.Deck)).success, "native merchant card acquired: " + FixtureCard);
        }
    }

    private static void CheckFixture()
    {
        switch (FixtureCard)
        {
            case "RUPTURE":
                var powers = Harness.State.Players.Select(p => p.Creature.GetPower<RupturePower>()).OfType<RupturePower>().ToArray();
                Harness.Require(powers.Length == 1 && powers[0].Amount == 1, "native Rupture applied once and retained");
                break;
            case "SPOILS_OF_BATTLE":
            case "WROUGHT_IN_WAR":
                int expectedDamage = FixtureCard == "SPOILS_OF_BATTLE" ? 16 : 17;
                var blades = Harness.State.Players.SelectMany(p => PileType.Hand.GetPile(p).Cards).OfType<SovereignBlade>().ToArray();
                Harness.Require(blades.Length == 1 && blades[0].CreatedThroughForge && blades[0].DynamicVars.Damage.IntValue == expectedDamage,
                    $"native Forge generated one blade with {expectedDamage} damage");
                if (Mounted)
                    Harness.Require(SovereignBlade.GetVfxNode(blades[0].Owner, blades[0])?.IsInsideTree() == true,
                        "persistent native blade orbit is attached");
                break;
            case "BOUNCING_FLASK":
                int poison = CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(c => c.GetPower<PoisonPower>()?.Amount ?? 0);
                Harness.Require(poison == 9, "native Bouncing Flask applied nine poison");
                break;
        }
    }

    private static async Task Play(string label, string actor, bool extra = false)
    {
        bool playFixture = FixtureCard is not null && !extra && actor == Harness.Roles[^1];
        if (!playFixture && !ProbeOrbs && !ProbeGenerated && !ProbeTransform && !ProbeFool) { await Harness.Actor(label, actor); return; }
        await Harness.Barrier(label + "-ready");
        if (Harness.Role == actor)
        {
            string id = ProbeFool ? (extra ? "DEFEND_FOOL" : "FOOLISH_SHOT")
                : playFixture ? FixtureCard! : ProbeGenerated || ProbeTransform ? (extra ? "DEFEND_IRONCLAD" : ProbeTransform ? "PRIMAL_FORCE" : "ANGER") : (extra ? "DUALCAST" : "ZAP");
            var card = PileType.Hand.GetPile(LocalContext.GetMe(Harness.State)!).Cards.First(c => c.Id.Entry == id);
            bool done = false;
            var executor = Harness.Manager.ActionExecutor;
            void Finished(GameAction action) { if (action is PlayCardAction play && play.OwnerId == card.Owner.NetId) done = true; }
            executor.AfterActionExecuted += Finished;
            try
            {
                var target = card.TargetType == TargetType.AnyEnemy ? CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(c => c.IsAlive) : null;
                int hp = target?.CurrentHp ?? 0;
                Harness.Require(card.TryManualPlay(target), "actual native " + id + " played");
                await Harness.Wait(() => done);
                if (ProbeFool && !extra) Harness.Require(target!.CurrentHp < hp, "native Foolish Shot dealt arrow damage");
            }
            finally { executor.AfterActionExecuted -= Finished; }
            Harness.RecordCard();
        }
        await Harness.Barrier(label + "-played");
        await Harness.Idle();
    }

    internal static async Task Run()
    {
        string actor = Harness.Roles[0];
        if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 35; i++) await NGame.Instance!.AwaitProcessFrame();
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"model-before-{Harness.Role}.png"));
        }
        await Harness.StateBarrier("model-opening");
        if (ProbeBoss)
        {
            Harness.Require(Harness.Roles.Length == 1 && Mounted, "boss display probe uses rendered mounted singleplayer");
            int openingRound = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
            await Harness.ClickViaViewport(NCombatRoom.Instance!.Ui.EndTurnButton, "boss-liquify-turn");
            await Harness.Wait(() => CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > openingRound && Hooks.IsIdle(), 60);
            await Harness.Idle();
            Harness.Require(BossSandpit().Amount == 4, "native boss applied Sandpit during its first turn");
        }
        var prefixRoles = FixtureCard is not null ? Harness.Roles.Reverse() : Harness.Roles;
        foreach (string role in prefixRoles) await Play("model-prefix-" + role, role);
        CheckFixture();
        int? bossTargetAmount = null;
        if (ProbeBoss)
        {
            bossTargetAmount = BossSandpit().Amount;
            CheckBossDisplay(bossTargetAmount.Value);
            NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, "boss-target.png"));
        }
        int target = History.Count;
        string expected = Harness.Snapshot();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Play("model-extra-" + attempt, actor, extra: true);
            await Harness.Barrier("model-request-ready-" + attempt);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Normal;
            int actionsBefore = _actions;
            var timer = Stopwatch.StartNew();
            if (Harness.Role == actor) await Harness.RequestViaViewport(target);
            await Harness.Wait(() => Network.Voting);
            Network.Vote(true);
            await Harness.Wait(() => !Network.Busy, 35);
            Harness.Require(Network.Status == "Undo complete", "model replay terminal success: " + Network.Status);
            await Harness.Idle();
            Harness.Require(_actions > actionsBefore && !Replay.ModelOnly && !TestMode.IsOn, "actual replay actions ran and execution mode restored");
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "deferred UI leaves native target state unchanged");
            CheckFixture();
            if (bossTargetAmount is { } amount)
            {
                CheckBossDisplay(amount);
                var ui = NCombatRoom.Instance!.Ui;
                Harness.Require(!ui.CardPreviewContainer.GetChildren().OfType<NCard>().Any()
                    && !ui.MessyCardPreviewContainer.GetChildren().OfType<NCard>().Any(),
                    "no replay-only card previews remain on the boss screen");
            }
            if (Mounted)
                Harness.Require(!NCombatRoom.Instance!.GetChildren().Any(node => node is NCombatStartBanner or NPlayerTurnBanner or NEnemyTurnBanner),
                    "no replay banners remain in the restored room");
            var player = LocalContext.GetMe(Harness.State)!;
            var hand = NPlayerHand.Instance!;
            Harness.Require(NRun.Instance is not null && NCombatRoom.Instance is not null && hand is not null, "late native combat UI exists");
            Harness.Require(hand!.ActiveHolders.Count == PileType.Hand.GetPile(player).Cards.Count, "late hand nodes match current model hand");
            var map = NRun.Instance!.GlobalUi.MapScreen;
            Harness.Require(map.GetNode<Control>("TheMap/Points").GetChildCount() > 1, "late map points exist");
            await Harness.ClickViaViewport(NRun.Instance.GlobalUi.TopBar.Map, "model-open-map");
            await Harness.Wait(() => map.IsOpen);
            if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < 20; i++) await NGame.Instance!.AwaitProcessFrame();
                NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"model-map-{Harness.Role}-{attempt}.png"));
            }
            map.Close(animateOut: false);
            await Harness.Idle();
            Harness.Log($"MODEL_PROBE_DONE attempt={attempt} elapsedMs={timer.ElapsedMilliseconds} modelActions={_actions} attachments={_attachments}");
            Harness.RecordUndo();
            await Harness.StateBarrier("model-restored-" + attempt);
            if (!DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < 35; i++) await NGame.Instance!.AwaitProcessFrame();
                NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"model-ui-{Harness.Role}-{attempt}.png"));
            }
        }
        foreach (string role in Harness.Roles) await Play("model-continued-" + role, role, extra: true);
        await Harness.StateBarrier("model-continued-final");
        int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        await Harness.Barrier("model-endturn-ready");
        await Harness.ClickViaViewport(NCombatRoom.Instance!.Ui.EndTurnButton, "model-native-end-turn");
        await Harness.Wait(() => CombatManager.Instance.DebugOnlyGetState()?.RoundNumber > round && Hooks.IsIdle(), 60);
        await Harness.Idle();
        await Harness.StateBarrier("model-enemy-turn-continued");
        Harness.Log("MODEL_TURN_CONTINUED after late UI and actual End Turn button");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"model-proof.{Harness.Role}.json"), JsonSerializer.Serialize(new
        {
            schema = 1, role = Harness.Role, pid = System.Environment.ProcessId,
            model_actions = _actions, attachments = _attachments,
            native_test_mode_used_during_model_playback = false, native_test_mode_after = TestMode.IsOn,
            result = "PRODUCTION_MODEL_REPLAY_VERIFIED"
        }));
        await Harness.Barrier("model-terminal");
    }

    private static SandpitPower BossSandpit()
    {
        var combat = CombatManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(Harness.State)!;
        return combat.Enemies.SelectMany(enemy => enemy.Powers).OfType<SandpitPower>()
            .Single(power => power.Target == player.Creature);
    }

    private static void CheckBossDisplay(int expectedAmount)
    {
        var power = BossSandpit();
        Harness.Require(power.Amount == expectedAmount, "native Sandpit power retained");
        var boss = NCombatRoom.Instance!.GetCreatureNode(power.Owner)!;
        var display = boss.GetNode<NCreatureStateDisplay>("%HealthBar");
        var container = display.GetNode<NPowerContainer>("%PowerContainer");
        Harness.Require(container.GetChildren().OfType<NPower>().Any(node => node.Model == power && node.IsVisibleInTree()),
            "native Sandpit icon is visible and bound to the restored power");
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    private static class ObserveModelAction
    {
        private static void Prefix()
        {
            if (CommandLineHelper.GetValue("minimal-suite") == "preview") return;
            if (Replay.MountedReplay)
            {
                Harness.Require(NRun.Instance is not null && NCombatRoom.Instance is not null
                    && NGame.Instance!.GetNodeOrNull<CanvasLayer>("UndoSyncRestoring") is { Visible: true }
                    && !TestMode.IsOn, "mounted native combat scene is covered during replay");
                _actions++;
                return;
            }
            if (!Replay.ModelOnly) return;
            Harness.Require(NRun.Instance is null && NCombatRoom.Instance is null && !TestMode.IsOn,
                "MODEL_ACTION_WITHOUT_COMBAT_UI production=true testMode=false");
            _actions++;
        }
    }

    private sealed class MusicProbe
    {
        private readonly SceneTree _tree = NGame.Instance!.GetTree();
        private readonly GodotObject? _event = CurrentProxy()?.Get("_musicEv").AsGodotObject();
        private readonly string? _track = CurrentProxy()?.Get("_currentTrack").AsString();
        private readonly string? _expected = CombatManager.Instance.DebugOnlyGetState()?.Encounter?.CustomBgm;
        private readonly int _start;
        private int _samples, _inactive;

        internal MusicProbe()
        {
            _start = Timeline(_event);
            Sample();
            _tree.ProcessFrame += Sample;
        }

        private static Node? CurrentProxy() => NRun.Instance?.RunMusicController.GetNode("Proxy");

        private static int Timeline(GodotObject? music) =>
            GodotObject.IsInstanceValid(music) ? music!.Call("get_timeline_position").AsInt32() : -1;

        private void Sample()
        {
            _samples++;
            if (!GodotObject.IsInstanceValid(_event) || _event!.Call("get_playback_state").AsInt32() is not (0 or 1))
                _inactive++;
        }

        internal void Finish()
        {
            _tree.ProcessFrame -= Sample;
            Sample();
            var current = CurrentProxy()?.Get("_musicEv").AsGodotObject();
            Harness.Log($"BOSS_MUSIC same={_event is not null && _event == current} custom={_track == _expected && !string.IsNullOrEmpty(_track)}"
                + $" samples={_samples} inactive={_inactive} before={_start} after={Timeline(current)}");
        }
    }

    internal static bool OrbViewsMatch()
    {
        foreach (var node in NCombatRoom.Instance!.CreatureNodes)
        {
            if (!node.Entity.IsAlive || node.Entity.Player?.PlayerCombatState?.OrbQueue is not { } queue
                || node.OrbManager is not { } orbs) continue;
            var slots = orbs.GetNode<Control>("%Orbs").GetChildren().OfType<NOrb>().ToArray();
            if (slots.Length != queue.Capacity || !slots.Where(n => n.Model is not null).Select(n => n.Model).SequenceEqual(queue.Orbs))
                return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(Replay), nameof(Replay.ExecuteAsync))]
    private static class ObserveCompletion
    {
        private static void Prefix(out MusicProbe? __state) => __state = ProbeBoss ? new MusicProbe() : null;

        private static void Postfix(ref Task<uint> __result, MusicProbe? __state)
        {
            if (CommandLineHelper.GetValue("minimal-suite") != "preview") __result = Observe(__result, __state);
        }
        private static async Task<uint> Observe(Task<uint> original, MusicProbe? music)
        {
            uint checksum;
            try { checksum = await original; }
            finally { music?.Finish(); }
            Harness.Require(!Replay.ModelOnly && NRun.Instance is not null && NCombatRoom.Instance is not null
                && !TestMode.IsOn, "production model replay returned with native UI and normal execution mode");
            _attachments++;
            if (!FuzzDriver.Active)
                Harness.Require(OrbViewsMatch(), "late orb slots and model identities match native queue");
            File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"model-adapter.{Harness.Role}.json"), JsonSerializer.Serialize(new
            {
                role = Harness.Role, pid = System.Environment.ProcessId, model_actions = _actions,
                attachments = _attachments, native_test_mode_used = false, checksum,
                target_sha256 = Harness.Snapshot(), production = true, harness_behavior_overrides = false
            }));
            return checksum;
        }
    }
}
