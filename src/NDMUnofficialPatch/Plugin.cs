using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NDMUnofficialPatch
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "ndm.unofficialpatch";
        public const string PluginName = "NDM Unofficial Patch";
        public const string PluginVersion = "0.24.4";

        internal static ManualLogSource Logger;

        // SHA-256 of GameAssembly.dll for every build the patch has been tested on.
        // On any other build the plugin loads but applies no patch.
        private static readonly string[] KnownBuilds =
        {
            "F43C380D13E922D16F32C2C2A192C55D48BCA0AC9A26EEB8D9EDB8437F2E4D04", // 1.8, April 2024
        };

        public override void Load()
        {
            Logger = Log;
            Core.SessionLog.Start(Log);
            Log.LogInfo($"{PluginName} {PluginVersion} loading");

            string hash = HashFile(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            if (Array.IndexOf(KnownBuilds, hash) < 0)
            {
                Log.LogWarning($"Unknown game build (GameAssembly.dll SHA-256 {hash}). No patch applied.");
                return;
            }

            Settings.Bind(Config);
            Log.LogInfo($"Known game build ({hash.Substring(0, 12)}). Applying patches.");
            // Each patch class is applied on its own, so that one failure (a method renamed or overloaded in a
            // future build) is logged and leaves every other patch in place.
            var harmony = new Harmony(PluginGuid);
            int failed = 0;
            foreach (var type in LoadableTypes())
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try
                {
                    string unsafeParameter = HookSafety.FindUnsafeParameter(type);
                    if (unsafeParameter != null)
                    {
                        failed++;
                        Log.LogError($"Patch {type.Name} refused: {unsafeParameter}");
                        continue;
                    }
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"Patch {type.Name} not applied: {e.InnerException?.Message ?? e.Message}");
                }
            }
            Log.LogInfo($"{harmony.GetPatchedMethods().Count()} method(s) patched" + (failed > 0 ? $", {failed} patch class(es) failed" : ""));

            if (Settings.CharacterManager.Value || Settings.MinionColumns.Value || Settings.DiagnosticsMorale.Value || Settings.RoomNeedsPanel.Value || Settings.RecruitTargets.Value)
            {
                AddComponent<Management.ManagerBehaviour>();
            }
            if (Settings.GameEventSystem.Value || Settings.FurnitureToolSwitch.Value || Settings.CombatStrength.Value || Settings.MaximumMinions.Value > 0 || Floors.FloorInsertion.Needed)
            {
                AddComponent<Fixes.FixesBehaviour>();
            }
            if (Settings.ResourceBar.Value || Settings.Bilan.Value || Settings.StorageCapacity.Value || Settings.ResourceMaximum.Value > 0)
            {
                AddComponent<Economy.ResourceBarBehaviour>();
            }
        }

        // Types of this plugin that can be loaded. If one type references an assembly that is missing, GetTypes throws;
        // the other types are still returned, so every other patch applies.
        private static System.Collections.Generic.IEnumerable<Type> LoadableTypes()
        {
            try
            {
                return typeof(Plugin).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                Logger.LogWarning($"Some types of the patch could not load ({e.LoaderExceptions.FirstOrDefault()?.Message}); the others are applied");
                return e.Types.Where(t => t != null);
            }
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }

    // Phase 0 smoke test: proves that a Harmony hook on the game's own code fires.
    // SaveBuffersManager is a singleton of the save system; its Awake runs when it is created.
    [HarmonyPatch(typeof(SaveBuffersManager), nameof(SaveBuffersManager.Awake))]
    internal static class SmokeTestSaveBuffersManagerAwake
    {
        private static void Postfix()
        {
            Plugin.Logger.LogInfo("Smoke test: hook on SaveBuffersManager.Awake fired");
        }
    }
}
