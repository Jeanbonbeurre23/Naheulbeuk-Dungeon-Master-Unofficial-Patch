# Inserted floors

Plugin 0.23.0, 3 October 2026, game 1.8. The conversion of a save started with seven floors is described in `floor-conversion.md`.

Setting `Floors.InsertedFloors`, 5 by default, 0 to 10, 0 turning the feature off. It replaces `Floors.InsertTestFloor` of plugins 0.21 and 0.22. Code in `src/NDMUnofficialPatch/Floors/FloorInsertion.cs` (scenes, lock, save marker), `FloorTables.cs` (per-floor tables), `FloorConversion.cs` (first load of a converted save) and `FloorPatches.cs` (hooks).

With N inserted floors, a new game (campaign or sandbox) gets N copies of floor 4 as floors 5 to 4+N. The tavern floor and the roof floor become floors 5+N and 6+N, N x 20 units higher, with the roof, the balloons and the bridges. With the default of 5, the dungeon has twelve floors, 0 to 11: floors 5 to 9 are copies of floor 4, the tavern floor is 10 and the roof is 11. Floors keep the game's numbering, 0 being the ground floor.

## When it applies

A new game started with `GameMaster.LaunchCampaign` or `SandboxSettingsPage.LaunchSandbox` gets the number of floors the setting gives at that moment. A save made in such a game is marked with `NDMUnofficialPatch-data\<save>.floors.txt` beside the patch's other data files, with a line `inserted N`. A marked save always loads with its N inserted floors, whatever the setting says, because its entities and its floor manager count them. A marker written by plugin 0.22 has no number and means one floor. A save written from a game without inserted floors removes the marker of that save name. Saves without a marker load with seven floors, so a campaign started before 0.23.0 stays as it was unless it is converted.

## What changes in the scenes

Before `DungeonGameMode.OnSceneLoaded` collects the floors, the patch looks for the objects of floors 4 to 6 in every loaded scene: the grid objects of `World` (`FloorGrid` and `FloorRoot`), the decoration of `World_HD` (`EnvironmentVisualManagement`) and the floor roots of `World_Audio` and of the level scene (`FloorRoot`). Floors 6 and 5 are raised by N x 20 units and renumbered 6+N and 5+N. Floor 4 is copied N times, the k-th copy k x 20 units higher as floor 4+k, and each copy loses everything named after the Golbargh (his spawner, his lair and his lair's sound). Renumbering sets the floor number of `FloorGrid`, `FloorRoot` and `EnvironmentVisualManagement`, and the height of the grid's origin, which is written in world coordinates.

Fixed meshes merged at build time into combined meshes draw where they were baked whatever their object's position. The patch recognises them by their mesh, named `Combined Mesh (root: scene) N`, and gives each a root transform raised by the object's own number of floors (`Renderer.staticBatchRootTransform`), one root object per height.

The level scenes' per-floor layer prefabs (`FloorLayerPreset.LayersByFloors`) follow: floors 5 to 4+N take floor 4's, 5+N takes 5's and 6+N takes 6's. A scene that would load after the floors were collected is processed when it appears, for two minutes.

## Tables

The inserted floors take floor 4's entry and floors 5+N and 6+N take those of the floors they were, in `FloorInformationsConfig` (upkeep per decade, the two minion limits, the distance per floor). Floors 7 to 6+N get floor 6's description in `UIGameConfig.FloorDescriptions`. The unlock nodes of floors 5 and 6 (`FloorTreeComponentConfig.m_floorNumber`) become 5+N and 6+N, and the conditions on floors 5 and 6 (`UnlockFloorConditionConfig.m_nbFloor`) follow them. These configs are shared by every game of a session, so the game's values are read once and put back for any game without inserted floors. They are extended when the scenes load, again just before the game's systems start (`EcsSystems.Init`), and every 2 seconds for two minutes, each table once for a given N.

Every wall piece range ending at floor 6 (`WallPartConfig<T>.Items[].Range`, in `BuilderConfig.TopWalls` and `TopPatch` and in every room type's `CornerWalls` and `SideWalls`) is extended to floor 6+N. All floors share the same pieces, so no floor changes look.

The camera's limit rectangles (`CameraController.m_boundaryList`, one per floor read at floor + 2) get floor 4's rectangle inserted N times. The floor tower gets a copy of floor 4's button for each inserted floor, the buttons of floors 5 and 6 are renumbered 5+N and 6+N, and every label is set again (`UIUtility.Text.GetFloorText`). A label above floor 6, missing from the game's texts, is floor 6's plus the difference, so that languages counting from 1 stay right. A copied button gets the game's ECS references through `DataManager.InjectMonoBehaviour` on its first update when it lacks them.

## The inserted floors' lock

The inserted floors follow floor 4. `FloorUtility.IsFloorLocked` returns floor 4's answer for each of them, and their buttons show floor 4's unlock entity, so their lock, conditions and price are floor 4's. When the game unlocks floor 4 (`FloorUtility.UnlockFloor`), the patch creates for each inserted floor an unlock entity carrying only a `FloorTreeComponent` with its number, unlocked, and runs the game's `UnlockFloor` on it. That call raises the minion limit as for any floor, loads the entities of the floor's roots (its garden and its entrance to the outside, copied from floor 4) and refreshes the floor's pathfinding. The Golbargh is created only for floor 4, as before. Each inserted floor costs floor 4's upkeep, 350 gold per decade, 1,750 for five.

Log lines start with `[Floors]`.
