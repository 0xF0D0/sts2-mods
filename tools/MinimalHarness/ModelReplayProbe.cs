using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
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

    private static async Task Play(string label, string actor, bool extra = false)
    {
        if (!ProbeOrbs && !ProbeGenerated && !ProbeTransform) { await Harness.Actor(label, actor); return; }
        await Harness.Barrier(label + "-ready");
        if (Harness.Role == actor)
        {
            string id = ProbeGenerated || ProbeTransform ? (extra ? "DEFEND_IRONCLAD" : ProbeTransform ? "PRIMAL_FORCE" : "ANGER") : (extra ? "DUALCAST" : "ZAP");
            var card = PileType.Hand.GetPile(LocalContext.GetMe(Harness.State)!).Cards.Single(c => c.Id.Entry == id);
            bool done = false;
            var executor = Harness.Manager.ActionExecutor;
            void Finished(GameAction action) { if (action is PlayCardAction play && play.OwnerId == card.Owner.NetId) done = true; }
            executor.AfterActionExecuted += Finished;
            try
            {
                var target = card.TargetType == TargetType.AnyEnemy ? CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(c => c.IsAlive) : null;
                Harness.Require(card.TryManualPlay(target), "actual native " + id + " played");
                await Harness.Wait(() => done);
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
        foreach (string role in Harness.Roles) await Play("model-prefix-" + role, role);
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
            Harness.Require(_actions > actionsBefore && !Replay.ModelOnly && !TestMode.IsOn, "actual UI-free actions ran and test execution mode restored");
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "deferred UI leaves native target state unchanged");
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

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    private static class ObserveModelAction
    {
        private static void Prefix()
        {
            if (!Replay.ModelOnly) return;
            Harness.Require(NRun.Instance is null && NCombatRoom.Instance is null && !TestMode.IsOn,
                "MODEL_ACTION_WITHOUT_COMBAT_UI production=true testMode=false");
            _actions++;
        }
    }

    [HarmonyPatch(typeof(Replay), nameof(Replay.ExecuteAsync))]
    private static class ObserveCompletion
    {
        private static void Postfix(ref Task<uint> __result) => __result = Observe(__result);
        private static async Task<uint> Observe(Task<uint> original)
        {
            uint checksum = await original;
            Harness.Require(!Replay.ModelOnly && NRun.Instance is not null && NCombatRoom.Instance is not null
                && !TestMode.IsOn, "production model replay returned with native UI and normal execution mode");
            _attachments++;
            foreach (var node in NCombatRoom.Instance!.CreatureNodes)
                if (node.Entity.IsAlive && node.Entity.Player?.PlayerCombatState?.OrbQueue is { } queue && node.OrbManager is { } orbs)
                {
                    var slots = orbs.GetNode<Control>("%Orbs").GetChildren().OfType<NOrb>().ToArray();
                    Harness.Require(slots.Length == queue.Capacity && slots.Where(n => n.Model is not null).Select(n => n.Model).SequenceEqual(queue.Orbs),
                        "late orb slots and model identities match native queue");
                }
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
