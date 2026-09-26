# Quest Tracker 1.19.0

Install over 1.18.5 - replace both DLLs, keep your config.

## Every location can have a map now, including the ones no mod draws

Until this release the Maps tab took its picture, its world bounds, its floors and even its place
names out of the DynamicMaps mod's art folder. A location that mod does not ship - every modded map,
and every map at all on an install without it - got the sentence "No map image for this location."
and a sidebar list.

The mod now measures each map itself, in raid, in the same pass that harvests the quest zones: the
rectangle the world occupies in game coordinates, and the height bands that read as floors. That is
enough to draw a map without any picture at all, and it is what a location shows after one raid on
it - a plain dark backdrop over its own rectangle, the alignment guides on, the floor picker
working, and every pin exactly where it belongs, with the line `No map picture yet - capture one in
raid (Ctrl+F9)` above it. A modded map is now a map with pins on it rather than a list.

## Capture the picture yourself, in a raid

**Ctrl+F9 inside a raid draws the map from straight above**, one picture per floor band, and writes
it to `BepInEx\plugins\QuestTree\captures\<key>\`. It takes a second or two per floor, spread over
frames as a handful of short hitches rather than one freeze, and the Maps tab draws it from then on.
The key is rebindable in F12 (**Map > Capture map picture key**), including to nothing at all, and
it needs *Harvest quest zones in raid* on, because the picture is drawn to exactly the rectangle that
harvest measured - which is why a pin lands on the right building without anything having to agree
twice.

**Press it again from somewhere else on the map.** The game streams distant chunks out of memory, so
any one capture of a large map has regions the camera found empty. Each press fills in what the
earlier ones could not see, and where two captures cover the same ground the pixel seen from closer
wins - so a map gets *sharper* the more often you capture it, instead of being overwritten with the
blurriest view. The log line says how much was newly drawn, how much was kept and how much is still
empty.

Smaller things that are all lessons from a picture that came out wrong:

- The topmost floor is photographed from 300 m up, so buildings, roofs and shadows are in it. From
  three metres over the ground every warehouse rendered as a flat slab, because its roof was behind
  the camera. A floor with another above it is still shot from just under that one - there, the
  ceiling is exactly what should be cut away.
- **The buildings are there at all** because of a second fix. A camera photographing a whole map
  judges a building against a kilometre-tall view, and the game's own level-of-detail rules answer
  that a 20 m warehouse is 2 % of the picture and need not be drawn - so it was not, at any camera
  height, and every capture came back as roads and bare terrain. The capture switches that selection
  off for the length of one render.
- **The ground is smooth** rather than a two-metre checkerboard: the terrain draws its averaged base
  texture for the same instant, because its detail textures tile about every two metres and two
  metres is four pixels from up there. Both this and the setting above are globals, and both are put
  back by the statement that changed them - your next frame is your own.
- **The roofs draw wherever you stood.** EFT hides distant geometry by switching renderers off rather
  than by letting a camera cull them, so a warehouse's roof and upper walls were off while its floor
  was drawn, and an early campaign capture of Customs had the boiler room, Big Red and several
  warehouses as patches of ground with a black wall outline round them. Every renderer those culling
  volumes hold is forced on for the whole of one floor - once before its first tile, put back after its
  last - because a stop on Customs flattens some twenty-seven thousand components out of them and doing
  that twice per tile would be a hitch of its own, twenty-four times over. The game's own force-enable
  could not be used at all: it switches twenty-five components a frame, and a tile is one frame.
  **Whole GameObjects a culling volume deactivates are switched on too**, not only the renderers on
  its component list: a volume holds both, and a roof that is a deactivated object cannot be brought
  back by enabling a renderer. Those are done one at a time, each inside its own guard, because
  activating an object runs the game's own Awake and OnEnable and one script that throws must not stop
  the rest of the roofs coming back; each one's state is recorded as it is found and put back. Two
  limits worth writing down: an object whose *parent* is the deactivated one is not climbed to, and the
  state a release restores is the state at the moment the hold was taken. The cost is stated rather
  than hidden: for the second or two a floor takes, the player's own frames draw that distant geometry
  too and the water on the Water layer in them is the flat capture blue, and both undo themselves when
  the floor is done.
- **The edges are clean.** Each tile is rendered at twice the resolution it is kept at and averaged
  back down, into a four-sample multisampled target the camera is told to use; a pixel half covered by
  a roof edge takes
  the colour of the samples that were drawn rather than a blend with the clear colour. Then a despeckle
  pass replaces a pixel that disagrees with all eight of its neighbours by their median - the specular
  glints and single black pixels that a smoothing pass keeps, because a smoothing pass reads an
  isolated outlier as an edge.
- The colours are muted and the highlights held back, so the picture reads as a map and a coloured
  pin is the brightest thing on the screen.
- **Water on the game's own Water layer is painted a flat map blue.** Its own shader has nothing to
  reflect from a camera that is not the player's, so it came back as flat cyan blocks by Dorms and a
  sheet of sky over the warehouse yard. Two answers to that were wrong before this one. Hiding those
  renderers took the Customs river out of the map and left its bed showing. Painting every renderer
  whose *shader* was named like water or like a puddle was worse: that matched 256 renderers on
  Customs, almost all of them wet-surface decals - the sheen over a yard, the bridge deck, an interior
  floor - and the picture came back with blue slabs all over the map. So the test is the LAYER and
  nothing else: Unity's layer 4, "Water", which is where EFT puts a river, a pond or the sea, and a
  merely wet-looking surface is left to draw itself against the grey reflection below. **No
  shader-name test is left anywhere in the capture.** On a map with nothing on that layer the water is
  photographed exactly as it draws, and the capture line states which of the two happened - a count of
  `water-layer renderers painted`, or `water drawn as is`.
- **A patch that came back flat cyan is painted over from its surroundings** before the exposure is
  measured. That is a separate pass and a COLOUR test, not a shader one: in raw linear light, green
  and blue both high with red low is a water quad the render flattened, and it is filled in from the
  ground around it.
- **Glass and transparent effects are left out**, for the same reason and with no such loss: from above
  they were blue streaks lying across the crane and the railway, and the crane and the rails are what a
  map should show.
- **Reflective surfaces reflect a flat grey** rather than the sky. A wet metal roof under an empty sky
  mirrors it and reads as a hole in the map, so the capture hands the scene a tiny neutral-grey
  reflection environment for the render at a modest intensity - a wet roof still looks wet, and still
  looks like a roof. Baked reflection probes are untouched, so an interior still reflects its own room.
- **The world ends where the map ends.** A capture's rectangle is padded and clamped past the playable
  area, so it takes in hillside and skybox terrain that looks exactly like the map and is not part of
  it, and nothing on the picture said so. The game's own navigation mesh is the one thing in the scene
  that knows where a player can go: it is rasterised into a mask, grown 8 m so no roof, yard or
  interior is caught, and everything outside it is **cut out of the picture** - written as transparent,
  fading out over the last 6 m, so the Maps tab's own dark plate is what you see where the map is not.
  It was a 45 % darken and a half desaturation first, and looking at it said the area should be gone
  rather than dimmed: a dimmed hillside is still a hillside somebody will try to walk to. Cutting it
  also solves the streamed-out chunks for free - a hole is transparent too, which reads as "no picture
  here" instead of as a black building. The per-floor line says how much: `... 34 % outside the
  walkable area`. Nothing else about a capture changes: the merge, the sidecar, the drawn mask and the
  exposure all work on the same numbers, and a host copy or a zip copy - a JPEG, which cannot carry
  transparency - is flattened onto that same dark plate. Onto the plate's colour and not onto black:
  black was the first answer and came back a visibly darker rectangle than the same capture looks
  like at home, two machines showing one map in two tones.
- A capture that came back too dark to be a map is **refused rather than written**, and says so:
  heavy weather takes the sun away, and the stretch that would have made a picture of it makes a
  field of noise. A capture taken under different light from the pictures already on disk also
  changes nothing, rather than merging into a visible seam - the line names both brightnesses and
  tells you to capture at a similar time of day or delete the folder and start over.
- **How a picture was rendered is written into its meta**, and two pictures are merged only when that
  matches: the capture light, the level-of-detail switch, the terrain texture. Tune any of them - or
  install a build that did - and the next capture replaces the map rather than merging into it, and
  says which recipe each was made under. That is how the black rain-era and building-less pictures get
  thrown away instead of being blended into good ones.
- Place names come from the scene: the extraction points and the cleaned-up bot zone names. **Map
  labels**, a new setting, shows all of them by default. The zone names are held back until you
  zoom in, so the wide view stays clean and nothing has to be switched off to get it. DynamicMaps'
  artwork keeps its author's own labels whatever this is set to.
- **The names are readable over a photograph.** White text, or the accent colour for an extract, on a
  nearly solid dark plate; a fixed size on screen at any zoom rather than a size in map units; a small
  dot on the exact spot with the plate above it; a name that would land on one already drawn is
  dropped, and the overlaps are worked out again when the zoom moves by a quarter. Zone names wait
  until you are zoomed in far enough for them to fit - below that a map's forty-odd tags are a wall of
  text over everything - and extract names are drawn at any zoom. At nine pixels on a dark plate at
  55 % the text was sitting on concrete and could not be read; both numbers moved because of that.
- **Every extract is marked with a green diamond**, whatever the label setting says. The names can be
  switched off; the extracts cannot, because on a picture of a map they are the first thing anybody
  looks for. It is an exit-sign green rather than the accent, so it cannot be mistaken for a quest
  pin; the legend names it, and the facts line counts the extracts - which is how a capture that found
  none at all announces that it wants taking again.

## Or capture a whole map without walking it

**Ctrl+Shift+F9 runs a capture campaign.** One press plans a grid of stops about 120 m apart across the
map, finds somewhere standable in each cell, teleports you from one to the next, takes a capture at every
stop, and puts you back exactly where you pressed it. Customs is 9 x 5 cells of 115 x 100 m - about 33
stops once the cells with nowhere to stand are dropped - and each stop is a full capture of the floors
and side views, with the 3D model rebuilt at every stop. The log's closing line gives the campaign's
real duration. 120 m because at 200 m the ground between stops was not covered well: every pixel of the
map has to be, at some stop, both loaded and that stop's nearest - which is the pixel the merge keeps.

**The campaign keeps a journal.** Every run appends its stop lines to
`captures\<key>\<key>.campaign.txt` beside the pictures, the last twenty runs of that map, because the
game log is gone the moment the game restarts and the run that captured 11 of 16 stops had nothing left
to say why by the time anybody looked. Nothing reads it but a person: the uploader and the packager
both ignore it.

**Start such a raid with AI set to none.** Nothing here disables bots, and a campaign is a player
standing still for a second and a half at some thirty places on the map. It writes nothing to you but a
position, through the game's own teleport; it does not touch health or god mode, it is local to you on
a Fika raid, and it stops itself if you die or the raid ends, saying which stop it got to.

**Or let it capture as you play.** *Capture the map automatically while I play* - off by default -
takes a capture every few seconds (*Seconds between automatic captures*, 5 by default, 2 to 120) once
you have moved 15 m since the last one, so a raid spent walking a map builds its picture by itself.
It **will** hitch every few seconds. It is a tool for a raid set aside for map-building, not
something to leave on while you play for real, and the Settings tab and the F12 description both say
so.

## Share them through the host

A capture costs one raid on one map, and a Fika group has one host and several players. **Share
captured maps** (on by default) offers each finished capture to the server one floor at a time as a
2048-px JPEG, and every client of that host picks up the maps it does not have itself on the first
Maps-tab open of a session.

The host decides. Uploads are refused unless it runs with `QUESTTREE_ACCEPT_MAPS=1`, because a
picture is the one thing a peer can post that everybody else then looks at; `tools/server-host.cmd`
in the source repo sets it and starts the server. A host that has not opted in says so once, nothing
is sent, and it is not asked again that session. The limits: one picture a post, up to 2.5 MB a
picture, eight floors and four side pictures a map, up to eight atlas pages a map at up to 6 MB each,
one 3D mesh a capture up to 48 MB (in 16 MiB parts past that size - an SPT server takes no request
body past 30,000,000 bytes), 132 MB a map and 1.5 GB in all on the host, 600 MB downloaded per
session (at least three minutes, and longer while it still arrives at 1 MB/s), 2 GB of other players'
maps kept on a client, and up to 240 seconds a mesh or atlas-page request. A map you captured yourself
is not downloaded back from the host. A solo player needs none of it - their own captures are read
straight out of their own folder.

## DynamicMaps is now a choice rather than a dependency

**Map pictures come from** has three settings: *DynamicMaps when it has the map, else my captures*
(the default, so nothing about an existing install's map changes), *my captures when I have one, else
DynamicMaps*, and *my captures only*. Nothing of DynamicMaps' artwork is bundled, copied or
redistributed either way, and each map's author is still credited under the map. A captured map
carries our own credit instead: `Map: captured in-game with Quest Tracker 1.19.0, 3 captures since
2026-09-19 (10:49)`.

## The map in three dimensions

**A captured map now opens in 3D**: the ground with its picture draped over it and its buildings
standing on it. **Drag** to move, **right-drag** to turn and tilt, **scroll** to come closer, and the
floor picker peels the storeys - the second floor is drawn standing on the first and on the ground, so
a multi-storey map reads as a building rather than a stack of slabs. Pins, extract diamonds and place
names sit on the ground at their real height and keep their size on screen. The **3D relief** toggle
beside the floor picker (and **Map view** in Settings) switches back to the flat picture at any time;
it is greyed out for a map with no relief captured, and a DynamicMaps map or a bare harvested
rectangle always draws flat. **Mirror map artwork** and **Extra map artwork rotation** do not apply
in 3D - the picture is laid on the ground by the coordinates it was measured over. The geometry is
built once per file and kept across repaints, so clicking a quest row does not rebuild a map.

**The same press measures the map's shape.** Beside the pictures, a capture writes
`<key>-mesh.bin`: the ground as a raycast grid at two metres a cell, cast from each floor band's own
camera height, and the buildings as geometry. The experiments measured the whole of Customs at 45 ms
through `RaycastCommand` against 290 ms one ray at a time, and found that colliders do not stream out
with the player - so the ground comes back complete from anywhere on the map and needs none of the
merging the pixels need. The buildings come from the renderers themselves - 184,000 of them on
Customs, filtered by size and by the layers the picture draws - taking each building's MOST detailed
level of detail whenever its source totals at most 1,000,000 triangles (the last real level, never an
impostor card, when it is bigger), reducing it with our own decimation (quadric edge collapse, on a
worker thread) to a budget set by its footprint, and reading the meshes only the graphics card holds
back asynchronously off their own buffers. A mesh's buffer target is never
written: doing so killed the game outright on 2026-09-22, and it is forbidden everywhere in this
feature. The work is spread over frames, capped at 3,000,000 triangles a map, and three log lines
say what was built and what was cut. A file is about 0.3 MB of ground plus up to a few tens of
megabytes of buildings for a map of Customs' size; the format allows 6,000,000 triangles and
12,000,000 vertices a file, and the host takes a mesh of up to 48 MB that inflates to at most 160 MB.

**The 3D map travels with the pictures.** The mesh goes up to the host on its own route after the
floors (`POST /questtree/maps/mesh`) and comes down with them (`POST /questtree/maps/meshfile`), so
one player's raid gives the whole group a map that pans and tilts. It rides the same single opt-in
(`QUESTTREE_ACCEPT_MAPS=1`) and the same rule as the pictures: a set whose capture built a mesh is
not served until both have arrived, so a borrowed map never names geometry the host does not hold.
The file is identified by its sha256 at every hop, and the host reads the whole of it before storing
it - its header, every building's heights and triangle indices, and that its rectangle, floors and
counts are the ones its pictures' meta states. **A mesh problem costs the mesh and never the map**: a
mesh the host can never use (unreadable, not fitting its own pictures, or too big for the host's
space) is refused once and the pictures are served without it, so the map draws flat; only a mesh the
host could not *write* leaves the floors waiting, dropped at the host's first start a day later. On
the way down, a mesh that does not match is left out and the pictures kept, while one that does not
arrive at all - a timeout, a dropped connection - leaves that map as it was for the session and is
fetched again, whole, on the next start. Nothing about it bumps a schema version: an older host stores
the pictures and ignores the mesh, and an older client never asks.

**The side pictures travel the same way.** The four oblique views a capture takes for the walls go up
through the floors' own route, one post each, after the floors and before the mesh, encoded as a floor
is (on the map backdrop, 2048 px on the long side, JPEG at q80, up to 2.5 MB), and come down through
the same image route. A set that names sides waits for every one of them as it waits for its floors -
but a side is never worth the map: one the host cannot use (not a JPEG, over the cap, not a view of
the capture box its meta describes, or posted empty because the client could not encode it) is
dropped from the set with one line in the host's log and the rest is served, never refused and never
flattened. On the way down, a side whose JPEG is not the size its meta states is left out; its walls
are tinted. A host from before sides refuses a side post as a floor it has no record of, rather than
storing it over floor 0, and the client goes on to the mesh.

**The atlas pages travel the same way.** A capture's atlas pages - up to eight 4096 px sheets of the
game's own building textures, which the 3D view drapes on the buildings - go up after the sides and
before the mesh, one post each, at their full size as JPEGs at quality 90 (80 for one that would pass
6 MB), up to 6 MB a page. A page that does not get through in 240 seconds is dropped and the upload goes
on; a page that does not arrive on the way down is fetched again, alone, the next session. A page is
never worth the map either: one the host cannot use (not a JPEG, over 6 MB, not the size its meta
states, or posted empty because the client could not encode it) is dropped with one line in the host's
log and the rest is served; the buildings drawn from it fall back to the side pictures and tints. The
host stores a page as `<key>-atlas-<n>.jpg` and rewrites its sha256 to the stored JPEG's, and a client
downloading the set holds each page to that sha and its stated size. Pages only dress the mesh's
buildings, so they come down only with a mesh, and a set served flat carries none. A host from before
pages refuses a page post as a floor it has no record of, and the client goes on to the mesh. The
per-map budget is 132 MB: eight floors and four sides at 2.5 MB, eight pages at 6 MB, a 48 MB mesh, and
6 MB of margin; the host's whole store is 1.5 GB - all eleven maps at that ceiling, though not a
twelfth set beside them.

**A throwaway diagnostic ships in this build, with no key bound**, said out loud because it is not a
feature: the mesh probe of the 3D experiments, which does nothing until you give it a key under F12 >
**Advanced > Mesh probe key (throwaway)**. Once bound, in a raid it writes
`BepInEx\plugins\QuestTree\captures\<map>.meshprobe.txt` - whether the game's own meshes can be read
back off the graphics card, and how much of the map its colliders cover from where you stand - and in
the menu `captures\menu.meshprobe.txt`, listing the loaded shaders, cameras and layers, with a small
test view in the bottom-left corner until the key is pressed again (it swallows clicks inside its own
512 px square while it is up). Nothing in the mod depends on it. It reads up to twenty scene meshes
and asks the graphics card for a copy of one, modifying none of them; the readback test has to
complete once in the menu before a raid will run it. It is kept out of the in-game Settings tab and
is meant to be removed again.

## Under the hood

- **The zone file is schema v2.** Each map's record now carries the measured extent - its four
  edges, which of the three sources produced it, and when - plus its floor bands with their names and
  height ranges, and the server copies both onto the marker payload. The extent is ranked NavMesh
  first, then Terrain, then BorderZone, in that order because a measured Customs raid said so: the
  border zones describe a small interior box that left 180 of 282 harvested zones outside, the
  terrain spans half again as much world as is played, and the NavMesh box sits within about 40 m of
  the hand-made reference bounds on a kilometre-wide map. Both halves refuse an extent that does not
  contain the zones already harvested for the map, so a wrong rectangle is dropped rather than drawn.
  A host older than the client still refuses the whole harvest by schema, as it did before, and says
  so in its own log.
- **Three new routes**, built on the zone harvest's pattern: `POST /questtree/maps/upload` takes one
  floor, `GET /questtree/maps` answers an index carrying a sha256 stamp per map, and `POST
  /questtree/maps/image` serves one floor. Floors accumulate in `.incoming\` and only ever move into
  place as a whole set, so a client that drops out after two floors of four leaves the host's
  existing pictures untouched. The client assembles a download in a staging folder and writes the
  meta last, so no half set is ever drawn and a download that dies is simply repeated. An older host
  answering with SPT's HTML, or a newer one answering with an index shape this build does not know,
  both fall back silently to whatever the client already had.
- **Packaging gates the payload.** The release ships whatever map sets exist under
  `Source\Tarkov-QuestTree-Server\maps\` - none to all eleven - and `package.ps1 -RefreshMaps` copies
  them from the install, refusing while the server is running (a host writes into that folder as
  sets arrive). There are six gates, and five of them fail the run: the folder layout (nothing but
  the floors `<key>\<key>-<level>.jpg`, the side pictures `<key>\<key>-side-<N|S|E|W>.jpg`, the
  atlas pages `<key>\<key>-atlas-<0..7>.jpg`, `<key>\*.map.json` and the map's own
  `<key>\<key>-mesh.bin`), 1.5 MB per image - side pictures included - and 6 MB per atlas page, the
  meta's schema against the constant the shipped client reads, and
  `tools/check-maps-pack.py`, which checks every floor's JPEG dimensions against its meta and its meta
  against the extent's own arithmetic, every side's JPEG size against its meta and its basis for unit
  length, every atlas page's JPEG size and sha256 against its meta, that no floor, side or page file
  goes unnamed, and holds a set's 3D mesh to the sha256, byte
  length, extent, floor levels and counts its meta states. The sixth is the payload's total size:
  printed on every run with the meshes' share of it, and a **warning** past 80 MB rather than a
  failure, because a mesh cannot be made smaller without losing the map. With 3 M-triangle meshes the
  warning is expected to fire; it says so when the meshes are most of the payload, and says the
  pictures grew when they are not.
  Each gate was proven able to fail against a planted fake set, one fault at a time. How many maps
  are covered is also a warning naming the missing ones, not a gate, because a map with no set falls
  back instead of breaking.
- **The render recipe is a field.** The meta's `render` field records what decides whether two pictures
  are pictures of the same thing, and any difference at all replaces the set instead of merging into
  it. The capture header also stopped printing a rendering path taken from the player's camera before
  the orthographic switch: that is not the path that renders, and a header that named it was a fact
  about the wrong camera.
- **The render recipe now carries twelve things**, not three: the light, the LOD bias, the terrain
  base-map distance, whether cyan water is painted out, whether the distance culling was forced
  visible, whether the grey reflection environment was in place, which generation of the water
  treatment drew it and whether that pass's paint shader was actually there (the `p` or `n` on the
  `wr` term, so a capture whose shader the platform stripped cannot merge into one that painted),
  the smoothing window, the despeckle pass, whether the walkable mask is alpha or shading, the
  supersampling factor, the multisampling level the device actually gave and which edition of the
  excluded-layer list drew it - which spells
  `own-1.5;lod1000;basemap0;water1;cull1;refl1;wr4p;smooth5;despeckle1;reach2;ss2;msaa4;layers2` on this
  machine. Each changes what a pixel is a picture of, so each has to force a replacement rather than
  a merge - which is why every set captured before this release is replaced by the first capture
  taken after it. Multisampling is asked for at 4, then 2, then 1, and the level achieved is
  recorded, since two machines that resolved differently did not make the same picture. Four rather
  than eight because a 2048 half-float tile at eight samples is four hundred megabytes of video
  memory on a machine that is also running a raid, and the difference on geometry that is already
  supersampled two by two is not something anybody will find in the picture. The camera is told to
  use what the target was granted, so the samples are used rather than merely allocated.
- **Memory is budgeted rather than hoped for.** One floor may work in **256 MiB** of arrays and
  textures, counted term by term at 26 bytes per output pixel - the float buffer, the drawn mask,
  two sets of distances, the picture, the sidecar texture and, on a merge, the previous picture and
  its sidecar - and the pixels per metre come down half a pixel per metre at a time, to a floor of
  one, until the floor fits. Lowering the scale rather than refusing the map, because a map at 2.5
  px/m is a map and one that threw an OutOfMemoryException is not, and deterministically, because
  the scale is part of what decides whether a later capture may be merged into this one. Interchange
  at 4 px/m is what died in a raid - "GetPixels: scripting array creation failed" on its first floor
  and an OutOfMemoryException on the other two - so it comes down a step or two instead, and the
  header says which and why. The step it lands on is the arithmetic on the rectangle THAT INSTALL's
  harvest measured, not a constant: on the 965x925 m Interchange this one now measures, 4 px/m is
  3860x3700 and 354 MB a floor and it captures at 3 px/m and 199 MB (`4 px/m would need 354 MB a
  floor, over the 256 MB budget, so 3 px/m (199 MB)`); the earlier 932x896 m measurement of the same
  map settled at 3.5 px/m and 254 MB. Customs at 4 px/m is 239 MB on its 1118x539 m, inside the
  budget and untouched, which also keeps the sets already on disk mergeable. Outside that count
  and small: the 34 MB half-float staging texture, one per capture, and the video memory the tile
  target holds. Between floors everything the floor held is released and a collect runs by hand, the
  one place this mod does that, because these are large-object-heap allocations and the next floor
  asks for the same sizes a frame later.
- **Two crashes the Customs campaigns found are fixed.** A half-float HDR render can hand back a
  NaN - a shader dividing by a zero-length vector is the usual way - and a NaN is false to every
  comparison that would have rejected it, so it travelled into the smoothing filter, whose
  range-weight lookup casts a float to an int; Mono casts a NaN to int.MinValue where desktop .NET
  gives 0, which is an index a long way outside the array, and four stops of a campaign died of it.
  NaN is now treated as "not drawn" in all three places that can see one. Separately, every readback
  goes through GetPixelData, a view of the texture's own memory, instead of GetPixels: the managed
  arrays it handed back - 16 bytes a pixel for each of a hundred and twenty-eight staging bands a
  floor, plus a whole decoded picture per merge - are what fragmented the heap that the allocation
  failures above fell out of.
- **The walkable mask** is a 2 m grid over the extent - 150 thousand cells on a kilometre of map -
  built by marking every cell a NavMesh triangle covers (bounding box plus a barycentric test on the
  cell centre, so one large triangle fills its cells rather than marking their corners), then a
  two-sweep chamfer distance transform outward, then a weight that is full inside the 8 m dilation and
  ramps to zero over 6 m. That weight becomes the picture's alpha, which is why a capture's PNG is RGBA
  now and a quarter larger than it was; the colour underneath is left exactly as it was developed, so
  the exposure is untouched and a merge stays byte-stable - the mask is a property of the map and is
  identical in every capture of it. No NavMesh means no mask and nothing cut, which is exactly how the
  picture looked before this existed.
- **The percentage-pin rule counts item spots too.** A quest with a harvested position on a map loses
  its percentage-placed pins there, and a harvested *item* spot now counts as that coverage in the
  same way a trigger zone does: it is a real world position for that quest from a loaded scene. The
  rule lives in one pure, static method so it can be exercised on hand-built lists rather than only
  by booting a server, and the boot line now says how many percentage pins each map kept and how many
  were dropped, instead of one number that quietly meant the kept ones.
- **`package.ps1 -RefreshBuilds`** copies the trained weapon-build cache from the install over the
  seed the release ships - the last hand-copy in the release process, and the one whose failure mode
  was shipping the previous training run. It prints the stamp, the build count and the trader / flea /
  unpriced split of both files before the copy and of the seed after it, because the interesting
  failure is not a copy that breaks but one that works and ships a worse seed; and it refuses outright
  while `SPT.Server.exe` is running, since a training server rewrites that file as it goes and the
  user may be in a raid on it. The training launchers now also accept map uploads, so a capture raid
  run against a training server can hand its pictures over.
- **Two new tools.** `tools/check-capture.py` checks a fresh capture against its own meta and against
  the server's zone file for that map - the case it exists to catch is an extent three metres off,
  which draws every pin slightly wrong and still looks like a picture. `tools/server-host.cmd` starts
  the server as a picture host. `tools/check-dtos.py` covers the new DTOs on both sides.

- **Six review passes over everything since 1.13.1.** Client data, the weapon solver, the views, the
  marker payloads, the map pipeline and the tooling were each read by a reviewer told what to break,
  and the findings were fixed in place - the full list is in the CHANGELOG. Two of them change what
  you will notice: a map set downloaded from a host keeps its extract labels as extracts (the label
  kind now travels on the wire), and the weapon solver's remembered builds are solved again once on
  the first boot of this version, because the solver is now version 12.
- **Map transfers use their own HTTP client.** Every picture, atlas page and mesh post, up or down,
  goes out as exactly one request with one deadline (30 s for a picture, 240 s for a mesh part or a
  page) that aborts it, or gives it up ten seconds later if the socket will not - the same bytes SPT's
  own client would send, without its three silent retries
  and its 100 s cut, which made a transfer needing more than 100 s impossible. The map index is still
  asked through SPT's RequestHandler. A mod that patches RequestHandler or SPT's HTTP client to reroute
  traffic (a proxy or relay mod) no longer sees map transfers; Fika does not do this.

## What has been seen on screen, and what has not

Worth being exact about, because a map is a thing you look at and most of this has been looked at
only once.

**Seen:** the extent measured and stored for Customs (`extent for bigmap 1118x539 m (navmesh), 1
floors, 0 of 282 zones outside`); one full capture of Customs end to end - rendered in two tiles in
415 ms, written as a 3.5 MB picture, and found and catalogued by the Maps tab on the next open (`1
captured map(s) (1 floors) to draw from`); an upload accepted and stored by a local host under
`user\mods\QuestTree\maps\bigmap\`; the refusal when a second capture's light did not match the
first's, which fired exactly as intended and left the set alone; **the captured picture itself drawn
in the viewport with the pins over it**, once the picture order was set to prefer captures, and
zoomed in far enough to read the ground; and **several presses merging into one set** - by the end
with no holes left in it and the colour right.

**Several whole campaigns have been run on Customs and looked at.** The one with the forced culling,
the supersampling, the despeckle, the walkable mask and the new labels in had roofs on the buildings
everywhere, clean edges, plated names that could be read over the photograph, a green diamond on
every extract, and the unreachable ground marked off. Two later ones added the rest of the picture
work: **the cut-out edge** drawn against the panel rather than argued for, **the grey reflections**
on the wet roofs, **the campaign journal** written and still there after a restart, and **the
four-sample target** produced. Those runs are also where the water pass's history comes from: the
build that painted every water-*shader* renderer blue put **blue slabs** over yards, the bridge deck
and interior floors on screen, which is what the layer test replaced, and the capture taken after
that fix showed them gone.

**A multi-floor map has been captured.** Interchange came back with **three floors**, and its
interior floors rendered rather than coming back as empty bands - so the multi-floor camera, which
shoots a floor with another above it from just under that one, does what it was written to do.

**Seen on 2026-09-23:** **Big Red with its roof**, in the Customs picture after two campaigns on
the build that draws the HighPolyCollider layer (the probe run inside it found the building's
walls-and-roof mesh there, excluded by name).

**Not seen yet.** **Everything three-dimensional.** No capture has run the relief or the building
readback for real, no 3D map has been drawn on a screen, no mesh has crossed to a host: the first
Customs press on this build writes `bigmap-mesh.bin` and its three log lines, `tools/check-capture.py`
is its gate, and the Maps tab is where the ground, the draped picture, the standing buildings, the
cut-out edge (whether the Standard shader's cutout variant survived this game's shader stripping),
the marker heights and the label culling are judged. **A capture that MERGES.** Every capture before the ARGB32 fix replaced the whole
picture, so no set on disk has yet been made of more than one capture; the first capture after it
should log the previous picture as read and keep the far side of the map while it improves the
near one - the case to look at is a capture inside Big Red after a campaign, which used to erase
the factory side. **The garage band on Interchange** - the three-floor capture above ran the older
floor-banding threshold, and the current one should find a fourth band for the garage, which no
capture has produced. **The memory budget lowering a scale inside a raid**: the arithmetic is
checked by hand and printed, but no raid has yet logged the header line that says it stepped a map
down. **The native readbacks in a raid at all** - no capture has yet been taken on the build that
has them, and what they exist to fix is heap fragmentation over many captures, which only shows in a
campaign that runs to the end without an allocation failure. Beyond the picture: automatic capture
has not been left on for a raid; no second client has downloaded a set from the host; the floor
picker over a captured multi-floor picture has not been driven; the extent-only backdrop is
untested, since this install has DynamicMaps for all eleven vanilla maps and no modded map; the
other two picture-order settings are untried (prefer-captures is the one that was exercised); and
the too-dark refusal was written *after* the rain capture that prompted it, along with the capture
light meant to keep a cloudy raid usable. The capture campaign that fills `maps\` is what exercises
the rest.

## What to look for

- Raid any map, press **Ctrl+F9**, then open the tracker on Maps. The picture should be of that map,
  the right way round, with the pins on the right buildings - the Dorms objective pin on Dorms is the
  quickest test. If it looks right, the geometry is right.
- The log line after a capture, in `BepInEx\LogOutput.log`: `QuestTree: captured bigmap "Ground"
  2048x988 px (0.55 m/px), 2 tiles, 475 ms, ...` - that is Customs at the 2048 resolution setting,
  which is what the run above was taken at; the default 8192 setting makes it 4472x2156 px
  (0.25 m/px) across fifteen tiles. A tail saying some of it was not drawn is an
  invitation to press the key again somewhere else - do, and watch the second line say how much was
  newly drawn.
- **The water.** No blue slabs anywhere: not over a yard, not on the bridge deck, not across an
  interior floor. Whatever the map has on the Water layer should read as water rather than as a sheet
  of reflected sky, and the capture line says which case you got - a count of `water-layer renderers
  painted`, or `water drawn as is`, which means the map had nothing on that layer and its water was
  photographed as it draws. The wet roofs should look like roofs rather than mirrors, and the blue
  streaks over the crane and the railway should be gone.
- **The cut-out edge.** The picture should fade out into the panel where the playable area stops, over
  a few metres rather than at a line, and nothing inside the map - no roof, no yard, no interior -
  should be caught by it. The capture line says how much of the floor was outside. A transparent patch
  in the middle of the map is a hole, not the edge: press the key again somewhere else and it fills.
- **The campaign journal.** After a campaign, `captures\<key>\<key>.campaign.txt` should hold that
  run's stop lines with timestamps, and still hold them after the game is restarted.
- **The names and the extracts.** Every extract should carry a green diamond at any zoom, with
  "extract" in the legend and a count in the facts line above the sidebar. Names should be readable at
  a glance over the photograph, the same size however far you zoom, never stacked on each other, and
  the zone names should appear only as you zoom in.
- **The campaign key.** Start a raid with AI set to none, press **Ctrl+Shift+F9**, and let it run. It
  should name each stop as it goes (`campaign stop 4 of 16 at ... - captured.`), finish with a count
  and a time, and leave you standing where you pressed it. Then look at the map: it should be whole.
- Pick a map DynamicMaps ships and switch **Map pictures come from** to *my captures when I have
  one*. The picture should change to yours on the repaint, and the credit line under it should stop
  saying "via DynamicMaps".
- On a map you have never raided, check the backdrop: a dark rectangle with guides and pins beats the
  old sidebar-only message, and the line above it should say `No map picture yet`.
- Start the server from `tools/server-host.cmd` and capture something. Its console should say
  `map uploads from clients are accepted (QUESTTREE_ACCEPT_MAPS=1).` and the client should log
  `capture of <map> uploaded to the host`. Start it normally instead and the client should say the
  host does not accept map pictures, once.
- Everything that is not the map should be exactly as it was in 1.18.5.
