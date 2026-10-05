using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Stage M3: the Maps tab's "Capture from game files" actions - one map (merge, or Replace), or every capturable map one
    /// after another - and the lines the tab shows for them: whether a capture can start and why not, the progress while it
    /// runs, and the result when it ends.
    ///
    /// A run is MenuMapHost.Start with <see cref="MapCapture.RunMenuCapture"/> as its work, hosted on
    /// UI.TrackerHotkey - the one plugin-made behaviour proven to tick in EFT's main menu (memory ddol-objects-dead-in-menu).
    /// Its end is not a callback: <see cref="Poll"/> (TrackerHotkey.Update) sees the host's slot free again and reads the
    /// run's verdict (<see cref="MenuMapHost.LastOutcome"/>, matched by claim) and the session's own (written, refused for
    /// Replace, set aside). A run that died without its finally ends the same way - the host's dead-run check frees the slot
    /// and leaves a verdict.
    ///
    /// The in-game self-test's capture (QuestGraph.SelfTest, the "-menu" test set) does not come through here.
    /// </summary>
    internal static class MenuCaptureRunner
    {
        private const string Tag = "QuestTree: menu capture: ";

        /// <summary>The run this started and has not yet seen end, or null.</summary>
        private static MapCapture.MenuSession _session;

        /// <summary>The host's claim of that run (<see cref="MenuMapHost.CurrentClaim"/> right after the start).</summary>
        private static int _claim;

        /// <summary>The display name of the map running, for the lines.</summary>
        private static string _runningName;

        private static Stopwatch _clock;

        /// <summary>Capture all: the maps still to go, in session order, with their display names.</summary>
        private static readonly Queue<(string Id, string Name)> _queue = new Queue<(string Id, string Name)>();

        private static int _queueTotal;
        private static bool _cancelled;

        /// <summary>Capture all: one short phrase per map that has ended, for the closing line.</summary>
        private static readonly List<string> _allResults = new List<string>();

        /// <summary>Bumped whenever a run starts or ends, so the Maps tab knows its buttons and lines are stale and
        /// repaints once (and only then - the progress line is updated in place).</summary>
        internal static int Version { get; private set; }

        /// <summary>True from a start until <see cref="Poll"/> has seen the run end - including between the maps of a
        /// "capture all".</summary>
        internal static bool Running => _session != null;

        /// <summary>Whether a "capture all" is in progress.</summary>
        internal static bool RunningAll => _queueTotal > 0;

        /// <summary>The location id of the map running, or null.</summary>
        internal static string RunningLocation => _session?.LocationId;

        /// <summary>The last run's result line (one map), or capture all's closing line, or null before any.</summary>
        internal static string LastResult { get; private set; }

        /// <summary>The map the last result is about (null for capture all's closing line).</summary>
        internal static string LastResultLocation { get; private set; }

        /// <summary>Whether the menu was left unproven by a run - "Restart the game before your next raid". Sticky, as the
        /// host's own advice is.</summary>
        internal static bool RestartAdvised => MenuMapHost.RestartAdvised != null;

        /// <summary>The progress line while a run is going, or null.</summary>
        internal static string Progress
        {
            get
            {
                if (_session == null) return null;

                var phase = MenuMapHost.Phase ?? "starting";
                var seconds = _clock == null ? 0d : _clock.Elapsed.TotalSeconds;
                var of = _queueTotal > 0
                    ? $"map {(_queueTotal - _queue.Count).ToString(CultureInfo.InvariantCulture)} of {_queueTotal.ToString(CultureInfo.InvariantCulture)}: "
                    : "";

                return $"{of}{_runningName}: {phase} ({Clock(seconds)})" + (MenuMapHost.StopAsked ? " - stopping" : "");
            }
        }

        /// <summary>Whether a capture from game files may start now, and why not - the Maps tab greys its buttons with
        /// this. The main menu only (no raid, no hideout, no GameWorld at all), no menu run and no capture going, no
        /// earlier run that advised a restart, a menu host to run on, and - the cheap half of "its preset resolves" - a
        /// Scene key for the map. Never throws.</summary>
        /// <param name="locationId">The selected map's location id, or null to ask only about the game's state (capture
        /// all).</param>
        /// <param name="why">Why not, in a few words.</param>
        internal static bool CanStart(string locationId, out string why)
        {
            why = null;

            try
            {
                if (Running) why = $"a capture from game files is running ({_runningName})";
                else if (MenuMapHost.Busy) why = "the menu map host is busy (another run is going: the self-test or the map data probe)";
                else if (MapCapture.IsCapturing) why = "a map capture is running";
                else if (MenuMapHost.RestartAdvised != null) why = "an earlier capture could not restore the menu - restart the game first";
                else if (QuestTree.UI.TrackerHotkey.Current == null) why = "the tracker's menu host is not up";
                else if (MenuMapHost.WorldSet(out var world)) why = $"a GameWorld is set ({world}) - restart the game and capture from the main menu before visiting the hideout or a raid";
                else if (!MenuMapHost.InMenu(out var notMenu)) why = notMenu;
                else if (locationId != null && !MenuMapHost.HasScenes(locationId, out var noScenes)) why = noScenes;
            }
            catch (Exception ex)
            {
                why = $"the check failed ({ex.GetType().Name})";
            }

            return why == null;
        }

        /// <summary>Starts a capture of one map into its REAL key: merged into a stored menu set (or a new set), or with
        /// <paramref name="replace"/> a fresh set that replaces the stored one (moved aside first). False with the reason.</summary>
        /// <param name="locationId">The map's location id (the Maps tab's key).</param>
        /// <param name="displayName">Its name for the lines.</param>
        /// <param name="replace">Replace rather than merge.</param>
        /// <param name="refusal">Why it did not start.</param>
        internal static bool Start(string locationId, string displayName, bool replace, out string refusal)
        {
            _queue.Clear();
            _queueTotal = 0;
            _allResults.Clear();
            _cancelled = false;

            return StartOne(locationId, displayName, replace ? MapCapture.MenuWriteMode.Replace : MapCapture.MenuWriteMode.Merge,
                out refusal, out _);
        }

        /// <summary>Capture all: every location <see cref="MenuMapHost.ListCapturableLocations"/> lists (one per preset,
        /// vanilla and modded), one after another, each MERGED into its stored set or started fresh - a map whose stored
        /// set cannot take a menu capture (a raid set) is refused and listed for Replace, never replaced unasked.
        /// <see cref="Cancel"/> stops the map running and drops the rest. False with the reason.</summary>
        /// <param name="refusal">Why it did not start.</param>
        internal static bool StartAll(out string refusal)
        {
            refusal = null;

            if (!CanStart(null, out refusal)) return false;

            var maps = MenuMapHost.ListCapturableLocations();
            if (maps.Count == 0)
            {
                refusal = "the session lists no map with scenes";
                return false;
            }

            _queue.Clear();
            _allResults.Clear();
            _cancelled = false;

            foreach (var map in maps) _queue.Enqueue((map.Id, map.Name));
            _queueTotal = _queue.Count;

            Plugin.LogSource?.LogInfo(
                $"{Tag}capture all - {_queueTotal.ToString(CultureInfo.InvariantCulture)} map(s), one after another: " +
                string.Join("; ", maps.Select(m => m.Describe())) + ".");

            if (StartNext()) return true;

            refusal = _allResults.LastOrDefault() ?? "no map could be started";
            _queueTotal = 0;
            return false;
        }

        /// <summary>Stops the map running (its capture is disposed - nothing is written unless its write already ran - and
        /// the map is unloaded) and drops the rest of a capture all.</summary>
        internal static void Cancel()
        {
            if (!Running) return;

            _cancelled = true;
            _queue.Clear();
            MenuMapHost.RequestStop("cancelled from the Maps tab");
            Plugin.LogSource?.LogInfo($"{Tag}cancel asked for {_runningName} - the capture stops at its next step and the map is unloaded.");
            Version++;
        }

        /// <summary>Polled from TrackerHotkey.Update (which ticks in the menu): sees a run end, writes its result line,
        /// and starts a capture all's next map. Never throws.</summary>
        internal static void Poll()
        {
            try
            {
                if (_session == null || MenuMapHost.Busy) return;

                Finish();

                if (_queueTotal > 0)
                {
                    if (_cancelled || RestartAdvised || !StartNext())
                    {
                        CloseAll();
                    }
                }
            }
            catch (Exception ex)
            {
                _session = null;
                _clock = null;
                _queue.Clear();
                _queueTotal = 0;
                Plugin.LogSource?.LogWarning($"{Tag}the Maps tab's run check failed ({ex.GetType().Name}: {ex.Message}).");

                // (review) the tab must not go on showing a run that is over: a result line, and a repaint
                LastResult = $"the capture's end could not be read ({ex.GetType().Name}: {ex.Message}) - LogOutput.log's " +
                             "\"QuestTree: menu capture\" lines say what was written.";
                LastResultLocation = null;
                Version++;
                ModSettings.RequestRepaint();
            }
        }

        /// <summary>Starts capture all's next map that will start, recording a refusal for each one that will not.</summary>
        private static bool StartNext()
        {
            while (_queue.Count > 0)
            {
                var (id, name) = _queue.Dequeue();

                if (StartOne(id, name, MapCapture.MenuWriteMode.Merge, out var refusal, out var needsReplace)) return true;

                _allResults.Add(needsReplace ? $"{name}: needs Replace ({refusal})" : $"{name}: not started ({refusal})");

                // Every later map would be refused for the same reason (a raid, a restart advised).
                if (!CanStart(null, out _)) break;
            }

            return false;
        }

        /// <summary>Capture all's closing line.</summary>
        private static void CloseAll()
        {
            var skipped = _queue.Count;
            _queue.Clear();

            var line = $"Capture all: {_allResults.Count.ToString(CultureInfo.InvariantCulture)} of " +
                       $"{_queueTotal.ToString(CultureInfo.InvariantCulture)} map(s) done" +
                       (_cancelled ? " (cancelled)" : skipped > 0 ? $" ({skipped.ToString(CultureInfo.InvariantCulture)} not started)" : "") +
                       ". " + string.Join(" | ", _allResults);

            LastResult = line;
            LastResultLocation = null;
            _queueTotal = 0;
            Plugin.LogSource?.LogInfo(Tag + line);
            Version++;
            ModSettings.RequestRepaint();
        }

        /// <summary>One run's start. Main thread.</summary>
        /// <param name="locationId">The map's location id.</param>
        /// <param name="displayName">Its name for the lines.</param>
        /// <param name="write">Merge or Replace.</param>
        /// <param name="refusal">Why it did not start.</param>
        /// <param name="needsReplace">The refusal is Merge's pre-check: the stored set is not one from game files.</param>
        private static bool StartOne(string locationId, string displayName, MapCapture.MenuWriteMode write, out string refusal,
            out bool needsReplace)
        {
            refusal = null;
            needsReplace = false;

            if (!CanStart(locationId, out refusal)) return false;

            var key = MenuMapHost.LocationKey(locationId, out var noKey);
            if (key == null)
            {
                refusal = noKey;
                return false;
            }

            if (!MapCapture.IsUsableKey(key))
            {
                refusal = $"'{key}' cannot be a capture folder name";
                return false;
            }

            // (review) Merge refuses a raid set - decided from its meta now, not after loading the whole map
            if (write == MapCapture.MenuWriteMode.Merge && MapCapture.MenuReplaceMode)
            {
                var blocked = MapCapture.MergeRefusal(key);
                if (blocked != null)
                {
                    refusal = blocked;
                    needsReplace = true;

                    var name = string.IsNullOrEmpty(displayName) ? locationId : displayName;
                    LastResult = $"{name} was not captured: {blocked}. Choose \"Replace with a fresh capture\" to replace it " +
                                 "(the old set is kept as a backup). Nothing was loaded.";
                    LastResultLocation = locationId;
                    Plugin.LogSource?.LogInfo($"{Tag}{key}: {blocked} - not started; Replace is needed.");
                    Version++;
                    return false;
                }
            }

            var host = QuestTree.UI.TrackerHotkey.Current;
            var session = new MapCapture.MenuSession(locationId, key, write);

            if (!MenuMapHost.Start(host, locationId, () => MapCapture.RunMenuCapture(session), out refusal, out var claim)) return false;

            _session = session;
            _claim = claim;
            _runningName = string.IsNullOrEmpty(displayName) ? locationId : displayName;
            _clock = Stopwatch.StartNew();

            Plugin.LogSource?.LogInfo(
                $"{Tag}started from the Maps tab: '{locationId}' into {key} ({MapCapture.MenuWriteText(write)}). Do not start a " +
                "raid or open the hideout until the FINISHED line.");

            Version++;
            ModSettings.RequestRepaint();
            return true;
        }

        /// <summary>The run has ended: its result line, from the host's verdict and the session's.</summary>
        private static void Finish()
        {
            var session = _session;
            var name = _runningName;
            var seconds = _clock?.Elapsed.TotalSeconds ?? 0d;

            _session = null;
            _clock = null;

            var outcome = MenuMapHost.LastOutcome;
            if (outcome != null && outcome.Claim != _claim) outcome = null;

            string result;
            string brief;

            if (session.Written != null)
            {
                result = $"captured {name} from game files: {session.Written}{Backup(session)}";
                brief = "written";

                if (outcome?.Problem != null) result += $" - but the run then {outcome.Problem}";
            }
            else if (session.WriteFailed != null)
            {
                result = $"{name} was not captured: its write failed ({session.WriteFailed}){Backup(session)}.";
                brief = "write failed";
            }
            else if (session.NeedsReplace != null)
            {
                result = $"{name} was not captured: the stored set cannot take a capture from game files ({session.NeedsReplace}). " +
                         "Choose \"Replace with a fresh capture\" to replace it (a copy is kept).";
                brief = "needs Replace";
            }
            else if (outcome?.Problem != null)
            {
                result = $"{name} was not captured - {outcome.Problem}.";
                brief = outcome.Problem.StartsWith("stopped", StringComparison.Ordinal) ? "stopped" : "failed";
            }
            else
            {
                result = $"{name}: nothing was written" +
                         (session.Stopped != null ? $" - {session.Stopped}" : " - the log's \"QuestTree: capture\" lines say why") + ".";
                brief = "nothing written";
            }

            // (review) any other branch that ran after a Replace moved the set aside says where it is
            if (session.Written == null && session.WriteFailed == null && session.SetAside != null) result += Backup(session).TrimStart(';') + ".";

            var trouble = outcome?.Trouble ?? MenuMapHost.RestartAdvised;
            if (trouble != null) result += $" Restart the game before your next raid ({trouble}).";

            result += $" ({Clock(seconds)})";

            LastResult = result;
            LastResultLocation = session.LocationId;
            if (_queueTotal > 0) _allResults.Add($"{name}: {brief}");

            Plugin.LogSource?.LogInfo(Tag + result);
            Version++;
            ModSettings.RequestRepaint();
        }

        /// <summary>Where a Replace's old set is, truthfully: kept in its backup folder (with its size) after a write, put back
        /// after a failed one, or still in the backup when the put-back failed. "" when nothing was set aside.</summary>
        private static string Backup(MapCapture.MenuSession session)
        {
            if (session?.SetAside == null) return "";

            if (session.Written == null && session.SetAsideRestored) return "; the old set was restored";

            var mb = (session.SetAsideBytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture);
            return $"; the old set is kept in captures\\{session.SetAside} ({mb} MB)";
        }

        /// <summary>"12:34" or "1:02:03".</summary>
        private static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0d, seconds));
            return t.TotalHours >= 1d
                ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }
}
