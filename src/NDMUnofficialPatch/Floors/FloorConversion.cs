using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using Unity.Mathematics;
using UnityEngine;

namespace NDMUnofficialPatch.Floors
{
    // The first load of a save converted to inserted floors (tools/convert_save_floors.py, docs/features/floor-conversion.md).
    //
    // The conversion renumbers the save's floors 5 and 6 and writes, for each new floor, its floor data entity, its place
    // in the floor manager and an empty wall map. It leaves the floor itself to the game: on a load, every inserted floor
    // that has its floor data and no entity on it (no square, wall or piece of furniture) is made here as a new game
    // makes it, with the game's own functions and in the game's order. Every load of a save with inserted floors is
    // checked, so a converted save saved before its floors were built (as with plugin 0.23.0) gets them on its next
    // load; a floor that has entities, or no floor data, is left alone, so nothing is built twice.
    //   1. Base room and squares, as GridInitSystem.InitAsync makes them in a new game: RoomsUtility.AddRoom (type
    //      CORRIDOR, the floor's width x length), LightsUtility.UpdateRoomLight, the room's VisualComponent.Instance set
    //      to the floor's ground object, and RoomsUtility.AddOrUpdateRoomTile with every square of the floor. Done when
    //      the first root of an inserted floor starts its Init, that is once the save's world and the systems exist.
    //   2. Level content (walls, doors, garden, dead zones, floor layer): DungeonGameMode.InitSpawners runs
    //      FloorRoot.Init(isLoadGame) for every floor root. The compiler inlined FloorRoot.Init into InitSpawners, so a
    //      hook on Init never runs (the 0.23.0 test); the hook is on the iterator's MoveNext (FloorRoot._Init_d__2), whose
    //      first step reads isLoadGame. For the floors built here it is set to false before that step, the new-game path,
    //      which runs the scene's spawners where a loaded game would destroy them.
    //   3. Unlock: once the loading has injected the interface (DungeonGameMode.InjectUI), each floor built here is
    //      unlocked through FloorUtility.UnlockFloor as in a new game when floor 4 unlocks (FloorInsertion.UnlockInserted),
    //      if floor 4 is unlocked.
    //   4. Stairs: on the same loads, links between stairs that are not one floor apart, or to a stair that no longer
    //      exists, are removed (RepairStairs).
    internal static unsafe class FloorConversion
    {
        private static bool _pending;
        private static bool _requested; // the marker asked for the build (a save just converted)
        private static int _first, _last;
        private static bool _roomsTried;
        private static readonly HashSet<int> Built = new(); // floors given a base room and squares on this load
        private static readonly List<string> Spawned = new();
        private static float _unlockAt = -1f;

        internal static bool Pending => _pending;

        // Called by FloorInsertion when the scenes of a load of a save with inserted floors are set up.
        internal static void Arm(int first, int last, bool requested)
        {
            _pending = true;
            _requested = requested;
            _first = first;
            _last = last;
            _roomsTried = false;
            Built.Clear();
            Spawned.Clear();
            _unlockAt = -1f;
            _stairsAt = -1f;
            _loaded = false;
            if (requested) Plugin.Logger.LogInfo($"[Floors] converted save: floors {first} to {last} will be built on this load");
        }

        internal static void Disarm()
        {
            _pending = false;
            _loaded = false;
            _unlockAt = -1f;
            _stairsAt = -1f;
        }

        // FloorRoot._Init_d__2.MoveNext prefix: on the iterator's first step, the floors built here take the new-game path.
        internal static void BeforeInitStep(FloorRoot._Init_d__2 step)
        {
            if (!_pending || step == null || step.__1__state != 0 || !step.isLoadGame) return;
            FloorRoot root = step.__4__this;
            if (root == null) return;
            int floor = root.m_floor;
            if (floor < _first || floor > _last) return;
            if (!_roomsTried) BuildRooms();
            if (!Built.Contains(floor)) return;
            step.isLoadGame = false;
            Spawned.Add($"{root.gameObject.scene.name}/{root.gameObject.name}");
        }

        // DungeonGameMode.InjectUI postfix: the load has run every floor root's Init.
        internal static void AfterLoad()
        {
            if (!_pending) return;
            _loaded = true;
            _stairsAt = Time.realtimeSinceStartup + 1f;
            if (Built.Count == 0 && !_requested) return;
            Plugin.Logger.LogInfo($"[Floors] converted save loaded; floor roots run as for a new game: {(Spawned.Count == 0 ? "none" : string.Join(", ", Spawned))}");
            _unlockAt = Time.realtimeSinceStartup + 1f;
        }

        // Every frame, from FloorInsertion.Update.
        internal static void Update()
        {
            if (!_pending || !_loaded) return;
            float now = Time.realtimeSinceStartup;
            if (_stairsAt >= 0f && now >= _stairsAt)
            {
                _stairsAt = -1f;
                try { RepairStairs(); }
                catch (Exception e) { Plugin.Logger.LogError($"[Floors] checking the stair links: {e}"); }
            }
            if (_unlockAt >= 0f && now >= _unlockAt)
            {
                _unlockAt = -1f;
                try { UnlockBuilt(); }
                catch (Exception e) { Plugin.Logger.LogError($"[Floors] unlocking the converted floors: {e}"); }
            }
            if (_stairsAt < 0f && _unlockAt < 0f) _pending = false;
        }

        // ---- Stairs ---------------------------------------------------------------------------------------------------

        // A stair is linked to the stairs of the floors just above and below at its place (StairComponent.LinkedStairs).
        // A converted save made before 0.23.3 keeps the links it had: the stair from floor 4 to the tavern floor then
        // links floor 4 to floor 5+N, and the pathfinding, which expects the linked stair on the next floor, throws
        // KeyNotFoundException on every search (the tests of 0.23.1 and 0.23.2). An exception in ComputePathfindSystem
        // stops the rest of the frame's systems, among them the one that deletes the frame's area-of-effect events, so
        // those events pile up: a test autosave held 468,251 of them and 580,000 entities, the cause of the lag,
        // of the interface glitches, and at last of EcsWorld.NewEntity failing to grow the world (ArgumentOutOfRange).
        //
        // StairsUtility.SetupLinks only adds links (it keeps a stair's old links and adds the stairs found one floor
        // away at the same place), so 0.23.2, which called it alone, changed nothing. Every load of a save with inserted
        // floors now removes, in place, each link to a stair that is not one floor away or no longer exists: the
        // remaining links are moved to the front of the stair's NativeArray and its length is lowered (the buffer stays
        // allocated and is freed as before). SetupLinks is then called on the stair to refresh its visual and mark the
        // pathfinding dirty, and UpdateCanBuildStair to refresh its build buttons.
        private static float _stairsAt = -1f;
        private static bool _loaded; // DungeonGameMode.InjectUI has run on this load

        private static void RepairStairs()
        {
            var mode = DungeonGameMode.Instance;
            EcsWorld world = mode == null || mode.DataManager == null ? null : mode.DataManager.m_world;
            if (world == null || !world.IsAlive()) return;
            int size = world.GetWorldSize();
            var stairPool = RawPool.Of<StairComponent>(world, -1);
            var floorPool = RawPool.Of<GridFloorComponent>(world, 4);
            if (stairPool.IsNull || floorPool.IsNull) return;
            int linksAt = Il2CppRaw.ValueFieldOffset<StairComponent>("LinkedStairs");
            var repaired = new List<int>();
            var found = new List<string>();
            foreach (int e in stairPool.Entities())
            {
                IntPtr item = stairPool.Item(e);
                IntPtr mine = floorPool.Item(e);
                if (item == IntPtr.Zero || mine == IntPtr.Zero) continue;
                int floor = *(int*)mine;
                // NativeArray<int>: buffer pointer, then length.
                int* buffer = *(int**)(item + linksAt);
                int* length = (int*)(item + linksAt + IntPtr.Size);
                if (*length < 0 || *length > 16 || (*length > 0 && buffer == null))
                {
                    Plugin.Logger.LogWarning($"[Floors] stair {e}: links not readable (length {*length}); stairs left as they are");
                    return;
                }
                int kept = 0;
                for (int i = 0; i < *length; i++)
                {
                    int other = buffer[i];
                    IntPtr theirs = other >= 0 && other < size && world.IsEntityAlive(other, size) ? floorPool.Item(other) : IntPtr.Zero;
                    if (theirs != IntPtr.Zero && Math.Abs(*(int*)theirs - floor) == 1)
                    {
                        buffer[kept++] = other;
                        continue;
                    }
                    found.Add($"stair {e} on floor {floor} linked to " + (theirs == IntPtr.Zero ? $"{other}, gone" : $"{other} on floor {*(int*)theirs}"));
                }
                if (kept == *length) continue;
                *length = kept;
                repaired.Add(e);
            }
            if (repaired.Count == 0) return;
            var after = new List<string>();
            try
            {
                var stairs = Utility<StairsUtility>(world.GetUtilities());
                foreach (int e in repaired) stairs.SetupLinks(e, true);
                foreach (int e in repaired) stairs.UpdateCanBuildStair(e);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Floors] refreshing the repaired stairs: {ex.Message}"); }
            foreach (int e in repaired)
            {
                IntPtr item = stairPool.Item(e);
                if (item == IntPtr.Zero) continue;
                int* buffer = *(int**)(item + linksAt);
                int length = *(int*)(item + linksAt + IntPtr.Size);
                var ids = new List<string>();
                for (int i = 0; i < length && i < 16 && buffer != null; i++) ids.Add(buffer[i].ToString());
                after.Add($"{e}: [{string.Join(", ", ids)}]");
            }
            Plugin.Logger.LogInfo($"[Floors] stair links removed: {string.Join("; ", found)}. Links now: {string.Join("; ", after)}");
        }

        // ---- 1. Base room and squares --------------------------------------------------------------------------------

        private static T Utility<T>(Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, IUtility> utilities) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            var type = Il2CppType.Of<T>();
            if (!utilities.ContainsKey(type)) throw new InvalidOperationException($"the world has no {typeof(T).Name}");
            return utilities[type].Cast<T>();
        }

        private static void BuildRooms()
        {
            _roomsTried = true;
            var report = new StringBuilder();
            try
            {
                var mode = DungeonGameMode.Instance;
                var data = mode == null ? null : mode.DataManager;
                EcsWorld world = data == null ? null : data.m_world;
                if (world == null || !world.IsAlive()) throw new InvalidOperationException("no world yet");
                var withData = FloorsOf<FloorDataComponent>(world, "Floor");
                var withEntities = FloorsOf<GridFloorComponent>(world, "Floor");
                bool any = false;
                for (int floor = _first; floor <= _last; floor++) if (withData.Contains(floor) && !withEntities.Contains(floor)) any = true;
                if (!any)
                {
                    if (_requested) Plugin.Logger.LogInfo($"[Floors] converted save: floors {_first} to {_last} already built, or without floor data; nothing built");
                    return;
                }
                var utilities = world.GetUtilities();
                var rooms = Utility<RoomsUtility>(utilities);
                var lights = Utility<Lights.LightsUtility>(utilities);
                RoomConfig corridor = CorridorConfig(report);
                var grids = mode.Floors;
                for (int floor = _first; floor <= _last; floor++)
                {
                    if (!withData.Contains(floor)) { report.Append($" floor {floor}: no floor data entity, left alone;"); continue; }
                    if (withEntities.Contains(floor)) { report.Append($" floor {floor}: already has entities, left alone;"); continue; }
                    if (grids == null || !grids.ContainsKey(floor)) { report.Append($" floor {floor}: no floor grid in the scenes, left alone;"); continue; }
                    var grid = grids[floor];
                    int width = grid.m_width, length = grid.m_length;
                    int room = rooms.AddRoom(floor, RoomType.CORRIDOR, width * length, 0, corridor);
                    lights.UpdateRoomLight(room);
                    string visual = SetRoomVisual(world, room, grid.m_ground);
                    var squares = new Il2CppSystem.Collections.Generic.List<int2>();
                    for (int x = 0; x < width; x++)
                        for (int y = 0; y < length; y++)
                            squares.Add(new int2(x, y));
                    rooms.AddOrUpdateRoomTile(room, floor, squares);
                    Built.Add(floor);
                    report.Append($" floor {floor}: base room {room} ({width} x {length} squares), {visual};");
                }
                Plugin.Logger.LogInfo($"[Floors] converted save, base rooms:{report}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[Floors] converted save, building the base rooms failed:{report} {e}");
            }
        }

        // The room config of the floors' base rooms (CORRIDOR), as GridInitSystem takes it from the world config.
        private static RoomConfig CorridorConfig(StringBuilder report)
        {
            RoomConfig found = null;
            var names = new List<string>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RoomConfig>()))
            {
                var c = o.Cast<RoomConfig>();
                if (c.Type != RoomType.CORRIDOR) continue;
                names.Add(c.name);
                found ??= c;
            }
            if (found == null) throw new InvalidOperationException("no RoomConfig of type CORRIDOR loaded");
            report.Append($" corridor config {found.name}" + (names.Count > 1 ? $" (of {names.Count}: {string.Join(", ", names)})" : "") + ";");
            return found;
        }

        // The floors that have at least one entity with the component, read from its int field.
        private static HashSet<int> FloorsOf<T>(EcsWorld world, string field)
        {
            var result = new HashSet<int>();
            var pool = RawPool.Of<T>(world, -1);
            if (pool.IsNull) return result;
            int at = Il2CppRaw.ValueFieldOffset<T>(field);
            foreach (int e in pool.Entities())
            {
                IntPtr item = pool.Item(e);
                if (item != IntPtr.Zero) result.Add(*(int*)(item + at));
            }
            return result;
        }

        // GridInitSystem gives the base room's VisualComponent the floor's ground object. The component is read in place
        // and the reference written through the garbage collector's write barrier.
        private static string SetRoomVisual(EcsWorld world, int room, GameObject ground)
        {
            if (ground == null) return "no ground object";
            IEcsPool pool = world.GetPoolByType(Il2CppType.Of<VisualComponent>());
            if (pool == null) return "no visual pool";
            var raw = RawPool.From(pool.Pointer, -1, "VisualComponent");
            IntPtr item = raw.Item(room);
            if (item == IntPtr.Zero) return "room has no VisualComponent";
            IntPtr dense = Il2CppRaw.ReadPointer(pool.Pointer, "_denseItems");
            IntPtr field = item + Il2CppRaw.ValueFieldOffset<VisualComponent>("Instance");
            IL2CPP.il2cpp_gc_wbarrier_set_field(dense, field, ground.Pointer);
            return "visual set to " + ground.name;
        }

        // ---- 3. Unlock -----------------------------------------------------------------------------------------------

        private static void UnlockBuilt()
        {
            if (Built.Count == 0) { if (_requested) Plugin.Logger.LogInfo("[Floors] converted save: no floor built on this load, nothing to unlock"); return; }
            var mode = DungeonGameMode.Instance;
            EcsWorld world = mode == null || mode.DataManager == null ? null : mode.DataManager.m_world;
            if (world == null || !world.IsAlive()) throw new InvalidOperationException("no world");
            var floors = Utility<FloorUtility>(world.GetUtilities());
            if (floors.IsFloorLocked(FloorInsertion.Copied))
            {
                Plugin.Logger.LogInfo("[Floors] converted save: floor 4 is locked, so the new floors stay locked and unlock with it");
                return;
            }
            var done = new List<string>();
            var sorted = new List<int>(Built);
            sorted.Sort();
            foreach (int floor in sorted) done.Add(FloorInsertion.UnlockInserted(floors, world, floor));
            Plugin.Logger.LogInfo($"[Floors] converted save: floor 4 is unlocked, new floors unlocked with it: {string.Join(", ", done)}. Save the game to keep them.");
        }
    }
}
