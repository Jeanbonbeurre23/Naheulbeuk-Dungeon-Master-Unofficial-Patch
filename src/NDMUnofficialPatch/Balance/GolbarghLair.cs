using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // The Golbargh is disturbed only by people in his lair.
    //
    // Game 1.8, from the method bodies, the configs bundle and the French texts, read on 2 October 2026. The Golbargh's
    // patience gauge falls by itself (0.02 a second) and faster when his floor is crowded. Every minion except Zangdar
    // and Reivax, and every adventurer, who enters his floor is added to his EntitiesOnFloorComponent
    // (UpdateChangeFloorEventSystem.ProcessOnEnterFloorEvents), and removed when he leaves it or dies. A
    // ConditionalStatsModifierConfig of type NUMBER_OF_ENTITIES_ON_FLOOR, at 6, 12 and 18 people, applies the states
    // ST_CB_G_TooMuchEntities_A, B and C, which add 0.01, 0.023 and 0.09 a second to his patience loss; their emote reads
    // "Quel est ce bruit !? Zangdar !!". UpdateConditionalStatsModifierSystem.ShouldApplyConditionalStats compares the
    // count from ComputeNumberOfEntitiesOnFloor with each threshold. When someone on his floor dies,
    // DeathUpdateSystem.UnregisterFromGolbarghFloor removes him from the set and applies to the Golbargh the
    // GolbarghPatienceInformationsConfig state EntityDiedOnFloorState (AS_State_Patience_EntityOnFloorDied).
    //
    // A postfix on ComputeNumberOfEntitiesOnFloor replaces the count, for the Golbargh, with the number of those same
    // kinds of people (minions other than Zangdar and Reivax, and adventurers, alive and in the dungeon) who stand on a
    // square of a GOLBARGH_LAIR room. A prefix on UnregisterFromGolbarghFloor notes whether the person who died stood in
    // the lair, and a prefix on StatesUtility.ApplyState skips the state the game then applies to the Golbargh during
    // that call when the death happened outside it. A person's square is his GridCoordinatesComponent on his
    // GridFloorComponent; the lair's squares are the floor squares whose RoomTileComponent names a GOLBARGH_LAIR room.
    internal static unsafe class GolbarghLair
    {
        private static IntPtr _world;
        private static int _golbargh = -1;
        private static float _golbarghAt = -1000f;
        private static readonly HashSet<(int Floor, int X, int Y)> Lair = new();
        private static float _lairAt = -1000f;
        private static int _lairRooms;
        private static int _count;
        private static float _countAt = -1000f;
        private static int _lastFloorCount = -1, _lastLairCount = -1;
        private static bool _errorLogged;

        // The Golbargh's entity while UnregisterFromGolbarghFloor runs for a death outside his lair, otherwise -1.
        internal static int SuppressDeathStateFor = -1;

        internal static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Golbargh] {what} failed, the game's own rule applies: {e.Message}");
        }

        private static void CheckWorld()
        {
            if (GameContext.WorldPointer == _world) return;
            _world = GameContext.WorldPointer;
            _golbargh = -1;
            _golbarghAt = _lairAt = _countAt = -1000f;
            Lair.Clear();
        }

        internal static int Golbargh(EcsWorld world)
        {
            CheckWorld();
            if (Time.unscaledTime - _golbarghAt < 2f) return _golbargh;
            _golbarghAt = Time.unscaledTime;
            _golbargh = RawPool.Of<GolbarghTag>(world, -1).FirstEntity();
            return _golbargh;
        }

        private static void BuildLair(EcsWorld world)
        {
            if (Time.unscaledTime - _lairAt < 10f) return;
            _lairAt = Time.unscaledTime;
            int before = Lair.Count;
            Lair.Clear();
            var rooms = GameContext.ListRooms().Where(r => r.Type == RoomType.GOLBARGH_LAIR).ToDictionary(r => r.Entity, r => r.Floor);
            var roomTiles = RawPool.Of<RoomTileComponent>(world, 8);
            var coords = RawPool.Of<GridCoordinatesComponent>(world, 8);
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            if (rooms.Count > 0)
                foreach (int t in roomTiles.Entities())
                {
                    IntPtr rt = roomTiles.Item(t);
                    if (rt == IntPtr.Zero || !rooms.TryGetValue(*(int*)(rt + 4), out int floor)) continue;
                    IntPtr c = coords.Item(t), f = floors.Item(t);
                    if (f != IntPtr.Zero) floor = *(int*)f; // the square's own floor, the room's when it has none
                    if (c != IntPtr.Zero) Lair.Add((floor, *(int*)c, *(int*)(c + 4)));
                }
            if (Lair.Count != before || rooms.Count != _lairRooms)
                Plugin.Logger.LogInfo($"[Golbargh] lair: {rooms.Count} room(s), {Lair.Count} square(s)");
            _lairRooms = rooms.Count;
        }

        internal static bool InLair(EcsWorld world, int entity)
        {
            BuildLair(world);
            IntPtr c = RawPool.Of<GridCoordinatesComponent>(world, 8).Item(entity);
            IntPtr f = RawPool.Of<GridFloorComponent>(world, 4).Item(entity);
            return c != IntPtr.Zero && f != IntPtr.Zero && Lair.Contains((*(int*)f, *(int*)c, *(int*)(c + 4)));
        }

        // The people the game would count on his floor who stand in his lair, recounted at most twice a second.
        internal static int CountInLair(EcsWorld world, int golbargh)
        {
            if (Time.unscaledTime - _countAt < 0.5f) return _count;
            _countAt = Time.unscaledTime;
            Il2CppRaw.ExpectValueFieldOffset<RoomTileComponent>("RoomEntityID", 4);
            Il2CppRaw.ExpectValueFieldOffset<GridCoordinatesComponent>("Coordinates", 0);
            Il2CppRaw.ExpectValueFieldOffset<GridFloorComponent>("Floor", 0);
            var zangdar = RawPool.Of<ZangdarTag>(world, -1);
            var reivax = RawPool.Of<ReivaxTag>(world, -1);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            int count = 0;
            foreach (int e in RawPool.Of<MinionTag>(world, -1).Entities())
                if (e != golbargh && !zangdar.Has(e) && !reivax.Has(e) && !dead.Has(e) && !outside.Has(e) && InLair(world, e)) count++;
            foreach (int e in RawPool.Of<Adventurer.AdventurerTag>(world, -1).Entities())
                if (e != golbargh && !dead.Has(e) && !outside.Has(e) && InLair(world, e)) count++;
            _count = count;
            return count;
        }

        internal static void Logged(int floorCount, int lairCount)
        {
            if (floorCount == _lastFloorCount && lairCount == _lastLairCount) return;
            _lastFloorCount = floorCount;
            _lastLairCount = lairCount;
            Plugin.Logger.LogInfo($"[Golbargh] people counted by the game on his floor: {floorCount}; in his lair, now counted instead: {lairCount}");
        }
    }

    [HarmonyPatch(typeof(UpdateConditionalStatsModifierSystem), nameof(UpdateConditionalStatsModifierSystem.ComputeNumberOfEntitiesOnFloor))]
    internal static unsafe class GolbarghCrowdPatch
    {
        private static bool Prepare() => Settings.GolbarghLairOnly.Value;

        // ComputeNumberOfEntitiesOnFloor(ref ConditionalStatsModifiers conditionalStatModifiers, int entity).
        private static void Postfix(int __1, ref int __result)
        {
            try
            {
                if (!GameContext.TryWorld(out var world, out _)) return;
                int golbargh = GolbarghLair.Golbargh(world);
                if (golbargh < 0 || __1 != golbargh) return;
                int lair = GolbarghLair.CountInLair(world, golbargh);
                GolbarghLair.Logged(__result, lair);
                __result = lair;
            }
            catch (Exception e) { GolbarghLair.ReportError("counting the people in the Golbargh's lair", e); }
        }
    }

    [HarmonyPatch(typeof(DeathUpdateSystem), nameof(DeathUpdateSystem.UnregisterFromGolbarghFloor))]
    internal static unsafe class GolbarghDeathPatch
    {
        private static bool Prepare() => Settings.GolbarghLairOnly.Value;

        private static void Prefix(int __0)
        {
            GolbarghLair.SuppressDeathStateFor = -1;
            try
            {
                if (!GameContext.TryWorld(out var world, out _)) return;
                int golbargh = GolbarghLair.Golbargh(world);
                if (golbargh < 0 || __0 == golbargh) return;
                IntPtr deadFloor = RawPool.Of<GridFloorComponent>(world, 4).Item(__0);
                IntPtr hisFloor = RawPool.Of<GridFloorComponent>(world, 4).Item(golbargh);
                if (deadFloor == IntPtr.Zero || hisFloor == IntPtr.Zero || *(int*)deadFloor != *(int*)hisFloor) return;
                bool inLair = GolbarghLair.InLair(world, __0);
                GolbarghLair.SuppressDeathStateFor = inLair ? -1 : golbargh;
                Plugin.Logger.LogInfo($"[Golbargh] a death on his floor (entity {__0}), {(inLair ? "in his lair: it upsets him" : "outside his lair: it does not upset him")}");
            }
            catch (Exception e) { GolbarghLair.ReportError("placing a death on the Golbargh's floor", e); }
        }

        private static void Postfix() => GolbarghLair.SuppressDeathStateFor = -1;
    }

    [HarmonyPatch(typeof(StatesUtility), nameof(StatesUtility.ApplyState), new[] { typeof(int), typeof(StateEntityConfig), typeof(int), typeof(bool) })]
    internal static class GolbarghDeathStatePatch
    {
        private static bool Prepare() => Settings.GolbarghLairOnly.Value;

        // ApplyState(int entity, StateEntityConfig stateEntityConfig, int sourceEntity, bool doStateTreatments), the overload
        // at RVA 0xcd1ef0 that UnregisterFromGolbarghFloor calls once, at its end, with the Golbargh as entity.
        private static bool Prefix(int __0, ref int __result)
        {
            if (GolbarghLair.SuppressDeathStateFor < 0 || __0 != GolbarghLair.SuppressDeathStateFor) return true;
            GolbarghLair.SuppressDeathStateFor = -1;
            __result = -1;
            return false;
        }
    }
}
