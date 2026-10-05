using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NDMUnofficialPatch.Floors
{
    // Hooks of the floor insertion (FloorInsertion). All are installed when Floors.InsertedFloors is above 0 or when a
    // save carries the inserted-floors marker; each does nothing in a game without inserted floors.

    // DungeonGameMode declares OnSceneLoaded over its base class's, so the declared one is named explicitly.
    [HarmonyPatch]
    internal static class FloorSceneLoadedPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(DungeonGameMode), nameof(DungeonGameMode.OnSceneLoaded));
        private static void Prefix()
        {
            try { FloorInsertion.OnSceneLoaded(); }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] scene load: {e}"); }
        }
    }

    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.LaunchCampaign))]
    internal static class FloorNewCampaignPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix() => FloorInsertion.OnNewGame("campaign");
    }

    [HarmonyPatch(typeof(SandboxSettingsPage), nameof(SandboxSettingsPage.LaunchSandbox))]
    internal static class FloorNewSandboxPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix() => FloorInsertion.OnNewGame("sandbox");
    }

    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.LoadGame))]
    internal static class FloorLoadGamePatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix(string __0) => FloorInsertion.OnLoadGame(__0);
    }

    [HarmonyPatch(typeof(Aube.SaveManagerStandalone), nameof(Aube.SaveManagerStandalone.Load))]
    internal static class FloorSaveReadPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix(Aube.SaveManagerStandalone __instance, Aube.SaveManager.LoadRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) FloorInsertion.OnSaveRead(name, __instance.CreatePath(name));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] noting the save being read: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Aube.SaveManagerStandalone), nameof(Aube.SaveManagerStandalone.Save))]
    internal static class FloorSaveWritePatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix(Aube.SaveManagerStandalone __instance, Aube.SaveManager.SaveRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (!string.IsNullOrEmpty(name)) FloorInsertion.OnSaveWrite(name, __instance.CreatePath(name));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] marking the save: {e.Message}"); }
        }
    }

    // CameraController.m_boundaryList holds one rectangle per floor, read at floor + 2 (floors -2 to 6). The inserted
    // floors take floor 4's rectangle; the two floors above keep those of the floors they were.
    [HarmonyPatch(typeof(CameraController), nameof(CameraController.Init))]
    internal static class FloorCameraPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix(CameraController __instance)
        {
            if (!FloorInsertion.Active) return;
            try
            {
                var list = __instance.m_boundaryList;
                if (list == null || list.Count != 9)
                {
                    Plugin.Logger.LogWarning($"[Floors] camera limits: {(list == null ? "no list" : list.Count + " rectangles")}, expected 9; left as they are");
                    return;
                }
                var four = list[FloorInsertion.Copied + 2];
                for (int k = 0; k < FloorInsertion.Count; k++) list.Insert(FloorInsertion.FirstInserted + 2, four);
                Plugin.Logger.LogInfo($"[Floors] camera limits: floor 4's rectangle given to floors {FloorInsertion.FirstInserted} to {FloorInsertion.LastInserted} ({list.Count} rectangles)");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] camera limits: {e}"); }
        }
    }

    // DungeonPage.Awake collects the floor buttons under the floor tower and sizes its per-floor enemy count from
    // their number. Before that, the patch copies floor 4's button for each inserted floor, renumbers the two buttons
    // above and relabels.
    [HarmonyPatch(typeof(DungeonPage), nameof(DungeonPage.Awake))]
    internal static class FloorButtonsPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix(DungeonPage __instance)
        {
            if (!FloorInsertion.Active) return;
            try
            {
                var root = __instance.m_floorButtonRoot;
                if (root == null) { Plugin.Logger.LogWarning("[Floors] floor buttons: no button root"); return; }
                var buttons = root.GetComponentsInChildren<FloorButton>(true);
                FloorButton four = null;
                foreach (var b in buttons)
                {
                    if (b.m_floor > 6) return; // already done
                    if (b.m_floor == FloorInsertion.Copied) four = b;
                }
                if (four == null) { Plugin.Logger.LogWarning("[Floors] floor buttons: no button for floor 4"); return; }
                int n = FloorInsertion.Count;
                foreach (var b in buttons)
                {
                    if (b.m_floor == 6 || b.m_floor == 5) b.m_floor += n;
                }
                // The tower lists floors from the top: each copy goes just above floor 4's button, the highest first.
                for (int floor = FloorInsertion.LastInserted; floor >= FloorInsertion.FirstInserted; floor--)
                {
                    var go = Object.Instantiate(four.gameObject, four.transform.parent, false).Cast<GameObject>();
                    go.transform.SetSiblingIndex(four.transform.GetSiblingIndex());
                    go.name = four.gameObject.name + $" (inserted floor {floor})";
                    go.GetComponent<FloorButton>().m_floor = floor;
                }
                int labelled = 0;
                foreach (var b in root.GetComponentsInChildren<FloorButton>(true))
                {
                    if (b.m_text == null) continue;
                    b.m_text.text = UIUtility.Text.GetFloorText(b.m_floor);
                    labelled++;
                }
                Plugin.Logger.LogInfo($"[Floors] floor buttons: floor 4's button copied for floors {FloorInsertion.FirstInserted} to {FloorInsertion.LastInserted}, buttons 5 and 6 renumbered {5 + n} and {6 + n}, {labelled} label(s) set");
            }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] floor buttons: {e}"); }
        }
    }

    // The inserted floors' buttons show floor 4's lock and conditions.
    [HarmonyPatch(typeof(FloorButton), nameof(FloorButton.TryInitUnlockTreeEntity))]
    internal static class FloorButtonLockPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix(FloorButton __instance)
        {
            if (!FloorInsertion.IsInserted(__instance.m_floor)) return;
            try
            {
                var parent = __instance.transform.parent;
                if (parent == null) return;
                foreach (var b in parent.GetComponentsInChildren<FloorButton>(true))
                {
                    if (b.m_floor != FloorInsertion.Copied) continue;
                    // Floor 4's button finds its entity on its own update; until then the inserted floors' wait.
                    if (b.m_unlockTreeEntity >= 0 && __instance.m_unlockTreeEntity != b.m_unlockTreeEntity)
                        __instance.m_unlockTreeEntity = b.m_unlockTreeEntity;
                    break;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Floors] floor {__instance.m_floor} button lock: {e.Message}"); }
        }
    }

    // The copied button gets the game's ECS injection the other buttons got (DataManager.InjectMonoBehaviour), checked
    // on its first update: in the first test of 0.22.0 it had none and threw in FloorButton.Update every frame.
    [HarmonyPatch(typeof(FloorButton), nameof(FloorButton.Update))]
    internal static class FloorButtonInjectPatch
    {
        private static readonly System.Collections.Generic.HashSet<IntPtr> Checked = new();
        internal static void Reset() => Checked.Clear();
        private static bool Prepare() => FloorInsertion.Needed;
        private static bool Prefix(FloorButton __instance)
        {
            if (!FloorInsertion.Active) return true;
            IntPtr me = __instance.Pointer;
            if (Checked.Contains(me)) return true;
            try
            {
                if (Common.Il2CppRaw.ReadPointer(me, "m_floorManagerPool") != IntPtr.Zero) { Checked.Add(me); return true; }
                var gameMode = DungeonGameMode.Instance;
                var data = gameMode == null ? null : gameMode.DataManager;
                if (data == null || !data.IsInit) return false; // not ready: skip this frame rather than throw
                var list = new Il2CppSystem.Collections.Generic.List<IMonoBehaviourInject>();
                list.Add(new IMonoBehaviourInject(me));
                data.InjectMonoBehaviour(list);
                bool ok = Common.Il2CppRaw.ReadPointer(me, "m_floorManagerPool") != IntPtr.Zero;
                Checked.Add(me);
                Plugin.Logger.LogInfo($"[Floors] floor {__instance.m_floor} button had no ECS injection; injected " + (ok ? "(pools now set)" : "(pools still empty)"));
                return ok;
            }
            catch (Exception e)
            {
                Checked.Add(me);
                Plugin.Logger.LogWarning($"[Floors] injecting the floor {__instance.m_floor} button: {e.Message}");
                return true;
            }
        }
    }

    // The game's systems initialise after the world's configs are loaded: the last moment to extend the tables before
    // the unlock nodes become entities and the wall systems first run.
    [HarmonyPatch(typeof(Leopotam.EcsLite.EcsSystems), nameof(Leopotam.EcsLite.EcsSystems.Init))]
    internal static class FloorSystemsInitPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix()
        {
            try { FloorInsertion.BeforeSystemsInit(); }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] extending the tables: {e}"); }
        }
    }

    // On a load, the inserted floors built by FloorConversion take the new-game path of FloorRoot.Init. The compiler
    // inlined FloorRoot.Init into DungeonGameMode.InitSpawners, so the hook is on the iterator's first step.
    [HarmonyPatch(typeof(FloorRoot._Init_d__2), nameof(FloorRoot._Init_d__2.MoveNext))]
    internal static class FloorRootInitPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Prefix(FloorRoot._Init_d__2 __instance)
        {
            try { FloorConversion.BeforeInitStep(__instance); }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] floor root of a converted save: {e}"); }
        }
    }

    // DungeonGameMode.InjectUI runs once a load has initialised every floor root.
    [HarmonyPatch(typeof(DungeonGameMode), nameof(DungeonGameMode.InjectUI))]
    internal static class FloorLoadedPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix()
        {
            try { FloorConversion.AfterLoad(); }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] converted save after loading: {e}"); }
        }
    }

    // An inserted floor is locked exactly when floor 4 is.
    [HarmonyPatch(typeof(FloorUtility), nameof(FloorUtility.IsFloorLocked))]
    internal static class FloorLockPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix(FloorUtility __instance, int __0, ref bool __result)
        {
            if (FloorInsertion.IsInserted(__0)) __result = __instance.IsFloorLocked(FloorInsertion.Copied);
        }
    }

    [HarmonyPatch(typeof(FloorUtility), nameof(FloorUtility.UnlockFloor))]
    internal static class FloorUnlockPatch
    {
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix(FloorUtility __instance, int __0)
        {
            try { FloorInsertion.AfterUnlock(__instance, __0); }
            catch (Exception e) { Plugin.Logger.LogError($"[Floors] unlocking the inserted floors with floor 4: {e}"); }
        }
    }

    // The game's floor labels exist for floors 0 to 6 (UI_FLOOR_0 to UI_FLOOR_6, counted from 0 or from 1 depending
    // on the language). A floor above 6 takes floor 6's label plus the difference.
    [HarmonyPatch(typeof(UIUtility.Text), nameof(UIUtility.Text.GetFloorText))]
    internal static class FloorLabelPatch
    {
        private static bool _inside;
        private static bool Prepare() => FloorInsertion.Needed;
        private static void Postfix(int __0, ref string __result)
        {
            if (!FloorInsertion.Active || __0 < 7 || _inside) return;
            _inside = true;
            try
            {
                string six = UIUtility.Text.GetFloorText(6);
                __result = int.TryParse(six, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    ? (n + __0 - 6).ToString(CultureInfo.InvariantCulture)
                    : __0.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { __result = __0.ToString(CultureInfo.InvariantCulture); }
            finally { _inside = false; }
        }
    }
}
