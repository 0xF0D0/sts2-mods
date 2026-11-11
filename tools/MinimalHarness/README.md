# Native UndoSync verification

Run commands from the repository root. The runner requires no game already
running and backs up/restores local mod/settings files with hash verification.
Do not launch another game while a batch owns the installation. Artifacts are
printed as `ARTIFACT_ROOT`; failures are retained and stop the batch.

Fast content fuzzing (one actual native game process, no multiplayer claim):

```sh
python3 tools/MinimalHarness/fuzz.py run --content-seed CONTENT-A --schedule-seed ACTION-A --cases 20
```

This defaults to headless + the game's `Instant` setting. Native `Cmd.Wait` skips
animation waits; TestMode stays off. Current measured single case including
startup, UI proposal, nonempty replay, exact state comparison and continued play
was 11.561 seconds. This is not a fixed throughput guarantee; native startup and
run reconstruction remain. Every case currently starts a fresh process.

Real local ENet phase regressions (separate evidence):

```sh
python3 tools/MinimalHarness/fuzz.py run --transport enet --peers 3 --phases --content-seed CONTENT-B --schedule-seed PHASE-A
python3 tools/MinimalHarness/fuzz.py run --transport enet --peers 4 --phases --content-seed CONTENT-C --schedule-seed PHASE-B
```

The host resolves fixture counts/model IDs; all peers apply checked native
factory/acquisition calls before the combat header. Encounters come from the
native first-act generated Monster sequence. No arbitrary catalog states or
manual enchantment pairing. Factory failure is FIXTURE_ERROR, never a successful
content case. Supplied potions are present in state; this initial driver does
not claim potion-use, Elite/Boss, every character or full-campaign coverage.

Each proposer rotates through actual viewport picker input. Voters use viewport
mouse input and the native callback. `--phases` holds a real native card task
until the last busy voter has sent Accept and host has entered Preparing; then
it releases immediately. It also varies vote order/frame delays, holds one
replay until all other peers reach Done, and declares one missing-recorder fault
per round. No fixed eight-second sleeps, replacement votes or test checksums.
Each condition needs an observed counter, not just a requested schedule.

Content and schedule seeds are separate. A trace records resolved actions,
choice indices/encodings, target digests and phase schedules. Reproduce saved
choices without resampling:

```sh
python3 tools/MinimalHarness/fuzz.py repro --trace /absolute/artifact/case-directory
```

Use `--repeat 2` to check byte-identical fixture and per-peer action traces.
`--speed normal` requests a separate ordinary-speed regression. Native ENet
reconstruction can retry if a client reaches the host before its replacement
listener opens. This dominated a measured Instant run (59.790s vs 45.735s normal
for the same three-peer trace), so animation acceleration is not reported as a
guaranteed multiplayer speedup. The retry remains visible and unmodified.

`verify_run.py` checks PID/role/build identity, native transport, real restore
markers, all-peer final checksums, target state checks, continued input and
requested phase/input coverage. Exit zero alone cannot pass. Evidence is bound
to the exact DLL hashes and game version. `tools/MinimalCheck` is applicable to
the minimal core; the older `tools/SurfaceCheck` still requires removed snapshot
implementation files and currently exits 2.

Steam worker/coordinator enrollment and real Steam execution remain the next
milestone. One PC/account cannot establish three distinct authenticated Steam
peers. ENet/singleplayer reports must never be called Steam verification. No
release, commit or Workshop update is performed by these runners.

Control delivery/lifetime regressions use the same real native ENet peers. Each
test deliberately faults an actual native await or serialized send boundary;
there are no substitute votes, participants, transports or state checksums.
Build `tools/MinimalHarness/MinimalHarness.csproj -c Release`, then prepare the
backup once and restore it after all owned game processes have exited:

```sh
python3 tools/MinimalHarness/runtime.py prepare
python3 tools/MinimalHarness/runtime.py run --root /absolute/backup-root --label control-matrix --peers 3 --suite fault-send-matrix --speed instant
python3 tools/MinimalHarness/runtime.py run --root /absolute/backup-root --label discarded-effect --peers 3 --suite fault-old-action --speed normal
python3 tools/MinimalHarness/runtime.py run --root /absolute/backup-root --label peer-disconnect --peers 3 --suite fault-disconnect --speed instant
python3 tools/MinimalHarness/runtime.py run --root /absolute/backup-root --label peer-error --peers 3 --suite fault-replay-error --speed instant
python3 tools/MinimalHarness/runtime.py run --root /absolute/backup-root --label knowledge-choice --peers 3 --suite fault-knowledge-choice --speed instant --visible
python3 tools/MinimalHarness/verify_run.py /absolute/backup-root/knowledge-choice --require-input viewport
python3 tools/MinimalHarness/runtime.py restore --root /absolute/backup-root
```

`fault-send-matrix` runs 14 undos in ONE three-peer session: drop Vote, Ready,
StartReceived, Done, Resumed, Request, upload Plan, Propose, relay Plan, Prepare,
Start, Resume, Release and an interval of ACKs. Require exact target states,
all-peer final comparison, no repeated reconstruction, drained ACK queues,
continued native input and a fault marker for every case. Zero-target matrix
cases are complemented by nonempty replay in the other suites/regression.
`fault-old-action` holds a genuine attack inside a native presentation await,
not after the effect body. `fault-disconnect` uses graceful native disconnect so
the host actually receives the disconnect callback. `now:true` on this native
ENet version can lose the close notification; do not treat that as a test of
callback handling. `fault-replay-error` reports a held executor to the host,
then raises a native action exception and drops the first Cancel; all peers must
terminate with the original cause. A fixture failure stops the CLI with exit 1.
`--expect baseline` is for deliberate before-fix reproduction only: PASS then
means the defect was witnessed and task-owned cleanup completed, not recovery.
These transport faults reproduce the log-only send contract locally. They do
not verify an actual Steam SDK rejection or authenticated remote Steam peers.

`fault-knowledge-choice` enters the native Hive Knowledge Demon boss encounter,
ends the player turn, selects the host's curse with viewport input, and leaves
the other native selection screens open. Undo must finish the discarded enemy
turn, restore the exact earlier state, reach checksum agreement and allow a
continued card play. The baseline reproduction uses `--minimal-expect=baseline`
through `runtime.run(extra_args=...)` and records the old turn still waiting
before task-owned cancellation. Fixed runs record rendered choice screenshots.
The `verify_run.py` command checks target state, native input and peer checksums.
To also audit connection lifetime, use `runtime.run(retained=True)`.
`--knowledge-chooser none` leaves every curse screen open. `--knowledge-chooser
client` completes only the client's choice before the host requests undo.

`fault-turn-choice --choice-card TOOLS_OF_THE_TRADE` acquires the card through
the native merchant factory, plays it and ends the turn to reach a real pending
selection. Other fixtures are `FOREGONE_CONCLUSION`, `TYRANNY`, `ENTROPY` and
`STRATAGEM`. They exercise hand, draw-pile and shuffle choices. The Entropy
fixture reveals Colorless1 through the game's unlock API before joining so
every temporary test account can legally acquire it. The runtime restores the
original test account progress afterward.
