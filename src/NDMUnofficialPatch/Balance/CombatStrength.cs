using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Every fighting job as strong at grade 10 as the strongest adventurer at his top level.
    //
    // Game 1.8, from the method bodies. A minion's attack and defense are the base values of AttackComponent.Value and
    // DefenseComponent.Value, which GradesUtility.UpdateGaugeAndStatisticsForGrade (and UpdateGradeInfosOnEntity, which
    // repeats its code) sets from his job's tables (MinionJobInfoConfig.m_attackValue and m_defenseValue, one value
    // per grade, through CalculateAttackPower and CalculateDefense). His life points are the maximum of the gauge in
    // LifePointsComponent: a FloatWithModifiers whose base comes from his origin (Minion.OriginInfoConfig
    // m_lifePointsGauge) and to which each grade update appends the job's modifier for that grade
    // (m_lifePointValueModifier). A FloatWithModifiers computes (base + the ADD values + the ADD_PERCENTAGE_FROM_BASE
    // values times base / 100) times the MULTIPLY values, or its forced value when one is set. An adventurer's
    // attack, defense and life points come from his attack type's tables (Adventurer.AttackTypeInfoConfig
    // m_attackForLevelValue, m_defenseForLevelValue, m_lifePointForLevelValue, one value per level).
    //
    // A fighting job is one whose attack table is above 0 at grade 10. In the configs bundle of game 1.8, read on
    // 1 October 2026, the job configurations with such a table are MinionGuard, MinionSpy, MinionSorcerer,
    // MinionPharmagician, MinionNecromancer, MinionCultist, MinionDemon, MinionUndead, MinionGhost and MinionGolbargh;
    // which job type each one serves is read at run time and logged. Golbargh, Reivax and Zangdar are unique characters
    // and are left alone. Every other job configuration has 0 attack and 0 defense at every grade.
    //
    // When a game world appears the patch reads every adventurer attack type at its top level and takes the one with
    // the highest attack times life points as the reference, as chosen on 29 September 2026 for guards and
    // extended to every fighting job on 1 October 2026. For each stat of each fighting job, when the job's grade-10
    // value is above 0, the multiplier R is the reference's value over it, never below 1, and grade index i (0 for
    // grade 1) gets the game's value times 1 + (R - 1) * min(i, 9) / 9: grade 1 unchanged, the full multiplier from
    // grade 10 on. When the job's grade-10 value is 0 (the defense of the undead), the reference's value times
    // min(i, 9) / 9 is added to the game's value instead, so that grade 10 reaches it as well.
    //
    // Attack and defense: each fighting job's two tables are rewritten in memory from a copy of the game's values taken
    // the first time, so the game's own grade updates use them, and each hired minion's attack and defense bases are
    // set to his grade's value. Life points: the modifiers the game appends make a second grade update stack, so the
    // base of each minion's life gauge is set instead, computed from his origin's base and his own modifiers so that the
    // gauge reaches the target: the reference's life points at grade 10, his own life times the multiplier below (R for
    // life being computed per job and origin, with that job's modifiers of grades 1 to 10). A minion whose grade-10
    // life already exceeds the reference keeps it. Current life points keep their proportion of the maximum. Everything
    // is computed from the game's values, never from the current ones, so applying it again changes nothing. It is
    // applied when a world appears, every 10 seconds, and after each grade update; a minion who moves to a job that
    // does not fight gets his new job's attack and defense and his origin's base life back.
    internal static unsafe class CombatStrength
    {
        private const int FullGrade = 9; // GradeType.GRADE_10

        private static readonly JobType[] NotScaled = { JobType.GOLBARGH, JobType.REIVAX, JobType.ZANGDAR, JobType.ADVENTURER, JobType.CLIENT, JobType.WORKER };

        private sealed class Job
        {
            public JobType Type;
            public float[] Attack, Defense;
            public ValueModifier[] LifeModifiers;
            public readonly Dictionary<OriginType, float> LifeRatio = new();
        }

        private static readonly Dictionary<IntPtr, (float[] Attack, float[] Defense)> Vanilla = new();
        private static readonly Dictionary<JobType, Job> Jobs = new();
        private static readonly Dictionary<OriginType, float> OriginBase = new();
        private static readonly HashSet<int> Touched = new();

        private static IntPtr _world;
        private static bool _ready;
        private static float _nextSweep;
        private static float _targetAttack, _targetDefense, _targetLife;
        private static int _changesLogged;
        private static bool _errorLogged;

        private static bool _layoutChecked;
        private static int _attackValue, _defenseValue, _lifeGauge, _gaugeMax, _gaugeCurrent;
        private static int _base, _computed, _forced, _dirty, _gradeOffset;
        private static float _invalidForce;

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Combat] {what} failed, further errors are not logged: {e.Message}");
        }

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            _attackValue = Il2CppRaw.ValueFieldOffset<AttackComponent>("Value");
            _defenseValue = Il2CppRaw.ValueFieldOffset<DefenseComponent>("Value");
            _lifeGauge = Il2CppRaw.ValueFieldOffset<LifePointsComponent>("Gauge");
            _gaugeMax = Il2CppRaw.ValueFieldOffset<Gauge>("MaxValue");
            _gaugeCurrent = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentValue");
            Il2CppRaw.ExpectValueFieldOffset<FloatWithModifiers>("m_modifiers", 0);
            _base = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_baseValue");
            _computed = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_computedValue");
            _forced = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_forcedValue");
            _dirty = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_isDirty");
            _gradeOffset = Il2CppRaw.ValueFieldOffset<GradeComponent>("CurrentGrade");
            _invalidForce = FloatWithModifiers.INVALID_FORCE_VALUE;
            _layoutChecked = true;
        }

        private static float Multiplier(float ratio, int gradeIndex) => 1f + (ratio - 1f) * Math.Min(Math.Max(gradeIndex, 0), FullGrade) / (float)FullGrade;

        private static float Ratio(float target, float own) => own > 0f && target > own ? target / own : 1f;

        // ---- Every frame ----

        internal static void Update()
        {
            if (!GameContext.Ready) return;
            try
            {
                IntPtr world = GameContext.WorldPointer;
                if (world != _world)
                {
                    _world = world;
                    _ready = false;
                    Touched.Clear();
                    _ready = Prepare();
                    _nextSweep = 0f;
                }
                if (!_ready || Time.unscaledTime < _nextSweep) return;
                _nextSweep = Time.unscaledTime + 10f;
                Sweep();
            }
            catch (Exception e)
            {
                _ready = false;
                ReportError("strengthening fighting minions", e);
            }
        }

        // ---- The reference and the tables ----

        private static float[] Copy(Il2CppArrayBase<float> array)
        {
            var copy = new float[array.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = array[i];
            return copy;
        }

        // The game's table scaled for one stat, as described above; written into the job's own table too.
        private static float[] Scale(float[] vanilla, Il2CppArrayBase<float> table, float target, out string how)
        {
            float top = vanilla.Length > FullGrade ? vanilla[FullGrade] : 0f;
            var scaled = new float[vanilla.Length];
            if (top > 0f)
            {
                float ratio = Ratio(target, top);
                for (int i = 0; i < scaled.Length; i++) scaled[i] = vanilla[i] * Multiplier(ratio, i);
                how = $"x{F(ratio)}";
            }
            else
            {
                for (int i = 0; i < scaled.Length; i++) scaled[i] = vanilla[i] + target * Math.Min(i, FullGrade) / FullGrade;
                how = $"+{F(target)} at grade 10";
            }
            for (int i = 0; i < scaled.Length && i < table.Length; i++) table[i] = scaled[i];
            return scaled;
        }

        private static bool Prepare()
        {
            CheckLayout();
            var worldConfig = GameContext.GameData.WorldConfig;
            Jobs.Clear();
            OriginBase.Clear();

            // The reference: the adventurer attack type with the highest attack times life points at its top level.
            var types = worldConfig.AttackTypesConfig;
            string best = null;
            float bestScore = float.MinValue;
            foreach (AttackType type in Enum.GetValues(typeof(AttackType)))
            {
                Adventurer.AttackTypeInfoConfig config;
                try { config = types.GetAdventurerConfig(type); } catch { continue; }
                if (config == null) continue;
                var a = config.m_attackForLevelValue;
                var d = config.m_defenseForLevelValue;
                var l = config.m_lifePointForLevelValue;
                if (a == null || d == null || l == null || a.Length == 0 || d.Length == 0 || l.Length == 0) continue;
                int top = Math.Min(a.Length, Math.Min(d.Length, l.Length)) - 1;
                Plugin.Logger.LogInfo($"[Combat] adventurer {type}, level {top + 1} of {top + 1}: attack {F(a[top])}, defense {F(d[top])}, life points {F(l[top])}");
                float score = a[top] * l[top];
                if (score <= bestScore) continue;
                bestScore = score;
                best = $"{type} at level {top + 1}";
                _targetAttack = a[top];
                _targetDefense = d[top];
                _targetLife = l[top];
            }
            if (best == null) { Plugin.Logger.LogWarning("[Combat] no adventurer attack type readable; minions left as they are"); return false; }
            Plugin.Logger.LogInfo($"[Combat] reference: {best}, attack {F(_targetAttack)}, defense {F(_targetDefense)}, life points {F(_targetLife)}");

            foreach (JobType type in Enum.GetValues(typeof(JobType)))
            {
                if (NotScaled.Contains(type)) continue;
                MinionJobInfoConfig config;
                try { config = worldConfig.JobsConfig.GetMinionConfig(type); } catch { continue; }
                if (config == null || config.m_attackValue == null || config.m_defenseValue == null || config.m_lifePointValueModifier == null) continue;
                var attack = config.m_attackValue.m_internalArray;
                var defense = config.m_defenseValue.m_internalArray;
                if (attack == null || defense == null) continue;
                if (!Vanilla.TryGetValue(config.Pointer, out var vanilla))
                {
                    vanilla = (Copy(attack), Copy(defense));
                    Vanilla[config.Pointer] = vanilla;
                }
                if (vanilla.Attack.Length <= FullGrade || vanilla.Attack[FullGrade] <= 0f) continue; // does not fight

                var job = new Job { Type = type };
                job.Attack = Scale(vanilla.Attack, attack, _targetAttack, out string attackHow);
                job.Defense = Scale(vanilla.Defense, defense, _targetDefense, out string defenseHow);
                var lifeArray = config.m_lifePointValueModifier.m_internalArray;
                job.LifeModifiers = new ValueModifier[lifeArray.Length];
                for (int i = 0; i < job.LifeModifiers.Length; i++) job.LifeModifiers[i] = lifeArray[i];
                Jobs[type] = job;

                var line = new List<string>();
                for (int i = 0; i < vanilla.Attack.Length; i++)
                    line.Add($"{i + 1}: {F(vanilla.Attack[i])}/{F(i < vanilla.Defense.Length ? vanilla.Defense[i] : 0f)}/{(i < job.LifeModifiers.Length ? job.LifeModifiers[i].Operator + " " + F(job.LifeModifiers[i].Value) : "-")}");
                Plugin.Logger.LogInfo($"[Combat] {type} ({config.name}): attack {attackHow}, defense {defenseHow}; game's values by grade, attack/defense/life modifier: {string.Join("; ", line)}");
            }
            if (Jobs.Count == 0) { Plugin.Logger.LogWarning("[Combat] no fighting job found; minions left as they are"); return false; }
            return true;
        }

        private static float OriginBaseLife(OriginType origin)
        {
            if (OriginBase.TryGetValue(origin, out float baseLife)) return baseLife;
            baseLife = 0f;
            var info = GameContext.GameData.WorldConfig.OriginsConfig.GetMinionConfig(origin);
            if (info != null && info.m_lifePointsGauge != null) baseLife = info.m_lifePointsGauge.m_maxValue;
            OriginBase[origin] = baseLife;
            return baseLife;
        }

        // The life multiplier at grade 10 for a job and an origin: the reference's life over the gauge of a minion of
        // that origin carrying the job's modifiers of grades 1 to 10.
        private static float LifeRatio(Job job, OriginType origin, float baseLife)
        {
            if (job.LifeRatio.TryGetValue(origin, out float ratio)) return ratio;
            float add = 0f, pct = 0f, mul = 1f;
            for (int i = 0; i <= FullGrade && i < job.LifeModifiers.Length; i++) Accumulate(job.LifeModifiers[i], ref add, ref pct, ref mul);
            float grade10 = (baseLife + add + pct * baseLife / 100f) * mul;
            ratio = Ratio(_targetLife, grade10);
            job.LifeRatio[origin] = ratio;
            Plugin.Logger.LogInfo($"[Combat] {job.Type}, {origin}: base life points {F(baseLife)}, at grade 10 {F(grade10)}, life multiplier x{F(ratio)}");
            return ratio;
        }

        private static void Accumulate(ValueModifier m, ref float add, ref float pct, ref float mul)
        {
            if (m.Value == 0f) return;
            switch (m.Operator)
            {
                case EModifierOperator.ADD: add += m.Value; break;
                case EModifierOperator.ADD_PERCENTAGE_FROM_BASE_VALUE: pct += m.Value; break;
                case EModifierOperator.MULTIPLY: mul *= m.Value; break;
            }
        }

        // ---- Minions ----

        private static Job JobOf(RawPool jobs, int entity)
        {
            IntPtr j = jobs.Item(entity);
            return j != IntPtr.Zero && Jobs.TryGetValue(((JobPracticedComponent*)j)->JobPracticed, out var job) ? job : null;
        }

        private static void Sweep()
        {
            if (!GameContext.TryWorld(out var world, out int size)) return;
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var seen = new HashSet<int>();
            foreach (var m in GameContext.ListMinions())
            {
                seen.Add(m.Entity);
                Apply(world, m.Entity, m.Name, jobs);
            }
            // Minions who left a fighting job: back to the game's values.
            foreach (int e in new List<int>(Touched))
                if (!seen.Contains(e) || JobOf(jobs, e) == null) Restore(world, size, e, jobs);
        }

        // After a grade update, from the postfix below.
        internal static void OnGradeUpdated(int entity)
        {
            if (!_ready) return;
            try
            {
                if (!GameContext.TryWorld(out var world, out _)) return;
                var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
                if (JobOf(jobs, entity) == null || !RawPool.Of<MinionTag>(world, -1).Has(entity) || RawPool.Of<UniqueComponent>(world, -1).Has(entity)) return;
                string name;
                try { name = GameContext.Minions.GetMinionFullName(entity); } catch { name = $"entity {entity}"; }
                Apply(world, entity, name, jobs);
            }
            catch (Exception e) { ReportError("strengthening a minion after a grade update", e); }
        }

        private static bool Forced(IntPtr fwm) => *(float*)(fwm + _forced) != _invalidForce;

        private static bool SetBase(IntPtr fwm, float value)
        {
            if (Forced(fwm)) return false;
            ref float b = ref *(float*)(fwm + _base);
            if (Math.Abs(b - value) < 0.001f) return false;
            b = value;
            *(byte*)(fwm + _dirty) = 1;
            return true;
        }

        private static void Apply(EcsWorld world, int entity, string name, RawPool jobs)
        {
            var job = JobOf(jobs, entity);
            if (job == null) return;
            IntPtr g = RawPool.Of<GradeComponent>(world, -1).Item(entity);
            IntPtr o = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent)).Item(entity);
            if (g == IntPtr.Zero || o == IntPtr.Zero) return;
            int grade = *(int*)(g + _gradeOffset);
            var origin = ((OriginComponent*)o)->Origin;
            Touched.Add(entity);

            var changes = new List<string>();
            IntPtr a = RawPool.Of<AttackComponent>(world, -1).Item(entity);
            if (a != IntPtr.Zero && grade >= 0 && grade < job.Attack.Length)
            {
                IntPtr fwm = a + _attackValue;
                float before = *(float*)(fwm + _base);
                if (SetBase(fwm, job.Attack[grade])) changes.Add($"attack {F(before)} -> {F(job.Attack[grade])}");
            }
            IntPtr d = RawPool.Of<DefenseComponent>(world, -1).Item(entity);
            if (d != IntPtr.Zero && grade >= 0 && grade < job.Defense.Length)
            {
                IntPtr fwm = d + _defenseValue;
                float before = *(float*)(fwm + _base);
                if (SetBase(fwm, job.Defense[grade])) changes.Add($"defense {F(before)} -> {F(job.Defense[grade])}");
            }
            IntPtr l = RawPool.Of<LifePointsComponent>(world, -1).Item(entity);
            float baseLife = OriginBaseLife(origin);
            if (l != IntPtr.Zero && baseLife > 0f)
            {
                float ratio = LifeRatio(job, origin, baseLife);
                string life = SetLife(l + _lifeGauge, baseLife, grade >= FullGrade ? -1f : Multiplier(ratio, grade));
                if (life != null) changes.Add(life);
            }
            if (changes.Count > 0 && _changesLogged < 120)
            {
                _changesLogged++;
                Plugin.Logger.LogInfo($"[Combat] {name} ({job.Type}, {origin}, {(GradeType)grade}): {string.Join(", ", changes)}");
            }
        }

        // Sets the base of a life gauge so that its maximum is the target: the reference's life points when multiplier
        // is negative (grade 10 and above), otherwise the maximum the gauge has with the origin's base, times multiplier.
        private static string SetLife(IntPtr gauge, float baseLife, float multiplier)
        {
            IntPtr fwm = gauge + _gaugeMax;
            if (Forced(fwm)) return null;
            float add = 0f, pct = 0f, mul = 1f;
            IntPtr list = *(IntPtr*)(fwm);
            if (list != IntPtr.Zero)
            {
                IntPtr data = *(IntPtr*)list;
                int length = *(int*)(list + IntPtr.Size);
                if (length < 0 || length > 256) return null;
                for (int i = 0; i < length && data != IntPtr.Zero; i++) Accumulate(*(ValueModifier*)(data + i * sizeof(ValueModifier)), ref add, ref pct, ref mul);
            }
            float perBase = (1f + pct / 100f) * mul;
            if (perBase <= 0f) return null;
            float own = baseLife * perBase + add * mul;
            float target = multiplier < 0f ? Math.Max(own, _targetLife) : own * multiplier;
            float newBase = (target / mul - add) / (1f + pct / 100f);
            float oldBase = *(float*)(fwm + _base);
            float oldMax = oldBase * perBase + add * mul;
            if (!SetBase(fwm, newBase)) return null;
            *(float*)(fwm + _computed) = target;
            ref float current = ref *(float*)(gauge + _gaugeCurrent);
            if (oldMax > 0f) current = Math.Min(target, current * target / oldMax);
            return $"life points {F(oldMax)} -> {F(target)}";
        }

        // A minion who left a fighting job: his attack and defense bases from his new job's tables at his grade, as the
        // game's grade update would set them, and his life gauge's base back to his origin's.
        private static void Restore(EcsWorld world, int size, int entity, RawPool jobs)
        {
            Touched.Remove(entity);
            if (!world.IsEntityAlive(entity, size)) return;
            IntPtr j = jobs.Item(entity);
            IntPtr g = RawPool.Of<GradeComponent>(world, -1).Item(entity);
            if (j != IntPtr.Zero && g != IntPtr.Zero)
            {
                int grade = *(int*)(g + _gradeOffset);
                var config = GameContext.GameData.WorldConfig.JobsConfig.GetMinionConfig(((JobPracticedComponent*)j)->JobPracticed);
                var attack = config?.m_attackValue?.m_internalArray;
                var defense = config?.m_defenseValue?.m_internalArray;
                IntPtr a = RawPool.Of<AttackComponent>(world, -1).Item(entity);
                IntPtr d = RawPool.Of<DefenseComponent>(world, -1).Item(entity);
                if (attack != null && a != IntPtr.Zero && grade >= 0 && grade < attack.Length) SetBase(a + _attackValue, attack[grade]);
                if (defense != null && d != IntPtr.Zero && grade >= 0 && grade < defense.Length) SetBase(d + _defenseValue, defense[grade]);
            }
            IntPtr o = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent)).Item(entity);
            IntPtr l = RawPool.Of<LifePointsComponent>(world, -1).Item(entity);
            if (o != IntPtr.Zero && l != IntPtr.Zero)
            {
                float baseLife = OriginBaseLife(((OriginComponent*)o)->Origin);
                if (baseLife > 0f) SetBase(l + _lifeGauge + _gaugeMax, baseLife);
            }
            Plugin.Logger.LogInfo($"[Combat] entity {entity} left a fighting job: attack and defense set from his new job, life base back to his origin's");
        }
    }

    [HarmonyPatch(typeof(GradesUtility), nameof(GradesUtility.UpdateGradeInfosOnEntity))]
    internal static class CombatGradePatch
    {
        private static bool Prepare() => Settings.CombatStrength.Value;
        private static void Postfix(int __0) => CombatStrength.OnGradeUpdated(__0);
    }
}
