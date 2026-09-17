using System;
using System.Collections.Generic;
using System.IO;
using LivePortals;
using UnityEngine;

// A synthetic scene seen from the origin along +z (cube face 0): ground, a thin post, a creature-sized box, a long
// receding wall on the left, a hut, a far hill, sky. Everything is an axis-aligned box or a plane so rays are exact.
// Each view is written as truth (ray traced from the new eye) beside the layered relief drawn from that eye.
static class Program
{
    struct Box { public Vector3 Min, Max; public int Id; }
    static readonly List<Box> Boxes = new List<Box>
    {
        new Box { Min = new Vector3(0.9f, -1.6f, 3.0f), Max = new Vector3(1.05f, 1.2f, 3.15f), Id = 1 },   // post
        new Box { Min = new Vector3(-0.5f, -1.6f, 4.0f), Max = new Vector3(0.2f, -0.2f, 4.5f), Id = 2 },   // creature
        new Box { Min = new Vector3(-3.0f, -1.6f, 1.5f), Max = new Vector3(-1.6f, 0.9f, 14f), Id = 3 },    // wall
        new Box { Min = new Vector3(-80f, -1.6f, 70f), Max = new Vector3(80f, 14f, 90f), Id = 4 },         // hill
        new Box { Min = new Vector3(2.5f, -1.6f, 9f), Max = new Vector3(6f, 3.5f, 12f), Id = 5 },          // hut
    };
    const float GroundY = -1.6f;

    static bool Trace(Vector3 o, Vector3 d, out float t, out Color32 c)
    {
        t = float.MaxValue; c = default; int id = -1;
        if (d.y < -1e-6f) { float tg = (GroundY - o.y) / d.y; if (tg > 0) { t = tg; id = 0; } }
        foreach (var b in Boxes)
        {
            float t0 = 0f, t1 = float.MaxValue; bool ok = true;
            for (int a = 0; a < 3; a++)
            {
                float oa = o[a], da = d[a], mn = b.Min[a], mx = b.Max[a];
                if (Math.Abs(da) < 1e-9f) { if (oa < mn || oa > mx) { ok = false; break; } continue; }
                float ta = (mn - oa) / da, tb = (mx - oa) / da;
                if (ta > tb) { float s = ta; ta = tb; tb = s; }
                if (ta > t0) t0 = ta;
                if (tb < t1) t1 = tb;
                if (t0 > t1) { ok = false; break; }
            }
            if (ok && t0 > 1e-4f && t0 < t) { t = t0; id = b.Id; }
        }
        if (id < 0) return false;
        Vector3 hit = o + d * t;
        bool chk = (((int)Math.Floor(hit.x * 2 + 1e-3) + (int)Math.Floor(hit.y * 2 + 1e-3) + (int)Math.Floor(hit.z * 2 + 1e-3)) & 1) == 0;
        Color32[] pal =
        {
            new Color32(90, 140, 60, 255), new Color32(200, 60, 50, 255), new Color32(60, 170, 80, 255),
            new Color32(120, 110, 100, 255), new Color32(70, 90, 140, 255), new Color32(190, 150, 90, 255)
        };
        var k = pal[id];
        if (chk) k = new Color32((byte)(k.r * 0.7f), (byte)(k.g * 0.7f), (byte)(k.b * 0.7f), 255);
        c = k;
        return true;
    }

    // ---- Stored captures: "capture <folder with .bin and .rgba files> <key> <outdir>" draws point 0 of a real capture
    // from a few eye positions behind the ring. tools/LayerTest/export_capture.py makes the .rgba files from the PNGs.
    static Vector3 Rot(int face, Vector3 v)
    {
        switch (face)
        {
            case 1: return new Vector3(-v.x, v.y, -v.z);
            case 2: return new Vector3(-v.z, v.y, v.x);
            case 3: return new Vector3(v.z, v.y, -v.x);
            case 4: return new Vector3(v.x, v.z, -v.y);
            case 5: return new Vector3(v.x, -v.z, v.y);
            default: return v;
        }
    }

    static Color32[] ReadRgba(string path, out int res)
    {
        res = 0;
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        res = (int)Math.Round(Math.Sqrt(b.Length / 4));
        var px = new Color32[res * res];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(b[i * 4], b[i * 4 + 1], b[i * 4 + 2], b[i * 4 + 3]);
        return px;
    }

    class Point
    {
        public Vector3 Offset;
        public ReliefMesh.Data[] Back = new ReliefMesh.Data[6], Front = new ReliefMesh.Data[6], Skirt = new ReliefMesh.Data[6], Shell = new ReliefMesh.Data[6];
        public Color32[][] BackTex = new Color32[6][], FrontTex = new Color32[6][];
        public int Res;
    }

    static Point LoadPoint(string dir, string key, int p, Vector3 offset, float scale)
    {
        string binPath = Path.Combine(dir, $"{key}_p{p}.bin");
        if (!File.Exists(binPath)) return null;
        var bytes = File.ReadAllBytes(binPath);
        bool lp08 = BitConverter.ToInt32(bytes, 0) == 0x4C503038;
        int n = BitConverter.ToInt32(bytes, 4);
        float range = BitConverter.ToSingle(bytes, 8);
        float tan = lp08 ? BitConverter.ToSingle(bytes, 12) : 1f;
        int off = lp08 ? 16 : 12, cells = (n - 1) * (n - 1);
        Func<int, float[]> read = count =>
        {
            var d = new float[count];
            for (int i = 0; i < count; i++) d[i] = ((bytes[off + i * 2] << 8) | bytes[off + i * 2 + 1]) / 65535f * range;
            off += count * 2;
            return d;
        };
        var pt = new Point { Offset = offset };
        for (int f = 0; f < 6; f++)
        {
            pt.Back[f] = pt.Front[f] = pt.Skirt[f] = pt.Shell[f] = new ReliefMesh.Data();
            if (lp08 && bytes[off++] == 0) continue;
            var g = new FaceGrids { Tan = tan, BgNode = read(n * n), BgCell = read(cells), FgNode = read(n * n), FgCell = read(cells) };
            pt.Back[f] = ReliefMesh.BackgroundData(g, n); pt.Front[f] = ReliefMesh.ForegroundData(g, n); pt.Skirt[f] = ReliefMesh.SkirtData(g, n);
            pt.Shell[f] = p == 0 ? ReliefMesh.GroundShellData(g, n, range * 1.01f, true, ReliefMesh.ShellMinDepth) : new ReliefMesh.Data();
            // Into the ring's frame: face rotation, the point's own scale (secondary points sit a touch farther out), its offset.
            foreach (var m in new[] { pt.Back[f], pt.Front[f], pt.Skirt[f], pt.Shell[f] })
                for (int i = 0; i < m.Verts.Count; i++) m.Verts[i] = Rot(f, m.Verts[i]) * scale + offset;
            pt.BackTex[f] = ReadRgba(Path.Combine(dir, $"{key}_p{p}_{f}.rgba"), out int r1); if (r1 > 0) pt.Res = r1;
            pt.FrontTex[f] = ReadRgba(Path.Combine(dir, $"{key}_p{p}_f{f}.rgba"), out _);
        }
        return pt;
    }

    // POINTS=n limits how many viewpoints are drawn (default all); LAYER_ONLY=skirt shows the under pass alone.
    static void DrawCapture(string dir, string key, string outDir)
    {
        var offsets = new List<Vector3>();
        foreach (var line in File.ReadAllLines(Path.Combine(dir, key + ".txt")))
            if (line.Contains(".offset="))
            {
                var parts = line.Substring(line.IndexOf('=') + 1).Split(',');
                offsets.Add(new Vector3(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
            }
        int max = int.TryParse(Environment.GetEnvironmentVariable("POINTS"), out int mp) ? mp : 99;
        var points = new List<Point>();
        for (int p = 0; p < offsets.Count && p < max; p++)
        {
            var pt = LoadPoint(dir, key, p, offsets[p], 1f + 0.01f * p);
            if (pt != null) points.Add(pt);
        }
        // Where a player's camera ends up relative to the far ring: behind it, a bit above, often off to a side.
        Vector3[] eyes = { new Vector3(0, 1f, -5f), new Vector3(2.5f, 1.2f, -4f), new Vector3(-3f, 0.8f, -3f), new Vector3(0, 2f, -2.5f), new Vector3(1f, 0.3f, -1.2f), new Vector3(-0.5f, 1.5f, -9f) };
        // EYES="x,y,z;x,y,z;..." replaces them (up to six are laid out by capviews.sh).
        string eyesEnv = Environment.GetEnvironmentVariable("EYES");
        if (!string.IsNullOrEmpty(eyesEnv))
        {
            var list = new List<Vector3>();
            foreach (var e3 in eyesEnv.Split(';'))
            {
                var c = e3.Split(',');
                list.Add(new Vector3(float.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture)));
            }
            eyes = list.ToArray();
        }
        int W = 640;
        string fr = Environment.GetEnvironmentVariable("FRUSTUM");
        if (!string.IsNullOrEmpty(fr))
        {
            var c5 = fr.Split(',');
            Frustum = new float[5];
            for (int i = 0; i < 5; i++) Frustum[i] = float.Parse(c5[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        float fl = 1f / (float)Math.Tan(20 * Math.PI / 180);
        string only = Environment.GetEnvironmentVariable("LAYER_ONLY") ?? "";
        for (int e = 0; e < eyes.Length; e++)
        {
            var img = new Color32[W * W]; var zb = new float[W * W];
            for (int i = 0; i < zb.Length; i++) { zb[i] = float.MaxValue; img[i] = new Color32(120, 170, 230, 255); }
            if (Frustum == null) LookAt(eyes[e], new Vector3(0, 0, -0.15f));
            ClipZ = -0.15f; // the window's near plane lies on the pane, 0.15 m behind the capture point
            foreach (var pt in points)
                for (int f = 0; f < 6; f++)
                {
                    Raster(pt.Skirt[f], pt.BackTex[f], pt.Res, eyes[e], fl, W, img, zb, Layers.CutoffAll);
                    Raster(pt.Shell[f], pt.BackTex[f], pt.Res, eyes[e], fl, W, img, zb, Layers.CutoffAll);
                }
            for (int i = 0; i < zb.Length; i++) zb[i] = float.MaxValue;
            foreach (var pt in points)
                for (int f = 0; f < 6 && only != "skirt"; f++)
                {
                    if (Environment.GetEnvironmentVariable("NO_CAPTURED") == null) Raster(pt.Back[f], pt.BackTex[f], pt.Res, eyes[e], fl, W, img, zb, Layers.CutoffCaptured);
                    if (Environment.GetEnvironmentVariable("NO_FILLED") == null) Raster(pt.Back[f], pt.BackTex[f], pt.Res, eyes[e], fl, W, img, zb, Layers.CutoffAll, Layers.FilledPush, pt.Offset);
                    if (only != "nofg") Raster(pt.Front[f], pt.FrontTex[f], pt.Res, eyes[e], fl, W, img, zb, 0.5f);
                }
            ClipZ = float.MinValue;
            // Only what shows through the pane (an ellipse 2.7 x 2.8 m on the ring plane) counts.
            for (int y = 0; y < W; y++)
                for (int x = 0; x < W; x++)
                {
                    Vector3 d = CamF + CamR * (((x + 0.5f) / W - 0.5f) * 2f / fl) + CamU * (((y + 0.5f) / W - 0.5f) * 2f / fl);
                    float t = (-0.15f - eyes[e].z) / d.z;
                    Vector3 hit = eyes[e] + d * t;
                    if (Frustum == null && (t <= 0 || hit.x * hit.x / (1.35f * 1.35f) + hit.y * hit.y / (1.4f * 1.4f) > 1f)) img[y * W + x] = new Color32(30, 30, 30, 255);
                }
            CamR = new Vector3(1, 0, 0); CamU = new Vector3(0, 1, 0); CamF = new Vector3(0, 0, 1);
            WritePpm(Path.Combine(outDir, $"cap{e}.ppm"), img, W, false, W);
        }
    }

    /// <summary>Decode a stored PNG with the mod's own decoder and print a checksum plus the corners; also writes a PPM next to it for a look.</summary>
    static void PngCheck(string path)
    {
        var file = File.ReadAllBytes(path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var px = LivePortals.Png.Decode(file, out int w, out int h);
        long decodeMs = sw.ElapsedMilliseconds;
        if (px == null) { Console.WriteLine("FAILED " + path); return; }
        sw.Restart();
        var chain = LivePortals.Mips.Chain(px, w, h, out int levels);
        long mipMs = sw.ElapsedMilliseconds;
        ulong sum = 14695981039346656037UL;
        for (int i = 0; i < px.Length; i++) { sum ^= px[i]; sum *= 1099511628211UL; }
        Console.WriteLine($"{Path.GetFileName(path)}: {w}x{h} decode {decodeMs} ms, mips {levels} levels {chain.Length} bytes in {mipMs} ms, fnv {sum:x16}, bottom-left {px[0]},{px[1]},{px[2]},{px[3]} top-right {px[px.Length-4]},{px[px.Length-3]},{px[px.Length-2]},{px[px.Length-1]}");
        using (var o = new BinaryWriter(File.Create(path + ".ppm")))
        {
            o.Write(System.Text.Encoding.ASCII.GetBytes("P6 " + w + " " + h + " 255" + (char)10));
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; o.Write(px[i]); o.Write(px[i + 1]); o.Write(px[i + 2]); }
        }
    }

    static void Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "capture") { DrawCapture(args[1], args[2], args[3]); return; }
        if (args.Length >= 2 && args[0] == "png") { PngCheck(args[1]); return; }
        int res = 768, step = 6;
        float range = 120f;
        string outDir = args.Length > 0 ? args[0] : ".";
        var raw = new RawFace { Col = new Color32[res * res], Sky = new bool[res * res], Depth = new float[res * res] };
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                var local = new Vector3(((x + 0.5f) / res - 0.5f) * 2f, ((y + 0.5f) / res - 0.5f) * 2f, 1f);
                int p = y * res + x;
                if (Trace(Vector3.zero, local, out float t, out Color32 c) && t < range) { raw.Col[p] = c; raw.Depth[p] = Math.Max(0.05f, t); }
                else { raw.Col[p] = new Color32(150, 170, 200, 0); raw.Sky[p] = true; raw.Depth[p] = range; }
            }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layers = Layers.Process(raw, res, step, range);
        Console.WriteLine($"Layers.Process: {sw.ElapsedMilliseconds} ms");
        int n = res / step + 1;
        var bg = ReliefMesh.BackgroundData(layers.Grids, n);
        var fg = ReliefMesh.ForegroundData(layers.Grids, n);
        Console.WriteLine($"bg verts {bg.Verts.Count} tris {bg.Tris.Count / 3}; fg verts {fg.Verts.Count} tris {fg.Tris.Count / 3}");
        if (args.Length > 1)
        {
            int cells = n - 1;
            // hut top edge: y = 3.5 at z = 9 -> v; x from 2.5..6 at z 9
            float uu = (0.97f / 3f) / 2f + 0.5f, vv = (0.6f / 9f) / 2f + 0.5f;
            int ccx = (int)(uu * cells), ccy = (int)(vv * cells);
            for (int cy = ccy - 2; cy <= ccy + 2; cy++)
            {
                var line = "";
                for (int cx = ccx - 3; cx <= ccx + 3; cx++)
                    line += $" [{cx},{cy} c{layers.Grids.BgCell[cy * cells + cx]:0.0} n{layers.Grids.BgNode[cy * n + cx]:0.0} a{layers.Back[(cy * 6 + 3) * res + cx * 6 + 3].a}]";
                Console.WriteLine(line);
            }
        }
        WritePpm(Path.Combine(outDir, "tex_back.ppm"), layers.Back, res, true);
        WritePpm(Path.Combine(outDir, "tex_front.ppm"), layers.Front, res, true);

        Vector3[] eyes = { new Vector3(0, 0, 0), new Vector3(0.8f, 0.2f, -1.5f), new Vector3(-0.9f, 0f, -2.5f), new Vector3(1.3f, -0.4f, -0.5f) };
        int W = 512;
        float f = 1f / (float)Math.Tan(35 * Math.PI / 180); // 70 degree fov
        for (int e = 0; e < eyes.Length; e++)
        {
            var truth = new Color32[W * W];
            var mine = new Color32[W * W];
            var zb = new float[W * W];
            for (int i = 0; i < zb.Length; i++) { zb[i] = float.MaxValue; mine[i] = new Color32(255, 0, 255, 255); }
            for (int y = 0; y < W; y++)
                for (int x = 0; x < W; x++)
                {
                    var d = new Vector3(((x + 0.5f) / W - 0.5f) * 2f / f, ((y + 0.5f) / W - 0.5f) * 2f / f, 1f);
                    truth[y * W + x] = Trace(eyes[e], d, out float t, out Color32 c) && t < 400 ? c : new Color32(255, 0, 255, 255);
                }
            string only = Environment.GetEnvironmentVariable("LAYER_ONLY") ?? "";
            // Pass one: the skirts. Pass two, on a fresh depth buffer: everything real, over them.
            if (only == "" || only == "skirt" || only == "under") Raster(ReliefMesh.SkirtData(layers.Grids, n), layers.Back, res, eyes[e], f, W, mine, zb, Layers.CutoffAll);
            for (int i = 0; i < zb.Length; i++) zb[i] = float.MaxValue;
            if (only == "skirt" || only == "under") only = "none";
            if (only != "fg" && only != "none") Raster(bg, layers.Back, res, eyes[e], f, W, mine, zb, Layers.CutoffCaptured);
            if (only != "fg" && only != "none") Raster(bg, layers.Back, res, eyes[e], f, W, mine, zb, Layers.CutoffAll, Layers.FilledPush);
            if (only != "bg" && only != "none") Raster(fg, layers.Front, res, eyes[e], f, W, mine, zb, 0.5f);
            int bad = 0;
            for (int i = 0; i < truth.Length; i++)
                if (Math.Abs(truth[i].r - mine[i].r) + Math.Abs(truth[i].g - mine[i].g) + Math.Abs(truth[i].b - mine[i].b) > 40) bad++;
            Console.WriteLine($"view{e} eye {eyes[e]}: {100f * bad / truth.Length:0.00}% pixels differ from truth");
            var both = new Color32[W * 2 * W];
            for (int y = 0; y < W; y++) { Array.Copy(truth, y * W, both, y * 2 * W, W); Array.Copy(mine, y * W, both, y * 2 * W + W, W); }
            WritePpm(Path.Combine(outDir, $"view{e}.ppm"), both, W * 2, false, W);
        }
    }

    static float[] Frustum; // l, r, b, t, near from a "LivePortals dump:" log line (FRUSTUM=l,r,b,t,near): draws the game's exact picture
    static bool NoDepth = Environment.GetEnvironmentVariable("NO_DEPTH") != null; // painter's order: what a broken depth test would look like
    static float ClipZ = float.MinValue; // world z below which nothing is drawn

    // Camera basis (world axes by default). With LookAt set, views aim at the pane like a player's camera does.
    static Vector3 CamR = new Vector3(1, 0, 0), CamU = new Vector3(0, 1, 0), CamF = new Vector3(0, 0, 1);

    static void LookAt(Vector3 eye, Vector3 target)
    {
        Vector3 f = target - eye; f = f * (1f / (float)Math.Sqrt(f.x * f.x + f.y * f.y + f.z * f.z));
        Vector3 r = new Vector3(f.z, 0, -f.x); r = r * (1f / (float)Math.Sqrt(r.x * r.x + r.z * r.z));
        CamF = f; CamR = r; CamU = new Vector3(f.y * r.z - f.z * r.y, f.z * r.x - f.x * r.z, f.x * r.y - f.y * r.x);
    }

    static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

    static void Clip(List<Vector3> v, List<Vector2> u, List<Vector3> ov, List<Vector2> ou, Func<Vector3, float> dist)
    {
        ov.Clear(); ou.Clear();
        for (int k = 0; k < v.Count; k++)
        {
            Vector3 a = v[k], b = v[(k + 1) % v.Count];
            Vector2 ua = u[k], ub = u[(k + 1) % v.Count];
            float da = dist(a), db = dist(b);
            if (da >= 0) { ov.Add(a); ou.Add(ua); }
            if ((da >= 0) != (db >= 0)) { float s = da / (da - db); ov.Add(a + (b - a) * s); ou.Add(ua + (ub - ua) * s); }
        }
    }

    static void Raster(ReliefMesh.Data m, Color32[] tex, int res, Vector3 eye, float f, int W, Color32[] img, float[] zb, float cutoff, float scale = 1f, Vector3 pivot = default)
    {
        if (tex == null) return;
        var poly = new List<Vector3>(); var puv = new List<Vector2>();
        var tmpV = new List<Vector3>(); var tmpU = new List<Vector2>();
        for (int t = 0; t < m.Tris.Count; t += 3)
        {
            poly.Clear(); puv.Clear();
            for (int k = 0; k < 3; k++) { poly.Add((m.Verts[m.Tris[t + k]] - pivot) * scale + pivot); puv.Add(m.Uvs[m.Tris[t + k]]); }
            if (ClipZ > float.MinValue)
            {
                Clip(poly, puv, tmpV, tmpU, p => p.z - ClipZ);
                poly.Clear(); puv.Clear(); poly.AddRange(tmpV); puv.AddRange(tmpU);
            }
            for (int k = 0; k < poly.Count; k++) { Vector3 d = poly[k] - eye; poly[k] = new Vector3(Dot(d, CamR), Dot(d, CamU), Dot(d, CamF)); }
            Clip(poly, puv, tmpV, tmpU, p => p.z - 0.05f);
            for (int k = 1; k + 1 < tmpV.Count; k++) Tri(tmpV[0], tmpV[k], tmpV[k + 1], tmpU[0], tmpU[k], tmpU[k + 1], tex, res, f, W, img, zb, cutoff);
        }
    }

    static void Tri(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, Color32[] tex, int res, float f, int W, Color32[] img, float[] zb, float cutoff)
    {
        float ax, ay, bx, by, cx, cy;
        if (Frustum != null)
        {
            // The game's off-axis frustum: l, r, b, t on the plane at distance near.
            float l = Frustum[0], r = Frustum[1], bo = Frustum[2], t = Frustum[3], nr = Frustum[4];
            ax = (a.x / a.z * nr - l) / (r - l) * W; ay = (a.y / a.z * nr - bo) / (t - bo) * W;
            bx = (b.x / b.z * nr - l) / (r - l) * W; by = (b.y / b.z * nr - bo) / (t - bo) * W;
            cx = (c.x / c.z * nr - l) / (r - l) * W; cy = (c.y / c.z * nr - bo) / (t - bo) * W;
        }
        else
        {
            ax = (a.x / a.z * f * 0.5f + 0.5f) * W; ay = (a.y / a.z * f * 0.5f + 0.5f) * W;
            bx = (b.x / b.z * f * 0.5f + 0.5f) * W; by = (b.y / b.z * f * 0.5f + 0.5f) * W;
            cx = (c.x / c.z * f * 0.5f + 0.5f) * W; cy = (c.y / c.z * f * 0.5f + 0.5f) * W;
        }
        float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.Abs(area) < 1e-9f) return;
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                float w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float iz = w0 / a.z + w1 / b.z + w2 / c.z;
                float z = 1f / iz;
                if (!NoDepth && z >= zb[y * W + x]) continue;
                float u = (w0 * ua.x / a.z + w1 * ub.x / b.z + w2 * uc.x / c.z) * z;
                float v = (w0 * ua.y / a.z + w1 * ub.y / b.z + w2 * uc.y / c.z) * z;
                int tx = Math.Min(res - 1, Math.Max(0, (int)(u * res))), ty = Math.Min(res - 1, Math.Max(0, (int)(v * res)));
                Color32 t = tex[ty * res + tx];
                if (t.a < cutoff * 255f) continue;
                zb[y * W + x] = z;
                img[y * W + x] = t;
            }
    }

    static void WritePpm(string path, Color32[] px, int w, bool showAlpha, int h = -1)
    {
        if (px == null) return;
        if (h < 0) h = px.Length / w;
        using (var fs = File.Create(path))
        {
            var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            fs.Write(head, 0, head.Length);
            var row = new byte[w * 3];
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    if (showAlpha && c.a < 128) c = new Color32(255, 0, 255, 255);
                    row[x * 3] = c.r; row[x * 3 + 1] = c.g; row[x * 3 + 2] = c.b;
                }
                fs.Write(row, 0, row.Length);
            }
        }
    }
}
