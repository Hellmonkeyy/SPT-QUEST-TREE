# Quest Tracker 1.12.1

A fix release. Install over 1.12.0 - replace both DLLs, keep your config.

## Task items count as items you have

The pre-raid check told you to pack a task item, and there is no way to pack one.

Tarkov keeps quest items in two containers of their own - the game's Tasks screen calls them
**"Task items on character"** and **"Task items in stash"** - and an item flagged as a quest item
cannot be dragged into a rig at all. The game carries them into the raid for you. The mod was only
looking through your gear and your stash, so a set of plans handed to you by the very quest that
wants them planted read **"0 of 1 · elsewhere"** on the map's TAKE WITH YOU list, and the ready-up
button said **1 TO PACK** about something you already had and could not lose.

Both containers now count as held on you. The rows say **task item** rather than "on you", so you can
see which part of the list the game is carrying for you and which part you have to pack yourself.

A partial holding is still amber, and still honest: two of something with one in the task container
is not ready, and the row says so.

## Also

- The map's amber pre-raid summary no longer says "none of it is on you" on a map where something
  already is - it says how much of the list you have.
- On a found-in-raid requirement the row stays with the plainer "on you". The task-item count has no
  found-in-raid split, and the label would otherwise claim the game was carrying a copy the quest
  will refuse.
- Both halves' assembly version was stamped 1.11.0 while the mod reported 1.12.0. They agree again -
  both DLLs now read `1.12.1`, and the build stamps the commit they came from.

## Nothing to configure

No new settings, no schema change, no profile migration. The profile payload stays at schema 2 on
purpose: the new count is additive, so a 1.12.1 client talking to a 1.12.0 server simply falls back
to the older wording rather than losing the whole on-you/in-stash split.
