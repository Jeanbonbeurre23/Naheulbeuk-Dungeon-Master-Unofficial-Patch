using System;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;

namespace NDMUnofficialPatch.Management
{
    // Steers a character to his assigned room when he looks for a prop there.
    //
    // Read from game 1.8. The behaviour trees for showering and using the toilets (BT_Shower, BT_Pee, room type
    // BATHROOM), sleeping (BT_Sleep, DORMITORY), eating in the canteen (BT_EatInCanteen, CANTEEN), the break room
    // (BT_Entertainment_BreakRoom, BREAK_ROOM), cooking (BT_Cook, KITCHEN) and paying salaries (BT_PaySalary,
    // TREASURE_ROOM) all find their target with FindIoInRoomTask. Its OnExecute calls AFindRoomTask.FindValidRooms,
    // which lists the rooms of the wanted type that hold a usable prop, taking the first cleanliness band that has one
    // in the order the character's origin prefers (see RoomNeeds.cs), then narrowed by prestige when the task checks it
    // (m_availableRoomList, m_afterPrestigeCheckRoomList), calls SearchInRoom on them, and leaves the chosen
    // prop in m_foundPropEntity. OnExecute then writes that prop to the behaviour tree and the character reserves it.
    //
    // After FindValidRooms, if the character has an assigned room of the searched type and the game chose a prop
    // elsewhere, this patch runs the task's own SearchInRoom on the assigned room alone. If that finds a free prop, it
    // replaces the game's choice; otherwise the game's choice is restored. The assigned room is only tried when it is
    // among the rooms the game itself listed for this character, so the game's order of cleanliness and its prestige
    // rule still apply.
    [HarmonyPatch(typeof(AFindRoomTask), nameof(AFindRoomTask.FindValidRooms))]
    internal static class RoomSearchRedirect
    {
        private static bool Prepare() => Settings.CharacterManager.Value;

        private static void Postfix(AFindRoomTask __instance, RoomType roomToSearchFor)
        {
            if (CharacterManager.ByMinion.Count == 0) return;
            try
            {
                int minion = __instance.m_entity;
                if (!CharacterManager.ByMinion.ContainsKey(minion)) return;
                if (!GameContext.TryWorld(out var world, out int size)) return;
                if (!CharacterManager.TryGetRoom(world, size, minion, roomToSearchFor, out int room, out Slot slot)) return;
                var task = __instance.TryCast<FindIoInRoomTask>();
                if (task == null) return;

                int gameProp = task.m_foundPropEntity;
                if (gameProp < 0)
                {
                    CharacterManager.Note(minion, slot, "nothing free anywhere; the game found no prop either");
                    return;
                }
                if (GameContext.RoomOfProp(world, gameProp) == room)
                {
                    CharacterManager.Note(minion, slot, "used his assigned room (the game chose it too)");
                    return;
                }
                if (!Accepted(__instance, room, out string why))
                {
                    CharacterManager.Note(minion, slot, "went elsewhere: " + why);
                    return;
                }

                float distance = task.m_nearestDistance;
                bool ended = task.m_isLoopingOver;
                task.m_foundPropEntity = -1;
                task.m_nearestDistance = float.MaxValue;
                task.m_isLoopingOver = false;
                task.SearchInRoom(room);
                if (task.m_foundPropEntity >= 0)
                {
                    CharacterManager.Note(minion, slot, "steered to his assigned room");
                    return;
                }
                task.m_foundPropEntity = gameProp;
                task.m_nearestDistance = distance;
                task.m_isLoopingOver = ended;
                CharacterManager.Note(minion, slot, "went elsewhere: nothing free in his assigned room");
            }
            catch (Exception e)
            {
                CharacterManager.ReportError("steering a room search", e);
            }
        }

        private static bool Accepted(AFindRoomTask task, int room, out string why)
        {
            List<int> afterPrestige = task.m_afterPrestigeCheckRoomList;
            bool prestigeUsed = task.CheckRoomPrestige && afterPrestige != null && afterPrestige.Count > 0;
            if (prestigeUsed)
            {
                if (Contains(afterPrestige, room)) { why = null; return true; }
                why = Contains(task.m_availableRoomList, room)
                    ? "his assigned room is below the prestige he expects"
                    : "his assigned room is not among the rooms the game tried (it tried a cleanliness his origin prefers first, or the room has no usable prop, is unfinished or closed)";
                return false;
            }
            if (Contains(task.m_availableRoomList, room)) { why = null; return true; }
            why = "his assigned room is not among the rooms the game tried (it tried a cleanliness his origin prefers first, or the room has no usable prop, is unfinished or closed)";
            return false;
        }

        private static bool Contains(List<int> list, int value)
        {
            if (list == null) return false;
            int n = list.Count;
            for (int i = 0; i < n; i++)
                if (list[i] == value) return true;
            return false;
        }
    }
}
