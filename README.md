# Quest Tracker

An in-game quest planner for SPT. It opens on a map of the raid you have picked in the matchmaker,
with every quest you can do there pinned where it happens, and behind that sits the whole quest
progression - every quest in the game, including the ones you have not unlocked - as a branching
tree coloured by your progress. Plus the things a wiki cannot tell you: what your quests will ask
you not to sell, why a quest is locked, what to do next, and how far you are from Kappa.

**Built for SPT 4.1.6.** Not to be confused with DrakiaXYZ's *QuestTracker*, a different mod that
lists your active quests in raid; the two coexist, and this one's folder is `QuestTree`.

---

## Installing

Drag the `BepInEx` and `SPT_Runtime` folders from the archive into your SPT install folder (the one
containing `EscapeFromTarkov.exe`) and let them merge. You should end up with:

```
[SPT folder]\BepInEx\plugins\QuestTree\QuestTree.dll
[SPT folder]\BepInEx\plugins\QuestTree\kappa-quests.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\QuestTreeServer.dll
[SPT folder]\SPT_Runtime\user\mods\QuestTree\zones\*.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\cache\weapon-builds.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\maps\<map>\*.jpg
[SPT folder]\SPT_Runtime\user\mods\QuestTree\maps\<map>\<map>.map.json
[SPT folder]\SPT_Runtime\user\mods\QuestTree\maps\<map>\<map>-mesh.bin
```

A map's `*.jpg` are its floors and, for a map in 3D, its side pictures (`<map>-side-<N|S|E|W>.jpg`) and
atlas pages (`<map>-atlas-<0..7>.jpg`); `<map>-mesh.bin` is its 3D model. A map without them draws
flat.

The archive also carries this README and the release notes beside those two folders; they are for
reading, not for installing.

The `zones`, `cache` and `maps` files are data the mod ships so a fresh install starts with map
zones already known, weapon builds already solved and whatever map pictures were captured for this
release. Nothing breaks without them, but they come back slowly: a map's zones are learned by
raiding it, its picture by capturing it, and the builds improve a little on each server start. Keep
them.

Start the server first, then the game. A **Quest Tracker** button appears in the bottom taskbar, and
**Ctrl+Q** opens the tracker from anywhere in the menu - including the raid ready-up screen, where
the game hides the taskbar. That screen gets a button of its own too.

### Both halves are required

The client half works alone, but without the server half it can only show quests you have **already
unlocked**, with no pins, no item lists and no Kappa checklist - the toolbar will say so.

**On Fika, the half that matters is the one on the machine hosting the server.** If you join
someone else's server, *they* need `SPT_Runtime\user\mods\QuestTree` installed (the `zones` folder
included), not you. A client talking to a host without it gets the unlocked-only tree, and its
`BepInEx\LogOutput.log` shows `Http response status code: NotFound` on `/questtree/...` requests
and `could not send zones` after a raid. Harmless, but featureless. Both halves must also come from
the **same download** - the mod tells you in the Kappa tab if the versions disagree.

## Why there is a server half

The game client is never sent quests you have not unlocked. The server's own quest-list endpoint
filters down to quests already in your profile plus those whose prerequisites you have already met -
exactly the part a progression tree needs to see past.

The server half serves the complete, unfiltered quest list, reads your stash for the item and Kappa
checklists, reads the Kappa quest list out of the quest database, and keeps the map's zone store.

## The map

The tracker opens on **Maps**, on the map you have picked in the matchmaker when you have picked
one (the sidebar says "Your next raid"), otherwise on the busiest map. Pick a map and a floor at the
top; the map fills the panel and a column on the right lists what you can do there:

- **Do next here** - the map's unfinished quests, ranked by the same score the **Do next** tab uses
  and following whichever goal you picked there, with the one fact that matters per row.
- **Quests on the map** - every quest with an objective or item on this map, one row each. Click a
  row for its objectives and rewards inline, and the map flies to its pins.
- **Items to find here** - quest items that spawn on this map, and whether you already hold them.
  Click one to open the game's own inspect window on it. **Refresh** on the header line re-reads
  your stash.
- A legend under the map name says what the pin shapes and colours mean.
- Credits for the map image and the pin icons.

**Pins are real.** Objective zones are read from the map itself the first time anyone on the server
runs a raid there, and item pins come from the game's own spawn points. The release ships every
official map already harvested, so nothing is needed from you; a modded or custom map is pinned the
first time you load into it (the line above the map tells you how many zones it knows). Pins carry
the quest's status colour, name themselves on hover, and open the quest on click. **Accepted quests
only** narrows the pins and the list to what you have actually taken.

Where a raid has harvested a real position for a quest on a map - a trigger zone, or the spot an item
was found - the second-hand pins placed by *percentage* against somebody else's map picture are
dropped for that quest. A percentage is measured against the image its source used, so on any other
picture it lands somewhere else, and a harvested position is strictly better. A quest the harvest has
not reached keeps all of its percentage pins, because for that quest they are the only positions
anything has.

### Map pictures

**A map's picture comes from one of three places, and you decide the order.** Settings > Map >
**Map pictures come from**:

- **DynamicMaps when it has the map, else my captures** - the default, so an install that has been
  using [DynamicMaps](https://github.com/mpstark/DynamicMaps) sees the map it saw before.
- **my captures when I have one, else DynamicMaps** - your own pictures first, its artwork for the
  maps you have not captured.
- **my captures only** - DynamicMaps is not consulted at all, and a map with no capture shows its
  bounds and its pins.

DynamicMaps is **optional** and nothing of it is bundled, copied or redistributed: its files are
read out of its own folder when you have it, and each map's author is credited under the map. A
picture your host holds is used right after your own capture of the same map, and never before it.

**A map with no picture at all is still a map.** The rectangle a map occupies in game coordinates
and its floor bands are measured during the same in-raid pass that harvests the quest zones, so one
raid on a location - any location, including a modded one - is enough for it to draw: a plain dark
backdrop over that rectangle, the alignment guides on, the floor picker working, and every pin
where it belongs. The line above the map reads `No map picture yet - capture one in raid (Ctrl+F9)`
and the credit line says `Map extent harvested in raid; no picture yet.`

**Ctrl+F9 inside a raid takes the picture.** The game draws the map from straight above, one
picture per floor band, and writes it beside the plugin. It needs *Harvest quest zones in raid* on
(the picture is drawn to exactly the rectangle that harvest measured) and a living player in a raid;
it is rebindable in the F12 menu under **Map > Capture map picture key**, including to nothing at
all, and the Settings tab shows the key it is bound to without rebinding it. The work is spread over
frames - a handful of short hitches, not one long freeze - and takes a second or two per floor.

- **Press it again from somewhere else.** The game streams distant chunks out, so any one capture of
  a large map has regions the camera found empty. Each press fills in what the earlier ones could
  not see, and where two captures cover the same spot the one taken from closer wins, so a map gets
  sharper rather than merely newer. The log line says how much was newly drawn, how much was kept
  and how much is still empty.
- **Roofs are in it.** The topmost floor is photographed from 300 m up, so buildings, roofs and
  shadows draw rather than a set of floor slabs; a floor with another above it is shot from just
  under that one, so the ceiling is what gets cut away.
- **Buildings and smooth ground.** A camera looking at a whole map from above measures a building
  against a kilometre-tall view, and the game's own level-of-detail rules answer that a 20 m
  warehouse is too small to draw at all - which is why early captures had roads and terrain and no
  buildings anywhere. The capture turns that selection off for the length of one render, and puts
  the terrain on its averaged base texture for the same instant, so the ground reads as ground
  instead of a two-metre checkerboard. Both are put back by the statement that changed them: your
  next frame is drawn with your own settings.
- **The colours are muted** and the highlights held back deliberately, so the picture reads as a map
  and a coloured pin is the brightest thing on the screen.
- **A capture can be refused, and nothing is written when it is.** Too dark to be a map, which is
  heavy weather or night: `QuestTree: bigmap "Ground" rendered too dark to be a map (p98 0.0087) -
  nothing was written. Heavy weather or night; try again in daylight.` Or taken under different
  light from the pictures already on disk, which cannot be merged into them without a seam:
  `QuestTree: bigmap was captured under different light than the picture on disk (p98 0.0104 vs
  0.5051 stored) - nothing was changed. Capture at a similar time of day to add to it, or delete
  BepInEx/plugins/QuestTree/captures/bigmap to start over.`
- **Every pixel is an average of four, and the edges are cleaned up.** Each tile is rendered at twice
  the resolution it is kept at, with four-sample multisampling on top of that, and averaged
  back down - a pixel half covered by a roof edge takes the roof's colour rather than the roof mixed
  with nothing. A last pass replaces a pixel that disagrees with all eight of its neighbours by their
  median, which is what removes the specular glints and the single black pixels a smoothing pass
  preserves because it reads them as edges. Zoom in on a railing or a roofline: it is a line, not a
  staircase.
- **Water on the game's own Water layer is painted a flat map blue**, so a river, a pond or the sea
  reads as water rather than as a sheet of reflected sky. Two earlier tries were worse: hiding those
  renderers took the Customs river out of the picture and left its bed showing, and painting every
  renderer whose *shader* was merely named like water put blue slabs over yards, the bridge deck and
  several interior floors, because most of those are wet-surface decals and not water at all. So the
  test is the layer and nothing else - there is no shader-name test left in the capture - and a
  wet-looking surface is left to draw itself against the flat grey reflection below. On a map with
  nothing on that layer the water is photographed exactly as it draws; the capture line says which
  happened, ending either with a count of `water-layer renderers painted` or with `water drawn as
  is`.
- **Flat cyan patches are painted over from their surroundings.** That is a separate pass and a
  *colour* test rather than a shader one: a pixel that comes back with green and blue both high and
  red low is a water quad the render flattened to cyan, and it is filled in from the ground around it
  before the exposure is measured.
- **Glass and transparent effects are left out**, because they are drawn by shaders that expect the
  player's camera behind them and from above they came back as blue streaks over the crane and the
  railway. The crane and the rails under them are what a map should show.
- **Reflective roofs reflect a flat grey** instead of the sky. A wet metal roof with nothing but sky
  above it mirrors it and reads as a hole in the map; the capture gives the scene a neutral grey
  reflection for the render, so a wet roof still looks wet and still looks like a roof. An interior
  with its own baked reflection is left alone.
- **Buildings the game had switched off are switched back on.** EFT hides distant geometry by
  disabling renderers rather than by letting a camera cull them, so a warehouse's roof and upper walls
  were off while its floor was drawn, and a capture showed a patch of ground with a wall outline round
  it. Every one of those renderers is forced visible for the whole of one floor - once before its
  first tile, put back after its last, because a big map holds tens of thousands of them and doing it
  twice per tile would cost more than the render - so roofs draw wherever you took the capture from.
  **Whole objects it had deactivated are switched on too**, not only renderers: a culling volume also
  holds a list of GameObjects it turns off outright, and a roof that is one of those cannot be brought
  back by enabling a renderer. Those are switched on one at a time, each in its own guard, because
  activating an object runs the game's own Awake and OnEnable code and one script that throws must not
  stop the rest of the roofs coming back; each is put back exactly as it was found. The honest trade:
  for the second or two a floor takes, **your own frames** draw that distant geometry too, which makes
  them slower, and the water on the Water layer in them is the flat capture blue. Both undo themselves
  when the floor is done.
- **The ground outside the playable area is cut out of the picture.** The extent is padded and clamped
  past the edge of the world, so a capture takes in a border of hillside and skybox terrain that looks
  exactly like the map and is not part of it. Everything outside the area the game's own navigation
  mesh describes - grown 8 m, so no roof, yard or interior is caught by it - is written as
  **transparent**, fading out over the last few metres, and the panel's own dark plate shows through
  where the map is not. It was a dim grey wash first; a dimmed hillside is still a hillside somebody
  will try to walk to. It also means a chunk the game had streamed out reads as "no picture here"
  rather than as a black building. The per-floor log line says how much of the floor was outside. A
  JPEG cannot carry transparency, so the copies that go to a host or ship in a release are flattened
  onto that same dark plate - the colour the tab draws where a floor has no picture, not black, so a
  picture downloaded from a host is the same tone as the one the player who captured it sees.
- **Capture resolution** is 8192 px on the longest side by default - a quarter of a metre to the
  pixel on Customs, where a vehicle is 16 pixels across - with 4096 and 2048 each a step smaller,
  four times cheaper in file size and in raid frames. None of them draws more than four pixels to the
  metre, the memory budget below may take a big map a step coarser still, and whatever you capture at,
  what is shared with a host or shipped in a release is downscaled to 2048.
- **A floor is captured inside a memory budget**, rather than asked for and hoped for. One floor may
  work in **256 MiB** of arrays and textures - 26 bytes per output pixel while it is being built -
  and when the scale the resolution setting asks for would not fit, the capture brings the pixels
  per metre down half a pixel per metre at a time until it does, to a floor of one. That is why a
  big map can come out a little coarser than the setting says, and the capture header tells you when
  it happened: `4 px/m would need 354 MB a floor, over the 256 MB budget, so 3 px/m (199 MB)`.
  Those are Interchange's numbers on the 965x925 m rectangle a harvest measures for it, and
  Interchange at four pixels to the metre is exactly what died in a raid with "GetPixels: scripting
  array creation failed" and an OutOfMemoryException; Customs at 4 px/m needs 239 MB on its
  1118x539 m, is inside the budget and is untouched, so the captures already on your disk still
  merge. Both figures are the budget's own arithmetic - ceil(span x px/m) per axis at 26 bytes a
  pixel - so a map whose harvested rectangle differs lands somewhere else. The scale a map lands on
  is deterministic - the same map at the same setting always gets the same number - which is what
  lets two captures of it be merged at all.
- **A capture taken by a different build may replace yours rather than add to it.** The meta records
  how a picture was rendered - thirteen things, from the capture light and the level-of-detail switch to
  the multisampling the device actually granted - and two pictures may only be merged when all of it
  matches, or identical ground would become different pixels. When it does not match, the log says so
  and the new capture starts the map over:
  `QuestTree: the capture of bigmap already on disk cannot be added to - it was taken before the
  render recipe was recorded, and this one is rendered
  own-1.5;lod1000;basemap0;water1;cull1;refl1;wr4p;smooth5;despeckle1;reach2;ss2;msaa4;layers2 - so this one
  replaces it.`

**Ctrl+Shift+F9 captures the whole map in one press.** Instead of walking a kilometre of Customs
pressing the other key, this teleports you across a grid of standable spots about 120 m apart,
takes a capture at each - floors, side views and the 3D model, each stop adding what it newly sees - and puts
you back exactly where you pressed it: about 33 stops on Customs (9 x 5 cells of 115 x 100 m,
fewer where a cell has nowhere to stand), and the log's closing line gives the real duration. **Start such a raid with AI set to none: it does not disable bots**, and it
leaves you standing still for a second and a half at every stop. It moves nothing but your position,
it is local to you on a Fika raid, and the log names every stop and what happened there. It also
writes those lines to `captures\<key>\<key>.campaign.txt`, keeping the last twenty runs, because a
game log is gone the moment the game restarts and "it captured 11 of 16 stops" is a thing you want to
still have the next day.

**Or let it capture as you play.** *Capture the map automatically while I play* (off by default, and
in the F12 menu under **Map** rather than in the in-game Settings tab, which only reports whether it
is on) takes a capture every few seconds - *Seconds between automatic captures*, 5 by default,
anything from 2 to 120 - and only once you have moved 15 m since the last one, so a raid spent
walking a map builds its picture by itself. It **will** hitch every few seconds; it is meant for a
raid you have set aside for map-building, not for one you are playing for real.

**The files land in `BepInEx\plugins\QuestTree\captures\<key>\`** - one PNG per floor, a
`<key>.map.json` saying which world rectangle those pixels cover, a `.dist.png` beside each floor that
only the next capture's merge reads, and the campaign journal if you have run one. Deleting a map's
folder starts that map over.

**Labels** are set by Settings > Map > **Map labels**: *All* - the default, the extracts plus the
map's own zone names - or *Extracts only*, or *None*. The zone names only appear once you have zoomed
in, so the wide view stays clean either way. They are read from the scene, they are drawn on captured
pictures only, and DynamicMaps' artwork keeps its author's own labels whatever this says.

A name on a captured map is white text - the accent colour for an extract - on a nearly solid dark
plate, because the thing behind it is a photograph of concrete and roofs and anything lighter could
not be read. It stays the same size on screen at any zoom, sits just above a small dot marking the
exact spot, and is dropped when it would land on a name already drawn. Zone names appear only once
you are zoomed in far enough for them to fit; extract names are drawn at any zoom.

**Every extract a capture found is marked with a green diamond**, whatever the label setting says -
the names can be turned off, the extracts cannot, because on a picture of a map they are the first
thing you look for. The legend under the map names the diamond, and the facts line above the sidebar
counts them, which is also how you tell a capture that found no extracts at all and wants taking
again.

**The same press also builds the map in three dimensions.** After the last picture and before the
meta, the capture casts a ray straight down through the centre of every cell of the map's rectangle -
a one-metre cell, coarser in half-metre steps only for an extent over 4 million cells (so every stock map
runs at 1 m: about 600,000 rays on Customs), batched through the physics jobs - from the same
height the picture's camera stood at, and records what it hit as a height grid per floor band. Then
it walks the scene's renderers for the buildings: anything at least six metres long and two and a
half tall inside the rectangle, on the layers the picture draws, taking the MOST detailed
level-of-detail step whenever its source totals at most a million triangles (and the last real
step, never an impostor card, when it is bigger), reading the triangles either from the mesh directly
or - for the fifth or so that live only on the graphics card - off the card itself, and reducing
each building with our own decimation only where it is past its budget: 20 triangles for every square
metre of its surface (its bounding box's six faces), up to 250,000 a building, and never less than the
footprint rule before this release would have given it. The map's cap is derived per capture from
what its buildings need and what this machine's memory holds (a sixteenth of RAM, an eighth of video
memory, never under three million triangles, never over twenty million) - on a 16 GB machine Customs
keeps every building at its full source or its surface target, about 6.8 million triangles, where it
kept at most three. The log's building line names the cap, what it came from, and how many buildings
sat at their old floor. One line in the log says when
the scene is being held for it, because the map's hidden geometry is switched on for as long as the
build runs and the player can see that happen. The result is quantised to sixteen bits and deflated
into `captures\<key>\<key>-mesh.bin` beside the pictures - on a stock map about 50-90 MB, sized by
the capturing machine and the map's surfaces - and the meta gains a `mesh` block naming it with its SHA-256. It is an
addition, never a condition: a mesh phase that fails loses the mesh and nothing else, a relief with
no buildings in it is a complete file, and every map captured before this release carries on
drawing flat. Automatic capture, which comes round every few seconds, builds it only for a map that
has none yet - the ground grid is the same every time, and the renderer walk is the part that costs.

**The 3D mesh accumulates.** A capture that builds the mesh adds to the one already stored rather
than replacing it: it reads the stored mesh and its identity sidecar, `captures\<key>\<key>-mesh.index`
(which renderer every stored building was read from - a 64-bit hash of its scene path, its bounds, its
submesh range and its geometry - its LOD group and level, how it was stored, and which material every
atlas tile is), skips every building already stored, reads only what is new, degraded (stored over its
limit, as it is, clustered or from a coarser LOD level) or changed, re-plans the triangle budget over
the stored and the new buildings together, fills relief cells this stop could not measure from the
stored relief, appends new textures to the stored atlas pages (only the pages that take a new tile are
re-encoded), and writes the stored buildings and the new ones as one file of the same format. So a
campaign's mesh is every stop's buildings, not the last stop's alone, and after the first stop a stop
costs a few seconds of mesh instead of the whole building phase. A LOD group is only ever stored at one
level: a finer level replaces a coarser one only when this stop read the whole of it. A stored building
is never removed because it was not loaded this time - only replaced - so something genuinely removed
from a map within one game version stays until a rebuild. A stop that added nothing keeps the stored
files as they were. The sidecar is local: it is never uploaded or shipped, the viewer never reads it,
and without it (or with one that does not match the mesh) the next capture simply builds from scratch
and writes one. Three settings, in the F12 menu under **Advanced** only: **3D map: add to the stored
mesh** (on; off rebuilds from scratch at every capture, the last one winning - the old behaviour),
**3D map: rebuild from scratch on the next capture** (a one-shot that turns itself off once a mesh is
written), and **3D map: verify the last campaign stop (debug)**, which at a campaign's last stop also
builds the mesh from scratch into `<key>-mesh.verify.bin` for `python tools/compare-mesh.py
captures\<key>` to hold the accumulated mesh to (every building present, none with fewer triangles,
the relief and the height range held, the textures as sharp). A change of the mod's mesh recipe, the
game version, the map's rectangle, its floors or the render mask rebuilds from scratch once by itself.

**In 3D.** Where a map has been captured with relief - the ground's real heights, measured by
raycast in the same raid that took the picture - the Maps tab opens it as geometry: the captured
picture laid over the ground, with the buildings standing on it. **Drag** to slide the map,
**right-drag** to turn and tilt it, **scroll** to come closer. The floor picker peels the storeys:
choosing the second floor draws it standing on the first and on the ground, so a multi-storey map
reads as a building rather than as a stack of slabs. Pins, extract diamonds and place names sit on
the ground at their real height and keep their size on screen, exactly as they do on the flat map.
The **3D relief** toggle beside the floor picker switches back to the flat picture at any time, and
the same choice lives in Settings and the F12 menu as **Map view**; it is greyed out for a map with
no relief captured yet, and its tooltip says why. A map from DynamicMaps, and a map that is only a
harvested rectangle, has no relief and always draws flat. Two settings do not apply in 3D and are
ignored there: **Mirror map artwork** and **Extra map artwork rotation** - the picture is laid onto
the ground by the coordinates it was measured over, so there is nothing left for them to correct. A
relief file is some tens of megabytes for a map of Customs' size (no host takes one past 512 MB),
and is read in the background; a file with more detail than this machine's graphics card can hold (a
quarter of its video memory at 64 bytes a triangle) is drawn flat, and the log says so. Choosing a
lower floor takes the storeys above it off with the camera's own near plane, set along the cut height -
nothing is clipped or copied on the CPU, so switching floors is one matrix; if one cannot be read, or does not describe the same rectangle as the
picture, the map draws flat and the log says why.

**Sharing is through the host.** A server started with `tools/server-host.cmd` - which sets
`QUESTTREE_ACCEPT_MAPS=1` and nothing else - accepts uploaded pictures, and **Share captured maps**
(on by default) offers each finished capture to it one floor at a time as a JPEG, then its side
pictures (the four oblique views the 3D map textures building walls with) the same way, then its
atlas pages (up to eight 4096 px sheets of the game's own building textures, which the 3D map dresses
the buildings in) at their full size as JPEGs at quality 90 (80 for a page that would otherwise pass
6 MB), then the capture's 3D mesh if it built
one. Every client of that host then picks up the maps it does not
have itself, once per session, the first time the Maps tab reaches a map nothing on that machine
can already draw a picture of - mesh included, so a map somebody else raided opens in 3D on your
machine too. A host without the variable refuses, says so in one line, and is not asked again that
session; captures stay on the machine that took them. An older host that has never heard of meshes
stores the pictures and ignores the rest, which costs one line in the log and nothing else. A solo
player needs none of this - your own captures are read straight out of the folder above. The
limits: one picture a post, up to 2.5 MB a picture, 8 floors and 4 side pictures a map, up to 8
atlas pages a map at up to 6 MB each, and one mesh a capture up to what the host's disk allows. The
host's ceilings are derived from its free disk when it starts: a quarter of it for the whole store (never
under 1.5 GB nor over 32 GB), an eighth of that a map, and that less the map's pictures for a mesh (never
over 512 MB) - its boot log line says the three numbers. Your machine takes host meshes up to a fiftieth
of its RAM (328 MB on 16 GB, 512 MB at most) and keeps up to a quarter of its free disk of other
players' maps (2 to 32 GB; the set installed longest ago makes way), and says both once a session. The
download gets at least three minutes a session and goes on past that while it is still arriving at
1 MB/s or better. A mesh goes up in parts of 16 MiB - an SPT server takes no request body past
30,000,000 bytes - up to 64 of them, and the host joins them and checks the whole against the size the
capture declared; a mesh part or atlas page may take up to 240 seconds a request, and a whole mesh
coming down gets a deadline sized to it (60 s plus the transfer at 512 KB/s, 240 s to 30 minutes). A page that does not get through is
dropped from that upload rather than ending it, and a page that does not arrive on your machine is
fetched again, on its own, the next session. A map you captured yourself is never downloaded back
from the host. A side picture is never worth the map either: one the host cannot use - not a JPEG, too
big, or not a view of the capture it came with - is dropped and the set is served without it, and on
your machine a side that does not arrive at the size its meta states is left out; those walls are
tinted instead. An atlas page likewise: one the host cannot use - not a JPEG, over 6 MB, or not the
size its meta states - is dropped and the set is served without it, a page that does not arrive at
its stated size and sha256 is left out on your machine, and the buildings drawn from it fall back to
the side pictures and tints. A set served without its mesh carries no pages - they only dress the
mesh's buildings. A set whose
capture built a mesh is not served until both the floors and the mesh have arrived, so a borrowed
map never names geometry the host does not hold. A mesh problem costs the mesh and never the map:
a mesh the host can never use - unreadable, not the same rectangle or the same floors as its
pictures, or too big for the host's space - is refused once, the pictures are served without it,
and the map draws flat everywhere, which is what every map did before this release; only a mesh the
host could not *write* leaves the floors waiting, and those are dropped at the host's first start a
day later. The mesh is checked by its sha256 at every hop, and one that arrives on your machine not
matching is left out while the pictures are kept; one that does not arrive at all - a timeout, a
dropped connection - leaves that map as it was for the session and is fetched again, whole, on the
next start. Two players who capture the same map in the same second share a slot on the host, and
the second one is refused rather than merged - its own next capture goes up normally. The credit line under a captured map is ours and names the build and the raid rather than
a licence: `Map: captured in-game with Quest Tracker 1.19.0, 3 captures since 2026-09-19 (10:49)`.

## The tree

The **Tree** button, top right, is the whole progression. Each quest is a box that says what it is
without being clicked: a status mark and the name, then the trader, the level it wants and how many
of its objectives are done - with a bar along the bottom for the one you are on. Marks on the right
say what it pays out: experience, an item, reputation, a trader unlock.

**Six states, and each has a glyph as well as a colour**, so the tree still reads if you are
colour-blind or the box is small: in progress, available to start, completed (dimmed and struck
through, so finished work recedes), **level gated** - every prerequisite quest done but you are
short a level, loyalty or standing - locked behind another quest, and **failed** - the game has
failed or expired it, and the box says whether the trader will let you restart it.

Two marks in the top-right corner cover what a status cannot: **!** on a quest this profile can
never complete (wrong faction, another edition, a seasonal event), and **?** on a quest whose
prerequisite is not in your quest list at all - a quest mod referencing a quest another mod removed
- with the missing id named in the detail panel. Both used to draw as ordinary locked boxes.

Level-gated is its own state because it is the one that changes what you do: "another quest first"
means write it off for now, "you need two more levels" means keep it in mind.

**A box you cannot start says why**, in place of its trader line - `Needs Carbines III`, or `Lv 30`.
That is the single most useful thing a blocked quest can tell you and it used to cost a click.

**A run of quests in single file draws as one box.** Where each quest unlocks exactly the next and
they all come from one trader - a Gunsmith or Weapon Proficiency sequence - the tree folds the run
into its first box: the series name, the trader, how many of the run are done, and a bar for that
count, coloured by the first quest in it still to do. A `+` in the corner says there is more inside.
Click the box to unfold the run; the `–` on its first quest folds it again, and the tree remembers
what you opened for the session.

**A run opens itself when something inside needs seeing**: a failed quest, a quest whose
prerequisite is not installed, a search of three letters or more matching a member's name, or a
member you opened from the detail panel. With *Hide completed quests* on, a part-done run is drawn
quest by quest rather than as a box claiming to hold the finished ones. The **Chains** button and
`C` turn folding off and on - off and on again closes every run you had opened - and the setting
persists.

- **The box is the same box at every zoom.** It does not swap to a title, or to a code, or to a
  coloured bar - it scales, and nothing appears or disappears while you move.
- **Rest on any box** and a card appears with the full name, the level and loyalty it wants, how
  deep in its branch you are, every objective with its live count, and the rewards. No click, and
  the detail panel stays on whatever you were comparing against.
- **Hovering lights the whole chain** - every quest this one waits on, all the way back, and
  everything that unlocks from it, all the way forward, with the rest of the tree fading away.
- **Search highlights in place.** Matches light up, everything else dims, and nothing moves. The
  count reads `12 matches · 830 quests shown`. `Enter` opens the first match.
- **Focus (`X`)** cuts the tree to what you can work on now and everything within four quests of
  it. The reach is a setting, 1 to 10.
- **A coloured slab down each box** says whose chain it is, with that trader's portrait beside the
  quest their chains begin at. Modded traders get a colour of their own, chosen to avoid the status
  colours - so a trader can never be mistaken for a state.
- **Tabs** along the top: All, then one per trader, ordered by how many of their quests you can act
  on. The row scrolls.
- **The legend is in the toolbar**, a bar-and-name chip per state. On a panel too narrow to fit all
  six beside the view buttons they fold into one **Legend** chip carrying the six glyphs in their
  colours, with the full names on hover. Narrower still - under about 1,460 px, where even the
  collapsed chip leaves no room for the line saying how much of the tab you are seeing - the
  left-hand block shortens too: a 160px search box reading `Search  ( / )`, and **Mine (M)** in
  place of **My quests (M)**. The shortcut letter stays in every shortened label, because that is
  what the label is for.
- **Badges** in a box's corner: a gold **K** for a quest on the Kappa list, a blue **C** for one
  you must finish before Collector can be accepted on *your* install - including the quests behind
  those. On a stock install that is a wide net: 252 of 558 quests, and it contains all 136 Kappa
  quests, so most K boxes wear a C too. Where a quest mod has trimmed Collector it becomes small
  and sharp - four direct requirements instead of a hundred and thirty-six. Settings chooses which
  mark the boxes wear, so you can have the wide one, the narrow one, or both.

**Click a quest** for its detail: status, level and trader chips, why it is locked - and the quest
in your way is a link straight to it - then the wiki page, and:

- **Build**, for a Gunsmith-style quest: the weapon, every number the game will check it against,
  the parts it insists on, and a worked-out build that meets them. Its own section below.
- **Take with you** - the items you must be carrying, named properly, with how many you already
  hold and what happens to each: handed in, found in raid, left in place, planted.
- **Route** - the whole chain between you and this quest, in the order you can do it, each step a
  link, carrying "started is enough" or "N h after" where a prerequisite asks for that.
- **Objectives** with live progress bars, a "Show on the map" link where there is a pin, and each
  one opening the item it names.
- **Rewards**, each marked by kind and each opening the item it gives you. An unlocked trader offer
  names the item rather than saying "a new offer".
- **Unlocks** - what finishing it opens up.

Every quest named in there is a link; **Back** at the top retraces them, and returns to the list you
came from. The box the panel is about is outlined in the accent colour. The panel collapses with the
chevron.

## Gunsmith builds

A Gunsmith quest tells you it wants ergonomics of at least 62 and recoil no worse than 250. It does
not tell you which parts get you there, and working that out by hand across the hundreds of parts
that fit a given weapon is the job people install a quest tracker to avoid.

The **Build** section does it for you. For each weapon a quest names:

- **What the game will check** - every threshold, the parts the quest insists on by name, and the
  categories it insists on ("a suppressor", "a tactical device").
- **A build that meets them**, part by part, with the slot each one goes in. **Every row opens the
  game's own inspect window**, because a name alone does not tell you what to look for in a
  trader's list.
- **What that build scores** on each number the quest cares about, so you can see the margin.

A quest can ask for more than one weapon, and each gets its own block.

**The heading tells you how much to trust it**, which matters more than it sounds:

- **Suggested build** - every requirement the mod can check is met, and there are none it cannot.
- **Build meets every checkable requirement** - the numbers are met, but the quest also constrains
  something the mod cannot score. Eyeball that one on the gun before you hand it in.
- **Closest build found** - no complete build was found. It says which threshold it missed and by
  how much, which separates "this quest is hard" from "your parts are limited".

**And then the game is asked.** Every heading above is the mod checking its own arithmetic. A build
your profile can actually assemble carries one more line, computed by the game's own hand-in test on
the exact preset *Save as a weapon preset* would write:

- **The game will accept this build**, with the ergonomics, recoil and weight the game measured, or
- **The game would refuse this build: recoil 265.98 where the quest wants ≤ 250** - the first test
  the trader's code fails, in the trader's own numbers, or
- **Game check: checkable once the quest is accepted** - the test needs the quest's own condition,
  and the game gives the client that only for quests you hold.

Two things the check cannot know, and says on the line: the gun it tests is **unloaded** and at
**full durability**, while the trader weighs the real one loaded and tests its real durability - so
a build that passes a weight limit by a few grams can still be refused with a magazine in. Both
halves of the mod have to be 1.14.0 or newer for this line; an older server sends no items to
assemble it from, and the line says so instead of guessing.

Three things worth knowing about how the builds are worked out:

- **The cheapest build wins, and "cheapest" is a number.** Every part is costed at the lowest cash
  price any trader asks for it at any loyalty level, converted to roubles - so Peacekeeper's dollars
  and Ref's GP coins are counted as the money a trader treats them as, rather than as barter goods.
  A part no trader sells for money at all is costed at **three times its handbook price**, because
  it has to come off the flea and the flea does not charge handbook. Then every separate part you
  have to buy adds a flat **10,000** on top, so a build of thirty cheap parts does not beat one of
  two dear ones on price alone - a trader trip costs you something too. The prices come from the
  database's own trader tables, the same for every player on the same install, which is what makes a
  solved build shippable; `QUESTTREE_PER_PURCHASE` and `QUESTTREE_FLEA_MULTIPLE` move the other two.
- **Nothing in a build is spare.** Every part is checked by taking it off and re-deriving the
  quest's requirements on what is left; if the smaller gun still passes, the part goes. So a build
  is not merely correct, it is stripped - which matters when you are the one buying the parts.
- **Assembled size is measured extended.** Where a quest limits the grid size of the finished
  weapon, a stock that folds or collapses is reported rather than assumed: the mod tells you the
  reduction and judges on the extended figure. A build that fits extended fits whatever you then do
  with the stock.

### It only suggests parts you can actually get

A build made of parts you cannot buy is not advice, it is a taunt. So the build is worked out
against **what you specifically can obtain right now**, and each row says which of seven things the
part is *to you*:

- **already on the gun** - it is on the weapon's own default preset, so there is nothing to get.
- **already on yours** - fitted to a copy of the quest's weapon you own. Not "owned elsewhere":
  already done.
- **in your stash** - loose, which is the only ownership that is genuinely free. A copy fitted to
  some *other* weapon is not free and is not counted as owned; the row names the gun instead -
  *"fitted to your equipped MDR"* - and still shows you the price, because stripping the gun you
  raid with is your decision to make, not the mod's.
- **a price in roubles** - a trader will sell it to you at your current loyalty. Locked assortments
  and quest-locked offers are already excluded; nothing is suggested that the trader would refuse
  you.
- **barter** - a trader has it, but for goods rather than money, so there is no rouble figure to
  print. It is counted as a barter in the total rather than quietly costed at nothing.
- **about N ₽ on the flea** - if you have flea access, with the price marked as an estimate,
  because a trader price is a fact and a flea price is a guess.
- **not sold**, or **needs \<trader> at loyalty N** - and a part in either state is left out of the
  build entirely rather than quietly recommended.

**Fence is not a source.** His stock is randomly generated, rotates on a timer and carries his
mark-up, so it is not a price the next player will see - neither the shared builds nor your own ever
read him. That is why a part with nowhere left to come from says *"no trader but Fence sells
another"* rather than naming him as an option.

**The copies you do not hold are charged.** Where a build fits two or three of the same part, only
the ones you actually have are free, and the row says what you have in terms you can check rather
than as one number that counted the gun's own parts as things you own: *"this build fits 2 of these;
the gun comes with 1, you have 1 loose - another comes from Skier at loyalty 3"*.

**When no build can be made from what you can get, it says why**, and the three cases it
distinguishes are the three that change what you do. The panel says it in a sentence; the server
writes the same verdict as one line in its own log (wrapped here):

```
Quest Tracker: profile 6a1f0d... (level 24, flea open) - 'Gunsmith - Part 8' (AKS-74N): blocked -
trader level - AK Zenit PT Lock from Skier at loyalty 2; closest attempt missed: recoil 280.67,
needs <= 275 (short by 5.67); the shared build needed AK Zenit PT Lock
```

That one is a diagnostic line, so it needs `QUESTTREE_DEBUG=1` (see Troubleshooting) - the panel says
the same thing without it.

That is a goal, not a dead end. "You need Skier at loyalty 2" is worth knowing; "no build found" is
not. Where a quest *names* a part you cannot buy, the row says the quest names it - nothing can
avoid that one.

The panel shows both: the build anyone could make, and the build **you** can make, with what it
costs you in roubles and how many of the parts you already own.

### Send the build to your gun

**Save as a weapon preset** puts the build into the game's own build list, so you can load it onto the
weapon in one click at the workbench - and the game's preset screen will offer to **buy the parts you
are missing**, through its own purchase flow rather than anything the mod does to your profile.

It appears immediately - no restart - unless the game's build list cannot be reached, in which case
the line under the button says to go back to profile select. Nothing is written unless you press the
button.

Every preset the mod saves is named `QT: <quest> - <weapon>`, and each part of that earns its place:

- The prefix makes the mod's presets obvious in a list that is otherwise yours, and keeps them clear of
  one you named yourself. The game de-duplicates saved builds **by name**, so a preset called
  "Gunsmith" would have silently replaced yours.
- The weapon is there because a quest can ask for more than one, and each gets its own build. Without
  it, saving the second would have replaced the first.
- Saving the same build again replaces the mod's own preset rather than piling up a second.

**This is the one thing the mod writes to your profile**, and the presets are ordinary saved builds
once written - see *Uninstalling* below.

### It keeps getting better on its own

The mod ships with a set of worked-out builds and keeps looking for cheaper ones - a small, fixed
budget per server launch, in the background and off the startup path, so it never delays the server
coming up. Anything it finds is written down and appears the next time you launch. It only ever
replaces a build with a **better** one, so the answer never gets worse, and it never changes under
you mid-session.

To push it harder, set `QUESTTREE_TRAIN=1` in the shell you launch the server from. That turns a
launch into a training session: it searches until it has spent 340,000 attempts or 5 hours - the
point past which a measured eleven-hour run found nothing further - using half your cores, and writes
down every improvement as it finds it. `QUESTTREE_TRAIN_THREADS=N` overrides how many cores it uses;
`QUESTTREE_TRAIN_ATTEMPTS` and `QUESTTREE_TRAIN_HOURS` move the two caps, and `0` removes one.
Entirely optional - a normal launch is unaffected, and stopping the server early loses nothing,
because every improvement is written as it is found.

## The other views

Whole screens, next to Tree. Every quest row in them opens that quest's detail with the tree framed
around it.

- **Do next** - every unfinished quest, scored and ranked. Quests you have accepted come first,
  then everything else in order of what it is worth doing: what the quest pays, how much of the tree
  it opens, whether your traders can reach the offers it unlocks, how close it is to done, how close
  it is to startable, whether Kappa needs it, and how much work it looks like.

  **Pick what you are playing for** from the selector at the top - Balanced, Kappa path, Fast
  levelling, Trader unlocks, Item hoarding - and the order changes with it. A quest paying 200k
  experience and opening nothing tops Fast levelling and sits near the bottom of Kappa path, which
  is the whole reason it is a choice rather than a fixed answer.

  **Every row says why it is there** - *"unlocks 12 · 96k XP · 80% ready"*. A ranking has nothing to
  be checked against, so the test is whether a row's reason justifies its position; if it does not,
  you can see that rather than having to trust a number.

  Rows name their map, and one line tells you when several of your top ten are in the same place,
  because travel between maps is the real cost. Quests you cannot start yet are included and ranked
  lower, with the gate named - a quest two levels away is worth knowing about before you vendor
  something it wants. It ranks rather than filters, so it stays useful whatever state your profile
  is in.

  **Click a row and it opens in place** with everything you need to actually do the quest: its
  objectives with live counts, the items to take and how many you already hold, where to go, what it
  pays and what it unlocks - and, for a Gunsmith quest, the whole weapon build with prices and what
  is already in your stash. Open as many as you like and compare them; the list stays where it was.
  It is the same content the detail panel shows, drawn from the same code, so the two cannot
  disagree.
- **Items** - every item an unfinished quest will ask for, how many you hold, whether found-in-raid
  is required, and which quests want it. **This is the "do not sell that" list.** It deliberately
  includes items for quests you have not unlocked yet, because that is exactly when you would
  otherwise vendor them. Click the item for the game's own inspect window, or the quest names
  underneath it to open that quest.
- **Kappa** - tabs across the top: the Collector hand-in checklist against your stash
  (found-in-raid, which is what Collector actually requires), the full Kappa quest list and your
  progress through it, and - only where a mod has changed Collector - what it actually requires on
  your install. Collector items open the inspect window like every other item list.
- **Settings** - below.

## Controls

| | |
| --- | --- |
| Drag | Pan the tree or the map |
| Mouse wheel | Zoom, anchored to the cursor (and scroll the tab row) |
| `F` | Fit the current tab on screen; on the map, fit the floor |
| `M` | Jump to the quests you can work on |
| `X` | Focus: only what you can work on, and everything within reach of it |
| `C` | Chains: fold each single-file run of quests into one box, or unfold them all |
| `/` | Focus the search box; `Enter` opens the first match, `Esc` leaves the box |
| `[` `]` | On the map, the floor below or above |
| `Esc` | Close the hint, then the quest detail, then the tracker |
| `?` button | Show the controls hint again |
| `Ctrl+Q` | Open or close the tracker from anywhere in the menu (rebindable in F12) |
| `Ctrl+F9` | In a raid: take this map's picture for the Maps tab (rebindable in F12) |
| `Ctrl+Shift+F9` | In a raid: capture the whole map, stop by stop, and return you (rebindable in F12) |
| (unbound) | The mesh probe: a temporary diagnostic that does nothing until you bind it - see below |

**The mesh probe is not a feature, and it ships with no key.** Bind one in the F12 menu under
**Advanced > Mesh probe key (throwaway)** to use it (a bare key such as F10 works, and Ctrl, Shift or
Alt held will block a bare binding). It is the diagnostic key of the 3D map experiments: pressed in a raid it
measures whether the game's own meshes can be read back off the graphics card and how much of the map
its colliders actually cover, and pressed in the menu it lists the loaded shaders, cameras and layers
and puts a small test view in the corner of the screen (press again to close it). It writes
`BepInEx\plugins\QuestTree\captures\<map>.meshprobe.txt` and `captures\menu.meshprobe.txt`, and
nothing in the mod depends on it. What it does touch, said plainly rather than as "it changes
nothing": it reads up to twenty of the scene's meshes and asks the graphics card for a copy of one,
without modifying any of them - an earlier version of it modified a mesh's buffer targets and put
them back, and the restoring write crashed the game outright, so nothing in this mod writes those any
more; in the menu the test view is a 512x512 panel in the bottom-left corner that stays until you
press the key again and swallows any click inside it while it is there. Press the key once in the **menu**
before you press it in a raid: the readback test only runs in a raid once it has completed in the
menu, where a crash costs nothing. It is meant to be removed again. Clear it again in the F12 menu
under **Advanced > Mesh probe key (throwaway)**; the in-game Settings tab deliberately does not list
it.

**The keys are split by view.** `F`, `M`, `X`, `C` and `/` are the tree's, and only fire there: on
**Maps** the only keys are `F` and `[` `]`, and on **Do next**, **Items**, **Kappa** and **Settings**
none of them fire at all. They used to fire everywhere, where `F` and `M` did nothing visible and
`X` silently flipped a tree-only setting from a view that does not show the tree. Nothing fires
while the cursor is in the search box, so typing `f` into it searches rather than re-framing.

## Settings

The Settings view, in six sections, and the same values in BepInEx's F12 menu. Each section resets
to its own defaults from the last row in it.

- **Tree** - compact layout, two-line titles, **collapse chains**, whether the trader stripe shows,
  prerequisite lines and their opacity, hover dimming strength, **Focus on what you can work on**
  and how far it reaches, the visible-quest ceiling, which quest badges the boxes wear (Kappa,
  Collector, or both), the trader-card overview threshold, and the hide filters (unobtainable /
  completed / traderless).
- **Do next** - which goal the ranking optimises for, and how many rows the tab lists.
- **Behaviour** - open on the map or remember the last view, tooltips, hover sounds, in-raid zone
  harvesting, the controls hint, and reloading `kappa-quests.json`. The open-tracker shortcut is
  rebound in F12.
- **Map** - accepted quests only, which sidebar sections show, whether quests you have **not**
  accepted are counted when working out what to take into a raid, how many "do next" rows, which
  pins carry their name at rest (hover only / in progress and available / all), sidebar width, the
  artwork rotation and mirror overrides, and the alignment guides that outline the area a map's
  coordinates cover and mark its origin. Plus the map pictures: where they come from, which labels a
  captured one carries, the capture resolution, and whether a capture is offered to the host. The two
  capture keys, and whether the map is captured automatically as you play and how often, are
  *reported* here and set in F12 - this page has no key-binding control and no automatic-capture
  toggle.
- **Colours** - the six status colours (in progress, available, completed, level gated, locked,
  **failed**) and the accent, with presets in-game and any hex colour in F12, plus **Restore the
  pre-1.10 colours** for anyone who preferred the old palette.
- **Trader colours** - the stripe colour per trader, one row each, generated from the traders your
  install actually has, so modded ones are in the list too.

## Notes

- **Quest mods are supported.** Everything is read from live server data, so modded traders,
  quests and maps appear automatically; a modded map gets its pins the first time it is raided.
- **Large installs stay responsive** - only the quests actually on screen are built.
- **Kappa list** comes from the Collector quest's own requirements in the quest database, and is
  what badges quests "Kappa" in the tree and the detail. If a mod has changed Collector on your
  install, the Kappa tab says so and still shows the real list. To track your own list instead, put
  quest names in `kappa-quests.json` and hit Reload in Settings. The file ships empty on purpose -
  `[]` - because the database's own list is the default.
- **Collector badge** answers a different question from the Kappa list: not "is this on the
  community's Kappa list" but "does Collector, as it exists on this install, depend on this". It is
  read from the loaded quest graph, so it follows your quest mods, and it is the same set the Kappa
  tab lists under "To unlock Collector". On an unmodded install it is the larger of the two sets,
  since it also counts everything the Kappa quests themselves require.
- **Zone harvesting** reads the map's quest trigger volumes a few seconds into a raid and sends
  them to the server once. It touches nothing in the raid and can be turned off in Settings.
- **Not a cheat.** It only displays quest data you would otherwise look up on a wiki, and pins the
  places the game itself marks for those quests.

## Troubleshooting

**Which log, and what to grep for.** The client half writes to `BepInEx\LogOutput.log` and prefixes
every line `QuestTree`; the server half writes to the server console and its own log, and prefixes
every line `Quest Tracker:`. A symptom that could be either is worth grepping for both.

**The server console is quiet on purpose.** A normal boot prints about five `Quest Tracker` lines -
the version and quest count, the remembered weapon builds, the background build search and its
result, and the map markers - plus a line for anything that went wrong. Everything the mod measures
about itself (the per-profile parts bill, the pricing and flea coverage, the solver's dry run, one
line per weapon build, the per-map zone counts, the request timings) is a **diagnostic** line, hidden
unless you ask for it. To see them, set `QUESTTREE_DEBUG=1` in the environment the server starts in -
an environment variable rather than a config file, because it cannot be packaged into a release by
accident. Starting the server from Explorer gives a shell no chance to set one, so the source repo
ships `tools/server-debug.cmd`, which sets it and starts the server in a visible console; a one-line
`.cmd` beside `SPT.Server.exe` does the same job. The stamp line says which mode you are in:
`Diagnostics off (QUESTTREE_DEBUG=1 turns them on).` Training (`QUESTTREE_TRAIN=1`) turns them on by
itself, since the whole point of a training run is to watch it. Quoting the diagnostic lines in a bug
report is worth the restart.

- **No taskbar button** - check `BepInEx\LogOutput.log` for lines starting `QuestTree`. The load
  line carries the build stamp (`QuestTree 1.19.0+abc1234: loaded.`), which is what to quote.
- **The tracker is slow to open** - `LogOutput.log` carries one line per open with the time in each
  phase: `QuestTree: panel open - 499 ms: quests: server 29, quests: parse 233, graph 61, ...`. The
  quest fetch and its parse run on a worker thread, so the game is drawing frames through those two.
  The server half logs each per-profile request's time whenever it passes 200 ms, and the first
  request's time as a diagnostic line (`QUESTTREE_DEBUG=1`). Quote both lines rather than a feeling.
- **Tree only shows unlocked quests, no pins** - the server half is missing, or you are on someone
  else's server that does not have it. See "Both halves are required" above. The server half says so
  in its own log every time it answers - `Quest Tracker <stamp>: serving 558 quests to the client
  mod.` - so the absence of that line is the confirmation, and a small count in it is the other
  half of the answer.
- **`Http response status code: NotFound` on `/questtree/...`** - same cause: the server you are on
  has no Quest Tracker server half, or one older than this client.
- **Server refuses to load the mod** - the server half must match your SPT version. This build
  targets SPT 4.1.6.
- **On Fika, a map stops learning zones once someone joins with a newer build** - the host's server
  half is older than the joiner's client, so it refuses a harvest whose schema it cannot read in full
  rather than storing half of one, and it skips any zone file already stamped newer than it reads.
  Both refusals name the reason in the **host's** server log - `Quest Tracker: refused a zone harvest
  for 'bigmap' - harvest schema v2 is newer than the v1 this server reads ...`, and `Quest Tracker:
  zones/bigmap.json is schema v2, newer than the v1 this server reads - skipped ...`. Update the
  host's half; the joiner's version is not the one that decides.
- **A map says "not harvested yet"** - it is a map the release did not ship zones for. One raid on
  it fixes that for everyone on the server.
- **A map has no picture** - nothing has drawn one yet: DynamicMaps does not ship that location, you
  have not captured it, and no host has sent one. The map still draws its harvested rectangle with
  every pin on it, and one raid with the capture key gives it a picture. See "Map pictures" above.
- **A capture is black or has holes** - a black or nearly black picture is weather or night: our
  capture camera gets the scene's direct sunlight, and heavy rain takes the sun away. That case is
  refused rather than written, with `rendered too dark to be a map (p98 ...) - nothing was written.
  Heavy weather or night; try again in daylight.`, so try it again in clear daylight. Holes - blank
  regions in an otherwise good picture - are chunks the game had streamed out of memory because the
  player was far from them; press the key again from another part of the map and the second capture
  fills what the first could not see. The log line ends with how much of the floor is still empty.
  `Ctrl+Shift+F9` does the walking for you, and *Capture the map automatically while I play* fills
  the map in as you cross it.
- **The map fades out into the panel at the edges** - that is deliberate, and it is where the world
  ends. A capture's rectangle is padded past the playable area, so it takes in hillside, water and
  skybox terrain that looks exactly like the map and is not part of it. Everything outside the area the
  game's own navigation mesh describes - grown by 8 m, so no roof, yard or interior is caught by it - is
  left transparent, so what you see there is the panel behind the map rather than scenery you could
  walk to. The capture line says how much: `... 34 % outside the walkable area`. A hole in the middle of
  a map is the same transparency and does mean something: that is ground no capture has drawn yet, and
  another press fills it in.
- **A capture replaced the one I had instead of adding to it** - two pictures are only merged when
  they were rendered the same way, and the log line names what differed: a new build's render recipe,
  a changed extent, a different resolution, a different set of floors. The map starts over from this
  capture, which is the right answer - the old pixels no longer mean the same thing as the new ones.
  Use `Ctrl+Shift+F9` once and the map is whole again after one campaign.
- **Uploads say the host does not accept map pictures** - that is the host opting out, which is the
  default: a picture is the one thing a peer can post that everyone else then looks at. Start the
  host's server from `tools/server-host.cmd` (it sets `QUESTTREE_ACCEPT_MAPS=1` and starts
  `SPT.Server.exe`), or set that variable in whatever starts the server. The host's own log says
  which mode it booted in - `Quest Tracker: map uploads from clients are accepted
  (QUESTTREE_ACCEPT_MAPS=1).` or `... are declined (QUESTTREE_ACCEPT_MAPS=1 accepts them).` Your
  captures are unaffected either way; they are read from your own folder.
- **The map I captured is not shown** - the picture order prefers DynamicMaps by default, so on a
  map it ships you get its artwork even after you have captured your own. Settings > Map > **Map
  pictures come from** > *my captures when I have one, else DynamicMaps* (or *my captures only*)
  switches it, and the map repaints. If the credit line still says "via DynamicMaps", the setting is
  the reason.
- **`tarkovdev-last-failure.txt` in `SPT_Runtime\user\mods\QuestTree`** - tarkov.dev could not be
  reached, so the server stops asking for a day rather than paying the attempt on every boot. Delete
  the file to retry sooner. A successful download lands beside it as `tarkovdev-quests.json` and
  refreshes itself after seven days; delete that one to refresh it now.

## Uninstalling

Delete `BepInEx\plugins\QuestTree` and `SPT_Runtime\user\mods\QuestTree`.

Nothing the mod writes is needed to play, and nothing breaks by removing it. Two things outlive the
two folders:

- **Any weapon preset you saved** with *Save as a weapon preset*. Those are ordinary saved builds in
  your profile, named `QT: <quest> - <weapon>`. Delete them in the game's own build list.
- **`BepInEx\config\com.takov.questtree.cfg`**, your settings. Harmless, and deleting it is optional.

Everything else - the harvested map zones, the solved weapon builds, the downloaded objective
locations (`tarkovdev-quests.json`, `objective-gps.json`) and the `tarkovdev-last-failure.txt` stamp
beside them - lives in the two folders above and goes with them.
