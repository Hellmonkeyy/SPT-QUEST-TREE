# Quest Tracker 1.14.0

Install over 1.13.x - replace both DLLs, keep your config. **Both halves must be 1.14.0**: an older
server answers the new check with "the server sent no items".

## The game agrees with the build

Until now every verdict this mod gave about a Gunsmith build was the mod checking its own arithmetic.
Each build your profile can assemble now carries one more line, computed by the game's own hand-in
test on the exact preset *Save as preset* would write:

- **The game will accept this build** (ergonomics, recoil and weight as the game measured them), or
- **The game would refuse this build: recoil 265.98 where the quest wants ≤ 250** - the first test
  the trader's code fails, in the trader's numbers, or
- **Game check: checkable once the quest is accepted** - the test needs the quest's own condition,
  which the game only gives the client for quests you hold.

Two things the check cannot know, said on the line: it tests an unloaded gun at full durability. The
trader weighs the real one loaded and tests its real durability, so a build passing a weight limit
by a few grams can still be refused once a magazine is in.

## Also

- Magazine capacity is read from the magazine slot only, the way the game reads it. No vanilla
  build changes; a modded part that carries a cartridge list without being a magazine no longer
  counts as one.
