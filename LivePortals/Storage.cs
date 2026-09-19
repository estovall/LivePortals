using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LivePortals
{
    /// <summary>
    /// Captures live on disk under &lt;game data&gt;/LivePortals/&lt;world&gt;/ (read back by CaptureLoader): per capture point, six background PNGs,
    /// up to six foreground PNGs and one file of depth grids, plus one text file per portal with the lighting and
    /// the point offsets. Keyed by the portal's network ID, so they survive restarts and are found again from
    /// whichever portal is paired with it.
    /// </summary>
    internal static class Storage
    {
        internal static string Key(ZDOID id) => id.UserID.ToString(CultureInfo.InvariantCulture) + "_" + id.ID.ToString(CultureInfo.InvariantCulture);

        /// <summary>Where captures live: a LivePortals folder in the game's own data folder (next to worlds and characters), or wherever CaptureFolder points.</summary>
        internal static string BaseDir { get; private set; }

        /// <summary>
        /// Main thread, at start-up. Up to 0.9.5 captures sat in BepInEx/config/LivePortals, which made a mod-manager
        /// profile hundreds of megabytes to share; they are moved out of there once.
        /// </summary>
        internal static void Configure(string configured, string gameDataPath, string oldConfigPath)
        {
            BaseDir = !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : Path.Combine(gameDataPath, "LivePortals");
            try
            {
                string old = Path.Combine(oldConfigPath, "LivePortals");
                if (!Directory.Exists(old) || Path.GetFullPath(old).TrimEnd('\\', '/').Equals(Path.GetFullPath(BaseDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(BaseDir);
                int moved = 0;
                foreach (var sub in Directory.GetDirectories(old))
                {
                    string dst = Path.Combine(BaseDir, Path.GetFileName(sub));
                    if (!Directory.Exists(dst)) { Directory.Move(sub, dst); moved++; continue; }
                    foreach (var f in Directory.GetFiles(sub))
                    {
                        string to = Path.Combine(dst, Path.GetFileName(f));
                        if (File.Exists(to)) File.Delete(f); else File.Move(f, to);
                        moved++;
                    }
                    if (Directory.GetFileSystemEntries(sub).Length == 0) Directory.Delete(sub);
                }
                foreach (var f in Directory.GetFiles(old)) File.Delete(f);
                if (Directory.GetFileSystemEntries(old).Length == 0) Directory.Delete(old);
                Plugin.Log.LogInfo($"LivePortals: moved the captures out of the config folder to {BaseDir} ({moved} folders or files), so a shared profile no longer carries them.");
            }
            catch (Exception e) { Plugin.Log.LogWarning("LivePortals: could not move old captures out of the config folder: " + e.Message); }
        }

        internal static string Dir()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) world = world.Replace(c, '_');
            string dir = Path.Combine(BaseDir ?? Path.Combine(Paths.ConfigPath, "LivePortals"), world);
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string DebugDir()
        {
            string dir = Path.Combine(BaseDir ?? Path.Combine(Paths.ConfigPath, "LivePortals"), "debug");
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string FacePath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_" + i + ".png");
        internal static string FrontPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_f" + i + ".png");
        internal static string LocalPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_l" + i + ".png");
        internal static string AmbientPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_a" + i + ".png");
        internal static string AmbientFrontPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_af" + i + ".png");
        internal static string LocalFrontPath(string dir, string key, int p, int i) => Path.Combine(dir, key + "_p" + p + "_lf" + i + ".png");
        internal static string GridPath(string dir, string key, int p) => Path.Combine(dir, key + "_p" + p + ".bin");
        internal static string MetaPath(string dir, string key) => Path.Combine(dir, key + ".txt");
        internal static string GrassPath(string dir, string key) => Path.Combine(dir, key + "_grass.bin");

        internal static string FirePath(string dir, string key) => Path.Combine(dir, key + "_fire.bin");

        internal static void SaveFire(Job job, FireSet fire)
        {
            string path = FirePath(job.Dir, job.Key);
            if (fire != null && fire.Items.Count > 0) fire.Save(path);
            else if (File.Exists(path)) File.Delete(path);
        }

        internal static void SaveGrass(Job job, GrassSet grass)
        {
            string path = GrassPath(job.Dir, job.Key);
            if (grass != null && grass.Groups.Count > 0) grass.Save(path);
            else if (File.Exists(path)) File.Delete(path);
        }

        private const int GridMagic = 0x4C503038; // "LP08": adds the face tangent and a presence byte per face
        private const int GridMagic07 = 0x4C503037;
        internal const string Format = "2";

        /// <summary>A save in progress. Paths are resolved on the main thread; the writing can happen on any thread.</summary>
        internal class Job
        {
            public string Dir, Key;
            public readonly List<string> Lines = new List<string>();
        }

        internal static Job Begin(ZDOID id)
        {
            return new Job { Dir = Dir(), Key = Key(id) };
        }

        /// <summary>Remove this portal's stored capture. The meta file, which is what makes a set visible, goes first and is written back last.</summary>
        internal static void Clear(Job job)
        {
            string mp = MetaPath(job.Dir, job.Key);
            if (File.Exists(mp)) File.Delete(mp);
            foreach (var f in Directory.GetFiles(job.Dir, job.Key + "_p*")) File.Delete(f);
        }

        /// <summary>Encodes from plain arrays (EncodeArrayToPNG is thread safe).</summary>
        internal static void SaveFace(Job job, int p, int i, FaceLayers face, int res)
        {
            File.WriteAllBytes(FacePath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.Back, GraphicsFormat.R8G8B8A8_SRGB, (uint)res, (uint)res));
            if (face.Front != null)
                File.WriteAllBytes(FrontPath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.Front, GraphicsFormat.R8G8B8A8_SRGB, (uint)res, (uint)res));
            if (face.Local != null && p == 0)
                File.WriteAllBytes(LocalPath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.Local, GraphicsFormat.R8G8B8A8_SRGB, (uint)face.LocalRes, (uint)face.LocalRes));
            if (face.LocalFront != null && p == 0)
                File.WriteAllBytes(LocalFrontPath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.LocalFront, GraphicsFormat.R8G8B8A8_SRGB, (uint)face.LocalRes, (uint)face.LocalRes));
            if (face.Ambient != null && p == 0)
                File.WriteAllBytes(AmbientPath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.Ambient, GraphicsFormat.R8G8B8A8_SRGB, (uint)res, (uint)res));
            if (face.AmbientFront != null && p == 0)
                File.WriteAllBytes(AmbientFrontPath(job.Dir, job.Key, p, i), ImageConversion.EncodeArrayToPNG(face.AmbientFront, GraphicsFormat.R8G8B8A8_SRGB, (uint)res, (uint)res));
        }

        internal static void SavePoint(Job job, int p, RawPoint pt, FaceGrids[] grids)
        {
            int n = pt.Res / pt.Step + 1;
            using (var w = new BinaryWriter(File.Create(GridPath(job.Dir, job.Key, p))))
            {
                w.Write(GridMagic);
                w.Write(n);
                w.Write(pt.DepthRange);
                w.Write(pt.FaceTan);
                for (int i = 0; i < 6; i++)
                {
                    w.Write((byte)(grids[i] != null ? 1 : 0));
                    if (grids[i] == null) continue;
                    WriteDepths(w, grids[i].BgNode, pt.DepthRange);
                    WriteDepths(w, grids[i].BgCell, pt.DepthRange);
                    WriteDepths(w, grids[i].FgNode, pt.DepthRange);
                    WriteDepths(w, grids[i].FgCell, pt.DepthRange);
                }
            }
            string pre = "p" + p + ".";
            job.Lines.Add(pre + "offset=" + V(pt.Offset));
            job.Lines.Add(pre + "sun=" + C(pt.Sun));
            job.Lines.Add(pre + "ambient=" + C(pt.Ambient));
            job.Lines.Add(pre + "fog=" + C(pt.Fog));
            job.Lines.Add(pre + "dayFraction=" + pt.DayFraction.ToString("R", CultureInfo.InvariantCulture));
            job.Lines.Add(pre + "avgLum=" + pt.AverageLuminance.ToString("R", CultureInfo.InvariantCulture));
            job.Lines.Add(pre + "avgLocalLum=" + pt.AverageLocalLuminance.ToString("R", CultureInfo.InvariantCulture));
            job.Lines.Add(pre + "avgAmbientLum=" + pt.AverageAmbientLuminance.ToString("R", CultureInfo.InvariantCulture));
            job.Lines.Add(pre + "avgColor=" + C(pt.AverageColor));
            job.Lines.Add(pre + "grassGain=" + C(pt.GrassGain));
            job.Lines.Add(pre + "ringHeight=" + pt.RingHeight.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static void Finish(Job job, int points, long takenAt)
        {
            var lines = new List<string>
            {
                "format=" + Format,
                "takenAt=" + takenAt.ToString(CultureInfo.InvariantCulture),
                "points=" + points.ToString(CultureInfo.InvariantCulture),
            };
            lines.AddRange(job.Lines);
            File.WriteAllText(MetaPath(job.Dir, job.Key), string.Join("\n", lines));
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

        /// <summary>Time of day (0..1) the stored capture's primary viewpoint was taken at, or -1 without one.</summary>
        internal static float StoredDayFraction(ZDOID id)
        {
            string mp = MetaPath(Dir(), Key(id));
            if (!File.Exists(mp)) return -1f;
            foreach (var line in File.ReadAllLines(mp))
                if (line.StartsWith("p0.dayFraction=") && float.TryParse(line.Substring(15), NumberStyles.Float, CultureInfo.InvariantCulture, out float d)) return d;
            return -1f;
        }

        /// <summary>The portal's meta file as key/value pairs, or null when there is none. Any thread.</summary>
        internal static Dictionary<string, string> ReadMeta(string dir, string key)
        {
            string mp = MetaPath(dir, key);
            if (!File.Exists(mp)) return null;
            var meta = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(mp))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) meta[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return meta;
        }

        /// <summary>Any thread.</summary>
        internal static bool ReadGrids(string path, PortalCapture cap)
        {
            if (!File.Exists(path)) return false;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                int magic = r.ReadInt32();
                if (magic != GridMagic && magic != GridMagic07) return false;
                int n = r.ReadInt32();
                float range = r.ReadSingle();
                float tan = magic == GridMagic ? r.ReadSingle() : 1f;
                if (n < 2 || n > 2049) return false;
                cap.Grid = n;
                cap.DepthRange = range;
                int cells = (n - 1) * (n - 1);
                for (int i = 0; i < 6; i++)
                {
                    if (magic == GridMagic && r.ReadByte() == 0) continue;
                    cap.Grids[i] = new FaceGrids
                    {
                        Tan = tan,
                        BgNode = ReadDepths(r, n * n, range),
                        BgCell = ReadDepths(r, cells, range),
                        FgNode = ReadDepths(r, n * n, range),
                        FgCell = ReadDepths(r, cells, range),
                    };
                }
            }
            return true;
        }

        /// <summary>Depths as 16-bit fractions of the range; 0 is kept for "none".</summary>
        private static void WriteDepths(BinaryWriter w, float[] d, float range)
        {
            var bytes = new byte[d.Length * 2];
            for (int i = 0; i < d.Length; i++)
            {
                int v = d[i] <= 0f ? 0 : Mathf.Clamp(Mathf.RoundToInt(d[i] / range * 65535f), 1, 65535);
                bytes[i * 2] = (byte)(v >> 8);
                bytes[i * 2 + 1] = (byte)(v & 255);
            }
            w.Write(bytes);
        }

        private static float[] ReadDepths(BinaryReader r, int count, float range)
        {
            var bytes = r.ReadBytes(count * 2);
            if (bytes.Length != count * 2) throw new EndOfStreamException("grid file is short");
            var d = new float[count];
            for (int i = 0; i < count; i++) d[i] = ((bytes[i * 2] << 8) | bytes[i * 2 + 1]) / 65535f * range;
            return d;
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

        internal static Color P(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 3) return Color.white;
            float r = F(parts[0]), g = F(parts[1]), b = F(parts[2]), a = parts.Length > 3 ? F(parts[3]) : 1f;
            return new Color(r, g, b, a);
        }

        internal static Vector3 PV(string s)
        {
            var parts = s.Split(',');
            if (parts.Length < 3) return Vector3.zero;
            return new Vector3(F(parts[0]), F(parts[1]), F(parts[2]));
        }

        private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
    }
}
