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
using MegaCrit.Sts2.Core.Saves.Runs;

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
    // CombatState.CurrentSide is deliberately NOT captured here: ChecksumHook only stores
    // play-phase sync points, and RestoreTo always runs from an idle player play phase, so
    // CurrentSide is CombatSide.Player at both capture time and restore time.
    private uint _nextCombatId;
    private readonly List<Creature> _escaped = new();
    private readonly List<CardModel> _combatCardList = new();
    private List<object>? _historyEntries;
    private readonly Dictionary<RunRngType, SerializableRng> _runRngs = new();

    // Change B1: CombatTurnState.PlayersTakingExtraTurn (CombatTurnState.cs:68, exposed
    // read-only via CombatManager.PlayersTakingExtraTurn, CombatManager.cs:123-136) — captured
    // by NetId (PlayerCapture.PetIds pattern) since it's combat-scoped, not per-player. See
    // RestoreTurnCoordination for why this is the one CombatTurnState field that actually
    // varies within a play phase.
    private readonly List<ulong> _playersTakingExtraTurn = new();

    private readonly List<CreatureCapture> _creatures = new();
    private readonly HashSet<uint> _liveCreatureIds = new();
    private readonly Dictionary<ulong, PlayerCapture> _players = new();

    // clone maps shared across players (keyed by the live object)
    private readonly Dictionary<CardModel, CardModel> _cardClones = new();
    private readonly Dictionary<OrbModel, OrbModel> _orbClones = new();
    private readonly Dictionary<PotionModel, PotionModel> _potionClones = new();
    // Independent DynamicVarSet.Clone(this) per potion — see FPotionDynVars for why
    // _potionClones above isn't enough on its own.
    private readonly Dictionary<PotionModel, object> _potionDynamicVars = new();
    private readonly Dictionary<RelicModel, object> _relicShadow = new();

    private sealed class CreatureCapture
    {
        public Creature Ref = null!;
        public uint CombatId;
        public int Hp, MaxHp, Block;
        // Read by gameplay, not just the health bar: HellraiserPower checks
        // HpDisplay.IsInfinite() (HellraiserPower.cs:45) and HardenedShellPower flips it on
        // apply/remove (HardenedShellPower.cs:66,75), so it changes mid-combat.
        public HpDisplay HpDisplay;
        public List<PowerCapture> Powers = new();
        public SerializableRng? MonsterRng;
        public MoveMachineCapture? Moves;

        // Per-turn mutable state (MonsterModel.SpawnedThisTurn, set true on summon at
        // MonsterModel.cs:413) that combat can flip mid-fight; not captured elsewhere.
        // (Creature.MonsterMaxHpBeforeModification is NOT captured: its only writer is
        // creature creation, Creature.cs:383, never during combat, and this snapshot
        // preserves creature object identity — the field is never stale to restore.)
        public bool SpawnedThisTurn;

        // The one piece of VIEW state this snapshot has to carry: everything else about
        // a creature is rebuilt from the model, but NCombatRoom.RandomizeEnemyScalesAndHues
        // rolls this hue from a throwaway RNG straight onto the visual node (see
        // UiRefresh.CaptureVisualTweak), and that value dies with the node if it isn't
        // captured here and replayed onto whatever node a revive builds.
        public UiRefresh.CreatureVisualTweak? VisualTweak;
    }

    private sealed class PowerCapture
    {
        public PowerModel Ref = null!;
        public int Amount, AmountOnTurnStart;
        public bool SkipNextDurationTick;
        public object? InternalShadow; // MemberwiseClone of _internalData
        public object? DynamicVarsClone; // DynamicVarSet.Clone(this) — same pattern as RelicCapture.DynamicVarsClone
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
        // The only path back to true after a death is Creature.HealInternal crossing
        // dead->alive (Creature.cs:478-486); this snapshot writes _currentHp directly
        // (RestoreCreatures pass 1) and bypasses that entirely, so without this a revived
        // player stays invisible to CombatState.IterateHookListeners for the rest of
        // combat. Not part of the checksum, so peers can't catch a dropped restore here.
        public bool IsActiveForHooks;
        public PlayerTurnPhase Phase;
        public int TurnNumber;
        public Dictionary<PileType, List<CardModel>> Piles = new();
        public List<OrbModel> Orbs = new();
        public int OrbCapacity;
        public bool HasOrbQueue;
        public List<uint> PetIds = new();
        public List<PotionModel?> PotionSlots = new();
        public List<RelicCapture> Relics = new();
        // List membership/order, as opposed to per-relic state (captured above). Insurance,
        // not an observed bug — see RestoreRelicList.
        public List<RelicModel> RelicOrder = new();

        // Part of the multiplayer checksum contract (NetFullCombatState.FromRun,
        // NetFullCombatState.cs:434/436/437) but invisible to peer checksum comparison:
        // every peer captures/restores (or omits) these identically, so a mismatch here
        // never diverges peers — it only shows up as a silently wrong local restore.
        // ToSerializable() can throw on a mid-mutation model; null means "not captured",
        // and the restore side skips rather than stomping live state with garbage.
        public int MaxPotionCount;
        public SerializablePlayerRngSet? PlayerRng;
        public SerializableRelicGrabBag? RelicGrabBag;
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
    private static readonly FieldInfo? FPowerDynVars = AccessTools.Field(typeof(PowerModel), "_dynamicVars");

    private static readonly FieldInfo? FMonsterRng = AccessTools.Field(typeof(MonsterModel), "_rng");
    private static readonly PropertyInfo? PMonsterNextMove = AccessTools.Property(typeof(MonsterModel), "NextMove");
    // SpawnedThisTurn's setter is `private set` and calls AssertMutable() (MonsterModel.cs:248-258),
    // so — same as every other checksummed field in this file with no usable public setter
    // (FRelicStack, FPcsTurnNumber, ...) — restore goes straight at the backing field.
    private static readonly FieldInfo? FMonsterSpawnedThisTurn = AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");

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
    // TurnNumber is `public int TurnNumber { get; private set; } = 1;` — auto-property,
    // so the only setter is the backing field (there's no public setter, only IncrementTurnNumber()).
    private static readonly FieldInfo? FPcsTurnNumber = AccessTools.Field(typeof(PlayerCombatState), "<TurnNumber>k__BackingField");
    private static readonly FieldInfo? FPcsPets = AccessTools.Field(typeof(PlayerCombatState), "_pets");
    private static readonly FieldInfo? FPlayerGold = AccessTools.Field(typeof(Player), "_gold");
    // IsActiveForHooks has no public setter (private set, Player.cs:112) — the only way
    // back to true is Creature.HealInternal (see PlayerCapture.IsActiveForHooks), which
    // this snapshot's direct HP write bypasses. Same auto-property backing field pattern
    // as FRelicStack/FPcsTurnNumber below.
    private static readonly FieldInfo? FPlayerIsActiveForHooks = AccessTools.Field(typeof(Player), "<IsActiveForHooks>k__BackingField");
    private static readonly FieldInfo? FPlayerPotionSlots = AccessTools.Field(typeof(Player), "_potionSlots");

    private static readonly FieldInfo? FPileCards = AccessTools.Field(typeof(CardPile), "_cards");
    private static readonly FieldInfo? FOrbQueueOrbs = AccessTools.Field(typeof(OrbQueue), "_orbs");
    private static readonly PropertyInfo? POrbQueueCapacity = AccessTools.Property(typeof(OrbQueue), "Capacity");

    private static readonly FieldInfo? FRelicStack = AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    private static readonly PropertyInfo? PRelicStatus = AccessTools.Property(typeof(RelicModel), "Status");
    private static readonly FieldInfo? FRelicDynVars = AccessTools.Field(typeof(RelicModel), "_dynamicVars");

    private static readonly FieldInfo? FPotionOwner = AccessTools.Field(typeof(PotionModel), "_owner");
    private static readonly PropertyInfo? PPotionRemoved = AccessTools.Property(typeof(PotionModel), "HasBeenRemovedFromState");
    // PotionModel has no DeepCloneFields override (unlike PowerModel/RelicModel), so
    // MutableClone() shares _dynamicVars by reference with the live potion instead of
    // deep-cloning it (PotionModel.cs:134-144 lazily builds it once from CanonicalVars,
    // then never reassigns the field). Closing a hole, not fixing an observed bug — current
    // content only reads potion DynamicVars — but PowerModel/RelicModel both already clone
    // theirs independently, and this was the gap. Written back through this handle.
    private static readonly FieldInfo? FPotionDynVars = AccessTools.Field(typeof(PotionModel), "_dynamicVars");

    private static readonly FieldInfo? FCombatNextId = AccessTools.Field(typeof(CombatState), "_nextCreatureId");
    private static readonly FieldInfo? FCombatEscaped = AccessTools.Field(typeof(CombatState), "_escapedCreatures");
    private static readonly FieldInfo? FCombatAllCards = AccessTools.Field(typeof(CombatState), "_allCards");
    private static readonly FieldInfo? FCombatAllies = AccessTools.Field(typeof(CombatState), "_allies");
    private static readonly FieldInfo? FCombatEnemies = AccessTools.Field(typeof(CombatState), "_enemies");

    private static readonly FieldInfo? FHistoryEntries =
        AccessTools.Field(typeof(CombatHistory), "_entries");

    private static readonly FieldInfo? FRunRngs = AccessTools.Field(typeof(RunRngSet), "_rngs");

    // Change B: CombatManager._turnState (type MegaCrit.Sts2.Core.Combat.CombatTurnState,
    // internal sealed — CombatTurnState.cs:21) is only reachable by reflection since the type
    // itself is internal to the game assembly. CombatManager already exposes read-only public
    // wrappers for PlayersTakingExtraTurn/IsEnemyTurnStarted/EndingPlayerTurnPhaseOne/
    // EndingPlayerTurnPhaseTwo (CombatManager.cs:123-153) — Capture() reads through those, no
    // reflection needed there. But CombatManager.PlayersTakingExtraTurn returns a defensive
    // .ToList() COPY (CombatManager.cs:134), and the three bools have no public setter, so
    // restoring requires reaching CombatTurnState's own properties (live List<Player>, and
    // { get; set; } bools) through this field.
    private static readonly FieldInfo? FCmTurnState = AccessTools.Field(typeof(CombatManager), "_turnState");
    private static readonly PropertyInfo? PTsPlayersTakingExtraTurn =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "PlayersTakingExtraTurn") : null; // CombatTurnState.cs:68, live List<Player>
    private static readonly PropertyInfo? PTsIsEnemyTurnStarted =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "IsEnemyTurnStarted") : null; // CombatTurnState.cs:98
    private static readonly PropertyInfo? PTsEndingPlayerTurnPhaseOne =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "EndingPlayerTurnPhaseOne") : null; // CombatTurnState.cs:100
    private static readonly PropertyInfo? PTsEndingPlayerTurnPhaseTwo =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "EndingPlayerTurnPhaseTwo") : null; // CombatTurnState.cs:102
    private static readonly PropertyInfo? PTsPlayersReadyToBeginEnemyTurn =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "PlayersReadyToBeginEnemyTurn") : null; // CombatTurnState.cs:66, live HashSet<Player>, no CombatManager wrapper

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

    /// <summary>
    /// MemberwiseClone plus an independent copy of every container-valued field, so the
    /// returned shadow doesn't share its mutable sub-objects with `source`. Plain
    /// MemberwiseClone alone copies reference-typed fields BY REFERENCE — a List/HashSet/
    /// Dictionary/array field on the "clone" is still the SAME object `source` holds, so the
    /// container keeps mutating right along with the live object and is never actually
    /// snapshotted. Confirmed victim: UnsettlingLamp._doubledPowers (List&lt;PowerModel&gt;,
    /// UnsettlingLamp.cs:18) — DoubledPowers.Add(power) fires from BeforePowerAmountChanged
    /// (UnsettlingLamp.cs:107) and is cleared only at BeforeCombatStart (:66) / AfterCombatEnd
    /// (:155), so it accumulates across turns; after an undo the shadow's "captured" list has
    /// already grown past what it held at capture time, because it was never a separate list.
    /// See CopyShadowContainers for how the copy is done and ShadowContainersShared for the
    /// one case (DynamicVarSet) where it deliberately isn't.
    /// </summary>
    /// <summary>Test seam: UndoFuzz's startup self-test needs to prove Shadow's container-copy branch
    /// works without depending on a run happening to encounter a relic that owns one. Shadow itself
    /// stays private.</summary>
    internal static object? ShadowForTest(object? source) => Shadow(source);

    private static object? Shadow(object? source)
    {
        if (source == null) return null;
        var clone = MShadowClone.Invoke(source, null)!;
        CopyShadowContainers(clone);
        return clone;
    }

    /// <summary>Count of container-valued fields Shadow() gave an independent copy of — incremented
    /// once per field, from inside CopyShadowContainers' own copy path. Same purpose/pattern as
    /// UiRefresh.UiRefreshFailureCount: a plain counter a harness can assert on, so a run's log
    /// shows the container-copy mechanism actually engaged rather than being assumed to from
    /// reading the code. Dormant/unused outside a harness.</summary>
    internal static int ShadowContainersCopied;

    /// <summary>Count of container-valued fields Shadow() found but could NOT copy — the field's
    /// runtime type has no (T value) copy constructor, or that constructor threw, so the field is
    /// left aliasing the live object's container exactly as plain MemberwiseClone would have left
    /// it, and CopyShadowContainers logs a one-line warning naming the declaring type/field/field
    /// type. DynamicVarSet (DynamicVarSet.cs:12) is the expected occupant of this bucket: it
    /// implements IReadOnlyDictionary&lt;string, DynamicVar&gt; but its only constructor takes
    /// IEnumerable&lt;DynamicVar&gt;, not a DynamicVarSet, so Activator.CreateInstance never finds a
    /// matching constructor. That's fine — relics, potions and powers all capture their own
    /// DynamicVars independently through the game's own DynamicVars.Clone(owner) (see
    /// RelicCapture.DynamicVarsClone / PowerCapture.DynamicVarsClone / _potionDynamicVars above),
    /// so a shared _dynamicVars field on a Shadow() clone is never actually relied on.</summary>
    internal static int ShadowContainersShared;

    /// <summary>
    /// Walks `clone`'s instance fields — same traversal/filters as CopyMutableFields (concrete
    /// type up to but excluding object, Instance|Public|NonPublic|DeclaredOnly, skipping
    /// IsInitOnly and delegate-typed fields for the same reasons CopyMutableFields does) — and
    /// replaces any container-valued field with a fresh container of the SAME runtime type
    /// holding the SAME elements. CopySkip (CopyMutableFields' skip-list for _owner/_cloneOf/etc)
    /// does not apply here: this only ever rewrites fields on `clone` itself, never on a live
    /// object, so there is nothing to protect against.
    ///
    /// Element identity is deliberately preserved — this copies the CONTAINER, never the
    /// elements. A cloned List&lt;PowerModel&gt; must hold the very same PowerModel references the
    /// live list does, because those models are captured/restored independently elsewhere in
    /// this file and the snapshot's whole design depends on model identity never changing under
    /// it.
    ///
    /// Two shapes are handled, in order:
    ///  - System.Array, via Array.Clone() (shallow — same element-identity rule as above).
    ///  - anything else that is System.Collections.IEnumerable and isn't a string, via its copy
    ///    constructor (Activator.CreateInstance(type, new object[] { value })) — List&lt;T&gt;,
    ///    HashSet&lt;T&gt; and Dictionary&lt;K,V&gt; all provide one.
    /// If neither applies, or the copy constructor throws, the field is left sharing the live
    /// object's container (ShadowContainersShared) and a one-line warning is logged — this must
    /// never fail the capture.
    /// </summary>
    private static void CopyShadowContainers(object clone)
    {
        for (var t = clone.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsInitOnly) continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;

                var value = f.GetValue(clone);
                if (value is Array array)
                {
                    f.SetValue(clone, array.Clone());
                    ShadowContainersCopied++;
                    continue;
                }
                if (value is System.Collections.IEnumerable && value is not string)
                {
                    try
                    {
                        f.SetValue(clone, Activator.CreateInstance(value.GetType(), new object[] { value }));
                        ShadowContainersCopied++;
                    }
                    catch (Exception ex)
                    {
                        ShadowContainersShared++;
                        Log.Write($"Shadow: {t.Name}.{f.Name} ({value.GetType().Name}) has no usable copy constructor, leaving shared: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A throwaway copy of a stored clone, for CopyMutableFields to hand to the live model.
    /// CopyMutableFields assigns REFERENCES, so copying a stored clone in directly would leave
    /// the live model and the snapshot sharing the mutable sub-objects the game deep-clones
    /// (CardModel.DeepCloneFields: _keywords, _dynamicVars, _energyCost, _temporaryStarCosts,
    /// Enchantment, Affliction — CardModel.cs:1194-1217). The next combat mutation would then
    /// also corrupt the snapshot, so restoring to the same point twice would replay the polluted
    /// values. Re-cloning keeps the stored copy pristine — the same reason power internal data
    /// goes through Shadow() on every restore.
    /// </summary>
    private static AbstractModel Pristine(AbstractModel stored) => stored.MutableClone();

    private static readonly MethodInfo? MDeepCloneFields =
        AccessTools.Method(typeof(AbstractModel), "DeepCloneFields");

    /// <summary>
    /// CopyMutableFields (above) assigns every field BY REFERENCE, so a live model just handed a
    /// throwaway Pristine() clone's deep sub-objects also inherits those sub-objects' back-
    /// references to the throwaway. CardModel.DeepCloneFields (CardModel.cs:1194-1217) is the
    /// concrete failure: `_energyCost = _energyCost?.Clone(this)` binds CardEnergyCost._card to
    /// whatever "this" was at clone time — after CopyMutableFields(Pristine(clone), card), that's
    /// the discarded pristine clone, not the live card. CardEnergyCost.GetWithModifiers
    /// (CardEnergyCost.cs:94-121) reads that back-reference straight off _card (`if
    /// (_card.IsCanonical) return num;`, then a `_card.CombatState != null` gate before applying
    /// Hook.ModifyEnergyCostInCombat); a detached clone has CombatState == null, so every global
    /// cost modifier silently evaporates after a restore. NetFullCombatState.FromRun only writes
    /// the energyCost field when the modified cost differs from canonical
    /// (NetFullCombatState.cs:330-333), so the drop is invisible until a byte-diff catches it
    /// missing: measured on a headless fuzz run (seed widen1-24, QUEEN_BOSS, Ironclad, after
    /// CARD.CORRUPTION zeroed skill costs) as a 964-byte restore checksum against a 988-byte
    /// capture, with the field diff showing "Energy Cost: 0" lines present in the capture and gone
    /// from the restore. RelicModel.DeepCloneFields (RelicModel.cs:512-516) and
    /// PowerModel.DeepCloneFields (PowerModel.cs:582-587) both do the identical
    /// `_dynamicVars = DynamicVars.Clone(this)` rebind, so the same stale-back-reference shape
    /// exists at every CopyMutableFields call site in this file, not just cards.
    ///
    /// The fix is to run the game's OWN DeepCloneFields on the live model right after
    /// CopyMutableFields, instead of hand-rebinding _energyCost/_dynamicVars/etc. one field at a
    /// time: DeepCloneFields IS the game's "make this model's deep sub-objects belong to me" step,
    /// so it stays correct the moment the game adds another deep-cloned sub-object, where a
    /// hand-written field list would silently drift out of date — exactly the failure mode this
    /// mod keeps getting bitten by (CopyMutableFields itself exists because a hand-rolled field
    /// list drifted). Invoked via reflection on the live instance, not called directly, so virtual
    /// dispatch picks whichever override the model's runtime type defines (CardModel/RelicModel/
    /// PowerModel/EnchantmentModel/EventModel all override AbstractModel's `protected virtual void
    /// DeepCloneFields()`).
    ///
    /// Safe to call on a live in-combat model, not only on a freshly-minted clone: everything
    /// DeepCloneFields does is either a re-clone (_dynamicVars, _energyCost,
    /// _temporaryStarCosts) or a re-clone-and-rebind through EnchantInternal/AfflictInternal
    /// (CardModel.cs:1492-1499 / 1508-1520) — both preceded by nulling the field before rebinding,
    /// so neither method's "already set" InvalidOperationException guard can trigger here.
    /// </summary>
    private static void RebindDeepCloneOwnership(AbstractModel live)
    {
        if (MDeepCloneFields == null)
        {
            Log.Write("RebindDeepCloneOwnership: AbstractModel.DeepCloneFields not found, skipping");
            return;
        }
        try
        {
            MDeepCloneFields.Invoke(live, null);
        }
        catch (Exception ex)
        {
            Log.Write($"RebindDeepCloneOwnership({live.Id}) FAILED: {ex.Message}");
        }
    }

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

            // Change B1: see the field's doc comment. Read through CombatManager's own public
            // wrapper — no reflection needed for the read side.
            if (CombatManager.Instance?.PlayersTakingExtraTurn is { } extraTurnPlayers)
                foreach (var p in extraTurnPlayers)
                    snap._playersTakingExtraTurn.Add(p.NetId);

            foreach (var creature in cs.Creatures)
                snap.CaptureCreature(creature);

            foreach (var ally in cs.Allies)
                if (ally.Player is { } player)
                    snap.CapturePlayer(player);

            Log.Write($"Capture: round={snap._round} side={cs.CurrentSide} creatures={snap._creatures.Count} " +
                      $"players=[{string.Join(", ", snap._players.Select(kv => $"{kv.Key}:{DescribePlayer(kv.Value)}"))}] " +
                      $"history={snap._historyEntries?.Count ?? 0} combatCards={snap._combatCardList.Count} " +
                      $"extraTurn=[{string.Join(",", snap._playersTakingExtraTurn)}]");
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
            HpDisplay = creature.HpDisplay,
            VisualTweak = UiRefresh.CaptureVisualTweak(creature),
        };

        foreach (var power in creature.Powers)
        {
            // Same treatment as RelicCapture.DynamicVarsClone below: PowerModel._dynamicVars
            // is a mutable clone (DynamicVarSet.Clone, DynamicVarSet.cs:153) and relics
            // already captured it — powers were the gap.
            object? powerDynVars = null;
            try { powerDynVars = power.DynamicVars?.Clone(power); } catch { }
            cap.Powers.Add(new PowerCapture
            {
                Ref = power,
                Amount = (int)FPowerAmount!.GetValue(power)!,
                AmountOnTurnStart = (int)FPowerAmountTurnStart!.GetValue(power)!,
                SkipNextDurationTick = (bool)FPowerSkipTick!.GetValue(power)!,
                InternalShadow = Shadow(FPowerInternal?.GetValue(power)),
                DynamicVarsClone = powerDynVars,
            });
        }

        if (creature.Monster is { } monster)
        {
            if (FMonsterRng?.GetValue(monster) is Rng rng)
                cap.MonsterRng = rng.ToSerializable();
            cap.Moves = CaptureMoves(monster);
            cap.SpawnedThisTurn = monster.SpawnedThisTurn;
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
        // Checksummed alongside everything else below (NetFullCombatState.cs:434/436/437);
        // see the comment on PlayerCapture for why peer checksums can't catch it if these
        // three are dropped. ToSerializable() walks live model state, so — same defensive
        // posture as the relic DynamicVars clone further down — a throw here yields null
        // rather than an exception aborting the whole player capture.
        SerializablePlayerRngSet? playerRng = null;
        try { playerRng = player.PlayerRng.ToSerializable(); } catch { }

        SerializableRelicGrabBag? relicGrabBag = null;
        try { relicGrabBag = player.RelicGrabBag.ToSerializable(); } catch { }

        var pcs = player.PlayerCombatState;
        var cap = new PlayerCapture
        {
            Energy = (int)FPcsEnergy!.GetValue(pcs)!,
            Stars = (int)FPcsStars!.GetValue(pcs)!,
            Gold = (int)FPlayerGold!.GetValue(player)!,
            IsActiveForHooks = player.IsActiveForHooks,
            Phase = pcs.Phase,
            TurnNumber = pcs.TurnNumber,
            MaxPotionCount = player.MaxPotionCount,
            PlayerRng = playerRng,
            RelicGrabBag = relicGrabBag,
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
            {
                _potionClones[slot] = (PotionModel)slot.MutableClone();
                // Independent clone, same pattern/try-catch as the relic path below — see
                // FPotionDynVars for why _potionClones alone isn't enough for this field.
                try
                {
                    if (slot.DynamicVars?.Clone(slot) is { } potionDynVars)
                        _potionDynamicVars[slot] = potionDynVars;
                }
                catch { }
            }
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
        // List membership/order (StackCount/etc. above are per-relic). No content path
        // mutates player.Relics mid-combat (AddRelicInternal/RemoveRelicInternal have no
        // callers outside Player.cs and Commands/), so this always comes back identical to
        // cap.Relics's membership — see RestoreRelicList for what happens if it doesn't.
        cap.RelicOrder.AddRange(player.Relics);

        _players[player.NetId] = cap;
    }

    /// <summary>
    /// The potion this snapshot captured in one player's belt slot, or null.
    /// The picker needs this because a potion step's checksum context is written after
    /// the potion already left the belt, so the name only survives in the state before it.
    /// </summary>
    internal PotionModel? GetPotionAtSlot(ulong netId, int slotIndex)
    {
        if (!_players.TryGetValue(netId, out var cap)) return null;
        if (slotIndex < 0 || slotIndex >= cap.PotionSlots.Count) return null;
        return cap.PotionSlots[slotIndex];
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
        Try("turn coordination", () => RestoreTurnCoordination(cs));
        Try("run rng", RestoreRunRng);
        Try("history", RestoreHistory);
        Try("combat lists", () => RestoreCombatLists(cs));
        Log.Write("Restore complete");
    }

    /// <summary>Count of Try() sections that threw during a restore, and the name of the most recent
    /// one — for the headless fuzzer (UndoFuzz.cs) to notice a silently-swallowed restore failure
    /// without re-reading the log file. Incremented from this catch block, the actual source of truth,
    /// rather than string-matching Log.Write's output. Dormant/unused outside the fuzzer — normal play
    /// never reads these.</summary>
    internal static int RestoreSectionFailureCount;
    internal static string LastFailedRestoreSection = "";

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            RestoreSectionFailureCount++;
            LastFailedRestoreSection = what;
            Log.Write($"Restore section '{what}' FAILED: {ex}");
        }
    }

    private void RestoreCreatures(CombatState cs)
    {
        var live = cs.Creatures.ToDictionary(c => c.CombatId ?? uint.MaxValue);

        // Creatures crossing dead -> alive on a node that stays IN the roster the whole
        // time (a revived PLAYER — CombatManager.HandlePlayerDeath leaves players in the
        // roster, unlike a dead monster, which CombatState.RemoveCreature drops). Those
        // never go through pass 2's ReviveCreature below, so their node never runs the
        // game's own death->alive transition; pass 3 does that explicitly. Filled in
        // during pass 1, below, before the HP write overwrites the pre-restore value.
        var resurrected = new List<Creature>();

        // Pass 1: write every captured creature's model state back FIRST, before any
        // revive runs. UiRefresh.ReviveCreature -> NCombatRoom.AddCreature ->
        // NCreature.Create(entity) -> _Ready() -> _stateDisplay.SetCreature(Entity)
        // (NCreature.cs:450-494) builds the fresh node's visuals (health bar, etc.)
        // straight off the model at construction time. If the model were still at its
        // post-death HP (0) when that node gets built, the revived creature would come
        // back looking damaged/mid-hit. Getting HP/block/powers/etc. right first means
        // the node is built correct the first time.
        foreach (var cap in _creatures)
        {
            // Read dead/alive off the model's CURRENT (pre-restore) state, before the
            // HP write two lines down overwrites it — cap.Ref.IsDead is CurrentHp <= 0
            // (Creature.cs:207-209). Only creatures still in the roster qualify here;
            // ones missing from `live` are handled by pass 2's ReviveCreature instead.
            if (live.ContainsKey(cap.CombatId) && cap.Ref.IsDead && cap.Hp > 0)
                resurrected.Add(cap.Ref);

            int oldHp = (int)FCreatureHp!.GetValue(cap.Ref)!;
            int oldMaxHp = (int)FCreatureMaxHp!.GetValue(cap.Ref)!;
            int oldBlock = (int)FCreatureBlock!.GetValue(cap.Ref)!;

            FCreatureHp.SetValue(cap.Ref, cap.Hp);
            FCreatureMaxHp.SetValue(cap.Ref, cap.MaxHp);
            FCreatureBlock.SetValue(cap.Ref, cap.Block);
            cap.Ref.HpDisplay = cap.HpDisplay;

            // These writes are straight to the backing fields, so none of Creature's
            // CurrentHpChanged/MaxHpChanged/BlockChanged events fire on their own — every
            // event-driven HP widget (NTopBarHp, NMultiplayerPlayerState) would otherwise
            // keep showing whatever it last saw. Runs for EVERY captured creature, not just
            // resurrected ones: any undo that changes HP or block has this same stale-widget
            // problem, the player-death case just made it visible. Try-wrapped so a UI
            // failure here can never abort the restore.
            Try($"creature value changed {cap.CombatId}", () =>
                UiRefresh.FireCreatureValueChanged(cap.Ref, oldHp, cap.Hp, oldMaxHp, cap.MaxHp, oldBlock, cap.Block));

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
                    if (p.DynamicVarsClone != null)
                        FPowerDynVars?.SetValue(p.Ref, p.DynamicVarsClone);
                    powers.Add(p.Ref);
                }
            }

            if (cap.Ref.Monster is { } monster)
            {
                if (cap.MonsterRng != null && FMonsterRng?.GetValue(monster) is Rng rng)
                    rng.LoadFromSerializable(cap.MonsterRng);
                if (cap.Moves != null)
                    RestoreMoves(monster, cap.Moves);
                // Must run after RestoreMoves: when RestoreMoves rebuilds a destroyed state
                // machine it goes through SetUpForCombat() (MonsterModel.cs:410-414), which
                // also sets SpawnedThisTurn = true as a side effect. This explicit restore
                // overwrites that with the captured value so a revived monster doesn't
                // wrongly look "just spawned" for the rest of the fight.
                FMonsterSpawnedThisTurn?.SetValue(monster, cap.SpawnedThisTurn);
            }
        }

        // Pass 2: now revive creatures killed after the snapshot — the model is
        // already correct (pass 1), so the node AddCreature builds reads the right
        // HP/block/powers instead of the stale post-death state.
        foreach (var cap in _creatures)
        {
            // A creature killed after the snapshot is gone from the roster; put the
            // saved reference back on its side so the model matches the snapshot.
            if (!live.ContainsKey(cap.CombatId))
                Try($"revive {cap.CombatId}", () =>
                {
                    UiRefresh.ReviveCreature(cs, cap.Ref);

                    // Must come after ReviveCreature: the node this applies to only exists
                    // once AddCreature has built it. Fine to come before pass 6's
                    // RepositionEnemies — in fact better to: SetScaleAndHue calls
                    // UpdateBounds, and PositionEnemies reads Visuals.Bounds.Size when
                    // spreading the row, so applying the scale here first gives that later
                    // layout pass the right widths to work with.
                    if (cap.VisualTweak is { } tweak)
                        UiRefresh.ApplyVisualTweak(cap.Ref, tweak);
                });
        }

        // Pass 3: bring resurrected in-roster creatures (dead players, collected during
        // pass 1 above) out of their death pose — the in-roster counterpart of pass 2:
        // same "came back from the dead" situation, but the node was never destroyed
        // (players stay in the roster on death), so there is no fresh AddCreature-built
        // node to pick the correct visuals up automatically; the existing node has to be
        // told explicitly via UiRefresh.ReviveVisuals. Deliberately does not touch
        // anything pass 2 already handled — those got a brand-new node built from the
        // already-restored (pass 1) model, which is correct from the start.
        foreach (var c in resurrected)
            Try($"revive visuals {c.CombatId}", () => UiRefresh.ReviveVisuals(c));

        // Pass 4: creatures that exist now but were not in the snapshot were summoned
        // after it was taken — drop them from the model and the scene.
        foreach (var creature in cs.Creatures.ToList())
            if (creature.CombatId is { } id && !_liveCreatureIds.Contains(id))
                Try($"remove summoned {id}", () => UiRefresh.RemoveCreature(cs, creature));

        // Pass 5: put allies/enemies back in captured relative order.
        Try("creature order", () => RestoreCreatureOrder(cs));

        // Pass 6: re-lay-out the enemy row — a revive in pass 2 may have appended a
        // node out of position (see UiRefresh.RepositionEnemies for why).
        Try("enemy layout", () => UiRefresh.RepositionEnemies(cs));
    }

    /// <summary>
    /// AddCreature (used by UiRefresh.ReviveCreature) always appends to the end of
    /// _allies/_enemies, so a revived creature lands last regardless of where it sat
    /// when the snapshot was taken. NetFullCombatState.FromRun iterates cs.Creatures
    /// (= _allies.Concat(_enemies), CombatState.cs:71) and that order is part of the
    /// checksummed state dump, so membership alone isn't enough — the lists have to
    /// go back in the captured relative order too.
    /// </summary>
    private void RestoreCreatureOrder(CombatState cs)
    {
        // _creatures was captured by iterating cs.Creatures (allies then enemies), so
        // filtering by side preserves each side's original relative order.
        var desiredAllies = _creatures.Where(c => c.Ref.Side == CombatSide.Player).Select(c => c.Ref).ToList();
        var desiredEnemies = _creatures.Where(c => c.Ref.Side != CombatSide.Player).Select(c => c.Ref).ToList();

        RestoreSideOrder(FCombatAllies, cs, desiredAllies, "allies");
        RestoreSideOrder(FCombatEnemies, cs, desiredEnemies, "enemies");
    }

    private static void RestoreSideOrder(FieldInfo? field, CombatState cs, List<Creature> desired, string label)
    {
        if (field?.GetValue(cs) is not List<Creature> live) return;
        // Only rewrite when the counts match — a mismatch means a revive failed (or
        // some other list-affecting section did), and reordering on top of that would
        // silently resurrect a creature the model no longer holds. Leave it alone and
        // let the mismatch show up elsewhere (e.g. restore fidelity logging).
        if (live.Count != desired.Count)
        {
            Log.Write($"RestoreCreatureOrder: {label} count mismatch live={live.Count} desired={desired.Count}, leaving order as-is");
            return;
        }
        live.Clear();
        live.AddRange(desired);
    }

    private static void RestoreMoves(MonsterModel monster, MoveMachineCapture cap)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null)
        {
            // CombatManager.RemoveCreature nulls the move state machine on death
            // (creature.Monster.ResetStateMachine(), CombatManager.cs:1361-1370 ->
            // MonsterModel.cs:390-393 sets _moveStateMachine = null), and the
            // MoveStateMachine getter never rebuilds it lazily (MonsterModel.cs:223-238).
            // Reviving the creature (RestoreCreatures pass 2, which runs after this) puts
            // HP back above zero but does nothing about this — without a rebuild here the
            // monster is left with NO state machine at all, and the next turn's RollMove
            // (MonsterModel.cs:416-419) dereferences it -> NullReferenceException.
            // SetUpForCombat() (MonsterModel.cs:410-414) is the only other place that
            // builds one; only call it while the machine IS null, since its private setter
            // throws if a machine already exists (MonsterModel.cs:223-238).
            monster.SetUpForCombat();
            sm = monster.MoveStateMachine;
            if (sm == null)
            {
                Log.Write($"RestoreMoves: {monster.Id} still has no state machine after SetUpForCombat(), giving up");
                return;
            }
            Log.Write($"RestoreMoves: rebuilt destroyed state machine for {monster.Id}");
        }

        if (cap.CurrentStateId.Length > 0 && sm.States.TryGetValue(cap.CurrentStateId, out var target))
            MSmForceState?.Invoke(sm, new object[] { target });
        FSmPerformedFirst?.SetValue(sm, cap.PerformedFirstMove);

        // A rebuilt machine (above) contains brand-new MonsterState instances that the
        // captured StateLog/NextMove objects are foreign to, so re-resolve every captured
        // state against the CURRENT machine's States dictionary by id before writing it
        // back. Unconditional (not only after a rebuild) because it's a no-op otherwise:
        // when sm is the original machine, every id maps straight back to the very same
        // instance that was captured.
        sm.StateLog.Clear();
        int unresolvedStates = 0;
        foreach (var entry in cap.StateLog)
        {
            if (entry is not MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState capturedState)
                continue;
            if (sm.States.TryGetValue(capturedState.Id, out var resolved))
                sm.StateLog.Add(resolved);
            else
            {
                sm.StateLog.Add(capturedState);
                unresolvedStates++;
            }
        }
        if (unresolvedStates > 0)
            Log.Write($"RestoreMoves: {unresolvedStates} StateLog id(s) for {monster.Id} missing from the current machine, keeping captured objects");

        foreach (var (key, performed) in cap.MovePerformedOnce)
            if (sm.States.TryGetValue(key, out var state) && FMovePerformedOnce != null)
                FMovePerformedOnce.SetValue(state, performed);

        if (cap.NextMove is MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState capturedNextMove)
        {
            object resolvedNextMove = capturedNextMove;
            if (sm.States.TryGetValue(capturedNextMove.Id, out var resolvedState) &&
                resolvedState is MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState resolvedMove)
                resolvedNextMove = resolvedMove;
            PMonsterNextMove?.SetValue(monster, resolvedNextMove);
        }
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
            // See PlayerCapture.IsActiveForHooks: the only public path back to true is
            // Creature.HealInternal crossing dead->alive, which the direct HP write in
            // RestoreCreatures never goes through.
            FPlayerIsActiveForHooks?.SetValue(player, cap.IsActiveForHooks);
            pcs.Phase = cap.Phase;
            // Checksummed field with no public setter (only IncrementTurnNumber()) — go
            // through the auto-property's backing field, same pattern as FRelicStack.
            FPcsTurnNumber?.SetValue(pcs, cap.TurnNumber);

            RestorePiles(pcs, cap, tracker);
            Try("card fields", () => RestoreCardFields(pcs));
            Try("orbs", () => RestoreOrbQueue(pcs, cap));
            Try("pets", () => RestorePets(pcs, cs, cap));
            // Must run before RestorePotionSlots: it indexes _potionSlots by slot, and a
            // belt shrink from a stale MaxPotionCount discards whatever sits in the
            // removed slots (Player.SetMaxPotionCountInternal, Player.cs:597-617).
            Try("max potion count", () => RestoreMaxPotionCount(player, cap));
            Try("potions", () => RestorePotionSlots(player, cap));
            // Must run before RestoreRelicStates: it reconciles which RelicModel instances
            // are on the player at all, and RestoreRelicStates only writes per-relic state
            // for relics that are (by then) actually present.
            Try("relic list", () => RestoreRelicList(player, cap));
            Try("relics", () => RestoreRelicStates(cap));
            // Checksummed (NetFullCombatState.cs:436-437) but never printed by ToString(),
            // which is exactly why Change C (byte-exact fidelity check) exists — see
            // ChecksumHook.SerializeCurrentState.
            Try("player rng", () => { if (cap.PlayerRng != null) player.PlayerRng.LoadFromSerializable(cap.PlayerRng); });
            Try("relic grab bag", () => { if (cap.RelicGrabBag != null) player.RelicGrabBag.LoadFromSerializable(cap.RelicGrabBag); });

            // hooks= is the one restored value with no other observable: IsActiveForHooks
            // is not in the checksum payload, so the byte-exact fidelity check cannot prove
            // it, and a player left with hooks=False silently loses every relic/potion/card
            // trigger for the rest of the combat.
            // phase= is worth logging alongside hooks=: restoring a turn-start anchor used to
            // land on Start (the anchor predated the play-phase entry hooks), which replayed the
            // turn without them. It must read Play after a turn-start undo.
            Log.Write($"RestorePlayer {player.NetId}: hooks={player.IsActiveForHooks} phase={player.PlayerCombatState.Phase} " +
                      $"turn={player.PlayerCombatState.TurnNumber} maxPotions={player.MaxPotionCount} relics={player.Relics.Count()}");
        }
    }

    /// <summary>
    /// Player.MaxPotionCount has no setter — only AddToMaxPotionCount/SubtractFromMaxPotionCount
    /// (Player.cs:566,575), which go through SetMaxPotionCountInternal and fire
    /// MaxPotionCountChanged so NPotionContainer grows/shrinks its holders. Belt size
    /// changing mid-undo (a relic/card effect resizing it, then undoing across that
    /// action) is rare, hence the log line whenever this actually fires.
    /// </summary>
    private static void RestoreMaxPotionCount(Player player, PlayerCapture cap)
    {
        int delta = cap.MaxPotionCount - player.MaxPotionCount;
        if (delta == 0) return;
        Log.Write($"RestoreMaxPotionCount: {player.NetId} {player.MaxPotionCount} -> {cap.MaxPotionCount}");
        if (delta > 0) player.AddToMaxPotionCount(delta);
        else player.SubtractFromMaxPotionCount(-delta);
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
            CopyMutableFields(Pristine(clone), card);
            RebindDeepCloneOwnership(card);
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
            {
                CopyMutableFields(Pristine(clone), orb);
                RebindDeepCloneOwnership(orb);
            }
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
            {
                CopyMutableFields(Pristine(clone), potion);
                RebindDeepCloneOwnership(potion);
            }
            // Must come after CopyMutableFields above: the stored clone's _dynamicVars
            // still aliases whatever object the live potion already has (see
            // FPotionDynVars), so the generic sweep is a no-op on this field either way —
            // but only this explicit write, using the independently captured clone, is
            // guaranteed to actually land the captured values.
            if (_potionDynamicVars.TryGetValue(potion, out var potionDynVars))
                FPotionDynVars?.SetValue(potion, potionDynVars);
            FPotionOwner?.SetValue(potion, player);
            PPotionRemoved?.SetValue(potion, false);
        }
    }

    /// <summary>
    /// Insurance for relic list membership/order — per-relic state (StackCount/IsWax/
    /// IsMelted/Status/DynamicVars) is captured/restored separately by RestoreRelicStates.
    /// No content path mutates player.Relics mid-combat today (AddRelicInternal/
    /// RemoveRelicInternal have no callers outside Player.cs and Commands/), so the normal
    /// case is "already identical to the capture" and this does nothing — touching the
    /// list when it doesn't need it would fire RelicObtained/RelicRemoved for no reason.
    /// If it's ever NOT identical, reconcile through the game's own public methods
    /// (silent: true) so ownership/events/hook subscriptions stay consistent.
    /// </summary>
    private static void RestoreRelicList(Player player, PlayerCapture cap)
    {
        var live = player.Relics;
        bool identical = live.Count == cap.RelicOrder.Count;
        if (identical)
            for (int i = 0; i < live.Count; i++)
                if (!ReferenceEquals(live[i], cap.RelicOrder[i])) { identical = false; break; }
        if (identical) return;

        Log.Write($"RestoreRelicList: {player.NetId} relic list diverged from capture " +
                  $"(live=[{string.Join(",", live.Select(r => r.Id))}] " +
                  $"captured=[{string.Join(",", cap.RelicOrder.Select(r => r.Id))}]) — reconciling");

        foreach (var relic in live.Except(cap.RelicOrder).ToList())
            player.RemoveRelicInternal(relic, silent: true);

        for (int i = 0; i < cap.RelicOrder.Count; i++)
        {
            var relic = cap.RelicOrder[i];
            if (player.Relics.Contains(relic)) continue;
            // Clamp: earlier inserts in this loop can only ever shrink how far ahead of
            // the live list this index is, never push it past the end.
            player.AddRelicInternal(relic, Math.Min(i, player.Relics.Count), silent: true);
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
            {
                // Re-shadow the shadow before handing it to the live relic — same reasoning as
                // Pristine(clone) above (see its doc comment), and the precedent already set by
                // the power restore path a few methods up: FPowerInternal?.SetValue(p.Ref,
                // Shadow(p.InternalShadow)). Now that Shadow() (above) gives its clone
                // independent containers, CopyMutableFields(shadow, r.Ref) directly would hand
                // those containers to the live relic BY REFERENCE, so the live relic and the
                // stored _relicShadow entry would go back to sharing them — the next combat
                // mutation would corrupt the stored snapshot, and restoring to the same point
                // twice would replay already-polluted values. CopyMutableFields(Shadow(shadow)!,
                // r.Ref) throws away a fresh shadow-of-the-shadow instead, keeping the stored
                // one pristine.
                CopyMutableFields(Shadow(shadow)!, r.Ref);
                RebindDeepCloneOwnership(r.Ref);
            }
        }
    }

    /// <summary>
    /// CombatManager keeps per-combat turn coordination on an internal object
    /// (CombatManager._turnState, type CombatTurnState — CombatTurnState.cs:21). We only ever
    /// snapshot in, and RestoreTo (ChecksumHook.RestoreTo) only ever restores into, an idle
    /// player play phase (see UndoProtocol.CommitAsync's gating), so:
    ///  - PlayersTakingExtraTurn (CombatTurnState.cs:68) is the one field here that actually
    ///    varies WITHIN a play phase: it's rebuilt wholesale at the player-&gt;enemy transition
    ///    (SwitchFromPlayerToEnemySide, CombatManager.cs:1824-1832, via Hook.ShouldTakeExtraTurn)
    ///    and cleared at combat end (:1307) and at that same transition (:1826) — but it stays
    ///    non-empty for the duration of an extra turn and selects who ends their turn
    ///    (EndPlayerTurnPhaseOneInternal :1529, EndPlayerTurnPhaseTwoInternal :1748). So it's
    ///    captured (StateSnapshot._playersTakingExtraTurn, by NetId) and rebuilt here from the
    ///    live players.
    ///  - IsEnemyTurnStarted / EndingPlayerTurnPhaseOne / EndingPlayerTurnPhaseTwo
    ///    (CombatTurnState.cs:98/100/102) are all false at every idle player-play-phase moment:
    ///    each is set true only for the duration of its own transition and back to false before
    ///    the next player-phase checksum can fire (CombatManager.cs:833/839, :1458/1471,
    ///    :1706/1720). PlayersReadyToBeginEnemyTurn (:66) is cleared at the very start of the
    ///    player turn (CombatManager.cs:730) and only gains entries once a player calls
    ///    SetReadyToBeginEnemyTurn — again, entirely within-transition. Rather than capture
    ///    these four, force them to their known-neutral values on restore, and log loudly if any
    ///    of them was NOT already neutral: that would mean a player-play-phase checksum fired
    ///    while one of these was non-neutral, which should be impossible under the invariant
    ///    above — evidence of an unresolved multiplayer divergence we are hunting.
    ///
    /// Game code mutates all of the above under turnState.ReadyLock (CombatTurnState.cs:62, e.g.
    /// CombatManager.cs:1824). We do NOT take that lock via reflection: RestoreTo runs on the
    /// main thread with the action queue idle (the same invariant CommitAsync relies on to
    /// restore at all), so nothing else can be contending for it in this window, and reflecting
    /// into a System.Threading.Lock's ref-struct EnterScope() token for a lock that isn't
    /// actually contended here would be fragile for no real benefit.
    /// </summary>
    private void RestoreTurnCoordination(CombatState cs)
    {
        var cm = CombatManager.Instance;
        var turnState = FCmTurnState?.GetValue(cm);
        if (turnState == null)
        {
            Log.Write("RestoreTurnCoordination: no live CombatTurnState, skipping");
            return;
        }

        // B1: rebuild from the live players captured by NetId — see the doc comment above.
        // Must go through CombatTurnState's own property, not CombatManager.PlayersTakingExtraTurn
        // (that one returns a .ToList() copy, CombatManager.cs:134 — mutating it would touch
        // nothing real).
        if (PTsPlayersTakingExtraTurn?.GetValue(turnState) is List<Player> liveExtraTurn)
        {
            var byNetId = cs.Players.ToDictionary(p => p.NetId);
            liveExtraTurn.Clear();
            foreach (var netId in _playersTakingExtraTurn)
                if (byNetId.TryGetValue(netId, out var player))
                    liveExtraTurn.Add(player);
        }

        // B2: force the transition-only flags back to neutral, logging first if they weren't.
        NormalizeNeutralBool(turnState, PTsIsEnemyTurnStarted, cm.IsEnemyTurnStarted, "IsEnemyTurnStarted");
        NormalizeNeutralBool(turnState, PTsEndingPlayerTurnPhaseOne, cm.EndingPlayerTurnPhaseOne, "EndingPlayerTurnPhaseOne");
        NormalizeNeutralBool(turnState, PTsEndingPlayerTurnPhaseTwo, cm.EndingPlayerTurnPhaseTwo, "EndingPlayerTurnPhaseTwo");

        if (PTsPlayersReadyToBeginEnemyTurn?.GetValue(turnState) is HashSet<Player> readySet)
        {
            if (readySet.Count > 0)
                Log.Write($"RestoreTurnCoordination: expected neutral, found PlayersReadyToBeginEnemyTurn with {readySet.Count} player(s) still ready — clearing.");
            readySet.Clear();
        }
    }

    /// <summary>
    /// Forces one of CombatTurnState's transition-only bool flags back to its idle-play-phase
    /// value (always false — see RestoreTurnCoordination's doc comment), logging loudly first
    /// if it was found non-neutral. Message is deliberately searchable
    /// ("RestoreTurnCoordination: expected neutral, found ...") — that log is evidence for an
    /// unresolved multiplayer divergence we are hunting.
    /// </summary>
    private static void NormalizeNeutralBool(object turnState, PropertyInfo? setProp, bool wasSet, string name)
    {
        if (wasSet)
            Log.Write($"RestoreTurnCoordination: expected neutral, found {name}=true — forcing false.");
        setProp?.SetValue(turnState, false);
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
