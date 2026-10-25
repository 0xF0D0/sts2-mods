using System.Reflection;
using System.Text.Json;

// Metadata-only: never load mod/native initializers or start Godot/the game.
if (args.Length != 2) throw new ArgumentException("Usage: MinimalCheck <mod-project-dir> <game-data-dir>");
string modRoot = Path.GetFullPath(args[0]);
string gameRoot = Path.GetFullPath(args[1]);
string assemblyPath = Path.Combine(modRoot, "bin/Release/net9.0/UndoSyncMinimal.dll");
var modules = Directory.GetDirectories(modRoot)
    .Select(Path.GetFileName).Where(n => n is not "bin" and not "obj").Order().ToArray();
Require(modules.SequenceEqual(new[] { "History", "Hook", "Network", "Replay", "UI" }), "exactly five production modules");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, "UndoSyncMinimal.json")));
Require(manifest.RootElement.GetProperty("id").GetString() == "UndoSyncMinimal", "manifest identity");
string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
var paths = Directory.GetFiles(runtime, "*.dll").Concat(Directory.GetFiles(gameRoot, "*.dll"))
    .Append(assemblyPath).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
var mod = context.LoadFromAssemblyPath(assemblyPath);
var game = context.LoadFromAssemblyPath(Path.Combine(gameRoot, "sts2.dll"));
const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
int patches = 0;
foreach (var type in mod.GetTypes())
{
    foreach (var attribute in type.GetCustomAttributesData().Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
    {
        var target = (Type)attribute.ConstructorArguments[0].Value!;
        string methodName = (string)attribute.ConstructorArguments[1].Value!;
        var methods = target.GetMethods(all).Where(m => m.Name == methodName).ToArray();
        Require(methods.Length == 1, $"unambiguous patch {target.Name}.{methodName}");
        var method = methods[0];
        foreach (var patch in type.GetMethods(all).Where(m => m.Name is "Prefix" or "Postfix"))
        {
            foreach (var parameter in patch.GetParameters())
            {
                string name = parameter.Name!;
                if (name == "__instance")
                    Require(parameter.ParameterType.FullName == target.FullName, $"{type.Name} instance injection");
                else if (name == "__result")
                    Require(parameter.ParameterType.GetElementType()?.FullName == method.ReturnType.FullName,
                        $"{type.Name} result injection");
                else
                    Require(method.GetParameters().Any(p => p.Name == name && p.ParameterType.FullName == parameter.ParameterType.FullName),
                        $"{type.Name} named parameter {name}");
            }
        }
        patches++;
    }
}
Require(patches == 12, $"all 12 Harmony targets checked (actual {patches})");
var reflectionMembers = new (string Type, string Name, bool Property)[]
{
    ("MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplayWriter", "_replay", false),
    ("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet", "_actionQueues", false),
    ("MegaCrit.Sts2.Core.Nodes.Combat.NCardPlayQueue", "_playQueue", false),
    ("MegaCrit.Sts2.Core.Runs.RunManager", "ShouldSave", true),
    ("MegaCrit.Sts2.Core.Nodes.Cards.NCard", "_titleLabel", false),
    ("MegaCrit.Sts2.Core.Nodes.Cards.NCard", "_descriptionLabel", false),
    ("MegaCrit.Sts2.Core.Nodes.Cards.NCard", "_typeLabel", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_visuals", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_outline", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_image", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_label", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_hsv", false),
    ("MegaCrit.Sts2.Core.Nodes.CommonUi.NPopupYesNoButton", "_outlineMaterial", false)
};
foreach (var member in reflectionMembers)
{
    var type = game.GetType(member.Type, throwOnError: true)!;
    Require(member.Property ? type.GetProperty(member.Name, all)?.SetMethod is not null : type.GetField(member.Name, all) is not null,
        $"reflection member {type.Name}.{member.Name}");
}
var queues = game.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet", true)!;
var nested = queues.GetNestedType("ActionQueue", BindingFlags.NonPublic)!;
Require(nested.GetField("actions", all)?.FieldType.GetGenericArguments().Single().Name == "GameAction", "literal queue contains GameAction");
var message = mod.GetType("UndoSyncMinimal.UndoMinimalMessage", true)!;
Require(message.IsPublic && message.IsValueType && message.GetInterfaces().Any(i => i.Name == "INetMessage"), "discoverable native wire message");
var entry = mod.GetType("UndoSyncMinimal.Hooks", true)!;
Require(entry.GetCustomAttributesData().Any(a => a.AttributeType.Name == "ModInitializerAttribute")
    && entry.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static) is not null, "mod initializer");
Console.WriteLine($"PASS metadata-only: five modules, manifest, {patches} patch targets and injected parameters, {reflectionMembers.Length + 1} reflection surfaces, message, initializer.");
Console.WriteLine("NOT TESTED: Harmony installation, Godot UI, serialization execution, replay, network transport or gameplay.");

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"OK {description}");
}
