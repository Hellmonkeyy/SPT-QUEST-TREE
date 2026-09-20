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
- The colours are muted and the highlights held back, so the picture reads as a map and a coloured
  pin is the brightest thing on the screen.
- Water is left out. It renders as flat cyan placeholder blocks from any camera that is not the
  player's; the ground under it draws instead, which is what a map should show.
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
- Place names come from the scene: the extraction points, and optionally the cleaned-up bot zone
  names. **Map labels** is *Extracts only* by default, because forty zone names over a big map is a
  lot of text. DynamicMaps' artwork keeps its author's own labels whatever this is set to.

## Or capture a whole map without walking it

**Ctrl+Shift+F9 runs a capture campaign.** One press plans a grid of stops 200 m apart across the
map, finds somewhere standable in each cell, teleports you from one to the next, takes a capture at
every stop, and puts you back exactly where you pressed it. Customs is about sixteen stops and a
couple of minutes. 200 m is the spacing the merge wants: at half a metre to the pixel that is 400 px
between stops, comfortably inside the region the game keeps loaded around a player, so every pixel of
the map is, at some stop, both loaded and that stop's nearest - which is the pixel the merge keeps.

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
- **The render recipe is a field.** The capture camera is orthographic, and Unity's built-in pipeline
  does not give an orthographic camera deferred shading - it falls back to forward whatever the
  copied settings say, which the code used to claim otherwise and now states, and which is why the
  capture light may carry a narrow culling mask and per-pixel lighting and have them honoured. The
  meta's `render` field records the three things that decide what a pixel is a picture of - the
  light's intensity, the level-of-detail bias, the terrain base-map distance - and any difference at
  all replaces the set instead of merging into it.
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

That last pair is also how the two things this release fixes last were found, both of them visible
in that viewport and neither of them a thing a log line would have said: every building drawing as a
flat footprint at both camera heights (Unity's LOD selection culling whole objects against a 512 m
orthographic view, now overridden for the render), and the terrain's detail textures tiling as a
two-metre checker (now forced to the terrain's smooth base map for the render). The pictures that
showed both were taken before either fix; a capture taken now does not merge into them, by design.

**Not seen yet:** the buildings and the smooth ground the last two fixes are for - they are written
against a diagnosis, not against a picture, and the next capture is what confirms them; **the campaign
key**, which has not been pressed in a raid, so the grid, the teleports and the return to where you
pressed it are untried; **automatic capture**, which has not been left on for a raid; a second
client downloading a set from the host; any map with more than one floor, so the multi-floor camera
and the floor picker over a captured picture are untested in game; the labels' appearance on a real
picture; the extent-only backdrop, since this install has DynamicMaps for all eleven vanilla maps and
no modded map to try it on; the other two picture-order settings (prefer-captures is the one that was
exercised); and the too-dark refusal, which was written *after* the rain capture that prompted it,
along with the capture light meant to keep a cloudy raid usable. The capture campaign that fills
`maps\` is what exercises all of it.

## What to look for

- Raid any map, press **Ctrl+F9**, then open the tracker on Maps. The picture should be of that map,
  the right way round, with the pins on the right buildings - the Dorms objective pin on Dorms is the
  quickest test. If it looks right, the geometry is right.
- The log line after a capture, in `BepInEx\LogOutput.log`: `QuestTree: captured bigmap "Ground"
  2236x1078 px (0.50 m/px), 2 tiles, 475 ms, ...`. A tail saying some of it was not drawn is an
  invitation to press the key again somewhere else - do, and watch the second line say how much was
  newly drawn.
- **The buildings.** Zoom in on a captured picture: warehouses, Dorms and Big Red should be buildings
  with roofs and shadows, not flat footprints, and the ground between them should be smooth rather
  than a two-metre checkerboard. Both are new in this release and neither has been seen yet.
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
