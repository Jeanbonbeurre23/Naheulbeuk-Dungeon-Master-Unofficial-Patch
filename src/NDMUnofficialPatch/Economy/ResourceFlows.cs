using System;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;

namespace NDMUnofficialPatch.Economy
{
    // What was produced and consumed of each workshop resource and each food type over the last decade of game
    // time, measured from the game's own calls. A decade is cut into 240 buckets; each event is added to the bucket
    // of the game time at which it happened, and a sum over the last 240 buckets is a sliding decade.
    //
    // Workshop resources (ResourceType) live in gauges handled by ResourceUtility:
    // - AddProducedResourceAmount(type, amount) is every production: crafting (CraftResourceActionSystem), burials
    //   (BuryCorpseActionSystem), torture (TortureEntityActionSystem) and raid loot (RaidsUtility). It stocks the
    //   share not set aside for automatic sale, up to the gauge's limit, and puts the rest in a sale buffer.
    // - ChangeRawResourceAmount(type, delta) is every other change of the stock, and returns the change it made
    //   after clamping between zero and the limit. Negative: raids, invocations, spells, teleports, trap enchanting,
    //   market purchases paid in resources, thefts. Positive: market purchases of resources, refunds, stolen goods
    //   given back, quest rewards.
    // - SellResources(type, count) is every sale, from the sale buffer or from the stock panel.
    // Produced counts production and every positive change; consumed counts every negative change and every sale.
    // Changes made inside AddProducedResourceAmount are already counted as production, and the stock removed by
    // DecreaseResourceGaugeLimit (a storage destroyed) is counted when it is sold.
    //
    // Food (FoodType) is added by FoodUtility.AddAmountToFoodResource (cooking, returned stolen food, quest rewards)
    // and removed by FoodUtility.RemoveAmountToFoodResource (meals, thefts).
    //
    // Gold earned is recorded under GoldIncome by the Bilan (Economy/Bilan.cs), with the same buckets.
    internal static unsafe class ResourceFlows
    {
        internal const int WorkshopKinds = 5;   // ResourceType: ARMAMENT, MAGIC, TOOLS, INTEL, CORPSES
        internal const int FoodKinds = 5;       // FoodType: MEAT, SOUP, SWEET, CHEESE, WASTE
        internal const int ResourceKinds = WorkshopKinds + FoodKinds;
        internal const int GoldIncome = ResourceKinds;   // gold earned, for the Bilan (Economy/Bilan.cs)
        internal const int Kinds = GoldIncome + 1;
        private const int Buckets = 240;

        private static readonly double[,] Produced = new double[Kinds, Buckets];
        private static readonly double[,] Consumed = new double[Kinds, Buckets];
        private static readonly long[] BucketOf = new long[Buckets];
        private static ulong _startMs;
        private static ulong _lastMs;
        private static bool _started;

        internal static int ProductionDepth;
        internal static int CapacityDepth;

        static ResourceFlows() => Reset();

        internal static int Workshop(ResourceType t) => (int)t;
        internal static int Food(FoodType t) => WorkshopKinds + (int)t;

        internal static void Reset()
        {
            Array.Clear(Produced, 0, Produced.Length);
            Array.Clear(Consumed, 0, Consumed.Length);
            for (int i = 0; i < Buckets; i++) BucketOf[i] = -1;
            _started = false;
            _startMs = _lastMs = 0;
        }

        private static bool TryBucket(out long bucket, out ulong now)
        {
            bucket = 0;
            if (!GameClock.TryNow(out now)) return false;
            ulong size = GameClock.DecadeMs / Buckets;
            if (size == 0) return false;
            if (_started && now + size < _lastMs) Reset(); // time went back: another save was loaded
            if (!_started) { _started = true; _startMs = now; }
            _lastMs = now;
            bucket = (long)(now / size);
            return true;
        }

        internal static void Add(int kind, bool produced, double amount)
        {
            if (kind < 0 || kind >= Kinds || !(amount > 0)) return;
            try
            {
                if (!TryBucket(out long bucket, out _)) return;
                int slot = (int)(bucket % Buckets);
                if (BucketOf[slot] != bucket)
                {
                    for (int k = 0; k < Kinds; k++) { Produced[k, slot] = 0; Consumed[k, slot] = 0; }
                    BucketOf[slot] = bucket;
                }
                (produced ? Produced : Consumed)[kind, slot] += amount;
            }
            catch (Exception e) { ReportError(e); }
        }

        // Totals over the last decade of game time, and how much of that decade has been observed since the
        // flows were last reset (a new game or a loaded save), in days.
        internal static bool TrySums(double[] produced, double[] consumed, out double observedDays, out int daysPerDecade)
        {
            observedDays = 0;
            daysPerDecade = GameClock.DaysPerDecade;
            Array.Clear(produced, 0, produced.Length);
            Array.Clear(consumed, 0, consumed.Length);
            if (!TryBucket(out long bucket, out ulong now)) return false;
            for (int slot = 0; slot < Buckets; slot++)
            {
                long b = BucketOf[slot];
                if (b < 0 || b > bucket || b <= bucket - Buckets) continue;
                for (int k = 0; k < Kinds; k++)
                {
                    produced[k] += Produced[k, slot];
                    consumed[k] += Consumed[k, slot];
                }
            }
            ulong decade = GameClock.DecadeMs;
            ulong observed = now - _startMs;
            if (observed > decade) observed = decade;
            observedDays = decade == 0 ? 0 : (double)observed / decade * daysPerDecade;
            return true;
        }

        private static bool _errorLogged;
        internal static void ReportError(Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[ResourceBar] measuring resource flows failed, further errors are not logged: {e.Message}");
        }
    }

    // Game time from the calendar (CalendarManagerComponent.Date, the date UpdateCalendarSystem advances and
    // compares with the previous decade's date).
    internal static unsafe class GameClock
    {
        private static IntPtr _world;
        private static RawPool _pool;
        private static int _entity = -1;
        private static ulong _decadeMs;
        private static int _daysPerDecade;

        internal static bool TryNow(out ulong ms)
        {
            ms = 0;
            if (!GameContext.TryWorld(out EcsWorld world, out _)) return false;
            if (world.Pointer != _world)
            {
                _world = world.Pointer;
                _pool = RawPool.Of<CalendarManagerComponent>(world, sizeof(CalendarManagerComponent));
                _entity = -1;
            }
            IntPtr p = _pool.Item(_entity);
            if (p == IntPtr.Zero)
            {
                _entity = _pool.FirstEntity();
                p = _pool.Item(_entity);
                if (p == IntPtr.Zero) return false;
            }
            DungeonDateTime date = ((CalendarManagerComponent*)p)->Date;
            ms = date.ToMilliseconds();
            return true;
        }

        internal static ulong DecadeMs
        {
            get
            {
                if (_decadeMs == 0)
                {
                    try { var d = DungeonDateTime.ONE_DECADE; _decadeMs = d.ToMilliseconds(); }
                    catch { _decadeMs = 0; }
                    if (_decadeMs == 0) _decadeMs = (ulong)DaysPerDecade * 24UL * 3600UL * 1000UL;
                }
                return _decadeMs;
            }
        }

        internal static int DaysPerDecade
        {
            get
            {
                if (_daysPerDecade == 0)
                {
                    try { _daysPerDecade = (int)DungeonDateTime.DAYS_PER_DECADE; } catch { }
                    if (_daysPerDecade <= 0) _daysPerDecade = 10;
                }
                return _daysPerDecade;
            }
        }
    }

    [HarmonyPatch(typeof(ResourceUtility), nameof(ResourceUtility.AddProducedResourceAmount))]
    internal static class ProducedResourcePatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Prefix(ResourceType __0, float __1)
        {
            ResourceFlows.ProductionDepth++;
            if (__1 > 0) ResourceFlows.Add(ResourceFlows.Workshop(__0), true, __1);
            else if (__1 < 0) ResourceFlows.Add(ResourceFlows.Workshop(__0), false, -__1);
        }
        private static void Postfix() { if (ResourceFlows.ProductionDepth > 0) ResourceFlows.ProductionDepth--; }
    }

    [HarmonyPatch(typeof(ResourceUtility), nameof(ResourceUtility.DecreaseResourceGaugeLimit))]
    internal static class CapacityDecreasePatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Prefix() => ResourceFlows.CapacityDepth++;
        private static void Postfix() { if (ResourceFlows.CapacityDepth > 0) ResourceFlows.CapacityDepth--; }
    }

    [HarmonyPatch(typeof(ResourceUtility), nameof(ResourceUtility.ChangeRawResourceAmount))]
    internal static class RawResourceChangePatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Postfix(ResourceType __0, float __result)
        {
            if (ResourceFlows.ProductionDepth > 0 || ResourceFlows.CapacityDepth > 0) return;
            if (__result > 0) ResourceFlows.Add(ResourceFlows.Workshop(__0), true, __result);
            else if (__result < 0) ResourceFlows.Add(ResourceFlows.Workshop(__0), false, -__result);
        }
    }

    [HarmonyPatch(typeof(ResourceUtility), nameof(ResourceUtility.SellResources))]
    internal static class ResourceSalePatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Prefix(ResourceType __0, int __1)
        {
            if (__1 > 0) ResourceFlows.Add(ResourceFlows.Workshop(__0), false, __1);
        }
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.AddAmountToFoodResource))]
    internal static class FoodAddedPatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Prefix(FoodType __0, int __1)
        {
            if (__1 > 0) ResourceFlows.Add(ResourceFlows.Food(__0), true, __1);
        }
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.RemoveAmountToFoodResource))]
    internal static class FoodRemovedPatch
    {
        private static bool Prepare() => Settings.ResourceBar.Value;
        private static void Prefix(FoodType __0, int __1)
        {
            if (__1 > 0) ResourceFlows.Add(ResourceFlows.Food(__0), false, __1);
        }
    }
}
