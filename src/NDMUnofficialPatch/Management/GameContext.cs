using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;

namespace NDMUnofficialPatch.Management
{
    internal struct MinionInfo
    {
        public int Entity;
        public short Gen;
        public string Name;
        public JobType Job;
        public bool HasJob;
    }

    internal struct RoomInfo
    {
        public int Entity;
        public short Gen;
        public RoomType Type;
        public int Floor;
        public bool UnderConstruction;
        public string Label;
    }

    // The game objects the character manager, the resource bar and the storage capacity read, captured from two systems
    // that run every frame: WorkerTaskSystem (fields m_world and m_roomsUtility) and UpdateBuildEventsSystem (fields
    // m_minionUtility, m_resourceUtility and m_foodUtility).
    // A new pair of system objects means a new world, that is a new game or a loaded save.
    internal static unsafe class GameContext
    {
        private static IntPtr _workerTaskSystem;
        private static IntPtr _buildEventsSystem;
        private static IntPtr _world;
        private static bool _layoutChecked;

        internal static void CaptureWorkerTaskSystem(IntPtr system)
        {
            if (system == _workerTaskSystem) return;
            _workerTaskSystem = system;
            IntPtr world = Il2CppRaw.ReadObject(system, "m_world", "EcsWorld");
            if (world == _world) return;
            _world = world;
            Economy.ResourceFlows.Reset();
            CharacterManager.OnWorldChanged();
            RecruitTargets.OnWorldChanged();
        }

        internal static void CaptureBuildEventsSystem(IntPtr system) => _buildEventsSystem = system;

        // Whether any feature reads the world through this class, so that the two capture patches are installed.
        internal static bool Needed =>
            Settings.CharacterManager.Value || Settings.ResourceBar.Value || Settings.Bilan.Value || Settings.MinionColumns.Value
            || Settings.StorageCapacity.Value || Settings.DiagnosticsMorale.Value || Settings.RoomNeedsPanel.Value
            || Settings.CombatStrength.Value || Settings.FurnitureToolSwitch.Value || Settings.DiagnosticsUnreachable.Value
            || Settings.HealDeadlock.Value || Settings.CleaningRadius.Value > 0 || Settings.MaximumMinions.Value > 0
            || Settings.ResourceMaximum.Value > 0 || Settings.GolbarghLairOnly.Value || Settings.RecruitTargets.Value
            || Balance.NecromancerCultistHealing.Needed;

        internal static bool Ready => _workerTaskSystem != IntPtr.Zero && _buildEventsSystem != IntPtr.Zero && _world != IntPtr.Zero;

        internal static bool TryWorld(out EcsWorld world, out int size)
        {
            world = null;
            size = 0;
            if (_world == IntPtr.Zero) return false;
            world = new EcsWorld(_world);
            if (!world.IsAlive()) return false;
            size = world.GetWorldSize();
            return true;
        }

        internal static MinionUtility Minions => new MinionUtility(Il2CppRaw.ReadObject(_buildEventsSystem, "m_minionUtility", "MinionUtility"));

        internal static RoomsUtility Rooms => new RoomsUtility(Il2CppRaw.ReadObject(_workerTaskSystem, "m_roomsUtility", "RoomsUtility"));

        internal static ResourceUtility ResourceUtility => new ResourceUtility(Il2CppRaw.ReadObject(_buildEventsSystem, "m_resourceUtility", "ResourceUtility"));

        internal static FoodUtility FoodUtility => new FoodUtility(Il2CppRaw.ReadObject(_buildEventsSystem, "m_foodUtility", "FoodUtility"));

        internal static GameData GameData => new GameData(Il2CppRaw.ReadObject(ResourceUtility.Pointer, "m_gameData", "GameData"));

        // The world captured last, to tell when a new one appears.
        internal static IntPtr WorldPointer => _world;

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            Il2CppRaw.ExpectValueFieldOffset<ParentsComponent>("Parents", 0);
            _layoutChecked = true;
        }

        // Player minions from the game's own filter (MinionTag, not dead, not a recruitment candidate, not leaving),
        // without unique characters (UniqueComponent) and VIPs (VipTag).
        internal static List<MinionInfo> ListMinions()
        {
            var result = new List<MinionInfo>();
            if (!Ready || !TryWorld(out var world, out int size)) return result;
            var util = Minions;
            var filter = new EcsFilter(Il2CppRaw.ReadObject(util.Pointer, "m_minionFilter", "EcsFilter"));
            var unique = RawPool.Of<UniqueComponent>(world, -1);
            var vip = RawPool.Of<VipTag>(world, -1);
            var job = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));

            int count = filter.GetEntitiesCount();
            var ids = filter.GetRawEntities();
            for (int i = 0; i < count; i++)
            {
                int e = ids[i];
                if (!world.IsEntityAlive(e, size) || unique.Has(e) || vip.Has(e)) continue;
                var info = new MinionInfo { Entity = e, Gen = world.GetEntityGen(e) };
                IntPtr j = job.Item(e);
                if (j != IntPtr.Zero) { info.Job = ((JobPracticedComponent*)j)->JobPracticed; info.HasJob = true; }
                try { info.Name = util.GetMinionFullName(e); } catch { info.Name = null; }
                if (string.IsNullOrWhiteSpace(info.Name)) info.Name = $"Minion {e}";
                result.Add(info);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        // Every room of the dungeon (RoomsUtility.m_roomFilter), labelled by type, floor and order of construction.
        internal static List<RoomInfo> ListRooms()
        {
            var result = new List<RoomInfo>();
            if (!Ready || !TryWorld(out var world, out int size)) return result;
            var filter = new EcsFilter(Il2CppRaw.ReadObject(Rooms.Pointer, "m_roomFilter", "EcsFilter"));
            var types = RawPool.Of<RoomTypeComponent>(world, sizeof(RoomTypeComponent));
            var floors = RawPool.Of<GridFloorComponent>(world, sizeof(GridFloorComponent));
            var building = RawPool.Of<RoomInConstructionComponent>(world, -1);

            int count = filter.GetEntitiesCount();
            var ids = filter.GetRawEntities();
            for (int i = 0; i < count; i++)
            {
                int e = ids[i];
                IntPtr t = types.Item(e);
                if (t == IntPtr.Zero || !world.IsEntityAlive(e, size)) continue;
                IntPtr f = floors.Item(e);
                result.Add(new RoomInfo
                {
                    Entity = e,
                    Gen = world.GetEntityGen(e),
                    Type = ((RoomTypeComponent*)t)->Value,
                    Floor = f == IntPtr.Zero ? -1 : ((GridFloorComponent*)f)->Floor,
                    UnderConstruction = building.Has(e),
                });
            }
            result.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : a.Floor != b.Floor ? a.Floor.CompareTo(b.Floor) : a.Entity.CompareTo(b.Entity));
            for (int i = 0, n = 0; i < result.Count; i++)
            {
                n = i > 0 && result[i - 1].Type == result[i].Type && result[i - 1].Floor == result[i].Floor ? n + 1 : 1;
                var r = result[i];
                r.Label = $"{Names.Room(r.Type)}, floor {(r.Floor < 0 ? "?" : (r.Floor + 1).ToString(CultureInfo.InvariantCulture))}, no. {n}" + (r.UnderConstruction ? " (under construction)" : "");
                result[i] = r;
            }
            return result;
        }

        // The room a prop stands in: the first entry of its ParentsComponent (a FixedList32Bytes<int>: a 16-bit count,
        // 2 bytes of padding, then the ids), which is how TavernUtility.TavernAccesibleTreatment finds a prop's room.
        internal static int RoomOfProp(EcsWorld world, int prop)
        {
            CheckLayout();
            var parents = RawPool.Of<ParentsComponent>(world, 32);
            IntPtr p = parents.Item(prop);
            if (p == IntPtr.Zero || *(ushort*)p == 0) return -1;
            return *(int*)(p + 4);
        }
    }

    internal static class Names
    {
        internal static string Room(RoomType type) => type switch
        {
            RoomType.TAVERN => "Tavern",
            RoomType.DORMITORY => "Dormitory",
            RoomType.KITCHEN => "Kitchen",
            RoomType.CANTEEN => "Canteen",
            RoomType.BATHROOM => "Bathroom",
            RoomType.WORKSHOP => "Workshop",
            RoomType.CORRIDOR => "Corridor",
            RoomType.TRAINING_ROOM => "Training room",
            RoomType.GARDEN => "Garden",
            RoomType.ARMORY => "Armory",
            RoomType.LABORATORY => "Laboratory",
            RoomType.LIBRARY => "Library",
            RoomType.TREASURE_ROOM => "Treasure room",
            RoomType.BREAK_ROOM => "Break room",
            RoomType.PRISON => "Prison",
            _ => type.ToString(),
        };

        internal static string Job(JobType job)
        {
            string s = job.ToString().ToLowerInvariant().Replace('_', ' ');
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    [HarmonyPatch(typeof(WorkerTaskSystem), nameof(WorkerTaskSystem.Run))]
    internal static class CaptureWorkerTaskSystemPatch
    {
        private static bool Prepare() => GameContext.Needed;
        private static void Postfix(WorkerTaskSystem __instance)
        {
            try { GameContext.CaptureWorkerTaskSystem(__instance.Pointer); }
            catch (Exception e) { CharacterManager.ReportError("capturing the world", e); }
        }
    }

    [HarmonyPatch(typeof(UpdateBuildEventsSystem), nameof(UpdateBuildEventsSystem.Run))]
    internal static class CaptureBuildEventsSystemPatch
    {
        private static bool Prepare() => GameContext.Needed;
        private static void Postfix(UpdateBuildEventsSystem __instance) => GameContext.CaptureBuildEventsSystem(__instance.Pointer);
    }

    // The character whose details panel the player opened last; the manager preselects him.
    [HarmonyPatch(typeof(MinionDetailsTooltip), nameof(MinionDetailsTooltip.SetTarget))]
    internal static class LastClickedCharacterPatch
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static void Postfix(int entity) => CharacterManager.LastClicked = entity;
    }
}
