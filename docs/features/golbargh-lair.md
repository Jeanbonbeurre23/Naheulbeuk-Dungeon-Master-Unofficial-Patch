# The Golbargh disturbed only by people in his lair

Plugin 0.24.4, 5 October 2026, game 1.8 (first version 0.19.0; this version gives deaths back to the game). Setting `Balance.GolbarghLairOnly` (on by default).

## What it does

The Golbargh's patience falls faster only for the people who stand in his lair, and no longer for everyone on his floor. Deaths follow the game's rule: each death on his floor, in his lair or not, refills his patience.

Added on 2 October 2026 with deaths limited to the lair as well, on the reading that a death on the Golbargh's floor upset him. The game's state for a death fills his patience instead: `AS_State_Patience_EntityOnFloorDied` modifies the patience gauge once by a damage of -60, and `UpdateDamageOverTimeSystem.Run` subtracts the damage from the gauge. Since 0.24.4, deaths are left to the game, and the crowd rule stays.

## The game's rule

Read from the game's code, configs bundle and French texts on 2 and 5 October 2026. The Golbargh's patience gauge has a maximum of 60 and loses 0.08 a second. His lair's prestige gives back between 0.04 and 0.07 a second, and his throne and food store each give 0.125 a second while he uses them. Every minion except Zangdar and Reivax, and every adventurer, who enters his floor is added to his `EntitiesOnFloorComponent` (`UpdateChangeFloorEventSystem`), and removed when he leaves the floor or dies. Three `ConditionalStatsModifierConfig` entries of type `NUMBER_OF_ENTITIES_ON_FLOOR`, at 6, 12 and 18 people, apply the states `ST_CB_G_TooMuchEntities_A`, `B` and `C`, which add 0.01, 0.023 and 0.09 a second to his patience loss. Their emote reads "Quel est ce bruit !? Zangdar !!". `UpdateConditionalStatsModifierSystem.ShouldApplyConditionalStats` compares each threshold with the count from `ComputeNumberOfEntitiesOnFloor`, which counts the members of that set, the Golbargh himself excluded (`IncludeStateOwner` is 0 in the three entries).

When someone on his floor dies, `DeathUpdateSystem.UnregisterFromGolbarghFloor` removes him from the set, checks that he was on the Golbargh's floor, and applies to the Golbargh `AS_State_Patience_EntityOnFloorDied`, which adds 60 to his patience once.

## How the patch works

`Balance/GolbarghLair.cs` has two hooks. A postfix on `ComputeNumberOfEntitiesOnFloor` replaces the count, when the entity asking is the Golbargh, with the number of people of the same kinds who stand on a square of a `GOLBARGH_LAIR` room: minions other than Zangdar and Reivax, and adventurers, alive and inside the dungeon. A person's square is his `GridCoordinatesComponent` on his `GridFloorComponent`, and the lair's squares are the squares whose `RoomTileComponent` names a lair room. The lair is rebuilt every 10 seconds, so a lair built or extended during play counts within that time, and the count is redone at most twice a second. A prefix on `UnregisterFromGolbarghFloor` logs each death on his floor and changes nothing.

Plugins 0.19.0 to 0.24.2 also had a prefix on `StatesUtility.ApplyState` that skipped the death state for deaths outside the lair. 0.24.4 removes it.

## Log

`[Golbargh]` when the lair changes (rooms and squares), each time the count changes (the number of people the game counts on his floor, and the number in his lair that is now counted instead), and for each death on his floor, with `in his lair` or `outside his lair`, followed by `the game refills his patience`. An error is logged once, and the game's own rule then applies.

## Limits

The patch does not change his own loss of patience, his interactions with adventurers, or the other states of `GolbarghPatienceInformationsConfig`: `DemonDiedState` takes 10 from his patience when one of his demons dies, wherever it happens, and `DemonBecomesHostileState` adds 5. The count takes everyone of the counted kinds who stands in the lair; a person who appears there without entering the floor the usual way (a summon, for instance) would count, where the game, which counts its set, would not. Whether a walking character's `GridCoordinatesComponent` follows him square by square, or only at the end of a move, was not checked; if it lags, the count lags with it.
