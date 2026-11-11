using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using UndoSync;

namespace MinimalHarness;

internal static class FuzzDriver
{
    internal sealed record Step(string Kind, string Label, string Actor, int Index, string Model, int Target, int[]? Choices = null);
    private static readonly List<Step> Saved = new();
    private static readonly List<Step> Trace = new();
    private static Random _random = null!;
    internal static bool Active;
    internal static string ScheduleSeed = "";
    private static string TracePath => Path.Combine(Harness.DirectoryPath, $"trace.{Harness.Role}.jsonl");
    private static int _cursor;
    internal static bool Phases;
    internal static readonly Dictionary<string, int> Coverage = new() { ["busy_votes"] = 0, ["preparing_before_release"] = 0, ["skew_holds"] = 0, ["recorder_faults"] = 0 };
    private sealed record Schedule(string LastVoter, string[] VoteOrder, int[] DelayFrames, string SlowRole, string RecorderFault);
    private static string? _liveArm, _replayArm;
    private static string _round = "";
    private static Schedule? _schedule;

    private static async Task<Schedule> ResolveSchedule(string proposer)
    {
        string key = "fuzz-schedule-" + proposer;
        if (Harness.Role == Harness.Roles[0])
        {
            Step step;
            if (FuzzCases.Repro is not null) step = Replay("schedule", proposer);
            else
            {
                var voters = Harness.Roles.Where(r => r != proposer).OrderBy(_ => _random.Next()).ToArray();
                var plan = new Schedule(voters[^1], voters, voters.Select(_ => _random.Next(0, 7)).ToArray(),
                    Harness.Roles[_random.Next(Harness.Roles.Length)], voters[0]);
                step = new Step("schedule", proposer, proposer, 0, JsonSerializer.Serialize(plan), -1);
            }
            await Harness.TestControl.Put(key, JsonSerializer.Serialize(step));
        }
        await Harness.Wait(() => Harness.TestControl.Read(key) is not null);
        var resolved = JsonSerializer.Deserialize<Step>(Harness.TestControl.Read(key)!)!;
        if (Harness.Role != Harness.Roles[0] && FuzzCases.Repro is not null)
            Harness.Require(Replay("schedule", proposer) == resolved, "TRACE_ERROR saved schedule");
        Record(resolved);
        return JsonSerializer.Deserialize<Schedule>(resolved.Model)!;
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    private static class PhaseHold
    {
        private static void Postfix(ref Task __result)
        {
            if (!Active || !Phases) return;
            if (!Network.Executing && _liveArm is { } live)
            {
                _liveArm = null;
                __result = Hold(__result, "live", live);
            }
            else if (Network.Executing && _replayArm is { } replay)
            {
                _replayArm = null;
                __result = Hold(__result, "replay", replay);
            }
        }
        private static async Task Hold(Task original, string kind, string label)
        {
            await original;
            Harness.Log($"PHASE_HOLD kind={kind} round={label}");
            await Harness.TestControl.Put($"{kind}-held-{label}", Harness.Role);
            if (kind == "replay") Coverage["skew_holds"]++;
            await Harness.Wait(() => Harness.TestControl.Read($"{kind}-release-{label}") is not null, 20);
            Harness.Log($"PHASE_RELEASE kind={kind} round={label}");
        }
    }
    private static Step Record(Step step)
    {
        Trace.Add(step);
        File.AppendAllText(TracePath, JsonSerializer.Serialize(step) + "\n");
        return step;
    }
    private static Step Replay(string kind, string label)
    {
        var step = Saved[_cursor++];
        Harness.Require(step.Kind == kind && step.Label == label, "TRACE_ERROR saved step order");
        return step;
    }
    internal static void Initialize()
    {
        _random = FuzzCases.Seeded(ScheduleSeed);
        if (FuzzCases.Repro is not null)
            Saved.AddRange(File.ReadLines(Path.Combine(FuzzCases.Repro, $"trace.{Harness.Role}.jsonl")).Select(s => JsonSerializer.Deserialize<Step>(s)!));
    }

    private static Creature? Target(CardModel c) => c.TargetType switch
    {
        TargetType.AnyEnemy => CombatManager.Instance.DebugOnlyGetState()!.Enemies.FirstOrDefault(e => e.IsAlive),
        TargetType.AnyAlly => c.Owner.Creature,
        _ => null
    };

    private static async Task Play(string label, string actor, bool held = false)
    {
        await Harness.Idle();
        var player = Harness.State.Players[Array.IndexOf(Harness.Roles, actor)];
        string key = "fuzz-action-" + label;
        if (Harness.Role == Harness.Roles[0])
        {
            Step step;
            if (FuzzCases.Repro is not null) step = Replay("play", label);
            else
            {
                var options = PileType.Hand.GetPile(player).Cards.Select((c, index) => (c, index))
                    .Where(pair => pair.c.CanPlayTargeting(Target(pair.c))).ToList();
                Harness.Require(options.Count > 0, "UNCOVERED no legal action for " + actor);
                var selected = options[_random.Next(options.Count)];
                step = new Step("play", label, actor, selected.index, selected.c.Id.ToString(),
                    Target(selected.c) is { } target ? CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList().IndexOf(target) : -1);
            }
            await Harness.TestControl.Put(key, JsonSerializer.Serialize(step));
        }
        await Harness.Wait(() => Harness.TestControl.Read(key) is not null);
        var plan = JsonSerializer.Deserialize<Step>(Harness.TestControl.Read(key)!)!;
        if (Harness.Role != Harness.Roles[0] && FuzzCases.Repro is not null)
            Harness.Require(Replay("play", label) == plan, "TRACE_ERROR peer action differs from saved trace");
        Record(plan);
        Harness.Require(plan.Actor == actor, "TRACE_ERROR actor matches schedule");
        int before = History.Count;
        if (Harness.Role == actor)
        {
            var card = PileType.Hand.GetPile(player).Cards[plan.Index];
            Harness.Require(card.Id.ToString() == plan.Model, "TRACE_ERROR actual hand matches resolved action");
            bool finished = false;
            var executor = Harness.Manager.ActionExecutor;
            void Done(GameAction action) { if (action is PlayCardAction play && play.OwnerId == player.NetId && play.CardModelId == card.Id) finished = true; }
            executor.AfterActionExecuted += Done;
            try
            {
                if (held) _liveArm = label;
                Harness.Log($"FUZZ_PLAY label={label} card={card.Id} owner={player.NetId}");
                var target = card.TargetType == TargetType.AnyEnemy
                    ? CombatManager.Instance.DebugOnlyGetState()!.Enemies.ToList()[plan.Target]
                    : card.TargetType == TargetType.AnyAlly ? player.Creature : null;
                Harness.Require(card.TryManualPlay(target), "native fuzz action enqueued");
                await Harness.Wait(() => (held ? Harness.TestControl.Read("live-held-" + label) is not null : finished) && History.Count > before);
            }
            finally { executor.AfterActionExecuted -= Done; }
            Harness.RecordCard();
            await Harness.TestControl.Put("fuzz-finished-" + label, History.Count.ToString());
        }
        await Harness.Wait(() => Harness.TestControl.Read("fuzz-finished-" + label) is not null);
        await Harness.Wait(() => History.Count >= int.Parse(Harness.TestControl.Read("fuzz-finished-" + label)!));
        // Only the acting peer is held after executing the card. Remote peers
        // may still be finishing a choice or moving the card out of play.
        if (!held || Harness.Role != actor) await Harness.Idle();
        await Harness.StateBarrier("fuzz-action-" + label);
    }

    private static async Task ProbeChecksums(string round, Schedule phase, NetChecksumData stale)
    {
        await Harness.Wait(() => Harness.TestControl.Read("replay-held-" + round) is not null, 90);
        await Harness.Wait(() => Harness.Roles.Where(r => r != phase.SlowRole)
            .All(r => Harness.TestControl.Read("phase-fast-done-" + round + "-" + r) is not null), 90);
        Harness.Require(!Harness.Manager.ChecksumTracker.IsEnabled && UndoSync.Replay.SuppressNative, "native checksums remain off while other peers replay");
        uint before = Harness.Manager.ChecksumTracker.NextId;
        Harness.Manager.ChecksumTracker.GenerateChecksum("retained-session suppression probe", null);
        Harness.Require(Harness.Manager.ChecksumTracker.NextId == before && before == 0, "native checksum probe did not generate/send/advance ID");
        int dropped = UndoSync.Replay.DroppedChecksumMessages;
        await Harness.Barrier("checksum-probe-ready-" + round);
        Harness.Log("DECLARED_FAULT stale_native_checksum=true round=" + round);
        if (Harness.Role == Harness.Roles[0])
            Harness.Manager.NetService.SendMessage(new StateDivergenceMessage
            {
                senderChecksum = stale, senderCombatState = NetFullCombatState.FromRun(Harness.State, null)
            });
        else Harness.Manager.NetService.SendMessage(new ChecksumDataMessage { checksumData = stale });
        int expected = Harness.Role == Harness.Roles[0] ? Harness.Roles.Length - 1 : 1;
        await Harness.Wait(() => UndoSync.Replay.DroppedChecksumMessages >= dropped + expected);
        await Harness.Barrier("checksum-probe-checked-" + round);
        Harness.Log("CHECKSUM_ISOLATION_PROBE_PASS round=" + round);
    }

    internal static async Task Run()
    {
        await FuzzCases.Encounter(CombatManager.Instance.DebugOnlyGetState()!.Encounter!);
        await Harness.StateBarrier("fuzz-opening");
        // Retain a non-empty prefix to exercise event replay, then remove one real
        // native action per proposer. An empty/no-op case cannot satisfy the gate.
        await Play("prefix", Harness.Roles[0]);
        foreach (string proposer in Harness.Roles)
        {
            _round = proposer;
            _schedule = Phases ? await ResolveSchedule(proposer) : null;
            NetChecksumData stale = Harness.LastNativeChecksum;
            string expected = Harness.Snapshot();
            int target = History.Count;
            await Play("discard-" + proposer, proposer);
            if (_schedule is { } schedule)
            {
                await Play("held-" + proposer, schedule.LastVoter, held: true);
                if (Harness.Role == schedule.RecorderFault)
                {
                    History.Clear(); Coverage["recorder_faults"]++;
                    Harness.Log("DECLARED_FAULT recorder_absence=true round=" + proposer);
                }
                if (Harness.Role == schedule.SlowRole) _replayArm = proposer;
                await Harness.Barrier("phase-fixture-" + proposer);
                if (Harness.Role == proposer) await Harness.Wait(Hooks.IsIdle);
            }
            if (Harness.Role == proposer) await Harness.RequestViaViewport(target);
            if (Harness.Roles.Length > 1)
            {
                await Harness.Wait(() => Network.Busy && Network.Voting && UI.CurrentPopup is not null);
                await Harness.Barrier("fuzz-proposal-" + proposer);
            }
            if (_schedule is { } phase)
            {
                for (int i = 0; i < phase.VoteOrder.Length; i++)
                {
                    string voter = phase.VoteOrder[i];
                    if (Harness.Role == voter)
                    {
                        for (int frame = 0; frame < phase.DelayFrames[i]; frame++) await NGame.Instance!.AwaitProcessFrame();
                        if (voter == phase.LastVoter) Harness.Require(!Hooks.IsIdle(), "last voter observed native action executing");
                        await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "fuzz-accept");
                        Harness.Require(Network.LocalVoted, "native viewport fuzz vote retained");
                        if (voter == phase.LastVoter)
                        {
                            Harness.Require(!Hooks.IsIdle(), "last native UI accept retained while still busy");
                            Coverage["busy_votes"]++;
                            Harness.Log("PHASE_BUSY_ACCEPT retained=true round=" + proposer);
                        }
                        await Harness.TestControl.Put("phase-voted-" + proposer + "-" + voter, "true");
                    }
                    await Harness.Wait(() => Harness.TestControl.Read("phase-voted-" + proposer + "-" + voter) is not null);
                }
                if (Harness.Role == Harness.Roles[0])
                {
                    await Harness.Wait(() => Network.Preparing);
                    Harness.Require(Harness.TestControl.Read("live-release-held-" + proposer) is null,
                        "all votes reached host before held action drained");
                    Coverage["preparing_before_release"]++;
                    Harness.Log("PHASE_ALL_VOTES_WHILE_BUSY round=" + proposer);
                    await Harness.TestControl.Put("live-release-held-" + proposer, "true");
                }
                if (Harness.Role != phase.SlowRole)
                {
                    await Harness.Wait(() => Network.Status == "Replay finished: waiting for all players", 90);
                    await Harness.TestControl.Put("phase-fast-done-" + proposer + "-" + Harness.Role, "true");
                }
                await ProbeChecksums(proposer, phase, stale);
                if (Harness.Role == Harness.Roles[0])
                {
                    await Harness.Wait(() => Harness.Roles.Where(r => r != phase.SlowRole)
                        .All(r => Harness.TestControl.Read("phase-fast-done-" + proposer + "-" + r) is not null), 90);
                    Harness.Require(Harness.TestControl.Read("replay-held-" + proposer) == phase.SlowRole,
                        "slow replay observed while all other peers are at Done barrier");
                    await Harness.TestControl.Put("replay-release-" + proposer, "true");
                }
            }
            else if (Harness.Role != proposer)
            {
                await Harness.ClickViaViewport(UI.CurrentPopup!.GetNode<NPopupYesNoButton>("VerticalPopup/YesButton"), "fuzz-accept");
                Harness.Require(Network.LocalVoted, "native viewport fuzz vote retained");
            }
            await Harness.Wait(() => !Network.Busy, 120);
            Harness.Require(Network.Status == "Undo complete", "fuzz terminal native resume");
            await Harness.Idle();
            // The native evoke animation briefly keeps the outgoing orb node in
            // the scene. Check the settled view after the protocol has released it.
            if (!ModelReplayProbe.OrbViewsMatch())
            {
                Harness.Log("FUZZ_ORB_VIEW_SETTLING round=" + proposer);
                await Harness.Wait(ModelReplayProbe.OrbViewsMatch, 3);
            }
            Harness.Require(ModelReplayProbe.OrbViewsMatch(), "fuzz restored orb view matches native queue");
            Harness.Require(History.Count == target && Harness.Snapshot() == expected, "fuzz complete native target restored");
            Harness.RecordUndo();
            Record(new Step("target", proposer, proposer, target, expected, -1));
            if (FuzzCases.Repro is not null)
            {
                var saved = Replay("target", proposer);
                Harness.Require(saved.Index == target && saved.Model == expected, "TRACE_ERROR saved target digest");
            }
            await Harness.StateBarrier("fuzz-restored-" + proposer);
        }
        await Play("continued", Harness.Roles[^1]);
        if (Harness.Roles.Length > 1) await Harness.CheckConnectedUi("fuzz-final");
        Harness.Require(FuzzCases.Repro is null || _cursor == Saved.Count, "TRACE_ERROR consumed complete saved trace");
        await Harness.Barrier("fuzz-terminal");
    }

    // The wire choice type is determined by the native selection API, not by
    // the card pile (simple grids may also contain deck/hand cards).
    private enum ChoiceKind { Unknown, Index, Indexes, Combat, Deck }
    private static readonly AsyncLocal<ChoiceKind> Context = new();
    private static readonly Dictionary<string, ChoiceKind> Methods = new()
    {
        [nameof(CardSelectCmd.FromChooseACardScreen)] = ChoiceKind.Index,
        [nameof(CardSelectCmd.FromSimpleGrid)] = ChoiceKind.Indexes,
        [nameof(CardSelectCmd.FromSimpleGridForRewards)] = ChoiceKind.Indexes,
        [nameof(CardSelectCmd.FromCombatPile)] = ChoiceKind.Combat,
        [nameof(CardSelectCmd.FromHand)] = ChoiceKind.Combat,
        [nameof(CardSelectCmd.FromHandForDiscard)] = ChoiceKind.Combat,
        [nameof(CardSelectCmd.FromHandForUpgrade)] = ChoiceKind.Combat,
        [nameof(CardSelectCmd.FromDeckForUpgrade)] = ChoiceKind.Deck,
        [nameof(CardSelectCmd.FromDeckForTransformation)] = ChoiceKind.Deck,
        [nameof(CardSelectCmd.FromDeckForEnchantment)] = ChoiceKind.Deck,
        [nameof(CardSelectCmd.FromDeckGeneric)] = ChoiceKind.Deck
    };
    [HarmonyPatch]
    private static class SelectionContext
    {
        private static IEnumerable<MethodBase> TargetMethods() => typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => Methods.ContainsKey(m.Name));
        private static void Prefix(MethodBase __originalMethod, out ChoiceKind __state)
        {
            __state = Context.Value;
            if (Active) Context.Value = Methods[__originalMethod.Name];
        }
        private static void Postfix(ChoiceKind __state) { if (Active) Context.Value = __state; }
    }
    internal sealed class Selector : ICardSelector
    {
        private int _selection;
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            Harness.Require(!Network.Executing, "replay uses recorded choices rather than live selector");
            var list = options.ToList();
            string label = "choice-" + _selection++;
            var kind = Context.Value;
            Harness.Require(kind != ChoiceKind.Unknown, "UNCOVERED unsupported native choice API");
            int[] indices;
            Step? savedChoice = FuzzCases.Repro is not null ? Replay("choice", label) : null;
            if (savedChoice is not null) indices = savedChoice.Choices!;
            else indices = Enumerable.Range(0, list.Count).OrderBy(_ => _random.Next()).Take(Math.Min(list.Count, Math.Max(minSelect, 1))).ToArray();
            Harness.Require(indices.Length >= minSelect && indices.Length <= maxSelect && indices.Distinct().Count() == indices.Length && indices.All(i => i >= 0 && i < list.Count), "TRACE_ERROR legal choice cardinality");
            var selected = indices.Select(i => list[i]).ToList();
            string models = string.Join(",", selected.Select(c => c.Id));
            var step = new Step("choice", label, Harness.Role, (int)kind, models, -1, indices);
            Harness.Require(savedChoice is null || savedChoice.Index == (int)kind && savedChoice.Model == models, "TRACE_ERROR saved choice models and encoding");
            Record(step);
            var player = LocalContext.GetMe(Harness.State)!;
            uint id = Harness.Manager.PlayerChoiceSynchronizer.ChoiceIds[Harness.State.GetPlayerSlotIndex(player)] - 1;
            await NGame.Instance!.AwaitProcessFrame();
            var result = kind switch
            {
                ChoiceKind.Index => PlayerChoiceResult.FromIndex(indices.Length == 0 ? null : indices[0]),
                ChoiceKind.Indexes => PlayerChoiceResult.FromIndexes(indices.ToList()),
                ChoiceKind.Combat => PlayerChoiceResult.FromMutableCombatCards(selected),
                ChoiceKind.Deck => PlayerChoiceResult.FromMutableDeckCards(selected),
                _ => throw new InvalidOperationException("Unsupported native choice encoding")
            };
            Harness.Manager.PlayerChoiceSynchronizer.SyncLocalChoice(player, id, result);
            Harness.RecordSelection();
            Harness.Log($"FUZZ_CHOICE kind={kind} id={id} cards={models}");
            return selected;
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("UNCOVERED native reward selection");
    }
}
