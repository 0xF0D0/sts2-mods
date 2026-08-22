using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// SurfaceCheck — verifies, without launching the game, whether a game update breaks UndoSync's assumptions.
//
//   dotnet run -- check                 : run all five checks; exit 1 on any finding
//   dotnet run -- baseline              : regenerate surface-baseline.json from the current game DLL
//   dotnet run -- coverage-baseline     : top up snapshot-coverage.json with newly-seen fields
//   dotnet run -- copy-fields-baseline  : top up copy-fields.json with newly-seen fields
//   dotnet run -- net-state-baseline    : top up net-state-fields.json with newly-seen fields
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
// Check 4 (copy-field ledger): StateSnapshot.CopyMutableFields blindly reflects every
//   non-skipped, non-delegate, non-init-only instance field from a clone back onto the
//   live CardModel/OrbModel/PotionModel/RelicModel (and every subclass's own fields,
//   since it walks from the concrete runtime type). CopySkip is a hand-maintained
//   exclusion list with no matching inclusion list, so verify every field it would copy
//   is accounted for in copy-fields.json — either judged safe, or flagged. Answers "does
//   a human know about every field this reflective copy touches?", which Check 3 does
//   not cover (Check 3 is about StateSnapshot's own capture/restore fields, not this
//   separate blind-copy path). Each ledger entry also records the field's type and a
//   risk bucket (value/collection/reference-game/reference-other) derived from it, so a
//   type change is visible even when the name is not — the same shape as the bug already
//   hit, where CardModel._energyCost kept its name but CardEnergyCost._card ended up
//   pointing at a discarded clone — and so the 200+ entry backlog can be triaged by risk
//   instead of reviewed flat.
// Check 5 (NetFullCombatState field ledger): NetFullCombatState is exactly what
//   ChecksumTracker hashes and compares across peers (ChecksumTracker.cs:160 calls
//   NetFullCombatState.FromRun(_runState, action); GenerateChecksum(NetFullCombatState) at
//   :295-301 serializes it and XxHash32's the bytes) — so its own field surface, plus every
//   nested IPacketSerializable struct it recursively serializes (CreatureState/PowerState/
//   OrbState/PlayerState/CombatPileState/CardState/PotionState/RelicState), IS the exact
//   specification of "what must be identical after a restore." Checks 1-4 answer questions
//   about the mod's OWN assumptions (reflection targets, state-type surface, snapshot
//   coverage, copy-field ledger); Check 5 instead starts from the GAME's own authoritative
//   list of checksummed fields and asks, for each one, "does UndoSync's snapshot/restore (or
//   Check 3/4's existing ledgers, for fields covered by CardModel/OrbModel/PotionModel/
//   RelicModel's wholesale reflective copy) actually account for it?" — the question that
//   would have caught nextRewardIds (NetFullCombatState.cs:371/394) going unrestored: it
//   was present in Check 2's own surface-baseline.json the whole time, but nothing ever
//   asked "is this field, specifically, captured and restored somewhere?" Same ledger
//   shape/mechanics as Check 4 (net-state-fields.json: stale/type-changed/empty-reason all
//   FAIL; "UNREVIEWED" is a valid non-empty placeholder that shows up in a backlog, not a
//   silent pass) — see net-state-baseline below for how it's populated.

var mode = args.Length > 0 ? args[0] : "check";
string gameDataDir = ArgValue("--game") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");
string modSrcDir = ArgValue("--mod") ?? FindUp("UndoSync");
string baselinePath = ArgValue("--baseline") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "surface-baseline.json");
string coveragePath = ArgValue("--coverage") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "snapshot-coverage.json");
string copyFieldsPath = ArgValue("--copy-fields") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "copy-fields.json");
string netStatePath = ArgValue("--net-state") ?? Path.Combine(FindUp("tools"), "SurfaceCheck", "net-state-fields.json");

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

// ───────────── Check 4 data: the field surface StateSnapshot.CopyMutableFields touches ─────────────
//
// CopySkip must not be duplicated here — hardcoding those names would recreate the very
// drift this check exists to catch. Parse them out of StateSnapshot.cs itself (already
// read into srcFiles by Check 1) instead. A parse failure is a hard stop, not a silent
// fallback to a hardcoded list.

var stateSnapshotFile = srcFiles.FirstOrDefault(f => Path.GetFileName(f) == "StateSnapshot.cs");
if (stateSnapshotFile == null)
{
    Console.Error.WriteLine($"StateSnapshot.cs not found under {modSrcDir}; cannot parse CopySkip");
    return 2;
}
var copySkipMatch = Regex.Match(File.ReadAllText(stateSnapshotFile), @"CopySkip\s*=\s*new\(\)\s*\{([^}]*)\}", RegexOptions.Singleline);
if (!copySkipMatch.Success)
{
    Console.Error.WriteLine($"could not parse the CopySkip initializer out of {stateSnapshotFile} — refusing to fall back to a hardcoded list");
    return 2;
}
var copySkip = Regex.Matches(copySkipMatch.Groups[1].Value, @"""([^""]*)""").Select(m => m.Groups[1].Value).ToHashSet();

// typeof(Delegate) is a *runtime* Type; System.Reflection.Type.IsAssignableFrom always
// returns false across a MetadataLoadContext boundary because the reflection-only
// System.Delegate is a different Type object than the one `typeof` would give us here.
// So instead of `typeof(Delegate).IsAssignableFrom(fieldType)` (what CopyMutableFields
// itself does, safely, since it runs in the real load context), walk the field type's
// BaseType chain *within* `mlc` and compare by full name against System.Delegate /
// System.MulticastDelegate. That mirrors what IsAssignableFrom would have checked,
// without needing to resolve System.Delegate as a Type object in whichever core
// assembly happens to define it.
bool IsDelegateType(Type? t)
{
    for (; t != null; t = t.BaseType)
        if (t.FullName is "System.Delegate" or "System.MulticastDelegate")
            return true;
    return false;
}

// Risk buckets for a copy-field's TYPE, not its name. CopyMutableFields assigns by
// reference, so the only thing that determines whether that's dangerous is the field's
// shape:
//   value       — IsValueType (struct/enum/primitive) or string. Copying these by
//                 reference is meaningless; the only possible risk is a missed change
//                 notification, never aliasing.
//   collection   — a mutable container (List<>/HashSet<>/Dictionary<,>/etc., or anything
//                  implementing one of the generic collection interfaces). HIGHEST risk:
//                  the live model and the discarded clone end up sharing one container
//                  unless something re-clones it.
//   reference-game    — any other reference type under MegaCrit.*. SECOND highest: this is
//                        exactly the CardEnergyCost shape (a game object that can carry a
//                        back-reference into the clone it was made from).
//   reference-other   — every other reference type (BCL types not caught above, etc.).
string[] RiskBuckets = { "collection", "reference-game", "reference-other", "value" };

// Generic collection shapes worth flagging. Curated rather than "anything generic in
// System.Collections.Generic" so a stray IComparer<T>/IEqualityComparer<T> field (not a
// container) doesn't get misclassified as a collection.
var collectionGenericTypeDefs = new HashSet<string>(StringComparer.Ordinal)
{
    "System.Collections.Generic.List`1", "System.Collections.Generic.HashSet`1",
    "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.Queue`1",
    "System.Collections.Generic.Stack`1", "System.Collections.Generic.LinkedList`1",
    "System.Collections.Generic.SortedList`2", "System.Collections.Generic.SortedSet`1",
    "System.Collections.Generic.SortedDictionary`2",
    "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.ICollection`1",
    "System.Collections.Generic.IList`1", "System.Collections.Generic.IDictionary`2",
    "System.Collections.Generic.ISet`1", "System.Collections.Generic.IReadOnlyCollection`1",
    "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyDictionary`2",
};

// Same MetadataLoadContext caveat as IsDelegateType above: compare generic type
// definitions by FullName, never by reference-equality against a runtime `typeof(...)`.
bool IsCollectionType(Type t)
{
    bool Matches(Type candidate) => candidate.IsGenericType
        && collectionGenericTypeDefs.Contains(candidate.GetGenericTypeDefinition().FullName ?? "");
    return Matches(t) || t.GetInterfaces().Any(Matches);
}

string ClassifyRisk(Type fieldType)
{
    if (fieldType.IsValueType || fieldType.FullName == "System.String") return "value";
    if (IsCollectionType(fieldType)) return "collection";
    if (fieldType.FullName != null && fieldType.FullName.StartsWith("MegaCrit.", StringComparison.Ordinal)) return "reference-game";
    return "reference-other";
}

// Check 4's ledger now stores each field's type next to its reason, so a type change is
// visible even when the field name is not (see ClassifyRisk above — that's the whole
// point: a field going from `int` to `List<int>` flips its risk bucket with no rename).
// Ledgers from before this change only ever had a bare reason string per key. Tolerate
// that shape on read so `copy-fields-baseline` can migrate entries in place (reason
// preserved verbatim) and `check` degrades gracefully — skipping the type-change
// comparison, see below — instead of throwing on an un-migrated file.
SortedDictionary<string, CopyFieldEntry> LoadCopyFields(string path)
{
    var result = new SortedDictionary<string, CopyFieldEntry>(StringComparer.Ordinal);
    if (!File.Exists(path)) return result;
    var raw = JsonSerializer.Deserialize<SortedDictionary<string, JsonElement>>(File.ReadAllText(path))!;
    foreach (var (key, el) in raw)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            result[key] = new CopyFieldEntry { Reason = el.GetString() ?? "" };
        }
        else
        {
            result[key] = new CopyFieldEntry
            {
                Type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                Risk = el.TryGetProperty("risk", out var r) ? r.GetString() ?? "" : "",
                Reason = el.TryGetProperty("reason", out var rz) ? rz.GetString() ?? "" : "",
            };
        }
    }
    return result;
}

// The four root types CopyMutableFields is called on. It walks from the CONCRETE runtime
// type up to (but excluding) System.Object, so every subclass's own declared fields are
// in scope too — not just the four roots' own fields. Report each field once per
// DECLARING type, keyed "<DeclaringType.FullName>.<fieldName>", so a field on a shared
// base (e.g. AbstractModel) that all four roots reach isn't reported once per root.
string[] copyFieldsRootTypeNames =
{
    "MegaCrit.Sts2.Core.Models.CardModel", "MegaCrit.Sts2.Core.Models.OrbModel",
    "MegaCrit.Sts2.Core.Models.PotionModel", "MegaCrit.Sts2.Core.Models.RelicModel",
};

var copyFieldsLive = new SortedDictionary<string, LiveFieldInfo>(StringComparer.Ordinal);
var copyFieldsSummary = new List<(string Root, int DeclaringTypes, int Fields, Dictionary<string, int> RiskCounts)>();

foreach (var rootName in copyFieldsRootTypeNames)
{
    var root = Resolve(rootName);
    if (root == null) { Console.WriteLine($"  WARN  copy-fields root type unresolved: {rootName}"); continue; }

    var declaringTypesForRoot = new HashSet<string>();
    var fieldsForRoot = new HashSet<string>();
    var riskCountsForRoot = RiskBuckets.ToDictionary(b => b, _ => 0);

    foreach (var concrete in sts2.GetTypes().Where(root.IsAssignableFrom))
    {
        for (var t = concrete; t != null && t.FullName != "System.Object"; t = t.BaseType)
        {
            foreach (var f in t.GetFields(InstanceDeclared))
            {
                if (f.IsInitOnly) continue;
                if (IsDelegateType(f.FieldType)) continue;
                if (copySkip.Contains(f.Name)) continue;

                var key = $"{t.FullName}.{f.Name}";
                var risk = ClassifyRisk(f.FieldType);
                copyFieldsLive[key] = new LiveFieldInfo(f.FieldType.FullName ?? f.FieldType.Name, risk);
                declaringTypesForRoot.Add(t.FullName!);
                if (fieldsForRoot.Add(key)) riskCountsForRoot[risk]++;
            }
        }
    }

    copyFieldsSummary.Add((rootName, declaringTypesForRoot.Count, fieldsForRoot.Count, riskCountsForRoot));
}

if (mode == "copy-fields-baseline")
{
    var copyFields = LoadCopyFields(copyFieldsPath);

    // Drop stale entries first: named in the ledger but no longer part of the live
    // copy-field surface (renamed/removed field, or now caught by one of the filters).
    int droppedCf = 0;
    foreach (var staleKey in copyFields.Keys.Where(k => !copyFieldsLive.ContainsKey(k)).ToList())
    {
        copyFields.Remove(staleKey);
        droppedCf++;
    }

    // PRESERVE every remaining entry's reason verbatim — and, for an entry already
    // migrated to the typed format, its recorded type too, so a real type change (the
    // risk this ledger exists to catch) surfaces as a `check` failure instead of being
    // silently absorbed here. The one exception is an entry still in the pre-type
    // bare-string format (Type == ""): it never had a type recorded, so this fills one in
    // from the live surface now; its reason is untouched either way. New keys (not in the
    // ledger at all) get type/risk from the live surface and reason "UNREVIEWED".
    int addedCf = 0, migratedCf = 0;
    foreach (var (key, live) in copyFieldsLive)
    {
        if (copyFields.TryGetValue(key, out var existing))
        {
            if (existing.Type.Length == 0)
            {
                existing.Type = live.TypeFullName;
                existing.Risk = live.Risk;
                migratedCf++;
            }
            continue;
        }
        copyFields[key] = new CopyFieldEntry { Type = live.TypeFullName, Risk = live.Risk, Reason = "UNREVIEWED" };
        addedCf++;
    }

    File.WriteAllText(copyFieldsPath, JsonSerializer.Serialize(copyFields, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"\ncopy-fields baseline updated: {copyFieldsPath} ({copyFields.Count} field(s), {addedCf} newly marked UNREVIEWED, {droppedCf} stale dropped, {migratedCf} migrated to typed entries)");
    return failures == 0 ? 0 : 1;
}

// ───────────── Check 5 data: NetFullCombatState's checksummed field surface ─────────────
//
// NetFullCombatState.Serialize/Deserialize (NetFullCombatState.cs:469-521) touch exactly its 7
// declared members (nextChoiceIds, nextRewardIds, lastExecutedActionId, lastExecutedHookId,
// Creatures, Players, Rng), and each nested IPacketSerializable struct's own Serialize/
// Deserialize likewise touches exactly that struct's own declared fields (verified by reading
// NetFullCombatState.cs directly — every nested struct's Serialize method writes every field it
// declares, no exceptions). So walking every declared field of NetFullCombatState plus its
// nested IPacketSerializable struct types reconstructs the exact checksummed field surface
// without needing to parse the hand-written Serialize bodies themselves at runtime. The
// IPacketSerializable filter (rather than "every nested type") excludes compiler-generated
// nested types NetFullCombatState.ToString()'s lambdas produce (e.g. an `<>c` cached-delegate
// holder) that aren't part of the wire format.
string netStateRootTypeName = "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState";
const string IPacketSerializableFullName = "MegaCrit.Sts2.Core.Multiplayer.Serialization.IPacketSerializable";
var netStateFieldsLive = new SortedDictionary<string, string>(StringComparer.Ordinal); // key -> field type full name
var netStateRoot = Resolve(netStateRootTypeName);
int netStateTypeCount = 0;
if (netStateRoot == null)
{
    Console.WriteLine($"  WARN  Check 5 root type unresolved: {netStateRootTypeName}");
}
else
{
    var netStateTypes = new List<Type> { netStateRoot };
    netStateTypes.AddRange(netStateRoot.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
        .Where(t => t.GetInterfaces().Any(i => i.FullName == IPacketSerializableFullName)));
    netStateTypeCount = netStateTypes.Count;

    foreach (var t in netStateTypes)
        foreach (var f in t.GetFields(InstanceDeclared))
            netStateFieldsLive[$"{t.FullName}.{f.Name}"] = f.FieldType.FullName ?? f.FieldType.Name;
}

// Same ledger shape as copy-fields.json (Type + Reason), minus the risk bucket: that field
// exists there to flag CopyMutableFields' aliasing risk, which has no equivalent here — this
// ledger's only question per field is "is its post-restore value accounted for", so a reason
// string covers it.
SortedDictionary<string, NetStateFieldEntry> LoadNetStateFields(string path)
{
    if (!File.Exists(path)) return new SortedDictionary<string, NetStateFieldEntry>(StringComparer.Ordinal);
    return JsonSerializer.Deserialize<SortedDictionary<string, NetStateFieldEntry>>(File.ReadAllText(path))!;
}

if (mode == "net-state-baseline")
{
    var netStateFields = LoadNetStateFields(netStatePath);

    // Drop stale entries: named in the ledger but no longer part of the live checksummed
    // surface (renamed/removed field on NetFullCombatState or one of its nested structs).
    int droppedNs = 0;
    foreach (var staleKey in netStateFields.Keys.Where(k => !netStateFieldsLive.ContainsKey(k)).ToList())
    {
        netStateFields.Remove(staleKey);
        droppedNs++;
    }

    // PRESERVE every remaining entry's reason AND recorded type verbatim — a real type change
    // (e.g. nextRewardIds widening from List<int> to List<long>) must surface as a `check`
    // failure below, not be silently absorbed by re-running this. Only ever fill in a blank
    // type (first-time baseline) or add a brand-new key, as UNREVIEWED.
    int addedNs = 0;
    foreach (var (key, typeFullName) in netStateFieldsLive)
    {
        if (netStateFields.TryGetValue(key, out var existing))
        {
            if (existing.Type.Length == 0) existing.Type = typeFullName;
            continue;
        }
        netStateFields[key] = new NetStateFieldEntry { Type = typeFullName, Reason = "UNREVIEWED" };
        addedNs++;
    }

    File.WriteAllText(netStatePath, JsonSerializer.Serialize(netStateFields, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"\nnet-state-fields baseline updated: {netStatePath} ({netStateFields.Count} field(s), {addedNs} newly marked UNREVIEWED, {droppedNs} stale dropped)");
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

// ───────────── Check 4: CopyMutableFields field ledger ─────────────

int copyFieldFailures = 0;

if (!File.Exists(copyFieldsPath))
{
    Console.WriteLine($"\n── Check 4: CopyMutableFields field ledger — no ledger file at {copyFieldsPath}; skipping (generate one with `dotnet run -- copy-fields-baseline`) ──");
}
else
{
    var copyFields = LoadCopyFields(copyFieldsPath);
    Console.WriteLine($"\n── Check 4: CopyMutableFields field ledger — does every blindly-copied field have a human verdict? ──");
    foreach (var (root, declTypes, fieldCount, riskCounts) in copyFieldsSummary)
    {
        var bucketStr = string.Join(", ", RiskBuckets.Select(b => $"{b}={riskCounts[b]}"));
        Console.WriteLine($"  {root}: {declTypes} declaring type(s), {fieldCount} field(s)  [{bucketStr}]");
    }

    // Stale: named in the ledger but no longer part of the live copy-field surface.
    foreach (var stale in copyFields.Keys.Where(k => !copyFieldsLive.ContainsKey(k)))
    {
        Console.WriteLine($"  FAIL  {stale}: stale ledger entry (field no longer copied by CopyMutableFields)");
        copyFieldFailures++;
    }

    // Type changed: the field is still copied, but its recorded type no longer matches the
    // live build's. Reported distinctly from stale/unaccounted so the message says what
    // changed, not just that something did — this is exactly the CardEnergyCost shape: an
    // unrelated-looking field whose *type* started carrying a back-reference, with the name
    // unchanged. Entries not yet migrated to the typed format (Type == "") have nothing to
    // compare against and are skipped — run copy-fields-baseline first.
    foreach (var (key, entry) in copyFields)
    {
        if (entry.Type.Length == 0) continue;
        if (!copyFieldsLive.TryGetValue(key, out var live)) continue; // already reported as stale
        if (entry.Type != live.TypeFullName)
        {
            Console.WriteLine($"  FAIL  {key}: type changed ({entry.Type} -> {live.TypeFullName}, risk {entry.Risk} -> {live.Risk}) — re-review and update the ledger");
            copyFieldFailures++;
        }
    }

    // Every non-empty reason is required.
    foreach (var (key, entry) in copyFields)
        if (string.IsNullOrWhiteSpace(entry.Reason))
        {
            Console.WriteLine($"  FAIL  {key}: empty justification");
            copyFieldFailures++;
        }

    // Unaccounted: CopyMutableFields would copy it, but the ledger says nothing — the
    // case that would catch a new identity/back-reference field slipping past CopySkip.
    var accountedCf = new HashSet<string>(copyFields.Keys);
    var unaccountedCf = copyFieldsLive.Keys.Where(k => !accountedCf.Contains(k)).ToList();
    foreach (var key in unaccountedCf)
    {
        var live = copyFieldsLive[key];
        Console.WriteLine($"  FAIL  {key}: unaccounted for (would be copied, no ledger entry) [{live.Risk}, {live.TypeFullName}]");
        copyFieldFailures++;
    }

    var unreviewedCf = copyFields.Where(kv => kv.Value.Reason == "UNREVIEWED").Select(kv => kv.Key).ToList();
    Console.WriteLine($"  {copyFieldsLive.Count} live field(s), {copyFields.Count - unreviewedCf.Count} reviewed, {unreviewedCf.Count} unreviewed, {unaccountedCf.Count} unaccounted");

    // Break the backlog down by risk bucket instead of one flat list: collection and
    // reference-game are the ones that can actually alias the clone (the CardEnergyCost
    // shape), so those get listed by name — capped, with a "+N more" tail — since a human
    // has to judge each one. value fields can only ever miss a "no event fires" call, and
    // reference-other is everything else not already called out; both are merely counted
    // (already broken out per root in the summary table above).
    const int maxListedCf = 40;
    string RiskOf(string key) => copyFieldsLive.TryGetValue(key, out var lf) ? lf.Risk
        : (copyFields.TryGetValue(key, out var e) && e.Risk.Length > 0 ? e.Risk : "reference-other");

    Console.WriteLine($"\n── Check 4 UNREVIEWED backlog: {unreviewedCf.Count} field(s) nobody has judged yet ──");
    if (unreviewedCf.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var bucket in RiskBuckets)
        {
            var keysInBucket = unreviewedCf.Where(k => RiskOf(k) == bucket).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (keysInBucket.Count == 0) continue;

            if (bucket is "collection" or "reference-game")
            {
                Console.WriteLine($"  {bucket} ({keysInBucket.Count}):");
                foreach (var key in keysInBucket.Take(maxListedCf))
                    Console.WriteLine($"    UNREVIEWED  {key}");
                if (keysInBucket.Count > maxListedCf)
                    Console.WriteLine($"    +{keysInBucket.Count - maxListedCf} more");
            }
            else
            {
                Console.WriteLine($"  {bucket}: {keysInBucket.Count} (see summary table above)");
            }
        }
    }

    Console.WriteLine(copyFieldFailures == 0
        ? "  Check 4: every copied field is accounted for"
        : $"  Check 4: {copyFieldFailures} failure(s) — a game update added a copied field the ledger doesn't know about, changed a copied field's type, or a ledger entry is stale");
}

// ───────────── Check 5: NetFullCombatState checksummed-field ledger ─────────────

int netStateFailures = 0;

if (!File.Exists(netStatePath))
{
    Console.WriteLine($"\n── Check 5: NetFullCombatState field ledger — no ledger file at {netStatePath}; skipping (generate one with `dotnet run -- net-state-baseline`) ──");
}
else
{
    var netStateFields = LoadNetStateFields(netStatePath);
    Console.WriteLine($"\n── Check 5: NetFullCombatState field ledger — is every field the game checksums accounted for by UndoSync's snapshot/restore? ──");
    Console.WriteLine($"  {netStateFieldsLive.Count} live field(s) across {netStateTypeCount} type(s) (NetFullCombatState + its nested IPacketSerializable structs)");

    // Stale: named in the ledger but no longer part of the live checksummed surface.
    foreach (var stale in netStateFields.Keys.Where(k => !netStateFieldsLive.ContainsKey(k)))
    {
        Console.WriteLine($"  FAIL  {stale}: stale ledger entry (field no longer part of NetFullCombatState's serialized surface)");
        netStateFailures++;
    }

    // Type changed: still checksummed, but the ledger's recorded type no longer matches the
    // live build's — same "name unchanged, shape changed" risk Check 4 already guards against.
    foreach (var (key, entry) in netStateFields)
    {
        if (entry.Type.Length == 0) continue;
        if (!netStateFieldsLive.TryGetValue(key, out var liveType)) continue; // already reported as stale
        if (entry.Type != liveType)
        {
            Console.WriteLine($"  FAIL  {key}: type changed ({entry.Type} -> {liveType}) — re-review and update the ledger");
            netStateFailures++;
        }
    }

    // Every non-empty reason is required — "UNREVIEWED" counts (see the backlog below); a
    // truly blank reason does not. This is what turns "deliberately not restored" into a
    // recorded, auditable decision instead of a silent gap.
    foreach (var (key, entry) in netStateFields)
        if (string.IsNullOrWhiteSpace(entry.Reason))
        {
            Console.WriteLine($"  FAIL  {key}: empty justification");
            netStateFailures++;
        }

    // Unaccounted: the game checksums it, but the ledger says nothing at all — the case that
    // would have caught nextRewardIds going unrestored (it was already visible in Check 2's
    // surface-baseline.json, but nothing had ever asked this specific question about it).
    var accountedNs = new HashSet<string>(netStateFields.Keys);
    var unaccountedNs = netStateFieldsLive.Keys.Where(k => !accountedNs.Contains(k)).ToList();
    foreach (var key in unaccountedNs)
    {
        Console.WriteLine($"  FAIL  {key}: unaccounted for (game checksums it, no ledger entry) [{netStateFieldsLive[key]}]");
        netStateFailures++;
    }

    var unreviewedNs = netStateFields.Where(kv => kv.Value.Reason == "UNREVIEWED").Select(kv => kv.Key).ToList();
    Console.WriteLine($"  {netStateFieldsLive.Count} live field(s), {netStateFields.Count - unreviewedNs.Count} reviewed, {unreviewedNs.Count} unreviewed, {unaccountedNs.Count} unaccounted");

    Console.WriteLine($"\n── Check 5 UNREVIEWED backlog: {unreviewedNs.Count} field(s) nobody has judged yet ──");
    if (unreviewedNs.Count == 0) Console.WriteLine("  none");
    else foreach (var key in unreviewedNs.OrderBy(k => k, StringComparer.Ordinal)) Console.WriteLine($"  UNREVIEWED  {key}");

    Console.WriteLine(netStateFailures == 0
        ? "  Check 5: every checksummed field is accounted for"
        : $"  Check 5: {netStateFailures} failure(s) — a game update added checksummed state UndoSync doesn't know about, changed a field's type, or a ledger entry is stale");
}

return failures + diffs + coverageFailures + copyFieldFailures + netStateFailures == 0 ? 0 : 1;

sealed class TypeCoverage
{
    [System.Text.Json.Serialization.JsonPropertyName("captured")]
    public SortedDictionary<string, string> Captured { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("ignored")]
    public SortedDictionary<string, string> Ignored { get; set; } = new();
}

// A field's live shape, as computed from the current build via the MetadataLoadContext:
// its type's full name (what copy-fields.json records under "type") and the risk bucket
// ClassifyRisk derived from that type.
sealed class LiveFieldInfo
{
    public string TypeFullName { get; }
    public string Risk { get; }
    public LiveFieldInfo(string typeFullName, string risk) { TypeFullName = typeFullName; Risk = risk; }
}

// One copy-fields.json ledger entry. Type/Risk are derived facts about the current build
// (filled in by copy-fields-baseline); Reason is the only human-owned field, and the one
// thing that must always survive a baseline re-run verbatim.
sealed class CopyFieldEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("risk")]
    public string Risk { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

// One net-state-fields.json ledger entry (Check 5). Type is a derived fact about the current
// build (filled in by net-state-baseline); Reason is the only human-owned field — either a
// citation of the code that captures/restores this field, or a justification for why it is
// deliberately not independently restored. "UNREVIEWED" is a valid placeholder Reason (shows
// up in Check 5's backlog, does not fail the check); an EMPTY Reason does fail it.
sealed class NetStateFieldEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}
