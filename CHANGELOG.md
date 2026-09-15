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
- Trader reputation rewards name their trader. All 508 of them were being read from the wrong field
  and showed as a bare "Reputation +0.02".

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
