# Game event system

Plugin 0.12.0, 29 September 2026, game 1.8. Setting `Fixes.GameEventSystem` (on by default).

## The problem

`Aube.PlayerInputs` keeps in `m_eventSystem` the first event system it is given: `PlayerInputs.get_EventSystem` fills the field once, from `EventSystem.current` or the first event system found, and never asks again. `PlayerInputs.IsMouseOnUi` asks that event system whether the mouse is over the interface. The construction page and the furniture popup (`ConstructionPage.Update`, `ConstructionPropPopup.Update`) use the answer to tell a click on the interface from a click for the builder, and the flag `m_isOnUi`, which `ObservationController` reads before selecting what is under the mouse, comes from the same event system.

With UnityExplorer installed, its library UniverseLib creates an event system at start-up and the game keeps that one. The click guard's log of 28 September names it: the game kept `UniverseLibCanvas` while the current event system was `UI`. UniverseLib's event system does not handle the game's interface, so the answer is always "not over the interface", and clicks on menus also reach the dungeon (problem G16) and the builder. Players without UnityExplorer keep the game's own event system and do not have this problem.

## The fix

`Fixes/GameEventSystem.cs`, once a second of real time: when the event system kept by `PlayerInputs` differs from the current one, is gone, or belongs to UniverseLib or UnityExplorer (its object or a parent is named after either), and the current one belongs to neither, `m_eventSystem` is set to the current one and the change is logged under `[EventSystem]`. Without UnityExplorer nothing is done. The click guard of 0.4.0 stays in place.
