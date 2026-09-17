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

        internal static GrassSet Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var set = new GrassSet();
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
                        // One draw call takes at most 1023 instances: split here, once, so drawing allocates nothing.
                        for (int start = 0; start < n; start += 1023)
                        {
                            int count = Mathf.Min(1023, n - start);
                            var part = new Group { Prefab = g.Prefab, Local = new Matrix4x4[count], World = new Matrix4x4[count] };
                            System.Array.Copy(all, start, part.Local, 0, count);
                            set.Groups.Add(part);
                        }
                    }
                }
                set.Resolve();
                return set;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("LivePortals: could not read " + Path.GetFileName(path) + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Find each group's mesh and material: they belong to the clutter prefabs the game keeps loaded.</summary>
        private void Resolve()
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
        internal void Draw(Camera cam, Matrix4x4 anchor)
        {
            foreach (var g in Groups)
            {
                if (g.Mesh == null || g.Material == null || g.World == null) continue;
                for (int k = 0; k < g.Local.Length; k++) g.World[k] = anchor * g.Local[k];
                Graphics.DrawMeshInstanced(g.Mesh, 0, g.Material, g.World, g.World.Length, null, ShadowCastingMode.Off, false, Plugin.FaceLayer, cam);
            }
        }
    }
}
