using System;
using System.Collections.Generic;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // A domestic's cleaning stop also cleans the squares of the same room around it.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 1 October 2026. BT_Clean, the behaviour tree of
    // CLEAN (a domestic's work), finds a spot with FindDirtinessPositionTask, spawns a NeedClean entity there
    // (NeedCleanEntityConfig, one square), walks to it and runs CleanDirtinessTask. The NeedClean entity carries a
    // TilesToCleanComponent: an array of floor squares (TilesToCleanConfig.InitComponent allocates 5) and
    // TotalDirtinessPercentageOnTiles, set to 100 at creation. Each frame CleanDirtinessActionSystem.OnUpdateLoop lowers
    // the dirt of those squares together, a full square (DirtinessConfig.m_maxDirtinessPerTile, 60) in
    // DirtinessConfig.m_maxCleanDurationPerTile (1.5 s) of game time, with nothing of the domestic's grade in the
    // formula; it then sets TotalDirtinessPercentageOnTiles to the squares' mean dirt and, when that is no longer
    // positive, sets it to 0 and ends the action. The action's IoEntityComponent names the NeedClean entity.
    //
    // A prefix on OnUpdateLoop notes the NeedClean entities of the actions under way whose squares are not yet clean. A
    // postfix finds those whose TotalDirtinessPercentageOnTiles fell to 0 during the frame, that is the stops just
    // completed, and for each one clears the dirt of every square of the same room (RoomTileComponent) within
    // Balance.CleaningRadius squares of the stop, with DirtinessUtility.ResetDirtiness, the method the game uses to clear
    // a square's dirt, which calls OnTileDirtinessChange when the dirt changed. A stop interrupted before its squares are
    // clean cleans nothing more.
    [HarmonyPatch(typeof(CleanDirtinessActionSystem), nameof(CleanDirtinessActionSystem.OnUpdateLoop))]
    internal static unsafe class CleaningArea
    {
        private static bool Prepare() => Settings.CleaningRadius.Value > 0;

        private static readonly List<int> Pending = new();
        private static readonly Dictionary<(int Floor, int X, int Y), int> Tiles = new();
        private static IntPtr _tilesWorld;
        private static float _tilesBuiltAt = -1000f;
        private static int _totalOffset = -1;
        private static bool _errorLogged, _missLogged;
        private static int _stops, _squares;
        private static float _nextSummary;

        private static void Layout()
        {
            if (_totalOffset >= 0) return;
            Il2CppRaw.ExpectValueFieldOffset<IoEntityComponent>("IoEntity", 0);
            Il2CppRaw.ExpectValueFieldOffset<GridCoordinatesComponent>("Coordinates", 0);
            Il2CppRaw.ExpectValueFieldOffset<GridFloorComponent>("Floor", 0);
            Il2CppRaw.ExpectValueFieldOffset<RoomTileComponent>("RoomEntityID", 4);
            Il2CppRaw.ExpectValueFieldOffset<TileDirtinessComponent>("CurrentDirtiness", 4);
            _totalOffset = Il2CppRaw.ValueFieldOffset<TilesToCleanComponent>("TotalDirtinessPercentageOnTiles");
        }

        private static void Prefix(EcsFilter __0, EcsWorld __1)
        {
            Pending.Clear();
            try
            {
                if (__0 == null || __1 == null) return;
                int count = __0.GetEntitiesCount();
                if (count <= 0) return;
                Layout();
                var ids = __0.GetRawEntities();
                var io = RawPool.Of<IoEntityComponent>(__1, 8);
                var tiles = RawPool.Of<TilesToCleanComponent>(__1, 24);
                for (int i = 0; i < count; i++)
                {
                    IntPtr a = io.Item(ids[i]);
                    if (a == IntPtr.Zero) continue;
                    int need = *(int*)a;
                    IntPtr t = tiles.Item(need);
                    if (t != IntPtr.Zero && *(float*)(t + _totalOffset) > 0f) Pending.Add(need);
                }
            }
            catch (Exception e) { Report(e); }
        }

        private static void Postfix(CleanDirtinessActionSystem __instance, EcsWorld __1)
        {
            if (Pending.Count > 0)
            {
                try
                {
                    var tiles = RawPool.Of<TilesToCleanComponent>(__1, 24);
                    DirtinessUtility util = null;
                    foreach (int need in Pending)
                    {
                        IntPtr t = tiles.Item(need);
                        if (t == IntPtr.Zero || *(float*)(t + _totalOffset) > 0f) continue;
                        util ??= new DirtinessUtility(Il2CppRaw.ReadObject(__instance.Pointer, "m_dirtinessUtility", "DirtinessUtility"));
                        CleanAround(__1, util, need);
                    }
                }
                catch (Exception e) { Report(e); }
                finally { Pending.Clear(); }
            }
            if (_stops > 0 && Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 300f;
                Plugin.Logger.LogInfo($"[Cleaning] {_stops} cleaning stop(s) finished in the last 5 minutes; {_squares} square(s) around them cleaned as well (radius {Settings.CleaningRadius.Value})");
                _stops = 0;
                _squares = 0;
            }
        }

        private static void CleanAround(EcsWorld world, DirtinessUtility util, int need)
        {
            var coords = RawPool.Of<GridCoordinatesComponent>(world, 8);
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            IntPtr c = coords.Item(need), f = floors.Item(need);
            if (c == IntPtr.Zero || f == IntPtr.Zero) return;
            int x = *(int*)c, y = *(int*)(c + 4), floor = *(int*)f;

            var roomTiles = RawPool.Of<RoomTileComponent>(world, 8);
            var dirt = RawPool.Of<TileDirtinessComponent>(world, 16);
            int center = Tile(world, floor, x, y, coords, rebuild: true);
            IntPtr rc = center < 0 ? IntPtr.Zero : roomTiles.Item(center);
            if (rc == IntPtr.Zero)
            {
                if (!_missLogged) Plugin.Logger.LogInfo($"[Cleaning] no floor square found under a cleaning stop at ({x},{y}) on floor {floor}; the squares around it are left to the game");
                _missLogged = true;
                return;
            }
            int room = *(int*)(rc + 4);
            int r = Settings.CleaningRadius.Value, cleaned = 0;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int tile = (dx == 0 && dy == 0) ? center : Tile(world, floor, x + dx, y + dy, coords, rebuild: false);
                    if (tile < 0) continue;
                    IntPtr rt = roomTiles.Item(tile);
                    if (rt == IntPtr.Zero || *(int*)(rt + 4) != room) continue;
                    IntPtr d = dirt.Item(tile);
                    if (d == IntPtr.Zero || *(float*)(d + 4) <= 0f) continue;
                    util.ResetDirtiness(tile, false);
                    cleaned++;
                }
            _stops++;
            _squares += cleaned;
        }

        // The floor square entity at a position, from an index of every entity with TileDirtinessComponent. The index
        // is rebuilt for a new world, and when the square under a stop is missing or has changed, at most every 5 s.
        private static int Tile(EcsWorld world, int floor, int x, int y, RawPool coords, bool rebuild)
        {
            if (_tilesWorld != GameContext.WorldPointer) Build(world);
            if (Found(floor, x, y, coords, out int tile)) return tile;
            if (!rebuild || Time.unscaledTime - _tilesBuiltAt < 5f) return -1;
            Build(world);
            return Found(floor, x, y, coords, out tile) ? tile : -1;
        }

        private static bool Found(int floor, int x, int y, RawPool coords, out int tile)
        {
            if (!Tiles.TryGetValue((floor, x, y), out tile)) return false;
            IntPtr c = coords.Item(tile);
            return c != IntPtr.Zero && *(int*)c == x && *(int*)(c + 4) == y;
        }

        private static void Build(EcsWorld world)
        {
            bool first = _tilesWorld != GameContext.WorldPointer;
            _tilesWorld = GameContext.WorldPointer;
            _tilesBuiltAt = Time.unscaledTime;
            Tiles.Clear();
            var dirt = RawPool.Of<TileDirtinessComponent>(world, 16);
            var coords = RawPool.Of<GridCoordinatesComponent>(world, 8);
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            var roomTiles = RawPool.Of<RoomTileComponent>(world, 8);
            int viaRoom = 0;
            foreach (int e in dirt.Entities())
            {
                IntPtr c = coords.Item(e);
                if (c == IntPtr.Zero) continue;
                IntPtr f = floors.Item(e);
                if (f == IntPtr.Zero)
                {
                    // A square without a floor of its own takes its room's.
                    IntPtr rt = roomTiles.Item(e);
                    f = rt == IntPtr.Zero ? IntPtr.Zero : floors.Item(*(int*)(rt + 4));
                    if (f == IntPtr.Zero) continue;
                    viaRoom++;
                }
                Tiles[(*(int*)f, *(int*)c, *(int*)(c + 4))] = e;
            }
            if (first) Plugin.Logger.LogInfo($"[Cleaning] {Tiles.Count} floor square(s) indexed ({viaRoom} placed on a floor through their room); a cleaning stop cleans the squares of its room within {Settings.CleaningRadius.Value} square(s)");
        }

        private static void Report(Exception e)
        {
            Pending.Clear();
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Cleaning] error, the game's cleaning goes on unchanged: {e.Message}");
        }
    }
}
