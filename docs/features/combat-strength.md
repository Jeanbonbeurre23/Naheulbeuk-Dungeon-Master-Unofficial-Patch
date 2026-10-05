# Combat strength

Plugin 0.18.0, 1 October 2026, game 1.8. Setting `Balance.CombatStrength` (on by default). It replaces the setting `Balance.GuardStrength` of plugins 0.12.0 to 0.17.0, which covered guards only and which BepInEx leaves in the config file with no effect.

## What it does

The aim, set on 29 September 2026 for guards and extended on 1 October 2026 to every fighting minion, mages and spies included, is a grade-10 minion on par with a top-tier adventurer. The rule chosen for guards stays: the strongest adventurer class at its top level is the reference, grade 1 keeps the game's values, and the boost grows evenly up to grade 10.

A job fights when its attack table is above 0 at grade 10. In the game's configs, the job configurations with such a table are those of the guard, the spy, the sorcerer, the pharmagician, the necromancer, the cultist, the demon, the undead, the ghost and Golbargh. Golbargh, Reivax and Zangdar are unique characters and are left alone, as are unique characters in every job. Every other job has 0 attack and 0 defense at every grade in the game, and is not changed. Which job type each configuration serves is read when the game runs, and the log names it.

For each stat of each fighting job, when the job's grade-10 value is above 0, the multiplier R is the reference's value over it, never below 1, and grade index i (0 for grade 1) gets the game's value times 1 + (R - 1) × min(i, 9) / 9. When the job's grade-10 value is 0, the reference's value times min(i, 9) / 9 is added to the game's value instead, so that grade 10 reaches it too. Life points are computed per job and origin, from the origin's base life and the job's life modifiers of grades 1 to 10, and a minion whose grade-10 life already exceeds the reference keeps his own.

## Expected values

The reference in the 30 September log is MELEE at level 15: attack 40, defense 2, life points 300. Every adventurer attack type had the same values at its top level.

| Job (configuration) | Game's grade-10 attack / defense | Grade-10 after the patch | Game's grade-10 life, human origin (60 base) |
|---|---|---|---|
| Guard, spy | 10 / 2 | 40 / 2 | 335, kept |
| Sorcerer, pharmagician, necromancer, cultist | 10 / 6 | 40 / 6 | 114, raised to 300 |
| Demon | 20 / 6 | 40 / 6 | its origin's base plus 180, raised to 300 if below |
| Undead, ghost | 10 / 0 | 40 / 2 | its origin's base plus 180, raised to 300 if below |

The defense of 6 at grade 10 for sorcerers, pharmagicians, necromancers, cultists and demons is above the reference's 2 and is kept. At grade 11, the game's tables give sorcerers, pharmagicians, necromancers and cultists a defense of 0 and no life modifier, and demons an attack and a defense of 0. The patch keeps that shape, since 0 times a multiplier stays 0; life points at grade 11 are still raised to the reference.

## How the patch applies it

`Balance/CombatStrength.cs` works on every fighting job. Attack and defense: the job's two tables are rewritten in memory from a copy of the game's values taken the first time, so the game's own grade updates use them, and each hired minion's attack and defense bases are set to his grade's value. Life points: each grade update appends a modifier, so running it again would stack life; the base of each minion's life gauge is set instead, computed from his origin's base and his own modifiers. Current life points keep their proportion of the maximum. It runs when a world appears, every 10 seconds, and after each grade update (a postfix on `GradesUtility.UpdateGradeInfosOnEntity`). A minion who moves to a job that does not fight gets his new job's attack and defense and his origin's base life back; a minion who moves between fighting jobs gets his new job's values.

The log, under `[Combat]`, gives every adventurer attack type at its top level, the reference, each fighting job with its game's table and its multipliers, the life multiplier per job and origin, and up to 120 minions' values before and after.

## Limits

- The tables are changed in memory; turning the switch off and restarting the game restores the game's tables. The values written on each minion are saved with the game: with the switch off, a minion's attack and defense return to the game's values at his next grade update, while the base of his life gauge stays as the patch set it, since the game never resets it.
- Whether summoned demons and undead are counted among the minions (`MinionTag`, the game's minion filter) was not checked in the code; the log lists the minions changed.
- Spells, skills and the damage formula were not read. The patch matches the three stats the game stores, not the outcome of a fight.
