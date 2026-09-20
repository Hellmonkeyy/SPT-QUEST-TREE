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
- The colours are muted and the highlights held back, so the picture reads as a map and a coloured
  pin is the brightest thing on the screen.
- Water is left out. It renders as flat cyan placeholder blocks from any camera that is not the
  player's; the ground under it draws instead, which is what a map should show.
- A capture that came back too dark to be a map is **refused rather than written**, and says so:
  heavy weather takes the sun away, and the stretch that would have made a picture of it makes a
  field of noise. A capture taken under different light from the pictures already on disk also
  changes nothing, rather than merging into a visible seam - the line names both brightnesses and
  tells you to capture at a similar time of day or delete the folder and start over.
- Place names come from the scene: the extraction points, and optionally the cleaned-up bot zone
  names. **Map labels** is *Extracts only* by default, because forty zone names over a big map is a
  lot of text. DynamicMaps' artwork keeps its author's own labels whatever this is set to.

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
`user\mods\QuestTree\maps\bigmap\`; and the refusal when a second capture's light did not match the
first's, which fired exactly as intended and left the set alone.

**Not seen yet:** a captured picture actually drawn in the viewport - the setting prefers DynamicMaps
by default and DynamicMaps ships Customs, so the capture was catalogued but the artwork is what was
on screen; a merge that added ground from a second spot (the second press was the one the light test
refused); a second client downloading a set from the host; any map with more than one floor, so the
multi-floor camera and the floor picker over a captured picture are untested in game; the labels'
appearance on a real picture; the extent-only backdrop, since this install has DynamicMaps for all
eleven vanilla maps and no modded map to try it on; the three picture-order settings taking effect;
and the too-dark refusal, which was written *after* the rain capture that prompted it, along with the
capture light meant to keep a cloudy raid usable. The capture campaign that fills `maps\` is what
exercises all of it.

## What to look for

- Raid any map, press **Ctrl+F9**, then open the tracker on Maps. The picture should be of that map,
  the right way round, with the pins on the right buildings - the Dorms objective pin on Dorms is the
  quickest test. If it looks right, the geometry is right.
- The log line after a capture, in `BepInEx\LogOutput.log`: `QuestTree: captured bigmap "Ground"
  2236x1078 px (0.50 m/px), 2 tiles, 475 ms, ...`. A tail saying some of it was not drawn is an
  invitation to press the key again somewhere else - do, and watch the second line say how much was
  newly drawn.
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
