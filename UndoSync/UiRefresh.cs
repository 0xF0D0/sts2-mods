using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// Re-synchronizes the event-driven UI after StateSnapshot.Restore() writes model
/// state directly (which fires none of the events the visual nodes listen to).
/// Every section is best-effort: a failure logs and moves on, because a cosmetic
/// glitch is better than an aborted restore.
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
    // Backs ReviveVisuals' health-bar re-show: AnimateIn(HealthBarAnimMode) is public
    // (NCreatureStateDisplay.cs:331), but the display itself is only reachable through the
    // private NCreature._stateDisplay field above, so resolving the method also goes
    // through the TStateDisplay reflection handle for consistency with that lookup.
    private static readonly MethodInfo? MStateDisplayAnimateIn = TStateDisplay != null ? AccessTools.Method(TStateDisplay, "AnimateIn") : null;

    private static readonly FieldInfo? FPotionHolders = TPotionContainer != null ? AccessTools.Field(TPotionContainer, "_holders") : null;
    private static readonly MethodInfo? MHolderAddPotion = TPotionHolder != null ? AccessTools.Method(TPotionHolder, "AddPotion") : null;
    private static readonly FieldInfo? FHolderPotionBacking = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "<Potion>k__BackingField") : null;
    private static readonly FieldInfo? FHolderDisabled = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "_disabledUntilPotionRemoved") : null;
    private static readonly FieldInfo? FHolderEmptyIcon = TPotionHolder != null ? AccessTools.Field(TPotionHolder, "_emptyIcon") : null;
    private static readonly MethodInfo? MPotionCreate = TPotionNode != null ? AccessTools.Method(TPotionNode, "Create") : null;
    // The container's own placement logic (NPotionContainer.cs:295-311) — going through
    // it instead of re-deriving the -30/-30 offset and holder lookup here means a rebuild
    // can't drift from what the game itself does when a potion is picked up.
    private static readonly MethodInfo? MContainerAdd = TPotionContainer != null ? AccessTools.Method(TPotionContainer, "Add", new[] { typeof(PotionModel), typeof(bool) }) : null;

    private static readonly FieldInfo? FRoomCreatureNodes = AccessTools.Field(typeof(NCombatRoom), "_creatureNodes");
    private static readonly FieldInfo? FRoomRemovingCreatureNodes = AccessTools.Field(typeof(NCombatRoom), "_removingCreatureNodes");

    // Used by RepositionEnemies to replay NCombatRoom's own enemy-layout step after a
    // revive (see that method for why). All private game members, verified against
    // decompiled/MegaCrit/sts2/Core/Nodes/Rooms/NCombatRoom.cs.
    private static readonly PropertyInfo? PRoomEncounterSlots = AccessTools.Property(typeof(NCombatRoom), "EncounterSlots"); // private, Control?, :302
    private static readonly MethodInfo? MRoomPositionWithSlots = AccessTools.Method(typeof(NCombatRoom), "PositionCreaturesWithSlots"); // private, List<NCreature>, :503
    private static readonly MethodInfo? MRoomPositionEnemies = AccessTools.Method(typeof(NCombatRoom), "PositionEnemies"); // private, (List<NCreature>, float), :510
    private static readonly FieldInfo? FRoomVisuals = AccessTools.Field(typeof(NCombatRoom), "_visuals"); // private ICombatRoomVisuals, :264

    // Backs CaptureVisualTweak/ApplyVisualTweak: the per-node scale/hue variation
    // NCombatRoom.RandomizeEnemyScalesAndHues rolls onto NCreatureVisuals at combat
    // setup (NCombatRoom.cs:865-897). _hue is private with no public getter
    // (NCreatureVisuals.cs:188), so reading it back requires reflection even though
    // applying it goes through the public NCreature.SetScaleAndHue.
    private static readonly FieldInfo? FVisualsHue = AccessTools.Field(typeof(NCreatureVisuals), "_hue");

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

    // Backs ReviveVisuals: Creature.Revived (public event Action<Creature>?, Creature.cs:345)
    // has no public raise/invoke from outside the declaring class, so firing it for a
    // creature this restore brought back from 0 HP needs the backing delegate field —
    // same pattern as FTurnStarted above.
    private static readonly FieldInfo? FCreatureRevived = AccessTools.Field(typeof(Creature), "Revived");

    // Backs FireCreatureValueChanged: BlockChanged/CurrentHpChanged/MaxHpChanged (public
    // event Action<int,int>?, Creature.cs:329-345) are the same story as Revived above —
    // no public raise from outside Creature, so replaying them for StateSnapshot's direct
    // _currentHp/_maxHp/_block writes needs the backing delegate fields.
    private static readonly FieldInfo? FCreatureBlockChanged = AccessTools.Field(typeof(Creature), "BlockChanged");
    private static readonly FieldInfo? FCreatureCurrentHpChanged = AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    private static readonly FieldInfo? FCreatureMaxHpChanged = AccessTools.Field(typeof(Creature), "MaxHpChanged");

    // Backs DropOrphanedCardVfx: NSovereignBladeVfx is the per-forge-card VFX node ForgeCmd
    // parents directly onto the player's NCreature (ForgeCmd.PlayCombatRoomForgeVfx,
    // ForgeCmd.cs:108-122) and finds again by CARD IDENTITY (SovereignBlade.GetVfxNode,
    // SovereignBlade.cs:221-225). Both the type and its Card getter (NSovereignBladeVfx.cs:18,372)
    // are resolved once through reflection, same as the other optional handles above, so a game
    // version without this class makes the whole section a no-op instead of a build break.
    private static readonly Type? TBladeVfx = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Vfx.NSovereignBladeVfx");
    private static readonly PropertyInfo? PBladeVfxCard = TBladeVfx != null ? AccessTools.Property(TBladeVfx, "Card") : null;

    // Backs SyncOrbNodes: NOrbManager's node-per-slot list, the container they're parented
    // to, and the tween that last animated their layout are all private fields
    // (NOrbManager.cs:122-138), and TweenLayout/UpdateControllerNavigation — the game's own
    // post-mutation layout/focus step, which every node-list mutator in that class calls
    // (AddOrbAnim/EvokeOrbAnim, NOrbManager.cs:244-282) — are private methods too. Same
    // reflection-access pattern as the power/potion sections above.
    private static readonly FieldInfo? FOrbMgrOrbs = AccessTools.Field(typeof(NOrbManager), "_orbs"); // private List<NOrb>, NOrbManager.cs:124
    private static readonly FieldInfo? FOrbMgrContainer = AccessTools.Field(typeof(NOrbManager), "_orbContainer"); // private Control, NOrbManager.cs:122
    private static readonly FieldInfo? FOrbMgrCurTween = AccessTools.Field(typeof(NOrbManager), "_curTween"); // private Tween?, NOrbManager.cs:138
    private static readonly MethodInfo? MOrbMgrTweenLayout = AccessTools.Method(typeof(NOrbManager), "TweenLayout"); // private, no args, NOrbManager.cs:306
    private static readonly MethodInfo? MOrbMgrUpdateNav = AccessTools.Method(typeof(NOrbManager), "UpdateControllerNavigation"); // private, no args, NOrbManager.cs:283

    internal static void RefreshAll(CombatState cs)
    {
        Section("interaction", () => ResetInteraction(cs));
        Section("hand", () => SyncLocalHand(cs));
        Section("hand snap", SnapHandHolders);
        Section("powers", () => RebuildPowerIcons(cs));
        Section("orbs", () => SyncOrbNodes(cs));
        Section("potions", () => RebuildPotionSlots(cs));
        Section("pile counters", () => SyncPileCounters(cs));
        Section("intents", () => RefreshIntents(cs));
        Section("global", () => NotifyGlobal(cs));
        Section("card visuals", DeferredCardVisualRefresh);
        Section("orphaned blade vfx", () => DropOrphanedCardVfx(cs));
    }

    /// <summary>Same purpose as StateSnapshot.RestoreSectionFailureCount — a counter incremented from
    /// this catch block (the source of truth) so the headless fuzzer (UndoFuzz.cs) can notice a
    /// silently-swallowed UI-refresh failure without re-reading the log file. Dormant/unused outside
    /// the fuzzer.</summary>
    internal static int UiRefreshFailureCount;
    internal static string LastFailedUiRefreshSection = "";

    /// <summary>Counts each PLAYER SyncOrbNodes actually rebuilt a node list for — incremented once
    /// per loop iteration inside SyncOrbNodes, only after it has confirmed a non-null NOrbManager AND
    /// finished rebuilding that player's node list (see the increment's own call site for exactly
    /// where). Same purpose/pattern as UiRefreshFailureCount above: a plain counter the UI-mode fuzzer
    /// (UndoFuzz.cs's --undosync-uitest path) reads back by delta, so a claim like "SyncOrbNodes was
    /// actually exercised against real nodes" is backed by a number instead of an
    /// ObserveOrbManagerPresence() sample taken at a completely different point in the combat — a real
    /// NOrbManager existing once is not proof this method ever ran against it. Dormant/unused outside
    /// the fuzzer.</summary>
    internal static int SyncOrbNodesRebuiltCount;

    private static void Section(string name, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            UiRefreshFailureCount++;
            LastFailedUiRefreshSection = name;
            Log.Write($"UiRefresh '{name}' FAILED: {ex}");
        }
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

    // ── orbs ──

    /// <summary>
    /// Rebuilds NOrbManager's node list from the restored OrbQueue model, for every player
    /// with an orb manager.
    ///
    /// NOrbManager keeps one NOrb node PER SLOT, not per orb (_orbs, NOrbManager.cs:124):
    /// filled slots occupy indices 0..orbCount-1 in channel order, and an empty slot is a
    /// node whose Model is null. The game maintains that invariant itself — AddOrbAnim
    /// inserts a newly channeled orb's node at the first empty slot's index
    /// (NOrbManager.cs:244-260), and EvokeOrbAnim removes the matching node and appends a
    /// fresh empty one (NOrbManager.cs:264-280) — but StateSnapshot.Restore() writes
    /// OrbQueue._orbs and Capacity directly, so none of that node bookkeeping runs and the
    /// node list is left stale relative to the restored model. Two ways that surfaces:
    ///   (A) EvokeOrbAnim does `_orbs.Last(node => node.Model == orb)` (NOrbManager.cs:265) —
    ///       a reference-identity match against OrbModel. If no node holds the restored
    ///       model instance, that Linq call throws InvalidOperationException.
    ///   (B) TweenLayout reads `Player.PlayerCombatState.OrbQueue.Capacity` and then indexes
    ///       `_orbs[i]` for i &lt; capacity (NOrbManager.cs:306-327). If the restored capacity
    ///       is larger than the stale node list's length, that throws
    ///       ArgumentOutOfRangeException.
    /// Both are reachable from PlayCardAction — e.g. a power like StormPower channels an orb
    /// via Hook.AfterCardPlayed on any card played at all, which reaches TweenLayout through
    /// AddOrbAnim — so the card and its energy are already spent before the throw; the
    /// effect itself never lands.
    ///
    /// This does not diff the old node list against the new model state; it always tears the
    /// whole thing down and rebuilds it from scratch. That covers every case (orb count
    /// growing or shrinking, capacity growing or shrinking, sitting at max capacity) with no
    /// per-case branching, because the rebuild only ever has to make the node list equal the
    /// model, never patch it. The new nodes are built directly from the LIVE (post-restore)
    /// OrbModel instances, so node/model identity is restored by construction — exactly what
    /// EvokeOrbAnim's reference match in (A) needs — and the rebuilt list's length always
    /// equals Capacity by construction — exactly what TweenLayout's indexing in (B) needs.
    /// It is deterministic on every peer: it reads only restored model state
    /// (OrbQueue.Capacity/.Orbs) and LocalContext.IsMe, nothing transient.
    /// </summary>
    private static void SyncOrbNodes(CombatState cs)
    {
        foreach (var player in cs.Players)
        {
            var queue = player.PlayerCombatState?.OrbQueue;
            if (queue == null) continue;

            // Null here is the normal case for a character without orbs, and for every
            // creature in a headless TestMode run (NCreature.Create returns null there,
            // NCreature.cs:450-455) — not an error, so skip silently.
            var mgr = NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager;
            if (mgr == null) continue;

            int capacity = queue.Capacity;
            var models = queue.Orbs;

            if (FOrbMgrOrbs?.GetValue(mgr) is not List<NOrb> list ||
                FOrbMgrContainer?.GetValue(mgr) is not Control container)
            {
                Log.Write($"SyncOrbNodes: missing reflection handle(s) for player={player.NetId}, skipping");
                continue;
            }

            // Kill the running layout tween BEFORE freeing any node below — it still targets
            // the pre-restore nodes. TweenLayout kills _curTween too (NOrbManager.cs:311),
            // but only once this method calls it further down; freeing a node the old tween
            // is still animating first would reintroduce the same kind of stale-reference
            // crash this method exists to fix.
            if (FOrbMgrCurTween?.GetValue(mgr) is Tween tween)
            {
                tween.Kill();
                FOrbMgrCurTween.SetValue(mgr, null);
            }

            foreach (var node in list)
            {
                container.RemoveChildSafely(node);
                node.QueueFreeSafely();
            }
            list.Clear();

            // One fresh node per slot, handed the LIVE restored model straight away (or null
            // for a slot past the orb count) — see the method doc comment above for why that
            // preserves model identity and fixes both (A) and (B).
            bool isLocal = LocalContext.IsMe(player);
            for (int i = 0; i < capacity; i++)
            {
                var model = i < models.Count ? models[i] : null;
                var orbNode = NOrb.Create(isLocal, model);
                container.AddChildSafely(orbNode);
                list.Add(orbNode);
                orbNode.Position = Vector2.Zero;
            }

            // TweenLayout indexes _orbs[i] for i < capacity (NOrbManager.cs:306-327), which is
            // only safe now that the list's length equals capacity — that's the fix for (B).
            // UpdateControllerNavigation re-links controller focus neighbors across the
            // rebuilt list (NOrbManager.cs:283-303), and UpdateVisuals repaints every node's
            // passive/evoke labels from its (possibly now different) model.
            MOrbMgrTweenLayout?.Invoke(mgr, null);
            MOrbMgrUpdateNav?.Invoke(mgr, null);
            mgr.UpdateVisuals(OrbEvokeType.None);

            // This player's node list is now fully rebuilt against a confirmed non-null manager —
            // see SyncOrbNodesRebuiltCount's own doc comment for why this, not just observing `mgr`
            // non-null somewhere else, is what the UI-mode fuzzer needs to prove this method ran.
            SyncOrbNodesRebuiltCount++;

            Log.Write($"SyncOrbNodes: player={player.NetId} capacity={capacity} orbs={models.Count} ids=[{string.Join(",", models.Select(m => m.Id.Entry))}]");
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

        // Clear every holder first. The container's own Add() (below) resolves its
        // target holder by scanning PotionSlots for the potion, so a stale NPotion
        // still sitting in a holder from before the restore would confuse that lookup.
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
        }

        string path = MContainerAdd != null ? "container.Add" : "manual";
        var mapping = new List<string>();
        for (int i = 0; i < localPlayer.PotionSlots.Count; i++)
        {
            var potion = localPlayer.PotionSlots[i];
            mapping.Add($"{i}:{potion?.Id.Entry ?? "-"}");
            if (potion == null) continue;

            if (MContainerAdd != null)
            {
                // isInitialization: true skips the FTUE popup check; the holder itself
                // is resolved internally via potion.Owner.PotionSlots.IndexOf(potion).
                MContainerAdd.Invoke(container, new object[] { potion, true });
                continue;
            }

            // Fallback for when the container's Add couldn't be resolved: replicate its
            // placement by hand (NPotionContainer.cs:301-311), including the -30/-30
            // position it gives every freshly created NPotion before AddPotion repositions it.
            if (i >= holders.Count || holders[i] is not Node fallbackHolder) continue;
            if (MPotionCreate?.Invoke(null, new object?[] { potion }) is Control nPotion)
            {
                nPotion.Position = new Vector2(-30f, -30f);
                MHolderAddPotion?.Invoke(fallbackHolder, new object[] { nPotion });
            }
        }
        Log.Write($"RebuildPotionSlots: container=found holders={holders.Count} path={path} slots=[{string.Join(",", mapping)}]");
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

    /// <summary>
    /// Drops Sovereign Blade VFX left attached to a player after a restore removed the card it
    /// belongs to. ForgeCmd.PlayCombatRoomForgeVfx (ForgeCmd.cs:108-122) parents one NSovereignBladeVfx
    /// per blade to the player's NCreature and finds it again by CARD IDENTITY
    /// (SovereignBlade.GetVfxNode, SovereignBlade.cs:221-225). Undoing a forge removes the blade card
    /// from the piles but not its VFX, so the next forge — which correctly creates a NEW blade card —
    /// misses the identity lookup and attaches a second sword; they pile up one per undo+forge cycle.
    /// Nothing here is checksummed, so restore fidelity cannot catch it.
    /// </summary>
    private static void DropOrphanedCardVfx(CombatState cs)
    {
        if (TBladeVfx == null || PBladeVfxCard == null) return;
        if (NCombatRoom.Instance == null) return;

        int dropped = 0;
        foreach (var creature in cs.Allies)
        {
            var player = creature.Player;
            if (player?.PlayerCombatState == null) continue;

            var node = NCombatRoom.Instance.GetCreatureNode(creature);
            if (node == null) continue;

            // Materialized once per player: the live piles the game itself checks
            // (SovereignBlade.GetVfxNode does the same `== originalCard` reference lookup).
            var liveCards = player.PlayerCombatState.AllCards.ToList();

            foreach (var child in node.GetChildren())
            {
                if (!TBladeVfx.IsInstanceOfType(child)) continue;

                var card = PBladeVfxCard.GetValue(child) as CardModel;
                if (card != null && liveCards.Contains(card)) continue; // still in play — keep it

                child.QueueFreeSafely();
                dropped++;
            }
        }

        if (dropped > 0)
            Log.Write($"DropOrphanedCardVfx: dropped {dropped} stale blade vfx");
    }

    // ── creature roster changes (called from StateSnapshot) ──

    /// <summary>
    /// The per-node scale/hue NCombatRoom.RandomizeEnemyScalesAndHues rolls onto a creature's
    /// visuals at combat setup (NCombatRoom.cs:865-897) — see CaptureVisualTweak for why this
    /// has to be captured off the live node instead of recomputed.
    /// </summary>
    internal readonly record struct CreatureVisualTweak(float Scale, float Hue);

    /// <summary>
    /// Reads the scale/hue NCombatRoom.RandomizeEnemyScalesAndHues (NCombatRoom.cs:865-897)
    /// rolled onto this creature's node at combat setup, so a later revive can restore it
    /// instead of losing it. That method only runs for monster types with 2+ copies in the
    /// room, and per node it calls the PUBLIC NCreature.SetScaleAndHue(scale, hue)
    /// (NCreature.cs:1083) with hue = Rng.Chaotic.NextFloat(0.05f) — a throwaway RNG value that
    /// exists only on the node (NCreatureVisuals._hue, initialized to 1f at :188 and written by
    /// nothing else in the game), so it can only be read back off the node, not recomputed.
    /// Reads Visuals.DefaultScale, not Visuals.Scale: NCreature.ScaleTo (NCreature.cs:1089)
    /// tweens Scale off DefaultScale as its base, so DefaultScale is the persistent per-node
    /// value and Scale may be a transient animation state.
    ///
    /// Returns null when the node/handle is missing, or when the node was never randomized.
    /// The "never randomized" test is hue >= 0.5f: the untouched _hue initializer is 1f, while
    /// RandomizeEnemyScalesAndHues always assigns a value in [0, 0.05). This guard matters —
    /// calling SetScaleAndHue(1f, 1f) on an untouched node would install the HSV shader
    /// material and tint a creature that was never tinted (the guard inside
    /// NCreatureVisuals.SetScaleAndHue is !Mathf.IsEqualApprox(hue, 0f), NCreatureVisuals.cs:338).
    /// </summary>
    internal static CreatureVisualTweak? CaptureVisualTweak(Creature creature)
    {
        try
        {
            var node = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (node == null || FVisualsHue == null) return null;
            if (FVisualsHue.GetValue(node.Visuals) is not float hue) return null;
            if (hue >= 0.5f) return null; // untouched initializer (NCreatureVisuals.cs:188) — never randomized

            return new CreatureVisualTweak(node.Visuals.DefaultScale, hue);
        }
        catch (Exception ex)
        {
            Log.Write($"CaptureVisualTweak ERROR: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-applies a tweak captured by CaptureVisualTweak, through the same public
    /// NCreature.SetScaleAndHue the game itself uses to roll it in the first place
    /// (NCreature.cs:1083). Never throws — a missing node here is a cosmetic miss, not
    /// worth failing the revive over.
    /// </summary>
    internal static void ApplyVisualTweak(Creature creature, CreatureVisualTweak tweak)
    {
        try { NCombatRoom.Instance?.GetCreatureNode(creature)?.SetScaleAndHue(tweak.Scale, tweak.Hue); }
        catch (Exception ex) { Log.Write($"ApplyVisualTweak ERROR: {ex.Message}"); }
    }

    /// <summary>
    /// Replays the creature-value change events the restore skipped. StateSnapshot writes
    /// _currentHp/_maxHp/_block through reflection, so none of the game's event-driven HP
    /// widgets ever hear about it — NTopBarHp (NTopBarHp.cs:89) and NMultiplayerPlayerState
    /// (NMultiplayerPlayerState.cs:426) both track CurrentHpChanged and would otherwise keep
    /// showing pre-restore numbers. Firing the game's own events lets every subscriber update
    /// itself, instead of this mod hunting down each widget. Never throws — same posture as
    /// every other UiRefresh section, a stale widget is better than an aborted restore.
    /// </summary>
    internal static void FireCreatureValueChanged(Creature creature, int oldHp, int newHp, int oldMaxHp, int newMaxHp, int oldBlock, int newBlock)
    {
        try
        {
            if (oldHp != newHp && FCreatureCurrentHpChanged?.GetValue(creature) is Delegate hpChanged)
                hpChanged.DynamicInvoke(oldHp, newHp);
            if (oldMaxHp != newMaxHp && FCreatureMaxHpChanged?.GetValue(creature) is Delegate maxHpChanged)
                maxHpChanged.DynamicInvoke(oldMaxHp, newMaxHp);
            if (oldBlock != newBlock && FCreatureBlockChanged?.GetValue(creature) is Delegate blockChanged)
                blockChanged.DynamicInvoke(oldBlock, newBlock);
        }
        catch (Exception ex)
        {
            Log.Write($"FireCreatureValueChanged ERROR: {ex.Message}");
        }
    }

    internal static void ReviveCreature(CombatState cs, Creature creature)
    {
        // Death removal unattaches the creature (CombatState.RemoveCreature sets
        // CombatState = null) and AddCreature refuses anything not attached to it, so
        // re-attach first — the same back-reference AttachCreature sets. CombatId
        // survives removal untouched, so nothing else has to be rebuilt.
        if (!ReferenceEquals(creature.CombatState, cs))
            creature.CombatState = cs;

        // A node from the pre-undo timeline can still be around (death animation in
        // flight), and AddCreature always makes a fresh one — drop the old one so the
        // revived creature has exactly one visual.
        PurgeCreatureNodes(creature);

        cs.AddCreature(creature);
        NCombatRoom.Instance?.AddCreature(creature);
        Log.Write($"ReviveCreature: {creature.CombatId}");
    }

    /// <summary>
    /// Brings a creature that this restore brought back from 0 HP out of its death state.
    /// Only needed for creatures still IN the roster — a monster dropped from combat gets an
    /// entirely new node from ReviveCreature/NCombatRoom.AddCreature, but a dead PLAYER keeps
    /// its node (CombatManager.HandlePlayerDeath leaves players in the roster) and that node
    /// stays collapsed. NCreature.StartReviveAnim (NCreature.cs:969-985) is the game's own
    /// undo of the death pose: it fires the "Revive" spine trigger (or AnimTempRevive for
    /// players), calls AnimEnableUi() to bring the local player's combat UI back, and restores
    /// the hitbox. The Revived event is fired separately because NCreatureStateDisplay
    /// subscribes to it (OnCreatureRevived, NCreatureStateDisplay.cs:387) to restore the HP
    /// bar's hitbox MouseFilter — and this snapshot writes _currentHp directly, so
    /// Creature.HealInternal never runs to fire it (Creature.cs:478-486). OnCreatureRevived
    /// does NOT re-show the bar itself (the death visuals hid it), which is what the explicit
    /// AnimateIn(FromHidden) call below is for.
    /// </summary>
    internal static void ReviveVisuals(Creature creature)
    {
        try
        {
            var node = NCombatRoom.Instance?.GetCreatureNode(creature);
            node?.StartReviveAnim();
            if (FCreatureRevived?.GetValue(creature) is Delegate revived)
                revived.DynamicInvoke(creature);

            // StartReviveAnim() undoes the death POSE, but the death visuals also hid the
            // HP bar entirely, and OnCreatureRevived (NCreatureStateDisplay.cs:387), which
            // just ran via the Revived event above, only restores the hitbox MouseFilter —
            // it never re-shows the bar. AnimateIn(FromHidden) is the game's own fast
            // (0.15s) fade-back-in for exactly this "bar already exists, was hidden" case
            // (NCreatureStateDisplay.cs:331-350; HealthBarAnimMode.FromHidden doc comment).
            if (node != null && FStateDisplayOnCreature?.GetValue(node) is { } display)
                MStateDisplayAnimateIn?.Invoke(display, new object[] { HealthBarAnimMode.FromHidden });

            Log.Write($"ReviveVisuals: {creature.CombatId}");
        }
        catch (Exception ex)
        {
            Log.Write($"ReviveVisuals ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops any lingering NCreature node for this creature from NCombatRoom's two
    /// backing lists (NCombatRoom.cs:243-245) before AddCreature builds a fresh one.
    /// Never throws — a leftover visual is a cosmetic glitch, not worth failing the revive.
    /// </summary>
    private static void PurgeCreatureNodes(Creature creature)
    {
        var room = NCombatRoom.Instance;
        if (room == null) return;

        PurgeFromList(FRoomCreatureNodes?.GetValue(room), creature);
        PurgeFromList(FRoomRemovingCreatureNodes?.GetValue(room), creature);
    }

    private static void PurgeFromList(object? listObj, Creature creature)
    {
        if (listObj is not System.Collections.IList list) return;

        var toRemove = new List<NCreature>();
        foreach (var entry in list)
            if (entry is NCreature node && node.Entity == creature)
                toRemove.Add(node);

        foreach (var node in toRemove)
        {
            list.Remove(node);
            if (GodotObject.IsInstanceValid(node))
                node.QueueFreeSafely(); // NCreature is pooled; plain QueueFree would break the pool (NCreature.cs:1064).
        }
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

    /// <summary>
    /// Replays NCombatRoom's own enemy-layout step (CreateEnemyNodes,
    /// NCombatRoom.cs:475-501) for the current enemy roster. AddCreature only
    /// positions a freshly created node from a slot marker when the creature has a
    /// SlotName (NCombatRoom.cs:722-745); slot-less encounters instead get their
    /// layout from PositionEnemies, which spreads the WHOLE enemy list by total width
    /// and normally runs once at combat setup, never again. AddCreature also always
    /// appends the new node to _creatureNodes/_enemyContainer, so a revived enemy
    /// would otherwise render last left-to-right — this call re-lays-out the row (and
    /// only the row) so a revived enemy lands back where it belongs.
    /// </summary>
    internal static void RepositionEnemies(CombatState cs)
    {
        var room = NCombatRoom.Instance;
        if (room == null) return;

        if (PRoomEncounterSlots == null || MRoomPositionWithSlots == null || MRoomPositionEnemies == null || FRoomVisuals == null)
        {
            Log.Write("RepositionEnemies: missing reflection handle(s)");
            return;
        }

        // Model order, not node-creation order: AddCreature appends, so a revived
        // enemy would otherwise be laid out last. cs.Enemies was just put back in
        // captured order by StateSnapshot.RestoreCreatureOrder.
        var nodes = cs.Enemies
            .Select(room.GetCreatureNode)
            .Where(n => n != null)
            .Select(n => n!)
            .ToList();
        if (nodes.Count == 0) return;

        // Deliberately NOT calling NCombatRoom.RandomizeEnemyScalesAndHues() here: it
        // re-randomizes scale/hue for every duplicate-type enemy in the room
        // (NCombatRoom.cs:865-890), which would visibly change enemies that never
        // died. The revived node's own scale/hue variation is not lost, though — it is
        // restored from the snapshot (StateSnapshot.CreatureCapture.VisualTweak, filled
        // by UiRefresh.CaptureVisualTweak/ApplyVisualTweak) instead of being re-rolled.
        if (PRoomEncounterSlots.GetValue(room) != null)
        {
            MRoomPositionWithSlots.Invoke(room, new object[] { nodes });
        }
        else
        {
            float scaling = FRoomVisuals.GetValue(room) is ICombatRoomVisuals visuals
                ? visuals.Encounter.GetCameraScaling()
                : 1f;
            MRoomPositionEnemies.Invoke(room, new object[] { nodes, scaling });
        }
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
