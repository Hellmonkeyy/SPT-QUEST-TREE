# Quest Tracker

An in-game quest planner for SPT. It opens on a map of the raid you have picked in the matchmaker,
with every quest you can do there pinned where it happens, and behind that sits the whole quest
progression - every quest in the game, including the ones you have not unlocked - as a branching
tree coloured by your progress. Plus the things a wiki cannot tell you: what your quests will ask
you not to sell, why a quest is locked, what to do next, and how far you are from Kappa.

**Built for SPT 4.1.5.** Not to be confused with DrakiaXYZ's *QuestTracker*, a different mod that
lists your active quests in raid; the two coexist, and this one's folder is `QuestTree`.

---

## Installing

Drag the `BepInEx` and `SPT_Runtime` folders from the archive into your SPT install folder (the one
containing `EscapeFromTarkov.exe`) and let them merge. You should end up with:

```
[SPT folder]\BepInEx\plugins\QuestTree\QuestTree.dll
[SPT folder]\BepInEx\plugins\QuestTree\kappa-quests.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\QuestTreeServer.dll
[SPT folder]\SPT_Runtime\user\mods\QuestTree\zones\*.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\cache\weapon-builds.json
```

The archive also carries this README and the release notes beside those two folders; they are for
reading, not for installing.

The `zones` and `cache` files are data the mod ships so a fresh install starts with map zones already
known and weapon builds already solved. Nothing breaks without them, but they come back slowly: a map's
zones are learned by raiding it, and the builds improve a little on each server start. Keep them.

Start the server first, then the game. A **Quest Tracker** button appears in the bottom taskbar, and
**Ctrl+Q** opens the tracker from anywhere in the menu - including the raid ready-up screen, where
the game hides the taskbar. That screen gets a button of its own too.

### Both halves are required

The client half works alone, but without the server half it can only show quests you have **already
unlocked**, with no pins, no item lists and no Kappa checklist - the toolbar will say so.

**On Fika, the half that matters is the one on the machine hosting the server.** If you join
someone else's server, *they* need `SPT_Runtime\user\mods\QuestTree` installed (the `zones` folder
included), not you. A client talking to a host without it gets the unlocked-only tree, and its
`BepInEx\LogOutput.log` shows `Http response status code: NotFound` on `/questtree/...` requests
and `could not send zones` after a raid. Harmless, but featureless. Both halves must also come from
the **same download** - the mod tells you in the Kappa tab if the versions disagree.

## Why there is a server half

The game client is never sent quests you have not unlocked. The server's own quest-list endpoint
filters down to quests already in your profile plus those whose prerequisites you have already met -
exactly the part a progression tree needs to see past.

The server half serves the complete, unfiltered quest list, reads your stash for the item and Kappa
checklists, reads the Kappa quest list out of the quest database, and keeps the map's zone store.

## The map

The tracker opens on **Maps**, on the map you have picked in the matchmaker when you have picked
one (the sidebar says "Your next raid"), otherwise on the busiest map. Pick a map and a floor at the
top; the map fills the panel and a column on the right lists what you can do there:

- **Do next here** - the map's unfinished quests, ranked by the same score the **Do next** tab uses
  and following whichever goal you picked there, with the one fact that matters per row.
- **Quests on the map** - every quest with an objective or item on this map, one row each. Click a
  row for its objectives and rewards inline, and the map flies to its pins.
- **Items to find here** - quest items that spawn on this map, and whether you already hold them.
  Click one to open the game's own inspect window on it. **Refresh** on the header line re-reads
  your stash.
- A legend under the map name says what the pin shapes and colours mean.
- Credits for the map image and the pin icons.

**Pins are real.** Objective zones are read from the map itself the first time anyone on the server
runs a raid there, and item pins come from the game's own spawn points. The release ships every
official map already harvested, so nothing is needed from you; a modded or custom map is pinned the
first time you load into it (the line above the map tells you how many zones it knows). Pins carry
the quest's status colour, name themselves on hover, and open the quest on click. **Accepted quests
only** narrows the pins and the list to what you have actually taken.

Map images are read from [DynamicMaps](https://github.com/mpstark/DynamicMaps)' own map folder when
you have it - nothing is bundled, copied or redistributed, and each map's author is credited in the
view. Without DynamicMaps you get the list alone.

## The tree

The **Tree** button, top right, is the whole progression. Each quest is a box that says what it is
without being clicked: a status mark and the name, then the trader, the level it wants and how many
of its objectives are done - with a bar along the bottom for the one you are on. Marks on the right
say what it pays out: experience, an item, reputation, a trader unlock.

**Six states, and each has a glyph as well as a colour**, so the tree still reads if you are
colour-blind or the box is small: in progress, available to start, completed (dimmed and struck
through, so finished work recedes), **level gated** - every prerequisite quest done but you are
short a level, loyalty or standing - locked behind another quest, and **failed** - the game has
failed or expired it, and the box says whether the trader will let you restart it.

Two marks in the top-right corner cover what a status cannot: **!** on a quest this profile can
never complete (wrong faction, another edition, a seasonal event), and **?** on a quest whose
prerequisite is not in your quest list at all - a quest mod referencing a quest another mod removed
- with the missing id named in the detail panel. Both used to draw as ordinary locked boxes.

Level-gated is its own state because it is the one that changes what you do: "another quest first"
means write it off for now, "you need two more levels" means keep it in mind.

**A box you cannot start says why**, in place of its trader line - `Needs Carbines III`, or `Lv 30`.
That is the single most useful thing a blocked quest can tell you and it used to cost a click.

**A run of quests in single file draws as one box.** Where each quest unlocks exactly the next and
they all come from one trader - a Gunsmith or Weapon Proficiency sequence - the tree folds the run
into its first box: the series name, the trader, how many of the run are done, and a bar for that
count, coloured by the first quest in it still to do. A `+` in the corner says there is more inside.
Click the box to unfold the run; the `–` on its first quest folds it again, and the tree remembers
what you opened for the session.

**A run opens itself when something inside needs seeing**: a failed quest, a quest whose
prerequisite is not installed, a search of three letters or more matching a member's name, or a
member you opened from the detail panel. With *Hide completed quests* on, a part-done run is drawn
quest by quest rather than as a box claiming to hold the finished ones. The **Chains** button and
`C` turn folding off and on - off and on again closes every run you had opened - and the setting
persists.

- **The box is the same box at every zoom.** It does not swap to a title, or to a code, or to a
  coloured bar - it scales, and nothing appears or disappears while you move.
- **Rest on any box** and a card appears with the full name, the level and loyalty it wants, how
  deep in its branch you are, every objective with its live count, and the rewards. No click, and
  the detail panel stays on whatever you were comparing against.
- **Hovering lights the whole chain** - every quest this one waits on, all the way back, and
  everything that unlocks from it, all the way forward, with the rest of the tree fading away.
- **Search highlights in place.** Matches light up, everything else dims, and nothing moves. The
  count reads `12 matches · 830 quests shown`. `Enter` opens the first match.
- **Focus (`X`)** cuts the tree to what you can work on now and everything within four quests of
  it. The reach is a setting, 1 to 10.
- **A coloured slab down each box** says whose chain it is, with that trader's portrait beside the
  quest their chains begin at. Modded traders get a colour of their own, chosen to avoid the status
  colours - so a trader can never be mistaken for a state.
- **Tabs** along the top: All, then one per trader, ordered by how many of their quests you can act
  on. The row scrolls.
- **The legend is in the toolbar**, a bar-and-name chip per state. On a panel too narrow to fit all
  six beside the view buttons they fold into one **Legend** chip carrying the six glyphs in their
  colours, with the full names on hover.
- **Badges** in a box's corner: a gold **K** for a quest on the Kappa list, a blue **C** for one
  you must finish before Collector can be accepted on *your* install - including the quests behind
  those. On a stock install that is a wide net: 252 of 558 quests, and it contains all 136 Kappa
  quests, so most K boxes wear a C too. Where a quest mod has trimmed Collector it becomes small
  and sharp - four direct requirements instead of a hundred and thirty-six. Settings chooses which
  mark the boxes wear, so you can have the wide one, the narrow one, or both.

**Click a quest** for its detail: status, level and trader chips, why it is locked - and the quest
in your way is a link straight to it - then the wiki page, and:

- **Build**, for a Gunsmith-style quest: the weapon, every number the game will check it against,
  the parts it insists on, and a worked-out build that meets them. Its own section below.
- **Take with you** - the items you must be carrying, named properly, with how many you already
  hold and what happens to each: handed in, found in raid, left in place, planted.
- **Route** - the whole chain between you and this quest, in the order you can do it, each step a
  link, carrying "started is enough" or "N h after" where a prerequisite asks for that.
- **Objectives** with live progress bars, a "Show on the map" link where there is a pin, and each
  one opening the item it names.
- **Rewards**, each marked by kind and each opening the item it gives you. An unlocked trader offer
  names the item rather than saying "a new offer".
- **Unlocks** - what finishing it opens up.

Every quest named in there is a link; **Back** at the top retraces them, and returns to the list you
came from. The box the panel is about is outlined in the accent colour. The panel collapses with the
chevron.

## Gunsmith builds

A Gunsmith quest tells you it wants ergonomics of at least 62 and recoil no worse than 250. It does
not tell you which parts get you there, and working that out by hand across the hundreds of parts
that fit a given weapon is the job people install a quest tracker to avoid.

The **Build** section does it for you. For each weapon a quest names:

- **What the game will check** - every threshold, the parts the quest insists on by name, and the
  categories it insists on ("a suppressor", "a tactical device").
- **A build that meets them**, part by part, with the slot each one goes in. **Every row opens the
  game's own inspect window**, because a name alone does not tell you what to look for in a
  trader's list.
- **What that build scores** on each number the quest cares about, so you can see the margin.

A quest can ask for more than one weapon, and each gets its own block.

**The heading tells you how much to trust it**, which matters more than it sounds:

- **Suggested build** - every requirement the mod can check is met, and there are none it cannot.
- **Build meets every checkable requirement** - the numbers are met, but the quest also constrains
  something the mod cannot score. Eyeball that one on the gun before you hand it in.
- **Closest build found** - no complete build was found. It says which threshold it missed and by
  how much, which separates "this quest is hard" from "your parts are limited".

**And then the game is asked.** Every heading above is the mod checking its own arithmetic. A build
your profile can actually assemble carries one more line, computed by the game's own hand-in test on
the exact preset *Save as a weapon preset* would write:

- **The game will accept this build**, with the ergonomics, recoil and weight the game measured, or
- **The game would refuse this build: recoil 265.98 where the quest wants ≤ 250** - the first test
  the trader's code fails, in the trader's own numbers, or
- **Game check: checkable once the quest is accepted** - the test needs the quest's own condition,
  and the game gives the client that only for quests you hold.

Two things the check cannot know, and says on the line: the gun it tests is **unloaded** and at
**full durability**, while the trader weighs the real one loaded and tests its real durability - so
a build that passes a weight limit by a few grams can still be refused with a magazine in. Both
halves of the mod have to be 1.14.0 or newer for this line; an older server sends no items to
assemble it from, and the line says so instead of guessing.

Two things worth knowing about how the builds are worked out:

- **Nothing in a build is spare.** Every part is checked by taking it off and re-deriving the
  quest's requirements on what is left; if the smaller gun still passes, the part goes. So a build
  is not merely correct, it is stripped - which matters when you are the one buying the parts.
- **Assembled size is measured extended.** Where a quest limits the grid size of the finished
  weapon, a stock that folds or collapses is reported rather than assumed: the mod tells you the
  reduction and judges on the extended figure. A build that fits extended fits whatever you then do
  with the stock.

### It only suggests parts you can actually get

A build made of parts you cannot buy is not advice, it is a taunt. So the build is worked out
against **what you specifically can obtain right now**:

- **Parts already in your stash** are free, and the row says so. If the part is fitted to another
  weapon it says which one - *"fitted to your equipped MDR"* - and still shows you the price,
  because stripping the gun you raid with is your decision to make, not the mod's.
- **Parts a trader will sell you at your current loyalty**, with the price. Locked assortments and
  quest-locked offers are already excluded; nothing is suggested that the trader would refuse you.
- **Parts on the flea**, if you have flea access, with the price marked as an estimate - a trader
  price is a fact, a flea price is a guess.
- **Anything else is left out of the build entirely** rather than quietly recommended.

**When no build can be made from what you can get, it says why**, and the three cases it
distinguishes are the three that change what you do:

```
'Gunsmith - Part 8' (AKS-74N): blocked - trader level -
   AK Zenit PT Lock from Skier at loyalty 2
   closest attempt missed: recoil 280.67, needs <= 275 (short by 5.67)
```

That is a goal, not a dead end. "You need Skier at loyalty 2" is worth knowing; "no build found" is
not. Where a quest *names* a part you cannot buy, the row says the quest names it - nothing can
avoid that one.

The panel shows both: the build anyone could make, and the build **you** can make, with what it
costs you in roubles and how many of the parts you already own.

### Send the build to your gun

**Save as a weapon preset** puts the build into the game's own build list, so you can load it onto the
weapon in one click at the workbench - and the game's preset screen will offer to **buy the parts you
are missing**, through its own purchase flow rather than anything the mod does to your profile.

It appears immediately - no restart - unless the game's build list cannot be reached, in which case
the line under the button says to go back to profile select. Nothing is written unless you press the
button.

Every preset the mod saves is named `QT: <quest> - <weapon>`, and each part of that earns its place:

- The prefix makes the mod's presets obvious in a list that is otherwise yours, and keeps them clear of
  one you named yourself. The game de-duplicates saved builds **by name**, so a preset called
  "Gunsmith" would have silently replaced yours.
- The weapon is there because a quest can ask for more than one, and each gets its own build. Without
  it, saving the second would have replaced the first.
- Saving the same build again replaces the mod's own preset rather than piling up a second.

**This is the one thing the mod writes to your profile**, and the presets are ordinary saved builds
once written - see *Uninstalling* below.

### It keeps getting better on its own

The mod ships with a set of worked-out builds and keeps looking for cheaper ones - a small, fixed
budget per server launch, in the background and off the startup path, so it never delays the server
coming up. Anything it finds is written down and appears the next time you launch. It only ever
replaces a build with a **better** one, so the answer never gets worse, and it never changes under
you mid-session.

To push it harder, set `QUESTTREE_TRAIN=1` in the shell you launch the server from. That turns a
launch into a training session: it searches until it has spent 340,000 attempts or 5 hours - the
point past which a measured eleven-hour run found nothing further - using half your cores, and writes
down every improvement as it finds it. `QUESTTREE_TRAIN_THREADS=N` overrides how many cores it uses;
`QUESTTREE_TRAIN_ATTEMPTS` and `QUESTTREE_TRAIN_HOURS` move the two caps, and `0` removes one.
Entirely optional - a normal launch is unaffected, and stopping the server early loses nothing,
because every improvement is written as it is found.

## The other views

Whole screens, next to Tree. Every quest row in them opens that quest's detail with the tree framed
around it.

- **Do next** - every unfinished quest, scored and ranked. Quests you have accepted come first,
  then everything else in order of what it is worth doing: what the quest pays, how much of the tree
  it opens, whether your traders can reach the offers it unlocks, how close it is to done, how close
  it is to startable, whether Kappa needs it, and how much work it looks like.

  **Pick what you are playing for** from the selector at the top - Balanced, Kappa path, Fast
  levelling, Trader unlocks, Item hoarding - and the order changes with it. A quest paying 200k
  experience and opening nothing tops Fast levelling and sits near the bottom of Kappa path, which
  is the whole reason it is a choice rather than a fixed answer.

  **Every row says why it is there** - *"unlocks 12 · 96k XP · 80% ready"*. A ranking has nothing to
  be checked against, so the test is whether a row's reason justifies its position; if it does not,
  you can see that rather than having to trust a number.

  Rows name their map, and one line tells you when several of your top ten are in the same place,
  because travel between maps is the real cost. Quests you cannot start yet are included and ranked
  lower, with the gate named - a quest two levels away is worth knowing about before you vendor
  something it wants. It ranks rather than filters, so it stays useful whatever state your profile
  is in.

  **Click a row and it opens in place** with everything you need to actually do the quest: its
  objectives with live counts, the items to take and how many you already hold, where to go, what it
  pays and what it unlocks - and, for a Gunsmith quest, the whole weapon build with prices and what
  is already in your stash. Open as many as you like and compare them; the list stays where it was.
  It is the same content the detail panel shows, drawn from the same code, so the two cannot
  disagree.
- **Items** - every item an unfinished quest will ask for, how many you hold, whether found-in-raid
  is required, and which quests want it. **This is the "do not sell that" list.** It deliberately
  includes items for quests you have not unlocked yet, because that is exactly when you would
  otherwise vendor them. Click the item for the game's own inspect window, or the quest names
  underneath it to open that quest.
- **Kappa** - tabs across the top: the Collector hand-in checklist against your stash
  (found-in-raid, which is what Collector actually requires), the full Kappa quest list and your
  progress through it, and - only where a mod has changed Collector - what it actually requires on
  your install. Collector items open the inspect window like every other item list.
- **Settings** - below.

## Controls

| | |
| --- | --- |
| Drag | Pan the tree or the map |
| Mouse wheel | Zoom, anchored to the cursor (and scroll the tab row) |
| `F` | Fit the current tab on screen; on the map, fit the floor |
| `M` | Jump to the quests you can work on |
| `X` | Focus: only what you can work on, and everything within reach of it |
| `C` | Chains: fold each single-file run of quests into one box, or unfold them all |
| `/` | Focus the search box; `Enter` opens the first match, `Esc` leaves the box |
| `[` `]` | On the map, the floor below or above |
| `Esc` | Close the hint, then the quest detail, then the tracker |
| `?` button | Show the controls hint again |
| `Ctrl+Q` | Open or close the tracker from anywhere in the menu (rebindable in F12) |

## Settings

The Settings view, in six sections, and the same values in BepInEx's F12 menu. Each section resets
to its own defaults from the last row in it.

- **Tree** - compact layout, two-line titles, **collapse chains**, whether the trader stripe shows,
  prerequisite lines and their opacity, hover dimming strength, how far **Focus** reaches, the
  visible-quest ceiling, which quest badges the boxes wear (Kappa, Collector, or both), the
  trader-card overview threshold, and the hide filters (unobtainable / completed / traderless).
- **Do next** - which goal the ranking optimises for, and how many rows the tab lists.
- **Behaviour** - open on the map or remember the last view, tooltips, hover sounds, in-raid zone
  harvesting, the controls hint, and reloading `kappa-quests.json`. The open-tracker shortcut is
  rebound in F12.
- **Map** - accepted quests only, which sidebar sections show, how many "do next" rows, which pins
  carry their name at rest (hover only / in progress and available / all), sidebar width, and the
  artwork rotation and mirror overrides.
- **Colours** - the six status colours (in progress, available, completed, level gated, locked,
  **failed**) and the accent, with presets in-game and any hex colour in F12, plus **Restore the
  pre-1.10 colours** for anyone who preferred the old palette.
- **Trader colours** - the stripe colour per trader, one row each, generated from the traders your
  install actually has, so modded ones are in the list too.

## Notes

- **Quest mods are supported.** Everything is read from live server data, so modded traders,
  quests and maps appear automatically; a modded map gets its pins the first time it is raided.
- **Large installs stay responsive** - only the quests actually on screen are built.
- **Kappa list** comes from the Collector quest's own requirements in the quest database, and is
  what badges quests "Kappa" in the tree and the detail. If a mod has changed Collector on your
  install, the Kappa tab says so and still shows the real list. To track your own list instead, put
  quest names in `kappa-quests.json` and hit Reload in Settings. The file ships empty on purpose -
  `[]` - because the database's own list is the default.
- **Collector badge** answers a different question from the Kappa list: not "is this on the
  community's Kappa list" but "does Collector, as it exists on this install, depend on this". It is
  read from the loaded quest graph, so it follows your quest mods, and it is the same set the Kappa
  tab lists under "To unlock Collector". On an unmodded install it is the larger of the two sets,
  since it also counts everything the Kappa quests themselves require.
- **Zone harvesting** reads the map's quest trigger volumes a few seconds into a raid and sends
  them to the server once. It touches nothing in the raid and can be turned off in Settings.
- **Not a cheat.** It only displays quest data you would otherwise look up on a wiki, and pins the
  places the game itself marks for those quests.

## Troubleshooting

- **No taskbar button** - check `BepInEx\LogOutput.log` for lines starting `QuestTree`. The load
  line carries the build stamp (`QuestTree 1.17.0+abc1234: loaded.`), which is what to quote.
- **The tracker is slow to open** - `LogOutput.log` carries one line per open with the time in each
  phase: `QuestTree: panel open - 499 ms: quests: server 29, quests: parse 233, graph 61, ...`. The
  quest fetch and its parse run on a worker thread, so the game is drawing frames through those two.
  The server half logs each per-profile request's time on the first one and again whenever it passes
  200 ms. Quote both lines rather than a feeling.
- **Tree only shows unlocked quests, no pins** - the server half is missing, or you are on someone
  else's server that does not have it. See "Both halves are required" above.
- **`Http response status code: NotFound` on `/questtree/...`** - same cause: the server you are on
  has no Quest Tracker server half, or one older than this client.
- **Server refuses to load the mod** - the server half must match your SPT version. This build
  targets SPT 4.1.5.
- **A map says "not harvested yet"** - it is a map the release did not ship zones for. One raid on
  it fixes that for everyone on the server.
- **A map has no image** - that map has no DynamicMaps image, which is expected for a few of them.
  The quest list still works.
- **`tarkovdev-last-failure.txt` in `SPT_Runtime\user\mods\QuestTree`** - tarkov.dev could not be
  reached, so the server stops asking for a day rather than paying the attempt on every boot. Delete
  the file to retry sooner. A successful download lands beside it as `tarkovdev-quests.json` and
  refreshes itself after seven days; delete that one to refresh it now.

## Uninstalling

Delete `BepInEx\plugins\QuestTree` and `SPT_Runtime\user\mods\QuestTree`.

Nothing the mod writes is needed to play, and nothing breaks by removing it. Two things outlive the
two folders:

- **Any weapon preset you saved** with *Save as a weapon preset*. Those are ordinary saved builds in
  your profile, named `QT: <quest> - <weapon>`. Delete them in the game's own build list.
- **`BepInEx\config\com.takov.questtree.cfg`**, your settings. Harmless, and deleting it is optional.

Everything else - the harvested map zones, the solved weapon builds, the downloaded objective
locations (`tarkovdev-quests.json`, `objective-gps.json`) and the `tarkovdev-last-failure.txt` stamp
beside them - lives in the two folders above and goes with them.
