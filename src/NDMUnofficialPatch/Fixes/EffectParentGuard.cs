using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leopotam.EcsLite;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;

namespace NDMUnofficialPatch.Fixes
{
    // Keeps a visual effect whose parent cannot carry it from stopping the game's systems.
    //
    // Game 1.8, from the method bodies, read on 5 October 2026. A character's animation can ask for a visual effect
    // (SpawnDespawnSimpleVisualObjectAnimEvent.Execute calls SimpleVisualObjectUtility.CreateSimpleVisualObjectEventEntity).
    // The request is an entity carrying SimpleVisualObjectComponent, SpawnObjectEventComponent and
    // ParentLinkSimpleVisualObjectComponent, whose ParentEntity is the entity the effect is attached to. When the
    // animation event's target is the character's action target, the parent is read from the IOEntity component of the
    // character's active action. For an action without that component the pool returns its empty slot, which holds
    // entity 0. Entity 0 is the game's state entity (resources, factions, recruitment) and has no visual.
    //
    // Each frame SimpleVisualObjectEventSystem.Run calls ManageSpawnEvent on every request, then deletes the requests.
    // ManageSpawnEvent builds the effect entity (SimpleVisualObjectUtility.CreateEntity), and InstantiateVisualEntity
    // asks CharacterVisualUtility.TryGetSkeletonBone for the parent's bone. GetSkeleton reads the parent's
    // VisualComponent.Instance and throws NullReferenceException when there is none; on later calls the bone comes back
    // null and InstantiateVisualEntity throws itself. The exception leaves Run before the requests are deleted, so the
    // same request throws again on the next frame, each time leaving one more effect entity half-built (without
    // SimpleVisualObjectComponent), and the systems after this one in EcsSystems.Run no longer run.
    //
    // In a save of 5 October 2026 this began during a battle on the Golbargh's floor and lasted three minutes, until the
    // game was saved: 10,472 exceptions in Player.log, 6,738 half-built effect entities whose parent is entity 0, and
    // 1,204,542 area-of-effect events never deleted.
    //
    // EffectSpawnGuard, a prefix on ManageSpawnEvent, skips a request whose parent exists but has no VisualComponent
    // with a live GameObject; Run then deletes the request as usual. A parent that no longer exists is left to the game,
    // whose InstantiateVisualEntity logs an error and returns no object in that case.
    // EffectLoadGuard, a prefix on SimpleVisualObjectUtility.InstantiateLoadedEntity, skips at load an effect entity
    // without a VisualComponent of its own (a request saved before it was handled, which has no visual to build), and a
    // half-built one whose parent exists without a VisualComponent, which EffectCleanup deletes. When an effect follows
    // its parent, the game checks the parent's visual itself before reading its skeleton.
    // EffectCleanup, a prefix on SimpleVisualObjectEventSystem.Run, deletes once per world, before the first requests
    // are handled, the effect entities whose parent exists without a VisualComponent: half-built effects, and saved
    // requests (SimpleVisualObjectComponent without a VisualComponent and without a spawn or despawn event). The game's
    // own save check, SaveCompatibilityCheckerSystem.CheckParentSimpleVisualObjects, deletes effect entities whose
    // parent no longer exists with the same EcsWorld.DelEntity call.
    internal static unsafe class EffectParents
    {
        private static IntPtr _world;
        private static EcsWorld _worldObject;
        private static RawPool _links, _effects, _visuals, _spawns, _despawns;
        private static int _parentOffset = -1, _idOffset, _genOffset, _characterOffset, _instanceOffset;

        private static IntPtr _spawnSystem, _spawnWorld;
        private static IntPtr _cleanedSystem, _cleanedWorld;
        private static bool _broken;

        private static int _skipped, _skippedAtLoad;
        private static readonly Dictionary<string, int> SkippedAtLoad = new();

        private static void Layout()
        {
            if (_parentOffset >= 0) return;
            Il2CppRaw.ExpectValueFieldOffset<ParentLinkSimpleVisualObjectComponent>("Follow", 0);
            int parent = Il2CppRaw.ValueFieldOffset<ParentLinkSimpleVisualObjectComponent>("ParentEntity");
            _idOffset = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Id");
            _genOffset = Il2CppRaw.ValueFieldOffset<EcsPackedEntity>("Gen");
            _characterOffset = Il2CppRaw.ValueFieldOffset<SimpleVisualObjectComponent>("CharacterEntity");
            _instanceOffset = Il2CppRaw.ValueFieldOffset<VisualComponent>("Instance");
            _parentOffset = parent;
        }

        // The pools of the world, read again whenever the world changes.
        private static bool Bind(IntPtr world)
        {
            if (world == IntPtr.Zero) return false;
            if (world == _world) return true;
            Layout();
            var w = new EcsWorld(world);
            if (!w.IsAlive()) return false;
            var links = RawPool.Of<ParentLinkSimpleVisualObjectComponent>(w, 88);
            var effects = RawPool.Of<SimpleVisualObjectComponent>(w, 24);
            var visuals = RawPool.Of<VisualComponent>(w, 24);
            if (links.IsNull || effects.IsNull || visuals.IsNull) return false;
            _links = links;
            _effects = effects;
            _visuals = visuals;
            _spawns = RawPool.Of<SpawnObjectEventComponent>(w, -1);
            _despawns = RawPool.Of<DespawnObjectEventComponent>(w, -1);
            _worldObject = w;
            _world = world;
            return true;
        }

        private static IntPtr WorldOfSystem(IntPtr system)
            => Il2CppRaw.ReadObject(Il2CppRaw.ReadObject(system, "m_simpleVisualObjectUtility", "SimpleVisualObjectUtility"), "m_world", "EcsWorld");

        // The entity the parent link points to, or -1 when it no longer exists.
        private static int Parent(IntPtr link)
        {
            int id = *(int*)(link + _parentOffset + _idOffset);
            short gen = *(short*)(link + _parentOffset + _genOffset);
            int size = _worldObject.GetWorldSize();
            return _worldObject.IsEntityAlive(id, size) && _worldObject.GetEntityGen(id) == gen ? id : -1;
        }

        private static bool LiveObject(IntPtr gameObject)
            => gameObject != IntPtr.Zero && *(IntPtr*)(gameObject + Il2CppRaw.FieldOffset(gameObject, "m_CachedPtr")) != IntPtr.Zero;

        private static string Describe(int entity)
        {
            if (entity == 0) return "entity 0 (the game's state entity)";
            if (GameContext.Ready)
            {
                try
                {
                    string name = GameContext.Minions.GetMinionFullName(entity);
                    if (!string.IsNullOrWhiteSpace(name)) return $"{name} (entity {entity})";
                }
                catch { }
            }
            return $"entity {entity}";
        }

        private static void Fail(string what, Exception e)
        {
            _broken = true;
            Plugin.Logger.LogWarning($"[Effects] {what} failed, the effect guard is off for this session: {e.Message}");
        }

        // ManageSpawnEvent, before the effect is built: false skips the request.
        internal static bool AllowSpawn(IntPtr system, int request)
        {
            if (_broken) return true;
            try
            {
                if (system != _spawnSystem)
                {
                    _spawnWorld = WorldOfSystem(system);
                    _spawnSystem = system;
                }
                if (!Bind(_spawnWorld)) return true;
                IntPtr link = _links.Item(request);
                if (link == IntPtr.Zero) return true;
                int parent = Parent(link);
                if (parent < 0) return true;
                IntPtr visual = _visuals.Item(parent);
                if (visual != IntPtr.Zero && LiveObject(*(IntPtr*)(visual + _instanceOffset))) return true;

                _skipped++;
                if (_skipped <= 10 || _skipped == 100 || _skipped == 1000 || _skipped % 10000 == 0)
                {
                    string asker = "an unknown character";
                    IntPtr effect = _effects.Item(request);
                    if (effect != IntPtr.Zero)
                    {
                        int id = *(int*)(effect + _characterOffset + _idOffset);
                        short gen = *(short*)(effect + _characterOffset + _genOffset);
                        int size = _worldObject.GetWorldSize();
                        if (_worldObject.IsEntityAlive(id, size) && _worldObject.GetEntityGen(id) == gen) asker = Describe(id);
                    }
                    string why = visual == IntPtr.Zero ? "has no visual" : "has no visual object at the moment";
                    Plugin.Logger.LogInfo($"[Effects] a visual effect asked for by {asker} was not built: its parent, {Describe(parent)}, {why}. Effects skipped this session: {_skipped}");
                }
                return false;
            }
            catch (Exception e)
            {
                Fail("checking a visual effect's parent", e);
                return true;
            }
        }

        // SimpleVisualObjectUtility.InstantiateLoadedEntity, at load: false skips the entity.
        internal static bool AllowLoad(IntPtr utility, int entity)
        {
            if (_broken) return true;
            try
            {
                if (!Bind(Il2CppRaw.ReadObject(utility, "m_world", "EcsWorld"))) return true;
                IntPtr link = _links.Item(entity);
                int parent = link == IntPtr.Zero ? -1 : Parent(link);
                string why;
                if (!_visuals.Has(entity))
                    why = parent == 0 ? $"no visual of its own, attached to {Describe(0)}" : "no visual of its own";
                else if (parent >= 0 && !_effects.Has(entity) && !_visuals.Has(parent))
                    why = $"a creation that did not finish, attached to {Describe(parent)}";
                else
                    return true;
                _skippedAtLoad++;
                SkippedAtLoad[why] = SkippedAtLoad.TryGetValue(why, out int n) ? n + 1 : 1;
                return false;
            }
            catch (Exception e)
            {
                Fail("checking a loaded visual effect", e);
                return true;
            }
        }

        // SimpleVisualObjectEventSystem.Run, once per world before the first requests are handled.
        internal static void CleanOnce(IntPtr system)
        {
            if (_broken || system == _cleanedSystem) return;
            try
            {
                IntPtr world = WorldOfSystem(system);
                _cleanedSystem = system;
                if (world == _cleanedWorld) return;
                if (!Bind(world)) { _cleanedSystem = IntPtr.Zero; return; }
                _cleanedWorld = world;

                if (_skippedAtLoad > 0)
                {
                    Plugin.Logger.LogInfo($"[Effects] at load, {_skippedAtLoad} visual effect(s) not built: "
                        + string.Join("; ", SkippedAtLoad.Select(kv => $"{kv.Value} with {kv.Key}")));
                    _skippedAtLoad = 0;
                    SkippedAtLoad.Clear();
                }

                int halfBuilt = 0, requests = 0;
                var parents = new Dictionary<int, int>();
                foreach (int e in _links.Entities())
                {
                    IntPtr link = _links.Item(e);
                    if (link == IntPtr.Zero) continue;
                    int parent = Parent(link);
                    if (parent < 0 || _visuals.Has(parent)) continue;
                    bool request = _effects.Has(e);
                    if (request && (_visuals.Has(e) || _spawns.Has(e) || _despawns.Has(e))) continue;
                    _worldObject.DelEntity(e, true);
                    if (request) requests++; else halfBuilt++;
                    parents[parent] = parents.TryGetValue(parent, out int n) ? n + 1 : 1;
                }
                if (halfBuilt + requests == 0) return;
                Plugin.Logger.LogInfo($"[Effects] {halfBuilt} half-built visual effect(s) and {requests} saved effect request(s) removed, "
                    + "attached to an entity without a visual: " + string.Join(", ", parents.Select(kv => $"{Describe(kv.Key)}: {kv.Value}")));
            }
            catch (Exception e)
            {
                Fail("removing half-built visual effects", e);
            }
        }
    }

    [HarmonyPatch(typeof(SimpleVisualObjectEventSystem), nameof(SimpleVisualObjectEventSystem.ManageSpawnEvent))]
    internal static class EffectSpawnGuard
    {
        private static bool Prepare() => Settings.EffectParentGuard.Value;

        private static bool Prefix(SimpleVisualObjectEventSystem __instance, int __0)
            => __instance == null || EffectParents.AllowSpawn(__instance.Pointer, __0);
    }

    [HarmonyPatch(typeof(SimpleVisualObjectUtility), nameof(SimpleVisualObjectUtility.InstantiateLoadedEntity))]
    internal static class EffectLoadGuard
    {
        private static bool Prepare() => Settings.EffectParentGuard.Value;

        private static bool Prefix(SimpleVisualObjectUtility __instance, int __0)
            => __instance == null || EffectParents.AllowLoad(__instance.Pointer, __0);
    }

    [HarmonyPatch(typeof(SimpleVisualObjectEventSystem), nameof(SimpleVisualObjectEventSystem.Run))]
    internal static class EffectCleanup
    {
        private static bool Prepare() => Settings.EffectParentGuard.Value;

        private static void Prefix(SimpleVisualObjectEventSystem __instance)
        {
            if (__instance != null) EffectParents.CleanOnce(__instance.Pointer);
        }
    }
}
