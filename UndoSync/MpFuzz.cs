using System;
using System.Collections.Generic;
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
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// STEPS 1, 2 AND 3 of a multiplayer fuzz harness: get two real game instances into the same combat
/// together, with no UI clicking, drive each instance's own local player through that combat via
/// UndoFuzz's existing single-process driver, propose synchronized undos from the host on a cadence,
/// auto-accept them on the client, and detect and report any checksum divergence between the two
/// peers — including, above all, a divergence that follows a committed restore. Steps 1-2 (get both
/// peers into the same combat and drive it to completion with zero divergences) are unchanged from
/// before; step 3 adds real cross-peer undo on top, going through the SAME vote protocol a human
/// player uses (UndoProtocol.ProposeTarget / OnProposalReceived / CommitAsync) rather than calling
/// ChecksumHook.RestoreTo directly — see <see cref="DriveOurCombatAsync"/>'s own doc comment for how
/// this file's proposing and commit-watching run alongside UndoFuzz's shared drive loop (whose own
/// UndoFuzz.RestoresAllowed=false keeps that shared loop's TWO OWN restore policies from ever firing
/// here, so every restore this run performs went through the real multiplayer vote). Proposing itself
/// (<see cref="ProposeRestoreIfDue"/>) is NOT a separate loop — it runs as a hook
/// (UndoFuzz.MpProposeRestoreHook) invoked from INSIDE UndoFuzz.DriveCombatAsync's own idle window,
/// a fix for a measured defect where an earlier, independent proposal-poll loop kept missing the
/// brief window a concurrently-acting multiplayer peer is actually idle; see that hook's own doc
/// comment for the full diagnosis. Only commit-watching (<see cref="WatchCommitsLoopAsync"/>) is
/// still a background poll loop, since nothing about watching an already-committed restore needs to
/// run inside the drive loop's own idle window.
///
/// WHY DIVERGENCE DETECTION IS THE POINT, AND WHY STEP 3 IS THE REASON THIS HARNESS EXISTS AT ALL:
/// every check this mod has today (the checksum fidelity checks in ChecksumHook, the node/model
/// invariants in UiRefresh) passes when all peers are wrong in the SAME way — that is how both the
/// orb-node bug and the UnsettlingLamp relic bug stayed hidden from every prior fuzzer, singleplayer
/// or headless. Both of those checks are local: they compare a peer against its own snapshot or its
/// own local model, so they are structurally blind to an ASYMMETRIC bug — a restore that leaves the
/// two peers in DIFFERENT states. Real multiplayer gives the one oracle singleplayer structurally
/// cannot: RunManager.Instance.ChecksumTracker compares a checksum of the FULL combat state
/// (NetFullCombatState.FromRun) across peers after every action, and the two peers each computed that
/// checksum from their own INDEPENDENTLY maintained copy of the game state. Detecting that comparison
/// failing — not merely reaching combat, not merely completing it, not merely restoring at all — is
/// the entire reason this harness exists; see <see cref="InstallDivergenceObserverPatch"/> /
/// <see cref="OnStateDivergenceObserved"/> for the passive Harmony observer that makes a divergence
/// impossible to miss, <see cref="WatchForDivergenceAfterCommitAsync"/> for the check that ties a
/// divergence to the specific restore that preceded it, and RunAsync's own final summary (step 10)
/// for the full list of what the pass/fail line is now gated on.
///
/// It still DOES cast exactly one map-coord vote per instance (step 6, unchanged from step 1), purely
/// as the minimum needed to move the party off the map screen and into a combat room; see
/// <see cref="VoteForNextMapCoordAsync"/>'s doc comment for why that vote must go through the game's
/// own replicated action-queue path rather than a shortcut.
///
/// COMPLETELY DORMANT IN NORMAL PLAY, same contract as UndoFuzz.cs: <see cref="MaybeStart"/> is
/// the only entry point, called once from UndoSyncMod.Initialize(), and its very first line is a
/// CommandLineHelper.HasArg check for "undosync-mpfuzz" — absent that flag, nothing else in this
/// file ever executes or subscribes to anything. MaybeStart ALSO refuses to start (logged, not
/// silent) if --undosync-fuzz or --undosync-uitest is present in the SAME process — see MaybeStart's
/// own doc comment for why: step 9 below writes the same shared UndoFuzz static selectors
/// (_activeIdleWaitTimeout/_activeCombatWallClockTimeout/RestoresAllowed) that UndoFuzz's own two
/// paths write, and UndoFuzz.MaybeStart already keeps ITS two paths mutually exclusive for the
/// identical reason.
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
///   8. Log the "combat entered" milestone: role, our own net id (RunManager.Instance.NetService
///      .NetId, INetGameService.cs:20 — already used the same way by ChecksumHook.EnsureSubscribed),
///      the number of players CombatState reports (UndoSyncMod.GetCombatState(), already used by
///      UndoFuzz.cs), each player's character id and net id (Player.Character / Player.NetId,
///      Player.cs:44/:48, plus the pre-existing model.Id.Entry idiom used throughout this mod), and
///      whether RunManager.Instance.ChecksumTracker.IsEnabled (ChecksumTracker.cs:59) is true. NOT
///      labeled "SUCCESS" any more — that label is now reserved for step 10's final verdict, which
///      additionally requires zero checksum divergences, an actually-completed combat, and real
///      progress (see step 10's own list item below).
///   9. STEP 2 (+ STEP 3): resolve our own Player again (LocalContext.GetMe) and drive it through the
///      combat via <see cref="DriveOurCombatAsync"/>, which delegates to UndoFuzz.DriveCombatAsync —
///      see that method's own doc comment for the multiplayer-specific setup (generous timeouts,
///      UndoFuzz's own two restore policies forced off) and for why reusing UndoFuzz's existing
///      driver, rather than writing a second one, was the whole point of exposing it.
///      DriveOurCombatAsync ALSO installs <see cref="ProposeRestoreIfDue"/> as UndoFuzz.
///      MpProposeRestoreHook — host-only restore proposals on a cadence (Part B), invoked from
///      inside UndoFuzz.DriveCombatAsync's own idle window rather than run as a separate loop, see
///      ProposeRestoreIfDue's own doc comment for why — and starts
///      <see cref="WatchCommitsLoopAsync"/> (Part C) concurrently: both-peer commit-watching
///      (fidelity, the stuck-after-restore watch, and the divergence-after-restore check) — see that
///      method's own doc comment for the full design. Divergence detection itself (Part A) is NOT
///      part of either step's own code path — it runs passively, throughout, off the Harmony patch
///      MaybeStart installs before any of this; see InstallDivergenceObserverPatch/
///      OnStateDivergenceObserved.
///  10. Log the final summary and pass/fail verdict — see RunAsync's own step-10 comment block for
///      the full field list and why the pass line requires ALL of: divergenceCount == 0, the combat
///      actually completing (outcome.Completed with no outcome.DriveError), real progress
///      (outcome.TurnsPlayed &gt; 0 AND outcome.CardsPlayed &gt; 0), at least one restore actually
///      COMMITTED (outcome.RestoresCommitted &gt; 0 — step 3's whole point, added this step; a run
///      that proposed nothing or whose proposals never committed proves nothing about cross-peer
///      restore fidelity), zero restore fidelity failures, zero orb invariant violations, and zero
///      card-selection-pending violations. Not divergenceCount == 0 alone, which a run that never
///      drove any combat would trivially also satisfy (measured: a run with combatCompleted=False and
///      a driveError still printed SUCCESS before that was fixed, because zero divergences is
///      meaningless when nothing happened for the two peers to possibly diverge over) — and, as of
///      step 3, not "combat completed with zero divergences" alone either, since that would also
///      trivially pass a run that never actually exercised the one restore path this harness exists
///      to fuzz.
///  11. Quit unless --undosync-mpfuzz-noquit, matching UndoFuzz's existing harness flag. Done from
///      a try/finally (unlike UndoFuzz's sequential end-of-loop quit) because this is a single
///      one-shot attempt, not a combat loop with its own per-iteration failure bucket — any step
///      here can bail out early via `return`, and the process must still not be left stranded at
///      whatever screen it stopped on.
///
/// On any timeout in steps 1-9, log exactly which step it was and the observed state at that moment
/// — a stall must never come out of this file as an unexplained hang.
///
/// MULTIPLAYER IDLE-GATE FIX (formerly "KNOWN RISK, INSTRUMENTED RATHER THAN FIXED HERE" — a live
/// two-instance run has since confirmed both predicted and unpredicted shapes of this bug, and it is
/// now fixed, in UndoFuzz.cs, via a multiplayer-only gate — see
/// UndoFuzz.IsMultiplayerIdleGateOpen/UndoFuzz._useMultiplayerIdleGate for the implementation and
/// UndoFuzz.DriveCombatAsync's own doc comment for the same finding from that side):
/// UndoFuzz.WaitForIdleOurTurnAsync used to gate "ready to act" on UndoSyncMod.CanUndoRedo() alone,
/// which in turn requires CombatState.CurrentSide == Player and ActionQueueSynchronizer.CombatState
/// == PlayPhase — both COMBAT-WIDE shared values, not per-player, and neither one answers "did the
/// action I, this specific peer, just issued actually resolve". That gap took two different measured
/// shapes on the two roles of a live run (--undosync-mpfuzz --undosync-mpfuzz-role=host/client, both
/// hitting driveError="action budget exhausted (800 ...)"):
///
///   HOST (turnsPlayed=797, cardsPlayed=3, 21s) — the shape traced from source before the live run,
///   confirmed by it. Verified against source (CombatManager.cs:932-1071, 1437-1473;
///   PlayerCmd.cs:278-288; CardModel.cs's own CanPlay): when ONE player calls PlayerCmd.EndTurn while
///   the OTHER hasn't yet, SetReadyToEndTurn only adds that player to
///   CombatTurnState.PlayersReadyToEndTurn — it does NOT touch that player's own
///   PlayerCombatState.Phase (stays Play) or the shared ActionQueueSynchronizer.CombatState (stays
///   PlayPhase); only CombatManager.PlayerActionsDisabled flips, and nothing in the OLD
///   CanUndoRedo/WaitForIdleOurTurnAsync chain — nor CardModel.CanPlay — read it. AllPlayersReadyToEndTurn
///   only fires the actual phase transition once EVERY player has readied (CombatManager.cs:
///   1055-1071). Net effect: the driver of the FASTER player saw WaitForIdleOurTurnAsync return Ready
///   again immediately (no wait at all, since nothing it checked changed), found the same
///   empty-of-playable-cards hand it just had, and called PlayerCmd.EndTurn again — a no-op once
///   IsPlayerReadyToEndTurn is already true (PlayerCmd.EndTurn checks that FIRST and returns without
///   enqueuing anything, PlayerCmd.cs:278-288), but DriveCombatAsync still counted it as a turn and
///   burned one unit of ActionBudgetPerCombat every iteration, with NO Task.Delay between iterations.
///
///   CLIENT (turnsPlayed=0, cardsPlayed=800, 3s) — NOT predicted by the source trace above; only the
///   live run exposed it. A client's action is *requested* from the host rather than enqueued locally
///   (ActionQueueSynchronizer.RequestEnqueue, ActionQueueSynchronizer.cs:141-166), so the client's own
///   local ActionQueueSet.IsEmpty (part of CanUndoRedo) can read true while that request is still in
///   flight to the host and back — the OLD check let the driver play its next card before the
///   previous one had actually landed anywhere, all the way through the 800-unit budget.
///
/// Both are a busy-spin/race, not a hang: each resolves on its own (the host's once the slower peer
/// catches up; the client's once each request round-trips) rather than deadlocking — which is exactly
/// why the run's own IdleWait.TimedOut/STALL STATE branch (DescribeStallState) never fired for
/// either; both instead ended in an ordinary-looking "BUDGET EXHAUSTED" that would otherwise misread
/// as "this combat just needed more turns". Two diagnostics were added specifically so this failure
/// shape was distinguishable rather than silently misread, and both stay in place as a backstop now
/// that the gate exists (a settle-wait falling through repeatedly would still look like this):
///   (a) DescribeStallState's turn-coordination section also reflects
///       CombatTurnState.PlayersReadyToEndTurn (UndoFuzz.cs), not just PlayersReadyToBeginEnemyTurn —
///       "which players have ended their turn" is visible whenever this dump fires.
///   (b) UndoFuzz.DriveCombatAsync's BUDGET EXHAUSTED branch also calls DescribeStallState(), not
///       only the two TimedOut branches — because, per the busy-spin shape above, TimedOut was
///       exactly the branch this failure mode could reach last, if at all.
///
/// THE FIX: UndoFuzz.WaitForIdleOurTurnAsync now ANDs two more conditions onto its Ready check,
/// through UndoFuzz.IsMultiplayerIdleGateOpen, but ONLY when UndoFuzz._useMultiplayerIdleGate is set —
/// a static selector written ONLY by this file's own DriveOurCombatAsync below, immediately before
/// its own UndoFuzz.DriveCombatAsync call, following the exact same "only the currently-active path's
/// own setup writes it" discipline as _activeIdleWaitTimeout/RestoresAllowed already do. The headless
/// (--undosync-fuzz) and UI-mode (--undosync-uitest) call graphs never reach DriveOurCombatAsync, so
/// they can never observe anything but that flag's default (false) — WaitForIdleOurTurnAsync's own
/// short-circuiting `||` on the flag means IsMultiplayerIdleGateOpen is never even CALLED on those two
/// paths. DriveCombatAsync's own shared logic is otherwise completely unchanged for every path — see
/// IsMultiplayerIdleGateOpen's own doc comment for exactly which condition closes which shape above.
///
/// Logs to the existing UndoSync-&lt;pid&gt;.log (Log.cs) with a "[MpFuzz]" prefix, same reasoning
/// as UndoFuzz's own "[Fuzz]" prefix: this never runs alongside anything else that cares about the
/// log, so there is nothing to disentangle by splitting files. Divergences additionally get their own
/// "[MpFuzz][divergence]" tag — see InstallDivergenceObserverPatch.
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

    // --- Step 3 (Part B) restore-proposal cadence -------------------------------------------------
    // Consulted from ProposeRestoreIfDue (the body of UndoFuzz.MpProposeRestoreHook, invoked from
    // inside UndoFuzz.DriveCombatAsync's own idle window) — see that method's own doc comment for
    // why Part B moved there from an earlier, independent poll loop.

    /// <summary>Host-only cadence for step 3's restore proposals (Part B): a proposal fires after
    /// every this-many successful driver actions (a TryManualPlay that returned true, or an EndTurn —
    /// the same definition CombatOutcome.ActionsSinceLastProposal counts, incremented at the same
    /// site as UndoFuzz's own ActionsSinceLastDeterministicRestore). Mirrors UndoFuzz.
    /// UiTestActionsPerRestore's value (2) exactly: a real multiplayer combat driven by this harness
    /// plays only a handful of turns total (the step-2 run measured turnsPlayed=5 per peer), the same
    /// "a low cadence is needed for a short combat to produce ANY restores at all" reasoning
    /// UiTestActionsPerRestore's own doc comment gives for UndoFuzz's UI-mode path.</summary>
    private const int ActionsPerProposal = 2;

    /// <summary>Host-only cap on proposals for step 3 (Part B) — deliberately SMALLER than UndoFuzz's
    /// own UiTestMaxRestoresPerCombat (15). Unlike the UI/headless paths, where a "restore" is one
    /// synchronous ChecksumHook.RestoreTo call, a step-3 proposal here is a full network round trip
    /// (propose -&gt; auto-accept vote -&gt; host tally -&gt; broadcast commit -&gt; CommitAsync's own
    /// idle-wait poll, up to 60s, on BOTH peers) — piling up 15 of those inside one real combat risks
    /// spending most of MultiplayerCombatWallClockTimeout (20 minutes) on proposals alone, leaving
    /// little of the budget for the driver to actually keep playing between them. 5 is enough to
    /// exercise the cross-peer checksum oracle (this harness's whole point) repeatedly without
    /// dominating the run.</summary>
    private const int MaxProposalsPerCombat = 5;

    /// <summary>Bounded wait (Part C item 2) used right after WatchCommitsLoopAsync observes a new
    /// commit on this peer: a cross-peer divergence for that restore's own post-restore checksum, if
    /// any, arrives as a network message (ChecksumTracker.OnReceivedStateDivergenceMessage) that is
    /// not guaranteed to have landed by the instant UndoProtocol.CommitCount itself ticked locally —
    /// so _divergenceCount is snapshotted, this is waited out, and it is re-checked, rather than read
    /// inline. Kept short relative to the minutes-scale waits elsewhere in this file: a divergence
    /// report rides the SAME post-action checksum round trip every ordinary action already uses
    /// (ChecksumTracker.CompareChecksums, run host-side on every ChecksumDataMessage it receives), not
    /// a slow/best-effort path, so it is expected to resolve in well under this bound if it is going
    /// to happen at all.</summary>
    private static readonly TimeSpan DivergenceWatchAfterCommit = TimeSpan.FromSeconds(5);

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
    ///
    /// Also refuses to start (logged, not silent) if --undosync-fuzz or --undosync-uitest is present
    /// in the SAME process — this file's own step 9 (DriveOurCombatAsync) now writes the same shared
    /// UndoFuzz static selectors (_activeIdleWaitTimeout/_activeCombatWallClockTimeout/RestoresAllowed)
    /// that UndoFuzz's own two paths write before their own DriveCombatAsync calls. UndoFuzz.MaybeStart
    /// already keeps ITS two paths mutually exclusive within one process for the identical reason (see
    /// its own doc comment: "running both concurrently would corrupt whichever one loses the race") —
    /// this is the same guard, applied here rather than inside UndoFuzz.cs, so UndoFuzz's own entry
    /// logic (and therefore its two existing paths' behaviour) stays completely untouched by this file.
    /// </summary>
    internal static void MaybeStart()
    {
        try
        {
            if (!CommandLineHelper.HasArg(MpFuzzArg)) return;

            if (CommandLineHelper.HasArg("undosync-fuzz") || CommandLineHelper.HasArg("undosync-uitest"))
            {
                Log.Write($"[MpFuzz] --{MpFuzzArg} was passed alongside --undosync-fuzz/--undosync-uitest "
                    + "— these all drive combat through the same shared UndoFuzz.DriveCombatAsync "
                    + "selectors and cannot safely run in the same process (see MaybeStart's own doc "
                    + "comment). Not starting.");
                return;
            }

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

            // Part A: installed here, unconditionally, regardless of role — see
            // InstallDivergenceObserverPatch's own doc comment for why OnReceivedStateDivergenceMessage
            // fires on BOTH host and client, so both roles need this to observe a divergence on their
            // own side rather than relying on the other peer's log.
            InstallDivergenceObserverPatch();

            // Step 3, Part A: fuzz-only auto-accept for the undo vote — see
            // UndoProtocol.AutoAcceptForFuzz's own doc comment for why this exact flag/placement
            // makes leaking into a normal game structurally impossible (only reachable once
            // --undosync-mpfuzz's own gate, including the mutual-exclusivity check above it, has
            // already passed — same discipline as InstallDivergenceObserverPatch immediately above).
            // Set on BOTH roles unconditionally: only the CLIENT will ever actually receive an
            // UndoProposalMessage in this file's own step-3 design (only the host proposes, see
            // ProposeRestoreIfDue's own doc comment), but setting it on the host too costs
            // nothing and keeps this call site simple.
            UndoProtocol.AutoAcceptForFuzz = true;

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
    // Divergence detection (Part A) — see this file's own top-of-file "WHY DIVERGENCE DETECTION
    // IS THE POINT" paragraph.
    // ==================================================================================

    /// <summary>Total number of checksum divergences observed by <see cref="OnStateDivergenceObserved"/>
    /// this process. Read by RunAsync's final summary (step 10) — the success line requires this to be
    /// zero; see that block for why. Single-threaded, same reasoning as UndoFuzz's own _gameErrors
    /// field: every message-handler call in this codebase (including
    /// ChecksumTracker.OnReceivedStateDivergenceMessage) is driven from INetGameService.Update, whose
    /// own doc comment says plainly "Messages... will not be processed unless this is called" — i.e.
    /// dispatched from the game's own per-frame update pump, the same single thread as everything else
    /// in this file. No lock needed.</summary>
    private static int _divergenceCount;

    /// <summary>Human-readable detail of the MOST RECENT divergence observed — repeated in step 10's
    /// summary so a divergence is visible in both the immediate per-occurrence "[MpFuzz][divergence]"
    /// line and the run's final verdict, without needing to scroll back.</summary>
    private static string _lastDivergenceDetail = "";

    /// <summary>
    /// Installs a Harmony POSTFIX on
    /// ChecksumTracker.OnReceivedStateDivergenceMessage(StateDivergenceMessage message, ulong senderId)
    /// (ChecksumTracker.cs:136), so this file finds out about every checksum divergence the moment the
    /// game itself does, on both sides of the connection.
    ///
    /// WHY THIS ONE OF THE THREE METHODS NAMED IN THE DESIGN NOTE (ChecksumTracker.cs:136/218/241):
    ///   - OnReceivedStateDivergenceMessage(StateDivergenceMessage message, ulong senderId) (:136) —
    ///     the one patched here. Verified call graph: CompareChecksums (host-only, run when a client's
    ///     ChecksumDataMessage doesn't match the host's own tracked checksum) sends a
    ///     StateDivergenceMessage to that one client (ChecksumTracker.cs:214); the client's
    ///     OnReceivedStateDivergenceMessage receives it, logs/reports via LogStateDivergence, and —
    ///     because LogStateDivergence checks `_netService.Type == NetGameType.Client`
    ///     (ChecksumTracker.cs:229) — sends its OWN StateDivergenceMessage back to the host with no
    ///     explicit recipient (i.e. to the host, the client's only peer); the host's OWN
    ///     OnReceivedStateDivergenceMessage then receives THAT message too. So this handler genuinely
    ///     fires on BOTH peers for the same divergence, confirming the method's own doc comment ("Also
    ///     called on the host when the client receives the host's message, so that the host knows what
    ///     state the client was in and can log both.") — the other two candidates below do not have
    ///     this guarantee on their own.
    ///   - LogStateDivergence(TrackedChecksum, StateDivergenceMessage, ulong, int) (:218) — called from
    ///     inside OnReceivedStateDivergenceMessage on both sides too (so it fires exactly when the
    ///     method above does), and carries richer detail (the full NetFullCombatState dumps via
    ///     `localChecksum`). NOT used as the hook point for a mechanical reason: its first parameter is
    ///     ChecksumTracker's own PRIVATE nested struct TrackedChecksum, which cannot be named from this
    ///     assembly — a Harmony patch method for it would need Harmony's positional `__0`/`object`
    ///     parameter-injection convention instead of ordinary named-parameter matching (the convention
    ///     this mod's other patches, e.g. UndoFuzz.OnGameLogError, already rely on).
    ///     OnReceivedStateDivergenceMessage's own parameters (a public struct and a ulong) need none of
    ///     that.
    ///   - ReportDivergenceToSentry(TrackedChecksum, StateDivergenceMessage, ulong, int) (:241) — same
    ///     TrackedChecksum problem as LogStateDivergence, PLUS it only runs `if (!TestMode.IsOn)`
    ///     (ChecksumTracker.cs:224, guarding both the Log.Error above it and the call to this method) —
    ///     true for MpFuzz's real multiplayer run, but a strictly narrower condition than
    ///     OnReceivedStateDivergenceMessage itself needs to satisfy, for zero extra benefit here (this
    ///     file only needs "a divergence happened, with this id/peer", not the Sentry attachment
    ///     payload).
    ///
    /// A POSTFIX specifically, not a prefix: a prefix could return false and skip the original method
    /// entirely (Harmony's own mechanism for suppressing a call). A postfix runs only AFTER the real
    /// method has already done its own job, so this can never alter or block the game's own divergence
    /// handling (client abandon / host disconnect, RunManager.cs:1514-1533) — the "passive observer
    /// only" requirement for all of Part A.
    ///
    /// Same gating/instance/never-PatchAll reasoning as UndoFuzz.InstallGameErrorCapturePatch (see its
    /// own doc comment): called manually from MaybeStart, only once --undosync-mpfuzz's own gate
    /// (including the --undosync-fuzz/--undosync-uitest mutual-exclusivity check above it) has already
    /// passed, using a dedicated Harmony instance ("undosync.mpfuzz", distinct from both
    /// UndoSyncMod.Initialize()'s "com.beomsu.undosync" and UndoFuzz's own "undosync.fuzz") — never a
    /// [HarmonyPatch] attribute class, so a normal player's game (no --undosync-mpfuzz on the command
    /// line) never has ChecksumTracker touched at all.
    /// </summary>
    private static void InstallDivergenceObserverPatch()
    {
        try
        {
            var harmony = new Harmony("undosync.mpfuzz");
            var original = AccessTools.Method(typeof(ChecksumTracker), "OnReceivedStateDivergenceMessage");
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(MpFuzz), nameof(OnStateDivergenceObserved)));
            harmony.Patch(original, postfix: postfix);
            Log.Write("[MpFuzz] Patched ChecksumTracker.OnReceivedStateDivergenceMessage (mpfuzz-only, passive postfix observer) to record checksum divergences.");
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] WARNING: failed to install divergence observer patch on ChecksumTracker.OnReceivedStateDivergenceMessage: {ex.Message}");
        }
    }

    /// <summary>
    /// Harmony postfix target for ChecksumTracker.OnReceivedStateDivergenceMessage(StateDivergenceMessage
    /// message, ulong senderId) (ChecksumTracker.cs:136) — see InstallDivergenceObserverPatch for
    /// how/why this gets installed and why this specific method was chosen over the other two
    /// candidates.
    ///
    /// Harmony matches postfix parameters by NAME against the original's own parameter names (same
    /// convention UndoFuzz.OnGameLogError's own doc comment describes for its `text` parameter) — both
    /// `message` and `senderId` here are named identically to ChecksumTracker.cs:136's own signature,
    /// and neither needs Harmony's `__0`-style positional injection since both types
    /// (StateDivergenceMessage, ulong) are public.
    ///
    /// Records the checksum id and the remote peer id (the two pieces of detail this task's design
    /// note asks for explicitly), plus the remote checksum value carried on the message itself
    /// ("whatever the message carries" — StateDivergenceMessage.senderChecksum is a
    /// NetChecksumData{id, checksum}, per StateDivergenceMessage.cs; .senderCombatState is the full
    /// NetFullCombatState dump, which ChecksumTracker's OWN Log.Error already prints in full inside
    /// LogStateDivergence — not repeated here, since that would duplicate a very large string for no
    /// extra signal beyond "a divergence happened, with this id/peer").
    ///
    /// A pure observer: `void` return means Harmony always keeps whatever the original method already
    /// did (see InstallDivergenceObserverPatch's own "postfix, not prefix" paragraph), and the
    /// try/catch below means a bug in this recording code can never propagate into — or interrupt —
    /// the game's own divergence handling (client abandon / host disconnect, RunManager.cs:1514-1533).
    /// </summary>
    private static void OnStateDivergenceObserved(StateDivergenceMessage message, ulong senderId)
    {
        try
        {
            _divergenceCount++;
            ulong myNetId = RunManager.Instance?.NetService?.NetId ?? 0;
            _lastDivergenceDetail = $"checksumId={message.senderChecksum.id} remotePeerId={senderId} "
                + $"remoteChecksum={message.senderChecksum.checksum} ourNetId={myNetId}";
            Log.Write($"[MpFuzz][divergence] #{_divergenceCount} {_lastDivergenceDetail}");

            // Fuzz-only full diagnostic dump — see ChecksumHook.DumpDivergenceDiagnostics's own doc
            // comment for exactly what this writes (the "[MpFuzz][diag] SUMMARY"/"STATE" lines) and
            // why: the line above only carries the remote peer's checksum HASH, never this peer's own
            // payload for the same id, which is what's actually needed to diagnose (not just detect) a
            // divergence. Internally gated on the same --undosync-mpfuzz flag this whole file's own
            // MaybeStart entry gate already checked, so this call is always safe/cheap either way.
            ChecksumHook.DumpDivergenceDiagnostics(message.senderChecksum.id, senderId, message.senderChecksum.checksum);
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] OnStateDivergenceObserved ERROR: {ex.Message}");
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

            // Step 8: combat entered — same signal step 1 already proved (playersInCombat/
            // checksumTrackerEnabled). Logged as a milestone, not "SUCCESS": that label is now
            // reserved for step 10's final verdict below, which additionally requires zero checksum
            // divergences.
            var cs = UndoSyncMod.GetCombatState();
            var players = cs?.Players;
            string playersDesc = players == null
                ? "null"
                : string.Join(", ", players.Select(p => $"(netId={p.NetId}, character={p.Character.Id.Entry})"));
            ulong myNetId = RunManager.Instance?.NetService?.NetId ?? 0;
            bool checksumEnabled = RunManager.Instance?.ChecksumTracker.IsEnabled ?? false;
            Log.Write($"[MpFuzz] combat entered: role={role} netId={myNetId} playersInCombat={players?.Count.ToString() ?? "null"} "
                + $"players=[{playersDesc}] checksumTrackerEnabled={checksumEnabled}");

            // Step 9: STEP 2 + STEP 3 — drive OUR OWN local player through the combat, and (via
            // DriveOurCombatAsync's own ProposeRestoreIfDue hook + WatchCommitsLoopAsync)
            // propose/watch synchronized undos alongside it. Resolve `me` again (the map-vote step's
            // own `me`, inside VoteForNextMapCoordAsync, is out of scope here) via the same
            // LocalContext.GetMe(runState) idiom used throughout this mod.
            var runStateForCombat = RunManager.Instance?.DebugOnlyGetState();
            var me = runStateForCombat != null ? LocalContext.GetMe(runStateForCombat) : null;
            if (me == null)
            {
                Log.Write("[MpFuzz] FAIL: combat-drive step — LocalContext.GetMe(runState) returned "
                    + "null after CombatManager.Instance.IsInProgress became true; cannot resolve "
                    + "which creature is ours to drive.");
                return;
            }

            int restoreSectionFailuresBefore = StateSnapshot.RestoreSectionFailureCount;
            int uiRefreshFailuresBefore = UiRefresh.UiRefreshFailureCount;
            int orbInvariantViolationsBefore = UiRefresh.OrbInvariantViolationCount;

            var outcome = await DriveOurCombatAsync(role, me);

            int restoreSectionFailureDelta = StateSnapshot.RestoreSectionFailureCount - restoreSectionFailuresBefore;
            int uiRefreshFailureDelta = UiRefresh.UiRefreshFailureCount - uiRefreshFailuresBefore;
            int orbInvariantViolationDelta = UiRefresh.OrbInvariantViolationCount - orbInvariantViolationsBefore;

            // Step 10: summary (Part C) — one block per instance. The SUCCESS line requires
            // divergenceCount == 0 — see this file's own top-of-file "WHY DIVERGENCE DETECTION IS THE
            // POINT" paragraph for why that is non-negotiable: every other line in this block can look
            // fine while both peers were wrong in the same way, which is exactly how the orb-node bug
            // and the UnsettlingLamp relic bug both stayed hidden. Only a divergence proves the two
            // peers actually disagreed.
            //
            // divergenceCount == 0 alone is NOT sufficient, though — measured on a live run: a combat
            // that hit combatCompleted=False with a driveError (the multiplayer busy-spin bug this
            // file's own top-of-file "MULTIPLAYER IDLE-GATE FIX" paragraph describes) still printed
            // "SUCCESS ... zero checksum divergences observed", because zero divergences is trivially
            // true when nothing ever ran for the two peers to possibly disagree over. This is the same
            // false-pass shape UndoFuzz's own UI-mode path was bitten by before and now refuses to
            // repeat — RunUiTestCombatsAsync will not print PROVEN unless restores and node rebuilds
            // actually occurred, not just "zero violations observed". Following that precedent: SUCCESS
            // here requires ALL of divergenceCount == 0, the combat actually completing (Completed with
            // no DriveError), AND real progress (TurnsPlayed and CardsPlayed both above zero). A run
            // that did not drive a combat must never read as a pass — the else branch below names
            // exactly which of these failed, rather than only ever blaming divergences.
            bool zeroDivergences = _divergenceCount == 0;
            bool combatActuallyCompleted = outcome.Completed && outcome.DriveError == null;
            bool madeRealProgress = outcome.TurnsPlayed > 0 && outcome.CardsPlayed > 0;
            // Step 3 additions (Part D) — SUCCESS now ALSO requires all of these. A run that
            // committed no restores must never pass: that is the entire point of step 3 (see this
            // file's own top-of-file "WHY DIVERGENCE DETECTION IS THE POINT" paragraph — divergence
            // detection is worthless if nothing ever restored for the two peers to possibly disagree
            // about the RESULT of).
            bool restoresActuallyCommitted = outcome.RestoresCommitted > 0;
            bool zeroFidelityFailures = outcome.FidelityFailures == 0;
            bool zeroOrbInvariantViolations = orbInvariantViolationDelta == 0;
            bool zeroSelectionViolations = UndoProtocol.SelectionPendingViolations == 0;

            // Both peers dump their whole ring so the same ids can be diffed side by side.

            ChecksumHook.DumpDivergenceRing(role);

            Log.Write($"[MpFuzz] ==================== summary: role={role} ====================");
            Log.Write($"[MpFuzz] role={role} netId={myNetId} combatCompleted={outcome.Completed} "
                + $"turnsPlayed={outcome.TurnsPlayed} cardsPlayed={outcome.CardsPlayed} "
                + $"divergenceCount={_divergenceCount} "
                + $"restoresProposed={outcome.RestoresProposed} "
                + $"restoresCommitted={outcome.RestoresCommitted} "
                + $"fidelityFailures={outcome.FidelityFailures} "
                + $"divergencesAfterRestore={outcome.DivergencesAfterRestore} "
                + $"selectionViolations={UndoProtocol.SelectionPendingViolations} "
                + $"stuckAfterRestoreCount={(outcome.StuckAfterRestore ? 1 : 0)} "
                + $"restoreSectionFailureDelta={restoreSectionFailureDelta} "
                + $"uiRefreshFailureDelta={uiRefreshFailureDelta} "
                + $"orbInvariantViolationDelta={orbInvariantViolationDelta}"
                + (outcome.DriveError != null ? $" driveError=\"{outcome.DriveError}\"" : "")
                + (outcome.StuckAfterRestore ? $" stuckAfterRestore=\"{outcome.StuckAfterRestoreDetail}\"" : ""));

            // Proposal-failure visibility: a run that reports restoresProposed=0 must explain itself
            // rather than leave the reader to guess (this is exactly the shape the measured defect
            // ProposeRestoreIfDue's own doc comment describes took — see UndoFuzz.MpProposeRestoreHook
            // for the full diagnosis). Host-only, same as the counters themselves: ProposeRestoreIfDue
            // returns immediately on the client (role != "host") without touching any of these, so a
            // client's own breakdown would only ever read all-zero and add nothing.
            if (role == "host")
            {
                Log.Write($"[MpFuzz] role={role} propose skip breakdown (of {outcome.RestoresProposed} "
                    + $"proposal(s) actually made, cap={MaxProposalsPerCombat}): "
                    + $"skippedForCadence={outcome.ProposeSkippedCadence} "
                    + $"skippedForCap={outcome.ProposeSkippedCap} "
                    + $"skippedForNoOlderSyncPoint={outcome.ProposeSkippedNoTarget} "
                    + $"skippedForCanUndoRedoFalse={outcome.ProposeSkippedCanUndoRedoFalse}.");
            }

            if (zeroDivergences && combatActuallyCompleted && madeRealProgress && restoresActuallyCommitted
                && zeroFidelityFailures && zeroOrbInvariantViolations && zeroSelectionViolations)
            {
                Log.Write($"[MpFuzz] SUCCESS role={role} netId={myNetId} — combat driven with "
                    + $"{outcome.RestoresCommitted} restore(s) committed, zero checksum divergences, "
                    + "zero fidelity failures, zero orb invariant violations, and zero selection "
                    + "violations. See the summary line above for the full counts.");
            }
            else
            {
                var reasons = new List<string>();
                if (!zeroDivergences)
                {
                    reasons.Add($"{_divergenceCount} checksum divergence(s) observed (most recent: "
                        + $"{_lastDivergenceDetail}) — see the \"[MpFuzz][divergence]\" line(s) above "
                        + "for the checksum id(s) and remote peer id(s) involved");
                }
                if (!combatActuallyCompleted)
                {
                    if (outcome.StuckAfterRestore)
                        reasons.Add("driver got stuck after a committed restore "
                            + $"(detail=\"{outcome.StuckAfterRestoreDetail}\") — the exact shape an "
                            + "action-id-reuse bug in ChecksumHook.RestoreTo would take");
                    else if (outcome.DriveError != null)
                        reasons.Add($"combat did not complete: driveError=\"{outcome.DriveError}\"");
                    else
                        reasons.Add("combat did not complete (Completed=false with no driveError recorded)");
                }
                if (!madeRealProgress)
                {
                    reasons.Add("no real progress was made "
                        + $"(turnsPlayed={outcome.TurnsPlayed}, cardsPlayed={outcome.CardsPlayed})");
                }
                if (!restoresActuallyCommitted)
                {
                    reasons.Add($"NO RESTORES WERE COMMITTED (restoresProposed={outcome.RestoresProposed}, "
                        + "restoresCommitted=0) — step 3 exists to prove cross-peer restore fidelity, "
                        + "which a run that never actually restored cannot do");
                }
                if (!zeroFidelityFailures)
                {
                    reasons.Add($"{outcome.FidelityFailures} restore fidelity failure(s) on this peer "
                        + "— see the ChecksumHook RESTORE FIDELITY line(s) above");
                }
                if (!zeroOrbInvariantViolations)
                {
                    reasons.Add($"{orbInvariantViolationDelta} orb invariant violation(s) "
                        + "(UiRefresh.OrbInvariantViolationCount)");
                }
                if (!zeroSelectionViolations)
                {
                    reasons.Add($"{UndoProtocol.SelectionPendingViolations} card-selection-pending "
                        + "violation(s) — see the \"[UndoProtocol] SELECTION VIOLATION\" line(s) above");
                }
                Log.Write($"[MpFuzz] FAILURE role={role} netId={myNetId} — THIS IS NOT A PASS — "
                    + string.Join("; ", reasons) + ".");
            }
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
    // Combat drive (step 9, Part B)
    // ==================================================================================

    /// <summary>
    /// Drives OUR OWN local player through the combat entered by RunAsync's step 7/8, by delegating to
    /// UndoFuzz.DriveCombatAsync — the SAME single-process driver UndoFuzz's headless
    /// (--undosync-fuzz) and UI-mode (--undosync-uitest) paths both already use unmodified (see
    /// UndoFuzz.cs's own top-of-file doc comment, "Both paths reuse the exact same drive loop").
    /// Reused rather than reimplemented: it already handles card selection/targeting (TryManualPlay,
    /// CardModel's real production play path), end-turn (PlayerCmd.EndTurn), the action budget, and
    /// the stuck-after-restore watch — see UndoFuzz.DriveCombatAsync's own doc comment for the full
    /// behaviour, none of which changes here.
    ///
    /// Made reachable from this file by the MINIMUM visibility change UndoFuzz.cs needed:
    /// DriveCombatAsync itself, its CombatOutcome result type, and the two per-path timeout selectors
    /// (_activeIdleWaitTimeout/_activeCombatWallClockTimeout) went from `private` to `internal` —
    /// nothing about their logic changed. See UndoFuzz.MultiplayerIdleWaitTimeout /
    /// MultiplayerCombatWallClockTimeout's own doc comments for the new multiplayer values, and
    /// UndoFuzz.RestoresAllowed's own doc comment for the new restore off-switch this path uses.
    ///
    /// MULTIPLAYER-SPECIFIC SETUP, all done here (never inside UndoFuzz.cs, so the headless/UI paths
    /// stay byte-for-byte unaffected — see each written field's own doc comment for why leaking is
    /// structurally impossible):
    ///   1. _activeIdleWaitTimeout/_activeCombatWallClockTimeout set to the new Multiplayer* constants
    ///      — two real OS processes over real (loopback) ENet, each doing its own real asset loads and
    ///      real animations, same "minutes, not seconds" reasoning this file's own class-level timeout
    ///      constants already use for the pre-combat steps, re-derived again here for the in-combat
    ///      drive loop.
    ///   2. UndoFuzz.RestoresAllowed = false — NO RESTORES IN STEP 2. Restore/undo
    ///      (ChecksumHook.RestoreTo, UndoPicker, the undo vote) is step 3 and is deliberately not
    ///      started by this file; setting this false makes UndoFuzz.AttemptRestore return null
    ///      unconditionally, regardless of which restore policy
    ///      (UndoFuzz's own private _useDeterministicRestorePolicy) would otherwise have been
    ///      consulted — that selector is therefore never even read on this path and is left completely
    ///      untouched (still private, still defaulted to the headless policy) by this file.
    ///   3. UndoFuzz._useMultiplayerIdleGate = true — see this file's own top-of-file "MULTIPLAYER
    ///      IDLE-GATE FIX" paragraph for the two measured live-run failure shapes this closes
    ///      (a host busy-spinning on no-op EndTurn calls; a client racing ahead of its own in-flight,
    ///      host-requested actions) and UndoFuzz.IsMultiplayerIdleGateOpen's own doc comment for
    ///      exactly how. Same "only the currently-active path's own setup writes it, immediately
    ///      before its own DriveCombatAsync call" discipline as the two fields above.
    ///
    /// `rng` is a fresh, non-seeded System.Random — never the game's own Rng/RunRngSet streams (same
    /// reasoning as every other harness-side rng in this mod), and deliberately NOT derived from a
    /// shared seed the way UndoFuzz's headless/UI paths are: each side drives its OWN local player
    /// independently (per this file's own class-level doc comment), so there is no cross-process
    /// choice to keep in sync, and step 2's goal (prove divergence detection works against two
    /// independently-acting real peers) is better served by genuinely independent choices on each side
    /// than by a shared seed that could mask a desync behind identical decisions.
    /// </summary>
    private static async Task<UndoFuzz.CombatOutcome> DriveOurCombatAsync(string role, Player me)
    {
        UndoFuzz._activeIdleWaitTimeout = UndoFuzz.MultiplayerIdleWaitTimeout;
        UndoFuzz._activeCombatWallClockTimeout = UndoFuzz.MultiplayerCombatWallClockTimeout;
        UndoFuzz.RestoresAllowed = false;
        UndoFuzz._useMultiplayerIdleGate = true;

        var outcome = new UndoFuzz.CombatOutcome { CombatIndex = 0, BaseSeed = $"mpfuzz-{role}", Seed = $"mpfuzz-{role}" };
        var rng = new Random();
        var proposeRng = new Random();

        Log.Write($"[MpFuzz] role={role} driving combat "
            + $"(idleWaitTimeout={UndoFuzz.MultiplayerIdleWaitTimeout.TotalMinutes}m, "
            + $"combatWallClockTimeout={UndoFuzz.MultiplayerCombatWallClockTimeout.TotalMinutes}m, "
            + "restoresAllowed=false, multiplayerIdleGate=true).");

        // Step 3, Part B — MEASURED FIX: install the propose hook, invoked by UndoFuzz.AttemptRestore
        // from INSIDE UndoFuzz.DriveCombatAsync's own idle window, instead of running it as a
        // separately-scheduled poll loop — see ProposeRestoreIfDue's own doc comment and
        // UndoFuzz.MpProposeRestoreHook's own doc comment for the measured defect this closes (a
        // clean two-instance run that completed a real combat on both peers still reported
        // restoresProposed=0, because the old independent poll kept missing the brief window
        // UndoSyncMod.CanUndoRedo() is true for a concurrently-acting multiplayer peer). Same "only
        // the currently-active path's own setup writes it, immediately before its own
        // DriveCombatAsync call" discipline as every other MpFuzz-only selector above.
        UndoFuzz.MpProposeRestoreHook = () => ProposeRestoreIfDue(role, outcome, proposeRng);

        // Step 3, Part C only now: concurrent background loop that just watches for restores landing
        // via the real commit path — started alongside (not instead of) UndoFuzz.DriveCombatAsync
        // below. See WatchCommitsLoopAsync's own doc comment for the full design and for why running
        // it concurrently via a fire-and-forget Task is safe under this file's own single-threaded,
        // cooperative-interleaving-via-await model (same assumption every other polling loop in this
        // mod already relies on — see e.g. UndoFuzz's own "_gameErrors" doc comment).
        _ = WatchCommitsLoopAsync(role, outcome);

        await UndoFuzz.DriveCombatAsync(0, me, rng, outcome);
        return outcome;
    }

    // ==================================================================================
    // Restore proposal (step 3, Part B) + commit watch (step 3, Part C)
    // ==================================================================================

    /// <summary>
    /// Step 3, Part B — the body of UndoFuzz.MpProposeRestoreHook, installed by DriveOurCombatAsync
    /// above and invoked by UndoFuzz.AttemptRestore. Proposes a restore to a uniformly random older
    /// stored sync point on a fixed cadence.
    ///
    /// MEASURED DEFECT THIS REPLACES: this logic used to live inline in what was then
    /// WatchAndProposeLoopAsync, an independent Task.Delay(PollInterval) background loop running
    /// concurrently with, but not synchronized to, the drive loop. A clean two-instance run that
    /// completed a real multiplayer combat on both peers (combatCompleted=True, host 4 turns/15
    /// cards, client 5 turns/14 cards, 46s, 29 sync points stored) still reported
    /// "restoresProposed=0 restoresCommitted=0 FAILURE — NO RESTORES WERE COMMITTED" —
    /// outcome.ActionsSinceLastProposal was being incremented correctly by
    /// UndoFuzz.DriveCombatAsync, but in multiplayer the two players act concurrently, so each peer's
    /// own ActionQueueSet.IsEmpty (part of UndoSyncMod.CanUndoRedo()) is only briefly true, between
    /// one action landing and the next being issued. A poll on its own independent schedule had no
    /// reason to land inside that narrow window and kept missing it entirely.
    ///
    /// THE FIX: propose from INSIDE the driver's own idle window instead, where CanUndoRedo() is
    /// already known to hold. UndoFuzz.AttemptRestore invokes UndoFuzz.MpProposeRestoreHook (this
    /// method, via the closure DriveOurCombatAsync installs) at exactly the point
    /// UndoFuzz.DriveCombatAsync has just received IdleWait.Ready from
    /// UndoFuzz.WaitForIdleOurTurnAsync — i.e. PlayerCombatState.Phase == Play and CanUndoRedo() both
    /// hold, for THIS peer, right now. That makes missing the window structurally impossible instead
    /// of merely unlikely.
    ///
    /// Only the host proposes: the README notes simultaneous proposals from two peers just wait out
    /// UndoProtocol's own 30s vote timeout against each other (TimeoutFrames, UndoProtocol.cs)
    /// instead of ever committing, which would waste run time on a scenario this harness has no
    /// interest in reproducing.
    ///
    /// Gates, in the same order the old loop checked them (cheapest/most-deterministic first):
    ///   1. MaxProposalsPerCombat — a hard cap on proposals this combat.
    ///   2. ActionsPerProposal — the fixed cadence (see that constant's own doc comment for why).
    ///   3. UndoSyncMod.CanUndoRedo() — the same idle/safety gate UndoProtocol.ProposeTarget
    ///      re-checks internally regardless, so this is a cheap skip, not the load-bearing guard —
    ///      the load-bearing guarantee is now the call site itself (see this method's own "THE FIX"
    ///      paragraph above). Kept anyway as a cheap assertion, and logged LOUDLY rather than merely
    ///      skipped if it ever reads false: that would mean the idle-window invariant this whole fix
    ///      relies on broke, which is worth knowing about immediately, not silently re-hiding behind
    ///      another skip.
    ///   4. UndoFuzz.PickRandomOlderSyncPoint returning null — nothing older than "now" stored yet
    ///      (reused rather than duplicated; that method's own visibility went private -&gt; internal
    ///      for exactly this call site).
    /// Each skip increments its own counter on <paramref name="outcome"/> (CombatOutcome.
    /// ProposeSkipped*, see their own doc comments in UndoFuzz.cs) so a zero-proposal run can explain
    /// itself in RunAsync's step-10 summary instead of reporting restoresProposed=0 with no further
    /// clue — see the summary block's new "propose skip breakdown" line.
    /// </summary>
    private static void ProposeRestoreIfDue(string role, UndoFuzz.CombatOutcome outcome, Random rng)
    {
        if (role != "host") return; // only the host ever proposes — see this method's own doc comment

        if (outcome.RestoresProposed >= MaxProposalsPerCombat)
        {
            outcome.ProposeSkippedCap++;
            return;
        }
        if (outcome.ActionsSinceLastProposal < ActionsPerProposal)
        {
            outcome.ProposeSkippedCadence++;
            return;
        }
        if (!UndoSyncMod.CanUndoRedo())
        {
            // Should be structurally impossible — see this method's own "THE FIX" paragraph: the
            // only caller, UndoFuzz.AttemptRestore, is only reached after
            // UndoFuzz.WaitForIdleOurTurnAsync has already confirmed CanUndoRedo()==true for this
            // same peer, on this same synchronous call chain, with nothing awaited in between. A
            // false read here means that invariant did not hold — log it loudly rather than let it
            // silently re-create the exact "why did nothing propose" mystery this fix exists to
            // eliminate.
            outcome.ProposeSkippedCanUndoRedoFalse++;
            Log.Write($"[MpFuzz] role={role} WARNING: ProposeRestoreIfDue ran with CanUndoRedo()==false "
                + "— this should be impossible from this call site (see the method's own doc comment). "
                + "The idle-window invariant this fix relies on did not hold; investigate before "
                + "trusting proposal cadence again.");
            return;
        }

        var target = UndoFuzz.PickRandomOlderSyncPoint(rng);
        if (target == null)
        {
            outcome.ProposeSkippedNoTarget++;
            return; // nothing older than "now" stored yet — try again once more sync points exist
        }

        outcome.ActionsSinceLastProposal = 0;
        outcome.RestoresProposed++;
        Log.Write($"[MpFuzz] role={role} PROPOSING restore -> id={target.ChecksumId} "
            + $"({target.Context}) (proposal {outcome.RestoresProposed}/{MaxProposalsPerCombat}).");
        UndoProtocol.ProposeTarget(target.ChecksumId);
    }

    /// <summary>
    /// Step 3, Part C only — Part B (proposing) moved into <see cref="ProposeRestoreIfDue"/> above,
    /// see that method's own doc comment for the measured defect that fix closes. This loop keeps
    /// its commit-watching half unchanged: started concurrently with UndoFuzz.DriveCombatAsync from
    /// DriveOurCombatAsync above and running for as long as CombatManager.Instance.IsInProgress, it
    /// watches UndoProtocol.CommitCount every PollInterval tick for restores that landed on THIS peer
    /// via the real vote/commit path, and for each newly observed commit:
    ///   1. Records ChecksumHook.LastRestoreFidelityOk into outcome.FidelityFailures if false.
    ///   2. Looks the committed SyncPoint back up (ChecksumHook.TryGetSyncPoint) and hands it to
    ///      UndoFuzz.NotifyExternalRestore, which arms DriveCombatAsync's own preexisting
    ///      stuck-after-restore watch for it — see that method's own doc comment for why this is
    ///      the SAME mechanism the headless/UI paths already use, not a second one (requirement 3:
    ///      "the driver can still act afterwards").
    ///   3. Starts WatchForDivergenceAfterCommitAsync to check, a few seconds later, whether
    ///      MpFuzz's own cross-peer divergence counter grew — the headline finding this whole
    ///      harness exists to catch, logged unmissably and tagged with the checksum id if so.
    ///   4. Increments outcome.RestoresCommitted.
    ///
    /// Wrapped in try/catch, same as every other fire-and-forget task in this mod (e.g.
    /// TimeoutWatchdog in UndoProtocol.cs): an unhandled exception in a fire-and-forget Task would
    /// otherwise fail silently.
    /// </summary>
    private static async Task WatchCommitsLoopAsync(string role, UndoFuzz.CombatOutcome outcome)
    {
        try
        {
            int lastObservedCommitCount = UndoProtocol.CommitCount;
            while (CombatManager.Instance is { IsInProgress: true })
            {
                await Task.Delay(PollInterval);

                int commitCountNow = UndoProtocol.CommitCount;
                if (commitCountNow != lastObservedCommitCount)
                {
                    lastObservedCommitCount = commitCountNow;
                    uint committedId = UndoProtocol.LastCommittedChecksumId;
                    outcome.RestoresCommitted++;

                    bool fidelityOk = ChecksumHook.LastRestoreFidelityOk;
                    if (!fidelityOk)
                    {
                        outcome.FidelityFailures++;
                        Log.Write($"[MpFuzz] role={role} RESTORE FIDELITY FAILURE — committed "
                            + $"id={committedId} (restore #{outcome.RestoresCommitted} this combat). "
                            + "See the ChecksumHook RESTORE FIDELITY line(s) above for the byte-level diff.");
                    }
                    Log.Write($"[MpFuzz] role={role} restore committed -> id={committedId} "
                        + $"(commitCount={commitCountNow}, fidelityOk={fidelityOk}).");

                    if (ChecksumHook.TryGetSyncPoint(committedId, out var sp))
                        UndoFuzz.NotifyExternalRestore(sp);
                    else
                        Log.Write($"[MpFuzz] role={role} committed id={committedId} but its sync point "
                            + "is no longer resolvable — cannot arm the stuck-after-restore watch for it.");

                    _ = WatchForDivergenceAfterCommitAsync(role, committedId, outcome);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] WatchCommitsLoopAsync ERROR: {ex}");
        }
    }

    /// <summary>
    /// Part C item 2: called (fire-and-forget) by WatchCommitsLoopAsync right after it observes a
    /// new commit on this peer. Snapshots MpFuzz's own _divergenceCount, waits up to
    /// DivergenceWatchAfterCommit, and re-checks — see that constant's own doc comment for why a
    /// bounded wait (rather than an inline check) is needed. If the count grew, this is exactly the
    /// asymmetric-restore bug this entire harness was built to catch (see this file's own top-of-file
    /// "WHY DIVERGENCE DETECTION IS THE POINT" paragraph): logged with heavy emphasis and tagged with
    /// the checksum id that was committed, and recorded into outcome.DivergencesAfterRestore.
    /// </summary>
    private static async Task WatchForDivergenceAfterCommitAsync(string role, uint committedChecksumId, UndoFuzz.CombatOutcome outcome)
    {
        try
        {
            int before = _divergenceCount;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < DivergenceWatchAfterCommit)
            {
                if (_divergenceCount != before)
                {
                    outcome.DivergencesAfterRestore++;
                    Log.Write("[MpFuzz][divergence] ################################################################");
                    Log.Write($"[MpFuzz][divergence] DIVERGENCE AFTER RESTORE — role={role} "
                        + $"committedChecksumId={committedChecksumId} — the two peers disagreed "
                        + "immediately after a committed undo. THIS IS THE HEADLINE FINDING THIS "
                        + $"HARNESS WAS BUILT TO CATCH. detail={_lastDivergenceDetail}");
                    Log.Write("[MpFuzz][divergence] ################################################################");
                    return;
                }
                await Task.Delay(PollInterval);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[MpFuzz] WatchForDivergenceAfterCommitAsync ERROR: {ex.Message}");
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
