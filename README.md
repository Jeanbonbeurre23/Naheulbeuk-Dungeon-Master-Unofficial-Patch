# NDM Unofficial Patch

An unofficial patch for *Naheulbeuk's Dungeon Master*, Steam version 1.8. It fixes glitches that players still report, adds safety nets for situations where the game gets stuck, adds management tools, and changes part of the game's balance. Each change can be turned off on its own in a settings file.

Version 0.24.0, by Jeanbonbeurre23. This patch is not made or endorsed by the developers of the game.

*La version française suit la version anglaise : [Français](#français).*

## What it does

Every change below is active once the patch is installed. The settings file (see [Settings](#settings)) turns each one off or adjusts it.

**Fixes**

- A click on a button or panel of the interface no longer also selects the room or character behind it, and no longer moves the camera there.
- A click on a furniture icon while adding walls, removing walls or deleting now switches the builder to furniture. When the walls cannot be validated, the builder's popup shows the tool actually in use.
- The builder no longer marks a whole floor as not accessible when the floor is connected but its check from the stairs failed to start.
- When every pharmagician lies in bed waiting to be healed, one of them gets up and heals the others, so wounded minions no longer wait forever.
- With UnityExplorer installed, clicks on the game's menus no longer reach the dungeon behind them.

**Safety nets**

- Before the game overwrites a save, the patch keeps a copy of it, up to 10 copies per save.
- A tavern counter with fewer barmen than it needs gets the game's own hiring called for it.
- An attack whose fight has not changed for 5 minutes of game time has its alert ended, as the game does when a fight is over.
- A room under construction with no work left gets the game's own completion check.
- During a tutorial step that locks the interface, Ctrl+Shift+F9 gives the controls back and Ctrl+Shift+F10 validates the step.

**Management**

- The management tab of the Minions window is wider and shows minions in one column per group of origins: greenskins, humans, elves and drows by default.
- The character sheet gets a third page, "Assigned rooms", to give each minion his own dormitory, bathroom, canteen, break room and job room, and to forbid him rooms.
- In a recruitment candidate's window, a "Keep − N +" row sets how many minions of that job and origin the dungeon keeps, and the patch recruits listed candidates until the number is reached.
- A tab at the top left of the screen opens a panel that shows, for each room type the minions look for, whether they found a free piece of furniture there, and names those who did not.

**Economy**

- A bar at the top of the screen shows, for each workshop resource and each food, what was produced and consumed over the last decade.
- A "Bilan" counter next to "Dépenses", at the bottom right, shows the gold earned over the last decade minus the expenses.
- Every minion's salary is the lowest the game's formula gives for his origin, job and grade.
- Storage furniture holds 10 times its normal capacity.
- Weapons, magic, tools, intel and corpses can be stocked up to 10,000 instead of 1,000. The gauge steps start at the same stock as in the game, so the extra room is storage only.

**Balance**

- Every new game has 12 floors: five copies of floor 4 are inserted between floor 4 and the tavern. They unlock with floor 4 and each costs floor 4's upkeep. See [Saves](#saves) before starting a game.
- The dungeon holds up to 500 minions.
- The fighting minions (guards, spies, sorcerers, pharmagicians, necromancers, cultists, demons, undead) grow with their grade so that grade 10 matches the strongest adventurer at his top level. Grade 1 keeps the game's values.
- A loaded trap always goes off under an adventurer and never under one of your minions.
- A domestic cleans a 5 by 5 patch at each cleaning stop, where the game cleans 5 squares.
- The Golbargh is disturbed only by the people standing in his lair, not by everyone on his floor.
- While a unique raid still needs raids won against a faction, new ordinary raids go to that faction's territory 9 times out of 10.

The patch also writes what it does to `BepInEx\LogOutput.log` and to one file per launch in `BepInEx\NDMUnofficialPatch-logs`. The `Diagnostics` settings turn off its extra diagnostic logging. The patch's own interface elements are in English.

## Requirements

- *Naheulbeuk's Dungeon Master* on Steam, version 1.8, on Windows. At start-up the patch checks the game's `GameAssembly.dll` and applies nothing on any other version; the log then says "Unknown game build".
- BepInEx 6 for Unity IL2CPP, 64-bit, bleeding-edge build 788. Other BepInEx builds were not tested.

## Installation

1. Find the game's folder: in Steam, right-click *Naheulbeuk's Dungeon Master*, then **Manage › Browse local files**. It is the folder that contains `NDM.exe`.
2. Download BepInEx: [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip), from the [BepInEx bleeding-edge builds page](https://builds.bepinex.dev/projects/bepinex_be). Extract the archive into the game's folder, so that `BepInEx`, `dotnet`, `winhttp.dll` and `doorstop_config.ini` sit next to `NDM.exe`.
3. Start the game once, wait for the main menu, then quit. This first start takes several minutes longer than usual while BepInEx prepares itself; the next ones take the usual time.
4. Download `NDMUnofficialPatch-0.24.0.zip` from the Releases page and extract it into the game's folder. The patch must end up as `BepInEx\plugins\NDMUnofficialPatch\NDMUnofficialPatch.dll`.
5. Start the game. In `BepInEx\LogOutput.log`, the lines `NDM Unofficial Patch 0.24.0 loading` and `Known game build` show that the patch is running.

## Settings

The first start with the patch creates `BepInEx\config\ndm.unofficialpatch.cfg`. Every setting in it has a description of what it does and of its default. Edit the file with the game closed: `true` and `false` turn a feature on and off, and a number set to `0` turns its feature off.

The settings are grouped as in the list above: `Fixes`, `SafetyNet`, `Management`, `Economy`, `Balance`, `Floors`, `Raids` and `Diagnostics`. For example, `InsertedFloors = 0` in `[Floors]` keeps the game's seven floors, and `StorageCapacityFactor = 1` in `[Economy]` gives storage furniture its normal capacity.

## Saves

**The inserted floors change new games.** A game started with them is saved with 12 floors and always loads with them, whatever the setting says later. Such a save is meant to be loaded with the patch installed; loading it without the patch was not tested. To keep the game's seven floors, set `InsertedFloors = 0` before starting the new game. Saves started without the patch, or with the setting at 0, load with seven floors.

**Adding the floors to a campaign already started.** The script `tools\convert_save_floors.py` (Python 3) writes a copy of a seven-floor save with the inserted floors, under a new name, and never changes the original. With the game closed:

`python tools\convert_save_floors.py "%USERPROFILE%\AppData\LocalLow\Artefacts Studio\NDM\Save\Game_Default_1.sav" "1 (12 floors)"`

The patch builds the new floors the first time the copy is loaded. The dungeon and the tavern are connected again once stairs are built up through the new floors. [docs/features/floor-conversion.md](docs/features/floor-conversion.md) describes the script.

**Files next to the saves**, in `%USERPROFILE%\AppData\LocalLow\Artefacts Studio\NDM`:

- `NDMUnofficialPatch-backups` holds the copies of saves made before the game overwrote them.
- `NDMUnofficialPatch-data` holds, for each save, its number of inserted floors, its assigned and forbidden rooms, and its recruitment targets. Keep these files with their saves.

## Uninstalling

Delete the folder `BepInEx\plugins\NDMUnofficialPatch`. To remove BepInEx as well, delete `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and `changelog.txt` from the game's folder.

Keep the patch for saves with inserted floors: loading them without it was not tested. Other saves load without it. Some values the patch wrote into them stay: lowered salaries and the attack and defense of fighting minions until the minion's next grade change, when the game recomputes them, and the extra life of fighting minions for good.

## Building from source

The build needs the .NET SDK 6 or later, and the game with BepInEx installed and started once. Create `GamePath.props` at the root of the repository, pointing at the game's folder:

```xml
<Project>
  <PropertyGroup>
    <GameDir>C:\Program Files (x86)\Steam\steamapps\common\NDM</GameDir>
  </PropertyGroup>
</Project>
```

`dotnet build src\NDMUnofficialPatch\NDMUnofficialPatch.csproj -c Release` builds the patch and copies it into the game's `BepInEx\plugins` folder; `-p:Deploy=false` skips the copy. [docs/features](docs/features) has one page per feature, with the game's rule as read from its code and what the patch changes. [tools/il2cpp-reader](tools/il2cpp-reader) holds the scripts used to read the game's native code.

## License

The patch's code, documentation and scripts are released under the GNU General Public License, version 2 (see [LICENSE](LICENSE)). The game, its code and its content belong to their owners and are not covered by this license.

---

## Français

Un patch non officiel pour *Naheulbeuk's Dungeon Master*, version Steam 1.8. Il corrige des bugs que les joueurs signalent encore, ajoute des garde-fous pour les situations où le jeu se bloque, ajoute des outils de gestion et modifie une partie de l'équilibrage. Chaque changement peut être désactivé séparément dans un fichier de réglages.

Version 0.24.0, par Jeanbonbeurre23. Ce patch n'est ni réalisé ni approuvé par les développeurs du jeu.

### Ce qu'il fait

Tous les changements ci-dessous sont actifs dès que le patch est installé. Le fichier de réglages (voir [Réglages](#réglages)) permet de désactiver ou d'ajuster chacun d'eux.

**Corrections**

- Un clic sur un bouton ou un panneau de l'interface ne sélectionne plus aussi la salle ou le personnage situé derrière, et ne déplace plus la caméra vers lui.
- Un clic sur une icône de mobilier pendant l'ajout ou le retrait de murs, ou la suppression, fait bien passer le constructeur au mobilier. Quand les murs ne peuvent pas être validés, la fenêtre du constructeur affiche l'outil réellement utilisé.
- Le constructeur ne déclare plus tout un étage inaccessible quand l'étage est relié mais que sa vérification depuis l'escalier n'a pas démarré.
- Quand tous les pharmagiciens sont couchés en attendant d'être soignés, l'un d'eux se lève et soigne les autres, si bien que les sbires blessés n'attendent plus sans fin.
- Avec UnityExplorer installé, les clics sur les menus du jeu n'atteignent plus le donjon situé derrière.

**Garde-fous**

- Avant que le jeu n'écrase une sauvegarde, le patch en garde une copie, jusqu'à 10 copies par sauvegarde.
- Un comptoir de taverne qui a moins de barmen qu'il n'en faut déclenche l'embauche prévue par le jeu.
- Une attaque dont le combat n'a pas évolué pendant 5 minutes de jeu voit son alerte terminée, comme le fait le jeu à la fin d'un combat.
- Une salle en construction sans plus aucun travail à faire passe par la vérification de fin de chantier du jeu.
- Pendant une étape du tutoriel qui bloque l'interface, Ctrl+Maj+F9 rend les commandes et Ctrl+Maj+F10 valide l'étape.

**Gestion**

- L'onglet de gestion de la fenêtre des sbires est élargi et range les sbires en une colonne par groupe d'origines : peaux-vertes, humains, elfes et drows par défaut.
- La fiche de personnage gagne une troisième page, « Assigned rooms », pour attribuer à chaque sbire son dortoir, sa salle de bain, sa cantine, sa salle de repos et sa salle de travail, et pour lui interdire des salles.
- Dans la fenêtre d'un candidat au recrutement, une ligne « Keep − N + » fixe le nombre de sbires de ce métier et de cette origine que le donjon conserve, et le patch recrute les candidats proposés jusqu'à l'atteindre.
- Un onglet en haut à gauche de l'écran ouvre un panneau qui indique, pour chaque type de salle que cherchent les sbires, s'ils y ont trouvé un meuble libre, et nomme ceux qui n'en ont pas trouvé.

**Économie**

- Une barre en haut de l'écran affiche, pour chaque ressource d'atelier et chaque nourriture, ce qui a été produit et consommé pendant la dernière décade.
- Un compteur « Bilan » à côté de « Dépenses », en bas à droite, affiche l'or gagné pendant la dernière décade moins les dépenses.
- Le salaire de chaque sbire est le plus bas que donne la formule du jeu pour son origine, son métier et son grade.
- Les meubles de stockage contiennent 10 fois leur capacité normale.
- Armes, magie, outils, renseignements et cadavres peuvent être stockés jusqu'à 10 000 au lieu de 1 000. Les paliers des jauges commencent au même stock que dans le jeu, si bien que la place supplémentaire ne sert qu'au stockage.

**Équilibrage**

- Chaque nouvelle partie compte 12 étages : cinq copies de l'étage 4 sont insérées entre l'étage 4 et la taverne. Elles se débloquent avec l'étage 4 et coûtent chacune son entretien. Lire [Sauvegardes](#sauvegardes) avant de commencer une partie.
- Le donjon peut accueillir jusqu'à 500 sbires.
- Les sbires combattants (gardes, espions, sorciers, pharmagiciens, nécromanciens, cultistes, démons, morts-vivants) progressent avec leur grade de sorte qu'au grade 10 ils valent l'aventurier le plus fort à son niveau maximal. Le grade 1 garde les valeurs du jeu.
- Un piège chargé se déclenche toujours sous un aventurier et jamais sous l'un de vos sbires.
- Un domestique nettoie un carré de 5 sur 5 à chaque arrêt de nettoyage, là où le jeu nettoie 5 cases.
- Le Golbargh n'est dérangé que par les personnes présentes dans son antre, et non par tout son étage.
- Tant qu'un raid unique attend encore des raids gagnés contre une faction, les nouveaux raids ordinaires vont 9 fois sur 10 sur le territoire de cette faction.

Le patch écrit aussi ce qu'il fait dans `BepInEx\LogOutput.log` et dans un fichier par lancement dans `BepInEx\NDMUnofficialPatch-logs`. Les réglages `Diagnostics` désactivent ses journaux de diagnostic supplémentaires. Les éléments d'interface propres au patch sont en anglais.

### Prérequis

- *Naheulbeuk's Dungeon Master* sur Steam, version 1.8, sous Windows. Au démarrage, le patch vérifie le fichier `GameAssembly.dll` du jeu et n'applique rien sur une autre version ; le journal indique alors « Unknown game build ».
- BepInEx 6 pour Unity IL2CPP, 64 bits, build bleeding-edge 788. Les autres builds de BepInEx n'ont pas été testés.

### Installation

1. Trouver le dossier du jeu : dans Steam, clic droit sur *Naheulbeuk's Dungeon Master*, puis **Gérer › Parcourir les fichiers locaux**. C'est le dossier qui contient `NDM.exe`.
2. Télécharger BepInEx : [BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip), depuis la [page des builds bleeding-edge de BepInEx](https://builds.bepinex.dev/projects/bepinex_be). Extraire l'archive dans le dossier du jeu, de sorte que `BepInEx`, `dotnet`, `winhttp.dll` et `doorstop_config.ini` se trouvent à côté de `NDM.exe`.
3. Lancer le jeu une fois, attendre le menu principal, puis quitter. Ce premier lancement prend plusieurs minutes de plus que d'habitude, le temps que BepInEx se prépare ; les suivants prennent le temps habituel.
4. Télécharger `NDMUnofficialPatch-0.24.0.zip` depuis la page Releases et l'extraire dans le dossier du jeu. Le patch doit se trouver en `BepInEx\plugins\NDMUnofficialPatch\NDMUnofficialPatch.dll`.
5. Lancer le jeu. Dans `BepInEx\LogOutput.log`, les lignes `NDM Unofficial Patch 0.24.0 loading` et `Known game build` indiquent que le patch fonctionne.

### Réglages

Le premier lancement avec le patch crée `BepInEx\config\ndm.unofficialpatch.cfg`. Chaque réglage y est accompagné d'une description, en anglais, de ce qu'il fait et de sa valeur par défaut. Modifier le fichier jeu fermé : `true` et `false` activent et désactivent une fonction, et un nombre mis à `0` désactive la sienne.

Les réglages sont regroupés comme dans la liste ci-dessus : `Fixes`, `SafetyNet`, `Management`, `Economy`, `Balance`, `Floors`, `Raids` et `Diagnostics`. Par exemple, `InsertedFloors = 0` dans `[Floors]` conserve les sept étages du jeu, et `StorageCapacityFactor = 1` dans `[Economy]` rend aux meubles de stockage leur capacité normale.

### Sauvegardes

**Les étages insérés modifient les nouvelles parties.** Une partie commencée avec eux est sauvegardée avec 12 étages et se charge toujours avec eux, quel que soit ensuite le réglage. Une telle sauvegarde est faite pour être chargée avec le patch installé ; son chargement sans le patch n'a pas été testé. Pour garder les sept étages du jeu, mettre `InsertedFloors = 0` avant de commencer la nouvelle partie. Les sauvegardes commencées sans le patch, ou avec ce réglage à 0, se chargent avec sept étages.

**Ajouter les étages à une campagne déjà commencée.** Le script `tools\convert_save_floors.py` (Python 3) écrit une copie d'une sauvegarde à sept étages avec les étages insérés, sous un nouveau nom, sans jamais modifier l'originale. Jeu fermé :

`python tools\convert_save_floors.py "%USERPROFILE%\AppData\LocalLow\Artefacts Studio\NDM\Save\Game_Default_1.sav" "1 (12 floors)"`

Le patch construit les nouveaux étages au premier chargement de la copie. Le donjon et la taverne sont de nouveau reliés une fois des escaliers construits à travers les nouveaux étages. [docs/features/floor-conversion.md](docs/features/floor-conversion.md) décrit le script, en anglais.

**Fichiers à côté des sauvegardes**, dans `%USERPROFILE%\AppData\LocalLow\Artefacts Studio\NDM` :

- `NDMUnofficialPatch-backups` contient les copies des sauvegardes faites avant que le jeu ne les écrase.
- `NDMUnofficialPatch-data` contient, pour chaque sauvegarde, son nombre d'étages insérés, ses salles attribuées et interdites, et ses objectifs de recrutement. Ces fichiers vont avec leurs sauvegardes.

### Désinstallation

Supprimer le dossier `BepInEx\plugins\NDMUnofficialPatch`. Pour retirer aussi BepInEx, supprimer `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` et `changelog.txt` du dossier du jeu.

Garder le patch pour les sauvegardes avec des étages insérés : leur chargement sans lui n'a pas été testé. Les autres se chargent sans lui. Certaines valeurs que le patch y a écrites restent : les salaires abaissés, ainsi que l'attaque et la défense des sbires combattants, jusqu'au prochain changement de grade du sbire, quand le jeu les recalcule, et la vie supplémentaire des sbires combattants définitivement.

### Compiler depuis les sources

La compilation demande le SDK .NET 6 ou plus récent, et le jeu avec BepInEx installé et lancé une fois. Créer `GamePath.props` à la racine du dépôt, pointant vers le dossier du jeu :

```xml
<Project>
  <PropertyGroup>
    <GameDir>C:\Program Files (x86)\Steam\steamapps\common\NDM</GameDir>
  </PropertyGroup>
</Project>
```

`dotnet build src\NDMUnofficialPatch\NDMUnofficialPatch.csproj -c Release` compile le patch et le copie dans le dossier `BepInEx\plugins` du jeu ; `-p:Deploy=false` évite la copie. [docs/features](docs/features) contient une page par fonction, en anglais, avec la règle du jeu telle que lue dans son code et ce que le patch change. [tools/il2cpp-reader](tools/il2cpp-reader) contient les scripts servant à lire le code natif du jeu.

### Licence

Le code, la documentation et les scripts du patch sont publiés sous la licence publique générale GNU, version 2 (voir [LICENSE](LICENSE)). Le jeu, son code et son contenu appartiennent à leurs propriétaires et ne sont pas couverts par cette licence.
