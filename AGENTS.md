# STS2 Mods: Codex Working Rules

## Evidence before implementation

- Treat the decompiled game source as the authority for game APIs, reflection
  names, accessors, and multiplayer behavior.  Do not introduce a reflection
  string until it has been verified there.
- Read call sites, not just declarations.  Confirm public setters, all relevant
  branches, and whether a method is a network sender or only a local receiver.
- Distinguish game catalogues (`ModelDb.All*` and `Debug*`) from states the
  game can actually produce.  Fuzzers must use the game's factories and valid
  encounter pools unless a test explicitly documents an intentional fault.
- A crash stack identifies where execution failed; it does not prove that an
  upstream state was legitimate.  Establish a real gameplay path before
  calling it a game defect.

## Scope and changes

- Make the smallest change that addresses the verified bug.  Preserve unrelated
  working-tree changes and do not refactor adjacent code without a separately
  established need.
- Do not decide release scope, publish a release, or commit changes unless the
  user explicitly requests that action.  A temporary local deployment is
  permitted only when needed to verify the requested fix; preserve and restore
  the prior installed build afterward.
- Before declaring a cause fixed, compare a reproducible before/after case.
  State clearly when a conclusion is source-derived but not live-verified.

## Verification standards

- A quiet run is not a passing run.  Prove that the target mechanism executed:
  use counters, explicit log markers, or an injected condition when ordinary
  gameplay does not reliably cover it.
- Identify a test run by its process id and logs, not timestamps alone.  Check
  for stale game processes, port conflicts, and sleep before interpreting a
  result.
- Keep static and runtime evidence separate.  Run the narrow relevant checks:
  `dotnet build -c Release`, `tools/SurfaceCheck`, and a real game/fuzzer run
  when the change affects runtime behavior.
- Do not call an audit exhaustive without recording what was checked, what was
  rejected, and what remains untested.

## Game-instance safety

- Before launching, run `pgrep -fl "Slay the Spire 2"`.  If another instance
  exists, do not launch or terminate it without coordinating with the user.
- Do not use broad `pkill`.  Terminate only a process started by this task,
  using its known pid or unique command-line argument.
- Wrap long-running game tests in `caffeinate -dimsu`.
- ENet port 33771 permits only one multiplayer harness at a time; do not run
  concurrent multiplayer test jobs.
