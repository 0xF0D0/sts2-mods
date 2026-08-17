using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace UndoSync;

/// <summary>
/// Deep capture/restore of mid-combat model state, written against the game's own
/// definition of "combat state": every field hashed by NetFullCombatState.FromRun()
/// (the multiplayer checksum contract), plus the non-hashed state that testing and
/// the restore-fidelity check showed matters (monster move machines, power internal
/// data, per-card mutable fields, combat history).
///
/// Design rules:
///  - Model references are PRESERVED (cards, orbs, potions keep their identity so
///    the event-driven UI stays bound); mutable state is copied back from clones
///    taken via the game's own AbstractModel.MutableClone().
///  - Everything player-scoped lives in a per-player container keyed by NetId.
///  - Each restore section is isolated; one failing section logs and moves on.
/// </summary>
internal sealed class StateSnapshot
{
    internal bool IsFailed { get; private init; }

    private static readonly StateSnapshot Failed = new() { IsFailed = true };

    // ── captured data ──

    private int _round;
    private CombatSide _side;
    private uint _nextCombatId;
    private readonly List<Creature> _escaped = new();
    private readonly List<CardModel> _combatCardList = new();
    private List<object>? _historyEntries;
    private readonly Dictionary<RunRngType, SerializableRng> _runRngs = new();

    private readonly List<CreatureCapture> _creatures = new();
    private readonly HashSet<uint> _liveCreatureIds = new();
    private readonly Dictionary<ulong, PlayerCapture> _players = new();

    // clone maps shared across players (keyed by the live object)
    private readonly Dictionary<CardModel, CardModel> _cardClones = new();
    private readonly Dictionary<OrbModel, OrbModel> _orbClones = new();
    private readonly Dictionary<PotionModel, PotionModel> _potionClones = new();
    private readonly Dictionary<RelicModel, object> _relicShadow = new();

    private sealed class CreatureCapture
    {
        public Creature Ref = null!;
        public uint CombatId;
        public int Hp, MaxHp, Block;
        public List<PowerCapture> Powers = new();
        public SerializableRng? MonsterRng;
        public MoveMachineCapture? Moves;
    }

    private sealed class PowerCapture
    {
        public PowerModel Ref = null!;
        public int Amount, AmountOnTurnStart;
        public bool SkipNextDurationTick;
        public object? InternalShadow; // MemberwiseClone of _internalData
    }

    private sealed class MoveMachineCapture
    {
        public string CurrentStateId = "";
        public bool PerformedFirstMove;
        public List<object> StateLog = new();
        public Dictionary<string, bool> MovePerformedOnce = new();
        public object? NextMove;
    }

    private sealed class PlayerCapture
    {
        public int Energy, Stars, Gold;
        public PlayerTurnPhase Phase;
        public Dictionary<PileType, List<CardModel>> Piles = new();
        public List<OrbModel> Orbs = new();
        public int OrbCapacity;
        public bool HasOrbQueue;
        public List<uint> PetIds = new();
        public List<PotionModel?> PotionSlots = new();
        public List<RelicCapture> Relics = new();
    }

    private sealed class RelicCapture
    {
        public RelicModel Ref = null!;
        public int StackCount;
        public bool IsWax, IsMelted;
        public object? Status;
        public object? DynamicVarsClone;
    }

    // ── reflection handles (game facts; validated by tools/SurfaceCheck) ──

    private static readonly FieldInfo? FCreatureHp = AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo? FCreatureMaxHp = AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo? FCreatureBlock = AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo? FCreaturePowers = AccessTools.Field(typeof(Creature), "_powers");

    private static readonly FieldInfo? FPowerAmount = AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly FieldInfo? FPowerAmountTurnStart = AccessTools.Field(typeof(PowerModel), "_amountOnTurnStart");
    private static readonly FieldInfo? FPowerSkipTick = AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    private static readonly FieldInfo? FPowerInternal = AccessTools.Field(typeof(PowerModel), "_internalData");

    private static readonly FieldInfo? FMonsterRng = AccessTools.Field(typeof(MonsterModel), "_rng");
    private static readonly PropertyInfo? PMonsterNextMove = AccessTools.Property(typeof(MonsterModel), "NextMove");

    private static readonly Type? TMoveMachine =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
    private static readonly FieldInfo? FSmCurrentState = TMoveMachine != null ? AccessTools.Field(TMoveMachine, "_currentState") : null;
    private static readonly FieldInfo? FSmPerformedFirst = TMoveMachine != null ? AccessTools.Field(TMoveMachine, "_performedFirstMove") : null;
    private static readonly MethodInfo? MSmForceState = TMoveMachine != null ? AccessTools.Method(TMoveMachine, "ForceCurrentState") : null;

    private static readonly Type? TMoveState =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
    private static readonly FieldInfo? FMovePerformedOnce = TMoveState != null ? AccessTools.Field(TMoveState, "_performedAtLeastOnce") : null;

    private static readonly FieldInfo? FPcsEnergy = AccessTools.Field(typeof(PlayerCombatState), "_energy");
    private static readonly FieldInfo? FPcsStars = AccessTools.Field(typeof(PlayerCombatState), "_stars");
    private static readonly FieldInfo? FPcsPets = AccessTools.Field(typeof(PlayerCombatState), "_pets");
    private static readonly FieldInfo? FPlayerGold = AccessTools.Field(typeof(Player), "_gold");
    private static readonly FieldInfo? FPlayerPotionSlots = AccessTools.Field(typeof(Player), "_potionSlots");

    private static readonly FieldInfo? FPileCards = AccessTools.Field(typeof(CardPile), "_cards");
    private static readonly FieldInfo? FOrbQueueOrbs = AccessTools.Field(typeof(OrbQueue), "_orbs");
    private static readonly PropertyInfo? POrbQueueCapacity = AccessTools.Property(typeof(OrbQueue), "Capacity");

    private static readonly FieldInfo? FRelicStack = AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    private static readonly PropertyInfo? PRelicStatus = AccessTools.Property(typeof(RelicModel), "Status");
    private static readonly FieldInfo? FRelicDynVars = AccessTools.Field(typeof(RelicModel), "_dynamicVars");

    private static readonly FieldInfo? FPotionOwner = AccessTools.Field(typeof(PotionModel), "_owner");
    private static readonly PropertyInfo? PPotionRemoved = AccessTools.Property(typeof(PotionModel), "HasBeenRemovedFromState");

    private static readonly FieldInfo? FCombatNextId = AccessTools.Field(typeof(CombatState), "_nextCreatureId");
    private static readonly FieldInfo? FCombatEscaped = AccessTools.Field(typeof(CombatState), "_escapedCreatures");
    private static readonly FieldInfo? FCombatAllCards = AccessTools.Field(typeof(CombatState), "_allCards");

    private static readonly FieldInfo? FHistoryEntries =
        AccessTools.Field(typeof(CombatHistory), "_entries");

    private static readonly FieldInfo? FRunRngs = AccessTools.Field(typeof(RunRngSet), "_rngs");

    private static readonly MethodInfo MShadowClone =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // ── generic mutable-field copier ──

    // Identity/ownership/plumbing fields that must never be copied back onto a live
    // object. Delegate-typed fields (event subscriber lists) are skipped automatically.
    private static readonly HashSet<string> CopySkip = new()
    {
        "_owner", "_cloneOf", "_canonicalInstance", "_deckVersion",
    };

    private static void CopyMutableFields(object from, object to)
    {
        for (var t = from.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsInitOnly) continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                if (CopySkip.Contains(f.Name)) continue;
                f.SetValue(to, f.GetValue(from));
            }
        }
    }

    private static object? Shadow(object? source) =>
        source == null ? null : MShadowClone.Invoke(source, null);

    // ── capture ──

    internal static StateSnapshot? Capture()
    {
        try
        {
            var cs = UndoSyncMod.GetCombatState();
            if (cs == null) return null;

            var snap = new StateSnapshot
            {
                _round = cs.RoundNumber,
                _side = cs.CurrentSide,
            };

            if (FCombatNextId?.GetValue(cs) is uint nextId) snap._nextCombatId = nextId;
            snap._escaped.AddRange(cs.EscapedCreatures);
            if (FCombatAllCards?.GetValue(cs) is List<CardModel> all) snap._combatCardList.AddRange(all);
            if (CombatManager.Instance?.History != null &&
                FHistoryEntries?.GetValue(CombatManager.Instance.History) is System.Collections.IList entries)
                snap._historyEntries = entries.Cast<object>().ToList();

            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState != null && FRunRngs?.GetValue(runState.Rng) is Dictionary<RunRngType, Rng> rngs)
                foreach (var (type, rng) in rngs)
                    snap._runRngs[type] = rng.ToSerializable();

            foreach (var creature in cs.Creatures)
                snap.CaptureCreature(creature);

            foreach (var ally in cs.Allies)
                if (ally.Player is { } player)
                    snap.CapturePlayer(player);

            Log.Write($"Capture: round={snap._round} side={snap._side} creatures={snap._creatures.Count} " +
                      $"players=[{string.Join(", ", snap._players.Select(kv => $"{kv.Key}:{DescribePlayer(kv.Value)}"))}] " +
                      $"history={snap._historyEntries?.Count ?? 0} combatCards={snap._combatCardList.Count}");
            return snap;
        }
        catch (Exception ex)
        {
            Log.Write($"Capture FAILED: {ex}");
            return Failed;
        }
    }

    private static string DescribePlayer(PlayerCapture p) =>
        $"e{p.Energy}/g{p.Gold}/{string.Join("+", p.Piles.Select(kv => kv.Value.Count))}";

    private void CaptureCreature(Creature creature)
    {
        if (creature.CombatId is not { } combatId) return;
        _liveCreatureIds.Add(combatId);

        var cap = new CreatureCapture
        {
            Ref = creature,
            CombatId = combatId,
            Hp = (int)FCreatureHp!.GetValue(creature)!,
            MaxHp = (int)FCreatureMaxHp!.GetValue(creature)!,
            Block = (int)FCreatureBlock!.GetValue(creature)!,
        };

        foreach (var power in creature.Powers)
        {
            cap.Powers.Add(new PowerCapture
            {
                Ref = power,
                Amount = (int)FPowerAmount!.GetValue(power)!,
                AmountOnTurnStart = (int)FPowerAmountTurnStart!.GetValue(power)!,
                SkipNextDurationTick = (bool)FPowerSkipTick!.GetValue(power)!,
                InternalShadow = Shadow(FPowerInternal?.GetValue(power)),
            });
        }

        if (creature.Monster is { } monster)
        {
            if (FMonsterRng?.GetValue(monster) is Rng rng)
                cap.MonsterRng = rng.ToSerializable();
            cap.Moves = CaptureMoves(monster);
        }

        _creatures.Add(cap);
    }

    private static MoveMachineCapture? CaptureMoves(MonsterModel monster)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return null;

        var cap = new MoveMachineCapture
        {
            PerformedFirstMove = FSmPerformedFirst?.GetValue(sm) is true,
            NextMove = PMonsterNextMove?.GetValue(monster),
        };
        if (FSmCurrentState?.GetValue(sm) is { } current)
            cap.CurrentStateId = StateIdOf(current) ?? "";
        cap.StateLog.AddRange(sm.StateLog);
        foreach (var (key, state) in sm.States)
            if (TMoveState != null && TMoveState.IsInstanceOfType(state) && FMovePerformedOnce != null)
                cap.MovePerformedOnce[key] = FMovePerformedOnce.GetValue(state) is true;
        return cap;
    }

    private static string? StateIdOf(object state) =>
        AccessTools.Property(state.GetType(), "Id")?.GetValue(state) as string;

    private void CapturePlayer(Player player)
    {
        var pcs = player.PlayerCombatState;
        var cap = new PlayerCapture
        {
            Energy = (int)FPcsEnergy!.GetValue(pcs)!,
            Stars = (int)FPcsStars!.GetValue(pcs)!,
            Gold = (int)FPlayerGold!.GetValue(player)!,
            Phase = pcs.Phase,
        };

        foreach (var pile in pcs.AllPiles)
        {
            cap.Piles[pile.Type] = pile.Cards.ToList();
            foreach (var card in pile.Cards)
                _cardClones[card] = (CardModel)card.MutableClone();
        }

        if (pcs.OrbQueue is { } orbs)
        {
            cap.HasOrbQueue = true;
            cap.OrbCapacity = orbs.Capacity;
            foreach (var orb in orbs.Orbs)
            {
                cap.Orbs.Add(orb);
                _orbClones[orb] = (OrbModel)orb.MutableClone();
            }
        }

        if (FPcsPets?.GetValue(pcs) is System.Collections.IList pets)
            foreach (var pet in pets)
                if (pet is Creature { CombatId: { } id })
                    cap.PetIds.Add(id);

        foreach (var slot in player.PotionSlots)
        {
            cap.PotionSlots.Add(slot);
            if (slot != null && !_potionClones.ContainsKey(slot))
                _potionClones[slot] = (PotionModel)slot.MutableClone();
        }

        foreach (var relic in player.Relics)
        {
            object? dynVars = null;
            try { dynVars = relic.DynamicVars?.Clone(relic); } catch { }
            cap.Relics.Add(new RelicCapture
            {
                Ref = relic,
                StackCount = relic.StackCount,
                IsWax = relic.IsWax,
                IsMelted = relic.IsMelted,
                Status = PRelicStatus?.GetValue(relic),
                DynamicVarsClone = dynVars,
            });
            try { _relicShadow[relic] = Shadow(relic)!; } catch { }
        }

        _players[player.NetId] = cap;
    }

    // ── restore ──

    internal void Restore()
    {
        var cs = UndoSyncMod.GetCombatState();
        if (cs == null)
        {
            Log.Write("Restore: no combat state");
            return;
        }

        Try("creatures", () => RestoreCreatures(cs));
        Try("players", () => RestorePlayers(cs));
        Try("run rng", RestoreRunRng);
        Try("history", RestoreHistory);
        Try("combat lists", () => RestoreCombatLists(cs));
        Log.Write("Restore complete");
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"Restore section '{what}' FAILED: {ex}"); }
    }

    private void RestoreCreatures(CombatState cs)
    {
        var live = cs.Creatures.ToDictionary(c => c.CombatId ?? uint.MaxValue);

        foreach (var cap in _creatures)
        {
            // A creature killed after the snapshot is gone from the roster; put the
            // saved reference back on its side so the model matches the snapshot.
            if (!live.ContainsKey(cap.CombatId))
            {
                Try($"revive {cap.CombatId}", () => UiRefresh.ReviveCreature(cs, cap.Ref));
            }

            FCreatureHp!.SetValue(cap.Ref, cap.Hp);
            FCreatureMaxHp!.SetValue(cap.Ref, cap.MaxHp);
            FCreatureBlock!.SetValue(cap.Ref, cap.Block);

            if (FCreaturePowers?.GetValue(cap.Ref) is List<PowerModel> powers)
            {
                powers.Clear();
                foreach (var p in cap.Powers)
                {
                    FPowerAmount!.SetValue(p.Ref, p.Amount);
                    FPowerAmountTurnStart!.SetValue(p.Ref, p.AmountOnTurnStart);
                    FPowerSkipTick!.SetValue(p.Ref, p.SkipNextDurationTick);
                    // re-shadow so repeated restores from this snapshot stay pristine
                    FPowerInternal?.SetValue(p.Ref, Shadow(p.InternalShadow));
                    powers.Add(p.Ref);
                }
            }

            if (cap.Ref.Monster is { } monster)
            {
                if (cap.MonsterRng != null && FMonsterRng?.GetValue(monster) is Rng rng)
                    rng.LoadFromSerializable(cap.MonsterRng);
                if (cap.Moves != null)
                    RestoreMoves(monster, cap.Moves);
            }
        }

        // Creatures that exist now but were not in the snapshot were summoned after
        // it was taken — drop them from the model and the scene.
        foreach (var creature in cs.Creatures.ToList())
            if (creature.CombatId is { } id && !_liveCreatureIds.Contains(id))
                Try($"remove summoned {id}", () => UiRefresh.RemoveCreature(cs, creature));
    }

    private static void RestoreMoves(MonsterModel monster, MoveMachineCapture cap)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        if (cap.CurrentStateId.Length > 0 && sm.States.TryGetValue(cap.CurrentStateId, out var target))
            MSmForceState?.Invoke(sm, new object[] { target });
        FSmPerformedFirst?.SetValue(sm, cap.PerformedFirstMove);

        sm.StateLog.Clear();
        foreach (var entry in cap.StateLog)
            if (entry is MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState ms)
                sm.StateLog.Add(ms);

        foreach (var (key, performed) in cap.MovePerformedOnce)
            if (sm.States.TryGetValue(key, out var state) && FMovePerformedOnce != null)
                FMovePerformedOnce.SetValue(state, performed);

        if (cap.NextMove != null)
            PMonsterNextMove?.SetValue(monster, cap.NextMove);
    }

    private void RestorePlayers(CombatState cs)
    {
        var tracker = CombatManager.Instance?.StateTracker;

        foreach (var ally in cs.Allies)
        {
            if (ally.Player is not { } player) continue;
            if (!_players.TryGetValue(player.NetId, out var cap))
            {
                Log.Write($"Restore: no capture for player {player.NetId}");
                continue;
            }

            var pcs = player.PlayerCombatState;
            FPcsEnergy!.SetValue(pcs, cap.Energy);
            FPcsStars!.SetValue(pcs, cap.Stars);
            FPlayerGold!.SetValue(player, cap.Gold);
            pcs.Phase = cap.Phase;

            RestorePiles(pcs, cap, tracker);
            Try("card fields", () => RestoreCardFields(pcs));
            Try("orbs", () => RestoreOrbQueue(pcs, cap));
            Try("pets", () => RestorePets(pcs, cs, cap));
            Try("potions", () => RestorePotionSlots(player, cap));
            Try("relics", () => RestoreRelicStates(cap));
        }
    }

    private void RestorePiles(PlayerCombatState pcs, PlayerCapture cap, CombatStateTracker? tracker)
    {
        var before = pcs.AllPiles.SelectMany(p => p.Cards).ToHashSet();

        foreach (var pile in pcs.AllPiles)
        {
            if (!cap.Piles.TryGetValue(pile.Type, out var saved)) continue;
            if (FPileCards?.GetValue(pile) is not List<CardModel> cards) continue;
            cards.Clear();
            cards.AddRange(saved);
            pile.InvokeContentsChanged();
        }

        // The game's CardPile.AddInternal/RemoveInternal keep the state tracker's
        // per-card subscriptions in sync; direct list writes bypass that, so diff
        // and fix them up through the tracker's public API.
        if (tracker == null) return;
        var after = pcs.AllPiles.SelectMany(p => p.Cards).ToHashSet();
        foreach (var gone in before.Except(after)) tracker.Unsubscribe(gone);
        foreach (var added in after.Except(before)) tracker.Subscribe(added);
    }

    private void RestoreCardFields(PlayerCombatState pcs)
    {
        int restored = 0;
        foreach (var card in pcs.AllCards)
        {
            if (!_cardClones.TryGetValue(card, out var clone)) continue;
            CopyMutableFields(clone, card);
            restored++;
        }
        Log.Write($"RestoreCardFields: {restored} cards");
    }

    private void RestoreOrbQueue(PlayerCombatState pcs, PlayerCapture cap)
    {
        if (!cap.HasOrbQueue || pcs.OrbQueue is not { } queue) return;
        if (FOrbQueueOrbs?.GetValue(queue) is not List<OrbModel> orbs) return;

        orbs.Clear();
        foreach (var orb in cap.Orbs)
        {
            if (_orbClones.TryGetValue(orb, out var clone))
                CopyMutableFields(clone, orb);
            orbs.Add(orb);
        }
        POrbQueueCapacity?.SetValue(queue, cap.OrbCapacity);
    }

    private static void RestorePets(PlayerCombatState pcs, CombatState cs, PlayerCapture cap)
    {
        if (FPcsPets?.GetValue(pcs) is not System.Collections.IList pets) return;
        pets.Clear();
        foreach (var id in cap.PetIds)
            foreach (var creature in cs.Creatures)
                if (creature.CombatId == id)
                {
                    pets.Add(creature);
                    break;
                }
    }

    private void RestorePotionSlots(Player player, PlayerCapture cap)
    {
        if (FPlayerPotionSlots?.GetValue(player) is not List<PotionModel?> slots) return;
        for (int i = 0; i < slots.Count && i < cap.PotionSlots.Count; i++)
        {
            var potion = cap.PotionSlots[i];
            slots[i] = potion;
            if (potion == null) continue;
            if (_potionClones.TryGetValue(potion, out var clone))
                CopyMutableFields(clone, potion);
            FPotionOwner?.SetValue(potion, player);
            PPotionRemoved?.SetValue(potion, false);
        }
    }

    private void RestoreRelicStates(PlayerCapture cap)
    {
        foreach (var r in cap.Relics)
        {
            FRelicStack?.SetValue(r.Ref, r.StackCount);
            r.Ref.IsWax = r.IsWax;
            r.Ref.IsMelted = r.IsMelted;
            if (r.Status != null) PRelicStatus?.SetValue(r.Ref, r.Status);
            if (r.DynamicVarsClone != null) FRelicDynVars?.SetValue(r.Ref, r.DynamicVarsClone);
            // subclass-private per-turn counters (e.g. cards-played trackers)
            if (_relicShadow.TryGetValue(r.Ref, out var shadow))
                CopyMutableFields(shadow, r.Ref);
        }
    }

    private void RestoreRunRng()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null || FRunRngs?.GetValue(runState.Rng) is not Dictionary<RunRngType, Rng> rngs) return;
        foreach (var (type, serialized) in _runRngs)
        {
            if (rngs.TryGetValue(type, out var rng))
                rng.LoadFromSerializable(serialized);
            else
                rngs[type] = new Rng(serialized);
        }
    }

    private void RestoreHistory()
    {
        if (_historyEntries == null || CombatManager.Instance?.History is not { } history) return;
        if (FHistoryEntries?.GetValue(history) is not System.Collections.IList entries) return;
        int old = entries.Count;
        entries.Clear();
        foreach (var e in _historyEntries) entries.Add(e);
        Log.Write($"RestoreHistory: {old} -> {entries.Count}");
    }

    private void RestoreCombatLists(CombatState cs)
    {
        cs.RoundNumber = _round;
        FCombatNextId?.SetValue(cs, _nextCombatId);
        if (FCombatEscaped?.GetValue(cs) is List<Creature> escaped)
        {
            escaped.Clear();
            escaped.AddRange(_escaped);
        }
        if (FCombatAllCards?.GetValue(cs) is List<CardModel> all)
        {
            all.Clear();
            all.AddRange(_combatCardList);
        }
    }
}
