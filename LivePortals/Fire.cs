using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// Flames are not captured as a picture either. They are see-through, write no depth and never hold still: painted
    /// onto the wall behind them they come out as a stretched copy per viewpoint, and every attempt to give them a
    /// depth of their own tore chunks out of whatever stood near them (0.9.8 to 0.9.20). Like the grass, the capture
    /// leaves them out and notes where each fire's effects stand; the window then plays copies of the game's own
    /// particle effects at those places behind the pane. Real geometry: in the right place from every angle,
    /// hidden by the pillar in front of it, burning, and as bright at night as a flame is.
    /// </summary>
    internal class FireSet
    {
        internal class Item
        {
            public string Prefab;      // the networked prefab the effect belongs to (a torch, a hearth)
            public int[] Path;         // child indices from the prefab's root down to the effect
            public string Name;        // the effect's name, to check the path still leads to the same thing
            public Vector3 Pos;        // in the far ring's frame
            public Quaternion Rot;
            public Vector3 Scale;      // world scale at capture time
            public bool[] Active;      // activeSelf of the effect's objects, depth first, as they were
        }

        /// <summary>The light of one fire, as it shone at capture time: what comes through the ring onto the viewer's side.</summary>
        internal class Source
        {
            public Vector3 Pos;        // in the far ring's frame; +z is the side the ring's front looks at
            public Color Color;
            public float Intensity, Range;
        }

        public readonly List<Item> Items = new List<Item>();
        public readonly List<Source> Sources = new List<Source>();
        private const int Magic = 0x4C504652; // "LPFR"
        internal const float Radius = 40f;

        /// <summary>A particle effect the window can play itself: part of a fire, furnace or cooking place somebody built, of ordinary size.</summary>
        internal static bool Qualifies(ParticleSystemRenderer ps, Vector3 centre)
        {
            if (ps.GetComponent<ParticleSystem>() == null) return false;
            if (Vector3.Distance(ps.transform.position, centre) > Radius) return false;
            var root = ps.GetComponentInParent<ZNetView>();
            if (root == null || root.transform == ps.transform) return false;
            return root.GetComponent<Fireplace>() != null || root.GetComponent<Smelter>() != null || root.GetComponent<CookingStation>() != null;
        }

        /// <summary>Main thread, at capture time. The effects are recorded whole: from the topmost particle system under the prefab's root.</summary>
        internal static FireSet Record(Vector3 centre, Quaternion rot, List<ParticleSystemRenderer> effects)
        {
            var set = new FireSet();
            Matrix4x4 toLocal = Matrix4x4.TRS(centre, rot, Vector3.one).inverse;
            Quaternion invRot = Quaternion.Inverse(rot);
            var seen = new HashSet<Transform>();
            var tops = new List<Transform>();
            foreach (var ps in effects)
            {
                if (ps == null) continue;
                var root = ps.GetComponentInParent<ZNetView>();
                if (root == null) continue;
                Transform top = ps.transform;
                for (Transform t = ps.transform; t != null && t != root.transform; t = t.parent)
                    if (t.GetComponent<ParticleSystem>() != null) top = t;
                if (seen.Add(top)) tops.Add(top);
            }
            // One light per fire: the strongest lamp under each fire's root.
            var roots = new HashSet<ZNetView>();
            foreach (var top in tops)
            {
                var root = top.GetComponentInParent<ZNetView>();
                if (root == null || !roots.Add(root)) continue;
                Light best = null;
                foreach (var l in root.GetComponentsInChildren<Light>(false))
                    if (l.enabled && l.type != LightType.Directional && (best == null || l.intensity * l.range > best.intensity * best.range)) best = l;
                if (best != null)
                    set.Sources.Add(new Source { Pos = toLocal.MultiplyPoint3x4(best.transform.position), Color = best.color, Intensity = best.intensity, Range = best.range });
            }
            tops.Sort((a, b) => (a.position - centre).sqrMagnitude.CompareTo((b.position - centre).sqrMagnitude));
            int max = Plugin.LiveFireMax.Value;
            foreach (var top in tops)
            {
                if (set.Items.Count >= max) break;
                var root = top.GetComponentInParent<ZNetView>();
                var path = new List<int>();
                bool ok = true;
                for (Transform t = top; t != root.transform; t = t.parent)
                {
                    if (t.parent == null) { ok = false; break; }
                    path.Insert(0, t.GetSiblingIndex());
                }
                if (!ok || path.Count == 0) continue;
                var all = top.GetComponentsInChildren<Transform>(true);
                var active = new bool[all.Length];
                for (int i = 0; i < all.Length; i++) active[i] = all[i].gameObject.activeSelf;
                set.Items.Add(new Item
                {
                    Prefab = Utils.GetPrefabName(root.gameObject),
                    Path = path.ToArray(),
                    Name = top.name,
                    Pos = toLocal.MultiplyPoint3x4(top.position),
                    Rot = invRot * top.rotation,
                    Scale = top.lossyScale,
                    Active = active,
                });
            }
            return set;
        }

        internal void Save(string path)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Magic);
                w.Write(Items.Count);
                foreach (var it in Items)
                {
                    w.Write(it.Prefab);
                    w.Write(it.Name);
                    w.Write(it.Path.Length);
                    foreach (int i in it.Path) w.Write(i);
                    w.Write(it.Pos.x); w.Write(it.Pos.y); w.Write(it.Pos.z);
                    w.Write(it.Rot.x); w.Write(it.Rot.y); w.Write(it.Rot.z); w.Write(it.Rot.w);
                    w.Write(it.Scale.x); w.Write(it.Scale.y); w.Write(it.Scale.z);
                    w.Write(it.Active.Length);
                    foreach (bool b in it.Active) w.Write(b);
                }
                w.Write(Sources.Count);
                foreach (var s in Sources)
                {
                    w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
                    w.Write(s.Color.r); w.Write(s.Color.g); w.Write(s.Color.b);
                    w.Write(s.Intensity); w.Write(s.Range);
                }
            }
        }

        /// <summary>Any thread.</summary>
        internal static FireSet Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var set = new FireSet();
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != Magic) return null;
                    int n = r.ReadInt32();
                    if (n < 0 || n > 1000) return null;
                    for (int k = 0; k < n; k++)
                    {
                        var it = new Item { Prefab = r.ReadString(), Name = r.ReadString() };
                        int pn = r.ReadInt32();
                        if (pn < 1 || pn > 32) return null;
                        it.Path = new int[pn];
                        for (int i = 0; i < pn; i++) it.Path[i] = r.ReadInt32();
                        it.Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        it.Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        it.Scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        int an = r.ReadInt32();
                        if (an < 0 || an > 4096) return null;
                        it.Active = new bool[an];
                        for (int i = 0; i < an; i++) it.Active[i] = r.ReadBoolean();
                        set.Items.Add(it);
                    }
                    if (r.BaseStream.Position < r.BaseStream.Length) // files of the first 0.9.21 build end here
                    {
                        int sn = r.ReadInt32();
                        if (sn < 0 || sn > 1000) return null;
                        for (int k = 0; k < sn; k++)
                        {
                            var s = new Source { Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) };
                            s.Color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1f);
                            s.Intensity = r.ReadSingle(); s.Range = r.ReadSingle();
                            set.Sources.Add(s);
                        }
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

        /// <summary>
        /// What the fires on the shown side of the far ring throw onto the ring's centre: a brightness (a lamp of
        /// intensity 1 at arm's length is about 1) and its colour. The falloff is the engine's own, roughly.
        /// </summary>
        internal float LightAtRing(bool frontSide, out Color colour)
        {
            float sum = 0f; Color c = Color.black;
            foreach (var s in Sources)
            {
                if ((s.Pos.z >= 0f) != frontSide && Mathf.Abs(s.Pos.z) > 0.5f) continue;
                float d = s.Pos.magnitude;
                if (d >= s.Range || s.Range <= 0.01f) continue;
                float f = 1f - d / s.Range;
                float e = s.Intensity * f * f;
                sum += e; c += s.Color * e;
            }
            colour = sum > 0.0001f ? c / sum : Color.white;
            colour.a = 1f;
            return sum;
        }

        private static GameObject _stash;

        /// <summary>
        /// Main thread: copies of the recorded effects as children of holder (which stands for the far ring's frame),
        /// on the capture layer, renderers off; the window switches them on only while its own camera renders.
        /// Nothing but particle systems survives in a copy: no lights, sounds or scripts.
        /// </summary>
        private bool EnsureStash()
        {
            if (ZNetScene.instance == null) return false;
            if (_stash == null)
            {
                // Copies are made under a switched-off parent, so none of their scripts ever wakes up before it is removed.
                _stash = new GameObject("LivePortals_Stash");
                _stash.SetActive(false);
                Object.DontDestroyOnLoad(_stash);
            }
            return true;
        }

        /// <summary>All the effects at once (the offline path). The window builds them a few per frame with BuildItem.</summary>
        internal int Build(Transform holder, List<ParticleSystemRenderer> renderers)
        {
            int built = 0;
            for (int i = 0; i < Items.Count; i++) if (BuildItem(i, holder, renderers)) built++;
            return built;
        }

        /// <summary>Copy the game's effect for Items[index] under holder. Main thread; each is an Instantiate plus the stripping of its scripts, so a window spreads them over frames.</summary>
        internal bool BuildItem(int index, Transform holder, List<ParticleSystemRenderer> renderers)
        {
            if (index < 0 || index >= Items.Count || !EnsureStash()) return false;
            var scene = ZNetScene.instance;
            var it = Items[index];
                try
                {
                    var prefab = scene.GetPrefab(it.Prefab);
                    if (prefab == null) return false;
                    Transform src = prefab.transform;
                    foreach (int i in it.Path)
                    {
                        if (i < 0 || i >= src.childCount) { src = null; break; }
                        src = src.GetChild(i);
                    }
                    if (src == null || src.name != it.Name || src.GetComponent<ParticleSystem>() == null) return false;
                    var go = Object.Instantiate(src.gameObject, _stash.transform);
                    go.name = "LivePortals_Fire_" + it.Name;
                    // Scripts first (they are what requires the other components), then everything that is not the effect.
                    for (int pass = 0; pass < 3; pass++)
                        foreach (var c in go.GetComponentsInChildren<Component>(true))
                        {
                            if (c == null || c is Transform || c is ParticleSystem || c is ParticleSystemRenderer) continue;
                            if (pass == 0 && !(c is MonoBehaviour)) continue;
                            Object.DestroyImmediate(c);
                        }
                    var all = go.GetComponentsInChildren<Transform>(true);
                    if (all.Length == it.Active.Length)
                        for (int i = 0; i < all.Length; i++) all[i].gameObject.SetActive(it.Active[i]);
                    go.SetActive(true);
                    foreach (var t in all) t.gameObject.layer = Plugin.FaceLayer;
                    bool localScale = true;
                    foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = ps.main;
                        // The far ring's frame moves with the viewer's eye (the window camera stays on the real one):
                        // particles left behind in world space would trail across the picture.
                        main.simulationSpace = ParticleSystemSimulationSpace.Local;
                        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                        main.playOnAwake = true;
                        if (main.loop) main.prewarm = true;
                        if (ps.transform == go.transform) localScale = main.scalingMode == ParticleSystemScalingMode.Local;
                    }
                    foreach (var r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        if (!r.enabled) continue; // off in the prefab: a system that only feeds sub-emitters
                        r.enabled = false;
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                        renderers.Add(r);
                    }
                    go.transform.SetParent(holder, false);
                    go.transform.localPosition = it.Pos;
                    go.transform.localRotation = it.Rot;
                    // A system that scales by its own transform alone had the prefab's scale in the world, whatever its parents'.
                    go.transform.localScale = localScale ? src.localScale : it.Scale;
                    return true;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("LivePortals: could not copy the effect " + it.Name + " of " + it.Prefab + ": " + e.Message);
                }
            return false;
        }
    }
}
