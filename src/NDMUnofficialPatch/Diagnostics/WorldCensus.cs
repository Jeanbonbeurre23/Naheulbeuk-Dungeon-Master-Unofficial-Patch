using System;
using System.Text;
using HarmonyLib;
using Leopotam.EcsLite;
using UnityEngine;
using Walls;

namespace NDMUnofficialPatch.Diagnostics
{
    // Phase 2 groundwork: proves that the plugin can read the game's ECS world, which every
    // watchdog will need. Once a minute of real time it logs a few counts read from system filters.
    internal static class WorldCensus
    {
        private static float _next;
        private static bool _errorLogged;
        internal static EcsWorld World;
        internal static TavernInfluxSystem Tavern;
        internal static CheckRoomConstructionStateSystem Construction;

        internal static void Reset()
        {
            World = null;
            Tavern = null;
            Construction = null;
            _next = 0f;
        }

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _next) return;
            _next = now + 60f;
            try
            {
                var sb = new StringBuilder("[Census]");
                if (World != null) sb.Append(" entities ").Append(World.GetEntitiesCount());
                if (Tavern != null)
                {
                    sb.Append(", tavern customers ").Append(Tavern.m_clientFilter.Value.GetEntitiesCount());
                    sb.Append(", barmen ").Append(Tavern.m_barmansFilter.Value.GetEntitiesCount());
                }
                if (Construction != null)
                    sb.Append(", rooms under construction ").Append(Construction.m_roomInConstructionFilter.Value.GetEntitiesCount());
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Census] reading the world failed: {e}");
            }
        }
    }

    // A new world is built when a game starts or loads; drop references to the previous one.
    [HarmonyPatch(typeof(TutorialSystem), nameof(TutorialSystem.Init))]
    internal static class CensusResetPatch
    {
        private static bool Prepare() => Settings.DiagnosticsCensus.Value;
        private static void Postfix() => WorldCensus.Reset();
    }

    [HarmonyPatch(typeof(TavernInfluxSystem), nameof(TavernInfluxSystem.Run))]
    internal static class CensusTavernPatch
    {
        private static bool Prepare() => Settings.DiagnosticsCensus.Value;
        private static void Postfix(TavernInfluxSystem __instance)
        {
            if (WorldCensus.Tavern == null)
            {
                WorldCensus.Tavern = __instance;
                WorldCensus.World = __instance.m_world.Value;
            }
            WorldCensus.Tick();
        }
    }

    [HarmonyPatch(typeof(CheckRoomConstructionStateSystem), nameof(CheckRoomConstructionStateSystem.Run))]
    internal static class CensusConstructionPatch
    {
        private static bool Prepare() => Settings.DiagnosticsCensus.Value;
        private static void Postfix(CheckRoomConstructionStateSystem __instance)
        {
            if (WorldCensus.Construction == null) WorldCensus.Construction = __instance;
        }
    }
}
