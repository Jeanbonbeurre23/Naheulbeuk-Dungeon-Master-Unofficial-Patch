# Minimum salaries

Plugin 0.6.0, 27 September 2026, game 1.8. Setting `Economy.MinimumSalaries` (on by default).

## What it does

Every minion's salary is the lowest the game's own formula gives for his origin, job and grade. Recruitment candidates already show that salary, a minion's salary drops to it when a save is loaded, and it is set again whenever the game recomputes it, at each grade change.

## How the game computes a salary

Read from the method bodies of `SalaryUtility` in game 1.8:

- `ComputeIntermediateFactorForEntity`: F = ((origin factor + 1) × job factor + 1) × grade. The origin factor comes from the minion's entry in `OriginsConfig`, the job factor from his entry in `JobsConfig`, the grade from `GradeComponent.CurrentGrade`.
- `ComputeDismissCostForEntity`: dismissal cost = round((gacha / (15 − 0.35 F) + F) × `EconomyConfig.UniversalBonusFactor`).
- `ComputeSalaryForEntity`: salary = round(dismissal cost × `EconomyConfig.UniversalSalaryFactor`), and 0 at grade 0.

Rounding is `Mathf.Round`. The gacha value (`GachaStatisticComponent.Value`) is drawn once per minion by `GachaStatisticConfig.SetupComponent`, uniformly between 1 and 100, and is the only random part of the salary. For a given origin, job and grade the lowest salary is the one at gacha 1; if 15 − 0.35 F were negative the formula would decrease with gacha, and the patch takes the lower of the values at 1 and at 100 so that both cases are covered.

The game sets the salary in `SetSalaryAndDismissCost`, called when a recruitment candidate is created (`RecruitmentUtility.CreateNewCandidate`) and when an entity is set up from its configuration (`SalaryConfig.SetupComponent`), and in `UpdateDismissAndSalaryCosts`, called on a grade change (`GradesUtility.UpdateGradeInfosOnEntity`). Entities whose `SalaryConfig` has `UseConfiguredValues` set, the unique characters, keep their configured values.

## How the patch sets it

A postfix on `SetSalaryAndDismissCost` and on `UpdateDismissAndSalaryCosts` (`Economy/MinimumSalaries.cs`) computes the lowest salary and writes it with the game's own `SalaryUtility.SetSalaryCost`, which also flags the decade's salary total for recomputation (`SalaryManagerComponent.UpdateSalarialCosts`). A sweep over every entity with a `SalaryComponent`, from a postfix on `UpdateSalarySystem.Run`, runs when a game world appears and then every 10 seconds of real time; it lowers the salaries of a loaded save and catches any change made by another path.

Before changing an entity, the patch checks its reproduction of the formula against the game: with the minion's real gacha value, it must give the same dismissal cost as `ComputeDismissCostForEntity`, and with his stored dismissal cost, the same salary as `ComputeSalaryForEntity`. An entity for which either differs is left alone and reported once in the log.

What the patch does not change: the gacha value, which other systems may read (a getter named `MinionJobInfoConfig.CraftSuccessGachaModifierValue` suggests a link with craft success); the dismissal cost; configured salaries; grade-0 salaries.

## Consequences in the game

- The gold set aside for salaries each decade (`UpdateSalarySystem.TryRequestSalaryPayment`, `SalaryManagerComponent.CurrentSalarialCosts`) falls accordingly.
- A minion's salary is added to his own purse (`MoneyComponent`, in `SalaryUtility.GetSalary`). In 1.8 the classes that hold a pool of that component are the salary systems, `SkillUtility` and `StealGoldTask` (adventurers stealing gold); the tavern's takings (`TavernUtility.PayConsumption`, `PayConsumptionTask`) hold none, so a smaller purse should not lower tavern income.
- No morale effect of the salary level was found. `SalaryInformationsConfig` holds states for underpaid, overpaid, raised and not raised minions, but their getters have no caller and no code reading them was found among the functions searched; the state applied at payday (`GetSalaryTask`) does not depend on the amount.
- Salaries are stored in the save. With the setting turned off, lowered salaries stay as they are until the game recomputes them at the next grade change.
