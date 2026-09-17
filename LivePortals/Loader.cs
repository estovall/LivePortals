using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// Loads a stored capture without stalling the game. A worker thread reads the files, decodes the PNGs, builds
    /// their mip chains and computes the relief geometry; the main thread then turns those into textures and
    /// meshes a few per frame, within a time budget all windows share. (0.9.2 and before did all of it in one
    /// frame: up to forty PNG decodes and a hundred meshes, a freeze of most of a second on the first approach.)
    /// </summary>
    internal class CaptureLoader
    {
        /// <summary>Main-thread milliseconds per frame spent turning loaded data into textures and meshes, all windows together.</summary>
        internal const float FrameBudgetMs = 4f;

        public bool Done { get; private set; }
        /// <summary>The captures asked for, or null when there were none or the files were unreadable. The caller owns it.</summary>
        public CaptureSet Result { get; private set; }
        public readonly int FromPoint;

        private readonly string _dir, _key;
        private readonly bool _opaque;
        private readonly int _maxPoints;
        private volatile bool _threadDone, _cancelled;
        private string _error;
        private CaptureSet _set;
        private GrassSet _grass;
        private readonly List<Action> _items = new List<Action>();
        private int _next;
        private static int _budgetFrame = -1;
        private static double _spentMs;

        private CaptureLoader(string dir, string key, bool opaqueAlpha, int fromPoint, int maxPoints)
        {
            _dir = dir; _key = key; _opaque = opaqueAlpha; FromPoint = fromPoint; _maxPoints = maxPoints;
        }

        /// <summary>Load viewpoints fromPoint up to (not including) maxPoints of this portal's capture. Grass and the set's meta come with point 0 only.</summary>
        internal static CaptureLoader Start(ZDOID id, bool opaqueAlpha, int fromPoint, int maxPoints)
        {
            var l = new CaptureLoader(Storage.Dir(), Storage.Key(id), opaqueAlpha, fromPoint, maxPoints);
            if (!File.Exists(Storage.MetaPath(l._dir, l._key))) { l._threadDone = true; l.Finish(null); return l; }
            ThreadPool.QueueUserWorkItem(_ => l.Work());
            return l;
        }

        /// <summary>Drop the load. Main thread only: whatever the main thread already created is destroyed here; the worker's plain arrays are left to the collector.</summary>
        internal void Cancel()
        {
            if (Done) { Result?.Destroy(); Result = null; return; }
            _cancelled = true;
            if (_threadDone) { _set?.Destroy(); _set = null; }
            Done = true;
        }

        // ---- worker thread ----
        private void Work()
        {
            try
            {
                var meta = Storage.ReadMeta(_dir, _key);
                if (meta == null || !meta.TryGetValue("format", out var fmt) || fmt != Storage.Format) return; // older layout: replaced by the next trip
                if (!meta.TryGetValue("points", out var ps) || !int.TryParse(ps, NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) || points < 1) return;
                var set = new CaptureSet { AvailablePoints = points };
                if (meta.TryGetValue("takenAt", out var ta)) long.TryParse(ta, NumberStyles.Integer, CultureInfo.InvariantCulture, out set.TakenAt);
                int to = Mathf.Min(points, Mathf.Max(FromPoint + 1, _maxPoints));
                if (FromPoint == 0) _grass = GrassSet.Read(Storage.GrassPath(_dir, _key));
                for (int p = FromPoint; p < to && !_cancelled; p++)
                {
                    var cap = new PortalCapture { TakenAt = set.TakenAt };
                    if (!Storage.ReadGrids(Storage.GridPath(_dir, _key, p), cap)) { _error = "grid file of point " + p + " missing or unreadable"; return; }
                    string pre = "p" + p + ".";
                    Vector3 offset = Vector3.zero;
                    if (meta.TryGetValue(pre + "sun", out var s)) cap.Sun = Storage.P(s);
                    if (meta.TryGetValue(pre + "ambient", out var a)) cap.Ambient = Storage.P(a);
                    if (meta.TryGetValue(pre + "fog", out var f)) cap.Fog = Storage.P(f);
                    if (meta.TryGetValue(pre + "dayFraction", out var df)) float.TryParse(df, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.DayFraction);
                    if (meta.TryGetValue(pre + "avgLum", out var al)) float.TryParse(al, NumberStyles.Float, CultureInfo.InvariantCulture, out cap.AverageLuminance);
                    if (meta.TryGetValue(pre + "avgColor", out var ac)) cap.AverageColor = Storage.P(ac);
                    if (meta.TryGetValue(pre + "offset", out var os)) offset = Storage.PV(os);
                    set.Captures.Add(cap);
                    set.Offsets.Add(offset);
                    for (int i = 0; i < 6 && !_cancelled; i++)
                    {
                        var g = cap.Grids[i];
                        if (g == null) continue;
                        int face = i, point = p;
                        if (!QueueTexture(Storage.FacePath(_dir, _key, p, i), t => cap.Faces[face] = t)) { _error = "face " + i + " of point " + p + " missing or unreadable"; return; }
                        // The foreground is drawn from the primary viewpoint only (see PortalWindow.BuildReliefs).
                        if (p == 0) QueueTexture(Storage.FrontPath(_dir, _key, p, i), t => cap.Fronts[face] = t);
                        int n = cap.Grid;
                        var back = ReliefMesh.BackgroundData(g, n);
                        var skirt = ReliefMesh.SkirtData(g, n);
                        var shell = p == 0 ? ReliefMesh.GroundShellData(g, n, cap.DepthRange * 1.01f, true, ReliefMesh.ShellMinDepth) : null;
                        var front = p == 0 && g.FgCell != null ? ReliefMesh.ForegroundData(g, n) : null;
                        _items.Add(() =>
                        {
                            cap.Back[face] = ReliefMesh.Make("LivePortals_Back", back);
                            cap.Skirt[face] = ReliefMesh.Make("LivePortals_Skirt", skirt);
                            if (shell != null) cap.Shell[face] = ReliefMesh.Make("LivePortals_Shell", shell);
                            if (front != null) cap.Front[face] = ReliefMesh.Make("LivePortals_Front", front);
                        });
                    }
                }
                if (_grass != null) _items.Add(() => { _grass.Resolve(); set.Grass = _grass; });
                _set = set;
            }
            catch (Exception e) { _error = e.Message; }
            finally { _threadDone = true; }
        }

        /// <summary>Decode and prepare one PNG on this thread and queue its upload. False when the file is missing or broken.</summary>
        private bool QueueTexture(string path, Action<Texture2D> into)
        {
            if (!File.Exists(path)) return false;
            byte[] file = File.ReadAllBytes(path);
            byte[] pixels = Png.Decode(file, out int w, out int h);
            if (pixels != null)
            {
                if (_opaque) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = pixels[i] >= 100 ? (byte)255 : (byte)0;
                byte[] chain = Mips.Chain(pixels, w, h, out int levels);
                _items.Add(() => into(Upload(chain, w, h, levels)));
                return true;
            }
            // Not a PNG this decoder understands: let the engine decode it (a stall, but a rare one).
            Plugin.Dbg("managed PNG decode failed for " + Path.GetFileName(path) + "; decoding in the engine");
            _items.Add(() =>
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!tex.LoadImage(file, !_opaque)) { UnityEngine.Object.Destroy(tex); return; }
                if (_opaque)
                {
                    var px = tex.GetPixels32();
                    for (int i = 0; i < px.Length; i++) px[i].a = px[i].a >= 100 ? (byte)255 : (byte)0;
                    tex.SetPixels32(px);
                    tex.Apply(true, true);
                }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                into(tex);
            });
            return true;
        }

        private static Texture2D Upload(byte[] chain, int w, int h, int levels)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
                if (tex.mipmapCount != levels) throw new InvalidOperationException("engine expects " + tex.mipmapCount + " mip levels, built " + levels);
                tex.LoadRawTextureData(chain);
                tex.Apply(false, true);
            }
            catch (Exception e)
            {
                Plugin.Dbg("mip chain upload failed (" + e.Message + "); uploading the top level only");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                var top = new byte[w * h * 4];
                Array.Copy(chain, top, top.Length);
                tex.LoadRawTextureData(top);
                tex.Apply(false, true);
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        // ---- main thread ----
        /// <summary>Call once per frame until Done. Spends at most the shared budget.</summary>
        internal void Step()
        {
            if (Done || !_threadDone) return;
            if (_cancelled) { Cancel(); return; }
            if (_set == null)
            {
                if (_error != null) Plugin.Log.LogWarning("LivePortals: could not load capture " + _key + ": " + _error);
                Finish(null);
                return;
            }
            if (Time.frameCount != _budgetFrame) { _budgetFrame = Time.frameCount; _spentMs = 0; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (_spentMs < FrameBudgetMs)
            {
                if (_next >= _items.Count) { Finish(_set); return; }
                _items[_next++]();
                _spentMs += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
            }
        }

        private void Finish(CaptureSet set)
        {
            if (set == null) { _set?.Destroy(); _set = null; }
            Result = set;
            _items.Clear();
            Done = true;
        }
    }

    /// <summary>Box-filtered mip chain of an RGBA32 image, laid out the way Texture2D.LoadRawTextureData expects (level 0 first).</summary>
    internal static class Mips
    {
        internal static byte[] Chain(byte[] top, int w, int h, out int levels)
        {
            levels = 1;
            long total = (long)w * h * 4;
            for (int lw = w, lh = h; lw > 1 || lh > 1; levels++)
            {
                lw = Mathf.Max(1, lw >> 1); lh = Mathf.Max(1, lh >> 1);
                total += (long)lw * lh * 4;
            }
            var chain = new byte[total];
            Array.Copy(top, chain, top.Length);
            int srcOff = 0, sw = w, sh = h, dstOff = top.Length;
            while (sw > 1 || sh > 1)
            {
                int dw = Mathf.Max(1, sw >> 1), dh = Mathf.Max(1, sh >> 1);
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Mathf.Min(sh - 1, y * 2), y1 = Mathf.Min(sh - 1, y * 2 + 1);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Mathf.Min(sw - 1, x * 2), x1 = Mathf.Min(sw - 1, x * 2 + 1);
                        int a = srcOff + (y0 * sw + x0) * 4, b = srcOff + (y0 * sw + x1) * 4, c = srcOff + (y1 * sw + x0) * 4, d = srcOff + (y1 * sw + x1) * 4;
                        int o = dstOff + (y * dw + x) * 4;
                        for (int k = 0; k < 4; k++) chain[o + k] = (byte)((chain[a + k] + chain[b + k] + chain[c + k] + chain[d + k] + 2) >> 2);
                    }
                }
                srcOff = dstOff; dstOff += dw * dh * 4; sw = dw; sh = dh;
            }
            return chain;
        }
    }

    /// <summary>
    /// Reads the PNGs the capture writes (8-bit, not interlaced; grey, grey+alpha, RGB or RGBA) into RGBA32 with the
    /// bottom row first, as a Texture2D holds it. Only so the decoding can happen off the main thread: the engine's
    /// own LoadImage cannot.
    /// </summary>
    internal static class Png
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        internal static byte[] Decode(byte[] file, out int w, out int h)
        {
            w = h = 0;
            try
            {
                if (file == null || file.Length < 8) return null;
                for (int i = 0; i < 8; i++) if (file[i] != Signature[i]) return null;
                int pos = 8, bitDepth = 0, colorType = 0, interlace = 0;
                var idat = new MemoryStream();
                while (pos + 8 <= file.Length)
                {
                    int len = BE(file, pos);
                    uint type = (uint)BE(file, pos + 4);
                    pos += 8;
                    if (len < 0 || pos + len > file.Length) return null;
                    if (type == 0x49484452) // IHDR
                    {
                        w = BE(file, pos); h = BE(file, pos + 4);
                        bitDepth = file[pos + 8]; colorType = file[pos + 9]; interlace = file[pos + 12];
                    }
                    else if (type == 0x49444154) idat.Write(file, pos, len); // IDAT
                    else if (type == 0x49454E44) break; // IEND
                    pos += len + 4; // data + crc
                }
                if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024 || bitDepth != 8 || interlace != 0) return null;
                int ch;
                switch (colorType) { case 0: ch = 1; break; case 2: ch = 3; break; case 4: ch = 2; break; case 6: ch = 4; break; default: return null; }
                int stride = w * ch;
                var raw = new byte[h * (stride + 1)];
                idat.Position = 2; // zlib header; the deflate stream follows
                using (var ds = new DeflateStream(idat, CompressionMode.Decompress))
                {
                    int got = 0;
                    while (got < raw.Length)
                    {
                        int n = ds.Read(raw, got, raw.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < raw.Length) return null;
                }
                // Undo the per-row filters in place; each row then holds plain samples after its filter byte.
                for (int y = 0; y < h; y++)
                {
                    int row = y * (stride + 1), filter = raw[row], cur = row + 1, prev = y > 0 ? row - stride : -1;
                    switch (filter)
                    {
                        case 0: break;
                        case 1: for (int x = ch; x < stride; x++) raw[cur + x] += raw[cur + x - ch]; break;
                        case 2: if (prev >= 0) for (int x = 0; x < stride; x++) raw[cur + x] += raw[prev + x]; break;
                        case 3:
                            for (int x = 0; x < stride; x++)
                            {
                                int a = x >= ch ? raw[cur + x - ch] : 0, b = prev >= 0 ? raw[prev + x] : 0;
                                raw[cur + x] += (byte)((a + b) >> 1);
                            }
                            break;
                        case 4:
                            for (int x = 0; x < stride; x++)
                            {
                                int a = x >= ch ? raw[cur + x - ch] : 0, b = prev >= 0 ? raw[prev + x] : 0, c = x >= ch && prev >= 0 ? raw[prev + x - ch] : 0;
                                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                                raw[cur + x] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                            }
                            break;
                        default: return null;
                    }
                }
                var outp = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                {
                    int src = y * (stride + 1) + 1, dst = (h - 1 - y) * w * 4;
                    switch (ch)
                    {
                        case 4: Array.Copy(raw, src, outp, dst, stride); break;
                        case 3: for (int x = 0; x < w; x++) { outp[dst + x * 4] = raw[src + x * 3]; outp[dst + x * 4 + 1] = raw[src + x * 3 + 1]; outp[dst + x * 4 + 2] = raw[src + x * 3 + 2]; outp[dst + x * 4 + 3] = 255; } break;
                        case 2: for (int x = 0; x < w; x++) { byte v = raw[src + x * 2]; outp[dst + x * 4] = v; outp[dst + x * 4 + 1] = v; outp[dst + x * 4 + 2] = v; outp[dst + x * 4 + 3] = raw[src + x * 2 + 1]; } break;
                        default: for (int x = 0; x < w; x++) { byte v = raw[src + x]; outp[dst + x * 4] = v; outp[dst + x * 4 + 1] = v; outp[dst + x * 4 + 2] = v; outp[dst + x * 4 + 3] = 255; } break;
                    }
                }
                return outp;
            }
            catch (Exception) { return null; }
        }

        private static int BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }
}
