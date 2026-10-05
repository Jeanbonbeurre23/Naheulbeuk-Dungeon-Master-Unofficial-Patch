using System;
using System.Collections.Generic;
using HarmonyLib;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Diagnostics
{
    // Writes each resignation of a player minion to the log, with his gauges and the states lowering his morale
    // (MoraleProbe.Describe). The resignation itself goes ahead as the game decides.
    //
    // MinionUtility.MinionResign(minion, fromGuardLockerDestroyed) is where the game makes a minion resign (MoraleProbe's
    // header says where it is called from). A prefix reads the minion before the call and always lets the call through.
    // Guards whose locker was destroyed, unique characters, VIPs and entities that are not player minions are not logged.
    internal static class ResignationLog
    {
        private static readonly Dictionary<int, float> LastLogged = new();
        private static bool _errorLogged;

        internal static void OnResign(int minion, bool fromGuardLockerDestroyed)
        {
            try
            {
                if (fromGuardLockerDestroyed) return;
                if (!GameContext.TryWorld(out var world, out int size) || !world.IsEntityAlive(minion, size)) return;
                if (!RawPool.Of<MinionTag>(world, -1).Has(minion)) return;
                if (RawPool.Of<UniqueComponent>(world, -1).Has(minion) || RawPool.Of<VipTag>(world, -1).Has(minion)) return;
                if (LastLogged.TryGetValue(minion, out float last) && Time.unscaledTime - last < 60f) return;
                LastLogged[minion] = Time.unscaledTime;
                string line;
                try { line = MoraleProbe.Describe(minion, out _); }
                catch (Exception e) { line = $"entity {minion} (reading his state failed: {e.Message})"; }
                Plugin.Logger.LogInfo($"[Resign] resigning: {line}");
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Resign] reading a resigning minion failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.MinionResign))]
    internal static class ResignationLogPatch
    {
        private static bool Prepare() => Settings.DiagnosticsMorale.Value;
        private static void Prefix(int __0, bool __1) => ResignationLog.OnResign(__0, __1);
    }
}
