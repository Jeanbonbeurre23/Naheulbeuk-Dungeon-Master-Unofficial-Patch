using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Aube;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Diagnostics
{
    // Writes to the log why minions lose morale, read from the game's own state.
    //
    // Game 1.8, from the method bodies. A minion resigns through MinionUtility.MinionResign, which adds the
    // ResignTagComponent that AIComputeResignScoreJob turns into the resign action. Apart from guards whose locker is
    // destroyed (EntityUtility.DestroyProp passes fromGuardLockerDestroyed), the call comes from
    // StatesUtility.OnBehaviourStateApplied, when a state whose behaviour is "resign" is applied to a minion that
    // carries no CannotResignTagComponent and is not a lieutenant. The states applied to a minion are entities listed in
    // its EntityStateHandlerComponent.CurrentStates. A state that moves morale carries a StateAffectConfigComponent
    // (StateAffectConfig: positive or negative, and the affect's name); a visible state carries a
    // StateUIConfigComponent (its name). The minion's gauges are MinionMoraleComponent, MinionHungerComponent,
    // MinionEnergyComponent, MinionFunComponent, MinionHygieneComponent and MinionToiletsComponent: each holds a Gauge
    // and its steps (GaugeStep: name, percentage range, states applied). Which states each step applies, and how much
    // each state moves morale, comes from the game's data and not from its code, so the probe reads them at run time.
    //
    // 20 seconds after a world appears, and every 5 minutes of real time after that, the probe writes one line per
    // player minion (gauges with their current step, negative morale states, the state the game shows as the last
    // negative one), a count of each negative state over all minions, and a count of the room searches the minions made
    // since the last report, by room type: how many found a prop, how many found none although a room was acceptable,
    // how many found no acceptable room. Once per world it writes the steps of each gauge for one minion per origin.
    // A minion who resigns gets the same line, written just before he leaves (ResignationLog.cs).
    internal static unsafe class MoraleProbe
    {
        private const float FirstDelay = 20f;
        private const float Period = 300f;

        private static IntPtr _world;
        private static float _next;
        private static bool _stepsDumped;
        private static bool _layoutChecked;
        private static int _gaugeStepIndex, _gaugePercentage, _stepName, _statesList;
        private static bool _errorLogged;

        // Room searches since the last report, by room type: searches, prop found, none found with an acceptable room,
        // no acceptable room.
        private static readonly Dictionary<RoomType, int[]> Searches = new();

        private static readonly (string Name, Func<EcsWorld, RawPool> Pool)[] Gauges =
        {
            ("morale", w => RawPool.Of<MinionMoraleComponent>(w, -1)),
            ("hunger", w => RawPool.Of<MinionHungerComponent>(w, -1)),
            ("energy", w => RawPool.Of<MinionEnergyComponent>(w, -1)),
            ("fun", w => RawPool.Of<MinionFunComponent>(w, -1)),
            ("hygiene", w => RawPool.Of<MinionHygieneComponent>(w, -1)),
            ("toilets", w => RawPool.Of<MinionToiletsComponent>(w, -1)),
        };

        private static void CheckLayout()
        {
            if (_layoutChecked) return;
            // Every minion gauge component starts with its Gauge (the game's GetGauge returns the component's address).
            Il2CppRaw.ExpectValueFieldOffset<MinionMoraleComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionHungerComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionEnergyComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionFunComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionHygieneComponent>("Gauge", 0);
            Il2CppRaw.ExpectValueFieldOffset<MinionToiletsComponent>("Gauge", 0);
            _gaugeStepIndex = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentStepIndex");
            _gaugePercentage = Il2CppRaw.ValueFieldOffset<Gauge>("CurrentPercentage");
            _stepName = Il2CppRaw.ValueFieldOffset<GaugeStep>("Name");
            _statesList = Il2CppRaw.ValueFieldOffset<EntityStateHandlerComponent>("CurrentStates");
            _layoutChecked = true;
        }

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Morale] {what} failed, further errors are not logged: {e.Message}");
        }

        // ---- Every frame ----

        internal static void Update()
        {
            if (!GameContext.Ready) return;
            IntPtr world = GameContext.WorldPointer;
            if (world != _world)
            {
                _world = world;
                _next = Time.unscaledTime + FirstDelay;
                _stepsDumped = false;
                Searches.Clear();
                return;
            }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Period;
            try { Report(); }
            catch (Exception e) { ReportError("writing the morale report", e); }
        }

        // ---- Reading ----

        private struct GaugeValue
        {
            public bool Present;
            public float Percentage;
            public string Step;
        }

        private static GaugeValue ReadGauge(RawPool pool, int entity, int stepsOffset)
        {
            IntPtr c = pool.Item(entity);
            if (c == IntPtr.Zero) return default;
            var v = new GaugeValue { Present = true, Percentage = *(float*)(c + _gaugePercentage) };
            int index = *(int*)(c + _gaugeStepIndex);
            IntPtr steps = *(IntPtr*)(c + stepsOffset);
            if (steps != IntPtr.Zero && index >= 0 && index < Il2CppRaw.ArrayLength(steps))
            {
                IntPtr step = Il2CppRaw.ArrayData(steps) + index * Il2CppRaw.ArrayElementSize(steps);
                v.Step = String(*(IntPtr*)(step + _stepName));
            }
            return v;
        }

        private static string String(IntPtr s) => s == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(s);

        private static string Text(LocalizationKey key)
        {
            if (key == null) return null;
            try
            {
                string t = key.GetTranslation();
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            catch { }
            try { return key.m_key; } catch { return null; }
        }

        // The states applied to an entity (EntityStateHandlerComponent.CurrentStates, a NativeList<int>: a pointer to
        // the list, whose first fields are the data pointer and the length).
        internal static List<int> StatesOf(EcsWorld world, int entity)
        {
            var result = new List<int>();
            IntPtr handler = RawPool.Of<EntityStateHandlerComponent>(world, -1).Item(entity);
            if (handler == IntPtr.Zero) return result;
            IntPtr list = *(IntPtr*)(handler + _statesList);
            if (list == IntPtr.Zero) return result;
            IntPtr data = *(IntPtr*)list;
            int length = *(int*)(list + IntPtr.Size);
            if (data == IntPtr.Zero || length < 0 || length > 512) return result;
            for (int i = 0; i < length; i++) result.Add(*(int*)(data + i * 4));
            return result;
        }

        internal struct StateInfo
        {
            public string Name;
            public bool Affects;
            public bool Negative;
        }

        internal static StateInfo DescribeState(EcsWorld world, int state)
        {
            var info = new StateInfo();
            IntPtr affect = RawPool.Of<StateAffectConfigComponent>(world, -1).Item(state);
            if (affect != IntPtr.Zero) affect = *(IntPtr*)(affect + Il2CppRaw.ValueFieldOffset<StateAffectConfigComponent>("m_config"));
            if (affect != IntPtr.Zero)
            {
                var config = new StateAffectConfig(affect);
                info.Affects = true;
                info.Negative = config.m_moraleValue == StateAffectConfig.MoraleValueEnum.NEGATIVE;
                info.Name = Text(config.m_affectName);
            }
            IntPtr ui = RawPool.Of<StateUIConfigComponent>(world, -1).Item(state);
            if (ui != IntPtr.Zero) ui = *(IntPtr*)(ui + Il2CppRaw.ValueFieldOffset<StateUIConfigComponent>("m_config"));
            if (ui != IntPtr.Zero)
            {
                string name = Text(new StateUIConfig(ui).m_stateNameLocKey);
                if (!string.IsNullOrWhiteSpace(name)) info.Name = info.Name == null || info.Name == name ? name : $"{name} ({info.Name})";
            }
            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = $"state {state}";
            return info;
        }

        // One line about a minion: gauges, negative morale states, and the last negative one the game shows.
        internal static string Describe(int entity, out List<string> negatives)
        {
            negatives = new List<string>();
            CheckLayout();
            if (!GameContext.TryWorld(out var world, out int size) || !world.IsEntityAlive(entity, size)) return $"entity {entity} (not alive)";
            var minions = GameContext.Minions;
            var sb = new StringBuilder();
            string name;
            try { name = minions.GetMinionFullName(entity); } catch { name = null; }
            sb.Append(string.IsNullOrWhiteSpace(name) ? $"entity {entity}" : name);

            IntPtr origin = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent)).Item(entity);
            if (origin != IntPtr.Zero) sb.Append(", ").Append(((OriginComponent*)origin)->Origin);
            IntPtr job = RawPool.Of<JobPracticedComponent>(world, sizeof(JobPracticedComponent)).Item(entity);
            if (job != IntPtr.Zero) sb.Append(", ").Append(((JobPracticedComponent*)job)->JobPracticed);
            IntPtr grade = RawPool.Of<GradeComponent>(world, -1).Item(entity);
            if (grade != IntPtr.Zero) sb.Append(", grade ").Append(*(int*)(grade + Il2CppRaw.ValueFieldOffset<GradeComponent>("CurrentGrade")));

            sb.Append(" |");
            foreach (var g in Gauges)
            {
                var pool = g.Pool(world);
                var v = ReadGauge(pool, entity, StepsOffset(g.Name));
                if (!v.Present) continue;
                sb.Append(' ').Append(g.Name).Append(' ').Append(v.Percentage.ToString("0.##", CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(v.Step)) sb.Append(" (").Append(v.Step).Append(')');
            }

            foreach (int s in StatesOf(world, entity))
            {
                if (!world.IsEntityAlive(s, size)) continue;
                var st = DescribeState(world, s);
                if (st.Affects && st.Negative) negatives.Add(st.Name);
            }
            sb.Append(" | negative: ").Append(negatives.Count == 0 ? "none" : string.Join(", ", negatives));

            try
            {
                var states = new StatesUtility(Il2CppRaw.ReadObject(minions.Pointer, "m_statesUtility", "StatesUtility"));
                int last = states.GetLastNegativeMoraleAffectApplied(entity);
                if (last >= 0 && world.IsEntityAlive(last, size)) sb.Append(" | last negative shown: ").Append(DescribeState(world, last).Name);
            }
            catch { }
            return sb.ToString();
        }

        private static readonly Dictionary<string, int> StepsOffsets = new();

        private static int StepsOffset(string gauge)
        {
            if (StepsOffsets.TryGetValue(gauge, out int o)) return o;
            o = gauge switch
            {
                "morale" => Il2CppRaw.ValueFieldOffset<MinionMoraleComponent>("Steps"),
                "hunger" => Il2CppRaw.ValueFieldOffset<MinionHungerComponent>("Steps"),
                "energy" => Il2CppRaw.ValueFieldOffset<MinionEnergyComponent>("Steps"),
                "fun" => Il2CppRaw.ValueFieldOffset<MinionFunComponent>("Steps"),
                "hygiene" => Il2CppRaw.ValueFieldOffset<MinionHygieneComponent>("Steps"),
                _ => Il2CppRaw.ValueFieldOffset<MinionToiletsComponent>("Steps"),
            };
            StepsOffsets[gauge] = o;
            return o;
        }

        // ---- Report ----

        private static void Report()
        {
            CheckLayout();
            if (!GameContext.TryWorld(out var world, out _)) return;
            var minions = GameContext.ListMinions();
            Plugin.Logger.LogInfo($"[Morale] report on {minions.Count} minion(s)");
            var counts = new Dictionary<string, int>();
            foreach (var m in minions)
            {
                Plugin.Logger.LogInfo("[Morale] " + Describe(m.Entity, out var negatives));
                foreach (string n in negatives.Distinct()) counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
            }
            if (counts.Count > 0)
                Plugin.Logger.LogInfo("[Morale] negative states over all minions: " + string.Join("; ", counts.OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}")));
            if (Searches.Count > 0)
            {
                Plugin.Logger.LogInfo("[Morale] room searches since the last report (searches: prop found / every prop taken / no acceptable room): " +
                    string.Join("; ", Searches.OrderBy(p => p.Key).Select(p => $"{Names.Room(p.Key)} {p.Value[0]}: {p.Value[1]} / {p.Value[2]} / {p.Value[3]}")));
                Searches.Clear();
            }
            if (!_stepsDumped)
            {
                _stepsDumped = true;
                DumpSteps(world, minions);
            }
        }

        // The steps of each gauge, for the first minion of each origin: name, percentage range, states applied.
        private static void DumpSteps(EcsWorld world, List<MinionInfo> minions)
        {
            var seen = new HashSet<OriginType>();
            var origins = RawPool.Of<OriginComponent>(world, sizeof(OriginComponent));
            foreach (var m in minions)
            {
                IntPtr o = origins.Item(m.Entity);
                if (o == IntPtr.Zero || !seen.Add(((OriginComponent*)o)->Origin)) continue;
                foreach (var g in Gauges)
                {
                    IntPtr c = g.Pool(world).Item(m.Entity);
                    if (c == IntPtr.Zero) continue;
                    IntPtr steps = *(IntPtr*)(c + StepsOffset(g.Name));
                    if (steps == IntPtr.Zero) continue;
                    var sb = new StringBuilder($"[Morale] steps of {g.Name} for {((OriginComponent*)o)->Origin}:");
                    long n = Il2CppRaw.ArrayLength(steps);
                    for (int i = 0; i < n; i++)
                    {
                        var step = new GaugeStep(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<GaugeStep>.NativeClassPtr,
                            Il2CppRaw.ArrayData(steps) + i * Il2CppRaw.ArrayElementSize(steps)));
                        sb.Append($" [{i} '{step.Name}' {step.PercentageMin.ToString("0.##", CultureInfo.InvariantCulture)}-{step.PercentageMax.ToString("0.##", CultureInfo.InvariantCulture)}");
                        var states = step.StatesToApply;
                        if (states != null && states.Length > 0)
                            sb.Append(" -> ").Append(string.Join(", ", states.Where(s => s != null).Select(s => s.name)));
                        sb.Append(']');
                    }
                    Plugin.Logger.LogInfo(sb.ToString());
                }
            }
        }

        // ---- Room searches ----

        internal static void CountSearch(AFindRoomTask task, RoomType type)
        {
            try
            {
                var io = task.TryCast<FindIoInRoomTask>();
                if (io == null) return;
                int minion = task.m_entity;
                if (!GameContext.TryWorld(out var world, out _) || !RawPool.Of<MinionTag>(world, -1).Has(minion)) return;
                if (!Searches.TryGetValue(type, out var c)) Searches[type] = c = new int[4];
                c[0]++;
                if (io.m_foundPropEntity >= 0) c[1]++;
                else if (task.m_availableRoomList != null && task.m_availableRoomList.Count > 0) c[2]++;
                else c[3]++;
            }
            catch (Exception e) { ReportError("counting a room search", e); }
        }
    }

    [HarmonyPatch(typeof(AFindRoomTask), nameof(AFindRoomTask.FindValidRooms))]
    internal static class RoomSearchCountPatch
    {
        private static bool Prepare() => Settings.DiagnosticsMorale.Value;
        private static void Postfix(AFindRoomTask __instance, RoomType roomToSearchFor) => MoraleProbe.CountSearch(__instance, roomToSearchFor);
    }
}
