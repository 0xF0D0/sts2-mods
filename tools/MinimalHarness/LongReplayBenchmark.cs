using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Unlocks;
using UndoSync;

namespace MinimalHarness;

// A bounded timing fixture, not content-fuzz coverage. Two legitimate shop
// Finesse cards + native deck removals produce a legal zero-cost draw/block loop.
// Play 500 real cards; never modify energy, enemies, queues or production history.
internal static class LongReplayBenchmark
{
    private static bool Active => CommandLineHelper.GetValue("minimal-suite") == "replay-long";
    private static ulong _start, _coreEnd, _visible;
    private static string? _error;
    private static bool _acceleratingFixture;

    internal static async Task Prepare()
    {
        foreach (var player in Harness.State.Players)
        {
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), showPreview: false);
            var pool = ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(UnlockState.all, CardMultiplayerConstraint.MultiplayerOnly)
                .Where(card => card is Finesse).ToArray();
            Harness.Require(pool.Length == 1, "Finesse is an unlocked native multiplayer colorless shop option");
            for (int i = 0; i < 2; i++)
            {
                var card = CardFactory.CreateForMerchant(player, pool, CardRarity.Uncommon).Card;
                Harness.Require(card is Finesse && (await CardPileCmd.Add(card, PileType.Deck)).success,
                    "native merchant factory and deck acquisition produce Finesse");
            }
        }
        Harness.Log("LONG_FIXTURE two shop Finesse per player; native removals; no TestMode or enemy/energy overrides");
    }

    private static async Task Play()
    {
        var player = LocalContext.GetMe(Harness.State)!;
        var card = PileType.Hand.GetPile(player).Cards.First(c => c is Finesse && c.CanPlay());
        var executor = Harness.Manager.ActionExecutor;
        bool finished = false;
        void Done(GameAction action) { if (action is PlayCardAction play && play.OwnerId == player.NetId) finished = true; }
        executor.AfterActionExecuted += Done;
        try
        {
            Harness.Require(card.TryManualPlay(null), "real Finesse manual play accepted");
            await Harness.Wait(() => finished, 10);
        }
        finally { executor.AfterActionExecuted -= Done; }
        Harness.RecordCard();
    }

    private static void ReportResources(string stage)
    {
        Harness.Log($"LONG_RESOURCES stage={stage}"
            + $" objects={Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}"
            + $" resources={Performance.GetMonitor(Performance.Monitor.ObjectResourceCount):F0}"
            + $" nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0}"
            + $" orphans={Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):F0}");
    }

    internal static async Task Run()
    {
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant; // setup only
        _acceleratingFixture = CommandLineHelper.HasArg("minimal-fast-fixture");
        await Harness.StateBarrier("long-opening");
        ReportResources("before-capture");
        if (Harness.Role == "host")
        {
            for (int i = 0; i < 500; i++)
            {
                await Play();
                if ((i + 1) % 50 == 0) Harness.Log($"LONG_GENERATED actions={i + 1} events={History.Count}");
            }
            await Harness.Idle();
            await Harness.TestControl.Put("long-generated", "500");
        }
        await Harness.Wait(() => Harness.TestControl.Read("long-generated") is not null, 180);
        await Harness.Idle(240);
        await Harness.StateBarrier("long-target", 240);
        int target = History.Count;
        string expected = Harness.Snapshot();
        if (Harness.Role == "host") { await Play(); await Harness.TestControl.Put("long-extra", "true"); }
        await Harness.Wait(() => Harness.TestControl.Read("long-extra") is not null);
        await Harness.Idle();
        _acceleratingFixture = false;
        ReportResources("after-capture");
        var game = NGame.Instance!;
        if (CommandLineHelper.HasArg("minimal-resource-probe"))
        {
            // Let short-lived native VFX from accelerated capture expire on
            // both variants before measuring the same history.
            await game.ToSignal(game.GetTree().CreateTimer(3), SceneTreeTimer.SignalName.Timeout);
            ReportResources("before-replay");
        }
        var replay = History.Freeze(target);
        int actions = replay.events.Count(e => e.eventType == CombatReplayEventType.GameAction);
        Harness.Require(actions == 500, "exactly 500 real recorded GameActions in measured prefix");
        var writer = new PacketWriter();
        foreach (var entry in replay.events) writer.Write(entry);
        string eventHash = Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
        File.WriteAllBytes(Path.Combine(Harness.DirectoryPath, $"long-plan.{Harness.Role}.bin"), History.Export(target));
        int repeats = int.Parse(CommandLineHelper.GetValue("minimal-long-repeats") ?? "1");
        Harness.Require(repeats is >= 1 and <= 10, "bounded long replay repeat count");
        for (int attempt = 1; attempt <= repeats; attempt++)
        {
            if (attempt > 1)
            {
                string extra = $"long-extra-{attempt}";
                if (Harness.Role == "host")
                {
                    await Play();
                    await Harness.TestControl.Put(extra, "true");
                }
                await Harness.Wait(() => Harness.TestControl.Read(extra) is not null);
                await Harness.Idle();
            }
            await Measure(attempt, repeats, target, expected, actions, eventHash);
        }
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        if (Harness.Role == "host") { await Play(); await Harness.TestControl.Put("long-continued", "true"); }
        await Harness.Wait(() => Harness.TestControl.Read("long-continued") is not null);
        await Harness.Idle();
        await Harness.StateBarrier("long-final");
        await Harness.Barrier("long-terminal");
    }

    private static async Task Measure(int attempt, int repeats, int target, string expected, int actions, string eventHash)
    {
        _start = _coreEnd = _visible = 0;
        _error = null;
        if (attempt > 1)
        {
            var writer = new PacketWriter();
            foreach (var entry in History.Freeze(target).events) writer.Write(entry);
            Harness.Require(Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition))) == eventHash,
                "repeated undo retains the exact original action prefix");
        }
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Normal;
        await Harness.Barrier($"long-measure-ready-{attempt}");
        if (Harness.Role == "host") Network.Request(target);
        await Harness.Wait(() => Network.Voting);
        Network.Vote(true);
        await Harness.Wait(() => !Network.Busy, 230);
        bool completed = Network.Status == "Undo complete";
        ulong terminal = Time.GetTicksMsec();
        var sample = new
        {
            role = Harness.Role, pid = System.Environment.ProcessId, attempt, actions, events = target,
            event_sha256 = eventHash, target_sha256 = expected, completed,
            core_ms = _coreEnd > 0 ? (long)(_coreEnd - _start) : -1,
            visible_ms = _visible > 0 ? (long)(_visible - _start) : -1,
            input_ready_ms = _start > 0 ? (long)(terminal - _start) : -1,
            error = _error, status = Network.Status,
            original_speed = "Normal", speed_after = SaveManager.Instance.PrefsSave.FastMode.ToString()
        };
        string sampleName = attempt == 1 ? "long-speed" : $"long-speed-{attempt}";
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"{sampleName}.{Harness.Role}.json"), JsonSerializer.Serialize(sample));
        Harness.Log("LONG_MEASURED " + JsonSerializer.Serialize(sample));
        Harness.Require(completed, "500-action replay completed: " + Network.Status);
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == expected, "500-action replay exact native target state");
        Harness.Require(SaveManager.Instance.PrefsSave.FastMode == FastModeType.Normal, "original Normal speed preserved");
        Harness.RecordUndo();
        await Harness.Barrier($"long-restored-{attempt}");
        if (CommandLineHelper.HasArg("minimal-resource-probe"))
        {
            ReportResources($"after-replay-{attempt}");
            var game = NGame.Instance!;
            await game.ToSignal(game.GetTree().CreateTimer(3), SceneTreeTimer.SignalName.Timeout);
            ReportResources($"after-settle-{attempt}");
            if ((attempt == 1 || attempt == repeats) && CommandLineHelper.HasArg("minimal-gc-resource-probe"))
            {
                // Diagnostic only, after the timing window. Allow the game
                // thread to keep processing native releases during collection.
                await Harness.Barrier($"long-gc-ready-{attempt}");
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                for (int i = 0; i < 5; i++) await game.AwaitProcessFrame();
                ReportResources($"after-gc-{attempt}");
            }
        }
    }

    // This optional fixture setup shortens live capture. It is off on every
    // process before the measurement barrier. Compare the saved event and target
    // hashes to the original fixture to prove that the input is identical.
    [HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.GetTweenForCardsChangingPiles), new[] { typeof(IEnumerable<CardPileAddResult>), typeof(bool) })]
    private static class AccelerateFixture
    {
        private static void Postfix(ref (Tween?, bool) __result)
        {
            if (!_acceleratingFixture || __result.Item1 is not { } tween) return;
            tween.FastForwardToCompletion();
            if (!tween.IsRunning()) __result = (null, __result.Item2);
        }
    }

    // Observe production entry/exit only. No timing override, fake completion,
    // production field mutation or alternate restore path in either version.
    [HarmonyPatch(typeof(Replay), nameof(Replay.ExecuteAsync))]
    private static class MeasureCore
    {
        private static void Prefix() { if (Active) _start = Time.GetTicksMsec(); }
        private static void Postfix(ref Task<uint> __result) { if (Active) __result = Observe(__result); }
        private static async Task<uint> Observe(Task<uint> original)
        {
            try { return await original; }
            catch (Exception error) { _error = error.ToString(); throw; }
            finally { _coreEnd = Time.GetTicksMsec(); }
        }
    }
    [HarmonyPatch(typeof(Replay), nameof(Replay.ResumeNative))]
    private static class MeasureVisible
    {
        private static void Postfix() { if (Active) _visible = Time.GetTicksMsec(); }
    }
}
