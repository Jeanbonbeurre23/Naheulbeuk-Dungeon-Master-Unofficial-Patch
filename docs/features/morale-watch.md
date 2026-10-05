# Morale watch

Plugin 0.12.1, 30 September 2026, game 1.8. Part of `Diagnostics.Morale` (on by default). It replaces the guard watch of 0.12.0, which watched guards only.

## Why

A test session of 29 September 2026 recorded 47 resignations, of guards, artisans, cooks, spies and a domestic, most of them a fall from high morale to 0 within a few minutes while all their needs dropped. The 0.11.0 log did not record what these minions were doing, which states were applied to them, or at what speed the game ran.

## What the game does (read from the method bodies and field lists)

A minion's behaviour is chosen by score (`BehaviourTreeOwnerComponent`: available behaviours, their scores, the one running). A gauge falls and rises at the rates of `Gauge.DecreasePerSecondRealTime` and `IncreasePerSecondRealTime`, whose modifiers come from the states applied to the minion. A state carrying a `StatsModifierConfigComponent` adds its stat modifiers (a statistic such as `DECREASE_MORALE_GAUGE`, an operator, a value) to the matching rate, and a state carrying a `StopGaugeModifierConfigComponent` stops the gauges it lists. `GaugeUtility.PauseAllGauges` forces the decrease rate of every gauge to 0, for a wounded minion waiting for a pharmagician, a minion on strike and a minion in a discussion.

## What the watch writes

`Diagnostics/MoraleWatch.cs`, every 5 seconds of real time, for every player minion whose morale fell by 3 points or more since the last sample, or is below 25, one `[MoraleWatch]` line, at most one per minion every 15 seconds and 2000 per session. The line carries his needs and negative states, his morale before and now, the game's time scale, the morale fall and rise rates with their modifiers, the decrease rate of each need (marked "paused" when forced), the behaviour running and the five best-scored behaviours, every state applied to him with its stat modifiers and the gauges it stops, and his room searches of the last 3 minutes (found, every prop taken, or no acceptable room, and how long ago). The searches come from the room-needs records, so they are empty when `Management.RoomNeedsPanel` is off.

Every 5 minutes it writes, per job, the number of minions, their average morale, their average morale fall rate and how many have their needs paused, then the behaviours each job is running, then the behaviours run by the minions whose needs are paused.

## What it does not do

It changes nothing in play.
