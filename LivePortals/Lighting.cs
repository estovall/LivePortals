using UnityEngine;

namespace LivePortals
{
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
        /// Brightness follows the light level ratio; hue follows the colour of that light, which tracks dawn/dusk/night.
        /// </summary>
        internal static Color Tint(PortalCapture cap, float strength, bool hasGlow = true)
        {
            Sample(out var sun, out var amb, out _, out _);
            float ratio = Level(sun, amb) / Level(cap.Sun, cap.Ambient);
            ratio = Mathf.Clamp(ratio, 0.04f, 4f);
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
