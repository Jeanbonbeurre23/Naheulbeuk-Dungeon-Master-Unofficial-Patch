# Necromancers and cultists heal

Plugin 0.25.0, 8 October 2026, game 1.8. Setting `Balance.NecromancersAndCultistsHeal` (on by default).

## What it does

Necromancers heal undead and vampires, and cultists heal their own demons, outside fights. In the game only pharmagicians heal, and their `HEAL` behaviour (`BT_Heal`) goes only to minions lying in an infirmary bed. Undead and demons never lie in a bed, since their behaviour configs (`UndeadBehaviourConfig`, `DemonBehaviourConfig`) have no `REQUEST_HEAL`. Vampires are an origin of ordinary minions, so they lie in beds and pharmagicians heal them.

Since undead and demons cannot lie in a bed, the patch gives undead, vampires and demons a heal at close range, adds the game's bed heal of vampires by necromancers, and heals no one during a fight.

## Heal at close range

Twice a second, each necromancer and each cultist who is not fighting tends one patient at a time. A necromancer's patients are wounded undead and wounded vampire minions who are not asking for a bed heal; a cultist's are the wounded demons linked to him. The patient stands on the healer's floor within 5 squares and is not fighting. Among those, the healer takes the one with the lowest share of his life. After 10 seconds of game time together, the patient gets the result of a pharmagician's heal: his life at its maximum, and his healable states removed (bleeding, poison, Broken, Frozen, the Dynamo's electrocution, the curses Yrfoul, Oboulos, Dlul and Mankdebol, and the hurt emote). The heal starts over when the patient leaves the 5 squares, when either one starts fighting, or when the patient loses more than 0.25 life points between two checks, which marks a hit. Fighting means a current behaviour of `COMBAT`, `ENEMY_COMBAT` or `USE_SKILL`. The healer keeps doing his own work during those 10 seconds.

## Bed heal of vampires

Every 5 seconds, each necromancer who lacks it gets the pharmagician job's three heal components (`ComputeHealScore`, `HealDuration`, `HealProgression`), added by the job's own component configs, so that he has the `HEAL` behaviour with the pharmagician's 10 seconds of work. Three hooks keep him to vampires. A prefix on `AISwitchBehaviourSystem.Run` sets his `HEAL` score to 0 unless a vampire waits in bed with no healer or with him as healer. A prefix on `FindMinionToHealTask.OnExecute`, when the healer looking is a necromancer, marks every other waiting patient as taken for the time of the call, and the postfix gives them back. A postfix on `MinionUtility.IsTherePharmagicianInDungeon` counts a necromancer as a healer only for a vampire, so that a wounded minion does not wait in bed for a necromancer who will not come. The heal deadlock fix (`heal-deadlock.md`) leaves necromancers out of the pharmagicians it watches. Pharmagicians are unchanged and still heal vampires too.

The three components are kept in the save. With the setting off, the patch removes them from every necromancer when a save is loaded. Before removing the patch itself from the game, turning the setting off and saving once leaves a save with no trace of it; otherwise the necromancers of that save keep the heal of a pharmagician, without the restriction to vampires.

## Log

`[Healing]`: once per necromancer given the heal (the first 20 one by one), each heal at close range for the first 40, then every 5 minutes the number of undead, vampires and demons healed at close range. An error is logged once, and necromancers and cultists then stop healing until the game is restarted.

## Limits

A heal at close range has no animation, and the healer does not walk to his patient. A character's square is his `GridCoordinatesComponent`; whether it follows a walking character square by square was not checked.
