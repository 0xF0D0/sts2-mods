# Native overlapping lifecycle regressions

`categories.py` covers the six coarse hook/input categories. This companion
checks when reconstruction begins while native work or another peer's choice
is still in progress. It uses the actual game and ENet, not a mock replay model.

```sh
python3 tools/MinimalHarness/mixed.py --output /absolute/new/evidence-directory
```

An existing output path, game process or occupied ENet port rejects startup.
The default builds core/harness and checks native surfaces. `--no-build` is only
for a pinned, already built pair. All jobs are sequential, under caffeinate,
with primary + independent ZIP backups and exact mod/test-profile restoration.
Any failure stops the batch and preserves PID-bound logs and its terminal result.

| Case | Native overlap | Mandatory evidence |
| --- | --- | --- |
| lifecycle-sp / lifecycle-mp3 | Undo with a first card already in hand while the turn-start draw task remains pending | Real native pending Draw, canceled combat before scene removal, old loop terminal outcome, no detached view access; three repeated restores, input and next turn |
| enemy-sp / enemy-mp3 | Undo while a genuine enemy TakeTurn task is pending | Real enemy task/current side at trigger; same teardown and target checks |
| redraw-sp / redraw-mp3 | Gambling Chip selects and discards all five cards, queues potion input before confirmation, then shuffles/redraws | Actual live/replayed DiscardAndDraw and Shuffle counters; exact enqueue/resume traces, full targets and hand model bindings |
| transform-sp / transform-mp3 | Nonempty Gambling Chip redraw/shuffle and queued potion, followed by seven native Fool Ecstasy/Vitriol turns | All startup mechanisms present in every replay; actual generated Joy plays, card transforms/IDs, exact turn-8 target; continue to turn 9 |
| skew-mp3 | Fool + Ironclad + Silent; one player still choosing, two finished, third player's card started before the proposal and remains pending at final vote | Native unfinished action observed at final vote, actual partly completed choice screens, all three proposers, earlier/later target alternation, exact restored state/inventory and next turn |

This is nine fixed cases, not nine new gameplay categories. Singleplayer has
its native one-person request path; multiplayer has three actual processes.
Read-only observers never replace tasks, insert pauses, fake players/results,
write production private state or force game RNG/HP/card IDs. Timed cases use
the production Request/Vote API after an actual native task is observed; hand
and potion selections are completed with viewport input and native callbacks.
Normal speed is used for race cases to keep genuine work pending; the
reconstruction itself retains normal production acceleration. Other cases use
native Instant mode. A missing timing mechanism is not a passing test.

A deterministic baseline reproduces the v0.1.21 outgoing-card-view error.
Its terminal expected-defect result is separate from ordinary fixed-case PASS.
See `results/2026-11-25/teardown-mixed/` for the before/after evidence. Run
`categories.py` separately after these focused cases for full category integration.
Actual Steam/P2P, every card/character/mode and every frame ordering are outside
these bounded local runs. No release occurs by running this command.
