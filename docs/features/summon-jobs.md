# Jobs for summoned undead and demons

Plugin 0.26.0, 9 October 2026, game 1.8. Settings `Balance.SummonJobs` (on by default), `Balance.UndeadJobs` and `Balance.DemonJobs`.

## What it does

Summoned undead and demons get a job by themselves and do it as minions of that job do. Undead can be servants (`DOMESTIC`), guards and tool artisans; demons servants, guards, jailers (`TORTURER`), weapon artisans and Library workers (`LIBRARY`, the sorcerer's work: astral energy and spells). In the game undead only wander and fight, and demons follow their cultist.

Summons get their jobs automatically, in proportions set in the settings. A summon guard uses the guard tables of the guard rooms without any locker, and summons are never recruitable.

## The game's rule

Undead (`AS_Undead`, `AS_Troll_Undead`) and demons (`AS_Demon`, `AS_CursedWarrior`) carry the same `JobPracticedComponent`, `GradeComponent` and origin component as minions, with the job `UNDEAD` or `DEMON` and the origins `SKELETON`, `ZOMBIE`, `DEMON` or `CURSED_KNIGHT`, whose origin config has no need decay, no salary and no craftable resource. They have no `MinionTag`, no `ReservedPlacesComponent` and no craft components. Their behaviour config is `UndeadBehaviourConfig` (BORED, DEATH, COMBAT, INVOKED) or `DemonBehaviourConfig` (BORED, RESIGN, DEATH, COMBAT, ENEMY_COMBAT, DEMON_FOLLOW_CULTIST); job minions use `MinionBehaviourConfig`. A minion's job components come from `WorldConfig.MinionJobComponents[job]`. A guard's idle behaviour (`BT_Bored_1`) uses a guard table one time in ten, found from the room of the furniture named by his `IoAttachedToComponent` (normally his locker), and otherwise walks into that room.

## The shares

`UndeadJobs` (default `DOMESTIC:34,GUARD:33,ARTISAN:33`) and `DemonJobs` (default `DOMESTIC:20,GUARD:20,TORTURER:20,ARTISAN:20,LIBRARY:20`) list the shares as `JOB:share`, separated by commas. `NONE` keeps a share of summons as the game makes them. Every 5 seconds, each summon still on his own job, alive, in the dungeon, not rising (behaviour `INVOKED`) and, for a demon, linked to a living cultist, gets the job furthest below its share among the summons of his kind, or keeps none when no job is below its share. At most 30 summons get a job per pass. A summon keeps the job he was given; changing the shares only steers the summons given a job afterwards.

## How the patch works

`Balance/SummonJobs.cs`. For a summon given a job, the patch:

- adds the job's components and the minion components the jobs need (`ReservedPlaces`, `CraftSpeedMultiplier`, `CraftQualityModifier`, `DamageOnCraftPercentage`, `TrapTriggerModifier`), each through its own component config (`AComponentConfig.AddComponents`, then `SetupComponents`), taken from `WorldConfig.MinionJobComponents[job]` and from an ordinary minion, and skipping a component whose config component he already has;
- sets his `JobPracticed`;
- points his `BehaviourTreeOwnerConfigComponent` at an ordinary minion's, which names `MinionBehaviourConfig`, and empties his list of available behaviours, which the game then builds again from that config;
- for a guard, sets his `IoAttachedToComponent` to a guard table (`ARMORY_TABLE`) of a guard room, on his floor when there is one, the table with the fewest summon guards first.

Every 5 seconds, the summons with a job have their behaviour config checked again, which covers a save just loaded, and guards whose table is gone get another. Skeletons and zombies are given the goblins' tools (4), demons and cursed warriors the dwarves' weapons (4) and the humans' astral energy (4), with matching resource priorities, in memory only. A summon's look was chosen when he was created and does not change.

## Log

`[SummonJobs]`: the templates taken from an ordinary minion; the resources given to each origin; each job given, with the components added (the first 60); for the first three summons of each job, 4 seconds after the job and then every minute for 5 minutes, the behaviour he runs and the behaviours available to him; every 5 minutes, per kind and per job, the number of summons and what they are doing. An error stops the feature for the session and is logged with its full trace.

## Limits

In testing, summon servants cleaned and summon guards trained; summon artisans had the craft behaviour available but were seen idle, which was not explained. The animations of skeletons, zombies and demons lack some job movements: an undead servant cleans without the broom animation, and `Player.log` records "No animator found in AN_L_Domestic_CleanBroom" each time; the same holds for the training dummy. Summons have no salary and no needs, so they never eat, sleep or rest. A demon with a job no longer follows his cultist and stays linked to him. With the setting off, no new job is given, and at the next load summons with a job get their own behaviour config back (they wander and fight), keeping their job components and job.
