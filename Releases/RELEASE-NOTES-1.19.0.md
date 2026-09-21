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
  could not be used at all: it switches twenty-five components a frame, and a tile is one frame. The
  cost is stated rather than hidden: for the second or two a floor takes, the player's own frames draw
  that distant geometry too and their water is the flat capture blue, and both undo themselves when the
  floor is done.
- **The edges are clean.** Each tile is rendered at twice the resolution it is kept at and averaged
  back down, into a four-sample multisampled target the camera is told to use; a pixel half covered by
  a roof edge takes
  the colour of the samples that were drawn rather than a blend with the clear colour. Then a despeckle
  pass replaces a pixel that disagrees with all eight of its neighbours by their median - the specular
  glints and single black pixels that a smoothing pass keeps, because a smoothing pass reads an
  isolated outlier as an edge.
- The colours are muted and the highlights held back, so the picture reads as a map and a coloured
  pin is the brightest thing on the screen.
- **Water is painted a flat map blue.** Its own shader has nothing to reflect from a camera that is not
  the player's, so it came back as flat cyan blocks by Dorms and a sheet of sky over the warehouse
  yard. Hiding the water renderers - which is what the build before this did - fixed that and took the
  Customs river out of the map, leaving its bed showing, which is worse: a map of Customs without its
  river is a map missing a landmark. So the water is drawn, every surface of it, in a colour that reads
  as water on a map. Anything the shader test misses is still painted out from its surroundings before
  the exposure is measured, and that test now reads every material a renderer has rather than only its
  first.
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
  transparency - is flattened onto the same black the viewport puts behind it.
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
  labels** shows all of them by default now - the zone names are held back until you zoom in, so the
  wide view stays clean and nothing has to be switched off to get it. DynamicMaps' artwork keeps its
  author's own labels whatever this is set to.
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

**Ctrl+Shift+F9 runs a capture campaign.** One press plans a grid of stops 200 m apart across the
map, finds somewhere standable in each cell, teleports you from one to the next, takes a capture at
every stop, and puts you back exactly where you pressed it. Customs is about sixteen stops and a
couple of minutes. 200 m is the spacing the merge wants: at half a metre to the pixel that is 400 px
between stops, comfortably inside the region the game keeps loaded around a player, so every pixel of
the map is, at some stop, both loaded and that stop's nearest - which is the pixel the merge keeps.

**The campaign keeps a journal.** Every run appends its stop lines to
`captures\<key>\<key>.campaign.txt` beside the pictures, the last twenty runs of that map, because the
game log is gone the moment the game restarts and the run that captured 11 of 16 stops had nothing left
to say why by the time anybody looked. Nothing reads it but a person: the uploader and the packager
both ignore it.

**Start such a raid with AI set to none.** Nothing here disables bots, and a campaign is a player
standing still for a second and a half at sixteen places on the map. It writes nothing to you but a
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
is sent, and it is not asked again that session. The limits: one floor a post, up to 2.5 MB a floor
and eight floors a map, 20 MB a map and 300 MB in all on the host, 60 MB downloaded per session. A
solo player needs none of it - their own captures are read straight out of their own folder.

## DynamicMaps is now a choice rather than a dependency

**Map pictures come from** has three settings: *DynamicMaps when it has the map, else my captures*
(the default, so nothing about an existing install's map changes), *my captures when I have one, else
DynamicMaps*, and *my captures only*. Nothing of DynamicMaps' artwork is bundled, copied or
redistributed either way, and each map's author is still credited under the map. A captured map
carries our own credit instead: `Map: captured in-game with Quest Tracker 1.19.0, 3 captures since
2026-09-19 (10:49)`.

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
  them from the install. Five gates fail the run: the folder layout (nothing but `<key>\*.jpg` and
  `<key>\*.map.json`), 1.5 MB per image, 40 MB in total, the meta's schema against the constant the
  shipped client reads, and `tools/check-maps-pack.py`, which checks every floor's JPEG dimensions
  against its meta and its meta against the extent's own arithmetic. Each was proven able to fail
  against a planted fake set, one fault at a time. How many maps are covered is a **warning** naming
  the missing ones, not a gate, because a map with no set falls back instead of breaking.
- **The render recipe is a field.** The meta's `render` field records what decides whether two pictures
  are pictures of the same thing, and any difference at all replaces the set instead of merging into
  it. The capture header also stopped printing a rendering path taken from the player's camera before
  the orthographic switch: that is not the path that renders, and a header that named it was a fact
  about the wrong camera.
- **The render recipe now carries eleven things**, not three: the light, the LOD bias, the terrain
  base-map distance, whether cyan water is painted out, whether the distance culling was forced
  visible, which generation of the water treatment drew it, the smoothing window, the despeckle pass,
  whether the walkable mask is alpha or shading, the supersampling factor and the multisampling level
  the device actually gave. Each
  changes what a pixel is a picture of, so each has to force a replacement rather than a merge - which
  is why every set captured before this release is replaced by the first capture taken after it.
  Multisampling is asked for at 4, then 2, then 1, and the level achieved is recorded, since two
  machines that resolved differently did not make the same picture. Four rather than eight because a
  2048 half-float tile at eight samples is four hundred megabytes of video memory on a machine that is
  also running a raid, and the difference on geometry that is already supersampled two by two is not
  something anybody will find in the picture. The camera is told to use what the target was granted, so
  the samples are used rather than merely allocated.
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

**Two whole campaigns have been run on Customs and looked at.** The second of them - with the forced
culling, the supersampling, the despeckle, the walkable mask and the new labels in - had roofs on the
buildings everywhere, clean edges, plated names that could be read over the photograph, a green diamond
on every extract, and the unreachable ground marked off. That is most of this release's picture work
confirmed on screen rather than argued for, and it is also where the rest of it came from: everything
below is a fault that picture showed.

**Not seen yet**, all of it written against that picture rather than confirmed by a newer one: **the
cut-out** - the unreachable area was shaded grey in that capture, and this build makes it transparent
instead, so nothing yet shows how the fade reads against the panel or that no roof or yard is caught by
it; **the blue water**, which replaces having hidden it and is meant to put the Customs river back on
the map; **the grey reflections**, for the wet roofs that mirrored the sky; and **the campaign journal**
and the four-sample target, neither of which has been produced once. Beyond the picture: automatic
capture has not been left on for a raid; no second client has downloaded a set from the host; no map
with more than one floor has been captured, so the multi-floor camera and the floor picker over a
captured picture are untried; the extent-only backdrop is untested, since this install has DynamicMaps
for all eleven vanilla maps and no modded map; the other two picture-order settings are untried
(prefer-captures is the one that was exercised); and the too-dark refusal was written *after* the rain
capture that prompted it, along with the capture light meant to keep a cloudy raid usable. The capture
campaign that fills `maps\` is what exercises the rest.

## What to look for

- Raid any map, press **Ctrl+F9**, then open the tracker on Maps. The picture should be of that map,
  the right way round, with the pins on the right buildings - the Dorms objective pin on Dorms is the
  quickest test. If it looks right, the geometry is right.
- The log line after a capture, in `BepInEx\LogOutput.log`: `QuestTree: captured bigmap "Ground"
  2236x1078 px (0.50 m/px), 2 tiles, 475 ms, ...`. A tail saying some of it was not drawn is an
  invitation to press the key again somewhere else - do, and watch the second line say how much was
  newly drawn.
- **The water.** The Customs river and the ponds should be on the map, in a flat blue that reads as
  water - not a sheet of reflected sky, and not a dry riverbed. The wet roofs should look like roofs
  rather than mirrors, and the blue streaks over the crane and the railway should be gone.
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
