# PeerView

Read-only **spectate mode** for multiplayer combat. Click another player's
character and the bottom hand UI becomes their hand; the energy orb and pile
counters show their values — the same screen grammar as playing yourself.

## Usage

- In combat, **click another player's character** to enter spectate mode:
  - The bottom hand is replaced by that player's actual hand (hover to zoom,
    live-updating as they draw and play)
  - Every "yours" readout on screen switches to theirs: energy orb, stars,
    draw/discard/exhaust counters (the exhaust button pops in/out based on
    *their* exhaust pile), HP, gold, deck count, potions, relics, and the
    character portrait — hovering a potion slot describes *their* potion
  - A "Spectating — name" banner shows at the top center of the screen
    (Korean strings when the game language is Korean), and the end-turn
    button is hidden until you exit
- While spectating:
  - Click a pile button or use the game's view hotkeys → that player's pile
    (the draw pile is displayed sorted, order hidden — same as vanilla)
  - The top-bar deck button or deck hotkey → that player's master deck
  - Pressing the same key/button again toggles the screen closed (the hand
    strip hides while a pile/deck screen is open)
  - When they hit a card-selection screen — a card-generating potion, Survivor,
    anything that makes them pick — the same screen appears on your side,
    read-only, captioned with who is choosing. It closes itself once they
    pick; Esc closes it without leaving spectate.
- Exit: the back button on the left edge, Esc/back, clicking the character
  again, or automatically on combat end — your own hand and indicators are
  restored.
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
- **Hand strip anchor**: the strip's baseline Y comes from the hand container's
  global position *minus the hand node's own local offset*. Ending your turn
  makes `NPlayerHand.AnimDisable` tween the whole hand node down to
  `_disablePosition` (0, 100), so reading the live position would pin the
  replica 100px low for the rest of the turn; removing the node's offset yields
  the resting anchor, correct mid-tween as well.
- **Top-bar readouts**: gold, HP and deck count are label-stamped from the
  viewed player and re-stamped by postfixes on the vanilla refresh methods
  (`UpdateGold`, `UpdateHealth`, `OnPileContentsChanged`) so local updates
  can't flash local values back in. Gold is *not* done with a `_player` swap:
  `UpdateGoldAnim` drives a "+N" popup off `_currentGold` bookkeeping, and
  swapping would fake a delta animation — while spectating `UpdateGold` is
  skipped outright and its `_currentGold`/`_additionalGold` ledger written
  directly, so a local gold change can't flash your own number mid-animation
  and leaving spectate still shows the right total. Stars reuse vanilla's own
  `SetStarCountText` (keeping its 0-star red and shader hues) and need a
  `_Process` postfix, because that method repaints from the local player every
  frame regardless of events.
- **Potions, relics and portrait**: node collections, so they can't be
  label-stamped — the vanilla nodes are hidden and read-only replicas drawn in
  their place. Hiding is always `Modulate = Colors.Transparent`, never
  `Visible = false`: the top bar is a Container, and collapsing a slot reflows
  the whole row (the belt once landed next to the gold counter that way). The
  belt is redrawn on a `TopLevel` overlay — which a Container's layout pass
  skips — with the viewed player's slot count, squeezed into the real belt's
  span when they carry more slots than you so it cannot spill onto the room
  icon. Replicas are real `NPotionHolder.Create(isUsable: false)` holders fed
  through `AddPotion`, with vanilla's own `(-30, -30)` potion offset from
  `NPotionContainer.Add` (without it the artwork renders outside its frame);
  going through real holders is what makes hover behave like the real belt, and
  a Prefix on `NPotionHolder.OnFocus` points the tooltip at the peer's potion.
  Unlike NCard, none of these are pooled.
- **Peer card-selection mirror**: `CardSelectCmd.From*` take the candidate list
  as an argument and remote peers resolve the result by *index* into it, which
  proves every peer holds the same list in the same order — so the mirror is
  drawn from local data with no network involvement. Prefixes record the
  pending choice per player (so entering spectate mid-choice still shows it);
  the vanilla screen is shown read-only with a full-rect `MouseFilter.Stop`
  child and `FocusMode = None` swept over every descendant. It must be closed
  through `NOverlayStack.Remove` — that is what recalculates the stack's shared
  input-blocking backstop and frees the node; a direct `QueueFree` would strand
  the backstop over the screen. Closing is driven by
  `PlayerChoiceSynchronizer.PlayerChoiceReceived`, with a continuation on the
  original `Task` as the safety net for choices that end without one.
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
