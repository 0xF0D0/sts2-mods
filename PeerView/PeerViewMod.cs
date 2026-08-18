using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace PeerView;

[ModInitializer("Initialize")]
public static class PeerViewMod
{
    public static void Initialize()
    {
        var harmony = new Harmony("com.beomsu.peerview");
        harmony.PatchAll(typeof(PeerViewMod).Assembly);
        Log.Write("PeerView initialized");
    }
}

internal static class Log
{
    // Per-process log file: two local fastmp instances share the same user data dir.
    private static readonly string LogPath = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", $"PeerView-{System.Environment.ProcessId}.log");

    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath,
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] === Log cleared (new session) ==={System.Environment.NewLine}");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath,
                $"[{System.DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>
/// Spectate mode: hides the local NPlayerHand and shows a read-only replica of the
/// viewed player's hand in the same spot, using the game's own card visuals
/// (NCard.Create — the same API NMultiplayerCardIntent uses to render peers' cards)
/// and the vanilla fan layout tables (HandPosHelper). While active, the vanilla
/// energy orb and draw/discard/exhaust counters show the viewed player's values
/// (see PeerIndicators). Pure UI: no game state is touched.
/// </summary>
internal static class PeerSpectate
{
    private sealed class StripCard
    {
        public required Control Box;
        public required NCard Card;
    }

    private static Player? _peer;
    private static Control? _root;
    private static Control? _cardLayer;
    private static Label? _header;
    private static NPlayerHand? _hiddenHand;
    private static NEndTurnButton? _hiddenEndTurn;
    private static readonly List<StripCard> _cards = new();
    private static int _hovered = -1;
    private static bool _rebuildQueued;
    private static bool _indicatorRefreshQueued;

    private static CardPile? _subscribedHand;
    private static CardPile? _subscribedDraw;
    private static CardPile? _subscribedDiscard;
    private static CardPile? _subscribedExhaust;
    private static PlayerCombatState? _subscribedState;
    private static bool _combatEndSubscribed;
    private static bool _capstoneClosedSubscribed;

    internal static bool Active => _peer != null && _root != null && GodotObject.IsInstanceValid(_root) && _root.IsInsideTree();

    internal static Player? Peer => Active ? _peer : null;

    internal static void Enter(Player peer)
    {
        Exit();
        var room = NCombatRoom.Instance;
        var hand = room?.Ui?.Hand;
        var combatState = peer.PlayerCombatState;
        if (room == null || hand == null || combatState == null)
            return;

        _peer = peer;
        _hiddenHand = hand;
        hand.Visible = false;
        // Ending your own turn is not something you do while watching someone
        // else's hand — hide the button (its position/state machine keeps running
        // underneath; Visible is orthogonal to its tweens).
        _hiddenEndTurn = room.Ui.EndTurnButton;
        if (_hiddenEndTurn != null && GodotObject.IsInstanceValid(_hiddenEndTurn))
            _hiddenEndTurn.Visible = false;

        _root = new Control { Name = "PeerViewStrip", MouseFilter = Control.MouseFilterEnum.Ignore };
        hand.GetParent().AddChildSafely(_root);
        Rect2 visible = hand.GetViewport().GetVisibleRect();
        _cardLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChildSafely(_cardLayer);
        // The fan tables are symmetric around x=0, so anchor the layer at the exact
        // horizontal center of the canvas; the vanilla card container only supplies
        // the hand's baseline height (its own x sits slightly off-center).
        _cardLayer.GlobalPosition = new Vector2(
            visible.Position.X + visible.Size.X / 2f,
            hand.CardHolderContainer.GlobalPosition.Y);

        // Spectate banner: top-center of the screen, clear of the battlefield.
        _header = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(900f, 44f),
            Size = new Vector2(900f, 44f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = PeerScreens.IsKorean
                ? $"관전 중 — {PeerScreens.PlayerName(peer)}"
                : $"Spectating — {PeerScreens.PlayerName(peer)}",
        };
        _header.AddThemeFontSizeOverride("font_size", 30);
        _header.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        _header.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        _header.AddThemeConstantOverride("outline_size", 10);
        _root.AddChildSafely(_header);
        _header.GlobalPosition = new Vector2(
            visible.Position.X + visible.Size.X / 2f - _header.Size.X / 2f,
            visible.Position.Y + 150f);

        AttachExitButton(visible);

        if (NCapstoneContainer.Instance != null)
        {
            NCapstoneContainer.Instance.CapstoneClosed += OnCapstoneClosed;
            _capstoneClosedSubscribed = true;
        }

        _subscribedHand = combatState.Hand;
        _subscribedHand.ContentsChanged += OnHandContentsChanged;
        _subscribedDraw = combatState.DrawPile;
        _subscribedDraw.ContentsChanged += OnPeerPileCountsChanged;
        _subscribedDiscard = combatState.DiscardPile;
        _subscribedDiscard.ContentsChanged += OnPeerPileCountsChanged;
        _subscribedExhaust = combatState.ExhaustPile;
        _subscribedExhaust.ContentsChanged += OnPeerPileCountsChanged;
        _subscribedState = combatState;
        _subscribedState.EnergyChanged += OnEnergyChanged;
        if (CombatManager.Instance != null)
        {
            CombatManager.Instance.CombatEnded += OnCombatEnded;
            _combatEndSubscribed = true;
        }

        Rebuild();
        PeerIndicators.ApplyAll();
        PeerIndicators.RefreshEnergyCounters();
        Log.Write($"spectate enter: {PeerScreens.PlayerName(peer)} (netId={peer.NetId}, {combatState.Hand.Cards.Count} cards)");
    }

    internal static void Exit()
    {
        bool wasActive = _peer != null;
        if (_subscribedHand != null)
        {
            _subscribedHand.ContentsChanged -= OnHandContentsChanged;
            _subscribedHand = null;
        }
        if (_subscribedDraw != null)
        {
            _subscribedDraw.ContentsChanged -= OnPeerPileCountsChanged;
            _subscribedDraw = null;
        }
        if (_subscribedDiscard != null)
        {
            _subscribedDiscard.ContentsChanged -= OnPeerPileCountsChanged;
            _subscribedDiscard = null;
        }
        if (_subscribedExhaust != null)
        {
            _subscribedExhaust.ContentsChanged -= OnPeerPileCountsChanged;
            _subscribedExhaust = null;
        }
        if (_subscribedState != null)
        {
            _subscribedState.EnergyChanged -= OnEnergyChanged;
            _subscribedState = null;
        }
        if (_combatEndSubscribed)
        {
            if (CombatManager.Instance != null)
                CombatManager.Instance.CombatEnded -= OnCombatEnded;
            _combatEndSubscribed = false;
        }
        if (_capstoneClosedSubscribed)
        {
            if (NCapstoneContainer.Instance != null)
                NCapstoneContainer.Instance.CapstoneClosed -= OnCapstoneClosed;
            _capstoneClosedSubscribed = false;
        }

        ClearCards();
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.QueueFreeSafelyNoPool();
        _root = null;
        _cardLayer = null;
        _header = null;

        if (_hiddenEndTurn != null && GodotObject.IsInstanceValid(_hiddenEndTurn))
            _hiddenEndTurn.Visible = true;
        _hiddenEndTurn = null;

        if (_hiddenHand != null && GodotObject.IsInstanceValid(_hiddenHand))
        {
            _hiddenHand.Visible = true;
            // Self-heal any layout state that went stale while the hand was hidden
            // (public wrapper around the vanilla RefreshLayout).
            try
            {
                _hiddenHand.ForceRefreshCardIndices();
            }
            catch (System.Exception e)
            {
                Log.Write($"hand refresh error: {e}");
            }
        }
        _hiddenHand = null;

        ulong exitedNetId = _peer?.NetId ?? 0;
        _peer = null;
        _hovered = -1;

        if (wasActive)
        {
            // With _peer cleared the display patches are inert again, so these calls
            // repaint the vanilla indicators with the local player's values.
            PeerIndicators.RestoreAll();
            PeerIndicators.RefreshEnergyCounters();
            Log.Write($"spectate exit: netId={exitedNetId}");
        }
    }

    /// <summary>
    /// The same back button the vanilla pile/deck screens use, borrowed by
    /// instantiating the card-pile-screen scene and detaching its BackButton node
    /// (there is no standalone back-button scene). Clicking it exits spectate.
    /// It lives under _root, so it hides with the strip while a capstone is open
    /// and is freed with the strip on exit.
    /// </summary>
    private static void AttachExitButton(Rect2 visible)
    {
        try
        {
            string scenePath = SceneHelper.GetScenePath("/screens/card_pile_screen");
            var donor = PreloadManager.Cache.GetScene(scenePath).Instantiate<Node>();
            var back = donor.GetNodeOrNull<NButton>("BackButton");
            if (back == null)
            {
                donor.QueueFreeSafelyNoPool();
                Log.Write("exit button: BackButton not found in donor scene");
                return;
            }
            donor.RemoveChild(back);
            donor.QueueFreeSafelyNoPool();
            _root!.AddChildSafely(back);
            back.GlobalPosition = new Vector2(
                visible.Position.X,
                visible.Position.Y + visible.Size.Y * 0.58f);
            back.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Exit()));
            back.Enable();
        }
        catch (System.Exception e)
        {
            Log.Write($"exit button error: {e}");
        }
    }

    private static void ClearCards()
    {
        foreach (var entry in _cards)
        {
            // NCard is pooled: QueueFreeSafely routes it through NodePool.Free so its
            // OnFreedToPool cleanup runs. The box is ours and can be freed normally.
            if (GodotObject.IsInstanceValid(entry.Card))
                entry.Card.QueueFreeSafely();
            if (GodotObject.IsInstanceValid(entry.Box))
                entry.Box.QueueFreeSafelyNoPool();
        }
        _cards.Clear();
        _hovered = -1;
    }

    private static void OnHandContentsChanged()
    {
        // Piles mutate several times within one action; coalesce to one rebuild.
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Callable.From(() =>
        {
            _rebuildQueued = false;
            if (!Active)
                return;
            Rebuild();
        }).CallDeferred();
    }

    private static void OnPeerPileCountsChanged()
    {
        if (_indicatorRefreshQueued)
            return;
        _indicatorRefreshQueued = true;
        Callable.From(() =>
        {
            _indicatorRefreshQueued = false;
            if (!Active)
                return;
            PeerIndicators.ApplyAll();
        }).CallDeferred();
    }

    private static void OnEnergyChanged(int _, int __) => PeerIndicators.RefreshEnergyCounters();

    private static void OnCombatEnded(CombatRoom _) => Exit();

    private static void OnCapstoneClosed() => SetStripVisible(true);

    /// <summary>
    /// The strip draws above capstone screens (it lives near the top of the combat
    /// UI tree), so it hides while any capstone is open — see PatchCapstoneOpen.
    /// </summary>
    internal static void SetStripVisible(bool visible)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.Visible = visible;
    }

    private static void Rebuild()
    {
        if (!Active || _peer?.PlayerCombatState == null || _cardLayer == null)
            return;
        ClearCards();
        foreach (CardModel model in _peer.PlayerCombatState.Hand.Cards)
        {
            var nCard = NCard.Create(model);
            if (nCard == null)
                continue;

            var box = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            _cardLayer.AddChildSafely(box);
            box.AddChildSafely(nCard);
            // Must run with the node inside the tree — NCard renders its "Broken
            // Card" fallback face if visuals are refreshed before it is ready
            // (vanilla NMultiplayerCardIntent gates this call on IsNodeReady too).
            nCard.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            Vector2 cardSize = nCard.Size;
            if (cardSize.X < 1f || cardSize.Y < 1f)
                cardSize = new Vector2(256f, 380f);
            box.Size = cardSize;
            box.PivotOffset = cardSize / 2f;
            // NCard draws centered on its own origin, so park that origin at the
            // box center to make the visual fill the box rect.
            nCard.Position = cardSize / 2f;

            // Invisible top-most sibling that owns mouse hover for this card, so we
            // never have to touch the pooled NCard's own input configuration.
            var sensor = new Control { Size = cardSize, MouseFilter = Control.MouseFilterEnum.Stop };
            box.AddChildSafely(sensor);
            int index = _cards.Count;
            sensor.MouseEntered += () => OnCardHover(index, hovered: true);
            sensor.MouseExited += () => OnCardHover(index, hovered: false);

            _cards.Add(new StripCard { Box = box, Card = nCard });
        }
        ApplyLayout();
    }

    private static void OnCardHover(int index, bool hovered)
    {
        if (!Active)
            return;
        if (hovered)
            _hovered = index;
        else if (_hovered == index)
            _hovered = -1;
        ApplyLayout();
    }

    // Mirrors NPlayerHand.RefreshLayout: vanilla fan tables, hovered card raised to
    // full scale, neighbors pushed away with the same falloff.
    private static void ApplyLayout()
    {
        int count = _cards.Count;
        if (count == 0)
            return;
        Vector2 scale = SafeScale(count);
        for (int i = 0; i < count; i++)
        {
            Control box = _cards[i].Box;
            if (!GodotObject.IsInstanceValid(box))
                continue;
            Vector2 pos = SafePosition(count, i);
            if (_hovered > -1 && _hovered != i)
            {
                float shift = Mathf.Lerp(100f, 0f, Mathf.Min(1f, (float)Mathf.Abs(_hovered - i) / 4f));
                pos += Vector2.Left * Mathf.Sign(_hovered - i) * shift;
            }
            if (i == _hovered)
            {
                box.RotationDegrees = 0f;
                box.Scale = Vector2.One;
                pos.Y = (0f - box.Size.Y) * 0.5f + 2f;
                box.ZIndex = 100;
            }
            else
            {
                box.RotationDegrees = SafeAngle(count, i);
                box.Scale = scale;
                box.ZIndex = i;
            }
            box.Position = pos - box.PivotOffset;
        }
    }

    // HandPosHelper's tables stop at 10 cards; spread evenly past that.
    private static Vector2 SafePosition(int n, int i)
    {
        if (n <= 10)
            return HandPosHelper.GetPosition(n, i);
        float x = -550f + 1100f * i / (n - 1);
        return new Vector2(x, 0f);
    }

    private static float SafeAngle(int n, int i) => n <= 10 ? HandPosHelper.GetAngle(n, i) : 0f;

    private static Vector2 SafeScale(int n) => n <= 10 ? HandPosHelper.GetScale(n) : Vector2.One * 0.6f;
}

/// <summary>
/// While spectating, the vanilla combat indicators show the viewed player's values
/// instead of duplicating them in custom UI: the energy orb (via the RefreshLabel
/// player swap patch below) and the draw/discard/exhaust pile counters (labels
/// repainted here; the AddCard/RemoveCard postfixes keep local pile animations from
/// flashing local counts back in).
/// </summary>
internal static class PeerIndicators
{
    private static readonly System.Reflection.FieldInfo CountLabelField =
        AccessTools.Field(typeof(NCombatCardPile), "_countLabel");

    private static readonly System.Reflection.FieldInfo CurrentCountField =
        AccessTools.Field(typeof(NCombatCardPile), "_currentCount");

    internal static PileType? PileTypeOf(NCombatCardPile button) => button switch
    {
        NDrawPileButton => PileType.Draw,
        NDiscardPileButton => PileType.Discard,
        NExhaustPileButton => PileType.Exhaust,
        _ => null,
    };

    private static CardPile? PeerPileFor(NCombatCardPile button)
    {
        var state = PeerSpectate.Peer?.PlayerCombatState;
        if (state == null)
            return null;
        return PileTypeOf(button) switch
        {
            PileType.Draw => state.DrawPile,
            PileType.Discard => state.DiscardPile,
            PileType.Exhaust => state.ExhaustPile,
            _ => null,
        };
    }

    internal static void ApplyPeerCount(NCombatCardPile button)
    {
        try
        {
            CardPile? pile = PeerPileFor(button);
            if (pile == null || !GodotObject.IsInstanceValid(button))
                return;
            if (CountLabelField.GetValue(button) is MegaLabel label && GodotObject.IsInstanceValid(label))
                label.SetTextAutoSize(pile.Cards.Count.ToString());
        }
        catch (System.Exception e)
        {
            Log.Write($"indicator apply error: {e}");
        }
    }

    internal static void ApplyAll()
    {
        var ui = NCombatRoom.Instance?.Ui;
        if (ui == null)
            return;
        ApplyPeerCount(ui.DrawPile);
        ApplyPeerCount(ui.DiscardPile);
        ApplyPeerCount(ui.ExhaustPile);
        // The exhaust button only pops in once a card lands in the pile — mirror
        // that against the VIEWED player's pile while spectating (a peer with
        // exhausted cards should show the button even if ours is empty).
        var peerExhaust = PeerSpectate.Peer?.PlayerCombatState?.ExhaustPile;
        if (peerExhaust != null)
            SetExhaustVisible(ui.ExhaustPile, peerExhaust.Cards.Count > 0 || LocalCount(ui.ExhaustPile) > 0);
    }

    internal static void RestoreAll()
    {
        var ui = NCombatRoom.Instance?.Ui;
        if (ui == null)
            return;
        foreach (NCombatCardPile button in new NCombatCardPile[] { ui.DrawPile, ui.DiscardPile, ui.ExhaustPile })
        {
            try
            {
                if (!GodotObject.IsInstanceValid(button))
                    continue;
                // _currentCount is the vanilla bookkeeping for the LOCAL pile — its
                // subscriptions kept running while we painted peer values on top.
                if (CountLabelField.GetValue(button) is MegaLabel label
                    && GodotObject.IsInstanceValid(label)
                    && CurrentCountField.GetValue(button) is int localCount)
                    label.SetTextAutoSize(localCount.ToString());
            }
            catch (System.Exception e)
            {
                Log.Write($"indicator restore error: {e}");
            }
        }
        SetExhaustVisible(ui.ExhaustPile, LocalCount(ui.ExhaustPile) > 0);
    }

    private static int LocalCount(NCombatCardPile button)
    {
        try
        {
            if (GodotObject.IsInstanceValid(button) && CurrentCountField.GetValue(button) is int count)
                return count;
        }
        catch { }
        return 0;
    }

    private static void SetExhaustVisible(NExhaustPileButton button, bool visible)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(button) || button.Visible == visible)
                return;
            if (visible)
            {
                // The same pop-in path vanilla uses when the first card is exhausted.
                button.AnimIn();
                button.Enable();
            }
            else
            {
                button.Visible = false;
            }
        }
        catch (System.Exception e)
        {
            Log.Write($"exhaust visibility error: {e}");
        }
    }

    /// <summary>
    /// Re-runs the vanilla energy label refresh; with spectate active the RefreshLabel
    /// patch feeds it the viewed player, otherwise the local player.
    /// </summary>
    internal static void RefreshEnergyCounters()
    {
        var container = NCombatRoom.Instance?.Ui?.EnergyCounterContainer;
        if (container == null || !GodotObject.IsInstanceValid(container))
            return;
        foreach (NEnergyCounter counter in EnumerateEnergyCounters(container))
            counter.Call("RefreshLabel");
    }

    private static IEnumerable<NEnergyCounter> EnumerateEnergyCounters(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is NEnergyCounter counter)
                yield return counter;
            foreach (NEnergyCounter nested in EnumerateEnergyCounters(child))
                yield return nested;
        }
    }
}

/// <summary>
/// While spectating, the energy orb renders the viewed player's energy: the local
/// player field is swapped in just for the duration of the vanilla RefreshLabel
/// call, so its color/material logic runs untouched against the peer's state.
/// Only the local player's own counter is swapped.
/// </summary>
[HarmonyPatch(typeof(NEnergyCounter), "RefreshLabel")]
public static class PatchEnergyCounter
{
    private static readonly System.Reflection.FieldInfo PlayerField =
        AccessTools.Field(typeof(NEnergyCounter), "_player");

    public static void Prefix(NEnergyCounter __instance, out Player? __state)
    {
        __state = null;
        try
        {
            Player? peer = PeerSpectate.Peer;
            if (peer == null)
                return;
            if (PlayerField.GetValue(__instance) is not Player current || !LocalContext.IsMe(current))
                return;
            __state = current;
            PlayerField.SetValue(__instance, peer);
        }
        catch (System.Exception e)
        {
            Log.Write($"energy swap error: {e}");
        }
    }

    public static void Postfix(NEnergyCounter __instance, Player? __state)
    {
        if (__state != null)
            PlayerField.SetValue(__instance, __state);
    }
}

/// <summary>
/// The top-bar deck button opens the viewed player's deck while spectating (and
/// keeps its vanilla toggle-close behavior). The keyboard deck action never gets
/// here — PatchPeerViewInput consumes it before NHotkeyManager.
/// </summary>
[HarmonyPatch(typeof(NTopBarDeckButton), "OnRelease")]
public static class PatchTopBarDeckClick
{
    public static bool Prefix()
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            if (peer == null)
                return true;
            var capstone = NCapstoneContainer.Instance;
            if (capstone?.CurrentCapstoneScreen is NDeckViewScreen)
            {
                capstone.Close();
                PeerScreens.ForgetCapstone();
            }
            else
            {
                PeerScreens.ShowDeck(peer);
            }
            return false;
        }
        catch (System.Exception e)
        {
            Log.Write($"deck button error: {e}");
            return true;
        }
    }
}

/// <summary>
/// Any capstone screen (peer piles/deck, but also vanilla ones like the map) opens
/// above the battlefield but below our strip — hide the strip while one is up.
/// Re-shown from PeerSpectate.OnCapstoneClosed.
/// </summary>
[HarmonyPatch(typeof(NCapstoneContainer), "Open")]
public static class PatchCapstoneOpen
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerSpectate.SetStripVisible(false);
    }
}

/// <summary>
/// The local pile buttons keep animating their own pile while we spectate; repaint
/// the peer's count right after their label updates so local values never stick.
/// </summary>
[HarmonyPatch(typeof(NCombatCardPile), "AddCard")]
public static class PatchPileAddCard
{
    public static void Postfix(NCombatCardPile __instance)
    {
        if (PeerSpectate.Active)
            PeerIndicators.ApplyPeerCount(__instance);
    }
}

[HarmonyPatch(typeof(NCombatCardPile), "RemoveCard")]
public static class PatchPileRemoveCard
{
    public static void Postfix(NCombatCardPile __instance)
    {
        if (PeerSpectate.Active)
            PeerIndicators.ApplyPeerCount(__instance);
    }
}

/// <summary>
/// Clicking a pile button while spectating opens the viewed player's pile instead
/// of the local one (the keyboard path is handled in PatchPeerViewInput, which
/// preempts NHotkeyManager entirely).
/// </summary>
[HarmonyPatch(typeof(NCombatCardPile), "OnRelease")]
public static class PatchPileButtonClick
{
    public static bool Prefix(NCombatCardPile __instance)
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            if (peer == null)
                return true;
            PileType? type = PeerIndicators.PileTypeOf(__instance);
            var state = peer.PlayerCombatState;
            if (type == null || state == null)
                return true;
            CardPile pile = type == PileType.Draw ? state.DrawPile
                : type == PileType.Discard ? state.DiscardPile
                : state.ExhaustPile;
            if (pile.Cards.Count > 0)
                PeerScreens.ShowPile(peer, type.Value);
            return false;
        }
        catch (System.Exception e)
        {
            Log.Write($"pile click error: {e}");
            return true;
        }
    }
}

/// <summary>
/// Capstone screens for the viewed player's deck and non-hand piles, reusing the
/// vanilla NDeckViewScreen / NCardPileScreen (both accept an arbitrary player/pile).
/// </summary>
internal static class PeerScreens
{
    internal static Node? CapstoneScreen;
    internal static PileType? ShownPile; // null while the deck view is shown

    internal static bool CapstoneIsOurs()
    {
        if (CapstoneScreen == null)
            return false;
        var capstone = NCapstoneContainer.Instance;
        if (capstone == null || !ReferenceEquals(capstone.CurrentCapstoneScreen, CapstoneScreen))
        {
            CapstoneScreen = null;
            ShownPile = null;
            return false;
        }
        return true;
    }

    internal static void CloseCapstone()
    {
        if (CapstoneIsOurs())
            NCapstoneContainer.Instance?.Close();
        CapstoneScreen = null;
        ShownPile = null;
    }

    internal static void ForgetCapstone()
    {
        CapstoneScreen = null;
        ShownPile = null;
    }

    internal static void ShowPile(Player peer, PileType type)
    {
        var combatState = peer.PlayerCombatState;
        if (combatState == null)
            return;
        CardPile? pile = type switch
        {
            PileType.Draw => combatState.DrawPile,
            PileType.Discard => combatState.DiscardPile,
            PileType.Exhaust => combatState.ExhaustPile,
            _ => null,
        };
        if (pile == null)
            return;
        var screen = NCardPileScreen.ShowScreen(pile, System.Array.Empty<string>());
        SetOwnerLabel(screen, peer, type);
        CapstoneScreen = screen;
        ShownPile = type;
        Log.Write($"showing {type} of {PlayerName(peer)} (netId={peer.NetId}, {pile.Cards.Count} cards)");
    }

    internal static void ShowDeck(Player peer)
    {
        var screen = NDeckViewScreen.ShowScreen(peer);
        if (screen == null)
            return;
        CapstoneScreen = screen;
        ShownPile = null;
        Log.Write($"showing deck of {PlayerName(peer)} (netId={peer.NetId})");
    }

    /// <summary>
    /// The vanilla pile screen has no owner indication (it only ever shows your own
    /// piles), so repurpose its bottom info label to say whose cards these are.
    /// </summary>
    private static void SetOwnerLabel(NCardPileScreen screen, Player peer, PileType type)
    {
        try
        {
            var label = screen.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel");
            if (label == null)
                return;
            string pileName = type switch
            {
                PileType.Draw => IsKorean ? "뽑을 카드 더미 (실제 순서와 무관하게 정렬됨)" : "Draw Pile (sorted — actual order hidden)",
                PileType.Discard => IsKorean ? "버린 카드 더미" : "Discard Pile",
                PileType.Exhaust => IsKorean ? "소멸된 카드 더미" : "Exhaust Pile",
                _ => type.ToString(),
            };
            label.Text = $"[center]{PlayerName(peer)} — {pileName}";
            label.Visible = true;
        }
        catch (System.Exception e)
        {
            Log.Write($"owner label error: {e}");
        }
    }

    /// <summary>
    /// Same language gate UndoSync uses: Korean strings for the "kor" game
    /// language, English otherwise.
    /// </summary>
    internal static bool IsKorean
    {
        get
        {
            try
            {
                return LocManager.Instance.Language == "kor";
            }
            catch
            {
                return false;
            }
        }
    }

    internal static string PlayerName(Player peer)
    {
        try
        {
            var svc = RunManager.Instance?.NetService;
            if (svc != null)
                return PlatformUtil.GetPlayerName(svc.Platform, peer.NetId);
        }
        catch { }
        return $"Player {peer.NetId}";
    }
}

/// <summary>
/// Makes other players' characters on the battlefield clickable: releasing a left
/// click on an ally's hitbox toggles spectate mode for that player. Mirrors the
/// guards vanilla uses for the corner player-state widget
/// (NMultiplayerPlayerState.OnRelease) so we never swallow a targeting click.
/// </summary>
[HarmonyPatch(typeof(NCreature), "_Ready")]
public static class PatchCreatureClick
{
    public static void Postfix(NCreature __instance)
    {
        try
        {
            Creature? entity = __instance.Entity;
            if (entity == null || !entity.IsPlayer)
                return;
            var hitbox = __instance.Hitbox;
            if (hitbox == null)
                return;
            var creature = __instance;
            hitbox.Connect(Control.SignalName.GuiInput,
                Callable.From<InputEvent>(ev => OnHitboxGuiInput(creature, ev)));
        }
        catch (System.Exception e)
        {
            Log.Write($"creature hook error: {e}");
        }
    }

    private static void OnHitboxGuiInput(NCreature creature, InputEvent ev)
    {
        try
        {
            if (ev is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb)
                return;
            var hitbox = creature.Hitbox;
            // The control owns the pointer from press to release; ignore releases
            // that drifted off the hitbox (event position is hitbox-local).
            if (hitbox == null || !new Rect2(Vector2.Zero, hitbox.Size).HasPoint(mb.Position))
                return;
            Player? player = creature.Entity?.Player;
            if (player == null || NRun.Instance == null)
                return;
            // Your own character is not a spectate target — your hand is already on
            // screen, and self-clicks happen constantly during normal play.
            if (LocalContext.IsMe(player))
                return;
            // A live card play (card picked up / awaiting its target) or a card
            // selection screen must never be interrupted: normal card-play targeting
            // does NOT go through NTargetManager, so the IsInSelection guard below
            // does not cover it (found in 4-player testing: clicking your own
            // character to aim Defend opened the peer view and ate the play).
            var hand = NCombatRoom.Instance?.Ui?.Hand;
            if (hand == null || hand.InCardPlay || hand.IsInCardSelection)
                return;
            var targetManager = NTargetManager.Instance;
            if (targetManager.IsInSelection
                || targetManager.LastTargetingFinishedFrame == (long)creature.GetTree().GetFrame())
                return;
            if (NCapstoneContainer.Instance?.InUse == true)
                return;
            if (PeerSpectate.Active && PeerSpectate.Peer == player)
                PeerSpectate.Exit();
            else
                PeerSpectate.Enter(player);
        }
        catch (System.Exception e)
        {
            Log.Write($"creature click error: {e}");
        }
    }
}

/// <summary>
/// Input routing while spectating. Runs as a prefix on NGame._Input, i.e. in the
/// _input phase, which precedes NHotkeyManager's _UnhandledInput — SetInputAsHandled
/// therefore keeps the vanilla pile buttons from also reacting. Releases of the view
/// actions are swallowed too: the vanilla combat pile buttons trigger on *release*,
/// and would otherwise hijack the screen right after our press-triggered switch.
///
/// While spectating (no capstone open): view-pile/deck actions open the viewed
/// player's piles; Esc/back exits spectate. While one of our capstones is open:
/// the same actions switch between piles, pressing the current one closes it.
/// </summary>
[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchPeerViewInput
{
    public static void Prefix(NGame __instance, InputEvent inputEvent)
    {
        try
        {
            if (HandleInput(inputEvent))
                __instance.GetViewport()?.SetInputAsHandled();
        }
        catch (System.Exception e)
        {
            Log.Write($"input error: {e}");
        }
    }

    private static bool HandleInput(InputEvent inputEvent)
    {
        bool capstoneOurs = PeerScreens.CapstoneIsOurs();
        bool spectating = PeerSpectate.Active;
        if (!capstoneOurs && !spectating)
            return false;
        Player? peer = PeerSpectate.Peer;
        if (peer == null)
        {
            // Capstone open but spectate gone (e.g. combat ended behind it): let the
            // screen behave like a vanilla one.
            return false;
        }

        if (inputEvent.IsActionPressed(MegaInput.viewDeckAndTabLeft))
        {
            if (capstoneOurs && PeerScreens.ShownPile == null)
                PeerScreens.CloseCapstone();
            else
                PeerScreens.ShowDeck(peer);
            return true;
        }
        if (inputEvent.IsActionPressed(MegaInput.viewDrawPile))
        {
            if (capstoneOurs && PeerScreens.ShownPile == PileType.Draw)
                PeerScreens.CloseCapstone();
            else
                PeerScreens.ShowPile(peer, PileType.Draw);
            return true;
        }
        if (inputEvent.IsActionPressed(MegaInput.viewDiscardPile))
        {
            if (capstoneOurs && PeerScreens.ShownPile == PileType.Discard)
                PeerScreens.CloseCapstone();
            else
                PeerScreens.ShowPile(peer, PileType.Discard);
            return true;
        }
        if (inputEvent.IsActionPressed(MegaInput.viewExhaustPileAndTabRight))
        {
            if (capstoneOurs && PeerScreens.ShownPile == PileType.Exhaust)
                PeerScreens.CloseCapstone();
            else
                PeerScreens.ShowPile(peer, PileType.Exhaust);
            return true;
        }
        if (inputEvent.IsActionReleased(MegaInput.viewDeckAndTabLeft)
            || inputEvent.IsActionReleased(MegaInput.viewDrawPile)
            || inputEvent.IsActionReleased(MegaInput.viewDiscardPile)
            || inputEvent.IsActionReleased(MegaInput.viewExhaustPileAndTabRight))
        {
            return true;
        }

        // Esc/back closes our capstone if one is open, otherwise exits spectate.
        // Matched by raw keycode as well: in-game neither ui_cancel nor
        // mega_pause_and_back fires for the Escape key (and Escape, being a
        // control key, is immune to the Korean IME keycode issues that raw
        // letter keys have).
        bool escKey = inputEvent is InputEventKey escEv
            && (escEv.Keycode == Key.Escape || escEv.PhysicalKeycode == Key.Escape)
            && !escEv.Echo;
        bool escPressed = (escKey && ((InputEventKey)inputEvent).Pressed)
            || inputEvent.IsActionPressed(MegaInput.cancel)
            || inputEvent.IsActionPressed(MegaInput.pauseAndBack)
            || inputEvent.IsActionPressed(MegaInput.back);
        bool escReleased = (escKey && !((InputEventKey)inputEvent).Pressed)
            || inputEvent.IsActionReleased(MegaInput.cancel)
            || inputEvent.IsActionReleased(MegaInput.pauseAndBack)
            || inputEvent.IsActionReleased(MegaInput.back);
        if (capstoneOurs)
        {
            if (escPressed)
            {
                PeerScreens.CloseCapstone();
                return true;
            }
            if (escReleased)
                return true;
        }
        else if (spectating && NCapstoneContainer.Instance?.InUse != true)
        {
            if (escPressed)
            {
                PeerSpectate.Exit();
                return true;
            }
            if (escReleased)
                return true;
        }
        return false;
    }
}
