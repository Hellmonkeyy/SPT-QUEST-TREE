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
