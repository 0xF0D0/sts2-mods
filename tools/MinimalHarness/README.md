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
