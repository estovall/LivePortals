using System;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// Captures live on disk under BepInEx/config/LivePortals/&lt;world&gt;/: six PNG faces and a small text file
    /// with the lighting they were taken under. Keyed by the portal's network ID, so they survive restarts and
    /// are found again from whichever portal is paired with it.
    /// </summary>
    internal static class Storage
    {
        internal static string Key(ZDOID id) => id.UserID.ToString(CultureInfo.InvariantCulture) + "_" + id.ID.ToString(CultureInfo.InvariantCulture);

        private static string Dir()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
            string dir = Path.Combine(Path.Combine(Paths.ConfigPath, "LivePortals"), world);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string FacePath(string dir, string key, int i) => Path.Combine(dir, key + "_" + i + ".png");
        private static string MetaPath(string dir, string key) => Path.Combine(dir, key + ".txt");

        internal static void Save(ZDOID id, PortalCapture cap)
        {
            string dir = Dir(), key = Key(id);
            for (int i = 0; i < 6; i++) File.WriteAllBytes(FacePath(dir, key, i), cap.Faces[i].EncodeToPNG());
            string meta = string.Join("\n", new[]
            {
                "takenAt=" + cap.TakenAt.ToString(CultureInfo.InvariantCulture),
                "sun=" + C(cap.Sun), "ambient=" + C(cap.Ambient), "fog=" + C(cap.Fog),
                "dayFraction=" + cap.DayFraction.ToString("R", CultureInfo.InvariantCulture),
                "avgLum=" + cap.AverageLuminance.ToString("R", CultureInfo.InvariantCulture),
                "avgColor=" + C(cap.AverageColor),
            });
            File.WriteAllText(MetaPath(dir, key), meta);
        }

        /// <summary>Unix time of the stored capture for this portal, or -1 if there is none.</summary>
        internal static long StoredTime(ZDOID id)
        {
            string dir = Dir(), key = Key(id);
            string mp = MetaPath(dir, key);
            if (!File.Exists(mp)) return -1;
            foreach (var line in File.ReadAllLines(mp))
                if (line.StartsWith("takenAt=") && long.TryParse(line.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) return t;
            return File.GetLastWriteTimeUtc(mp).Ticks;
        }

        internal static PortalCapture Load(ZDOID id)
        {
            string dir = Dir(), key = Key(id);
            if (!File.Exists(MetaPath(dir, key))) return null;
            var cap = new PortalCapture();
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    string fp = FacePath(dir, key, i);
                    if (!File.Exists(fp)) { cap.Destroy(); return null; }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!tex.LoadImage(File.ReadAllBytes(fp), false)) { cap.Destroy(); return null; }
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.filterMode = FilterMode.Bilinear;
                    cap.Faces[i] = tex;
                }
                foreach (var line in File.ReadAllLines(MetaPath(dir, key)))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "takenAt": long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out cap.TakenAt); break;
                        case "sun": cap.Sun = P(v); break;
                        case "ambient": cap.Ambient = P(v); break;
                        case "fog": cap.Fog = P(v); break;
                        case "dayFraction": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.DayFraction); break;
                        case "avgLum": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.AverageLuminance); break;
                        case "avgColor": cap.AverageColor = P(v); break;
                    }
                }
                return cap;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("LivePortals: could not load capture " + key + ": " + e.Message);
                cap.Destroy();
                return null;
            }
        }

        private static string C(Color c) => string.Join(",", new[]
        {
            c.r.ToString("R", CultureInfo.InvariantCulture), c.g.ToString("R", CultureInfo.InvariantCulture),
            c.b.ToString("R", CultureInfo.InvariantCulture), c.a.ToString("R", CultureInfo.InvariantCulture)
        });

        private static Color P(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 3) return Color.white;
            float r = F(parts[0]), g = F(parts[1]), b = F(parts[2]), a = parts.Length > 3 ? F(parts[3]) : 1f;
            return new Color(r, g, b, a);
        }

        private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }
}
