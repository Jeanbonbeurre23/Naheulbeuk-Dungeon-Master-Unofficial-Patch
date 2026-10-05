using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NDMUnofficialPatch.Fixes
{
    // Problem G16, found in testing on 27 September 2026: a click on a button of the interface (the buttons at the
    // bottom left, the panel of a room or of a piece of furniture) also clicks the room behind it. The room is
    // selected, its panel replaces the one in use, and the camera moves to it; the Clean toggle of a room's panel
    // "jumps to another room" this way.
    //
    // In the game, a click in the dungeon is handled by ObservationController. With the mouse, OnValidatePressed
    // records every press, over the interface or not; on release, OnValidate raycasts into the dungeon and selects
    // what it hits unless PlayerInputs.m_isOnUi is set. That flag is written once per frame, at the end of
    // PlayerInputs.Update, from EventSystem.IsPointerOverGameObject on the event system PlayerInputs cached the
    // first time it needed one. It is therefore one frame old when the click is handled, it depends on the input
    // module's record of the pointer rather than on a raycast made at that moment, and it is only as good as the
    // cached event system.
    //
    // The guard asks the question again at the moment of the press and of the click, with a fresh raycast of every
    // raycaster of the interface at the mouse position. A click is kept from the dungeon when the press or the
    // release happened over the interface. With a gamepad the game's own rules apply unchanged.
    internal static class ClickThroughGuard
    {
        private static bool _pressOverInterface;
        private static string _pressObject;
        private static int _blockedLogged;
        private static int _disagreementsLogged;
        private static bool _errorLogged;

        internal static void OnPress()
        {
            _pressOverInterface = OverInterface(out _pressObject);
        }

        // True when the click must not reach the dungeon.
        internal static bool ShouldBlock()
        {
            if (IsGamepad()) return false;
            bool now = OverInterface(out string nowObject);
            bool block = now || _pressOverInterface;
            bool gameSaysUi = GameSaysOverInterface();

            if (block && _blockedLogged < 50)
            {
                _blockedLogged++;
                string where = now ? $"released over '{nowObject}'" : $"pressed over '{_pressObject}'";
                Plugin.Logger.LogInfo($"[ClickGuard] a click on the interface was kept from the dungeon ({where}; the game's own test: {(gameSaysUi ? "over the interface" : "not over the interface")}; {EventSystemsDescription()})");
            }
            else if (!block && gameSaysUi && _disagreementsLogged < 20)
            {
                _disagreementsLogged++;
                Plugin.Logger.LogInfo($"[ClickGuard] the game counts this click as over the interface and the raycast found no interface element; the game's rule applies ({EventSystemsDescription()})");
            }
            return block;
        }

        // A raycast of every raycaster of the interface at the mouse position. World raycasters are ignored, and
        // so is anything that is not a raycast target, exactly as the event system does for its own clicks.
        private static bool OverInterface(out string objectName)
        {
            objectName = null;
            try
            {
                var mouse = Mouse.current;
                var system = EventSystem.current;
                if (mouse == null || system == null) return false;
                var data = new PointerEventData(system) { position = mouse.position.ReadValue() };
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                system.RaycastAll(data, results);
                for (int i = 0; i < results.Count; i++)
                {
                    var hit = results[i];
                    var module = hit.module;
                    var go = hit.gameObject;
                    if (go == null || module == null) continue;
                    if (module.TryCast<GraphicRaycaster>() == null && go.GetComponent<RectTransform>() == null) continue;
                    objectName = go.name;
                    return true;
                }
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Plugin.Logger.LogWarning($"[ClickGuard] the interface raycast failed, the game's own test applies: {e.Message}");
                }
            }
            return false;
        }

        private static bool IsGamepad()
        {
            try { return UIManager.Instance != null && UIManager.Instance.IsGamepad; }
            catch { return false; }
        }

        private static bool GameSaysOverInterface()
        {
            try { return UIManager.Instance?.Inputs?.IsOnUi ?? false; }
            catch { return false; }
        }

        private static string EventSystemsDescription()
        {
            try
            {
                var cached = UIManager.Instance?.Inputs?.EventSystem;
                var current = EventSystem.current;
                string c = cached == null ? "none" : cached.name;
                string k = current == null ? "none" : current.name;
                return cached != null && current != null && cached.Pointer == current.Pointer
                    ? $"event system '{k}'"
                    : $"event system used by the game '{c}', current event system '{k}'";
            }
            catch { return "event systems unknown"; }
        }
    }

    [HarmonyPatch(typeof(ObservationController), nameof(ObservationController.OnValidatePressed))]
    internal static class ClickThroughGuardPressPatch
    {
        private static bool Prepare() => Settings.ClickThroughGuard.Value;
        private static void Prefix() => ClickThroughGuard.OnPress();
    }

    [HarmonyPatch(typeof(ObservationController), nameof(ObservationController.OnValidate))]
    internal static class ClickThroughGuardClickPatch
    {
        private static bool Prepare() => Settings.ClickThroughGuard.Value;
        private static bool Prefix() => !ClickThroughGuard.ShouldBlock();
    }
}
