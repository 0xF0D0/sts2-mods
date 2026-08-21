using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Orbs;

namespace UndoSync;

/// <summary>
/// Second line of defense against a visual-layer exception aborting a gameplay action.
///
/// <see cref="UiRefresh.SyncOrbNodes"/> is the actual fix for the model/node desync this
/// guards against — it rebuilds <c>NOrbManager</c>'s node list from the restored
/// <c>OrbQueue</c> right after every restore (see its own doc comment in UiRefresh.cs), so
/// the two structures should not actually drift apart in normal play. This class exists only
/// for the case where they do anyway — a code path this mod hasn't found yet, or a future
/// game update that reaches <c>NOrbManager</c> through some other route. Without a guard here,
/// that kind of desync throws inside <c>NOrbManager</c> and takes the whole gameplay action
/// down with it (see below). With this guard, the same desync degrades to a visual glitch
/// plus a loud log line instead of a dead run.
///
/// <para>
/// <b>Why suppressing the exception here is safe, not just convenient:</b> both call sites
/// that reach into <c>NOrbManager</c> from gameplay code mutate the model FIRST and only then
/// make the visual call that can throw — and everything that was supposed to run after the
/// visual call (the actual gameplay effect) never runs today, because the exception
/// propagates out of <c>NOrbManager</c>, through <c>OrbCmd</c>, through
/// <c>CardModel.OnPlayWrapper</c> (CardModel.cs:1858, called unguarded from
/// PlayCardAction.cs:103) and kills <c>PlayCardAction</c> — the card and its energy are
/// already spent by the time that happens, and the effect itself never lands. Suppressing the
/// visual exception restores that lost gameplay rather than skipping any:
/// </para>
///
/// <code>
/// // OrbCmd.Evoke, OrbCmd.cs:136-143 (private static async Task Evoke(...), OrbCmd.cs:125-153)
/// bool removed = false;
/// if (dequeue)
/// {
///     removed = orbQueue.Remove(evokedOrb);                                          // model mutated FIRST
///     NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.EvokeOrbAnim(evokedOrb); // visual — throws here today
/// }
/// choiceContext.PushModel(evokedOrb);
/// IEnumerable&lt;Creature&gt; targets = await evokedOrb.Evoke(choiceContext);         // real gameplay, AFTER the visual call
/// // ...and further down (OrbCmd.cs:147,150), also after the visual call:
/// //   await Hook.AfterOrbEvoked(choiceContext, player.Creature.CombatState, evokedOrb, targets);
/// //   if (removed) evokedOrb.RemoveInternal();
/// </code>
///
/// <code>
/// // OrbCmd.Channel, OrbCmd.cs:84-89 (public static async Task Channel(...), OrbCmd.cs:68-92)
/// if (await player.PlayerCombatState.OrbQueue.TryEnqueue(orb))                       // model mutated FIRST
/// {
///     CombatManager.Instance.History.OrbChanneled(combatState, orb);
///     orb.PlayChannelSfx();
///     NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager?.AddOrbAnim(); // visual — throws here today
///     await Hook.AfterOrbChanneled(combatState, choiceContext, player, orb);          // real gameplay, AFTER
/// }
/// </code>
///
/// <para>
/// There is also no model-divergence risk to argue away here: every method this class guards
/// only touches <c>NOrbManager</c>'s own node-side state — <c>_orbs</c> (NOrbManager.cs:124),
/// <c>_orbContainer</c> (:122), <c>_curTween</c> (:138), the <c>NOrb</c> child nodes
/// themselves, and <c>_creatureNode.Hitbox</c> (focus handling, e.g. :212/:277/:301). None of
/// them write to the combat model, so suppressing a throw partway through one can never leave
/// gameplay state (as opposed to node state) half-mutated. <c>TweenLayout</c> reads
/// <c>Player.PlayerCombatState.OrbQueue.Capacity</c> (NOrbManager.cs:308) to size its layout
/// loop, but that is a read, not a write — it cannot desync the model either.
/// </para>
///
/// <para>
/// <b>Logging:</b> every suppression is logged loudly, with the guarded method's name and the
/// full exception (<see cref="Exception.ToString"/>, not just <see cref="Exception.Message"/>,
/// so the stack trace survives) — see <see cref="OnException"/>. A swallowed desync that logs
/// nothing would be worse than the crash it replaces, because the next one becomes invisible;
/// this log line is what keeps that from happening.
/// </para>
/// </summary>
internal static class OrbVisualGuard
{
    /// <summary>Same purpose/pattern as <see cref="UiRefresh.UiRefreshFailureCount"/> — a plain
    /// counter incremented from <see cref="OnException"/> (the source of truth) so a harness
    /// (e.g. UndoFuzz.cs) can assert a guard actually fired without scraping the log.</summary>
    internal static int SuppressedCount;

    /// <summary>
    /// Shared finalizer body for every patch below. HarmonyX runs a finalizer on every call to
    /// the patched method regardless of outcome, passing <c>null</c> for <c>__exception</c> on
    /// the (overwhelmingly common) success path — the null check makes this a no-op then.
    ///
    /// When there IS an exception: logs it in full and increments <see cref="SuppressedCount"/>,
    /// then returns <c>null</c> to suppress it (see the class doc comment above for why that is
    /// safe here). Returning <c>null</c> from a HarmonyX finalizer clears the exception so the
    /// original call returns normally instead of throwing; returning a non-null
    /// <see cref="Exception"/> would replace it and let it propagate — never done here.
    /// </summary>
    private static Exception? OnException(string method, Exception? exception)
    {
        if (exception == null) return exception; // nothing to suppress — pass the (null) value through unchanged

        SuppressedCount++;
        Log.Write(
            $"[OrbVisualGuard] SUPPRESSED exception in NOrbManager.{method} (guard #{SuppressedCount}) — " +
            "a visual-layer failure here would otherwise abort the gameplay action that triggered it " +
            "(card/energy already spent, effect lost); see OrbVisualGuard's class doc comment for why " +
            $"swallowing it is safe. Full exception:{Environment.NewLine}{exception}");
        return null;
    }

    // Six guarded methods, verified against decompiled/MegaCrit/sts2/Core/Nodes/Orbs/NOrbManager.cs.
    // HarmonyPatch targets private methods by string name the same way this mod's other patches
    // already do (see ChecksumHook.cs's PatchRunManagerInitializeShared/PatchUndoSyncInput) —
    // no reflection needed at the patch site since HarmonyX resolves NonPublic members itself.

    /// <summary>Guards <c>NOrbManager.RemoveSlotAnim(int)</c> — public, NOrbManager.cs:199.</summary>
    [HarmonyPatch(typeof(NOrbManager), "RemoveSlotAnim")]
    public static class PatchNOrbManagerRemoveSlotAnim
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("RemoveSlotAnim", __exception);
    }

    /// <summary>Guards <c>NOrbManager.AddSlotAnim(int)</c> — public, NOrbManager.cs:219.</summary>
    [HarmonyPatch(typeof(NOrbManager), "AddSlotAnim")]
    public static class PatchNOrbManagerAddSlotAnim
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("AddSlotAnim", __exception);
    }

    /// <summary>Guards <c>NOrbManager.AddOrbAnim()</c> — public, NOrbManager.cs:244.</summary>
    [HarmonyPatch(typeof(NOrbManager), "AddOrbAnim")]
    public static class PatchNOrbManagerAddOrbAnim
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("AddOrbAnim", __exception);
    }

    /// <summary>Guards <c>NOrbManager.EvokeOrbAnim(OrbModel)</c> — public, NOrbManager.cs:264.</summary>
    [HarmonyPatch(typeof(NOrbManager), "EvokeOrbAnim")]
    public static class PatchNOrbManagerEvokeOrbAnim
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("EvokeOrbAnim", __exception);
    }

    /// <summary>Guards <c>NOrbManager.UpdateControllerNavigation()</c> — private, NOrbManager.cs:283.</summary>
    [HarmonyPatch(typeof(NOrbManager), "UpdateControllerNavigation")]
    public static class PatchNOrbManagerUpdateControllerNavigation
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("UpdateControllerNavigation", __exception);
    }

    /// <summary>Guards <c>NOrbManager.TweenLayout()</c> — private, NOrbManager.cs:306.</summary>
    [HarmonyPatch(typeof(NOrbManager), "TweenLayout")]
    public static class PatchNOrbManagerTweenLayout
    {
        [HarmonyFinalizer]
        public static Exception? Finalizer(Exception __exception) => OnException("TweenLayout", __exception);
    }
}
