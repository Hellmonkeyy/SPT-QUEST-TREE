# Quest Tracker 1.18.4

Install over 1.18.3 - replace both DLLs, keep your config.

## The map stops flickering and resetting when you click a dropdown

Opening the map picker, closing it again and re-picking the map already shown are the three
commonest clicks in that view, and none of them changes the map. Each one threw the picture, the
mask, the pan handler, every place name and up to two hundred-odd pins away and drew exactly the
same thing again - a visible flash, and your pan and zoom back to a fresh fit each time.

The map viewport is now kept across a rebuild that would draw the identical map: same map, same
floor, same quest statuses, same marker payload, same picture, same settings, same selected quest,
same panel size. The sidebar and the pickers are cheap and are still rebuilt every time. Nothing
about panning or zooming changed, and F still refits the floor.

A rebuild that would draw something different still throws the viewport away and builds it properly
- switching map or floor, a quest's status moving, hiding or showing quests, a change of profile,
and any change to a setting.

## The pin count says how many pins are on the map

The selected map's sidebar printed the marker payload's total, which is not what you are looking at:
with accepted-only pins on, or on a map whose spawn list runs past the 200-pin cap, "340 pins" sat
over a map with twelve on it and read as pins that had failed to draw. It now says "12 of 340 pins"
whenever fewer are drawn than the payload holds, and plain "340 pins" when they all are.

## A server that has stopped answering costs one wait, not four

A server that accepts the connection and then sits on it is the worst case the fetching path has.
Since 1.18.3 the four payloads - profile, Kappa, pre-raid check and map markers - are prefetched on
a worker, and a prefetch that gets no answer leaves each payload uncached, so the call site goes and
asks for itself. Against a hanging server that meant the worker proved the server was silent and
then the game's own thread proved it again, fifteen seconds at a time, once per payload.

After a prefetch **times out**, those payloads are answered as unavailable for thirty seconds
instead - no request, nothing cached, nothing latched. The map draws no pins and the pre-raid cue
says nothing, which is what they already do when the server cannot be reached; the window expires by
itself, and the next open past it fetches exactly as before. A refused connection or a missing route
is unchanged: those come back in milliseconds and cost nothing worth avoiding.

A batch the panel has given up waiting for is now cancelled too, so its worker stops issuing
requests the call sites are already re-issuing - it used to duplicate up to four round trips,
including the pre-raid check's walk of the whole inventory, against a server already too slow to
have met the deadline.

## Refresh always asks

Every Refresh link on the four tabs - Do next, Maps, Items, Kappa - now clears those hold-offs
before it refetches. Pressing it is you saying "ask now", and without this the Kappa tab could say
the server cannot be reached over a button that did nothing at all for half a minute. A hand-in or a
quest accepted deliberately does not clear them: that says the answer changed, not that the server
started answering. A change of profile or server clears them, because it is a different server.

## Under the hood

- One combined warning line per batch names the payloads that did not answer, rather than one line
  per payload about the same hanging server.
- The open-time line counts only requests that were actually issued. A payload the budget skipped or
  cancellation stopped no longer prints a `: prefetch 0` pair next to a `: server` phase about to be
  the real fifteen seconds, and the round-trip count no longer reports four where one was made.
- A settings generation counter, bumped by every setting change and every section reset, feeds the
  map's keep-or-rebuild test. One number rather than a list of entries, because the pin colours reach
  the map without the map reading a setting at all - named entries meant an F12 colour change left
  every pin in the old palette beside a legend rebuilt in the new one.

Nothing on this list has been watched happening on screen: the changes are reasoned from the code.
What to look for on the first session with this build:

- Pan and zoom the map, then open the map dropdown and close it again. The map should not move, and
  there should be no flash of the picture.
- Re-pick the map already selected: same thing, nothing moves.
- Switch map, and switch floor on a multi-floor map. Both should repaint properly.
- Press F. The floor should refit.
- Change a pin or status colour in the F12 menu with the map open. The pins should come back in the
  new colour, not just the legend.
- Turn accepted-only pins on. The sidebar should read "N of M pins" with N the number you can count
  on the map.
- Only with a server that hangs: one warning line in the log saying the payloads "did not answer
  within 15s", and no fifteen-second freeze of the game after it.

Archive sha256: 0f1dd03f09975f4124781f051472c58a19d5a0d77c8952003ef39a32b05412a1
