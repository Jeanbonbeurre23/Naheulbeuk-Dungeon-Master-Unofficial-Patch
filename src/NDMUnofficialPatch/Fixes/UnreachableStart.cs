using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Mathematics;

namespace NDMUnofficialPatch.Fixes
{
    // Redoes the builder's accessibility walk when the game's own walk did not start.
    //
    // Game 1.8, from the method bodies. After each change the builder validates, CheckUnreachableTilesJob fills a set
    // with every walkable square of the floor, then walks from the free square of one stairs entity of the floor
    // (m_stairTile, the first its filter yields; StairsUtility.GetUnblockedTile gives the square) and from the dungeon
    // entrances, removing each square it reaches through its 4 neighbours. BuilderJobs.UpdateUnreachableTilesJob hands
    // what is left to Builder.SetUnreachableTiles, which marks those squares and refuses to validate while any remain.
    //
    // On the third floor of a test save, on 30 September 2026, after walls were drawn in the east of the floor, the set came back
    // with every walkable square of the floor still in it, the stairs' own free square included, although a walk from
    // that square over the same set reached every square (1091 of 1091).
    // The job's walk had not started. Why it does not start was not found in the code.
    //
    // A prefix on Builder.SetUnreachableTiles checks for that case: when the free square of the job's stairs is still
    // in the set, it walks from that square over the set, 4 neighbours at a time as the job does, and removes the
    // squares reached before the builder reads the set. It walks from the same stairs as the game, so a part of the
    // floor that those stairs cannot reach stays marked. The start square is removed only when the walk reaches a
    // neighbour of it, as in the job.
    [HarmonyPatch(typeof(Builder), nameof(Builder.SetUnreachableTiles))]
    [HarmonyPriority(Priority.High)]
    internal static class UnreachableStart
    {
        private static bool Prepare() => Settings.UnreachableStart.Value;

        private static bool _errorLogged;
        private static string _lastLogged = "";

        private static void Prefix(Builder __instance, Il2CppSystem.Collections.Generic.HashSet<int2> unreachableTiles)
        {
            try
            {
                var set = unreachableTiles;
                if (set == null || set.Count == 0 || __instance == null) return;
                var jobs = __instance.m_jobs;
                var job = jobs?.m_checkUnreachableJob;
                if (job == null) return;
                int stairs = job.m_stairTile;
                if (stairs < 0) return;
                var util = __instance.m_stairsUtility;
                if (util == null) return;
                int2 start = util.GetUnblockedTile(stairs);
                if (!set.Contains(start)) return; // the game's walk reached it

                int before = set.Count;
                var reached = new HashSet<(int, int)>();
                var queue = new Queue<int2>();
                queue.Enqueue(start);
                bool startReachedBack = false;
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    foreach (var n in new[] { new int2(t.x + 1, t.y), new int2(t.x - 1, t.y), new int2(t.x, t.y + 1), new int2(t.x, t.y - 1) })
                    {
                        if (n.x == start.x && n.y == start.y) { startReachedBack = true; continue; }
                        if (reached.Contains((n.x, n.y)) || !set.Contains(n)) continue;
                        reached.Add((n.x, n.y));
                        queue.Enqueue(n);
                    }
                }
                foreach (var (x, y) in reached) set.Remove(new int2(x, y));
                if (startReachedBack) set.Remove(start);

                string line = $"[Unreachable] the builder's walk from stairs {stairs} did not start; walked from its free square ({start.x},{start.y}): {before - set.Count} square(s) reached, {set.Count} left unreachable";
                if (line != _lastLogged) Plugin.Logger.LogInfo(line);
                _lastLogged = line;
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Unreachable] redoing the builder's walk failed, further errors are not logged: {e.Message}");
            }
        }
    }
}
