# Quest Tracker 1.13.3

Install over 1.13.2 - replace both DLLs, keep your config. Both halves must come from this archive.

## No waiting

**The tracker opens in half a second instead of a second and a half.** The first open of a session
used to spend most of its time turning the map's SVG image into a mesh, on the game's main thread,
with the screen frozen behind a loading notice. That work now happens on a worker thread: the map
tab appears at once with its quest list and says *Rendering the map...* where the picture goes, and
the picture arrives on its own about a second later. Measured on a real profile: 1,390 ms down to
499 ms for the open itself.

**The server starts seven seconds sooner.** Building the map markers read every map's loot table
off disk during the server's own startup - five to seven seconds, on the boot path. It runs in the
background now, and finishes well before anyone is at the map tab. Each map's loot table is also
read once per server run instead of on every rebuild.

**A tarkov.dev outage no longer costs every boot.** When the site cannot be reached, the server
remembers that for a day instead of paying two attempts and a pause on every start; delete
`tarkovdev-last-failure.txt` in the mod's folder to try sooner. A successful download now refreshes
itself after a week, where before it was kept forever.

## Under the hood

- The panel logs one line per open with the time spent in each phase, and the server logs how long
  each per-profile request took the first time and whenever it exceeds 200 ms. These are the numbers
  the next release will be judged against.
- The hover card no longer searches for its canvas every frame.
