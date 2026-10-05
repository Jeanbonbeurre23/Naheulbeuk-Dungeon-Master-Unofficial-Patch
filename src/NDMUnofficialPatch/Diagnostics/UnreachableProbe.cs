using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using Unity.Mathematics;

namespace NDMUnofficialPatch.Diagnostics
{
    // Writes to the log the squares the builder calls unreachable, and what stands around them.
    //
    // In testing on 30 September 2026, on a third floor, the builder refused constructions because some squares were
    // not accessible, although nothing was visible there. Game 1.8, from the method bodies and field lists:
    // the builder runs CheckUnreachableTilesJob after each change it validates (a prop, a door, a wall section, an undo,
    // a floor change, the destruction of a prop). The job walks the floor from the dungeon entrances and the stairs'
    // free square (StairsUtility.GetUnblockedTile) and stops at walls (WallDataComponent), doors and the squares that
    // props block (GridBlockComponent, per-square description of the prop's footprint); the squares it cannot reach
    // end in UnreachableTiles, which BuilderJobs.UpdateUnreachableTilesJob hands to Builder.SetUnreachableTiles. The
    // builder then draws them on the ground and refuses to validate.
    //
    // A postfix on Builder.SetUnreachableTiles writes, when the set of squares changes: the floor being built
    // (BuilderManagerComponent.BuildingFloor), the squares grouped into connected zones, and for each zone the entities
    // on that floor whose grid position lies within 2 squares of it (GridCoordinatesComponent, GridFloorComponent,
    // GridSizeComponent). Entities are grouped by the list of their components (EcsWorld.GetComponentTypes): a group of
    // more than 5 entities is written as a count, a smaller one entity by entity with its position, size and prop type
    // (PropIdentityComponent). An entity that blocks squares without anything visible would show there with an odd list
    // of components.
    //
    // Plugin 0.13.1 adds, after the session of 30 September 2026 in which nearly the whole third floor was marked
    // (about 1100 squares, so the entity list covered the whole floor): the job's start (CheckUnreachableTilesJob:
    // m_stairTile, the stairs entity the walk starts beside, CurrentFloor and CurrentRoom, read from Builder.m_jobs), and
    // a map of the floor with one character per square. The entity list is skipped for a zone with more than 300
    // entities around it. At most 30 maps are written per session.
    //
    // Plugin 0.13.2 adds, after the second session of 30 September 2026 in which the job always started from stairs
    // 51884 and reached nothing: for every stairs entity of the floor, the free square the game starts from
    // (StairsUtility.GetUnblockedTile, reached through Builder.m_stairsUtility), whether that square is among the
    // squares the builder calls unreachable (when the walk reached nothing, that set holds every walkable square), and
    // how many of those squares a walk from it over the set (4 neighbours, as CheckUnreachableTilesJob.Execute walks)
    // would reach; the entities on and around the job's stairs, one by one; and in the map, '*' for the job's free
    // square and 'g' ('G' when in the set) for room squares that carry a GridBlockComponent of their own.
    internal static unsafe class UnreachableProbe
    {
        private const int Margin = 2;
        private const int ListLimit = 5;
        private const int MaxZones = 10;
        private const int MaxTilesListed = 60;
        private const int MaxListedEntities = 300;
        private const int MaxMaps = 30;
        private static int _maps;

        private static string _lastKey = "";
        private static bool _errorLogged, _layoutChecked;
        private static int _sizeOffset;

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            Il2CppRaw.ExpectValueFieldOffset<GridCoordinatesComponent>("Coordinates", 0);
            Il2CppRaw.ExpectValueFieldOffset<GridFloorComponent>("Floor", 0);
            Il2CppRaw.ExpectValueFieldOffset<PropIdentityComponent>("Type", 0);
            _sizeOffset = Il2CppRaw.ValueFieldOffset<GridSizeComponent>("Size");
            _layoutChecked = true;
        }

        internal static void OnSet(Builder builder, Il2CppSystem.Collections.Generic.HashSet<int2> set)
        {
            try
            {
                if (!GameContext.TryWorld(out var world, out int size)) return;
                CheckLayout();
                var tiles = new List<(int X, int Y)>();
                if (set != null)
                {
                    var e = set.GetEnumerator();
                    while (e.MoveNext()) tiles.Add((e.Current.x, e.Current.y));
                }
                tiles.Sort();
                int floor = BuildingFloor(world);
                string key = floor + ":" + string.Join(";", tiles.Select(t => t.X + "," + t.Y));
                if (key == _lastKey) return;
                bool wasEmpty = _lastKey.EndsWith(":");
                _lastKey = key;
                if (tiles.Count == 0)
                {
                    if (!wasEmpty) Plugin.Logger.LogInfo($"[Unreachable] floor {floor}: no unreachable square now");
                    return;
                }

                var zones = Zones(tiles);
                Plugin.Logger.LogInfo($"[Unreachable] floor {floor}: {tiles.Count} unreachable square(s) in {zones.Count} zone(s): " +
                    string.Join("; ", zones.Select(z => $"{z.Count} from ({z.Min(t => t.X)},{z.Min(t => t.Y)}) to ({z.Max(t => t.X)},{z.Max(t => t.Y)})")));
                Plugin.Logger.LogInfo("[Unreachable] squares: " + string.Join(" ", tiles.Take(MaxTilesListed).Select(t => $"({t.X},{t.Y})")) +
                    (tiles.Count > MaxTilesListed ? $" and {tiles.Count - MaxTilesListed} more" : ""));
                int stair = JobStart(builder);
                var squares = new HashSet<(int, int)>(tiles);

                var coords = RawPool.Of<GridCoordinatesComponent>(world, -1);
                var floors = RawPool.Of<GridFloorComponent>(world, -1);
                var sizes = RawPool.Of<GridSizeComponent>(world, -1);
                var props = RawPool.Of<PropIdentityComponent>(world, -1);
                var onFloor = new List<(int Entity, int X, int Y, int W, int H)>();
                foreach (int ent in coords.Entities())
                {
                    if (!world.IsEntityAlive(ent, size)) continue;
                    IntPtr f = floors.Item(ent);
                    if (f == IntPtr.Zero || *(int*)f != floor) continue;
                    var c = *(int2*)coords.Item(ent);
                    int w = 1, h = 1;
                    IntPtr s = sizes.Item(ent);
                    if (s != IntPtr.Zero) { var sz = *(int2*)(s + _sizeOffset); w = Math.Max(1, sz.x); h = Math.Max(1, sz.y); }
                    onFloor.Add((ent, c.x, c.y, w, h));
                }

                int zoneIndex = 0;
                foreach (var zone in zones.Take(MaxZones))
                {
                    zoneIndex++;
                    int x0 = zone.Min(t => t.X) - Margin, x1 = zone.Max(t => t.X) + Margin;
                    int y0 = zone.Min(t => t.Y) - Margin, y1 = zone.Max(t => t.Y) + Margin;
                    // The footprint's anchor is not known for every prop, so the size is taken as reaching either way.
                    var near = onFloor.Where(o => o.X + Math.Max(o.W, o.H) - 1 >= x0 && o.X - Math.Max(o.W, o.H) + 1 <= x1
                                               && o.Y + Math.Max(o.W, o.H) - 1 >= y0 && o.Y - Math.Max(o.W, o.H) + 1 <= y1).ToList();
                    var groups = new Dictionary<string, List<(int Entity, int X, int Y, int W, int H)>>();
                    foreach (var o in near)
                    {
                        string sig = Signature(world, o.Entity);
                        if (!groups.TryGetValue(sig, out var g)) groups[sig] = g = new();
                        g.Add(o);
                    }
                    Plugin.Logger.LogInfo($"[Unreachable] zone {zoneIndex}: {near.Count} entit(ies) within {Margin} squares, {groups.Count} kind(s)");
                    if (near.Count > MaxListedEntities)
                    {
                        Plugin.Logger.LogInfo("[Unreachable]   too many to list; see the map");
                        continue;
                    }
                    foreach (var g in groups.OrderBy(p => p.Value.Count))
                    {
                        if (g.Value.Count > ListLimit)
                        {
                            Plugin.Logger.LogInfo($"[Unreachable]   {g.Value.Count} x [{g.Key}]");
                            continue;
                        }
                        foreach (var o in g.Value)
                        {
                            var sb = new StringBuilder($"[Unreachable]   entity {o.Entity} at ({o.X},{o.Y}) size {o.W}x{o.H}");
                            IntPtr p = props.Item(o.Entity);
                            if (p != IntPtr.Zero) sb.Append(", prop type ").Append(*(PropType*)p);
                            sb.Append(" [").Append(g.Key).Append(']');
                            Plugin.Logger.LogInfo(sb.ToString());
                        }
                    }
                }
                if (zones.Count > MaxZones) Plugin.Logger.LogInfo($"[Unreachable] {zones.Count - MaxZones} more zone(s) not described");
                if (_maps < MaxMaps)
                {
                    _maps++;
                    var start = Starts(world, builder, onFloor, squares, stair);
                    Map(world, floor, onFloor, squares, stair, start);
                    AroundStart(world, onFloor, squares, stair);
                }
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Unreachable] describing the unreachable squares failed, further errors are not logged: {e}");
            }
        }

        // The job's start: the stairs entity it walks from (-1 when none), with its floor and room, written to the log.
        private static int JobStart(Builder builder)
        {
            try
            {
                var jobs = builder?.m_jobs;
                if (jobs == null) return -1;
                var job = jobs.m_checkUnreachableJob;
                if (job == null) return -1;
                Plugin.Logger.LogInfo($"[Unreachable] job: stairs entity {job.m_stairTile}, floor {job.CurrentFloor}, room {job.CurrentRoom}");
                return job.m_stairTile;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogInfo($"[Unreachable] job fields unreadable: {e.Message}");
                return -1;
            }
        }

        // One character per square of the floor, rows from the smallest y down to the largest, columns from the
        // smallest x. The walking start is '@' (the stairs entity the job names), other stairs 'S', doors 'D', tavern
        // entries and teleporters 'T', hidden internal walls 'h', walls '#', dead zones 'x', other props that block
        // squares 'o', room squares with a GridBlockComponent of their own 'g' ('G' in the set), room squares '.',
        // corridor squares ','; '*' the free square of the job's stairs; '!' marks a room or corridor square the builder calls
        // unreachable, and '?' an unreachable square with nothing else known there. Footprints of props and doors are
        // drawn from their grid position over their size.
        // For every stairs entity of the floor: its free square, whether it is in the set, and how many squares of the
        // set a walk from it would reach. Returns the job's stairs' free square, or null.
        private static (int, int)? Starts(EcsWorld world, Builder builder, List<(int Entity, int X, int Y, int W, int H)> onFloor, HashSet<(int, int)> set, int startStair)
        {
            (int, int)? jobStart = null;
            var stairs = RawPool.Of<StairComponent>(world, -1);
            StairsUtility util = null;
            try { util = builder?.m_stairsUtility; } catch { }
            if (util == null) { Plugin.Logger.LogInfo("[Unreachable] no StairsUtility on the builder"); return null; }
            foreach (var o in onFloor)
            {
                if (!stairs.Has(o.Entity)) continue;
                try
                {
                    var t = util.GetUnblockedTile(o.Entity);
                    var tile = (t.x, t.y);
                    if (o.Entity == startStair) jobStart = tile;
                    bool inSet = set.Contains(tile);
                    int reach = inSet ? Walk(set, tile) : 0;
                    Plugin.Logger.LogInfo($"[Unreachable] stairs {o.Entity}{(o.Entity == startStair ? " (the job's)" : "")}: free square ({t.x},{t.y}), " +
                        (inSet ? $"in the set; a walk from it would reach {reach} of the {set.Count} squares" : "not in the set, so a walk from it reaches nothing"));
                }
                catch (Exception e) { Plugin.Logger.LogInfo($"[Unreachable] stairs {o.Entity}: free square unreadable: {e.Message}"); }
            }
            return jobStart;
        }

        // Squares of the set reached from a square of the set, moving to the 4 neighbours that are in the set.
        private static int Walk(HashSet<(int, int)> set, (int, int) from)
        {
            var seen = new HashSet<(int, int)> { from };
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var n in new[] { (t.X + 1, t.Y), (t.X - 1, t.Y), (t.X, t.Y + 1), (t.X, t.Y - 1) })
                    if (set.Contains(n) && seen.Add(n)) queue.Enqueue(n);
            }
            return seen.Count;
        }

        // Every entity whose grid position lies on the job's stairs or within 2 squares of them, one line each.
        private static void AroundStart(EcsWorld world, List<(int Entity, int X, int Y, int W, int H)> onFloor, HashSet<(int, int)> set, int startStair)
        {
            var st = onFloor.FirstOrDefault(o => o.Entity == startStair);
            if (st.Entity != startStair || startStair < 0) return;
            var props = RawPool.Of<PropIdentityComponent>(world, -1);
            int n = 0;
            foreach (var o in onFloor)
            {
                if (o.X < st.X - 2 || o.X > st.X + st.W + 1 || o.Y < st.Y - 2 || o.Y > st.Y + st.H + 1) continue;
                if (++n > 80) { Plugin.Logger.LogInfo("[Unreachable]   and more"); break; }
                IntPtr p = props.Item(o.Entity);
                Plugin.Logger.LogInfo($"[Unreachable]   near the job's stairs: entity {o.Entity} at ({o.X},{o.Y}) size {o.W}x{o.H}" +
                    (p != IntPtr.Zero ? $", prop type {*(PropType*)p}" : "") + $", square {(set.Contains((o.X, o.Y)) ? "in" : "not in")} the set [{Signature(world, o.Entity)}]");
            }
        }

        private static void Map(EcsWorld world, int floor, List<(int Entity, int X, int Y, int W, int H)> onFloor, HashSet<(int, int)> unreachable, int startStair, (int, int)? startTile)
        {
            var stairs = RawPool.Of<StairComponent>(world, -1);
            var doors = RawPool.Of<DoorComponent>(world, -1);
            var teleporters = RawPool.Of<TeleporterTagComponent>(world, -1);
            var hidden = RawPool.Of<Walls.InternalHiddenWallTag>(world, -1);
            var walls = RawPool.Of<Walls.WallDataComponent>(world, -1);
            var dead = RawPool.Of<DeadZoneComponent>(world, -1);
            var roomTiles = RawPool.Of<RoomTileComponent>(world, -1);
            var blocks = RawPool.Of<GridBlockComponent>(world, -1);
            var props = RawPool.Of<PropIdentityComponent>(world, -1);
            var cells = new Dictionary<(int, int), (int Rank, char C)>();
            void Put(int x, int y, int rank, char c)
            {
                if (!cells.TryGetValue((x, y), out var cur) || rank > cur.Rank) cells[(x, y)] = (rank, c);
            }
            foreach (var o in onFloor)
            {
                int e = o.Entity;
                int rank; char c; bool footprint = true;
                if (e == startStair) { rank = 10; c = '@'; }
                else if (stairs.Has(e)) { rank = 9; c = 'S'; }
                else if (doors.Has(e)) { rank = 8; c = 'D'; }
                else if (teleporters.Has(e) || (props.Item(e) != IntPtr.Zero && *(PropType*)props.Item(e) == PropType.TAVERN_ENTRY)) { rank = 7; c = 'T'; }
                else if (hidden.Has(e)) { rank = 6; c = 'h'; footprint = false; }
                else if (walls.Has(e)) { rank = 5; c = '#'; footprint = false; }
                else if (dead.Has(e)) { rank = 4; c = 'x'; footprint = false; }
                else if (props.Has(e) && blocks.Has(e)) { rank = 3; c = 'o'; }
                else if (roomTiles.Has(e) && blocks.Has(e))
                {
                    rank = 2; c = 'g';
                    footprint = false;
                }
                else if (roomTiles.Has(e))
                {
                    var type = *(RoomType*)roomTiles.Item(e);
                    rank = 1; c = type == RoomType.CORRIDOR ? ',' : '.';
                    footprint = false;
                }
                else continue;
                if (!footprint) { Put(o.X, o.Y, rank, c); continue; }
                for (int dx = 0; dx < o.W; dx++)
                    for (int dy = 0; dy < o.H; dy++) Put(o.X + dx, o.Y + dy, rank, c);
            }
            foreach (var t in unreachable)
            {
                if (!cells.TryGetValue(t, out var cur)) cells[t] = (0, '?');
                else if (cur.C == '.' || cur.C == ',') cells[t] = (cur.Rank, '!');
                else if (cur.C == 'g') cells[t] = (cur.Rank, 'G');
            }
            if (startTile.HasValue) cells[startTile.Value] = (11, '*');
            if (cells.Count == 0) return;
            int x0 = cells.Keys.Min(k => k.Item1), x1 = cells.Keys.Max(k => k.Item1);
            int y0 = cells.Keys.Min(k => k.Item2), y1 = cells.Keys.Max(k => k.Item2);
            var sb = new StringBuilder($"[Unreachable] map of floor {floor}, x {x0} to {x1} left to right, y {y0} to {y1} top to bottom:");
            sb.Append('\n').Append("      ");
            for (int x = x0; x <= x1; x++) sb.Append(Math.Abs(x) / 10 % 10);
            sb.Append('\n').Append("      ");
            for (int x = x0; x <= x1; x++) sb.Append(Math.Abs(x) % 10);
            for (int y = y0; y <= y1; y++)
            {
                sb.Append('\n').Append(y.ToString().PadLeft(5)).Append(' ');
                for (int x = x0; x <= x1; x++) sb.Append(cells.TryGetValue((x, y), out var cur) ? cur.C : ' ');
            }
            Plugin.Logger.LogInfo(sb.ToString());
            foreach (var o in onFloor)
                if (stairs.Has(o.Entity))
                    Plugin.Logger.LogInfo($"[Unreachable] stairs entity {o.Entity} at ({o.X},{o.Y}) size {o.W}x{o.H}{(o.Entity == startStair ? ", the job's start" : "")}");
        }

        private static int BuildingFloor(EcsWorld world)
        {
            var pool = RawPool.Of<BuilderManagerComponent>(world, -1);
            int ent = pool.FirstEntity();
            if (ent < 0) return -1;
            return *(int*)(pool.Item(ent) + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("BuildingFloor"));
        }

        // Squares that touch by a side belong to the same zone.
        private static List<List<(int X, int Y)>> Zones(List<(int X, int Y)> tiles)
        {
            var left = new HashSet<(int, int)>(tiles);
            var zones = new List<List<(int X, int Y)>>();
            while (left.Count > 0)
            {
                var start = left.First();
                left.Remove(start);
                var zone = new List<(int X, int Y)>();
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    zone.Add(t);
                    foreach (var n in new[] { (t.X + 1, t.Y), (t.X - 1, t.Y), (t.X, t.Y + 1), (t.X, t.Y - 1) })
                        if (left.Remove(n)) queue.Enqueue(n);
                }
                zones.Add(zone);
            }
            return zones.OrderByDescending(z => z.Count).ToList();
        }

        private static string Signature(EcsWorld world, int entity)
        {
            var list = new Il2CppReferenceArray<Il2CppSystem.Type>(0);
            int n = world.GetComponentTypes(entity, ref list);
            var names = new List<string>();
            for (int i = 0; i < n && i < list.Length; i++) names.Add(list[i]?.Name ?? "?");
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }
    }

    [HarmonyPatch(typeof(Builder), nameof(Builder.SetUnreachableTiles))]
    internal static class UnreachableTilesPatch
    {
        private static bool Prepare() => Settings.DiagnosticsUnreachable.Value;
        private static void Postfix(Builder __instance, Il2CppSystem.Collections.Generic.HashSet<int2> unreachableTiles) => UnreachableProbe.OnSet(__instance, unreachableTiles);
    }
}
