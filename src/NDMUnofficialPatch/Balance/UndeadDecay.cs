using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Undead lose life only when they are harmed.
    //
    // Game 1.8, from the method bodies, the configs bundle and the French texts, read on 8 October 2026. Each frame
    // UpdateCorpsesResourceGaugeSystem.Run first updates the compost gauge (AUpdateResourceGaugeSystem.UpdateGaugeSteps
    // and CheckIfCurrentStepChanged, the steps and their notification), then goes through its filter m_undeadFilter,
    // Inc<Undead.UndeadTag> and Exc<DeathComponent>. For each undead it takes the life gauge that
    // StatisticsUtility.GetGaugeForType returns for LIFE_POINTS (7), the Gauge of his LifePointsComponent read from
    // StatisticsUtility.m_lifePointsPool, and lowers its CurrentValue by CorpsesResourceConfig.m_valueByStep at the
    // compost gauge's current step times the frame's duration (GameData + 0x10): 0.022 life points a second when the
    // compost store is 84 to 100 % full (step "DoT UD T5"), then 0.031, 0.040, 0.049 and 0.058, and 0.067 below 17 %
    // ("DoT UD T0"). It then sets the DamageType of his LastDamageTakenComponent (EntityUtility.m_lastDamageTakenPool)
    // to EDamageType.UNDEAD (8) and calls GaugeUtility.ClampAndUpdatePercentage, which writes only the gauge's
    // CurrentValue and CurrentPercentage. The six steps of the compost gauge apply no state (StatesToApply is empty).
    // The rule texts UI_RULE_SKELETON_DESC2, UI_RULE_ZOMBIE_DESC3 and UI_RULE_GHOST_DESC2 describe it ("Plus vous stockez
    // de composte, plus il survivra longtemps"). Demons are not in the filter, and no other system lowers a demon's life
    // by itself: UI_RULE_DEMON_DESC reads "Suit son cultiste. Attaque avec les malédictions de son cultiste."
    //
    // A prefix on Run notes, for each undead of the system's own filter, his life gauge's CurrentValue and
    // CurrentPercentage and his last damage's DamageType, read from the two pools the system writes to. A postfix writes
    // them back, which undoes everything Run did to the undead and nothing else. Hits, and states such as poison or
    // burning, are applied by other systems and are left to the game. The compost gauge keeps its steps and its other
    // uses.
    [HarmonyPatch(typeof(UpdateCorpsesResourceGaugeSystem), nameof(UpdateCorpsesResourceGaugeSystem.Run))]
    internal static unsafe class UndeadDecay
    {
        private static bool Prepare() => Settings.UndeadNoDecay.Value;

        private struct Kept
        {
            internal int Entity;
            internal float Value;
            internal float Percentage;
            internal int DamageType; // -1 when he has no LastDamageTakenComponent
        }

        private static readonly List<Kept> Undead = new();
        private static IntPtr _system, _filterPointer;
        private static EcsFilter _filter;
        private static RawPool _life, _lastDamage;
        private static int _value = -1, _percentage, _damageType;
        private static bool _failed, _announce;
        private static float _undone, _nextSummary;

        private static void Layout()
        {
            if (_value >= 0) return;
            int gauge = Il2CppRaw.ValueFieldOffset<LifePointsComponent>("Gauge");
            int value = gauge + Il2CppRaw.ValueFieldOffset<Gauge>("CurrentValue");
            int percentage = gauge + Il2CppRaw.ValueFieldOffset<Gauge>("CurrentPercentage");
            _damageType = Il2CppRaw.ValueFieldOffset<LastDamageTakenComponent>("DamageType");
            _percentage = percentage;
            _value = value;
        }

        // The pools and the filter of this system object; read again when the game builds new systems for another save.
        private static void Bind(IntPtr system, IntPtr filter)
        {
            Layout();
            IntPtr statistics = Il2CppRaw.ReadObject(system, "m_statisticsUtility", "StatisticsUtility");
            IntPtr entities = Il2CppRaw.ReadObject(system, "m_entityUtility", "EntityUtility");
            _life = RawPool.From(Il2CppRaw.ReadPointer(statistics, "m_lifePointsPool"), 120, "LifePointsComponent");
            _lastDamage = RawPool.From(Il2CppRaw.ReadPointer(entities, "m_lastDamageTakenPool"), 68, "LastDamageTakenComponent");
            if (_life.IsNull) throw new InvalidOperationException("the life points pool is not set");
            if (Il2CppRaw.ClassName(filter) != "EcsFilter") throw new InvalidOperationException($"m_undeadFilter holds {Il2CppRaw.ClassName(filter)}, expected EcsFilter");
            _filter = new EcsFilter(filter);
            _system = system;
            _filterPointer = filter;
            _announce = true;
        }

        private static void Fail(string what, Exception e)
        {
            Undead.Clear();
            if (_failed) return;
            _failed = true;
            Plugin.Logger.LogWarning($"[Undead] {what} failed, undead lose life by themselves as in the game for the rest of the session: {e.Message}");
        }

        private static void Prefix(UpdateCorpsesResourceGaugeSystem __instance)
        {
            Undead.Clear();
            if (_failed) return;
            try
            {
                IntPtr system = __instance.Pointer;
                IntPtr filter = Il2CppRaw.ReadPointer(system, "m_undeadFilter");
                if (system != _system || filter != _filterPointer) Bind(system, filter);
                int count = _filter.GetEntitiesCount();
                if (count <= 0) return;
                var ids = _filter.GetRawEntities();
                for (int i = 0; i < count; i++)
                {
                    int e = ids[i];
                    IntPtr life = _life.Item(e);
                    if (life == IntPtr.Zero) continue;
                    IntPtr last = _lastDamage.Item(e);
                    Undead.Add(new Kept
                    {
                        Entity = e,
                        Value = *(float*)(life + _value),
                        Percentage = *(float*)(life + _percentage),
                        DamageType = last == IntPtr.Zero ? -1 : *(int*)(last + _damageType),
                    });
                }
            }
            catch (Exception e) { Fail("reading the undead's life", e); }
        }

        private static void Postfix()
        {
            if (Undead.Count == 0) return;
            int count = Undead.Count;
            try
            {
                foreach (var k in Undead)
                {
                    IntPtr life = _life.Item(k.Entity);
                    if (life == IntPtr.Zero) continue;
                    float* value = (float*)(life + _value);
                    if (*value < k.Value) _undone += k.Value - *value;
                    *value = k.Value;
                    *(float*)(life + _percentage) = k.Percentage;
                    if (k.DamageType < 0) continue;
                    IntPtr last = _lastDamage.Item(k.Entity);
                    if (last != IntPtr.Zero) *(int*)(last + _damageType) = k.DamageType;
                }
            }
            catch (Exception e) { Fail("writing back the undead's life", e); return; }
            finally { Undead.Clear(); }

            if (_announce)
            {
                _announce = false;
                _undone = 0f;
                _nextSummary = Time.unscaledTime + 300f;
                Plugin.Logger.LogInfo($"[Undead] {count} undead in the game's decay filter; their life no longer falls by itself");
            }
            else if (Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 300f;
                Plugin.Logger.LogInfo($"[Undead] {count} undead; {_undone.ToString("0.##", CultureInfo.InvariantCulture)} life points of decay undone in the last 5 minutes");
                _undone = 0f;
            }
        }
    }
}
