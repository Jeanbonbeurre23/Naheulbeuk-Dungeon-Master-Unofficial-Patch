# Minion columns

Plugin 0.8.0, 27 September 2026, game 1.8. Settings `Management.MinionColumns` (on by default) and `Management.MinionColumnGroups`.

## What it does

The management tab of the Minions window is widened and shows the minions in one column per group of origins: Greenskins (green), Humans (blue), Elves (pink), Drows (black), and a fifth column, Others, only when a minion fits none of the four. Each column has a coloured header with the group's name and its number of minions, and scrolls on its own. The minions keep the game's own slots, with portrait, name, salary and task, and a click selects the minion, opens his sheet and moves the camera to him as before. The filter wheel and the sort toggles at the top of the tab still apply, within each column.

Design aim, 27 September 2026: a much wider window with at least four columns, greenskins, humans, elves and drows (green, blue, pink, black).

## Groups

The default groups are Greenskins: orc, goblin, troll; Humans: human, dwarf, barbarian; Elves: elf; Drows: drow, vampire. Greenskins follow the game's own test (`FilterUtility.IsGreenSkin`: orc, goblin, troll). Dwarves and barbarians with the humans, and vampires with the drows, are the patch's choice, not the game's; skeletons, zombies and demons go to Others. The setting `MinionColumnGroups` changes the groups: `Name:ORIGIN,ORIGIN` separated by semicolons, left to right, with colours in the order green, blue, pink, black. The origins are the game's `OriginType` values: HUMAN, ORC, SKELETON, DWARF, ELF, GOBLIN, DROW, ZOMBIE, BARBARIAN, TROLL, DEMON, VAMPIRE.

## How it is built

The window is `MinionManagementPage` (prefab `UI_NPCManagementMenu`). Its `RootAnim` is 1040 units wide out of the 3840 of the reference resolution, and the character sheet (`UI_NPCManagement`, 1004 units wide) hangs off its right edge. While the management tab is shown, the patch sets `RootAnim` to 2800 units and the tab (`ManagementTab`) to 2720, which leaves room for the sheet on a 16:9 screen; the width is set again after each frame's animations. The other tabs (recruitment, necromancy, teleportation, VIP) keep the game's width.

The game's list (`CharacterList` on `ManagementTab`, a `BaseEntityList`) is virtualised: it instantiates only the slots in view and recycles them while the list scrolls, so it cannot be split into columns. The patch leaves it running and hides its scroll view (`RootScrollView`), then builds its own view in the same place (`Management/MinionColumns.cs`):

- the minions to show are the list's own (`BaseEntityList.Entities`), already filtered and sorted by the game;
- each minion's column comes from his origin (`OriginComponent`);
- each minion gets a slot made from the list's slot prefab (`BaseEntityList.m_slotPrefab`) and set up by the list itself (`CharacterList.SetupSlot`), with the page's callbacks: a click goes to `BaseManagementPage.OnSlotSubmit` exactly as a click on the game's slot;
- slots are scaled to the column width (800 by 190 units at full size) and the selected minion's slot is highlighted (`CharacterListSlot.SetSelected`, from `BaseManagementPage.SelectedEntity`);
- the view is compared with the list four times a second and follows hiring, departures, filtering and sorting.

During a room pick from the rooms page of the sheet (`RoomPicker`), the window returns to the game's width and list so that the dungeon is visible, and goes back to the columns afterwards.

## Limits

- The window covers the dungeon while the management tab is open.
- Gamepad navigation between columns has not been designed; the game's own navigation handles the slots as it finds them.
- Every minion has a live slot, where the game's list keeps about ten; a large dungeon may cost some frames while the window is open.
- Group names are in English.
