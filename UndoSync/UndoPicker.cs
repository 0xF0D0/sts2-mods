using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
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
    internal readonly record struct PaginationPageDiagnostics(
        int Page,
        int PageCount,
        int FirstChoice,
        int LastChoice,
        int CardCount);

    internal readonly record struct PaginationProbeResult(
        bool Passed,
        bool ChoicesExceedPage,
        bool NextButtonAdvanced,
        bool ClosedWithoutSelection,
        int TotalChoices,
        PaginationPageDiagnostics FirstPage,
        PaginationPageDiagnostics SecondPage,
        string Detail);

    // Keep the picker bounded without making older native sync points unreachable.
    // A page is deliberately small because NCard is a real, relatively heavy game
    // preview node; navigation reuses the same strip and only renders this many.
    private const int EntriesPerPage = 8;
    private const string LocTableName = "main_menu_ui";

    private static NGenericPopup? _popup;
    private static readonly List<NCard> _cardNodes = new();
    private static HBoxContainer? _pageBox;
    private static Button? _previousPageButton;
    private static Button? _nextPageButton;
    private static Button? _pageIndicator;
    private static List<SyncPoint>? _pagePoints;
    private static bool _pageKorean;
    private static Vector2 _pageHolderSize;
    private static float _pageScale;
    private static Font? _pageFont;
    private static int _pageIndex;

    // Reflection into NCard's private title/description label fields, used to
    // overwrite the "???" NotSeen text on dummy (non-card-step) tiles after
    // UpdateVisuals() re-writes it. Both fields confirmed in decompiled NCard.cs
    // (_titleLabel : MegaLabel, _descriptionLabel : MegaRichTextLabel).
    private static readonly FieldInfo? FCardTitleLabel = AccessTools.Field(typeof(NCard), "_titleLabel");
    private static readonly FieldInfo? FCardDescriptionLabel = AccessTools.Field(typeof(NCard), "_descriptionLabel");
    private static readonly FieldInfo? FCardTypeLabel = AccessTools.Field(typeof(NCard), "_typeLabel");

    // Verified against decompiled NPopupYesNoButton.cs:154-170 and :212-222. These are the
    // fields initialized by _Ready and subsequently dereferenced by SetText/IsYes/actions.
    private static readonly FieldInfo? FPopupVisuals = AccessTools.Field(typeof(NPopupYesNoButton), "_visuals");
    private static readonly FieldInfo? FPopupOutline = AccessTools.Field(typeof(NPopupYesNoButton), "_outline");
    private static readonly FieldInfo? FPopupImage = AccessTools.Field(typeof(NPopupYesNoButton), "_image");
    private static readonly FieldInfo? FPopupLabel = AccessTools.Field(typeof(NPopupYesNoButton), "_label");
    private static readonly FieldInfo? FPopupHsv = AccessTools.Field(typeof(NPopupYesNoButton), "_hsv");
    private static readonly FieldInfo? FPopupOutlineMaterial = AccessTools.Field(typeof(NPopupYesNoButton), "_outlineMaterial");
    private static bool _popupReadinessReflectionWarningLogged;

    internal static bool IsOpen => _popup != null && GodotObject.IsInstanceValid(_popup);

    /// <summary>The currently open popup (picker OR restart-confirm), when any — fuzz-only accessor
    /// for <c>UndoFuzz.CaptureRestartConfirmDialogAsync</c> (UndoFuzz.cs, gated behind
    /// --undosync-uitest-screenshot) to read the live node tree (title/body/button text) and confirm
    /// <see cref="ConfirmRestartCombat"/> actually left a popup open before screenshotting it. No
    /// other caller needs this: every other consumer of <see cref="_popup"/> is a method in this
    /// file, which already has direct field access.</summary>
    internal static NGenericPopup? CurrentPopup => _popup;

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

        if (!ArePopupControlsReady(popup))
        {
            Close();
            return;
        }

        // Captured up front (not re-resolved inside the continuation) so HandlePopupResult
        // acts on the same SyncPoint instance the popup advertised via its button label —
        // ChecksumHook.TryGetCombatStart's "not already newest" check could in principle flip
        // between now and the button press.
        bool hasCombatStart = ChecksumHook.TryGetCombatStart(out var combatStart);
        var task = popup.WaitForConfirmation(
            new LocString(LocTableName, "UNDOSYNC.PICKER_BODY"),
            new LocString(LocTableName, "UNDOSYNC.PICKER_TITLE"),
            // The no-button slot: null hides it (NGenericPopup.cs:104-107) when there is no
            // combat-start anchor to offer (e.g. this IS the combat start already).
            hasCombatStart ? new LocString(LocTableName, "UNDOSYNC.RESTART_COMBAT") : null,
            new LocString(LocTableName, "UNDOSYNC.CANCEL"));
        _ = HandlePopupResult(task, popup, hasCombatStart ? combatStart : null);

        int shown = InjectCardStrip(popup, points, korean);
        int pages = (shown + EntriesPerPage - 1) / EntriesPerPage;
        Log.Write($"[UndoPicker] opened with {shown} choices across {pages} pages (native chrome), "
                  + $"renderWindow={EntriesPerPage}, restartCombat={hasCombatStart}");
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
            Log.Write("[UndoPicker] closed");
        }
    }

    /// <summary>Temporary compatibility shim for the old fuzzer call site.</summary>
    [Obsolete("The fuzzer must dispatch Open and InspectPaginationForFuzzer to Godot's main thread.")]
    internal static Task<PaginationProbeResult> InspectPaginationForFuzzerAsync()
        => Task.FromException<PaginationProbeResult>(
            new InvalidOperationException("Temporary shim: dispatch picker probe to Godot's main thread first."));

    /// <summary>
    /// Fuzzer-only inspection of the live picker pagination. This deliberately
    /// emits the same Godot signal used by a real press of the Next button; it
    /// never emits a card-choice signal and always closes the picker in a
    /// finally block. The method is inert unless a fuzzer calls it after
    /// <see cref="Open"/> has created a multi-page picker.
    /// </summary>
    internal static PaginationProbeResult InspectPaginationForFuzzer()
    {
        var empty = new PaginationPageDiagnostics(0, 0, 0, 0, 0);
        if (!IsOpen || _pagePoints == null || _nextPageButton == null)
        {
            Close();
            return new PaginationProbeResult(
                Passed: false,
                ChoicesExceedPage: false,
                NextButtonAdvanced: false,
                ClosedWithoutSelection: true,
                TotalChoices: 0,
                FirstPage: empty,
                SecondPage: empty,
                Detail: "picker is not open or pagination controls are unavailable");
        }

        bool choicesExceedPage = false;
        bool nextButtonAdvanced = false;
        PaginationPageDiagnostics firstPage = empty;
        PaginationPageDiagnostics secondPage = empty;
        int totalChoices = _pagePoints.Count - 1;
        PaginationProbeResult result;

        try
        {
            choicesExceedPage = totalChoices > EntriesPerPage;
            firstPage = CapturePaginationPageDiagnostics();

            if (choicesExceedPage && !_nextPageButton.Disabled)
            {
                int pageBeforePress = _pageIndex;
                // This is the actual callback path wired in InjectCardStrip:
                // Button.Pressed -> ShowPage(popup, _pageIndex + 1).
                _nextPageButton.EmitSignal(BaseButton.SignalName.Pressed);
                nextButtonAdvanced = _pageIndex == pageBeforePress + 1;
                secondPage = CapturePaginationPageDiagnostics();
            }

            int expectedSecondLast = Math.Min(EntriesPerPage * 2, totalChoices);
            bool secondPageRangeCorrect = secondPage.Page == 2
                && secondPage.FirstChoice == EntriesPerPage + 1
                && secondPage.LastChoice == expectedSecondLast
                && secondPage.CardCount == expectedSecondLast - EntriesPerPage;
            bool firstPageRangeCorrect = firstPage.Page == 1
                && firstPage.FirstChoice == 1
                && firstPage.LastChoice == EntriesPerPage
                && firstPage.CardCount == EntriesPerPage;
            bool passed = choicesExceedPage && firstPageRangeCorrect
                && nextButtonAdvanced && secondPageRangeCorrect;
            string detail = passed
                ? $"choices={totalChoices}; first page={firstPage.FirstChoice}-{firstPage.LastChoice} "
                  + $"cards={firstPage.CardCount}; second page={secondPage.FirstChoice}-{secondPage.LastChoice} "
                  + $"cards={secondPage.CardCount}; next=pressed"
                : $"choices={totalChoices}; first={firstPage}; second={secondPage}; "
                  + $"nextAdvanced={nextButtonAdvanced}";
            Log.Write($"[UndoPicker] pagination probe {(passed ? "PASS" : "FAIL")}: {detail}");
            result = new PaginationProbeResult(passed, choicesExceedPage, nextButtonAdvanced,
                ClosedWithoutSelection: false, totalChoices, firstPage, secondPage, detail);
        }
        catch (Exception ex)
        {
            string detail = $"exception during live next-button probe: {ex.Message}";
            Log.Write($"[UndoPicker] pagination probe FAIL: {detail}");
            result = new PaginationProbeResult(false, choicesExceedPage, nextButtonAdvanced,
                ClosedWithoutSelection: false, totalChoices, firstPage, secondPage, detail);
        }
        finally
        {
            // No card button is pressed by this probe. Closing the popup here
            // proves the inspection leaves no pending picker selection.
            Close();
        }
        return result with { ClosedWithoutSelection = !IsOpen };

        PaginationPageDiagnostics CapturePaginationPageDiagnostics()
        {
            if (_pagePoints == null)
                return empty;
            int choices = _pagePoints.Count - 1;
            int pageCount = (choices + EntriesPerPage - 1) / EntriesPerPage;
            int first = 1 + _pageIndex * EntriesPerPage;
            int last = Math.Min(choices, first + EntriesPerPage - 1);
            return new PaginationPageDiagnostics(_pageIndex + 1, pageCount, first, last, _cardNodes.Count);
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
                    ["UNDOSYNC.RESTART_COMBAT"] = "전투 처음부터",
                    ["UNDOSYNC.RESTART_CONFIRM_TITLE"] = "전투 재시작",
                    ["UNDOSYNC.RESTART_CONFIRM_BODY"] = "지금까지의 전투 진행이 모두 사라지고 첫 턴으로 돌아갑니다.",
                }
                : new Dictionary<string, string>
                {
                    ["UNDOSYNC.PICKER_TITLE"] = "Undo to which point?",
                    ["UNDOSYNC.PICKER_BODY"] = "",
                    ["UNDOSYNC.CANCEL"] = "Cancel",
                    ["UNDOSYNC.RESTART_COMBAT"] = "restart combat",
                    ["UNDOSYNC.RESTART_CONFIRM_TITLE"] = "Restart Combat",
                    ["UNDOSYNC.RESTART_CONFIRM_BODY"] = "This discards everything that happened in this fight and returns to its first turn.",
                });
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] Loc merge failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Awaits the popup's two buttons (a card pick in the strip closes the picker itself
    /// first, before either of these can fire). Yes button ("Cancel") → result true
    /// (NGenericPopup.cs:111-115) → just close, as before this method handled anything else.
    /// No button ("restart combat", only wired up when <paramref name="combatStart"/> is
    /// non-null — see Open()) → result false (:117-119) → close, then hand off to
    /// <see cref="ConfirmRestartCombat"/> for one more yes/no before anything is proposed.
    /// Unlike a card-tile click (still UndoProtocol.ProposeTarget straight away, unchanged —
    /// stepping back one card is cheap), restarting throws away the whole fight, so this path
    /// alone gets a second chance to back out before it reaches ProposeTarget.
    /// </summary>
    private static async Task HandlePopupResult(Task<bool> task, NGenericPopup popup, SyncPoint? combatStart)
    {
        bool cancelled;
        try { cancelled = await task; }
        catch { return; }
        if (!ReferenceEquals(_popup, popup)) return; // already closed via a card pick
        Close();

        if (cancelled)
        {
            Log.Write("[UndoPicker] cancel button pressed, closing without proposing");
            return;
        }
        if (combatStart == null)
        {
            // No-button slot was never shown (Open() passed null), so the game itself
            // cannot have produced a false result — defensive only.
            Log.Write("[UndoPicker] WARN: no-button result but combatStart was null — ignoring");
            return;
        }
        Log.Write($"[UndoPicker] restart-combat button pressed, targetId={combatStart.ChecksumId}");
        _ = OpenRestartConfirmationNextFrame(combatStart.ChecksumId);
    }

    /// <summary>
    /// Waits until the source picker's button signal has completely unwound before opening the second
    /// modal. NGenericPopup resolves its TaskCompletionSource inside its first Released callback, so
    /// <see cref="HandlePopupResult"/> can resume synchronously before NVerticalPopup's second
    /// Released callback calls NModalContainer.Clear(). Opening the confirmation immediately in that
    /// window lets that old cleanup callback clear the NEW popup — the exact restart-combat defect
    /// this method prevents. One ProcessFrame lets both callbacks complete; the confirmation is then
    /// the only modal slot occupant.
    /// </summary>
    private static async Task OpenRestartConfirmationNextFrame(uint targetChecksumId)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            if (tree == null)
            {
                Log.Write("[UndoPicker] restart-combat confirmation skipped: NGame.Instance/GetTree() was null");
                return;
            }
            await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await ConfirmRestartCombat(targetChecksumId);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoPicker] restart-combat confirmation continuation ERROR: {ex}");
        }
    }

    /// <summary>
    /// Second, plain yes/no confirmation before discarding the whole fight — the picker itself
    /// has already fully closed by the time this runs (HandlePopupResult's own <see cref="Close"/>
    /// call above), so NModalContainer's modal slot is guaranteed empty before this opens a new one.
    ///
    /// This is a fresh <see cref="NGenericPopup"/>, not a reuse of the just-closed one — a popup's
    /// buttons resolve its TaskCompletionSource exactly once (NGenericPopup.cs:111-121) and free the
    /// node on press, so there is nothing left to reopen. Follows <c>UndoProtocol.ShowVotePopup</c>'s
    /// shape (UndoProtocol.cs:1196-1213) rather than this file's own <see cref="Open"/>: that popup is
    /// a plain body+title+reject+accept confirm, the closest existing match to a yes/no dialog, where
    /// <see cref="Open"/>'s "Cancel"/"restart combat" pair is inverted (Cancel is the safe yes-slot,
    /// restart-combat the no-slot) to fit its card-strip picker UI — not what a second confirmation
    /// should read like. <c>Close()</c> is called again here (a no-op today, since HandlePopupResult
    /// already cleared it) purely to keep the same Create-then-clear-then-Add ordering ShowVotePopup
    /// uses, so this stays correct even if a future caller reaches this method without already having
    /// cleared the modal.
    ///
    /// Confirm (yes, NGenericPopup.cs:111-115) → <see cref="UndoProtocol.ProposeTarget"/> with the
    /// combat-start id, same call a card-tile click makes. Cancel (no, :117-121), a closed dialog, or
    /// a mid-confirm force-close (e.g. combat ending) all fall through to close-and-return: no
    /// proposal, no restore.
    ///
    /// internal, not private: <c>UndoFuzz.CaptureRestartConfirmDialogAsync</c> (UndoFuzz.cs, gated
    /// behind --undosync-uitest-screenshot, itself only reachable behind --undosync-uitest — see
    /// UndoFuzz.MaybeStart) calls this method directly so it can photograph the REAL popup this
    /// method builds, rather than a hand-built replica. That caller never presses either button — it
    /// only needs the popup to exist and render — so it fires this fire-and-forget exactly like
    /// <see cref="HandlePopupResult"/> does above, and lets <see cref="Close"/> free the node
    /// afterward instead of waiting on the returned Task. No other caller outside this file exists;
    /// this is not a general-purpose public API.
    /// </summary>
    internal static async Task ConfirmRestartCombat(uint targetChecksumId)
    {
        EnsureLocEntries();
        var popup = NGenericPopup.Create();
        if (popup == null || NModalContainer.Instance == null)
        {
            Log.Write("[UndoPicker] ConfirmRestartCombat: NGenericPopup.Create() failed or NModalContainer.Instance is null — no proposal sent");
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
            Log.Write("[UndoPicker] restart-combat confirmation cancelled, closing without proposing");
            return;
        }
        Log.Write($"[UndoPicker] restart-combat confirmed, targetId={targetChecksumId}");
        UndoProtocol.ProposeTarget(targetChecksumId);
    }

    /// <summary>
    /// IsNodeReady() is not reliable for this instantiated native hierarchy. Check the concrete
    /// fields populated by NPopupYesNoButton._Ready instead, while still requiring the popup and
    /// both native buttons to be valid scene-tree nodes.
    /// </summary>
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
                Log.Write("[UndoPicker] native button readiness reflection unavailable for NPopupYesNoButton.cs:154-170");
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
            Log.Write($"[UndoPicker] native button readiness reflection check ERROR: {ex.Message}");
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
    /// scrolling strip of one bounded page of undo steps. Older pages are
    /// rendered on demand, so every stored point remains reachable without
    /// creating one NCard per action. Returns the total number of choices.
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

        // Leave a small, fixed footer for page navigation. The strip itself is
        // still a ScrollContainer because two cards may not fit at every UI scale.
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

    /// <summary>Replaces the bounded page in-place, retaining newest-first target semantics.</summary>
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
            // points[0] is the current state; points[i] is the target. Every
            // stored point, including turn-start anchors, gets a selectable item.
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
        Log.Write($"[UndoPicker] rendered page={_pageIndex + 1}/{pageCount} "
                  + $"choices={first}-{lastExclusive - 1} ncardNodes={_cardNodes.Count}");
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

        if (BuildDummyCardTile(holder, context, korean, holderSize, scale))
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
    private static bool BuildDummyCardTile(Control holder, string context, bool korean, Vector2 holderSize, float scale)
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
        // Historical inventory is not available from native journal metadata, so
        // potion steps intentionally fall back to the generic action label.
        string description = TryGetPotionName(context) ?? "";
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
    /// Native journal metadata does not contain the historical inventory needed to
    /// resolve a potion title. Keep the generic context label instead.
    /// </summary>
    private static string? TryGetPotionName(string context)
    {
        return null;
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
