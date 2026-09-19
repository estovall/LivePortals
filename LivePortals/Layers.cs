using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>The two layers of one captured face, as plain arrays (safe to build and encode off the main thread).</summary>
    internal class FaceLayers
    {
        public Color32[] Back;   // background colour: alpha 255 as captured, FilledAlpha where filled in under near things, 0 on sky
        public Color32[] Front;  // foreground colour: alpha 255 only on near things; null if the face has none
        /// <summary>Light that does not come from the sun or the sky (torches, fires, glowing things, flames), on the background only, at LocalRes per edge; null when not captured.</summary>
        public Color32[] Local;
        /// <summary>The same for the foreground pixels (flames, mostly); null when the face has no foreground.</summary>
        public Color32[] LocalFront;
        public int LocalRes;
        /// <summary>
        /// Sky light alone, background and foreground, at the picture's own resolution; null when not captured. Torchlight
        /// is smooth and gets by on half; this is the picture itself in another light, and at half resolution its
        /// soft edges against the picture's sharp ones drew a pale outline round every branch (0.9.22, at dusk).
        /// </summary>
        public Color32[] Ambient, AmbientFront;

        /// <summary>The arrays back to the pool (see CaptureRun.DropLayers). Nothing may read them after this.</summary>
        internal void Release()
        {
            Pool<Color32>.Return(Back); Pool<Color32>.Return(Front); Pool<Color32>.Return(Local); Pool<Color32>.Return(LocalFront);
            Pool<Color32>.Return(Ambient); Pool<Color32>.Return(AmbientFront);
            Back = Front = Local = LocalFront = Ambient = AmbientFront = null;
        }
        public FaceGrids Grids;
    }

    /// <summary>
    /// Splits a captured face into a foreground and a background layer using the per-pixel depth.
    ///
    /// Foreground is (1) anything clearly nearer than the scene behind it and narrow enough to see around (a
    /// creature, a trunk, a post: found by a morphological closing of the depth image), and (2) the near side of
    /// every remaining depth edge, one grid cell deep (the rim of a boulder or wall against what is behind it).
    /// Each pixel belongs to exactly one layer, so the silhouette is cut by the texture's alpha at pixel
    /// resolution, never by the mesh. The background layer continues underneath the foreground with colour and
    /// depth filled in from its surroundings, which is what shows when you look around a near object.
    ///
    /// Grids (n nodes per edge, a cell is step x step pixels, nodes sit on pixel corners):
    ///   BgNode  depth of the background surface at each node.
    ///   BgCell  per cell, the depth of the background surface inside it: the far one if the cell holds two, else
    ///           the mean of its background pixels (or of what was filled in, if foreground covers it all). A
    ///           corner whose node depth is
    ///           clearly some other surface (the near side of an edge, or fill from beyond it) uses this instead.
    ///   FgNode  depth of the foreground at each node that touches any, else 0.
    ///   FgCell  per cell, 0 if no foreground pixel, else the depth of the nearest foreground in it; corners use
    ///           FgNode when it is that same surface.
    /// </summary>
    internal static class Layers
    {
        private const float EdgeRatio = 1.15f;   // adjacent pixels this far apart in ratio...
        private const float EdgeDisp = 0.005f;   // ...and in 1/depth are two surfaces (5 px of shift per metre of eye movement)
        private const float SameRatio = 1.2f;

        /// <summary>
        /// Alpha of background pixels that were filled in rather than seen. The window draws the background twice
        /// from the one texture: with a cut-off above this (captured pixels only) where it is, and with a low
        /// cut-off (guesses included) pushed back by FilledPush, so that where another capture point really saw
        /// that surface its pixels win, while the guess still hides whatever lies well behind it.
        /// </summary>
        internal const byte FilledAlpha = 160;
        internal const float CutoffAll = 0.4f, CutoffCaptured = 0.82f, FilledPush = 1.04f;

        private static bool IsEdge(float a, float b)
        {
            float lo = a < b ? a : b, hi = a < b ? b : a;
            return hi > lo * EdgeRatio && (1f / lo - 1f / hi) > EdgeDisp;
        }

        internal static FaceLayers Process(RawFace raw, int res, int step, float range)
        {
            int cells = res / step, n = cells + 1;
            float[] D = raw.Depth;
            bool[] sky = raw.Sky;
            Color32[] col = raw.Col;
            var fg = Pool<byte>.Rent(res * res); // 0 background, 1 narrow near object, 2 near side of an edge

            // ---- 1. What is behind narrow near things: closing (max then min) of a coarse depth image ----
            const int q = 4;
            int cw = (res + q - 1) / q;
            var coarse = new float[cw * cw];
            for (int y = 0; y < res; y++)
            {
                int cr = (y / q) * cw;
                for (int x = 0; x < res; x++)
                {
                    float d = D[y * res + x];
                    int c = cr + x / q;
                    if (d > coarse[c]) coarse[c] = d;
                }
            }
            int rad = Mathf.Max(2, Mathf.RoundToInt(cw * 0.085f)); // window of about 15 degrees
            // Padded by the radius (zeros never win a max), so the closing is exact up to the face's border instead
            // of reading everything near the border as "nearer than its surroundings".
            int pw = cw + 2 * rad;
            var padded = new float[pw * pw];
            for (int y = 0; y < cw; y++) Array.Copy(coarse, y * cw, padded, (y + rad) * pw + rad, cw);
            padded = BoxExtreme(BoxExtreme(padded, pw, rad, true), pw, rad, false);
            var closed = new float[cw * cw];
            for (int y = 0; y < cw; y++) Array.Copy(padded, (y + rad) * pw + rad, closed, y * cw, cw);
            bool anyFg = false;
            for (int y = 0; y < res; y++)
            {
                int cr = (y / q) * cw;
                for (int x = 0; x < res; x++)
                {
                    int p = y * res + x;
                    if (sky[p]) continue;
                    float d = D[p], b = closed[cr + x / q];
                    if (d < b * 0.9f && (1f / d - 1f / b) > 0.02f) { fg[p] = 1; anyFg = true; }
                }
            }

            // ---- 2. Remaining depth edges: cells that hold two surfaces ----
            // A cell (with one pixel of margin, so an edge along its border still shows both sides) holds two
            // surfaces if its sorted depths have a gap. Ground running to the horizon spans a wide range inside a
            // cell too, but without a gap. The near side of the gap joins the foreground.
            var bgCell = new float[cells * cells];
            var sorted = new float[(step + 2) * (step + 2)];
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    int x0 = Mathf.Max(0, cx * step - 1), x1 = Mathf.Min(res - 1, (cx + 1) * step);
                    int y0 = Mathf.Max(0, cy * step - 1), y1 = Mathf.Min(res - 1, (cy + 1) * step);
                    int m = 0; float lo = float.MaxValue, hi = 0f;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int p = y * res + x;
                            if (fg[p] == 1) continue;
                            float d = D[p];
                            sorted[m++] = d;
                            if (d < lo) lo = d;
                            if (d > hi) hi = d;
                        }
                    if (m < 2 || !IsEdge(lo, hi)) continue;
                    Array.Sort(sorted, 0, m);
                    float t = 0f, bestGap = 0f, farLo = 0f;
                    for (int k = 0; k + 1 < m; k++)
                    {
                        if (!IsEdge(sorted[k], sorted[k + 1])) continue;
                        float gap = 1f / sorted[k] - 1f / sorted[k + 1];
                        if (gap > bestGap) { bestGap = gap; t = Mathf.Sqrt(sorted[k] * sorted[k + 1]); farLo = sorted[k + 1]; }
                    }
                    if (t <= 0f) continue;
                    for (int y = cy * step; y < (cy + 1) * step; y++)
                        for (int x = cx * step; x < (cx + 1) * step; x++)
                        {
                            int p = y * res + x;
                            if (fg[p] == 0 && D[p] < t) { fg[p] = 2; anyFg = true; }
                        }
                    // The far surface is the nearest one beyond the gap (a third, farther one is drawn with it).
                    double sum = 0; int cnt = 0;
                    for (int k = 0; k < m; k++)
                        if (sorted[k] >= t && sorted[k] <= farLo * SameRatio) { sum += sorted[k]; cnt++; }
                    bgCell[cy * cells + cx] = (float)(sum / cnt);
                }
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    int c = cy * cells + cx;
                    if (bgCell[c] > 0f) continue;
                    double sum = 0; int cnt = 0;
                    for (int y = cy * step; y < (cy + 1) * step; y++)
                        for (int x = cx * step; x < (cx + 1) * step; x++)
                            if (fg[y * res + x] == 0) { sum += D[y * res + x]; cnt++; }
                    if (cnt > 0) bgCell[c] = (float)(sum / cnt);
                }

            // ---- 2b. Flames at their fire's depth, in cells that hold no other near thing ----
            // A flame in front of the back wall (a hearth) becomes a near thing of its own at the fire's distance. A
            // flame in front of a pillar stays painted on the pillar: the cell's depth is one value, and moving it
            // would take the pillar's pixels along.
            if (raw.FlameDepth != null)
            {
                var fd = raw.FlameDepth;
                for (int cy = 0; cy < cells; cy++)
                    for (int cx = 0; cx < cells; cx++)
                    {
                        bool flame = false, other = false;
                        for (int y = cy * step; y < (cy + 1) * step; y++)
                            for (int x = cx * step; x < (cx + 1) * step; x++)
                            {
                                int p = y * res + x;
                                if (fd[p] > 0f) flame = true; else if (fg[p] != 0) other = true;
                            }
                        if (!flame || other) continue;
                        for (int y = cy * step; y < (cy + 1) * step; y++)
                            for (int x = cx * step; x < (cx + 1) * step; x++)
                            {
                                int p = y * res + x;
                                if (fd[p] > 0f) { fg[p] = 1; D[p] = fd[p]; anyFg = true; }
                            }
                    }
            }

            // ---- 3. Node depths from the four pixels around each node ----
            var bgNode = new float[n * n];
            var fgNode = new float[n * n];
            var known = new bool[n * n];
            var shared = new bool[n * n];
            var four = new int[4];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int xa = Mathf.Clamp(i * step - 1, 0, res - 1), xb = Mathf.Clamp(i * step, 0, res - 1);
                    int ya = Mathf.Clamp(j * step - 1, 0, res - 1), yb = Mathf.Clamp(j * step, 0, res - 1);
                    four[0] = ya * res + xa; four[1] = ya * res + xb; four[2] = yb * res + xa; four[3] = yb * res + xb;
                    float fMin = float.MaxValue, bMax = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float d = D[four[k]];
                        if (fg[four[k]] != 0) { if (d < fMin) fMin = d; }
                        else if (d > bMax) bMax = d;
                    }
                    float fSum = 0f, bSum = 0f; int fCnt = 0, bCnt = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float d = D[four[k]];
                        if (fg[four[k]] != 0) { if (d <= fMin * SameRatio) { fSum += d; fCnt++; } }
                        else if (d >= bMax / SameRatio) { bSum += d; bCnt++; }
                    }
                    int o = j * n + i;
                    float fv = fCnt > 0 ? fSum / fCnt : 0f, bv = bCnt > 0 ? bSum / bCnt : 0f;
                    if (fCnt > 0 && bCnt > 0 && Mathf.Max(fv, bv) < Mathf.Min(fv, bv) * SameRatio)
                    {
                        fv = bv = (fSum + bSum) / (fCnt + bCnt); // one surface shared by both layers: no crack between them
                        shared[j * n + i] = true;
                    }
                    fgNode[o] = fv;
                    bgNode[o] = bv;
                    known[o] = bCnt > 0;
                }

            // ---- 4. Foreground cells, and foreground depth at their corners that touch no foreground pixel ----
            var fgCell = new float[cells * cells];
            if (anyFg)
            {
                for (int cy = 0; cy < cells; cy++)
                    for (int cx = 0; cx < cells; cx++)
                        fgCell[cy * cells + cx] = NearFg(D, fg, res, cx * step, cy * step, (cx + 1) * step - 1, (cy + 1) * step - 1);
                // Corner depths: fit a plane through each cell's foreground pixels and read it at the corners. (A
                // plane is linear in 1/depth across the image.) The few pixels next to a node all lie on one side
                // of it when the surface ends there, and a surface seen at a slant then gets a rim of teeth from
                // any other angle. A node takes the mean of what its cells say, nearest surface first.
                var est = new float[n * n * 4];
                var estCount = new byte[n * n];
                for (int cy = 0; cy < cells; cy++)
                    for (int cx = 0; cx < cells; cx++)
                    {
                        float zc = fgCell[cy * cells + cx];
                        if (zc <= 0f) continue;
                        int x0 = Mathf.Max(0, cx * step - 2), x1 = Mathf.Min(res - 1, (cx + 1) * step + 1);
                        int y0 = Mathf.Max(0, cy * step - 2), y1 = Mathf.Min(res - 1, (cy + 1) * step + 1);
                        float mx = (cx + 0.5f) * step, my = (cy + 0.5f) * step, lim = zc * SameRatio, lo = float.MaxValue, hi = 0f;
                        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, s1 = 0, sxw = 0, syw = 0, sw = 0;
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                            {
                                int p = y * res + x;
                                float d = D[p];
                                if (fg[p] == 0 || d > lim || d < zc / SameRatio) continue;
                                double px = x + 0.5 - mx, py = y + 0.5 - my, w = 1.0 / d;
                                sxx += px * px; sxy += px * py; sx += px; syy += py * py; sy += py; s1 += 1;
                                sxw += px * w; syw += py * w; sw += w;
                                if (d < lo) lo = d;
                                if (d > hi) hi = d;
                            }
                        double det = sxx * (syy * s1 - sy * sy) - sxy * (sxy * s1 - sy * sx) + sx * (sxy * sy - syy * sx);
                        bool plane = s1 >= 8 && Math.Abs(det) > 1e-6 * s1 * s1 * s1;
                        double a = 0, b = 0, c = s1 > 0 ? sw / s1 : 1.0 / zc;
                        if (plane)
                        {
                            a = (sxw * (syy * s1 - sy * sy) - sxy * (syw * s1 - sy * sw) + sx * (syw * sy - syy * sw)) / det;
                            b = (sxx * (syw * s1 - sw * sy) - sxw * (sxy * s1 - sy * sx) + sx * (sxy * sw - syw * sx)) / det;
                            c = (sxx * (syy * sw - sy * syw) - sxy * (sxy * sw - syw * sx) + sxw * (sxy * sy - syy * sx)) / det;
                        }
                        for (int k = 0; k < 4; k++)
                        {
                            int i = cx + (k & 1), j = cy + (k >> 1), o = j * n + i;
                            double w = a * (i * step - mx) + b * (j * step - my) + c;
                            float z = w > 1e-6 ? (float)(1.0 / w) : hi;
                            if (lo <= hi) z = Mathf.Clamp(z, lo / 1.25f, hi * 1.25f); else z = zc;
                            est[o * 4 + estCount[o]++] = z;
                        }
                    }
                for (int o = 0; o < n * n; o++)
                {
                    if (estCount[o] == 0 || shared[o]) continue;
                    float near = float.MaxValue;
                    for (int k = 0; k < estCount[o]; k++) if (est[o * 4 + k] < near) near = est[o * 4 + k];
                    float sum = 0f; int cnt = 0;
                    for (int k = 0; k < estCount[o]; k++) if (est[o * 4 + k] <= near * 1.15f) { sum += est[o * 4 + k]; cnt++; }
                    fgNode[o] = sum / cnt;
                }
            }

            // ---- 5. Background depth under the foreground: grow it in from the known nodes around ----
            FillNodes(bgNode, known, fgNode, n, range);

            // ---- 6. Textures ----
            var back = Pool<Color32>.Rent(res * res);
            var state = Pool<byte>.Rent(res * res); // 0 unknown, 1 colour, 2 sky
            var unknown = new List<int>();
            Color32[] front = anyFg ? Pool<Color32>.Rent(res * res) : null;
            for (int p = 0; p < back.Length; p++)
            {
                if (fg[p] != 0) { unknown.Add(p); front[p] = col[p]; continue; }
                back[p] = col[p];
                state[p] = sky[p] ? (byte)2 : (byte)1;
            }
            if (front != null)
            {
                // Keep the real colour in the ring of pixels just outside the foreground, so filtering at the
                // cut-out edge blends toward what was there instead of toward black.
                foreach (int p in unknown)
                {
                    int x = p % res, y = p / res;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= res || yy >= res) continue;
                            int a = yy * res + xx;
                            if (fg[a] == 0 && front[a].r == 0 && front[a].g == 0 && front[a].b == 0) { Color32 c = col[a]; c.a = 0; front[a] = c; }
                        }
                }
            }
            float[] carried = FillPixels(back, state, unknown, col, D, res);

            // Cells that foreground covers completely have no background pixel of their own: their surface is
            // wherever the colours filled in under them came from.
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    int c = cy * cells + cx;
                    if (bgCell[c] > 0f) continue;
                    double sum = 0; int cnt = 0;
                    for (int y = cy * step; y < (cy + 1) * step; y++)
                        for (int x = cx * step; x < (cx + 1) * step; x++)
                            if (back[y * res + x].a != 0) { sum += Math.Min(range, carried[y * res + x]); cnt++; }
                    bgCell[c] = cnt > 0 ? (float)(sum / cnt) : range;
                }

            // ---- 7. Local light, at half resolution (it is smooth), only where the background was really seen ----
            Color32[] local = null, localFront = null; int lres = 0;
            if (raw.Local != null)
            {
                lres = res / 2;
                local = HalfRes(raw.Local, fg, sky, res, lres, false);
                if (front != null) localFront = HalfRes(raw.Local, fg, sky, res, lres, true);
            }

            Color32[] ambient = null, ambientFront = null;
            if (raw.Ambient != null)
            {
                ambient = OneLayer(raw.Ambient, fg, sky, false);
                if (front != null) ambientFront = OneLayer(raw.Ambient, fg, sky, true);
            }

            Pool<byte>.Return(fg); Pool<byte>.Return(state); Pool<float>.Return(carried);
            return new FaceLayers
            {
                Ambient = ambient,
                AmbientFront = ambientFront,
                Back = back,
                Front = front,
                Local = local,
                LocalFront = localFront,
                LocalRes = lres,
                Grids = new FaceGrids { BgNode = bgNode, BgCell = bgCell, FgNode = fgNode, FgCell = fgCell },
            };
        }

        /// <summary>The pixels of one layer (foreground or background, never sky) of a light image; black elsewhere.</summary>
        private static Color32[] OneLayer(Color32[] src, byte[] fg, bool[] sky, bool foreground)
        {
            var outp = Pool<Color32>.RentDirty(src.Length);
            var black = new Color32(0, 0, 0, 255);
            for (int p = 0; p < src.Length; p++)
            {
                if (sky[p] || (fg[p] != 0) != foreground) { outp[p] = black; continue; }
                Color32 c = src[p];
                outp[p] = new Color32(c.r, c.g, c.b, 255);
            }
            return outp;
        }

        /// <summary>The local-light image at half resolution, averaged over the pixels of one layer (foreground or background, never sky); black elsewhere.</summary>
        private static Color32[] HalfRes(Color32[] src, byte[] fg, bool[] sky, int res, int lres, bool foreground)
        {
            var outp = Pool<Color32>.RentDirty(lres * lres);
            for (int y = 0; y < lres; y++)
                for (int x = 0; x < lres; x++)
                {
                    int r = 0, g = 0, b = 0, cnt = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int p = (y * 2 + dy) * res + x * 2 + dx;
                            if (sky[p] || (fg[p] != 0) != foreground) continue;
                            Color32 c = src[p];
                            r += c.r; g += c.g; b += c.b; cnt++;
                        }
                    outp[y * lres + x] = cnt > 0 ? new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt), 255) : new Color32(0, 0, 0, 255);
                }
            return outp;
        }

        /// <summary>Mean depth of the nearest foreground surface among the pixels of a window; 0 if it has no foreground.</summary>
        private static float NearFg(float[] D, byte[] fg, int res, int x0, int y0, int x1, int y1)
        {
            float lo = float.MaxValue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int p = y * res + x;
                    if (fg[p] != 0 && D[p] < lo) lo = D[p];
                }
            if (lo == float.MaxValue) return 0f;
            double sum = 0; int cnt = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int p = y * res + x;
                    if (fg[p] != 0 && D[p] <= lo * SameRatio) { sum += D[p]; cnt++; }
                }
            return (float)(sum / cnt);
        }

        /// <summary>Separable running max (or min) over a square window, clamped at the borders.</summary>
        private static float[] BoxExtreme(float[] src, int w, int rad, bool max)
        {
            var tmp = new float[w * w];
            var dst = new float[w * w];
            for (int y = 0; y < w; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = Mathf.Max(0, x - rad), b = Mathf.Min(w - 1, x + rad);
                    float v = src[y * w + a];
                    for (int k = a + 1; k <= b; k++) { float s = src[y * w + k]; if (max ? s > v : s < v) v = s; }
                    tmp[y * w + x] = v;
                }
            for (int y = 0; y < w; y++)
            {
                int a = Mathf.Max(0, y - rad), b = Mathf.Min(w - 1, y + rad);
                for (int x = 0; x < w; x++)
                {
                    float v = tmp[a * w + x];
                    for (int k = a + 1; k <= b; k++) { float s = tmp[k * w + x]; if (max ? s > v : s < v) v = s; }
                    dst[y * w + x] = v;
                }
            }
            return dst;
        }

        private const float Behind = 1.1f; // a fill source must be this much farther than the foreground it fills under

        /// <summary>
        /// Background depth at nodes covered by foreground, grown in from known nodes around them, but only from
        /// ones that lie behind that foreground: the rim of a wall must get the hills beyond it, not the wall.
        /// </summary>
        private static void FillNodes(float[] node, bool[] known, float[] fgNode, int n, float range)
        {
            var todo = new List<int>();
            for (int o = 0; o < node.Length; o++) if (!known[o]) todo.Add(o);
            var vals = new List<float>();
            var next = new List<int>();
            while (todo.Count > 0)
            {
                vals.Clear(); next.Clear();
                foreach (int o in todo)
                {
                    int i = o % n, j = o / n;
                    float sum = 0f, min = fgNode[o] * Behind; int cnt = 0;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ii = i + di, jj = j + dj;
                            if (ii < 0 || jj < 0 || ii >= n || jj >= n) continue;
                            int a = jj * n + ii;
                            if (known[a] && node[a] >= min) { sum += node[a]; cnt++; }
                        }
                    vals.Add(cnt > 0 ? sum / cnt : -1f);
                }
                bool any = false;
                for (int k = 0; k < todo.Count; k++)
                {
                    if (vals[k] < 0f) { next.Add(todo[k]); continue; }
                    node[todo[k]] = vals[k]; known[todo[k]] = true; any = true;
                }
                if (!any) { foreach (int o in next) node[o] = range; break; }
                var t = todo; todo = next; next = t;
            }
        }

        /// <summary>
        /// Grow colour (or sky, which wins where it touches) into the unknown pixels, a ring per pass. Each filled
        /// pixel carries the depth of what it was filled from, and a pixel only takes from neighbours that lie
        /// behind the foreground it is under, so the fill always comes from the far side of an edge.
        /// </summary>
        private static float[] FillPixels(Color32[] px, byte[] state, List<int> unknown, Color32[] original, float[] D, int res)
        {
            var carried = Pool<float>.RentDirty(D.Length); Array.Copy(D, carried, D.Length);
            var todo = unknown;
            var next = new List<int>();
            var filled = new List<int>();
            var fillCol = new List<Color32>();
            var fillDepth = new List<float>();
            var acc = new FillAcc();
            for (int pass = 0; pass < 400 && todo.Count > 0; pass++)
            {
                next.Clear(); filled.Clear(); fillCol.Clear(); fillDepth.Clear();
                foreach (int p in todo)
                {
                    int x = p % res, y = p / res;
                    acc.Reset(D[p] * Behind);
                    if (x > 0) acc.Add(state[p - 1], px[p - 1], carried[p - 1]);
                    if (x < res - 1) acc.Add(state[p + 1], px[p + 1], carried[p + 1]);
                    if (y > 0) acc.Add(state[p - res], px[p - res], carried[p - res]);
                    if (y < res - 1) acc.Add(state[p + res], px[p + res], carried[p + res]);
                    if (acc.Cnt == 0 && !acc.Sky) { next.Add(p); continue; }
                    filled.Add(p);
                    if (acc.Sky || acc.Cnt == 0) { fillCol.Add(new Color32(original[p].r, original[p].g, original[p].b, 0)); fillDepth.Add(float.MaxValue); }
                    else { fillCol.Add(new Color32((byte)(acc.R / acc.Cnt), (byte)(acc.G / acc.Cnt), (byte)(acc.B / acc.Cnt), FilledAlpha)); fillDepth.Add(acc.Depth / acc.Cnt); }
                }
                if (filled.Count == 0) break;
                for (int k = 0; k < filled.Count; k++)
                {
                    px[filled[k]] = fillCol[k];
                    state[filled[k]] = fillCol[k].a == 0 ? (byte)2 : (byte)1;
                    carried[filled[k]] = fillDepth[k];
                }
                var t = todo; todo = next; next = t;
            }
            foreach (int p in todo) { Color32 c = original[p]; c.a = FilledAlpha; px[p] = c; } // nothing behind it to grow from: keep the picture
            return carried;
        }

        private class FillAcc
        {
            public int R, G, B, Cnt; public float Depth, Min; public bool Sky;
            public void Reset(float min) { R = G = B = Cnt = 0; Depth = 0f; Sky = false; Min = min; }
            public void Add(byte s, Color32 c, float depth)
            {
                if (s == 0 || depth < Min) return;
                if (s == 2) { Sky = true; return; }
                R += c.r; G += c.g; B += c.b; Depth += depth; Cnt++;
            }
        }
    }

    /// <summary>The two relief meshes of a face, built from its grids. A vertex at view depth z on the ray through (u,v) is ((u-0.5)*2z, (v-0.5)*2z, z).</summary>
    internal static class ReliefMesh
    {
        /// <summary>Mesh arrays without the engine object, so the geometry can be checked outside the game (tools/LayerTest).</summary>
        internal class Data
        {
            public readonly List<Vector3> Verts = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Tris = new List<int>();
        }

        internal static Mesh Background(FaceGrids g, int n) => Make("LivePortals_Back", BackgroundData(g, n));
        internal static Mesh Foreground(FaceGrids g, int n) => Make("LivePortals_Front", ForegroundData(g, n));
        internal static Mesh Skirts(FaceGrids g, int n) => Make("LivePortals_Skirt", SkirtData(g, n));

        [System.ThreadStatic] private static float _tan; // of the face being built; every Data method sets it first (the loader builds on a worker thread)

        private static Vector3 At(int i, int j, int cells, float z)
        {
            if (z < 0.05f) z = 0.05f;
            return new Vector3((i / (float)cells - 0.5f) * 2f * _tan * z, (j / (float)cells - 0.5f) * 2f * _tan * z, z);
        }

        /// <summary>
        /// The far part of a face below the horizon (or of all of it), flat and far away. Drawn under everything for
        /// view rays that never meet the relief: the ring's lower edge usually dips below the far side's ground, and
        /// a ray starting under the terrain finds nothing. It shows the distant ground in that direction instead of
        /// sky. Only cells that were far in the capture: a near beam or wall painted at infinity is a brown blot
        /// in every gap you look through from far back. Above the horizon it stops, where it would put copies of
        /// things against the sky.
        /// </summary>
        internal static Data GroundShellData(FaceGrids g, int n, float distance, bool whole, float minDepth)
        {
            _tan = g.Tan;
            int cells = n - 1;
            var data = new Data();
            int rows = whole ? cells : cells / 2;
            for (int cy = 0; cy < rows; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    if (g.BgCell[cy * cells + cx] < minDepth) continue;
                    int b = data.Verts.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        int i = cx + (k & 1), j = cy + (k >> 1);
                        data.Verts.Add(At(i, j, cells, distance));
                        data.Uvs.Add(new Vector2(i / (float)cells, j / (float)cells));
                    }
                    data.Tris.Add(b); data.Tris.Add(b + 2); data.Tris.Add(b + 1);
                    data.Tris.Add(b + 1); data.Tris.Add(b + 2); data.Tris.Add(b + 3);
                }
            return data;
        }

        internal const float ShellMinDepth = 25f;
        internal static Mesh GroundShell(FaceGrids g, int n, float distance, bool whole) => Make("LivePortals_Shell", GroundShellData(g, n, distance, whole, ShellMinDepth));

        /// <summary>Is a node at depth z part of the cell surface at depth zc? Loose enough for ground seen at a grazing angle.</summary>
        private static bool OnSurface(float z, float zc)
        {
            if (z < zc) return z > zc / 1.25f || (1f / z - 1f / zc) < 0.01f;
            return z < zc * 1.5f || (1f / zc - 1f / z) < 0.02f;
        }

        /// <summary>
        /// Background: one continuous sheet over the whole face. Cells that hold two surfaces are drawn flat at
        /// the far one; their near part is in the foreground layer, in front. No holes anywhere.
        /// </summary>
        internal static Data BackgroundData(FaceGrids g, int n)
        {
            _tan = g.Tan;
            int cells = n - 1;
            var data = new Data();
            List<Vector3> verts = data.Verts; List<Vector2> uvs = data.Uvs; List<int> tris = data.Tris;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    verts.Add(At(i, j, cells, g.BgNode[j * n + i]));
                    uvs.Add(new Vector2(i / (float)cells, j / (float)cells));
                }
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    int i0 = cy * n + cx, i1 = i0 + 1, i2 = i0 + n, i3 = i2 + 1;
                    float zc = g.BgCell[cy * cells + cx];
                    bool ok = zc <= 0f || (OnSurface(g.BgNode[i0], zc) && OnSurface(g.BgNode[i1], zc) && OnSurface(g.BgNode[i2], zc) && OnSurface(g.BgNode[i3], zc));
                    if (ok)
                    {
                        tris.Add(i0); tris.Add(i2); tris.Add(i1);
                        tris.Add(i1); tris.Add(i2); tris.Add(i3);
                        continue;
                    }
                    int b = verts.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        int i = cx + (k & 1), j = cy + (k >> 1);
                        float z = g.BgNode[j * n + i];
                        if (!OnSurface(z, zc)) z = zc;
                        verts.Add(At(i, j, cells, z));
                        uvs.Add(new Vector2(i / (float)cells, j / (float)cells));
                    }
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
            return data;
        }

        /// <summary>Depth the background sheet uses at corner k (bit 0 = +x, bit 1 = +y) of a cell: its node depth, or the cell's own surface where the node belongs to another one.</summary>
        private static float Corner(FaceGrids g, int n, int cx, int cy, int k)
        {
            float z = g.BgNode[(cy + (k >> 1)) * n + cx + (k & 1)];
            float zc = g.BgCell[cy * (n - 1) + cx];
            return zc > 0f && !OnSurface(z, zc) ? zc : z;
        }

        private static bool Torn(float a, float b)
        {
            float lo = a < b ? a : b, hi = a < b ? b : a;
            return hi > lo * 1.08f && (1f / lo - 1f / hi) > 0.003f;
        }

        /// <summary>
        /// Skirts: wherever two neighbouring cells of the background sheet disagree about the depth of the nodes
        /// they share (a near surface ends there and a far one begins), a wall along those nodes' view rays from
        /// the near depth to the far one, coloured like the far cell next to it. From the capture point the walls
        /// are edge-on and invisible; from anywhere else they are what you see when you look around a near object
        /// farther than any capture point did, instead of a hole. Not real, so they are drawn first and everything
        /// real is drawn over them.
        /// </summary>
        internal static Data SkirtData(FaceGrids g, int n)
        {
            _tan = g.Tan;
            int cells = n - 1;
            var data = new Data();
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    // Shared edge with the cell to the right: nodes (cx+1, cy) and (cx+1, cy+1).
                    if (cx + 1 < cells)
                        Wall(data, cells, cx + 1, cy, cx + 1, cy + 1,
                            Corner(g, n, cx, cy, 1), Corner(g, n, cx, cy, 3), Corner(g, n, cx + 1, cy, 0), Corner(g, n, cx + 1, cy, 2), 0.5f, 0f);
                    // Shared edge with the cell above: nodes (cx, cy+1) and (cx+1, cy+1).
                    if (cy + 1 < cells)
                        Wall(data, cells, cx, cy + 1, cx + 1, cy + 1,
                            Corner(g, n, cx, cy, 2), Corner(g, n, cx, cy, 3), Corner(g, n, cx, cy + 1, 0), Corner(g, n, cx, cy + 1, 1), 0f, 0.5f);
                }
            return data;
        }

        /// <summary>a0/a1: depths the first cell uses at the two nodes; b0/b1: the second cell's. (du, dv): half a cell toward the second cell.</summary>
        private static void Wall(Data data, int cells, int i0, int j0, int i1, int j1, float a0, float a1, float b0, float b1, float du, float dv)
        {
            if (!Torn(a0, b0) && !Torn(a1, b1)) return;
            // Only between surfaces that are both fairly near (a wall and the wall behind it). When the far side is
            // distant, the far shell already shows what lies in that direction, almost exactly, since distant
            // things barely shift with the eye. A wall from a beam a metre away to the sea a hundred metres away
            // is a huge flap that, from the side, covers the sky in every gap it can be seen through.
            if (Mathf.Max(a0 + a1, b0 + b1) * 0.5f >= ShellMinDepth) return;
            // A little longer than the gap at the far end, so no crack is left between the wall and the far sheet.
            if (a0 + a1 <= b0 + b1) { b0 *= 1.03f; b1 *= 1.03f; }
            else { a0 *= 1.03f; a1 *= 1.03f; }
            // Colour from the farther of the two cells.
            float sign = (b0 + b1) >= (a0 + a1) ? 1f : -1f;
            var uv0 = new Vector2((i0 + sign * du) / cells, (j0 + sign * dv) / cells);
            var uv1 = new Vector2((i1 + sign * du) / cells, (j1 + sign * dv) / cells);
            int b = data.Verts.Count;
            data.Verts.Add(At(i0, j0, cells, a0)); data.Uvs.Add(uv0);
            data.Verts.Add(At(i1, j1, cells, a1)); data.Uvs.Add(uv1);
            data.Verts.Add(At(i0, j0, cells, b0)); data.Uvs.Add(uv0);
            data.Verts.Add(At(i1, j1, cells, b1)); data.Uvs.Add(uv1);
            data.Tris.Add(b); data.Tris.Add(b + 2); data.Tris.Add(b + 1);
            data.Tris.Add(b + 1); data.Tris.Add(b + 2); data.Tris.Add(b + 3);
        }

        /// <summary>Foreground: a quad per cell that holds any foreground pixel; the texture's alpha does the cutting.</summary>
        internal static Data ForegroundData(FaceGrids g, int n)
        {
            _tan = g.Tan;
            int cells = n - 1;
            var data = new Data();
            List<Vector3> verts = data.Verts; List<Vector2> uvs = data.Uvs; List<int> tris = data.Tris;
            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    float zc = g.FgCell[cy * cells + cx];
                    if (zc <= 0f) continue;
                    int b = verts.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        int i = cx + (k & 1), j = cy + (k >> 1);
                        float z = g.FgNode[j * n + i];
                        if (z <= 0f || z > zc * 1.2f || z < zc / 1.2f) z = zc;
                        verts.Add(At(i, j, cells, z * 0.998f)); // a hair toward the capture point: never fights the background it lies on
                        uvs.Add(new Vector2(i / (float)cells, j / (float)cells));
                    }
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                    tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                }
            return data;
        }

        /// <summary>The engine object from the arrays. Main thread.</summary>
        internal static Mesh Make(string name, Data data)
        {
            List<Vector3> verts = data.Verts; List<Vector2> uvs = data.Uvs; List<int> tris = data.Tris;
            if (verts.Count == 0) return null;
            var m = new Mesh { name = name };
            m.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            var cols = new Color32[verts.Count];
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < cols.Length; i++) cols[i] = white;
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.colors32 = cols;
            if (WindowMaterial.DoubleSidedMeshes)
            {
                // The chosen shader culls back faces and the sheets are seen from both sides: add the reverse of every triangle.
                int count = tris.Count;
                var both = new List<int>(count * 2);
                both.AddRange(tris);
                for (int i = 0; i < count; i += 3) { both.Add(tris[i]); both.Add(tris[i + 2]); both.Add(tris[i + 1]); }
                tris = both;
            }
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
