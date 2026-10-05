# Effect parent guard

Plugin 0.24.3, 5 October 2026, game 1.8. Setting `Fixes.EffectParentGuard` (on by default).

## The problem

A character's animation can ask for a visual effect attached to another entity. When the animation's target is the character's action target and that action has no target recorded, the game attaches the effect to entity 0, its state entity, which has no visual. Building the effect then throws `NullReferenceException` in `SimpleVisualObjectEventSystem` on every frame, because the request that throws is never deleted, and every system that runs after that one stops: builders, construction checks, the clearing of area-of-effect events and more. Each frame also leaves one half-built effect entity in the world. In a save of 5 October 2026 this lasted three minutes and left 6,738 half-built effects and 1,204,542 area-of-effect events in the save.

## What the patch does

Before the game builds a requested effect, the patch looks at the entity it is attached to. When that entity exists but has no visual object, the request is skipped, and the game deletes it with the others at the end of the frame. An effect whose parent no longer exists is left to the game, which logs that case and goes on without an exception.

When a save loads, the patch skips any saved effect that has no visual of its own (a request saved before the game handled it) and any half-built effect attached to an entity without a visual, and before the first effects of the session are handled it removes the effects left by a skipped or failed request: half-built effects and saved requests attached to an entity without a visual. Effects attached to characters, furniture and other entities with a visual are left alone.

## How it is built

`Fixes/EffectParentGuard.cs` has three hooks. A prefix on `SimpleVisualObjectEventSystem.ManageSpawnEvent` reads the request's `ParentLinkSimpleVisualObjectComponent`, resolves its `ParentEntity`, and returns false when the parent exists without a `VisualComponent` whose `Instance` is a live `GameObject`. A prefix on `SimpleVisualObjectUtility.InstantiateLoadedEntity` returns false for an effect entity without a `VisualComponent`, and for one without `SimpleVisualObjectComponent` whose parent exists without a `VisualComponent`. A prefix on `SimpleVisualObjectEventSystem.Run` runs once per world: it goes through the entities with a parent link, and deletes with `EcsWorld.DelEntity` those whose parent exists without a `VisualComponent`, when they have no `SimpleVisualObjectComponent` (half-built) or when they have one but no visual of their own and no spawn or despawn event (saved requests). The game's own save check, `SaveCompatibilityCheckerSystem.CheckParentSimpleVisualObjects`, deletes effect entities whose parent no longer exists with the same call. Component layouts are checked against the runtime's metadata before any read; if a check or a read fails, the guard turns itself off for the session and logs why.

## Log

`[Effects] a visual effect asked for by <character> was not built: its parent, <entity>, has no visual. Effects skipped this session: N` for the first ten skips, then at 100, 1,000 and every 10,000. After a load, `[Effects] at load, N visual effect(s) not built: ...` and `[Effects] N half-built visual effect(s) and M saved effect request(s) removed, attached to an entity without a visual: <entity>: count`.

## Limits

The patch does not change why an action loses its target while its animation plays; that was not read in the code. The area-of-effect events and other events that piled up while the systems were stopped stay in a save made during that time, and the game handles them once its systems run again. Other ways of building an effect (projectiles, areas of effect) are not guarded.
