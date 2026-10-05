using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Diagnostics
{
    // Writes to the log what a minion is doing while his morale falls. It replaces the guard watch of 0.12.0, which
    // watched guards only.
    //
    // The session of 29 September 2026 (0.11.0) recorded 47 resignations: 27 guards, 9 artisans, 8 cooks, 2 spies and a
    // domestic. Most of them had morale 100 or near it at the report before, and fell to 0 within 1 to 4 minutes while
    // their hunger, energy, fun, hygiene and toilets all dropped at once. The same log shows other minions whose five
    // needs kept the same values across up to ten reports (50 minutes), which the game does when it pauses a minion's
    // gauges.
    //
    // Game 1.8, from the method bodies. A minion's behaviour is chosen by score: BehaviourTreeOwnerComponent holds the
    // behaviours available to him (AvailableBehaviours), their scores (BehvioursScores) and the one running
    // (CurrentBehaviour). A gauge falls and rises at the rates of Gauge.DecreasePerSecondRealTime and
    // IncreasePerSecondRealTime, FloatWithModifiers whose modifiers come from the states applied to the minion: a state
    // that carries a StatsModifierConfigComponent adds its StatModifiers (StatType, e.g. DECREASE_MORALE_GAUGE, an
    // operator and a value) to the matching rate. GaugeUtility.PauseAllGauges forces the decrease rate of every gauge to
    // 0 (m_forcedValue); RequestHealActionSystem calls it while a wounded minion waits for a pharmagician (the wait lasts
    // as long as MinionUtility.IsTherePharmagicianInDungeon is true), StrikeActionSystem for a minion on strike, and
    // DiscussionUtility during a discussion. A state that carries a StopGaugeModifierConfigComponent stops the gauges it
    // lists (StopGaugeModifier: gauge and rate).
    //
    // Every 5 seconds of real time, for every player minion: when his morale fell by 3 points or more since the last
    // sample, or is below 25, one [MoraleWatch] line (at most one per minion every 15 seconds, 2000 per session) with his
    // needs and negative states (MoraleProbe.Describe), his morale before and now, the game's time scale, the morale
    // rates with their modifiers, the decrease rate of each need (marked "paused" when forced), the behaviour running and
    // the five best-scored behaviours, every state applied with its stat modifiers and the gauges it stops, and his room
    // searches of the last 3 minutes (RoomNeeds.RecentSearches). Every 5 minutes, per job: how many minions, their
    // average morale and morale fall rate, how many have their needs paused, and the behaviours running; and, for the
    // minions whose needs are paused, the behaviours they run.
    internal static unsafe class MoraleWatch
    {
        private const float Period = 5f;
        private const float SummaryPeriod = 300f;
        private const float MinionInterval = 15f;
        private const float SearchWindow = 180f;
        private const int MaxLines = 2000;
        private const int MaxStates = 30;

        private static readonly Dictionary<int, float> LastMorale = new();
        private static readonly Dictionary<int, float> LastLine = new();
        private static IntPtr _world;
        private static float _next, _nextSummary;
        private static int _linesLogged;
        private static bool _layoutChecked, _errorLogged, _capLogged;
        private static int _percentage, _decrease, _increase, _computed, _forced, _available, _scores, _current;
        private static int _statsConfig, _stopConfig;
        private static float _invalidForce;

        private static readonly (string Name, Func<EcsWorld, RawPool> Pool)[] Needs =
        {
            ("hunger", w => RawPool.Of<MinionHungerComponent>(w, -1)),
            ("energy", w => RawPool.Of<MinionEnergyComponent>(w, -1)),
            ("fun", w => RawPool.Of<MinionFunComponent>(w, -1)),
            ("hygiene", w => RawPool.Of<MinionHygieneComponent>(w, -1)),
            ("toilets", w => RawPool.Of<MinionToiletsComponent>(w, -1)),
        };

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            // Every minion gauge component starts with its Gauge.
            Il2CppRaw.ExpectValueFieldOffset<MinionMoraleComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionHungerComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionEnergyComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionFunComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionHygieneComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionToiletsComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<FloatWithModifiers>("m_modifiers", 0);
            _percentage = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentPercentage");
            _decrease = Il2CppRaw.ValueFieldOffset<Gauge>("DecreasePerSecondRealTime");
            _increase = Il2CppRaw.ValueFieldOffset<Gauge>("IncreasePerSecondRealTime");
            _computed = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_computedValue");
            _forced = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_forcedValue");
            _invalidForce = FloatWithModifiers.INVALID_FORCE_VALUE;
            _available = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("AvailableBehaviours");
            _scores = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("BehvioursScores");
            _current = Il2CppRaw.ValueFieldOffset<BehaviourTreeOwnerComponent>("CurrentBehaviour");
            _statsConfig = Il2CppRaw.ValueFieldOffset<StatsModifierConfigComponent>("m_config");
            _stopConfig = Il2CppRaw.ValueFieldOffset<StopGaugeModifierConfigComponent>("m_config");
            _layoutChecked = true;
        }

        private sealed class JobTally
        {
            public int Count, Paused;
            public double Morale, FallRate;
            public readonly Dictionary<EBehaviourType, int> Running = new();
        }

        internal static void Update()
        {
            if (!GameContext.Ready || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Period;
            try
            {
                CheckLayout();
                if (GameContext.WorldPointer != _world)
                {
                    _world = GameContext.WorldPointer;
                    LastMorale.Clear();
                    LastLine.Clear();
                    _nextSummary = Time.unscaledTime + 30f;
                }
                if (!GameContext.TryWorld(out var world, out _)) return;
                var morale = RawPool.Of<MinionMoraleComponent>(world, -1);
                var brains = RawPool.Of<BehaviourTreeOwnerComponent>(world, -1);
                var hunger = RawPool.Of<MinionHungerComponent>(world, -1);
                bool summary = Time.unscaledTime >= _nextSummary;
                if (summary) _nextSummary = Time.unscaledTime + SummaryPeriod;
                var tallies = new Dictionary<string, JobTally>();
                var pausedRunning = new Dictionary<EBehaviourType, int>();

                foreach (var m in GameContext.ListMinions())
                {
                    IntPtr g = morale.Item(m.Entity);
                    if (g == IntPtr.Zero) continue;
                    IntPtr b = brains.Item(m.Entity);
                    float now = *(float*)(g + _percentage);

                    if (summary)
                    {
                        string job = m.HasJob ? m.Job.ToString() : "no job";
                        if (!tallies.TryGetValue(job, out var t)) tallies[job] = t = new JobTally();
                        t.Count++;
                        t.Morale += now;
                        t.FallRate += *(float*)(g + _decrease + _computed);
                        bool paused = Paused(hunger.Item(m.Entity));
                        if (b != IntPtr.Zero)
                        {
                            var running = *(EBehaviourType*)(b + _current);
                            t.Running[running] = t.Running.TryGetValue(running, out int c) ? c + 1 : 1;
                            if (paused) pausedRunning[running] = pausedRunning.TryGetValue(running, out int p) ? p + 1 : 1;
                        }
                        if (paused) t.Paused++;
                    }

                    bool fell = LastMorale.TryGetValue(m.Entity, out float before) && before - now >= 3f;
                    LastMorale[m.Entity] = now;
                    if (!fell && now >= 25f) continue;
                    if (LastLine.TryGetValue(m.Entity, out float last) && Time.unscaledTime - last < MinionInterval) continue;
                    if (_linesLogged >= MaxLines)
                    {
                        if (!_capLogged) Plugin.Logger.LogInfo($"[MoraleWatch] {MaxLines} lines written, no more lines this session (summaries continue)");
                        _capLogged = true;
                        continue;
                    }
                    _linesLogged++;
                    LastLine[m.Entity] = Time.unscaledTime;
                    Plugin.Logger.LogInfo(Line(world, m.Entity, g, b, fell ? before : (float?)null, now));
                }

                if (summary && tallies.Count > 0)
                {
                    Plugin.Logger.LogInfo($"[MoraleWatch] time scale x{F(Time.timeScale)}; per job (minions, average morale, average morale fall rate, needs paused): " +
                        string.Join("; ", tallies.OrderByDescending(p => p.Value.Count).Select(p =>
                            $"{p.Key} {p.Value.Count}, {F((float)(p.Value.Morale / p.Value.Count))}, {F((float)(p.Value.FallRate / p.Value.Count))}, {p.Value.Paused}")));
                    foreach (var p in tallies.OrderByDescending(p => p.Value.Count))
                        Plugin.Logger.LogInfo($"[MoraleWatch] {p.Key} running: " + string.Join(", ", p.Value.Running.OrderByDescending(r => r.Value).Select(r => $"{r.Key} x{r.Value}")));
                    if (pausedRunning.Count > 0)
                        Plugin.Logger.LogInfo("[MoraleWatch] minions with paused needs, running: " + string.Join(", ", pausedRunning.OrderByDescending(r => r.Value).Select(r => $"{r.Key} x{r.Value}")));
                }
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[MoraleWatch] watching morale failed, further errors are not logged: {e.Message}");
            }
        }

        private static string Line(EcsWorld world, int entity, IntPtr moraleGauge, IntPtr brain, float? before, float now)
        {
            var sb = new StringBuilder("[MoraleWatch] ");
            sb.Append(MoraleProbe.Describe(entity, out _));
            sb.Append(" | morale ").Append(before.HasValue ? F(before.Value) + " -> " : "").Append(F(now));
            sb.Append(" | time scale x").Append(F(Time.timeScale));
            sb.Append(" | morale fall ").Append(Rate(moraleGauge + _decrease)).Append(", rise ").Append(Rate(moraleGauge + _increase));
            sb.Append(" | need falls:");
            foreach (var n in Needs)
            {
                IntPtr c = n.Pool(world).Item(entity);
                if (c == IntPtr.Zero) continue;
                sb.Append(' ').Append(n.Name).Append(' ').Append(F(*(float*)(c + _decrease + _computed)));
                if (Paused(c)) sb.Append(" (paused)");
            }
            if (brain != IntPtr.Zero) sb.Append(" | ").Append(Brain(brain));
            sb.Append(" | states: ").Append(States(world, entity));
            string searches = RoomNeeds.RecentSearches(entity, SearchWindow);
            sb.Append(" | searches: ").Append(searches ?? "none in the last 3 minutes");
            return sb.ToString();
        }

        // A gauge whose decrease rate is forced (GaugeUtility.PauseAllGauges forces it to 0).
        private static bool Paused(IntPtr gauge)
        {
            if (gauge == IntPtr.Zero) return false;
            float forced = *(float*)(gauge + _decrease + _forced);
            return forced != _invalidForce;
        }

        // A rate: its computed value, its forced value when there is one, and its modifiers (a NativeList: a pointer to
        // the list, whose first fields are the data pointer and the length).
        private static string Rate(IntPtr fwm)
        {
            var sb = new StringBuilder(F(*(float*)(fwm + _computed)));
            float forced = *(float*)(fwm + _forced);
            if (forced != _invalidForce) sb.Append(" (forced ").Append(F(forced)).Append(')');
            IntPtr list = *(IntPtr*)fwm;
            if (list == IntPtr.Zero) return sb.ToString();
            IntPtr data = *(IntPtr*)list;
            int length = *(int*)(list + IntPtr.Size);
            if (data == IntPtr.Zero || length <= 0 || length > 64) return sb.ToString();
            sb.Append(" [");
            for (int i = 0; i < length; i++)
            {
                var mod = *(ValueModifier*)(data + i * sizeof(ValueModifier));
                if (i > 0) sb.Append(", ");
                sb.Append(mod.Operator).Append(' ').Append(F(mod.Value));
            }
            return sb.Append(']').ToString();
        }

        // The behaviour running and the five best-scored available behaviours.
        private static string Brain(IntPtr brain)
        {
            var current = *(EBehaviourType*)(brain + _current);
            IntPtr types = *(IntPtr*)(brain + _available);
            int typeCount = *(int*)(brain + _available + IntPtr.Size);
            IntPtr scores = *(IntPtr*)(brain + _scores);
            int scoreCount = *(int*)(brain + _scores + IntPtr.Size);
            var sb = new StringBuilder("running ").Append(current);
            if (types == IntPtr.Zero || scores == IntPtr.Zero || typeCount <= 0 || typeCount > 128 || scoreCount != typeCount)
                return sb.Append($" (scores unreadable: {typeCount} behaviours, {scoreCount} scores)").ToString();
            var list = new List<(EBehaviourType Type, float Score)>();
            for (int i = 0; i < typeCount; i++) list.Add((*(EBehaviourType*)(types + i * 4), *(float*)(scores + i * 4)));
            sb.Append("; best scores: ").Append(string.Join(", ", list.OrderByDescending(p => p.Score).Take(5).Select(p => $"{p.Type} {F(p.Score)}")));
            return sb.ToString();
        }

        // Every state applied to the minion: its name, the stat modifiers it adds and the gauges it stops.
        private static string States(EcsWorld world, int entity)
        {
            var stats = RawPool.Of<StatsModifierConfigComponent>(world, -1);
            var stops = RawPool.Of<StopGaugeModifierConfigComponent>(world, -1);
            var parts = new List<string>();
            var states = MoraleProbe.StatesOf(world, entity);
            foreach (int s in states)
            {
                if (parts.Count >= MaxStates) { parts.Add($"and {states.Count - MaxStates} more"); break; }
                var sb = new StringBuilder(MoraleProbe.DescribeState(world, s).Name);
                try
                {
                    IntPtr c = stats.Item(s);
                    IntPtr config = c == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(c + _statsConfig);
                    if (config != IntPtr.Zero)
                    {
                        var smc = new StatsModifierConfig(config);
                        var mods = smc.m_statModifiers;
                        sb.Append(" {").Append(smc.name);
                        if (mods != null)
                            for (int i = 0; i < mods.Length; i++)
                                sb.Append(i == 0 ? ": " : ", ").Append(mods[i].StatType).Append(' ').Append(mods[i].Modifier.Operator).Append(' ').Append(F(mods[i].Modifier.Value));
                        sb.Append('}');
                    }
                    c = stops.Item(s);
                    config = c == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)(c + _stopConfig);
                    if (config != IntPtr.Zero)
                    {
                        var gauges = new StopGaugeModifierConfig(config).m_gaugesToStop;
                        if (gauges != null && gauges.Length > 0)
                            sb.Append(" {stops ").Append(string.Join(", ", gauges.Where(x => x != null).Select(x => $"{x.GaugeToStop} {x.FloatWithModifierToStop}"))).Append('}');
                    }
                }
                catch (Exception e) { sb.Append(" {unreadable: ").Append(e.Message).Append('}'); }
                parts.Add(sb.ToString());
            }
            return parts.Count == 0 ? "none" : string.Join("; ", parts);
        }
    }
}
