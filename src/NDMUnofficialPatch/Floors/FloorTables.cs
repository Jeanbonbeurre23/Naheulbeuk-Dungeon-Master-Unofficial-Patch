using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Floors
{
    // The game's tables keyed by floor number, extended for N inserted floors (FloorInsertion) and put back for games
    // played without them. These are configs, shared by every game of a session. Most are loaded with the world, after
    // the scenes, so each table is taken when it is first found: its game values are kept, then it is changed while a
    // game has inserted floors. Apply is called several times while a game starts and changes only what it has not
    // changed yet for that number of floors.
    //
    //   FloorInformationsConfig: upkeep per decade, minion limits (two versions) and distance, keys 0 to 6. The
    //     inserted floors 5 to 4+N take floor 4's entry, 5+N takes 5's and 6+N takes 6's.
    //   UIGameConfig.FloorDescriptions, keys -2 to 6: keys 7 to 6+N take key 6's description.
    //   FloorTreeComponentConfig.m_floorNumber of the unlock nodes of floors 5 and 6 (LockFloor5, LockFloor6): 5+N, 6+N.
    //   UnlockFloorConditionConfig.m_nbFloor, a condition that a given floor is unlocked: 5 and 6 become 5+N and 6+N.
    //   Wall pieces (WallPartConfig<T>.Items[].Range, floors -2..6 or 0..6, in BuilderConfig.TopWalls and TopPatch and in
    //     every RoomConfig's CornerWalls and SideWalls): a range ending at floor 6 ends at floor 6+N. The game picks a
    //     wall piece by floor (WallPartConfig.GetPartConfig) and gets nothing above 6 otherwise, which threw in the wall
    //     systems every frame in the first test of 0.22.0. All floors share the same pieces, so no floor changes look.
    internal static unsafe class FloorTables
    {
        private sealed class Table<T>
        {
            public string Name;
            public Aube.SerializableDictionary<int, T> D;
            public readonly Dictionary<int, T> Orig = new();
            public readonly HashSet<int> Had = new();
            public int ShiftedBy; // number of inserted floors the table is extended for, 0 when it holds the game's values
        }

        private static bool _active;
        private static int _n;
        private static readonly HashSet<IntPtr> Seen = new();
        private static readonly List<Table<int>> IntTables = new();
        private static readonly List<Table<float>> FloatTables = new();
        private static readonly List<(Aube.SerializableDictionary<int, Aube.LocalizationKey> D, int Added)> Descriptions = new(); // Added: keys 7 to 6+Added added
        private static readonly List<(FloorTreeComponentConfig Config, int Orig)> Trees = new();
        private static readonly List<(UnlockFloorConditionConfig Config, int Orig)> Conditions = new();
        private static readonly List<IntPtr> WallRangeEnds = new(); // addresses of Range.y values that were 6
        private static readonly HashSet<IntPtr> WallItemsSeen = new();
        // The configs that own the wall items, held so that the items stay alive while their addresses are kept.
        private static readonly List<Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase> WallOwners = new();
        private static int _wallsEnd = 6;

        internal static int WallRanges => WallRangeEnds.Count;

        private static IEnumerable<T> All<T>() where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())) yield return o.Cast<T>();
        }

        private static bool First(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase o) => o != null && Seen.Add(o.Pointer);

        // Takes every table not seen yet, with its game values.
        private static void Capture()
        {
            foreach (var cfg in All<FloorInformationsConfig>())
            {
                if (!First(cfg)) continue;
                AddInt(cfg.name + ".m_floorsDecadeCosts", cfg.m_floorsDecadeCosts);
                AddInt(cfg.name + ".m_minionLimitNumberByFloor", cfg.m_minionLimitNumberByFloor);
                AddInt(cfg.name + ".m_minionLimitNumberByFloorSwitch", cfg.m_minionLimitNumberByFloorSwitch);
                var d = cfg.m_distanceByFloor;
                if (d != null)
                {
                    var t = new Table<float> { Name = cfg.name + ".m_distanceByFloor", D = d };
                    for (int k = 4; k <= 7; k++) if (d.ContainsKey(k)) { t.Had.Add(k); t.Orig[k] = d[k]; }
                    FloatTables.Add(t);
                }
            }
            foreach (var cfg in All<UIGameConfig>())
                if (First(cfg) && cfg.FloorDescriptions != null) Descriptions.Add((cfg.FloorDescriptions, 0));
            foreach (var cfg in All<FloorTreeComponentConfig>()) if (First(cfg)) Trees.Add((cfg, cfg.m_floorNumber));
            foreach (var cfg in All<UnlockFloorConditionConfig>()) if (First(cfg)) Conditions.Add((cfg, cfg.m_nbFloor));
            foreach (var cfg in All<BuilderConfig>())
            {
                if (!First(cfg)) continue;
                WallOwners.Add(cfg);
                WallPartsOfEnumArray(cfg.Pointer, "TopWalls");
                WallParts(SafeRead(cfg.Pointer, "TopPatch"));
            }
            foreach (var cfg in All<RoomConfig>())
            {
                if (!First(cfg)) continue;
                WallOwners.Add(cfg);
                WallPartsOfEnumArray(cfg.Pointer, "CornerWalls");
                WallPartsOfEnumArray(cfg.Pointer, "SideWalls");
            }
        }

        private static IntPtr SafeRead(IntPtr obj, string field)
        {
            try { return Il2CppRaw.ReadPointer(obj, field); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] reading {field} of {Il2CppRaw.ClassName(obj)}: {e.Message}"); return IntPtr.Zero; }
        }

        private static void WallPartsOfEnumArray(IntPtr owner, string field)
        {
            IntPtr enumArray = SafeRead(owner, field);
            if (enumArray == IntPtr.Zero) return;
            IntPtr array = SafeRead(enumArray, "m_internalArray");
            long n = Il2CppRaw.ArrayLength(array);
            for (long i = 0; i < n; i++) WallParts(*(IntPtr*)(Il2CppRaw.ArrayData(array) + (int)i * IntPtr.Size));
        }

        // Notes every item of a WallPartConfig<T> whose floor range ends at 6.
        private static void WallParts(IntPtr part)
        {
            if (part == IntPtr.Zero) return;
            IntPtr items = SafeRead(part, "Items");
            long n = Il2CppRaw.ArrayLength(items);
            for (long i = 0; i < n; i++)
            {
                IntPtr item = *(IntPtr*)(Il2CppRaw.ArrayData(items) + (int)i * IntPtr.Size);
                if (item == IntPtr.Zero || !WallItemsSeen.Add(item)) continue;
                int* range = (int*)(item + Il2CppRaw.FieldOffset(item, "Range"));
                if (range[1] == 6) WallRangeEnds.Add((IntPtr)(range + 1));
            }
        }

        private static void AddInt(string name, Aube.SerializableDictionary<int, int> d)
        {
            if (d == null) return;
            var t = new Table<int> { Name = name, D = d };
            for (int k = 4; k <= 7; k++) if (d.ContainsKey(k)) { t.Had.Add(k); t.Orig[k] = d[k]; }
            IntTables.Add(t);
        }

        private static void Shift<T>(Table<T> t, int n)
        {
            if (t.ShiftedBy == n || !t.Had.Contains(4) || !t.Had.Contains(5) || !t.Had.Contains(6)) return;
            Unshift(t);
            t.D[6 + n] = t.Orig[6];
            t.D[5 + n] = t.Orig[5];
            for (int f = 5; f <= 4 + n; f++) t.D[f] = t.Orig[4];
            t.ShiftedBy = n;
        }

        private static void Unshift<T>(Table<T> t)
        {
            if (t.ShiftedBy == 0) return;
            for (int k = 5; k <= 6 + t.ShiftedBy; k++)
            {
                if (t.Had.Contains(k)) t.D[k] = t.Orig[k];
                else if (t.D.ContainsKey(k)) t.D.Remove(k);
            }
            t.ShiftedBy = 0;
        }

        private static void RemoveDescriptions(int i)
        {
            var (d, added) = Descriptions[i];
            if (added == 0) return;
            try { for (int k = 7; k <= 6 + added; k++) if (d.ContainsKey(k)) d.Remove(k); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] floor descriptions above 6 not removed: {e.Message}"); }
            Descriptions[i] = (d, 0);
        }

        // Extends whatever has been found so far for n inserted floors. Returns a summary of what is extended in total.
        internal static string Apply(int n)
        {
            _active = true;
            _n = n;
            Capture();
            foreach (var t in IntTables) Shift(t, n);
            foreach (var t in FloatTables) Shift(t, n);
            for (int i = 0; i < Descriptions.Count; i++)
            {
                if (Descriptions[i].Added == n) continue;
                RemoveDescriptions(i);
                var d = Descriptions[i].D;
                try
                {
                    if (d.ContainsKey(6)) { for (int k = 7; k <= 6 + n; k++) d[k] = d[6]; Descriptions[i] = (d, n); }
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] floor descriptions above 6 not added: {e.Message}"); }
            }
            int trees = 0, conditions = 0;
            foreach (var (cfg, orig) in Trees) if (orig == 5 || orig == 6) { if (cfg.m_floorNumber != orig + n) cfg.m_floorNumber = orig + n; trees++; }
            foreach (var (cfg, orig) in Conditions) if (orig == 5 || orig == 6) { if (cfg.m_nbFloor != orig + n) cfg.m_nbFloor = orig + n; conditions++; }
            foreach (IntPtr end in WallRangeEnds) *(int*)end = 6 + n;
            _wallsEnd = 6 + n;
            int shifted = 0;
            foreach (var t in IntTables) if (t.ShiftedBy == n) shifted++;
            foreach (var t in FloatTables) if (t.ShiftedBy == n) shifted++;
            int descriptions = 0;
            foreach (var (_, added) in Descriptions) if (added == n) descriptions++;
            return $"{shifted} per-floor table(s) extended, {descriptions} floor description table(s) extended, {trees} unlock node(s) and {conditions} unlock condition(s) renumbered, {WallRangeEnds.Count} wall piece range(s) extended to floor {6 + n}";
        }

        internal static void Restore()
        {
            Capture(); // the game's values, read before any change
            if (!_active) return;
            foreach (var t in IntTables) Unshift(t);
            foreach (var t in FloatTables) Unshift(t);
            for (int i = 0; i < Descriptions.Count; i++) RemoveDescriptions(i);
            foreach (var (cfg, orig) in Trees) cfg.m_floorNumber = orig;
            foreach (var (cfg, orig) in Conditions) cfg.m_nbFloor = orig;
            if (_wallsEnd != 6) foreach (IntPtr end in WallRangeEnds) *(int*)end = 6;
            _wallsEnd = 6;
            _active = false;
            _n = 0;
            Plugin.Logger.LogInfo("[Floors] the game's per-floor tables put back");
        }
    }
}
