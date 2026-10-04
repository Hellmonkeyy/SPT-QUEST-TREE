using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT.Settings.Graphics;
using Newtonsoft.Json;
using QuestTree.UI;
using UnityEngine;
using UnityEngine.AI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// The in-game self-test, started in the MAIN MENU by the Advanced setting "Run self-test (main menu)"
    /// (<see cref="ModSettings.SelfTestRun"/>). It runs unattended, then writes
    /// BepInEx/plugins/QuestTree/selftest/selftest-&lt;time&gt;.json and one summary line,
    /// "QuestTree: self-test: N passed, M failed ...". tools/tests/README_selftest.md describes the JSON.
    ///
    /// Steps: (1) preconditions, which are MenuCaptureRunner.CanStart's gates; (2) a real menu capture of the smallest map
    /// into its throwaway "-menu" TEST key (MapCapture.MenuSession's probe constructor), asserted from the run's own
    /// results; (3) every catalog set with a mesh opened in 3D (Map3DView.Attach on a viewport of the test's own, one
    /// at a time), measured, closed, and checked for leftovers and VRAM; (4) every catalog set's floors decoded in 2D,
    /// checking the viewing copy is used; (5) cleanup, which always runs: the test viewport goes, the test set's folder is
    /// deleted (ONLY that folder, see <see cref="DeleteRefusal"/>), and the real sets are compared with a snapshot taken at
    /// the start.
    ///
    /// Safety. It never runs in a raid: the start is gated like a capture from game files, and a raid starting cancels
    /// it (<see cref="Poll"/>). It never touches a real set: the capture key is the location's id plus
    /// MapCapture.MenuCaptureKeySuffix, checked before the start, and the snapshot check proves it afterwards. It never
    /// uploads: a "-menu" key is refused by MapCapture.MenuUploads, which the capture's write and its upload hold both
    /// ask, and that is checked before the start. Hosted on TrackerHotkey, the one behaviour proven to tick in EFT's menu
    /// (memory ddol-objects-dead-in-menu). Every step is guarded, so a throw fails that step and the run moves on.
    /// </summary>
    internal static class SelfTest
    {
        private const string Tag = "QuestTree: self-test: ";

        /// <summary>How far a map's VRAM may stay above its before-value once its 3D view is closed and the caches are
        /// dropped, MB. DXGI's figure is the whole process's and moves with the driver's own allocations.</summary>
        private const double VramToleranceMb = 256d;

        /// <summary>How far VRAM may stand, at the end of the 3D sweep, above the first map's after-value (the sweep's
        /// warmed-up baseline: shaders, the light-probe rig) before the trend check fails, MB.</summary>
        private const double SweepToleranceMb = 512d;

        /// <summary>A map whose load (attach to ready) takes longer than this is a WARN, ms.</summary>
        private const double LoadWarnMs = 15000d;

        /// <summary>The second wait for idle, after the 30 settle frames, before the open-view measurement.</summary>
        private const double IdleRewaitSeconds = 30d;

        private const double FirstFrameTimeoutSeconds = 240d;
        private const double SettleTimeoutSeconds = 60d;
        private const double DecodeTimeoutSeconds = 60d;

        /// <summary>The longest the test capture may run before the self-test asks it to stop. The host's own caps are
        /// longer still (MapCapture.MenuWorstCaseSeconds); Labyrinth takes about a minute.</summary>
        private const double CaptureCapSeconds = 1800d;

        /// <summary>How long cleanup waits for a stopped capture to unload its map.</summary>
        private const double HostWaitSeconds = 900d;

        /// <summary>Frames without a yield before <see cref="Poll"/> calls the run dead. The run yields every frame.</summary>
        private const int DeadAfterFrames = 600;

        /// <summary>The small maps, smallest first, for when the catalog knows no extent for a location.</summary>
        private static readonly string[] SmallMapsFirst = { "labyrinth", "factory4_day", "factory4_night", "sandbox", "sandbox_high" };

        private static bool _running;
        private static string _cancel;
        private static string _phase;
        private static Stopwatch _clock;
        private static int _lastTick;
        private static int _handledFrame = -1;
        private static bool _warned;
        private static Report _report;

        /// <summary>The test capture's key ("labyrinth-menu"), once chosen.</summary>
        private static string _testKey;

        /// <summary>The test key's folder did not exist before this run, and the capture was started: cleanup deletes it.
        /// False leaves it alone, so a test set the maintainer already had is never deleted.</summary>
        private static bool _testFolderMine;

        /// <summary>The host claim of the test capture, 0 before it starts.</summary>
        private static int _claim;

        /// <summary>The viewport this run's 3D view is drawn in, or null.</summary>
        private static GameObject _viewport;

        private static Dictionary<string, string> _realSetsBefore;
        private static int _mipLimitBefore;
        private static bool _sdBefore;
        private static int _navBefore;

        /// <summary>A run is going (from the start until its report is written).</summary>
        internal static bool Running => _running;

        /// <summary>What the run is doing, for the Maps tab, or null.</summary>
        internal static string Progress =>
            !_running ? null
                : $"{_phase ?? "starting"} ({Clock(_clock?.Elapsed.TotalSeconds ?? 0d)})" + (_cancel != null ? " - stopping" : "");

        /// <summary>Asks the run to stop: a capture is asked to stop, the open view closes, the rest is skipped, and
        /// cleanup runs. A no-op with no run.</summary>
        /// <param name="why">For the report.</param>
        internal static void Cancel(string why)
        {
            if (!_running || _cancel != null) return;

            _cancel = string.IsNullOrEmpty(why) ? "cancelled" : why;
            Log($"cancel asked ({_cancel}) - the step running stops and cleanup runs.");
            if (_claim != 0 && MenuMapHost.CurrentClaim == _claim) MenuMapHost.RequestStop("self-test: " + _cancel);
            ModSettings.RequestRepaint();
        }

        /// <summary>From TrackerHotkey.Update, every frame: the start switch, the cancels (the switch again, the map
        /// capture key, a raid starting) and the dead-run check. Never throws.</summary>
        internal static void Poll(MonoBehaviour host)
        {
            if (host == null || _handledFrame == Time.frameCount) return;
            _handledFrame = Time.frameCount;

            try
            {
                if (!ModSettings.Ready || ModSettings.SelfTestRun == null) return;

                if (_running)
                {
                    if (ModSettings.SelfTestRun.Value)
                    {
                        ModSettings.SelfTestRun.Value = false;
                        Cancel("the setting was switched on again");
                    }
                    else if (ModSettings.CaptureMapKey != null && ModSettings.ShortcutDown(ModSettings.CaptureMapKey.Value))
                    {
                        Cancel("the map capture key was pressed");
                    }

                    if (MenuMapHost.RaidStarting()) Cancel("a raid is starting");

                    if (Time.frameCount - _lastTick > DeadAfterFrames) OnDead();
                    return;
                }

                if (!ModSettings.SelfTestRun.Value) return;

                // Off FIRST, before anything can throw or crash the game, so no launch ever starts a run by itself.
                ModSettings.SelfTestRun.Value = false;
                Start(host);
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"{Tag}the poll failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // --- the run ---------------------------------------------------------------------------------------------------

        private static void Start(MonoBehaviour host)
        {
            var report = NewReport();
            var pre = report.Add("preconditions");
            var clock = Stopwatch.StartNew();

            string why = null;
            try
            {
                why = Refusal();
            }
            catch (Exception ex)
            {
                why = $"the check threw {ex.GetType().Name}";
            }

            pre.Check("main menu only, no capture running, hideout not loaded", why == null,
                why ?? "MenuCaptureRunner.CanStart passed: main menu screen, no GameWorld, no raid, the hideout scene (bunker_2, " +
                "the hideout preload) not loaded, no capture or menu run, no restart advised, the tracker's menu host up");

            // The delete guard, proven before anything can be deleted: it must refuse a real key and a path escape,
            // and accept only the test key. A guard that accepted everything would fail here.
            pre.Check("the delete guard refuses a real set's key", DeleteRefusal("labyrinth", "labyrinth", @"C:\x\captures") != null,
                DeleteRefusal("labyrinth", "labyrinth", @"C:\x\captures") ?? "accepted - the guard is broken");
            pre.Check("the delete guard refuses a path escape", DeleteRefusal(@"..\labyrinth-menu", @"..\labyrinth-menu", @"C:\x\captures") != null,
                DeleteRefusal(@"..\labyrinth-menu", @"..\labyrinth-menu", @"C:\x\captures") ?? "accepted - the guard is broken");
            pre.Check("the delete guard refuses a key that is not this run's", DeleteRefusal("bigmap-menu", "labyrinth-menu", @"C:\x\captures") != null,
                DeleteRefusal("bigmap-menu", "labyrinth-menu", @"C:\x\captures") ?? "accepted - the guard is broken");
            pre.Check("the delete guard accepts this run's test key", DeleteRefusal("labyrinth-menu", "labyrinth-menu", @"C:\x\captures") == null,
                DeleteRefusal("labyrinth-menu", "labyrinth-menu", @"C:\x\captures") ?? "accepted");
            // The existing-test-set refusal, proven: it fires for a folder on disk and for one only in the start snapshot,
            // and passes an absent key.
            var onDisk = TestSetConflict("labyrinth-menu", true, null);
            var inSnapshot = TestSetConflict("labyrinth-menu", false, new[] { "bigmap", "Labyrinth-menu" });
            var absent = TestSetConflict("labyrinth-menu", false, new[] { "bigmap", "labyrinth" });
            pre.Check("an existing test set refuses the capture", onDisk != null && inSnapshot != null && absent == null,
                $"on disk: {onDisk ?? "NOT refused"}; in the snapshot: {inSnapshot ?? "NOT refused"}; absent: {absent ?? "accepted"}");

            // The GPU-error filter, proven both ways: a d3d11 texture failure logged as an error counts; the info line
            // about a D3D11 device never counts, whatever its type.
            pre.Check("the GPU-error filter counts a d3d11 texture failure", IsGpuError(LogType.Error, GpuFailureSample), GpuFailureSample);
            pre.Check("the GPU-error filter ignores the Media Foundation info line",
                !IsGpuError(LogType.Log, MediaFoundationSample) && !IsGpuError(LogType.Warning, MediaFoundationSample) &&
                !IsGpuError(LogType.Error, MediaFoundationSample),
                MediaFoundationSample);

            pre.Seconds = clock.Elapsed.TotalSeconds;
            pre.Decide();

            if (why != null || pre.Result != "pass")
            {
                report.Overall = "refused";
                report.CancelReason = why ?? "a precondition check failed";
                Log($"refused - {report.CancelReason}. Nothing was run.");
                WriteReport(report);
                return;
            }

            _running = true;
            _cancel = null;
            _phase = "starting";
            _clock = Stopwatch.StartNew();
            _lastTick = Time.frameCount;
            _report = report;
            _testKey = null;
            _testFolderMine = false;
            _claim = 0;

            try
            {
                host.StartCoroutine(Run(report));
                ModSettings.RequestRepaint();
            }
            catch (Exception ex)
            {
                _running = false;
                report.Overall = "fail";
                report.CancelReason = $"the run could not start ({ex.GetType().Name}: {ex.Message})";
                Log(report.CancelReason + ".");
                WriteReport(report);
            }
        }

        /// <summary>Why a run may not start now, or null. MenuCaptureRunner.CanStart is the capture's own gate: no raid,
        /// no GameWorld, the main menu screen, the hideout scene not loaded (which is what the hideout preload leaves),
        /// no capture or menu-host run, no restart advised, and the tracker's host up.</summary>
        private static string Refusal()
        {
            if (ModEnvironment.IsHeadlessClient) return "a headless client has no main menu";
            if (MenuCaptureRunner.Running) return "a capture from game files is running";
            if (MenuMapHost.Busy) return "the menu map host is busy (a map is hosted in the menu)";
            if (MapCapture.IsCapturing) return "a map capture is running";
            if (!MenuCaptureRunner.CanStart(null, out var why)) return why;
            return null;
        }

        private static IEnumerator Run(Report report)
        {
            var total = Stopwatch.StartNew();
            StartListening();

            try
            {
                Log($"started (Quest Tracker {ModInfo.Stamp}). Do not start a raid or open the hideout until the summary line. " +
                    "Cancel: switch the setting on again, press the map capture key, or use the Maps tab's Cancel row.");

                // Through BepInEx's log source, whose lines reach BOTH LogOutput.log and Player.log. EFT's managed
                // Debug.unityLogger drops Log lines: the 2026-10-04 run's Debug.Log marker reached neither file. check_logs.py
                // takes the Player.log lines between this and the END marker as this run's.
                report.BeginMarker = $"QUESTTREE-SELFTEST-BEGIN {report.RunId}";
                Guard("the begin marker", () => Plugin.LogSource?.LogInfo(report.BeginMarker));

                // The tracker panel is closed at the start, so the Maps tab's picture is not on screen when this run frees
                // pictures. If the player reopens it, the Maps tab draws flat (MapView.MeshFor) and its capture section
                // shows the self-test's progress and a Cancel row instead of the capture buttons.
                Guard("closing the tracker panel", () =>
                {
                    if (TrackerAccess.IsOpen) TrackerAccess.Toggle();
                    MapView.ForgetDrawnMap();
                });

                var canary = report.Add("logcanary");
                var steps = Guarded(canary, Canary(canary));
                while (steps.MoveNext()) yield return steps.Current;

                Guard("the real sets' snapshot", () => _realSetsBefore = SnapshotSets());
                Guard("the state baseline", () =>
                {
                    _mipLimitBefore = QualitySettings.globalTextureMipmapLimit;
                    _sdBefore = GraphicsSettingsController.ApplySDModeOnRuntime;
                    _navBefore = NavVertices();
                });

                var capture = report.Add("capture");
                steps = Guarded(capture, CaptureStep(capture));
                while (steps.MoveNext()) yield return steps.Current;

                var solid = report.Add("sweep3d");
                steps = Guarded(solid, Sweep3D(solid));
                while (steps.MoveNext()) yield return steps.Current;

                var flat = report.Add("sweep2d");
                steps = Guarded(flat, Sweep2D(flat));
                while (steps.MoveNext()) yield return steps.Current;

                var cleanup = report.Add("cleanup");
                steps = Guarded(cleanup, Cleanup(cleanup), always: true);
                while (steps.MoveNext()) yield return steps.Current;
            }
            finally
            {
                // No yield here (C# allows none in a finally): whatever ended the run, the viewport goes, the report is
                // written and the slot frees. A coroutine Unity stopped without this is OnDead's.
                DestroyViewport();
                StopListening();
                EndMarker(report);
                report.Seconds = total.Elapsed.TotalSeconds;
                if (report.Overall == null) report.Overall = _cancel != null ? "cancelled" : null;
                if (_cancel != null) report.CancelReason = _cancel;
                WriteReport(report);
                _running = false;
                _report = null;
                _phase = null;
                ModSettings.RequestRepaint();
            }
        }

        /// <summary>Told by <see cref="Poll"/> that the run stopped yielding - its host was destroyed (a menu rebuilt) and
        /// Unity dropped the coroutine without its finally. What cleanup can do without frames is done here.</summary>
        private static void OnDead()
        {
            var report = _report;
            _running = false;
            _report = null;

            try
            {
                if (_claim != 0 && MenuMapHost.CurrentClaim == _claim) MenuMapHost.RequestStop("self-test: its run died");
                DestroyViewport();
                StopListening();

                if (report != null)
                {
                    EndMarker(report);

                    var dead = report.Add("cleanup");
                    dead.Error = $"the run stopped yielding for {DeadAfterFrames} frames (its host was destroyed?)";
                    dead.Check("ran without dying", false, dead.Error);

                    // The test set goes only when no capture can still be writing it.
                    if (!MenuMapHost.Busy && !MapCapture.IsCapturing)
                    {
                        var what = DeleteTestSet(out var deleted, out var absent);
                        dead.Check("test set removed", deleted || absent, what);
                    }
                    else
                    {
                        dead.Check("test set removed", false, $"the menu host is still busy, so {_testKey} was left; delete it by hand");
                    }

                    dead.Decide();
                    report.Overall = "fail";
                    report.CancelReason = dead.Error;
                    WriteReport(report);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"{Tag}the dead-run cleanup failed ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                _phase = null;
                ModSettings.RequestRepaint();
            }
        }

        /// <summary>Logs the END marker through BepInEx's log source (it reaches LogOutput.log and Player.log) and records
        /// it. Never throws.</summary>
        private static void EndMarker(Report report)
        {
            try
            {
                report.EndMarker = $"QUESTTREE-SELFTEST-END {report.RunId}";
                Plugin.LogSource?.LogInfo(report.EndMarker);
            }
            catch (Exception)
            {
                // The marker is a convenience for check_logs.py; the times in the report remain.
            }
        }

        /// <summary>Drives one step, every frame a yield, turning a throw into a failed check (C# allows no yield in a try
        /// with a catch). A step that is not <paramref name="always"/> is skipped once the run is cancelled.</summary>
        private static IEnumerator Guarded(Part part, IEnumerator body, bool always = false)
        {
            var clock = Stopwatch.StartNew();

            if (!always && _cancel != null)
            {
                part.Result = "skipped";
                part.Error = "cancelled: " + _cancel;
                yield break;
            }

            while (true)
            {
                bool more;

                try
                {
                    more = body.MoveNext();
                }
                catch (Exception ex)
                {
                    part.Error = $"{ex.GetType().Name}: {ex.Message} at {FirstFrame(ex.StackTrace)}";
                    part.Check("ran without throwing", false, part.Error);
                    more = false;
                }

                if (!more) break;

                _lastTick = Time.frameCount;
                yield return null;
            }

            try
            {
                (body as IDisposable)?.Dispose();
            }
            catch (Exception)
            {
                // A step's finally that throws has already been recorded by the throw above.
            }

            _lastTick = Time.frameCount;
            part.Seconds = clock.Elapsed.TotalSeconds;
            part.Decide();
        }

        // --- step 2: the capture test ------------------------------------------------------------------------------------

        private static IEnumerator CaptureStep(Part step)
        {
            _phase = "capture test: choosing the map";

            if (MenuMapHost.Busy)
            {
                step.Check("the menu host is free", false, "a map is already hosted in the menu (another run holds the slot)");
                yield break;
            }

            var root = MapCatalog.CapturesRootForSelfTest();
            var map = ChooseCaptureMap(step, root, out var noMap);
            if (map == null)
            {
                step.Check("a map to capture", false, noMap);
                yield break;
            }

            var key = MenuMapHost.LocationKey(map.Id, out var noKey);
            if (key == null)
            {
                step.Check("a map to capture", false, $"{map.Id}: {noKey}");
                yield break;
            }

            // The probe's constructor: the location's id plus "-menu", written merge-or-fresh. Never the real key.
            var session = new MapCapture.MenuSession(map.Id, key);
            _testKey = session.Key;

            var testKey = string.Equals(session.Key, key + MapCapture.MenuCaptureKeySuffix, StringComparison.Ordinal) &&
                          session.Write == MapCapture.MenuWriteMode.MergeOrFresh && MapCapture.IsUsableKey(session.Key);
            step.Check("writes the test key, never the real set", testKey,
                $"key {session.Key} ({MapCapture.MenuWriteText(session.Write)}); the real set is {key}");

            var uploads = MapCapture.MenuUploads(session.Key);
            step.Check("the test key is never uploaded", !uploads,
                uploads ? $"MapCapture.MenuUploads({session.Key}) is TRUE" : $"MapCapture.MenuUploads({session.Key}) is false: the write skips UploadCapture and no upload hold is taken");

            if (!testKey || uploads) yield break;

            // MergeOrFresh replaces a stored set it cannot merge into IN PLACE, with no copy kept (MapCapture.Fresh), so a
            // test set that is already there is never captured into: the step refuses rather than risk it.
            var conflict = TestSetConflict(session.Key, root != null && Directory.Exists(Path.Combine(root, session.Key)), _realSetsBefore?.Keys);
            step.Check("no test set of that key exists already", conflict == null,
                conflict ?? $"{session.Key} is absent from captures and from the start snapshot");
            if (conflict != null) yield break;

            step.Measure("map", map.Id);
            step.Measure("mapName", map.Name);
            step.Measure("testKey", session.Key);

            var vram = new Vram();
            var reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            step.Measure("vramBeforeMb", vram.UsageMb);

            var counts = Counts.Now();
            var mip = QualitySettings.globalTextureMipmapLimit;
            var sd = GraphicsSettingsController.ApplySDModeOnRuntime;
            var nav = NavVertices();
            step.Measure("mipLimitBefore", mip);
            step.Measure("sdModeBefore", sd);
            step.Measure("navMeshVerticesBefore", nav);

            var host = TrackerHotkey.Current;
            if (!MenuMapHost.Start(host, map.Id, () => MapCapture.RunMenuCapture(session), out var refusal, out var claim))
            {
                step.Check("the capture started", false, refusal);
                yield break;
            }

            _claim = claim;
            _testFolderMine = true;   // TestSetConflict proved the folder absent, so anything there now is this run's

            Log($"capture test: {map.Id} '{map.Name}' into the test key {session.Key} (the real set {key} is not touched).");

            var clock = Stopwatch.StartNew();
            while (MenuMapHost.Busy && MenuMapHost.CurrentClaim == claim)
            {
                if (_cancel != null && !MenuMapHost.StopAsked) MenuMapHost.RequestStop("self-test: " + _cancel);
                if (clock.Elapsed.TotalSeconds > CaptureCapSeconds && !MenuMapHost.StopAsked)
                    MenuMapHost.RequestStop($"the self-test's {CaptureCapSeconds:0} s cap");

                _phase = $"capture test: {map.Name}: {MenuMapHost.Phase ?? "starting"}";
                yield return null;
            }

            var seconds = clock.Elapsed.TotalSeconds;
            step.Measure("seconds", Math.Round(seconds, 1));
            _phase = "capture test: checking";

            for (var i = 0; i < 10; i++) yield return null;
            reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            step.Measure("vramAfterMb", vram.UsageMb);

            var outcome = MenuMapHost.LastOutcome;
            if (outcome != null && outcome.Claim != claim) outcome = null;

            step.Check("the capture wrote a set", session.Written != null,
                session.Written ?? session.Stopped ?? session.WriteFailed ?? session.NeedsReplace ?? outcome?.Problem ?? "nothing was written");

            var entry = MapCatalog.CaptureForSelfTest(session.Key);
            var floors = entry?.Layers.Where(l => l.HasArtwork && File.Exists(l.ImagePath)).ToList() ?? new List<DynamicMapsLibrary.MapLayer>();
            step.Check("floors written", entry != null && floors.Count > 0 && floors.Count == entry.Layers.Count,
                entry == null ? "the catalog has no set under the test key" : $"{floors.Count} of {entry.Layers.Count} floor picture(s) on disk");
            step.Check("mesh written", entry != null && !string.IsNullOrEmpty(entry.MeshPath) && File.Exists(entry.MeshPath),
                entry?.MeshPath ?? "no mesh in the set's meta");
            var sides = entry?.Sides.Count(s => s?.Picture != null && File.Exists(s.Picture.ImagePath)) ?? 0;
            step.Check("sides written", sides > 0, $"{sides} side picture(s) on disk");

            step.Check("the host's restore matches", outcome != null && outcome.Problem == null && outcome.Trouble == null &&
                                                     MenuMapHost.RestartAdvised == null,
                outcome == null ? "no verdict for this run's claim"
                    : outcome.Problem ?? outcome.Trouble ?? MenuMapHost.RestartAdvised ?? "every hosted scene unloaded and the compared state matches");

            var mipAfter = QualitySettings.globalTextureMipmapLimit;
            var sdAfter = GraphicsSettingsController.ApplySDModeOnRuntime;
            step.Check("texture mip limit and SD flag back to baseline", mipAfter == mip && sdAfter == sd,
                $"mip limit {mip} -> {mipAfter}, SD mode {sd} -> {sdAfter}");

            var navAfter = NavVertices();
            step.Check("NavMesh vertices back to 0", navAfter == nav && navAfter == 0, $"NavMesh vertices {nav} -> {navAfter}");

            var delta = Counts.Now().Minus(counts);
            step.Check("no d3d11 or texture-creation errors", delta.Gpu == 0, delta.Describe());
            step.Measure("unityExceptions", delta.Exceptions);
            step.Measure("unityErrors", delta.Errors);
        }

        /// <summary>The smallest capturable map whose test key has NO folder yet, in captures or in the start snapshot. It
        /// goes by the catalog's extent where a set of the map exists, else by <see cref="SmallMapsFirst"/>. A map with a
        /// test set is never picked. Null, with the reason, when every map has one.</summary>
        private static MenuMapHost.CapturableLocation ChooseCaptureMap(Part step, string root, out string why)
        {
            why = null;
            var maps = MenuMapHost.ListCapturableLocations();
            var sets = MapCatalog.SetsForSelfTest().Where(s => !IsTestOrBackup(s.Key)).ToList();

            double Area(MenuMapHost.CapturableLocation m)
            {
                var best = double.PositiveInfinity;

                foreach (var set in sets)
                {
                    var named = m.Ids.Any(id => string.Equals(id, set.Key, StringComparison.OrdinalIgnoreCase) ||
                                                set.Entry.InternalNames.Any(n => string.Equals(n, id, StringComparison.OrdinalIgnoreCase)));
                    if (!named) continue;

                    foreach (var layer in set.Entry.Layers)
                    {
                        if (!layer.HasBounds) continue;
                        best = Math.Min(best, (double)layer.BoundsSize.x * layer.BoundsSize.y);
                    }
                }

                return best;
            }

            int Rank(MenuMapHost.CapturableLocation m)
            {
                var i = Array.FindIndex(SmallMapsFirst, s => m.Ids.Any(id => string.Equals(id, s, StringComparison.OrdinalIgnoreCase)));
                return i < 0 ? SmallMapsFirst.Length : i;
            }

            bool HasTestFolder(MenuMapHost.CapturableLocation m)
            {
                var key = m.Id + MapCapture.MenuCaptureKeySuffix;
                return TestSetConflict(key, root != null && Directory.Exists(Path.Combine(root, key)), _realSetsBefore?.Keys) != null;
            }

            var ordered = maps
                .Select(m => (Map: m, Area: Area(m), Rank: Rank(m), Taken: HasTestFolder(m)))
                .OrderBy(c => c.Taken)
                .ThenBy(c => c.Area)
                .ThenBy(c => c.Rank)
                .ThenBy(c => c.Map.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            step.Measure("candidates", string.Join("; ", ordered.Take(6).Select(c =>
                $"{c.Map.Id} (area {(double.IsInfinity(c.Area) ? "unknown" : c.Area.ToString("0", CultureInfo.InvariantCulture) + " m2")}" +
                $"{(c.Taken ? ", test set exists" : "")})")));

            foreach (var c in ordered)
                if (!c.Taken && MenuMapHost.HasScenes(c.Map.Id, out _)) return c.Map;

            why = ordered.Count == 0 ? "no capturable location with scenes was found"
                : ordered.All(c => c.Taken) ? "every capturable map already has a -menu test set; delete one to run the capture test"
                : "no capturable map without a -menu test set has scenes";
            return null;
        }

        /// <summary>Why <paramref name="key"/> must not be captured into, or null. Pure. A folder of that name in captures,
        /// or in the snapshot taken at the run's start, is a test set someone already has, and MergeOrFresh could replace
        /// it in place with no copy kept. The preconditions prove it fires.</summary>
        internal static string TestSetConflict(string key, bool folderExists, IEnumerable<string> snapshotFolders)
        {
            var inSnapshot = snapshotFolders != null && key != null &&
                             snapshotFolders.Any(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));

            return folderExists || inSnapshot ? $"a test set {key} already exists; delete it or pick another map" : null;
        }

        // --- step 3: the 3D sweep -------------------------------------------------------------------------------------

        private static IEnumerator Sweep3D(Part step)
        {
            var wait = WaitForHost(step);
            while (wait.MoveNext()) yield return wait.Current;
            if (MenuMapHost.Busy) yield break;

            var sets = MapCatalog.SetsForSelfTest().Where(s => !IsTestOrBackup(s.Key)).ToList();
            var meshed = sets.Where(s => !string.IsNullOrEmpty(s.Entry.MeshPath)).OrderBy(s => s.Host).ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();

            step.Measure("sets", sets.Count);
            step.Measure("setsWithMesh", meshed.Count);
            step.Check("the catalog has a set with a 3D mesh", meshed.Count > 0, $"{meshed.Count} of {sets.Count} set(s) have a mesh");

            // Nothing cached from before the sweep: each map's numbers are its own.
            Map3DView.DropCaches();
            DynamicMapsLibrary.ReleaseCachedSprites();

            step.Maps = new List<Part>();

            var vram = new Vram();
            var reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            var baseline = vram.UsageMb;
            step.Measure("vramBaselineMb", baseline);

            var afters = new List<double>();

            foreach (var set in meshed)
            {
                var part = new Part { Name = set.Key, Source = set.Host ? "host" : "local" };
                step.Maps.Add(part);

                var one = Guarded(part, OpenIn3D(set.Key, set.Entry, part));
                while (one.MoveNext()) yield return one.Current;

                if (part.Measurements.TryGetValue("vramAfterMb", out var after) && after is double mb && mb >= 0d) afters.Add(mb);
            }

            if (meshed.Count == 0 || _cancel != null) yield break;

            // The trend: does VRAM keep climbing across the sweep? A closed view's memory is freed by the driver later
            // than the next read, so a single map's after-value proves nothing (those are WARNs). The sweep's last value,
            // read after a 3 s settle, is compared with the first map's after-value, the warmed-up baseline.
            var settle = Stopwatch.StartNew();
            while (settle.Elapsed.TotalSeconds < 3d) yield return null;

            reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            var final = vram.UsageMb;

            step.Measure("vramFinalMb", final);
            step.Measure("vramAfterSeriesMb", afters.Select(a => Math.Round(a, 0)).ToList());

            var known = final >= 0d && afters.Count > 0;
            var first = known ? afters[0] : -1d;
            if (known)
            {
                step.Measure("vramFirstAfterMb", first);
                step.Measure("vramPeakAfterMb", afters.Max());
            }

            step.Check("VRAM does not keep climbing across the sweep", known && final - first <= SweepToleranceMb,
                known
                    ? string.Format(CultureInfo.InvariantCulture,
                        "first map's after {0:0} MB -> sweep end {1:0} MB ({2:+0;-0} MB, tolerance {3:0}); before the sweep {4:0} MB",
                        first, final, final - first, SweepToleranceMb, baseline)
                    : "VRAM could not be read (EFT's VRamUsage plugin)");
        }

        private static IEnumerator OpenIn3D(string key, DynamicMapsLibrary.MapEntry entry, Part part)
        {
            _phase = $"3D sweep: {key}";

            if (!MenuMapHost.InMenu(out var notMenu))
            {
                part.Check("in the main menu", false, notMenu);
                Cancel($"left the main menu ({notMenu})");
                yield break;
            }

            if (MenuMapHost.Busy)
            {
                part.Check("no map hosted in the menu", false, "the menu map host is busy");
                yield break;
            }

            var level = entry.DefaultLayer?.Level ?? 0;
            part.Measure("level", level);

            for (var i = 0; i < 10; i++) yield return null;

            var vram = new Vram();
            var reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            var vramBefore = vram.UsageMb;
            part.Measure("vramBeforeMb", vramBefore);

            var meshes = Ids<Mesh>();
            var textures = Ids<Texture>();
            var counts = Counts.Now();

            try
            {
                string refused = null;
                var viewport = MakeViewport();
                var clock = Stopwatch.StartNew();

                var view = Map3DView.Attach(viewport, entry, entry.MeshPath, key, level, new Color(0.17f, 0.18f, 0.19f, 0.95f), null,
                    (path, reason) => refused = string.IsNullOrEmpty(reason) ? "refused" : reason);

                part.Measure("attachMs", Math.Round(clock.Elapsed.TotalMilliseconds, 1));
                part.Check("the view opened", view != null, view != null ? "attached" : Map3DView.LastRefusal);

                if (view != null)
                {
                    // The view's own "first frame drawn in X ms" is the CPU time of the ONE frame that first renders (a
                    // clock started inside that LateUpdate). Everything before it - the file read, the meshes built, the
                    // paced uploads - is the load. So: loadMs is attach to the build being ready (SelfTestReady), and
                    // firstFrameMs is the duration of the frame the first render ran in.
                    double? readyMs = null;
                    while (view != null && !view.SelfTestBroke && view.SelfTestFramesRendered == 0 && _cancel == null &&
                           clock.Elapsed.TotalSeconds < FirstFrameTimeoutSeconds)
                    {
                        if (readyMs == null && view.SelfTestReady) readyMs = clock.Elapsed.TotalMilliseconds;
                        yield return null;
                    }

                    var drawn = view != null && view.SelfTestFramesRendered > 0;
                    part.Check("first frame drawn", drawn && !view.SelfTestBroke,
                        drawn ? "drawn" :
                        view == null ? "the view was destroyed" :
                        view.SelfTestBroke ? "the view stopped: " + (refused ?? (view.SelfTestRefusal.Length > 0 ? view.SelfTestRefusal : "it broke")) :
                        _cancel != null ? "cancelled" : $"no frame in {FirstFrameTimeoutSeconds:0} s");

                    if (drawn)
                    {
                        // Observed in the frame after the first render, before that frame's LateUpdate, so the counts are the
                        // first frame's and unscaledDeltaTime is the duration of the frame the first render ran in. The view
                        // becomes ready and renders in the same LateUpdate, so a ready flag first seen now dates from then.
                        var observedMs = clock.Elapsed.TotalMilliseconds;
                        var frameMs = Time.unscaledDeltaTime * 1000d;
                        var loadMs = readyMs ?? Math.Max(0d, observedMs - frameMs);

                        part.Measure("loadMs", Math.Round(loadMs, 1));
                        part.Measure("firstFrameMs", Math.Round(frameMs, 1));
                        part.Measure("drawCalls", view.SelfTestDrawCalls);
                        part.Measure("trianglesSubmitted", view.SelfTestTrianglesSubmitted);
                        part.Measure("trianglesInView", view.SelfTestTrianglesInView);
                        part.Measure("renderMs", Math.Round(view.SelfTestRenderMs, 2));

                        if (loadMs > LoadWarnMs)
                            part.Warn("loads within 15 s", $"attach to ready took {loadMs / 1000d:0.0} s");
                        else
                            part.Check("loads within 15 s", true, $"attach to ready took {loadMs / 1000d:0.0} s");

                        // A set with a mesh that draws nothing at its default floor is broken (Shoreline, 2026-10-04: 0 draw
                        // calls, 0 triangles, and every other check passed).
                        part.Check("draws triangles at its default floor",
                            view.SelfTestTrianglesSubmitted > 0 && view.SelfTestDrawCalls > 0,
                            $"{view.SelfTestDrawCalls} draw call(s), {view.SelfTestTrianglesSubmitted:#,##0} triangle(s) submitted at level {level}");
                    }

                    var settle = Stopwatch.StartNew();
                    while (view != null && !view.SelfTestBroke && !view.SelfTestIdle && _cancel == null &&
                           settle.Elapsed.TotalSeconds < SettleTimeoutSeconds)
                    {
                        yield return null;
                    }

                    part.Measure("settledSeconds", Math.Round(settle.Elapsed.TotalSeconds, 1));

                    for (var i = 0; i < 30; i++) yield return null;

                    // Walls and pictures can arrive after the first idle (Woods, Interchange and Sandbox were busy again at
                    // the measurement in 2026-10-04's run), so wait for idle once more before measuring.
                    var again = Stopwatch.StartNew();
                    while (view != null && !view.SelfTestBroke && !view.SelfTestIdle && _cancel == null &&
                           again.Elapsed.TotalSeconds < IdleRewaitSeconds)
                    {
                        yield return null;
                    }

                    var idle = view != null && view.SelfTestIdle;
                    part.Measure("idleRewaitSeconds", Math.Round(again.Elapsed.TotalSeconds, 1));
                    part.Measure("idle", idle);
                    if (!idle && view != null && !view.SelfTestBroke && _cancel == null)
                        part.Warn("idle at measurement", $"still uploading or building walls after {SettleTimeoutSeconds + IdleRewaitSeconds:0} s; VRAM read while busy");
                    part.Measure("framesRendered", view != null ? view.SelfTestFramesRendered : 0L);
                    part.Check("still drawing after it settled", view != null && !view.SelfTestBroke,
                        view == null ? "the view was destroyed" :
                        view.SelfTestBroke ? "the view stopped: " + (refused ?? view.SelfTestRefusal) : "drawing");

                    reading = vram.Read();
                    while (reading.MoveNext()) yield return reading.Current;
                    part.Measure("vramOpenMb", vram.UsageMb);
                }
            }
            finally
            {
                DestroyViewport();
            }

            // Closed. Destroy is deferred to the frame's end; then the caches every view shares are dropped, as the menu
            // teardown before a raid drops them, and the pictures with them.
            yield return null;
            yield return null;
            Map3DView.DropCaches();
            DynamicMapsLibrary.ReleaseCachedSprites();
            MapCapture.CollectGarbage("the self-test closed a 3D view", force: true);

            for (var i = 0; i < 30; i++) yield return null;

            reading = vram.Read();
            while (reading.MoveNext()) yield return reading.Current;
            var vramAfter = vram.UsageMb;
            part.Measure("vramAfterMb", vramAfter);

            var left = NewViewObjects<Mesh>(meshes, key).Concat(NewViewObjects<Texture>(textures, key)).ToList();
            part.Measure("leftoverObjects", left.Count);
            part.Check("its meshes and textures were destroyed", left.Count == 0,
                left.Count == 0 ? "none of the view's meshes or textures is left" : string.Join(", ", left.Take(10)));

            // Per map a WARN only: the driver frees a closed view's memory later than the next read (2026-10-04: bigmap +833
            // MB, then the next map's before-value fell back). The sweep's trend check is the one that fails.
            part.Measure("vramAfterFresh", vram.Changed);
            var known = vramBefore >= 0d && vramAfter >= 0d;
            var detail = known
                ? string.Format(CultureInfo.InvariantCulture, "{0:0} -> {1:0} MB ({2:+0;-0} MB, tolerance {3:0})", vramBefore, vramAfter,
                    vramAfter - vramBefore, VramToleranceMb)
                : "VRAM could not be read (EFT's VRamUsage plugin)";

            if (known && vramAfter - vramBefore <= VramToleranceMb) part.Check("VRAM back near its before-value", true, detail);
            else part.Warn("VRAM back near its before-value", detail);

            var delta = Counts.Now().Minus(counts);
            part.Check("no d3d11 or texture-creation errors and no exceptions", delta.Gpu == 0 && delta.Exceptions == 0, delta.Describe());
        }

        // --- step 4: the 2D sweep -------------------------------------------------------------------------------------

        private static IEnumerator Sweep2D(Part step)
        {
            var wait = WaitForHost(step);
            while (wait.MoveNext()) yield return wait.Current;
            if (MenuMapHost.Busy) yield break;

            var sets = MapCatalog.SetsForSelfTest()
                .Where(s => !IsTestOrBackup(s.Key))
                .OrderBy(s => s.Host).ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            step.Measure("sets", sets.Count);
            step.Check("the catalog has a set", sets.Count > 0, $"{sets.Count} set(s)");
            step.Maps = new List<Part>();

            foreach (var set in sets)
            {
                var part = new Part { Name = set.Key, Source = set.Host ? "host" : "local" };
                step.Maps.Add(part);

                var one = Guarded(part, Decode(set.Key, set.Entry, part));
                while (one.MoveNext()) yield return one.Current;

                // Freed per map, so a sweep of eleven maps never holds them all.
                DynamicMapsLibrary.ReleaseCachedSprites();
            }
        }

        private static IEnumerator Decode(string key, DynamicMapsLibrary.MapEntry entry, Part part)
        {
            _phase = $"2D sweep: {key}";

            if (!MenuMapHost.InMenu(out var notMenu))
            {
                part.Check("in the main menu", false, notMenu);
                Cancel($"left the main menu ({notMenu})");
                yield break;
            }

            if (MenuMapHost.Busy)
            {
                part.Check("no map hosted in the menu", false, "the menu map host is busy");
                yield break;
            }

            var counts = Counts.Now();
            var floors = entry.Layers.Where(l => l.HasArtwork).ToList();
            part.Measure("floors", floors.Count);

            foreach (var layer in floors)
            {
                if (_cancel != null) yield break;

                var name = "floor" + layer.Level.ToString(CultureInfo.InvariantCulture);

                // The viewing copy beside the picture (MapCapture.ViewFileName): when it is on disk, it is what is drawn.
                var folder = Path.GetDirectoryName(layer.ImagePath) ?? "";
                var copy = Path.Combine(folder, MapCapture.ViewFileName(key, layer.Level));
                var hasCopy = File.Exists(copy);
                part.Measure(name + ".viewCopy", hasCopy);

                if (hasCopy)
                {
                    var used = string.Equals(Path.GetFullPath(layer.ImagePath), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase);
                    part.Check(name + ": the viewing copy is used", used,
                        used ? Path.GetFileName(copy) : $"draws {Path.GetFileName(layer.ImagePath)} though {Path.GetFileName(copy)} is on disk");
                }

                var clock = Stopwatch.StartNew();
                Sprite sprite;
                while (!layer.TryGetSprite(out sprite) && clock.Elapsed.TotalSeconds < DecodeTimeoutSeconds) yield return null;

                var texture = sprite != null ? sprite.texture : null;
                var ok = texture != null && texture.width > 0 && texture.height > 0;
                part.Measure(name + ".decodeMs", Math.Round(clock.Elapsed.TotalMilliseconds, 1));
                if (ok) part.Measure(name + ".size", $"{texture.width}x{texture.height} {texture.format}");

                part.Check(name + ": the picture decodes", ok,
                    ok ? Path.GetFileName(layer.ImagePath) :
                    layer.ArtworkFailed ? "the picture would not decode" : $"no picture in {DecodeTimeoutSeconds:0} s");
            }

            var delta = Counts.Now().Minus(counts);
            part.Check("no d3d11 or texture-creation errors and no exceptions", delta.Gpu == 0 && delta.Exceptions == 0, delta.Describe());
        }

        // --- step 5: cleanup (always) ---------------------------------------------------------------------------------

        private static IEnumerator Cleanup(Part step)
        {
            _phase = "cleaning up";
            DestroyViewport();

            var clock = Stopwatch.StartNew();
            while (MenuMapHost.Busy && MenuMapHost.CurrentClaim == _claim && _claim != 0 && clock.Elapsed.TotalSeconds < HostWaitSeconds)
            {
                if (!MenuMapHost.StopAsked && _cancel != null) MenuMapHost.RequestStop("self-test: " + _cancel);
                yield return null;
            }

            var free = !(MenuMapHost.Busy && MenuMapHost.CurrentClaim == _claim && _claim != 0) && !MapCapture.IsCapturing;
            step.Check("the test capture has ended", free, free ? "ended" : $"still running after {HostWaitSeconds:0} s");

            if (free)
            {
                var what = DeleteTestSet(out var deleted, out var absent);
                step.Measure("testKey", _testKey);
                step.Measure("testFolderDeleted", deleted);
                step.Check("test set removed", deleted || absent, what);
            }
            else
            {
                step.Check("test set removed", !_testFolderMine, $"a capture may still be writing {_testKey}; it was left");
            }

            Guard("rescanning the catalog", MapCatalog.InvalidateCaptures);
            Guard("dropping the map memory", () =>
            {
                MapView.ForgetDrawnMap();
                DynamicMapsLibrary.ReleaseCachedSprites();
            });

            yield return null;

            var changed = CompareSets(_realSetsBefore, SnapshotSets());
            step.Check("real sets untouched", _realSetsBefore != null && changed.Count == 0,
                _realSetsBefore == null ? "no snapshot was taken at the start"
                    : changed.Count == 0 ? $"{_realSetsBefore.Count} folder(s) under captures as they were" : string.Join("; ", changed.Take(10)));

            var mip = QualitySettings.globalTextureMipmapLimit;
            var sd = GraphicsSettingsController.ApplySDModeOnRuntime;
            step.Check("texture mip limit and SD flag at the run's baseline", mip == _mipLimitBefore && sd == _sdBefore,
                $"mip limit {_mipLimitBefore} -> {mip}, SD mode {_sdBefore} -> {sd}");

            var nav = NavVertices();
            step.Check("NavMesh vertices at the run's baseline", nav == _navBefore, $"{_navBefore} -> {nav}");
        }

        /// <summary>Deletes this run's test set folder - ONLY when <see cref="DeleteRefusal"/> passes, this run created
        /// it (<see cref="_testFolderMine"/>), it holds no sub-folder, is no link, and every file in it is named after the
        /// test key, as a capture of that key names them. Returns what happened. <paramref name="absent"/>: there was
        /// nothing of this run's to remove (no capture started, or a capture stopped before writing its folder).</summary>
        private static string DeleteTestSet(out bool deleted, out bool absent)
        {
            deleted = false;
            absent = false;

            if (_testKey == null || !_testFolderMine)
            {
                // The capture never started: TestSetConflict refused a folder that was already there, which is not this
                // run's and is never deleted, or the step stopped earlier.
                absent = true;
                return "nothing to remove: no test capture was started";
            }

            var root = MapCatalog.CapturesRootForSelfTest();
            var refusal = DeleteRefusal(_testKey, _testKey, root);
            if (refusal != null) return "not deleted: " + refusal;

            try
            {
                var folder = Path.GetFullPath(Path.Combine(root, _testKey));
                if (!Directory.Exists(folder))
                {
                    absent = true;
                    return $"nothing to remove: the capture wrote no {_testKey} folder";
                }

                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return "not deleted: the folder is a link";
                if (Directory.GetDirectories(folder).Length > 0) return "not deleted: it holds a sub-folder, which a capture never writes";

                var files = Directory.GetFiles(folder);
                var stray = files.Select(Path.GetFileName)
                    .Where(n => !n.StartsWith(_testKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (stray.Count > 0) return $"not deleted: it holds files a capture of {_testKey} would not name ({string.Join(", ", stray.Take(5))})";

                foreach (var file in files) File.Delete(file);
                Directory.Delete(folder, recursive: false);

                deleted = true;
                Log($"deleted the test set {folder} ({files.Length} file(s)).");
                return $"deleted {folder} ({files.Length} file(s))";
            }
            catch (Exception ex)
            {
                return $"not deleted: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>Why <paramref name="key"/>'s folder under <paramref name="root"/> must not be deleted, or null when it
        /// may. Pure: paths only, no disk. It is accepted only when it is <paramref name="thisRunsKey"/> exactly, ends
        /// with MapCapture.MenuCaptureKeySuffix after a non-empty location id, is a usable capture key (letters, digits,
        /// '-' and '_': no separator, no "..") and is no set-aside backup, and its folder resolves directly under the
        /// captures folder with that exact name. The preconditions run it against a real key and a path escape.</summary>
        internal static string DeleteRefusal(string key, string thisRunsKey, string root)
        {
            var suffix = MapCapture.MenuCaptureKeySuffix;

            if (string.IsNullOrEmpty(key)) return "no key";
            if (!string.Equals(key, thisRunsKey, StringComparison.Ordinal)) return $"{key} is not this run's test key";
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || key.Length <= suffix.Length)
                return $"{key} is not a test key (it does not end with {suffix})";
            if (!MapCapture.IsUsableKey(key)) return $"{key} is not a capture key";
            if (key.IndexOf(MapCapture.SetAsideInfix, StringComparison.OrdinalIgnoreCase) >= 0) return $"{key} is a set-aside backup";
            if (string.IsNullOrEmpty(root)) return "no captures folder";

            try
            {
                var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var folder = Path.GetFullPath(Path.Combine(full, key));

                if (!string.Equals(Path.GetDirectoryName(folder), full, StringComparison.OrdinalIgnoreCase))
                    return $"{folder} is not directly under {full}";
                if (!string.Equals(Path.GetFileName(folder), key, StringComparison.Ordinal))
                    return $"{folder} is not named {key}";
            }
            catch (Exception ex)
            {
                return $"the path could not be resolved ({ex.GetType().Name})";
            }

            return null;
        }

        // --- helpers ----------------------------------------------------------------------------------------------------

        /// <summary>Waits for the menu host to be free before a sweep (a stopped capture unloading), failing the step
        /// when it is not.</summary>
        private static IEnumerator WaitForHost(Part step)
        {
            var clock = Stopwatch.StartNew();
            while (MenuMapHost.Busy && _cancel == null && clock.Elapsed.TotalSeconds < HostWaitSeconds)
            {
                _phase = $"waiting for the menu host ({MenuMapHost.Phase ?? "busy"})";
                yield return null;
            }

            if (MenuMapHost.Busy) step.Check("the menu host is free", false, "a map is still hosted in the menu");
        }

        /// <summary>A test set ("-menu") or a set-aside backup: never swept, never in the real-set comparison.</summary>
        private static bool IsTestOrBackup(string key) =>
            string.IsNullOrEmpty(key) ||
            key.EndsWith(MapCapture.MenuCaptureKeySuffix, StringComparison.OrdinalIgnoreCase) ||
            key.IndexOf(MapCapture.SetAsideInfix, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The 3D view's viewport: a plate on the tracker's root canvas (the canvas that ticks in the menu),
        /// most of the screen, over everything.</summary>
        private static RectTransform MakeViewport()
        {
            DestroyViewport();

            var parent = TrackerHotkey.Current != null ? TrackerHotkey.Current.transform.parent : null;
            if (parent == null) throw new InvalidOperationException("the tracker's root canvas is gone");

            var go = new GameObject("QuestTreeSelfTestViewport", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0.15f, 0.15f);
            rect.anchorMax = new Vector2(0.85f, 0.85f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsLastSibling();

            var plate = go.GetComponent<UnityEngine.UI.Image>();
            plate.color = new Color(0f, 0f, 0f, 0.85f);
            plate.raycastTarget = false;

            _viewport = go;
            return rect;
        }

        /// <summary>The 3D view stopped and its viewport destroyed (the view releases everything from OnDestroy).
        /// Idempotent.</summary>
        private static void DestroyViewport()
        {
            var go = _viewport;
            _viewport = null;
            if (go == null) return;

            try
            {
                var view = go.GetComponent<Map3DView>();
                if (view != null) view.Abandon();

                go.transform.SetParent(null, worldPositionStays: false);
                UnityEngine.Object.Destroy(go);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"{Tag}the test viewport could not be destroyed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static HashSet<int> Ids<T>() where T : UnityEngine.Object =>
            new HashSet<int>(Resources.FindObjectsOfTypeAll<T>().Where(o => o != null).Select(o => o.GetInstanceID()));

        /// <summary>The objects of <typeparamref name="T"/> made since <paramref name="before"/> that are still alive and
        /// carry the 3D view's names: "QuestTreeMap3D..." (its textures, materials and sky) or "&lt;key&gt;-..." (its
        /// chunk meshes). The light-probe rig is kept for the session by design, so it is not counted.</summary>
        private static IEnumerable<string> NewViewObjects<T>(HashSet<int> before, string key) where T : UnityEngine.Object =>
            Resources.FindObjectsOfTypeAll<T>()
                .Where(o => o != null && !before.Contains(o.GetInstanceID()) && ViewOwned(o.name, key))
                .Select(o => $"{typeof(T).Name} '{o.name}'")
                .ToList();

        private static bool ViewOwned(string name, string key) =>
            !string.IsNullOrEmpty(name) &&
            ((name.StartsWith("QuestTreeMap3D", StringComparison.Ordinal) && !name.StartsWith("QuestTreeMap3DLightProbe", StringComparison.Ordinal)) ||
             (!string.IsNullOrEmpty(key) && name.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase)));

        private static int NavVertices()
        {
            try
            {
                return NavMesh.CalculateTriangulation().vertices?.Length ?? 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Every folder under captures with a stamp of its files (count, bytes, newest write) - stat only.</summary>
        private static Dictionary<string, string> SnapshotSets()
        {
            var sets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var root = MapCatalog.CapturesRootForSelfTest();
            if (root == null || !Directory.Exists(root)) return sets;

            foreach (var folder in Directory.GetDirectories(root))
            {
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
                sets[Path.GetFileName(folder)] = string.Format(CultureInfo.InvariantCulture, "{0} file(s), {1} bytes, newest {2:o}",
                    files.Count, files.Sum(f => f.Length), files.Count == 0 ? DateTime.MinValue : files.Max(f => f.LastWriteTimeUtc));
            }

            return sets;
        }

        /// <summary>The folders that differ between two snapshots, leaving out this run's test key only.</summary>
        private static List<string> CompareSets(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            var changed = new List<string>();
            if (before == null || after == null) return changed;

            bool Ours(string name) => _testKey != null && string.Equals(name, _testKey, StringComparison.OrdinalIgnoreCase);

            foreach (var pair in before)
            {
                if (Ours(pair.Key)) continue;
                if (!after.TryGetValue(pair.Key, out var now)) changed.Add($"{pair.Key} is gone");
                else if (!string.Equals(now, pair.Value, StringComparison.Ordinal)) changed.Add($"{pair.Key}: {pair.Value} -> {now}");
            }

            foreach (var pair in after)
                if (!Ours(pair.Key) && !before.ContainsKey(pair.Key)) changed.Add($"{pair.Key} appeared");

            return changed;
        }

        private static void Guard(string what, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"{Tag}{what} failed ({ex.GetType().Name}: {ex.Message}).");
                _report?.Notes.Add($"{what} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string FirstFrame(string stackTrace) =>
            (stackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        private static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0d, seconds));
            return t.TotalHours >= 1d
                ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        private static void Log(string text) => Plugin.LogSource?.LogInfo(Tag + text);

        /// <summary>
        /// A VRAM read that is not one fetch behind. VramProbe hands back the LAST fetched value, and the 2026-10-04 run's
        /// per-map readings lagged a whole map (bigmap's after-value was the next map's before-value, which then fell back).
        /// So: note the value held now, issue a fetch, give the render thread three frames to run it, then keep fetching and
        /// polling every frame until the value differs from the one held before, or 1 s passes. The value is taken then.
        /// </summary>
        private sealed class Vram
        {
            internal double UsageMb = -1d;

            /// <summary>Whether the value changed from the stale one within the poll (false: 1 s passed unchanged).</summary>
            internal bool Changed;

            internal IEnumerator Read()
            {
                var stale = VramProbe.TryRead(out var held, out _) ? held : -1d;   // also issues a fetch
                yield return null;
                yield return null;
                yield return null;

                var clock = Stopwatch.StartNew();
                var now = stale;
                Changed = false;

                while (true)
                {
                    now = VramProbe.TryRead(out var usage, out _) ? usage : -1d;   // reads, and issues the next fetch
                    if (now >= 0d && Math.Abs(now - stale) > 0.05d)
                    {
                        Changed = true;
                        break;
                    }

                    if (clock.Elapsed.TotalSeconds >= 1d) break;
                    yield return null;
                }

                UsageMb = now >= 0d ? Math.Round(now, 1) : -1d;
            }
        }

        // --- Unity's messages -------------------------------------------------------------------------------------------
        //
        // logMessageReceivedTHREADED: the plain event fires for the main thread's messages only, and a d3d11 failure is
        // logged from the render thread. The handler runs on any thread, so the counters are Interlocked and the samples
        // are under a lock. A native d3d11 line that goes only to Player.log reaches neither event: tools/tests/check_logs.py
        // reads Player.log for those, between this run's BEGIN and END markers.

        private static volatile bool _listening;
        [ThreadStatic] private static bool _inHandler;
        private static int _exceptions;
        private static int _errors;
        private static int _gpu;
        /// <summary>The worker canary: 0 not seen, 1 seen on a worker thread, 2 seen but raised on the main thread.</summary>
        private static int _canarySeen;

        private static int _canaryMainSeen;

        /// <summary>Every canary token starts with this; the handler drops any line containing it before counting.</summary>
        private const string CanaryPrefix = "qt-selftest-canary-";
        private static int _mainThreadId;
        private static volatile string _canaryToken;
        private static readonly object MessageLock = new object();
        private static readonly List<string> _firstMessages = new List<string>();
        private static readonly HashSet<string> _seenMessages = new HashSet<string>();
        private const int MaxMessages = 30;

        /// <summary>A d3d11 texture failure as Unity logs it, which the filter must count (a precondition proves it).</summary>
        private const string GpuFailureSample =
            "d3d11: failed to create 2D texture id=1234 width=3906 height=7812 mips=1 dxgifmt=77 [D3D error was 80070057 E_INVALIDARG]";

        /// <summary>An info line at every game start, which the filter must never count (a precondition proves it).</summary>
        private const string MediaFoundationSample = "D3D11 device created for Microsoft Media Foundation video decoding.";

        private static void ResetMessages()
        {
            Interlocked.Exchange(ref _exceptions, 0);
            Interlocked.Exchange(ref _errors, 0);
            Interlocked.Exchange(ref _gpu, 0);
            Interlocked.Exchange(ref _canarySeen, 0);
            Interlocked.Exchange(ref _canaryMainSeen, 0);

            lock (MessageLock)
            {
                _firstMessages.Clear();
                _seenMessages.Clear();
            }
        }

        private static void StartListening()
        {
            ResetMessages();
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _canaryToken = CanaryPrefix + Guid.NewGuid().ToString("N");
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
            _listening = true;
        }

        private static void StopListening()
        {
            _listening = false;

            try
            {
                Application.logMessageReceivedThreaded -= OnLog;
            }
            catch (Exception)
            {
                // Inert once _listening is false.
            }

            if (_report != null) FillMessages(_report);
        }

        /// <summary>
        /// The canary: one line logged from a WORKER thread with this run's token. Only a threaded handler sees it.
        ///
        /// Found in play (2026-10-04 run): EFT's managed Debug.unityLogger drops Log and Warning before any callback, so
        /// even a main-thread Debug.Log never reached Player.log, LogOutput.log or the handler. The game's own errors did
        /// reach the handler (103 exceptions). So a worker-thread line and a main-thread line are logged as a pair, first
        /// at Warning and, when neither arrives, once more at Error with a fresh token. Each line names itself, and
        /// check_logs.py must ignore "qt-selftest-canary-". The handler drops every canary line before counting.
        /// - the worker's line arrives: PASS, and the level is recorded;
        /// - only the main thread's arrives: the threaded callback misses worker threads, so FAIL;
        /// - neither arrives at Error either: Unity does not deliver this logger's lines to managed callbacks, so SKIP with
        ///   that finding, and check_logs.py's Player.log scan is the authority.
        /// </summary>
        private static IEnumerator Canary(Part step)
        {
            var logger = UnityEngine.Debug.unityLogger;

            step.Measure("loggerEnabled", logger != null && logger.logEnabled);
            step.Measure("loggerFilter", logger != null ? logger.filterLogType.ToString() : "none");
            step.Measure("loggerHandler", logger?.logHandler?.GetType().FullName ?? "none");
            step.Measure("gameMessagesSoFar", Volatile.Read(ref _exceptions) + Volatile.Read(ref _errors));

            // Warning first, then ONE retry at Error: EFT's logger says Warning is allowed and drops it all the same
            // (2026-10-04), so a Warning pair that never arrives decides nothing on its own.
            var tried = new List<string>();

            foreach (var kind in new[] { LogType.Warning, LogType.Error })
            {
                // Each attempt has a fresh token under the shared prefix, so a late line from the previous attempt is
                // dropped by the handler without setting this attempt's flags.
                var token = CanaryPrefix + Guid.NewGuid().ToString("N");
                Interlocked.Exchange(ref _canarySeen, 0);
                Interlocked.Exchange(ref _canaryMainSeen, 0);
                _canaryToken = token;

                var level = kind.ToString();
                step.Measure($"token{level}", token);
                tried.Add(level);

                void Emit(string text)
                {
                    if (kind == LogType.Warning) UnityEngine.Debug.LogWarning(text);
                    else UnityEngine.Debug.LogError(text);
                }

                Task.Run(() => Emit($"{token}-w (Quest Tree self-test canary from a worker thread - not a game error)"));
                Emit($"{token}-m (Quest Tree self-test canary from the main thread - not a game error)");

                for (var i = 0; i < 300 && (Volatile.Read(ref _canarySeen) == 0 || Volatile.Read(ref _canaryMainSeen) == 0); i++)
                    yield return null;

                var worker = Volatile.Read(ref _canarySeen);
                var main = Volatile.Read(ref _canaryMainSeen) != 0;
                step.Measure($"worker{level}Seen", worker != 0);
                step.Measure($"main{level}Seen", main);

                if (worker != 0)
                {
                    // Both arrived (or the worker's did, which is the proof): PASS at this level.
                    step.Measure("canaryLevel", level);
                    step.Check("error counter receives threaded logs", true,
                        $"the worker thread's {level} reached logMessageReceivedThreaded" +
                        (worker == 1 ? " on that worker thread" : " (raised on the main thread)") +
                        (main ? "; the main thread's arrived too" : ""));
                    yield break;
                }

                if (main)
                {
                    step.Measure("canaryLevel", level);
                    step.Check("error counter receives threaded logs", false,
                        $"error counter not receiving threaded logs (the main thread's {level} arrived, the worker thread's did not)");
                    yield break;
                }

                // Neither line of this pair arrived: the logger dropped this level. Try the next one.
            }

            step.Measure("canaryLevel", "none");
            step.Result = "skipped";
            step.Warn("error counter receives threaded logs",
                $"not testable here: neither the worker's nor the main thread's canary arrived at {string.Join(" or ", tried)} " +
                $"({step.Measurements["loggerHandler"]}), while the game's own messages did; check_logs.py's Player.log scan " +
                "is the authority for d3d11 errors");
        }

        /// <summary>Counts what Unity logs while the run goes, on any thread: exceptions, errors (and asserts), and the
        /// GPU's, which are d3d11 and texture-creation failures like those the 2026-10-03 crash began with. The mod's own
        /// lines are not counted. Never logs and never throws.</summary>
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!_listening || _inHandler) return;
            _inHandler = true;

            try
            {
                var text = condition ?? "";

                // Every canary line, this attempt's or a late one from an earlier attempt, returns BEFORE any counting.
                if (text.IndexOf(CanaryPrefix, StringComparison.Ordinal) >= 0)
                {
                    var token = _canaryToken;
                    if (token != null && text.IndexOf(token, StringComparison.Ordinal) >= 0)
                    {
                        if (text.IndexOf(token + "-m", StringComparison.Ordinal) >= 0) Interlocked.Exchange(ref _canaryMainSeen, 1);
                        else if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) Interlocked.Exchange(ref _canarySeen, 1);
                        else Interlocked.Exchange(ref _canarySeen, 2);   // the worker's line, but raised on the main thread
                    }

                    return;
                }

                if (text.IndexOf("QuestTree", StringComparison.Ordinal) >= 0) return;

                var gpu = IsGpuError(type, text);

                if (type == LogType.Exception) Interlocked.Increment(ref _exceptions);
                else if (type == LogType.Error || type == LogType.Assert) Interlocked.Increment(ref _errors);
                if (gpu) Interlocked.Increment(ref _gpu);

                if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                {
                    var line = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
                    if (line.Length > 240) line = line.Substring(0, 240) + "...";
                    line = $"{type} [{_phase}]: {line}";

                    lock (MessageLock)
                    {
                        if (_firstMessages.Count < MaxMessages && _seenMessages.Add(line)) _firstMessages.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                // A handler that throws would be re-entered by Unity's report of it.
            }
            finally
            {
                _inHandler = false;
            }
        }

        /// <summary>A GPU failure: an Error, Exception or Assert ONLY, whose text is a d3d11/d3d12/DXGI failure or a
        /// texture that could not be created. An info or warning line never counts, and nor does a d3d11 line that reports
        /// no failure (the Media Foundation device line).</summary>
        internal static bool IsGpuError(LogType type, string text)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return false;
            if (string.IsNullOrEmpty(text)) return false;

            bool Has(string s) => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

            var failed = Has("fail") || Has("E_INVALIDARG") || Has("E_OUTOFMEMORY") || Has("invalid") || Has("could not") ||
                         Has("unable to") || Has("error");
            var device = Has("d3d11") || Has("d3d12") || Has("dxgi");
            var texture = Has("texture") && (Has("failed to create") || Has("could not create") || Has("E_INVALIDARG"));

            return (device && failed) || texture;
        }

        private struct Counts
        {
            internal int Exceptions;
            internal int Errors;
            internal int Gpu;

            internal static Counts Now() => new Counts
            {
                Exceptions = Volatile.Read(ref _exceptions),
                Errors = Volatile.Read(ref _errors),
                Gpu = Volatile.Read(ref _gpu),
            };

            internal Counts Minus(Counts before) =>
                new Counts { Exceptions = Exceptions - before.Exceptions, Errors = Errors - before.Errors, Gpu = Gpu - before.Gpu };

            internal string Describe() => $"{Gpu} d3d11/texture error(s), {Exceptions} exception(s), {Errors} other error(s) from Unity";
        }

        // --- the report -------------------------------------------------------------------------------------------------

        /// <summary>One step, or one map of a sweep. Serialized as tools/tests/README_selftest.md describes.</summary>
        private sealed class Part
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string Source;
            [JsonProperty("result")] public string Result = "pass";
            [JsonProperty("seconds")] public double Seconds;
            [JsonProperty("error")] public string Error;
            [JsonProperty("checks")] public List<CheckResult> Checks = new List<CheckResult>();
            [JsonProperty("measurements")] public SortedDictionary<string, object> Measurements = new SortedDictionary<string, object>(StringComparer.Ordinal);
            [JsonProperty("maps", NullValueHandling = NullValueHandling.Ignore)] public List<Part> Maps;

            internal void Check(string name, bool pass, string detail) =>
                Checks.Add(new CheckResult { Name = name, Pass = pass, Detail = detail ?? "" });

            /// <summary>A WARN: recorded with pass true and warn true. It counts toward "warnings", never toward
            /// "failed".</summary>
            internal void Warn(string name, string detail) =>
                Checks.Add(new CheckResult { Name = name, Pass = true, Warn = true, Detail = detail ?? "" });

            internal void Measure(string name, object value) => Measurements[name] = value;

            /// <summary>"pass" when every check passed, no error was recorded and every map passed; "skipped" stays.</summary>
            internal void Decide()
            {
                Seconds = Math.Round(Seconds, 2);
                if (Result == "skipped") return;

                var failed = Error != null || Checks.Any(c => !c.Pass) || (Maps != null && Maps.Any(m => m.Result == "fail"));
                Result = failed ? "fail" : "pass";
            }
        }

        private sealed class CheckResult
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("pass")] public bool Pass;
            [JsonProperty("warn")] public bool Warn;
            [JsonProperty("detail")] public string Detail;
        }

        private sealed class Report
        {
            [JsonProperty("schema")] public int Schema = 1;
            [JsonProperty("kind")] public string Kind = "questtree-selftest";
            [JsonProperty("overall")] public string Overall;
            [JsonProperty("passed")] public int Passed;
            [JsonProperty("failed")] public int Failed;
            [JsonProperty("warnings")] public int Warnings;
            [JsonProperty("cancelReason")] public string CancelReason;
            [JsonProperty("runId")] public string RunId;
            [JsonProperty("startedUtc")] public string StartedUtc;
            [JsonProperty("finishedUtc")] public string FinishedUtc;
            [JsonProperty("startedLocal")] public string StartedLocal;
            [JsonProperty("finishedLocal")] public string FinishedLocal;

            /// <summary>The lines this run logged through Unity (so into Player.log as well as LogOutput.log) at its
            /// start and end. check_logs.py takes the Player.log lines between them as the run's.</summary>
            [JsonProperty("beginMarker")] public string BeginMarker;
            [JsonProperty("endMarker")] public string EndMarker;
            [JsonProperty("seconds")] public double Seconds;
            [JsonProperty("versions")] public SortedDictionary<string, object> Versions = new SortedDictionary<string, object>(StringComparer.Ordinal);
            [JsonProperty("steps")] public List<Part> Steps = new List<Part>();
            [JsonProperty("messages")] public SortedDictionary<string, object> Messages = new SortedDictionary<string, object>(StringComparer.Ordinal);
            [JsonProperty("notes")] public List<string> Notes = new List<string>();

            internal Part Add(string name)
            {
                var part = new Part { Name = name };
                Steps.Add(part);
                return part;
            }
        }

        private static Report NewReport()
        {
            var now = DateTime.Now;
            var report = new Report
            {
                RunId = Guid.NewGuid().ToString("N").Substring(0, 12),
                StartedUtc = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                StartedLocal = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            };

            // A refused run reports no messages; a started one counts from StartListening.
            ResetMessages();

            void Version(string name, Func<object> read)
            {
                try
                {
                    report.Versions[name] = read();
                }
                catch (Exception ex)
                {
                    report.Versions[name] = $"unread ({ex.GetType().Name})";
                }
            }

            Version("mod", () => ModInfo.Version);
            Version("build", () => ModInfo.Stamp);
            Version("game", () => Application.version);
            Version("unity", () => Application.unityVersion);
            Version("gpu", () => SystemInfo.graphicsDeviceName);
            Version("graphicsApi", () => SystemInfo.graphicsDeviceVersion);
            Version("gpuMemoryMb", () => SystemInfo.graphicsMemorySize);
            Version("systemMemoryMb", () => SystemInfo.systemMemorySize);
            Version("os", () => SystemInfo.operatingSystem);
            return report;
        }

        private static void FillMessages(Report report)
        {
            report.Messages["exceptions"] = Volatile.Read(ref _exceptions);
            report.Messages["errors"] = Volatile.Read(ref _errors);
            report.Messages["gpuErrors"] = Volatile.Read(ref _gpu);
            report.Messages["handler"] = "Application.logMessageReceivedThreaded";

            lock (MessageLock) report.Messages["first"] = _firstMessages.ToList();
        }

        /// <summary>Counts the checks, settles the overall verdict, writes the JSON and logs the summary line. Never
        /// throws.</summary>
        private static void WriteReport(Report report)
        {
            string path = null;

            try
            {
                if (!report.Messages.ContainsKey("first")) FillMessages(report);

                var checks = report.Steps.SelectMany(s => s.Checks.Concat(s.Maps?.SelectMany(m => m.Checks) ?? Enumerable.Empty<CheckResult>())).ToList();
                report.Passed = checks.Count(c => c.Pass);
                report.Failed = checks.Count - report.Passed;
                report.Warnings = checks.Count(c => c.Warn);
                var finished = DateTime.Now;
                report.FinishedUtc = finished.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                report.FinishedLocal = finished.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                report.Seconds = Math.Round(report.Seconds, 1);
                if (report.Overall == null) report.Overall = report.Failed > 0 || report.Steps.Any(s => s.Result == "fail") ? "fail" : "pass";

                var dir = Path.Combine(Path.GetDirectoryName(typeof(SelfTest).Assembly.Location) ?? "", "selftest");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, $"selftest-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"{Tag}the report could not be written ({ex.GetType().Name}: {ex.Message}).");
                path = null;
            }

            try
            {
                var failedNames = report.Steps
                    .SelectMany(s => s.Checks.Where(c => !c.Pass).Select(c => $"{s.Name}: {c.Name}")
                        .Concat(s.Maps?.SelectMany(m => m.Checks.Where(c => !c.Pass).Select(c => $"{s.Name}/{m.Name}: {c.Name}")) ?? Enumerable.Empty<string>()))
                    .Take(8)
                    .ToList();

                Plugin.LogSource?.LogInfo(
                    $"{Tag}{report.Passed} passed, {report.Failed} failed, {report.Warnings} warning(s) - {report.Overall.ToUpperInvariant()}" +
                    (report.CancelReason != null ? $" ({report.CancelReason})" : "") +
                    $" in {report.Seconds.ToString("0", CultureInfo.InvariantCulture)} s; report {path ?? "NOT WRITTEN"}" +
                    (failedNames.Count > 0 ? "; failed: " + string.Join(" | ", failedNames) : "") + ".");
            }
            catch (Exception)
            {
                // The summary is the last thing; nothing may keep the run from ending.
            }
        }
    }
}
