# Maximum number of minions

Plugin 0.19.0, 2 October 2026, game 1.8. Setting `Balance.MaximumMinions` (500 by default, 0 to turn it off).

## What it does

The dungeon holds up to 500 minions, or the game's own maximum when that is higher. Added on 2 October 2026.

## The game's rule

Read from the game's code on 2 October 2026. The maximum lives in `MinionManagerComponent.MaximumNumberOfMinions`, a single component of the world, saved with the game (it has a save formatter). `MinionUtility.SetMaximumNumberOfMinions` sets it to the sum of the minion places of every unlocked floor, and `IncrementMaximumNumberOfMinions(floorID)` adds the places of one floor. The game calls the increment when a game starts (`GameInitSystem.Init`) and when a floor is unlocked (`FloorUtility.UnlockFloor`). `MinionUtility.HasEnoughPlaceForMinions` compares the number of minions, plus the places taken by guard lockers, plus the minions to add, with the maximum.

## How the patch works

`Balance/MinionCap.cs` keeps the game's own maximum aside and writes the larger of it and the setting into the component. A prefix on the increment puts the game's value back before the game adds a floor, so the floor is added to the game's value and not to 500, and a postfix on the increment and on `SetMaximumNumberOfMinions` records the new game value and writes the larger one again. Every 2 seconds the patch checks that the component still holds that value, and when the game has changed it in some other way, it takes the new value as the game's. When a world appears, the patch runs `SetMaximumNumberOfMinions` once, so the game's value is recomputed from the unlocked floors even when the save holds a value the patch wrote earlier. The prefix and the postfixes act only on the world the patch follows, so a save that is loading does not receive a value meant for the previous one.

With the setting at 0 the hooks are not installed, but the recompute still runs once per world, so a save played with a higher maximum gets the game's own back when it is loaded. This needs the plugin's world reading, which the default settings turn on.

## Log

`[Minions]` when a world appears: the maximum the save held, the game's maximum from its unlocked floors, and the maximum now in force. A line each time the patch writes a new value, with the game's own. An error is logged once.

## Limits

Only the maximum is changed. Every other limit the game puts on a minion still applies: beds, canteen places, salaries, the rooms each one needs, and the population counts that other rules read (the Golbargh's crowd, morale states tied to crowded rooms). Whether a part of the interface shows the maximum from somewhere other than this component was not checked.
