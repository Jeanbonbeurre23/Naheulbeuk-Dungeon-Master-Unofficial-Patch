# Trap triggers

Plugin 0.17.0, 1 October 2026, game 1.8. Setting `Balance.TrapTriggers` (on by default).

## What it does

A loaded trap always goes off when an adventurer steps on it, and never when one of the player's minions does. Deceiving traps are included: they go off under any adventurer, where the game sets them off only for the origins they target. Added on 1 October 2026.

## The game's rule

Read from the game's code and configs bundle on 1 October 2026. When a character enters a trap's square, `UpdateTrapTriggerTileSystem.OnStepOnATrap` checks that the trap is loaded and hands the step to `OnMinionStepOnATrap` for a minion or to `OnAdventurerStepOnATrap` for an adventurer. No other kind of character sets a trap off this way.

For a minion, the game draws a number between 0 and 1 and sets the trap off when it is below `TrapConfig.m_minionProbability` (0.01) plus the minion's trap-trigger modifier. The Insouciant trait, present on all 115 elf lines of the 30 September morale watch, adds `TRAP_TRIGGER_MODIFIER_VALUE` 0.05, very probably to that modifier. An elf who escapes the draw gets a second one (`TryTriggerTrapForElf`, `m_elfProbability`, 0.02). Before the draw, the game skips the trap while a relic is exposed.

For an adventurer, the game requires the adventurer to belong to a group. On a deceiving trap, it sets the trap off only for one adventurer of the group, the leader by the order of the system's fields, and only when an origin of the group is one the trap targets (dwarves, probability 1). On any other trap, it draws against `m_adventurerProbability` (0.35). A group with an elf gets the second draw.

## How the patch works

`Balance/TrapTriggers.cs` has two prefixes. The one on `OnMinionStepOnATrap` skips the method, so the draw never happens. The one on `OnAdventurerStepOnATrap` makes the two calls the game makes when a trap goes off under an adventurer, `TrapUtility.TriggerTrap(trap, adventurer)` and `CrawlingStatsUtility.OnTriggerTrap(adventurer)`, then skips the game's method and its draw. The loaded check in `OnStepOnATrap` still applies, so an empty trap does nothing until it is reloaded.

## Log

`[Traps]` every 5 minutes of play when something happened: the number of traps set off by adventurers, and the number of minion steps on loaded traps left without effect. An error is logged once, and the game's own rule then applies to that trap.

## Limits

The trap panel still shows the game's percentage for adventurers (`TrapComponent.AdventurerProbability`, 35 %), since the patch changes the draw and not the stored value. A minion can still be hurt by a trap an adventurer sets off, if the trap's effect reaches the squares around it: the patch changes who sets a trap off, and the reach of the effect (`SkillUtility.UseSkill`) was not read. Adventurers who never step on a trap, because their path goes elsewhere, are not affected.
