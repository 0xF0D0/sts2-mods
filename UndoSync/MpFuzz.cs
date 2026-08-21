using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// STEP 1 ONLY of a multiplayer fuzz harness: get two real game instances into the same combat
/// together, with no UI clicking, and log enough to prove it worked. Does NOT drive cards, does
/// NOT restore/undo anything — those are later steps and are deliberately absent from this file.
/// It DOES cast exactly one map-coord vote per instance, purely as the minimum needed to move the
/// party off the map screen and into a combat room; see <see cref="VoteForNextMapCoordAsync"/>'s
/// doc comment for why that vote must go through the game's own replicated action-queue path
/// rather than a shortcut.
///
/// COMPLETELY DORMANT IN NORMAL PLAY, same contract as UndoFuzz.cs: <see cref="MaybeStart"/> is
/// the only entry point, called once from UndoSyncMod.Initialize(), and its very first line is a
/// CommandLineHelper.HasArg check for "undosync-mpfuzz" — absent that flag, nothing else in this
/// file ever executes or subscribes to anything.
///
/// Usage (opt-in only, two processes, run one with each set of flags):
///   --undosync-mpfuzz --undosync-mpfuzz-role=host
///   --undosync-mpfuzz --undosync-mpfuzz-role=client [--undosync-mpfuzz-clientid=N] [--undosync-mpfuzz-noquit]
///   --undosync-mpfuzz-role     Required. "host" or "client" — anything else (or absent) bails out
///                               with a clear log line instead of guessing.
///   --undosync-mpfuzz-clientid Optional, client only. The ulong net id the client claims when
///                               connecting (JoinButtonPressed's own ulong.Parse(_idField.Text) —
///                               NMultiplayerTest.cs:313). Default 1001.
///   --undosync-mpfuzz-noquit   Optional. Stay open at the end instead of quitting — mirrors
///                               UndoFuzz's --undosync-fuzz-noquit / --undosync-uitest-noquit.
///
/// WHY THIS ROUTE (see the design note this file was speced from): --fastmp already automates the
/// network side (NMultiplayerSubmenu.StartHostAsync -> NetHostGameService.StartENetHost;
/// NJoinFriendScreen.FastMpJoin -> ENetClientConnectionInitializer), but still leaves character
/// select and run start as UI. The game ships its own debug multiplayer harness that does the
/// whole thing headlessly-from-code, including SetUpNewMultiplayer, and its scene is in the
/// shipped pck: res://scenes/debug/multiplayer_test.tscn, rooted at
/// MegaCrit.Sts2.Core.Nodes.Debug.Multiplayer.NMultiplayerTest : Control, IStartRunLobbyListener
/// (NMultiplayerTest.cs:42). This file drives that scene's own (private) button handlers via
/// AccessTools/Harmony reflection instead of reimplementing what they do, because
/// ReadyButtonPressed in particular (NMultiplayerTest.cs:319-332) does non-trivial
/// _localPlayerData setup (deck/relics/potions/rng/odds/grab-bag/extra-fields/unlock-state) that
/// must not be skipped or re-derived by hand.
///
/// SEQUENCE (mirrors the design note's numbered steps exactly):
///   1. Await NGame.Instance.GameStartupComplete (NGame.cs:481) — same wait point
///      NSceneBootstrapper.StartNewRun itself awaits before starting a debug run, and the same
///      one UndoFuzz.RunWhenReadyAsync awaits.
///   2. Instantiate the scene via SceneHelper.Instantiate&lt;NMultiplayerTest&gt;("debug/multiplayer_test")
///      (SceneHelper.cs:12,39 — GetScenePath turns "debug/multiplayer_test" into
///      "res://scenes/debug/multiplayer_test.tscn"), install it via
///      NGame.Instance.RootSceneContainer.SetCurrentScene (NGame.cs:440, NSceneContainer.cs:73),
///      then poll (real time, not a single frame — see WaitForSceneReadyAsync) until its private
///      fields are bound by its own _Ready() (NMultiplayerTest.cs:235-272) before touching them.
///   3. Host: invoke HostButtonPressed() (:306). Client: set _ipField.Text="127.0.0.1" and
///      _idField.Text=&lt;clientid&gt; (both TextEdit, :207/:209) BEFORE invoking JoinButtonPressed()
///      (:311-317), which reads them via ulong.Parse/plain string, never a reflection round-trip.
///   4. Both: poll until _lobby (:223, type StartRunLobby?) is non-null and its Players
///      (StartRunLobby.cs:109, public List&lt;StartRunLobbyPlayer&gt;) has reached 2 (host + this
///      client) — logging the count every time it changes so the handshake is visible in the log
///      — then invoke ReadyButtonPressed() (:319-332). Once every player is ready, StartRunLobby
///      calls back BeginRun (IStartRunLobbyListener, NMultiplayerTest.cs:333) on its own, no
///      further action needed from here; see StartRunLobby.SetReady (:701) / IsAboutToBeginGame
///      (:739) / BeginRunForAllPlayersIfAllReady for that internal chain, and BeginRunAsync (:354)
///      for how it reaches RunManager.Instance.SetUpNewMultiplayer.
///   5. Wait for RunManager.Instance.IsInProgress (RunManager.cs:104), same GENEROUS real-time
///      timeout reasoning as the rest of this file — see the timeout constants below for why
///      UndoFuzz's headless 10s/45s bounds (HeadlessIdleWaitTimeout/HeadlessCombatWallClockTimeout)
///      would be badly wrong here.
///   6. Nothing so far has moved the party onto a map node — RunManager.Instance.IsInProgress just
///      means the run object exists, not that anyone chose where to go. Resolve the local Player
///      (LocalContext.GetMe) and the run's current MapLocation, pick a Monster child of the current
///      MapPoint (falling back to any child, logged, if there is no Monster child), and vote for it
///      via VoteForNextMapCoordAsync. Both roles run identical vote logic; the host/client
///      asymmetry (only the host actually enqueues MoveToMapCoordAction once every slot has voted)
///      lives entirely inside MapSelectionSynchronizer/ActionQueueSynchronizer, not here.
///   7. Wait for CombatManager.Instance.IsInProgress (CombatManager.cs:167), same generous
///      real-time timeout reasoning as step 5.
///   8. On success, log one summary line: role, our own net id (RunManager.Instance.NetService
///      .NetId, INetGameService.cs:20 — already used the same way by ChecksumHook.EnsureSubscribed),
///      the number of players CombatState reports (UndoSyncMod.GetCombatState(), already used by
///      UndoFuzz.cs), each player's character id and net id (Player.Character / Player.NetId,
///      Player.cs:44/:48, plus the pre-existing model.Id.Entry idiom used throughout this mod), and
///      whether RunManager.Instance.ChecksumTracker.IsEnabled (ChecksumTracker.cs:59) is true.
///      On any timeout, log exactly which step it was and the observed state at that moment — a
///      stall must never come out of this file as an unexplained hang.
///   9. Quit unless --undosync-mpfuzz-noquit, matching UndoFuzz's existing harness flag. Done from
///      a try/finally (unlike UndoFuzz's sequential end-of-loop quit) because this is a single
///      one-shot attempt, not a combat loop with its own per-iteration failure bucket — any step
///      here can bail out early via `return`, and the process must still not be left stranded at
///      whatever screen it stopped on.
///
/// Logs to the existing UndoSync-&lt;pid&gt;.log (Log.cs) with a "[MpFuzz]" prefix, same reasoning
/// as UndoFuzz's own "[Fuzz]" prefix: this never runs alongside anything else that cares about the
/// log, so there is nothing to disentangle by splitting files.
/// </summary>
internal static class MpFuzz
{
    private const string MpFuzzArg = "undosync-mpfuzz";
    private const string RoleArg = "undosync-mpfuzz-role";
    private const string ClientIdArg = "undosync-mpfuzz-clientid";
    private const string NoQuitArg = "undosync-mpfuzz-noquit";

    /// <summary>Default for --undosync-mpfuzz-clientid, per the design note.</summary>
    private const ulong DefaultClientId = 1001;

    /// <summary>This is always a two-instance test: one host, one client.</summary>
    private const int ExpectedPlayerCount = 2;

    // --- Timeouts -------------------------------------------------------------------------------
    // UndoFuzz's HeadlessIdleWaitTimeout (10s) / HeadlessCombatWallClockTimeout (45s) are tuned for
    // a *headless* TestMode.IsOn combat driven action-by-action from this same process — no network
    // round-trip, no asset loading, no real animation. Every wait in THIS file instead spans two
    // separate OS processes talking over real (loopback) ENet, each doing real asset loads, real
    // scene transitions, and real map/act setup. Reusing UndoFuzz's bounds here would abort a
    // perfectly healthy run for the same reason UiTestIdleWaitTimeout/UiTestCombatWallClockTimeout
    // exist for UndoFuzz's own UI-mode path (see UndoFuzz.cs's top-of-file doc comment) — so these
    // are minutes, not seconds, same rationale, independently re-derived for a two-process run
    // rather than a single-process real-time one.
    private static readonly TimeSpan SceneReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LobbyConnectTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CombatStartTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bound for confirming our own map-coord vote actually round-tripped through
    /// ActionQueueSynchronizer.RequestEnqueue and came back out the other side as
    /// VoteForMapCoordAction.ExecuteAction -&gt; MapSelectionSynchronizer.PlayerVotedForMapCoord,
    /// observed via GetVote(me) — see VoteForNextMapCoordAsync's doc comment for the full path and
    /// why this can only be confirmed this way, not by inference. This genuinely crosses the network
    /// (client role) or at least the action queue (host role), so — unlike a same-thread call, which
    /// would make "waiting to confirm" a tautology true on the very next line — it needs a real
    /// bounded wait. Kept far shorter than LobbyConnectTimeout/RunStartTimeout because voting is one
    /// lightweight action on an already-connected, already-running session, not a fresh connection or
    /// an asset load.
    /// </summary>
    private static readonly TimeSpan MapVoteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Real-time poll cadence for every wait in this file. Coarser than UndoFuzz's own
    /// 10ms IdlePollInterval on purpose: every wait here is bounded in minutes and spans a network
    /// round-trip or asset load, so polling every 10ms would only add log-free CPU churn, not
    /// responsiveness.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // --- Reflection handles onto NMultiplayerTest's PRIVATE members -----------------------------
    // Every one of these is in the design note's own "verified members" list; SurfaceCheck's
    // Check 1 re-verifies each string against the shipped assembly at build time.
    private static readonly FieldInfo? FIdField = AccessTools.Field(typeof(NMultiplayerTest), "_idField");
    private static readonly FieldInfo? FIpField = AccessTools.Field(typeof(NMultiplayerTest), "_ipField");
    private static readonly FieldInfo? FLobby = AccessTools.Field(typeof(NMultiplayerTest), "_lobby");
    private static readonly MethodInfo? MHostButtonPressed = AccessTools.Method(typeof(NMultiplayerTest), "HostButtonPressed");
    private static readonly MethodInfo? MJoinButtonPressed = AccessTools.Method(typeof(NMultiplayerTest), "JoinButtonPressed");
    private static readonly MethodInfo? MReadyButtonPressed = AccessTools.Method(typeof(NMultiplayerTest), "ReadyButtonPressed");

    // ==================================================================================
    // Entry point
    // ==================================================================================

    /// <summary>
    /// Called once from UndoSyncMod.Initialize(). The HasArg check below is the ONLY gate on this
    /// entire file running: everything else is unreachable unless --undosync-mpfuzz was passed.
    /// </summary>
    internal static void MaybeStart()
    {
        try
        {
            if (!CommandLineHelper.HasArg(MpFuzzArg)) return;

            if (!CommandLineHelper.TryGetValue(RoleArg, out var roleArg) || string.IsNullOrEmpty(roleArg))
            {
                Log.Write($"[MpFuzz] --{MpFuzzArg} was passed but --{RoleArg}=host|client is missing — cannot proceed, bailing out.");
                return;
            }

            string role = roleArg.Trim().ToLowerInvariant();
            bool isHost;
            switch (role)
            {
                case "host":
                    isHost = true;
                    break;
                case "client":
                    isHost = false;
                    break;
                default:
                    Log.Write($"[MpFuzz] --{RoleArg}=\"{roleArg}\" not recognised — expected \"host\" or \"client\", bailing out.");
                    return;
            }

            ulong clientId = DefaultClientId;
            if (!isHost && CommandLineHelper.TryGetValue(ClientIdArg, out var clientIdArg)
                && !string.IsNullOrEmpty(clientIdArg) && ulong.TryParse(clientIdArg, out var parsedClientId))
                clientId = parsedClientId;

            Log.Write($"[MpFuzz] --{MpFuzzArg} detected — role={role}"
                + (isHost ? "" : $" clientId={clientId}")
                + " — will run once game startup completes.");
            _ = RunWhenReadyAsync(isHost, clientId);
        }
        catch (Exception ex)
        {
            // Never throw out of a mod initializer — that would take the whole mod load down with it.
            Log.Write($"[MpFuzz] MaybeStart ERROR: {ex}");
        }
    }

    /// <summary>
    /// Waits for the game to finish booting before doing anything — same wait point and same
    /// not-actually-circular reasoning as UndoFuzz.RunWhenReadyAsync (NGame.cs:481,
    /// NSceneBootstrapper.cs:85): UndoSyncMod.Initialize() runs from OneTimeInitialization
    /// .ExecuteVeryEarly, awaited at the very first line of NGame.GameStartup, so GameStartup is
    /// still running (and GameStartupComplete not yet signalled) at the moment this fire-and-forget
    /// task starts; awaiting it here only parks this task, it does not block GameStartup itself.
    /// </summary>
    private static async Task RunWhenReadyAsync(bool isHost, ulong clientId)
    {
        try
        {
            var game = NGame.Instance;
            if (game == null)
            {
                Log.Write("[MpFuzz] NGame.Instance was null at mod-init time — cannot wait for startup, aborting.");
                return;
            }
            await game.GameStartupComplete;
            await RunAsync(isHost, clientId);
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] RunWhenReadyAsync ERROR: {ex}");
        }
    }

    // ==================================================================================
    // Main sequence
    // ==================================================================================

    private static async Task RunAsync(bool isHost, ulong clientId)
    {
        string role = isHost ? "host" : "client";
        Log.Write($"[MpFuzz] ==================== starting: role={role} ====================");

        try
        {
            // Fail fast and specifically if a game update renamed/removed anything this file
            // reflects onto, rather than an opaque NullReferenceException three steps later.
            if (FIdField == null || FIpField == null || FLobby == null
                || MHostButtonPressed == null || MJoinButtonPressed == null || MReadyButtonPressed == null)
            {
                Log.Write("[MpFuzz] FAIL: one or more reflection handles onto NMultiplayerTest failed to resolve "
                    + $"(idField={FIdField != null} ipField={FIpField != null} lobby={FLobby != null} "
                    + $"hostBtn={MHostButtonPressed != null} joinBtn={MJoinButtonPressed != null} readyBtn={MReadyButtonPressed != null}) "
                    + "— the game likely changed NMultiplayerTest's private surface.");
                return;
            }

            // Step 2: instantiate + install the scene, then wait for its own _Ready() to bind its
            // private fields (NMultiplayerTest.cs:235-272) before this file touches any of them.
            var game = NGame.Instance;
            if (game == null)
            {
                Log.Write("[MpFuzz] FAIL: NGame.Instance became null before scene install — aborting.");
                return;
            }

            var scene = SceneHelper.Instantiate<NMultiplayerTest>("debug/multiplayer_test");
            game.RootSceneContainer.SetCurrentScene(scene);
            Log.Write("[MpFuzz] instantiated res://scenes/debug/multiplayer_test.tscn and installed it via RootSceneContainer.SetCurrentScene.");

            if (!await WaitForSceneReadyAsync(scene))
            {
                Log.Write($"[MpFuzz] FAIL: scene's private fields were still unbound {SceneReadyTimeout.TotalSeconds}s after "
                    + "SetCurrentScene — NMultiplayerTest._Ready() apparently never ran.");
                return;
            }
            Log.Write("[MpFuzz] scene ready — private fields bound.");

            // Step 3: host or join.
            if (isHost)
            {
                MHostButtonPressed.Invoke(scene, null);
                Log.Write("[MpFuzz] invoked HostButtonPressed().");
            }
            else
            {
                var ipField = (TextEdit)FIpField.GetValue(scene)!;
                var idField = (TextEdit)FIdField.GetValue(scene)!;
                ipField.Text = "127.0.0.1";
                idField.Text = clientId.ToString();
                MJoinButtonPressed.Invoke(scene, null);
                Log.Write($"[MpFuzz] set _ipField.Text=127.0.0.1 _idField.Text={clientId}, invoked JoinButtonPressed().");
            }

            // Step 4: wait for the lobby to exist and the peer to actually be in it, then ready up.
            var lobby = await WaitForLobbyConnectedAsync(scene);
            if (lobby == null)
            {
                var lobbyNow = (StartRunLobby?)FLobby.GetValue(scene);
                Log.Write($"[MpFuzz] FAIL: lobby never reached {ExpectedPlayerCount} connected players within "
                    + $"{LobbyConnectTimeout.TotalMinutes}m (lobby null={lobbyNow == null}"
                    + (lobbyNow == null ? "" : $" players={lobbyNow.Players.Count}") + ").");
                return;
            }
            MReadyButtonPressed.Invoke(scene, null);
            Log.Write($"[MpFuzz] invoked ReadyButtonPressed() with {lobby.Players.Count} players in the lobby.");

            // Step 5: wait for the run to actually start. This needs no further action from this
            // file — StartRunLobby.BeginRunForAllPlayersIfAllReady fires the callback chain on its
            // own once both sides have called ReadyButtonPressed.
            // IsInProgress alone is NOT enough to proceed, and waiting on it alone is what made the
            // first live two-instance run fail. RunManager.IsInProgress is `State != null`
            // (RunManager.cs:104), which SetUpNewMultiplayer sets as its first statement — but
            // LocalContext.NetId, which LocalContext.GetMe reads, is only set later in the same
            // StartNewMultiplayerRun call, by RunManager.Launch() (RunManager.cs:713). Polling only
            // IsInProgress therefore wins the race and lands us in the map-vote step with
            // GetMe(runState) still null, which is exactly what both instances logged:
            //   "FAIL: map vote step — LocalContext.GetMe(runState) returned null".
            // Wait for the thing actually needed downstream instead of a proxy that happens to flip
            // earlier.
            if (!await WaitForConditionAsync(
                    () => RunManager.Instance is { IsInProgress: true } rm
                          && rm.DebugOnlyGetState() is { } rs
                          && LocalContext.GetMe(rs) != null
                          // The act map is not installed at the moment State becomes non-null:
                          // RunState.Map defaults to NullActMap.Instance (RunState.cs:102), whose
                          // StartingMapPoint is a bare `new MapPoint(0, 0)` with no children
                          // (NullActMap.cs:12). The second live run voted against exactly that and
                          // logged "current point Point[0,0] has no children at all to vote for".
                          // The real map arrives later, when StartNewMultiplayerRun reaches
                          // SetActInternal(0), so wait for a map that actually has somewhere to go.
                          && rs.Map is not NullActMap
                          && rs.Map.GetAllMapPoints().Any(),
                    RunStartTimeout))
            {
                var rmNow = RunManager.Instance;
                Log.Write($"[MpFuzz] FAIL: the run never reached a usable state within "
                    + $"{RunStartTimeout.TotalMinutes}m (RunManager.Instance null={rmNow == null}, "
                    + $"IsInProgress={rmNow?.IsInProgress}, localPlayerResolved="
                    + $"{(rmNow?.DebugOnlyGetState() is { } s2 && LocalContext.GetMe(s2) != null)}).");
                return;
            }
            Log.Write($"[MpFuzz] run started, local player resolved, act map installed "
                + $"({RunManager.Instance!.DebugOnlyGetState()!.Map.GetAllMapPoints().Count()} map points).");

            // Step 6: the run being "in progress" does NOT put anyone on a map node — that needs an
            // actual vote. Unlike everything above, this one does need action from this file (both
            // roles run the identical vote logic; see VoteForNextMapCoordAsync's doc comment for why
            // it must go through the game's own action-queue replication instead of a shortcut).
            if (!await VoteForNextMapCoordAsync(role))
            {
                // VoteForNextMapCoordAsync already logged exactly which part of the vote failed.
                return;
            }

            // Step 7: wait for the combat to actually start, now that a destination has been voted
            // for. Same as the run-start wait, this needs no further action from this file —
            // MapSelectionSynchronizer.MoveToMapCoord (host-only, automatic once every slot has
            // voted) enqueues MoveToMapCoordAction, which is what actually moves the party.
            if (!await WaitForConditionAsync(() => CombatManager.Instance.IsInProgress, CombatStartTimeout))
            {
                Log.Write($"[MpFuzz] FAIL: CombatManager.Instance.IsInProgress never became true within "
                    + $"{CombatStartTimeout.TotalMinutes}m (RunManager.Instance.IsInProgress="
                    + $"{RunManager.Instance?.IsInProgress}).");
                return;
            }

            // Step 8: success — one summary line with everything asked for.
            var cs = UndoSyncMod.GetCombatState();
            var players = cs?.Players;
            string playersDesc = players == null
                ? "null"
                : string.Join(", ", players.Select(p => $"(netId={p.NetId}, character={p.Character.Id.Entry})"));
            ulong myNetId = RunManager.Instance?.NetService?.NetId ?? 0;
            bool checksumEnabled = RunManager.Instance?.ChecksumTracker.IsEnabled ?? false;
            Log.Write($"[MpFuzz] SUCCESS role={role} netId={myNetId} playersInCombat={players?.Count.ToString() ?? "null"} "
                + $"players=[{playersDesc}] checksumTrackerEnabled={checksumEnabled}");
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] RunAsync ERROR: {ex}");
        }
        finally
        {
            // Quit when the run is over — success or diagnosed failure alike — so the process
            // doesn't sit at whatever screen it stopped on, holding the machine's game slot.
            // try/finally (not UndoFuzz's sequential end-of-loop quit) precisely because any step
            // above can `return` early. Godot's own shutdown (not a kill) so logs flush.
            if (!CommandLineHelper.HasArg(NoQuitArg))
            {
                Log.Write($"[MpFuzz] quitting the game (pass --{NoQuitArg} to stay open)");
                NGame.Instance?.GetTree()?.Quit();
            }
            else
            {
                Log.Write($"[MpFuzz] --{NoQuitArg} set — staying open.");
            }
        }
    }

    // ==================================================================================
    // Map vote (step 6)
    // ==================================================================================

    /// <summary>
    /// Resolves the local player and the run's current MapLocation, picks a destination map point,
    /// and votes for it — the minimum needed to move the party off the map screen and into a combat.
    /// Returns true once our own vote is confirmed to have round-tripped; false (with a logged
    /// reason) on any failure. Both host and client call this with identical logic; the
    /// host/client asymmetry lives entirely inside MapSelectionSynchronizer/ActionQueueSynchronizer.
    ///
    /// WHY THIS GOES THROUGH VoteForMapCoordAction + RequestEnqueue, NOT A DIRECT
    /// MapSelectionSynchronizer.PlayerVotedForMapCoord CALL (this file's own earlier draft did the
    /// latter and it was wrong — kept here so nobody "simplifies" it back):
    /// MapSelectionSynchronizer.PlayerVotedForMapCoord (MapSelectionSynchronizer.cs:52) only mutates
    /// that ONE process's own private `_votes` list — nothing about it touches the network. Searching
    /// the entire decompiled tree, it has exactly one caller: VoteForMapCoordAction.ExecuteAction
    /// (VoteForMapCoordAction.cs:46-50), and that action carries a ToNetAction() override returning
    /// NetVoteForMapCoordAction (:52-59) — i.e. it exists to be replicated, not called directly.
    /// GameActions only reach ExecuteAction on both peers via
    /// ActionQueueSynchronizer.RequestEnqueue(GameAction) (ActionQueueSynchronizer.cs:141-166), whose
    /// own summary says it plainly: "If you are the host, the GameAction is directly enqueued, and a
    /// message is sent to notify clients about the action. If you are the client, ... a message is
    /// sent to the host to request that the action be enqueued." Calling PlayerVotedForMapCoord
    /// directly on each process would fill only that process's own vote slot, forever — the other
    /// peer's slot would never arrive, MapSelectionSynchronizer's `_votes.All(...)` check
    /// (MapSelectionSynchronizer.cs:76) would never pass on either side, MoveToMapCoord() would never
    /// fire, and this file would run out the clock on CombatStartTimeout with no diagnostic pointing
    /// at the real cause — worse than an honest failure, because GetVote(me) would falsely look fine
    /// (see the round-trip note below).
    /// </summary>
    private static async Task<bool> VoteForNextMapCoordAsync(string role)
    {
        var runManager = RunManager.Instance;
        var runState = runManager?.DebugOnlyGetState();
        if (runManager == null || runState == null)
        {
            Log.Write("[MpFuzz] FAIL: map vote step — "
                + $"RunManager.Instance null={runManager == null} DebugOnlyGetState() null={runState == null}, "
                + "cannot resolve a run state to vote with.");
            return false;
        }

        var me = LocalContext.GetMe(runState);
        if (me == null)
        {
            Log.Write("[MpFuzz] FAIL: map vote step — LocalContext.GetMe(runState) returned null "
                + "(LocalContext.NetId not set for this process?), cannot vote as an unknown player.");
            return false;
        }

        MapPoint? currentPoint = runState.CurrentMapCoord.HasValue
            ? runState.Map.GetPoint(runState.CurrentMapCoord.Value)
            : runState.Map.StartingMapPoint;
        if (currentPoint == null)
        {
            Log.Write("[MpFuzz] FAIL: map vote step — could not resolve a current MapPoint "
                + $"(CurrentMapCoord={runState.CurrentMapCoord}); Map.GetPoint/StartingMapPoint returned null.");
            return false;
        }

        // Prefer a Monster child so we reach *a* combat — the whole point of this file — but settle
        // for any child (logged) rather than getting stuck picking nothing at all.
        MapPoint? destination = currentPoint.Children.FirstOrDefault(c => c.PointType == MapPointType.Monster);
        bool foundMonster = destination != null;
        destination ??= currentPoint.Children.FirstOrDefault();
        if (destination == null)
        {
            Log.Write($"[MpFuzz] FAIL: map vote step — current point {currentPoint} has no children at all to vote for.");
            return false;
        }
        if (!foundMonster)
        {
            Log.Write($"[MpFuzz] map vote: no Monster child of {currentPoint} — settling for {destination} "
                + $"(PointType={destination.PointType}) instead, just to reach *a* combat.");
        }

        var synchronizer = runManager.MapSelectionSynchronizer;
        MapLocation source = runState.MapLocation;
        var vote = new MapVote { mapGenerationCount = synchronizer.MapGenerationCount, coord = destination.coord };

        var voteAction = new VoteForMapCoordAction(me, source, vote);
        runManager.ActionQueueSynchronizer.RequestEnqueue(voteAction);
        Log.Write($"[MpFuzz] role={role} voted coord={vote.coord} "
            + $"pointType={destination.PointType} mapGenerationCount={vote.mapGenerationCount} source={source} "
            + "— enqueued via RequestEnqueue, awaiting round trip.");

        // GetVote(me) is now a REAL signal, not a tautology: it only flips once our own vote has
        // actually round-tripped through RequestEnqueue and come back out as
        // VoteForMapCoordAction.ExecuteAction on this same process (true for host and client alike —
        // see RequestEnqueue's host/client split above). That is asynchronous, unlike the same-thread
        // mutation a direct PlayerVotedForMapCoord call would have been, so this is a genuine bounded
        // wait rather than a tight/instant retry.
        bool confirmed = await WaitForConditionAsync(() =>
        {
            MapVote? registered = synchronizer.GetVote(me);
            return registered.HasValue
                && registered.Value.coord.Equals(vote.coord)
                && registered.Value.mapGenerationCount == vote.mapGenerationCount;
        }, MapVoteTimeout);

        if (!confirmed)
        {
            Log.Write($"[MpFuzz] FAIL: map vote step — our own vote for {vote.coord} never showed up via "
                + $"GetVote(me) within {MapVoteTimeout.TotalSeconds}s of RequestEnqueue "
                + $"(currently registered={synchronizer.GetVote(me)}). The action likely never made it "
                + "through the queue — check for action-queue/network log lines around this timestamp.");
            return false;
        }

        Log.Write($"[MpFuzz] confirmed our own vote round-tripped: GetVote(me)={synchronizer.GetVote(me)}");
        return true;
    }

    // ==================================================================================
    // Polling helpers
    // ==================================================================================

    /// <summary>
    /// Polls (real time, via Task.Delay — see PollInterval's doc comment) until both _idField and
    /// _ipField read non-null off <paramref name="scene"/>, i.e. until NMultiplayerTest._Ready()
    /// (NMultiplayerTest.cs:235-272) has run and bound its private fields. Bound by a Stopwatch,
    /// same "wall-clock, not poll-count" reasoning as UndoFuzz.WaitForIdleOurTurnAsync's own doc
    /// comment (a poll-count bound's real duration depends on how long each individual delay
    /// actually takes under load).
    /// </summary>
    private static async Task<bool> WaitForSceneReadyAsync(NMultiplayerTest scene)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (FIdField!.GetValue(scene) != null && FIpField!.GetValue(scene) != null)
                return true;
            if (sw.Elapsed > SceneReadyTimeout)
                return false;
            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Polls until <paramref name="scene"/>'s _lobby field is non-null AND its Players count has
    /// reached <see cref="ExpectedPlayerCount"/> (StartRunLobby.cs:109) — "the peer has actually
    /// connected", per the design note. Logs the count every time it changes (including the very
    /// first non-null read) so the host/client handshake is visible in the log even when this
    /// times out. Returns the lobby on success, null on timeout.
    /// </summary>
    private static async Task<StartRunLobby?> WaitForLobbyConnectedAsync(NMultiplayerTest scene)
    {
        var sw = Stopwatch.StartNew();
        int lastLoggedCount = -1;
        while (true)
        {
            var lobby = (StartRunLobby?)FLobby!.GetValue(scene);
            if (lobby != null)
            {
                int count = lobby.Players.Count;
                if (count != lastLoggedCount)
                {
                    Log.Write($"[MpFuzz] lobby.Players.Count={count}");
                    lastLoggedCount = count;
                }
                if (count >= ExpectedPlayerCount)
                    return lobby;
            }
            if (sw.Elapsed > LobbyConnectTimeout)
                return null;
            await Task.Delay(PollInterval);
        }
    }

    /// <summary>Generic real-time poll used for the run-start / combat-start waits (step 5) — same
    /// Stopwatch-bound shape as the two helpers above, extracted since neither of those two waits
    /// needs anything beyond "is this predicate true yet".</summary>
    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (predicate())
                return true;
            if (sw.Elapsed > timeout)
                return false;
            await Task.Delay(PollInterval);
        }
    }
}
