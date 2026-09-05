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
            report.BinaryHashes["0Harmony.dll"] = HashFile(Path.Combine(GetSts2DataDir(), "0Harmony.dll"));
            report.BinaryHashes["GodotSharp.dll"] = HashFile(Path.Combine(GetSts2DataDir(), "GodotSharp.dll"));
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
            // SetUpTest intentionally omits GenerateRooms. A real save must contain every act's RoomSet.
            manager.GenerateRooms();
            manager.Launch();
            manager.ChecksumTracker.IsEnabled = true;
            await manager.SetActInternal(0);

            var encounter = state.Act.AllRegularEncounters.FirstOrDefault()
                ?? throw new InvalidOperationException("Act 0 has no regular encounter");
            var validMapCoord = state.Map.StartingMapPoint.coord;
            report.OriginalMapCoord = validMapCoord.ToString();
            state.AddVisitedMapCoord(validMapCoord);
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
            var originalActions = new List<GameAction>();
            Action<GameAction> originalActionTracker = action => originalActions.Add(action);
            manager.ActionQueueSet.ActionEnqueued += originalActionTracker;
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
            report.OriginalPotionRngDigest = NativeRngDigest(state);

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
            report.OriginalFinishedActions = originalActions.Count(a => a.State == GameActionState.Finished);
            report.OriginalCanceledActions = originalActions.Count(a => a.State == GameActionState.Canceled);
            manager.ActionQueueSet.ActionEnqueued -= originalActionTracker;

            // Write the raw private replay packet so the original net IDs are preserved. WriteReplay intentionally
            // anonymizes IDs and consumes chaotic RNG, which would make this in-memory identity test ambiguous.
            var recordedReplay = GetRecordedReplay(manager.CombatReplayWriter);
            var rawReplayWriter = new PacketWriter();
            rawReplayWriter.Write(recordedReplay);
            var replayBytes = rawReplayWriter.Buffer.AsSpan(0, rawReplayWriter.BytePosition).ToArray();
            var replayPath = Path.Combine(report.UserDataDir, "undo-replay-lab-native-raw.mcr").Replace('\\', '/');
            File.WriteAllBytes(replayPath, replayBytes);
            report.ReplayPath = replayPath;
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

            for (var trialNumber = 1; trialNumber <= 2; trialNumber++)
            {
                try
                {
                    await RunFreshReplayTrialAsync(report, replayBytes, initialBytes, encounter.Id, validMapCoord, trialNumber);
                }
                catch (Exception ex)
                {
                    report.Phase($"replay-trial-{trialNumber}", "FAIL", ex.Message);
                    report.Error = AppendError(report.Error, $"Replay trial {trialNumber} failed: {ex}");
                }
            }
            report.CheckpointSha256AfterTrials = Sha256(initialBytes);
            report.ImmutableCheckpointPreserved = string.Equals(report.InitialSaveSha256, report.CheckpointSha256AfterTrials, StringComparison.Ordinal);
            report.Phase("checkpoint-immutability", report.ImmutableCheckpointPreserved ? "PASS" : "FAIL",
                report.ImmutableCheckpointPreserved ? null : "Frozen pre-combat save bytes changed across replay trials");
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

    private static async Task RunFreshReplayTrialAsync(LabReport report, byte[] replayBytes, byte[] initialBytes,
        ModelId encounterId, MegaCrit.Sts2.Core.Map.MapCoord expectedMapCoord, int trialNumber)
    {
        RunManager manager = RunManager.Instance;
        TestMode.TurnOnInternal();
        try
        {
            var replay = DeserializePacket<CombatReplay>(replayBytes);
            if (replay.events.Count == 0)
                throw new InvalidDataException("Replay event list is empty for the exercised target");

            // Deserialize fresh immutable bytes for every trial; no RunState or SerializableRun object is shared.
            var fresh = RunState.FromSerializable(DeserializeSave(initialBytes));
            var singleplayer = new NetSingleplayerGameService();
            if (fresh.Players.Count != 1 || fresh.Players[0].NetId != singleplayer.NetId)
                throw new InvalidDataException("Frozen save does not contain the expected singleplayer net ID");

            // SetUpReplay calls InitializeSavedRun and avoids SetUpTest's InitializeNewRun bag/hook mutations.
            manager.SetUpReplay(fresh, replay, fresh.Players[0].NetId);
            // The harness is an isolated headless diagnostic; there is no run lobby for combat synchronization.
            manager.CombatStateSynchronizer.IsDisabled = true;
            report.ReplayShouldSaveBeforeSuppression = manager.ShouldSave;
            if (!report.ReplayShouldSaveBeforeSuppression)
                throw new InvalidOperationException("SetUpReplay did not enable native saving before lab suppression");
            DisableSaving(manager);
            report.ReplaySaveSuppressed = !manager.ShouldSave;
            report.ReplaySaveSetup = report.ReplayShouldSaveBeforeSuppression && report.ReplaySaveSuppressed;
            manager.Launch();
            manager.ChecksumTracker.IsEnabled = true;
            // Native load flow calls GenerateMap after SetUpReplay/Launch; this consumes SavedMapsToLoad
            // and installs the serialized map without regenerating it from RNG.
            await manager.GenerateMap();
            var currentMapPoint = fresh.CurrentMapPoint
                ?? throw new InvalidDataException("Fresh saved state has no current map point after GenerateMap");
            report.ReplayedMapCoord = currentMapPoint.coord.ToString();
            if (!currentMapPoint.coord.Equals(expectedMapCoord))
                throw new InvalidDataException($"Fresh current map coord {currentMapPoint.coord} did not match original {expectedMapCoord}");
            var encounter = fresh.Act.AllRegularEncounters.FirstOrDefault(e => e.Id == encounterId)
                ?? throw new InvalidOperationException($"Fresh saved state cannot resolve recorded encounter {encounterId.Entry}");
            await manager.EnterRoomDebug(RoomType.Monster, MegaCrit.Sts2.Core.Map.MapPointType.Unassigned,
                encounter.ToMutable(), showTransition: false);
            var player = LocalContext.GetMe(fresh) ?? throw new InvalidOperationException("Fresh replay local player unavailable");
            await WaitForCombatAsync(player);

            ChoiceOptionTrace.Clear();
            manager.ActionQueueSet.FastForwardNextActionId(replay.nextActionId);
            manager.ActionQueueSynchronizer.FastForwardHookId(replay.nextHookId);
            manager.PlayerChoiceSynchronizer.FastForwardChoiceIds(replay.choiceIds);
            var replayActions = new List<GameAction>();
            Action<GameAction> replayActionTracker = action => replayActions.Add(action);
            manager.ActionQueueSet.ActionEnqueued += replayActionTracker;
            var potionBoundary = FindPotionBoundary(replay.events);
            var trialReplayedEvents = 0;
            var trialReplayedActions = 0;
            var trialReplayedChoices = 0;
            for (var i = 0; i < replay.events.Count; i++)
            {
                var replayEvent = replay.events[i];
                await DispatchReplayEventAsync(manager, fresh, replayEvent);
                report.ReplayedEvents++;
                trialReplayedEvents++;
                if (replayEvent.eventType == CombatReplayEventType.GameAction)
                {
                    report.ReplayedActions++;
                    trialReplayedActions++;
                }
                if (replayEvent.eventType == CombatReplayEventType.PlayerChoice)
                {
                    report.ReplayedChoices++;
                    trialReplayedChoices++;
                }
                if (i + 1 == potionBoundary)
                {
                    // The prefix contains both potion actions and their choice/resume events. Wait once at
                    // this stable boundary so the post-potion native RNG can be compared.
                    await WaitForStableBoundaryAsync(manager);
                    report.ReplayedPotionRngDigest = NativeRngDigest(fresh);
                }
            }
            // Pump the complete event stream before the final wait. Choice and resume events must arrive while actions suspend.
            await WaitForStableBoundaryAsync(manager);
            manager.ActionQueueSet.ActionEnqueued -= replayActionTracker;

            var replayedChoiceOptions = ChoiceOptionTrace.ToList();
            var replayedAttackPotionActionCompleted = !player.Potions.OfType<AttackPotion>().Any();
            var replayedSkillPotionActionCompleted = !player.Potions.OfType<SkillPotion>().Any();
            var potionChoiceReplayMatch = report.AttackPotionActionCompleted && report.SkillPotionActionCompleted
                && replayedAttackPotionActionCompleted && replayedSkillPotionActionCompleted
                && report.RecordedChoices > 0 && report.RecordedChoices == trialReplayedChoices
                && report.OriginalChoiceOptions.SequenceEqual(replayedChoiceOptions);
            report.ReplayedChoiceOptions = replayedChoiceOptions;
            report.ReplayedAttackPotionActionCompleted |= replayedAttackPotionActionCompleted;
            report.ReplayedSkillPotionActionCompleted |= replayedSkillPotionActionCompleted;
            report.PotionChoiceReplayMatch = report.ReplayTrials.Count == 0
                ? potionChoiceReplayMatch : report.PotionChoiceReplayMatch && potionChoiceReplayMatch;
            report.Phase("potion-choice-replay", potionChoiceReplayMatch ? "PASS" : "FAIL",
                potionChoiceReplayMatch ? null : $"Trial {trialNumber}: actual choice options or choice event counts differed");

            var trialFinishedActions = replayActions.Count(a => a.State == GameActionState.Finished);
            var trialCanceledActions = replayActions.Count(a => a.State == GameActionState.Canceled);
            report.ReplayedFinishedActions += trialFinishedActions;
            report.ReplayedCanceledActions += trialCanceledActions;
            var potionRngMatch = string.Equals(report.OriginalPotionRngDigest, report.ReplayedPotionRngDigest, StringComparison.Ordinal);
            report.PotionRngMatch = report.ReplayTrials.Count == 0
                ? potionRngMatch : report.PotionRngMatch && potionRngMatch;
            report.Phase("potion-rng-state", potionRngMatch ? "PASS" : "FAIL",
                potionRngMatch ? null : $"Trial {trialNumber}: native RNG state differed after the two actual potion actions");

            var permanentDeckGrowthRestored = CompareGeneticAlgorithm(fresh, report);
            report.PermanentDeckGrowthRestored = report.ReplayTrials.Count == 0
                ? permanentDeckGrowthRestored : report.PermanentDeckGrowthRestored && permanentDeckGrowthRestored;
            report.Phase("genetic-algorithm", permanentDeckGrowthRestored ? "PASS" : "FAIL",
                permanentDeckGrowthRestored ? null : $"Trial {trialNumber}: persistent DeckVersion growth did not match");

            report.ReplayedCombatDigest = NativeCombatDigest(fresh);
            var modelStateMatch = string.Equals(report.OriginalCombatDigest, report.ReplayedCombatDigest, StringComparison.Ordinal);
            report.ModelStateMatch = report.ReplayTrials.Count == 0
                ? modelStateMatch : report.ModelStateMatch && modelStateMatch;
            report.Phase("model-state", modelStateMatch ? "PASS" : "FAIL",
                modelStateMatch ? null : $"Trial {trialNumber}: NetFullCombatState native packet differed after replay");
            var replayDispatchPass = trialReplayedEvents > 0;
            report.Phase("replay-dispatch", replayDispatchPass ? "PASS" : "FAIL",
                replayDispatchPass ? null : $"Trial {trialNumber}: no replay events were dispatched");

            // SetUpReplay installs NetReplayGameService. RequestEnqueue has no replay service path, so do not call it
            // or label an offline enqueue as a live singleplayer handoff.
            report.Phase("post-replay-singleplayer-action", "UNSUPPORTED",
                "Native SetUpReplay uses NetReplayGameService; live RequestEnqueue continuation is not verified");
            var normalCardReplayPass = report.OriginalNormalCardActionCompleted
                && report.RecordedActions > 0 && trialReplayedActions == report.RecordedActions
                && trialFinishedActions >= report.RecordedActions;
            report.Phase("normal-card-action-replay", normalCardReplayPass ? "PASS" : "FAIL",
                normalCardReplayPass ? null : $"Trial {trialNumber}: normal queued card action did not finish in replay");

            report.ReplayTrials.Add(new ReplayTrialSummary
            {
                Trial = trialNumber,
                FrozenCheckpointSha256 = Sha256(initialBytes),
                ReplayedEvents = trialReplayedEvents,
                ReplayedActions = trialReplayedActions,
                ReplayedChoices = trialReplayedChoices,
                FinishedActions = trialFinishedActions,
                CanceledActions = trialCanceledActions,
                PotionOptions = replayedChoiceOptions,
                PotionRngDigest = report.ReplayedPotionRngDigest,
                CombatDigest = report.ReplayedCombatDigest,
                PotionChoicePass = potionChoiceReplayMatch,
                PotionRngPass = potionRngMatch,
                GeneticAlgorithmPass = permanentDeckGrowthRestored,
                ModelStatePass = modelStateMatch,
                ReplayDispatchPass = replayDispatchPass,
                NormalCardActionPass = normalCardReplayPass
            });
            report.Phase($"replay-trial-{trialNumber}", "PASS");
        }
        finally
        {
            try { manager.CleanUp(); } catch { }
            TestMode.IsOn = false;
        }
    }

    private static string AppendError(string? existing, string error)
        => string.IsNullOrEmpty(existing) ? error : existing + System.Environment.NewLine + error;

    private static int FindPotionBoundary(IReadOnlyList<CombatReplayEvent> events)
    {
        var gameActions = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].eventType != CombatReplayEventType.GameAction)
                continue;
            gameActions++;
            if (gameActions == 3)
                return i;
        }
        throw new InvalidDataException("Replay did not contain a third game action boundary after the two potion actions");
    }

    private static Task DispatchReplayEventAsync(RunManager manager, RunState state, CombatReplayEvent replayEvent)
    {
        switch (replayEvent.eventType)
        {
            case CombatReplayEventType.GameAction:
                Require(replayEvent.action != null && replayEvent.playerId.HasValue, "Malformed GameAction replay event");
                var actionPlayer = state.GetPlayer(replayEvent.playerId!.Value)
                    ?? throw new InvalidDataException($"GameAction references unknown player {replayEvent.playerId.Value}");
                manager.ActionQueueSet.EnqueueWithoutSynchronizing(replayEvent.action!.ToGameAction(actionPlayer));
                return Task.CompletedTask;
            case CombatReplayEventType.HookAction:
                Require(replayEvent.playerId.HasValue && replayEvent.hookId.HasValue && replayEvent.gameActionType.HasValue,
                    "Malformed HookAction replay event");
                manager.ActionQueueSet.EnqueueWithoutSynchronizing(manager.ActionQueueSynchronizer.GetHookActionForId(
                    replayEvent.hookId!.Value, replayEvent.playerId!.Value, replayEvent.gameActionType!.Value));
                return Task.CompletedTask;
            case CombatReplayEventType.ResumeAction:
                Require(replayEvent.actionId.HasValue, "Malformed ResumeAction replay event");
                manager.ActionQueueSet.ResumeActionWithoutSynchronizing(replayEvent.actionId!.Value);
                return Task.CompletedTask;
            case CombatReplayEventType.PlayerChoice:
                Require(replayEvent.playerId.HasValue && replayEvent.choiceId.HasValue && replayEvent.playerChoiceResult.HasValue,
                    "Malformed PlayerChoice replay event");
                var choicePlayer = state.GetPlayer(replayEvent.playerId!.Value)
                    ?? throw new InvalidDataException($"PlayerChoice references unknown player {replayEvent.playerId.Value}");
                manager.PlayerChoiceSynchronizer.ReceiveReplayChoice(choicePlayer, replayEvent.choiceId!.Value,
                    replayEvent.playerChoiceResult!.Value);
                return Task.CompletedTask;
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

    private static string NativeRngDigest(RunState state)
    {
        var packet = new PacketWriter();
        packet.Write(state.Rng.ToSerializable());
        return Sha256(packet.Buffer.AsSpan(0, packet.BytePosition).ToArray());
    }

    private static CombatReplay GetRecordedReplay(CombatReplayWriter writer)
    {
        // Verified installed decompile: CombatReplayWriter stores the active replay in private field _replay.
        var field = typeof(CombatReplayWriter).GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CombatReplayWriter).FullName, "_replay");
        return field.GetValue(writer) as CombatReplay
            ?? throw new InvalidOperationException("CombatReplayWriter did not contain a stable replay");
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
        public string? OriginalMapCoord { get; set; }
        public string? ReplayedMapCoord { get; set; }
        public Dictionary<string, string?> BinaryHashes { get; } = new();
        public Dictionary<string, PhaseResult> Phases { get; } = new();
        public string? Error { get; set; }
        public int InitialSaveBytes { get; set; }
        public string? InitialSaveSha256 { get; set; }
        public bool NativeSaveRoundTrip { get; set; }
        public bool ReplaySaveSetup { get; set; }
        public bool ReplayShouldSaveBeforeSuppression { get; set; }
        public bool ReplaySaveSuppressed { get; set; }
        public string? ReplayPath { get; set; }
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
        public int OriginalFinishedActions { get; set; }
        public int OriginalCanceledActions { get; set; }
        public int ReplayedFinishedActions { get; set; }
        public int ReplayedCanceledActions { get; set; }
        public string? OriginalPotionRngDigest { get; set; }
        public string? ReplayedPotionRngDigest { get; set; }
        public bool PotionRngMatch { get; set; }
        public bool ModelStateMatch { get; set; }
        public string? OriginalCombatDigest { get; set; }
        public string? ReplayedCombatDigest { get; set; }
        public List<string> OriginalChoiceOptions { get; set; } = new();
        public List<string> ReplayedChoiceOptions { get; set; } = new();
        public int ReplayTrialCount => ReplayTrials.Count;
        public List<ReplayTrialSummary> ReplayTrials { get; } = new();
        public string? CheckpointSha256AfterTrials { get; set; }
        public bool ImmutableCheckpointPreserved { get; set; }
        public bool HasFailure => Error != null || Phases.Values.Any(p => p.Status is "FAIL" or "SKIP");

        public void InitializePhases()
        {
            foreach (var phase in new[]
            {
                "bootstrap", "native-save-load", "record", "potion-choice-replay", "potion-rng-state",
                "genetic-algorithm", "model-state", "replay-dispatch", "checkpoint-immutability",
                "normal-card-action-replay", "post-replay-singleplayer-action", "replay-trial-1", "replay-trial-2"
            })
                Phase(phase, "SKIP");
        }

        public void Phase(string name, string status, string? detail = null)
        {
            if (!Phases.TryGetValue(name, out var existing) || existing.Status is "SKIP" or "RUNNING")
            {
                Phases[name] = new PhaseResult { Status = status, Detail = detail };
                return;
            }
            if (existing.Status == "FAIL" || status == "PASS" && existing.Status == "UNSUPPORTED")
                return;
            if (status == "FAIL")
                Phases[name] = new PhaseResult { Status = status, Detail = detail };
        }
    }

    private sealed class PhaseResult
    {
        public string Status { get; set; } = "SKIP";
        public string? Detail { get; set; }
    }

    private sealed class ReplayTrialSummary
    {
        public int Trial { get; set; }
        public string? FrozenCheckpointSha256 { get; set; }
        public int ReplayedEvents { get; set; }
        public int ReplayedActions { get; set; }
        public int ReplayedChoices { get; set; }
        public int FinishedActions { get; set; }
        public int CanceledActions { get; set; }
        public List<string> PotionOptions { get; set; } = new();
        public string? PotionRngDigest { get; set; }
        public string? CombatDigest { get; set; }
        public bool PotionChoicePass { get; set; }
        public bool PotionRngPass { get; set; }
        public bool GeneticAlgorithmPass { get; set; }
        public bool ModelStatePass { get; set; }
        public bool ReplayDispatchPass { get; set; }
        public bool NormalCardActionPass { get; set; }
    }
}
