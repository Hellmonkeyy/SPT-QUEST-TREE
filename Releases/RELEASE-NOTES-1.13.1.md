# Quest Tracker 1.13.1

Install over any 1.12.x - replace both DLLs, keep your config. 1.13.0 was built but never
published, so everything in it is here too.

The short version: **five features in the mod did not work at all**, and four of them failed
silently - nothing logged, nothing threw, the UI just quietly did nothing or the wrong thing. They
were found by reading the whole codebase rather than by anyone reporting them, which is the
uncomfortable part: they had been shipping broken for several releases and looked fine.

## Five things that never worked

**The pre-raid check went blank on a whole map.** If a single requirement on a map could not be
placed - no known spawn, no zone - the check returned a verdict with no lines at all. So the map
drew no summary, no TAKE WITH YOU list, and a plain ready-up button, on maps where it had plenty to
say about everything else. It now drops the one requirement it cannot place, says so in a line, and
keeps the other N honest.

**Searching for a trader matched nothing.** The search box says "Search quests or traders", and
typing `Prapor` returned "No matches" across 800-odd quests. The trader names were being loaded
*after* the search index was built, so the index was always built from an empty dictionary. The same
bug left the trader colour palette unprimed, which is why tab colours could shift between sessions.

**21 vanilla quests lost every item-spawn pin.** A quest that says "find this item" without naming a
map got its pins filed nowhere. *Delivery From the Past* and 20 others showed no item pins at all.

**Expanding a Do-next row threw you back to the top of the list.** The scroll position was being
cleared by the very redraw that was supposed to restore it. This one had a comment explaining the
failure it was written to prevent, and it had never once worked.

**Tree boxes said `0/2` on a quest you could hand in.** Hand-over objectives were counted as never
done, so a box could read `0/2` while the Do-next row for the same quest said "ready to hand in".
They agree now.

## Weapon builds

**Saved presets are echoed back correctly.** Sending a build to your gun could fail on the round
trip because the reply was serialised with the wrong converter. Also, on a quest that wants more
than one gun - *Gunsmith Part 21*, *Old Friend's Request* - every build was saved under the same
name, and the game de-duplicates by name, so you kept one gun and lost the rest. Presets are now
named `QT: <quest> - <weapon>`.

**The item watchlist was looking for a template that does not exist.** The dollars currency id had
one wrong character and had never matched anything, so dollar amounts were not recognised as money
you hold. Roubles and euros were fine.

**The builds themselves are re-seeded.** 56 of the 60 shipped builds come from a fresh training run;
4 are kept from the previous seed where it was still better. In plain terms the builds are about as
expensive as before - 9,986,014 to 9,981,074 roubles across all sixty, which is well under a
percent - so this is not the part to get excited about.

## Correction, 17 September 2026

This release originally claimed that 9 of the 60 builds were *provably* the cheapest possible, that a
provably minimal build stops being searched, and that a line containing `UNSOUND` in the server log
would mean the mod had caught one of its own bounds being wrong. **All three are withdrawn.** The
machinery behind them was removed from the code the same day this release went out: measured over
the shipped builds, the whole apparatus contributed under 0.05% of their cost, its bounds were wrong
three times in one session, and nothing a player could do depended on the answer. The 1.13.1 build
you download here still contains it and still prints those lines; they are harmless, and they are
not evidence of anything. From 1.13.2 they are gone. One check of that family is kept, because it
is cheap and you can act on it: every part in a build is still proven necessary by removing it and
re-checking, so "nothing here is spare" remains true.

## Also

- Hideout-craft unlocks were systematically under-ranked in Do-next, because a hideout area id was
  being read as a trader id.
- 16 event quests were not flagged as events, disagreeing with the lock reason shown on the same
  card.
- The profile panel no longer silently drops constraints it cannot score - they are reported as
  unchecked rather than assumed to pass.
- Six non-playable "locations" were being accepted as maps.
- Trader assortments were being regenerated many times per request for data that does not change.
- The README claimed nothing is written to your profile. That has been false since 1.12.0 - saved
  presets go into your profile and survive uninstalling the mod - and it now says so, with
  instructions for removing them.
