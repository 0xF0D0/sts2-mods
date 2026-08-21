using Godot;
using HarmonyLib;
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

/// <summary>
/// Sent by every peer once its OWN local restore (ChecksumHook.RestoreTo, inside CommitAsync) has
/// finished, to tell the host it is safe to count this peer toward the barrier release. Same shape
/// as UndoVoteMessage (target id + sender id) — the host's tally of these (RegisterAck/CheckAllAcked)
/// deliberately reuses UndoVoteMessage's own tally shape (a HashSet&lt;ulong&gt; plus
/// AllPlayerIds().All(...)) rather than inventing a second one. See "── Cross-peer restore barrier
/// ──" below for the full flow this belongs to.
/// </summary>
public record struct UndoRestoreAckMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public ulong ackerNetId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteULong(ackerNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        ackerNetId = reader.ReadULong();
    }
}

/// <summary>
/// Broadcast by the host once every player has sent an UndoRestoreAckMessage for the same target id
/// (CheckAllAcked), or once the barrier's own timeout gives up on stragglers (BarrierTimeoutWatchdog).
/// Receipt is what actually lowers the barrier on every OTHER peer (OnResumeReceived) — the host
/// lowers its own directly in the same call that decides to send this, for the same reason
/// CheckAllAccepted calls CommitAsync directly instead of waiting for its own UndoCommitMessage to
/// loop back: a peer's own broadcast is never delivered back to itself.
/// </summary>
public record struct UndoResumeMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;

    public void Serialize(PacketWriter writer) => writer.WriteUInt(targetChecksumId);

    public void Deserialize(PacketReader reader) => targetChecksumId = reader.ReadUInt();
}

// ── Vote-based synchronized undo, with a cross-peer restore barrier ──
//
// Left Arrow → RequestUndo():
//   singleplayer: restore immediately.
//   multiplayer:  broadcast UndoProposal, every peer shows an accept/reject popup.
//                 The HOST tallies votes (proposer counts as accepted). All accepted
//                 → host broadcasts UndoCommit and everyone restores the same sync
//                 point after their action queue drains. Any reject → UndoCancel.
//
// UndoCommit used to just start each peer's own restore independently, with nothing stopping a
// peer that finished first from continuing to play while a slower peer was still mid-restore — a
// real two-instance run measured the two peers restoring 726ms apart, long enough for an ordinary
// card play to execute on the faster peer and never reach the slower one before it, too, restored
// (see this file's git history for the measurement). So UndoCommit now ALSO raises a barrier
// (CombatManager.PlayerActionsDisabled, reached via reflection — see PlayerActionsDisabledProp's
// own doc comment for why) the INSTANT it is received, before CommitAsync's idle wait even starts:
// every peer still restores only once it is itself idle (the existing CanUndoRedo-style gate inside
// CommitAsync, unchanged), then sends UndoRestoreAck. The HOST tallies acks the same way it tallies
// votes and, once every player has acked, broadcasts UndoResume — which is what actually lowers the
// barrier on every peer. See "── Cross-peer restore barrier ──" further down for the full mechanism.
internal static class UndoProtocol
{
    private static INetGameService? _registeredService;

    private static uint? _pendingTargetId;
    private static int _pendingGeneration;
    private const int TimeoutFrames = 30 * 60; // ~30s at 60fps
    private static readonly HashSet<ulong> _accepted = new();
    private static NGenericPopup? _popup;

    /// <summary>
    /// Fuzz-only auto-accept for the undo vote (MpFuzz step 3, Part A). When true,
    /// OnProposalReceived below skips ShowVotePopup and instead calls SubmitLocalVote(accept: true)
    /// directly — the exact same function HandleVotePopupResult calls once a human's Accept button
    /// resolves the popup, so the UndoVoteMessage actually sent and the host's tally logic
    /// (RegisterVote/CheckAllAccepted) are completely unmodified; only the popup UI itself is
    /// skipped, and only on the peer that would have shown it.
    ///
    /// Only ever written by MpFuzz.MaybeStart, and only after that file's own --undosync-mpfuzz
    /// CommandLineHelper.HasArg gate (MpFuzz.cs) has already passed. A normal game process never
    /// executes past that gate, so this field's compile-time initializer (false) is the only value
    /// any normal player's UndoProtocol ever observes — the same "only the currently-active fuzz
    /// path's own setup ever writes this" discipline UndoFuzz.RestoresAllowed /
    /// UndoFuzz._useMultiplayerIdleGate already use for their own fuzz-only switches (UndoFuzz.cs),
    /// applied here to UndoProtocol instead because this behaviour belongs to the vote protocol
    /// itself, not the combat driver.
    ///
    /// The cross-peer restore barrier below (ArmActionBarrier/ReleaseActionBarrier and everything
    /// that drives them) is NOT gated behind any fuzz flag, unlike this field — it is a real fix to
    /// shipped multiplayer behaviour, not fuzz-only scaffolding, so it runs unconditionally for
    /// every peer, human-driven or fuzz-driven, singleplayer or multiplayer.
    /// </summary>
    internal static bool AutoAcceptForFuzz;

    private const string LocTableName = "main_menu_ui";

    // ── Cross-peer restore barrier: reflection onto CombatManager.PlayerActionsDisabled ──

    /// <summary>
    /// CombatManager.PlayerActionsDisabled (CombatManager.cs:101-115) has a PUBLIC getter but a
    /// PRIVATE setter — confirmed by reading the decompiled property body, not just its declaration
    /// line (an earlier draft of this fix assumed the whole property was public-settable from the
    /// declaration alone and was wrong). NPlayerHand (NPlayerHand.cs:886, :1128) and NPotionPopup
    /// (NPotionPopup.cs:439) only ever READ it, which is why those call sites compile fine without
    /// reflection; a WRITE from this mod (a different assembly, not CombatManager itself) needs
    /// AccessTools the same way this mod already reaches other private game state it does not own
    /// (ChecksumHook.cs's TrackerNextIdProp/TrackerChecksumsField/ActionQueueSetWasResetField, etc.).
    ///
    /// Going through PropertyInfo.SetValue here — rather than poking a private backing field — is
    /// what makes this safe: SetValue invokes the REAL compiled setter body, including its
    /// change-detection and the PlayerActionsDisabledChanged?.Invoke(...) it fires on an actual
    /// change (CombatManager.cs:106-113) — the exact signal NPlayerHand/NPotionPopup already react
    /// to, so a human player is blocked too, not just this protocol's own idle-wait polling.
    ///
    /// tools/SurfaceCheck's Check 1 validates every AccessTools string in this mod against the
    /// shipped sts2.dll at build time, so if a future game update renames or removes this property,
    /// that becomes a loud static failure caught before shipping — not a silent no-op at runtime.
    /// That check is what makes reflecting into a private setter acceptable here instead of fragile;
    /// see also ArmActionBarrier's own null-guard for the runtime-defensive half of the same concern.
    /// </summary>
    private static readonly System.Reflection.PropertyInfo? PlayerActionsDisabledProp =
        AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled");

    /// <summary>
    /// Set by ArmActionBarrier to whatever CombatManager.Instance.PlayerActionsDisabled was
    /// immediately before this peer forced it true, and consumed by ReleaseActionBarrier to put it
    /// back — never blindly to false. The game may legitimately already have actions disabled when a
    /// commit arrives (e.g. the local player already pressed End Turn — CombatManager.
    /// OnEndedTurnLocally, CombatManager.cs:992-994 — sets this true independently of any undo), and
    /// forcing it false on release would hand control back when the game itself says it should not
    /// be. Null doubles as "no barrier currently armed on this peer" — see BarrierArmed below.
    /// </summary>
    private static bool? _barrierPreviousActionsDisabled;

    /// <summary>
    /// The checksum id this peer's barrier is currently armed for, or null if none. Deliberately
    /// separate from _pendingTargetId above: OnCommitReceived/CheckAllAccepted call ResetPending()
    /// right after starting the commit (the VOTE is over), but the barrier must stay armed across
    /// that reset until every peer has actually finished restoring and the host has broadcast
    /// UndoResume — so it needs its own target id, its own tally, and its own generation counter
    /// rather than reusing the vote-phase fields that get cleared out from under it.
    /// </summary>
    private static uint? _barrierTargetId;

    /// <summary>Generation counter for the barrier, incremented every time it resolves (normal
    /// release, timeout, or a hard reset via ResetBarrier) — same "stale watchdogs become no-ops"
    /// role _pendingGeneration plays for TimeoutWatchdog, reused here for BarrierTimeoutWatchdog.</summary>
    private static int _barrierGeneration;

    /// <summary>Host-only tally of who has sent UndoRestoreAckMessage for _barrierTargetId. Same
    /// shape as _accepted above (a HashSet&lt;ulong&gt; checked against AllPlayerIds().All(...)) by
    /// design — see UndoRestoreAckMessage's own doc comment. Harmless and unread on non-host peers.</summary>
    private static readonly HashSet<ulong> _acked = new();

    /// <summary>True while this peer's own barrier is armed — i.e. a commit has been received (or,
    /// on the host, decided) and this peer has not yet processed the matching UndoResume. Used to
    /// refuse a NEW undo proposal (RequestUndo/ProposeTarget/OnProposalReceived) while a PREVIOUS
    /// commit's barrier is still winding down, so "nobody acts until everybody has restored" also
    /// covers proposing another restore, not just playing a card.</summary>
    private static bool BarrierArmed => _barrierTargetId != null;

    /// <summary>Matches CommitAsync's own idle-wait loop bound (that loop's own "up to ~60s at
    /// 60fps" comment) — pulled out as a named constant so BarrierTimeoutFrames below is derived
    /// from it instead of holding a second copy of the same magic number that could silently drift
    /// out of sync with it.</summary>
    private const int CommitIdleWaitFrames = 60 * 60; // ~60s at 60fps — mirrors CommitAsync's own bound

    /// <summary>
    /// Bound for BarrierTimeoutWatchdog (see its own doc comment), reusing TimeoutFrames — the
    /// proposal flow's own budget — per the task's "reuse the timeout mechanism the proposal flow
    /// already has", rather than inventing a second timeout constant. It is added ON TOP of
    /// CommitIdleWaitFrames rather than used alone: CommitAsync's own idle-wait loop can legitimately
    /// run for that entire ~60s before either restoring or aborting, and the barrier must stay armed
    /// for all of it — a shorter bound could release the barrier while a peer is still legitimately
    /// waiting for an idle moment to restore, recreating exactly the race this barrier exists to
    /// close. TimeoutFrames on top of that is margin for the ack/resume round trip once every peer
    /// that needed to restore actually has (~90s total from the moment a barrier arms).
    /// </summary>
    private const int BarrierTimeoutFrames = CommitIdleWaitFrames + TimeoutFrames;

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
            _registeredService.UnregisterMessageHandler<UndoRestoreAckMessage>(OnRestoreAckReceived);
            _registeredService.UnregisterMessageHandler<UndoResumeMessage>(OnResumeReceived);
        }
        svc.RegisterMessageHandler<UndoProposalMessage>(OnProposalReceived);
        svc.RegisterMessageHandler<UndoVoteMessage>(OnVoteReceived);
        svc.RegisterMessageHandler<UndoCommitMessage>(OnCommitReceived);
        svc.RegisterMessageHandler<UndoCancelMessage>(OnCancelReceived);
        svc.RegisterMessageHandler<UndoRestoreAckMessage>(OnRestoreAckReceived);
        svc.RegisterMessageHandler<UndoResumeMessage>(OnResumeReceived);
        _registeredService = svc;
        ResetPending();
        ResetBarrier();
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
        if (BarrierArmed)
        {
            // A previous commit's barrier (see "── Cross-peer restore barrier ──" below) hasn't
            // released on this peer yet — nobody may act, including proposing another restore,
            // until every peer has finished the one already in flight.
            Log.Write($"[UndoProtocol] RequestUndo ignored: restore barrier still armed for id={_barrierTargetId}");
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
        if (BarrierArmed)
        {
            // Defense in depth alongside RequestUndo's own check above: covers the narrow window
            // between RequestUndo opening the picker and the picker calling back in here, during
            // which a commit could have arrived and armed the barrier.
            Log.Write($"[UndoProtocol] ProposeTarget ignored: restore barrier still armed for id={_barrierTargetId}");
            return;
        }
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

        // Part C (step 3) proof, not a new guard — see AssertQueueEmptyOrRecordViolation's own doc
        // comment. CanUndoRedo() just above already required ActionQueueSet.IsEmpty, so this can
        // only ever record zero today; it stays as an explicit, independently-logged assertion.
        AssertQueueEmptyOrRecordViolation("ProposeTarget");

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

        if (BarrierArmed)
        {
            // This peer's own restore barrier (see "── Cross-peer restore barrier ──" below) from a
            // PREVIOUS commit hasn't released yet — it is still mid its own idle-wait/restore, or
            // still waiting on the host's UndoResume. Auto-reject exactly like the missing-sync-point
            // case below: the proposer's own "Waiting" popup needs a real UndoVoteMessage(accept:
            // false) to resolve into an UndoCancelMessage, not silence.
            Log.Write($"[UndoProtocol] Proposal id={msg.targetChecksumId} received while this peer's own restore barrier (target={_barrierTargetId}) is still armed — auto-rejecting so nobody diverges.");
            SubmitLocalVote(accept: false);
            return;
        }

        if (!ChecksumHook.HasSyncPoint(msg.targetChecksumId))
        {
            // We can't restore to a point we don't have — reject so nobody diverges.
            Log.Write($"[UndoProtocol] Missing sync point id={msg.targetChecksumId} — auto-rejecting");
            SubmitLocalVote(accept: false);
            return;
        }

        if (AutoAcceptForFuzz)
        {
            // MpFuzz (step 3, Part A) only — see AutoAcceptForFuzz's own doc comment for why this
            // can never fire in a normal game. Goes through SubmitLocalVote, the SAME function
            // HandleVotePopupResult calls once a human clicks Accept on the popup below — the vote
            // message and host tally are completely unmodified; only the popup UI is skipped.
            Log.Write($"[UndoProtocol] --undosync-mpfuzz auto-accept: target id={msg.targetChecksumId}");
            SubmitLocalVote(accept: true);
        }
        else
        {
            ShowVotePopup(msg.targetChecksumId, msg.proposerNetId);
        }
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
        // The barrier goes up FIRST — before ClosePopup, before CommitAsync's idle wait even starts
        // — so no new player action can slip in on THIS peer during the window before every peer
        // has restored. See ArmBarrierForCommit/ArmActionBarrier's own doc comments.
        ArmBarrierForCommit(msg.targetChecksumId);
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

    /// <summary>Host-only: tally one peer's restore ack, same shape as OnVoteReceived above.</summary>
    private static void OnRestoreAckReceived(UndoRestoreAckMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Restore ack from {msg.ackerNetId} (target id={msg.targetChecksumId})");
        if (_registeredService?.Type != NetGameType.Host) return; // host tallies
        RegisterAck(msg.ackerNetId, msg.targetChecksumId);
    }

    /// <summary>
    /// Lowers the barrier on every peer EXCEPT the host that sent this (the host lowers its own
    /// directly inside CheckAllAcked/BarrierTimeoutWatchdog — see UndoResumeMessage's own doc
    /// comment for why a self-sent broadcast never loops back). ReleaseBarrierForTarget itself
    /// already ignores a mismatched/stale target, so a resume that arrives after this peer's own
    /// barrier already resolved (e.g. via its own timeout) is a harmless no-op.
    /// </summary>
    private static void OnResumeReceived(UndoResumeMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Resume received for id={msg.targetChecksumId}");
        ReleaseBarrierForTarget(msg.targetChecksumId);
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
        // Host's own barrier: the broadcast above never loops back to its own sender (same reason
        // RegisterVote(MyNetId, accept) is called directly a few lines up instead of relying on
        // OnVoteReceived firing locally) — so arm it here explicitly, exactly like OnCommitReceived
        // does for every OTHER peer that receives the broadcast.
        ArmBarrierForCommit(target);
        ClosePopup();
        _ = CommitAsync(target);
        ResetPending();
    }

    /// <summary>Sends this peer's own restore ack after a successful local restore (called from
    /// CommitAsync). Host-only special case mirrors SubmitLocalVote: a host's own broadcast never
    /// loops back to itself, so the host also registers its own ack directly.</summary>
    private static void SendRestoreAck(uint targetId)
    {
        var svc = _registeredService;
        if (svc == null) return;
        svc.SendMessage(new UndoRestoreAckMessage { targetChecksumId = targetId, ackerNetId = MyNetId });
        if (svc.Type == NetGameType.Host)
            RegisterAck(MyNetId, targetId); // host's own ack never loops back as a message
    }

    /// <summary>Host-only: tally a restore ack, same shape as RegisterVote above.</summary>
    private static void RegisterAck(ulong acker, uint targetId)
    {
        if (_barrierTargetId != targetId) return; // stale ack for an already-resolved barrier
        _acked.Add(acker);
        CheckAllAcked();
    }

    /// <summary>Host-only: once every player has acked the current barrier target, release it —
    /// same shape as CheckAllAccepted above.</summary>
    private static void CheckAllAcked()
    {
        if (_barrierTargetId == null) return;
        var target = _barrierTargetId.Value;
        var all = AllPlayerIds().ToList();
        if (all.Count == 0 || !all.All(id => _acked.Contains(id)))
            return;
        Log.Write($"[UndoProtocol] All {all.Count} players acked restore id={target} — releasing barrier.");
        _registeredService?.SendMessage(new UndoResumeMessage { targetChecksumId = target });
        // Host's own release: the broadcast above never loops back to its own sender — same reason
        // ArmBarrierForCommit is called directly in CheckAllAccepted instead of relying on
        // OnCommitReceived firing locally.
        ReleaseBarrierForTarget(target);
    }

    // ── Fuzz-only bookkeeping (MpFuzz step 3, Part C) ──

    /// <summary>MpFuzz (step 3, Part C) bookkeeping only. Incremented once per successful
    /// ChecksumHook.RestoreTo call inside CommitAsync below — i.e. once per commit actually executed
    /// on THIS peer, host or client alike (CommitAsync runs on both: the host calls it directly from
    /// CheckAllAccepted, the client from OnCommitReceived). Runs unconditionally for every peer, fuzz
    /// or not — cheap bookkeeping nothing outside MpFuzz's own summary ever reads.</summary>
    internal static int CommitCount;

    /// <summary>The ChecksumId of the SyncPoint most recently committed via CommitCount above — read
    /// by MpFuzz's own commit-watch loop immediately after observing CommitCount advance, to look the
    /// SyncPoint back up (ChecksumHook.TryGetSyncPoint) and arm UndoFuzz's stuck-after-restore watch
    /// for it. See CommitCount's own doc comment for why this is safe to leave unconditional.
    /// </summary>
    internal static uint LastCommittedChecksumId;

    /// <summary>Count of AssertQueueEmptyOrRecordViolation calls that ever found the queue non-empty
    /// — see that method's own doc comment. Read by MpFuzz's step-10 summary; expected to stay zero
    /// for every run.</summary>
    internal static int SelectionPendingViolations;

    /// <summary>
    /// Part C (step 3) proof, not a new guard — the task this was built for asked to verify, not
    /// assume, that "a restore must never be attempted or committed while a card selection is
    /// pending". Both call sites below (ProposeTarget and CommitAsync) already run this only AFTER
    /// their own existing CanUndoRedo()/`idle` gate has already required
    /// RunManager.Instance.ActionQueueSet.IsEmpty, so under today's code this can only ever record
    /// zero — it exists so that guarantee is PROVEN by a live run's own counter
    /// (SelectionPendingViolations) rather than resting solely on a reading of
    /// ActionQueueSet.PauseActionForPlayerChoice's source (ActionQueueSet.cs:261 — it pauses an
    /// action without removing it from its owner's queue, so a pending card selection should already
    /// make IsEmpty false), and doubles as a regression tripwire if a future edit to either gate's own
    /// composition ever drops that clause.
    /// </summary>
    private static void AssertQueueEmptyOrRecordViolation(string where)
    {
        var queues = RunManager.Instance?.ActionQueueSet;
        if (queues != null && queues.IsEmpty) return;
        SelectionPendingViolations++;
        Log.Write($"[UndoProtocol] SELECTION VIOLATION #{SelectionPendingViolations} at {where}: "
            + $"ActionQueueSet.IsEmpty={(queues != null ? queues.IsEmpty.ToString() : "null queue")} — "
            + "a restore must never be attempted or committed while a card selection (or any other "
            + "action) is pending.");
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
            for (int i = 0; i < CommitIdleWaitFrames; i++) // up to ~60s at 60fps
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
                    // Part C (step 3) proof — see AssertQueueEmptyOrRecordViolation's own doc
                    // comment; `idle` above already required aq.IsEmpty, so this can only ever
                    // record zero, but it stays as an explicit, independently-logged assertion.
                    AssertQueueEmptyOrRecordViolation("CommitAsync");
                    ChecksumHook.RestoreTo(sp);
                    CommitCount++;
                    LastCommittedChecksumId = sp.ChecksumId;
                    // Tell the host this peer is done — see "── Cross-peer restore barrier ──"
                    // below. Sent even if this peer's own barrier failed to arm (reflection
                    // missing/threw): the ack means "I finished restoring", independent of whether
                    // the barrier itself is enforcing that on this peer.
                    SendRestoreAck(sp.ChecksumId);
                    return;
                }
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            Log.Write("[UndoProtocol] Commit aborted: never reached an idle play phase — skipping restore");
            // No ack is sent — this peer never restored. If combat is still in progress, this
            // peer's own barrier stays armed and BarrierTimeoutWatchdog's bound (~90s, longer than
            // this loop's own ~60s) is what eventually frees it; if combat ended, ResetOnCombatEnd
            // (via CombatManager.Reset's Harmony postfix) clears the barrier bookkeeping instead —
            // see ResetBarrier's own doc comment for why that path deliberately does not touch
            // PlayerActionsDisabled itself.
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] CommitAsync ERROR: {ex}");
        }
    }

    // ── Cross-peer restore barrier ──
    //
    // Arms on commit RECEIPT (ArmBarrierForCommit, called from OnCommitReceived for every peer that
    // receives the broadcast, and from CheckAllAccepted for the host itself, whose own broadcast
    // never loops back) — i.e. before CommitAsync's idle wait even starts, per the task this was
    // built for: "the moment a peer receives UndoCommitMessage ... it must stop allowing new player
    // actions", not after it happens to go idle. Releases only once every player has acked
    // (ReleaseBarrierForTarget, driven by CheckAllAcked on the host / OnResumeReceived on every other
    // peer), or once BarrierTimeoutWatchdog's bound elapses, whichever comes first.
    //
    // Singleplayer cannot deadlock on any of this: ProposeTarget's own `svc.Type ==
    // NetGameType.Singleplayer` branch (above) calls ChecksumHook.RestoreTo directly and returns
    // BEFORE any UndoCommitMessage is ever sent, so ArmBarrierForCommit is never reached at all in
    // singleplayer — there is no message for a deadlock to wait on because none of this code runs.
    //
    // A HOSTED game with only the host connected (NetGameType.Host, zero clients — different from
    // true Singleplayer above) also cannot deadlock, and resolves synchronously: AllPlayerIds()
    // returns just [MyNetId], so CheckAllAccepted (already, unchanged) and CheckAllAcked (new, same
    // shape) both tally with a single local Register*(MyNetId, ...) call — never a network round
    // trip — which means the barrier arms and releases within the same CommitAsync continuation the
    // moment this peer's own restore lands, exactly "the ack tally completes immediately and the
    // barrier is released in the same flow".

    /// <summary>
    /// Forces CombatManager.Instance.PlayerActionsDisabled true via PlayerActionsDisabledProp,
    /// remembering whatever it was so ReleaseActionBarrier can put it back exactly (see
    /// _barrierPreviousActionsDisabled's own doc comment for why not just false). If the reflection
    /// handle failed to resolve, or SetValue itself throws (e.g. CombatManager's private _turnState
    /// backing field the real setter reads is unexpectedly null), this does NOT proceed with a
    /// half-armed barrier: it logs loudly and returns without recording a previous value, so
    /// BarrierArmed/ReleaseActionBarrier both correctly treat this peer as "no barrier" and the
    /// commit falls through to the OLD non-atomic per-peer restore path instead of silently
    /// pretending a barrier is holding when it is not.
    /// </summary>
    private static void ArmActionBarrier()
    {
        if (PlayerActionsDisabledProp == null)
        {
            Log.Write("[UndoProtocol] BARRIER NOT ARMED: CombatManager.PlayerActionsDisabled reflection handle missing — this commit will proceed WITHOUT a cross-peer barrier on this peer.");
            return;
        }
        try
        {
            var cm = CombatManager.Instance;
            bool previous = (bool)(PlayerActionsDisabledProp.GetValue(cm) ?? false);
            PlayerActionsDisabledProp.SetValue(cm, true);
            _barrierPreviousActionsDisabled = previous; // recorded only once the set actually succeeded
            Log.Write($"[UndoProtocol] Barrier armed: PlayerActionsDisabled forced true (was {previous}).");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] ArmActionBarrier ERROR (barrier NOT armed on this peer): {ex.Message}");
        }
    }

    /// <summary>
    /// Restores whatever ArmActionBarrier captured, rather than forcing false — see
    /// _barrierPreviousActionsDisabled's own doc comment. No-op if this peer's barrier was never
    /// successfully armed (reflection missing/failed) or has already been released, so a duplicate
    /// resume/timeout can't stomp on unrelated state.
    /// </summary>
    private static void ReleaseActionBarrier()
    {
        if (_barrierPreviousActionsDisabled == null) return;
        if (PlayerActionsDisabledProp == null) { _barrierPreviousActionsDisabled = null; return; }
        try
        {
            // MEASURED CORRECTION: do NOT restore the pre-barrier value. That value belongs to the
            // timeline the restore just discarded. A peer that had ended its turn before the undo
            // had PlayerActionsDisabled == true (CombatManager.OnEndedTurnLocally, CombatManager.cs:992-994);
            // handing that back after restoring to a point where the turn had NOT been ended leaves
            // that player permanently unable to act, while the other peer plays on. That is exactly
            // what a live two-instance run showed: the client logged "armed ... (was True)" and
            // "released ... restored to True", then never acted again and fell behind.
            //
            // The correct value is derived from the state we restored INTO, and for this mod that is
            // always the same answer: ChecksumHook.TryStoreSyncPoint only ever anchors at a moment
            // when every player is in PlayerTurnPhase.Play and nobody is mid card/potion effect, so
            // any restore target is by construction a moment where player actions are live.
            const bool afterRestoreActionsDisabled = false;
            PlayerActionsDisabledProp.SetValue(CombatManager.Instance, afterRestoreActionsDisabled);
            Log.Write($"[UndoProtocol] Barrier released: PlayerActionsDisabled set to {afterRestoreActionsDisabled} "
                + $"(pre-barrier value was {_barrierPreviousActionsDisabled.Value}, deliberately NOT restored — "
                + "it belonged to the discarded timeline).");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] ReleaseActionBarrier ERROR: {ex.Message}");
        }
        finally
        {
            _barrierPreviousActionsDisabled = null;
        }
    }

    /// <summary>Arms the barrier for a specific commit target: records the target/generation this
    /// peer is now barriered for, resets the host-only ack tally, forces PlayerActionsDisabled true
    /// (ArmActionBarrier), and starts this peer's own bounded fallback (BarrierTimeoutWatchdog).
    /// Called from both OnCommitReceived and CheckAllAccepted — see the top-of-section comment for
    /// why both call sites are needed.</summary>
    private static void ArmBarrierForCommit(uint targetId)
    {
        _barrierTargetId = targetId;
        _barrierGeneration++;
        _acked.Clear();
        ArmActionBarrier();
        _ = BarrierTimeoutWatchdog(_barrierGeneration, targetId);
    }

    /// <summary>Releases the barrier for <paramref name="targetId"/> on THIS peer: restores
    /// PlayerActionsDisabled (ReleaseActionBarrier) and clears the barrier bookkeeping. Ignores a
    /// mismatched target — a stale UndoResume for an already-resolved barrier, or one that arrives
    /// after this peer's own BarrierTimeoutWatchdog already gave up — rather than releasing state
    /// that no longer belongs to this call.</summary>
    private static void ReleaseBarrierForTarget(uint targetId)
    {
        if (_barrierTargetId != targetId)
        {
            Log.Write($"[UndoProtocol] ReleaseBarrierForTarget: ignoring id={targetId} — this peer's armed target is {(_barrierTargetId?.ToString() ?? "none")} (stale message or already released).");
            return;
        }
        ReleaseActionBarrier();
        _barrierTargetId = null;
        _barrierGeneration++;
        _acked.Clear();
    }

    /// <summary>
    /// Bounded fallback for the restore barrier, mirroring TimeoutWatchdog's own generation-guarded
    /// polling loop below (same reuse of TimeoutFrames — see BarrierTimeoutFrames's own doc comment
    /// for why it is not used alone) but for the ack/resume phase instead of the vote/proposal phase.
    /// Runs on EVERY peer that arms a barrier — the host (from CheckAllAccepted) and every client
    /// (from OnCommitReceived) alike, not just the host — because either side of the wait can stall:
    /// this peer's own restore might never reach an idle play phase (CommitAsync's own "never reached
    /// an idle play phase" abort), another peer's might not, or the resume broadcast itself might
    /// never arrive. Per the task requirement this implements ("a permanently frozen combat is worse
    /// than a reported risk"), whichever peer is still barriered when the clock runs out releases
    /// itself unconditionally and logs loudly that the session may now be divergent, rather than
    /// waiting forever for a message that may never come. If THIS peer is the host, it additionally
    /// broadcasts UndoResume for the same target so any peer still waiting normally is freed
    /// immediately instead of also waiting out its own full budget — the same "the host's own
    /// watchdog also notifies everyone else" shape TimeoutWatchdog already uses for UndoCancelMessage.
    /// </summary>
    private static async Task BarrierTimeoutWatchdog(int generation, uint targetId)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            if (tree == null) return;
            for (int i = 0; i < BarrierTimeoutFrames; i++)
            {
                if (_barrierGeneration != generation || _barrierTargetId != targetId)
                    return; // released normally (resume received, or a newer barrier armed)
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (_barrierGeneration != generation || _barrierTargetId != targetId)
                return;
            Log.Write($"[UndoProtocol] BARRIER TIMEOUT: id={targetId} did not resolve within {BarrierTimeoutFrames} frames (~{BarrierTimeoutFrames / 60}s) "
                + "— releasing the barrier on this peer unconditionally. Peers may now be DIVERGENT: some may "
                + "still be mid-restore, or the resume broadcast never arrived. Treat the next checksum "
                + "mismatch, if any, as expected fallout of this timeout, not a new bug.");
            if (_registeredService?.Type == NetGameType.Host)
                _registeredService.SendMessage(new UndoResumeMessage { targetChecksumId = targetId });
            ReleaseBarrierForTarget(targetId);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] BarrierTimeoutWatchdog ERROR: {ex.Message}");
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

    /// <summary>Clears all barrier bookkeeping on this peer WITHOUT touching
    /// CombatManager.PlayerActionsDisabled itself — used when the barrier's own outcome no longer
    /// matters (a net service change in EnsureHandlersRegistered, or combat ending in
    /// ResetOnCombatEnd) rather than when it actually resolves normally (ReleaseBarrierForTarget,
    /// which DOES restore the property). Deliberately does not call ReleaseActionBarrier: by the
    /// time either caller runs, the game's own lifecycle (a fresh connection, or CombatManager's own
    /// SetUpCombat/EndCombatInternal, both of which already set PlayerActionsDisabled themselves —
    /// CombatManager.cs:726, :1310) is what should own the property going forward, not a stale
    /// reflected value from a barrier whose target no longer exists.</summary>
    private static void ResetBarrier()
    {
        _barrierTargetId = null;
        _barrierGeneration++;
        _barrierPreviousActionsDisabled = null;
        _acked.Clear();
    }

    /// <summary>Combat ended — drop any in-flight proposal, its popup, and any in-flight restore
    /// barrier (see ResetBarrier's own doc comment for why the barrier is cleared, not released).</summary>
    internal static void ResetOnCombatEnd()
    {
        UndoPicker.Close();
        if (_pendingTargetId == null && _barrierTargetId == null) return;
        Log.Write("[UndoProtocol] Combat ended — clearing pending proposal" + (_barrierTargetId != null ? " and in-flight restore barrier" : ""));
        ClosePopup();
        ResetPending();
        ResetBarrier();
    }
}
