using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NDMUnofficialPatch.Fixes
{
    // Points the game's input wrapper back to the game's own event system.
    //
    // Game 1.8, from the method bodies. Aube.PlayerInputs keeps in m_eventSystem the first event system it is given
    // (PlayerInputs.get_EventSystem fills it once, from EventSystem.current or the first one found) and never asks
    // again. PlayerInputs.IsMouseOnUi asks that event system whether the mouse is over the interface, and the
    // construction page and the furniture popup (ConstructionPage.Update, ConstructionPropPopup.Update) use the
    // answer to decide whether a click belongs to the interface or to the builder; the input wrapper's m_isOnUi, which
    // ObservationController reads, comes from the same event system. With UnityExplorer installed, its library
    // UniverseLib creates an event system of its own at start-up, and the game keeps that one (the click guard's log of
    // 28 September names it: 'UniverseLibCanvas', while the current event system is 'UI'). That event system does not
    // handle the game's interface, so the answer is always "not over the interface": a click on the interface also
    // reaches the dungeon or the builder.
    //
    // Once a second of real time, when the event system kept by PlayerInputs differs from the current one, is gone, or
    // belongs to UniverseLib or UnityExplorer, and the current one belongs to neither, m_eventSystem is set to the
    // current one and the change is logged. Without UnityExplorer the two are the same and nothing is done.
    internal static class GameEventSystem
    {
        private static float _next;
        private static int _changes;
        private static bool _errorLogged;

        private static bool IsForeign(EventSystem system)
        {
            if (system == null) return false;
            for (Transform t = system.transform; t != null; t = t.parent)
            {
                string name = t.name ?? "";
                if (name.IndexOf("UniverseLib", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("UnityExplorer", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("ExplorerCore", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // Every frame, from FixesBehaviour.
        internal static void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            try
            {
                var inputs = UIManager.Instance?.Inputs;
                if (inputs == null) return;
                var current = EventSystem.current;
                if (current == null || IsForeign(current)) return;
                var kept = inputs.m_eventSystem;
                bool keptAlive = kept != null && !kept.WasCollected;
                if (keptAlive && kept.Pointer == current.Pointer) return;
                if (keptAlive && !IsForeign(kept)) return;
                inputs.m_eventSystem = current;
                if (_changes < 20)
                {
                    _changes++;
                    Plugin.Logger.LogInfo($"[EventSystem] the game's input wrapper kept event system '{(keptAlive ? kept.name : "none")}'; set to the game's own, '{current.name}'");
                }
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[EventSystem] checking the game's event system failed: {e.Message}");
            }
        }
    }
}
