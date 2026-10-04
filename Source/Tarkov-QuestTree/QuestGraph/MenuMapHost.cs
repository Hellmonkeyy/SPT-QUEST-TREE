using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.Settings.Graphics;
using EFT.UI.Screens;
using JsonType;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Stage M1 of the menu capture: hosts a whole raid map in the MAIN MENU. It reads the location's ScenesPreset (data,
    /// never a list in code), loads every scene of it additively, lets a caller work while they are loaded, then unloads them
    /// and puts back the global state the scenes changed. It also owns the machinery MenuSceneProbe proved in game
    /// (4d859f8): the main-menu gate, the state snapshot/restore, the DontDestroyOnLoad diff, the dead-run check, the
    /// Streamer switch-off, the log-message counter and the per-scene counts - the probe now calls into this.
    ///
    /// What EFT itself does (decompiled 4.1.6, read-only): the location's <c>Scene</c> ResourceKey (server base.json, e.g.
    /// path <c>maps/customs_preset.bundle</c>, rcid <c>bigmap.scenespreset.asset</c>) names a <see cref="ScenesPreset"/>.
    /// LoadScenesFromPresetOperation.LoadPresetFromConfigAsync (:105-128) loads it with
    /// <c>Assets.Manager.LoadAssetAsync(key)</c>, and LoadPresetAsync (:130-221) loads each of its scene keys through
    /// <c>IAssetsManager.LoadScene(key.path, key.ToAssetName(), mode, ...)</c> (AssetsManagerExtension.cs:63-68,
    /// AssetsManager.cs:519 -> the scene coroutine at :363, which loads the key's bundle first, so built-in and bundled
    /// modded scenes load the same way), then its ChildPresets the same way, then sets ActiveSceneName active. The game loads
    /// the first scene Single, which would unload the menu; this host loads every scene Additive and never makes one active.
    ///
    /// Hosted on TrackerHotkey (a child of the tracker's root canvas), because a plugin-created DontDestroyOnLoad object never
    /// ticks in EFT's menu (memory: ddol-objects-dead-in-menu).
    /// </summary>
    internal static class MenuMapHost
    {
        private const string HostTag = "QuestTree: menu map host: ";

        /// <summary>The hard limit on one scene's (or the preset's) load, and on each unload and the asset sweep - the
        /// probe's per-scene timeout.</summary>
        internal const double TimeoutSeconds = 60d;

        private const int MaxDistinctMessages = 20;

        /// <summary>A run whose coroutine has not yielded for more than this many frames is dead (Unity stopped it without
        /// its finally).</summary>
        private const int DeadAfterFrames = 3;

        /// <summary>How deep ChildPresets are followed. A preset that names itself (or a loop) is cut by the seen-set; this
        /// only bounds a pathological chain.</summary>
        private const int MaxPresetDepth = 8;

        /// <summary>Scenes whose name (or rcid) ends in one of these are not loaded: audio-only and culling-bake scenes
        /// carry nothing a map capture draws, and they cost load time and memory. A suffix rule on scene names, never a map
        /// name. Rollback: an empty array loads every scene of the preset.
        ///
        /// _DesignMain is NOT skipped (play-test 2026-09-30): it holds the BorderZones and ExfiltrationPoints - 0 of 5 and
        /// 0 of 29 on Customs without it - which the capture's labels and MapExtentProbe's fallback extent read.</summary>
        internal static readonly string[] SkippedSceneSuffixes = { "_Sound", "_Culling" };

        /// <summary>Stage M3 rollback: false ignores the "Menu capture: skip scenes ending in" setting and skips exactly
        /// <see cref="SkippedSceneSuffixes"/>, as stage M1 did.</summary>
        internal static readonly bool SkipSuffixesFromSettings = true;

        /// <summary>Stage M3: the suffixes a run skips - the setting's comma list (ModSettings.MenuCaptureSkipSuffixes),
        /// trimmed, empty items dropped, so an empty setting skips nothing; <see cref="SkippedSceneSuffixes"/> when the
        /// settings are not bound or <see cref="SkipSuffixesFromSettings"/> is off. Read once per resolve, so a change
        /// takes effect at the next run. A suffix rule on scene names, never a map name.</summary>
        internal static string[] SkipSuffixes()
        {
            try
            {
                if (!SkipSuffixesFromSettings || !ModSettings.Ready || ModSettings.MenuCaptureSkipSuffixes == null)
                    return SkippedSceneSuffixes;

                return (ModSettings.MenuCaptureSkipSuffixes.Value ?? "")
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception)
            {
                // A setting that cannot be read is the M1 behaviour, never "load every audio scene" by accident.
                return SkippedSceneSuffixes;
            }
        }

        /// <summary>Always loaded whatever <see cref="SkippedSceneSuffixes"/> says: the NavMesh lives only in the _AI scene,
        /// and the capture's extent and floors come from it.</summary>
        internal static readonly string[] KeptSceneSuffixes = { "_AI" };

        // --- the run slot ------------------------------------------------------------------------------------------------
        // One menu, one run: the probe and the host share this slot, so they never load scenes over each other, and the
        // dead-run check below covers whichever owns it.

        private static int _claim;
        private static int _nextClaim;
        private static MonoBehaviour _runHost;
        private static bool _hostKnown;
        private static bool _waitingOnInstruction;
        private static Action<string, int> _onDead;
        private static int _handledFrame = -1;
        private static bool _warned;

        /// <summary>The frame the run's coroutine last yielded on - see <see cref="DeadAfterFrames"/>.</summary>
        private static int _lastTick;

        /// <summary>Whose lines the shared helpers write: the owner's tag while a run holds the slot, the host's otherwise,
        /// so the probe's log reads exactly as it did before the move.</summary>
        private static string _voice = HostTag;

        /// <summary>Sticky for the session: an earlier run (probe or host) could not prove it left the menu as it found it.</summary>
        internal static string RestartAdvised;

        /// <summary>True while the probe or the host has scenes loading, loaded or unloading.</summary>
        internal static bool Busy => _claim != 0;

        /// <summary>Stage M3: the claim of the run holding the slot, 0 when none - so a caller that started a run can match
        /// <see cref="LastOutcome"/> to it.</summary>
        internal static int CurrentClaim => _claim;

        /// <summary>Stage M3: what the run in progress is doing, in a few words ("loading scene 3/26 'x'", "floor 1/2",
        /// "unloading"), for the Maps tab's progress line; null while no run holds the slot. Main thread.</summary>
        internal static string Phase { get; private set; }

        /// <summary>Stage M3: sets <see cref="Phase"/> while a run holds the slot - the host's own steps and the capture's
        /// (MapCapture's menu phase hook) both say it here. A no-op outside a run.</summary>
        /// <param name="text">A few words.</param>
        internal static void SetPhase(string text)
        {
            if (_claim != 0) Phase = text;
        }

        /// <summary>Stage M3: why the run was asked to stop (the Maps tab's cancel), or null. Read by the load loop and the
        /// work driver, which turn it into the run's abort - the same path as a raid starting or the work's cap - so the
        /// capture's own finally runs and every hosted scene is unloaded.</summary>
        private static string _stopAsked;

        /// <summary>Stage M3: asks the run holding the slot to stop at its next step: loading ends and the map is
        /// unloaded, or the capture is disposed (nothing is written unless its write already ran) and then unloaded. A
        /// no-op with no run.</summary>
        /// <param name="why">For the FINISHED line.</param>
        internal static void RequestStop(string why)
        {
            if (_claim != 0 && _stopAsked == null) _stopAsked = string.IsNullOrEmpty(why) ? "asked to stop" : why;
        }

        /// <summary>Stage M3: whether the run holding the slot has been asked to stop.</summary>
        internal static bool StopAsked => _claim != 0 && _stopAsked != null;

        /// <summary>Stage M3: one finished run's verdict, as its FINISHED line says it.</summary>
        internal sealed class RunOutcome
        {
            /// <summary>The claim of the run (<see cref="CurrentClaim"/> while it ran).</summary>
            internal int Claim;

            internal string LocationId;

            /// <summary>Why it was refused, stopped, or its work failed; null when it loaded, worked and unloaded.</summary>
            internal string Problem;

            /// <summary>The first thing that left the menu unproven - a restart is advised - or null when the state
            /// matched.</summary>
            internal string Trouble;

            internal double Seconds;
        }

        /// <summary>Stage M3: the last finished run's verdict (set in its finally, or by the dead-run check), or null.</summary>
        internal static RunOutcome LastOutcome { get; private set; }

        /// <summary>Takes the run slot for <paramref name="tag"/>'s owner. 0 when it is taken. <paramref name="host"/> is the
        /// behaviour running the coroutine (null when unknown: then only a stalled yield marks the run dead);
        /// <paramref name="onDead"/> is told the host's state and the frames since the last yield when the run died without
        /// its cleanup, after the slot is released and the log capture stopped.</summary>
        internal static int TryClaim(string tag, MonoBehaviour host, Action<string, int> onDead)
        {
            if (_claim != 0) return 0;

            _claim = ++_nextClaim;
            if (_claim == 0) _claim = ++_nextClaim;
            _runHost = host;
            _hostKnown = host != null;
            _waitingOnInstruction = false;
            _onDead = onDead;
            _voice = tag ?? HostTag;
            _lastTick = Time.frameCount;
            _ddolBefore = null;
            _stopAsked = null;
            Phase = "starting";
            return _claim;
        }

        /// <summary>Gives the slot back, only if <paramref name="claim"/> still holds it (a run found dead and replaced must
        /// not free its successor's slot from a late finally).</summary>
        internal static void Release(int claim)
        {
            if (claim == 0 || claim != _claim) return;
            ReleaseSlot();
        }

        private static void ReleaseSlot()
        {
            Phase = null;
            _stopAsked = null;
            _claim = 0;
            _runHost = null;
            _hostKnown = false;
            _waitingOnInstruction = false;
            _onDead = null;
            _voice = HostTag;
        }

        /// <summary>Every yield of a run goes through this, so the poll can tell a live run from a dead one.</summary>
        internal static object Tick()
        {
            _lastTick = Time.frameCount;
            return null;
        }

        /// <summary>Polled from TrackerHotkey.Update, which is proven to tick in the menu. Unity stops a coroutine whose host
        /// is destroyed WITHOUT running its finally, so a menu rebuilt mid-run would leave scenes loaded and the slot taken
        /// for the session; a coroutine that stopped yielding is dead the same way, whatever killed it. Never throws.</summary>
        internal static void PollFromHotkey(MonoBehaviour host)
        {
            if (_claim == 0 || host == null || _handledFrame == Time.frameCount) return;
            _handledFrame = Time.frameCount;

            try
            {
                var hostGone = _hostKnown && (_runHost == null || !_runHost.isActiveAndEnabled);

                // While the caller's work waits on a Unity yield instruction (a WaitForSeconds, an AsyncOperation) the
                // coroutine legitimately skips frames, so only the host test applies then.
                var stalled = !_waitingOnInstruction && Time.frameCount - _lastTick > DeadAfterFrames;
                if (!hostGone && !stalled) return;

                var state = !_hostKnown ? "unknown" : _runHost == null ? "destroyed" : _runHost.isActiveAndEnabled ? "alive" : "inactive";
                var frames = Time.frameCount - _lastTick;
                var onDead = _onDead;

                // Stage M3: the Maps tab waits on this run's verdict - a dead run has one too.
                LastOutcome = new RunOutcome
                {
                    Claim = _claim,
                    LocationId = _current?.LocationId,
                    Problem = $"its coroutine died (host {state}) and its scenes were unloaded in an emergency",
                    Trouble = "the run died without its cleanup",
                };

                ReleaseSlot();
                StopCapture();
                onDead?.Invoke(state, frames);
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"{HostTag}the dead-run check failed ({ex.GetType().Name}: {ex.Message}) at {FirstFrame(ex.StackTrace)}");
            }
        }

        // --- the locations -----------------------------------------------------------------------------------------------

        /// <summary>One location the session knows with a Scene key - a candidate for a menu capture. Listing does not
        /// resolve its preset (that loads a bundle); <see cref="ResolveScenes"/> does.</summary>
        internal sealed class CapturableLocation
        {
            /// <summary>The id to host it by: the first enabled location sharing the preset, else the first.</summary>
            internal string Id;
            internal string Name;

            /// <summary>True when any location sharing the preset is enabled on the server.</summary>
            internal bool Enabled;
            internal ResourceKey Scene;

            /// <summary>Every location id that names this preset (an event copy of a map can share one), in session order.</summary>
            internal readonly List<string> Ids = new List<string>();

            /// <summary>The ids among <see cref="Ids"/> the server has disabled - listed, not dropped: a disabled location's
            /// scenes still load, and its map may still be wanted.</summary>
            internal readonly List<string> DisabledIds = new List<string>();

            /// <summary>"bigmap 'Customs'", with any other ids and "(disabled)" marks.</summary>
            internal string Describe() =>
                $"{Id} '{Name}'" + (Enabled ? "" : " (disabled)") +
                (Ids.Count > 1
                    ? $" [shared by {string.Join(", ", Ids.Select(i => DisabledIds.Contains(i) ? i + " (disabled)" : i))}]"
                    : "");
        }

        /// <summary>Every preset the client session's locations name, vanilla and modded, once each - deduplicated by the
        /// Scene key's (path, rcid), keeping every location id that shares it - in the session's order, with its display
        /// name. Disabled locations are listed and marked. The hideout is left out: its preset is the hideout, which the menu
        /// gate refuses. Resolves no preset (that loads a bundle). Empty when there is no session yet. Never throws.</summary>
        internal static List<CapturableLocation> ListCapturableLocations()
        {
            var result = new List<CapturableLocation>();

            try
            {
                var locations = SessionLocations();
                if (locations == null) return result;

                var byPreset = new Dictionary<string, CapturableLocation>(StringComparer.OrdinalIgnoreCase);

                foreach (var location in locations.Values)
                {
                    if (location == null || location.IsHideout || !HasSceneKey(location.Scene)) continue;

                    // The assets manager lowercases both halves (AssetsManager.cs:527), so case never makes two presets.
                    var presetKey = (location.Scene.path ?? "") + "|" + (location.Scene.rcid ?? "");

                    if (!byPreset.TryGetValue(presetKey, out var entry))
                    {
                        entry = new CapturableLocation
                        {
                            Id = location.Id,
                            Name = DisplayName(location),
                            Enabled = location.Enabled,
                            Scene = location.Scene,
                        };
                        byPreset[presetKey] = entry;
                        result.Add(entry);
                    }
                    else if (location.Enabled && !entry.Enabled)
                    {
                        // Host by an enabled id when one shares the preset.
                        entry.Id = location.Id;
                        entry.Name = DisplayName(location);
                        entry.Enabled = true;
                    }

                    entry.Ids.Add(location.Id);
                    if (!location.Enabled) entry.DisabledIds.Add(location.Id);
                }
            }
            catch (Exception ex)
            {
                Log($"listing the session's locations failed ({ex.GetType().Name}: {ex.Message}).");
            }

            return result;
        }

        /// <summary>The session's locations, the way the game reads them itself
        /// (ClientTransitController.TryGetLocation: Singleton&lt;ClientApplication&lt;IEftSession&gt;&gt; -&gt;
        /// GetClientBackEndSession().LocationSettings.locations). Null when any link is missing.</summary>
        private static Dictionary<string, LocationSettings.Location> SessionLocations()
        {
            if (!Singleton<ClientApplication<IEftSession>>.Instantiated) return null;
            var session = Singleton<ClientApplication<IEftSession>>.Instance?.GetClientBackEndSession();
            return session?.LocationSettings?.locations;
        }

        /// <summary>The location whose dictionary key, Id or _Id is <paramref name="locationId"/> - exact first, then ignoring
        /// case, so "bigmap" and "Interchange" both work as typed. Null with the reason otherwise.</summary>
        private static LocationSettings.Location FindLocation(string locationId, out string why)
        {
            why = null;
            var locations = SessionLocations();

            if (locations == null)
            {
                why = "there is no client session with a location list yet";
                return null;
            }

            if (locations.TryGetValue(locationId, out var byKey) && byKey != null) return byKey;

            var exact = locations.Values.FirstOrDefault(l => l != null && (l.Id == locationId || l._Id == locationId));
            if (exact != null) return exact;

            var loose = locations.Values
                .Where(l => l != null && (string.Equals(l.Id, locationId, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(l._Id, locationId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (loose.Count == 1) return loose[0];

            why = loose.Count > 1
                ? $"'{locationId}' matches {loose.Count} locations ignoring case - type the id exactly"
                : $"no location '{locationId}' in the session (capturable: {string.Join("; ", ListCapturableLocations().Select(c => c.Describe()))})";
            return null;
        }

        private static bool HasSceneKey(ResourceKey key) =>
            key != null && (!string.IsNullOrEmpty(key.path) || !string.IsNullOrEmpty(key.rcid));

        /// <summary>The localised name the game shows (LocationSettings.Location.LocalizedName), else the base.json Name,
        /// else the id.</summary>
        private static string DisplayName(LocationSettings.Location location)
        {
            try
            {
                var localized = location.LocalizedName;
                if (!string.IsNullOrEmpty(localized) && !localized.EndsWith(" Name", StringComparison.Ordinal)) return localized;
            }
            catch (Exception)
            {
                // No locale yet: fall through to the raw name.
            }

            return !string.IsNullOrEmpty(location.Name) ? location.Name : location.Id;
        }

        // --- the scene list ----------------------------------------------------------------------------------------------

        /// <summary>One scene key of a preset, in load order.</summary>
        internal sealed class SceneEntry
        {
            internal SceneResourceKey Key;

            /// <summary>The Unity scene name: what the game's own load passes to SceneManager (the asset name's file name
            /// without extension, AssetsManager.cs:366).</summary>
            internal string Name;

            /// <summary>The preset this key came from (the location's, or a child's).</summary>
            internal string Preset;

            /// <summary>Why it is not loaded, or null when it is.</summary>
            internal string Skip;

            /// <summary>Set once it is loaded.</summary>
            internal Scene Scene;
        }

        /// <summary>What <see cref="ResolveScenes"/> found for a location.</summary>
        internal sealed class ScenePlan
        {
            internal string LocationId;
            internal string LocationName;
            internal string Bundle;
            internal string Rcid;
            internal string ActiveSceneName;

            /// <summary>Every key of the preset and its children, in load order, kept and skipped.</summary>
            internal readonly List<SceneEntry> All = new List<SceneEntry>();

            /// <summary>Why the location cannot be hosted, or null.</summary>
            internal string Refusal;

            /// <summary>Stage M3: the suffixes this resolve skipped (<see cref="SkipSuffixes"/>, read once).</summary>
            internal string[] Skip = SkippedSceneSuffixes;

            internal List<SceneEntry> Kept => All.Where(e => e.Skip == null).ToList();
        }

        /// <summary>Resolves <paramref name="locationId"/>'s scenes into <paramref name="plan"/>: takes the location's Scene
        /// key from the client session, loads its ScenesPreset BY ITS rcid through <c>Assets.Manager.LoadAssetAsync(key)</c>
        /// (AssetsManager.cs:519-523: the asset named <c>key.ToAssetName()</c>, which is the rcid when it is set, in the
        /// bundle <c>key.path</c> - never "the first asset" of the bundle, because the Customs bundle also holds a stray
        /// Lighthouse editor preset), and reads its scene keys in order, then each ChildPreset's, and its active scene name.
        /// Skips by <see cref="SkippedSceneSuffixes"/>, keeps <see cref="KeptSceneSuffixes"/>, and logs the list. A failure is
        /// <see cref="ScenePlan.Refusal"/>. Each yield goes through <see cref="Tick"/>.</summary>
        internal static IEnumerator ResolveScenes(string locationId, ScenePlan plan)
        {
            plan.LocationId = locationId;
            LocationSettings.Location location = null;
            string why = null;

            if (string.IsNullOrWhiteSpace(locationId))
            {
                plan.Refusal = "no location id was given";
                yield break;
            }

            if (!Try("finding the location", () => location = FindLocation(locationId, out why)) || location == null)
            {
                plan.Refusal = why ?? "finding the location threw (logged above)";
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            plan.LocationName = DisplayName(location);
            var key = location.Scene;

            if (location.IsHideout) plan.Refusal = "it is the hideout, which the menu host never loads";
            else if (!HasSceneKey(key)) plan.Refusal = "its Scene key is empty (the location has no scenes the client can load)";
            else if (string.IsNullOrEmpty(key.rcid)) plan.Refusal = $"its Scene key names the bundle '{key.path}' but no rcid, so its preset cannot be picked by name";

            if (plan.Refusal != null)
            {
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            plan.Bundle = key.path;
            plan.Rcid = key.rcid;
            plan.Skip = SkipSuffixes();

            IOperation<object> op = null;
            if (!Try($"LoadAssetAsync('{key.path}', '{key.rcid}')", () => op = EFT.Assets.Manager.LoadAssetAsync(key)) || op == null)
            {
                plan.Refusal = $"the assets manager gave no operation for its preset '{key.rcid}' in '{key.path}'";
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            var clock = Stopwatch.StartNew();
            while (!op.Completed)
            {
                if (clock.Elapsed.TotalSeconds > TimeoutSeconds)
                {
                    // A modded map's bundle may be fetched from the server on first use; one that never arrives in the menu
                    // is this refusal.
                    plan.Refusal = $"its preset '{key.rcid}' in '{key.path}' did not load in {TimeoutSeconds:0} s";
                    Log($"{locationId}: refused - {plan.Refusal}.");
                    yield break;
                }

                yield return Tick();
            }

            if (op.Failed)
            {
                plan.Refusal = $"its preset '{key.rcid}' in '{key.path}' did not load: {op.Error}";
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            ScenesPreset preset = null;
            Try("reading the preset", () => preset = op.Result as ScenesPreset);

            if (preset == null)
            {
                plan.Refusal = $"'{key.rcid}' in '{key.path}' loaded as {(op.Result == null ? "nothing" : op.Result.GetType().Name)}, not a ScenesPreset";
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            if (!Try("walking the preset", () => Walk(preset, plan, new HashSet<ScenesPreset>(), 0)))
            {
                plan.Refusal = $"its preset '{key.rcid}' could not be read (logged above)";
                yield break;
            }

            if (plan.All.Count == 0)
            {
                plan.Refusal = $"its preset '{key.rcid}' lists no scenes";
                Log($"{locationId}: refused - {plan.Refusal}.");
                yield break;
            }

            var kept = plan.All.Count(e => e.Skip == null);
            Log($"{locationId} preset '{key.rcid}' - {plan.All.Count} scene(s): " +
                string.Join(", ", plan.All.Select((e, i) =>
                    $"{i + 1} {e.Name} ({(e.Skip == null ? "kept" : "skipped: " + e.Skip)}{(e.Key.onlyOffline ? ", onlyOffline" : "")})")) +
                $". {kept} kept, {plan.All.Count - kept} skipped (suffixes: {(plan.Skip.Length == 0 ? "none" : string.Join(", ", plan.Skip))}); " +
                $"active scene '{plan.ActiveSceneName}'; " +
                $"'{plan.LocationName}' from '{key.path}' in {clock.ElapsedMilliseconds} ms.");

            // Where each scene lives: a scene loaded from a bundle keeps that bundle loaded after the scene unloads (the
            // assets manager holds it), which matters for a modded map's multi-GB scene bundle.
            Log($"{locationId}: scene key paths: " + string.Join(", ", plan.All
                .GroupBy(e => string.IsNullOrEmpty(e.Key.path) ? "(none)" : e.Key.path)
                .Select(g => $"'{g.Key}' x{g.Count()}")) + ".");

            if (kept == 0) plan.Refusal = "every scene of its preset is skipped";
            else if (!plan.All.Any(e => e.Skip == null && KeptSceneSuffixes.Any(s => e.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))))
                Log($"{locationId}: no scene ends in {string.Join("/", KeptSceneSuffixes)} - the map will come without a NavMesh.");
        }

        /// <summary>Adds <paramref name="preset"/>'s scene keys in order, then each child's, the way LoadPresetAsync walks
        /// them (the raw key list, not ScenesResourceKeys, whose onlyOffline filter depends on the last raid's
        /// DisableServerScenes; SPT's local raid passes false, i.e. loads them all - TarkovApplication.cs:2462).</summary>
        private static void Walk(ScenesPreset preset, ScenePlan plan, HashSet<ScenesPreset> seen, int depth)
        {
            if (preset == null) return;

            if (depth > MaxPresetDepth)
            {
                Log($"{plan.LocationId}: child preset '{preset.name}' is deeper than {MaxPresetDepth} levels - cut, its scenes are not listed.");
                return;
            }

            if (!seen.Add(preset)) return;

            if (depth == 0) plan.ActiveSceneName = preset.ActiveSceneName;

            var names = new HashSet<string>(plan.All.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var key in preset._scenesResourceKeys ?? new List<SceneResourceKey>())
            {
                if (key == null) continue;

                var assetName = key.ToAssetName() ?? "";
                var entry = new SceneEntry
                {
                    Key = key,
                    Name = Path.GetFileNameWithoutExtension(assetName),
                    Preset = preset.name,
                };

                if (string.IsNullOrEmpty(entry.Name)) entry.Skip = "no scene name";
                else if (!names.Add(entry.Name)) entry.Skip = "listed twice";
                else entry.Skip = SuffixSkip(entry.Name, key.rcid, plan.Skip);

                plan.All.Add(entry);
            }

            foreach (var child in preset.ChildPresets ?? new ScenesPreset[0])
                Walk(child, plan, seen, depth + 1);
        }

        /// <summary>Why a scene is skipped by its suffix, or null. Tested on the scene name and on the rcid without its
        /// extension; a kept suffix wins.</summary>
        private static string SuffixSkip(string name, string rcid, string[] skip)
        {
            var candidates = new[] { name, Path.GetFileNameWithoutExtension(rcid ?? "") ?? "" };

            if (KeptSceneSuffixes.Any(s => candidates.Any(c => c.EndsWith(s, StringComparison.OrdinalIgnoreCase)))) return null;

            var hit = (skip ?? SkippedSceneSuffixes).FirstOrDefault(s => candidates.Any(c => c.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            return hit == null ? null : $"ends in {hit}";
        }

        // --- the run -----------------------------------------------------------------------------------------------------

        /// <summary>How long a scene's bundle phase may take (the assets manager loading the scene's bundle, before Unity's
        /// scene load exists and reports progress). A modded map's multi-GB scene bundle can take minutes from a slow disk;
        /// once the scene load exists, only a stall of <see cref="TimeoutSeconds"/> ends the wait.</summary>
        internal const double BundleLoadCapSeconds = 600d;

        /// <summary>The wall-clock cap on the caller's work while the map is hosted. Past it the work is stopped (its finally
        /// blocks run) and the map is unloaded, so a wedged capture never leaves the menu holding a map.
        ///
        /// Stage M2b: never under the menu capture's own worst case (MapCapture.MenuWorstCaseSeconds, 3438 s with the menu
        /// budgets) plus <see cref="WhileLoadedMarginSeconds"/> for the wake and its restore - 3738 s today - so the host
        /// never cuts a capture its own caps would have let finish. 1800 s stays the floor.</summary>
        internal static readonly double WhileLoadedCapSeconds =
            Math.Max(1800d, MapCapture.MenuWorstCaseSeconds + WhileLoadedMarginSeconds);

        /// <summary>Stage M2b: the seconds the whileLoaded cap allows over the capture's worst case - the wake's survey and
        /// switches, the directional lights, and the wake's restore (Customs: under 1 s together).</summary>
        private const double WhileLoadedMarginSeconds = 300d;

        /// <summary>How many times the unload goes round for scenes that appeared during it before it gives up and says so.</summary>
        internal const int MaxUnloadPasses = 8;

        /// <summary>A scene that appeared during the run: what the unload takes down.</summary>
        internal sealed class Hosted
        {
            internal Scene Scene;

            /// <summary>Taken when it appeared: an unloaded Scene no longer has a name.</summary>
            internal string Name;

            /// <summary>The preset entry it is the load of, or null when nothing asked for it (streamed in, or loaded by a
            /// script of the hosted scenes).</summary>
            internal SceneEntry Entry;
        }

        /// <summary>One run's state: the baseline, what is loaded, and what went wrong.</summary>
        internal sealed class RunContext
        {
            internal string LocationId;
            internal MenuState Before;

            /// <summary>Why the location was refused (its preset or a scene failed); the loaded scenes are then unloaded.</summary>
            internal string Refusal;

            /// <summary>Why loading or the work stopped for a reason outside the location (a raid starting, the menu left, the
            /// work's cap).</summary>
            internal string Abort;

            /// <summary>What the caller's work (<c>whileLoaded</c>) reported by throwing.</summary>
            internal string WorkFailure;

            /// <summary>Anything that leaves the menu unproven - a restart is advised.</summary>
            internal readonly List<string> Trouble = new List<string>();

            /// <summary>Every scene load started, with its operation - each is waited for before the unload, never
            /// abandoned while it progresses.</summary>
            internal readonly Dictionary<SceneEntry, LoadSceneOperation> Operations = new Dictionary<SceneEntry, LoadSceneOperation>();

            /// <summary>The handles of the scenes loaded when the run began: never ours to unload.</summary>
            internal HashSet<int> BaselineHandles;

            /// <summary>Every scene that appeared during the run (SceneManager.sceneLoaded, plus a before/after diff), in order
            /// of appearance, not yet seen unloaded. The unload takes down exactly this set - not names derived from rcids,
            /// which a scene's own name need not match.</summary>
            internal readonly List<Hosted> Appeared = new List<Hosted>();

            /// <summary>The caller's work while it runs, innermost on top - disposed top first on every exit.</summary>
            internal readonly Stack<IEnumerator> Work = new Stack<IEnumerator>();
        }

        /// <summary>The run in progress, for the sceneLoaded handler and an emergency unload when its coroutine dies.</summary>
        private static RunContext _current;

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                NoteAppeared(_current, scene);
            }
            catch (Exception)
            {
                // A throwing sceneLoaded handler would break the game's own; the diff catches the scene later.
            }
        }

        private static void NoteAppeared(RunContext ctx, Scene scene)
        {
            if (ctx == null || !scene.IsValid()) return;
            if (ctx.BaselineHandles != null && ctx.BaselineHandles.Contains(scene.handle)) return;
            if (ctx.Appeared.Any(h => h.Scene.handle == scene.handle)) return;
            ctx.Appeared.Add(new Hosted { Scene = scene, Name = scene.name });
        }

        /// <summary>The before/after diff: every loaded scene not there when the run began, in case one arrived without a
        /// sceneLoaded call.</summary>
        private static void DiffScenes(RunContext ctx)
        {
            if (ctx == null) return;
            for (var i = 0; i < SceneManager.sceneCount; i++) NoteAppeared(ctx, SceneManager.GetSceneAt(i));
        }

        /// <summary>Disposes the caller's work, innermost first, so its own finally blocks run. Never throws.</summary>
        private static void DisposeWork(RunContext ctx)
        {
            _waitingOnInstruction = false;
            if (ctx == null) return;

            while (ctx.Work.Count > 0)
            {
                var e = ctx.Work.Pop();
                try
                {
                    (e as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    _ownFailures++;
                    Plugin.LogSource?.LogWarning($"{_voice}disposing the work while loaded threw {ex.GetType().Name}: {ex.Message} at {FirstFrame(ex.StackTrace)}");
                }
            }
        }

        /// <summary>Watches one scene load: the bundle phase (no Unity operation yet) may take <see cref="BundleLoadCapSeconds"/>;
        /// after that the load only expires when its progress has not moved for <see cref="TimeoutSeconds"/>.</summary>
        private sealed class LoadWatch
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly Stopwatch _still = Stopwatch.StartNew();
            private float _last = -1f;
            private bool _sceneStarted;

            internal long ElapsedMs => _clock.ElapsedMilliseconds;

            /// <summary>Why the load is given up, or null while it may still finish.</summary>
            internal string Expired(LoadSceneOperation op)
            {
                var async = op.AsyncOperation;

                if (async == null)
                {
                    return _clock.Elapsed.TotalSeconds > BundleLoadCapSeconds
                        ? $"its bundle phase did not end in {BundleLoadCapSeconds:0} s"
                        : null;
                }

                var progress = async.progress;
                if (!_sceneStarted || progress != _last)
                {
                    _sceneStarted = true;
                    _last = progress;
                    _still.Restart();
                    return null;
                }

                return _still.Elapsed.TotalSeconds > TimeoutSeconds
                    ? $"its progress stalled at {progress.ToString("0.00", CultureInfo.InvariantCulture)} for {TimeoutSeconds:0} s"
                    : null;
            }
        }

        /// <summary>Takes the slot and starts <see cref="Run"/> on <paramref name="host"/>. False, with the reason, when a run
        /// is going, the menu gate refuses, an earlier run advised a restart, or the coroutine cannot start.</summary>
        internal static bool Start(MonoBehaviour host, string locationId, Func<IEnumerator> whileLoaded, out string refusal) =>
            Start(host, locationId, whileLoaded, out refusal, out _);

        /// <summary>Stage M3 (review): <see cref="Start(MonoBehaviour, string, Func{IEnumerator}, out string)"/>, also handing
        /// back the run's claim - taken here, so a caller never reads <see cref="CurrentClaim"/> after a start whose run may
        /// already have changed it. 0 when it did not start.</summary>
        internal static bool Start(MonoBehaviour host, string locationId, Func<IEnumerator> whileLoaded, out string refusal,
            out int startedClaim)
        {
            refusal = null;
            startedClaim = 0;

            if (host == null) refusal = "no behaviour to run on";
            else if (Busy) refusal = "a run is still going";
            else if (!InMenu(out var why)) refusal = $"{why}. It runs in the main menu only";
            else if (RestartAdvised != null) refusal = $"an earlier run could not restore the menu ({RestartAdvised}). RESTART THE GAME first";
            else if (whileLoaded != null && WorldSet(out var world)) refusal = WorkRefusal(world);

            if (refusal != null) return false;

            var claim = TryClaim(HostTag, host, OnDead);
            if (claim == 0)
            {
                refusal = "a run is still going";
                return false;
            }

            try
            {
                startedClaim = claim;
                host.StartCoroutine(Body(claim, locationId, whileLoaded));
                return true;
            }
            catch (Exception ex)
            {
                startedClaim = 0;
                Release(claim);
                StopCapture();
                refusal = $"its coroutine could not start on {host.GetType().Name} ({ex.GetType().Name}: {ex.Message})";
                return false;
            }
        }

        /// <summary>Whether any GameWorld is the singleton - a raid's, or a HideoutGameWorld outliving a hideout visit. The
        /// menu gate allows the latter for a bare host run, but work while loaded (a capture) must not start: the hosted
        /// scenes' lamps and windows register into a live world as they load and wake, before the capture could refuse.
        /// Checked before anything loads. A test that throws counts as set.</summary>
        internal static bool WorldSet(out string world)
        {
            world = null;

            try
            {
                var instance = Singleton<GameWorld>.Instance;
                if (instance == null) return false;

                world = instance.GetType().Name;
                return true;
            }
            catch (Exception ex)
            {
                world = $"unknown ({ex.GetType().Name})";
                return true;
            }
        }

        /// <summary>The refusal for work while loaded under a GameWorld.</summary>
        internal static string WorkRefusal(string world) =>
            $"a GameWorld is set ({world}) - a menu capture runs only before the hideout or a raid has been visited; restart " +
            "the game and capture from the main menu";

        /// <summary>Hosts <paramref name="locationId"/>'s map in the main menu: resolves its scenes, loads them all additively
        /// one at a time, runs <paramref name="whileLoaded"/> (may be null; phase M2's capture; capped at
        /// <see cref="WhileLoadedCapSeconds"/>) while they are loaded, then unloads every scene that appeared during the run
        /// in reverse, sweeps unused assets, collects, restores the global state and compares it with the baseline. A location
        /// whose preset or any scene fails to load is refused - what did load is unloaded - with the reason on the FINISHED
        /// line. Takes the run slot itself when not started through Start (then a dead run is only seen by its
        /// stalled yields).</summary>
        internal static IEnumerator Run(string locationId, Func<IEnumerator> whileLoaded) => Body(0, locationId, whileLoaded);

        private static IEnumerator Body(int claim, string locationId, Func<IEnumerator> whileLoaded)
        {
            if (claim == 0)
            {
                claim = TryClaim(HostTag, null, OnDead);
                if (claim == 0)
                {
                    Log($"{locationId}: refused - a run is still going.");
                    yield break;
                }
            }

            var total = Stopwatch.StartNew();
            var ctx = new RunContext { LocationId = locationId };
            var plan = new ScenePlan();
            var completed = false;
            _current = ctx;

            // The scenes already loaded are the menu's; anything that appears from here on is the run's to unload.
            ctx.BaselineHandles = new HashSet<int>();
            for (var i = 0; i < SceneManager.sceneCount; i++) ctx.BaselineHandles.Add(SceneManager.GetSceneAt(i).handle);
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            StartCapture();

            try
            {
                Log($"{locationId}: hosting its map in the main menu. Do not click anything until the FINISHED line.");

                if (!InMenu(out var why)) ctx.Refusal = $"{why} - it runs in the main menu only";
                else if (RestartAdvised != null) ctx.Refusal = $"an earlier run could not restore the menu ({RestartAdvised}) - RESTART THE GAME first";
                else if (whileLoaded != null && WorldSet(out var world)) ctx.Refusal = WorkRefusal(world);

                if (ctx.Refusal == null)
                {
                    Try("the DontDestroyOnLoad baseline", () => Log($"DontDestroyOnLoad roots before: {TakeDdolBaseline()}."));

                    Try("the baseline", () =>
                    {
                        ctx.Before = MenuState.Take();
                        Log($"baseline: {ctx.Before.Describe()}");
                        Log($"baseline memory: {Memory()}; {VramProbe.Text()}");
                    });

                    SetPhase("reading the map's scene list");
                    var resolve = ResolveScenes(locationId, plan);
                    while (resolve.MoveNext()) yield return resolve.Current;

                    if (plan.Refusal != null) ctx.Refusal = plan.Refusal;
                }

                if (ctx.Refusal == null)
                {
                    var load = LoadAll(plan.Kept, ctx);
                    while (load.MoveNext()) yield return load.Current;
                }

                if (ctx.Refusal == null && ctx.Abort == null && whileLoaded != null)
                {
                    SetPhase("capturing");
                    var work = Drive(whileLoaded, ctx);
                    while (work.MoveNext()) yield return work.Current;
                }

                // A run refused at the gate took no baseline and loaded nothing: no sweep, restore or comparison (a raid being
                // the reason would otherwise read as "a raid started during the run").
                if (ctx.Before != null || ctx.Operations.Count > 0)
                {
                    SetPhase("unloading the map's scenes");
                    var unload = UnloadAll(ctx);
                    while (unload.MoveNext()) yield return unload.Current;
                }

                completed = true;
            }
            finally
            {
                StopCapture();

                // The caller's work gets its finally blocks whatever ended the run.
                DisposeWork(ctx);

                if (!completed)
                {
                    ctx.Trouble.Add("the run ended early (an exception escaped, or Unity stopped the coroutine)");
                    EmergencyUnload("the run ended early");
                }

                try
                {
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                }
                catch (Exception)
                {
                    // Nothing to do: the handler is inert once _current is not this run.
                }

                try
                {
                    LogMessages("the host's own steps");

                    var head = $"FINISHED {locationId} in {total.ElapsedMilliseconds} ms - ";
                    var outcome =
                        ctx.Refusal != null ? $"REFUSED: {ctx.Refusal}" :
                        ctx.Abort != null ? $"STOPPED: {ctx.Abort}" :
                        $"{plan.Kept.Count} scene(s) of '{plan.Rcid}' loaded and unloaded" +
                        (ctx.WorkFailure != null ? $", but the work while loaded failed: {ctx.WorkFailure}" : "");

                    // Stage M3: the Maps tab's result line reads this.
                    LastOutcome = new RunOutcome
                    {
                        Claim = claim,
                        LocationId = locationId,
                        Problem = ctx.Refusal != null ? $"refused: {ctx.Refusal}" :
                                  ctx.Abort != null ? $"stopped: {ctx.Abort}" :
                                  ctx.WorkFailure != null ? $"the capture failed: {ctx.WorkFailure}" : null,
                        Trouble = ctx.Trouble.Count > 0 ? ctx.Trouble[0] : null,
                        Seconds = total.Elapsed.TotalSeconds,
                    };

                    if (ctx.Trouble.Count == 0)
                    {
                        Log(head + outcome + "; every hosted scene is unloaded and the compared state matches (scenes, " +
                            "LocationScene lists, LevelSettings and EnvironmentManager instances, RenderSettings " +
                            "ambient/probe/fog/skybox/sun, texture streaming and shadow distance, shader globals, NavMesh " +
                            "vertices, DontDestroyOnLoad roots). State outside that list is not proven.");
                    }
                    else
                    {
                        RestartAdvised = ctx.Trouble[0];
                        Log(head + outcome + "; RESTART THE GAME ADVISED: " + string.Join("; ", ctx.Trouble) + ".");
                    }
                }
                catch (Exception)
                {
                    // The verdict line is the last thing; nothing may keep the slot from freeing.
                }

                if (ReferenceEquals(_current, ctx)) _current = null;
                Release(claim);
            }
        }

        /// <summary>Loads each scene with <c>Assets.Manager.LoadScene(key.path, key.ToAssetName(), Additive, true)</c>
        /// (IAssetsManager, AssetsManager.cs:519), one at a time, each watched by a <see cref="LoadWatch"/>; switches off its
        /// Streamers; waits two frames; logs its counts. Any scene that does not load refuses the location (the caller
        /// unloads what did, after waiting for a load still going). Never Single: that would unload the menu.</summary>
        internal static IEnumerator LoadAll(IList<SceneEntry> scenes, RunContext ctx)
        {
            var measured = new List<Scene>();
            var nav = new NavTrack();
            Try("sampling the NavMesh before loading", () => nav.Sample(null, 0, scenes.Count, force: true));

            for (var i = 0; i < scenes.Count; i++)
            {
                var entry = scenes[i];
                var label = $"scene {i + 1}/{scenes.Count}";

                // Stage M3: the Maps tab's cancel, before the next scene starts loading.
                if (_stopAsked != null)
                {
                    ctx.Abort = $"{_stopAsked} before {label} '{entry.Name}'";
                    break;
                }

                SetPhase($"loading {label} '{entry.Name}'");

                if (!InMenu(out var why))
                {
                    ctx.Abort = $"no longer in the menu before {label} '{entry.Name}' ({why})";
                    break;
                }

                if (IsLoaded(SceneManager.GetSceneByName(entry.Name)))
                {
                    // Loading it again would make a second copy; an earlier run left it, or the menu uses a scene of that name.
                    ctx.Refusal = $"{label} '{entry.Name}' is already loaded";
                    break;
                }

                var memBefore = Memory();
                var errorsBefore = ErrorCounts();
                var appearedBefore = ctx.Appeared.Count;
                LoadSceneOperation op = null;

                if (!Try($"LoadScene('{entry.Key.path}', '{entry.Key.ToAssetName()}')", () =>
                        op = EFT.Assets.Manager.LoadScene(entry.Key.path, entry.Key.ToAssetName(), LoadSceneMode.Additive, true, null)) ||
                    op == null)
                {
                    ctx.Refusal = $"{label} '{entry.Name}' did not start loading";
                    break;
                }

                ctx.Operations[entry] = op;

                var watch = new LoadWatch();
                string expired = null;
                while (!op.Completed)
                {
                    if ((expired = watch.Expired(op)) != null) break;
                    if (RaidStarting()) { ctx.Abort = $"a GameWorld appeared while {label} '{entry.Name}' was loading"; break; }
                    if (_stopAsked != null) { ctx.Abort = $"{_stopAsked} while {label} '{entry.Name}' was loading"; break; }
                    yield return Tick();
                }

                if (ctx.Abort != null) break;

                if (expired != null)
                {
                    ctx.Refusal = $"{label} '{entry.Name}' did not finish loading: {expired}";
                    break;
                }

                if (op.Failed)
                {
                    ctx.Refusal = $"{label} '{entry.Name}' did not load: {op.Error}";
                    break;
                }

                var loadMs = watch.ElapsedMs;
                DiffScenes(ctx);

                // Which of the scenes that appeared during this load is the one asked for: by name, else the first.
                var arrived = ctx.Appeared.Skip(appearedBefore).Where(h => h.Entry == null && IsLoaded(h.Scene)).ToList();
                var hosted = arrived.FirstOrDefault(h => string.Equals(h.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) ??
                             arrived.FirstOrDefault();

                if (hosted == null)
                {
                    ctx.Refusal = $"{label} '{entry.Name}' reported done in {loadMs} ms but no new scene appeared";
                    break;
                }

                if (!string.Equals(hosted.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                    Log($"{label}: asked for '{entry.Name}', the scene that appeared is '{hosted.Name}'.");

                hosted.Entry = entry;
                entry.Scene = hosted.Scene;
                var scene = hosted.Scene;

                // A Streamer loads and unloads further scenes around the camera from its own Update - scenes this host would
                // not know to unload. Switched off before it gets a frame.
                Try($"disabling {label}'s streamers", () => DisableStreamers(scene, label));

                // Two frames: Start() runs on the first, and a camera has rendered (so Renderer.isVisible means something)
                // by the second.
                yield return Tick();
                yield return Tick();

                measured.Add(scene);
                Try($"measuring {label}", () => MeasureScene(label, entry.Name, scene, loadMs, memBefore, errorsBefore));
                Try($"sampling the NavMesh after {label}", () => nav.Sample(entry.Name, i + 1, scenes.Count,
                    force: KeptSceneSuffixes.Any(s => entry.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))));
            }

            Try("the NavMesh sampling summary", () => nav.Summary());

            if (ctx.Refusal != null) Log($"{ctx.LocationId}: STOPPED LOADING - refused: {ctx.Refusal}.");
            else if (ctx.Abort != null) Log($"{ctx.LocationId}: STOPPED LOADING: {ctx.Abort}.");
            else if (measured.Count > 0) Try("the combined measurement", () => MeasureTogether(measured, ctx.Before));
        }

        /// <summary>Runs the caller's coroutine inline, so every one of its yields also ticks the dead-run check: nested
        /// IEnumerators are flattened onto <see cref="RunContext.Work"/>; a Unity yield instruction is passed through with the
        /// stalled-yield test suspended. A throw ends the work (recorded; the unload still runs); a raid starting or
        /// <see cref="WhileLoadedCapSeconds"/> passing aborts into the unload. The stack is disposed on every exit.</summary>
        private static IEnumerator Drive(Func<IEnumerator> whileLoaded, RunContext ctx)
        {
            IEnumerator root = null;
            if (!Try("starting the work while loaded", () => root = whileLoaded()))
            {
                ctx.WorkFailure = "it threw on start (logged above)";
                yield break;
            }

            if (root != null) ctx.Work.Push(root);
            var clock = Stopwatch.StartNew();

            try
            {
                while (ctx.Work.Count > 0)
                {
                    if (RaidStarting())
                    {
                        ctx.Abort = "a GameWorld appeared while the map was hosted";
                        yield break;
                    }

                    // Stage M3: the Maps tab's cancel - disposed like the cap, so the capture's finally runs.
                    if (_stopAsked != null)
                    {
                        ctx.Abort = _stopAsked;
                        Log($"{ctx.LocationId}: {ctx.Abort} - the work is stopped, unloading.");
                        yield break;
                    }

                    if (clock.Elapsed.TotalSeconds > WhileLoadedCapSeconds)
                    {
                        ctx.Abort = $"the work while loaded ran past its {WhileLoadedCapSeconds:0} s cap";
                        Log($"{ctx.LocationId}: {ctx.Abort} - stopped, unloading.");
                        yield break;
                    }

                    var top = ctx.Work.Peek();
                    var more = false;
                    object current = null;

                    if (!Try("the work while loaded", () =>
                        {
                            more = top.MoveNext();
                            if (more) current = top.Current;
                        }))
                    {
                        ctx.WorkFailure = "it threw (logged above)";
                        yield break;
                    }

                    if (!more)
                    {
                        // Finished on its own: disposing a finished iterator is a no-op, but a hand-written one may hold more.
                        var done = ctx.Work.Pop();
                        try { (done as IDisposable)?.Dispose(); } catch (Exception) { }
                        continue;
                    }

                    if (current is IEnumerator nested)
                    {
                        ctx.Work.Push(nested);
                        continue;
                    }

                    if (current == null)
                    {
                        yield return Tick();
                        continue;
                    }

                    _waitingOnInstruction = true;
                    yield return current;
                    _waitingOnInstruction = false;
                    Tick();
                }
            }
            finally
            {
                DisposeWork(ctx);
            }
        }

        /// <summary>Waits for every load still going (never abandoning one that progresses), then unloads every scene that
        /// appeared during the run in reverse order of appearance - requested or not - then Resources.UnloadUnusedAssets and
        /// a forced collection, then the DontDestroyOnLoad check, the restore and the comparison with the baseline, as the
        /// probe does. Anything unproven lands in the run's trouble.</summary>
        internal static IEnumerator UnloadAll(RunContext ctx)
        {
            foreach (var pair in ctx.Operations.ToList())
            {
                var load = pair.Value;
                if (load == null || load.Completed) continue;

                var watch = new LoadWatch();
                string expired = null;
                Log($"waiting for '{pair.Key.Name}''s load to end, so it can be unloaded.");
                while (!load.Completed && (expired = watch.Expired(load)) == null) yield return Tick();

                if (!load.Completed)
                    ctx.Trouble.Add($"'{pair.Key.Name}''s load never finished ({expired}), so its scene may be left loaded");
            }

            DiffScenes(ctx);

            var unasked = ctx.Appeared.Where(h => h.Entry == null).Select(h => $"'{h.Name}'").ToList();
            if (unasked.Count > 0)
                Log($"{unasked.Count} scene(s) appeared that were not requested (streamed in, or loaded by a hosted script) - " +
                    $"unloaded with the rest: {string.Join(", ", unasked)}.");

            // Repeated until nothing is left (M1 review): a scene that appears WHILE the others unload - loaded by a script
            // of a hosted scene on its way out - is caught by the next pass's diff and unloaded too. A scene whose unload
            // failed is not retried (its trouble is already said), so the passes end; MaxUnloadPasses bounds a scene that
            // keeps loading another.
            var failed = new HashSet<int>();

            for (var pass = 1; ; pass++)
            {
                if (pass > 1) DiffScenes(ctx);

                var pending = ctx.Appeared.Count(h => !failed.Contains(h.Scene.handle));
                if (pending == 0) break;

                if (pass > MaxUnloadPasses)
                {
                    ctx.Trouble.Add($"scenes kept appearing through {MaxUnloadPasses} unload passes");
                    break;
                }

                if (pass > 1)
                    Log($"unload pass {pass}: {pending} scene(s) appeared during the unload - " +
                        string.Join(", ", ctx.Appeared.Where(h => !failed.Contains(h.Scene.handle)).Select(h => $"'{h.Name}'")) + ".");

                for (var i = ctx.Appeared.Count - 1; i >= 0; i--)
                {
                    var hosted = ctx.Appeared[i];
                    var scene = hosted.Scene;

                    if (failed.Contains(scene.handle)) continue;

                    if (!IsLoaded(scene))
                    {
                        Log($"unload '{hosted.Name}': not loaded (any more) - nothing to do.");
                        ctx.Appeared.RemoveAt(i);
                        continue;
                    }

                    var clock = Stopwatch.StartNew();
                    AsyncOperation op = null;
                    SetPhase($"unloading '{hosted.Name}' ({ctx.Appeared.Count} left)");

                    if (!Try($"UnloadSceneAsync('{hosted.Name}')", () => op = SceneManager.UnloadSceneAsync(scene)) || op == null)
                    {
                        failed.Add(scene.handle);
                        ctx.Trouble.Add($"'{hosted.Name}' could not be unloaded (no operation)");
                        continue;
                    }

                    while (!op.isDone && clock.Elapsed.TotalSeconds < TimeoutSeconds) yield return Tick();

                    if (!op.isDone)
                    {
                        failed.Add(scene.handle);
                        ctx.Trouble.Add($"'{hosted.Name}''s unload did not finish in {TimeoutSeconds:0} s");
                        continue;
                    }

                    // By reference, not by index: a scene that appeared during this unload was appended to the list (the
                    // sceneLoaded handler), so the index may have moved - never past i, but said plainly.
                    ctx.Appeared.Remove(hosted);
                    Log($"unloaded '{hosted.Name}' in {clock.ElapsedMilliseconds} ms. {Memory()}");
                }
            }

            yield return Tick();
            Log($"after the unloads: {Memory()}; {VramProbe.Text()}");

            if (RaidStarting())
            {
                // A raid is loading: its first scene loads Single and takes ours with it, and a sweep or a restore now would
                // land on the raid's own state (its LevelSettings writes the same RenderSettings).
                ctx.Trouble.Add("a raid started loading during the run - the asset sweep and the restore were skipped");

                // ...but not the texture mip limit, a user setting the raid does not re-apply (see MenuState.MipLimit)
                if (ctx.Before != null)
                {
                    var put = new List<string>();
                    Try("restoring the texture mip limit", () => ctx.Before.RestoreMipLimit(put));
                    if (put.Count > 0) Log($"restore: put back {put[0]} (the rest skipped for the raid).");
                }
            }
            else
            {
                var clock = Stopwatch.StartNew();
                AsyncOperation sweep = null;
                SetPhase("freeing the map's assets");
                Try("Resources.UnloadUnusedAssets", () => sweep = Resources.UnloadUnusedAssets());

                if (sweep != null)
                {
                    while (!sweep.isDone && clock.Elapsed.TotalSeconds < TimeoutSeconds) yield return Tick();
                    if (!sweep.isDone) ctx.Trouble.Add($"Resources.UnloadUnusedAssets did not finish in {TimeoutSeconds:0} s");
                }

                Try("the garbage collection", () => MapCapture.CollectGarbage("after the menu map host", force: true));
                Log($"after UnloadUnusedAssets ({clock.ElapsedMilliseconds} ms) and a collection: {Memory()}; {VramProbe.Text()}");
            }

            if (ctx.Before != null && !RaidStarting())
            {
                SetPhase("restoring the menu");
                Try("the DontDestroyOnLoad leak check", () => SweepDdol(ctx.Trouble));
                Try("restoring the global state", () => ctx.Before.Restore(ctx.Trouble));
                yield return Tick();
                Try("the final comparison", () =>
                {
                    var after = MenuState.Take();
                    Log($"after: {after.Describe()}");
                    foreach (var change in ctx.Before.Differences(after)) ctx.Trouble.Add($"still different: {change}");
                });
            }
            else if (ctx.Before == null && ctx.Operations.Count > 0)
            {
                ctx.Trouble.Add("no baseline was taken, so nothing could be compared or restored");
            }

            if (ctx.Appeared.Count > 0) ctx.Trouble.Add($"scene(s) {string.Join(", ", ctx.Appeared.Select(h => h.Name))} are still loaded");
        }

        private static void OnDead(string hostState, int framesAgo)
        {
            Log($"the host's coroutine is dead (host {hostState}, last yield {framesAgo} frame(s) ago) - it died without its cleanup.");
            EmergencyUnload("the host's coroutine died mid-run");
        }

        /// <summary>For a run that cannot finish its own: disposes the caller's work (its finally blocks run), stops watching
        /// sceneLoaded, and starts an unload of every scene that appeared during the run, without waiting. The menu is then
        /// unproven, so a restart is advised for the session.</summary>
        private static void EmergencyUnload(string why)
        {
            var started = new List<string>();
            var ctx = _current;

            DisposeWork(ctx);

            // 2026-10-03: the texture mip limit, which nothing else on this path puts back (see MenuState.MipLimit)
            try
            {
                var restored = new List<string>();
                ctx?.Before?.RestoreMipLimit(restored);
                if (restored.Count > 0) Log($"emergency unload: put back {restored[0]}.");
            }
            catch (Exception ex)
            {
                Log($"emergency unload: the texture mip limit could not be put back ({ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                DiffScenes(ctx);
            }
            catch (Exception ex)
            {
                Log($"emergency unload: the scene diff failed ({ex.GetType().Name}: {ex.Message}).");
            }

            foreach (var hosted in ctx?.Appeared.AsEnumerable().Reverse().ToList() ?? new List<Hosted>())
            {
                try
                {
                    if (!IsLoaded(hosted.Scene)) continue;
                    if (SceneManager.UnloadSceneAsync(hosted.Scene) != null) started.Add(hosted.Name);
                }
                catch (Exception ex)
                {
                    Log($"emergency unload of '{hosted.Name}' failed ({ex.GetType().Name}: {ex.Message}).");
                }
            }

            ctx?.Appeared.Clear();
            if (ReferenceEquals(_current, ctx)) _current = null;
            RestartAdvised = why;
            Log($"EMERGENCY UNLOAD ({why}): started for [{string.Join(", ", started)}], not waited for. RESTART THE GAME ADVISED.");
        }

        // --- stage M2: waking the hosted scenes for a capture ------------------------------------------------------------

        /// <summary>The location's own Id for <paramref name="locationId"/> as typed ("interchange" gives "Interchange") -
        /// the key a raid capture of it is filed under (Player.Location / GameWorld.LocationId). Null with the reason when the
        /// session has no such location. Never throws.</summary>
        internal static string LocationKey(string locationId, out string why)
        {
            why = null;

            try
            {
                if (string.IsNullOrWhiteSpace(locationId))
                {
                    why = "no location id was given";
                    return null;
                }

                var location = FindLocation(locationId.Trim(), out why);
                if (location == null) return null;

                if (string.IsNullOrEmpty(location.Id))
                {
                    why = $"the location '{locationId}' has no Id";
                    return null;
                }

                return location.Id;
            }
            catch (Exception ex)
            {
                why = $"finding the location threw {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>Stage M3: the Maps tab's cheap "can this map be hosted" test - the location is in the session, is not the
        /// hideout, and its Scene key names a bundle and an rcid. Loads nothing: whether the preset itself loads is only
        /// known when <see cref="ResolveScenes"/> tries, and its refusal reaches the result line. False with the reason.
        /// Never throws.</summary>
        /// <param name="locationId">The location id as the Maps tab knows it.</param>
        /// <param name="why">Why it cannot be hosted.</param>
        internal static bool HasScenes(string locationId, out string why)
        {
            why = null;

            try
            {
                if (string.IsNullOrWhiteSpace(locationId))
                {
                    why = "no map is selected";
                    return false;
                }

                var location = FindLocation(locationId.Trim(), out why);
                if (location == null) return false;

                if (location.IsHideout) why = "it is the hideout";
                else if (!HasSceneKey(location.Scene)) why = "the game lists no scenes for it";
                else if (string.IsNullOrEmpty(location.Scene.rcid)) why = "its scene list has no name to load it by";

                return why == null;
            }
            catch (Exception ex)
            {
                why = $"its scenes could not be looked up ({ex.GetType().Name})";
                return false;
            }
        }

        /// <summary>The scenes the run in progress has loaded - the map's, never the menu's (a baseline scene is never in
        /// <see cref="RunContext.Appeared"/>). Empty outside a run.</summary>
        internal static List<Scene> HostedScenes()
        {
            var ctx = _current;
            var scenes = new List<Scene>();
            if (ctx == null) return scenes;

            foreach (var hosted in ctx.Appeared)
            {
                if (!IsLoaded(hosted.Scene)) continue;
                if (ctx.BaselineHandles != null && ctx.BaselineHandles.Contains(hosted.Scene.handle)) continue;
                if (scenes.Any(s => s.handle == hosted.Scene.handle)) continue;
                scenes.Add(hosted.Scene);
            }

            return scenes;
        }

        /// <summary>Objects (renderers, transforms, activations, restores) handled between two yields of the wake and its
        /// restore, so a map of 200,000 renderers never freezes the menu for seconds in one frame. Every yield goes
        /// through the host's driver (<see cref="Tick"/>).</summary>
        internal const int WakeChunk = 5000;

        /// <summary>Stage M2 rollback: false wakes nothing, and a menu capture draws the hosted scenes as they loaded (with
        /// Interchange's interiors switched off - play-test 2026-09-30).</summary>
        internal static readonly bool WakeHostedScenes = true;

        /// <summary>Stage M2 (review): the broad second pass - every inactive ancestor of capture geometry, every switched-off
        /// capture-layer renderer, terrain and LOD group - on top of the culler lists. Off: the pass only COUNTS what it would
        /// wake beyond the culler lists, so a play-test says whether the narrow rule is enough.</summary>
        internal static readonly bool MenuWakeAllInactive = false;

        /// <summary>Stage M2 (review) rollback: false leaves the menu's own capture-layer renderers drawing into the capture.</summary>
        internal static readonly bool HideMenuGeometry = true;

        /// <summary>
        /// What the wake changed, as one journal in the order it was changed - so <see cref="Restore"/> undoes exactly
        /// that, newest first, and nothing else. Each entry is recorded BEFORE its switch, so one whose Awake/OnEnable threw
        /// after it went active is still put back. The restore can be spread over frames (<see cref="RestoreSpread"/>);
        /// <see cref="Restore"/> finishes whatever is left at once, is idempotent and never throws - the menu capture calls
        /// it from its whole-run finally.
        /// </summary>
        internal sealed class Wake
        {
            internal enum Kind
            {
                MenuHidden,
                CullerDisabled,
                Activated,
                RendererEnabled,
                ForceOffCleared,
                TerrainEnabled,
                LodEnabled,
                AudioMuted,

                /// <summary>Stage M2b: a hosted scene's directional light, off for the menu rig.</summary>
                LightDisabled,
            }

            private struct Entry
            {
                internal UnityEngine.Object Item;
                internal Kind Kind;
            }

            private readonly List<Entry> _journal = new List<Entry>();
            private readonly int[] _counts = new int[Enum.GetValues(typeof(Kind)).Length];

            /// <summary>The hosted scenes' handles when the wake began: a woken entry anywhere else is said after the
            /// restore.</summary>
            internal HashSet<int> HostedHandles = new HashSet<int>();

            private int _undone;
            private int _back, _gone, _failed;
            private string _first;
            private bool _finished;
            private Stopwatch _clock;

            internal int Count(Kind kind) => _counts[(int)kind];

            internal int Changes => _journal.Count;

            /// <summary>Records one switch about to be made.</summary>
            internal void Record(UnityEngine.Object item, Kind kind)
            {
                _journal.Add(new Entry { Item = item, Kind = kind });
                _counts[(int)kind]++;
            }

            /// <summary>The woken entries that are switched off again - a script or a culler undid them - for the line
            /// just before the first tile.</summary>
            internal string StillOff()
            {
                int objects = 0, renderers = 0, forced = 0, lods = 0, lights = 0;

                foreach (var e in _journal)
                {
                    try
                    {
                        if (e.Item == null) continue;

                        switch (e.Kind)
                        {
                            case Kind.Activated when !((GameObject)e.Item).activeSelf: objects++; break;
                            case Kind.RendererEnabled when !((Renderer)e.Item).enabled: renderers++; break;
                            case Kind.ForceOffCleared when ((Renderer)e.Item).forceRenderingOff: forced++; break;
                            case Kind.LodEnabled when !((LODGroup)e.Item).enabled: lods++; break;
                            case Kind.LightDisabled when ((Light)e.Item).enabled: lights++; break;
                        }
                    }
                    catch (Exception)
                    {
                        // A destroyed entry says nothing about the wake.
                    }
                }

                return $"{objects} of {Count(Kind.Activated)} woken object(s) inactive again, {renderers} of " +
                       $"{Count(Kind.RendererEnabled)} renderer(s) disabled again, {forced} of {Count(Kind.ForceOffCleared)} " +
                       $"forceRenderingOff set again, {lods} of {Count(Kind.LodEnabled)} LODGroup(s) disabled again" +
                       (Count(Kind.LightDisabled) > 0
                           ? $", {lights} of {Count(Kind.LightDisabled)} disabled directional light(s) enabled again"
                           : "");
            }

            /// <summary>The restore, <see cref="WakeChunk"/> entries a frame.</summary>
            internal IEnumerator RestoreSpread()
            {
                while (!Step(WakeChunk)) yield return Tick();
                Finish();
            }

            /// <summary>Finishes the restore now (whatever <see cref="RestoreSpread"/> did not get to). Never throws.</summary>
            internal void Restore()
            {
                try
                {
                    Step(int.MaxValue);
                }
                catch (Exception ex)
                {
                    _failed++;
                    if (_first == null) _first = $"{ex.GetType().Name}: {ex.Message}";
                }

                Finish();
            }

            /// <summary>Undoes up to <paramref name="budget"/> entries, newest first. True when none is left.</summary>
            private bool Step(int budget)
            {
                if (_clock == null) _clock = Stopwatch.StartNew();

                for (var n = 0; n < budget && _undone < _journal.Count; n++)
                {
                    var e = _journal[_journal.Count - 1 - _undone];
                    _undone++;

                    if (e.Item == null)
                    {
                        _gone++;
                        continue;
                    }

                    try
                    {
                        switch (e.Kind)
                        {
                            case Kind.MenuHidden: ((Renderer)e.Item).forceRenderingOff = false; break;
                            case Kind.CullerDisabled: ((Behaviour)e.Item).enabled = true; break;
                            case Kind.Activated: ((GameObject)e.Item).SetActive(false); break;
                            case Kind.RendererEnabled: ((Renderer)e.Item).enabled = false; break;
                            case Kind.ForceOffCleared: ((Renderer)e.Item).forceRenderingOff = true; break;
                            case Kind.TerrainEnabled: ((Terrain)e.Item).enabled = false; break;
                            case Kind.LodEnabled: ((LODGroup)e.Item).enabled = false; break;
                            case Kind.AudioMuted: ((Behaviour)e.Item).enabled = true; break;
                            case Kind.LightDisabled: ((Light)e.Item).enabled = true; break;
                        }

                        _back++;
                    }
                    catch (Exception ex)
                    {
                        // Deactivating runs OnDisable; one that throws must not stop the rest going back.
                        _failed++;
                        if (_first == null) _first = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }

                return _undone >= _journal.Count;
            }

            /// <summary>The restore's line, the foreign-scene check, and a restart advised when a switch would not go back.
            /// Once.</summary>
            private void Finish()
            {
                if (_finished) return;
                _finished = true;

                try
                {
                    if (_journal.Count == 0) return;

                    Log($"wake restored: {_back} of {_journal.Count} switch(es) put back - " +
                        string.Join(", ", Enum.GetValues(typeof(Kind)).Cast<Kind>().Where(k => Count(k) > 0).Select(k => $"{k} {Count(k)}")) +
                        $"; {_gone} destroyed since, {_failed} threw{(_first != null ? $" (first: {_first})" : "")}; " +
                        $"{_clock?.ElapsedMilliseconds ?? 0} ms.");

                    // The wake touches the hosted scenes only (the menu's renderers are MenuHidden, on purpose): anything
                    // else in the journal is an object that moved scene while woken, or a rule that reached too far.
                    var foreign = new List<string>();
                    var foreignCount = 0;

                    foreach (var e in _journal)
                    {
                        if (e.Kind == Kind.MenuHidden || e.Item == null) continue;

                        var go = e.Item as GameObject ?? (e.Item as Component)?.gameObject;
                        if (go == null || HostedHandles.Contains(go.scene.handle)) continue;

                        foreignCount++;
                        if (foreign.Count < 10) foreign.Add($"{e.Kind} '{go.name}' in '{go.scene.name}'");
                    }

                    if (foreignCount > 0)
                        Log($"wake: {foreignCount} restored entry(ies) are not in a hosted scene: {string.Join("; ", foreign)}.");

                    if (_failed > 0 && RestartAdvised == null)
                        RestartAdvised = $"{_failed} of the menu capture's wake switch(es) could not be put back";
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"{_voice}the wake's restore line failed ({ex.GetType().Name}: {ex.Message}).");
                }
                finally
                {
                    _journal.Clear();
                }
            }
        }

        /// <summary>What the survey of the hosted scenes found, for the wake and its lines.</summary>
        private sealed class WakeSurvey
        {
            internal readonly HashSet<Transform> Needed = new HashSet<Transform>();
            internal readonly List<GameObject> Inactive = new List<GameObject>();
            internal readonly List<Renderer> Geometry = new List<Renderer>();
            internal readonly List<Terrain> Terrains = new List<Terrain>();
            internal readonly List<LODGroup> Lods = new List<LODGroup>();
            internal readonly List<DisablerCullingObject> Cullers = new List<DisablerCullingObject>();
        }

        /// <summary>Renderer types that draw effects, never a surface of the map: never woken. By name, as MeasureScene
        /// reads AudioSource, so no module reference is needed.</summary>
        private static readonly HashSet<string> EffectRenderers = new HashSet<string>(StringComparer.Ordinal)
        {
            "ParticleSystemRenderer", "TrailRenderer", "LineRenderer", "VFXRenderer",
        };

        private static bool IsGeometry(Renderer r, int mask) =>
            r != null && (mask & (1 << r.gameObject.layer)) != 0 && !EffectRenderers.Contains(r.GetType().Name);

        /// <summary>
        /// Stage M2: readies the hosted map for a capture in the menu, recording every switch in <paramref name="wake"/>
        /// for <see cref="Wake.Restore"/>. In order:
        ///   1. the MENU's own renderers on a capture layer (its scenes and DontDestroyOnLoad) get forceRenderingOff, so the
        ///      capture camera never draws the menu into the map, wherever they stand;
        ///   2. the hosted scenes are surveyed, <see cref="WakeChunk"/> objects a frame;
        ///   3. every DisablerCullingObject in the hosted scenes that owns a switched-off target, or sits in an inactive
        ///      hierarchy the wake may switch on, is DISABLED - that unregisters it from the game's culling system
        ///      (BaseSystemComponent.OnDisable), so no ManualUpdate switches its targets back off during the capture;
        ///   4. the NARROW wake: the union of every hosted culler's _gameObjectsToTurnOff (activated when inactive) and
        ///      _componentsToTurnOff / _compsToTurnOffWhoIgnoreInversedColliders (DisablerCullingObject.cs:20-30): a
        ///      capture-layer Renderer enabled and its forceRenderingOff cleared, a LODGroup enabled, a capture-layer Terrain
        ///      enabled; lights and other behaviours are left to MapCapture's own per-floor hold, as in a raid. A Perfect
        ///      Culling renderer is woken only when a culler lists it;
        ///   5. the BROAD pass (<see cref="MenuWakeAllInactive"/>): every inactive ancestor of capture geometry and every
        ///      switched-off capture-layer renderer, terrain and LOD group. Off by default - it only counts what it would
        ///      wake beyond step 4;
        ///   6. every enabled AudioSource under a woken object is disabled (muted) for the capture.
        /// The capture layers are MapCapture's own mask, so Triggers, CullingMask and the collider layers never count; effect
        /// renderers never do either. Each activation is guarded on its own and counted (Awake/OnEnable run, as in
        /// MapCapture.HoldScene); the exceptions the game logs meanwhile are counted from the host's log capture.
        /// </summary>
        /// <param name="wake">Where the switches are recorded.</param>
        /// <param name="mask">The capture's culling mask (MapCapture.MenuCaptureMask).</param>
        internal static IEnumerator WakeHosted(Wake wake, int mask)
        {
            if (!WakeHostedScenes)
            {
                Log("wake: switched off (WakeHostedScenes) - the hosted scenes are captured as they loaded.");
                yield break;
            }

            var clock = Stopwatch.StartNew();
            var errorsBefore = ErrorCounts();
            List<Scene> scenes = null;

            if (!Try("finding the hosted scenes", () => scenes = HostedScenes()) || scenes == null || scenes.Count == 0)
            {
                Log("wake: no hosted scene is loaded - nothing was switched on.");
                yield break;
            }

            wake.HostedHandles = new HashSet<int>(scenes.Select(s => s.handle));

            // 1. The menu's own geometry out of the picture.
            if (HideMenuGeometry)
            {
                var hiding = HideMenuSide(wake, mask);
                while (hiding.MoveNext()) yield return hiding.Current;
            }

            // 2. The survey, a chunk a frame.
            var survey = new WakeSurvey();
            var surveying = SurveyHosted(scenes, mask, survey);
            while (surveying.MoveNext()) yield return surveying.Current;

            var surveyMs = clock.ElapsedMilliseconds;
            var throws = 0;
            string firstThrow = null;

            void Threw(Exception ex)
            {
                throws++;
                if (firstThrow == null) firstThrow = $"{ex.GetType().Name}: {ex.Message}";
            }

            // The culler lists' union, in the hosted scenes only.
            var objects = new List<GameObject>();
            var objectSet = new HashSet<GameObject>();
            var components = new List<Component>();
            var componentSet = new HashSet<Component>();
            var owning = new HashSet<DisablerCullingObject>();
            var otherComponents = 0;

            foreach (var culler in survey.Cullers)
            {
                try
                {
                    if (culler == null) continue;

                    foreach (var go in culler._gameObjectsToTurnOff ?? new List<GameObject>())
                    {
                        if (go == null || go.activeSelf || !wake.HostedHandles.Contains(go.scene.handle)) continue;
                        owning.Add(culler);
                        if (objectSet.Add(go)) objects.Add(go);
                    }

                    foreach (var list in new[] { culler._componentsToTurnOff, culler._compsToTurnOffWhoIgnoreInversedColliders })
                    foreach (var c in list ?? new List<Component>())
                    {
                        if (c == null || !wake.HostedHandles.Contains(c.gameObject.scene.handle)) continue;

                        var off =
                            c is Renderer r ? IsGeometry(r, mask) && (!r.enabled || r.forceRenderingOff) :
                            c is LODGroup g ? !g.enabled :
                            c is Terrain t ? (mask & (1 << t.gameObject.layer)) != 0 && !t.enabled :
                            false;

                        if (!(c is Renderer) && !(c is LODGroup) && !(c is Terrain))
                        {
                            otherComponents++;
                            continue;
                        }

                        if (!off) continue;
                        owning.Add(culler);
                        if (componentSet.Add(c)) components.Add(c);
                    }
                }
                catch (Exception ex)
                {
                    Threw(ex);
                }
            }

            // 3. The cullers that could switch the wake back off: one owning a target it is about to lose, or one in an
            // inactive hierarchy (activated, it would register and apply "not entered" - everything off).
            foreach (var culler in survey.Cullers)
            {
                try
                {
                    if (culler == null || !culler.enabled) continue;
                    if (!owning.Contains(culler) && culler.gameObject.activeInHierarchy) continue;

                    wake.Record(culler, Wake.Kind.CullerDisabled);
                    culler.enabled = false;
                }
                catch (Exception ex)
                {
                    Threw(ex);
                }
            }

            yield return Tick();

            // 4. The narrow wake: the culler lists.
            var sinceYield = 0;

            foreach (var go in objects)
            {
                try
                {
                    // A script woken earlier may have switched it on itself: only a still-inactive one is ours to record.
                    if (go == null || go.activeSelf) continue;

                    wake.Record(go, Wake.Kind.Activated);
                    go.SetActive(true);
                }
                catch (Exception ex)
                {
                    Threw(ex);
                }

                if (++sinceYield < WakeChunk) continue;
                sinceYield = 0;
                yield return Tick();
            }

            foreach (var c in components) WakeComponent(wake, c, Threw);

            var narrowMs = clock.ElapsedMilliseconds;
            var hiddenByParent = objects.Count(o => o != null && o.activeSelf && !o.activeInHierarchy) +
                                 components.Count(c => c != null && !c.gameObject.activeInHierarchy);

            yield return Tick();

            // 5. The broad pass - applied only with MenuWakeAllInactive, counted always.
            var wouldObjects = 0;
            var wouldComponents = 0;

            foreach (var go in survey.Inactive)
            {
                if (go == null || go.activeSelf || !survey.Needed.Contains(go.transform)) continue;
                wouldObjects++;

                if (!MenuWakeAllInactive) continue;

                try
                {
                    wake.Record(go, Wake.Kind.Activated);
                    go.SetActive(true);
                }
                catch (Exception ex)
                {
                    Threw(ex);
                }

                if (++sinceYield < WakeChunk) continue;
                sinceYield = 0;
                yield return Tick();
            }

            foreach (var r in survey.Geometry)
            {
                if (r == null || (r.enabled && !r.forceRenderingOff)) continue;
                wouldComponents++;
                if (MenuWakeAllInactive) WakeComponent(wake, r, Threw);
            }

            foreach (var t in survey.Terrains)
            {
                if (t == null || t.enabled) continue;
                wouldComponents++;
                if (MenuWakeAllInactive) WakeComponent(wake, t, Threw);
            }

            foreach (var g in survey.Lods)
            {
                if (g == null || g.enabled) continue;
                wouldComponents++;
                if (MenuWakeAllInactive) WakeComponent(wake, g, Threw);
            }

            yield return Tick();

            // 6. Muted: an audio source a woken object started (by name - the project references no audio module).
            var muted = new HashSet<Behaviour>();
            var woken = objects.Where(o => o != null && o.activeInHierarchy).ToList();
            if (MenuWakeAllInactive) woken.AddRange(survey.Inactive.Where(o => o != null && o.activeInHierarchy));

            foreach (var go in woken)
            {
                try
                {
                    foreach (var b in go.GetComponentsInChildren<Behaviour>(false))
                    {
                        if (b == null || !b.enabled || b.GetType().Name != "AudioSource" || !muted.Add(b)) continue;
                        wake.Record(b, Wake.Kind.AudioMuted);
                        b.enabled = false;
                    }
                }
                catch (Exception ex)
                {
                    Threw(ex);
                }

                if (++sinceYield < WakeChunk) continue;
                sinceYield = 0;
                yield return Tick();
            }

            var errors = ErrorCounts();

            Log($"wake: {scenes.Count} hosted scene(s), capture mask 0x{mask:X8}, {survey.Cullers.Count} culling object(s), " +
                $"{wake.Count(Wake.Kind.CullerDisabled)} disabled for the capture. NARROW (the culler lists): " +
                $"{objects.Count} inactive object(s) and {components.Count} switched-off renderer/LOD/terrain target(s) - " +
                $"{wake.Count(Wake.Kind.Activated)} object(s) activated, {wake.Count(Wake.Kind.RendererEnabled)} renderer(s) enabled, " +
                $"{wake.Count(Wake.Kind.ForceOffCleared)} forceRenderingOff cleared, {wake.Count(Wake.Kind.LodEnabled)} LODGroup(s) " +
                $"and {wake.Count(Wake.Kind.TerrainEnabled)} terrain(s) enabled; {otherComponents} other listed component(s) " +
                $"(lights, scripts) left to the capture's own hold; {hiddenByParent} target(s) still under an inactive parent. " +
                $"BROAD ({(MenuWakeAllInactive ? "APPLIED" : "counted only")}): {wouldObjects} more inactive ancestor(s) of " +
                $"capture geometry and {wouldComponents} more switched-off renderer/terrain/LOD group(s) beyond the lists. " +
                $"{wake.Count(Wake.Kind.AudioMuted)} audio source(s) muted; {throws} switch(es) threw" +
                $"{(firstThrow != null ? $" (first: {firstThrow})" : "")}, {errors[0] - errorsBefore[0]} exception(s) and " +
                $"{errors[1] - errorsBefore[1]} error(s) logged by the game meanwhile; survey {surveyMs} ms, narrow " +
                $"{narrowMs - surveyMs} ms, total {clock.ElapsedMilliseconds} ms. {Memory()}");
        }

        /// <summary>
        /// Stage M2b: switches off every ENABLED directional light in the hosted scenes - active or under an inactive
        /// parent, so one a later activation would bring back stays dark - recording each in <paramref name="wake"/>'s
        /// journal before its switch, so <see cref="Wake.Restore"/> puts it back. The menu capture's rig is its whole light:
        /// a scene sun (the scripts scene carries one) would add a second sun and a second set of shadows. Point and spot
        /// lights are left as the raid capture leaves them. Returns how many were switched off; never throws.
        /// </summary>
        /// <param name="wake">The menu capture's wake journal.</param>
        internal static int DisableHostedDirectionalLights(Wake wake)
        {
            var disabled = 0;
            var threw = 0;
            string first = null;
            var names = new List<string>();
            List<Scene> scenes = null;

            if (wake == null || !Try("finding the hosted scenes", () => scenes = HostedScenes()) || scenes == null)
            {
                Log("directional lights: no hosted scene is loaded - none disabled.");
                return 0;
            }

            foreach (var scene in scenes)
            {
                GameObject[] roots = null;
                if (!Try("listing a hosted scene's roots", () => roots = IsLoaded(scene) ? scene.GetRootGameObjects() : null) ||
                    roots == null)
                    continue;

                foreach (var root in roots)
                {
                    Light[] lights = null;
                    if (!Try("listing a hosted root's lights", () => lights = root != null ? root.GetComponentsInChildren<Light>(true) : null) ||
                        lights == null)
                        continue;

                    foreach (var light in lights)
                    {
                        try
                        {
                            if (light == null || light.type != LightType.Directional || !light.enabled) continue;

                            wake.Record(light, Wake.Kind.LightDisabled);
                            light.enabled = false;
                            disabled++;
                            if (names.Count < 8) names.Add($"'{light.name}' in '{light.gameObject.scene.name}'");
                        }
                        catch (Exception ex)
                        {
                            threw++;
                            if (first == null) first = $"{ex.GetType().Name}: {ex.Message}";
                        }
                    }
                }
            }

            Log($"directional lights: {disabled} enabled directional light(s) in the hosted scenes disabled for the capture's light " +
                $"rig{(names.Count > 0 ? $" ({string.Join(", ", names)}{(disabled > names.Count ? ", ..." : "")})" : "")}; {threw} threw" +
                $"{(first != null ? $" (first: {first})" : "")}.");

            return disabled;
        }

        /// <summary>One culler-listed (or broad) component switched on: a capture-layer renderer enabled and its
        /// forceRenderingOff cleared, a LODGroup or capture-layer terrain enabled. Recorded before each switch.</summary>
        private static void WakeComponent(Wake wake, Component c, Action<Exception> threw)
        {
            try
            {
                switch (c)
                {
                    case Renderer r:
                        if (!r.enabled)
                        {
                            wake.Record(r, Wake.Kind.RendererEnabled);
                            r.enabled = true;
                        }

                        if (r.forceRenderingOff)
                        {
                            wake.Record(r, Wake.Kind.ForceOffCleared);
                            r.forceRenderingOff = false;
                        }

                        break;

                    case LODGroup g when !g.enabled:
                        wake.Record(g, Wake.Kind.LodEnabled);
                        g.enabled = true;
                        break;

                    case Terrain t when !t.enabled:
                        wake.Record(t, Wake.Kind.TerrainEnabled);
                        t.enabled = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                threw(ex);
            }
        }

        /// <summary>The survey, one root at a time, yielding whenever <see cref="WakeChunk"/> objects have been read: every
        /// capture-layer renderer and terrain marks itself and its parents as needed (the broad rule), every inactive
        /// GameObject is listed parents first (GetComponentsInChildren is depth-first pre-order), and every culling object
        /// is listed.</summary>
        private static IEnumerator SurveyHosted(List<Scene> scenes, int mask, WakeSurvey survey)
        {
            var read = 0;

            foreach (var scene in scenes)
            {
                GameObject[] roots = null;
                if (!Try("listing a hosted scene's roots", () => roots = IsLoaded(scene) ? scene.GetRootGameObjects() : null) ||
                    roots == null)
                    continue;

                foreach (var root in roots)
                {
                    Try("surveying a hosted root", () => read += SurveyRoot(root, mask, survey));

                    if (read < WakeChunk) continue;
                    read = 0;
                    yield return Tick();
                }
            }
        }

        /// <summary>One root of <see cref="SurveyHosted"/>; returns how many objects it read.</summary>
        private static int SurveyRoot(GameObject root, int mask, WakeSurvey survey)
        {
            if (root == null) return 0;

            var read = 0;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                read++;
                if (!IsGeometry(r, mask)) continue;
                survey.Geometry.Add(r);
                MarkUp(r.transform, survey.Needed);
            }

            foreach (var t in root.GetComponentsInChildren<Terrain>(true))
            {
                read++;
                if (t == null || (mask & (1 << t.gameObject.layer)) == 0) continue;
                survey.Terrains.Add(t);
                MarkUp(t.transform, survey.Needed);
            }

            // After the marks: a group counts when capture geometry is at or beneath it.
            foreach (var g in root.GetComponentsInChildren<LODGroup>(true))
            {
                read++;
                if (g != null && survey.Needed.Contains(g.transform)) survey.Lods.Add(g);
            }

            foreach (var c in root.GetComponentsInChildren<DisablerCullingObject>(true))
                if (c != null) survey.Cullers.Add(c);

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                read++;
                if (t != null && !t.gameObject.activeSelf) survey.Inactive.Add(t.gameObject);
            }

            return read;
        }

        /// <summary>Marks <paramref name="t"/> and every parent, stopping at the first already marked (its parents are).</summary>
        private static void MarkUp(Transform t, HashSet<Transform> needed)
        {
            for (var walk = t; walk != null; walk = walk.parent)
                if (!needed.Add(walk)) return;
        }

        /// <summary>Hides the MENU's own renderers from the capture: every renderer on a capture layer in a scene that is not
        /// hosted, and in DontDestroyOnLoad, gets forceRenderingOff for the capture (recorded, put back by the restore) -
        /// inside the map's rectangle or not, and whether active or not, so none can switch on into the picture. Logs how
        /// many, and the bounds of the ones that were drawing.</summary>
        private static IEnumerator HideMenuSide(Wake wake, int mask)
        {
            var roots = new List<GameObject>();

            Try("listing the menu's own roots", () =>
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!IsLoaded(scene) || wake.HostedHandles.Contains(scene.handle)) continue;
                    roots.AddRange(scene.GetRootGameObjects());
                }

                roots.AddRange(DdolRoots());
            });

            int hidden = 0, drawing = 0, read = 0, failed = 0;
            var bounds = new Bounds();

            foreach (var root in roots)
            {
                Renderer[] renderers = null;
                if (!Try("listing a menu root's renderers", () => renderers = root != null ? root.GetComponentsInChildren<Renderer>(true) : null) ||
                    renderers == null)
                    continue;

                foreach (var r in renderers)
                {
                    read++;

                    try
                    {
                        if (r == null || r.forceRenderingOff || (mask & (1 << r.gameObject.layer)) == 0) continue;

                        if (r.enabled && r.gameObject.activeInHierarchy)
                        {
                            var b = r.bounds;
                            if (Finite(b.center) && Finite(b.size))
                            {
                                if (drawing == 0) bounds = b;
                                else bounds.Encapsulate(b);
                                drawing++;
                            }
                        }

                        wake.Record(r, Wake.Kind.MenuHidden);
                        r.forceRenderingOff = true;
                        hidden++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }

                if (read < WakeChunk) continue;
                read = 0;
                yield return Tick();
            }

            Log($"menu side: {hidden} renderer(s) on capture layers outside the hosted scenes (the menu's own scenes and " +
                $"DontDestroyOnLoad) hidden for the capture (forceRenderingOff), {drawing} of them drawing" +
                (drawing > 0
                    ? $", bounds x {F(bounds.min.x)}..{F(bounds.max.x)} z {F(bounds.min.z)}..{F(bounds.max.z)} y {F(bounds.min.y)}..{F(bounds.max.y)}"
                    : "") +
                (failed > 0 ? $"; {failed} would not switch" : "") + ".");
        }

        // --- the measurements --------------------------------------------------------------------------------------------

        /// <summary>Disables every enabled component in <paramref name="scene"/> whose type name contains "Streamer", and
        /// logs each under <paramref name="label"/>.</summary>
        internal static void DisableStreamers(Scene scene, string label)
        {
            if (!IsLoaded(scene)) return;

            var names = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || b.GetType().Name.IndexOf("Streamer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var was = b.enabled;
                    b.enabled = false;
                    names.Add($"{b.GetType().FullName} on '{b.gameObject.name}' (was {(was ? "enabled" : "disabled")})");
                }
            }

            Log(names.Count == 0
                ? $"{label}: no streamer component."
                : $"{label}: disabled {names.Count} streamer component(s): {string.Join("; ", names.Take(10))}.");
        }

        /// <summary>One scene, just loaded: what it contains and what it cost. <paramref name="label"/> starts each line (the
        /// probe's "level 17"), <paramref name="what"/> names it on the first.</summary>
        internal static void MeasureScene(string label, string what, Scene scene, long loadMs, string memBefore, int[] errorsBefore)
        {
            var clock = Stopwatch.StartNew();
            var roots = scene.GetRootGameObjects();
            var cameras = LiveCameras();

            int meshRenderers = 0, meshActive = 0, meshVisible = 0, renderersActive = 0, renderersVisible = 0, inAnyMask = 0;
            int terrains = 0, terrainsActive = 0, colliders = 0, collidersActive = 0, lights = 0, lightsActive = 0, directional = 0;
            int lods = 0, lodsActive = 0, behaviours = 0, behavioursActive = 0, missingScripts = 0;
            int bodiesFree = 0, audioEnabled = 0, sceneCameras = 0, locationScenes = 0, levelSettings = 0, streamers = 0;
            var bounds = new Bounds();
            var hasBounds = false;
            var xs = new List<float>();
            var zs = new List<float>();
            var layers = new Dictionary<int, int>();
            var terrainRects = new List<string>();
            var cameraNames = new List<string>();

            foreach (var root in roots)
            {
                if (root == null) continue;

                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;

                    var active = r.enabled && r.gameObject.activeInHierarchy;
                    var isMesh = r is MeshRenderer;
                    if (isMesh) meshRenderers++;
                    if (!active) continue;

                    renderersActive++;
                    var visible = r.isVisible;
                    if (visible) renderersVisible++;
                    if (isMesh)
                    {
                        meshActive++;
                        if (visible) meshVisible++;
                    }

                    var layer = r.gameObject.layer;
                    layers[layer] = layers.TryGetValue(layer, out var n) ? n + 1 : 1;
                    if (cameras.Any(c => (c.cullingMask & (1 << layer)) != 0)) inAnyMask++;

                    var b = r.bounds;
                    if (!Finite(b.center) || !Finite(b.size)) continue;
                    if (hasBounds) bounds.Encapsulate(b);
                    else { bounds = b; hasBounds = true; }

                    if (isMesh)
                    {
                        xs.Add(b.center.x);
                        zs.Add(b.center.z);
                    }
                }

                foreach (var t in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (t == null) continue;
                    terrains++;
                    if (!t.enabled || !t.gameObject.activeInHierarchy) continue;
                    terrainsActive++;

                    var at = t.GetPosition();
                    var size = t.terrainData != null ? t.terrainData.size : Vector3.zero;
                    if (terrainRects.Count < 8)
                        terrainRects.Add($"x {F(at.x)}..{F(at.x + size.x)} z {F(at.z)}..{F(at.z + size.z)} y {F(at.y)}..{F(at.y + size.y)}");
                }

                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    colliders++;
                    if (c.enabled && c.gameObject.activeInHierarchy) collidersActive++;
                }

                foreach (var l in root.GetComponentsInChildren<Light>(true))
                {
                    if (l == null) continue;
                    lights++;
                    if (!l.enabled || !l.gameObject.activeInHierarchy) continue;
                    lightsActive++;
                    if (l.type == LightType.Directional) directional++;
                }

                foreach (var g in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if (g == null) continue;
                    lods++;
                    if (g.enabled && g.gameObject.activeInHierarchy) lodsActive++;
                }

                foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    // A missing script comes back as a null entry.
                    if (m == null) { missingScripts++; continue; }

                    behaviours++;
                    if (m.isActiveAndEnabled) behavioursActive++;
                    if (m is LocationScene) locationScenes++;
                    else if (m is LevelSettings) levelSettings++;
                    else if (m is Streamer) streamers++;
                }

                foreach (var body in root.GetComponentsInChildren<Rigidbody>(false))
                    if (body != null && !body.isKinematic) bodiesFree++;

                // By name: the project references no audio module, and an enabled source is enough to say "may play".
                foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(false))
                    if (behaviour != null && behaviour.isActiveAndEnabled && behaviour.GetType().Name == "AudioSource") audioEnabled++;

                foreach (var cam in root.GetComponentsInChildren<Camera>(false))
                {
                    if (cam == null || !cam.enabled) continue;
                    sceneCameras++;
                    if (cameraNames.Count < 5) cameraNames.Add(cam.name);
                }
            }

            var errors = ErrorCounts();

            Log($"{label} '{what}' loaded in {loadMs} ms: {scene.rootCount} roots; " +
                $"MeshRenderers {meshActive} active / {meshRenderers}; Terrains {terrainsActive} active / {terrains}; " +
                $"Colliders {collidersActive} active / {colliders}; Lights {lightsActive} active / {lights} ({directional} directional); " +
                $"LODGroups {lodsActive} active / {lods}; MonoBehaviours {behavioursActive} active / {behaviours}, {missingScripts} missing script(s).");

            Log($"{label} bounds of {renderersActive} active renderers: " +
                (hasBounds
                    ? $"x {F(bounds.min.x)}..{F(bounds.max.x)} z {F(bounds.min.z)}..{F(bounds.max.z)} y {F(bounds.min.y)}..{F(bounds.max.y)}"
                    : "none") +
                (xs.Count > 0 ? $"; MeshRenderer centres 1-99 %: x {F(Pct(xs, 0.01f))}..{F(Pct(xs, 0.99f))} z {F(Pct(zs, 0.01f))}..{F(Pct(zs, 0.99f))}" : "") +
                (terrainRects.Count > 0 ? $"; terrain(s): {string.Join(" | ", terrainRects)}" : ""));

            Log($"{label} drawn? {renderersVisible} of {renderersActive} active renderers isVisible " +
                $"({meshVisible} of {meshActive} MeshRenderers); {inAnyMask} on a layer some enabled camera's mask includes; " +
                $"layers {string.Join(", ", layers.OrderByDescending(p => p.Value).Take(8).Select(p => $"{LayerMask.LayerToName(p.Key)}({p.Key})={p.Value}"))}.");

            Log($"{label} side effects: {sceneCameras} enabled camera(s) of its own" +
                (cameraNames.Count > 0 ? $" [{string.Join(", ", cameraNames)}]" : "") +
                $", {bodiesFree} non-kinematic rigidbodies, {audioEnabled} enabled audio sources; " +
                $"LocationScene x{locationScenes}, LevelSettings x{levelSettings}, Streamer x{streamers}; " +
                $"log during the load: {errors[0] - errorsBefore[0]} exceptions, {errors[1] - errorsBefore[1]} errors, " +
                $"{errors[2] - errorsBefore[2]} asserts, {errors[3] - errorsBefore[3]} warnings.");

            Log($"{label} memory: before {memBefore}; after {Memory()}. (measured in {clock.ElapsedMilliseconds} ms)");
        }

        /// <summary>Diagnostics only: which scenes add NavMesh area. After a scene loads, the NavMesh is triangulated and a
        /// line is logged only when its vertex count or XZ box changed, naming the scene(s) since the last sample.
        ///
        /// Cost: Unity exposes no cheap NavMesh-data counter (NavMesh.GetSettingsCount counts agent types, which scene
        /// data does not change), so the triangulation is the signal. On an empty or small NavMesh it costs ~0 ms; it
        /// reaches 30-40 ms and ~10 MB of fresh arrays per call once a large mesh (an _AI scene) is in - garbage the
        /// non-compacting Mono heap keeps reserved. So every scene is sampled only while the arrays allocated so far are
        /// under <see cref="DenseBytes"/> AND the last count was under <see cref="DenseVertices"/> vertices (and the GC
        /// is on); otherwise only _AI scenes, every <see cref="SparseEvery"/>th scene and the last. At most
        /// <see cref="MaxSamples"/> samples in all: worst case ~256 MB + 40 x ~11 MB plus the forced _AI and last samples, about 700 MB (far less in practice). A
        /// sparse sample's delta is attributed to the range of scenes since the previous sample.</summary>
        private sealed class NavTrack
        {
            private const long DenseBytes = 256L * 1024 * 1024;
            private const int DenseVertices = 50000;
            private const int SparseEvery = 25;
            private const int MaxSamples = 40;

            private int _verts = -1;
            private float _minX, _maxX, _minZ, _maxZ;
            private long _spentMs, _bytes;
            private int _samples, _changes, _skipped, _scenes;
            private int _lastSampled;
            private string _firstUnsampled;

            internal void Sample(string sceneName, int index, int count, bool force)
            {
                if (sceneName != null) _scenes++;
                if (_firstUnsampled == null) _firstUnsampled = sceneName;

                // Each triangulation allocates its arrays anew (~10 MB on a 300k-vertex mesh); with the collector off
                // (EFT turns it off in a raid) that garbage would stay, so then only the sparse samples run.
                var dense = _bytes < DenseBytes && _verts < DenseVertices &&
                            UnityEngine.Scripting.GarbageCollector.GCMode == UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
                // The _AI scene and the last are never skipped: on Streets the NavMesh arrives at scene 245 of 245, long
                // after the cheap empty-mesh samples have spent the cap.
                var must = force || index == count;
                if (!must && (_samples >= MaxSamples || (!dense && index % SparseEvery != 0)))
                {
                    _skipped++;
                    return;
                }

                var clock = Stopwatch.StartNew();
                var triangulation = NavMesh.CalculateTriangulation();
                var vertices = triangulation.vertices ?? Array.Empty<Vector3>();
                _bytes += vertices.Length * 12L + (triangulation.indices?.Length ?? 0) * 4L + (triangulation.areas?.Length ?? 0) * 4L;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var v in vertices)
                {
                    if (v.x < minX) minX = v.x;
                    if (v.x > maxX) maxX = v.x;
                    if (v.z < minZ) minZ = v.z;
                    if (v.z > maxZ) maxZ = v.z;
                }

                var ms = clock.ElapsedMilliseconds;
                _spentMs += ms;
                _samples++;

                var changed = vertices.Length != _verts || minX != _minX || maxX != _maxX || minZ != _minZ || maxZ != _maxZ;

                if (changed && sceneName != null)
                {
                    _changes++;
                    var which = index - _lastSampled <= 1
                        ? $"scene {index}/{count} '{sceneName}'"
                        : $"scenes {_lastSampled + 1}-{index}/{count} ('{_firstUnsampled}'..'{sceneName}', one sample for all {index - _lastSampled})";
                    var before = Math.Max(_verts, 0);
                    var delta = vertices.Length - before;
                    Log($"NavMesh after {which}: {before} -> {vertices.Length} vertices ({(delta >= 0 ? "+" : "")}{delta}); " +
                        (vertices.Length == 0
                            ? "empty"
                            : $"xz x {F(minX)}..{F(maxX)} z {F(minZ)}..{F(maxZ)} ({F(maxX - minX)}x{F(maxZ - minZ)} m)") +
                        $" [{ms} ms].");
                }
                else if (sceneName == null)
                {
                    Log($"NavMesh before loading: {vertices.Length} vertices [{ms} ms].");
                }

                _verts = vertices.Length;
                _minX = minX; _maxX = maxX; _minZ = minZ; _maxZ = maxZ;
                _lastSampled = index;
                _firstUnsampled = null;
            }

            internal void Summary() =>
                Log($"NavMesh per-scene sampling: {_samples} sample(s) (incl. the one before loading) in {_spentMs} ms " +
                    $"(~{_bytes / (1024 * 1024)} MB of arrays), {_changes} change(s); of {_scenes} loaded scene(s) {_skipped} not sampled " +
                    $"(dense under {DenseBytes / (1024 * 1024)} MB and {DenseVertices} vertices, then every {SparseEvery}th, the last " +
                    $"and every _AI scene; at most {MaxSamples} samples).");
        }

        /// <summary>Everything loaded, together: what phase 2's capture would find, and whether any camera draws it.</summary>
        internal static void MeasureTogether(List<Scene> scenes, MenuState before)
        {
            var clock = Stopwatch.StartNew();
            var triangulation = NavMesh.CalculateTriangulation();
            var navMs = clock.ElapsedMilliseconds;
            var navVerts = triangulation.vertices?.Length ?? 0;
            var navTris = (triangulation.indices?.Length ?? 0) / 3;

            var borderZones = SafeCount(() => LocationScene.GetAll<BorderZone>().Count());
            var spawns = SafeCount(() => LocationScene.GetAll<SpawnPointMarker>().Count());
            var exits = SafeCount(() => LocationScene.GetAll<ExfiltrationPoint>().Count());
            var botZones = SafeCount(() => LocationScene.GetAll<BotZone>().Count());
            var triggers = SafeCount(() => UnityEngine.Object.FindObjectsOfType<TriggerWithId>(true).Length);

            Log($"together ({scenes.Count} scene(s)) - phase 2 inputs: NavMesh {navVerts} vertices / {navTris} triangles " +
                $"(menu baseline {before?.NavVertices ?? -1}, {navMs} ms); Terrain.activeTerrains {Terrain.activeTerrains.Length} " +
                $"(baseline {before?.Terrains ?? -1}); LocationScene.LoadedScenes {LocationScene.LoadedScenes.Count}; BorderZones {borderZones}, " +
                $"SpawnPointMarkers {spawns}, ExfiltrationPoints {exits}, BotZones {botZones}; TriggerWithId (incl. inactive) {triggers}; " +
                $"Singleton<GameWorld> {(Singleton<GameWorld>.Instantiated ? "SET" : "not set")}, " +
                $"Singleton<LevelSettings> {(Singleton<LevelSettings>.Instantiated ? "SET" : "not set")}; Camera.main '{(Camera.main != null ? Camera.main.name : "none")}'.");

            // Diagnostics: the XZ boxes MapExtentProbe chooses from, in the format its own "extent sources" line uses in a
            // raid, so a menu run and a raid on the same map compare line for line.
            Try("the together bounds", () => Log($"together bounds: {MapExtentProbe.SourceBounds(triangulation.vertices)}."));

            if (before != null)
            {
                var now = MenuState.Take();
                var changes = before.Differences(now).ToList();
                Log(changes.Count == 0
                    ? "together: the compared global state is unchanged while loaded."
                    : $"together: the loaded scenes changed {string.Join("; ", changes)}.");
            }

            // Which camera could draw them: its mask against their layers, and its frustum against their bounds.
            var probed = new List<Renderer>();
            foreach (var scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                        if (r != null && r.enabled) probed.Add(r);
                }
            }

            foreach (var cam in LiveCameras())
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                var at = cam.transform.position;
                int inMask = 0, inView = 0, visible = 0;

                foreach (var r in probed)
                {
                    if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                    inMask++;
                    if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) inView++;
                    if (r.isVisible) visible++;
                }

                Log($"camera '{cam.name}' (scene '{cam.gameObject.scene.name}', depth {F(cam.depth)}, mask 0x{cam.cullingMask:X8}, " +
                    $"{(cam.targetTexture != null ? "to a RenderTexture" : "to the screen")}, at {V(at)}, far {F(cam.farClipPlane)}): " +
                    $"of {probed.Count} probed renderers {inMask} on its layers, {inView} of those inside its frustum; " +
                    $"{visible} isVisible (any camera).");
            }

            Log($"together: measured in {clock.ElapsedMilliseconds} ms. {Memory()}");
        }

        // --- the menu's global state --------------------------------------------------------------------------------------

        /// <summary>The global state a map scene is known (from the decompiled code) to change and not put back, plus the
        /// counts that say whether the menu is as it was.</summary>
        internal sealed class MenuState
        {
            internal int SceneCount;
            internal string SceneNames;
            internal string ActiveScene;
            internal int LocationScenes;
            internal int DoorColliders;
            internal LevelSettings LevelSettings;
            internal bool GameWorld;
            internal int Lightmaps;
            internal int NavVertices;
            internal int Terrains;

            internal AmbientMode AmbientMode;
            internal Color AmbientEquator, AmbientGround, AmbientSky, AmbientLight;
            internal float AmbientIntensity;
            internal bool Fog;
            internal Color FogColor;
            internal float FogDensity, FogStart, FogEnd;
            internal FogMode FogMode;
            internal Material Skybox;
            internal Light Sun;

            internal float StreamingBudget;
            internal int StreamingReduction;
            internal float ShadowDistance;
            internal float ControllerBudget;
            internal int ControllerReduction;

            /// <summary>2026-10-03: the global texture mip limit and EFT's SD-mode flag. EFT's SDModeController.Awake (a scene
            /// component - Streets', by its setting's name; unconfirmed) raises the limit by one when SD mode is on and mip
            /// streaming off, and only a raid's end (TarkovApplication) puts it back. Something raised it during the
            /// 2026-10-03 menu run: every DXT texture made afterwards came out one mip short, and one whose halved side was no
            /// longer whole 4x4 blocks (7812 -> 3906) was refused by D3D11 (E_INVALIDARG, 1,584 lines) until the 3D view
            /// crashed.</summary>
            internal int MipLimit;
            internal bool SdRuntime;
            internal SphericalHarmonicsL2 AmbientProbe;
            internal EnvironmentManagerBase Environment;

            internal float DirectionLightShadow, WaterLevel, SsrFactor;
            internal Color MinAmbientColor, TopHorizontSkyColor;

            internal static MenuState Take()
            {
                var s = new MenuState
                {
                    SceneCount = SceneManager.sceneCount,
                    ActiveScene = SceneManager.GetActiveScene().name,
                    LocationScenes = LocationScene.LoadedScenes.Count,
                    DoorColliders = LocationScene.DoorsCollisionColliders.Count,
                    LevelSettings = Singleton<LevelSettings>.Instance,
                    GameWorld = Singleton<GameWorld>.Instantiated,
                    Lightmaps = LightmapSettings.lightmaps?.Length ?? 0,
                    Terrains = Terrain.activeTerrains.Length,
                    AmbientMode = RenderSettings.ambientMode,
                    AmbientEquator = RenderSettings.ambientEquatorColor,
                    AmbientGround = RenderSettings.ambientGroundColor,
                    AmbientSky = RenderSettings.ambientSkyColor,
                    AmbientLight = RenderSettings.ambientLight,
                    AmbientIntensity = RenderSettings.ambientIntensity,
                    Fog = RenderSettings.fog,
                    FogColor = RenderSettings.fogColor,
                    FogDensity = RenderSettings.fogDensity,
                    FogStart = RenderSettings.fogStartDistance,
                    FogEnd = RenderSettings.fogEndDistance,
                    FogMode = RenderSettings.fogMode,
                    Skybox = RenderSettings.skybox,
                    Sun = RenderSettings.sun,
                    StreamingBudget = QualitySettings.streamingMipmapsMemoryBudget,
                    StreamingReduction = QualitySettings.streamingMipmapsMaxLevelReduction,
                    ShadowDistance = QualitySettings.shadowDistance,
                    ControllerBudget = GraphicsSettingsController.MipStreamingMemoryBudget,
                    ControllerReduction = GraphicsSettingsController.StreamingMipmapsMaxLevelReduction,
                    MipLimit = QualitySettings.globalTextureMipmapLimit,
                    SdRuntime = GraphicsSettingsController.ApplySDModeOnRuntime,
                    AmbientProbe = RenderSettings.ambientProbe,
                    Environment = EnvironmentManagerBase.Instance,
                    DirectionLightShadow = Shader.GetGlobalFloat("_DirectionLightShadow"),
                    WaterLevel = Shader.GetGlobalFloat("_WaterLevel"),
                    SsrFactor = Shader.GetGlobalFloat("_SSRFactor"),
                    MinAmbientColor = Shader.GetGlobalColor("_MinAmbientColor"),
                    TopHorizontSkyColor = Shader.GetGlobalColor("_TopHorizontSkyColor"),
                };

                var names = new List<string>();
                for (var i = 0; i < s.SceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    names.Add($"{scene.name}#{scene.buildIndex}");
                }

                s.SceneNames = string.Join(", ", names);

                try
                {
                    s.NavVertices = NavMesh.CalculateTriangulation().vertices?.Length ?? 0;
                }
                catch (Exception)
                {
                    s.NavVertices = -1;
                }

                return s;
            }

            internal string Describe() =>
                $"{SceneCount} scene(s) [{SceneNames}], active '{ActiveScene}'; LocationScenes {LocationScenes}, door colliders " +
                $"{DoorColliders}; Singleton<LevelSettings> {(LevelSettings != null ? "set" : "not set")}, Singleton<GameWorld> " +
                $"{(GameWorld ? "SET" : "not set")}; lightmaps {Lightmaps}; NavMesh vertices {NavVertices}; terrains {Terrains}; " +
                $"ambient {AmbientMode}, fog {Fog}, skybox '{(Skybox != null ? Skybox.name : "none")}', sun '{(Sun != null ? Sun.name : "none")}'; " +
                $"streaming budget {F(StreamingBudget)} MB / reduction {StreamingReduction} (controller {F(ControllerBudget)}/{ControllerReduction}); " +
                $"texture mip limit {MipLimit}{(SdRuntime ? " (SD mode on)" : "")}; " +
                $"shadow distance {F(ShadowDistance)}; EnvironmentManager {(Environment != null ? "'" + Environment.name + "'" : "none")}.";

            /// <summary>2026-10-03: SD mode's raised mip limit (see <see cref="MipLimit"/>) and its flag put back - the flag first,
            /// since GraphicsSettingsGroup adds one for it whenever the texture quality is next applied. Its own method so the
            /// paths that skip <see cref="Restore"/> (a raid starting, an emergency unload) still run it: a limit left raised
            /// would be the next run's clean-looking baseline.</summary>
            /// <param name="restored">Where to say what was put back, or null.</param>
            internal void RestoreMipLimit(List<string> restored)
            {
                var limit = QualitySettings.globalTextureMipmapLimit;
                var sd = GraphicsSettingsController.ApplySDModeOnRuntime;
                if (limit == MipLimit && sd == SdRuntime) return;

                GraphicsSettingsController.ApplySDModeOnRuntime = SdRuntime;
                QualitySettings.globalTextureMipmapLimit = MipLimit;
                restored?.Add($"texture mip limit {limit}{(sd ? " SD" : "")} -> {MipLimit}{(SdRuntime ? " SD" : "")}");
            }

            internal IEnumerable<string> Differences(MenuState now)
            {
                if (now.SceneCount != SceneCount) yield return $"scene count {SceneCount} -> {now.SceneCount}";
                if (now.SceneNames != SceneNames) yield return $"scenes [{SceneNames}] -> [{now.SceneNames}]";
                if (now.ActiveScene != ActiveScene) yield return $"active scene '{ActiveScene}' -> '{now.ActiveScene}'";
                if (now.LocationScenes != LocationScenes) yield return $"LocationScene.LoadedScenes {LocationScenes} -> {now.LocationScenes}";
                if (now.DoorColliders != DoorColliders) yield return $"LocationScene.DoorsCollisionColliders {DoorColliders} -> {now.DoorColliders}";
                if (!ReferenceEquals(now.LevelSettings, LevelSettings)) yield return "Singleton<LevelSettings> changed";
                if (now.GameWorld != GameWorld) yield return $"Singleton<GameWorld> {GameWorld} -> {now.GameWorld}";
                if (now.Lightmaps != Lightmaps) yield return $"lightmaps {Lightmaps} -> {now.Lightmaps}";
                if (now.Terrains != Terrains) yield return $"active terrains {Terrains} -> {now.Terrains}";
                if (now.NavVertices != NavVertices) yield return $"NavMesh vertices {NavVertices} -> {now.NavVertices}";
                if (now.AmbientMode != AmbientMode || now.AmbientSky != AmbientSky || now.AmbientEquator != AmbientEquator ||
                    now.AmbientGround != AmbientGround || now.AmbientLight != AmbientLight || now.AmbientIntensity != AmbientIntensity)
                    yield return "RenderSettings ambient";
                if (now.Fog != Fog || now.FogColor != FogColor || now.FogDensity != FogDensity || now.FogStart != FogStart ||
                    now.FogEnd != FogEnd || now.FogMode != FogMode)
                    yield return "RenderSettings fog";
                if (!ReferenceEquals(now.Skybox, Skybox)) yield return "RenderSettings skybox";
                if (!ReferenceEquals(now.Sun, Sun)) yield return "RenderSettings sun";
                if (now.StreamingBudget != StreamingBudget || now.StreamingReduction != StreamingReduction)
                    yield return $"texture streaming budget {F(StreamingBudget)}/{StreamingReduction} -> {F(now.StreamingBudget)}/{now.StreamingReduction}";
                if (now.DirectionLightShadow != DirectionLightShadow || now.WaterLevel != WaterLevel || now.SsrFactor != SsrFactor ||
                    now.MinAmbientColor != MinAmbientColor || now.TopHorizontSkyColor != TopHorizontSkyColor)
                    yield return "LevelSettings' shader globals";
                if (now.ShadowDistance != ShadowDistance) yield return $"shadow distance {F(ShadowDistance)} -> {F(now.ShadowDistance)}";
                if (now.ControllerBudget != ControllerBudget || now.ControllerReduction != ControllerReduction)
                    yield return "GraphicsSettingsController's streaming statics";
                if (now.MipLimit != MipLimit || now.SdRuntime != SdRuntime)
                    yield return $"texture mip limit {MipLimit}{(SdRuntime ? " SD" : "")} -> {now.MipLimit}{(now.SdRuntime ? " SD" : "")}";
                if (now.AmbientProbe != AmbientProbe) yield return "RenderSettings.ambientProbe";
                if (!ReferenceEquals(now.Environment, Environment)) yield return "EnvironmentManagerBase._instance";
            }

            /// <summary>Puts back what LevelSettings.Awake and LocationScene.Awake change and their teardown does not. Every
            /// write is said in the log. Never touches Singleton&lt;GameWorld&gt;.</summary>
            internal void Restore(List<string> trouble)
            {
                var now = Take();
                var restored = new List<string>();

                if (now.AmbientMode != AmbientMode || now.AmbientSky != AmbientSky || now.AmbientEquator != AmbientEquator ||
                    now.AmbientGround != AmbientGround || now.AmbientLight != AmbientLight || now.AmbientIntensity != AmbientIntensity)
                {
                    RenderSettings.ambientMode = AmbientMode;
                    RenderSettings.ambientEquatorColor = AmbientEquator;
                    RenderSettings.ambientGroundColor = AmbientGround;
                    RenderSettings.ambientSkyColor = AmbientSky;
                    RenderSettings.ambientLight = AmbientLight;
                    RenderSettings.ambientIntensity = AmbientIntensity;
                    restored.Add("ambient");
                }

                if (now.Fog != Fog || now.FogColor != FogColor || now.FogDensity != FogDensity || now.FogStart != FogStart ||
                    now.FogEnd != FogEnd || now.FogMode != FogMode)
                {
                    RenderSettings.fog = Fog;
                    RenderSettings.fogColor = FogColor;
                    RenderSettings.fogDensity = FogDensity;
                    RenderSettings.fogStartDistance = FogStart;
                    RenderSettings.fogEndDistance = FogEnd;
                    RenderSettings.fogMode = FogMode;
                    restored.Add("fog");
                }

                if (!ReferenceEquals(now.Skybox, Skybox))
                {
                    RenderSettings.skybox = Skybox;
                    restored.Add("skybox");
                }

                if (!ReferenceEquals(now.Sun, Sun))
                {
                    RenderSettings.sun = Sun;
                    restored.Add("sun");
                }

                if (now.StreamingBudget != StreamingBudget || now.StreamingReduction != StreamingReduction)
                {
                    QualitySettings.streamingMipmapsMemoryBudget = StreamingBudget;
                    QualitySettings.streamingMipmapsMaxLevelReduction = StreamingReduction;
                    restored.Add("texture streaming budget");
                }

                if (now.DirectionLightShadow != DirectionLightShadow || now.WaterLevel != WaterLevel || now.SsrFactor != SsrFactor ||
                    now.MinAmbientColor != MinAmbientColor || now.TopHorizontSkyColor != TopHorizontSkyColor)
                {
                    Shader.SetGlobalFloat("_DirectionLightShadow", DirectionLightShadow);
                    Shader.SetGlobalFloat("_WaterLevel", WaterLevel);
                    Shader.SetGlobalFloat("_SSRFactor", SsrFactor);
                    Shader.SetGlobalColor("_MinAmbientColor", MinAmbientColor);
                    Shader.SetGlobalColor("_TopHorizontSkyColor", TopHorizontSkyColor);
                    restored.Add("shader globals");
                }

                if (now.ShadowDistance != ShadowDistance)
                {
                    QualitySettings.shadowDistance = ShadowDistance;
                    restored.Add("shadow distance");
                }

                if (now.ControllerBudget != ControllerBudget || now.ControllerReduction != ControllerReduction)
                {
                    GraphicsSettingsController.MipStreamingMemoryBudget = ControllerBudget;
                    GraphicsSettingsController.StreamingMipmapsMaxLevelReduction = ControllerReduction;
                    restored.Add("GraphicsSettingsController's streaming statics");
                }

                RestoreMipLimit(restored);

                if (now.AmbientProbe != AmbientProbe)
                {
                    RenderSettings.ambientProbe = AmbientProbe;
                    restored.Add("ambient probe");
                }

                // A map's EnvironmentManager nulls the protected static _instance on destroy whatever it holds. Put back a
                // live one that was there before; drop a destroyed one. Reached by the field's type, never its name.
                if (!ReferenceEquals(now.Environment, Environment))
                {
                    var field = typeof(EnvironmentManagerBase)
                        .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .FirstOrDefault(f => f.FieldType == typeof(EnvironmentManagerBase));

                    if (field == null)
                    {
                        trouble.Add("EnvironmentManagerBase's instance field could not be found to restore");
                    }
                    else if (Environment != null)
                    {
                        field.SetValue(null, Environment);
                        restored.Add("EnvironmentManager instance (the menu's own)");
                    }
                    else if (now.Environment == null)
                    {
                        field.SetValue(null, null);
                        restored.Add("EnvironmentManager instance (released a destroyed one)");
                    }
                    else
                    {
                        trouble.Add("a live EnvironmentManager from the probed scenes is still the instance");
                    }
                }

                // A map's LevelSettings releases the singleton on destroy WHATEVER it holds, so a menu that had one of its own
                // loses it. Put back only a live object that was there before; drop only a destroyed one.
                if (!ReferenceEquals(now.LevelSettings, LevelSettings))
                {
                    if (LevelSettings != null)
                    {
                        Singleton<LevelSettings>.Create(LevelSettings);
                        restored.Add("Singleton<LevelSettings> (the menu's own)");
                    }
                    else if (now.LevelSettings == null)
                    {
                        // Unity-null (destroyed) but still referenced.
                        Singleton<LevelSettings>.Release(now.LevelSettings);
                        restored.Add("Singleton<LevelSettings> (released a destroyed one)");
                    }
                    else
                    {
                        trouble.Add("a live LevelSettings from the probed scenes is still the singleton");
                    }
                }

                // LocationScene.Awake adds a door's colliders to this static list and nothing removes them; unloaded, they
                // are destroyed entries. Only destroyed ones are removed.
                var removed = LocationScene.DoorsCollisionColliders.RemoveAll(c => c == null);
                if (removed > 0) restored.Add($"{removed} destroyed door collider(s) removed from LocationScene.DoorsCollisionColliders");

                Log(restored.Count == 0 ? "restore: nothing needed putting back." : $"restore: put back {string.Join(", ", restored)}.");
            }
        }

        // --- the checks --------------------------------------------------------------------------------------------------

        /// <summary>The main menu, strictly: no raid, no raid loading, no hideout, no capture. Reads only.</summary>
        internal static bool InMenu(out string why)
        {
            why = null;

            try
            {
                // A HideoutGameWorld on its own is allowed (it can outlive a hideout visit); the hideout itself is refused by
                // its scene. Any other GameWorld is a raid or a raid loading.
                var worlds = UnityEngine.Object.FindObjectsOfType<GameWorld>();
                var raidWorld = worlds.FirstOrDefault(w => w != null && !(w is HideoutGameWorld));
                var singleton = Singleton<GameWorld>.Instance;
                var screen = CurrentScreen();

                if (MeshProbe.RaidWatchers > 0) why = "in a raid (a raid watcher is alive)";
                else if (singleton != null && !(singleton is HideoutGameWorld))
                    why = $"a raid GameWorld is the singleton ({singleton.GetType().Name})";
                else if (raidWorld != null) why = $"a raid GameWorld object exists ({raidWorld.GetType().Name}; a raid is loading)";
                else if (IsLoaded(SceneManager.GetSceneByName(EFT.Scenes.HideoutSceneName))) why = "the hideout scene (bunker_2) is loaded";
                else if (MapCapture.IsCapturing) why = "a map capture is running";
                else if (screen != EEftScreenType.MainMenu.ToString())
                    why = $"the current screen is {screen}, not the main menu - go back to the main menu screen first";
            }
            catch (Exception ex)
            {
                // A test that cannot be made is a refusal here: loading a map into a raid is the one outcome to avoid.
                why = $"the menu test itself failed ({ex.GetType().Name}: {ex.Message})";
            }

            return why == null;
        }

        /// <summary>The current EFT screen's type as text, or "unknown". Read through the manager's static field so a missing
        /// manager is not created here.</summary>
        internal static string CurrentScreen()
        {
            try
            {
                var manager = EftScreenManager._instance;
                var controller = manager?.CurrentScreenController;
                return controller == null ? "unknown (no screen)" : controller.ScreenType.ToString();
            }
            catch (Exception ex)
            {
                return $"unknown ({ex.GetType().Name})";
            }
        }

        /// <summary>The cheap per-frame half of <see cref="InMenu"/>, while a scene loads: a raid GameWorld or raid watcher.</summary>
        internal static bool RaidStarting()
        {
            try
            {
                var singleton = Singleton<GameWorld>.Instance;
                return MeshProbe.RaidWatchers > 0 || (singleton != null && !(singleton is HideoutGameWorld));
            }
            catch (Exception)
            {
                return true;
            }
        }

        internal static bool IsLoaded(Scene scene) => scene.IsValid() && scene.isLoaded;

        // --- the DontDestroyOnLoad diff (log only) ------------------------------------------------------------------------

        /// <summary>The DontDestroyOnLoad scene's roots before the run, by instance id - see <see cref="DdolRoots"/>. Cleared
        /// when a run takes the slot, so a run whose baseline failed never compares against an older run's.</summary>
        private static HashSet<int> _ddolBefore;

        /// <summary>Takes the DontDestroyOnLoad baseline and returns how many roots it holds.</summary>
        internal static int TakeDdolBaseline()
        {
            _ddolBefore = new HashSet<int>(DdolRoots().Select(g => g.GetInstanceID()));
            return _ddolBefore.Count;
        }

        /// <summary>The DontDestroyOnLoad scene's root objects, reached through a throwaway object of our own that is moved
        /// there and destroyed again at once.</summary>
        private static List<GameObject> DdolRoots()
        {
            var marker = new GameObject("QuestTreeMenuSceneProbeMarker");
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(marker);
                return marker.scene.GetRootGameObjects().Where(g => g != null && g != marker).ToList();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        /// <summary>Logs every DontDestroyOnLoad root that appeared during the run - NEVER destroys one: the game parks icon
        /// cameras, light pools, the physics overlap system and factory objects there at runtime - and advises a restart
        /// when any appeared. Clears destroyed entries from MineDirectional.Mines, a static list its mines add themselves to
        /// and never leave.</summary>
        internal static void SweepDdol(List<string> trouble)
        {
            if (_ddolBefore == null)
            {
                trouble.Add("no DontDestroyOnLoad baseline was taken, so its leaks could not be checked");
                return;
            }

            var added = DdolRoots()
                .Where(g => !_ddolBefore.Contains(g.GetInstanceID()) && !g.name.StartsWith("QuestTree", StringComparison.Ordinal))
                .ToList();

            if (added.Count == 0)
            {
                Log("DontDestroyOnLoad: no new roots.");
            }
            else
            {
                Log($"DontDestroyOnLoad: {added.Count} new root(s) appeared during the run - left in place, not destroyed.");

                foreach (var g in added.Take(20))
                {
                    var types = g.GetComponents<Component>()
                        .Select(c => c == null ? "(missing script)" : c.GetType().Name)
                        .Distinct();
                    Log($"DontDestroyOnLoad new root '{g.name}': {string.Join(", ", types)}.");
                }

                trouble.Add($"{added.Count} new DontDestroyOnLoad root(s) appeared during the run (listed above)");
            }

            var mines = MineDirectional.Mines.RemoveAll(m => m == null);
            Log($"MineDirectional.Mines: removed {mines} destroyed entries, {MineDirectional.Mines.Count} left.");
        }

        // --- the log capture ---------------------------------------------------------------------------------------------

        private static bool _capturing;
        private static bool _inHandler;
        private static int _exceptions;
        private static int _errors;
        private static int _asserts;
        private static int _warnings;
        private static int _plainLogs;
        private static int _ownFailures;
        private static readonly Dictionary<string, int> MessageCounts = new Dictionary<string, int>();
        private static readonly List<string> MessageOrder = new List<string>();
        private static readonly Dictionary<string, string> MessageFrames = new Dictionary<string, string>();
        private static readonly HashSet<string> AllDistinct = new HashSet<string>();

        internal static void StartCapture()
        {
            _exceptions = _errors = _asserts = _warnings = _plainLogs = _ownFailures = 0;
            MessageCounts.Clear();
            MessageOrder.Clear();
            MessageFrames.Clear();
            AllDistinct.Clear();

            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            _capturing = true;
        }

        internal static void StopCapture()
        {
            _capturing = false;

            try
            {
                Application.logMessageReceived -= OnLog;
            }
            catch (Exception)
            {
                // Nothing to do: the handler is inert once _capturing is false.
            }
        }

        /// <summary>Counts every message Unity logs during a run and keeps the first 20 distinct warnings, errors, asserts and
        /// exceptions. It never logs itself (no re-entry) and never throws.</summary>
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!_capturing || _inHandler) return;
            _inHandler = true;

            try
            {
                if (type == LogType.Log)
                {
                    _plainLogs++;
                    return;
                }

                if (condition != null && condition.StartsWith("QuestTree", StringComparison.Ordinal)) return;

                switch (type)
                {
                    case LogType.Exception: _exceptions++; break;
                    case LogType.Error: _errors++; break;
                    case LogType.Assert: _asserts++; break;
                    default: _warnings++; break;
                }

                var text = (condition ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (text.Length > 300) text = text.Substring(0, 300) + "...";
                var key = $"{type}: {text}";

                if (AllDistinct.Count < 10000) AllDistinct.Add(key);

                if (MessageCounts.TryGetValue(key, out var n))
                {
                    MessageCounts[key] = n + 1;
                }
                else if (MessageOrder.Count < MaxDistinctMessages)
                {
                    MessageCounts[key] = 1;
                    MessageOrder.Add(key);
                    MessageFrames[key] = FirstFrame(stackTrace);
                }
            }
            catch (Exception)
            {
                // A log handler that throws would be re-entered by Unity's own report of it.
            }
            finally
            {
                _inHandler = false;
            }
        }

        internal static int[] ErrorCounts() => new[] { _exceptions, _errors, _asserts, _warnings };

        /// <summary>The run's message summary; <paramref name="ownSteps"/> names whose steps the failure count is of ("the
        /// probe's own steps").</summary>
        internal static void LogMessages(string ownSteps)
        {
            Log($"messages over the run: {_exceptions} exceptions, {_errors} errors, {_asserts} asserts, {_warnings} warnings, " +
                $"{_plainLogs} plain logs; {AllDistinct.Count} distinct non-plain; {_ownFailures} failure(s) in {ownSteps}.");

            for (var i = 0; i < MessageOrder.Count; i++)
            {
                var key = MessageOrder[i];
                MessageFrames.TryGetValue(key, out var frame);
                Log($"message {i + 1} (x{MessageCounts[key]}): {key}" + (string.IsNullOrEmpty(frame) ? "" : $" @ {frame}"));
            }
        }

        // --- helpers -----------------------------------------------------------------------------------------------------

        /// <summary>Runs one step, counting and logging a throw. False when it threw.</summary>
        internal static bool Try(string what, Action work)
        {
            try
            {
                work();
                return true;
            }
            catch (Exception ex)
            {
                _ownFailures++;
                Plugin.LogSource?.LogWarning($"{_voice}{what} threw {ex.GetType().Name}: {ex.Message} at {FirstFrame(ex.StackTrace)}");
                return false;
            }
        }

        internal static int SafeCount(Func<int> count)
        {
            try
            {
                return count();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        internal static List<Camera> LiveCameras() =>
            Camera.allCameras.Where(c => c != null && c.enabled && c.gameObject.activeInHierarchy).ToList();

        internal static string Memory()
        {
            long heap = -1, allocated = -1, reserved = -1, priv = -1;

            try { heap = GC.GetTotalMemory(false); } catch (Exception) { }
            try { allocated = Profiler.GetTotalAllocatedMemoryLong(); } catch (Exception) { }
            try { reserved = Profiler.GetTotalReservedMemoryLong(); } catch (Exception) { }

            try
            {
                using (var process = Process.GetCurrentProcess()) priv = process.PrivateMemorySize64;
            }
            catch (Exception)
            {
                // Not every runtime can read it; -1 says so.
            }

            return $"memory: managed heap {MB(heap)} MB, Unity allocated {MB(allocated)} MB, Unity reserved {MB(reserved)} MB, " +
                   $"process private {MB(priv)} MB";
        }

        private static string MB(long bytes) =>
            bytes < 0 ? "?" : (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);

        internal static string F(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        private static bool Finite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        private static float Pct(List<float> values, float share)
        {
            values.Sort();
            var i = (int)Math.Round(share * (values.Count - 1));
            return values[Math.Max(0, Math.Min(values.Count - 1, i))];
        }

        internal static string FirstFrame(string stackTrace) =>
            (stackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        /// <summary>A line in the current run owner's voice (the probe's tag while it runs, the host's otherwise).</summary>
        private static void Log(string text) => Plugin.LogSource?.LogInfo(_voice + text);
    }
}
