using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;

namespace UndoSync;

/// <summary>
/// Step picker shown when the player presses Left Arrow: choose how far back to
/// rewind BEFORE the vote opens (in multiplayer) or the restore happens (in
/// singleplayer). Built entirely from built-in Godot nodes — mod assemblies can't
/// define Godot script subclasses (no source generators), so no NModalContainer
/// (it requires IScreenContext); a full-screen ColorRect blocks game input instead.
/// </summary>
internal static class UndoPicker
{
    private const int MaxEntries = 10;

    private static ColorRect? _root;

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

        var backstop = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backstop.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        vbox.AddChild(new Label
        {
            Text = korean ? "어느 시점으로 되돌릴까요?" : "Undo to which point?",
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // points[0] is the current state; undoing to points[i] rewinds i actions.
        for (int i = 1; i < points.Count && i <= MaxEntries; i++)
        {
            int steps = i;
            // The oldest action being undone is the one that produced points[i-1].
            string what = DescribeAction(points[i - 1].Context, korean);
            var btn = new Button
            {
                Text = korean
                    ? $"{steps}수 되돌리기  ({what}까지 취소)"
                    : $"Undo {steps} action{(steps > 1 ? "s" : "")}  (back through {what})",
            };
            uint targetId = points[i].ChecksumId;
            btn.Pressed += () =>
            {
                Close();
                UndoProtocol.ProposeTarget(targetId);
            };
            vbox.AddChild(btn);
        }

        var cancel = new Button { Text = korean ? "취소" : "Cancel" };
        cancel.Pressed += Close;
        vbox.AddChild(cancel);

        backstop.AddChild(panel);
        // Center the panel within the full-rect backstop.
        panel.AnchorLeft = panel.AnchorRight = 0.5f;
        panel.AnchorTop = panel.AnchorBottom = 0.5f;
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;

        var game = NGame.Instance;
        if (game == null) return;
        if (game.Theme != null)
            backstop.Theme = game.Theme; // reuse game fonts (Korean glyphs included)
        game.AddChild(backstop);
        _root = backstop;
        Log.Write($"[UndoPicker] opened with {Math.Min(points.Count - 1, MaxEntries)} choices");
    }

    internal static void Close()
    {
        var root = _root;
        _root = null;
        if (root != null && GodotObject.IsInstanceValid(root))
            root.QueueFree();
    }

    private static bool IsKorean()
    {
        try { return LocManager.Instance.Language == "kor"; }
        catch { return false; }
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
