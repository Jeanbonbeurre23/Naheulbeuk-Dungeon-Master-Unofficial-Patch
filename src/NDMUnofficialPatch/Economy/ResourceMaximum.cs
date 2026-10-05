using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Economy
{
    // A higher maximum stock for the five workshop resources, with their gauge steps kept at the same stock.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 2 October 2026. Each workshop resource (ARMAMENT,
    // MAGIC, TOOLS, INTEL, CORPSES) has a gauge on the player entity, in a component that starts with its ResourceGauge
    // and holds the gauge's steps (Steps, GaugeStep.PercentageMin and PercentageMax, percentages of the gauge's
    // maximum). The maximum, Gauge.MaxValue, is 1000 for every one of them in the configs (m_gaugeData.m_maxValue).
    // ResourceUtility.RecomputeResourceGaugeLimit sets the stock limit to the storage capacity, lowered to that maximum
    // (see StorageCapacity.cs). The steps give the bonuses: the armament grade (6 steps, grade 6 from 83 %), the raid
    // reach of intel (3 steps), the undead damage of corpses (6 steps); magic and tools have none.
    //
    // When a world appears, the patch sets each gauge's maximum to Economy.ResourceMaximum, rescales its steps so that
    // each one starts and ends at the same stock as in the game (a percentage times the game's maximum over the new
    // one), the top step still ending at 100 %, and runs the game's recompute of each limit.
    //
    // The gauge holds its own copy of the steps, which GaugeConfig.SetupSteps sorts by PercentageMin, lowest first
    // (its comparison, <SetupSteps>b__2_0, subtracts the two minimums), where the configurations list them highest
    // first. GaugeUtility.HasChangedStep relies on that order: above the current step it looks at the next indices,
    // below it at the previous ones, and logs "Could not find any steps when changing step for gauge" when the search
    // finds nothing. Each step of the gauge is therefore matched to its configuration step by name (GaugeStep.Name),
    // or by rank when a name is missing. Plugin 0.19.0 to 0.20.0 wrote them by position, which reversed the ranges:
    // the weapons step of grade 1 held the stock of grade 6 and the reverse, and the game's search stopped working. The game's maximum and
    // steps are read from the resource configurations the first time, before the patch changes anything, and every
    // later world is set from those values, so applying it again changes nothing. The gauges are saved with the game,
    // so with the setting at 0, or below the game's maximum, the game's maximum and steps are written back when a world
    // appears. Food has no maximum in the game, only the capacity of its storage furniture, and is left alone.
    internal static unsafe class ResourceMaximum
    {
        private static readonly Type[] ConfigTypes =
        {
            typeof(ArmamentResourceConfig), typeof(MagicResourceConfig), typeof(ToolsResourceConfig), typeof(IntelResourceConfig), typeof(CorpsesResourceConfig),
        };

        private sealed class Original
        {
            public float Max;
            public float[] Min, Top;
            public string[] Name;
            public int[] Ascending; // indices in the configuration, in the order the gauge holds its steps
        }

        private static readonly Original[] Originals = new Original[5];
        private static IntPtr _appliedWorld;
        private static float _retryAt;
        private static bool _errorLogged;
        private static int _stepMin = -1, _stepMax, _stepName, _maxValue, _base, _forced, _dirty;
        private static float _invalidForce;

        private static float Target => Math.Max(0, Settings.ResourceMaximum.Value);

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Resources] {what} failed, further errors are not logged: {e.Message}");
        }

        private static void CheckLayout()
        {
            if (_stepMin >= 0) return;
            Il2CppRaw.ExpectValueFieldOffset<ResourceGauge>("Gauge", 0);
            _maxValue = Il2CppRaw.ValueFieldOffset<Gauge>("MaxValue");
            _base = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_baseValue");
            _forced = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_forcedValue");
            _dirty = Il2CppRaw.ValueFieldOffset<FloatWithModifiers>("m_isDirty");
            _invalidForce = FloatWithModifiers.INVALID_FORCE_VALUE;
            _stepMax = Il2CppRaw.ValueFieldOffset<GaugeStep>("PercentageMax");
            _stepName = Il2CppRaw.ValueFieldOffset<GaugeStep>("Name");
            _stepMin = Il2CppRaw.ValueFieldOffset<GaugeStep>("PercentageMin");
        }

        private static int StepsOffset(int type) => type switch
        {
            0 => Il2CppRaw.ValueFieldOffset<ArmamentResourceComponent>("Steps"),
            1 => Il2CppRaw.ValueFieldOffset<MagicResourceComponent>("Steps"),
            2 => Il2CppRaw.ValueFieldOffset<ToolsResourceComponent>("Steps"),
            3 => Il2CppRaw.ValueFieldOffset<IntelResourceComponent>("Steps"),
            _ => Il2CppRaw.ValueFieldOffset<CorpsesResourceComponent>("Steps"),
        };

        private static string StepName(IntPtr step)
        {
            IntPtr p = *(IntPtr*)(step + _stepName);
            return p == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(p);
        }

        // The game's maximum and steps of a resource type, from its configuration, read once per session.
        private static Original OriginalOf(int type)
        {
            if (Originals[type] != null) return Originals[type];
            var found = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(ConfigTypes[type]));
            if (found == null || found.Length == 0) return null;
            IntPtr data = Il2CppRaw.ReadPointer(found[0].Pointer, "m_gaugeData");
            if (data == IntPtr.Zero) return null;
            var o = new Original { Max = *(float*)(data + Il2CppRaw.FieldOffset(data, "m_maxValue")) };
            IntPtr steps = *(IntPtr*)(data + Il2CppRaw.FieldOffset(data, "m_steps"));
            int n = (int)Il2CppRaw.ArrayLength(steps);
            o.Min = new float[n];
            o.Top = new float[n];
            o.Name = new string[n];
            if (n > 0)
            {
                int size = Il2CppRaw.ArrayElementSize(steps);
                for (int i = 0; i < n; i++)
                {
                    IntPtr s = Il2CppRaw.ArrayData(steps) + i * size;
                    o.Min[i] = *(float*)(s + _stepMin);
                    o.Top[i] = *(float*)(s + _stepMax);
                    o.Name[i] = StepName(s);
                }
            }
            // GaugeConfig.SetupSteps sorts the gauge's copy by PercentageMin, lowest first, where the configurations
            // list the highest first.
            o.Ascending = new int[n];
            for (int i = 0; i < n; i++) o.Ascending[i] = i;
            Array.Sort(o.Ascending, (a, b) => o.Min[a].CompareTo(o.Min[b]));
            Originals[type] = o;
            return o;
        }

        // Every frame, from ResourceBarBehaviour, before the storage capacity, whatever the setting.
        internal static void Update()
        {
            if (!GameContext.Ready) return;
            IntPtr world = GameContext.WorldPointer;
            if (world == _appliedWorld || Time.unscaledTime < _retryAt) return;
            try
            {
                CheckLayout();
                var utility = GameContext.ResourceUtility;
                if (Il2CppRaw.ReadPointer(utility.Pointer, "m_world") != world) return;
                _appliedWorld = world;
                for (int type = 0; type < StorageCapacity.GaugePools.Length; type++) Apply(utility, type);
            }
            catch (Exception e)
            {
                _retryAt = Time.unscaledTime + 5f;
                ReportError("raising the resource maximums", e);
            }
        }

        private static void Apply(ResourceUtility utility, int type)
        {
            var resource = (ResourceType)type;
            var o = OriginalOf(type);
            IntPtr component = StorageCapacity.GaugeOf(utility, type);
            if (o == null || component == IntPtr.Zero || o.Max <= 0f)
            {
                Plugin.Logger.LogWarning($"[Resources] {resource}: configuration or gauge not found; left as it is");
                return;
            }
            // Below the game's maximum, 0 included, the game's own maximum and steps are written, which gives a save
            // played with a higher maximum its normal gauge back. The values are saved with the game.
            bool raise = Target >= o.Max;
            float target = raise ? Target : o.Max;
            IntPtr max = component + _maxValue; // the ResourceGauge, and its Gauge, start the component
            if (*(float*)(max + _forced) != _invalidForce)
            {
                Plugin.Logger.LogWarning($"[Resources] {resource}: the gauge's maximum is forced by the game; left as it is");
                return;
            }
            float before = *(float*)(max + _base);
            bool changed = before != target;
            if (changed)
            {
                *(float*)(max + _base) = target;
                *(byte*)(max + _dirty) = 1;
            }

            IntPtr steps = *(IntPtr*)(component + StepsOffset(type));
            int n = steps == IntPtr.Zero ? 0 : (int)Il2CppRaw.ArrayLength(steps);
            string stepText = "no steps";
            if (n > 0 && n == o.Min.Length)
            {
                int size = Il2CppRaw.ArrayElementSize(steps);
                float ratio = o.Max / target;
                var parts = new string[n];
                int byOrder = 0;
                for (int i = 0; i < n; i++)
                {
                    IntPtr s = Il2CppRaw.ArrayData(steps) + i * size;
                    // The configuration step this one was copied from: by its name, or else by its rank.
                    string name = StepName(s);
                    int src = name == null ? -1 : Array.IndexOf(o.Name, name);
                    if (src < 0 || Array.LastIndexOf(o.Name, name) != src) { src = o.Ascending[i]; byOrder++; }
                    float min = o.Min[src] * ratio;
                    float top = o.Top[src] >= 100f ? 100f : o.Top[src] * ratio;
                    if (*(float*)(s + _stepMin) != min || *(float*)(s + _stepMax) != top) changed = true;
                    *(float*)(s + _stepMin) = min;
                    *(float*)(s + _stepMax) = top;
                    parts[i] = $"{o.Name[src] ?? "step " + i} {F(o.Min[src] * o.Max / 100f)}-{(o.Top[src] >= 100f ? F(target) : F(o.Top[src] * o.Max / 100f))}";
                }
                stepText = "steps by stock " + string.Join(", ", parts) + (byOrder > 0 ? $" ({byOrder} matched by rank, their names not found)" : "");
            }
            else if (n > 0) stepText = $"{n} steps where the configuration has {o.Min.Length}; steps left as they are";
            if (!changed)
            {
                if (raise) Plugin.Logger.LogInfo($"[Resources] {resource}: maximum already {F(target)} in this save; {stepText}");
                return;
            }
            utility.RecomputeResourceGaugeLimit(resource);
            Plugin.Logger.LogInfo(raise
                ? $"[Resources] {resource}: maximum {F(before)} -> {F(target)} (game's {F(o.Max)}); {stepText}"
                : $"[Resources] {resource}: ResourceMaximum is off or below the game's {F(o.Max)}; the save's maximum {F(before)} and steps set back to the game's");
        }
    }
}
