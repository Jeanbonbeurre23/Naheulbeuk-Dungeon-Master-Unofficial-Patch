using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Fixes
{
    // Ends the wait in which every pharmagician lies in bed waiting for another pharmagician.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 1 October 2026. A wounded minion carries a
    // RequestHealComponent. AIComputeRequestHealScoreJob gives REQUEST_HEAL a raw score of 1 to every minion that carries
    // one, whenever an available dormitory holds a DORMITORY_HEALTHCARE prop, and the behaviour's curve (AIEvaluator,
    // REQUEST_HEAL) turns it into 0.9, above every need and above every job's work (0.5, HEAL included). BT_RequestHeal
    // finds a bed (FindSimpleBedTask), walks to it and waits (RequestHealActionSystem) until a pharmagician has healed
    // him, or until MinionUtility.IsTherePharmagicianInDungeon returns false. That method returns false only when no
    // pharmagician other than the minion himself exists (m_pharmagicianFilter: every entity with
    // AIComputeHealScoreComponent, not dead, not outside the dungeon), so a pharmagician waiting in bed still counts.
    // When every pharmagician is wounded, each one waits for the others and no one heals anyone. On 30 September 2026,
    // at the last morale-watch summary, all six pharmagicians of the test dungeon and 31 minions in all were waiting.
    //
    // When every pharmagician is running REQUEST_HEAL, this patch picks the one with the lowest entity id and gets him
    // up. A postfix on IsTherePharmagicianInDungeon returns false for him, which ends his wait and makes his bed search
    // fail. A postfix on AIComputeRequestHealScoreJobSystem.Run, which completes its job before returning
    // (JobHandle.ScheduleBatchedJobsAndComplete), sets his REQUEST_HEAL score to 0, so AISwitchBehaviourSystem gives him
    // his other behaviours, healing among them (the HEAL score does not depend on the healer's own wounds). The others
    // keep waiting, since a pharmagician exists for them, and he heals them. He is let go as soon as another pharmagician
    // is up and not wounded, or when he is no longer wounded; he then lies down in his turn if he still needs to.
    internal static unsafe class HealDeadlock
    {
        private static int _breaker = -1;
        private static short _breakerGen;
        private static string _breakerName;
        private static IntPtr _world;
        private static float _nextCheck;
        private static int _current = -1, _base, _final;
        private static bool _errorLogged;

        internal static int Breaker => _breaker;

        private static void Layout()
        {
            if (_current >= 0) return;
            Il2CppRaw.ExpectValueFieldOffset<AIComputeRequestHealComponent>("ScoreData", 0);
            _base = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("BaseScore");
            _final = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("FinalScore");
            _current = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour");
        }

        // After the REQUEST_HEAL scores of the frame are written.
        internal static void AfterScores()
        {
            try
            {
                if (!GameContext.TryWorld(out var world, out int size)) return;
                Layout();
                if (_world != GameContext.WorldPointer)
                {
                    _world = GameContext.WorldPointer;
                    _breaker = -1;
                }
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + 0.5f;
                    Evaluate(world, size);
                }
                if (_breaker < 0) return;
                IntPtr s = RawPool.Of<AIComputeRequestHealComponent>(world, -1).Item(_breaker);
                if (s == IntPtr.Zero) return;
                *(float*)(s + _base) = 0f;
                *(float*)(s + _final) = 0f;
            }
            catch (Exception e) { Report(e); }
        }

        private static void Evaluate(EcsWorld world, int size)
        {
            var healers = RawPool.Of<AIComputeHealScoreComponent>(world, -1);
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            var wounded = RawPool.Of<RequestHealComponent>(world, -1);
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, -1);
            // Necromancers given the heal by NecromancerCultistHealing heal vampires only and are not counted here.
            var list = healers.Entities().Where(e => world.IsEntityAlive(e, size) && !dead.Has(e) && !outside.Has(e) && !Balance.NecromancerCultistHealing.IsNecromancer(world, e)).ToList();
            bool Waiting(int e)
            {
                IntPtr b = brains.Item(e);
                return b != IntPtr.Zero && *(EBehaviourType*)(b + _current) == EBehaviourType.REQUEST_HEAL;
            }

            if (_breaker >= 0)
            {
                string why = null;
                if (!list.Contains(_breaker) || world.GetEntityGen(_breaker) != _breakerGen) why = "he is no longer a pharmagician in the dungeon";
                else if (!wounded.Has(_breaker)) why = "he is no longer wounded";
                else
                {
                    int able = list.FirstOrDefault(e => e != _breaker && !Waiting(e) && !wounded.Has(e), -1);
                    if (able >= 0) why = $"{Name(able)} is up and not wounded, and can heal him";
                }
                if (why != null)
                {
                    Plugin.Logger.LogInfo($"[Heal] {_breakerName} is let go: {why}");
                    _breaker = -1;
                }
            }

            if (_breaker < 0 && list.Count > 0 && list.All(Waiting))
            {
                _breaker = list.Min();
                _breakerGen = world.GetEntityGen(_breaker);
                _breakerName = Name(_breaker);
                int waiting = brains.Entities().Count(Waiting);
                Plugin.Logger.LogInfo($"[Heal] every pharmagician ({list.Count}) is waiting to be healed, with {waiting} minion(s) waiting in all; {_breakerName} gets up to heal the others");
            }
        }

        private static string Name(int e)
        {
            try { string n = GameContext.Minions.GetMinionFullName(e); if (!string.IsNullOrWhiteSpace(n)) return n; }
            catch { }
            return $"Minion {e}";
        }

        internal static void Report(Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Heal] error, the game's healing goes on unchanged: {e.Message}");
        }
    }

    [HarmonyPatch(typeof(AIComputeRequestHealScoreJobSystem), nameof(AIComputeRequestHealScoreJobSystem.Run))]
    internal static class HealDeadlockScores
    {
        private static bool Prepare() => Settings.HealDeadlock.Value;
        private static void Postfix() => HealDeadlock.AfterScores();
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.IsTherePharmagicianInDungeon))]
    internal static class HealDeadlockWait
    {
        private static bool Prepare() => Settings.HealDeadlock.Value;
        private static void Postfix(int __0, ref bool __result)
        {
            if (__result && __0 >= 0 && __0 == HealDeadlock.Breaker) __result = false;
        }
    }
}
