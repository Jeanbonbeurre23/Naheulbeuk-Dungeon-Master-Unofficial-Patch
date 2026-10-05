using System;
using System.Collections.Generic;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.SafetyNet
{
    // Problem G2. In game 1.8, MinionUtility.HireBarmansForCounter is called from three places only:
    // when a counter is built (UpdateBuildEventsSystem), when a tavern entrance is built
    // (TavernUtility.TavernAccesibleTreatment), and, in one case, when a save is loaded
    // (SaveCompatibilityCheckerSystem.CheckIoEntitiesAttached). A barman who dies, or a hire refused because
    // the tavern was not accessible at that moment, is not made good until one of those events happens.
    // Guard lockers, by contrast, are refilled at every change of ten-day period (UpdateGuardLockerSystem).
    //
    // At a fixed interval of game time this watchdog reads every tavern counter. A counter that has fewer
    // barmen than its configuration requires on two checks in a row gets a call to the game's own
    // HireBarmansForCounter, which hires nothing unless the tavern is accessible. Counters still waiting for
    // a worker to build or remove them (WorkerTaskComponent) are left alone.
    internal static unsafe class TavernWatchdog
    {
        // Components that the generated wrappers declare as plain structs (PropIdentityComponent,
        // IoAttachedToComponent) are read through those structs. The two others hold a generic struct or a
        // reference, so they are read by offset, and the offsets are checked against the runtime once.
        private const int EntitiesAttachedSize = 16;      // IoEntitiesAttachedComponent { NativeList<int> AttachedEntities }
        private const int EntitiesAttachedConfigSize = 8; // IoEntitiesAttachedConfigComponent { IoEntitiesAttachedConfig m_config }
        private static bool _layoutChecked;

        private static float _next;
        private static IntPtr _system;
        private static bool _disabled;
        private static int _failures;
        private static int _lastCounterCount = -1;

        // Keyed by entity id and generation, so a recycled id never inherits another counter's state.
        private static readonly HashSet<long> Short = new();
        private static readonly HashSet<long> RefusalLogged = new();
        private static readonly Dictionary<long, int> StaleLogged = new();

        internal static void Tick(UpdateBuildEventsSystem system)
        {
            if (_disabled) return;
            float now = Time.time;
            if (now < _next) return;
            _next = now + Settings.TavernWatchdogInterval.Value;

            try
            {
                if (system.Pointer != _system)
                {
                    _system = system.Pointer;
                    Short.Clear();
                    RefusalLogged.Clear();
                    StaleLogged.Clear();
                    _lastCounterCount = -1;
                }
                Check(system.Pointer);
                _failures = 0;
            }
            catch (Exception e)
            {
                _failures++;
                Plugin.Logger.LogWarning($"[Tavern watchdog] check failed ({_failures} in a row): {e.Message}");
                if (_failures >= 3)
                {
                    _disabled = true;
                    Plugin.Logger.LogError($"[Tavern watchdog] disabled for this session after 3 failed checks. Last error: {e}");
                }
            }
        }

        private static void Check(IntPtr sys)
        {
            IntPtr identityPtr = Il2CppRaw.ReadPointer(sys, "m_propIdentityPool");
            var world = new EcsWorld(Il2CppRaw.ReadObject(identityPtr, "_world", "EcsWorld"));
            if (!world.IsAlive()) return;
            var minions = new MinionUtility(Il2CppRaw.ReadObject(sys, "m_minionUtility", "MinionUtility"));
            var tavern = new TavernUtility(Il2CppRaw.ReadObject(sys, "m_tavernUtility", "TavernUtility"));

            if (!_layoutChecked)
            {
                Il2CppRaw.ExpectValueFieldOffset<IoEntitiesAttachedComponent>("AttachedEntities", 0);
                Il2CppRaw.ExpectValueFieldOffset<IoEntitiesAttachedConfigComponent>("m_config", 0);
                _layoutChecked = true;
            }
            var identity = RawPool.From(identityPtr, sizeof(PropIdentityComponent), "PropIdentityComponent");
            var attached = RawPool.Of<IoEntitiesAttachedComponent>(world, EntitiesAttachedSize);
            var config = RawPool.Of<IoEntitiesAttachedConfigComponent>(world, EntitiesAttachedConfigSize);
            var attachedTo = RawPool.Of<IoAttachedToComponent>(world, sizeof(IoAttachedToComponent));
            var workerTask = RawPool.Of<WorkerTaskComponent>(world, -1);
            if (attached.IsNull || config.IsNull) return;

            int worldSize = world.GetWorldSize();
            long end = Math.Min(identity.SparseLength, worldSize);
            int counters = 0, waitingForWorker = 0;
            var seen = new HashSet<long>();

            for (int e = 0; e < end; e++)
            {
                IntPtr id = identity.Item(e);
                if (id == IntPtr.Zero || ((PropIdentityComponent*)id)->Type != PropType.TAVERN_COUNTER) continue;
                if (!world.IsEntityAlive(e, worldSize)) continue;
                counters++;
                long key = ((long)world.GetEntityGen(e) << 32) | (uint)e;
                seen.Add(key);

                if (workerTask.Has(e)) { waitingForWorker++; Short.Remove(key); continue; }

                IntPtr list = ListOf(attached.Item(e));
                IntPtr cfg = config.Item(e);
                if (list == IntPtr.Zero || cfg == IntPtr.Zero || *(IntPtr*)cfg == IntPtr.Zero) continue;
                int count = *(int*)(list + 8);
                int required = new IoEntitiesAttachedConfig(*(IntPtr*)cfg).MaximumNumberOfEntities;

                ReportStaleEntries(world, worldSize, attachedTo, e, key, list, count);

                if (count >= required)
                {
                    Short.Remove(key);
                    RefusalLogged.Remove(key);
                    continue;
                }

                // First sighting of a shortfall: wait one interval, so that the watchdog never acts in the middle
                // of something the game is still doing, such as a counter being moved or rebuilt.
                if (Short.Add(key))
                {
                    Plugin.Logger.LogInfo($"[Tavern watchdog] counter {e} has {count} of {required} barmen; will hire if still short at the next check");
                    continue;
                }

                minions.HireBarmansForCounter(e);
                int after = *(int*)(ListOf(attached.Item(e)) + 8);
                if (after > count)
                {
                    Short.Remove(key);
                    RefusalLogged.Remove(key);
                    Plugin.Logger.LogInfo($"[Tavern watchdog] counter {e}: hired {after - count} barman(s), now {after} of {required}");
                }
                else if (RefusalLogged.Add(key))
                {
                    string why = tavern.IsTavernAccessible()
                        ? "the game hired nobody although the tavern is accessible"
                        : "the tavern is not accessible (no tavern room, no entrance, or tavern not unlocked yet)";
                    Plugin.Logger.LogInfo($"[Tavern watchdog] counter {e} still has {after} of {required} barmen: {why}. Will retry at each check.");
                }
            }

            Short.RemoveWhere(k => !seen.Contains(k));
            RefusalLogged.RemoveWhere(k => !seen.Contains(k));
            if (counters != _lastCounterCount)
            {
                _lastCounterCount = counters;
                Plugin.Logger.LogInfo($"[Tavern watchdog] {counters} tavern counter(s) in the dungeon, {waitingForWorker} waiting for a worker");
            }
        }

        private static IntPtr ListOf(IntPtr attachedItem)
        {
            // NativeList<int> holds a pointer to an UnsafeList<int> { int* Ptr; int m_length; int m_capacity; ... }.
            return attachedItem == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)attachedItem;
        }

        // Diagnostic only. An entry in a counter's list that is not a living entity attached back to that counter
        // blocks hiring, because HireBarmansForCounter counts list entries. Nothing is repaired here until a
        // test session shows that this happens.
        private static void ReportStaleEntries(EcsWorld world, int worldSize, RawPool attachedTo, int counter, long key, IntPtr list, int count)
        {
            IntPtr items = *(IntPtr*)list;
            int stale = 0;
            for (int i = 0; i < count; i++)
            {
                int b = *(int*)(items + i * 4);
                IntPtr link = world.IsEntityAlive(b, worldSize) ? attachedTo.Item(b) : IntPtr.Zero;
                if (link == IntPtr.Zero || world.Resolve(((IoAttachedToComponent*)link)->IoAttachedPackedEntity, worldSize) != counter) stale++;
            }
            StaleLogged.TryGetValue(key, out int logged);
            if (stale == logged) return;
            StaleLogged[key] = stale;
            if (stale > 0)
                Plugin.Logger.LogWarning($"[Tavern watchdog] counter {counter} lists {stale} barman(s) who no longer exist or are attached elsewhere; this blocks hiring");
        }
    }

    [HarmonyPatch(typeof(UpdateBuildEventsSystem), nameof(UpdateBuildEventsSystem.Run))]
    internal static class TavernWatchdogTick
    {
        private static bool Prepare() => Settings.TavernWatchdog.Value;
        private static void Postfix(UpdateBuildEventsSystem __instance) => TavernWatchdog.Tick(__instance);
    }
}
