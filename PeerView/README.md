# PeerView

Read-only **spectate mode** for multiplayer combat. Click another player's
character and the bottom hand UI becomes their hand; the energy orb and pile
counters show their values — the same screen grammar as playing yourself.

## Usage

- In combat, **click another player's character** to enter spectate mode:
  - The bottom hand is replaced by that player's actual hand (hover to zoom,
    live-updating as they draw and play)
  - The energy orb and draw/discard/exhaust counters switch to their values
    (the exhaust button pops in/out based on *their* exhaust pile)
  - A "Spectating — name" banner shows at the top center of the screen
    (Korean strings when the game language is Korean)
- While spectating:
  - Click a pile button or use the game's view hotkeys → that player's pile
    (the draw pile is displayed sorted, order hidden — same as vanilla)
  - The top-bar deck button or deck hotkey → that player's master deck
  - Pressing the same key/button again toggles the screen closed (the hand
    strip hides while a pile/deck screen is open)
- Exit: Esc/back, clicking the character again, or automatically on combat
  end — your own hand and indicators are restored.
- Clicking your own character does nothing (spectate targets are peers only).

## Why this is safe

Multiplayer runs deterministic lockstep — every peer executes every action
locally — so **other players' card data already exists in your client's
`CombatState`, in real time**. This mod only draws that data:

- No GameActions, no RNG consumption, no synchronized state touched → desync
  is impossible.
- No network messages.
- `affects_gameplay: false` → excluded from the handshake mod-match check, so
  **only the viewer needs it installed**.

## Implementation notes

- **Hand strip**: `NCard.Create(model)` (the same API the game uses to render
  peers' played cards) + the vanilla `HandPosHelper` fan tables reproduce the
  real `NPlayerHand` layout and hover behavior. The real hand is only hidden
  (`Visible = false`), never mutated. `UpdateVisuals` must run **after** the
  node enters the tree (otherwise it renders the "Broken Card" fallback), and
  NCard draws centered on its own origin. NCards are pooled via `NodePool` —
  release them with `QueueFreeSafely`.
- **Energy orb**: prefix/postfix on `NEnergyCounter.RefreshLabel` swaps
  `_player` to the viewed player only for the duration of the call, so the
  vanilla color/material logic runs untouched against their state. Only the
  local counter (`LocalContext.IsMe`) is swapped.
- **Pile counters**: labels are repainted from the viewed player's piles
  (subscribed via `ContentsChanged`); `AddCard`/`RemoveCard` postfixes stop
  local pile animations from flashing local counts back in. Restored from the
  vanilla `_currentCount` bookkeeping on exit.
- **Click-to-spectate**: `NCreature._Ready` postfix connects `GuiInput` on
  player creature hitboxes. Guards: self excluded (`LocalContext.IsMe`), an
  active card play (`NPlayerHand.InCardPlay` — normal card targeting does NOT
  go through NTargetManager, so this is mandatory), card selection mode,
  targeting selection, capstone open. The release that finishes targeting is
  ignored via `LastTargetingFinishedFrame`.
- **Key routing**: `NGame._Input` prefix — the `_input` phase precedes
  NHotkeyManager's `_UnhandledInput`, so consuming view-action press AND
  release events prevents the vanilla pile buttons (release-triggered) from
  hijacking the screen. Esc is matched by raw keycode (neither `ui_cancel`
  nor `mega_pause_and_back` fires for Escape in combat, and raw letter keys
  are unreliable under the Korean IME — Escape is not).
- Logs: `<godot-user-data>/logs/PeerView-<pid>.log`

## Build & deploy

```bash
dotnet build -c Release
# copy PeerView.json + PeerView.dll into <game>/SlayTheSpire2.app/Contents/MacOS/mods/PeerView/
```

Validate patch targets after a game update:

```bash
cd ../tools/SurfaceCheck && dotnet run -- check --mod ../../PeerView
```
