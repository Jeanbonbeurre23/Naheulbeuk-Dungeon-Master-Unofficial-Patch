# Builder accessibility walk

Plugin 0.14.0, 30 September 2026, game 1.8. Setting `Fixes.UnreachableStart` (on by default).

## Why

On the third floor of a test save, after walls were drawn in the east, the builder called every square of the floor not accessible and refused to validate, although the floor was connected. Its walk from the stairs had not started.

## What it does

`Fixes/UnreachableStart.cs`, a prefix on `Builder.SetUnreachableTiles`. When the free square of the stairs the builder's job walked from (`CheckUnreachableTilesJob.m_stairTile`, square from `StairsUtility.GetUnblockedTile`) is still among the squares handed to the builder, the job's walk did not start. The prefix then walks from that square over the same squares, 4 neighbours at a time as the job does, and removes the squares it reaches before the builder reads them. One `[Unreachable]` line says how many squares it reached and how many remain.

## What it does not change

The rule. The walk starts from the same stairs as the game's, so squares those stairs cannot reach stay marked and the builder still refuses. When the game's own walk worked, the prefix does nothing.
