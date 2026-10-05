# Resource bar

Plugin 0.5.0, 27 September 2026, game 1.8. Setting `Economy.ResourceBar` (on by default).

## What it does

A bar at the top centre of the dungeon screen shows an icon for each workshop resource (weapons, magic, tools, intel, corpses) and each food type (meat, soup, sweets, leftovers). Under each icon, "produced / consumed" gives what the dungeon produced and consumed of it over the last decade of game time, which is ten in-game days. The figures are green when production covers consumption, red when it does not, and in the game's gold when nothing was measured. A workshop resource the game has not unlocked is left out, and so is cheese, which has no line on the game's own food page, unless something was measured for them.

The decade slides: the figures always cover the last ten days before the current game time and change continuously. After a save is loaded, or a new game started, only the days played since then are covered, and a caption at the end of the bar says how many ("last 3.2 of 10 days") until ten days have passed.

Design choices, 27 September 2026: workshop resources and food, not gold; measured flows over a sliding decade, not the last completed decade nor the game's own food forecasts.

## What counts as produced and consumed

The workshop resources live in gauges handled by `ResourceUtility`. The patch hooks the four places where their quantities change (`Economy/ResourceFlows.cs`).

- `AddProducedResourceAmount(type, amount)` is every production: crafting in workshops (`CraftResourceActionSystem`), burials (`BuryCorpseActionSystem`), torture (`TortureEntityActionSystem`) and raid loot (`RaidsUtility`). The amount counts as produced. The game stocks the share not set aside for automatic sale, up to the gauge's limit, and puts the rest in a sale buffer.
- `ChangeRawResourceAmount(type, delta)` is every other change of the stock, and returns the change made after clamping between zero and the gauge's limit. A negative change counts as consumed: raids, invocations, spells, teleports, trap enchanting, thefts by adventurers. A positive change counts as produced: resources bought at the market, invocation costs refunded, stolen goods given back, quest rewards.
- `SellResources(type, count)` is every sale, automatic (from the sale buffer) or made from the stock panel. The count counts as consumed. `SellResources` pays the gold and records the sale without touching the stock; a sale from the stock panel (`StockPopup.OnButtonSell`) first subtracts the count from the gauge itself, and an automatic sale takes it from the buffer, which was never stocked.
- Changes made inside `AddProducedResourceAmount` are already counted as production. When a storage is destroyed, `DecreaseResourceGaugeLimit` removes the stock above the new limit and puts it in the sale buffer; that stock counts as consumed when it is sold, not when it is removed.

Selling therefore counts as consumption: a resource set to be sold automatically at 100 % shows equal production and consumption. For every workshop resource, produced minus consumed over a period equals the change of the stock over that period, up to the fractions of a unit waiting in the sale buffer.

Food is added by `FoodUtility.AddAmountToFoodResource` (cooking in `CookActionSystem`, returned stolen food, quest rewards) and removed by `FoodUtility.RemoveAmountToFoodResource` (meals taken in `PickConsumptionTask`, thefts in `StealFoodTask`). Food lost when a storage is destroyed (`RemoveCapacityToFoodResource`) is not counted.

## Time

Game time is the calendar's date (`CalendarManagerComponent.Date`, the date `UpdateCalendarSystem` advances and compares with the previous decade's date), converted with `DungeonDateTime.ToMilliseconds`. A decade (`DungeonDateTime.ONE_DECADE`) is cut into 240 buckets of one in-game hour; each event is added to the bucket of the moment it happened, and the bar sums the last 240 buckets. The buckets are emptied when the game world changes (a new game, a loaded save) or when the date goes back.

## How the bar is built

The bar is a child of the dungeon HUD (`DungeonPage`, prefab `DungeonHUD`, reference resolution 3840 by 2160), so it scales and hides with the game's own HUD. Its background, its separator and its text style are copied from the game's resource counter at the bottom right (`ResourceBar`, `UI_ResourcesInfos`, text `TextDecade`). Nothing in it is a raycast target, so it never takes a click from the dungeon. Workshop icons come from `UIGameConfig.ResourceIcons`; food icons are the sprites of the game's food page (`ICN_Food_Meat`, `ICN_Food_Soup`, `ICN_Food_Sweet`, `ICN_Food_Waste`), found by name among the loaded sprites. A cell whose icon is not found shows the resource's English name. Unlocked resources are read from `ResourceUtility.IsResourceUnlocked`.

## Limits

- The figures are not kept across a save and a reload.
- Names are in English whatever the game's language, and only appear where an icon is missing.
- The bar has no tooltip; the split between use, sale and theft is not shown.
