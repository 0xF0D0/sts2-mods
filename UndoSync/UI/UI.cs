using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.addons.mega_text;

namespace UndoSync;

internal static class UI
{
    private sealed record PickerPoint(int EventCount, string Context);
    private static string? _networkView;

    private const int EntriesPerPage = 8;
    private const string LocTableName = "main_menu_ui";

    private static NGenericPopup? _popup;
    private static readonly List<NCard> _cardNodes = new();
    private static HBoxContainer? _pageBox;
    private static Button? _previousPageButton;
    private static Button? _nextPageButton;
    private static Button? _pageIndicator;
    private static List<PickerPoint>? _pagePoints;
    private static bool _pageKorean;
    private static Vector2 _pageHolderSize;
    private static float _pageScale;
    private static Font? _pageFont;
    private static int _pageIndex;

    private static readonly FieldInfo? FCardTitleLabel = AccessTools.Field(typeof(NCard), "_titleLabel");
    private static readonly FieldInfo? FCardDescriptionLabel = AccessTools.Field(typeof(NCard), "_descriptionLabel");
    private static readonly FieldInfo? FCardTypeLabel = AccessTools.Field(typeof(NCard), "_typeLabel");

    private static readonly FieldInfo? FPopupVisuals = AccessTools.Field(typeof(NPopupYesNoButton), "_visuals");
    private static readonly FieldInfo? FPopupOutline = AccessTools.Field(typeof(NPopupYesNoButton), "_outline");
    private static readonly FieldInfo? FPopupImage = AccessTools.Field(typeof(NPopupYesNoButton), "_image");
    private static readonly FieldInfo? FPopupLabel = AccessTools.Field(typeof(NPopupYesNoButton), "_label");
    private static readonly FieldInfo? FPopupHsv = AccessTools.Field(typeof(NPopupYesNoButton), "_hsv");
    private static readonly FieldInfo? FPopupOutlineMaterial = AccessTools.Field(typeof(NPopupYesNoButton), "_outlineMaterial");
    private static bool _popupReadinessReflectionWarningLogged;

    internal static bool IsOpen => _popup != null && GodotObject.IsInstanceValid(_popup);

    internal static NGenericPopup? CurrentPopup => _popup;

    internal static void Open()
    {
        if (Network.Busy || !Hooks.IsIdle() || !History.Available) return;
        var points = new List<PickerPoint> { new(History.Count, "") };
        points.AddRange(History.Targets.Select(t => new PickerPoint(t.EventCount, "")));
        for (int i = 0; i + 1 < points.Count; i++)
            points[i] = points[i] with { Context = History.DescribeRange(points[i + 1].EventCount, points[i].EventCount) };
        if (points.Count < 2)
        {
            Hooks.Log($"[UndoPicker] nothing to undo (sync points={points.Count})");
            return;
        }
        Close();

        bool korean = IsKorean();
        EnsureLocEntries();

        var popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null)
        {
            Hooks.Log("[UndoPicker] NGenericPopup.Create() failed or NModalContainer.Instance is null — aborting open");
            return;
        }

        int dumpBudget = 30;
        DumpTree(popup, 0, ref dumpBudget);

        NModalContainer.Instance.Add(popup);
        _popup = popup;

        if (!ArePopupControlsReady(popup))
        {
            Close();
            return;
        }

        var combatStart = points.FirstOrDefault(p => p.EventCount == 0);
        bool hasCombatStart = combatStart != null;
        var task = popup.WaitForConfirmation(
            new LocString(LocTableName, "UNDOSYNC.PICKER_BODY"),
            new LocString(LocTableName, "UNDOSYNC.PICKER_TITLE"),
            hasCombatStart ? new LocString(LocTableName, "UNDOSYNC.RESTART_COMBAT") : null,
            new LocString(LocTableName, "UNDOSYNC.CANCEL"));
        _ = HandlePopupResult(task, popup, hasCombatStart ? combatStart : null);

        int shown = InjectCardStrip(popup, points, korean);
        int pages = (shown + EntriesPerPage - 1) / EntriesPerPage;
        Hooks.Log($"[UndoPicker] opened with {shown} choices across {pages} pages (native chrome), "
                  + $"renderWindow={EntriesPerPage}, restartCombat={hasCombatStart}");
    }

    internal static void Close()
    {
        var popup = _popup;
        _popup = null;
        _networkView = null;

        foreach (var card in _cardNodes)
        {
            if (GodotObject.IsInstanceValid(card))
                card.QueueFreeSafely(); // NCard is NodePool-pooled; plain QueueFree would break the pool.
        }
        _cardNodes.Clear();
        _pageBox = null;
        _previousPageButton = null;
        _nextPageButton = null;
        _pageIndicator = null;
        _pagePoints = null;
        _pageIndex = 0;
        _pageFont = null;

        if (popup != null && GodotObject.IsInstanceValid(popup))
        {
            EnsureModalCleared(popup);
            Hooks.Log("[UndoPicker] closed");
        }
    }


    internal static void Tick()
    {
        if (Network.Executing)
        {
            if (IsOpen) Close();
            return;
        }
        if (Network.Busy)
        {
            string view = Network.LocalVoted ? "waiting" : "vote";
            if (_networkView != view) ShowNetworkPopup(view);
        }
        else if (_networkView != null || IsOpen && !Hooks.IsIdle()) Close();
    }

    private static void ShowNetworkPopup(string view)
    {
        if (NModalContainer.Instance == null) return;
        if (!IsOpen && NModalContainer.Instance.OpenModal != null) return;
        Close();
        EnsureLocEntries();
        var popup = NGenericPopup.Create();
        if (popup == null) return;
        NModalContainer.Instance.Add(popup);
        _popup = popup;
        if (!ArePopupControlsReady(popup)) { Close(); return; }
        _networkView = view;
        Task<bool> answer;
        if (view == "vote")
        {
            var body = new LocString(LocTableName, "UNDOSYNC.PROPOSAL_BODY");
            body.Add("steps", History.Targets.Count(t => t.EventCount >= Network.Target));
            answer = popup.WaitForConfirmation(body,
                new LocString(LocTableName, "UNDOSYNC.PROPOSAL_TITLE"),
                new LocString(LocTableName, "UNDOSYNC.REJECT"),
                new LocString(LocTableName, "UNDOSYNC.ACCEPT"));
        }
        else
            answer = popup.WaitForConfirmation(
                new LocString(LocTableName, "UNDOSYNC.WAITING_BODY"),
                new LocString(LocTableName, "UNDOSYNC.WAITING_TITLE"), null,
                new LocString(LocTableName, "UNDOSYNC.CANCEL"));
        _ = HandleNetworkAnswer(answer, popup, view);
        Hooks.Log($"UI_{view.ToUpperInvariant()} target={Network.Target}");
    }

    private static async Task HandleNetworkAnswer(Task<bool> answer, NGenericPopup popup, string view)
    {
        bool accepted;
        try { accepted = await answer; }
        catch { return; }
        if (!ReferenceEquals(_popup, popup)) return;
        Close();
        // The native button's remaining Released callback clears its modal.
        // Tick opens the next popup only after that signal has unwound.
        if (view == "vote") Network.Vote(accepted);
        else Network.Fail("Undo cancelled by a player.");
    }

    private static void EnsureLocEntries()
    {
        try
        {
            var korean = LocManager.Instance.Language == "kor";
            LocManager.Instance.GetTable(LocTableName).MergeWith(korean
                ? new Dictionary<string, string>
                {
                    ["UNDOSYNC.PICKER_TITLE"] = "어느 시점으로 되돌릴까요?",
                    ["UNDOSYNC.PROPOSAL_TITLE"] = "되돌리기 제안",
                    ["UNDOSYNC.PROPOSAL_BODY"] = "{steps}수를 되돌리려 합니다.\n수락하시겠습니까?",
                    ["UNDOSYNC.WAITING_TITLE"] = "되돌리기 제안 중",
                    ["UNDOSYNC.WAITING_BODY"] = "다른 플레이어의 수락을 기다리는 중...",
                    ["UNDOSYNC.ACCEPT"] = "수락",
                    ["UNDOSYNC.REJECT"] = "거절",
                    ["UNDOSYNC.PICKER_BODY"] = "",
                    ["UNDOSYNC.CANCEL"] = "취소",
                    ["UNDOSYNC.RESTART_COMBAT"] = "전투 처음부터",
                    ["UNDOSYNC.RESTART_CONFIRM_TITLE"] = "전투 재시작",
                    ["UNDOSYNC.RESTART_CONFIRM_BODY"] = "지금까지의 전투 진행이 모두 사라지고 첫 턴으로 돌아갑니다.",
                }
                : new Dictionary<string, string>
                {
                    ["UNDOSYNC.PICKER_TITLE"] = "Undo to which point?",
                    ["UNDOSYNC.PROPOSAL_TITLE"] = "Undo Proposal",
                    ["UNDOSYNC.PROPOSAL_BODY"] = "Undo {steps} action(s).\nAccept?",
                    ["UNDOSYNC.WAITING_TITLE"] = "Undo Proposed",
                    ["UNDOSYNC.WAITING_BODY"] = "Waiting for other players to accept...",
                    ["UNDOSYNC.ACCEPT"] = "Accept",
                    ["UNDOSYNC.REJECT"] = "Reject",
                    ["UNDOSYNC.PICKER_BODY"] = "",
                    ["UNDOSYNC.CANCEL"] = "Cancel",
                    ["UNDOSYNC.RESTART_COMBAT"] = "restart combat",
                    ["UNDOSYNC.RESTART_CONFIRM_TITLE"] = "Restart Combat",
                    ["UNDOSYNC.RESTART_CONFIRM_BODY"] = "This discards everything that happened in this fight and returns to its first turn.",
                });
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] Loc merge failed: {ex.Message}");
        }
    }

    private static async Task HandlePopupResult(Task<bool> task, NGenericPopup popup, PickerPoint? combatStart)
    {
        bool cancelled;
        try { cancelled = await task; }
        catch { return; }
        if (!ReferenceEquals(_popup, popup)) return; // already closed via a card pick
        Close();

        if (cancelled)
        {
            Hooks.Log("[UndoPicker] cancel button pressed, closing without proposing");
            return;
        }
        if (combatStart == null)
        {
            Hooks.Log("[UndoPicker] WARN: no-button result but combatStart was null — ignoring");
            return;
        }
        Hooks.Log($"[UndoPicker] restart-combat button pressed, targetId={combatStart.EventCount}");
        _ = OpenRestartConfirmationNextFrame(combatStart.EventCount);
    }

    private static async Task OpenRestartConfirmationNextFrame(int targetChecksumId)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            if (tree == null)
            {
                Hooks.Log("[UndoPicker] restart-combat confirmation skipped: NGame.Instance/GetTree() was null");
                return;
            }
            await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await ConfirmRestartCombat(targetChecksumId);
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] restart-combat confirmation continuation ERROR: {ex}");
        }
    }

    internal static async Task ConfirmRestartCombat(int targetChecksumId)
    {
        EnsureLocEntries();
        var popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null)
        {
            Hooks.Log("[UndoPicker] ConfirmRestartCombat: NGenericPopup.Create() failed or NModalContainer.Instance is null — no proposal sent");
            return;
        }
        Close(); // guarantees a clear modal slot before Add(); no-op here, see doc comment above.
        NModalContainer.Instance.Add(popup);
        _popup = popup;
        bool confirmed;
        try
        {
            if (!ArePopupControlsReady(popup))
            {
                Close();
                return;
            }
            confirmed = await popup.WaitForConfirmation(
                new LocString(LocTableName, "UNDOSYNC.RESTART_CONFIRM_BODY"),
                new LocString(LocTableName, "UNDOSYNC.RESTART_CONFIRM_TITLE"),
                new LocString(LocTableName, "UNDOSYNC.CANCEL"),
                new LocString(LocTableName, "UNDOSYNC.RESTART_COMBAT"));
        }
        catch
        {
            return;
        }
        if (!ReferenceEquals(_popup, popup)) return; // force-closed elsewhere (e.g. combat ended mid-confirm)
        Close();

        if (!confirmed)
        {
            Hooks.Log("[UndoPicker] restart-combat confirmation cancelled, closing without proposing");
            return;
        }
        Hooks.Log($"[UndoPicker] restart-combat confirmed, targetId={targetChecksumId}");
        Network.Request(targetChecksumId);
    }

    private static bool ArePopupControlsReady(NGenericPopup popup)
    {
        if (!GodotObject.IsInstanceValid(popup) || !popup.IsInsideTree())
            return false;
        if (FPopupVisuals == null || FPopupOutline == null || FPopupImage == null || FPopupLabel == null
            || FPopupHsv == null || FPopupOutlineMaterial == null)
        {
            if (!_popupReadinessReflectionWarningLogged)
            {
                _popupReadinessReflectionWarningLogged = true;
                Hooks.Log("[UndoPicker] native button readiness reflection unavailable for NPopupYesNoButton.cs:154-170");
            }
            return false;
        }
        try
        {
            var vertical = popup.GetNodeOrNull<Control>("VerticalPopup");
            var yes = vertical?.GetNodeOrNull<NPopupYesNoButton>("YesButton");
            var no = vertical?.GetNodeOrNull<NPopupYesNoButton>("NoButton");
            return vertical != null && yes != null && no != null
                && vertical.IsInsideTree() && yes.IsInsideTree() && no.IsInsideTree()
                && NativeButtonFieldsReady(yes) && NativeButtonFieldsReady(no);
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] native button readiness reflection check ERROR: {ex.Message}");
            return false;
        }
    }

    private static bool NativeButtonFieldsReady(NPopupYesNoButton button)
    {
        foreach (var field in new[]
        {
            FPopupVisuals, FPopupOutline, FPopupImage, FPopupLabel, FPopupHsv, FPopupOutlineMaterial,
        })
        {
            if (field?.GetValue(button) is not GodotObject value || !GodotObject.IsInstanceValid(value))
                return false;
        }
        return true;
    }

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
            Hooks.Log($"[UndoPicker] EnsureModalCleared ERROR: {ex.Message}");
        }
    }

    private static void DumpTree(Node node, int depth, ref int budget)
    {
        if (budget <= 0 || depth > 3) return;
        budget--;
        string indent = new string(' ', depth * 2);
        Hooks.Log($"[UndoPicker] tree {indent}{node.Name} : {node.GetType().Name} pos={(node as Control)?.Position} size={(node as Control)?.Size}");
        foreach (Node child in node.GetChildren())
        {
            if (budget <= 0) break;
            DumpTree(child, depth + 1, ref budget);
        }
    }


    private static int InjectCardStrip(NGenericPopup popup, List<PickerPoint> points, bool korean)
    {
        var vpopup = popup.GetNode<Control>("VerticalPopup");
        var body = vpopup.GetNode<Control>("Description");
        var bodyRect = body.Size;
        body.Visible = false;

        var headerLabel = vpopup.GetNodeOrNull<MegaLabel>("Header");
        var stripFont = headerLabel?.GetThemeFont(ThemeConstants.Label.Font)
                         ?? vpopup.GetThemeFont(ThemeConstants.Label.Font);
        if (stripFont == null)
            Hooks.Log("[UndoPicker] could not resolve a font for strip MegaLabels; theme font override will be skipped");

        const float visibleCards = 2f;
        float scaleY = (bodyRect.Y - 70f) / NCard.defaultSize.Y;
        float scaleX = (bodyRect.X - (visibleCards + 1f) * 16f - visibleCards * 32f)
                       / (visibleCards * NCard.defaultSize.X);
        float scale = Mathf.Clamp(Mathf.Min(scaleY, scaleX), 0.25f, 0.78f);
        var holderSize = NCard.defaultSize * scale + new Vector2(32f, 32f);
        Hooks.Log($"[UndoPicker] body rect pos={body.Position} size={bodyRect} -> scale={scale} holderSize={holderSize}");

        float navigationHeight = 48f;
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            Position = body.Position,
            Size = new Vector2(bodyRect.X, Mathf.Max(1f, bodyRect.Y - navigationHeight)),
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        vpopup.AddChild(scroll);

        var hbox = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsVertical = Control.SizeFlags.Fill,
        };
        hbox.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(hbox);

        var navigation = new HBoxContainer
        {
            Position = new Vector2(body.Position.X, body.Position.Y + bodyRect.Y - navigationHeight),
            Size = new Vector2(bodyRect.X, navigationHeight),
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        navigation.AddThemeConstantOverride("separation", 18);
        vpopup.AddChild(navigation);

        var previous = new Button { Text = "‹", CustomMinimumSize = new Vector2(56f, 40f) };
        var indicator = new Button { Disabled = true, CustomMinimumSize = new Vector2(96f, 40f) };
        var next = new Button { Text = "›", CustomMinimumSize = new Vector2(56f, 40f) };
        navigation.AddChild(previous);
        navigation.AddChild(indicator);
        navigation.AddChild(next);

        _pageBox = hbox;
        _previousPageButton = previous;
        _nextPageButton = next;
        _pageIndicator = indicator;
        _pagePoints = points;
        _pageKorean = korean;
        _pageHolderSize = holderSize;
        _pageScale = scale;
        _pageFont = stripFont;
        _pageIndex = 0;

        previous.Pressed += () => ShowPage(popup, _pageIndex - 1);
        next.Pressed += () => ShowPage(popup, _pageIndex + 1);
        ShowPage(popup, 0);
        return points.Count - 1;
    }

    private static void ShowPage(NGenericPopup popup, int pageIndex)
    {
        if (!ReferenceEquals(_popup, popup) || _pageBox == null || _pagePoints == null)
            return;

        int choiceCount = _pagePoints.Count - 1;
        int pageCount = (choiceCount + EntriesPerPage - 1) / EntriesPerPage;
        if (pageCount <= 0) return;
        _pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);

        foreach (var card in _cardNodes)
        {
            if (GodotObject.IsInstanceValid(card))
                card.QueueFreeSafely();
        }
        _cardNodes.Clear();
        foreach (Node child in _pageBox.GetChildren())
        {
            _pageBox.RemoveChild(child);
            child.QueueFreeSafely();
        }

        int first = 1 + _pageIndex * EntriesPerPage;
        int lastExclusive = Math.Min(_pagePoints.Count, first + EntriesPerPage);
        for (int i = first; i < lastExclusive; i++)
        {
            if (_pagePoints[i - 1].Context.StartsWith("After player turn start"))
                _pageBox.AddChild(BuildTurnSeparator(_pageHolderSize));
            _pageBox.AddChild(BuildItem(_pagePoints[i - 1].Context, i, _pagePoints[i],
                _pageKorean, _pageHolderSize, _pageScale, _pageFont));
        }

        if (_previousPageButton != null)
            _previousPageButton.Disabled = _pageIndex == 0;
        if (_nextPageButton != null)
            _nextPageButton.Disabled = _pageIndex == pageCount - 1;
        if (_pageIndicator != null)
            _pageIndicator.Text = _pageKorean
                ? $"{_pageIndex + 1} / {pageCount}"
                : $"page {_pageIndex + 1} / {pageCount}";
        Hooks.Log($"[UndoPicker] rendered page={_pageIndex + 1}/{pageCount} "
                  + $"choices={first}-{lastExclusive - 1} ncardNodes={_cardNodes.Count}");
    }

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

    private static Control BuildItem(string context, int steps, PickerPoint target, bool korean, Vector2 holderSize, float scale, Font? font)
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

        BuildHolderVisual(holder, context, korean, holderSize, scale, font);

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
            Network.Request(target.EventCount);
        };

        var stepLabel = new MegaLabel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (font != null)
            stepLabel.AddThemeFontOverride(ThemeConstants.Label.Font, font);
        Hooks.Log($"[UndoPicker] item steps={steps} targetId={target.EventCount} context='{context}'");
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

    private static void BuildHolderVisual(Control holder, string context, bool korean, Vector2 holderSize, float scale, Font? font)
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
                    nCard.Position = holderSize * 0.5f;
                    holder.AddChild(nCard);
                    _cardNodes.Add(nCard);
                    Callable.From(() => nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal)).CallDeferred();
                    return;
                }
            }
            Hooks.Log($"[UndoPicker] card lookup failed for '{cardId}' (context='{context}')");
        }

        if (BuildDummyCardTile(holder, context, korean, holderSize, scale))
            return;

        BuildTextTile(holder, context, holderSize, korean, font);
    }

    private static bool BuildDummyCardTile(Control holder, string context, bool korean, Vector2 holderSize, float scale)
    {
        var baseModel = ModelDb.AllCards.FirstOrDefault(c => c.Rarity == CardRarity.Basic)
                        ?? ModelDb.AllCards.FirstOrDefault(c => c.Rarity == CardRarity.Common)
                        ?? ModelDb.AllCards.FirstOrDefault();
        if (baseModel == null)
        {
            Hooks.Log("[UndoPicker] ModelDb.AllCards is empty; falling back to text tile");
            return false;
        }
        Hooks.Log($"[UndoPicker] dummy card base = {baseModel.Id} rarity={baseModel.Rarity} type={baseModel.Type}");

        var nCard = NCard.Create(baseModel, ModelVisibility.NotSeen);
        if (nCard == null)
        {
            Hooks.Log("[UndoPicker] NCard.Create() returned null for dummy tile; falling back to text tile");
            return false;
        }

        nCard.Scale = new Vector2(scale, scale);
        nCard.Position = holderSize * 0.5f;
        holder.AddChild(nCard);
        _cardNodes.Add(nCard);

        string label = DescribeAction(context, korean);
        string typePlaque = korean ? "시스템" : "system";
        string description = TryGetPotionName(context) ?? "";
        Callable.From(() =>
        {
            nCard.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            OverwriteDummyCardText(nCard, label, typePlaque, description);
        }).CallDeferred();

        return true;
    }

    private static void OverwriteDummyCardText(NCard nCard, string label, string typePlaque, string description)
    {
        try
        {
            if (FCardTitleLabel?.GetValue(nCard) is MegaLabel titleLabel)
                titleLabel.SetTextAutoSize(label);
            else
                Hooks.Log("[UndoPicker] dummy card _titleLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] dummy card title overwrite failed: {ex.Message}");
        }

        try
        {
            if (FCardDescriptionLabel?.GetValue(nCard) is MegaRichTextLabel descriptionLabel)
                descriptionLabel.SetTextAutoSize(description);
            else
                Hooks.Log("[UndoPicker] dummy card _descriptionLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] dummy card description overwrite failed: {ex.Message}");
        }

        try
        {
            if (FCardTypeLabel?.GetValue(nCard) is MegaLabel typeLabel)
                typeLabel.SetTextAutoSize(typePlaque);
            else
                Hooks.Log("[UndoPicker] dummy card _typeLabel reflection missed (null field or wrong type)");
        }
        catch (Exception ex)
        {
            Hooks.Log($"[UndoPicker] dummy card type overwrite failed: {ex.Message}");
        }
    }

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

    private static string? TryGetPotionName(string context)
    {
        return null;
    }

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
