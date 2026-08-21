using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.TopBar;
// NTopBarGold/NTopBarHp live in a lowercase "sts2" namespace (a casing quirk in the
// game's own build — NTopBar.cs itself imports both casings for the same reason).
using MegaCrit.sts2.Core.Nodes.TopBar;
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

    // Vanilla's deck/pile screen dims to StsColors.screenBackdrop (alpha 0.8, NCardPileScreen.cs:185).
    // Spectate is not a modal screen — you still want to watch the fight — so this is much lighter.
    private const float DimAlpha = 0.35f;

    private static Player? _peer;
    private static Control? _root;
    private static ColorRect? _dim;
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
    private static Player? _subscribedGoldPeer;
    private static Creature? _subscribedCreature;
    private static CardPile? _subscribedDeckPile;
    private static Player? _subscribedPotionPeer;
    private static Player? _subscribedRelicPeer;

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

        // Full-screen dim so spectate mode is unmistakable at a glance. Same technique
        // as NCardPileScreen's Background ColorRect (NCardPileScreen.cs:182-185): start
        // at StsColors.transparentBlack and tween Modulate's alpha up — but faster
        // (0.2s vs vanilla's 1.0s), since this is a mode toggle, not a screen opening.
        // MouseFilter.Ignore like the rest of this strip: clicks must keep passing
        // through to the ally's character underneath, since clicking it is one of the
        // ways to exit spectate. Added as _root's first child so _cardLayer, _header,
        // and the exit button all draw above it (Godot draws siblings in child order).
        _dim = new ColorRect { Color = Colors.Black, Modulate = StsColors.transparentBlack, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChildSafely(_dim);
        _root.MoveChildSafely(_dim, 0);
        _dim.GlobalPosition = visible.Position;
        _dim.Size = visible.Size;
        _dim.CreateTween()
            .TweenProperty(_dim, "modulate", new Color(0f, 0f, 0f, DimAlpha), 0.2)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);

        _cardLayer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChildSafely(_cardLayer);
        // The fan tables are symmetric around x=0, so anchor the layer at the exact
        // horizontal center of the canvas; the vanilla card container only supplies
        // the hand's baseline height (its own x sits slightly off-center).
        _cardLayer.GlobalPosition = new Vector2(
            visible.Position.X + visible.Size.X / 2f,
            RestingHandY(hand));

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
        _subscribedState.StarsChanged += OnPeerStarsChanged;
        if (CombatManager.Instance != null)
        {
            CombatManager.Instance.CombatEnded += OnCombatEnded;
            _combatEndSubscribed = true;
        }
        _subscribedGoldPeer = peer;
        peer.GoldChanged += OnPeerGoldChanged;
        _subscribedCreature = peer.Creature;
        _subscribedCreature.CurrentHpChanged += OnPeerHpChanged;
        _subscribedCreature.MaxHpChanged += OnPeerHpChanged;
        _subscribedDeckPile = peer.Deck;
        _subscribedDeckPile.ContentsChanged += OnPeerDeckChanged;
        _subscribedPotionPeer = peer;
        peer.PotionProcured += OnPeerPotionsChanged;
        peer.UsedPotionRemoved += OnPeerPotionsChanged;
        peer.PotionDiscarded += OnPeerPotionsChanged;
        _subscribedRelicPeer = peer;
        peer.RelicObtained += OnPeerRelicsChanged;
        peer.RelicRemoved += OnPeerRelicsChanged;

        Rebuild();
        PeerIndicators.ApplyAll();
        PeerIndicators.RefreshEnergyCounters();
        PeerTopBar.ApplyGold();
        PeerTopBar.ApplyHp();
        PeerTopBar.ApplyDeckCount();
        PeerStarCounter.Apply();
        PeerPotionReplica.Enter(peer);
        PeerRelicReplica.Enter(peer);
        Log.Write($"spectate enter: {PeerScreens.PlayerName(peer)} (netId={peer.NetId}, {combatState.Hand.Cards.Count} cards)");
        // If this peer already has a card-selection screen open on their end (we
        // recorded it via a CardSelectCmd prefix before we started watching them),
        // show its mirror now that we're actually looking at them.
        PeerCardSelectMirror.OnSpectateEntered(peer);
    }

    internal static void Exit()
    {
        // Close before anything else tears down — CloseShown() is a no-op if nothing
        // is up, and doing it first keeps the mirror from ever outliving the strip.
        PeerCardSelectMirror.OnSpectateExited();
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
            _subscribedState.StarsChanged -= OnPeerStarsChanged;
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
        if (_subscribedGoldPeer != null)
        {
            _subscribedGoldPeer.GoldChanged -= OnPeerGoldChanged;
            _subscribedGoldPeer = null;
        }
        if (_subscribedCreature != null)
        {
            _subscribedCreature.CurrentHpChanged -= OnPeerHpChanged;
            _subscribedCreature.MaxHpChanged -= OnPeerHpChanged;
            _subscribedCreature = null;
        }
        if (_subscribedDeckPile != null)
        {
            _subscribedDeckPile.ContentsChanged -= OnPeerDeckChanged;
            _subscribedDeckPile = null;
        }
        if (_subscribedPotionPeer != null)
        {
            _subscribedPotionPeer.PotionProcured -= OnPeerPotionsChanged;
            _subscribedPotionPeer.UsedPotionRemoved -= OnPeerPotionsChanged;
            _subscribedPotionPeer.PotionDiscarded -= OnPeerPotionsChanged;
            _subscribedPotionPeer = null;
        }
        if (_subscribedRelicPeer != null)
        {
            _subscribedRelicPeer.RelicObtained -= OnPeerRelicsChanged;
            _subscribedRelicPeer.RelicRemoved -= OnPeerRelicsChanged;
            _subscribedRelicPeer = null;
        }
        PeerPotionReplica.Exit();
        PeerRelicReplica.Exit();

        ClearCards();
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.QueueFreeSafelyNoPool();
        _root = null;
        // _dim is a child of _root, so QueueFreeSafelyNoPool above already frees it
        // (and its running tween, which auto-invalidates when its target is freed) —
        // an instant exit, matching Exit()'s no-fade-out treatment of everything else.
        _dim = null;
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
            PeerTopBar.RestoreGold();
            PeerTopBar.RestoreHp();
            PeerTopBar.RestoreDeckCount();
            PeerStarCounter.Restore();
            Log.Write($"spectate exit: netId={exitedNetId}");
        }
    }

    private static readonly System.Reflection.FieldInfo? HandShowPositionField =
        AccessTools.Field(typeof(NPlayerHand), "_showPosition");

    /// <summary>
    /// The Y the hand's card container sits at with its "player actions disabled"
    /// offset removed. Once you end your turn, NPlayerHand.AnimDisable tweens the
    /// whole hand node down to _disablePosition (0, 100), so reading the live global
    /// position would anchor the replica strip 100px too low for the rest of the turn.
    /// _showPosition is the hand's resting local position, so subtracting the node's
    /// current local offset from it yields the resting anchor — correct mid-tween too.
    /// </summary>
    private static float RestingHandY(NPlayerHand hand)
    {
        float liveY = hand.CardHolderContainer.GlobalPosition.Y;
        try
        {
            Vector2 resting = HandShowPositionField?.GetValue(hand) is Vector2 showPosition
                ? showPosition
                : Vector2.Zero;
            return liveY - hand.Position.Y + resting.Y;
        }
        catch (System.Exception e)
        {
            Log.Write($"hand anchor error: {e}");
            return liveY;
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

    private static void OnPeerGoldChanged() => PeerTopBar.ApplyGold();

    private static void OnPeerHpChanged(int _, int __) => PeerTopBar.ApplyHp();

    private static void OnPeerDeckChanged() => PeerTopBar.ApplyDeckCount();

    private static void OnPeerStarsChanged(int _, int __) => PeerStarCounter.Apply();

    private static void OnPeerPotionsChanged(PotionModel _) => PeerPotionReplica.Rebuild();

    private static void OnPeerRelicsChanged(RelicModel _) => PeerRelicReplica.Rebuild();

    private static void OnCombatEnded(CombatRoom _) => Exit();

    private static void OnCapstoneClosed() => SetStripVisible(true);

    /// <summary>
    /// The strip draws above capstone screens (it lives near the top of the combat
    /// UI tree), so it hides while any capstone is open — see PatchCapstoneOpen.
    /// _dim is a child of _root, so toggling Visible here hides the full-screen dim
    /// along with the rest of the strip whenever a capstone (peer piles/deck) opens
    /// on top — no extra work needed.
    /// </summary>
    internal static void SetStripVisible(bool visible)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.Visible = visible;
        PeerPotionReplica.SetVisible(visible);
        PeerRelicReplica.SetVisible(visible);
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
/// Kind of card-selection entry point a peer's pending choice came through — used to
/// recreate the matching read-only mirror screen (see PeerCardSelectMirror.CreateMirror).
/// </summary>
internal enum PeerCardSelectKind
{
    ChooseACard,
    SimpleGrid,
    CombatPile,
}

/// <summary>
/// Enough of a CardSelectCmd call's arguments to recreate its selection screen
/// read-only, captured by the Harmony prefixes below. Lockstep guarantees these
/// candidate lists/piles are identical on every peer's machine (CardSelectCmd's
/// remote-choice path only ever receives an INDEX into a list it built itself — see
/// e.g. FromChooseACardScreen's `cards[num]`), so recreating the same screen locally
/// from the same arguments is exact, not a guess.
/// </summary>
internal sealed class PeerPendingChoice
{
    public required PeerCardSelectKind Kind { get; init; }
    public required Player Player { get; init; }
    public IReadOnlyList<CardModel>? Cards { get; init; }
    public bool CanSkip { get; init; }
    public CardSelectorPrefs Prefs { get; init; }
    public CardPile? Pile { get; init; }
    public System.Func<CardModel, bool>? Filter { get; init; }
}

/// <summary>
/// Shows a read-only mirror of a spectated peer's card-selection screen (Attack
/// Potion-style grids, Survivor-style pile picks, etc.) built from the exact same
/// candidate list/pile the peer's own screen was built from (see PeerPendingChoice).
/// Never creates a GameAction, consumes RNG, or sends a net message — this is a
/// second instance of the game's own selection-screen classes, with every input path
/// neutered (see BlockInput) so it is purely something to look at.
/// </summary>
internal static class PeerCardSelectMirror
{
    private static readonly Dictionary<ulong, PeerPendingChoice> _pending = new();
    private static Control? _shownScreen;
    private static ulong? _shownForNetId;
    private static PlayerChoiceSynchronizer? _registeredSynchronizer;

    internal static bool IsShown
    {
        get
        {
            // Self-heal if the screen got freed by something other than CloseShown
            // (e.g. a scene teardown) so we don't report a stale "shown" forever.
            if (_shownScreen != null && !GodotObject.IsInstanceValid(_shownScreen))
            {
                _shownScreen = null;
                _shownForNetId = null;
            }
            return _shownScreen != null;
        }
    }

    /// <summary>Called from the three CardSelectCmd prefixes below.</summary>
    internal static void Record(PeerPendingChoice choice)
    {
        try
        {
            EnsureSubscribed();
            ulong netId = choice.Player.NetId;
            // One pending choice per player (nested selections aren't a thing this
            // mirrors) — a new one replaces whatever this player had, closing the old
            // mirror first if it happened to be the one on screen.
            if (_shownForNetId == netId)
                CloseShown();
            _pending[netId] = choice;
            if (PeerSpectate.Peer?.NetId == netId)
                ShowFor(choice);
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror record error: {e}");
        }
    }

    /// <summary>
    /// Called from PeerSpectate.Enter once the new peer is fully set up: if they
    /// already have a pending choice recorded (their screen was opened before we
    /// started watching them), show its mirror now.
    /// </summary>
    internal static void OnSpectateEntered(Player peer)
    {
        try
        {
            if (_pending.TryGetValue(peer.NetId, out PeerPendingChoice? choice))
                ShowFor(choice);
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror spectate-enter error: {e}");
        }
    }

    /// <summary>Called from PeerSpectate.Exit — closes whatever mirror is up, if any.</summary>
    internal static void OnSpectateExited() => CloseShown();

    private static void ShowFor(PeerPendingChoice choice)
    {
        CloseShown();
        Control? screen = CreateMirror(choice);
        if (screen == null)
            return;
        _shownScreen = screen;
        _shownForNetId = choice.Player.NetId;
        PeerSpectate.SetStripVisible(false);
    }

    /// <summary>
    /// Closes the currently-shown mirror through NOverlayStack.Remove — never
    /// QueueFree directly. Remove is what recalculates the shared backstop (the
    /// input-blocking dim) and calls the screen's own AfterOverlayClosed (which frees
    /// the node); skipping it would leave the backstop dim on screen permanently,
    /// the same class of bug UndoSync hit with NModalContainer.
    /// </summary>
    internal static void CloseShown()
    {
        try
        {
            if (_shownScreen != null && GodotObject.IsInstanceValid(_shownScreen) && _shownScreen is IOverlayScreen overlay)
                NOverlayStack.Instance?.Remove(overlay);
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror close error: {e}");
        }
        finally
        {
            bool wasShown = _shownScreen != null;
            _shownScreen = null;
            _shownForNetId = null;
            if (wasShown)
                PeerSpectate.SetStripVisible(true);
        }
    }

    private static Control? CreateMirror(PeerPendingChoice choice)
    {
        try
        {
            switch (choice.Kind)
            {
                case PeerCardSelectKind.ChooseACard:
                {
                    // ShowScreen pushes onto NOverlayStack itself and returns null in
                    // TestMode — nothing to mirror in that case.
                    NChooseACardSelectionScreen? screen = NChooseACardSelectionScreen.ShowScreen(choice.Cards!, choice.CanSkip);
                    if (screen == null || !GodotObject.IsInstanceValid(screen))
                        return null;
                    ApplyBannerText(screen, choice.Player);
                    BlockInput(screen);
                    return screen;
                }
                case PeerCardSelectKind.SimpleGrid:
                {
                    // Create does NOT push (unlike ShowScreen above) — we push it ourselves.
                    NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(choice.Cards!, choice.Prefs);
                    NOverlayStack.Instance?.Push(screen);
                    if (!GodotObject.IsInstanceValid(screen))
                        return null;
                    ApplyBottomLabelText(screen, choice.Player);
                    BlockInput(screen);
                    return screen;
                }
                case PeerCardSelectKind.CombatPile:
                {
                    NCombatPileCardSelectScreen screen = NCombatPileCardSelectScreen.Create(choice.Pile!, choice.Prefs, choice.Filter);
                    NOverlayStack.Instance?.Push(screen);
                    if (!GodotObject.IsInstanceValid(screen))
                        return null;
                    ApplyBottomLabelText(screen, choice.Player);
                    BlockInput(screen);
                    return screen;
                }
                default:
                    return null;
            }
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror create error: {e}");
            return null;
        }
    }

    /// <summary>
    /// NChooseACardSelectionScreen's banner normally reads "Choose a Card" — accurate
    /// for the player picking, misleading for a spectator — so repoint it at who's
    /// actually picking. "Banner" is a plain (non-unique-name) direct child of the
    /// screen root, so it's reachable by path the same way PeerScreens.SetOwnerLabel
    /// already reaches %BottomLabel on the pile/deck capstones.
    /// </summary>
    private static void ApplyBannerText(NChooseACardSelectionScreen screen, Player peer)
    {
        try
        {
            NCommonBanner? banner = screen.GetNodeOrNull<NCommonBanner>("Banner");
            if (banner == null || !GodotObject.IsInstanceValid(banner) || banner.label == null)
                return;
            banner.label.SetTextAutoSize(PeerScreens.IsKorean
                ? $"{PeerScreens.PlayerName(peer)} 고르는 중"
                : $"{PeerScreens.PlayerName(peer)} is choosing");
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror banner error: {e}");
        }
    }

    /// <summary>Same idea as ApplyBannerText, for the grid screens' %BottomLabel prompt.</summary>
    private static void ApplyBottomLabelText(Control screen, Player peer)
    {
        try
        {
            var label = screen.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel");
            if (label == null || !GodotObject.IsInstanceValid(label))
                return;
            label.Text = PeerScreens.IsKorean
                ? $"[center]{PeerScreens.PlayerName(peer)} 고르는 중"
                : $"[center]{PeerScreens.PlayerName(peer)} is choosing";
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror label error: {e}");
        }
    }

    /// <summary>
    /// Neuters the mirror so a spectator can never make a "selection" on someone
    /// else's behalf. Mouse: a full-screen Stop-filter Control added as the LAST
    /// child (the same "invisible top-most sibling" trick PeerSpectate.Rebuild uses
    /// for its card hover sensors) sits above every real widget and eats every click
    /// before it reaches the card holders/buttons underneath. Keyboard/controller:
    /// recursing FocusMode=None over every descendant Control blocks focus-driven
    /// "confirm" input. FocusMode=None (rather than NClickableControl.Disable()) is
    /// used because the grid screens' card holders (NGridCardHolder : NCardHolder :
    /// Control, confirmed in decompiled source) are NOT NClickableControl — a
    /// Disable()-only sweep would miss them, while FocusMode=None reaches every
    /// widget type uniformly. Without this, a click/confirm would resolve the
    /// mirror's own (never-awaited) _completionSource and make the screen vanish on
    /// its own, and the spectator would wrongly think they had just chosen a card.
    /// </summary>
    private static void BlockInput(Control screen)
    {
        try
        {
            DisableFocusRecursive(screen);
            var blocker = new Control
            {
                Name = "PeerViewSelectBlocker",
                MouseFilter = Control.MouseFilterEnum.Stop,
                FocusMode = Control.FocusModeEnum.None,
            };
            screen.AddChildSafely(blocker);
            blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror input block error: {e}");
        }
    }

    private static void DisableFocusRecursive(Node node)
    {
        if (node is Control control)
            control.FocusMode = Control.FocusModeEnum.None;
        foreach (Node child in node.GetChildren())
            DisableFocusRecursive(child);
    }

    /// <summary>
    /// RunManager.Instance.PlayerChoiceSynchronizer is (re)created per run
    /// (RunManager.cs), so — same as UndoSync's _registeredService pattern — track
    /// which instance we're subscribed to and swap the subscription when it changes,
    /// rather than subscribing once at mod-init and going stale after the first run.
    /// </summary>
    private static void EnsureSubscribed()
    {
        PlayerChoiceSynchronizer? synchronizer = RunManager.Instance?.PlayerChoiceSynchronizer;
        if (synchronizer == null || ReferenceEquals(synchronizer, _registeredSynchronizer))
            return;
        if (_registeredSynchronizer != null)
            _registeredSynchronizer.PlayerChoiceReceived -= OnPlayerChoiceReceived;
        synchronizer.PlayerChoiceReceived += OnPlayerChoiceReceived;
        _registeredSynchronizer = synchronizer;
    }

    /// <summary>
    /// PlayerChoiceReceived also fires for the LOCAL player's own choices
    /// (SyncLocalChoice invokes it directly before sending the net message) — only
    /// react to peers we're actually tracking a pending mirror for; that alone also
    /// filters out the local echo, since local players are never recorded into
    /// _pending in the first place (the three prefixes below skip LocalContext.IsMe).
    /// No UX highlighting of what was picked — just close, per the design.
    /// </summary>
    private static void OnPlayerChoiceReceived(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        try
        {
            if (!_pending.Remove(player.NetId))
                return;
            if (_shownForNetId == player.NetId)
                CloseShown();
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror choice-received error: {e}");
        }
    }

    /// <summary>
    /// Safety net for choices that end without a PlayerChoiceReceived ever arriving:
    /// the peer dropped mid-selection, the combat tore down around it, the selection
    /// was cancelled. Without this a pending entry could outlive its combat and pop a
    /// stale mirror the next time that peer is spectated. The From* methods are async,
    /// so a Harmony postfix runs the moment the Task is handed back rather than when
    /// the choice resolves — observing the Task itself is what tracks the real end.
    /// The continuation lands on a thread pool thread, so the node work is deferred
    /// onto the main thread.
    /// </summary>
    internal static void ClearWhenSettled(System.Threading.Tasks.Task task, Player player)
    {
        try
        {
            ulong netId = player.NetId;
            task.ContinueWith(
                _ => Callable.From(() => Forget(netId)).CallDeferred(),
                System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror settle hook error: {e}");
        }
    }

    private static void Forget(ulong netId)
    {
        try
        {
            _pending.Remove(netId);
            if (_shownForNetId == netId)
                CloseShown();
        }
        catch (System.Exception e)
        {
            Log.Write($"card select mirror forget error: {e}");
        }
    }
}

/// <summary>
/// Mirrors CardSelectCmd.FromChooseACardScreen (Attack Potion-style "pick 1 of up to
/// 3 generated cards" screens) for a spectated peer. Void prefix — never blocks or
/// alters the original call.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen),
    new System.Type[] { typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(bool) })]
public static class PatchCardSelectMirrorChooseACard
{
    public static void Prefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip)
    {
        try
        {
            if (LocalContext.IsMe(player) || CardSelectCmd.Selector != null)
                return;
            // Matches the real method's own guards (ReportSoftlock on 0, throw on
            // >3): in both cases no screen ever shows for ANY player, so don't mirror one.
            if (cards.Count == 0 || cards.Count > 3)
                return;
            PeerCardSelectMirror.Record(new PeerPendingChoice
            {
                Kind = PeerCardSelectKind.ChooseACard,
                Player = player,
                Cards = cards,
                CanSkip = canSkip,
            });
        }
        catch (System.Exception e)
        {
            Log.Write($"choose-a-card mirror hook error: {e}");
        }
    }

    public static void Postfix(System.Threading.Tasks.Task<CardModel> __result, Player player)
    {
        if (!LocalContext.IsMe(player))
            PeerCardSelectMirror.ClearWhenSettled(__result, player);
    }
}

/// <summary>Mirrors CardSelectCmd.FromSimpleGrid for a spectated peer.</summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid),
    new System.Type[] { typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs) })]
public static class PatchCardSelectMirrorSimpleGrid
{
    public static void Prefix(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs)
    {
        try
        {
            if (LocalContext.IsMe(player) || CardSelectCmd.Selector != null)
                return;
            // Matches the real method's own no-UI shortcuts: an empty list, or a
            // count small enough (and no manual confirm required) that it
            // auto-selects everything without ever showing a screen.
            if (cardsIn.Count == 0)
                return;
            if (!prefs.RequireManualConfirmation && cardsIn.Count <= prefs.MinSelect)
                return;
            PeerCardSelectMirror.Record(new PeerPendingChoice
            {
                Kind = PeerCardSelectKind.SimpleGrid,
                Player = player,
                Cards = cardsIn,
                Prefs = prefs,
            });
        }
        catch (System.Exception e)
        {
            Log.Write($"simple grid mirror hook error: {e}");
        }
    }

    public static void Postfix(System.Threading.Tasks.Task<IEnumerable<CardModel>> __result, Player player)
    {
        if (!LocalContext.IsMe(player))
            PeerCardSelectMirror.ClearWhenSettled(__result, player);
    }
}

/// <summary>
/// Mirrors CardSelectCmd.FromCombatPile (Survivor-style "pick from draw/discard/
/// exhaust" screens) for a spectated peer. Only the 5-arg overload is patched — the
/// 4-arg overload just forwards into this one with a default (always-true) filter.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
    new System.Type[] { typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(System.Func<CardModel, bool>) })]
public static class PatchCardSelectMirrorCombatPile
{
    public static void Prefix(CardPile pile, Player player, CardSelectorPrefs prefs, System.Func<CardModel, bool>? filter)
    {
        try
        {
            if (LocalContext.IsMe(player) || CardSelectCmd.Selector != null)
                return;
            if (CombatManager.Instance.IsEnding || !pile.IsCombatPile)
                return;
            // Matches the real method's own no-UI shortcuts (see FromCombatPile):
            // nothing left after the filter, or few enough left that it auto-selects.
            int count = filter == null ? pile.Cards.Count : pile.Cards.Count(filter);
            if (count == 0)
                return;
            if (!prefs.RequireManualConfirmation && count <= prefs.MinSelect)
                return;
            PeerCardSelectMirror.Record(new PeerPendingChoice
            {
                Kind = PeerCardSelectKind.CombatPile,
                Player = player,
                Pile = pile,
                Prefs = prefs,
                Filter = filter,
            });
        }
        catch (System.Exception e)
        {
            Log.Write($"combat pile mirror hook error: {e}");
        }
    }

    public static void Postfix(System.Threading.Tasks.Task<IEnumerable<CardModel>> __result, Player player)
    {
        if (!LocalContext.IsMe(player))
            PeerCardSelectMirror.ClearWhenSettled(__result, player);
    }
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
/// While spectating, the top-bar gold/HP/deck-count labels show the viewed player's
/// values, using the same postfix-reapply technique as PeerIndicators' pile counters:
/// the label is stamped directly with the peer's value, and a postfix on whichever
/// vanilla method would otherwise repaint it with the LOCAL player's value (a card
/// effect changing your own gold/HP/deck while you happen to be spectating someone
/// else) repaints it again with the peer's value right after.
/// </summary>
internal static class PeerTopBar
{
    private static readonly System.Reflection.FieldInfo GoldLabelField =
        AccessTools.Field(typeof(NTopBarGold), "_goldLabel");

    private static readonly System.Reflection.FieldInfo CurrentGoldField =
        AccessTools.Field(typeof(NTopBarGold), "_currentGold");

    private static readonly System.Reflection.FieldInfo HpLabelField =
        AccessTools.Field(typeof(NTopBarHp), "_hpLabel");

    private static readonly System.Reflection.FieldInfo HpPlayerField =
        AccessTools.Field(typeof(NTopBarHp), "_player");

    private static readonly System.Reflection.FieldInfo DeckCountLabelField =
        AccessTools.Field(typeof(NTopBarDeckButton), "_countLabel");

    private static readonly System.Reflection.FieldInfo DeckPileField =
        AccessTools.Field(typeof(NTopBarDeckButton), "_pile");

    internal static void ApplyGold()
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            NTopBarGold? gold = NRun.Instance?.GlobalUi.TopBar.Gold;
            if (peer == null || gold == null || !GodotObject.IsInstanceValid(gold))
                return;
            if (GoldLabelField.GetValue(gold) is MegaLabel label && GodotObject.IsInstanceValid(label))
                label.SetTextAutoSize(peer.Gold.ToString());
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar gold apply error: {e}");
        }
    }

    internal static void RestoreGold()
    {
        try
        {
            NTopBarGold? gold = NRun.Instance?.GlobalUi.TopBar.Gold;
            if (gold == null || !GodotObject.IsInstanceValid(gold))
                return;
            // _currentGold is vanilla's own ledger for the LOCAL player, kept up to
            // date by its subscription the whole time we were painting peer values.
            if (GoldLabelField.GetValue(gold) is MegaLabel label
                && GodotObject.IsInstanceValid(label)
                && CurrentGoldField.GetValue(gold) is int localGold)
                label.SetTextAutoSize(localGold.ToString());
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar gold restore error: {e}");
        }
    }

    internal static void ApplyHp()
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            NTopBarHp? hp = NRun.Instance?.GlobalUi.TopBar.Hp;
            if (peer == null || hp == null || !GodotObject.IsInstanceValid(hp))
                return;
            if (HpLabelField.GetValue(hp) is MegaLabel label && GodotObject.IsInstanceValid(label))
                label.SetTextAutoSize($"{peer.Creature.CurrentHp}/{peer.Creature.MaxHp}");
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar hp apply error: {e}");
        }
    }

    internal static void RestoreHp()
    {
        try
        {
            NTopBarHp? hp = NRun.Instance?.GlobalUi.TopBar.Hp;
            if (hp == null || !GodotObject.IsInstanceValid(hp))
                return;
            if (HpLabelField.GetValue(hp) is MegaLabel label
                && GodotObject.IsInstanceValid(label)
                && HpPlayerField.GetValue(hp) is Player localPlayer)
                label.SetTextAutoSize($"{localPlayer.Creature.CurrentHp}/{localPlayer.Creature.MaxHp}");
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar hp restore error: {e}");
        }
    }

    internal static void ApplyDeckCount()
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            NTopBarDeckButton? deck = NRun.Instance?.GlobalUi.TopBar.Deck;
            if (peer == null || deck == null || !GodotObject.IsInstanceValid(deck))
                return;
            if (DeckCountLabelField.GetValue(deck) is MegaLabel label && GodotObject.IsInstanceValid(label))
                label.SetTextAutoSize(peer.Deck.Cards.Count.ToString());
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar deck count apply error: {e}");
        }
    }

    internal static void RestoreDeckCount()
    {
        try
        {
            NTopBarDeckButton? deck = NRun.Instance?.GlobalUi.TopBar.Deck;
            if (deck == null || !GodotObject.IsInstanceValid(deck))
                return;
            if (DeckCountLabelField.GetValue(deck) is MegaLabel label
                && GodotObject.IsInstanceValid(label)
                && DeckPileField.GetValue(deck) is CardPile localPile)
                label.SetTextAutoSize(localPile.Cards.Count.ToString());
        }
        catch (System.Exception e)
        {
            Log.Write($"top bar deck count restore error: {e}");
        }
    }
}

/// <summary>
/// While spectating, the potion belt shows the viewed player's potions painted
/// in place over the vanilla slot frames — NOT by hiding NPotionContainer and
/// drawing a replacement beside it. NTopBar's row is a layout Container: hiding a
/// child collapses its slot, which shoves every sibling after it (room icon, etc.)
/// left and lets whatever we draw next get laid out right next to gold instead of
/// where the potions belong. So the vanilla container, its holders, and their
/// frames are left fully alone; only each local potion's own picture (if any) is
/// hidden, and a same-sized peer NPotion is drawn on top of it via a TopLevel
/// overlay root — TopLevel exempts a node from its parent Container's layout pass
/// entirely (Godot: "Nodes inside a Container will not affect the container in any
/// way once top_level is enabled"), so it can sit at an absolute GlobalPosition
/// copied from the real slot regardless of what the top bar's layout is doing.
/// Each local holder's _isUsable is also forced false for the whole spectate
/// session so a slot click can't open MY potion popup while a peer's picture is
/// drawn over it; both this and the local potion's Visible are restored on Exit.
/// </summary>
internal static class PeerPotionReplica
{
    private static readonly System.Reflection.FieldInfo HoldersField =
        AccessTools.Field(typeof(NPotionContainer), "_holders");

    private static readonly System.Reflection.FieldInfo IsUsableField =
        AccessTools.Field(typeof(NPotionHolder), "_isUsable");

    private static readonly System.Reflection.FieldInfo PotionScaleField =
        AccessTools.Field(typeof(NPotionHolder), "_potionScale");

    private static Control? _root;
    private static Player? _peer;
    private static List<NPotionHolder>? _localHolders;
    private static readonly List<NPotion> _replicaPotions = new();

    // Per local holder: its own reference, the local potion it had (if any, so we
    // can un-hide the exact same node on Exit), and its original _isUsable value.
    private static readonly List<(NPotionHolder Holder, NPotion? LocalPotion, bool WasUsable)> _hiddenState = new();

    internal static void Enter(Player peer)
    {
        try
        {
            NPotionContainer? container = NRun.Instance?.GlobalUi.TopBar.PotionContainer;
            if (container == null || !GodotObject.IsInstanceValid(container))
                return;
            Node? parent = container.GetParent();
            if (parent == null || HoldersField.GetValue(container) is not List<NPotionHolder> holders)
                return;

            _hiddenState.Clear();
            foreach (NPotionHolder holder in holders)
            {
                if (!GodotObject.IsInstanceValid(holder))
                    continue;
                bool wasUsable = IsUsableField.GetValue(holder) is bool b && b;
                NPotion? localPotion = holder.Potion;
                if (localPotion != null && GodotObject.IsInstanceValid(localPotion))
                    localPotion.Visible = false;
                // Never let a spectate-time click reach the LOCAL potion under our
                // overlay — the only vanilla state this touches, restored on Exit.
                IsUsableField.SetValue(holder, false);
                _hiddenState.Add((holder, localPotion, wasUsable));
            }
            _localHolders = holders;

            // TopLevel is the fix: without it, NTopBar's Container would lay this
            // root out on its own (see class doc comment) regardless of any
            // Position/GlobalPosition we assign below.
            _root = new Control { TopLevel = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            parent.AddChildSafely(_root);

            _peer = peer;
            Rebuild();
        }
        catch (System.Exception e)
        {
            Log.Write($"potion replica enter error: {e}");
        }
    }

    /// <summary>
    /// Full rebuild on every potion change (procured/used/discarded) — belts are
    /// small, so this is simpler and safer than trying to patch a single slot.
    /// Peer potions map to local holders by index; a peer with more potions than
    /// the local player has holders is truncated (never invents new slots).
    /// </summary>
    internal static void Rebuild()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root) || _peer == null || _localHolders == null)
            return;
        try
        {
            ClearReplicas();
            int i = 0;
            foreach (PotionModel potion in _peer.Potions)
            {
                if (i >= _localHolders.Count)
                {
                    Log.Write($"potion replica: peer has more potions than local holders ({_localHolders.Count}) — truncating");
                    break;
                }
                NPotionHolder localHolder = _localHolders[i];
                i++;
                if (!GodotObject.IsInstanceValid(localHolder))
                    continue;
                NPotion? nPotion = NPotion.Create(potion);
                if (nPotion == null)
                    continue;
                _root.AddChildSafely(nPotion);
                nPotion.MouseFilter = Control.MouseFilterEnum.Ignore;

                // Copying an existing local potion's own transform is the exact
                // match; with no local potion to copy, fall back to the holder's
                // own position/scale, mirroring what vanilla AddPotion does.
                NPotion? localPotion = localHolder.Potion;
                if (localPotion != null && GodotObject.IsInstanceValid(localPotion))
                {
                    nPotion.GlobalPosition = localPotion.GlobalPosition;
                    nPotion.Scale = localPotion.Scale;
                    nPotion.PivotOffset = localPotion.PivotOffset;
                }
                else
                {
                    Vector2 scale = PotionScaleField.GetValue(localHolder) is Vector2 s ? s : new Vector2(0.9f, 0.9f);
                    nPotion.GlobalPosition = localHolder.GlobalPosition;
                    nPotion.Scale = scale;
                    nPotion.PivotOffset = nPotion.Size * 0.5f;
                }
                _replicaPotions.Add(nPotion);
            }
        }
        catch (System.Exception e)
        {
            Log.Write($"potion replica rebuild error: {e}");
        }
    }

    private static void ClearReplicas()
    {
        foreach (NPotion potion in _replicaPotions)
        {
            // NPotionHolder/NPotion are plain nodes, not pooled like NCard.
            if (GodotObject.IsInstanceValid(potion))
                potion.QueueFreeSafelyNoPool();
        }
        _replicaPotions.Clear();
    }

    internal static void Exit()
    {
        try
        {
            ClearReplicas();
            if (_root != null && GodotObject.IsInstanceValid(_root))
                _root.QueueFreeSafelyNoPool();
            _root = null;

            foreach (var state in _hiddenState)
            {
                if (!GodotObject.IsInstanceValid(state.Holder))
                    continue;
                IsUsableField.SetValue(state.Holder, state.WasUsable);
                if (state.LocalPotion != null && GodotObject.IsInstanceValid(state.LocalPotion))
                    state.LocalPotion.Visible = true;
            }
            _hiddenState.Clear();
            _localHolders = null;
            _peer = null;
        }
        catch (System.Exception e)
        {
            Log.Write($"potion replica exit error: {e}");
        }
    }

    internal static void SetVisible(bool visible)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.Visible = visible;
    }
}

/// <summary>
/// Same replica technique as PeerPotionReplica, for the relic strip. Uses
/// NRelicInventoryHolder — the factory the top-bar relic row itself uses (it can
/// flash and shows stacked amounts) — rather than NRelicBasicHolder, which its own
/// doc comment marks as a flatter substitute used elsewhere (run history, the
/// multiplayer expanded state, the relic collection) that "cannot flash and never
/// displays amounts". A plain HFlowContainer reproduces the vanilla row's wrapping.
/// </summary>
internal static class PeerRelicReplica
{
    private static NRelicInventory? _hidden;
    private static HFlowContainer? _root;
    private static Player? _peer;

    internal static void Enter(Player peer)
    {
        try
        {
            NRelicInventory? inventory = NRun.Instance?.GlobalUi.RelicInventory;
            if (inventory == null || !GodotObject.IsInstanceValid(inventory))
                return;
            Node? parent = inventory.GetParent();
            if (parent == null)
                return;

            Vector2 origin = inventory.GlobalPosition;
            Vector2 size = inventory.Size;

            _hidden = inventory;
            inventory.Visible = false;

            // TopLevel so NGlobalUi's own layout can't reposition this root the way
            // it collapsed the potion belt (see PeerPotionReplica's doc comment) —
            // this one looked fine in testing, but it's the same hide-and-add-a-
            // sibling shape, so the same preventive fix applies.
            _root = new HFlowContainer { TopLevel = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            parent.AddChildSafely(_root);
            _root.GlobalPosition = origin;
            _root.Size = size;

            _peer = peer;
            Rebuild();
        }
        catch (System.Exception e)
        {
            Log.Write($"relic replica enter error: {e}");
        }
    }

    /// <summary>Full rebuild on every relic change — relic counts are small.</summary>
    internal static void Rebuild()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root) || _peer == null)
            return;
        try
        {
            foreach (Node child in _root.GetChildren())
            {
                if (child is NRelicInventoryHolder holder && GodotObject.IsInstanceValid(holder))
                    holder.QueueFreeSafelyNoPool();
            }
            foreach (RelicModel relic in _peer.Relics)
            {
                NRelicInventoryHolder? holder = NRelicInventoryHolder.Create(relic);
                if (holder != null)
                    _root.AddChildSafely(holder);
            }
        }
        catch (System.Exception e)
        {
            Log.Write($"relic replica rebuild error: {e}");
        }
    }

    internal static void Exit()
    {
        try
        {
            if (_root != null && GodotObject.IsInstanceValid(_root))
                _root.QueueFreeSafelyNoPool();
            _root = null;
            if (_hidden != null && GodotObject.IsInstanceValid(_hidden))
                _hidden.Visible = true;
            _hidden = null;
            _peer = null;
        }
        catch (System.Exception e)
        {
            Log.Write($"relic replica exit error: {e}");
        }
    }

    internal static void SetVisible(bool visible)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            _root.Visible = visible;
    }
}

/// <summary>
/// While spectating, the star counter (when visible) shows the viewed player's
/// stars by calling vanilla's own SetStarCountText, reusing its label formatting,
/// 0-star red color, and shader hue changes exactly instead of reimplementing them
/// here. NStarCounter._Process re-lerps and repaints from the LOCAL player (its
/// _player field is fixed at Initialize, never swapped) every single frame
/// regardless of visibility, so a one-shot stamp from a StarsChanged event alone
/// would be overwritten within a frame — PatchStarCounterProcess below is what
/// actually keeps this pinned to the peer's value; the OnStarsChanged postfix and
/// the PlayerCombatState.StarsChanged subscription in PeerSpectate exist for the
/// same belt-and-suspenders reason PeerIndicators' pile counters have both an
/// event-driven apply and a postfix-driven one.
/// Visibility is never touched here: a character that doesn't use stars keeps this
/// hidden even while spectating a star-using peer — a known, accepted gap.
/// </summary>
internal static class PeerStarCounter
{
    private static readonly System.Reflection.FieldInfo StarCounterField =
        AccessTools.Field(typeof(NCombatUi), "_starCounter");

    private static readonly System.Reflection.FieldInfo StarCounterPlayerField =
        AccessTools.Field(typeof(NStarCounter), "_player");

    private static readonly System.Reflection.MethodInfo SetStarCountTextMethod =
        AccessTools.Method(typeof(NStarCounter), "SetStarCountText");

    private static readonly System.Reflection.MethodInfo RefreshVisibilityMethod =
        AccessTools.Method(typeof(NStarCounter), "RefreshVisibility");

    /// <summary>
    /// Whether the counter shows at all is a property of whose character you are
    /// looking at — a Regent's counter is always up, everyone else's appears once
    /// they hold a star — so vanilla's own RefreshVisibility is run against the
    /// player being shown. Its verdict is sticky (`Visible = Visible || ...`, so it
    /// can only ever turn the counter ON), which is why visibility is cleared first;
    /// otherwise a spectated Regent's counter would linger on your own screen after
    /// you stopped watching them.
    /// </summary>
    private static void RefreshVisibilityFor(NStarCounter counter, Player player)
    {
        object? saved = StarCounterPlayerField.GetValue(counter);
        try
        {
            counter.Visible = false;
            StarCounterPlayerField.SetValue(counter, player);
            RefreshVisibilityMethod.Invoke(counter, null);
        }
        finally
        {
            StarCounterPlayerField.SetValue(counter, saved);
        }
    }

    private static NStarCounter? Instance()
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        if (ui == null || !GodotObject.IsInstanceValid(ui))
            return null;
        return StarCounterField.GetValue(ui) as NStarCounter;
    }

    internal static void Apply()
    {
        try
        {
            Player? peer = PeerSpectate.Peer;
            NStarCounter? counter = Instance();
            if (peer?.PlayerCombatState == null || counter == null || !GodotObject.IsInstanceValid(counter))
                return;
            SetStarCountTextMethod.Invoke(counter, new object[] { peer.PlayerCombatState.Stars });
            RefreshVisibilityFor(counter, peer);
        }
        catch (System.Exception e)
        {
            Log.Write($"star counter apply error: {e}");
        }
    }

    internal static void Restore()
    {
        try
        {
            NStarCounter? counter = Instance();
            if (counter == null || !GodotObject.IsInstanceValid(counter))
                return;
            if (StarCounterPlayerField.GetValue(counter) is Player localPlayer && localPlayer.PlayerCombatState != null)
            {
                SetStarCountTextMethod.Invoke(counter, new object[] { localPlayer.PlayerCombatState.Stars });
                RefreshVisibilityFor(counter, localPlayer);
            }
        }
        catch (System.Exception e)
        {
            Log.Write($"star counter restore error: {e}");
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
/// Fires when the LOCAL player's own stars change while spectating (NStarCounter's
/// StarsChanged subscription is bound once to the local player and never swapped) —
/// repaints with the peer's value right after so a local star gain can't leave the
/// local number showing, even for the one frame before PatchStarCounterProcess
/// would otherwise catch it.
/// </summary>
[HarmonyPatch(typeof(NStarCounter), "OnStarsChanged")]
public static class PatchStarCounterChanged
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerStarCounter.Apply();
    }
}

/// <summary>
/// NStarCounter._Process recomputes and repaints its label from the LOCAL player
/// every frame (regardless of visibility), so this is the patch that actually keeps
/// the counter pinned to the peer's value in real time — without it, PeerStarCounter
/// Apply() would be overwritten within a single frame by vanilla's own loop.
/// </summary>
[HarmonyPatch(typeof(NStarCounter), "_Process")]
public static class PatchStarCounterProcess
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerStarCounter.Apply();
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
/// UpdateGold only ever fires from the LOCAL player's own GoldChanged event (its
/// _player field is fixed at Initialize, never swapped) — this repaints the label
/// with the peer's gold right after, so a local gold change while spectating can't
/// leave the local value showing. See PeerTopBar's doc comment for why this is a
/// direct label stamp (P1) rather than the _player-swap trick PatchEnergyCounter
/// uses: UpdateGold kicks off UpdateGoldAnim's "+N" popup/ledger animation, which
/// would read the wrong player's gold delta if we ever redirected _player itself.
/// </summary>
[HarmonyPatch(typeof(NTopBarGold), "UpdateGold")]
public static class PatchTopBarGold
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerTopBar.ApplyGold();
    }
}

/// <summary>Same idea as PatchTopBarGold, for the HP label.</summary>
[HarmonyPatch(typeof(NTopBarHp), "UpdateHealth")]
public static class PatchTopBarHp
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerTopBar.ApplyHp();
    }
}

/// <summary>Same idea as PatchTopBarGold, for the deck-count label.</summary>
[HarmonyPatch(typeof(NTopBarDeckButton), "OnPileContentsChanged")]
public static class PatchTopBarDeckCount
{
    public static void Postfix()
    {
        if (PeerSpectate.Active)
            PeerTopBar.ApplyDeckCount();
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
        // A peer's card-selection mirror takes priority over both of the below: Esc
        // closes just the mirror and leaves spectate (and any capstone underneath it)
        // untouched.
        if (PeerCardSelectMirror.IsShown)
        {
            if (escPressed)
            {
                PeerCardSelectMirror.CloseShown();
                return true;
            }
            if (escReleased)
                return true;
        }
        else if (capstoneOurs)
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
