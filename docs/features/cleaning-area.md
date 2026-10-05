# Cleaning area

Plugin 0.16.0, 1 October 2026, game 1.8. Setting `Balance.CleaningRadius` (2 by default, 0 to 6).

## What it does

When a domestic finishes a cleaning stop, every square of the same room within `CleaningRadius` squares of the stop is cleaned as well. With the default of 2, a stop cleans a 5 by 5 patch, 25 squares, where the game cleans 5. The squares outside the stop's room are left alone, so a stop in a corridor does not clean the room behind the wall. A stop interrupted before its own squares are clean cleans nothing more. With 0, the hook is not installed and the game's cleaning is unchanged.

Of three ways to speed up cleaning, a larger area per stop, faster stops, and less dirt left by feet and furniture, the first was chosen on 1 October 2026. A larger area cuts the walking between stops, which takes more of a domestic's time than the stop itself.

## How the game cleans

`BT_Clean`, the behaviour tree of CLEAN (a domestic's work), finds a spot with `FindDirtinessPositionTask`, spawns a one-square NeedClean entity there (`NeedCleanEntityConfig`), walks to it and runs `CleanDirtinessTask`. The NeedClean entity carries a `TilesToCleanComponent`, an array of floor squares (`TilesToCleanConfig.InitComponent` makes room for 5) and `TotalDirtinessPercentageOnTiles`, set to 100 at creation. Each frame, `CleanDirtinessActionSystem.OnUpdateLoop` lowers the dirt of those squares together: a full square (`DirtinessConfig.m_maxDirtinessPerTile`, 60) is clean after `m_maxCleanDurationPerTile` (1.5 s) of game time. The domestic's grade does not enter the formula. The system then sets `TotalDirtinessPercentageOnTiles` to the squares' mean dirt and, when that is no longer positive, sets it to 0 and ends the action.

## How the patch works

`Balance/CleaningArea.cs` puts a prefix and a postfix on `CleanDirtinessActionSystem.OnUpdateLoop`. The prefix notes, for each cleaning action under way, its NeedClean entity (the action's `IoEntityComponent`) when its squares are not yet clean. The postfix finds those whose `TotalDirtinessPercentageOnTiles` fell to 0 during the frame, the stops just finished. For each, it takes the stop's square and room (`GridCoordinatesComponent`, `GridFloorComponent`, `RoomTileComponent`) and calls `DirtinessUtility.ResetDirtiness` on every dirty square of that room within the radius. `ResetDirtiness` is the method the game uses to clear a square's dirt, when a prop or a wall is placed on it for instance, and it calls `OnTileDirtinessChange` when the dirt changed.

The squares are found through an index of every entity with a `TileDirtinessComponent`, by floor and position. The index is built for each new world and rebuilt, at most every 5 seconds, when the square under a stop is missing from it, which happens after a room is built.

## Log

`[Cleaning]` lines: the number of floor squares indexed, once per game, and every 5 minutes of play the number of stops finished and of squares cleaned around them. An error is logged once, and the game's cleaning then goes on unchanged.

## Limits

The patch changes how much one stop cleans, not how often domestics clean. In a test on 30 September 2026 no domestic was cleaning in any of the five snapshots of the morale watch, most of them waiting to be healed (see `heal-deadlock.md`). Whether two domestics per floor are enough depends on how much of their time is left for CLEAN once healing works. The dirt vanishes from the patch at once when the stop ends, rather than during the stop.
