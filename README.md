# Quest Tracker

An in-game quest planner for SPT. It opens on a map of the raid you have picked in the matchmaker,
with every quest you can do there pinned where it happens, and behind that sits the whole quest
progression - every quest in the game, including the ones you have not unlocked - as a branching
tree coloured by your progress. Plus the things a wiki cannot tell you: what your quests will ask
you not to sell, why a quest is locked, what to do next, and how far you are from Kappa.

**Built for SPT 4.1.5.**

---

## Installing

Drag the `BepInEx` and `SPT_Runtime` folders from the archive into your SPT install folder (the one
containing `EscapeFromTarkov.exe`) and let them merge. You should end up with:

```
[SPT folder]\BepInEx\plugins\QuestTree\QuestTree.dll
[SPT folder]\BepInEx\plugins\QuestTree\kappa-quests.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\QuestTreeServer.dll
[SPT folder]\SPT_Runtime\user\mods\QuestTree\zones\*.json
```

Start the server first, then the game. A **Quest Tracker** button appears in the bottom taskbar.

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

- **Do next here** - the map's unfinished quests ranked the way you would act on them (in
  progress, then ready to hand in, then closest to complete), with the one fact that matters per
  row.
- **Quests on the map** - every quest with an objective or item on this map, one row each. Click a
  row for its objectives and rewards inline, and the map flies to its pins.
- **Items to find here** - quest items that spawn on this map, and whether you already hold them.
  **Refresh** on the header line re-reads your stash.
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

The **Tree** button, top right, is the whole progression. Each quest is a box with a status bar on
its left edge - green in progress, amber available, dark green completed, grey locked - and lines
run through the column gaps to what it unlocks.

- **Zoom out** and the boxes simplify: title only, then a short code (`GUN-3`, `EM-4`) that keeps a
  chain readable at any distance. Two-part titles ("Gunsmith - Part 3") take two lines.
- **Hover a quest** and it lights up with everything it requires and unlocks, while the rest of the
  tree fades away from it - harder the closer you are zoomed, softer in the overview.
- **Focus (`X`)** cuts the tree down to the quests you can work on now, plus what they need and
  what they unlock.
- **Tabs** along the top: All, then one per trader, ordered by how many of their quests you can act
  on. The row scrolls.

**Click a quest** for its detail: status, level and trader chips, why it is locked, the wiki page,
then **Requires** (with "started is enough" or "N h after" where a prerequisite asks for that),
**Route** (the whole chain between you and it, in the order you can do it), **Objectives** with
live progress and a "Show on the map" link where there is a pin, **Rewards** and **Unlocks**. Every
quest named in there is a link; **Back** at the top retraces them, and returns to the list you came
from. The box the panel is about is outlined in the accent colour. The panel collapses with the
chevron.

## The other views

Whole screens, next to Tree. Every quest row in them opens that quest's detail with the tree framed
around it.

- **Do next** - every unfinished quest, ranked: in progress first, then ones you already hold every
  item for, then partial holdings, then the rest by nearest gate. It ranks rather than filters, so
  it stays useful whatever state your profile is in.
- **Items** - every item an unfinished quest will ask for, how many you hold, whether found-in-raid
  is required, and which quests want it. **This is the "do not sell that" list.** It deliberately
  includes items for quests you have not unlocked yet, because that is exactly when you would
  otherwise vendor them.
- **Kappa** - the Collector hand-in checklist against your stash (found-in-raid, which is what
  Collector actually requires), the full Kappa quest list and your progress through it.
- **Settings** - below.

## Controls

| | |
| --- | --- |
| Drag | Pan the tree or the map |
| Mouse wheel | Zoom, anchored to the cursor (and scroll the tab row) |
| `F` | Fit the current tab on screen; on the map, fit the floor |
| `M` | Jump to the quests you can work on |
| `X` | Focus: only what you can work on, and its neighbours |
| `/` | Focus the search box; `Enter` opens the first match, `Esc` leaves the box |
| `[` `]` | On the map, the floor below or above |
| `Esc` | Close the hint, then the quest detail, then the tracker |
| `?` | Show the controls hint again |

## Settings

The Settings view, in four sections, and the same values in BepInEx's F12 menu. Every section has a
"Reset this section to defaults" row.

- **Tree** - compact layout, two-line titles, zoomed-out codes, prerequisite lines and their
  opacity, hover dimming strength, the zoom levels the boxes simplify at, the visible-quest ceiling,
  Focus, and the hide filters (unobtainable / completed / traderless).
- **Behaviour** - open on the map or remember the last view, tooltips, hover sounds, in-raid zone
  harvesting, the controls hint, and reloading `kappa-quests.json`.
- **Map** - accepted quests only, which sidebar sections show, how many "do next" rows, which pins
  carry their name at rest (hover only / in progress and available / all), sidebar width, and the
  artwork rotation and mirror overrides.
- **Colours** - the four status colours and the accent, with presets in-game and any hex colour in
  F12.

## Notes

- **Quest mods are supported.** Everything is read from live server data, so modded traders,
  quests and maps appear automatically; a modded map gets its pins the first time it is raided.
- **Large installs stay responsive** - only the quests actually on screen are built.
- **Kappa list** comes from the Collector quest's own requirements in the quest database, and is
  what badges quests "Kappa" in the tree and the detail. If a mod has changed Collector on your
  install, the Kappa tab says so and still shows the real list. To track your own list instead, put
  quest names in `kappa-quests.json` and hit Reload in Settings.
- **Zone harvesting** reads the map's quest trigger volumes a few seconds into a raid and sends
  them to the server once. It touches nothing in the raid and can be turned off in Settings.
- **Not a cheat.** It only displays quest data you would otherwise look up on a wiki, and pins the
  places the game itself marks for those quests.

## Troubleshooting

- **No taskbar button** - check `BepInEx\LogOutput.log` for lines starting `QuestTree`. The load
  line carries the build stamp (`QuestTree 1.8.2+abc1234: loaded.`), which is what to quote.
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

## Uninstalling

Delete `BepInEx\plugins\QuestTree` and `SPT_Runtime\user\mods\QuestTree`. Nothing is written to your
profile.
