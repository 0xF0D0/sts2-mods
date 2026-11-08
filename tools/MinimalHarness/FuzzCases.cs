using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MinimalHarness;

// Schema 1 is a test artifact, not a production save. Only host resolves counts/IDs.
internal static class FuzzCases
{
    internal sealed record Loadout(int Relics, int Potions, int Cards, int[] UpgradeSlots);
    internal sealed record Spec(int Schema, string ContentSeed, Loadout[] Players);
    internal static Random Seeded(string seed) => new(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed)), 0));
    internal static string ContentSeed = "";
    internal static string? Repro;
    internal static readonly List<string> Resolved = new();
    private static int _step;

    // Every replica advances the native RNG/grab bags through the same factory call,
    // then checks the host's resolved ID BEFORE obtaining/adding it. No peer rolls
    // an independent loadout and no factory side effects are replaced with IDs.
    private static async Task Confirm(string description)
    {
        string key = $"fixture-step-{_step++}";
        if (Harness.Role == Harness.Roles[0]) await Harness.TestControl.Put(key, description);
        await Harness.Wait(() => Harness.TestControl.Read(key) is not null);
        Harness.Require(Harness.TestControl.Read(key) == description, "FIXTURE_ERROR host factory resolution: " + description);
        if (Repro is not null)
        {
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Repro, "fixture-resolved.json")))!;
            Harness.Require(saved[_step - 1] == description, "FIXTURE_ERROR saved factory resolution: " + description);
        }
        Resolved.Add(description);
        Harness.Log("FIXTURE " + description);
    }

    internal static async Task Prepare()
    {
        try
        {
            Spec? spec = null;
            if (Harness.Role == Harness.Roles[0])
            {
                if (Repro is not null) spec = JsonSerializer.Deserialize<Spec>(File.ReadAllText(Path.Combine(Repro, "content-fixture.json")))!;
                else
                {
                    var rng = Seeded(ContentSeed);
                    spec = new Spec(1, ContentSeed, Harness.State.Players.Select(p => new Loadout(
                        rng.Next(1, 3), rng.Next(1, Math.Min(2, p.MaxPotionCount) + 1), rng.Next(2, 5),
                        Enumerable.Range(0, p.Deck.Cards.Count).Where(_ => rng.Next(100) < 30).ToArray())).ToArray());
                }
                await Harness.TestControl.Put("content-fixture", JsonSerializer.Serialize(spec));
            }
            await Harness.Wait(() => Harness.TestControl.Read("content-fixture") is not null);
            spec = JsonSerializer.Deserialize<Spec>(Harness.TestControl.Read("content-fixture")!)!;
            Harness.Require(spec.Schema == 1 && spec.ContentSeed == ContentSeed && spec.Players.Length == Harness.State.Players.Count,
                "FIXTURE_ERROR specification matches native run");
            File.WriteAllText(Path.Combine(Harness.DirectoryPath, $"content-fixture.{Harness.Role}.json"), JsonSerializer.Serialize(spec));
            if (Harness.Role == Harness.Roles[0]) File.WriteAllText(Path.Combine(Harness.DirectoryPath, "content-fixture.json"), JsonSerializer.Serialize(spec));
            for (int slot = 0; slot < spec.Players.Length; slot++)
            {
                var p = Harness.State.Players[slot];
                var plan = spec.Players[slot];
                for (int i = 0; i < plan.Relics; i++)
                {
                    var relic = RelicFactory.PullNextRelicFromFront(p, p.PlayerRng.Rewards);
                    await Confirm($"player={slot} relic={relic.Id}");
                    Harness.Require(p.GetRelicById(relic.Id) is null, "FIXTURE_ERROR no duplicate relic");
                    await RelicCmd.Obtain(relic.ToMutable(), p);
                }
                for (int i = 0; i < plan.Potions; i++)
                {
                    var potion = PotionFactory.CreateRandomPotionOutOfCombat(p, p.PlayerRng.Rewards);
                    await Confirm($"player={slot} potion={potion.Id}");
                    Harness.Require((await PotionCmd.TryToProcure(potion.ToMutable(), p)).success, "FIXTURE_ERROR potion procured");
                }
                foreach (var result in CardFactory.CreateForReward(p, plan.Cards, CardCreationOptions.ForRoom(p, RoomType.Monster)))
                {
                    await Confirm($"player={slot} card={result.Card.Id} upgrade={result.Card.CurrentUpgradeLevel}");
                    Harness.Require((await CardPileCmd.Add(result.Card, PileType.Deck)).success, "FIXTURE_ERROR reward card added");
                }
                foreach (int index in plan.UpgradeSlots)
                {
                    var card = p.Deck.Cards[index];
                    Harness.Require(card.IsUpgradable, "FIXTURE_ERROR requested native upgrade eligible");
                    await Confirm($"player={slot} upgrade={index}:{card.Id}");
                    CardCmd.Upgrade(card);
                }
            }
            if (Harness.Role == Harness.Roles[0]) File.WriteAllText(Path.Combine(Harness.DirectoryPath, "fixture-resolved.json"), JsonSerializer.Serialize(Resolved));
            await Harness.Barrier("fixture-complete");
        }
        catch (Exception ex) { throw new InvalidOperationException("FIXTURE_ERROR native acquisition failed", ex); }
    }

    internal static async Task Encounter(EncounterModel encounter)
    {
        // Encounter is pulled by native CreateRoom from the real act's seeded
        // generated Monster sequence, rather than choosing ModelDb.AllEncounters.
        await Confirm("native-map encounter=" + encounter.Id);
        if (Harness.Role == Harness.Roles[0]) File.WriteAllText(Path.Combine(Harness.DirectoryPath, "fixture-resolved.json"), JsonSerializer.Serialize(Resolved));
    }
}
