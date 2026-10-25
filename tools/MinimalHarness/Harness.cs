using System.Security.Cryptography;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using UndoSync;

namespace MinimalHarness;

[ModInitializer(nameof(Initialize))]
public static class Harness
{
    private static string _role = "", _directory = "", _stage = "startup";
    private static bool _active;
    private static int _peers = 2, _selections, _generation;
    private static string _suite = "basic";
    private static string[] Roles => Enumerable.Range(0, _peers).Select(i => i == 0 ? "host" : i == 1 ? "client" : $"client{i}").ToArray();
    private static bool AutoChoices => _suite is "extended" or "vote-loss" or "replay-loss";
    private static CharacterModel Character => _suite == "basic" || _role == "ui" && _suite != "choice-ui"
        ? ModelDb.Character<Ironclad>() : ModelDb.Character<Silent>();
    private static int _divergences, _cards, _undos;
    private static INetGameService? _pump;
    private static readonly CancellationTokenSource Lifetime = new();
    private static RunManager Manager => RunManager.Instance;
    private static RunState State => Manager.DebugOnlyGetState() ?? throw new InvalidOperationException("No run.");
    private static string LogPath => Path.Combine(_directory, $"{_role}-{System.Environment.ProcessId}.log");

    public static void Initialize()
    {
        _role = CommandLineHelper.GetValue("minimal-harness") ?? "";
        if (_role.Length == 0) return;
        _directory = CommandLineHelper.GetValue("minimal-dir") ?? throw new InvalidOperationException("Missing test dir.");
        Directory.CreateDirectory(_directory);
        _active = true;
        _peers = int.Parse(CommandLineHelper.GetValue("minimal-peers") ?? "2");
        _suite = CommandLineHelper.GetValue("minimal-suite") ?? "basic";
        new Harmony("com.beomsu.minimalharness").PatchAll(typeof(Harness).Assembly);
        Manager.RunStarted += runState =>
        {
            _generation++;
            Manager.ChecksumTracker.StateDiverged += (_, _) => _divergences++;
            if (AutoChoices && CardSelectCmd.LocalSelector is null)
                _ = CardSelectCmd.UseSelector(new NativeSelector(), localOnly: true);
        };
        NGame.Instance!.GetTree().ProcessFrame += Pump;
        _ = RunAsync();
    }

    private static void Log(string text)
    {
        string line = $"[MinimalHarness pid={System.Environment.ProcessId} role={_role} stage={_stage}] {text}";
        GD.Print(line); File.AppendAllText(LogPath, line + "\n");
    }
    private static void Pump() => _pump?.Update();
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        Log("CHECK " + reason);
    }
    private static async Task Wait(Func<bool> predicate, int seconds = 45)
    {
        ulong end = Time.GetTicksMsec() + (ulong)seconds * 1000;
        while (!predicate())
        {
            if (File.Exists(Path.Combine(_directory, "abort"))) throw new OperationCanceledException("Parent classified and aborted run.");
            if (Time.GetTicksMsec() > end)
                throw new TimeoutException($"{_stage}: busy={Network.Busy} status={Network.Status} count={History.Count} "
                    + $"run={Manager.IsInProgress} combat={CombatManager.Instance.IsInProgress} "
                    + $"sync={(Manager.IsInProgress ? Manager.ActionQueueSynchronizer.CombatState : null)} "
                    + $"executor={(Manager.IsInProgress ? Manager.ActionExecutor.IsRunning : null)}");
            await NGame.Instance!.AwaitProcessFrame(Lifetime.Token);
        }
    }
    private static async Task Idle()
    {
        await Wait(() => !Network.Busy && Hooks.IsIdle());
        for (int i = 0; i < 5; i++) await NGame.Instance!.AwaitProcessFrame(Lifetime.Token);
        Require(Hooks.IsIdle(), "idle native player phase");
    }
    private static void Mark(string name) => File.WriteAllText(Path.Combine(_directory, name + "." + _role), History.Count.ToString());
    private static bool Marked(string name, string role) => File.Exists(Path.Combine(_directory, name + "." + role));
    private static async Task Barrier(string name)
    {
        Mark(name);
        await Wait(() => Roles.All(role => Marked(name, role)));
    }
    private static int _snapshot;
    private static string Snapshot()
    {
        var state = NetFullCombatState.FromRun(State, null);
        File.WriteAllText(Path.Combine(_directory, $"snapshot-{_role}-{_snapshot++}.txt"), state.ToString());
        var writer = new PacketWriter(); writer.Write(state);
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static async Task RunAsync()
    {
        int exit = 1;
        try
        {
            Log("HARNESS_INIT");
            await NGame.Instance!.GameStartupComplete;
            await Wait(() => NGame.Instance.MainMenu is not null, 60);
            await NGame.Instance.Transition.FadeOut(0.05f);
            if (_suite == "preview")
            {
                var window = NGame.Instance.GetWindow();
                window.Title = $"UndoSync — {_role} — PID {System.Environment.ProcessId}";
                int index = Array.IndexOf(Roles, _role);
                window.Size = new Vector2I(1000, 700);
                window.Position = new Vector2I(20 + index * 1020, 80);
            }
            await BootAsync();
            _stage = "combat-entry";
            await Idle();
            Require(Manager.ChecksumTracker.IsEnabled, "native checksum enabled");
            Require(NCombatRoom.Instance is not null, "real native combat UI exists");
            Log($"COMBAT_READY players={State.Players.Count} encounter={CombatManager.Instance.DebugOnlyGetState()!.Encounter?.Id}");
            if (_suite == "preview")
            {
                Log($"PREVIEW_READY players={State.Players.Count} automaticPlay=false automaticVote=false");
                await Task.Delay(Timeout.Infinite, Lifetime.Token);
            }
            else if (_suite == "choice-ui") await ChoiceUiAsync();
            else if (_role == "ui") await UiAsync();
            else if (_suite == "regression") await RegressionAsync();
            else if (_suite == "extended") await ExtendedAsync();
            else if (_suite is "vote-loss" or "replay-loss") await LossAsync();
            else await MultiplayerAsync();
            Require(_divergences == 0, "zero native state divergences");
            Log($"HARNESS_PASS peers={_peers} suite={_suite} cards={_cards} undos={_undos} selections={_selections} divergences={_divergences}");
            exit = 0;
        }
        catch (Exception exception) { Log($"HARNESS_FAIL {exception}"); }
        finally
        {
            File.WriteAllText(Path.Combine(_directory, $"verdict.{_role}"), exit == 0 ? "PASS" : "FAIL");
            _pump = null;
            // Every peer reaches the final barrier before cleaning its native run.
            if (Manager.IsInProgress) Manager.CleanUp(graceful: true);
            for (int i = 0; i < 4; i++) await NGame.Instance!.AwaitProcessFrame();
            NGame.Instance!.GetTree().Quit(exit);
        }
    }

    private static async Task BootAsync()
    {
        _stage = "boot";
        var listener = new Listener();
        StartRunLobby? lobby = null;
        RunState state;
        if (_role == "ui")
        {
            state = RunState.CreateForNewRun(new[] { Player.CreateForNewRun(Character, UnlockState.all, 1UL) },
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), Array.Empty<ModifierModel>(), GameMode.Standard, 0, (CommandLineHelper.GetValue("minimal-seed") ?? "MINIMAL_UI_A"));
            Manager.SetUpNewSingleplayer(state, shouldSave: false);
        }
        else
        {
            if (_role == "host")
            {
                var service = new NetHostGameService(PeerVersionInfo.LocalDefault());
                Require(!service.StartENetHost(33771, 4).HasValue, "real ENet host started");
                _pump = service;
                lobby = new StartRunLobby(GameMode.Standard, service, listener, _peers);
                lobby.AddLocalHostPlayer(UnlockState.all, 0);
                lobby.SetSeed(CommandLineHelper.GetValue("minimal-seed") ?? "MINIMAL_MP_A");
                Log("HOST_LISTENING port=33771");
            }
            else
            {
                var service = new NetClientGameService(PeerVersionInfo.LocalDefault());
                var flow = new JoinFlow(service);
                var result = await flow.Begin(new ENetClientConnectionInitializer(ulong.Parse(CommandLineHelper.GetValue("clientId") ?? "1001"), "127.0.0.1", 33771), NGame.Instance!.GetTree());
                Require(result.sessionState == RunSessionState.InLobby, "real client joined native lobby");
                lobby = new StartRunLobby(GameMode.Standard, service, listener, -1);
                lobby.InitializeFromMessage(result.joinResponse!.Value);
                _pump = service;
            }
            lobby.SetLocalCharacter(Character);
            await Wait(() => lobby.Players.Count == _peers);
            lobby.SetReady(true);
            await Wait(() => listener.Seed is not null);
            state = RunState.CreateForNewRun(lobby.Players.Select(p => Player.CreateForNewRun(p.character,
                    UnlockState.FromSerializable(p.unlockState), p.id)).ToList(),
                listener.Acts!.Select(a => a.ToMutable()).ToList(), Array.Empty<ModifierModel>(), GameMode.Standard, 0, listener.Seed!);
            Manager.SetUpNewMultiplayer(state, lobby, shouldSave: false);
        }
        if (lobby is not null)
        {
            NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, lobby.Players.Select(p => p.id));
            NGame.Instance.ReactionContainer.InitializeNetworking(lobby.NetService);
        }
        await PreloadManager.LoadRunAssets(state.Players.Select(p => p.Character));
        await Manager.FinalizeStartingRelics();
        if (AutoChoices)
        {
            foreach (var player in state.Players)
            {
                var relic = RelicFactory.PullNextRelicFromBack(player, RelicRarity.Shop, r => r is Toolbox);
                Require(relic is Toolbox, "native shop factory produced Toolbox fixture");
                await RelicCmd.Obtain(relic.ToMutable(), player);
            }
        }
        Manager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(state));
        lobby?.CleanUp(disconnectSession: false);
        _pump = null; // Native NRun now owns Update; replacement lobbies pump themselves.
        await Manager.SetActInternal(0);
        // Use the real act's generated encounter sequence and a real Monster map point.
        // No catalogue encounter/loadout injection; native CreateRoom pulls the encounter.
        var point = state.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster).OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await Manager.EnterMapCoordDebug(point.coord, RoomType.Monster, MapPointType.Monster, showTransition: false);
        await NGame.Instance.Transition.FadeIn(0.05f);
    }

    private static async Task PlayOne(bool attack = false)
    {
        await Idle();
        var player = LocalContext.GetMe(State)!;
        var card = PileType.Hand.GetPile(player).Cards
            .Where(c => (!attack || c.TargetType == TargetType.AnyEnemy) && c.CanPlay() && !c.Owner.Creature.IsDead && (!AutoChoices ||
                c.Id.Entry.Contains("DEFEND") || c.Id.Entry.Contains("STRIKE") || c.Id.Entry == "SURVIVOR" || c.Id.Entry == "NEUTRALIZE"))
            .OrderBy(c => AutoChoices && c.Id.Entry == "SURVIVOR" ? -1 : c.Id.Entry.Contains("DEFEND") ? 0 : 1).FirstOrDefault()
            ?? throw new InvalidOperationException("No playable native hand card.");
        var target = card.TargetType == TargetType.AnyEnemy
            ? CombatManager.Instance.DebugOnlyGetState()!.Enemies.First(c => c.IsAlive)
            : null;
        int before = History.Count;
        Log($"PLAY card={card.Id} owner={player.NetId} events={before}");
        bool finished = false;
        var executor = Manager.ActionExecutor;
        void OnFinished(GameAction action)
        {
            if (action is PlayCardAction play && play.OwnerId == player.NetId && play.CardModelId == card.Id) finished = true;
        }
        executor.AfterActionExecuted += OnFinished;
        try
        {
            Require(card.TryManualPlay(target), "native manual card accepted");
            await Wait(() => finished && History.Count > before);
        }
        finally { executor.AfterActionExecuted -= OnFinished; }
        await Idle();
        _cards++;
        Require(Snapshot().Length > 0, "post-action native state available");
    }

    private static async Task UiAsync()
    {
        _stage = "ui-picker";
        string opening = Snapshot();
        await PlayOne();
        Require(History.Targets.Count > 0, "picker has earlier checkpoint");
        Log("UI_READY press Left and click earliest checkpoint using native input");
        await Wait(() => Network.Status == "Undo complete" && !Network.Busy, 150);
        await Idle();
        Require(Snapshot() == opening, "UI-triggered undo restores opening native state");
        _undos++;
        Log("UI_RESTORED native view ready for visual inspection");
        await Wait(() => File.Exists(Path.Combine(_directory, "ui-verified")), 120);
    }

    private static async Task MultiplayerAsync()
    {
        _stage = "initial-actions";
        await Barrier("opening");
        if (_role == "host") { await PlayOne(); Mark("host-play"); }
        await Wait(() => Marked("host-play", "host"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "host-play.host"))));
        await Idle();
        string firstTarget = Snapshot();
        int firstCount = History.Count;
        await Barrier("first-target");
        if (_role == "client") { await PlayOne(); Mark("client-play"); }
        await Wait(() => Marked("client-play", "client"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "client-play.client"))));
        await Idle();
        await Barrier("reject-ready");
        string beforeReject = Snapshot();
        _stage = "reject";
        if (_role == "host") Network.Request(firstCount);
        await Wait(() => Network.Busy);
        if (_role == "client") Network.Vote(false);
        await Wait(() => !Network.Busy);
        Require(Snapshot() == beforeReject, "rejected proposal leaves native state unchanged");
        await Barrier("rejected");

        _stage = "host-undo";
        if (_role == "host") Network.Request(firstCount);
        await Wait(() => Network.Busy);
        if (_role == "client") Network.Vote(true);
        await Wait(() => !Network.Busy, 100);
        Require(Network.Status == "Undo complete", "first replay completed");
        await Idle();
        Require(History.Count == firstCount && Snapshot() == firstTarget, "first replay matches saved native state and truncates history");
        _undos++;
        await Barrier("first-undo");

        _stage = "second-actions";
        if (_role == "host") { await PlayOne(); Mark("host-second"); }
        await Wait(() => Marked("host-second", "host"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "host-second.host"))));
        await Idle();
        string secondTarget = Snapshot();
        int secondCount = History.Count;
        await Barrier("second-target");
        if (_role == "client") { await PlayOne(); Mark("client-second"); }
        await Wait(() => Marked("client-second", "client"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "client-second.client"))));
        await Idle();
        await Barrier("client-request-ready");
        _stage = "client-undo";
        if (_role == "client") Network.Request(secondCount);
        await Wait(() => Network.Busy);
        Network.Vote(true);
        await Wait(() => !Network.Busy, 100);
        Require(Network.Status == "Undo complete", "client-requested replay completed");
        await Idle();
        Require(History.Count == secondCount && Snapshot() == secondTarget, "second replay matches saved native state and truncates history");
        _undos++;
        await Barrier("second-undo");

        _stage = "continued-input";
        if (_role == "host") { await PlayOne(); Mark("host-final"); }
        await Wait(() => Marked("host-final", "host"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "host-final.host"))));
        await Idle();
        if (_role == "client") { await PlayOne(); Mark("client-final"); }
        await Wait(() => Marked("client-final", "client"));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, "client-final.client"))));
        await Idle();
        Require(_cards >= 3, "local input executed before and after both replays");
        File.WriteAllText(Path.Combine(_directory, "final-state." + _role), Snapshot());
        await Barrier("complete");
        Require(File.ReadAllText(Path.Combine(_directory, "final-state.host")) == File.ReadAllText(Path.Combine(_directory, "final-state.client")), "real peers final native state matches");
        await Barrier("verdict-ready");
    }

    private static async Task StateBarrier(string label)
    {
        File.WriteAllText(Path.Combine(_directory, label + ".state." + _role), Snapshot());
        await Barrier(label);
        Require(Roles.Select(role => File.ReadAllText(Path.Combine(_directory, label + ".state." + role))).Distinct().Count() == 1,
            label + " all real peer native states match");
    }

    private static async Task Actor(string label, string role, bool attack = false)
    {
        if (_role == role) { await PlayOne(attack); Mark(label); }
        await Wait(() => Marked(label, role));
        await Wait(() => History.Count >= int.Parse(File.ReadAllText(Path.Combine(_directory, label + "." + role))));
        await Idle();
        await Barrier(label + "-settled");
    }

    private static async Task EndTurnAll(string label)
    {
        _stage = label;
        await Idle();
        int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
        await Barrier(label + "-start");
        var me = LocalContext.GetMe(State)!;
        Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
        await Wait(() => CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round && Hooks.IsIdle(), 80);
        await Idle();
        Log($"TURN_ADVANCED from={round} to={CombatManager.Instance.DebugOnlyGetState()!.RoundNumber}");
        await StateBarrier(label + "-end");
    }

    private static async Task UndoTo(int target, string snapshot, string requester, string label)
    {
        _stage = label;
        await Barrier(label + "-ready");
        int selections = _selections;
        if (_role == requester) Network.Request(target);
        await Wait(() => Network.Busy);
        Network.Vote(true);
        await Wait(() => !Network.Busy, 120);
        Require(Network.Status == "Undo complete", label + " terminal resume: " + Network.Status);
        await Idle();
        Require(History.Count == target && Snapshot() == snapshot, label + " exact target state and history truncation");
        Require(_selections == selections, label + " replay did not invoke the live choice selector");
        _undos++;
        await StateBarrier(label + "-resumed");
    }

    private static async Task CheckConnectedUi(string label)
    {
        await Barrier(label + "-input-ready");
        Manager.InputSynchronizer.SyncLocalMousePos(new Vector2(240, 260), NRun.Instance!);
        var remote = State.Players.Where(p => p.NetId != Manager.NetService.NetId).Select(p => p.NetId).ToHashSet();
        await Wait(() => remote.IsSubsetOf(NGame.Instance!.RemoteCursorContainer.GetChildren()
            .OfType<NRemoteMouseCursor>().Select(c => c.PlayerId)), 10);
        Require(State.Players.Select(p => p.NetId).ToHashSet().SetEquals(Manager.RunLobby!.PlayerIds), label + " native roster connected");
        foreach (var node in NRun.Instance!.GlobalUi.MultiplayerPlayerContainer.GetChildren().OfType<NMultiplayerPlayerState>())
            Require(!node.GetNode<TextureRect>("%DisconnectedIndicator").Visible, label + $" avatar {node.Player.NetId} online");
        foreach (ulong peer in remote)
            Require(NGame.Instance!.RemoteCursorContainer.GetCursorPosition(peer).IsFinite(), label + $" cursor {peer} bound");
        await Barrier(label + "-ui-checked");
    }

    private static int EnemyHp() => CombatManager.Instance.DebugOnlyGetState()!.Enemies.Sum(e => e.CurrentHp);

    private static async Task RegressionAsync()
    {
        _stage = "manual-report-regression";
        await StateBarrier("regression-opening");
        await CheckConnectedUi("opening");
        foreach (string requester in Roles)
        {
            _stage = "regression-" + requester;
            string original = Snapshot();
            int target = History.Count, hp = EnemyHp();
            await Actor("attack-" + requester, requester, attack: true);
            Require(EnemyHp() < hp, "real attack reduced enemy HP before undo");
            await StateBarrier("damaged-" + requester);
            if (_role == requester) Network.Request(target);
            await Wait(() => Network.Busy);
            for (int frame = 0; frame < 4; frame++) await NGame.Instance!.AwaitProcessFrame();
            if (_role == requester)
            {
                Require(Network.LocalVoted, "proposer already accepted its own proposal");
                Require(UI.CurrentPopup != null, "proposer native waiting popup exists");
                string header = UI.CurrentPopup!.GetNode<MegaLabel>("VerticalPopup/Header").Text;
                Require(header == "Undo Proposed" || header == "되돌리기 제안 중", "proposer sees waiting rather than accept");
                Log("PROPOSER_UI header=" + header);
            }
            else Require(Network.CanVote, "other participant must explicitly vote");
            await Barrier("proposal-ui-" + requester);
            if (requester == "host" && CommandLineHelper.HasArg("minimal-ui-probe"))
            {
                Log("REGRESSION_UI_READY waiting popup inspection");
                await Wait(() => File.Exists(Path.Combine(_directory, "ui-verified")), 120);
            }
            Network.Vote(true);
            await Wait(() => !Network.Busy, 120);
            Require(Network.Status == "Undo complete", "real multiplayer replay resumed");
            await Idle();
            Require(EnemyHp() == hp && History.Count == target && Snapshot() == original, "enemy HP and full native state restored");
            _undos++;
            await CheckConnectedUi("restored-" + requester);
            await StateBarrier("restored-state-" + requester);
        }
        await Actor("post-replay-real-attack", "client2", attack: true);
        await StateBarrier("post-replay-real-attack");
        await CheckConnectedUi("final");
        await Barrier("regression-terminal");
    }

    private static async Task ExtendedAsync()
    {
        _stage = "opening-hooks";
        await StateBarrier("opening-hooks");
        Require(_selections > 0, "opening native local choice synchronized");
        foreach (string role in Roles) await Actor("serial-" + role, role);
        string targetState = Snapshot();
        int target = History.Count;
        await StateBarrier("target-A");
        _stage = "concurrent-input";
        await Barrier("concurrent-ready");
        await PlayOne();
        await StateBarrier("concurrent-done");
        var prefix = History.Freeze(target);
        var prefixWriter = new PacketWriter(); prefixWriter.Write(prefix);
        File.WriteAllBytes(Path.Combine(_directory, $"prefix.{_role}.bin"), prefixWriter.Buffer[..prefixWriter.BytePosition]);
        File.WriteAllLines(Path.Combine(_directory, $"prefix.{_role}.txt"), prefix.events.Select((e, i) => $"{i} {e.eventType} player={e.playerId} action={e.action?.GetType().Name} hook={e.hookId} resume={e.actionId} choice={e.choiceId}"));
        foreach (var kind in Enum.GetValues<CombatReplayEventType>())
            Log($"EVENT_COVERAGE kind={kind} count={prefix.events.Count(e => e.eventType == kind)}");
        Require(prefix.events.Any(e => e.eventType == CombatReplayEventType.HookAction), "real HookAction recorded");
        Require(prefix.events.Any(e => e.eventType == CombatReplayEventType.PlayerChoice), "real PlayerChoice recorded");
        Require(prefix.events.Any(e => e.eventType == CombatReplayEventType.ResumeAction), "real ResumeAction recorded");

        string current = Snapshot();
        _stage = "one-reject";
        await Barrier("reject-start");
        if (_role == "host") Network.Request(target);
        await Wait(() => Network.Busy);
        Network.Vote(_role != Roles[^1]);
        await Wait(() => !Network.Busy);
        Require(Snapshot() == current, "one rejection preserves native state");
        await Barrier("reject-end");

        _stage = "vote-timeout";
        if (_role == "host") Network.Request(target);
        await Wait(() => Network.Busy);
        if (_role != Roles[^1]) Network.Vote(true);
        await Wait(() => !Network.Busy, 45);
        Require(Snapshot() == current && Network.Status != "Undo complete", "withheld vote times out without state mutation");
        await Barrier("timeout-end");

        for (int i = 0; i < Roles.Length; i++)
        {
            if (i > 0) await Actor("repeat-mutation-" + i, Roles[i]);
            await UndoTo(target, targetState, Roles[i], "requester-" + Roles[i]);
        }
        await EndTurnAll("cross-enemy-turn");
        foreach (string role in Roles) await Actor("turn2-" + role, role);
        string turn2 = Snapshot();
        int turn2Target = History.Count;
        await StateBarrier("target-B");
        await Actor("turn2-mutation", "host");
        await UndoTo(turn2Target, turn2, Roles[^1], "cross-turn-replay");
        foreach (string role in Roles) await Actor("post-replay-" + role, role);
        await StateBarrier("extended-final");
        Require(_undos == _peers + 1, "every peer requested and cross-turn replay completed");
        Require(_cards >= 4, "local actions executed across repeated rebuilds and turn transition");
        await Barrier("extended-verdict");
    }

    private static async Task ChoiceUiAsync()
    {
        _stage = "native-choice-ui";
        CardModel? survivor = null;
        for (int turn = 0; turn < 4; turn++)
        {
            survivor = PileType.Hand.GetPile(LocalContext.GetMe(State)!).Cards.FirstOrDefault(c => c.Id.Entry == "SURVIVOR");
            if (survivor is not null) break;
            int round = CombatManager.Instance.DebugOnlyGetState()!.RoundNumber;
            var me = LocalContext.GetMe(State)!;
            Manager.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
            await Wait(() => CombatManager.Instance.DebugOnlyGetState()!.RoundNumber > round && Hooks.IsIdle());
        }
        Require(survivor is not null, "normal starter deck drew Survivor");
        Require(survivor!.TryManualPlay(null), "Survivor native manual play accepted");
        Log("CHOICE_UI_READY select one discard through native UI");
        await Wait(Hooks.IsIdle, 150);
        await Idle();
        string target = Snapshot();
        int count = History.Count;
        await PlayOne();
        var plan = History.Freeze(count);
        Require(plan.events.Any(e => e.eventType == CombatReplayEventType.PlayerChoice), "native UI choice recorded");
        Network.Request(count);
        await Wait(() => !Network.Busy, 100);
        Require(Network.Status == "Undo complete", "historical native UI choice replay completed");
        await Idle();
        Require(Snapshot() == target, "historical choice exact native state");
        _undos++;
        Log("CHOICE_UI_REPLAYED inspect no selection prompt remains");
        await Wait(() => File.Exists(Path.Combine(_directory, "ui-verified")), 120);
    }

    private static async Task LossAsync()
    {
        _stage = _suite;
        await StateBarrier("loss-opening");
        await Actor("loss-host-card", "host");
        int target = History.Count;
        string before = Snapshot();
        await Actor("loss-client-card", "client");
        await Barrier("loss-ready");
        string dropped = Roles[^1];
        if (_role == "host") Network.Request(target);
        await Wait(() => Network.Busy);
        if (_suite == "vote-loss")
        {
            if (_role == dropped)
            {
                Log("INJECT_REAL_DISCONNECT during vote");
                Manager.NetService.Disconnect(NetError.Quit, now: true);
                await Wait(() => !Manager.IsInProgress, 60);
                Log("EXPECTED_DISCONNECTED participant reached menu");
                return;
            }
            Network.Vote(true);
        }
        else
        {
            int generation = _generation;
            Network.Vote(true);
            if (_role == dropped)
            {
                await Wait(() => _generation > generation && Network.Executing, 100);
                Log("INJECT_REAL_DISCONNECT after replacement RunStarted");
                Manager.NetService.Disconnect(NetError.Quit, now: true);
            }
        }
        await Wait(() => !Network.Busy, 220);
        Require(Network.Status != "Undo complete", "peer loss never commits a partial replay");
        if (_suite == "replay-loss")
            await Wait(() => !Manager.IsInProgress, 60);
        else Require(Manager.IsInProgress && Hooks.IsIdle(), "vote loss leaves unchanged live combat playable");
        Log("EXPECTED_FAILURE_HANDLED " + Network.Status);
        var survivors = _suite == "vote-loss" ? Roles.Where(r => r != dropped).ToArray() : Roles;
        Mark("loss-terminal");
        await Wait(() => survivors.All(r => Marked("loss-terminal", r)), 220);
    }

    private sealed class NativeSelector : ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            var selected = list.Take(Math.Min(maxSelect, Math.Max(1, minSelect))).ToList();
            var player = LocalContext.GetMe(State)!;
            int slot = State.GetPlayerSlotIndex(player);
            uint choice = Manager.PlayerChoiceSynchronizer.ChoiceIds[slot] - 1;
            await NGame.Instance!.AwaitProcessFrame();
            var result = list.All(c => c.Pile?.Type == PileType.Hand)
                ? PlayerChoiceResult.FromMutableCombatCards(selected)
                : PlayerChoiceResult.FromIndex(selected.Count == 0 ? -1 : list.IndexOf(selected[0]));
            Manager.PlayerChoiceSynchronizer.SyncLocalChoice(player, choice, result);
            _selections++;
            Log($"NATIVE_SELECTION player={player.NetId} choice={choice} count={selected.Count}");
            return selected;
        }
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("Reward selection is outside this harness.");
    }


    private sealed class Listener : IStartRunLobbyListener
    {
        internal string? Seed;
        internal List<ActModel>? Acts;
        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers) { Seed = seed; Acts = acts; }
        public void PlayerConnected(StartRunLobbyPlayer player) { }
        public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
        public void RemotePlayerDisconnected(StartRunLobbyPlayer player) { }
        public void LocalPlayerDisconnected(NetErrorInfo info) { Log("BOOT_DISCONNECT " + info); }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SeenFtue))]
    private static class FtueFixture
    {
        private static bool Prefix(ref bool __result)
        {
            if (!_active) return true;
            __result = true; return false;
        }
    }
}
