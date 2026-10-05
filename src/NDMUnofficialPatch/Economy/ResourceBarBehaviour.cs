using System;
using UnityEngine;

namespace NDMUnofficialPatch.Economy
{
    // A component added by the plugin to its own object, which drives the resource bar, the Bilan, the resource
    // maximums and the storage capacity every frame.
    public sealed class ResourceBarBehaviour : MonoBehaviour
    {
        private bool _errorLogged;

        public ResourceBarBehaviour(IntPtr pointer) : base(pointer) { }

        public void Update()
        {
            // The depth counters only matter during a call of the game; between frames they are zero.
            ResourceFlows.ProductionDepth = 0;
            ResourceFlows.CapacityDepth = 0;
            Bilan.ExcludedDepth = 0;
            ResourceMaximum.Update(); // also when off: it then gives a save the game's maximums back
            if (Settings.StorageCapacity.Value) StorageCapacity.Update();
            if (Settings.Bilan.Value) Bilan.Update();
            if (!Settings.ResourceBar.Value) return;
            try { ResourceBarOverlay.Update(); }
            catch (Exception e)
            {
                if (_errorLogged) return;
                _errorLogged = true;
                Plugin.Logger.LogWarning($"[ResourceBar] update failed: {e.Message}");
            }
        }
    }
}
