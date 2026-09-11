# Quest Tracker — the road to a public release

## Context

The mod is at 1.8.5: 17,000 lines across a BepInEx client and a .NET 10 server half, three code
audits, zero build warnings, no TODOs left in the source. It does enough that other people would want
it, and the decision is that it gets published.

Three things stand between here and there, and none of them is code quality:

1. **The project has no backup.** `git remote -v` is empty. Eight releases and months of work exist
   in one folder on one disk.
2. **Five releases have never been watched running**, and there are bugs from 1.8.5 not yet
   described. Publish unverified work and strangers find the bugs instead of you.
3. **Nothing about it is ready to publish**: no LICENSE file, `ModMetadata.Url` deliberately null, no
   mod page, no screenshots.

One feature comes first: the **pre-raid checklist**, the natural finish to what 1.8.5 started.

**Tests were considered and declined.** A legitimate call at this size, recorded here for one
consequence only: the play test is the sole safety net this project has, which is what makes Phase 1
load-bearing rather than housekeeping.

### Assumptions that were wrong, and what replaced them

Four things this roadmap originally took for granted, checked against the code and the SPT wiki
export and corrected. Recorded so they are not re-assumed later:

- **The destination is The Forge (sp-mod.com), not "the hub".** The wiki is explicit that it is the
  main source for mods and *the only one SPT supports*. Mod pages there carry a **Versions tab**, and
  users are told to install only the version matching their SPT. That makes the release a
  per-SPT-version artifact, not a single download.
- **`gh` is not installed on this machine.** Phase 0 cannot assume `gh repo create`.
- **"Do next here" already lists unaccepted quests**, and the full map list already colours them
  amber with their trader named. A proposed "Accept first" section would have duplicated both, and
  was cut. What is actually missing is one empty line of explanatory text — see Phase 2.
- **Map scoping is by `node.LocationKey`** (`MapView.GroupByMap`), not by harvested zones, so the
  checklist works on a map nobody has raided yet. It also skips `AnyLocation` quests, which matters.

### Order, size, and what gates what

| | Phase | Rough size | Gated by |
| --- | --- | --- | --- |
| 0 | Back up the repo, add `ROADMAP.md` and `LICENSE` | Minutes | Nothing. Do it today. |
| 1 | **1.8.6** — your bugs, then the verify pass | Unknown until you describe the bugs, plus one play session | Needs the bug list and one session in-game |
| 2 | **1.9.0** — the pre-raid checklist | Four commits, no server change | Phase 1, specifically item 4 |
| 3 | Publish 1.9.0 on The Forge | Mostly your writing and screenshots | A clean Phase 1 session |

Phases 0 and 1 are independent, so the backup does not wait on anything. Only Phase 2 has a real
dependency, and it is a specific one rather than general caution — see "Reuse" below.

---

## Phase 0 — Back the work up. Today, before anything else.

Nothing else in this plan matters if the disk dies.

**The blocker:** `gh` is not on this machine, so pick a route.

- **Recommended:** `winget install GitHub.cli`, then `gh auth login`, then
  `gh repo create quest-tracker --private --source=. --push`. Private, not public: the code goes out
  through The Forge on your schedule, not by accident today. Git identity is already configured
  (`Hellmonkeyy`).
- **If you would rather not install anything:** create the repo in the browser, then `git remote add`
  and `git push -u origin master`, which will prompt for credentials once.
- **Zero-account stopgap, thirty seconds:** `git bundle create` onto another drive or a cloud folder.
  A bundle is a single file holding the entire history and clones back exactly. Worth doing right now
  regardless of which route above you take later.

Also in this phase, because they are one-liners and Phase 3 needs them anyway:

- **Write this roadmap to `ROADMAP.md`** at the repo root, so it is versioned with the code it
  describes and travels with the backup rather than living only in a plan file.
- **Add `LICENSE`** (MIT) at the repo root. `ModMetadata.License` already claims MIT and no file
  backs that claim.
- **Delete the repo's copy of `spt_wiki_export.md`** (738 KB, untracked). The real one lives in
  `Tarkov Knowledge\`; this duplicate was nearly committed by a `git add -A` during 1.8.5.
- **Back up `Tarkov Knowledge\` too.** Those notes are the project's memory, and they are as unbacked
  as the code was.

## Phase 1 — 1.8.6: the bugs, then the verify pass

**Scope is unknown until you describe the bugs you saw.** That is the first input needed, and per the
standing preference the fix goes in directly rather than returning as another plan.

Then the backlog gets watched once, in one session. The mod is currently removed from `C:\Games\SPT`
(backed up in `Releases\removed-from-SPT-2026-09-08\`), so this starts by installing
`Releases\QuestTracker-1.8.5-SPT-4.1.5.zip`.

**Read the log before judging anything by eye.** `BepInEx\LogOutput.log` carries one line a session
when `GameStyle.MeasureWidth` rejects TMP's answer, naming both numbers. That line is the difference
between "the measurement is fixed" and "the fallback is quietly carrying it", and the two are
indistinguishable on screen.

Then, in order of how likely each is to be wrong:

1. Toolbar legend, trader tabs and a quest's detail chips all sized to their text.
2. The three Settings dropdowns: the open list covers what is under it, nothing covers the list.
3. Kappa tabs switch sections; a quest row lands on that quest framed in the tree, including as the
   first thing done after opening the panel.
4. Map: a quest with a carried item names it properly in **Bring**, with a held count.
5. Ctrl+Q on the ready-up screen and the final countdown; the button on the ready-up screen.
6. Item rows open the inspect window, owned or not; an unexamined item showing `?` is correct.
7. Server: clean boot at quest schema v3.

**Nothing new is stacked on top until this is clean.** That is the entire lesson of 1.8.4.

## Phase 2 — 1.9.0: the pre-raid checklist

The question the mod cannot answer is the one asked when it would help most: *I have picked this map
and I am about to press Ready. What do I need?*

1.8.5 built the per-quest half (`QuestSummary.AddItemsToBring`). This aggregates it per raid.

### One new section: "Take with you" — `UI/MapView.cs`

Every item a quest on this map needs you to be **carrying**, with held counts, red when you have
none. Distinct from the existing "Items to find here", which is spawn-based: that section is what the
map gives you, this is what you must bring through the door.

**No server change.** The data landed in 1.8.5: `QuestPayloadBuilder.IsCarriedItemCondition`
(`LeaveItemAtLocation`, `PlaceBeacon`) fills `TargetItems`/`TargetItemNames`, and
`ProfilePayloadBuilder.CollectOwnership` counts those templates through `IsAnyItemCondition`.

**Where it goes, exactly.** `MapView.BuildSelectedMap` builds the sidebar on a `y` cursor with
`listX`/`inner` already in scope, in commented blocks: `// ---- header`, then `// ---- do next here`,
then "Quests on *map*", then the items block at the `ModSettings.ShowItemsSection` test, then
credits. **Take with you** is a new block immediately before that items block, so the two item
sections sit together and the quest sections stay together.

**It needs a settings toggle**, because every other sidebar section has one and a section that cannot
be turned off would be the odd one out. Mirror `ShowItemsSection` exactly — it is five mechanical
edits: the property, the `config.Bind` call, the `Entries` array, the `SettingChanged += Raise` line
(all four in `ModSettings.cs`), and the `Toggle(...)` plus the `ResetLink` argument list in
`SettingsView.BuildMapSection`. Missing the `SettingChanged` line is the specific bug this codebase
has hit twice before: the setting binds, and changing it from F12 does nothing.

Three details that decide whether it is right:

- **Scope by `node.LocationKey`**, exactly as `GroupByMap` does, so it works on a map with no
  harvested zones.
- **`GroupByMap` skips `AnyLocation` quests.** A carried-item objective you can complete anywhere
  would silently never appear. Include those in every map's checklist — you can do them on this raid,
  so they belong on this list.
- **Follow the existing `StartedOnly` toggle**, so the section agrees with the two above it rather
  than inventing its own idea of which quests count.

### The line that makes it a checklist

A section of rows is another list. What turns it into a checklist is **one line at the top of the
sidebar** giving the answer without reading anything: *"Ready — everything this map needs is on
you"*, or *"Missing 1 of 4: MS2000 Marker"*, in the shared warning colour.

It is nearly free, since it comes from the same aggregation as the section, and it is the entire
reason the feature is worth having at the moment the ready-up screen is on screen. It also stays
truthful when the section is empty, which is the common case: *"Nothing to bring for this map."*

**Where:** in the `// ---- header` block of `BuildSelectedMap`, on the line after the map name, using
`AddDetailLine` rather than `AddAt` — `AddAt` ellipsises at the column edge, and this is a sentence,
not a label. That distinction is already written into that file as a comment, learned the hard way.

### One branch, not a new section: the empty reason line in Do next

Stated precisely, because the first version of this plan oversold it. The full "Quests on *map*"
list already shows an unaccepted quest as amber with its status glyph and its trader
(`MapView.AddQuestRow`), so the information is not missing from the screen.

The narrow gap is in **Do next here**: `DoNextView.Detail` covers in-progress, ready-to-hand-in,
partly-held, locked and level-gated, then falls through to `""`. An available quest therefore sits in
a ranked "do this next" list as the only row that does not explain itself, beside rows that do. One
branch closes it: *"not accepted — take it from Ragman first"*.

Worth doing because it is one branch and it improves the Do next tab everywhere, not just the map.
Not worth more than that: the list is capped at `MaxDoNextRows` (8 by default), so on a busy map the
row may not be shown at all. This is a polish item riding along with the section above, not a reason
for the release.

### Reuse rather than a second implementation

`QuestSummary.AddItemsToBring` already aggregates carried items with held counts and the
found-in-raid rule. Lift that aggregation into a shared collector next to `ItemWatchlistView.Collect`,
which is the established shape, so the per-quest detail and the per-map checklist can never disagree
about what a quest wants. Rows follow the rule 1.8.5 settled: item rows inspect via
`GameStyle.InspectItem`, quest rows open the quest.

**This is why Phase 1 comes first, and not only for tidiness.** `AddItemsToBring` shipped in 1.8.5
and has never been seen running. Refactoring it into a shared collector before anyone has confirmed
it produces the right names and counts would build the new feature on an unverified foundation, and a
wrong result would then appear in two places instead of one. Verify item 4 of the Phase 1 list before
touching this.

### The entry point is the point

`TrackerAccess.Show` already passes the selected raid location and `QuestTreePanel.PreselectRaidMap`
opens the map for it. Opening from the ready-up screen should land on the checklist, not merely on
the map.

**Keys are out of scope, and worth knowing why before anyone asks:** the mod holds no key data of any
kind. It would mean tarkov.dev's `neededKeys` through the existing `TarkovDevClient`, and that API
returned 422 on every attempt across this entire session. A candidate for later, behind its own
fallback — never something a release depends on.

## Phase 3 — Publish 1.9.0 on The Forge

Only after Phase 1 has been through a clean session. Almost all writing and packaging, not code.

- **`ModMetadata.Url`** points at the Forge page. That file's comment already says to set it "when
  there is somewhere real to point at".
- **Version the release against SPT, not against yourself.** The Forge's Versions tab exists because
  users are told to match the mod version to their SPT version. `SptVersion = ~4.1.0` is the claim
  being made; publish 1.9.0 as the SPT 4.1 build rather than inventing a 2.0.
- **Screenshots — needs you in-game.** The map with pins, the tree at a readable zoom, a quest
  detail, the Kappa tab. This is the single largest driver of whether anyone installs a Forge mod,
  and it is the one item nobody but you can produce.
- **The page.** The README is close to the right text already. What it needs on top: the
  both-halves-required warning stated up front, the Fika note (the half that matters is the *host's*
  — this will be the most common support question by a wide margin), and the SPT version stated
  plainly.
- **Attribution, checked before strangers check it for you.** DynamicMaps images are read from the
  user's own install and credited in-view, and the zip redistributes no third-party data: the `zones`
  files are harvested from the game by the mod itself. The gap is that nothing names tarkov.dev or
  tarkovdata, which the mod does fetch from. Add it to the README and the page.
- **A bug-report surface.** The build stamp already exists (`QuestTree 1.9.0+abc1234: loaded.`); tell
  people to quote that line and attach `LogOutput.log`.

**What publishing obligates, stated once so it is a choice rather than a surprise:** `~4.1.0` means
the mod stops loading when SPT 4.2 ships, and Forge users will expect an update within days. The
client half is the exposure — it patches `MenuTaskBar` and `MatchMakerAcceptScreen` and reads private
fields through `Compat`, all of which a game update can move. The server half is far safer. Decide
now whether you are willing to be on that clock; if not, publishing with an honest "4.1 only, updated
when I get to it" note on the page is a perfectly respectable answer.

---

## Deliberately not in this plan

- **Tests.** Declined; see Context.
- **Hideout requirements in the items list.** Ruled out, not deferred: another installed mod already
  covers hideout item requirements, and duplicating it would put two "do not sell that" lists in the
  same game disagreeing with each other. Do not re-propose this.
- **Trader and barter planning.** The leading remaining feature idea now, and reachable from data the
  mod already holds. After the checklist and the release, not alongside them.
- **The audit's deferred list** (`QuestTracker-Audit-2026-09-07.md`). Every item is recorded with a
  reason not to do it yet and none has been observed to bite.

## Execution notes — so none of this is rediscovered

Every item here cost time during 1.8.5 and should cost none next time.

**Builds fail without `SptPath`.** The repo deliberately does not deploy, and neither project
resolves the game DLLs on its own:

```bash
dotnet build "Source/Tarkov-QuestTree/QuestTree.csproj"        -c Release --no-incremental -v q --nologo -p:SptPath="C:\Games\SPT"
dotnet build "Source/Tarkov-QuestTree-Server/QuestTreeServer.csproj" -c Release --no-incremental -v q --nologo -p:SptPath="C:\Games\SPT"
```

The project files are `QuestTree.csproj` and `QuestTreeServer.csproj` — **not** named after their
folders. Keep both commands in one shell script from the first minute; the whole release is a loop of
patch, run it, commit.

**Edit through Python patch scripts, not inline heredocs.** The established workflow is a script that
asserts `s.count(old) == 1` before every replacement, so a failed anchor stops rather than silently
doing nothing. Write those scripts with the Write tool: a bash heredoc containing triple-quoted
Python failed outright this session and cost a round trip.

**Group commits by file, not by idea.** Each file gets patched and built once. In Phase 2 that is
roughly: the shared collector, then `MapView` (section plus summary line), then
`ModSettings` + `SettingsView` (the toggle, five mechanical edits), then the `DoNextView` branch.

**Watch for the doc-comment trap.** Inserting a member directly beneath an existing `///` block
silently steals that comment for the new member. It has happened twice in this codebase, both times
in `ModSettings.cs`, which Phase 2 edits again.

**Commit trailers.** Commits made with Claude carry the `Co-Authored-By` and `Claude-Session`
trailers for that session; the session URL differs per session, so take it from the environment at
the time rather than copying an old one out of `git log`.

**Packaging is a copy and one PowerShell call.** Copy the two DLLs from
`bin/Release/netstandard2.1/QuestTree.dll` and `bin/Release/net10.0/QuestTreeServer.dll` over a copy
of the previous release folder, then `Compress-Archive` the `BepInEx` and `SPT_Runtime` folders.
**The package is 14 files** — check that count, and commit before building the zip so the build stamp
is not `-dirty`.

**Do not re-derive these; they are already written down.** The item-inspect API, the two matchmaker
screens and why the taskbar vanishes on them, the TMP measurement history, and the badge-closure
rules are all in `Tarkov Knowledge\QuestTracker-Knowledge.md` §10. Read that section before Phase 2
rather than decompiling anything again.

## Verification

- **Phase 0:** `git remote -v` shows the private remote and `git log origin/master -1` matches local,
  or a `.bundle` exists off this disk and `git bundle verify` passes. `LICENSE` at the root.
- **Phase 1:** the seven-point list, one session, `LogOutput.log` read first. The outcome goes into
  `Tarkov Knowledge\QuestTracker-Knowledge.md` §10 either way — including what the measurement
  actually does, which is still unknown.
- **Phase 2:** Shoreline and Anesthesia is the known carried-item case. Confirm **Take with you**
  names the marker with the right held count, and that the summary line flips between "ready" and
  "missing" when you move that marker in and out of your stash — that flip is the feature working.
  Confirm an `AnyLocation` carried-item quest appears too, since that is the edge the design
  deliberately handles, and that a map with nothing to bring says so rather than showing an empty
  section. Confirm a quest you have not taken now says so in Do next. Then a raid: what the checklist
  said to bring is what the quest actually wanted.
- **Phase 3:** a genuine clean-install test is available without a second SPT copy — the mod is
  already removed and backed up, so install *only* from the packaged zip, following *only* the Forge
  page text, and confirm both halves load and the tracker opens.
