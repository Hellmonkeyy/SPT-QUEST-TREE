# Quest Tracker 1.13.2

The tree tells the truth about three kinds of quest it used to draw as ordinary locked boxes, and
the release itself is built by a script instead of by hand.

**Failed quests are their own state.** A quest the game has failed or expired drew exactly like one
you had not reached yet. It has its own colour and glyph now, and the box says whether the trader
will let you restart it.

**Quests you can never do wear a mark.** Wrong faction, another game edition, a seasonal event: the
detail panel always said so, the box never did. It wears a `!` now.

**A quest whose prerequisite is not installed wears a mark too.** A quest mod that requires a quest
from a mod you do not have drew as a quest you could start - it sat at the root of its tree with no
line into it. It wears a `?` now, and the panel lists the missing quest's id under *Missing
prerequisites*, which is what identifies the absent mod.

**Boxes are sized for the name they show.** The ten abbreviated series ("W. Prof." for Weapon
Proficiency, and nine more) were measured on the full name and drawn with the short one, so every
one of those boxes was wider than its title.

## Under the hood

- The panel logs how long opening it took, and how much of that was waiting on the server. Nothing
  had ever measured it; the next release is about making it smaller.
- The one-off check that asked the game's own hand-in test about saved presets (it accepted 6 of 6)
  is removed from the build. It ran once per profile and wrote its working to the game's log.
- Builds refuse to compile when the two copies of the version number disagree - which has happened
  once already, in 1.12.1.
- The release archive is assembled by `package.ps1` from a fixed list of files, and the script
  refuses any archive holding a file off that list. Until now every release was zipped by hand from
  the live install folder, one directory away from a third-party file that must never ship. The
  README is in the archive for the first time.
- The weapon-build proof claims in the published 1.13.1 notes are withdrawn; see that entry.

---

# Quest Tracker 1.13.1

The release that was actually published: 1.13.0 was built but never went out, so everything under
1.13.0 below shipped here. What 1.13.1 adds on top:

**Dollars count as money you hold.** The item watchlist's dollar template id had one wrong character
and had never matched anything. Roubles and euros were fine.

**A build you asked for during a re-solve no longer goes stale for a generation.** A request arriving
while the server was already computing your builds was refused and then forgotten, so the panel kept
the old answer until something else happened to ask.

**Opening a three-weapon quest asked the server three times per repaint.** *Old Friend's Request*
made three blocking calls for an answer that could not have changed between them; a not-ready answer
now stands for two seconds.

**Quests with no map stopped claiming items on the hideout.** An unlocated quest's items were fanned
across all nineteen location entries, six of which nobody can raid.

**A quest stating two thresholds on one stat is held to the stronger one**, and a modded "recoil at
least X" is read the right way round.

**The builds are re-seeded.** 56 of the 60 shipped builds come from a fresh training run and 4 are
kept from the previous seed; in total they cost within a percent of what they did.

## Withdrawn

The published notes for this release also claimed that 9 of 60 builds were *provably* the cheapest
possible, that a provably minimal build stops being searched, and that an `UNSOUND` line in the server
log would mean the mod had caught a wrong bound. Those claims are withdrawn: the machinery behind them
was removed from the code the same day, having contributed under 0.05% of the builds' cost and been
wrong three times in one session. The 1.13.1 build still prints the lines; they mean nothing. 1.13.2
is the first release without them. Irreducibility - every part proven necessary by taking it off and
re-checking - is kept.

---

# Quest Tracker 1.13.0

A whole-project code review, and what it found. Five features did not work at all, four of them
failing silently - nothing logged, nothing crashed, the screen just quietly did nothing.

## Five things that now work

**The pre-raid check comes back.** If one of a map's requirements had been placed by guesswork rather
than from a harvested zone, the whole map went dark: no summary, no "take with you" list, and a plain
ready-up button - throwing away every *other* requirement on that map to avoid overstating one. Those
rows are back. Only the green light is withheld, the button says **READY?**, and the sidebar says how
many of the map's requirements it could not place.

**Search finds traders.** The box has always said *"Search quests or traders"* and typing a trader
matched nothing, because the trader names were attached to the quests after the search index was
built. Typing `Prapor` works. The same bug was quietly randomising trader colours between sessions.

**21 quests get their item pins.** A quest that does not name its own map - *Lend-Lease - Part 1*,
*Vitamins - Part 1*, *Secret Benefactor*, *Delivery From the Past* and 17 more - was filed under a map
key that matches nothing, so its items were never pinned even though the server knew the exact spawn
coordinates. All 21 are pinned now, on the maps their items actually spawn on.

**Clicking a Do-next row keeps your place.** Opening the ninth row threw you back to the top of the
list, with the row you opened off screen.

**Quest boxes stop reading 0/2 on a quest you could hand in.** The tree counted hand-over objectives as
never done, however full your stash - while the Do-next row for the same quest correctly said "ready to
hand in". One rule serves both now.

## The weapon builder tells the truth about its own proofs

The verifier used a magic number to mean *"I cannot prove a lower bound for this"*, and every reader
took it for the strongest possible proof. A requirement nothing could bound was therefore marked
**provably minimal**, cached that way, counted in the boot log's total, and the adversarial search that
exists to disprove exactly such claims ran on the wrong set. That number is gone rather than guarded,
and requirements with no bound are the ones the falsifier now attacks hardest.

**Builds stop being permanently stale.** A profile doing Gunsmith early - a part already on a gun you
own, a part a trader only sells at a higher loyalty - had every request answer "this build is broken".
The panel never left the stale state while a background thread re-solved all sixty requirements in a
loop. That is the player the feature is most for.

## Also

- Hideout-craft unlocks were systematically under-ranked in **Do next**: the reward carries a hideout
  area where the mod read a trader, so the ranking scored all 31 of them as good as unreachable.
- 16 seasonal quests were not flagged as seasonal, and 9 more were called permanently unavailable
  during the very weeks the game shows them. The mod asks the game now instead of reimplementing it.
- Weapon presets appear without restarting the game (also fixed in 1.12.2).
- One preset per gun: a quest that wants more than one weapon - *Gunsmith - Part 21*, *Old Friend's
  Request* - saved every build under the same name, and the game de-duplicates by name, so you kept
  one gun and lost the rest. Presets are named `QT: <quest> - <weapon>` now.
- Opening the Maps tab, or its floor dropdown, no longer rebuilds the whole item watchlist.
- The builds panel no longer regenerates every trader's stock on every request.
- A dropdown re-picking its current value no longer stays painted over the page.
- A pin serving a finished quest and an unfinished one stops claiming there is nothing left to do.
- A hand-edited or third-party map-zone file with a missing list no longer costs that map its whole
  harvest.
- The six locations nobody can raid - the hideout, the Arena scene, four unshipped stubs - stop
  appearing as maps.
- A cached weapon build is now invalidated when a mod update makes it unassemblable, rather than being
  refused at the workbench.

## Documentation

**The README said nothing is written to your profile.** That stopped being true in 1.12.0: presets you
save yourself are ordinary saved builds and they survive uninstalling. The uninstall section says so,
and *Save as a weapon preset* is documented at last. The mod also points at its own page in the
launcher's mod list now.

Internally, the build enables the compiler's documentation analysers, which caught eight malformed doc
comments on the first run - one of them written during this very review. They do not catch every kind:
twenty-eight comment blocks carrying two summaries between them are still owed a pass, because that
particular mistake raises no warning at all.

---

# Quest Tracker 1.12.2

## Weapon presets appear without restarting the game

**Save as a weapon preset** wrote the preset correctly but could not show it, so it only turned up
after a trip back to profile select - which is what the button was built to avoid. It said so rather
than pretending ("Go back to the profile select for it to appear"), but saying so is not the feature.

The server echoes the saved build back to the client so it can drop it into the list the game is
already reading from. It was echoing it with the mod's own JSON settings instead of SPT's, and SPT
attaches item ids to a converter registered in its serializer rather than to the type - so every
`_id`, `_tpl` and `parentId` in that echo came out as an empty object. Not mis-shaped: **missing.**
The client threw on the first one.

It now serialises with SPT's own serializer, the same one that wrote the profile, so what the client
inserts and what is on disk agree by construction.

A second fault was sitting behind the first, and the same change fixes it: the item's `upd` block was
being renamed as well, so had only the ids been patched, presets would have loaded with no durability,
no fire mode and no found-in-raid flag - quietly, with nothing to see.

## Also

- Every reason a preset cannot be shown immediately is now written to the log. Four of them used to
  fail silently, and the only reason this bug was diagnosable is that it happened to land in the one
  case that spoke up.
- A successful insert says so in the log too.

---

# Quest Tracker 1.12.1

## Task items count as items you have

The pre-raid check told you to pack a task item - and there is no way to pack one. Quest items live in
the game's own task-item containers ("Task items on character" and "Task items in stash"), and they
cannot be dragged into a rig at all; the game carries them into the raid for you. The mod was only
looking in your gear and your stash, so a plan handed to you by the quest that grants it read
**"0 of 1 - elsewhere"**, and the ready-up button said **1 TO PACK** about something you already had
and could never lose.

Those containers now count as held on you, and the rows say **task item** rather than "on you", so it
is clear which part of your kit the game is carrying for you.

## Also

- The map's amber pre-raid summary no longer says "none of it is on you" on a map where something
  already is - it now says how much of the list you have.
- Both halves' assembly version was still stamped 1.11.0 while the mod reported 1.12.0. They agree
  again.

---

# Quest Tracker 1.12.0

## Send the build straight to your gun

A Gunsmith quest now has **Save as a weapon preset**. Press it and the build lands in the game's own
build list, ready to load onto the weapon in one click at the workbench - and the game's preset screen
offers to **buy the parts you are missing**, through its own purchase flow.

It appears immediately; no restart, no going back to profile select. Nothing is written unless you
press the button, and every preset is named `QT: <quest>` so it is obvious which are the mod's and
re-saving replaces its own rather than piling up.

## Do next actually ranks now

The tab sorted into four tiers and then, in practice, **alphabetically** - on a profile with seventy-nine
accepted quests the tiers were all ties, so the quest's name decided the order.

It now scores every quest on what it is worth doing: what it pays, how much of the tree it opens,
trader unlocks you can actually reach, how close it is to done, how close it is to startable, whether
Kappa needs it, and how much work it looks like.

**Pick what you are playing for** - Balanced, Kappa path, Fast levelling, Trader unlocks, Item
hoarding - and the order changes with it. **Every row says why it is where it is**: *"unlocks 12 ·
96k XP · 80% ready"*. Rows name their map, and a line tells you when several of your top ten are in
the same place, because travel between maps is the real cost.

## Click a quest and it opens where you are

Rows expand in place, as many as you like, with everything needed to do the quest: objectives and
live counts, what to take and how much you already hold, where to go, what it pays and unlocks, and
the full weapon build for a Gunsmith quest. The list keeps your place when you open one.

## Objectives were missing on 42% of quests

A quest could open and show requirements, rewards and unlocks but **no objectives at all** - no what
to do, no where to go. 173 vanilla quests and 38 modded ones showed none, and 149 more showed part of
the list.

The mod was filtering on a flag that is set on nothing: across the 1,606 objectives in the quest
database it is absent 1,080 times, false 526 times and **true not once**. Fixed, which also restores
objectives to the detail panel, the hover card, the node's progress count, the map sidebar, the
"take with you" list and search.

## Also

- Rewards know what they are worth in roubles, whether they are cash or gear, and which loyalty level
  an unlocked offer appears at.
- Trader reputation rewards name their trader. All 532 of them were being read from the wrong field
  and showed as a bare "Reputation +0.02". (Said 508 here until 1.13.0, which is the count of
  experience rewards.)

---

# Quest Tracker 1.11.0 — what's new since 1.8.5

## Gunsmith builds you can actually make

Open any Gunsmith quest (or any other quest that asks you to assemble a weapon) and the panel now
shows you a build that meets every number the quest checks — ergonomics, recoil, weight, magazine,
sighting range, size — with the parts the quest insists on already in it. Every part is a row you
can click to open in the inspect window.

**It only recommends parts you can get.** The build is worked out for *your* profile: what is loose
in your stash, what your traders sell you at your current loyalty levels, and — once you have flea
access — what is on the flea market. If the standard build names something you cannot buy, the mod
searches again using only what you can, and every build it shows you has passed an independent check
that it really meets the quest and really assembles.

**Each row tells you what the part costs you.** "already on the gun", "in your stash", a trader
price, "barter", or a flea estimate (flea prices move, so that one is marked as an estimate). The
headline adds it up: parts, roubles, and how many barters.

**If you own it but it's on a gun, it says so.** "fitted to your equipped MDR" beside the price.
The mod never assumes you will strip a weapon you use — that is your call, so it shows you both the
part and what a second one costs.

**When there is no build from what you can get, it tells you why — and what would fix it.**
Three different situations, kept apart:

- *Your traders are too low* — "to build it you need: Delta-Tek Sprut mount from Jaeger at
  loyalty 2". A goal, not a dead end.
- *You need the flea market* — "reachable once the flea market unlocks at level 15".
- *Nobody sells it* — no trader at any level stocks what this quest needs on your install.

In each case you also see the closest build it could make from what you have, and exactly which
number it misses and by how much ("recoil 280.67, needs <= 275 (short by 5.67)").

**It keeps the parts your gun already has.** The build is chosen to cost you the least — the
price of what you would have to buy plus a fixed allowance for each trip to a trader — so a part
already on the weapon's standard preset is preferred over an equivalent one you would have to go
and get.

**It gets better while you play.** The mod ships with builds worked out and checked in advance,
and every launch spends a few seconds in the background looking for cheaper ones for your
install. Anything it finds is used from the next start. It stops on its own; it will not sit
burning a core.

## Also

- The quest panel shows the build as a diff against the weapon's stock configuration: what is
  already fitted, what to swap (and for what), what to add.
- Height and width limits are now checked on the assembled gun, not only listed.
- A build that meets everything the mod can check but has a limit it cannot verify says so, rather
  than showing a green light it has not earned.

## For FIKA hosts

Builds are worked out per profile. One background worker serves every player on the server, at
the lowest priority, so six players never mean six times the CPU. A player's answer is refreshed
when their trader levels or stash change.

## Nothing to configure

There are no settings for any of this. The developer-only switches (training, falsification,
thread count, cache location) are environment variables that are off unless you set them; a
normal install never trains, never falsifies, and uses two threads for a few seconds at start.
