using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace UndoSync;

/// <summary>
/// Step picker shown when the player presses Left Arrow: choose how far back to
/// rewind BEFORE the vote opens (in multiplayer) or the restore happens (in
/// singleplayer). Built entirely from built-in Godot nodes plus game node
/// instances (NCard) — mod assemblies can't define Godot script subclasses (no
/// source generators), so no NModalContainer (it requires IScreenContext); a
/// full-screen ColorRect blocks game input instead.
///
/// Steps are shown as a horizontally-scrolling "card strip": card steps render
/// the actual game card visual (NCard), non-card steps (turn start/end, potion
/// use, etc.) render a small text tile. Newest-first, left to right.
/// </summary>
internal static class UndoPicker
{
    private const int MaxEntries = 20;

    private static readonly Vector2 HolderSize = new(150f, 230f);

    private static ColorRect? _root;
    private static readonly List<NCard> _cardNodes = new();

    internal static bool IsOpen => _root != null && GodotObject.IsInstanceValid(_root);

    internal static void Open()
    {
        var points = ChecksumHook.SyncPointsNewestFirst();
        if (points.Count < 2)
        {
            Log.Write($"[UndoPicker] nothing to undo (sync points={points.Count})");
            return;
        }
        Close();

        bool korean = IsKorean();

        var game = NGame.Instance;
        if (game == null) return;

        var backstop = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backstop.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 16);
        backstop.AddChild(vbox);
        // Center the vbox within the full-rect backstop.
        vbox.AnchorLeft = vbox.AnchorRight = 0.5f;
        vbox.AnchorTop = vbox.AnchorBottom = 0.5f;
        vbox.GrowHorizontal = Control.GrowDirection.Both;
        vbox.GrowVertical = Control.GrowDirection.Both;

        vbox.AddChild(new Label
        {
            Text = korean ? "어느 시점으로 되돌릴까요?" : "Undo to which point?",
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        // Fit the strip to its content so few entries stay centered; cap at 80% of
        // the UI-space width (game.Size, not GetViewportRect which is the internal
        // render resolution and can exceed the window).
        int entryCount = Math.Min(points.Count - 1, MaxEntries);
        float contentWidth = entryCount * (HolderSize.X + 16f) + 16f;
        scroll.CustomMinimumSize = new Vector2(Mathf.Min(contentWidth, game.Size.X * 0.8f), 260f);
        vbox.AddChild(scroll);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(hbox);

        // points[0] is the current state; undoing to points[i] rewinds i actions.
        // The oldest action being undone is the one that produced points[i-1].
        int shown = 0;
        for (int i = 1; i < points.Count && i <= MaxEntries; i++)
        {
            string context = points[i - 1].Context;
            uint targetId = points[i].ChecksumId;
            hbox.AddChild(BuildItem(context, i, targetId, korean));
            shown++;
        }

        var cancel = new Button { Text = korean ? "취소" : "Cancel" };
        cancel.Pressed += Close;
        var cancelHolder = new CenterContainer();
        cancelHolder.AddChild(cancel);
        vbox.AddChild(cancelHolder);

        if (game.Theme != null)
            backstop.Theme = game.Theme; // reuse game fonts (Korean glyphs included)
        game.AddChild(backstop);
        _root = backstop;
        Log.Write($"[UndoPicker] opened with {shown} choices (card strip)");
    }

    internal static void Close()
    {
        var root = _root;
        _root = null;

        foreach (var card in _cardNodes)
        {
            if (GodotObject.IsInstanceValid(card))
                card.QueueFreeSafely(); // NCard is NodePool-pooled; plain QueueFree would break the pool.
        }
        _cardNodes.Clear();

        if (root != null && GodotObject.IsInstanceValid(root))
        {
            root.QueueFree();
            Log.Write("[UndoPicker] closed");
        }
    }

    /// <summary>
    /// Builds one column of the strip: an optional hover-only highlight label,
    /// the fixed-size holder (card visual or text tile) with a full-cover
    /// transparent click/hover button on top, and the always-visible step-count
    /// label below.
    /// </summary>
    private static Control BuildItem(string context, int steps, uint targetId, bool korean)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);

        var highlight = new Label
        {
            Text = korean ? $"여기까지 물리기 ({steps}수)" : $"Undo through here ({steps})",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
        };
        highlight.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        column.AddChild(highlight);

        var holder = new Control
        {
            CustomMinimumSize = HolderSize,
            Size = HolderSize,
            PivotOffset = HolderSize * 0.5f,
        };
        column.AddChild(holder);

        BuildHolderVisual(holder, context, korean);

        var clickButton = new Button
        {
            Flat = true,
            Text = "",
            FocusMode = Control.FocusModeEnum.None,
        };
        clickButton.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(clickButton); // added last: sits on top of the card/tile visuals.
        clickButton.Pressed += () =>
        {
            Close();
            UndoProtocol.ProposeTarget(targetId);
        };
        clickButton.MouseEntered += () =>
        {
            holder.Scale = new Vector2(1.1f, 1.1f);
            highlight.Visible = true;
        };
        clickButton.MouseExited += () =>
        {
            holder.Scale = Vector2.One;
            highlight.Visible = false;
        };

        var stepLabel = new Label
        {
            Text = korean ? $"{steps}수" : $"{steps} step{(steps > 1 ? "s" : "")}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        column.AddChild(stepLabel);

        return column;
    }

    /// <summary>
    /// Fills the holder with either the real card visual (when the step's context
    /// names a card) or a small text tile describing the step.
    /// </summary>
    private static void BuildHolderVisual(Control holder, string context, bool korean)
    {
        var cardId = TryGetCardModelId(context);
        if (cardId != null)
        {
            var cardModel = ModelDb.GetByIdOrNull<CardModel>(cardId);
            if (cardModel != null)
            {
                var nCard = NCard.Create(cardModel);
                if (nCard != null)
                {
                    nCard.Scale = new Vector2(0.55f, 0.55f);
                    // NCard draws centered on its own origin (see NCardBundle's
                    // center-offset math), so place the origin at the holder center.
                    nCard.Position = HolderSize * 0.5f;
                    holder.AddChild(nCard);
                    _cardNodes.Add(nCard);
                    // UpdateVisuals must run after the node is in the tree, or it
                    // falls back to the "Broken Card" placeholder render.
                    Callable.From(() => nCard.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal)).CallDeferred();
                    return;
                }
            }
            Log.Write($"[UndoPicker] card lookup failed for '{cardId}' (context='{context}')");
        }

        BuildTextTile(holder, context, korean);
    }

    private static void BuildTextTile(Control holder, string context, bool korean)
    {
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        holder.AddChild(panel);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        panel.AddChild(center);

        center.AddChild(new Label
        {
            Text = DescribeAction(context, korean),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(HolderSize.X - 16f, 0f),
        });
    }

    private static bool IsKorean()
    {
        try { return LocManager.Instance.Language == "kor"; }
        catch { return false; }
    }

    /// <summary>
    /// Pulls a "CARD.SOME_ENTRY" token's entry half out of a checksum context
    /// string, e.g. "...card: CARD.STRIKE_IRONCLAD (123) index: 3..." → the
    /// ModelId for CARD.STRIKE_IRONCLAD. Returns null when the context names no card.
    /// </summary>
    private static ModelId? TryGetCardModelId(string context)
    {
        var idx = context.IndexOf("CARD.", StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + 5;
        var end = start;
        while (end < context.Length && (char.IsLetterOrDigit(context[end]) || context[end] == '_'))
            end++;
        if (end == start) return null;
        return new ModelId("CARD", context.Substring(start, end - start));
    }

    /// <summary>
    /// Turn a checksum context string into a short human label.
    /// e.g. "finished action execution PlayCardAction card: CARD.STRIKE_IRONCLAD (123) index: 3 targetid: 2"
    ///   → "STRIKE IRONCLAD", and "After player turn start" → turn-start label.
    /// </summary>
    private static string DescribeAction(string context, bool korean)
    {
        if (context.StartsWith("After player turn start"))
            return korean ? "턴 시작" : "turn start";
        var idx = context.IndexOf("CARD.", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var end = idx + 5;
            while (end < context.Length && (char.IsLetterOrDigit(context[end]) || context[end] == '_'))
                end++;
            return context.Substring(idx + 5, end - idx - 5).Replace('_', ' ');
        }
        if (context.Contains("EndPlayerTurnAction"))
            return korean ? "턴 종료" : "end turn";
        if (context.Contains("UsePotionAction"))
            return korean ? "포션 사용" : "potion use";
        return korean ? "행동" : "action";
    }
}
