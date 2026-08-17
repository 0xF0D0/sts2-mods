using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

// ── Protocol messages (auto-registered on every modded peer via GetSubtypesInMods) ──

public record struct UndoProposalMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong proposerNetId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(proposerNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        proposerNetId = reader.ReadULong();
    }
}

public record struct UndoVoteMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong voterNetId;
    public bool accept;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(voterNetId);
        writer.WriteBool(accept);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        voterNetId = reader.ReadULong();
        accept = reader.ReadBool();
    }
}

public record struct UndoCommitMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;

    public void Serialize(PacketWriter writer) => writer.WriteUInt(targetChecksumId);

    public void Deserialize(PacketReader reader) => targetChecksumId = reader.ReadUInt();
}

public record struct UndoCancelMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong byNetId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(byNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        byNetId = reader.ReadULong();
    }
}

// ── Vote-based synchronized undo ──
//
// Left Arrow → RequestUndo():
//   singleplayer: restore immediately.
//   multiplayer:  broadcast UndoProposal, every peer shows an accept/reject popup.
//                 The HOST tallies votes (proposer counts as accepted). All accepted
//                 → host broadcasts UndoCommit and everyone restores the same sync
//                 point after their action queue drains. Any reject → UndoCancel.
internal static class UndoProtocol
{
    private static INetGameService? _registeredService;

    private static uint? _pendingTargetId;
    private static int _pendingGeneration;
    private const int TimeoutFrames = 30 * 60; // ~30s at 60fps
    private static readonly HashSet<ulong> _accepted = new();
    private static NGenericPopup? _popup;

    private const string LocTableName = "main_menu_ui";

    internal static void EnsureHandlersRegistered()
    {
        var svc = RunManager.Instance?.NetService;
        if (svc == null || ReferenceEquals(svc, _registeredService))
            return;
        if (_registeredService != null)
        {
            _registeredService.UnregisterMessageHandler<UndoProposalMessage>(OnProposalReceived);
            _registeredService.UnregisterMessageHandler<UndoVoteMessage>(OnVoteReceived);
            _registeredService.UnregisterMessageHandler<UndoCommitMessage>(OnCommitReceived);
            _registeredService.UnregisterMessageHandler<UndoCancelMessage>(OnCancelReceived);
        }
        svc.RegisterMessageHandler<UndoProposalMessage>(OnProposalReceived);
        svc.RegisterMessageHandler<UndoVoteMessage>(OnVoteReceived);
        svc.RegisterMessageHandler<UndoCommitMessage>(OnCommitReceived);
        svc.RegisterMessageHandler<UndoCancelMessage>(OnCancelReceived);
        _registeredService = svc;
        ResetPending();
        Log.Write($"[UndoProtocol] Handlers registered. service={svc.Type} netId={svc.NetId}");
    }

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
                    ["UNDOSYNC.PROPOSAL_TITLE"] = "되돌리기 제안",
                    ["UNDOSYNC.PROPOSAL_BODY"] = "{player} 님이 {steps}수를 되돌리려 합니다.\n수락하시겠습니까?",
                    ["UNDOSYNC.WAITING_TITLE"] = "되돌리기 제안 중",
                    ["UNDOSYNC.WAITING_BODY"] = "다른 플레이어의 수락을 기다리는 중...",
                    ["UNDOSYNC.ACCEPT"] = "수락",
                    ["UNDOSYNC.REJECT"] = "거절",
                    ["UNDOSYNC.CANCEL"] = "취소",
                }
                : new Dictionary<string, string>
                {
                    ["UNDOSYNC.PROPOSAL_TITLE"] = "Undo Proposal",
                    ["UNDOSYNC.PROPOSAL_BODY"] = "{player} wants to undo {steps} action(s).\nAccept?",
                    ["UNDOSYNC.WAITING_TITLE"] = "Undo Proposed",
                    ["UNDOSYNC.WAITING_BODY"] = "Waiting for other players to accept...",
                    ["UNDOSYNC.ACCEPT"] = "Accept",
                    ["UNDOSYNC.REJECT"] = "Reject",
                    ["UNDOSYNC.CANCEL"] = "Cancel",
                });
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] Loc merge failed: {ex.Message}");
        }
    }

    private static ulong MyNetId => _registeredService?.NetId ?? 0;

    private static IEnumerable<ulong> AllPlayerIds()
    {
        var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
        if (players == null) yield break;
        foreach (var p in players)
            yield return p.NetId;
    }

    // ── Entry point (Left Arrow) ──

    internal static void RequestUndo()
    {
        EnsureHandlersRegistered();
        var svc = _registeredService;
        if (svc == null) return;

        if (UndoPicker.IsOpen)
        {
            UndoPicker.Close();
            return;
        }
        if (_pendingTargetId != null)
        {
            Log.Write("[UndoProtocol] RequestUndo ignored: proposal already pending");
            return;
        }
        if (!UndoSyncMod.CanUndoRedo())
        {
            Log.Write("[UndoProtocol] RequestUndo blocked by guards");
            return;
        }
        // The proposer picks the rewind target FIRST; the vote (or the restore, in
        // singleplayer) happens for that specific target.
        UndoPicker.Open();
    }

    /// <summary>Called by the picker with the chosen sync point id.</summary>
    internal static void ProposeTarget(uint targetChecksumId)
    {
        var svc = _registeredService;
        if (svc == null) return;
        if (_pendingTargetId != null) return;
        if (!UndoSyncMod.CanUndoRedo())
        {
            Log.Write("[UndoProtocol] ProposeTarget blocked by guards");
            return;
        }
        if (!ChecksumHook.TryGetSyncPoint(targetChecksumId, out var target))
        {
            Log.Write($"[UndoProtocol] ProposeTarget: sync point id={targetChecksumId} no longer exists");
            return;
        }

        if (svc.Type == NetGameType.Singleplayer)
        {
            Log.Write($"[UndoProtocol] Singleplayer undo to id={target.ChecksumId}");
            ChecksumHook.RestoreTo(target);
            return;
        }

        _pendingTargetId = target.ChecksumId;
        _accepted.Clear();
        _accepted.Add(MyNetId);
        svc.SendMessage(new UndoProposalMessage
        {
            targetChecksumId = target.ChecksumId,
            proposerNetId = MyNetId,
        });
        Log.Write($"[UndoProtocol] Proposed undo to id={target.ChecksumId} ({StepsTo(target.ChecksumId)} steps)");
        ShowWaitingPopup(target.ChecksumId);
        _ = TimeoutWatchdog(_pendingGeneration, target.ChecksumId);
        // Host proposing alone (no other players yet) — tally immediately.
        if (svc.Type == NetGameType.Host)
            CheckAllAccepted();
    }

    /// <summary>
    /// How many actions a rewind to <paramref name="targetId"/> undoes. Computed
    /// locally on each peer — the sync point id streams are identical, so every
    /// peer gets the same number without putting it in the message.
    /// </summary>
    private static int StepsTo(uint targetId) =>
        ChecksumHook.SyncPointsNewestFirst().Count(p => p.ChecksumId > targetId);

    // ── Message handlers ──

    private static void OnProposalReceived(UndoProposalMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Proposal received from {senderId}: target id={msg.targetChecksumId}");
        if (_pendingTargetId != null)
        {
            Log.Write("[UndoProtocol] Ignoring proposal: another proposal already pending");
            return;
        }
        _pendingTargetId = msg.targetChecksumId;
        _accepted.Clear();
        _accepted.Add(msg.proposerNetId);

        if (!ChecksumHook.HasSyncPoint(msg.targetChecksumId))
        {
            // We can't restore to a point we don't have — reject so nobody diverges.
            Log.Write($"[UndoProtocol] Missing sync point id={msg.targetChecksumId} — auto-rejecting");
            SubmitLocalVote(accept: false);
            return;
        }
        ShowVotePopup(msg.targetChecksumId, msg.proposerNetId);
        _ = TimeoutWatchdog(_pendingGeneration, msg.targetChecksumId);
    }

    private static void OnVoteReceived(UndoVoteMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Vote from {msg.voterNetId}: accept={msg.accept} (target id={msg.targetChecksumId})");
        if (_pendingTargetId != msg.targetChecksumId) return;
        if (_registeredService?.Type != NetGameType.Host) return; // host tallies
        RegisterVote(msg.voterNetId, msg.accept);
    }

    private static void OnCommitReceived(UndoCommitMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Commit received for id={msg.targetChecksumId}");
        ClosePopup();
        _ = CommitAsync(msg.targetChecksumId);
        ResetPending();
    }

    private static void OnCancelReceived(UndoCancelMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Cancelled by {msg.byNetId} (target id={msg.targetChecksumId})");
        ClosePopup();
        ResetPending();
    }

    // ── Vote handling ──

    private static void SubmitLocalVote(bool accept)
    {
        var svc = _registeredService;
        if (svc == null || _pendingTargetId == null) return;
        var target = _pendingTargetId.Value;
        svc.SendMessage(new UndoVoteMessage
        {
            targetChecksumId = target,
            voterNetId = MyNetId,
            accept = accept,
        });
        if (svc.Type == NetGameType.Host)
            RegisterVote(MyNetId, accept); // host's own vote never loops back as a message
    }

    /// <summary>Host-only: tally a vote, then commit or cancel.</summary>
    private static void RegisterVote(ulong voter, bool accept)
    {
        if (_pendingTargetId == null) return;
        var target = _pendingTargetId.Value;
        if (!accept)
        {
            Log.Write($"[UndoProtocol] {voter} rejected — cancelling");
            _registeredService?.SendMessage(new UndoCancelMessage { targetChecksumId = target, byNetId = voter });
            ClosePopup();
            ResetPending();
            return;
        }
        _accepted.Add(voter);
        CheckAllAccepted();
    }

    private static void CheckAllAccepted()
    {
        if (_pendingTargetId == null) return;
        var target = _pendingTargetId.Value;
        var all = AllPlayerIds().ToList();
        if (all.Count == 0 || !all.All(id => _accepted.Contains(id)))
            return;
        Log.Write($"[UndoProtocol] All {all.Count} players accepted — committing id={target}");
        _registeredService?.SendMessage(new UndoCommitMessage { targetChecksumId = target });
        ClosePopup();
        _ = CommitAsync(target);
        ResetPending();
    }

    // ── Commit: wait for the local action queue to drain, then restore ──

    private static async Task CommitAsync(uint targetId)
    {
        try
        {
            if (!ChecksumHook.TryGetSyncPoint(targetId, out var sp))
            {
                Log.Write($"[UndoProtocol] COMMIT FAILED: missing sync point id={targetId} — peers may diverge!");
                return;
            }
            // Restore only in an idle PLAYER play phase. An empty action queue is not
            // enough: the enemy turn has idle gaps between monster actions, and
            // restoring there leaves the async enemy-turn flow running against the
            // restored state. All peers evaluate the same synchronized state, so they
            // proceed (or abort) together.
            var tree = NGame.Instance?.GetTree();
            for (int i = 0; i < 60 * 60; i++) // up to ~60s at 60fps
            {
                var cm = CombatManager.Instance;
                if (cm == null || !cm.IsInProgress)
                {
                    Log.Write("[UndoProtocol] Commit aborted: combat ended before restore");
                    return;
                }
                var cs = UndoSyncMod.GetCombatState();
                var syncr = RunManager.Instance?.ActionQueueSynchronizer;
                var aq = RunManager.Instance?.ActionQueueSet;
                bool idle = cs != null && cs.CurrentSide == CombatSide.Player
                    && syncr != null && syncr.CombatState == ActionSynchronizerCombatState.PlayPhase
                    && aq != null && aq.IsEmpty
                    && NGame.Instance?.Transition?.InTransition != true;
                if (idle)
                {
                    ChecksumHook.RestoreTo(sp);
                    return;
                }
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            Log.Write("[UndoProtocol] Commit aborted: never reached an idle play phase — skipping restore");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] CommitAsync ERROR: {ex}");
        }
    }

    // ── Popup UI ──

    private static void ShowVotePopup(uint targetId, ulong proposerNetId)
    {
        EnsureLocEntries();
        try
        {
            var popup = NGenericPopup.Create();
            if (popup == null || NModalContainer.Instance == null) return;
            ClosePopup();
            _popup = popup;
            NModalContainer.Instance.Add(popup);
            var body = new LocString(LocTableName, "UNDOSYNC.PROPOSAL_BODY");
            body.Add("player", ResolvePlayerName(proposerNetId));
            body.Add("steps", StepsTo(targetId));
            var task = popup.WaitForConfirmation(
                body,
                new LocString(LocTableName, "UNDOSYNC.PROPOSAL_TITLE"),
                new LocString(LocTableName, "UNDOSYNC.REJECT"),
                new LocString(LocTableName, "UNDOSYNC.ACCEPT"));
            _ = HandleVotePopupResult(task, popup);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] ShowVotePopup ERROR: {ex}");
            SubmitLocalVote(accept: false);
        }
    }

    private static string ResolvePlayerName(ulong netId)
    {
        try
        {
            var svc = _registeredService;
            if (svc == null) return $"{netId}";
            return PlatformUtil.GetPlayerName(svc.Platform, netId);
        }
        catch
        {
            return $"{netId}";
        }
    }

    /// <summary>
    /// Auto-cancels a proposal that nobody resolved within the timeout. Every peer
    /// runs one; the host additionally broadcasts the cancel so all popups close even
    /// if a peer's own watchdog drifted. Generation guard makes stale watchdogs no-ops.
    /// </summary>
    private static async Task TimeoutWatchdog(int generation, uint targetId)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            if (tree == null) return;
            for (int i = 0; i < TimeoutFrames; i++)
            {
                if (_pendingGeneration != generation || _pendingTargetId != targetId)
                    return; // resolved (commit/cancel/new proposal)
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (_pendingGeneration != generation || _pendingTargetId != targetId)
                return;
            Log.Write($"[UndoProtocol] Proposal id={targetId} timed out — cancelling");
            if (_registeredService?.Type == NetGameType.Host)
                _registeredService.SendMessage(new UndoCancelMessage { targetChecksumId = targetId, byNetId = MyNetId });
            ClosePopup();
            ResetPending();
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] TimeoutWatchdog ERROR: {ex.Message}");
        }
    }

    private static async Task HandleVotePopupResult(Task<bool> task, NGenericPopup popup)
    {
        bool accepted;
        try { accepted = await task; }
        catch { accepted = false; }
        if (!ReferenceEquals(_popup, popup)) return; // popup was force-closed; vote is moot
        _popup = null;
        EnsureModalCleared(popup); // button click freed the popup; make sure the backstop went with it
        SubmitLocalVote(accepted);
    }

    private static void ShowWaitingPopup(uint targetId)
    {
        EnsureLocEntries();
        try
        {
            var popup = NGenericPopup.Create();
            if (popup == null || NModalContainer.Instance == null) return;
            ClosePopup();
            _popup = popup;
            NModalContainer.Instance.Add(popup);
            var task = popup.WaitForConfirmation(
                new LocString(LocTableName, "UNDOSYNC.WAITING_BODY"),
                new LocString(LocTableName, "UNDOSYNC.WAITING_TITLE"),
                null,
                new LocString(LocTableName, "UNDOSYNC.CANCEL"));
            _ = HandleWaitingPopupResult(task, popup, targetId);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] ShowWaitingPopup ERROR: {ex}");
        }
    }

    private static async Task HandleWaitingPopupResult(Task<bool> task, NGenericPopup popup, uint targetId)
    {
        try { await task; } catch { return; }
        if (!ReferenceEquals(_popup, popup)) return; // already committed/cancelled
        _popup = null;
        EnsureModalCleared(popup);
        // Proposer pressed Cancel.
        Log.Write("[UndoProtocol] Proposer cancelled");
        _registeredService?.SendMessage(new UndoCancelMessage { targetChecksumId = targetId, byNetId = MyNetId });
        ResetPending();
    }

    private static void ClosePopup()
    {
        var popup = _popup;
        _popup = null;
        if (popup != null)
            EnsureModalCleared(popup);
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
                popup.QueueFree();
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] EnsureModalCleared ERROR: {ex.Message}");
        }
    }

    private static void ResetPending()
    {
        _pendingTargetId = null;
        _pendingGeneration++;
        _accepted.Clear();
    }

    /// <summary>Combat ended — drop any in-flight proposal and its popup.</summary>
    internal static void ResetOnCombatEnd()
    {
        UndoPicker.Close();
        if (_pendingTargetId == null) return;
        Log.Write("[UndoProtocol] Combat ended — clearing pending proposal");
        ClosePopup();
        ResetPending();
    }
}
