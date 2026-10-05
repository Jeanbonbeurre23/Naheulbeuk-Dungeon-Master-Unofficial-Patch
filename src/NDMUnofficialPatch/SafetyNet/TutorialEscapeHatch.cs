using System;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace NDMUnofficialPatch.SafetyNet
{
    // Experimental escape hatch for G1, checked every frame while a tutorial page is on screen.
    //   Ctrl+Shift+F9  : give the player back all inputs and hide the black mask; the step stays active.
    //   Ctrl+Shift+F10 : validate the current tutorial step as if the player had done it.
    // Keyboard.current reads the keyboard directly, so it works while the game ignores its own inputs.
    [HarmonyPatch(typeof(TutorialPage), nameof(TutorialPage.LateUpdate))]
    internal static class TutorialEscapeHatch
    {
        private static bool Prepare() => Settings.TutorialEscapeHatch.Value;

        private static void Postfix(TutorialPage __instance)
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (!(kb.ctrlKey.isPressed && kb.shiftKey.isPressed)) return;

            if (kb.f9Key.wasPressedThisFrame) Release(__instance);
            else if (kb.f10Key.wasPressedThisFrame) Validate(__instance);
        }

        private static void Release(TutorialPage page)
        {
            Plugin.Logger.LogWarning($"[EscapeHatch] Ctrl+Shift+F9: releasing the tutorial input lock (step entity {page.m_tutorialStepEntity})");
            try { page.EnableAllInputs(true, -1); }
            catch (Exception e) { Plugin.Logger.LogError($"[EscapeHatch] EnableAllInputs failed: {e.Message}"); }
            try
            {
                var mask = page.m_dynamicMask;
                if (mask != null) mask.gameObject.SetActive(false);
            }
            catch (Exception e) { Plugin.Logger.LogError($"[EscapeHatch] hiding the mask failed: {e.Message}"); }
        }

        private static void Validate(TutorialPage page)
        {
            Plugin.Logger.LogWarning($"[EscapeHatch] Ctrl+Shift+F10: validating tutorial step entity {page.m_tutorialStepEntity}");
            try { page.ValidateTutorialStep(); }
            catch (Exception e) { Plugin.Logger.LogError($"[EscapeHatch] ValidateTutorialStep failed: {e.Message}"); }
        }
    }
}
