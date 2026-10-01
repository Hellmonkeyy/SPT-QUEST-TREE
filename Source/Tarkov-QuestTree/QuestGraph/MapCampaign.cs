using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// The two ways to photograph a whole map without walking it, both of them drivers of the one
    /// capture in <see cref="MapCapture"/> rather than second renderers of their own:
    ///
    ///   - the CAMPAIGN key: one press teleports the player across a grid of standable spots covering
    ///     the map, captures at each, and then leaves the player standing in the nearest extract they
    ///     can use (see <see cref="Finish"/>) - or, with that setting off, a stopped campaign, or no
    ///     usable extract, puts them back where they pressed it. Pressing the key again, or the raid's
    ///     time left running short (see <see cref="WhyOutOfTime"/>), ends it after the stop in hand as
    ///     though it had finished;
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
    /// The merge is also what makes a grid of stops the right shape. A stop 120 m from its neighbour
    /// (115 x 100 m on Customs' grid) is a little over 200 px away at the 0.5 m/px a 4096-wide Customs
    /// capture works out to, and the region the
    /// streamer had loaded around each observed stop on Customs was far wider than that - so every
    /// pixel of the map is, at some stop, both LOADED and the nearest stop's own pixel, which is
    /// exactly the pixel the merge keeps. Cells further apart than the loaded radius would leave the
    /// nearest-wins rule choosing between two pictures that are both empty there.
    ///
    /// What this deliberately does NOT do:
    ///   - it does not disable bots. Nothing here makes the player safe; a campaign run with AI on is
    ///     a player standing still for a second and a half at some thirty places on the map. Start the
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
        /// 120 m, down from 200 on 2026-09-24: at 200 m the ground between stops was not captured
        /// well (user). The merge rule and the streamer still decide it: at 4096 px across a 1118 m
        /// Customs the picture is about 0.5 m/px, so 120 m is ~240 px between stops - well inside the
        /// radius the streamer had loaded around a capture point on Customs, which is what the
        /// nearest-capture-wins merge needs (see the class comment). Customs' measured 1035x499 m is
        /// 9x5 = 45 cells of 115x100 m at this size (33 stops once the cells with nowhere to stand are
        /// dropped, on the 2026-09-25 run).</summary>
        internal const float CampaignCellMetres = 120f; // 200 until 2026-09-24: areas between stops were not being scanned properly (user)

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
        /// the ground; the rise is clear of that and of the kerbs and debris a NavMesh is draped
        /// over.
        ///
        /// Why so little and not five. The game DOES see this as a fall - the claim that it does not was
        /// wrong: <c>Player.Teleport</c> sets the transform and then calls
        /// <c>MovementContext.ResetFlying</c>, which re-bases the fall height to the NEW position,
        /// i.e. the stop plus the rise. <c>CheckFlying</c> then measures the drop from there to
        /// the ground and hands it to <c>ActiveHealthController.HandleFall</c>, which does nothing
        /// below the globals' <c>Health.Falling.SafeHeight</c> - 3 m on this server. So what the
        /// teleport itself saves the player is the 500 m fall; the rise is a real fall with
        /// metres of headroom, which is why this number stays small. Raising it to four would break
        /// both of the player's legs at every stop.</summary>
        // Lowered from 2 m to 1 m on 2026-09-24: at 2 m the arrival point sat inside low ceilings
        // (sheds, walkways, the underside of stairs) at stop after stop, which is worse than the
        // clipping risk it guarded against - a NavMesh sample sits within a few tens of centimetres
        // of the visible floor, so one metre still clears it, and the drop is barely a step.
        private const float CampaignTeleportRise = 1f;

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

        /// <summary>Rollback for ending a finished campaign at an extract: false = a campaign that visited every stop puts
        /// the player back where the key was pressed, as before, whatever <see cref="ModSettings.CampaignEndAtExtract"/>
        /// says. The setting is the player's switch; this is the code's.</summary>
        internal const bool EndAtExtract = true;

        /// <summary>How far outside an extract's trigger box the landing spot may be, in metres, when none of the NavMesh
        /// points sampled inside the box is IN it - a trigger narrower than the NavMesh's own margin from the walls, or one
        /// hung over a doorway. Two metres is a step or two, so the player still ends the campaign at the extract rather
        /// than somewhere near it; further than that and the point is refused, with the reason in the closing line.</summary>
        private const float ExtractNearMetres = 2f;

        /// <summary>Heights above a NavMesh point, in metres, at which the player's body is tested against an extract's
        /// trigger box: feet, middle, head. The trigger fires on the player's capsule, not on the point under it, so a box
        /// whose floor sits above the ground (or whose top is at waist height) still counts when any of these is inside.</summary>
        private static readonly float[] ExtractBodyHeights = { 0.1f, 0.9f, 1.6f };

        /// <summary>Rollback for the raid-time guard: false = a campaign visits every stop whatever the raid's clock says,
        /// as before, whatever <see cref="ModSettings.CampaignStopForRaidTime"/> says. The setting is the player's switch;
        /// this is the code's.</summary>
        internal const bool StopForRaidTime = true;

        /// <summary>Rollback for stopping a campaign with its own key: false = the key does nothing while a campaign runs,
        /// as before.</summary>
        internal const bool CancelByKey = true;

        /// <summary>Seconds of raid time kept back, on top of one more stop, for getting out: the teleport into the
        /// extract and the extract's own countdown once the player is in it - seven to ten seconds on most exits, more on
        /// some - and a few steps to walk when the campaign lands BESIDE a trigger. Generous on purpose: the two ways of
        /// being wrong are one stop fewer on the map, which the next raid fills, and MIA with the player's gear, which
        /// nothing fills.</summary>
        private const float ExtractReserveSeconds = 90f;

        /// <summary>The reserve instead of <see cref="ExtractReserveSeconds"/> when an early end will NOT send the player to
        /// an extract - "end at an extract" or <see cref="EndAtExtract"/> off - so they are put back at the start and have
        /// to walk out: five minutes, a walk across most of a map. A campaign whose extract search then finds nothing
        /// usable cannot be told apart in advance and keeps the 90 s.</summary>
        private const float WalkOutReserveSeconds = 300f;

        /// <summary>What the guard assumes a stop costs before any stop has been measured, in seconds. The first stop of
        /// a Customs campaign at 8 px/m took about 156 s (it builds the 3D mesh whole; every later stop only adds to it,
        /// 74-105 s), so the first guess is the dear one - the guard's job is to be wrong on the safe side.</summary>
        private const float FirstStopGuessSeconds = 156f;

        /// <summary>What the opening estimate assumes EVERY stop costs, in seconds: about the middle of the 74-105 s the
        /// later stops of a Customs campaign at 8 px/m took. Only for the line at the start - the guard itself measures.</summary>
        private const float PlanStopGuessSeconds = 90f;

        /// <summary>Set by the campaign key while a campaign runs (<see cref="CancelByKey"/>): the campaign finishes the
        /// stop in hand - never cut short mid-capture - and then ends as a finished one does. Read at the top of each
        /// stop; cleared when a campaign starts and when it ends.</summary>
        private bool _cancelRequested;

        /// <summary>Whether the running campaign is still in its loop of stops, where a cancel can still take effect.
        /// False once the loop is over - a campaign waiting for its last capture to let go has nothing left to stop, and
        /// the key says so rather than promising a stop it cannot make.</summary>
        private bool _acceptingCancel;

        /// <summary>Whether the running campaign has said why it cannot read the raid's time left. Once per campaign:
        /// a clock that cannot be read before one stop cannot be read before the next either.</summary>
        private bool _raidTimeUnreadableSaid;

        /// <summary>Seconds a single stop waits for its capture: the capture's own worst case with every cap in
        /// force (MapCapture.WorstCaseSeconds, review F45), so this only fires for a capture that has stopped
        /// finishing. Past it the campaign stops, having said so, and puts the player back once the capture
        /// has let go.
        ///
        /// Deliberately far above anything a capture should take, because it is not a performance
        /// budget: it is the one thing standing between a capture that has stopped finishing and a
        /// player left at the far end of the map for the rest of the raid. The wait watches a flag
        /// another component owns, and the one state it cannot tell from work in progress is work that
        /// stopped with the flag still set - a coroutine Unity abandoned while the object lived, a step
        /// that hung on a file.
        ///
        /// The capture is allowed to be slow: a picture of a multi-floor map at the sharpest resolution
        /// setting is tens of tile renders and a per-floor develop and encode of tens of millions of pixels,
        /// each spread over frames on purpose. A ceiling that a legitimate capture could reach would abort
        /// campaigns instead of rescuing them, which is the worse failure of the two.</summary>
        private static readonly float MaxCaptureWaitSeconds = (float)MapCapture.WorstCaseSeconds;

        /// <summary>Campaign speed step 2 (review): seconds a stop waits for a checkpoint's write still running before any has
        /// been measured; after one, twice its measured time (MapCapture.LastCheckpointSeconds).</summary>
        private const double WriteWaitSeconds = 120d;

        /// <summary>Campaign speed step 3, the rollback: true - a campaign on a map that already has a stored set leaves out
        /// the stops that can add nothing to it (<see cref="SurveyStored"/>) and says "resuming: N of M stops left". False:
        /// every stop of the plan is visited, as before. The player's switch to force every stop is '3D map: rebuild from
        /// scratch on the next capture' (see <see cref="WhyNoResume"/>).</summary>
        internal const bool CampaignResume = true;

        /// <summary>Campaign speed step 3: true - a stop is left out only when, besides the pictures, the stored set's meta
        /// records a capture that STOOD in the stop's cell and whose 3D mesh stage completed into the mesh that meta names
        /// (MapCapture's stands). The mesh merge adds the buildings the streamer loaded around the player, which no picture
        /// shows; a completed capture from inside the cell is what proves that cell's area was streamed into the mesh. A set
        /// written before stands were recorded has none, so its first campaign visits every stop. False: the pictures alone
        /// decide.</summary>
        internal const bool ResumeSkipsNeedBuildings = true;

        /// <summary>Campaign speed step 3: how far past its own cell a stop's surroundings reach, as a share of a cell's side
        /// on each axis. A stop stands within <see cref="CampaignSampleShare"/> of its cell's centre, so the pixels it is the
        /// NEAREST stop to - the ones only it can win - reach past the cell's edge by up to that share; half a cell covers
        /// them with room to spare, and is still inside the radius the streamer had loaded around a stop (the class
        /// comment). Pixels further out are some other stop's to win.</summary>
        private const float ResumeMarginShare = 0.5f;

        /// <summary>Campaign speed step 3: sidecar steps (four metres each) by which a stored pixel must be FARTHER than this
        /// stop would record for the stop to still be worth visiting. One, not zero: the stop is planned at its NavMesh point
        /// and captured from where the player then stands, and the two can round to neighbouring steps - a stop captured
        /// last campaign must not look one step closer than itself and be visited for ever.</summary>
        private const int ResumeMarginSteps = 1;

        /// <summary>Campaign speed step 3: pixels a stop may leave unimproved and still be left out - noise, not coverage.
        /// Measured on the stored Customs set right after a full campaign: two stops that WERE captured still had a
        /// handful (6 and 1) of side pixels in their surroundings recorded from ~300 m although they stand 20-80 m from
        /// what those pixels look at - their own captures did not improve them, so another visit would not either. 64 px is
        /// 1 m² of ground at 8 px/m; a ten-metre patch never seen is 6,400.</summary>
        private const int ResumeIgnoredPixels = 64;

        /// <summary>Campaign speed step 3: seconds the campaign waits for the stored set to be surveyed on its worker before
        /// it gives up on resuming and visits every stop. Customs' one floor and four sides take a few seconds.</summary>
        private const float ResumeWaitSeconds = 120f;

        /// <summary>The last stop's capture was still running when the campaign stopped waiting.</summary>
        private bool _stillCapturing;

        /// <summary>The campaign's hold on its map's uploads (WP3), null when none is held: taken before the first
        /// stop, released exactly once at the campaign's end - its finally, <see cref="RestoreWhenDone"/> or
        /// <see cref="OnDestroy"/> - and that release is the campaign's one upload.</summary>
        private MapTransfer.UploadHold _campaignHold;

        /// <summary>The upload's part of the summary line and the Journal, set when the hold is released.</summary>
        private string _campaignUploadNote = "";

        /// <summary>The running campaign's map, stops and captures so far, for <see cref="OnDestroy"/>'s line - the one
        /// end of a campaign that has no local variables to read them from.</summary>
        private string _lastMap;

        private int _lastStops;

        private int _lastCaptured;

        /// <summary>Restores the player once the capture has cleared its flag - with no time backstop (review F45):
        /// the capture is bounded by its own caps and watchdog, and a restore while it runs would photograph the
        /// wrong place. The campaign stays "running" until then, so neither the key nor the automatic tick can start
        /// something that the restore would then teleport out from under.
        ///
        /// WP3: the campaign's hold is released HERE, after the wait - so the late capture writes its meta under the
        /// hold, its map is owed, and the release uploads exactly once, including that capture (and a last-stop
        /// verification build, which runs inside the capture).</summary>
        /// <param name="start">Where the campaign started.</param>
        /// <param name="map">The campaign's map.</param>
        /// <param name="captured">Stops captured before the campaign stopped waiting.</param>
        /// <param name="stops">Stops the campaign planned.</param>
        /// <param name="lastStop">The last stop the player was moved to, for <see cref="Finish"/>'s distance.</param>
        /// <param name="completed">Whether the campaign finished - every stop, or ended early for raid time or by the
        /// key - see <see cref="Finish"/>.</param>
        private IEnumerator RestoreWhenDone(Vector3 start, string map, int captured, int stops, Vector3 lastStop, bool completed)
        {
            try
            {
                while (MapCapture.IsCapturing) yield return null;

                // Campaign speed step 2: the held stops, the late capture's included, start writing before the player is moved;
                // the upload hold's release below waits for the write, as Run's own end does.
                if (_gameWorld != null) MapCapture.StartCampaignWrite("the campaign stopped");
            }
            finally
            {
                Finish(start, lastStop, completed);
                ReleaseCampaignHold(map, captured, stops, "campaign stopped");
                _running = false;
            }
        }

        /// <summary>
        /// Ends the campaign's hold on its map's uploads and says what came of it (WP3): one upload of the map when a
        /// capture of it was written during the campaign and sharing is on, else no upload and why. Idempotent - the
        /// field is taken first - so the finally and <see cref="OnDestroy"/> can both call it.
        /// </summary>
        /// <param name="map">The campaign's map.</param>
        /// <param name="captured">How many stops were captured.</param>
        /// <param name="stops">How many stops the campaign planned.</param>
        /// <param name="what">"campaign done", "campaign ended early", "campaign stopped" or "campaign ended with the
        /// raid".</param>
        private void ReleaseCampaignHold(string map, int captured, int stops, string what)
        {
            // Campaign speed step 1 (4): every end of a campaign passes here (done, early, stopped, the late capture's
            // RestoreWhenDone, the raid's OnDestroy) - the held relief goes with it. First, before any early return.
            MapCapture.CampaignEnds();

            var hold = _campaignHold;
            _campaignHold = null;

            // The rollback switch off, or already released.
            if (hold == null)
            {
                _campaignUploadNote = "";
                return;
            }

            var still = MapCapture.IsCapturing ? " while a capture is still running" : "";

            // Automatic capture's hold goes first (WP3 5.1): with both on the map, the campaign's own release is then
            // the one that uploads, so "one upload at the end of the campaign" is literal.
            if (_autoHold != null) ReleaseAutoHold("capture campaign ended");

            // Campaign speed step 2: stops still being written (CampaignEnds above started the write when the raid ended
            // between two stops, or a checkpoint outlived its wait) keep the hold until they are down, so the one upload
            // reads them - the release then runs on the main thread when the write finishes, from the plugin object once the
            // raid is gone.
            if (MapCapture.WhenCampaignWritten(() => _campaignUploadNote = ReleaseUploadsNow(hold, map, captured, what, " after its last stops were written")))
            {
                _campaignUploadNote = ", 1 upload once the last stops are written";
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {what}: uploads of {map} stay held until the stops since the last checkpoint are written.");
                return;
            }

            _campaignUploadNote = ReleaseUploadsNow(hold, map, captured, what, still);
        }

        /// <summary>The release half of <see cref="ReleaseCampaignHold"/>: the one upload (WP3) and its line, and the summary
        /// note it comes to. Static, so a release deferred past the raid (campaign speed step 2) touches nothing of this
        /// component. Never throws.</summary>
        /// <param name="hold">The campaign's hold, taken.</param>
        /// <param name="map">The campaign's map.</param>
        /// <param name="captured">How many stops were captured.</param>
        /// <param name="what">For the line: how the campaign ended.</param>
        /// <param name="still">Added to the line: " while a capture is still running", or "".</param>
        private static string ReleaseUploadsNow(MapTransfer.UploadHold hold, string map, int captured, string what, string still)
        {
            MapTransfer.UploadStart outcome;

            // Never throws: the callers are a finally and OnDestroy, and the lines after them (clearing _running) must run.
            try
            {
                outcome = MapTransfer.ReleaseUploads(hold);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the campaign's upload hold on {map} could not be released ({ex.Message}).");
                outcome = MapTransfer.UploadStart.NoHost;
            }

            var intermediate = hold.Landed;

            switch (outcome)
            {
                case MapTransfer.UploadStart.Started:
                case MapTransfer.UploadStart.Queued:
                case MapTransfer.UploadStart.StillHeld:
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {what}: 1 upload of {map} ({captured} stops, {intermediate} intermediate uploads){still}.");
                    return $", 1 upload at the end ({hold.Deferred} held back)";

                default:
                    var why = outcome == MapTransfer.UploadStart.SharingOff ? "sharing is off"
                        : outcome == MapTransfer.UploadStart.Declined ? "this host declines map pictures"
                        : outcome == MapTransfer.UploadStart.NoHost ? "no plugin object"
                        : "no capture was written";
                    Plugin.LogSource?.LogInfo($"QuestTree: {what}: no upload of {map} - {why} ({captured} stops).");
                    return $", no upload ({why})";
            }
        }

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

        /// <summary>WP3: while automatic capture runs, its map's upload waits at most this long (s) - so a player who
        /// keeps walking shares the map at most once per ten minutes, and a crash loses at most ten minutes of it...</summary>
        internal const float AutoUploadMaxHoldSeconds = 600f;

        /// <summary>...or until no automatic capture has started for this long (s): longer than an ordinary loot or
        /// fight pause, short enough that a player who has finished shares the result within two minutes.</summary>
        internal const float AutoUploadIdleSeconds = 120f;

        /// <summary>WP3 rollback for the debounce alone: false = every automatic capture uploads as it lands, as before.</summary>
        internal const bool AutoUploadDebounce = true;

        /// <summary>Automatic capture's hold on its map's uploads (WP3), null when none is held. Taken before an
        /// automatic capture starts, released by <see cref="ReleaseAutoHold"/> on the rules above, when the setting
        /// goes off, when a campaign ends and when the raid does.</summary>
        private MapTransfer.UploadHold _autoHold;

        /// <summary>Time.realtimeSinceStartup of the last automatic capture that STARTED - the idle rule's clock.</summary>
        private float _autoLastStartAt;

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

        /// <summary>
        /// WP3: Unity abandons <see cref="Run"/> without its finally when the GameWorld goes (see Run's comment), so the
        /// campaign's hold is released here or it would lapse unflushed. <see cref="MapTransfer.UploadCapture"/> starts
        /// its coroutine on the PLUGIN object, which outlives the raid, so the one upload still runs, in the menu. A
        /// capture cut off by the raid never reaches its meta, so what is owed is the last capture that did.
        /// </summary>
        private void OnDestroy()
        {
            // Campaign speed step 1 (4): the raid is gone - whether or not a hold was taken (its rollback switch off), no
            // campaign relief may survive into the next raid
            MapCapture.CampaignEnds();

            try
            {
                if (_campaignHold != null)
                    ReleaseCampaignHold(_lastMap ?? _campaignHold.Key, _lastCaptured, _lastStops, "campaign ended with the raid");

                if (_autoHold != null) ReleaseAutoHold("raid ended");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the campaign's upload hold could not be released ({ex.Message}).");
            }
        }

        /// <summary>The campaign key. While a campaign runs it asks that campaign to stop (see
        /// <see cref="RequestCancel"/>); otherwise it refuses, saying why, while a capture is running and
        /// outside a raid with a living player.</summary>
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
                    RequestCancel();
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

                if (!Prepare(out var stops, out var start, out var map, out var grid)) return;

                // Set here rather than inside the coroutine: Update can run again before the
                // coroutine's first statement.
                _running = true;
                StartCoroutine(Run(stops, start, map, grid));
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the capture campaign key failed ({ex.Message}).");
            }
        }

        /// <summary>The campaign key pressed while a campaign runs: asks it to stop once the stop in hand is captured, and
        /// says so. The capture is never cut short - a stop abandoned mid-capture is a picture half-written and a player
        /// moved out from under it - and the campaign then ends as a finished one does (see <see cref="Run"/>). With
        /// <see cref="CancelByKey"/> off, the old refusal.
        ///
        /// Said in the log only: the campaign has no on-screen notice of its own anywhere, and the key is pressed by
        /// somebody who has the console or the log open to watch the stops go by.</summary>
        private void RequestCancel()
        {
            // One test for both refusals, and the constant beside a field rather than alone: a bare `if (!CancelByKey)`
            // is a compile-time constant whose body the build reports as unreachable code.
            if (!(CancelByKey && _acceptingCancel))
            {
                Plugin.LogSource?.LogInfo(!_acceptingCancel
                    ? "QuestTree: the capture campaign is already ending - it is waiting for its last capture to finish."
                    : "QuestTree: a capture campaign is already running - the key does nothing until it has finished.");
                return;
            }

            if (_cancelRequested)
            {
                Plugin.LogSource?.LogInfo(
                    "QuestTree: the capture campaign's stop is already requested - it ends once the stop in hand is captured.");
                return;
            }

            _cancelRequested = true;
            Plugin.LogSource?.LogInfo(
                "QuestTree: capture campaign stop requested - it finishes the stop in hand, then " +
                (ExtractEnabled() ? "goes to an extract." : "puts you back where you started."));
        }

        /// <summary>Whether a campaign that ends as finished looks for an extract at all: the code's switch and the
        /// player's - the same test <see cref="Finish"/> makes. Asked by the stop lines, which would otherwise promise
        /// an extract the setting has turned off.</summary>
        private static bool ExtractEnabled() => EndAtExtract && (ModSettings.CampaignEndAtExtract?.Value ?? true);

        /// <summary>Whether the raid-time guard is on: the code's switch and the player's.</summary>
        private static bool TimeGuardEnabled() =>
            StopForRaidTime && (ModSettings.CampaignStopForRaidTime?.Value ?? true);

        /// <summary>
        /// The raid's time left, in seconds, as the game's own raid timer counts it: the running game's
        /// <c>AbstractGame.GameTimer</c> (<c>EFT.AbstractGame.GameTimer</c>, an <c>EFT.GameTimer</c> made in
        /// <c>AbstractGame.Create</c> from the raid's session time), whose <c>SessionTime</c> is the raid's length - kept
        /// current by <c>ChangeSessionTime</c> when the raid's time is changed - and whose <c>GetPastTime()</c> is the
        /// time since the timer started, capped at that length. The game's own escape time is the same two added up
        /// (<c>EscapeDateTime = StartDateTime + SessionTime</c>); they are read here through public members rather than
        /// through the private escape date. <c>Singleton&lt;AbstractGame&gt;</c> is how the game itself reaches the
        /// timer (ExfiltrationController stamps an exit's start time with <c>GameTimer.PastTimeSeconds()</c>).
        ///
        /// False, with why, when there is no running game, its timer has not started or has stopped, or the raid has no
        /// length at all. Never throws.
        /// </summary>
        /// <param name="seconds">The raid's time left, never negative.</param>
        /// <param name="why">When false: why it could not be read.</param>
        private static bool TryRaidSecondsLeft(out float seconds, out string why)
        {
            seconds = 0f;
            why = null;

            try
            {
                if (!Singleton<AbstractGame>.Instantiated)
                {
                    why = "there is no running game to ask";
                    return false;
                }

                var timer = Singleton<AbstractGame>.Instance.GameTimer;
                if (timer == null)
                {
                    why = "the game has no raid timer";
                    return false;
                }

                if (timer.Status != GameTimer.EGameTimerStatus.Started)
                {
                    why = $"the raid timer is {timer.Status}, not running";
                    return false;
                }

                var length = timer.SessionTime;
                if (!length.HasValue || length.Value <= TimeSpan.Zero)
                {
                    why = "the raid has no time limit";
                    return false;
                }

                var left = (length.Value - timer.GetPastTime()).TotalSeconds;
                if (double.IsNaN(left) || double.IsInfinity(left))
                {
                    why = "the raid timer gave no number";
                    return false;
                }

                seconds = (float)Math.Max(0d, left);
                return true;
            }
            catch (Exception ex)
            {
                why = $"reading the raid timer threw ({ex.Message})";
                return false;
            }
        }

        /// <summary>
        /// The raid-time guard, asked before each stop: a reason to end the campaign NOW so the player gets out in time,
        /// or null to carry on. One more stop is taken to cost the average of the stops measured so far - or
        /// <see cref="FirstStopGuessSeconds"/> before any has been - and getting out <see cref="ExtractReserveSeconds"/>
        /// more; with less than that left in the raid, the campaign ends here. The average includes the first stop's
        /// whole mesh build, so it runs high for the rest of a campaign: wrong on the side of one stop fewer, never on the
        /// side of MIA. An unreadable clock carries on - the behaviour before this guard - and says why once.
        /// </summary>
        /// <param name="done">Stops the campaign has been through, for the line.</param>
        /// <param name="total">Stops the campaign planned.</param>
        /// <param name="measuredSeconds">The summed duration of the stops captured so far, in seconds.</param>
        /// <param name="measured">How many stops that sum covers.</param>
        /// <param name="extraSeconds">What the next stop costs on top of an ordinary one: the debug verification build's
        /// <see cref="MapCapture.VerifyExtraSeconds"/> when the next stop is the last and MeshVerifyLastStop is on.</param>
        private string WhyOutOfTime(int done, int total, double measuredSeconds, int measured, float extraSeconds)
        {
            if (!TimeGuardEnabled()) return null;

            if (!TryRaidSecondsLeft(out var left, out var why))
            {
                if (!_raidTimeUnreadableSaid)
                {
                    _raidTimeUnreadableSaid = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the capture campaign cannot read the raid's time left ({why}) - it visits every " +
                        "stop without watching the clock.");
                }

                return null;
            }

            var stop = measured > 0 ? (float)(measuredSeconds / measured) : FirstStopGuessSeconds;
            // A player who will not be sent to an extract - the setting or the rollback off - has to walk to one, so
            // they are left the walk-out reserve. "No extract qualifies" cannot be known until Finish looks, so that
            // case keeps the ordinary reserve.
            // Campaign speed step 2 (review): and the last checkpoint's write, which the end of a held campaign starts
            var need = stop + extraSeconds + (float)MapCapture.LastCheckpointSeconds +
                       (ExtractEnabled() ? ExtractReserveSeconds : WalkOutReserveSeconds);
            if (left >= need) return null;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: campaign stopped at stop {done} of {total} for raid time - {Whole(left)} s left, a stop " +
                $"needs ~{Whole(need)} s; " + (ExtractEnabled() ? "going to an extract." : "going back to the start."));

            return $"for raid time, {Whole(left)} s left";
        }

        /// <summary>The line at a campaign's start: the raid's time left against a first guess at the campaign's length
        /// (<see cref="PlanStopGuessSeconds"/> a stop), and a warning when that plus <see cref="ExtractReserveSeconds"/>
        /// will not fit - saying whether the guard will end it in time or nothing will. Never throws: the reading does
        /// not, and the rest is arithmetic.</summary>
        /// <param name="stops">Stops the campaign planned.</param>
        private void SayRaidTimeAtStart(int stops)
        {
            if (!TryRaidSecondsLeft(out var left, out var why))
            {
                _raidTimeUnreadableSaid = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the capture campaign cannot read the raid's time left ({why}) - " +
                    (TimeGuardEnabled()
                        ? "so it cannot stop in time to extract, and visits every stop without watching the clock."
                        : "not that it would watch it: 'stop in time to extract' is off."));
                return;
            }

            var estimate = stops * PlanStopGuessSeconds;
            Plugin.LogSource?.LogInfo(
                $"QuestTree: raid time left {Minutes(left)} min, estimated campaign ~{Minutes(estimate)} min ({stops} stops).");

            if (estimate + ExtractReserveSeconds <= left) return;

            Plugin.LogSource?.LogWarning(TimeGuardEnabled()
                ? $"QuestTree: the capture campaign will probably not fit in this raid's {Minutes(left)} min left - it " +
                  "stops early, when the time left is no longer enough for another stop and an extract, and the rest of " +
                  "the map waits for another raid."
                : $"QuestTree: the capture campaign will probably not fit in this raid's {Minutes(left)} min left and " +
                  "'stop in time to extract' is off - press the campaign key again to stop it, or the raid runs out " +
                  "and you are MIA.");
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
        /// <param name="grid">The grid the stops were planned on and each stop's cell, for the resume (step 3).</param>
        private bool Prepare(out List<Vector3> stops, out Vector3 start, out string map, out CampaignGrid grid)
        {
            stops = null;
            start = Vector3.zero;
            map = null;
            grid = null;

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

            // Stage M2c: this raid already found the map's stored set is a menu set raid captures leave alone - every stop
            // would be refused, so the campaign is, up front and with the real reason.
            if (MapCapture.IsGuardedMenuSet(map))
            {
                Plugin.LogSource?.LogWarning($"QuestTree: no capture campaign on {map} - {MapCapture.GuardedMenuSetReason}.");
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
            // smaller than the nominal 120 m; the search radius follows the cell it searches.
            var radius = SampleRadius(cells, stepX, stepZ);

            stops = Standable(cells, extent, start, radius, out var dropped, out var kept);

            var columns = 1;
            var rows = 1;
            foreach (var cell in cells)
            {
                columns = Math.Max(columns, cell.Col + 1);
                rows = Math.Max(rows, cell.Row + 1);
            }

            grid = new CampaignGrid
            {
                Extent = extent,
                MinX = minX,
                MinZ = minZ,
                StepX = stepX,
                StepZ = stepZ,
                Columns = columns,
                Rows = rows,
                Cells = kept,
            };

            if (stops.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no capture campaign on {map} - none of its {cells.Count} cells has anywhere " +
                    $"to stand within {Whole(radius)} m of its centre (is there a NavMesh on this " +
                    "map?).");
                return false;
            }

            // The REAL spacing, not the nominal 120 m: the planner divides the measured rectangle into a
            // whole number of cells, so the stops on Customs are 115 m apart one way and 100 m the
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
        /// The cell, not the nominal 120 m, because the planner divides the rectangle into a whole
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
        /// <param name="kept">The cell of each returned stop, in the same order - the resume (step 3) asks about the cell
        /// a stop stands for, not the point it found.</param>
        private static List<Vector3> Standable(
            List<MapCampaignGrid.Stop> cells, MapExtentDto extent, Vector3 start, float radius, out int dropped,
            out List<MapCampaignGrid.Stop> kept)
        {
            var heights = Heights(extent, start.y);
            var stops = new List<Vector3>(cells.Count);
            kept = new List<MapCampaignGrid.Stop>(cells.Count);
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
                    kept.Add(cell);
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

        // --- the resume (campaign speed step 3) ----------------------------------------------------

        /// <summary>The grid a campaign's stops were planned on: the inset rectangle's corner, a cell's size, the grid's
        /// shape, and the cell each stop stands for (parallel to the stops). What the resume measures a stop's
        /// surroundings by.</summary>
        private sealed class CampaignGrid
        {
            internal MapExtentDto Extent;
            internal double MinX;
            internal double MinZ;
            internal double StepX;
            internal double StepZ;
            internal int Columns;
            internal int Rows;
            internal List<MapCampaignGrid.Stop> Cells;
        }

        /// <summary>What the survey of the stored set found, per stop of the plan: whether it is left out, and the counts
        /// that decided it.</summary>
        private sealed class ResumeSurvey
        {
            /// <summary>Why the survey could not decide, or null.</summary>
            internal string Why;

            internal bool[] Skip;

            /// <summary>Floor pixels in reach this stop would draw at least <see cref="ResumeMarginSteps"/> + 1 steps closer
            /// than stored, and floor pixels in reach never seen.</summary>
            internal long[] FloorFarther;

            internal long[] FloorUnseen;

            /// <summary>The same for the side views, by the ground point each pixel looks at.</summary>
            internal long[] SideFarther;

            internal long[] SideUnseen;

            /// <summary>Recorded stands of completed mesh stages in the stop's cell.</summary>
            internal int[] Stands;

            /// <summary>Side pixels the heal read as never seen.</summary>
            internal long Healed;

            /// <summary>Why the stored mesh lets no stop be left out, or null (<see cref="ResumeSkipsNeedBuildings"/>).</summary>
            internal string BuildingsWhy;

            /// <summary>The set's first-captured stamp as read, for the check after the first capture.</summary>
            internal string FirstCapturedAt;

            internal int Pictures;
            internal double MainMs;
            internal double WorkerMs;
        }

        /// <summary>Why this campaign visits every stop whatever is stored, or null when it may resume: the rollback, the
        /// player's force-all switch ('3D map: rebuild from scratch on the next capture'), accumulation off (each capture
        /// then builds the mesh alone, so a stop left out is buildings lost), or the last campaign's write still running
        /// (its files may be mid-commit). "" for nothing worth saying.</summary>
        /// <param name="map">The map, for the lines.</param>
        /// <param name="stops">The plan's stops.</param>
        private static string WhyNoResume(string map, int stops)
        {
            if (!CampaignResume || stops < 2) return "";

            if (ModSettings.MeshRebuildNext?.Value ?? false)
                return "'3D map: rebuild from scratch on the next capture' is on, which runs every stop";

            if (!(ModSettings.MeshAccumulate?.Value ?? true))
                return "'3D map: add to the stored mesh' is off, so every stop's buildings are its own";

            if (MapCapture.CampaignWriting) return "the last campaign's stops are still being written";

            return null;
        }

        /// <summary>The resume's lines: "resuming: N of M stops left (K skipped, already seen from close enough)" with the
        /// time the survey took, at Info and into the journal; one Debug line per stop left out with the counts that decided
        /// it; or why every stop runs. Silent for a map with nothing stored. Stops are named by their number in the plan,
        /// as every other campaign line names them.</summary>
        /// <param name="map">The map.</param>
        /// <param name="stops">The plan's stops.</param>
        /// <param name="survey">The survey, or null when there is none.</param>
        /// <param name="why">Why there is none ("" for nothing to say).</param>
        /// <param name="left">Stops left to visit.</param>
        private static void SayResume(string map, List<Vector3> stops, ResumeSurvey survey, string why, int left)
        {
            if (survey == null)
            {
                if (!string.IsNullOrEmpty(why))
                    Plugin.LogSource?.LogInfo($"QuestTree: not resuming the campaign on {map} - {why}; every stop runs.");
                return;
            }

            var inv = CultureInfo.InvariantCulture;

            for (var s = 0; s < stops.Count; s++)
            {
                if (!survey.Skip[s]) continue;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: resume: stop {(s + 1).ToString(inv)} of {stops.Count.ToString(inv)} at {At(stops[s])} left out - " +
                    $"ground {survey.FloorFarther[s].ToString(inv)} px farther, {survey.FloorUnseen[s].ToString(inv)} never seen; " +
                    $"sides {survey.SideFarther[s].ToString(inv)} farther, {survey.SideUnseen[s].ToString(inv)} never seen " +
                    $"(up to {ResumeIgnoredPixels.ToString(inv)} ignored); {survey.Stands[s].ToString(inv)} completed capture(s) " +
                    "stood in its cell.");
            }

            var skipped = stops.Count - left;
            var line = $"resuming: {left.ToString(inv)} of {stops.Count.ToString(inv)} stops left ({skipped.ToString(inv)} " +
                       "skipped, already seen from close enough)";

            Plugin.LogSource?.LogInfo(
                $"QuestTree: {line} - {survey.Pictures.ToString(inv)} stored picture(s) read in " +
                $"{survey.WorkerMs.ToString("0", inv)} ms on a worker, {survey.MainMs.ToString("0", inv)} ms on the main thread " +
                $"({survey.Healed.ToString(inv)} side pixel(s) healed)" +
                (survey.BuildingsWhy != null ? $"; nothing is left out because {survey.BuildingsWhy}" : "") + ".");
            MapCapture.Journal(map, line + ".");
        }

        /// <summary>
        /// Campaign speed step 3, on a worker: which of the plan's stops can add nothing to the stored set. A stop is left
        /// out only when all of these hold:
        ///
        ///   - GROUND. In its surroundings - its cell grown by <see cref="ResumeMarginShare"/> of a cell on each side - no
        ///     pixel of any band that lies inside THAT BAND'S OWN walkable mask (MapCapture.StoredSet.BuildMasks) is stored
        ///     as never seen, or as farther than this stop would record there by more than <see cref="ResumeMarginSteps"/>.
        ///     "Would record" is the merge's own: the flat distance from the stop's standing point to the pixel's centre in
        ///     the sidecar's four-metre steps (MapCapture.ResumeStep), and the merge takes a pixel only when that is strictly
        ///     closer (CaptureMerge.Takes) - so a stop can only win pixels it stands closer to. Each band is judged on its own
        ///     pixels: a basement that failed at a stop while the top band merged stays never seen there, and keeps the stop.
        ///   - SIDES. Likewise every side pixel whose ground point (the SideSteps mapping) falls in the surroundings, after
        ///     the capture's own heal (MapCapture.HealStoredSide).
        ///   - BUILDINGS (<see cref="ResumeSkipsNeedBuildings"/>). The meta records a capture that stood in the stop's own
        ///     cell and whose mesh stage completed into the mesh it names.
        ///
        /// Up to <see cref="ResumeIgnoredPixels"/> offending pixels are forgiven (see there).
        ///
        /// A stop that failed to capture last time is not left out by this rule: nothing near its standing point was then
        /// photographed from there, so the pixels around it hold a neighbour's distance - dozens of steps farther than its
        /// own step 0 - and it offends by thousands of pixels; and it recorded no stand.
        ///
        /// One picture decoded at a time (a side's sidecar with its picture's empty-pixel flags), so the peak is two
        /// pictures' bytes. Never throws.
        /// </summary>
        /// <param name="set">The stored set, read on the main thread.</param>
        /// <param name="grid">The plan's grid.</param>
        /// <param name="stops">The plan's stops, where the player will stand (x and z are all that count).</param>
        private static ResumeSurvey SurveyStored(MapCapture.StoredSet set, CampaignGrid grid, List<Vector3> stops)
        {
            var clock = Stopwatch.StartNew();
            var n = stops.Count;
            var r = new ResumeSurvey
            {
                Skip = new bool[n],
                FloorFarther = new long[n],
                FloorUnseen = new long[n],
                SideFarther = new long[n],
                SideUnseen = new long[n],
                Stands = new int[n],
                FirstCapturedAt = set.FirstCapturedAt,
            };

            try
            {
                if (grid?.Cells == null || grid.Cells.Count != n || grid.Columns < 1 || grid.Rows < 1 ||
                    !(grid.StepX > 0d) || !(grid.StepZ > 0d))
                {
                    r.Why = "the plan has no grid to measure its stops by";
                    return r;
                }

                // The one stop standing for each cell, -1 for a cell with nowhere to stand.
                var stopAt = new int[grid.Columns * grid.Rows];
                for (var c = 0; c < stopAt.Length; c++) stopAt[c] = -1;

                var sx = new float[n];
                var sz = new float[n];

                for (var s = 0; s < n; s++)
                {
                    var cell = grid.Cells[s];
                    if (cell.Col < 0 || cell.Col >= grid.Columns || cell.Row < 0 || cell.Row >= grid.Rows)
                    {
                        r.Why = "a stop's cell is outside the grid";
                        return r;
                    }

                    stopAt[cell.Row * grid.Columns + cell.Col] = s;
                    sx[s] = stops[s].x;
                    sz[s] = stops[s].z;
                }

                foreach (var picture in set.Floors)
                {
                    var dist = MapCapture.ReadStoredDist(picture, out var why);
                    if (dist == null)
                    {
                        r.Why = $"floor \"{picture.Name}\": {why}";
                        return r;
                    }

                    r.Pictures++;
                    SurveyFloor(set, picture, grid, stopAt, sx, sz, dist, r);
                }

                foreach (var picture in set.Sides)
                {
                    var dist = MapCapture.ReadStoredDist(picture, out var why);
                    if (dist == null)
                    {
                        r.Why = $"{picture.Name}: {why}";
                        return r;
                    }

                    // The capture's own repair on load (HealSideDist), without the skyline - see HealStoredSide.
                    var empty = MapCapture.ReadStoredEmpty(picture, out why);
                    if (empty == null)
                    {
                        r.Why = $"{picture.Name}: {why}";
                        return r;
                    }

                    r.Healed += MapCapture.HealStoredSide(dist, empty);
                    empty = null;

                    r.Pictures += 2;
                    SurveySide(picture, grid, stopAt, sx, sz, dist, r);
                }

                r.BuildingsWhy = ResumeSkipsNeedBuildings ? CountStands(set, grid, stopAt, r) : null;

                for (var s = 0; s < n; s++)
                {
                    var offending = r.FloorFarther[s] + r.FloorUnseen[s] + r.SideFarther[s] + r.SideUnseen[s];
                    var buildings = !ResumeSkipsNeedBuildings || (r.BuildingsWhy == null && r.Stands[s] > 0);
                    r.Skip[s] = offending <= ResumeIgnoredPixels && buildings;
                }

                return r;
            }
            catch (Exception ex)
            {
                r.Why = $"surveying the stored set threw ({ex.GetType().Name}: {ex.Message})";
                return r;
            }
            finally
            {
                r.WorkerMs = clock.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>The cells whose surroundings (the cell grown by <see cref="ResumeMarginShare"/>) hold a coordinate, on
        /// one axis: cell c's surroundings are [min + (c - m) step, min + (c + 1 + m) step), so c runs over
        /// (u - 1 - m, u + m] with u the coordinate in cells. Empty (lo &gt; hi) off the grid.</summary>
        /// <param name="v">The world coordinate.</param>
        /// <param name="min">The grid's edge on this axis.</param>
        /// <param name="step">A cell's size on this axis.</param>
        /// <param name="count">Cells on this axis.</param>
        /// <param name="lo">The first cell.</param>
        /// <param name="hi">The last cell.</param>
        private static void Around(double v, double min, double step, int count, out int lo, out int hi)
        {
            var u = (v - min) / step;
            if (double.IsNaN(u) || double.IsInfinity(u))
            {
                lo = 0;
                hi = -1;
                return;
            }

            lo = Math.Max(0, (int)Math.Floor(u - 1d - ResumeMarginShare) + 1);
            hi = Math.Min(count - 1, (int)Math.Floor(u + ResumeMarginShare));
        }

        /// <summary>One band's pixels against every stop whose surroundings hold them - the ground rule of
        /// <see cref="SurveyStored"/>, in DevelopBand's float arithmetic, counted only inside the band's own mask.</summary>
        private static void SurveyFloor(MapCapture.StoredSet set, MapCapture.StoredPicture band, CampaignGrid grid, int[] stopAt,
            float[] sx, float[] sz, byte[] dist, ResumeSurvey r)
        {
            var w = set.Width;
            var h = set.Height;
            var colLo = new int[w];
            var colHi = new int[w];
            var px = new float[w];

            for (var col = 0; col < w; col++)
            {
                px[col] = set.PixelX(col);
                Around(px[col], grid.MinX, grid.StepX, grid.Columns, out colLo[col], out colHi[col]);
            }

            for (var row = 0; row < h; row++)
            {
                var pz = set.PixelZ(row);
                Around(pz, grid.MinZ, grid.StepZ, grid.Rows, out var rowLo, out var rowHi);
                if (rowLo > rowHi) continue;

                var index = row * w;

                for (var col = 0; col < w; col++, index++)
                {
                    int old = dist[index];

                    // Nothing can be more than the margin closer than a step at or under the margin.
                    if (old <= ResumeMarginSteps || colLo[col] > colHi[col]) continue;

                    var walkable = -1;

                    for (var cr = rowLo; cr <= rowHi; cr++)
                    for (var cc = colLo[col]; cc <= colHi[col]; cc++)
                    {
                        var s = stopAt[cr * grid.Columns + cc];
                        if (s < 0) continue;

                        if (old != MapCapture.ResumeUnseen)
                        {
                            var dx = px[col] - sx[s];
                            var dz = pz - sz[s];
                            if (old <= MapCapture.ResumeStep(Mathf.Sqrt(dx * dx + dz * dz)) + ResumeMarginSteps) continue;
                        }

                        // Only pixels where this band is walkable count - asked once a pixel, and only when it matters.
                        if (walkable < 0) walkable = band.Walkable(col, row) ? 1 : 0;
                        if (walkable == 0) break;

                        if (old == MapCapture.ResumeUnseen) r.FloorUnseen[s]++;
                        else r.FloorFarther[s]++;
                    }
                }
            }
        }

        /// <summary>One side view's pixels against every stop whose surroundings hold the ground point each looks at - the
        /// side rule of <see cref="SurveyStored"/>, in SideSteps' arithmetic. Sides have no walkable mask: every pixel
        /// counts.</summary>
        private static void SurveySide(MapCapture.StoredPicture side, CampaignGrid grid, int[] stopAt, float[] sx, float[] sz,
            byte[] dist, ResumeSurvey r)
        {
            var w = side.Width;
            var h = side.Height;

            for (var row = 0; row < h; row++)
            {
                var index = row * w;

                for (var col = 0; col < w; col++, index++)
                {
                    int old = dist[index];
                    if (old <= ResumeMarginSteps) continue;

                    MapSideView.GroundPointOf(side.Right, side.Up, side.OriginR, side.OriginU, side.Ppm, h, side.YMin,
                        col + 0.5d, h - row - 0.5d, out var x, out var z);

                    Around(x, grid.MinX, grid.StepX, grid.Columns, out var colLo, out var colHi);
                    Around(z, grid.MinZ, grid.StepZ, grid.Rows, out var rowLo, out var rowHi);

                    for (var cr = rowLo; cr <= rowHi; cr++)
                    for (var cc = colLo; cc <= colHi; cc++)
                    {
                        var s = stopAt[cr * grid.Columns + cc];
                        if (s < 0) continue;

                        if (old == MapCapture.ResumeUnseen)
                        {
                            r.SideUnseen[s]++;
                            continue;
                        }

                        var dx = x - sx[s];
                        var dz = z - sz[s];
                        if (old > MapCapture.ResumeStep((float)Math.Sqrt(dx * dx + dz * dz)) + ResumeMarginSteps) r.SideFarther[s]++;
                    }
                }
            }
        }

        /// <summary>The buildings rule of <see cref="SurveyStored"/>: the recorded stands of completed mesh stages counted
        /// into the cell each lies in - or why no stop may be left out: no stored mesh, or none recorded (a set written
        /// before stands were, or whose mesh has since been rebuilt with nothing recorded).</summary>
        private static string CountStands(MapCapture.StoredSet set, CampaignGrid grid, int[] stopAt, ResumeSurvey r)
        {
            if (!set.HasMesh) return "there is no stored 3D mesh";
            if (set.Stands.Count == 0) return "the stored 3D mesh records no capture's standing point yet";

            foreach (var stand in set.Stands)
            {
                var c = (int)Math.Floor((stand.x - grid.MinX) / grid.StepX);
                var k = (int)Math.Floor((stand.y - grid.MinZ) / grid.StepZ);
                if (c < 0 || c >= grid.Columns || k < 0 || k >= grid.Rows) continue;

                var s = stopAt[k * grid.Columns + c];
                if (s >= 0) r.Stands[s]++;
            }

            return null;
        }

        // --- the run ---------------------------------------------------------------------------

        /// <summary>One campaign: teleport, settle, capture, wait, repeat - and the player put back
        /// where they started whatever happens, or at an extract when it finished (see <see cref="Finish"/>).
        ///
        /// Between stops, and only there, two chosen ends are checked: the campaign key pressed again
        /// (<see cref="RequestCancel"/>) and the raid's time left no longer enough for one more stop and an extract
        /// (<see cref="WhyOutOfTime"/>). Both let the stop in hand finish and count as a finished campaign.
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
        /// <param name="grid">The plan's grid and each stop's cell, for the resume (step 3).</param>
        private IEnumerator Run(List<Vector3> stops, Vector3 start, string map, CampaignGrid grid)
        {
            var clock = Stopwatch.StartNew();
            var captured = 0;
            var skipped = 0;
            _stillCapturing = false;
            var failures = 0;
            string stopped = null;

            // the wait the stop in hand was given - MapCapture's worst case, plus the verification build's at the last
            // stop with MeshVerifyLastStop on - so a timeout line names the number it used
            var waitUsed = MaxCaptureWaitSeconds;

            // WP3: every capture of this map writes its meta under the hold and is owed rather than uploaded; the
            // campaign's end releases it, and that is the one upload. Before the try, so every end of the try sees it.
            _lastMap = map;
            _lastStops = stops.Count;
            _lastCaptured = 0;
            _campaignUploadNote = "";
            _campaignHold = MapTransfer.HoldUploads(this, map, "capture campaign", preempts: true);

            // Set only on the line after the loop, so it is true for a campaign that went through every stop - or that
            // ended early ON PURPOSE, for raid time or at the player's key (`endedEarly`), which are finished campaigns
            // too: the player is to get out, not be put back where they started with the clock still running - and false
            // for every other end: a break (death, raid over, two failures in a row, a capture past its worst case) sets
            // `stopped`, and an exception reaches the finally without passing that line - whereas `stopped` alone is
            // still null after an exception, which is why the finally cannot ask it.
            var completed = false;

            // Why a campaign ended before its last stop by choice - "for raid time, ...", "cancelled with the key" - or
            // null. Kept apart from `stopped` because these ends count as finished (see `completed`).
            string endedEarly = null;

            // The last place the player was moved to, for Finish's "m from the last stop": the start until the first
            // teleport, so an early end - even one before any stop - measures from where the player really was.
            var lastStop = start;

            // The raid-time guard's measurements: the summed seconds of the stops captured so far, from the teleport to the
            // capture letting go, and how many.
            var measuredSeconds = 0d;
            var measured = 0;

            // Campaign speed step 3: stops of the plan left out by the resume, for the closing line; and whether that was
            // every stop, which ends the campaign where it stands.
            var leftOut = 0;
            var nothingLeft = false;

            try
            {
                // Campaign speed step 1 (4): a new campaign session - its first stop casts the relief, later stops reuse it.
                // Inside the try (review), so the finally's ReleaseCampaignHold -> CampaignEnds follows it whatever happens.
                MapCapture.CampaignBegins(stops.Count);

                _cancelRequested = false;
                _raidTimeUnreadableSaid = false;
                _acceptingCancel = true;

                // Campaign speed step 3: the stops that can add nothing to the stored set are left out - the plan's order
                // kept, the rest simply not visited. The survey reads the stored sidecars on a worker; the frames go on.
                ResumeSurvey survey = null;
                var resumeWhy = WhyNoResume(map, stops.Count);

                if (resumeWhy == null)
                {
                    var set = MapCapture.ReadStoredSet(map, grid?.Extent);

                    if (set == null) resumeWhy = "";   // no stored set: a first campaign, nothing to say
                    else if (set.Why != null) resumeWhy = $"the stored set cannot be read for it ({set.Why})";
                    else
                    {
                        var job = System.Threading.Tasks.Task.Run(() => SurveyStored(set, grid, stops));
                        var until = Time.realtimeSinceStartup + ResumeWaitSeconds;

                        while (!job.IsCompleted && WhyStop() == null && Time.realtimeSinceStartup < until) yield return null;

                        if (!job.IsCompleted) resumeWhy = WhyStop() ?? $"reading the stored set took over {Whole(ResumeWaitSeconds)} s";
                        else if (job.IsFaulted) resumeWhy = $"reading the stored set failed ({job.Exception?.GetBaseException().Message})";
                        else
                        {
                            survey = job.Result;
                            survey.MainMs = set.MainMs;
                            if (survey.Why != null)
                            {
                                resumeWhy = survey.Why;
                                survey = null;
                            }
                        }

                        // The decoded pictures (up to two at a time, a side's being its sidecar and its picture) died with
                        // the worker; EFT's collector is off in a raid, so they are collected here, before the first stop
                        // asks for memory of its own.
                        set = null;
                        if (job.IsCompleted) MapCapture.CollectGarbage("after the campaign's resume survey", force: true);
                    }
                }

                var order = new List<int>(stops.Count);
                for (var s = 0; s < stops.Count; s++)
                    if (survey == null || !survey.Skip[s]) order.Add(s);

                leftOut = stops.Count - order.Count;
                SayResume(map, stops, survey, resumeWhy, order.Count);

                // Whether the first capture has yet been checked for having merged into the set the survey read (see
                // MapCapture.ResumeFirstCapturedAt) - only asked when a stop was left out.
                var resumeCheck = leftOut > 0;

                // Campaign speed step 3: nothing left is its own end - no stop, no raid-time line, no write, no teleport:
                // the player stays where they stand (see the finally).
                nothingLeft = order.Count == 0;
                if (!nothingLeft) SayRaidTimeAtStart(order.Count);

                // The plan's number of the last stop visited (0 before any) - every line names stops by the plan's numbers,
                // whether or not a resume left some out.
                var lastNumber = 0;

                for (var k = 0; k < order.Count && stopped == null; k++)
                {
                    var i = order[k];

                    stopped = WhyStop();
                    if (stopped != null) break;

                    // The two chosen ends, checked only here - between stops, never mid-capture - so the stop in hand is
                    // always finished first. lastNumber is the plan's number of the last stop visited.
                    if (_cancelRequested)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: campaign cancelled after stop {lastNumber} of {stops.Count}; " +
                            (ExtractEnabled() ? "going to an extract." : "going back to the start."));
                        endedEarly = "cancelled with the key";
                        break;
                    }

                    // The next stop's own extra: the debug verification build runs inside the last stop's capture - the last
                    // one VISITED (step 3).
                    var verifyNext = k == order.Count - 1 && (ModSettings.MeshVerifyLastStop?.Value ?? false);
                    endedEarly = WhyOutOfTime(
                        lastNumber, stops.Count, measuredSeconds, measured, verifyNext ? (float)MapCapture.VerifyExtraSeconds : 0f);
                    if (endedEarly != null) break;

                    // Campaign speed step 2: a checkpoint whose write outlived its stop's wait finishes before anything moves -
                    // the next capture would merge into the copies it is writing. Bounded (review): twice the last checkpoint's
                    // measured time, or 120 s before one is measured; a death, the raid's end or the key end the wait as they
                    // end the campaign.
                    if (MapCapture.CampaignWriting)
                    {
                        var writeLimit = MapCapture.LastCheckpointSeconds > 0d ? 2d * MapCapture.LastCheckpointSeconds : WriteWaitSeconds;
                        var writeUntil = Time.realtimeSinceStartup + (float)writeLimit;

                        while (MapCapture.CampaignWriting)
                        {
                            stopped = WhyStop();
                            if (stopped != null || _cancelRequested) break;

                            if (Time.realtimeSinceStartup >= writeUntil)
                            {
                                stopped = $"a checkpoint's write had not finished after {Whole((float)writeLimit)} s";
                                break;
                            }

                            yield return null;
                        }

                        if (stopped != null) break;

                        if (_cancelRequested)
                        {
                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: campaign cancelled after stop {lastNumber} of {stops.Count}; " +
                                (ExtractEnabled() ? "going to an extract." : "going back to the start."));
                            endedEarly = "cancelled with the key";
                            break;
                        }
                    }

                    var stopBegan = clock.Elapsed.TotalSeconds;

                    var stop = stops[i];

                    // One metre up, and nothing else touched: see CampaignTeleportRise for what the
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

                    lastStop = stop;
                    lastNumber = i + 1;

                    // The streamer's turn: nothing here can hurry it, so this is simply time.
                    yield return new WaitForSeconds(CampaignSettleSeconds);

                    stopped = WhyStop();
                    if (stopped != null) break;

                    // The 3D mesh at every stop, and cheap after the first: each stop ADDS the buildings the streamer has
                    // loaded there to the stored mesh (MapMeshBuilder.Request.Base, WP2) and re-reads only what is new or
                    // degraded - so the campaign's mesh is the union of every stop, not the last stop's alone (review
                    // F46). The side views take their y range from the stored one and only widen it (review F13), and the
                    // stop's wait is the capture's own worst case (MapCapture.WorstCaseSeconds, review F45). The last stop
                    // may also build the mesh from scratch for comparison (MeshVerifyLastStop), and waits for that too.
                    var verify = k == order.Count - 1 && (ModSettings.MeshVerifyLastStop?.Value ?? false);

                    // Campaign speed step 2: the stop the checkpoint line names
                    MapCapture.CampaignStopIs(i + 1);

                    if (!MapCapture.TryStartCapture(buildMesh: true, verifyMesh: verify))
                    {
                        skipped++;
                        failures++;

                        // Stage M2c: the first stop found the stored set is a menu set raid captures leave alone - every
                        // later stop would be refused the same way, so the campaign stops now, saying why
                        if (MapCapture.IsGuardedMenuSet(map))
                        {
                            stopped = MapCapture.GuardedMenuSetReason;
                            break;
                        }

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
                    var wait = MaxCaptureWaitSeconds + (verify ? (float)MapCapture.VerifyExtraSeconds : 0f);
                    waitUsed = wait;
                    var waitUntil = Time.time + wait;
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
                            $"the capture at stop {i + 1} had not finished after {Whole(wait)} s";
                        _stillCapturing = true;
                        break;
                    }

                    captured++;
                    _lastCaptured = captured;

                    // Captured stops only: a skipped one costs a fraction of a stop and would drag the average down, and
                    // an average that runs low is the one that strands a player.
                    measuredSeconds += clock.Elapsed.TotalSeconds - stopBegan;
                    measured++;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: campaign stop {i + 1} of {stops.Count} at {At(stop)} - captured.");

                    // Campaign speed step 3: the stops left out were judged against the set on disk, which only holds if this
                    // capture MERGED into it. One that replaced it (a different pixel size, render recipe or exposure - only
                    // the capture can decide those) started a new set, and the stops left out are visited after all, at the
                    // end, in the plan's order.
                    if (resumeCheck)
                    {
                        resumeCheck = false;
                        var now = MapCapture.ResumeFirstCapturedAt(map);

                        if (!string.Equals(now, survey.FirstCapturedAt, StringComparison.Ordinal))
                        {
                            for (var s = 0; s < stops.Count; s++)
                                if (survey.Skip[s]) order.Add(s);

                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: resume on {map} called off - this campaign's first capture started the set " +
                                $"afresh (first captured {now ?? "unknown"}, was {survey.FirstCapturedAt}), so the {leftOut} " +
                                "stop(s) left out are visited after all, at the end.");
                            MapCapture.Journal(map, $"resume called off - the first capture started the set afresh; {leftOut} stop(s) added back.");
                            leftOut = 0;
                        }
                    }
                }

                completed = stopped == null;

                // Campaign speed step 2: the stops held since the last checkpoint start writing HERE, on a worker, before the
                // finally moves the player to an extract or back (review: not waited for - the held set cannot change after
                // the last stop, so the teleport need not wait); the finally's release of the upload hold waits for the write
                // (MapCapture.WhenCampaignWritten), so the one upload sees it. Every end that leaves a world gets it (done,
                // early for time, cancelled, a death, a failed stop); a capture still running (a death or a timeout mid-stop)
                // is waited for by RestoreWhenDone, which starts the write after it.
                if (!nothingLeft && _gameWorld != null && !MapCapture.IsCapturing)
                    MapCapture.StartCampaignWrite(stopped != null ? $"the campaign stopped ({stopped})"
                        : endedEarly != null ? $"the campaign ended early ({endedEarly})"
                        : "the campaign's last stop");
            }
            finally
            {
                // Nothing left to cancel: the loop is over, whichever way it ended.
                _acceptingCancel = false;
                _cancelRequested = false;

                // Where the player is left, for automatic capture's "last capture" below: the start, unless Finish
                // moved them to an extract.
                var endedAt = start;

                // A capture still running when the campaign gave up is NOT run out from under (review F45): the
                // player stays at the stop until it clears, with no time backstop, before being moved back. Done
                // in a coroutine of its own because a finally cannot wait.
                //
                // WP3: the campaign's one upload is released where the last capture is known to have ended - here, or
                // in RestoreWhenDone after its wait - so that capture (and a last-stop verification build inside it)
                // is under the hold.
                // WHENEVER a capture is still running (PART-07 review), not only on the timeout: a death or an abort
                // breaks the wait loop with the stop's capture in flight, and releasing the hold here would let that
                // capture write its meta unheld - a second upload, stopped and re-queued by the supersede guard.
                if (nothingLeft)
                {
                    // Campaign speed step 3: nothing was visited, so nothing moves - no Finish, no extract, no write - and
                    // the upload hold is let go without a line: no capture was written under it, so none is owed.
                    ReleaseNothingLeft();
                    _running = false;

                    // automatic capture's "last capture" is where the player stands now - nothing moved them
                    try
                    {
                        var player = _gameWorld != null ? _gameWorld.MainPlayer : null;
                        if (player != null) endedAt = player.Transform.position;
                    }
                    catch (Exception)
                    {
                        // the start, as before
                    }
                }
                else if (MapCapture.IsCapturing)
                {
                    if (_stillCapturing)
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the capture is still running past its own worst case of {Whole(waitUsed)} s - " +
                            "you are put back as soon as it finishes.");

                    _campaignUploadNote = _campaignHold != null ? ", upload after the last capture ends" : "";
                    StartCoroutine(RestoreWhenDone(start, map, captured, stops.Count, lastStop, completed));   // clears _running itself
                }
                else
                {
                    endedAt = Finish(start, lastStop, completed);
                    ReleaseCampaignHold(
                        map, captured, stops.Count,
                        stopped != null ? "campaign stopped" : endedEarly != null ? "campaign ended early" : "campaign done");
                    _running = false;
                }

                // A campaign has just photographed the map from everywhere, including the cell it
                // started in and the stop nearest the extract it may have ended at, so automatic capture
                // treats where the player is left as its own last capture: it waits for the player to move
                // on rather than immediately taking one more picture of where they are standing.
                _autoHasCaptured = true;
                _autoLastCaptureX = endedAt.x;
                _autoLastCaptureZ = endedAt.z;
                _autoDueAt = Time.time;

                if (nothingLeft)
                {
                    var line = $"nothing left to capture ({stops.Count.ToString(CultureInfo.InvariantCulture)} stops already " +
                               "seen from close enough)";
                    Plugin.LogSource?.LogInfo($"QuestTree: capture campaign on {map}: {line}.");
                    MapCapture.Journal(map, line + ".");
                }
                else
                {
                    var seconds = (clock.ElapsedMilliseconds / 1000d).ToString("0", CultureInfo.InvariantCulture);
                    var counts = $"{stops.Count} stop(s), {captured} captured, {skipped} skipped, " +
                                 (leftOut > 0 ? $"{leftOut} left out (already seen from close enough), " : "") +
                                 $"{seconds} s{_campaignUploadNote}.";

                    // "ended early", not "done": the map is not whole, and whoever reads the journal days later should not
                    // take a raid-time or keyed end for a campaign that visited every stop.
                    var outcome = stopped != null ? $"stopped ({stopped})"
                        : endedEarly != null ? $"ended early ({endedEarly})"
                        : "done";

                    Plugin.LogSource?.LogInfo($"QuestTree: capture campaign on {map} {outcome} - {counts}");
                    MapCapture.Journal(map, $"{outcome} - {counts}");
                }
            }
        }

        /// <summary>Campaign speed step 3: the end of a campaign that had nothing left to visit - the session ended (as every
        /// end does, see <see cref="ReleaseCampaignHold"/>) and the upload hold let go quietly: no capture was written under
        /// it, so nothing is owed and there is no upload to announce. Never throws.</summary>
        private void ReleaseNothingLeft()
        {
            MapCapture.CampaignEnds();

            var hold = _campaignHold;
            _campaignHold = null;
            _campaignUploadNote = "";

            try
            {
                if (hold != null) MapTransfer.ReleaseUploads(hold);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the campaign's upload hold could not be released ({ex.Message}).");
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

        // --- the end at an extract --------------------------------------------------------------

        /// <summary>
        /// Where a campaign leaves the player, and the position it left them at. A campaign that FINISHED
        /// (<paramref name="completed"/>) - visited every stop, or ended early on purpose because the raid's time left was
        /// running short or the player pressed the key again - ends with the player standing in the nearest extract they
        /// can use right now, so a map-building raid ends with a walk of a few metres rather than a trip back across the
        /// map from where the key was pressed. The two early ends count because both mean "get me out": one is the clock
        /// saying so, the other the player. Every other end - a death, the raid gone, two failed stops in a row, a capture
        /// past its worst case, an exception, the setting or <see cref="EndAtExtract"/> off, or no usable extract - is the
        /// old <see cref="Restore"/>, because a campaign that did not finish is one the player may want to run again from
        /// where they stood.
        ///
        /// Nothing is restored first because nothing needs it: the campaign changes the player's POSITION and nothing
        /// else - no god mode, no noclip, no gravity or collision switch (see the class comment) - so the move to the
        /// extract replaces the move back to the start rather than following it, and one teleport is the whole of it.
        ///
        /// Never throws: the callers are finallys, and the lines after them (the upload hold, _running) must run.
        /// </summary>
        /// <param name="start">Where the player was standing when the key was pressed.</param>
        /// <param name="lastStop">The last stop the player was moved to, which the closing line measures from.</param>
        /// <param name="completed">Whether the campaign finished: every stop, or an early end for raid time or by the
        /// key.</param>
        /// <returns>Where the player was left: the extract's landing point, or <paramref name="start"/>.</returns>
        private Vector3 Finish(Vector3 start, Vector3 lastStop, bool completed)
        {
            try
            {
                // A dead player or a raid that is over is Restore's to report, not a reason to look for an extract.
                if (completed && EndAtExtract && (ModSettings.CampaignEndAtExtract?.Value ?? true) &&
                    _gameWorld != null && _gameWorld.MainPlayer != null && PlayerIsAlive())
                {
                    var player = _gameWorld.MainPlayer;

                    if (!TryFindExtract(player, out var name, out var landing, out var outside, out var refused))
                    {
                        Plugin.LogSource?.LogInfo($"QuestTree: campaign done, no usable extract: {refused}");
                    }
                    else if (Teleport(landing + Vector3.up * CampaignTeleportRise))
                    {
                        // The same one metre up as every stop, for the same reason (see CampaignTeleportRise): the
                        // landing is a NavMesh surface, and Player.Teleport re-bases the fall height to the arrival
                        // point, so the drop is one metre - under the game's Health.Falling.SafeHeight, no damage.
                        //
                        // A landing BESIDE the trigger is said as such: the player is not in it, so no extraction has
                        // started, and "ended at extract" would read as though one had.
                        Plugin.LogSource?.LogInfo(outside > 0f
                            ? $"QuestTree: campaign ended beside extract '{name}' " +
                              $"({outside.ToString("0.0", CultureInfo.InvariantCulture)} m outside its trigger; step in to extract)"
                            : $"QuestTree: campaign ended at extract '{name}' ({Whole(Vector3.Distance(lastStop, landing))} m " +
                              "from the last stop)");
                        return landing;
                    }

                    // A teleport that failed has said why; the player still is not left at the last stop.
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the capture campaign could not look for an extract ({ex.Message}).");
            }

            Restore(start);
            return start;
        }

        /// <summary>
        /// The nearest extract the local player can use right now, and a NavMesh point inside (or right beside) its
        /// trigger; false with every candidate's reason when there is none. The rule, in the game's own terms
        /// (CommonAssets.Scripts.Game.ExfiltrationController and EFT.Interactive.ExfiltrationPoint in this client):
        ///
        ///   - the point is this player's: <c>ExfiltrationPoint.InfiltrationMatch(player)</c>, the game's own test when a
        ///     player walks into the trigger - the spawn's entry point for a PMC exit, the profile id a Scav was dealt for
        ///     a Scav exit, side and entry point for a shared one. It is what <c>EligiblePoints(profile)</c> filters by for
        ///     a PMC, and the only answer for a Scav (whose exits are dealt by ScavExfiltrationClaim, not listed there).
        ///     Candidates are the controller's ExfiltrationPoints and ScavExfiltrationPoints; the secret exits (a separate
        ///     array, found by walking into them) and transits (TransitPoint, not an ExfiltrationPoint) are left out;
        ///   - it is OPEN: status RegularMode. NotPresent is an exit this raid did not roll, Pending/Hidden one not open,
        ///     UncompleteRequirements one waiting on a switch or a payment, Countdown a shared timer already running,
        ///     AwaitsManualActivation a flare or a lever;
        ///   - it is an Individual exit: a SharedTimer (the vehicles) or Manual one needs more than standing in it;
        ///   - every requirement it has is met for this player now - <c>ExfiltrationRequirement.Met(player, point)</c>, the
        ///     game's own check: a paid exit is met once paid, a co-op one only in Countdown, a switch-gated one once its
        ///     status is RegularMode, a timed one once its start time has passed, an empty-slot one when the slot is;
        ///   - the player is not on the controller's banned list.
        ///
        /// Of the usable ones, one with NO requirements wins over a nearer one whose requirement is met - a met
        /// requirement can stop being met on arrival - and within each the nearest to the player wins.
        /// </summary>
        /// <param name="player">The local player - the only one a campaign moves.</param>
        /// <param name="name">The chosen extract's name, as the game's settings spell it.</param>
        /// <param name="landing">Where to put the player's feet.</param>
        /// <param name="outside">Metres the standing player is outside the chosen trigger; 0 when inside it.</param>
        /// <param name="refused">When false: each of this player's extracts with the reason it was refused.</param>
        private bool TryFindExtract(Player player, out string name, out Vector3 landing, out float outside, out string refused)
        {
            name = null;
            landing = Vector3.zero;
            outside = 0f;
            refused = "";

            var controller = _gameWorld.ExfiltrationController;
            if (controller == null)
            {
                refused = "this raid has no extraction controller";
                return false;
            }

            if (controller.BannedPlayers != null && controller.BannedPlayers.Contains(player.Id))
            {
                refused = "the game has barred you from extracting in this raid";
                return false;
            }

            // The shared exits are in both arrays; the set keeps each point once.
            var points = new List<ExfiltrationPoint>();
            var seen = new HashSet<ExfiltrationPoint>();
            if (controller.ExfiltrationPoints != null)
                foreach (var p in controller.ExfiltrationPoints)
                    if (p != null && seen.Add(p)) points.Add(p);
            if (controller.ScavExfiltrationPoints != null)
                foreach (var p in controller.ScavExfiltrationPoints)
                    if (p != null && seen.Add(p)) points.Add(p);

            var from = player.Transform.position;
            var reasons = new List<string>();
            var others = 0;
            var bestTier = int.MaxValue;
            var bestDistance = float.MaxValue;

            foreach (var point in points)
            {
                var label = point.Settings?.Name;
                if (string.IsNullOrEmpty(label)) label = point.name;

                try
                {
                    // Not this player's at all - a Scav exit for a PMC, a PMC exit of another spawn. Counted rather than
                    // listed: they are most of a map's exits and none of them was ever a candidate.
                    // The side test first, explicitly: the base InfiltrationMatch reads only the profile's entry point,
                    // so a Scav whose profile carries one would otherwise match a PMC exit the game never dealt it. A
                    // Scav's exits are the Scav (and shared) points; a PMC's are the rest, the shared ones deciding for
                    // themselves in their own InfiltrationMatch.
                    var scavPoint = point is ScavExfiltrationPoint;
                    var scavPlayer = player.Profile?.Info?.Side == EPlayerSide.Savage;
                    if ((scavPlayer && !scavPoint) || !point.InfiltrationMatch(player))
                    {
                        others++;
                        continue;
                    }

                    var why = WhyNotUsable(point, player, out var hasRequirements);
                    if (why == null)
                    {
                        if (TryLanding(point, out var at, out var off))
                        {
                            var tier = hasRequirements ? 1 : 0;
                            var distance = Vector3.Distance(from, at);
                            if (tier < bestTier || (tier == bestTier && distance < bestDistance))
                            {
                                bestTier = tier;
                                bestDistance = distance;
                                name = label;
                                landing = at;
                                outside = off;
                            }

                            continue;
                        }

                        why = $"no NavMesh within {Whole(ExtractNearMetres)} m of its trigger";
                    }

                    reasons.Add($"{label}: {why}");
                }
                catch (Exception ex)
                {
                    reasons.Add($"{label}: could not be read ({ex.Message})");
                }
            }

            if (name != null) return true;

            if (reasons.Count == 0) reasons.Add("none of this raid's extracts is yours");
            refused = string.Join(", ", reasons) +
                      (others > 0 ? string.Format(CultureInfo.InvariantCulture, " (and {0} for another side or spawn)", others) : "");
            return false;
        }

        /// <summary>Why this player cannot use this extract now, or null when they can - the status, type and
        /// requirement halves of <see cref="TryFindExtract"/>'s rule.</summary>
        /// <param name="point">An extract already known to be this player's.</param>
        /// <param name="player">The local player.</param>
        /// <param name="hasRequirements">Whether the extract has any requirement at all, met or not.</param>
        private static string WhyNotUsable(ExfiltrationPoint point, Player player, out bool hasRequirements)
        {
            hasRequirements = false;

            switch (point.Status)
            {
                case EExfiltrationStatus.RegularMode:
                    break;
                case EExfiltrationStatus.NotPresent:
                    return "not in this raid";
                case EExfiltrationStatus.UncompleteRequirements:
                    return "requirements not met";
                case EExfiltrationStatus.Countdown:
                    return "shared timer running";
                case EExfiltrationStatus.AwaitsManualActivation:
                    return "needs activating";
                case EExfiltrationStatus.Pending:
                case EExfiltrationStatus.Hidden:
                    return "not open yet";
                default:
                    return "status " + point.Status;
            }

            var type = point.Settings?.ExfiltrationType ?? EExfiltrationType.Individual;
            if (type == EExfiltrationType.SharedTimer) return "shared-timer (vehicle) extract";
            if (type == EExfiltrationType.Manual) return "manually activated extract";

            // Walked by hand rather than through the game's UnmetRequirements: a Reference requirement can leave a NULL
            // entry in Requirements (CreateRequirement returns null for None and Reference), which that LINQ would call
            // Met on and throw.
            List<string> unmet = null;
            foreach (var requirement in point.Requirements ?? Array.Empty<ExfiltrationRequirement>())
            {
                if (requirement == null) continue;

                hasRequirements = true;
                if (requirement.Met(player, point)) continue;

                unmet ??= new List<string>();
                unmet.Add(requirement.Requirement.ToString());
            }

            return unmet == null ? null : "needs " + string.Join("+", unmet);
        }

        /// <summary>A NavMesh point where a standing player is inside this extract's trigger box - sampled from the box's
        /// centre, then from four points half way to its corners - or, failing that, the sampled point nearest the box when
        /// it is within <see cref="ExtractNearMetres"/> of it (a trigger too small or too close to a wall for the NavMesh
        /// to reach into). The box is the one ExfiltrationPoint.Awake reads and folds the transform's scale into, so its
        /// size is in the transform's local units, which is what the inside test compares in.
        ///
        /// A point BESIDE the box must not be above its top: the 3 m sample radius reaches a roof or the floor over a
        /// ground-floor trigger, and a point up there is metres from the extract by any path, however close it is to
        /// the box in a straight line.</summary>
        /// <param name="point">The extract.</param>
        /// <param name="landing">Where the player's feet go.</param>
        /// <param name="outside">Metres the standing player is outside the box; 0 when inside it.</param>
        private static bool TryLanding(ExfiltrationPoint point, out Vector3 landing, out float outside)
        {
            landing = Vector3.zero;
            outside = 0f;

            var box = point.GetComponent<BoxCollider>();
            var frame = box != null ? box.transform : point.transform;
            var centre = box != null ? box.center : Vector3.zero;

            // An extract with no box is one the game could not have read either (Awake would have thrown); a 2 m cube at
            // its transform still finds the ground there and lands the player beside it.
            var half = (box != null ? box.size : Vector3.one * 2f) * 0.5f;

            // Far enough down from the box's middle to reach the ground under a tall box, and never less than a storey.
            var radius = Mathf.Max(half.y + 1f, 3f);

            // The box's highest point in world space - the highest of its eight corners, so a tilted box is measured
            // by its real top rather than by its centre plus half its local height.
            var top = float.MinValue;
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? -half.x : half.x,
                    (c & 2) == 0 ? -half.y : half.y,
                    (c & 4) == 0 ? -half.z : half.z);
                top = Mathf.Max(top, frame.TransformPoint(centre + corner).y);
            }

            var bestDistance = float.MaxValue;

            for (var i = 0; i < 5; i++)
            {
                var offset = i switch
                {
                    1 => new Vector3(half.x * 0.5f, 0f, half.z * 0.5f),
                    2 => new Vector3(-half.x * 0.5f, 0f, half.z * 0.5f),
                    3 => new Vector3(half.x * 0.5f, 0f, -half.z * 0.5f),
                    4 => new Vector3(-half.x * 0.5f, 0f, -half.z * 0.5f),
                    _ => Vector3.zero
                };

                if (!NavMesh.SamplePosition(frame.TransformPoint(centre + offset), out var hit, radius, NavMesh.AllAreas))
                    continue;

                // The trigger fires on the player's capsule, not on the point under it: inside when feet, middle or head is.
                var distance = float.MaxValue;
                foreach (var height in ExtractBodyHeights)
                    distance = Mathf.Min(distance, DistanceToBox(frame, centre, half, hit.position + Vector3.up * height));

                if (distance <= 0f)
                {
                    landing = hit.position;
                    return true;
                }

                // Beside it, and only from the box's own level or below - see the summary.
                if (hit.position.y <= top && distance < bestDistance)
                {
                    bestDistance = distance;
                    landing = hit.position;
                }
            }

            if (bestDistance > ExtractNearMetres) return false;

            outside = bestDistance;
            return true;
        }

        /// <summary>World-space metres from <paramref name="world"/> to a box given in <paramref name="frame"/>'s local
        /// space by its centre and half size; 0 when inside it.</summary>
        private static float DistanceToBox(Transform frame, Vector3 centre, Vector3 half, Vector3 world)
        {
            var local = frame.InverseTransformPoint(world) - centre;
            var clamped = new Vector3(
                Mathf.Clamp(local.x, -half.x, half.x),
                Mathf.Clamp(local.y, -half.y, half.y),
                Mathf.Clamp(local.z, -half.z, half.z));

            return clamped == local ? 0f : Vector3.Distance(world, frame.TransformPoint(clamped + centre));
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

                    // WP3: turned off - what automatic capture held back goes up now, once its last capture is written.
                    if (_autoHold != null && !MapCapture.IsCapturing) ReleaseAutoHold("automatic capture turned off");
                    return;
                }

                var now = Time.time;
                if (now < _autoEvalAt) return;
                _autoEvalAt = now + AutoCaptureEvalSeconds;

                // WP3: the debounce's two flushes. Before the campaign check, so a campaign cannot starve the idle flush
                // (both holds may cover the map; the second release is the one that uploads).
                if (_autoHold != null && !MapCapture.IsCapturing)
                {
                    var rt = Time.realtimeSinceStartup;

                    if (rt - _autoLastStartAt >= AutoUploadIdleSeconds)
                        ReleaseAutoHold($"no automatic capture for {Whole(AutoUploadIdleSeconds)} s");
                    else if (rt - _autoHold.Since >= AutoUploadMaxHoldSeconds)
                        ReleaseAutoHold($"{Whole(AutoUploadMaxHoldSeconds / 60f)} min since the last upload");
                }

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

                // Stage M2c: the map's stored set is a menu set raid captures leave alone (found earlier this raid)
                if (MapCapture.IsGuardedMenuSet(map))
                {
                    Skip(MapCapture.GuardedMenuSetReason);
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

                // WP3: held BEFORE the start - TryStartCapture can run the capture's first step there and then, and the
                // hold must exist before any meta of this capture is written. A start that fails leaves the hold for the
                // idle rule to release, with nothing owed.
                if (AutoUploadDebounce && _autoHold == null)
                {
                    _autoHold = MapTransfer.HoldUploads(this, map, "automatic capture", preempts: false);

                    // The idle clock starts with the hold (PART-07 review): a start that then fails must not have the
                    // idle rule release the hold at the next evaluation and this line take it again, a second later.
                    _autoLastStartAt = Time.realtimeSinceStartup;
                }

                // automatic: this tick comes round every few seconds, so the capture builds the 3D mesh
                // only for a map that has none yet - the pictures are taken exactly as ever. A campaign
                // stop and a key press are places somebody chose and always build it.
                if (!MapCapture.TryStartCapture(automatic: true))
                {
                    Skip("the capture did not start");
                    return;
                }

                _autoLastStartAt = Time.realtimeSinceStartup;

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

        /// <summary>
        /// Ends automatic capture's hold on its map's uploads (WP3), and so issues the one upload of what it held back
        /// - unless nothing was written, sharing is off, or another hold (a campaign's) still covers the map. The next
        /// automatic capture takes the hold again, so a player who keeps walking uploads at most once per
        /// <see cref="AutoUploadMaxHoldSeconds"/>. Never throws.
        /// </summary>
        /// <param name="reason">Why now, for the line.</param>
        private void ReleaseAutoHold(string reason)
        {
            var hold = _autoHold;
            _autoHold = null;
            if (hold == null) return;

            try
            {
                var outcome = MapTransfer.ReleaseUploads(hold);

                if (outcome == MapTransfer.UploadStart.Started || outcome == MapTransfer.UploadStart.Queued)
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: automatic capture: 1 upload of {hold.Key} ({hold.Deferred} capture(s) held back, {reason}).");
                else
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: automatic capture: no upload of {hold.Key} ({outcome}, {reason}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: automatic capture's upload hold could not be released ({ex.Message}).");
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

        /// <summary>Seconds as minutes to one decimal, for the raid-time lines: a whole minute is too coarse when the
        /// question is whether a 90 s stop still fits.</summary>
        private static string Minutes(float seconds) =>
            float.IsNaN(seconds) || float.IsInfinity(seconds)
                ? "n/a"
                : (seconds / 60f).ToString("0.0", CultureInfo.InvariantCulture);
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
    /// becomes 9x5 cells of 115x100 m). Every stop is then the centre of its cell, which is inside that
    /// rectangle by construction. The cells can be a good deal smaller than CampaignCellMetres, which is why the
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
