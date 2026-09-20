# Quest Tracker 1.18.5

Install over 1.18.4 - replace the server DLL; the client is unchanged apart from the version it
reports, and the two halves check each other's version, so replace both DLLs from this zip.

## The server console says what a player needs and nothing else

A normal boot wrote **109** `Quest Tracker` lines into the server console and its log. Every one of
them was at Information, because that is the level SPT's shipped `sptLogger.json` prints, and most of
them were written to be read while working on the solver rather than by someone running the mod: the
per-profile parts bill, the pricing coverage across eleven traders, the flea audit, the solver's dry
run, the per-map zone counts, one line for every one of sixty weapon builds. The five lines that say
whether the mod is actually working were buried in the middle of them.

A normal boot now prints **about five**:

```
Quest Tracker 1.18.5+abc1234: serving 850 quests to the client mod. Diagnostics off (QUESTTREE_DEBUG=1 turns them on).
Quest Tracker: 60 weapon build(s) remembered from a previous boot.
Quest Tracker: looking for smaller weapon builds in the background - 390 attempt(s) across 2 thread(s) ...
Quest Tracker: built 174 item-spawn and 675 objective markers across 13 maps.
Quest Tracker: launch search done - 390 attempt(s) in 2.2 minute(s) across 2 thread(s) (179/min), 1 smaller build(s) found.
```

Plus a line whenever something is wrong or degraded - a weapon-build cache that does not fit this
install, tarkov.dev unreachable so the map has fewer pins, a zone harvest refused, a request that took
longer than 200 ms - and one line per raid event, a zone harvest being saved or a weapon preset being
written.

## The diagnostic lines are one variable away

Nothing was deleted. Forty-eight log lines became **diagnostic** lines: Information when
`QUESTTREE_DEBUG` is set to `1`, `true`, `yes` or `on`, and Debug otherwise. Their text did not
change, so anything that greps for one still finds it.

- `tools/server-debug.cmd` in the source repo sets the variable and starts the server in a visible
  console. Double-click it.
- Or set `QUESTTREE_DEBUG=1` in whatever starts the server. It is read once, at startup - an
  environment variable rather than a config file, because a config file can be packaged into a release
  by accident and a variable cannot.
- Training (`QUESTTREE_TRAIN=1`) turns them on by itself. A training run exists to be watched, and a
  training console with the progress lines hidden would be a blank window for five hours.

The stamp line always says which mode the boot is in, so a quiet console explains its own quietness
rather than looking like a mod that failed to load.

Two warnings moved with them, both of them checks the code makes against itself rather than news for a
player: the one comparing the copy budget against the served rows, and the one comparing the panel's
count of preset changes against the search's. Each fires only when two internal counts of the same
thing disagree, neither is anything a player can act on, and both are still printed under
`QUESTTREE_DEBUG`. Every genuine warning and every error is untouched.

## What to look for

Nothing here changes what the mod does, only what it writes down.

- Start the server normally. Between the mod's load line and the first raid there should be about five
  `Quest Tracker` lines, and the first one should end with `Diagnostics off (QUESTTREE_DEBUG=1 turns
  them on).`
- Run `tools/server-debug.cmd` instead: the stamp line should end with `Diagnostics on
  (QUESTTREE_DEBUG).` and the boot should be as talkative as 1.18.4 was.
- Open the tracker, hand a quest in, raid a map. The panel, the pins and the builds should be exactly
  as they were.
