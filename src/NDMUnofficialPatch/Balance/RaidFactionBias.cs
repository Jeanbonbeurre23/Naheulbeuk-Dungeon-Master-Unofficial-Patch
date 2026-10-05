using System;
using System.Collections.Generic;
using System.Linq;
using Faction;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using Raid;

namespace NDMUnofficialPatch.Balance
{
    // New ordinary raids go, most of the time, to a faction against which an unfinished unique raid still waits for raids.
    //
    // Game 1.8, from the method bodies, a save of 3 October 2026 and the configs bundle, read on 3 October 2026.
    //
    // Unique raids are not drawn. UpdateRaidsDisplaySystem.FillDisplayableUniqueRaidEntities lists every unique raid
    // whose first condition list is valid, and Run displays each of them at its own location (the locations tagged
    // ReservedForUniqueRaidTagComponent). A condition entity names its unique raid in ConditionRootComponent.RootEntity.
    // "Frappe préventive" (AS_UniqueRaid_Barbarian_01), which opens the barbarian chain, waits on a
    // RaidOfFactionResultConditionComponent: five raids won against FactionType.BARBARIANS.
    //
    // Ordinary raids are drawn. For each generated raid not yet displayed, Run calls CanDisplayRaid, which calls
    // TrySelectPositionOnMap(raid). That method calls RaidsUtility.FillPossibleRaidLocations(ref m_availableRaidLocations,
    // raid, ignoreUsedLocation: false), which keeps the locations of RaidsManagerComponent.RaidLocationEntities that are not
    // reserved for a unique raid, not used by a displayed raid, within the map's visible radius, and whose
    // AllowedRaidTypesComponent includes the raid's MissionTypeComponent. It then draws one of them uniformly with the
    // world's RandomComponent and writes it to the raid's RaidLocationComponent.RaidLocationEntity.
    //
    // A raid's faction follows its location: RaidsUtility.GetFactionType(raid) reads the location's FactionZoneComponent
    // and returns RaidsConfig.GetFactionTypeFromFactionZone of it. UpdateConditionStatusSystem.
    // GetRaidsOfFactionFinishedTriggerCount counts a finished raid for a faction condition when GetFactionType(raid)
    // equals the condition's FactionType. Moving a raid to another faction's location therefore makes it a raid against
    // that faction for the conditions too.
    //
    // A postfix on TrySelectPositionOnMap, when the game placed the raid, looks for faction conditions that are active
    // and not valid and whose root is a unique raid without FinishedRaidTagComponent. If the game's location already
    // belongs to one of those factions, nothing changes. Otherwise, UniqueRaidFactionChance percent of the time, the raid
    // goes to a location drawn uniformly among those the game had just listed for it (m_availableRaidLocations) whose
    // faction is one of those factions. The game's own filters thus still apply: free, not reserved, in reach, and
    // accepting the raid's mission type. The faction of a location is taken from GetFactionType itself.
    internal static unsafe class RaidFactionBias
    {
        private static readonly System.Random Rng = new();
        private static readonly Dictionary<int, FactionType> FactionOfZone = new();
        private static IntPtr _world;
        private static string _lastPending;
        private static bool _layoutChecked, _errorLogged;

        internal static void Report(Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Raids] error, the game's location stands for this raid (later errors are not logged): {e.Message}");
        }

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            Il2CppRaw.ExpectValueFieldOffset<RaidLocationComponent>("RaidLocationEntity", 0);
            Il2CppRaw.ExpectValueFieldOffset<FactionZoneComponent>("FactionZone", 0);
            Il2CppRaw.ExpectValueFieldOffset<RaidOfFactionResultConditionComponent>("FactionType", 4);
            Il2CppRaw.ExpectValueFieldOffset<ConditionStatusComponent>("IsActive", 0);
            Il2CppRaw.ExpectValueFieldOffset<ConditionStatusComponent>("IsValid", 1);
            Il2CppRaw.ExpectValueFieldOffset<ConditionStatusComponent>("HasBeenValidated", 2);
            Il2CppRaw.ExpectValueFieldOffset<ConditionRootComponent>("RootEntity", 0);
            Il2CppRaw.ExpectValueFieldOffset<ConditionBehaviourComponent>("RequiredValue", 8);
            Il2CppRaw.ExpectValueFieldOffset<ConditionBehaviourComponent>("CurrentValue", 16);
            _layoutChecked = true;
        }

        // Factions against which a unique raid not yet finished waits for raids, each with the unique raids concerned
        // and their progress, for the log.
        private static Dictionary<FactionType, List<string>> PendingFactions(EcsWorld world)
        {
            var result = new Dictionary<FactionType, List<string>>();
            var conditions = RawPool.Of<RaidOfFactionResultConditionComponent>(world, 8);
            var status = RawPool.Of<ConditionStatusComponent>(world, 4);
            var roots = RawPool.Of<ConditionRootComponent>(world, 4);
            var behaviour = RawPool.Of<ConditionBehaviourComponent>(world, 20);
            var unique = RawPool.Of<UniqueRaidComponent>(world, -1);
            var finished = RawPool.Of<FinishedRaidTagComponent>(world, -1);
            foreach (int c in conditions.Entities())
            {
                IntPtr s = status.Item(c);
                if (s == IntPtr.Zero) continue;
                byte* flags = (byte*)s;
                if (flags[0] == 0 || flags[1] != 0 || flags[2] != 0) continue; // inactive, valid, or validated once
                IntPtr r = roots.Item(c);
                if (r == IntPtr.Zero) continue;
                int raid = *(int*)r;
                if (!unique.Has(raid) || finished.Has(raid)) continue;
                var faction = (FactionType)(*(int*)(conditions.Item(c) + 4));
                IntPtr b = behaviour.Item(c);
                string progress = b == IntPtr.Zero ? "" : $" at {*(int*)(b + 16)}/{*(int*)(b + 8)}";
                if (!result.TryGetValue(faction, out var list)) result[faction] = list = new List<string>();
                list.Add($"unique raid {raid}{progress}");
            }
            return result;
        }

        // The faction the game gives a raid placed at this location. RaidsUtility.GetFactionType reads the raid's
        // RaidLocationComponent, so the location is written there for the call and the previous value put back.
        // The answer depends only on the location's faction zone, a fixed property of the configs, and is cached per zone.
        private static FactionType? FactionOf(RaidsUtility utility, RawPool zones, IntPtr raidLocation, int raid, int location)
        {
            IntPtr z = zones.Item(location);
            if (z == IntPtr.Zero) return null;
            int zone = *(int*)z;
            if (FactionOfZone.TryGetValue(zone, out var known)) return known;
            int kept = *(int*)raidLocation;
            try
            {
                *(int*)raidLocation = location;
                known = utility.GetFactionType(raid);
            }
            finally
            {
                *(int*)raidLocation = kept;
            }
            FactionOfZone[zone] = known;
            return known;
        }

        private static string Describe(RawPool zones, int location, FactionType? faction)
        {
            IntPtr z = zones.Item(location);
            string zone = z == IntPtr.Zero ? "no zone" : ((EFactionZone)(*(int*)z)).ToString();
            return $"location {location} ({zone}, {faction?.ToString() ?? "no faction"})";
        }

        internal static void AfterPlacement(UpdateRaidsDisplaySystem system, int raid)
        {
            int chance = Settings.UniqueRaidFactionChance.Value;
            IntPtr sys = system.Pointer;
            var world = new EcsWorld(Il2CppRaw.ReadObject(sys, "m_ecsWorld", "EcsWorld"));
            if (world.Pointer != _world)
            {
                _world = world.Pointer;
                FactionOfZone.Clear();
                _lastPending = null;
            }
            CheckLayout();

            var pending = PendingFactions(world);
            string summary = string.Join("; ", pending.Select(p => $"{p.Key} ({string.Join(", ", p.Value)})"));
            if (summary != _lastPending)
            {
                _lastPending = summary;
                Plugin.Logger.LogInfo(pending.Count == 0
                    ? "[Raids] no unfinished unique raid waits for raids against a faction; the game places ordinary raids alone"
                    : $"[Raids] unfinished unique raids waiting for raids against a faction: {summary}. A new ordinary raid goes to that faction {chance} % of the time");
            }
            if (pending.Count == 0) return;

            var locations = RawPool.From(Il2CppRaw.ReadPointer(sys, "m_raidLocationPool"), 4, "RaidLocationComponent");
            IntPtr raidLocation = locations.Item(raid);
            if (raidLocation == IntPtr.Zero) return;
            var utility = new RaidsUtility(Il2CppRaw.ReadObject(sys, "m_raidsUtility", "RaidsUtility"));
            var zones = RawPool.Of<FactionZoneComponent>(world, 4);

            int drawn = *(int*)raidLocation;
            FactionType? drawnFaction = FactionOf(utility, zones, raidLocation, raid, drawn);
            string from = Describe(zones, drawn, drawnFaction);
            if (drawnFaction is FactionType f && pending.ContainsKey(f))
            {
                Plugin.Logger.LogInfo($"[Raids] raid {raid}: the game placed it at {from}, kept");
                return;
            }
            if (Rng.Next(100) >= chance)
            {
                Plugin.Logger.LogInfo($"[Raids] raid {raid}: left at {from} by the {100 - chance} % draw");
                return;
            }

            var available = system.m_availableRaidLocations;
            var candidates = new List<int>();
            for (int i = 0; i < available.Count; i++)
            {
                int l = available[i];
                if (l != drawn && FactionOf(utility, zones, raidLocation, raid, l) is FactionType lf && pending.ContainsKey(lf))
                    candidates.Add(l);
            }
            if (candidates.Count == 0)
            {
                Plugin.Logger.LogInfo($"[Raids] raid {raid}: left at {from}; none of the {available.Count} free location(s) the game listed for its mission type belongs to {string.Join(" or ", pending.Keys)}");
                return;
            }
            int target = candidates[Rng.Next(candidates.Count)];
            *(int*)raidLocation = target;
            Plugin.Logger.LogInfo($"[Raids] raid {raid}: moved from {from} to {Describe(zones, target, FactionOf(utility, zones, raidLocation, raid, target))}, drawn among {candidates.Count} free location(s) of that faction");
        }
    }

    [HarmonyPatch(typeof(UpdateRaidsDisplaySystem), nameof(UpdateRaidsDisplaySystem.TrySelectPositionOnMap))]
    internal static class RaidFactionBiasPatch
    {
        private static bool Prepare() => Settings.UniqueRaidFactionChance.Value > 0;

        // TrySelectPositionOnMap(int raidEntity) returns true when it wrote a location to the raid's RaidLocationComponent.
        private static void Postfix(UpdateRaidsDisplaySystem __instance, int __0, bool __result)
        {
            if (!__result) return;
            try
            {
                RaidFactionBias.AfterPlacement(__instance, __0);
            }
            catch (Exception e)
            {
                RaidFactionBias.Report(e);
            }
        }
    }
}
