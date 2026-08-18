using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.addons.mega_text;

namespace UndoSync;

/// <summary>
/// Step picker shown when the player presses Left Arrow: choose how far back to
/// rewind BEFORE the vote opens (in multiplayer) or the restore happens (in
/// singleplayer).
///
/// Built from the game's own popup chrome (<see cref="NGenericPopup"/> +
/// <see cref="NModalContainer"/> — the same API <c>UndoProtocol</c> uses for its
/// vote/waiting popups) so the frame, title, cancel button and dim all look and
/// behave like native UI. The popup's body label is hidden and replaced with a
/// horizontally-scrolling "card strip": card steps render the actual game card
/// visual (NCard); non-card steps (turn start/end, potion use, etc.) render a
/// real NCard too, built from an arbitrary base card and shown with
/// <see cref="ModelVisibility.NotSeen"/> — the game's own "undiscovered card"
/// look (blurred art, no cost gem, "???" text) — with the title/description
/// overwritten to describe the step. This keeps every tile in the strip a real
/// card frame instead of mixing in a plain panel. Newest-first, left to right.
/// </summary>
internal static class UndoPicker
{
    private const int MaxEntries = 20;
    private const string LocTableName = "main_menu_ui";

    private static NGenericPopup? _popup;
    private static readonly List<NCard> _cardNodes = new();

    // Reflection into NCard's private title/description label fields, used to
    // overwrite the "???" NotSeen text on dummy (non-card-step) tiles after
    // UpdateVisuals() re-writes it. Both fields confirmed in decompiled NCard.cs
    // (_titleLabel : MegaLabel, _descriptionLabel : MegaRichTextLabel).
    private static readonly FieldInfo? FCardTitleLabel = AccessTools.Field(typeof(NCard), "_titleLabel");
    private static readonly FieldInfo? FCardDescriptionLabel = AccessTools.Field(typeof(NCard), "_descriptionLabel");
    private static readonly FieldInfo? FCardTypeLabel = AccessTools.Field(typeof(NCard), "_typeLabel");

    internal static bool IsOpen => _popup != null && GodotObject.IsInstanceValid(_popup);

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
        EnsureLocEntries();

        var popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null)
        {
            Log.Write("[UndoPicker] NGenericPopup.Create() failed or NModalContainer.Instance is null — aborting open");
            return;
        }

        int dumpBudget = 30;
        DumpTree(popup, 0, ref dumpBudget);

        NModalContainer.Instance.Add(popup);
        _popup = popup;

        var task = popup.WaitForConfirmation(
            new LocString(LocTableName, "UNDOSYNC.PICKER_BODY"),
            new LocString(LocTableName, "UNDOSYNC.PICKER_TITLE"),
            null,
            new LocString(LocTableName, "UNDOSYNC.CANCEL"));
        _ = HandleCancelled(task, popup);

        int shown = InjectCardStrip(popup, points, korean);
        Log.Write($"[UndoPicker] opened with {shown} choices (native chrome)");
    }

    internal static void Close()
    {
        var popup = _popup;
        _popup = null;

        foreach (var card in _cardNodes)
        {
            if (GodotObject.IsInstanceValid(card))
                card.QueueFreeSafely(); // NCard is NodePool-pooled; plain QueueFree would break the pool.
        }
        _cardNodes.Clear();

        if (popup != null && GodotObject.IsInstanceValid(popup))
        {
            EnsureModalCleared(popup);
            Log.Write("[UndoPicker] closed");
        }
    }

    // ── Native chrome plumbing ──

    private static void EnsureLocEntries()
    {
        // Re-merge every time: LocManager rebuilds its tables on language change,
        // which silently drops previously merged mod entries.
        try
        {
            var korean = LocManager.Instance.Language == "kor";
            LocManager.Instance.GetTable(LocTableName).MergeWith(korean
                ? new Dictionary<string, string>
                {
                    ["UNDOSYNC.PICKER_TITLE"] = "어느 시점으로 되돌릴까요?",
                    ["UNDOSYNC.PICKER_BODY"] = "",
                    ["UNDOSYNC.CANCEL"] = "취소",
                }
                : new Dictionary<string, string>
                {
                    ["UNDOSYNC.PICKER_TITLE"] = "Undo to which point?",
                    ["UNDOSYNC.PICKER_BODY"] = "",
                    ["UNDOSYNC.CANCEL"] = "Cancel",
                });
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] Loc merge failed: {ex.Message}");
        }
    }

    /// <summary>Awaits the popup's Cancel button; a card pick closes the picker itself first.</summary>
    private static async Task HandleCancelled(Task<bool> task, NGenericPopup popup)
    {
        try { await task; }
        catch { return; }
        if (!ReferenceEquals(_popup, popup)) return; // already closed via a card pick
        Close();
    }

    /// <summary>
    /// NModalContainer.Add() sets OpenModal and shows an input-blocking backstop;
    /// only Clear() resets them. Force-freeing just the popup node leaves the window
    /// dimmed and unclickable, and no future modal can open.
    /// </summary>
    private static void EnsureModalCleared(NGenericPopup popup)
    {
        try
        {
            var container = NModalContainer.Instance;
            if (container != null && ReferenceEquals(container.OpenModal, popup))
                container.Clear(); // frees children, resets OpenModal, hides backstop
            else if (GodotObject.IsInstanceValid(popup))
                popup.QueueFreeSafely();
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] EnsureModalCleared ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic dump of the popup's node tree (depth 3, 30 nodes max) so the
    /// actual body rect and child layout can be read from the log for visual tuning.
    /// </summary>
    private static void DumpTree(Node node, int depth, ref int budget)
    {
        if (budget <= 0 || depth > 3) return;
        budget--;
        string indent = new string(' ', depth * 2);
        Log.Write($"[UndoPicker] tree {indent}{node.Name} : {node.GetType().Name} pos={(node as Control)?.Position} size={(node as Control)?.Size}");
        foreach (Node child in node.GetChildren())
        {
            if (budget <= 0) break;
            DumpTree(child, depth + 1, ref budget);
        }
    }

    // ── Card strip (injected into the popup body) ──

    /// <summary>
    /// Hides the popup's body label and fills its rect with a horizontally
    /// scrolling strip of undo steps, sized to fit. Returns the number shown.
    /// </summary>
    private static int InjectCardStrip(NGenericPopup popup, List<SyncPoint> points, bool korean)
    {
        var vpopup = popup.GetNode<Control>("VerticalPopup");
        var body = vpopup.GetNode<Control>("Description");
        var bodyRect = body.Size;
        body.Visible = false;

        // MegaLabel._Ready() throws InvalidOperationException unless the control already has a
        // "font" theme override set (MegaLabelHelper.AssertThemeFontOverride, MegaLabelHelper.cs:40-47,
        // called from MegaLabel._Ready(), MegaLabel.cs:196 — a deliberate guard against a Godot
        // engine bug, per that method's own doc comment, not something to suppress). Every bare
        // `new MegaLabel` this picker builds itself (stepLabel below, BuildTextTile's label) had no
        // override, so opening the picker logged ~38 of these. Borrow the font straight off the
        // popup's own "Header" label — a native MegaLabel (NVerticalPopup.cs:136) that necessarily
        // already passed this same assertion, so its resolved font is real — and thread it into
        // every MegaLabel we construct so the strip's own text stays visually native.
        var headerLabel = vpopup.GetNodeOrNull<MegaLabel>("Header");
        var stripFont = headerLabel?.GetThemeFont(ThemeConstants.Label.Font)
                         ?? vpopup.GetThemeFont(ThemeConstants.Label.Font);
        if (stripFont == null)
            Log.Write("[UndoPicker] could not resolve a font for strip MegaLabels; theme font override will be skipped");

        // Upper bound leaves room for the step label (~30px) below the holder plus
        // top/bottom breathing room, so the card fills the body instead of the
        // small fixed tile the original 0.55 cap produced.
        // Fit both axes: height leaves room for the step label, width keeps ~2 cards
        // fully visible at once (the rest scroll) so nothing is clipped by the panel.
        const float visibleCards = 2f;
        float scaleY = (bodyRect.Y - 70f) / NCard.defaultSize.Y;
        float scaleX = (bodyRect.X - (visibleCards + 1f) * 16f - visibleCards * 32f)
                       / (visibleCards * NCard.defaultSize.X);
        float scale = Mathf.Clamp(Mathf.Min(scaleY, scaleX), 0.25f, 0.78f);
        // +32 (16px per side) so the card's corner cost gem — drawn outside the
        // card's own rect — doesn't get clipped by the holder bounds.
        var holderSize = NCard.defaultSize * scale + new Vector2(32f, 32f);
        Log.Write($"[UndoPicker] body rect pos={body.Position} size={bodyRect} -> scale={scale} holderSize={holderSize}");

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            Position = body.Position,
            Size = bodyRect,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        vpopup.AddChild(scroll);

        // Fill mode lets the strip take the full body height; Center alignment
        // (this container's primary/horizontal axis) keeps a short strip in the
        // middle of the body instead of pinned to the left edge.
        var hbox = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        hbox.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(hbox);

        // points[0] is the current state; undoing to points[i] rewinds i actions.
        // The oldest action being undone is the one that produced points[i-1].
        int shown = 0;
        for (int i = 1; i < points.Count && i <= MaxEntries; i++)
        {
            string context = points[i - 1].Context;
            // Turn starts stay stored — the turn's first card rewinds to one — but they
            // are not a move the player made, so instead of an entry they become a
            // plain divider marking where the turn changed.
            if (context.StartsWith("After player turn start"))
            {
                hbox.AddChild(BuildTurnSeparator(holderSize));
                continue;
            }
            hbox.AddChild(BuildItem(context, i, points[i], korean, holderSize, scale, stripFont));
            shown++;
        }
        return shown;
    }

    /// <summary>
    /// Builds one column of the strip: the fixed-size holder (card visual or
    /// text tile) with a full-cover transparent click/hover button on top, and
    /// the step-count label below that swaps to a hover prompt on MouseEntered.
    /// Column Alignment centers the (shorter) holder+label stack vertically
    /// within the full-height column the parent HBox stretches it to.
    /// </summary>
    /// <summary>A thin vertical rule marking a turn change between two step tiles.</summary>
    private static Control BuildTurnSeparator(Vector2 holderSize)
    {
        var column = new Control
        {
            CustomMinimumSize = new Vector2(10f, holderSize.Y),
            Size = new Vector2(10f, holderSize.Y),
        };
        var line = new ColorRect
        {
            Color = new Color(0.85f, 0.80f, 0.68f, 0.35f),
            Position = new Vector2(4f, holderSize.Y * 0.12f),
            Size = new Vector2(2f, holderSize.Y * 0.76f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        column.AddChild(line);
        return column;
    }

    private static Control BuildItem(string context, int steps, SyncPoint target, bool korean, Vector2 holderSize, float scale, Font? font)
    {
        var column = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        column.AddThemeConstantOverride("separation", 4);

        var holder = new Control
        {
            CustomMinimumSize = holderSize,
            Size = holderSize,
            PivotOffset = holderSize * 0.5f,
        };
        column.AddChild(holder);

        BuildHolderVisual(holder, context, target, korean, holderSize, scale, font);

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
            UndoProtocol.ProposeTarget(target.ChecksumId);
        };

        var stepLabel = new MegaLabel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // See InjectCardStrip: MegaLabel throws in _Ready() without a "font" theme override, and
        // _Ready() runs on tree entry — this must land before stepLabel is added anywhere below.
        if (font != null)
            stepLabel.AddThemeFontOverride(ThemeConstants.Label.Font, font);
        // A step count in the label reads in Korean as "move #N" ("2수 취소" =
        // "cancel move #2"), not "undo N moves back" — the meaning we actually want.
        // Any number here gets misread the same way, so drop it entirely; hover just
        // recolors gold to signal the tile is clickable. steps stays logged below.
        Log.Write($"[UndoPicker] item steps={steps} targetId={target.ChecksumId} context='{context}'");
        // Newest-first strip: clicking a card cancels it and everything newer — i.e.
        // everything from the clicked card up to now — so "up to here", not "from here".
        string normalText = korean ? "여기까지 취소" : "undo up to here";
        string hoverText = normalText;
        stepLabel.SetTextAutoSize(normalText);
        column.AddChild(stepLabel);

        clickButton.MouseEntered += () =>
        {
            holder.Scale = new Vector2(1.06f, 1.06f);
            stepLabel.SetTextAutoSize(hoverText);
            stepLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f));
        };
        clickButton.MouseExited += () =>
        {
            holder.Scale = Vector2.One;
            stepLabel.SetTextAutoSize(normalText);
            stepLabel.RemoveThemeColorOverride("font_color"); // reverts to the theme's default font_color.
        };

        return column;
    }

    /// <summary>
    /// Fills the holder with either the real card visual (when the step's context
    /// names a card) or a small text tile describing the step.
    /// </summary>
    private static void BuildHolderVisual(Control holder, string context, SyncPoint before, bool korean, Vector2 holderSize, float scale, Font? font)
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
                    nCard.Scale = new Vector2(scale, scale);
                    // NCard draws centered on its own origin (see NCardBundle's
                    // center-offset math), so place the origin at the holder center.
                    nCard.Position = holderSize * 0.5f;
                    holder.AddChild(nCard);
                    _cardNodes.Add(nCard);
                    // UpdateVisuals must run after the node is in the tree, or it
                    // falls back to the "Broken Card" placeholder render.
                    // PileType.None (not Hand) — Hand makes NCard treat the card as a
                    // live playable-in-hand instance and run Model.CanPlay(), which
                    // flags this out-of-combat picker copy as unplayable and draws the
                    // red "can't play" icon over the cost gem. None is what every other
                    // read-only preview context uses (NInspectCardScreen, NMerchantCard,
                    // NCardBundle, selection screens, ...) and skips that check.
                    Callable.From(() => nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal)).CallDeferred();
                    return;
                }
            }
            Log.Write($"[UndoPicker] card lookup failed for '{cardId}' (context='{context}')");
        }

        if (BuildDummyCardTile(holder, context, before, korean, holderSize, scale))
            return;

        BuildTextTile(holder, context, holderSize, korean, font);
    }

    /// <summary>
    /// Card-shaped dummy tile for non-card steps (turn start/end, potion use, ...):
    /// a real <see cref="NCard"/> built from an arbitrary base card model and shown
    /// with <see cref="ModelVisibility.NotSeen"/> — the same rendering the game uses
    /// for undiscovered cards in the library (real card frame, blurred portrait, no
    /// cost gem, "???" title/description). The title/description are then
    /// overwritten with the step's own label so the tile reads as a system tile
    /// instead of a mystery card, while still looking like a native card among the
    /// real card-step tiles in the strip.
    ///
    /// Placed and pooled identically to a real card-step tile (same Scale, same
    /// origin-centered Position, added to <see cref="_cardNodes"/> so <see cref="Close"/>
    /// returns it to the NCard pool via QueueFreeSafely()).
    ///
    /// Returns false (caller falls back to <see cref="BuildTextTile"/>) when no base
    /// card model is available or <see cref="NCard.Create"/> fails — this is the
    /// pre-existing plain-panel tile and stays as the safety net.
    /// </summary>
    private static bool BuildDummyCardTile(Control holder, string context, SyncPoint before, bool korean, Vector2 holderSize, float scale)
    {
        // The base card decides the frame: NCard takes its banner texture/material
        // straight off Model (NCard.cs:1255) and rarity drives it, so an arbitrary
        // pick can hand us a gold rare banner. Prefer the plainest frame there is —
        // a Basic card (Strike/Defend), the same silver banner real steps show —
        // and fall back through Common before giving up on determinism.
        var baseModel = ModelDb.AllCards.FirstOrDefault(c => c.Rarity == CardRarity.Basic)
                        ?? ModelDb.AllCards.FirstOrDefault(c => c.Rarity == CardRarity.Common)
                        ?? ModelDb.AllCards.FirstOrDefault();
        if (baseModel == null)
        {
            Log.Write("[UndoPicker] ModelDb.AllCards is empty; falling back to text tile");
            return false;
        }
        Log.Write($"[UndoPicker] dummy card base = {baseModel.Id} rarity={baseModel.Rarity} type={baseModel.Type}");

        var nCard = NCard.Create(baseModel, ModelVisibility.NotSeen);
        if (nCard == null)
        {
            Log.Write("[UndoPicker] NCard.Create() returned null for dummy tile; falling back to text tile");
            return false;
        }

        nCard.Scale = new Vector2(scale, scale);
        // NCard draws centered on its own origin (see NCardBundle's center-offset
        // math), so place the origin at the holder center — same as a real card step.
        nCard.Position = holderSize * 0.5f;
        holder.AddChild(nCard);
        _cardNodes.Add(nCard);

        string label = DescribeAction(context, korean);
        string typePlaque = korean ? "시스템" : "system";
        // Resolved from the snapshot BEFORE this step, not the context string — see
        // TryGetPotionName for why the context alone can't name the potion.
        string description = TryGetPotionName(context, before) ?? "";
        // UpdateVisuals must run after the node is in the tree (see the real-card
        // path above), and the title/description overwrite must run strictly after
        // it, since UpdateVisuals() is what writes the "???" NotSeen text in the
        // first place. One deferred call keeps that ordering.
        Callable.From(() =>
        {
            nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            OverwriteDummyCardText(nCard, label, typePlaque, description);
        }).CallDeferred();

        return true;
    }

    /// <summary>
    /// Overwrites the NotSeen "???" title/description NCard just wrote in
    /// UpdateVisuals() with the undo step's own label. Both target fields are
    /// private on NCard, so this goes through reflection; any failure (missing
    /// field, unexpected type) is logged and swallowed — a card stuck on "???" is
    /// a cosmetic miss, not worth failing the whole picker over.
    /// </summary>
    private static void OverwriteDummyCardText(NCard nCard, string label, string typePlaque, string description)
    {
        try
        {
            if (FCardTitleLabel?.GetValue(nCard) is MegaLabel titleLabel)
                titleLabel.SetTextAutoSize(label);
            else
                Log.Write("[UndoPicker] dummy card _titleLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] dummy card title overwrite failed: {ex.Message}");
        }

        try
        {
            if (FCardDescriptionLabel?.GetValue(nCard) is MegaRichTextLabel descriptionLabel)
                descriptionLabel.SetTextAutoSize(description);
            else
                Log.Write("[UndoPicker] dummy card _descriptionLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] dummy card description overwrite failed: {ex.Message}");
        }

        try
        {
            // The type plaque ("공격"/"스킬"/"파워") is written from Model.Type at
            // NCard.cs:1270 — on a dummy tile the base card's type is meaningless,
            // so relabel it as a system step.
            if (FCardTypeLabel?.GetValue(nCard) is MegaLabel typeLabel)
                typeLabel.SetTextAutoSize(typePlaque);
            else
                Log.Write("[UndoPicker] dummy card _typeLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] dummy card type overwrite failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Card-shaped dummy tile fallback for non-card steps: a themed PanelContainer
    /// (the popup tree gives it the game's panel stylebox via inherited Theme)
    /// sized exactly like a real card holder, with the step's label centered
    /// inside via CenterContainer. Only used when <see cref="BuildDummyCardTile"/>
    /// can't produce a real NCard (no base card model, or NCard.Create fails).
    /// </summary>
    private static void BuildTextTile(Control holder, string context, Vector2 holderSize, bool korean, Font? font)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = holderSize,
            Size = holderSize,
        };
        holder.AddChild(panel);

        var center = new CenterContainer();
        panel.AddChild(center);

        var label = new MegaLabel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        // See InjectCardStrip: MegaLabel throws in _Ready() without a "font" theme override, and
        // _Ready() runs on tree entry — this must land before label is added anywhere below.
        if (font != null)
            label.AddThemeFontOverride(ThemeConstants.Label.Font, font);
        center.AddChild(label);
        label.SetTextAutoSize(DescribeAction(context, korean));
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
    /// The potion's display name for a potion step, resolved from the state BEFORE the step:
    /// UsePotionAction.ToString() runs after the potion has already left the belt, so the
    /// context string carries only the player's NetId and the belt slot index, never the name.
    /// Returns null when the context is not a potion step or the potion cannot be resolved.
    /// </summary>
    private static string? TryGetPotionName(string context, SyncPoint before)
    {
        try
        {
            // Two context shapes carry a potion identity (verified in UsePotionAction.ToString()
            // and DiscardPotionGameAction.ToString()); everything else is not a potion step.
            string netIdMarker, slotMarker;
            if (context.Contains("UsePotionAction "))
            {
                netIdMarker = "UsePotionAction ";
                slotMarker = "index: ";
            }
            else if (context.Contains("DiscardPotionGameAction"))
            {
                netIdMarker = "for player ";
                slotMarker = "potion slot: ";
            }
            else
            {
                return null;
            }

            if (!ulong.TryParse(TokenAfter(context, netIdMarker), out var netId)) return null;
            if (!int.TryParse(TokenAfter(context, slotMarker), out var slot)) return null;

            return before.Snapshot.GetPotionAtSlot(netId, slot)?.Title.GetFormattedText();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The whitespace-delimited token right after the first occurrence of marker, or null.</summary>
    private static string? TokenAfter(string s, string marker)
    {
        var idx = s.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        var end = start;
        while (end < s.Length && !char.IsWhiteSpace(s[end]))
            end++;
        return end > start ? s.Substring(start, end - start) : null;
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
