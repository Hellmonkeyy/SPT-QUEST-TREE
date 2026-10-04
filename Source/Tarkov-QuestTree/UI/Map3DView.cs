using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuestTree.QuestGraph;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The map as geometry: the captured picture draped over the ground heights a raid measured, with the
    /// buildings standing on it, rendered into the Maps tab's viewport.
    ///
    /// HOW IT DRAWS, and why this shape rather than any of the obvious ones. There is no scene, no
    /// MeshRenderer and no enabled camera anywhere in here. Every frame this component queues its meshes
    /// for one private camera with <see cref="Graphics.DrawMesh(Mesh, Matrix4x4, Material, int, Camera)"/>
    /// and then renders that camera BY HAND. Phase 3-0 measured both candidates in the real menu
    /// (captures/menu.meshprobe.txt, 2026-09-23): this path lit 64 of 64 sampled pixels at 0.37 ms a
    /// frame, and the alternative - an enabled camera with <c>cullingMask = 0</c> and a CommandBuffer on
    /// AfterForwardOpaque - drew nothing at all. So this is the path that works, and the mechanics below
    /// are that experiment's, promoted to production code.
    ///
    /// WHY NOTHING OF OURS LEAKS INTO THE MENU OR THE HIDEOUT, which is the risk a second camera and a
    /// second light in someone else's scene actually carries:
    ///
    ///   - the camera is <c>enabled = false</c>, so Unity never renders it in its own loop; the only
    ///     render it ever does is the <see cref="Camera.Render"/> call in <see cref="RenderNow"/>;
    ///   - it draws one layer, chosen at runtime as a layer with NO Renderer on it at all, active or
    ///     inactive (see <see cref="ChoosePrivateLayer"/>) - a camera-mask test is the wrong test,
    ///     because EFT switches distant renderers off and a layer that looks free is then a statement
    ///     about where the player is standing;
    ///   - the one light - a far SPOT standing in for the sun (spot-sun stage C, 2026-09-29: a directional
    ///     light's shadows go through the game's replacement screen-space collect, which drew full shadow
    ///     on every receiver; a spot's are sampled per fragment in the ForwardAdd pass and never pass
    ///     through it) - is disabled and is switched on ONLY inside the render bracket, and off again in a
    ///     finally. Each render places it along the sun's direction at a distance fitted to what the view
    ///     shows (FitView), so its near-parallel rays light and shadow like the sun's. A per-light culling
    ///     mask is honoured in forward rendering (the path this camera uses, for the oblique cut - see
    ///     PrivateCameraPath) and is not a guarantee under the deferred path the game uses, so the mask is
    ///     not the safeguard: the light being off outside those two statements is;
    ///   - scene fog is switched off for the render and restored in the same finally, because our
    ///     geometry is hundreds of metres across and the menu's fog would swallow it;
    ///   - <see cref="OnDisable"/> stops rendering entirely, and <see cref="OnDestroy"/> destroys every
    ///     mesh, material, texture, camera and light it made. The viewport is destroyed by
    ///     MapView.DiscardViewport and by the menu teardown, and this component goes with it.
    ///
    /// The camera and the light are ROOT GameObjects rather than children of the viewport. That is not an
    /// oversight: a Camera under a uGUI canvas inherits the canvas's transform, and an overlay canvas is
    /// positioned and scaled in SCREEN pixels - a camera under one has its view matrix scaled by the
    /// canvas scale factor and offset by half the screen, which is not a camera that can be placed in
    /// world space. They are destroyed explicitly, and they are ordinary objects of the menu scene (not
    /// DontDestroyOnLoad), so a scene change takes them even if nothing else does.
    ///
    /// WHAT THE MARKERS DO. In 2D the map container's local units ARE map metres, so a pin is placed once
    /// and pan and zoom move it for free. Here there is no such rect - the ground is geometry inside a
    /// camera - so this component implements <see cref="IOverlayHost"/> the other way round: it records
    /// each registered rect's map (x, z) and re-places it every frame from
    /// <see cref="Camera.WorldToViewportPoint(Vector3)"/>, at the ground height under that point plus
    /// a metre. The builders in MapView cannot tell which host they have.
    /// </summary>
    internal sealed class Map3DView : MonoBehaviour, IOverlayHost
    {
        // --- the view's constants -----------------------------------------------------------------

        /// <summary>The camera's vertical field of view. 45 as the experiment ran it.</summary>
        private const float FieldOfView = 45f;

        private const float NearClip = 0.5f;

        /// <summary>Far enough for the diagonal of the biggest map from outside it. Streets' extent is
        /// about 1.3 km across, and the dolly is capped at 1.5 times the diagonal.</summary>
        private const float FarClip = 4000f;

        /// <summary>The closest the dolly comes. Thirty metres is about a building's width across the
        /// screen: closer than that the relief's 2 m cells are visible as facets and there is nothing
        /// more to see.</summary>
        private const float MinDistance = 30f;

        /// <summary>The furthest, as a multiple of the extent's diagonal.</summary>
        private const float MaxDistanceOfDiagonal = 1.5f;

        private const float MinPitch = 15f;
        private const float MaxPitch = 89f;

        /// <summary>The opening tilt. Steep enough to read the map as a map, shallow enough that the
        /// buildings have sides.</summary>
        private const float DefaultPitch = 55f;

        /// <summary>Degrees of orbit per screen pixel dragged.</summary>
        internal const float OrbitDegreesPerPixel = 0.3f;

        /// <summary>How much of the distance one wheel notch takes off.</summary>
        internal const float DollyPerNotch = 0.12f;

        /// <summary>
        /// LIGHT LIKE THE GAME, stage 1 (review 2026-09-28, the viewer alone; the captured sun follows in stage 3). The
        /// game renders in GAMMA colour space (the capture's exposure gamma is 1.0 there), where the Standard shader adds
        /// light in display values: the old sun 1.2 x sin 45 + a Trilight sky of 0.93 + a fill came to about twice white
        /// on a roof, so any roof over half grey clipped (washed out), a shadow took away a quarter of that (invisible),
        /// and ambient equalled the sun (flat). Now an EXPOSURE BUDGET: the sun at <see cref="SunIntensity"/> (warm, the
        /// game's default sun colour), the ambient at <see cref="AmbientOfSun"/> of it (the game's ambient is a fraction
        /// of its sun), the whole scaled by <see cref="Exposure"/> so a white surface facing up in the sun lands at
        /// <see cref="WhiteInSun"/> - nothing clips, shadows take most of a surface's light
        /// (<see cref="ShadowStrength"/>). The Standard shader in gamma space also keeps 22 % of every surface for its
        /// dielectric specular (4 % in linear), so the diffuse it draws is 0.78 of the light: the exposure divides by
        /// that share, read from the colour space at run time (<see cref="DiffuseShare"/>). The fill light is gone: an
        /// SH light that turned with the view brightened whatever the viewer faced and read as "attached to the camera".
        /// </summary>
        private const float SunIntensity = 1f;

        private static readonly Color SunColour = new Color(1f, 0.82f, 0.57f);   // LevelSettings' default SunColor 255/209/145
        private const float AmbientOfSun = 0.45f;   // stage 1 review: at 0.35 a wall facing away from the sun (equator ambient only) went near black
        private const float WhiteInSun = 0.92f;

        /// <summary>
        /// Spot-sun stage D: the anchor on the tonemap path - a white up-facing surface in the sun is exposed to 1.8, ABOVE
        /// 1, and the tonemap's shoulder (ACES or Neutral, after the post-exposure) rolls it off to about 0.9 the way the
        /// game's own frame rolls off a sunlit roof, while the mid-tones keep their contrast. Only on a view whose
        /// calibration proved the path HDR (<see cref="_tonemapOn"/>): on an LDR target 1.8 would clip every lit face flat.
        /// Rollback: 0.92f, the plain path's <see cref="WhiteInSun"/>.
        /// </summary>
        private const float WhiteInSunTonemap = 1.8f;

        /// <summary>The white-in-the-sun anchor this view renders with: the calibrated <see cref="_tonemapAnchor"/> on the tonemap path,
        /// <see cref="WhiteInSun"/> on the plain one.</summary>
        private float WhiteAnchor => _tonemapOn ? _tonemapAnchor : WhiteInSun;

        /// <summary>
        /// Play-test 2026-09-29: the tonemap path's anchor as this view's calibration MEASURED it - the v whose read is
        /// <see cref="AnchorReadTarget"/> on the curve (<see cref="InverseTonemap"/>), clamped to [<see cref="WhiteInSun"/>,
        /// the top sample]. <see cref="WhiteInSunTonemap"/> until the calibration runs. By PPv2's ACES fit in gamma space
        /// with +0.6 EV, 1.8 reads about 0.96 - at the very top of the range the line reports against - and 1.0 about
        /// 0.88; the anchor taken from the curve lands a sunlit white at 0.9 whatever the grade really does.
        /// </summary>
        private float _tonemapAnchor = WhiteInSunTonemap;

        /// <summary>What a white up-facing surface in the sun should read after the tonemap: bright, under white.</summary>
        private const float AnchorReadTarget = 0.9f;

        /// <summary>The building shader's diffuse share of the light: the Standard shader keeps a dielectric specular
        /// reserve - 0.22 in gamma space, 0.04 in linear - and a Lambert shader (the Legacy family) keeps none.</summary>
        private float DiffuseShare =>
            _buildingShader != null && _buildingShader.name.StartsWith("Standard", StringComparison.Ordinal)
                ? QualitySettings.activeColorSpace == ColorSpace.Linear ? 0.96f : 0.78f
                : 1f;

        /// <summary>
        /// LIGHT LIKE THE GAME, stage 3: the light a view renders with - the CAPTURED one when the capture recorded the
        /// raid's light and it is usable (a sun above <see cref="MinCapturedSunElevation"/> with intensity over
        /// <see cref="MinCapturedSunIntensity"/> by day; an ambient with all 27 harmonics), else the stage 1 PRESET. The
        /// captured sun keeps its azimuth, so the real-time shadows lie along the ones baked into the picture, and its
        /// elevation is clamped to <see cref="MaxCapturedSunElevation"/> so a noon capture still throws a shadow of some
        /// length (the picture's are shorter - accepted). The captured ambient is EFT's own spherical harmonics, which
        /// EVALUATE TO DISPLAY (gamma) VALUES; Unity's Standard shader treats the probe as linear and converts what it
        /// evaluates to gamma, so the probe is re-projected (<see cref="ProbeRefit"/>). The captured ambient is used ONLY
        /// with the captured sun: it belongs to that sun's raid, and a night or overcast ambient under the preset's warm
        /// day sun would be a light the game never showed. The exposure budget then holds as in stage 1 - a white
        /// up-facing surface in the sun just under WhiteInSun - over the plan's own sun and ambient top.
        /// <see cref="UseCapturedLight"/> false keeps the preset for every map.
        /// </summary>
        private static readonly bool UseCapturedLight = true;

        private const float MinCapturedSunElevation = 15f;
        private const float MaxCapturedSunElevation = 55f;
        private const float MinCapturedSunIntensity = 0.05f;

        /// <summary>The probe is scaled in linear space; a factor k on the gamma result is k^gamma on the linear probe.</summary>
        private const float ProbeGamma = 2.2f;

        /// <summary>
        /// Review of stage 3: how EFT's gamma-valued harmonics become a linear probe. True: a LEAST-SQUARES REFIT - the
        /// probe whose evaluation is closest, over <see cref="RefitDirections"/> directions spread on the sphere, to the
        /// gamma-to-linear of EFT's own evaluation there, so that the shader's linear-to-gamma of it lands near EFT's value
        /// in EVERY direction. False (the first stage 3 cut): every coefficient of a channel scaled by
        /// GammaToLinear(top) / top - exact straight up only; the gamma curve is not a scale, so the dimmer sideways and
        /// downward values came out about 30 % and 90 % too bright against the game.
        /// </summary>
        private const bool ProbeRefit = true;

        /// <summary>The refit's sample count: well over the nine unknowns, spread evenly (a Fibonacci sphere).</summary>
        private const int RefitDirections = 64;

        /// <summary>The convention check's tolerance, per cent: EFT's own top formula against Unity's evaluation. A
        /// difference over this means the 27 floats are not packed as Unity's SphericalHarmonicsL2, and the probe would be
        /// a wrong light - refused, the preset ambient used.</summary>
        private const float ConventionTolerancePercent = 2f;

        /// <summary>The shape check's warning level, per cent: the probe's gamma result at the horizon and straight down
        /// against EFT's. It warns in the first-frame line and does not refuse (second order cannot follow a gamma curve
        /// exactly).</summary>
        private const float ShapeWarnPercent = 10f;

        /// <summary>The ambient's floor as a fraction of the sun (both at the top, display terms): a captured ambient
        /// under it is raised to it, so a face turned from the sun is not near black. 0 turns the floor off (the first
        /// stage 3 cut: the ambient as captured, however dim).</summary>
        private const float AmbientFloorOfSun = 0.3f;

        /// <summary>Spot-sun stage D: the ambient floor on the tonemap path. Off: the floor was there because on the plain
        /// path a face turned from the sun went near black at the 0.92 anchor; under the 1.8 anchor and the tonemap's toe
        /// the captured ambient (about 0.18 of the sun) already reads, and raising it flattens the game's own contrast.
        /// Rollback: <see cref="AmbientFloorOfSun"/>.</summary>
        private const float AmbientFloorOfSunTonemap = 0f;

        /// <summary>The ambient floor this view resolves its plan with (<see cref="ResolveAmbient"/>).</summary>
        private float AmbientFloor => _tonemapOn ? AmbientFloorOfSunTonemap : AmbientFloorOfSun;

        /// <summary>The sky dome and the fog take the captured sky and fog colours' HUE at the preset's brightness (see
        /// <see cref="Rescaled"/>), because TOD_Sky's colours are scene-referred and came out black and dark teal when drawn
        /// as they are. False: the preset colours regardless of the capture (the look before stage 3).</summary>
        private static readonly bool CapturedSkyHue = true;

        /// <summary>What the view lights with - resolved once per view from the entry's captured light or the preset.</summary>
        private sealed class LightPlan
        {
            internal bool Captured;
            internal string Why = "";
            internal string TimeOfDay = "";

            /// <summary>Towards the sun, unit; the light's forward is its negation.</summary>
            internal Vector3 SunDirection;

            internal Color SunColour;
            internal float SunIntensity;
            internal float ShadowStrength;
            internal float ElevationDegrees;
            internal float CapturedElevationDegrees;

            /// <summary>The captured ambient as a probe in LINEAR terms (re-projected), or null for the Trilight preset.</summary>
            internal SphericalHarmonicsL2? Probe;

            /// <summary>The ambient straight up, in display (gamma) terms - the exposure budget's ambient term: the probe's
            /// top as the shader will draw it when there is a probe, else the preset's.</summary>
            internal Color AmbientTop;

            /// <summary>The Trilight preset's ambient top under this plan's sun - the budget's ambient whenever the probe
            /// is not drawn (no probe, or the over-the-shoulder light).</summary>
            internal Color PresetAmbientTop;

            /// <summary>Why the captured ambient is not used although the captured sun is ("" when it is used).</summary>
            internal string AmbientWhy = "";

            /// <summary>Convention check: EFT's top formula against Unity's evaluation straight up, per cent (-1 = not run).</summary>
            internal float ConventionError = -1f;

            /// <summary>True when the probe came from the least-squares refit, false when from the top scaling.</summary>
            internal bool Refit;

            /// <summary>Shape check: the probe's gamma result at the horizon and straight down against EFT's, per cent.</summary>
            internal float ShapeError;

            /// <summary>The captured ambient's top over the sun's (brightest channels), before any floor.</summary>
            internal float AmbientOfSunCaptured;

            /// <summary>True when the ambient was raised to <see cref="AmbientFloorUsed"/>.</summary>
            internal bool AmbientRaised;

            /// <summary>Stage D: the floor the plan was resolved with (<see cref="AmbientFloor"/>: 0 on the tonemap path), so the
            /// log reports the floor applied, not the plain path's constant.</summary>
            internal float AmbientFloorUsed;

            internal Color Zenith;
            internal Color Horizon;
            internal Color Fog;

            /// <summary>True when the sky dome or the fog took a captured hue (<see cref="CapturedSkyHue"/>).</summary>
            internal bool SkyHueCaptured;
        }

        /// <summary>The plan is resolved once and kept for the view's life because the entry it is resolved from is fixed
        /// for the view's life too: Attach is the only writer of _entry, and a map change builds a new view (the sky mesh
        /// coloured from the plan is equally per view). A future path that swaps _entry on a live view must clear this
        /// and destroy _skyMesh.</summary>
        private LightPlan _plan;

        private LightPlan Plan => _plan ??= ResolveLight();

        /// <summary>The preset sun's direction (towards it), from the stage 1 pitch and yaw.</summary>
        private static Vector3 PresetSunDirection => -(Quaternion.Euler(SunPitch, SunYaw, 0f) * Vector3.forward);

        private LightPlan ResolveLight()
        {
            var plan = new LightPlan
            {
                SunDirection = PresetSunDirection,
                SunColour = SunColour,
                SunIntensity = SunIntensity,
                ShadowStrength = ShadowStrength,
                ElevationDegrees = SunPitch,
                AmbientTop = AmbientSky * (SunIntensity * AmbientOfSun),
                PresetAmbientTop = AmbientSky * (SunIntensity * AmbientOfSun),
                Zenith = SkyZenith,
                Horizon = SkyHorizon,
                Fog = SkyHorizon,
                Why = "no lighting block in the capture",
            };

            var captured = _entry?.Lighting;
            if (!UseCapturedLight) { plan.Why = "the captured light is switched off"; return plan; }
            if (captured == null) return plan;

            plan.TimeOfDay = captured.TimeOfDay ?? "";

            try
            {
                // the sun
                var sunOk = captured.HasSun && captured.SunColor.HasValue;
                var elevation = sunOk ? Mathf.Asin(Mathf.Clamp(captured.SunDirection.y, -1f, 1f)) * Mathf.Rad2Deg : 0f;

                if (sunOk && !captured.IsDay) { sunOk = false; plan.Why = "captured at night"; }
                else if (sunOk && elevation < MinCapturedSunElevation) { sunOk = false; plan.Why = $"the captured sun is {elevation:0} deg up, under {MinCapturedSunElevation:0}"; }
                else if (sunOk && captured.SunIntensity < MinCapturedSunIntensity) { sunOk = false; plan.Why = "the captured sun is out (overcast or night)"; }
                else if (!sunOk) plan.Why = "the capture has no sun";

                if (sunOk)
                {
                    var azimuth = Mathf.Atan2(captured.SunDirection.x, captured.SunDirection.z);
                    var clamped = Mathf.Min(elevation, MaxCapturedSunElevation) * Mathf.Deg2Rad;
                    plan.SunDirection = new Vector3(Mathf.Cos(clamped) * Mathf.Sin(azimuth), Mathf.Sin(clamped), Mathf.Cos(clamped) * Mathf.Cos(azimuth)).normalized;
                    plan.CapturedElevationDegrees = elevation;
                    plan.ElevationDegrees = Mathf.Min(elevation, MaxCapturedSunElevation);
                    plan.SunColour = new Color(Mathf.Max(0.05f, captured.SunColor.Value.r), Mathf.Max(0.05f, captured.SunColor.Value.g), Mathf.Max(0.05f, captured.SunColor.Value.b), 1f);
                    plan.SunIntensity = Mathf.Max(MinCapturedSunIntensity, captured.SunIntensity);
                    plan.ShadowStrength = captured.SunShadowStrength > 0.05f ? Mathf.Clamp01(captured.SunShadowStrength) : ShadowStrength;
                    plan.Captured = true;
                    plan.Why = "";
                }

                // the ambient: the preset's under the plan's sun, unless the captured sun is used and EFT's harmonics pass
                plan.PresetAmbientTop = AmbientSky * (plan.SunIntensity * AmbientOfSun);
                plan.AmbientTop = plan.PresetAmbientTop;
                if (plan.Captured) ResolveAmbient(plan, captured.AmbientSh, AmbientFloor);

                // the sky and the fog: the captured hue at the preset's brightness, since the captured values are
                // scene-referred (Customs at noon records a sky of 0.05) and the game's exposure that brightens them is not
                // captured; a colour too dark to carry a hue keeps the preset
                if (CapturedSkyHue)
                {
                    if (captured.SkyColor.HasValue && captured.SkyColor.Value.maxColorComponent >= 0.001f)
                    {
                        plan.Zenith = Rescaled(captured.SkyColor.Value, SkyZenith);
                        plan.SkyHueCaptured = true;
                    }

                    var horizon = captured.FogColor ?? captured.EquatorColor;
                    if (horizon.HasValue && horizon.Value.maxColorComponent >= 0.001f)
                    {
                        plan.Horizon = Rescaled(horizon.Value, SkyHorizon);
                        plan.Fog = plan.Horizon;
                        plan.SkyHueCaptured = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the captured light of {_mapKey} could not be used ({ex.GetType().Name}: {ex.Message}) - the preset lights it.");
                return new LightPlan
                {
                    SunDirection = PresetSunDirection, SunColour = SunColour, SunIntensity = SunIntensity, ShadowStrength = ShadowStrength,
                    ElevationDegrees = SunPitch, AmbientTop = AmbientSky * (SunIntensity * AmbientOfSun),
                    PresetAmbientTop = AmbientSky * (SunIntensity * AmbientOfSun),
                    Zenith = SkyZenith, Horizon = SkyHorizon, Fog = SkyHorizon, Why = "the captured light could not be used",
                };
            }

            return plan;
        }

        /// <summary>
        /// A captured sky or fog colour at the preset's brightness: its hue kept, its brightest channel set to the preset's.
        /// TOD_Sky's colours are scene-referred - the game brightens them through its Prism auto-exposure before they reach
        /// the screen, and that exposure is not captured - so drawn as they are through the unlit dome and as the fog they
        /// read as night (the dome went black, tall distant objects dark teal). The hue is what carries the weather (an
        /// overcast grey, a dusk orange), and the preset carries a brightness known to read well; the caller keeps the
        /// preset for a colour too dark (max under 0.001) to carry a hue.
        /// </summary>
        private static Color Rescaled(Color captured, Color preset)
        {
            var scale = preset.maxColorComponent / captured.maxColorComponent;
            return new Color(captured.r * scale, captured.g * scale, captured.b * scale, 1f);
        }

        /// <summary>
        /// Stage 3 review: EFT's ambient harmonics into a probe for the gamma-space Standard shader, with two checks that
        /// CAN fail. CONVENTION first: the game's own AmbientLight.GetColorTop reads its top as
        /// sh[c,1] + sh[c,0] - sh[c,6] - sh[c,8], which is exactly Unity's evaluation straight up when the 27 floats are
        /// packed as Unity's SphericalHarmonicsL2 - so the two are compared per channel, and a difference over
        /// <see cref="ConventionTolerancePercent"/> of the larger refuses the probe (the preset ambient is used). Then the
        /// re-projection (<see cref="ProbeRefit"/>), then SHAPE: the probe's gamma result at four horizon azimuths and
        /// straight down against EFT's own values there (the directions the top-only scaling got wrong). Then the floor
        /// (<see cref="AmbientFloorOfSun"/>). Leaves plan.Probe null, with plan.AmbientWhy saying why, when refused.
        /// </summary>
        private static void ResolveAmbient(LightPlan plan, float[] sh, float floorOfSun)
        {
            if (sh == null || sh.Length != 27) { plan.AmbientWhy = "the capture has no ambient harmonics"; return; }

            var eft = new SphericalHarmonicsL2();
            for (var c = 0; c < 3; c++)
                for (var i = 0; i < 9; i++)
                    eft[c, i] = sh[c * 9 + i];

            var top = EvaluateOne(eft, Vector3.up);
            if (!(top.r > 0.001f && top.g > 0.001f && top.b > 0.001f && top.maxColorComponent < 16f))
            {
                plan.AmbientWhy = string.Format(CultureInfo.InvariantCulture, "EFT ambient refused: its top {0:0.000}/{1:0.000}/{2:0.000} is out of range", top.r, top.g, top.b);
                return;
            }

            // (a) the convention check: the game's top formula against Unity's evaluation, per channel
            var convention = 0f;
            for (var c = 0; c < 3; c++)
            {
                var theirs = eft[c, 1] + eft[c, 0] - eft[c, 6] - eft[c, 8];
                var unitys = top[c];
                var larger = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(theirs), Mathf.Abs(unitys)));
                convention = Mathf.Max(convention, 100f * Mathf.Abs(theirs - unitys) / larger);
            }

            plan.ConventionError = convention;
            if (convention > ConventionTolerancePercent)
            {
                plan.AmbientWhy = string.Format(CultureInfo.InvariantCulture, "EFT ambient refused: convention check failed ({0:0.0} %)", convention);
                return;
            }

            // the re-projection: the refit, or (the rollback, or a singular system) the per-channel top scaling
            var probe = eft;
            var refitFailure = "";
            plan.Refit = ProbeRefit && RefitForGamma(eft, out probe, out refitFailure);
            if (!plan.Refit)
            {
                probe = ScaleByTop(eft, top);
                plan.AmbientWhy = refitFailure;   // "" under the rollback: top scaling chosen, not fallen back to
            }

            // (b) the shape check, before the floor (the floor is a deliberate departure from EFT, not an error of the fit)
            plan.ShapeError = ShapeError(eft, probe);

            // the top the shader will draw, for the exposure budget and the ground's division
            var drawn = EvaluateOne(probe, Vector3.up);
            plan.AmbientTop = new Color(
                Mathf.LinearToGammaSpace(Mathf.Max(0f, drawn.r)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, drawn.g)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, drawn.b)), 1f);

            // the floor: a dim captured ambient raised to a share of the sun; k on the gamma result is k^gamma on the probe
            var sunTop = Mathf.Max(0.0001f, plan.SunIntensity * plan.SunColour.maxColorComponent);
            plan.AmbientOfSunCaptured = plan.AmbientTop.maxColorComponent / sunTop;
            plan.AmbientFloorUsed = floorOfSun;
            var floor = floorOfSun * sunTop;
            if (floorOfSun > 0f && plan.AmbientTop.maxColorComponent < floor)
            {
                var ratio = floor / Mathf.Max(0.0001f, plan.AmbientTop.maxColorComponent);
                var linear = Mathf.Pow(ratio, ProbeGamma);
                for (var c = 0; c < 3; c++)
                    for (var i = 0; i < 9; i++)
                        probe[c, i] *= linear;

                plan.AmbientTop = new Color(plan.AmbientTop.r * ratio, plan.AmbientTop.g * ratio, plan.AmbientTop.b * ratio, 1f);
                plan.AmbientRaised = true;
            }

            plan.Probe = probe;
        }

        private static Color EvaluateOne(SphericalHarmonicsL2 sh, Vector3 direction)
        {
            var result = new Color[1];
            sh.Evaluate(new[] { direction }, result);
            return result[0];
        }

        /// <summary>The rollback re-projection: each channel's coefficients scaled by GammaToLinear(top) / top - exact
        /// straight up, too bright everywhere dimmer (see <see cref="ProbeRefit"/>).</summary>
        private static SphericalHarmonicsL2 ScaleByTop(SphericalHarmonicsL2 eft, Color top)
        {
            var probe = eft;
            for (var c = 0; c < 3; c++)
            {
                var factor = Mathf.GammaToLinearSpace(top[c]) / top[c];
                for (var i = 0; i < 9; i++)
                    probe[c, i] = eft[c, i] * factor;
            }

            return probe;
        }

        /// <summary>
        /// The least-squares re-projection: per channel, the nine coefficients minimising the sum over
        /// <see cref="RefitDirections"/> Fibonacci-sphere directions d of (probe(d) - GammaToLinear(max(0, EFT(d))))^2,
        /// from the normal equations (A^T A) c = A^T y. The basis columns A[., k] are MEASURED - a unit probe (coefficient
        /// k 1, the rest 0) evaluated over the directions - so the fit leans on no constant of Unity's basis, only on
        /// Evaluate being what the shader draws. That reliance is TESTED first: the fit is only right if Evaluate is the
        /// plain linear sum of the basis (a clamp at 0, like the shader's own max, would cut the negative half off every
        /// odd lobe and the fit would be silently wrong - and neither the convention check, all positive straight up, nor
        /// the shape check, which goes through the same Evaluate, would see it). False, with
        /// <paramref name="failure"/> saying why, when that test fails or the system is singular (it should not be for 64
        /// spread directions); the caller then falls back to the top scaling.
        /// </summary>
        private static bool RefitForGamma(SphericalHarmonicsL2 eft, out SphericalHarmonicsL2 probe, out string failure)
        {
            probe = eft;
            failure = "";
            var f = CultureInfo.InvariantCulture;

            // linearity, part one: the y-linear lobe alone must be odd - negative straight down, the exact opposite of up
            var yLobe = new SphericalHarmonicsL2();
            yLobe[0, 1] = 1f;
            var lobeUp = EvaluateOne(yLobe, Vector3.up).r;
            var lobeDown = EvaluateOne(yLobe, Vector3.down).r;
            if (!(lobeDown < 0f) || Mathf.Abs(lobeUp + lobeDown) >= 1e-4f)
            {
                failure = string.Format(f, "Evaluate is not the linear basis (the y lobe gives {0:0.0000} up, {1:0.0000} down) - top-scaled instead", lobeUp, lobeDown);
                return false;
            }

            var n = RefitDirections;
            var directions = new Vector3[n];
            var golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (var j = 0; j < n; j++)
            {
                var y = 1f - 2f * (j + 0.5f) / n;
                var r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                var phi = golden * j;
                directions[j] = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r);
            }

            // the measured basis (the red channel of each unit probe; the channels are independent)
            var basis = new double[n, 9];
            var values = new Color[n];
            for (var k = 0; k < 9; k++)
            {
                var unit = new SphericalHarmonicsL2();
                unit[0, k] = 1f;
                unit.Evaluate(directions, values);
                for (var j = 0; j < n; j++) basis[j, k] = values[j].r;
            }

            // the targets: EFT's display value in each direction (taken to linear per channel below)
            var target = new Color[n];
            eft.Evaluate(directions, target);

            // linearity, part two: EFT's own red evaluation must be the measured basis times its red coefficients
            for (var j = 0; j < n; j++)
            {
                double sum = 0;
                for (var k = 0; k < 9; k++) sum += basis[j, k] * eft[0, k];
                if (Math.Abs(target[j].r - sum) > 1e-4 * (1.0 + Math.Abs(sum)))
                {
                    failure = string.Format(f, "Evaluate is not the linear basis ({0:0.0000} against the basis sum {1:0.0000}) - top-scaled instead", target[j].r, sum);
                    return false;
                }
            }

            var normal = new double[9, 9];
            for (var k = 0; k < 9; k++)
                for (var l = 0; l < 9; l++)
                {
                    double sum = 0;
                    for (var j = 0; j < n; j++) sum += basis[j, k] * basis[j, l];
                    normal[k, l] = sum;
                }

            // per channel: the targets clamped at 0 (the shader clamps) and taken to linear, then the solve
            var solved = eft;
            var rhs = new double[9];
            var coefficients = new double[9];
            for (var c = 0; c < 3; c++)
            {
                for (var k = 0; k < 9; k++)
                {
                    double sum = 0;
                    for (var j = 0; j < n; j++) sum += basis[j, k] * Mathf.GammaToLinearSpace(Mathf.Max(0f, target[j][c]));
                    rhs[k] = sum;
                }

                if (!SolveLinear((double[,])normal.Clone(), rhs, coefficients))
                {
                    failure = "the refit's system is singular - top-scaled instead";
                    return false;
                }

                for (var k = 0; k < 9; k++)
                {
                    if (double.IsNaN(coefficients[k]) || double.IsInfinity(coefficients[k]))
                    {
                        failure = "the refit solved to a non-finite coefficient - top-scaled instead";
                        return false;
                    }

                    solved[c, k] = (float)coefficients[k];
                }
            }

            probe = solved;
            return true;
        }

        /// <summary>Gaussian elimination with partial pivoting on a square system, in place (a and b are overwritten);
        /// false when a pivot is effectively zero.</summary>
        private static bool SolveLinear(double[,] a, double[] b, double[] x)
        {
            var n = b.Length;
            for (var col = 0; col < n; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < n; row++)
                    if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;

                if (Math.Abs(a[pivot, col]) < 1e-12) return false;

                if (pivot != col)
                {
                    for (var k = 0; k < n; k++) { var t = a[col, k]; a[col, k] = a[pivot, k]; a[pivot, k] = t; }
                    var tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
                }

                for (var row = col + 1; row < n; row++)
                {
                    var f = a[row, col] / a[col, col];
                    for (var k = col; k < n; k++) a[row, k] -= f * a[col, k];
                    b[row] -= f * b[col];
                }
            }

            for (var row = n - 1; row >= 0; row--)
            {
                var sum = b[row];
                for (var k = row + 1; k < n; k++) sum -= a[row, k] * x[k];
                x[row] = sum / a[row, row];
            }

            return true;
        }

        /// <summary>The shape check: the probe's value, converted to gamma as the shader does (negatives clamped to 0),
        /// against EFT's own at four horizon azimuths and straight down - per channel, as a share of the brightest
        /// channel of EFT's TOP (floored at 0.01), the same yardstick for every direction: an error measured against the
        /// ambient's own brightness is one you can see, while measured against a dark direction's own value (the ground
        /// bounce straight down) a tiny absolute miss would read as a large per cent and cry wolf. The worst, per cent.</summary>
        private static float ShapeError(SphericalHarmonicsL2 eft, SphericalHarmonicsL2 probe)
        {
            var directions = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left, Vector3.down };
            var theirs = new Color[directions.Length];
            var ours = new Color[directions.Length];
            eft.Evaluate(directions, theirs);
            probe.Evaluate(directions, ours);

            var scale = Mathf.Max(0.01f, EvaluateOne(eft, Vector3.up).maxColorComponent);
            var worst = 0f;
            for (var j = 0; j < directions.Length; j++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var want = Mathf.Max(0f, theirs[j][c]);
                    var got = Mathf.LinearToGammaSpace(Mathf.Max(0f, ours[j][c]));
                    worst = Mathf.Max(worst, 100f * Mathf.Abs(got - want) / scale);
                }
            }

            return worst;
        }

        /// <summary>The light a white up-facing surface gets per channel under a plan, BEFORE the exposure: the sun's share
        /// (its intensity x its colour x the light direction's rise) plus the ambient top the view draws.</summary>
        private static Color UpLight(LightPlan plan, float upShare, bool sun)
        {
            var s = plan.SunIntensity * Mathf.Max(0f, upShare);
            var ambient = AmbientTopFor(plan, sun);
            return new Color(s * plan.SunColour.r + ambient.r, s * plan.SunColour.g + ambient.g, s * plan.SunColour.b + ambient.b, 1f);
        }

        /// <summary>
        /// Whether the view draws the captured probe: only in the Sun mode. EFT's probe is brighter on the real sun's
        /// side, which is fixed in the world; the over-the-shoulder light turns with the view, so under it that bright
        /// side would sit anywhere against the light - a face lit by both, its opposite by neither. The Trilight preset
        /// has no side and goes with any light direction.
        /// </summary>
        private static bool DrawsProbe(LightPlan plan, bool sun) => sun && plan.Probe.HasValue;

        /// <summary>The ambient top the view draws, display terms: the probe's, or the Trilight preset's.</summary>
        private static Color AmbientTopFor(LightPlan plan, bool sun) => DrawsProbe(plan, sun) ? plan.AmbientTop : plan.PresetAmbientTop;

        /// <summary>The factor on the sun and the ambient that puts a white up-facing surface in the sun at the view's
        /// anchor (<see cref="WhiteAnchor"/>, its brightest channel), for a light rising <paramref name="upShare"/> (sin of
        /// its elevation).</summary>
        private float Exposure(LightPlan plan, float upShare, bool sun) =>
            WhiteAnchor / Mathf.Max(0.01f, DiffuseShare * UpLight(plan, upShare, sun).maxColorComponent);

        /// <summary>The light the flat ground gets under the budget, PER CHANNEL - the ground picture, developed and already
        /// lit by the game at capture, is drawn at its own value and its own hue by dividing its colour by this. Alpha 1:
        /// the cutout ground's clip edge must not move.</summary>
        private Color FlatGroundLight(LightPlan plan, float upShare, bool sun)
        {
            var k = DiffuseShare * Exposure(plan, upShare, sun);
            var light = UpLight(plan, upShare, sun);
            return new Color(k * light.r, k * light.g, k * light.b, 1f);
        }

        /// <summary>The ground material's colour: one over the flat ground's light per channel, alpha 1. Stage D: times
        /// <see cref="_emissionScale"/> (1 on the plain path), so a lit-fallback ground on the tonemap path comes out of the
        /// tonemap at the value the emission sides beside it do.</summary>
        private Color GroundColour(LightPlan plan, float upShare, bool sun)
        {
            var light = FlatGroundLight(plan, upShare, sun);
            var k = _emissionScale;
            return new Color(k / Mathf.Max(0.05f, light.r), k / Mathf.Max(0.05f, light.g), k / Mathf.Max(0.05f, light.b), 1f);
        }

        /// <summary>The plan's sun rise for the mode in use: the sun's own, or the over-the-shoulder light's pitch.</summary>
        private float UpShareFor(LightPlan plan, bool sun) =>
            sun ? Mathf.Max(0f, plan.SunDirection.y) : Mathf.Sin(LightPitch * Mathf.Deg2Rad);

        /// <summary>
        /// Test 2026-09-28 (the light "not placed right"): the light was FIXED in the world, 50 degrees down at yaw -30,
        /// while the camera orbits - at the default view (yaw 0, looking along the capture's +Z) it sat about twenty degrees
        /// behind the camera, so every face the viewer saw was lit flat and the map read as unlit; turned to look the other
        /// way it would sit in front, and every visible wall would be in its own shadow. Two ways to light a map you orbit,
        /// chosen by ModSettings.MapLighting and set every render (a change of the setting rebuilds the viewport):
        /// - SUN (the default, the maintainer's choice): the key light stays fixed in the world, travelling towards yaw
        ///   <see cref="SunYaw"/> and <see cref="SunPitch"/> degrees down - it comes from the south-west of the capture's
        ///   frame (+Z north), behind-left of the default view, so at that view a wall facing the camera takes half the sun
        ///   (a 60-degree incidence), its west face the same and its east face none (form), and shadows lie one way across the whole map as
        ///   it is orbited (it reads as a place); the ambient (a third of the sun) lights the faces the sun does not.
        ///   The second 2026-09-28 test: a sun at 60 up and yaw -30 read as "attached to the camera" - it WAS nearly behind
        ///   the default view, and a fill light turned with the view - and cast shadows too short to see; 45 down makes
        ///   them 1.7 times longer. The fill is gone (see the exposure budget above).
        /// - OVER THE SHOULDER: one light that turns with the view, <see cref="LightYawOffset"/> degrees off the view's yaw
        ///   and <see cref="LightPitch"/> degrees down, so the faces the viewer sees are lit with form on both sides
        ///   whatever the yaw, and shadows fall away from the viewer.
        /// </summary>
        private const float SunPitch = 45f;

        private const float SunYaw = 45f;
        private const float LightPitch = 50f;
        private const float LightYawOffset = 40f;

        /// <summary>The lighting the setting asks for, the sun when the settings are not ready.</summary>
        private static ModSettings.MapLightMode Lighting =>
            ModSettings.Ready && ModSettings.MapLighting != null ? ModSettings.MapLighting.Value : ModSettings.MapLightMode.Sun;

        /// <summary>
        /// HQ S1.1: the private camera renders only when something it shows has changed - the view moved
        /// (<see cref="ViewVersion"/>), the render texture was remade, the cut height changed, the pipeline pumped
        /// (an upload, a cut, a wall job), the shared tile store cut or failed a tile, a material is still waiting
        /// for its picture, the first frame, or a finished build. A clean frame keeps the last image in the render
        /// texture, which the RawImage goes on showing. False renders every LateUpdate as before.
        /// </summary>
        private static readonly bool RenderOnChange = true;

        /// <summary>HQ S1.1: with <see cref="RenderOnChange"/>, a forced render every this many clean frames
        /// (0 = never) - a safety valve should a render texture ever lose its contents between changes.</summary>
        private static readonly int RenderHeartbeatFrames = 0;

        /// <summary>HQ S1.2: multisampling on the private render texture (1 = off, as before). The texture is up to
        /// 4096 x 4096 (EnsureRenderTexture), and at 4 samples that would be over half a gigabyte, so a texture over
        /// <see cref="MsaaPixelCap"/> pixels is made without it - a rule about the texture, never about a map.</summary>
        private static readonly int RenderMsaa = 4;

        private static readonly long MsaaPixelCap = 4_000_000;

        /// <summary>
        /// HQ S1.3: the sun's light casts shadows (None = the flat look, as before). The shadow settings are
        /// QualitySettings, global and the player's: the two the render sets (shadows, shadowDistance) are saved before
        /// it and put back after it (RenderNow), so the player's preset - which may have shadows off entirely - is
        /// untouched outside the bracket. Spot-sun stage C: the shadow distance reaches the farthest point of the fitted
        /// view from the camera (FitView), so every receiver on screen is inside it. Stage 0: the mode is the setting's
        /// (ModSettings.MapShadows), Soft when the settings are not ready; a change rebuilds the viewport.
        /// </summary>
        private static LightShadows ShadowMode
        {
            get
            {
                if (!ModSettings.Ready || ModSettings.MapShadows == null) return LightShadows.Soft;

                switch (ModSettings.MapShadows.Value)
                {
                    case ModSettings.MapShadowMode.Off: return LightShadows.None;
                    case ModSettings.MapShadowMode.Hard: return LightShadows.Hard;
                    default: return LightShadows.Soft;
                }
            }
        }

        private static readonly float ShadowStrength = 0.85f;   // stage 1: under the exposure budget a shadow keeps the ambient's third, as the game's do

        /// <summary>Whether the last render drew shadows at all (a cut floor turns them off - ShadowsUnderCut), for the
        /// first-frame line.</summary>
        private bool _shadowsDrawn;

        /// <summary>Stage 3: where the view's light came from, for the first-frame line - with the ambient's two checks (the
        /// convention, which refuses, and the shape, which warns) and its share of the sun.</summary>
        private string LightSource()
        {
            var plan = Plan;
            var f = CultureInfo.InvariantCulture;

            // the captured ambient is only ever used with the captured sun, so a preset sun means the preset for both
            var sky = plan.SkyHueCaptured ? "sky hue captured" : "sky preset";
            if (!plan.Captured) return $"preset ({plan.Why}), {sky}";

            var text = string.Format(f, "captured {0} (sun {1:0} deg up{2}, shadow strength {3:0.00})",
                string.IsNullOrEmpty(plan.TimeOfDay) ? "raid light" : "at " + plan.TimeOfDay,
                plan.ElevationDegrees,
                plan.CapturedElevationDegrees > plan.ElevationDegrees + 0.5f ? string.Format(f, " clamped from {0:0}", plan.CapturedElevationDegrees) : "",
                plan.ShadowStrength);

            if (!plan.Probe.HasValue)
                return text + ", preset ambient (" + (string.IsNullOrEmpty(plan.AmbientWhy) ? "no reason recorded" : plan.AmbientWhy) + "), " + sky;

            text += string.Format(f, ", EFT ambient (top {0:0.00}/{1:0.00}/{2:0.00}, convention {3:0.0} %, {4}, shape error {5:0.0} %{6}), ambient {7:0.00} of the sun{8}",
                plan.AmbientTop.r, plan.AmbientTop.g, plan.AmbientTop.b, plan.ConventionError,
                plan.Refit ? "refit" : string.IsNullOrEmpty(plan.AmbientWhy) ? "top-scaled" : plan.AmbientWhy,
                plan.ShapeError, plan.ShapeError > ShapeWarnPercent ? string.Format(f, " - WARNING over {0:0} %", ShapeWarnPercent) : "",
                plan.AmbientOfSunCaptured,
                plan.AmbientRaised ? string.Format(f, ", ambient raised to {0:0.00} of the sun", plan.AmbientFloorUsed) : "");

            if (Lighting != ModSettings.MapLightMode.Sun) text += " - not drawn over the shoulder, the preset ambient is";

            return text + ", " + sky;
        }

        /// <summary>Stage 1: the exposure factor, the ground's colour factor and whether the fog was drawn, of the last render,
        /// for the first-frame line.</summary>
        private float _exposureRendered;

        private Color _groundColour = Color.white;
        private bool _fogDrawn;

        /// <summary>
        /// The light's bias and normal bias when the light probe did not measure them (Map3DLightProbe.Result) - then the
        /// spot draws no shadows (<see cref="_spotShadowsWhy"/>), so these only fill the light and are never drawn with.
        ///
        /// Where the values come from instead. The 2026-09-28 in-game test drew acne (sun-facing walls black) with the
        /// directional light's defaults, because a directional bias is scaled by the cascade's texel size and the
        /// cascades reached 1,500 m. A spot's bias is not a cascade's: the built-in pipeline scales a spot light's
        /// shadowBias and shadowNormalBias with its own shadow map's texel, and how that behaves for a spot kilometres
        /// from the map was unknown. So nothing here is derived: the probe renders the production setup (the far spot,
        /// ForcePixel, the white cookie, the near plane just short of the lit area, range ten times the distance) and
        /// sweeps bias {0.05, 0.01, 0.002} and normal bias {0.4, 0.1}, keeping the largest pair that both shadows a 20 m
        /// box and leaves a quad tilted 60 degrees to the light free of acne. The viewer takes that pair as it is.
        ///
        /// Caveat: the probe measured at its own distance (Result.MaxDistanceOk) over a 100 m radius; the viewer's distance
        /// is fitted to the view and never past that one, but its radius and near plane are the view's. If acne or
        /// floating shadows show in a play-test, the probe's sweep is what to widen, not these.
        ///
        /// Rollback: none needed - with no probe result there are no shadows. 0.05f and 0.4f are Unity's defaults.
        /// </summary>
        private static readonly float ShadowBias = 0.05f;

        /// <summary>The normal bias when the probe did not measure one - see <see cref="ShadowBias"/>. Unity's default.</summary>
        private static readonly float ShadowNormalBias = 0.4f;

        /// <summary>Spot-sun stage C: the spot's distance from the fitted view's centre, as a multiple of the view's radius
        /// (FitView), clamped to <see cref="SpotDistanceMin"/> and the probe's largest passing distance. Fifteen radii puts
        /// the rays at the view's edge within 4 degrees of parallel - near enough to the sun's that the buildings' shadows
        /// lie along the picture's baked ones - while the shadow map's depth span (near plane D minus the radius) stays
        /// two radii deep.</summary>
        private const float SpotDistanceOfRadius = 15f;

        /// <summary>The spot's range as a multiple of its distance: the map sits at a tenth of the range, where Unity's spot
        /// attenuation is flat - the ratio the probe measured the attenuation at.</summary>
        private const float SpotRangeOfDistance = 10f;

        /// <summary>The cone's full angle over the one that exactly covers the fitted radius: a margin so the view's edge is
        /// not on the cone's edge.</summary>
        private const float SpotConeMargin = 1.05f;

        /// <summary>The smallest share of the spot's distance its shadow near plane may be: the spot is moved out until the
        /// near plane is at least this share of the distance (when the probe's maximum allows). The probe proved shadows at
        /// near / range of about 0.1 (near D - 150 over range 10 D); a near of 0.1 D over range 10 D is near / range 0.01,
        /// which keeps the receivers' depth precision within 10x of what was proved - a near of 0.1 m would not.</summary>
        private const float SpotNearShareOfDistance = 0.1f;

        /// <summary>The closest the spot comes, metres: a view zoomed to 30 m still gets near-parallel rays.</summary>
        private const float SpotDistanceMin = 500f;

        /// <summary>The farthest the spot goes when there is no probe result (then it casts no shadows): the probe's own
        /// farthest distance.</summary>
        private const float SpotDistanceNoProbe = Map3DLightProbe.SweepDistanceMax;

        /// <summary>The spot's attenuation divisor when there is no probe result: Unity's built-in spot falloff at a tenth
        /// of the range (<see cref="SpotRangeOfDistance"/>) is about 0.8, so the light is raised by 1 / 0.8.</summary>
        private const float AttenuationNoProbe = 1.25f;

        /// <summary>The smallest fitted radius, metres, so a degenerate fit still gives a cone.</summary>
        private const float FitRadiusMin = 1f;

        /// <summary>The shadow distance over the farthest fitted point from the camera: a margin so the last receivers on
        /// screen are not in the shadow fade.</summary>
        private const float ShadowDistanceMargin = 1.05f;

        /// <summary>The white 4x4 clamp cookie the probe proved: the default spot cookie is a round vignette, which would
        /// darken the fitted view's corners. Made once per view, destroyed with <see cref="_lightGo"/>.</summary>
        private Texture2D _lightCookie;

        /// <summary>The probe's result as this view took it at build: null when the probe did not run or this view's
        /// shader is not Standard (the only one the probe vouches for).</summary>
        private Map3DLightProbe.Result _probe;

        /// <summary>Why the spot draws no shadows whatever the setting says - "none (probe failed: why)", "none (probe not
        /// run)" or "none (legacy shader)" - null when the probe passed. Kept for the first-frame line.</summary>
        private string _spotShadowsWhy;

        /// <summary>The last render's spot: its distance from the fitted centre and the fitted radius, metres - for the
        /// first-frame line.</summary>
        private float _spotDistance;

        /// <summary>The last render's fitted radius, metres (FitView) - for the first-frame line.</summary>
        private float _spotRadius;

        /// <summary>The last render's shadow near plane over its range (the shadow map's far plane), as set - for the
        /// first-frame line, so the depth precision the shadow map had can be judged.</summary>
        private float _spotNearFarRatio;

        /// <summary>The last render's QualitySettings.shadowDistance, 0 when it drew no shadows - for the first-frame line.</summary>
        private float _spotShadowDistance;

        /// <summary>The union of every drawn floor's ground and building mesh bounds (world space: the meshes are drawn
        /// with the identity matrix). Set by <see cref="Finish"/>, cleared by <see cref="BeginBuild"/>.</summary>
        private Bounds _mapBounds;

        /// <summary>Whether <see cref="_mapBounds"/> holds at least one mesh's bounds; false until the build finishes or
        /// when it has no meshes, and then FitView falls back to the camera's focus.</summary>
        private bool _mapBoundsSet;

        /// <summary>The viewport points <see cref="FitView"/> casts rays through: the four corners and the centre.</summary>
        private static readonly Vector3[] FitViewportPoints =
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
        };

        /// <summary>
        /// HQ S1.3: whether a cut frame (the oblique near plane of the floor cut, ApplyCut) draws shadows. Off: the cut is
        /// only the camera's near plane, so the geometry it removes from view (upper storeys, roofs) would still cast onto
        /// the exposed floor; a cut frame is drawn without shadows, an uncut one (the top floor, a one-band map) with them.
        /// Spot-sun stage C keeps it off for the same reason: the spot's shadow map is rendered from the light, which
        /// never sees the cut.
        /// </summary>
        private static readonly bool ShadowsUnderCut = false;

        /// <summary>
        /// HQ S1.4: hemisphere ambient for the render - sky from above, ground bounce from below, an equator between
        /// - set inside the same bracket as the fog and put back after it (the menu scene runs Flat ambient at 0.6,
        /// and a Trilight left behind would tint the player's hideout). False leaves the scene's ambient as before.
        /// </summary>
        private static readonly bool AmbientTrilight = true;

        // test 2026-09-28: brighter than the menu's Flat 0.6 the view had before - the first values read as dusk
        /// <summary>Stage 1: the ambient's SHAPE - sky a little blue, ground a little warm - at unit brightness; its level is
        /// AmbientOfSun x the sun, scaled with it by the exposure at render time.</summary>
        private static readonly Color AmbientSky = new Color(0.92f, 0.96f, 1.00f);

        private static readonly Color AmbientEquator = new Color(0.95f, 0.95f, 0.95f);
        private static readonly Color AmbientGround = new Color(0.60f, 0.56f, 0.50f);

        /// <summary>Stage 1: aerial perspective - linear fog in the sky's horizon colour from a little past the camera's
        /// focus to well beyond it, set inside the bracket and put back. The game's world has fog; a map with none reads
        /// as a model. False forces fog off, as before.</summary>
        private static readonly bool AerialFog = true;

        private const float FogStartOfDistance = 1.2f;
        private const float FogSpanOfFarClip = 0.6f;

        /// <summary>
        /// HQ S1.4: a sky behind the map - one vertex-coloured dome the size of the far clip, centred on the camera
        /// each render, drawn through Hidden/Internal-Colored (the one vertex-colour shader this view resolves)
        /// with depth writes off, so every mesh draws over it. False keeps the solid backdrop colour.
        /// </summary>
        private static readonly bool SkyDome = true;

        private static readonly Color SkyZenith = new Color(0.40f, 0.55f, 0.78f);
        private static readonly Color SkyHorizon = new Color(0.78f, 0.83f, 0.90f);
        /// <summary>Under the horizon: the fog's own colour while the aerial fog is on (fogged ground at the map's edge must
        /// meet a dome of the same colour, not a dark band), else the old dark grey.</summary>
        private static Color SkyBelow => AerialFog ? SkyHorizon : new Color(0.30f, 0.31f, 0.33f);
        private const float SkyRadiusOfFarClip = 0.9f;
        private const int SkyRings = 8;
        private const int SkySegments = 24;

        /// <summary>Vertices per mesh chunk. Unity takes more than this in one mesh with
        /// <see cref="IndexFormat.UInt32"/>, but a chunked mesh is a mesh that can be freed and drawn in
        /// pieces, and the relief of a 4-million-cell band would otherwise be one 96 MB buffer.</summary>
        private const int MaxVerticesPerMesh = 250_000;

        /// <summary>
        /// Spatial chunking, in metres: every building triangle is bucketed by the cell of its CENTROID on a square XZ grid
        /// of this size from the extent's min corner, on top of the per-tile / roof / side / tint split - so each chunk is
        /// one (cell, material) and its bounds are a cell plus whatever its triangles overhang. The relief is cut into
        /// square blocks of about this size instead of full-width row strips. Why: a chunk in file order covers the whole
        /// map, so Unity's per-DrawMesh frustum culling never culls anything and a 30 m view draws all of Customs. Draw
        /// calls rise to a few thousand, of which only the cells in view reach the GPU. Rollback: 0 (one cell - the old
        /// file-order chunks and the full-width relief strips). Static readonly so the branch is not folded away.
        /// </summary>
        internal static readonly float SpatialChunkMetres = 128f;

        /// <summary>
        /// Compact vertices: position UNorm16x4 relative to the chunk's lattice-snapped origin (the origin and the uniform
        /// scale go through the DrawMesh matrix, <see cref="MeshFrame"/>), normal SNorm8x4, UV UNorm16x2 - 16 bytes a vertex
        /// against 32 - packed on the worker and uploaded with SetVertexBufferData. A chunk whose UVs do not fit [0, 1]
        /// after a whole-repeat shift (atlas faces many repeats long) keeps Float32x2 UVs (20 bytes); a flat-colour
        /// build adds a UNorm8x4 colour. Rollback: false (Vector3/Vector3/Vector2 arrays, identity matrix).
        /// </summary>
        internal static readonly bool CompactVertices = true;

        /// <summary>
        /// Sixteen-bit indices: every chunk is capped at <see cref="SixteenBitVertexLimit"/> vertices and drawn with
        /// <see cref="IndexFormat.UInt16"/> - half the index memory. The cap is hard: a building group's crease split is made
        /// before its triangles are cut into chunks (PrepareBuildings), and a chunk over the limit all the same would go up
        /// UInt32 on its own (MeshData.Pack, MakeFloatMesh), never with a wrapped index. Rollback: false (250,000-vertex
        /// UInt32 chunks).
        /// </summary>
        internal static readonly bool SixteenBitIndices = true;

        /// <summary>The most vertices a UInt16-indexed mesh can address.</summary>
        private const int SixteenBitVertexLimit = 65_535;

        /// <summary>The vertex count at which a building sink flushes its chunk: the normals (and the crease split) are made
        /// before the cut, so a chunk never grows after it.</summary>
        private static int ChunkVertexCap => SixteenBitIndices ? SixteenBitVertexLimit : MaxVerticesPerMesh;

        /// <summary>
        /// The merge rule for draw calls: a tile's (or a wall tint's) chunks on a floor are kept split by cell only when
        /// they come to more than this many vertices together; under it they are merged back into one chunk. Hundreds of
        /// tiles are a few small props each, and a draw call apiece per cell would be thousands of calls culling saves
        /// nothing on. 0 keeps every split.
        /// </summary>
        private const int CellSplitMinVertices = 5000;

        /// <summary>
        /// A chunk under this many vertices keeps float positions, normals and UVs (MeshData.Pack): Unity batches meshes
        /// under 300 vertices dynamically, on the CPU, from float data - a compact one would not batch. Its positions are
        /// still snapped to the <see cref="PositionQuantum"/> lattice, so it meets compact neighbours on the same points.
        /// </summary>
        private const int DynamicBatchVertices = 300;

        /// <summary>The relief's vertex cap per chunk: no crease split there, so the full sixteen-bit range.</summary>
        private static int ReliefVertexCap => SixteenBitIndices ? SixteenBitVertexLimit : MaxVerticesPerMesh;

        /// <summary>
        /// The ONE step every position is snapped to, in metres, for every chunk of every floor: 1/256 m (under 2 mm of
        /// error), a power of two so every origin is an exact float multiple of it and every lattice point an exact float
        /// (to 65 km). One lattice for all, so a vertex two chunks share (a relief block edge, a roof meeting its wall in
        /// the next cell) is the same lattice point in both - no crack. 65,535 steps span 256 m, more than a 128 m cell
        /// plus its overhang; a chunk wider than that (a merged small tile, a huge face) keeps float positions snapped to
        /// the same lattice rather than a coarser step - a coarsest-step-per-floor would be set by the merged tiles that
        /// span the whole map (3 cm on Customs). The file's own XZ quantum is the extent over 65,535 (1.7 cm on Customs),
        /// so this adds nothing visible to the data.
        /// </summary>
        private const double PositionQuantum = 1d / 256d;

        /// <summary>
        /// Opt B: the relief's simplification tolerance, metres. Inside each relief block, aligned squares of whole quads are
        /// merged bottom-up into one leaf (<see cref="ReliefSimplifier"/>) while all its (side + 1)^2 heights lie within a slab
        /// this thick around their least-squares plane - so any triangulation of the leaf's grid points, and the true surface,
        /// both lie in that slab, and the drawn ground is never more than this far from the full-resolution one. The outer quad
        /// ring of a block (its overlap with the neighbour, and the neighbour's overlap with it) is never merged, and a leaf
        /// with a smaller neighbour's corner on its side is drawn as a fan through it, so no crack and no T-junction. Rollback:
        /// 0 (every whole quad drawn, as before). Static readonly so the branch is not folded away.
        /// </summary>
        internal static readonly float ReliefSimplifyTolerance = 0.15f;

        /// <summary>The largest relief leaf, in quads a side (a power of two): 32 quads is 16 m at 0.5 m cells.</summary>
        private const int ReliefLeafMaxQuads = 32;

        /// <summary>
        /// Opt B: a renderer (one file "building") whose largest world extent is under this many metres is a SMALL PROP: its
        /// faces (not its roofs) go to chunks of their own (<see cref="SizeClassSmallProp"/>), which <see cref="Submit"/> skips
        /// when the prop would come to less than <see cref="SmallPropMinPixels"/> on screen.
        /// </summary>
        internal static readonly float SmallPropMetres = 4f;

        /// <summary>Opt B: the on-screen size, pixels, under which a small-prop chunk is not drawn - measured for the largest
        /// prop in the chunk at the chunk's nearest point to the camera, so it never hides a prop that would show. Rollback: 0
        /// (never hidden, and no small-prop chunks).</summary>
        internal static readonly float SmallPropMinPixels = 2f;

        /// <summary>Opt B: a renderer whose largest world extent is under this many metres is a SMALL BUILDING: its chunks cast
        /// no shadow when "3D map: small buildings cast shadows" is off. The 3D extent rather than the footprint, so a lamp post
        /// or a mast keeps its long shadow. Rollback: 0 (no small-building chunks).</summary>
        internal static readonly float SmallShadowMetres = 6f;

        /// <summary>A chunk's size class (<see cref="MeshData.SizeClass"/>): an ordinary one.</summary>
        private const byte SizeClassNormal = 0;

        /// <summary>A small building's chunk: no shadow under the setting.</summary>
        private const byte SizeClassSmallBuilding = 1;

        /// <summary>A small prop's chunk: no shadow under the setting, and not drawn when under the pixel threshold.</summary>
        private const byte SizeClassSmallProp = 2;

        /// <summary>The size class of a renderer of largest extent <paramref name="extent"/> m; a roof is never a small prop
        /// (the roofs carry the top picture, which the map is read by from far away), only a small building.</summary>
        private static byte SizeClassOf(float extent, bool roof)
        {
            if (!(extent >= 0f)) return SizeClassNormal;
            if (!roof && SmallPropMinPixels > 0f && extent < SmallPropMetres) return SizeClassSmallProp;
            return extent < SmallShadowMetres ? SizeClassSmallBuilding : SizeClassNormal;
        }

        /// <summary>The chunk-name suffix of a size class.</summary>
        private static string SizeSuffix(byte sizeClass) =>
            sizeClass == SizeClassNormal ? "" : sizeClass == SizeClassSmallProp ? "-prop" : "-small";

        /// <summary>The setting "3D map: small buildings cast shadows"; true (today's look) before the settings are ready.</summary>
        private static bool SmallBuildingShadowsWanted =>
            !ModSettings.Ready || ModSettings.MapSmallBuildingShadows == null || ModSettings.MapSmallBuildingShadows.Value;

        /// <summary>
        /// How far the mesh file's extent may differ from the picture's before the mesh is refused, in
        /// metres.
        ///
        /// Five centimetres, and the tightness is the point: the two are written from the SAME doubles by
        /// the same capture, and the only thing between them is the meta's extent being read back as a
        /// float - which at the ±2 km a map coordinate reaches costs about 0.0002 m. A metre of slack
        /// would have been 4,000 times the error it was there to absorb, and a check with that much slack
        /// in it is a check that cannot fail: a re-harvested extent moves by tens of metres (Interchange
        /// moved 1073x1033 against a meta of 965x925), but a cell-sized disagreement is exactly the kind
        /// that draws a plausible map with everything in the wrong place.
        /// </summary>
        private const float ExtentTolerance = 0.05f;

        /// <summary>The shaders this view will make a material from, in order of preference. All four
        /// were confirmed loaded and supported in the menu by phase 3-0 experiment 3; the first one
        /// wins, so in practice this is Standard. NOT UI/Default, which is ZWrite Off - a scene drawn
        /// with it has no depth order at all and a wall behind a hill draws in front of it.</summary>
        private static readonly string[] Shaders =
        {
            "Standard",
            "Legacy Shaders/Diffuse",
            "Unlit/Texture",

            // The floor, and the only one of the four with no texture: it draws vertex colours. A view
            // that falls through to here gets flat grey geometry and says so in the log.
            "Hidden/Internal-Colored"
        };

        /// <summary>The flat colours used when only Hidden/Internal-Colored resolved: the ground and the
        /// buildings, told apart by shade so the map is still readable without the picture on it.</summary>
        private static readonly Color32 FlatGroundColour = new Color32(120, 118, 110, 255);
        private static readonly Color32 FlatBuildingColour = new Color32(168, 164, 156, 255);

        // --- what one drawn floor holds -----------------------------------------------------------

        /// <summary>One floor of the peel: its relief, its buildings, and the two materials that carry its
        /// picture. Two per floor and not one per mesh, because the texture is the only thing that
        /// differs between the meshes - and TWO rather than one because the ground clips on the picture's
        /// alpha and the buildings must not. See <see cref="MakeGroundMaterial"/>.</summary>
        private sealed class Floor
        {
            public int Level;

            /// <summary>The map layer this floor's picture comes from, or null when the mesh has a band
            /// the pictures do not - which happens to a capture whose upper floor's PNG was dropped.
            /// Drawn untextured rather than not drawn: the shape of the ground is most of the value.</summary>
            public DynamicMapsLibrary.MapLayer Layer;

            /// <summary>The relief's material: alpha-clipped where the shader has a cutout, so the
            /// picture's transparent surround does not draw as a black apron over ground the player can
            /// never reach. See <see cref="MakeGroundMaterial"/>.</summary>
            public Material GroundMaterial;

            /// <summary>The buildings' material: always OPAQUE. A wall's UVs are the planar ones the
            /// ground uses, so a wall standing near the edge of the walkable area samples transparent
            /// pixels up its height - and a cutout there would cut the wall away rather than the
            /// ground.</summary>
            public Material BuildingMaterial;

            /// <summary>Pictures stage C: the roofs' material - the top picture drawn as EMISSION (the game's own light and
            /// shadows, never lit twice) when <see cref="RoofsActive"/> holds on the emission path, else the SAME object as
            /// <see cref="BuildingMaterial"/>, so everything that draws a roof with it draws exactly as before. A separate
            /// material because BuildingMaterial stays the lit fallback that tints and side faces draw with while their own
            /// material waits. Destroyed at teardown only when it is not BuildingMaterial.</summary>
            public Material RoofMaterial;

            /// <summary>The geometry, which this floor does NOT own: it belongs to the static cache and
            /// outlives every view of this map. See <see cref="Built"/>.</summary>
            public Built Meshes;
        }

        /// <summary>
        /// One band's built geometry, cached across views.
        ///
        /// The meshes are the expensive part of opening a 3D map - 151,000 vertices triangulated and
        /// uploaded - and EVERY repaint destroys the viewport: a quest row clicked, a floor stepped, a
        /// setting touched, the pin filter turned. Rebuilding them per click was a hitch per click. So
        /// they are built once per (file, floor) and handed to each new view; the materials are not
        /// cached, because they are one allocation each and they carry per-view state.
        ///
        /// Which means nothing here may be destroyed by a view's teardown while it is still cached. It
        /// is dropped by <see cref="DropCaches"/>, which MapView calls when the map memory goes: a
        /// profile change, a capture landing, and the menu teardown before a raid - the last of those
        /// being the one that matters, since a Mesh is not a scene object and a scene change does not
        /// take it.
        ///
        /// REFERENCE-COUNTED, because a drop and a live view are not ordered. MapView.ForgetDrawnMap
        /// destroys the viewport and then drops the caches in the same frame, and Destroy is deferred -
        /// so the view is still drawing when its meshes are asked to go. A drop therefore takes each
        /// entry OUT of the cache at once (no later view can reuse it) but only destroys the meshes of
        /// an entry nobody is drawing; an entry still in use is marked <see cref="Orphaned"/>, and the
        /// last view to let go of it destroys it (<see cref="Unuse"/>). Every order ends with the meshes
        /// destroyed exactly once and never under a view that is using them.
        /// </summary>
        internal sealed class Built
        {
            public readonly List<Mesh> Ground = new List<Mesh>();
            public readonly List<Mesh> Buildings = new List<Mesh>();

            /// <summary>How many live views are drawing this entry.</summary>
            public int Users;

            /// <summary>Taken out of the cache by a drop while still in use: the last user destroys it.</summary>
            public bool Orphaned;

            /// <summary>Set only when the ground AND the buildings were both built. An entry put in the
            /// cache before its build (so a throw halfway cannot leak what was already made) and never
            /// finished is not reused - it is destroyed and built again.</summary>
            public bool Complete;

            public long GroundTriangles;
            public long BuildingTriangles;
            public int BuildingCount;
            public long Cells;

            /// <summary>Opt B: the relief's owned triangles before simplification (<see cref="ReliefSimplifyTolerance"/>);
            /// <see cref="GroundTriangles"/> is after. For the first-frame line.</summary>
            public long GroundTrianglesFull;

            /// <summary>The buildings' vertical faces, one entry per colour: see <see cref="WallTint"/>.
            /// Empty until <see cref="WallsPending"/> clears. Owned here like the rest of the geometry -
            /// meshes AND materials - and destroyed with it by <see cref="DestroyBuilt"/>.</summary>
            public readonly List<WallTint> Walls = new List<WallTint>();

            /// <summary>Wall triangles found by the roof pass, whether or not the walls are built yet -
            /// counted into <see cref="BuildingTriangles"/> so the totals do not change when they are.</summary>
            public long WallTriangles;

            /// <summary>The walls still have to be built, because their colours come from this floor's
            /// picture and the picture was not decoded yet when the roofs were. Built by the first frame
            /// that has it - see <see cref="Map3DView.Draw"/>.</summary>
            public bool WallsPending;

            /// <summary>How many colours the walls were built in, for the log line.</summary>
            public int Tints;

            /// <summary>The faces textured from a side picture, one slot per compass side in
            /// <see cref="SideOrder"/> order; null where that side's picture is absent. Meshes and their
            /// material live here like the rest, and go with the entry.</summary>
            public readonly SideTexture[] Sides = new SideTexture[4];

            /// <summary>Building triangles given the top-down picture (roofs), a side picture, or a tint -
            /// the three shares the build log line reports.</summary>
            public long TopTriangles;
            public long SideTriangles;

            /// <summary>
            /// The faces textured with the game's OWN materials (Stage X): one mesh list and one material per TILE
            /// this floor uses - one per game material - so each material a band draws is one draw call. UVs are the
            /// material's raw UVs, in repeats of its tile, which the tile's Repeat wrap tiles on the GPU (a wall 40
            /// repeats long is 40 bricks, not a stretched one). So a brick wall is brick and a crane is a crane - where the
            /// projected textures (top, sides, tints) can only paint what a camera saw from outside. Faces of a
            /// building with no captured texture keep those projected textures.
            /// </summary>
            public readonly List<SideTexture> Atlas = new List<SideTexture>();

            /// <summary><see cref="Atlas"/> by tile, for the upload units. Main thread only.</summary>
            public readonly Dictionary<int, SideTexture> AtlasByTile = new Dictionary<int, SideTexture>();

            /// <summary>Building triangles textured from an atlas page, for the log line.</summary>
            public long AtlasTriangles;

            /// <summary>Pictures stage C: top faces that had an atlas tile and took the top picture instead (counted in
            /// <see cref="TopTriangles"/> too), for the log line.</summary>
            public long RoofPictureTriangles;

            /// <summary>Play-test 2026-09-29: upward atlas faces kept on the atlas because their tile is cut out
            /// (Prep.RoofKeptCutOut), and their area in m2, for the log line.</summary>
            public long RoofCutOutTriangles;

            public double RoofCutOutArea;

            /// <summary>WP8 (V.2): the four shares by area, m2, and the walls tinted outside 40 degrees.</summary>
            public double AtlasArea;

            public double TopArea;
            public double SideArea;
            public double TintArea;
            public long WallsOutside;

            /// <summary>
            /// Top faces that stand ON ANOTHER FLOOR than the band their building is filed under, with the
            /// level whose picture textures them. See <see cref="Prep.FloorForFace"/>.
            ///
            /// Seen on Interchange: the mall's ground-floor slab (184,000 m2 of floor at y = 21 m) belongs to
            /// a building the builder filed under the BASEMENT band, because the same object reaches down to
            /// the parking level at 16 m. Textured with its building's band, the ground floor of the mall
            /// sampled the basement picture - transparent over the whole mall, so it drew as the black of a
            /// transparent pixel's RGB, speckled where the basement picture happened to be opaque. A face on
            /// a floor now takes that floor's picture: the same one the relief under it uses.
            /// </summary>
            public readonly List<(int Level, Mesh Mesh)> RoofsOnOtherFloors = new List<(int Level, Mesh Mesh)>();

            /// <summary>How many top faces went to another floor's picture, for the log line.</summary>
            public long MovedRoofTriangles;

            /// <summary>How many ground-skirt faces were left out as ground, for the log line. See
            /// Prep.GroundSkirt.</summary>
            public long GroundSkirtTriangles;

            /// <summary>Chunks of this entry's ground and buildings before and after the merge rule
            /// (<see cref="CellSplitMinVertices"/>), for the first-frame line.</summary>
            public int ChunksSplit;

            /// <summary>The same for the walls, set when they are built (a rebuild of the walls replaces them).</summary>
            public int WallChunksSplit;

            public int WallChunksMerged;

            public int ChunksMerged;

            /// <summary>A wall build for this entry is in flight - a worker, or its meshes being uploaded - by
            /// the view that started it. Abandoned with that view, which puts the entry back to waiting.</summary>
            public bool WallsRunning;

            /// <summary>What a side's faces are drawn with while that side's picture is not there - still
            /// decoding, evicted, or failed. Untextured, matte, in <see cref="WallAverage"/>. Made on first
            /// need and destroyed with the entry. Without it those faces were skipped, and every wall facing
            /// a side whose picture never came was a hole for the session - worse than having no sides.</summary>
            public Material SideFallback;

            /// <summary>The mean of this floor's wall tints once they are built, else the fallback grey: what
            /// a side's faces look like while their picture is missing, so they match the tinted walls.</summary>
            public Color WallAverage = new Color(0.55f, 0.53f, 0.50f, 1f);

            /// <summary>Building triangles dropped for having a vertex that is not a finite number -
            /// a NoHit y in the file dequantises to NaN, and one NaN vertex in a merged bucket makes
            /// the whole bucket's bounds NaN, which fails frustum culling and takes the bucket off
            /// screen entirely.</summary>
            public int Dropped;
        }

        /// <summary>The faces of one floor's buildings that one side picture textures, and the material
        /// that carries it. The picture itself is not held here: the material's texture is assigned by
        /// each view from its OWN entry's side picture, every frame it is missing, exactly as the floors'
        /// pictures are - an entry held over a capture rescan must not keep a released picture alive.</summary>
        internal sealed class SideTexture
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public Material Material;

            /// <summary>For an atlas group: the tile (<see cref="TileStore"/> index) its material draws. -1 for a side.</summary>
            public int Tile = -1;

            /// <summary>HQ S3.11 (S3 review): an alpha-page tile this view cannot clip (no cutout shader resolved) - drawn
            /// with the floor's fallback rather than as an opaque tile, as such materials were before alpha tiles.</summary>
            public bool Unclippable;
        }

        /// <summary>The compass sides in slot order. Slot i of <see cref="Built.Sides"/> and of a view's
        /// side pictures is SideOrder[i].</summary>
        private static readonly string[] SideOrder = { "N", "S", "E", "W" };

        /// <summary>
        /// The walls of every building on a floor whose colour fell in one bucket: their meshes and the
        /// one material that colours them.
        ///
        /// Buckets rather than a colour per building, because the Standard shader has no per-vertex
        /// colour: a colour is a material, a material is a draw call, and two hundred buildings would be
        /// two hundred draw calls a frame. Sixteen colours at most per floor is sixteen - and on a map
        /// whose roofs are concrete, red iron and grey felt, sixteen is more than the picture has to
        /// say.
        /// </summary>
        internal sealed class WallTint
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();

            /// <summary>Untextured, matte, coloured. NULL when the shader has no _Color to tint with
            /// (Unlit/Texture, or the flat-colour fallback) - those walls then draw with the floor's own
            /// building material, which is exactly how they drew before walls had colours.</summary>
            public Material Material;

            public Color Colour;
        }

        // --- the orbit's state --------------------------------------------------------------------

        /// <summary>Where the view is looking from, so a rebuild - a floor change, a quest selected, a
        /// setting touched - can put it back. The 3D half of MapView's <c>_savedScale</c>/<c>_savedPan</c>.</summary>
        internal struct ViewState
        {
            public float Yaw;
            public float Pitch;
            public float Distance;
            public Vector2 Focus;
        }

        private float _yaw;
        private float _pitch = DefaultPitch;
        private float _distance;
        private Vector2 _focus;

        /// <summary>The orbit, for MapView to keep across a rebuild.</summary>
        internal ViewState State => new ViewState
        {
            Yaw = _yaw,
            Pitch = _pitch,
            Distance = _distance,
            Focus = _focus
        };

        // --- what it was given --------------------------------------------------------------------

        private RectTransform _viewport;
        private DynamicMapsLibrary.MapEntry _entry;
        private int _selectedLevel;
        private string _mapKey = "";
        private string _meshPath = "";
        private Color _backdrop;

        /// <summary>Told when the mesh turns out to be unusable, so the Maps tab can draw this map in 2D
        /// instead of showing an empty viewport, with a short reason for the toggle's tooltip. Called at
        /// most once.</summary>
        private Action<string, string> _onRefused;

        /// <summary>This capture's side pictures by slot (<see cref="SideOrder"/>), null where absent.</summary>
        private readonly DynamicMapsLibrary.SidePicture[] _sides = new DynamicMapsLibrary.SidePicture[4];

        /// <summary>How many of <see cref="_sides"/> are present.</summary>
        private int _sideCount;

        /// <summary>The present sides as letters in slot order - "NSEW", "NE", "" - for the cache key and
        /// the log line.</summary>
        private string _sidesKey = "";

        /// <summary>Picture-cache slots this view has reserved for its sides, returned in
        /// <see cref="Release"/>.</summary>
        private int _reservedSides;

        /// <summary>
        /// Reads the entry's USABLE side pictures into their slots: present, and not already known to have
        /// failed to decode. Reserves nothing - the cache room is taken by <see cref="TakeSideRoom"/> once
        /// the shader is known, since under the flat-colour fallback the sides take no part at all.
        /// </summary>
        private void TakeSides()
        {
            if (_entry?.Sides == null) return;

            foreach (var side in _entry.Sides)
            {
                if (side?.Picture == null) continue;

                var slot = Array.IndexOf(SideOrder, side.Dir);
                if (slot < 0 || _sides[slot] != null) continue;

                // A picture this session has already failed to decode is not a side this view can use: the
                // key is made from the sides actually usable, so a view built now never waits on it.
                if (side.Picture.ArtworkFailed) continue;

                _sides[slot] = side;
            }

            CountSides();
        }

        /// <summary>Recounts <see cref="_sideCount"/> and <see cref="_sidesKey"/> from the slots.</summary>
        private void CountSides()
        {
            _sideCount = 0;
            _sidesKey = "";

            for (var slot = 0; slot < SideOrder.Length; slot++)
            {
                if (_sides[slot] == null) continue;

                _sideCount++;
                _sidesKey += SideOrder[slot];
            }
        }

        /// <summary>Takes picture-cache room for the sides this view uses, if it uses any and does not hold
        /// the room already. See DynamicMapsLibrary.ReserveSprites for why a 3D view with sides needs it.</summary>
        private void TakeSideRoom()
        {
            if (_reservedSides > 0) return;

            // The side pictures: held by this view for as long as it draws and in the floors' picture cache, so
            // without their room a full peel would evict a floor this view asks for again next frame. NOT the
            // atlas pages any more (stage X): those are decoded by the TileStore, cut into tiles and let go, and
            // never enter the picture cache - the tiles' bytes are counted in the build line instead.
            var room = SidesActive ? _sideCount : 0;
            if (room <= 0) return;

            DynamicMapsLibrary.ReserveSprites(room);
            _reservedSides = room;
        }

        /// <summary>Gives back whatever room this view holds. Idempotent. With <paramref name="evict"/> (the
        /// view closing or switched off) the side and page pictures it held past the flat map's ceiling are
        /// freed on the spot rather than whenever the next floor decode happens to trim the cache; a rebuild
        /// that takes the room straight back passes false, so the pages it still uses are not thrown away and
        /// decoded again.</summary>
        private void ReturnSideRoom(bool evict = true)
        {
            if (_reservedSides <= 0) return;

            if (evict) DynamicMapsLibrary.ReturnSprites(_reservedSides, HeldPictures());
            else DynamicMapsLibrary.ReserveSprites(-_reservedSides);

            _reservedSides = 0;
        }

        /// <summary>The side pictures this view holds, for <see cref="ReturnSideRoom"/>. (Atlas pages are not in the
        /// picture cache: see <see cref="TileStore"/>.)</summary>
        private IEnumerable<DynamicMapsLibrary.MapLayer> HeldPictures()
        {
            foreach (var side in _sides)
                if (side?.Picture != null) yield return side.Picture;
        }

        /// <summary>Whether the side pictures take part at all: they need a textured shader, so under the
        /// flat-colour fallback every wall is a tint, exactly as if the capture had no sides.</summary>
        private bool SidesActive => _sideCount > 0 && !_flatColours;

        /// <summary>The most atlas pages a map has: MapMeshFile's cap, referenced rather than copied.</summary>
        private const int MaxAtlasPages = MapMeshFile.MaxAtlasPages;

        /// <summary>This capture's USABLE atlas pages by page number, null where absent or already failed.</summary>
        private readonly DynamicMapsLibrary.AtlasPage[] _pages = new DynamicMapsLibrary.AtlasPage[MaxAtlasPages];

        private int _pageCount;

        /// <summary>The usable page numbers, for the cache key and the log line.</summary>
        private string _pagesKey = "";

        /// <summary>Whether the atlas takes part: a textured shader and at least one usable page. Under the flat
        /// fallback, or with no pages (a capture from before Stage W), every building face keeps the stage U/V
        /// rule, exactly as before.</summary>
        private bool AtlasActive => _pageCount > 0 && !_flatColours;

        /// <summary>
        /// Pictures stage C: whether this build's roofs (top faces, n.y &gt;= <see cref="RoofNormalY"/>) take the captured,
        /// game-lit top picture even where the atlas has a tile for them. Decided ONCE per build by
        /// <see cref="DecideRoofs"/>, just before the build key, and not read live: the roof pass and the wall pass can be
        /// snapshotted frames apart (StartWalls), and a setting flipped between them would put a triangle in both passes
        /// or in neither. Without the atlas it changes nothing (every roof is on the picture already), so it is only true
        /// with it.
        /// </summary>
        private bool RoofsActive => _roofsActive;

        private bool _roofsActive;

        /// <summary>Why this build's roofs are where they are, for the log line - captured by <see cref="DecideRoofs"/> with
        /// the decision, so the line never reports a setting flipped since.</summary>
        private enum RoofReason
        {
            NoAtlas,
            SwitchedOff,
            DensityUnknown,
            TooCoarse,
            Picture
        }

        private RoofReason _roofReason;

        /// <summary>The density <see cref="DecideRoofs"/> decided on, px/m.</summary>
        private float _roofPpm;

        /// <summary>The lowest capture density over EVERY band of the map with a picture layer, px/m - per map, not per
        /// selected floor, so stepping floors (which changes the drawn levels) never flips the roofs' source and rebuilds
        /// the view. 0 when a floor's is unknown (a capture from before the meta carried it) or there is no floor - which
        /// keeps the roofs on the atlas, today's look. A band with no picture layer is skipped: it is never drawn (Draw
        /// waits for a picture that never comes).</summary>
        private float PicturePpm
        {
            get
            {
                if (_file == null) return 0f;

                var lowest = float.PositiveInfinity;

                foreach (var band in _file.Bands)
                {
                    if (band == null) continue;

                    var layer = LayerOf(band.Level);
                    if (layer == null) continue;

                    var ppm = layer.PxPerMetre;
                    if (!(ppm > 0f) || float.IsInfinity(ppm)) return 0f;

                    if (ppm < lowest) lowest = ppm;
                }

                return float.IsPositiveInfinity(lowest) ? 0f : lowest;
            }
        }

        /// <summary>Sets <see cref="RoofsActive"/> for this build. Called after the shader (flat colours) and the atlas
        /// pages are settled and before the build key, which carries the result.</summary>
        private void DecideRoofs()
        {
            _roofPpm = PicturePpm;

            // the clause is about the atlas's roofs: with no atlas (or flat colours) every roof is on the picture as ever
            _roofReason = _flatColours || !AtlasActive ? RoofReason.NoAtlas
                : !RoofsFromPicture || !ModSettings.RoofsFromPictureWanted ? RoofReason.SwitchedOff
                : !(_roofPpm > 0f) ? RoofReason.DensityUnknown
                : _roofPpm < RoofPictureMinPpm ? RoofReason.TooCoarse
                : RoofReason.Picture;

            _roofsActive = _roofReason == RoofReason.Picture;
        }

        /// <summary>The roofs clause of the build log line, from this build's decision (<see cref="DecideRoofs"/>). The
        /// percentage is of the top faces (every one on the picture while this is on) that came off the atlas. Play-test
        /// 2026-09-29: the upward atlas faces kept on the atlas as cut out, by count and by area against the top faces'
        /// area, and how the tiles' opacity was read (<see cref="TileStore.OpacityNote"/>) - the exclusion that left 98 %
        /// of the building area on the atlas when it went by page.</summary>
        private string RoofsText(long pictureRoofs, long topTriangles, long cutOutRoofs, double cutOutArea, double topArea)
        {
            switch (_roofReason)
            {
                case RoofReason.Picture:
                    var opacity = _heldTiles?.OpacityNote ?? "";
                    return string.Format(CultureInfo.InvariantCulture,
                        ", roofs from the picture ({0:#,##0} faces, {1:0} % of top faces, taken from the atlas) at {2:0.#} px/m, " +
                        "{3:#,##0} top faces kept on the atlas as cut out ({4:0} % of the top area){5}",
                        pictureRoofs, topTriangles > 0 ? 100d * pictureRoofs / topTriangles : 0d, _roofPpm,
                        cutOutRoofs, cutOutArea + topArea > 0d ? 100d * cutOutArea / (cutOutArea + topArea) : 0d,
                        opacity.Length > 0 ? " (" + opacity + ")" : " (tile opacity not read: whole alpha pages kept)");
                case RoofReason.SwitchedOff:
                    return ", roofs atlas (switched off)";
                case RoofReason.DensityUnknown:
                    return ", roofs atlas (picture density unknown)";
                case RoofReason.TooCoarse:
                    return string.Format(CultureInfo.InvariantCulture, ", roofs atlas (picture {0:0.#} px/m < {1:0.#})",
                        _roofPpm, RoofPictureMinPpm);
                default:
                    return "";
            }
        }

        /// <summary>Reads the entry's usable atlas pages into their slots - present and not already failed.</summary>
        private void TakeAtlas()
        {
            if (_entry?.AtlasPages != null)
            {
                foreach (var page in _entry.AtlasPages)
                {
                    if (page?.Picture == null || page.Page < 0 || page.Page >= MaxAtlasPages) continue;
                    if (_pages[page.Page] != null || page.Picture.ArtworkFailed) continue;

                    _pages[page.Page] = page;
                }
            }

            CountPages();
        }

        private void CountPages()
        {
            _pageCount = 0;
            _pagesKey = "";

            for (var page = 0; page < _pages.Length; page++)
            {
                if (_pages[page] == null) continue;

                _pageCount++;
                _pagesKey += (_pagesKey.Length > 0 ? "," : "") + page.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Why the mesh was refused, in a few words for the tooltip.</summary>
        private string _refusal = "";

        /// <summary>Whether <see cref="_refusal"/> is about the scene, not the file. See
        /// <see cref="LastRefusalIsScene"/>.</summary>
        private bool _sceneRefusal;

        // --- what it made -------------------------------------------------------------------------

        private RawImage _image;
        private RenderTexture _rt;
        private Camera _camera;
        private Light _light;
        private GameObject _cameraGo;
        private GameObject _lightGo;


        private int _drawLayer = -1;
        private int _privateMask;

        private readonly List<Floor> _floors = new List<Floor>();

        /// <summary>Where an overlay that is off screen is put: far enough outside the viewport that the
        /// mask culls it, near enough that no float precision is lost.</summary>
        private static readonly Vector2 Parked = new Vector2(-100000f, -100000f);

        /// <summary>The overlays to re-place, with the map (x, z) each one stands at.</summary>
        private readonly List<(RectTransform Rect, Vector2 Map)> _overlays =
            new List<(RectTransform, Vector2)>();

        private MapMeshFile _file;
        private MapMeshFile.ReliefBand _groundBand;
        private float _groundFallbackY;

        private Task<Loaded> _loading;

        /// <summary>The read that <see cref="BeginBuild"/> last built from, kept for a rebuild.</summary>
        private Loaded _loaded;
        private bool _built;
        private bool _broke;
        private int _rtWidth;
        private int _rtHeight;

        public event Action<float, Vector2> OnViewChanged;

        /// <summary>Bumped by every orbit, pan, dolly and fly-to, and by the mesh landing - whatever moves
        /// the camera. See <see cref="IOverlayHost.ViewVersion"/>: it is how a subscriber knows the view
        /// moved when the scale did not, which in 3D is every orbit and every pan.</summary>
        public int ViewVersion { get; private set; }

        /// <summary>Screen pixels per map metre at the focus distance - what the label cull and the
        /// at-rest pin names measure their collisions in. The vertical field of view spans
        /// <c>2 d tan(fov/2)</c> metres at the focus depth, and the viewport is that many canvas units
        /// tall, so this is the same quantity the 2D view's container scale is.</summary>
        public float Scale
        {
            get
            {
                var height = _viewport != null ? _viewport.rect.height : 0f;
                var span = 2f * Mathf.Max(1f, _distance) * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);

                return span > 0f ? Mathf.Max(0.0001f, height / span) : 1f;
            }
        }

        /// <summary>
        /// Where a map point is on screen, in the same canvas units an overlay's anchoredPosition is in -
        /// what the label cull and the at-rest pin names decide their overlaps with.
        ///
        /// NOT FINITE (NaN, NaN) for a point BEHIND the camera, where the projection flips and a point
        /// past the horizon would come back mirrored on the far side of the viewport. Not a fixed
        /// off-screen value: every point behind the camera would then project to the same spot, and the
        /// overlap deciders would have them hide each other. Both deciders treat a non-finite point as
        /// "not on screen" - hidden, and claiming no space.
        /// </summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        public Vector2 Project(Vector2 mapXZ) =>
            TryProject(mapXZ, out var local, out _) ? local : NotOnScreen;

        /// <summary>What <see cref="Project"/> answers for a point the view cannot place.</summary>
        private static readonly Vector2 NotOnScreen = new Vector2(float.NaN, float.NaN);

        /// <summary>The projection both <see cref="Project"/> and <see cref="PlaceOverlays"/> use.</summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        /// <param name="local">Its position in the viewport, in canvas units from the centre.</param>
        /// <param name="inside">Whether it lands on the viewport, with a margin of its own size.</param>
        /// <returns>False when the point is behind the camera.</returns>
        private bool TryProject(Vector2 mapXZ, out Vector2 local, out bool inside)
        {
            local = Parked;
            inside = false;

            if (_camera == null || _viewport == null) return false;

            var world = new Vector3(mapXZ.x, GroundAt(mapXZ.x, mapXZ.y) + 1f, mapXZ.y);
            var view = _camera.WorldToViewportPoint(world);

            if (!(view.z > 0f)) return false;

            local = new Vector2(
                (view.x - 0.5f) * _viewport.rect.width,
                (view.y - 0.5f) * _viewport.rect.height);

            inside = view.x > -OverlayMargin && view.x < 1f + OverlayMargin &&
                     view.y > -OverlayMargin && view.y < 1f + OverlayMargin;

            return true;
        }

        /// <summary>How far outside the viewport an overlay is still placed rather than parked, as a
        /// fraction of the viewport. A marker just off the edge is one the mask would clip anyway.</summary>
        private const float OverlayMargin = 0.15f;

        // --- construction --------------------------------------------------------------------------

        /// <summary>
        /// Puts a 3D view on a viewport, starts reading its mesh file, and returns it. Never throws: on
        /// any failure it tears itself down, tells the caller the mesh is refused and returns null, and
        /// the caller draws the flat picture.
        /// </summary>
        /// <param name="viewport">The Maps tab's viewport, which this component is added to.</param>
        /// <param name="entry">The map being drawn - its layers carry the pictures.</param>
        /// <param name="meshPath">The relief file, already checked for existence and length by
        /// MapCatalog.</param>
        /// <param name="mapKey">The map's key, for the log lines.</param>
        /// <param name="selectedLevel">The floor showing; this and everything below it are drawn.</param>
        /// <param name="backdrop">What the camera clears to, so the 3D view sits on the same colour the
        /// flat one does.</param>
        /// <param name="restore">The orbit to resume, or null to open on the fitted view.</param>
        /// <param name="onRefused">Called with the mesh path and a short reason if the file turns out to
        /// be unusable, so the Maps tab can say why its 3D toggle is grey.</param>
        internal static Map3DView Attach(
            RectTransform viewport, DynamicMapsLibrary.MapEntry entry, string meshPath, string mapKey,
            int selectedLevel, Color backdrop, ViewState? restore, Action<string, string> onRefused)
        {
            LastRefusal = "";
            LastRefusalIsScene = false;

            if (viewport == null || entry == null || string.IsNullOrEmpty(meshPath)) return null;

            var view = viewport.gameObject.AddComponent<Map3DView>();

            view._viewport = viewport;
            view._entry = entry;
            view._meshPath = meshPath;
            view._mapKey = mapKey ?? "";
            view._selectedLevel = selectedLevel;
            view._backdrop = backdrop;
            view._onRefused = onRefused;

            // The side pictures and atlas pages this capture has, by slot. Their cache room is taken once the
            // shader is known (TakeSideRoom).
            view.TakeSides();
            view.TakeAtlas();

            try
            {
                view.Build(restore);
                return view;
            }
            catch (Exception ex)
            {
                // A half-built view is a camera and a light loose in the menu, which is the one thing
                // this must not leave behind.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view for '{view._mapKey}' could not be built " +
                    $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");

                // NOT through Refuse: this failure is synchronous, so the caller is still inside its own
                // build and draws the flat picture in THIS pass. Telling it again through the callback
                // would only ask for a second repaint of a view that is already correct - so the reason
                // is handed over on the side instead, for the toggle's tooltip.
                LastRefusal = string.IsNullOrEmpty(view._refusal)
                    ? "could not be opened in this scene"
                    : view._refusal;

                LastRefusalIsScene = view._sceneRefusal;

                view._onRefused = null;
                view.Release();
                Destroy(view);

                return null;
            }
        }

        /// <summary>Why the last <see cref="Attach"/> that returned null gave up, for the caller to put in
        /// front of the player. A static because the alternative is an out parameter on a method whose
        /// other seven arguments are the interesting ones, and it is read on the line after the call that
        /// set it, on the one thread that builds UI.</summary>
        internal static string LastRefusal { get; private set; } = "";

        /// <summary>Whether <see cref="LastRefusal"/> is about the SCENE rather than the file - there is
        /// no spare layer to draw on right now. The caller must not hold that against the mesh file: the
        /// next scene may well have a free layer, and a file refusal lasts until a capture or a profile
        /// change.</summary>
        internal static bool LastRefusalIsScene { get; private set; }

        private void Build(ViewState? restore)
        {
            _drawLayer = PrivateLayer();

            if (_drawLayer < 0)
            {
                // Every layer has a renderer on it or is in a live camera's mask. Drawing on a shared
                // layer would put our geometry in front of the player's menu, so we do not draw at all.
                _refusal = "no spare layer in this scene";
                _sceneRefusal = true;

                throw new InvalidOperationException(
                    "no layer of the loaded scene is free of renderers, so there is nowhere private to draw");
            }

            _privateMask = 1 << _drawLayer;

            // The picture of the render, under the viewport's own backing plate but UNDER MapSpace too:
            // first sibling, because uGUI draws siblings in order and the markers live in MapSpace. Made
            // the wrong way round, every pin would be hidden behind the render of the map they are on.
            var imageGo = new GameObject("Map3DImage", typeof(RectTransform), typeof(RawImage));
            var rect = (RectTransform)imageGo.transform;
            rect.SetParent(_viewport, worldPositionStays: false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsFirstSibling();

            _image = imageGo.GetComponent<RawImage>();
            _image.color = Color.white;

            // OFF: the viewport's own backing Image is the raycast target the drag and scroll handlers
            // need, and a RawImage over it with this on would consume every gesture before the handlers
            // on the viewport saw it.
            _image.raycastTarget = false;

            EnsureRenderTexture();

            _cameraGo = new GameObject("QuestTreeMap3DCamera", typeof(Camera));
            _camera = _cameraGo.GetComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = _backdrop;
            _camera.orthographic = false;
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = FarClip;
            _camera.targetTexture = _rt;
            _camera.cullingMask = _privateMask;
            _camera.useOcclusionCulling = false;
            _camera.allowMSAA = RenderMsaa > 1;
            _camera.allowHDR = false;   // until Attach below says whether the tonemap path runs (stage D)

            // Forward, for the oblique cut: see PrivateCameraPath.
            _camera.renderingPath = PrivateCameraPath;

            // Below every camera the game has, so even a frame in which this one were somehow enabled
            // would draw under the menu rather than over it.
            _camera.depth = -50f;

            // Lighting stage 4: the game's PostProcessing stack on this camera - a tonemap and ambient occlusion - only
            // when the setting asks and the game has the stack's resources loaded (Map3DPostProcess); off, nothing
            // changes and nothing is logged (the quiet console).
            if (Map3DPostProcess.Wanted)
                Plugin.LogSource?.LogInfo($"QuestTree: 3D map post-processing for {_mapKey}: {Map3DPostProcess.Probe()}");
            _postProcessAttached = Map3DPostProcess.Attach(_camera, _drawLayer, out _, _entry?.Lighting?.PrismTonemap);

            // Spot-sun stage D: the tonemap path is what really attached, not what the setting asked for - a game without
            // the stack's resources, or an attach that threw, keeps the plain path whatever the setting says. On it the
            // camera renders HDR into a half-float target, because an ARGB32 one clips every value above 1 before the
            // tonemap sees it and the 1.8 anchor would come out flat white. The self-test "tonemap" forces the LDR target
            // so the calibration below must log CLIPPED - the proof its check can fail.
            _tonemapOn = _postProcessAttached && Map3DPostProcess.Attached;
            _hdrTarget = _tonemapOn && SelfTest != "tonemap";
            _camera.allowHDR = _hdrTarget;
            EnsureRenderTexture();   // remade as ARGBHalf on the HDR path (the first was made before this was known)

            // the plan's ambient floor follows _tonemapOn; nothing has read the lazy plan yet, cleared all the same so the
            // order can never matter
            _plan = null;

            // Spot-sun stage A: whether spot shadows and emission work in this game, measured once per session on the
            // probe's own camera before this view's first render (and before its light is made, which stage C sets from
            // the result). Only the Standard shader has either variant; the shader is the one BeginBuild will resolve.
            var probeShader = ResolveShader(out _);
            var standard = probeShader != null && probeShader.name.StartsWith("Standard", StringComparison.Ordinal);
            if (standard) Map3DLightProbe.Run(_drawLayer, probeShader);

            // Stage C: the probe's verdict is taken only for the Standard shader it measured - the result is cached per
            // session, and a later view on the legacy fallback must not inherit a pass that shader cannot honour.
            _probe = standard ? Map3DLightProbe.Last : null;
            _spotShadowsWhy = !standard ? "none (legacy shader)"
                : _probe == null ? "none (probe not run)"
                : !_probe.SpotShadows ? "none (probe failed: " + (_probe.Why ?? "no reason recorded") + ")"
                : null;

            // The white clamp cookie the probe proved (the default one vignettes the cone). The built-in spot cookie is
            // read from the alpha channel, so the alpha is white too.
            _lightCookie = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "QuestTreeMap3DLight-cookie",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var white = new Color32[16];
            for (var i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
            _lightCookie.SetPixels32(white);
            _lightCookie.Apply(false);

            // The one light: a SPOT, set up as the probe measured it (see ShadowBias); RenderNow places and aims it each
            // render. ForcePixel, so it is never demoted to a vertex light whatever the player's pixel light count.
            _lightGo = new GameObject("QuestTreeMap3DLight", typeof(Light));
            _lightGo.layer = _drawLayer;
            _light = _lightGo.GetComponent<Light>();
            _light.type = LightType.Spot;
            _light.renderMode = LightRenderMode.ForcePixel;
            _light.cookie = _lightCookie;
            _light.intensity = SunIntensity;
            _light.color = SunColour;
            _light.shadows = _spotShadowsWhy == null ? ShadowMode : LightShadows.None;
            _light.shadowStrength = ShadowStrength;
            _light.shadowCustomResolution = _probe != null ? _probe.ResolutionEffective : Map3DLightProbe.SpotShadowMapMax;
            _light.shadowBias = _spotShadowsWhy == null ? _probe.Bias : ShadowBias;
            _light.shadowNormalBias = _spotShadowsWhy == null ? _probe.NormalBias : ShadowNormalBias;
            _light.cullingMask = _privateMask;
            _light.enabled = false;
            _lightGo.transform.rotation = Quaternion.Euler(SunPitch, SunYaw, 0f);

            // The file read and the deflate are a worker's job; every Mesh it turns into is Unity's
            // thread only, and that happens in LateUpdate when this lands. A capture's relief is a
            // third of a megabyte and reads in a few milliseconds, but the panel must not wait on a
            // disk however fast it is - the same rule DynamicMapsLibrary's picture read follows.
            var path = _meshPath;
            var bound = _readBound = ViewerReadBound(out _readBoundVramMb);
            _loading = Task.Run(() => ReadFile(path, bound));

            _restored = restore.HasValue;

            if (restore.HasValue)
            {
                _yaw = restore.Value.Yaw;
                _pitch = Mathf.Clamp(restore.Value.Pitch, MinPitch, MaxPitch);
                _distance = restore.Value.Distance;
                _focus = restore.Value.Focus;
            }
            else
            {
                var layer = ResolveLayer();
                _focus = layer != null ? layer.BoundsCentre : Vector2.zero;
                _yaw = 0f;
                _pitch = DefaultPitch;
                _distance = FitDistance();
            }

            Place();

            // Spot-sun stage D: the tonemap's curve read back before anything real is drawn - it sets the pictures'
            // emission scale (their materials are made later, in BeginBuild) and proves the path HDR, or falls back.
            if (_tonemapOn) CalibrateTonemap(standard ? probeShader : null);

            // One render with nothing queued, so the viewport shows the backdrop colour from the first
            // frame. Without it the RawImage displays an uninitialised render texture - black, or worse -
            // for the two or three frames the file read takes, which reads as the map having failed.
            try
            {
                RenderNow();
                Present();
            }
            catch (Exception ex)
            {
                // Not fatal: the first frames look wrong and the view still works.
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map's first clear failed ({ex.Message}).");
            }
        }

        /// <summary>A parsed file and the write time it was read at - which is half of the key the built
        /// meshes are cached under, and has to come back from the worker rather than be read again on the
        /// main thread, or a file rewritten between the two would be cached under the wrong stamp.</summary>
        private sealed class Loaded
        {
            public MapMeshFile File;
            public long Stamp;
        }

        /// <summary>The file, read and parsed off the main thread. A cache of one, keyed by path and
        /// write time: switching floor rebuilds the whole viewport, and re-inflating the same file for
        /// every step of the peel is work nobody asked for. The parsed object holds no Unity object, so
        /// it is safe to keep across a rebuild and across a scene change - and it is dropped with the
        /// meshes by <see cref="DropCaches"/>.</summary>
        /// <param name="path">The mesh file.</param>
        /// <param name="maxTriangles">The most triangles this machine will draw (<see cref="ViewerReadBound"/>).
        /// A file past it is refused by the reader before its arrays are allocated.</param>
        private static Loaded ReadFile(string path, long maxTriangles)
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;

            lock (CacheLock)
            {
                if (_cachedFile != null && _cachedPath == path && _cachedStamp == stamp)
                    return new Loaded { File = _cachedFile, Stamp = stamp };
            }

            int generation;
            lock (CacheLock) generation = _cacheGeneration;

            // Through a FileStream, not ReadAllBytes: the same bytes through the same reader, minus a
            // compressed copy of the whole file on the large-object heap.
            MapMeshFile parsed;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                parsed = MapMeshFile.Read(stream, maxTriangles);

            // review 2026-09-29 (Q4): a stored set's relief pillars (a stack's cap, a hall's roof panels draped with the
            // picture) despiked as the builder does it, so host sets and shipped seeds heal without a recapture. Here, on
            // the read's worker, once per read and before the file is cached or any floor is prepared from it - so no
            // PrepareFloor ever sees a half-despiked band and a rebuild from the cache pays nothing. A throw keeps the
            // relief as stored.
            DespikeRead(parsed, path);

            lock (CacheLock)
            {
                // Only if nothing dropped the cache while this read was in flight. A read started before
                // a drop and finished after it would otherwise put the dropped file straight back - after
                // the menu teardown, into the raid.
                if (generation == _cacheGeneration)
                {
                    _cachedFile = parsed;
                    _cachedPath = path;
                    _cachedStamp = stamp;
                }
            }

            return new Loaded { File = parsed, Stamp = stamp };
        }

        /// <summary>WORKER. A just-read file's top band despiked (MapMeshBuilder.DespikeStored), timed and logged; a throw
        /// is logged and the relief kept as stored.</summary>
        /// <param name="file">The file just read.</param>
        /// <param name="path">Its path, for the line.</param>
        private static void DespikeRead(MapMeshFile file, string path)
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var moved = MapMeshBuilder.DespikeStored(file);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: 3D map relief of {Path.GetFileName(path)} - {moved.ToString(CultureInfo.InvariantCulture)} stored cell(s) " +
                    $"despiked on load, in {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the 3D map's relief despike failed ({ex.GetType().Name}: {ex.Message}) - drawn as stored.");
            }
        }

        /// <summary>A graphics card's share the 3D view may fill with building triangles: a quarter of its
        /// memory at 64 bytes a triangle - twice the builder's share (MapMeshBuilder.MemoryCeiling), so a file
        /// built on a similar machine always fits.</summary>
        private const long ViewerVramShare = 4;

        /// <summary>GPU bytes per drawn triangle, for <see cref="ViewerReadBound"/>: 12 of indices plus ~1.2
        /// vertices at 32 bytes, with margin.</summary>
        private const long ViewerBytesPerTriangle = 64;

        /// <summary>
        /// MAIN THREAD (SystemInfo). The most building triangles this view reads:
        /// min(MapMeshFile.MaxTriangles, VRAM / 4 / 64 B) - 33.5 M on an 8 GB card, 8.4 M on a 2 GB one - or the
        /// format's own bound when the card does not say how much memory it has. Passed to MapMeshFile.Read,
        /// which refuses a file past it before allocating it, and the flat map is drawn instead.
        /// </summary>
        private static long ViewerReadBound(out int vramMb)
        {
            try { vramMb = SystemInfo.graphicsMemorySize; } catch (Exception) { vramMb = 0; }

            if (vramMb <= 0) return MapMeshFile.MaxTriangles;

            var byVram = ((long)vramMb << 20) / ViewerVramShare / ViewerBytesPerTriangle;

            return Math.Min(MapMeshFile.MaxTriangles, byVram);
        }

        /// <summary>This view's read bound and the VRAM it came from, for the refusal line.</summary>
        private long _readBound;
        private int _readBoundVramMb;

        private static readonly object CacheLock = new object();
        private static MapMeshFile _cachedFile;
        private static string _cachedPath;
        private static long _cachedStamp;

        /// <summary>Bumped by every <see cref="DropCaches"/>, under <see cref="CacheLock"/>. See
        /// <see cref="ReadFile"/>.</summary>
        private static int _cacheGeneration;

        /// <summary>The (file, write time) the built meshes in <see cref="_cachedFloors"/> belong to, or null.
        /// A file rewritten under the same name gets a new stamp and so a new key, which is what throws
        /// the geometry of the capture before last away.</summary>
        private static string _builtKey;

        /// <summary>The built geometry, per band level, for the file <see cref="_builtKey"/> names. Main
        /// thread only - every value holds Unity Meshes - and owned by this class rather than by any view:
        /// see <see cref="Built"/>.</summary>
        private static readonly Dictionary<int, Built> _cachedFloors = new Dictionary<int, Built>();

        /// <summary>The private layer this session settled on, or -1 before the first scan, after any
        /// failure and after any scene load. Once per SCENE rather than once per view: the scan walks
        /// every Renderer in the loaded scene, and a repaint happens on every click - but a scene that
        /// loads (the hideout, additively, into the menu) can put renderers on the very layer chosen, and
        /// ours would then draw them into the map without a line in the log. See
        /// <see cref="OnSceneLoaded"/>.</summary>
        private static int _sessionLayer = -1;

        /// <summary>Whether <see cref="OnSceneLoaded"/> is subscribed. Static and subscribed at most once:
        /// SceneManager.sceneLoaded is a static event, and a second subscription would be a second call
        /// per load, and a leaked one a call into a class that has dropped everything.</summary>
        private static bool _sceneHooked;

        /// <summary>A scene has loaded: the layer scan describes a scene that has changed, so the next
        /// view scans again. The live view keeps drawing on the layer it has until it is rebuilt - the
        /// panel rebuilds on the way back from any scene change that matters (the hideout, a raid).</summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _sessionLayer = -1;
        }

        private static void HookScenes()
        {
            if (_sceneHooked) return;

            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _sceneHooked = true;
            }
            catch (Exception ex)
            {
                // Not fatal: the layer is then re-scanned only after failures and drops, as before.
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map could not watch scene loads ({ex.Message}).");
            }
        }

        private static void UnhookScenes()
        {
            if (!_sceneHooked) return;

            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _sceneHooked = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map could not stop watching scene loads ({ex.Message}).");
            }
        }

        /// <summary>
        /// Drops everything cached across views: the parsed file, the built meshes and the chosen layer.
        ///
        /// Called by MapView when the map memory goes - a profile change, a capture landing, and the
        /// menu teardown before a raid. The last is the one that must not be missed: a Mesh is not a
        /// scene object, so nothing else would ever free it, and up to some tens of megabytes of map
        /// geometry would sit in the raid's memory for no reason.
        /// </summary>
        internal static void DropCaches()
        {
            DropBuiltMeshes();
            DropTiles();
            CancelAllPreps();

            lock (CacheLock)
            {
                _cachedFile = null;
                _cachedPath = null;
                _cachedStamp = 0L;
                _cacheGeneration++;
            }

            // The layer goes only on THIS path, not on a map switch. All three callers of DropCaches are
            // points where the loaded scene may have changed under us - a raid has been and gone by the
            // time the menu builds another map - and that is the one thing that can invalidate the scan.
            _sessionLayer = -1;

            // And the scene hook with it: nothing is cached any more for a scene load to invalidate, and
            // the next view's scan subscribes again.
            UnhookScenes();
        }

        /// <summary>
        /// Takes every built entry out of the cache and forgets which file they were built from. For a
        /// map switch, where the parsed file replaces itself and the layer is still good, and as the
        /// mesh half of <see cref="DropCaches"/>.
        ///
        /// An entry no view is drawing is destroyed now. One a LIVE view is still drawing - the view
        /// MapView.DiscardViewport has just destroyed gets one more LateUpdate, Destroy being deferred -
        /// is only orphaned, and that view destroys it when it lets go. See <see cref="Built"/>.
        /// </summary>
        private static void DropBuiltMeshes()
        {
            var destroyed = 0;
            var deferred = 0;

            foreach (var built in _cachedFloors.Values)
            {
                if (built == null) continue;

                if (built.Users > 0)
                {
                    built.Orphaned = true;
                    deferred++;
                    continue;
                }

                destroyed += DestroyBuilt(built);
            }

            _cachedFloors.Clear();
            _builtKey = null;

            if (destroyed > 0 || deferred > 0)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: dropped the cached 3D map - {destroyed} mesh(es) destroyed, {deferred} floor(s) " +
                    $"left to the view still drawing them.");
            }
        }

        /// <summary>Destroys one entry's meshes and empties it, so a second call is a no-op.</summary>
        /// <param name="built">The entry.</param>
        /// <returns>How many meshes were destroyed.</returns>
        private static int DestroyBuilt(Built built)
        {
            var count = 0;

            for (var i = 0; i < built.Ground.Count; i++) { Discard(built.Ground[i]); count++; }
            for (var i = 0; i < built.Buildings.Count; i++) { Discard(built.Buildings[i]); count++; }

            count += DestroyWalls(built);

            for (var i = 0; i < built.RoofsOnOtherFloors.Count; i++) { Discard(built.RoofsOnOtherFloors[i].Mesh); count++; }
            built.RoofsOnOtherFloors.Clear();

            Discard(built.SideFallback);
            built.SideFallback = null;

            for (var slot = 0; slot < built.Sides.Length; slot++)
            {
                var side = built.Sides[slot];
                if (side == null) continue;

                for (var i = 0; i < side.Meshes.Count; i++) { Discard(side.Meshes[i]); count++; }

                side.Meshes.Clear();
                Discard(side.Material);
                side.Material = null;
                built.Sides[slot] = null;
            }

            // The tile groups: meshes and materials are the entry's; the tile TEXTURES are the TileStore's and are
            // not touched here (destroying a material never destroys its texture).
            foreach (var atlas in built.Atlas)
            {
                if (atlas == null) continue;

                for (var i = 0; i < atlas.Meshes.Count; i++) { Discard(atlas.Meshes[i]); count++; }

                atlas.Meshes.Clear();
                Discard(atlas.Material);
                atlas.Material = null;
            }

            built.Atlas.Clear();
            built.AtlasByTile.Clear();

            built.Ground.Clear();
            built.Buildings.Clear();
            built.Complete = false;

            return count;
        }

        /// <summary>Destroys an entry's walls - their meshes and their materials - and empties the list.
        /// Also the clean-up for a wall build that threw halfway, which is why it is its own method.</summary>
        /// <param name="built">The entry.</param>
        /// <returns>How many meshes were destroyed.</returns>
        private static int DestroyWalls(Built built)
        {
            var count = 0;

            foreach (var tint in built.Walls)
            {
                if (tint == null) continue;

                for (var i = 0; i < tint.Meshes.Count; i++)
                {
                    Discard(tint.Meshes[i]);
                    count++;
                }

                tint.Meshes.Clear();
                Discard(tint.Material);
                tint.Material = null;
            }

            built.Walls.Clear();
            built.Tints = 0;

            return count;
        }

        /// <summary>A view has stopped drawing an entry: the last one out destroys it if a drop has
        /// already taken it out of the cache.</summary>
        /// <param name="built">The entry.</param>
        private static void Unuse(Built built)
        {
            if (built == null) return;

            if (built.Users > 0) built.Users--;

            if (built.Users == 0 && built.Orphaned) DestroyBuilt(built);
        }

        /// <summary>
        /// A layer 8..31 that nothing in the loaded scene draws on and no live camera would show.
        ///
        /// INACTIVE renderers count. EFT hides distant geometry by switching renderers and whole
        /// GameObjects off - the reason MapCapture.ForceCulling exists - so "no ACTIVE renderer on this
        /// layer" is a statement about where the player is standing, and a layer with ten thousand
        /// disabled renderers on it would show a piece of the hideout the moment one came back. The
        /// camera-mask test is the second condition, not the first.
        ///
        /// Layers 0-7 are Unity's own and are never taken. Phase 3-0 found 19 candidates in the menu, so
        /// this failing is not the expected case - but it is possible, and then nothing is drawn at all.
        /// </summary>
        private static int PrivateLayer()
        {
            if (_sessionLayer >= 0) return _sessionLayer;

            // Watching for scene loads from the first scan on, so the result is never older than the
            // scene it describes.
            HookScenes();

            _sessionLayer = ChoosePrivateLayer();

            return _sessionLayer;
        }

        /// <summary>The scan itself. See <see cref="PrivateLayer"/>, which is what callers use.</summary>
        private static int ChoosePrivateLayer()
        {
            var used = 0;

            var renderers = FindObjectsOfType<Renderer>(true);

            for (var i = 0; i < (renderers?.Length ?? 0); i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                var layer = renderer.gameObject.layer;
                if (layer >= 0 && layer < 32) used |= 1 << layer;
            }

            var shown = 0;
            var cameras = Camera.allCameras;

            for (var i = 0; i < (cameras?.Length ?? 0); i++)
            {
                if (cameras[i] != null) shown |= cameras[i].cullingMask;
            }

            for (var layer = 8; layer < 32; layer++)
            {
                var bit = 1 << layer;
                if ((used & bit) == 0 && (shown & bit) == 0) return layer;
            }

            return -1;
        }

        // --- the render texture --------------------------------------------------------------------

        /// <summary>Makes the render texture the viewport's size in REAL pixels, or remakes it when the
        /// panel has been resized. The rect is in canvas units and the canvas may be scaled, so a
        /// texture sized from the rect alone is soft on a scaled-up UI and wasteful on a scaled-down
        /// one.</summary>
        private bool EnsureRenderTexture()
        {
            var scale = CanvasScale();

            var width = Mathf.Clamp(Mathf.RoundToInt(_viewport.rect.width * scale), 64, 4096);
            var height = Mathf.Clamp(Mathf.RoundToInt(_viewport.rect.height * scale), 64, 4096);

            // stage D: the format follows _hdrTarget too, so the texture made before Attach is remade once it is decided
            var format = RenderFormat();
            if (_rt != null && width == _rtWidth && height == _rtHeight && _rt.format == format) return false;

            var previous = _rt;

            // HQ S1.2: multisampled when the texture is small enough for it; Unity resolves the samples when the
            // RawImage reads it.
            var samples = RenderMsaa > 1 && (long)width * height <= MsaaPixelCap ? RenderMsaa : 1;

            _rt = new RenderTexture(width, height, 24, format)
            {
                name = "QuestTreeMap3D",
                antiAliasing = samples
            };
            _rt.Create();

            _rtWidth = width;
            _rtHeight = height;
            _rtSamples = samples;

            if (_camera != null) _camera.targetTexture = _rt;
            if (_image != null) _image.texture = _rt;

            if (previous == null) return true;

            // The old one goes only after the new one is in place on both the camera and the image: a
            // released texture still assigned to either is a frame rendered into nothing, or a UI quad
            // sampling freed memory.
            previous.Release();
            Destroy(previous);
            return true;
        }

        /// <summary>Spot-sun stage D: the view's target format - ARGBHalf on the HDR path (<see cref="_hdrTarget"/>), so the
        /// tonemap sees the values above 1 the 1.8 anchor puts there, ARGB32 otherwise. A device without half-float render
        /// targets gets ARGB32, and the calibration then reads CLIPPED and falls back.</summary>
        private RenderTextureFormat RenderFormat()
        {
            if (!_hdrTarget) return RenderTextureFormat.ARGB32;

            // asked once per view: EnsureRenderTexture runs every frame
            if (!_halfSupported.HasValue) _halfSupported = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);
            return _halfSupported.Value ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        }

        /// <summary>Whether the device renders into ARGBHalf, asked on the first HDR <see cref="RenderFormat"/> of the view.</summary>
        private bool? _halfSupported;

        /// <summary>Play-test 2026-09-29: what the RawImage shows on the HDR path - <see cref="_rt"/> copied into an ARGB32
        /// texture after each real render (<see cref="Present"/>); null on the plain path, where the image shows the target.</summary>
        private RenderTexture _display;

        /// <summary>
        /// Play-test 2026-09-29 (tonemap on: the view blown out and oversaturated, while the calibration read a sane curve):
        /// the ALPHA of the HDR target. Every opaque Standard pass writes alpha 1 (UNITY_OPAQUE_ALPHA) and the spot's
        /// ForwardAdd pass is blended "[_SrcBlend] One" on alpha as on colour, so each lit pixel's alpha adds up to 2 or
        /// more. An ARGB32 target (tonemap off) clamps that back to 1 as it is written; the ARGBHalf target keeps it, PPv2
        /// carries alpha through untouched, and the UI's premultiplied RawImage draw (rgb x alpha, Blend One
        /// OneMinusSrcAlpha) doubles every lit pixel and clips it per channel - white ground, oversaturated trees. Every
        /// readback (the calibration, the emission check) compares rgb only, and the calibration's quads are unlit (alpha
        /// 1), so none of them could see it. On the HDR path the frame is now copied into an ARGB32 texture - the same
        /// blit the readbacks use, which clamps alpha to 1 exactly as the plain path's target does - and the image shows
        /// that copy. Off the HDR path (the plain path, or a fallback) the image shows <see cref="_rt"/> as before and the
        /// copy is let go.
        /// </summary>
        private void Present()
        {
            if (_image == null || _rt == null) return;

            if (!_hdrTarget)
            {
                if (_image.texture != _rt) _image.texture = _rt;

                if (_display != null)
                {
                    var unused = _display;
                    _display = null;
                    unused.Release();
                    Destroy(unused);
                }

                return;
            }

            RenderTexture old = null;
            if (_display == null || _display.width != _rt.width || _display.height != _rt.height)
            {
                old = _display;
                _display = new RenderTexture(_rt.width, _rt.height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "QuestTreeMap3D-display"
                };
                _display.Create();
            }

            Graphics.Blit(_rt, _display);
            if (_image.texture != _display) _image.texture = _display;

            // the old copy goes only once the image is off it (as EnsureRenderTexture's target)
            if (old != null)
            {
                old.Release();
                Destroy(old);
            }
        }

        // --- building the meshes -------------------------------------------------------------------

        /// <summary>
        /// Starts turning the parsed file into meshes, or refuses it. Runs on the main thread in the first
        /// LateUpdate after the read lands (and again for a rebuild that drops a failed side).
        ///
        /// ONLY THE CHEAP HALF runs here: the checks, the shader, the cache key, one Floor (and one registered
        /// cache entry) per drawn band. The geometry of every band not already cached is prepared on a worker
        /// (<see cref="PrepareFloor"/> - plain arrays, no Unity object) and uploaded afterwards one mesh per
        /// unit under a per-frame budget (<see cref="Pump"/>), so a map of three million building triangles
        /// never freezes the panel for longer than a frame's budget. Until the last unit is done the view
        /// draws nothing: the RawImage keeps the backdrop it was cleared to, and the overlays stay parked.
        /// </summary>
        private void BeginBuild()
        {
            _built = true;
            ResetPipeline();

            // stage C: the spot is fitted to this build's meshes, never a previous build's
            _mapBounds = default(Bounds);
            _mapBoundsSet = false;

            _buildClock.Reset();
            _buildClock.Start();

            // the chunks line reports this build's uploads: MakeMesh's counters from here on
            _uploadTicksAtStart = _uploadTicks;
            _uploadMeshesAtStart = _uploadMeshes;

            // From the worker the first time; from the kept result on a rebuild (a side dropped).
            var loaded = _loading != null ? _loading.Result : _loaded;
            _loading = null;
            _loaded = loaded;

            _file = loaded?.File;

            if (_file == null || _file.Bands == null || _file.Bands.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has no ground in it - drawing the flat " +
                    $"picture instead.");
                Refuse("the file has no ground in it");
                return;
            }

            if (!ExtentAgrees(out var complaint))
            {
                // The check the plan puts here and nowhere else: a mesh quantised over one rectangle and
                // drawn under a picture stretched over another is a map whose landmarks are all in
                // plausible-looking wrong places, and nothing about it reads as broken.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' {complaint} - it is not of the same " +
                    $"rectangle as the picture, so this map draws flat.");
                Refuse("its extent is not the picture's");
                return;
            }

            _levels = DrawnLevels();

            var shader = ResolveShader(out var shaderName);

            if (shader == null)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: none of the {Shaders.Length} shaders the 3D map can draw with is loaded " +
                    $"in this scene - '{_mapKey}' draws flat.");
                Refuse("no shader this scene has loaded can draw it");
                return;
            }

            // Only Hidden/Internal-Colored has no texture of its own, and a view on it draws vertex
            // colours or nothing at all.
            var flat = shaderName == "Hidden/Internal-Colored";

            // Kept for the walls, which can be built on a later frame than the rest - see StartWalls.
            _buildingShader = shader;
            _flatColours = flat;
            _shaderName = shaderName;

            // Which heights are which floor's surfaces - the roof routing reads it while the floors build.
            MeasureFloorRanges();

            // Now, and not at Attach: whether the sides take part depends on the shader just resolved.
            TakeSideRoom();

            // The built meshes belong to a (file, write time), and anything cached under another one is
            // the geometry of a map that has been replaced. The sides are in the key because they decide
            // which faces are textured and which are tinted: the same mesh classified against four sides
            // and against none is two different builds. HERE and not earlier, because whether the sides
            // take part at all depends on the shader just resolved (SidesActive reads _flatColours).
            // Pictures stage C: the roofs' source splits the triangles between the passes differently, so it is in the key
            // too - a setting flipped, or a denser capture, rebuilds the cached meshes rather than drawing the old split.
            DecideRoofs();

            var key = _meshPath + "|" + loaded.Stamp.ToString(CultureInfo.InvariantCulture) + "|" +
                      (SidesActive ? _sidesKey : "-") + "|atlas:" + (AtlasActive ? _pagesKey : "-") + "|" +
                      FloorRangesKey() + "|roofs:" + (RoofsActive ? "P" : "-");

            if (_builtKey != key)
            {
                DropBuiltMeshes();
                _builtKey = key;
            }

            _viewBuildKey = key;

            _groundShader = ResolveGroundShader(shader, shaderName, out _groundCutout, out _cutoutNote);

            // Spot-sun stage B: emission or the division path, decided once per build - after the probe (run in the view's
            // setup, before any build) and before the first material below is made; the check is owed again for this build
            DecideEmission();
            _emissionChecked = false;
            _sideChecked = false;
            _frameChecked = false;
            _frameCheckTries = 0;

            // One Floor per drawn band, each with a cache entry - reused when complete, registered empty
            // when not, and then filled by the worker's data.
            var toPrepare = new List<(int Level, Built Into)>();

            for (var i = 0; i < _levels.Count; i++) RegisterFloor(_levels[i], shader, toPrepare);

            _groundBand = _file.Band(_levels[_levels.Count - 1]);
            _groundFallbackY = FallbackGroundY();
            var cut = CutHeight();
            if (!SameCut(cut, _cutY)) _timeRender = true;
            _cutY = cut;

            // Fitted ONCE per view: RebuildWithoutFailedSides comes back through here, and refitting then snapped
            // the player's zoom back and Finish saved the snapped distance (review F26).
            if (!_restored)
            {
                _distance = FitDistance();
                _restored = true;
            }

            Place();

            // The side pictures first, then the floors lowest to highest: the floor SHOWING ends up the most
            // recently used of everything this view holds. Asked now, so they decode while the geometry is
            // being prepared rather than after.
            if (SidesActive)
            {
                for (var slot = 0; slot < _sides.Length; slot++) _sides[slot]?.Picture?.TryGetSprite(out _);
            }

            // The atlas: the tiles of this file, cut from its pages a few a frame by the shared TileStore (see
            // PumpTiles). Taken before the prep snapshot, which reads the tile index.
            if (AtlasActive) AcquireTiles();

            foreach (var floor in _floors)
                if (!_flatColours) floor.Layer?.TryGetSprite(out _);

            _preparing = toPrepare;

            if (toPrepare.Count == 0)
            {
                // Everything came out of the cache: nothing to prepare, straight to the walls and the cut.
                AfterPrepare(new List<FloorData>());
                return;
            }

            // One worker job per (build, level), SHARED: a repaint during a build makes a new view with the same
            // key, and it attaches to the job already running instead of starting another - so clicking quests
            // while a big map builds does not stack up workers (each one is the whole floor's geometry in
            // memory). A level with no job, or only a cancelled one, gets a new job with its own snapshot.
            foreach (var item in toPrepare)
                _held.Add((item.Level, AcquirePrep(PrepKey(item.Level), item.Level, SnapshotPrep())));
        }

        /// <summary>
        /// One floor of the peel: its two materials, and its cache entry - taken from the cache when a
        /// previous view of this same file and floor completed it, else registered EMPTY here and listed in
        /// <paramref name="toPrepare"/> for the worker. Registered before it is built, as ever, so a view that
        /// goes away mid-build leaves every mesh it made where a drop will find it.
        /// </summary>
        private void RegisterFloor(int level, Shader shader, List<(int Level, Built Into)> toPrepare)
        {
            var band = _file.Band(level);
            if (band == null) return;

            if (!_cachedFloors.TryGetValue(level, out var meshes) || meshes == null || !meshes.Complete)
            {
                if (meshes != null)
                {
                    // A half-built entry: destroyed now when nobody holds it; ORPHANED when a view still
                    // does (one whose build was abandoned in the frame this view was made), so that view's
                    // release destroys it. Replacing it in the cache without either would lose it for good -
                    // a view-held entry that is no longer in the cache is found by nothing.
                    if (meshes.Users == 0) DestroyBuilt(meshes);
                    else meshes.Orphaned = true;
                }

                meshes = new Built { Cells = band.CellCount };
                _cachedFloors[level] = meshes;

                toPrepare.Add((level, meshes));
                _reusedFloors = false;
            }

            // Counted as in use from here until this view releases it - see Built.
            meshes.Users++;

            var building = Matte(new Material(shader) { name = $"QuestTreeMap3D-buildings-{level}" });

            _floors.Add(new Floor
            {
                Level = level,
                Layer = LayerOf(level),
                GroundMaterial = MakeGroundMaterial(level),
                BuildingMaterial = building,
                // Pictures stage C: the roofs on the emission recipe - since the 2026-09-29 review on the CUTOUT variant the
                // probe and the ground proved, at cutoff 0 (EmissiveNoClip: the roofs sample the ground picture, whose alpha
                // is the reach mask, and must never be clipped by it). Gated on _emissiveSides, and put back to the lit
                // building material by Draw if the side/roof check fails (LitRoofs). Off the emission path the roofs share
                // the lit building material, as before stage C.
                RoofMaterial = _emissiveSides && RoofsActive
                    ? EmissiveNoClip(Matte(new Material(shader) { name = $"QuestTreeMap3D-roofs-{level}" }))
                    : building,
                Meshes = meshes
            });
        }

        /// <summary>
        /// The worker's result has landed: the counts go onto their entries, the checks that need them are
        /// made, and every mesh is queued as one unit of upload work. Then the walls of every floor whose
        /// picture is already here are started, and the pump takes it from there.
        /// </summary>
        private void AfterPrepare(List<FloorData> prepared)
        {
            _prepDone = true;

            foreach (var data in prepared)
            {
                var into = IntoFor(data.Level);
                if (into == null) continue;

                into.Cells = data.Cells;
                into.GroundTriangles = data.GroundTriangles;
                into.GroundTrianglesFull = data.GroundTrianglesFull;
                into.BuildingTriangles = data.BuildingTriangles;
                into.BuildingCount = data.BuildingCount;
                into.Dropped = data.Dropped;
                into.TopTriangles = data.TopTriangles;
                into.SideTriangles = data.SideTriangles;
                into.WallTriangles = data.WallTriangles;
                into.MovedRoofTriangles = data.MovedRoofTriangles;
                into.GroundSkirtTriangles = data.GroundSkirtTriangles;
                into.AtlasTriangles = data.AtlasTriangles;
                into.RoofPictureTriangles = data.RoofPictureTriangles;
                into.RoofCutOutTriangles = data.RoofCutOutTriangles;
                into.RoofCutOutArea = data.RoofCutOutArea;
                into.AtlasArea = data.AtlasArea;
                into.TopArea = data.TopArea;
                into.SideArea = data.SideArea;
                into.TintArea = data.TintArea;
                into.WallsOutside = data.WallsOutside;
                into.WallsPending = data.WallTriangles > 0;
                into.ChunksSplit = data.ChunksSplit;
                into.ChunksMerged = data.ChunksMerged;
            }

            // The check that can fail, and the one an empty or garbage file gets caught by: a view with
            // no triangles in it is a viewport with the markers of a map floating over a flat colour,
            // which looks like a bug in the markers rather than in the mesh. On the COUNTS, before a single
            // mesh is uploaded, so a file that would show nothing costs nothing to refuse.
            var drawable = 0L;
            var pictured = 0;

            foreach (var floor in _floors)
            {
                // Only a floor that CAN be drawn: Draw skips any floor whose material has no picture on
                // it (a textured shader samples white without one), so a band with no picture layer is
                // never drawn at all, and its triangles counting here would pass this check for a view
                // that shows nothing. Flat colours need no picture, so there every floor counts.
                var canDraw = _flatColours || (floor.Layer != null && floor.Layer.HasArtwork);
                if (!canDraw) continue;

                pictured++;
                drawable += floor.Meshes.GroundTriangles + floor.Meshes.BuildingTriangles;
            }

            if (pictured == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has {_levels.Count} band(s) but no picture for " +
                    $"any of them - drawing the flat picture instead.");
                Refuse("no band of it has a picture");
                return;
            }

            if (drawable == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has {_levels.Count} band(s) but not one " +
                    $"triangle in them - drawing the flat picture instead.");
                Refuse("there is no ground in the bands it has");
                return;
            }

            // One unit per mesh. The side materials are made with the first mesh of their side, on this
            // thread, where a Material can be made at all.
            foreach (var data in prepared)
            {
                var into = IntoFor(data.Level);
                if (into == null) continue;

                var level = data.Level;

                // Every mesh through MakeMesh: nothing keeps a CPU copy of any of them. The dollhouse cut is the
                // camera's oblique near plane (ApplyCut), which clips on the GPU and needs no arrays to clip from.
                foreach (var mesh in data.Ground) _work.Enqueue(() => into.Ground.Add(MakeMesh(mesh)));
                foreach (var mesh in data.Roofs) _work.Enqueue(() => into.Buildings.Add(MakeMesh(mesh)));

                foreach (var roof in data.RoofsElsewhere)
                {
                    var item = roof;
                    _work.Enqueue(() => into.RoofsOnOtherFloors.Add((item.Level, MakeMesh(item.Data))));
                }

                for (var slot = 0; slot < data.Sides.Length; slot++)
                {
                    if (data.Sides[slot] == null) continue;

                    var s = slot;

                    foreach (var mesh in data.Sides[slot])
                    {
                        _work.Enqueue(() =>
                        {
                            if (into.Sides[s] == null)
                            {
                                var sideMaterial = Matte(new Material(_buildingShader)
                                    { name = $"QuestTreeMap3D-side{SideOrder[s]}-{level}" });

                                // Spot-sun stage B: a side picture is a finished image, as the ground's is (captured in
                                // raid under the game's light, graded) - drawn as emission, never lit again. The flag
                                // follows the ground's (the sides use the buildings' shader, which is Standard whenever
                                // the ground's is) unless the variant failed its check this session (_sideEmission). Its
                                // _EmissionMap follows the picture wherever Draw assigns it. Review 2026-09-29: on the proven
                                // CUTOUT variant at cutoff 0 (EmissiveNoClip), not the unproven opaque one.
                                if (_emissiveSides) EmissiveNoClip(sideMaterial);

                                into.Sides[s] = new SideTexture { Material = sideMaterial };
                            }

                            into.Sides[s].Meshes.Add(MakeMesh(mesh));
                        });
                    }
                }

                foreach (var pair in data.Atlas)
                {
                    var tile = pair.Key;

                    foreach (var mesh in pair.Value)
                    {
                        _work.Enqueue(() =>
                        {
                            // One Standard, matte, opaque material per (tile, floor); its _MainTex is the tile, assigned
                            // by Draw from this view's TileStore once the tile is cut.
                            if (!into.AtlasByTile.TryGetValue(tile, out var group))
                            {
                                // HQ S3.11: a tile on an alpha page clips on it - the ground's cutout shader and recipe
                                // (S3 review: the shader ResolveGroundShader found, whichever it was; with none, the tile is
                                // not drawn opaque but left to the floor's fallback)
                                var store = _heldTiles ?? _tiles;
                                var alphaTile = store != null && store.AlphaTile(tile);
                                var clippable = alphaTile && _groundCutout && _groundShader != null;
                                var material = Matte(new Material(clippable ? _groundShader : _buildingShader) { name = $"QuestTreeMap3D-tile{tile}-{level}" });
                                if (clippable) MakeCutout(material);

                                group = new SideTexture
                                {
                                    Tile = tile,
                                    Material = material,
                                    Unclippable = alphaTile && !clippable
                                };

                                into.AtlasByTile[tile] = group;
                                into.Atlas.Add(group);
                            }

                            group.Meshes.Add(MakeMesh(mesh));
                        });
                    }
                }

                // Complete only once every mesh of it is in: a view that goes away before this unit leaves
                // an incomplete entry, which the next view throws away and builds again.
                _work.Enqueue(() => into.Complete = true);
            }

            // The walls' colours come from each floor's picture. The floor SHOWING always has its picture by
            // now (the 3D branch only runs once the flat path has it), so its walls start with the rest; a
            // peeled lower floor may still be decoding, and its walls are started by the first frame that
            // has the picture (Draw). An entry reused from the cache with its walls still waiting gets the
            // same chance here.
            _work.Enqueue(StartInitialWalls);
        }

        /// <summary>The cache entry this build prepared a level into.</summary>
        private Built IntoFor(int level)
        {
            if (_preparing == null) return null;

            foreach (var item in _preparing)
                if (item.Level == level) return item.Into;

            return null;
        }

        /// <summary>Starts the walls of every floor whose picture is here - the "initial" walls, which the
        /// build line waits for, so its tint count is the map's and not whatever finished first.</summary>
        private void StartInitialWalls()
        {
            foreach (var floor in _floors)
            {
                var built = floor.Meshes;
                if (built == null || !built.WallsPending || built.WallsRunning) continue;

                Texture picture = null;

                if (!_flatColours && floor.Layer != null && floor.Layer.TryGetSprite(out var sprite) && sprite != null)
                    picture = sprite.texture;

                StartWalls(built, floor.Level, picture, late: false);
            }
        }

        /// <summary>
        /// Advances the build by at most a frame's budget: runs queued units until the budget is spent (one
        /// at least), collects wall workers that have finished, and - once nothing of the first build is
        /// left - queues the cut, then finishes. Called every frame while anything is outstanding.
        /// </summary>
        private void Pump()
        {
            var frame = Stopwatch.StartNew();

            PollWallJobs();

            while (_work.Count > 0 && !_broke)
            {
                var unit = _work.Dequeue();

                try
                {
                    unit();
                }
                catch (Exception ex)
                {
                    // A unit of the first build that throws leaves a map half made - the flat picture is the
                    // better answer. (Wall units catch their own; see EnqueueWallUpload.)
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                        $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");
                    Refuse("could not be turned into meshes");
                    return;
                }

                if (frame.ElapsedMilliseconds >= (_ready ? BackgroundBudgetMs : FrameBudgetMs)) break;
            }

            if (!_ready && _prepDone && _work.Count == 0 && !InitialWallsRunning()) Finish();

            if (!_ready)
            {
                _buildFrames++;
                _longestFrameMs = Math.Max(_longestFrameMs, frame.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Main-thread work per frame while a build is uploading, in milliseconds. One unit is at most one
        /// mesh of <see cref="MaxVerticesPerMesh"/> vertices uploaded, which
        /// measures tens of milliseconds - so a frame is at most this plus one unit, inside the 200 ms the
        /// panel may stall for.
        /// </summary>
        private const long FrameBudgetMs = 100;

        /// <summary>The same budget once the map is on screen, for work that arrives later (a lower floor's
        /// walls): one frame's worth, so the view stays interactive while it finishes. Still one unit at least.</summary>
        private const long BackgroundBudgetMs = 16;

        /// <summary>MakeMesh's counters when this view's build began; see <see cref="LogChunks"/>.</summary>
        private long _uploadTicksAtStart;

        private int _uploadMeshesAtStart;

        /// <summary>
        /// The chunks line, once per build: how the geometry was cut and stored - meshes (relief and buildings), the
        /// vertex strides they came out at (16 compact, 20 with float UVs, 24 with a colour too, 32 float), how many are
        /// sixteen-bit indexed, and the main-thread upload time MakeMesh measured over this build (walls that arrive after
        /// Finish are not in it). The resident estimate is on the build line (<see cref="MeshBytes"/>).
        /// </summary>
        private void LogChunks()
        {
            var seen = new HashSet<Built>();
            var ground = 0;
            var buildings = 0;
            var sixteen = 0;
            var vertices = 0L;
            var strides = new SortedDictionary<int, long>();

            void Count(Mesh mesh, bool isGround)
            {
                if (mesh == null) return;

                if (isGround) ground++;
                else buildings++;

                if (mesh.indexFormat == IndexFormat.UInt16) sixteen++;

                var stride = mesh.vertexBufferCount > 0 ? mesh.GetVertexBufferStride(0) : 0;
                strides.TryGetValue(stride, out var had);
                strides[stride] = had + mesh.vertexCount;
                vertices += mesh.vertexCount;
            }

            foreach (var floor in _floors)
            {
                var built = floor?.Meshes;
                if (built == null || !seen.Add(built)) continue;

                foreach (var mesh in built.Ground) Count(mesh, true);
                foreach (var mesh in BuildingMeshesOf(built)) Count(mesh, false);
            }

            var mix = new List<string>();
            foreach (var pair in strides)
                mix.Add(string.Format(CultureInfo.InvariantCulture, "{0} B x {1:#,##0}", pair.Key, pair.Value));

            Plugin.LogSource?.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "QuestTree: 3D map for {0} - chunks: {1:#,##0} mesh(es) ({2:#,##0} relief, {3:#,##0} building) on a {4} m grid, " +
                "{5:#,##0} vertices by stride [{6}], {7:#,##0} sixteen-bit indexed; uploaded {8:#,##0} mesh(es) in {9:#,##0} ms " +
                "main thread (compact {10}, 16-bit {11}).",
                _mapKey, ground + buildings, ground, buildings, SpatialChunkMetres,
                vertices, string.Join(", ", mix.ToArray()), sixteen,
                _uploadMeshes - _uploadMeshesAtStart,
                (_uploadTicks - _uploadTicksAtStart) * 1000d / Stopwatch.Frequency,
                CompactVertices && CompactSupported() ? "on" : "off", SixteenBitIndices ? "on" : "off"));
        }

        /// <summary>The first build is done: log what it came to, announce the view, and draw from the next
        /// frame on.</summary>
        private void Finish()
        {
            _ready = true;
            _measureFirstFrame = true;
            _timeRender = true;
            _forceRender = true;
            _buildClock.Stop();

            // The jobs this build used are no longer needed by it. See the collection in LateUpdate.
            foreach (var held in _held) ReleasePrep(held.Prep);
            _held.Clear();

            var cells = 0L;
            var groundTriangles = 0L;
            var buildings = 0;
            var buildingTriangles = 0L;
            var dropped = 0;
            var wallTriangles = 0L;
            var tints = 0;
            var wallsWaiting = 0;
            var topTriangles = 0L;
            var sideTriangles = 0L;
            var movedRoofs = 0L;
            var skirts = 0L;
            var atlasTriangles = 0L;
            var pictureRoofs = 0L;
            var cutOutRoofs = 0L;
            var cutOutRoofArea = 0d;
            double atlasArea = 0d, topArea = 0d, sideArea = 0d, tintArea = 0d;
            var wallsOutside = 0L;

            GroundAboveCut(out var groundAbove, out var groundMeasured);
            ComputeMapBounds();

            foreach (var floor in _floors)
            {
                atlasTriangles += floor.Meshes.AtlasTriangles;
                pictureRoofs += floor.Meshes.RoofPictureTriangles;
                cutOutRoofs += floor.Meshes.RoofCutOutTriangles;
                cutOutRoofArea += floor.Meshes.RoofCutOutArea;
                atlasArea += floor.Meshes.AtlasArea;
                topArea += floor.Meshes.TopArea;
                sideArea += floor.Meshes.SideArea;
                tintArea += floor.Meshes.TintArea;
                wallsOutside += floor.Meshes.WallsOutside;
                topTriangles += floor.Meshes.TopTriangles;
                sideTriangles += floor.Meshes.SideTriangles;
                movedRoofs += floor.Meshes.MovedRoofTriangles;
                skirts += floor.Meshes.GroundSkirtTriangles;
                cells += floor.Meshes.Cells;
                groundTriangles += floor.Meshes.GroundTriangles;
                buildings += floor.Meshes.BuildingCount;
                buildingTriangles += floor.Meshes.BuildingTriangles;
                dropped += floor.Meshes.Dropped;
                wallTriangles += floor.Meshes.WallTriangles;
                tints += floor.Meshes.Tints;
                if (floor.Meshes.WallsPending) wallsWaiting++;
            }

            // What the walls came to. "in N tints" is summed over the floors whose walls are built; a floor
            // still waiting for its picture is counted separately, and logs its own line when they are.
            var wallNote = wallTriangles == 0
                ? "no walls"
                : _flatColours
                    ? "walls in flat colour"
                    : string.Format(CultureInfo.InvariantCulture, "walls in {0} tints", tints) +
                      (wallsWaiting > 0
                          ? string.Format(CultureInfo.InvariantCulture,
                              ", {0} floor(s) of walls waiting for a picture", wallsWaiting)
                          : "");

            // Which pictures the building faces went to: the Stage U split. Over every building triangle
            // that was kept (top + sides + tint); percentages rounded, so they may sum to 99 or 101.
            var faces = atlasTriangles + topTriangles + sideTriangles + wallTriangles;
            var sidesNote =
                (AtlasActive
                    ? string.Format(CultureInfo.InvariantCulture, ", atlas {0} page(s) {1} tile(s)", _pageCount,
                        _heldTiles?.Tiles.Count ?? 0)
                    : "") +
                (SidesActive
                    ? string.Format(CultureInfo.InvariantCulture, ", sides {0} ({1})",
                        _sideCount, string.Join(",", _sidesKey.ToCharArray()))
                    : ", sides 0") +
                RoofsText(pictureRoofs, topTriangles, cutOutRoofs, cutOutRoofArea, topArea) +
                (faces > 0
                    ? (AtlasActive
                        ? string.Format(CultureInfo.InvariantCulture, ", faces atlas {0:0} % / top {1:0} % / sides {2:0} % / tint {3:0} %",
                            100d * atlasTriangles / faces, 100d * topTriangles / faces, 100d * sideTriangles / faces,
                            100d * wallTriangles / faces)
                        : string.Format(CultureInfo.InvariantCulture, ", faces top {0:0} % / sides {1:0} % / tint {2:0} %",
                            100d * topTriangles / faces, 100d * sideTriangles / faces, 100d * wallTriangles / faces))
                    : "") +
                (atlasArea + topArea + sideArea + tintArea > 0d
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", by area atlas {0:0} % / top {1:0} % / sides {2:0} % / tint {3:0} %",
                        100d * atlasArea / (atlasArea + topArea + sideArea + tintArea),
                        100d * topArea / (atlasArea + topArea + sideArea + tintArea),
                        100d * sideArea / (atlasArea + topArea + sideArea + tintArea),
                        100d * tintArea / (atlasArea + topArea + sideArea + tintArea))
                    : "") +
                (SidesActive && !LegacyViewRule
                    ? string.Format(CultureInfo.InvariantCulture, ", walls outside 40 deg tinted {0:#,##0}", wallsOutside)
                    : "") +
                (movedRoofs > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", {0:#,##0} top face(s) on the picture of the floor they stand on", movedRoofs)
                    : "") +
                (skirts > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", {0:#,##0} ground-skirt face(s) left to the relief", skirts)
                    : "");

            Plugin.LogSource?.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "QuestTree: 3D map for {0} - {1} band(s) {2:#,##0} cells -> {3:#,##0} triangles, " +
                "{4:#,##0} buildings {5:#,##0} triangles ({11}), built in {6:#,##0} ms over {14} frame(s) " +
                "(longest {15:#,##0} ms), meshes ~{16:#,##0} MB, textures resident ~{17:#,##0} MB, layer {7}, shader {8}, " +
                "ground cutout: {9}{10}{12}{13}, path {18}.",
                _mapKey, _levels.Count, cells, groundTriangles, buildings, buildingTriangles,
                _buildClock.ElapsedMilliseconds, _drawLayer,
                _flatColours ? _shaderName + " (flat colours, no picture)" : _shaderName,
                _cutoutNote + (_reusedFloors ? ", meshes reused" : ""),
                dropped > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", dropped {0:#,##0} building triangle(s) with a vertex that is not a number", dropped)
                    : "",
                wallNote,
                sidesNote,
                float.IsNaN(_cutY) || CutMode != CutByNearPlane
                    ? ""
                    : string.Format(CultureInfo.InvariantCulture,
                        ", cut at {0:0.0} m (level {1}) by the camera's near plane, ground above the cut: {2:#,##0} of {3:#,##0} cells",
                        _cutY, _selectedLevel, groundAbove, groundMeasured),
                _buildFrames,
                _longestFrameMs,
                ResidentMeshBytes() / (1024d * 1024d),

                // Every decoded picture in the shared cache (floors, sides), at 4 B a pixel plus a third for a mip
                // chain, and every atlas tile cut so far (DXT1: half a byte a pixel plus a third). Tiles still
                // waiting their paced cut are not in it yet; the TileStore logs its own total when it finishes.
                (DynamicMapsLibrary.ResidentRasterBytes + TileStore.ResidentBytesAll) / (1024d * 1024d),
                RenderingPathOf(_camera)));

            LogChunks();

            // A floor switch whose entries were all cached: nothing was uploaded, and the cut is one matrix.
            if (_reusedFloors)
            {
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map floor switch for {0} to level {1} - ready in {2:#,##0} ms over {3} frame(s), " +
                    "0 meshes uploaded, 0 clipped, cut {4}.",
                    _mapKey, _selectedLevel, _buildClock.ElapsedMilliseconds, _buildFrames, CutText()));
            }

            // ANNOUNCED, not just placed. The labels were culled when the viewport was built - against the
            // camera as it stood before the mesh landed, with the ground at the fallback height - and
            // Place() moved the camera without telling anyone. Without this the cull and the pin names
            // kept that placeholder's decisions until the player's first drag.
            Moved();
        }

        /// <summary>Forgets everything of a build in progress: queued units, a worker's pending result, wall
        /// jobs. A wall job abandoned mid-flight leaves its entry's walls to be built again by whichever view
        /// draws it next - never half made (see <see cref="AbandonWalls"/>).</summary>
        private void ResetPipeline()
        {
            _work.Clear();

            // Let go of every job this build waited on. Not cancelled here: another view of the same build may
            // attach within the grace - see SharedPrep.
            foreach (var held in _held) ReleasePrep(held.Prep);
            _held.Clear();

            _preparing = null;
            _prepDone = false;
            _ready = false;
            _measureFirstFrame = false;
            _buildFrames = 0;
            _longestFrameMs = 0;
            _reusedFloors = true;

            AbandonWalls();
        }

        /// <summary>The shared jobs this build is waiting on, one per level it prepares. See
        /// <see cref="SharedPrep"/>.</summary>
        private readonly List<(int Level, SharedPrep Prep)> _held = new List<(int Level, SharedPrep Prep)>();

        /// <summary>
        /// One floor's preparation, shared by every view of the same build that needs it. A view HOLDS it while
        /// it waits and lets go once it has the result (or goes away); a job nobody holds is cancelled after
        /// <see cref="PrepIdleGraceMs"/> - a grace, not at once, because a repaint destroys the old view at the
        /// end of its frame and the new view attaches a frame or so later, and cancelling in between would
        /// throw away exactly the work the new view wants. The registry is touched by the main thread and by
        /// the grace timers, so every access is under <see cref="PrepLock"/>.
        /// </summary>
        private sealed class SharedPrep
        {
            public string Key = "";
            public Task<FloorData> Task;
            public CancellationTokenSource Cancel;
            public int Holders;
            public int IdleStamp;
        }

        private static readonly object PrepLock = new object();
        private static readonly Dictionary<string, SharedPrep> _preps = new Dictionary<string, SharedPrep>();

        /// <summary>A job's identity: the build's cache key (file, write time, sides, floor ranges), the level,
        /// and whether the flat-colour shader is in use - the one input the cache key leaves out that changes
        /// the arrays (vertex colours). Two views with the same key would prepare the same floor to the byte.</summary>
        private string PrepKey(int level) =>
            _builtKey + "|" + level.ToString(CultureInfo.InvariantCulture) + (_flatColours ? "|flat" : "");

        /// <summary>How long a job nobody holds is kept for a view that may still attach, in milliseconds.</summary>
        private const int PrepIdleGraceMs = 2000;

        /// <summary>The running (or finished, not yet collected) job for this key - attached to - or a new one.</summary>
        private static SharedPrep AcquirePrep(string key, int level, Prep prep)
        {
            lock (PrepLock)
            {
                if (_preps.TryGetValue(key, out var existing) && !existing.Cancel.IsCancellationRequested &&
                    !existing.Task.IsCanceled && !existing.Task.IsFaulted)
                {
                    existing.Holders++;
                    existing.IdleStamp++;
                    return existing;
                }

                var cancel = new CancellationTokenSource();
                var token = cancel.Token;

                var shared = new SharedPrep
                {
                    Key = key,
                    Cancel = cancel,
                    Holders = 1,
                    Task = System.Threading.Tasks.Task.Run(() => PrepareFloor(prep, level, token), token)
                };

                _preps[key] = shared;
                return shared;
            }
        }

        /// <summary>A view lets go of a job. The last one out starts the grace; if nobody has attached when it
        /// runs out, the job is cancelled and forgotten, and its output - if it had any - is garbage.</summary>
        private static void ReleasePrep(SharedPrep shared)
        {
            int stamp;

            lock (PrepLock)
            {
                if (shared.Holders > 0) shared.Holders--;
                if (shared.Holders > 0) return;

                stamp = ++shared.IdleStamp;
            }

            System.Threading.Tasks.Task.Delay(PrepIdleGraceMs).ContinueWith(_ =>
            {
                lock (PrepLock)
                {
                    // Attached to again (the stamp moved), or held again: nothing to do.
                    if (shared.Holders > 0 || shared.IdleStamp != stamp) return;

                    shared.Cancel.Cancel();

                    if (_preps.TryGetValue(shared.Key, out var current) && ReferenceEquals(current, shared))
                        _preps.Remove(shared.Key);
                }
            });
        }

        /// <summary>Cancels every job and forgets them all - for <see cref="DropCaches"/>, where the map memory
        /// goes: a worker that finished after it would hand its floor to nobody.</summary>
        private static void CancelAllPreps()
        {
            lock (PrepLock)
            {
                foreach (var shared in _preps.Values) shared.Cancel.Cancel();
                _preps.Clear();
            }
        }

        /// <summary>Whether a finished job was cancelled rather than failed - its level is simply asked for again.</summary>
        private static bool WasCancelled(Task task) =>
            task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException;

        /// <summary>The levels this build is preparing, and the entries they go into.</summary>
        private List<(int Level, Built Into)> _preparing;

        /// <summary>The drawn band levels of this build, lowest first.</summary>
        private List<int> _levels = new List<int>();

        /// <summary>Units of main-thread work - a mesh uploaded, a wall build landed - run by <see cref="Pump"/>.</summary>
        private readonly Queue<Action> _work = new Queue<Action>();

        private readonly Stopwatch _buildClock = new Stopwatch();
        private int _buildFrames;
        private long _longestFrameMs;
        private bool _prepDone;

        /// <summary>The first build is finished and the view draws. Until then the backdrop shows.</summary>
        private bool _ready;

        /// <summary>The next drawn frame is the first: time it and say so, once.</summary>
        private bool _measureFirstFrame;

        /// <summary>Draw calls submitted this frame - see <see cref="Submit"/>.</summary>
        private int _drawCalls;

        /// <summary>HQ S1.2: the samples the render texture was made with, for the first-frame line.</summary>
        private int _rtSamples = 1;

        /// <summary>HQ S1.1: what the last rendered frame showed, and what has to differ for the next one to render -
        /// the view version, the cut height and the tile store's version; a build just finished forces one; a
        /// material still waiting for its picture (<see cref="_unsettled"/>) keeps rendering until it has it.</summary>
        private int _renderedViewVersion = int.MinValue;

        private float _renderedCutY = float.NaN;
        private int _renderedTileVersion = -1;
        private bool _forceRender;
        private bool _unsettled;

        /// <summary>HQ S4: clean frames since the last render, and the view version the settled line was last said for -
        /// after <see cref="SettledFrames"/> clean frames following a move, one Info line names the camera, so a screenshot's
        /// pose can be repeated (the PART-00 landmark screenshots).</summary>
        private int _cleanFrames;

        private int _settledViewVersion = int.MinValue;
        private const int SettledFrames = 90;

        /// <summary>HQ S1.1: frames that reached the draw decision, and how many of them rendered - the release line.</summary>
        private long _framesSeen;

        private long _framesRendered;

        private string _shaderName = "";
        private string _cutoutNote = "";

        private bool _restored;

        /// <summary>The cache key this view's current build was begun under. See the cancelled branch of LateUpdate.</summary>
        private string _viewBuildKey;

        /// <summary>The height buildings are cut at, or NaN for no cut. See <see cref="CutHeight"/>.</summary>
        private float _cutY = float.NaN;

        /// <summary>Every building mesh of an entry - roofs, roofs on other floors, wall tints, sides, atlas
        /// faces. Not the ground. For <see cref="ResidentMeshBytes"/>.</summary>
        private static IEnumerable<Mesh> BuildingMeshesOf(Built built)
        {
            foreach (var mesh in built.Buildings) yield return mesh;
            foreach (var roof in built.RoofsOnOtherFloors) yield return roof.Mesh;

            foreach (var tint in built.Walls)
                foreach (var mesh in tint.Meshes)
                    yield return mesh;

            foreach (var side in built.Sides)
            {
                if (side == null) continue;
                foreach (var mesh in side.Meshes) yield return mesh;
            }

            foreach (var atlas in built.Atlas)
            {
                if (atlas == null) continue;
                foreach (var mesh in atlas.Meshes) yield return mesh;
            }
        }

        /// <summary>
        /// Roughly what a mesh of ours costs in memory, in bytes: its vertex stride per vertex (32 bytes as floats, 16-24
        /// compact) and two or four bytes per index by its index format, times the copies held. Every mesh is held ONCE,
        /// on the GPU: each is uploaded non-readable and nothing keeps its worker arrays, since the cut is the
        /// camera's near plane and clips nothing on the CPU. An estimate for the log line, not an accounting.
        /// </summary>
        private static long MeshBytes(Mesh mesh, int copies)
        {
            if (mesh == null) return 0L;

            // The mesh's OWN layout and index format (compact vertices are 16-24 bytes, sixteen-bit indices 2), read from
            // the mesh rather than assumed, so the estimate follows the rollback switches.
            var stride = mesh.vertexBufferCount > 0 ? mesh.GetVertexBufferStride(0) : 0;
            for (var stream = 1; stream < mesh.vertexBufferCount; stream++) stride += mesh.GetVertexBufferStride(stream);

            var indexBytes = mesh.indexFormat == IndexFormat.UInt16 ? 2L : 4L;
            var indices = 0L;

            for (var sub = 0; sub < mesh.subMeshCount; sub++) indices += (long)mesh.GetIndexCount(sub);

            return ((long)mesh.vertexCount * stride + indices * indexBytes) * copies;
        }

        /// <summary>What this view's geometry holds resident: every mesh of every entry it draws, GPU only - no
        /// CPU copy is kept of any mesh. For the build line.</summary>
        private long ResidentMeshBytes()
        {
            var total = 0L;
            var seen = new HashSet<Built>();

            foreach (var floor in _floors)
            {
                var built = floor?.Meshes;
                if (built == null || !seen.Add(built)) continue;

                foreach (var mesh in built.Ground) total += MeshBytes(mesh, 1);
                foreach (var mesh in BuildingMeshesOf(built)) total += MeshBytes(mesh, 1);
            }

            return total;
        }

        /// <summary>Two cut heights are the same cut (NaN is "no cut", equal to itself here).</summary>
        private static bool SameCut(float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : a == b;

        /// <summary>How far above the chosen floor's top the cut is made, in metres: enough to keep that
        /// floor's own ceiling slab and furniture-height geometry out of the cut, not so much that the
        /// floor above starts to show.</summary>
        private const float CutAboveFloor = 0.3f;

        /// <summary>
        /// Where the dollhouse cut goes: just above the top of the chosen floor, or NaN for none.
        ///
        /// Seen on Interchange: choosing the second floor drew that floor's relief as a tray lying inside
        /// the WHOLE mall, because the mall is one building filed under the ground floor (by its centroid)
        /// and stands through every storey. Peeling bands cannot fix that - the building is not in the
        /// bands above - so the buildings of every drawn band are cut at this height instead, and the
        /// floor being looked at is seen from above with the storeys over it taken off.
        ///
        /// No cut when the chosen floor is the top band: nothing is above it to take away, and the map
        /// then looks exactly as it did before the cut existed. No cut either when the floor's height band
        /// is unknown - a cut at a guessed height would slice a building somewhere meaningless.
        /// </summary>
        private float CutHeight()
        {
            if (_file?.Bands == null) return float.NaN;

            var anyAbove = false;

            foreach (var band in _file.Bands)
                if (band != null && band.Level > _selectedLevel) anyAbove = true;

            if (!anyAbove) return float.NaN;

            var layer = LayerOf(_selectedLevel);
            if (layer == null || layer.GameBounds.Count == 0) return float.NaN;

            var top = layer.GameBounds[0].Max.z;

            // The catalog files a floor with no height band as claiming every height (+-2000 m); a cut
            // there cuts nothing.
            if (float.IsNaN(top) || float.IsInfinity(top) || top >= 1000f) return float.NaN;

            return top + CutAboveFloor;
        }

        // --- the dollhouse cut: the camera's oblique near plane -----------------------------------------

        /// <summary>No cut at all: every drawn band is drawn whole. The rollback for <see cref="CutMode"/> - the
        /// CPU precut this replaced comes back only by reverting WP7's viewer commit.</summary>
        private const int CutNone = 0;

        /// <summary>The cut is the private camera's near plane, made oblique so it lies along y = cut height:
        /// the GPU clips every triangle there, exactly at the pixel. See <see cref="ApplyCut"/>.</summary>
        private const int CutByNearPlane = 1;

        /// <summary>How the dollhouse cut is made. Rollback: <see cref="CutNone"/>. Static readonly, not const, so
        /// the test in <see cref="ApplyCut"/> is not a constant the compiler would call unreachable code.</summary>
        private static readonly int CutMode = CutByNearPlane;

        /// <summary>
        /// How far above the cut the camera is kept, in metres. An oblique near plane needs the camera on the
        /// plane's negative side - above the cut - or it clips the wrong half; <see cref="Place"/> dollies out
        /// until the camera is at least this far over it. At the lowest pitch that is about (band height + 0.8)
        /// x 3.9 m from the floor's ground, which is under <see cref="MinDistance"/> for any ordinary storey.
        /// </summary>
        private const float MinCameraAboveCut = 0.5f;

        /// <summary>The private camera's rendering path. Forward: the deferred lighting pass rebuilds positions
        /// from depth with the parameters of an ordinary projection, which an oblique projection breaks (a
        /// view-dependent shading error on Standard). The light is masked to the private layer and switched on
        /// only inside the render bracket either way. Rollback: <see cref="RenderingPath.UsePlayerSettings"/>.</summary>
        private const RenderingPath PrivateCameraPath = RenderingPath.Forward;

        /// <summary>Frames drawn uncut because the camera was not above the cut - said once, in
        /// <see cref="Release"/>.</summary>
        private int _cutSkippedFrames;

        /// <summary>The uncut-frames line is said once per view.</summary>
        private bool _cutSkipLogged;

        /// <summary>
        /// The cut plane y = <paramref name="cutY"/> in the camera's space, as <see cref="Camera.CalculateObliqueMatrix"/>
        /// takes it. In world space the plane is (0, -1, 0, cutY): its normal points DOWN, so a point's value
        /// cutY - y is positive below the cut (kept) and negative above it (clipped), and the camera, above the
        /// cut, is on the negative side - which is what an oblique near plane requires (its w in camera space
        /// is cutY - y_camera, below zero). The view matrix is orthonormal, so the plane goes over as a point
        /// and a normal.
        /// </summary>
        private Vector4 CutPlaneInCameraSpace(float cutY)
        {
            var view = _camera.worldToCameraMatrix;
            var n = view.MultiplyVector(Vector3.down).normalized;
            var p = view.MultiplyPoint(new Vector3(0f, cutY, 0f));

            return new Vector4(n.x, n.y, n.z, -Vector3.Dot(p, n));
        }

        /// <summary>
        /// MAIN THREAD, inside the render bracket only. Replaces the projection's near plane with the cut plane,
        /// so everything above the cut is clipped on the GPU. <see cref="Camera.CalculateObliqueMatrix"/> changes
        /// only the projection's z row: x, y and w of every clip position - so the picture, the pins and the
        /// labels - are those of the ordinary projection. Returns whether the matrix was set; the caller resets
        /// it in its finally, so no other reader of the camera ever sees it.
        ///
        /// A camera that is not above the cut (a band taller than <see cref="Place"/> can dolly past) draws
        /// that frame uncut rather than broken, and the frame is counted.
        /// </summary>
        private bool ApplyCut()
        {
            if (CutMode != CutByNearPlane || float.IsNaN(_cutY) || _camera == null) return false;

            if (!(_camera.transform.position.y > _cutY + 0.01f))
            {
                _cutSkippedFrames++;
                return false;
            }

            // From the camera's own perspective each time, so the aspect follows the render texture
            // (EnsureRenderTexture) and the oblique matrix is never derived from the previous frame's.
            _camera.ResetProjectionMatrix();
            _camera.projectionMatrix = _camera.CalculateObliqueMatrix(CutPlaneInCameraSpace(_cutY));

            return true;
        }

        /// <summary>
        /// How much of the drawn relief rises above the cut: measured cells of the drawn bands whose height is
        /// over <see cref="_cutY"/>, of all measured cells of those bands. The GPU plane clips the ground too
        /// (the CPU cut never touched it), so terrain higher than the chosen floor's top no longer hides it -
        /// intended, and said in the build line. O(cells), once per build, main thread.
        /// </summary>
        private void GroundAboveCut(out long above, out long measured)
        {
            above = 0L;
            measured = 0L;

            if (float.IsNaN(_cutY) || _file == null) return;

            foreach (var level in _levels)
            {
                var band = _file.Band(level);
                var heights = band?.Heights;
                if (heights == null) continue;

                for (var i = 0; i < heights.Length; i++)
                {
                    var code = heights[i];
                    if (code == MapMeshFile.NoHit) continue;

                    measured++;
                    if (_file.HeightOf(code) > _cutY) above++;
                }
            }
        }

        /// <summary>The cut for the log lines: its height, or "off".</summary>
        private string CutText() =>
            float.IsNaN(_cutY) || CutMode != CutByNearPlane
                ? "off"
                : _cutY.ToString("0.0", CultureInfo.InvariantCulture) + " m";

        /// <summary>The path the private camera actually renders with, for the build line.</summary>
        private static string RenderingPathOf(Camera camera)
        {
            try { return camera != null ? camera.actualRenderingPath.ToString() : "none"; }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>Whether the mesh's extent is the picture's. See <see cref="ExtentTolerance"/>.</summary>
        /// <param name="complaint">What differs, for the log line.</param>
        private bool ExtentAgrees(out string complaint)
        {
            complaint = "";

            var layer = ResolveLayer();

            // REFUSED, not waved through. The extent is the only thing that ties the geometry to the
            // picture and to the markers; with no rectangle to check against there is nothing to say the
            // mesh belongs to this map at all, and a viewer that draws it anyway is guessing.
            if (layer == null || !layer.HasBounds)
            {
                complaint = "cannot be checked - this floor declares no rectangle of its own";
                return false;
            }

            var dx = Mathf.Abs((float)_file.MinX - layer.BoundsMin.x);
            var dz = Mathf.Abs((float)_file.MinZ - layer.BoundsMin.y);
            var dX = Mathf.Abs((float)_file.MaxX - layer.BoundsMax.x);
            var dZ = Mathf.Abs((float)_file.MaxZ - layer.BoundsMax.y);

            if (dx <= ExtentTolerance && dz <= ExtentTolerance &&
                dX <= ExtentTolerance && dZ <= ExtentTolerance)
            {
                return true;
            }

            complaint = string.Format(
                CultureInfo.InvariantCulture,
                "covers ({0:0.#}, {1:0.#})..({2:0.#}, {3:0.#}) where the picture covers " +
                "({4:0.#}, {5:0.#})..({6:0.#}, {7:0.#})",
                _file.MinX, _file.MinZ, _file.MaxX, _file.MaxZ,
                layer.BoundsMin.x, layer.BoundsMin.y, layer.BoundsMax.x, layer.BoundsMax.y);

            return false;
        }

        /// <summary>
        /// The band levels to draw, lowest first: the floor peel. Every band at or below the chosen
        /// floor, so looking at the second storey of Interchange shows it standing on the first and the
        /// ground - that is what makes it read as a building rather than a floating slab.
        ///
        /// Capped at the picture cache's ceiling, keeping the HIGHEST bands. Each drawn band holds its
        /// floor's picture resident, and asking for one more than the cache holds would evict a texture
        /// this view asks for again next frame - a 39 MiB decode per frame, forever. Eight bands are
        /// possible in the format and six fit in the cache; no captured map has more than four.
        /// </summary>
        private List<int> DrawnLevels()
        {
            var levels = new List<int>();

            foreach (var band in _file.Bands)
            {
                if (band == null || band.Level > _selectedLevel) continue;
                levels.Add(band.Level);
            }

            // Nothing at or below the floor showing: the lowest band there is, rather than nothing at
            // all. That happens to a capture whose basement has a picture but no relief - the player has
            // selected level -1 and the mesh starts at 0 - and drawing the ground under them is a better
            // answer than refusing the whole file over which floor happens to be picked.
            if (levels.Count == 0)
            {
                var lowest = int.MaxValue;

                foreach (var band in _file.Bands)
                    if (band != null && band.Level < lowest) lowest = band.Level;

                if (lowest != int.MaxValue) levels.Add(lowest);

                return levels;
            }

            levels.Sort();

            // ONE LESS than the cache holds. The floor showing is also loaded by the flat path - the
            // sidebar and the kept-viewport key both read its sprite - so a peel that filled the cache
            // exactly would leave that one texture as the eviction victim, and the two sides would take
            // turns evicting each other's picture at 39 MiB a decode.
            var ceiling = Mathf.Max(1, DynamicMapsLibrary.MaxResidentSprites - 1);

            while (levels.Count > ceiling) levels.RemoveAt(0);

            return levels;
        }

        /// <summary>The first of <see cref="Shaders"/> that resolves in this scene.</summary>
        /// <param name="name">Its name, for the log line.</param>
        private static Shader ResolveShader(out string name)
        {
            for (var i = 0; i < Shaders.Length; i++)
            {
                var shader = Shader.Find(Shaders[i]);
                if (shader == null) continue;

                name = Shaders[i];
                return shader;
            }

            name = "none";
            return null;
        }

        /// <summary>Whether every floor of this view came out of the cache, for the build log line -
        /// "reused" is the difference between a click that hitches and one that does not, and it is worth
        /// being able to read that off the log rather than infer it from the milliseconds.</summary>
        private bool _reusedFloors = true;

        /// <summary>
        /// The ground's shader, and whether it will really clip on the picture's alpha.
        ///
        /// WHY THE GROUND IS A SPECIAL CASE. A captured picture is the walkable cut-out: alpha 255 inside
        /// the playable area and 0 outside it. The relief is not - the rays hit the whole rectangle, so
        /// the ground mesh covers every cell of it. Drawn with an opaque shader, the surround draws as the
        /// RGB of a fully transparent pixel, which is black: a black apron around the map where the flat
        /// view shows the panel's own backdrop through the alpha. Clipping the ground on alpha puts that
        /// back.
        ///
        /// Standard does it with a material setup rather than a different shader - the same switch its own
        /// inspector makes for Rendering Mode "Cutout", spelled out here because there is no inspector at
        /// runtime. Legacy Diffuse has a separate cutout shader instead, which may or may not be loaded in
        /// this scene; if it is not, the ground stays opaque and the log says "no", because a black apron
        /// is a cosmetic fault and a missing shader is not worth refusing a map over.
        /// </summary>
        /// <param name="opaque">The shader the buildings use - the first of <see cref="Shaders"/> that
        /// resolved.</param>
        /// <param name="opaqueName">Its name.</param>
        /// <param name="cutout">Whether the returned shader, set up by
        /// <see cref="MakeGroundMaterial"/>, clips on alpha.</param>
        /// <param name="note">What the build log line says after "ground cutout: ".</param>
        private static Shader ResolveGroundShader(
            Shader opaque, string opaqueName, out bool cutout, out string note)
        {
            if (opaqueName == "Standard")
            {
                cutout = true;
                note = "yes";
                return opaque;
            }

            if (opaqueName == "Legacy Shaders/Diffuse")
            {
                var legacy = Shader.Find(LegacyCutout);

                if (legacy != null)
                {
                    cutout = true;
                    note = "yes (" + LegacyCutout + ")";
                    return legacy;
                }
            }

            // Unlit/Texture and Hidden/Internal-Colored: no cutout variant worth reaching for - the first
            // has one only under a name the probe never saw loaded, and the second has no texture at all,
            // so there is no alpha to clip against in the first place.
            cutout = false;
            note = "no";
            return opaque;
        }

        /// <summary>The cutout shader tried for a scene where only Legacy Diffuse resolved.</summary>
        private const string LegacyCutout = "Legacy Shaders/Transparent/Cutout/Diffuse";

        private Shader _groundShader;
        private bool _groundCutout;

        /// <summary>Whether the only shader that resolved draws vertex colours and has no texture. Read by
        /// <see cref="Draw"/>, which must not ask the picture cache for a texture it cannot use: the
        /// material's mainTexture would stay null whatever was assigned, so the ask would repeat every
        /// frame and move a floor to the front of the sprite cache's queue sixty times a second.</summary>
        private bool _flatColours;

        /// <summary>
        /// One floor's ground material, alpha-clipped when <see cref="ResolveGroundShader"/> found a way
        /// to be.
        ///
        /// The Standard recipe is the whole of what its inspector's "Cutout" mode does: the render type
        /// tag, the mode value the shader's own GUI reads back, the clip threshold, opaque blending with
        /// depth written, the _ALPHATEST_ON keyword that actually compiles the clip in - and the
        /// AlphaTest queue, so the ground draws after the opaque geometry and its discarded fragments
        /// leave no depth behind. The two blend keywords are switched OFF explicitly rather than left
        /// alone: a material that has been through this path once must not carry a blend mode into a
        /// clip mode, and Unity's own setup function disables them for exactly that reason.
        /// </summary>
        /// <param name="level">The floor, for the material's name.</param>
        private Material MakeGroundMaterial(int level)
        {
            var material = Matte(new Material(_groundShader) { name = $"QuestTreeMap3D-ground-{level}" });

            // Spot-sun stage B: the picture drawn as EMISSION - the finished image at its own value, never lit, never
            // shadowed, never divided by a light estimate. The flag holds only on the Standard shader, which always clips
            // (ResolveGroundShader), so the cutout goes on first and the emission recipe after it, as the probe proved it.
            if (_emissiveGround)
            {
                Emissive(_groundCutout ? MakeCutout(material) : material);

                // SELF-TEST "ground" (QUESTTREE_PROBE_SABOTAGE): the keyword off with the flag left on - the emission is
                // never sampled, the ground reads black, and CheckPictureEmission must say MISMATCH
                if (SelfTest == "ground") material.DisableKeyword("_EMISSION");

                return material;
            }

            // stage 1 (the fallback: a failed probe, the Legacy cutout shader, or the switch off): the picture is
            // already lit (the game's sun and the capture's own light, developed to its percentiles) - its colour is divided by the light the flat ground gets, so it shows at its own value and the
            // sun only adds its slope shading and the buildings' shadows on top (_Color takes values over 1)
            if (material.HasProperty("_Color")) material.SetColor("_Color", GroundColour(Plan, UpShareFor(Plan, Lighting == ModSettings.MapLightMode.Sun), Lighting == ModSettings.MapLightMode.Sun));

            if (!_groundCutout) return material;

            return MakeCutout(material);
        }

        /// <summary>
        /// Spot-sun stage B rollback: false puts the ground back on stage 1's division path (_Color = one over the flat
        /// ground's light) and the side pictures back to lit, whatever the probe found. A readonly field, not a const, so
        /// the branch it switches off is not unreachable code to the compiler.
        /// </summary>
        private static readonly bool EmissiveGround = true;

        /// <summary>The self-test switch the light probe reads (Map3DLightProbe.SelfTestVariable), read once here the same
        /// way. "ground" builds the ground material with the flag on but WITHOUT the _EMISSION keyword, so the emission
        /// check must log MISMATCH (black against the picture) - the proof that check can fail. The probe itself ignores
        /// "ground" (it acts on "shadow" and "emission" only), so it still passes and the flag still comes on. Stage D:
        /// "tonemap" keeps the camera LDR (allowHDR off, an ARGB32 target) on the tonemap path, so the tonemap calibration
        /// must log CLIPPED and fall back to the plain anchor (<see cref="CalibrateTonemap"/>). "framecheck" halves the
        /// tonemap frame check's prediction, so it must log TOO BRIGHT and fall back (<see cref="CheckTonemapFrame"/>).</summary>
        private const string SelfTestVariable = "QUESTTREE_PROBE_SABOTAGE";

        private static readonly string SelfTest = ReadSelfTest();

        /// <summary>The self-test switch, read once. A process that may not read its environment runs the normal path.</summary>
        private static string ReadSelfTest()
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(SelfTestVariable);
                return value == null ? "" : value.Trim().ToLowerInvariant();
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Whether this build draws the ground's picture as emission: <see cref="EmissiveGround"/> is on, the ground's
        /// shader is Standard (the Legacy cutout fallback has no emission, and neither does Unlit/Texture or
        /// Hidden/Internal-Colored), and the session's light probe ran and its emission check read the texel back
        /// (Map3DLightProbe.Result.Emission). Decided once per build by <see cref="DecideEmission"/>, before any material is
        /// made, so every material of the build agrees with the first-frame line.
        /// </summary>
        private bool _emissiveGround;

        /// <summary>Whether this build draws the side pictures as emission: as <see cref="_emissiveGround"/> (the sides use
        /// the buildings' shader, which is Standard whenever the ground's is), unless the side/roof emission check failed
        /// this session (<see cref="_sideEmission"/>). Since the 2026-09-29 review the sides and roofs draw on the cutout
        /// variant at cutoff 0 (<see cref="EmissiveNoClip"/>); the check proves that material, not an opaque variant.</summary>
        private bool _emissiveSides;

        /// <summary>Why <see cref="_emissiveGround"/> is false, for the first-frame line; empty when it is true.</summary>
        private string _emissiveWhy = "";

        /// <summary>The factor on the pictures' emission, _EmissionColor = white x this. 1 on the plain path (the picture
        /// then shows at its own value in an LDR frame); on the tonemap path the scale <see cref="CalibrateTonemap"/> read
        /// back, so the picture's mid grey comes out of the tonemap as mid grey.</summary>
        private float _emissionScale = 1f;

        /// <summary>Whether the post-processing stack was attached to this view's camera - then every render is graded by it,
        /// and a rendered pixel cannot be held against the picture's raw texel.</summary>
        private bool _postProcessAttached;

        /// <summary>
        /// Spot-sun stage D: whether this view renders on the TONEMAP path - the 1.8 anchor (<see cref="WhiteAnchor"/>),
        /// no ambient floor (<see cref="AmbientFloor"/>) and the calibrated emission scale. Set in Build from what
        /// Map3DPostProcess really attached, and taken back to false by <see cref="CalibrateTonemap"/> when the path proves
        /// not to be HDR (or the calibration cannot be made): an unproven HDR path gets the plain anchor, which cannot clip.
        /// </summary>
        private bool _tonemapOn;

        /// <summary>Whether the camera renders HDR into an ARGBHalf target: the tonemap path, unless the self-test "tonemap"
        /// forces the LDR one. Decided once in Build and kept for the view (a calibration fallback leaves the target as it is).</summary>
        private bool _hdrTarget;

        /// <summary>True only inside <see cref="CalibrateTonemap"/>'s renders: RenderNow then leaves the light off, the fog
        /// off and the floor cut out, so the quads read the tonemap of their emission and nothing else.</summary>
        private bool _calibrating;

        /// <summary>The emission values the calibration draws, one quad each, left to right. 0.18 and 0.32 are the shadows'
        /// and the mid-tones' toe, 0.5 the picture's mid grey at scale 1, 1.0 and 1.8 the step the clip check reads (1.8
        /// is <see cref="WhiteInSunTonemap"/>), 2.5 the head room over it.</summary>
        private static readonly float[] CalibrationValues = { 0.18f, 0.32f, 0.5f, 1f, 1.8f, 2.5f };

        /// <summary>T(v): what each of <see cref="CalibrationValues"/> read back as, 0..1 (the mean of r, g and b).</summary>
        private readonly float[] _tonemapCurve = new float[CalibrationValues.Length];

        /// <summary>Whether <see cref="_tonemapCurve"/> holds a real read (the calibration got as far as its renders).</summary>
        private bool _tonemapRead;

        /// <summary>Why the tonemap path fell back after Attach ("CLIPPED", "calibration failed ..."), for the first-frame
        /// line; null while it stands.</summary>
        private string _tonemapFallback;

        /// <summary>How far in front of the camera the calibration quads stand: well past the 0.5 m near plane, near enough
        /// that nothing of the map (not drawn in those renders anyway) could be in front.</summary>
        private const float CalibrationDistance = 2f;

        /// <summary>The clip check: T(1.8) - T(1.0) under this (5 steps of 8 bits) means every value above 1 came out as 1
        /// - an LDR target or intermediate somewhere - and the 1.8 anchor would draw every lit face flat white.</summary>
        private const float CalibrationClipStep = 5f / 255f;

        /// <summary>The picture's mid grey, and what it should read after the tonemap: the emission scale is the one that
        /// takes a 0.5 texel to a 0.5 read.</summary>
        private const float PictureMidGrey = 0.5f;

        /// <summary>The emission scale's lower clamp: a curve whose toe already reads 0.5 at under 0.25 is not a tonemap
        /// the pictures should be halved for.</summary>
        private const float EmissionScaleMin = 0.5f;

        /// <summary>The emission scale's upper clamp: a curve that never reaches 0.5 by 2.5 (a failed or foreign grade)
        /// must not multiply the pictures without bound.</summary>
        private const float EmissionScaleMax = 4f;

        /// <summary>The target the calibration line reports against (it does not act on it): a 0.32 mid-shadow should not
        /// be crushed. Review 2026-09-29: the anchor's own read range (was 0.82..0.96) is gone - the anchor is taken from
        /// the curve at <see cref="AnchorReadTarget"/>, and the line says CLAMPED when it cannot be.</summary>
        private const float ShadowReadMin = 0.2f;

        /// <summary>The Standard shader's _EmissionColor, looked up once: the calibration, the checks and the per-draw
        /// scale match set it.</summary>
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        /// <summary>
        /// Spot-sun stage D: reads the tonemap's curve back, once per build before the first real render, on the tonemap
        /// path only. Six quads 2 m in front of the camera, one per <see cref="CalibrationValues"/> across the view, each the
        /// stage B emission recipe (<see cref="Emissive"/>: black albedo, a 1x1 white emission map, _EmissionColor = v) on
        /// the Standard shader, drawn for this camera alone with no light probes and no shadows. They go through the whole
        /// bracket (<see cref="RenderWindow"/> into a temporary target, the post-processing active) with the light, the fog
        /// and the cut off (<see cref="_calibrating"/>); each quad's centre pixel is its T(v). From the curve: the emission
        /// scale e with T(e x 0.5) = 0.5, interpolated linearly between the samples. Two checks that can fail: an empty
        /// render compared with the brightest quad (the quads were drawn at all), and T(1.8) - T(1.0) at least
        /// <see cref="CalibrationClipStep"/> (the path is HDR) - the self-test "tonemap" forces an LDR target so the second
        /// must say CLIPPED. Either failing puts the view on the plain anchor and the ambient floor with e = 1. One log
        /// line; never throws.
        /// </summary>
        /// <param name="shader">The Standard shader the probe measured, or null on the legacy fallback (no emission).</param>
        private void CalibrateTonemap(Shader shader)
        {
            var f = CultureInfo.InvariantCulture;
            var count = CalibrationValues.Length;
            var meshes = new Mesh[count];
            var materials = new Material[count];
            Texture2D white = null;

            _emissionScale = 1f;
            _tonemapRead = false;
            _tonemapAnchor = WhiteInSunTonemap;
            _tonemapFallback = null;

            try
            {
                if (shader == null)
                {
                    TonemapFallback("calibration skipped (legacy shader, no emission to calibrate with)");
                    Plugin.LogSource?.LogInfo("QuestTree: 3D map tonemap calibration - skipped (legacy shader), the plain anchor is used.");
                    return;
                }

                if (_camera == null || _rt == null)
                {
                    TonemapFallback("calibration skipped (no camera)");
                    Plugin.LogSource?.LogInfo("QuestTree: 3D map tonemap calibration - skipped (no camera), the plain anchor is used.");
                    return;
                }

                white = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    name = "QuestTreeMap3D-calibration-white",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                };
                white.SetPixel(0, 0, Color.white);
                white.Apply(false);

                // the quads across the view at the calibration distance: a column each, 70 % of its width, 60 % of the height
                var eye = _camera.transform;
                var halfHeight = CalibrationDistance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var halfWidth = halfHeight * _camera.aspect;
                var column = 2f * halfWidth / count;
                var right = eye.right * (column * 0.35f);
                var up = eye.up * (halfHeight * 0.3f);
                var toward = -eye.forward;

                var width = _rt.width;
                var height = _rt.height;
                var px = new int[count];
                var py = new int[count];
                var y0 = height - 1;
                var y1 = 0;

                for (var i = 0; i < count; i++)
                {
                    var centre = eye.position + eye.forward * CalibrationDistance + eye.right * (-halfWidth + column * (i + 0.5f));
                    meshes[i] = CalibrationQuad(centre, right, up, toward);

                    // Play-test 2026-09-29 (every quad read 0.00, yet drawn differed from empty): the quads were on the
                    // OPAQUE _EMISSION variant, which nothing had proven - a build without it falls back to the keyword-less
                    // variant, the black albedo with the light off, exactly 0/0/0. The CUTOUT emission recipe is the one the
                    // light probe and the ground proved (Map3DLightProbe's rig.Emissive); the white texel's alpha 1 passes the clip.
                    var material = Matte(new Material(shader) { name = "QuestTreeMap3D-calibration-" + i.ToString(f) });
                    material.mainTexture = white;
                    EmissiveNoClip(material);
                    var v = CalibrationValues[i];
                    material.SetColor(EmissionColorId, new Color(v, v, v, 1f));
                    materials[i] = material;

                    var at = _camera.WorldToViewportPoint(centre);
                    px[i] = Mathf.Clamp(Mathf.RoundToInt(at.x * (width - 1)), 0, width - 1);
                    py[i] = Mathf.Clamp(Mathf.RoundToInt(at.y * (height - 1)), 0, height - 1);
                    y0 = Mathf.Min(y0, py[i]);
                    y1 = Mathf.Max(y1, py[i]);
                }

                var window = new RectInt(0, y0, width, y1 - y0 + 1);

                // a pixel between quads 0 and 1 (each quad is 70 % of its column): the backdrop in both renders when only
                // the quads went black, black in the drawn one too when the whole render did
                var gapX = Mathf.Clamp(Mathf.RoundToInt((1f / count) * (width - 1)), 0, width - 1);
                var gap = (py[0] - y0) * width + gapX;

                _calibrating = true;

                // Play-test 2026-09-29: a warm-up render first, discarded - the camera's first render with a freshly added
                // PostProcessLayer is the one the calibration used to read, and nothing else proves that render is whole
                RenderWindow(window, null);

                var drawn = RenderWindow(window, () =>
                {
                    for (var i = 0; i < count; i++)
                        Graphics.DrawMesh(meshes[i], Matrix4x4.identity, materials[i], _drawLayer, _camera, 0, null,
                            ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
                });
                var empty = RenderWindow(window, null);
                _calibrating = false;

                for (var i = 0; i < count; i++)
                {
                    var c = drawn[(py[i] - y0) * width + px[i]];
                    _tonemapCurve[i] = (c.r + c.g + c.b) / (3f * 255f);
                }

                _tonemapRead = true;

                var brightest = (py[count - 1] - y0) * width + px[count - 1];
                var clipped = TonemapAt(1.8f) - TonemapAt(1f) < CalibrationClipStep;

                // Play-test 2026-09-29: every quad at black is not a clip - no emission reached the frame - so it is NOT
                // DRAWN, like quads that read the backdrop; CLIPPED is kept for a curve that really flattens above 1
                var black = true;
                for (var i = 0; i < count; i++) black &= _tonemapCurve[i] < 1.5f / 255f;

                // review 2026-09-29: the anchor is taken from the curve, so "out of range" could hardly fire; what can is
                // the anchor pinned at an end - the curve reaches 0.9 below 0.92 or never by 2.5
                var anchorClamped = false;
                var notDrawn = black || Near(drawn[brightest], empty[brightest], 5);

                if (notDrawn)
                    TonemapFallback(black ? "NOT DRAWN (every quad read black)" : "NOT DRAWN (the quads read the backdrop)");
                else if (clipped) TonemapFallback("CLIPPED");
                else
                {
                    _emissionScale = Mathf.Clamp(InverseTonemap(PictureMidGrey) / PictureMidGrey, EmissionScaleMin, EmissionScaleMax);
                    var wanted = InverseTonemap(AnchorReadTarget);
                    _tonemapAnchor = Mathf.Clamp(wanted, WhiteInSun, CalibrationValues[count - 1]);
                    anchorClamped = wanted <= WhiteInSun || wanted >= CalibrationValues[count - 1];
                    _plan = null;   // the exposure budget follows the anchor
                }

                var anchorRead = TonemapAt(_tonemapOn ? _tonemapAnchor : WhiteInSunTonemap);
                var shadowOk = TonemapAt(0.32f) >= ShadowReadMin;

                Plugin.LogSource?.LogInfo(string.Format(
                    f,
                    "QuestTree: 3D map tonemap calibration - T(0.18/0.32/0.5/1.0/1.8/2.5) = {0:0.00}/{1:0.00}/{2:0.00}/{3:0.00}/{4:0.00}/{5:0.00}, " +
                    "emission scale x{6:0.00}, white in the sun {7:0.00} reads {8:0.00} {9} (1.80 reads {12:0.00}), " +
                    "gap {13}/{14}/{15} vs backdrop {16}/{17}/{18}, {10}{11}",
                    _tonemapCurve[0], _tonemapCurve[1], _tonemapCurve[2], _tonemapCurve[3], _tonemapCurve[4], _tonemapCurve[5],
                    _emissionScale, _tonemapOn ? _tonemapAnchor : WhiteInSunTonemap, anchorRead,
                    anchorClamped ? "CLAMPED (0.90 is not on the curve between 0.92 and 2.5)" : shadowOk ? "ok" : "ok, 0.32 crushed",
                    notDrawn ? (black ? "NOT DRAWN (every quad black)" : "NOT DRAWN") : clipped ? "CLIPPED" : "hdr",
                    notDrawn || clipped
                        ? string.Format(f, " - tonemap {0}: the plain anchor {1:0.00} and the ambient floor are used{2}",
                            notDrawn ? "not calibrated" : "clipped", WhiteInSun,
                            SelfTest == "tonemap" ? " (SELF-TEST: LDR target forced)" : "") +
                          ", post-processing taken off"
                        : ".",
                    TonemapAt(WhiteInSunTonemap),
                    drawn[gap].r, drawn[gap].g, drawn[gap].b, empty[gap].r, empty[gap].g, empty[gap].b));
            }
            catch (Exception ex)
            {
                TonemapFallback("calibration failed (" + ex.GetType().Name + ")");
                Plugin.LogSource?.LogInfo(string.Format(f,
                    "QuestTree: 3D map tonemap calibration - could not be made ({0}: {1}), the plain anchor {2:0.00} is used.",
                    ex.GetType().Name, ex.Message, WhiteInSun));
            }
            finally
            {
                _calibrating = false;
                for (var i = 0; i < count; i++)
                {
                    if (meshes[i] != null) Destroy(meshes[i]);
                    if (materials[i] != null) Destroy(materials[i]);
                }

                if (white != null) Destroy(white);
            }
        }

        /// <summary>Back to the plain path for this view: the plain anchor (<see cref="WhiteAnchor"/> follows
        /// <see cref="_tonemapOn"/>), the ambient floor (the lazy plan is cleared so it is resolved again with it) and the
        /// pictures at their own value - the materials already made are brought to it here, since a fallback can come
        /// mid-view (the stack detached) after BeginBuild made them at the calibrated scale: each floor's ground (its
        /// emission, or the lit fallback's _Color, which carries the scale too) and its side pictures.
        /// Play-test 2026-09-29: and the post-processing OFF for the view - the stack taken off the camera
        /// (<see cref="Map3DPostProcess.TakeOff"/>), allowHDR false and the ARGB32 target - so a fallback renders exactly as
        /// the tonemap setting off does. Before, the stack stayed on and graded the plain anchor's frame with ACES and +0.6
        /// EV, which drew the whole view blown out.</summary>
        private void TonemapFallback(string why)
        {
            _tonemapOn = false;
            _tonemapFallback = why;
            _emissionScale = 1f;
            _tonemapAnchor = WhiteInSunTonemap;
            _plan = null;

            if (_postProcessAttached || Map3DPostProcess.Attached) Map3DPostProcess.TakeOff(_camera, "tonemap " + why);
            _postProcessAttached = false;
            _hdrTarget = false;

            if (_camera != null)
            {
                _camera.allowHDR = false;

                // remade as ARGB32 now when the camera draws into it; inside RenderWindow the camera is on a temporary of
                // the old descriptor, which that render may finish on, and the next Draw's EnsureRenderTexture remakes it
                if (_camera.targetTexture == _rt) EnsureRenderTexture();
            }

            for (var i = 0; i < _floors.Count; i++)
            {
                var floor = _floors[i];
                var ground = floor?.GroundMaterial;

                if (ground != null)
                {
                    if (_emissiveGround && ground.IsKeywordEnabled("_EMISSION")) MatchEmissionScale(ground);
                    else if (!_emissiveGround && ground.HasProperty("_Color"))
                    {
                        var sun = Lighting == ModSettings.MapLightMode.Sun;
                        ground.SetColor("_Color", GroundColour(Plan, UpShareFor(Plan, sun), sun));
                    }
                }

                // Pictures stage C: the roofs' emission at the ground's scale, so a roof and the street beside it agree
                var roofs = floor?.RoofMaterial;
                if (roofs != null && roofs != floor.BuildingMaterial && roofs.IsKeywordEnabled("_EMISSION"))
                    MatchEmissionScale(roofs);

                var sides = floor?.Meshes?.Sides;
                if (sides == null) continue;

                for (var slot = 0; slot < sides.Length; slot++)
                {
                    var material = sides[slot]?.Material;
                    if (material != null && material.IsKeywordEnabled("_EMISSION")) MatchEmissionScale(material);
                }
            }

            _forceRender = true;
        }

        /// <summary>One calibration quad in world space (drawn at the identity), wound clockwise as seen from
        /// <paramref name="toward"/>'s side - the camera's - so it is not culled.</summary>
        private static Mesh CalibrationQuad(Vector3 centre, Vector3 right, Vector3 up, Vector3 toward)
        {
            var mesh = new Mesh { name = "QuestTreeMap3D-calibration" };
            mesh.vertices = new[] { centre - right - up, centre - right + up, centre + right + up, centre + right - up };
            mesh.normals = new[] { toward, toward, toward, toward };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>T(v) from the calibration's samples, linear between them and held at the ends.</summary>
        private float TonemapAt(float v)
        {
            var n = CalibrationValues.Length;
            if (v <= CalibrationValues[0]) return _tonemapCurve[0];

            for (var i = 1; i < n; i++)
            {
                if (v > CalibrationValues[i]) continue;

                var a = CalibrationValues[i - 1];
                var b = CalibrationValues[i];
                return Mathf.Lerp(_tonemapCurve[i - 1], _tonemapCurve[i], (v - a) / (b - a));
            }

            return _tonemapCurve[n - 1];
        }

        /// <summary>The v with T(v) = <paramref name="read"/>: the first sample interval that brackets it, linear inside it;
        /// the end values when the curve never reaches it (the caller clamps the scale).</summary>
        private float InverseTonemap(float read)
        {
            var n = CalibrationValues.Length;
            if (read <= _tonemapCurve[0]) return CalibrationValues[0];

            for (var i = 1; i < n; i++)
            {
                var lo = _tonemapCurve[i - 1];
                var hi = _tonemapCurve[i];
                if (read > hi || hi <= lo) continue;

                return Mathf.Lerp(CalibrationValues[i - 1], CalibrationValues[i], (read - lo) / (hi - lo));
            }

            return CalibrationValues[n - 1];
        }

        /// <summary>The first-frame line's tonemap clause: what the path is and what it read, or why it is off.</summary>
        private string TonemapText()
        {
            var f = CultureInfo.InvariantCulture;

            if (_tonemapOn)
                return string.Format(f, "{0}, post-exposure {1:0.00}, anchor {2:0.00} reads {3:0.00}, ambient floor {4}, emission x{5:0.00}",
                    Map3DPostProcess.TonemapperName, Map3DPostProcess.PostExposure, _tonemapAnchor,
                    _tonemapRead ? TonemapAt(_tonemapAnchor) : -1f,
                    AmbientFloorOfSunTonemap > 0f ? AmbientFloorOfSunTonemap.ToString("0.00", f) : "off",
                    _emissionScale);

            if (!ModSettings.PostProcessingWanted) return "off (setting)";

            // play-test 2026-09-29: a fallback takes the stack off, so its reason comes before "not attached"
            if (_tonemapFallback != null)
                return string.Format(f, "{0}, plain anchor {1:0.00}, post-processing taken off", _tonemapFallback, WhiteInSun);

            if (!_postProcessAttached) return string.Format(f, "off (not attached), plain anchor {0:0.00}", WhiteInSun);

            return string.Format(f, "{0}, plain anchor {1:0.00}", _tonemapFallback ?? "off", WhiteInSun);
        }

        /// <summary>Stage D: the side materials outlive a view (the floor cache), so one made under another view's
        /// calibration is brought to this view's scale before it is drawn. A compare first, so a draw sets nothing when it
        /// is already right.</summary>
        private void MatchEmissionScale(Material material)
        {
            var want = new Color(_emissionScale, _emissionScale, _emissionScale, 1f);
            if (material.GetColor(EmissionColorId) != want) material.SetColor(EmissionColorId, want);
        }

        /// <summary>Whether this build's tonemap frame check (<see cref="CheckTonemapFrame"/>) is made or given up. Reset per
        /// build in BeginBuild.</summary>
        private bool _frameChecked;

        /// <summary>The rendered frames the frame check has tried and found unusable (centre covered, off the ground).</summary>
        private int _frameCheckTries;

        /// <summary>The most rendered frames the frame check tries before it gives up for the build (two renders each).</summary>
        private const int FrameCheckTries = 8;

        /// <summary>How far the frame's centre may sit from the curve's prediction, as a share of it (summed r+g+b).</summary>
        private const float FrameCheckTolerance = 0.15f;

        /// <summary>The smallest predicted r+g+b, in 8-bit steps, the frame check judges: darker, 15 % is under the noise.</summary>
        private const int FrameCheckDarkest = 60;

        /// <summary>The most the frame check's nine picture samples may sit from their mean (summed r+g+b, as a share).</summary>
        private const float FrameCheckUniform = 0.06f;

        /// <summary>The alpha the image may get before the frame check falls back: above it the premultiplied UI draw
        /// scales the frame.</summary>
        private const float FrameCheckAlphaMax = 1.05f;

        /// <summary>The last <see cref="UniformTexel"/> spread, for the frame check's line.</summary>
        private float _frameCheckSpread;

        /// <summary>
        /// The frame check's reference: the picture sampled on a 3x3 grid at -1, 0 and +1 screen pixels' footprint around
        /// <paramref name="uv"/> (the camera's distance and field of view over the render height, in metres, over the
        /// picture's extent), each through <see cref="TexelAt"/>; the mean is returned and <paramref name="spread"/> is
        /// the largest share any sample's r+g+b sits from the mean's.
        /// </summary>
        private Color32 UniformTexel(Texture picture, Vector2 uv, out float spread)
        {
            var metresPerPixel = 2f * Mathf.Max(1f, _distance) * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, _rt.height);
            var spanX = Mathf.Max(1f, (float)(_file.MaxX - _file.MinX));
            var spanZ = Mathf.Max(1f, (float)(_file.MaxZ - _file.MinZ));

            var samples = new Color32[9];
            float r = 0f, g = 0f, b = 0f, a = 0f;
            var n = 0;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var at = new Vector2(
                        Mathf.Clamp01(uv.x + dx * metresPerPixel / spanX),
                        Mathf.Clamp01(uv.y + dy * metresPerPixel / spanZ));
                    var s = TexelAt(picture, at);
                    samples[n++] = s;
                    r += s.r; g += s.g; b += s.b; a += s.a;
                }
            }

            var mean = new Color32(
                (byte)Mathf.RoundToInt(r / 9f), (byte)Mathf.RoundToInt(g / 9f),
                (byte)Mathf.RoundToInt(b / 9f), (byte)Mathf.RoundToInt(a / 9f));

            var meanSum = Mathf.Max(1f, (r + g + b) / 9f);
            spread = 0f;
            for (var i = 0; i < samples.Length; i++)
                spread = Mathf.Max(spread, Mathf.Abs(samples[i].r + samples[i].g + samples[i].b - meanSum) / meanSum);

            _frameCheckSpread = spread;
            return mean;
        }

        /// <summary>Set only around the frame check's cover render: Draw leaves the ground out.</summary>
        private bool _withoutGround;

        /// <summary>
        /// Play-test 2026-09-29: the proof on the REAL frame that the tonemap path draws the ground where the calibration
        /// says. After a real render on the tonemap path, the view's own target <see cref="_rt"/> is read at the viewport
        /// centre through the same blit into ARGB32 RenderWindow and the display copy use, and held against the ground
        /// picture there (the mean of <see cref="UniformTexel"/>'s nine samples, judged only where they agree within
        /// <see cref="FrameCheckUniform"/>) taken through the calibrated curve: T(emission scale x texel), per channel. The
        /// emission ground has a black albedo, so nothing but the tonemap moves it. Over by more than
        /// <see cref="FrameCheckTolerance"/> (r+g+b) is TOO BRIGHT and the view falls back to the plain path
        /// (<see cref="TonemapFallback"/>); TOO DARK is logged only (ambient occlusion darkens ground by a wall). The raw
        /// alpha is read too, through an ARGBHalf blit, and the alpha the image gets: above
        /// <see cref="FrameCheckAlphaMax"/> there (Present not applied) is ALPHA, a fallback too. The centre must be bare ground: a render of everything BUT the ground is
        /// compared with an empty one there first; a covered or off-ground centre is tried again on a later rendered
        /// frame, up to <see cref="FrameCheckTries"/>. Self-test "framecheck" halves the prediction, so the check must
        /// say TOO BRIGHT and fall back. Never throws.
        /// </summary>
        private void CheckTonemapFrame()
        {
            var f = CultureInfo.InvariantCulture;

            try
            {
                if (!_tonemapRead || !_emissiveGround || _camera == null || _rt == null)
                {
                    _frameChecked = true;
                    Plugin.LogSource?.LogInfo("QuestTree: 3D map tonemap frame check - " +
                        (!_emissiveGround ? "the ground is not on emission" : "no calibrated curve") + ", skipped.");
                    return;
                }

                var floor = _levels.Count > 0 ? FloorAt(_levels[_levels.Count - 1]) : null;
                var ground = floor?.GroundMaterial;
                var picture = ground != null ? ground.mainTexture : null;

                // the selected floor's picture arrives on a later frame, and that frame's render checks it
                if (floor == null || floor.Meshes == null || picture == null) return;

                string retry = null;
                Color32 texel = default;
                var centre = new RectInt(_rt.width / 2, _rt.height / 2, 1, 1);

                if (!FocusUv(out var uv)) retry = "centre not on the ground";
                else
                {
                    // review: the frame samples the picture mips down (and anisotropic), so one full-size texel is no
                    // reference on varied ground - a 3x3 grid at +-1 screen pixel's footprint, judged only where it is
                    // uniform, and its mean is the reference
                    texel = UniformTexel(picture, uv, out var spread);
                    if (texel.a < EmissionCheckAlphaMin * 255f) retry = "centre on the picture's clipped surround";
                    else if (spread > FrameCheckUniform) retry = "ground at the centre not uniform";
                }

                if (retry == null)
                {
                    // anything but the ground at the centre: a roof, a wall or a tree over it
                    var others = RenderWindow(centre, () =>
                    {
                        _withoutGround = true;
                        try
                        {
                            for (var i = 0; i < _floors.Count; i++) Draw(_floors[i]);
                        }
                        finally
                        {
                            _withoutGround = false;
                        }
                    })[0];
                    var empty = RenderWindow(centre, null)[0];

                    if (!Near(others, empty, EmissionCheckTolerance)) retry = "centre covered by a building or tree";
                }

                if (retry != null)
                {
                    if (++_frameCheckTries < FrameCheckTries) return;

                    _frameChecked = true;
                    Plugin.LogSource?.LogInfo(string.Format(f,
                        "QuestTree: 3D map tonemap frame check - {0} on {1} rendered frame(s), skipped.", retry, _frameCheckTries));
                    return;
                }

                _frameChecked = true;

                // the view's own target, as the image gets it: 8-bit through the blit, and the raw alpha through a half one
                var read = (Color32)ReadThrough(_rt, centre, RenderTextureFormat.ARGB32, TextureFormat.RGBA32);
                var alpha = _halfSupported == true
                    ? ReadThrough(_rt, centre, RenderTextureFormat.ARGBHalf, TextureFormat.RGBAHalf).a
                    : float.NaN;

                var sabotage = SelfTest == "framecheck" ? 0.5f : 1f;
                var er = Mathf.RoundToInt(255f * sabotage * TonemapAt(_emissionScale * texel.r / 255f));
                var eg = Mathf.RoundToInt(255f * sabotage * TonemapAt(_emissionScale * texel.g / 255f));
                var eb = Mathf.RoundToInt(255f * sabotage * TonemapAt(_emissionScale * texel.b / 255f));
                var expected = er + eg + eb;

                string verdict;
                var ratio = 0f;
                if (expected < FrameCheckDarkest) verdict = "picture too dark to judge, skipped";
                else
                {
                    ratio = (read.r + read.g + read.b) / (float)expected;
                    verdict = ratio > 1f + FrameCheckTolerance ? "TOO BRIGHT"
                        : ratio < 1f - FrameCheckTolerance ? "TOO DARK"
                        : "ok";
                }

                // review: rgb cannot see the alpha bug, so the alpha the IMAGE gets is guarded too - the display copy's
                // (clamped by Present), or the raw target's if the image is somehow on it; above 1 the premultiplied UI
                // draw would still scale the frame
                var shownAlpha = _display != null && _image != null && _image.texture == _display
                    ? ReadThrough(_display, centre, RenderTextureFormat.ARGB32, TextureFormat.RGBA32).a
                    : alpha;
                var alphaBad = shownAlpha > FrameCheckAlphaMax;
                if (alphaBad) verdict = "ALPHA";

                var line = string.Format(f,
                    "QuestTree: 3D map tonemap frame check - centre reads {0}/{1}/{2} vs picture {3}/{4}/{5}: {6} " +
                    "(x{7:0.00} of the curve's {8}/{9}/{10} at emission x{11:0.00}, picture the mean of 9 samples within {15:0.0} %; " +
                    "alpha {12:0.00} in the view's target, {16:0.00} in the image{13}{14})",
                    read.r, read.g, read.b, texel.r, texel.g, texel.b, verdict, ratio, er, eg, eb, _emissionScale, alpha,
                    alpha > 1.01f && !alphaBad ? ", clamped by Present" : "",
                    SelfTest == "framecheck" ? "; SELF-TEST: prediction halved" : "",
                    100f * _frameCheckSpread, shownAlpha);

                // review: TOO DARK is logged only - ambient occlusion rightly darkens ground beside a wall
                if (verdict == "TOO BRIGHT" || alphaBad)
                {
                    TonemapFallback("frame check " + verdict);
                    _forceRender = true;

                    // review: the image is on the remade ARGB32 target now, uncleared until the next render - the last
                    // frame copied in so there is no flash
                    if (_display != null && _rt != null && _image != null && _image.texture == _rt) Graphics.Blit(_display, _rt);

                    line += string.Format(f, " - the plain anchor {0:0.00} and the ambient floor are used, post-processing taken off.", WhiteInSun);
                }
                else line += verdict == "TOO DARK" ? " - logged only (ambient occlusion may darken it)." : ".";

                Plugin.LogSource?.LogInfo(line);
            }
            catch (Exception ex)
            {
                _frameChecked = true;
                Plugin.LogSource?.LogInfo(string.Format(f,
                    "QuestTree: 3D map tonemap frame check - could not be made ({0}: {1}).", ex.GetType().Name, ex.Message));
            }
        }

        /// <summary>The ground picture's UV at the view's focus (as Prep.PlanarUv: where it falls across the extent is where
        /// it falls across the picture); false when the focus is not over the ground band.</summary>
        private bool FocusUv(out Vector2 uv)
        {
            uv = default;
            if (_file == null || _groundBand == null || !_groundBand.TryHeightAt(_focus.x, _focus.y, out _)) return false;

            var spanX = (float)(_file.MaxX - _file.MinX);
            var spanZ = (float)(_file.MaxZ - _file.MinZ);
            uv = new Vector2(
                spanX > 0f ? Mathf.Clamp01((_focus.x - (float)_file.MinX) / spanX) : 0.5f,
                spanZ > 0f ? Mathf.Clamp01((_focus.y - (float)_file.MinZ) / spanZ) : 0.5f);
            return true;
        }

        /// <summary>One pixel of <paramref name="source"/> blitted whole into a one-sample temporary of
        /// <paramref name="through"/> (the blit resolves MSAA, and an ARGB32 one clamps to 0..1 as RenderWindow's does)
        /// and read back as <paramref name="readAs"/>.</summary>
        private static Color ReadThrough(RenderTexture source, RectInt pixel, RenderTextureFormat through, TextureFormat readAs)
        {
            var previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D readable = null;

            try
            {
                target = RenderTexture.GetTemporary(source.width, source.height, 0, through);
                Graphics.Blit(source, target);

                RenderTexture.active = target;
                readable = new Texture2D(1, 1, readAs, false);
                readable.ReadPixels(new Rect(pixel.x, pixel.y, 1f, 1f), 0, 0, false);

                return readable.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) Destroy(readable);
            }
        }

        /// <summary>What the opaque side check found this session: sides stay lit for the rest of the session after a
        /// MISMATCH, and are checked once more per build until one passes.</summary>
        private enum SideEmission
        {
            Unproven,
            Proven,
            Failed
        }

        private static SideEmission _sideEmission = SideEmission.Unproven;

        /// <summary>Sets <see cref="_emissiveGround"/>, <see cref="_emissiveSides"/> and, when the ground's is false,
        /// <see cref="_emissiveWhy"/>. Reads the probe's cached result - it never runs the probe. The shader is looked at
        /// before the probe: a Legacy or Unlit ground has no emission whatever the probe found.</summary>
        private void DecideEmission()
        {
            _emissiveGround = false;
            _emissiveWhy = "";

            var probe = Map3DLightProbe.Last;

            if (!EmissiveGround) _emissiveWhy = "switched off";
            else if (_groundShader == null || _groundShader.name != "Standard") _emissiveWhy = "legacy shader";
            else if (probe == null) _emissiveWhy = "probe not run";
            else if (!probe.Emission)
            {
                // An emission check that never ran (the probe could not start, or threw first) leaves the read at its default,
                // alpha included - the target is cleared opaque, so a real read has alpha. Then the probe's reason says more.
                var read = probe.EmissionRead;
                var unread = read.r == 0 && read.g == 0 && read.b == 0 && read.a == 0;

                _emissiveWhy = "probe: " + (unread && !string.IsNullOrEmpty(probe.Why)
                    ? probe.Why
                    : string.Format(CultureInfo.InvariantCulture, "emission read {0}/{1}/{2}", read.r, read.g, read.b));
            }
            else _emissiveGround = true;

            _emissiveSides = _emissiveGround && _sideEmission != SideEmission.Failed;
        }

        /// <summary>
        /// Spot-sun stage B: the emission recipe Map3DLightProbe's emission check proved (Build, the rig's Emissive), on a
        /// material already Matte and, for the ground, already cut out. A BLACK albedo with alpha 1 - Standard's alpha is
        /// _Color.a x _MainTex.a, so the cutout still clips on the picture's own alpha while the light, the ambient and the
        /// shadows add nothing to it; the picture as the emission map at <see cref="_emissionScale"/>; the _EMISSION keyword
        /// that compiles the emission in; no GI flags (there is no lightmapper to feed). _EmissionMap is set from the current
        /// _MainTex here and again wherever Draw puts a picture on the material (<see cref="SetPicture"/>).
        /// </summary>
        /// <param name="material">The material, returned.</param>
        private Material Emissive(Material material)
        {
            return EmissiveCore(material);
        }

        /// <summary>
        /// Review 2026-09-29: the emission recipe for the roofs, the side pictures and the tonemap calibration's quads, on
        /// the CUTOUT variant (<see cref="MakeCutout"/> + <see cref="Emissive"/>, _ALPHATEST_ON with _EMISSION) - the one
        /// the light probe and the ground proved - instead of the opaque _EMISSION variant nothing proved, which the
        /// calibration's all-black read says the game build probably lacks. _Cutoff 0, so nothing is ever clipped: the
        /// roofs sample the ground picture, whose alpha is the reach mask, and a side picture has no mask. Shadows are
        /// unchanged: the cutout ShadowCaster clips at the same 0 (alpha x _Color.a 1 is never under it). The queue is
        /// AlphaTest, drawn after the opaque geometry with depth written, like the ground.
        /// </summary>
        /// <param name="material">The material, returned.</param>
        private Material EmissiveNoClip(Material material)
        {
            MakeCutout(material);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0f);

            return EmissiveCore(material);
        }

        /// <summary>The body of <see cref="Emissive"/>, shared with <see cref="EmissiveNoClip"/>.</summary>
        private Material EmissiveCore(Material material)
        {
            if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0f, 0f, 0f, 1f));

            // Play-test 2026-09-29 (emission check MISMATCH, centre 216/207/193 against the picture's 188/181/172): the black
            // albedo stops the diffuse, but NOT the specular. A dielectric (Matte's metallic 0) keeps Standard's fixed 4 %
            // specular colour whatever the albedo, and the _SPECULARHIGHLIGHTS_OFF keyword Matte asks for is a
            // shader_feature the game's build strips when no shipped material used it - so the sun's highlight came back
            // (the excess, 28/26/21, is the sun's own colour 0.98/0.91/0.76). Metallic 1 makes the specular colour the
            // albedo, black: BRDF1 then zeroes the direct specular outright (specularTerm *= any(specColor)) and the
            // diffuse share with it, through a uniform no build can strip. The emission is untouched by either.
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 1f);
            if (material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", material.mainTexture);
            // alpha 1, as MatchEmissionScale compares it (white x scale would scale the alpha too)
            if (material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", new Color(_emissionScale, _emissionScale, _emissionScale, 1f));

            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;

            return material;
        }

        /// <summary>A side material made again as the lit path makes it (Matte, the buildings' shader, white), carrying the
        /// old one's picture; the old one is destroyed. For a side whose emission variant failed its check.</summary>
        /// <param name="old">The emissive side material.</param>
        private Material LitSideMaterial(Material old)
        {
            var material = Matte(new Material(_buildingShader) { name = old.name });
            material.mainTexture = old.mainTexture;
            Destroy(old);

            return material;
        }

        /// <summary>Puts a picture on a picture material: _MainTex always (the cutout's alpha, and the lit path's colour),
        /// and _EmissionMap too when that material is drawn as emission - an emission map left on an evicted texture would
        /// draw black under a _MainTex that had been put back.</summary>
        /// <param name="material">The ground's or a side's material.</param>
        /// <param name="picture">The picture.</param>
        /// <param name="emissive">Whether the material is on the emission path (<see cref="_emissiveGround"/> or
        /// <see cref="_emissiveSides"/>).</param>
        private static void SetPicture(Material material, Texture picture, bool emissive)
        {
            material.mainTexture = picture;
            if (emissive && material.HasProperty("_EmissionMap")) material.SetTexture("_EmissionMap", picture);
        }

        /// <summary>The first-frame line's ground clause: "emission x1.00", or the division path with its reason and colour.</summary>
        private string GroundText() =>
            _emissiveGround
                ? string.Format(CultureInfo.InvariantCulture, "emission x{0:0.00}{1}", _emissionScale,
                    SelfTest == "ground" ? " (SELF-TEST: _EMISSION left off)" : "")
                : string.Format(CultureInfo.InvariantCulture, "lit fallback ({0}) {1:0.00}/{2:0.00}/{3:0.00}",
                    _emissiveWhy, _groundColour.r, _groundColour.g, _groundColour.b);

        /// <summary>The first-frame line's sides clause: "emission" once the opaque check has passed this session, "emission
        /// pending check" before this build's check has run (the first frame is logged before it), "emission unproven" after
        /// a check that skipped, "lit" off the emission path (with the reason when the check failed), "none" when the side
        /// pictures take no part.</summary>
        private string SidesText() =>
            !SidesActive ? "none"
            : _emissiveSides
                ? (_sideEmission == SideEmission.Proven ? "emission" : !_sideChecked ? "emission pending check" : "emission unproven")
            : _sideEmission == SideEmission.Failed ? "lit (the emission check failed)"
            : "lit";

        /// <summary>Whether this build's ground check (<see cref="CheckPictureEmission"/>) has been made - or given up on -
        /// already. Reset per build in BeginBuild.</summary>
        private bool _emissionChecked;

        /// <summary>As <see cref="_emissionChecked"/>, for the side check.</summary>
        private bool _sideChecked;

        /// <summary>The emission check's tolerance per channel, in 8-bit steps.</summary>
        private const int EmissionCheckTolerance = 6;

        /// <summary>How far the Unlit reference may sit from the picture's full-size texel, per channel, when neither fog nor
        /// post-processing is drawn: further means the reference is not showing the picture, and a comparison with it
        /// proves nothing. Wider than the check's own tolerance, because the drawn mip averages neighbouring texels.</summary>
        private const int ReferenceTexelTolerance = 12;

        /// <summary>The picture's alpha (at full size, at the centre) under which the centre is taken as clipped or on the
        /// clip's edge and the check skipped: the drawn mip can average in the transparent surround, so not 0.5.</summary>
        private const float EmissionCheckAlphaMin = 0.75f;

        /// <summary>How far, in pixels, the side check looks from a side mesh's projected bounds centre for a pixel the side
        /// actually covers - a chunk of many buildings has its bounds centre in the air between them.</summary>
        private const int SideSearchRadius = 48;

        /// <summary>
        /// Spot-sun stage B's proof, after a render with the pictures as emission. Every render goes through RenderNow, the
        /// whole bracket, into a temporary target of the view's size and samples, resolved to one sample before it is read
        /// (<see cref="RenderFrame"/>), with nothing queued but what is being tested - so no building stands in front, and
        /// the view's own texture, the real frame, is never touched.
        ///
        /// GROUND (the cutout emission variant), once per build: the pixel at the viewport's centre - where the camera
        /// looks at the focus on the selected floor's ground (Place) - with the ground material, and with Unlit/Texture
        /// carrying the same picture. Fog, post-processing, mip and filtering are the same for both, so the only difference
        /// left is what Standard adds to or takes from the picture: an emission not compiled in reads black, a light, an
        /// ambient or a specular veil on the black albedo reads brighter - a MISMATCH beyond
        /// <see cref="EmissionCheckTolerance"/>. So that two identical WRONG reads cannot pass (review S2): the reference
        /// must differ from the backdrop (an empty render), and with no fog or post-processing it must match the picture's
        /// own texel within <see cref="ReferenceTexelTolerance"/>; either failing is a skip, said so, never an ok.
        ///
        /// SIDES (their EmissiveNoClip material - the cutout variant at cutoff 0, as the roofs; review 2026-09-29), once per
        /// build until it passes:
        /// one side mesh of the selected floor whose bounds centre is on screen, drawn with its material and with the
        /// Unlit reference; the pixel nearest that centre which the reference covers (differs from the backdrop) is
        /// compared. A pass marks the sides proven for the session; a MISMATCH puts them on the lit path for the session
        /// (Draw rebuilds each side material without the emission) and forces a render.
        ///
        /// Waited for while the picture a part needs is not on its material yet; otherwise skipped with the reason. Never
        /// throws out of the render.
        /// </summary>
        private void CheckPictureEmission()
        {
            if (_camera == null || _rt == null || !_emissiveGround) return;

            var floor = _levels.Count > 0 ? FloorAt(_levels[_levels.Count - 1]) : null;
            if (floor == null || floor.Meshes == null) return;

            Material unlitReference = null;

            try
            {
                var unlit = Shader.Find("Unlit/Texture");
                var usable = unlit != null && unlit.isSupported;

                if (!_emissionChecked)
                {
                    var ground = floor.GroundMaterial;
                    var picture = ground != null ? ground.mainTexture : null;

                    // the selected floor's picture arrives on a later frame, and that frame's render checks it
                    if (picture != null)
                    {
                        _emissionChecked = true;

                        string verdict;
                        try
                        {
                            verdict = usable
                                ? GroundVerdict(floor, ground, picture, unlit, ref unlitReference)
                                : "no Unlit/Texture to compare with, skipped";
                        }
                        catch (Exception ex)
                        {
                            verdict = "could not be made (" + ex.GetType().Name + ": " + ex.Message + ")";
                        }

                        Plugin.LogSource?.LogInfo("QuestTree: 3D map emission check - " + verdict);
                    }
                }

                if (!_sideChecked)
                {
                    string verdict;
                    try
                    {
                        // the sides' and roofs' material (cutout variant, cutoff 0) is proven on a side picture, or - a
                        // capture without sides - on the roofs (stage C), which draw with the same recipe
                        var roofsToProve = !SidesActive && floor.RoofMaterial != null &&
                                           floor.RoofMaterial != floor.BuildingMaterial &&
                                           floor.RoofMaterial.IsKeywordEnabled("_EMISSION");

                        verdict = !(SidesActive || roofsToProve) || !_emissiveSides || _sideEmission != SideEmission.Unproven
                            ? ""   // nothing on the emission path to prove, or proven (or failed) already this session
                            : !usable
                                ? "no Unlit/Texture to compare with, skipped"
                                : SidesActive
                                    ? SideVerdict(floor, unlit, ref unlitReference)
                                    : RoofVerdict(floor, unlit, ref unlitReference);
                    }
                    catch (Exception ex)
                    {
                        verdict = "could not be made (" + ex.GetType().Name + ": " + ex.Message + ")";
                    }

                    // null: no side picture here yet - a later render tries again
                    if (verdict != null)
                    {
                        _sideChecked = true;
                        if (verdict.Length > 0) Plugin.LogSource?.LogInfo("QuestTree: 3D map side emission check - " + verdict);
                    }
                }
            }
            catch (Exception ex)
            {
                // once: a check that throws is not retried every frame
                _emissionChecked = true;
                _sideChecked = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: 3D map emission check - could not be made ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                if (unlitReference != null) Destroy(unlitReference);
            }
        }

        /// <summary>The ground part of <see cref="CheckPictureEmission"/>: its log verdict. Three renders, each read back as
        /// the one centre pixel.</summary>
        private string GroundVerdict(Floor floor, Material ground, Texture picture, Shader unlit, ref Material unlitReference)
        {
            if (_file == null || _groundBand == null || !_groundBand.TryHeightAt(_focus.x, _focus.y, out _))
                return "centre not on the ground, skipped";

            // the ground's planar UV of the focus (as Prep.PlanarUv): where it falls across the extent is where it falls
            // across the picture
            var spanX = (float)(_file.MaxX - _file.MinX);
            var spanZ = (float)(_file.MaxZ - _file.MinZ);
            var uv = new Vector2(
                spanX > 0f ? Mathf.Clamp01((_focus.x - (float)_file.MinX) / spanX) : 0.5f,
                spanZ > 0f ? Mathf.Clamp01((_focus.y - (float)_file.MinZ) / spanZ) : 0.5f);

            var texel = TexelAt(picture, uv);

            if (texel.a < EmissionCheckAlphaMin * 255f)
                return string.Format(CultureInfo.InvariantCulture, "centre on the picture's clipped surround (alpha {0}), skipped", texel.a);

            var centre = new RectInt(_rt.width / 2, _rt.height / 2, 1, 1);

            var read = RenderAtUnitEmission(floor.Meshes.Ground, ground, true, centre)[0];

            unlitReference = ReferenceFor(unlitReference, unlit, picture);
            var expected = RenderFrame(floor.Meshes.Ground, unlitReference, true, centre)[0];

            var empty = RenderFrame(null, null, true, centre)[0];

            if (Near(expected, empty, EmissionCheckTolerance))
                return string.Format(CultureInfo.InvariantCulture, "reference is the backdrop ({0}/{1}/{2}), skipped",
                    expected.r, expected.g, expected.b);

            // Only a render with neither fog nor grading can be held against the raw texel; _fogDrawn is the last render's
            if (!_fogDrawn && !_postProcessAttached && !Near(expected, texel, ReferenceTexelTolerance))
                return string.Format(CultureInfo.InvariantCulture,
                    "reference {0}/{1}/{2} does not match the picture {3}/{4}/{5}, skipped",
                    expected.r, expected.g, expected.b, texel.r, texel.g, texel.b);

            return string.Format(CultureInfo.InvariantCulture, "centre reads {0}/{1}/{2} vs picture {3}/{4}/{5}: {6}",
                read.r, read.g, read.b, expected.r, expected.g, expected.b,
                Near(read, expected, EmissionCheckTolerance) ? "ok" : "MISMATCH");
        }

        /// <summary>The most side meshes the side check tries before it gives up for this build: each try whose window has
        /// no covered pixel costs two renders.</summary>
        private const int SideMeshTries = 8;

        /// <summary>
        /// The side part of <see cref="CheckPictureEmission"/>: its log verdict, or null to try again on a later render (no
        /// side of the selected floor has its picture yet). Sets <see cref="_sideEmission"/> on a comparison. Each on-screen
        /// side mesh, up to <see cref="SideMeshTries"/>, is drawn with the Unlit reference and against an empty frame, both
        /// read back only in the window of <see cref="SideSearchRadius"/> around its projected bounds centre; the first with
        /// a covered pixel is then drawn with its emission material, and that pixel compared.
        /// </summary>
        private string SideVerdict(Floor floor, Shader unlit, ref Material unlitReference)
        {
            var anyPicture = false;
            var tries = 0;
            var dark = 0;

            for (var slot = 0; slot < floor.Meshes.Sides.Length && tries < SideMeshTries; slot++)
            {
                var side = floor.Meshes.Sides[slot];
                var material = side?.Material;
                var picture = material != null ? material.mainTexture : null;
                if (picture == null || !material.IsKeywordEnabled("_EMISSION")) continue;

                anyPicture = true;

                for (var i = 0; i < side.Meshes.Count && tries < SideMeshTries; i++)
                {
                    var mesh = side.Meshes[i];
                    if (mesh == null) continue;

                    var result = ReadOpaque(mesh, material, picture, unlit, ref unlitReference, out var read, out var expected);
                    if (result < 0) continue;   // its centre is off screen: not a try

                    tries++;
                    if (result == 0) continue;   // this chunk's centre is in the air between its buildings - the next one

                    if (result == OpaqueTooDark)
                    {
                        dark++;
                        continue;
                    }

                    var ok = SettleOpaque(read, expected);

                    return string.Format(CultureInfo.InvariantCulture, "side {0} reads {1}/{2}/{3} vs picture {4}/{5}/{6}: {7}",
                        SideOrder[slot], read.r, read.g, read.b, expected.r, expected.g, expected.b,
                        ok ? "ok" : "MISMATCH, the sides and roofs are drawn lit");
                }
            }

            // left Unproven: the next build checks again, and a pass is never made of a picture black either way
            if (dark > 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} of {1} side mesh(es) too dark to judge, skipped (too dark to judge)", dark, tries);

            if (tries > 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "nothing drawn within {0} px of the centres of {1} side mesh(es), skipped", SideSearchRadius, tries);

            return anyPicture ? "no side mesh's centre on screen, skipped" : null;
        }

        /// <summary>
        /// Pictures stage C review S1: the check of <see cref="SideVerdict"/> made on the ROOFS, for a capture without side
        /// pictures - the roofs' emissive material is the same recipe (<see cref="EmissiveNoClip"/>: the cutout Standard
        /// _EMISSION variant at cutoff 0, review 2026-09-29), and nothing else would prove it with a no-clip cutoff. The selected floor's roof meshes, up to <see cref="SideMeshTries"/>, the same way. Its log
        /// verdict, "" when there is nothing to prove, or null to try again on a later render (the roofs' picture is not
        /// on their material yet). A MISMATCH puts that recipe on the lit path for the session: Draw then gives
        /// the roofs the lit building material (<see cref="RoofMaterialOf"/>).
        /// </summary>
        private string RoofVerdict(Floor floor, Shader unlit, ref Material unlitReference)
        {
            var material = floor.RoofMaterial;
            if (material == null || material == floor.BuildingMaterial || !material.IsKeywordEnabled("_EMISSION")) return "";

            var picture = material.mainTexture;
            if (picture == null) return null;

            var tries = 0;
            var dark = 0;

            for (var i = 0; i < floor.Meshes.Buildings.Count && tries < SideMeshTries; i++)
            {
                var mesh = floor.Meshes.Buildings[i];
                if (mesh == null) continue;

                var result = ReadOpaque(mesh, material, picture, unlit, ref unlitReference, out var read, out var expected);
                if (result < 0) continue;

                tries++;
                if (result == 0) continue;

                if (result == OpaqueTooDark)
                {
                    dark++;
                    continue;
                }

                var ok = SettleOpaque(read, expected);

                return string.Format(CultureInfo.InvariantCulture, "roofs read {0}/{1}/{2} vs picture {3}/{4}/{5}: {6}",
                    read.r, read.g, read.b, expected.r, expected.g, expected.b,
                    ok ? "ok" : "MISMATCH, the roofs and sides are drawn lit");
            }

            // left Unproven: the next build checks again, and a pass is never made of a picture black either way
            if (dark > 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} of {1} roof mesh(es) too dark to judge, skipped (too dark to judge)", dark, tries);

            if (tries > 0)
                return string.Format(CultureInfo.InvariantCulture,
                    "nothing drawn within {0} px of the centres of {1} roof mesh(es), skipped", SideSearchRadius, tries);

            return "no roof mesh's centre on screen, skipped";
        }

        /// <summary>
        /// One mesh of the side/roof check (the EmissiveNoClip material - named "opaque" from before the 2026-09-29 review,
        /// when it was the opaque variant): drawn with the Unlit reference and against an empty frame, both read back only in
        /// the window of <see cref="SideSearchRadius"/> around its projected bounds centre; when a pixel there is covered,
        /// drawn with <paramref name="material"/> at unit emission and that pixel returned in both reads. -1 when the
        /// centre is off screen (not a try), 0 when nothing near it is covered, 1 when compared,
        /// <see cref="OpaqueTooDark"/> when the covered pixel's picture is too dark to tell a broken emission from a
        /// working one (a broken variant draws the black albedo, which reads about black too).
        /// </summary>
        private int ReadOpaque(Mesh mesh, Material material, Texture picture, Shader unlit, ref Material unlitReference,
            out Color32 read, out Color32 expected)
        {
            read = expected = default;

            var width = _rt.width;
            var height = _rt.height;

            // world bounds: a compact mesh's own are in lattice units, and its centre there is nowhere near the map
            var at = _camera.WorldToViewportPoint(WorldBoundsOf(mesh).center);
            if (at.z <= 0f || at.x < 0f || at.x > 1f || at.y < 0f || at.y > 1f) return -1;

            // the window around the projected centre, clipped to the view
            var px = Mathf.RoundToInt(at.x * (width - 1));
            var py = Mathf.RoundToInt(at.y * (height - 1));
            var x0 = Mathf.Max(0, px - SideSearchRadius);
            var y0 = Mathf.Max(0, py - SideSearchRadius);
            var x1 = Mathf.Min(width - 1, px + SideSearchRadius);
            var y1 = Mathf.Min(height - 1, py + SideSearchRadius);
            var window = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);

            var one = new List<Mesh>(1) { mesh };

            // one reference material for the whole check: its picture is whichever part used it last
            unlitReference = ReferenceFor(unlitReference, unlit, picture);
            var reference = RenderFrame(one, unlitReference, false, window);
            var backdrop = RenderFrame(null, null, false, window);

            var pixel = CoveredNear(reference, backdrop, window.width, window.height, px - x0, py - y0);
            if (pixel < 0) return 0;

            expected = reference[pixel];

            // a near-black texel would pass whatever the emission does: not evidence, so no emission render either
            if (Math.Max(expected.r, Math.Max(expected.g, expected.b)) <= OpaqueDarkMax) return OpaqueTooDark;

            read = RenderAtUnitEmission(one, material, false, window)[pixel];

            return 1;
        }

        /// <summary><see cref="ReadOpaque"/>'s result for a covered pixel too dark to judge.</summary>
        private const int OpaqueTooDark = 2;

        /// <summary>The brightest a reference pixel's max channel can be and still be too dark for the opaque check: three
        /// tolerances, so a broken emission (about black) cannot land within the tolerance of it.</summary>
        private const int OpaqueDarkMax = 3 * EmissionCheckTolerance;

        /// <summary>Records the opaque check's outcome for the session and returns whether it passed. A MISMATCH puts the
        /// sides and the roofs on the lit path for the rest of the session: Draw rebuilds each emissive side material lit
        /// and gives the roofs the building material, from the next render.</summary>
        private bool SettleOpaque(Color32 read, Color32 expected)
        {
            var ok = Near(read, expected, EmissionCheckTolerance);
            _sideEmission = ok ? SideEmission.Proven : SideEmission.Failed;

            if (!ok)
            {
                _emissiveSides = false;
                _forceRender = true;
            }

            return ok;
        }

        /// <summary>Stage D: <see cref="RenderFrame"/> with the material's _EmissionColor at white for that one render and put
        /// back after. The Unlit reference draws the picture at 1; on the tonemap path the pictures' emission is scaled
        /// (<see cref="_emissionScale"/>), and the check proves the emission is compiled in and nothing lights it, not the
        /// scale - both go through the same tonemap, so at 1 they must still agree.</summary>
        private Color32[] RenderAtUnitEmission(List<Mesh> meshes, Material material, bool ground, RectInt window)
        {
            var has = material.HasProperty(EmissionColorId);
            var was = has ? material.GetColor(EmissionColorId) : Color.white;

            try
            {
                if (has) material.SetColor(EmissionColorId, Color.white);
                return RenderFrame(meshes, material, ground, window);
            }
            finally
            {
                if (has) material.SetColor(EmissionColorId, was);
            }
        }

        /// <summary>The Unlit/Texture reference material with <paramref name="picture"/> on it, made on first use.</summary>
        private static Material ReferenceFor(Material reference, Shader unlit, Texture picture)
        {
            if (reference == null) reference = new Material(unlit) { name = "QuestTreeMap3D-emission-check" };
            reference.mainTexture = picture;

            return reference;
        }

        /// <summary>Whether two colours agree within <paramref name="tolerance"/> on each of r, g and b.</summary>
        private static bool Near(Color32 a, Color32 b, int tolerance) =>
            Math.Abs(a.r - b.r) <= tolerance && Math.Abs(a.g - b.g) <= tolerance && Math.Abs(a.b - b.b) <= tolerance;

        /// <summary>The index, in a window's pixels (<paramref name="width"/> x <paramref name="height"/>), of the pixel
        /// nearest (x, y) - window coordinates - where <paramref name="drawn"/> differs from <paramref name="backdrop"/>:
        /// a pixel the mesh covers; -1 when none does.</summary>
        private static int CoveredNear(Color32[] drawn, Color32[] backdrop, int width, int height, int x, int y)
        {
            var best = -1;
            var bestDistance = int.MaxValue;

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    var distance = (px - x) * (px - x) + (py - y) * (py - y);
                    if (distance >= bestDistance) continue;

                    var index = py * width + px;
                    if (Near(drawn[index], backdrop[index], EmissionCheckTolerance)) continue;

                    best = index;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// <paramref name="meshes"/> alone (none: an empty frame, the backdrop), with <paramref name="material"/>, rendered
        /// through <see cref="RenderNow"/> into a temporary target of the view's own size and samples (so the projection, the
        /// mip and the coverage are the view's), then BLITTED into a one-sample target and read back from that - review S2:
        /// nothing else in the repo reads a multisampled target directly, and a direct read may come back black. Only
        /// <paramref name="window"/> is read back (a pixel, or the side check's square), row by row from its lower left, as
        /// a whole frame would be ~15 MB at 1440p. The ground is submitted as Draw submits it (no casting), anything else
        /// casting and receiving. The camera's target is put back and both temporaries released whatever throws.
        /// </summary>
        private Color32[] RenderFrame(List<Mesh> meshes, Material material, bool ground, RectInt window) =>
            RenderWindow(window, meshes != null && material != null ? () => SubmitAll(meshes, material, ground) : (Action)null);

        /// <summary>The meshes <see cref="RenderFrame"/> draws: the ground as Draw submits it (no casting), anything else
        /// casting and receiving.</summary>
        private void SubmitAll(List<Mesh> meshes, Material material, bool ground)
        {
            // opt B: a check draws exactly the meshes it names - no small-prop skip, no shadow rule
            _plainSubmit = true;

            try
            {
                for (var i = 0; i < meshes.Count; i++)
                {
                    var mesh = meshes[i];
                    if (mesh == null) continue;

                    if (ground) Submit(mesh, material, castShadows: false);
                    else Submit(mesh, material);
                }
            }
            finally
            {
                _plainSubmit = false;
            }
        }

        /// <summary>
        /// The body of <see cref="RenderFrame"/>, with what is drawn left to <paramref name="submit"/> (null: an empty
        /// frame): the DrawMesh calls it makes, then RenderNow into a temporary of the view's own descriptor (on the tonemap
        /// path an MSAA ARGBHalf, so the render is the view's HDR one), blitted into a one-sample ARGB32 - the blit resolves
        /// the samples and brings the half floats to 8 bits (the tonemap's output is already 0..1) - and read back there.
        /// Stage D's calibration draws its own quads through it.
        /// </summary>
        private Color32[] RenderWindow(RectInt window, Action submit)
        {
            var target = _camera.targetTexture;
            var previous = RenderTexture.active;
            RenderTexture temporary = null;
            RenderTexture resolved = null;
            Texture2D readable = null;

            try
            {
                temporary = RenderTexture.GetTemporary(_rt.descriptor);
                _camera.targetTexture = temporary;

                submit?.Invoke();

                RenderNow();

                resolved = RenderTexture.GetTemporary(temporary.width, temporary.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(temporary, resolved);

                RenderTexture.active = resolved;
                readable = new Texture2D(window.width, window.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(window.x, window.y, window.width, window.height), 0, 0, false);

                return readable.GetPixels32();
            }
            finally
            {
                try { RenderTexture.active = previous; } catch (Exception) { /* nothing further to try */ }
                try { if (_camera != null) _camera.targetTexture = target; } catch (Exception) { /* as above */ }
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                if (resolved != null) RenderTexture.ReleaseTemporary(resolved);
                if (readable != null) Destroy(readable);
            }
        }

        /// <summary>The picture's full-size texel at <paramref name="uv"/>: a blit whose every pixel samples that one point
        /// (scale 0, offset uv) into a 1x1 target, read back - the picture itself is not CPU-readable (as ReadPalette).</summary>
        private static Color32 TexelAt(Texture source, Vector2 uv)
        {
            var previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D readable = null;

            try
            {
                target = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, target, Vector2.zero, uv);

                RenderTexture.active = target;
                readable = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0, false);

                return readable.GetPixels32()[0];
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) Destroy(readable);
            }
        }

        /// <summary>The Standard cutout recipe on a material (see <see cref="MakeGroundMaterial"/>): the render type tag,
        /// the mode, the clip at 0.5, opaque blending with depth written, _ALPHATEST_ON, the AlphaTest queue. HQ S3.11: shared
        /// with the alpha-page tile materials.</summary>
        /// <param name="material">The material, returned.</param>
        internal static Material MakeCutout(Material material)
        {
            material.SetOverrideTag("RenderType", "TransparentCutout");

            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 1f);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0.5f);

            if (material.HasProperty("_SrcBlend"))
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);

            if (material.HasProperty("_DstBlend"))
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);

            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 1);

            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            return material;
        }

        /// <summary>Matte, not plastic. Standard's defaults are a smooth dielectric, which turns a
        /// hillside into a mirror of the one light in the scene.</summary>
        /// <param name="material">The material to dull.</param>
        internal static Material Matte(Material material)
        {
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);

            // Test 2026-09-28 (review): at roughness 1 the Standard shader still draws a broad specular highlight and its
            // glossy reflection, which over a large ground plane at a grazing angle reads as a far-away sun. Both off (the
            // keywords are shader features; a build without the variant falls back to the default one, silently).
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", 0f);
            if (material.HasProperty("_GlossyReflections")) material.SetFloat("_GlossyReflections", 0f);

            return material;
        }

        /// <summary>The floor ranges as text, for the cache key: the roof routing depends on them, and they
        /// come from the meta, which a rescan can change while the mesh file stays the same.</summary>
        private string FloorRangesKey()
        {
            var text = "";

            for (var i = 0; i < _floorRanges.Count; i++)
            {
                var r = _floorRanges[i];
                text += string.Format(CultureInfo.InvariantCulture, "{0}:{1:0.##}-{2:0.##};", r.Level, r.Low, r.High);
            }

            return text;
        }

        /// <summary>The height range each floor's own surfaces are found in, from the meta: [minY - slack,
        /// maxY + slack] per band the mesh file has. Set by <see cref="BeginBuild"/> before any floor is
        /// built; the same for every selection, so the routing it drives is safe to cache.</summary>
        private readonly List<(int Level, float Low, float High)> _floorRanges = new List<(int Level, float Low, float High)>();

        /// <summary>How far outside its declared height band a face may be and still be ON that floor: the
        /// half metre the harvest's bands and the relief's own floor test already allow.</summary>
        private const float FloorFaceSlack = 0.5f;

        /// <summary>Fills <see cref="_floorRanges"/> from the entry's layers, for the bands the file has. A
        /// band with the catalog's "any height" placeholder (+-2000 m) is left out - a range that claims
        /// every face would take every roof.</summary>
        private void MeasureFloorRanges()
        {
            _floorRanges.Clear();

            foreach (var band in _file.Bands)
            {
                if (band == null) continue;

                var layer = LayerOf(band.Level);
                if (layer == null || layer.GameBounds.Count == 0) continue;

                var low = layer.GameBounds[0].Min.z;
                var high = layer.GameBounds[0].Max.z;

                if (!(low > -1000f) || !(high < 1000f) || high < low) continue;

                _floorRanges.Add((band.Level, low - FloorFaceSlack, high + FloorFaceSlack));
            }
        }

        /// <summary>This view's floor with the given level, or null. At most six floors, so a loop.</summary>
        private Floor FloorAt(int level)
        {
            for (var i = 0; i < _floors.Count; i++)
                if (_floors[i] != null && _floors[i].Level == level) return _floors[i];

            return null;
        }

        // --- the walls -------------------------------------------------------------------------------

        /// <summary>A wall's colour when the picture has nothing to say: a transparent pixel under the
        /// building, a readback that failed, or no picture at all. Warm grey, which reads as rendered
        /// concrete and is the commonest wall there is.</summary>
        private static readonly Color FallbackWallColour = new Color(0.55f, 0.53f, 0.50f, 1f);

        /// <summary>The walls' vertex colour under the flat-colour shader: the buildings' own flat colour
        /// a quarter darker, so walls still read as walls against their roofs.</summary>
        private static readonly Color32 FlatWallColour = new Color32(126, 123, 117, 255);

        /// <summary>The most colours one floor's walls are drawn in. See <see cref="WallTint"/>.</summary>
        private const int MaxWallTints = 16;

        /// <summary>How wide the readable copy of a floor's picture is. The tint is an average over a few
        /// metres of roof, so 256 columns across a kilometre - 4 m to the pixel - is all the detail the
        /// question needs, and it reads back in well under a millisecond.</summary>
        private const int PaletteWidth = 256;

        /// <summary>How much of the roof's brightness a wall keeps: three quarters.</summary>
        private const float WallValue = 0.75f;

        /// <summary>How much of the roof's saturation a wall keeps.</summary>
        private const float WallSaturation = 0.85f;

        /// <summary>
        /// A small READABLE copy of a floor's picture, as pixels.
        ///
        /// The picture itself cannot be read: DynamicMapsLibrary decodes it with markNonReadable, which
        /// frees the CPU copy - the right call for a 39 MiB texture that is only ever drawn. So the GPU
        /// copies it down into a temporary render texture, and ReadPixels brings that back. Once per floor
        /// per built entry (the entry is cached, so once per session per floor in practice), and nothing
        /// of it outlives the call: the render texture goes back to the pool and the readable texture is
        /// destroyed in the finally, so there is no Unity object here for a teardown to miss.
        /// </summary>
        /// <param name="source">The floor's picture.</param>
        /// <param name="width">The copy's width, <see cref="PaletteWidth"/>.</param>
        /// <param name="height">The copy's height, keeping the picture's aspect.</param>
        private static Color32[] ReadPalette(Texture source, out int width, out int height)
        {
            width = PaletteWidth;
            height = source.width > 0
                ? Mathf.Clamp(Mathf.RoundToInt(PaletteWidth * (float)source.height / source.width), 1, 1024)
                : PaletteWidth;

            var previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D readable = null;

            try
            {
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, target);

                RenderTexture.active = target;

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);

                // GetPixels32 reads the CPU copy ReadPixels just wrote - no Apply, which would only upload
                // it back to the GPU for nothing.
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) Destroy(readable);
            }
        }

        /// <summary>
        /// Groups the buildings' colours into at most <paramref name="max"/> buckets, deterministically.
        ///
        /// Each colour falls in a cell of a 4x4x4 grid over RGB; the most populous cells become the
        /// buckets, coloured by the mean of their members, and a colour in any other cell goes to the
        /// nearest bucket. Deterministic (ties by cell number), so the same file gives the same walls on
        /// every open - a k-means seeded at random would repaint a building between one click and the
        /// next.
        /// </summary>
        /// <param name="colours">One colour per walled building.</param>
        /// <param name="max">The most buckets.</param>
        /// <param name="centres">Filled with each bucket's colour.</param>
        /// <returns>The bucket of each colour, in step with <paramref name="colours"/>.</returns>
        internal static int[] BucketColours(List<Color> colours, int max, List<Color> centres)
        {
            centres.Clear();
            var bucketOf = new int[colours.Count];
            if (colours.Count == 0 || max <= 0) return bucketOf;

            var cells = new Dictionary<int, (Color Sum, int Count)>();
            var cellOf = new int[colours.Count];

            for (var i = 0; i < colours.Count; i++)
            {
                var key = Cell(colours[i]);
                cellOf[i] = key;

                cells.TryGetValue(key, out var held);
                cells[key] = (held.Sum + colours[i], held.Count + 1);
            }

            var chosen = new List<int>(cells.Keys);
            chosen.Sort((x, y) =>
            {
                var byCount = cells[y].Count.CompareTo(cells[x].Count);
                return byCount != 0 ? byCount : x.CompareTo(y);
            });

            if (chosen.Count > max) chosen.RemoveRange(max, chosen.Count - max);

            var bucketOfCell = new Dictionary<int, int>();

            for (var k = 0; k < chosen.Count; k++)
            {
                var cell = cells[chosen[k]];
                var mean = cell.Sum / cell.Count;
                mean.a = 1f;

                centres.Add(mean);
                bucketOfCell[chosen[k]] = k;
            }

            for (var i = 0; i < colours.Count; i++)
            {
                if (bucketOfCell.TryGetValue(cellOf[i], out var own))
                {
                    bucketOf[i] = own;
                    continue;
                }

                var best = 0;
                var bestDistance = float.MaxValue;

                for (var k = 0; k < centres.Count; k++)
                {
                    var d = colours[i] - centres[k];
                    var distance = d.r * d.r + d.g * d.g + d.b * d.b;
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = k;
                }

                bucketOf[i] = best;
            }

            return bucketOf;
        }

        /// <summary>A colour's cell in a 4x4x4 grid over RGB.</summary>
        private static int Cell(Color c)
        {
            var r = Mathf.Clamp(Mathf.FloorToInt(c.r * 4f), 0, 3);
            var g = Mathf.Clamp(Mathf.FloorToInt(c.g * 4f), 0, 3);
            var b = Mathf.Clamp(Mathf.FloorToInt(c.b * 4f), 0, 3);

            return r * 16 + g * 4 + b;
        }

        /// <summary>
        /// One wall colour's material: the building shader, matte, untextured, coloured. Null when the
        /// shader has no _Color to colour with - under the flat-colour fallback the walls carry vertex
        /// colours instead, and under Unlit/Texture there is nothing to tint - and the walls then draw with
        /// the floor's own building material.
        /// </summary>
        private Material MakeWallMaterial(int level, int bucket, Color colour) =>
            MakeTintMaterial($"QuestTreeMap3D-walls-{level}-{bucket}", colour);

        /// <summary>A matte, untextured, coloured material on the building shader, or null when that shader
        /// has no _Color (or the flat-colour fallback is in use). See <see cref="MakeWallMaterial"/>.</summary>
        private Material MakeTintMaterial(string name, Color colour)
        {
            if (_flatColours || _buildingShader == null) return null;

            // Remembered per shader: under Unlit/Texture every untextured side slot asks here EVERY frame, and a
            // null answer that was not remembered made and destroyed a Material each time (review F25).
            if (ReferenceEquals(_tintlessShader, _buildingShader)) return null;

            var material = Matte(new Material(_buildingShader) { name = name });

            if (!material.HasProperty("_Color"))
            {
                _tintlessShader = _buildingShader;
                Discard(material);
                return null;
            }

            // No texture: Standard's _MainTex then samples white, and the wall is exactly _Color, lit.
            material.mainTexture = null;
            material.color = colour;

            return material;
        }

        /// <summary>The shader the buildings are drawn with, kept for the wall materials, which may be made
        /// on a later frame than the rest (when the floor's picture arrives).</summary>
        private Shader _buildingShader;

        /// <summary>The building shader last found to have no _Color, or null. See <see cref="MakeTintMaterial"/>.</summary>
        private Shader _tintlessShader;

        // --- the atlas tiles (stage X) -----------------------------------------------------------------

        /// <summary>The tile store of the file last drawn with an atlas, or null. Main thread only; shared by every
        /// view of that file, like <see cref="_cachedFloors"/>, and dropped with it by <see cref="DropCaches"/>.</summary>
        private static TileStore _tiles;

        /// <summary>The store this view holds a use of, or null. See <see cref="AcquireTiles"/>.</summary>
        private TileStore _heldTiles;

        /// <summary>Takes a use of this file's tile store, making it on the first view of the file. Idempotent.</summary>
        private void AcquireTiles()
        {
            if (_heldTiles != null || _file == null) return;

            if (_tiles == null || !ReferenceEquals(_tiles.File, _file))
            {
                DropTiles();

                var paths = new string[MaxAtlasPages];
                var alphaPaths = new string[MaxAtlasPages];
                for (var page = 0; page < _pages.Length; page++)
                {
                    paths[page] = _pages[page]?.Picture?.ImagePath;
                    alphaPaths[page] = _pages[page]?.AlphaPath;
                }

                _tiles = TileStore.For(_file, _mapKey, paths, alphaPaths);
            }

            _tiles.Users++;
            _heldTiles = _tiles;
        }

        /// <summary>Lets go of this view's use of its tile store; the last user of a dropped store destroys it.
        /// Idempotent.</summary>
        private void ReleaseTiles()
        {
            var tiles = _heldTiles;
            if (tiles == null) return;

            _heldTiles = null;
            tiles.Users--;

            if (tiles.Users <= 0 && tiles.Orphaned) tiles.Destroy();
        }

        /// <summary>Takes the tile store out of the cache: destroyed now when no view uses it, else orphaned for its
        /// last user to destroy - the same contract as <see cref="Built"/>.</summary>
        private static void DropTiles()
        {
            var tiles = _tiles;
            _tiles = null;

            if (tiles == null) return;

            if (tiles.Users > 0) tiles.Orphaned = true;
            else tiles.Destroy();
        }

        /// <summary>
        /// One file's atlas TILES: every (page, tile rect) its buildings' ranges name, each cut out of its page into a
        /// texture of its own - so the GPU can REPEAT it. Stage W's page could not wrap (a UV past its tile sampled
        /// the neighbour), and EFT's walls are UV'd in world units, many repeats long: on Customs 1,737 of ~1,900
        /// textured uses fell back to a flat colour. A tile of its own with wrapMode Repeat draws the material's raw
        /// UVs as they are, with the Standard shader and nothing compiled at runtime.
        ///
        /// Per page, in order, paced: the PNG read on a worker; decoded READABLE on the main thread in the frame's
        /// one decode turn (DynamicMapsLibrary.TryTakeDecodeTurn - the picture cache's own LoadImage pacing, so a
        /// page and a floor never decode in one frame); its pixels taken once (GetPixels32) and the page texture
        /// destroyed at once; then its tiles cut a few a frame (<see cref="TileBudgetMs"/>) - each an RGB24 texture
        /// with mips, compressed to DXT1 (the builder makes every side a multiple of 4), Repeat, trilinear, aniso 4,
        /// and made non-readable - and the page's pixels let go. 284 tiles of 256 px are about 12 MB this way,
        /// against about 99 MB uncompressed.
        ///
        /// The page never enters the picture cache: nothing else draws it, and holding it would be 85 MB for nothing.
        /// REFERENCE-COUNTED by views and orphaned by a drop, as <see cref="Built"/> is; a tile's texture is assigned
        /// to the entries' materials by Draw and never destroyed by them.
        /// </summary>
        internal sealed class TileStore
        {
            /// <summary>Main-thread time a frame spends cutting tiles, in milliseconds (a tile is about 1 ms).</summary>
            private const double TileBudgetMs = 4d;

            /// <summary>HQ S1.5: anisotropic filtering on every tile - walls seen at a grazing angle keep their texture.</summary>
            private static readonly int TileAniso = 16;

            /// <summary>HQ S1.5: tiles up to this many pixels are DXT-compressed at high quality (slower, fewer block
            /// artefacts); larger ones at the fast setting so a cut stays inside <see cref="TileBudgetMs"/>.</summary>
            private static readonly int TileCompressHighQualityMaxPixels = 65_536;

            /// <summary>One tile: its rect on its page, and its texture once cut.</summary>
            internal sealed class Tile
            {
                public int Page;
                public int X;
                public int Y;
                public int W;
                public int H;
                public Texture2D Texture;
                public bool Failed;
                public long Bytes;
            }

            /// <summary>The parsed file this store belongs to - its identity in the cache.</summary>
            public MapMeshFile File;

            public readonly List<Tile> Tiles = new List<Tile>();

            /// <summary>How many live views hold this store.</summary>
            public int Users;

            /// <summary>Taken out of the cache while in use: the last user destroys it.</summary>
            public bool Orphaned;

            /// <summary>Every tile in every live store's bytes, for the build line.</summary>
            public static long ResidentBytesAll { get; private set; }

            private readonly Dictionary<long, int> _index = new Dictionary<long, int>();
            private readonly string[] _paths = new string[MaxAtlasPages];

            /// <summary>HQ S3.11: each page's alpha mask file, or null (a PNG page carries its own alpha).</summary>
            private readonly string[] _alphaPaths = new string[MaxAtlasPages];
            private readonly bool[] _pageFailed = new bool[MaxAtlasPages];
            private readonly List<int>[] _tilesOfPage = new List<int>[MaxAtlasPages];
            private readonly Dictionary<int, Color32[]> _buffers = new Dictionary<int, Color32[]>();
            private string _mapKey = "";

            private int _page = -1;
            private int _nextPage;
            private int _cursor;
            private Task<byte[]> _reading;
            private Color32[] _pixels;
            private int _pageWidth;
            private int _pageHeight;

            private int _pumpedFrame = -1;
            private bool _done;
            private bool _destroyed;
            private int _frames;
            private int _pagesCut;
            private int _cut;
            private int _failed;
            private long _bytes;
            private bool _allCompressed = true;
            private readonly Stopwatch _clock = new Stopwatch();

            /// <summary>The store for a file: every distinct tile its ranges name, indexed. Main thread; nothing is
            /// read or decoded until the first <see cref="Pump"/>.</summary>
            /// <param name="file">The parsed file.</param>
            /// <param name="mapKey">For the log lines.</param>
            /// <param name="paths">Each page's PNG by page number, null where the view has none.</param>
            /// <param name="alphaPaths">HQ S3.11: each page's alpha mask file, or null.</param>
            public static TileStore For(MapMeshFile file, string mapKey, string[] paths, string[] alphaPaths = null)
            {
                var store = new TileStore { File = file, _mapKey = mapKey ?? "" };

                for (var page = 0; page < MaxAtlasPages && page < paths.Length; page++) store._paths[page] = paths[page];
                if (alphaPaths != null)
                    for (var page = 0; page < MaxAtlasPages && page < alphaPaths.Length; page++) store._alphaPaths[page] = alphaPaths[page];

                if (file?.Buildings != null)
                {
                    foreach (var building in file.Buildings)
                    {
                        if (building?.Ranges == null) continue;

                        foreach (var range in building.Ranges)
                        {
                            if (range.Page < 0 || range.Page >= MaxAtlasPages) continue;

                            var key = KeyOf(range.Page, range.TileX, range.TileY, range.TileW, range.TileH);
                            if (store._index.ContainsKey(key)) continue;

                            store._index[key] = store.Tiles.Count;
                            store.Tiles.Add(new Tile
                            {
                                Page = range.Page, X = range.TileX, Y = range.TileY, W = range.TileW, H = range.TileH
                            });

                            (store._tilesOfPage[range.Page] ??= new List<int>()).Add(store.Tiles.Count - 1);
                        }
                    }
                }

                // A page with tiles but no file here: those tiles can never be cut. Failed up front, so the view drops
                // the page rather than drawing its faces in the fallback colour for good.
                for (var page = 0; page < MaxAtlasPages; page++)
                    if (store._tilesOfPage[page] != null && string.IsNullOrEmpty(store._paths[page]))
                        store.FailPage(page, null);

                return store;
            }

            /// <summary>A tile's identity: page (3 bits), x and y (13 each), w and h (11 each, up to 2047 - HQ S3.10 widened
            /// them from 9, where a 512 px side masked to 0).</summary>
            private static long KeyOf(int page, int x, int y, int w, int h) =>
                ((long)page << 48) | ((long)(x & 0x1FFF) << 35) | ((long)(y & 0x1FFF) << 22) | ((long)(w & 0x7FF) << 11) |
                (long)(h & 0x7FF);

            /// <summary>WORKER-SAFE (the index is complete before any prep starts and never changes): the tile a
            /// range draws, or -1.</summary>
            public int TileOf(MapMeshFile.AtlasRange range) =>
                _index.TryGetValue(KeyOf(range.Page, range.TileX, range.TileY, range.TileW, range.TileH), out var tile)
                    ? tile
                    : -1;

            /// <summary>The tile's texture, or null while it is not cut yet, or failed.</summary>
            public Texture2D TextureOf(int tile) =>
                tile >= 0 && tile < Tiles.Count ? Tiles[tile].Texture : null;

            /// <summary>HQ S1.1: bumped whenever a tile is cut or fails, or a page fails - a view renders again when it
            /// differs from what its last frame saw.</summary>
            public int Version { get; private set; }

            /// <summary>Whether a tile failed for good (its own cut threw, or its page failed).</summary>
            public bool TileFailed(int tile) => tile >= 0 && tile < Tiles.Count && Tiles[tile].Failed;

            /// <summary>HQ S3.11: the page a tile sits on, or -1.</summary>
            public int PageOf(int tile) => tile >= 0 && tile < Tiles.Count ? Tiles[tile].Page : -1;

            /// <summary>HQ S3.11: whether a page carries alpha (MapMeshFile.AlphaPages): its tiles are cut RGBA -> DXT5 and drawn
            /// through an alpha-clipped material.</summary>
            public bool AlphaPage(int page) => File != null && page >= 0 && page < MaxAtlasPages && (File.AlphaPages >> page & 1) != 0;

            /// <summary>HQ S3.11: whether a tile is on an alpha page.</summary>
            public bool AlphaTile(int tile) => AlphaPage(PageOf(tile));

            // --- which alpha-page tiles are really cut out (play-test 2026-09-29) ------------------------------------

            /// <summary>
            /// Play-test 2026-09-29: whether a tile is GENUINELY cut out - it has at least one texel the viewer's clip at 0.5
            /// throws away - rather than merely sitting on an alpha page. The builder marks a whole PAGE alpha as soon as one
            /// cut-out tile lands on it (MapMeshBuilder.AlphaMaskOf), and the packer mixes opaque tiles onto the same
            /// pages: on the 2026-09-29 Woods capture 3 of 7 pages were alpha and 92 % of the top-face AREA sat on them, of
            /// which 435 of 459 tiles had not one clear texel. Keeping every alpha-page tile's roof on the atlas left the
            /// roofs-from-the-picture setting moving 2 % of the building area. Only a tile with a clear texel keeps its
            /// cutout; an opaque one on an alpha page takes the picture like any other roof.
            ///
            /// Not a page's tile, or not an alpha page: false. The measurement not made yet, or not possible for the
            /// page (see <see cref="MeasureOpacity"/>): true - the old whole-page rule, which can only keep a roof on the
            /// atlas, never draw a leaf card as a solid quad. Workers call <see cref="WaitOpacity"/> first.
            /// </summary>
            public bool CutOutTile(int tile)
            {
                if (!AlphaTile(tile)) return false;

                var measured = _opacity != null && _opacity.Status == TaskStatus.RanToCompletion ? _opacity.Result : null;
                if (measured == null || tile < 0 || tile >= measured.CutOut.Length) return true;

                return measured.CutOut[tile];
            }

            /// <summary>What <see cref="MeasureOpacity"/> found: per tile whether it is cut out (true for every alpha-page tile
            /// it could not read), and one line for the build log.</summary>
            private sealed class Opacity
            {
                public bool[] CutOut;
                public string Note = "";
            }

            /// <summary>The opacity measurement, started by <see cref="EnsureOpacity"/>; null until then.</summary>
            private Task<Opacity> _opacity;

            /// <summary>MAIN THREAD, from the build's snapshot when the roofs take the picture: starts the per-tile opacity
            /// measurement on a worker, once per store. Nothing here touches Unity, so the worker needs nothing from the
            /// main thread and a prep waiting on it cannot deadlock.</summary>
            public void EnsureOpacity()
            {
                if (_opacity != null || File == null) return;

                var tiles = new (int Page, int X, int Y, int W, int H)[Tiles.Count];
                for (var i = 0; i < tiles.Length; i++) tiles[i] = (Tiles[i].Page, Tiles[i].X, Tiles[i].Y, Tiles[i].W, Tiles[i].H);

                var alphaPages = new bool[MaxAtlasPages];
                for (var page = 0; page < MaxAtlasPages; page++) alphaPages[page] = AlphaPage(page) && _tilesOfPage[page] != null;

                var paths = (string[])_paths.Clone();
                var masks = (string[])_alphaPaths.Clone();

                _opacity = Task.Run(() => MeasureOpacity(tiles, alphaPages, paths, masks));
            }

            /// <summary>WORKER: waits for <see cref="EnsureOpacity"/>'s measurement, so every triangle of a build is routed on
            /// the same answer (the roof pass and the wall pass must split each triangle exactly once). Returns at once when
            /// none was started - <see cref="CutOutTile"/> then keeps the whole-page rule for the build.</summary>
            public void WaitOpacity(CancellationToken cancel)
            {
                var task = _opacity;
                if (task == null) return;

                try
                {
                    task.Wait(cancel);
                }
                catch (AggregateException)
                {
                    // MeasureOpacity catches its own failures; a fault here still leaves CutOutTile on the whole-page rule
                }
            }

            /// <summary>The opacity measurement's clause for the build line, or "" when it was not made or not finished.</summary>
            public string OpacityNote =>
                _opacity != null && _opacity.Status == TaskStatus.RanToCompletion && _opacity.Result != null ? _opacity.Result.Note : "";

            /// <summary>The alpha a texel must reach to survive the viewer's clip (MakeCutout's _Cutoff 0.5).</summary>
            private const byte ClipAlpha = 128;

            /// <summary>
            /// WORKER. For each alpha page with tiles, the alpha the tile store will cut from - the page's own mask file when
            /// it has one (a host's JPEG page: its red channel, as <see cref="ApplyAlphaMask"/> reads it), else the page PNG's
            /// alpha channel - streamed row by row through <see cref="PngAlphaRows"/>, and every tile with one texel under
            /// <see cref="ClipAlpha"/> marked cut out. A page that cannot be read (missing, a JPEG with no mask, a mask of
            /// another size, a PNG this reader does not take) keeps every tile cut out: the old rule for that page, said in
            /// the note. Never throws.
            /// </summary>
            private static Opacity MeasureOpacity((int Page, int X, int Y, int W, int H)[] tiles, bool[] alphaPages,
                string[] paths, string[] masks)
            {
                var result = new Opacity { CutOut = new bool[tiles.Length] };
                var f = CultureInfo.InvariantCulture;
                int read = 0, opaque = 0, cut = 0, unknown = 0;
                var failures = "";

                for (var i = 0; i < tiles.Length; i++) result.CutOut[i] = alphaPages[tiles[i].Page];

                for (var page = 0; page < MaxAtlasPages; page++)
                {
                    if (!alphaPages[page]) continue;

                    var onPage = new List<int>();
                    for (var i = 0; i < tiles.Length; i++)
                        if (tiles[i].Page == page) onPage.Add(i);

                    string why;
                    try
                    {
                        why = MeasurePage(page, paths[page], masks[page], tiles, onPage, result.CutOut);
                    }
                    catch (Exception ex)
                    {
                        why = ex.GetType().Name + ": " + ex.Message;
                    }

                    if (why != null)
                    {
                        // the old rule for the whole page: every tile on it keeps its cutout
                        foreach (var i in onPage) result.CutOut[i] = true;
                        unknown += onPage.Count;
                        failures += string.Format(f, "{0}page {1} unread ({2})", failures.Length > 0 ? ", " : "", page, why);
                        continue;
                    }

                    read++;
                    foreach (var i in onPage)
                        if (result.CutOut[i]) cut++;
                        else opaque++;
                }

                result.Note = string.Format(f, "alpha tiles {0} opaque / {1} cut out over {2} page(s) read{3}{4}",
                    opaque, cut, read,
                    unknown > 0 ? string.Format(f, ", {0} kept cut out unread", unknown) : "",
                    failures.Length > 0 ? " - " + failures : "");

                return result;
            }

            /// <summary>One alpha page of <see cref="MeasureOpacity"/>: null when read (<paramref name="cutOut"/> set for each
            /// tile on it), else why not. A tile's rect is from the page's BOTTOM (TileStore.Cut); a PNG's row 0 is its top,
            /// so a tile covers PNG rows height - Y - H .. height - Y - 1.</summary>
            private static string MeasurePage(int page, string path, string mask, (int Page, int X, int Y, int W, int H)[] tiles,
                List<int> onPage, bool[] cutOut)
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "no page file";

                var fromMask = !string.IsNullOrEmpty(mask);
                string source;
                int pageWidth = 0, pageHeight = 0;

                if (fromMask)
                {
                    // the store applies a mask only when it is the page's own size (ApplyAlphaMask); the page's size from its
                    // header - the first 64 KB holds a PNG's IHDR and a JPEG's frame header alike
                    var head = new byte[64 * 1024];
                    int got;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        got = stream.Read(head, 0, head.Length);
                    if (got < head.Length) Array.Resize(ref head, got);

                    if (!DynamicMapsLibrary.PictureSize(head, out pageWidth, out pageHeight)) return "page size unreadable";
                    if (!System.IO.File.Exists(mask)) return "no mask file";
                    source = mask;
                }
                else
                {
                    source = path;
                }

                var bytes = System.IO.File.ReadAllBytes(source);
                var starts = new int[onPage.Count];
                var ends = new int[onPage.Count];
                var height = 0;

                var why = PngAlphaRows(bytes, fromMask, (width, h) =>
                {
                    if (fromMask && (width != pageWidth || h != pageHeight))
                        return string.Format(CultureInfo.InvariantCulture, "mask {0}x{1} is not the page's {2}x{3}", width, h,
                            pageWidth, pageHeight);

                    height = h;
                    for (var k = 0; k < onPage.Count; k++)
                    {
                        var t = tiles[onPage[k]];
                        if (t.X < 0 || t.Y < 0 || t.W <= 0 || t.H <= 0 || t.X + t.W > width || t.Y + t.H > h)
                            return string.Format(CultureInfo.InvariantCulture, "tile {0} outside the page", onPage[k]);

                        starts[k] = h - t.Y - t.H;
                        ends[k] = h - t.Y - 1;
                        cutOut[onPage[k]] = false;   // proven opaque until a clear texel is found
                    }

                    return null;
                }, (row, alpha, stride, offset) =>
                {
                    for (var k = 0; k < onPage.Count; k++)
                    {
                        var index = onPage[k];
                        if (cutOut[index] || row < starts[k] || row > ends[k]) continue;

                        var t = tiles[index];
                        for (var x = t.X; x < t.X + t.W; x++)
                        {
                            if (alpha[x * stride + offset] >= ClipAlpha) continue;

                            cutOut[index] = true;
                            break;
                        }
                    }
                });

                return why;
            }

            /// <summary>
            /// WORKER. A minimal PNG reader for the one question <see cref="MeasurePage"/> asks: 8-bit, non-interlaced,
            /// grey (0), RGB (2), grey+alpha (4) or RGBA (6) - what the capture and the host write. The IDAT data is
            /// inflated as one stream (zlib's two header bytes skipped: .NET's DeflateStream takes raw deflate) and each
            /// scanline unfiltered against the one before. <paramref name="header"/> gets (width, height) and may refuse
            /// the picture; <paramref name="row"/> gets (PNG row, the unfiltered bytes, bytes per pixel, the byte offset of
            /// the channel read). The channel: for a mask (<paramref name="mask"/>) the first one (grey, or red, as the
            /// store's ApplyAlphaMask reads it); for a page its alpha - and a page with no alpha channel is opaque
            /// throughout, which is what the store would cut from it, so every row then reads 255. Null when read, else
            /// why not.
            /// </summary>
            private static string PngAlphaRows(byte[] png, bool mask, Func<int, int, string> header,
                Action<int, byte[], int, int> row)
            {
                if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return "not a PNG";

                int width = 0, height = 0, depth = 0, colour = -1, interlace = 0;
                var idat = new MemoryStream();
                var at = 8;

                while (at + 8 <= png.Length)
                {
                    var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
                    var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                    var data = at + 8;
                    if (length < 0 || data + length > png.Length) return "truncated chunk " + type;

                    if (type == "IHDR" && length >= 13)
                    {
                        width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                        height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                        depth = png[data + 8];
                        colour = png[data + 9];
                        interlace = png[data + 12];
                    }
                    else if (type == "IDAT")
                    {
                        idat.Write(png, data, length);
                    }
                    else if (type == "tRNS")
                    {
                        // review 2026-09-29: a transparency chunk makes texels clear that this reader would count as
                        // opaque - refused, so the page keeps the whole-page rule (roofs stay on the atlas: the safe side)
                        return "tRNS chunk";
                    }
                    else if (type == "IEND")
                    {
                        break;
                    }

                    at = data + length + 4;   // past the CRC
                }

                if (width <= 0 || height <= 0 || width > 1 << 14 || height > 1 << 14) return "no usable IHDR";
                if (depth != 8 || interlace != 0) return string.Format(CultureInfo.InvariantCulture, "depth {0}, interlace {1}", depth, interlace);

                int bpp;
                switch (colour)
                {
                    case 0: bpp = 1; break;
                    case 2: bpp = 3; break;
                    case 4: bpp = 2; break;
                    case 6: bpp = 4; break;
                    default: return string.Format(CultureInfo.InvariantCulture, "colour type {0}", colour);
                }

                // the channel read, or -1: a page with no alpha channel is opaque
                var offset = mask ? 0 : colour == 6 ? 3 : colour == 4 ? 1 : -1;

                var refused = header(width, height);
                if (refused != null) return refused;

                if (idat.Length < 2) return "no image data";
                idat.Position = 0;
                var cmf = idat.ReadByte();
                var flg = idat.ReadByte();
                if ((cmf & 0x0F) != 8 || (flg & 0x20) != 0) return "not a plain zlib stream";

                var stride = width * bpp;
                var previous = new byte[stride];
                var current = new byte[stride];
                var opaqueRow = offset < 0 ? new byte[] { 255 } : null;

                using (var inflate = new System.IO.Compression.DeflateStream(idat, System.IO.Compression.CompressionMode.Decompress))
                {
                    var filter = new byte[1];

                    for (var y = 0; y < height; y++)
                    {
                        if (!ReadFully(inflate, filter, 1) || !ReadFully(inflate, current, stride))
                            return string.Format(CultureInfo.InvariantCulture, "image data ends at row {0}", y);

                        if (!Unfilter(filter[0], current, previous, bpp))
                            return string.Format(CultureInfo.InvariantCulture, "filter {0} at row {1}", filter[0], y);

                        if (opaqueRow != null) row(y, opaqueRow, 0, 0);   // every x reads index 0: 255
                        else row(y, current, bpp, offset);

                        var swap = previous;
                        previous = current;
                        current = swap;
                    }
                }

                return null;
            }

            /// <summary>Reads exactly <paramref name="count"/> bytes, or false at the stream's end.</summary>
            private static bool ReadFully(Stream stream, byte[] buffer, int count)
            {
                var done = 0;
                while (done < count)
                {
                    var got = stream.Read(buffer, done, count - done);
                    if (got <= 0) return false;
                    done += got;
                }

                return true;
            }

            /// <summary>PNG's five scanline filters undone in place (the spec's reconstruction), against the previous row's
            /// unfiltered bytes (zero before the first row). False for a filter type the spec does not have.</summary>
            private static bool Unfilter(byte type, byte[] line, byte[] above, int bpp)
            {
                var n = line.Length;

                switch (type)
                {
                    case 0:
                        return true;
                    case 1:
                        for (var i = bpp; i < n; i++) line[i] = (byte)(line[i] + line[i - bpp]);
                        return true;
                    case 2:
                        for (var i = 0; i < n; i++) line[i] = (byte)(line[i] + above[i]);
                        return true;
                    case 3:
                        for (var i = 0; i < n; i++)
                            line[i] = (byte)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + above[i]) >> 1));
                        return true;
                    case 4:
                        for (var i = 0; i < n; i++)
                        {
                            int a = i >= bpp ? line[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
                            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            line[i] = (byte)(line[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                        }

                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>Whether a page could not be had at all (missing, unreadable, will not decode, too big).</summary>
            public bool PageFailed(int page) => page >= 0 && page < MaxAtlasPages && _pageFailed[page];

            /// <summary>Whether the page of <paramref name="tile"/> failed.</summary>
            public bool PageFailedFor(int tile) => tile >= 0 && tile < Tiles.Count && _pageFailed[Tiles[tile].Page];

            /// <summary>MAIN THREAD, once a frame however many views call it: the next step of the paced cut.</summary>
            public void Pump()
            {
                if (_done || _destroyed || _pumpedFrame == Time.frameCount) return;

                _pumpedFrame = Time.frameCount;
                _frames++;
                if (!_clock.IsRunning) _clock.Start();

                var frame = Stopwatch.StartNew();

                while (frame.Elapsed.TotalMilliseconds < TileBudgetMs)
                {
                    if (_page < 0 && !NextPage())
                    {
                        Finish();
                        return;
                    }

                    if (_pixels == null)
                    {
                        // Read on a worker; decoded in this frame's one decode turn, and that decode is the frame.
                        if (_reading == null)
                        {
                            var path = _paths[_page];
                            _reading = Task.Run(() => System.IO.File.ReadAllBytes(path));
                            return;
                        }

                        if (!_reading.IsCompleted || !DynamicMapsLibrary.TryTakeDecodeTurn()) return;

                        var task = _reading;
                        _reading = null;

                        if (!Decode(task)) _page = -1;
                        return;
                    }

                    var tiles = _tilesOfPage[_page];

                    if (_cursor >= tiles.Count)
                    {
                        // Every tile of the page is cut: its pixels go (64 MB of managed array for a 4096 page).
                        _pixels = null;
                        _pagesCut++;
                        _page = -1;
                        continue;
                    }

                    Cut(tiles[_cursor++]);
                }
            }

            /// <summary>Moves to the next page with tiles still to cut; false when there is none.</summary>
            private bool NextPage()
            {
                while (_nextPage < MaxAtlasPages)
                {
                    var page = _nextPage++;
                    if (_tilesOfPage[page] == null || _pageFailed[page]) continue;

                    _page = page;
                    _cursor = 0;
                    return true;
                }

                return false;
            }

            /// <summary>MAIN THREAD. The page's pixels, from a READABLE decode whose texture is destroyed at once.</summary>
            private bool Decode(Task<byte[]> task)
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    FailPage(_page, $"could not be read ({task.Exception?.GetBaseException().Message})");
                    return false;
                }

                var bytes = task.Result;

                // The header before the decode, as for every picture (review F49): a page is AtlasPageSize a side.
                if (!DynamicMapsLibrary.PictureSize(bytes, out var width, out var height) ||
                    width > MapMeshFile.AtlasPageSize || height > MapMeshFile.AtlasPageSize)
                {
                    FailPage(_page, $"is not a PNG of at most {MapMeshFile.AtlasPageSize} px a side ({width}x{height})");
                    return false;
                }

                Texture2D texture = null;

                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false) { name = "QuestTreeMap3D-atlaspage" };

                    // Readable (markNonReadable false): the tiles are cut from its pixels.
                    if (!texture.LoadImage(bytes, markNonReadable: false))
                    {
                        FailPage(_page, "would not decode");
                        return false;
                    }

                    _pageWidth = texture.width;
                    _pageHeight = texture.height;
                    _pixels = texture.GetPixels32();

                    // HQ S3.11: a host page travelled as a JPEG, alpha 255 throughout - its mask puts the alpha back
                    if (AlphaPage(_page) && !string.IsNullOrEmpty(_alphaPaths[_page])) ApplyAlphaMask(_page);
                    return true;
                }
                catch (Exception ex)
                {
                    _pixels = null;
                    FailPage(_page, $"could not be decoded ({ex.GetType().Name}: {ex.Message})");
                    return false;
                }
                finally
                {
                    Discard(texture);
                }
            }

            /// <summary>MAIN THREAD. One tile out of the current page's pixels into a texture of its own.</summary>
            private void Cut(int index)
            {
                var tile = Tiles[index];

                if (tile.W <= 0 || tile.H <= 0 || tile.X < 0 || tile.Y < 0 ||
                    tile.X + tile.W > _pageWidth || tile.Y + tile.H > _pageHeight)
                {
                    tile.Failed = true;
                    _failed++;
                    return;
                }

                Texture2D texture = null;

                try
                {
                    var size = tile.W * tile.H;

                    if (!_buffers.TryGetValue(size, out var buffer))
                    {
                        buffer = new Color32[size];
                        _buffers[size] = buffer;
                    }

                    // Row by row, bottom up on both sides: the page's row 0 is its bottom (LoadImage's order, and the
                    // builder's TileY is from the bottom), and so is the tile's - no flip.
                    for (var row = 0; row < tile.H; row++)
                        Array.Copy(_pixels, (tile.Y + row) * _pageWidth + tile.X, buffer, row * tile.W, tile.W);

                    // HQ S3.11: a tile on an alpha page keeps its alpha - RGBA32, compressed to DXT5
                    var alpha = AlphaPage(tile.Page);

                    texture = new Texture2D(tile.W, tile.H, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, mipChain: true)
                    {
                        name = $"QuestTreeMap3D-tile{index}",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = TileAniso
                    };

                    texture.SetPixels32(buffer);
                    texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);

                    // DXT needs 4 x 4 blocks; the builder aligns every tile, and one that is not stays uncompressed.
                    if (tile.W % MapMeshFile.TileAlign == 0 && tile.H % MapMeshFile.TileAlign == 0)
                        texture.Compress(highQuality: size <= TileCompressHighQualityMaxPixels);

                    // Uploaded and the CPU copy freed.
                    texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

                    var compressed = texture.format == TextureFormat.DXT1 || texture.format == TextureFormat.DXT5;
                    if (!compressed) _allCompressed = false;

                    // DXT1: half a byte a pixel; DXT5 one; anything else counted at 4 (D3D11 has no 24-bit format). Mips + 1/3.
                    var bytes = texture.format == TextureFormat.DXT1 ? (long)size / 2
                        : texture.format == TextureFormat.DXT5 ? size
                        : (long)size * 4;
                    if (texture.mipmapCount > 1) bytes += bytes / 3;

                    tile.Texture = texture;
                    tile.Bytes = bytes;
                    _bytes += bytes;
                    ResidentBytesAll += bytes;
                    _cut++;
                    unchecked { Version++; }
                    texture = null;
                }
                catch (Exception ex)
                {
                    tile.Failed = true;
                    _failed++;
                    unchecked { Version++; }

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: atlas tile {index} of '{_mapKey}' could not be cut ({ex.GetType().Name}: {ex.Message}).");
                }
                finally
                {
                    Discard(texture);
                }
            }

            /// <summary>HQ S3.11: the page's alpha mask (a grey PNG of the page's size) read and decoded, its red channel
            /// written into the page pixels' alpha. Any failure leaves the page opaque and says so once, in debug.</summary>
            private void ApplyAlphaMask(int page)
            {
                Texture2D mask = null;

                try
                {
                    var bytes = System.IO.File.ReadAllBytes(_alphaPaths[page]);

                    if (!DynamicMapsLibrary.PictureSize(bytes, out var width, out var height) || width != _pageWidth || height != _pageHeight)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: atlas page {page} of '{_mapKey}' has a mask of another size ({width}x{height}) - drawn opaque.");
                        return;
                    }

                    mask = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false) { name = "QuestTreeMap3D-alphamask" };
                    if (!mask.LoadImage(bytes, markNonReadable: false) || mask.width != _pageWidth || mask.height != _pageHeight)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: atlas page {page} of '{_mapKey}' has a mask that would not decode - drawn opaque.");
                        return;
                    }

                    var alpha = mask.GetPixels32();
                    var n = Math.Min(alpha.Length, _pixels.Length);
                    for (var i = 0; i < n; i++) _pixels[i].a = alpha[i].r;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: atlas page {page} of '{_mapKey}' has a mask that could not be read ({ex.GetType().Name}) - drawn opaque.");
                }
                finally
                {
                    Discard(mask);
                }
            }

            /// <summary>Marks a page and every tile on it failed, and says so once.</summary>
            private void FailPage(int page, string reason)
            {
                if (page < 0 || page >= MaxAtlasPages || _pageFailed[page]) return;

                _pageFailed[page] = true;
                unchecked { Version++; }

                var tiles = _tilesOfPage[page];
                if (tiles != null)
                    foreach (var index in tiles)
                        if (!Tiles[index].Failed && Tiles[index].Texture == null) { Tiles[index].Failed = true; _failed++; }

                if (reason != null)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: atlas page {page} of the 3D map for '{_mapKey}' {reason} - its faces are drawn " +
                        $"without the game's textures.");
                }
            }

            private void Finish()
            {
                _done = true;
                _clock.Stop();
                _buffers.Clear();

                Plugin.LogSource?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "QuestTree: 3D map textures for {0} - {1} tile(s) cut from {2} page(s) in {3:#,##0} ms over {4} " +
                    "frame(s), ~{5:0.0} MB {6}{7}.",
                    _mapKey, _cut, _pagesCut, _clock.ElapsedMilliseconds, _frames, _bytes / (1024d * 1024d),
                    _allCompressed ? "(DXT1)" : "(some uncompressed)",
                    _failed > 0 ? string.Format(CultureInfo.InvariantCulture, ", {0} failed", _failed) : ""));
            }

            /// <summary>MAIN THREAD. Destroys every tile's texture and forgets the work in flight. Idempotent.</summary>
            public void Destroy()
            {
                if (_destroyed) return;

                _destroyed = true;
                _done = true;
                _reading = null;
                _pixels = null;
                _buffers.Clear();

                foreach (var tile in Tiles)
                {
                    if (tile.Texture != null) Discard(tile.Texture);

                    tile.Texture = null;
                    ResidentBytesAll -= tile.Bytes;
                    tile.Bytes = 0;
                }

                _bytes = 0;
            }
        }

        // --- the geometry, prepared off the main thread ------------------------------------------------

        /// <summary>
        /// One mesh's worth of arrays, prepared by a worker and uploaded by <see cref="MakeMesh(MeshData)"/>.
        /// Copies, not the worker's lists: the accumulators reuse their lists for the next chunk.
        /// </summary>
        internal sealed class MeshData
        {
            public string Name = "";
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Indices;
            public Color32[] Colours;

            /// <summary>Opt B: the chunk's size class (<see cref="SizeClassOf"/>) and, for a small one, the largest extent (m)
            /// of any renderer in it - what <see cref="Submit"/> measures against the pixel threshold. Set by the sinks, kept
            /// through MergeSmall (the max) and Pack, and registered with the mesh by <see cref="MakeMesh"/>.</summary>
            public byte SizeClass;

            public float PropExtent;

            /// <summary>Its UVs are in repeats of a tile drawn with wrapMode Repeat (an atlas chunk), so a whole-repeat shift
            /// of all of them samples the same texels - which is what lets <see cref="Pack"/> bring them into [0, 1].</summary>
            public bool RepeatUvs;

            // --- the compact form (CompactVertices), made by Pack on the worker; null Packed = the float arrays above

            /// <summary>The interleaved vertex buffer, <see cref="Stride"/> bytes a vertex as 32-bit words.</summary>
            public uint[] Packed;

            public int VertexCount;
            public int Stride;
            public bool UvFloat;
            public bool HasColour;

            /// <summary>The indices as sixteen-bit, when <see cref="SixteenBitIndices"/> and the chunk has at most
            /// <see cref="SixteenBitVertexLimit"/> vertices; else null and <see cref="Indices"/> goes up as UInt32.</summary>
            public ushort[] Indices16;

            /// <summary>The lattice origin and uniform scale (<see cref="MeshFrame"/>), and the bounds in lattice units.</summary>
            public Vector3 Origin;

            public float Scale;
            public Bounds LocalBounds;

            /// <summary>One float's bits as a word, without an allocation (BitConverter.GetBytes would make one per UV).</summary>
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            private struct FloatBits
            {
                [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
                [System.Runtime.InteropServices.FieldOffset(0)] public uint Word;
            }

            /// <summary>
            /// WORKER. The compact form of this chunk (<see cref="CompactVertices"/>), and the float arrays dropped:
            /// <list type="bullet">
            /// <item>POSITION UNorm16x4: x, y, z as steps of the one lattice step (<see cref="PositionQuantum"/>) from an origin
            /// snapped to that lattice; w = 65535, i.e. 1.0, because
            /// Standard's forward pass multiplies the whole float4 by unity_ObjectToWorld and a w of 0 would drop the
            /// translation. The world position comes back through the DrawMesh matrix (<see cref="MeshFrame"/>).</item>
            /// <item>NORMAL SNorm8x4: the world normal (the frame's scale is uniform, so the object-space normal IS it).</item>
            /// <item>COLOR UNorm8x4, flat-colour builds only.</item>
            /// <item>TEXCOORD0 UNorm16x2 when every UV is in [0, 1] (after a whole-repeat shift for <see cref="RepeatUvs"/>):
            /// 1/65,535 is a sixteenth of a texel at 4,096 px and a quarter at the 16,384 px ground picture. Float32x2
            /// otherwise: an atlas face 17.25 repeats long cannot be UNorm16, and at Float16 it would be off by 0.016
            /// repeats - pixels, not sub-texel.</item>
            /// </list>
            /// Attributes in Unity's required order (position, normal, colour, texcoord), each a whole number of words.
            /// A chunk under <see cref="DynamicBatchVertices"/> vertices, or wider than 65,534 steps, stays float, its positions
            /// snapped to the same lattice (and its atlas UVs shifted by whole repeats all the same).
            /// </summary>
            public void Pack()
            {
                var n = Vertices?.Length ?? 0;
                if (n == 0 || Indices == null || Normals == null || Normals.Length < n) return;

                // --- the frame: the bounds, then the smallest power-of-two quantum the extent fits in
                var min = Vertices[0];
                var max = Vertices[0];

                for (var i = 1; i < n; i++)
                {
                    min = Vector3.Min(min, Vertices[i]);
                    max = Vector3.Max(max, Vertices[i]);
                }

                // ONE step for every chunk, never a coarser one for a wide chunk (see PositionQuantum)
                const double q = PositionQuantum;

                var ox = Math.Floor(min.x / q) * q;
                var oy = Math.Floor(min.y / q) * q;
                var oz = Math.Floor(min.z / q) * q;

                // one step of headroom: the rounding of the largest coordinate must not need code 65536
                var fits = Math.Max(max.x - ox, Math.Max(max.y - oy, max.z - oz)) / q <= 65534d;

                // --- the UVs: a whole-repeat shift for a Repeat tile, in place - for the float form too, where a smaller
                // magnitude is more float precision
                var shiftU = 0f;
                var shiftV = 0f;

                if (RepeatUvs && Uvs != null && Uvs.Length >= n)
                {
                    var minU = float.PositiveInfinity;
                    var minV = float.PositiveInfinity;

                    for (var i = 0; i < n; i++)
                    {
                        minU = Mathf.Min(minU, Uvs[i].x);
                        minV = Mathf.Min(minV, Uvs[i].y);
                    }

                    if (!float.IsInfinity(minU) && !float.IsNaN(minU)) shiftU = Mathf.Floor(minU);
                    if (!float.IsInfinity(minV) && !float.IsNaN(minV)) shiftV = Mathf.Floor(minV);

                    if (shiftU != 0f || shiftV != 0f)
                    {
                        var shift = new Vector2(shiftU, shiftV);
                        for (var i = 0; i < n; i++) Uvs[i] -= shift;
                    }

                    shiftU = shiftV = 0f;
                }

                // --- too small to leave dynamic batching, or too wide for one lattice frame: float, on the lattice
                if (n < DynamicBatchVertices || !fits)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var v = Vertices[i];
                        Vertices[i] = new Vector3(Snap(v.x, q), Snap(v.y, q), Snap(v.z, q));
                    }

                    return;
                }

                var uvFloat = false;

                if (Uvs != null && Uvs.Length >= n)
                {
                    for (var i = 0; i < n && !uvFloat; i++)
                    {
                        var u = Uvs[i].x - shiftU;
                        var v = Uvs[i].y - shiftV;

                        // !(in range) rather than (out of range), so a NaN goes to the float form too
                        if (!(u >= 0f && u <= 1f && v >= 0f && v <= 1f)) uvFloat = true;
                    }
                }

                var hasColour = Colours != null && Colours.Length >= n;
                var words = 2 + 1 + (hasColour ? 1 : 0) + (uvFloat ? 2 : 1);
                var packed = new uint[n * words];

                var localMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var localMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                var bits = new FloatBits();

                for (var i = 0; i < n; i++)
                {
                    var at = i * words;
                    var p = Vertices[i];

                    var cx = Code(p.x, ox, q);
                    var cy = Code(p.y, oy, q);
                    var cz = Code(p.z, oz, q);

                    packed[at] = cx | (cy << 16);
                    packed[at + 1] = cz | (65535u << 16);

                    var local = new Vector3(cx / 65535f, cy / 65535f, cz / 65535f);
                    localMin = Vector3.Min(localMin, local);
                    localMax = Vector3.Max(localMax, local);

                    var normal = Normals[i];
                    packed[at + 2] = Snorm8(normal.x) | (Snorm8(normal.y) << 8) | (Snorm8(normal.z) << 16);

                    var next = at + 3;

                    if (hasColour)
                    {
                        var c = Colours[i];
                        packed[next++] = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
                    }

                    var uv = Uvs != null && i < Uvs.Length ? Uvs[i] : Vector2.zero;

                    if (uvFloat)
                    {
                        bits.Float = uv.x;
                        packed[next] = bits.Word;
                        bits.Float = uv.y;
                        packed[next + 1] = bits.Word;
                    }
                    else
                    {
                        packed[next] = Unorm16(uv.x - shiftU) | (Unorm16(uv.y - shiftV) << 16);
                    }
                }

                Packed = packed;
                VertexCount = n;
                Stride = words * 4;
                UvFloat = uvFloat;
                HasColour = hasColour;
                Origin = new Vector3((float)ox, (float)oy, (float)oz);
                Scale = (float)(65535d * q);
                LocalBounds = new Bounds((localMin + localMax) * 0.5f, localMax - localMin);

                if (SixteenBitIndices && n <= SixteenBitVertexLimit)
                {
                    var small = new ushort[Indices.Length];
                    for (var i = 0; i < small.Length; i++) small[i] = (ushort)Indices[i];

                    Indices16 = small;
                    Indices = null;
                }

                // the float arrays are garbage now: MakeMesh uploads the packed form only
                Vertices = null;
                Normals = null;
                Uvs = null;
                Colours = null;
            }

            /// <summary>A coordinate on the lattice, as a float: floor(t + 0.5) like <see cref="Code"/>, so a float chunk's vertex
            /// is the point a compact neighbour's code decodes to. Exact in float: a whole number of power-of-two steps.</summary>
            private static float Snap(float value, double quantum) => (float)(Math.Floor(value / quantum + 0.5d) * quantum);

            /// <summary>A coordinate as lattice steps from the origin, clamped to the code range.</summary>
            private static uint Code(float value, double origin, double quantum)
            {
                // floor(t + 0.5), not Math.Round: the banker's rounding is not shift-invariant, and a vertex two chunks share
                // must get codes exactly an integer apart (origins are whole quanta apart) - the same lattice point in both
                var steps = Math.Floor((value - origin) / quantum + 0.5d);
                if (!(steps > 0d)) return 0u;
                return steps >= 65535d ? 65535u : (uint)steps;
            }

            /// <summary>A unit-range value as a UNorm16 code.</summary>
            private static uint Unorm16(float value) => (uint)Mathf.Clamp(Mathf.RoundToInt(value * 65535f), 0, 65535);

            /// <summary>A [-1, 1] value as an SNorm8 byte (two's complement, low byte).</summary>
            private static uint Snorm8(float value) =>
                (uint)(byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(value * 127f), -127, 127);

            /// <summary>WORKER. The arrays, copied, with their normals. <paramref name="creases"/> (WP8 D5, building
            /// geometry): corners whose faces meet at a crease sharper than <see cref="CreaseCosine"/> get vertices of
            /// their own (<see cref="SplitCreases"/>); the relief passes false and keeps the smooth average.</summary>
            public static MeshData From(
                string name, List<Vector3> vertices, List<Vector2> uvs, List<int> indices, List<Color32> colours,
                bool creases = true)
            {
                var data = new MeshData
                {
                    Name = name,
                    Vertices = vertices.ToArray(),
                    Uvs = uvs.ToArray(),
                    Indices = indices.ToArray(),
                    Colours = colours?.ToArray()
                };

                data.Normals = creases && CreaseCosine > -1f
                    ? SplitCreases(data, CreaseCosine)
                    : NormalsOf(data.Vertices, data.Indices);
                return data;
            }

            /// <summary>Two corner normals closer than this share one output vertex.</summary>
            private const float CornerAgree = 0.999f;

            /// <summary>
            /// WORKER. WP8 (D5): CREASE-AWARE normals. For each corner (face f at vertex v) the normal is the
            /// area-weighted sum of the faces around v within <paramref name="creaseCosine"/> of f's own normal - so a
            /// roof corner sums the roof's faces and not the wall's it is welded to (the decimator welds a game mesh's
            /// split hard-edge vertices; 24 % of the atlas roof vertices shared an index with a wall, and lit from above
            /// the band along every roof edge rendered dark). Corners of one vertex whose normals agree share it; each
            /// further group gets a COPY of the vertex (position, UV, colour) and its corners are repointed - the
            /// indices, vertices, UVs and colours of <paramref name="data"/> are replaced in place. A degenerate face's
            /// corner takes every face. At <paramref name="creaseCosine"/> -2 every corner of a vertex sums the same
            /// faces in the same order as <see cref="NormalsOf"/>, so nothing splits and the normals are its own.
            /// O(corners x fan).
            /// </summary>
            internal static Vector3[] SplitCreases(MeshData data, float creaseCosine)
            {
                var vertices = data.Vertices;
                var indices = data.Indices;
                var n = vertices.Length;
                var faces = indices.Length / 3;
                var fw = new Vector3[faces];
                var fn = new Vector3[faces];

                for (var f = 0; f < faces; f++)
                {
                    int a = indices[f * 3], b = indices[f * 3 + 1], c = indices[f * 3 + 2];
                    if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;

                    var w = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                    var length = w.magnitude;

                    fw[f] = w;
                    fn[f] = length > 1e-12f ? w / length : Vector3.zero;
                }

                // each vertex's corners, in corner order (so the sums run in NormalsOf's face order)
                var start = new int[n + 1];
                for (var k = 0; k < faces * 3; k++)
                {
                    var v = indices[k];
                    if ((uint)v < (uint)n) start[v + 1]++;
                }

                for (var v = 0; v < n; v++) start[v + 1] += start[v];

                var corners = new int[start[n]];
                var fill = new int[n];
                Array.Copy(start, fill, n);

                for (var k = 0; k < faces * 3; k++)
                {
                    var v = indices[k];
                    if ((uint)v < (uint)n) corners[fill[v]++] = k;
                }

                var normals = new List<Vector3>(n);
                for (var v = 0; v < n; v++) normals.Add(Vector3.up);

                List<Vector3> extraVertices = null;
                List<Vector2> extraUvs = null;
                List<Color32> extraColours = null;

                var groupNormal = new List<Vector3>();
                var groupIndex = new List<int>();

                for (var v = 0; v < n; v++)
                {
                    int s = start[v], e = start[v + 1];
                    if (s == e) continue;

                    groupNormal.Clear();
                    groupIndex.Clear();

                    for (var i = s; i < e; i++)
                    {
                        var corner = corners[i];
                        var own = fn[corner / 3];
                        var degenerate = own.sqrMagnitude < 0.5f;
                        var sum = Vector3.zero;

                        for (var j = s; j < e; j++)
                        {
                            var g = corners[j] / 3;
                            if (degenerate || Vector3.Dot(fn[g], own) >= creaseCosine) sum += fw[g];
                        }

                        var length = sum.magnitude;
                        var normal = length > 1e-12f ? sum / length : Vector3.up;

                        var index = -1;
                        for (var k = 0; k < groupNormal.Count; k++)
                        {
                            if (Vector3.Dot(groupNormal[k], normal) < CornerAgree) continue;

                            index = groupIndex[k];
                            break;
                        }

                        if (index < 0)
                        {
                            if (groupIndex.Count == 0)
                            {
                                index = v;
                                normals[v] = normal;
                            }
                            else
                            {
                                extraVertices ??= new List<Vector3>();
                                index = n + extraVertices.Count;
                                extraVertices.Add(vertices[v]);

                                if (data.Uvs != null && v < data.Uvs.Length) (extraUvs ??= new List<Vector2>()).Add(data.Uvs[v]);
                                if (data.Colours != null && v < data.Colours.Length) (extraColours ??= new List<Color32>()).Add(data.Colours[v]);

                                normals.Add(normal);
                            }

                            groupNormal.Add(normal);
                            groupIndex.Add(index);
                        }

                        indices[corner] = index;
                    }
                }

                if (extraVertices != null)
                {
                    var grownV = new Vector3[n + extraVertices.Count];
                    Array.Copy(vertices, grownV, n);
                    extraVertices.CopyTo(grownV, n);
                    data.Vertices = grownV;

                    if (data.Uvs != null)
                    {
                        var grownU = new Vector2[grownV.Length];
                        Array.Copy(data.Uvs, grownU, Math.Min(n, data.Uvs.Length));
                        extraUvs?.CopyTo(grownU, n);
                        data.Uvs = grownU;
                    }

                    if (data.Colours != null)
                    {
                        var grownC = new Color32[grownV.Length];
                        Array.Copy(data.Colours, grownC, Math.Min(n, data.Colours.Length));
                        extraColours?.CopyTo(grownC, n);
                        data.Colours = grownC;
                    }
                }

                return normals.ToArray();
            }

            /// <summary>
            /// WORKER. <see cref="From"/> over only the vertices <paramref name="indices"/> reference, renumbered in
            /// first-use order. A roof chunk's vertices are shared by its own-floor list and every "on another
            /// floor" list, and each destination copied ALL of them - up to 250k vertices per destination, most
            /// unreferenced, on the CPU and the GPU alike (review F24). The normals are the same either way:
            /// NormalsOf only sums the triangles of the list it is given.
            /// </summary>
            public static MeshData Compacted(
                string name, List<Vector3> vertices, List<Vector2> uvs, List<int> indices, List<Color32> colours)
            {
                var remap = new Dictionary<int, int>(Math.Min(indices.Count, vertices.Count));
                var keptVertices = new List<Vector3>();
                var keptUvs = new List<Vector2>();
                var keptColours = colours != null ? new List<Color32>() : null;
                var keptIndices = new List<int>(indices.Count);

                foreach (var old in indices)
                {
                    if (!remap.TryGetValue(old, out var index))
                    {
                        index = keptVertices.Count;
                        remap[old] = index;
                        keptVertices.Add(vertices[old]);
                        keptUvs.Add(uvs[old]);
                        keptColours?.Add(colours[old]);
                    }

                    keptIndices.Add(index);
                }

                return From(name, keptVertices, keptUvs, keptIndices, keptColours);
            }

            /// <summary>
            /// WORKER. Per-vertex normals: the sum of the (area-weighted) face normals of every triangle using the
            /// vertex, normalised - cross(v1 - v0, v2 - v0) for a triangle (v0, v1, v2), which is up for the
            /// ground's (a, c, b) winding, the winding that rendered lit. Computed HERE rather than by
            /// Mesh.RecalculateNormals on the main thread, so the upload is cheaper. A vertex no
            /// triangle uses, or whose faces cancel, gets straight up.
            /// </summary>
            internal static Vector3[] NormalsOf(Vector3[] vertices, int[] indices)
            {
                var normals = new Vector3[vertices.Length];

                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var a = indices[t];
                    var b = indices[t + 1];
                    var c = indices[t + 2];

                    var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);

                    normals[a] += n;
                    normals[b] += n;
                    normals[c] += n;
                }

                for (var i = 0; i < normals.Length; i++)
                {
                    var n = normals[i];
                    var length = n.magnitude;
                    normals[i] = length > 1e-12f ? n / length : Vector3.up;
                }

                return normals;
            }
        }

        /// <summary>One floor's geometry and counts, as a worker prepared it. The counts go onto the entry in
        /// <see cref="AfterPrepare"/>; every <see cref="MeshData"/> becomes one unit of upload.</summary>
        private sealed class FloorData
        {
            public int Level;
            public long Cells;
            public long GroundTriangles;

            /// <summary>Opt B: the relief's triangles before simplification; see <see cref="Built.GroundTrianglesFull"/>.</summary>
            public long GroundTrianglesFull;

            public long BuildingTriangles;
            public long TopTriangles;
            public long SideTriangles;
            public long WallTriangles;
            public long MovedRoofTriangles;
            public long GroundSkirtTriangles;
            public int BuildingCount;
            public int Dropped;

            /// <summary>Chunks before and after the merge rule (<see cref="CellSplitMinVertices"/>), for the first-frame line.</summary>
            public int ChunksSplit;

            public int ChunksMerged;

            public readonly List<MeshData> Ground = new List<MeshData>();
            public readonly List<MeshData> Roofs = new List<MeshData>();
            public readonly List<(int Level, MeshData Data)> RoofsElsewhere = new List<(int Level, MeshData Data)>();
            public readonly List<MeshData>[] Sides = new List<MeshData>[4];

            /// <summary>Atlas-textured faces, per tile (<see cref="TileStore"/> index).</summary>
            public readonly Dictionary<int, List<MeshData>> Atlas = new Dictionary<int, List<MeshData>>();
            public long AtlasTriangles;

            /// <summary>Pictures stage C: atlas top faces given the top picture instead - see
            /// <see cref="Built.RoofPictureTriangles"/>.</summary>
            public long RoofPictureTriangles;

            /// <summary>See <see cref="Built.RoofCutOutTriangles"/>.</summary>
            public long RoofCutOutTriangles;

            public double RoofCutOutArea;

            /// <summary>WP8 (V.2): the same four shares by AREA, m2, and the walls tinted for facing no side within
            /// 40 degrees.</summary>
            public double AtlasArea;

            public double TopArea;
            public double SideArea;
            public double TintArea;
            public long WallsOutside;
        }

        /// <summary>One floor's walls as a worker prepared them: a colour and its meshes per tint.</summary>
        private sealed class WallData
        {
            public readonly List<(Color Colour, List<MeshData> Meshes)> Tints = new List<(Color Colour, List<MeshData> Meshes)>();
            public Color Average = FallbackWallColour;
            public bool HasAverage;
            public int TintCount;
            public long Triangles;

            /// <summary>Chunks before and after the merge rule, for the first-frame line.</summary>
            public int ChunksSplit;

            public int ChunksMerged;
        }

        /// <summary>
        /// What a worker builds from: a SNAPSHOT of everything the classification and the UVs read, taken on
        /// the main thread, plus scratch arrays of its own. No Unity object is in it (a SidePicture is read for
        /// its numbers only - its picture slot is never touched), and nothing in it changes after the snapshot,
        /// so a worker cannot see the view rebuilt or a side dropped under it halfway through a floor.
        ///
        /// Everything that decides a triangle's fate - <see cref="ViewFor"/>, <see cref="FloorForFace"/>, the
        /// finite test - lives here, so the roof pass and the wall pass, even on two workers, classify with the
        /// SAME code and the same inputs: a triangle is in exactly one of them, as it was on one thread.
        /// </summary>
        private sealed class Prep
        {
            public MapMeshFile File;
            public string MapKey = "";
            public bool Flat;
            public bool SidesActive;

            /// <summary>Pictures stage C: <see cref="RoofsActive"/> as this build decided it.</summary>
            public bool RoofsFromPicture;

            public readonly DynamicMapsLibrary.SidePicture[] Sides = new DynamicMapsLibrary.SidePicture[4];
            public (int Level, float Low, float High)[] FloorRanges = new (int, float, float)[0];
            public float SpanX;
            public float SpanZ;

            /// <summary><see cref="CompactVertices"/> as this snapshot decided it - false too on a GPU without the formats
            /// (<see cref="CompactSupported"/>, asked on the main thread).</summary>
            public bool Compact;

            /// <summary>The spatial grid's columns and rows (<see cref="SpatialChunkMetres"/>); 1 x 1 under the rollback.</summary>
            public int CellsX = 1;

            public int CellsZ = 1;

            /// <summary>
            /// The spatial cell of a triangle: the one its CENTROID is in, so a triangle is in exactly one chunk and a
            /// building across a cell line is split along its triangles, never duplicated. Clamped to the grid - a building
            /// is kept up to a metre outside the extent. 0 under the rollback (one cell).
            /// </summary>
            public int CellOf(Vector3 a, Vector3 b, Vector3 c)
            {
                if (CellsX <= 1 && CellsZ <= 1) return 0;

                var x = (a.x + b.x + c.x) / 3f - (float)File.MinX;
                var z = (a.z + b.z + c.z) / 3f - (float)File.MinZ;

                var cx = Mathf.Clamp(Mathf.FloorToInt(x / SpatialChunkMetres), 0, CellsX - 1);
                var cz = Mathf.Clamp(Mathf.FloorToInt(z / SpatialChunkMetres), 0, CellsZ - 1);

                return cz * CellsX + cx;
            }

            /// <summary>Which atlas pages this view can draw (usable, textured shader). A face on a page that is
            /// not here keeps the stage U/V rule, so a page lost in transport costs its faces' texture, never
            /// the faces.</summary>
            public readonly bool[] PagePresent = new bool[MaxAtlasPages];

            /// <summary>The tiles of this file, for their index. Read-only on the worker: built whole before any
            /// prep starts and never changed after (see <see cref="TileStore.TileOf"/>). Null without an atlas.</summary>
            public TileStore Tiles;

            /// <summary>The atlas ranges of the building last loaded, or null.</summary>
            private List<MapMeshFile.AtlasRange> _ranges;

            /// <summary>Per range of the building last loaded, its tile index, or -1 when it draws no tile here (page
            /// absent, or a tile the store does not know).</summary>
            private int[] _rangeTile = new int[0];

            /// <summary>The building last loaded - the one whose UVs <see cref="AtlasUv"/> reads.</summary>
            private MapMeshFile.Building _building;

            /// <summary>
            /// The atlas range of the triangle at index position <paramref name="i"/> of the building last loaded,
            /// or -1 when it is in none this view can draw. Decides a face BEFORE the stage U/V rule does: an atlas
            /// face is never a roof, a side face or a tint, so the roof pass and the wall pass both ask here first
            /// and still split every triangle exactly once between them.
            /// </summary>
            public int AtlasRangeAt(int i)
            {
                if (_ranges == null) return -1;

                for (var k = 0; k < _ranges.Count; k++)
                {
                    var range = _ranges[k];
                    if (i < range.First || i >= range.First + range.Count) continue;

                    return _rangeTile[k] >= 0 ? k : -1;
                }

                return -1;
            }

            /// <summary>The tile range <paramref name="k"/> of the building last loaded draws with.</summary>
            public int TileOfRange(int k) => _rangeTile[k];

            /// <summary>
            /// Pictures stage C: whether a face the atlas would texture is a ROOF that takes the top picture instead - true
            /// only under <see cref="RoofsFromPicture"/> and for an upward face, n.y &gt;= <see cref="RoofNormalY"/> of its
            /// unit normal: faces <see cref="ViewFor"/> then sends to <see cref="TopView"/> under both of its rules. An
            /// underside (the top camera never saw it), a wall and a degenerate face keep the atlas. Asked only for faces
            /// with an atlas range, by the roof pass and <see cref="WallTriangle"/> alike, so the two still split every
            /// triangle exactly once. A face on a CUT-OUT tile keeps the atlas (review; play-test 2026-09-29: a tile with a
            /// clear texel, <see cref="TileStore.CutOutTile"/> - no longer every tile of an alpha page).
            /// </summary>
            public bool RoofOnPicture(int range, Vector3 a, Vector3 b, Vector3 c) =>
                RoofsFromPicture && UpFacing(a, b, c) && !CutOutRange(range);

            /// <summary>Play-test 2026-09-29: an upward atlas face that stays on the atlas ONLY because its tile is cut out
            /// (leaf cards, grates, catwalks - on the opaque picture it would draw as a solid quad). Counted by the roof
            /// pass for the build line, so the exclusion's share is on record.</summary>
            public bool RoofKeptCutOut(int range, Vector3 a, Vector3 b, Vector3 c) =>
                RoofsFromPicture && UpFacing(a, b, c) && CutOutRange(range);

            /// <summary>Whether range <paramref name="range"/>'s tile is genuinely cut out (<see cref="TileStore.CutOutTile"/>).</summary>
            private bool CutOutRange(int range) => Tiles != null && Tiles.CutOutTile(TileOfRange(range));

            /// <summary>n.y &gt;= <see cref="RoofNormalY"/> of the unit normal. No square root and no allocation: n.y &gt;= k|n|
            /// with k &gt; 0 is n.y &gt; 0 and n.y^2 &gt;= k^2 |n|^2 - this runs over every atlas triangle of the map.</summary>
            private static bool UpFacing(Vector3 a, Vector3 b, Vector3 c)
            {
                var n = Vector3.Cross(b - a, c - a);
                var squared = n.sqrMagnitude;

                if (!(squared > 1e-12f) || !(n.y > 0f)) return false;

                return n.y * n.y >= RoofNormalY * RoofNormalY * squared;
            }

            /// <summary>The raw material UV of vertex <paramref name="i"/> of the building last loaded, in range
            /// <paramref name="k"/> - see <see cref="AtlasUvOf"/>.</summary>
            public Vector2 AtlasUv(int i, int k) => AtlasUvOf(_building, i, _ranges[k]);

            // Scratch, per worker. Grown, never shrunk.
            private bool[] _finite = new bool[0];
            private Vector3[] _positions = new Vector3[0];
            private int _count;

            public bool Finite(int i) => _finite[i];
            public Vector3 Position(int i) => _positions[i];

            /// <summary>The planar UV of a world point: where it falls across the extent, which is where it
            /// falls across the picture. CLAMPED, because the grid overhangs by up to half a cell and a
            /// building's triangles are kept up to a metre outside the extent.</summary>
            public Vector2 PlanarUv(float x, float z) =>
                new Vector2(
                    Mathf.Clamp01((x - (float)File.MinX) / SpanX),
                    Mathf.Clamp01((z - (float)File.MinZ) / SpanZ));

            /// <summary>The band a building's declared level belongs to: its own where the file has that
            /// band, else the nearest.</summary>
            public int BandLevelFor(int level)
            {
                var best = int.MinValue;
                var distance = int.MaxValue;

                foreach (var band in File.Bands)
                {
                    if (band == null) continue;
                    if (band.Level == level) return level;

                    var gap = Math.Abs(band.Level - level);
                    if (gap >= distance) continue;

                    distance = gap;
                    best = band.Level;
                }

                return best;
            }

            /// <summary>Dequantises a building into the scratch and flags its finite vertices. A y stored as
            /// NoHit dequantises to NaN, and one NaN vertex in a merged bucket makes the whole bucket's bounds
            /// NaN - so every triangle touching one is dropped. False when no vertex is finite.</summary>
            public bool LoadBuilding(MapMeshFile.Building building)
            {
                var n = building.VertexCount;

                if (_finite.Length < n) _finite = new bool[n];
                if (_positions.Length < n) _positions = new Vector3[n];

                _count = n;
                _building = building;
                _ranges = AtlasRangesOf(building);

                if (_ranges != null)
                {
                    if (_rangeTile.Length < _ranges.Count) _rangeTile = new int[_ranges.Count];

                    for (var k = 0; k < _ranges.Count; k++)
                    {
                        var range = _ranges[k];
                        var present = Tiles != null && range.Page >= 0 && range.Page < MaxAtlasPages && PagePresent[range.Page];
                        _rangeTile[k] = present ? Tiles.TileOf(range) : -1;
                    }
                }

                _skirtBand = File.Band(BandLevelFor(building.Level));
                _minY = float.PositiveInfinity;

                var any = false;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                for (var i = 0; i < n; i++)
                {
                    var vertex = building.VertexAt(i);

                    _finite[i] = !float.IsNaN(vertex.x) && !float.IsInfinity(vertex.x) &&
                                 !float.IsNaN(vertex.y) && !float.IsInfinity(vertex.y) &&
                                 !float.IsNaN(vertex.z) && !float.IsInfinity(vertex.z);

                    _positions[i] = vertex;
                    any |= _finite[i];
                    if (_finite[i] && vertex.y < _minY) _minY = vertex.y;

                    if (!_finite[i]) continue;

                    min = Vector3.Min(min, vertex);
                    max = Vector3.Max(max, vertex);
                }

                // opt B: the renderer's size, over its finite vertices (NaN with none - SizeClassOf calls that normal)
                var size = max - min;
                LoadedExtent = any ? Mathf.Max(size.x, Mathf.Max(size.y, size.z)) : float.NaN;

                return any;
            }

            /// <summary>Opt B: the largest world extent (m) of the building last loaded, over its finite vertices; NaN with
            /// none. Its size class (<see cref="SizeClassOf"/>).</summary>
            public float LoadedExtent { get; private set; } = float.NaN;

            /// <summary>The relief of the band the building last loaded is filed in, or null. See GroundSkirt.</summary>
            private MapMeshFile.ReliefBand _skirtBand;

            /// <summary>The lowest finite vertex height of the building last loaded.</summary>
            private float _minY;

            /// <summary>
            /// Whether a triangle of the building last loaded is GROUND the building happens to own - a
            /// foundation skirt, a pavement apron, a kiosk's plinth - rather than a roof: near-horizontal
            /// (<c>|n.y| &gt;= 0.5</c>), at the building's base (centroid within <see cref="GroundSkirtRise"/> of its
            /// lowest vertex), and within <see cref="GroundSkirtTolerance"/> of the band's relief height under
            /// the centroid. The relief already draws that ground with the floor's own picture; the face drawn
            /// as well took a side view or a wall tint and smeared it over the street (the brown apron around
            /// the Bridge kiosk). The base test is what keeps real roofs: the top band's relief is measured from
            /// over the roofs, so a roof is ALSO within 0.3 m of its relief - but never within a metre of its
            /// building's foot. No relief under the centroid (a hole, off the grid) keeps the face.
            /// </summary>
            public bool GroundSkirt(Vector3 a, Vector3 b, Vector3 c)
            {
                if (_skirtBand == null) return false;

                var n = Vector3.Cross(b - a, c - a);
                var length = n.magnitude;
                if (!(length > 1e-6f) || Mathf.Abs(n.y) / length < RoofNormalY) return false;

                var y = (a.y + b.y + c.y) / 3f;
                if (!(y <= _minY + GroundSkirtRise)) return false;

                if (!_skirtBand.TryHeightAt((a.x + b.x + c.x) / 3f, (a.z + b.z + c.z) / 3f, out var ground)) return false;

                return Mathf.Abs(y - ground) <= GroundSkirtTolerance;
            }

            /// <summary>
            /// Which picture textures a building face: <see cref="TopView"/>, a side (slot + 1), or
            /// <see cref="TintView"/> - asked only after <see cref="AtlasRangeAt"/>, so the order is atlas, then this.
            /// WITHOUT side pictures, exactly the tint build's rule - top when <c>|n.y| &gt;= 0.5</c>
            /// (<see cref="IsRoof"/>), else tint. WITH them (WP8 D4): the TOP picture for every face with
            /// <c>n.y &gt;= 0.5</c> - roofs pitched up to 60 degrees, which the top camera sees unoccluded; the tint for
            /// an underside (<c>n.y &lt;= -0.35</c>); a SIDE picture for a wall only when its horizontal normal is within
            /// 40 degrees of facing that side's camera, the closest winning; else the tint (the building's own colour).
            /// The roof pass and the wall pass both ask here, so a triangle still lands in exactly one of them.
            /// Under <see cref="LegacyViewRule"/> the pre-WP8 contract: the picture whose camera looks most squarely at
            /// the face, scored <c>-dot(n, f)</c> with the top camera's f = (0,-1,0) among them; below
            /// <see cref="MinViewScore"/> a tint.
            /// </summary>
            public int ViewFor(Vector3 a, Vector3 b, Vector3 c)
            {
                if (!SidesActive) return IsRoof(a, b, c) ? TopView : TintView;

                var n = Vector3.Cross(b - a, c - a);
                var length = n.magnitude;

                if (!(length > 1e-6f)) return TopView;

                n /= length;

                if (!LegacyViewRule)
                {
                    if (n.y >= RoofNormalY) return TopView;
                    if (n.y <= -UndersideNormalY) return TintView;
                    if (!SideWalls) return TintView;

                    var h = new Vector2(n.x, n.z);
                    var hl = h.magnitude;
                    if (!(hl > 1e-4f)) return TintView;

                    h /= hl;

                    var wall = TintView;
                    var bestCos = SideMaxCosine;

                    for (var slot = 0; slot < Sides.Length; slot++)
                    {
                        var side = Sides[slot];
                        if (side == null) continue;

                        // The side camera's horizontal look, reversed: a wall faces the camera when its normal
                        // points back along it.
                        var d = new Vector2(-side.Forward.x, -side.Forward.z);
                        var dl = d.magnitude;
                        if (!(dl > 1e-6f)) continue;

                        var cos = Vector2.Dot(h, d / dl);
                        if (!(cos > bestCos)) continue;

                        bestCos = cos;
                        wall = slot + 1;
                    }

                    return wall;
                }

                var best = TopView;
                var bestScore = n.y;

                for (var slot = 0; slot < Sides.Length; slot++)
                {
                    var side = Sides[slot];
                    if (side == null) continue;

                    var score = -Vector3.Dot(n, side.Forward);
                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = slot + 1;
                }

                return bestScore < MinViewScore ? TintView : best;
            }

            /// <summary>The floor whose picture textures a top face at height <paramref name="y"/>: the floor
            /// it STANDS ON when its height is inside one floor's band (plus the slack), else
            /// <paramref name="filed"/>. Nearer floor wins an overlap; ties to the higher floor. See
            /// Built.RoofsOnOtherFloors for why.</summary>
            public int FloorForFace(float y, int filed)
            {
                var best = filed;
                var bestDistance = float.MaxValue;

                for (var i = 0; i < FloorRanges.Length; i++)
                {
                    var range = FloorRanges[i];
                    if (y < range.Low || y > range.High) continue;

                    var distance = Mathf.Max(0f, Mathf.Max(range.Low + FloorFaceSlack - y, y - (range.High - FloorFaceSlack)));

                    if (distance < bestDistance || (Mathf.Approximately(distance, bestDistance) && range.Level > best))
                    {
                        best = range.Level;
                        bestDistance = distance;
                    }
                }

                return best;
            }

            /// <summary>The triangle at <paramref name="i"/> of the building last loaded, if it is a usable
            /// WALL (a tint): in range, finite, and <see cref="ViewFor"/> says tint. The exact complement of
            /// what the roof pass keeps as top or side.</summary>
            public bool WallTriangle(MapMeshFile.Building building, int i, out Vector3 a, out Vector3 b, out Vector3 c)
            {
                a = b = c = Vector3.zero;

                var ia = building.Indices[i];
                var ib = building.Indices[i + 1];
                var ic = building.Indices[i + 2];

                if (ia >= building.VertexCount || ib >= building.VertexCount || ic >= building.VertexCount) return false;
                if (!_finite[ia] || !_finite[ib] || !_finite[ic]) return false;

                var pa = _positions[ia];
                var pb = _positions[ib];
                var pc = _positions[ic];

                // Ground a building owns is the relief's, in both passes: the roof pass skips it the same way.
                if (GroundSkirt(pa, pb, pc)) return false;

                // An atlas face is textured by its own material, never tinted - unless it is a roof on the top picture
                // (stage C), which the roof pass takes: ViewFor below then says TopView, so it is not a wall either.
                var range = AtlasRangeAt(i);
                if (range >= 0 && !RoofOnPicture(range, pa, pb, pc)) return false;

                a = pa;
                b = pb;
                c = pc;

                return ViewFor(a, b, c) == TintView;
            }

            /// <summary>
            /// The colour of the building last loaded: the picture under the middle of its footprint, a
            /// quarter darker and a little greyer - a 3x3 average around the centroid, ignoring transparent
            /// pixels. Row 0 of the readback is the picture's BOTTOM (MinZ), the planar UV's convention.
            /// </summary>
            public Color WallColour(Color32[] palette, int width, int height)
            {
                if (palette == null || width <= 0 || height <= 0) return FallbackWallColour;

                var sumX = 0d;
                var sumZ = 0d;
                var n = 0;

                for (var i = 0; i < _count; i++)
                {
                    if (!_finite[i]) continue;
                    sumX += _positions[i].x;
                    sumZ += _positions[i].z;
                    n++;
                }

                if (n == 0) return FallbackWallColour;

                var uv = PlanarUv((float)(sumX / n), (float)(sumZ / n));

                var cx = Mathf.Clamp(Mathf.RoundToInt(uv.x * (width - 1)), 0, width - 1);
                var cy = Mathf.Clamp(Mathf.RoundToInt(uv.y * (height - 1)), 0, height - 1);

                float r = 0f, g = 0f, bl = 0f;
                var taken = 0;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var y = cy + dy;
                    if (y < 0 || y >= height) continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = cx + dx;
                        if (x < 0 || x >= width) continue;

                        var pixel = palette[y * width + x];
                        if (pixel.a < 128) continue;

                        r += pixel.r;
                        g += pixel.g;
                        bl += pixel.b;
                        taken++;
                    }
                }

                if (taken == 0) return FallbackWallColour;

                var average = new Color(r / (255f * taken), g / (255f * taken), bl / (255f * taken), 1f);

                Color.RGBToHSV(average, out var hue, out var saturation, out var value);

                var tinted = Color.HSVToRGB(hue, saturation * WallSaturation, value * WallValue);
                tinted.a = 1f;

                return tinted;
            }
        }

        // --- the mesh file's atlas data (format v2) - the ONE place the viewer reads it --------------------

        /// <summary>
        /// A building's atlas ranges, or null when it has none: a building whose materials were not captured, or
        /// one with ranges but no UVs. WORKER-safe: plain reads of the parsed file, which MapMeshFile.Read has
        /// already validated (ranges ascending, whole triangles, inside the building, on a page the file has, tile
        /// rects on the page, finite bounds, every vertex in at most one range).
        ///
        /// With <see cref="AtlasUvOf"/> and <see cref="TileStore.For"/>, the only places the viewer reads MapMeshFile
        /// format v3's atlas data.
        /// </summary>
        private static List<MapMeshFile.AtlasRange> AtlasRangesOf(MapMeshFile.Building building)
        {
            var ranges = building.Ranges;
            if (ranges == null || ranges.Count == 0 || building.U == null || building.V == null) return null;

            return ranges;
        }

        /// <summary>
        /// Vertex <paramref name="i"/>'s RAW material UV (format v3), dequantised over the bounds of the one range
        /// that uses it: <c>u = UMin + code / 65535 * (UMax - UMin)</c> (MapMeshFile.AtlasRange.U), in repeats of
        /// the range's tile. Drawn on the tile's OWN texture with wrapMode Repeat, so 17.25 samples the tile at
        /// 0.25 - the GPU does the repeating a page never could. No flip: v = 0 is the tile's bottom row, which is
        /// where a texture's v = 0 is, and the tile was cut from the page bottom-up (TileStore.Cut).
        /// </summary>
        private static Vector2 AtlasUvOf(MapMeshFile.Building building, int i, MapMeshFile.AtlasRange range) =>
            new Vector2(building.UOf(i, range), building.VOf(i, range));

        /// <summary>The worker's snapshot of this view, with fresh scratch. Main thread only.</summary>
        private Prep SnapshotPrep()
        {
            var prep = new Prep
            {
                File = _file,
                MapKey = _mapKey,
                Flat = _flatColours,
                SidesActive = SidesActive,
                RoofsFromPicture = RoofsActive,
                FloorRanges = _floorRanges.ToArray(),
                SpanX = (float)(_file.MaxX - _file.MinX),
                SpanZ = (float)(_file.MaxZ - _file.MinZ)
            };

            for (var slot = 0; slot < _sides.Length; slot++) prep.Sides[slot] = _sides[slot];

            // compact vertices only where the GPU takes the formats (asked here, on the main thread)
            prep.Compact = CompactVertices && CompactSupported();

            if (SpatialChunkMetres > 0f)
            {
                prep.CellsX = Mathf.Max(1, Mathf.CeilToInt(prep.SpanX / SpatialChunkMetres));
                prep.CellsZ = Mathf.Max(1, Mathf.CeilToInt(prep.SpanZ / SpatialChunkMetres));
            }

            if (AtlasActive && _heldTiles != null)
            {
                prep.Tiles = _heldTiles;
                for (var page = 0; page < _pages.Length; page++) prep.PagePresent[page] = _pages[page] != null;

                // play-test 2026-09-29: which alpha-page tiles are really cut out, measured once per store on a worker;
                // PrepareFloor and PrepareWalls wait for it, so both passes route on the same answer
                if (prep.RoofsFromPicture) _heldTiles.EnsureOpacity();
            }

            return prep;
        }

        /// <summary>WORKER: one floor, prepared. Cancellable between chunks and buildings - a cancelled job
        /// throws OperationCanceledException and its output is never made.</summary>
        private static FloorData PrepareFloor(Prep p, int level, CancellationToken cancel)
        {
            var band = p.File.Band(level);
            var data = new FloorData { Level = level, Cells = band?.CellCount ?? 0L };

            // play-test 2026-09-29: the roof routing reads the tiles' measured opacity (Prep.RoofOnPicture)
            if (p.RoofsFromPicture) p.Tiles?.WaitOpacity(cancel);

            if (band != null && p.SpanX > 0f && p.SpanZ > 0f)
            {
                PrepareGround(p, band, data, cancel);
                PrepareBuildings(p, level, data, cancel);
            }

            // the merge rule: a tile with little on this floor is one chunk, not one per cell - per size class, so a small
            // prop's chunk is never merged into one that is always drawn. The roofs and sides keep their ordinary chunks as
            // they were and fold only their small-class ones (opt B), so the size split adds at most a chunk or two per list
            // where little is small. Per (destination, cell) it makes one SINK per size class: two for a roof (never a small
            // prop), three for a side, an atlas tile or a wall tint (normal, small building, small prop). A sink is not one
            // chunk: the vertex-cap Flush() in PrepareBuildings and PrepareWalls can cut a full sink into several.
            var total = data.Ground.Count + data.RoofsElsewhere.Count;

            data.ChunksSplit = total;
            data.ChunksMerged = total;

            void Merge(List<MeshData> chunks, bool smallOnly)
            {
                if (chunks == null) return;

                data.ChunksSplit += chunks.Count;
                MergeSmall(chunks, smallOnly);
                data.ChunksMerged += chunks.Count;
            }

            Merge(data.Roofs, true);
            foreach (var side in data.Sides) Merge(side, true);
            foreach (var pair in data.Atlas) Merge(pair.Value, false);

            if (p.Compact) PackAll(data, cancel);

            return data;
        }

        /// <summary>
        /// WORKER. The merge rule (<see cref="CellSplitMinVertices"/>): one tile's (or one wall tint's) chunks on a floor,
        /// put back into ONE chunk when they come to no more than that many vertices together - a draw call per cell is
        /// not worth it for a few small props. Concatenated as they are (normals made, UVs unshifted, before Pack), so
        /// nothing about a face changes; only its draw call does. Under the rollback, or with one chunk, nothing to do.
        /// </summary>
        private static void MergeSmall(List<MeshData> chunks, bool smallOnly = false)
        {
            if (SpatialChunkMetres <= 0f || CellSplitMinVertices <= 0 || chunks == null || chunks.Count <= 1) return;

            // Opt B: per size class (MeshData.SizeClass) - a merged chunk is one class, so Submit can still skip or unshadow it
            // whole. smallOnly leaves the ordinary class as it is (the roofs and sides, which were never merged).
            var mixed = false;

            foreach (var chunk in chunks)
                mixed |= (chunk?.SizeClass ?? SizeClassNormal) != (chunks[0]?.SizeClass ?? SizeClassNormal);

            if (!mixed)
            {
                if (!smallOnly || (chunks[0]?.SizeClass ?? SizeClassNormal) != SizeClassNormal) MergeRun(chunks);
                return;
            }

            var runs = new List<MeshData>[SizeClassSmallProp + 1];

            foreach (var chunk in chunks)
            {
                var size = chunk?.SizeClass ?? SizeClassNormal;
                (runs[size] ??= new List<MeshData>()).Add(chunk);
            }

            chunks.Clear();

            for (var size = 0; size < runs.Length; size++)
            {
                var run = runs[size];
                if (run == null) continue;

                if (!smallOnly || size != SizeClassNormal) MergeRun(run);
                chunks.AddRange(run);
            }
        }

        /// <summary>WORKER. <see cref="MergeSmall"/>'s merge of one run of chunks of ONE size class: all into one chunk when
        /// they come to no more than <see cref="CellSplitMinVertices"/> vertices.</summary>
        private static void MergeRun(List<MeshData> chunks)
        {
            if (chunks.Count <= 1) return;

            var vertices = 0;
            var indices = 0;
            var colours = true;

            foreach (var chunk in chunks)
            {
                if (chunk?.Vertices == null || chunk.Normals == null || chunk.Uvs == null || chunk.Indices == null) return;

                vertices += chunk.Vertices.Length;
                indices += chunk.Indices.Length;
                colours &= chunk.Colours != null;
            }

            if (vertices > CellSplitMinVertices) return;

            var merged = new MeshData
            {
                Name = chunks[0].Name + "-merged",
                Vertices = new Vector3[vertices],
                Normals = new Vector3[vertices],
                Uvs = new Vector2[vertices],
                Indices = new int[indices],
                Colours = colours ? new Color32[vertices] : null,
                RepeatUvs = chunks[0].RepeatUvs,
                SizeClass = chunks[0].SizeClass
            };

            var v = 0;
            var k = 0;

            foreach (var chunk in chunks)
            {
                var n = chunk.Vertices.Length;

                Array.Copy(chunk.Vertices, 0, merged.Vertices, v, n);
                Array.Copy(chunk.Normals, 0, merged.Normals, v, n);
                Array.Copy(chunk.Uvs, 0, merged.Uvs, v, Math.Min(n, chunk.Uvs.Length));
                if (colours) Array.Copy(chunk.Colours, 0, merged.Colours, v, Math.Min(n, chunk.Colours.Length));

                for (var i = 0; i < chunk.Indices.Length; i++) merged.Indices[k++] = chunk.Indices[i] + v;

                v += n;
                merged.PropExtent = Mathf.Max(merged.PropExtent, chunk.PropExtent);
            }

            chunks.Clear();
            chunks.Add(merged);
        }

        /// <summary>WORKER. Every chunk of a floor in its compact form (<see cref="MeshData.Pack"/>): here, off the main
        /// thread, so an upload is a buffer copy.</summary>
        private static void PackAll(FloorData data, CancellationToken cancel)
        {
            foreach (var mesh in data.Ground) PackOne(mesh, cancel);
            foreach (var mesh in data.Roofs) PackOne(mesh, cancel);
            foreach (var roof in data.RoofsElsewhere) PackOne(roof.Data, cancel);

            foreach (var side in data.Sides)
            {
                if (side == null) continue;
                foreach (var mesh in side) PackOne(mesh, cancel);
            }

            foreach (var pair in data.Atlas)
                foreach (var mesh in pair.Value)
                    PackOne(mesh, cancel);
        }

        /// <summary>WORKER. One chunk packed, cancellable between chunks.</summary>
        private static void PackOne(MeshData mesh, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            mesh?.Pack();
        }

        /// <summary>
        /// WORKER. One band's ground: a quad per cell whose four CORNERS - the four neighbouring cell centres -
        /// were all measured; a cell with no hit is a hole and stays one. Vertices are the cell centres in world
        /// metres, row 0 at MinZ (world order, not the picture's), so the planar UV lands the picture the right
        /// way up. Chunked in square blocks (<see cref="PrepareGroundBlocks"/>) under <see cref="SpatialChunkMetres"/>, else
        /// in rows sharing one row with the next chunk, each chunk under <see cref="ReliefVertexCap"/> vertices. Wound
        /// (a, c, b) / (b, c, d) so faces point up - the phase 3-0 winding that rendered lit.
        /// </summary>
        private static void PrepareGround(Prep p, MapMeshFile.ReliefBand band, FloorData data, CancellationToken cancel)
        {
            if (band.Width < 2 || band.Height < 2) return;

            if (SpatialChunkMetres > 0f)
            {
                PrepareGroundBlocks(p, band, data, cancel);
                return;
            }

            var rowsPerChunk = Mathf.Clamp(ReliefVertexCap / Mathf.Max(1, band.Width), 2, band.Height);

            var map = new int[band.Width * rowsPerChunk];

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = p.Flat ? new List<Color32>() : null;

            for (var first = 0; first < band.Height - 1; first += rowsPerChunk - 1)
            {
                var last = Mathf.Min(band.Height - 1, first + rowsPerChunk - 1);

                cancel.ThrowIfCancellationRequested();

                for (var i = 0; i < map.Length; i++) map[i] = -1;

                vertices.Clear();
                uvs.Clear();
                indices.Clear();
                colours?.Clear();

                for (var row = first; row < last; row++)
                {
                    for (var col = 0; col < band.Width - 1; col++)
                    {
                        // All four or none: three corners would be a notch, which reads as damage.
                        if (band.CodeAt(col, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col, row + 1) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row + 1) == MapMeshFile.NoHit) continue;

                        var a = Corner(p, band, col, row, 0, first, band.Width, map, vertices, uvs, colours);
                        var b = Corner(p, band, col + 1, row, 0, first, band.Width, map, vertices, uvs, colours);
                        var c = Corner(p, band, col, row + 1, 0, first, band.Width, map, vertices, uvs, colours);
                        var d = Corner(p, band, col + 1, row + 1, 0, first, band.Width, map, vertices, uvs, colours);

                        indices.Add(a);
                        indices.Add(c);
                        indices.Add(b);

                        indices.Add(b);
                        indices.Add(c);
                        indices.Add(d);
                    }
                }

                if (indices.Count > 0)
                {
                    data.Ground.Add(MeshData.From($"{p.MapKey}-relief-{band.Level}-{first}", vertices, uvs, indices, colours,
                        creases: false));
                    data.GroundTriangles += indices.Count / 3;
                    data.GroundTrianglesFull += indices.Count / 3;
                }

                if (last >= band.Height - 1) break;
            }
        }

        /// <summary>
        /// WORKER. <see cref="PrepareGround"/> in square blocks of about <see cref="SpatialChunkMetres"/> a side (256 cells at
        /// 0.5 m, held to (side + 2)^2 &lt;= <see cref="ReliefVertexCap"/>: 253 quads under sixteen-bit indices), so each
        /// block's bounds are a block and the frustum culls the ground the view does not show. Blocks step by
        /// <c>side</c> but each also draws ONE more quad row and column, overlapping the next block: the shared cell
        /// centres have the same height code and land on the same lattice point (<see cref="PositionQuantum"/>), and the
        /// overlap covers whatever sub-ulp gap two different matrices' rounding could still leave. The overlap draws the
        /// same picture at the same depth, so it cannot show; it is not counted in the triangle total.
        /// </summary>
        private static void PrepareGroundBlocks(Prep p, MapMeshFile.ReliefBand band, FloorData data, CancellationToken cancel)
        {
            var cell = band.CellMetres > 1e-4f ? band.CellMetres : 1f;
            var side = Mathf.Max(1, Mathf.RoundToInt(SpatialChunkMetres / cell));
            side = Mathf.Clamp(side, 1, (int)Math.Sqrt(ReliefVertexCap) - 2);

            // one quad of overlap each way: side + 1 quads, side + 2 vertices a row
            var stride = side + 2;
            var map = new int[stride * stride];

            // opt B: the block's heights for the simplifier (NaN = no hit), its scratch, and its triangles as vertex slots
            var heights = new float[stride * stride];
            var sizes = new int[(stride - 1) * (stride - 1)];
            var marks = new bool[stride * stride];
            var triangles = new List<int>();

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = p.Flat ? new List<Color32>() : null;

            for (var firstRow = 0; firstRow < band.Height - 1; firstRow += side)
            {
                var lastRow = Mathf.Min(band.Height - 1, firstRow + side + 1);
                var ownRows = firstRow + side;

                for (var firstCol = 0; firstCol < band.Width - 1; firstCol += side)
                {
                    var lastCol = Mathf.Min(band.Width - 1, firstCol + side + 1);
                    var ownCols = firstCol + side;
                    var owned = 0;

                    cancel.ThrowIfCancellationRequested();

                    for (var i = 0; i < map.Length; i++) map[i] = -1;

                    vertices.Clear();
                    uvs.Clear();
                    indices.Clear();
                    colours?.Clear();

                    // Opt B: the block's quads through ReliefSimplifier - every whole quad (all four corners measured, as in
                    // the strips) as before, or merged leaves where the ground is flat to ReliefSimplifyTolerance. Its
                    // vertices are the block's own grid points, so Corner places them exactly as it always has: same heights,
                    // same lattice points, same planar UVs (linear in x and z, so a merged quad samples the picture exactly as
                    // the quads it replaces did), and the flat-colour build's one FlatGroundColour on every vertex - there is
                    // no colour boundary for a merge to cross.
                    var quadsW = lastCol - firstCol;
                    var quadsH = lastRow - firstRow;
                    var width = quadsW + 1;

                    for (var z = 0; z <= quadsH; z++)
                    {
                        for (var x = 0; x <= quadsW; x++)
                        {
                            var code = band.CodeAt(firstCol + x, firstRow + z);
                            heights[z * width + x] = code == MapMeshFile.NoHit ? float.NaN : p.File.HeightOf(code);
                        }
                    }

                    // the overlap quad belongs to the next block: drawn here, counted there
                    owned = ReliefSimplifier.Build(heights, quadsW, quadsH, ownCols - firstCol, ownRows - firstRow,
                        ReliefSimplifyTolerance, ReliefLeafMaxQuads, sizes, marks, triangles, out var ownedFull);

                    for (var t = 0; t < triangles.Count; t++)
                    {
                        var slot = triangles[t];
                        indices.Add(Corner(p, band, firstCol + slot % width, firstRow + slot / width, firstCol, firstRow, stride, map,
                            vertices, uvs, colours));
                    }

                    data.GroundTrianglesFull += ownedFull;

                    if (indices.Count == 0) continue;

                    var block = MeshData.From(
                        string.Format(CultureInfo.InvariantCulture, "{0}-relief-{1}-r{2}c{3}", p.MapKey, band.Level, firstRow, firstCol),
                        vertices, uvs, indices, colours, creases: false);

                    // The normals from the WHOLE band, not the block: a block's own sum stops at its edge, so the two copies
                    // of an edge vertex would light differently and every block line would show under the lit fallback.
                    for (var slot = 0; slot < map.Length; slot++)
                    {
                        var vertex = map[slot];
                        if (vertex < 0) continue;

                        block.Normals[vertex] = ReliefNormal(p, band, firstCol + slot % stride, firstRow + slot / stride);
                    }

                    data.Ground.Add(block);
                    data.GroundTriangles += owned;
                }
            }
        }

        // BEGIN ReliefSimplifier - tools/check-relief-simplify.py compiles this region on its own: no Unity type in it.
        /// <summary>
        /// WORKER. Opt B: one relief block's triangles, simplified within a tolerance and free of cracks and T-junctions.
        ///
        /// Input: the block's vertex heights, (quadsW + 1) x (quadsH + 1) row-major, NaN where nothing was hit. A quad is WHOLE
        /// when its four corners were hit (all four or none, as ever). Output: triangles as vertex slots (z * (quadsW + 1) + x),
        /// wound as the relief always was - (a, c, b) / (b, c, d), a = (x, z), b = (x + 1, z), c = (x, z + 1) - so they face up.
        ///
        /// The rule, and why it holds:
        /// <list type="number">
        /// <item>Leaves. Every whole quad starts as a leaf of size 1. Bottom-up, for s = 2, 4, .. maxLeaf, an aligned s x s
        /// square becomes ONE leaf when its four s/2 children are leaves and all its (s + 1)^2 heights lie in a slab of
        /// thickness <c>tolerance</c> around their least-squares plane. Any triangulation of the leaf's grid points lies in
        /// that slab (a convex set), and so does the full-resolution surface, so the two differ by at most the tolerance.</item>
        /// <item>The ring. Leaves are only made inside quads [1, quadsW - 2] x [1, quadsH - 2]: the block's outer quad ring -
        /// its overlap quad, and the neighbour's overlap quad it starts on - is always full resolution, the same triangles
        /// in both blocks. So the line a block ends on is, in its neighbour, the edge of an unmerged quad strip: both blocks
        /// have every grid vertex along it, and the shared edges match exactly (no crack).</item>
        /// <item>No T-junction. A vertex is drawn only where it is the corner of a leaf (or a fan centre, strictly inside its
        /// leaf). A leaf of size 1 has no grid point inside its sides. A larger leaf collects every leaf corner lying on its
        /// perimeter - exactly the corners of the smaller neighbours along it - and, when there is any beyond its own four,
        /// is drawn as a fan from its centre through all of them; so every edge ends at every vertex lying on it, and
        /// two leaves sharing a stretch of side split it at the same points. No balance rule is needed.</item>
        /// </list>
        /// With tolerance 0 (or maxLeaf under 2) nothing merges: every whole quad, two triangles, as before.
        /// Returns the triangles owned (quads with x &lt; ownCols and z &lt; ownRows; the overlap quads are the next block's);
        /// <c>ownedFull</c> is what that was before simplification (two a whole owned quad).
        /// </summary>
        internal static class ReliefSimplifier
        {
            public static int Build(
                float[] heights, int quadsW, int quadsH, int ownCols, int ownRows, double tolerance, int maxLeaf,
                int[] sizes, bool[] marks, System.Collections.Generic.List<int> triangles, out int ownedFull)
            {
                triangles.Clear();
                ownedFull = 0;

                var width = quadsW + 1;
                var owned = 0;

                // --- sizes[quad]: a leaf's size at its origin quad, 0 for a quad inside a bigger leaf, -1 for a hole
                for (var z = 0; z < quadsH; z++)
                {
                    for (var x = 0; x < quadsW; x++)
                    {
                        var at = z * width + x;
                        var whole = !float.IsNaN(heights[at]) && !float.IsNaN(heights[at + 1]) &&
                                    !float.IsNaN(heights[at + width]) && !float.IsNaN(heights[at + width + 1]);

                        sizes[z * quadsW + x] = whole ? 1 : -1;
                        if (whole && x < ownCols && z < ownRows) ownedFull += 2;
                    }
                }

                // --- merge, bottom-up, inside the ring
                const int ring = 1;   // CHECK-MUTATE-RING

                if (tolerance > 0d)
                {
                    for (var s = 2; s <= maxLeaf; s *= 2)
                    {
                        var h = s / 2;

                        for (var z0 = ring; z0 + s - 1 <= quadsH - 1 - ring; z0 += s)
                        {
                            for (var x0 = ring; x0 + s - 1 <= quadsW - 1 - ring; x0 += s)
                            {
                                if (sizes[z0 * quadsW + x0] != h || sizes[z0 * quadsW + x0 + h] != h ||
                                    sizes[(z0 + h) * quadsW + x0] != h || sizes[(z0 + h) * quadsW + x0 + h] != h)
                                    continue;

                                if (!Flat(heights, width, x0, z0, s, tolerance)) continue;

                                sizes[z0 * quadsW + x0] = s;
                                sizes[z0 * quadsW + x0 + h] = 0;
                                sizes[(z0 + h) * quadsW + x0] = 0;
                                sizes[(z0 + h) * quadsW + x0 + h] = 0;
                            }
                        }
                    }
                }

                // --- every leaf's corners
                for (var i = 0; i < width * (quadsH + 1); i++) marks[i] = false;

                for (var z = 0; z < quadsH; z++)
                {
                    for (var x = 0; x < quadsW; x++)
                    {
                        var s = sizes[z * quadsW + x];
                        if (s < 1) continue;

                        marks[z * width + x] = true;
                        marks[z * width + x + s] = true;
                        marks[(z + s) * width + x] = true;
                        marks[(z + s) * width + x + s] = true;
                    }
                }

                // --- the triangles
                var perimeter = new System.Collections.Generic.List<int>();

                for (var z = 0; z < quadsH; z++)
                {
                    for (var x = 0; x < quadsW; x++)
                    {
                        var s = sizes[z * quadsW + x];
                        if (s < 1) continue;

                        var a = z * width + x;
                        var b = a + s;
                        var c = a + s * width;
                        var d = c + s;
                        var before = triangles.Count;

                        perimeter.Clear();

                        if (s > 1)
                        {
                            // clockwise seen from above (x right, z up), as a -> c -> d -> b: the winding of (a, c, b)
                            for (var k = 0; k < s; k++) Mark(perimeter, marks, a + k * width);          // up the left side
                            for (var k = 0; k < s; k++) Mark(perimeter, marks, c + k);                  // along the top
                            for (var k = 0; k < s; k++) Mark(perimeter, marks, d - k * width);          // down the right side
                            for (var k = 0; k < s; k++) Mark(perimeter, marks, b - k);                  // back along the bottom
                        }

                        if (perimeter.Count > 4)   // CHECK-MUTATE-FAN
                        {
                            var centre = (z + s / 2) * width + x + s / 2;

                            for (var k = 0; k < perimeter.Count; k++)
                            {
                                triangles.Add(centre);
                                triangles.Add(perimeter[k]);
                                triangles.Add(perimeter[(k + 1) % perimeter.Count]);
                            }
                        }
                        else
                        {
                            triangles.Add(a);
                            triangles.Add(c);
                            triangles.Add(b);

                            triangles.Add(b);
                            triangles.Add(c);
                            triangles.Add(d);
                        }

                        if (x < ownCols && z < ownRows) owned += (triangles.Count - before) / 3;
                    }
                }

                return owned;
            }

            private static void Mark(System.Collections.Generic.List<int> perimeter, bool[] marks, int slot)
            {
                if (marks[slot]) perimeter.Add(slot);
            }

            /// <summary>Whether the (s + 1)^2 heights of the square at (x0, z0) lie in a slab of thickness
            /// <paramref name="tolerance"/> around their least-squares plane.</summary>
            private static bool Flat(float[] heights, int width, int x0, int z0, int s, double tolerance)
            {
                var half = s / 2d;
                var n = (double)(s + 1) * (s + 1);
                double sum = 0d, sx = 0d, sz = 0d;

                for (var z = 0; z <= s; z++)
                {
                    for (var x = 0; x <= s; x++)
                    {
                        var y = (double)heights[(z0 + z) * width + x0 + x];
                        sum += y;
                        sx += y * (x - half);
                        sz += y * (z - half);
                    }
                }

                // sum over the grid of (x - s/2)^2 = (s + 1) * s (s + 1) (s + 2) / 12
                var sxx = (s + 1d) * s * (s + 1d) * (s + 2d) / 12d;
                var mean = sum / n;
                var gx = sx / sxx;
                var gz = sz / sxx;

                var lo = double.PositiveInfinity;
                var hi = double.NegativeInfinity;

                for (var z = 0; z <= s; z++)
                {
                    for (var x = 0; x <= s; x++)
                    {
                        var r = heights[(z0 + z) * width + x0 + x] - (mean + gx * (x - half) + gz * (z - half));
                        if (r < lo) lo = r;
                        if (r > hi) hi = r;
                        if (hi - lo > tolerance) return false;
                    }
                }

                return true;
            }
        }
        // END ReliefSimplifier

        /// <summary>
        /// WORKER. A relief vertex's normal from the band itself: the area-weighted sum of both triangles of each of the (up
        /// to four) whole quads around cell centre (<paramref name="col"/>, <paramref name="row"/>), wound as the relief is -
        /// so it is the same number in every block that holds a copy of the vertex. Straight up when no quad is whole.
        /// </summary>
        private static Vector3 ReliefNormal(Prep p, MapMeshFile.ReliefBand band, int col, int row)
        {
            var sum = Vector3.zero;

            for (var qr = row - 1; qr <= row; qr++)
            {
                for (var qc = col - 1; qc <= col; qc++)
                {
                    if (qc < 0 || qr < 0 || qc >= band.Width - 1 || qr >= band.Height - 1) continue;

                    var ca = band.CodeAt(qc, qr);
                    var cb = band.CodeAt(qc + 1, qr);
                    var cc = band.CodeAt(qc, qr + 1);
                    var cd = band.CodeAt(qc + 1, qr + 1);

                    if (ca == MapMeshFile.NoHit || cb == MapMeshFile.NoHit || cc == MapMeshFile.NoHit || cd == MapMeshFile.NoHit)
                        continue;

                    var a = new Vector3(band.CellCentreX(qc), p.File.HeightOf(ca), band.CellCentreZ(qr));
                    var b = new Vector3(band.CellCentreX(qc + 1), p.File.HeightOf(cb), band.CellCentreZ(qr));
                    var c = new Vector3(band.CellCentreX(qc), p.File.HeightOf(cc), band.CellCentreZ(qr + 1));
                    var d = new Vector3(band.CellCentreX(qc + 1), p.File.HeightOf(cd), band.CellCentreZ(qr + 1));

                    // triangles (a, c, b) and (b, c, d), as MeshData.NormalsOf sums them: cross(v1 - v0, v2 - v0)
                    sum += Vector3.Cross(c - a, b - a);
                    sum += Vector3.Cross(c - b, d - b);
                }
            }

            var length = sum.magnitude;
            return length > 1e-12f ? sum / length : Vector3.up;
        }

        /// <summary>One cell centre as a vertex, added on first use; the map is per CHUNK, <paramref name="stride"/>
        /// columns a row from (<paramref name="firstCol"/>, <paramref name="firstRow"/>).</summary>
        private static int Corner(
            Prep p, MapMeshFile.ReliefBand band, int col, int row, int firstCol, int firstRow, int stride, int[] map,
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colours)
        {
            var slot = (row - firstRow) * stride + (col - firstCol);
            var known = map[slot];
            if (known >= 0) return known;

            var x = band.CellCentreX(col);
            var z = band.CellCentreZ(row);
            var y = p.File.HeightOf(band.CodeAt(col, row));

            map[slot] = vertices.Count;

            vertices.Add(new Vector3(x, y, z));
            uvs.Add(p.PlanarUv(x, z));
            colours?.Add(FlatGroundColour);

            return map[slot];
        }

        /// <summary>
        /// WORKER. The buildings filed under this floor: every usable triangle to the top picture (a roof), a
        /// side picture, or a tint (only COUNTED here; the walls are built by <see cref="PrepareWalls"/>). Roof
        /// faces on another floor's height go to that floor's picture (<see cref="Prep.FloorForFace"/>).
        ///
        /// Two steps per building. First every triangle is classified and the SHARED-vertex ones are gathered per
        /// destination - one group per atlas tile, one per floor its roofs stand on (<see cref="BuildingGroups"/>) -
        /// with its spatial cell (<see cref="Prep.CellOf"/>). Then each group's normals are made ONCE, over the whole group
        /// with the crease rule (MeshData.From), and only then are its triangles dealt into the per-(group, cell) sinks
        /// with those normals. Made per chunk instead, the two copies of a vertex on a cell line would sum different faces
        /// and light differently - a seam along the grid on every smooth roof. The group is exactly what the file-order
        /// chunk used to see, so the normals are the ones before spatial chunking. Side faces use unshared vertices
        /// (per-face light), like the tints. Every chunk holds at most <see cref="ChunkVertexCap"/> vertices - a hard
        /// bound now, the crease split having happened before the cut.
        ///
        /// Opt B: every sink is also keyed by the renderer's size class (<see cref="SizeClassOf"/>), so a small prop's or a
        /// small building's faces land in chunks of their own that <see cref="Submit"/> can skip or draw without a shadow as
        /// a whole - a (tile, cell) chunk holds many renderers, and its bounds say nothing about any one of them. At most three
        /// chunks where there was one, and only where small renderers are; MergeSmall folds the light ones back per class.
        /// </summary>
        private static void PrepareBuildings(Prep p, int level, FloorData data, CancellationToken cancel)
        {
            if (p.File.Buildings == null || p.File.Buildings.Count == 0) return;

            var cap = ChunkVertexCap;
            var remap = new ChunkRemap();
            var groups = new BuildingGroups();

            // One per (tile, cell, size class) and per (floor the roof stands on, cell, size class), made on first use.
            var tileSinks = new Dictionary<(int Tile, int Cell, byte Size), GroupSink>();
            var roofSinks = new Dictionary<(int Floor, int Cell, byte Size), GroupSink>();

            // One per (side slot, cell, size class), made on the first face that side takes there.
            var sideSinks = new Dictionary<(int Slot, int Cell, byte Size), MeshSink>();

            foreach (var building in p.File.Buildings)
            {
                cancel.ThrowIfCancellationRequested();

                if (building == null || building.VertexCount == 0 || building.Indices == null) continue;
                if (p.BandLevelFor(building.Level) != level) continue;

                data.BuildingCount++;

                p.LoadBuilding(building);
                groups.Begin(building.VertexCount);

                // opt B: this renderer's size class - for its faces (maybe a small prop), and for its roofs (never a prop)
                var extent = p.LoadedExtent;
                var faceSize = SizeClassOf(extent, roof: false);
                var roofSize = SizeClassOf(extent, roof: true);

                // Three at a time; a triangle with an index past the building's own vertices is dropped - a
                // caller that trusts a file it did not write is a caller that throws inside a mesh build.
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count; i += 3)
                {
                    var ua = building.Indices[i];
                    var ub = building.Indices[i + 1];
                    var uc = building.Indices[i + 2];

                    // Range-checked as uint BEFORE the cast: an index past int.MaxValue must be refused, not
                    // wrapped negative into a valid-looking one.
                    if (ua >= building.VertexCount || ub >= building.VertexCount || uc >= building.VertexCount) continue;

                    var a = (int)ua;
                    var b = (int)ub;
                    var c = (int)uc;

                    if (!p.Finite(a) || !p.Finite(b) || !p.Finite(c))
                    {
                        data.Dropped++;
                        continue;
                    }

                    // Ground the building owns (a foundation skirt): the relief draws it with the floor's picture.
                    // Before the atlas, as in WallTriangle, so both passes still split every triangle once.
                    if (p.GroundSkirt(p.Position(a), p.Position(b), p.Position(c)))
                    {
                        data.GroundSkirtTriangles++;
                        continue;
                    }

                    var cell = p.CellOf(p.Position(a), p.Position(b), p.Position(c));

                    // An atlas face first: the game's own material, the file's own raw UVs - unless it is a roof and this
                    // build takes the roofs from the top picture (stage C): then it falls through to ViewFor, which sends
                    // it to the top picture like any roof without a tile. WallTriangle asks the same two questions.
                    var range = p.AtlasRangeAt(i);

                    if (range >= 0 && p.RoofOnPicture(range, p.Position(a), p.Position(b), p.Position(c)))
                    {
                        data.RoofPictureTriangles++;
                        range = -1;
                    }
                    else if (range >= 0 && p.RoofKeptCutOut(range, p.Position(a), p.Position(b), p.Position(c)))
                    {
                        // play-test 2026-09-29: the exclusion on record - it left 98 % of the roof area on the atlas when it
                        // was by page
                        data.RoofCutOutTriangles++;
                        data.RoofCutOutArea += TriangleArea(p.Position(a), p.Position(b), p.Position(c));
                    }

                    if (range >= 0)
                    {
                        groups.Add(p, false, p.TileOfRange(range), range, a, b, c, cell);
                        data.AtlasTriangles++;
                        data.AtlasArea += TriangleArea(p.Position(a), p.Position(b), p.Position(c));
                        continue;
                    }

                    var pa = p.Position(a);
                    var pb = p.Position(b);
                    var pc = p.Position(c);

                    var view = p.ViewFor(pa, pb, pc);
                    var area = TriangleArea(pa, pb, pc);

                    if (view == TintView)
                    {
                        data.WallTriangles++;
                        data.TintArea += area;

                        // WP8 (V.2): a WALL (not an underside) the rule left without a side picture.
                        if (p.SidesActive && !LegacyViewRule && area > 0d)
                        {
                            var ny = Vector3.Cross(pb - pa, pc - pa).y / (float)(2d * area);
                            if (ny > -UndersideNormalY && ny < RoofNormalY) data.WallsOutside++;
                        }

                        continue;
                    }

                    if (view != TopView)
                    {
                        var slot = view - 1;

                        if (!sideSinks.TryGetValue((slot, cell, faceSize), out var sink))
                        {
                            sink = new MeshSink
                            {
                                Flat = false,
                                Name = CellName(p, "side" + SideOrder[slot], level, cell) + SizeSuffix(faceSize),
                                Target = data.Sides[slot] ??= new List<MeshData>(),
                                SizeClass = faceSize
                            };
                            sideSinks[(slot, cell, faceSize)] = sink;
                        }

                        var side = p.Sides[slot];

                        if (sink.Count + 3 > cap) sink.Flush();

                        sink.Note(extent);

                        sink.Add(pa, SideUv(side, pa));
                        sink.Add(pb, SideUv(side, pb));
                        sink.Add(pc, SideUv(side, pc));

                        data.SideTriangles++;
                        data.SideArea += area;
                        continue;
                    }

                    data.TopTriangles++;
                    data.TopArea += area;

                    var floorLevel = p.FloorForFace((pa.y + pb.y + pc.y) / 3f, level);
                    if (floorLevel != level) data.MovedRoofTriangles++;

                    groups.Add(p, true, floorLevel, -1, a, b, c, cell);
                }

                // --- the shared-vertex groups: normals over the whole group, then dealt into their cells
                for (var g = 0; g < groups.Count; g++)
                {
                    cancel.ThrowIfCancellationRequested();

                    var group = groups[g];
                    var shaded = group.Shade();
                    var triangles = group.Cells.Count;

                    // a new vertex numbering (the group's, after the crease split): every chunk places it afresh
                    remap.Begin(shaded.Vertices.Length);

                    var size = group.Roof ? roofSize : faceSize;

                    for (var t = 0; t < triangles; t++)
                    {
                        var cell = group.Cells[t];
                        GroupSink sink;

                        if (group.Roof)
                        {
                            if (!roofSinks.TryGetValue((group.Key, cell, size), out sink))
                            {
                                var floor = group.Key;
                                var name = (floor == level
                                    ? CellName(p, "buildings", level, cell)
                                    : CellName(p, "buildings", level, cell) + "-on" + floor.ToString(CultureInfo.InvariantCulture)) +
                                    SizeSuffix(size);

                                sink = new GroupSink(remap, name, p.Flat, false, size, floor == level
                                    ? (Action<MeshData>)(mesh => data.Roofs.Add(mesh))
                                    : mesh => data.RoofsElsewhere.Add((floor, mesh)));
                                roofSinks[(group.Key, cell, size)] = sink;
                            }
                        }
                        else if (!tileSinks.TryGetValue((group.Key, cell, size), out sink))
                        {
                            var list = AtlasList(data, group.Key);
                            sink = new GroupSink(remap,
                                CellName(p, "tile" + group.Key.ToString(CultureInfo.InvariantCulture), level, cell) + SizeSuffix(size),
                                false, true, size, list.Add);
                            tileSinks[(group.Key, cell, size)] = sink;
                        }

                        if (sink.Count + 3 > cap)
                        {
                            // the roofs are counted as they are flushed, as FlushRoofs counted them
                            var flushed = sink.Flush();
                            if (group.Roof) data.BuildingTriangles += flushed;
                        }

                        sink.Triangle(shaded, t);
                        sink.Note(extent);
                    }
                }
            }

            foreach (var pair in sideSinks) pair.Value.Flush();
            foreach (var pair in tileSinks) pair.Value.Flush();

            // Counted into the building total whether or not they are built yet, so the log line's totals are
            // the file's and do not move when a floor's walls arrive a frame later. The roofs are counted as flushed.
            data.BuildingTriangles += data.WallTriangles + data.SideTriangles + data.AtlasTriangles;

            foreach (var pair in roofSinks) data.BuildingTriangles += pair.Value.Flush();
        }

        /// <summary>A chunk's mesh name: map, what it draws, the floor, and - when there is a grid - the spatial cell.</summary>
        private static string CellName(Prep p, string what, int level, int cell) =>
            p.CellsX * p.CellsZ > 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}-c{3}", p.MapKey, what, level, cell)
                : string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}", p.MapKey, what, level);

        /// <summary>
        /// WORKER. One building's shared-vertex triangles, gathered per destination before they are cut into cells: a
        /// group per atlas tile and per floor its roofs stand on, each with the building vertices it uses renumbered
        /// densely (one <see cref="ChunkRemap"/>, a group being an owner), their UVs (the tile's raw UV, or the planar
        /// one), and each triangle's cell. Groups and their lists are pooled across buildings.
        /// </summary>
        private sealed class BuildingGroups
        {
            private readonly ChunkRemap _local = new ChunkRemap();
            private readonly List<Group> _pool = new List<Group>();
            private readonly Dictionary<(bool Roof, int Key), Group> _byKey = new Dictionary<(bool Roof, int Key), Group>();

            public int Count { get; private set; }

            public Group this[int i] => _pool[i];

            /// <summary>A new building of <paramref name="vertexCount"/> vertices: no group, no vertex placed.</summary>
            public void Begin(int vertexCount)
            {
                _local.Begin(vertexCount);
                _byKey.Clear();

                for (var i = 0; i < Count; i++) _pool[i].Clear();
                Count = 0;
            }

            /// <summary>Triangle (a, b, c) of the building last loaded into the group of (<paramref name="roof"/>,
            /// <paramref name="key"/>): a tile index, or the floor level a roof stands on. <paramref name="range"/> is the
            /// atlas range whose UVs the vertices take (a vertex is in at most one range, so the first use's is every use's).</summary>
            public void Add(Prep p, bool roof, int key, int range, int a, int b, int c, int cell)
            {
                if (!_byKey.TryGetValue((roof, key), out var group))
                {
                    if (Count == _pool.Count) _pool.Add(new Group());

                    group = _pool[Count++];
                    group.Roof = roof;
                    group.Key = key;
                    group.Owner = _local.NewOwner();
                    _byKey[(roof, key)] = group;
                }

                group.Indices.Add(Vertex(p, group, range, a));
                group.Indices.Add(Vertex(p, group, range, b));
                group.Indices.Add(Vertex(p, group, range, c));
                group.Cells.Add(cell);
            }

            private int Vertex(Prep p, Group group, int range, int i)
            {
                if (_local.TryGet(i, group.Owner, out var index)) return index;

                var position = p.Position(i);

                index = group.Positions.Count;
                _local.Set(i, group.Owner, index);

                group.Positions.Add(position);
                group.Uvs.Add(range >= 0 ? p.AtlasUv(i, range) : p.PlanarUv(position.x, position.z));

                return index;
            }
        }

        /// <summary>One destination's triangles of one building (<see cref="BuildingGroups"/>).</summary>
        private sealed class Group
        {
            public bool Roof;
            public int Key;
            public int Owner;
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Indices = new List<int>();

            /// <summary>Per triangle, its spatial cell.</summary>
            public readonly List<int> Cells = new List<int>();

            /// <summary>The group's normals made once, over all of it, with the crease rule: MeshData.From splits a vertex
            /// whose faces meet at a crease, and the returned mesh's triangle t is the group's triangle t.</summary>
            public MeshData Shade() => MeshData.From("", Positions, Uvs, Indices, null);

            public void Clear()
            {
                Positions.Clear();
                Uvs.Clear();
                Indices.Clear();
                Cells.Clear();
            }
        }

        /// <summary>
        /// One (group, cell) chunk while it is prepared: vertices taken from a shaded group (<see cref="Group.Shade"/>) with
        /// their normals, placed once per chunk through the shared <see cref="ChunkRemap"/> (a flush takes a new owner, so
        /// the building in hand is placed afresh), flushed into mesh data under the vertex cap.
        /// </summary>
        private sealed class GroupSink
        {
            private readonly ChunkRemap _remap;
            private readonly string _name;
            private readonly bool _flat;
            private readonly bool _repeat;
            private readonly byte _size;
            private readonly Action<MeshData> _target;
            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector3> _normals = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<Color32> _colours = new List<Color32>();
            private readonly List<int> _indices = new List<int>();
            private int _owner;
            private int _part;
            private float _extent;

            /// <param name="remap">The pass's remap.</param>
            /// <param name="name">The chunk name's stem.</param>
            /// <param name="flat">A flat-colour build: each vertex gets the flat building colour.</param>
            /// <param name="repeat">Atlas UVs, in repeats of a Repeat-wrapped tile (MeshData.RepeatUvs).</param>
            /// <param name="size">Opt B: the size class of every renderer this sink takes (MeshData.SizeClass).</param>
            /// <param name="target">Where each flushed chunk goes.</param>
            public GroupSink(ChunkRemap remap, string name, bool flat, bool repeat, byte size, Action<MeshData> target)
            {
                _remap = remap;
                _name = name;
                _flat = flat;
                _repeat = repeat;
                _size = size;
                _target = target;
                _owner = remap.NewOwner();
            }

            public int Count => _vertices.Count;

            /// <summary>Opt B: a renderer of largest extent <paramref name="extent"/> m has faces in this chunk.</summary>
            public void Note(float extent)
            {
                if (extent > _extent) _extent = extent;
            }

            /// <summary>Triangle <paramref name="t"/> of <paramref name="shaded"/>.</summary>
            public void Triangle(MeshData shaded, int t)
            {
                for (var k = 0; k < 3; k++)
                {
                    var id = shaded.Indices[t * 3 + k];

                    if (!_remap.TryGet(id, _owner, out var index))
                    {
                        index = _vertices.Count;
                        _remap.Set(id, _owner, index);

                        _vertices.Add(shaded.Vertices[id]);
                        _normals.Add(shaded.Normals[id]);
                        _uvs.Add(shaded.Uvs[id]);
                        if (_flat) _colours.Add(FlatBuildingColour);
                    }

                    _indices.Add(index);
                }
            }

            /// <summary>The chunk into mesh data, emptied for the next. Returns the triangles flushed.</summary>
            public int Flush()
            {
                var triangles = _indices.Count / 3;

                if (_indices.Count > 0)
                {
                    _target(new MeshData
                    {
                        Name = _name + "-" + (_part++).ToString(CultureInfo.InvariantCulture),
                        Vertices = _vertices.ToArray(),
                        Normals = _normals.ToArray(),
                        Uvs = _uvs.ToArray(),
                        Indices = _indices.ToArray(),
                        Colours = _flat ? _colours.ToArray() : null,

                        // drawn on the tile's own texture with wrapMode Repeat: Pack may shift these UVs by whole repeats
                        RepeatUvs = _repeat,
                        SizeClass = _size,
                        PropExtent = _extent
                    });
                }

                _extent = 0f;

                _vertices.Clear();
                _normals.Clear();
                _uvs.Clear();
                _colours.Clear();
                _indices.Clear();

                _owner = _remap.NewOwner();
                return triangles;
            }
        }

        /// <summary>A triangle's area, m2.</summary>
        private static double TriangleArea(Vector3 a, Vector3 b, Vector3 c) => 0.5d * Vector3.Cross(b - a, c - a).magnitude;

        /// <summary>WORKER. A floor's mesh list for one tile, made on first use.</summary>
        private static List<MeshData> AtlasList(FloorData data, int tile)
        {
            if (!data.Atlas.TryGetValue(tile, out var list))
            {
                list = new List<MeshData>();
                data.Atlas[tile] = list;
            }

            return list;
        }

        /// <summary>
        /// WORKER. Where each vertex of the building in hand has been placed, per chunk - for every sink that shares a
        /// building's vertices (roofs, atlas tiles). One per prep pass rather than one array per sink: with a sink per
        /// (tile, cell) there are thousands of sinks, and an index array the size of the largest building in each was
        /// gigabytes. A vertex's placements are a short chain (one per chunk the building's faces around it fell in),
        /// stamped per building, so <see cref="Begin"/> forgets everything without clearing. A chunk is an OWNER number;
        /// a flush takes a new one, so the flushed chunk's entries simply stop matching.
        /// </summary>
        private sealed class ChunkRemap
        {
            private int[] _stamp = new int[0];
            private int[] _head = new int[0];
            private int _building;

            private int[] _next = new int[1024];
            private int[] _ownerOf = new int[1024];
            private int[] _value = new int[1024];
            private int _used;

            private int _owners;

            /// <summary>A fresh owner number, for a new chunk.</summary>
            public int NewOwner() => ++_owners;

            /// <summary>A new building of <paramref name="vertexCount"/> vertices: no vertex is placed anywhere.</summary>
            public void Begin(int vertexCount)
            {
                if (_stamp.Length < vertexCount)
                {
                    _stamp = new int[vertexCount];
                    _head = new int[vertexCount];
                    _building = 0;
                }

                _building++;
                _used = 0;
            }

            public bool TryGet(int vertex, int owner, out int value)
            {
                if (_stamp[vertex] == _building)
                {
                    for (var e = _head[vertex]; e >= 0; e = _next[e])
                    {
                        if (_ownerOf[e] != owner) continue;

                        value = _value[e];
                        return true;
                    }
                }

                value = -1;
                return false;
            }

            public void Set(int vertex, int owner, int value)
            {
                if (_used == _next.Length)
                {
                    Array.Resize(ref _next, _used * 2);
                    Array.Resize(ref _ownerOf, _used * 2);
                    Array.Resize(ref _value, _used * 2);
                }

                var head = _stamp[vertex] == _building ? _head[vertex] : -1;

                _stamp[vertex] = _building;
                _next[_used] = head;
                _ownerOf[_used] = owner;
                _value[_used] = value;
                _head[vertex] = _used++;
            }
        }

        /// <summary>
        /// WORKER. One floor's walls: each walled building's colour from the palette (read back on the main
        /// thread before this started), the colours grouped into at most <see cref="MaxWallTints"/> buckets,
        /// and the geometry into each bucket with UNSHARED vertices - a corner shared by two walls at right
        /// angles would light both as if they faced the corner. Two walks, as before: colours first so they can
        /// be bucketed knowing all of them, then geometry. Null when the floor has no walls.
        /// </summary>
        private static WallData PrepareWalls(Prep p, int level, Color32[] palette, int width, int height, CancellationToken cancel)
        {
            if (!(p.SpanX > 0f) || !(p.SpanZ > 0f)) return null;

            // play-test 2026-09-29: WallTriangle asks RoofOnPicture too, and must get the roof pass's answer
            if (p.RoofsFromPicture) p.Tiles?.WaitOpacity(cancel);

            // --- walk 1: which buildings have walls, and in what colour
            var walled = new List<int>();
            var colours = new List<Color>();

            for (var index = 0; index < p.File.Buildings.Count; index++)
            {
                cancel.ThrowIfCancellationRequested();

                var building = p.File.Buildings[index];
                if (building == null || building.VertexCount == 0 || building.Indices == null) continue;
                if (p.BandLevelFor(building.Level) != level) continue;

                if (!p.LoadBuilding(building)) continue;

                var hasWall = false;
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count && !hasWall; i += 3)
                    if (p.WallTriangle(building, i, out _, out _, out _)) hasWall = true;

                if (!hasWall) continue;

                walled.Add(index);
                colours.Add(p.Flat ? (Color)FlatWallColour : p.WallColour(palette, width, height));
            }

            if (walled.Count == 0) return null;

            var data = new WallData();

            if (!p.Flat)
            {
                var sum = Color.black;
                for (var i = 0; i < colours.Count; i++) sum += colours[i];

                var average = sum / colours.Count;
                average.a = 1f;

                data.Average = average;
                data.HasAverage = true;
            }

            // --- the buckets
            var centres = new List<Color>();
            var bucketOf = BucketColours(colours, p.Flat ? 1 : MaxWallTints, centres);

            // One sink per (bucket, spatial cell), made on the bucket's first wall in that cell (Prep.CellOf).
            var cells = p.CellsX * p.CellsZ;
            var cap = ChunkVertexCap;
            var sinks = new Dictionary<long, MeshSink>();

            for (var k = 0; k < centres.Count; k++) data.Tints.Add((centres[k], new List<MeshData>()));

            data.TintCount = p.Flat ? 0 : centres.Count;

            // --- walk 2: the geometry
            for (var w = 0; w < walled.Count; w++)
            {
                cancel.ThrowIfCancellationRequested();

                var building = p.File.Buildings[walled[w]];
                if (!p.LoadBuilding(building)) continue;

                var k = bucketOf[w];
                var count = building.Indices.Length - building.Indices.Length % 3;

                // opt B: the renderer's size class, as PrepareBuildings keys its sinks (walls are never roofs)
                var extent = p.LoadedExtent;
                var size = SizeClassOf(extent, roof: false);

                for (var i = 0; i < count; i += 3)
                {
                    if (!p.WallTriangle(building, i, out var a, out var b, out var c)) continue;

                    var cell = p.CellOf(a, b, c);
                    var key = ((long)k * cells + cell) * 3 + size;

                    if (!sinks.TryGetValue(key, out var sink))
                    {
                        sink = new MeshSink
                        {
                            Flat = p.Flat,
                            Name = CellName(p, "walls", level, cell) + "-" + k.ToString(CultureInfo.InvariantCulture) + SizeSuffix(size),
                            Target = data.Tints[k].Meshes,
                            SizeClass = size
                        };
                        sinks[key] = sink;
                    }

                    if (sink.Count + 3 > cap) sink.Flush();

                    sink.Note(extent);

                    sink.Add(a, p.PlanarUv(a.x, a.z));
                    sink.Add(b, p.PlanarUv(b.x, b.z));
                    sink.Add(c, p.PlanarUv(c.x, c.z));

                    data.Triangles++;
                }
            }

            foreach (var pair in sinks) pair.Value.Flush();

            // the merge rule, per tint, as per tile in PrepareFloor
            foreach (var tint in data.Tints)
            {
                data.ChunksSplit += tint.Meshes.Count;
                MergeSmall(tint.Meshes);
                data.ChunksMerged += tint.Meshes.Count;
            }

            // compact here, on the worker, as PrepareFloor's chunks are
            if (p.Compact)
            {
                foreach (var tint in data.Tints)
                    foreach (var mesh in tint.Meshes)
                        PackOne(mesh, cancel);
            }

            return data;
        }

        /// <summary>Unshared-vertex geometry while it is prepared, flushed into mesh data under the vertex cap:
        /// a tint's walls, or a side's faces, in one spatial cell, into <see cref="Target"/>.</summary>
        private sealed class MeshSink
        {
            public bool Flat;

            /// <summary>The chunk name's stem; each flush appends its part number.</summary>
            public string Name = "";

            /// <summary>The list each flushed chunk goes to.</summary>
            public List<MeshData> Target;

            /// <summary>Opt B: the size class of every renderer this sink takes (MeshData.SizeClass).</summary>
            public byte SizeClass;

            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<int> _indices = new List<int>();
            private readonly List<Color32> _colours = new List<Color32>();
            private int _part;
            private float _extent;

            public int Count => _vertices.Count;

            /// <summary>Opt B: a renderer of largest extent <paramref name="extent"/> m has faces in this chunk.</summary>
            public void Note(float extent)
            {
                if (extent > _extent) _extent = extent;
            }

            public void Add(Vector3 position, Vector2 uv)
            {
                _indices.Add(_vertices.Count);
                _vertices.Add(position);
                _uvs.Add(uv);
                if (Flat) _colours.Add(FlatWallColour);
            }

            public void Flush()
            {
                if (_indices.Count == 0 || Target == null) return;

                var data = MeshData.From(Name + "-" + (_part++).ToString(CultureInfo.InvariantCulture), _vertices, _uvs, _indices,
                    Flat ? _colours : null);

                data.SizeClass = SizeClass;
                data.PropExtent = _extent;
                Target.Add(data);

                _vertices.Clear();
                _uvs.Clear();
                _indices.Clear();
                _colours.Clear();
                _extent = 0f;
            }
        }

        /// <summary>What <see cref="Prep.ViewFor"/> answers for a face textured by the top-down picture.</summary>
        private const int TopView = 0;

        /// <summary>What <see cref="Prep.ViewFor"/> answers for a face drawn in a flat tint.</summary>
        private const int TintView = -1;

        /// <summary>The least score a face needs to be textured by the picture that sees it best (the pre-WP8 rule,
        /// <see cref="LegacyViewRule"/>).</summary>
        private const float MinViewScore = 0.35f;

        /// <summary>WP8 (D4 commit 4): a face whose normal points down more than this (n.y &lt;= -0.35) is an underside -
        /// a ceiling or a soffit - and takes the tint: no picture sees it.</summary>
        private const float UndersideNormalY = 0.35f;

        /// <summary>WP8 (D4 commit 4): a wall takes a side picture only when its horizontal normal is within 40 degrees
        /// (cosine 0.766) of facing that side's camera; beyond it the 45-degree oblique shows it as a streak.</summary>
        private const float SideMaxCosine = 0.766f;

        /// <summary>WP8 rollback: the pre-WP8 face rule (the picture that sees the face best, scored -dot(n, f), a tint
        /// under 0.35) - which gave pitched roofs to the side pictures and walls at any angle to them. Static readonly so
        /// the choice is not a constant the compiler folds.</summary>
        internal static readonly bool LegacyViewRule = false;

        /// <summary>WP8 rollback: walls take a side picture within 40 degrees. False: every wall without an atlas tile
        /// takes the tint.</summary>
        internal static readonly bool SideWalls = true;

        /// <summary>Pictures stage C rollback: false keeps every roof with an atlas tile on the atlas (raw albedo lit by the
        /// map's sun), whatever the setting says. Static readonly so the branch is not code the compiler folds away.</summary>
        internal static readonly bool RoofsFromPicture = true;

        /// <summary>Pictures stage C: the least capture density, px/m, at which a roof takes the top picture rather than its
        /// atlas tile. Under it (a 4 px/m capture: 0.25 m a pixel) a roof would be blurrier than the atlas's own texture.
        /// 6, not 8: menu sets now load an 8,192 px viewing copy, which puts Customs' ground at 7.16 px/m - under 8 its roofs
        /// fell back to the atlas. Rollback: 8.</summary>
        private const float RoofPictureMinPpm = 6f;

        /// <summary>WP8 (V.3) debug switch, OFF in every build: draws the building faces by SOURCE - atlas textured as
        /// usual, an atlas FLAT tile magenta, top-picture faces blue, side-picture faces orange, tints grey - and logs
        /// the camera's world position whenever it moves, so before/after screenshots can be matched to the metre.</summary>
        internal static readonly bool DebugFaceSources = false;

        /// <summary>WP8 (D5): corners of one vertex whose faces are within this cosine (45 degrees) share a smoothed
        /// normal; a vertex whose faces meet at a sharper crease is split, so a roof edge welded to its wall stops
        /// darkening a band along the roof. Rollback: -2 (never split - the pre-WP8 area-weighted average).</summary>
        internal static readonly float CreaseCosine = 0.707f;

        /// <summary>How close to vertical a face's normal has to be to count as a roof: cos 60 degrees.</summary>
        private const float RoofNormalY = 0.5f;

        /// <summary>How near the relief a near-horizontal building face has to be to count as ground. See
        /// Prep.GroundSkirt.</summary>
        private const float GroundSkirtTolerance = 0.3f;

        /// <summary>How far above its building's lowest vertex a face can sit and still be a ground skirt: well
        /// under a storey, so no roof - whose relief it also matches - ever qualifies.</summary>
        private const float GroundSkirtRise = 1f;

        /// <summary>Whether a triangle faces up or down enough to take the top-down picture, from its own
        /// cross product. |n.y|, not n.y: an overhang's underside is as flat as a roof. Degenerate goes with the
        /// roofs, where it draws nothing either way.</summary>
        private static bool IsRoof(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            var length = n.magnitude;

            if (!(length > 1e-6f)) return true;

            return Mathf.Abs(n.y) / length >= RoofNormalY;
        }

        /// <summary>A world point's UV on a side picture: the Stage U contract's pixel mapping (row 0 at the
        /// image TOP) turned into a texture's bottom-origin v - <c>v = (dot(u,p) - originU) * ppm / height</c>,
        /// the "height minus" and the "one minus" cancelling. Clamped.</summary>
        private static Vector2 SideUv(DynamicMapsLibrary.SidePicture side, Vector3 p)
        {
            var u = (Vector3.Dot(side.Right, p) - side.OriginR) * side.PxPerMetre / side.Width;
            var v = (Vector3.Dot(side.Up, p) - side.OriginU) * side.PxPerMetre / side.Height;

            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        /// <summary>
        /// MAIN THREAD. One mesh from prepared data: the packed form when the worker made one (<see cref="MakeCompactMesh"/>),
        /// else the float arrays (<see cref="MakeFloatMesh"/>). One mesh is one unit of paced work; with spatial chunks a unit
        /// is small (at most 65,535 vertices, most far fewer), so <see cref="Pump"/>'s budget check after each unit holds the
        /// frame to <see cref="FrameBudgetMs"/> plus one small unit - batching several meshes into a unit would only coarsen
        /// that, as each mesh's Unity cost is its own. The time is measured for the chunks line.
        /// </summary>
        private static Mesh MakeMesh(MeshData data)
        {
            var clock = Stopwatch.StartNew();

            try
            {
                var mesh = data.Packed != null ? MakeCompactMesh(data) : MakeFloatMesh(data);

                // opt B: a small chunk's class and largest prop go with the mesh, for Submit
                if (mesh != null && data.SizeClass != SizeClassNormal) Sizes.Add(mesh, new MeshSize(data.SizeClass, data.PropExtent));

                return mesh;
            }
            finally
            {
                // main thread only, like every caller: the chunks line's measured upload time
                _uploadTicks += clock.ElapsedTicks;
                _uploadMeshes++;
            }
        }

        /// <summary>Main-thread time spent in <see cref="MakeMesh"/> since the session began, in Stopwatch ticks, and the
        /// meshes it made. Static, because MakeMesh is; a build reads the difference over itself (Finish).</summary>
        private static long _uploadTicks;

        private static int _uploadMeshes;

        /// <summary>The vertex layouts of a compact mesh, by (colour, float UV): attributes in Unity's required order.</summary>
        private static readonly VertexAttributeDescriptor[][] CompactLayouts =
        {
            CompactLayout(false, false), CompactLayout(false, true), CompactLayout(true, false), CompactLayout(true, true)
        };

        private static VertexAttributeDescriptor[] CompactLayout(bool colour, bool uvFloat)
        {
            var layout = new List<VertexAttributeDescriptor>
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.UNorm16, 4),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4)
            };

            if (colour) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4));

            layout.Add(uvFloat
                ? new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
                : new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UNorm16, 2));

            return layout.ToArray();
        }

        /// <summary>
        /// MAIN THREAD, once per session: whether this GPU takes every format a compact mesh uses. Asked before a prep
        /// snapshot (<see cref="SnapshotPrep"/>), so a machine without them gets float meshes rather than invisible ones.
        /// </summary>
        private static bool CompactSupported()
        {
            if (_compactSupported.HasValue) return _compactSupported.Value;

            bool supported;

            try
            {
                supported = SystemInfo.SupportsVertexAttributeFormat(VertexAttributeFormat.UNorm16, 4) &&
                            SystemInfo.SupportsVertexAttributeFormat(VertexAttributeFormat.SNorm8, 4) &&
                            SystemInfo.SupportsVertexAttributeFormat(VertexAttributeFormat.UNorm8, 4) &&
                            SystemInfo.SupportsVertexAttributeFormat(VertexAttributeFormat.UNorm16, 2) &&
                            SystemInfo.SupportsVertexAttributeFormat(VertexAttributeFormat.Float32, 2);
            }
            catch (Exception)
            {
                supported = false;
            }

            _compactSupported = supported;

            if (!supported)
                Plugin.LogSource?.LogWarning(
                    "QuestTree: this GPU does not take the compact vertex formats (UNorm16/SNorm8) - the 3D map uses float vertices.");

            return supported;
        }

        private static bool? _compactSupported;

        /// <summary>
        /// MAIN THREAD. A compact mesh (<see cref="MeshData.Pack"/>): the packed buffer as it is, the indices in the format
        /// Pack chose, one submesh with the bounds Pack measured (in lattice units - DontRecalculateBounds, since the
        /// positions are codes Unity would have to decode), and its frame registered for <see cref="Submit"/>. Non-readable:
        /// nothing reads a mesh back.
        /// </summary>
        private static Mesh MakeCompactMesh(MeshData data)
        {
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds;

            var mesh = new Mesh { name = data.Name };
            var layout = CompactLayouts[(data.HasColour ? 2 : 0) + (data.UvFloat ? 1 : 0)];

            mesh.SetVertexBufferParams(data.VertexCount, layout);
            mesh.SetVertexBufferData(data.Packed, 0, 0, data.Packed.Length, 0, flags);

            int count;

            if (data.Indices16 != null)
            {
                count = data.Indices16.Length;
                mesh.SetIndexBufferParams(count, IndexFormat.UInt16);
                mesh.SetIndexBufferData(data.Indices16, 0, 0, count, flags);
            }
            else
            {
                count = data.Indices.Length;
                mesh.SetIndexBufferParams(count, IndexFormat.UInt32);
                mesh.SetIndexBufferData(data.Indices, 0, 0, count, flags);
            }

            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, count)
            {
                bounds = data.LocalBounds,
                firstVertex = 0,
                vertexCount = data.VertexCount
            }, flags);
            mesh.bounds = data.LocalBounds;

            mesh.UploadMeshData(true);

            Frames.Add(mesh, new MeshFrame(data.Origin, data.Scale));
            return mesh;
        }

        /// <summary>MAIN THREAD. A float mesh, as every mesh was before <see cref="CompactVertices"/>; sixteen-bit indices
        /// when <see cref="SixteenBitIndices"/> and the chunk is small enough, set BEFORE the triangles.</summary>
        private static Mesh MakeFloatMesh(MeshData data)
        {
            var sixteen = SixteenBitIndices && data.Vertices.Length <= SixteenBitVertexLimit;
            var mesh = new Mesh { name = data.Name, indexFormat = sixteen ? IndexFormat.UInt16 : IndexFormat.UInt32 };

            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            if (data.Colours != null) mesh.SetColors(data.Colours);
            mesh.SetTriangles(data.Indices, 0, calculateBounds: true);

            // Non-readable: nothing reads it back. The cut is the camera's near plane, so no CPU copy of any
            // mesh is kept, and the MeshData behind it is garbage once this unit returns.
            mesh.UploadMeshData(true);

            return mesh;
        }

        // --- the walls, started on the main thread, prepared on a worker, uploaded paced ----------------

        /// <summary>One floor's wall build in flight: its worker, then its upload units.</summary>
        private sealed class WallJob
        {
            public Built Built;
            public int Level;
            public bool Late;
            public Stopwatch Clock;
            public Task<WallData> Task;

            /// <summary>Finished - uploaded, failed or abandoned. The build line waits for the initial ones.</summary>
            public bool Done;
        }

        /// <summary>Wall builds this view started, in flight or uploading.</summary>
        private readonly List<WallJob> _wallJobs = new List<WallJob>();

        /// <summary>Cancels this view's wall workers; replaced after each <see cref="AbandonWalls"/>.</summary>
        private CancellationTokenSource _wallCancel;

        /// <summary>
        /// Starts one floor's walls, if they are waiting and there is a way to colour them: in flat colours at
        /// once, else once the floor's picture is resident. The palette is read back HERE (a GPU readback, main
        /// thread only, well under a millisecond at 256 columns); the rest goes to a worker.
        /// </summary>
        private void StartWalls(Built built, int level, Texture picture, bool late)
        {
            if (built == null || !built.WallsPending || built.WallsRunning) return;
            if (!_flatColours && picture == null) return;

            Color32[] palette = null;
            var width = 0;
            var height = 0;

            if (!_flatColours)
            {
                try
                {
                    palette = ReadPalette(picture, out width, out height);
                }
                catch (Exception ex)
                {
                    // Not fatal: every building then gets the fallback grey, and the walls still stand.
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: could not read the colours of floor {level} of '{_mapKey}' back from the " +
                        $"GPU ({ex.GetType().Name}: {ex.Message}) - its walls are drawn in one grey.");
                    palette = null;
                }
            }

            var prep = SnapshotPrep();
            built.WallsRunning = true;

            // The view's wall token: AbandonWalls cancels it, so a repaint stops the walls it abandons rather
            // than leaving the worker to finish into nothing.
            _wallCancel ??= new CancellationTokenSource();
            var token = _wallCancel.Token;

            _wallJobs.Add(new WallJob
            {
                Built = built,
                Level = level,
                Late = late,
                Clock = Stopwatch.StartNew(),
                Task = Task.Run(() => PrepareWalls(prep, level, palette, width, height, token), token)
            });
        }

        /// <summary>Wall workers that have finished: their meshes queued, or their failure handled.</summary>
        private void PollWallJobs()
        {
            for (var i = 0; i < _wallJobs.Count; i++)
            {
                var job = _wallJobs[i];
                if (job.Done || job.Task == null || !job.Task.IsCompleted) continue;

                var task = job.Task;
                job.Task = null;

                if (WasCancelled(task)) continue;   // abandoned: AbandonWalls already put the entry back to waiting

                if (task.IsFaulted)
                {
                    WallsFailed(job, task.Exception?.GetBaseException());
                    continue;
                }

                EnqueueWallUpload(job, task.Result);
            }

            // Finished jobs leave the list, so an idle view stops pumping. Backwards, to remove in place.
            for (var i = _wallJobs.Count - 1; i >= 0; i--)
                if (_wallJobs[i].Done) _wallJobs.RemoveAt(i);
        }

        /// <summary>
        /// A floor's prepared walls into the entry, paced: the tints (and their materials) are registered in
        /// the first unit, so a failure later finds every one; each mesh is a unit; the last unit finishes the
        /// job. Each unit catches its own failure, which costs this floor its walls and nothing else.
        /// </summary>
        private void EnqueueWallUpload(WallJob job, WallData data)
        {
            var built = job.Built;

            if (data == null)
            {
                _work.Enqueue(() => FinishWalls(job));
                return;
            }

            var tints = new WallTint[data.Tints.Count];

            _work.Enqueue(() => WallUnit(job, () =>
            {
                if (data.HasAverage)
                {
                    built.WallAverage = data.Average;
                    if (built.SideFallback != null) built.SideFallback.color = data.Average;
                }

                for (var k = 0; k < data.Tints.Count; k++)
                {
                    tints[k] = new WallTint
                    {
                        Colour = data.Tints[k].Colour,
                        Material = MakeWallMaterial(job.Level, k, data.Tints[k].Colour)
                    };

                    built.Walls.Add(tints[k]);
                }

                built.Tints = data.TintCount;
                built.WallChunksSplit = data.ChunksSplit;
                built.WallChunksMerged = data.ChunksMerged;
            }));

            for (var k = 0; k < data.Tints.Count; k++)
            {
                var bucket = k;

                foreach (var mesh in data.Tints[k].Meshes)
                {
                    var source = mesh;

                    _work.Enqueue(() => WallUnit(job, () =>
                    {
                        tints[bucket].Meshes.Add(MakeMesh(source));
                    }));
                }
            }

            _work.Enqueue(() => FinishWalls(job));
        }

        /// <summary>One wall unit, skipped once its job has failed or been abandoned, failing its job (not the
        /// map) when it throws.</summary>
        private void WallUnit(WallJob job, Action work)
        {
            if (job.Done) return;

            try
            {
                work();
            }
            catch (Exception ex)
            {
                WallsFailed(job, ex);
            }
        }

        /// <summary>The job's walls are all in: the entry stops waiting, and a floor whose picture came late
        /// says so in its own line (the build line has been written).</summary>
        private void FinishWalls(WallJob job)
        {
            if (job.Done) return;

            job.Done = true;
            job.Built.WallsRunning = false;
            job.Built.WallsPending = false;

            if (job.Late)
            {
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map walls for {0} floor {1} - {2:#,##0} triangles in {3} tint(s), built in " +
                    "{4:#,##0} ms (the floor's picture had just arrived).",
                    _mapKey, job.Level, job.Built.WallTriangles, job.Built.Tints, job.Clock.ElapsedMilliseconds));
            }
        }

        /// <summary>A wall build that failed: what it made goes, the entry stops waiting (once - a failure
        /// retried every frame is a warning every frame), and the floor keeps its roofs and ground.</summary>
        private void WallsFailed(WallJob job, Exception ex)
        {
            if (job.Done) return;

            job.Done = true;
            DestroyWalls(job.Built);
            job.Built.WallsRunning = false;
            job.Built.WallsPending = false;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the walls of floor {job.Level} of the 3D map for '{_mapKey}' could not be built " +
                $"({ex?.GetType().Name}: {ex?.Message}) - that floor draws its roofs and ground only.");
        }

        /// <summary>Whether a wall build started with the first build is still going - the build line waits
        /// for those, so its tint count is the map's.</summary>
        private bool InitialWallsRunning()
        {
            foreach (var job in _wallJobs)
                if (!job.Late && !job.Done) return true;

            return false;
        }

        /// <summary>
        /// Forgets every wall build of this view that has not finished. What an abandoned build had already
        /// registered is destroyed, and its entry goes back to WAITING - so the next view to draw it starts the
        /// walls again, and never inherits half of them. A worker still running is left to finish into
        /// nothing: its result is never collected.
        /// </summary>
        private void AbandonWalls()
        {
            foreach (var job in _wallJobs)
            {
                if (job.Done) continue;

                job.Done = true;
                DestroyWalls(job.Built);
                job.Built.WallsRunning = false;
                job.Built.WallsPending = true;
            }

            _wallJobs.Clear();

            if (_wallCancel != null)
            {
                _wallCancel.Cancel();
                _wallCancel = null;
            }
        }

        // --- the frame ------------------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_broke) return;

            try
            {
                // The atlas tiles, a few a frame, whatever else this frame is waiting for. Shared by every view of
                // the file and pumped once a frame however many views ask.
                _heldTiles?.Pump();

                if (_loading != null)
                {
                    if (!_loading.IsCompleted) return;

                    if (_loading.IsFaulted)
                    {
                        var reason = _loading.Exception?.GetBaseException();

                        // Past this machine's own bound (ViewerReadBound), not a broken file: said as what it is.
                        if (reason is MapMeshFile.ReaderBoundException over)
                        {
                            Plugin.LogSource?.LogWarning(string.Format(
                                CultureInfo.InvariantCulture,
                                "QuestTree: the 3D relief of '{0}' has more detail ({1:#,##0} {2}) than this graphics " +
                                "card can hold (bound {3:#,##0} triangles from {4:#,##0} MB of VRAM) - drawing the flat map.",
                                _mapKey, over.Count, over.Unit, _readBound, _readBoundVramMb));

                            _loading = null;
                            Refuse("has more detail than this graphics card can hold");
                            return;
                        }

                        // InvalidDataException is the format's own refusal - a truncated file, a bad
                        // magic, a count past the caps it enforces on read. One line and the flat
                        // picture, never a stack trace at the player.
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the 3D relief of '{_mapKey}' could not be read " +
                            $"({reason?.GetType().Name}: {reason?.Message}) - drawing the flat picture " +
                            $"instead.");

                        _loading = null;

                        // The exception TYPE, not its message: InvalidDataException is the format's own
                        // refusal and reads as "the file is not a mesh file", while an IOException is
                        // "the file could not be read at all" - two different things for a player to do
                        // about. The message itself can be a paragraph and this goes in a tooltip.
                        Refuse(reason is System.IO.InvalidDataException
                            ? "is not a readable relief file"
                            : "could not be read from disk");

                        return;
                    }

                    if (_built) return;

                    try
                    {
                        // Its own time counts toward the build's longest frame: the checks, one material pair
                        // per floor and the registration all run in this frame, before any pacing starts.
                        var start = Stopwatch.StartNew();
                        BeginBuild();
                        _longestFrameMs = Math.Max(_longestFrameMs, start.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        // A throw in the build leaves an empty viewport, which is the one outcome worse
                        // than 2D - so this one refuses the mesh rather than just going quiet.
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                            $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");

                        Refuse("could not be turned into meshes");
                    }

                    return;
                }

                // The workers preparing the floors: nothing to do until every one has landed - the backdrop shows.
                if (_held.Count > 0 && !_prepDone)
                {
                    for (var i = 0; i < _held.Count; i++)
                    {
                        var task = _held[i].Prep.Task;
                        if (!task.IsCompleted) return;

                        // Cancelled under us (a drop while this view waited): that level is asked for again.
                        if (WasCancelled(task))
                        {
                            // Cancelled by a cache DROP (the shared key is gone or another map's): every caller of
                            // DropCaches destroys this view first, so asking again would start a full-floor job
                            // for a view on its way out, keyed "|level" off the null key (review F27). Stopped.
                            if (_builtKey != _viewBuildKey)
                            {
                                _broke = true;
                                return;
                            }

                            var level = _held[i].Level;
                            ReleasePrep(_held[i].Prep);
                            _held[i] = (level, AcquirePrep(PrepKey(level), level, SnapshotPrep()));
                            return;
                        }

                        if (task.IsFaulted)
                        {
                            var reason = task.Exception?.GetBaseException();

                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                                $"({reason?.GetType().Name}: {reason?.Message}) - drawing the flat picture instead.");

                            Refuse("could not be turned into meshes");
                            return;
                        }
                    }

                    var prepared = new List<FloorData>();
                    foreach (var held in _held) prepared.Add(held.Prep.Task.Result);

                    // Collected, but STILL HELD until the build finishes: a repaint during the paced upload (which
                    // on a big map takes a second or two) then finds the finished job and only re-uploads from its
                    // arrays, instead of starting the worker again. Let go in Finish, or in ResetPipeline.
                    AfterPrepare(prepared);
                    if (_broke) return;
                }

                // Before anything is drawn this frame, so no mesh queued below belongs to the build being thrown
                // away. See RebuildWithoutFailedSides.
                if (_sideFailed) RebuildWithoutFailedSides();
                if (_broke) return;

                // Uploads, cuts and wall builds, paced - while the first build is on, and after it for walls
                // that arrive late.
                var pumped = false;
                if (!_ready || _work.Count > 0 || _wallJobs.Count > 0)
                {
                    Pump();
                    pumped = true;
                }

                if (_broke || !_ready) return;
                if (_camera == null || _floors.Count == 0) return;

                var first = _measureFirstFrame;
                var resized = EnsureRenderTexture();

                // HQ S1.1: nothing this frame shows has changed - the last image stays in the texture. The overlays
                // are placed regardless: they are cheap, and MapView may have added a pin.
                var tileVersion = _heldTiles?.Version ?? 0;
                _framesSeen++;

                var dirty = !RenderOnChange || _forceRender || first || pumped || resized || _unsettled ||
                            (_rt != null && !_rt.IsCreated()) ||
                            _renderedViewVersion != ViewVersion || !SameCut(_cutY, _renderedCutY) ||
                            tileVersion != _renderedTileVersion ||
                            SmallBuildingShadowsWanted != _smallShadows ||
                            (RenderHeartbeatFrames > 0 && _framesSeen % RenderHeartbeatFrames == 0);

                if (!dirty)
                {
                    PlaceOverlays();

                    // HQ S4: the view has been still for a while after a move - say where the camera is, once
                    if (++_cleanFrames == SettledFrames && _settledViewVersion != ViewVersion)
                    {
                        _settledViewVersion = ViewVersion;
                        SaySettled();
                    }

                    return;
                }

                _cleanFrames = 0;
                _forceRender = false;
                _unsettled = false;
                _renderedViewVersion = ViewVersion;
                _renderedCutY = _cutY;
                _renderedTileVersion = tileVersion;

                // opt B: the setting as this render reads it (a change re-renders above, never rebuilds), and its counts
                _smallShadows = SmallBuildingShadowsWanted;
                _smallHidden = 0;
                _shadowOff = 0;
                _framesRendered++;

                var clock = first ? Stopwatch.StartNew() : null;
                _drawCalls = 0;

                Place();

                // S1 review: no dome on a cut floor - the oblique near plane would clip its upper half to a hard edge
                if (SkyDome && float.IsNaN(_cutY)) DrawSky();

                // The first frame counts what frustum culling keeps of what it submits (CountInView), so the chunks line
                // proves the spatial chunking rather than asserting it.
                if (first)
                {
                    GeometryUtility.CalculateFrustumPlanes(_camera, _viewPlanes);
                    _viewTriangles = _viewTrianglesIn = 0L;
                    _viewDrawsIn = 0;
                    _countView = true;
                }

                try
                {
                    for (var i = 0; i < _floors.Count; i++) Draw(_floors[i]);
                }
                finally
                {
                    _countView = false;
                }

                RenderNow();
                Present();
                PlaceOverlays();

                if (first)
                {
                    // The CPU side of one frame: the DrawMesh submissions and the manual Render. What a map of
                    // this size costs to look at, frame after frame - the number that says whether it holds 60.
                    _measureFirstFrame = false;

                    // Stage C: the spot's cone, range, near plane, map size and biases are read BACK from the light, not
                    // taken from the values computed, so the line proves the setters took them (the near plane's
                    // inspector range is 0.1-10; the probe read it back at production distances too).
                    Plugin.LogSource?.LogInfo(string.Format(
                        CultureInfo.InvariantCulture,
                        "QuestTree: 3D map for {0} - first frame drawn in {1:0.0} ms, render {2:0.0} ms, {3} draw call(s), cut {4}, msaa {5}x, " +
                        "light {6} spot at {7:0} m, cone {8:0.0} deg over r {9:0} m, range {10:0} m, near {11:0} m (read back, near/far {31:0.0000}), map {12} px, " +
                        "bias {13:0.000}/{14:0.00}, atten x{15:0.00}; shadows {16} to {17:0} m; ground {18}, sides {19}; tonemap {32}; ambient {20}, sky {21}, " +
                        "pixel lights {22}, colour space {23}, exposure x{24:0.00} (white in the sun at {25:0.00}, ambient {26:0.00} of the sun), " +
                        "fog {27}; source {28}; post-processing {29}; probe {30}.",
                        _mapKey, clock.Elapsed.TotalMilliseconds, _renderMs, _drawCalls, CutText(), _rtSamples,
                        Lighting == ModSettings.MapLightMode.Sun
                            ? string.Format(CultureInfo.InvariantCulture, "sun ({0:0} up, azimuth {1:0}, colour {2:0.00}/{3:0.00}/{4:0.00})",
                                Plan.ElevationDegrees, Mathf.Atan2(Plan.SunDirection.x, Plan.SunDirection.z) * Mathf.Rad2Deg,
                                Plan.SunColour.r, Plan.SunColour.g, Plan.SunColour.b)
                            : string.Format(CultureInfo.InvariantCulture, "over the shoulder ({0:0} deg off the view, {1:0} down)", LightYawOffset, LightPitch),
                        _spotDistance,
                        _light != null ? _light.spotAngle : -1f, _spotRadius,
                        _light != null ? _light.range : -1f, _light != null ? _light.shadowNearPlane : -1f,
                        _light != null ? _light.shadowCustomResolution : -1,
                        _light != null ? _light.shadowBias : -1f, _light != null ? _light.shadowNormalBias : -1f,
                        _probe != null ? _probe.Attenuation : AttenuationNoProbe,
                        ShadowsText(), _spotShadowDistance,
                        GroundText(),   // stage B: "emission x{e}", or "lit fallback (why) r/g/b" - the division colour only where it is used
                        SidesText(),
                        DrawsProbe(Plan, Lighting == ModSettings.MapLightMode.Sun) ? "EFT probe" : AmbientTrilight ? "trilight" : "scene",
                        SkyDome && !_skyBroken ? "dome" : "backdrop",
                        QualitySettings.pixelLightCount, QualitySettings.activeColorSpace, _exposureRendered, WhiteAnchor,
                        // the ambient the view draws over the sun, as LightSource() reports it - not the preset's constant
                        AmbientTopFor(Plan, Lighting == ModSettings.MapLightMode.Sun).maxColorComponent /
                            Mathf.Max(0.0001f, Plan.SunIntensity * Plan.SunColour.maxColorComponent),
                        !AerialFog ? "off" : _fogDrawn ? "aerial" : "off for the cut floor",
                        LightSource(), Map3DPostProcess.Describe(), Map3DLightProbe.Describe(), _spotNearFarRatio,
                        TonemapText()));   // stage D: {32}

                    // Spatial chunking's proof: of the triangles submitted, those in meshes whose world bounds the frustum
                    // keeps (TestPlanesAABB, as Unity culls each DrawMesh), and the draw calls that reach the GPU; and the
                    // chunk count before and after the merge rule (walls only where they are built by now).
                    var split = 0;
                    var merged = 0;
                    var reliefFull = 0L;
                    var reliefDrawn = 0L;
                    var counted = new HashSet<Built>();

                    foreach (var floor in _floors)
                    {
                        var built = floor?.Meshes;
                        if (built == null || !counted.Add(built)) continue;

                        split += built.ChunksSplit + built.WallChunksSplit;
                        merged += built.ChunksMerged + built.WallChunksMerged;
                        reliefFull += built.GroundTrianglesFull;
                        reliefDrawn += built.GroundTriangles;
                    }

                    Plugin.LogSource?.LogInfo(string.Format(
                        CultureInfo.InvariantCulture,
                        "QuestTree: 3D map for {0} - first frame: {1:#,##0} of {2:#,##0} submitted triangles in the view frustum " +
                        "({3:0.0} %), {4:#,##0} of {5:#,##0} draw call(s) in view (Unity culls the rest); chunks on a {6} m grid " +
                        "{7:#,##0} split, {8:#,##0} after merging tiles/tints under {9:#,##0} vertices; relief {10:#,##0} -> {11:#,##0} " +
                        "triangles (simplified within {12} m); {13:#,##0} small-prop draw(s) hidden under {14} px; {15:#,##0} small-building " +
                        "draw(s) without a shadow (small buildings cast shadows: {16}).",
                        _mapKey, _viewTrianglesIn, _viewTriangles,
                        _viewTriangles > 0 ? 100d * _viewTrianglesIn / _viewTriangles : 0d,
                        _viewDrawsIn, _drawCalls, SpatialChunkMetres, split, merged, CellSplitMinVertices,
                        reliefFull, reliefDrawn, ReliefSimplifyTolerance, _smallHidden, SmallPropMinPixels, _shadowOff,
                        _smallShadows ? "on" : "off"));
                }

                // Spot-sun stage B's proof, after the real frame is in the view's texture (the check renders into a
                // temporary one): the ground once per build, the sides once per build until they pass, each when its picture is here
                if (_emissiveGround && (!_emissionChecked || !_sideChecked)) CheckPictureEmission();

                // Play-test 2026-09-29: the tonemap path proven on the REAL frame, not only on the calibration's quads
                if (_tonemapOn && !_frameChecked) CheckTonemapFrame();
            }
            catch (Exception ex)
            {
                // Once. A throw here would otherwise be a console line sixty times a second, and the
                // view goes quiet rather than noisy: the last frame stays in the texture.
                _broke = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view for '{_mapKey}' stopped drawing " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>
        /// Stage C: how far the map reaches from <paramref name="centre"/> towards the light - the largest projection of
        /// the eight corners of <see cref="_mapBounds"/> on <paramref name="dir"/>, at least <paramref name="radius"/> - so
        /// the shadow near plane is short of every caster, not just the fitted view's. The radius when there are no bounds.
        /// </summary>
        private float TowardsLight(Vector3 centre, Vector3 dir, float radius)
        {
            if (!_mapBoundsSet) return radius;

            var min = _mapBounds.min;
            var max = _mapBounds.max;
            var reach = radius;
            for (var k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? min.x : max.x, (k & 2) == 0 ? min.y : max.y, (k & 4) == 0 ? min.z : max.z);
                reach = Mathf.Max(reach, Vector3.Dot(corner - centre, dir));
            }

            return reach;
        }

        /// <summary>Stage C: the first-frame line's shadows clause for the last render - the mode drawn, or why none was:
        /// the setting, the cut floor (ShadowsUnderCut), or the spot's own reason (<see cref="_spotShadowsWhy"/>).</summary>
        private string ShadowsText()
        {
            if (_shadowsDrawn) return ShadowMode.ToString();
            if (ShadowMode == LightShadows.None) return "off (setting)";
            if (_spotShadowsWhy != null) return _spotShadowsWhy;
            return "off for the cut floor";
        }

        /// <summary>One DrawMesh for our camera, counted. Every mesh this view draws goes through here, so the
        /// first-frame line's draw-call count is the real one.</summary>
        /// <param name="mesh">The mesh.</param>
        /// <param name="material">Its material.</param>
        /// <param name="castShadows">Whether it casts shadows (it always receives them). The ground does not (review
        /// 2026-09-28): a bumpy height mesh at a shadow-map pixel of 0.3-0.6 m self-shadows into speckle, and its picture
        /// already carries the game's own shadows from above. Stage C: receiving costs the emission ground nothing (its
        /// albedo is black, so the spot's pass adds nothing to shadow); only the lit fallback's ground is shadowed.</param>
        private void Submit(Mesh mesh, Material material, bool castShadows = true)
        {
            // A compact mesh's positions are lattice steps from its origin: its frame turns them back into world metres. Every
            // other mesh (the calibration quads, a float chunk under the rollback) has none and draws with identity, as before.
            var matrix = Frames.TryGetValue(mesh, out var frame) ? frame.Matrix : Matrix4x4.identity;

            // Opt B: a small chunk (MeshData.SizeClass) - a small prop's skipped while its largest prop would be under
            // SmallPropMinPixels on screen, and any small one drawn without a shadow when the setting says so.
            if (!_plainSubmit && Sizes.TryGetValue(mesh, out var size))
            {
                if (size.Class == SizeClassSmallProp &&
                    TooSmallOnScreen(frame != null ? frame.World(mesh.bounds) : mesh.bounds, size.Extent))
                {
                    _smallHidden++;
                    return;
                }

                if (castShadows && !_smallShadows)
                {
                    castShadows = false;
                    _shadowOff++;
                }
            }

            if (castShadows) Graphics.DrawMesh(mesh, matrix, material, _drawLayer, _camera);
            else
                Graphics.DrawMesh(mesh, matrix, material, _drawLayer, _camera, 0, null, ShadowCastingMode.Off, true);
            _drawCalls++;

            if (_countView) CountInView(mesh, frame);
        }

        // --- opt B: small chunks ----------------------------------------------------------------------------------

        /// <summary>A small chunk's size class and the largest extent (m) of a renderer in it (<see cref="MeshData.SizeClass"/>).</summary>
        internal sealed class MeshSize
        {
            public readonly byte Class;
            public readonly float Extent;

            public MeshSize(byte sizeClass, float extent)
            {
                Class = sizeClass;
                Extent = extent;
            }
        }

        /// <summary>Every small chunk's <see cref="MeshSize"/>, keyed by its mesh; removed with the mesh, like
        /// <see cref="Frames"/>. Main thread only.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, MeshSize> Sizes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, MeshSize>();

        /// <summary>Set while <see cref="SubmitAll"/> draws a check's named meshes: Submit applies no opt B rule then.</summary>
        private bool _plainSubmit;

        /// <summary>"3D map: small buildings cast shadows" as the current render read it.</summary>
        private bool _smallShadows = true;

        /// <summary>This render's small-prop chunks skipped, and small chunks drawn without a shadow - for the first-frame line.</summary>
        private int _smallHidden;

        private int _shadowOff;

        /// <summary>
        /// Whether a prop <paramref name="extent"/> m across, anywhere in <paramref name="world"/>, comes to less than
        /// <see cref="SmallPropMinPixels"/> on screen: measured at the bounds' NEAREST point to the camera (perspective) or
        /// by the orthographic size, so the largest prop in the chunk at its largest - never hides one that would show.
        /// </summary>
        private bool TooSmallOnScreen(Bounds world, float extent)
        {
            if (SmallPropMinPixels <= 0f || _camera == null || !(extent > 0f)) return false;

            var pixels = _rt != null ? _rt.height : _camera.pixelHeight;
            if (pixels <= 0) return false;

            float metresPerPixel;

            if (_camera.orthographic)
            {
                metresPerPixel = 2f * _camera.orthographicSize / pixels;
            }
            else
            {
                var distance = Mathf.Sqrt(world.SqrDistance(_camera.transform.position));
                if (!(distance > 0f)) return false;

                metresPerPixel = 2f * distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / pixels;
            }

            return extent < SmallPropMinPixels * metresPerPixel;
        }

        // --- compact meshes: their frames, and what the first frame's culling would keep -------------------------

        /// <summary>
        /// A compact mesh's frame: world = <see cref="Origin"/> + local * <see cref="Scale"/>, the local position being a
        /// UNorm16 lattice code in [0, 1]. Uniform scale, so Unity's normal matrix (the inverse transpose) is the identity
        /// up to scale, the SNorm8 normals are world normals, and the built-in shaders' normalize undoes the scale.
        /// </summary>
        internal sealed class MeshFrame
        {
            public readonly Vector3 Origin;
            public readonly float Scale;
            public readonly Matrix4x4 Matrix;

            public MeshFrame(Vector3 origin, float scale)
            {
                Origin = origin;
                Scale = scale;
                Matrix = Matrix4x4.TRS(origin, Quaternion.identity, new Vector3(scale, scale, scale));
            }

            /// <summary>Local bounds (lattice units) as world bounds.</summary>
            public Bounds World(Bounds local) => new Bounds(Origin + local.center * Scale, local.size * Scale);
        }

        /// <summary>
        /// Every compact mesh's frame, keyed by the mesh. A weak table, so an entry goes with its mesh's managed wrapper and
        /// nothing has to be removed when <see cref="DestroyBuilt"/> destroys an entry's meshes; static, because the cache
        /// entries (and their meshes) outlive the view that built them. Main thread only.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, MeshFrame> Frames =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, MeshFrame>();

        /// <summary>A mesh's bounds in WORLD metres: Mesh.bounds is in its own space, which for a compact mesh is lattice
        /// units. Every reader of a mesh's bounds goes through here (the map bounds, the emission check's centre).</summary>
        private static Bounds WorldBoundsOf(Mesh mesh) =>
            Frames.TryGetValue(mesh, out var frame) ? frame.World(mesh.bounds) : mesh.bounds;

        /// <summary>Set for the first frame's Draw only: <see cref="Submit"/> then counts what the frustum keeps.</summary>
        private bool _countView;

        /// <summary>The camera's frustum planes for the first-frame count, without the cut's oblique near plane (which is only
        /// applied inside the render bracket) - so the count is what plain frustum culling keeps, an upper bound.</summary>
        private readonly Plane[] _viewPlanes = new Plane[6];

        private long _viewTriangles;
        private long _viewTrianglesIn;
        private int _viewDrawsIn;

        /// <summary>
        /// First frame only: the triangles submitted, and those of meshes whose world bounds pass
        /// GeometryUtility.TestPlanesAABB - the same test against the same bounds Unity's culling makes per DrawMesh. So the
        /// line's "in view" figure is what reaches the GPU, not what the chunking was meant to achieve.
        /// </summary>
        private void CountInView(Mesh mesh, MeshFrame frame)
        {
            var triangles = mesh.subMeshCount > 0 ? (long)mesh.GetIndexCount(0) / 3L : 0L;
            _viewTriangles += triangles;

            var bounds = frame != null ? frame.World(mesh.bounds) : mesh.bounds;
            if (!GeometryUtility.TestPlanesAABB(_viewPlanes, bounds)) return;

            _viewTrianglesIn += triangles;
            _viewDrawsIn++;
        }

        /// <summary>
        /// Spot-sun stage C: the union of every drawn floor's ground and building mesh bounds, for <see cref="FitView"/>.
        /// Once per build, when it finishes (Finish); Mesh.bounds is kept by Unity, so this is a walk over the lists, not
        /// the vertices. The walls are not in it: they are built later (StartWalls) and stand under the roofs, which are.
        /// </summary>
        private void ComputeMapBounds()
        {
            _mapBoundsSet = false;
            _mapBounds = default(Bounds);

            foreach (var floor in _floors)
            {
                var meshes = floor?.Meshes;
                if (meshes == null) continue;

                AddBounds(meshes.Ground);
                AddBounds(meshes.Buildings);
            }
        }

        /// <summary>Encapsulates each non-empty mesh's bounds into <see cref="_mapBounds"/>, the first one setting it.</summary>
        private void AddBounds(List<Mesh> meshes)
        {
            for (var i = 0; i < meshes.Count; i++)
            {
                var mesh = meshes[i];
                if (mesh == null || mesh.vertexCount == 0) continue;

                // world bounds: a compact mesh's own are in lattice units
                var bounds = WorldBoundsOf(mesh);

                if (_mapBoundsSet) _mapBounds.Encapsulate(bounds);
                else
                {
                    _mapBounds = bounds;
                    _mapBoundsSet = true;
                }
            }
        }

        /// <summary>
        /// Spot-sun stage C: the part of the map the camera shows, as a sphere the spot's cone is fitted to. A ray through
        /// each viewport corner and the centre meets the planes of the map's lowest and highest points; a ray that misses
        /// a plane (parallel, or pointing away) or meets it past the far clip is taken at the far clip instead; each point
        /// is clamped into the map's bounds, so a view that reaches past the map is fitted to the map, not to the empty
        /// distance. The centre is the points' bounding box's, the radius its half diagonal (at least
        /// <see cref="FitRadiusMin"/>). Called before ApplyCut, so the rays are the unmodified perspective's: the cut's
        /// oblique matrix changes only depth, and the clamp spans the whole map's height, so a cut floor is inside the
        /// fit. False, with the camera's focus sphere, when there are no bounds yet.
        /// </summary>
        private bool FitView(out Vector3 centre, out float radius)
        {
            var camera = _camera.transform;
            centre = camera.position + camera.forward * Mathf.Max(FitRadiusMin, _distance);
            radius = Mathf.Max(FitRadiusMin, _distance);
            if (!_mapBoundsSet) return false;

            var low = _mapBounds.min.y;
            var high = _mapBounds.max.y;
            var fit = new Bounds();
            var any = false;

            for (var k = 0; k < FitViewportPoints.Length; k++)
            {
                var ray = _camera.ViewportPointToRay(FitViewportPoints[k]);
                AddFitPoint(ray, low, ref fit, ref any);
                AddFitPoint(ray, high, ref fit, ref any);
            }

            centre = fit.center;
            radius = Mathf.Max(FitRadiusMin, fit.extents.magnitude);
            return true;
        }

        /// <summary>One of <see cref="FitView"/>'s points: where <paramref name="ray"/> meets the plane y =
        /// <paramref name="y"/>, or its point at the far clip; clamped into the map's bounds and added to the fit.</summary>
        private void AddFitPoint(Ray ray, float y, ref Bounds fit, ref bool any)
        {
            // the far clip is a DEPTH (along the camera's forward), not a distance along the ray: a corner ray reaches
            // it at FarClip / cos(the ray's angle off the forward)
            var depthPerMetre = Mathf.Max(1e-4f, Vector3.Dot(ray.direction, _camera.transform.forward));
            var point = ray.GetPoint(FarClip / depthPerMetre);
            if (Mathf.Abs(ray.direction.y) > 1e-6f)
            {
                var t = (y - ray.origin.y) / ray.direction.y;
                if (t >= 0f && t * depthPerMetre <= FarClip) point = ray.GetPoint(t);
            }

            var min = _mapBounds.min;
            var max = _mapBounds.max;
            point = new Vector3(Mathf.Clamp(point.x, min.x, max.x), Mathf.Clamp(point.y, min.y, max.y),
                Mathf.Clamp(point.z, min.z, max.z));

            if (any) fit.Encapsulate(point);
            else
            {
                fit = new Bounds(point, Vector3.zero);
                any = true;
            }
        }

        /// <summary>HQ S1.4: the sky dome, made once and drawn centred on the camera - no shadows cast or received, so
        /// the shadow pass never sees a 3.6 km sphere.</summary>
        private Mesh _skyMesh;

        private Material _skyMaterial;
        private bool _skyBroken;

        private void DrawSky()
        {
            if (_skyBroken || _camera == null) return;

            try
            {
                if (_skyMesh == null || _skyMaterial == null)
                {
                    var shader = Shader.Find(Shaders[Shaders.Length - 1]);
                    if (shader == null)
                    {
                        _skyBroken = true;
                        return;
                    }

                    _skyMaterial = new Material(shader) { name = "QuestTreeMap3D-sky", renderQueue = 1000 };
                    _skyMaterial.SetInt("_ZWrite", 0);
                    _skyMaterial.SetInt("_Cull", 0);
                    var plan = Plan;
                    _skyMesh = SkyMesh(FarClip * SkyRadiusOfFarClip, plan.Zenith, plan.Horizon, AerialFog ? plan.Horizon : SkyBelow);
                }

                var at = Matrix4x4.Translate(_camera.transform.position);
                Graphics.DrawMesh(_skyMesh, at, _skyMaterial, _drawLayer, _camera, 0, null, ShadowCastingMode.Off, false);
                _drawCalls++;
            }
            catch (Exception ex)
            {
                // Once: the map draws over the backdrop colour instead.
                _skyBroken = true;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the 3D map's sky could not be drawn ({ex.GetType().Name}: {ex.Message}) - backdrop colour instead.");
            }
        }

        /// <summary>A sphere of <see cref="SkyRings"/> rings and <see cref="SkySegments"/> segments, coloured by height:
        /// <see cref="SkyZenith"/> at the top, <see cref="SkyHorizon"/> at the horizon, <see cref="SkyBelow"/> under it.
        /// Drawn from inside with culling off, so the winding does not matter.</summary>
        private static Mesh SkyMesh(float radius, Color zenith, Color horizon, Color below)
        {
            var rings = SkyRings * 2;
            var vertices = new List<Vector3>();
            var colours = new List<Color32>();
            var indices = new List<int>();

            for (var r = 0; r <= rings; r++)
            {
                var v = (float)r / rings;
                var polar = Mathf.PI * v;
                var y = Mathf.Cos(polar);
                var ring = Mathf.Sin(polar);

                Color colour;
                if (y >= 0f) colour = Color.Lerp(horizon, zenith, Mathf.Pow(y, 0.6f));
                else colour = Color.Lerp(horizon, below, Mathf.Pow(-y, 0.4f));

                for (var g = 0; g <= SkySegments; g++)
                {
                    var azimuth = 2f * Mathf.PI * g / SkySegments;
                    vertices.Add(new Vector3(ring * Mathf.Cos(azimuth), y, ring * Mathf.Sin(azimuth)) * radius);
                    colours.Add(colour);
                }
            }

            var stride = SkySegments + 1;
            for (var r = 0; r < rings; r++)
                for (var g = 0; g < SkySegments; g++)
                {
                    var a = r * stride + g;
                    var b = a + stride;
                    indices.Add(a); indices.Add(b); indices.Add(a + 1);
                    indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
                }

            var mesh = new Mesh { name = "QuestTreeMap3D-sky" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(indices, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 2f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>Queues one floor's meshes for our camera, with the floor's picture on them - the
        /// relief through the clipping material, the buildings through the opaque one.</summary>
        private void Draw(Floor floor)
        {
            var ground = floor.GroundMaterial;
            var walls = floor.BuildingMaterial;
            var roofs = RoofMaterialOf(floor);

            if (ground == null || walls == null) return;

            // Asked for every frame, and cheap: a resident sprite is a dictionary-free list touch. It
            // has to be asked, because the picture cache can release a floor's texture under us - and a
            // material whose mainTexture has been destroyed draws white, not the last picture. Both
            // materials are checked, since either can be the one holding the destroyed reference.
            if (!_flatColours && (ground.mainTexture == null || walls.mainTexture == null || roofs.mainTexture == null) &&
                floor.Layer != null && floor.Layer.TryGetSprite(out var sprite) &&
                sprite != null && sprite.texture != null)
            {
                SetPicture(ground, sprite.texture, _emissiveGround);   // and its emission map, on the emission path
                walls.mainTexture = sprite.texture;

                // stage C: the roofs' own material, when it is one (made only on the emission path, so emissive)
                if (roofs != walls) SetPicture(roofs, sprite.texture, _emissiveGround);
            }

            // NOT DRAWN until its picture is on it. A textured shader with a null _MainTex samples white,
            // so a peeled lower storey whose PNG is still being decoded would draw as a blank white slab
            // under the floor being looked at - which reads as the map having gone wrong, where a floor
            // that appears a moment later reads as a floor that appeared a moment later. Asked for above,
            // so the frame that has it draws it.
            if (!_flatColours && ground.mainTexture == null)
            {
                // S1 review: a floor with no layer at all never gets a picture - not worth a frame a frame
                if (floor.Layer != null) _unsettled = true;
                return;
            }

            var meshes = floor.Meshes;
            if (meshes == null) return;

            // HQ S1.1: walls still to come, or any material below still waiting for its picture, keep the view
            // rendering every frame until it is whole; a failed tile or side is settled (it never arrives).
            if (meshes.WallsPending) _unsettled = true;

            // WP8 (V.3): the faces by source, when the debug switch is on (never in a shipped build).
            var debug = DebugFaceSources && !_flatColours;
            if (debug) NoteDebugCamera();

            // The first frame this floor's picture is here: its walls can be coloured now - started here,
            // prepared on a worker and uploaded paced (StartWalls), so not a per-frame cost either.
            if (meshes.WallsPending && !meshes.WallsRunning) StartWalls(meshes, floor.Level, ground.mainTexture, late: true);

            // the tonemap frame check's cover render draws everything BUT the ground (CheckTonemapFrame)
            for (var i = 0; i < meshes.Ground.Count && !_withoutGround; i++)
            {
                var mesh = meshes.Ground[i];
                if (mesh != null) Submit(mesh, ground, castShadows: false);
            }

            // Every mesh is drawn whole: the dollhouse cut is the camera's oblique near plane (ApplyCut), which
            // clips each triangle on the GPU at the cut height. The peel still leaves out every band over the
            // chosen floor.
            var roofMaterial = debug ? DebugOr(DebugTop, roofs) : roofs;

            for (var i = 0; i < meshes.Buildings.Count; i++)
            {
                var mesh = meshes.Buildings[i];
                if (mesh != null) Submit(mesh, roofMaterial);
            }

            // Roofs standing on another floor, with THAT floor's building material - its picture. A floor
            // this view does not draw (it is above the chosen one) has its faces above the cut anyway; if it
            // is not here they fall back to this floor's material. A floor whose picture has not arrived yet
            // is skipped this frame rather than drawn white, as the floors themselves are.
            for (var i = 0; i < meshes.RoofsOnOtherFloors.Count; i++)
            {
                var roof = meshes.RoofsOnOtherFloors[i];
                // An owner above the chosen floor is not drawn: a SLOPED face routed there by its centroid can still
                // reach below the cut, and that sliver takes the chosen floor's picture - the one at the cut - not
                // this filing band's (review F28).
                var owner = FloorAt(roof.Level) ?? FloorAt(_selectedLevel);
                var material = owner != null && owner.BuildingMaterial != null ? RoofMaterialOf(owner) : roofs;

                if (material == null) continue;

                // S1 review: the owner's picture is not here yet - this frame is not whole (its Draw may put the
                // texture on the material later this same frame, after this roof was skipped)
                if (!_flatColours && material.mainTexture == null)
                {
                    _unsettled = true;
                    continue;
                }

                var mesh = roof.Mesh;
                if (mesh != null) Submit(mesh, debug ? DebugOr(DebugTop, material) : material);
            }

            // The walls, one colour at a time: at most sixteen more DrawMesh calls per floor. A tint with
            // no material of its own (a shader with nothing to tint) draws with the buildings' material.
            for (var t = 0; t < meshes.Walls.Count; t++)
            {
                var tint = meshes.Walls[t];
                if (tint == null) continue;

                var material = tint.Material != null ? tint.Material : walls;
                if (debug) material = DebugOr(DebugTint, material);

                for (var i = 0; i < tint.Meshes.Count; i++)
                {
                    var mesh = tint.Meshes[i];
                    if (mesh != null) Submit(mesh, material);
                }
            }

            // The faces the game's own materials texture: one DrawMesh per (tile, mesh chunk) - one per material a
            // band uses, on a map of ~300 materials about 300 calls. A tile not cut yet draws in the floor's wall
            // colour rather than leaving holes; a page that FAILED is dropped and the view rebuilt without it
            // (RebuildWithoutFailedSides), its faces going back to the U/V rule.
            if (AtlasActive)
            {
                var tiles = _heldTiles;

                for (var g = 0; g < meshes.Atlas.Count; g++)
                {
                    var atlas = meshes.Atlas[g];
                    var material = atlas?.Material;
                    if (material == null) continue;

                    // The tile's texture, once the store has cut it (a few a frame); until then, and for good if it
                    // failed, the floor's wall colour. Unity's null covers a store destroyed under a cached entry.
                    if (material.mainTexture == null && tiles != null)
                    {
                        var texture = tiles.TextureOf(atlas.Tile);
                        if (texture != null) material.mainTexture = texture;
                    }

                    // A whole PAGE that failed (unreadable, will not decode, too big): the view is rebuilt without it,
                    // its faces going back to the U/V rule - as a failed side picture's do.
                    if (tiles != null && tiles.PageFailedFor(atlas.Tile)) _sideFailed = true;

                    if (material.mainTexture == null && tiles != null && !tiles.TileFailed(atlas.Tile)) _unsettled = true;

                    var draw = material.mainTexture != null && !atlas.Unclippable ? material : SideFallbackFor(meshes, floor.Level, walls);

                    // debug: a FLAT tile (the atlas's 4 x 4 colour for a material without a texture) in magenta
                    if (debug && material.mainTexture != null && material.mainTexture.width <= 4)
                        draw = DebugOr(DebugFlat, draw);

                    for (var i = 0; i < atlas.Meshes.Count; i++)
                    {
                        var mesh = atlas.Meshes[i];
                        if (mesh != null) Submit(mesh, draw);
                    }
                }
            }

            // The faces the side pictures texture: at most four more DrawMesh calls per floor. Each side's
            // picture is fetched from THIS view's entry whenever the material has lost it (the picture
            // cache can evict a side like a floor), and a side whose picture is not here yet is skipped
            // rather than drawn white - its faces appear the frame it arrives, as a peeled floor does.
            if (!SidesActive) return;

            for (var slot = 0; slot < meshes.Sides.Length; slot++)
            {
                var side = meshes.Sides[slot];
                var material = side?.Material;
                if (material == null) continue;

                // Stage B review S1: the side/roof emission material failed its check this session - a side material still
                // carrying it (made before the check, or cached by another view) is rebuilt lit, as the lit path makes it
                if (!_emissiveSides && material.IsKeywordEnabled("_EMISSION")) material = side.Material = LitSideMaterial(material);
                else if (_emissiveSides && material.IsKeywordEnabled("_EMISSION")) MatchEmissionScale(material);

                var picture = _sides[slot]?.Picture;

                if (material.mainTexture == null && picture != null && picture.TryGetSprite(out var sideSprite) &&
                    sideSprite != null && sideSprite.texture != null)
                {
                    SetPicture(material, sideSprite.texture, _emissiveSides);   // as the ground's
                }

                // A side whose picture has FAILED to decode will never have one: noted, and the view is
                // rebuilt without that side at the top of the next frame (not mid-draw - see LateUpdate), so
                // its faces go back to the top picture or a tint as if the side had never been captured.
                if (picture != null && picture.ArtworkFailed) _sideFailed = true;

                if (material.mainTexture == null && !(picture != null && picture.ArtworkFailed)) _unsettled = true;

                // No picture on it (decoding, evicted, or failed): drawn in the floor's wall colour rather
                // than skipped. A skipped face is a hole straight through the building.
                var draw = material.mainTexture != null ? material : SideFallbackFor(meshes, floor.Level, walls);
                if (debug) draw = DebugOr(DebugSide, draw);

                for (var i = 0; i < side.Meshes.Count; i++)
                {
                    var mesh = side.Meshes[i];
                    if (mesh != null) Submit(mesh, draw);
                }
            }
        }

        /// <summary>
        /// Pictures stage C review S1: a floor's roof material, put back to the lit building material first when the side/roof
        /// emission material has failed its check this session (<see cref="_emissiveSides"/> false) - as a side's is by
        /// <see cref="LitSideMaterial"/>, so a machine whose cutout _EMISSION variant is broken draws lit roofs, not black
        /// ones. Asked for the owner of a roof on another floor too, whose own Draw may come later in the frame.
        /// </summary>
        /// <param name="floor">The floor; its BuildingMaterial is not null.</param>
        private Material RoofMaterialOf(Floor floor)
        {
            var roofs = floor.RoofMaterial;
            if (roofs == null) return floor.BuildingMaterial;

            if (!_emissiveSides && roofs != floor.BuildingMaterial && roofs.IsKeywordEnabled("_EMISSION"))
            {
                Discard(roofs);
                floor.RoofMaterial = roofs = floor.BuildingMaterial;
            }

            return roofs;
        }

        /// <summary>The debug switch's colours, by source (see <see cref="DebugFaceSources"/>).</summary>
        private const int DebugTop = 0;

        private const int DebugSide = 1;
        private const int DebugTint = 2;
        private const int DebugFlat = 3;

        private static readonly Color[] DebugColours =
        {
            new Color(0.2f, 0.35f, 1f), new Color(1f, 0.55f, 0.1f), new Color(0.55f, 0.55f, 0.55f), new Color(1f, 0f, 1f)
        };

        /// <summary>The debug switch's materials, made on first need and destroyed with the view.</summary>
        private readonly Material[] _debugMaterials = new Material[4];

        /// <summary>Where the camera was when the debug switch last logged it.</summary>
        private Vector3 _debugCameraAt = new Vector3(float.NaN, float.NaN, float.NaN);

        /// <summary>A debug source's flat material, or null when the shader has nothing to colour with.</summary>
        private Material DebugMaterial(int kind)
        {
            if (_debugMaterials[kind] == null)
                _debugMaterials[kind] = MakeTintMaterial($"QuestTreeMap3D-debug-{kind}", DebugColours[kind]);

            return _debugMaterials[kind];
        }

        /// <summary>A debug source's material, or <paramref name="fallback"/> when it cannot be made.</summary>
        private Material DebugOr(int kind, Material fallback)
        {
            var material = DebugMaterial(kind);
            return material != null ? material : fallback;
        }

        /// <summary>Logs the camera's world position and angles when it has moved half a metre since the last line -
        /// the debug switch's way of matching two runs' screenshots to the metre.</summary>
        private void NoteDebugCamera()
        {
            if (_camera == null) return;

            var at = _camera.transform.position;
            if (!float.IsNaN(_debugCameraAt.x) && (at - _debugCameraAt).sqrMagnitude < 0.25f) return;

            _debugCameraAt = at;
            var angles = _camera.transform.eulerAngles;

            Plugin.LogSource?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "QuestTree: 3D map face-source debug for {0} - camera at ({1:0.0}, {2:0.0}, {3:0.0}), pitch {4:0.0}, yaw {5:0.0}; " +
                "atlas textured, flat tile magenta, top blue, side orange, tint grey.",
                _mapKey, at.x, at.y, at.z, angles.x, angles.y));
        }

        /// <summary>HQ S4: the camera's pose, once the view has settled after a move - position, pitch, yaw, distance and the
        /// floor - so a landmark screenshot can be taken again from the same place.</summary>
        private void SaySettled()
        {
            if (_camera == null) return;

            try
            {
                var at = _camera.transform.position;
                var angles = _camera.transform.eulerAngles;

                Plugin.LogSource?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "QuestTree: 3D map view of {0} settled - camera at ({1:0.0}, {2:0.0}, {3:0.0}), pitch {4:0.0}, yaw {5:0.0}, " +
                    "distance {6:0.0} m, focus ({7:0.0}, {8:0.0}), floor {9}.",
                    _mapKey, at.x, at.y, at.z, angles.x, angles.y, _distance, _focus.x, _focus.y, _selectedLevel));
            }
            catch (Exception)
            {
                // a line, not a feature
            }
        }

        /// <summary>The material a side's faces are drawn with while the side has no picture: the entry's
        /// untextured wall-coloured material, made on first need; the floor's building material if the shader
        /// has nothing to colour with.</summary>
        private Material SideFallbackFor(Built built, int level, Material buildings)
        {
            if (built.SideFallback == null)
                built.SideFallback = MakeTintMaterial($"QuestTreeMap3D-sidefallback-{level}", built.WallAverage);

            return built.SideFallback != null ? built.SideFallback : buildings;
        }

        /// <summary>Set by <see cref="Draw"/> when a side's picture has failed to decode; consumed at the top
        /// of the next frame by <see cref="RebuildWithoutFailedSides"/>.</summary>
        private bool _sideFailed;

        /// <summary>
        /// Rebuilds this view's geometry without every side whose picture has FAILED - not one still loading,
        /// which gets the fallback colour until it arrives.
        ///
        /// The faces a failed side would have textured are then classified again among the sides that are
        /// left: they go to another side that sees them well enough, or to the top picture, or to a tint -
        /// the same answer a capture without that side would have given. The cache key carries the sides in
        /// use, so the entries built WITH the failed side are dropped (or orphaned, if another live view
        /// still draws them) and nothing reuses them.
        /// </summary>
        private void RebuildWithoutFailedSides()
        {
            _sideFailed = false;

            var droppedSides = "";
            var droppedPages = "";

            for (var slot = 0; slot < _sides.Length; slot++)
            {
                var picture = _sides[slot]?.Picture;
                if (picture == null || !picture.ArtworkFailed) continue;

                droppedSides += SideOrder[slot];
                _sides[slot] = null;
            }

            for (var page = 0; page < _pages.Length; page++)
            {
                if (_pages[page] == null || _heldTiles == null || !_heldTiles.PageFailed(page)) continue;

                droppedPages += (droppedPages.Length > 0 ? "," : "") + page.ToString(CultureInfo.InvariantCulture);
                _pages[page] = null;
            }

            // "sides NE, atlas pages 3" - two lists, not the letters and the page numbers run together.
            var dropped = droppedSides.Length > 0 ? "sides " + droppedSides : "";
            if (droppedPages.Length > 0) dropped += (dropped.Length > 0 ? ", " : "") + "atlas pages " + droppedPages;

            if (dropped.Length == 0 || _loaded == null) return;

            CountSides();
            CountPages();

            // The room for the dropped sides goes back; BeginBuild takes what the rest need straight away, so
            // nothing is evicted in between.
            ReturnSideRoom(evict: false);

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the picture(s) {dropped} of the 3D map for '{_mapKey}' could not be decoded - the " +
                $"buildings are rebuilt without them.");

            ReleaseFloors();
            BeginBuild();
        }

        /// <summary>
        /// The one render: our light on, the scene's fog off, the camera rendered by hand, and both put
        /// back by the statement that changed them. The finally is the whole safety of this design - a
        /// light left enabled here is a light in the player's hideout, and fog left off is the menu's own
        /// background flattened.
        /// </summary>
        private void RenderNow()
        {
            var fog = RenderSettings.fog;
            var oblique = false;

            // HQ S1.3: the player's shadow settings the render sets, put back in the finally whatever happens in between.
            var shadowsWere = QualitySettings.shadows;
            var distanceWas = QualitySettings.shadowDistance;
            var shadowsSet = false;

            // HQ S1.4: the scene's ambient, put back in the finally.
            var ambientModeWas = RenderSettings.ambientMode;
            var ambientSkyWas = RenderSettings.ambientSkyColor;
            var ambientEquatorWas = RenderSettings.ambientEquatorColor;
            var ambientGroundWas = RenderSettings.ambientGroundColor;
            var ambientIntensityWas = RenderSettings.ambientIntensity;
            var ambientProbeWas = RenderSettings.ambientProbe;
            var ambientSet = false;

            // stage 1: the reflection intensity and the fog's shape are the menu's too, put back the same way
            var reflectionWas = RenderSettings.reflectionIntensity;
            var reflectionSet = false;
            var fogModeWas = RenderSettings.fogMode;
            var fogColorWas = RenderSettings.fogColor;
            var fogStartWas = RenderSettings.fogStartDistance;
            var fogEndWas = RenderSettings.fogEndDistance;
            var fogSet = false;

            try
            {
                RenderSettings.fog = false;   // the menu's own, off unless AerialFog sets ours below
                _light.enabled = !_calibrating;   // stage D: the calibration's quads are emission alone, nothing lit

                // Stage D review: the stack can drop off mid-view (SetActive's volume check, or a throw, calls Detach); with
                // no tonemap the 1.8 anchor and the calibrated scale would draw blown-out white, so the view goes back to the
                // plain path before this render reads its plan. TonemapFallback runs once: it clears _tonemapOn.
                if (_tonemapOn && !_calibrating && !Map3DPostProcess.Attached)
                {
                    TonemapFallback("stack detached");
                    Plugin.LogSource?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "QuestTree: 3D map tonemap - the post-processing stack was taken off ({0}); the plain anchor {1:0.00}, the ambient floor and emission x1.00 are used from here.",
                        Map3DPostProcess.Describe(), WhiteInSun));
                }

                // the sun fixed in the world (the captured one, or the preset), or the one light over the shoulder - under
                // the exposure budget either way
                var plan = Plan;
                var sun = Lighting == ModSettings.MapLightMode.Sun;
                var upShare = UpShareFor(plan, sun);

                // Stage C: the spot stands where the sun is, seen from the part of the map the view shows. dir points
                // TOWARDS the light, as the directional light before it pointed along -SunDirection; over the shoulder it
                // is the reverse of that rotation's forward, so the mode is the same rotation as before. Fitted before
                // ApplyCut, whose oblique matrix is not the view's perspective (FitView).
                FitView(out var centre, out var radius);
                var dir = sun
                    ? plan.SunDirection
                    : -(Quaternion.Euler(LightPitch, _yaw + LightYawOffset, 0f) * Vector3.forward);
                var farthest = _probe != null ? Mathf.Max(SpotDistanceMin, _probe.MaxDistanceOk) : SpotDistanceNoProbe;
                // far enough that the near plane (below) is at least SpotNearShareOfDistance of the distance whenever the
                // probe's maximum allows it - the map's reach towards the light, not the fit's radius, sets that
                var reach = TowardsLight(centre, dir, radius);
                var distance = Mathf.Clamp(
                    Mathf.Max(SpotDistanceOfRadius * radius, (reach + 10f) / (1f - SpotNearShareOfDistance)),
                    SpotDistanceMin, farthest);

                _lightGo.transform.SetPositionAndRotation(centre + dir * distance, Quaternion.LookRotation(-dir));
                // the cone tangent to the fitted sphere: asin, not atan - they part when the distance is clamped to the
                // probe's maximum and the radius is a large share of it
                _light.spotAngle = Mathf.Clamp(2f * Mathf.Asin(Mathf.Min(1f, radius / distance)) * Mathf.Rad2Deg * SpotConeMargin, 1f, 179f);
                _light.range = SpotRangeOfDistance * distance;

                // The near plane is fitted to the whole map's reach towards the light, not the fit's: a spot's shadow
                // caster is not pancaked (unity_LightShadowBias.y is 0 for spots), so a caster nearer the light than the
                // plane casts nothing - a tall building up-sun of a zoomed-in fit would leak light exactly where the view
                // looks. 10 m short of the nearest any map mesh can be to the light, never below 0.1.
                _light.shadowNearPlane = Mathf.Max(0.1f, distance - reach - 10f);
                _spotNearFarRatio = _light.shadowNearPlane / Mathf.Max(0.0001f, _light.range);
                _spotDistance = distance;
                _spotRadius = radius;

                // the spot's attenuation (what distance and cookie leave of it at the map, as the probe measured) divided
                // back out, so the sun's intensity under the exposure budget is what reaches a surface facing it
                var exposure = Exposure(plan, upShare, sun);
                var attenuation = _probe != null ? Mathf.Max(0.0001f, _probe.Attenuation) : AttenuationNoProbe;
                _light.intensity = plan.SunIntensity * exposure / attenuation;
                _light.color = plan.SunColour;
                _light.shadowStrength = plan.ShadowStrength;
                _exposureRendered = exposure;
                _groundColour = GroundColour(plan, upShare, sun);

                if (DrawsProbe(plan, sun))
                {
                    // stage 3: EFT's ambient, re-projected, scaled in linear terms to the budget's factor on the display value
                    var scaled = plan.Probe.Value;
                    var linearFactor = Mathf.Pow(exposure, ProbeGamma);
                    for (var c = 0; c < 3; c++)
                        for (var i = 0; i < 9; i++)
                            scaled[c, i] *= linearFactor;

                    ambientSet = true;   // before the first write: a setter that throws half way is still put back
                    RenderSettings.ambientMode = AmbientMode.Custom;
                    RenderSettings.ambientIntensity = 1f;
                    RenderSettings.ambientProbe = scaled;
                }
                else if (AmbientTrilight)
                {
                    var ambient = plan.SunIntensity * AmbientOfSun * exposure;
                    ambientSet = true;
                    RenderSettings.ambientMode = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = AmbientSky * ambient;
                    RenderSettings.ambientEquatorColor = AmbientEquator * ambient;
                    RenderSettings.ambientGroundColor = AmbientGround * ambient;
                    RenderSettings.ambientIntensity = 1f;
                }

                // the gamma-space Standard shader's environment reflection would veil every surface with the menu's probe
                reflectionSet = true;
                RenderSettings.reflectionIntensity = 0f;

                oblique = !_calibrating && ApplyCut();   // stage D: the cut plane could clip the calibration's quads

                // the aerial fog, on uncut frames only: the fog coordinate is clip-space depth, which the cut's oblique
                // projection replaces (stage 1 review) - a cut frame is drawn clear, as it is drawn without shadows
                if (AerialFog && !oblique && !_calibrating)
                {
                    var start = FogStartOfDistance * Mathf.Max(0f, _distance);
                    fogSet = true;
                    RenderSettings.fog = true;
                    RenderSettings.fogMode = FogMode.Linear;
                    RenderSettings.fogColor = Plan.Fog;
                    RenderSettings.fogStartDistance = start;
                    RenderSettings.fogEndDistance = start + FogSpanOfFarClip * FarClip;
                }

                _fogDrawn = fogSet;

                // Stage C: shadows only where the setting asks, the frame is uncut (ShadowsUnderCut) and the probe proved spot
                // shadows (_spotShadowsWhy). The shadow distance reaches the farthest fitted point from the camera, so no
                // receiver on screen is in the fade.
                _shadowsDrawn = ShadowMode != LightShadows.None && (!oblique || ShadowsUnderCut) && _spotShadowsWhy == null;
                _light.shadows = _shadowsDrawn ? ShadowMode : LightShadows.None;
                _spotShadowDistance = 0f;

                if (_shadowsDrawn)
                {
                    shadowsSet = true;   // before the first write: a setter that throws half way is still put back
                    QualitySettings.shadows = ShadowQuality.All;
                    QualitySettings.shadowDistance =
                        (Vector3.Distance(_camera.transform.position, centre) + radius) * ShadowDistanceMargin;
                    _spotShadowDistance = QualitySettings.shadowDistance;
                }

                // stage 4: the post-processing volume is live for this render only (a game camera whose volume layer
                // included ours would otherwise be graded), and the stack is told when the frame is cut so it keeps the
                // oblique projection it would otherwise reset
                Map3DPostProcess.SetActive(true, oblique);

                var clock = _timeRender ? Stopwatch.StartNew() : null;
                _camera.Render();

                if (clock != null)
                {
                    _renderMs = clock.Elapsed.TotalMilliseconds;
                    _timeRender = false;
                }
            }
            finally
            {
                // FOG FIRST. Both matter, and if one of them is going to be skipped by a throw it must
                // not be the global: a light left on is ours to find on our own object, while fog left
                // off is the menu's own scene changed under the player for the rest of the session. Each
                // is guarded separately for the same reason - one throwing must not skip the other.
                try { RenderSettings.fog = fog; } catch (Exception) { /* nothing further to try */ }
                try { Map3DPostProcess.SetActive(false); } catch (Exception) { /* as above */ }
                try { if (_light != null) _light.enabled = false; } catch (Exception) { /* as above */ }

                if (fogSet)
                {
                    try { RenderSettings.fogMode = fogModeWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.fogColor = fogColorWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.fogStartDistance = fogStartWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.fogEndDistance = fogEndWas; } catch (Exception) { /* as above */ }
                }

                if (reflectionSet)
                {
                    try { RenderSettings.reflectionIntensity = reflectionWas; } catch (Exception) { /* as above */ }
                }

                // HQ S1.4: the scene's ambient is the menu's, each field put back on its own.
                if (ambientSet)
                {
                    try { RenderSettings.ambientMode = ambientModeWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientSkyColor = ambientSkyWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientEquatorColor = ambientEquatorWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientGroundColor = ambientGroundWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientIntensity = ambientIntensityWas; } catch (Exception) { /* as above */ }

                    // LAST: the probe Unity recomputed from the Trilight colours goes back to the scene's own (a Skybox or
                    // Custom probe is not rebuilt by putting the mode back)
                    try { RenderSettings.ambientProbe = ambientProbeWas; } catch (Exception) { /* as above */ }
                }

                // HQ S1.3: the shadow settings are the player's, each put back on its own.
                if (shadowsSet)
                {
                    try { QualitySettings.shadows = shadowsWere; } catch (Exception) { /* as above */ }
                    try { QualitySettings.shadowDistance = distanceWas; } catch (Exception) { /* as above */ }
                }

                // The oblique projection lives ONLY inside this bracket: every other reader of the camera
                // (TryProject, PanBy, the next ApplyCut) sees its own perspective, recomputed from the field of
                // view, the aspect and the clip planes.
                try { if (oblique && _camera != null) _camera.ResetProjectionMatrix(); } catch (Exception) { /* as above */ }
            }
        }

        /// <summary>Time the next render, for the first-frame line. Set by <see cref="Finish"/> and whenever the
        /// cut height changes, so the first frame after a floor switch is timed too.</summary>
        private bool _timeRender;

        /// <summary>The last timed render, in milliseconds.</summary>
        private double _renderMs;

        /// <summary>Puts the camera where the orbit says, looking at the focus point on the ground.</summary>
        private void Place()
        {
            if (_camera == null) return;

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var target = new Vector3(_focus.x, GroundAt(_focus.x, _focus.y), _focus.y);

            // The oblique cut needs the camera above it (see ApplyCut): dolly out until it is at least
            // MinCameraAboveCut over the cut, never past the furthest the dolly goes. sin(pitch) >= sin 15.
            // Only while the cut is the near plane (PART-03 review): the CutNone rollback draws uncut and must
            // not dolly. The pushed-out distance is kept, as any dolly is - it is the view's own state.
            if (CutMode == CutByNearPlane && !float.IsNaN(_cutY))
            {
                var sin = Mathf.Sin(_pitch * Mathf.Deg2Rad);
                var need = (_cutY + MinCameraAboveCut - target.y) / sin;
                if (need > _distance) _distance = Mathf.Min(need, MaxDistance());
            }

            _camera.transform.position = target + rotation * new Vector3(0f, 0f, -_distance);
            _camera.transform.rotation = rotation;
        }

        /// <summary>The ground height at a map point: the chosen floor's relief where it has one, else
        /// the floor's own band bottom. Never NaN - a NaN in a camera position loses the whole
        /// view.</summary>
        private float GroundAt(float x, float z)
        {
            if (_groundBand != null && _groundBand.TryHeightAt(x, z, out var y)) return y;

            return _groundFallbackY;
        }

        /// <summary>What the ground is taken to be where the relief has a hole: the selected floor's
        /// declared band bottom, which is the height the flat map already files its markers under, else
        /// the file's own low end.</summary>
        private float FallbackGroundY()
        {
            var layer = ResolveLayer();

            // Not the "any height" placeholders (about -1003 m from the extent probe, -2000 m from the catalog):
            // the same test MeasureFloorRanges makes, or the camera and pins went a kilometre under the map (F22).
            if (layer != null && layer.GameBounds.Count > 0)
            {
                var low = layer.GameBounds[0].Min.z;
                if (low > -1000f && low < 1000f) return low;
            }

            return _file != null && !float.IsNaN(_file.YMin) ? _file.YMin : 0f;
        }

        // --- the overlays ---------------------------------------------------------------------------

        /// <summary>Records an overlay and where on the map it stands. Its scale is set to one and left
        /// there: the container it is in is at scale one in 3D, so the pixel sizes the builders use are
        /// already screen pixels and there is nothing to counter-scale.</summary>
        /// <param name="child">The overlay's rect, placed at its map coordinates.</param>
        public void KeepConstantScale(RectTransform child)
        {
            if (child == null) return;

            child.localScale = Vector3.one;
            _overlays.Add((child, child.anchoredPosition));

            // PARKED on the spot. Its anchoredPosition is raw map metres - the position the flat view
            // reads through a scaled container - and this container is at scale one, so left alone it
            // would draw at a thousand canvas units from the centre for the frames between here and the
            // mesh landing. Every overlay stays parked until a real projection places it.
            child.anchoredPosition = Parked;
        }

        /// <summary>
        /// Every overlay, moved to where its map position is on screen this frame.
        ///
        /// The container the overlays are in is anchored at the viewport's centre, pivoted at its centre
        /// and at scale one, so a child's anchoredPosition IS its offset from the middle of the viewport
        /// in canvas units - which is what makes this two multiplications rather than a rect
        /// conversion.
        ///
        /// Parked rather than placed when the point is behind the camera (where the projection flips and
        /// a pin would appear mirrored on the far side of the map) or outside the viewport by more than
        /// <see cref="OverlayMargin"/> - a marker the mask would clip anyway. See
        /// <see cref="TryProject"/>, which both this and <see cref="Project"/> go through.
        /// </summary>
        private void PlaceOverlays()
        {
            if (_overlays.Count == 0) return;

            for (var i = 0; i < _overlays.Count; i++)
            {
                var rect = _overlays[i].Rect;

                // Unity's null: the viewport can be destroyed a frame before this component is.
                if (rect == null) continue;

                var onScreen = TryProject(_overlays[i].Map, out var local, out var inside) && inside;

                // PARKED far outside the viewport rather than deactivated, and that is deliberate:
                // SetActive on these rects belongs to LabelCull, which switches a zone name off when it
                // would collide with an extract's. A frame loop that also wrote activeSelf would turn
                // every culled name back on the moment it came on screen, and the cull would be dead in
                // 3D. The viewport's RectMask2D culls a parked rect from rendering, which is the cost
                // this was for.
                rect.anchoredPosition = onScreen ? local : Parked;
            }
        }

        // --- the gestures ---------------------------------------------------------------------------

        /// <summary>Turns and tilts the view. Yaw is free, pitch is clamped short of straight down so
        /// the camera never looks along its own up vector, where the rotation is undefined.</summary>
        /// <param name="yaw">Degrees to turn.</param>
        /// <param name="pitch">Degrees to tilt.</param>
        internal void Orbit(float yaw, float pitch)
        {
            _yaw += yaw;
            _pitch = Mathf.Clamp(_pitch + pitch, MinPitch, MaxPitch);

            Moved();
        }

        /// <summary>
        /// Drags the focus point across the ground, so the map follows the cursor.
        ///
        /// The conversion is screen pixels -> world metres at the focus depth: the viewport spans
        /// <c>2 d tan(fov/2)</c> metres vertically at that depth, over its own height in pixels. The
        /// vertical half is then divided by the sine of the pitch, because the ground is TILTED away
        /// from the screen: at 20 degrees a pixel of screen height is nearly three metres of ground, and
        /// without the correction a drag at a low tilt moves the map a third as far as the cursor.
        /// </summary>
        /// <param name="delta">The drag, in screen pixels.</param>
        internal void PanBy(Vector2 delta)
        {
            if (_camera == null) return;

            var pixels = _viewport.rect.height * CanvasScale();
            if (!(pixels > 0f)) return;

            var metresPerPixel = 2f * _distance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / pixels;

            var right = _camera.transform.right;
            right.y = 0f;
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;

            var forward = _camera.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;

            var tilt = Mathf.Max(0.26f, Mathf.Sin(_pitch * Mathf.Deg2Rad));

            // MINUS: the map follows the cursor, so the camera goes the other way.
            var move = -right * (delta.x * metresPerPixel) -
                       forward * (delta.y * metresPerPixel / tilt);

            _focus = ClampFocus(_focus + new Vector2(move.x, move.z));

            Moved();
        }

        /// <summary>Comes closer or goes further, clamped so the map can neither be lost behind the
        /// camera nor followed out past the far plane.</summary>
        /// <param name="notches">The wheel delta.</param>
        internal void Dolly(float notches)
        {
            _distance = Mathf.Clamp(
                _distance * (1f - DollyPerNotch * notches), MinDistance, MaxDistance());

            Moved();
        }

        /// <summary>Moves the focus to a map point, keeping the current distance and tilt - the 3D
        /// answer to "fly to this quest's pin". The requested scale is ignored on purpose: in 3D it
        /// would be a distance, and jumping the player's zoom as well as their position on a row click
        /// is two surprises where one was asked for.</summary>
        /// <param name="contentPoint">The point, in map coordinates.</param>
        /// <param name="scale">Ignored. See above.</param>
        public void FocusOn(Vector2 contentPoint, float scale)
        {
            _focus = ClampFocus(contentPoint);

            Moved();
        }

        /// <summary>
        /// Keeps the focus point over the map: within the floor's rectangle plus a fifth of its size on
        /// every side. Without a limit a long drag slides the whole map off the screen with nothing to
        /// drag back, and a pan's speed scales with the distance, so at the far dolly limit one flick is
        /// kilometres.
        /// </summary>
        /// <param name="focus">The focus wanted, in map coordinates.</param>
        private Vector2 ClampFocus(Vector2 focus)
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return focus;

            var margin = layer.BoundsSize * FocusMargin;

            return new Vector2(
                Mathf.Clamp(focus.x, layer.BoundsMin.x - margin.x, layer.BoundsMax.x + margin.x),
                Mathf.Clamp(focus.y, layer.BoundsMin.y - margin.y, layer.BoundsMax.y + margin.y));
        }

        /// <summary>How far past the floor's rectangle the focus may go, as a fraction of its size.</summary>
        private const float FocusMargin = 0.2f;

        /// <summary>Re-places the camera and tells whoever is listening the view has moved.</summary>
        private void Moved()
        {
            Place();

            unchecked { ViewVersion++; }

            // Zero for the pan: it means nothing here, and no subscriber reads it - MapView keeps the
            // 3D view's own State instead. See IOverlayHost.
            OnViewChanged?.Invoke(Scale, Vector2.zero);
        }

        /// <summary>The furthest the dolly goes: one and a half times the extent's diagonal, which frames
        /// the whole map from outside it whatever the tilt.</summary>
        private float MaxDistance()
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return FarClip * 0.5f;

            var size = layer.BoundsSize;

            return Mathf.Min(FarClip * 0.5f, Mathf.Max(MinDistance + 1f, size.magnitude * MaxDistanceOfDiagonal));
        }

        /// <summary>The distance the whole floor just fits at, at the opening tilt: the greater of what
        /// the extent's width needs across the screen and what its depth needs up it, the depth
        /// foreshortened by the tilt, with a tenth of margin.</summary>
        private float FitDistance()
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return MinDistance * 4f;

            var size = layer.BoundsSize;
            var tangent = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            var aspect = _viewport != null && _viewport.rect.height > 0f
                ? Mathf.Max(0.2f, _viewport.rect.width / _viewport.rect.height)
                : 1.6f;

            var forDepth = size.y * Mathf.Sin(_pitch * Mathf.Deg2Rad) * 0.5f / Mathf.Max(0.01f, tangent);
            var forWidth = size.x * 0.5f / Mathf.Max(0.01f, tangent * aspect);

            return Mathf.Clamp(Mathf.Max(forDepth, forWidth) * 1.1f, MinDistance, MaxDistance());
        }

        /// <summary>The canvas scale factor, for turning screen pixels into canvas units and back. The
        /// canvas is found once and kept: this is read every frame by the render-texture sizing, and a
        /// parent walk per frame is the mistake PanZoomHandler.ResolveEventCamera already had to fix.</summary>
        private float CanvasScale()
        {
            if (_canvas == null && _viewport != null) _canvas = _viewport.GetComponentInParent<Canvas>();

            return _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        }

        private Canvas _canvas;

        /// <summary>The layer of the floor showing, which is what the extent, the fit and the fallback
        /// ground height are read from.</summary>
        private DynamicMapsLibrary.MapLayer ResolveLayer() => LayerOf(_selectedLevel) ?? _entry?.DefaultLayer;

        /// <summary>The map layer with this level, or null.</summary>
        /// <param name="level">The floor level.</param>
        private DynamicMapsLibrary.MapLayer LayerOf(int level)
        {
            if (_entry == null) return null;

            foreach (var layer in _entry.Layers)
                if (layer != null && layer.Level == level) return layer;

            return null;
        }

        // --- the end ---------------------------------------------------------------------------------

        /// <summary>
        /// Stops this view for good, from MapView.DiscardViewport just before it destroys the viewport. Destroy is
        /// deferred to the end of the frame, so the view still got one LateUpdate - and in the paced upload that
        /// was up to <see cref="FrameBudgetMs"/> spent uploading into an entry the next view throws away (review
        /// F23). Release still runs from OnDestroy and frees everything as before.
        /// </summary>
        internal void Abandon() => _broke = true;

        /// <summary>Says once that this map's mesh is not usable, and stops. The caller drops the mesh
        /// for the session and repaints into the flat picture.</summary>
        /// <param name="reason">A few words for the toggle's tooltip, written to follow "it was not used:
        /// ", so "the file has no ground in it" rather than "no ground".</param>
        private void Refuse(string reason = "")
        {
            // Before the callback, and before anything else can throw: what LateUpdate tests first.
            _broke = true;

            if (!string.IsNullOrEmpty(reason)) _refusal = reason;

            var refused = _onRefused;
            _onRefused = null;

            // The overlays are about to be the only thing in the viewport, for the one frame between
            // here and the repaint that redraws them flat. Parked, or they would flash at raw map metres.
            Park();

            // This session's layer choice goes with any failure: it is the one piece of cached state a
            // failed view could have been caused by, and one extra scan is a cheap way to be sure the
            // next attempt is not refused for a stale reason.
            _sessionLayer = -1;

            Release();

            try
            {
                refused?.Invoke(_meshPath, _refusal);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: could not report the refused 3D relief ({ex.Message}).");
            }
        }

        /// <summary>Puts every registered overlay back off screen. For the frame between a refusal and
        /// the repaint that draws the map flat, where this view no longer projects anything.</summary>
        private void Park()
        {
            for (var i = 0; i < _overlays.Count; i++)
            {
                var rect = _overlays[i].Rect;
                if (rect != null) rect.anchoredPosition = Parked;
            }
        }

        /// <summary>Stops rendering while the viewport is inactive - a visit to the tree tab leaves this
        /// viewport in the hierarchy, switched off, and a view that kept rendering there would be
        /// burning a camera render a frame on a picture nobody is looking at.</summary>
        private void OnDisable()
        {
            if (_light != null) _light.enabled = false;

            // A viewport switched off (a visit to the tree tab) holds no pictures anyone is looking at, so
            // the cache room for its sides goes back while it is off and is taken again when it returns. The
            // pictures themselves STAY resident (code review 1.19.0): evicting them re-read and re-decoded four
            // mipmapped 2048 px sides on every tab switch; the cache trims them only when it needs the room.
            ReturnSideRoom(evict: false);
        }

        private void OnEnable()
        {
            // Unity calls this on AddComponent too, before anything is known - nothing is taken then, since
            // the sides are counted, and the shader resolved, only later. After a return it retakes the room.
            if (_built && !_broke) TakeSideRoom();
        }

        private void OnDestroy()
        {
            Release();
        }

        /// <summary>
        /// Everything this view made, destroyed. The order matters: the camera stops pointing at the
        /// texture before the texture is released, or Unity renders one more frame - LateUpdate runs
        /// again in the frame a Destroy is requested in - into freed memory, and a camera whose
        /// targetTexture is null renders to the BACKBUFFER, over the player's menu.
        ///
        /// Idempotent, because it is reached from three places: a refused mesh, OnDestroy, and a failed
        /// Attach.
        /// </summary>
        private void Release()
        {
            _broke = true;

            if (RenderOnChange && _framesSeen > 0)
            {
                var seen = _framesSeen;
                var rendered = _framesRendered;
                _framesSeen = 0;
                _framesRendered = 0;

                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map for {0} - rendered {1:#,##0} of {2:#,##0} frame(s) ({3:0} % skipped as unchanged).",
                    _mapKey, rendered, seen, seen > 0 ? 100d * (seen - rendered) / seen : 0d));
            }

            if (_cutSkippedFrames > 0 && !_cutSkipLogged)
            {
                _cutSkipLogged = true;
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map drew {0} frame(s) uncut - the camera was not above the cut at {1:0.0} m.",
                    _cutSkippedFrames, _cutY));
            }

            // The picture-cache room reserved for this view's side pictures goes back first, whatever
            // else fails below: a reservation outliving its view raises the ceiling for good.
            ReturnSideRoom();

            try
            {
                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.targetTexture = null;
                }

                if (_light != null) _light.enabled = false;
                if (_image != null) _image.texture = null;
                if (_rt != null) _rt.Release();

                if (_skyMesh != null) { Destroy(_skyMesh); _skyMesh = null; }
                if (_skyMaterial != null) { Destroy(_skyMaterial); _skyMaterial = null; }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map view could not be quietened ({ex.Message}).");
            }

            // Each in its own guard, in a finally-shaped sequence: one refusal must not leave the rest
            // of a camera, a light and two hundred meshes in the menu.
            ReleaseFloors();

            _overlays.Clear();
            _groundBand = null;

            for (var i = 0; i < _debugMaterials.Length; i++)
            {
                Discard(_debugMaterials[i]);
                _debugMaterials[i] = null;
            }

            Map3DPostProcess.Detach(_camera);   // stage 4: before the camera goes, so the stack's buffers and HDR flag are undone

            Discard(_rt);
            Discard(_display);
            Discard(_cameraGo);
            Discard(_lightGo);
            Discard(_lightCookie);   // after its light: the cookie is ours, the light only held it
            Discard(_image != null ? _image.gameObject : null);

            _rt = null;
            _display = null;
            _camera = null;
            _cameraGo = null;
            _light = null;
            _lightGo = null;
            _lightCookie = null;
            _image = null;
        }

        /// <summary>Lets go of this view's floors: their materials destroyed, their cached geometry released
        /// (see <see cref="Unuse"/>). Half of <see cref="Release"/>, and all of what a rebuild throws away.</summary>
        private void ReleaseFloors()
        {
            // First: queued units and wall jobs point at these floors' entries, and must not run after them.
            ResetPipeline();

            // The tiles are let go like the meshes: kept for the next view of this file, destroyed only when a drop
            // has orphaned the store and this was its last user.
            ReleaseTiles();

            foreach (var floor in _floors)
            {
                if (floor == null) continue;

                // The MESHES are let go, not destroyed. They belong to the static cache and outlive this
                // view by design, and destroying them here is the bug that would make the cache worse
                // than no cache - the next view would find a dictionary full of destroyed Mesh references
                // and draw nothing. Unuse destroys them only when a drop has already taken them out of
                // the cache and this was the last view drawing them. See Built.
                Unuse(floor.Meshes);
                floor.Meshes = null;

                // The texture is NOT ours - it belongs to the picture cache, which hands the same
                // Texture2D to the flat view's Image. Destroying the material never destroys what was
                // assigned to it, which is exactly what is wanted here.
                Discard(floor.GroundMaterial);
                // stage C: the roofs' material is its own object only on the emission path - else it IS the buildings'
                if (floor.RoofMaterial != null && floor.RoofMaterial != floor.BuildingMaterial) Discard(floor.RoofMaterial);
                Discard(floor.BuildingMaterial);

                floor.GroundMaterial = null;
                floor.RoofMaterial = null;
                floor.BuildingMaterial = null;
            }

            _floors.Clear();
        }

        /// <summary>One Destroy that cannot take the rest of the teardown with it.</summary>
        private static void Discard(UnityEngine.Object thing)
        {
            if (thing == null) return;

            // A compact mesh's frame goes with it: Unity's Mono has no ephemerons, so the weak table would otherwise keep
            // every frame (and its key) for the session.
            if (thing is Mesh mesh)
            {
                Frames.Remove(mesh);
                Sizes.Remove(mesh);
            }

            try
            {
                Destroy(thing);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view could not destroy a {thing.GetType().Name} ({ex.Message}).");
            }
        }
    }
}
