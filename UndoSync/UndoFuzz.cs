using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace UndoSync;

/// <summary>
/// Headless regression fuzzer for the capture/restore layer (ChecksumHook.cs + StateSnapshot.cs).
/// Drives many combats with no UI and no human input, undoing at random safe points, and asserts on
/// every restore that the game's own checksum payload (NetFullCombatState.FromRun(rs, null), the same
/// thing ChecksumTracker hashes) comes back byte-identical — see ChecksumHook.VerifyRestoreFidelity.
///
/// COMPLETELY DORMANT IN NORMAL PLAY. <see cref="MaybeStart"/> is the only entry point, it is called
/// once from UndoSyncMod.Initialize(), and its very first line is a CommandLineHelper.HasArg check for
/// "undosync-fuzz" — absent that flag, nothing else in this file ever executes or subscribes to
/// anything. This is a debugging tool, not a feature; it ships inside UndoSync only because that's
/// where the capture/restore code (and the internal hooks this needs) already lives.
///
/// Usage (opt-in only): --undosync-fuzz [--undosync-fuzz-count N] [--undosync-fuzz-seed XXXX]
///   --undosync-fuzz          Required. Presence alone opts in (CommandLineHelper.HasArg, same as the
///                             game's own --autoslay / --fastmp / --bootstrap flags).
///   --undosync-fuzz-count    Optional. How many combats to run. Default 50.
///   --undosync-fuzz-seed     Optional. Base seed. Default a fresh SeedHelper.GetRandomSeed(). Per-
///                             combat seeds are derived as "{baseSeed}-{combatIndex}", and everything
///                             this file rolls dice on (which card, which target, which sync point,
///                             whether to restore at all, which character/relics/potions/deck cards/
///                             upgrades/enchantments to fuzz with) is drawn from a System.Random seeded
///                             off (baseSeed, combatIndex) — so re-running with the same base seed
///                             reproduces the same sequence of combats and in-combat decisions.
///
/// Logs to the existing UndoSync-&lt;pid&gt;.log (Log.cs) with a "[Fuzz]" prefix, rather than a
/// separate file: this tool only ever runs standalone (never alongside real multiplayer undo
/// activity, since TestMode.IsOn makes RestoreTo's own picker/vote path irrelevant here), so there's
/// nothing to disentangle by splitting files, and reusing Log.cs means no new file-IO error surface.
/// </summary>
internal static class UndoFuzz
{
    private const string FuzzArg = "undosync-fuzz";
    private const string CountArg = "undosync-fuzz-count";
    private const string SeedArg = "undosync-fuzz-seed";
    private const int DefaultCombatCount = 50;

    /// <summary>Chance, checked once per eligible decision point, of attempting a restore. Kept low
    /// (~10-15%) and gated by <see cref="MaxRestoresPerCombat"/> + <see cref="MinTurnsBetweenRestores"/>
    /// below so restores stay rare, spread-out events rather than the dominant thing the drive loop
    /// does — a restore-happy policy was previously collapsing every combat back to the oldest anchor
    /// over and over instead of letting play actually progress.</summary>
    private const double RestoreProbability = 0.12;

    /// <summary>Hard cap on restores attempted per combat. Small on purpose: this is a fuzzer for the
    /// restore/verify path itself, not a stress test of restore volume — a handful of restores at
    /// varied points across many combats explores far more of the state space than piling up restores
    /// inside a single combat while the other 9 barely get touched.</summary>
    private const int MaxRestoresPerCombat = 3;

    /// <summary>Minimum number of end-turns the drive loop must have issued since the last restore
    /// before another one is even considered. Without this, back-to-back restores at the same
    /// unchanged decision point are possible (nothing forces forward progress in between).</summary>
    private const int MinTurnsBetweenRestores = 1;

    /// <summary>Hard cap on card-plays + end-turns for one combat, so a scripted loop that keeps
    /// producing "playable" cards (rather than a genuine game hang) can't run forever. Raised from the
    /// original 400: that budget was being burned by a restore policy that kept rewinding to turn 1
    /// forever (see RestoreProbability's doc comment) rather than by genuine long combats. With the
    /// restore policy fixed this is pure safety margin, not the expected steady state — a combat that
    /// still exhausts it is a harness/drive-loop concern, logged and counted separately from restore
    /// fidelity failures (see RunAllCombatsAsync's summary).</summary>
    private const int ActionBudgetPerCombat = 800;

    private static readonly TimeSpan CombatStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CombatWallClockTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan IdleWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for any single awaited step inside SetUpRandomLoadoutAsync (one relic Obtain,
    /// one potion TryToProcure, one CardPileCmd.Add). CombatWallClockTimeout only wraps
    /// DriveCombatAsync, so without a timeout here a relic/potion/card whose resolution blocks headless
    /// (e.g. it needs a selection prompt reachable only through UI, or awaits something that never
    /// completes without a human) would wedge this method — and therefore the whole combat, and
    /// therefore the whole fuzz run — forever. See AwaitWithTimeoutAsync.</summary>
    private static readonly TimeSpan LoadoutStepTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Materialized once and cached: ModelDb.DebugEnchantments is a LINQ query over reflection
    /// (ModelDb.cs:106, `from t in AllAbstractModelSubtypes ...`), so re-evaluating it every combat
    /// would re-walk every loaded type via reflection for no reason — this file runs dozens of combats
    /// per invocation.</summary>
    private static List<EnchantmentModel>? _debugEnchantmentsCache;
    private static List<EnchantmentModel> DebugEnchantmentsCache => _debugEnchantmentsCache ??= ModelDb.DebugEnchantments.ToList();

    private sealed class CombatOutcome
    {
        public int CombatIndex;
        public string BaseSeed = "";
        public string Seed = "";
        public string EncounterId = "";

        /// <summary>Which of ModelDb.AllCharacters (by ModelId.Entry) this combat used — set once per
        /// combat via a random pick in RunOneCombatAsync. Previously always Ironclad, which left every
        /// other character's starting deck/relics/mechanics entirely unexercised.</summary>
        public string CharacterId = "";

        /// <summary>The resolved encounter's RoomType (Monster/Elite/Boss), as a string — set from
        /// mutableEncounter.RoomType.ToString() next to where EncounterId is assigned, so an Elite/Boss
        /// combat is distinguishable from a Monster one at a glance in the per-combat log line, now
        /// that ResolveEncounterPool's pool spans all three (see its doc comment).</summary>
        public string RoomTypeName = "";

        public bool Completed;
        public string? DriveError;

        /// <summary>Set only when DriveError specifically came from ActionBudgetPerCombat hitting
        /// zero — lets the summary tell "harness ran out of steam" apart from every other DriveError
        /// (setup failure, wall-clock timeout, stuck-waiting), all of which are also harness/drive
        /// concerns rather than restore-fidelity findings. See RunAllCombatsAsync's summary.</summary>
        public bool BudgetExhausted;

        public int RestoresAttempted;
        public int RestoresFailed;

        /// <summary>Cumulative end-turns issued this combat — the progress signal a stalled drive
        /// loop would fail to move, and the basis for MinTurnsBetweenRestores spacing below.</summary>
        public int TurnsPlayed;

        /// <summary>Cumulative successful CardModel.TryManualPlay calls this combat (only counts when
        /// it returned true, i.e. the PlayCardAction was actually enqueued) — logged so a suspiciously
        /// fast "completed" combat can be checked for real: did it actually play cards, or did
        /// something end it before it began?</summary>
        public int CardsPlayed;

        /// <summary>Counts of what SetUpRandomLoadoutAsync actually managed to inject before combat
        /// start — each counts only successful applications (relic actually obtained, potion actually
        /// procured, card actually added to the deck pile, upgrade actually applied, enchantment
        /// actually applied), never attempts. Surfaced in both the per-combat log line and the
        /// run-level coverage summary so "did this run actually explore anything beyond the untouched
        /// starting deck" is answerable from the log alone.</summary>
        public int RelicsInjected;
        public int PotionsInjected;
        public int DeckCardsInjected;
        public int CardsUpgraded;
        public int EnchantsApplied;

        /// <summary>TurnsPlayed's value as of the most recent restore (0 if none yet this combat) —
        /// TurnsPlayed - LastRestoreTurnMark is "turns played since last restore" for the
        /// MinTurnsBetweenRestores gate in TryAttemptRestore.</summary>
        public int LastRestoreTurnMark;

        /// <summary>True when the driver could not complete a successful action (a card play that
        /// resolved, or an end-turn) within the normal timeout after a restore — the shape an
        /// action-id-reuse bug in ChecksumHook.RestoreTo's FastForwardNextActionId/FastForwardHookId
        /// would take (a stale id colliding with a live one, silently wedging the action queue).
        /// Distinct from a fidelity failure: restored state can come back byte-identical and the
        /// driver can still be unable to act afterward, or vice versa.</summary>
        public bool StuckAfterRestore;
        public string StuckAfterRestoreDetail = "";

        /// <summary>Count of StateSnapshot.Try / UiRefresh.Section catch-block failures observed
        /// during this combat. Both swallow exceptions into a log line so one broken section can't
        /// abort the rest of a restore, which means a silently-broken section would otherwise never
        /// surface here. Nonzero fails the combat as its own finding, separate from
        /// fidelity/stuck-after-restore.</summary>
        public int SectionFailures;
        public string SectionFailureDetail = "";

        public readonly List<string> FailureRepros = new();
    }

    /// <summary>
    /// Auto-picks cards for any selection prompt the fuzzer's randomized loadout/deck can trigger
    /// (e.g. a relic or card whose effect asks the player to choose N cards) — a direct headless port
    /// of AutoSlay.Helpers.AutoSlayCardSelector
    /// (decompiled/MegaCrit/sts2/Core/AutoSlay/Helpers/AutoSlayCardSelector.cs), with one deliberate
    /// substitution: a plain System.Random instead of the game's Rng. Same reasoning as `rng`/`pickRng`
    /// elsewhere in this file — the fuzzer's own choices must never draw from the game's own
    /// Rng/RunRngSet streams, because those drive checksummed gameplay, and a harness-made pick
    /// perturbing them would make the "same baseSeed reproduces the same run" guarantee documented at
    /// the top of this file false for any run that happens to hit a selection prompt.
    ///
    /// What this buys us: without a selector installed via CardSelectCmd.UseSelector, any card/relic
    /// whose resolution asks the player to pick cards blocks forever headless (there's no UI to answer
    /// it) — which would make randomized decks/relics/potions, i.e. this whole widening effort,
    /// untestable rather than merely unrepresentative.
    /// </summary>
    private sealed class FuzzCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        private readonly Random _rng;

        public FuzzCardSelector(Random rng)
        {
            _rng = rng;
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            if (list.Count == 0)
                return Task.FromResult((IEnumerable<CardModel>)Array.Empty<CardModel>());

            int n = Math.Min(maxSelect, list.Count);
            if (n < minSelect) n = Math.Min(minSelect, list.Count);

            // Inline Fisher-Yates over _rng — never the game's Rng, see this class's doc comment.
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return Task.FromResult((IEnumerable<CardModel>)list.Take(n));
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return default;
            return new CardRewardSelection { card = options[_rng.Next(options.Count)].Card, alternative = null };
        }
    }

    // ==================================================================================
    // Entry point
    // ==================================================================================

    /// <summary>
    /// Called once from UndoSyncMod.Initialize(). The HasArg check below is the ONLY gate on this
    /// entire file running: everything else is unreachable unless --undosync-fuzz was passed.
    /// </summary>
    /// <summary>
    /// When true, ChecksumHook traces every checksum it receives before any filtering. Only ever set
    /// by the fuzz run: the anchor filters return silently, so a missing anchor kind is otherwise
    /// indistinguishable from a checksum the game never generated.
    /// </summary>
    internal static bool TraceChecksums;

    /// <summary>
    /// Stands in for RunManager.SendPostActionChecksum (RunManager.cs:568-572), which the headless
    /// path never reaches. Same guard as production: in-combat, and skipping the two action types it
    /// skips. Fuzz-only — nothing subscribes this outside a --undosync-fuzz run.
    /// </summary>
    private static void GenerateMissingPostActionChecksum(MegaCrit.Sts2.Core.GameActions.GameAction action)
    {
        try
        {
            if (CombatManager.Instance is not { IsInProgress: true }) return;
            if (action is MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction) return;
            if (action is MegaCrit.Sts2.Core.GameActions.ReadyToBeginEnemyTurnAction) return;
            RunManager.Instance?.ChecksumTracker.GenerateChecksum($"finished action execution {action}", action);
        }
        catch (Exception ex)
        {
            Log.Write($"[Fuzz] GenerateMissingPostActionChecksum ERROR: {ex.Message}");
        }
    }

    internal static void MaybeStart()
    {
        try
        {
            if (!CommandLineHelper.HasArg(FuzzArg)) return;
            TraceChecksums = CommandLineHelper.HasArg("undosync-fuzz-trace");

            int count = DefaultCombatCount;
            if (CommandLineHelper.TryGetValue(CountArg, out var countStr)
                && int.TryParse(countStr, out var parsedCount) && parsedCount > 0)
                count = parsedCount;

            string baseSeed = CommandLineHelper.TryGetValue(SeedArg, out var seedArg) && !string.IsNullOrEmpty(seedArg)
                ? seedArg
                : SeedHelper.GetRandomSeed();

            Log.Write($"[Fuzz] --{FuzzArg} detected — will run {count} combat(s) once game startup completes (baseSeed=\"{baseSeed}\").");
            _ = RunWhenReadyAsync(count, baseSeed);
        }
        catch (Exception ex)
        {
            // Never throw out of a mod initializer — that would take the whole mod load down with it.
            Log.Write($"[Fuzz] MaybeStart ERROR: {ex}");
        }
    }

    /// <summary>
    /// Waits for the game to finish booting before doing anything. NGame.GameStartupComplete
    /// (NGame.cs:481) "Completes when GameStartup has finished (including ModelDb initialization)...
    /// Used by NSceneBootstrapper to wait for initialization before starting a debug run" — the exact
    /// same officially-sanctioned wait point NSceneBootstrapper.StartNewRun itself awaits
    /// (NSceneBootstrapper.cs:85) before building its own off-main-menu run. Reused here rather than
    /// re-derived, since it already accounts for everything CreateForTest/SetUpTest below need ready
    /// (ModelDb populated, SaveManager initialized, etc).
    ///
    /// Not circular despite being awaited from inside mod init: UndoSyncMod.Initialize() (which calls
    /// MaybeStart, which starts this method as a fire-and-forget task) itself runs from
    /// ModManager.Initialize, called from OneTimeInitialization.ExecuteVeryEarly
    /// (OneTimeInitialization.cs:59), awaited at the very first line of NGame.GameStartup (NGame.cs:
    /// 651) — i.e. GameStartup is still running (and hasn't set _gameStartupComplete yet) at the
    /// moment this task starts. Awaiting the Task here just parks this fire-and-forget task until the
    /// rest of GameStartup (including LaunchMainMenu) finishes; it does not block GameStartup itself.
    /// </summary>
    private static async Task RunWhenReadyAsync(int count, string baseSeed)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null)
            {
                Log.Write("[Fuzz] NGame.Instance was null at mod-init time — cannot wait for startup, aborting.");
                return;
            }
            await game.GameStartupComplete;
            await RunAllCombatsAsync(count, baseSeed);
        }
        catch (Exception ex)
        {
            Log.Write($"[Fuzz] RunWhenReadyAsync ERROR: {ex}");
        }
    }

    // ==================================================================================
    // Combat loop
    // ==================================================================================

    private static async Task RunAllCombatsAsync(int combatCount, string baseSeed)
    {
        Log.Write($"[Fuzz] ==================== starting: {combatCount} combat(s), baseSeed=\"{baseSeed}\" ====================");

        var pool = ResolveEncounterPool();
        if (pool.Count == 0)
        {
            Log.Write("[Fuzz] ABORT: no encounters resolved from ModelDb.AllEncounters — nothing to fight.");
            return;
        }

        int completed = 0;
        int restoresAttempted = 0;
        int restoresFailed = 0;
        int budgetExhausted = 0;
        var failingSeeds = new List<string>();
        var driveErrorSeeds = new List<string>();
        var stuckAfterRestoreSeeds = new List<string>();
        var sectionFailureSeeds = new List<string>();

        // Coverage accumulators for the "coverage:" summary line below — answer "did this run
        // actually explore anything beyond the untouched starting deck" from the log alone, without
        // post-processing every per-combat line.
        var characterCounts = new Dictionary<string, int>();
        var roomTypeCounts = new Dictionary<string, int>();
        int totalRelicsInjected = 0;
        int totalPotionsInjected = 0;
        int totalDeckCardsInjected = 0;
        int totalCardsUpgraded = 0;
        int totalEnchantsApplied = 0;

        try
        {
            for (int i = 0; i < combatCount; i++)
            {
                // Independent RNG per combat, seeded off (baseSeed, i) only — deliberately NOT the
                // game's own Rng/RunRngSet streams (those drive checksummed gameplay and must stay
                // untouched by our card/target/restore choices). Reusing the same (baseSeed, i) pair
                // reproduces the same sequence of choices, which is all "REPRO: baseSeed=... combatIndex=..."
                // in the failure log needs to give back the same run.
                var pickRng = new Random(DeterministicSeed($"{baseSeed}-{i}"));
                var encounterPrototype = pool[pickRng.Next(pool.Count)];

                CombatOutcome outcome;
                try
                {
                    outcome = await RunOneCombatAsync(i, baseSeed, encounterPrototype, pickRng);
                }
                catch (Exception ex)
                {
                    // RunOneCombatAsync already guards its own body; this is a last-resort net so one
                    // bad combat can never take the whole fuzz run down.
                    Log.Write($"[Fuzz] combat={i} UNCAUGHT ERROR: {ex}");
                    outcome = new CombatOutcome { CombatIndex = i, BaseSeed = baseSeed, Seed = $"{baseSeed}-{i}", DriveError = ex.Message };
                }

                restoresAttempted += outcome.RestoresAttempted;
                restoresFailed += outcome.RestoresFailed;
                if (outcome.Completed && outcome.DriveError == null)
                    completed++;

                // "Failing" here means only what this fuzzer actually exists to catch: a restore that
                // didn't come back byte-identical (VerifyRestoreFidelity FAIL). Budget exhaustion,
                // wall-clock timeouts, stuck-waiting, and setup errors are all harness/drive-loop
                // concerns — real, worth fixing, but NOT evidence of a capture/restore state bug, and
                // conflating them here would send someone chasing a nonexistent state bug from a
                // seed where the restore/verify path never even found anything wrong.
                if (outcome.FailureRepros.Count > 0)
                    failingSeeds.Add(outcome.Seed);
                if (outcome.StuckAfterRestore)
                    stuckAfterRestoreSeeds.Add(outcome.Seed);
                if (outcome.SectionFailures > 0)
                    sectionFailureSeeds.Add(outcome.Seed);
                if (outcome.BudgetExhausted)
                    budgetExhausted++;
                else if (outcome.DriveError != null)
                    driveErrorSeeds.Add(outcome.Seed);

                if (outcome.CharacterId.Length > 0)
                    characterCounts[outcome.CharacterId] = characterCounts.GetValueOrDefault(outcome.CharacterId) + 1;
                if (outcome.RoomTypeName.Length > 0)
                    roomTypeCounts[outcome.RoomTypeName] = roomTypeCounts.GetValueOrDefault(outcome.RoomTypeName) + 1;
                totalRelicsInjected += outcome.RelicsInjected;
                totalPotionsInjected += outcome.PotionsInjected;
                totalDeckCardsInjected += outcome.DeckCardsInjected;
                totalCardsUpgraded += outcome.CardsUpgraded;
                totalEnchantsApplied += outcome.EnchantsApplied;

                // One line per combat on the happy path; failures already got a full block logged
                // from inside RunOneCombatAsync/TryAttemptRestore, so this line is just the index.
                // turnsPlayed/cardsPlayed make a stalled drive loop (or a suspiciously fast "completed")
                // visible at a glance without digging into the full per-action log.
                Log.Write($"[Fuzz] combat={i}/{combatCount - 1} seed={outcome.Seed} encounter={outcome.EncounterId} "
                    + $"character={outcome.CharacterId} roomType={outcome.RoomTypeName} "
                    + $"completed={outcome.Completed} turnsPlayed={outcome.TurnsPlayed} cardsPlayed={outcome.CardsPlayed} "
                    + $"relics={outcome.RelicsInjected} potions={outcome.PotionsInjected} deckAdds={outcome.DeckCardsInjected} "
                    + $"upgrades={outcome.CardsUpgraded} enchants={outcome.EnchantsApplied} "
                    + $"restoresAttempted={outcome.RestoresAttempted} restoresFailed={outcome.RestoresFailed}"
                    + (outcome.BudgetExhausted ? " budgetExhausted=true" : "")
                    + (outcome.StuckAfterRestore ? $" stuckAfterRestore=\"{outcome.StuckAfterRestoreDetail}\"" : "")
                    + (outcome.SectionFailures > 0 ? $" sectionFailures={outcome.SectionFailures} (\"{outcome.SectionFailureDetail}\")" : "")
                    + (outcome.DriveError != null ? $" driveError=\"{outcome.DriveError}\"" : ""));
            }
        }
        finally
        {
            // Belt-and-suspenders alongside the per-combat finally in RunOneCombatAsync: whatever
            // happened above, TestMode must come back off. RunManager.InitializeShared
            // (RunManager.cs:473) re-derives ChecksumTracker.IsEnabled from TestMode.IsOn on every
            // future SetUpTest/real run anyway, so resetting this one flag is sufficient — there is no
            // separate ChecksumTracker-level flag that survives to corrupt a later real run.
            TestMode.IsOn = false;
        }

        Log.Write("[Fuzz] ==================== summary ====================");
        Log.Write($"[Fuzz] combats run: {combatCount}, completed cleanly: {completed}, restores attempted: {restoresAttempted}, "
            + $"restores failed: {restoresFailed}, budget exhausted: {budgetExhausted}");
        // This list is the one that matters most: seeds where ChecksumHook.VerifyRestoreFidelity
        // actually returned FAIL. Nothing else belongs in it.
        Log.Write(failingSeeds.Count == 0
            ? "[Fuzz] no restore-fidelity failures."
            : $"[Fuzz] failing combat seeds ({failingSeeds.Count}) — restore fidelity FAILED, investigate: {string.Join(", ", failingSeeds)}");
        // Requirement A: driver couldn't act after a restore within the normal timeout — the shape an
        // action-id-reuse bug would take. A real finding, kept separate from fidelity above.
        Log.Write(stuckAfterRestoreSeeds.Count == 0
            ? "[Fuzz] no stuck-after-restore failures."
            : $"[Fuzz] stuck-after-restore combat seeds ({stuckAfterRestoreSeeds.Count}) — driver could not act after a restore, investigate: {string.Join(", ", stuckAfterRestoreSeeds)}");
        // Requirement B: StateSnapshot.Try / UiRefresh.Section silently swallowed an exception. A real
        // finding, kept separate from fidelity/stuck-after-restore above.
        Log.Write(sectionFailureSeeds.Count == 0
            ? "[Fuzz] no section failures (StateSnapshot.Restore / UiRefresh)."
            : $"[Fuzz] combat seeds with section failures ({sectionFailureSeeds.Count}) — a Restore/UiRefresh section swallowed an exception, investigate: {string.Join(", ", sectionFailureSeeds)}");
        // Separate, deliberately not called "failures": harness/drive-loop trouble (timeouts, stuck
        // waits, setup errors, budget exhaustion) that never got far enough to attempt a fidelity
        // comparison, and isn't a stuck-after-restore or section-failure finding either.
        if (driveErrorSeeds.Count > 0)
            Log.Write($"[Fuzz] combats with other drive errors, not real findings — budget-or-timeout (harness) ({driveErrorSeeds.Count}): {string.Join(", ", driveErrorSeeds)}");

        // "Did this run actually explore anything beyond the untouched starting deck" should be
        // answerable from this one line: characters/roomTypes show the encounter-side spread, the five
        // injected/applied totals show the loadout-side spread (SetUpRandomLoadoutAsync).
        Log.Write($"[Fuzz] coverage: characters {FormatCoverageCounts(characterCounts)} roomTypes {FormatCoverageCounts(roomTypeCounts)} "
            + $"relicsInjected={totalRelicsInjected} potionsInjected={totalPotionsInjected} deckCardsInjected={totalDeckCardsInjected} "
            + $"upgrades={totalCardsUpgraded} enchants={totalEnchantsApplied}");

        Log.Write("[Fuzz] ==================== done ====================");

        // Quit when the run is over. Without this the process sits at the main menu forever, which
        // holds the machine's single game slot and makes "the run finished" indistinguishable from
        // "still running" for anything else waiting on it. Godot's own shutdown (not a kill) so logs
        // flush. Fuzz-only by construction: this method only runs under --undosync-fuzz.
        if (!CommandLineHelper.HasArg("undosync-fuzz-noquit"))
        {
            Log.Write("[Fuzz] quitting the game (pass --undosync-fuzz-noquit to stay open)");
            NGame.Instance?.GetTree()?.Quit();
        }
    }

    /// <summary>Formats a coverage-counting dictionary as "{KEY=N, KEY2=M, ...}", ordered by
    /// descending count then ascending key so the coverage summary line is stable across runs of the
    /// same combatCount even though Dictionary's own enumeration order isn't guaranteed.</summary>
    private static string FormatCoverageCounts(Dictionary<string, int> counts)
    {
        var ordered = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);
        return "{" + string.Join(", ", ordered.Select(kv => $"{kv.Key}={kv.Value}")) + "}";
    }

    /// <summary>
    /// Sets up one throwaway TestMode run, drives one combat to completion (with random restores
    /// along the way), and tears the run back down — regardless of how it went. Never throws: any
    /// failure becomes outcome.DriveError instead, so RunAllCombatsAsync's loop is never at risk.
    /// </summary>
    private static async Task<CombatOutcome> RunOneCombatAsync(int combatIndex, string baseSeed, EncounterModel encounterPrototype, Random rng)
    {
        string combatSeed = $"{baseSeed}-{combatIndex}";
        var outcome = new CombatOutcome { CombatIndex = combatIndex, BaseSeed = baseSeed, Seed = combatSeed, EncounterId = encounterPrototype.Id.Entry };

        // TestMode.TurnOnInternal's own doc comment says "NEVER CALL THIS. Only calls should be in
        // NetCoreRunner and CiCoreRunner" (TestMode.cs:53) — called anyway because this whole file
        // only ever runs opt-in and headless, doing exactly what those runners do to get a usable
        // RunState without a real player driving the main menu.
        TestMode.TurnOnInternal();

        // Declared outside the try so the finally block below can dispose it regardless of how far
        // setup got (installed only partway through the try body, so it may still be null there).
        IDisposable? selectorScope = null;
        try
        {
            // Random character per combat, drawn from the same per-combat `rng` everything else here
            // uses (never the game's own Rng/RunRngSet — see this file's top-of-file "Usage" doc
            // comment). Previously always Ironclad: fuzzing one character out of five left relic/
            // potion/card pools, starting decks, and any character-specific mechanics almost entirely
            // unexercised.
            var characters = ModelDb.AllCharacters.ToList();
            var character = characters[rng.Next(characters.Count)];
            outcome.CharacterId = character.Id.Entry;

            var player = Player.CreateForNewRun(character, UnlockState.all, 1uL);
            var runState = RunState.CreateForTest(
                players: new List<Player> { player },
                // Fixed at 10, not randomized: ascension widens the explored state space (harder
                // monsters, ascension modifiers) without adding a reproduction dimension to failing
                // seeds — "REPRO: baseSeed=... combatIndex=..." stays sufficient to reproduce a
                // failure without also needing to record which ascension level it hit.
                ascensionLevel: 10,
                seed: combatSeed);

            // disableCombatStateSync defaults to true (RunManager.cs:446) — left at its default
            // deliberately, since that's the singleplayer/test posture this harness wants.
            RunManager.Instance.SetUpTest(runState, new NetSingleplayerGameService(), shouldSave: false);
            RunManager.Instance.Launch(); // required — LocalContext.NetId (read by LocalContext.GetMe below) is only set here (RunManager.cs:713)

            // THE one non-obvious step in this whole harness: SetUpTest -> InitializeShared just set
            // ChecksumTracker.IsEnabled = !TestMode.IsOn (RunManager.cs:473), i.e. false, because
            // TestMode.IsOn is now true. GenerateChecksum no-ops entirely while IsEnabled is false
            // (ChecksumTracker.cs:88-91) — without the line below, ChecksumHook would never see a
            // single checksum, would never store a sync point, and there would be nothing to restore
            // or verify for the rest of this combat.
            RunManager.Instance.ChecksumTracker.IsEnabled = true;

            // Probe (trace runs only): ActionQueueSet.ActionEnqueued fires inside
            // EnqueueWithoutSynchronizing BEFORE its cancel gates (ActionQueueSet.cs:118), and
            // ActionExecutor.JustBeforeActionFinishedExecuting is the exact event RunManager hangs
            // the post-action checksum on (RunManager.cs:489). Watching both answers the question
            // source alone could not: does our enqueued PlayCardAction reach the queue, and does it
            // ever finish executing?
            if (TraceChecksums)
            {
                RunManager.Instance.ActionQueueSet.ActionEnqueued += a =>
                    Log.Write($"[Fuzz][probe] enqueued {a.GetType().Name} state={a.State}");
                RunManager.Instance.ActionExecutor.BeforeActionExecuted += a =>
                    Log.Write($"[Fuzz][probe] beforeExec {a.GetType().Name} state={a.State}");
                RunManager.Instance.ActionExecutor.JustBeforeActionFinishedExecuting += a =>
                    Log.Write($"[Fuzz][probe] finished {a.GetType().Name} state={a.State}");
                RunManager.Instance.ActionExecutor.AfterActionExecuted += a =>
                    Log.Write($"[Fuzz][probe] afterExec {a.GetType().Name} state={a.State}");
            }

            // Per-action checksums do not exist on this path, BY DESIGN — and without them UndoSync
            // can never anchor on a card play, which is most of what is worth fuzzing.
            // NonInteractiveMode.IsActive is `TestMode.IsOn || AutoSlayerCheck()`
            // (NonInteractiveMode.cs), so the headless harness always takes ActionExecutor's
            // non-interactive branch (ActionExecutor.cs:140-145), which never subscribes
            // JustBeforeFinished — and JustBeforeFinished is what RunManager.SendPostActionChecksum
            // hangs off (RunManager.cs:489, :568). Turn-boundary checksums still appear only because
            // CombatManager calls GenerateChecksum directly.
            //
            // So the harness generates them itself, mirroring SendPostActionChecksum's own filter
            // exactly (RunManager.cs:568-572). Timing caveat, stated plainly: production fires this
            // from inside GameAction.Execute's finally, immediately after State = Finished
            // (GameAction.cs:143-146); this fires after Execute() returned and the executor called
            // AfterActionFinished (ActionExecutor.cs:144, :211-224). Nothing between those two points
            // mutates combat state — the only way they can differ is if a continuation awaiting the
            // action's completion runs inline in between and touches state.
            RunManager.Instance.ActionExecutor.AfterActionExecuted += GenerateMissingPostActionChecksum;

            // The game's own debug bootstrap (NSceneBootstrapper.StartNewRun, NSceneBootstrapper.cs:104-107)
            // does these three between Launch() and EnterRoomDebug. Skipping them leaves the act
            // unset and both location-targeted routers without a current location, which is the
            // difference between this harness and the sequence the game itself is known to work from.
            await RunManager.Instance.SetActInternal(0);
            RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
            RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);

            // Installed here, before EnterRoomDebug and before SetUpRandomLoadoutAsync below: either
            // one can trigger a card-selection prompt (a randomly-injected relic/card whose effect
            // asks the player to choose N cards, or the encounter's own combat start), and without a
            // selector installed that blocks forever headless — see FuzzCardSelector's doc comment for
            // why this uses a plain System.Random rather than the game's Rng.
            selectorScope = CardSelectCmd.UseSelector(new FuzzCardSelector(rng));

            // Before EnterRoomDebug, not after: relics/potions can carry "at the start of combat"
            // hooks, and those should fire naturally as part of EnterRoomDebug's own combat-start
            // sequence rather than being separately simulated after the fact.
            await SetUpRandomLoadoutAsync(player, character, combatIndex, rng, outcome);

            var mutableEncounter = encounterPrototype.ToMutable();
            // Deliberately NOT calling DebugRandomizeRng() here (contrast FightConsoleCmd.cs:44, which
            // does call it for developer convenience): leaving the encounter's _rng null lets
            // GenerateMonstersWithSlots (EncounterModel.cs:261-264) derive it from
            // runState.Rng.Seed + TotalFloor + the encounter's own id instead — fully determined by
            // combatSeed, so re-running with the same combatSeed reproduces the same monsters/intents.
            //
            // RunManager.EnterRoomDebug overrides the roomType argument below with the encounter's own
            // (`roomType = encounterModel.RoomType;`, RunManager.cs:1100) whenever `model` is an
            // EncounterModel — which mutableEncounter always is here — so passing RoomType.Monster
            // literally stays correct verbatim even now that ResolveEncounterPool's pool includes
            // Elite and Boss encounters too.
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, mutableEncounter, showTransition: false);
            outcome.EncounterId = mutableEncounter.Id.Entry;
            outcome.RoomTypeName = mutableEncounter.RoomType.ToString();

            var me = LocalContext.GetMe(runState);
            if (me == null)
            {
                outcome.DriveError = "LocalContext.GetMe(runState) returned null after Launch";
                Log.Write($"[Fuzz] combat={combatIndex} {outcome.DriveError}");
                return outcome;
            }

            await DriveCombatAsync(combatIndex, me, rng, outcome);
        }
        catch (Exception ex)
        {
            outcome.DriveError = ex.Message;
            Log.Write($"[Fuzz] combat={combatIndex} seed={combatSeed} encounter={outcome.EncounterId} SETUP/DRIVE ERROR: {ex}");
        }
        finally
        {
            try
            {
                // CleanUp() no-ops when State is already null (RunManager.cs:1548), so this is safe to
                // call unconditionally regardless of how far setup got.
                RunManager.Instance.CleanUp();
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} CleanUp ERROR: {ex}");
            }
            // Disposed here, before TestMode.IsOn is cleared below (not after): CardSelectCmd.Reset —
            // called from inside RunManager.CleanUp() just above, and reachable from other cleanup
            // paths outside this file's control — only force-clears the stack (and warns about a
            // "leaked selector") when !TestMode.IsOn (CardSelectCmd.cs:164-168). Disposing our own
            // scope while TestMode.IsOn is still true lets it clear the stack itself, quietly and on
            // purpose, instead of a later Reset() call mistaking our own selector for a leak — and
            // without disposing at all, the next combat's UseSelector() call would throw
            // "A card selector is already active." (CardSelectCmd.cs:180-183).
            try
            {
                selectorScope?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} selector scope Dispose ERROR: {ex}");
            }
            // Reset per-combat (not only once at the very end of the whole run) so a mid-run crash on
            // combat N can never leave TestMode on for combat N+1's setup too.
            TestMode.IsOn = false;
        }
        return outcome;
    }

    /// <summary>
    /// Races `task` against LoadoutStepTimeout. Returns true once `task` itself completes — the
    /// trailing `await task` (rather than just checking Task.WhenAny's result) lets any exception
    /// `task` throws propagate out to the caller's own try/catch, so callers get uniform exception
    /// handling whether the step failed fast or had to be guarded against a hang. Returns false
    /// immediately on timeout, after logging loudly.
    ///
    /// Known, accepted caveat: on timeout the underlying `task` is NOT cancelled — none of
    /// RelicCmd.Obtain/PotionCmd.TryToProcure/CardPileCmd.Add take a CancellationToken to give it — so
    /// it keeps running in the background and may complete later against a run this combat's finally
    /// block has already torn down via RunManager.Instance.CleanUp(). Accepted because this path
    /// should never actually trigger (every awaited step here is a plain state mutation, not something
    /// that should ever block headless) and the only alternative is letting a genuine hang stall the
    /// whole fuzz run indefinitely.
    /// </summary>
    private static async Task<bool> AwaitWithTimeoutAsync(Task task, string what, int combatIndex)
    {
        var finished = await Task.WhenAny(task, Task.Delay(LoadoutStepTimeout));
        if (finished != task)
        {
            Log.Write($"[Fuzz] combat={combatIndex} loadout: {what} TIMED OUT after {LoadoutStepTimeout.TotalSeconds:F0}s — abandoning this step (the task is left running in the background rather than cancelled, see AwaitWithTimeoutAsync's doc comment).");
            return false;
        }
        await task; // already completed; this exists only so a faulted task rethrows to the caller
        return true;
    }

    /// <summary>
    /// Randomizes this combat's starting loadout — relics, potions, extra deck cards, upgrades,
    /// enchantments — all driven off `rng` (never the game's own Rng/RunRngSet, same reasoning as
    /// everywhere else in this file). Called from RunOneCombatAsync after the location-router calls
    /// and after the card selector is installed, but before EnterRoomDebug (see the call site's own
    /// comment for why).
    ///
    /// Every individual injection (one relic, one potion, one deck card, one upgrade, one enchantment)
    /// is independently wrapped in try/catch and logs+continues on failure: this method exists
    /// specifically to exercise models the fuzzer previously never touched, so one bad/incompatible
    /// model throwing must never abort the rest of the loadout, let alone the combat. Every awaited
    /// step is also guarded by AwaitWithTimeoutAsync, since CombatWallClockTimeout only covers
    /// DriveCombatAsync and this method runs entirely before that.
    /// </summary>
    private static async Task SetUpRandomLoadoutAsync(Player player, CharacterModel character, int combatIndex, Random rng, CombatOutcome outcome)
    {
        // --- Relics --------------------------------------------------------------------------------
        var relicPool = ModelDb.AllRelics.ToList();
        int relicCount = rng.Next(0, 5); // 0-4 inclusive
        for (int i = 0; i < relicCount && relicPool.Count > 0; i++)
        {
            int idx = rng.Next(relicPool.Count);
            var model = relicPool[idx];
            relicPool.RemoveAt(idx); // draw without replacement, independent of the ownership check below

            try
            {
                if (player.GetRelicById(model.Id) != null)
                    continue; // already owned (e.g. a starting relic) — nothing to inject

                var obtainTask = RelicCmd.Obtain(model.ToMutable(), player);
                if (!await AwaitWithTimeoutAsync(obtainTask, $"relic '{model.Id.Entry}'", combatIndex))
                    continue;
                outcome.RelicsInjected++;
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} loadout: relic '{model.Id.Entry}' FAILED: {ex.Message}");
            }
        }

        // --- Potions -------------------------------------------------------------------------------
        var potionPool = ModelDb.AllPotions.ToList();
        int potionCount = rng.Next(0, player.MaxPotionCount + 1);
        for (int i = 0; i < potionCount && potionPool.Count > 0; i++)
        {
            int idx = rng.Next(potionPool.Count);
            var model = potionPool[idx];
            potionPool.RemoveAt(idx);

            try
            {
                var procureTask = PotionCmd.TryToProcure(model.ToMutable(), player);
                if (!await AwaitWithTimeoutAsync(procureTask, $"potion '{model.Id.Entry}'", combatIndex))
                    continue;
                if (procureTask.Result.success) // can fail if the potion bar is full (PotionCmd.cs:22)
                    outcome.PotionsInjected++;
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} loadout: potion '{model.Id.Entry}' FAILED: {ex.Message}");
            }
        }

        // --- Deck cards ----------------------------------------------------------------------------
        if (RunManager.Instance.DebugOnlyGetState() is not ICardScope scope)
        {
            Log.Write($"[Fuzz] combat={combatIndex} loadout: RunManager.Instance.DebugOnlyGetState() returned null — skipping deck-card injection");
        }
        else
        {
            var cardPool = character.CardPool.AllCards.ToList();
            int cardCount = rng.Next(0, 9);
            for (int i = 0; i < cardCount && cardPool.Count > 0; i++)
            {
                int idx = rng.Next(cardPool.Count);
                var model = cardPool[idx];
                cardPool.RemoveAt(idx);

                try
                {
                    var card = scope.CreateCard(model, player);
                    var addTask = CardPileCmd.Add(card, PileType.Deck);
                    if (!await AwaitWithTimeoutAsync(addTask, $"deck card '{model.Id.Entry}'", combatIndex))
                        continue;
                    if (addTask.Result.success)
                        outcome.DeckCardsInjected++;
                }
                catch (Exception ex)
                {
                    Log.Write($"[Fuzz] combat={combatIndex} loadout: deck card '{model.Id.Entry}' FAILED: {ex.Message}");
                }
            }
        }

        // --- Upgrades + enchantments -----------------------------------------------------------------
        // Snapshot first: CardCmd.Upgrade/Enchant mutate the CardModel in place, and
        // PileType.Deck.GetPile(player).Cards is the live IReadOnlyList backing the pile we'd be
        // mutating while iterating it, not a copy.
        var deckSnapshot = PileType.Deck.GetPile(player).Cards.ToList();

        foreach (var card in deckSnapshot)
        {
            if (rng.Next(100) >= 30) continue; // ~30% upgrade chance
            try
            {
                // Same guard UpgradeCardConsoleCmd.cs uses before calling CardCmd.Upgrade.
                if (card.CurrentUpgradeLevel < card.MaxUpgradeLevel)
                {
                    CardCmd.Upgrade(card); // synchronous, no await — no timeout guard needed
                    outcome.CardsUpgraded++;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} loadout: upgrade '{card.Id.Entry}' FAILED: {ex.Message}");
            }
        }

        var enchantments = DebugEnchantmentsCache;
        if (enchantments.Count > 0)
        {
            foreach (var card in deckSnapshot)
            {
                if (rng.Next(100) >= 15) continue; // ~15% enchant chance
                var enchantment = enchantments[rng.Next(enchantments.Count)];
                try
                {
                    // Always check CanEnchant first: CardCmd.Enchant throws InvalidOperationException,
                    // rather than returning null/false, both when CanEnchant is false and when the
                    // card already carries a different enchantment (CardCmd.cs:536-553).
                    if (enchantment.CanEnchant(card))
                    {
                        CardCmd.Enchant(enchantment.ToMutable(), card, 1m); // synchronous, no await
                        outcome.EnchantsApplied++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[Fuzz] combat={combatIndex} loadout: enchant '{enchantment.Id.Entry}' on '{card.Id.Entry}' FAILED: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Result of waiting for an actionable idle player-turn state — see
    /// WaitForIdleOurTurnAsync.</summary>
    private enum IdleWait { Ready, CombatEnded, TimedOut }

    /// <summary>Set by WaitForIdleOurTurnAsync immediately before it returns TimedOut, to
    /// "phase={...} blockers=[{UndoSyncMod.DescribeUndoRedoBlockers()}]" — cleared back to "" on every
    /// Ready/CombatEnded return. DriveCombatAsync reads this right after a TimedOut result so both of
    /// its "stuck" log lines can say WHICH condition was still blocking, not just that something was.
    /// </summary>
    private static string _lastIdleWaitBlockers = "";

    /// <summary>
    /// Polls until either combat ends, or the game reaches a state where it is genuinely safe/our turn
    /// to act: UndoSyncMod.CanUndoRedo() (UndoSync/UndoSyncMod.cs:44-66 — not mid-restore, combat in
    /// progress, CombatState.CurrentSide == Player, synchronizer PlayPhase, ActionQueueSet.IsEmpty,
    /// card-play visual queue drained, not mid-transition; the same condition UndoProtocol.CommitAsync
    /// polls inline at UndoProtocol.cs:392-395 before a human-triggered restore) PLUS
    /// PlayerCombatState.Phase == Play for `me` specifically. The Phase check is needed on top of
    /// CanUndoRedo because CanUndoRedo only inspects CombatState.CurrentSide, not any individual
    /// player's own phase-machine position — Play is the phase TryManualPlay/EndTurn are legal in
    /// (NCardPlay.cs:225/241 call TryManualPlay from the UI; PlayerCmd.EndTurn's IsPlayerReadyToEndTurn
    /// guard, PlayerCmd.cs:280, is keyed off the same phase machine).
    ///
    /// One helper because every wait this driver needs — top of iteration, after TryManualPlay
    /// enqueues a PlayCardAction, after PlayerCmd.EndTurn — is this same condition: "the queue drained
    /// and it's our turn to act again". TryManualPlay only ENQUEUES (CardModel.cs:1797-1801,
    /// RequestEnqueue) rather than resolving synchronously, so the next loop iteration's call to this
    /// method IS the wait for that action to actually finish — reaching Ready again is the
    /// confirmation it executed, not just that it was accepted.
    ///
    /// Bound is wall-clock (IdleWaitTimeout, via a Stopwatch), not a poll count. MEASURED: the
    /// previous bound was MaxConsecutiveIdlePolls = 4000 at IdlePollInterval = 10ms, documented in a
    /// comment as "~40s" — a 250-combat headless fuzz run instead showed 4 stalls each actually taking
    /// 133.5s to hit that poll count, because `await Task.Delay(10)` does not resolve in 10ms under
    /// this workload. A poll-count bound's real-world duration depends entirely on how long each
    /// individual delay actually takes; a Stopwatch bounds the real elapsed time directly regardless.
    ///
    /// On timeout, stashes a snapshot of what's still blocking into <see cref="_lastIdleWaitBlockers"/>
    /// (via UndoSyncMod.DescribeUndoRedoBlockers()) before returning — otherwise a timeout only ever
    /// logs "stuck", with no way to tell which of CanUndoRedo's conditions was still false.
    ///
    /// Deliberately does NOT check the overall combat wall-clock itself (DriveCombatAsync's own loop
    /// does that once per iteration with a distinct, unambiguous message) — this keeps a plain
    /// wall-clock exhaustion from ever being misattributed as a stuck-after-restore finding.
    /// </summary>
    private static async Task<IdleWait> WaitForIdleOurTurnAsync(Player me)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!CombatManager.Instance.IsInProgress)
            {
                _lastIdleWaitBlockers = "";
                return IdleWait.CombatEnded;
            }
            var pcs = me.PlayerCombatState;
            if (pcs != null && pcs.Phase == PlayerTurnPhase.Play && UndoSyncMod.CanUndoRedo())
            {
                _lastIdleWaitBlockers = "";
                return IdleWait.Ready;
            }
            if (sw.Elapsed > IdleWaitTimeout)
            {
                _lastIdleWaitBlockers = $"phase={me.PlayerCombatState?.Phase.ToString() ?? "null"} blockers=[{UndoSyncMod.DescribeUndoRedoBlockers()}]";
                return IdleWait.TimedOut;
            }
            await Task.Delay(IdlePollInterval);
        }
    }

    /// <summary>
    /// Drives one already-entered combat to completion, one action at a time — like a human: issue one
    /// action (a card play via CardModel.TryManualPlay, or PlayerCmd.EndTurn), then wait via
    /// WaitForIdleOurTurnAsync for it to actually resolve before touching hand/state again. Never reads
    /// the hand again immediately after enqueueing a play — that was the previous driver's core
    /// mistake with CardCmd.AutoPlay (see the file's top-of-file doc comment for the full diagnosis).
    ///
    /// Tracks `watchingRestore`/`restoreActionPending` across iterations to implement the
    /// "progress after restore" assertion (requirement A): once TryAttemptRestore performs a restore,
    /// the very next successful action (a TryManualPlay that returns true, or an EndTurn call) must
    /// resolve — confirmed by the following WaitForIdleOurTurnAsync returning Ready — within the normal
    /// timeout, or the combat is flagged StuckAfterRestore instead of a generic timeout. This is
    /// exactly the shape an action-id-reuse bug in ChecksumHook.RestoreTo's FastForward* calls would
    /// take: the queue looks idle right after the restore, but a newly-enqueued action with a
    /// colliding id never actually drains.
    /// </summary>
    private static async Task DriveCombatAsync(int combatIndex, Player me, Random rng, CombatOutcome outcome)
    {
        var sw = Stopwatch.StartNew();
        int restoreSectionFailuresBefore = StateSnapshot.RestoreSectionFailureCount;
        int uiRefreshFailuresBefore = UiRefresh.UiRefreshFailureCount;

        try
        {
            // EnterRoomDebug's own await chain can return slightly before CombatManager flips
            // IsInProgress true: "True from the start of SetUpCombat until IsInProgress flips true in
            // StartCombatInternal" (CombatManager.cs:180-181). Poll briefly rather than assume.
            while (!CombatManager.Instance.IsInProgress && sw.Elapsed < CombatStartTimeout)
                await Task.Delay(IdlePollInterval);
            if (!CombatManager.Instance.IsInProgress)
            {
                outcome.DriveError = $"combat never reached IsInProgress within {CombatStartTimeout.TotalSeconds}s of EnterRoomDebug returning";
                Log.Write($"[Fuzz] combat={combatIndex} {outcome.DriveError}");
                return;
            }

            // MEASURED ROOT CAUSE of this harness never producing a single card-play anchor:
            // 35 PlayCardActions reached the queue as WaitingForExecution and NOT ONE ever raised
            // ActionExecutor.JustBeforeActionFinishedExecuting — the executor simply never ran them.
            // InitializeShared leaves it paused (RunManager.cs:491) and only CombatManager unpauses it
            // (CombatManager.cs:579, :831, :1353); the headless EnterRoomDebug entry does not pass
            // those call sites. NSceneBootstrapper hits the same thing and unpauses by hand for its
            // non-combat rooms (NSceneBootstrapper.cs:117). IsPaused/Unpause are public
            // (ActionExecutor.cs:31, :86).
            if (RunManager.Instance.ActionExecutor.IsPaused)
            {
                Log.Write($"[Fuzz] combat={combatIndex} ActionExecutor was paused after combat start — unpausing (queued actions would never execute otherwise)");
                RunManager.Instance.ActionExecutor.Unpause();
            }

            int actionBudget = ActionBudgetPerCombat;

            // Set from the moment a restore happens until the first action issued afterward is
            // confirmed to have actually resolved. restoreActionPending distinguishes "still waiting
            // to even settle back into an idle state after the restore" from "issued an action, still
            // waiting for it to resolve" — both count as stuck-after-restore if they time out, but the
            // log message differs so a human can tell which shape the failure took.
            bool watchingRestore = false;
            bool restoreActionPending = false;
            string restoreWatchDesc = "";

            while (CombatManager.Instance.IsInProgress)
            {
                if (sw.Elapsed > CombatWallClockTimeout)
                {
                    outcome.DriveError = $"combat exceeded {CombatWallClockTimeout.TotalSeconds}s wall clock — abandoning";
                    Log.Write($"[Fuzz] combat={combatIndex} TIMEOUT: {outcome.DriveError}");
                    return;
                }
                if (actionBudget <= 0)
                {
                    outcome.DriveError = $"action budget exhausted ({ActionBudgetPerCombat} card-plays/end-turns) — abandoning";
                    outcome.BudgetExhausted = true;
                    Log.Write($"[Fuzz] combat={combatIndex} BUDGET EXHAUSTED (turnsPlayed={outcome.TurnsPlayed}, cardsPlayed={outcome.CardsPlayed}, restoresAttempted={outcome.RestoresAttempted}) — this is a harness/drive-loop concern, not a restore-fidelity finding");
                    return;
                }

                var wait = await WaitForIdleOurTurnAsync(me);
                if (wait == IdleWait.CombatEnded) break;
                if (wait == IdleWait.TimedOut)
                {
                    if (watchingRestore)
                    {
                        outcome.StuckAfterRestore = true;
                        outcome.StuckAfterRestoreDetail = $"{restoreWatchDesc} | {_lastIdleWaitBlockers}";
                        string shape = restoreActionPending
                            ? "an action was issued after the restore but its queue never drained"
                            : "the driver never returned to an idle actionable state after the restore";
                        Log.Write($"[Fuzz] combat={combatIndex} STUCK AFTER RESTORE — {shape} -> {restoreWatchDesc} | {_lastIdleWaitBlockers}. This is the shape an action-id-reuse bug (FastForwardNextActionId/FastForwardHookId) would take.");
                    }
                    else
                    {
                        outcome.DriveError = $"stuck waiting for idle player Play phase | {_lastIdleWaitBlockers}";
                        Log.Write($"[Fuzz] combat={combatIndex} STUCK: {outcome.DriveError}");
                    }
                    return;
                }

                // Ready. If we were watching for the first post-restore action to land and one was
                // actually issued, it just resolved — draining is the confirmation (see this method's
                // and WaitForIdleOurTurnAsync's doc comments). Progress confirmed; clear the watch.
                if (watchingRestore && restoreActionPending)
                {
                    watchingRestore = false;
                    restoreActionPending = false;
                }

                var restored = TryAttemptRestore(combatIndex, rng, outcome);
                if (restored != null)
                {
                    watchingRestore = true;
                    restoreActionPending = false;
                    restoreWatchDesc = $"id={restored.ChecksumId} ({restored.Context})";
                    continue; // re-settle via the same wait above before touching hand/cards
                }

                var pile = PileType.Hand.GetPile(me);
                var candidates = pile.Cards.Where(c => c.CanPlay(out _, out _)).ToList();

                bool acted = false;
                while (candidates.Count > 0)
                {
                    int idx = rng.Next(candidates.Count);
                    var card = candidates[idx];
                    var target = PickRandomTarget(card, rng);
                    bool played;
                    try
                    {
                        // The real player-input path (verified against decompiled/): CardModel.
                        // TryManualPlay (CardModel.cs:1787-1795) checks CanPlayTargeting(target) then
                        // EnqueueManualPlay -> RunManager.Instance.ActionQueueSynchronizer.
                        // RequestEnqueue(new PlayCardAction(...)) (CardModel.cs:1797-1801) — exactly
                        // what the UI calls (NCardPlay.cs:225 targeted, :241 untargeted). Unlike
                        // CardCmd.AutoPlay (the "play this card for free" helper — its last lines
                        // build ResourceInfo{EnergySpent=0,...} and call OnPlayWrapper(...,
                        // isAutoPlay: true, ...), CardCmd.cs:122-131), this goes through the action
                        // queue: resources are actually spent and a "finished action execution
                        // PlayCardAction ..." checksum fires, which is what ChecksumHook anchors sync
                        // points on.
                        played = card.TryManualPlay(target);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Fuzz] combat={combatIndex} CardModel.TryManualPlay({card.Id.Entry}) THREW: {ex}");
                        played = false;
                    }
                    if (played)
                    {
                        outcome.CardsPlayed++;
                        actionBudget--;
                        acted = true;
                        break;
                    }
                    // Looked playable a moment ago (CanPlay) but CanPlayTargeting said no at the
                    // moment of the actual attempt (e.g. its only legal target stopped being
                    // hittable) — exclude it and try the next candidate rather than retrying forever.
                    candidates.RemoveAt(idx);
                }

                if (!acted)
                {
                    PlayerCmd.EndTurn(me, canBackOut: false);
                    outcome.TurnsPlayed++;
                    actionBudget--;
                }
                if (watchingRestore)
                    restoreActionPending = true;
                // No explicit wait here: TryManualPlay only enqueues, it doesn't resolve the card
                // synchronously, and EndTurn just flips a ready flag. The very next loop iteration's
                // WaitForIdleOurTurnAsync above is what waits for that PlayCardAction (or the
                // end-turn's enemy-turn transition) to actually finish.
            }

            outcome.Completed = true;
            Log.Write($"[Fuzz] combat={combatIndex} finished in {sw.Elapsed.TotalSeconds:F1}s "
                + $"(turnsPlayed={outcome.TurnsPlayed}, cardsPlayed={outcome.CardsPlayed}, "
                + $"restoresAttempted={outcome.RestoresAttempted}, restoresFailed={outcome.RestoresFailed})");
        }
        finally
        {
            // Requirement B: StateSnapshot.Restore()'s per-section Try() and UiRefresh.RefreshAll()'s
            // per-section Section() both swallow exceptions into a log line so one broken section
            // can't abort the rest of a restore — but that means a silently-broken section would
            // otherwise never surface here. Both increment a counter from inside their own catch block
            // (the source of truth) rather than this file re-scanning the log. Checked in a finally so
            // every exit path from this method (natural completion, any early return above, or an
            // uncaught exception) still gets the delta applied to `outcome`.
            int restoreDelta = StateSnapshot.RestoreSectionFailureCount - restoreSectionFailuresBefore;
            int uiDelta = UiRefresh.UiRefreshFailureCount - uiRefreshFailuresBefore;
            if (restoreDelta > 0 || uiDelta > 0)
            {
                outcome.SectionFailures = restoreDelta + uiDelta;
                var parts = new List<string>();
                if (restoreDelta > 0) parts.Add($"Restore:'{StateSnapshot.LastFailedRestoreSection}'x{restoreDelta}");
                if (uiDelta > 0) parts.Add($"UiRefresh:'{UiRefresh.LastFailedUiRefreshSection}'x{uiDelta}");
                outcome.SectionFailureDetail = string.Join(", ", parts);
                Log.Write($"[Fuzz] combat={combatIndex} SECTION FAILURE(S) — {outcome.SectionFailureDetail} (see 'FAILED' lines above for the full exception text)");
            }
        }
    }

    /// <summary>Mirrors CombatRoomHandler.GetRandomTarget (CombatRoomHandler.cs:124-141): only
    /// AnyEnemy cards need an explicit target here, and only among currently-hittable enemies.</summary>
    private static Creature? PickRandomTarget(CardModel card, Random rng)
    {
        if (card.TargetType != TargetType.AnyEnemy) return null;
        var combatState = card.CombatState;
        if (combatState == null) return null;
        var enemies = combatState.HittableEnemies.ToList();
        return enemies.Count == 0 ? null : enemies[rng.Next(enemies.Count)];
    }

    /// <summary>
    /// With low probability (RestoreProbability), and only when all of the pacing gates below say
    /// it's safe/worthwhile, picks a uniformly random *older* stored sync point and restores straight
    /// to it via ChecksumHook.RestoreTo — bypassing UndoPicker/UndoProtocol entirely, since this is
    /// singleplayer with no UI and no vote to run. Records the pass/fail via
    /// ChecksumHook.LastRestoreFidelityOk.
    ///
    /// Returns the SyncPoint that was actually restored to (regardless of whether its fidelity check
    /// passed or failed — a restore happened either way, and the caller needs to know that to run the
    /// "progress after restore" watch), or null if no restore was attempted this call.
    ///
    /// Gates, cheapest/most-deterministic first (each one skipping avoids burning an rng.NextDouble()
    /// draw on a restore that couldn't happen anyway):
    ///   1. MaxRestoresPerCombat — a small fixed cap, so a handful of restores spread across many
    ///      combats explores far more than piling them all into one.
    ///   2. MinTurnsBetweenRestores — at least one full end-turn issued since the last restore, so
    ///      back-to-back restores at the same unmoved decision point can't happen.
    ///   3. The RestoreProbability roll itself.
    ///   4. UndoSyncMod.CanUndoRedo() — the same idle/safety gate a human-triggered undo uses.
    /// </summary>
    private static SyncPoint? TryAttemptRestore(int combatIndex, Random rng, CombatOutcome outcome)
    {
        if (outcome.RestoresAttempted >= MaxRestoresPerCombat) return null;
        if (outcome.TurnsPlayed - outcome.LastRestoreTurnMark < MinTurnsBetweenRestores) return null;
        if (rng.NextDouble() >= RestoreProbability) return null;
        if (!UndoSyncMod.CanUndoRedo()) return null;

        // SyncPointsNewestFirst (index 0 = current state, i.e. "now") plus the separate combat-start
        // anchor (ChecksumHook.cs:45-50, kept outside SyncPoints so MaxSyncPoints trimming can't lose
        // it) — both are legitimate RestoreTo targets, so both are fair game to fuzz. Deduped by
        // checksum id: TryStoreSyncPoint stores the combat's very first turn-start point in BOTH
        // places (ChecksumHook.cs:333-337) whenever it hasn't been trimmed out of SyncPoints yet, so
        // without this check that one anchor would be counted — and therefore picked — twice as often
        // as every other candidate. Combat-start's id is always <= every id in SyncPoints (it's the
        // earliest point of the whole combat), so appending it can never make it "the newest".
        var stored = ChecksumHook.SyncPointsNewestFirst();
        if (ChecksumHook.TryGetCombatStart(out var startPoint) && stored.All(sp => sp.ChecksumId != startPoint.ChecksumId))
            stored.Add(startPoint);

        // Need at least one candidate besides "now" — restoring to the current state tests nothing.
        if (stored.Count < 2) return null;

        // Uniformly random among everything OLDER than "now", not always the single oldest anchor —
        // now that anchors include card plays (via TryManualPlay's PlayCardAction), this exercises
        // every anchor kind, not just "After player turn start". stored[0] is newest-first, so
        // Skip(1) drops exactly "now" and nothing else.
        var candidates = stored.Skip(1).ToList();
        var target = candidates[rng.Next(candidates.Count)];

        outcome.RestoresAttempted++;
        outcome.LastRestoreTurnMark = outcome.TurnsPlayed;
        ChecksumHook.RestoreTo(target); // logs its own RESTORE/RESTORE FIDELITY lines, including the line-diff on failure

        if (ChecksumHook.LastRestoreFidelityOk)
        {
            Log.Write($"[Fuzz] combat={combatIndex} restore -> id={target.ChecksumId} ({target.Context}): OK");
            return target;
        }

        outcome.RestoresFailed++;
        string repro = $"REPRO: baseSeed={outcome.BaseSeed} combatIndex={combatIndex} combatSeed={outcome.Seed} "
            + $"encounter={outcome.EncounterId} anchorChecksumId={target.ChecksumId} anchorContext=\"{target.Context}\"";
        outcome.FailureRepros.Add(repro);
        Log.Write($"[Fuzz] combat={combatIndex} RESTORE FIDELITY FAILURE — {repro}");
        Log.Write("[Fuzz] (full line-diff logged immediately above by ChecksumHook.RestoreTo/VerifyRestoreFidelity)");
        // A restore DID happen even though fidelity failed — the driver must still be able to act
        // afterward; that's a distinct concern from fidelity (requirement A), so still return target.
        return target;
    }

    // ==================================================================================
    // Encounter pool
    // ==================================================================================

    /// <summary>
    /// Pool of encounters EnterRoomDebug can fight, resolved live from ModelDb rather than a hard-coded
    /// list of ids:
    ///   (a) the pool is derived from the game's own model db, so a content update (new/removed/
    ///       renamed encounters) automatically widens or narrows it instead of a hard-coded list
    ///       silently drifting out of date or, worse, quietly resolving to nothing;
    ///   (b) event encounters (ModelDb.EventEncounters) are excluded — they're reachable only through
    ///       event flow (e.g. choosing "Start a Fight!"; see RoomType's own doc comment on the
    ///       MapPointType/RoomType distinction), not through a plain EnterRoomDebug the way a
    ///       Monster/Elite/Boss encounter is;
    ///   (c) the explicit OrderBy on the encounter's own id makes pickRng.Next(pool.Count) in
    ///       RunAllCombatsAsync reproducible for a given (baseSeed, combatIndex) even if ModelDb's own
    ///       internal enumeration order ever changes (e.g. a Concat/Distinct implementation detail) —
    ///       without a stable order, "REPRO: baseSeed=... combatIndex=..." in a failure log could stop
    ///       reproducing the same encounter across two runs of the mod on different game builds.
    /// </summary>
    /// <summary>
    /// Deterministic 32-bit seed for a combat's harness RNG.
    ///
    /// This exists because the obvious `HashCode.Combine(baseSeed, i)` is NOT reproducible across
    /// processes: .NET randomizes String.GetHashCode per process by default, so every fuzz run would
    /// derive a different pickRng from the same --undosync-fuzz-seed, and the "REPRO: baseSeed=...
    /// combatIndex=..." line this harness prints on every failure would hand back a DIFFERENT combat.
    /// That was measured, not assumed: re-running seed "widen1" after a fix turned combat 24 from
    /// QUEEN_BOSS/Ironclad into SEAPUNK_NORMAL/Defect, which would have made a genuine regression look
    /// fixed. FNV-1a over the UTF-16 code units is stable across processes, machines and runtimes.
    /// </summary>
    private static int DeterministicSeed(string s)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }

    private static List<EncounterModel> ResolveEncounterPool()
    {
        var eventEncounters = ModelDb.EventEncounters.ToHashSet();
        var pool = ModelDb.AllEncounters
            .Where(e => !eventEncounters.Contains(e))
            .Where(e => e.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss)
            .OrderBy(e => e.Id.Entry, StringComparer.Ordinal)
            .ToList();

        var byRoomType = pool.GroupBy(e => e.RoomType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        Log.Write($"[Fuzz] encounter pool: {pool.Count} total, by room type: {{{string.Join(", ", byRoomType)}}}");
        return pool;
    }
}
