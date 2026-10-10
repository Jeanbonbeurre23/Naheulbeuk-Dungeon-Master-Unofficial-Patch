using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Every minion with a job can train in the training room.
    //
    // Game 1.8, from the method bodies, the behaviour trees and the configs bundle, read on 9 October 2026. A minion
    // can choose a behaviour only when he carries its score component: BehaviourTreeUtility.FillAvailablesBehaviours
    // lists, from the behaviour config his BehaviourTreeOwnerConfigComponent names, the behaviours whose score pool has
    // him. MinionBehaviourConfig lists TRAIN for every minion, but only the guard's job components
    // (WorldConfig.MinionJobComponents[GUARD]) hold AIComputeTrainScoreConfig, which adds AIComputeTrainScoreComponent.
    // AIComputeTrainScoreJob gives a base score of 1 to a minion whose grade is below his maximum grade
    // (GradeNativeComponent) and 0 otherwise, and TRAIN's evaluator (S_WORK, the evaluator of every job's work) turns
    // it into 0.5. The training tree (BT_Train) has no job condition: it books a melee dummy, else a ranged one, in a
    // training room of the minion's prestige, walks to it and runs TrainTask, whose action system applies the
    // training state of the prop while he trains.
    //
    // Every minion, summons included, can go to the training room to gain grades. Guards keep the game's rule, and
    // the others train only when their job has nothing for them.
    //
    // Every 10 seconds the patch gives AIComputeTrainScoreConfig (the guard's own config object) to every character
    // whose behaviour config is MinionBehaviourConfig, who has a GradeNativeComponent and no train score yet, through
    // AComponentConfig.AddComponents and SetupComponents, and empties his list of available behaviours so that the game
    // builds it again. In a prefix on AISwitchBehaviourSystem.Run it caps the train base score of every non-guard at
    // 0.4, which TRAIN's evaluator turns into 0.2: above idling (0.01), below any job's work (0.5) and below a need the
    // game already scores. With the setting off, the train score is removed again from every non-guard.
    internal static unsafe class TrainingForAll
    {
        private const float Cap = 0.4f;

        private static IntPtr _world;
        private static bool _broken, _noConfigLogged;
        private static float _nextPass, _nextSummary;
        private static AComponentConfig _trainConfig;
        private static int _availOffset = -1, _currentOffset, _scoreOffset, _base, _final, _given, _logged;
        private static FramePool _scores;
        private static readonly List<int> Trainees = new();
        private static readonly Dictionary<IntPtr, bool> MinionConfigs = new();

        private static void Layout()
        {
            if (_availOffset >= 0) return;
            Il2CppRaw.ExpectValueFieldOffset<BehaviourTreeOwnerComponent>("AvailableBehavioursIndex", 0x10);
            _currentOffset = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour");
            _scoreOffset = Il2CppRaw.ValueFieldOffset<AIComputeTrainScoreComponent>("ScoreData");
            _base = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("BaseScore");
            _final = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("FinalScore");
            _availOffset = 0x10;
        }

        internal static void Update()
        {
            if (_broken || !GameContext.Ready || !GameContext.TryWorld(out var world, out int size)) return;
            float now = Time.unscaledTime;
            if (GameContext.WorldPointer != _world)
            {
                _world = GameContext.WorldPointer;
                _trainConfig = null;
                _scores = null;
                Trainees.Clear();
                MinionConfigs.Clear();
                _nextPass = now + 5f;
            }
            if (now < _nextPass) return;
            _nextPass = now + 10f;
            try
            {
                Layout();
                _scores ??= FramePool.Of<AIComputeTrainScoreComponent>(world);
                if (Settings.EveryoneTrains.Value) Pass(world, size);
                else Strip(world, size);
                if (now >= _nextSummary)
                {
                    if (_nextSummary > 0f && Settings.EveryoneTrains.Value) Summary(world);
                    _nextSummary = now + 300f;
                }
            }
            catch (Exception e)
            {
                _broken = true;
                Trainees.Clear();
                Plugin.Logger.LogWarning($"[Training] failed, only guards train from now on: {e}");
            }
        }

        private static AComponentConfig TrainConfig()
        {
            if (_trainConfig != null) return _trainConfig;
            var guard = GameContext.GameData.WorldConfig.MinionJobComponents.m_internalArray[(int)JobType.GUARD];
            var configs = guard?.ComponentConfigs;
            if (configs != null)
                for (int i = 0; i < configs.Length; i++)
                    if (configs[i] != null && Il2CppRaw.ClassName(configs[i].Pointer) == "AIComputeTrainScoreConfig")
                        return _trainConfig = configs[i];
            if (!_noConfigLogged) Plugin.Logger.LogWarning("[Training] the guard's job has no AIComputeTrainScoreConfig; nobody else is given training");
            _noConfigLogged = true;
            return null;
        }

        // Whether the character's behaviour config is MinionBehaviourConfig, cached by owner config.
        private static bool UsesMinionBehaviours(RawPool owners, int e)
        {
            IntPtr item = owners.Item(e);
            if (item == IntPtr.Zero) return false;
            IntPtr config = *(IntPtr*)(item + Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerConfigComponent>("m_config"));
            if (config == IntPtr.Zero) return false;
            if (!MinionConfigs.TryGetValue(config, out bool yes))
                MinionConfigs[config] = yes = new BehaviourTreeOwnerConfig(config).BehaviourConfig?.name == "MinionBehaviourConfig";
            return yes;
        }

        private static bool IsGuard(RawPool jobs, int e)
        {
            IntPtr j = jobs.Item(e);
            return j != IntPtr.Zero && ((JobPracticedComponent*)j)->JobPracticed == JobType.GUARD;
        }

        private static void Pass(EcsWorld world, int size)
        {
            var config = TrainConfig();
            if (config == null) return;
            var owners = RawPool.Of<BehaviourTreeOwnerConfigComponent>(world, -1);
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152);
            var grades = RawPool.Of<GradeNativeComponent>(world, -1);
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var scores = RawPool.Of<AIComputeTrainScoreComponent>(world, -1);
            var gameData = GameContext.GameData;
            int budget = 60, given = 0;
            Trainees.Clear();
            foreach (int e in owners.Entities())
            {
                if (!world.IsEntityAlive(e, size) || dead.Has(e) || !grades.Has(e) || !brains.Has(e)) continue;
                if (!UsesMinionBehaviours(owners, e)) continue;
                bool guard = IsGuard(jobs, e);
                if (scores.Has(e))
                {
                    if (!guard) Trainees.Add(e);
                    continue;
                }
                if (guard || budget <= 0) continue;
                if (!config.AddComponents(world, e, false)) continue;
                config.SetupComponents(world, gameData, e, false);
                // The game builds the list of available behaviours again, now with TRAIN.
                IntPtr b = brains.Item(e);
                *(IntPtr*)(b + _availOffset) = IntPtr.Zero;
                *(int*)(b + _availOffset + IntPtr.Size) = 0;
                Trainees.Add(e);
                budget--;
                given++;
                _given++;
                if (_logged < 10)
                {
                    _logged++;
                    Plugin.Logger.LogInfo($"[Training] {Name(e)} can now train in the training room when he has no work");
                }
            }
            if (given > 0) Plugin.Logger.LogInfo($"[Training] training given to {given} more character(s), {_given} this session");
        }

        private static void Strip(EcsWorld world, int size)
        {
            Trainees.Clear();
            var owners = RawPool.Of<BehaviourTreeOwnerConfigComponent>(world, -1);
            var jobs = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent));
            var scores = RawPool.Of<AIComputeTrainScoreComponent>(world, -1);
            int count = 0;
            foreach (int e in scores.Entities())
            {
                if (!world.IsEntityAlive(e, size) || IsGuard(jobs, e) || !UsesMinionBehaviours(owners, e)) continue;
                Del<AIComputeTrainScoreComponent>(world, e);
                Del<AIComputeTrainScoreConfigComponent>(world, e);
                count++;
            }
            if (count > 0) Plugin.Logger.LogInfo($"[Training] setting off: training removed from {count} non-guard(s)");
        }

        private static void Del<T>(EcsWorld world, int e)
        {
            IEcsPool pool = world.GetPoolByType(Il2CppType.Of<T>());
            if (pool != null && pool.Has(e)) pool.Del(e, false);
        }

        // In a prefix on AISwitchBehaviourSystem.Run: non-guards score training below any work.
        internal static void BeforeSwitch()
        {
            if (_broken || _scores == null || Trainees.Count == 0 || GameContext.WorldPointer != _world) return;
            try
            {
                _scores.Refresh();
                foreach (int e in Trainees)
                {
                    IntPtr item = _scores.Item(e);
                    if (item == IntPtr.Zero) continue;
                    float* b = (float*)(item + _scoreOffset + _base);
                    if (*b <= Cap) continue;
                    *b = Cap;
                    *(float*)(item + _scoreOffset + _final) = Cap * 0.5f;
                }
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Logger.LogWarning($"[Training] failed while scoring, only guards train from now on: {e}");
            }
        }

        private static void Summary(EcsWorld world)
        {
            var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, 152);
            int training = 0;
            foreach (int e in Trainees)
            {
                IntPtr b = brains.Item(e);
                if (b != IntPtr.Zero && *(EBehaviourType*)(b + _currentOffset) == EBehaviourType.TRAIN) training++;
            }
            Plugin.Logger.LogInfo($"[Training] {Trainees.Count} non-guard(s) can train; {training} of them training at this moment");
        }

        private static string Name(int e)
        {
            try { string n = GameContext.Minions.GetMinionFullName(e); if (!string.IsNullOrWhiteSpace(n)) return $"{n} (entity {e})"; }
            catch { }
            return $"entity {e}";
        }
    }

    [HarmonyPatch(typeof(AISwitchBehaviourSystem), nameof(AISwitchBehaviourSystem.Run))]
    internal static class TrainingScores
    {
        private static bool Prepare() => Settings.EveryoneTrains.Value;
        private static void Prefix() => TrainingForAll.BeforeSwitch();
    }
}
