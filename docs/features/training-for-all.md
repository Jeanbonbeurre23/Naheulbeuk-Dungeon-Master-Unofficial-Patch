# Training for all

Plugin 0.27.0, 9 October 2026, game 1.8. Setting `Balance.EveryoneTrains` (on by default).

## The problem

In the game only guards train. Every minion's behaviour list contains training, and the training tree asks for no particular job, but only the guard's job gives the train score that makes the behaviour available.

## The game's rule

`BehaviourTreeUtility.FillAvailablesBehaviours` lists, from the behaviour config, the behaviours whose score pool holds the character. `MinionBehaviourConfig` lists TRAIN for every minion, but only the guard's job components (`WorldConfig.MinionJobComponents[GUARD]`) contain `AIComputeTrainScoreConfig`, which adds `AIComputeTrainScoreComponent`. `AIComputeTrainScoreJob` gives a base score of 1 while the minion's grade is below his maximum grade (`GradeNativeComponent`), which TRAIN's evaluator (`S_WORK`, the evaluator of every job's work) turns into 0.5. The training tree books a melee dummy, else a ranged one, in a training room of the minion's prestige, walks to it and runs `TrainTask`, whose action system applies the training state of the prop while he trains.

## What the patch does

Every minion with a job, and every summon who was given a job, can go to the training room to gain grades. Guards train as in the game. The others train only when their job has nothing for them to do: their training scores 0.2, above idling and below any job's work (0.5) and below a need the game already scores, so a cook with something to cook cooks, and a cook with nothing to cook trains instead of standing around.

## How it is built

`Balance/TrainingForAll.cs`. Every 10 seconds (real time) it takes the guard's own `AIComputeTrainScoreConfig` and gives it, through `AComponentConfig.AddComponents` and `SetupComponents`, to every character whose behaviour config is `MinionBehaviourConfig`, who has a `GradeNativeComponent`, who is not a guard and has no train score yet (60 per pass at most). It then empties his list of available behaviours so that the game builds it again with TRAIN. A prefix on `AISwitchBehaviourSystem.Run` caps the train `BaseScore` of every non-guard at 0.4, which `S_WORK` turns into 0.2. With the setting off, the train score and its config component are removed from every non-guard at the next pass.

## Log

`[Training] <name> (entity N) can now train in the training room when he has no work` for the first 10, `[Training] training given to N more character(s), M this session` at each pass that gives some, and every 5 minutes `[Training] N non-guard(s) can train; M of them training at this moment`.

## Limits

The training room's dummies are shared with the guards, and the training search still prefers rooms of the minion's prestige. How much grade a training gives a non-guard was not read; a training guard carries a state adding 0.1 to his grade gauge's increase. Some characters' animation sets lack the training animations, the summons' among them: they train without the animation, and `Player.log` records an error each time. The train score is kept in the save: before removing the patch, set `EveryoneTrains = false`, then load and save each game once, or non-guards keep training at the priority of their work.
