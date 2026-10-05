using System;
using HarmonyLib;

namespace NDMUnofficialPatch
{
    // A new game, campaign or sandbox, is about to start. The floor insertion has to know it before the scenes load,
    // and the character manager and the recruitment targets must not restore the data of a save that the main menu
    // only read to list it.
    //
    // Game 1.8, from the method bodies, read on 5 October 2026. Three methods leave the main menu for a new game:
    // GameMaster.LaunchCampaign; ModeSelectionPage.OnPopupValidate, whose body is a copy of LaunchCampaign's (the
    // compiler inlined it there) and which therefore never calls it; and SandboxSettingsPage.LaunchSandbox. A new
    // campaign started on 5 October 2026 logged no call to LaunchCampaign, so it went through OnPopupValidate. Each of
    // the three is hooked here.
    internal static class NewGameNotice
    {
        internal static void Notice(string how)
        {
            try { Floors.FloorInsertion.OnNewGame(how); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[NewGame] floor insertion not told: {e.Message}"); }
            try { Management.CharacterManager.OnNewGame(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[NewGame] character manager not told: {e.Message}"); }
            try { Management.RecruitTargets.OnNewGame(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[NewGame] recruitment targets not told: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(GameMaster), nameof(GameMaster.LaunchCampaign))]
    internal static class NewCampaignPatch
    {
        private static void Prefix() => NewGameNotice.Notice("campaign");
    }

    [HarmonyPatch(typeof(ModeSelectionPage), nameof(ModeSelectionPage.OnPopupValidate))]
    internal static class NewCampaignPopupPatch
    {
        private static void Prefix() => NewGameNotice.Notice("campaign, from the mode selection popup");
    }

    [HarmonyPatch(typeof(SandboxSettingsPage), nameof(SandboxSettingsPage.LaunchSandbox))]
    internal static class NewSandboxPatch
    {
        private static void Prefix() => NewGameNotice.Notice("sandbox");
    }
}
