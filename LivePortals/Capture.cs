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
        /// Render six 90-degree faces from pos/rot with a copy of the main camera (geometry only, no sky camera).
        /// Per face: one render with the game's own fog for colour; two with fog off, cleared black and white, whose
        /// differing pixels were never drawn (sky, alpha 0); two with linear black/white fog over DepthRange, whose
        /// difference is the fog factor and therefore the view depth of every pixel, shader-independent.
        /// The local player, this portal's swirl effect and every window are hidden meanwhile.
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
            int mask = main.cullingMask;
            if (gc.m_skyCamera != null) mask &= ~(gc.m_skyCamera.cullingMask & ~main.cullingMask);
            cam.cullingMask = mask;
            cam.ResetProjectionMatrix();

            var rtColor = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var rtA = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var rtB = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var texC = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var texA = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var texB = new Texture2D(res, res, TextureFormat.RGBA32, false);

            var hidden = new List<Renderer>();
            HideForCapture(portal, hidden);
            bool fogOn = RenderSettings.fog; var fogMode = RenderSettings.fogMode; var fogColor = RenderSettings.fogColor;
            float fogStart = RenderSettings.fogStartDistance, fogEnd = RenderSettings.fogEndDistance, fogDensity = RenderSettings.fogDensity;
            var cap = new PortalCapture { DepthSize = dres, DepthRange = depthRange };
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    cam.transform.SetPositionAndRotation(pos, rot * FaceRotations[i]);

                    // 1. Colour, with the game's fog as it is right now.
                    RenderSettings.fog = fogOn; RenderSettings.fogMode = fogMode; RenderSettings.fogColor = fogColor;
                    RenderSettings.fogStartDistance = fogStart; RenderSettings.fogEndDistance = fogEnd; RenderSettings.fogDensity = fogDensity;
                    cam.backgroundColor = fogColor;
                    Render(cam, rtColor, texC, res);

                    // 2. Sky mask: no fog, black then white clear.
                    RenderSettings.fog = false;
                    cam.backgroundColor = Color.black; Render(cam, rtA, texA, res);
                    var skyA = texA.GetPixels32();
                    cam.backgroundColor = Color.white; Render(cam, rtB, texB, res);
                    var skyB = texB.GetPixels32();

                    // 3. Depth: linear fog 0..DepthRange, black then white. B-A = 1-factor = z/DepthRange.
                    RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
                    RenderSettings.fogStartDistance = 0f; RenderSettings.fogEndDistance = depthRange;
                    RenderSettings.fogColor = Color.black; cam.backgroundColor = Color.black; Render(cam, rtA, texA, res);
                    var fogA = texA.GetPixels32();
                    RenderSettings.fogColor = Color.white; cam.backgroundColor = Color.white; Render(cam, rtB, texB, res);
                    var fogB = texB.GetPixels32();

                    var col = texC.GetPixels32();
                    float exposure = Plugin.CaptureExposure.Value;
                    long lumSum = 0, rSum = 0, gSum = 0, bSum = 0; int count = 0;
                    var depthFull = new float[res * res];
                    for (int p = 0; p < col.Length; p++)
                    {
                        Color32 a = skyA[p], b = skyB[p];
                        int diff = Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
                        if (diff > 60)
                        {
                            col[p] = new Color32(0, 0, 0, 0); // never drawn: sky
                            depthFull[p] = depthRange;
                            continue;
                        }
                        Color32 fa = fogA[p], fb = fogB[p];
                        float dz = ((fb.r - fa.r) + (fb.g - fa.g) + (fb.b - fa.b)) / (3f * 255f);
                        depthFull[p] = Mathf.Clamp01(dz) * depthRange;

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
                    cap.Depth[i] = Downsample(depthFull, res, dres);
                    if (i == 0 && count > 0)
                    {
                        cap.AverageLuminance = lumSum / (255f * count);
                        cap.AverageColor = new Color(rSum / (255f * count), gSum / (255f * count), bSum / (255f * count), 1f);
                    }
                }
            }
            finally
            {
                RenderSettings.fog = fogOn; RenderSettings.fogMode = fogMode; RenderSettings.fogColor = fogColor;
                RenderSettings.fogStartDistance = fogStart; RenderSettings.fogEndDistance = fogEnd; RenderSettings.fogDensity = fogDensity;
                foreach (var r in hidden) if (r != null) r.enabled = true;
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

        private static void HideForCapture(TeleportWorld portal, List<Renderer> hidden)
        {
            var lp = Player.m_localPlayer;
            if (lp != null)
                foreach (var r in lp.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
            if (portal != null && portal.m_target_found != null)
                foreach (var r in portal.m_target_found.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; hidden.Add(r); }
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
