using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Track T, stage T0: a READ-ONLY diagnostic of one map's data, run in the main menu. Started by the Advanced setting
    /// "Run map data probe (main menu)" (ModSettings.MapDataProbeLocation, a location id; empty = off), which it clears
    /// BEFORE it starts, so no launch ever starts a run by itself. It hosts the map through MenuMapHost (whose restore puts
    /// the menu back and compares the state), measures what the 3D map's real textures would need - texture reach and read
    /// time, the terrain and its MicroSplat material, decals and roads, trees, normal maps, a viewer-style directional light
    /// with shadows, memory - and writes BepInEx/plugins/QuestTree/probe/mapdata-&lt;location&gt;-&lt;time&gt;.json plus one
    /// summary line.
    ///
    /// Safety: it never WAKES the hosted scenes (no activation, no renderer enable - every survey includes inactive objects
    /// instead), never writes a property of a game material, texture, mesh, terrain or QualitySettings, reads materials and
    /// meshes only through sharedMaterials / sharedMesh (never the instancing getters), never sets a mesh buffer target
    /// (memory never-write-mesh-buffer-targets), and destroys everything it creates. It holds no reference to a game object
    /// past its work, so the host's asset sweep is not kept from freeing anything. Every measurement is guarded on its own
    /// and records its error.
    ///
    /// Started and finished from TrackerHotkey.Update (memory ddol-objects-dead-in-menu). Cancelled by setting the setting
    /// again while it runs; a raid starting stops it through the host.
    /// </summary>
    internal static class MapDataProbe
    {
        private const string Tag = "QuestTree: map data probe: ";

        private const int TextureCap = 1024;
        private const int TextureSamples = 50;
        private const int NormalMaterialSamples = 200;
        private const int NormalReadSamples = 20;
        private const int MaxFloatProps = 200;
        private const int MaxTerrainsDetailed = 16;
        private const int MaxFields = 120;
        private const int AfterFrames = 10;

        /// <summary>GetAlphamaps whole only up to this many bytes of floats; past it a square window of at most this many
        /// bytes is read. Kept small: Mono's heap grows for the array and never gives the memory back.</summary>
        private const long AlphamapFullBytesMax = 64L * 1024 * 1024;

        /// <summary>The builder's main-texture property list (MapMeshBuilder.TextureProperties), copied.</summary>
        private static readonly string[] TextureProperties =
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_MainTex0", "_Albedo", "_AlbedoMap", "_AlbedoTex", "_Diffuse",
            "_DiffuseMap", "_BaseAlbedoASmoothness", "_MainTexture", "_BaseTex", "_ColorMap"
        };

        private static readonly Regex MainLike = new Regex("(main|albedo|diffuse|base|color)", RegexOptions.IgnoreCase);

        private static readonly Regex NotMain = new Regex(
            "(bump|normal|nrm|spec|gloss|rough|metal|mask|detail|noise|height|occl|ao|emiss|light|dudv|ramp|blur|depth|flow|mip)",
            RegexOptions.IgnoreCase);

        private static readonly string[] NormalProperties = { "_BumpMap", "_NormalMap", "_Normal" };

        private static readonly Regex NormalLike = new Regex("(norm|bump)", RegexOptions.IgnoreCase);

        /// <summary>A float/vector property whose name suggests a per-layer scale, tiling or tint.</summary>
        private static readonly Regex LayerLike = new Regex(
            "(scale|tile|tiling|uv|tint|color|colour|layer|splat|offset|\\d)", RegexOptions.IgnoreCase);

        /// <summary>A tree shader: SpeedTree, or "tree" not preceded by an "s" (so not "street").</summary>
        private static readonly Regex TreeShader = new Regex("(speedtree|(^|[^s])tree)", RegexOptions.IgnoreCase);

        private static readonly HashSet<string> EffectRenderers = new HashSet<string>
        {
            "ParticleSystemRenderer", "TrailRenderer", "LineRenderer", "VFXRenderer",
        };

        private static readonly string[] LightShaders = { "Standard", "Legacy Shaders/Diffuse", "Diffuse" };

        // --- the run's state (numbers and strings only: no reference to a game object outlives the work) ----------------

        private sealed class RunState
        {
            internal string Location;
            internal Dictionary<string, object> Report;
            internal List<string> Errors;
            internal Dictionary<string, object> Memory;
            internal Stopwatch Clock;
            internal Stopwatch HostClock;
            internal int Claim;
            internal bool Hosting;
            internal int EndFrame = -1;
            internal string Cancel;
            internal HashSet<int> TexturesBeforeIds;
            internal HashSet<string> TexturesBeforeNames;
        }

        private static RunState _run;

        /// <summary>ReadOne's byte buffer, reused across reads; dropped when the report is written.</summary>
        private static byte[] _readBuffer;
        private static int _handledFrame = -1;
        private static bool _warned;

        /// <summary>A run is going (from its start until its report is written).</summary>
        internal static bool Running => _run != null;

        /// <summary>From TrackerHotkey.Update, every frame: the start switch, the cancel and the run's end. Never throws.</summary>
        internal static void Poll(MonoBehaviour host)
        {
            if (host == null || _handledFrame == Time.frameCount) return;
            _handledFrame = Time.frameCount;

            try
            {
                if (!ModSettings.Ready || ModSettings.MapDataProbeLocation == null) return;

                var run = _run;
                if (run != null)
                {
                    PollRunning(run);
                    return;
                }

                var location = ModSettings.MapDataProbeLocation.Value?.Trim();
                if (string.IsNullOrEmpty(location)) return;

                // Off FIRST, before anything can throw or crash the game, so no launch ever starts a run by itself.
                ModSettings.MapDataProbeLocation.Value = "";
                Start(host, location);
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"{Tag}the poll failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static void PollRunning(RunState run)
        {
            // The setting set again while a run goes is the cancel.
            var again = ModSettings.MapDataProbeLocation.Value?.Trim();
            if (!string.IsNullOrEmpty(again))
            {
                ModSettings.MapDataProbeLocation.Value = "";

                if (run.Cancel == null)
                {
                    run.Cancel = "the setting was set again";
                    if (run.Claim != 0 && MenuMapHost.CurrentClaim == run.Claim) MenuMapHost.RequestStop("map data probe: cancelled");
                    Plugin.LogSource?.LogInfo($"{Tag}cancel asked - the measurement stops and the map is unloaded.");
                }
            }

            if (run.Hosting)
            {
                if (MenuMapHost.Busy && MenuMapHost.CurrentClaim == run.Claim) return;

                // The host's run is over (unloaded and restored, or found dead): give VRAM's fetch a few frames.
                run.Hosting = false;
                run.EndFrame = Time.frameCount;
                VramProbe.TryRead(out _, out _);
                // Review B24: the Maps tab's 3D guard reads MenuMapHost.Busy, which no setting change announces - the tab
                // is redrawn so its view comes back in 3D now the slot is free.
                ModSettings.RequestRepaint();
                return;
            }

            if (run.EndFrame >= 0 && Time.frameCount - run.EndFrame < AfterFrames) return;

            Finish(run);
        }

        // --- start --------------------------------------------------------------------------------------------------------

        private static void Start(MonoBehaviour host, string location)
        {
            var run = new RunState
            {
                Location = location,
                Clock = Stopwatch.StartNew(),
                Errors = new List<string>(),
                Memory = new Dictionary<string, object>(),
                Report = new Dictionary<string, object>(),
            };

            var started = DateTime.Now;
            run.Report["probe"] = "mapdata (stage T0, read-only)";
            run.Report["client"] = Version();
            run.Report["location"] = location;
            run.Report["startedLocal"] = started.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            run.Report["unity"] = Application.unityVersion;
            run.Report["gpu"] = Safe(() => $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB)");
            run.Report["colorSpace"] = Safe(() => QualitySettings.activeColorSpace.ToString());
            run.Report["memory"] = run.Memory;
            run.Report["errors"] = run.Errors;
            _run = run;

            string why = null;
            try
            {
                if (ModEnvironment.IsHeadlessClient) why = "a headless client has no main menu";
                else if (SelfTest.Running) why = "the self-test is running";
                else if (MenuCaptureRunner.Running) why = "a capture from game files is running";
                else MenuCaptureRunner.CanStart(location, out why);
            }
            catch (Exception ex)
            {
                why = $"the check threw {ex.GetType().Name}";
            }

            if (why != null)
            {
                run.Report["overall"] = "refused";
                run.Report["refusal"] = why;
                Finish(run);
                return;
            }

            run.Memory["before"] = MemoryNow();
            run.Report["mipLimitBefore"] = QualitySettings.globalTextureMipmapLimit;
            run.Report["textureStreamingActive"] = QualitySettings.streamingMipmapsActive;

            // 6. The viewer's lighting, in the plain menu (the context the viewer draws in), before anything is hosted.
            Measure(run, "lighting", LightTest);

            // 1 (part). What the game already holds before hosting: ids and names only, never the textures themselves.
            Measure(run, "texturesBeforeHosting", s =>
            {
                var clock = Stopwatch.StartNew();
                var all = Resources.FindObjectsOfTypeAll<Texture2D>();
                run.TexturesBeforeIds = new HashSet<int>();
                run.TexturesBeforeNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var t in all)
                {
                    if (t == null) continue;
                    run.TexturesBeforeIds.Add(t.GetInstanceID());
                    if (!string.IsNullOrEmpty(t.name)) run.TexturesBeforeNames.Add(t.name);
                }

                s["texture2DCount"] = run.TexturesBeforeIds.Count;
                s["distinctNames"] = run.TexturesBeforeNames.Count;
                s["ms"] = Ms(clock);
            });

            run.HostClock = Stopwatch.StartNew();

            string refusal;
            int claim;
            bool startedHost;
            try
            {
                startedHost = MenuMapHost.Start(host, location, () => Work(run), out refusal, out claim);
            }
            catch (Exception ex)
            {
                startedHost = false;
                claim = 0;
                refusal = $"the host's start threw {ex.GetType().Name}: {ex.Message}";
            }

            if (!startedHost)
            {
                run.Report["overall"] = "refused";
                run.Report["refusal"] = refusal ?? "the host did not start";
                Finish(run);
                return;
            }

            run.Claim = claim;
            run.Hosting = true;
            // Review B24: the Maps tab redraws now, so its 3D guard (MenuMapHost.Busy) takes the view down while the map
            // is hosted, rather than on some later unrelated repaint.
            ModSettings.RequestRepaint();
            Plugin.LogSource?.LogInfo($"{Tag}hosting '{location}' to measure it (read-only). Do not start a raid or open the hideout until its summary line.");
        }

        // --- the work while the map is hosted -----------------------------------------------------------------------------

        private static IEnumerator Work(RunState run)
        {
            var host = new Dictionary<string, object>();
            run.Report["host"] = host;
            host["startToWorkMs"] = run.HostClock == null ? -1d : Ms(run.HostClock);
            host["startToWorkNote"] = "from MenuMapHost.Start to the first step of the work: the scene list's resolve and every scene's load";

            Survey survey = null;
            Texture2D scratch = null;

            try
            {
                Measure(run, "scenes", s =>
                {
                    var scenes = MenuMapHost.HostedScenes();
                    s["count"] = scenes.Count;
                    s["names"] = scenes.Select(x => x.name).ToList();
                });

                // 7. Memory while hosted: a fetch, three frames for the render thread, then the read.
                VramProbe.TryRead(out _, out _);
                yield return null;
                yield return null;
                yield return null;
                run.Memory["hosted"] = MemoryNow();

                Measure(run, "survey", s =>
                {
                    var clock = Stopwatch.StartNew();
                    survey = Survey.Take(MenuMapHost.HostedScenes());
                    s["renderers"] = survey.Renderers.Count;
                    s["monoBehaviours"] = survey.Behaviours.Count;
                    s["terrains"] = survey.Terrains.Count;
                    s["inactiveRenderers"] = survey.Renderers.Count(r => r != null && !r.gameObject.activeInHierarchy);
                    s["disabledRenderers"] = survey.Renderers.Count(r => r != null && !r.enabled);
                    s["note"] = "every component of the hosted scenes, inactive included: the scenes are not woken";
                    s["ms"] = Ms(clock);
                });
                yield return null;

                if (survey == null)
                {
                    run.Errors.Add("no survey - nothing else could be measured");
                    yield break;
                }

                scratch = new Texture2D(TextureCap, TextureCap, TextureFormat.RGBA32, false, false) { name = "QuestTreeMapDataProbe-scratch" };

                var sc = scratch;
                Measure(run, "textures", s => Textures(run, survey, sc, s));
                yield return null;

                Measure(run, "terrain", s => TerrainSection(survey, sc, s));
                yield return null;

                Measure(run, "decals", s => Decals(survey, s));
                yield return null;

                Measure(run, "trees", s => Trees(survey, s));
                yield return null;

                Measure(run, "normalMaps", s => NormalMaps(survey, sc, s));
                yield return null;

                VramProbe.TryRead(out _, out _);
                yield return null;
                yield return null;
                yield return null;
                run.Memory["hostedEnd"] = MemoryNow();
                host["elapsedAtWorkEndMs"] = Ms(run.Clock);
                host["mipLimitWhileHosted"] = QualitySettings.globalTextureMipmapLimit;
            }
            finally
            {
                survey?.Clear();
                survey = null;

                if (scratch != null)
                {
                    try { UnityEngine.Object.Destroy(scratch); } catch (Exception) { }
                }
            }
        }

        // --- the end ------------------------------------------------------------------------------------------------------

        private static void Finish(RunState run)
        {
            string path = null;

            try
            {
                if (run.Claim != 0)
                {
                    var outcome = MenuMapHost.LastOutcome;
                    if (outcome != null && outcome.Claim != run.Claim) outcome = null;

                    var host = run.Report.TryGetValue("host", out var h) && h is Dictionary<string, object> d ? d : new Dictionary<string, object>();
                    run.Report["host"] = host;
                    host["claim"] = run.Claim;
                    host["outcomeFound"] = outcome != null;
                    host["problem"] = outcome?.Problem;
                    host["trouble"] = outcome?.Trouble;
                    host["seconds"] = outcome == null ? -1d : Math.Round(outcome.Seconds, 1);
                    host["stateMatches"] = outcome != null && outcome.Trouble == null;
                    host["restartAdvised"] = MenuMapHost.RestartAdvised;

                    run.Memory["after"] = MemoryNow();

                    if (!run.Report.ContainsKey("overall"))
                        run.Report["overall"] = run.Cancel != null ? "cancelled"
                            : outcome == null ? "unknown (no host verdict)"
                            : outcome.Problem != null ? "stopped" : "done";
                }

                if (run.Cancel != null) run.Report["cancel"] = run.Cancel;

                var finished = DateTime.Now;
                run.Report["finishedLocal"] = finished.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                run.Report["seconds"] = Math.Round(run.Clock.Elapsed.TotalSeconds, 1);

                var dir = Path.Combine(Path.GetDirectoryName(typeof(MapDataProbe).Assembly.Location) ?? "", "probe");
                Directory.CreateDirectory(dir);
                var file = $"mapdata-{FileSafe(run.Location)}-{finished.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
                path = Path.Combine(dir, file);
                File.WriteAllText(path, JsonConvert.SerializeObject(run.Report, Formatting.Indented,
                    new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore }));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"{Tag}the report could not be written ({ex.GetType().Name}: {ex.Message}).");
                path = null;
            }
            finally
            {
                _run = null;
                _readBuffer = null;
            }

            try
            {
                Plugin.LogSource?.LogInfo($"{Tag}{run.Location} - {Summary(run)}; {run.Errors.Count} measurement error(s); report {path ?? "NOT WRITTEN"}.");
            }
            catch (Exception)
            {
                // The line is the last thing.
            }
        }

        private static string Summary(RunState run)
        {
            var r = run.Report;
            var overall = r.TryGetValue("overall", out var o) ? o as string : "?";
            if (overall == "refused") return $"refused ({r["refusal"]}); nothing was loaded";

            string Get(string section, string key)
            {
                if (!r.TryGetValue(section, out var s) || !(s is Dictionary<string, object> d)) return "-";
                return d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "-";
            }

            return $"{overall}: {Get("textures", "materials")} materials, {Get("textures", "mainTextures")} main textures " +
                   $"({Get("textures", "msPerTexture")} ms each at {TextureCap} px, full map about {Get("textures", "extrapolatedFullMapSeconds")} s); " +
                   $"{Get("terrain", "count")} terrain(s); {Get("decals", "decalShaderRenderers")} decal-shader renderers; " +
                   $"{Get("trees", "renderers")} tree renderers, {Get("trees", "distinctMeshes")} meshes; " +
                   $"shadow darker: {Get("lighting", "shadowDarker")}; host state matches: {Get("host", "stateMatches")}; " +
                   $"scenes not woken ({Get("textures", "streamingWithMipsDropped")} streamed texture(s) at a lower mip): sample " +
                   "content and means are not representative, timings are";
        }

        // --- the survey ---------------------------------------------------------------------------------------------------

        /// <summary>Every renderer, behaviour and terrain of the hosted scenes, inactive included. Cleared at the work's end.</summary>
        private sealed class Survey
        {
            internal List<Renderer> Renderers = new List<Renderer>();
            internal List<MonoBehaviour> Behaviours = new List<MonoBehaviour>();
            internal List<Terrain> Terrains = new List<Terrain>();

            internal static Survey Take(List<Scene> scenes)
            {
                var s = new Survey();

                foreach (var scene in scenes)
                {
                    if (!scene.IsValid() || !scene.isLoaded) continue;

                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root == null) continue;
                        s.Renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));
                        s.Behaviours.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true).Where(b => b != null));
                        s.Terrains.AddRange(root.GetComponentsInChildren<Terrain>(true));
                    }
                }

                return s;
            }

            internal void Clear()
            {
                Renderers = null;
                Behaviours = null;
                Terrains = null;
            }
        }

        // --- 1. textures --------------------------------------------------------------------------------------------------

        private static void Textures(RunState run, Survey survey, Texture2D scratch, Dictionary<string, object> s)
        {
            var mask = MapCapture.MenuCaptureMask();
            s["captureMask"] = mask;

            var materials = new HashSet<Material>();
            var renderersOnLayers = 0;

            foreach (var r in survey.Renderers)
            {
                if (r == null || (mask & (1 << r.gameObject.layer)) == 0 || EffectRenderers.Contains(r.GetType().Name)) continue;
                renderersOnLayers++;
                foreach (var m in r.sharedMaterials)
                    if (m != null) materials.Add(m);
            }

            s["renderersOnCaptureLayers"] = renderersOnLayers;
            s["materials"] = materials.Count;

            var textures = new Dictionary<Texture, int>();
            var props = new Dictionary<string, int>();
            var noTexture = 0;

            foreach (var m in materials)
            {
                var t = MainTexture(m, out var prop);
                if (t == null)
                {
                    noTexture++;
                    continue;
                }

                Bump(props, prop);
                textures[t] = textures.TryGetValue(t, out var n) ? n + 1 : 1;
            }

            s["materialsWithoutMainTexture"] = noTexture;
            s["mainTextureProperties"] = props;
            s["mainTextures"] = textures.Count;

            var sizes = new Dictionary<string, int>();
            var sides = new Dictionary<string, int>();
            var formats = new Dictionary<string, int>();
            var dims = new Dictionary<string, int>();
            int readable = 0, over1024 = 0, streaming = 0, streamingReduced = 0;
            long bytesAtCap = 0;

            foreach (var t in textures.Keys)
            {
                Bump(sizes, $"{t.width}x{t.height}");
                Bump(sides, SideBucket(Math.Max(t.width, t.height)));
                Bump(formats, Format(t));
                Bump(dims, t.dimension.ToString());
                if (t.isReadable) readable++;
                if (Math.Max(t.width, t.height) > TextureCap) over1024++;

                if (t is Texture2D t2 && t2.streamingMipmaps)
                {
                    streaming++;
                    if (t2.loadedMipmapLevel > 0) streamingReduced++;
                }

                Capped(t.width, t.height, TextureCap, out var cw, out var ch);
                bytesAtCap += (long)cw * ch * 4;
            }

            s["sizeHistogram"] = Top(sizes, 40);
            s["maxSideHistogram"] = sides;
            s["formatHistogram"] = formats;
            s["dimensionHistogram"] = dims;
            s["readable"] = readable;
            s["over1024"] = over1024;
            s["streamingMipmaps"] = streaming;
            s["streamingWithMipsDropped"] = streamingReduced;
            s["notWokenNote"] = "the hosted scenes are never woken, so streamed textures sit at a low mip: sample sizes' content and " +
                                "means are NOT representative of full quality; the read timings are";
            s["rgbaBytesAtCap"] = bytesAtCap;

            // Reach without loading: the same objects, or the same names, held before the map was hosted.
            if (run.TexturesBeforeIds != null)
            {
                s["heldBeforeHosting"] = textures.Keys.Count(t => run.TexturesBeforeIds.Contains(t.GetInstanceID()));
                s["heldBeforeHostingNote"] = "headline: the same texture object was already loaded before the map was hosted";
                s["heldBeforeHostingSameNameWeak"] = textures.Keys.Count(t => !string.IsNullOrEmpty(t.name) && run.TexturesBeforeNames.Contains(t.name));
                s["sameNameNote"] = "WEAK evidence: a name match may be a different texture that shares a common name";
            }

            // Timing: N spread samples, blit and read back at the cap, as the builder's ReadTexture does.
            var flat = textures.Keys.Where(t => t.dimension == TextureDimension.Tex2D)
                .OrderBy(t => t.GetInstanceID()).ToList();
            var picks = Spread(flat, TextureSamples);
            var timings = new List<double>();
            var samples = new List<object>();
            long bytes = 0;

            foreach (var t in picks)
            {
                var one = new Dictionary<string, object>
                {
                    ["name"] = t.name,
                    ["size"] = $"{t.width}x{t.height}",
                    ["format"] = Format(t),
                };

                try
                {
                    if (t is Texture2D t2)
                    {
                        one["streaming"] = t2.streamingMipmaps;
                        one["loadedMip"] = t2.loadedMipmapLevel;
                        one["mips"] = t2.mipmapCount;
                    }

                    var ms = ReadOne(t, TextureCap, scratch, out var b, out var mean);
                    timings.Add(ms);
                    bytes += b;
                    one["ms"] = Math.Round(ms, 2);
                    one["mean"] = mean;
                }
                catch (Exception ex)
                {
                    one["error"] = $"{ex.GetType().Name}: {ex.Message}";
                }

                samples.Add(one);
            }

            s["readSamples"] = samples;
            s["readCount"] = timings.Count;
            s["readTotalMs"] = Math.Round(timings.Sum(), 1);
            s["readBytes"] = bytes;
            s["firstReadMs"] = timings.Count > 0 ? Math.Round(timings[0], 2) : -1d;

            var warm = timings.Skip(1).ToList();
            var per = warm.Count > 0 ? warm.Average() : timings.Count > 0 ? timings[0] : -1d;
            s["msPerTexture"] = Math.Round(per, 2);
            s["medianMs"] = timings.Count > 0 ? Math.Round(timings.OrderBy(x => x).ElementAt(timings.Count / 2), 2) : -1d;
            s["extrapolatedFullMapSeconds"] = per >= 0d ? Math.Round(per * flat.Count / 1000d, 1) : -1d;
            s["extrapolationNote"] = "msPerTexture (the first read left out as warm-up) x every 2D main texture; a main-thread synchronous ReadPixels each";
        }

        // --- 2. terrain ---------------------------------------------------------------------------------------------------

        private static void TerrainSection(Survey survey, Texture2D scratch, Dictionary<string, object> s)
        {
            s["count"] = survey.Terrains.Count;
            s["active"] = survey.Terrains.Count(t => t != null && t.isActiveAndEnabled);

            var list = new List<object>();
            s["terrains"] = list;

            var first = true;
            foreach (var terrain in survey.Terrains.Where(t => t != null).Take(MaxTerrainsDetailed))
            {
                var one = new Dictionary<string, object>();
                list.Add(one);
                var takeHeavy = first;
                first = false;

                Part(one, "basics", () =>
                {
                    one["name"] = terrain.name;
                    one["scene"] = terrain.gameObject.scene.name;
                    one["enabled"] = terrain.enabled;
                    one["activeInHierarchy"] = terrain.gameObject.activeInHierarchy;
                    one["layer"] = LayerMask.LayerToName(terrain.gameObject.layer);
                    one["position"] = V(terrain.transform.position);
                    one["drawInstanced"] = terrain.drawInstanced;
                });

                var data = terrain.terrainData;
                if (data == null)
                {
                    one["terrainData"] = null;
                    continue;
                }

                Part(one, "data", () =>
                {
                    one["size"] = V(data.size);
                    one["heightmapResolution"] = data.heightmapResolution;
                    one["alphamapResolution"] = data.alphamapResolution;
                    one["alphamapSize"] = $"{data.alphamapWidth}x{data.alphamapHeight}";
                    one["alphamapLayers"] = data.alphamapLayers;
                    one["alphamapTextureCount"] = data.alphamapTextureCount;
                    one["alphamapTextures"] = (data.alphamapTextures ?? new Texture2D[0])
                        .Select(t => t == null ? null : $"{t.name} {t.width}x{t.height} {Format(t)} readable {t.isReadable}").ToList();
                });

                Part(one, "layers", () =>
                {
                    var layers = new List<object>();
                    foreach (var layer in data.terrainLayers ?? new TerrainLayer[0])
                    {
                        if (layer == null)
                        {
                            layers.Add(null);
                            continue;
                        }

                        layers.Add(new Dictionary<string, object>
                        {
                            ["name"] = layer.name,
                            ["diffuse"] = Describe(layer.diffuseTexture),
                            ["normal"] = Describe(layer.normalMapTexture),
                            ["mask"] = Describe(layer.maskMapTexture),
                            ["tileSize"] = new[] { layer.tileSize.x, layer.tileSize.y },
                            ["tileOffset"] = new[] { layer.tileOffset.x, layer.tileOffset.y },
                            ["diffuseRemapMax"] = V(layer.diffuseRemapMax),
                        });
                    }

                    one["terrainLayers"] = layers;
                });

                Part(one, "materialTemplate", () => one["materialTemplate"] = DescribeMaterial(terrain.materialTemplate, scratch, takeHeavy));

                Part(one, "trees", () =>
                {
                    one["treeInstanceCount"] = data.treeInstanceCount;
                    var protos = data.treePrototypes ?? new TreePrototype[0];
                    var counts = new int[protos.Length];

                    foreach (var instance in data.treeInstances ?? new TreeInstance[0])
                        if (instance.prototypeIndex >= 0 && instance.prototypeIndex < counts.Length) counts[instance.prototypeIndex]++;

                    one["treePrototypes"] = protos.Select((p, i) => (object)new Dictionary<string, object>
                    {
                        ["prefab"] = p?.prefab != null ? p.prefab.name : null,
                        ["instances"] = counts[i],
                    }).ToList();
                    one["detailPrototypes"] = (data.detailPrototypes ?? new DetailPrototype[0]).Length;
                });

                if (takeHeavy)
                {
                    Part(one, "getAlphamaps", () =>
                    {
                        int w = data.alphamapWidth, h = data.alphamapHeight, layers = data.alphamapLayers;
                        var full = (long)w * h * Math.Max(1, layers) * 4 <= AlphamapFullBytesMax;
                        if (!full)
                        {
                            var side = Math.Max(16, (int)Math.Sqrt(AlphamapFullBytesMax / (4d * Math.Max(1, layers))));
                            w = Math.Min(w, side);
                            h = Math.Min(h, side);
                        }

                        var clock = Stopwatch.StartNew();
                        var maps = data.GetAlphamaps(0, 0, w, h);
                        var ms = Ms(clock);

                        one["getAlphamaps"] = new Dictionary<string, object>
                        {
                            ["ok"] = maps != null,
                            ["whole"] = full,
                            ["read"] = $"{w}x{h}x{layers}",
                            ["dims"] = maps == null ? null : $"{maps.GetLength(0)}x{maps.GetLength(1)}x{maps.GetLength(2)}",
                            ["ms"] = ms,
                            ["centreWeights"] = maps == null || maps.GetLength(2) == 0 ? null
                                : Enumerable.Range(0, maps.GetLength(2)).Select(l => (double)Math.Round(maps[maps.GetLength(0) / 2, maps.GetLength(1) / 2, l], 3)).ToList(),
                        };
                    });
                }
            }

            // MicroSplat, by type name.
            Part(s, "microSplat", () =>
            {
                var found = survey.Behaviours.Where(b => b != null && b.GetType().Name.IndexOf("MicroSplat", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var types = new Dictionary<string, int>();
                foreach (var b in found) Bump(types, b.GetType().FullName);
                s["microSplatTypes"] = types;

                var dumps = new Dictionary<string, object>();
                foreach (var group in found.GroupBy(b => b.GetType()))
                    dumps[group.Key.FullName ?? group.Key.Name] = DumpFields(group.First(), BindingFlags.Public | BindingFlags.Instance);

                s["microSplatFirstOfEachType"] = dumps;
                s["microSplatTerrainPresent"] = found.Any(b => b.GetType().Name == "MicroSplatTerrain");
                s["microSplatMeshTerrainPresent"] = found.Any(b => b.GetType().Name == "MicroSplatMeshTerrain");
            });

            // No Unity Terrain: what the ground is - the largest renderers by XZ area on the Terrain or Default layer.
            if (survey.Terrains.Count == 0 || !survey.Terrains.Any(t => t != null && t.terrainData != null))
            {
                Part(s, "groundWithoutTerrain", () =>
                {
                    var terrainLayer = LayerMask.NameToLayer("Terrain");
                    var defaultLayer = LayerMask.NameToLayer("Default");

                    s["largestGroundRenderers"] = survey.Renderers
                        .Where(r => r != null && (r.gameObject.layer == terrainLayer || r.gameObject.layer == defaultLayer) &&
                                    !EffectRenderers.Contains(r.GetType().Name))
                        .Select(r => (r, area: (double)r.bounds.size.x * r.bounds.size.z))
                        .OrderByDescending(x => x.area)
                        .Take(10)
                        .Select(x => (object)new Dictionary<string, object>
                        {
                            ["name"] = x.r.name,
                            ["layer"] = LayerMask.LayerToName(x.r.gameObject.layer),
                            ["active"] = x.r.gameObject.activeInHierarchy && x.r.enabled,
                            ["areaM2"] = Math.Round(x.area, 0),
                            ["bounds"] = B(x.r.bounds),
                            ["material"] = x.r.sharedMaterial != null ? x.r.sharedMaterial.name : null,
                            ["shader"] = x.r.sharedMaterial != null && x.r.sharedMaterial.shader != null ? x.r.sharedMaterial.shader.name : null,
                            ["mainTexture"] = Describe(MainTexture(x.r.sharedMaterial, out _)),
                        })
                        .ToList();
                });
            }
        }

        /// <summary>A material's shader, keywords, every texture property (Texture2DArray copy tests when
        /// <paramref name="heavy"/>) and its layer-like float/vector properties. Reads only.</summary>
        private static Dictionary<string, object> DescribeMaterial(Material m, Texture2D scratch, bool heavy)
        {
            if (m == null) return null;

            var d = new Dictionary<string, object>
            {
                ["name"] = m.name,
                ["shader"] = m.shader != null ? m.shader.name : null,
                ["keywords"] = m.shaderKeywords?.ToList(),
                ["renderQueue"] = m.renderQueue,
            };

            var textures = new List<object>();
            d["textures"] = textures;
            var arrays = new List<Texture2DArray>();

            foreach (var name in m.GetTexturePropertyNames() ?? new string[0])
            {
                var t = m.GetTexture(name);
                var one = new Dictionary<string, object> { ["property"] = name };
                textures.Add(one);
                if (t == null) continue;

                one["type"] = t.GetType().Name;
                one["name"] = t.name;
                one["size"] = $"{t.width}x{t.height}";
                one["format"] = Format(t);
                one["readable"] = t.isReadable;
                one["mips"] = t.mipmapCount;

                if (t is Texture2DArray array)
                {
                    one["depth"] = array.depth;
                    if (!arrays.Contains(array)) arrays.Add(array);
                }
            }

            var shader = m.shader;
            if (shader != null)
            {
                var values = new List<object>();
                var total = 0;

                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var type = shader.GetPropertyType(i);
                    if (type == ShaderPropertyType.Texture) continue;

                    total++;
                    var name = shader.GetPropertyName(i);
                    if (!LayerLike.IsMatch(name) || values.Count >= MaxFloatProps) continue;

                    object value;
                    switch (type)
                    {
                        case ShaderPropertyType.Color:
                            var c = m.GetColor(name);
                            value = new[] { c.r, c.g, c.b, c.a };
                            break;
                        case ShaderPropertyType.Vector:
                            var v = m.GetVector(name);
                            value = new[] { v.x, v.y, v.z, v.w };
                            break;
                        default:
                            value = m.GetFloat(name);
                            break;
                    }

                    values.Add(new Dictionary<string, object> { ["name"] = name, ["type"] = type.ToString(), ["value"] = value });
                }

                d["nonTextureProperties"] = total;
                d["layerLikeProperties"] = values;
            }

            if (heavy)
            {
                var tests = new List<object>();
                d["arrayCopyTests"] = tests;
                d["copyTextureSupport"] = SystemInfo.copyTextureSupport.ToString();

                foreach (var array in arrays.Take(6)) tests.Add(ArrayCopyTest(array, scratch));
            }

            return d;
        }

        /// <summary>Slice 0 of a Texture2DArray read two ways: Graphics.CopyTexture into an own Texture2D of the same format
        /// and then the blit readback (only when the copy is well defined - see <see cref="CopyRefusal"/>); and a direct Blit
        /// of the slice. Each path is judged on its own: plausible (not black), and, for a readable array, matching the CPU
        /// slice. A failed CopyTexture only logs, so "ok" alone could never fail. Everything made here is destroyed here.</summary>
        private static Dictionary<string, object> ArrayCopyTest(Texture2DArray array, Texture2D scratch)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = array.name,
                ["size"] = $"{array.width}x{array.height}x{array.depth}",
                ["format"] = Format(array),
                ["readable"] = array.isReadable,
            };

            // The CPU slice, when the array keeps its pixels: the truth both GPU paths are compared with.
            int[] cpu = null;
            if (array.isReadable)
            {
                try
                {
                    var pixels = array.GetPixels32(0, 0);
                    cpu = MeanOf(pixels, array.width, array.height);
                    d["cpuMean"] = cpu;
                }
                catch (Exception ex)
                {
                    d["cpuMean"] = $"unread ({ex.GetType().Name}: {ex.Message})";
                }
            }

            int[] copyMean = null;
            var refusal = CopyRefusal(array);
            if (refusal != null)
            {
                d["copyTexture"] = new Dictionary<string, object> { ["skipped"] = refusal };
            }
            else
            {
                Texture2D copy = null;
                try
                {
                    var clock = Stopwatch.StartNew();
                    copy = new Texture2D(array.width, array.height, array.graphicsFormat, TextureCreationFlags.None)
                    {
                        name = "QuestTreeMapDataProbe-slice",
                    };
                    Graphics.CopyTexture(array, 0, 0, copy, 0, 0);
                    var copyMs = Ms(clock);

                    var readMs = ReadOne(copy, TextureCap, scratch, out var bytes, out copyMean);
                    d["copyTexture"] = new Dictionary<string, object>
                    {
                        ["threw"] = false,
                        ["copyMs"] = copyMs,
                        ["readMs"] = Math.Round(readMs, 2),
                        ["bytes"] = bytes,
                        ["mean"] = copyMean,
                        ["copyFormat"] = Format(copy),
                    };
                }
                catch (Exception ex)
                {
                    copyMean = null;
                    d["copyTexture"] = new Dictionary<string, object> { ["threw"] = true, ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
                }
                finally
                {
                    if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                }
            }

            int[] blitMean = null;
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            try
            {
                Capped(array.width, array.height, TextureCap, out var w, out var h);
                var clock = Stopwatch.StartNew();
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(array, rt, 0, 0);
                RenderTexture.active = rt;
                scratch.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                blitMean = Mean(scratch, w, h);
                d["blitSlice"] = new Dictionary<string, object> { ["threw"] = false, ["ms"] = Ms(clock), ["mean"] = blitMean };
            }
            catch (Exception ex)
            {
                d["blitSlice"] = new Dictionary<string, object> { ["threw"] = true, ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }

            // Three separate facts, each able to fail.
            d["copyPlausible"] = copyMean == null ? (object)"not run" : Plausible(copyMean);
            d["blitPlausible"] = blitMean == null ? (object)"not run" : Plausible(blitMean);
            d["copyMatchesCpu"] = cpu == null ? "not readable" : copyMean == null ? (object)"not run" : Close(copyMean, cpu);
            d["blitMatchesCpu"] = cpu == null ? "not readable" : blitMean == null ? (object)"not run" : Close(blitMean, cpu);
            d["matchTolerance"] = "each of the mean R, G, B within 12 of 255";

            return d;
        }

        /// <summary>Why a CopyTexture of slice 0 into a Texture2D would be undefined or unsafe here, or null. A compressed
        /// texture whose side is not a multiple of 4 is never created (D3D11 E_INVALIDARG, which preceded a native crash in
        /// this project); under a mip limit the GPU's top mip may be smaller than array.width, an undefined copy.</summary>
        private static string CopyRefusal(Texture2DArray array)
        {
            var support = SystemInfo.copyTextureSupport;
            if ((support & CopyTextureSupport.Basic) == 0) return $"copyTextureSupport {support} lacks Basic";
            if ((support & CopyTextureSupport.DifferentTypes) == 0) return $"copyTextureSupport {support} lacks DifferentTypes (array to 2D)";

            if (GraphicsFormatUtility.IsCompressedFormat(array.graphicsFormat) && (array.width % 4 != 0 || array.height % 4 != 0))
                return $"block-compressed {array.graphicsFormat} with a side not a multiple of 4 ({array.width}x{array.height})";

            var limit = QualitySettings.globalTextureMipmapLimit;
            if (limit > 0) return $"globalTextureMipmapLimit is {limit}: the GPU's top mip may be smaller than the copy region";

            return null;
        }

        private static bool Plausible(int[] mean) => mean != null && mean.Length >= 3 && mean[0] + mean[1] + mean[2] > 0;

        private static bool Close(int[] a, int[] b) =>
            Math.Abs(a[0] - b[0]) <= 12 && Math.Abs(a[1] - b[1]) <= 12 && Math.Abs(a[2] - b[2]) <= 12;

        /// <summary>The mean RGBA of a w x h pixel array, every 4th texel each way.</summary>
        private static int[] MeanOf(Color32[] pixels, int w, int h)
        {
            long r = 0, g = 0, b = 0, a = 0, n = 0;
            for (var y = 0; y < h; y += 4)
                for (var x = 0; x < w; x += 4)
                {
                    var i = y * w + x;
                    if (i >= pixels.Length) continue;
                    var c = pixels[i];
                    r += c.r;
                    g += c.g;
                    b += c.b;
                    a += c.a;
                    n++;
                }

            if (n == 0) return new[] { -1, -1, -1, -1 };
            return new[] { (int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n) };
        }

        // --- 3. decals and roads ------------------------------------------------------------------------------------------

        private static void Decals(Survey survey, Dictionary<string, object> s)
        {
            var byShader = new Dictionary<string, List<Renderer>>();
            foreach (var r in survey.Renderers)
            {
                if (r == null) continue;
                var shader = ShaderName(r.sharedMaterial);
                if (shader == null || shader.IndexOf("decal", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!byShader.TryGetValue(shader, out var list)) byShader[shader] = list = new List<Renderer>();
                list.Add(r);
            }

            s["decalShaderRenderers"] = byShader.Values.Sum(l => l.Count);
            s["decalShaders"] = byShader.ToDictionary(p => p.Key, p => (object)new Dictionary<string, object>
            {
                ["count"] = p.Value.Count,
                ["first5"] = p.Value.Take(5).Select(r => (object)DescribeRenderer(r)).ToList(),
            });

            var byType = survey.Behaviours.Where(b => b != null && b.GetType().Name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(b => b.GetType().FullName ?? b.GetType().Name).ToList();

            s["decalComponentTypes"] = byType.ToDictionary(g => g.Key, g => (object)new Dictionary<string, object>
            {
                ["count"] = g.Count(),
                ["first5"] = g.Take(5).Select(b => (object)DescribeDecal(b)).ToList(),
            });

            var roads = survey.Renderers.Where(r => r != null && (Has(r.name, "road") || Has(r.sharedMaterial != null ? r.sharedMaterial.name : null, "road"))).ToList();
            var roadShaders = new Dictionary<string, int>();
            foreach (var r in roads) Bump(roadShaders, ShaderName(r.sharedMaterial) ?? "(none)");
            s["roadRenderers"] = roads.Count;
            s["roadShaders"] = roadShaders;
            s["roadFirst5"] = roads.Take(5).Select(r => (object)DescribeRenderer(r)).ToList();
        }

        private static Dictionary<string, object> DescribeDecal(MonoBehaviour b)
        {
            var d = new Dictionary<string, object>
            {
                ["gameObject"] = b.name,
                ["active"] = b.isActiveAndEnabled,
                ["position"] = V(b.transform.position),
                ["lossyScale"] = V(b.transform.lossyScale),
            };

            var r = b.GetComponent<Renderer>();
            Material material = r != null ? r.sharedMaterial : null;
            if (r != null) d["bounds"] = B(r.bounds);

            // Else the first Material field of the component, read by reflection (never written).
            if (material == null)
            {
                foreach (var f in b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!typeof(Material).IsAssignableFrom(f.FieldType)) continue;
                    material = f.GetValue(b) as Material;
                    if (material != null)
                    {
                        d["materialField"] = f.Name;
                        break;
                    }
                }
            }

            d["material"] = material != null ? material.name : null;
            d["shader"] = ShaderName(material);
            d["mainTexture"] = Describe(MainTexture(material, out _));
            d["fields"] = DumpFields(b, BindingFlags.Public | BindingFlags.Instance);
            return d;
        }

        // --- 4. trees -----------------------------------------------------------------------------------------------------

        private static void Trees(Survey survey, Dictionary<string, object> s)
        {
            var trees = survey.Renderers.Where(r => r != null && TreeShader.IsMatch(ShaderName(r.sharedMaterial) ?? "")).ToList();
            s["renderers"] = trees.Count;

            var shaders = new Dictionary<string, int>();
            var types = new Dictionary<string, int>();
            var materials = new HashSet<Material>();
            var meshes = new Dictionary<Mesh, int>();
            var lods = new HashSet<LODGroup>();

            foreach (var r in trees)
            {
                Bump(shaders, ShaderName(r.sharedMaterial));
                Bump(types, r.GetType().Name);
                foreach (var m in r.sharedMaterials)
                    if (m != null) materials.Add(m);

                var mesh = MeshOf(r);
                if (mesh != null) meshes[mesh] = meshes.TryGetValue(mesh, out var n) ? n + 1 : 1;

                var lod = r.GetComponentInParent<LODGroup>(true);
                if (lod != null) lods.Add(lod);
            }

            long triangles = 0;
            foreach (var pair in meshes) triangles += Triangles(pair.Key) * pair.Value;

            s["shaderHistogram"] = shaders;
            s["rendererTypes"] = types;
            s["distinctMeshes"] = meshes.Count;
            s["distinctMaterials"] = materials.Count;
            s["totalTriangles"] = triangles;
            s["readableMeshes"] = meshes.Keys.Count(m => m.isReadable);
            s["meshesNeedingGpuReadback"] = meshes.Keys.Count(m => !m.isReadable);
            s["top20"] = meshes.OrderByDescending(p => p.Value).Take(20).Select(p => (object)new Dictionary<string, object>
            {
                ["mesh"] = p.Key.name,
                ["instances"] = p.Value,
                ["triangles"] = Triangles(p.Key),
                ["vertices"] = p.Key.vertexCount,
                ["readable"] = p.Key.isReadable,
                ["tangents"] = p.Key.HasVertexAttribute(VertexAttribute.Tangent),
            }).ToList();

            var lodCounts = new Dictionary<string, int>();
            foreach (var g in lods) Bump(lodCounts, g.lodCount.ToString(CultureInfo.InvariantCulture));
            s["lodGroups"] = lods.Count;
            s["lodCountHistogram"] = lodCounts;

            long terrainTrees = 0;
            foreach (var t in survey.Terrains)
                if (t != null && t.terrainData != null) terrainTrees += t.terrainData.treeInstanceCount;
            s["terrainTreeInstances"] = terrainTrees;
            s["terrainTreeNote"] = "per terrain, with prototypes and their instance counts, under terrain.terrains[].treePrototypes";
        }

        // --- 5. normal maps -----------------------------------------------------------------------------------------------

        private static void NormalMaps(Survey survey, Texture2D scratch, Dictionary<string, object> s)
        {
            var mask = MapCapture.MenuCaptureMask();

            // Each material with the first renderer that uses it, for the mesh's tangents.
            var users = new Dictionary<Material, Renderer>();
            foreach (var r in survey.Renderers)
            {
                if (r == null || (mask & (1 << r.gameObject.layer)) == 0 || EffectRenderers.Contains(r.GetType().Name)) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && !users.ContainsKey(m)) users[m] = r;
            }

            var picks = Spread(users.Keys.OrderBy(m => m.GetInstanceID()).ToList(), NormalMaterialSamples);
            s["materialsSampled"] = picks.Count;
            s["materialsTotal"] = users.Count;

            var props = new Dictionary<string, int>();
            var sizes = new Dictionary<string, int>();
            var formats = new Dictionary<string, int>();
            var found = new List<Texture>();
            int withNormal = 0, withTangents = 0, withoutTangents = 0, normalWithoutTangents = 0, noMesh = 0;

            foreach (var m in picks)
            {
                var normal = NormalTexture(m, out var prop);
                if (normal != null)
                {
                    withNormal++;
                    Bump(props, prop);
                    Bump(sizes, $"{normal.width}x{normal.height}");
                    Bump(formats, Format(normal));
                    if (normal.dimension == TextureDimension.Tex2D && !found.Contains(normal)) found.Add(normal);
                }

                var mesh = MeshOf(users[m]);
                if (mesh == null)
                {
                    noMesh++;
                    continue;
                }

                var tangents = mesh.HasVertexAttribute(VertexAttribute.Tangent);
                if (tangents) withTangents++;
                else withoutTangents++;
                if (normal != null && !tangents) normalWithoutTangents++;
            }

            s["withNormalMap"] = withNormal;
            s["normalProperties"] = props;
            s["normalSizeHistogram"] = Top(sizes, 30);
            s["normalFormatHistogram"] = formats;
            s["meshWithTangents"] = withTangents;
            s["meshWithoutTangents"] = withoutTangents;
            s["normalMapButNoTangents"] = normalWithoutTangents;
            s["noMesh"] = noMesh;

            var timings = new List<double>();
            long bytes = 0;
            var readErrors = new List<string>();
            foreach (var t in found.Take(NormalReadSamples))
            {
                try
                {
                    timings.Add(ReadOne(t, TextureCap, scratch, out var b, out _));
                    bytes += b;
                }
                catch (Exception ex)
                {
                    readErrors.Add($"{t.name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            s["readErrors"] = readErrors;

            s["readCount"] = timings.Count;
            s["readTotalMs"] = Math.Round(timings.Sum(), 1);
            s["msPerNormalMap"] = timings.Count > 0 ? Math.Round(timings.Average(), 2) : -1d;
            s["readBytes"] = bytes;
        }

        // --- 6. the viewer's lighting -------------------------------------------------------------------------------------

        /// <summary>A disabled camera and a directional light with hard shadows on a free layer, a cube over a plane, two
        /// renders (the cube casting shadows, then not), and the plane's pixel where the shadow should fall against one on
        /// the lit side. Everything is made and destroyed (DestroyImmediate) inside this call, so no frame of the game's ever
        /// draws it. QualitySettings are read, never written.</summary>
        private static void LightTest(Dictionary<string, object> s)
        {
            s["qualityShadows"] = QualitySettings.shadows.ToString();
            s["qualityShadowCascades"] = QualitySettings.shadowCascades;
            s["qualityShadowDistance"] = QualitySettings.shadowDistance;
            s["qualityShadowResolution"] = QualitySettings.shadowResolution.ToString();
            s["qualityShadowProjection"] = QualitySettings.shadowProjection.ToString();
            s["qualityLevel"] = QualitySettings.GetQualityLevel();
            s["pixelLightCount"] = QualitySettings.pixelLightCount;

            var layer = -1;
            for (var i = 31; i >= 8; i--)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
                layer = i;
                break;
            }

            if (layer < 0) layer = 31;
            s["layer"] = layer;

            Shader shader = null;
            foreach (var name in LightShaders)
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }

            s["shader"] = shader != null ? shader.name : null;
            if (shader == null) throw new InvalidOperationException("no lit shader found (Standard, Legacy Shaders/Diffuse, Diffuse)");

            var origin = new Vector3(0f, -9000f, 0f);
            var made = new List<UnityEngine.Object>();
            var previous = RenderTexture.active;
            RenderTexture rt = null;

            try
            {
                var material = new Material(shader) { name = "QuestTreeMapDataProbe-lit", color = Color.white };
                made.Add(material);

                GameObject Make(PrimitiveType type, Vector3 position, Vector3 scale)
                {
                    var go = GameObject.CreatePrimitive(type);
                    made.Add(go);
                    go.name = "QuestTreeMapDataProbe-" + type;
                    var collider = go.GetComponent<Collider>();
                    if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
                    go.layer = layer;
                    go.transform.position = position;
                    go.transform.localScale = scale;
                    var r = go.GetComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = ShadowCastingMode.On;
                    r.receiveShadows = true;
                    return go;
                }

                Make(PrimitiveType.Plane, origin, new Vector3(2f, 1f, 2f));   // 20 x 20 m
                var cube = Make(PrimitiveType.Cube, origin + new Vector3(0f, 2f, 0f), Vector3.one);

                var lightGo = new GameObject("QuestTreeMapDataProbe-light");
                made.Add(lightGo);
                lightGo.layer = layer;
                lightGo.transform.rotation = Quaternion.Euler(50f, 0f, 0f);   // from the south, 50 degrees up
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.Hard;
                light.intensity = 1f;
                light.color = Color.white;
                light.cullingMask = 1 << layer;
                light.renderMode = LightRenderMode.ForcePixel;

                var camGo = new GameObject("QuestTreeMapDataProbe-camera");
                made.Add(camGo);
                var camera = camGo.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 5f;
                camera.cullingMask = 1 << layer;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 50f;
                camera.transform.position = origin + new Vector3(0f, 12f, 0f);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "QuestTreeMapDataProbe-rt" };
                rt.Create();
                camera.targetTexture = rt;

                // Where the cube's shadow falls on the plane, and the mirror point on the lit side.
                var dir = lightGo.transform.forward;
                var centre = cube.transform.position;
                var t = (origin.y - centre.y) / dir.y;
                var shadowPoint = centre + dir * t;
                var litPoint = new Vector3(2f * centre.x - shadowPoint.x, origin.y, 2f * centre.z - shadowPoint.z);

                var scratch = new Texture2D(256, 256, TextureFormat.RGBA32, false, false) { name = "QuestTreeMapDataProbe-light-read" };
                made.Add(scratch);

                Color Pixel(Vector3 world)
                {
                    var vp = camera.WorldToViewportPoint(world);
                    var x = Mathf.Clamp(Mathf.RoundToInt(vp.x * 255f), 0, 255);
                    var y = Mathf.Clamp(Mathf.RoundToInt(vp.y * 255f), 0, 255);
                    return scratch.GetPixel(x, y);
                }

                double[] Render()
                {
                    camera.Render();
                    RenderTexture.active = rt;
                    scratch.ReadPixels(new Rect(0, 0, 256, 256), 0, 0, false);
                    scratch.Apply(false);
                    var shadowed = Pixel(shadowPoint);
                    var lit = Pixel(litPoint);
                    return new double[] { Lum(shadowed), Lum(lit) };
                }

                // Any other light that reaches the probe layer could fake a shadow: listed, and a baseline render with the
                // probe's own light off measures what they do.
                var others = UnityEngine.Object.FindObjectsOfType<Light>()
                    .Where(l => l != null && l != light && l.isActiveAndEnabled && (l.cullingMask & (1 << layer)) != 0).ToList();
                s["otherLightsOnLayer"] = others.Count;
                s["otherLightNames"] = others.Take(20).Select(l => $"{l.name} ({l.type}, shadows {l.shadows})").ToList();

                var clock = Stopwatch.StartNew();
                light.enabled = false;
                var baseline = Render();
                light.enabled = true;
                var withShadow = Render();
                cube.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                var withoutShadow = Render();
                s["renderMs"] = Ms(clock);

                s["actualRenderingPath"] = camera.actualRenderingPath.ToString();
                s["cameraRenderingPath"] = camera.renderingPath.ToString();
                s["shadowPointLuminance"] = Math.Round(withShadow[0], 4);
                s["litPointLuminance"] = Math.Round(withShadow[1], 4);
                s["shadowPointLuminanceNoCaster"] = Math.Round(withoutShadow[0], 4);
                s["baselineShadowPointLuminance"] = Math.Round(baseline[0], 4);
                s["baselineLitPointLuminance"] = Math.Round(baseline[1], 4);

                // Judged against the baseline: the probe's light must brighten the lit point, and the darkening it adds at
                // the shadow point must exceed whatever darkening the other lights alone already show there.
                var ownLights = withShadow[1] > baseline[1] + 0.02d;
                var ownGap = withShadow[1] - withShadow[0];
                var baseGap = baseline[1] - baseline[0];
                s["probeLightBrightens"] = ownLights;
                s["shadowDarker"] = ownLights && withShadow[0] + 0.02d < withShadow[1] && withShadow[0] + 0.02d < withoutShadow[0] &&
                                    ownGap > baseGap + 0.02d;
                s["note"] = "shadowDarker: the probe's light brightens the lit point over the baseline (probe light off); the shadow " +
                            "point is darker than the lit point, darker than itself with the cube's shadow casting off, and the " +
                            "lit-minus-shadow gap exceeds the baseline's gap";
            }
            finally
            {
                RenderTexture.active = previous;

                for (var i = made.Count - 1; i >= 0; i--)
                {
                    try { if (made[i] != null) UnityEngine.Object.DestroyImmediate(made[i]); } catch (Exception) { }
                }

                if (rt != null)
                {
                    try
                    {
                        rt.Release();
                        UnityEngine.Object.DestroyImmediate(rt);
                    }
                    catch (Exception) { }
                }
            }
        }

        // --- helpers ------------------------------------------------------------------------------------------------------

        /// <summary>Runs one measurement into its own section of the report; a throw is recorded and the run goes on.</summary>
        private static void Measure(RunState run, string name, Action<Dictionary<string, object>> body)
        {
            var section = new Dictionary<string, object>();
            run.Report[name] = section;
            var clock = Stopwatch.StartNew();

            try
            {
                body(section);
            }
            catch (Exception ex)
            {
                var text = $"{ex.GetType().Name}: {ex.Message} at {MenuMapHost.FirstFrame(ex.StackTrace)}";
                section["error"] = text;
                run.Errors.Add($"{name}: {text}");
            }

            section["sectionMs"] = Ms(clock);
        }

        /// <summary>One guarded part of a section: a throw lands in the section's "errors" and the section goes on.</summary>
        private static void Part(Dictionary<string, object> section, string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                if (!section.TryGetValue("errors", out var e) || !(e is List<string> list)) section["errors"] = list = new List<string>();
                list.Add($"{name}: {ex.GetType().Name}: {ex.Message} at {MenuMapHost.FirstFrame(ex.StackTrace)}");
            }
        }

        /// <summary>Blits <paramref name="texture"/> into a temporary ARGB32 target at most <paramref name="cap"/> on its
        /// longer side and reads it back into the own scratch texture, then copies the bytes out - the builder's
        /// ReadTexture path. Returns the milliseconds.</summary>
        private static double ReadOne(Texture texture, int cap, Texture2D scratch, out long bytes, out int[] mean)
        {
            Capped(texture.width, texture.height, cap, out var w, out var h);
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            var clock = Stopwatch.StartNew();

            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                scratch.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);

                var read = scratch.GetRawTextureData<Color32>();
                var stride = scratch.width;
                // One reused buffer (the copy's cost is timed, not its allocation).
                var size = w * h * 4;
                if (_readBuffer == null || _readBuffer.Length < size) _readBuffer = new byte[TextureCap * TextureCap * 4];
                var copy = _readBuffer;
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var c = read[y * stride + x];
                        var o = (y * w + x) * 4;
                        copy[o] = c.r;
                        copy[o + 1] = c.g;
                        copy[o + 2] = c.b;
                        copy[o + 3] = c.a;
                    }

                var ms = clock.Elapsed.TotalMilliseconds;
                bytes = size;
                mean = Mean(scratch, w, h);
                return ms;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>The mean RGBA (0-255) of the scratch texture's w x h corner, every 4th texel.</summary>
        private static int[] Mean(Texture2D scratch, int w, int h)
        {
            var read = scratch.GetRawTextureData<Color32>();
            var stride = scratch.width;
            long r = 0, g = 0, b = 0, a = 0, n = 0;

            for (var y = 0; y < h; y += 4)
                for (var x = 0; x < w; x += 4)
                {
                    var c = read[y * stride + x];
                    r += c.r;
                    g += c.g;
                    b += c.b;
                    a += c.a;
                    n++;
                }

            if (n == 0) return new[] { -1, -1, -1, -1 };
            return new[] { (int)(r / n), (int)(g / n), (int)(b / n), (int)(a / n) };
        }

        private static void Capped(int width, int height, int cap, out int w, out int h)
        {
            var side = Math.Max(1, Math.Max(width, height));
            var scale = Math.Min(1d, cap / (double)side);
            w = Math.Max(1, Math.Min(cap, (int)Math.Round(width * scale)));
            h = Math.Max(1, Math.Min(cap, (int)Math.Round(height * scale)));
        }

        private static Texture MainTexture(Material material, out string property)
        {
            property = null;
            if (material == null) return null;

            foreach (var name in TextureProperties)
            {
                if (!material.HasProperty(name)) continue;
                var t = material.GetTexture(name);
                if (t == null) continue;
                property = name;
                return t;
            }

            foreach (var name in material.GetTexturePropertyNames() ?? new string[0])
            {
                if (string.IsNullOrEmpty(name) || !MainLike.IsMatch(name) || NotMain.IsMatch(name)) continue;
                var t = material.GetTexture(name);
                if (t == null || t.dimension != TextureDimension.Tex2D) continue;
                property = name;
                return t;
            }

            return null;
        }

        private static Texture NormalTexture(Material material, out string property)
        {
            property = null;
            if (material == null) return null;

            foreach (var name in NormalProperties)
            {
                if (!material.HasProperty(name)) continue;
                var t = material.GetTexture(name);
                if (t == null) continue;
                property = name;
                return t;
            }

            foreach (var name in material.GetTexturePropertyNames() ?? new string[0])
            {
                if (string.IsNullOrEmpty(name) || !NormalLike.IsMatch(name)) continue;
                var t = material.GetTexture(name);
                if (t == null) continue;
                property = name;
                return t;
            }

            return null;
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r == null) return null;
            if (r is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>Index counts are metadata: readable without the mesh being readable.</summary>
        private static long Triangles(Mesh mesh)
        {
            long n = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) n += mesh.GetIndexCount(i) / 3;
            return n;
        }

        private static Dictionary<string, object> DescribeRenderer(Renderer r)
        {
            var m = r.sharedMaterial;
            return new Dictionary<string, object>
            {
                ["name"] = r.name,
                ["type"] = r.GetType().Name,
                ["active"] = r.gameObject.activeInHierarchy && r.enabled,
                ["layer"] = LayerMask.LayerToName(r.gameObject.layer),
                ["bounds"] = B(r.bounds),
                ["material"] = m != null ? m.name : null,
                ["shader"] = ShaderName(m),
                ["renderQueue"] = m != null ? m.renderQueue : -1,
                ["mainTexture"] = Describe(MainTexture(m, out _)),
            };
        }

        /// <summary>A component's fields by reflection - names, types, and a short value: a Unity object's name and type,
        /// an array's length, a primitive's text. Reads only; never calls a property getter.</summary>
        private static List<object> DumpFields(object target, BindingFlags flags)
        {
            var list = new List<object>();
            foreach (var f in target.GetType().GetFields(flags).Take(MaxFields))
            {
                string value;
                try
                {
                    var v = f.GetValue(target);
                    value = v == null ? "null"
                        : v is UnityEngine.Object uo ? (uo == null ? "null (destroyed)" : $"{uo.name} ({uo.GetType().Name})")
                        : v is Array a ? $"{f.FieldType.GetElementType()?.Name}[{a.Length}]"
                        : v is string str ? str
                        : f.FieldType.IsPrimitive || f.FieldType.IsEnum ? Convert.ToString(v, CultureInfo.InvariantCulture)
                        : v.GetType().Name;
                }
                catch (Exception ex)
                {
                    value = $"unread ({ex.GetType().Name})";
                }

                if (value != null && value.Length > 200) value = value.Substring(0, 200);
                list.Add(new Dictionary<string, object> { ["name"] = f.Name, ["type"] = f.FieldType.Name, ["value"] = value });
            }

            return list;
        }

        private static string Describe(Texture t) =>
            t == null ? null : $"{t.name} {t.width}x{t.height} {Format(t)}{(t is Texture2DArray a ? $" x{a.depth}" : "")}";

        private static string Format(Texture t)
        {
            if (t is Texture2D t2) return t2.format.ToString();
            if (t is Texture2DArray a) return a.format.ToString();
            return t.graphicsFormat.ToString();
        }

        private static string ShaderName(Material m) => m != null && m.shader != null ? m.shader.name : null;

        private static bool Has(string text, string part) => text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string SideBucket(int side) =>
            side <= 128 ? "<=128" : side <= 256 ? "<=256" : side <= 512 ? "<=512" : side <= 1024 ? "<=1024" : side <= 2048 ? "<=2048" : side <= 4096 ? "<=4096" : ">4096";

        private static void Bump(Dictionary<string, int> counts, string key)
        {
            key = key ?? "(null)";
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private static Dictionary<string, int> Top(Dictionary<string, int> counts, int n) =>
            counts.OrderByDescending(p => p.Value).Take(n).ToDictionary(p => p.Key, p => p.Value);

        private static List<T> Spread<T>(List<T> items, int n)
        {
            if (items.Count <= n) return items.ToList();
            var picks = new List<T>(n);
            for (var i = 0; i < n; i++) picks.Add(items[(int)((long)i * items.Count / n)]);
            return picks;
        }

        private static double Lum(Color c) => 0.2126d * c.r + 0.7152d * c.g + 0.0722d * c.b;

        private static double Ms(Stopwatch clock) => Math.Round(clock.Elapsed.TotalMilliseconds, 1);

        private static float[] V(Vector3 v) => new[] { (float)Math.Round(v.x, 2), (float)Math.Round(v.y, 2), (float)Math.Round(v.z, 2) };

        private static float[] V(Vector4 v) => new[] { v.x, v.y, v.z, v.w };

        private static Dictionary<string, object> B(Bounds b) => new Dictionary<string, object> { ["center"] = V(b.center), ["size"] = V(b.size) };

        private static Dictionary<string, object> MemoryNow()
        {
            var d = new Dictionary<string, object>();
            try { d["unityAllocatedMb"] = Math.Round(Profiler.GetTotalAllocatedMemoryLong() / 1048576d, 1); } catch (Exception) { }
            try { d["unityReservedMb"] = Math.Round(Profiler.GetTotalReservedMemoryLong() / 1048576d, 1); } catch (Exception) { }
            try { d["monoHeapMb"] = Math.Round(GC.GetTotalMemory(false) / 1048576d, 1); } catch (Exception) { }

            if (VramProbe.TryRead(out var usage, out var budget))
            {
                d["vramMb"] = Math.Round(usage, 1);
                d["vramBudgetMb"] = Math.Round(budget, 1);
            }
            else
            {
                d["vram"] = VramProbe.Text();
            }

            try { d["text"] = MenuMapHost.Memory(); } catch (Exception) { }
            return d;
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception ex) { return $"unread ({ex.GetType().Name})"; }
        }

        private static string Version()
        {
            try
            {
                var attr = typeof(MapDataProbe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr?.InformationalVersion ?? typeof(MapDataProbe).Assembly.GetName().Version?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FileSafe(string text)
        {
            var chars = (text ?? "map").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
            return chars.Length == 0 ? "map" : new string(chars);
        }
    }
}
