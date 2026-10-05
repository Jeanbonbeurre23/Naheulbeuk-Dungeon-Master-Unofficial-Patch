using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Management
{
    // What the room-needs panel shows, computed from the game's own rules (game 1.8, read from the method bodies and
    // the behaviour trees in the game's data).
    //
    // Free props. A need behaviour looks for a prop of one kind in rooms of one type (FindIoInRoomTask: RoomToSearchFor
    // and PropType); several trees try a second or third kind when the first is not found (the break room: a pet,
    // photo booth or snack shelf, a rest desk, a card table; training: a melee dummy, then a ranged target). A room is
    // usable when one of its props is of that kind, carries no worker task and is free (FindIoInRoomTask.IsRoomValid:
    // IoUtility.IsIOAvailable and a free place). Dirtiness only sets which rooms are tried first
    // (AFindRoomTask.HasOneRoomMatchingDirtiness); it never makes a room unusable. So a search that finds nothing means
    // that no room of the type had a free, finished prop of that kind.
    //
    // A postfix on FindValidRooms records, for every player minion's FindIoInRoomTask, the room type, the kind of prop,
    // the room of the prop found (none if none) and the time. For each room type the panel gets, over the last 3 minutes
    // of real time: per kind of prop, how many minions looked for one and how many found one, and the minions whose
    // last search for that kind found none; and the minions whose most recent search in that room type, whatever the
    // kind, found none.
    //
    // Prestige. A minion's required prestige level is ReputationConfig.GetSatisfaction(origin, grade) / 10; a room's
    // level is ReputationConfig.GetRoomPrestigeLevel(room type, RoomPrestigeComponent.Value). Prestige narrows only the
    // cook's and the training searches (CheckRoomPrestige), and when no room meets the level the search takes any usable
    // room (AFindRoomTask.FindValidRooms). The panel shows it as information and does not count it as a problem.
    internal static unsafe class RoomNeeds
    {
        internal const float Window = 180f;

        // Room types always shown, before the others that minions search for.
        internal static readonly RoomType[] NeedTypes = { RoomType.DORMITORY, RoomType.CANTEEN, RoomType.BATHROOM, RoomType.BREAK_ROOM };

        private struct Search
        {
            public float Time;
            public int Room;
            public bool Found;
        }

        private static readonly Dictionary<(int Minion, RoomType Type, PropType Prop), Search> Searches = new();
        private static readonly Dictionary<RoomType, bool> PrestigeCheckedByType = new();
        private static IntPtr _world;
        private static bool _errorLogged;

        internal sealed class Kind
        {
            public PropType Prop;
            public int Searched;
            public int Found;
            public readonly List<int> Without = new(); // most recent first
        }

        internal sealed class Row
        {
            public RoomType Type;
            public int Rooms;
            public readonly List<Kind> Kinds = new(); // most minions without first
            public readonly List<int> Unmet = new();  // minions whose most recent search in this type found nothing
            public readonly Dictionary<int, string> MinionNames = new();
            public int PrestigeMinion = -1;
            public string PrestigeMinionName;
            public int PrestigeRequired;
            public int PrestigeGot = -1;
            public int BestRoomLevel = -1;
            public int LevelCount;
            public bool PrestigeCheckedBySearch;
            public bool PrestigeObserved;

            public bool Problem => Unmet.Count > 0;
            public bool PrestigeShort => PrestigeMinion >= 0;

            public string KindsText()
            {
                if (Kinds.Count == 0) return "no search in the last 3 minutes";
                return string.Join(", ", Kinds.Select(k => $"{PropName(k.Prop)} found {k.Found}/{k.Searched}" +
                    (k.Without.Count > 0 ? " (without: " + string.Join(", ", k.Without.Take(4).Select(Name)) + (k.Without.Count > 4 ? ", ..." : "") + ")" : "")));
            }

            public string Name(int minion) => MinionNames.TryGetValue(minion, out var n) ? n : $"minion {minion}";

            // One line for the log.
            public override string ToString()
            {
                string prestige = PrestigeShort
                    ? $"prestige: {PrestigeMinionName} would prefer {PrestigeRequired}, got {PrestigeGot}, best room {BestRoomLevel} of {LevelCount} levels"
                    : $"prestige met, best room {BestRoomLevel} of {LevelCount} levels";
                return $"{Names.Room(Type)} ({Rooms} room(s)): {KindsText()}; {Unmet.Count} minion(s) whose last search found nothing; {prestige}";
            }
        }

        internal static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[RoomNeeds] {what} failed, further errors are not logged: {e.Message}");
        }

        // A short name for the kinds of prop the need behaviours look for, the enum's name otherwise.
        internal static string PropName(PropType prop) => prop switch
        {
            PropType.DOOR => "door or desk",
            PropType.DORMITORY_BED => "bed",
            PropType.CANTEEN_TABLE => "table",
            PropType.BATHROOM_SHOWER => "shower",
            PropType.BATHROOM_TOILET => "toilet",
            PropType.BREAK_ROOM_TABLE_ENTERTAINMENT => "card table",
            PropType.BREAK_ROOM_DESK_ENTERTAINMENT => "rest desk",
            PropType.BREAK_ROOM_ENTERTAINMENT => "pet, photo or snacks",
            PropType.TRAINING_ROOM_MELEE_DUMMY => "melee dummy",
            PropType.TRAINING_ROOM_PROJECTILE_TARGET => "ranged target",
            PropType.TAVERN_ENTERTAINMENT => "pet or photo",
            PropType.TAVERN_ENTERTAINMENT_TABLE => "card table",
            PropType.TAVERN_TABLE => "table",
            PropType.KITCHEN_COOKER => "cooker",
            PropType.PRISON_CELL => "cell",
            PropType.LIBRARY_MAGIC_DUMMY => "magic dummy",
            PropType.TREASURE_ROOM_RECEPTION_DESK => "reception desk",
            _ => prop.ToString().ToLowerInvariant().Replace('_', ' '),
        };

        // ---- Searches ----

        internal static void OnSearch(AFindRoomTask task, RoomType type)
        {
            try
            {
                if (type == RoomType.CORRIDOR) return;
                var io = task.TryCast<FindIoInRoomTask>();
                if (io == null) return;
                if (!GameContext.TryWorld(out var world, out _)) return;
                CheckWorld();
                int minion = task.m_entity;
                if (!RawPool.Of<MinionTag>(world, -1).Has(minion)) return;
                int prop = io.m_foundPropEntity;
                Searches[(minion, type, io.PropType)] = new Search
                {
                    Time = Time.unscaledTime,
                    Found = prop >= 0,
                    Room = prop >= 0 ? GameContext.RoomOfProp(world, prop) : -1,
                };
                bool prestige = task.CheckRoomPrestige;
                PrestigeCheckedByType[type] = PrestigeCheckedByType.TryGetValue(type, out bool seen) ? seen || prestige : prestige;
            }
            catch (Exception e) { ReportError("recording a room search", e); }
        }

        // The room searches a minion made in the last `within` seconds of real time, newest first: room type, kind of
        // prop, outcome and how long ago. Used by the morale watch (Diagnostics/MoraleWatch.cs).
        internal static string RecentSearches(int minion, float within)
        {
            float now = Time.unscaledTime;
            var list = new List<(RoomType Type, PropType Prop, Search S)>();
            foreach (var p in Searches)
                if (p.Key.Minion == minion && now - p.Value.Time <= within) list.Add((p.Key.Type, p.Key.Prop, p.Value));
            if (list.Count == 0) return null;
            list.Sort((a, b) => b.S.Time.CompareTo(a.S.Time));
            return string.Join("; ", list.Select(x => $"{Names.Room(x.Type)} {PropName(x.Prop)} {(x.S.Found ? "found" : "none free")} {(int)(now - x.S.Time)} s ago"));
        }

        private static void CheckWorld()
        {
            IntPtr world = GameContext.WorldPointer;
            if (world == _world) return;
            _world = world;
            Searches.Clear();
            PrestigeCheckedByType.Clear();
        }

        // ---- Computing the rows ----

        private struct RoomData
        {
            public RoomType Type;
            public int Level;
        }

        internal static List<Row> Compute()
        {
            var rows = new List<Row>();
            if (!GameContext.Ready || !GameContext.TryWorld(out var world, out int size)) return rows;
            CheckWorld();
            float now = Time.unscaledTime;

            Reputation.ReputationConfig reputation = null;
            var reputationPool = RawPool.Of<Reputation.ReputationConfigComponent>(world, -1);
            IntPtr reputationItem = reputationPool.Item(reputationPool.FirstEntity());
            if (reputationItem != IntPtr.Zero)
            {
                IntPtr config = *(IntPtr*)(reputationItem + Il2CppRaw.ValueFieldOffset<Reputation.ReputationConfigComponent>("m_config"));
                if (config != IntPtr.Zero) reputation = new Reputation.ReputationConfig(config);
            }

            // Rooms.
            var rooms = new Dictionary<int, RoomData>();
            var filter = new EcsFilter(Il2CppRaw.ReadObject(GameContext.Rooms.Pointer, "m_roomFilter", "EcsFilter"));
            var types = RawPool.Of<RoomTypeComponent>(world, sizeof(RoomTypeComponent));
            var building = RawPool.Of<RoomInConstructionComponent>(world, -1);
            var prestige = RawPool.Of<Reputation.RoomPrestigeComponent>(world, sizeof(Reputation.RoomPrestigeComponent));
            int count = filter.GetEntitiesCount();
            var ids = filter.GetRawEntities();
            for (int i = 0; i < count; i++)
            {
                int e = ids[i];
                IntPtr t = types.Item(e);
                if (t == IntPtr.Zero || building.Has(e) || !world.IsEntityAlive(e, size)) continue;
                var r = new RoomData { Type = ((RoomTypeComponent*)t)->Value, Level = -1 };
                IntPtr p = prestige.Item(e);
                if (p != IntPtr.Zero && reputation != null)
                {
                    try { r.Level = reputation.GetRoomPrestigeLevel(r.Type, ((Reputation.RoomPrestigeComponent*)p)->Value); }
                    catch (Exception ex) { ReportError($"reading the prestige level of a {r.Type} room", ex); }
                }
                rooms[e] = r;
            }

            // Minions: name and required prestige.
            var names = new Dictionary<int, string>();
            var required = new Dictionary<int, int>();
            var originPool = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
            var gradePool = RawPool.Of<GradeComponent>(world, -1);
            int gradeOffset = Il2CppRaw.ValueFieldOffset<GradeComponent>("CurrentGrade");
            foreach (var m in GameContext.ListMinions())
            {
                names[m.Entity] = m.Name;
                IntPtr o = originPool.Item(m.Entity);
                IntPtr g = gradePool.Item(m.Entity);
                int need = 0;
                if (o != IntPtr.Zero && g != IntPtr.Zero && reputation != null)
                {
                    try { need = reputation.GetSatisfaction(((OriginComponent*)o)->Origin, (GradeType)(*(int*)(g + gradeOffset))) / 10; }
                    catch (Exception e) { ReportError("reading the prestige a minion requires", e); }
                }
                required[m.Entity] = need;
            }

            // Recent searches of living player minions.
            var recent = Searches.Where(p => now - p.Value.Time <= Window && names.ContainsKey(p.Key.Minion)).ToList();

            // Rows, in a fixed order for the needs, then the other room types searched.
            var order = new List<RoomType>(NeedTypes);
            foreach (var p in recent)
                if (!order.Contains(p.Key.Type)) order.Add(p.Key.Type);

            foreach (var type in order)
            {
                var row = new Row { Type = type };
                foreach (var pair in names) row.MinionNames[pair.Key] = pair.Value;
                foreach (var r in rooms.Values)
                {
                    if (r.Type != type) continue;
                    row.Rooms++;
                    if (r.Level > row.BestRoomLevel) row.BestRoomLevel = r.Level;
                }
                if (reputation != null)
                {
                    try { row.LevelCount = reputation.GetRoomPrestigeLevels(type)?.Length ?? 0; } catch { row.LevelCount = 0; }
                }
                row.PrestigeObserved = PrestigeCheckedByType.TryGetValue(type, out bool checkedPrestige);
                row.PrestigeCheckedBySearch = checkedPrestige;

                var ofType = recent.Where(p => p.Key.Type == type).ToList();
                foreach (var group in ofType.GroupBy(p => p.Key.Prop))
                {
                    var kind = new Kind { Prop = group.Key };
                    foreach (var p in group.OrderByDescending(p => p.Value.Time))
                    {
                        kind.Searched++;
                        if (p.Value.Found) kind.Found++;
                        else kind.Without.Add(p.Key.Minion);
                    }
                    row.Kinds.Add(kind);
                }
                row.Kinds.Sort((a, b) => a.Without.Count != b.Without.Count ? b.Without.Count.CompareTo(a.Without.Count) : b.Searched.CompareTo(a.Searched));

                // Each minion's most recent search in this room type, whatever the kind.
                var bestGap = int.MinValue;
                foreach (var byMinion in ofType.GroupBy(p => p.Key.Minion))
                {
                    var last = byMinion.OrderByDescending(p => p.Value.Time).First();
                    if (!last.Value.Found) row.Unmet.Add(byMinion.Key);

                    int need = required.TryGetValue(byMinion.Key, out int n) ? n : 0;
                    if (need <= 0) continue;
                    int got = -1;
                    var found = byMinion.Where(p => p.Value.Found).OrderByDescending(p => p.Value.Time).FirstOrDefault();
                    if (found.Value.Found && found.Value.Room >= 0 && rooms.TryGetValue(found.Value.Room, out var used)) got = used.Level;
                    else got = row.BestRoomLevel;
                    if (need <= got) continue;
                    int gap = need - got;
                    if (need > row.PrestigeRequired || (need == row.PrestigeRequired && gap > bestGap))
                    {
                        row.PrestigeRequired = need;
                        row.PrestigeMinion = byMinion.Key;
                        row.PrestigeMinionName = row.Name(byMinion.Key);
                        row.PrestigeGot = got;
                        bestGap = gap;
                    }
                }
                rows.Add(row);
            }
            return rows;
        }
    }

    // Runs after the character manager's own postfix on the same method, so that it records the prop finally chosen.
    [HarmonyPatch(typeof(AFindRoomTask), nameof(AFindRoomTask.FindValidRooms))]
    [HarmonyPriority(Priority.Low)]
    internal static class RoomNeedsSearchPatch
    {
        private static bool Prepare() => Settings.RoomNeedsPanel.Value;
        private static void Postfix(AFindRoomTask __instance, RoomType roomToSearchFor) => RoomNeeds.OnSearch(__instance, roomToSearchFor);
    }
}
