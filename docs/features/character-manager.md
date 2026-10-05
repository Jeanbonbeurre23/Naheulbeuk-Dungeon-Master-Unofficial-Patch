# Character manager

Plugin 0.15.0, 30 September 2026, game 1.8. Setting `Management.CharacterManager` (on by default).

## What it does

The character sheet that opens beside the Minions window (the Minions button among the icons at the bottom left of the screen) gains a third page, "Assigned rooms". The sheet already pages with its two arrows between the identity page (gauges, moods) and the traits page (rules, traits, alterations); the right arrow on the traits page now opens the rooms page, and the left arrow on the rooms page returns to the traits page. The page number reads 3 of 3 while it is shown. The page exists for the player's minions only: unique characters (`UniqueComponent`) and VIPs (`VipTag`) keep the game's two pages, as do the sheet's other modes (recruitment, necromancy, teleportation and the rest).

For the character on the sheet, five rows each show a slot, its current room and two arrows. The arrows cycle through "Game's choice" and the rooms of the matching type, named by type, floor and order of construction, leaving out the rooms forbidden to him. Under each row, two links and a line of text follow. "Pick in dungeon" waits for a click on a room in the dungeon and assigns that room if its type fits the row and it is not forbidden to him; the row reads "Click a bathroom in the dungeon" meanwhile, and the link becomes "Cancel". "Show", present when a room is assigned, moves the camera to it. The line gives the outcome of the last pick for a few seconds, then the outcome of the character's last search for that slot.

| Slot | Room type | Behaviour trees steered |
|---|---|---|
| Job room | Kitchen for cooks, treasure room for bankers | `BT_Cook`, `BT_PaySalary` |
| Dormitory | `DORMITORY` | `BT_Sleep` |
| Bathroom (toilets, showers) | `BATHROOM` | `BT_Pee`, `BT_Shower` |
| Canteen | `CANTEEN` | `BT_EatInCanteen` |
| Break room | `BREAK_ROOM` | `BT_Entertainment_BreakRoom` |

Below the slots, the "Forbidden rooms" section lists the rooms forbidden to the character, one line each, with "Show" and "Lift" links. "Forbid a room" waits for a click on a room in the dungeon and forbids it; the link becomes "Cancel" meanwhile, and the note beside it gives the outcome for a few seconds. A line also gives the time at which the room was last left out of one of his searches, which shows that the ban acts. The section shows four lines at most; with five bans or more, the fourth line counts those not shown, and lifting one brings the next into view. "Lift" removes the ban at once.

An assignment and a ban do not work alike. An assignment is a preference, and the game's choice stands when the assigned room cannot serve. A ban is a rule, and it holds even when every room of the type is forbidden to him, in which case the need that room type serves stays unmet. A room cannot be assigned and forbidden to the same character: forbidding his assigned room clears that slot back to the game's choice and says so, and a forbidden room is not offered for a slot until its ban is lifted. Other characters keep using the room in both cases.

Design choices, 27 September 2026: an assignment is a preference; assigned rooms stay open to every character; the job room row only offers rooms of the character's current job. Design choices, 30 September 2026: bans are set on the character's sheet; a ban holds even when it leaves a need without any room; a ban covers every search the character makes with `FindIoInRoomTask`, and no other search.

## Which rooms a ban covers

`FindIoInRoomTask` is the task by which a character looks for a free prop in a room of a given type. The behaviour trees of the game's `configs_assets_all` bundle, read on 30 September 2026, use it for these room types.

| Room type | Behaviour trees |
|---|---|
| `DORMITORY` | `BT_Sleep` |
| `BATHROOM` | `BT_Pee`, `BT_Shower` |
| `CANTEEN` | `BT_EatInCanteen` |
| `BREAK_ROOM` | `BT_Entertainment_BreakRoom`, `BT_Entertainment_BreakRoom_1` |
| `KITCHEN` | `BT_Cook` |
| `TREASURE_ROOM` | `BT_PaySalary`, `BT_GetSalary` |
| `TRAINING_ROOM` | `BT_Train` |
| `LIBRARY` | `BT_MagicTraining` |
| `PRISON` | `BT_Entertainment_Prison`, `BT_Entertainment_Prison_1` |
| `GOLBARGH_LAIR` | `BT_Entertainment_Golbargh` |
| `TAVERN` | `BT_Drink`, `BT_EatInTavern`, `BT_Entertainment_Tavern` and others |
| `REIVAX_OFFICE`, `ZANGDAR_LOCAL` | branches of `BT_Sleep`, `BT_Pee`, `BT_Shower`, `BT_Entertainment_Protagonist` |

A ban on a treasure room also covers `BT_GetSalary`, in which a minion looks for the treasure room's reception desk (prop type `TREASURE_ROOM_RECEPTION_DESK`) before collecting his salary. A minion for whom every treasure room is forbidden therefore cannot collect his salary, and the page says so when a treasure room is forbidden. No `FindIoInRoomTask` of the bundle searches a workshop, a garden, an armory, a laboratory or a corridor, and the page refuses a ban on one of those, since it would change nothing; crafting uses `FindIoAndResourceForCraftTask`, which a ban does not cover.

## How the page is built

The sheet is the game's `MinionDetailsTooltip` component (prefab `UI_NPCManagement`, docked to the right of `UI_NPCManagementMenu`). In `MANAGEMENT` mode it keeps its pages in `m_contents`, indexed by `EContent` (`MANAGEMENT_1`, `MANAGEMENT_2`), and lists the pages available for the current character in `m_enableContents`. Its arrows call `NextContent` and `PreviousContent`, and their state follows `NextContentEnableCheck` and `PreviousContentEnableCheck`.

The rooms page (`Management/RoomsPage.cs`) is built once per sheet, the first time it opens. Its title and texts are clones of the traits page's `TitleRules` and `TextAlteration` texts, with the game's localisation component removed so it does not replace the English labels, and its arrows are new buttons drawn with the sprites of the sheet's paging arrows. It takes the rectangle of the traits page and sits above it in the same parent. By the sizes in the prefab, and assuming a canvas 2 160 units high (its root is 3 840 units wide), the traits page's rectangle is about 1 175 units high, and the rooms page with its Forbidden rooms section needs about 1 040. The fit on screen was not checked in this version.

Harmony hooks on the sheet add the page to the paging. `NextContentEnableCheck` returns true on the last game page for an eligible character, and false on the rooms page. `NextContent`, on the last game page, opens the rooms page instead of doing nothing. `PreviousContent`, on the rooms page, closes it instead of moving the game's page. `SwitchContent`, `SetTarget`, `SetMode` and `OnDisable` close it, so a new character, a new mode or a closed window never leaves it open. While the page is shown, the game's current page stays active and animated, and its `CanvasGroup` is set transparent and unclickable after each frame's animations; its values are restored when the page closes.

## How picking and showing work

A click in the dungeon reaches `ObservationController.OnValidate`, which raycasts under the pointer and passes the entity hit, with its `EntityRef`, to `OnEntityClicked`; that method selects the entity and opens its panel (for a room, `RoomDetailsPage`). `OnValidate` skips the click when the pointer is over the game's interface, so a click on the part of the dungeon that the Minions window leaves visible reaches it. While a pick is under way, for a slot or for a ban, a prefix on `OnEntityClicked` (`Management/RoomPicker.cs`) takes the click: the game selects nothing and opens no panel, and the Minions window stays open. The entity hit is the room when the player clicks its floor (`EntityRef` type `ROOM`); a wall (type `WALL`) is turned into its room with `RoomsUtility.GetRoomEntityFromWall`, as `ObservationController.ClickEntity` does, and a prop, an item or a prison cell into the room it stands in, through its `ParentsComponent`. A click on a character, on a room of the wrong type, or on a room a ban cannot cover leaves the pick under way and says why. Closing the page, changing character or closing the window cancels the pick.

"Show" calls `CameraController.SetEntityCoords` on the room, as `ObservationController.OnClickedRoom` does when the player clicks a room. When the room is on another floor than the one displayed (`DungeonPage.m_currentFloor`), it also calls `DungeonPage.SetDesiredFloor` with the room's floor index and moves the camera again once the floor has changed.

## How the game chooses a room

Each behaviour tree above finds its target prop with `FindIoInRoomTask`, whose `OnExecute` calls `AFindRoomTask.FindValidRooms(RoomType)`. That method first calls `HasOneRoomMatchingDirtiness` and gives up when it returns false. `HasOneRoomMatchingDirtiness` fills `m_availableRoomList` with the rooms of the type in one cleanliness band after another, in an order set by the character's origin, and each time has `CheckAvailableRoomList` remove every room for which the task's `IsRoomValid` returns false; `FindIoInRoomTask.IsRoomValid` keeps a room that holds a free, finished prop of the kind wanted. Dirty origins (orc, goblin, troll) try dirty rooms first, then middle, then clean; neutral origins (human, dwarf, drow) try middle or clean rooms, then dirty; clean origins (elf, vampire) try clean rooms, then middle, then dirty. The first band that keeps a room is the one searched, so cleanliness sets the order of the rooms tried and never makes a search fail by itself. When the task checks prestige, `FindValidRooms` then narrows the list to the rooms at the prestige the character expects, and keeps the whole list when none is. `SearchInRoom` runs on what is left and leaves the chosen prop in `m_foundPropEntity`, which `OnExecute` writes to the behaviour tree before the character reserves it.

## How the steering works

A postfix on `FindValidRooms` (`Management/RoomSearchRedirect.cs`) acts only for a character with an assignment of the searched type. If the game chose a prop outside the assigned room, and the assigned room is among the rooms the game kept for this character, the task's own `SearchInRoom` runs on the assigned room alone. A free prop found there replaces the game's choice; otherwise the game's choice is restored. The assigned room is left aside when the game searched another cleanliness band first, when it is below the prestige he expects, or when it has no free prop. The log records each outcome at most once a minute per character and slot.

Other jobs find their place with other tasks: crafting (`FindIoAndResourceForCraftTask`), cleaning (`FindDirtinessPositionTask`), serving drinks (`FindWaitingLineToServeTask`), working behind the counter, guarding, torturing. No job room can be assigned to them in this version, and their job row reads "Not available for this job yet".

## How a ban works

A prefix on `FindIoInRoomTask.IsRoomValid` (`Management/ForbiddenRooms.cs`) returns false for a room forbidden to the searching character (the task's `m_entity`), so `CheckAvailableRoomList` removes the room from the list before cleanliness and prestige are weighed. The game then chooses among his other rooms by its own rules. When every room of the type is forbidden, or none of the others has a free prop, `HasOneRoomMatchingDirtiness` finds no band with a room and the search fails, as it does when no room of the type has a free prop. The base method `AFindRoomTask.IsRoomValid` returns true without looking at the room and is not hooked; the hook acts on `FindIoInRoomTask`'s own override, which the other search tasks (crafting, training spots, coaching, torture cells) do not share.

The check made for each room of each search is one lookup in a set of (character, room) pairs, and the prefix returns at once while no ban exists. A ban belongs to one character and one room, both identified by entity id and generation, so a ban does not carry over to a new character or a new room that reuses an id. The log records each room left out of a search at most once a minute per character and room.

## Where assignments and bans are kept

When the game writes a save, the patch writes `AppData\LocalLow\Artefacts Studio\NDM\NDMUnofficialPatch-data\<save name>.assignments.tsv`, beside the Save folder and never inside a save. A ban takes the word `Forbidden` in the slot column. When a save is loaded, the file is read and each line is applied once the characters exist: a character is matched by entity id and name, or by name alone; a room by id, type and floor. Unmatched lines are dropped and counted in the log. Plugin versions before 0.15.0 drop the ban lines as unreadable and keep the assignments.

## Settings, dependencies and limits

- `Management.CharacterManager` (on by default) adds the page, the steering and the bans.
- The page has no dependency beyond BepInEx.
- The page's labels are in English whatever the game's language.
- Floors are shown as the game's internal floor index plus one.
- In the labels, rooms are told apart by type, floor and order of construction only; picking and showing are the ways to relate a label to a room.
- The camera centres the room on the screen, and the Minions window may cover part of it.
- A ban keeps a character from looking for a prop in the room. It does not keep him from walking through it, and tasks other than `FindIoInRoomTask` (cleaning, guarding, crafting, a fight) can still bring him there.
- A minion whose searches fail because of bans appears on the room-needs panel as one whose need is unmet, as he would with no room at all.
