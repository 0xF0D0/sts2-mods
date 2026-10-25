# Extended runtime validation, 2026-10-25

User authorization: validate the remaining cooperative core, through four real
players. UI layout remains deferred. Keep the five production owners; fixes
must be driven by a reproduced failure. No commit, publication or permanent
installation/configuration change.

## Matrix and gates

1. Generalize the existing native harness to 2/3/4 real processes, unique ENet
   IDs, one job on port 33771 at a time. Preserve the earlier basic scenario.
2. Focused singleplayer native choice UI test: Silent/Survivor when drawn,
   actual choice input, then replay a prefix containing that choice. Original
   choice UI must appear; historical replay must complete without opening it.
3. Extended 2-, 3-, 4-peer runs: native shop-pool Toolbox fixture acquired via
   RelicFactory and RelicCmd before the header, native local-choice selector
   which explicitly sends real reserved choices (the game's LocalSelector
   branch does not do that itself), all four replay event kinds covered.
   Each peer uses a normal factory-created starter deck. No private mod state
   mutation, fake peers or stub transport.
4. For each participant count: serial and concurrent native input; every peer
   can request undo; one rejection; one withheld vote causing real 30s timeout;
   repeated undo and post-undo play; cross-player/enemy-turn replay; compare
   full native state to saved targets and across peers. Record observed counters.
5. Separate fault runs: a real participant disconnects while voting; a real
   participant disappears after Start. Require bounded terminal results and
   no playable partial replay. Native code/connection gates are inspected
   before any injected condition. Delays are cooperative timing conditions,
   not a malicious-input model. Disconnect recovery remains out of scope.
6. Review exit/resource diagnostics; clean up harness-owned run before exit
   where possible. Do not claim leak freedom from one clean shutdown.

No launch before no-process/no-port preflight, game-version/source audit,
settings/profile backup and elimination of known startup modals. Every game is
wrapped in caffeinate -dimsu. Back up all used default local accounts plus prior
installed test mods. Restore only after all task processes have exited and
compare file SHA-256 maps. Keep raw and bounded PID-specific artifacts.

## Evidence and no-go

Build Release with warnings as errors, run MinimalCheck, inspect all native API
call sites. Every run needs PID-bound markers, explicit terminal verdict for
all participating processes, snapshots/checksums and relevant event counts.
Classify stalls before terminating only task-owned PIDs. Record failures before
narrow fixes and repeat the exact failing scenario.

Steam P2P and separate physical machines require distinct authenticated peers;
this local ENet setup cannot establish that evidence. Record this as untested,
not as a passing four-player Steam result. Never call the matrix exhaustive.

## Progress

- Plan recorded; no active game or UDP 33771 owner at preflight.

- Focused choice-ui1, PID 93121: FAIL at exact native state comparison after
  successful replay protocol. Source review found the harness called
  PlayerCmd.EndTurn directly; native NEndTurnButton sends EndPlayerTurnAction
  through ActionQueueSynchronizer.RequestEnqueue. The direct call omitted the
  end-turn action from history. Corrected both focused and extended harness
  paths to the UI sender; production unchanged. Repeat pending.
- Subsequent preflight detected non-harness PID 93266. No termination or new
  launch performed; asked the user to coordinate its shutdown.

- User authorized terminating PID 93266; sent TERM to that exact PID and verified
  subsequent runtime preflight clear.
- choice-ui2 PID 94840 exit 0 PASS: actual discard selection by CUA; replay
  events=5, waits=1, choices=1, checksum=701338407; exact native state matched.
  Screenshot after replay showed normal turn-2 combat and no selection overlay.
- extended2-01 PIDs 95299/95316 FAIL at opening state barrier. Snapshots showed
  client still in PlayerTurnPhase.Start with undrawn hand while Hooks.IsIdle
  returned true. Native CombatManager.RunAutoPrePlayPhase explicitly awaits setup
  before transitioning each player to Play. Added live-player phase gate in
  Hook.IsIdle; extended2-02 passes the same opening barrier and has reached the
  real withheld-vote timeout scenario. No sixth module added.

- extended2-02 PIDs 95570/95583 exit 0 PASS, native divergence callbacks 0,
  each cards=5/undos=3/selections=2. Covered all four event types, serial and
  concurrent input, rejection, real vote timeout, both requesters, exact prefix
  restoration/truncation, no live selector during replay, enemy-turn transition,
  cross-turn replay and further input. Headless ENet; not UI-layout evidence.

- extended3-01 PIDs 95958/95998/95999 and extended3-02 PIDs
  96275/96325/96326 failed at automatic fingerprint vote rejection. Dumped
  native prefixes confirm identical ordered control events and choice results
  with local PlayerChoice arrival interleaved differently (native
  SyncLocalChoice records before sending). Canonical comparison was attempted
  in extended3-03, then the user clarified scope: no automatic rejection apart
  from actual votes. Stopped that obsolete run via harness abort marker; removed
  fingerprint comparison entirely, including its helper and wire field.
  extended3-04 validates the final user-only vote behavior. Prior evidence is
  historical and must not be cited as a final-build pass.

- extended3-04 PIDs 96767/96775/96776 exit 0 PASS on user-only vote build:
  each cards=5/undos=4/selections=2/divergences=0. All three requesters, four
  event kinds, reject/timeout, exact native state/truncation and cross-turn pass.
- extended4-01 PIDs 97164/97175/97176/97177 stopped by user redirection via
  harness abort marker. This is CANCELLED, not a four-player acceptance pass.
  Four-player completion and fault matrix remain unfinished. Prior two-player
  pass predates removal of fingerprint fields and needs final-build regression.
- All four test accounts and both installed mod directories restored using
  runtime.py restore after all processes exited; SHA-256 maps compared. No game
  process remains and UDP 33771 is free. Raw artifacts/cleanup.json retained at
  /var/folders/l9/jlf03xp92jg3zqrjkx02wnq00000gn/T/undosync-extended-lpj0_j6o.
- Existing UndoSync/UndoFuzz.cs and UndoSync/MpFuzz.cs remain untouched. They are
  coupled to V1 capture/restore and have not been ported to UndoSyncMinimal.
  MinimalHarness currently uses starter decks and a Toolbox fixture, not the
  prior broad randomized deck/relic/potion/upgrades driver.


## Promotion into UndoSync

User requested replacing the original UndoSync project with the validated
minimal implementation. Production sources now live under UndoSync's five
module directories, assembly/namespace/mod ID UndoSync. Harness and metadata
checker target that same project. Old randomized fuzz drivers are preserved
under tools/LegacyFuzz, uncompiled and unported. Historical artifact identities
remain unchanged. Previous interactive fixture accounts/mods and manual Steam
settings were restored before this migration; no game was running.
