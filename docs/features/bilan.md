# Bilan

Plugin 0.7.0, 27 September 2026, game 1.8. Setting `Economy.Bilan` (on by default).

## What it does

The resource counter at the bottom right of the dungeon screen (guards, minions, gold, Dépenses, decade) gains an element right of Dépenses: the Bilan, the gold earned over the last decade of game time minus the Dépenses figure. It shows a signed number, green for a benefit and red for a deficit, next to the counter's gold icon. After a new game or a loaded save, only the days played since are covered; until a whole decade has been observed the Bilan is dimmed, and the resource bar at the top of the screen says how many days are covered.

Design choice, 27 September 2026: earnings minus Dépenses, construction, hiring and market purchases left out as one-off investments; not all gold in minus all gold out, nor the game's accounting of the last completed decade.

## Dépenses

Dépenses is the counter's charges element (`ResourceBar.m_chargesText`, object `UI_SalaryCost`). `ResourceBar.GetCharges` takes its value from `EconomyUtility.GetChargesCost`, which adds the decade's salaries (`SalaryManagerComponent.CurrentSalarialCosts`), the tavern's costs (`TavernUtility.CalculateTavernCosts`) and the floors' decade costs (`FloorUtility.GetTotalFloorsDecadeCosts`). It is the fixed cost of the coming decade, and the Bilan reads it from the same function.

## Earnings

Every payment of gold to the player goes through `EconomyUtility.GiveGoldAmount`. Its callers in 1.8 are the tavern (`TavernUtility.PayConsumption`, `PayConsumptionTask`), resource sales (`ResourceUtility.SellResources`), raids (`RaidsUtility.ApplyRaidConsequences`), quest consequences (`ConsequenceUtility.CheckGoldConsequence`), adventurers (`Adventurer.DeleteGroupTask`, `AdventurerIncursionPage.InitTarget`), construction (`Builder`, `BuilderHistory`), furniture sold from its panel (`PropDetailsPage.OnButtonSell`) and the start of a new game (`GameInitSystem.Init`).

A prefix on `GiveGoldAmount` (`Economy/Bilan.cs`) records the amount as earnings in the buckets of the resource bar (`ResourceFlows`, 240 buckets per decade of calendar time, emptied on a new game or a loaded save). Gold given while one of these runs is left out: `Builder.ValidateRoom`, `ValidatePropsInCorridor`, `DeleteRoom`, `UpdateDeletedPropCost`, `CancelRoom`; `BuilderHistory.Undo`, `UndoAll`, `UndoForWallsBlueprint`; `PropDetailsPage.OnButtonSell`; `GameInitSystem.Init`. They give back construction money, sell furniture or give the starting gold. `BuilderHistory.UndoDelete` takes a game struct its hook could not convert (see `Core/HookSafety.cs`), so its callers are hooked instead.

## How the element is built

The element is a copy of the Dépenses element, placed right of it with a copy of the separator that follows it. The copy loses its tooltip trigger (`EventTrigger`) and its tutorial identifier (`RectTransformIdentifier`), and takes the gold icon of the counter (`UI_Cost/IcnValue`). Its number is shown by the game's own `ResourceText`, with `ForceSign` and `UseGreenRedColor` set, so it animates changes as the counter's other numbers do.

## Limits

- The Bilan has no tooltip.
- Earnings are not kept across a save and a reload.
- Only the counter of the dungeon screen has the Bilan; the copy of the counter in the quests menu does not.
