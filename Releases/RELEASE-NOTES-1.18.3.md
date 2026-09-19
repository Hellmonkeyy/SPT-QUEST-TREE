# Quest Tracker 1.18.3

Install over 1.18.2 - replace both DLLs, keep your config.

## Opening the tracker stops freezing the game on four more fetches

The quest list has been fetched on a background thread since 1.17.0, but it was never the only round
trip an open made. Four more went out one at a time on the thread that draws frames, each at its own
point in the build: the profile payload (your level, trader state, objective counters and lock
reasons), the Kappa checklist, the pre-raid check, and the map markers. Every one of them was a
blocking request under a fifteen-second cap, so a server that was slow to answer - or that accepted
the connection and then sat on it - stopped the game rather than the loading notice.

They are fetched, parsed and cleaned on one more worker now, in sequence, during the same wait the
quest list already had, with the loading notice up and frames still being drawn. By the time each
call site asks for its payload it is already in the cache the call site reads, so it answers without
a request.

Whichever of the four is already cached is not asked for again - the test used is each getter's own
cache rule, including a remembered failure and the map markers' one-minute hold-off after the server
answers empty. And a payload that does not arrive is not cached at all, which means its getter
behaves exactly as it did before any of this existed: it goes and asks, on the main thread, behind
the loading notice. That is the fallback, and it is the whole of it.

## The open line says where the time went

The one log line per open now names each payload's two halves as `profile: prefetch` and
`profile: prefetch parse`, beside the quest list's own pair. The `profile: server` and
`kappa: server` entries further along the line are the main-thread twins: they are still marked
where those requests used to be made, and them reading 0 is what says the prefetch covered them.

A payload printing 0 in its prefetch pair was covered by a wait that was already happening, not
free - the two workers run at the same time, so the line charges each measured half against the
frames actually spent waiting and keeps the phases summing to the total.

## A stale answer cannot land on top of a newer one

Each of the four carries a counter that moves every time something announces the payload has
changed - a hand-in, a quest accepted, the tracker's Refresh link, the ready-up screen, a zone
harvest, a change of profile or server. A request already in flight cannot be cancelled, so the
counter is compared before the answer is published: an answer asked for before the change is dropped
and the getter asks again, rather than overwriting what the change was announcing.

A batch that finished while the panel was shut is discarded outright. It was fetched for a stash you
have had every opportunity to change since - a closed panel is exactly when items get moved - and
nothing bumps a counter when you pack a magazine, so that answer would pass the check and latch. The
pre-raid check's promise is that if it says you have enough on you, you have enough; a minutes-old
answer cannot be allowed to speak for it.

## A prefetch that runs out of time says so

The batch may keep starting requests for twenty seconds, so four hung requests cannot hold the worker
past the forty seconds the open waits. If the open does give up on the payloads, the log says which
half it gave up on and the wait is charged to `payloads: gave up` rather than to `quests: main` - that
phase is meant to be the small remainder the game still spends its own thread on, and forty seconds
landing in it would read as the main thread having spent it.

## Under the hood

- The four helper scripts moved into `tools/`, which is where packaging now looks for the DTO check.

Nothing on this list has been watched happening on screen: the changes are reasoned from the code and
from the log line the open writes. What to look for on the first open with this build:

- The open line's `quests: main` should be small - tens of milliseconds - with the payload work
  showing up in the `: prefetch` pairs instead.
- The `profile: server` and `kappa: server` entries later in the same line should read 0. A non-zero
  one means that payload was not prefetched, and the line above it says why.
- `payloads: gave up` should not appear at all against a working server.
- No `UnobservedTaskException` in `Player.log`. One would mean an abandoned request faulted after its
  cap fired.

Archive sha256: bfb531f8ed3908801a87a3750d80fed4ce97470b7e209c0568551623ced5d660
