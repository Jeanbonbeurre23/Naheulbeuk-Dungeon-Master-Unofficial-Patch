# Storage capacity

Plugin 0.9.0, 28 September 2026, game 1.8. Settings `Economy.StorageCapacity` (on by default) and `Economy.StorageCapacityFactor` (10 by default, from 1 to 100).

## What it does

Every piece of storage furniture holds the factor times the capacity the game gives it. This covers the stores of the five workshop resources (weapons, magic, tools, intel, corpses) and the food stores. With the default factor, a shelf that held 50 units holds 500.

The aim, set on 28 September 2026, was a drastic increase of storage. The factor of 10 is the default because the aim was an order of magnitude rather than a number, and the setting changes it.

## Workshop resources

Each resource type has a gauge (`ResourceGauge`) on the player entity (`GameData.PlayerEntity`). `ResourceUtility.RecomputeResourceGaugeLimit` adds up `ResourcesCapacityComponent.Capacity` over the storage props of that type, leaving out a prop whose worker task (`WorkerTaskComponent`) is new or a destruction order. It writes the sum to `TotalLimit` and to `CurrentLimit`, then lowers `CurrentLimit` to the gauge's maximum (`Gauge.MaxValue`) when the sum exceeds it. The stock never goes above `CurrentLimit` (`ChangeRawResourceAmount`), and production that does not fit goes to the sale buffer and is sold (`AddProducedResourceAmount`, `AddResourceToSellToBuffer`). The game runs the recompute when a storage prop is built (`UpdateBuildEventsSystem`) and when one is destroyed (`DecreaseResourceGaugeLimit`, which then sells the stock above the new limit).

A postfix on the recompute multiplies the sum by the factor, then applies the same maximum. The recompute starts from the props every time, so a shelf built before the patch was installed counts like one built after. When a game starts or a save is loaded, the patch runs the recompute once for each resource type, and the log gives, per type, the sum from the storage furniture, the multiplied sum, the game's maximum and the resulting limit.

The gauge's maximum is not raised. The gauge steps (`GaugeStep.PercentageMin` and `PercentageMax`, with the states each step applies to minions) are percentages of that maximum, and raising it would move every step. A resource whose multiplied capacity exceeds the maximum stops at the maximum, and the log says so.

## Food

`FoodResourceDatasComponent.FoodDatas`, on the player entity, holds per food type the stock (`CurrentValue`) and the capacity (`MaximumValue`). The capacity starts at 0 and the game keeps it by increments. `FoodUtility.AddCapacityToFoodResource` adds a prop's capacity when a prop with a `FoodStorageConfigComponent` finishes building (`AddCapacityToAllFood` calls it for every type for a store that takes any food). `RemoveCapacityToFoodResource` subtracts it when such a prop is destroyed (`EntityUtility.DestroyProp`, for a prop with no worker task or with a finished one that is not a copy made for destruction), and lowers the stock to the new capacity.

A prefix multiplies the capacity passed to both functions by the factor. A save made without the patch, or with another factor, holds capacities counted at another scale, and subtracting ten times a shelf that had been added once would leave too little. So when a game starts or a save is loaded, the capacity of each food type is set to the sum, over the food storage props that count, of each prop's capacity times the factor, and the increments keep it from then on. A prop counts when it has no worker task, or a finished one, or a destruction order, and is not a copy made for destruction. The log gives, per food type, the sum from the props, the multiplied sum, the value the save held and whether that value matched the game's own count.

## Consequences in play

Workshop production that fits in storage is kept instead of being sold automatically, so the gold earned from automatic sales drops until the stores fill, and the Bilan with it. The stock panel still sells resources on request. With more stock kept, a gauge reaches its higher steps with fewer shelves. Cooks keep cooking until the larger food stores are full.

## Turning it off

With the factor set to 1, the resources behave as without the patch, and loading a save sets its food capacities back to the game's own sums. With `StorageCapacity` turned off, the resource limits return to normal at the next storage built or destroyed, but food capacities saved at the multiplied scale stay in the save. Setting the factor to 1, loading the save and saving it once before turning the setting off avoids that.

## Limits

- The panel of a single piece of furniture, if it shows a capacity, shows the game's value, not the multiplied one.
- The rule deciding which food stores count at load time is inferred from `DestroyProp` and the build event, not read from a single function of the game. The log line for each food type says whether a save made without the patch matched it.
