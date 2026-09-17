using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// Where the opening of a portal is and how big: the pane's centre, width and height.
    ///
    /// The wooden portal uses the values Max tuned in game (PaneWidth, PaneHeight, RingCenterOffset). Every other
    /// portal (the big stone one, modded ones) is measured: rays from the middle of the model, inside the portal's
    /// plane, against the portal's own solid colliders find the inside of the frame to the left, right, above and
    /// below. The "Other portals" config values override what was measured; the numpad keys tune whichever kind of
    /// portal is nearest.
    /// </summary>
    internal struct PortalShape
    {
        public Vector3 Centre;
        public float Width, Height;

        private struct Measured { public bool Ok; public float Width, Height, CentreHeight; }
        private static readonly Dictionary<int, Measured> Cache = new Dictionary<int, Measured>();
        private static readonly HashSet<string> Logged = new HashSet<string>();

        internal static bool IsWood(TeleportWorld tw) => Utils.GetPrefabName(tw.gameObject) == "portal_wood";

        internal static PortalShape Of(TeleportWorld tw)
        {
            Quaternion rot = tw.transform.rotation;
            if (IsWood(tw))
            {
                Measure(tw); // only for the log: how the measurement compares with the values tuned by eye
                return new PortalShape
                {
                    Centre = tw.transform.position + rot * new Vector3(0f, ModelHeight(tw, 1.64f) + Plugin.RingCenterOffset.Value, Plugin.PaneForwardOffset.Value),
                    Width = Plugin.PaneWidth.Value,
                    Height = Plugin.PaneHeight.Value,
                };
            }
            Measured m = Measure(tw);
            float w = Plugin.OtherPaneWidth.Value > 0f ? Plugin.OtherPaneWidth.Value : m.Width;
            float h = Plugin.OtherPaneHeight.Value > 0f ? Plugin.OtherPaneHeight.Value : m.Height;
            float ch = Plugin.OtherCenterHeight.Value > 0f ? Plugin.OtherCenterHeight.Value : m.CentreHeight;
            return new PortalShape
            {
                Centre = tw.transform.position + rot * new Vector3(0f, ch, Plugin.PaneForwardOffset.Value),
                Width = w,
                Height = h,
            };
        }

        /// <summary>What was measured for this portal, to start the tuning keys from.</summary>
        internal static void MeasuredValues(TeleportWorld tw, out float width, out float height, out float centreHeight)
        {
            Measured m = Measure(tw);
            width = m.Width; height = m.Height; centreHeight = m.CentreHeight;
        }

        private static float ModelHeight(TeleportWorld tw, float fallback)
        {
            if (Plugin.RingCenterHeight.Value > 0f && IsWood(tw)) return Plugin.RingCenterHeight.Value;
            if (tw.m_model != null)
            {
                float fromBounds = Vector3.Dot(tw.m_model.bounds.center - tw.transform.position, tw.transform.up);
                if (fromBounds > 0.5f && fromBounds < 8f) return fromBounds;
            }
            return fallback;
        }

        private static Measured Measure(TeleportWorld tw)
        {
            int id = tw.GetInstanceID();
            if (Cache.TryGetValue(id, out Measured cached)) return cached;

            Vector3 up = tw.transform.up, right = tw.transform.right, basePos = tw.transform.position;
            float h0 = ModelHeight(tw, 1.64f);
            // Fallback: the wooden portal's tuned opening, scaled by how much taller this model is (its model is 3.29 m).
            float scale = Mathf.Clamp(h0 / 1.64f, 0.5f, 4f);
            var m = new Measured { Ok = false, Width = 2.7f * scale, Height = 2.8f * scale, CentreHeight = Mathf.Max(0.5f, h0 - 0.35f * scale) };

            var cols = new List<Collider>();
            foreach (var c in tw.GetComponentsInChildren<Collider>(false))
                if (c.enabled && !c.isTrigger) cols.Add(c);
            string how = "no solid colliders";
            if (cols.Count > 0)
            {
                Vector3 centre = basePos + up * h0;
                float dU = 0f, dD = 0f, dR = 0f, dL = 0f;
                bool ok = true;
                for (int pass = 0; pass < 3 && ok; pass++)
                {
                    dU = Cast(cols, centre, up);
                    dD = Cast(cols, centre, -up);
                    if (dD < 0f) dD = Vector3.Dot(centre - basePos, up); // no sill: the ground the portal stands on
                    if (dU < 0f || dD <= 0f) { ok = false; break; }
                    centre += up * ((dU - dD) * 0.5f);
                    dR = Cast(cols, centre, right);
                    dL = Cast(cols, centre, -right);
                    if (dR < 0f || dL < 0f) { ok = false; break; }
                    centre += right * ((dR - dL) * 0.5f);
                }
                float width = dR + dL, height = dU + dD;
                if (ok && width > 1.2f && width < 10f && height > 1.2f && height < 10f)
                {
                    // The rays find the colliders, and on the stone portal those sit inside the visible arch: at 0.97 of
                    // the measurement (3.62 x 3.81 m) Max asked for about a tenth more.
                    // (1.07 first, then "another 5 %, the bottom still has a gap".)
                    m = new Measured { Ok = true, Width = width * 1.125f, Height = height * 1.125f, CentreHeight = Vector3.Dot(centre - basePos, up) };
                    how = "measured from its colliders";
                }
                else how = ok ? $"measurement out of range ({width:0.00} x {height:0.00})" : "a ray found no frame";
            }
            Cache[id] = m;
            string prefab = Utils.GetPrefabName(tw.gameObject);
            if (Logged.Add(prefab))
                Plugin.Log.LogInfo($"LivePortals: opening of {prefab}: {m.Width:0.00} x {m.Height:0.00} m, centre {m.CentreHeight:0.00} m above its base ({how}; model centre at {h0:0.00} m, {cols.Count} colliders).");
            return m;
        }

        /// <summary>Distance to the nearest of the portal's own colliders along dir, or -1.</summary>
        private static float Cast(List<Collider> cols, Vector3 origin, Vector3 dir)
        {
            float best = -1f;
            var ray = new Ray(origin, dir);
            foreach (var c in cols)
                if (c != null && c.Raycast(ray, out RaycastHit hit, 12f) && (best < 0f || hit.distance < best)) best = hit.distance;
            return best;
        }
    }
}
