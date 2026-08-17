using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// Re-synchronizes the event-driven UI after StateSnapshot.Restore() writes model
/// state directly (which fires none of the events the visual nodes listen to).
/// Every section is best-effort: a failure logs and moves on, because a cosmetic
/// glitch is better than an aborted restore.
///
/// Known v1 gap: orb visuals are not rebuilt (the model is restored; the display
/// catches up on the next orb event). None of the current test characters use orbs.
/// </summary>
internal static class UiRefresh
{
    // internal UI types (not public in the game assembly)
    private static readonly Type? TPowerContainer = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPowerContainer");
    private static readonly Type? TStateDisplay = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay");
    private static readonly Type? TPotionContainer = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer");
    private static readonly Type? TPotionHolder = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder");
    private static readonly Type? TPotionNode = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotion");
    private static readonly Type? TPileButton = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");

    private static readonly FieldInfo? FStateDisplayOnCreature = AccessTools.Field(typeof(NCreature), "_stateDisplay");
    private static readonly FieldInfo? FPowerContainerOnDisplay = TStateDisplay != null ? AccessTools.Field(TStateDisplay, "_powerContainer") : null;
    private static readonly FieldInfo? FPowerNodes = TPowerContainer != null ? AccessTools.Field(TPowerContainer, "_powerNodes") : null;
    private static readonly MethodInfo? MPowerAdd = TPowerContainer != null ? AccessTools.Method(TPowerContainer, "Add") : null;

    private static readonly FieldInfo? FPotionHolders = TPotionContainer != null ? AccessTools.Field(TPotionContainer, "_holders") : null;
    private static readonly MethodInfo? MHolderAddPotion = TPotionHolder != null ? AccessTools.Method(TPotionHolder, "AddPotion") : null;
    private static readonly FieldInfo? FHolderPotionBacking = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "<Potion>k__BackingField") : null;
    private static readonly FieldInfo? FHolderDisabled = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "_disabledUntilPotionRemoved") : null;
    private static readonly FieldInfo? FHolderEmptyIcon = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "_emptyIcon") : null;
    private static readonly MethodInfo? MPotionCreate = TPotionNode != null ? AccessTools.Method(TPotionNode, "Create") : null;

    private static readonly FieldInfo? FPileButtonCount = TPileButton != null ? AccessTools.Field(TPileButton, "_currentCount") : null;
    private static readonly FieldInfo? FPileButtonLabel = TPileButton != null ? AccessTools.Field(TPileButton, "_countLabel") : null;
    private static readonly FieldInfo? FPileButtonPile = TPileButton != null ? AccessTools.Field(TPileButton, "_pile") : null;

    private static readonly FieldInfo? FHandCurrentPlay = AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    private static readonly FieldInfo? FHandMode = AccessTools.Field(typeof(NPlayerHand), "_currentMode");
    private static readonly FieldInfo? FPlayQueue = AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");

    private static readonly FieldInfo? FHolderTargetPos = AccessTools.Field(typeof(NHandCardHolder), "_targetPosition");
    private static readonly FieldInfo? FHolderPosCancel = AccessTools.Field(typeof(NHandCardHolder), "_positionCancelToken");
    private static readonly FieldInfo? FHolderTargetAngle = AccessTools.Field(typeof(NHandCardHolder), "_targetAngle");
    private static readonly FieldInfo? FHolderTargetScale = AccessTools.Field(typeof(NHandCardHolder), "_targetScale");
    private static readonly MethodInfo? MHolderAngleInstant = AccessTools.Method(typeof(NHandCardHolder), "SetAngleInstantly");
    private static readonly MethodInfo? MHolderScaleInstant = AccessTools.Method(typeof(NHandCardHolder), "SetScaleInstantly");

    private static readonly MethodInfo? MNotifyChanged = AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged");
    private static readonly FieldInfo? FTurnStarted = AccessTools.Field(typeof(CombatManager), "TurnStarted");
    private static readonly PropertyInfo? PActionsDisabled = AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled");

    internal static void RefreshAll(CombatState cs)
    {
        Section("interaction", () => ResetInteraction(cs));
        Section("hand", () => SyncLocalHand(cs));
        Section("hand snap", SnapHandHolders);
        Section("powers", () => RebuildPowerIcons(cs));
        Section("potions", () => RebuildPotionSlots(cs));
        Section("pile counters", () => SyncPileCounters(cs));
        Section("intents", () => RefreshIntents(cs));
        Section("global", () => NotifyGlobal(cs));
        Section("card visuals", DeferredCardVisualRefresh);
    }

    private static void Section(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"UiRefresh '{name}' FAILED: {ex}"); }
    }

    // ── interaction / end-turn state ──

    private static void ResetInteraction(CombatState cs)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;

        // Un-ready anyone who had pressed End Turn, through the game's own API so
        // its lock and PlayerUnendedTurn events run.
        foreach (var player in cs.Players)
            if (cm.IsPlayerReadyToEndTurn(player))
                cm.UndoReadyToEndTurn(player);

        if (PActionsDisabled?.GetValue(cm) is true)
            PActionsDisabled.SetValue(cm, false);

        var hand = NPlayerHand.Instance;
        if (hand != null)
        {
            if (FHandCurrentPlay?.GetValue(hand) is Node playNode)
            {
                FHandCurrentPlay.SetValue(hand, null);
                if (GodotObject.IsInstanceValid(playNode)) playNode.QueueFree();
            }
            // return the hand to its normal play mode
            if (FHandMode is { } modeField)
            {
                try { FHandMode.SetValue(hand, Enum.Parse(modeField.FieldType, "Play")); }
                catch { }
            }
        }

        // Drop queued card-play visuals (cards mid-flight to the discard pile).
        if (NCardPlayQueue.Instance is { } queue &&
            FPlayQueue?.GetValue(queue) is System.Collections.IList entries)
        {
            foreach (var entry in entries)
                if (entry != null && AccessTools.Field(entry.GetType(), "card")?.GetValue(entry) is Node cardNode &&
                    GodotObject.IsInstanceValid(cardNode))
                    cardNode.QueueFree();
            entries.Clear();
        }
    }

    // ── hand ──

    private static void SyncLocalHand(CombatState cs)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        var localPlayer = cs.Players.FirstOrDefault(LocalContext.IsMe);
        if (localPlayer == null) return;
        var modelHand = localPlayer.PlayerCombatState.Hand.Cards.ToList();

        var shown = new Dictionary<CardModel, NCard>();
        foreach (var holder in hand.ActiveHolders)
            if (holder.CardNode is { } nCard && nCard.Model is { } model)
                shown[model] = nCard;

        foreach (var (model, nCard) in shown)
            if (!modelHand.Contains(model))
                hand.Remove(model);

        foreach (var model in modelHand)
        {
            if (shown.ContainsKey(model)) continue;
            if (NCard.Create(model) is { } created)
                hand.Add(created);
        }

        hand.ForceRefreshCardIndices();
        Log.Write($"SyncLocalHand: model={modelHand.Count} shown={shown.Count}");
    }

    private static void SnapHandHolders()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;
        foreach (var holder in hand.ActiveHolders)
        {
            if (FHolderPosCancel?.GetValue(holder) is System.Threading.CancellationTokenSource cts)
                cts.Cancel();
            if (FHolderTargetPos?.GetValue(holder) is Vector2 pos)
                holder.Position = pos;
            if (FHolderTargetAngle?.GetValue(holder) is float angle)
                MHolderAngleInstant?.Invoke(holder, new object[] { angle });
            if (FHolderTargetScale?.GetValue(holder) is { } scale)
                MHolderScaleInstant?.Invoke(holder, new[] { scale });
        }
    }

    // ── power icons ──

    private static void RebuildPowerIcons(CombatState cs)
    {
        foreach (var creature in cs.Creatures)
        {
            var node = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (node == null) continue;
            var display = FStateDisplayOnCreature?.GetValue(node);
            var container = display != null ? FPowerContainerOnDisplay?.GetValue(display) : null;
            if (container == null) continue;

            if (FPowerNodes?.GetValue(container) is System.Collections.IList icons)
            {
                foreach (var icon in icons)
                    if (icon is Node n && GodotObject.IsInstanceValid(n))
                        n.QueueFree();
                icons.Clear();
            }
            foreach (var power in creature.Powers)
                MPowerAdd?.Invoke(container, new object[] { power });
        }
    }

    // ── potions ──

    private static void RebuildPotionSlots(CombatState cs)
    {
        if (TPotionContainer == null) return;
        var container = FindNodeOfType(NRun.Instance, TPotionContainer);
        if (container == null) return;
        var localPlayer = cs.Players.FirstOrDefault(LocalContext.IsMe);
        if (localPlayer == null) return;

        if (FPotionHolders?.GetValue(container) is not System.Collections.IList holders) return;
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i] is not Node holder) continue;

            foreach (var child in holder.GetChildren())
                if (TPotionNode != null && TPotionNode.IsInstanceOfType(child))
                    child.QueueFree();
            FHolderPotionBacking?.SetValue(holder, null);
            FHolderDisabled?.SetValue(holder, false);
            if (holder is CanvasItem ci) ci.Modulate = Colors.White;
            if (FHolderEmptyIcon?.GetValue(holder) is CanvasItem icon) icon.Modulate = Colors.White;

            var potion = i < localPlayer.PotionSlots.Count ? localPlayer.PotionSlots[i] : null;
            if (potion != null && MPotionCreate?.Invoke(null, new object?[] { potion }) is Node nPotion)
                MHolderAddPotion?.Invoke(holder, new object[] { nPotion });
        }
    }

    // ── pile counters ──

    private static void SyncPileCounters(CombatState cs)
    {
        if (TPileButton == null || NCombatRoom.Instance == null) return;
        var localPlayer = cs.Players.FirstOrDefault(LocalContext.IsMe);
        if (localPlayer == null) return;

        foreach (var button in FindNodesOfType(NCombatRoom.Instance, TPileButton))
        {
            if (FPileButtonPile?.GetValue(button) is not CardPile pile) continue;
            int count = pile.Cards.Count;
            FPileButtonCount?.SetValue(button, count);
            var label = FPileButtonLabel?.GetValue(button);
            if (label == null) continue;
            var setText = AccessTools.Method(label.GetType(), "SetTextAutoSize");
            if (setText != null) setText.Invoke(label, new object[] { count.ToString() });
            else if (label is Label plain) plain.Text = count.ToString();
        }
    }

    // ── intents / global refresh ──

    private static void RefreshIntents(CombatState cs)
    {
        foreach (var creature in cs.Creatures)
        {
            if (creature.Monster == null) continue;
            var node = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (node != null)
                _ = node.RefreshIntents();
        }
    }

    private static void NotifyGlobal(CombatState cs)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;
        MNotifyChanged?.Invoke(cm.StateTracker, new object[] { "UndoSync" });
        if (FTurnStarted?.GetValue(cm) is Delegate turnStarted)
            turnStarted.DynamicInvoke(cs);
    }

    /// <summary>
    /// Two-pass deferred card redraw: freshly created NCards are not ready inside
    /// this frame, and pile-dependent cost modifiers resolve one frame later still.
    /// </summary>
    private static void DeferredCardVisualRefresh()
    {
        var game = NGame.Instance;
        if (game == null) return;
        Callable.From(RedrawHandCards).CallDeferred();
        _ = RedrawNextFrame(game);
    }

    private static async Task RedrawNextFrame(NGame game)
    {
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        RedrawHandCards();
    }

    private static void RedrawHandCards()
    {
        try
        {
            var hand = NPlayerHand.Instance;
            if (hand == null) return;
            foreach (var holder in hand.ActiveHolders)
                holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
        }
        catch (Exception ex) { Log.Write($"RedrawHandCards ERROR: {ex.Message}"); }
    }

    // ── creature roster changes (called from StateSnapshot) ──

    internal static void ReviveCreature(CombatState cs, Creature creature)
    {
        cs.AddCreature(creature);
        NCombatRoom.Instance?.AddCreature(creature);
        Log.Write($"ReviveCreature: {creature.CombatId}");
    }

    internal static void RemoveCreature(CombatState cs, Creature creature)
    {
        var room = NCombatRoom.Instance;
        var node = room?.GetCreatureNode(creature)
                   ?? room?.RemovingCreatureNodes.FirstOrDefault(n => n.Entity == creature);
        if (node != null && room != null)
        {
            node.Visible = false;
            room.RemoveCreatureNode(node);
            node.QueueFree();
        }
        cs.RemoveCreature(creature);
        Log.Write($"RemoveCreature (post-snapshot summon): {creature.CombatId}");
    }

    // ── helpers ──

    private static Node? FindNodeOfType(Node? root, Type type)
    {
        if (root == null) return null;
        if (type.IsInstanceOfType(root)) return root;
        foreach (var child in root.GetChildren())
            if (FindNodeOfType(child, type) is { } found)
                return found;
        return null;
    }

    private static IEnumerable<Node> FindNodesOfType(Node root, Type type)
    {
        if (type.IsInstanceOfType(root)) yield return root;
        foreach (var child in root.GetChildren())
            foreach (var found in FindNodesOfType(child, type))
                yield return found;
    }
}
