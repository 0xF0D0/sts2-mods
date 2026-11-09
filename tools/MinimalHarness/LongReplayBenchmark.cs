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

    internal static async Task Run()
    {
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant; // setup only
        await Harness.StateBarrier("long-opening");
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
        var replay = History.Freeze(target);
        int actions = replay.events.Count(e => e.eventType == CombatReplayEventType.GameAction);
        Harness.Require(actions == 500, "exactly 500 real recorded GameActions in measured prefix");
        var writer = new PacketWriter();
        foreach (var entry in replay.events) writer.Write(entry);
        string eventHash = Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
        File.WriteAllBytes(Path.Combine(Harness.DirectoryPath, $"long-plan.{Harness.Role}.bin"), History.Export(target));
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Normal;
        await Harness.Barrier("long-measure-ready");
        if (Harness.Role == "host") Network.Request(target);
        await Harness.Wait(() => Network.Voting);
        Network.Vote(true);
        await Harness.Wait(() => !Network.Busy, 230);
        bool completed = Network.Status == "Undo complete";
        ulong terminal = Time.GetTicksMsec();
        var sample = new
        {
            role = Harness.Role, pid = System.Environment.ProcessId, actions, events = target,
            event_sha256 = eventHash, target_sha256 = expected, completed,
            core_ms = _coreEnd > 0 ? (long)(_coreEnd - _start) : -1,
            visible_ms = _visible > 0 ? (long)(_visible - _start) : -1,
            input_ready_ms = _start > 0 ? (long)(terminal - _start) : -1,
            error = _error, status = Network.Status,
            original_speed = "Normal", speed_after = SaveManager.Instance.PrefsSave.FastMode.ToString()
        };
        File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"long-speed.{Harness.Role}.json"), JsonSerializer.Serialize(sample));
        Harness.Log("LONG_MEASURED " + JsonSerializer.Serialize(sample));
        Harness.Require(completed, "500-action replay completed: " + Network.Status);
        await Harness.Idle();
        Harness.Require(History.Count == target && Harness.Snapshot() == expected, "500-action replay exact native target state");
        Harness.Require(SaveManager.Instance.PrefsSave.FastMode == FastModeType.Normal, "original Normal speed preserved");
        Harness.RecordUndo();
        await Harness.Barrier("long-restored");
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        if (Harness.Role == "host") { await Play(); await Harness.TestControl.Put("long-continued", "true"); }
        await Harness.Wait(() => Harness.TestControl.Read("long-continued") is not null);
        await Harness.Idle();
        await Harness.StateBarrier("long-final");
        await Harness.Barrier("long-terminal");
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
