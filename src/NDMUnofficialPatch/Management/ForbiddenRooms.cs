using System;
using HarmonyLib;

namespace NDMUnofficialPatch.Management
{
    // Keeps a character out of the rooms forbidden to him (see CharacterManager.Forbid).
    //
    // Read from game 1.8. A FindIoInRoomTask search (sleeping, the toilets and showers, eating in the canteen, the break
    // room, cooking, paying and collecting salaries, training, and the other behaviour trees listed with
    // CharacterManager.ForbiddableTypes) runs AFindRoomTask.FindValidRooms, which first calls
    // HasOneRoomMatchingDirtiness and gives up when that returns false. HasOneRoomMatchingDirtiness fills
    // m_availableRoomList with the rooms of one cleanliness band after another, in the order the character's origin
    // prefers (see RoomNeeds.cs), and each time has CheckAvailableRoomList remove every room for which the task's
    // virtual IsRoomValid returns false. FindIoInRoomTask.IsRoomValid keeps a room that holds a free prop of the kind
    // wanted. The prestige check and SearchInRoom then work on what is left of that list.
    //
    // This prefix makes IsRoomValid return false for a room forbidden to the searching character, so the room leaves the
    // list before cleanliness bands and prestige are weighed. The game then chooses among his other rooms by its own
    // rules; when every room of the type is forbidden, or none of the others has a free prop, the search fails as it
    // does when no room has one.
    [HarmonyPatch(typeof(FindIoInRoomTask), nameof(FindIoInRoomTask.IsRoomValid))]
    internal static class ForbiddenRoomFilter
    {
        private static bool Prepare() => Settings.CharacterManager.Value;

        private static bool Prefix(FindIoInRoomTask __instance, int __0, ref bool __result)
        {
            if (!CharacterManager.AnyBan) return true;
            try
            {
                int minion = __instance.m_entity;
                if (!CharacterManager.HasBan(minion, __0)) return true;
                if (!GameContext.TryWorld(out var world, out _)) return true;
                if (!CharacterManager.Skips(world, minion, __0)) return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                CharacterManager.ReportError("leaving a forbidden room out of a search", e);
                return true;
            }
        }
    }
}
