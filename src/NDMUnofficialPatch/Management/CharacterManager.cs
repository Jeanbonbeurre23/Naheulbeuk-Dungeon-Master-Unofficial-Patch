using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aube;
using HarmonyLib;
using UnityEngine;

namespace NDMUnofficialPatch.Management
{
    internal enum Slot { Job, Dormitory, Bathroom, Canteen, BreakRoom }

    internal struct RoomRef
    {
        public int Entity;
        public short Gen;
        public RoomType Type;
        public int Floor;
    }

    internal sealed class Assignment
    {
        public short Gen;
        public string Name;
        public readonly Dictionary<Slot, RoomRef> Rooms = new();
        public readonly Dictionary<Slot, string> LastNote = new();
        // The rooms forbidden to him, in the order they were forbidden, and when each was last taken out of a search.
        public readonly List<RoomRef> Forbidden = new();
        public readonly Dictionary<int, DateTime> LastSkipped = new();
        public bool IsEmpty => Rooms.Count == 0 && Forbidden.Count == 0;
    }

    // Which room each chosen character should use for each slot, and which rooms he may not use.
    //
    // An assignment is a preference: the game's own choice stands whenever the assigned room cannot serve (see
    // RoomSearchRedirect), and other characters keep using the room. A ban is a rule: the room is taken out of every
    // search he makes with FindIoInRoomTask (see ForbiddenRoomFilter), even when no other room of its type can serve,
    // and other characters keep using it. A room cannot be his assigned room and forbidden to him at the same time.
    // Both are kept per save in a file beside the Save folder, never inside the save.
    internal static class CharacterManager
    {
        internal static readonly Slot[] AllSlots = { Slot.Job, Slot.Dormitory, Slot.Bathroom, Slot.Canteen, Slot.BreakRoom };
        internal static readonly Dictionary<int, Assignment> ByMinion = new();
        internal static int LastClicked = -1;

        // The room types that some FindIoInRoomTask of the game's behaviour trees searches (field RoomToSearchFor, 0
        // when absent), read from the configs bundle of game 1.8 on 30 September 2026: TAVERN (BT_Drink, BT_EatInTavern,
        // BT_Entertainment_Tavern and others), REIVAX_OFFICE and ZANGDAR_LOCAL (branches of BT_Sleep, BT_Pee, BT_Shower
        // and BT_Entertainment_Protagonist), DORMITORY (BT_Sleep), KITCHEN (BT_Cook), CANTEEN (BT_EatInCanteen), BATHROOM
        // (BT_Pee, BT_Shower), TRAINING_ROOM (BT_Train), LIBRARY (BT_MagicTraining), TREASURE_ROOM (BT_PaySalary,
        // BT_GetSalary), BREAK_ROOM (BT_Entertainment_BreakRoom), PRISON (BT_Entertainment_Prison) and GOLBARGH_LAIR
        // (BT_Entertainment_Golbargh). No FindIoInRoomTask searches a workshop, a garden, an armory, a laboratory or a
        // corridor, so a ban on one of those would change nothing.
        internal static readonly HashSet<RoomType> ForbiddableTypes = new()
        {
            RoomType.TAVERN, RoomType.REIVAX_OFFICE, RoomType.ZANGDAR_LOCAL, RoomType.DORMITORY, RoomType.KITCHEN,
            RoomType.CANTEEN, RoomType.BATHROOM, RoomType.TRAINING_ROOM, RoomType.LIBRARY, RoomType.TREASURE_ROOM,
            RoomType.BREAK_ROOM, RoomType.PRISON, RoomType.GOLBARGH_LAIR,
        };

        // One entry per ban, (character << 32) | room, so the check made for each room of each search is one lookup.
        private static readonly HashSet<long> ForbiddenPairs = new();
        private static readonly Dictionary<long, float> SkipLogged = new();

        private static readonly HashSet<string> ErrorsLogged = new();
        private static string _pendingLoadPath;
        private static DateTime _pendingLoadTime;
        private static List<string[]> _pendingEntries;
        private static readonly Dictionary<string, float> NoteLogged = new();

        internal static string SlotName(Slot s) => s switch
        {
            Slot.Job => "Job room",
            Slot.Dormitory => "Dormitory",
            Slot.Bathroom => "Bathroom (toilets, showers)",
            Slot.Canteen => "Canteen",
            Slot.BreakRoom => "Break room",
            _ => s.ToString(),
        };

        internal static RoomType? NeedType(Slot s) => s switch
        {
            Slot.Dormitory => RoomType.DORMITORY,
            Slot.Bathroom => RoomType.BATHROOM,
            Slot.Canteen => RoomType.CANTEEN,
            Slot.BreakRoom => RoomType.BREAK_ROOM,
            _ => null,
        };

        // Room types where a job's work is found through FindIoInRoomTask, the only search the manager can steer,
        // read from the game's behaviour trees on 27 September 2026: BT_Cook searches the kitchen, BT_PaySalary
        // (the banker paying salaries) the treasure room. Other jobs find their place with other tasks (crafting,
        // cleaning, serving drinks, guarding), so no job room can be assigned to them yet.
        internal static RoomType[] JobRoomTypes(JobType job) => job switch
        {
            JobType.COOK => new[] { RoomType.KITCHEN },
            JobType.BANKER => new[] { RoomType.TREASURE_ROOM },
            _ => Array.Empty<RoomType>(),
        };

        internal static RoomType[] SlotTypes(Slot s, MinionInfo m)
        {
            var need = NeedType(s);
            if (need.HasValue) return new[] { need.Value };
            return m.HasJob ? JobRoomTypes(m.Job) : Array.Empty<RoomType>();
        }

        // The entry of this character, or null when he has none (an entry left by an earlier character with the same
        // entity id does not count).
        internal static Assignment Of(MinionInfo m) => ByMinion.TryGetValue(m.Entity, out var a) && a.Gen == m.Gen ? a : null;

        private static Assignment Entry(MinionInfo m)
        {
            if (!ByMinion.TryGetValue(m.Entity, out var a) || a.Gen != m.Gen)
            {
                a = new Assignment { Gen = m.Gen, Name = m.Name };
                ByMinion[m.Entity] = a;
            }
            return a;
        }

        internal static void Assign(MinionInfo m, Slot slot, RoomInfo? room)
        {
            var a = Entry(m);
            if (room.HasValue)
            {
                var r = room.Value;
                a.Rooms[slot] = new RoomRef { Entity = r.Entity, Gen = r.Gen, Type = r.Type, Floor = r.Floor };
                a.LastNote.Remove(slot);
                Plugin.Logger.LogInfo($"[Manager] {m.Name}: {SlotName(slot)} set to {r.Label} (room entity {r.Entity})");
                // The rooms page never offers a forbidden room for a slot; a file edited by hand is settled here in
                // favour of the assignment.
                if (a.Forbidden.RemoveAll(f => f.Entity == r.Entity) > 0)
                {
                    a.LastSkipped.Remove(r.Entity);
                    Plugin.Logger.LogInfo($"[Manager] {m.Name}: ban on {r.Label} lifted, it is now his {SlotName(slot)}");
                }
            }
            else if (a.Rooms.Remove(slot))
            {
                a.LastNote.Remove(slot);
                Plugin.Logger.LogInfo($"[Manager] {m.Name}: {SlotName(slot)} back to the game's choice");
            }
            if (a.IsEmpty) ByMinion.Remove(m.Entity);
            RebuildForbidden();
        }

        // ---- Bans --------------------------------------------------------------------------------------------

        private static long Key(int minion, int room) => ((long)minion << 32) | (uint)room;

        private static void RebuildForbidden()
        {
            ForbiddenPairs.Clear();
            foreach (var kv in ByMinion)
                foreach (var r in kv.Value.Forbidden)
                    ForbiddenPairs.Add(Key(kv.Key, r.Entity));
        }

        internal static bool AnyBan => ForbiddenPairs.Count > 0;

        internal static bool HasBan(int minion, int room) => ForbiddenPairs.Contains(Key(minion, room));

        internal static bool IsForbidden(MinionInfo m, RoomInfo room)
        {
            var a = Of(m);
            return a != null && a.Forbidden.Exists(r => r.Entity == room.Entity && r.Gen == room.Gen);
        }

        // Forbids the room to the character. A slot assigned to that room goes back to the game's choice; the slots
        // cleared this way are returned.
        internal static List<Slot> Forbid(MinionInfo m, RoomInfo room)
        {
            var a = Entry(m);
            if (!a.Forbidden.Exists(r => r.Entity == room.Entity && r.Gen == room.Gen))
            {
                a.Forbidden.RemoveAll(r => r.Entity == room.Entity); // a ban on an earlier room with the same entity id
                a.Forbidden.Add(new RoomRef { Entity = room.Entity, Gen = room.Gen, Type = room.Type, Floor = room.Floor });
                a.LastSkipped.Remove(room.Entity);
                Plugin.Logger.LogInfo($"[Manager] {m.Name}: {room.Label} forbidden to him (room entity {room.Entity})");
            }
            var cleared = a.Rooms.Where(kv => kv.Value.Entity == room.Entity).Select(kv => kv.Key).ToList();
            foreach (var slot in cleared)
            {
                a.Rooms.Remove(slot);
                a.LastNote.Remove(slot);
                Plugin.Logger.LogInfo($"[Manager] {m.Name}: {SlotName(slot)} back to the game's choice, since that room is now forbidden to him");
            }
            RebuildForbidden();
            return cleared;
        }

        internal static void Lift(MinionInfo m, int room, string label)
        {
            var a = Of(m);
            if (a == null) return;
            if (a.Forbidden.RemoveAll(r => r.Entity == room) > 0)
            {
                a.LastSkipped.Remove(room);
                Plugin.Logger.LogInfo($"[Manager] {m.Name}: ban on {label} lifted (room entity {room})");
            }
            if (a.IsEmpty) ByMinion.Remove(m.Entity);
            RebuildForbidden();
        }

        // Called by ForbiddenRoomFilter for a room of a search when the pair (character, room) has a ban. True when the
        // ban still belongs to this character and to this room, that is when neither entity id has been reused since.
        internal static bool Skips(Leopotam.EcsLite.EcsWorld world, int minion, int room)
        {
            if (!ByMinion.TryGetValue(minion, out var a) || world.GetEntityGen(minion) != a.Gen) return false;
            for (int i = 0; i < a.Forbidden.Count; i++)
            {
                var r = a.Forbidden[i];
                if (r.Entity != room) continue;
                if (world.GetEntityGen(room) != r.Gen) return false;
                a.LastSkipped[room] = DateTime.Now;
                // Logged at most once a minute for the same character and room.
                long key = Key(minion, room);
                float now = Time.unscaledTime;
                if (!SkipLogged.TryGetValue(key, out float at) || now - at >= 60f)
                {
                    SkipLogged[key] = now;
                    string floor = r.Floor < 0 ? "?" : (r.Floor + 1).ToString(CultureInfo.InvariantCulture);
                    Plugin.Logger.LogInfo($"[Manager] {a.Name}: forbidden room left out of his search ({Names.Room(r.Type)}, floor {floor}, room entity {room})");
                }
                return true;
            }
            return false;
        }

        // The assigned room of this character for a search of this room type, if any and if it still exists.
        internal static bool TryGetRoom(Leopotam.EcsLite.EcsWorld world, int worldSize, int minion, RoomType type, out int room, out Slot slot)
        {
            room = -1;
            slot = Slot.Job;
            if (!ByMinion.TryGetValue(minion, out var a)) return false;
            if (world.GetEntityGen(minion) != a.Gen) return false;
            foreach (var kv in a.Rooms)
            {
                if (kv.Value.Type != type) continue;
                var r = kv.Value;
                if (r.Entity < 0 || r.Entity >= worldSize || world.GetEntityGen(r.Entity) != r.Gen) return false;
                room = r.Entity;
                slot = kv.Key;
                return true;
            }
            return false;
        }

        internal static void Note(int minion, Slot slot, string text)
        {
            if (!ByMinion.TryGetValue(minion, out var a)) return;
            string stamped = $"{DateTime.Now:HH:mm:ss} {text}";
            a.LastNote[slot] = stamped;
            // The same outcome for the same character and slot is logged at most once a minute.
            string key = $"{minion}/{slot}/{text}";
            float now = Time.unscaledTime;
            if (NoteLogged.TryGetValue(key, out float at) && now - at < 60f) return;
            NoteLogged[key] = now;
            Plugin.Logger.LogInfo($"[Manager] {a.Name}, {SlotName(slot)}: {text}");
        }

        internal static void ReportError(string what, Exception e)
        {
            if (!ErrorsLogged.Add(what + e.Message)) return;
            Plugin.Logger.LogWarning($"[Manager] error while {what}: {e.Message}");
        }

        // ---- Persistence -------------------------------------------------------------------------------------

        private static string DataPath(string savePath)
        {
            string saveDir = Path.GetDirectoryName(savePath);
            string root = Path.GetDirectoryName(saveDir) ?? saveDir;
            return Path.Combine(root, "NDMUnofficialPatch-data", Path.GetFileNameWithoutExtension(savePath) + ".assignments.tsv");
        }

        private static bool IsProfile(string name) => name != null && name.StartsWith("Player_Profile", StringComparison.OrdinalIgnoreCase);

        internal static void OnSave(string filename, string savePath)
        {
            if (string.IsNullOrEmpty(savePath) || IsProfile(filename)) return;
            try
            {
                string path = DataPath(savePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // A ban takes the word Forbidden in the slot column; plugin versions before 0.15.0 drop such lines.
                var lines = new List<string> { "# NDM Unofficial Patch, character assignments: minion id, generation, name, slot (or Forbidden), room id, generation, type, floor" };
                int bans = 0;
                foreach (var kv in ByMinion)
                {
                    foreach (var r in kv.Value.Rooms) lines.Add(Line(kv.Key, kv.Value, r.Key.ToString(), r.Value));
                    foreach (var r in kv.Value.Forbidden) { lines.Add(Line(kv.Key, kv.Value, ForbiddenColumn, r)); bans++; }
                }
                File.WriteAllLines(path, lines);
                Plugin.Logger.LogInfo($"[Manager] {lines.Count - 1 - bans} assignment(s) and {bans} ban(s) written to {path}");
            }
            catch (Exception e) { ReportError("writing assignments", e); }
        }

        private const string ForbiddenColumn = "Forbidden";

        private static string Line(int minion, Assignment a, string column, RoomRef r) => string.Join("\t",
            minion.ToString(CultureInfo.InvariantCulture), a.Gen.ToString(CultureInfo.InvariantCulture), a.Name.Replace('\t', ' '), column,
            r.Entity.ToString(CultureInfo.InvariantCulture), r.Gen.ToString(CultureInfo.InvariantCulture), r.Type.ToString(),
            r.Floor.ToString(CultureInfo.InvariantCulture));

        internal static void OnLoad(string filename, string savePath)
        {
            if (IsProfile(filename)) return;
            // The main menu also reads every save to list it, so only the last read before a new world counts.
            _pendingLoadPath = string.IsNullOrEmpty(savePath) ? null : DataPath(savePath);
            _pendingLoadTime = DateTime.Now;
        }

        // A new game starts: the save the main menu read last was only listed, and its assignments are not restored.
        internal static void OnNewGame()
        {
            if (_pendingLoadPath != null) Plugin.Logger.LogInfo("[Manager] new game: no saved assignment restored");
            _pendingLoadPath = null;
        }

        internal static void OnWorldChanged()
        {
            ByMinion.Clear();
            ForbiddenPairs.Clear();
            SkipLogged.Clear();
            _pendingEntries = null;
            if (_pendingLoadPath == null) return;
            string path = _pendingLoadPath;
            _pendingLoadPath = null;
            if ((DateTime.Now - _pendingLoadTime).TotalMinutes > 3) return; // a save listed in the menu, then a new game
            try
            {
                if (!File.Exists(path)) return;
                _pendingEntries = File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).Where(p => p.Length >= 8).ToList();
                Plugin.Logger.LogInfo($"[Manager] {_pendingEntries.Count} saved assignment(s) read from {path}; they are applied once the characters are in place");
            }
            catch (Exception e) { ReportError("reading assignments", e); }
        }

        // Called every two seconds. Applies assignments read from a save once the loaded world lists its characters:
        // a character is matched by id and name, or by name alone if ids changed; a room by id, type and floor.
        internal static void Tick()
        {
            if (_pendingEntries == null || !GameContext.Ready) return;
            var minions = GameContext.ListMinions();
            if (minions.Count == 0) return;
            var rooms = GameContext.ListRooms();
            int applied = 0, bans = 0, dropped = 0;
            foreach (var p in _pendingEntries)
            {
                Slot slot = Slot.Job;
                bool ban = p[3] == ForbiddenColumn;
                if (!int.TryParse(p[0], out int id) || (!ban && !Enum.TryParse(p[3], out slot)) || !int.TryParse(p[4], out int roomId)
                    || !Enum.TryParse(p[6], out RoomType type) || !int.TryParse(p[7], out int floor)) { dropped++; continue; }
                string name = p[2];
                var m = minions.FirstOrDefault(x => x.Entity == id && x.Name == name);
                if (m.Name == null)
                {
                    var byName = minions.Where(x => x.Name == name).ToList();
                    if (byName.Count == 1) m = byName[0];
                }
                var r = rooms.FirstOrDefault(x => x.Entity == roomId && x.Type == type && x.Floor == floor);
                if (m.Name == null || r.Label == null) { dropped++; continue; }
                if (ban) { Forbid(m, r); bans++; }
                else { Assign(m, slot, r); applied++; }
            }
            _pendingEntries = null;
            Plugin.Logger.LogInfo(applied + bans == 0 && dropped > 0
                ? $"[Manager] none of the {dropped} saved line(s) matches the characters and rooms of this game; none applied"
                : $"[Manager] saved assignments restored: {applied} assignment(s) and {bans} ban(s) applied, {dropped} line(s) dropped (character or room not found)");
        }
    }

    [HarmonyPatch(typeof(SaveManagerStandalone), nameof(SaveManagerStandalone.Save))]
    internal static class ManagerSavePatch
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Prefix(SaveManagerStandalone __instance, SaveManager.SaveRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) CharacterManager.OnSave(name, __instance.CreatePath(name));
            }
            catch (Exception e) { CharacterManager.ReportError("saving assignments", e); }
        }
    }

    [HarmonyPatch(typeof(SaveManagerStandalone), nameof(SaveManagerStandalone.Load))]
    internal static class ManagerLoadPatch
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Prefix(SaveManagerStandalone __instance, SaveManager.LoadRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) CharacterManager.OnLoad(name, __instance.CreatePath(name));
            }
            catch (Exception e) { CharacterManager.ReportError("noting the save being loaded", e); }
        }
    }
}
