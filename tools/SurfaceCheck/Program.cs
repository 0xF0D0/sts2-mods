using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// SurfaceCheck — verifies, without launching the game, whether a game update breaks UndoSync's assumptions.
//
//   dotnet run -- check      : run both checks; exit 1 on any finding
//   dotnet run -- baseline   : regenerate surface-baseline.json from the current game DLL
//
// Check 1 (reflection targets): extract every AccessTools.*/[HarmonyPatch] string
//   reference from the mod source and verify it still exists in the game assembly.
//   Catches renames/removals the compiler cannot see.
// Check 2 (state surface): dump the instance-field surface of state-bearing types
//   and diff against a committed baseline. New game state shows up here — the early
//   warning for the symmetric-omission class where the snapshot silently under-restores.

var mode = args.Length > 0 ? args[0] : "check";
string gameDataDir = ArgValue("--game") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");
string modSrcDir = ArgValue("--mod") ?? FindUp("UndoSync");
string baselinePath = ArgValue("--baseline") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "surface-baseline.json");

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

return failures + diffs == 0 ? 0 : 1;
