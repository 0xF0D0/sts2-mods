using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace UndoReplayLab;

[ModInitializer("Initialize")]
public static class UndoReplayLabMod
{
    private const string Flag = "undo-replay-lab";
    private const string OutputArg = "undo-replay-lab-output";
    private const string UserDirArg = "undo-replay-lab-user-dir";
    private const string SeedArg = "undo-replay-lab-seed";
    private const string HarmonyId = "UndoReplayLab.native-replay-validation";
    private const string DefaultSeed = "undo-replay-lab-native-seed";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
    private static readonly List<string> ChoiceOptionTrace = new();

    public static void Initialize()
    {
        if (CommandLineHelper.HasArg(Flag))
            _ = RunWhenReadyAsync();
    }

    private static async Task RunWhenReadyAsync()
    {
        var report = new LabReport
        {
            Pid = System.Environment.ProcessId,
            StartedUtc = DateTimeOffset.UtcNow,
            UserDataDir = OS.GetUserDataDir(),
            ExpectedUserDataDir = GetValue(UserDirArg),
            OutputPath = GetValue(OutputArg)
        };
        report.InitializePhases();
        Harmony? harmony = null;

        try
        {
            report.Phase("bootstrap", "RUNNING");
            if (report.ExpectedUserDataDir != null)
            {
                report.UserDirMatch = PathsEqual(report.ExpectedUserDataDir, report.UserDataDir);
                if (!report.UserDirMatch)
                    throw new InvalidOperationException($"OS.GetUserDataDir mismatch: expected '{report.ExpectedUserDataDir}', actual '{report.UserDataDir}'");
            }

            var game = NGame.Instance ?? throw new InvalidOperationException("NGame.Instance is null during mod initialization");
            if (!await AwaitWithTimeout(game.GameStartupComplete, StartupTimeout))
                throw new TimeoutException("NGame.GameStartupComplete did not complete within the startup timeout");

            report.BinaryHashes["UndoReplayLab.dll"] = HashFile(typeof(UndoReplayLabMod).Assembly.Location);
            report.BinaryHashes["sts2.dll"] = HashFile(Path.Combine(GetSts2DataDir(), "sts2.dll"));
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(UndoReplayLabMod).Assembly);
            report.Phase("bootstrap", "PASS");

            await RunLabAsync(report, GetValue(SeedArg) ?? DefaultSeed);
        }
        catch (Exception ex)
        {
            report.Phase("bootstrap", "FAIL", ex.Message);
            report.Error = ex.ToString();
        }
        finally
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            try
            {
                var outputPath = report.OutputPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = Path.Combine(report.UserDataDir, "undo-replay-lab-report.json");
                report.OutputPath = Path.GetFullPath(outputPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(report.OutputPath)!);
                File.WriteAllText(report.OutputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                report.Error = (report.Error == null ? "" : report.Error + System.Environment.NewLine) + "Report write failed: " + ex;
            }
            NGame.Instance?.GetTree()?.Quit(report.HasFailure ? 1 : 0);
        }
    }

    private static async Task RunLabAsync(LabReport report, string seed)
    {
        RunManager manager = RunManager.Instance;
        IDisposable? selector = null;
        bool managerOwned = false;

        try
        {
            report.Phase("native-save-load", "RUNNING");
            TestMode.TurnOnInternal();

            // Defect is a playable real character. Deprived is a mock and has no valid starting deck.
            var player = Player.CreateForNewRun<Defect>(UnlockState.all, 1UL);
            var state = RunState.CreateForTest(new[] { player }, seed: seed);
            manager.SetUpTest(state, new NetSingleplayerGameService(), shouldSave: false);
            managerOwned = true;
            manager.Launch();
            manager.ChecksumTracker.IsEnabled = true;
            await manager.SetActInternal(0);

            var encounter = state.Act.AllRegularEncounters.FirstOrDefault()
                ?? throw new InvalidOperationException("Act 0 has no regular encounter");
            state.AddVisitedMapCoord(new MegaCrit.Sts2.Core.Map.MapCoord(0, 0));
            manager.RunLocationTargetedBuffer.OnLocationChanged(state.RunLocation);
            manager.MapSelectionSynchronizer.OnLocationChanged(state.MapLocation);

            // Controlled five-card persistent deck, established before the checkpoint and combat RNG consumption.
            player.Deck.Clear(silent: true);
            var deckVersion = state.CreateCard<GeneticAlgorithm>(player);
            await CardPileCmd.Add(deckVersion, PileType.Deck, skipVisuals: true);
            for (var i = 0; i < 4; i++)
                await CardPileCmd.Add(state.CreateCard<DefendDefect>(player), PileType.Deck, skipVisuals: true);

            var attackProcure = await PotionCmd.TryToProcure<AttackPotion>(player);
            var skillProcure = await PotionCmd.TryToProcure<SkillPotion>(player);
            if (!attackProcure.success || !skillProcure.success)
                throw new InvalidOperationException($"Could not procure deterministic potion setup: attack={attackProcure.failureReason}, skill={skillProcure.failureReason}");

            // Freeze the native SerializableRun before EnterRoomDebug. Entering combat consumes opening-hand RNG.
            var initialBytes = Encoding.UTF8.GetBytes(JsonSerializationUtility.ToJson(manager.ToSave(null)));
            report.InitialSaveBytes = initialBytes.Length;
            report.InitialSaveSha256 = Sha256(initialBytes);
            report.NativeSaveRoundTrip = DeserializeSave(initialBytes) != null;

            // EnterRoomDebug records its own initial state before map history and room creation. This is the replay snapshot.
            manager.CombatReplayWriter.IsEnabled = true;
            await manager.EnterRoomDebug(RoomType.Monster, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned,
                encounter.ToMutable(), showTransition: false);
            report.EncounterId = encounter.Id.Entry;
            report.Phase("native-save-load", report.NativeSaveRoundTrip ? "PASS" : "FAIL");

            var original = LocalContext.GetMe(state) ?? throw new InvalidOperationException("Local player unavailable after combat setup");
            await WaitForCombatAsync(original);
            ChoiceOptionTrace.Clear();
            var generationBefore = RunPotionGenerationProbe(original, report);
            selector = CardSelectCmd.UseSelector(new LocalChoiceSelector(() => original, report), localOnly: true);

            var attackPotion = original.Potions.OfType<AttackPotion>().FirstOrDefault()
                ?? throw new InvalidOperationException("AttackPotion was not restored in the original state");
            attackPotion.EnqueueManualUse(original.Creature);
            report.PotionActionsEnqueued++;
            await WaitForStableBoundaryAsync(manager);
            report.AttackPotionActionCompleted = attackPotion.HasBeenRemovedFromState;

            var skillPotion = original.Potions.OfType<SkillPotion>().FirstOrDefault()
                ?? throw new InvalidOperationException("SkillPotion was not restored in the original state");
            skillPotion.EnqueueManualUse(original.Creature);
            report.PotionActionsEnqueued++;
            await WaitForStableBoundaryAsync(manager);
            report.SkillPotionActionCompleted = skillPotion.HasBeenRemovedFromState;

            var geneticAction = FindGeneticAlgorithmAction(original);
            manager.ActionQueueSynchronizer.RequestEnqueue(geneticAction);
            report.ActionsEnqueued++;
            await WaitForStableBoundaryAsync(manager);
            report.OriginalDeckVersionGrowth = deckVersion.IncreasedBlock;

            var normalAction = FindPlayableCardAction(original);
            manager.ActionQueueSynchronizer.RequestEnqueue(normalAction);
            report.ActionsEnqueued++;
            await WaitForStableBoundaryAsync(manager);
            report.OriginalNormalCardActionCompleted = normalAction.State == GameActionState.Finished;
            report.OriginalCombatDigest = NativeCombatDigest(state);
            report.OriginalChoiceOptions = ChoiceOptionTrace.ToList();

            var replayPath = Path.Combine(report.UserDataDir, "undo-replay-lab-native.mcr").Replace('\\', '/');
            manager.CombatReplayWriter.WriteReplay(replayPath, stopRecording: true);
            var replayBytes = File.ReadAllBytes(replayPath);
            report.ReplayBytes = replayBytes.Length;
            report.ReplaySha256 = Sha256(replayBytes);
            var replay = DeserializePacket<CombatReplay>(replayBytes);
            report.RecordedEvents = replay.events.Count;
            report.RecordedActions = replay.events.Count(e => e.eventType == CombatReplayEventType.GameAction);
            report.RecordedChoices = replay.events.Count(e => e.eventType == CombatReplayEventType.PlayerChoice);
            report.Phase("record", report.RecordedEvents > 0 ? "PASS" : "FAIL",
                report.RecordedEvents == 0 ? "No events were recorded for the exercised target" : null);

            // Clean up the original manager and selector before the native replay setup.
            selector.Dispose();
            selector = null;
            manager.CleanUp();
            managerOwned = false;
            TestMode.IsOn = false;

            await RunFreshReplayTrialAsync(report, replay, initialBytes, generationBefore);
        }
        catch (Exception ex)
        {
            report.Phase("native-save-load", "FAIL", ex.Message);
            report.Error = ex.ToString();
        }
        finally
        {
            try { selector?.Dispose(); } catch { }
            if (managerOwned)
            {
                try { manager.CleanUp(); } catch { }
            }
            TestMode.IsOn = false;
        }
    }

    private static async Task RunFreshReplayTrialAsync(LabReport report, CombatReplay replay, byte[] initialBytes, List<string> generationBefore)
    {
        RunManager manager = RunManager.Instance;
        TestMode.TurnOnInternal();
        try
        {
            if (replay.events.Count == 0)
                throw new InvalidDataException("Replay event list is empty for the exercised target");

            // Deserialize fresh immutable bytes for every trial; no RunState or SerializableRun object is shared.
            var fresh = RunState.FromSerializable(DeserializeSave(initialBytes));
            var singleplayer = new NetSingleplayerGameService();
            if (fresh.Players.Count != 1 || fresh.Players[0].NetId != singleplayer.NetId)
                throw new InvalidDataException("Frozen save does not contain the expected singleplayer net ID");

            // SetUpReplay calls InitializeSavedRun and avoids SetUpTest's InitializeNewRun bag/hook mutations.
            manager.SetUpReplay(fresh, replay, fresh.Players[0].NetId);
            report.ReplayShouldSaveBeforeSuppression = manager.ShouldSave;
            DisableSaving(manager);
            report.ReplaySaveSuppressed = !manager.ShouldSave;
            report.ReplaySaveSetup = true;
            manager.Launch();
            manager.ChecksumTracker.IsEnabled = true;
            var encounter = fresh.Act.AllRegularEncounters.FirstOrDefault()
                ?? throw new InvalidOperationException("Fresh saved state has no regular encounter");
            await manager.EnterRoomDebug(RoomType.Monster, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned,
                encounter.ToMutable(), showTransition: false);
            var player = LocalContext.GetMe(fresh) ?? throw new InvalidOperationException("Fresh replay local player unavailable");
            await WaitForCombatAsync(player);

            ChoiceOptionTrace.Clear();
            var generationAfter = RunPotionGenerationProbe(player, report);
            CompareGeneration(report, generationBefore, generationAfter);

            manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            foreach (var replayEvent in replay.events)
            {
                DispatchReplayEvent(manager, fresh, replayEvent);
                report.ReplayedEvents++;
                if (replayEvent.eventType == CombatReplayEventType.GameAction)
                    report.ReplayedActions++;
                if (replayEvent.eventType == CombatReplayEventType.PlayerChoice)
                    report.ReplayedChoices++;
            }
            // Pump the complete event stream before waiting. Choice and resume events must arrive while actions suspend.
            await WaitForStableBoundaryAsync(manager);

            report.ReplayedChoiceOptions = ChoiceOptionTrace.ToList();
            report.ReplayedAttackPotionActionCompleted = !player.Potions.OfType<AttackPotion>().Any();
            report.ReplayedSkillPotionActionCompleted = !player.Potions.OfType<SkillPotion>().Any();
            report.PotionChoiceReplayMatch = report.AttackPotionActionCompleted && report.SkillPotionActionCompleted
                && report.ReplayedAttackPotionActionCompleted && report.ReplayedSkillPotionActionCompleted
                && report.RecordedChoices > 0 && report.RecordedChoices == report.ReplayedChoices
                && report.OriginalChoiceOptions.SequenceEqual(report.ReplayedChoiceOptions);
            report.Phase("potion-choice-replay", report.PotionChoiceReplayMatch ? "PASS" : "FAIL",
                report.PotionChoiceReplayMatch ? null : "Actual FromChooseACardScreen options or choice event counts differed");

            report.PermanentDeckGrowthRestored = CompareGeneticAlgorithm(fresh, report);
            report.Phase("genetic-algorithm", report.PermanentDeckGrowthRestored ? "PASS" : "FAIL",
                report.PermanentDeckGrowthRestored ? null : "Persistent DeckVersion growth did not match");

            report.ReplayedCombatDigest = NativeCombatDigest(fresh);
            report.ModelStateMatch = string.Equals(report.OriginalCombatDigest, report.ReplayedCombatDigest, StringComparison.Ordinal);
            report.Phase("model-state", report.ModelStateMatch ? "PASS" : "FAIL",
                report.ModelStateMatch ? null : "NetFullCombatState native packet differed after replay");
            report.Phase("replay-dispatch", report.ReplayedEvents > 0 ? "PASS" : "FAIL",
                report.ReplayedEvents == 0 ? "No replay events were dispatched" : null);

            // SetUpReplay installs NetReplayGameService. RequestEnqueue has no replay service path, so do not call it
            // or label an offline enqueue as a live singleplayer handoff.
            report.Phase("post-replay-singleplayer-action", "UNSUPPORTED",
                "Native SetUpReplay uses NetReplayGameService; live RequestEnqueue continuation is not verified");
            report.Phase("normal-card-action-replay", report.OriginalNormalCardActionCompleted
                && report.RecordedActions > 0 && report.ReplayedActions == report.RecordedActions ? "PASS" : "FAIL",
                report.OriginalNormalCardActionCompleted && report.RecordedActions > 0 && report.ReplayedActions == report.RecordedActions
                    ? null : "Normal queued card action did not complete and replay");
        }
        finally
        {
            try { manager.CleanUp(); } catch { }
            TestMode.IsOn = false;
        }
    }

    private static List<string> RunPotionGenerationProbe(Player player, LabReport report)
    {
        var cards = player.Character.CardPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint);
        var generated = new List<string>();
        generated.AddRange(CardFactory.GetDistinctForCombat(player, cards.Where(c => c.Type == CardType.Attack), 3,
            player.RunState.Rng.CombatCardGeneration).Select(c => c.Id.Entry));
        generated.Add("|");
        generated.AddRange(CardFactory.GetDistinctForCombat(player, cards.Where(c => c.Type == CardType.Skill), 3,
            player.RunState.Rng.CombatCardGeneration).Select(c => c.Id.Entry));
        report.PotionGenerationCalls += 2;
        report.PotionGenerationChoices += generated.Count - 1;
        return generated;
    }

    private static void CompareGeneration(LabReport report, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        report.AttackSkillChoicesMatch = before.SequenceEqual(after);
        report.Phase("native-card-generation-rng-probe", report.AttackSkillChoicesMatch ? "PASS" : "FAIL",
            report.AttackSkillChoicesMatch ? "Exact native CardFactory attack/skill RNG probe matched" : "Native CardFactory attack/skill RNG probe differed");
    }

    private static void DispatchReplayEvent(RunManager manager, RunState state, CombatReplayEvent replayEvent)
    {
        switch (replayEvent.eventType)
        {
            case CombatReplayEventType.GameAction:
                Require(replayEvent.action != null && replayEvent.playerId.HasValue, "Malformed GameAction replay event");
                var actionPlayer = state.GetPlayer(replayEvent.playerId!.Value)
                    ?? throw new InvalidDataException($"GameAction references unknown player {replayEvent.playerId.Value}");
                manager.ActionQueueSet.EnqueueWithoutSynchronizing(replayEvent.action!.ToGameAction(actionPlayer));
                return;
            case CombatReplayEventType.HookAction:
                Require(replayEvent.playerId.HasValue && replayEvent.hookId.HasValue && replayEvent.gameActionType.HasValue,
                    "Malformed HookAction replay event");
                manager.ActionQueueSet.EnqueueWithoutSynchronizing(manager.ActionQueueSynchronizer.GetHookActionForId(
                    replayEvent.hookId!.Value, replayEvent.playerId!.Value, replayEvent.gameActionType!.Value));
                return;
            case CombatReplayEventType.ResumeAction:
                Require(replayEvent.actionId.HasValue, "Malformed ResumeAction replay event");
                manager.ActionQueueSet.ResumeActionWithoutSynchronizing(replayEvent.actionId!.Value);
                return;
            case CombatReplayEventType.PlayerChoice:
                Require(replayEvent.playerId.HasValue && replayEvent.choiceId.HasValue && replayEvent.playerChoiceResult.HasValue,
                    "Malformed PlayerChoice replay event");
                var choicePlayer = state.GetPlayer(replayEvent.playerId!.Value)
                    ?? throw new InvalidDataException($"PlayerChoice references unknown player {replayEvent.playerId.Value}");
                manager.PlayerChoiceSynchronizer.ReceiveReplayChoice(choicePlayer, replayEvent.choiceId!.Value,
                    replayEvent.playerChoiceResult!.Value);
                return;
            default:
                throw new InvalidDataException($"Unsupported CombatReplayEventType {replayEvent.eventType}");
        }
    }

    private static bool CompareGeneticAlgorithm(RunState state, LabReport report)
    {
        var deck = state.Players[0].Deck.Cards.OfType<GeneticAlgorithm>().FirstOrDefault();
        report.DeckVersionGrowth = deck?.IncreasedBlock ?? -1;
        report.GeneticAlgorithmDeckCount = state.Players[0].Deck.Cards.Count(c => c is GeneticAlgorithm);
        return deck != null && report.GeneticAlgorithmDeckCount == 1 && deck.IncreasedBlock == report.OriginalDeckVersionGrowth;
    }

    private static PlayCardAction FindGeneticAlgorithmAction(Player player)
    {
        var hand = player.PlayerCombatState?.Hand ?? throw new InvalidOperationException("Player hand missing");
        var card = hand.Cards.OfType<GeneticAlgorithm>().FirstOrDefault()
            ?? throw new InvalidOperationException("Persistent GeneticAlgorithm was absent from the opening hand");
        return new PlayCardAction(card, null);
    }

    private static PlayCardAction FindPlayableCardAction(Player player)
    {
        var hand = player.PlayerCombatState?.Hand ?? throw new InvalidOperationException("Player hand missing");
        var card = hand.Cards.FirstOrDefault(c => c is not GeneticAlgorithm && c.CanPlay(out _, out _))
            ?? hand.Cards.FirstOrDefault(c => c.CanPlay(out _, out _))
            ?? throw new InvalidOperationException("No playable card in hand");
        Creature? target = card.TargetType switch
        {
            TargetType.AnyEnemy => player.Creature.CombatState?.Enemies.FirstOrDefault(),
            TargetType.AnyAlly => player.Creature,
            _ => null
        };
        return new PlayCardAction(card, target);
    }

    private static async Task WaitForCombatAsync(Player player)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CombatManager.Instance?.IsInProgress == true && player.PlayerCombatState != null)
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Combat did not become ready");
    }

    private static async Task WaitForStableBoundaryAsync(RunManager manager)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        var observedActivity = false;
        while (DateTime.UtcNow < deadline)
        {
            if (manager.ActionExecutor.IsRunning || !manager.ActionQueueSet.IsEmpty)
            {
                observedActivity = true;
                await Task.Delay(25);
                continue;
            }
            if (!observedActivity)
                return;
            await Task.Delay(75);
            if (!manager.ActionExecutor.IsRunning && manager.ActionQueueSet.IsEmpty)
                return;
        }
        throw new TimeoutException("Action queue did not reach a stable boundary");
    }

    private static string NativeCombatDigest(RunState state)
    {
        var packet = NetFullCombatState.FromRun(state, null);
        var writer = new PacketWriter();
        writer.Write(packet);
        return Sha256(writer.Buffer.AsSpan(0, writer.BytePosition).ToArray());
    }

    private static SerializableRun DeserializeSave(byte[] bytes) =>
        JsonSerializer.Deserialize<SerializableRun>(Encoding.UTF8.GetString(bytes), JsonSerializationUtility.Options)
        ?? throw new InvalidDataException("SerializableRun deserialized null");

    private static T DeserializePacket<T>(byte[] bytes) where T : IPacketSerializable, new()
    {
        var reader = new PacketReader();
        reader.Reset(bytes);
        return reader.Read<T>();
    }

    private static void DisableSaving(RunManager manager)
    {
        // Verified installed decompile: RunManager.ShouldSave has a private setter at line 91.
        var property = typeof(RunManager).GetProperty(nameof(RunManager.ShouldSave), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(typeof(RunManager).FullName, nameof(RunManager.ShouldSave));
        var setter = property.GetSetMethod(nonPublic: true)
            ?? throw new MissingMethodException(typeof(RunManager).FullName, nameof(RunManager.ShouldSave));
        setter.Invoke(manager, new object[] { false });
        if (manager.ShouldSave)
            throw new InvalidOperationException("Could not suppress SetUpReplay saving");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static string? GetValue(string name) =>
        CommandLineHelper.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static async Task<bool> AwaitWithTimeout(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static string GetSts2DataDir() =>
        System.Environment.GetEnvironmentVariable("STS2DataDir")
        ?? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64";

    private static bool PathsEqual(string expected, string actual) =>
        string.Equals(Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string? HashFile(string path) => File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : null;
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    private static class ChooseCardObserver
    {
        [HarmonyPrefix]
        private static void Prefix(IReadOnlyList<CardModel> cards)
        {
            ChoiceOptionTrace.Add(string.Join(",", cards.Select(c => c.Id.Entry)));
        }
    }

    private sealed class LocalChoiceSelector : ICardSelector
    {
        private readonly Func<Player> _player;
        private readonly LabReport _report;

        public LocalChoiceSelector(Func<Player> player, LabReport report)
        {
            _player = player;
            _report = report;
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var available = options.ToList();
            var selected = available.Take(Math.Max(minSelect, Math.Min(maxSelect, available.Count))).ToList();
            var player = _player();
            var slot = player.RunState.Players.ToList().IndexOf(player);
            var choiceIds = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds;
            if (slot < 0 || slot >= choiceIds.Count || choiceIds[slot] == 0)
                throw new InvalidOperationException("Local selector could not resolve the reserved choice ID");
            var selectedIndex = selected.Count == 0 ? -1 : available.IndexOf(selected[0]);
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceIds[slot] - 1,
                PlayerChoiceResult.FromIndex(selectedIndex));
            _report.LocalChoicesSynced++;
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) =>
            options.Count == 0 ? default : new CardRewardSelection { card = options[0].Card, alternative = null };
    }

    private sealed class LabReport
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset FinishedUtc { get; set; }
        [JsonPropertyName("userDataDir")] public string UserDataDir { get; set; } = "";
        public string? ExpectedUserDataDir { get; set; }
        public bool UserDirMatch { get; set; }
        public string? OutputPath { get; set; }
        public string? EncounterId { get; set; }
        public Dictionary<string, string?> BinaryHashes { get; } = new();
        public Dictionary<string, PhaseResult> Phases { get; } = new();
        public string? Error { get; set; }
        public int InitialSaveBytes { get; set; }
        public string? InitialSaveSha256 { get; set; }
        public bool NativeSaveRoundTrip { get; set; }
        public bool ReplaySaveSetup { get; set; }
        public bool ReplayShouldSaveBeforeSuppression { get; set; }
        public bool ReplaySaveSuppressed { get; set; }
        public int ReplayBytes { get; set; }
        public string? ReplaySha256 { get; set; }
        public int RecordedEvents { get; set; }
        public int RecordedActions { get; set; }
        public int RecordedChoices { get; set; }
        public int ReplayedEvents { get; set; }
        public int ReplayedActions { get; set; }
        public int ReplayedChoices { get; set; }
        public int ActionsEnqueued { get; set; }
        public int PotionActionsEnqueued { get; set; }
        public int LocalChoicesSynced { get; set; }
        public int PotionGenerationCalls { get; set; }
        public int PotionGenerationChoices { get; set; }
        public bool AttackSkillChoicesMatch { get; set; }
        public bool AttackPotionActionCompleted { get; set; }
        public bool SkillPotionActionCompleted { get; set; }
        public bool ReplayedAttackPotionActionCompleted { get; set; }
        public bool ReplayedSkillPotionActionCompleted { get; set; }
        public bool PotionChoiceReplayMatch { get; set; }
        public bool PermanentDeckGrowthRestored { get; set; }
        public int GeneticAlgorithmDeckCount { get; set; }
        public int DeckVersionGrowth { get; set; }
        public int OriginalDeckVersionGrowth { get; set; }
        public bool OriginalNormalCardActionCompleted { get; set; }
        public bool ModelStateMatch { get; set; }
        public string? OriginalCombatDigest { get; set; }
        public string? ReplayedCombatDigest { get; set; }
        public List<string> OriginalChoiceOptions { get; set; } = new();
        public List<string> ReplayedChoiceOptions { get; set; } = new();
        public bool HasFailure => Error != null || Phases.Values.Any(p => p.Status is "FAIL" or "SKIP");

        public void InitializePhases()
        {
            foreach (var phase in new[]
            {
                "bootstrap", "native-save-load", "native-card-generation-rng-probe", "record",
                "potion-choice-replay", "genetic-algorithm", "model-state", "replay-dispatch",
                "normal-card-action-replay", "post-replay-singleplayer-action"
            })
                Phase(phase, "SKIP");
        }

        public void Phase(string name, string status, string? detail = null) =>
            Phases[name] = new PhaseResult { Status = status, Detail = detail };
    }

    private sealed class PhaseResult
    {
        public string Status { get; set; } = "SKIP";
        public string? Detail { get; set; }
    }
}
