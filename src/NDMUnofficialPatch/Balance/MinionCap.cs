using System;
using HarmonyLib;
using NDMUnofficialPatch.Common;
using NDMUnofficialPatch.Management;
using UnityEngine;

namespace NDMUnofficialPatch.Balance
{
    // A larger maximum number of minions.
    //
    // Game 1.8, from the method bodies, read on 2 October 2026. The maximum lives in MinionManagerComponent
    // (MaximumNumberOfMinions), a single component of the world. MinionUtility.SetMaximumNumberOfMinions sets it to the
    // sum of the minion places of every unlocked floor, and IncrementMaximumNumberOfMinions(floor) adds one floor's
    // places to it (called when a game starts, GameInitSystem.Init, and when a floor is unlocked,
    // FloorUtility.UnlockFloor). MinionUtility.HasEnoughPlaceForMinions, which the recruitment buttons, the construction
    // of guard lockers and the game's conditions ask, compares the current count, plus the places taken by guard
    // lockers, plus the minions to add, with that maximum.
    //
    // The patch keeps the game's own maximum aside and writes the larger of it and Balance.MaximumMinions into the
    // component. Before an increment it puts the game's value back, so the game adds the floor to its own value, and
    // after the increment or a full recompute it records the new game value and writes the larger one again. When a
    // world appears, it runs SetMaximumNumberOfMinions once, so the game's value is recomputed from the unlocked floors
    // even when the save holds a value the patch wrote. The value the patch writes is saved with the game (the component
    // has a save formatter), so with the setting at 0 the patch still runs that recompute once per world, which gives a
    // save played with a larger maximum the game's own back.
    internal static unsafe class MinionCap
    {
        private static int _gameMax = -1, _written = -1;
        private static IntPtr _world;
        private static float _next;
        private static bool _errorLogged;

        private static int Target => Math.Max(0, Settings.MaximumMinions.Value);

        private static void ReportError(string what, Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Minions] {what} failed, further errors are not logged: {e.Message}");
        }

        private static IntPtr Item()
        {
            if (!GameContext.TryWorld(out var world, out _)) return IntPtr.Zero;
            Il2CppRaw.ExpectValueFieldOffset<MinionManagerComponent>("MaximumNumberOfMinions", 0);
            var pool = RawPool.Of<MinionManagerComponent>(world, -1);
            int entity = pool.FirstEntity();
            return entity < 0 ? IntPtr.Zero : pool.Item(entity);
        }

        // Every frame, from FixesBehaviour.
        internal static void Update()
        {
            if (!GameContext.Ready || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                if (GameContext.WorldPointer != _world)
                {
                    var minions = GameContext.Minions;
                    // The utility of the previous world stays captured until its systems stop running.
                    if (Il2CppRaw.ReadPointer(minions.Pointer, "m_world") != GameContext.WorldPointer) return;
                    IntPtr item = Item();
                    if (item == IntPtr.Zero) return;
                    _world = GameContext.WorldPointer;
                    _gameMax = -1;
                    _written = -1;
                    int saved = *(int*)item;
                    minions.SetMaximumNumberOfMinions(); // the postfix, when installed, records the game's value and writes the target
                    if (Target > 0)
                        Plugin.Logger.LogInfo($"[Minions] the save held a maximum of {saved}; the game's maximum from its unlocked floors is {_gameMax}; maximum now {*(int*)item}");
                    else if (saved != *(int*)item)
                        Plugin.Logger.LogInfo($"[Minions] MaximumMinions is 0: the save's maximum of {saved} set back to the game's {*(int*)item}, from its unlocked floors");
                    return;
                }
                if (Target > 0) Enforce();
            }
            catch (Exception e) { ReportError("setting the maximum number of minions", e); }
        }

        private static void Enforce()
        {
            IntPtr item = Item();
            if (item == IntPtr.Zero) return;
            int current = *(int*)item;
            if (current != _written) _gameMax = current; // the game changed it since the patch last wrote
            int want = Math.Max(_gameMax, Target);
            if (current != want)
            {
                *(int*)item = want;
                Plugin.Logger.LogInfo($"[Minions] maximum {current} -> {want} (the game's own: {_gameMax})");
            }
            _written = want;
        }

        // Whether the utility the game is calling belongs to the world the patch follows. While a save loads, the game
        // initialises the new world's floors before the patch has seen that world; the patch then waits for its own
        // recompute (Update) rather than writing into either world.
        private static bool Followed(IntPtr utility) =>
            _world != IntPtr.Zero && _world == GameContext.WorldPointer && Il2CppRaw.ReadPointer(utility, "m_world") == _world;

        internal static void BeforeIncrement(IntPtr utility)
        {
            try
            {
                if (!Followed(utility)) return;
                IntPtr item = Item();
                if (item != IntPtr.Zero && _gameMax >= 0 && *(int*)item == _written) *(int*)item = _gameMax;
            }
            catch (Exception e) { ReportError("restoring the game's maximum before a floor is added", e); }
        }

        internal static void AfterGameChange(IntPtr utility)
        {
            try
            {
                if (!Followed(utility)) return;
                IntPtr item = Item();
                if (item == IntPtr.Zero) return;
                _gameMax = *(int*)item;
                _written = -1;
                Enforce();
            }
            catch (Exception e) { ReportError("setting the maximum after the game changed it", e); }
        }
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.SetMaximumNumberOfMinions))]
    internal static class MinionCapSetPatch
    {
        private static bool Prepare() => Settings.MaximumMinions.Value > 0;
        private static void Postfix(MinionUtility __instance) => MinionCap.AfterGameChange(__instance.Pointer);
    }

    [HarmonyPatch(typeof(MinionUtility), nameof(MinionUtility.IncrementMaximumNumberOfMinions))]
    internal static class MinionCapIncrementPatch
    {
        private static bool Prepare() => Settings.MaximumMinions.Value > 0;
        private static void Prefix(MinionUtility __instance) => MinionCap.BeforeIncrement(__instance.Pointer);
        private static void Postfix(MinionUtility __instance) => MinionCap.AfterGameChange(__instance.Pointer);
    }
}
