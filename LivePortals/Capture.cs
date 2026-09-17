using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>One capture: six cube faces (alpha 0 where the capture saw sky) plus the lighting it was taken under.</summary>
    internal class PortalCapture
    {
        public Texture2D[] Faces = new Texture2D[6];
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
        /// Each face is rendered twice, cleared to black and to white; pixels that differ were never drawn, i.e. sky,
        /// and get alpha 0. The local player, this portal's swirl effect and every window are hidden meanwhile.
        /// </summary>
        internal static PortalCapture Take(Vector3 pos, Quaternion rot, TeleportWorld portal)
        {
            var gc = GameCamera.instance;
            if (gc == null || gc.m_camera == null) { Plugin.Log.LogWarning("LivePortals: no game camera, cannot capture."); return null; }
            var main = gc.m_camera;
            int res = Plugin.CaptureResolution.Value;

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
            if (gc.m_skyCamera != null) mask &= ~gc.m_skyCamera.cullingMask;
            mask &= ~(1 << Plugin.HiddenLayer);
            cam.cullingMask = mask;

            var rtA = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var rtB = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
            var texA = new Texture2D(res, res, TextureFormat.RGBA32, false);
            var texB = new Texture2D(res, res, TextureFormat.RGBA32, false);

            var hidden = new List<Renderer>();
            HideForCapture(portal, hidden);
            bool fog = RenderSettings.fog;
            var cap = new PortalCapture();
            try
            {
                RenderSettings.fog = fog; // keep the game's fog: it is part of what you would see
                for (int i = 0; i < 6; i++)
                {
                    cam.transform.SetPositionAndRotation(pos, rot * FaceRotations[i]);
                    cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
                    cam.targetTexture = rtA;
                    cam.Render();
                    cam.backgroundColor = new Color(1f, 1f, 1f, 1f);
                    cam.targetTexture = rtB;
                    cam.Render();
                    cam.targetTexture = null;

                    RenderTexture.active = rtA; texA.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
                    RenderTexture.active = rtB; texB.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
                    RenderTexture.active = null;

                    var a = texA.GetPixels32();
                    var b = texB.GetPixels32();
                    float exposure = Plugin.CaptureExposure.Value;
                    long lumSum = 0, rSum = 0, gSum = 0, bSum = 0; int count = 0;
                    for (int p = 0; p < a.Length; p++)
                    {
                        Color32 ca = a[p], cb = b[p];
                        int diff = Mathf.Abs(ca.r - cb.r) + Mathf.Abs(ca.g - cb.g) + Mathf.Abs(ca.b - cb.b);
                        if (diff > 60)
                        {
                            a[p] = new Color32(0, 0, 0, 0); // never drawn: sky
                            continue;
                        }
                        if (exposure != 1f)
                        {
                            ca.r = (byte)Mathf.Clamp(Mathf.RoundToInt(ca.r * exposure), 0, 255);
                            ca.g = (byte)Mathf.Clamp(Mathf.RoundToInt(ca.g * exposure), 0, 255);
                            ca.b = (byte)Mathf.Clamp(Mathf.RoundToInt(ca.b * exposure), 0, 255);
                        }
                        ca.a = 255;
                        a[p] = ca;
                        if (i == 0 && (p & 15) == 0) { lumSum += (ca.r * 54 + ca.g * 183 + ca.b * 19) >> 8; rSum += ca.r; gSum += ca.g; bSum += ca.b; count++; }
                    }
                    var face = new Texture2D(res, res, TextureFormat.RGBA32, true);
                    face.wrapMode = TextureWrapMode.Clamp;
                    face.filterMode = FilterMode.Bilinear;
                    face.SetPixels32(a);
                    face.Apply(true, false);
                    cap.Faces[i] = face;
                    if (i == 0 && count > 0)
                    {
                        cap.AverageLuminance = lumSum / (255f * count);
                        cap.AverageColor = new Color(rSum / (255f * count), gSum / (255f * count), bSum / (255f * count), 1f);
                    }
                }
            }
            finally
            {
                RenderSettings.fog = fog;
                foreach (var r in hidden) if (r != null) r.enabled = true;
                PortalWindow.SetAllVisible(true);
                Object.Destroy(texA); Object.Destroy(texB);
                rtA.Release(); rtB.Release();
                Object.Destroy(rtA); Object.Destroy(rtB);
                Object.Destroy(go);
            }

            Lighting.Sample(out cap.Sun, out cap.Ambient, out cap.Fog, out cap.DayFraction);
            cap.TakenAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return cap;
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
                // Normalise both fog colours to unit luminance and take the ratio: pure hue shift, no brightness.
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
