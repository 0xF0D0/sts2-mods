using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;

namespace UndoSync;

/// <summary>
/// Process-wide gate which survives the official ReturnToMainMenu/rejoin boundary.
/// A transition overlay only consumes pointer input; it cannot stop an in-flight
/// network request, a direct caller, or an already-created GameAction. This gate
/// therefore sits at the synchronizer and queue boundaries instead.
/// </summary>
internal static class NativeReplayActionGate
{
    private static readonly object Gate = new();
    private static bool _active;
    private static int _generation;
    private static readonly Dictionary<GameAction, int> PermittedReplayActions =
        new(ReferenceComparer<GameAction>.Instance);
    private static readonly Dictionary<GameAction, int> ReplayOwnedActions =
        new(ReferenceComparer<GameAction>.Instance);
    private static readonly Dictionary<uint, int> ReplayOwnedActionIds = new();

    internal static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// Monotonic transition identity for test harness lifecycle ownership. A driver that began
    /// waiting for generation N must never resume into a later N+1 restore.
    /// </summary>
    internal static int Generation
    {
        get
        {
            lock (Gate)
            {
                return _generation;
            }
        }
    }

    internal static void Enter(uint targetChecksumId, int eventCount)
    {
        lock (Gate)
        {
            _generation++;
            _active = true;
            PermittedReplayActions.Clear();
            ReplayOwnedActions.Clear();
            ReplayOwnedActionIds.Clear();
            Log.Write($"[NativeReplayBarrier] ENTER generation={_generation} "
                + $"target={targetChecksumId} events={eventCount}.");

        }
    }

    internal static void Release(string reason)
    {
        lock (Gate)
        {
            if (!_active)
            {
                return;
            }

            Log.Write($"[NativeReplayBarrier] RELEASE generation={_generation} reason={reason}.");
            _active = false;
            PermittedReplayActions.Clear();
            ReplayOwnedActions.Clear();
            ReplayOwnedActionIds.Clear();
        }
    }

    /// <summary>
    /// Allows exactly one replay-owned action through the direct queue boundary. This intentionally
    /// marks an object rather than opening a temporal "replay is allowed" window. Once the queue
    /// assigns its id, the postfix below records that id as persistent provenance for any later
    /// PlayerChoice resume in this generation.
    /// </summary>
    internal static void PermitReplayEnqueue(GameAction action, string source)
    {
        lock (Gate)
        {
            if (!_active)
            {
                throw new InvalidOperationException("Cannot permit a native replay action while the replay barrier is inactive.");
            }

            PermittedReplayActions[action] = _generation;
            Log.Write($"[NativeReplayBarrier] REPLAY_ENQUEUE_PERMITTED generation={_generation} "
                + $"source={source} type={action.GetType().FullName} owner={action.OwnerId} "
                + $"actionId={FormatActionId(action)}.");
        }
    }

    internal static void PermitReplayResume(uint actionId)
    {
        lock (Gate)
        {
            if (!_active)
            {
                throw new InvalidOperationException("Cannot permit a native replay resume while the replay barrier is inactive.");
            }

            ReplayOwnedActionIds[actionId] = _generation;
            Log.Write($"[NativeReplayBarrier] REPLAY_RESUME_PROVENANCE_RECORDED generation={_generation} "
                + $"source=NativeReplayRestorer type=<resume> owner=<unknown> id={actionId}.");
        }
    }

    private static bool ShouldBlockRequest(GameAction action, string source)
    {
        lock (Gate)
        {
            if (!_active || !IsBlockedPlayerAction(action))
            {
                return false;
            }

            LogBlockedExternalAction(action, source, _generation);
            return true;
        }
    }

    private static bool ShouldBlockDirectEnqueue(GameAction action, string source)
    {
        lock (Gate)
        {
            if (!_active)
            {
                return false;
            }

            if (PermittedReplayActions.TryGetValue(action, out int actionGeneration)
                && actionGeneration == _generation)
            {
                PermittedReplayActions.Remove(action);
                ReplayOwnedActions[action] = _generation;
                Log.Write($"[NativeReplayBarrier] REPLAY_ENQUEUE_ACCEPTED generation={_generation} "
                    + $"source={source} type={action.GetType().FullName} owner={action.OwnerId} "
                    + $"actionId={FormatActionId(action)}.");
                return false;
            }

            if (!IsBlockedPlayerAction(action))
            {
                return false;
            }

            LogBlockedExternalAction(action, source, _generation);
            return true;
        }
    }

    private static bool ShouldBlockRequestResume(GameAction action, string source)
    {
        lock (Gate)
        {
            if (!_active)
            {
                return false;
            }

            if (ReplayOwnedActions.TryGetValue(action, out int actionGeneration)
                && actionGeneration == _generation)
            {
                Log.Write($"[NativeReplayBarrier] REPLAY_RESUME_REQUEST_ACCEPTED generation={_generation} "
                    + $"source={source} type={action.GetType().FullName} owner={action.OwnerId} "
                    + $"actionType={action.ActionType} id={FormatActionId(action)}.");
                return false;
            }

            LogBlockedExternalAction(action, source, _generation);
            return true;
        }
    }

    private static bool ShouldBlockDirectResume(string source, uint actionId, out int permittedGeneration)
    {
        lock (Gate)
        {
            permittedGeneration = 0;
            if (!_active)
            {
                return false;
            }

            if (ReplayOwnedActionIds.TryGetValue(actionId, out int actionGeneration)
                && actionGeneration == _generation)
            {
                permittedGeneration = actionGeneration;
                Log.Write($"[NativeReplayBarrier] REPLAY_RESUME_ACCEPTED generation={_generation} "
                    + $"source={source} type=<resume> owner=<unknown> actionType=<resume> "
                    + $"id={actionId}.");
                return false;
            }

            Log.Write($"[NativeReplayBarrier] BLOCKED_EXTERNAL_ACTION generation={_generation} "
                + $"source={source} type=<resume> owner=<unknown> id={actionId}.");
            return true;
        }
    }

    private static void RecordAssignedResumeId(ActionQueueSet queueSet, uint oldId, int permittedGeneration)
    {
        lock (Gate)
        {
            if (!_active || permittedGeneration != _generation)
            {
                return;
            }

            uint nextId = queueSet.NextActionId;
            if (nextId == 0)
            {
                Log.Write($"[NativeReplayBarrier] REPLAY_RESUME_PROVENANCE_FAILED generation={_generation} "
                    + $"oldId={oldId} reason=next-id-underflow.");
                return;
            }

            uint newId = nextId - 1;
            ReplayOwnedActionIds[newId] = _generation;
            Log.Write($"[NativeReplayBarrier] REPLAY_RESUME_PROVENANCE_CHAINED generation={_generation} "
                + $"oldId={oldId} newId={newId}.");
        }
    }

    private static void RecordAssignedActionId(GameAction action)
    {
        lock (Gate)
        {
            if (!_active
                || !ReplayOwnedActions.TryGetValue(action, out int actionGeneration)
                || actionGeneration != _generation
                || !action.Id.HasValue)
            {
                return;
            }

            ReplayOwnedActionIds[action.Id.Value] = _generation;
            Log.Write($"[NativeReplayBarrier] REPLAY_ACTION_ID_RECORDED generation={_generation} "
                + $"source=ActionQueueSet.EnqueueWithoutSynchronizing type={action.GetType().FullName} "
                + $"owner={action.OwnerId} id={action.Id.Value}.");
        }
    }

    private static void LogBlockedExternalAction(GameAction action, string source, int generation) =>
        Log.Write($"[NativeReplayBarrier] BLOCKED_EXTERNAL_ACTION generation={generation} "
            + $"source={source} type={action.GetType().FullName} owner={action.OwnerId} "
            + $"id={FormatActionId(action)}.");

    private static string FormatActionId(GameAction action) => action.Id?.ToString() ?? "<none>";

    private static bool IsBlockedPlayerAction(GameAction action) =>
        ActionQueueSet.IsGameActionPlayerDriven(action)
        && action.ActionType != GameActionType.NonCombat
        && action.ActionType != GameActionType.Any;

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue), new[] { typeof(GameAction) })]
    private static class RequestEnqueuePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameAction action) =>
            !ShouldBlockRequest(action, "ActionQueueSynchronizer.RequestEnqueue");
    }

    [HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.EnqueueWithoutSynchronizing), new[] { typeof(GameAction) })]
    private static class EnqueueWithoutSynchronizingPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameAction gameAction) =>
            !ShouldBlockDirectEnqueue(gameAction, "ActionQueueSet.EnqueueWithoutSynchronizing");

        [HarmonyPostfix]
        private static void Postfix(GameAction gameAction) => RecordAssignedActionId(gameAction);
    }

    [HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice), new[] { typeof(GameAction) })]
    private static class RequestResumePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameAction action) =>
            !ShouldBlockRequestResume(action, "ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice");
    }

    [HarmonyPatch(typeof(ActionQueueSet), nameof(ActionQueueSet.ResumeActionWithoutSynchronizing), new[] { typeof(uint) })]
    private static class ResumeWithoutSynchronizingPatch
    {
        private struct ResumeState
        {
            internal uint OldId;
            internal int PermittedGeneration;
        }

        [HarmonyPrefix]
        private static bool Prefix(uint id, ref ResumeState __state)
        {
            __state.OldId = id;
            return !ShouldBlockDirectResume(
                "ActionQueueSet.ResumeActionWithoutSynchronizing", id, out __state.PermittedGeneration);
        }

        [HarmonyPostfix]
        private static void Postfix(ActionQueueSet __instance, ResumeState __state)
        {
            if (__state.PermittedGeneration != 0)
            {
                RecordAssignedResumeId(__instance, __state.OldId, __state.PermittedGeneration);
            }
        }
    }
}
