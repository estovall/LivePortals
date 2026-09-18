using UnityEngine;

namespace LivePortals
{
    /// <summary>sRGB bytes to linear light and back, for worker threads.</summary>
    internal static class Srgb
    {
        internal static readonly float[] Lin = MakeLin();
        private static readonly byte[] Enc = MakeEnc();

        private static float[] MakeLin()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++) { float c = i / 255f; t[i] = c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f); }
            return t;
        }

        private static byte[] MakeEnc()
        {
            var t = new byte[4096];
            for (int i = 0; i < t.Length; i++)
            {
                float l = i / (float)(t.Length - 1);
                float c = l <= 0.0031308f ? l * 12.92f : 1.055f * Mathf.Pow(l, 1f / 2.4f) - 0.055f;
                t[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(c * 255f), 0, 255);
            }
            return t;
        }

        internal static byte Encode(float linear) => Enc[Mathf.Clamp((int)(linear * (Enc.Length - 1) + 0.5f), 0, Enc.Length - 1)];
    }

    internal static class Lighting
    {
        /// <summary>
        /// For a capture that holds its sky-lit part as a layer of its own: the tint of the picture itself, which
        /// now follows the sun alone, and the strength of the sky-light layer on top of it, as a multiplier of light
        /// per channel. Together: picture * sun + skyPart * (ambient - sun), the torchlight layer as before. The sun's
        /// light falls to a tenth at night and the sky's to a third; one number for both (Tint) left everything
        /// the sky lights, which at night is everything, three times too dark.
        /// </summary>
        /// <summary>The most any tint may brighten a picture. More reveals the 8-bit steps of a dark capture as blocks.</summary>
        internal const float MaxBrighten = 1.5f;

        internal static void SplitTint(PortalCapture cap, float strength, out Color sunTint, out Color skyGain)
        {
            Sample(out var sun, out var amb, out _, out _);
            // Brightening is capped low: a picture taken in near darkness (sun 0.002 on a moonless night, seen later
            // under a moon at 0.4) was scaled to the old cap of 4 and came out blown out and blocky, the 8-bit
            // picture having nothing to brighten. Dimming stays free. The 0.05 floor keeps two tiny values from
            // making a large ratio.
            float ratio = Mathf.Clamp((Luminance(sun) + 0.05f) / (Luminance(cap.Sun) + 0.05f), 0.04f, MaxBrighten);
            Color chroma = Color.white;
            float thenLum = Luminance(cap.Sun), nowLum = Luminance(sun);
            if (thenLum > 0.01f && nowLum > 0.01f)
            {
                Color a = cap.Sun / thenLum, b = sun / nowLum;
                chroma = new Color(Mathf.Clamp(b.r / Mathf.Max(0.05f, a.r), 0.7f, 1.5f),
                                   Mathf.Clamp(b.g / Mathf.Max(0.05f, a.g), 0.7f, 1.5f),
                                   Mathf.Clamp(b.b / Mathf.Max(0.05f, a.b), 0.7f, 1.5f), 1f);
                chroma = Color.Lerp(Color.white, chroma, 0.25f);
            }
            sunTint = Color.Lerp(Color.white, chroma * ratio, strength);
            sunTint.a = 1f;
            // Light colours are given in display values; the light itself goes with their 2.2th power.
            skyGain = new Color(SkyChannel(amb.r, cap.Ambient.r, sunTint.r, strength), SkyChannel(amb.g, cap.Ambient.g, sunTint.g, strength), SkyChannel(amb.b, cap.Ambient.b, sunTint.b, strength), 1f);
        }

        private static float SkyChannel(float now, float then, float sunTint, float strength)
        {
            float ra = Mathf.Lerp(1f, Mathf.Clamp((now + 0.05f) / (then + 0.05f), 0.04f, MaxBrighten), strength);
            return Mathf.Clamp(Mathf.Pow(ra, 2.2f) - Mathf.Pow(sunTint, 2.2f), 0f, 4f);
        }

        /// <summary>
        /// One number for the parts of a split capture that have no sky-light layer over them (filled-in ground, the
        /// other viewpoints, skirts, the far shell): the picture's own shares of sun, sky and torch light, each
        /// followed to now.
        /// </summary>
        internal static Color MixedTint(PortalCapture cap, Color sunTint, Color skyGain)
        {
            float total = Mathf.Max(0.001f, cap.AverageLuminance);
            float sky = Mathf.Clamp(cap.AverageAmbientLuminance, 0f, total), local = Mathf.Clamp(cap.AverageLocalLuminance, 0f, total - sky);
            float sunShare = (total - sky - local) / total, skyShare = sky / total, localShare = local / total;
            Color c = new Color(Mix(sunTint.r, skyGain.r, sunShare, skyShare, localShare), Mix(sunTint.g, skyGain.g, sunShare, skyShare, localShare), Mix(sunTint.b, skyGain.b, sunShare, skyShare, localShare), 1f);
            return c;
        }

        private static float Mix(float sunTint, float skyGain, float sunShare, float skyShare, float localShare)
        {
            float s = Mathf.Pow(sunTint, 2.2f);
            float light = sunShare * s + skyShare * (s + skyGain) + localShare;
            return Mathf.Clamp(Mathf.Pow(light, 1f / 2.2f), 0.04f, MaxBrighten);
        }

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
        /// Brightness follows the light level ratio; hue follows the colour of that light, which tracks dawn/dusk/night.
        /// </summary>
        internal static Color Tint(PortalCapture cap, float strength, bool hasGlow = true)
        {
            Sample(out var sun, out var amb, out _, out _);
            float ratio = Level(sun, amb) / Level(cap.Sun, cap.Ambient);
            ratio = Mathf.Clamp(ratio, 0.04f, MaxBrighten);
            // Without the local-light layer (no additive shader passed the self-test, or a capture from before
            // 0.9.8) nothing puts the torchlight back after the darkening, and a torch-lit room seen at night is
            // as black as the hillside. Then hold back the darkening by the share of the picture's light that
            // was torchlight: one number for the whole picture, but a lit room stays lit.
            if (!hasGlow && ratio < 1f && cap.AverageLuminance > 0.001f)
                ratio = Mathf.Lerp(ratio, 1f, Mathf.Clamp01(cap.AverageLocalLuminance / cap.AverageLuminance));
            // Hue from the light that falls on the scene (sun and ambient), then and now. Not from the fog colour:
            // that belongs to the biome the viewer stands in, and turned a forest seen from the plains orange.
            Color chroma = Color.white;
            Color then = cap.Sun * 0.65f + cap.Ambient * 0.35f, now = sun * 0.65f + amb * 0.35f;
            float thenLum = Luminance(then), nowLum = Luminance(now);
            if (thenLum > 0.01f && nowLum > 0.01f)
            {
                Color a = then / thenLum, b = now / nowLum;
                // Gently: one multiplier for the whole picture cannot tell sunlit from shaded, and Valheim's evening
                // sun is a deep orange. At half strength (0.8.2) a noon capture seen at dusk came out "super orange"
                // (red 1.7x, blue 0.6x); the world around the viewer shifts far less, its shadows stay blue.
                chroma = new Color(Mathf.Clamp(b.r / Mathf.Max(0.05f, a.r), 0.7f, 1.5f),
                                   Mathf.Clamp(b.g / Mathf.Max(0.05f, a.g), 0.7f, 1.5f),
                                   Mathf.Clamp(b.b / Mathf.Max(0.05f, a.b), 0.7f, 1.5f), 1f);
                chroma = Color.Lerp(Color.white, chroma, 0.25f);
            }
            Color tint = chroma * ratio;
            tint.a = 1f;
            return Color.Lerp(Color.white, tint, strength);
        }
    }
}
