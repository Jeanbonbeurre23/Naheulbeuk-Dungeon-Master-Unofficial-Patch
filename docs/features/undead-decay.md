# Undead keep their life between hits

Plugin 0.24.6, 8 October 2026, game 1.8. Setting `Balance.UndeadNoDecay` (on by default).

## What it does

Undead (skeletons, zombies, ghosts) lose life only when they are harmed: hits, and states such as poison or burning. In the game they also lose life by themselves until they die: 0.022 life points a second when the compost store is 84 to 100 % full, then 0.031, 0.040, 0.049 and 0.058, and 0.067 below 17 %. The undead's rule text says that more compost makes him survive longer. Demons have no such loss in the game, and the patch does not touch them.

## How the patch works

`Balance/UndeadDecay.cs` puts a prefix and a postfix on `UpdateCorpsesResourceGaugeSystem.Run`, the system that lowers the undead's life. The prefix goes through the system's own filter of undead and notes, for each one, his life gauge's current value and percentage and the damage type of his last damage, read from the two pools the system writes to (`StatisticsUtility.m_lifePointsPool` and `EntityUtility.m_lastDamageTakenPool`). The postfix writes these values back. Everything `Run` did to the undead is undone, and the rest of `Run`, the compost gauge's steps and notification, is left to the game. Hits, states and heals come from other systems and are untouched. Nothing is written to the save beyond the life values the game would have saved anyway.

## Log

`[Undead]` once per loaded game, with the number of undead in the game's filter, then every 5 minutes with the number of undead and the life points of decay undone during those 5 minutes. An error is logged once, and undead then lose life by themselves as in the game until the game is restarted.

## Limits

The rule text of the undead still says that more compost makes him last longer. Compost keeps its other uses. Undead no longer die of age, so they stay until they are killed.
