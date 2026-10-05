# Furniture tool switch

Plugin 0.12.0, 29 September 2026, game 1.8. Setting `Fixes.FurnitureToolSwitch` (on by default).

## The problem

In testing, a click on a furniture icon left the builder in wall addition, wall removal or deletion, and this happened before any mod was installed. Read from the method bodies: a click on an icon of the construction popup runs `ConstructionPropPopup.OnItemSlotSubmit`, which, for anything but a door, calls `SwitchToProp` and then sets the popup's tool to furniture (`SetTool(ADD_PROP)`) whatever `SwitchToProp` did. `SwitchToProp` leaves the builder in its state without a word in two cases. In wall mode (`Builder.BuildState.Walls`) it validates the walls first (`Builder.ValidateWalls`), which refuses when the room being edited is on another floor than the one displayed (`BuilderManagerComponent.BuildingFloor` against `DungeonGameMode.m_currentFloorIndex`), when a wall stands on an obstacle (`WallOnObstacle`), when the room's surface is not valid (`SurfaceNotValid`), and in other cases further down the method. Outside corridors and gardens it also requires the room to have walls (`DoesRoomHaveWall`). The popup then shows the furniture tool while the builder keeps adding or removing walls, or deleting. With UnityExplorer installed, the wrong event system (see `game-event-system.md`) makes it more frequent: a click on an icon also starts a wall section behind the popup.

## The fix

A postfix on `OnItemSlotSubmit` (`Fixes/FurnitureToolSwitch.cs`) checks whether the builder reached the furniture state (`Props`, or `Doors` for a door). If not, it logs the builder's state and the reasons it can read from `BuilderManagerComponent`, then removes the causes that cost no work and tries `SwitchToProp` again. A wall section being drawn is cancelled (`ConstructionPropPopup.TryCancelWallSection`). When another floor is displayed, the room's floor is asked for (`DungeonPage.SetDesiredFloor`) and the switch is tried again once it shows, within 4 seconds. If the builder still does not switch, the popup's tool is set back to the one in use before the click, so that the buttons show the mode the builder is really in. A locked icon is left to the game, and so is a door.

## Limits

When the walls themselves are not valid (a wall on an obstacle, an invalid surface), the builder stays in wall mode, as the game requires; the patch only makes the buttons and the log say so. The log line under `[Builder]` is the way to find a case this does not cover.
