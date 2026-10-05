using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.SafetyNet
{
    internal enum ConstructionWatchdogMode { Off, Report, Finish }

    // Problem G3, the "room stuck under construction" case. Read from game 1.8: a room leaves construction in
    // RoomsUtility.CheckRoomBuildFinished(room, emitTracker), which returns without doing anything while any child
    // of the room carries a WorkerTaskComponent whose roomID is that room and is not marked for destruction; otherwise
    // it finishes the room. It is called only on a handful of events: a worker finishing a task (WorkerTaskSystem, with
    // emitTracker true), a room validated, cancelled, pasted or added, a prop deleted. If the last pending task
    // disappears through any other path, nothing calls it again and the room stays under construction.
    //
    // At a fixed interval of game time this watchdog lists every room under construction with the worker tasks that
    // name it. A room with no task left for the configured number of minutes, outside build mode, gets a call to
    // CheckRoomBuildFinished(room, true), exactly as a worker finishing its last task would make; the game's own check
    // decides whether the room is finished. A room whose tasks make no progress for that long is reported, and left alone.
    internal static unsafe class ConstructionWatchdog
    {
        private sealed class Watch
        {
            internal string Signature;
            internal float Since;
            internal bool Reported;
            internal bool RefusalLogged;
        }

        private sealed class Tasks
        {
            internal readonly List<int> Entities = new();
            internal float Progress;
        }

        internal static bool InBuildMode;
        private static float _next;
        private static IntPtr _system;
        private static bool _disabled;
        private static int _failures;
        private static readonly Dictionary<long, Watch> Watches = new();

        internal static void Tick(WorkerTaskSystem system)
        {
            if (_disabled) return;
            float now = Time.time;
            if (now < _next) return;
            _next = now + 30f;

            try
            {
                if (system.Pointer != _system)
                {
                    _system = system.Pointer;
                    Watches.Clear();
                }
                Check(system.Pointer, now);
                _failures = 0;
            }
            catch (Exception e)
            {
                _failures++;
                Plugin.Logger.LogWarning($"[Construction watchdog] check failed ({_failures} in a row): {e.Message}");
                if (_failures >= 3)
                {
                    _disabled = true;
                    Plugin.Logger.LogError($"[Construction watchdog] disabled for this session after 3 failed checks. Last error: {e}");
                }
            }
        }

        private static void Check(IntPtr sys, float now)
        {
            var world = new EcsWorld(Il2CppRaw.ReadObject(sys, "m_world", "EcsWorld"));
            if (!world.IsAlive()) return;
            var rooms = new RoomsUtility(Il2CppRaw.ReadObject(sys, "m_roomsUtility", "RoomsUtility"));
            int worldSize = world.GetWorldSize();

            var inConstruction = RawPool.Of<RoomInConstructionComponent>(world, sizeof(RoomInConstructionComponent));
            var workerTask = RawPool.Of<WorkerTaskComponent>(world, sizeof(WorkerTaskComponent));
            if (inConstruction.IsNull) return;

            // Worker tasks grouped by the room they name.
            var byRoom = new Dictionary<int, Tasks>();
            long taskEnd = Math.Min(workerTask.SparseLength, worldSize);
            for (int e = 0; e < taskEnd; e++)
            {
                IntPtr item = workerTask.Item(e);
                if (item == IntPtr.Zero || !world.IsEntityAlive(e, worldSize)) continue;
                var task = (WorkerTaskComponent*)item;
                int room = world.Resolve(task->roomID, worldSize);
                if (room < 0) continue;
                if (!byRoom.TryGetValue(room, out var t)) byRoom[room] = t = new Tasks();
                t.Entities.Add(e);
                t.Progress += task->buildTimeCounter;
            }

            var seen = new HashSet<long>();
            long roomEnd = Math.Min(inConstruction.SparseLength, worldSize);
            for (int r = 0; r < roomEnd; r++)
            {
                if (!inConstruction.Has(r) || !world.IsEntityAlive(r, worldSize)) continue;
                byRoom.TryGetValue(r, out var tasks);
                int pending = tasks?.Entities.Count ?? 0;
                string signature = pending + "/" + (tasks?.Progress ?? 0f).ToString("0.0");

                long key = ((long)world.GetEntityGen(r) << 32) | (uint)r;
                seen.Add(key);
                if (!Watches.TryGetValue(key, out var w))
                {
                    Watches[key] = new Watch { Signature = signature, Since = now };
                    continue;
                }
                if (w.Signature != signature)
                {
                    w.Signature = signature;
                    w.Since = now;
                    w.Reported = false;
                    w.RefusalLogged = false;
                    continue;
                }

                float still = now - w.Since;
                if (still < Settings.ConstructionWatchdogMinutes.Value * 60f) continue;

                if (pending > 0)
                {
                    if (w.Reported) continue;
                    w.Reported = true;
                    Plugin.Logger.LogWarning($"[Construction watchdog] room {r} under construction, {pending} worker task(s) without progress for {still / 60f:0.0} min: {Describe(world, worldSize, workerTask, tasks)}");
                    continue;
                }

                if (Settings.ConstructionWatchdog.Value != ConstructionWatchdogMode.Finish || InBuildMode)
                {
                    if (w.Reported) continue;
                    w.Reported = true;
                    Plugin.Logger.LogWarning($"[Construction watchdog] room {r} has been under construction with no worker task naming it for {still / 60f:0.0} min" +
                                             (InBuildMode ? " (build mode open, nothing done)" : ""));
                    continue;
                }

                rooms.CheckRoomBuildFinished(r, true);
                if (!inConstruction.Has(r))
                {
                    Watches.Remove(key);
                    Plugin.Logger.LogWarning($"[Construction watchdog] room {r}: no worker task named it for {still / 60f:0.0} min; the game's completion check has now finished it");
                }
                else if (!w.RefusalLogged)
                {
                    w.RefusalLogged = true;
                    Plugin.Logger.LogWarning($"[Construction watchdog] room {r}: the game's completion check still sees unfinished work, although no worker task names this room. Will retry at each check.");
                }
            }

            var gone = new List<long>();
            foreach (var k in Watches.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) Watches.Remove(k);
        }

        private static string Describe(EcsWorld world, int worldSize, RawPool workerTask, Tasks tasks)
        {
            var sb = new StringBuilder();
            foreach (int e in tasks.Entities)
            {
                var t = (WorkerTaskComponent*)workerTask.Item(e);
                if (t == null) continue;
                sb.Append(" [task ").Append(e)
                  .Append(' ').Append(t->taskType)
                  .Append(t->isProp ? " prop" : t->IsDoor ? " door" : " wall/tile")
                  .Append(" worker ").Append(world.IsEntityAlive(t->workerAssigned, worldSize) ? t->workerAssigned.ToString() : "none")
                  .Append(t->isReadyToBuild ? " ready" : " not ready")
                  .Append(t->workStarted ? " started" : " not started")
                  .Append(" progress ").Append(t->buildTimeCounter.ToString("0.0"))
                  .Append(']');
            }
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(WorkerTaskSystem), nameof(WorkerTaskSystem.Run))]
    internal static class ConstructionWatchdogTick
    {
        private static bool Prepare() => Settings.ConstructionWatchdog.Value != ConstructionWatchdogMode.Off;
        private static void Postfix(WorkerTaskSystem __instance) => ConstructionWatchdog.Tick(__instance);
    }

    // The watchdog never finishes a room while the player is in build mode, where rooms are being laid out.
    [HarmonyPatch(typeof(BuilderController), nameof(BuilderController.Enter))]
    internal static class ConstructionWatchdogBuildModeEnter
    {
        private static bool Prepare() => Settings.ConstructionWatchdog.Value != ConstructionWatchdogMode.Off;
        private static void Postfix() => ConstructionWatchdog.InBuildMode = true;
    }

    // BuilderController is a state of the game's state machine (Aube.HsmState) and has two Exit overloads;
    // both are patched, since either may be the one the state machine calls.
    [HarmonyPatch(typeof(BuilderController), nameof(BuilderController.Exit), new[] { typeof(Aube.HsmState) })]
    internal static class ConstructionWatchdogBuildModeExit
    {
        private static bool Prepare() => Settings.ConstructionWatchdog.Value != ConstructionWatchdogMode.Off;
        private static void Postfix() => ConstructionWatchdog.InBuildMode = false;
    }

    [HarmonyPatch(typeof(BuilderController), nameof(BuilderController.Exit), new[] { typeof(Aube.HsmState), typeof(bool) })]
    internal static class ConstructionWatchdogBuildModeExitRemoved
    {
        private static bool Prepare() => Settings.ConstructionWatchdog.Value != ConstructionWatchdogMode.Off;
        private static void Postfix() => ConstructionWatchdog.InBuildMode = false;
    }
}
