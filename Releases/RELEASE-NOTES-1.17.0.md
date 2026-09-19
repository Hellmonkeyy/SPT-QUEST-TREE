# Quest Tracker 1.17.0

Install over 1.16.0 - replace both DLLs, keep your config.

## The quest list loads off the main thread

Opening the tracker used to freeze the game for the quarter second it took to fetch and parse the
quest list. That work now runs on a worker while the loading notice is up; the game keeps drawing.
If the list ever fails to arrive in time, the tracker builds the unlocked-only tree it has always
shown without the server half and asks again on the next open, instead of waiting on a dead notice.

## Under the hood

- The README describes everything shipped since 1.13.2: folded quest chains, the game's own verdict
  on a build, the collapsing legend, the new settings and log lines.
- Two release gates: packaging refuses when the client and server disagree about a wire field (the
  cause of the 1.12.2 preset fault), and when the shipped build history is stamped by an older
  solver (the cause of every install re-solving on every boot in 1.13.2). Both proven able to fail.
