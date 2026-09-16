using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace UndoSync;

/// <summary>
/// Mod-owned audit sidecar for the native replay carried by UndoCommitMessage.
/// The native CombatReplay remains opaque and authoritative; this sidecar makes the
/// restore-consumed header/control values and deterministic payload measurements explicit.
/// </summary>
public struct ReplayHeaderManifest
{
    internal const int CurrentVersion = 1;

    public int manifestVersion;
    public uint targetChecksumId;
    public int eventCount;
    public int checksumCount;
    public int choiceIdCount;
    public int rewardIdCount;
    public string replayVersion;
    public string replayGitCommit;
    public uint replayModelIdHash;
    public uint nextActionId;
    public uint nextChecksumId;
    public uint nextHookId;
    public List<uint>? choiceIds;
    public List<int>? rewardIds;
    public string choiceIdsDigest;
    public string rewardIdsDigest;
    public int serializableRunByteCount;
    public string serializableRunDigest;
    public int eventsByteCount;
    public string eventsDigest;
    public int checksumDataByteCount;
    public string checksumDataDigest;
    public int serializedPayloadByteCount;
    public string serializedPayloadDigest;

    public void Serialize(PacketWriter writer)
    {
        List<uint> choiceIds = this.choiceIds
            ?? throw new InvalidOperationException("ReplayHeaderManifest choiceIds is null");
        List<int> rewardIds = this.rewardIds
            ?? throw new InvalidOperationException("ReplayHeaderManifest rewardIds is null");
        writer.WriteInt(manifestVersion);
        writer.WriteUInt(targetChecksumId);
        writer.WriteInt(eventCount);
        writer.WriteInt(checksumCount);
        writer.WriteInt(choiceIdCount);
        writer.WriteInt(rewardIdCount);
        writer.WriteString(replayVersion ?? string.Empty);
        writer.WriteString(replayGitCommit ?? string.Empty);
        writer.WriteUInt(replayModelIdHash);
        writer.WriteUInt(nextActionId);
        writer.WriteUInt(nextChecksumId);
        writer.WriteUInt(nextHookId);
        writer.WriteInt(choiceIds.Count);
        foreach (uint choiceId in choiceIds)
            writer.WriteUInt(choiceId);
        writer.WriteInt(rewardIds.Count);
        foreach (int rewardId in rewardIds)
            writer.WriteInt(rewardId);
        writer.WriteString(choiceIdsDigest ?? string.Empty);
        writer.WriteString(rewardIdsDigest ?? string.Empty);
        writer.WriteInt(serializableRunByteCount);
        writer.WriteString(serializableRunDigest ?? string.Empty);
        writer.WriteInt(eventsByteCount);
        writer.WriteString(eventsDigest ?? string.Empty);
        writer.WriteInt(checksumDataByteCount);
        writer.WriteString(checksumDataDigest ?? string.Empty);
        writer.WriteInt(serializedPayloadByteCount);
        writer.WriteString(serializedPayloadDigest ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        manifestVersion = reader.ReadInt();
        targetChecksumId = reader.ReadUInt();
        eventCount = reader.ReadInt();
        checksumCount = reader.ReadInt();
        choiceIdCount = reader.ReadInt();
        rewardIdCount = reader.ReadInt();
        replayVersion = reader.ReadString();
        replayGitCommit = reader.ReadString();
        replayModelIdHash = reader.ReadUInt();
        nextActionId = reader.ReadUInt();
        nextChecksumId = reader.ReadUInt();
        nextHookId = reader.ReadUInt();
        int manifestChoiceIdCount = reader.ReadInt();
        choiceIds = new List<uint>(manifestChoiceIdCount);
        for (int index = 0; index < manifestChoiceIdCount; index++)
            choiceIds.Add(reader.ReadUInt());
        int manifestRewardIdCount = reader.ReadInt();
        rewardIds = new List<int>(manifestRewardIdCount);
        for (int index = 0; index < manifestRewardIdCount; index++)
            rewardIds.Add(reader.ReadInt());
        choiceIdsDigest = reader.ReadString();
        rewardIdsDigest = reader.ReadString();
        serializableRunByteCount = reader.ReadInt();
        serializableRunDigest = reader.ReadString();
        eventsByteCount = reader.ReadInt();
        eventsDigest = reader.ReadString();
        checksumDataByteCount = reader.ReadInt();
        checksumDataDigest = reader.ReadString();
        serializedPayloadByteCount = reader.ReadInt();
        serializedPayloadDigest = reader.ReadString();
    }
}

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
    /// Validates the received manifest before constructing the restore plan. Keeping this
    /// boundary in the journal makes it impossible for a caller to produce a native restore
    /// plan from a network replay without first checking its mod-owned audit sidecar.
    /// </summary>
    internal static bool TryCreateRestorePlan(
        CombatReplay? canonicalReplay,
        ReplayHeaderManifest manifest,
        uint targetChecksumId,
        out NativeReplayRestorePlan plan,
        out string error)
    {
        plan = null!;
        if (!ApplyManifestHeader(canonicalReplay, manifest, out string applyError))
        {
            error = $"native replay manifest header could not be applied: {applyError}";
            return false;
        }

        if (!TryValidateManifest(canonicalReplay, manifest, targetChecksumId, out string manifestError))
        {
            error = $"native replay manifest is invalid: {manifestError}";
            return false;
        }

        return TryCreateRestorePlan(canonicalReplay, targetChecksumId, out plan, out error);
    }

    /// <summary>
    /// Applies the typed, mod-owned header values before validation. Native replay transport is
    /// opaque and older/native serializers can omit these fields; the host manifest is therefore
    /// authoritative for the values consumed by NativeReplayRestorer.
    /// </summary>
    internal static bool ApplyManifestHeader(
        CombatReplay? replay,
        ReplayHeaderManifest manifest,
        out string error)
    {
        if (replay == null)
        {
            error = "cannot apply a replay header to a null CombatReplay";
            return false;
        }

        if (manifest.choiceIds == null)
        {
            error = "manifest choiceIds is null; expected a typed UInt32 list";
            return false;
        }

        if (manifest.rewardIds == null)
        {
            error = "manifest rewardIds is null; expected a typed Int32 list";
            return false;
        }

        replay.version = manifest.replayVersion;
        replay.gitCommit = manifest.replayGitCommit;
        replay.modelIdHash = manifest.replayModelIdHash;
        replay.nextActionId = manifest.nextActionId;
        replay.nextChecksumId = manifest.nextChecksumId;
        replay.nextHookId = manifest.nextHookId;
        replay.choiceIds = new List<uint>(manifest.choiceIds);
        replay.rewardIds = new List<int>(manifest.rewardIds);
        error = string.Empty;
        return true;
    }

    /// <summary>Builds the mod-owned audit sidecar for a canonical native replay.</summary>
    internal static bool TryCreateManifest(
        CombatReplay? replay,
        uint targetChecksumId,
        int eventCount,
        out ReplayHeaderManifest manifest,
        out string error)
    {
        manifest = default;
        if (replay == null)
        {
            error = "native replay manifest requires a non-null replay";
            return false;
        }

        if (!TryReadReplayShape(replay, targetChecksumId, eventCount, out string shapeError))
        {
            error = shapeError;
            return false;
        }

        if (!TryDigestReplay(replay, out int payloadByteCount, out string payloadDigest, out error))
        {
            return false;
        }

        if (!TryDigestReplayParts(
                replay,
                out int serializableRunByteCount,
                out string serializableRunDigest,
                out int eventsByteCount,
                out string eventsDigest,
                out int checksumDataByteCount,
                out string checksumDataDigest,
                out error))
        {
            return false;
        }

        manifest = new ReplayHeaderManifest
        {
            manifestVersion = ReplayHeaderManifest.CurrentVersion,
            targetChecksumId = targetChecksumId,
            eventCount = eventCount,
            checksumCount = replay.checksumData.Count,
            choiceIdCount = replay.choiceIds.Count,
            rewardIdCount = replay.rewardIds.Count,
            replayVersion = replay.version,
            replayGitCommit = replay.gitCommit,
            replayModelIdHash = replay.modelIdHash,
            nextActionId = replay.nextActionId,
            nextChecksumId = replay.nextChecksumId,
            nextHookId = replay.nextHookId,
            choiceIds = new List<uint>(replay.choiceIds),
            rewardIds = new List<int>(replay.rewardIds),
            choiceIdsDigest = DigestChoiceIds(replay.choiceIds),
            rewardIdsDigest = DigestRewardIds(replay.rewardIds),
            serializableRunByteCount = serializableRunByteCount,
            serializableRunDigest = serializableRunDigest,
            eventsByteCount = eventsByteCount,
            eventsDigest = eventsDigest,
            checksumDataByteCount = checksumDataByteCount,
            checksumDataDigest = checksumDataDigest,
            serializedPayloadByteCount = payloadByteCount,
            serializedPayloadDigest = payloadDigest,
        };
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates the sidecar against the just-received native payload. Every mismatch is logged
    /// by field before the aggregate rejection is returned, so a bad header cannot reach restore.
    /// </summary>
    internal static bool TryValidateManifest(
        CombatReplay? replay,
        ReplayHeaderManifest manifest,
        uint targetChecksumId,
        out string error)
    {
        error = string.Empty;
        var mismatches = new List<string>();
        if (replay == null)
        {
            mismatches.Add("replay=<null>");
        }
        else
        {
            if (!TryReadReplayShape(replay, targetChecksumId, manifest.eventCount, out string shapeError))
                mismatches.Add($"shape={shapeError}");

            Compare(mismatches, "manifest.version", ReplayHeaderManifest.CurrentVersion, manifest.manifestVersion);
            Compare(mismatches, "manifest.targetChecksumId", targetChecksumId, manifest.targetChecksumId);
            Compare(mismatches, "eventCount", manifest.eventCount, replay.events?.Count ?? -1);
            Compare(mismatches, "checksumCount", manifest.checksumCount, replay.checksumData?.Count ?? -1);
            Compare(mismatches, "choiceIdCount", manifest.choiceIdCount, replay.choiceIds?.Count ?? -1);
            Compare(mismatches, "rewardIdCount", manifest.rewardIdCount, replay.rewardIds?.Count ?? -1);
            Compare(mismatches, "replay.version", manifest.replayVersion, replay.version);
            Compare(mismatches, "replay.gitCommit", manifest.replayGitCommit, replay.gitCommit);
            Compare(mismatches, "replay.modelIdHash", manifest.replayModelIdHash, replay.modelIdHash);
            Compare(mismatches, "replay.nextActionId", manifest.nextActionId, replay.nextActionId);
            Compare(mismatches, "replay.nextChecksumId", manifest.nextChecksumId, replay.nextChecksumId);
            Compare(mismatches, "replay.nextHookId", manifest.nextHookId, replay.nextHookId);

            if (manifest.choiceIds == null)
                mismatches.Add("choiceIds: manifest=<null>, expected typed UInt32 list");
            else if (replay.choiceIds == null)
                mismatches.Add("choiceIds: expected typed UInt32 list, actual=<null>");
            else if (!manifest.choiceIds.SequenceEqual(replay.choiceIds))
                mismatches.Add($"choiceIds: expected={FormatValues(manifest.choiceIds)}, actual={FormatValues(replay.choiceIds)}");
            if (manifest.rewardIds == null)
                mismatches.Add("rewardIds: manifest=<null>, expected typed Int32 list");
            else if (replay.rewardIds == null)
                mismatches.Add("rewardIds: expected typed Int32 list, actual=<null>");
            else if (!manifest.rewardIds.SequenceEqual(replay.rewardIds))
                mismatches.Add($"rewardIds: expected={FormatValues(manifest.rewardIds)}, actual={FormatValues(replay.rewardIds)}");

            if (replay.choiceIds != null)
                Compare(mismatches, "choiceIds.digest", manifest.choiceIdsDigest, DigestChoiceIds(replay.choiceIds));
            if (replay.rewardIds != null)
                Compare(mismatches, "rewardIds.digest", manifest.rewardIdsDigest, DigestRewardIds(replay.rewardIds));

            if (TryDigestReplayParts(
                    replay,
                    out int serializableRunByteCount,
                    out string serializableRunDigest,
                    out int eventsByteCount,
                    out string eventsDigest,
                    out int checksumDataByteCount,
                    out string checksumDataDigest,
                    out string partsError))
            {
                Compare(mismatches, "serializableRun.byteCount", manifest.serializableRunByteCount, serializableRunByteCount);
                Compare(mismatches, "serializableRun.digest", manifest.serializableRunDigest, serializableRunDigest);
                Compare(mismatches, "events.byteCount", manifest.eventsByteCount, eventsByteCount);
                Compare(mismatches, "events.digest", manifest.eventsDigest, eventsDigest);
                Compare(mismatches, "checksumData.byteCount", manifest.checksumDataByteCount, checksumDataByteCount);
                Compare(mismatches, "checksumData.digest", manifest.checksumDataDigest, checksumDataDigest);
            }
            else
            {
                mismatches.Add($"native replay payload parts={partsError}");
            }

            if (TryDigestReplay(replay, out int payloadByteCount, out string payloadDigest, out string digestError))
            {
                Compare(mismatches, "serializedPayloadByteCount", manifest.serializedPayloadByteCount, payloadByteCount);
                Compare(mismatches, "serializedPayloadDigest", manifest.serializedPayloadDigest, payloadDigest);
            }
            else
            {
                mismatches.Add($"serializedPayload={digestError}");
            }
        }

        if (mismatches.Count == 0)
            return true;

        foreach (string mismatch in mismatches)
            Log.Write($"[UndoProtocol] Native replay manifest field mismatch: {mismatch}");
        error = string.Join("; ", mismatches);
        return false;
    }

    internal static string DescribeManifest(ReplayHeaderManifest manifest)
    {
        return $"manifestVersion={manifest.manifestVersion} targetChecksumId={manifest.targetChecksumId} "
            + $"eventCount={manifest.eventCount} checksumCount={manifest.checksumCount} "
            + $"choiceIds(count={manifest.choiceIdCount},digest={manifest.choiceIdsDigest ?? string.Empty}) "
            + $"rewardIds(count={manifest.rewardIdCount},digest={manifest.rewardIdsDigest ?? string.Empty}) "
            + $"version={manifest.replayVersion} gitCommit={manifest.replayGitCommit ?? string.Empty} "
            + $"modelIdHash={manifest.replayModelIdHash} "
            + $"nextActionId={manifest.nextActionId} "
            + $"nextChecksumId={manifest.nextChecksumId} "
            + $"nextHookId={manifest.nextHookId} "
            + $"serializableRun(bytes={manifest.serializableRunByteCount},digest={manifest.serializableRunDigest ?? string.Empty}) "
            + $"events(bytes={manifest.eventsByteCount},digest={manifest.eventsDigest ?? string.Empty}) "
            + $"checksumData(bytes={manifest.checksumDataByteCount},digest={manifest.checksumDataDigest ?? string.Empty}) "
            + $"payload(bytes={manifest.serializedPayloadByteCount},digest={manifest.serializedPayloadDigest ?? string.Empty})";
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

    private static bool TryReadReplayShape(
        CombatReplay replay,
        uint targetChecksumId,
        int eventCount,
        out string error)
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

        if (replay.choiceIds == null)
        {
            error = "native replay has no choice-id header list";
            return false;
        }

        if (replay.rewardIds == null)
        {
            error = "native replay has no reward-id header list";
            return false;
        }

        if (eventCount < 0 || eventCount > replay.events.Count)
        {
            error = $"native replay event prefix {eventCount} is outside "
                + $"the native event range [0, {replay.events.Count}]";
            return false;
        }

        if (!replay.checksumData.Any(checksum => checksum.checksumData.id == targetChecksumId))
        {
            error = $"native replay does not contain target checksum id {targetChecksumId}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryDigestReplay(
        CombatReplay replay,
        out int byteCount,
        out string digest,
        out string error)
    {
        try
        {
            var writer = new PacketWriter { WarnOnGrow = false };
            writer.Write(replay);
            byteCount = writer.BytePosition;
            digest = Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, byteCount)));
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            byteCount = 0;
            digest = string.Empty;
            error = $"native replay serialization failed: {exception.Message}";
            return false;
        }
    }

    private static bool TryDigestReplayParts(
        CombatReplay replay,
        out int serializableRunByteCount,
        out string serializableRunDigest,
        out int eventsByteCount,
        out string eventsDigest,
        out int checksumDataByteCount,
        out string checksumDataDigest,
        out string error)
    {
        serializableRunByteCount = 0;
        serializableRunDigest = string.Empty;
        eventsByteCount = 0;
        eventsDigest = string.Empty;
        checksumDataByteCount = 0;
        checksumDataDigest = string.Empty;

        if (replay.serializableRun == null)
        {
            error = "serializableRun is null";
            return false;
        }

        if (replay.events == null)
        {
            error = "events is null";
            return false;
        }

        if (replay.checksumData == null)
        {
            error = "checksumData is null";
            return false;
        }

        try
        {
            var runWriter = new PacketWriter { WarnOnGrow = false };
            runWriter.Write(replay.serializableRun);
            serializableRunByteCount = runWriter.BytePosition;
            serializableRunDigest = Convert.ToHexString(
                SHA256.HashData(runWriter.Buffer.AsSpan(0, serializableRunByteCount)));

            var eventWriter = new PacketWriter { WarnOnGrow = false };
            eventWriter.WriteInt(replay.events.Count);
            foreach (CombatReplayEvent replayEvent in replay.events)
                eventWriter.Write(replayEvent);
            eventsByteCount = eventWriter.BytePosition;
            eventsDigest = Convert.ToHexString(
                SHA256.HashData(eventWriter.Buffer.AsSpan(0, eventsByteCount)));

            var checksumWriter = new PacketWriter { WarnOnGrow = false };
            checksumWriter.WriteInt(replay.checksumData.Count);
            foreach (ReplayChecksumData checksumData in replay.checksumData)
                checksumWriter.Write(checksumData);
            checksumDataByteCount = checksumWriter.BytePosition;
            checksumDataDigest = Convert.ToHexString(
                SHA256.HashData(checksumWriter.Buffer.AsSpan(0, checksumDataByteCount)));
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"native replay part serialization failed: {exception.Message}";
            return false;
        }
    }

    private static string DigestChoiceIds(IReadOnlyList<uint> values)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.WriteInt(values.Count);
        foreach (uint value in values)
            writer.WriteUInt(value);
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static string DigestRewardIds(IReadOnlyList<int> values)
    {
        var writer = new PacketWriter { WarnOnGrow = false };
        writer.WriteInt(values.Count);
        foreach (int value in values)
            writer.WriteInt(value);
        return Convert.ToHexString(SHA256.HashData(writer.Buffer.AsSpan(0, writer.BytePosition)));
    }

    private static void Compare<T>(List<string> mismatches, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            mismatches.Add($"{field}: expected={expected}, actual={actual}");
    }

    private static string FormatValues<T>(IEnumerable<T> values)
    {
        return $"[{string.Join(",", values)}]";
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
