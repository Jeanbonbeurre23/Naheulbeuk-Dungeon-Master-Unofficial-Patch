using System;
using UnityEngine;

namespace NDMUnofficialPatch.Management
{
    // A component added by the plugin to its own object. Every frame it keeps the Minions window's origin columns
    // in step (MinionColumns), lets the morale probe write its reports (Diagnostics/MoraleProbe.cs), drives the room-needs panel and the recruitment targets and completes a camera move to a room on another floor; every two seconds of real time it applies assignments read from a save and refreshes the rooms page of
    // the character sheet; after each frame's animations it keeps the sheet's own page hidden under the rooms page
    // while that page is shown.
    public sealed class ManagerBehaviour : MonoBehaviour
    {
        private float _next;
        private bool _errorLogged;

        public ManagerBehaviour(IntPtr pointer) : base(pointer) { }

        public void Update()
        {
            try
            {
                if (Settings.MinionColumns.Value) MinionColumns.Update();
                if (Settings.DiagnosticsMorale.Value)
                {
                    Diagnostics.MoraleProbe.Update();
                    Diagnostics.MoraleWatch.Update();
                }
                if (Settings.RoomNeedsPanel.Value) RoomNeedsPanel.Update();
                if (Settings.RecruitTargets.Value) RecruitTargets.Update();
                if (!Settings.CharacterManager.Value) return;
                RoomPicker.Update();
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 2f;
                CharacterManager.Tick();
                RoomsPage.TickAll();
            }
            catch (Exception e) { LogOnce(e); }
        }

        public void LateUpdate()
        {
            try
            {
                if (Settings.MinionColumns.Value) MinionColumns.LateUpdate();
                if (Settings.CharacterManager.Value) RoomsPage.LateUpdateAll();
            }
            catch (Exception e) { LogOnce(e); }
        }

        private void LogOnce(Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogWarning($"[Manager] update failed: {e.Message}");
        }
    }
}
