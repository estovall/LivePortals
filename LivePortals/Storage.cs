using System;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// Captures live on disk under BepInEx/config/LivePortals/&lt;world&gt;/: per capture point, six PNG faces, six
    /// depth maps and six backdrops, plus one text file per portal with the lighting and the point offsets.
    /// Keyed by the portal's network ID, so they survive restarts and are found again from whichever portal is
    /// paired with it.
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

        private static string FacePath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_" + i + ".png");
        private static string DepthPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_d" + i + ".png");
        private static string BackPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_b" + i + ".png");
        private static string MetaPath(string dir, string key) => Path.Combine(dir, key + ".txt");

        internal static void Save(ZDOID id, CaptureSet set)
        {
            string dir = Dir(), key = Key(id);
            // Remove any older files for this portal so a smaller set does not leave stale points behind.
            foreach (var f in Directory.GetFiles(dir, key + "_*.png")) File.Delete(f);
            var lines = new System.Collections.Generic.List<string>
            {
                "takenAt=" + set.TakenAt.ToString(CultureInfo.InvariantCulture),
                "points=" + set.Captures.Count.ToString(CultureInfo.InvariantCulture),
            };
            for (int p = 0; p < set.Captures.Count; p++)
            {
                var cap = set.Captures[p];
                for (int i = 0; i < 6; i++)
                {
                    File.WriteAllBytes(FacePath(dir, key, p, i), cap.Faces[i].EncodeToPNG());
                    if (cap.Depth[i] != null) File.WriteAllBytes(DepthPath(dir, key, p, i), EncodeDepth(cap.Depth[i], cap.DepthSize, cap.DepthRange));
                    if (cap.Backdrops[i] != null) File.WriteAllBytes(BackPath(dir, key, p, i), cap.Backdrops[i].EncodeToPNG());
                }
                Vector3 o = set.Offsets[p];
                lines.Add($"p{p}.offset=" + V(o));
                lines.Add($"p{p}.depthSize=" + cap.DepthSize.ToString(CultureInfo.InvariantCulture));
                lines.Add($"p{p}.depthRange=" + cap.DepthRange.ToString("R", CultureInfo.InvariantCulture));
                lines.Add($"p{p}.sun=" + C(cap.Sun));
                lines.Add($"p{p}.ambient=" + C(cap.Ambient));
                lines.Add($"p{p}.fog=" + C(cap.Fog));
                lines.Add($"p{p}.dayFraction=" + cap.DayFraction.ToString("R", CultureInfo.InvariantCulture));
                lines.Add($"p{p}.avgLum=" + cap.AverageLuminance.ToString("R", CultureInfo.InvariantCulture));
                lines.Add($"p{p}.avgColor=" + C(cap.AverageColor));
            }
            File.WriteAllText(MetaPath(dir, key), string.Join("\n", lines));
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

        internal static CaptureSet Load(ZDOID id)
        {
            string dir = Dir(), key = Key(id);
            string mp = MetaPath(dir, key);
            if (!File.Exists(mp)) return null;
            var set = new CaptureSet();
            try
            {
                var meta = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(mp))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) meta[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
                if (!meta.TryGetValue("points", out var ps) || !int.TryParse(ps, NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) || points < 1)
                    return null; // pre-0.6 layout: not readable, will be replaced by the next trip
                if (meta.TryGetValue("takenAt", out var ta)) long.TryParse(ta, NumberStyles.Integer, CultureInfo.InvariantCulture, out set.TakenAt);
                for (int p = 0; p < points; p++)
                {
                    var cap = new PortalCapture { TakenAt = set.TakenAt };
                    for (int i = 0; i < 6; i++)
                    {
                        string fp = FacePath(dir, key, p, i);
                        if (!File.Exists(fp)) { cap.Destroy(); set.Destroy(); return null; }
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                        if (!tex.LoadImage(File.ReadAllBytes(fp), false)) { cap.Destroy(); set.Destroy(); return null; }
                        tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;
                        cap.Faces[i] = tex;
                        string bp = BackPath(dir, key, p, i);
                        if (File.Exists(bp))
                        {
                            var bt = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                            if (bt.LoadImage(File.ReadAllBytes(bp), false)) { bt.wrapMode = TextureWrapMode.Clamp; bt.filterMode = FilterMode.Bilinear; cap.Backdrops[i] = bt; }
                            else UnityEngine.Object.Destroy(bt);
                        }
                    }
                    string pre = "p" + p + ".";
                    if (meta.TryGetValue(pre + "depthSize", out var ds)) int.TryParse(ds, NumberStyles.Integer, CultureInfo.InvariantCulture, out cap.DepthSize);
                    if (meta.TryGetValue(pre + "depthRange", out var dr)) float.TryParse(dr, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.DepthRange);
                    if (meta.TryGetValue(pre + "sun", out var s)) cap.Sun = P(s);
                    if (meta.TryGetValue(pre + "ambient", out var a)) cap.Ambient = P(a);
                    if (meta.TryGetValue(pre + "fog", out var f)) cap.Fog = P(f);
                    if (meta.TryGetValue(pre + "dayFraction", out var df)) float.TryParse(df, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.DayFraction);
                    if (meta.TryGetValue(pre + "avgLum", out var al)) float.TryParse(al, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.AverageLuminance);
                    if (meta.TryGetValue(pre + "avgColor", out var ac)) cap.AverageColor = P(ac);
                    if (cap.DepthSize >= 2)
                        for (int i = 0; i < 6; i++)
                        {
                            string dp = DepthPath(dir, key, p, i);
                            cap.Depth[i] = File.Exists(dp) ? DecodeDepth(File.ReadAllBytes(dp), cap.DepthSize, cap.DepthRange) : null;
                        }
                    Vector3 off = Vector3.zero;
                    if (meta.TryGetValue(pre + "offset", out var os)) off = PV(os);
                    set.Captures.Add(cap);
                    set.Offsets.Add(off);
                }
                return set;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("LivePortals: could not load capture " + key + ": " + e.Message);
                set.Destroy();
                return null;
            }
        }

        /// <summary>Depth as a PNG: 16 bits per node in the red (high) and green (low) channels.</summary>
        private static byte[] EncodeDepth(float[] depth, int n, float range)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                int v = Mathf.Clamp(Mathf.RoundToInt(depth[i] / range * 65535f), 0, 65535);
                px[i] = new Color32((byte)(v >> 8), (byte)(v & 255), 0, 255);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.Destroy(tex);
            return png;
        }

        private static float[] DecodeDepth(byte[] png, int n, float range)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(png, false) || tex.width != n || tex.height != n) { UnityEngine.Object.Destroy(tex); return null; }
            var px = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);
            var depth = new float[n * n];
            for (int i = 0; i < depth.Length; i++) depth[i] = ((px[i].r << 8) | px[i].g) / 65535f * range;
            return depth;
        }

        private static string C(Color c) => string.Join(",", new[]
        {
            c.r.ToString("R", CultureInfo.InvariantCulture), c.g.ToString("R", CultureInfo.InvariantCulture),
            c.b.ToString("R", CultureInfo.InvariantCulture), c.a.ToString("R", CultureInfo.InvariantCulture)
        });

        private static string V(Vector3 v) => string.Join(",", new[]
        {
            v.x.ToString("R", CultureInfo.InvariantCulture), v.y.ToString("R", CultureInfo.InvariantCulture), v.z.ToString("R", CultureInfo.InvariantCulture)
        });

        private static Color P(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 3) return Color.white;
            float r = F(parts[0]), g = F(parts[1]), b = F(parts[2]), a = parts.Length > 3 ? F(parts[3]) : 1f;
            return new Color(r, g, b, a);
        }

        private static Vector3 PV(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 3) return Vector3.zero;
            return new Vector3(F(parts[0]), F(parts[1]), F(parts[2]));
        }

        private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }
}
