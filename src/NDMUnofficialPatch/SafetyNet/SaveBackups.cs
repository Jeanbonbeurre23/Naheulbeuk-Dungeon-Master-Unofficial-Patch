using System;
using System.IO;
using System.Linq;
using Aube;
using HarmonyLib;

namespace NDMUnofficialPatch.SafetyNet
{
    // Problem G6. Before the game writes a save file, the existing file is copied to
    // <save root>\NDMUnofficialPatch-backups\<file name>\<yyyyMMdd-HHmmss><extension>,
    // and only the newest N copies are kept. The backup folder sits outside the game's Save folder,
    // so the game never lists it. A failure here is logged and never stops the game from saving.
    [HarmonyPatch(typeof(SaveManagerStandalone), nameof(SaveManagerStandalone.Save))]
    internal static class SaveBackups
    {
        private static bool Prepare() => Settings.SaveBackups.Value;

        private static void Prefix(SaveManagerStandalone __instance, SaveManager.SaveRequest request)
        {
            try
            {
                string name = request?.Filename;
                if (string.IsNullOrEmpty(name)) return;
                string path = __instance.CreatePath(name);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                string saveDir = Path.GetDirectoryName(path);
                string root = Path.GetDirectoryName(saveDir) ?? saveDir;
                string dir = Path.Combine(root, "NDMUnofficialPatch-backups", Path.GetFileName(path));
                Directory.CreateDirectory(dir);

                string dest = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(path));
                File.Copy(path, dest, true);

                // Names start with a sortable timestamp, so name order is age order.
                var old = new DirectoryInfo(dir).GetFiles()
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .Skip(Settings.SaveBackupsPerFile.Value)
                    .ToList();
                foreach (var f in old) f.Delete();

                Plugin.Logger.LogInfo($"[SaveBackups] copied '{Path.GetFileName(path)}' to {dest} ({old.Count} old copy(ies) removed)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[SaveBackups] backup failed, the game saves normally: {e.Message}");
            }
        }
    }
}
