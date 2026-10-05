# Room-needs panel

Plugin 0.13.0, 30 September 2026, game 1.8. Setting `Management.RoomNeedsPanel` (on by default).

## Why it changed

Versions 0.11.0 to 0.12.2 showed, for each room type, three segments for the dirtiness bands (dirty, middle, clean) and turned a segment red when minions required a band that no room of the type was in. That rested on the reading that a minion refuses rooms outside his origin's band. The game does not refuse them. `AFindRoomTask.HasOneRoomMatchingDirtiness` only sets the order in which rooms are tried, and any room of the type that holds a free, finished prop of the right kind ends up acceptable. On 30 September 2026 the segments were replaced with what does block a need, free props.

## What it shows

One row per room type: dormitory, canteen, bathroom and break room always, then every other type the minions searched in the last 3 minutes. Up to three segments name the kinds of prop the minions looked for in that room type in the last 3 minutes of real time (the kinds with the most minions who found none come first, then the most searched), with the number of minions who found a free one over the number who looked. A segment is green when all found one, amber when some did not and red when none did. A click on a segment shows a minion whose last search for that kind found none, the most recent first, and the next at each further click. The status names the minions whose most recent search in that room type, whatever the kind, found nothing, and the tab counts the room types that have such minions.

The prestige bar is unchanged in form: the level preferred by the most demanding minion whose need the room type does not meet, and a mark at the best room. It no longer counts as a problem, since prestige only narrows the cook's and the training searches and never makes a search fail.

## How it reads the game

A postfix on `AFindRoomTask.FindValidRooms` records, for every player minion's `FindIoInRoomTask`, the room type, the kind of prop (`PropType`), whether a prop was found and in which room, and the time.

## Limits

Several behaviour trees try a second or third kind of prop when the first is not found, and the tavern's entertainment can end with a drink at the counter, which is not a prop search. A red or amber segment for one kind can therefore sit beside a green one that served the same minions. The status, which looks at each minion's most recent search in the room type, is the closer measure of a need left unmet. Bankers appear on the treasure room's row: they look for the reception desk, which only one can hold at a time.
