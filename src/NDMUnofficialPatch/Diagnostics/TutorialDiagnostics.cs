using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UI;
using UnityEngine;

namespace NDMUnofficialPatch.Diagnostics
{
    // Phase 1 diagnostics for problem G1 (interface freezes at scripted tutorial steps).
    // Logs every tutorial step shown, validated or (de)activated, every UI target the tutorial
    // fails to find, and writes the catalogue of all tutorial steps to a TSV file once per session.
    internal static class TutorialDiagnostics
    {
        private static bool _catalogWritten;
        private static readonly Dictionary<IntPtr, string> StepNames = new();
        private static readonly Dictionary<string, float> LastMissingTargetLog = new();

        internal static string OutputDir => Path.Combine(Paths.BepInExRootPath, "NDMUnofficialPatch");

        internal static void TryWriteCatalog(string trigger)
        {
            if (_catalogWritten) return;
            try
            {
                var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TutorialsConfig>());
                if (found == null || found.Length == 0)
                {
                    Plugin.Logger.LogInfo($"[Tutorial] catalogue not written yet: no TutorialsConfig loaded ({trigger})");
                    return;
                }

                var config = found[0].Cast<TutorialsConfig>();
                var steps = config.AllTutorialStepConfigs;
                var sb = new StringBuilder();
                sb.AppendLine("index\tstep\tvalidation\tgamepadOnly\tblackMask\twaitForPage\tpageToWait\tforceReturnToHud\thighlight");
                for (int i = 0; i < steps.Length; i++)
                {
                    var entityConfig = steps[i];
                    if (entityConfig == null) continue;
                    var step = entityConfig.m_tutorialStepConfig;
                    sb.Append(i).Append('\t').Append(entityConfig.name);
                    if (step != null)
                    {
                        StepNames[step.Pointer] = entityConfig.name;
                        sb.Append('\t').Append(step.m_validationType)
                          .Append('\t').Append(step.m_isGamepadOnly)
                          .Append('\t').Append(step.m_enableBlackMask)
                          .Append('\t').Append(step.WaitForPageVisible)
                          .Append('\t').Append(step.PageToWait)
                          .Append('\t').Append(step.ForceReturnToHud)
                          .Append('\t').Append(DescribeIdentifiers(step.IdentifiersToHighlight));
                    }
                    sb.AppendLine();
                }

                Directory.CreateDirectory(OutputDir);
                var path = Path.Combine(OutputDir, "tutorial_steps.tsv");
                File.WriteAllText(path, sb.ToString());
                _catalogWritten = true;
                Plugin.Logger.LogInfo($"[Tutorial] catalogue of {steps.Length} steps written to {path} ({trigger})");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Tutorial] catalogue failed: {e}");
            }
        }

        internal static string DescribeIdentifiers(Il2CppReferenceArray<IdentifierDescriptor> ids)
        {
            if (ids == null || ids.Length == 0) return "";
            var parts = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                var d = ids[i];
                parts.Add(d == null ? "null" : $"{d.name}[{d.InstanceType}/{d.Identifier}]");
            }
            return string.Join(", ", parts);
        }

        internal static string StepName(TutorialStepConfig step)
        {
            if (step == null) return "null";
            return StepNames.TryGetValue(step.Pointer, out var n) ? n : step.name;
        }

        // The game looks UI targets up often; log each missing target at most once every 10 seconds.
        internal static void LogMissingTarget(IdentifierDescriptor descriptor, int result)
        {
            string key = descriptor == null ? "null" : descriptor.name;
            float now = Time.realtimeSinceStartup;
            if (LastMissingTargetLog.TryGetValue(key, out var last) && now - last < 10f) return;
            LastMissingTargetLog[key] = now;
            string detail = descriptor == null ? "" : $" [{descriptor.InstanceType}/{descriptor.Identifier}]";
            Plugin.Logger.LogWarning($"[Tutorial] UI target not found: '{key}'{detail} -> {result}");
        }
    }

    [HarmonyPatch(typeof(TutorialSystem), nameof(TutorialSystem.Init))]
    internal static class TutorialSystemInitPatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        private static void Postfix() => TutorialDiagnostics.TryWriteCatalog("TutorialSystem.Init");
    }

    [HarmonyPatch(typeof(TutorialPage), nameof(TutorialPage.InitAndPush))]
    internal static class TutorialPageInitAndPushPatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        private static void Postfix(TutorialPage __instance, int tutorialStepEntity)
        {
            TutorialDiagnostics.TryWriteCatalog("TutorialPage.InitAndPush");
            try
            {
                var step = __instance.TutorialStepConfig;
                Plugin.Logger.LogInfo(
                    $"[Tutorial] step shown: entity {tutorialStepEntity}, '{TutorialDiagnostics.StepName(step)}', " +
                    $"validation {__instance.ValidationType}, gamepadOnly {(step != null && step.m_isGamepadOnly)}, " +
                    $"blackMask {__instance.EnableBlackMask}, input mode {InputModeDiagnostics.CurrentMode}, " +
                    $"highlight: {TutorialDiagnostics.DescribeIdentifiers(step?.IdentifiersToHighlight)}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[Tutorial] step log failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(TutorialUtility), nameof(TutorialUtility.ValidateTutorialStep))]
    internal static class TutorialUtilityValidatePatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        private static void Prefix(int tutorialStepEntity) =>
            Plugin.Logger.LogInfo($"[Tutorial] step validated: entity {tutorialStepEntity}");
    }

    [HarmonyPatch(typeof(TutorialUtility), nameof(TutorialUtility.ActivateTutorialStep))]
    internal static class TutorialUtilityActivatePatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        private static void Prefix(int tutorialStepEntity, bool active) =>
            Plugin.Logger.LogInfo($"[Tutorial] step {(active ? "activated" : "deactivated")}: entity {tutorialStepEntity}");
    }

    [HarmonyPatch(typeof(TutorialPage), nameof(TutorialPage.EnableButtonInteractability))]
    internal static class TutorialPageEnableButtonPatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        // The same always-clickable HUD controls are re-enabled at every step, so each control is logged once per session.
        private static readonly System.Collections.Generic.HashSet<int> Seen = new();

        private static void Prefix(int identifierEntity)
        {
            if (Seen.Add(identifierEntity))
                Plugin.Logger.LogInfo($"[Tutorial] highlighted control made clickable: identifier entity {identifierEntity} (logged once)");
        }
    }

    [HarmonyPatch(typeof(IdentifierUtility), nameof(IdentifierUtility.GetIdentifierEntity))]
    internal static class IdentifierLookupPatch
    {
        private static bool Prepare() => Settings.DiagnosticsTutorial.Value;

        private static void Postfix(IdentifierDescriptor descriptor, int __result)
        {
            // A lookup with no descriptor at all is the game asking for nothing, not a missing control.
            if (__result < 0 && descriptor != null) TutorialDiagnostics.LogMissingTarget(descriptor, __result);
        }
    }
}
