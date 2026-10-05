# Recruitment targets

Plugin 0.20.0, 2 October 2026, game 1.8. Setting `Management.RecruitTargets` (on by default).

## What it does

In the window of a recruitment candidate, a row "Keep − N +" next to the Recruit button sets how many minions of that candidate's job and origin the dungeon keeps. One click changes the number by 1, Shift+click by 5, and the row also shows how many you have. While a job and origin has fewer minions than its target, the patch recruits one of the listed candidates of that job and origin every 3 seconds of game time, as the Recruit button does. Design choices, 2 October 2026: a stepper, a target that works as a minimum, and no overview beyond the candidate's window.

## The game's rule

Read from the game's code and configs bundle on 2 October 2026. The recruitment list (`RecruitmentComponent.EntitiesAvailableForRecruitment`, on the player entity) holds the candidates, which carry `CandidateToRecruitTagComponent`. With the recruitment roll activated (`RecruitmentComponent.IsRollActivated`), `RecruitmentUtility.DrawCandidates` deletes all candidates and creates new ones. It runs every decade and after a paid reroll, and creates one candidate for each recruitable job in each origin available for that job (`CreateAllNewCandidatesForJob`, `OriginsUtility.GetAllOriginsAvailablesForJob`), then adds unique characters. The recruitable jobs (`MinionJobInfoConfig.m_isRecruitable`) are domestic, cook, artisan, sorcerer, pharmagician, cultist, banker, torturer, necromancer and spy. Each recruitable origin lists the jobs it can take (`OriginInfoConfig.m_availableJobs`): drows, for instance, can be guards, necromancers, spies and torturers.

The Recruit button (`MinionDetailsTooltip.OnButtonRecruit`) calls `RecruitCandidateAtIndex`, which turns the candidate into a minion arriving through the teleporter. With the roll activated, it then creates a new candidate of the same job and origin. Neither takes gold; the minion costs his salary every decade. The button refuses when `MinionUtility.HasEnoughPlaceForMinions(1)` returns true, which, despite its name, happens when one more minion would go over the maximum. Guards and barmen are never in the list: the game hires guards for each guard locker (`HireGuardsForGuardLocker`) and barmen for each tavern counter.

## How the patch works

`Management/RecruitTargets.cs` adds the row to the candidate's sheet (`MinionDetailsTooltip` in `RECRUITMENT` mode), next to the Recruit button in the same layout. The row appears only for a listed candidate who is not unique. Every 3 seconds of game time, for each target above the count, the patch checks that recruitment is available (`RecruitmentUtility.IsRecruitmentAvailable`) and that the dungeon is not full. It then recruits one candidate of that job and origin from the list. When that candidate is the one shown in an open sheet, it recruits through the sheet's own `OnButtonRecruit`, so the game shows its feedback and selects the next candidate. Otherwise it calls `RecruitCandidateAtIndex`, the call the button makes. The game's `RecruitmentUtility` is taken from `UpdateRecruitmentProcessSystem`, which runs every frame.

A minion counts toward a target when he is alive, is not a candidate, is not unique or a VIP, and is not resigning or leaving the dungeon (`ResignTagComponent`, `LeaveDungeonTag`, `ExitDungeonTagComponent`). A raid participant (`RaidParticipantTagComponent`) counts even while he is away. A target is a minimum: the patch never fires anyone. Targets are written, when the game saves, to `NDMUnofficialPatch-data\<save>.recruitment.tsv` next to the room assignments, and read back when that save is loaded. A new game starts with none.

## Log

`[Recruit]` lines appear for the following:

- the first time the row is added, with the Recruit button, its parent's layout and its siblings, so the placement can be checked;
- each change of a target;
- each minion recruited, with his name, grade and the count reached;
- once per change of state, when a target waits because recruitment is not available, the dungeon is full, or the list has no candidate of that job and origin;
- the targets written at each save and restored at each load.

Errors are logged once each.

## Limits

The row follows the layout of the Recruit button's container, and the first log line describes that container in case the row lands somewhere awkward. A recruited minion has the grade the game drew for that candidate. The top-up does not use the paid reroll, so with the roll not activated (early campaign, scripted lists), a target waits until the game lists a matching candidate. The patch does not check whether a tutorial step is waiting for a manual recruit. Recruits arrive one per target every 3 seconds, so a large gap fills over some seconds of play. Whether a minion away on a raid carries `RaidParticipantTagComponent` for the whole raid was not checked; if he loses it while away and carries a leaving tag, he is replaced, and the target is exceeded when he returns.
