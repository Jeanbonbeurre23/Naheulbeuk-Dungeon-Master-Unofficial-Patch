# Maximum stock of the workshop resources

Plugin 0.20.1, 2 October 2026, game 1.8 (first version 0.19.0; this version corrects the order of the steps). Setting `Economy.ResourceMaximum` (10000 by default, 0 to turn it off).

## What it does

Weapons, magic, tools, intel and corpses can each be stocked up to 10000, where the game stops at 1000. The gauge steps keep the stock at which they start in the game, so grade 6 weapons still need 830 units, and a stock above 1000 only adds storage. Added on 2 October 2026, with the steps kept at the same stock.

## The game's rule

Read from the game's code and configs bundle on 2 October 2026. Each workshop resource has a gauge on the player entity. The gauge's maximum, `Gauge.MaxValue`, is 1000 for all five (`m_gaugeData.m_maxValue` in `ArmamentResourceConfig`, `MagicResourceConfig`, `ToolsResourceConfig`, `IntelResourceConfig` and `CorpsesResourceConfig`). `ResourceUtility.RecomputeResourceGaugeLimit` sets the stock limit to the capacity of the storage furniture, lowered to that maximum, and the gauge counts as full when the stock reaches the limit (`IsGaugeFull`).

The gauge steps are percentages of the maximum (`GaugeStep.PercentageMin` and `PercentageMax`, 0 to 100). The weapons gauge has six, one per grade, from grade 1 at 0 % to grade 6 at 83 %. Intel has three, which set the raid reach, and corpses have six, which set the undead damage. Magic and tools have none. Food has no maximum, only the capacity of its storage furniture.

The gauge does not use the configuration's list of steps directly. `GaugeConfig.SetupSteps` copies it into the gauge component (`Steps`, with the value of each step in `ValueByStep`) and sorts the copy by `PercentageMin`, lowest first, with a comparison that subtracts the two minimums (`<SetupSteps>b__2_0`). The configurations list the highest step first, so the copy runs in the reverse order. `GaugeUtility.HasChangedStep` relies on the sorted order: when the gauge leaves its current step upward it looks at the following steps, and downward at the preceding ones. When that search finds nothing it logs "Could not find any steps when changing step for gauge (p - p %)" and the gauge keeps its step.

## How the patch works

`Economy/ResourceMaximum.cs` runs when a world appears. For each of the five resources it sets the base value of `Gauge.MaxValue` to the setting and marks it for recomputation, so modifiers the game adds to it still apply. It rescales each step by the game's maximum over the new one, so that a step starting at 83 % of 1000 starts at 8.3 % of 10000, that is at the same 830 units; the top step still ends at 100 %, so grade 6 now runs from 830 to 10000. Each step of the gauge is matched to the configuration step it was copied from by its name (`GaugeStep.Name`, "GRADE 6" for instance), and by its rank in the sorted order when a name is missing or repeated. It then runs the game's recompute of each limit, which the storage capacity patch (`docs/features/storage-capacity.md`) multiplies by its factor before the new maximum caps it. The game's maximum and steps are read from the resource configurations once per session, before anything is changed, and every later world is set from those values, so loading another save gives the same result.

The gauges are saved with the game. With the setting at 0, or below the game's maximum, the patch writes the game's maximum and steps back when a world appears, which gives a save played with a higher maximum its normal gauges back.

## Log

`[Resources]` when a world appears, one line per resource: the maximum before and after, and the steps by stock in the gauge's order with their names (for weapons "GRADE 1 0-170, GRADE 2 170-330, …, GRADE 6 830-10000"), with a note when a step was matched by rank. A line when the save already held the new maximum, and a line when the game's values are written back.

## Limits

With the storage factor at 10, a stock of 10000 needs storage furniture that would hold 1000 units in the game; with less, the limit is ten times what the furniture holds. The gauges on screen fill in proportion to the new maximum, so 1000 units now show as a tenth of a gauge. Whether any rule other than the steps reads the stock as a fraction of the maximum (selling, the AI's choice of what to craft, `AreAllGaugesFull`) was not read in full. The gauge's steps are a sorted copy of the configuration's (see above); the configuration itself is not changed.

## Correction in 0.20.1

Plugins 0.19.0 and 0.20.0 wrote the steps by position, taking the configuration's order to be the gauge's. The gauge holds them in the reverse order, so each range went to the opposite step. For weapons, the step of grade 1 held 830 to 10000 units and the step of grade 6 held 0 to 170. For intel the reach levels were reversed, and for corpses the damage tiers. The ranges were then in decreasing order, which broke the game's search for the next step: once a gauge had moved away from the step it held at loading, it stayed on that step and logged "Could not find any steps when changing step for gauge" every frame. Those lines appeared in testing on 2 October 2026, with values of 8 % and 0.65 % (800 and 65 units). While those versions were installed, the grade given to guards hired for lockers, the raid reach and the undead damage could follow the wrong step. Version 0.20.1 matches the steps by name, and the game's search then finds the right step from wherever the gauge stands.
