using System;
using System.Collections.Generic;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Economy
{
    // Every minion's salary is the lowest the game's formula gives for his origin, job and grade.
    //
    // SalaryUtility computes a minion's costs in two steps (game 1.8, read from the method bodies):
    //   F       = ((origin factor + 1) * job factor + 1) * grade          ComputeIntermediateFactorForEntity
    //             origin factor: OriginsConfig minion config; job factor: JobsConfig minion config
    //   dismiss = round((gacha / (15 - 0.35 F) + F) * UniversalBonusFactor)   ComputeDismissCostForEntity
    //   salary  = round(dismiss * UniversalSalaryFactor)                       ComputeSalaryForEntity
    // UniversalBonusFactor and UniversalSalaryFactor come from EconomyConfig; rounding is Mathf.Round. The gacha
    // value (GachaStatisticComponent) is drawn once per minion, uniformly from 1 to 100
    // (GachaStatisticConfig.SetupComponent); it is the only random part of the salary. For a given origin, job and
    // grade, the lowest salary is therefore the one at gacha 1, or at gacha 100 if 15 - 0.35 F is negative.
    //
    // The game sets the salary when a recruitment candidate is created (RecruitmentUtility.CreateNewCandidate and
    // SalaryConfig.SetupComponent call SetSalaryAndDismissCost) and when a grade changes
    // (GradesUtility.UpdateGradeInfosOnEntity calls UpdateDismissAndSalaryCosts). A postfix on both sets the salary
    // to the lowest value with the game's own SalaryUtility.SetSalaryCost, which also asks for the decade's salary
    // total to be recomputed. A sweep of every entity with a salary runs when a game world appears and every
    // 10 seconds of real time, for minions of a loaded save and any change made elsewhere.
    //
    // The minion's gacha value, which other game systems read, is never changed, nor is his dismissal cost. Salaries
    // the game takes from configuration (SalaryConfig.UseConfiguredValues, unique characters) are left alone, as
    // are minions of grade 0, whose salary the game sets to 0.
    //
    // Before changing anything, the formula above is checked against the game's own ComputeDismissCostForEntity
    // and ComputeSalaryForEntity for the minion's real values; a minion for which they differ is left alone and
    // reported once in the log.
    internal static unsafe class MinimumSalaries
    {
        private const int GachaMin = 1;
        private const int GachaMax = 100;

        private static IntPtr _world;
        private static float _nextSweep;
        private static bool _layoutChecked;
        private static int _gradeOffset;
        private static int _configOffset;
        private static readonly HashSet<int> Mismatches = new();
        private static int _changesLogged;
        private static bool _errorLogged;

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            Il2CppRaw.ExpectValueFieldOffset<SalaryComponent>("DismissCost", 32);
            Il2CppRaw.ExpectValueFieldOffset<SalaryComponent>("SalaryCost", 36);
            Il2CppRaw.ExpectValueFieldOffset<GachaStatisticComponent>("Value", 0);
            _gradeOffset = Il2CppRaw.ValueFieldOffset<GradeComponent>("CurrentGrade");
            _configOffset = Il2CppRaw.ValueFieldOffset<SalaryConfigComponent>("m_config");
            _layoutChecked = true;
        }

        // Every frame, from UpdateSalarySystem.Run.
        internal static void OnSalarySystemRun(UpdateSalarySystem system)
        {
            IntPtr world = Il2CppRaw.ReadObject(system.Pointer, "m_world", "EcsWorld");
            if (world == _world && Time.unscaledTime < _nextSweep) return;
            bool newWorld = world != _world;
            if (newWorld) Mismatches.Clear();
            _world = world;
            _nextSweep = Time.unscaledTime + 10f;
            var utility = new SalaryUtility(Il2CppRaw.ReadObject(system.Pointer, "m_salaryUtility", "SalaryUtility"));
            Sweep(utility, newWorld);
        }

        private static void Sweep(SalaryUtility utility, bool newWorld)
        {
            var world = WorldOf(utility);
            var salaries = RawPool.Of<SalaryComponent>(world, sizeof(SalaryComponent));
            int changed = 0, seen = 0;
            foreach (int e in salaries.Entities())
            {
                seen++;
                if (Apply(utility, e)) changed++;
            }
            if (newWorld || changed > 0)
                Plugin.Logger.LogInfo($"[Salaries] {seen} salaried entities checked, {changed} salaries lowered to the minimum for their origin, job and grade");
        }

        // The utility's own world (SalaryUtility.m_world, an EcsWorldInject whose only field is the world).
        private static EcsWorld WorldOf(SalaryUtility utility) => new EcsWorld(Il2CppRaw.ReadObject(utility.Pointer, "m_world", "EcsWorld"));

        // Sets the entity's salary to the lowest value for its origin, job and grade. True when it changed.
        internal static bool Apply(SalaryUtility utility, int entity)
        {
            try
            {
                CheckLayout();
                var world = WorldOf(utility);
                var salaries = RawPool.Of<SalaryComponent>(world, sizeof(SalaryComponent));
                IntPtr salary = salaries.Item(entity);
                if (salary == IntPtr.Zero) return false;

                IntPtr config = RawPool.Of<SalaryConfigComponent>(world, -1).Item(entity);
                if (config != IntPtr.Zero)
                {
                    IntPtr salaryConfig = *(IntPtr*)(config + _configOffset);
                    if (salaryConfig != IntPtr.Zero && new SalaryConfig(salaryConfig).UseConfiguredValues) return false;
                }
                IntPtr grade = RawPool.Of<GradeComponent>(world, -1).Item(entity);
                if (grade == IntPtr.Zero || *(int*)(grade + _gradeOffset) == 0) return false;
                IntPtr gacha = RawPool.Of<GachaStatisticComponent>(world, sizeof(GachaStatisticComponent)).Item(entity);
                if (gacha == IntPtr.Zero) return false;

                IntPtr gameData = Il2CppRaw.ReadObject(utility.Pointer, "m_gameData", "GameData");
                var economy = new GameData(gameData).WorldConfig.EconomyConfig;
                float bonusFactor = economy.UniversalBonusFactor;
                float salaryFactor = economy.UniversalSalaryFactor;
                float f = utility.ComputeIntermediateFactorForEntity(entity);
                float denominator = 15f - f * 0.35f;
                if (denominator == 0f) return false;

                // The formula must give the game's own results for this minion's real values.
                int realGacha = ((GachaStatisticComponent*)gacha)->Value;
                uint storedDismiss = ((SalaryComponent*)salary)->DismissCost;
                int gameDismiss = utility.ComputeDismissCostForEntity(entity);
                int gameSalary = utility.ComputeSalaryForEntity(entity);
                int ownDismiss = Dismiss(realGacha, f, denominator, bonusFactor);
                int ownSalary = Salary(storedDismiss, salaryFactor);
                if (ownDismiss != gameDismiss || ownSalary != gameSalary)
                {
                    if (Mismatches.Add(entity))
                        Plugin.Logger.LogWarning($"[Salaries] entity {entity} left alone: the formula gives dismissal {ownDismiss} and salary {ownSalary}, the game {gameDismiss} and {gameSalary}");
                    return false;
                }

                int lowest = Math.Min(Salary((uint)Math.Max(0, Dismiss(GachaMin, f, denominator, bonusFactor)), salaryFactor),
                                      Salary((uint)Math.Max(0, Dismiss(GachaMax, f, denominator, bonusFactor)), salaryFactor));
                if (lowest < 0) lowest = 0;
                uint current = ((SalaryComponent*)salary)->SalaryCost;
                if (current == (uint)lowest) return false;
                utility.SetSalaryCost((uint)lowest, entity);
                if (_changesLogged < 30)
                {
                    _changesLogged++;
                    Plugin.Logger.LogInfo($"[Salaries] entity {entity}: salary {current} -> {lowest} (gacha {realGacha}, factor {f:0.###})");
                }
                return true;
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Plugin.Logger.LogWarning($"[Salaries] setting a salary failed, further errors are not logged: {e.Message}");
                }
                return false;
            }
        }

        private static int Dismiss(int gacha, float f, float denominator, float bonusFactor)
        {
            float value = ((float)gacha / denominator + f) * bonusFactor;
            return (int)MathF.Round(value);
        }

        private static int Salary(uint dismiss, float salaryFactor)
        {
            float value = (float)(double)dismiss * salaryFactor;
            return (int)MathF.Round(value);
        }
    }

    [HarmonyPatch(typeof(SalaryUtility), nameof(SalaryUtility.SetSalaryAndDismissCost))]
    internal static class SalaryOnCreationPatch
    {
        private static bool Prepare() => Settings.MinimumSalaries.Value;
        private static void Postfix(SalaryUtility __instance, int __0) => MinimumSalaries.Apply(__instance, __0);
    }

    [HarmonyPatch(typeof(SalaryUtility), nameof(SalaryUtility.UpdateDismissAndSalaryCosts))]
    internal static class SalaryOnGradePatch
    {
        private static bool Prepare() => Settings.MinimumSalaries.Value;
        private static void Postfix(SalaryUtility __instance, int __0) => MinimumSalaries.Apply(__instance, __0);
    }

    [HarmonyPatch(typeof(UpdateSalarySystem), nameof(UpdateSalarySystem.Run))]
    internal static class SalarySweepPatch
    {
        private static bool Prepare() => Settings.MinimumSalaries.Value;
        private static void Postfix(UpdateSalarySystem __instance)
        {
            try { MinimumSalaries.OnSalarySystemRun(__instance); }
            catch { }
        }
    }
}
