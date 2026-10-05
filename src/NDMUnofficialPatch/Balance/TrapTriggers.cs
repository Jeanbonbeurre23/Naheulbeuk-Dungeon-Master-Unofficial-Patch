using System;
using HarmonyLib;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // Armed traps always go off under adventurers and never under the player's minions.
    //
    // Game 1.8, from the method bodies and the configs bundle, read on 1 October 2026. When a character enters a trap's
    // square, UpdateTrapTriggerTileSystem.OnStepOnATrap checks that the trap is loaded (TrapComponent) and hands the
    // step to OnMinionStepOnATrap for a minion (MinionTag) or to OnAdventurerStepOnATrap for an adventurer
    // (AdventurerTag); no other character sets a trap off this way.
    //
    // For a minion, after checks that include whether a relic is exposed, the game draws a number between 0 and 1 and
    // sets the trap off when it is below TrapConfig.m_minionProbability (0.01 in the configs) plus the minion's
    // TrapTriggerModifierComponent, clamped to 0..1. The Insouciant trait carries TRAP_TRIGGER_MODIFIER_VALUE +0.05,
    // which is very probably that modifier. An elf who escapes the draw gets a second one (TryTriggerTrapForElf, which
    // reads m_elfProbability, 0.02, by its name; its body was not read).
    //
    // For an adventurer, the game requires a group (GroupLinkComponent). On a deceiving trap it goes on only for one
    // adventurer of the group, by the order of the system's fields the leader (GroupLeaderTag), and the trap goes off
    // when an origin of the group is one the trap targets (DeceivingTrapTypeConfig: dwarves, probability 1). On any other
    // trap it goes off when the draw is below m_adventurerProbability (0.35). In both cases a group with an elf gets the
    // second draw. When the trap goes off, the game calls TrapUtility.TriggerTrap(trap, adventurer) and
    // CrawlingStatsUtility.OnTriggerTrap(adventurer).
    //
    // A prefix on OnMinionStepOnATrap skips it, so a minion never sets a trap off. A prefix on OnAdventurerStepOnATrap
    // makes the two calls the game makes when the trap goes off, and skips the draw, so every adventurer who steps on a
    // loaded trap sets it off, deceiving traps included. If either call fails, the game's own method runs.
    internal static class TrapTriggers
    {
        internal static int AdventurerTriggers, MinionStepsIgnored;
        private static float _nextSummary;
        private static bool _errorLogged;

        internal static void Summary()
        {
            if (Time.unscaledTime < _nextSummary) return;
            _nextSummary = Time.unscaledTime + 300f;
            if (AdventurerTriggers == 0 && MinionStepsIgnored == 0) return;
            Plugin.Logger.LogInfo($"[Traps] last 5 minutes: {AdventurerTriggers} trap(s) set off by adventurers, {MinionStepsIgnored} step(s) of minions on loaded traps left without effect");
            AdventurerTriggers = 0;
            MinionStepsIgnored = 0;
        }

        internal static void Report(Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Traps] error, the game's own rule applies to this trap: {e.Message}");
        }
    }

    [HarmonyPatch(typeof(UpdateTrapTriggerTileSystem), nameof(UpdateTrapTriggerTileSystem.OnMinionStepOnATrap))]
    internal static class TrapsSpareMinions
    {
        private static bool Prepare() => Settings.TrapTriggers.Value;

        private static bool Prefix()
        {
            TrapTriggers.MinionStepsIgnored++;
            TrapTriggers.Summary();
            return false;
        }
    }

    [HarmonyPatch(typeof(UpdateTrapTriggerTileSystem), nameof(UpdateTrapTriggerTileSystem.OnAdventurerStepOnATrap))]
    internal static class TrapsAlwaysOnAdventurers
    {
        private static bool Prepare() => Settings.TrapTriggers.Value;

        // OnAdventurerStepOnATrap(int adventurerEntity, int trapEntity).
        private static bool Prefix(UpdateTrapTriggerTileSystem __instance, int __0, int __1)
        {
            TrapUtility traps;
            try
            {
                traps = new TrapUtility(Il2CppRaw.ReadObject(__instance.Pointer, "m_trapUtility", "TrapUtility"));
            }
            catch (Exception e)
            {
                TrapTriggers.Report(e);
                return true;
            }
            try
            {
                traps.TriggerTrap(__1, __0);
            }
            catch (Exception e)
            {
                TrapTriggers.Report(e);
                return false; // the trap may have gone off already; running the game's draw on top could set it off twice
            }
            try
            {
                new Adventurer.CrawlingStatsUtility(Il2CppRaw.ReadObject(__instance.Pointer, "m_crawlingStatsUtility", "CrawlingStatsUtility")).OnTriggerTrap(__0);
            }
            catch (Exception e) { TrapTriggers.Report(e); }
            TrapTriggers.AdventurerTriggers++;
            TrapTriggers.Summary();
            return false;
        }
    }
}
