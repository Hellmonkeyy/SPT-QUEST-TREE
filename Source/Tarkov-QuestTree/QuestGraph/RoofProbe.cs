using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EFT;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// THROWAWAY DIAGNOSTIC. One key that writes down everything the game knows about the geometry
    /// above the player, so that a roof which refuses to be captured can be argued about from evidence
    /// instead of from guesses.
    ///
    /// Why it exists: Big Red on Customs still captures as a translucent blue-green sheet with its own
    /// shelving showing through it, after the capture already forces every renderer, LOD group and
    /// deactivated GameObject that the scene's distance cullers hold (MapCapture.ForceCulling). So the
    /// roof is not being hidden by the culling machinery this code knows about, and the next guess -
    /// another culler, a shader that draws one-sided, a transparent queue, a static batch, a LOD level
    /// the reference point puts out of range - is one guess too many. This prints all of them for
    /// everything in a 40 m column over the player and lets the picture be read rather than imagined.
    ///
    /// DELETE this file, the ModSettings.RoofProbeKey entry and the one line in GameWorldStartedPatch
    /// together, the moment the roof question is settled. Nothing else in the mod may come to depend on
    /// it; it writes one text file and changes nothing in the scene.
    /// </summary>
    internal sealed class RoofProbe : MonoBehaviour
    {
        /// <summary>Metres around the player, in XZ, that count as "over the player". Forty: Big Red is
        /// about 45 m across and standing anywhere inside it puts its roof, its walls and the shelving
        /// under it all in the column.</summary>
        private const float ColumnRadius = 40f;

        /// <summary>Metres above the player's feet a thing has to reach before it is a roof candidate.
        /// Three: over head height, so the floor and the crates on it are left out.</summary>
        private const float RoofRise = 3f;

        /// <summary>Most renderer lines the file carries. Three hundred is more than any one building
        /// has and small enough to paste whole.</summary>
        private const int MaxLines = 300;

        /// <summary>Adds the probe key's watcher to a raid, from
        /// <see cref="QuestTree.Patches.GameWorldStartedPatch"/>. Ahead of the HarvestZones gate on
        /// purpose, exactly as the Phase 0 experiment key was: this is a debug key, not part of what the
        /// mod does for a player. Never throws.</summary>
        /// <param name="gameWorld">The raid's world, as handed to the patch.</param>
        public static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeRoofProbe");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);

                var probe = go.AddComponent<RoofProbe>();
                probe._gameWorld = gameWorld;

                // Said out loud once per raid, at Info: the probe's first two runs produced nothing
                // at all and there was no way to tell a key that never fired from a watcher that was
                // never installed. The key it prints is the bound one, so a cfg edit is visible here
                // too. Goes with the rest of RoofProbe.cs when the roof question is settled.
                var key = ModSettings.Ready && ModSettings.RoofProbeKey != null
                    ? ModSettings.KeyText(ModSettings.RoofProbeKey.Value, " + ")
                    : "";
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: roof probe watching {(string.IsNullOrEmpty(key) ? "no key (unbound)" : key)}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the roof probe key ({ex.Message}).");
            }
        }

        private GameWorld _gameWorld;
        private bool _warned;

        /// <summary>Whether this raid has already said that the main key was seen with the modifiers
        /// missing. Once per raid: the point is to tell "the key never reached us" apart from "the
        /// binding wants a modifier you were not holding", and a held-down key would otherwise say it
        /// every frame.</summary>
        private bool _saidModifiersMissing;

        private void Update()
        {
            if (!ModSettings.Ready || ModSettings.RoofProbeKey == null) return;

            try
            {
                var shortcut = ModSettings.RoofProbeKey.Value;
                if (shortcut.MainKey == KeyCode.None) return;

                // ModSettings.ShortcutDown, not the shortcut's own IsDown: BepInEx refuses a press
                // while ANY key outside the combination is held, which is why the first two probe
                // presses in a raid recorded nothing.
                if (!ModSettings.ShortcutDown(shortcut))
                {
                    // The main key went down and the test still failed, so the modifiers are wrong: one
                    // the binding names is not held, or one it does not name is. Those are the only two
                    // ways a press can be swallowed now - a movement key cannot do it any more.
                    if (!_saidModifiersMissing && Input.GetKeyDown(shortcut.MainKey))
                    {
                        _saidModifiersMissing = true;
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: roof probe saw {shortcut.MainKey} but the modifiers were not held " +
                            $"as bound (it wants exactly {ModSettings.KeyText(shortcut, " + ")}, and Ctrl, Shift " +
                            "or Alt held on top of that blocks it)");
                    }

                    return;
                }

                Run();
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the roof probe key failed ({ex.Message}).");
            }
        }

        /// <summary>Reads the column over the player and writes the file. One press, one file, all of it
        /// inside one try: a diagnostic that takes the raid down is worse than no diagnostic.</summary>
        private void Run()
        {
            try
            {
                var player = _gameWorld?.MainPlayer;
                if (player == null)
                {
                    Plugin.LogSource?.LogInfo("QuestTree: the roof probe needs a player in a raid.");
                    return;
                }

                var at = player.Transform.position;
                var map = MapName();

                var cullers = Cullers();
                var renderers = Renderers(at);

                var text = new StringBuilder();

                text.AppendLine($"QuestTree roof probe (throwaway) - {map} - {Now()}");
                text.AppendLine(
                    $"mod {ModInfo.Stamp}, player at {F(at.x)},{F(at.y)},{F(at.z)}, column radius {F(ColumnRadius)} m, " +
                    $"roof floor y {F(at.y + RoofRise)}");
                text.AppendLine(
                    $"{renderers.Count} renderer(s) in the column reach above that, {cullers.Components.Count} component(s) " +
                    $"and {cullers.Objects.Count} object(s) are held by {cullers.Count} DisablerCullingObject(s) in this scene");
                text.AppendLine(
                    "NOTE: a renderer on an inactive object can report stale or zero bounds, so a line with a " +
                    "zero-size box is placed by its transform, not its mesh.");
                text.AppendLine(
                    "NOTE: GetComponentInParent skips INACTIVE parents in this Unity, so \"lod [none]\" on a line " +
                    "whose inHierarchy is no may mean no group was found rather than no group exists.");
                text.AppendLine();

                text.AppendLine("--- renderers, highest top first ---");

                var written = 0;

                foreach (var renderer in renderers)
                {
                    if (written >= MaxLines)
                    {
                        text.AppendLine($"... {renderers.Count - written} more, not written (the cap is {MaxLines}).");
                        break;
                    }

                    text.AppendLine(Describe(renderer, cullers));
                    written++;
                }

                text.AppendLine();
                AppendOthers(text, at);

                var path = Write(map, text.ToString());
                if (path == null) return;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: roof probe wrote {written} renderer line(s) for the column over " +
                    $"{F(at.x)},{F(at.z)} to {path}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the roof probe failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // --- what is in the column ---------------------------------------------------------------

        /// <summary>Every renderer whose box comes within <see cref="ColumnRadius"/> of the player in XZ
        /// and reaches more than <see cref="RoofRise"/> above him, highest top first.
        ///
        /// Resources.FindObjectsOfTypeAll rather than FindObjectsOfType, because a renderer the game has
        /// DEACTIVATED is exactly what is being looked for - and then filtered to the loaded scene, since
        /// that call also returns every renderer on every asset and prefab the game has in memory.</summary>
        /// <param name="at">The player's position.</param>
        private static List<Renderer> Renderers(Vector3 at)
        {
            var found = new List<Renderer>();

            foreach (var renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null) continue;
                if (renderer.hideFlags != HideFlags.None) continue;

                var go = renderer.gameObject;
                if (go == null || !go.scene.IsValid()) continue;

                var bounds = renderer.bounds;
                var top = bounds.size == Vector3.zero ? go.transform.position.y : bounds.max.y;
                if (top <= at.y + RoofRise) continue;

                if (!InColumn(bounds, go.transform.position, at)) continue;

                found.Add(renderer);
            }

            found.Sort((a, b) => Top(b).CompareTo(Top(a)));
            return found;
        }

        private static float Top(Renderer renderer)
        {
            var bounds = renderer.bounds;
            return bounds.size == Vector3.zero ? renderer.transform.position.y : bounds.max.y;
        }

        /// <summary>Whether a box (or, for a zero-size one, a point) is within the column.</summary>
        /// <param name="bounds">The renderer's world bounds.</param>
        /// <param name="position">Its transform position, for a box with no size.</param>
        /// <param name="at">The player's position.</param>
        private static bool InColumn(Bounds bounds, Vector3 position, Vector3 at)
        {
            float dx;
            float dz;

            if (bounds.size == Vector3.zero)
            {
                dx = position.x - at.x;
                dz = position.z - at.z;
            }
            else
            {
                dx = Mathf.Max(0f, Mathf.Abs(bounds.center.x - at.x) - bounds.extents.x);
                dz = Mathf.Max(0f, Mathf.Abs(bounds.center.z - at.z) - bounds.extents.z);
            }

            return dx * dx + dz * dz <= ColumnRadius * ColumnRadius;
        }

        /// <summary>One renderer as one line: everything the game will tell us about why it might not be
        /// in a picture taken from above.</summary>
        /// <param name="renderer">The renderer to describe.</param>
        /// <param name="cullers">What the scene's cullers hold.</param>
        private static string Describe(Renderer renderer, Held cullers)
        {
            var go = renderer.gameObject;
            var bounds = renderer.bounds;

            var line = new StringBuilder();

            line.Append($"top {F(Top(renderer)),9}  ");
            line.Append($"{renderer.GetType().Name,-16} ");
            line.Append($"layer {LayerName(go.layer),-22} ");
            line.Append($"enabled {YesNo(renderer.enabled)} ");
            line.Append($"inHierarchy {YesNo(go.activeInHierarchy)} ");
            line.Append($"activeSelf {YesNo(go.activeSelf)} ");
            line.Append($"staticBatch {YesNo(renderer.isPartOfStaticBatch)} ");
            line.Append($"shadows {renderer.shadowCastingMode} ");
            line.Append($"box [{F(bounds.min.x)},{F(bounds.min.y)},{F(bounds.min.z)} .. " +
                        $"{F(bounds.max.x)},{F(bounds.max.y)},{F(bounds.max.z)}] ");
            line.Append($"held [{HeldBy(renderer, go, cullers)}] ");
            line.Append($"lod [{Lod(renderer)}] ");
            line.Append($"mats [{Materials(renderer)}] ");
            line.Append($"path {Path_(go.transform)}");

            return line.ToString();
        }

        private static string Materials(Renderer renderer)
        {
            try
            {
                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) return "none";

                var parts = new List<string>(materials.Length);

                foreach (var material in materials)
                {
                    if (material == null)
                    {
                        parts.Add("null");
                        continue;
                    }

                    var shader = material.shader != null ? material.shader.name : "no shader";
                    parts.Add($"{shader} q{material.renderQueue}");
                }

                return string.Join(" | ", parts.ToArray());
            }
            catch (Exception ex)
            {
                return $"unreadable ({ex.GetType().Name})";
            }
        }

        /// <summary>Which LOD group governs this renderer, how many levels it has, which level this
        /// renderer belongs to, and how large the group is on the live camera right now - the number the
        /// LOD system actually decides on, computed the way Unity documents it (the group's world size
        /// over the distance to the camera, divided by the view's height at that distance).</summary>
        /// <param name="renderer">The renderer to look up.</param>
        private static string Lod(Renderer renderer)
        {
            try
            {
                var group = renderer.GetComponentInParent<LODGroup>();
                if (group == null) return "none";

                var levels = group.GetLODs();
                var mine = -1;

                for (var i = 0; i < levels.Length && mine < 0; i++)
                {
                    var members = levels[i].renderers;
                    if (members == null) continue;

                    foreach (var member in members)
                    {
                        if (member == null || member.GetInstanceID() != renderer.GetInstanceID()) continue;
                        mine = i;
                        break;
                    }
                }

                var relative = "n/a";
                var camera = LiveCamera();

                if (camera != null)
                {
                    var distance = Vector3.Distance(
                        camera.transform.position,
                        group.transform.TransformPoint(group.localReferencePoint));

                    if (distance > 0.01f)
                    {
                        var height = 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                        if (height > 0.0001f) relative = F(group.size / height);
                    }
                }

                var thresholds = string.Join("/", levels.Select(l => F(l.screenRelativeTransitionHeight)).ToArray());

                return $"{group.name} enabled {YesNo(group.enabled)} levels {group.lodCount} mine {mine} " +
                       $"size {F(group.size)} relative {relative} thresholds {thresholds}";
            }
            catch (Exception ex)
            {
                return $"unreadable ({ex.GetType().Name})";
            }
        }

        // --- what the cullers hold ---------------------------------------------------------------

        /// <summary>Every component and GameObject the scene's DisablerCullingObjects switch, and which
        /// culler and which of its three lists each came from.</summary>
        private sealed class Held
        {
            public int Count;
            public readonly Dictionary<int, string> Components = new Dictionary<int, string>();
            public readonly Dictionary<int, string> Objects = new Dictionary<int, string>();
        }

        private static Held Cullers()
        {
            var held = new Held();

            try
            {
                foreach (var culler in Resources.FindObjectsOfTypeAll<DisablerCullingObject>())
                {
                    if (culler == null || culler.hideFlags != HideFlags.None) continue;
                    if (culler.gameObject == null || !culler.gameObject.scene.IsValid()) continue;

                    held.Count++;

                    Note(held.Components, culler._componentsToTurnOff, culler.name, "components");
                    Note(held.Components, culler._compsToTurnOffWhoIgnoreInversedColliders, culler.name, "ignoreInverse");
                    NoteObjects(held.Objects, culler._gameObjectsToTurnOff, culler.name);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the roof probe could not read the cullers ({ex.Message}).");
            }

            return held;
        }

        private static void Note(Dictionary<int, string> into, List<Component> components, string culler, string list)
        {
            if (components == null) return;

            foreach (var component in components)
            {
                if (component == null) continue;

                var id = component.GetInstanceID();
                into[id] = into.TryGetValue(id, out var already) ? $"{already}, {culler}.{list}" : $"{culler}.{list}";
            }
        }

        private static void NoteObjects(Dictionary<int, string> into, List<GameObject> objects, string culler)
        {
            if (objects == null) return;

            foreach (var item in objects)
            {
                if (item == null) continue;

                var id = item.GetInstanceID();
                into[id] = into.TryGetValue(id, out var already)
                    ? $"{already}, {culler}.objects"
                    : $"{culler}.objects";
            }
        }

        /// <summary>Which culler lists this renderer or its object appears in, walking up the hierarchy
        /// for the object case: a culler switches a parent off and every roof under it goes with it.</summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="go">Its GameObject.</param>
        /// <param name="cullers">What the cullers hold.</param>
        private static string HeldBy(Renderer renderer, GameObject go, Held cullers)
        {
            var parts = new List<string>();

            if (cullers.Components.TryGetValue(renderer.GetInstanceID(), out var asComponent)) parts.Add(asComponent);

            var group = renderer.GetComponentInParent<LODGroup>();
            if (group != null && cullers.Components.TryGetValue(group.GetInstanceID(), out var asGroup))
            {
                parts.Add($"lodgroup:{asGroup}");
            }

            var transform = go.transform;
            var depth = 0;

            while (transform != null && depth < 24)
            {
                if (cullers.Objects.TryGetValue(transform.gameObject.GetInstanceID(), out var asObject))
                {
                    parts.Add(depth == 0 ? asObject : $"{asObject} ({depth} up)");
                }

                transform = transform.parent;
                depth++;
            }

            return parts.Count == 0 ? "nothing" : string.Join(" + ", parts.ToArray());
        }

        // --- the other things in the column ------------------------------------------------------

        /// <summary>The cameras, lights and reflection probes in the column, which is what a sheet of
        /// blue-green over a warehouse could also be.</summary>
        /// <param name="text">The file being built.</param>
        /// <param name="at">The player's position.</param>
        private static void AppendOthers(StringBuilder text, Vector3 at)
        {
            text.AppendLine("--- cameras, lights and reflection probes in the column ---");

            var any = false;

            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!Near(camera, at)) continue;

                any = true;
                text.AppendLine(
                    $"camera  {camera.name} enabled {YesNo(camera.enabled)} depth {F(camera.depth)} " +
                    $"mask 0x{camera.cullingMask:X8} path {Path_(camera.transform)}");
            }

            foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (!Near(light, at)) continue;

                any = true;
                text.AppendLine(
                    $"light   {light.name} enabled {YesNo(light.enabled)} type {light.type} " +
                    $"intensity {F(light.intensity)} shadows {light.shadows} path {Path_(light.transform)}");
            }

            foreach (var probe in Resources.FindObjectsOfTypeAll<ReflectionProbe>())
            {
                if (!Near(probe, at)) continue;

                any = true;
                text.AppendLine(
                    $"probe   {probe.name} enabled {YesNo(probe.enabled)} mode {probe.mode} " +
                    $"intensity {F(probe.intensity)} box [{F(probe.size.x)}x{F(probe.size.y)}x{F(probe.size.z)}] " +
                    $"path {Path_(probe.transform)}");
            }

            if (!any) text.AppendLine("none.");
        }

        private static bool Near(Component component, Vector3 at)
        {
            if (component == null || component.hideFlags != HideFlags.None) return false;

            var go = component.gameObject;
            if (go == null || !go.scene.IsValid()) return false;

            var position = go.transform.position;
            var dx = position.x - at.x;
            var dz = position.z - at.z;

            return dx * dx + dz * dz <= ColumnRadius * ColumnRadius;
        }

        // --- output ------------------------------------------------------------------------------

        /// <summary>Writes the file to BepInEx/plugins/QuestTree/captures/&lt;map&gt;.roofprobe.txt -
        /// beside the capture folders rather than inside one, so nothing that reads a capture ever sees
        /// it. Returns the path, or null when it could not be written.</summary>
        /// <param name="map">The map's internal name.</param>
        /// <param name="text">The whole file.</param>
        private static string Write(string map, string text)
        {
            try
            {
                var modPath = System.IO.Path.GetDirectoryName(typeof(RoofProbe).Assembly.Location);
                if (string.IsNullOrEmpty(modPath))
                {
                    Plugin.LogSource?.LogWarning("QuestTree: the plugin has no file location, so the roof probe cannot write.");
                    return null;
                }

                var dir = System.IO.Path.Combine(modPath, "captures");
                Directory.CreateDirectory(dir);

                var path = System.IO.Path.Combine(dir, $"{Stem(map)}.roofprobe.txt");
                File.WriteAllText(path, text);
                return path;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the roof probe could not write its file ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        private static string Stem(string map)
        {
            var stem = new StringBuilder(map.Length);

            foreach (var c in map)
            {
                stem.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return stem.Length == 0 ? "unknown" : stem.ToString();
        }

        private string MapName()
        {
            try
            {
                var map = _gameWorld?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(map)) map = _gameWorld?.LocationId;
                return string.IsNullOrEmpty(map) ? "unknown" : map;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>The live first-person camera, for the LOD relative size - the same two steps
        /// MapCapture uses.</summary>
        private static Camera LiveCamera()
        {
            try
            {
                var manager = EFT.CameraControl.CameraManager.instance;
                if (manager != null && manager.Camera != null) return manager.Camera;
            }
            catch
            {
                // Camera.main below.
            }

            return Camera.main;
        }

        /// <summary>The full hierarchy path of a transform, root first.</summary>
        /// <param name="transform">The transform.</param>
        private static string Path_(Transform transform)
        {
            var parts = new List<string>();
            var walk = transform;
            var depth = 0;

            while (walk != null && depth < 24)
            {
                parts.Add(walk.name);
                walk = walk.parent;
                depth++;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string LayerName(int layer)
        {
            var name = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(name) ? $"#{layer}" : $"{name}({layer})";
        }

        private static string YesNo(bool value) => value ? "yes" : "no ";

        private static string F(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "n/a" : v.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Now() =>
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
