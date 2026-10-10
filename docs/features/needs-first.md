# Needs first

Plugin 0.27.0, 9 October 2026, game 1.8. Settings `Balance.NeedsFirst` (on by default) and `Balance.NeedsFirstBelow` (40 by default).

## The problem

In the game, a working minion turns to a need only when his current task ends or when his work tree reaches a point where it reconsiders, and the toilet, a wash or fun outrank work only once they fall below about 27%, after his morale has started to fall at 30%. Chatting outranks almost every need. In a test game, 174 minions resigned in six hours, each with at least two needs below 30%, and 76 of them unpaid; some were still at work with a due salary and a pressing toilet as their best scores.

## The game's rule

`AISwitchBehaviourSystem.Run` goes through every character once per frame. For each behaviour he has, it reads the `ScoreData` of the behaviour's score component (`GetScoreDataForType`: TOILET reads `AIComputeToiletsScoreComponent`, EAT_IN_CANTEEN and EAT_IN_TAVERN read `AIComputeEatScoreComponent`, the three ENTERTAINMENT behaviours read `AIComputeFunScoreComponent`, DISCUSS reads `AIComputeDiscussScoreComponent`) and turns its `BaseScore` into a `FinalScore` with the behaviour's evaluator from the `AIEvaluator` config. A need's base score is its deficit, one minus the gauge's fraction. Sleep and the two meals use `C_PRIMARY_NEEDS`: 0 above a gauge of 70%, 0.35 at 67%, 0.55 at 30%, 1 at 10% and below. Hygiene, the toilet and the entertainments use `C_SECONDARY_NEEDS`: 0 above 70%, 0.3 at 60%, 0.45 at 30%, 0.8 at 10% and below. Work, training and healing score 0.5 at most (`S_WORK`), chatting 0.9, a due salary 0.9. Below 30% each need applies a state that adds 0.1 per second to the fall of morale, and between 30% and 40% the game shows the need above the minion's head.

The game changes a character's behaviour only when one of three flags of his `BehaviourTreeOwnerComponent` is set: `ShouldComputePriority` when his tree ends, `TryUpdatePriority` from a `TryRecomputeBehaviourPriorityTask` node, and `ForceUpdatePriority` from `BehaviourTreeUtility.ForceUpdateBehaviour`, which the game calls on about forty events. With `ForceUpdatePriority`, it stops the running tree, the game's fallbacks release what the tree held, and the best behaviour starts.

## What the patch does

As soon as one of a minion's five needs (hunger, sleep, fun, hygiene, toilet) falls below 40%, or his pay is due, he drops his work, his idling, a chat, his training or his guard round and goes to satisfy his most urgent need: the lowest gauge, a due salary counting as 30%. When he finishes a need and another one is still below 40%, he goes to that one next instead of going back to work. A fight, a flight, a wounded minion's wait for a healer, a prisoner being carried and a need already being satisfied are not interrupted. When a need cannot be met, for instance when no toilet or bed is free, he goes back to what the game would have him do and tries again 30 seconds of game time later, and his next most urgent need is tried meanwhile.

## How it is built

`Balance/NeedsFirst.cs`. Twice a second (real time) it lists the minions (`MinionTag`, alive, in the dungeon) with a gauge below `NeedsFirstBelow` (`Gauge.CurrentPercentage` of the five need components) or a `RequestSalaryTagComponent`. A prefix on `AISwitchBehaviourSystem.Run` handles each of them whose current behaviour is a job, training, idling, chatting, guard rounds or paying salaries, or a need whose tree has just ended. It reads his needs again, picks the most urgent one the game scores above 0, sets that need's `BaseScore` and `FinalScore` to 1, and sets to 0 those of his other needs and of seventeen work, training and chatting score components. The game's evaluator then gives the chosen need its maximum: 1 for a meal or sleep, 0.9 for the salary, 0.8 for the others. When he is working, idling, chatting or training, the patch also sets `ForceUpdatePriority`, at most once every 3 seconds of game time. A need chosen again within 4 seconds of game time has failed; after two such failures, or three forced changes in a row that did not take him to a need, that need is left to the game for 30 seconds of game time. Field offsets are checked against the runtime's metadata, and the behaviour and flag offsets against those the method bodies use; if a check or a read fails, the patch turns itself off for the session and logs why.

## Log

`[Needs] <name> (<entity>, <job>) leaves <behaviour> for his <need> (<gauge>%)` for the first 40 interruptions; `[Needs] <name>: his <need> (<gauge>%) is left to the game for 30 seconds, since ...` for the first 30 give-ups; every 5 minutes, a count of the interruptions, of the needs chosen when another ended, and of the give-ups, per need.

## Limits

The patch does not add toilets, beds, showers or tables: when the dungeon lacks them, minions retry every 30 seconds instead of working, and their morale still falls. A minion whose pay is due still needs a banker free to pay him. Bankers, pharmagicians and guards leave their posts for their needs like everyone else.
