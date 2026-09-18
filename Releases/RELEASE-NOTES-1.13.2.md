# Quest Tracker 1.13.2

Install over 1.13.1 - replace both DLLs, keep your config. Both halves must come from this archive.

## The tree tells the truth

Three kinds of quest used to draw as ordinary locked boxes. They do not any more.

**Failed quests are their own state.** A quest the game has failed or expired drew exactly like one
you had not reached yet. It has its own colour and glyph now, and the box says whether the trader
will let you restart it.

**Quests you can never do wear a `!`.** Wrong faction, another game edition, a seasonal event: the
detail panel always said so, the box never did.

**A quest whose prerequisite is not installed wears a `?`.** A quest mod that requires a quest from
a mod you do not have drew as a quest you could start, sitting at the root of its tree with no line
into it. The detail panel now lists the missing quest's id under *Missing prerequisites*, which is
what identifies the absent mod.

**Boxes are sized for the name they show.** The ten abbreviated series - "W. Prof." for Weapon
Proficiency, and nine more - were measured on the full name and drawn with the short one, so every
one of those boxes was wider than its title.

## Under the hood

- The panel logs how long opening it took and how much of that was waiting on the server. Nothing
  had ever measured it; 1.13.3 is about making it smaller.
- A one-off check that asked the game's own hand-in test about saved presets (it accepted 6 of 6) is
  removed. It ran once per profile and wrote its working to the game's log.
- Builds refuse to compile when the two copies of the version number disagree.
- The archive is assembled by a script from a fixed list of files, and the README is in it for the
  first time.

## Withdrawn from 1.13.1

The 1.13.1 notes claimed that 9 of 60 weapon builds were provably the cheapest possible, that a
provably minimal build stops being searched, and that an `UNSOUND` line in the server log would mean
the mod had caught a wrong bound. Those claims are withdrawn and the machinery behind them is gone:
measured over the shipped builds it contributed under 0.05% of their cost, and it was wrong three
times in one session. One check of that family stays because you can act on it - every part in a
build is still proven necessary by removing it and re-checking. Training runs (`train.ps1`) now
stop on their own after 340,000 attempts or 5 hours, the point past which a measured eleven-hour
run found nothing.
