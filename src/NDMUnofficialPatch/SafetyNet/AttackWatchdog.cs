using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Combat;
using Il2CppInterop.Runtime;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using UnityEngine;

namespace NDMUnofficialPatch.SafetyNet
{
    internal enum AttackWatchdogMode { Off, Report, End }

    // Problem G5. Read from AlertUpdateSystem in game 1.8: an enemy group in the ALERT state moves to END_ALERT
    // only when no ally targets it any more (AreStillAllyFightingEnemyGroup) or when every member waits to be
    // taken to prison (WaitForIncarcerationComponent or ToIncarcerateComponent). The dungeon-wide alert ends
    // when every group is in NO_ALERT or END_ALERT (IsEndGlobalAlertGroup, then EndGlobalAlert). An ally that
    // keeps a group as its target while unable to reach it, or a member that is neither defeated nor
    // imprisoned, keeps the alert on indefinitely, and with it the locks the alert places on building.
    //
    // At a fixed interval of game time this watchdog takes a snapshot of every group in alert: its members,
    // which of them are alive and which wait for prison, and the allies that target it. A group whose snapshot
    // has not changed, and in which no fighter has lost life points, for the configured number of minutes is
    // reported in full in the log and, in End mode, moved to END_ALERT, the state the game itself sets when a fight is
    // over. The game's own UpdateEndAlert then runs on the next frame and releases the fighters.
    internal static unsafe class AttackWatchdog
    {
        // GroupEnemyComponent holds a generic struct, so the generated wrapper is not a plain struct and the component
        // is read by offset: FixedList32Bytes<int> EnemyEntities at 0, AlertState at 0x20, then IsFighting,
        // NearestArmoryGuardAlert and LastLeaderGridCoordinates. The offsets are checked against the runtime once.
        // GroupEnemyLinkComponent, on each member, holds the group as a plain entity id (int GroupEnemy).
        private const int GroupSize = 52;
        private const int AlertStateOffset = 0x20;
        private const int MembersOffset = 4;  // FixedList32Bytes<int>: ushort length, 2 bytes of padding, then up to 7 ints
        private const int MaxMembers = 7;
        private const int StartAlert = 2, Alert = 3, EndAlert = 4;
        private static bool _offsetsChecked;

        private sealed class Watch
        {
            internal string Signature;
            internal float Since;      // game time of the last change or of the last damage dealt
            internal float FirstSeen;
            internal bool Reported;
            internal readonly Dictionary<int, float> LastLife = new();
        }

        private static float _next;
        private static IntPtr _system;
        private static bool _disabled;
        private static bool _layoutChecked;
        private static int _failures;
        private static readonly Dictionary<long, Watch> Watches = new();

        internal static void Tick(AlertUpdateSystem system)
        {
            if (_disabled) return;
            float now = Time.time;
            if (now < _next) return;
            _next = now + 15f;

            try
            {
                if (system.Pointer != _system)
                {
                    _system = system.Pointer;
                    Watches.Clear();
                }
                Check(system.Pointer, now);
                _failures = 0;
            }
            catch (Exception e)
            {
                _failures++;
                Plugin.Logger.LogWarning($"[Attack watchdog] check failed ({_failures} in a row): {e.Message}");
                if (_failures >= 3)
                {
                    _disabled = true;
                    Plugin.Logger.LogError($"[Attack watchdog] disabled for this session after 3 failed checks. Last error: {e}");
                }
            }
        }

        private static void Check(IntPtr sys, float now)
        {
            var world = new EcsWorld(Il2CppRaw.ReadObject(sys, "m_ecsWorld", "EcsWorld"));
            if (!world.IsAlive()) return;
            if (!_offsetsChecked)
            {
                Il2CppRaw.ExpectValueFieldOffset<GroupEnemyComponent>("EnemyEntities", 0);
                Il2CppRaw.ExpectValueFieldOffset<GroupEnemyComponent>("AlertState", AlertStateOffset);
                _offsetsChecked = true;
            }
            int worldSize = world.GetWorldSize();

            var groups = RawPool.From(Il2CppRaw.ReadPointer(sys, "m_enemyGroupPool"), GroupSize, "GroupEnemyComponent");
            var combat = RawPool.From(Il2CppRaw.ReadPointer(sys, "m_combatPool"), sizeof(CombatComponent), "CombatComponent");
            var waitPrison = RawPool.From(Il2CppRaw.ReadPointer(sys, "m_waitForIncarcerationPool"), -1, "WaitForIncarcerationComponent");
            var toPrison = RawPool.From(Il2CppRaw.ReadPointer(sys, "m_toIncarceratePool"), -1, "ToIncarcerateComponent");
            var links = RawPool.Of<GroupEnemyLinkComponent>(world, sizeof(GroupEnemyLinkComponent));
            var groupFilter = new EcsFilter(Il2CppRaw.ReadObject(sys, "m_enemyGroupFilter", "EcsFilter"));
            var allyFilter = new EcsFilter(Il2CppRaw.ReadObject(sys, "m_combatAllyFilter", "EcsFilter"));
            IEcsPool life = world.GetPoolByType(Il2CppType.Of<LifePointsComponent>());

            // Allies currently in combat, and the group each one targets.
            var allies = new List<(int ally, int target)>();
            int allyCount = allyFilter.GetEntitiesCount();
            var allyIds = allyFilter.GetRawEntities();
            for (int i = 0; i < allyCount; i++)
            {
                int a = allyIds[i];
                IntPtr c = combat.Item(a);
                if (c != IntPtr.Zero) allies.Add((a, world.Resolve(((CombatComponent*)c)->EnemyGroupToTarget, worldSize)));
            }

            var seen = new HashSet<long>();
            int groupCount = groupFilter.GetEntitiesCount();
            var groupIds = groupFilter.GetRawEntities();
            for (int i = 0; i < groupCount; i++)
            {
                int g = groupIds[i];
                IntPtr item = groups.Item(g);
                if (item == IntPtr.Zero) continue;
                int state = *(int*)(item + AlertStateOffset);
                if (state != StartAlert && state != Alert) continue;

                int len = *(ushort*)item;
                if (len > MaxMembers) throw new InvalidOperationException($"group {g} reports {len} members, more than a FixedList32Bytes<int> holds; layout differs from game 1.8");
                var members = new int[len];
                for (int m = 0; m < len; m++) members[m] = *(int*)(item + MembersOffset + m * 4);
                if (!_layoutChecked) CheckLayout(world, worldSize, links, g, members);

                // Who is in the fight and in what condition. Life points are compared separately, because
                // regeneration changes them without anything happening; only a drop counts as progress.
                var sb = new StringBuilder();
                var fighters = new List<int>();
                foreach (int m in members)
                {
                    bool alive = world.IsEntityAlive(m, worldSize);
                    bool jailed = alive && (waitPrison.Has(m) || toPrison.Has(m));
                    sb.Append(" m").Append(m).Append(alive ? (jailed ? ":prison" : ":up") : ":gone");
                    if (alive && !jailed) fighters.Add(m);
                }
                foreach (var (ally, target) in allies)
                {
                    if (target != g) continue;
                    sb.Append(" a").Append(ally);
                    fighters.Add(ally);
                }
                string signature = sb.ToString();

                long key = ((long)world.GetEntityGen(g) << 32) | (uint)g;
                seen.Add(key);
                if (!Watches.TryGetValue(key, out var w))
                {
                    w = new Watch { Signature = signature, Since = now, FirstSeen = now };
                    Watches[key] = w;
                    RecordDamage(w, life, fighters);
                    continue;
                }
                bool damage = RecordDamage(w, life, fighters);
                if (w.Signature != signature || damage)
                {
                    w.Signature = signature;
                    w.Since = now;
                    w.Reported = false;
                    continue;
                }

                float stuckFor = now - w.Since;
                if (stuckFor < Settings.AttackWatchdogMinutes.Value * 60f) continue;

                if (!w.Reported)
                {
                    w.Reported = true;
                    Plugin.Logger.LogWarning($"[Attack watchdog] enemy group {g} unchanged for {stuckFor / 60f:0.0} min (alert running for {(now - w.FirstSeen) / 60f:0.0} min): {Describe(world, worldSize, combat, links, waitPrison, toPrison, life, g, members, allies)}");
                }
                if (Settings.AttackWatchdog.Value == AttackWatchdogMode.End)
                {
                    *(int*)(item + AlertStateOffset) = EndAlert;
                    Watches.Remove(key);
                    Plugin.Logger.LogWarning($"[Attack watchdog] enemy group {g}: alert state set to END_ALERT; the game's end-of-alert routine runs on the next frame");
                }
            }

            var gone = new List<long>();
            foreach (var k in Watches.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) Watches.Remove(k);
        }

        private static float LifeValue(IEcsPool life, int entity)
        {
            try
            {
                if (life == null || !life.Has(entity)) return float.NaN;
                var c = new LifePointsComponent(life.GetRaw(entity).Pointer);
                return c.Gauge.CurrentValue;
            }
            catch
            {
                return float.NaN;
            }
        }

        private static string Life(IEcsPool life, int entity)
        {
            float v = LifeValue(life, entity);
            return float.IsNaN(v) ? "unknown" : v.ToString("0.#");
        }

        // True when any fighter has fewer life points than at the previous check. A rise (regeneration) is not progress.
        private static bool RecordDamage(Watch w, IEcsPool life, List<int> fighters)
        {
            bool damage = false;
            foreach (int f in fighters)
            {
                float v = LifeValue(life, f);
                if (float.IsNaN(v)) continue;
                if (w.LastLife.TryGetValue(f, out float last) && v < last - 0.01f) damage = true;
                w.LastLife[f] = v;
            }
            return damage;
        }

        // The member list is read at a fixed offset. Check once per session that the ids read there are entities
        // whose GroupEnemyLinkComponent points back at the group; if none of them does, the layout is not the one
        // this code was written for, and the watchdog stops rather than act on garbage.
        private static void CheckLayout(EcsWorld world, int worldSize, RawPool links, int group, int[] members)
        {
            if (members.Length == 0 || links.IsNull) return;
            foreach (int m in members)
            {
                if (!world.IsEntityAlive(m, worldSize)) continue;
                IntPtr link = links.Item(m);
                if (link != IntPtr.Zero && ((GroupEnemyLinkComponent*)link)->GroupEnemy == group)
                {
                    _layoutChecked = true;
                    return;
                }
            }
            bool anyAlive = Array.Exists(members, m => world.IsEntityAlive(m, worldSize));
            if (anyAlive)
                throw new InvalidOperationException($"no member of group {group} links back to it; member list layout differs from game 1.8");
        }

        private static string Describe(EcsWorld world, int worldSize, RawPool combat, RawPool links, RawPool waitPrison, RawPool toPrison,
            IEcsPool life, int group, int[] members, List<(int ally, int target)> allies)
        {
            var sb = new StringBuilder();
            sb.Append(members.Length).Append(" member(s):");
            foreach (int m in members)
            {
                sb.Append(" [").Append(m);
                if (!world.IsEntityAlive(m, worldSize)) { sb.Append(" no longer exists]"); continue; }
                IntPtr link = links.Item(m);
                if (link == IntPtr.Zero || ((GroupEnemyLinkComponent*)link)->GroupEnemy != group) sb.Append(" not linked to this group");
                if (waitPrison.Has(m)) sb.Append(" waiting for prison");
                if (toPrison.Has(m)) sb.Append(" being taken to prison");
                IntPtr c = combat.Item(m);
                sb.Append(" combat ").Append(c == IntPtr.Zero ? "none" : ((CombatComponent*)c)->State.ToString());
                sb.Append(" life ").Append(Life(life, m)).Append(']');
            }
            int targeting = 0;
            foreach (var (ally, target) in allies)
            {
                if (target != group) continue;
                targeting++;
                IntPtr c = combat.Item(ally);
                sb.Append(" ally [").Append(ally).Append(" combat ").Append(c == IntPtr.Zero ? "none" : ((CombatComponent*)c)->State.ToString())
                  .Append(" life ").Append(Life(life, ally)).Append(']');
            }
            if (targeting == 0) sb.Append(" no ally targets this group");
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(AlertUpdateSystem), nameof(AlertUpdateSystem.Run))]
    internal static class AttackWatchdogTick
    {
        private static bool Prepare() => Settings.AttackWatchdog.Value != AttackWatchdogMode.Off;
        private static void Postfix(AlertUpdateSystem __instance) => AttackWatchdog.Tick(__instance);
    }
}
