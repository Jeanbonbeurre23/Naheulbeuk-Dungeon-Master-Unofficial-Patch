using System.Collections.Generic;
using Aube;
using HarmonyLib;
using UnityEngine;

namespace NDMUnofficialPatch.Diagnostics
{
    // Phase 1 diagnostics for G2 (tavern barmen), G5 (attacks that never end) and G6 (saves).
    // Each hook only writes to the BepInEx log; none changes the game's behaviour.

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.HireBarmansForCounter))]
    internal static class HireBarmansPatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix(int counterEntity) =>
            Plugin.Logger.LogInfo($"[Tavern] barmen hired for counter entity {counterEntity}");
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.SendAttachedBarmansOut))]
    internal static class SendBarmansOutPatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix(int counterEntity) =>
            Plugin.Logger.LogInfo($"[Tavern] barmen sent out from counter entity {counterEntity}");
    }

    internal static class AlertTimeline
    {
        internal static readonly Dictionary<int, float> StartedAt = new();
        internal static float FirstStart = -1f;
    }

    // Called when combat starts for an enemy group (from GroupEnemyUtility.StartAlertForGroup, AlertUpdateSystem.UpdateAlert
    // and others); logged once per group. Plugin 0.0.3 hooked AlertUpdateSystem.UpdateStartAlert instead, whose second
    // parameter is `ref GroupEnemyComponent`. Il2CppInterop declares that struct as a class, and its hook trampoline read
    // the first 8 bytes of the struct as an object pointer, which crashed the game at the first attack (27 September 2026).
    [HarmonyPatch(typeof(CombatUtility), nameof(CombatUtility.AddStartCombatEvent))]
    internal static class AlertStartPatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix(int enemyGroup)
        {
            if (AlertTimeline.StartedAt.ContainsKey(enemyGroup)) return;
            float now = Time.realtimeSinceStartup;
            AlertTimeline.StartedAt[enemyGroup] = now;
            if (AlertTimeline.FirstStart < 0f) AlertTimeline.FirstStart = now;
            Plugin.Logger.LogInfo($"[Attack] combat started for enemy group entity {enemyGroup}");
        }
    }

    [HarmonyPatch(typeof(AlertUpdateSystem), nameof(AlertUpdateSystem.EndGlobalAlert))]
    internal static class AlertEndPatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix()
        {
            float seconds = AlertTimeline.FirstStart < 0f ? -1f : Time.realtimeSinceStartup - AlertTimeline.FirstStart;
            Plugin.Logger.LogInfo($"[Attack] global alert ended after {seconds:0} s of real time, {AlertTimeline.StartedAt.Count} enemy group(s)");
            AlertTimeline.StartedAt.Clear();
            AlertTimeline.FirstStart = -1f;
        }
    }

    [HarmonyPatch(typeof(SaveManagerStandalone), nameof(SaveManagerStandalone.Save))]
    internal static class SaveWritePatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix(SaveManagerStandalone __instance, SaveManager.SaveRequest request)
        {
            string name = request?.Filename ?? "?";
            string path = "?";
            try { path = __instance.CreatePath(name); } catch { }
            Plugin.Logger.LogInfo($"[Save] writing '{name}' to {path}");
        }
    }

    [HarmonyPatch(typeof(SaveManagerStandalone), nameof(SaveManagerStandalone.Load))]
    internal static class SaveLoadPatch
    {
        private static bool Prepare() => Settings.DiagnosticsEvents.Value;

        private static void Prefix(SaveManager.LoadRequest request) =>
            Plugin.Logger.LogInfo($"[Save] loading '{request?.Filename ?? "?"}'");
    }
}
