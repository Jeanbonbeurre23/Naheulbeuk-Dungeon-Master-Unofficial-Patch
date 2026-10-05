using System;
using System.Linq;
using HarmonyLib;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Management
{
    // Choosing a room by clicking it in the dungeon, and showing a room by moving the camera to it.
    //
    // A click in the dungeon reaches ObservationController.OnValidate, which raycasts under the pointer and passes
    // the entity hit, with its EntityRef, to OnEntityClicked; that method selects the entity and opens its panel
    // (for a room, RoomDetailsPage). The game skips this when the pointer is over its interface, so a click on the
    // part of the dungeon left visible by the Minions window reaches it. While a pick is under way, a prefix on
    // OnEntityClicked takes the click instead: the game neither selects anything nor opens a panel, and the Minions
    // window stays open.
    //
    // The entity hit is a room when the player clicks its floor (EntityRef type ROOM). A wall (type WALL) is turned
    // into its room with RoomsUtility.GetRoomEntityFromWall, as ObservationController.ClickEntity does; a prop or a
    // prison cell into the room it stands in, through its ParentsComponent.
    //
    // A pick either assigns the room to a slot or, from the Forbidden rooms section of the page, forbids it to the
    // character.
    internal static unsafe class RoomPicker
    {
        private static RoomsPage _page;
        private static Slot _slot;
        private static bool _forbid;
        private static MinionInfo _minion;

        // A camera move waiting for the floor change it asked for.
        private static int _focusRoom = -1;
        private static int _focusFloor;
        private static DungeonPage _focusDungeon;
        private static float _focusUntil;

        internal static bool Active => _page != null;
        internal static bool IsPicking(RoomsPage page, Slot slot) => _page == page && !_forbid && _slot == slot;
        internal static bool IsForbidding(RoomsPage page) => _page == page && _forbid;

        internal static void Start(RoomsPage page, Slot slot, MinionInfo minion)
        {
            _page = page;
            _slot = slot;
            _forbid = false;
            _minion = minion;
            Plugin.Logger.LogInfo($"[Manager] {minion.Name}: picking the {CharacterManager.SlotName(slot)} in the dungeon");
        }

        internal static void StartForbid(RoomsPage page, MinionInfo minion)
        {
            _page = page;
            _forbid = true;
            _minion = minion;
            Plugin.Logger.LogInfo($"[Manager] {minion.Name}: picking a room to forbid in the dungeon");
        }

        internal static void Cancel(RoomsPage page)
        {
            if (page != null && _page != page) return;
            if (_page != null) Plugin.Logger.LogInfo("[Manager] pick cancelled");
            _page = null;
        }

        // Called by the prefix for every click in the dungeon while a pick is under way.
        internal static void OnWorldClick(int entity, EntityRef entityRef)
        {
            var page = _page;
            if (page == null) return;
            if (!GameContext.TryWorld(out var world, out _)) return;

            EntityRef.Type kind = EntityRef.Type.UNDEFINED;
            try { if (entityRef != null) kind = entityRef.EntityType; } catch { }

            var types = RawPool.Of<RoomTypeComponent>(world, sizeof(RoomTypeComponent));
            int room = -1;
            if (types.Has(entity)) room = entity;
            else if (kind == EntityRef.Type.WALL) room = GameContext.Rooms.GetRoomEntityFromWall(entity);
            else if (kind == EntityRef.Type.PROP || kind == EntityRef.Type.CELL || kind == EntityRef.Type.ITEM)
            {
                int parent = GameContext.RoomOfProp(world, entity);
                if (types.Has(parent)) room = parent;
            }

            if (room < 0)
            {
                string what = kind switch
                {
                    EntityRef.Type.MINION or EntityRef.Type.WORKER or EntityRef.Type.BARMAN or EntityRef.Type.PROTAGONIST => "a character",
                    EntityRef.Type.ADVENTURER => "an adventurer",
                    EntityRef.Type.CUSTOMER => "a customer",
                    _ => "not a room",
                };
                Reject(page, what == "not a room" ? "That is not a room. Click the floor of a room." : $"That is {what}. Click the floor of a room.");
                return;
            }

            var info = GameContext.ListRooms().FirstOrDefault(r => r.Entity == room);
            if (info.Label == null)
            {
                Reject(page, _forbid ? "This room cannot be forbidden." : "This room cannot be assigned.");
                return;
            }
            var minion = _minion;
            string noun = Names.Room(info.Type).ToLowerInvariant();

            if (_forbid)
            {
                if (info.Type == RoomType.CORRIDOR) { Reject(page, "A corridor cannot be forbidden. Click the floor of a room."); return; }
                if (info.Type == RoomType.WORKSHOP) { Reject(page, "A ban has no effect on a workshop: crafting is not covered."); return; }
                if (!CharacterManager.ForbiddableTypes.Contains(info.Type))
                {
                    Reject(page, $"A ban has no effect on {WithArticle(noun)}: the searches it covers never go there.");
                    return;
                }
                if (CharacterManager.IsForbidden(minion, info)) { Reject(page, "That room is already forbidden to him."); return; }
                _page = null;
                var cleared = CharacterManager.Forbid(minion, info);
                page.ForbidAccepted(info, cleared);
                return;
            }

            var allowed = CharacterManager.SlotTypes(_slot, minion);
            if (!allowed.Contains(info.Type))
            {
                string wanted = string.Join(" or ", allowed.Select(t => Names.Room(t).ToLowerInvariant()));
                Reject(page, $"That is {WithArticle(noun)}. Click {WithArticle(wanted)}.");
                return;
            }
            if (CharacterManager.IsForbidden(minion, info))
            {
                Reject(page, "That room is forbidden to him. Lift the ban first.");
                return;
            }

            var slot = _slot;
            _page = null;
            CharacterManager.Assign(minion, slot, info);
            page.PickAccepted(slot, info);
        }

        private static void Reject(RoomsPage page, string message)
        {
            if (_forbid) page.ForbidRejected(message);
            else page.PickRejected(_slot, message);
        }

        private static string WithArticle(string noun) => ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;

        // Moves the camera to the room with CameraController.SetEntityCoords, as ObservationController.OnClickedRoom
        // does; when the room is on another floor, asks the dungeon page for that floor and moves the camera again
        // once the floor has changed.
        internal static void Show(Component sheet, RoomInfo room)
        {
            var camera = CameraController.Instance;
            if (camera == null) return;
            camera.SetEntityCoords(room.Entity);

            DungeonPage dungeon = null;
            var half = sheet == null ? null : sheet.GetComponentInParent<BaseHalfScreenPage>();
            if (half != null) dungeon = half.m_dungeonPage;
            if (dungeon == null) dungeon = UnityEngine.Object.FindObjectOfType<DungeonPage>();
            int current = dungeon == null ? int.MinValue : dungeon.m_currentFloor;
            Plugin.Logger.LogInfo($"[Manager] showing {room.Label} (room entity {room.Entity}, room floor index {room.Floor}, displayed floor index {(current == int.MinValue ? "unknown" : current.ToString())})");
            if (dungeon == null || room.Floor < 0 || current == room.Floor) return;
            dungeon.SetDesiredFloor(room.Floor);
            _focusRoom = room.Entity;
            _focusFloor = room.Floor;
            _focusDungeon = dungeon;
            _focusUntil = Time.unscaledTime + 4f;
        }

        // Every frame, from ManagerBehaviour.
        internal static void Update()
        {
            if (_focusRoom < 0) return;
            try
            {
                if (Time.unscaledTime > _focusUntil || _focusDungeon == null || _focusDungeon.WasCollected)
                {
                    Plugin.Logger.LogInfo($"[Manager] the displayed floor did not become index {_focusFloor}");
                    _focusRoom = -1;
                    return;
                }
                if (_focusDungeon.m_currentFloor != _focusFloor) return;
                CameraController.Instance?.SetEntityCoords(_focusRoom);
                _focusRoom = -1;
            }
            catch (Exception e)
            {
                _focusRoom = -1;
                CharacterManager.ReportError("moving the camera to a room", e);
            }
        }
    }

    [HarmonyPatch(typeof(ObservationController), nameof(ObservationController.OnEntityClicked))]
    internal static class RoomPickerClickPatch
    {
        private static bool Prepare() => Settings.CharacterManager.Value;
        private static bool Prefix(int __0, EntityRef __1)
        {
            if (!RoomPicker.Active) return true;
            try { RoomPicker.OnWorldClick(__0, __1); }
            catch (Exception e) { CharacterManager.ReportError("picking a room", e); }
            return false;
        }
    }
}
