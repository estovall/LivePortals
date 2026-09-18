using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// The window's dissolve-in as blocks: a grid of cells over the pane, each with the moment it appears (mostly
    /// from the rim inward, with some randomness so the front is ragged rather than a closing iris). The plug cuts
    /// out by a noise texture built from the same cells; the picture sprite is a mesh of the same cells whose
    /// vertex alpha switches per cell. So both show exactly the same blocks, and the picture is never half
    /// transparent (0.9.14 faded it instead, which lost the blocky ring Max liked).
    /// </summary>
    internal static class Dissolve
    {
        internal const int N = 32;
        internal static readonly float[] Orders = MakeOrders();

        private static float[] MakeOrders()
        {
            var o = new float[N * N];
            var rng = new System.Random(7);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N - 0.5f, dy = (y + 0.5f) / N - 0.5f;
                    float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                    o[y * N + x] = 0.65f * r + 0.35f * (float)rng.NextDouble();
                }
            return o;
        }

        /// <summary>How much of the pane is in for a fade value: done by two thirds of the way, the last stray holes read as a glitch.</summary>
        internal static float Reveal(float visible) => Mathf.Clamp01(visible * 1.5f);

        /// <summary>Cutoff for the plug's noise texture (alpha = 1 + order*254 over 255): texels at or above it are drawn.</summary>
        internal static float Cutoff(float visible)
        {
            float v = Reveal(visible);
            return v >= 0.999f ? 0f : Mathf.Clamp01(1f - v) + 0.004f;
        }

        internal static bool CellShown(int cell, float visible)
        {
            float v = Reveal(visible);
            if (v >= 0.999f) return true;
            return (1f + Mathf.RoundToInt(Orders[cell] * 254f)) / 255f >= Cutoff(visible);
        }

        /// <summary>The plug's dissolve texture: one texel per cell block, point filtered.</summary>
        internal static Texture2D Noise()
        {
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "LivePortals_Dissolve" };
            var px = new Color32[N * N];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, (byte)(1 + Mathf.RoundToInt(Orders[i] * 254f)));
            t.SetPixels32(px);
            t.Apply(false, false);
            return t;
        }

        /// <summary>
        /// The picture's mesh: one quad per cell (its own four vertices, so its alpha is its own), covering the unit
        /// disc (corners outside the rim are pulled onto it) or the unit square. UVs as the plain pane: u = x + 0.5.
        /// </summary>
        internal static Mesh MakeMesh(bool round, out int[] cellOfQuad)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var tris = new System.Collections.Generic.List<int>();
            var cells = new System.Collections.Generic.List<int>();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var c = new Vector2[4];
                    int outside = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        float px = (x + (k & 1)) / (float)N - 0.5f, py = (y + (k >> 1)) / (float)N - 0.5f;
                        var p = new Vector2(px, py);
                        if (round && p.magnitude > 0.5f) { outside++; p = p.normalized * 0.5f; }
                        c[k] = p;
                    }
                    if (outside == 4) continue;
                    int b = verts.Count;
                    for (int k = 0; k < 4; k++) { verts.Add(new Vector3(c[k].x, c[k].y, 0f)); uvs.Add(c[k] + new Vector2(0.5f, 0.5f)); }
                    // Both windings: the pane is seen from both sides and the sprite shader culls neither, but keep it safe.
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 1); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 1); tris.Add(b + 3); tris.Add(b + 2);
                    cells.Add(y * N + x);
                }
            var m = new Mesh { name = "LivePortals_Picture" };
            m.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            var cols = new Color32[verts.Count];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255);
            m.colors32 = cols;
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            cellOfQuad = cells.ToArray();
            return m;
        }

        /// <summary>Switch the mesh's cells on or off for this fade value. cols is a scratch array of the mesh's vertex count.</summary>
        internal static void Apply(Mesh m, int[] cellOfQuad, Color32[] cols, float visible)
        {
            for (int q = 0; q < cellOfQuad.Length; q++)
            {
                // A hidden cell is black as well as clear: an additive picture shader would ignore the alpha.
                var c = CellShown(cellOfQuad[q], visible) ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                cols[q * 4] = c; cols[q * 4 + 1] = c; cols[q * 4 + 2] = c; cols[q * 4 + 3] = c;
            }
            m.colors32 = cols;
        }
    }
}
