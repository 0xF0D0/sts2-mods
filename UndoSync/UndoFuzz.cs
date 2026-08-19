using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
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
        /// procured, card actually added to the deck pile, upgrade actually applied), never attempts.
        /// Surfaced in both the per-combat log line and the run-level coverage summary so "did this run
        /// actually explore anything beyond the untouched starting deck" is answerable from the log
        /// alone. No EnchantsApplied counter: see SetUpRandomLoadoutAsync's enchantment comment block
        /// for why there is no enchantment injection step left to count.</summary>
        public int RelicsInjected;
        public int PotionsInjected;
        public int DeckCardsInjected;
        public int CardsUpgraded;

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

        /// <summary>True when at least one game-side Log.Error call was observed during this combat —
        /// captured via the fuzz-only Harmony prefix on MegaCrit.Sts2.Core.Logging.Log.Error (see
        /// InstallGameErrorCapturePatch/OnGameLogError). Set in RunOneCombatAsync right after
        /// DriveCombatAsync returns, from whether _gameErrors is non-empty (cleared at the top of
        /// RunOneCombatAsync so a prior combat's errors can never leak into this flag). These are the
        /// GAME's own errors, not an UndoSync/ChecksumHook finding — most commonly
        /// CombatManager.RunTurnLoopAfter's turn-loop death (CombatManager.cs:516-528), which
        /// production only ever reports through Log.Error/Sentry with no public flag or event.</summary>
        public bool SawGameError;

        /// <summary>True when the GAME's own combat turn loop died (CombatManager.RunTurnLoopAfter,
        /// CombatManager.cs:516-528) — set by DriveCombatAsync when WaitForIdleOurTurnAsync returns
        /// IdleWait.GameTurnLoopDied, i.e. _gameTurnLoopDied was seen set by RecordGameError. This
        /// combat can never complete: the game's own turn loop is gone, so nothing the driver does from
        /// here on can ever be picked up again. Deliberately NOT an UndoSync/ChecksumHook finding (this
        /// combat never got as far as attempting or verifying a restore) and deliberately distinct from
        /// both StuckAfterRestore (a driver that COULD still act but didn't within the timeout) and a
        /// generic DriveError (budget/wall-clock/setup) — see RunAllCombatsAsync's summary for how each
        /// is kept out of the others' seed lists.</summary>
        public bool TurnLoopDied;

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
    /// Ring buffer of game-side error text captured via <see cref="OnGameLogError"/> — the Harmony
    /// prefix on MegaCrit.Sts2.Core.Logging.Log.Error installed only under --undosync-fuzz (see
    /// <see cref="InstallGameErrorCapturePatch"/>). Single-threaded harness (everything in this file
    /// runs on the one task chain kicked off from RunWhenReadyAsync), so no lock is needed. Cleared at
    /// the start of every combat in RunOneCombatAsync so an error from combat N-1's own turn loop can
    /// never be attributed to combat N.
    /// </summary>
    private static readonly List<string> _gameErrors = new();

    /// <summary>Cap on <see cref="_gameErrors"/> — oldest entries are trimmed from the front once
    /// exceeded. Only a diagnostic aid for the stall-dump/summary surfacing (DescribeStallState,
    /// RunAllCombatsAsync's summary): the full, untruncated text of every capture already went to the
    /// mod log via Log.Write at capture time — see RecordGameError — so this only bounds how much
    /// stays resident in memory.</summary>
    private const int MaxCapturedGameErrors = 20;

    /// <summary>
    /// Set true by <see cref="RecordGameError"/> when the captured text is CombatManager.
    /// RunTurnLoopAfter's own report of its turn loop dying (CombatManager.cs:521) — "the combat is
    /// stuck until the room is restarted" is the game's own diagnosis, reported ONLY via Log.Error/
    /// Sentry with no public flag or event otherwise. Checked at the top of every
    /// WaitForIdleOurTurnAsync poll iteration, before the normal IsInProgress check, so DriveCombatAsync
    /// finds out within one IdlePollInterval instead of burning the full IdleWaitTimeout waiting on a
    /// combat that can never move again — the game's own turn loop is gone, so nothing this driver does
    /// (a card play, an end-turn) can ever be picked up again. Reset to false in RunOneCombatAsync at
    /// the exact place <see cref="_gameErrors"/> is already cleared, so a death in combat N-1 can never
    /// be attributed to combat N.
    /// </summary>
    private static bool _gameTurnLoopDied;

    /// <summary>
    /// Installs a Harmony PREFIX on MegaCrit.Sts2.Core.Logging.Log.Error(string text, int
    /// skipFrames = 2) (Log.cs:75) so every game-side error the fuzzer's combats provoke lands in
    /// this fuzz log instead of only in Godot's own separate stdout log file.
    ///
    /// Why this exists: CombatManager.RunTurnLoopAfter's catch block (CombatManager.cs:516-528) is
    /// the game's only handling for its own turn loop dying — "the combat is stuck until the room is
    /// restarted" — and it reports that ONLY via Log.Error and Sentry; there is no public flag or
    /// event exposing it. Without this patch, a stalled fuzz combat showed nothing but "stuck" in the
    /// fuzz log, while the actual exception/stack trace sat in a different log file that nobody
    /// investigating a stall would think to open.
    ///
    /// Gated on --undosync-fuzz ONLY: called from MaybeStart, right after its
    /// CommandLineHelper.HasArg(FuzzArg) check, so this patch is never applied in a normal player's
    /// game. Deliberately NOT a [HarmonyPatch] attribute class (unlike ChecksumHook/UndoSyncMod's
    /// patches) — those get swept up by UndoSyncMod.Initialize()'s harmony.PatchAll(...) and would
    /// therefore patch the game's own logger for every player, not just this opt-in fuzz tool.
    ///
    /// Uses its own Harmony instance, not UndoSyncMod's: the Harmony("com.beomsu.undosync") instance
    /// UndoSyncMod.Initialize() creates is a local variable there, not stored anywhere this file can
    /// reach, so a second instance ("undosync.fuzz") is created here for this fuzz-only patch.
    ///
    /// The patch is a passive observer ONLY, never suppressing the original call — see
    /// OnGameLogError's doc comment for why. Wrapped in try/catch: a diagnostic capture must never be
    /// able to break the run it exists to help diagnose.
    /// </summary>
    private static void InstallGameErrorCapturePatch()
    {
        try
        {
            var harmony = new Harmony("undosync.fuzz");
            var original = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Logging.Log), "Error");
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(UndoFuzz), nameof(OnGameLogError)));
            harmony.Patch(original, prefix: prefix);
            Log.Write("[Fuzz] Patched MegaCrit.Sts2.Core.Logging.Log.Error (fuzz-only, passive observer) to capture game errors into this log.");
        }
        catch (Exception ex)
        {
            Log.Write($"[Fuzz] WARNING: failed to install game-error capture patch on Log.Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Harmony prefix target for MegaCrit.Sts2.Core.Logging.Log.Error(string text, int
    /// skipFrames = 2) (Log.cs:75) — see <see cref="InstallGameErrorCapturePatch"/> for how/why this
    /// gets installed. Harmony matches this by parameter NAME against the original's `text` parameter;
    /// the original's `skipFrames` parameter is simply omitted here since this prefix has no use for
    /// it.
    ///
    /// A pure observer, nothing else: `void` return means Harmony always runs the original Log.Error
    /// afterward regardless of what happens in here (a prefix can only skip the original by returning
    /// `bool` and returning false — this one can't, its return type is void). The try/catch below
    /// means an exception in here can also never propagate into the game's own logging call.
    /// </summary>
    private static void OnGameLogError(string text)
    {
        try
        {
            RecordGameError(text);
        }
        catch (Exception ex)
        {
            Log.Write($"[Fuzz] OnGameLogError ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Records one game-side error (captured via <see cref="OnGameLogError"/>) into the fuzz-only
    /// ring buffer <see cref="_gameErrors"/>, so a stalled combat's dump (DescribeStallState) and the
    /// run summary (RunAllCombatsAsync) can report it instead of it sitting only in Godot's own
    /// stdout log file. Logs the FULL text immediately, in order, via Log.Write — so nothing is lost
    /// even if the run crashes before the ring buffer is ever read back — and only THEN truncates what
    /// gets kept in memory, since a turn-loop death (CombatManager.cs:516-528) carries a very long
    /// stack.
    /// </summary>
    private static void RecordGameError(string text)
    {
        Log.Write($"[Fuzz][gameerror] {text}");

        // CombatManager.RunTurnLoopAfter's own report of its turn loop dying (CombatManager.cs:521) —
        // matched by literal substring since `text` also carries the full exception appended after it
        // (`{e}` in the source). See _gameTurnLoopDied's doc comment for why this gets its own flag
        // instead of just another entry in _gameErrors.
        if (text.Contains("turn loop died while its combat is in progress"))
            _gameTurnLoopDied = true;

        string entry = text.Length > 4000 ? text.Substring(0, 4000) + "… (truncated)" : text;
        _gameErrors.Add(entry);
        while (_gameErrors.Count > MaxCapturedGameErrors)
            _gameErrors.RemoveAt(0);
    }

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

            // Fuzz-only, passive: captures the game's own Log.Error calls into this log too — see
            // InstallGameErrorCapturePatch's doc comment for why (CombatManager's turn-loop death is
            // otherwise reported only via Log.Error/Sentry, with no public flag or event).
            InstallGameErrorCapturePatch();

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
            Log.Write("[Fuzz] ABORT: no encounters resolved from the harness's act (ModelDb.Acts.FirstOrDefault()) — nothing to fight.");
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
        var gameErrorSeeds = new List<string>();
        var turnLoopDiedSeeds = new List<string>();

        // Coverage accumulators for the "coverage:" summary line below — answer "did this run
        // actually explore anything beyond the untouched starting deck" from the log alone, without
        // post-processing every per-combat line.
        var characterCounts = new Dictionary<string, int>();
        var roomTypeCounts = new Dictionary<string, int>();
        int totalRelicsInjected = 0;
        int totalPotionsInjected = 0;
        int totalDeckCardsInjected = 0;
        int totalCardsUpgraded = 0;

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
                if (outcome.SawGameError)
                    gameErrorSeeds.Add(outcome.Seed);
                if (outcome.TurnLoopDied)
                    turnLoopDiedSeeds.Add(outcome.Seed);
                if (outcome.BudgetExhausted)
                    budgetExhausted++;
                // TurnLoopDied combats also set DriveError (see DriveCombatAsync's GameTurnLoopDied
                // branch) — excluded here so they land only in turnLoopDiedSeeds above, never also in
                // driveErrorSeeds, which would double-count the same combat under two different labels.
                else if (outcome.DriveError != null && !outcome.TurnLoopDied)
                    driveErrorSeeds.Add(outcome.Seed);

                if (outcome.CharacterId.Length > 0)
                    characterCounts[outcome.CharacterId] = characterCounts.GetValueOrDefault(outcome.CharacterId) + 1;
                if (outcome.RoomTypeName.Length > 0)
                    roomTypeCounts[outcome.RoomTypeName] = roomTypeCounts.GetValueOrDefault(outcome.RoomTypeName) + 1;
                totalRelicsInjected += outcome.RelicsInjected;
                totalPotionsInjected += outcome.PotionsInjected;
                totalDeckCardsInjected += outcome.DeckCardsInjected;
                totalCardsUpgraded += outcome.CardsUpgraded;

                // One line per combat on the happy path; failures already got a full block logged
                // from inside RunOneCombatAsync/TryAttemptRestore, so this line is just the index.
                // turnsPlayed/cardsPlayed make a stalled drive loop (or a suspiciously fast "completed")
                // visible at a glance without digging into the full per-action log.
                Log.Write($"[Fuzz] combat={i}/{combatCount - 1} seed={outcome.Seed} encounter={outcome.EncounterId} "
                    + $"character={outcome.CharacterId} roomType={outcome.RoomTypeName} "
                    + $"completed={outcome.Completed} turnsPlayed={outcome.TurnsPlayed} cardsPlayed={outcome.CardsPlayed} "
                    + $"relics={outcome.RelicsInjected} potions={outcome.PotionsInjected} deckAdds={outcome.DeckCardsInjected} "
                    + $"upgrades={outcome.CardsUpgraded} "
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
        // These are the GAME's own errors (MegaCrit.Sts2.Core.Logging.Log.Error, captured via the
        // fuzz-only patch — see InstallGameErrorCapturePatch), NOT an UndoSync/ChecksumHook finding —
        // most commonly CombatManager.RunTurnLoopAfter's turn-loop death (CombatManager.cs:516-528),
        // which production only ever reports through Log.Error/Sentry with no public flag or event.
        // Kept separate from failingSeeds/stuckAfterRestoreSeeds/sectionFailureSeeds above for the
        // same reason those stay separate from each other: conflating "the game itself errored" with
        // an UndoSync-specific finding would send someone chasing the wrong code.
        Log.Write(gameErrorSeeds.Count == 0
            ? "[Fuzz] no game errors captured (MegaCrit.Sts2.Core.Logging.Log.Error)."
            : $"[Fuzz] combat seeds with game errors ({gameErrorSeeds.Count}) — the GAME's own Log.Error fired during these, not an UndoSync finding; see \"[Fuzz][gameerror]\" lines above for full text: {string.Join(", ", gameErrorSeeds)}");
        // Every turn-loop death is also captured above as a game error (RecordGameError sees the same
        // Log.Error text) — this is a strict subset of gameErrorSeeds, but a more actionable one:
        // WaitForIdleOurTurnAsync detected it and DriveCombatAsync gave up immediately instead of
        // reporting a generic stuck-after-restore/drive-error for it. This is the GAME's own failure
        // (CombatManager.RunTurnLoopAfter, CombatManager.cs:516-528), NOT an UndoSync/ChecksumHook
        // finding — kept out of driveErrorSeeds below so the two lists never double-count the same
        // combat.
        Log.Write(turnLoopDiedSeeds.Count == 0
            ? "[Fuzz] no game turn-loop deaths detected."
            : $"[Fuzz] combat seeds where the game's own turn loop died ({turnLoopDiedSeeds.Count}) — not an UndoSync finding, the combat could never complete regardless; see \"[Fuzz][gameerror]\" lines above for the stack: {string.Join(", ", turnLoopDiedSeeds)}");
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
            + $"upgrades={totalCardsUpgraded}");

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

        // Cleared per combat, not just once for the whole run: an error captured by OnGameLogError
        // during combat N-1 (e.g. its own turn-loop death) must not be attributed to combat N's
        // DescribeStallState/SawGameError — "gameErrors={n}" only means "this combat" if the ring
        // buffer actually starts empty at combat start.
        _gameErrors.Clear();
        // Same reasoning as _gameErrors.Clear() above: see _gameTurnLoopDied's doc comment.
        _gameTurnLoopDied = false;

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

            // Records a visited map coord matching the encounter's own RoomType, before the two
            // location-router calls below. A real run only ever reaches combat by walking a map point
            // (MapScreenHandler), so RunState.CurrentMapCoord is always already set once combat
            // starts. This harness instead drives straight into EnterRoomDebug with no map navigation
            // at all, so without this step RunState.CurrentMapCoord stays null forever (RunState.cs:
            // 112-121, `_visitedMapCoords.Last()` on a list nothing has ever added to) and
            // RunState.CurrentMapPoint stays null right behind it (RunState.cs:127-137).
            //
            // That matters because game code reads CurrentMapPoint with no null guard:
            // FurCoat.BeforeCombatStart (FurCoat.cs:127-133) does
            // `Owner.RunState.CurrentMapPoint.coord`, which NREs and kills the combat's own turn loop
            // the instant a fuzzed loadout happens to include FurCoat. A real run can never enter
            // combat without a current map point, so this is a harness defect being fixed here, not a
            // game bug being worked around.
            //
            // The recorded point's PointType must match the encounter's RoomType, not just be ANY
            // point: fighting a Boss encounter while RunState says the player is standing on a Monster
            // map point is exactly the kind of state-the-game-cannot-produce pairing the rest of this
            // change (ResolveEncounterPool, the relic/potion/card factories in SetUpRandomLoadoutAsync)
            // exists to eliminate.
            //
            // Must run before RunLocationTargetedBuffer.OnLocationChanged/MapSelectionSynchronizer.
            // OnLocationChanged just below, not after: RunState.MapLocation is
            // `new MapLocation(CurrentMapCoord, CurrentActIndex)` (RunState.cs:147), and RunLocation
            // embeds MapLocation (RunState.cs:142) — recording the coord after either router call
            // would hand both routers a location whose coord is still null.
            //
            // Must run after SetActInternal (immediately above), not before: RunState.Map defaults to
            // NullActMap.Instance until SetActInternal installs a real map (RunState.cs:102), so
            // map.GetAllMapPoints()/StartingMapPoint below need the real map already in place.
            //
            // Wrapped in try/catch, log-and-continue rather than aborting the combat: this is
            // best-effort scaffolding for FurCoat and anything else that reads CurrentMapPoint, not
            // itself something this fuzzer exists to test.
            try
            {
                MapPointType wanted = encounterPrototype.RoomType switch
                {
                    RoomType.Elite => MapPointType.Elite,
                    RoomType.Boss => MapPointType.Boss,
                    _ => MapPointType.Monster,
                };
                var map = runState.Map;
                var point = map.GetAllMapPoints().FirstOrDefault(p => p.PointType == wanted) ?? map.StartingMapPoint;
                runState.AddVisitedMapCoord(point.coord);
            }
            catch (Exception ex)
            {
                Log.Write($"[Fuzz] combat={combatIndex} WARNING: failed to record a visited map point matching the encounter's RoomType: {ex.Message}");
            }

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
            await SetUpRandomLoadoutAsync(player, encounterPrototype.RoomType, combatIndex, rng, outcome);

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
            // See CombatOutcome.SawGameError's doc comment: the game's own errors, not an UndoSync
            // finding — checked here, right after the drive loop returns, against whatever
            // OnGameLogError appended to _gameErrors (cleared at the top of this method) during this
            // combat specifically.
            if (_gameErrors.Count > 0)
                outcome.SawGameError = true;
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
    /// Randomizes this combat's starting loadout — relics, potions, extra deck cards — all driven off
    /// `rng` for HOW MANY of each to inject (never the game's own Rng/RunRngSet for that count, same
    /// reasoning as everywhere else in this file), but drawn via the game's own reward factories
    /// (RelicFactory/PotionFactory/CardFactory) for WHICH one — off player.PlayerRng.Rewards, the same
    /// stream a real reward screen draws from (PlayerRngSet.cs:19, Player.cs:55) — rather than from
    /// ModelDb's catalogues of everything defined in the game (ModelDb.AllRelics/AllPotions,
    /// character.CardPool.AllCards). Those catalogues include content the game itself would never
    /// actually offer here — mock/deprecated entries, rarity-blind draws, a character's full card pool
    /// regardless of the room's own rarity odds — which was manufacturing loadouts a real run can't
    /// reach. Called from RunOneCombatAsync after the location-router calls and after the card
    /// selector is installed, but before EnterRoomDebug (see the call site's own comment for why).
    ///
    /// Every individual injection (one relic, one potion, one card-reward batch) is independently
    /// wrapped in try/catch and logs+continues on failure: this method exists specifically to exercise
    /// models the fuzzer previously never touched, so one bad/incompatible model throwing must never
    /// abort the rest of the loadout, let alone the combat. Every awaited step is also guarded by
    /// AwaitWithTimeoutAsync, since CombatWallClockTimeout only covers DriveCombatAsync and this
    /// method runs entirely before that.
    ///
    /// No enchantment step: see the comment block after the upgrade pass below for why there is
    /// nothing here to fuzz.
    /// </summary>
    private static async Task SetUpRandomLoadoutAsync(Player player, RoomType roomType, int combatIndex, Random rng, CombatOutcome outcome)
    {
        // --- Relics --------------------------------------------------------------------------------
        // RelicFactory.PullNextRelicFromFront (RelicFactory.cs:21) is the game's own relic-reward
        // factory, not a catalogue this file has to filter itself: it rolls a rarity off the rng
        // passed in and pulls the next matching relic from player.RelicGrabBag, removing it from the
        // bag as it goes (RelicFactory.cs:47-48) — so it can never hand back a relic already seen this
        // run. The previous ModelDb.AllRelics draw-without-replacement loop was reimplementing exactly
        // that no-duplicates guarantee, and only for the pool it happened to build, not the game's own
        // per-run grab bag. The fuzzer's own `rng` still decides HOW MANY relics to inject (a synthetic
        // decision the game never makes on its own); WHICH relic now comes from the same roll a real
        // reward screen would make.
        int relicCount = rng.Next(0, 5); // 0-4 inclusive
        for (int i = 0; i < relicCount; i++)
        {
            var model = RelicFactory.PullNextRelicFromFront(player, player.PlayerRng.Rewards);
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
        // PotionFactory.CreateRandomPotionOutOfCombat (PotionFactory.cs:30) is the game's own
        // out-of-combat potion factory: it draws from GetPotionOptions(player) — this character's own
        // PotionPool plus the shared pool, both already filtered by player.UnlockState
        // (PotionFactory.cs:92-95) — with rarity rolled off the rng passed in, exactly what a
        // rest-site/reward potion draw would produce. ModelDb.AllPotions, the previous source here,
        // has no such filtering: it includes potions this character/unlock combination could never
        // actually be offered.
        int potionCount = rng.Next(0, player.MaxPotionCount + 1);
        for (int i = 0; i < potionCount; i++)
        {
            var model = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
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
        // CardCreationOptions.ForRoom(player, roomType) (CardCreationOptions.cs:80) + CardFactory.
        // CreateForReward (CardFactory.cs:89-109) is the game's own combat-reward card factory:
        // ForRoom restricts the pool to player.Character.CardPool and picks rarity odds off roomType
        // (Monster/Elite/Boss each get their own CardRarityOddsType, CardCreationOptions.cs:100-107)
        // — exactly what this encounter's own reward screen would offer. character.CardPool.AllCards,
        // the previous source here, ignored rarity odds entirely and drew every card in the pool with
        // even weight regardless of room type. CreateForReward also rolls each returned card's
        // upgrade itself unless CardCreationFlags.NoUpgradeRoll is set (CardFactory.cs:98-102) — left
        // unset here on purpose, since that's the game's own upgrade behaviour, not something this
        // harness should suppress.
        //
        // Cards come back already owned by `player` (CardFactory.cs:241, `player.RunState.CreateCard`),
        // so unlike the old `scope.CreateCard(model, player)` call, nothing here needs
        // RunManager.Instance.DebugOnlyGetState()/ICardScope any more.
        //
        // The whole batch call is wrapped, not just the per-card CardPileCmd.Add below: unlike
        // RelicFactory/PotionFactory (which fall back to a default rather than throw), CreateForReward
        // eagerly builds its full result list up front and can throw InvalidOperationException if
        // cardCount asks for more unique cards/rarities than a small pool can offer
        // (CardFactory.cs:229-232, :237-240) — that failure must still leave the rest of the loadout
        // (and the combat) untouched, same as every other injection in this method.
        int cardCount = rng.Next(0, 9);
        try
        {
            var options = CardCreationOptions.ForRoom(player, roomType);
            foreach (var result in CardFactory.CreateForReward(player, cardCount, options))
            {
                var card = result.Card; // already owned by player -- do not scope.CreateCard it again
                try
                {
                    var addTask = CardPileCmd.Add(card, PileType.Deck);
                    if (!await AwaitWithTimeoutAsync(addTask, $"deck card '{card.Id.Entry}'", combatIndex))
                        continue;
                    if (addTask.Result.success)
                        outcome.DeckCardsInjected++;
                }
                catch (Exception ex)
                {
                    Log.Write($"[Fuzz] combat={combatIndex} loadout: deck card '{card.Id.Entry}' FAILED: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Fuzz] combat={combatIndex} loadout: card reward batch (cardCount={cardCount}) FAILED: {ex.Message}");
        }

        // --- Upgrades ------------------------------------------------------------------------------
        // Snapshot first: CardCmd.Upgrade mutates the CardModel in place, and
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

        // --- Enchantments ----------------------------------------------------------------------------
        // Deliberately nothing here. Unlike relics/potions/cards, there is no gameplay pool of "cards
        // that can be enchanted" for a harness to draw from: every enchantment in the game is applied
        // by one specific source that also decides which card receives it. BladeOfInk.OnPlay, for
        // example, enchants only the Shivs it creates with Inky (BladeOfInk.cs:32-37) — never an
        // arbitrary card in hand or deck — and that pairing isn't incidental: Inky.OnPlay reads
        // cardPlay.Target directly (Inky.cs:28) whenever the enchanted card's own TargetType isn't
        // AllEnemies, which is safe only because BladeOfInk only ever hands Inky a Shiv, and Shiv is
        // TargetType.AnyEnemy (Shiv.cs:54) and therefore always carries a target. Applying Inky to an
        // arbitrary deck card via this file's previous injection loop routinely handed it a
        // TargetType.Self/None card instead, with no cardPlay.Target to read — a "vanilla softlock"
        // this harness manufactured entirely by itself, not a bug a real run could ever hit.
        //
        // ModelDb.DebugEnchantments — this file's previous source for this step — is documented as
        // "Every Enchantment defined in the game code, including mock ones for testing"
        // (ModelDb.cs:104-108); applying one of those to a random card was never representative of
        // anything a real run does. Enchantment capture/restore still gets exercised whenever
        // CardFactory.CreateForReward above happens to put a card like BladeOfInk in the deck and it
        // gets played in combat — that path is authentic (the game's own OnPlay enchanting its own
        // Shivs), unlike this deleted step.
    }

    /// <summary>Result of waiting for an actionable idle player-turn state — see
    /// WaitForIdleOurTurnAsync.</summary>
    private enum IdleWait { Ready, CombatEnded, TimedOut, GameTurnLoopDied }

    /// <summary>Set by WaitForIdleOurTurnAsync immediately before it returns TimedOut, to
    /// "phase={...} blockers=[{UndoSyncMod.DescribeUndoRedoBlockers()}]" — cleared back to "" on every
    /// Ready/CombatEnded return. DriveCombatAsync reads this right after a TimedOut result so both of
    /// its "stuck" log lines can say WHICH condition was still blocking, not just that something was.
    /// </summary>
    private static string _lastIdleWaitBlockers = "";

    /// <summary>Set by DriveCombatAsync right after a driver-issued action is actually taken —
    /// "TryManualPlay({card.Id.Entry})" right after a CardModel.TryManualPlay(target) call returns
    /// true, "EndTurn" right after the PlayerCmd.EndTurn call — and read back into
    /// DescribeStallState's log line as lastDriverAction=. Lets a stall be told apart as "stuck right
    /// after ending the turn" (lastDriverAction=EndTurn) from "stuck after a specific card"
    /// (lastDriverAction=TryManualPlay(...)), which neither _lastIdleWaitBlockers nor DescribeStallState
    /// alone can say — both only describe the CURRENT stuck state, not what the driver did to get
    /// there.</summary>
    private static string _lastDriverAction = "";

    // ==================================================================================
    // Stall diagnostics
    // ==================================================================================

    /// <summary>Reflection handle for ActionQueueSet's private field `_actionQueues`
    /// (List&lt;ActionQueue&gt;, ActionQueueSet.cs:48) — the declaring type (ActionQueueSet) is public
    /// and known at compile time, so this one can be a plain readonly field, unlike the per-element
    /// handles below.</summary>
    private static readonly FieldInfo? FActionQueues = AccessTools.Field(typeof(ActionQueueSet), "_actionQueues");

    /// <summary>Same reflection pattern StateSnapshot.cs:240/249-250 already uses to reach
    /// CombatManager._turnState and, off its runtime field type, the PlayersReadyToBeginEnemyTurn
    /// property (CombatTurnState.cs:66, live HashSet&lt;Player&gt;) — CombatManager exposes public
    /// wrappers for IsEnemyTurnStarted/EndingPlayerTurnPhaseOne/EndingPlayerTurnPhaseTwo/IsAboutToLose
    /// (CombatManager.cs:123-153) but not for PlayersReadyToBeginEnemyTurn, so this is the only way to
    /// reach it. Declared independently here rather than reusing StateSnapshot's private members.</summary>
    private static readonly FieldInfo? FCmTurnState = AccessTools.Field(typeof(CombatManager), "_turnState");
    private static readonly PropertyInfo? PTsPlayersReadyToBeginEnemyTurn =
        FCmTurnState != null ? AccessTools.Property(FCmTurnState.FieldType, "PlayersReadyToBeginEnemyTurn") : null;

    /// <summary>Lazily-cached reflection handles for ActionQueueSet's private nested `ActionQueue` type
    /// (ActionQueueSet.cs:22) — private, so it cannot be named in source, meaning its declaring Type is
    /// only knowable at runtime (off the first element's GetType()). Every element of _actionQueues
    /// shares that same runtime type, so this dictionary is populated once per field name on first use
    /// and reused for every element on every call afterward, rather than re-reflecting per element per
    /// call.</summary>
    private static Type? _cachedActionQueueElementType;
    private static Dictionary<string, FieldInfo?> _cachedActionQueueElementFields = new();

    private static FieldInfo? GetActionQueueElementField(Type elementType, string fieldName)
    {
        if (elementType != _cachedActionQueueElementType)
        {
            _cachedActionQueueElementType = elementType;
            _cachedActionQueueElementFields = new Dictionary<string, FieldInfo?>();
        }
        if (!_cachedActionQueueElementFields.TryGetValue(fieldName, out var field))
        {
            field = AccessTools.Field(elementType, fieldName);
            _cachedActionQueueElementFields[fieldName] = field;
        }
        return field;
    }

    /// <summary>Formats one ActionQueueSet._actionQueues element (a private ActionQueue instance,
    /// ActionQueueSet.cs:22-33) by reflecting its public fields — ownerId, actions, isPaused,
    /// isCancellingPlayerDrivenCombatActions, actionCancellingPlayCardActions — via
    /// GetActionQueueElementField. Every field read is individually null-guarded, degrading to "?"/
    /// "null" rather than throwing, since the caller (DescribeStallState) must never throw either.
    /// </summary>
    private static string DescribeActionQueue(object queueElement)
    {
        var t = queueElement.GetType();
        object? ownerIdVal = GetActionQueueElementField(t, "ownerId")?.GetValue(queueElement);
        object? actionsVal = GetActionQueueElementField(t, "actions")?.GetValue(queueElement);
        object? isPausedVal = GetActionQueueElementField(t, "isPaused")?.GetValue(queueElement);
        object? isCancellingVal = GetActionQueueElementField(t, "isCancellingPlayerDrivenCombatActions")?.GetValue(queueElement);
        object? cancelPlayCardsVal = GetActionQueueElementField(t, "actionCancellingPlayCardActions")?.GetValue(queueElement);

        int actionCount = 0;
        var actionTypeNames = new List<string>();
        if (actionsVal is System.Collections.IEnumerable actionsEnumerable)
        {
            foreach (var a in actionsEnumerable)
            {
                actionCount++;
                if (actionTypeNames.Count < 3 && a != null)
                    actionTypeNames.Add(a.GetType().Name);
            }
        }
        string actionsSuffix = actionTypeNames.Count == 0
            ? ""
            : $"({string.Join(",", actionTypeNames)}{(actionCount > actionTypeNames.Count ? ",..." : "")})";

        string cancelPlayCardsDesc = cancelPlayCardsVal == null ? "null" : cancelPlayCardsVal.GetType().Name;

        return $"q[{ownerIdVal?.ToString() ?? "?"}]{{actions={actionCount}{actionsSuffix} "
            + $"paused={isPausedVal?.ToString() ?? "?"} cancellingPlayerDriven={isCancellingVal?.ToString() ?? "?"} "
            + $"cancelPlayCards={cancelPlayCardsDesc}}}";
    }

    /// <summary>
    /// Diagnostic dump of the end-turn chain's live state, called from DriveCombatAsync's two
    /// IdleWait.TimedOut branches. UndoSyncMod.DescribeUndoRedoBlockers() alone narrowed a 250-combat
    /// headless run's ~3% stuck combats down to two shapes — 12 stalls at phase=Play/
    /// syncState=EndTurnPhaseOne, 4 at phase=None/syncState=NotPlayPhase — but names the symptom, not
    /// the cause. The end-turn chain (CombatManager.cs): SetCombatState(EndTurnPhaseOne) (:1461) ->
    /// StartCancellingAllPlayerDrivenCombatActions() -> WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction
    /// (:1474) -> EndPlayerTurnPhaseOneInternal -> enqueue ReadyToBeginEnemyTurnAction (:1467) -> once
    /// every player is ready, AfterAllPlayersReadyToBeginEnemyTurn (:1699) calls
    /// SetCombatState(NotPlayPhase) (:1709). Staying stuck in EndTurnPhaseOne therefore means exactly
    /// one of: the action queue never drained, phase-one itself never finished, or
    /// ReadyToBeginEnemyTurnAction never completed / never registered this player as ready — this dump
    /// prints executor + queue + turn-coordination state so those can be told apart from the log alone.
    ///
    /// Every field is read through its own null-guard (degrading to a placeholder rather than skipping
    /// the rest of the line), and the whole body is additionally wrapped in try/catch: this only ever
    /// runs from an already-broken drive-loop path and must never itself throw and mask the original
    /// stall.
    /// </summary>
    private static string DescribeStallState()
    {
        try
        {
            // --- executor: ActionExecutor.IsPaused (ActionExecutor.cs:31), CurrentlyRunningAction
            // (ActionExecutor.cs:50) -----------------------------------------------------------------
            string executorPart;
            var executor = RunManager.Instance.ActionExecutor;
            if (executor == null)
            {
                executorPart = "executor{null}";
            }
            else
            {
                var current = executor.CurrentlyRunningAction;
                string runningDesc = "none";
                if (current != null)
                {
                    bool playerDriven = ActionQueueSet.IsGameActionPlayerDriven(current);
                    runningDesc = $"{current.GetType().Name}({current.State}) playerDriven={playerDriven}";
                }
                executorPart = $"executor{{paused={executor.IsPaused} running={runningDesc}}}";
            }

            // --- queues: ActionQueueSet.IsEmpty (:65), NextActionId (:77), per-player _actionQueues
            // (:48) -----------------------------------------------------------------------------------
            string queuesPart;
            var queueSet = RunManager.Instance.ActionQueueSet;
            if (queueSet == null)
            {
                queuesPart = "queues{null}";
            }
            else
            {
                var queueDescs = new List<string>();
                if (FActionQueues?.GetValue(queueSet) is System.Collections.IEnumerable rawQueues)
                {
                    foreach (var q in rawQueues)
                    {
                        if (q != null) queueDescs.Add(DescribeActionQueue(q));
                    }
                }
                queuesPart = $"queues{{empty={queueSet.IsEmpty} nextId={queueSet.NextActionId} {string.Join(" ", queueDescs)}}}";
            }

            // --- turn coordination: public wrappers already used elsewhere in this mod
            // (CombatManager.cs:123-153), plus PlayersReadyToBeginEnemyTurn via reflection (no public
            // wrapper exists for it) -------------------------------------------------------------------
            string turnPart;
            var cm = CombatManager.Instance;
            if (cm == null)
            {
                turnPart = "turn{null}";
            }
            else
            {
                string readyDesc = "?/?";
                var turnStateObj = FCmTurnState?.GetValue(cm);
                if (turnStateObj != null && PTsPlayersReadyToBeginEnemyTurn?.GetValue(turnStateObj) is HashSet<Player> readySet)
                {
                    int? totalPlayers = UndoSyncMod.GetCombatState()?.Players.Count;
                    string ids = string.Join(",", readySet.Select(p => p.NetId.ToString()));
                    readyDesc = $"{readySet.Count}/{(totalPlayers?.ToString() ?? "?")} ids=[{ids}]";
                }
                turnPart = $"turn{{enemyStarted={cm.IsEnemyTurnStarted} endingP1={cm.EndingPlayerTurnPhaseOne} "
                    + $"endingP2={cm.EndingPlayerTurnPhaseTwo} aboutToLose={cm.IsAboutToLose} ready={readyDesc}}}";
            }

            // --- game errors: _gameErrors, populated by OnGameLogError via the fuzz-only Log.Error
            // patch (InstallGameErrorCapturePatch) — the count captured so far THIS combat (cleared at
            // combat start in RunOneCombatAsync), plus the first line of the most recent one, so a
            // stall caused by CombatManager.RunTurnLoopAfter's turn-loop death (CombatManager.cs:
            // 516-528) is visible right in the stall dump instead of requiring a separate look at
            // Godot's own stdout log. -------------------------------------------------------------
            string gameErrorsPart = _gameErrors.Count == 0
                ? "gameErrors=0"
                : $"gameErrors={_gameErrors.Count} lastGameError=\"{FirstLineOf(_gameErrors[^1])}\"";

            return $"{executorPart} {queuesPart} {turnPart} lastDriverAction={_lastDriverAction} {gameErrorsPart}";
        }
        catch (Exception ex)
        {
            return $"(stall dump threw: {ex.Message})";
        }
    }

    /// <summary>First line of `text` (up to the first '\n', trimmed of a trailing '\r'), or the whole
    /// string if it has no newline — used by DescribeStallState to preview the most recent captured
    /// game error without dumping its full (possibly very long) stack trace into the stall-state
    /// line.</summary>
    private static string FirstLineOf(string text)
    {
        int idx = text.IndexOf('\n');
        return idx < 0 ? text : text.Substring(0, idx).TrimEnd('\r');
    }

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
    ///
    /// Checks <see cref="_gameTurnLoopDied"/> first, before even the IsInProgress check: once the
    /// game's own turn loop has died (CombatManager.cs:516-528), IsInProgress can still read true —
    /// nothing ever flips it false again on that path — so without this check ahead of it, a dead turn
    /// loop would otherwise present as an ordinary TimedOut stall and burn the full IdleWaitTimeout
    /// before DriveCombatAsync found out. See _gameTurnLoopDied's own doc comment for how it gets set.
    /// </summary>
    private static async Task<IdleWait> WaitForIdleOurTurnAsync(Player me)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (_gameTurnLoopDied)
            {
                _lastIdleWaitBlockers = "";
                return IdleWait.GameTurnLoopDied;
            }
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
                if (wait == IdleWait.GameTurnLoopDied)
                {
                    // The GAME's own turn loop died (CombatManager.cs:516-528) — not this driver being
                    // stuck. Deliberately NOT folded into the StuckAfterRestore branch below even when
                    // watchingRestore is set: a dead turn loop fully explains the lack of progress on
                    // its own (nothing enqueued from here on was ever going to drain, restore or no
                    // restore), so reporting it as stuck-after-restore would misdirect an investigation
                    // toward ChecksumHook.RestoreTo instead of the game's own turn loop.
                    outcome.TurnLoopDied = true;
                    outcome.DriveError = "the game's own combat turn loop died — see the [Fuzz][gameerror] lines above for the stack";
                    Log.Write($"[Fuzz] combat={combatIndex} {outcome.DriveError}");
                    return;
                }
                if (wait == IdleWait.TimedOut)
                {
                    if (watchingRestore)
                    {
                        outcome.StuckAfterRestore = true;
                        outcome.StuckAfterRestoreDetail = $"{restoreWatchDesc} | {_lastIdleWaitBlockers}";
                        string shape = restoreActionPending
                            ? "an action was issued after the restore but its queue never drained"
                            : "the driver never returned to an idle actionable state after the restore";
                        Log.Write($"[Fuzz] combat={combatIndex} STALL STATE: {DescribeStallState()}");
                        Log.Write($"[Fuzz] combat={combatIndex} STUCK AFTER RESTORE — {shape} -> {restoreWatchDesc} | {_lastIdleWaitBlockers}. This is the shape an action-id-reuse bug (FastForwardNextActionId/FastForwardHookId) would take.");
                    }
                    else
                    {
                        outcome.DriveError = $"stuck waiting for idle player Play phase | {_lastIdleWaitBlockers}";
                        Log.Write($"[Fuzz] combat={combatIndex} STALL STATE: {DescribeStallState()}");
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
                        _lastDriverAction = $"TryManualPlay({card.Id.Entry})";
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
                    _lastDriverAction = "EndTurn";
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
    /// Pool of encounters EnterRoomDebug can fight, for the ONE act this harness actually sets:
    /// RunOneCombatAsync always calls RunManager.Instance.SetActInternal(0), so act 0
    /// (ModelDb.Acts.FirstOrDefault()) is the only act whose encounters a combat here can ever
    /// legitimately be fighting.
    ///
    /// This previously unioned every act's encounters via ModelDb.AllEncounters (ModelDb.cs:204,
    /// `Acts.SelectMany(a => a.AllEncounters)`), which handed pickRng.Next(pool.Count) act-3 bosses
    /// and act-2 elites to fight while RunState.CurrentActIndex stayed 0 — an act/encounter pairing
    /// the game itself can never produce, since a real run only ever fights an act's own encounters
    /// while that act is current. That's the same mistake as the relic/potion/enchantment catalogues
    /// this change also fixes: drawing from "everything the game defines" instead of "everything this
    /// state of the game could actually offer" manufactures states no real run can reach.
    ///
    /// ActModel.AllRegularEncounters / AllWeakEncounters / AllEliteEncounters / AllBossEncounters
    /// (ActModel.cs:149-164) are the game's own partitioning of one act's encounters by RoomType, used
    /// here directly instead of re-deriving a RoomType filter over ModelDb.AllEncounters. That also
    /// makes the old ModelDb.EventEncounters filtering unnecessary: none of these four act-level pools
    /// ever include event encounters — they're reachable only through event flow (e.g. choosing
    /// "Start a Fight!"; see RoomType's own doc comment on the MapPointType/RoomType distinction), not
    /// through a plain EnterRoomDebug the way a Monster/Elite/Boss encounter is.
    ///
    /// The explicit OrderBy on the encounter's own id makes pickRng.Next(pool.Count) in
    /// RunAllCombatsAsync reproducible for a given (baseSeed, combatIndex) even if ModelDb/ActModel's
    /// own internal enumeration order ever changes (e.g. a Concat/Distinct implementation detail) —
    /// without a stable order, "REPRO: baseSeed=... combatIndex=..." in a failure log could stop
    /// reproducing the same encounter across two runs of the mod on different game builds.
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
        var act = ModelDb.Acts.FirstOrDefault();
        if (act == null)
        {
            Log.Write("[Fuzz] encounter pool: ModelDb.Acts returned no acts.");
            return new List<EncounterModel>();
        }

        var pool = act.AllRegularEncounters
            .Concat(act.AllWeakEncounters)
            .Concat(act.AllEliteEncounters)
            .Concat(act.AllBossEncounters)
            .Distinct()
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
