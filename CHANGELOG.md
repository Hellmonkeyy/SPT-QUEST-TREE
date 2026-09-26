# Quest Tracker 1.19.0

**Every location can have a map, including the ones no mod draws.** The Maps tab took its picture,
its world bounds, its floors and its place names out of the DynamicMaps mod's art folder, so a
location that mod does not ship - every modded map - showed "No map image for this location." and a
list. The mod now measures each map itself in the raid pass that harvests the quest zones: the
rectangle the world occupies in game coordinates, ranked NavMesh first, and the height bands that
read as floors. That alone draws a map - a dark backdrop over its own rectangle, guides on, floor
picker working, every pin where it belongs - after one raid on it.

**Ctrl+F9 in a raid takes the picture.** The game draws the map from straight above, one picture per
floor band, into `BepInEx\plugins\QuestTree\captures\<key>\`, at up to 8192 px and never past four
pixels to the metre - a quarter of a metre to the pixel on Customs, 4472x2156 across fifteen tiles -
spread over frames as short hitches rather than one freeze. It is drawn to exactly the rectangle
harvest measured, so a pin lands on the right building without anything agreeing twice. Pressing the
key again from somewhere else adds to what is there rather than replacing it - the game streams
distant chunks out, so each capture has holes another fills - and where two cover the same ground
the pixel seen from closer wins, so a map gets sharper the more often it is captured. What is shared
with a host or shipped in a release is downscaled to 2048 whatever the setting says.

**What the render does and does not draw** is most of the work in this release. The top floor is
shot from 300 m up so roofs draw; the level-of-detail selection that culled every building against a
kilometre-tall orthographic view is switched off for the render and the terrain put on its averaged
base map, so buildings are buildings and the ground is not a two-metre checker; every renderer AND
every whole object EFT's distance culling had switched off is forced visible for the whole of a
FLOOR - the objects one at a time, each guarded, because activating one runs the game's own Awake
and OnEnable - once before its first tile, put back after its last, because a stop on Customs
flattens twenty-seven thousand components out of the culling objects and the game's own force-enable
is a twenty-five-a-frame coroutine that would do nothing inside one tile - which is why a capture no
longer shows warehouses as patches of ground with a wall outline, and why for the second or two a
floor takes the player's own frames draw that distant geometry too and the water on the Water layer
in them is the flat capture blue. Each tile renders at twice the output resolution into a
four-sample multisampled target the camera is told to use, and is box-averaged back, a half-covered
edge taking the colour of its drawn samples alone; a despeckle pass medians the isolated outliers a
smoothing pass preserves as edges. The renderers on the game's own WATER LAYER are painted a flat
map blue - hiding them took the Customs river out of the picture and left its bed showing, and
painting everything whose shader was merely named like water put blue slabs over yards, the bridge
deck and interior floors, because most of those are wet-surface decals; the layer is the whole test
now, no shader-name test is left, and a map with nothing on that layer is captured with its water
exactly as it draws, which the capture line states either way. Glass and transparent effects are
left out, a patch that came back flat cyan is painted over from its surroundings by a pass that
tests the pixel's COLOUR rather than any shader, and reflective surfaces reflect a neutral grey
instead of the sky, so a wet roof is a roof and not a mirror. The NavMesh is rasterised into a
walkable mask, grown 8 m with a 6 m ramp, and everything outside it is CUT OUT - the mask is the
picture's alpha, so the skirt of hillside and skybox terrain past the playable area is transparent
and the Maps tab's own dark plate shows through, which is also what a streamed-out hole now looks
like; the per-floor line says how much was outside, and host and release copies, being JPEGs, are
flattened onto that same plate rather than onto black, so a downloaded picture is the tone the
picture at home is. Colours are muted so a pin is the brightest thing on screen; a
render too dark to be a map, or one under different light from the pictures on disk, is refused
rather than written. The meta records the whole recipe - thirteen values, from the light to the
multisampling the device actually gave, with the grey reflection and the generation of the water
treatment among them - and a set made under another recipe is replaced, not merged into.

**A captured map's names are readable and its extracts are marked.** White text, the accent colour
for an extract, on a nearly solid dark plate at a fixed size on screen whatever the zoom, a dot on
the spot with the plate above it, a name that would land on one already drawn dropped, and zone
names held back until there is room for them - which is why the new "Map labels" setting defaults
to all of them. Every extract carries an exit-sign-green diamond whatever the label setting says, since
the names can be turned off and the extracts should not be; the legend names the diamond and the
facts line counts them.

**Ctrl+Shift+F9 captures a whole map without you walking it.** One press plans a grid of stops about
120 m apart, finds somewhere standable in each cell, teleports the player from stop to stop, captures at
each and returns them to where they pressed it - about 33 stops on Customs (9 x 5 cells of 115 x 100 m,
the ones with nowhere to stand dropped), each a full capture, with the 3D model rebuilt at every stop;
120 m because at 200 m the ground between stops was not covered well, and the nearest-capture-wins
merge needs every pixel loaded at the stop nearest to it. It disables no bots and touches nothing but the player's position:
start such a raid with AI set to none. "Capture the map automatically while I play" (off by default)
does the same job as you walk, one capture every few seconds - 5 by default, 2 to 120 - once you
have moved 15 m; it hitches every few seconds and is meant for a raid set aside for map-building.

**Captures travel through the host.** Each finished capture is offered one floor at a time as a
2048-px JPEG, and every client of that host picks up the maps it lacks on the first Maps-tab open of
a session. The host decides: uploads are refused unless it runs with `QUESTTREE_ACCEPT_MAPS=1`
(`tools/server-host.cmd` sets it), the refusal is one line and is not retried that session, and a
set only ever moves into place whole. The release ships whatever map sets exist, gated on layout,
1.5 MB per image, the meta schema and `tools/check-maps-pack.py`, with two things reported rather
than gated: the payload's total size (printed on every run, a warning past 80 MB) and coverage of the
eleven vanilla maps (a warning naming the missing ones).

**DynamicMaps stays a selectable source.** "Map pictures come from" chooses DynamicMaps first (the
default, so nothing changes for an install already using it), our captures first, or our captures
only. Nothing of its artwork is bundled or copied, and each map's author is still credited; a
captured map carries our own credit line naming the build, the date and the raid's clock.

## Also

- The zone file is schema v2: each map's record carries its extent (four edges, the source that
  produced it, when) and its floor bands with names and height ranges, copied onto the marker
  payload. Both halves refuse an extent that does not contain the zones already harvested, so a
  wrong rectangle is dropped rather than drawn; a host older than the client refuses the harvest by
  schema exactly as before.
- Three routes on the zone harvest's pattern: a per-floor upload, an index carrying a sha256 stamp
  per map, and one that serves a floor. An older host answering with SPT's HTML and a newer one
  answering with an unknown index shape are both silent fallbacks to what the client already had.
- Captured place names come from the scene - the extraction points and the cleaned-up bot zone
  names, all of them by default now - and are re-culled when the zoom moves by a quarter rather than
  on every pan.
- A capture campaign appends its stop lines to `captures\<key>\<key>.campaign.txt`, the last twenty
  runs of that map, because a game log is gone the moment the game restarts. Nothing reads it but a
  person; the uploader and the packager both ignore it.
- A quest with a harvested position on a map loses its percentage-placed pins there, and a harvested
  ITEM spot now counts as that coverage the same way a trigger zone does - it is a real world
  position for that quest. The boot line says how many percentage pins each map kept and how many
  were dropped, instead of one number that quietly meant the kept ones.
- The capture header no longer prints a rendering path copied from the player's camera before the
  orthographic switch, which is not the path that renders. Multisampling is asked for at 4, then 2,
  then 1, and the level the device actually gave is recorded in the header and the render tag, since
  two machines that resolved differently did not make the same picture; the supersampling is what
  does the antialiasing either way. Four and not eight because a 2048 half-float tile at eight
  samples is four hundred megabytes of video memory on a machine that is also running a raid, and on
  geometry already supersampled two by two nobody will find the difference in the picture.
- **A floor is captured inside a memory budget.** One floor may work in 256 MiB of arrays and
  textures, counted at 26 bytes per output pixel term by term, and the pixels per metre come down
  half a pixel per metre at a time, to a floor of one, until it fits - deterministically, so two
  captures of one map still agree on the scale and still merge. Interchange, on the 965x925 m
  rectangle a harvest measures for it, wants 354 MB a floor at 4 px/m - which is exactly what died
  in a raid ("GetPixels: scripting array creation failed" on one floor, an OutOfMemoryException on
  the other two) - so it captures at 3 px/m and 199 MB instead, and the header says so: `4 px/m
  would need 354 MB a floor, over the 256 MB budget, so 3 px/m (199 MB)`. Customs at 4 px/m is
  239 MB on its 1118x539 m, inside the budget and untouched, so the sets already on disk stay
  mergeable. Every figure here is the budget's own arithmetic on a measured rectangle, so a map
  whose harvest comes out differently lands on a different step.
- **Two crashes found in a Customs campaign are fixed.** A half-float HDR render can hand back a NaN
  sample; it was false to every comparison that would have rejected it, reached the smoothing
  filter's range-weight lookup, and Mono casts a NaN to int.MinValue rather than to the 0 desktop
  .NET gives - an index a long way outside the array, and four stops died of it. NaN is now treated
  as "not drawn" in the three places that can see one. And every readback is a view of the texture's
  own memory through GetPixelData rather than a managed array from GetPixels - 16 bytes a pixel per
  staging band, a hundred and twenty-eight of them a floor, plus a whole decoded picture per merge,
  which is what fragmented the heap under the allocation failures above.
- `package.ps1 -RefreshBuilds` copies the trained weapon-build cache over the shipped seed, printing
  the stamp, the build count and the trader/flea/unpriced split either side of the copy so a worse
  training run is visible, and refusing outright while `SPT.Server.exe` is running. The training
  launchers also accept map uploads now, so a capture raid can hand its pictures to a training
  server.
- `tools/check-capture.py` checks a fresh capture against its own meta and the server's zone file
  for that map; `tools/server-host.cmd` starts the server as a picture host, `tools/check-dtos.py`
  and `tools/check-maps-pack.py` are the two gates `package.ps1` runs, and
  `tools/count-seed-sources.py` prints the seed's stamp, build count and trader/flea/unpriced split.
- **Six review passes over everything since 1.13.1** (client data, the weapon solver, the views, the
  marker payloads, the map pipeline, the tooling) fixed what they found rather than filing it. The
  ones a player could meet: the "loading" notice stayed up for good when the panel was hidden while
  it was still holding off a fetch; a status rebuild polluted the panel-open timer; Maps > Refresh
  could not retry a marker set the client had latched as failed; a Tree reset kept the trader
  colours; the Kappa override list had no cap; the tab highlight vanished when the pointer left it;
  a downloaded map set carried the wrong pixels-per-metre when its long side had been pinned, and its
  labels lost their kind on the wire (every extract read as a place name on the receiving machine).
  The solver ones: a rebuild now resets its tree and locks the named parts with their ancestors, the
  incumbent is measured rather than asserted, a prune pass may only lower the price, the cache key
  and fingerprint are culture-invariant, and repair-cost lines are priced against the weapon they
  belong to. **The solver version is 12**, so the builds remembered from an earlier boot are solved
  again once on the next start and the shipped seed is refreshed before packaging. A host now
  strips line breaks from floor names before they reach its log, and item places on the map keep the
  same identity from raid to raid instead of being re-keyed every time.
- **Captures merge now. They never did.** Every capture since the walkable mask went into the
  alpha channel has REPLACED the whole picture instead of adding to it: Unity decodes a PNG with
  alpha as ARGB32, the merge's loader accepted only RGBA32 and RGB24, so the previous picture was
  refused every time (one Info line per capture said so, in words that read like a format quirk),
  the merge saw no old pixel anywhere and took every new one. A campaign still looked merged
  because the game keeps most of Customs loaded from any one stop; a capture taken inside Big Red
  lost the far side, and the distance sidecar of a two-campaign set measured every pixel from one
  stop. ARGB32 is read now, with its byte order decided from a pixel that can tell the two possible
  orders apart and cross-checked against Unity's own reader, and refused when the evidence is
  missing. The render recipe's `reach` term now records whether the walkable mask was actually
  built rather than merely asked for, since a merge of a masked and an unmasked capture would
  leave a hard seam.
- **Big Red is a building again.** The throwaway probe key below, run inside it, found that the warehouse's
  own walls-and-roof mesh - the one with real materials that the player's camera draws - sits on the
  game layer named HighPolyCollider, and that name had put the layer on the capture's list of
  collider layers to leave out. What was left of the building was its two interior-volume shells
  drawn with a flat vertex-paint shader: the translucent teal slab three campaigns showed. The layer
  is drawn now, the recipe carries a `layers` term so every Customs set taken before this is replaced
  rather than merged into, and the collider layers still excluded are the ones the game's camera never
  draws either. Seen on screen on 2026-09-23: Big Red is a warehouse with a roof in the Customs
  picture, two campaigns on this build.
- **The capture now also measures the map's shape.** The picture is a photograph from above; the
  mesh is the ground and the buildings as geometry, written beside it in one press. The ground
  comes from a raycast grid - a one-metre cell since WP7, coarser in half-metre steps only for an
  extent over 4 million cells (it was two metres) - cast from each floor band's own camera height -
  the experiments measured the whole of Customs at 2 m in 45 ms through `RaycastCommand`, against 290 ms
  one ray at a time, and measured something better: colliders do NOT stream out with the player, so
  the ground comes back complete from anywhere on the map and needs none of the merging the pixels
  need. The buildings come from the renderers themselves, 184,000 of them on Customs filtered down
  to a few hundred by size and by the layers the picture draws, with each LOD group's last real
  geometry step taken rather than its impostor card, and with the meshes the graphics card holds
  alone read back asynchronously off their own buffers - never by writing a mesh's buffer target,
  which killed the process outright on 2026-09-22 and is now forbidden everywhere in this feature.
  Everything is chunked over frames (20,000 rays, 20,000 renderers, one building), capped at a
  triangle budget derived per capture (see "Extreme detail is the only detail" below), and says in
  three log lines what it built and what it cut.
- **Maps: 3D.** A captured map now opens as ground with its picture draped over it and its
  buildings standing on it - drag to move, right-drag to turn and tilt, scroll to come closer, and
  the floor picker peels the storeys. Markers, extracts and place names sit on the ground at their
  real height. The new **3D relief** toggle on the map (and **Map view** in Settings) switches back
  to the flat picture; maps with no relief captured for them are unchanged. "Mirror map artwork"
  and "Extra map artwork rotation" do not apply in 3D. The geometry is built once per file and
  cached across repaints, so clicking a quest row does not re-triangulate a map.
- **The 3D map travels with the pictures.** A capture's mesh - the ground relief and the building
  shells - goes up to the host on its own route after the floors and comes down with them, so one
  player's raid gives the whole group a map that pans and tilts rather than a flat one. It rides
  the same single opt-in (`QUESTTREE_ACCEPT_MAPS=1`, `tools/server-host.cmd`) and the same
  all-or-nothing rule as the pictures: a set whose capture built a mesh is not served until both
  have arrived, so a borrowed map never names geometry the host does not hold. The file is
  identified by its sha256 at every hop - the uploader will not offer a mesh its own meta no longer
  describes, the host refuses one whose bytes do not hash to what was claimed, and a downloader
  checks what arrived against what the index promised. The host also reads the whole file before
  storing it: header, every building's heights and triangle indices, and that its rectangle, floors
  and counts are the ones its pictures' meta states. A mesh problem costs the mesh and never the
  map: a mesh the host can never use (unreadable, not fitting its own pictures, or too big for the
  host's space) is refused once and the pictures are served without it, so the map draws flat; only
  a mesh the host could not *write* leaves the floors waiting, dropped at the host's first start a
  day later. On the way down, a mesh that does not match is left out and the pictures kept, while
  one that does not arrive at all - a timeout, a dropped connection - leaves that map as it was for
  the session and is fetched again, whole, on the next start, rather than installed flat for good.
  Nothing about it bumps a schema version: an older host stores the pictures and ignores the mesh,
  an older client never asks. The release
  payload gains the meshes (tens of megabytes a map, sized by the capturing machine), so the packager's total-size gate is now a warning at
  80 MB that prints the payload's size - and the meshes' share of it - on every run instead of a hard
  40 MB stop, and a shipped mesh is checked against its meta's sha256, byte length, extent and floor
  levels before it can be zipped.
- **The side pictures travel with the rest.** The four oblique views a capture takes for the 3D map's
  walls go up to the host through the same route as the floors - one post each, after the floors and
  before the mesh, encoded exactly as a floor is (flattened on the map backdrop, 2048 px on the long
  side, JPEG at q80, up to 2.5 MB) - and come down through the same image route. A set that names
  sides is not served until every floor, every named side and the mesh have arrived; but a side is
  never worth the map, so a side the host cannot use (not a JPEG, over the cap, not a view of the
  capture box its meta describes, or one the client could not encode and posts empty to say so) is
  DROPPED from the set with one line in the host's log, and the rest is served - never refused, never
  flattened. Each side is held to the capture box on the way in: its basis unit length, its origins
  the box's own projections, and its pixel size the projected span at its scale within two pixels,
  which is what catches a side described at one size and sent at another. A host from before sides
  refuses a side post as a floor it does not know rather than filing it as floor 0, and the client
  then goes on to the mesh. Every side a meta names is reserved against the store's total from the
  first floor. Packaging admits `<key>\<key>-side-<N|S|E|W>.jpg` by that exact name, holds sides
  to the 1.5 MB per-image gate, and `tools/check-maps-pack.py` checks each side's JPEG size against
  its meta, its basis for unit length, and that no side file goes unnamed.
- **Buildings in full detail, up to 3,000,000 triangles a map** (stage V; the budgets, caps and
  transfer limits in this bullet and the atlas bullet below are stage V's and W's, and WP7 replaced
  every one of them with a derived rule - see "Extreme detail is the only detail"). Each building's MOST detailed
  level of detail is read whenever its source totals at most 1,000,000 triangles (a bigger one keeps
  the old rule: the last real level, never an impostor card), and our own decimation - quadric edge
  collapse on a worker thread - reduces it to a budget set by its footprint area. The map budget
  rises from 300,000 triangles to 3,000,000, and everything that carries a mesh rises with it: the
  mesh format's caps are 6,000,000 triangles and 12,000,000 vertices a file; a mesh file may be 48 MB
  (was 12), a map 84 MB on the host and on the way down (8 floors and 4 sides at 2.5 MB, a 48 MB mesh
  and 6 MB of margin), a session downloads up to 300 MB (was 120) - both raised again by the atlas
  pages below - a mesh request may take 240 seconds
  (was 90), and the host checks a mesh inflating to at most 160 MB (36 MB of indices and 54 MB of
  vertices at 3 M triangles, and room for the relief grids). **A mesh past 16 MiB goes up in parts.**
  An SPT server runs on Kestrel with its default limit of 30,000,000 bytes a request body - measured,
  and nothing in SPT raises it - and a zlib-compressed post of a deflated mesh is about 1.03 times the
  mesh, so one post cannot carry more than about 28 MB. The client sends parts of 16 MiB (~17 MB on
  the wire), the host holds them in the capture's staging and joins them, and the whole is then
  checked exactly as a mesh that came in one post: length, sha256, header, fit and budget - and a
  budget failure found at the FIRST part serves the capture flat at once rather than holding its floors
  and parts. A host from before stage V never sees a part: its meta check drops any mesh past its 12 MB
  when the floors arrive, the set completes flat, and the client says at Info that the host kept the
  capture without its 3D mesh. Abandoned uploads - held parts included - are now swept every time a set
  completes and on upload posts every ten minutes, not only at the host's start. The packager's 80 MB
  payload warning is now expected to fire, and says so when the meshes are most of the payload.
- **The atlas pages travel with the rest.** A capture's atlas pages - up to eight 4096 px sheets of
  the game's own building textures, which the 3D view drapes on the buildings by the mesh's UVs - go up
  to the host through the floors' route, one post each, after the sides and before the mesh. A page is
  sent at its FULL size, never scaled to 2048 px as a floor is, as a JPEG at quality 90 - encoded once
  more at 80 if it comes out over 6 MB - and one still past that, or one that cannot be encoded, is posted
  empty so the host stops waiting for it. A page post may take 240 seconds, the mesh's deadline, both ways;
  a page that does not get through is dropped (posted empty) and the upload goes on to the mesh, and a
  page that does not arrive on the way down is noted in the map's stamp file and fetched alone next
  session.
  A set that names pages waits for every one as it waits for its sides - but a page is never worth
  the map: one the host cannot use (not a JPEG, over 6 MB, not the width and height its meta states,
  not a page 0 to 7, or posted empty) is DROPPED with one line in the host's log and the rest is
  served, and the buildings drawn from it fall back to the sides and tints. The host stores each page
  as `<key>-atlas-<n>.jpg` and REWRITES its sha256 in the served meta to the stored JPEG's (the capture's
  meta hashed its own PNG); a client downloading the set holds each page to that sha, its JPEG size and
  6 MB, and leaves out any page that fails. Pages come down after the mesh and only when the mesh
  landed, and a set served flat carries no pages at all. A host from before pages refuses a page post
  as a floor it does not know, never filing it as floor 0, and the client goes on to the mesh. The
  budgets rise with them: a map may hold 132 MB (was 84: 8 floors and 4 sides at 2.5 MB, 8 pages at
  6 MB, a 48 MB mesh, 6 MB of margin) on the host and on the way down, the host's whole store 1.5 GB
  (was 300 MB - eleven maps at the full 132 MB are 1,452 MB), and a session downloads up to 600 MB
  (was 300) - for at least three minutes and past that while it still arrives at 1 MB/s or better, where
  three minutes was a flat limit. A client now keeps at most 2 GB of host pictures, evicting the set
  installed longest ago (never one taken that session). A host set with the same capture instant as this
  machine's own capture is this machine's upload, and is no longer downloaded back. The host caches each
  set's stamp and per-file sha256 beside it (`<key>.stamp-cache.json`), keyed on every file's size and
  write time, so a restart re-hashes only files that changed rather than up to 1.5 GB under its lock.
  Every page a meta names is reserved at 6 MB against the store from the first floor.
  Packaging admits `<key>\<key>-atlas-<0..7>.jpg` by that exact name and holds pages to their own 6 MB
  gate rather than the 1.5 MB picture gate; the payload warning counts pages with the meshes; and
  `tools/check-maps-pack.py` checks every page's JPEG size and sha256 against its meta, that pages sit
  only beside a mesh, and that no page file goes unnamed.
- **Extreme detail is the only detail.** There is no quality setting and no per-map number: every
  budget is derived at capture time from what the scanner measures, the machine and the file itself.
  A building's target is 20 triangles for every square metre of its bounding box's SURFACE (was 6 per
  square metre of footprint), 24 to 250,000 a building, and never below the target the old footprint
  rule would have given the same building on the same list - so no building gets less than before, on
  any machine. A 62 m pylon on a 5x15 m footprint went from 288 triangles to its full source. The map's
  cap is the least of what the buildings need at that density divided by 0.9, this machine's memory
  ceiling (a sixteenth of RAM and an eighth of video memory at 64 bytes a triangle, never under the old
  3,000,000) and 20,000,000; on a 16 GB / 8 GB machine Customs stores about 6.8 million triangles at
  scale 1 where it stored at most three. The relief cell is 1 m wherever the extent fits 4 million
  cells a band and half a metre coarser at a time only where it does not, so every stock map runs at
  1 m and no extent can lose its mesh by throwing. The file stays version 3: its caps became hard
  bounds that only refuse hostile files - 40,000,000 triangles, 80,000,000 vertices and 1,000,000
  triangles a building - and a reader from before this change refuses a larger file by name while
  every earlier file reads unchanged. A building whose atlas split would pass a vertex cap stays
  untextured instead of costing the whole file. **The 3D view's floor cut is the camera's near plane**:
  choosing a lower floor makes the private camera's near plane oblique along the cut height, so the
  graphics card clips everything above it exactly at the pixel; the CPU clip, its cache of clipped
  copies and the managed copy of every building mesh it clipped from are gone, a floor switch is one
  matrix, and the camera renders forward (the deferred path cannot take an oblique projection). Terrain
  that rises above the chosen floor's top is clipped too now, and the build line counts it. The view
  refuses a file with more triangles than a quarter of its graphics card's memory holds and draws the
  flat map. **Host and client limits follow the disk and the RAM**: a host's store is a quarter of its
  free disk at boot (1.5 to 32 GB), a map an eighth of that, a mesh that less the map's pictures (512 MB
  at most), each upload held to the size its capture declared, in up to 64 parts of 16 MiB; a client
  takes host meshes up to a fiftieth of its RAM, keeps a quarter of its free disk of host maps (2 to
  32 GB), no longer stops at 600 MB a session, and gives a whole mesh download a deadline sized to it
  (60 s plus the transfer at 512 KB/s, 240 s to 30 minutes). `tools/check-capture.py` and
  `tools/check-maps-pack.py` hold the new bounds and the relief-cell rule (a 2 m capture from before
  this is a WARN, not an error), check-capture.py's `--compare OLD_ROOT` says whether any building
  lost detail between two captures, and check-maps-pack.py warns at a mesh over 100 MB, which GitHub
  refuses in a push.
- **A throwaway diagnostic ships unbound with this release**, which is worth saying out loud because
  it is not a feature: the mesh probe of the 3D map experiments, which does nothing until it is bound
  under F12 > **Advanced > Mesh probe key (throwaway)**. Once bound it writes
  `BepInEx\plugins\QuestTree\captures\<map>.meshprobe.txt` in a raid - whether the game's own meshes
  can be read back off the graphics card, and how much of the map its colliders cover from where you
  stand - and `captures\menu.meshprobe.txt` in the menu, where it lists the loaded shaders, cameras
  and layers and puts a small test view in the bottom-left corner until it is pressed again. Nothing
  in the mod depends on it. What it does touch: it reads up to twenty scene meshes and asks the
  graphics card for a copy of one, modifying none of them - the first version of it did modify a
  mesh's buffer targets and put them back, and the restoring write killed the game natively
  (`Mesh.set_vertexBufferTarget` into d3d11), so nothing in this mod writes those any more and the
  readback test now has to complete once in the menu before a raid will run it; in the menu the test
  view lives until the second press and swallows clicks inside its own 512 px square. It is meant to
  be removed again. It ships unbound - bind it under F12 > **Advanced > Mesh probe key (throwaway)** -
  and is deliberately kept out of the in-game Settings tab.
---

# Quest Tracker 1.18.5

**The server console prints what a player needs and nothing else.** A normal boot wrote 109 `Quest
Tracker` lines, all at Information because that is what SPT's shipped `sptLogger.json` prints, and most
of them were solver working notes - the per-profile parts bill, the pricing and flea coverage, the
solver dry run, the per-map zone counts, one line per weapon build. It now prints about five: the
version and quest count, the remembered builds, the background search and its result, the map markers,
plus anything wrong or degraded and one line per raid event.

**Forty-eight lines became diagnostic lines**, Information when `QUESTTREE_DEBUG` is `1`/`true`/`yes`/
`on` and Debug otherwise, read once at startup. None of their text changed, so anything that greps for
one still finds it. `tools/server-debug.cmd` sets the variable and starts the server in a visible
console; `QUESTTREE_TRAIN=1` implies it, because a training run exists to be watched. The stamp line
always says which mode the boot is in, so a quiet console explains its own quietness.

## Also

- Two self-checks demoted from warnings to diagnostic lines: the copy budget against the served rows,
  and the panel's count of preset changes against the search's. Both compare two internal counts of the
  same thing, neither is actionable by a player, and both still print under `QUESTTREE_DEBUG`. Every
  other warning and every error is untouched.
- A slow request still logs at Information every time it passes 200 ms - that line is the one the README
  tells a player to quote. Only the first-request timing moved.
- `QUESTTREE_TRAIN` is now read through the same parser as `QUESTTREE_DEBUG`, so the flag that turns
  training on and the flag that turns its console output on can never disagree about what counts as yes.
- The client half is unchanged apart from its version constant. Both halves still have to ship together:
  the Kappa check compares the two versions for equality.

---

# Quest Tracker 1.18.4

**The map stops flickering and resetting when you click a dropdown.** Opening the map picker, closing
it and re-picking the map already shown changed nothing about the map, yet each threw away the
picture, the mask, the pan handler, every place name and up to two hundred-odd pins to draw the same
thing - a visible flash, and pan and zoom back to a fresh fit. The viewport is kept across a rebuild
that would draw the identical map (map, floor, statuses, marker payload, picture, settings, selected
quest, panel size) and thrown away for one that would not; the sidebar and pickers are still rebuilt
every time, F still refits, and panning and zooming are untouched.

**The pin count says how many pins are on the map.** It printed the marker payload's total, so "340
pins" sat over a map showing twelve of them with accepted-only on, or past the 200-pin cap. It reads
"12 of 340 pins" when fewer are drawn than the payload holds, and plain "340 pins" when they all are.

**A server that has stopped answering costs one wait, not four.** A prefetch that times out left each
payload uncached, so the game's own thread went and proved the same silence again, fifteen seconds per
payload. Those payloads are answered as unavailable for thirty seconds instead - no request, nothing
cached, nothing latched, expiring on its own; a payload the budget never reached inherits the hold-off
of the one that hung. A refused connection or a missing route is unchanged. A batch the panel gave up
on is cancelled as well as dropped, so its worker stops duplicating round trips the call sites are
already re-issuing.

**Refresh always asks.** Every Refresh link on the four aux tabs clears those hold-offs and the
markers' empty-answer window before refetching - pressing it is the player saying "ask now", and on
the Kappa tab the button under "the server cannot be reached" did nothing for half a minute. A hand-in
does not clear them: it says the answer changed, not that the server started answering. A change of
profile or server clears both.

## Also

- One combined warning line per batch names the payloads that did not answer, instead of one line
  each about the same hanging server. The open line counts only requests actually issued, so a slot
  the budget skipped or cancellation stopped no longer prints a `: prefetch 0` pair or inflates the
  round-trip count.
- A settings generation counter, bumped by every setting change and section reset, feeds the map's
  keep-or-rebuild test. One number rather than named entries, because the pin colours reach the map
  without it reading a setting - named entries left every pin in the old palette after an F12 colour
  change.

---

# Quest Tracker 1.18.3

**Opening the tracker stops freezing the game on four more fetches.** The profile, Kappa, pre-raid
check and map-marker payloads were each a blocking request on the thread that draws frames, at their
own point in the build. They are fetched, parsed and cleaned on one more worker during the wait the
quest list already had, so each call site finds its payload cached; whichever is already cached is not
asked for again, and one that does not arrive is not cached at all, so its getter asks for itself
exactly as before.

**The open line says where that time went.** Each payload's two halves print as `profile: prefetch`
and `profile: prefetch parse`; the `profile: server` and `kappa: server` entries later in the line are
the main-thread twins, and them reading 0 is what says the prefetch covered them.

**A stale answer cannot land on top of a newer one.** Each payload carries a counter that moves when
something announces it has changed, compared before the answer is published - a request already in
flight cannot be cancelled. A batch that finished while the panel was shut is discarded rather than
published: nothing bumps a counter when you pack a magazine, and the pre-raid check's promise is that
if it says you have enough on you, you have enough.

**A prefetch that runs out of time says so.** The batch may keep starting requests for twenty
seconds, under the forty the open waits; giving up on the payloads is charged to `payloads: gave up`
rather than to `quests: main`, which is meant to be the small remainder the game still spends its own
thread on.

## Also

- The four helper scripts moved into `tools/`, which is where packaging looks for the DTO check.

---

# Quest Tracker 1.18.2

**The pre-raid button tells you what you are carrying now.** It was painted once from a
session-long cache only the tracker's Refresh link refetched, so packing the item left the button
saying "1 TO PACK" over a sidebar saying it was on you. It refetches on every matchmaker show and
repaints when the tracker refreshes.

**Pressing ready no longer pauses the game.** The first pre-raid check after a start cost 745 ms
against 10 and 6 for the next two - one-time work the game paid for on its own thread. The server
builds one full answer at boot, off the blocking path, and logs both passes phase by phase.

**Two quests asking for the same gun get two answers.** The game's own verdict was cached per build
but is computed per quest, so two held quests stating one requirement shared a verdict. Keyed by
quest and build now; and accepting a quest with the tracker open clears the "checkable once the
quest is accepted" line instead of leaving it behind.

**The repair search values barter parts the way the shared search does.** It valued a barter or
absent part at handbook face value where the shared search values it at handbook x3 - a threefold
bias towards barter parts, and a cost comparison between two different questions. A new boot line
reports the repaired builds' objective against their bill and what the difference is made of.

**A build that cannot be re-seated is replaced, not defended.** A remembered build whose parts
cannot be seated on this install described itself as costing zero, which is unbeatable, so it
blocked every smaller build found for it. Treated as absent now, with the weapon and reason logged;
the training path also audits the incumbent before measuring anything against it.

## Also

- The Kappa tab's lists draw at most 150 rows each with a "+N more" tail. A map draws every started
  quest's markers plus 200 others, ordered as the quest list is, and logs once per map when it trims.
- The 436 ms per-profile parts report moved off the boot's blocking path, taking the mod's share of
  a boot from 677 ms to 280; two caches read outside their lock are volatile; the boot survey and the
  solver dry run copy what a zone harvest rebuilds.
- A hover card that cannot read the profile says so once a session. Dead fields and doc comments
  attached to nothing are gone.
- The README describes 1.18.0 and 1.18.1. Packaging refuses a release with no changelog entry or
  stub notes, and records the built archive's sha256 in the notes.

---

# Quest Tracker 1.18.1

**Copies you do not hold are charged.** A build fitting two of the same part priced both at zero
if you owned one loose copy. Each copy past the ones you hold (preset parts, the copy on a gun you
own, loose copies) is a purchase now, in the search and on the panel; a second copy nobody sells
says so, or names the trader and loyalty that does. The shared builds are unchanged.

## Also

- A third toolbar layout below ~1460 px so the "N shown" count is readable at 1366 wide.
- The zone harvest route refuses a client newer than the server; a zone file stamped newer is
  skipped with a reason. A flag nothing read is gone.

---

# Quest Tracker 1.18.0

**The weapon builder now minimises what you pay, not the handbook.** Shared builds are priced at
the cheapest trader cash price at any loyalty, in roubles, with parts no trader sells for cash
priced at three times handbook so the search prefers a trader-sold part when one will do. Measured
on the reference profile: the old objective sat 22% under the real bill; the new one is within 0.1%
of it, and the builds priced more than 25% off the bill fell from 20 to 10. An hour's training
under the new prices found 9 cheaper builds across 6 quests and moved 16 part instances from
unpriced or flea-only onto trader stock. The shipped build history is re-seeded from that run.

**GP coins are money.** Ref prices everything in them, and the mod read all of Ref as barter, so
a Ref-only part could never be "buyable". It is now, at the game's own exchange rate.

## Also

- Fence is no longer read as a trader source: its stock is random and rotates, so a part it
  happened to hold read as buyable at a price that vanished on restock.
- Flea estimates on the panel refresh every minute instead of being frozen at whatever the first
  request saw (LiveFleaPrices rewrites them hourly).
- `train.ps1` runs the server from its own folder (it died on the logger config otherwise), and
  `train-all-threads.cmd` trains on every thread in a visible console.
- The bill line in the server log gains an "objective" column beside handbook and paid.

---

# Quest Tracker 1.17.0

**The quest list loads off the main thread.** Opening the tracker froze the game for the quarter
second the quest list took to fetch and parse; that runs on a worker now while the loading notice
is up. If the list does not arrive in time, the tracker builds the unlocked-only tree it has always
shown without the server half and asks again next open.

## Under the hood

- The README describes everything since 1.13.2: folded chains, the game's verdict on a build, the
  collapsing legend, the new settings and log lines. Two claims in it were wrong and are fixed: `?`
  is a button, not a key, and Enter goes to the first search match, not each in turn.
- Packaging refuses a release when the client and server disagree about a wire field, and when the
  shipped build history is stamped by an older solver. Both gates proven able to fail.

---

# Quest Tracker 1.16.0

**The toolbar fits at 1600 px.** When the bar's own arithmetic says the six status chips, the
"N shown" notice and the view buttons cannot all fit, the chips fold into one *Legend* item showing
the six glyphs in their colours (full names on hover), and the view buttons take their minimum
width. At 1920 nothing changes; at 1600 the notice gets its room back; at 1366 nothing overlaps but
the notice is still short.

**Dollar and euro trader prices are converted to roubles.** A Peacekeeper or Ref price was carried in
its own currency and labelled roubles, on the panel's cost labels and in the Cash totals, since the
builder shipped. The new measurement below is what exposed it.

**The weapon builder's objective is measured against the bill.** The optimiser minimises handbook
prices; the player pays trader or flea prices. The server now logs, per profile, the two totals over
the shared builds with something to buy, how many builds the two price the same parts more than 25%
apart, and the three widest. A measurement only - it decides whether the objective is ever changed.

---

# Quest Tracker 1.15.0

**Seven boxes become one.** A run of quests in single file - each unlocking exactly the next, one
trader - draws as one box with the series name, a done count and a progress bar, coloured by the
first quest still to do. Click to unfold; the `-` on the first quest folds it again; the tree
remembers what you opened for the session. A run stays open when something inside needs seeing: a
failed quest, a missing prerequisite, a search hit on a member's name, or a quest opened from the
panel. The **Chains** toolbar button (`C`) turns folding off; the setting persists. On the reference
profile: 148 runs holding 498 of 827 quests, the longest 21.

---

# Quest Tracker 1.14.0

**The game agrees with the build.** Every verdict this mod gave about a Gunsmith build was the mod
checking its own arithmetic. Each build your profile can assemble now carries one more line, computed
by the game's own hand-in test on the exact preset *Save as preset* would write: *The game will
accept this build*, or *The game would refuse this build: recoil 265.98 where the quest wants <= 250*
- the first test the trader's code fails, in the trader's numbers - or *checkable once the quest is
accepted*, because the test needs the quest's own condition, which the game gives the client only for
quests you hold. The check gun is unloaded and at full durability; the trader weighs the real one
loaded. Both halves must be 1.14.0.

**Magazine capacity is read from the magazine slot only**, the way the game reads it. No vanilla
build changes.

---

# Quest Tracker 1.13.3

**The tracker opens in half a second instead of a second and a half.** The first open of a session
spent most of its time turning the map's SVG into a mesh on the game's main thread. That runs on a
worker thread now: the map tab appears at once with its list and *Rendering the map...*, and the
picture arrives about a second later. Measured: 1,390 ms down to 499 ms.

**The server starts seven seconds sooner.** The map-marker build read every map's loot table off
disk on the boot path. It runs in the background now, and each table is read once per server run
instead of on every rebuild.

**A tarkov.dev outage no longer costs every boot.** A failure is remembered for a day (delete
`tarkovdev-last-failure.txt` to retry sooner); a successful download refreshes itself after a week
instead of being kept forever.

## Under the hood

- One log line per panel open with the time in each phase; the three per-profile server routes log
  their time on the first request and whenever they exceed 200 ms. At Debug they were never seen.
- The hover card finds its canvas once instead of every frame.

---

# Quest Tracker 1.13.2

The tree tells the truth about three kinds of quest it used to draw as ordinary locked boxes, and
the release itself is built by a script instead of by hand.

**Failed quests are their own state.** A quest the game has failed or expired drew exactly like one
you had not reached yet. It has its own colour and glyph now, and the box says whether the trader
will let you restart it.

**Quests you can never do wear a mark.** Wrong faction, another game edition, a seasonal event: the
detail panel always said so, the box never did. It wears a `!` now.

**A quest whose prerequisite is not installed wears a mark too.** A quest mod that requires a quest
from a mod you do not have drew as a quest you could start - it sat at the root of its tree with no
line into it. It wears a `?` now, and the panel lists the missing quest's id under *Missing
prerequisites*, which is what identifies the absent mod.

**Boxes are sized for the name they show.** The ten abbreviated series ("W. Prof." for Weapon
Proficiency, and nine more) were measured on the full name and drawn with the short one, so every
one of those boxes was wider than its title.

## Under the hood

- The panel logs how long opening it took, and how much of that was waiting on the server. Nothing
  had ever measured it; the next release is about making it smaller.
- The one-off check that asked the game's own hand-in test about saved presets (it accepted 6 of 6)
  is removed from the build. It ran once per profile and wrote its working to the game's log.
- Builds refuse to compile when the two copies of the version number disagree - which has happened
  once already, in 1.12.1.
- The release archive is assembled by `package.ps1` from a fixed list of files, and the script
  refuses any archive holding a file off that list. Until now every release was zipped by hand from
  the live install folder, one directory away from a third-party file that must never ship. The
  README is in the archive for the first time.
- The weapon-build proof claims in the published 1.13.1 notes are withdrawn; see that entry.

---

# Quest Tracker 1.13.1

The release that was actually published: 1.13.0 was built but never went out, so everything under
1.13.0 below shipped here. What 1.13.1 adds on top:

**Dollars count as money you hold.** The item watchlist's dollar template id had one wrong character
and had never matched anything. Roubles and euros were fine.

**A build you asked for during a re-solve no longer goes stale for a generation.** A request arriving
while the server was already computing your builds was refused and then forgotten, so the panel kept
the old answer until something else happened to ask.

**Opening a three-weapon quest asked the server three times per repaint.** *Old Friend's Request*
made three blocking calls for an answer that could not have changed between them; a not-ready answer
now stands for two seconds.

**Quests with no map stopped claiming items on the hideout.** An unlocated quest's items were fanned
across all nineteen location entries, six of which nobody can raid.

**A quest stating two thresholds on one stat is held to the stronger one**, and a modded "recoil at
least X" is read the right way round.

**The builds are re-seeded.** 56 of the 60 shipped builds come from a fresh training run and 4 are
kept from the previous seed; in total they cost within a percent of what they did.

## Withdrawn

The published notes for this release also claimed that 9 of 60 builds were *provably* the cheapest
possible, that a provably minimal build stops being searched, and that an `UNSOUND` line in the server
log would mean the mod had caught a wrong bound. Those claims are withdrawn: the machinery behind them
was removed from the code the same day, having contributed under 0.05% of the builds' cost and been
wrong three times in one session. The 1.13.1 build still prints the lines; they mean nothing. 1.13.2
is the first release without them. Irreducibility - every part proven necessary by taking it off and
re-checking - is kept.

---

# Quest Tracker 1.13.0

A whole-project code review, and what it found. Five features did not work at all, four of them
failing silently - nothing logged, nothing crashed, the screen just quietly did nothing.

## Five things that now work

**The pre-raid check comes back.** If one of a map's requirements had been placed by guesswork rather
than from a harvested zone, the whole map went dark: no summary, no "take with you" list, and a plain
ready-up button - throwing away every *other* requirement on that map to avoid overstating one. Those
rows are back. Only the green light is withheld, the button says **READY?**, and the sidebar says how
many of the map's requirements it could not place.

**Search finds traders.** The box has always said *"Search quests or traders"* and typing a trader
matched nothing, because the trader names were attached to the quests after the search index was
built. Typing `Prapor` works. The same bug was quietly randomising trader colours between sessions.

**21 quests get their item pins.** A quest that does not name its own map - *Lend-Lease - Part 1*,
*Vitamins - Part 1*, *Secret Benefactor*, *Delivery From the Past* and 17 more - was filed under a map
key that matches nothing, so its items were never pinned even though the server knew the exact spawn
coordinates. All 21 are pinned now, on the maps their items actually spawn on.

**Clicking a Do-next row keeps your place.** Opening the ninth row threw you back to the top of the
list, with the row you opened off screen.

**Quest boxes stop reading 0/2 on a quest you could hand in.** The tree counted hand-over objectives as
never done, however full your stash - while the Do-next row for the same quest correctly said "ready to
hand in". One rule serves both now.

## The weapon builder tells the truth about its own proofs

The verifier used a magic number to mean *"I cannot prove a lower bound for this"*, and every reader
took it for the strongest possible proof. A requirement nothing could bound was therefore marked
**provably minimal**, cached that way, counted in the boot log's total, and the adversarial search that
exists to disprove exactly such claims ran on the wrong set. That number is gone rather than guarded,
and requirements with no bound are the ones the falsifier now attacks hardest.

**Builds stop being permanently stale.** A profile doing Gunsmith early - a part already on a gun you
own, a part a trader only sells at a higher loyalty - had every request answer "this build is broken".
The panel never left the stale state while a background thread re-solved all sixty requirements in a
loop. That is the player the feature is most for.

## Also

- Hideout-craft unlocks were systematically under-ranked in **Do next**: the reward carries a hideout
  area where the mod read a trader, so the ranking scored all 31 of them as good as unreachable.
- 16 seasonal quests were not flagged as seasonal, and 9 more were called permanently unavailable
  during the very weeks the game shows them. The mod asks the game now instead of reimplementing it.
- Weapon presets appear without restarting the game (also fixed in 1.12.2).
- One preset per gun: a quest that wants more than one weapon - *Gunsmith - Part 21*, *Old Friend's
  Request* - saved every build under the same name, and the game de-duplicates by name, so you kept
  one gun and lost the rest. Presets are named `QT: <quest> - <weapon>` now.
- Opening the Maps tab, or its floor dropdown, no longer rebuilds the whole item watchlist.
- The builds panel no longer regenerates every trader's stock on every request.
- A dropdown re-picking its current value no longer stays painted over the page.
- A pin serving a finished quest and an unfinished one stops claiming there is nothing left to do.
- A hand-edited or third-party map-zone file with a missing list no longer costs that map its whole
  harvest.
- The six locations nobody can raid - the hideout, the Arena scene, four unshipped stubs - stop
  appearing as maps.
- A cached weapon build is now invalidated when a mod update makes it unassemblable, rather than being
  refused at the workbench.

## Documentation

**The README said nothing is written to your profile.** That stopped being true in 1.12.0: presets you
save yourself are ordinary saved builds and they survive uninstalling. The uninstall section says so,
and *Save as a weapon preset* is documented at last. The mod also points at its own page in the
launcher's mod list now.

Internally, the build enables the compiler's documentation analysers, which caught eight malformed doc
comments on the first run - one of them written during this very review. They do not catch every kind:
twenty-eight comment blocks carrying two summaries between them are still owed a pass, because that
particular mistake raises no warning at all.

---

# Quest Tracker 1.12.2

## Weapon presets appear without restarting the game

**Save as a weapon preset** wrote the preset correctly but could not show it, so it only turned up
after a trip back to profile select - which is what the button was built to avoid. It said so rather
than pretending ("Go back to the profile select for it to appear"), but saying so is not the feature.

The server echoes the saved build back to the client so it can drop it into the list the game is
already reading from. It was echoing it with the mod's own JSON settings instead of SPT's, and SPT
attaches item ids to a converter registered in its serializer rather than to the type - so every
`_id`, `_tpl` and `parentId` in that echo came out as an empty object. Not mis-shaped: **missing.**
The client threw on the first one.

It now serialises with SPT's own serializer, the same one that wrote the profile, so what the client
inserts and what is on disk agree by construction.

A second fault was sitting behind the first, and the same change fixes it: the item's `upd` block was
being renamed as well, so had only the ids been patched, presets would have loaded with no durability,
no fire mode and no found-in-raid flag - quietly, with nothing to see.

## Also

- Every reason a preset cannot be shown immediately is now written to the log. Four of them used to
  fail silently, and the only reason this bug was diagnosable is that it happened to land in the one
  case that spoke up.
- A successful insert says so in the log too.

---

# Quest Tracker 1.12.1

## Task items count as items you have

The pre-raid check told you to pack a task item - and there is no way to pack one. Quest items live in
the game's own task-item containers ("Task items on character" and "Task items in stash"), and they
cannot be dragged into a rig at all; the game carries them into the raid for you. The mod was only
looking in your gear and your stash, so a plan handed to you by the quest that grants it read
**"0 of 1 - elsewhere"**, and the ready-up button said **1 TO PACK** about something you already had
and could never lose.

Those containers now count as held on you, and the rows say **task item** rather than "on you", so it
is clear which part of your kit the game is carrying for you.

## Also

- The map's amber pre-raid summary no longer says "none of it is on you" on a map where something
  already is - it now says how much of the list you have.
- Both halves' assembly version was still stamped 1.11.0 while the mod reported 1.12.0. They agree
  again.

---

# Quest Tracker 1.12.0

## Send the build straight to your gun

A Gunsmith quest now has **Save as a weapon preset**. Press it and the build lands in the game's own
build list, ready to load onto the weapon in one click at the workbench - and the game's preset screen
offers to **buy the parts you are missing**, through its own purchase flow.

It appears immediately; no restart, no going back to profile select. Nothing is written unless you
press the button, and every preset is named `QT: <quest>` so it is obvious which are the mod's and
re-saving replaces its own rather than piling up.

## Do next actually ranks now

The tab sorted into four tiers and then, in practice, **alphabetically** - on a profile with seventy-nine
accepted quests the tiers were all ties, so the quest's name decided the order.

It now scores every quest on what it is worth doing: what it pays, how much of the tree it opens,
trader unlocks you can actually reach, how close it is to done, how close it is to startable, whether
Kappa needs it, and how much work it looks like.

**Pick what you are playing for** - Balanced, Kappa path, Fast levelling, Trader unlocks, Item
hoarding - and the order changes with it. **Every row says why it is where it is**: *"unlocks 12 ·
96k XP · 80% ready"*. Rows name their map, and a line tells you when several of your top ten are in
the same place, because travel between maps is the real cost.

## Click a quest and it opens where you are

Rows expand in place, as many as you like, with everything needed to do the quest: objectives and
live counts, what to take and how much you already hold, where to go, what it pays and unlocks, and
the full weapon build for a Gunsmith quest. The list keeps your place when you open one.

## Objectives were missing on 42% of quests

A quest could open and show requirements, rewards and unlocks but **no objectives at all** - no what
to do, no where to go. 173 vanilla quests and 38 modded ones showed none, and 149 more showed part of
the list.

The mod was filtering on a flag that is set on nothing: across the 1,606 objectives in the quest
database it is absent 1,080 times, false 526 times and **true not once**. Fixed, which also restores
objectives to the detail panel, the hover card, the node's progress count, the map sidebar, the
"take with you" list and search.

## Also

- Rewards know what they are worth in roubles, whether they are cash or gear, and which loyalty level
  an unlocked offer appears at.
- Trader reputation rewards name their trader. All 532 of them were being read from the wrong field
  and showed as a bare "Reputation +0.02". (Said 508 here until 1.13.0, which is the count of
  experience rewards.)

---

# Quest Tracker 1.11.0 — what's new since 1.8.5

## Gunsmith builds you can actually make

Open any Gunsmith quest (or any other quest that asks you to assemble a weapon) and the panel now
shows you a build that meets every number the quest checks — ergonomics, recoil, weight, magazine,
sighting range, size — with the parts the quest insists on already in it. Every part is a row you
can click to open in the inspect window.

**It only recommends parts you can get.** The build is worked out for *your* profile: what is loose
in your stash, what your traders sell you at your current loyalty levels, and — once you have flea
access — what is on the flea market. If the standard build names something you cannot buy, the mod
searches again using only what you can, and every build it shows you has passed an independent check
that it really meets the quest and really assembles.

**Each row tells you what the part costs you.** "already on the gun", "in your stash", a trader
price, "barter", or a flea estimate (flea prices move, so that one is marked as an estimate). The
headline adds it up: parts, roubles, and how many barters.

**If you own it but it's on a gun, it says so.** "fitted to your equipped MDR" beside the price.
The mod never assumes you will strip a weapon you use — that is your call, so it shows you both the
part and what a second one costs.

**When there is no build from what you can get, it tells you why — and what would fix it.**
Three different situations, kept apart:

- *Your traders are too low* — "to build it you need: Delta-Tek Sprut mount from Jaeger at
  loyalty 2". A goal, not a dead end.
- *You need the flea market* — "reachable once the flea market unlocks at level 15".
- *Nobody sells it* — no trader at any level stocks what this quest needs on your install.

In each case you also see the closest build it could make from what you have, and exactly which
number it misses and by how much ("recoil 280.67, needs <= 275 (short by 5.67)").

**It keeps the parts your gun already has.** The build is chosen to cost you the least — the
price of what you would have to buy plus a fixed allowance for each trip to a trader — so a part
already on the weapon's standard preset is preferred over an equivalent one you would have to go
and get.

**It gets better while you play.** The mod ships with builds worked out and checked in advance,
and every launch spends a few seconds in the background looking for cheaper ones for your
install. Anything it finds is used from the next start. It stops on its own; it will not sit
burning a core.

## Also

- The quest panel shows the build as a diff against the weapon's stock configuration: what is
  already fitted, what to swap (and for what), what to add.
- Height and width limits are now checked on the assembled gun, not only listed.
- A build that meets everything the mod can check but has a limit it cannot verify says so, rather
  than showing a green light it has not earned.

## For FIKA hosts

Builds are worked out per profile. One background worker serves every player on the server, at
the lowest priority, so six players never mean six times the CPU. A player's answer is refreshed
when their trader levels or stash change.

## Nothing to configure

There are no settings for any of this. The developer-only switches (training, falsification,
thread count, cache location) are environment variables that are off unless you set them; a
normal install never trains, never falsifies, and uses two threads for a few seconds at start.
