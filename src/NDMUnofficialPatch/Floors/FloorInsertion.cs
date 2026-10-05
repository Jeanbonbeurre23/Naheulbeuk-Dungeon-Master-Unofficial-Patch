using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Floors
{
    // Floors inserted between floor 4 and the tavern floor (docs/features/floor-insertion.md), setting
    // Floors.InsertedFloors (copies of floor 4, 5 by default, 0 = off).
    //
    // The game finds its floors in its scenes when a game starts (DungeonGameMode.OnSceneLoaded collects every FloorGrid
    // and FloorRoot into two dictionaries keyed by floor number) and derives everything else from them. Before that
    // collection, the patch copies floor 4's object in every scene N times, 20 units apart, as floors 5 to 4+N, after
    // removing the Golbargh's spawner, lair and lair sound from each copy, and raises floors 5 and 6 by N x 20 units as
    // floors 5+N and 6+N. Most fixed meshes were merged at build time into combined meshes (static batching), which draw
    // where they were baked whatever their object's position; those renderers get a root transform placed as many units
    // up (Renderer.staticBatchRootTransform), which the 0.21.0 probe showed to move them.
    //
    // The tables keyed by floor are extended (FloorTables) and put back for every game played without inserted floors.
    // The inserted floors follow floor 4: they are locked while floor 4 is locked, their buttons show floor 4's lock,
    // and unlocking floor 4 unlocks them through the game's own FloorUtility.UnlockFloor, each on an unlock entity of
    // its own created then.
    //
    // A save made with inserted floors gets a marker, NDMUnofficialPatch-data\<save>.floors.txt, giving their number.
    // Such a save always loads with that number of inserted floors, whatever the setting, since its entities and floor
    // manager count them; a marker written by plugin 0.22 has no number and means one. A seven-floor save converted by
    // tools/convert_save_floors.py has a marker that also asks for the new floors to be built on its first load; every
    // load builds the inserted floors that exist only as floor data (FloorConversion).
    internal static unsafe class FloorInsertion
    {
        internal const int Copied = 4;
        internal const int FirstInserted = 5;
        internal const float Height = 20f;
        internal const int MaxInserted = 10;
        private const string RootName = "NDMUP_RaisedFloorsRoot";

        // Whether the game being played has inserted floors, and how many.
        internal static bool Active { get; private set; }
        internal static int Count { get; private set; }
        internal static int LastInserted => Copied + Count;
        internal static bool IsInserted(int floor) => Active && floor >= FirstInserted && floor <= LastInserted;
        // The game's floor number a floor of this game stands for: an inserted floor stands for floor 4, the floors
        // above for 5 and 6.
        internal static int Original(int floor) => !Active || floor <= Copied ? floor : floor <= LastInserted ? Copied : floor - Count;

        private enum Pending { None, NewGame, Load }
        private static Pending _pending;
        private static DateTime _pendingTime;
        private static string _lastReadMarker;
        private static DateTime _lastReadTime;

        private static readonly Dictionary<int, GameObject> Roots = new(); // by number of floors raised
        private static Transform _rootParent;
        private static readonly HashSet<IntPtr> Processed = new();
        private static float _nextLatePass;
        private static float _lateUntil;

        // ---- Which games get the floor ---------------------------------------------------------------------------

        internal static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Artefacts Studio", "NDM", "NDMUnofficialPatch-data");

        // The hooks are installed when the switch is on or when some save already carries the marker.
        internal static bool Needed
        {
            get
            {
                if (Settings.InsertedFloors.Value > 0) return true;
                try { return Directory.Exists(DataFolder) && Directory.GetFiles(DataFolder, "*.floors.txt").Length > 0; }
                catch (Exception) { return true; }
            }
        }

        private static string MarkerPath(string savePath)
        {
            string saveDir = Path.GetDirectoryName(savePath);
            string root = Path.GetDirectoryName(saveDir) ?? saveDir;
            return Path.Combine(root, "NDMUnofficialPatch-data", Path.GetFileNameWithoutExtension(savePath) + ".floors.txt");
        }

        private static bool IsProfile(string name) => name != null && name.StartsWith("Player_Profile", StringComparison.OrdinalIgnoreCase);

        internal static void OnNewGame(string how)
        {
            _pending = Pending.NewGame;
            _pendingTime = DateTime.Now;
            int n = Math.Clamp(Settings.InsertedFloors.Value, 0, MaxInserted);
            Plugin.Logger.LogInfo($"[Floors] new game started ({how}); " + (n > 0 ? $"{n} cop(ies) of floor 4 will be inserted" : "no floor inserted"));
        }

        internal static void OnLoadGame(string name)
        {
            _pending = Pending.Load;
            _pendingTime = DateTime.Now;
        }

        // The main menu reads every save to list it, so only the last read before the scenes load counts.
        internal static void OnSaveRead(string filename, string savePath)
        {
            if (IsProfile(filename) || string.IsNullOrEmpty(savePath)) return;
            _lastReadMarker = MarkerPath(savePath);
            _lastReadTime = DateTime.Now;
        }

        internal static void OnSaveWrite(string filename, string savePath)
        {
            if (IsProfile(filename) || string.IsNullOrEmpty(savePath)) return;
            try
            {
                string path = MarkerPath(savePath);
                if (Active)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllLines(path, new[]
                    {
                        $"# NDM Unofficial Patch: this save was made with {Count} cop(ies) of floor 4 inserted as floors {FirstInserted} to {LastInserted} ({7 + Count} floors).",
                        "# It loads with them whatever Floors.InsertedFloors says. Delete this file only together with the save.",
                        $"inserted {Count}",
                        $"plugin {Plugin.PluginVersion}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    });
                    Plugin.Logger.LogInfo($"[Floors] save marked as made with {Count} inserted floor(s): {path}");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    Plugin.Logger.LogInfo($"[Floors] save written without inserted floors, marker removed: {path}");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] writing the save marker failed: {e.Message}"); }
        }

        // The number of inserted floors a marker gives; a marker of plugin 0.22 has no number and means one. A marker
        // written by the save conversion (tools/convert_save_floors.py) also has a line "build <first> <last>": the
        // floors to build on the first load (FloorConversion); buildFirst is 0 without it.
        internal static int ReadMarker(string path, out int buildFirst, out int buildLast)
        {
            int count = 1;
            buildFirst = buildLast = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("inserted ", StringComparison.Ordinal))
                {
                    if (int.TryParse(line.Substring(9).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) count = Math.Min(n, MaxInserted);
                }
                else if (line.StartsWith("build ", StringComparison.Ordinal))
                {
                    var parts = line.Substring(6).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2
                        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a)
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                    {
                        buildFirst = a;
                        buildLast = b;
                    }
                }
            }
            return count;
        }

        // Called before DungeonGameMode.OnSceneLoaded collects the floors.
        internal static void OnSceneLoaded()
        {
            bool recent = (DateTime.Now - _pendingTime).TotalMinutes < 10;
            int n;
            int buildFirst = 0, buildLast = 0;
            bool load = false;
            string why;
            if (_pending == Pending.NewGame && recent)
            {
                n = Math.Clamp(Settings.InsertedFloors.Value, 0, MaxInserted);
                why = $"new game, Floors.InsertedFloors = {Settings.InsertedFloors.Value}";
            }
            else if (_pending == Pending.Load && recent)
            {
                bool marked = _lastReadMarker != null && (DateTime.Now - _lastReadTime).TotalMinutes < 10 && File.Exists(_lastReadMarker);
                n = marked ? ReadMarker(_lastReadMarker, out buildFirst, out buildLast) : 0;
                load = true;
                why = marked ? $"loaded save carries the marker {_lastReadMarker}" : "loaded save has no marker";
            }
            else
            {
                n = 0;
                why = "no new game or load noticed before the scenes loaded";
            }
            FloorConversion.Disarm();
            _pending = Pending.None;
            Active = false;
            Count = 0;
            Processed.Clear();
            FloorButtonInjectPatch.Reset();
            Roots.Clear();
            _rootParent = null;

            if (n <= 0)
            {
                FloorTables.Restore();
                Plugin.Logger.LogInfo($"[Floors] seven floors ({why})");
                return;
            }
            Count = n;
            try
            {
                var report = new StringBuilder();
                ApplyScenes(report);
                Active = true;
                _treesChecked = false;
                _tablesLogged = null;
                report.Append(" tables so far: ").Append(FloorTables.Apply(Count)).Append(';');
                _lateUntil = Time.realtimeSinceStartup + 120f;
                _nextLatePass = Time.realtimeSinceStartup + 2f;
                Plugin.Logger.LogInfo($"[Floors] floor 4 copied as floors {FirstInserted} to {LastInserted}, floors 5 and 6 raised as {5 + Count} and {6 + Count} ({why}).{report}");
                // Every load is checked for inserted floors that exist only as floor data (FloorConversion).
                if (load)
                {
                    if (buildFirst > 0 && (buildFirst < FirstInserted || buildLast > LastInserted || buildFirst > buildLast))
                        Plugin.Logger.LogWarning($"[Floors] the marker asks to build floors {buildFirst} to {buildLast}, outside the inserted floors {FirstInserted} to {LastInserted}; the inserted floors are checked instead");
                    FloorConversion.Arm(FirstInserted, LastInserted, buildFirst > 0);
                }
            }
            catch (Exception e)
            {
                Active = true; // the scenes may already be changed; the rest of the floor rules must follow them
                Plugin.Logger.LogError($"[Floors] inserting the floors failed part way: {e}");
            }
        }

        // ---- Scenes ----------------------------------------------------------------------------------------------

        private struct FloorObject
        {
            public GameObject Go;
            public int Floor;
        }

        // Objects of floors 4 to 6 not processed yet: grid objects (FloorGrid and FloorRoot, World), decoration
        // (EnvironmentVisualManagement, World_HD) and roots (FloorRoot, World_Audio and the level scenes).
        private static List<FloorObject> FindFloorObjects()
        {
            var seen = new HashSet<IntPtr>();
            var result = new List<FloorObject>();
            void Note(Component c, int floor)
            {
                if (floor < Copied || floor > 6) return;
                var go = c.gameObject;
                if (Processed.Contains(go.Pointer) || !seen.Add(go.Pointer)) return;
                result.Add(new FloorObject { Go = go, Floor = floor });
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FloorGrid>())) { var g = o.Cast<FloorGrid>(); Note(g, g.m_floor); }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FloorRoot>())) { var r = o.Cast<FloorRoot>(); Note(r, r.m_floor); }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<EnvironmentVisualManagement>()))
            {
                var e = o.Cast<EnvironmentVisualManagement>();
                if (e.gameObject.name.StartsWith("Floor", StringComparison.Ordinal)) Note(e, e.m_floorIndex);
            }
            return result;
        }

        // The root of the merged renderers raised by the given number of floors.
        private static Transform RootFor(int steps)
        {
            if (Roots.TryGetValue(steps, out var go) && go != null) return go.transform;
            go = new GameObject($"{RootName}_{steps}");
            if (_rootParent != null) go.transform.SetParent(_rootParent, false);
            go.transform.position = new Vector3(0f, steps * Height, 0f);
            go.transform.rotation = Quaternion.identity;
            Roots[steps] = go;
            return go.transform;
        }

        private static void ApplyScenes(StringBuilder report)
        {
            var objects = FindFloorObjects();
            GameObject anyGrid = null;
            foreach (var f in objects) if (f.Go.GetComponent<FloorGrid>() != null) { anyGrid = f.Go; break; }
            if (anyGrid == null) throw new InvalidOperationException("no FloorGrid object of floors 4 to 6 found in the loaded scenes");
            _rootParent = anyGrid.transform.parent;
            Process(objects, report);
            ShiftLayerPresets(report);
        }

        private static void Process(List<FloorObject> objects, StringBuilder report)
        {
            // Floor 6 first, then 5, then the copies of 4, so that each number is free when it is given.
            objects.Sort((a, b) => b.Floor.CompareTo(a.Floor));
            foreach (var f in objects)
            {
                string scene = f.Go.scene.name;
                if (f.Floor == Copied)
                {
                    Processed.Add(f.Go.Pointer);
                    int merged = 0;
                    string removed = "";
                    var names = new List<string>();
                    var previous = f.Go;
                    for (int k = 1; k <= Count; k++)
                    {
                        var clone = Object.Instantiate(f.Go, f.Go.transform.parent, true).Cast<GameObject>();
                        clone.transform.SetSiblingIndex(previous.transform.GetSiblingIndex() + 1);
                        string r = RemoveGolbargh(clone);
                        if (k == 1) removed = r;
                        merged += Raise(clone, Copied + k, k);
                        clone.name = Rename(f.Go.name, Copied, Copied + k);
                        Processed.Add(clone.Pointer);
                        names.Add(clone.name);
                        previous = clone;
                    }
                    report.Append($" {scene}/{f.Go.name}: copied as {string.Join(", ", names)}, {merged} merged renderer(s) rooted" + (removed.Length > 0 ? $", removed from each copy: {removed}" : "") + ";");
                }
                else
                {
                    string before = f.Go.name;
                    int merged = Raise(f.Go, f.Floor + Count, Count);
                    f.Go.name = Rename(before, f.Floor, f.Floor + Count);
                    Processed.Add(f.Go.Pointer);
                    report.Append($" {scene}/{before}: raised as {f.Go.name}, {merged} merged renderer(s) rooted;");
                }
            }
        }

        private static string Rename(string name, int from, int to)
        {
            string a = "Floor" + from.ToString(CultureInfo.InvariantCulture);
            return name.StartsWith(a, StringComparison.Ordinal) ? "Floor" + to.ToString(CultureInfo.InvariantCulture) + name.Substring(a.Length) : name + " (" + to + ")";
        }

        // Moves an object up by the given number of floors, gives it its new floor number and roots its merged renderers.
        private static int Raise(GameObject go, int floor, int steps)
        {
            float dy = steps * Height;
            go.transform.position += new Vector3(0f, dy, 0f);
            var grid = go.GetComponent<FloorGrid>();
            if (grid != null)
            {
                grid.m_floor = floor;
                var origin = grid.m_origin;
                origin.y += dy;
                grid.m_origin = origin;
            }
            var root = go.GetComponent<FloorRoot>();
            if (root != null) root.m_floor = floor;
            var env = go.GetComponent<EnvironmentVisualManagement>();
            if (env != null) env.m_floorIndex = floor;
            return RootMerged(go, RootFor(steps));
        }

        // A renderer merged at build time draws a piece of a mesh named "Combined Mesh (root: scene) N".
        private static int RootMerged(GameObject go, Transform root)
        {
            int n = 0;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter == null ? null : filter.sharedMesh;
                if (mesh == null || !mesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal)) continue;
                r.staticBatchRootTransform = root;
                if (r.enabled)
                {
                    r.enabled = false;
                    r.enabled = true;
                }
                n++;
            }
            return n;
        }

        // The Golbargh's spawner (level scenes), lair (Campaign) and lair sound (World_Audio) stay on floor 4 only.
        private static string RemoveGolbargh(GameObject clone)
        {
            var found = new List<GameObject>();
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject.Pointer == clone.Pointer) continue;
                if (t.name.IndexOf("Golbargh", StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool inside = false;
                foreach (var g in found) if (t.IsChildOf(g.transform)) { inside = true; break; }
                if (!inside) found.Add(t.gameObject);
            }
            var names = new List<string>();
            foreach (var g in found)
            {
                names.Add(g.name);
                Object.DestroyImmediate(g);
            }
            return string.Join(", ", names);
        }

        // The level scenes' floor layer prefabs (FloorLayerPreset.LayersByFloors, keys 0 to 6).
        private static void ShiftLayerPresets(StringBuilder report)
        {
            int shifted = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FloorLayerManager>()))
            {
                var manager = o.Cast<FloorLayerManager>();
                var presets = manager.m_floorLayerPresets;
                if (presets == null) continue;
                for (int i = 0; i < presets.Count; i++)
                {
                    var d = presets[i].LayersByFloors;
                    if (d == null || d.ContainsKey(7) || !d.ContainsKey(Copied)) continue;
                    GameObject v4 = d[Copied], v5 = d.ContainsKey(5) ? d[5] : null, v6 = d.ContainsKey(6) ? d[6] : null;
                    if (v6 != null) d[6 + Count] = v6;
                    if (v5 != null) d[5 + Count] = v5;
                    for (int f = FirstInserted; f <= LastInserted; f++) d[f] = v4;
                    shifted++;
                }
            }
            report.Append($" {shifted} floor layer table(s) shifted;");
        }

        // Called before the game's systems initialise (EcsSystems.Init): the world's configs are loaded by then, and
        // the unlock nodes are made into entities after it.
        internal static void BeforeSystemsInit()
        {
            if (!Active) return;
            string summary = FloorTables.Apply(Count);
            if (summary == _tablesLogged) return;
            _tablesLogged = summary;
            Plugin.Logger.LogInfo($"[Floors] before the systems start: {summary}");
        }

        // A scene that loads after the floors were collected (World_HD or World_Audio, if ever) is processed when it
        // appears, and tables found late are extended, for two minutes after the game starts. The unlock entities of
        // floors 5 and 6, if made before their configs were renumbered, are renumbered once.
        internal static void Update()
        {
            if (!Active) return;
            FloorConversion.Update();
            float now = Time.realtimeSinceStartup;
            if (now > _lateUntil || now < _nextLatePass) return;
            _nextLatePass = now + 2f;
            BeforeSystemsInit();
            if (!_treesChecked) CheckTreeEntities();
            if (_rootParent == null) return;
            var late = FindFloorObjects();
            if (late.Count == 0) return;
            var report = new StringBuilder();
            foreach (var f in late)
                if (f.Go.GetComponent<FloorGrid>() != null)
                    report.Append($" warning: {f.Go.scene.name}/{f.Go.name} is a floor grid found after the floors were collected;");
            Process(late, report);
            Plugin.Logger.LogInfo($"[Floors] scene loaded late, processed:{report}");
        }

        private static bool _treesChecked;
        private static string _tablesLogged;

        // The game has one unlock entity per floor 1 to 6. With a number above 6 they are already renumbered; numbered
        // 1 to 6 they were made from the configs before the renumbering, and 6 and 5 become 6+N and 5+N.
        private static void CheckTreeEntities()
        {
            if (!Management.GameContext.TryWorld(out var world, out _)) return;
            var pool = RawPool.Of<FloorTreeComponent>(world, -1);
            if (pool.IsNull) return;
            int numberAt = Il2CppRaw.ValueFieldOffset<FloorTreeComponent>("FloorNumber");
            var entities = pool.Entities();
            if (entities.Count == 0) return;
            _treesChecked = true;
            bool has6 = false, above = false;
            foreach (int e in entities)
            {
                int n = *(int*)(pool.Item(e) + numberAt);
                if (n == 6) has6 = true;
                if (n > 6) above = true;
            }
            if (above || !has6)
            {
                Plugin.Logger.LogInfo($"[Floors] {entities.Count} unlock entit(ies) of floors found, numbering already right");
                return;
            }
            int changed = 0;
            foreach (int e in entities)
            {
                int* n = (int*)(pool.Item(e) + numberAt);
                if (*n == 6 || *n == 5) { *n += Count; changed++; }
            }
            Plugin.Logger.LogInfo($"[Floors] unlock entities of floors 5 and 6 renumbered {5 + Count} and {6 + Count} ({changed} entit(ies))");
        }

        // ---- Lock of the inserted floors -------------------------------------------------------------------------

        private static bool _unlocking;

        // FloorUtility.UnlockFloor(tree entity) has just run. When the entity is floor 4's, the inserted floors are
        // unlocked with it, each on an unlock entity of its own carrying only a FloorTreeComponent with its number.
        internal static void AfterUnlock(FloorUtility utility, int treeEntity)
        {
            if (!Active || _unlocking) return;
            var world = new EcsWorld(Il2CppRaw.ReadObject(utility.Pointer, "m_world", "EcsWorld"));
            var pool = RawPool.Of<FloorTreeComponent>(world, -1);
            int numberAt = Il2CppRaw.ValueFieldOffset<FloorTreeComponent>("FloorNumber");
            IntPtr item = pool.Item(treeEntity);
            if (item == IntPtr.Zero || *(int*)(item + numberAt) != Copied) return;
            var done = new List<string>();
            for (int floor = FirstInserted; floor <= LastInserted; floor++)
                done.Add(UnlockInserted(utility, world, floor));
            Plugin.Logger.LogInfo($"[Floors] floor 4 unlocked, inserted floors unlocked with it: {string.Join(", ", done)}");
        }

        internal static string UnlockInserted(FloorUtility utility, EcsWorld world, int floor)
        {
            var pool = RawPool.Of<FloorTreeComponent>(world, -1);
            int numberAt = Il2CppRaw.ValueFieldOffset<FloorTreeComponent>("FloorNumber");
            int unlockAt = Il2CppRaw.ValueFieldOffset<FloorTreeComponent>("HasBeenUnlock");
            int own = -1;
            foreach (int e in pool.Entities())
            {
                IntPtr it = pool.Item(e);
                if (it != IntPtr.Zero && *(int*)(it + numberAt) == floor) { own = e; break; }
            }
            bool created = false;
            if (own < 0)
            {
                own = world.NewEntity();
                var component = new FloorTreeComponent { FloorNumber = floor, HasBeenUnlock = true };
                world.GetPoolByType(Il2CppType.Of<FloorTreeComponent>()).AddRaw(own, component.BoxIl2CppObject());
                created = true;
            }
            IntPtr mine = pool.Item(own);
            if (mine != IntPtr.Zero) *(byte*)(mine + unlockAt) = 1;
            _unlocking = true;
            try { utility.UnlockFloor(own); }
            finally { _unlocking = false; }
            return $"floor {floor} (unlock entity {own}" + (created ? ", created)" : ")");
        }
    }
}
