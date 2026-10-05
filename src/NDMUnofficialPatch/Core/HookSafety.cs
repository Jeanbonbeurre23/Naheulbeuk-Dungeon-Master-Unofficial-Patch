using System;
using System.Reflection;
using HarmonyLib;

namespace NDMUnofficialPatch
{
    // Il2CppInterop declares a game struct that contains references or generic structs as a class deriving from
    // Il2CppSystem.ValueType. When a hooked method takes such a struct, the hook's native-to-managed trampoline
    // treats the struct's data as an object pointer. Plugin 0.0.3 crashed the game this way at the first attack
    // (AlertUpdateSystem.UpdateStartAlert, `ref GroupEnemyComponent`). Every patch target is therefore checked
    // before patching, and a target with such a parameter is refused with a log line.
    internal static class HookSafety
    {
        internal static string FindUnsafeParameter(Type patchClass)
        {
            var info = HarmonyMethodExtensions.GetMergedFromType(patchClass);
            if (info?.declaringType == null || info.methodName == null) return null;
            MethodBase target = info.argumentTypes != null
                ? AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes)
                : AccessTools.Method(info.declaringType, info.methodName);
            if (target == null) return null; // Harmony reports a missing target itself

            foreach (var p in target.GetParameters())
            {
                Type t = p.ParameterType;
                bool byRef = t.IsByRef;
                if (byRef) t = t.GetElementType();
                if (t != null && !t.IsValueType && typeof(Il2CppSystem.ValueType).IsAssignableFrom(t))
                    return $"{target.DeclaringType?.Name}.{target.Name} takes {(byRef ? "ref " : "")}{t.Name} '{p.Name}', a game struct that hook trampolines do not convert correctly";
            }
            return null;
        }
    }
}
