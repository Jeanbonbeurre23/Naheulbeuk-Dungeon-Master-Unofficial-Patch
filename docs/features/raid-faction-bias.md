# Raid faction bias

Plugin 0.24.0, 3 October 2026, game 1.8. Setting `Raids.UniqueRaidFactionChance` (90 by default, 0 turns it off).

## What it does

While a unique raid not yet finished waits for raids against a faction, each new ordinary raid the game places on the raid map goes, 90 % of the time, to a free location of that faction instead of the location the game drew. It was added on 3 October 2026 for a campaign in which the barbarian chain of unique raids, which opens with "Frappe préventive" and ends with "Le secret de l'acier 2", stayed out of reach. "Frappe préventive" appears only after five raids won against the barbarians, the campaign counted four, and ordinary raids seldom landed in barbarian territory.

## The game's rule

Read from the game's code, the configs bundle and a campaign save on 3 October 2026.

Unique raids are not drawn. `UpdateRaidsDisplaySystem.FillDisplayableUniqueRaidEntities` lists every unique raid whose first condition list is valid, and `Run` displays each one at its own location, one of the locations tagged `ReservedForUniqueRaidTagComponent`. A condition entity names its unique raid in `ConditionRootComponent.RootEntity`. In the save, 54 of the 60 unique raids are finished. The six left are the barbarian chain and "Pour Crôm !":

| Unique raid | Condition | Reward |
|---|---|---|
| Frappe préventive | 5 raids won against the barbarians (4 in the save) | gold, influence |
| Le secret de l'acier | Frappe préventive won | Râtelier d'armes d'entraînement de mêlée |
| La horde sauvage | Le secret de l'acier won | Squelette d'Ouklaf |
| Les invasions barbares | La horde sauvage won | Réserve de nourriture |
| Le secret de l'acier 2 | Les invasions barbares won | relic Collier du poing de Crôm |
| Pour Crôm ! | a room, and 5 Cellule pour Barbares built (4 in the save) | gold; the barbarians then ask to join |

Ordinary raids are drawn. For each generated raid not yet displayed, `Run` calls `CanDisplayRaid`, which calls `TrySelectPositionOnMap(raid)`. That method fills `m_availableRaidLocations` through `RaidsUtility.FillPossibleRaidLocations(ref list, raid, ignoreUsedLocation: false)`, which keeps the map's locations that are not reserved for a unique raid, not used by a displayed raid, within the map's visible radius (`MapCoordinatesComponent.FakeDistanceToDungeon` against a value of the raids manager), and whose allowed raid types include the raid's mission type. It then draws one of them uniformly with the world's random state and writes it to the raid's `RaidLocationComponent`.

A raid's faction follows its location. `RaidsUtility.GetFactionType(raid)` reads the location's faction zone and returns `RaidsConfig.GetFactionTypeFromFactionZone` of it, and `UpdateConditionStatusSystem.GetRaidsOfFactionFinishedTriggerCount` counts a finished raid for a faction condition when `GetFactionType(raid)` equals the condition's faction.

The map of the save has 102 locations, 10 of them barbarian. Six of those are reserved for the six unique raids above. The four others (clans Raggak, Hyènes, Grosgourdin and Ruchka) accept bounty hunts, looting, mercenary and service missions. Three of the displayed raids of the save sit at distance 600, the farthest on the map, so every location is within reach. None of the 10 raids displayed in the save was barbarian.

## How the patch works

`Balance/RaidFactionBias.cs` has a postfix on `UpdateRaidsDisplaySystem.TrySelectPositionOnMap`, which runs when the game has placed a raid. It lists the faction conditions that are active, not valid and never validated, whose root is a unique raid without `FinishedRaidTagComponent`. If there is none, or if the game's location already belongs to one of those factions, nothing changes. Otherwise, 90 % of the time, the raid goes to a location drawn uniformly among those the game had just listed for this raid that belong to one of those factions.

Three choices follow from the game's rule. The hook acts after the game's placement, so that every filter of the game still applies: the new location is free, within reach, accepts the raid's mission type, and is never reserved for a unique raid. The faction of a location is asked of `GetFactionType` itself, by writing the location into the raid's component for the call and putting the game's value back, because the victory counter uses that same function; the answer is cached per faction zone. The raid keeps everything else the game generated for it.

## Log

`[Raids]` when the set of waiting unique raids changes, with the faction, the unique raid's entity and its progress, for example `BARBARIANS (unique raid 225 at 4/5)`. Then one line per ordinary raid placed while some unique raid waits: kept where the game put it, left there by the draw, left there because no free location of the faction accepts its mission type, or moved, with both locations and their factions. An error is logged once, and the game's location then stands.

## Limits

Only raids of a mission type that the faction's free locations accept can be moved, so for the barbarians bounty hunts, looting, mercenary and service missions, and at most four barbarian raids can be on the map at once. The patch changes where new raids go, not how many the game displays or when, so its effect shows as displayed raids are finished, expire, or are rerolled. Whether the paid reroll places its new raids through `TrySelectPositionOnMap` was not read; the log will show it. Whether a raid's duration depends on its location's distance, and is computed before or after this choice, was not read either. The raid still has to be won for the condition to count it.
