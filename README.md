# Quest Tracker

An in-game quest progression tree for SPT — every quest in the game laid out as a branching tree,
coloured by your progress, with per-trader tabs, search, quest details, and Kappa container tracking.

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

Click a quest for its objectives, rewards, prerequisites and a link to its wiki page. The detail
panel can be collapsed with the chevron to give the graph its space back while keeping the quest
selected.

## Tabs

- **All** — every quest. On a quest-modded install this is thousands of quests, so search or a
  trader tab is usually the faster way in.
- **One tab per trader**, each showing your completion count. The row scrolls.
- **Kappa** — the Collector hand-in checklist with what is in your stash (and whether it is
  found-in-raid, which is what Collector actually requires), plus the Kappa quest list and your
  progress through it.

## Settings

Next to the Close button, and also in BepInEx's F12 menu — they are the same values.

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
