# Quest Tracker

An in-game quest planner for SPT. Every quest in the game — including the ones you have not
unlocked — laid out as a branching tree coloured by your progress, plus the things a wiki cannot
tell you: what your quests will ask you not to sell, what you can do on the map you are about to
load into, why a quest is locked, and how far you are from Kappa.

**Built for SPT 4.1.4.**

---

## Installing

Drag the `BepInEx` and `SPT_Runtime` folders from the archive into your SPT install folder (the one
containing `EscapeFromTarkov.exe`) and let them merge. You should end up with:

```
[SPT folder]\BepInEx\plugins\QuestTree\QuestTree.dll
[SPT folder]\BepInEx\plugins\QuestTree\kappa-quests.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\QuestTreeServer.dll
```

Start the server first, then the game. A **Quest Tracker** button appears in the bottom taskbar.

### Both halves are required

The client half works alone, but without the server half it can only show quests you have **already
unlocked** — the toolbar will say so.

**On Fika, the half that matters is the one on the machine hosting the server.** If you join
someone else's server, *they* need `SPT_Runtime\user\mods\QuestTree` installed, not you. A client
talking to a host without it gets an unlocked-only tree, and the host's console logs
`[UNHANDLED][/questtree/kappa]`. Both halves must also come from the **same download** — the mod
tells you in the Kappa tab if the versions disagree.

## Why there is a server half

The game client is never sent quests you have not unlocked. The server's own quest-list endpoint
filters down to quests already in your profile plus those whose prerequisites you have already met —
exactly the part a progression tree needs to see past.

The server half serves the complete, unfiltered quest list, reads your stash for the Kappa item
checklist, and reads the Kappa quest list out of the quest database.

## Controls

| | |
| --- | --- |
| Drag | Pan the tree |
| Mouse wheel | Zoom (and scroll the trader tab row) |
| `F` | Fit the current tab on screen |
| `M` | Jump to the quests you can work on |
| `/` | Focus the search box |
| `Esc` | Close the hint, then the quest detail, then the tree |
| `?` | Show the controls hint again |

Zoom is anchored to the cursor, so the quest you are pointing at stays put.

**Hover a quest** and it lights up along with everything it requires and everything it unlocks,
while the rest of the tree dims — the quickest way to read a chain.

**Click a quest** for its objectives, rewards, prerequisites, why it is locked, and a link to its
wiki page. Objectives show live progress where the game is tracking it. The detail panel collapses
with the chevron to give the graph its space back while keeping the quest selected; the X clears the
selection outright.

## The quest tabs

Along the top, and the row scrolls — wheel or drag it.

- **All** — every quest in the game, including ones you have not unlocked. On a quest-modded install
  this is thousands, so search or a trader tab is usually the faster way in.
- **One tab per trader**, each showing your completion count.

## The views

Grouped together at the **top right**, next to Close. These are whole screens rather than a slice of
the quest graph. Click one again to go back to the tree.

- **Do next** — every unfinished quest, ranked by how close you are to finishing it: in progress
  first, then ones you already hold every item for, then partial holdings, then the rest by nearest
  gate. It ranks rather than filters, so it stays useful whatever state your profile is in.
- **Maps** — unfinished quests grouped by the map they happen on. The answer to "I am loading into
  Customs, what can I do there". Quests that can be done anywhere are left out on purpose, so they
  do not bury the ones that change what you do with the raid.
- **Items** — every item an unfinished quest will ask for, how many you hold, whether found-in-raid
  is required, and which quests want it. **This is the "do not sell that" list.** It deliberately
  includes items for quests you have not unlocked yet, because that is exactly when you would
  otherwise vendor them.
- **Kappa** — the Collector hand-in checklist with what is in your stash (and whether it is
  found-in-raid, which is what Collector actually requires), plus the full Kappa quest list and your
  progress through it.
- **Settings** — below.

## Settings

In the view buttons at the top right, and also in BepInEx's F12 menu — they are the same values.

- **Hide unobtainable / completed / traderless quests** — filters, applied before layout so they
  genuinely make the tree smaller.
- **Compact layout** — smaller boxes packed tighter; roughly 1.6x as many quests on screen, at the
  cost of the objective line on each box. Off restores the original spacing exactly.
- **Draw prerequisite lines** — turning this off is a noticeable speed-up on dense chains.
- **Max visible quests** — ceiling on how many boxes exist at once. Only reachable zoomed right out.

## Notes

- **Quest mods are supported.** Everything is read from live server data, so modded traders and
  quests appear automatically.
- **Large installs stay responsive** — only the quests actually on screen are built.
- **Kappa list** comes from the Collector quest's own requirements in the quest database. If a mod
  has changed Collector on your install, the Kappa tab says so and still shows the real list. To
  track your own list instead, put quest names in `kappa-quests.json` and hit Reload in Settings.
- **Not a cheat.** It only displays quest data you would otherwise look up on a wiki.

## Troubleshooting

- **No taskbar button** — check `BepInEx\LogOutput.log` for lines starting `QuestTree`.
- **Tree only shows unlocked quests** — the server half is missing, or you are on someone else's
  server that does not have it. See "Both halves are required" above.
- **Server refuses to load the mod** — the server half must match your SPT version. This build
  targets SPT 4.1.4.

## Uninstalling

Delete `BepInEx\plugins\QuestTree` and `SPT_Runtime\user\mods\QuestTree`. Nothing is written to your
profile.
