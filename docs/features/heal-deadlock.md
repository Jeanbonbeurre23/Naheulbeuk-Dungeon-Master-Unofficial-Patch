# Heal deadlock

Plugin 0.16.0, 1 October 2026, game 1.8. Setting `Fixes.HealDeadlock` (on by default).

## The problem

A wounded minion carries a `RequestHealComponent`. `AIComputeRequestHealScoreJob` gives REQUEST_HEAL a raw score of 1 to every minion that carries one, as long as an available dormitory holds a `DORMITORY_HEALTHCARE` prop, and the behaviour's curve (`AIEvaluator`) turns it into 0.9, above every need and every job's work (0.5, HEAL included). `BT_RequestHeal` finds a bed (`FindSimpleBedTask`), walks to it and waits (`RequestHealActionSystem`) until a pharmagician has healed him, or until `MinionUtility.IsTherePharmagicianInDungeon` returns false. That method returns false only when no pharmagician other than the minion himself exists (every entity with `AIComputeHealScoreComponent`, not dead, not outside the dungeon). A pharmagician waiting in bed still counts. When every pharmagician is wounded, each one waits for another and nobody heals anyone; every wounded minion then waits with no end. On 30 September 2026, at the last summary of the morale watch, all six pharmagicians of the test dungeon were waiting, with 19 of its 26 domestics.

## What the patch does

When every pharmagician is running REQUEST_HEAL, the patch picks the one with the lowest entity id and gets him up. `IsTherePharmagicianInDungeon` returns false for him, which ends his wait and makes his bed search fail, and his REQUEST_HEAL score is set to 0 each time the game computes it, so the game gives him his other behaviours. Healing is one of them, since the HEAL score does not depend on the healer's own wounds. The other wounded minions keep waiting, as a pharmagician exists for them, and he heals them one by one. He is let go as soon as another pharmagician is up and not wounded, or when he is no longer wounded, and he then lies down in his turn if he still needs to, to be healed by that one.

The patch, added on 1 October 2026, gets one pharmagician up at a time rather than all of them, because a heal goes to a minion who asked for it by lying down (`FindMinionToHealTask` looks through the minions carrying a `RequestHealComponent`; the rest of its conditions was not read): if every pharmagician got up, none would be lying down to be healed.

## How it is built

`Fixes/HealDeadlock.cs` has two hooks. A postfix on `MinionUtility.IsTherePharmagicianInDungeon` returns false for the pharmagician picked. A postfix on `AIComputeRequestHealScoreJobSystem.Run` checks the pharmagicians every half second of real time and, for the one picked, writes 0 into the base and final score of his `AIComputeRequestHealComponent`. `Run` completes its job before returning (`JobHandle.ScheduleBatchedJobsAndComplete`), so the score written stays until `AISwitchBehaviourSystem` reads it. A pharmagician counts as waiting when his `BehaviourTreeOwnerComponent.CurrentBehaviour` is REQUEST_HEAL, and as wounded when he carries a `RequestHealComponent`.

## Log

`[Heal] every pharmagician (N) is waiting to be healed, with M minion(s) waiting in all; <name> gets up to heal the others` when the patch acts, and `[Heal] <name> is let go: <reason>` when it stops.

## Limits

The patch does not change why minions get wounded, how long a heal takes, or how many pharmagicians the dungeon needs. With a single pharmagician who is wounded, the game already ends his wait, and the patch then also keeps him from choosing REQUEST_HEAL again, so he works wounded until another pharmagician exists. The effect of carrying a wound while working was not read in the code.

Since 0.25.0, necromancers given the pharmagician's heal components to heal vampires (`necromancer-cultist-healing.md`) are left out of the pharmagicians the patch watches.
