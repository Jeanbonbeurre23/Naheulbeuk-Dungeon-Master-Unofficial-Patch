# Converting a seven-floor save

Plugin 0.23.3, 3 October 2026, game 1.8.

A game started before the inserted floors has seven floors in its save, and the plugin loads it with seven. The conversion gives such a save the inserted floors in two steps: a script rewrites a copy of the save, then the plugin builds the new floors with the game's own functions when that copy is first loaded. The source save is only read. Code in `tools/convert_save_floors.py` and `src/NDMUnofficialPatch/Floors/FloorConversion.cs`.

## Running it

The game must be closed. With Python 3, from the repository, the save folder being `%USERPROFILE%\AppData\LocalLow\Artefacts Studio\NDM\Save`:

`python tools\convert_save_floors.py "<save folder>\Game_Default_1.sav" "1 (12 floors)"`

The second argument is the new save's name as the game shows it; the file is `Game_<session>_<name>.sav` beside the source, and the script refuses to write over an existing file. `--floors N` sets the number of inserted floors (5 by default) and `--dry-run` converts and checks without writing. Beside the patch's data files the script writes the marker `<save>.floors.txt`, with the lines `inserted N` and `build 5 4+N`, and a report `<save>.conversion.txt` listing every change.

## What the script changes

A save is the game's ECS world written with the Odin serializer: one block per component type (`GameSave.PoolSaveData<T>`, each entity's index and component), then the world's entity table, the whole compressed with gzip behind a header holding the save's name and screenshot. The script reads it token by token and changes fixed-size values in place, except where it adds entries.

Floor numbers of 5 and above are raised by N. They sit in `GridFloorComponent.Floor` (every square, wall, piece of furniture and character: 8,466 entities on floors 5 and 6 in the campaign of 2 October), in the floor data and wall map of each floor, in the floor unlock entities and their conditions and notifications, in the camera's current floor, in the floor manager's highest floor, in the floors of path requests and of move, stair and combat actions (any field named `FloorId`), and in the behaviour trees' blackboards, where the variable `Floor` holds a floor and the y of the `Vector3` variables `Position` and `WaitingPosition` holds one too (a tavern client's waiting place on floor 5 reads (24, 5, 30)).

World heights of 99.5 and above are raised by N x 20, since floors 5 and 6 stand at 100 and 120. They sit in `TransformComponent.Position` (1,574 in that campaign, every one of them on floor 5 or 6 and none of the others), in the origin of each floor's data, in the start position of root-motion actions, in the behaviour trees' `float3` positions, and in the splines of paths being walked (nodes, curve points and samples; the samples' tangents are directions and stay). The grid manager's map of entities by square (`GridManagerComponent.TileEntities`) is keyed by grid identifiers that carry the floor in their upper bits (floor x 2^26 + x x 2^13 + y), and its 12,088 keys of floors 5 and 6 are renumbered. Animator parameter hashes that happen to fall in the same range of values sit elsewhere and are not touched.

Stair links that the renumbering would stretch over more than one floor are dropped. The game links a stair to the stairs of the floors just above and below at the same place (`StairsUtility.SetupLinks`, from the stairs' positions), and its pathfinding expects the linked stair on the next floor (`Pathfind.EntranceFactoryUtility.BuildEntranceGraph`). In the campaign of 2 October that is the pair between floor 4 and the tavern floor, stairs 32568 and 29385; each keeps its object and loses the link, as a stair whose other half is not built yet.

The maps keyed by floor are re-keyed and get an entry for each new floor: the floor manager's map from floor to floor data entity, the wall manager's map from floor to wall map entity, and the worker manager's three maps (workers to spawn, current timer and maximum spawn timer, the new floors taking 0, 0 and floor 4's timer). Each new floor gets two entities, appended after the last entity of the world with generation 1: a floor data entity copied from floor 4's with its own number and an origin k x 20 units higher, as `FloorDataUtility.CreateFloorEntity` makes it, and a wall map entity copied from floor 4's with its three maps emptied, as `Walls.UpdateWallMapSystem.Init` makes it in a new game. The plan had the plugin make these entities at load time; the script writes them instead, so that the game loads them with the save like every other floor's, and the plugin has no hash map of the game's to fill.

Before writing, the script packs the result, unpacks it and reads it again, and compares it with the source: the number of entities per floor (floors 5 and 6 moved to 5+N and 6+N, nothing else changed), the heights per band of 20 units, the floor data and wall maps (0 to 6+N), the per-floor maps (keys 0 to 6+N, old values under the new keys), the grid map's keys per floor and the entity table. Any field that looks like a floor or a height and that the script does not know stops it before anything is written.

## What the plugin does on the first load

The converted save loads with N inserted floors in the scenes, as a marked save does. On every load of a save with inserted floors, `FloorConversion` looks for inserted floors that have their floor data and no entity on them (no square, wall or furniture), and makes those floors as a new game makes them, in the game's order and with the game's functions. A converted save is the only case where such floors exist; the marker's `build` line only adds log lines.

The base room and squares come first. When `DungeonGameMode.InitSpawners` calls `FloorRoot.Init` on the first root of a new floor, the save's world and the game's systems exist. For each new floor, the plugin calls `RoomsUtility.AddRoom` with the floor's base room type (`CORRIDOR`) and surface, `LightsUtility.UpdateRoomLight`, sets the room's `VisualComponent.Instance` to the floor's ground object, and calls `RoomsUtility.AddOrUpdateRoomTile` with every square of the floor, which is what `GridInitSystem.InitAsync` does for every floor in a new game.

The level content comes next. A loaded game runs `FloorRoot.Init(true)` for every floor root, which destroys the scene's spawners since the save holds what they made. `DungeonGameMode.InitSpawners` builds the iterator of `FloorRoot.Init` itself (the compiler inlined the method), so the plugin hooks the iterator's `MoveNext` (`FloorRoot._Init_d__2`) and, on its first step, sets `isLoadGame` to false for the roots of the floors it built. Those roots take the new-game path, which runs the spawners of the copies of floor 4: walls, doors, the garden and its entrance, dead zones and the floor layer. The wall systems then fill the floors' wall maps.

The stairs are checked once the loading has set up the interface. Each link from a stair to a stair that is not one floor away, or that no longer exists, is removed in place: the stair's remaining links are moved to the front of its `LinkedStairs` array and the array's length is lowered, its buffer staying allocated and being freed by the game as before. `StairsUtility.SetupLinks` is then called on the stair, which refreshes its visual and marks the pathfinding dirty without removing anything, since it keeps a stair's old links and only adds those found one floor away at the same place; `UpdateCanBuildStair` refreshes its build buttons. This repairs a save converted before 0.23.3, and does nothing in any other save.

The unlock comes last. Once the loading has set up the interface (`DungeonGameMode.InjectUI`), and if floor 4 is unlocked, each new floor is unlocked as in a new game when floor 4 unlocks (`FloorInsertion.UnlockInserted`), which also raises the minion limit by one per floor, as `FloorUtility.UnlockFloor` does for every floor.

A new floor that already has entities, or that has no floor data, is left alone, so a second load never builds twice. Saving the game writes the ordinary marker, without the `build` line. Until then, loading the converted save again builds the floors again from the unchanged file.

## Known consequences

The dungeon and the tavern are not connected until stairs are built through the new floors. A link kept from floor 4 straight to floor 10 would not connect them: the game's pathfinding throws on every search that crosses a link between floors that are not adjacent, so the conversion drops it. The stair on floor 4 can then be built upward floor by floor with the game's stair buttons, through floors 5 to 9. The stair kept on floor 10 stands at the place of the floor 4 stair's other half, so a stair built on floor 9 at that place should be linked to it by the game when it is set up (`SetupLinks` runs on every new stair); whether it is was not checked.

The new floors get no default dirt (`DungeonGameMode.AddDefaultFloorDirtiness` runs for new games only) and start clean.

Things in motion on floors 5 and 6 when the save was made keep their state with the new numbers. A behaviour tree or action holding a floor or a position in a field the script does not list would still point at the old floor; whether any does was not checked.

Log lines start with `[Floors] converted save`.
