using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivePortals
{
    /// <summary>
    /// Grass is not captured as a picture. A meadow is thousands of thin blades at every depth; as a relief it is a
    /// stack of cut-out cards over smeared ground, and from any eye but the capture's own it falls apart (tried with
    /// depth, without depth, from one viewpoint and from four: 0.8.0 to 0.8.16). Instead the capture leaves the
    /// game's instanced clutter out of its renders and records where every tuft around the portal stands; the window
    /// then draws the game's own grass meshes, with the game's own material, at those places behind the pane. Real
    /// geometry: right from every angle, swaying in the wind, lit by the hour.
    /// </summary>
    internal class GrassSet
    {
        internal class Group
        {
            public string Prefab;        // the clutter prefab the batch came from, which names its mesh and material
            public Matrix4x4[] Local;    // instance transforms relative to the far ring's centre and rotation
            public Matrix4x4[] World;    // scratch, per window, rewritten every frame
            public Mesh Mesh;
            public Material Material;
        }

        public readonly List<Group> Groups = new List<Group>();
        private const int Magic = 0x4C504752; // "LPGR"
        internal const float Radius = 45f;

        /// <summary>Main thread, at capture time: every clutter instance within Radius of the portal, in the portal's frame.</summary>
        internal static GrassSet Record(Vector3 centre, Quaternion rot)
        {
            var set = new GrassSet();
            Matrix4x4 toLocal = Matrix4x4.TRS(centre, rot, Vector3.one).inverse;
            float gap = Plugin.GrassGap.Value;
            var byPrefab = new Dictionary<string, List<Matrix4x4>>();
            foreach (var u in InstanceRenderer.Instances)
            {
                var ir = u as InstanceRenderer;
                if (ir == null || !ir.isActiveAndEnabled || ir.m_mesh == null || ir.m_material == null || ir.m_instanceCount <= 0) continue;
                if (Utils.DistanceXZ(ir.transform.position, centre) > Radius + 12f) continue;
                string prefab = Utils.GetPrefabName(ir.gameObject);
                if (!byPrefab.TryGetValue(prefab, out var list)) byPrefab[prefab] = list = new List<Matrix4x4>();
                for (int k = 0; k < ir.m_instanceCount; k++)
                {
                    Matrix4x4 m = ir.m_instances[k];
                    float dx = m.m03 - centre.x, dz = m.m23 - centre.z, d2 = dx * dx + dz * dz;
                    if (d2 > Radius * Radius || d2 < gap * gap) continue;
                    list.Add(toLocal * m);
                }
            }
            foreach (var kv in byPrefab)
                if (kv.Value.Count > 0) set.Groups.Add(new Group { Prefab = kv.Key, Local = kv.Value.ToArray() });
            return set;
        }

        internal int Count { get { int n = 0; foreach (var g in Groups) n += g.Local.Length; return n; } }

        internal void Save(string path)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Magic);
                w.Write(Groups.Count);
                foreach (var g in Groups)
                {
                    w.Write(g.Prefab);
                    w.Write(g.Local.Length);
                    foreach (var m in g.Local)
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 4; c++) w.Write(m[r, c]);
                }
            }
        }

        /// <summary>The stored tufts, budgeted, without their meshes yet: any thread. Resolve() on the main thread finds those.</summary>
        internal static GrassSet Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var set = new GrassSet();
                var raw = new List<KeyValuePair<string, Matrix4x4[]>>();
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != Magic) return null;
                    int groups = r.ReadInt32();
                    for (int i = 0; i < groups; i++)
                    {
                        var g = new Group { Prefab = r.ReadString() };
                        int n = r.ReadInt32();
                        if (n < 0 || n > 400000) return null;
                        var all = new Matrix4x4[n];
                        for (int k = 0; k < n; k++)
                        {
                            var m = Matrix4x4.identity;
                            for (int row = 0; row < 3; row++)
                                for (int c = 0; c < 4; c++) m[row, c] = r.ReadSingle();
                            all[k] = m;
                        }
                        raw.Add(new KeyValuePair<string, Matrix4x4[]>(g.Prefab, all));
                    }
                }
                // Budget: keep the tufts nearest the far ring (every instance is rewritten and submitted each redraw).
                int budget = Plugin.GrassMaxInstances.Value;
                if (budget <= 0) return null;
                int total = 0;
                foreach (var kv in raw) total += kv.Value.Length;
                if (total > budget)
                {
                    var flat = new List<KeyValuePair<float, KeyValuePair<string, Matrix4x4>>>(total);
                    foreach (var kv in raw)
                        foreach (var m in kv.Value)
                        {
                            float dx = m.m03, dz = m.m23;
                            flat.Add(new KeyValuePair<float, KeyValuePair<string, Matrix4x4>>(dx * dx + dz * dz, new KeyValuePair<string, Matrix4x4>(kv.Key, m)));
                        }
                    flat.Sort((a, b) => a.Key.CompareTo(b.Key));
                    var kept = new Dictionary<string, List<Matrix4x4>>();
                    for (int k = 0; k < budget; k++)
                    {
                        if (!kept.TryGetValue(flat[k].Value.Key, out var list)) kept[flat[k].Value.Key] = list = new List<Matrix4x4>();
                        list.Add(flat[k].Value.Value);
                    }
                    raw.Clear();
                    foreach (var kv in kept) raw.Add(new KeyValuePair<string, Matrix4x4[]>(kv.Key, kv.Value.ToArray()));
                }
                // One draw call takes at most 1023 instances: split here, once, so drawing allocates nothing.
                foreach (var kv in raw)
                {
                    var all = kv.Value;
                    for (int start = 0; start < all.Length; start += 1023)
                    {
                        int count = Mathf.Min(1023, all.Length - start);
                        var part = new Group { Prefab = kv.Key, Local = new Matrix4x4[count], World = new Matrix4x4[count] };
                        System.Array.Copy(all, start, part.Local, 0, count);
                        set.Groups.Add(part);
                    }
                }
                return set;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("LivePortals: could not read " + Path.GetFileName(path) + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Find each group's mesh and material: they belong to the clutter prefabs the game keeps loaded.</summary>
        internal void Resolve()
        {
            var clutter = ClutterSystem.instance;
            if (clutter == null) return;
            foreach (var g in Groups)
                foreach (var c in clutter.m_clutter)
                {
                    if (c == null || c.m_prefab == null || c.m_prefab.name != g.Prefab) continue;
                    var ir = c.m_prefab.GetComponent<InstanceRenderer>();
                    if (ir != null) { g.Mesh = ir.m_mesh; g.Material = ir.m_material; }
                    break;
                }
        }

        /// <summary>Queue the grass for this camera, this frame. anchor: where the far ring's centre and rotation sit in front of the viewer.</summary>
        /// <param name="gain">How much of its light the grass keeps, per channel (Capture.MeasureGrassGain): the fog, occlusion and shadow the game would lay on it over there.</param>
        /// <param name="grow">0..1: how tall the blades stand (they grow in as the viewer comes near).</param>
        internal void Draw(Camera cam, Matrix4x4 anchor, Color gain, float grow = 1f)
        {
            if (grow <= 0.01f) return;
            bool scaled = grow < 0.999f;
            Matrix4x4 squash = Matrix4x4.Scale(new Vector3(1f, grow, 1f));
            bool dim = Mathf.Abs(gain.r - 1f) + Mathf.Abs(gain.g - 1f) + Mathf.Abs(gain.b - 1f) > 0.03f;
            foreach (var g in Groups)
            {
                if (g.Mesh == null || g.Material == null || g.World == null) continue;
                if (scaled) for (int k = 0; k < g.Local.Length; k++) g.World[k] = anchor * g.Local[k] * squash;
                else for (int k = 0; k < g.Local.Length; k++) g.World[k] = anchor * g.Local[k];
                MaterialPropertyBlock block = null;
                if (dim && g.Material.HasProperty("_Color"))
                {
                    // The game's own material, untouched: the colour goes in a property block. Colours are given in
                    // display values, the gain is in light.
                    if (_block == null) _block = new MaterialPropertyBlock();
                    Color c = g.Material.color;
                    _block.SetColor("_Color", new Color(c.r * Mathf.LinearToGammaSpace(gain.r), c.g * Mathf.LinearToGammaSpace(gain.g), c.b * Mathf.LinearToGammaSpace(gain.b), c.a));
                    block = _block;
                }
                Graphics.DrawMeshInstanced(g.Mesh, 0, g.Material, g.World, g.World.Length, block, ShadowCastingMode.Off, false, Plugin.FaceLayer, cam);
            }
        }

        private static MaterialPropertyBlock _block;
    }
}
