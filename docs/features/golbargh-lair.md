# The Golbargh disturbed only in his lair

Plugin 0.19.0, 2 October 2026, game 1.8. Setting `Balance.GolbarghLairOnly` (on by default).

## What it does

The Golbargh's patience falls faster only for the people who stand in his lair, and no longer for everyone on his floor. A death on his floor upsets him only when it happens in his lair. Added on 2 October 2026, with deaths limited to the lair as well.

## The game's rule

Read from the game's code, configs bundle and French texts on 2 October 2026. The Golbargh's patience gauge falls by itself (0.02 a second). Every minion except Zangdar and Reivax, and every adventurer, who enters his floor is added to his `EntitiesOnFloorComponent` (`UpdateChangeFloorEventSystem`), and removed when he leaves the floor or dies. Three `ConditionalStatsModifierConfig` entries of type `NUMBER_OF_ENTITIES_ON_FLOOR`, at 6, 12 and 18 people, apply the states `ST_CB_G_TooMuchEntities_A`, `B` and `C`, which add 0.01, 0.023 and 0.09 a second to his patience loss. Their emote reads "Quel est ce bruit !? Zangdar !!". `UpdateConditionalStatsModifierSystem.ShouldApplyConditionalStats` compares each threshold with the count from `ComputeNumberOfEntitiesOnFloor`, which counts the members of that set, the Golbargh himself excluded (`IncludeStateOwner` is 0 in the three entries).

When someone on his floor dies, `DeathUpdateSystem.UnregisterFromGolbarghFloor` removes him from the set, checks that he was on the Golbargh's floor, and applies to the Golbargh the state `GolbarghPatienceInformationsConfig.EntityDiedOnFloorState` through `StatesUtility.ApplyState(int, StateEntityConfig, int, bool)`, its only call to that method.

## How the patch works

`Balance/GolbarghLair.cs` has three hooks. A postfix on `ComputeNumberOfEntitiesOnFloor` replaces the count, when the entity asking is the Golbargh, with the number of people of the same kinds who stand on a square of a `GOLBARGH_LAIR` room: minions other than Zangdar and Reivax, and adventurers, alive and inside the dungeon. A person's square is his `GridCoordinatesComponent` on his `GridFloorComponent`, and the lair's squares are the squares whose `RoomTileComponent` names a lair room. The lair is rebuilt every 10 seconds, so a lair built or extended during play counts within that time, and the count is redone at most twice a second.

A prefix on `UnregisterFromGolbarghFloor` checks whether the person who died stood in the lair. When the death happened on his floor but outside the lair, a prefix on `ApplyState` skips the call that would apply the state to the Golbargh during that same call, and only that one. The person is still removed from the set, as in the game.

## Log

`[Golbargh]` when the lair changes (rooms and squares), each time the count changes (the number of people the game counts on his floor, and the number in his lair that is now counted instead), and for each death on his floor, in or outside his lair. An error is logged once, and the game's own rule then applies.

## Limits

The patch does not change his own slow loss of patience, his interactions with adventurers, or the other states of `GolbarghPatienceInformationsConfig`. One of them, `DemonDiedState`, is applied to him by `DeathUpdateSystem.OnDeathEntity`, next to the call that removes a demon from his cultist, and does not depend on where the demon dies; the conditions of that branch were not read in full. `DemonBecomesHostileState` was not traced. The count takes everyone of the counted kinds who stands in the lair; a person who appears there without entering the floor the usual way (a summon, for instance) would count, where the game, which counts its set, would not. Whether a walking character's `GridCoordinatesComponent` follows him square by square, or only at the end of a move, was not checked; if it lags, the count lags with it.
