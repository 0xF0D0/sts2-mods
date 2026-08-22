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

/// <summary>
/// targetChecksumId alone is not enough to stop peers restoring at different points on the shared
/// logical clock. ChecksumTracker.NextId (ChecksumTracker.cs:57, public getter/private setter) IS
/// that clock: GenerateChecksum's own doc comment (ChecksumTracker.cs:83-84) requires it be "called
/// the exact same amount of times for every peer, otherwise false positive mismatches will be
/// generated". Arming CombatManager.PlayerActionsDisabled (see "── Cross-peer restore barrier ──"
/// below) only blocks NEW player input — it cannot undo work already enqueued before the barrier
/// armed, and already-enqueued work can keep draining, and generating checksums, after it does.
///
/// agreedChecksumId is Phase 4 of the "── Quiescence-quorum handshake ──" (see that section header
/// for the full propose→vote→quiesce→agree→commit→ack→resume flow) — the value here is NOT a target
/// the host guessed or sampled on its own. It is the value CheckQuiesceRound found every peer's own
/// UndoQuiesceReportMessage unanimously agreeing on, i.e. the NextId every peer, independently,
/// already SETTLED at while idle and stable (see UndoQuiesceReportMessage's own doc comment) —
/// broadcast here only once that unanimous agreement exists, not before. This retires an earlier
/// design (hostNextChecksumId) that sampled the host's own NextId up front, at the moment the vote
/// passed, and had every peer wait to catch up to it: 12 live two-instance runs (6 clean, 6 failed)
/// measured every peer reporting AHEAD of that sample by the time the commit arrived, because the
/// vote→commit round trip itself gave every peer time to keep draining pre-barrier work the sample
/// never accounted for — see the top-of-file iteration-4 note for the exact failing checksum stream
/// this produced. Agreeing on the clock AFTER everyone reports quiet, instead of guessing a target
/// before anyone has, is what the whole quiesce-quorum handshake exists to do.
///
/// Every peer — including the host itself, through the same CommitAsync path; see that method's own
/// doc comment for why it is not special-cased — re-checks its own NextId against agreedChecksumId
/// ONE more time, immediately before calling ChecksumHook.RestoreTo. Unlike the retired design, this
/// is not a wait loop: Phase 2/3 already proved every peer REACHED and HELD at this exact value, so
/// there is nothing left to wait for — only whether it has since moved (see CommitAsync's own
/// "moved-since-agreement" log line, deliberately distinct from a Phase 3 disagreement, for why
/// conflating the two would repeat a mistake this project has already made once). Iteration 5 (see
/// the top-of-file iteration list) changed what happens when it HAS moved: rather than that one peer
/// silently not restoring, it now requests a fresh round through the SAME machinery this message's
/// own `round` field exists for.
///
/// `round` is the quiesce round (see UndoQuiesceRequestMessage's own doc comment) whose unanimous
/// agreement produced THIS commit — carried here, not just implied, so a peer whose own Phase 4
/// recheck fails (CommitAsync's moved-since-agreement or never-re-idle branches) can report exactly
/// which attempt failed via UndoCommitFailedMessage (see that message's own doc comment), and the
/// host can tell a live report apart from a stale one for an attempt it has already moved past —
/// the same round-matching discipline RegisterQuiesceReport already applies to
/// UndoQuiesceReportMessage, reused here for RegisterCommitFailure's own guard
/// (_commitTargetId/_commitRound below) instead of a parallel bookkeeping scheme.
/// </summary>
public record struct UndoCommitMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public uint agreedChecksumId;
    public int round;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteUInt(agreedChecksumId);
        writer.WriteInt(round);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        agreedChecksumId = reader.ReadUInt();
        round = reader.ReadInt();
    }
}

/// <summary>
/// Iteration 5 (see the top-of-file iteration list): the counterpart to UndoRestoreAckMessage for a
/// peer that CANNOT restore. Sent by a peer whose own Phase 4 recheck (CommitAsync, after Phase 2/3
/// already agreed — see UndoCommitMessage's own doc comment) finds either that its NextId moved past
/// the agreed clock, or that it never became idle again at all — see CommitAsync's own doc comment
/// for why both are routed here identically. Before this message existed, a peer in either state
/// just returned: it never restored (so it never sent UndoRestoreAckMessage either), which left the
/// host's CheckAllAcked tally permanently one peer short and every peer's barrier
/// (CombatManager.PlayerActionsDisabled) armed forever — the exact deadlock a live two-instance run
/// measured (see this file's top-of-file iteration-5 note).
///
/// Same shape as UndoQuiesceReportMessage — target id, round, reporter id — and the same "host
/// tallies, self-report never loops back" pattern every other host-tallied message here uses
/// (RequestCommitRetryOrGiveUp calls RegisterCommitFailure directly when the reporting peer IS the
/// host, exactly like SendQuiesceReport/SendRestoreAck do for their own host-self case).
///
/// `round` is UndoCommitMessage's own `round` field echoed back — RegisterCommitFailure checks it
/// against the host-only _commitTargetId/_commitRound below before acting, so a stale report for an
/// attempt the host has already retried or given up on (e.g. a second peer's own failure for the
/// SAME round, arriving after the first one already claimed it) is a harmless no-op instead of a
/// second, duplicate RequestNextQuiesceRound/UndoCancelMessage.
///
/// Handling this is NOT "the abort" itself — the host's decision (RegisterCommitFailure ->
/// AdvanceQuiesceRoundOrGiveUp) either retries via the SAME UndoQuiesceRequestMessage/
/// RequestNextQuiesceRound machinery Phase 3's own disagreement retry already uses, or, once the
/// round budget is exhausted, gives up via the SAME UndoCancelMessage broadcast a rejected vote
/// already uses — see AdvanceQuiesceRoundOrGiveUp's own doc comment. This message only ever gets a
/// failure IN FRONT of the host so that decision can be made at all; it is not itself broadcast to
/// other clients doing anything (ShouldBroadcast is true only for parity with every other message in
/// this file — non-host recipients log it and return, same as OnVoteReceived/OnRestoreAckReceived/
/// OnQuiesceReportReceived already do for their own host-only tallies).
/// </summary>
public record struct UndoCommitFailedMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int round;
    public ulong reporterNetId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(round);
        writer.WriteULong(reporterNetId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        round = reader.ReadInt();
        reporterNetId = reader.ReadULong();
    }
}

/// <summary>
/// Phase 2 of the "── Quiescence-quorum handshake ──" (see that section header for the full flow).
/// Broadcast by the host once every player has voted to accept (CheckAllAccepted) — round 0 — or
/// re-broadcast for a later round when Phase 3 (CheckQuiesceRound) finds the previous round's
/// UndoQuiesceReportMessages disagreed (RequestNextQuiesceRound). Every peer, host included through
/// the exact same OnQuiesceRequestReceived path used for every other peer, treats receipt as "start
/// (or redo) local quiescence detection for this round and report your settled clock back to me"
/// (BeginQuiesceRound).
///
/// `round` is what lets a peer tell a fresh request apart from one it is already resolving, and lets
/// its eventual UndoQuiesceReportMessage.round be matched back to the request that produced it — the
/// same generation-guard shape _pendingGeneration/_barrierGeneration already use to keep this file's
/// OWN async loops from racing a newer attempt, carried onto the wire here because this "which
/// attempt is current" question has to be agreed with the HOST, not just checked locally.
/// </summary>
public record struct UndoQuiesceRequestMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int round;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(round);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        round = reader.ReadInt();
    }
}

/// <summary>
/// Phase 2's answer to UndoQuiesceRequestMessage above, and the input to Phase 3
/// (CheckQuiesceRound). Sent by EVERY peer, host included (SendQuiesceReport reuses the same
/// self-tally pattern RegisterVote/RegisterAck already use for the host's own vote/ack, since a
/// host's own broadcast never loops back to itself), once its OWN local quiescence wait
/// (BeginQuiesceRound) finds BOTH: the same local-idle condition CommitAsync's gate 1 already used
/// before this handshake existed (IsLocallyIdle — CurrentSide == Player, PlayPhase,
/// ActionQueueSet.IsEmpty, not mid-transition), AND ChecksumTracker.NextId (ChecksumTracker.cs:57,
/// public getter) unchanged for QuiesceStablePolls consecutive polls while idle (see that constant's
/// own doc comment for the count and why). NextId at that moment IS settledChecksumId — the clock
/// value this peer has independently PROVEN it is done moving away from, not a live value still in
/// motion. `round` echoes the UndoQuiesceRequestMessage this answers, so a stale report for a round
/// the host has already moved past (CheckQuiesceRound's own round mismatch guard) is ignored instead
/// of corrupting a newer tally.
/// </summary>
public record struct UndoQuiesceReportMessage : INetMessage
{
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public uint targetChecksumId;
    public int round;
    public ulong reporterNetId;
    public uint settledChecksumId;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(round);
        writer.WriteULong(reporterNetId);
        writer.WriteUInt(settledChecksumId);
    }

    public void Deserialize(PacketReader reader)
    {
        targetChecksumId = reader.ReadUInt();
        round = reader.ReadInt();
        reporterNetId = reader.ReadULong();
        settledChecksumId = reader.ReadUInt();
    }
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
//                 point after their action queue drains AND the shared checksum clock
//                 catches up (see below). Any reject → UndoCancel.
//
// This went through five iterations, each closing a divergence a live two-instance run actually
// measured:
//
//   1. Original: each peer restored independently once idle, with nothing stopping a peer that
//      finished first from continuing to play while a slower peer was still mid-restore — measured
//      726ms apart, long enough for an ordinary card play to execute on the faster peer and never
//      reach the slower one before it, too, restored (see this file's git history). Fix: a barrier
//      (CombatManager.PlayerActionsDisabled, reached via reflection — see PlayerActionsDisabledProp's
//      own doc comment for why) that blocks new player input until every peer has restored and
//      acked (UndoRestoreAckMessage) and the host has broadcast UndoResume.
//
//   2. The barrier alone still wasn't enough: it blocks NEW player input but cannot undo work
//      already enqueued before it armed, and a run measured a peer draining that pre-barrier work
//      into two EXTRA checksums after everyone else had stopped — a different COUNT of
//      GenerateChecksum calls, which desynchronises ChecksumTracker.NextId (the peers' shared
//      logical clock) for good. Fix (superseded by iteration 4 below): CommitAsync's idle wait ALSO
//      waited for this peer's own NextId to reach a target the host sampled up front.
//
//   3. Arming the barrier on UndoCommitMessage receipt was STILL too late: nothing held any peer
//      through the propose→vote→tally round trip that precedes the commit, so a run measured both
//      peers' NextId already past the host's commit-time sample — by a DIFFERENT amount each —
//      before the commit even arrived. Fix: the barrier now arms at PROPOSAL time (ProposeTarget for
//      the proposer's own barrier, OnProposalReceived for every other peer), before the vote even
//      starts. This also matches the real user expectation: once a player proposes an undo,
//      everyone's input is held until it resolves, not just once the vote finishes.
//
//   4. Iteration 2's fix — the host sampling ChecksumTracker.NextId up front, at the moment the vote
//      passed, and every peer waiting to catch up to it — was ITSELF measured to guess wrong: 12 live
//      two-instance runs (6 clean, 6 failed) showed every peer reporting AHEAD of that sample by the
//      time the commit arrived, because the vote→commit round trip gave every peer time to keep
//      draining pre-barrier work the sample never accounted for. A failing run's raw streams:
//        host  : 0..7 [restore→id2] 8 9 10 11    [restore→id2] 12 13 14 15
//        client: 0..7 [restore→id2] 8 9 10 11 12 [restore→id2] 13 14
//      — every hash matched through id=11; on the SECOND undo the host restored right after id=11
//      while the client processed one more action (id=12) first, so from then on the same checksum
//      number denotes a different moment on each peer. Fix: stop guessing a target up front and
//      instead AGREE on the clock after everyone is quiet — see the "── Quiescence-quorum handshake
//      ──" section further down for the full propose→vote→quiesce→agree→commit→ack→resume flow this
//      replaces iteration 2/3's commit step with.
//
//   5. Iteration 4's own Phase 4 recheck (CommitAsync's "moved-since-agreement" gate) was, once it
//      actually fired, WORSE than iteration 4's disease: a live two-instance run's host log showed
//      quiesce round 0 AGREE at NextId=13, then — in the very same synchronous call, before the
//      client had even received the commit broadcast — the host's own local NextId had already
//      ticked to 14. CommitAsync logged "COMMIT ABORTED (phase 4, moved-since-agreement) ... skipping
//      restore" and returned. Two compounding bugs, not one: (a) that bare `return` never sent
//      UndoRestoreAckMessage, so the host's own CheckAllAcked tally sat one ack short FOREVER — every
//      peer's barrier (CombatManager.PlayerActionsDisabled) stayed armed for the rest of combat, the
//      run dying at turnsPlayed=0; (b) the comment on that abort said to "treat the next checksum
//      mismatch ... as expected fallout of this peer alone not restoring" — i.e. it accepted exactly
//      the split-brain (one peer restores, one doesn't) the whole quiesce-quorum handshake exists to
//      prevent, rather than the transient "stale agreement" it actually was (an action landing between
//      Phase 3 agreeing and Phase 4 rechecking — the same class of lag Phase 3's own disagreement
//      retry already tolerates, just one phase later). Fix: a Phase 4 failure (moved-since-agreement,
//      or the sibling "never re-idle" timeout) no longer returns silently. It reports itself to the
//      host (UndoCommitFailedMessage, sent directly if the failing peer IS the host — a self-broadcast
//      never loops back, same as every other message here) and the host retries through the SAME
//      round machinery Phase 3's own disagreement already uses (AdvanceQuiesceRoundOrGiveUp,
//      RequestNextQuiesceRound), bounded by the same MaxQuiesceRounds budget — not a second, parallel
//      retry limit. Only once that budget is exhausted does the host abort the whole proposal, and it
//      does so on EVERY peer via the same UndoCancelMessage broadcast a rejected vote already uses —
//      never just locally. A try/finally around CommitAsync's whole body (see its own doc comment)
//      guarantees one of these two outcomes — an ack, or a failure report — happens on every exit
//      path, including an unhandled exception, so a peer can no longer vanish from the host's tally
//      the way this bug did.
//
// See "── Cross-peer restore barrier ──" further down for the full arm/release mechanism, including
// every path that can end a proposal without a commit (rejected vote, proposer cancel, timeout, or —
// since iteration 4 — exhausting the quiesce-quorum's own retry budget, now reachable from a Phase 4
// failure too, not only a Phase 3 disagreement — see iteration 5 above). See "── Quiescence-quorum
// handshake ──" for what happens between the vote passing and the final commit.
internal static class UndoProtocol
{
    private static INetGameService? _registeredService;

    private static uint? _pendingTargetId;
    private static int _pendingGeneration;
    private const int TimeoutFrames = 30 * 60; // ~30s at 60fps
    private static readonly HashSet<ulong> _accepted = new();
    private static NGenericPopup? _popup;

    /// <summary>The checksum id the quiescence-quorum handshake (see that section header) is
    /// currently running for on THIS peer, or null if no quiesce phase is in progress. Deliberately
    /// separate from _pendingTargetId above, the same way _barrierTargetId already is and for the
    /// same reason: RequestNextQuiesceRound/OnQuiesceRequestReceived reset the vote-phase bookkeeping
    /// (_pendingTargetId et al.) the instant round 0 starts, but this field — and the round counter
    /// and report tally below — must keep living for however many rounds Phase 3 needs, which can run
    /// well past where _pendingTargetId would otherwise have been cleared.</summary>
    private static uint? _quiesceTargetId;

    /// <summary>The quiesce round THIS peer most recently received/started (every peer) or is
    /// currently collecting reports for (host only) — round 0 is the first attempt, right after the
    /// vote passes; round N is Phase 3's (N-1)th retry after a disagreement. Used both to stamp
    /// outgoing UndoQuiesceReportMessages and, on the host, as the key RegisterQuiesceReport checks a
    /// report against before accepting it into _quiesceReports (a report for any other round is
    /// stale). Reset to 0 by ResetQuiesce, which also nulls _quiesceTargetId — a round number alone,
    /// without a matching target, is never trusted (see RegisterQuiesceReport's own guard).</summary>
    private static int _quiesceRound;

    /// <summary>Host-only: this round's tally of each peer's reported settledChecksumId, keyed by
    /// reporter — unlike _accepted/_acked (presence-only HashSets), Phase 3 needs the actual VALUES
    /// peers reported, not just who has reported, to tell agreement from disagreement
    /// (CheckQuiesceRound). Cleared and rebuilt every round by RequestNextQuiesceRound. Harmless and
    /// unread on non-host peers, same as _acked.</summary>
    private static readonly Dictionary<ulong, uint> _quiesceReports = new();

    /// <summary>Host-only: the target/round of the commit attempt most recently broadcast via
    /// UndoCommitMessage (set in CheckQuiesceRound's AGREED branch), i.e. the attempt
    /// RegisterCommitFailure below is currently willing to act on a failure report for. Deliberately
    /// NOT the same field as _quiesceTargetId/_quiesceRound above: those get reset (ResetQuiesce) the
    /// instant a commit is decided — CheckQuiesceRound's AGREED branch and OnCommitReceived both call
    /// it right after kicking off CommitAsync — because Phase 2/3 legitimately consider themselves
    /// "done" at that point. A Phase 4 failure, though, is discovered AFTER that reset already ran
    /// (CommitAsync's own recheck happens inside the async call CheckQuiesceRound fires off, and for a
    /// client it happens after an entire network round trip), so a guard reusing the already-cleared
    /// _quiesceTargetId/_quiesceRound would reject every legitimate failure report as "stale" and this
    /// fix would never fire. See RegisterCommitFailure's own doc comment for how this field's guard
    /// keeps a second peer's failure report for the SAME already-claimed round from triggering a
    /// second, duplicate retry/give-up.</summary>
    private static uint? _commitTargetId;

    /// <summary>Paired with _commitTargetId above — see that field's own doc comment. Not reset
    /// alongside _quiesceRound by ResetQuiesce for the same reason.</summary>
    private static int _commitRound;

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

    /// <summary>Originally sized to match CommitAsync's own idle-wait loop bound; now also reused as
    /// Phase 2's round-0 quiesce-wait bound (QuiesceWaitFrames below) — round 0 can legitimately take
    /// this long for the same reason CommitAsync's old gate 1 could: an idle PLAYER play phase can be
    /// a while away if the OTHER side is still deep in a long enemy turn (many monster moves, visual
    /// effect sequences) when the vote passes. Kept as one named constant, reused by both, instead of
    /// two copies of the same magic number that could silently drift apart.</summary>
    private const int CommitIdleWaitFrames = 60 * 60; // ~60s at 60fps

    /// <summary>How many consecutive idle polls of an UNCHANGED ChecksumTracker.NextId
    /// (ChecksumTracker.cs:57) BeginQuiesceRound requires before calling this peer's clock "settled"
    /// and reporting it (see UndoQuiesceReportMessage's own doc comment). Picked, not derived: it
    /// only needs to outlast a straggling in-flight action's own local execution — once a queued
    /// action actually arrives, it executes and checksums near-instantly (a frame or two), it is the
    /// NETWORK transit before that which this bridges. 10 frames (~167ms at 60fps) is generous slack
    /// over ordinary LAN/typical-WAN one-way latency (tens to a couple hundred ms) while staying small
    /// enough that a healthy quiesce never feels like added latency to the player proposing the undo.</summary>
    private const int QuiesceStablePolls = 10; // ~167ms at 60fps

    /// <summary>Phase 2's round-0 wait bound — see CommitIdleWaitFrames's own doc comment for why
    /// round 0 specifically (unlike every later retry, QuiesceRetryWaitFrames below) needs the full
    /// ~60s: this is the FIRST time this peer is asked to be idle for this proposal, so a still-live
    /// enemy turn is a real possibility, not just network jitter.</summary>
    private const int QuiesceWaitFrames = CommitIdleWaitFrames; // ~60s at 60fps

    /// <summary>Wait bound for quiesce round ≥ 1 (a Phase 3 retry). Deliberately much shorter than
    /// QuiesceWaitFrames: by the time a retry happens, THIS peer already successfully reported once
    /// for round 0 — i.e. it already proved it was in an idle player play phase, and nothing can end
    /// that phase again while the cross-peer barrier (CombatManager.PlayerActionsDisabled) is still
    /// forcing player input off. A retry only ever has to wait out a residual straggling action still
    /// landing over the network, the same order-of-magnitude gap QuiesceStablePolls itself bridges —
    /// 5s is an order of magnitude of margin over that, not a tuned value.</summary>
    private const int QuiesceRetryWaitFrames = 5 * 60; // ~5s at 60fps

    /// <summary>Pure breathing room between a Phase 3 disagreement and the next round's request —
    /// gives the peer that was behind a moment to actually finish draining before being asked again,
    /// instead of the host hammering out a new request the instant the old one's tally completes.
    /// ~0.5s is comfortably longer than a single network round trip on any connection this handshake
    /// is expected to run over (the vote phase's own TimeoutFrames budgets whole seconds for a full
    /// round trip plus human reaction time), while staying short enough that a multi-round retry still
    /// resolves quickly in the common case.</summary>
    private const int QuiesceRetryIntervalFrames = 30; // ~0.5s at 60fps

    /// <summary>Slack QuiesceRoundTimeoutWatchdog (host-only) adds on top of a round's own
    /// peer-side wait bound (QuiesceWaitFrames/QuiesceRetryWaitFrames) before declaring the round
    /// incomplete — covers the one extra network hop a peer's report needs after ITS OWN bound
    /// permits it to settle at the very last eligible frame, so the host doesn't declare a round dead
    /// a moment before a legitimate, on-time report would have arrived.</summary>
    private const int QuiesceHostRoundMargin = 60; // ~1s at 60fps

    /// <summary>Bound on how many quiesce rounds this proposal will attempt before giving up and
    /// cancelling the undo (AdvanceQuiesceRoundOrGiveUp) — round 0 plus up to 2 retries, SHARED
    /// across both triggers that can now advance a round: a Phase 3 disagreement (peers settled at
    /// different values) and, since iteration 5, a Phase 4 commit failure (peers agreed, but one
    /// peer's own recheck immediately before restoring found it could not safely restore to that
    /// agreement — see RegisterCommitFailure's own doc comment). Deliberately ONE budget, not a
    /// separate counter per trigger — see AdvanceQuiesceRoundOrGiveUp's own doc comment for why both
    /// funnel into the same round-advancing decision. Both causes are measured to be transient and
    /// typically a single checksum id's worth of lag (see the "── Quiescence-quorum handshake ──"
    /// section header for the exact failing stream Phase 3's own disagreement retries past, and this
    /// file's top-of-file iteration-5 note for the equivalent Phase 4 case), so most real runs
    /// converge on round 0 or the first retry regardless of which trigger caused it; a second retry is
    /// slack for an unlucky repeat, not the expected path. Kept small deliberately — see
    /// BarrierTimeoutFrames's own doc comment for how this bound feeds into the outer barrier
    /// timeout's arithmetic.</summary>
    private const int MaxQuiesceRounds = 3;

    /// <summary>Phase 4's own bound (CommitAsync, after Phase 2/3 already agreed on a value): how
    /// long to wait for IsLocallyIdle() to hold again immediately before restoring. Deliberately much
    /// shorter than the retired design's ~60s commit wait — by Phase 4, every peer has ALREADY proven
    /// it was idle a moment ago (its own Phase 2 report), and nothing can end that idle phase while
    /// the barrier is armed (same reasoning as QuiesceRetryWaitFrames), so this only needs to absorb
    /// the commit broadcast's own network transit, not a fresh wait for a possibly-long enemy turn.</summary>
    private const int CommitReidleWaitFrames = 5 * 60; // ~5s at 60fps

    /// <summary>
    /// Bound for BarrierTimeoutWatchdog (see its own doc comment) — the ABSOLUTE last resort if
    /// every other release path (normal resume, a rejected vote, a proposer cancel, the proposal's
    /// own TimeoutWatchdog, or the quiesce-quorum's own round budget — see the "── Cross-peer restore
    /// barrier ──" section header for the full list) somehow never fires for this peer.
    ///
    /// The barrier now arms at PROPOSAL time (ProposeTarget/OnProposalReceived), not commit time, so
    /// this bound has to cover five phases end to end:
    ///   1. TimeoutFrames — the vote/proposal phase itself (this proposal's own TimeoutWatchdog
    ///      bound), which runs entirely INSIDE the barrier's armed window instead of before it.
    ///   2. QuiesceWaitFrames + QuiesceHostRoundMargin — round 0 of the quiescence-quorum handshake
    ///      (see that section header), the host's own worst-case bound on it completing at all.
    ///   3. (MaxQuiesceRounds − 1) × (QuiesceRetryIntervalFrames + QuiesceRetryWaitFrames +
    ///      QuiesceHostRoundMargin + CommitReidleWaitFrames) — every retry round this handshake is
    ///      allowed to attempt, each bounded the same way as round 0 but on the much shorter retry
    ///      budget (see QuiesceRetryWaitFrames's own doc comment for why a retry doesn't need round
    ///      0's full budget). The trailing + CommitReidleWaitFrames term is iteration 5 (see the
    ///      top-of-file iteration list): since a Phase 4 failure (CommitAsync's moved-since-agreement
    ///      or never-re-idle branches) now ALSO triggers a retry through this same round machinery
    ///      (RegisterCommitFailure → AdvanceQuiesceRoundOrGiveUp), every retry round budgeted here
    ///      might ITSELF reach agreement and then fail its own Phase 4 recheck before moving on to the
    ///      next round — a window this bound has to cover too, not just the FINAL round's (term 4
    ///      below already covers that one).
    ///   4. CommitReidleWaitFrames — Phase 4's own re-idle recheck immediately before RestoreTo, for
    ///      whichever round ends up being the last one attempted.
    ///   5. TimeoutFrames again — margin for the ack/resume round trip once every peer that needed to
    ///      restore actually has, the same margin every earlier design already reserved for this
    ///      phase alone.
    /// With MaxQuiesceRounds = 3: 1800 + (3600+60) + 2×(30+300+60+300) + 300 + 1800 = 8940 frames,
    /// ~149s total from the moment a barrier arms — 600 frames (~10s) more than iteration 4's own
    /// 8340-frame bound, entirely attributable to the new tail of term 3 above (up to 2 retry rounds,
    /// each now capable of its own Phase 4 failure before the NEXT round even starts). Without this
    /// margin, a barrier could theoretically be released here — "may now be DIVERGENT", per this
    /// watchdog's own log line — WHILE a legitimate, still-converging retry sequence was in progress,
    /// which would defeat the whole point of iteration 5's retry-instead-of-abort fix.
    /// </summary>
    private const int BarrierTimeoutFrames =
        TimeoutFrames
        + (QuiesceWaitFrames + QuiesceHostRoundMargin)
        + (MaxQuiesceRounds - 1) * (QuiesceRetryIntervalFrames + QuiesceRetryWaitFrames + QuiesceHostRoundMargin + CommitReidleWaitFrames)
        + CommitReidleWaitFrames
        + TimeoutFrames;

    internal static void EnsureHandlersRegistered()
    {
        var svc = RunManager.Instance?.NetService;
        if (svc == null || ReferenceEquals(svc, _registeredService))
            return;
        if (_registeredService != null)
        {
            _registeredService.UnregisterMessageHandler<UndoProposalMessage>(OnProposalReceived);
            _registeredService.UnregisterMessageHandler<UndoVoteMessage>(OnVoteReceived);
            _registeredService.UnregisterMessageHandler<UndoQuiesceRequestMessage>(OnQuiesceRequestReceived);
            _registeredService.UnregisterMessageHandler<UndoQuiesceReportMessage>(OnQuiesceReportReceived);
            _registeredService.UnregisterMessageHandler<UndoCommitMessage>(OnCommitReceived);
            _registeredService.UnregisterMessageHandler<UndoCommitFailedMessage>(OnCommitFailedReceived);
            _registeredService.UnregisterMessageHandler<UndoCancelMessage>(OnCancelReceived);
            _registeredService.UnregisterMessageHandler<UndoRestoreAckMessage>(OnRestoreAckReceived);
            _registeredService.UnregisterMessageHandler<UndoResumeMessage>(OnResumeReceived);
        }
        svc.RegisterMessageHandler<UndoProposalMessage>(OnProposalReceived);
        svc.RegisterMessageHandler<UndoVoteMessage>(OnVoteReceived);
        svc.RegisterMessageHandler<UndoQuiesceRequestMessage>(OnQuiesceRequestReceived);
        svc.RegisterMessageHandler<UndoQuiesceReportMessage>(OnQuiesceReportReceived);
        svc.RegisterMessageHandler<UndoCommitMessage>(OnCommitReceived);
        svc.RegisterMessageHandler<UndoCommitFailedMessage>(OnCommitFailedReceived);
        svc.RegisterMessageHandler<UndoCancelMessage>(OnCancelReceived);
        svc.RegisterMessageHandler<UndoRestoreAckMessage>(OnRestoreAckReceived);
        svc.RegisterMessageHandler<UndoResumeMessage>(OnResumeReceived);
        _registeredService = svc;
        ResetPending();
        ResetQuiesce();
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
            // A previous undo's barrier (see "── Cross-peer restore barrier ──" below) hasn't
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
            // which a DIFFERENT peer's proposal could have arrived and armed the barrier (the
            // barrier now arms on the proposal itself, not on a later commit — see
            // ArmBarrierForProposal's own doc comment).
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
        // Arm THIS peer's own barrier now — before the proposal is even sent, not later at commit
        // time. See the "── Cross-peer restore barrier ──" section header for why commit-time
        // arming was measured too late: nothing was holding either peer through the
        // propose→vote→tally round trip, so both peers' ChecksumTracker.NextId had already blown
        // past the host's commit-time sample, by a different amount each, before the commit even
        // arrived. CanUndoRedo() a few lines up already required this peer's own queue to be empty,
        // so there is nothing in flight here that this arm call needs to race against.
        ArmBarrierForProposal(target.ChecksumId);
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
            // PREVIOUS undo attempt hasn't released yet — it may still be mid its own vote, mid its
            // own idle-wait/restore, or still waiting on the host's UndoResume (arming now happens at
            // PROPOSAL time, so "still armed" covers the whole lifecycle, not just the restore tail).
            // Auto-reject exactly like the missing-sync-point case below: the proposer's own
            // "Waiting" popup needs a real UndoVoteMessage(accept: false) to resolve into an
            // UndoCancelMessage, not silence. This peer's OWN barrier is left untouched here — it
            // belongs to the OLDER proposal and resolves through that proposal's own release path,
            // not this new one.
            Log.Write($"[UndoProtocol] Proposal id={msg.targetChecksumId} received while this peer's own restore barrier (target={_barrierTargetId}) is still armed — auto-rejecting so nobody diverges.");
            SubmitLocalVote(accept: false);
            return;
        }

        // Arm THIS peer's own barrier for the NEW proposal now, unconditionally — before deciding
        // how to vote, not after. See ProposeTarget's own arm call and the section header for why:
        // every peer's input must be held for the WHOLE propose→vote→commit window, not just from
        // commit onward, or peers keep drifting apart during the vote round trip. Even a peer about
        // to auto-reject just below (missing sync point) arms first: that reject still has to travel
        // to the host and come back as an UndoCancelMessage before this peer is free again (see
        // OnCancelReceived's own release call), and other peers may still be mid-vote in the
        // meantime.
        ArmBarrierForProposal(msg.targetChecksumId);

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

    /// <summary>
    /// Phase 2 entry point on every peer OTHER than the host that just decided to send this (the
    /// host calls BeginQuiesceRound directly from RequestNextQuiesceRound — a self-broadcast never
    /// loops back). See UndoQuiesceRequestMessage's own doc comment for the full round mechanism.
    ///
    /// round 0 is special: it is this peer's first news that the VOTE has resolved (there is no
    /// separate "vote passed" message — this doubles as it), so this is where the vote-phase
    /// bookkeeping (_pendingTargetId/_pendingGeneration/_accepted) finally gets released via
    /// ResetPending(), the same transition point CheckAllAccepted uses on the host — see
    /// RequestNextQuiesceRound's own doc comment for why this can't happen any earlier (the vote's
    /// own TimeoutWatchdog needs _pendingTargetId to keep meaning "still voting" for its whole
    /// bounded window) or any later (the quiesce phase's own, longer bounds need to be the ones
    /// governing from here on, not the 30s vote-phase timeout still racing it).
    /// </summary>
    private static void OnQuiesceRequestReceived(UndoQuiesceRequestMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Quiesce request round={msg.round} for id={msg.targetChecksumId}");
        if (msg.round == 0)
        {
            if (_pendingTargetId != msg.targetChecksumId)
            {
                Log.Write($"[UndoProtocol] Ignoring quiesce request: pending target is {(_pendingTargetId?.ToString() ?? "none")}, message says {msg.targetChecksumId}");
                return;
            }
            ResetPending();
        }
        else if (_quiesceTargetId != msg.targetChecksumId)
        {
            // A retry round for a proposal this peer isn't (or is no longer) quiescing for — stale
            // message, or this peer's own copy of the handshake already ended some other way (e.g.
            // BarrierTimeoutWatchdog gave up). Ignore rather than starting a quiesce wait with
            // nothing to eventually resolve it.
            Log.Write($"[UndoProtocol] Ignoring quiesce retry: no in-progress quiesce phase for id={msg.targetChecksumId} (this peer is at {(_quiesceTargetId?.ToString() ?? "none")})");
            return;
        }
        _quiesceTargetId = msg.targetChecksumId;
        _quiesceRound = msg.round;
        _ = BeginQuiesceRound(msg.targetChecksumId, msg.round);
    }

    /// <summary>Host-only: tally one peer's settled-clock report, same shape as OnVoteReceived /
    /// OnRestoreAckReceived above — the real staleness/target guard lives in RegisterQuiesceReport
    /// (it also has to run for the host's OWN report, which never arrives as a message; see
    /// SendQuiesceReport), so this handler stays a thin dispatch like OnRestoreAckReceived.</summary>
    private static void OnQuiesceReportReceived(UndoQuiesceReportMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Quiesce report from {msg.reporterNetId}: round={msg.round} settled={msg.settledChecksumId} (target id={msg.targetChecksumId})");
        if (_registeredService?.Type != NetGameType.Host) return; // host tallies
        RegisterQuiesceReport(msg.reporterNetId, msg.targetChecksumId, msg.round, msg.settledChecksumId);
    }

    private static void OnCommitReceived(UndoCommitMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Commit received for id={msg.targetChecksumId} agreedChecksumId={msg.agreedChecksumId}");
        // The barrier is already up by now — armed back in OnProposalReceived when this peer first
        // saw the proposal for this same target, not here (arming on commit receipt was measured
        // too late — see the "── Cross-peer restore barrier ──" section header). Defensive check
        // only: a mismatch would mean the barrier lifecycle broke upstream — log loudly rather than
        // silently re-arming over it, which would also stomp _barrierPreviousActionsDisabled with
        // the barrier's OWN forced value instead of whatever it truly was before arming (see
        // CheckAllAccepted's identical check for the same reasoning).
        if (_barrierTargetId != msg.targetChecksumId)
            Log.Write($"[UndoProtocol] OnCommitReceived: barrier target mismatch — armed for {(_barrierTargetId?.ToString() ?? "none")}, commit says {msg.targetChecksumId}. This should be impossible now that the barrier arms at proposal time; investigate if seen.");
        ClosePopup();
        _ = CommitAsync(msg.targetChecksumId, msg.agreedChecksumId, msg.round);
        ResetPending(); // no-op by now on every peer except a host that somehow reached here (it never does — self-broadcast)
        ResetQuiesce();
    }

    /// <summary>Host-only: dispatch for UndoCommitFailedMessage — see that message's own doc comment.
    /// Thin, same shape as OnQuiesceReportReceived/OnRestoreAckReceived: the real staleness/target
    /// guard lives in RegisterCommitFailure (it also has to run for the host's OWN failure, which
    /// never arrives as a message — see RequestCommitRetryOrGiveUp).</summary>
    private static void OnCommitFailedReceived(UndoCommitFailedMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Commit failure reported by {msg.reporterNetId}: round={msg.round} (target id={msg.targetChecksumId})");
        if (_registeredService?.Type != NetGameType.Host) return; // host decides retry vs give up
        RegisterCommitFailure(msg.targetChecksumId, msg.round);
    }

    private static void OnCancelReceived(UndoCancelMessage msg, ulong senderId)
    {
        Log.Write($"[UndoProtocol] Cancelled by {msg.byNetId} (target id={msg.targetChecksumId})");
        ClosePopup();
        ResetPending();
        ResetQuiesce(); // no-op if this cancel arrived during the vote phase, before any quiesce round started
        // Release THIS peer's own barrier for the cancelled target — armed back in
        // OnProposalReceived/ProposeTarget when the (now-cancelled) proposal first arrived here.
        // ReleaseBarrierForTarget already no-ops on a mismatched/stale target, so this is harmless
        // even for a peer whose barrier already resolved some other way (e.g. its own
        // TimeoutWatchdog firing first).
        ReleaseBarrierForTarget(msg.targetChecksumId);
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

    /// <summary>Host-only: tally a vote, then commit or cancel. A reject releases the barrier too —
    /// see the "── Cross-peer restore barrier ──" section header for the full list of release paths
    /// this joins.</summary>
    private static void RegisterVote(ulong voter, bool accept)
    {
        if (_pendingTargetId == null) return;
        var target = _pendingTargetId.Value;
        if (!accept)
        {
            Log.Write($"[UndoProtocol] {voter} rejected — cancelling");
            _registeredService?.SendMessage(new UndoCancelMessage { targetChecksumId = target, byNetId = voter });
            ClosePopup();
            // Host's own release: the broadcast above never loops back to its own sender — same
            // reason ArmBarrierForProposal is called directly in ProposeTarget/OnProposalReceived
            // rather than relying on a self-sent message looping back. Every OTHER peer releases via
            // OnCancelReceived once this broadcast reaches it.
            ReleaseBarrierForTarget(target);
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
        // The host's OWN barrier for `target` is already armed by now — from ProposeTarget if the
        // host itself is the proposer, or from OnProposalReceived if it received the proposal from
        // someone else — never from here (arming on commit was measured too late; see the
        // "── Cross-peer restore barrier ──" section header). A mismatch would mean the barrier
        // lifecycle broke upstream; log it loudly rather than silently re-arming over it, which
        // would also stomp _barrierPreviousActionsDisabled with the barrier's OWN forced value
        // instead of whatever it truly was before arming.
        if (_barrierTargetId != target)
            Log.Write($"[UndoProtocol] CheckAllAccepted: barrier target mismatch at commit time — armed for {(_barrierTargetId?.ToString() ?? "none")}, committing {target}. This should be impossible now that the barrier arms at proposal time; investigate if seen.");
        Log.Write($"[UndoProtocol] All {all.Count} players accepted for id={target} — starting the quiescence-quorum handshake (see that section header) instead of committing to a guessed clock.");
        ClosePopup();
        // Vote phase is over from the host's point of view too; hand off to Phase 2. See
        // RequestNextQuiesceRound's own doc comment for why round 0 resets the vote-phase bookkeeping
        // (_pendingTargetId et al.) right here rather than at the eventual commit.
        RequestNextQuiesceRound(target, round: 0);
    }

    // ── Quiescence-quorum handshake: agree on the clock AFTER everyone is quiet ──
    //
    // Phase 1 (propose/vote/barrier, above) is unchanged. This section is Phases 2 and 3: once the
    // vote passes, instead of the host guessing a commit target up front (the retired
    // hostNextChecksumId design — see UndoCommitMessage's own doc comment for the exact failure 12
    // live two-instance runs measured), every peer independently waits until it is BOTH locally idle
    // AND its own ChecksumTracker.NextId has stopped moving, reports that settled value to the host,
    // and only once every peer's report agrees does the host broadcast the real UndoCommitMessage
    // (Phase 4, in CommitAsync below) with the value they all already reached.
    //
    // DISAGREEMENT IS THE EXPECTED CASE, NOT A FAILURE. The whole reason this handshake exists is
    // that peers can go idle at genuinely different points in the shared checksum sequence — a peer
    // can be locally idle while an action from the other peer is still in flight toward it. A round
    // where reports disagree just means one peer was a little behind; the fix is to let it catch up
    // and ask again, NOT to abort. Treating a first-round disagreement as fatal would fail most real
    // runs, since the measured gap is typically a single action's worth of network transit — see
    // CheckQuiesceRound's own doc comment for the retry mechanics, and MaxQuiesceRounds for the bound
    // that keeps a truly stuck case from retrying forever.
    private static void RequestNextQuiesceRound(uint targetId, int round)
    {
        if (round == 0)
        {
            // The vote has resolved from the HOST's point of view too — release the vote-phase
            // bookkeeping right here, the same transition point OnQuiesceRequestReceived uses on
            // every other peer (see that method's own doc comment). _pendingTargetId is expected to
            // already equal targetId: CheckAllAccepted, the only round-0 caller, only reaches this
            // line after checking exactly that.
            ResetPending();
        }
        _quiesceTargetId = targetId;
        _quiesceRound = round;
        _quiesceReports.Clear();
        Log.Write($"[UndoProtocol] Quiesce round {round} requested for id={targetId}");
        _registeredService?.SendMessage(new UndoQuiesceRequestMessage { targetChecksumId = targetId, round = round });
        // Host's own copy: the broadcast above never loops back to its own sender — same reason
        // ArmBarrierForProposal/RegisterVote/RegisterAck are all called directly for the host's own
        // half of their respective tallies.
        _ = BeginQuiesceRound(targetId, round);
        // Host-only bound on this round completing at all — see its own doc comment for why
        // BeginQuiesceRound's per-peer bound alone isn't enough to guarantee CheckQuiesceRound is
        // ever reached for this round.
        _ = QuiesceRoundTimeoutWatchdog(targetId, round);
    }

    /// <summary>
    /// Phase 2, run on EVERY peer for one round — host included, called directly from
    /// RequestNextQuiesceRound the same way every other peer reaches this from
    /// OnQuiesceRequestReceived; no special-casing, matching CommitAsync's own "a single path is
    /// easier to reason about than a host-only shortcut" reasoning.
    ///
    /// Waits for IsLocallyIdle() (the exact condition CommitAsync's gate 1 already used before this
    /// handshake existed, factored out so Phase 2 and Phase 4 can never drift into checking two
    /// subtly different conditions), then polls ChecksumTracker.NextId (ChecksumTracker.cs:57) once
    /// idle, resetting a stability counter to 0 whenever idle is lost OR NextId changes, until that
    /// counter reaches QuiesceStablePolls consecutive UNCHANGED polls (see that constant's own doc
    /// comment for the count and why). The NextId value at that point is this peer's settled clock
    /// for the round: reported via SendQuiesceReport once, not a value still in motion.
    ///
    /// Bounded by QuiesceWaitFrames (round 0) or the much shorter QuiesceRetryWaitFrames (round ≥ 1
    /// — see that constant's own doc comment for why a retry doesn't need round 0's generous budget).
    /// On timeout, logs which phase gave up and returns WITHOUT reporting — this peer simply never
    /// appears in the host's tally for the round, which QuiesceRoundTimeoutWatchdog (started
    /// alongside this from RequestNextQuiesceRound) is what bounds, not this loop hanging the host.
    /// </summary>
    private static async Task BeginQuiesceRound(uint targetId, int round)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            int boundFrames = round == 0 ? QuiesceWaitFrames : QuiesceRetryWaitFrames;
            int stableCount = 0;
            bool haveLast = false;
            uint lastNextId = 0;
            for (int i = 0; i < boundFrames; i++)
            {
                if (_quiesceTargetId != targetId || _quiesceRound != round)
                    return; // superseded by a newer round, resolved, or cancelled elsewhere
                if (IsLocallyIdle())
                {
                    uint nextId = RunManager.Instance?.ChecksumTracker?.NextId ?? 0;
                    if (haveLast && nextId == lastNextId)
                        stableCount++;
                    else
                    {
                        stableCount = 1; // this poll is the first sample of a fresh stability window
                        lastNextId = nextId;
                        haveLast = true;
                    }
                    if (stableCount >= QuiesceStablePolls)
                    {
                        Log.Write($"[UndoProtocol] Quiesce round {round} settled at NextId={nextId} for id={targetId} after {i} frames (~{i / 60f:0.0}s)");
                        SendQuiesceReport(targetId, round, nextId);
                        return;
                    }
                }
                else
                {
                    stableCount = 0;
                    haveLast = false;
                }
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (_quiesceTargetId != targetId || _quiesceRound != round)
                return; // resolved/superseded while this loop's last await was pending
            Log.Write($"[UndoProtocol] QUIESCE TIMEOUT (phase 2): round {round} for id={targetId} did not settle within {boundFrames} frames (~{boundFrames / 60}s) — not reporting. QuiesceRoundTimeoutWatchdog's own bound, or the outer barrier timeout, will resolve this proposal without this peer's report.");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] BeginQuiesceRound ERROR: {ex.Message}");
        }
    }

    /// <summary>Sends this peer's settled-clock report for a quiesce round (see BeginQuiesceRound
    /// above). Host-only special case mirrors SubmitLocalVote/SendRestoreAck: a host's own broadcast
    /// never loops back to itself, so the host also tallies its own report directly.</summary>
    private static void SendQuiesceReport(uint targetId, int round, uint settledChecksumId)
    {
        var svc = _registeredService;
        if (svc == null) return;
        svc.SendMessage(new UndoQuiesceReportMessage
        {
            targetChecksumId = targetId,
            round = round,
            reporterNetId = MyNetId,
            settledChecksumId = settledChecksumId,
        });
        if (svc.Type == NetGameType.Host)
            RegisterQuiesceReport(MyNetId, targetId, round, settledChecksumId);
    }

    /// <summary>Host-only: records one peer's report into _quiesceReports, guarded on BOTH the
    /// target and the round (not round alone — ResetQuiesce resets the round counter to 0, which a
    /// stale round-0 report for an ALREADY-FINISHED proposal could otherwise alias against a brand
    /// new proposal's own round 0) before re-checking whether the round is now complete.</summary>
    private static void RegisterQuiesceReport(ulong reporter, uint targetId, int round, uint settledChecksumId)
    {
        if (_quiesceTargetId != targetId || round != _quiesceRound) return;
        _quiesceReports[reporter] = settledChecksumId;
        CheckQuiesceRound(targetId, round);
    }

    /// <summary>
    /// Host-only: Phase 3. Once every player has reported for the CURRENT round, compares their
    /// settledChecksumId values.
    ///
    ///   ALL EQUAL → every peer, independently, stopped moving at the exact same point in the shared
    ///   checksum sequence. That point is now provably safe to commit to — broadcasts the final
    ///   UndoCommitMessage with it (Phase 4) and runs CommitAsync locally, exactly like the retired
    ///   design did except the value is AGREED, not guessed.
    ///
    ///   NOT ALL EQUAL, or not everyone has reported by the time QuiesceRoundTimeoutWatchdog's bound
    ///   elapses → see AdvanceQuiesceRoundOrGiveUp's own doc comment; this is the expected, transient
    ///   case this whole handshake exists to retry rather than fail on.
    /// </summary>
    private static void CheckQuiesceRound(uint targetId, int round)
    {
        if (_quiesceTargetId != targetId || round != _quiesceRound) return;
        var all = AllPlayerIds().ToList();
        if (all.Count == 0 || !all.All(id => _quiesceReports.ContainsKey(id)))
            return; // still waiting on stragglers — QuiesceRoundTimeoutWatchdog bounds this
        var reportDump = string.Join(", ", _quiesceReports.Select(kv => $"{kv.Key}={kv.Value}"));
        var distinctValues = _quiesceReports.Values.Distinct().ToList();
        if (distinctValues.Count == 1)
        {
            uint agreed = distinctValues[0];
            Log.Write($"[UndoProtocol] Quiesce round {round} AGREED at NextId={agreed} for id={targetId} — committing. Reports: {reportDump}");
            // Claim this attempt for RegisterCommitFailure's own guard BEFORE firing off CommitAsync
            // — see _commitTargetId's own doc comment for why this can't reuse _quiesceTargetId/
            // _quiesceRound (ResetQuiesce below clears those right after this branch, but a Phase 4
            // failure for THIS attempt can still be reported well after that reset runs).
            _commitTargetId = targetId;
            _commitRound = round;
            _registeredService?.SendMessage(new UndoCommitMessage { targetChecksumId = targetId, agreedChecksumId = agreed, round = round });
            _ = CommitAsync(targetId, agreed, round);
            ResetQuiesce();
            return;
        }
        AdvanceQuiesceRoundOrGiveUp(targetId, round, reportDump);
    }

    /// <summary>
    /// Host-only: shared tail for THREE distinct triggers, all reduced to the same round-budget
    /// decision — from the host's point of view each means "this attempt did not end in every peer
    /// provably safe to restore", so all three retry or give up identically:
    ///   - CheckQuiesceRound's "reports disagreed" branch (a Phase 3 disagreement — peers settled at
    ///     different NextId values).
    ///   - QuiesceRoundTimeoutWatchdog's "never heard from everyone" branch (also Phase 3 — one or
    ///     more peers never reported at all within the round's bound).
    ///   - RegisterCommitFailure (Phase 4 — added in iteration 5, see the top-of-file iteration list):
    ///     Phase 2/3 DID agree, but a peer's own recheck immediately before restoring
    ///     (CommitAsync's moved-since-agreement or never-re-idle branches) found it could not safely
    ///     restore to that agreement after all.
    /// `reportDump` is a free-form detail string for the log line, not necessarily a dump of
    /// _quiesceReports — RegisterCommitFailure passes a Phase-4-specific description instead, since a
    /// Phase 4 failure has no per-peer settledChecksumId tally to dump. See CommitAsync's own doc
    /// comment and this file's top-of-file iteration-5 note for why a Phase 4 failure is deliberately
    /// funnelled into this SAME function rather than a second, parallel retry/give-up mechanism — a
    /// stale agreement (Phase 4) and a disagreement that never formed (Phase 3) are different CAUSES
    /// (kept distinct in the log line that calls this — see CommitAsync's own "moved-since-agreement"
    /// vs this file's "did not converge"/"did not hear from" phase-3 lines) but the SAME remedy: ask
    /// again, bounded by the same round budget, or give up.
    ///
    /// RETRY (round + 1 &lt; MaxQuiesceRounds): this is the expected, transient case — see the
    /// "── Quiescence-quorum handshake ──" section header. Discards this round's reports and asks
    /// again after a short pause (RetryQuiesceRoundAfterInterval → RequestNextQuiesceRound), giving
    /// whichever peer was behind time to catch up before the next attempt.
    ///
    /// GIVE UP (round budget exhausted): only now, after MaxQuiesceRounds attempts have failed to
    /// converge, is this treated as a real problem — cancels through the existing UndoCancelMessage
    /// path (releasing the barrier everywhere, the same as a rejected vote) rather than ever
    /// committing to a value the peers never actually agreed on, and logs every peer's LAST reported
    /// value so a genuine divergence (as opposed to ordinary lag) is at least diagnosable afterward.
    /// AbortedProposalCount (MpFuzz step 3 proof-of-exercise counter — see its own doc comment) is
    /// incremented here, unconditionally, since this IS "the whole proposal aborted on every peer"
    /// regardless of which of the three triggers above caused it.
    /// </summary>
    private static void AdvanceQuiesceRoundOrGiveUp(uint targetId, int round, string reportDump)
    {
        if (round + 1 >= MaxQuiesceRounds)
        {
            Log.Write($"[UndoProtocol] QUIESCE FAILED for id={targetId}: round budget ({MaxQuiesceRounds}) exhausted — cancelling on every peer. {reportDump}");
            AbortedProposalCount++;
            _registeredService?.SendMessage(new UndoCancelMessage { targetChecksumId = targetId, byNetId = MyNetId });
            ClosePopup();
            ReleaseBarrierForTarget(targetId);
            ResetPending();
            ResetQuiesce();
            return;
        }
        Log.Write($"[UndoProtocol] Round {round} did not produce a safe-to-restore agreement for id={targetId} (expected — one or more peers were behind or slow): {reportDump}. Retrying round {round + 1} after a short interval.");
        _ = RetryQuiesceRoundAfterInterval(targetId, round + 1);
    }

    /// <summary>Host-only: the "wait a short interval" half of Phase 3's retry — pure breathing room
    /// so the peer that was behind has a real chance to finish draining before the next round asks
    /// again, rather than hammering it with back-to-back requests. See QuiesceRetryIntervalFrames's
    /// own doc comment for the bound.</summary>
    private static async Task RetryQuiesceRoundAfterInterval(uint targetId, int nextRound)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            for (int i = 0; i < QuiesceRetryIntervalFrames; i++)
            {
                if (!ProposalStillLive(targetId)) return; // resolved/cancelled/combat ended while waiting
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (!ProposalStillLive(targetId)) return;
            RequestNextQuiesceRound(targetId, nextRound);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] RetryQuiesceRoundAfterInterval ERROR: {ex.Message}");
        }
    }

    /// <summary>Host-only liveness check for the wait between AdvanceQuiesceRoundOrGiveUp deciding to
    /// retry and RequestNextQuiesceRound actually firing (RetryQuiesceRoundAfterInterval's own wait
    /// above). Deliberately checks _commitTargetId as well as _quiesceTargetId, not just the latter:
    /// a retry triggered by a Phase 3 disagreement finds _quiesceTargetId still live (Phase 3 never
    /// resets it mid-round), but a retry triggered by a Phase 4 failure (RegisterCommitFailure) runs
    /// AFTER CheckQuiesceRound's AGREED branch already called ResetQuiesce — _quiesceTargetId is null
    /// by then, and _commitTargetId (set in that same AGREED branch, and NOT cleared until the whole
    /// proposal resolves — see its own doc comment) is the only field still correctly tracking that
    /// this proposal is alive. Either field matching is "still live"; both clear (or mismatched) at
    /// once only happens when something else has definitively ended this proposal — a give-up cancel,
    /// a normal resume, or combat ending (ResetQuiesce/ResetBarrier both run together on every one of
    /// those paths).</summary>
    private static bool ProposalStillLive(uint targetId) =>
        _quiesceTargetId == targetId || _commitTargetId == targetId;

    /// <summary>
    /// Host-only: the ABSOLUTE bound on a single round actually completing (CheckQuiesceRound being
    /// reached with every peer's report in hand). BeginQuiesceRound's own per-peer bound already
    /// stops each peer from waiting forever, but a peer that hits ITS bound simply never sends a
    /// report — nothing else would ever re-check CheckQuiesceRound for this round if this watchdog
    /// didn't exist, since RegisterQuiesceReport only re-checks on a NEW report arriving. Bounded at
    /// the same per-round wait BeginQuiesceRound itself uses, plus QuiesceHostRoundMargin of slack
    /// for the last report's own network transit — see that constant's own doc comment.
    ///
    /// On firing, treats "never heard from everyone" exactly like a value disagreement
    /// (AdvanceQuiesceRoundOrGiveUp) — from the host's point of view a straggler that never reported
    /// and a straggler that reported a different value are the same problem: this round didn't reach
    /// a complete, unanimous quorum.
    /// </summary>
    private static async Task QuiesceRoundTimeoutWatchdog(uint targetId, int round)
    {
        try
        {
            var tree = NGame.Instance?.GetTree();
            int boundFrames = (round == 0 ? QuiesceWaitFrames : QuiesceRetryWaitFrames) + QuiesceHostRoundMargin;
            for (int i = 0; i < boundFrames; i++)
            {
                if (_quiesceTargetId != targetId || _quiesceRound != round)
                    return; // round already resolved (agreed, gave up, or superseded) some other way
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (_quiesceTargetId != targetId || _quiesceRound != round)
                return;
            var reportDump = string.Join(", ", _quiesceReports.Select(kv => $"{kv.Key}={kv.Value}"));
            Log.Write($"[UndoProtocol] QUIESCE TIMEOUT (phase 3, host): round {round} for id={targetId} did not hear from every peer within {boundFrames} frames (~{boundFrames / 60}s). Reports so far: {reportDump}");
            AdvanceQuiesceRoundOrGiveUp(targetId, round, reportDump);
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] QuiesceRoundTimeoutWatchdog ERROR: {ex.Message}");
        }
    }

    /// <summary>Clears this peer's quiesce-phase bookkeeping — called whenever the phase ends, any
    /// way it can: a successful commit (CheckQuiesceRound's own agreed branch, or OnCommitReceived on
    /// every other peer), a give-up cancel (AdvanceQuiesceRoundOrGiveUp's exhausted-rounds branch, or
    /// OnCancelReceived on every other peer), a fresh net-service registration
    /// (EnsureHandlersRegistered), or combat ending (ResetOnCombatEnd). Deliberately separate from
    /// ResetPending() — see _quiesceTargetId's own doc comment for why that resets much earlier, at
    /// round 0, while this has to keep living through however many later rounds Phase 3 needs.</summary>
    private static void ResetQuiesce()
    {
        _quiesceTargetId = null;
        _quiesceRound = 0;
        _quiesceReports.Clear();
    }

    /// <summary>
    /// Host-only: routes one peer's Phase 4 failure (see UndoCommitFailedMessage's own doc comment —
    /// reached either via that message, from a client, or directly via RequestCommitRetryOrGiveUp
    /// when the failing peer IS the host) into the SAME retry-or-give-up decision Phase 3's own
    /// disagreement handling already uses (AdvanceQuiesceRoundOrGiveUp) — see that method's own doc
    /// comment for why both causes share one function instead of a second, parallel one.
    ///
    /// Guarded against _commitTargetId/_commitRound, NOT _quiesceTargetId/_quiesceRound — see
    /// _commitTargetId's own doc comment for why. `round` is claimed (advanced to round + 1)
    /// IMMEDIATELY, before AdvanceQuiesceRoundOrGiveUp is even called, not after it returns: two
    /// peers can independently fail the SAME attempt (e.g. the host's own CommitAsync fails at the
    /// same moment a client's does too), and their two failure reports can both reach this function
    /// before either retry has had a chance to change anything else. Claiming the round up front
    /// means the SECOND report to arrive sees _commitRound already advanced past what it expected and
    /// is dropped as stale, rather than triggering a second, duplicate RequestNextQuiesceRound (or, if
    /// the round budget was already exhausted, a second UndoCancelMessage/ReleaseBarrierForTarget).
    /// </summary>
    private static void RegisterCommitFailure(uint targetId, int round)
    {
        if (_commitTargetId != targetId || round != _commitRound) return;
        _commitRound = round + 1;
        AdvanceQuiesceRoundOrGiveUp(targetId, round,
            "Phase 4 commit failure reported (see the reporting peer's own COMMIT ABORTED/COMMIT TIMEOUT log line above for the exact cause).");
    }

    /// <summary>
    /// The peer-side half of a Phase 4 failure — called from CommitAsync's finally block (see that
    /// method's own doc comment) once it has determined this peer will NOT be sending
    /// UndoRestoreAckMessage for this attempt. Mirrors SubmitLocalVote/SendQuiesceReport/
    /// SendRestoreAck's own "host's own broadcast never loops back to itself" special case: if this
    /// peer IS the host, RegisterCommitFailure is called directly instead of round-tripping a message
    /// to itself; otherwise UndoCommitFailedMessage carries the report to the host, which is the only
    /// peer that can decide to retry (RequestNextQuiesceRound broadcasts UndoQuiesceRequestMessage,
    /// documented as host-only) or give up (UndoCancelMessage).
    /// </summary>
    private static void RequestCommitRetryOrGiveUp(uint targetId, int round)
    {
        var svc = _registeredService;
        if (svc == null) return;
        if (svc.Type == NetGameType.Host)
            RegisterCommitFailure(targetId, round);
        else
            svc.SendMessage(new UndoCommitFailedMessage { targetChecksumId = targetId, round = round, reporterNetId = MyNetId });
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
        // ArmBarrierForProposal is called directly in ProposeTarget/OnProposalReceived instead of
        // relying on a self-sent message looping back.
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

    /// <summary>Iteration 5 (see the top-of-file iteration list) proof-of-exercise counter.
    /// Incremented once per peer, every time THAT peer's own CommitAsync finds its NextId moved past
    /// the agreed clock (the "moved-since-agreement" branch — see CommitAsync's own doc comment) and
    /// requests a fresh quiesce round instead of the old silent, un-acked return. Runs unconditionally
    /// on whichever peer detects it, host or client alike, same as CommitCount above — NOT
    /// host-only, unlike AbortedProposalCount below, since detection itself happens locally on
    /// whichever peer's own clock moved, before any message to the host is even sent. A fuzz run that
    /// never advances this counter has not exercised the retry path this fix adds, only the ordinary
    /// success path — see MpFuzz's own summary line for where this is surfaced.</summary>
    internal static int CommitRetryAfterClockMoveCount;

    /// <summary>Iteration 5 proof-of-exercise counter, host-only (see AdvanceQuiesceRoundOrGiveUp's
    /// own doc comment — every caller of the branch that increments this is host-only). Incremented
    /// once per proposal that the round budget (MaxQuiesceRounds) was exhausted for and that was
    /// therefore cancelled on every peer via UndoCancelMessage — whether the exhaustion was driven by
    /// a Phase 3 disagreement (pre-existing) or a Phase 4 commit failure (new in iteration 5). Reads
    /// zero on a non-host peer, same as _quiesceReports/_acked — that peer only ever learns of the
    /// abort via OnCancelReceived, which does not (and should not) duplicate the host's own count.
    /// </summary>
    internal static int AbortedProposalCount;

    // ── Fuzz-only fault injection (MpFuzz, --undosync-mpfuzz-inject-clock-move) ──
    //
    // 24 live two-instance peer-runs (see this file's top-of-file iteration-5 note and f893325's own
    // commit message) never once advanced CommitRetryAfterClockMoveCount above — the retry-vs-give-up
    // fix for CommitAsync's "moved-since-agreement" branch was reviewed, never PROVEN to actually run.
    // The two fields below let a fuzz run force it to run, a bounded number of times, deterministically.

    /// <summary>
    /// Set ONCE, at process start, to the number of times CommitAsync's own Phase-4 recheck (below)
    /// should be made to observe the "moved-since-agreement" condition on THIS peer, then counted down
    /// by CommitAsync itself as it fires — the same "only the currently-active fuzz path's own setup
    /// ever writes this" discipline AutoAcceptForFuzz above already uses, except this field keeps
    /// changing after that initial write (CommitAsync's own decrement below), where AutoAcceptForFuzz
    /// never changes again once MpFuzz.MaybeStart sets it.
    ///
    /// WHAT IT DOES: forces the exact `if` CommitAsync already evaluates (localNextId !=
    /// effectiveAgreedChecksumId, a few lines below its own gate 2) to be true, by perturbing the
    /// comparison's RHS to a value guaranteed to differ from the genuine localNextId this peer just
    /// read from ChecksumTracker.NextId (ChecksumTracker.cs:57) — see the injection site itself for why
    /// this, and not a parallel branch that fakes the counter/log/return, is what "causes" the
    /// condition rather than merely simulating its effects. ChecksumTracker.NextId itself is never
    /// touched — only the local value CommitAsync compares it against — so this cannot desync the real
    /// checksum id sequence the way writing NextId via reflection (ChecksumHook.cs's own
    /// TrackerNextIdProp) would.
    ///
    /// Bounded, not permanent: each firing decrements this counter (see the injection site below), so
    /// a run started with e.g. --undosync-mpfuzz-inject-clock-move-count=1 forces exactly one failure —
    /// enough for the very next attempt (the immediate retry round MaxQuiesceRounds budgets for — see
    /// that constant's own doc comment, UndoProtocol.cs:645) to succeed normally afterward, proving
    /// retry-then-succeed. A count &gt;= MaxQuiesceRounds (3) forces every attempt within one proposal's
    /// round budget to fail, proving retry-until-cancelled instead (see AdvanceQuiesceRoundOrGiveUp's
    /// own doc comment for the give-up branch this drives, and its AbortedProposalCount incrementing).
    ///
    /// Zero (the default) is a no-op: the injection site below is gated on this being &gt; 0, so a
    /// normal game process — which never runs past MpFuzz.MaybeStart's own --undosync-mpfuzz gate —
    /// never touches this field at all, and its compile-time initializer is the only value it ever
    /// observes. See MpFuzz.MaybeStart for the CLI flags that write this.
    /// </summary>
    internal static int ClockMoveInjectionsRemaining;

    /// <summary>Proof-of-exercise counter, incremented once per ACTUAL firing of the injection above —
    /// i.e. once per CommitAsync attempt whose "moved-since-agreement" condition only became true
    /// because of this fuzz injection, not a genuine clock move. Kept separate from
    /// CommitRetryAfterClockMoveCount above (which counts BOTH causes, injected or genuine, since from
    /// the branch's own point of view they are the same condition) so a run's summary can report
    /// "requested N, fired M, branch ran X times total" and a reader can tell whether the injection
    /// actually did what it was asked to, independent of whether a genuine clock move also happened to
    /// occur in the same run. Read by MpFuzz's own step-10 summary.</summary>
    internal static int ClockMoveInjectionsFiredCount;

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

    // ── Commit: recheck idle and the agreed clock once, then restore ──

    /// <summary>
    /// The single definition of "locally idle" shared by CommitAsync's gate 1 below and
    /// BeginQuiesceRound's quiescence wait (see the "── Quiescence-quorum handshake ──" section
    /// header) — factored out so Phase 2's "wait until quiet" and Phase 4's "wait until quiet again,
    /// then restore" can never drift into checking two subtly different conditions. Restore (and
    /// quiesce) only in an idle PLAYER play phase: an empty action queue alone is not enough, since
    /// the enemy turn has idle gaps between monster actions, and observing quiescence there would
    /// leave the async enemy-turn flow running against a state that is about to be discarded.
    /// </summary>
    private static bool IsLocallyIdle()
    {
        var cs = UndoSyncMod.GetCombatState();
        var syncr = RunManager.Instance?.ActionQueueSynchronizer;
        var aq = RunManager.Instance?.ActionQueueSet;
        return cs != null && cs.CurrentSide == CombatSide.Player
            && syncr != null && syncr.CombatState == ActionSynchronizerCombatState.PlayPhase
            && aq != null && aq.IsEmpty
            && NGame.Instance?.Transition?.InTransition != true;
    }

    /// <summary>
    /// Phase 4: runs on EVERY peer that receives (or, on the host, decides) the FINAL commit —
    /// OnCommitReceived and CheckQuiesceRound both call this the same way, host included, with no
    /// special-casing: a single path is easier to reason about than a host-only shortcut.
    ///
    /// By the time this runs, Phase 2/3 (see the "── Quiescence-quorum handshake ──" section header)
    /// already established that EVERY peer's ChecksumTracker.NextId (ChecksumTracker.cs:57) was,
    /// simultaneously, sitting at <paramref name="agreedChecksumId"/> while idle and stable — so
    /// unlike the retired hostNextChecksumId design, there is nothing left to WAIT for on the clock.
    /// What remains, both gated on the same frame:
    ///   1. LOCAL IDLE, once more (IsLocallyIdle) — still necessary even though Phase 2 already
    ///      proved it: the round trip since this peer's own quiesce report (host tally + commit
    ///      broadcast) is a real, if usually tiny, window, and a restore is only ever safe from an
    ///      idle player play phase.
    ///   2. THIS PEER'S OWN NextId STILL EQUALS agreedChecksumId, re-checked ONCE — not a wait loop
    ///      like gate 1, because by construction NextId cannot still be APPROACHING agreedChecksumId
    ///      here (Phase 2 already proved it reached and held there); it can only have STAYED or
    ///      MOVED PAST. If it moved, logged as "moved-since-agreement", deliberately distinct from a
    ///      Phase 3 disagreement: a Phase 3 disagreement means peers never agreed in the first place,
    ///      while this means one peer moved AFTER everyone agreed — a different failure, and this
    ///      project has already been bitten once by conflating two distinct causes under one message
    ///      (the old "AHEAD" vs "TIMEOUT" split this replaces).
    ///
    /// Iteration 5 (see the top-of-file iteration list) changed what happens when gate 2 fails, or
    /// gate 1 never opens within CommitReidleWaitFrames ("never re-idle"): this peer no longer just
    /// returns. A live two-instance run measured that the OLD bare-return left this peer's own
    /// UndoRestoreAckMessage un-sent forever, which left the host's CheckAllAcked tally one ack short
    /// forever, which left EVERY peer's restore barrier (CombatManager.PlayerActionsDisabled) armed
    /// for the rest of combat. Both non-restoring branches, and TryGetSyncPoint's own missing-sync-
    /// point branch, now leave `resolved` false and fall through to the finally block below, which
    /// reports the failure to the host (RequestCommitRetryOrGiveUp) so it can retry through the SAME
    /// round machinery Phase 3's own disagreement already uses (AdvanceQuiesceRoundOrGiveUp), bounded
    /// by the same MaxQuiesceRounds — never a second, parallel retry limit, and never a value this
    /// peer decides to accept as "expected fallout" the way the retired design's comment used to.
    ///
    /// FUZZ-ONLY FAULT INJECTION: gate 2's comparison below can be forced to fail on demand — see
    /// ClockMoveInjectionsRemaining's own doc comment (in the "── Fuzz-only fault injection" section
    /// further down this file) for the full design and the CLI flags that drive it. Zero (the only
    /// value any normal game process ever observes, since it is written only by MpFuzz.MaybeStart
    /// behind that file's own --undosync-mpfuzz gate) makes this a complete no-op: gate 2 below runs
    /// completely unmodified for every peer that never passes --undosync-mpfuzz-inject-clock-move.
    /// </summary>
    private static async Task CommitAsync(uint targetId, uint agreedChecksumId, int round)
    {
        // True once this peer has definitely done ONE of: sent UndoRestoreAckMessage (successful
        // restore), or reached a state some OTHER mechanism already owns the resolution of (combat
        // ended — ResetOnCombatEnd). Everything else — a missing sync point, moved-since-agreement,
        // never-re-idle, or an outright exception — must NOT leave the host's tally silently one peer
        // short, so the finally block below reports it exactly once, from a single place, rather than
        // a release/report call copied into every early-return branch (and therefore easy to miss
        // adding to a future one).
        bool resolved = false;
        try
        {
            if (!ChecksumHook.TryGetSyncPoint(targetId, out var sp))
            {
                Log.Write($"[UndoProtocol] COMMIT FAILED: missing sync point id={targetId} for this peer — requesting a retry/give-up decision instead of leaving the host's ack tally short.");
                return;
            }
            var tree = NGame.Instance?.GetTree();
            for (int i = 0; i < CommitReidleWaitFrames; i++)
            {
                var cm = CombatManager.Instance;
                if (cm == null || !cm.IsInProgress)
                {
                    Log.Write("[UndoProtocol] Commit aborted: combat ended before restore");
                    // ResetOnCombatEnd (via CombatManager.Reset's Harmony postfix) already owns
                    // cleanup for this peer's barrier/pending/quiesce bookkeeping without touching
                    // PlayerActionsDisabled itself (see ResetBarrier's own doc comment) — reporting a
                    // Phase 4 failure for a proposal whose combat no longer exists would be pointless
                    // at best (the host may already be resetting for the SAME reason) and risks racing
                    // a brand new proposal from the NEXT combat at worst, so this is deliberately
                    // treated as already-resolved rather than routed through RequestCommitRetryOrGiveUp.
                    resolved = true;
                    return;
                }
                if (IsLocallyIdle())
                {
                    uint localNextId = RunManager.Instance?.ChecksumTracker?.NextId ?? 0;
                    // Fuzz-only fault injection (--undosync-mpfuzz-inject-clock-move[-count=N] — see
                    // ClockMoveInjectionsRemaining's own doc comment for the full design). Zero (the
                    // default, and the only value any normal game process ever observes) makes this a
                    // no-op: effectiveAgreedChecksumId == agreedChecksumId and the `if` below behaves
                    // exactly as it always has. ChecksumTracker.NextId itself is never touched — only
                    // the local value it gets compared against — so a normal peer's own checksum id
                    // sequence can never be perturbed by this, fuzz-enabled or not.
                    uint effectiveAgreedChecksumId = agreedChecksumId;
                    bool clockMoveInjected = false;
                    if (ClockMoveInjectionsRemaining > 0)
                    {
                        ClockMoveInjectionsRemaining--;
                        ClockMoveInjectionsFiredCount++;
                        clockMoveInjected = true;
                        // Guaranteed to differ from localNextId (unlike, say, agreedChecksumId - 1,
                        // which would coincidentally EQUAL localNextId whenever this peer's clock had
                        // genuinely already moved by exactly one, silently swallowing the injection) —
                        // this simulates the same "moved by one tick since agreement" shape the real
                        // bug this fix targets took (top-of-file iteration-5 note: host observed
                        // NextId 13 -> 14 between AGREE and commit), just forced instead of waited for.
                        effectiveAgreedChecksumId = localNextId > 0 ? localNextId - 1 : localNextId + 1;
                        Log.Write($"[UndoProtocol] [FUZZ] injecting a moved-since-agreement clock move for id={targetId}: presenting agreedChecksumId={effectiveAgreedChecksumId} to this peer's own Phase-4 recheck instead of the real {agreedChecksumId} ({ClockMoveInjectionsRemaining} injection(s) left this run). The COMMIT ABORTED line below is tagged FUZZ-INJECTED — do not mistake it for a genuine clock move.");
                    }
                    if (localNextId != effectiveAgreedChecksumId)
                    {
                        // See this method's own doc comment for why this is distinct from a Phase 3
                        // disagreement, and why it now retries instead of accepting the divergence.
                        // Nothing to wait for locally: Phase 2 already proved every peer reached
                        // agreedChecksumId, so if THIS peer's clock has since moved off it, waiting
                        // longer on this peer alone cannot bring it back (NextId cannot be rewound —
                        // ChecksumHook.RestoreTo's own step-3 comment) — a FRESH quiesce round is what
                        // gives every peer a new, current value to agree on instead.
                        CommitRetryAfterClockMoveCount++;
                        Log.Write($"[UndoProtocol] COMMIT ABORTED (phase 4, moved-since-agreement{(clockMoveInjected ? ", FUZZ-INJECTED" : "")}) for id={targetId}: local NextId={localNextId} no longer equals the agreed clock={effectiveAgreedChecksumId}{(clockMoveInjected ? $" (real agreed={agreedChecksumId}, forced by --undosync-mpfuzz-inject-clock-move)" : "")} — requesting a fresh quiesce round rather than restoring alone.");
                        return;
                    }
                    // Part C (step 3) proof — see AssertQueueEmptyOrRecordViolation's own doc
                    // comment; the idle check above already required aq.IsEmpty, so this can only
                    // ever record zero, but it stays as an explicit, independently-logged assertion.
                    AssertQueueEmptyOrRecordViolation("CommitAsync");
                    ChecksumHook.RestoreTo(sp);
                    CommitCount++;
                    LastCommittedChecksumId = sp.ChecksumId;
                    // Tell the host this peer is done — see "── Cross-peer restore barrier ──"
                    // below. Sent even if this peer's own barrier failed to arm (reflection
                    // missing/threw): the ack means "I finished restoring", independent of whether
                    // the barrier itself is enforcing that on this peer.
                    SendRestoreAck(sp.ChecksumId);
                    resolved = true;
                    return;
                }
                if (tree == null) break;
                await NGame.Instance!.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            Log.Write($"[UndoProtocol] COMMIT TIMEOUT (phase 4, never re-idle) for id={targetId}: did not observe an idle play phase again within {CommitReidleWaitFrames} frames (~{CommitReidleWaitFrames / 60}s) of the final commit — requesting a retry/give-up decision. This should be rare: Phase 2 already proved this peer idle moments earlier, and nothing should be able to end that while the barrier is armed.");
        }
        catch (Exception ex)
        {
            Log.Write($"[UndoProtocol] CommitAsync ERROR: {ex}");
        }
        finally
        {
            // See this method's own doc comment and `resolved`'s own comment above. Deliberately a
            // single call site outside every branch, not one copied into each early return: a
            // release/report statement duplicated per-branch is exactly the shape that let the
            // moved-since-agreement and never-re-idle branches silently diverge from each other
            // before this fix (one had a comment claiming the resulting divergence was fine; neither
            // told the host anything). This also fires for the exception path above, which the old
            // code left with no ack AND no cancel — the same barrier-hang defect by a different route.
            if (!resolved)
                RequestCommitRetryOrGiveUp(targetId, round);
        }
    }

    // ── Cross-peer restore barrier ──
    //
    // Arms on PROPOSAL, not commit (ArmBarrierForProposal, called from ProposeTarget for the
    // proposer's own barrier, and from OnProposalReceived for every other peer on receipt) — i.e.
    // before the vote round trip even starts, not after it. Arming on commit receipt was the
    // original design and it was measured insufficient: nothing held any peer through
    // propose→vote→tally, so both peers kept executing actions during that whole window and each
    // one's ChecksumTracker.NextId had already blown past the host's commit-time sample — by a
    // DIFFERENT amount on each peer — before the commit even arrived (see UndoCommitMessage's own
    // doc comment for the shared-clock mechanism this barrier protects). Arming this early is what
    // actually holds everyone from the moment a restore becomes possible, matching the real user
    // expectation too: once a player proposes an undo, everyone's input is held until it resolves,
    // not just once the vote finishes.
    //
    // Every path that RESOLVES a proposal releases the barrier that arming call put up — normal
    // completion is not the only one:
    //   - ACCEPTED by everyone → CheckAllAccepted starts the quiescence-quorum handshake (see that
    //     section header) instead of committing directly; once it AGREES, CheckQuiesceRound ->
    //     CommitAsync restores, then SendRestoreAck -> the host's CheckAllAcked (once every player
    //     has acked) broadcasts UndoResume, which ReleaseBarrierForTarget lowers on every peer
    //     (OnResumeReceived for clients, directly for the host — a self-broadcast never loops back).
    //   - REJECTED by any one vote → RegisterVote's reject branch sends UndoCancelMessage and
    //     releases the host's own barrier directly (self-broadcast doesn't loop back); every other
    //     peer releases via OnCancelReceived once that broadcast arrives.
    //   - the PROPOSER cancels their own "waiting for votes" popup → HandleWaitingPopupResult sends
    //     UndoCancelMessage and releases the proposer's own barrier directly (same self-broadcast
    //     reason); every other peer again releases via OnCancelReceived.
    //   - the proposal simply TIMES OUT (nobody voted in time) → TimeoutWatchdog (one instance per
    //     peer, generation-guarded) releases THIS peer's own barrier unconditionally once its bound
    //     elapses, regardless of host/client role — it does not wait for a network round trip that
    //     may never arrive. (This bound only covers the VOTE phase — see OnQuiesceRequestReceived's
    //     own doc comment for why ResetPending at round 0 stops this watchdog before the quiesce
    //     phase's own, longer bounds take over.)
    //   - the quiescence-quorum handshake EXHAUSTS its retry budget — either because Phase 3 never
    //     reached a unanimous, complete report set (new in iteration 4), or because Phase 2/3 DID
    //     agree but a peer's own Phase 4 recheck then failed and every retry this triggered (new in
    //     iteration 5 — RegisterCommitFailure/RequestCommitRetryOrGiveUp, see their own doc comments)
    //     also failed to reach a fresh, restorable agreement — see AdvanceQuiesceRoundOrGiveUp's own
    //     doc comment for why both causes share one give-up branch → the host sends UndoCancelMessage
    //     exactly like a rejected vote, releasing its own barrier directly; every other peer again
    //     releases via OnCancelReceived.
    //   - a Phase 4 failure on some peer does NOT, by itself, release that peer's own barrier — see
    //     CommitAsync's own doc comment. It only ever resolves through one of the two paths above:
    //     either a RETRY succeeds and that peer goes on to ack normally, or the round budget above is
    //     exhausted and the barrier comes down via the SAME UndoCancelMessage broadcast every peer
    //     gets. A peer releasing itself unilaterally the moment its own Phase 4 recheck fails would
    //     free it to act while every OTHER peer, still waiting on this peer's now-missing ack, stayed
    //     barriered — exactly the split-brain (one peer restored, or is free to act, while another
    //     is not) this whole handshake exists to prevent.
    //   - none of the above ever fires → BarrierTimeoutWatchdog's own bound (BarrierTimeoutFrames,
    //     now sized to cover the vote phase, the quiesce-quorum phase (including its retry budget),
    //     the final commit's own re-idle recheck, AND the ack/resume margin — see that constant's own
    //     doc comment for the arithmetic) is the absolute last resort, releasing this peer's barrier
    //     unconditionally and logging that the session may now be divergent.
    //
    // Singleplayer cannot deadlock on any of this: ProposeTarget's own `svc.Type ==
    // NetGameType.Singleplayer` branch (above) calls ChecksumHook.RestoreTo directly and returns
    // BEFORE ArmBarrierForProposal is EVER reached — there is no barrier, no vote, and no
    // quiescence-quorum handshake for a deadlock to wait on, because none of that code runs at all.
    // This is the WHOLE guarantee: a single early `return` on the one code path every undo request
    // goes through (ProposeTarget), before any async state (barrier, pending vote, quiesce round) is
    // even created.
    //
    // A HOSTED game with only the host connected (NetGameType.Host, zero clients — different from
    // true Singleplayer above) also cannot deadlock: AllPlayerIds() returns just [MyNetId], so
    // CheckAllAccepted, CheckQuiesceRound, and CheckAllAcked all tally with a single local
    // Register*(MyNetId, ...) call — never a network round trip. This no longer resolves within one
    // fully synchronous call chain the way the retired design did (BeginQuiesceRound still awaits at
    // least QuiesceStablePolls process frames even when quiescing "against" nobody but itself, since
    // it runs through the exact same code path as a real multiplayer peer — see the "── Quiescence-
    // quorum handshake ──" section header for why no special-casing), but it is still fully bounded
    // by the same QuiesceWaitFrames/QuiesceHostRoundMargin/CommitReidleWaitFrames constants every
    // other peer is, and needs no cross-machine round trip to complete.

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

    /// <summary>Arms the barrier for a specific undo TARGET — the same checksum id that will later
    /// flow through the vote and into UndoCommitMessage: records the target/generation this peer is
    /// now barriered for, resets the host-only ack tally, forces PlayerActionsDisabled true
    /// (ArmActionBarrier), and starts this peer's own bounded fallback (BarrierTimeoutWatchdog).
    /// Called from ProposeTarget (the proposer arming its own barrier the instant it decides to
    /// propose, before the proposal is even sent) and OnProposalReceived (every other peer arming on
    /// receipt of that proposal) — deliberately NOT from the commit path any more. See the
    /// top-of-section comment for why arming this early, rather than on commit receipt, is what
    /// actually closes the propose→vote→tally divergence a live run measured.</summary>
    private static void ArmBarrierForProposal(uint targetId)
    {
        _barrierTargetId = targetId;
        _barrierGeneration++;
        _acked.Clear();
        ArmActionBarrier();
        _ = BarrierTimeoutWatchdog(_barrierGeneration, targetId);
    }

    /// <summary>Releases the barrier for <paramref name="targetId"/> on THIS peer: restores
    /// PlayerActionsDisabled (ReleaseActionBarrier) and clears the barrier bookkeeping. Ignores a
    /// mismatched target — a stale UndoResume/UndoCancel for an already-resolved barrier, one of
    /// TimeoutWatchdog's own releases racing a resume/cancel that got there first, or one that
    /// arrives after this peer's own BarrierTimeoutWatchdog already gave up — rather than releasing
    /// state that no longer belongs to this call. Called from every path that can end a proposal
    /// (see the "── Cross-peer restore barrier ──" section header for the full list), which is
    /// exactly why this no-op matters: most of those call sites call it unconditionally, without
    /// first checking that this peer's barrier is definitely still the one they think it is.
    ///
    /// Also clears _commitTargetId (host-only; harmless if already null on a client — see that
    /// field's own doc comment) since every call site here is a point where the WHOLE proposal is
    /// concluding, one way or another — the same reason _acked.Clear() already lives here rather than
    /// only in ArmBarrierForProposal.</summary>
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
        _commitTargetId = null;
    }

    /// <summary>
    /// The ABSOLUTE last-resort bounded fallback for the restore barrier — see the "── Cross-peer
    /// restore barrier ──" section header for the full list of release paths this backstops (normal
    /// resume, a rejected vote, a proposer cancel, and the proposal's own TimeoutWatchdog all resolve
    /// far sooner in the common case). Mirrors TimeoutWatchdog's own generation-guarded polling loop
    /// below (same reuse of TimeoutFrames as part of BarrierTimeoutFrames — see that constant's own
    /// doc comment for why it is not used alone), but bounds the WHOLE armed lifetime of the barrier
    /// now — vote phase plus commit phase — since arming moved from commit receipt to proposal time.
    /// Runs on EVERY peer that arms a barrier — the proposer (from ProposeTarget) and every other
    /// peer (from OnProposalReceived) alike, not just the host — because any of the earlier release
    /// paths can in principle fail to fire: this peer's own restore might never reach an idle play
    /// phase (CommitAsync's own "never reached an idle play phase" abort), another peer's might not,
    /// or a resume/cancel broadcast itself might never arrive. Per the task requirement this
    /// implements ("a permanently frozen combat is worse than a reported risk"), whichever peer is
    /// still barriered when the clock runs out releases itself unconditionally and logs loudly that
    /// the session may now be divergent, rather than waiting forever for a message that may never
    /// come. If THIS peer is the host, it additionally broadcasts UndoResume for the same target so
    /// any peer still waiting normally is freed immediately instead of also waiting out its own full
    /// budget — the same "the host's own watchdog also notifies everyone else" shape TimeoutWatchdog
    /// already uses for UndoCancelMessage.
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
    /// Also releases THIS peer's own barrier unconditionally — proposer and every other peer alike,
    /// not just the host. The barrier now arms at proposal time (ProposeTarget/OnProposalReceived),
    /// so a vote that never resolves must free it right here rather than leaving it to the much
    /// longer BarrierTimeoutWatchdog fallback; ReleaseBarrierForTarget's own mismatched-target no-op
    /// keeps this safe even when this peer's barrier already resolved some other way in the meantime
    /// (e.g. a resume/cancel that arrived in the same frame).
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
            ReleaseBarrierForTarget(targetId);
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
        // The proposer's own barrier — armed back in ProposeTarget the instant it decided to
        // propose — never gets an UndoCancelMessage back for itself (the broadcast above doesn't
        // loop back to its own sender), so release it directly here, the same pattern
        // RegisterVote's reject branch already uses for the host's own barrier.
        ReleaseBarrierForTarget(targetId);
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
        _commitTargetId = null; // see ReleaseBarrierForTarget's own comment on the same field
    }

    /// <summary>Combat ended — drop any in-flight proposal, its popup, and any in-flight restore
    /// barrier (see ResetBarrier's own doc comment for why the barrier is cleared, not released).</summary>
    internal static void ResetOnCombatEnd()
    {
        UndoPicker.Close();
        if (_pendingTargetId == null && _quiesceTargetId == null && _barrierTargetId == null) return;
        Log.Write("[UndoProtocol] Combat ended — clearing pending proposal"
            + (_quiesceTargetId != null ? " and in-flight quiesce-quorum handshake" : "")
            + (_barrierTargetId != null ? " and in-flight restore barrier" : ""));
        ClosePopup();
        ResetPending();
        ResetQuiesce();
        ResetBarrier();
    }
}
