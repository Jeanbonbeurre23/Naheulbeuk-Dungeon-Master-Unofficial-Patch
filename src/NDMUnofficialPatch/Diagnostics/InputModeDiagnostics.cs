using Aube;
using HarmonyLib;

namespace NDMUnofficialPatch.Diagnostics
{
    // Phase 1 diagnostics for G1: the game switches between mouse, keyboard and gamepad modes
    // (Aube.InputModeDetector). A device read as a gamepad could leave the tutorial waiting for
    // gamepad navigation while the mouse is ignored. This logs every switch to or from GAMEPAD
    // with the name of the device that caused it, and counts mouse/keyboard switches.
    internal static class InputModeDiagnostics
    {
        internal static string CurrentMode = "unknown";
        internal static int MouseKeyboardSwitches;
    }

    [HarmonyPatch(typeof(InputModeDetector), nameof(InputModeDetector.SetMode))]
    internal static class InputModeSetModePatch
    {
        private static bool Prepare() => Settings.DiagnosticsInput.Value;

        private static void Prefix(InputModeDetector __instance, out InputMode __state)
        {
            __state = __instance.m_inputMode;
        }

        private static void Postfix(InputModeDetector __instance, InputMode mode, InputMode __state)
        {
            string device = __instance.m_deviceName ?? "";
            string previous = InputModeDiagnostics.CurrentMode;
            InputModeDiagnostics.CurrentMode = $"{mode} ({device})";
            if (mode == __state && previous != "unknown") return;

            if (mode == InputMode.GAMEPAD || __state == InputMode.GAMEPAD || previous == "unknown")
                Plugin.Logger.LogInfo($"[Input] mode {__state} -> {mode}, device '{device}'");
            else
                InputModeDiagnostics.MouseKeyboardSwitches++;
        }
    }
}
