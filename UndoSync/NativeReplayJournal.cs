using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace UndoSync;

/// <summary>Native CombatReplay data and scalar bounds used by multiplayer restore.</summary>
internal sealed class NativeReplayRestorePlan
{
    internal NativeReplayRestorePlan(SerializableRun initialRun, CombatReplay replay, int eventCount, uint targetChecksumId)
    {
        InitialRun = initialRun;
        Replay = replay;
        EventCount = eventCount;
        TargetChecksumId = targetChecksumId;
    }

    internal SerializableRun InitialRun { get; }
    internal CombatReplay Replay { get; }
    internal int EventCount { get; }
    internal uint TargetChecksumId { get; }
}

/// <summary>Access boundary for the game's native CombatReplay journal.</summary>
internal static class NativeReplayJournal
{
    private static readonly FieldInfo? ReplayField =
        AccessTools.Field(typeof(CombatReplayWriter), "_replay");

    // The game-owned replay remains the source of truth for the current combat. Checkpoints
    // retain only this native reference plus scalar boundaries supplied by the callers.
    private static CombatReplay? _capturedReplay;

    /// <summary>
    /// Test-only observation for MpFuzz: reports the current native replay's first
    /// PlayerChoice event and total event count without changing the game-owned journal.
    /// </summary>
    internal static bool TryGetCurrentReplayChoiceBounds(
        out int firstPlayerChoiceEventIndex,
        out int currentEventCount)
    {
        firstPlayerChoiceEventIndex = -1;
        currentEventCount = 0;

        if (!TryGetReplay(RunManager.Instance?.CombatReplayWriter, out CombatReplay? replay, out _)
            || replay?.events == null)
        {
            return false;
        }

        currentEventCount = replay.events.Count;
        for (int index = 0; index < currentEventCount; index++)
        {
            if (replay.events[index].eventType == CombatReplayEventType.PlayerChoice)
            {
                firstPlayerChoiceEventIndex = index;
                return true;
            }
        }

        return false;
    }

    internal static bool TryCapturePoint(uint checksumId, string context, out int eventCount, out string error)
    {
        eventCount = 0;
        if (!TryGetReplay(RunManager.Instance?.CombatReplayWriter, out CombatReplay? replay, out error)
            || replay is null)
        {
            return false;
        }

        if (replay.serializableRun == null)
        {
            error = "native replay has no SerializableRun";
            return false;
        }

        if (replay.events == null)
        {
            error = "native replay has no native event list";
            return false;
        }

        _capturedReplay = replay;
        eventCount = replay.events.Count;
        error = string.Empty;
        return true;
    }

    internal static bool TryCreateRestorePlan(
        uint targetChecksumId,
        int eventCount,
        out NativeReplayRestorePlan plan,
        out string error)
    {
        plan = null!;
        if (_capturedReplay == null)
        {
            error = "native replay restore plan has no captured combat replay";
            return false;
        }

        // A live CombatReplayWriter contains the complete combat journal, so it normally has
        // checksum entries after the selected target. Build the target prefix first; applying
        // the strict canonical validator to the full live journal would reject every ordinary
        // local capture as if it were a malformed network payload.
        return TryCreateLocalRestorePlan(_capturedReplay, targetChecksumId, eventCount, out plan, out error);
    }

    private static bool TryCreateLocalRestorePlan(
        CombatReplay replay,
        uint targetChecksumId,
        int eventCount,
        out NativeReplayRestorePlan plan,
        out string error)
    {
        plan = null!;
        if (replay.serializableRun == null)
        {
            error = "native replay has no SerializableRun";
            return false;
        }

        if (replay.events == null)
        {
            error = "native replay has no native event list";
            return false;
        }

        if (replay.checksumData == null)
        {
            error = "native replay has no native checksum list";
            return false;
        }

        if (eventCount < 0 || eventCount > replay.events.Count)
        {
            error = $"native replay event prefix {eventCount} is outside "
                + $"the native event range [0, {replay.events.Count}]";
            return false;
        }

        CombatReplay prefix = CreatePrefix(replay, targetChecksumId, eventCount);
        // The live local journal may legitimately contain checksums after the selected
        // target. Validate only target presence before making the prefix; the resulting
        // prefix is then subject to the strict canonical validation below.
        if (!HasTargetChecksum(replay, targetChecksumId, out error))
        {
            return false;
        }

        // Prove that the target checksum survived truncation and that no later checksum is
        // exposed in the plan handed to canonicalization/restoration.
        if (!ValidateReplay(prefix, targetChecksumId, out error))
        {
            return false;
        }

        plan = new NativeReplayRestorePlan(
            prefix.serializableRun,
            prefix,
            prefix.events.Count,
            targetChecksumId);
        error = string.Empty;
        return true;
    }

    /// <summary>Validates a host-provided canonical native prefix.</summary>
    internal static bool TryCreateRestorePlan(
        CombatReplay? canonicalReplay,
        uint targetChecksumId,
        out NativeReplayRestorePlan plan,
        out string error)
    {
        plan = null!;
        if (canonicalReplay == null)
        {
            error = "canonical native replay is null";
            return false;
        }

        if (!ValidateReplay(canonicalReplay, targetChecksumId, out error))
        {
            return false;
        }

        plan = new NativeReplayRestorePlan(
            canonicalReplay.serializableRun,
            canonicalReplay,
            canonicalReplay.events.Count,
            targetChecksumId);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Creates a native CombatReplay containing the selected native prefix. This is not a
    /// custom game-state snapshot: SerializableRun and replay entries remain native types.
    /// </summary>
    internal static bool TryCreateCanonicalPrefix(
        NativeReplayRestorePlan sourcePlan,
        out CombatReplay canonicalReplay,
        out string error)
    {
        canonicalReplay = null!;
        if (sourcePlan == null)
        {
            error = "canonical native replay requires a restore plan";
            return false;
        }

        CombatReplay source = sourcePlan.Replay;
        if (!ValidateReplay(source, sourcePlan.TargetChecksumId, out error))
        {
            return false;
        }

        if (sourcePlan.EventCount < 0 || sourcePlan.EventCount > source.events.Count)
        {
            error = $"native replay event prefix {sourcePlan.EventCount} is outside "
                + $"the native event range [0, {source.events.Count}]";
            return false;
        }

        canonicalReplay = CreatePrefix(source, sourcePlan.TargetChecksumId, sourcePlan.EventCount);

        if (!ValidateReplay(canonicalReplay, sourcePlan.TargetChecksumId, out error))
        {
            canonicalReplay = null!;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static CombatReplay CreatePrefix(
        CombatReplay source,
        uint targetChecksumId,
        int eventCount)
    {
        return new CombatReplay
        {
            version = source.version,
            gitCommit = source.gitCommit,
            modelIdHash = source.modelIdHash,
            choiceIds = new List<uint>(source.choiceIds),
            rewardIds = new List<int>(source.rewardIds),
            nextActionId = source.nextActionId,
            nextChecksumId = source.nextChecksumId,
            nextHookId = source.nextHookId,
            serializableRun = source.serializableRun,
            events = source.events.GetRange(0, eventCount),
            checksumData = source.checksumData
                .Where(checksum => checksum.checksumData.id <= targetChecksumId)
                .ToList()
        };
    }

    /// <summary>
    /// Installs native journals into the fresh replay created by official multiplayer re-entry.
    /// The fresh writer's native run/header remain authoritative.
    /// </summary>
    internal static bool TryInstallPrefix(NativeReplayRestorePlan plan, out string error)
    {
        if (plan == null)
        {
            error = "native replay prefix installation requires a restore plan";
            return false;
        }

        if (!TryGetReplay(RunManager.Instance?.CombatReplayWriter, out CombatReplay? replay, out error)
            || replay is null)
        {
            return false;
        }

        if (ReferenceEquals(replay, plan.Replay))
        {
            error = "native replay prefix installation requires the fresh post-reentry replay";
            return false;
        }

        if (replay.events == null || replay.checksumData == null)
        {
            error = "fresh native replay has no writable event/checksum lists";
            return false;
        }

        if (!ValidateReplay(plan.Replay, plan.TargetChecksumId, out error))
        {
            return false;
        }

        if (plan.EventCount < 0 || plan.EventCount > plan.Replay.events.Count)
        {
            error = $"native replay event prefix {plan.EventCount} is outside "
                + $"the native event range [0, {plan.Replay.events.Count}]";
            return false;
        }

        replay.events.Clear();
        replay.events.AddRange(plan.Replay.events.GetRange(0, plan.EventCount));
        replay.checksumData.Clear();
        replay.checksumData.AddRange(plan.Replay.checksumData
            .Where(checksum => checksum.checksumData.id <= plan.TargetChecksumId));
        error = string.Empty;
        return true;
    }

    private static bool ValidateReplay(CombatReplay replay, uint targetChecksumId, out string error)
    {
        if (replay.serializableRun == null)
        {
            error = "native replay has no SerializableRun";
            return false;
        }

        if (replay.events == null)
        {
            error = "native replay has no native event list";
            return false;
        }

        if (replay.checksumData == null)
        {
            error = "native replay has no native checksum list";
            return false;
        }

        bool hasTargetChecksum = false;
        foreach (ReplayChecksumData checksum in replay.checksumData)
        {
            uint id = checksum.checksumData.id;
            if (id == targetChecksumId)
            {
                hasTargetChecksum = true;
            }
            else if (id > targetChecksumId)
            {
                error = $"native replay contains checksum id {id} after target {targetChecksumId}";
                return false;
            }
        }

        if (!hasTargetChecksum)
        {
            error = $"native replay does not contain target checksum id {targetChecksumId}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool HasTargetChecksum(CombatReplay replay, uint targetChecksumId, out string error)
    {
        if (replay.checksumData == null)
        {
            error = "native replay has no native checksum list";
            return false;
        }

        if (replay.checksumData.Any(checksum => checksum.checksumData.id == targetChecksumId))
        {
            error = string.Empty;
            return true;
        }

        error = $"native replay does not contain target checksum id {targetChecksumId}";
        return false;
    }

    private static bool TryGetReplay(
        CombatReplayWriter? writer,
        out CombatReplay? replay,
        out string error)
    {
        replay = null;
        if (writer == null)
        {
            error = "native replay requires a CombatReplayWriter";
            return false;
        }

        if (ReplayField == null)
        {
            error = "verified CombatReplayWriter._replay field was not found";
            return false;
        }

        object? value;
        try
        {
            value = ReplayField.GetValue(writer);
        }
        catch (Exception exception)
        {
            error = $"reading native CombatReplayWriter._replay failed: {exception.Message}";
            return false;
        }

        if (value is not CombatReplay nativeReplay)
        {
            error = value == null
                ? "CombatReplayWriter._replay is null"
                : $"CombatReplayWriter._replay has unexpected type {value.GetType().FullName}";
            return false;
        }

        replay = nativeReplay;
        error = string.Empty;
        return true;
    }
}
