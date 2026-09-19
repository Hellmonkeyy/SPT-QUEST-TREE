# Quest Tracker 1.18.2

Install over 1.18.1 - replace both DLLs, keep your config.

## The pre-raid button tells you what you are carrying now

The button on the ready-up screen was painted once from a cache that lived as long as the session,
and only the tracker's Refresh link ever refetched it. So packing the item and reopening the tracker
left the button saying "1 TO PACK" over a sidebar that said everything was already on you. It asks
the server again every time the matchmaker screen appears, and repaints when the tracker refreshes.

## Pressing ready no longer pauses the game

The first pre-raid check after a server start cost 745 ms against 10 and 6 ms for the two after it -
one-time work (the locale table, the zone index, the location lookups, the serialiser's metadata)
that the game asked for on its own thread while you were looking at the ready-up screen. The server
now builds one full answer at boot, off the path that holds the boot up, so the first real request
pays for none of it. Both the warm-up and the first request log where their time went, phase by
phase, so a regression here says so in the log.

## Two quests asking for the same gun get two answers

The game's own verdict on a build was cached per build, but it is computed per quest - it resolves
the quest's condition, and three of its answers are about the quest rather than the gun. Two held
quests stating the same requirement therefore shared one verdict, and the one that got there first
spoke for both. The cache is keyed by quest and build now. Relatedly, "checkable once the quest is
accepted" is what that verdict says before you accept: accepting a quest with the tracker open
cleared everything else and left that line behind, and it clears now too.

## The repair search values barter parts the way the shared search does

When a shared build has a part this profile cannot get, the server searches for a replacement within
reach. That search valued a barter part - or one no trader sells at all - at the handbook price at
face value, while the shared search values the same part at three times handbook. It was therefore
biased threefold towards barter parts, and the line comparing the two searches' costs was comparing
two different questions. Both use the same fallback now. A new boot line reports what the repaired
builds' objective values their parts at beside what the bill charges for them, and says what the
difference is made of: the parts a search has to put a number on and nobody is charged money for.

## A build that cannot be re-seated is replaced, not defended

A remembered build whose parts cannot be seated on this install - a mod changed the gun, a slot is
gone - was described as costing zero, and zero is unbeatable, so that entry blocked every smaller
build found for it for the life of the install. It is treated as absent now: a verified build
replaces it, and the log says which weapon and why. On the training path the remembered build is
also checked against the quest's requirement before anything is measured against it, which is what
the rest of the code already claimed happened on every path.

## Long lists and crowded maps are capped

Every row on the Kappa tab and every pin on a map is a game object, and both lists grow with what
your mods add. The Kappa tab's three lists draw at most 150 rows each, with a "+N more" tail when
there were more. A map draws every started quest's markers, however many, plus 200 of the rest;
when it has to drop any, it says so in the log once per map. Which 200 is now decided by the same
order the quest list uses, so a completed quest's pin is dropped before one you could accept and
walk to.

## Under the hood

- The per-profile parts report - 436 ms of every boot when it was measured, three log lines per
  profile, read by no request - runs on a background thread after the part a boot does have to wait
  for. The mod's share of the boot before the server answers anything falls from 677 ms to 280 on
  this install.
- Two caches read outside the lock that publishes them are `volatile`; the boot-path survey and the
  solver's dry run copy the collections a zone harvest can rebuild under them.
- A hover card that cannot read the profile now says so once per session instead of silently
  dropping its counters.
- Fields and properties nothing read, and doc comments attached to nothing - one summary was sitting
  eleven classes above its own class - are gone; the csproj's note now says how to count what is left.
- The README describes 1.18.0 and 1.18.1: the trader-cash objective, GP coins as money, the charged
  copies you do not hold, the narrow toolbar.
- Packaging refuses a release whose CHANGELOG entry is missing or whose notes are a stub, and writes
  the built archive's sha256 into the notes, since re-zipping does not reproduce the published bytes.

Nothing on this list has been watched happening on screen: the client changes are reasoned from the
code and the server changes from its log.

Archive sha256: 8a5fa8fac6d36e5267850c30dd53f0933405a018b52e8fc7cc7e21cbe697c14b
