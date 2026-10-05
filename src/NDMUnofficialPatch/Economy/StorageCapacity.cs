using System;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Economy
{
    // Storage furniture holds StorageCapacityFactor times the capacity the game gives it, for workshop resources and
    // for food.
    //
    // Workshop resources (game 1.8, read from the method bodies). Each ResourceType has a gauge, a ResourceGauge at the
    // start of a component on the player entity (GameData.PlayerEntity); ResourceUtility.GetResourceGauge picks the
    // pool by type. ResourceUtility.RecomputeResourceGaugeLimit(type) adds up ResourcesCapacityComponent.Capacity over
    // every storage prop of that type, leaving out a prop whose WorkerTaskComponent is new (isNew) or a destruction
    // order. It writes the sum to TotalLimit and CurrentLimit, then lowers CurrentLimit to the gauge's maximum
    // (Gauge.MaxValue, a FloatWithModifiers) when the sum is above it. ChangeRawResourceAmount keeps the stock between 0
    // and CurrentLimit, and AddProducedResourceAmount sends the production that does not fit to the sale buffer.
    // The game runs the recompute when a storage prop is built (UpdateBuildEventsSystem) and when one is destroyed
    // (DecreaseResourceGaugeLimit, which then sells the stock above the new limit).
    // A postfix on the recompute multiplies the sum by the factor and applies the same maximum. The recompute starts
    // from the props every time, so the result does not depend on when a prop was built or on the save it came from.
    // Gauge.MaxValue is left as it is: the gauge steps (GaugeStep.PercentageMin and PercentageMax, with the states they
    // apply) are percentages of it, and raising it would move every step.
    //
    // Food. FoodResourceDatasComponent.FoodDatas, on the player entity, holds per FoodType the stock (CurrentValue) and
    // the capacity (MaximumValue), which starts at 0. The game keeps the capacity by increments:
    // FoodUtility.AddCapacityToFoodResource when a prop with a FoodStorageConfigComponent finishes building
    // (AddCapacityToAllFood calls it for every food type), RemoveCapacityToFoodResource when such a prop is destroyed
    // (EntityUtility.DestroyProp, for a prop with no worker task, or a finished one that is not a copy made for
    // destruction); a removal lowers the stock to the new capacity. A prefix multiplies the capacity passed to both.
    // A save made without the patch, or with another factor, holds capacities counted at another scale. So when a
    // world appears, the capacity of each food type is set to the sum, over the food storage props that count, of each
    // prop's capacity times the factor, and the increments keep it from then on. A prop counts when it has no worker
    // task, or a finished one, or a destruction order, and is not a copy made for destruction: the props whose
    // capacity the game has added and not yet removed.
    //
    // With a factor of 1 the resources behave as without the patch, and loading a save sets the food capacities back
    // to the game's own sums.
    internal static unsafe class StorageCapacity
    {
        // Field of ResourceUtility holding the gauge pool of each ResourceType, in the order of the enum
        // (ARMAMENT, MAGIC, TOOLS, INTEL, CORPSES), as GetResourceGauge maps them.
        internal static readonly string[] GaugePools =
        {
            "m_armamentResourcePool", "m_magicResourcePool", "m_toolsResourcePool", "m_intelResourcePool", "m_corpsesResourcePool",
        };

        // What the last recompute of each resource type found, for the log written when a world appears.
        private struct Recompute
        {
            public bool Done;
            public float Sum;
            public float Scaled;
            public float Maximum;
            public float Limit;
        }

        private static readonly Recompute[] Last = new Recompute[5];

        private static bool _layoutChecked;
        private static int _maxValue;
        private static int _computed;
        private static int _forced;
        private static int _dirty;
        private static int _configOffset;
        private static float _invalidForce;

        private static IntPtr _appliedWorld;
        private static float _retryAt;
        private static int _changesLogged;
        private static bool _errorLogged;

        internal static float Factor => Math.Clamp(Settings.StorageCapacityFactor.Value, 1f, 100f);

        // The capacity of one prop, multiplied by the factor and rounded as the game stores it (an int).
        internal static int Scale(int capacity)
        {
            double scaled = Math.Round(capacity * (double)Factor);
            return scaled >= int.MaxValue ? int.MaxValue : scaled <= int.MinValue ? int.MinValue : (int)scaled;
        }

        // Offsets read from the runtime and checked against the ones the game's code uses (see the header).
        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            Il2CppRaw.ExpectValueFieldOffset<ResourceGauge>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<ResourceGauge>("CurrentLimit", 0x70);
            Il2CppRaw.ExpectValueFieldOffset<ResourceGauge>("TotalLimit", 0x9c);
            Il2CppRaw.ExpectValueFieldOffset<Gauge>("CurrentValue", 8);
            Il2CppRaw.ExpectValueFieldOffset<Gauge>("MaxValue", 0x10);
            Il2CppRaw.ExpectValueFieldOffset<FloatWithModifiers>("m_baseValue", 0x10);
            Il2CppRaw.ExpectValueFieldOffset<FoodResourceDatasComponent>("FoodDatas", 0);
            Il2CppRaw.ExpectValueFieldOffset<WorkerTaskComponent>("workFinished", 6);
            Il2CppRaw.ExpectValueFieldOffset<WorkerTaskComponent>("copyToDestroy", 28);
            Il2CppRaw.ExpectValueFieldOffset<WorkerTaskComponent>("taskType", 32);
            _maxValue = Il2CppRaw.ValueFieldOffset<Gauge>("MaxValue");
            _computed = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_computedValue");
            _forced = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_forcedValue");
            _dirty = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_isDirty");
            _configOffset = Il2CppRaw.ValueFieldOffset<FoodStorageConfigComponent>("m_config");
            _invalidForce = FloatWithModifiers.INVALID_FORCE_VALUE;
            _layoutChecked = true;
        }

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Storage] {what} failed, further errors are not logged: {e.Message}");
        }

        // ---- Workshop resources ----

        // The entity holding the gauges and the food data. ResourceUtility.GetResourceGauge and the FoodUtility capacity
        // methods read the int at 0x50 of the utility's m_gameData (at 0xa8 in ResourceUtility, 0xa0 in FoodUtility);
        // the runtime's offsets are checked to be those, so the entity is GameData.PlayerEntity.
        private static int PlayerEntityOf(IntPtr utility, int gameDataOffset)
        {
            int field = Il2CppRaw.FieldOffset(utility, "m_gameData");
            if (field != gameDataOffset) throw new InvalidOperationException($"{Il2CppRaw.ClassName(utility)}.m_gameData is at {field}, the game's code reads {gameDataOffset}");
            IntPtr gameData = Il2CppRaw.ReadObject(utility, "m_gameData", "GameData");
            int offset = Il2CppRaw.FieldOffset(gameData, "PlayerEntity");
            if (offset != 0x50) throw new InvalidOperationException($"GameData.PlayerEntity is at {offset}, the game's code reads 80");
            return *(int*)(gameData + offset);
        }

        // The gauge of a resource type, found as GetResourceGauge finds it: the type's pool, the player entity.
        internal static IntPtr GaugeOf(ResourceUtility utility, int type)
        {
            if (type < 0 || type >= GaugePools.Length) return IntPtr.Zero;
            var pool = RawPool.From(Il2CppRaw.ReadPointer(utility.Pointer, GaugePools[type]), -1, GaugePools[type]);
            return pool.Item(PlayerEntityOf(utility.Pointer, 0xa8));
        }

        // The gauge's maximum as FloatWithModifiers.GetComputedValue returns it: the forced value when one is set,
        // otherwise the computed one. The recompute has just called GetComputedValue, which clears the dirty flag;
        // NaN if the flag is still set.
        private static float MaximumOf(IntPtr gauge)
        {
            IntPtr max = gauge + _maxValue;
            float forced = *(float*)(max + _forced);
            if (forced != _invalidForce) return forced;
            if (*(byte*)(max + _dirty) != 0) return float.NaN;
            return *(float*)(max + _computed);
        }

        // Postfix of ResourceUtility.RecomputeResourceGaugeLimit: TotalLimit then holds the game's sum.
        internal static void AfterRecompute(ResourceUtility utility, ResourceType resourceType)
        {
            try
            {
                CheckLayout();
                int type = (int)resourceType;
                IntPtr gauge = GaugeOf(utility, type);
                if (gauge == IntPtr.Zero) return;
                ref float totalLimit = ref *(float*)(gauge + 0x9c);
                ref float currentLimit = ref *(float*)(gauge + 0x70);
                float sum = totalLimit;
                float maximum = MaximumOf(gauge);
                if (float.IsNaN(maximum))
                {
                    ReportError($"reading the maximum of the {resourceType} gauge", new InvalidOperationException("its value is marked for recomputation"));
                    return;
                }
                float scaled = MathF.Round(sum * Factor);
                float limit = Math.Max(0f, Math.Min(scaled, maximum));
                float before = currentLimit;
                totalLimit = scaled;
                currentLimit = limit;
                Last[type] = new Recompute { Done = true, Sum = sum, Scaled = scaled, Maximum = maximum, Limit = limit };
                if (before != limit && _changesLogged < 30)
                {
                    _changesLogged++;
                    Plugin.Logger.LogInfo($"[Storage] {resourceType}: storage gives {sum:0}, x{Factor:0.##} = {scaled:0}, game maximum {maximum:0}, limit {before:0} -> {limit:0}");
                }
            }
            catch (Exception e) { ReportError($"scaling the {resourceType} capacity", e); }
        }

        // ---- World appearing: resources recomputed, food capacities set from the props ----

        // Every frame. Applies the factor once to each new world, as soon as the game's systems for it are captured.
        internal static void Update()
        {
            if (!GameContext.Ready) return;
            IntPtr world = GameContext.WorldPointer;
            if (world == _appliedWorld || Time.unscaledTime < _retryAt) return;
            try
            {
                CheckLayout();
                var resources = GameContext.ResourceUtility;
                var food = GameContext.FoodUtility;
                // The utilities of the previous world stay captured until its systems stop running; wait for the new ones.
                if (Il2CppRaw.ReadPointer(resources.Pointer, "m_world") != world || Il2CppRaw.ReadPointer(food.Pointer, "m_world") != world) return;
                _appliedWorld = world;
                ApplyToResources(resources);
                ApplyToFood(food);
            }
            catch (Exception e)
            {
                _retryAt = Time.unscaledTime + 5f;
                ReportError("applying the factor to a new world", e);
            }
        }

        private static void ApplyToResources(ResourceUtility utility)
        {
            for (int type = 0; type < GaugePools.Length; type++)
            {
                Last[type] = default;
                utility.RecomputeResourceGaugeLimit((ResourceType)type);
                var r = Last[type];
                if (!r.Done) continue;
                string capped = r.Scaled > r.Maximum ? ", capped by the game maximum" : "";
                Plugin.Logger.LogInfo($"[Storage] {(ResourceType)type}: storage gives {r.Sum:0}, x{Factor:0.##} = {r.Scaled:0}, game maximum {r.Maximum:0}, limit {r.Limit:0}{capped}");
            }
        }

        private static void ApplyToFood(FoodUtility utility)
        {
            if (!GameContext.TryWorld(out var world, out int size)) return;
            int player = PlayerEntityOf(utility.Pointer, 0xa0);
            IntPtr datas = RawPool.Of<FoodResourceDatasComponent>(world, 16).Item(player);
            if (datas == IntPtr.Zero)
            {
                Plugin.Logger.LogWarning("[Storage] no food data on the player entity; food capacities left as they are");
                return;
            }
            // FoodDatas is a NativeArray<FoodResourceData>: buffer pointer, then length.
            IntPtr buffer = *(IntPtr*)datas;
            int length = *(int*)(datas + IntPtr.Size);
            if (buffer == IntPtr.Zero || length <= 0 || length > 32)
            {
                Plugin.Logger.LogWarning($"[Storage] food data unreadable (length {length}); food capacities left as they are");
                return;
            }

            var raw = new long[length];
            var scaled = new long[length];
            var storage = RawPool.Of<FoodStorageConfigComponent>(world, IntPtr.Size);
            var tasks = RawPool.Of<WorkerTaskComponent>(world, sizeof(WorkerTaskComponent));
            int counted = 0, notCounted = 0;
            foreach (int e in storage.Entities())
            {
                if (!world.IsEntityAlive(e, size)) continue;
                IntPtr task = tasks.Item(e);
                if (task != IntPtr.Zero)
                {
                    var t = (WorkerTaskComponent*)task;
                    if (t->copyToDestroy || !(t->workFinished || t->taskType == WorkerTaskType.DESTRUCTION))
                    {
                        notCounted++;
                        continue;
                    }
                }
                IntPtr config = *(IntPtr*)(storage.Item(e) + _configOffset);
                if (config == IntPtr.Zero) continue;
                var c = new FoodStorageConfig(config);
                int capacity = c.m_capacity;
                int type = (int)c.m_foodType;
                for (int i = 0; i < length; i++)
                {
                    if (!c.m_isForAllFoodType && i != type) continue;
                    raw[i] += capacity;
                    scaled[i] += Scale(capacity);
                }
                counted++;
            }

            Plugin.Logger.LogInfo($"[Storage] food: {counted} storage prop(s) counted, {notCounted} under construction or copies left out");
            for (int i = 0; i < length; i++)
            {
                var d = (FoodResourceData*)(buffer + i * sizeof(FoodResourceData));
                int stored = d->MaximumValue;
                int target = (int)Math.Clamp(scaled[i], 0, int.MaxValue);
                string origin = stored == target ? "already at this factor"
                    : stored == raw[i] ? "counted at the game's scale"
                    : "counted at another factor, or not matching the props";
                if (stored != target)
                {
                    d->MaximumValue = target;
                    if (d->CurrentValue > target) d->CurrentValue = target;
                }
                Plugin.Logger.LogInfo($"[Storage] food {(FoodType)i}: props give {raw[i]}, x{Factor:0.##} = {target}; the save held {stored} ({origin}); stock {d->CurrentValue}");
            }
        }

        // ---- Food increments ----

        internal static void LogFoodIncrement(string what, FoodType type, int capacity, int scaled)
        {
            if (_changesLogged >= 30) return;
            _changesLogged++;
            Plugin.Logger.LogInfo($"[Storage] food {type}: {what} {capacity} x{Factor:0.##} = {scaled}");
        }
    }

    [HarmonyPatch(typeof(ResourceUtility), nameof(ResourceUtility.RecomputeResourceGaugeLimit))]
    internal static class ResourceCapacityPatch
    {
        private static bool Prepare() => Settings.StorageCapacity.Value;
        private static void Postfix(ResourceUtility __instance, ResourceType __0) => StorageCapacity.AfterRecompute(__instance, __0);
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.AddCapacityToFoodResource))]
    internal static class FoodCapacityAddedPatch
    {
        private static bool Prepare() => Settings.StorageCapacity.Value;
        private static void Prefix(FoodType __0, ref int __1)
        {
            int scaled = StorageCapacity.Scale(__1);
            StorageCapacity.LogFoodIncrement("capacity added", __0, __1, scaled);
            __1 = scaled;
        }
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.RemoveCapacityToFoodResource))]
    internal static class FoodCapacityRemovedPatch
    {
        private static bool Prepare() => Settings.StorageCapacity.Value;
        private static void Prefix(FoodType __0, ref int __1)
        {
            int scaled = StorageCapacity.Scale(__1);
            StorageCapacity.LogFoodIncrement("capacity removed", __0, __1, scaled);
            __1 = scaled;
        }
    }
}
