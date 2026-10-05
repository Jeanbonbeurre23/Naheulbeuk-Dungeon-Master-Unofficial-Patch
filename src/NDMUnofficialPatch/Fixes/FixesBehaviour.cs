using System;
using UnityEngine;

namespace NDMUnofficialPatch.Fixes
{
    // A component added by the plugin to its own object, which drives the event-system repair, the second try of the
    // furniture tool switch, the combat strength, the maximum number of minions, and the late pass of the floor insertion every frame.
    public sealed class FixesBehaviour : MonoBehaviour
    {
        private bool _errorLogged;

        public FixesBehaviour(IntPtr pointer) : base(pointer) { }

        public void Update()
        {
            try
            {
                if (Settings.GameEventSystem.Value) GameEventSystem.Update();
                if (Settings.FurnitureToolSwitch.Value) FurnitureToolSwitch.Update();
                if (Settings.CombatStrength.Value) Balance.CombatStrength.Update();
                Balance.MinionCap.Update(); // also when off: it then gives a save the game's maximum back
                Floors.FloorInsertion.Update();
            }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[Fixes] update failed: {e.Message}");
            }
        }
    }
}
