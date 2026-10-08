using BepInEx.Configuration;

namespace NDMUnofficialPatch
{
    // All switches of the patch, stored in BepInEx\config\ndm.unofficialpatch.cfg.
    // Each Harmony patch class checks its switch in Prepare(); a switch turned off means the hook is never installed.
    internal static class Settings
    {
        internal static ConfigEntry<bool> DiagnosticsTutorial;
        internal static ConfigEntry<bool> DiagnosticsInput;
        internal static ConfigEntry<bool> DiagnosticsEvents;
        internal static ConfigEntry<bool> DiagnosticsCensus;
        internal static ConfigEntry<bool> DiagnosticsMorale;
        internal static ConfigEntry<bool> DiagnosticsUnreachable;
        internal static ConfigEntry<bool> TutorialEscapeHatch;
        internal static ConfigEntry<bool> SaveBackups;
        internal static ConfigEntry<int> SaveBackupsPerFile;
        internal static ConfigEntry<bool> TavernWatchdog;
        internal static ConfigEntry<int> TavernWatchdogInterval;
        internal static ConfigEntry<SafetyNet.AttackWatchdogMode> AttackWatchdog;
        internal static ConfigEntry<int> AttackWatchdogMinutes;
        internal static ConfigEntry<SafetyNet.ConstructionWatchdogMode> ConstructionWatchdog;
        internal static ConfigEntry<int> ConstructionWatchdogMinutes;
        internal static ConfigEntry<bool> CharacterManager;
        internal static ConfigEntry<bool> RecruitTargets;
        internal static ConfigEntry<bool> RoomNeedsPanel;
        internal static ConfigEntry<bool> MinionColumns;
        internal static ConfigEntry<string> MinionColumnGroups;
        internal static ConfigEntry<bool> ClickThroughGuard;
        internal static ConfigEntry<bool> GameEventSystem;
        internal static ConfigEntry<bool> FurnitureToolSwitch;
        internal static ConfigEntry<bool> UnreachableStart;
        internal static ConfigEntry<bool> CombatStrength;
        internal static ConfigEntry<int> CleaningRadius;
        internal static ConfigEntry<bool> HealDeadlock;
        internal static ConfigEntry<bool> EffectParentGuard;
        internal static ConfigEntry<bool> TrapTriggers;
        internal static ConfigEntry<int> MaximumMinions;
        internal static ConfigEntry<bool> GolbarghLairOnly;
        internal static ConfigEntry<bool> UndeadNoDecay;
        internal static ConfigEntry<bool> NecromancersAndCultistsHeal;
        internal static ConfigEntry<bool> ResourceBar;
        internal static ConfigEntry<bool> MinimumSalaries;
        internal static ConfigEntry<bool> Bilan;
        internal static ConfigEntry<bool> StorageCapacity;
        internal static ConfigEntry<float> StorageCapacityFactor;
        internal static ConfigEntry<int> ResourceMaximum;
        internal static ConfigEntry<int> InsertedFloors;
        internal static ConfigEntry<int> UniqueRaidFactionChance;

        internal static void Bind(ConfigFile cfg)
        {
            DiagnosticsTutorial = cfg.Bind("Diagnostics", "Tutorial", true,
                "Log every tutorial step shown, activated or validated, and every UI target a step cannot find, to trace tutorial steps that lock the interface.");
            DiagnosticsInput = cfg.Bind("Diagnostics", "InputMode", true,
                "Log every switch to or from gamepad mode with the device that caused it, to trace tutorial steps that lock the interface.");
            DiagnosticsEvents = cfg.Bind("Diagnostics", "Events", true,
                "Log barmen hired and sent out, attack alerts starting and ending, save files written and loaded.");
            DiagnosticsCensus = cfg.Bind("Diagnostics", "Census", true,
                "Once a minute of real time, log a few counts read from the game world: entities, tavern customers, barmen, rooms under construction.");
            DiagnosticsMorale = cfg.Bind("Diagnostics", "Morale", true,
                "20 seconds after a save is loaded, then every 5 minutes of real time, log each minion's morale and needs with their current step, the states lowering his morale, a count of those states over all minions, and how the minions' room searches ended. Also log the gauges' steps once per game, and each minion who resigns with the same details. Every 5 seconds, a line for each minion whose morale falls fast, with the behaviour he runs, the best-scored ones, his states and his recent room searches.");
            DiagnosticsUnreachable = cfg.Bind("Diagnostics", "UnreachableTiles", true,
                "When the builder finds squares it cannot reach, log them by zone with the entities around each zone and their components.");
            TutorialEscapeHatch = cfg.Bind("SafetyNet", "TutorialEscapeHatch", true,
                "While a tutorial step is on screen: Ctrl+Shift+F9 gives all inputs back and hides the black mask; Ctrl+Shift+F10 validates the step.");
            SaveBackups = cfg.Bind("SafetyNet", "SaveBackups", true,
                "Before the game overwrites a save file, copy it to AppData\\LocalLow\\Artefacts Studio\\NDM\\NDMUnofficialPatch-backups.");
            SaveBackupsPerFile = cfg.Bind("SafetyNet", "SaveBackupsPerFile", 10,
                new ConfigDescription("Number of backup copies kept for each save file; older copies are deleted.", new AcceptableValueRange<int>(1, 100)));
            TavernWatchdog = cfg.Bind("SafetyNet", "TavernWatchdog", true,
                "Taverns left without barmen. A tavern counter with fewer barmen than it requires, on two checks in a row, gets the game's own hiring routine called for it. The game hires nobody unless the tavern is accessible.");
            TavernWatchdogInterval = cfg.Bind("SafetyNet", "TavernWatchdogIntervalSeconds", 30,
                new ConfigDescription("Seconds of game time between two tavern checks.", new AcceptableValueRange<int>(10, 600)));
            AttackWatchdog = cfg.Bind("SafetyNet", "AttackWatchdog", SafetyNet.AttackWatchdogMode.End,
                "Attacks that never end. An enemy group whose fight has not changed (members, prisoners, allies targeting it) and in which nobody has lost life points for AttackWatchdogMinutes. Report: describe it in the log. End: describe it, then end its alert the way the game does when a fight is over. Off: do nothing.");
            AttackWatchdogMinutes = cfg.Bind("SafetyNet", "AttackWatchdogMinutes", 5,
                new ConfigDescription("Minutes of game time without any change before an enemy group counts as stuck.", new AcceptableValueRange<int>(1, 60)));
            ConstructionWatchdog = cfg.Bind("SafetyNet", "ConstructionWatchdog", SafetyNet.ConstructionWatchdogMode.Finish,
                "Constructions that never finish. Report: log rooms under construction whose worker tasks make no progress, or which have no worker task left, for ConstructionWatchdogMinutes. Finish: also run the game's own completion check on a room with no worker task left (never while build mode is open). Off: do nothing.");
            ConstructionWatchdogMinutes = cfg.Bind("SafetyNet", "ConstructionWatchdogMinutes", 3,
                new ConfigDescription("Minutes of game time without change before a room under construction is reported or checked.", new AcceptableValueRange<int>(1, 60)));
            ClickThroughGuard = cfg.Bind("Fixes", "ClickThroughGuard", true,
                "Clicks through the interface. A click whose press or release happens over the interface (buttons, panels) never selects the room or character behind it. With a gamepad the game's own rules apply.");
            GameEventSystem = cfg.Bind("Fixes", "GameEventSystem", true,
                "The game keeps the first event system it finds and asks it whether the mouse is over the interface. With UnityExplorer installed that is UnityExplorer's, which never sees the game's interface, so clicks on menus also reach the dungeon and the builder. The patch points the game back to its own event system.");
            FurnitureToolSwitch = cfg.Bind("Fixes", "FurnitureToolSwitch", true,
                "A click on a furniture icon while adding or removing walls, or deleting, switches the builder to furniture. When the game refuses (walls not valid, room on another floor, a wall being drawn), the patch cancels the wall being drawn or shows the room's floor and tries again; if the walls still cannot be validated, the popup's buttons go back to the tool really in use. Every refusal is logged with its reason.");
            UnreachableStart = cfg.Bind("Fixes", "UnreachableStart", true,
                "When the builder's accessibility check comes back with every walkable square of the floor marked because its walk from the stairs did not start, redo that walk from the same stairs, so that squares those stairs can reach are no longer called not accessible.");
            HealDeadlock = cfg.Bind("Fixes", "HealDeadlock", true,
                "When every pharmagician is waiting in bed to be healed, the one with the lowest id gets up and goes back to work, healing the others, until another pharmagician is up and not wounded; he then lies down in his turn. In the game's rule a pharmagician waiting in bed still counts as present, so wounded minions and pharmagicians can all wait with nobody left to heal them.");
            EffectParentGuard = cfg.Bind("Fixes", "EffectParentGuard", true,
                "Skips a visual effect that a character's animation attaches to an entity without a visual, and removes, when a save loads, the effects such a request left half-built. In the game, an effect attached to the target of a character's action goes to the game's state entity when that action has no target recorded; building it then fails on every frame, and every game system that runs after it stops, builders included, while area-of-effect events pile up in the save.");
            TrapTriggers = cfg.Bind("Balance", "TrapTriggers", true,
                "A loaded trap always goes off when an adventurer steps on it, deceiving traps included, and never when one of the player's minions does. In the game, a trap goes off under an adventurer 35 % of the time (deceiving traps only for the origins they target), and under a minion 1 %, plus 5 % with the Insouciant trait, plus a second 2 % for elves. The trap panel still shows the game's percentage.");
            CleaningRadius = cfg.Bind("Balance", "CleaningRadius", 2,
                new ConfigDescription("When a domestic finishes a cleaning stop, every square of the same room within this many squares of the stop is cleaned too: 2 gives a 5 by 5 patch, where the game cleans 5 squares per stop. 0 leaves the game's cleaning unchanged.", new AcceptableValueRange<int>(0, 6)));
            MaximumMinions = cfg.Bind("Balance", "MaximumMinions", 500,
                new ConfigDescription("The dungeon holds up to this many minions, or the game's own maximum when that is higher: in the game, the sum of the minion places of the unlocked floors. Recruitment and guard lockers check this maximum. 0 leaves the game's maximum unchanged.", new AcceptableValueRange<int>(0, 10000)));
            GolbarghLairOnly = cfg.Bind("Balance", "GolbarghLairOnly", true,
                "The Golbargh's patience falls faster only for the people who stand in his lair, not for everyone on his floor: in the game, 6, 12 and 18 minions and adventurers on his floor (Zangdar and Reivax excepted) make it fall faster. Deaths follow the game's rule: each death on his floor refills his patience. His patience still falls by itself, and the death of one of his demons still lowers it wherever it happens.");
            UndeadNoDecay = cfg.Bind("Balance", "UndeadNoDecay", true,
                "Undead (skeletons, zombies, ghosts) lose life only when they are harmed: hits, and states such as poison or burning. In the game, every undead also loses life by itself until he dies, 0.022 life points a second when the compost store is 84 to 100 % full and up to 0.067 when it is below 17 %. The undead's rule text still says that more compost makes him last longer. Demons have no such loss in the game and are not concerned.");
            NecromancersAndCultistsHeal = cfg.Bind("Balance", "NecromancersAndCultistsHeal", true,
                "Necromancers heal undead and vampires, and cultists heal their own demons, outside fights. A necromancer or a cultist tends one wounded patient at a time on his floor within 5 squares; after 10 seconds together, the patient gets the result of a pharmagician's heal: full life, and bleeding, poison, curses and the other healable states removed. Necromancers also take up the pharmagicians' heal for vampires lying in an infirmary bed, walking to the bed as a pharmagician does; for this the patch gives each necromancer the three heal components of the pharmagician job, which are kept in the save. With this setting off, the patch removes them from the necromancers when a save is loaded. In the game, undead and demons are never healed, and only pharmagicians heal.");
            CombatStrength = cfg.Bind("Balance", "CombatStrength", true,
                "The attack, defense and life points of every job that fights (attack above 0 at grade 10: guards, spies, sorcerers, pharmagicians, necromancers, cultists, demons, undead) grow with grade so that grade 10 matches the strongest adventurer class at its top level; grade 1 keeps the game's values and the boost grows evenly up to grade 10. Unique characters are left alone. The game's tables are changed in memory only. Replaces GuardStrength of plugin 0.12.0 to 0.17.0, which no longer has an effect.");
            ResourceBar = cfg.Bind("Economy", "ResourceBar", true,
                "A bar at the top of the dungeon screen: an icon per workshop resource (weapons, magic, tools, intel, corpses) and per food type, and under each one what was produced / consumed over the last decade of game time.");
            MinimumSalaries = cfg.Bind("Economy", "MinimumSalaries", true,
                "Every minion's salary is the lowest the game's formula gives for his origin, job and grade: the random part of the salary, drawn at recruitment, is set to its most favourable value. Unique characters keep their configured salary. Turned off, salaries keep their lowered value until the game recomputes them at the next grade change.");
            Bilan = cfg.Bind("Economy", "Bilan", true,
                "In the resource counter at the bottom right, right of Dépenses: gold earned over the last decade of game time minus Dépenses (salaries, tavern costs, floor costs). Construction refunds, furniture sales and the starting gold do not count as earnings.");
            StorageCapacity = cfg.Bind("Economy", "StorageCapacity", true,
                "Storage furniture holds StorageCapacityFactor times its normal capacity: shelves and other stores of weapons, magic, tools, intel and corpses, and food stores. A workshop resource stays below the maximum of its gauge (see ResourceMaximum). Food capacities are recounted from the storage furniture when a save is loaded, so a factor of 1 gives a save back its normal food capacities.");
            StorageCapacityFactor = cfg.Bind("Economy", "StorageCapacityFactor", 10f,
                new ConfigDescription("Multiplier applied to the capacity of every storage furniture.", new AcceptableValueRange<float>(1f, 100f)));
            ResourceMaximum = cfg.Bind("Economy", "ResourceMaximum", 10000,
                new ConfigDescription("Maximum stock of each workshop resource (weapons, magic, tools, intel, corpses), 1000 in the game. The gauge steps (weapon grade, raid reach of intel, undead damage of corpses) keep the stock they start at in the game: grade 6 weapons still from 830, and the top step now runs up to the new maximum, so a stock above 1000 only adds storage. The stock is still limited by the storage furniture (see StorageCapacity). 0, or a value below the game's maximum, leaves the game's maximum unchanged.", new AcceptableValueRange<int>(0, 100000)));
            MinionColumns = cfg.Bind("Management", "MinionColumns", true,
                "The management tab of the Minions window, widened and laid out in one column per group of origins (MinionColumnGroups), plus Others when a minion fits no group. The other tabs keep the game's layout.");
            MinionColumnGroups = cfg.Bind("Management", "MinionColumnGroups", "Greenskins:ORC,GOBLIN,TROLL;Humans:HUMAN,DWARF,BARBARIAN;Elves:ELF;Drows:DROW,VAMPIRE",
                "Columns of the Minions window, left to right, as Name:ORIGIN,ORIGIN separated by semicolons. Colours follow the order: green, blue, pink, black. Origins: HUMAN, ORC, SKELETON, DWARF, ELF, GOBLIN, DROW, ZOMBIE, BARBARIAN, TROLL, DEMON, VAMPIRE.");
            RoomNeedsPanel = cfg.Bind("Management", "RoomNeedsPanel", true,
                "A tab at the top left of the dungeon screen opens a panel with one row per room type the minions look for. Up to three segments name the kinds of prop they looked for there in the last 3 minutes, with how many found a free one over how many looked (green all, amber some, red none); the status names the minions whose last search there found nothing. A bar shows the prestige level preferred by the most demanding minion, with a mark at the best room; it is information only. Clicking a segment or the bar shows the minion concerned.");
            RecruitTargets = cfg.Bind("Management", "RecruitTargets", true,
                "In the window of a recruitment candidate, a row Keep - N + beside the Recruit button sets how many minions of the candidate's job and origin the dungeon keeps (Shift+click: 5 at a time). Every 3 seconds of game time, while a job and origin has fewer minions than its target, the patch recruits one listed candidate of that job and origin, as the Recruit button does. A target is a minimum: nobody is fired. Recruiting waits while the dungeon is full or the list has no such candidate. Guards and barmen are hired by the game for guard lockers and tavern counters and are not in the list. Targets are saved with each save.");
            CharacterManager = cfg.Bind("Management", "CharacterManager", true,
                "A third page, Assigned rooms, in the character sheet of the Minions window: a job room, dormitory, bathroom, canteen and break room per character (unique ones excluded). Assignments are preferences: the game's choice stands when the room cannot serve.");
            InsertedFloors = cfg.Bind("Floors", "InsertedFloors", 5,
                new ConfigDescription("Copies of floor 4 inserted between floor 4 and the tavern floor in every new game (campaign or sandbox); 0 turns it off. With 5, floors 5 to 9 are copies of floor 4 and the tavern floor and the roof floor become 10 and 11, raised with the roof, the balloons and the bridges. The inserted floors unlock with floor 4 and each costs floor 4's upkeep. A save made with inserted floors is marked (NDMUnofficialPatch-data\\<save>.floors.txt) with their number and always loads with them, whatever this setting says; saves without the marker load with seven floors. Replaces InsertTestFloor of plugins 0.21 and 0.22.",
                    new AcceptableValueRange<int>(0, Floors.FloorInsertion.MaxInserted)));
            UniqueRaidFactionChance = cfg.Bind("Raids", "UniqueRaidFactionChance", 90,
                new ConfigDescription("While a unique raid not yet finished waits for raids against a faction (in the campaign, Frappe préventive needs five raids won against the barbarians and opens their chain of unique raids), each new ordinary raid the game places on the raid map goes, this percent of the time, to a free location of that faction instead of the one the game drew. Only the locations the game itself lists for that raid are used: free, within reach, accepting the raid's mission type, and never one reserved for a unique raid. A raid the game already placed with that faction stays where it is. Unique raids still appear as in the game, as soon as their conditions are met. 0 turns it off.",
                    new AcceptableValueRange<int>(0, 100)));
        }
    }
}
