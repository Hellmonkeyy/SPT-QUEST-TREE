using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestTree.UI
{
    /// <summary>
    /// Spot-sun plan, stage A (2026-09-29): the readback probe that says whether the two shader paths the 3D map is
    /// moving to actually work in this game - a far SPOT light's shadows, and the Standard shader's EMISSION - before
    /// anything depends on them.
    ///
    /// Why a probe and not an assumption. The directional light's shadows went through the game's replacement collect
    /// shader and came back as full shadow everywhere (test 2026-09-29 00:54); the shader is compiled, so nothing about
    /// it could be read, only measured. A spot's shadows are sampled per fragment in the ForwardAdd pass and never pass
    /// through that shader, but whether the variant exists in the game's build, whether a light 20 km away still has
    /// the depth precision to shadow a 20 m box, whether Light.shadowNearPlane's setter clamps to the inspector's
    /// 0.1-10, how the bias scales with the range and whether shadowCustomResolution is honoured are all unknown. So
    /// each is rendered once, at production distances, into a 64x64 target and read back; every verdict is a
    /// comparison that the wrong outcome FAILS (a missing variant draws the shadow as lit, an absent emission reads
    /// black), and the self-test switch <see cref="SelfTestVariable"/> forces each wrong outcome to prove it.
    ///
    /// The rig is its own: a disabled orthographic camera looking straight down, rendered with Camera.Render directly,
    /// so the viewer's queued Graphics.DrawMesh calls (queued for the viewer's camera, not this one) are never
    /// consumed. Every object it makes is destroyed and every global it touches is put back in the finally of
    /// <see cref="Measure"/>. It runs once per session (<see cref="Last"/>), and never throws out of <see cref="Run"/>.
    /// </summary>
    internal static class Map3DLightProbe
    {
        /// <summary>What the probe measured - read by the viewer (stages B and C) and by its first-frame line.</summary>
        internal sealed class Result
        {
            /// <summary>Whether some distance and bias pair passed both the shadow test and the acne test.</summary>
            public bool SpotShadows;

            /// <summary>shade / lit at the reported attempt: near 0 when the box's shadow is drawn, near 1 when not.</summary>
            public float ShadowRatio = 1f;

            /// <summary>lit / (intensity x diffuse share): what the spot's distance attenuation and cookie leave of the
            /// light on a surface facing it. 1 when never measured (a throw), so a divisor built on it stays finite.</summary>
            public float Attenuation = 1f;

            /// <summary>The shadowBias of the reported attempt.</summary>
            public float Bias;

            /// <summary>The shadowNormalBias of the reported attempt.</summary>
            public float NormalBias;

            /// <summary>The largest distance that passed; on a failure the distance of the reported attempt (the
            /// smallest tried), which is still where an unshadowed spot can be put and its attenuation was measured.</summary>
            public float MaxDistanceOk;

            /// <summary>Light.shadowNearPlane as read back after the reported attempt set it to D - 150: equal to what was
            /// set when the setter takes production values, 10 or less when it clamps to the inspector's range. It proves
            /// only that the SETTER did not clamp - not that the renderer uses the value; the shadow test is the proof that
            /// the shadow map has the precision to shadow at D.</summary>
            public float NearPlaneRead;

            /// <summary>Enabled scene lights (not the rig's) whose cullingMask includes the private layer: each would light
            /// the probe's quads on top of the rig's light. All of them are disabled for the probe and re-enabled after.</summary>
            public int SceneLights;

            /// <summary>The shadow map resolution the probe can vouch for: the requested maximum when the shadow edge
            /// narrowed from the 2048 render to the maximum's, 2048 when it did not (the request was clamped).</summary>
            public int ResolutionEffective;

            /// <summary>Whether the edge did not narrow at the maximum - the custom resolution was clamped.</summary>
            public bool ResolutionClamped;

            /// <summary>Whether the edge could be measured at all (a pass, a maximum above 2048 and an edge wider than a
            /// pixel at 2048). False leaves <see cref="ResolutionEffective"/> at the request, unproven.</summary>
            public bool ResolutionMeasured;

            /// <summary>The shadow edge's width in pixels at 2048 and at the maximum, -1 when not measured.</summary>
            public int EdgeWidthReference = -1;

            /// <summary>As <see cref="EdgeWidthReference"/>, at the maximum.</summary>
            public int EdgeWidthMax = -1;

            /// <summary>Whether the emission quad read back as its texel, within <see cref="EmissionTolerance"/>.</summary>
            public bool Emission;

            /// <summary>The emission quad's centre pixel as read back.</summary>
            public Color32 EmissionRead;

            /// <summary>Why the spot shadows failed (or why the probe could not run), null on a pass.</summary>
            public string Why;
        }

        /// <summary>Rollback: false skips the probe entirely - <see cref="Run"/> returns null and logs nothing, and the
        /// viewer takes every path it takes on a failed probe. A readonly field, not a const, so the skipped branch is
        /// not unreachable code to the compiler.</summary>
        private static readonly bool RunProbe = true;

        /// <summary>The probe's SELF-TEST, read once per session: "shadow" draws the box with ShadowCastingMode.Off
        /// (the spot line must say FAIL), "emission" leaves _EMISSION off (the emission clause must say FAIL). Unset,
        /// or anything else, is the normal path. Kept in the code as the proof the two checks can fail.</summary>
        private const string SelfTestVariable = "QUESTTREE_PROBE_SABOTAGE";

        private static readonly string SelfTest = ReadSelfTest();

        /// <summary>The target's side in pixels. Small on purpose: every measurement is a block of 16 pixels or one
        /// row, and a render this size costs next to nothing beside its shadow map.</summary>
        private const int Size = 64;

        /// <summary>The quad's side, metres. The wide view shows exactly this much (orthographic half-size = half).</summary>
        private const float QuadSide = 200f;

        /// <summary>The radius the spot's cone is fitted to, metres - the quad's half side, as a production spot is
        /// fitted to the view's radius.</summary>
        private const float FitRadius = 100f;

        /// <summary>The shadow-casting box: 20 m wide, from 10 m to 30 m above the quad - high enough that a bias
        /// which lifts shadows off their casters by metres (peter-panning) moves the shadow off the centre block.</summary>
        private const float BoxSide = 20f;

        /// <summary>The box's underside above the quad, metres: the gap a lifted (peter-panned) shadow has to open.</summary>
        private const float BoxBottom = 10f;

        /// <summary>The box's top above the quad, metres: the face the spot sees, and the one its shadow edge projects
        /// from (the resolution check centres on x = half side x D / (D - top)).</summary>
        private const float BoxTop = 30f;

        /// <summary>The camera's height above the quad and its clip planes, metres. The tilted quad reaches 87 m up.</summary>
        private const float CameraHeight = 500f;
        private const float CameraNear = 1f;
        private const float CameraFar = 1000f;

        /// <summary>The spot's distances, largest first: the viewer wants the farthest that still shadows (a far spot's
        /// rays are near parallel, like the sun's).</summary>
        private static readonly float[] SweepDistances = { SweepDistanceMax, 5000f, 2000f };

        /// <summary>The farthest distance the sweep tries, metres - also the viewer's spot distance when there is no probe
        /// result (Map3DView), so the two never disagree.</summary>
        internal const float SweepDistanceMax = 20000f;

        /// <summary>shadowBias values, largest first: at a passing distance the largest pair that passes is kept - the
        /// most margin against acne that does not yet lift the shadow off the box.</summary>
        private static readonly float[] SweepBiases = { 0.05f, 0.01f, 0.002f };

        /// <summary>shadowNormalBias values, largest first, as above.</summary>
        private static readonly float[] SweepNormalBiases = { 0.4f, 0.1f };

        /// <summary>range = this x D: the quad sits at a tenth of the range, where the attenuation texture is flat.</summary>
        private const float RangeOfDistance = 10f;

        /// <summary>shadowNearPlane = D - this: the tilted quad's top (87 m) and the box's (30 m) stay beyond it.</summary>
        private const float NearPlaneMargin = 150f;

        /// <summary>The largest shadow map asked for. 8192 would be a 256 MB depth map for one light.</summary>
        internal const int SpotShadowMapMax = 4096;

        /// <summary>The resolution the maximum is compared against in the resolution check.</summary>
        private const int ResolutionReference = 2048;

        /// <summary>The spot's intensity; <see cref="Result.Attenuation"/> divides it back out.</summary>
        private const float LightIntensity = 0.5f;

        /// <summary>QualitySettings.shadowDistance for the probe: the quad is 500 m from the camera.</summary>
        private const float ProbeShadowDistance = 5000f;

        /// <summary>The shadow test: shade / lit at most this.</summary>
        private const float ShadowRatioMax = 0.15f;

        /// <summary>The acne test: the quad tilted this far from the light...</summary>
        private const float AcneTilt = 60f;

        /// <summary>...must read within this share of the same quad drawn not receiving shadows, at its centre.</summary>
        private const float AcneTolerance = 0.05f;

        /// <summary>The darkest lit value a verdict is drawn from: below it a ratio is noise over nothing, and a light
        /// that does not reach the quad must fail rather than divide zero by zero.</summary>
        private const float LitMin = 16f / 255f;

        /// <summary>The resolution check's view: this half-span in metres around the shadow's edge (1.5 m over 64 px).</summary>
        private const float EdgeHalfSpan = 0.75f;

        /// <summary>A pixel is on the edge above this share of lit (below it, it is the umbra)...</summary>
        private const float EdgeLow = 0.1f;

        /// <summary>...and below this share (above it, it is the lit side): the 10-90 % rise of the soft edge.</summary>
        private const float EdgeHigh = 0.9f;

        /// <summary>The maximum's edge counts as "equal" to the reference's - the request clamped - when it is more than
        /// this share of it. Twice the resolution should halve it; a pixel of quantisation must not read as a clamp.</summary>
        private const float ClampedShare = 0.75f;

        /// <summary>The emission texel, and the tolerance per channel it must read back within.</summary>
        private static readonly Color32 EmissionTexel = new Color32(128, 64, 32, 255);
        private const int EmissionTolerance = 4;

        /// <summary>The result of this session's probe, null until it ran (or when <see cref="RunProbe"/> is off).</summary>
        internal static Result Last { get; private set; }

        /// <summary>
        /// Runs the probe once per session and logs its one line; later calls return the first result. Never throws.
        /// </summary>
        /// <param name="layer">The viewer's private draw layer: the rig's camera and light see only it.</param>
        /// <param name="standard">The Standard shader the viewer's buildings use.</param>
        internal static Result Run(int layer, Shader standard)
        {
            if (Last != null || !RunProbe) return Last;

            var result = new Result
            {
                MaxDistanceOk = SweepDistances[SweepDistances.Length - 1],
                ResolutionEffective = MaxResolution(),
            };

            // cached before the attempt, so a throw is not retried every build of the session
            Last = result;

            try
            {
                if (layer < 0 || layer > 31) result.Why = "no private layer";
                else if (standard == null) result.Why = "no Standard shader";
                else Measure(result, layer, standard);
            }
            catch (Exception ex)
            {
                // Everything measured before the throw is kept for the line, but neither path is trusted.
                result.SpotShadows = false;
                result.Emission = false;
                result.Why = "the probe threw " + ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                Plugin.LogSource?.LogInfo(Line(result));
            }
            catch (Exception)
            {
                // a log line that cannot be formatted is not worth a thrown build
            }

            return result;
        }

        /// <summary>The first-frame line's clause: "spot ok|spot FAIL: why, emission ok|emission FAIL", or "not run".</summary>
        internal static string Describe()
        {
            var result = Last;
            if (result == null) return "not run";

            return (result.SpotShadows ? "spot ok" : "spot FAIL: " + result.Why) + ", " +
                   (result.Emission ? "emission ok" : "emission FAIL");
        }

        /// <summary>The probe's one log line.</summary>
        private static string Line(Result result)
        {
            var map = !result.SpotShadows ? ""
                : result.ResolutionClamped ? ", clamped"
                : result.ResolutionMeasured ? ""
                : ", edge unmeasured";

            var emission = result.Emission
                ? "pass"
                : string.Format(CultureInfo.InvariantCulture, "FAIL read {0}/{1}/{2}",
                    result.EmissionRead.r, result.EmissionRead.g, result.EmissionRead.b);

            return string.Format(CultureInfo.InvariantCulture,
                "QuestTree: 3D map light probe - spot shadows {0} (ratio {1:0.00}, D {2:0} m, near read {3:0} m, bias {4:0.000}/{5:0.00}, " +
                "map {6} px{7}), attenuation x{8:0.00}; emission {9}; scene lights on the layer: {10}{11}.",
                result.SpotShadows ? "pass" : "FAIL " + result.Why,
                result.ShadowRatio, result.MaxDistanceOk, result.NearPlaneRead, result.Bias, result.NormalBias,
                result.ResolutionEffective, map, result.Attenuation, emission,
                result.SceneLights, result.SceneLights > 0 ? ", disabled during the probe" : "");
        }

        /// <summary>The largest shadow map this machine can hold, up to <see cref="SpotShadowMapMax"/>.</summary>
        private static int MaxResolution() => Mathf.Min(SpotShadowMapMax, SystemInfo.maxTextureSize);

        /// <summary>The Standard shader's diffuse share of the light: a dielectric's specular reserve is 0.22 of it in gamma
        /// space and 0.04 in linear (as Map3DView.DiffuseShare).</summary>
        private static float DiffuseShare => QualitySettings.activeColorSpace == ColorSpace.Linear ? 0.96f : 0.78f;

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

        /// <summary>Everything the rig is made of, so the measurements take one argument and the finally finds it all.</summary>
        private sealed class Rig
        {
            public int Layer;
            public GameObject CameraGo;
            public Camera Camera;
            public GameObject LightGo;
            public Light Light;
            public RenderTexture Target;
            public Texture2D Readback;
            public Texture2D Cookie;
            public Texture2D EmissionTexture;
            public Mesh Quad;
            public Mesh Box;
            public Material White;
            public Material Caster;
            public Material Emissive;
        }

        /// <summary>One distance and bias pair, tried.</summary>
        private struct Attempt
        {
            public float Distance;
            public float Bias;
            public float NormalBias;
            public float NearRead;
            public float Lit;
            public float Ratio;
            public bool Pass;
            public string Why;
        }

        /// <summary>
        /// The rig, the sweep, the resolution check and the emission check, inside one try whose finally puts back every
        /// global in the order the viewer's RenderNow does (fog first) and destroys every object made - each on its own,
        /// so one throwing does not skip the rest.
        /// </summary>
        private static void Measure(Result result, int layer, Shader standard)
        {
            // saved before the first write
            var fogWas = RenderSettings.fog;
            var shadowsWere = QualitySettings.shadows;
            var shadowDistanceWas = QualitySettings.shadowDistance;
            var ambientModeWas = RenderSettings.ambientMode;
            var ambientLightWas = RenderSettings.ambientLight;
            var ambientProbeWas = RenderSettings.ambientProbe;
            var reflectionWas = RenderSettings.reflectionIntensity;
            var activeWas = RenderTexture.active;
            var globalsSet = false;

            var rig = new Rig { Layer = layer };
            var disabled = new List<Light>();

            try
            {
                // Before the rig's light exists, so it is never counted: the scene's lights whose mask takes in our layer
                // (a light's default mask is Everything) would light the quads too and fake a ratio or an emission read.
                // Each is added to the list BEFORE it is switched off, so the finally re-enables it whatever throws next.
                var lights = UnityEngine.Object.FindObjectsOfType<Light>();

                for (var i = 0; i < lights.Length; i++)
                {
                    var sceneLight = lights[i];
                    if (sceneLight == null || !sceneLight.isActiveAndEnabled || (sceneLight.cullingMask & (1 << layer)) == 0) continue;

                    result.SceneLights++;
                    disabled.Add(sceneLight);
                    sceneLight.enabled = false;
                }

                Build(rig, standard);

                globalsSet = true;   // before the first write: a setter that throws half way is still put back
                RenderSettings.fog = false;
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowDistance = ProbeShadowDistance;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.ambientProbe = new SphericalHarmonicsL2();   // LAST: zero, whatever the mode switch recomputed
                RenderSettings.reflectionIntensity = 0f;

                rig.Light.enabled = true;
                Sweep(rig, result);

                // the emission is judged with no light at all: whatever reads back is the emission and nothing else
                rig.Light.enabled = false;
                CheckEmission(rig, result);
            }
            finally
            {
                try { if (rig.Light != null) rig.Light.enabled = false; } catch (Exception) { /* nothing further to try */ }
                try { if (rig.Camera != null) rig.Camera.targetTexture = null; } catch (Exception) { /* as above */ }

                if (globalsSet)
                {
                    try { RenderSettings.fog = fogWas; } catch (Exception) { /* as above */ }
                    try { QualitySettings.shadows = shadowsWere; } catch (Exception) { /* as above */ }
                    try { QualitySettings.shadowDistance = shadowDistanceWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientMode = ambientModeWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.ambientLight = ambientLightWas; } catch (Exception) { /* as above */ }
                    try { RenderSettings.reflectionIntensity = reflectionWas; } catch (Exception) { /* as above */ }

                    // LAST, as in RenderNow: the probe the colour setters recomputed goes back to the scene's own
                    try { RenderSettings.ambientProbe = ambientProbeWas; } catch (Exception) { /* as above */ }
                }

                // the scene's lights back on, each on its own
                for (var i = 0; i < disabled.Count; i++)
                {
                    try { if (disabled[i] != null) disabled[i].enabled = true; } catch (Exception) { /* as above */ }
                }

                try { RenderTexture.active = activeWas; } catch (Exception) { /* as above */ }

                try { if (rig.Target != null) rig.Target.Release(); } catch (Exception) { /* as above */ }

                Drop(rig.CameraGo);
                Drop(rig.LightGo);
                Drop(rig.Target);
                Drop(rig.Readback);
                Drop(rig.Cookie);
                Drop(rig.EmissionTexture);
                Drop(rig.Quad);
                Drop(rig.Box);
                Drop(rig.White);
                Drop(rig.Caster);
                Drop(rig.Emissive);
            }
        }

        /// <summary>Destroys one object the probe made, a throw swallowed so the rest are still destroyed.</summary>
        private static void Drop(UnityEngine.Object thing)
        {
            if (thing == null) return;

            try { UnityEngine.Object.Destroy(thing); } catch (Exception) { /* nothing further to try */ }
        }

        /// <summary>Makes the rig's objects, each assigned to <paramref name="rig"/> as soon as it exists so the finally
        /// destroys it even if a later one throws.</summary>
        private static void Build(Rig rig, Shader standard)
        {
            var mask = 1 << rig.Layer;

            rig.Target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { name = "QuestTreeMap3DLightProbe", antiAliasing = 1 };
            rig.Target.Create();

            rig.Readback = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "QuestTreeMap3DLightProbe-readback" };

            rig.CameraGo = new GameObject("QuestTreeMap3DLightProbeCamera", typeof(Camera));
            rig.CameraGo.layer = rig.Layer;
            rig.Camera = rig.CameraGo.GetComponent<Camera>();
            rig.Camera.enabled = false;
            rig.Camera.clearFlags = CameraClearFlags.SolidColor;
            rig.Camera.backgroundColor = new Color(0f, 0f, 0f, 1f);
            rig.Camera.orthographic = true;
            rig.Camera.nearClipPlane = CameraNear;
            rig.Camera.farClipPlane = CameraFar;
            rig.Camera.targetTexture = rig.Target;
            rig.Camera.cullingMask = mask;
            rig.Camera.useOcclusionCulling = false;
            rig.Camera.allowMSAA = false;
            rig.Camera.allowHDR = false;
            rig.Camera.renderingPath = RenderingPath.Forward;   // the viewer's path: spot shadows are sampled in ForwardAdd
            rig.Camera.depth = -60f;                            // under the viewer's -50 and every game camera, as the viewer's
            AimWide(rig);

            // The default spot cookie is a round vignette: the lit block at (6,6) sits outside the cone's circle and would
            // read the vignette rather than the light. White, clamped: the whole square frustum at full strength. The
            // built-in spot cookie is read from the ALPHA channel, so the alpha is white too.
            rig.Cookie = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "QuestTreeMap3DLightProbe-cookie",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            rig.Cookie.SetPixels32(Filled(16, new Color32(255, 255, 255, 255)));
            rig.Cookie.Apply(false);

            rig.LightGo = new GameObject("QuestTreeMap3DLightProbeLight", typeof(Light));
            rig.LightGo.layer = rig.Layer;
            rig.Light = rig.LightGo.GetComponent<Light>();
            rig.Light.enabled = false;
            rig.Light.type = LightType.Spot;
            rig.Light.renderMode = LightRenderMode.ForcePixel;
            rig.Light.shadows = LightShadows.Soft;
            rig.Light.shadowStrength = 1f;
            rig.Light.cookie = rig.Cookie;
            rig.Light.shadowCustomResolution = MaxResolution();
            rig.Light.intensity = LightIntensity;
            rig.Light.color = Color.white;
            rig.Light.cullingMask = mask;

            rig.EmissionTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "QuestTreeMap3DLightProbe-emission",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            rig.EmissionTexture.SetPixels32(Filled(4, EmissionTexel));
            rig.EmissionTexture.Apply(false);

            rig.Quad = MakeQuad();
            rig.Box = MakeBox();

            rig.White = Map3DView.Matte(new Material(standard) { name = "QuestTreeMap3DLightProbe-white" });
            if (rig.White.HasProperty("_Color")) rig.White.SetColor("_Color", Color.white);

            rig.Caster = Map3DView.Matte(new Material(standard) { name = "QuestTreeMap3DLightProbe-caster" });
            if (rig.Caster.HasProperty("_Color")) rig.Caster.SetColor("_Color", Color.white);

            // Stage A's emission recipe, the one stage B applies to the ground: a black albedo clipped as the ground is,
            // the picture as the emission map at white, and no GI flags (no lightmapper exists to consume them)
            rig.Emissive = Map3DView.MakeCutout(Map3DView.Matte(new Material(standard) { name = "QuestTreeMap3DLightProbe-emission" }));
            if (rig.Emissive.HasProperty("_Color")) rig.Emissive.SetColor("_Color", new Color(0f, 0f, 0f, 1f));
            rig.Emissive.SetTexture("_EmissionMap", rig.EmissionTexture);
            rig.Emissive.SetColor("_EmissionColor", Color.white);

            // SELF-TEST "emission": the keyword is the variant switch - left off, the emission map is never sampled
            if (SelfTest != "emission") rig.Emissive.EnableKeyword("_EMISSION");

            rig.Emissive.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        /// <summary>
        /// The distances largest first, and at each the bias pairs largest first; the first pair that passes both tests
        /// ends the sweep. With none passing, the result reports the first pair tried at the smallest distance - the
        /// setting closest to what the viewer would fall back to - with its own reason.
        /// </summary>
        private static void Sweep(Rig rig, Result result)
        {
            var reported = default(Attempt);
            var found = false;

            for (var d = 0; d < SweepDistances.Length && !found; d++)
            {
                var near = PlaceSpot(rig, SweepDistances[d]);

                for (var b = 0; b < SweepBiases.Length && !found; b++)
                {
                    for (var n = 0; n < SweepNormalBiases.Length && !found; n++)
                    {
                        var attempt = Try(rig, SweepDistances[d], SweepBiases[b], SweepNormalBiases[n], near);

                        if (attempt.Pass)
                        {
                            reported = attempt;
                            found = true;
                        }
                        else if (b == 0 && n == 0)
                        {
                            reported = attempt;   // overwritten per distance: the smallest distance's first pair is kept
                        }
                    }
                }
            }

            result.SpotShadows = found;
            result.ShadowRatio = reported.Ratio;
            result.MaxDistanceOk = reported.Distance;
            result.NearPlaneRead = reported.NearRead;
            result.Bias = reported.Bias;
            result.NormalBias = reported.NormalBias;
            // only from a light that reached the quad: below LitMin the default 1 stands, so a divisor built on it stays finite
            if (reported.Lit >= LitMin) result.Attenuation = reported.Lit / (LightIntensity * DiffuseShare);
            result.Why = found ? null : reported.Why;

            if (!found) return;

            // the settings that passed, again, for the resolution check
            PlaceSpot(rig, reported.Distance);
            rig.Light.shadowBias = reported.Bias;
            rig.Light.shadowNormalBias = reported.NormalBias;
            CheckResolution(rig, result, reported.Distance, reported.Lit);
        }

        /// <summary>The spot at (0, D, 0) pointing straight down, its cone fitted to <see cref="FitRadius"/> (Unity's
        /// minimum of 1 degree applied here rather than left to a setter), its range and its shadow near plane. Returns
        /// the near plane as read back from the light.</summary>
        private static float PlaceSpot(Rig rig, float distance)
        {
            rig.LightGo.transform.SetPositionAndRotation(new Vector3(0f, distance, 0f), Quaternion.Euler(90f, 0f, 0f));
            rig.Light.spotAngle = Mathf.Clamp(2f * Mathf.Atan(FitRadius / distance) * Mathf.Rad2Deg, 1f, 179f);
            rig.Light.range = RangeOfDistance * distance;
            rig.Light.shadowNearPlane = distance - NearPlaneMargin;

            return rig.Light.shadowNearPlane;
        }

        /// <summary>
        /// One pair at one distance. The shadow test: the flat white quad (casting off, receiving) under the box (shadows
        /// only, invisible), lit = the block at (6,6), shade = the centre block, shade / lit at most
        /// <see cref="ShadowRatioMax"/>. The acne test: the quad alone, tilted <see cref="AcneTilt"/> degrees and casting on
        /// itself, its centre within <see cref="AcneTolerance"/> of the same quad drawn not receiving shadows - acne darkens it, a bias so large it
        /// swallows the surface does not change it, which is why the shadow test is the other half.
        /// </summary>
        private static Attempt Try(Rig rig, float distance, float bias, float normalBias, float near)
        {
            var attempt = new Attempt { Distance = distance, Bias = bias, NormalBias = normalBias, NearRead = near, Ratio = 1f };

            rig.Light.shadowBias = bias;
            rig.Light.shadowNormalBias = normalBias;

            DrawShadowScene(rig);
            var flat = Shoot(rig);

            attempt.Lit = Block(flat, 6, 6);
            var shade = Block(flat, Size / 2 - 2, Size / 2 - 2);

            if (attempt.Lit < LitMin)
            {
                attempt.Why = string.Format(CultureInfo.InvariantCulture, "lit {0:0.000} too dark to judge", attempt.Lit);
                return attempt;
            }

            attempt.Ratio = shade / attempt.Lit;

            if (attempt.Ratio > ShadowRatioMax)
            {
                attempt.Why = string.Format(CultureInfo.InvariantCulture, "ratio {0:0.00} over {1:0.00}", attempt.Ratio, ShadowRatioMax);
                return attempt;
            }

            // The reference is the same tilted quad NOT receiving shadows, not lit x cos(tilt): the desktop Standard shader's
            // diffuse is Disney's, not Lambert's, and at roughness 1 with the view on the light it reads about 1.1 x the
            // Lambert value (review 2026-09-29) - a fixed cosine would fail every pair. Same light, same place, so the only
            // difference between the two renders is the shadow the quad casts on itself.
            var tilt = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(AcneTilt, 0f, 0f), new Vector3(QuadSide, 1f, QuadSide));

            Draw(rig, rig.Quad, tilt, rig.White, ShadowCastingMode.On, false);
            var expected = Block(Shoot(rig), Size / 2 - 2, Size / 2 - 2);

            if (expected < LitMin * 0.5f)
            {
                attempt.Why = string.Format(CultureInfo.InvariantCulture, "acne: the tilted reference {0:0.000} too dark to judge", expected);
                return attempt;
            }

            Draw(rig, rig.Quad, tilt, rig.White, ShadowCastingMode.On, true);
            var centre = Block(Shoot(rig), Size / 2 - 2, Size / 2 - 2);

            if (Mathf.Abs(centre - expected) > AcneTolerance * expected)
            {
                attempt.Why = string.Format(CultureInfo.InvariantCulture, "acne: the tilted quad read {0:0.000}, {1:0.000} unshadowed",
                    centre, expected);
                return attempt;
            }

            attempt.Pass = true;
            return attempt;
        }

        /// <summary>The flat quad and, above its centre, the box. SELF-TEST "shadow": the box drawn casting Off - it casts
        /// nothing, the centre reads lit (its top face, drawn now, is lit as well), and the shadow test must fail.</summary>
        private static void DrawShadowScene(Rig rig)
        {
            Draw(rig, rig.Quad, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(QuadSide, 1f, QuadSide)),
                rig.White, ShadowCastingMode.Off, true);

            var boxCasting = SelfTest == "shadow" ? ShadowCastingMode.Off : ShadowCastingMode.ShadowsOnly;

            Draw(rig, rig.Box, Matrix4x4.TRS(new Vector3(0f, (BoxBottom + BoxTop) * 0.5f, 0f), Quaternion.identity,
                new Vector3(BoxSide, BoxTop - BoxBottom, BoxSide)), rig.Caster, boxCasting, false);
        }

        /// <summary>
        /// The shadow's edge rendered at <see cref="ResolutionReference"/> and at the maximum, in a 1.5 m view centred on
        /// where the box's edge projects (x = half side x D / (D - top)). Twice the resolution halves the soft edge's
        /// width in pixels; an edge no narrower means the request was clamped. The view goes back to the wide one after.
        /// </summary>
        private static void CheckResolution(Rig rig, Result result, float distance, float lit)
        {
            var max = MaxResolution();
            result.ResolutionEffective = max;

            if (max <= ResolutionReference) return;

            try
            {
                AimEdge(rig, BoxSide * 0.5f * distance / (distance - BoxTop));

                rig.Light.shadowCustomResolution = ResolutionReference;
                DrawShadowScene(rig);
                result.EdgeWidthReference = EdgeWidth(Shoot(rig), lit);

                rig.Light.shadowCustomResolution = max;
                DrawShadowScene(rig);
                result.EdgeWidthMax = EdgeWidth(Shoot(rig), lit);
            }
            finally
            {
                rig.Light.shadowCustomResolution = max;
                AimWide(rig);
            }

            // an edge sharper than two pixels at the reference cannot show a halving
            if (result.EdgeWidthReference < 2) return;

            result.ResolutionMeasured = true;
            result.ResolutionClamped = result.EdgeWidthMax > ClampedShare * result.EdgeWidthReference;
            if (result.ResolutionClamped) result.ResolutionEffective = ResolutionReference;
        }

        /// <summary>The pixels across the edge between <see cref="EdgeLow"/> and <see cref="EdgeHigh"/> of lit, on the
        /// centre four rows averaged (the edge runs along the view's rows' normal, so every row crosses it alike).</summary>
        private static int EdgeWidth(Color32[] pixels, float lit)
        {
            var width = 0;

            for (var x = 0; x < Size; x++)
            {
                var sum = 0f;

                for (var y = Size / 2 - 2; y < Size / 2 + 2; y++)
                {
                    var p = pixels[y * Size + x];
                    sum += p.r + p.g + p.b;
                }

                var value = sum / (4f * 3f * 255f);
                if (value > EdgeLow * lit && value < EdgeHigh * lit) width++;
            }

            return width;
        }

        /// <summary>The emission quad, flat, with the light off: its centre pixel must read the texel within
        /// <see cref="EmissionTolerance"/> on every channel.</summary>
        private static void CheckEmission(Rig rig, Result result)
        {
            Draw(rig, rig.Quad, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(QuadSide, 1f, QuadSide)),
                rig.Emissive, ShadowCastingMode.Off, false);

            var pixels = Shoot(rig);
            var read = pixels[(Size / 2) * Size + Size / 2];

            result.EmissionRead = read;
            result.Emission =
                Math.Abs(read.r - EmissionTexel.r) <= EmissionTolerance &&
                Math.Abs(read.g - EmissionTexel.g) <= EmissionTolerance &&
                Math.Abs(read.b - EmissionTexel.b) <= EmissionTolerance;
        }

        /// <summary>Queues one mesh for the rig's camera only - the viewer's camera never sees it, and the viewer's queue
        /// is for the viewer's camera, so neither consumes the other's draws. Light probes OFF: DrawMesh's default blends
        /// the scene's baked probes, which would light the quads in place of the zeroed ambient.</summary>
        private static void Draw(Rig rig, Mesh mesh, Matrix4x4 matrix, Material material, ShadowCastingMode cast, bool receive)
        {
            Graphics.DrawMesh(mesh, matrix, material, rig.Layer, rig.Camera, 0, null, cast, receive, null, LightProbeUsage.Off);
        }

        /// <summary>Renders the queued draws and reads the whole target back (as MeshProbeView.Sample: ReadPixels with the
        /// target active, the previous active texture put back in the finally).</summary>
        private static Color32[] Shoot(Rig rig)
        {
            rig.Camera.Render();

            var previous = RenderTexture.active;

            try
            {
                RenderTexture.active = rig.Target;
                rig.Readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);

                return rig.Readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>The mean of a 4x4 block's r, g and b from its lower-left pixel, 0 to 1 (the light and the albedo are
        /// white, so the three agree).</summary>
        private static float Block(Color32[] pixels, int x0, int y0)
        {
            var sum = 0f;

            for (var y = y0; y < y0 + 4; y++)
            {
                for (var x = x0; x < x0 + 4; x++)
                {
                    var p = pixels[y * Size + x];
                    sum += p.r + p.g + p.b;
                }
            }

            return sum / (16f * 3f * 255f);
        }

        /// <summary>The wide view: the whole quad from straight above.</summary>
        private static void AimWide(Rig rig)
        {
            rig.CameraGo.transform.SetPositionAndRotation(new Vector3(0f, CameraHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            rig.Camera.orthographicSize = QuadSide * 0.5f;
        }

        /// <summary>The edge view: <see cref="EdgeHalfSpan"/> around x on the quad's centre line, from straight above.
        /// The camera's right is +x, so the view's rows cross the box's edge, which runs along z.</summary>
        private static void AimEdge(Rig rig, float x)
        {
            rig.CameraGo.transform.SetPositionAndRotation(new Vector3(x, CameraHeight, 0f), Quaternion.Euler(90f, 0f, 0f));
            rig.Camera.orthographicSize = EdgeHalfSpan;
        }

        /// <summary>A unit quad in the XZ plane, facing up (clockwise seen from above, Unity's front face), with uvs.</summary>
        private static Mesh MakeQuad()
        {
            var mesh = new Mesh { name = "QuestTreeMap3DLightProbe-quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>A unit cube, one quad per face with its own normals. Each face's winding is checked against its normal
        /// (Unity's front face is the one Cross(b - a, c - a) points out of) rather than written out by hand.</summary>
        private static Mesh MakeBox()
        {
            var normals = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            var vertices = new Vector3[24];
            var vertexNormals = new Vector3[24];
            var triangles = new int[36];

            for (var f = 0; f < 6; f++)
            {
                var n = normals[f];
                var u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                var v = Vector3.Cross(n, u);
                var c = n * 0.5f;
                var i = f * 4;

                vertices[i] = c - u * 0.5f - v * 0.5f;
                vertices[i + 1] = c - u * 0.5f + v * 0.5f;
                vertices[i + 2] = c + u * 0.5f + v * 0.5f;
                vertices[i + 3] = c + u * 0.5f - v * 0.5f;

                for (var k = 0; k < 4; k++) vertexNormals[i + k] = n;

                var outward = Vector3.Dot(Vector3.Cross(vertices[i + 1] - vertices[i], vertices[i + 2] - vertices[i]), n) > 0f;
                var t = f * 6;

                triangles[t] = i;
                triangles[t + 1] = outward ? i + 1 : i + 2;
                triangles[t + 2] = outward ? i + 2 : i + 1;
                triangles[t + 3] = i;
                triangles[t + 4] = outward ? i + 2 : i + 3;
                triangles[t + 5] = outward ? i + 3 : i + 2;
            }

            var mesh = new Mesh { name = "QuestTreeMap3DLightProbe-box" };
            mesh.vertices = vertices;
            mesh.normals = vertexNormals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>An array of one colour, for the probe's small textures.</summary>
        private static Color32[] Filled(int count, Color32 colour)
        {
            var pixels = new Color32[count];
            for (var i = 0; i < count; i++) pixels[i] = colour;
            return pixels;
        }
    }
}
