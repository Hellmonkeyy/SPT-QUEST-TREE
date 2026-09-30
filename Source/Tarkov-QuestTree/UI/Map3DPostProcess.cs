using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace QuestTree.UI
{
    /// <summary>
    /// Lighting stage 4 (2026-09-28): an optional tonemap and ambient occlusion for the 3D map's private camera, run
    /// through the PostProcessing v2 stack the game itself ships and already has loaded. Off unless
    /// <see cref="ModSettings.PostProcessingWanted"/> says otherwise, and off whenever the game has no
    /// PostProcessResources loaded: this class never creates the resources asset, because its shaders only exist
    /// inside the game's own asset bundles and a resources object without them would draw black.
    ///
    /// The shape, and why:
    ///   - a PostProcessLayer on the viewer's camera, looking only at the viewer's private draw layer, with no
    ///     anti-aliasing (the viewer's target already has MSAA) and no deferred fog (the viewer's fog is the forward
    ///     scene fog it sets itself inside its render bracket);
    ///   - one global PostProcessVolume on that same private layer, whose GameObject is INACTIVE except for the
    ///     length of the viewer's own Camera.Render. PostProcessManager is one global registry: a game camera whose
    ///     layer's volumeLayer happens to include our layer index (a mask of Everything, say) would otherwise be
    ///     graded by our profile. Inactive, the volume is unregistered and nobody sees it. The layer is chosen free of
    ///     renderers and camera masks but not of the game's volumes, so every switch-on checks that ours is the only
    ///     volume the layer sees, and the profile overrides every parameter of every effect type besides;
    ///   - a profile built in code with ColorGrading (HDR grading mode with a tonemap following the capture's: ACES, or
    ///     Neutral for the game's default RomB) and scalable ambient obscurance, the one AO mode PPv2 can run in the
    ///     forward path without compute shaders; every other effect type is in the profile switched off;
    ///   - allowHDR left as the viewer set it: since spot-sun stage D (2026-09-29) the viewer turns it on, with an
    ///     ARGBHalf target, exactly when <see cref="Attach"/> succeeded, and anchors its exposure to the tonemap (a sunlit
    ///     white at 1.8, above 1, which an ARGB32 target would clip before the tonemap saw it). The viewer's own
    ///     calibration renders proves the path is HDR and falls back to the plain anchor when it is not.
    ///
    /// Every entry point here catches its own failure, logs one line and returns to the off state: a failure must never
    /// leave the volume active or the layer on the camera.
    /// </summary>
    internal static class Map3DPostProcess
    {
        /// <summary>The tonemapper used when a capture carries no Prism tonemap name, or one this class has no match for.
        /// ACES because it is the curve the game's own Prism offers under the same name, and the one that holds a
        /// bright roof's colour best.</summary>
        private const Tonemapper DefaultTonemapper = Tonemapper.ACES;

        /// <summary>Whether the capture's recorded Prism tonemap picks the PPv2 tonemapper (see <see cref="TonemapperFor"/>).
        /// False keeps <see cref="DefaultTonemapper"/> for every map - the rollback if a mapped curve reads worse than ACES.</summary>
        private static readonly bool FollowCapturedTonemap = true;

        /// <summary>The exposure under the tonemap, in EV. A tonemap's shoulder darkens every mid-tone; 0.6 EV (about x1.5)
        /// lifts the mid-tones back and lets the shoulder compress only the highlights. Since spot-sun stage D the viewer
        /// anchors its own exposure (a sunlit white at 1.8) and its emission scale to the curve with this in it, read back
        /// by its calibration, so a change here moves the anchor's reading, which the log reports. THE knob to turn after
        /// a play-test.</summary>
        internal const float PostExposure = 0.6f;

        /// <summary>Scalable AO's strength. The project renders in GAMMA colour space, so the forward composite multiplies
        /// the occlusion into display-encoded colour: a factor that would take a linear value to 0.7 takes the encoded one
        /// there instead, which is about 0.45 in light - occlusion reads about twice as strong as the same intensity in a
        /// linear project, where PPv2's defaults were tuned. The 2026-09-28 test also saw the shadows go very dark with AO
        /// on, and occlusion multiplies over the shadow rather than beside it. Scalable AO is not linear in its intensity
        /// (it raises intensity x occlusion to the power 0.6), so 0.35 gives about 0.72 of what 0.6 gave, not half; if
        /// the next test still reads dark, about 0.2 is the step that halves it. Rollback: 0.6f.</summary>
        private const float AoIntensity = 0.35f;

        /// <summary>Scalable AO's sampling radius, in metres. 2 m reaches a room's corners and a street's kerbs; the
        /// default quarter metre is sized for a first-person view and vanishes at map distances. Left at 2 m by the
        /// 2026-09-28 change: the complaint was how dark occlusion goes, which is the intensity, while the radius sets how
        /// far it spreads; changing one knob keeps the next play-test readable.</summary>
        private const float AoRadius = 2f;

        /// <summary>Whether ambient occlusion runs on a frame drawn with the floor cut's oblique near plane. Off, because an
        /// oblique projection rewrites the depth buffer's z mapping, and scalable AO reconstructs positions from that depth
        /// with the camera's near and far only - on a cut frame it would shade by the wrong depths. The tonemap is unaffected
        /// and stays on.</summary>
        private static readonly bool AoOnCutFrames = false;

        /// <summary>The volume's priority. High, so that were some other global volume ever on our private layer, ours would
        /// still be the one applied last.</summary>
        private const float VolumePriority = 1000f;

        /// <summary>The depth textures scalable AO asks the camera for (ScalableAO.GetCameraFlags). The layer ORs them in and
        /// never takes them out, so a frame without AO would pay two depth passes for nothing unless they are cleared.</summary>
        private const DepthTextureMode AoDepthFlags = DepthTextureMode.Depth | DepthTextureMode.DepthNormals;

        /// <summary>EFT's PostProcessLayer sets this global texture from every layer's render (its own addition to PPv2,
        /// RenderBuiltins). It is read back and put back around the viewer's render so the game's cameras never see the
        /// white texture ours leaves behind.</summary>
        private static readonly int AutoExposureTexId = Shader.PropertyToID("_BuiltInAutoExposureTex");

        private static readonly FieldInfo ResourcesField =
            typeof(PostProcessLayer).GetField("m_Resources", BindingFlags.Instance | BindingFlags.NonPublic);

        private static PostProcessResources _resources;
        private static string _resourcesVia;

        private static Camera _camera;
        private static PostProcessLayer _layer;
        private static ProjectionKeeper _keeper;
        private static GameObject _volumeGo;
        private static PostProcessVolume _volume;
        private static PostProcessProfile _profile;
        private static AmbientOcclusion _ao;
        private static Tonemapper _tonemapper = DefaultTonemapper;
        private static DepthTextureMode _depthWas;

        private static bool _exposureSaved;
        private static Texture _exposureWas;

        /// <summary>Cut frames on which PPv2's OnPreCull ran after the keeper's and so undid the floor cut. A static rather
        /// than the keeper's own field, so the count survives the camera's GameObject being destroyed before Detach reads it.</summary>
        private static int _cutUndone;

        private static readonly List<PostProcessVolume> VolumesSeen = new List<PostProcessVolume>();

        /// <summary>The last failure's one-line reason, so <see cref="Describe"/> can say why it is off.</summary>
        private static string _failure;

        /// <summary>Why the viewer took the stack off (<see cref="TakeOff"/>), for <see cref="Describe"/>; cleared by
        /// <see cref="Attach"/>.</summary>
        private static string _offReason;

        /// <summary>Whether the layer and the volume are really on the camera now. Internal since spot-sun stage D: the
        /// viewer decides HDR and its exposure anchor on this, not on the setting, which asks without knowing whether the
        /// game had the stack's resources.</summary>
        internal static bool Attached => _layer != null && _volumeGo != null;

        /// <summary>The tonemapper the attached profile runs (ACES or Neutral), for the viewer's first-frame line.</summary>
        internal static string TonemapperName => _tonemapper.ToString();

        /// <summary>Whether the setting asks for post-processing at all. The viewer logs the probe line only when this is
        /// true, so a player who never turned the setting on sees no new line.</summary>
        internal static bool Wanted => ModSettings.PostProcessingWanted;

        /// <summary>
        /// MAIN THREAD, once per view, before <see cref="Attach"/>. Finds the PostProcessResources the game is rendering
        /// with: first the live FPS camera's layer's private m_Resources, then any other loaded game layer's, and only then
        /// any loaded resources asset. The game's own object comes first because RuntimeUtilities.UpdateResources, which
        /// every layer's render calls, destroys and rebuilds PPv2's shared copy materials whenever the resources differ from
        /// the last layer's - a second asset would make that happen on every switch between our render and the game's.
        /// Caches the result and returns the one line the caller logs. While the setting is off it searches nothing.
        /// </summary>
        internal static string Probe()
        {
            if (!Wanted) return "post-processing off (setting)";

            try
            {
                // A resources asset the scene change unloaded compares equal to null here, which is what sends us looking again.
                if (Usable(_resources)) return $"post-processing resources found via {_resourcesVia} (cached)";

                _resources = null;
                _resourcesVia = null;

                var game = FromFpsCamera();
                if (Usable(game))
                {
                    _resources = game;
                    _resourcesVia = "the FPS camera's PostProcessLayer (the game's own object)";
                }

                PostProcessResources anyLayer = null;
                if (_resources == null)
                {
                    anyLayer = FromAnyGameLayer();
                    if (Usable(anyLayer))
                    {
                        _resources = anyLayer;
                        _resourcesVia = "a loaded game PostProcessLayer (the game's own object)";
                    }
                }

                if (_resources == null)
                {
                    foreach (var candidate in Resources.FindObjectsOfTypeAll<PostProcessResources>())
                    {
                        if (!Usable(candidate)) continue;

                        _resources = candidate;
                        var reference = game != null ? game : anyLayer;
                        _resourcesVia = reference == null
                            ? $"the loaded asset '{candidate.name}' (no game layer loaded to compare it with)"
                            : ReferenceEquals(reference, candidate)
                                ? $"the loaded asset '{candidate.name}' (the same object as the game's layer)"
                                : $"the loaded asset '{candidate.name}' (NOT the game layer's object - its shared copy materials will be rebuilt on every switch)";
                        break;
                    }
                }

                return _resources != null
                    ? $"post-processing resources found via {_resourcesVia}"
                    : "no PostProcessResources loaded - post-processing unavailable";
            }
            catch (Exception ex)
            {
                _resources = null;
                _resourcesVia = null;
                Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing probe failed ({ex.GetType().Name}: {ex.Message}) - post-processing unavailable.");
                return "post-processing probe failed - post-processing unavailable";
            }
        }

        /// <summary>A resources asset is only worth attaching with when what our two effects and the final pass need is in
        /// it: the uber pass carries the grading, copyStd the NaN guard and initial blit, lut2DBaker the grading LUT when the
        /// compute baker is not used, and the blue noise the final pass's dithering.</summary>
        private static bool Usable(PostProcessResources resources) =>
            resources != null
            && resources.shaders != null
            && resources.shaders.uber != null
            && resources.shaders.copyStd != null
            && resources.shaders.lut2DBaker != null
            && resources.blueNoise64 != null
            && resources.blueNoise64.Length > 0;

        /// <summary>The FPS camera's layer carries its resources in a private serialized field. CameraManager.instance is
        /// read, never CameraManager.Instance: that getter CONSTRUCTS a manager when there is none, which in the menu would
        /// be the game's camera manager built by us.</summary>
        private static PostProcessResources FromFpsCamera()
        {
            var manager = CameraManager.instance;
            if (manager == null) return null;

            PostProcessLayer layer = null;
            var camera = manager.Camera;
            if (camera != null) layer = camera.GetComponent<PostProcessLayer>();
            if (layer == null) layer = manager._postProcessLayer;

            return ResourcesOf(layer);
        }

        /// <summary>Any loaded PostProcessLayer that is not ours - the menu's own camera carries one when there is no FPS camera.</summary>
        private static PostProcessResources FromAnyGameLayer()
        {
            foreach (var layer in Resources.FindObjectsOfTypeAll<PostProcessLayer>())
            {
                if (layer == null || ReferenceEquals(layer, _layer)) continue;

                var resources = ResourcesOf(layer);
                if (Usable(resources)) return resources;
            }

            return null;
        }

        private static PostProcessResources ResourcesOf(PostProcessLayer layer) =>
            layer != null && ResourcesField != null ? ResourcesField.GetValue(layer) as PostProcessResources : null;

        /// <summary>
        /// MAIN THREAD, after the viewer's camera is made. Attaches the layer and builds the (inactive) volume when the
        /// setting is on and <see cref="Probe"/> found resources; otherwise attaches nothing. <paramref name="capturedTonemap"/>
        /// is the capture's Prism tonemap name (its lighting block's PrismTonemap), or null. On any failure everything
        /// already made is torn down again and false is returned; <paramref name="note"/> is the reason either way.
        /// </summary>
        internal static bool Attach(Camera camera, int layer, out string note, string capturedTonemap = null)
        {
            // One viewer at a time: a second attach first takes down whatever the first left. Reference checks, because a
            // camera Unity has already destroyed compares equal to null yet its profile still wants destroying.
            if (!ReferenceEquals(_camera, null) || !ReferenceEquals(_profile, null) || Attached) Detach(_camera);

            _failure = null;
            _offReason = null;
            _cutUndone = 0;

            if (!Wanted) { note = "post-processing off (setting)"; return false; }
            if (!Usable(_resources)) { note = "post-processing unavailable (no resources)"; return false; }
            if (camera == null || layer < 0 || layer > 31) { note = "post-processing unavailable (no camera or layer)"; return false; }

            try
            {
                _camera = camera;
                _depthWas = camera.depthTextureMode;
                _tonemapper = TonemapperFor(capturedTonemap);

                // The volume first, and inactive BEFORE its component is added: PostProcessVolume registers itself with the
                // global manager in OnEnable, under the layer its GameObject has at that moment, so the layer is set first
                // and the object stays inactive until SetActive(true).
                _volumeGo = new GameObject("QuestTreeMap3DPostVolume");
                _volumeGo.SetActive(false);
                _volumeGo.layer = layer;
                _volumeGo.transform.SetParent(camera.transform, false);

                // HideAndDontSave keeps Resources.UnloadUnusedAssets off a profile only our volume references; Detach
                // destroys it and its settings explicitly for the same reason.
                _profile = ScriptableObject.CreateInstance<PostProcessProfile>();
                _profile.hideFlags = HideFlags.HideAndDontSave;

                // Every parameter of our two effects overridden, so nothing of another volume's grading (a saturation, a
                // curve, a colour filter) can blend through underneath ours.
                var grading = _profile.AddSettings<ColorGrading>();
                grading.hideFlags = HideFlags.HideAndDontSave;
                grading.SetAllOverridesTo(true);
                grading.gradingMode.Override(GradingMode.HighDefinitionRange);
                grading.tonemapper.Override(_tonemapper);
                grading.postExposure.Override(PostExposure);

                _ao = _profile.AddSettings<AmbientOcclusion>();
                _ao.hideFlags = HideFlags.HideAndDontSave;
                _ao.SetAllOverridesTo(true);
                _ao.mode.Override(AmbientOcclusionMode.ScalableAmbientObscurance);
                _ao.intensity.Override(AoIntensity);
                _ao.radius.Override(AoRadius);

                // And every other effect type the stack knows, present and switched off, so no bloom, vignette or grain can
                // be switched on under us by anything.
                foreach (var type in PostProcessManager.instance.settingsTypes.Keys)
                {
                    if (type == typeof(ColorGrading) || type == typeof(AmbientOcclusion)) continue;

                    var off = _profile.AddSettings(type);
                    off.hideFlags = HideFlags.HideAndDontSave;
                    off.enabled.Override(false);
                }

                _volume = _volumeGo.AddComponent<PostProcessVolume>();
                _volume.isGlobal = true;
                _volume.priority = VolumePriority;
                _volume.weight = 1f;
                _volume.sharedProfile = _profile;   // sharedProfile, not profile: the profile getter clones it into a second instance

                // The layer's Awake runs inside AddComponent and hooks its four command buffers onto the camera; Init then
                // hands it the resources, which it first reads at the camera's OnPreCull, so the order here is free.
                _layer = camera.gameObject.AddComponent<PostProcessLayer>();
                _layer.Init(_resources);
                _layer.volumeLayer = 1 << layer;
                _layer.volumeTrigger = camera.transform;
                _layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                _layer.stopNaNPropagation = true;
                if (_layer.fog != null) _layer.fog.enabled = false;

                // There is no AutoExposure in our profile; off, the layer binds the plain white exposure texture rather than
                // generating a histogram it would never use.
                _layer.computeAutoExposure = false;

                // Added AFTER the layer, so its OnPreCull follows the layer's - see ProjectionKeeper.
                _keeper = camera.gameObject.AddComponent<ProjectionKeeper>();

                note = Describe();
                return true;
            }
            catch (Exception ex)
            {
                _failure = $"attach failed: {ex.GetType().Name}";
                Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing could not be attached ({ex.GetType().Name}: {ex.Message}) - it stays off.");
                Detach(camera);
                note = $"post-processing unavailable ({_failure})";
                return false;
            }
        }

        /// <summary>The PPv2 tonemapper for the Prism one the capture recorded. Prism's ACES is ACES; its Filmic and RomB
        /// (the game's default) are both gentle filmic shoulders, which Neutral is the nearest PPv2 has to.</summary>
        private static Tonemapper TonemapperFor(string prism)
        {
            if (!FollowCapturedTonemap || string.IsNullOrEmpty(prism)) return DefaultTonemapper;

            if (string.Equals(prism, "ACES", StringComparison.OrdinalIgnoreCase)) return Tonemapper.ACES;
            if (string.Equals(prism, "Filmic", StringComparison.OrdinalIgnoreCase)) return Tonemapper.Neutral;
            if (string.Equals(prism, "RomB", StringComparison.OrdinalIgnoreCase)) return Tonemapper.Neutral;

            return DefaultTonemapper;
        }

        /// <summary>
        /// MAIN THREAD, inside the viewer's render bracket only: true just before Camera.Render, false in the bracket's
        /// finally. <paramref name="keepProjection"/> is whether the frame carries the floor cut's oblique projection
        /// (ApplyCut's result): PPv2 resets the camera's projection in its OnPreCull, and the keeper puts the cut back.
        /// A failure while switching on - or another volume found on our layer - takes the whole stack off rather than
        /// leave a half-switched or foreign-graded render.
        /// </summary>
        internal static void SetActive(bool on, bool keepProjection = false)
        {
            if (!on)
            {
                SwitchOff();
                return;
            }

            if (!Attached || _camera == null) return;

            try
            {
                // Read back before our layer overwrites it at OnPreCull, put back in SwitchOff.
                _exposureWas = Shader.GetGlobalTexture(AutoExposureTexId);
                _exposureSaved = true;

                if (_keeper != null)
                {
                    _keeper.Keep = keepProjection;
                    _keeper.Projection = keepProjection ? _camera.projectionMatrix : Matrix4x4.identity;
                }

                // The layer re-reads the volume at every render, so a per-frame switch of AO is honoured on the frame it is
                // made. The depth textures follow it: cleared back to the viewer's own on a frame without AO, asked for on a
                // frame with it (the layer would OR them in itself at OnPreCull; asking here does not depend on that timing).
                var aoOn = !keepProjection || AoOnCutFrames;
                if (_ao != null) _ao.enabled.Override(aoOn);
                _camera.depthTextureMode = aoOn ? _depthWas | AoDepthFlags : _depthWas;

                _volumeGo.SetActive(true);

                // A check that can fail: the layer must see exactly one volume, ours. A game volume left on our layer index
                // would blend its extras into the map, and the private layer is chosen free of renderers and cameras only.
                VolumesSeen.Clear();
                PostProcessManager.instance.GetActiveVolumes(_layer, VolumesSeen);
                if (VolumesSeen.Count != 1 || !ReferenceEquals(VolumesSeen[0], _volume))
                {
                    var count = VolumesSeen.Count;
                    VolumesSeen.Clear();
                    _failure = $"{count} volume(s) on the private layer, not just ours";
                    Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing layer sees {count} volume(s), not only its own - it is taken off.");
                    Detach(_camera);
                    return;
                }

                VolumesSeen.Clear();
            }
            catch (Exception ex)
            {
                _failure = $"switch-on failed: {ex.GetType().Name}";
                Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing could not be switched on ({ex.GetType().Name}: {ex.Message}) - it is taken off.");
                Detach(_camera);
            }
        }

        /// <summary>The volume off and the global exposure texture put back, each in its own guard so one refusal does not
        /// skip the other. Safe with nothing attached.</summary>
        private static void SwitchOff()
        {
            try { if (_volumeGo != null) _volumeGo.SetActive(false); }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing volume could not be switched off ({ex.GetType().Name}: {ex.Message})."); }

            try { if (_keeper != null) _keeper.Keep = false; }
            catch (Exception) { /* the keeper only acts on the viewer's own renders; nothing further to try */ }

            if (_exposureSaved)
            {
                _exposureSaved = false;
                try { Shader.SetGlobalTexture(AutoExposureTexId, _exposureWas); }
                catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the game's auto-exposure texture could not be put back ({ex.GetType().Name}: {ex.Message})."); }
                _exposureWas = null;
            }
        }

        /// <summary>
        /// MAIN THREAD, in the viewer's Release before the camera's GameObject is discarded. Takes the layer, the keeper,
        /// the volume and the profile down and gives the camera back its depth-texture flags. Safe to call twice and with a
        /// null camera. A camera that is not the one attached is ignored: a rebuilt viewport's new view attaches before the
        /// old view's deferred Release runs, and the old view's Detach must not take the new stack down.
        /// </summary>
        internal static void Detach(Camera camera)
        {
            // ReferenceEquals throughout: a destroyed camera compares equal to null, and the old view's camera is often
            // destroyed by the time its Release runs - that must still count as "not ours".
            if (!ReferenceEquals(camera, null) && !ReferenceEquals(_camera, null) && !ReferenceEquals(camera, _camera)) return;

            SwitchOff();

            if (_cutUndone > 0)
            {
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map post-processing - the floor cut was undone by the stack on {0} frame(s).", _cutUndone));
                _cutUndone = 0;
            }

            // Disabled before it is destroyed: Destroy is deferred to the end of the frame, and a layer still enabled would
            // run once more at the next Camera.Render in this frame. OnDisable also takes its command buffers off the camera.
            try
            {
                if (_layer != null)
                {
                    _layer.enabled = false;
                    UnityEngine.Object.Destroy(_layer);
                }
            }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing layer could not be removed ({ex.GetType().Name}: {ex.Message})."); }

            try
            {
                if (_keeper != null)
                {
                    _keeper.Keep = false;
                    _keeper.enabled = false;
                    UnityEngine.Object.Destroy(_keeper);
                }
            }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's projection keeper could not be removed ({ex.GetType().Name}: {ex.Message})."); }

            try { if (_volumeGo != null) UnityEngine.Object.Destroy(_volumeGo); }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing volume could not be removed ({ex.GetType().Name}: {ex.Message})."); }

            try
            {
                if (_profile != null)
                {
                    foreach (var settings in _profile.settings)
                        if (settings != null) UnityEngine.Object.Destroy(settings);
                    UnityEngine.Object.Destroy(_profile);
                }
            }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's post-processing profile could not be removed ({ex.GetType().Name}: {ex.Message})."); }

            // Only the camera this class changed, and only while Unity still has it.
            try
            {
                if (_camera != null) _camera.depthTextureMode = _depthWas;   // the layer ORs Depth|DepthNormals in for AO and never takes them out
            }
            catch (Exception ex) { Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's camera flags could not be put back ({ex.GetType().Name}: {ex.Message})."); }

            _layer = null;
            _keeper = null;
            _volumeGo = null;
            _volume = null;
            _profile = null;
            _ao = null;
            _camera = null;
        }

        /// <summary>
        /// MAIN THREAD. Play-test 2026-09-29: the viewer's tonemap fallback (a calibration that read NOT DRAWN or CLIPPED,
        /// or failed) takes the whole stack off its camera, so the plain anchor's frame is not graded with ACES and the
        /// post-exposure - which drew the view blown out to white. <paramref name="reason"/> is what
        /// <see cref="Describe"/> says from then on. Safe with nothing attached.
        /// </summary>
        internal static void TakeOff(Camera camera, string reason)
        {
            // only the camera attached (or none): another view's stack is not this view's to take off, nor to describe
            var ours = ReferenceEquals(_camera, null) || ReferenceEquals(camera, _camera);
            Detach(camera);
            if (ours) _offReason = reason;
        }

        /// <summary>For the viewer's first-frame log line: what is running, or why nothing is.</summary>
        internal static string Describe()
        {
            if (Attached)
            {
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} tonemap, exposure {1:+0.0;-0.0} EV, SAO {2:0.0##} (off on cut frames), {3} intermediate",
                    _tonemapper, PostExposure, AoIntensity, _camera != null && _camera.allowHDR ? "HDR" : "LDR");

                // A check that can fail: the keeper counts every cut frame on which PPv2 ran AFTER it and so undid the cut.
                if (_cutUndone > 0)
                    line += string.Format(CultureInfo.InvariantCulture, " (the floor cut was undone by the stack on {0} frame(s))", _cutUndone);

                return line;
            }

            if (!Wanted) return "post-processing off (setting)";
            if (_offReason != null) return $"post-processing off ({_offReason})";
            if (_failure != null) return $"post-processing unavailable ({_failure})";
            if (!Usable(_resources)) return "post-processing unavailable (no resources)";
            return "post-processing not attached";
        }

        /// <summary>
        /// PostProcessLayer.OnPreCull calls Camera.ResetProjectionMatrix whenever the camera is not physical, which would
        /// throw away the floor cut's oblique projection the viewer set just before Render. This component, added after
        /// the layer, re-applies the cut in its own OnPreCull. Unity calls a GameObject's camera callbacks in component
        /// order in practice but does not promise it, so the order is checked every cut frame: if the camera still holds
        /// our matrix when we run, the layer has not run yet and will reset it after us, and that frame is counted in
        /// <see cref="_cutUndone"/> for <see cref="Describe"/> and <see cref="Detach"/> to report.
        /// </summary>
        private sealed class ProjectionKeeper : MonoBehaviour
        {
            public bool Keep;
            public Matrix4x4 Projection = Matrix4x4.identity;

            private Camera _own;

            private void OnPreCull()
            {
                if (!Keep) return;

                try
                {
                    if (_own == null) _own = GetComponent<Camera>();
                    if (_own == null) return;

                    if (_own.projectionMatrix == Projection) _cutUndone++;
                    _own.projectionMatrix = Projection;
                }
                catch (Exception)
                {
                    // Nothing to put right from here: the frame draws uncut, which is the viewer's own fallback too.
                }
            }
        }
    }
}
