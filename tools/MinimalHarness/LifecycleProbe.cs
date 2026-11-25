using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using UndoSync;
namespace MinimalHarness;

// Observers never delay or replace native work. The driver requests Undo through
// its normal production API after observing a genuinely suspended native Draw.
internal static class LifecycleProbe
{
    internal static bool Active => CommandLineHelper.GetValue("minimal-suite") == "lifecycle";
    private static bool Baseline => CommandLineHelper.GetValue("minimal-expect") == "baseline";
    private static bool Enemy => CommandLineHelper.GetValue("minimal-lifecycle-phase") == "enemy";
    private static bool _armed, _seen;
    private static int _lateViews, _liveDetach, _checks;
    private static Task? _draw;
    private static Player Me => LocalContext.GetMe(Harness.State)!;
    private static Task CurrentLoop => (Task)AccessTools.PropertyGetter(typeof(CombatManager), "DebugOnlyCurrentTurnLoopTask").Invoke(CombatManager.Instance, null)!;

    [HarmonyPatch(typeof(CardPileCmd), "DrawInternal")]
    private static class DrawObserver
    {
        private static void Postfix(Player player, bool fromHandDraw, Task<IEnumerable<CardModel>> __result)
        {
            if (!Active || Enemy || !_armed || Network.Executing || !fromHandDraw || player.NetId != Me.NetId
                || player.PlayerCombatState!.TurnNumber < 2 || __result.IsCompleted) return;
            _draw = __result; _seen = true;
            Harness.Log($"LIFECYCLE_DRAW_PENDING turn={player.PlayerCombatState.TurnNumber} hand={PileType.Hand.GetPile(player).Cards.Count}");
        }
    }
    [HarmonyPatch(typeof(Creature), nameof(Creature.TakeTurn))]
    private static class EnemyObserver
    {
        private static void Postfix(Creature __instance, Task __result)
        {
            if (!Active || !Enemy || !_armed || Network.Executing || !__instance.IsEnemy || __result.IsCompleted) return;
            _draw=__result;_seen=true;
            Harness.Log("LIFECYCLE_ENEMY_PENDING native=true");
        }
    }
    [HarmonyPatch(typeof(CardPileCmd), "CreateCardNodeAndUpdateVisuals")]
    private static class ViewObserver
    {
        private static void Prefix()
        {
            if (Active && _armed && Network.Executing && NCombatRoom.Instance is null && CombatManager.Instance.IsInProgress)
            {
                _lateViews++;
                Harness.Log("LIFECYCLE_OLD_VIEW_ACCESS room=null combatLive=true");
            }
        }
    }
    [HarmonyPatch(typeof(NSceneContainer), nameof(NSceneContainer.SetCurrentScene))]
    private static class DetachObserver
    {
        private static void Prefix(NSceneContainer __instance, Control node)
        {
            if (!Active || !_armed || !Network.Executing || node.GetType() != typeof(Control)
                || !ReferenceEquals(__instance, NGame.Instance!.RootSceneContainer)) return;
            bool live = CombatManager.Instance.IsInProgress;
            if (live) _liveDetach++;
            Harness.Log($"LIFECYCLE_DETACH oldCombatLive={live} drawPending={_draw?.IsCompleted == false}");
        }
    }
    internal static async Task Prepare()
    {
        foreach (var p in Harness.State.Players)
        {
            var keep = p.Deck.Cards.Where(c => c.Id.Entry.StartsWith("DEFEND_")).Take(3)
                .Concat(p.Deck.Cards.Where(c => c.Id.Entry.StartsWith("STRIKE_")).Take(2)).ToHashSet();
            Harness.Require(keep.Count == 5, "FIXTURE_ERROR five native basic cards");
            await CardPileCmd.RemoveFromDeck(p.Deck.Cards.Where(c => !keep.Contains(c)).ToArray(), showPreview: false);
        }
    }
    internal static async Task Run()
    {
        await Harness.Actor("lifecycle-target", Harness.Roles[0]);
        int target = History.Count; string expected = Harness.Snapshot();
        int repeats = Baseline ? 1 : Math.Max(3, Harness.Roles.Length);
        for (int i=0; i<repeats; i++)
        {
            await Harness.Actor("lifecycle-mutate-"+i, Harness.Roles[0]);
            var outgoing = CurrentLoop;
            _armed = true; _seen = false; _draw = null;
            await Harness.Barrier("lifecycle-end-"+i);
            Harness.Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(Me, Me.PlayerCombatState!.TurnNumber));
            await Harness.Wait(() => _seen);
            await Harness.Wait(() => _draw is { IsCompleted: false } && (Enemy
                ? CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy
                : Me.PlayerCombatState?.Phase == PlayerTurnPhase.Start && PileType.Hand.GetPile(Me).Cards.Count is > 0 and < 5));
            Harness.Log($"LIFECYCLE_TRIGGER hand={PileType.Hand.GetPile(Me).Cards.Count} drawPending={_draw?.IsCompleted == false}");
            string proposer = Harness.Roles[i % Harness.Roles.Length];
            await Harness.Barrier("lifecycle-draw-"+i);
            if (Harness.Role == proposer) Network.Request(target);
            await Harness.Wait(() => Network.Voting);
            if (Harness.Role != proposer) Network.Vote(true);
            await Harness.Wait(() => !Network.Busy, 60);
            _armed = false;
            Harness.Require(Network.Status == "Undo complete", "lifecycle restore finished");
            await Harness.Idle();
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "lifecycle exact native target restored");
            await Harness.Wait(() => outgoing.IsCompleted);
            string? error = outgoing.Exception?.GetBaseException().GetType().Name;
            Harness.Log($"LIFECYCLE_OLD_TASK completed={outgoing.IsCompleted} error={error ?? "none"} lateViews={_lateViews} liveDetach={_liveDetach}");
            if (Baseline)
            {
                Harness.Require(_lateViews > 0 && error == nameof(NullReferenceException), "BASELINE_CONFIRMED old native draw accessed detached view and faulted");
                Harness.Log("LIFECYCLE_BASELINE_CONFIRMED");
                return;
            }
            Harness.Require(_lateViews == 0 && _liveDetach == 0 && error is null or nameof(OperationCanceledException) or nameof(TaskCanceledException), "old combat canceled before native view removal without unexpected task exception");
            foreach (var card in PileType.Hand.GetPile(Me).Cards)
            {
                var h = NCombatRoom.Instance!.Ui.Hand.GetCardHolder(card);
                Harness.Require(h?.CardNode?.Model == card && h.CardNode.IsVisibleInTree(), "lifecycle current native hand binding");
            }
            Harness.RecordUndo(); _checks++;
            if (DisplayServer.GetName() != "headless")
            {
                RenderingServer.ForceDraw();
                NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(Harness.DirectoryPath, $"lifecycle-{i}-{Harness.Role}.png"));
            }
            await Harness.StateBarrier("lifecycle-restored-"+i);
        }
        await Harness.Actor("lifecycle-continued", Harness.Roles[0]);
        await Harness.EndTurnAll("lifecycle-next-turn");
        if (Harness.Roles.Length > 1) await Harness.CheckConnectedUi("lifecycle-final");
        File.WriteAllText(Path.Combine(Harness.DirectoryPath,$"lifecycle-proof.{Harness.Role}.json"),JsonSerializer.Serialize(new {target,expected,checks=_checks,late_views=_lateViews,live_detach=_liveDetach,phase=Enemy?"enemy":"draw"}));
        Harness.Log("LIFECYCLE_PASS");
        await Harness.Barrier("lifecycle-terminal");
    }
}
