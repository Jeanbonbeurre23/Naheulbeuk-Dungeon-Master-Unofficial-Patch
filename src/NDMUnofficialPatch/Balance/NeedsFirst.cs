using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // A minion drops his work for a need as soon as the need arises.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 9 October 2026. AISwitchBehaviourSystem.Run goes
    // through every character with a BehaviourTreeOwnerComponent once per frame. For each of his available behaviours
    // it takes the ScoreData of the behaviour's score component (GetScoreDataForType: TOILET reads
    // AIComputeToiletsScoreComponent, EAT_IN_CANTEEN and EAT_IN_TAVERN read AIComputeEatScoreComponent, the three
    // ENTERTAINMENT behaviours read AIComputeFunScoreComponent, and so on) and computes its FinalScore from its
    // BaseScore with the behaviour's evaluator (AIEvaluator in the configs: a curve, a line capped at a maximum, or the
    // base score itself). It changes the character's behaviour only when one of three flags of his
    // BehaviourTreeOwnerComponent is set: ShouldComputePriority (his behaviour tree has ended), TryUpdatePriority (set
    // by TryRecomputeBehaviourPriorityTask, a node some trees run between two steps) or ForceUpdatePriority (set by
    // BehaviourTreeUtility.ForceUpdateBehaviour, which the game calls on an alert, a resignation, a discussion and
    // about forty other events). With ForceUpdatePriority it stops the running tree, the game's fallbacks release what
    // the tree held, and the behaviour with the best FinalScore starts.
    //
    // A need's base score is its deficit, one minus the gauge's fraction. Sleeping and eating use the curve
    // C_PRIMARY_NEEDS (0 down to a gauge of 70%, 0.35 at 67%, 0.55 at 30%, 1 at 10% and below); hygiene, the toilet and
    // the three entertainments use C_SECONDARY_NEEDS (0 down to 70%, 0.3 at 60%, 0.45 at 30%, 0.8 at 10% and below).
    // Work, training and healing score 0.5 at most (S_WORK), chatting 0.9 (DISCUSS), a due salary 0.9 (S_SALARY), and
    // the morale of a minion starts to fall when a need goes below 30% (the gauge step that applies the "dissatisfied"
    // state). A hygiene, toilet or fun need therefore outranks work only below about 27%, and only at the next of the
    // three flags, which a working minion's tree may not raise for minutes. In a test game, 174 minions resigned
    // in six hours; each had at least two needs below 30% and 76 had not been paid, and some were still at work
    // with a due salary at 0.9 and a toilet at 0.8 as their best scores.
    //
    // A need arises when its gauge falls below 40% (the step at which the game shows the need above the minion's
    // head), a due salary counts as a need, and a need never interrupts a fight, a wounded minion's wait for a healer
    // or another need already being satisfied.
    //
    // Twice a second the patch lists the minions with a need below Balance.NeedsFirstBelow percent or a salary due
    // (RequestSalaryTagComponent). In a prefix on AISwitchBehaviourSystem.Run, for each of them whose behaviour is a job,
    // training, idling or chatting, or a need whose tree has just ended, it takes his most urgent need (the lowest
    // gauge; a due salary counts as 30%) among those the game scores above 0, sets that need's base score to 1 so that
    // its evaluator gives its maximum, and sets to 0 the base and final scores of his work, training, chatting and
    // other needs. When he is working, idling, chatting or training, it also sets ForceUpdatePriority, at most once
    // every 3 seconds. A minion who fights, flees, waits wounded in bed, is being taken to prison or carried off, or
    // satisfies another need is left alone. When a need fails twice within 4 seconds (no free toilet, no bed), or when
    // three forced changes in a row do not take him to a need, that need is left to the game for 30 seconds of game
    // time, and the next most urgent need is tried.
    internal static unsafe class NeedsFirst
    {
        private const int Needs = 6;
        private const int Salary = 5;
        private static readonly string[] NeedNames = { "hunger", "energy", "fun", "hygiene", "toilets", "salary" };

        // Behaviours (EBehaviourType values) that a need interrupts: work, training, idling, chatting.
        private static readonly HashSet<int> Interruptible = new()
        {
            0, 1, 2, 4, 5, 6, 7, 16, 17, 18, 23, 24, 26, 27, 28, 31, 33, 35, 55, 57, 60, 61, 62, 63, 64, 76, 83,
        };

        // Behaviours that satisfy a need: SLEEP, HYGIENE, TOILET, EAT_IN_CANTEEN, EAT_IN_TAVERN, the three
        // ENTERTAINMENT behaviours, GET_SALARY and DEMAND_SALARY.
        private static readonly HashSet<int> NeedBehaviours = new() { 3, 8, 9, 10, 11, 14, 25, 29, 30, 56 };

        private sealed class State
        {
            internal short Gen;
            internal readonly float[] Cooldown = new float[Needs];
            internal readonly int[] QuickFails = new int[Needs];
            internal float LastPush = -1000f, LastForce = -1000f;
            internal int LastKind = -1, ForceFails, ForcedKind = -1;
        }

        private static IntPtr _world;
        private static bool _broken;
        private static float _nextScan, _nextSummary;
        private static int _threshold;
        private static int _current = -1, _should, _force, _base, _final, _percentage;

        private static FramePool _owners, _salaryTags, _jobs;
        private static readonly FramePool[] Gauges = new FramePool[Needs];
        private static readonly (FramePool Pool, int Offset)[] NeedScores = new (FramePool, int)[Needs];
        private static readonly List<(FramePool Pool, int Offset)> WorkScores = new();
        private static readonly List<int> Pressing = new();
        private static readonly Dictionary<int, State> States = new();

        private static readonly int[] Interrupted = new int[Needs], Chained = new int[Needs], Backoffs = new int[Needs];
        private static int _logged, _backoffLogged;

        internal static bool Enabled => Settings.NeedsFirst.Value;

        private static void Layout()
        {
            if (_current >= 0) return;
            // The flags and the behaviour at the offsets the method bodies of game 1.8 use.
            Il2CppRaw.ExpectValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour", 0x84);
            Il2CppRaw.ExpectValueFieldOffset<BehaviourTreeOwnerComponent>("ShouldComputePriority", 0x8c);
            Il2CppRaw.ExpectValueFieldOffset<BehaviourTreeOwnerComponent>("ForceUpdatePriority", 0x8e);
            Il2CppRaw.ExpectValueFieldOffset<MinionHungerComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionEnergyComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionFunComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionHygieneComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionToiletsComponent>("Gauge", 0);
            _should = 0x8c;
            _force = 0x8e;
            _base = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("BaseScore");
            _final = Il2CppRaw.ValueFieldOffset<AIScoreUtility.ScoreData>("FinalScore");
            _percentage = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentPercentage");
            _current = 0x84;
        }

        private static (FramePool, int) Score<T>(EcsWorld world) => (FramePool.Of<T>(world), Il2CppRaw.ValueFieldOffset<T>("ScoreData"));

        private static void Bind(EcsWorld world)
        {
            _owners = FramePool.Of<BehaviourTreeOwnerComponent>(world);
            if (_owners.ItemSize != 152) throw new InvalidOperationException($"BehaviourTreeOwnerComponent is {_owners.ItemSize} bytes, expected 152");
            _salaryTags = FramePool.Of<RequestSalaryTagComponent>(world);
            _jobs = FramePool.Of<JobPracticedComponent>(world);
            Gauges[0] = FramePool.Of<MinionHungerComponent>(world);
            Gauges[1] = FramePool.Of<MinionEnergyComponent>(world);
            Gauges[2] = FramePool.Of<MinionFunComponent>(world);
            Gauges[3] = FramePool.Of<MinionHygieneComponent>(world);
            Gauges[4] = FramePool.Of<MinionToiletsComponent>(world);
            NeedScores[0] = Score<AIComputeEatScoreComponent>(world);
            NeedScores[1] = Score<AIComputeSleepScoreComponent>(world);
            NeedScores[2] = Score<AIComputeFunScoreComponent>(world);
            NeedScores[3] = Score<AIComputeHygieneScoreComponent>(world);
            NeedScores[4] = Score<AIComputeToiletsScoreComponent>(world);
            NeedScores[5] = Score<AIComputeDemandSalaryScoreComponent>(world);
            WorkScores.Clear();
            WorkScores.Add(Score<AIComputeCraftResourcesScoreComponent>(world));
            WorkScores.Add(Score<AIComputeCleanScoreComponent>(world));
            WorkScores.Add(Score<AIComputeCookingScoreComponent>(world));
            WorkScores.Add(Score<AIComputeCarryCorpsesScoreComponent>(world));
            WorkScores.Add(Score<AIComputeTrainScoreComponent>(world));
            WorkScores.Add(Score<AIComputeCoachScoreComponent>(world));
            WorkScores.Add(Score<AIComputeCoachForJobScoreComponent>(world));
            WorkScores.Add(Score<AIComputePaySalaryScoreComponent>(world));
            WorkScores.Add(Score<AIComputeMagicTrainingScoreComponent>(world));
            WorkScores.Add(Score<Discussion.AIComputeDiscussScoreComponent>(world));
            WorkScores.Add(Score<AIComputeWorkBehindCounterScoreComponent>(world));
            WorkScores.Add(Score<AIComputeHealScoreComponent>(world));
            WorkScores.Add(Score<AIComputeTortureEntityScoreComponent>(world));
            WorkScores.Add(Score<AIComputeInvokeScoreComponent>(world));
            WorkScores.Add(Score<AIComputeReloadTrapScoreComponent>(world));
            WorkScores.Add(Score<AIComputeEnchantTrapScoreComponent>(world));
            WorkScores.Add(Score<AIComputeSacrificeScoreComponent>(world));
            States.Clear();
            Pressing.Clear();
        }

        // In a prefix on AISwitchBehaviourSystem.Run, before the game reads the scores of the frame.
        internal static void BeforeSwitch()
        {
            if (_broken) return;
            try
            {
                if (!GameContext.TryWorld(out var world, out int size)) return;
                Layout();
                if (GameContext.WorldPointer != _world)
                {
                    _world = GameContext.WorldPointer;
                    Bind(world);
                    _nextScan = 0f;
                }
                float real = Time.unscaledTime;
                if (real >= _nextScan)
                {
                    _nextScan = real + 0.5f;
                    _threshold = Math.Clamp(Settings.NeedsFirstBelow.Value, 1, 100);
                    Scan(world, size);
                }
                if (real >= _nextSummary)
                {
                    if (_nextSummary > 0f) Summary();
                    _nextSummary = real + 300f;
                }
                if (Pressing.Count == 0) return;
                _owners.Refresh();
                _salaryTags.Refresh();
                foreach (var g in Gauges) g.Refresh();
                foreach (var s in NeedScores) s.Pool.Refresh();
                foreach (var s in WorkScores) s.Pool.Refresh();
                float now = Time.time;
                foreach (int e in Pressing) Handle(world, e, now);
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Logger.LogWarning($"[Needs] failed, minions follow the game's own priorities from now on: {e}");
            }
        }

        // The minions with a need below the threshold or a salary due.
        private static void Scan(EcsWorld world, int size)
        {
            Pressing.Clear();
            foreach (var g in Gauges) g.Refresh();
            _salaryTags.Refresh();
            var dead = RawPool.Of<DeathComponent>(world, -1);
            var outside = RawPool.Of<OutsideDungeonTag>(world, -1);
            foreach (int e in RawPool.Of<MinionTag>(world, -1).Entities())
            {
                if (!world.IsEntityAlive(e, size) || dead.Has(e) || outside.Has(e)) continue;
                for (int k = 0; k < Needs; k++)
                    if (Urgency(e, k) < float.MaxValue) { Pressing.Add(e); break; }
            }
            if (States.Count > 4 * Math.Max(Pressing.Count, 64))
                foreach (int e in States.Keys.ToList())
                    if (!world.IsEntityAlive(e, size)) States.Remove(e);
        }

        // The need's gauge in percent when it is below the threshold, 30 for a salary due, otherwise float.MaxValue.
        private static float Urgency(int e, int k)
        {
            if (k == Salary) return _salaryTags.Has(e) ? 30f : float.MaxValue;
            IntPtr g = Gauges[k].Item(e);
            if (g == IntPtr.Zero) return float.MaxValue;
            float pct = *(float*)(g + _percentage);
            return pct < _threshold ? pct : float.MaxValue;
        }

        private static IntPtr ScoreOf(int k, int e)
        {
            IntPtr item = NeedScores[k].Pool.Item(e);
            return item == IntPtr.Zero ? IntPtr.Zero : item + NeedScores[k].Offset;
        }

        private static void Handle(EcsWorld world, int e, float now)
        {
            IntPtr owner = _owners.Item(e);
            if (owner == IntPtr.Zero) return;
            int current = *(int*)(owner + _current);
            bool onNeed = NeedBehaviours.Contains(current);
            if (!onNeed && !Interruptible.Contains(current)) return; // fights, wounds, prison and the like

            short gen = world.GetEntityGen(e);
            if (!States.TryGetValue(e, out var st) || st.Gen != gen) States[e] = st = new State { Gen = gen };
            if (onNeed)
            {
                st.ForceFails = 0;
                if (*(byte*)(owner + _should) == 0) return; // a need under way is not interrupted
            }

            int best = -1;
            float bestUrgency = float.MaxValue;
            for (int k = 0; k < Needs; k++)
            {
                if (now < st.Cooldown[k]) continue;
                float u = Urgency(e, k);
                if (u >= bestUrgency) continue;
                IntPtr s = ScoreOf(k, e);
                if (s == IntPtr.Zero || *(float*)(s + _base) <= 0f) continue; // the game does not offer this need now
                best = k;
                bestUrgency = u;
            }
            if (best < 0) return;

            foreach (var (pool, offset) in WorkScores)
            {
                IntPtr item = pool.Item(e);
                if (item == IntPtr.Zero) continue;
                *(float*)(item + offset + _base) = 0f;
                *(float*)(item + offset + _final) = 0f;
            }
            for (int k = 0; k < Needs; k++)
            {
                IntPtr s = ScoreOf(k, e);
                if (s == IntPtr.Zero) continue;
                float v = k == best ? 1f : 0f;
                *(float*)(s + _base) = v;
                *(float*)(s + _final) = v;
            }

            if (onNeed)
            {
                Push(e, st, best, now);
                Chained[best]++;
                return;
            }
            if (now - st.LastForce < 3f) return;
            if (st.LastForce > 0f && st.ForcedKind >= 0 && now - st.LastForce < 10f && ++st.ForceFails >= 3)
            {
                // Three forced changes did not take him to a need: the game keeps his behaviour for this need.
                Backoff(e, st, st.ForcedKind, now, "the game kept his current behaviour three times");
                st.ForceFails = 0;
                return;
            }
            *(byte*)(owner + _force) = 1;
            st.LastForce = now;
            st.ForcedKind = best;
            Interrupted[best]++;
            Push(e, st, best, now);
            if (_logged < 40)
            {
                _logged++;
                Plugin.Logger.LogInfo($"[Needs] {Name(e)} leaves {(EBehaviourType)current} for his {NeedNames[best]} ({Level(e, best)})");
                if (_logged == 40) Plugin.Logger.LogInfo("[Needs] further interruptions are counted in the 5-minute summary only");
            }
        }

        // A need chosen again within 4 seconds of game time has failed; at the second failure it is left to the game
        // for 30 seconds.
        private static void Push(int e, State st, int k, float now)
        {
            if (st.LastKind == k && now - st.LastPush < 4f)
            {
                if (++st.QuickFails[k] >= 2) Backoff(e, st, k, now, "it could not be met twice in a row");
            }
            else st.QuickFails[k] = 0;
            st.LastKind = k;
            st.LastPush = now;
        }

        private static void Backoff(int e, State st, int k, float now, string why)
        {
            st.Cooldown[k] = now + 30f;
            st.QuickFails[k] = 0;
            st.LastKind = -1;
            Backoffs[k]++;
            if (_backoffLogged < 30)
            {
                _backoffLogged++;
                Plugin.Logger.LogInfo($"[Needs] {Name(e)}: his {NeedNames[k]} ({Level(e, k)}) is left to the game for 30 seconds, since {why}");
                if (_backoffLogged == 30) Plugin.Logger.LogInfo("[Needs] further give-ups are counted in the 5-minute summary only");
            }
        }

        private static string Level(int e, int k)
        {
            if (k == Salary) return "pay due";
            IntPtr g = Gauges[k].Item(e);
            return g == IntPtr.Zero ? "?" : $"{*(float*)(g + _percentage):0}%";
        }

        private static string Name(int e)
        {
            string job = "";
            try
            {
                _jobs.Refresh();
                IntPtr j = _jobs.Item(e);
                if (j != IntPtr.Zero) job = ", " + ((JobPracticedComponent*)j)->JobPracticed;
            }
            catch { }
            try
            {
                string n = GameContext.Minions.GetMinionFullName(e);
                if (!string.IsNullOrWhiteSpace(n)) return $"{n} ({e}{job})";
            }
            catch { }
            return $"entity {e}{job}";
        }

        private static void Summary()
        {
            int total = Interrupted.Sum() + Chained.Sum() + Backoffs.Sum();
            if (total == 0) return;
            string Row(int[] a) => string.Join(", ", Enumerable.Range(0, Needs).Where(k => a[k] > 0).Select(k => $"{NeedNames[k]} {a[k]}"));
            Plugin.Logger.LogInfo($"[Needs] last 5 minutes: work left for a need: {Row(Interrupted)}; next need chosen when one ended: {Row(Chained)}; needs left to the game for 30 seconds: {Row(Backoffs)}; minions with a need below {_threshold}% or a salary due at the last check: {Pressing.Count}");
            Array.Clear(Interrupted, 0, Needs);
            Array.Clear(Chained, 0, Needs);
            Array.Clear(Backoffs, 0, Needs);
        }
    }

    [HarmonyPatch(typeof(AISwitchBehaviourSystem), nameof(AISwitchBehaviourSystem.Run))]
    internal static class NeedsFirstScores
    {
        private static bool Prepare() => Settings.NeedsFirst.Value;
        private static void Prefix() => NeedsFirst.BeforeSwitch();
    }
}
