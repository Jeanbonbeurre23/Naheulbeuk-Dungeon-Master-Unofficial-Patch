using System;
using System.Collections.Generic;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;

namespace NDMUnofficialPatch.Fixes
{
    // Keeps a character from getting in line at a room that has no waiting line.
    //
    // Game 1.8, from the method bodies, the behaviour trees and five crash dumps, read on 9 October 2026. A room's
    // waiting line is a WaitingLineComponent on the room entity: three NativeLists, the counters of the room
    // (PossibleIos), the characters in line (WaitingLine) and the characters walking to the line
    // (MovingTowardsEntities). UpdateBuildEventsSystem.Run adds the component to the room, and allocates the lists,
    // when a prop carrying WaitingLineIoTagComponent is built in it (the tavern counters, the treasure room's reception
    // desk, Reivax's desk). A room where no such prop was ever built has no waiting line.
    //
    // StartWaitingInLineTask.OnExecute reads the room from its RoomToWaitInFrontOfPackedEntityVariable, takes the
    // room's WaitingLineComponent without checking that the room has one, and adds the character to its WaitingLine
    // list. For a room without the component the pool returns its empty slot, whose lists are null, and the add reads
    // the length of a null list: an access violation at GameAssembly+0x35d288 that closes the game. Minions reach the
    // task after FindWaitingLineToBeServedTask, which only picks rooms that have a waiting line. Adventurers on a
    // tavern quest event (BT_Adventurer_Tavern) do not: Adventurer.GetNextEventTask gives them the room the quest
    // names, and the tree goes from a position in that room straight to StartWaitingInLineTask.
    //
    // A test game closed this way five times in four days (Windows application log, event 1000, and the crash
    // dumps), each time in this task under BT_Adventurer_Tavern, with versions 0.24.4, 0.25.0 and 0.26.1 of this
    // patch installed.
    //
    // A prefix on StartWaitingInLineTask.OnExecute checks the room. When the room has a waiting line with its list
    // allocated, the game runs as usual. Otherwise the character is put in the line of another room of the same type
    // that has one, and has at least one counter, preferably on the same floor and with the shortest line: the
    // task's variable is pointed at that room for the call and restored by the postfix, so that the adventurer's quest
    // event, which Adventurer.PopEventTask later finds by that variable, still closes. When no room of that type has a
    // waiting line, the task ends in failure, as it does when the room no longer exists.
    internal static unsafe class WaitingLines
    {
        private const int ItemSize = 48;

        private static int _lineOffset = -1, _iosOffset, _typeOffset;
        private static bool _broken;
        private static int _redirected, _refused, _logged;
        private static IntPtr _restoreTask, _surveyed;
        private static EcsPackedEntity _restoreValue;
        private static readonly HashSet<long> Reported = new();

        private static void Layout()
        {
            if (_lineOffset >= 0) return;
            _iosOffset = Il2CppRaw.ValueFieldOffset<WaitingLineComponent>("PossibleIos");
            _typeOffset = Il2CppRaw.ValueFieldOffset<RoomTypeComponent>("Value");
            _lineOffset = Il2CppRaw.ValueFieldOffset<WaitingLineComponent>("WaitingLine");
        }

        // The UnsafeList behind a NativeList field, or IntPtr.Zero when the list was never allocated.
        private static IntPtr List(IntPtr item, int offset) => item == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(item + offset);

        private static int Count(IntPtr list) => list == IntPtr.Zero ? 0 : *(int*)(list + 8);

        private static bool Usable(RawPool lines, int room) => List(lines.Item(room), _lineOffset) != IntPtr.Zero;

        internal static bool BeforeStart(StartWaitingInLineTask task)
        {
            _restoreTask = IntPtr.Zero;
            if (_broken) return true;
            try
            {
                Layout();
                IntPtr t = task.Pointer;
                IntPtr worldPointer = Il2CppRaw.ReadPointer(t, "m_ecsWorld");
                if (worldPointer == IntPtr.Zero) return true;
                var world = new EcsWorld(worldPointer);
                int size = world.GetWorldSize();
                var lines = RawPool.From(Il2CppRaw.ReadPointer(t, "m_waitingLinePool"), ItemSize, "WaitingLineComponent");
                if (lines.IsNull) return true;
                if (worldPointer != _surveyed)
                {
                    _surveyed = worldPointer;
                    // The list of rooms is for the log only; a failure there leaves the guard on.
                    try { Survey(world, size, lines); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"[WaitingLine] could not list the rooms with counters: {e.Message}"); }
                }
                EcsPackedEntity packed = task.RoomToWaitInFrontOfPackedEntityVariable.value;
                int room = world.Resolve(packed, size);
                // A room that no longer exists is refused by the game itself.
                if (room < 0 || Usable(lines, room)) return true;

                int agent = *(int*)(t + Il2CppRaw.FieldOffset(t, "m_entity"));
                var types = RawPool.Of<RoomTypeComponent>(world, -1);
                IntPtr typeItem = types.Item(room);
                RoomType? type = typeItem == IntPtr.Zero ? null : *(RoomType*)(typeItem + _typeOffset);
                int other = Replacement(world, size, lines, types, type, room, agent);
                if (other >= 0)
                {
                    _restoreTask = t;
                    _restoreValue = packed;
                    task.RoomToWaitInFrontOfPackedEntityVariable.value = new EcsPackedEntity { Id = other, Gen = world.GetEntityGen(other) };
                    _redirected++;
                    Report(world, agent, room, type, other);
                    return true;
                }
                _refused++;
                Report(world, agent, room, type, -1);
                task.EndAction(false);
                return false;
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Logger.LogWarning($"[WaitingLine] guard failed, the game's own code runs from now on: {e}");
                return true;
            }
        }

        internal static void AfterStart(StartWaitingInLineTask task)
        {
            if (_restoreTask == IntPtr.Zero) return;
            try
            {
                if (task.Pointer == _restoreTask) task.RoomToWaitInFrontOfPackedEntityVariable.value = _restoreValue;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[WaitingLine] could not restore the room variable: {e.Message}"); }
            finally { _restoreTask = IntPtr.Zero; }
        }

        // A room of the same type with an allocated waiting line and at least one counter; on the character's floor
        // first, then the shortest line, then the lowest entity id.
        private static int Replacement(EcsWorld world, int size, RawPool lines, RawPool types, RoomType? type, int room, int agent)
        {
            if (type == null) return -1;
            var unavailable = RawPool.Of<UnavailableTag>(world, -1);
            var floors = RawPool.Of<GridFloorComponent>(world, 4);
            int Floor(int e) { IntPtr f = floors.Item(e); return f == IntPtr.Zero ? int.MinValue : *(int*)f; }
            int floor = Floor(agent);
            int best = -1, bestRank = int.MaxValue, bestLine = int.MaxValue;
            foreach (int r in lines.Entities())
            {
                if (r == room || !world.IsEntityAlive(r, size) || unavailable.Has(r)) continue;
                IntPtr item = lines.Item(r);
                IntPtr line = List(item, _lineOffset);
                if (line == IntPtr.Zero || Count(List(item, _iosOffset)) <= 0) continue;
                IntPtr ti = types.Item(r);
                if (ti == IntPtr.Zero || *(RoomType*)(ti + _typeOffset) != type.Value) continue;
                int rank = floor != int.MinValue && Floor(r) == floor ? 0 : 1;
                int length = Count(line);
                if (rank < bestRank || (rank == bestRank && length < bestLine))
                {
                    best = r;
                    bestRank = rank;
                    bestLine = length;
                }
            }
            return best;
        }

        // Once per world, at the first time someone gets in line: the rooms of the kinds that have counters, and
        // whether each has a waiting line, so that the log shows which rooms would have closed the game.
        private static void Survey(EcsWorld world, int size, RawPool lines)
        {
            var types = RawPool.Of<RoomTypeComponent>(world, -1);
            var parts = new List<string>();
            foreach (int r in types.Entities())
            {
                if (!world.IsEntityAlive(r, size)) continue;
                var type = *(RoomType*)(types.Item(r) + _typeOffset);
                if (type != RoomType.TAVERN && type != RoomType.TREASURE_ROOM && type != RoomType.REIVAX_OFFICE) continue;
                IntPtr item = lines.Item(r);
                string state = item == IntPtr.Zero ? "no waiting line"
                    : List(item, _lineOffset) == IntPtr.Zero ? "a waiting line with no list"
                    : $"a waiting line, {Count(List(item, _iosOffset))} counter(s), {Count(List(item, _lineOffset))} in line";
                parts.Add($"{type.ToString().ToLowerInvariant()} room entity {r}: {state}");
            }
            Plugin.Logger.LogInfo("[WaitingLine] rooms with counters in this dungeon: " + (parts.Count == 0 ? "none" : string.Join("; ", parts)));
        }

        private static string Who(EcsWorld world, int e)
        {
            string kind = RawPool.Of<Adventurer.AdventurerTag>(world, -1).Has(e) ? "adventurer" : RawPool.Of<MinionTag>(world, -1).Has(e) ? "minion" : "character";
            try
            {
                string name = GameContext.Minions?.GetMinionFullName(e);
                if (!string.IsNullOrWhiteSpace(name)) return $"{kind} {name} (entity {e})";
            }
            catch { }
            return $"{kind} entity {e}";
        }

        // Logging never stops the guard: a failure here is reported and the character is still handled.
        private static void Report(EcsWorld world, int agent, int room, RoomType? type, int other)
        {
            try { Describe(world, agent, room, type, other); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[WaitingLine] could not describe a case: {e.Message}"); }
        }

        private static void Describe(EcsWorld world, int agent, int room, RoomType? type, int other)
        {
            string what = type?.ToString().ToLowerInvariant() ?? "untyped";
            if (_logged < 30 && Reported.Add(((long)agent << 32) | (uint)room))
            {
                _logged++;
                string outcome = other >= 0
                    ? $"put in the line of {what} room entity {other} instead"
                    : $"no other {what} room has a waiting line and a counter; the step fails and the character tries again";
                Plugin.Logger.LogInfo($"[WaitingLine] {Who(world, agent)} was to get in line at {what} room entity {room}, which has no waiting line; {outcome}");
                if (_logged == 30) Plugin.Logger.LogInfo("[WaitingLine] further cases are counted only");
            }
            int total = _redirected + _refused;
            if (total % 100 == 0) Plugin.Logger.LogInfo($"[WaitingLine] {total} cases so far: {_redirected} sent to another room, {_refused} refused");
        }
    }

    [HarmonyPatch(typeof(StartWaitingInLineTask), "OnExecute")]
    internal static class WaitingLineGuard
    {
        private static bool Prepare() => Settings.WaitingLineGuard.Value;
        private static bool Prefix(StartWaitingInLineTask __instance) => WaitingLines.BeforeStart(__instance);
        private static void Postfix(StartWaitingInLineTask __instance) => WaitingLines.AfterStart(__instance);
    }
}
