using System;
using System.Collections.Generic;
using HarmonyLib;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Fixes
{
    // A click on a furniture icon switches the builder to furniture, or says why it cannot.
    //
    // Game 1.8, from the method bodies. A click on an icon of the construction popup runs
    // ConstructionPropPopup.OnItemSlotSubmit, which, for anything but a door, calls SwitchToProp and then sets the
    // popup's tool to furniture (SetTool(ADD_PROP)) whatever SwitchToProp did. SwitchToProp leaves the builder in its
    // state without a word in two cases. In wall mode (Builder.BuildState.Walls) it first validates the walls
    // (Builder.ValidateWalls), which refuses when the room being edited is on another floor than the one displayed
    // (BuilderManagerComponent.BuildingFloor against DungeonGameMode.m_currentFloorIndex), when a wall stands on an
    // obstacle (WallOnObstacle) or when the room's surface is not valid (SurfaceNotValid), among other checks. Outside
    // corridors and gardens it also requires the room to have walls (BuilderManagerComponent.DoesRoomHaveWall). The
    // popup then shows the furniture tool while the builder keeps adding or removing walls, or deleting.
    //
    // A postfix on OnItemSlotSubmit checks whether the builder reached the furniture state (Props, or Doors for a
    // door). If not, it logs the builder's state and the reasons it can read, then removes the causes that cost no
    // work and tries again: a wall section being drawn is cancelled (ConstructionPropPopup.TryCancelWallSection), and
    // when another floor is displayed the room's floor is asked for (DungeonPage.SetDesiredFloor) and the switch is
    // tried again once it shows. If the builder still does not switch, the popup's tool is set back to the one in use
    // before the click, so the buttons show the mode the builder is really in.
    internal static unsafe class FurnitureToolSwitch
    {
        private static ConstructionPropPopup.Tool _toolBefore;
        private static Builder.BuildState _stateBefore;

        private static ConstructionPropPopup _pendingPopup;
        private static PropEntityConfig _pendingProp;
        private static ConstructionPropPopup.Tool _pendingTool;
        private static int _pendingFloor;
        private static float _pendingUntil;
        private static int _logged;
        private static bool _errorLogged;

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Builder] {what} failed, further errors are not logged: {e.Message}");
        }

        private static void Log(string message)
        {
            if (_logged >= 100) return;
            _logged++;
            Plugin.Logger.LogInfo("[Builder] " + message);
        }

        internal static void Before(ConstructionPropPopup popup)
        {
            try
            {
                _toolBefore = popup.m_currentTool;
                var builder = popup.m_builder;
                _stateBefore = builder == null ? Builder.BuildState.Idle : builder.State;
            }
            catch (Exception e) { ReportError("reading the builder before a click on an icon", e); }
        }

        internal static void After(ConstructionPropPopup popup, ConstructionPropSlot slot)
        {
            try
            {
                var prop = slot?.m_config;
                var builder = popup.m_builder;
                // A locked icon is refused by the game before anything else; nothing to repair.
                if (prop == null || builder == null || slot.m_isLock) return;
                bool door = prop.m_propIdentity != null && prop.m_propIdentity.Type == PropType.DOOR;
                var wanted = door ? Builder.BuildState.Doors : Builder.BuildState.Props;
                var state = builder.State;
                if (state == wanted) return;

                var reasons = Reasons(out int buildingFloor, out int shownFloor, out bool drawing);
                Log($"a click on {(door ? "a door" : "a furniture")} icon left the builder in {state} (tool before: {_toolBefore}, state before: {_stateBefore}); {reasons}");
                if (door) { Restore(popup, "doors are left to the game"); return; }

                if (drawing && popup.TryCancelWallSection())
                {
                    Log("the wall section being drawn was cancelled; trying again");
                    if (TrySwitch(popup, prop)) return;
                }
                if (buildingFloor >= 0 && shownFloor >= 0 && buildingFloor != shownFloor)
                {
                    var page = UnityEngine.Object.FindObjectOfType<DungeonPage>();
                    if (page != null)
                    {
                        page.SetDesiredFloor(buildingFloor);
                        _pendingPopup = popup;
                        _pendingProp = prop;
                        _pendingTool = _toolBefore;
                        _pendingFloor = buildingFloor;
                        _pendingUntil = Time.unscaledTime + 4f;
                        Log($"showing the room's floor (index {buildingFloor}, displayed {shownFloor}); the switch is tried again once it shows");
                        return;
                    }
                }
                Restore(popup, reasons);
            }
            catch (Exception e) { ReportError("checking the builder after a click on an icon", e); }
        }

        private static bool TrySwitch(ConstructionPropPopup popup, PropEntityConfig prop)
        {
            popup.SwitchToProp(prop);
            if (popup.m_builder.State != Builder.BuildState.Props) return false;
            popup.SetTool(ConstructionPropPopup.Tool.ADD_PROP);
            Log("the builder switched to furniture");
            return true;
        }

        private static void Restore(ConstructionPropPopup popup, string why)
        {
            popup.SetTool(_toolBefore);
            Log($"the builder stays in {popup.m_builder.State}; the popup's tool is set back to {_toolBefore} ({why})");
        }

        // Every frame, from FixesBehaviour: the second try once the room's floor is displayed.
        internal static void Update()
        {
            if (_pendingPopup == null) return;
            try
            {
                if (_pendingPopup.WasCollected || Time.unscaledTime > _pendingUntil)
                {
                    if (!_pendingPopup.WasCollected)
                    {
                        _toolBefore = _pendingTool;
                        Restore(_pendingPopup, "the room's floor did not show");
                    }
                    _pendingPopup = null;
                    return;
                }
                var mode = DungeonGameMode.Instance;
                if (mode == null || mode.m_currentFloorIndex != _pendingFloor) return;
                var popup = _pendingPopup;
                _pendingPopup = null;
                if (!TrySwitch(popup, _pendingProp))
                {
                    _toolBefore = _pendingTool;
                    Restore(popup, Reasons(out _, out _, out _));
                }
            }
            catch (Exception e)
            {
                _pendingPopup = null;
                ReportError("switching the builder once the floor showed", e);
            }
        }

        // What BuilderManagerComponent says, read in place: the floor of the room being edited against the floor
        // displayed, and the flags ValidateWalls refuses on.
        private static string Reasons(out int buildingFloor, out int shownFloor, out bool drawing)
        {
            buildingFloor = shownFloor = -1;
            drawing = false;
            var parts = new List<string>();
            try
            {
                var mode = DungeonGameMode.Instance;
                if (mode != null) shownFloor = mode.m_currentFloorIndex;
                if (!GameContext.TryWorld(out var world, out _)) return "builder data not readable";
                var pool = RawPool.Of<BuilderManagerComponent>(world, -1);
                IntPtr m = pool.Item(pool.FirstEntity());
                if (m == IntPtr.Zero) return "builder data not found";
                buildingFloor = *(int*)(m + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("BuildingFloor"));
                drawing = *(bool*)(m + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("IsBuildingWall"));
                bool obstacle = *(bool*)(m + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("WallOnObstacle"));
                bool surface = *(bool*)(m + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("SurfaceNotValid"));
                bool money = *(bool*)(m + Il2CppRaw.ValueFieldOffset<BuilderManagerComponent>("NotEnoughMoney"));
                if (buildingFloor != shownFloor) parts.Add($"the room is on floor index {buildingFloor} and floor index {shownFloor} is displayed");
                if (drawing) parts.Add("a wall section is being drawn");
                if (obstacle) parts.Add("a wall stands on an obstacle");
                if (surface) parts.Add("the room's surface is not valid");
                if (money) parts.Add("not enough gold");
            }
            catch (Exception e) { parts.Add("builder data unreadable: " + e.Message); }
            return parts.Count == 0 ? "no refusal reason readable" : string.Join(", ", parts);
        }
    }

    [HarmonyPatch(typeof(ConstructionPropPopup), nameof(ConstructionPropPopup.OnItemSlotSubmit))]
    internal static class FurnitureToolSwitchPatch
    {
        private static bool Prepare() => Settings.FurnitureToolSwitch.Value;
        private static void Prefix(ConstructionPropPopup __instance) => FurnitureToolSwitch.Before(__instance);
        private static void Postfix(ConstructionPropPopup __instance, ConstructionPropSlot __1) => FurnitureToolSwitch.After(__instance, __1);
    }
}
