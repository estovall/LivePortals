using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// One capture: six cube faces (alpha 0 where the capture saw sky), a view-depth map per face, and the
    /// lighting it was taken under.
    /// </summary>
    internal class PortalCapture
    {
        public Texture2D[] Faces = new Texture2D[6];
        /// <summary>Background-only version of each face (near objects removed and filled in), drawn at the far distance behind the relief.</summary>
        public Texture2D[] Backdrops = new Texture2D[6];
        /// <summary>View depth per face in metres, row-major, DepthSize x DepthSize; DepthRange means "sky / far".</summary>
        public float[][] Depth = new float[6][];
        public int DepthSize;
        public float DepthRange = 120f;
        public Color Sun;        // directional light colour * intensity
        public Color Ambient;    // ambient colour
        public Color Fog;        // fog colour
        public float DayFraction;
        public long TakenAt;     // unix seconds
        public float AverageLuminance = 0.3f; // of the forward face, geometry only, for the spill light
        public Color AverageColor = Color.white;

        public void Destroy()
        {
            for (int i = 0; i < Faces.Length; i++) if (Faces[i] != null) Object.Destroy(Faces[i]);
            for (int i = 0; i < Backdrops.Length; i++) if (Backdrops[i] != null) Object.Destroy(Backdrops[i]);
        }
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

        /// <summary>
        /// Render six 90-degree faces from pos/rot with a copy of the main camera (geometry only, no sky layers).
        /// Per face: one render with the game's own fog for colour; two with fog off, cleared black and white, whose
        /// differing pixels were never drawn (sky, alpha 0); then a physics ray per relief vertex for depth.
        /// The local player (renderers and colliders), this portal's swirl effect and lights, and every window are
        /// hidden meanwhile.
        /// </summary>
        internal static PortalCapture Take(Vector3 pos, Quaternion rot, TeleportWorld portal)
        {
            var gc = GameCamera.instance;
            if (gc == null || gc.m_camera == null) { Plugin.Log.LogWarning("LivePortals: no game camera, cannot capture."); return null; }
            var main = gc.m_camera;
            int res = Plugin.CaptureResolution.Value;
            int dres = Mathf.Clamp(Plugin.DepthGrid.Value + 1, 9, res);
            float depthRange = Plugin.DepthRange.Value;
            var go = new GameObject("LivePortals_CaptureCamera");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            cam.targetTexture = null;
            cam.fieldOfView = 90f;
            cam.aspect = 1f;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.depthTextureMode = DepthTextureMode.None;
            cam.nearClipPlane = 0.08f;
            float colorFar = main.farClipPlane;
            int mask = main.cullingMask;
            if (gc.m_skyCamera != null) mask &= ~gc.m_skyCamera.cullingMask; // the sky is drawn live, never baked
            mask &= ~(1 << Plugin.FaceLayer);
            cam.cullingMask = mask;
            cam.ResetProjectionMatrix();

            var rtColor = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var rtA = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var rtB = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var texC = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var texA = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var texB = new Texture2D(res, res, TextureFormat.RGBA32, false);

            var hidden = new List<Renderer>();
            var hiddenLights = new List<Light>();
            var hiddenColliders = new List<Collider>();
            HideForCapture(portal, hidden, hiddenLights, hiddenColliders);
            bool fogOn = RenderSettings.fog;
            var cap = new PortalCapture { DepthSize = dres, DepthRange = depthRange };
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    cam.transform.SetPositionAndRotation(pos, rot * FaceRotations[i]);

                    // 1. Colour, with the game's fog as it is right now.
                    RenderSettings.fog = fogOn;
                    cam.farClipPlane = colorFar;
                    cam.backgroundColor = RenderSettings.fogColor;
                    Render(cam, rtColor, texC, res);

                    // 2. Sky mask: no fog, black then white clear; whatever differs was never drawn.
                    RenderSettings.fog = false;
                    cam.backgroundColor = Color.black; Render(cam, rtA, texA, res);
                    var skyA = texA.GetPixels32();
                    cam.backgroundColor = Color.white; Render(cam, rtB, texB, res);
                    var skyB = texB.GetPixels32();

                    var col = texC.GetPixels32();
                    float exposure = Plugin.CaptureExposure.Value;
                    long lumSum = 0, rSum = 0, gSum = 0, bSum = 0; int count = 0;
                    var sky = new bool[res * res];
                    for (int p = 0; p < col.Length; p++)
                    {
                        Color32 a = skyA[p], b = skyB[p];
                        int diff = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
                        if (diff > 60)
                        {
                            col[p] = new Color32(0, 0, 0, 0); // never drawn: sky
                            sky[p] = true;
                            continue;
                        }

                        Color32 c = col[p];
                        if (exposure != 1f)
                        {
                            c.r = (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * exposure), 0, 255);
                            c.g = (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * exposure), 0, 255);
                            c.b = (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * exposure), 0, 255);
                        }
                        c.a = 255;
                        col[p] = c;
                        if (i == 0 && (p & 15) == 0) { lumSum += (c.r * 54 + c.g * 183 + c.b * 19) >> 8; rSum += c.r; gSum += c.g; bSum += c.b; count++; }
                    }
                    var face = new Texture2D(res, res, TextureFormat.RGBA32, true);
                    face.wrapMode = TextureWrapMode.Clamp;
                    face.filterMode = FilterMode.Bilinear;
                    face.SetPixels32(col);
                    face.Apply(true, false);
                    cap.Faces[i] = face;
                    // 3. Depth: one ray per relief vertex against the world's colliders (terrain, pieces, trees,
                    //    water, creatures). Shader-independent; sky pixels are not cast.
                    cap.Depth[i] = RaycastDepth(pos, rot * FaceRotations[i], sky, res, dres, depthRange, out int cast, out float median);
                    Plugin.Dbg($"face {i}: {cast} rays, median depth {median:0.0} m");
                    cap.Backdrops[i] = BuildBackdrop(col, sky, res, cap.Depth[i], dres, depthRange);
                    if (i == 0 && count > 0)
                    {
                        cap.AverageLuminance = lumSum / (255f * count);
                        cap.AverageColor = new Color(rSum / (255f * count), gSum / (255f * count), bSum / (255f * count), 1f);
                    }
                }
            }
            finally
            {
                RenderSettings.fog = fogOn;
                foreach (var r in hidden) if (r != null) r.enabled = true;
                foreach (var l in hiddenLights) if (l != null) l.enabled = true;
                foreach (var c in hiddenColliders) if (c != null) c.enabled = true;
                PortalWindow.SetAllVisible(true);
                Object.Destroy(texA); Object.Destroy(texB); Object.Destroy(texC);
                rtA.Release(); rtB.Release(); rtColor.Release();
                Object.Destroy(rtA); Object.Destroy(rtB); Object.Destroy(rtColor);
                Object.Destroy(go);
            }

            Lighting.Sample(out cap.Sun, out cap.Ambient, out cap.Fog, out cap.DayFraction);
            cap.TakenAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return cap;
        }

        /// <summary>
        /// View depth at each grid node of a 90-degree face, from a physics ray along that node's direction.
        /// Solid colliders first; water is a trigger on its own layer, so it gets a second ray and the nearer wins.
        /// Nodes whose pixel is sky (or that hit nothing within range) sit at range.
        /// </summary>
        private static float[] RaycastDepth(Vector3 origin, Quaternion faceRot, bool[] sky, int res, int n, float range, out int cast, out float median)
        {
            var depth = new float[n * n];
            int solidMask = ~((1 << Plugin.FaceLayer) | (1 << 2)); // 2 = Ignore Raycast
            int waterLayer = LayerMask.NameToLayer("Water");
            int waterMask = waterLayer >= 0 ? 1 << waterLayer : 0;
            solidMask &= ~waterMask;
            var hits = new List<float>(n * n);
            cast = 0;
            for (int y = 0; y < n; y++)
            {
                float v = y / (float)(n - 1);
                int py = Mathf.RoundToInt(v * (res - 1));
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)(n - 1);
                    int px = Mathf.RoundToInt(u * (res - 1));
                    if (sky[py * res + px]) { depth[y * n + x] = range; continue; }
                    Vector3 local = new Vector3((u - 0.5f) * 2f, (v - 0.5f) * 2f, 1f);
                    float len = local.magnitude;
                    Vector3 dir = faceRot * (local / len);
                    float best = range * len; // ray distance that corresponds to view depth = range
                    cast++;
                    if (Physics.Raycast(origin, dir, out RaycastHit hit, best, solidMask, QueryTriggerInteraction.Ignore)) best = hit.distance;
                    if (waterMask != 0 && Physics.Raycast(origin, dir, out RaycastHit wh, best, waterMask, QueryTriggerInteraction.Collide)) best = wh.distance;
                    float z = Mathf.Min(range, best / len);
                    depth[y * n + x] = z;
                    if (z < range) hits.Add(z);
                }
            }
            if (hits.Count > 0) { hits.Sort(); median = hits[hits.Count / 2]; } else median = range;
            return depth;
        }

        private const int BackdropRes = 256;

        /// <summary>
        /// A background-only copy of the face at lower resolution: pixels on the near side of a depth jump are
        /// removed and filled from the surrounding far pixels (or sky), so what shows through a gap in the relief
        /// is plausible background instead of a second copy of the near object at the wrong distance.
        /// </summary>
        private static Texture2D BuildBackdrop(Color32[] col, bool[] sky, int res, float[] depth, int n, float range)
        {
            int b = BackdropRes;
            // Foreground nodes: clearly nearer than the farthest thing within two nodes of them.
            var fg = new bool[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = depth[y * n + x];
                    if (d >= range * 0.98f) continue;
                    float localMax = d;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = Mathf.Clamp(x + dx, 0, n - 1), yy = Mathf.Clamp(y + dy, 0, n - 1);
                            float v = depth[yy * n + xx];
                            if (v > localMax) localMax = v;
                        }
                    fg[y * n + x] = d < localMax * 0.7f && localMax - d > 0.8f;
                }

            var px = new Color32[b * b];
            var state = new byte[b * b]; // 0 unknown, 1 colour, 2 sky
            for (int y = 0; y < b; y++)
            {
                int sy = y * (res - 1) / (b - 1), ny = y * (n - 1) / (b - 1);
                for (int x = 0; x < b; x++)
                {
                    int sx = x * (res - 1) / (b - 1), nx = x * (n - 1) / (b - 1);
                    int p = y * b + x;
                    if (sky[sy * res + sx]) { state[p] = 2; px[p] = new Color32(0, 0, 0, 0); }
                    else if (fg[ny * n + nx]) state[p] = 0;
                    else { state[p] = 1; px[p] = col[sy * res + sx]; }
                }
            }
            // Dilate known pixels into the unknown ones, a ring per pass. Sky wins where it touches.
            var next = new byte[b * b];
            for (int pass = 0; pass < 48; pass++)
            {
                bool any = false;
                System.Array.Copy(state, next, state.Length);
                for (int y = 0; y < b; y++)
                    for (int x = 0; x < b; x++)
                    {
                        int p = y * b + x;
                        if (state[p] != 0) continue;
                        int r = 0, g = 0, bl = 0, cnt = 0; bool skyN = false;
                        if (x > 0) Acc(state[p - 1], px[p - 1], ref r, ref g, ref bl, ref cnt, ref skyN);
                        if (x < b - 1) Acc(state[p + 1], px[p + 1], ref r, ref g, ref bl, ref cnt, ref skyN);
                        if (y > 0) Acc(state[p - b], px[p - b], ref r, ref g, ref bl, ref cnt, ref skyN);
                        if (y < b - 1) Acc(state[p + b], px[p + b], ref r, ref g, ref bl, ref cnt, ref skyN);
                        if (skyN) { next[p] = 2; px[p] = new Color32(0, 0, 0, 0); any = true; }
                        else if (cnt > 0) { next[p] = 1; px[p] = new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(bl / cnt), 255); any = true; }
                    }
                var t = state; state = next; next = t;
                if (!any) break;
            }
            for (int p = 0; p < px.Length; p++) if (state[p] == 0) px[p] = new Color32(0, 0, 0, 0);

            var tex = new Texture2D(b, b, TextureFormat.RGBA32, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        private static void Acc(byte s, Color32 c, ref int r, ref int g, ref int b, ref int cnt, ref bool sky)
        {
            if (s == 2) sky = true;
            else if (s == 1) { r += c.r; g += c.g; b += c.b; cnt++; }
        }

        private static void Render(Camera cam, RenderTexture rt, Texture2D into, int res)
        {
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            RenderTexture.active = rt;
            into.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
            RenderTexture.active = null;
        }

        /// <summary>
        /// Depth at grid nodes: the minimum over the block around each node, so thin near things (a post, a
        /// branch) keep their distance instead of averaging away into the background.
        /// </summary>
        internal static float[] Downsample(float[] full, int res, int n)
        {
            var outp = new float[n * n];
            float step = (res - 1) / (float)(n - 1);
            int rad = Mathf.Max(1, Mathf.RoundToInt(step * 0.6f));
            for (int y = 0; y < n; y++)
            {
                int cy = Mathf.RoundToInt(y * step);
                for (int x = 0; x < n; x++)
                {
                    int cx = Mathf.RoundToInt(x * step);
                    float m = float.MaxValue;
                    for (int dy = -rad; dy <= rad; dy++)
                    {
                        int yy = Mathf.Clamp(cy + dy, 0, res - 1);
                        for (int dx = -rad; dx <= rad; dx++)
                        {
                            int xx = Mathf.Clamp(cx + dx, 0, res - 1);
                            float v = full[yy * res + xx];
                            if (v < m) m = v;
                        }
                    }
                    outp[y * n + x] = m;
                }
            }
            return outp;
        }

        private static void HideForCapture(TeleportWorld portal, List<Renderer> hidden, List<Light> hiddenLights, List<Collider> hiddenColliders)
        {
            var lp = Player.m_localPlayer;
            if (lp != null)
            {
                foreach (var r in lp.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
                foreach (var c in lp.GetComponentsInChildren<Collider>(false)) if (c.enabled) { c.enabled = false; hiddenColliders.Add(c); }
            }
            if (portal != null)
            {
                // The swirl and the portal's own glow would tint everything from a camera standing in the ring.
                if (portal.m_target_found != null)
                    foreach (var r in portal.m_target_found.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
                foreach (var l in portal.GetComponentsInChildren<Light>(false)) if (l.enabled) { l.enabled = false; hiddenLights.Add(l); }
            }
            PortalWindow.SetAllVisible(false);
        }
    }

    internal static class Lighting
    {
        private static readonly int AmbientId = Shader.PropertyToID("_AmbientColor");

        internal static void Sample(out Color sun, out Color ambient, out Color fog, out float dayFraction)
        {
            sun = Color.black; ambient = RenderSettings.ambientLight; fog = RenderSettings.fogColor; dayFraction = 0.5f;
            var env = EnvMan.instance;
            if (env != null)
            {
                if (env.m_dirLight != null) sun = env.m_dirLight.color * env.m_dirLight.intensity;
                dayFraction = env.GetDayFraction();
            }
            Color shaderAmb = Shader.GetGlobalColor(AmbientId);
            if (shaderAmb.maxColorComponent > 0.001f) ambient = shaderAmb;
        }

        internal static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>Overall light level a scene is lit with: mostly the sun, some ambient, never zero.</summary>
        internal static float Level(Color sun, Color ambient) => 0.65f * Luminance(sun) + 0.35f * Luminance(ambient) + 0.02f;

        /// <summary>
        /// Tint that turns a capture taken under (sun0, amb0, fog0) into roughly what the same scene looks like now.
        /// Brightness follows the light level ratio; hue follows the fog colour ratio, which tracks dawn/dusk/night.
        /// </summary>
        internal static Color Tint(PortalCapture cap, float strength)
        {
            Sample(out var sun, out var amb, out var fog, out _);
            float ratio = Level(sun, amb) / Level(cap.Sun, cap.Ambient);
            ratio = Mathf.Clamp(ratio, 0.04f, 4f);
            Color chroma = Color.white;
            float capFogLum = Luminance(cap.Fog), nowFogLum = Luminance(fog);
            if (capFogLum > 0.01f && nowFogLum > 0.01f)
            {
                Color a = cap.Fog / capFogLum, b = fog / nowFogLum;
                chroma = new Color(Mathf.Clamp(b.r / Mathf.Max(0.05f, a.r), 0.3f, 3f),
                                   Mathf.Clamp(b.g / Mathf.Max(0.05f, a.g), 0.3f, 3f),
                                   Mathf.Clamp(b.b / Mathf.Max(0.05f, a.b), 0.3f, 3f), 1f);
                chroma = Color.Lerp(Color.white, chroma, 0.6f);
            }
            Color tint = chroma * ratio;
            tint.a = 1f;
            return Color.Lerp(Color.white, tint, strength);
        }
    }
}
