using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// The two ways to photograph a whole map without walking it, both of them drivers of the one
    /// capture in <see cref="MapCapture"/> rather than second renderers of their own:
    ///
    ///   - the CAMPAIGN key: one press teleports the player across a grid of standable spots covering
    ///     the map, captures at each, and puts the player back where they pressed it;
    ///   - AUTO capture: while the player plays, a capture every few seconds once they have moved,
    ///     so a raid spent walking the map builds the picture by itself.
    ///
    /// Why either is needed at all. The game streams distant terrain and building chunks OUT, so one
    /// capture from one spot has whole regions the camera found empty - the west third of Customs,
    /// photographed from the east end. <see cref="MapCapture"/> already answers that by MERGING each
    /// capture into the one on disk, nearest capture winning per pixel, so a second capture from the
    /// other end fills the first one's holes. What it cannot do is walk: until now a complete picture
    /// of Customs meant a player physically crossing a kilometre of it, pressing the key every few
    /// hundred metres. That is the whole job here - the moving, not the rendering.
    ///
    /// The merge is also what makes a grid of stops the right shape. A stop 200 m from its neighbour
    /// is 400 px away at the 0.5 m/px a 4096-wide Customs capture works out to, and the region the
    /// streamer had loaded around each observed stop on Customs was far wider than that - so every
    /// pixel of the map is, at some stop, both LOADED and the nearest stop's own pixel, which is
    /// exactly the pixel the merge keeps. Cells further apart than the loaded radius would leave the
    /// nearest-wins rule choosing between two pictures that are both empty there.
    ///
    /// What this deliberately does NOT do:
    ///   - it does not disable bots. Nothing here makes the player safe; a campaign run with AI on is
    ///     a player standing still for a second and a half at eighteen places on the map. Start the
    ///     raid with AI set to none. Said in the setting's own description and in the Settings tab.
    ///   - it does not touch god mode, health, or any other player state. The only thing it writes to
    ///     the player is a position, through the game's own <c>Player.Teleport</c>.
    ///   - it does not run on a Fika headless client (no player to move) and does not sync the
    ///     teleport to anyone else: the teleport is the local one, <c>onServerToo</c> left at its
    ///     default false. Which is why the campaign REFUSES a raid anybody else is in - see
    ///     <see cref="ModEnvironment.RaidIsSolo"/>. Fika syncs the local player's position to its
    ///     peers from the transform, so a campaign in a co-op raid would either drag the other
    ///     players' copies of you across the map every second and a half or leave them looking at a
    ///     ghost; a map-building tool is not worth doing that to somebody's raid. Automatic capture
    ///     has no such gate, because it moves nobody.
    /// </summary>
    internal sealed class MapCampaign : MonoBehaviour
    {
        /// <summary>Side of one campaign grid cell, in metres: one stop per cell, at its centre.
        ///
        /// 200 m because of the merge rule and the streamer together. At 4096 px across a 1118 m
        /// Customs the picture is about 0.5 m/px, so 200 m is ~400 px between stops - comfortably
        /// inside the radius the streamer had loaded around a capture point on Customs, which is what
        /// the nearest-capture-wins merge needs (see the class comment). It also keeps the number of
        /// stops, and so the length of a campaign, sane: Customs' 1118x539 m extent is 6x3 = 18 cells
        /// at this size, a few minutes of captures.</summary>
        internal const float CampaignCellMetres = 200f;

        /// <summary>How far from a cell's centre a standable point may be found, in metres, on a grid
        /// whose cells are the nominal size. A cell with nothing walkable inside this radius - open
        /// water, an interior courtyard, the void past a map's edge - is dropped from the plan rather
        /// than captured from somewhere else.
        ///
        /// A CEILING, not the radius used: see <see cref="SampleRadius"/>, which takes the smaller of
        /// this and a third of the actual cell, since the grid's cells are as small as the extent
        /// divided by a whole number of them and 60 m around the centre of a 105 m cell reaches into
        /// the neighbour's ground.</summary>
        private const float CampaignSampleRadius = 60f;

        /// <summary>The largest share of a cell's shorter side the sample radius may be - see
        /// <see cref="SampleRadius"/>. Just under a third, so a spot found for one cell is still
        /// unambiguously in that cell and two neighbouring cells cannot both settle on the same patch
        /// of ground and photograph it twice.</summary>
        private const float CampaignSampleShare = 0.3f;

        /// <summary>Metres the player is put ABOVE the sampled point. The sample is a point ON the
        /// walkable surface, and materialising a capsule exactly there can leave it interpenetrating
        /// the ground; two metres is clear of that and of the kerbs and debris a NavMesh is draped
        /// over.
        ///
        /// Why two and not five. The game DOES see this as a fall - the claim that it does not was
        /// wrong: <c>Player.Teleport</c> sets the transform and then calls
        /// <c>MovementContext.ResetFlying</c>, which re-bases the fall height to the NEW position,
        /// which is this one, two metres up. <c>CheckFlying</c> then measures the drop from there to
        /// the ground and hands it to <c>ActiveHealthController.HandleFall</c>, which does nothing
        /// below the globals' <c>Health.Falling.SafeHeight</c> - 3 m on this server. So what the
        /// teleport itself saves the player is the 500 m fall; the two metres are a real fall with a
        /// metre of headroom, which is why this number stays small. Raising it to four would break
        /// both of the player's legs at every stop.</summary>
        private const float CampaignTeleportRise = 2f;

        /// <summary>Seconds waited between arriving at a stop and starting its capture, for the
        /// streamer to bring the surroundings in. TUNABLE, and the one number to change if captures
        /// come back with holes around the stop: too short and the picture is of chunks that had not
        /// loaded yet, too long and a campaign takes minutes. 1.5 s is about what a capture itself
        /// costs, and the merge forgives a partly-loaded stop anyway - its empty pixels lose to a
        /// neighbour's loaded ones.</summary>
        private const float CampaignSettleSeconds = 1.5f;

        /// <summary>Stops that may fail, in a row, before the campaign gives up - a capture that would
        /// not start, or a teleport that threw. Two rather than one because a single failure has an
        /// innocent explanation (a capture from the key still finishing, a teleport that landed in the
        /// one frame of an animation that objects); two in a row means this raid is refusing the whole
        /// exercise - no map name, no extent, a dead player, a player the game will not move - and the
        /// remaining stops would each fail in the same way, teleporting the player across the map for
        /// nothing.</summary>
        private const int MaxStartFailures = 2;

        /// <summary>Seconds a single stop will wait for its capture to finish before the campaign
        /// stops, having said so, and puts the player back.
        ///
        /// Deliberately far above anything a capture should take, because it is not a performance
        /// budget: it is the one thing standing between a capture that has stopped finishing and a
        /// player left at the far end of the map for the rest of the raid. The wait watches a flag
        /// another component owns, and the one state it cannot tell from work in progress is work that
        /// stopped with the flag still set - a coroutine Unity abandoned while the object lived, a step
        /// that hung on a file.
        ///
        /// Three minutes rather than the one first written, because the capture is allowed to be slow:
        /// a picture of a multi-floor map at the sharpest resolution setting is tens of tile renders and
        /// a per-floor develop and encode of tens of millions of pixels, each spread over frames on
        /// purpose. A ceiling that a legitimate capture could reach would abort campaigns instead of
        /// rescuing them, which is the worse failure of the two.</summary>
        private const float MaxCaptureWaitSeconds = 180f;

        /// <summary>Metres the player must have moved since the last automatic capture STARTED before
        /// another one is taken. A capture from where the last one was taken is a second photograph of
        /// the same loaded chunks: it costs a hitch and merges to almost nothing.
        ///
        /// 15 m rather than something of the order of a cell, because auto capture is a map-BUILDING
        /// tool and pictures are what it is for: a player crossing a building gains real pixels every
        /// 15 m, and the cost of an over-eager capture is a hitch, not a wrong picture.</summary>
        internal const float AutoCaptureMinMoveMetres = 15f;

        /// <summary>Seconds between automatic-capture EVALUATIONS - the cheap checks (alive, busy,
        /// moved far enough), not the captures, whose own spacing is the player's
        /// <see cref="ModSettings.AutoCaptureSeconds"/>.
        ///
        /// Why the two are separate: the interval is re-based on each capture that actually starts, so
        /// a tick that skips must not push the next chance a whole interval away - a player who has
        /// moved 14 m at one tick and 40 m a second later should be captured a second later. Every
        /// check an evaluation makes is a field read or a distance: the expensive question, whether the
        /// map has an extent, is asked of the memo alone (<see cref="MapExtentProbe.HasExtentFor"/>) and
        /// never measures.</summary>
        private const float AutoCaptureEvalSeconds = 1f;

        /// <summary>Seconds between evaluations while the map has no measured extent yet - the window
        /// before the harvester's second pass (27 s in) has measured one. Nothing can be captured in
        /// it, so the poll simply slows down rather than asking the same question every second and
        /// writing the same skip.</summary>
        private const float AutoCaptureProbeSeconds = 5f;

        /// <summary>Adds the campaign key and the automatic-capture ticker to a raid that has just
        /// started, from <see cref="QuestTree.Patches.GameWorldStartedPatch"/> - beside
        /// <see cref="MapCapture.Install"/> and behind the same HarvestZones gate, because everything
        /// here is a driver of that capture. Its GameObject hangs off the GameWorld, so it dies with
        /// the raid. Never throws.</summary>
        /// <param name="gameWorld">The raid's world, as handed to the patch.</param>
        public static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;

                // A Fika headless client has no player to teleport and nobody to press a key.
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeMapCampaign");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);

                var runner = go.AddComponent<MapCampaign>();
                runner._gameWorld = gameWorld;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the map campaign key ({ex.Message}).");
            }
        }

        private GameWorld _gameWorld;

        /// <summary>Whether a campaign is running. Set before the coroutine starts, cleared in its
        /// finally, and the answer to both "may the key start one" and "may an automatic capture
        /// happen now".</summary>
        private bool _running;

        /// <summary>Whether this component has already warned about a failure in Update. One warning
        /// per raid, shared by the key and the automatic ticker, because whatever is broken is broken
        /// every frame and a log line per frame helps nobody.</summary>
        private bool _warnedOnPoll;

        /// <summary>Time.time of the next automatic-capture evaluation - the cheap checks. See
        /// <see cref="AutoCaptureEvalSeconds"/>.</summary>
        private float _autoEvalAt;

        /// <summary>Time.time from which another automatic capture may be taken. Set only when one
        /// actually starts, which is what keeps a skipped evaluation from delaying the next.</summary>
        private float _autoDueAt;

        /// <summary>Whether the "automatic capture is on" line has been said for the current state of
        /// the setting. Cleared when the setting goes off, so turning it back on mid-raid says it
        /// again.</summary>
        private bool _autoAnnounced;

        /// <summary>Where the player was when the last automatic capture started, in world XZ, and
        /// whether there has been one at all. The first capture of a raid has nothing to have moved
        /// from and is taken as soon as there is an extent to draw it to.</summary>
        private bool _autoHasCaptured;

        private float _autoLastCaptureX;

        private float _autoLastCaptureZ;

        /// <summary>Why the last automatic evaluation captured nothing. Held so the Debug line is
        /// written when the reason CHANGES rather than once a second for a stationary player - a
        /// console this mod keeps quiet on purpose.</summary>
        private string _autoSkip;

        // --- the keys --------------------------------------------------------------------------

        private void Update()
        {
            PollCampaignKey();
            PollAutoCapture();
        }

        /// <summary>The campaign key. Refuses, saying why, while a campaign or a capture is already
        /// running, and outside a raid with a living player.</summary>
        private void PollCampaignKey()
        {
            if (!ModSettings.Ready || ModSettings.CampaignKey == null) return;

            try
            {
                // ModSettings.ShortcutDown, not the shortcut's own IsDown: BepInEx refuses a press
                // while ANY key outside the combination is held, and a raid always holds something.
                // A held MODIFIER the shortcut does not name still blocks, so this key stays distinct
                // from the single capture's one-modifier-fewer key - see ShortcutDown.
                if (!ModSettings.ShortcutDown(ModSettings.CampaignKey.Value)) return;

                if (_running)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: a capture campaign is already running - the key does nothing until it " +
                        "has finished.");
                    return;
                }

                // A capture holds the camera and the render target; a campaign that started now would
                // teleport the player out from under a picture that is still being taken.
                if (MapCapture.IsCapturing)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: a map capture is running - the capture campaign key does nothing until it " +
                        "has finished.");
                    return;
                }

                if (!PlayerIsAlive())
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the capture campaign works inside a raid with a living player only - " +
                        "nothing was captured.");
                    return;
                }

                // A raid with anybody else in it is refused: the teleport is local and unannounced -
                // see the class comment - so a campaign in a Fika co-op raid is something done TO the
                // other players. Unknown counts as not solo.
                if (!SoloRaid()) return;

                if (!Prepare(out var stops, out var start, out var map)) return;

                // Set here rather than inside the coroutine: Update can run again before the
                // coroutine's first statement.
                _running = true;
                StartCoroutine(Run(stops, start, map));
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the capture campaign key failed ({ex.Message}).");
            }
        }

        /// <summary>Whether this raid is one nobody else is in, which is the only kind a campaign may
        /// run in - and says why when it is not. See <see cref="ModEnvironment.RaidIsSolo"/>: a plain
        /// SPT install always answers yes, and a Fika client whose own answer cannot be read answers
        /// "unknown", which is refused rather than risked.</summary>
        private static bool SoloRaid()
        {
            var solo = ModEnvironment.RaidIsSolo;
            if (solo == true) return true;

            Plugin.LogSource?.LogInfo(solo == false
                ? "QuestTree: no capture campaign - this raid has other players in it, and the campaign " +
                  "teleports you across the map without telling them, which would leave their view of you " +
                  "wrong for as long as it ran. Run it in a solo raid; the single capture key still works " +
                  "here."
                : "QuestTree: no capture campaign - Fika is loaded and this build could not read whether this " +
                  "raid is a solo one, so it refuses rather than teleport you in front of other players. The " +
                  "single capture key still works here.");

            return false;
        }

        // --- the plan --------------------------------------------------------------------------

        /// <summary>Everything the campaign needs before the player is moved anywhere: the map's
        /// extent, a standable point per grid cell, and the position to put the player back at.
        /// False - having said why - means nothing is captured and nobody is teleported.</summary>
        /// <param name="stops">The sampled world positions to capture from, in visiting order.</param>
        /// <param name="start">Where the player was standing when the key was pressed.</param>
        /// <param name="map">The map's internal name, for the log lines.</param>
        private bool Prepare(out List<Vector3> stops, out Vector3 start, out string map)
        {
            stops = null;
            start = Vector3.zero;
            map = null;

            var player = _gameWorld?.MainPlayer;
            if (player == null) return false;

            // Player.Transform, not the obsolete MonoBehaviour transform: the game hides the latter
            // behind an Obsolete attribute this project treats as an error.
            start = player.Transform.position;
            map = MapKey();

            // No name, nothing to do: a capture of this raid would be refused by MapCapture for
            // exactly the same reason (it has nowhere to write to), and asking MapExtentProbe for the
            // extent of a nameless map would memoise a rectangle under a name no capture can use.
            if (string.IsNullOrEmpty(map))
            {
                Plugin.LogSource?.LogWarning(
                    "QuestTree: no capture campaign - this raid does not say which map it is, so there is " +
                    "nothing to capture into.");
                return false;
            }

            // The same rectangle the harvest measured and a capture is drawn to - not a measurement
            // of our own, so the grid covers exactly the map the pictures will cover.
            var extent = MapExtentProbe.TryProbeForCapture(map);
            if (extent == null)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no capture campaign on {map} - its extent could not be measured, so there is " +
                    "no rectangle to spread the stops over. The line above says why.");
                return false;
            }

            // The extent is the PADDED rectangle - at least 20 m of deliberate empty border on each
            // side, which on many maps is past the level border that kills a player who crosses it. The
            // picture covers the pad; the player is not sent into it. See MapExtentProbe.Inset: the
            // grid is planned over the measured rectangle, so every cell centre a point is sampled
            // around is ground the NavMesh actually reached.
            MapExtentProbe.Inset(
                extent.MinX, extent.MinZ, extent.MaxX, extent.MaxZ,
                out var minX, out var minZ, out var maxX, out var maxZ);

            var cells = MapCampaignGrid.Plan(
                minX, minZ, maxX, maxZ, CampaignCellMetres, start.x, start.z, out var stepX, out var stepZ);

            if (cells.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no capture campaign on {map} - its extent " +
                    $"({F(extent.MinX)},{F(extent.MinZ)}..{F(extent.MaxX)},{F(extent.MaxZ)}) covers no whole " +
                    "cell, which means it is not a rectangle a campaign can be walked over.");
                return false;
            }

            // Cells are the extent divided by a whole number of them, so they can be a good deal
            // smaller than the nominal 200 m; the search radius follows the cell it searches.
            var radius = SampleRadius(cells, stepX, stepZ);

            stops = Standable(cells, extent, start, radius, out var dropped);

            if (stops.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no capture campaign on {map} - none of its {cells.Count} cells has anywhere " +
                    $"to stand within {Whole(radius)} m of its centre (is there a NavMesh on this " +
                    "map?).");
                return false;
            }

            // The REAL spacing, not the nominal 200 m: the planner divides the measured rectangle into a
            // whole number of cells, so the stops on Customs are 173 m apart one way and 166 m the
            // other, and this line is what a reader judges the plan by.
            Plugin.LogSource?.LogInfo(
                $"QuestTree: capture campaign on {map} - {stops.Count} stop(s) on a {F(stepX)}x{F(stepZ)} m " +
                $"grid, starting from {At(start)}.");

            // The line that OPENS this run in the map's journal, which is also what the journal counts to
            // keep the last twenty runs and throw the older ones away - see MapCapture.Journal.
            MapCapture.Journal(
                map,
                $"campaign on {map} with {ModInfo.Stamp} - {stops.Count} stop(s) on a {F(stepX)}x{F(stepZ)} m grid, " +
                $"starting from {At(start)}.",
                startsRun: true);

            if (dropped > 0)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {dropped} of {cells.Count} campaign cells on {map} had nothing standable " +
                    $"within {Whole(radius)} m and were dropped from the plan.");
            }

            return true;
        }

        /// <summary>How far from a cell's centre a standable point may be found on THIS grid: the
        /// smaller of <see cref="CampaignSampleRadius"/> and <see cref="CampaignSampleShare"/> of the
        /// shorter side of a cell - counting only the axes that have more than one cell.
        ///
        /// The cell, not the nominal 200 m, because the planner divides the rectangle into a whole
        /// number of cells and takes what that gives: a 210 m wide map is two 105 m columns, and a flat
        /// 60 m search from each centre reaches into the neighbour's half - two stops on the same patch
        /// of ground, one of the two captures paying for a photograph the other already took.
        ///
        /// Only the axes with a neighbour to collide with, because that is the whole reason for the
        /// limit. A small map is one cell across, its stop is the only stop, and narrowing its search
        /// could only drop the one cell there is and cancel the campaign. Same reason a degenerate step
        /// falls back to the ceiling rather than to zero.</summary>
        /// <param name="cells">The planned cells, whose highest Col and Row give the grid's shape.</param>
        /// <param name="stepX">One cell's size along x, in metres.</param>
        /// <param name="stepZ">One cell's size along z, in metres.</param>
        private static float SampleRadius(List<MapCampaignGrid.Stop> cells, double stepX, double stepZ)
        {
            var columns = 1;
            var rows = 1;

            foreach (var cell in cells)
            {
                if (cell.Col + 1 > columns) columns = cell.Col + 1;
                if (cell.Row + 1 > rows) rows = cell.Row + 1;
            }

            var limit = double.PositiveInfinity;
            if (columns > 1) limit = Math.Min(limit, stepX);
            if (rows > 1) limit = Math.Min(limit, stepZ);

            if (double.IsNaN(limit) || double.IsInfinity(limit) || limit <= 0d) return CampaignSampleRadius;

            return Mathf.Min(CampaignSampleRadius, (float)(limit * CampaignSampleShare));
        }

        /// <summary>The sampled world position of each cell that has one, in the order given. A cell
        /// whose centre has no NavMesh within <paramref name="radius"/> at any of the heights tried is
        /// dropped.
        ///
        /// The heights tried, in order, because SamplePosition searches a SPHERE around the point it
        /// is given and a cell centre has no height of its own: the ground band's middle (right for
        /// most of most maps), then the player's own height (right for an indoor map whose "ground"
        /// band is a car park), then the middle of every other band the extent found (a rooftop, a
        /// basement). The first hit wins, so the cheap case costs one call.</summary>
        /// <param name="cells">The planned cells, in visiting order.</param>
        /// <param name="extent">The map's extent, for its floor bands' heights.</param>
        /// <param name="start">Where the player is standing, for the second height tried.</param>
        /// <param name="radius">How far from a cell's centre to search - see <see cref="SampleRadius"/>.</param>
        /// <param name="dropped">How many cells had nothing standable.</param>
        private static List<Vector3> Standable(
            List<MapCampaignGrid.Stop> cells, MapExtentDto extent, Vector3 start, float radius, out int dropped)
        {
            var heights = Heights(extent, start.y);
            var stops = new List<Vector3>(cells.Count);
            dropped = 0;

            foreach (var cell in cells)
            {
                var found = false;

                foreach (var y in heights)
                {
                    if (!NavMesh.SamplePosition(
                            new Vector3(cell.X, y, cell.Z), out var hit, radius, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    stops.Add(hit.position);
                    found = true;
                    break;
                }

                if (!found) dropped++;
            }

            return stops;
        }

        /// <summary>The heights a cell centre is probed at, best first. Never empty: the player's own
        /// height is always in it.</summary>
        /// <param name="extent">The map's extent, whose floor bands supply the rest.</param>
        /// <param name="playerY">The player's height.</param>
        private static List<float> Heights(MapExtentDto extent, float playerY)
        {
            var heights = new List<float>(4);

            var floors = extent?.Floors;
            if (floors != null)
            {
                foreach (var floor in floors)
                {
                    if (floor == null || floor.Level != 0) continue;
                    heights.Add((floor.MinY + floor.MaxY) * 0.5f);
                }
            }

            heights.Add(playerY);

            if (floors != null)
            {
                foreach (var floor in floors)
                {
                    if (floor == null || floor.Level == 0) continue;
                    heights.Add((floor.MinY + floor.MaxY) * 0.5f);
                }
            }

            return heights;
        }

        // --- the run ---------------------------------------------------------------------------

        /// <summary>One campaign: teleport, settle, capture, wait, repeat - and the player put back
        /// where they started whatever happens.
        ///
        /// Written with a try/finally and no catch because C# forbids a yield inside a try that has a
        /// catch, and the finally is the point: it is what guarantees the player is not left standing
        /// in a field at the far end of the map when a step throws, when a capture refuses twice, or
        /// when the player dies half way through. It is NOT a guarantee against the raid ending - Unity
        /// abandoning a coroutine is not guaranteed to run a finally at all - but a raid that has ended
        /// has nowhere to put anybody back to.</summary>
        /// <param name="stops">The sampled world positions to capture from, in visiting order.</param>
        /// <param name="start">Where the player was standing when the key was pressed.</param>
        /// <param name="map">The map's internal name, for the log lines.</param>
        private IEnumerator Run(List<Vector3> stops, Vector3 start, string map)
        {
            var clock = Stopwatch.StartNew();
            var captured = 0;
            var skipped = 0;
            var failures = 0;
            string stopped = null;

            try
            {
                for (var i = 0; i < stops.Count; i++)
                {
                    stopped = WhyStop();
                    if (stopped != null) break;

                    var stop = stops[i];

                    // Two metres up, and nothing else touched: see CampaignTeleportRise for what the
                    // drop costs, and the class comment for what is deliberately NOT changed.
                    //
                    // A failure here is treated as the stop failing rather than as the campaign
                    // failing, and counts against the same budget as a capture that would not start:
                    // the game can refuse to move a player for reasons that pass (an animation, an
                    // interaction), and one such frame should cost one stop. Two in a row is a player
                    // the game will not move, and the campaign gives up - with the restore still
                    // running, which is the point of the budget.
                    if (!Teleport(new Vector3(stop.x, stop.y + CampaignTeleportRise, stop.z)))
                    {
                        skipped++;
                        failures++;

                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: campaign stop {i + 1} of {stops.Count} at {At(stop)} - you could not " +
                            "be moved there.");

                        // Into the journal as well as the log - see MapCapture.Journal. A game log does
                        // not survive the game being restarted, and these are the lines somebody asks
                        // about days later.
                        MapCapture.Journal(map, $"stop {i + 1} of {stops.Count} at {At(stop)} - could not be moved there.");

                        if (failures >= MaxStartFailures)
                        {
                            stopped = $"{failures} stops in a row could not be reached";
                            break;
                        }

                        continue;
                    }

                    // The streamer's turn: nothing here can hurry it, so this is simply time.
                    yield return new WaitForSeconds(CampaignSettleSeconds);

                    stopped = WhyStop();
                    if (stopped != null) break;

                    if (!MapCapture.TryStartCapture())
                    {
                        skipped++;
                        failures++;

                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: campaign stop {i + 1} of {stops.Count} at {At(stop)} - the capture " +
                            "did not start.");

                        MapCapture.Journal(map, $"stop {i + 1} of {stops.Count} at {At(stop)} - the capture did not start.");

                        if (failures >= MaxStartFailures)
                        {
                            stopped = $"{failures} captures in a row did not start";
                            break;
                        }

                        continue;
                    }

                    failures = 0;

                    // The capture drives itself a tile a frame; the campaign's only job is not to move
                    // the player out from under it, because the streamer loads the world around the
                    // PLAYER and the picture is of whatever it has loaded.
                    //
                    // Bounded, because this watches a flag another component clears - see
                    // MaxCaptureWaitSeconds. The timeout is recorded rather than acted on here: the
                    // line below re-asks WhyStop first, so that a player who died during the capture is
                    // reported as having died rather than as a slow capture.
                    var waitUntil = Time.time + MaxCaptureWaitSeconds;
                    var timedOut = false;

                    while (MapCapture.IsCapturing)
                    {
                        if (!PlayerIsAlive() || _gameWorld == null) break;

                        if (Time.time >= waitUntil)
                        {
                            timedOut = true;
                            break;
                        }

                        yield return null;
                    }

                    stopped = WhyStop();
                    if (stopped != null) break;

                    if (timedOut)
                    {
                        stopped =
                            $"the capture at stop {i + 1} had not finished after {Whole(MaxCaptureWaitSeconds)} s";
                        break;
                    }

                    captured++;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: campaign stop {i + 1} of {stops.Count} at {At(stop)} - captured.");
                }
            }
            finally
            {
                Restore(start);
                _running = false;

                // A campaign has just photographed the map from everywhere, including the cell it
                // started in, so automatic capture treats the start position as its own last capture:
                // it waits for the player to move on rather than immediately taking one more picture
                // of where they are standing again.
                _autoHasCaptured = true;
                _autoLastCaptureX = start.x;
                _autoLastCaptureZ = start.z;
                _autoDueAt = Time.time;

                var seconds = (clock.ElapsedMilliseconds / 1000d).ToString("0", CultureInfo.InvariantCulture);
                var counts = $"{stops.Count} stop(s), {captured} captured, {skipped} skipped, {seconds} s.";

                Plugin.LogSource?.LogInfo(stopped == null
                    ? $"QuestTree: capture campaign on {map} done - {counts}"
                    : $"QuestTree: capture campaign on {map} stopped ({stopped}) - {counts}");

                MapCapture.Journal(map, stopped == null
                    ? $"done - {counts}"
                    : $"stopped ({stopped}) - {counts}");
            }
        }

        /// <summary>Why the campaign must stop now, or null to carry on: the player is dead, or the
        /// raid is gone. Checked before every teleport and after every wait, because both of those are
        /// where the seconds go.</summary>
        private string WhyStop()
        {
            if (_gameWorld == null) return "the raid has ended";
            if (!PlayerIsAlive()) return "the player is down";
            return null;
        }

        /// <summary>Moves the player, through the game's own <c>Player.Teleport(Vector3)</c> - which
        /// sets the movement context's transform position, resets the height interpolation and the
        /// damped velocity, resets the fall height, and re-reads the environment for the new place.
        /// There is nothing further to zero: the context's Velocity is a read-only passthrough to the
        /// CharacterController's own, which the controller recomputes from the new position.
        ///
        /// False, having said so, when there is nobody to move.</summary>
        /// <param name="to">Where to put the player.</param>
        private bool Teleport(Vector3 to)
        {
            try
            {
                var player = _gameWorld?.MainPlayer;
                if (player == null) return false;

                player.Teleport(to);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the capture campaign could not move the player ({ex.Message}).");
                return false;
            }
        }

        /// <summary>Puts the player back where the campaign found them. Runs from the coroutine's
        /// finally, so it runs on the ordinary end, on an abort, and on an exception alike.
        ///
        /// A DEAD player is left where they fell, deliberately: a corpse is a ragdoll the game is
        /// simulating and its position is not the player's to set any more, the screen has already
        /// moved on to the death view, and there is nothing a restored position could be for. It says
        /// so in one line rather than silently doing nothing.</summary>
        /// <param name="start">Where the player was standing when the key was pressed.</param>
        private void Restore(Vector3 start)
        {
            try
            {
                // Unity's == null, which is also true for a DESTROYED object: at the end of a raid the
                // GameWorld is gone, and reaching through it for a player would throw and be reported
                // as a failure to teleport rather than as the raid simply being over.
                if (_gameWorld == null)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the capture campaign could not put you back - the raid is already over.");
                    return;
                }

                var player = _gameWorld.MainPlayer;
                if (player == null)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the capture campaign could not put you back - there is no player any more.");
                    return;
                }

                if (!PlayerIsAlive())
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the capture campaign is over and you did not survive it - you are left where " +
                        "you fell rather than teleported as a corpse.");
                    return;
                }

                // The exact position, with no rise added: the player was standing there a minute ago,
                // so it is known to hold a player. The rise on a STOP is for a sampled NavMesh point,
                // which is a surface, not a place anybody has stood.
                player.Teleport(start);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture campaign could not put you back at {At(start)} ({ex.Message}) - " +
                    "extract or use a teleport mod if you are somewhere you should not be.");
            }
        }

        // --- automatic capture -----------------------------------------------------------------

        /// <summary>The automatic-capture ticker: while the setting is on, a capture every
        /// <see cref="ModSettings.AutoCaptureSeconds"/> seconds once the player has moved
        /// <see cref="AutoCaptureMinMoveMetres"/> metres since the last one, so a raid spent playing
        /// builds the map by itself.
        ///
        /// The setting is READ every evaluation rather than cached, so turning it on or off in the F12
        /// menu mid-raid takes effect at the next tick.</summary>
        private void PollAutoCapture()
        {
            if (!ModSettings.Ready || ModSettings.AutoCapture == null || ModSettings.AutoCaptureSeconds == null)
            {
                return;
            }

            try
            {
                if (!ModSettings.AutoCapture.Value)
                {
                    // So that turning it back on says the line again, and so a stale skip reason from
                    // an earlier spell cannot suppress the first line of the next one.
                    _autoAnnounced = false;
                    _autoSkip = null;
                    return;
                }

                var now = Time.time;
                if (now < _autoEvalAt) return;
                _autoEvalAt = now + AutoCaptureEvalSeconds;

                var seconds = ModSettings.AutoCaptureSeconds.Value;

                if (!_autoAnnounced)
                {
                    _autoAnnounced = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: automatic map capture is on - every {seconds} s after moving " +
                        $"{Whole(AutoCaptureMinMoveMetres)} m.");
                }

                if (_running)
                {
                    Skip("the capture campaign is running");
                    return;
                }

                if (MapCapture.IsCapturing)
                {
                    Skip("a capture is already running");
                    return;
                }

                if (!PlayerIsAlive())
                {
                    Skip("the player is down");
                    return;
                }

                // Not a reason to report: this is simply the interval the player asked for.
                if (now < _autoDueAt) return;

                var map = MapKey();
                if (string.IsNullOrEmpty(map))
                {
                    Skip("this raid does not say which map it is");
                    return;
                }

                // Nothing can be drawn before there is a rectangle to draw it to. HasExtentFor, not
                // TryProbeForCapture: the question here is whether one has ALREADY been measured, and
                // asking for one would measure it - a triangulation of the whole NavMesh, every poll,
                // for the first half minute of every raid this setting is on (see
                // MapExtentProbe.HasExtentFor). Waiting for the harvester's second pass also means the
                // first automatic capture is drawn to the rectangle the harvest accepted rather than to
                // one of this poll's own, which that pass would then supersede - a picture measured to
                // a superseded rectangle is replaced, not merged into.
                if (!MapExtentProbe.HasExtentFor(map))
                {
                    _autoEvalAt = now + AutoCaptureProbeSeconds;
                    Skip("the map's extent has not been measured yet");
                    return;
                }

                var player = _gameWorld?.MainPlayer;
                if (player == null)
                {
                    Skip("there is no player");
                    return;
                }

                var at = player.Transform.position;

                if (_autoHasCaptured &&
                    !MapCampaignGrid.MovedFarEnough(_autoLastCaptureX, _autoLastCaptureZ, at.x, at.z))
                {
                    Skip($"you have not moved {Whole(AutoCaptureMinMoveMetres)} m since the last capture");
                    return;
                }

                // automatic: this tick comes round every few seconds, so the capture builds the 3D mesh
                // only for a map that has none yet - the pictures are taken exactly as ever. A campaign
                // stop and a key press are places somebody chose and always build it.
                if (!MapCapture.TryStartCapture(automatic: true))
                {
                    Skip("the capture did not start");
                    return;
                }

                // Only a capture that STARTED moves the clock and the reference position - see
                // AutoCaptureEvalSeconds.
                _autoDueAt = now + Mathf.Max(0f, seconds);
                _autoLastCaptureX = at.x;
                _autoLastCaptureZ = at.z;
                _autoHasCaptured = true;
                _autoSkip = null;

                Plugin.LogSource?.LogDebug($"QuestTree: automatic map capture at {At(at)}.");
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: automatic map capture failed ({ex.Message}).");
            }
        }

        /// <summary>Records why an evaluation captured nothing, at Debug and only when the reason has
        /// changed since the last one - a stationary player would otherwise write the same line every
        /// second.</summary>
        /// <param name="why">The reason, as it goes in the line.</param>
        private void Skip(string why)
        {
            if (string.Equals(_autoSkip, why, StringComparison.Ordinal)) return;

            _autoSkip = why;
            Plugin.LogSource?.LogDebug($"QuestTree: no automatic map capture - {why}.");
        }

        // --- helpers ---------------------------------------------------------------------------

        /// <summary>Whether there is a raid with a living player to move and photograph. The same
        /// question <see cref="MapCapture"/> asks before a capture, asked here as well because a
        /// campaign has to keep asking it for as long as it runs.</summary>
        private bool PlayerIsAlive()
        {
            try
            {
                var player = _gameWorld?.MainPlayer;
                if (player == null) return false;

                var health = player.HealthController;
                return health != null && health.IsAlive;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The map's internal name, spelled the way the harvest spells it, so the extent this
        /// asks for is the one a capture of this map is drawn to.</summary>
        private string MapKey()
        {
            try
            {
                var map = _gameWorld?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(map)) map = _gameWorld?.LocationId;
                return map;
            }
            catch
            {
                return null;
            }
        }

        private static string At(Vector3 p) =>
            $"{Whole(p.x)},{Whole(p.z)}";

        private static string Whole(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "n/a" : v.ToString("0", CultureInfo.InvariantCulture);

        private static string F(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "n/a" : v.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Where a campaign's stops go and in what order, and whether the player has moved far enough for
    /// another automatic capture - the whole of the ARITHMETIC of this file, with nothing in it that
    /// needs a raid: no NavMesh, no player, no Unity object. That is deliberate, and it is what lets
    /// both rules be TESTED against known numbers rather than argued about.
    ///
    /// The grid: the rectangle handed in is divided into whole cells, at most
    /// <see cref="MapCampaign.CampaignCellMetres"/> across - ceil, so the cells are a little SMALLER
    /// than the nominal size rather than hanging over the edge of the map (Customs' measured 1035x499 m
    /// becomes 6x3 cells of 173x166 m). Every stop is then the centre of its cell, which is inside that
    /// rectangle by construction. The cells can be a good deal smaller than 200 m, which is why the
    /// step sizes are handed back: see <see cref="MapCampaign.SampleRadius"/>.
    ///
    /// The rectangle is the MEASURED one, not the padded extent the pictures are drawn to - the caller
    /// insets it first (<see cref="MapCampaign.Prepare"/>), because the pad is ground the map does not
    /// have and a stop is a place the player is put.
    ///
    /// The order: a serpentine, starting at the cell the player is standing in. Rows are visited from
    /// the player's own row to the far edge and then back past it to the near one, and each row after
    /// the first is walked in the opposite direction to the one before, so consecutive stops are
    /// almost always neighbours. Neighbours matter even though the player is teleported rather than
    /// walked: the streamer loads the world around wherever the player is, and an arrival next to the
    /// last stop starts from a mostly-loaded world, where an arrival across the map starts from
    /// nothing.
    ///
    /// The player's own cell is first because it is the one place already loaded, and because a
    /// campaign that begins where the player stands reads as one.
    /// </summary>
    internal static class MapCampaignGrid
    {
        /// <summary>One planned stop: which cell it is, and where its centre is in world XZ.</summary>
        internal struct Stop
        {
            /// <summary>Cell index along world +x, 0 at the extent's west edge.</summary>
            public int Col;

            /// <summary>Cell index along world +z, 0 at the extent's south edge.</summary>
            public int Row;

            /// <summary>World x of the cell's centre.</summary>
            public float X;

            /// <summary>World z of the cell's centre.</summary>
            public float Z;
        }

        /// <summary>The stops for one extent, in visiting order. Empty - never null - for an extent
        /// that is not a rectangle, or a cell size that is not a positive length; those are the cases
        /// a caller must not teleport anybody for.</summary>
        /// <param name="minX">West edge of the extent.</param>
        /// <param name="minZ">South edge of the extent.</param>
        /// <param name="maxX">East edge of the extent.</param>
        /// <param name="maxZ">North edge of the extent.</param>
        /// <param name="cellMetres">Longest a cell may be on either axis.</param>
        /// <param name="playerX">World x the player is standing at, which decides the first cell.</param>
        /// <param name="playerZ">World z the player is standing at.</param>
        /// <param name="stepX">One cell's size along x, in metres - zero when no stops were planned.
        /// Handed back because it is not the nominal cell size and the caller's search radius has to
        /// follow the cell it searches.</param>
        /// <param name="stepZ">One cell's size along z, in metres - zero when no stops were planned.</param>
        internal static List<Stop> Plan(
            double minX, double minZ, double maxX, double maxZ, float cellMetres, float playerX, float playerZ,
            out double stepX, out double stepZ)
        {
            var stops = new List<Stop>();

            stepX = 0d;
            stepZ = 0d;

            var width = maxX - minX;
            var height = maxZ - minZ;

            if (!Finite(minX) || !Finite(minZ) || !Finite(maxX) || !Finite(maxZ)) return stops;
            if (width <= 0d || height <= 0d) return stops;
            if (float.IsNaN(cellMetres) || float.IsInfinity(cellMetres) || cellMetres <= 0f) return stops;

            var columns = (int)Math.Ceiling(width / cellMetres);
            var rows = (int)Math.Ceiling(height / cellMetres);
            if (columns < 1) columns = 1;
            if (rows < 1) rows = 1;

            stepX = width / columns;
            stepZ = height / rows;

            var playerColumn = Cell(playerX, minX, stepX, columns);
            var playerRow = Cell(playerZ, minZ, stepZ, rows);

            // Rows from the player's own outward: theirs, then everything north of it, then back to
            // the south edge. One jump, at the point the sweep wraps - and a jump costs a campaign
            // nothing but one slower stop, since it teleports either way.
            var order = new List<int>(rows);
            for (var row = playerRow; row < rows; row++) order.Add(row);
            for (var row = playerRow - 1; row >= 0; row--) order.Add(row);

            for (var i = 0; i < order.Count; i++)
            {
                var row = order[i];

                foreach (var column in Columns(i, playerColumn, columns))
                {
                    stops.Add(new Stop
                    {
                        Col = column,
                        Row = row,
                        X = (float)(minX + (column + 0.5d) * stepX),
                        Z = (float)(minZ + (row + 0.5d) * stepZ)
                    });
                }
            }

            return stops;
        }

        /// <summary>The columns of one row, in visiting order. The FIRST row visited starts at the
        /// player's own column and runs east, wrapping round to the west edge so the row is still
        /// covered exactly once; every later row runs straight, alternating east and west so the rows
        /// join into a serpentine.
        ///
        /// Two joins in a campaign are therefore not neighbours - the first row's wrap, and the join
        /// from the first row to the second, which starts at whichever edge the alternation says
        /// rather than at wherever the wrap left off. Both cost one slower stop, which is the
        /// streamer having more to load at one arrival; nothing else in a campaign depends on
        /// adjacency, and picking the nearer edge instead would make the direction of every row
        /// depend on where the player happened to be standing.</summary>
        /// <param name="index">Which row of the visit order this is, 0 for the first.</param>
        /// <param name="playerColumn">The column the player is standing in.</param>
        /// <param name="columns">How many columns the grid has.</param>
        private static IEnumerable<int> Columns(int index, int playerColumn, int columns)
        {
            if (index == 0)
            {
                for (var i = 0; i < columns; i++) yield return (playerColumn + i) % columns;
                yield break;
            }

            // An odd row runs west, an even row east - so every row after the second joins its
            // neighbour at the edge the one before ended on.
            if (index % 2 == 1)
            {
                for (var column = columns - 1; column >= 0; column--) yield return column;
                yield break;
            }

            for (var column = 0; column < columns; column++) yield return column;
        }

        /// <summary>Whether the player has moved far enough from the last automatic capture for another
        /// one to be worth its hitch: at least <see cref="MapCampaign.AutoCaptureMinMoveMetres"/>
        /// metres, measured in XZ.
        ///
        /// Height is deliberately not part of the distance. A capture is a top-down picture, so a
        /// player who has climbed three floors without moving horizontally has nothing new to show the
        /// camera that the last capture did not already see.
        ///
        /// Here, in the pure half, rather than inline in the ticker, for one reason: it is a rule that
        /// can be wrong in a way nothing in a raid would report - a gate stuck shut captures nothing
        /// and looks like a quiet mod - so it is a function that can be tested. Compared as SQUARED
        /// distances, which is the same comparison without a square root; a coordinate that is not a
        /// number reads as "not moved", so a bad position cannot trigger captures.</summary>
        /// <param name="fromX">World x of the last capture.</param>
        /// <param name="fromZ">World z of the last capture.</param>
        /// <param name="toX">World x of the player now.</param>
        /// <param name="toZ">World z of the player now.</param>
        internal static bool MovedFarEnough(float fromX, float fromZ, float toX, float toZ)
        {
            var dx = toX - fromX;
            var dz = toZ - fromZ;

            if (float.IsNaN(dx) || float.IsNaN(dz) || float.IsInfinity(dx) || float.IsInfinity(dz)) return false;

            return dx * dx + dz * dz >=
                   MapCampaign.AutoCaptureMinMoveMetres * MapCampaign.AutoCaptureMinMoveMetres;
        }

        /// <summary>Which cell a world coordinate falls in, clamped to the grid so a player standing
        /// on the extent's very edge - or outside it, which a padded rectangle makes possible - still
        /// names a cell that exists.</summary>
        /// <param name="value">The world coordinate.</param>
        /// <param name="min">The extent's edge on that axis.</param>
        /// <param name="step">One cell's size on that axis.</param>
        /// <param name="count">How many cells the axis has.</param>
        private static int Cell(float value, double min, double step, int count)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;

            var index = (int)Math.Floor((value - min) / step);
            if (index < 0) return 0;
            if (index >= count) return count - 1;
            return index;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
