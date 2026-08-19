using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// SurfaceCheck — verifies, without launching the game, whether a game update breaks UndoSync's assumptions.
//
//   dotnet run -- check              : run all three checks; exit 1 on any finding
//   dotnet run -- baseline           : regenerate surface-baseline.json from the current game DLL
//   dotnet run -- coverage-baseline  : top up snapshot-coverage.json with newly-seen fields
//
// Check 1 (reflection targets): extract every AccessTools.*/[HarmonyPatch] string
//   reference from the mod source and verify it still exists in the game assembly.
//   Catches renames/removals the compiler cannot see.
// Check 2 (state surface): dump the instance-field surface of state-bearing types
//   and diff against a committed baseline. New game state shows up here — the early
//   warning for the symmetric-omission class where the snapshot silently under-restores.
// Check 3 (snapshot coverage): for the types StateSnapshot deep-captures, verify every
//   instance field is explicitly accounted for in snapshot-coverage.json — either
//   captured, or deliberately ignored with a reason. Checks 1-2 answer "did the game
//   change?"; Check 3 answers "do we still capture everything?" — the question that let
//   Player.MaxPotionCount/PlayerRng/RelicGrabBag go silently missing, since a field we
//   never captured was already in the Check 2 baseline and a symmetric omission is
//   invisible to peer checksums.

var mode = args.Length > 0 ? args[0] : "check";
string gameDataDir = ArgValue("--game") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");
string modSrcDir = ArgValue("--mod") ?? FindUp("UndoSync");
string baselinePath = ArgValue("--baseline") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "surface-baseline.json");
string coveragePath = ArgValue("--coverage") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "snapshot-coverage.json");

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string FindUp(string dirName)
{
    var d = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (d != null)
    {
        var cand = Path.Combine(d.FullName, dirName);
        if (Directory.Exists(cand)) return cand;
        d = d.Parent;
    }
    throw new DirectoryNotFoundException($"'{dirName}' not found above cwd");
}

// State-bearing types (simple names; resolved to full names after load).
// A field added/changed on any of these means CombatSnapshot needs review.
string[] surfaceTypeNames =
{
    "CombatState", "CombatManager", "CombatHistory", "CombatTurnState",
    "Creature", "Player", "PlayerCombatState",
    "CardModel", "CardPile", "PowerModel", "PotionModel", "RelicModel",
    "MonsterModel", "MonsterMoveStateMachine", "OrbModel", "OrbQueue",
    // Sub-objects that carry an owner back-reference and are deep-cloned with their
    // owner. They never appear in a snapshot field list of their own, so nothing else
    // here watches them — and a stale back-reference on one of these is a real,
    // checksum-visible defect (see StateSnapshot.RebindDeepCloneOwnership).
    "CardEnergyCost", "EnchantmentModel", "AfflictionModel",
    "RunState", "RunRngSet", "PlayerRngSet", "Rng",
    "NetFullCombatState", "ChecksumTracker",
    "ActionQueueSet", "ActionQueueSynchronizer", "PlayerChoiceSynchronizer",
};

var sts2Path = Path.Combine(gameDataDir, "sts2.dll");
if (!File.Exists(sts2Path)) { Console.Error.WriteLine($"sts2.dll not found: {sts2Path}"); return 2; }

// The game ships its own .NET runtime; prefer the game's copies and dedupe by
// file name (MetadataLoadContext refuses to load the same assembly twice).
var resolverPaths = Directory.GetFiles(gameDataDir, "*.dll")
    .Concat(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
    .GroupBy(Path.GetFileName)
    .Select(g => g.First())
    .ToArray();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));
var sts2 = mlc.LoadFromAssemblyPath(sts2Path);

// simple name → types index
var byName = new Dictionary<string, List<Type>>();
foreach (var t in sts2.GetTypes())
{
    if (!byName.TryGetValue(t.Name, out var list)) byName[t.Name] = list = new List<Type>();
    list.Add(t);
}

Type? Resolve(string name)
{
    if (name.Contains('.'))
        return sts2.GetType(name);
    if (byName.TryGetValue(name, out var list))
        return list.Count == 1 ? list[0] : null; // ambiguous → null; try full name instead
    return null;
}

const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags InstanceDeclared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

// Types whose state StateSnapshot deep-captures (Check 3 / coverage-baseline default set).
// Full names: these are looked up directly, no simple-name ambiguity to worry about.
string[] snapshotCoverageTypeNames =
{
    "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
    "MegaCrit.Sts2.Core.Models.PowerModel",
    "MegaCrit.Sts2.Core.Models.MonsterModel",
    "MegaCrit.Sts2.Core.Models.PotionModel",
    "MegaCrit.Sts2.Core.Models.RelicModel",
    "MegaCrit.Sts2.Core.Entities.Players.Player",
    "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState",
    "MegaCrit.Sts2.Core.Entities.Cards.CardPile",
    "MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue",
    "MegaCrit.Sts2.Core.Combat.CombatState",
    // Added for Change B (turn coordination restore) / Change C (ActionQueueSet._wasReset
    // normalization) — StateSnapshot/ChecksumHook now read or write fields on both.
    "MegaCrit.Sts2.Core.Combat.CombatTurnState",
    "MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet",
};

List<string> FieldNamesOf(Type t) => t.GetFields(InstanceDeclared).Select(fi => fi.Name).ToList();

int failures = 0;
var warnings = new List<string>();

// ───────────── Check 1: game members the mod references by string ─────────────

var targets = new List<(string type, string member, string kind, string src)>();
var typeVarDefs = new Dictionary<string, string>(); // variable name → TypeByName full name
var srcFiles = Directory.GetFiles(modSrcDir, "*.cs");

foreach (var f in srcFiles)
{
    var text = File.ReadAllText(f);
    foreach (Match m in Regex.Matches(text, @"(\w+)\s*=\s*\n?\s*AccessTools\.TypeByName\(""([^""]+)""\)"))
        typeVarDefs[m.Groups[1].Value] = m.Groups[2].Value;
}

foreach (var f in srcFiles)
{
    var name = Path.GetFileName(f);
    var text = File.ReadAllText(f);

    foreach (Match m in Regex.Matches(text, @"AccessTools\.(Field|Property|Method)\(typeof\(([\w.]+)\),\s*""([^""]+)"""))
        targets.Add((m.Groups[2].Value, m.Groups[3].Value, m.Groups[1].Value, name));

    foreach (Match m in Regex.Matches(text, @"AccessTools\.(Field|Property|Method)\((\w+Type),\s*""([^""]+)"""))
    {
        var varName = m.Groups[2].Value;
        if (typeVarDefs.TryGetValue(varName, out var fullName))
            targets.Add((fullName, m.Groups[3].Value, m.Groups[1].Value, name));
        else
            warnings.Add($"dynamic type, cannot verify statically: {varName}.{m.Groups[3].Value} ({name})");
    }

    foreach (Match m in Regex.Matches(text, @"AccessTools\.TypeByName\(""([^""]+)""\)"))
        targets.Add((m.Groups[1].Value, "", "Type", name));

    foreach (Match m in Regex.Matches(text, @"\[HarmonyPatch\(typeof\(([\w.]+)\),\s*""([^""]+)"""))
        targets.Add((m.Groups[1].Value, m.Groups[2].Value, "Method", name));
}

Console.WriteLine($"── Check 1: {targets.Count} reflection/patch references from the mod ──");
foreach (var (typeName, member, kind, src) in targets.Distinct())
{
    var t = Resolve(typeName.Split('.').Last()) ?? Resolve(typeName);
    if (t == null) { Console.WriteLine($"  FAIL  type missing: {typeName}  ({src})"); failures++; continue; }
    if (kind == "Type") continue;

    bool ok = kind switch
    {
        "Field" => t.GetField(member, All) != null,
        "Property" => t.GetProperty(member, All) != null,
        "Method" => t.GetMethods(All).Any(mi => mi.Name == member)
                    || t.GetProperty(member.Replace("set_", "").Replace("get_", ""), All) != null,
        _ => false,
    };
    if (!ok) { Console.WriteLine($"  FAIL  {t.Name}.{member} ({kind})  ({src})"); failures++; }
    else if (kind == "Property" && t.GetProperty(member, All)?.SetMethod == null)
        warnings.Add($"get-only property: {t.Name}.{member} — SetValue would fail at runtime ({src})");
}
Console.WriteLine(failures == 0 ? "  all references valid" : $"  {failures} broken!");
foreach (var w in warnings) Console.WriteLine($"  WARN  {w}");

// ───────────── Check 2: state-type field surface ─────────────

var surface = new SortedDictionary<string, List<string>>();
foreach (var name in surfaceTypeNames)
{
    var t = Resolve(name);
    if (t == null) { Console.WriteLine($"  WARN  surface type unresolved (ambiguous/missing): {name}"); continue; }
    surface[t.FullName!] = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Select(fi => $"{fi.Name} : {fi.FieldType.Name}")
        .OrderBy(s => s).ToList();
}

var json = JsonSerializer.Serialize(surface, new JsonSerializerOptions { WriteIndented = true });

if (mode == "baseline")
{
    File.WriteAllText(baselinePath, json);
    Console.WriteLine($"\nbaseline updated: {baselinePath} ({surface.Count} types)");
    return failures == 0 ? 0 : 1;
}

if (mode == "coverage-baseline")
{
    var coverage = File.Exists(coveragePath)
        ? JsonSerializer.Deserialize<SortedDictionary<string, TypeCoverage>>(File.ReadAllText(coveragePath))!
        : new SortedDictionary<string, TypeCoverage>();

    // Union of what's already in the file and the canonical list, so re-running this
    // after the file is deleted (or hand-edited to add/drop a type) still converges.
    var coverageTypeNames = coverage.Keys.Union(snapshotCoverageTypeNames).OrderBy(s => s, StringComparer.Ordinal);

    int added = 0;
    foreach (var typeName in coverageTypeNames)
    {
        var t = Resolve(typeName);
        if (t == null) { Console.WriteLine($"  WARN  coverage type unresolved (ambiguous/missing): {typeName}"); continue; }
        if (!coverage.TryGetValue(typeName, out var tc)) coverage[typeName] = tc = new TypeCoverage();

        // PRESERVE every existing entry and its reason verbatim (never touched below);
        // only ever ADD an entry for a field seen in neither map, and only as "UNREVIEWED"
        // — never silently downgrade an existing captured/ignored reason.
        foreach (var fieldName in FieldNamesOf(t))
        {
            if (tc.Captured.ContainsKey(fieldName) || tc.Ignored.ContainsKey(fieldName)) continue;
            tc.Ignored[fieldName] = "UNREVIEWED";
            added++;
        }
    }

    File.WriteAllText(coveragePath, JsonSerializer.Serialize(coverage, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"\ncoverage baseline updated: {coveragePath} ({coverage.Count} types, {added} field(s) newly marked UNREVIEWED)");
    return failures == 0 ? 0 : 1;
}

Console.WriteLine($"\n── Check 2: field surface of {surface.Count} state types vs baseline ──");
if (!File.Exists(baselinePath))
{
    Console.WriteLine("  no baseline — generate one first with `dotnet run -- baseline`.");
    return 2;
}
var baseline = JsonSerializer.Deserialize<SortedDictionary<string, List<string>>>(File.ReadAllText(baselinePath))!;
int diffs = 0;
foreach (var key in baseline.Keys.Union(surface.Keys))
{
    var oldF = baseline.GetValueOrDefault(key, new List<string>());
    var newF = surface.GetValueOrDefault(key, new List<string>());
    foreach (var added in newF.Except(oldF)) { Console.WriteLine($"  ADDED    {key}: {added}"); diffs++; }
    foreach (var removed in oldF.Except(newF)) { Console.WriteLine($"  REMOVED  {key}: {removed}"); diffs++; }
}
if (diffs == 0) Console.WriteLine("  no changes — snapshot assumptions hold");
else Console.WriteLine($"  {diffs} changes! Review the CombatSnapshot capture list, then refresh the baseline");

// ───────────── Check 3: snapshot coverage ─────────────

int coverageFailures = 0;

if (!File.Exists(coveragePath))
{
    Console.WriteLine($"\n── Check 3: snapshot coverage — no coverage file at {coveragePath}; skipping (generate one with `dotnet run -- coverage-baseline`) ──");
}
else
{
    var coverage = JsonSerializer.Deserialize<SortedDictionary<string, TypeCoverage>>(File.ReadAllText(coveragePath))!;
    Console.WriteLine($"\n── Check 3: snapshot coverage of {coverage.Count} tracked types (do we still capture everything?) ──");

    var unreviewed = new List<(string Type, string Field)>();

    foreach (var (typeName, tc) in coverage)
    {
        var t = Resolve(typeName);
        if (t == null) { Console.WriteLine($"  FAIL  coverage type unresolved (ambiguous/missing): {typeName}"); coverageFailures++; continue; }

        var liveFields = FieldNamesOf(t).ToHashSet();

        // Stale entries: named in the coverage file but no longer exist on the type.
        foreach (var stale in tc.Captured.Keys.Concat(tc.Ignored.Keys).Distinct().Where(f => !liveFields.Contains(f)))
        {
            Console.WriteLine($"  FAIL  {t.Name}.{stale}: stale coverage entry (field no longer exists on the type)");
            coverageFailures++;
        }

        // Every non-empty reason is required, regardless of which map it's in.
        foreach (var (field, reason) in tc.Captured.Concat(tc.Ignored))
            if (string.IsNullOrWhiteSpace(reason))
            {
                Console.WriteLine($"  FAIL  {t.Name}.{field}: empty justification");
                coverageFailures++;
            }

        // Unaccounted: exist on the type but named in neither map — the case that would
        // have caught MaxPotionCount/PlayerRng/RelicGrabBag going missing.
        var accounted = new HashSet<string>(tc.Captured.Keys.Concat(tc.Ignored.Keys));
        var unaccounted = liveFields.Where(f => !accounted.Contains(f)).ToList();
        foreach (var field in unaccounted)
        {
            Console.WriteLine($"  FAIL  {t.Name}.{field}: unaccounted for (neither captured nor ignored)");
            coverageFailures++;
        }

        int unreviewedCount = 0;
        foreach (var (field, reason) in tc.Ignored)
            if (reason == "UNREVIEWED") { unreviewed.Add((t.Name, field)); unreviewedCount++; }

        Console.WriteLine($"  {t.Name}: {tc.Captured.Count} captured, {tc.Ignored.Count - unreviewedCount} ignored, {unreviewedCount} unreviewed, {unaccounted.Count} unaccounted");
    }

    Console.WriteLine($"\n── Check 3 UNREVIEWED backlog: {unreviewed.Count} field(s) nobody has judged yet ──");
    if (unreviewed.Count == 0) Console.WriteLine("  none");
    else foreach (var (type, field) in unreviewed) Console.WriteLine($"  UNREVIEWED  {type}.{field}");

    Console.WriteLine(coverageFailures == 0
        ? "  Check 3: every covered field is accounted for"
        : $"  Check 3: {coverageFailures} failure(s) — a game update added state StateSnapshot doesn't know about, or a coverage entry is stale");
}

return failures + diffs + coverageFailures == 0 ? 0 : 1;

sealed class TypeCoverage
{
    [System.Text.Json.Serialization.JsonPropertyName("captured")]
    public SortedDictionary<string, string> Captured { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("ignored")]
    public SortedDictionary<string, string> Ignored { get; set; } = new();
}
