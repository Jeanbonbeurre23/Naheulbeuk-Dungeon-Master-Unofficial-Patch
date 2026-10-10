# Waiting line guard

Plugin 0.26.2, 9 October 2026, game 1.8. Setting `Fixes.WaitingLineGuard` (on by default).

## The problem

Adventurers whose quest sends them to the tavern get in line at the tavern room their quest names. The game only gives a room a waiting line when a counter is built in it, and the step that puts a character in line does not check that the room has one. When the quest names a tavern room without a counter, the game reads an empty list and closes without an error window (access violation at GameAssembly+0x35d288). Minions are not affected, since they choose a room with a waiting line before they get in line.

## The game's rule

A room's waiting line is a `WaitingLineComponent` on the room entity: three NativeLists, the counters of the room (`PossibleIos`), the characters in line (`WaitingLine`) and the characters walking to the line (`MovingTowardsEntities`). `UpdateBuildEventsSystem.Run` adds the component to the room, and allocates the lists, when a prop carrying `WaitingLineIoTagComponent` is built in it: the six tavern counters, the treasure room's reception desk and Reivax's desk. `StartWaitingInLineTask.OnExecute` reads the room from `RoomToWaitInFrontOfPackedEntityVariable`, checks that the room still exists, and takes its `WaitingLineComponent` without checking that the room has one; for a room without it, the pool returns its empty slot, whose lists are null. Minions reach the task after `FindWaitingLineToBeServedTask`, which only picks rooms with a waiting line. In `BT_Adventurer_Tavern`, `Adventurer.GetNextEventTask` writes the room named by the quest event into `TavernPackedEntity`, and the tree goes from a position in that room straight to `StartWaitingInLineTask`. Five crash dumps of the same closing all show this chain.

## What the patch does

Before a character gets in line, the patch looks at the room. When the room has a waiting line, the game goes on as usual. When it has none, the character is put in the line of another room of the same kind that has a waiting line and at least one counter, on his floor if there is one there, otherwise the one with the shortest line. He then waits there, is served at that room's counter, and goes back to the room his quest names to drink, so his quest event still ends as the game planned. When no room of that kind has a counter, the step fails and the adventurer starts his tavern visit again, walking in the room his quest names; the game no longer closes, and the visit can only end once a counter exists.

## How it is built

`Fixes/WaitingLineGuard.cs` has a prefix and a postfix on `StartWaitingInLineTask.OnExecute`. The prefix reads the task's world (`m_ecsWorld`), its pool of `WaitingLineComponent` (`m_waitingLinePool`, 48-byte items) and its room variable. When the room exists and has no component, or its `WaitingLine` list pointer is null, the prefix looks through the pool for a room with the same `RoomTypeComponent.Value`, a non-null `WaitingLine` list, a non-empty `PossibleIos` list and no `UnavailableTag`. If it finds one, it writes that room into the variable and lets the game's method run; the postfix writes the original room back, because `Adventurer.PopEventTask` later finds the quest event by that variable. If it finds none, it calls `EndAction(false)` and skips the game's method. Field offsets come from the runtime's metadata; if a read fails, the guard turns itself off for the session and logs why. A failure while listing the rooms or writing a log line is logged and leaves the guard on.

## Log

Once per world, at the first time a character gets in line: `[WaitingLine] rooms with counters in this dungeon: ...` for every tavern, treasure room and Reivax office, with `no waiting line` for a room without one. For each of the first 30 characters sent elsewhere or refused, a line naming him, the room and what was done. Then a count every 100 cases.

## Limits

The patch does not change which rooms a quest names. `EntityUtility.TryRemoveFromWaitingLine` reads the line of the room a minion was walking to in the same unchecked way; that path is not guarded, because only rooms with a waiting line are given to minions.
