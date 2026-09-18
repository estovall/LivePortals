using System.Collections.Generic;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.Rendering;

namespace LivePortals
{
    /// <summary>
    /// One capture as the window draws it. Per cube face: a background texture (near things removed and filled in,
    /// alpha 0 where the capture saw sky), a foreground texture (only the near things, alpha 0 elsewhere; null when
    /// the face has none), and the depth grids the two relief meshes are built from.
    /// </summary>
    internal class PortalCapture
    {
        public Texture2D[] Faces = new Texture2D[6];
        public Texture2D[] Fronts = new Texture2D[6];
        /// <summary>Light from torches, fires and glowing things (not the sun or sky), added on top of the background untinted; null when the capture has none.</summary>
        public Texture2D[] Locals = new Texture2D[6];
        /// <summary>The same for the foreground layer (the flames themselves, mostly).</summary>
        public Texture2D[] LocalsFront = new Texture2D[6];
        /// <summary>Relief meshes per face, built by the loader from the grids; the reliefs share them and this owns them.</summary>
        public Mesh[] Back = new Mesh[6], Skirt = new Mesh[6], Shell = new Mesh[6], Front = new Mesh[6];
        /// <summary>Nodes per edge of the depth grids; cells per edge is Grid - 1.</summary>
        public int Grid;
        public float DepthRange = 120f;
        public FaceGrids[] Grids = new FaceGrids[6];
        public Color Sun;        // directional light colour * intensity
        public Color Ambient;    // ambient colour
        public Color Fog;        // fog colour
        public float DayFraction;
        public long TakenAt;     // unix seconds
        public float AverageLuminance = 0.3f; // of the forward face, geometry only, for the spill light
        public float AverageLocalLuminance;   // the part of it that is not sunlight, and so does not follow the time of day
        public Color AverageColor = Color.white;

        public void Destroy()
        {
            for (int i = 0; i < Faces.Length; i++) if (Faces[i] != null) Object.Destroy(Faces[i]);
            for (int i = 0; i < Fronts.Length; i++) if (Fronts[i] != null) Object.Destroy(Fronts[i]);
            for (int i = 0; i < Locals.Length; i++) if (Locals[i] != null) Object.Destroy(Locals[i]);
            for (int i = 0; i < LocalsFront.Length; i++) if (LocalsFront[i] != null) Object.Destroy(LocalsFront[i]);
            for (int i = 0; i < 6; i++)
            {
                if (Back[i] != null) Object.Destroy(Back[i]);
                if (Skirt[i] != null) Object.Destroy(Skirt[i]);
                if (Shell[i] != null) Object.Destroy(Shell[i]);
                if (Front[i] != null) Object.Destroy(Front[i]);
            }
        }
    }

    /// <summary>View depth in metres at the grid nodes and cells of one face. See Layers for what each holds.</summary>
    internal class FaceGrids
    {
        /// <summary>Tangent of half the face's field of view: faces are captured a little wider than 90 degrees so neighbours overlap instead of leaving a crack between them.</summary>
        public float Tan = 1f;
        public float[] BgNode;  // n*n, always set
        public float[] BgCell;  // (n-1)*(n-1), always set
        public float[] FgNode;  // n*n, 0 = no foreground at the node
        public float[] FgCell;  // (n-1)*(n-1), 0 = no foreground in the cell
    }

    /// <summary>A set of captures of one portal from several points across its ring, each with its offset from the ring centre in the portal's frame.</summary>
    internal class CaptureSet
    {
        public readonly List<PortalCapture> Captures = new List<PortalCapture>();
        public readonly List<Vector3> Offsets = new List<Vector3>();
        public GrassSet Grass;
        public long TakenAt;
        /// <summary>How many viewpoints the stored capture has; Captures may hold fewer (far windows load only the primary).</summary>
        public int AvailablePoints;

        /// <summary>
        /// Faces captured from the secondary points: all but up. (0.8.1 to 0.8.5 also left out the one looking back.
        /// A portal with beams or a wall right behind it needs exactly that one: from far back you look through the
        /// gaps in directions the centre point had blocked.)
        /// </summary>
        internal static bool SecondaryFace(int face) => face != 4;

        public PortalCapture Primary => Captures.Count > 0 ? Captures[0] : null;

        public void Destroy()
        {
            foreach (var c in Captures) c.Destroy();
            Captures.Clear();
            Offsets.Clear();
        }

        /// <summary>Take over the captures of a set loaded later (further viewpoints of the same capture).</summary>
        public void Append(CaptureSet more)
        {
            Captures.AddRange(more.Captures);
            Offsets.AddRange(more.Offsets);
            more.Captures.Clear();
            more.Offsets.Clear();
        }

        /// <summary>Drop all but the first count viewpoints.</summary>
        public void Trim(int count)
        {
            for (int i = count; i < Captures.Count; i++) Captures[i].Destroy();
            if (Captures.Count > count) { Captures.RemoveRange(count, Captures.Count - count); Offsets.RemoveRange(count, Offsets.Count - count); }
        }

        /// <summary>
        /// Capture point offsets in the portal's frame (x right, y up): the centre first, then the one a player's
        /// camera needs most, above it (the camera sits above the player, so it looks over near things from higher
        /// up than the ring centre), then the two sides, then below.
        /// </summary>
        internal static Vector3[] PointOffsets(int count)
        {
            var all = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 1.1f, 0f),
                new Vector3(0.75f, 0f, 0f), new Vector3(-0.75f, 0f, 0f),
                new Vector3(0f, -0.7f, 0f),
            };
            count = Mathf.Clamp(count, 1, all.Length);
            var res = new Vector3[count];
            System.Array.Copy(all, res, count);
            return res;
        }

        /// <summary>
        /// A secondary viewpoint is only worth having in open space with a clear line to the centre. Inside a beam,
        /// a wall or the ground (portals get built into tight places) its relief is a wall of near surfaces that
        /// spoils the window from every angle. Try it closer to the centre, then give it up.
        /// </summary>
        internal static bool FindClearance(Vector3 centre, Quaternion rot, ref Vector3 offset, int solidMask)
        {
            foreach (float f in new[] { 1f, 0.65f, 0.4f })
            {
                Vector3 pos = centre + rot * (offset * f);
                if (Physics.CheckSphere(pos, 0.3f, solidMask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.Linecast(centre, pos, solidMask, QueryTriggerInteraction.Ignore)) continue;
                offset *= f;
                return true;
            }
            return false;
        }
    }

    /// <summary>What the capture camera saw on one face, straight off the GPU. Rows run bottom-up like texture data.</summary>
    internal class RawFace
    {
        public Color32[] Col;   // alpha 255 where something was drawn, 0 on sky (rgb is the fog colour there)
        public Color32[] Local; // the same view lit by nothing but local lights and emission; null when not captured
        public bool[] Sky;
        public float[] Depth;   // view depth in metres per pixel, DepthRange on sky and beyond
    }

    internal class RawPoint
    {
        public RawFace[] Faces = new RawFace[6];
        public Vector3 Offset;
        public int Res, Step;
        public float FaceTan = 1f;
        public float DepthRange;
        public Color Sun, Ambient, Fog;
        public float DayFraction;
        public float AverageLuminance = 0.3f;
        public float AverageLocalLuminance;
        public Color AverageColor = Color.white;
        public float SkyFraction, DiffFraction, MedianDepth; // of the forward face, for the log
    }

    internal static class Capture
    {
        // Face order: +Z (forward), -Z, -X (left), +X (right), +Y (up), -Y (down); rotations relative to the portal.
        internal static readonly Quaternion[] FaceRotations =
        {
            Quaternion.identity,
            Quaternion.Euler(0f, 180f, 0f),
            Quaternion.Euler(0f, -90f, 0f),
            Quaternion.Euler(0f, 90f, 0f),
            Quaternion.Euler(-90f, 0f, 0f),
            Quaternion.Euler(90f, 0f, 0f),
        };

        private enum DepthMethod { Unknown, ZBuffer, DepthTexture, Rays }

        // Decided once per session by comparing each way of reading GPU depth against physics rays.
        private static DepthMethod _method = DepthMethod.Unknown;
        private static bool _flipY, _reversedZ;

        private const float Near = 0.08f;
        internal const float FaceFov = 92f;
        internal static readonly float FaceTan = Mathf.Tan(FaceFov * 0.5f * Mathf.Deg2Rad);

        /// <summary>The cameras and targets of one capture. Built on the main thread; CaptureRun uses it over several frames.</summary>
        internal class Rig
        {
            public Camera Cam;       // sky mask and depth: nothing attached
            public Camera ColorCam;  // colour: carries the game's fog and ambient occlusion when CaptureFog is on
            public GameObject Go, ColorGo;
            public PostProcessingProfile Profile;
            public int Res, Step;
            public float Far, DepthRange, WaterLevel;
            public bool Hdr, Msaa, FogOn;
            public RenderTexture Color, A, B, Z, F;
            public Texture2D TexC, TexA, TexB, TexF; // for reads done on the spot: the probes, and every read when async readback is unavailable
            public CommandBuffer DepthCopy;
            /// <summary>The flames kept in the capture, and a depth-only stand-in at each one's place (see RenderFace step 4).</summary>
            public List<ParticleSystemRenderer> Flames = new List<ParticleSystemRenderer>();
            public List<GameObject> Proxies = new List<GameObject>();
            public int[] FlameLayers = new int[0];
            /// <summary>Depth comes from physics rays (no GPU depth read agreed with them, or none has been checked yet).</summary>
            public bool Rays => _method == DepthMethod.Rays || _method == DepthMethod.Unknown;
        }

        /// <summary>Cameras and targets for a capture, or null without a game camera. Main thread.</summary>
        internal static Rig MakeRig()
        {
            var gc = GameCamera.instance;
            if (gc == null || gc.m_camera == null) { Plugin.Log.LogWarning("LivePortals: no game camera, cannot capture."); return null; }
            var main = gc.m_camera;
            int step = Mathf.Max(2, Mathf.CeilToInt(Plugin.CaptureResolution.Value / (float)Plugin.MeshGrid.Value));
            int res = Mathf.Max(step * 8, Plugin.CaptureResolution.Value / step * step);
            float depthRange = Plugin.DepthRange.Value;

            var go = new GameObject("LivePortals_CaptureCamera");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            cam.targetTexture = null;
            cam.fieldOfView = FaceFov;
            cam.aspect = 1f;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.depthTextureMode = DepthTextureMode.None;
            cam.nearClipPlane = Near;
            int mask = main.cullingMask;
            if (gc.m_skyCamera != null) mask &= ~gc.m_skyCamera.cullingMask; // the sky is drawn live, never baked
            mask &= ~(1 << Plugin.FaceLayer);
            // Grass and the rest of the game's instanced clutter stay out of the capture: the window draws them as
            // real geometry (see GrassSet). The game submits them to every camera, on a layer of their own.
            int clutterLayer = LayerMask.NameToLayer("InstanceRenderer");
            if (clutterLayer >= 0) mask &= ~(1 << clutterLayer);
            cam.cullingMask = mask;
            cam.usePhysicalProperties = false;
            cam.ResetProjectionMatrix();

            // Colour comes from a second copy that carries the game's own post-processing stack, cut down to the two
            // effects that belong to the scene rather than to the eye: the distance fog (in this game an image
            // effect on the main camera, tinted toward the sun, not something the shaders do) and ambient
            // occlusion. Bloom, grading, vignette and the rest are applied to the window when the game draws it.
            var colorGo = new GameObject("LivePortals_CaptureColorCamera");
            var colorCam = colorGo.AddComponent<Camera>();
            colorCam.CopyFrom(cam);
            colorCam.enabled = false;
            colorCam.targetTexture = null;
            colorCam.aspect = 1f;
            colorCam.ResetProjectionMatrix();
            PostProcessingProfile profile = null;
            string look = "off";
            if (Plugin.CaptureFog.Value)
            {
                try
                {
                    var src = main.GetComponent<PostProcessingBehaviour>();
                    if (src != null && src.profile != null)
                    {
                        profile = Object.Instantiate(src.profile);
                        profile.debugViews.enabled = false;
                        profile.antialiasing.enabled = false;
                        profile.screenSpaceReflection.enabled = false;
                        profile.depthOfField.enabled = false;
                        profile.motionBlur.enabled = false;
                        profile.eyeAdaptation.enabled = false;
                        profile.bloom.enabled = false;
                        profile.colorGrading.enabled = false;
                        profile.userLut.enabled = false;
                        profile.chromaticAberration.enabled = false;
                        profile.grain.enabled = false;
                        profile.vignette.enabled = false;
                        profile.dithering.enabled = false;
                        colorGo.AddComponent<PostProcessingBehaviour>().profile = profile;
                        look = $"fog {(profile.fog.enabled ? "on" : "off in the game's profile")}, ao {(profile.ambientOcclusion.enabled ? "on" : "off")}";
                    }
                    else look = "no post-processing on the main camera";
                }
                catch (System.Exception e) { look = "failed: " + e.Message; }
                try
                {
                    // The game's ambient occlusion is Amplify Occlusion, its tint and strength set per environment.
                    // It is what makes interiors and crevices dark (and teal); without it they come out flat and warm.
                    var srcAo = main.GetComponent<AmplifyOcclusionEffect>();
                    if (srcAo != null && srcAo.enabled)
                    {
                        var ao = colorGo.AddComponent<AmplifyOcclusionEffect>();
                        foreach (var fld in typeof(AmplifyOcclusionEffect).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            if (!fld.IsLiteral && !fld.IsInitOnly) fld.SetValue(ao, fld.GetValue(srcAo));
                        ao.FilterEnabled = false; // temporal: needs a history of frames a one-off render does not have
                        look += $", amplify ao {ao.Intensity:0.00} {ao.ApplyMethod}";
                    }
                    else look += ", amplify ao off";
                }
                catch (System.Exception e) { look += ", amplify ao failed: " + e.Message; }
            }
            if (!_loggedLook)
            {
                _loggedLook = true;
                Plugin.Log.LogInfo($"LivePortals: capture look: {look}; path {main.actualRenderingPath}, hdr {main.allowHDR}, fog {RenderSettings.fog} {RenderSettings.fogMode} density {RenderSettings.fogDensity:0.0000}");
            }

            var rig = new Rig
            {
                Cam = cam, ColorCam = colorCam, Go = go, ColorGo = colorGo, Profile = profile, Res = res, Step = step, DepthRange = depthRange,
                Far = cam.farClipPlane, Hdr = cam.allowHDR, Msaa = cam.allowMSAA, FogOn = RenderSettings.fog,
                WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f,
                Color = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32),
                A = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32),
                B = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32),
                Z = new RenderTexture(res, res, 24, RenderTextureFormat.Depth),
                F = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear),
                TexC = new Texture2D(res, res, TextureFormat.RGBA32, false),
                TexA = new Texture2D(res, res, TextureFormat.RGBA32, false),
                TexB = new Texture2D(res, res, TextureFormat.RGBA32, false),
                TexF = new Texture2D(res, res, TextureFormat.RFloat, false, true),
                DepthCopy = new CommandBuffer { name = "LivePortals depth copy" },
            };
            rig.Z.filterMode = FilterMode.Point;
            rig.F.filterMode = FilterMode.Point;
            rig.Z.Create(); rig.F.Create(); rig.A.Create();
            rig.DepthCopy.Blit(BuiltinRenderTextureType.Depth, rig.F);
            return rig;
        }

        internal static void DestroyRig(Rig rig)
        {
            if (rig == null) return;
            RenderSettings.fog = rig.FogOn;
            RenderTexture.active = null;
            if (rig.Cam != null) rig.Cam.targetTexture = null;
            if (rig.ColorCam != null) rig.ColorCam.targetTexture = null;
            Object.Destroy(rig.TexA); Object.Destroy(rig.TexB); Object.Destroy(rig.TexC); Object.Destroy(rig.TexF);
            foreach (var rt in new[] { rig.Color, rig.A, rig.B, rig.Z, rig.F }) { rt.Release(); Object.Destroy(rt); }
            rig.DepthCopy.Release();
            foreach (var go in rig.Proxies) if (go != null) { var mr = go.GetComponent<MeshRenderer>(); if (mr != null && mr.sharedMaterial != null) Object.Destroy(mr.sharedMaterial); Object.Destroy(go); }
            rig.Proxies.Clear();
            Object.Destroy(rig.Go);
            Object.Destroy(rig.ColorGo);
            if (rig.Profile != null) Object.Destroy(rig.Profile);
        }

        /// <summary>
        /// A quad the size of each flame's bounds, on the capture layer, that only writes depth. Flames write none
        /// themselves; rendered alone these give every flame pixel the distance of its fire (RenderFace step 4).
        /// </summary>
        internal static void MakeProxies(Rig rig, Hidden hidden)
        {
            rig.Flames.Clear();
            var mat = WindowMaterial.MakeDepthOnly();
            if (mat == null || hidden.Flames.Count == 0) return;
            foreach (var ps in hidden.Flames)
            {
                if (ps == null) continue;
                // The soft glow billboards around a fire are flame-coloured over a wide area; they stay in the picture
                // but get no stand-in, or the wall and pillars behind them come forward with them (0.9.11).
                string n = (ps.gameObject.name + "|" + (ps.sharedMaterial != null ? ps.sharedMaterial.name : "")).ToLowerInvariant();
                if (n.Contains("glow") || n.Contains("light") || n.Contains("smoke") || n.Contains("distort")) continue;
                // A quad through the flame's centre, turned to face each capture point as it is rendered: its depth is
                // the fire's own, not a sphere's near side.
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "LivePortals_FlameProxy";
                var col = go.GetComponent<Collider>();
                if (col != null) Object.Destroy(col);
                go.layer = Plugin.FaceLayer;
                var b = ps.bounds;
                go.transform.position = b.center;
                go.transform.localScale = Vector3.Max(new Vector3(Mathf.Max(b.size.x, b.size.z), b.size.y, 1f), new Vector3(0.25f, 0.25f, 1f));
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(mat);
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;
                rig.Proxies.Add(go);
                rig.Flames.Add(ps);
            }
            rig.FlameLayers = new int[rig.Flames.Count];
            Object.Destroy(mat);
        }

        internal static void EnsureProbed(Rig rig, Vector3 pos, Quaternion rot)
        {
            if (_method == DepthMethod.Unknown) Probe(rig, pos, rot);
        }

        // ------------------------------------------------------------------
        // Reading renders back without waiting for the GPU
        // ------------------------------------------------------------------
        private static bool _asyncChecked, _asyncOk, _asyncFlip;

        /// <summary>
        /// Whether the GPU can hand pixels back asynchronously here, and which way up they come (that differs
        /// between graphics APIs). Decided once, by comparing with a plain read of the same picture.
        /// </summary>
        internal static void CheckAsync(Rig rig, Vector3 pos, Quaternion rot)
        {
            if (_asyncChecked) return;
            _asyncChecked = true;
            _asyncOk = false;
            if (!Plugin.AsyncReadback.Value || !SystemInfo.supportsAsyncGPUReadback)
            {
                Plugin.Log.LogInfo("LivePortals: async GPU readback " + (Plugin.AsyncReadback.Value ? "is not supported here" : "is off") + "; captures wait for the GPU instead.");
                return;
            }
            try
            {
                var cam = rig.ColorCam;
                cam.transform.SetPositionAndRotation(pos, rot);
                cam.targetTexture = rig.Color; cam.Render(); cam.targetTexture = null;
                var req = AsyncGPUReadback.Request(rig.Color, 0, TextureFormat.RGBA32);
                RenderTexture.active = rig.Color;
                rig.TexC.ReadPixels(new Rect(0, 0, rig.Res, rig.Res), 0, 0, false);
                RenderTexture.active = null;
                var sync = rig.TexC.GetPixels32();
                req.WaitForCompletion();
                if (req.hasError) { Plugin.Log.LogInfo("LivePortals: async GPU readback failed its check; captures wait for the GPU instead."); return; }
                var px = req.GetData<Color32>().ToArray();
                if (px.Length != sync.Length) { Plugin.Log.LogInfo($"LivePortals: async GPU readback returned {px.Length} pixels for {sync.Length}; captures wait for the GPU instead."); return; }
                int res = rig.Res;
                long same = 0, flipped = 0, samples = 0;
                for (int y = 0; y < res; y += 3)
                    for (int x = 0; x < res; x += 3)
                    {
                        Color32 s0 = sync[y * res + x];
                        same += Diff(s0, px[y * res + x]);
                        flipped += Diff(s0, px[(res - 1 - y) * res + x]);
                        samples++;
                    }
                _asyncFlip = flipped < same * 0.5f;
                long best = _asyncFlip ? flipped : same;
                _asyncOk = best < samples * 12;
                Plugin.Log.LogInfo($"LivePortals: async GPU readback check: {(_asyncOk ? "usable" : "does NOT match a plain read; captures wait for the GPU instead")}, {(_asyncFlip ? "flipped" : "same way up")}; mean difference per pixel same {same / (float)samples:0.0}, flipped {flipped / (float)samples:0.0}.");
            }
            catch (System.Exception e)
            {
                _asyncOk = false;
                Plugin.Log.LogInfo("LivePortals: async GPU readback check threw (" + e.Message + "); captures wait for the GPU instead.");
            }
        }

        private static int Diff(Color32 a, Color32 b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        /// <summary>
        /// The renders of one face, issued now: colour with the game's fog; a black/white clear pair with fog off
        /// whose differing pixels were never drawn (sky); and the GPU's own depth. Their pixels reach f later
        /// (or now, without async readback). The caller has hidden the player, the portal and the windows.
        /// </summary>
        internal static void RenderFace(Rig rig, FaceRaw f)
        {
            var cam = rig.Cam; var colorCam = rig.ColorCam;
            cam.transform.SetPositionAndRotation(f.Pos, f.Rot);
            colorCam.transform.SetPositionAndRotation(f.Pos, f.Rot);
            f.Flip = _asyncOk && _asyncFlip;
            try
            {
                // 1. Colour, with the game's fog as it is right now.
                RenderSettings.fog = rig.FogOn;
                cam.allowHDR = rig.Hdr; cam.allowMSAA = rig.Msaa;
                cam.depthTextureMode = DepthTextureMode.None;
                colorCam.backgroundColor = RenderSettings.fogColor;
                colorCam.targetTexture = rig.Color; colorCam.Render(); colorCam.targetTexture = null;
                Read(rig.Color, rig.TexC, rig.Res, f, 0);
                // 1b. The same view with the sun, the sky light and the fog off: only what torches, fires and
                //     glowing things contribute. The window adds this part back untinted, so torchlight does not
                //     fade with the sun, and the flames (which are emissive) survive the night.
                if (Plugin.CaptureLocalLight.Value)
                {
                    RenderLocalLight(rig);
                    Read(rig.A, rig.TexA, rig.Res, f, 4);
                }
                // 2. Sky mask: no fog, cleared black and then white. Where the two differ, nothing (or only
                //    something thin, like the haze dome the game hangs over the whole sky) was drawn, and if
                //    the depth buffer is empty there too it is sky. 0.7.1/0.8.0 tested "stayed black" instead
                //    and found no sky at all, because of that dome. The depth pass is a render of its own:
                //    its colour output is not usable (0.7.0 took the mask from it).
                RenderSettings.fog = false;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rig.A; cam.Render(); cam.targetTexture = null;
                Read(rig.A, rig.TexA, rig.Res, f, 1);
                cam.backgroundColor = Color.white;
                cam.targetTexture = rig.B; cam.Render(); cam.targetTexture = null;
                Read(rig.B, rig.TexB, rig.Res, f, 2);
                // 3. Depth per pixel.
                if (f.NeedGpu) { RenderDepthPass(rig, _method); Read(rig.F, rig.TexF, rig.Res, f, 3); }
                // 4. Flames: which pixels are flame (the flames rendered alone, on their own layer) and how far
                //    away each fire is (the depth-only stand-ins rendered alone). Flames write no depth, so
                //    without this a hearth is painted onto the wall behind it.
                if (f.NeedGpu && rig.Proxies.Count > 0)
                {
                    int mask = cam.cullingMask;
                    try
                    {
                        for (int k = 0; k < rig.Flames.Count; k++)
                        {
                            var ps = rig.Flames[k];
                            if (ps == null) continue;
                            rig.FlameLayers[k] = ps.gameObject.layer;
                            ps.gameObject.layer = Plugin.FaceLayer;
                        }
                        cam.cullingMask = 1 << Plugin.FaceLayer;
                        cam.allowHDR = rig.Hdr; cam.allowMSAA = rig.Msaa;
                        cam.backgroundColor = Color.black;
                        cam.targetTexture = rig.A; cam.Render(); cam.targetTexture = null;
                        Read(rig.A, rig.TexA, rig.Res, f, 5);
                    }
                    finally
                    {
                        for (int k = 0; k < rig.Flames.Count; k++) if (rig.Flames[k] != null) rig.Flames[k].gameObject.layer = rig.FlameLayers[k];
                    }
                    foreach (var go in rig.Proxies)
                        if (go != null)
                        {
                            Vector3 to = go.transform.position - f.Pos;
                            if (to.sqrMagnitude > 1e-4f) go.transform.rotation = Quaternion.LookRotation(to);
                            go.GetComponent<MeshRenderer>().enabled = true;
                        }
                    try { RenderDepthPass(rig, _method); Read(rig.F, rig.TexF, rig.Res, f, 6); }
                    finally
                    {
                        foreach (var go in rig.Proxies) if (go != null) go.GetComponent<MeshRenderer>().enabled = false;
                        cam.cullingMask = mask;
                    }
                }
            }
            finally { RenderSettings.fog = rig.FogOn; }
        }

        private static readonly int AmbientId = Shader.PropertyToID("_AmbientColor"), SunColorId = Shader.PropertyToID("_SunColor"), SunFogId = Shader.PropertyToID("_SunFogColor");
        private static readonly List<Light> _suns = new List<Light>();

        /// <summary>The colour of the scene lit by local lights and emission alone, into rig.A. Sun, sky light, reflections and fog are switched off for the render and put back after.</summary>
        private static void RenderLocalLight(Rig rig)
        {
            var cam = rig.Cam;
            _suns.Clear();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.enabled && l.type == LightType.Directional) { l.enabled = false; _suns.Add(l); }
            var mode = RenderSettings.ambientMode; Color amb = RenderSettings.ambientLight; float ambI = RenderSettings.ambientIntensity, refl = RenderSettings.reflectionIntensity;
            Color gAmb = Shader.GetGlobalColor(AmbientId), gSun = Shader.GetGlobalColor(SunColorId), gFog = Shader.GetGlobalColor(SunFogId);
            bool fog = RenderSettings.fog;
            try
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.ambientIntensity = 0f;
                RenderSettings.reflectionIntensity = 0f;
                Shader.SetGlobalColor(AmbientId, Color.black);
                Shader.SetGlobalColor(SunColorId, Color.black);
                Shader.SetGlobalColor(SunFogId, Color.black);
                RenderSettings.fog = false;
                cam.allowHDR = rig.Hdr; cam.allowMSAA = rig.Msaa;
                cam.depthTextureMode = DepthTextureMode.None;
                cam.backgroundColor = Color.black;
                cam.targetTexture = rig.A; cam.Render(); cam.targetTexture = null;
            }
            finally
            {
                foreach (var l in _suns) if (l != null) l.enabled = true;
                _suns.Clear();
                RenderSettings.ambientMode = mode; RenderSettings.ambientLight = amb; RenderSettings.ambientIntensity = ambI; RenderSettings.reflectionIntensity = refl;
                Shader.SetGlobalColor(AmbientId, gAmb); Shader.SetGlobalColor(SunColorId, gSun); Shader.SetGlobalColor(SunFogId, gFog);
                RenderSettings.fog = fog;
            }
        }

        /// <summary>Start reading a render target back into slot (0 colour, 1/2 sky pair, 3 depth, 4 local light). Without async readback it is read now.</summary>
        private static void Read(RenderTexture rt, Texture2D into, int res, FaceRaw f, int slot)
        {
            if (_asyncOk)
            {
                f.Req[slot] = AsyncGPUReadback.Request(rt, 0, slot == 3 || slot == 6 ? TextureFormat.RFloat : TextureFormat.RGBA32);
                f.Issued[slot] = true;
                return;
            }
            RenderTexture.active = rt;
            into.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
            RenderTexture.active = null;
            if (slot == 3 || slot == 6) f.SetDepth(slot, into.GetPixelData<float>(0).ToArray()); else f.Set(slot, into.GetPixels32());
        }

        /// <summary>
        /// Any thread: the sky mask from the black/white pair, the colour with its alpha and exposure, and the
        /// metric depth. Depth stays null without GPU depth (the caller fills it from rays). Face 0 also leaves
        /// its statistics in the point.
        /// </summary>
        internal static RawFace Compose(FaceRaw f, RawPoint pt, float far, float exposure, float waterLevel)
        {
            int res = pt.Res; float depthRange = pt.DepthRange;
            if (f.Flip)
            {
                FlipRows(f.RawCol, res); FlipRows(f.SkyA, res); FlipRows(f.SkyB, res);
                if (f.Gpu != null) FlipRows(f.Gpu, res);
                if (f.RawLocal != null) FlipRows(f.RawLocal, res);
                if (f.FlameMask != null) FlipRows(f.FlameMask, res);
                if (f.ProxyGpu != null) FlipRows(f.ProxyGpu, res);
                f.Flip = false;
            }
            var col = f.RawCol; var skyA = f.SkyA; var skyB = f.SkyB; var gpu = f.Gpu; var local = f.RawLocal;
            bool rays = gpu == null;
            long lumSum = 0, rSum = 0, gSum = 0, bSum = 0, localSum = 0; int count = 0;
            var sky = new bool[res * res];
            int diffSky = 0;
            for (int p = 0; p < col.Length; p++)
            {
                Color32 a = skyA[p], b = skyB[p];
                bool isSky = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 60;
                if (isSky && f.Face == 0) diffSky++;
                if (isSky && !rays)
                {
                    // Nothing solid within the depth range behind it. (0.8.1 asked for an empty depth
                    // buffer and again found no sky: whatever the game draws up there has depth.)
                    int y = p / res;
                    isSky = Linear(gpu[(_flipY ? res - 1 - y : y) * res + p % res], _reversedZ, far) >= depthRange;
                }
                Color32 c = col[p];
                if (isSky)
                {
                    c.a = 0; // never drawn: sky. The fog colour stays in rgb so cut-out edges do not fringe dark.
                    col[p] = c;
                    sky[p] = true;
                    continue;
                }
                if (exposure != 1f)
                {
                    c.r = (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * exposure), 0, 255);
                    c.g = (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * exposure), 0, 255);
                    c.b = (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * exposure), 0, 255);
                    if (local != null)
                    {
                        Color32 l = local[p];
                        l.r = (byte)Mathf.Clamp(Mathf.RoundToInt(l.r * exposure), 0, 255);
                        l.g = (byte)Mathf.Clamp(Mathf.RoundToInt(l.g * exposure), 0, 255);
                        l.b = (byte)Mathf.Clamp(Mathf.RoundToInt(l.b * exposure), 0, 255);
                        local[p] = l;
                    }
                }
                c.a = 255;
                col[p] = c;
                if (f.Face == 0 && (p & 15) == 0)
                {
                    lumSum += (c.r * 54 + c.g * 183 + c.b * 19) >> 8; rSum += c.r; gSum += c.g; bSum += c.b; count++;
                    if (local != null) { Color32 l = local[p]; localSum += (l.r * 54 + l.g * 183 + l.b * 19) >> 8; }
                }
            }
            var raw = new RawFace { Col = col, Sky = sky, Local = local, Depth = rays ? null : MetricDepth(gpu, sky, res, far, depthRange, f.Pos, f.Rot, waterLevel) };
            if (raw.Depth != null && f.FlameMask != null && f.ProxyGpu != null)
            {
                // Flame pixels take the depth of their fire's stand-in, wherever that is nearer than what was behind.
                // Only where the flame is most of what the pixel shows: its faint glow spills onto the wall or
                // pillar behind, and pulling those pixels forward tore chunks out of the pillars (0.9.11).
                for (int p = 0; p < res * res; p++)
                {
                    Color32 m = f.FlameMask[p];
                    int ml = m.r + m.g + m.b;
                    if (ml < 330) continue;
                    Color32 cc = col[p];
                    if (ml * 4 < (cc.r + cc.g + cc.b) * 3) continue;
                    int y = p / res;
                    float d = Linear(f.ProxyGpu[(_flipY ? res - 1 - y : y) * res + p % res], _reversedZ, far);
                    if (d >= far * 0.5f || d >= raw.Depth[p]) continue;
                    raw.Depth[p] = Mathf.Max(0.05f, d);
                    if (sky[p]) { sky[p] = false; Color32 c = col[p]; c.a = 255; col[p] = c; }
                }
            }
            if (f.Face == 0)
            {
                int skyCount = 0;
                var sample = new List<float>();
                for (int p = 0; p < sky.Length; p += 7) { if (sky[p]) skyCount++; else if (raw.Depth != null) sample.Add(raw.Depth[p]); }
                sample.Sort();
                pt.SkyFraction = skyCount * 7f / sky.Length;
                pt.DiffFraction = diffSky / (float)sky.Length;
                pt.MedianDepth = sample.Count > 0 ? sample[sample.Count / 2] : 0f;
                if (count > 0)
                {
                    pt.AverageLuminance = lumSum / (255f * count);
                    pt.AverageLocalLuminance = Mathf.Min(pt.AverageLuminance, localSum / (255f * count));
                    pt.AverageColor = new Color(rSum / (255f * count), gSum / (255f * count), bSum / (255f * count), 1f);
                }
            }
            return raw;
        }

        private static void FlipRows<T>(T[] a, int res)
        {
            var row = new T[res];
            for (int y = 0; y < res / 2; y++)
            {
                int o = y * res, q = (res - 1 - y) * res;
                System.Array.Copy(a, o, row, 0, res);
                System.Array.Copy(a, q, a, o, res);
                System.Array.Copy(row, 0, a, q, res);
            }
        }

        // ------------------------------------------------------------------
        // GPU depth
        // ------------------------------------------------------------------

        /// <summary>
        /// A render kept only for the depth the GPU wrote. ZBuffer: render into our own depth buffer and copy it
        /// out. DepthTexture: ask the engine for its camera depth texture (what the game's water and effects read)
        /// and copy that. Both need no shader of ours. Returns raw device depth; the colour of this render is not
        /// to be trusted (with ZBuffer it comes out black in this game).
        /// </summary>
        private static void RenderDepthPass(Rig rig, DepthMethod method)
        {
            var cam = rig.Cam;
            cam.allowHDR = false; cam.allowMSAA = false; // straight into the target, no intermediate buffer
            RenderTexture.active = rig.F;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = null;
            if (method == DepthMethod.ZBuffer)
            {
                cam.depthTextureMode = DepthTextureMode.None;
                // Clear the depth ourselves: with the deferred path the camera's own clear may not reach a buffer
                // handed over this way, and sky pixels would keep the last face's depth.
                Graphics.SetRenderTarget(rig.A.colorBuffer, rig.Z.depthBuffer);
                GL.Clear(true, true, Color.black, 1f);
                RenderTexture.active = null;
                cam.SetTargetBuffers(rig.A.colorBuffer, rig.Z.depthBuffer);
                cam.Render();
                cam.targetTexture = null;
                Graphics.Blit(rig.Z, rig.F);
            }
            else
            {
                cam.depthTextureMode = DepthTextureMode.Depth;
                cam.AddCommandBuffer(CameraEvent.AfterEverything, rig.DepthCopy);
                cam.targetTexture = rig.A;
                cam.Render();
                cam.targetTexture = null;
                cam.RemoveCommandBuffer(CameraEvent.AfterEverything, rig.DepthCopy);
                cam.depthTextureMode = DepthTextureMode.None;
            }
        }

        /// <summary>A depth render read back on the spot (the probe).</summary>
        private static float[] RenderDepth(Rig rig, DepthMethod method)
        {
            RenderDepthPass(rig, method);
            RenderTexture.active = rig.F;
            rig.TexF.ReadPixels(new Rect(0, 0, rig.Res, rig.Res), 0, 0, false);
            RenderTexture.active = null;
            return rig.TexF.GetPixelData<float>(0).ToArray();
        }

        private static float Linear(float d, bool reversed, float far)
        {
            float inv = reversed ? (1f / Near - 1f / far) * d + 1f / far : (1f / far - 1f / Near) * d + 1f / Near;
            return inv > 1e-6f ? 1f / inv : far;
        }

        /// <summary>
        /// Find out which way of reading depth works in this game build: render the down and forward faces with
        /// each, and compare against physics rays where those hit solid ground. Whatever agrees is used from then
        /// on; if nothing does, depth falls back to the rays themselves. The scores go to the log either way.
        /// </summary>
        private static void Probe(Rig rig, Vector3 pos, Quaternion rot)
        {
            int res = rig.Res;
            int solidMask = SolidMask(out _);
            const int N = 40;
            var methods = new[] { DepthMethod.ZBuffer, DepthMethod.DepthTexture };
            int[,,] agree = new int[2, 2, 2];
            int hits = 0;
            bool fogOn = RenderSettings.fog;
            RenderSettings.fog = false;
            rig.Cam.backgroundColor = Color.black;
            foreach (int face in new[] { 5, 0 })
            {
                Quaternion faceRot = rot * FaceRotations[face];
                rig.Cam.transform.SetPositionAndRotation(pos, faceRot);
                var raw = new float[2][];
                for (int m = 0; m < 2; m++)
                {
                    try { raw[m] = RenderDepth(rig, methods[m]); }
                    catch (System.Exception e) { Plugin.Log.LogWarning("LivePortals: depth probe " + methods[m] + " threw: " + e.Message); raw[m] = null; }
                }
                for (int gy = 0; gy < N; gy++)
                    for (int gx = 0; gx < N; gx++)
                    {
                        int x = (int)((gx + 0.5f) / N * res), y = (int)((gy + 0.5f) / N * res);
                        Vector3 local = new Vector3(((x + 0.5f) / res - 0.5f) * 2f * FaceTan, ((y + 0.5f) / res - 0.5f) * 2f * FaceTan, 1f);
                        float len = local.magnitude;
                        if (!Physics.Raycast(pos, faceRot * (local / len), out RaycastHit hit, 100f * len, solidMask, QueryTriggerInteraction.Ignore)) continue;
                        float zr = hit.distance / len;
                        if (zr < 0.3f) continue;
                        hits++;
                        for (int m = 0; m < 2; m++)
                        {
                            if (raw[m] == null) continue;
                            for (int f = 0; f < 2; f++)
                                for (int r = 0; r < 2; r++)
                                {
                                    float z = Linear(raw[m][(f == 1 ? res - 1 - y : y) * res + x], r == 1, rig.Far);
                                    if (Mathf.Abs(z - zr) < Mathf.Max(0.3f, zr * 0.1f)) agree[m, f, r]++;
                                }
                        }
                    }
            }
            RenderSettings.fog = fogOn;

            var sb = new System.Text.StringBuilder();
            int bestM = -1, bestF = 0, bestR = 0, best = 0;
            for (int m = 0; m < 2; m++)
                for (int f = 0; f < 2; f++)
                    for (int r = 0; r < 2; r++)
                    {
                        sb.Append($" {methods[m]}{(f == 1 ? "/flipped" : "")}{(r == 1 ? "/reversedZ" : "")}={agree[m, f, r]}");
                        if (agree[m, f, r] > best) { best = agree[m, f, r]; bestM = m; bestF = f; bestR = r; }
                    }
            if (hits < 40)
            {
                Plugin.Log.LogInfo($"LivePortals: depth probe had only {hits} ray hits; using rays for this capture and probing again next time.");
                return;
            }
            if (best >= hits * 0.6f)
            {
                _method = methods[bestM]; _flipY = bestF == 1; _reversedZ = bestR == 1;
                Plugin.Log.LogInfo($"LivePortals: depth from {_method}{(_flipY ? ", flipped" : "")}{(_reversedZ ? ", reversed Z" : "")}: {best}/{hits} rays agree. All:{sb} (platform reversed Z {SystemInfo.usesReversedZBuffer}, far {rig.Far:0})");
            }
            else
            {
                _method = DepthMethod.Rays;
                Plugin.Log.LogWarning($"LivePortals: no GPU depth read agrees with physics rays ({hits} hits):{sb}. Falling back to ray depth; silhouettes will be coarse.");
            }
        }

        /// <summary>Device depth to view depth in metres, clamped to the range; sky at the range; the sea surface (which draws without depth) from its plane.</summary>
        private static float[] MetricDepth(float[] gpu, bool[] sky, int res, float far, float range, Vector3 origin, Quaternion faceRot, float waterLevel)
        {
            var depth = new float[res * res];
            // World-space height per unit of view depth along each pixel's ray: y of faceRot * (lx, ly, 1).
            float yx = (faceRot * Vector3.right).y, yy = (faceRot * Vector3.up).y, yz = (faceRot * Vector3.forward).y;
            float above = origin.y - waterLevel;
            for (int y = 0; y < res; y++)
            {
                float ly = ((y + 0.5f) / res - 0.5f) * 2f * FaceTan;
                int row = (_flipY ? res - 1 - y : y) * res;
                for (int x = 0; x < res; x++)
                {
                    int p = y * res + x;
                    if (sky[p]) { depth[p] = range; continue; }
                    float z = Linear(gpu[row + x], _reversedZ, far);
                    if (above > 0.05f)
                    {
                        float wy = yx * ((x + 0.5f) / res - 0.5f) * 2f * FaceTan + yy * ly + yz;
                        if (wy < -1e-4f) { float zw = above / -wy; if (zw < z) z = zw; }
                    }
                    depth[p] = z < 0.05f ? 0.05f : (z > range ? range : z);
                }
            }
            return depth;
        }

        // ------------------------------------------------------------------
        // Ray depth (fallback)
        // ------------------------------------------------------------------
        internal static int SolidMask(out int waterMask)
        {
            int solidMask = ~((1 << Plugin.FaceLayer) | (1 << 2)); // 2 = Ignore Raycast
            int waterLayer = LayerMask.NameToLayer("Water");
            waterMask = waterLayer >= 0 ? 1 << waterLayer : 0;
            return solidMask & ~waterMask;
        }

        /// <summary>
        /// One physics ray per grid node, spread to the pixels around it. Things without colliders (foliage, grass)
        /// take the nearest hit below them in the same column. Coarse; only used when no GPU depth read works.
        /// </summary>
        internal static float[] RayDepthPerPixel(Vector3 origin, Quaternion faceRot, bool[] sky, int res, int step, float range)
        {
            int n = res / step + 1;
            var node = new float[n * n];
            int solidMask = SolidMask(out int waterMask);
            for (int y = 0; y < n; y++)
            {
                int py = Mathf.Min(res - 1, y * step);
                for (int x = 0; x < n; x++)
                {
                    int px = Mathf.Min(res - 1, x * step);
                    if (sky[py * res + px]) { node[y * n + x] = range; continue; }
                    Vector3 local = new Vector3((x / (float)(n - 1) - 0.5f) * 2f * FaceTan, (y / (float)(n - 1) - 0.5f) * 2f * FaceTan, 1f);
                    float len = local.magnitude;
                    Vector3 dir = faceRot * (local / len);
                    float best = range * len;
                    if (Physics.Raycast(origin, dir, out RaycastHit hit, best, solidMask, QueryTriggerInteraction.Ignore)) best = hit.distance;
                    if (waterMask != 0 && Physics.Raycast(origin, dir, out RaycastHit wh, best, waterMask, QueryTriggerInteraction.Collide)) best = wh.distance;
                    node[y * n + x] = Mathf.Min(range, best / len);
                }
            }
            for (int x = 0; x < n; x++)
            {
                float carry = -1f;
                for (int y = 0; y < n; y++)
                {
                    int i = y * n + x;
                    bool isSky = sky[Mathf.Min(res - 1, y * step) * res + Mathf.Min(res - 1, x * step)];
                    if (node[i] < range) carry = node[i];
                    else if (!isSky && carry > 0f) node[i] = carry;
                }
            }
            var depth = new float[res * res];
            for (int y = 0; y < res; y++)
            {
                int ny = (y + step / 2) / step;
                for (int x = 0; x < res; x++)
                {
                    int p = y * res + x;
                    depth[p] = sky[p] ? range : Mathf.Max(0.05f, node[ny * n + (x + step / 2) / step]);
                }
            }
            return depth;
        }

        private static bool _loggedLook;

        /// <summary>What a capture must not see, switched off for the frames it renders in and back on right after, so the game's own frame never shows the gap.</summary>
        internal class Hidden
        {
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Light> Lights = new List<Light>();
            public readonly List<Collider> Colliders = new List<Collider>();
            public readonly List<ParticleSystemRenderer> Flames = new List<ParticleSystemRenderer>(); // kept visible; see MakeProxies

            public void Hide()
            {
                foreach (var r in Renderers) if (r != null) r.enabled = false;
                foreach (var l in Lights) if (l != null) l.enabled = false;
                foreach (var c in Colliders) if (c != null) c.enabled = false;
                PortalWindow.SetAllVisible(false);
            }

            public void Show()
            {
                foreach (var r in Renderers) if (r != null) r.enabled = true;
                foreach (var l in Lights) if (l != null) l.enabled = true;
                foreach (var c in Colliders) if (c != null) c.enabled = true;
                PortalWindow.SetAllVisible(true);
            }
        }

        /// <summary>A particle effect that belongs to a light source and stays small: a flame, embers, sparks. Smoke, mist, weather and the like do not qualify.</summary>
        private static bool IsFlame(ParticleSystemRenderer ps)
        {
            if (ps.bounds.size.magnitude > 3f) return false;
            var root = ps.GetComponentInParent<ZNetView>();
            Transform top = root != null ? root.transform : (ps.transform.parent != null ? ps.transform.parent : ps.transform);
            foreach (var l in top.GetComponentsInChildren<Light>(false))
                if (l.enabled && l.type != LightType.Directional && Vector3.Distance(l.transform.position, ps.transform.position) < 1.5f) return true;
            return false;
        }

        /// <summary>Collect and hide everything a capture must not see. Show() puts it back.</summary>
        internal static Hidden HideForCapture(TeleportWorld portal)
        {
            var h = new Hidden();
            var hidden = h.Renderers; var hiddenLights = h.Lights; var hiddenColliders = h.Colliders;
            var lp = Player.m_localPlayer;
            if (lp != null)
            {
                foreach (var r in lp.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
                foreach (var c in lp.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; hiddenColliders.Add(c); }
            }
            if (portal != null)
            {
                // The portal itself: its frame, runes, tag sign, swirl and glow all sit around the camera standing in
                // the ring. The pane covers the ring at the other end anyway.
                foreach (var r in portal.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
                foreach (var l in portal.GetComponentsInChildren<Light>(false)) if (l.enabled) { l.enabled = false; hiddenLights.Add(l); }
                foreach (var c in portal.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; hiddenColliders.Add(c); }
            }
            // Hugin and Munin like to perch on portals, right next to the capture point.
            foreach (var raven in Object.FindObjectsByType<Raven>(FindObjectsSortMode.None))
            {
                if (portal != null && Vector3.Distance(raven.transform.position, portal.transform.position) > 12f) continue;
                foreach (var r in raven.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
                foreach (var c in raven.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; hiddenColliders.Add(c); }
            }
            // Particles (flames, smoke, sparks, mist, snow, falling leaves) have no depth: they would be painted onto
            // whatever wall or hill lies behind them, once per viewpoint, and smeared from any other angle. A torch
            // still lights its wall in the capture; its flame is left out.
            Vector3 at = portal != null ? portal.transform.position : Vector3.zero;
            // Except flames: the small effects that sit at a light (torches, fires, braziers, sconces). They are
            // painted onto whatever is right behind them, which for a flame a hand's breadth from its wall is
            // where it belongs, and a torch without its flame reads as unlit however well it lights the wall.
            foreach (var ps in Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None))
            {
                if (!ps.enabled || (portal != null && Vector3.Distance(ps.transform.position, at) > 150f)) continue;
                if (Plugin.CaptureFlames.Value && IsFlame(ps)) { h.Flames.Add(ps); continue; }
                ps.enabled = false;
                hidden.Add(ps);
            }
            PortalWindow.SetAllVisible(false);
            return h;
        }
    }
}
