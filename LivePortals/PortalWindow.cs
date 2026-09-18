using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// The window on one portal. A pane over the opening shows a texture rendered every frame by a small camera
    /// that looks at the partner portal's captures: for each capture point and cube face, a background relief (one
    /// continuous depth-displaced sheet, transparent where the capture saw sky, continuing underneath near things)
    /// and a foreground relief in front of it (the near things, cut out per pixel by their texture). Underneath, as
    /// a last resort for directions no capture point saw, skirts stretched across the depth edges; and the current
    /// sky behind everything. The camera draws the sky and the skirts first, then clears depth only and draws
    /// the reliefs over them. The camera's frustum is fitted to the pane as seen from
    /// your eye (off-axis projection), its near plane lies on the pane, and its orientation is your view direction
    /// mapped through the portal into the partner's frame, so the picture has real parallax from either side. The
    /// meshes are switched on only for the instant that camera renders, so no other camera sees them.
    /// </summary>
    public class PortalWindow : MonoBehaviour
    {
        private class Relief
        {
            public GameObject Anchor;
            public Vector3 Offset; // capture point offset in the partner's frame
            public readonly List<Renderer> Renderers = new List<Renderer>(); // what the capture really saw
            public readonly List<Renderer> Under = new List<Renderer>();     // skirts, drawn first
            public readonly List<Material> Materials = new List<Material>();
            public readonly List<Material> Untinted = new List<Material>(); // the local-light layers: never tinted
            public readonly List<Texture> Textures = new List<Texture>(); // blurred copies, ours to destroy
        }

        internal static readonly List<PortalWindow> All = new List<PortalWindow>();
        /// <summary>Metres beyond the visible range at which a window exists and loads its capture (see Plugin's scan).</summary>
        internal const float PreloadMargin = 15f;
        private CaptureLoader _loader;
        private static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);
        private static Mesh _quad, _disc;
        private static bool _hiddenForCapture;
        private static bool _loggedMasks;

        private TeleportWorld _tw;
        private ZNetView _nview;
        private ZDOID _targetId = ZDOID.None;
        private CaptureSet _set;
        private long _capTime = -2;
        private float _capCheckTimer;
        private float _tintTimer;
        private bool _loggedGeometry;

        private GameObject _pane;
        private Renderer _paneRenderer;
        private Material _paneMat;
        private bool _paneSolid;
        // The picture itself: a sprite drawn over the plug in the transparent stage. See EnsureBuilt.
        private GameObject _overlay;
        private Renderer _overlayRenderer;
        private Material _overlayMat;
        private Mesh _overlayMesh;
        private int[] _overlayCells;
        private Color32[] _overlayCols;
        private float _overlayShown = -1f;
        // The game's own swirl in the ring: transparent, on the pane's plane. It is switched off while the window
        // shows, or at some angles the game draws it after the picture and the ring goes black.
        private readonly List<Renderer> _swirl = new List<Renderer>();
        private bool _swirlHidden;
        private readonly List<Relief> _reliefs = new List<Relief>();
        private Camera _cam;
        private RenderTexture _rt;
        private Light _light;

        private bool _built, _visible, _suppressed;
        private int _frame;
        private float _lastRequest = -10f;
        private float _lastGlassLog = -10f;

        /// <summary>0 = the nearest window; others render less often.</summary>
        internal int Rank;
        /// <summary>Set by Update when this window would like a redraw; the scheduler in Plugin.LateUpdate grants the nearest few.</summary>
        internal bool WantsRender;
        internal float EyeDist => _eyeDist;
        private Vector3 _lastEye = new Vector3(float.NaN, 0f, 0f);
        private float _lastAlpha = -1f, _lastRenderTime = -10f, _eyeDist, _alphaNow;
        private int _loadedPoints;
        private float _paneW = 2.7f;
        private static readonly Plane[] _planes = new Plane[6];
        internal static int PerfRenders;
        internal static double PerfMs;
        private float _reloadTimer;
        // What RenderNow needs from the last Update.
        private Vector3 _pe, _anchor0;
        private Quaternion _rB;
        private ZDOID _target;
        private float _near, _far, _l, _r, _b, _t;
        private bool _glass, _realFront;
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private double _msAccum; private int _renders, _skips; private float _perfLogTime;

        // ------------------------------------------------------------------
        internal static void NotifyCaptureUpdated(ZDOID id)
        {
            foreach (var w in All) if (w._targetId == id) w._capCheckTimer = 0f;
        }

        /// <summary>Captures must not see other windows (or their spill light).</summary>
        internal static void SetAllVisible(bool on)
        {
            _hiddenForCapture = !on;
            foreach (var w in All) w.ApplyVisibility();
        }

        internal void SetSuppressed(bool s)
        {
            _suppressed = s;
            if (s) Hide();
        }

        private void Awake()
        {
            _tw = GetComponent<TeleportWorld>();
            _nview = GetComponent<ZNetView>();
            All.Add(this);
        }

        private void OnDestroy()
        {
            All.Remove(this);
            Cleanup();
        }

        private void OnDisable() { Hide(); }

        // ------------------------------------------------------------------
        private void Update()
        {
            if (!Plugin.Enabled.Value || _suppressed || _tw == null || _nview == null || !_nview.IsValid()) { Hide(); return; }
            var player = Player.m_localPlayer;
            var gc = GameCamera.instance;
            if (player == null || gc == null || gc.m_camera == null) { Hide(); return; }

            float dist = Vector3.Distance(player.transform.position, transform.position);
            float act = Mathf.Max(0.5f, _tw.m_activationRange);
            float range = act * Plugin.RangeMultiplier.Value;
            float full = act * Plugin.FullMultiplier.Value;
            if (dist > range + 3f + PreloadMargin) { Destroy(this); return; } // walked away; the scan re-adds us when we come back
            float alpha = range <= full ? (dist <= range ? 1f : 0f) : Mathf.Clamp01((range - dist) / (range - full));
            alpha = 1f - (1f - alpha) * (1f - alpha); // ease in: half visible a third of the way in

            bool glass = Plugin.GlassTest.Value;
            ZDOID target = glass ? _nview.GetZDO().m_uid : _nview.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (target == ZDOID.None) { Hide(); return; }
            ZDO tz = ZDOMan.instance.GetZDO(target);
            if (tz == null)
            {
                if (Time.time - _lastRequest > 1f) { _lastRequest = Time.time; ZDOMan.instance.RequestZDO(target); Plugin.Dbg("waiting for partner ZDO " + Storage.Key(target)); }
                Hide(); return;
            }
            if (target != _targetId) { _targetId = target; ReleaseCapture(); _capCheckTimer = 0f; }

            _capCheckTimer -= Time.deltaTime;
            if (_capCheckTimer <= 0f)
            {
                _capCheckTimer = 2f;
                long stored = Storage.StoredTime(target);
                if (stored != _capTime)
                {
                    ReleaseCapture();
                    _capTime = stored;
                    if (stored >= 0)
                    {
                        EnsureBuilt(gc); // first: it decides (WindowMaterial) how the capture has to be loaded
                        bool wantAll = dist <= Plugin.SecondaryViewpointRange.Value + 6f;
                        _loader = CaptureLoader.Start(target, !WindowMaterial.DepthWorks, 0, wantAll ? int.MaxValue : 1);
                    }
                }
            }
            if (_loader != null)
            {
                _loader.Step(); // a few textures and meshes per frame, off a worker thread's decoding
                if (_loader.Done)
                {
                    var got = _loader.Result;
                    int from = _loader.FromPoint;
                    _loader = null;
                    if (got != null && got.Captures.Count > 0)
                    {
                        if (_set == null || from == 0) { DestroyReliefs(); _set?.Destroy(); _set = got; }
                        else _set.Append(got);
                        _loadedPoints = _set.Captures.Count;
                        BuildReliefs();
                        Plugin.Dbg("window at " + Storage.Key(_nview.GetZDO().m_uid) + " shows capture " + Storage.Key(target) + " (" + _set.Captures.Count + " of " + _set.AvailablePoints + " points)");
                    }
                    else got?.Destroy();
                }
            }
            // Memory: a far window keeps only its primary viewpoint (a third of the textures); the others load when
            // you come within reach of them and go again when you leave, with a gap so it does not flap.
            _reloadTimer -= Time.deltaTime;
            if (_set != null && _loader == null && _reloadTimer <= 0f)
            {
                _reloadTimer = 2f;
                float reach = Plugin.SecondaryViewpointRange.Value;
                bool wantAll = dist <= reach + 6f, wantOne = dist > reach + 14f;
                if (wantAll && _loadedPoints < _set.AvailablePoints) _loader = CaptureLoader.Start(target, !WindowMaterial.DepthWorks, _loadedPoints, int.MaxValue);
                else if (wantOne && _loadedPoints > 1) { _set.Trim(1); _loadedPoints = 1; BuildReliefs(); }
            }
            if (alpha <= 0.001f || _set == null) { Hide(); return; }
            EnsureBuilt(gc);

            // ---- Pane geometry: the ring centre sits above the portal's base along its own up axis ----
            Quaternion rA = transform.rotation;
            Quaternion rB = tz.GetRotation();
            Vector3 up = rA * Vector3.up, n = rA * Vector3.forward, right = rA * Vector3.right;
            var shape = PortalShape.Of(_tw);
            float w = shape.Width, h = shape.Height;
            _paneW = w;
            Vector3 c = shape.Centre;
            _pane.transform.SetPositionAndRotation(c, rA);
            if (!_loggedGeometry)
            {
                _loggedGeometry = true;
                var mb = _tw.m_model != null ? _tw.m_model.bounds : new Bounds(transform.position, Vector3.zero);
                Plugin.Log.LogInfo($"LivePortals: portal {name} pos {transform.position} fwd {n} up {up} model bounds centre {mb.center} size {mb.size}; pane centre {c}");
            }

            // The rest depends on where the eye is, and the game moves its camera in LateUpdate: done in Refresh,
            // which the scheduler calls just before the game camera renders, so the window never trails the view.
            _gC = c; _gN = n; _gUp = up; _gRight = right; _gW = w; _gH = h; _gRA = rA; _gRB = rB; _gGlass = glass; _gTarget = target; _gAlpha = alpha;
            _poseReady = true;
        }

        private bool _poseReady;
        private Vector3 _gC, _gN, _gUp, _gRight;
        private float _gW, _gH, _gAlpha;
        private Quaternion _gRA, _gRB;
        private bool _gGlass;
        private ZDOID _gTarget;
        internal float Staleness => Time.time - _lastRenderTime;

        /// <summary>
        /// The eye-dependent half of the frame, from the game camera's final position for this frame: the off-axis
        /// frustum, where the reliefs stand, which way the pane faces, the tint, and whether a redraw is wanted.
        /// Up to 0.9.4 this ran in Update, before the game had moved its camera: the window showed the view for
        /// the previous frame's eye, a small but visible lag behind the frame around it.
        /// </summary>
        internal void Refresh(Camera main)
        {
            if (!_poseReady || _cam == null || _set == null || _pane == null || main == null) return;
            Vector3 c = _gC, n = _gN, up = _gUp, right = _gRight;
            float w = _gW, h = _gH, alpha = _gAlpha;
            Quaternion rA = _gRA, rB = _gRB;
            bool glass = _gGlass;
            ZDOID target = _gTarget;
            // ---- Off-axis frustum from the eye through the pane (Kooima's generalized perspective) ----
            Vector3 pe = main.transform.position;
            bool realFront = Vector3.Dot(pe - c, n) >= 0f;
            bool front = realFront;
            if (!realFront && Plugin.ArrivalViewBothSides.Value && !glass)
            {
                // The game always drops you at the partner's front, so from behind show the same front view,
                // computed for the eye mirrored through the pane (which keeps the parallax honest).
                pe = pe - 2f * Vector3.Dot(pe - c, n) * n;
                front = true;
            }
            Vector3 hr = right * (w * 0.5f), hu = up * (h * 0.5f);
            Vector3 pa, pb, pc; // lower-left, lower-right, upper-left as the viewer sees them
            if (front) { pa = c + hr - hu; pb = c - hr - hu; pc = c + hr + hu; }
            else { pa = c - hr - hu; pb = c + hr - hu; pc = c - hr + hu; }
            Vector3 vr = (pb - pa).normalized, vu = (pc - pa).normalized, vn = Vector3.Cross(vr, vu).normalized;
            Vector3 va = pa - pe, vb = pb - pe, vc = pc - pe;
            float d = Vector3.Dot(va, vn);
            _eyeDist = (pe - c).magnitude;
            if (d < 0.03f) { Hide(); return; } // eye in the plane of the pane
            // Near plane on the pane itself: the reliefs are full spheres around the far ring, and anything on the
            // far portal's near side maps to the space between the eye and the pane, where a real hole shows nothing.
            float near = Mathf.Max(0.02f, d - 0.01f), far = _cam.farClipPlane;
            float l = Vector3.Dot(vr, va) * near / d, r = Vector3.Dot(vr, vb) * near / d;
            float b = Vector3.Dot(vu, va) * near / d, t = Vector3.Dot(vu, vc) * near / d;

            // ---- Map through the portal: A's frame -> turned round -> B's frame ----
            // A point p near this portal maps to relief-space as anchor + map * (p - c), where each relief's origin
            // is its capture point. Keep the window camera on the real camera so the sky and clouds around it are
            // right, and put the reliefs where that makes the far ring coincide with c.
            Quaternion map = glass ? Quaternion.identity : rB * Flip * Quaternion.Inverse(rA);
            if (glass) rB = rA;
            Vector3 anchor0 = pe - map * (pe - c);
            const float ds = 1f; // the relief is metric; the DepthScale knob of the ray-depth days only ever made it wrong
            for (int k = 0; k < _reliefs.Count; k++)
            {
                var rl = _reliefs[k];
                // Secondary points sit a touch farther out so the primary wins where both saw the same surface.
                float s = ds * (1f + 0.01f * k);
                // A relief's origin is its capture point, and those sit 0.15 m in front of the far ring's centre.
                rl.Anchor.transform.SetPositionAndRotation(anchor0 + rB * ((rl.Offset + CaptureForward) * ds), rB);
                rl.Anchor.transform.localScale = Vector3.one * s;
            }
            _cam.transform.SetPositionAndRotation(pe, map * Quaternion.LookRotation(vn, vu));
            _cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, near, far);
            if (glass && Time.time - _lastGlassLog > 1f)
            {
                _lastGlassLog = Time.time;
                Plugin.Log.LogInfo($"LivePortals glass: eye {pe} pane {c} n {n} front {realFront} d {d:0.00} l {l:0.000} r {r:0.000} b {b:0.000} t {t:0.000} camFwd {_cam.transform.forward} camRight {_cam.transform.right} mainFwd {main.transform.forward}");
            }

            // The picture's u runs from pa, the viewer's left; the mesh has u=0 at -x, which is the viewer's left
            // only from behind. From the front, mirror the mesh (a negative x scale: the disc is symmetric, only
            // its UVs flip). Sprite shaders ignore texture scale/offset, so it has to be done on the geometry.
            _pane.transform.localScale = new Vector3(front ? -w : w, h, 1f);
            // The plug and the picture dissolve in as the same blocks (see Dissolve): the picture is cut per cell,
            // never half transparent.
            if (_paneSolid) WindowMaterial.SetPaneVisible(_paneMat, alpha);
            else _paneMat.color = new Color(1f, 1f, 1f, alpha);
            if (_overlay != null)
            {
                // A little toward the viewer, so it never fights the plug for the same depth.
                _overlay.transform.localPosition = new Vector3(0f, 0f, realFront ? 0.03f : -0.03f);
                float shown = Dissolve.Reveal(alpha);
                if (Mathf.Abs(shown - _overlayShown) > 0.002f)
                {
                    _overlayShown = shown;
                    Dissolve.Apply(_overlayMesh, _overlayCells, _overlayCols, alpha);
                }
            }
            SetSwirlHidden(alpha >= 0.5f && Plugin.HideSwirl.Value);

            // ---- Lighting: tint the captures to now, and spill light onto the viewer's side ----
            _tintTimer -= Time.deltaTime;
            if (_tintTimer <= 0f)
            {
                _tintTimer = 0.25f;
                var primary = _set.Primary;
                Color tint = Lighting.Tint(primary, Plugin.ToneMatch.Value);
                foreach (var rl in _reliefs)
                    foreach (var m in rl.Materials) WindowMaterial.SetTint(m, tint);
                UpdateLight(primary, c, realFront ? n : -n, tint, alpha);
            }

            _visible = true;
            ApplyVisibility();
            _pe = pe; _anchor0 = anchor0; _rB = rB; _target = target; _near = near; _far = far; _l = l; _r = r; _b = b; _t = t; _glass = glass; _realFront = realFront; _alphaNow = alpha;
            bool wantDump = _dumped != DumpRequest;
            WantsRender = !_hiddenForCapture && ShouldRender(main, pe, alpha, wantDump);
        }

        /// <summary>Redraw the window now, with the geometry of the last Update. Called by the scheduler.</summary>
        internal void RenderNow()
        {
            WantsRender = false;
            if (_cam == null || _set == null || _rt == null) return;
            Vector3 pe = _pe, anchor0 = _anchor0; Quaternion rB = _rB; ZDOID target = _target;
            float near = _near, far = _far, l = _l, r = _r, b = _b, t = _t; bool glass = _glass, realFront = _realFront;
            _lastEye = pe; _lastAlpha = _alphaNow; _lastRenderTime = Time.time;
            var gc = GameCamera.instance;
            UpdateResolutionTier(gc != null ? gc.m_camera : null);
            _sw.Restart();
            bool dump = _dumped != DumpRequest;
            // Two passes, as up to 0.9.3. Pass one: the sky, the far side's grass, then the guesses (skirts and far
            // shell), opaque and depth-tested among themselves so the nearest wins. Pass two keeps that picture,
            // clears depth only, and draws the reliefs over it, so a skirt (which spans all the depth between two
            // surfaces) never hides them. (0.9.4 to 0.9.15 drew the guesses blended in one pass without depth:
            // whichever skirt drew last showed, and rocks and chests stretched out across the picture.) The
            // capture meshes exist only while this camera renders: no other camera ever sees them.
            // No scene fog: the captures carry the far side's own fog, and the unlit shader would add the viewer's
            // on top. No shadow maps: nothing here receives them, and the cascades are the dearest part of a pass.
            bool fog = RenderSettings.fog;
            float shadows = QualitySettings.shadowDistance;
            var clear = _cam.clearFlags;
            int mask = _cam.cullingMask;
            RenderSettings.fog = false;
            QualitySettings.shadowDistance = 0f;
            try
            {
                if (_eyeDist <= Plugin.SecondaryViewpointRange.Value + 4f) _set.Grass?.Draw(_cam, Matrix4x4.TRS(anchor0, rB, Vector3.one));
                SetReliefsEnabled(true, true);
                _cam.Render();
                SetReliefsEnabled(true, false);
                _cam.clearFlags = CameraClearFlags.Depth;
                _cam.cullingMask = 1 << Plugin.FaceLayer;
                SetReliefsEnabled(false, true);
                _cam.Render();
            }
            finally
            {
                SetReliefsEnabled(false, false);
                SetReliefsEnabled(true, false);
                _cam.clearFlags = clear;
                _cam.cullingMask = mask;
                RenderSettings.fog = fog;
                QualitySettings.shadowDistance = shadows;
            }
            _sw.Stop();
            _msAccum += _sw.Elapsed.TotalMilliseconds; _renders++;
            PerfMs += _sw.Elapsed.TotalMilliseconds; PerfRenders++;
            if (dump)
            {
                _dumped = DumpRequest;
                SaveWindow("final");
                // The eye in the primary relief's own frame: what tools/LayerTest needs (EYES=x,y,z) to redraw this view.
                Vector3 eyeLocal = Quaternion.Inverse(rB) * (pe - (anchor0 + rB * CaptureForward));
                Plugin.Log.LogInfo($"LivePortals dump: window {Storage.Key(_nview.GetZDO().m_uid)} shows {Storage.Key(target)} ({_reliefs.Count} viewpoints), glass {glass}, front {realFront}, EYES={eyeLocal.x:0.###},{eyeLocal.y:0.###},{eyeLocal.z:0.###} near {near:0.###} far {far:0} frustum l {l:0.####} r {r:0.####} b {b:0.####} t {t:0.####} hdr {_cam.allowHDR} path {_cam.actualRenderingPath} depthBits {_rt.depth} format {_rt.format} material {WindowMaterial.Summary} pane {(_paneSolid ? "plug+sprite" : "sprite")} tint {Lighting.Tint(_set.Primary, Plugin.ToneMatch.Value)}");
            }
        }

        private void SaveWindow(string tag)
        {
            try
            {
                string dir = Storage.DebugDir();
                Directory.CreateDirectory(dir);
                // Through an 8-bit sRGB texture, so the PNG holds display values. (0.8.7 and 0.8.8 read the half-float
                // window texture directly and saved its linear values: dumps that looked dark and deep orange.)
                var srgb = RenderTexture.GetTemporary(_rt.width, _rt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(_rt, srgb);
                var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
                var prev = RenderTexture.active;
                RenderTexture.active = srgb;
                tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(srgb);
                File.WriteAllBytes(Path.Combine(dir, $"{System.DateTime.Now:HHmmss}_{Storage.Key(_nview.GetZDO().m_uid)}_{tag}.png"), tex.EncodeToPNG());
                Destroy(tex);
                if (tag == "final" && _rt.format == RenderTextureFormat.ARGBHalf)
                {
                    // The pane blends by this texture's alpha, and a float texture does not clamp it.
                    var raw = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBAHalf, false, true);
                    RenderTexture.active = _rt;
                    raw.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
                    RenderTexture.active = prev;
                    var px = raw.GetPixels();
                    float aMin = float.MaxValue, aMax = float.MinValue, cMax = 0f;
                    for (int i = 0; i < px.Length; i += 5)
                    {
                        if (px[i].a < aMin) aMin = px[i].a;
                        if (px[i].a > aMax) aMax = px[i].a;
                        cMax = Mathf.Max(cMax, px[i].maxColorComponent);
                    }
                    Plugin.Log.LogInfo($"LivePortals dump: window texture alpha {aMin:0.###}..{aMax:0.###}, brightest channel {cMax:0.###}");
                    Destroy(raw);
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("LivePortals: window dump failed: " + e.Message); }
        }

        /// <summary>Numpad 5 bumps this; every visible window then saves what it drew and logs where the eye was.</summary>
        internal static int DumpRequest;
        private int _dumped;
        internal static readonly Vector3 CaptureForward = new Vector3(0f, 0f, 0.15f);

        /// <summary>
        /// The window texture needs only as many texels as the pane covers on screen: a far window is drawn into a
        /// small one. Steps of about 1.4x with some hysteresis, so the texture is not recreated while the eye hovers
        /// on a boundary.
        /// </summary>
        private void UpdateResolutionTier(Camera main)
        {
            if (_rt == null) return;
            int max = Mathf.Max(128, Plugin.WindowResolution.Value);
            float fov = main != null ? main.fieldOfView : 65f;
            float px = Screen.height * _paneW / Mathf.Max(0.5f, 2f * _eyeDist * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad));
            int want = max;
            while (want > 128 && want / 1.4f >= px) want = Mathf.Max(128, (Mathf.RoundToInt(want / 1.4f) + 15) / 16 * 16);
            int cur = _rt.width;
            if (want == cur) return;
            if (want > cur ? px < cur * 1.08f : px > want * 0.92f) return;
            _rt.Release();
            _rt.width = want; _rt.height = want;
            _rt.Create();
            _cam.targetTexture = _rt;
            if (_overlayMat != null) _overlayMat.mainTexture = _rt;
            if (_overlay == null) WindowMaterial.SetPaneTexture(_paneMat, _rt);
        }

        private void SetReliefsEnabled(bool under, bool on)
        {
            // Secondary viewpoints only matter close up, where you look around near things; from farther away the
            // primary alone is indistinguishable and a third of the geometry.
            bool secondaries = _eyeDist <= Plugin.SecondaryViewpointRange.Value;
            for (int k = 0; k < _reliefs.Count; k++)
            {
                bool use = on && (k == 0 || secondaries);
                var rl = _reliefs[k];
                foreach (var r in under ? rl.Under : rl.Renderers) r.enabled = use;
            }
        }

        /// <summary>
        /// Whether to redraw the window this frame. It is skipped when the pane is outside the game camera's view,
        /// on frames a lower-ranked window sits out, and, unless RenderWhenStill, while the eye has not moved and the
        /// dissolve has not changed (the picture depends on the eye position, not on where you look; only the sky
        /// changes on its own, so a still window still refreshes twice a second).
        /// </summary>
        private bool ShouldRender(Camera main, Vector3 pe, float alpha, bool dump)
        {
            if (dump) return true;
            int stride = Mathf.Max(1, Plugin.RenderEveryNFrames.Value);
            if (stride > 1 && (_frame++ % stride) != 0) { _skips++; return false; }
            if (_paneRenderer != null)
            {
                GeometryUtility.CalculateFrustumPlanes(main, _planes);
                if (!GeometryUtility.TestPlanesAABB(_planes, _paneRenderer.bounds)) { _skips++; return false; }
            }
            float since = Time.time - _lastRenderTime;
            if (!Plugin.RenderWhenStill.Value)
            {
                // A new picture once the eye has moved by about a pixel's worth: 0.7 mm per metre of distance.
                // (0.9.2 to 0.9.4 used 5 mm per metre, which is five pixels: the far side moved in visible steps.)
                float thr = Mathf.Max(0.003f, _eyeDist * (Rank == 0 ? 0.0007f : 0.002f));
                bool moved = float.IsNaN(_lastEye.x) || (pe - _lastEye).sqrMagnitude > thr * thr || Mathf.Abs(alpha - _lastAlpha) > 0.002f;
                if (!moved && since < 0.5f) { _skips++; return false; }
            }
            // Cadence: the nearest window follows the eye every frame (a redraw is about 2 ms); the others thirty
            // times a second, taking turns for the rest of the frame's budget.
            float interval = Rank == 0 ? 0f : 1f / 30f;
            if (since < interval) { _skips++; return false; }
            if (Plugin.PerfLog.Value && Time.time - _perfLogTime > 10f)
            {
                if (_renders > 0)
                    Plugin.Log.LogInfo($"LivePortals perf: window {name} rank {Rank}: {_renders} renders / {_skips} skipped in {Time.time - _perfLogTime:0}s, avg {_msAccum / _renders:0.0} ms per render ({_msAccum / Mathf.Max(0.1f, Time.time - _perfLogTime):0.0} ms per second), {_reliefs.Count} viewpoints, eye {_eyeDist:0.0} m");
                _perfLogTime = Time.time; _msAccum = 0; _renders = 0; _skips = 0;
            }
            return true;
        }

        private void UpdateLight(PortalCapture cap, Vector3 c, Vector3 outward, Color tint, float alpha)
        {
            if (_light == null || cap == null) return;
            float strength = Plugin.PortalLight.Value;
            if (strength <= 0f) { _light.enabled = false; return; }
            Lighting.Sample(out var sun, out var amb, out _, out _);
            float here = Mathf.Clamp01(Lighting.Level(sun, amb));
            // The sunlit part of the far side follows the tint; its torchlight does not.
            float local = Mathf.Clamp(cap.AverageLocalLuminance, 0f, cap.AverageLuminance);
            float there = Mathf.Clamp01(((cap.AverageLuminance - local) * Lighting.Luminance(tint) + local) * 1.6f);
            float intensity = strength * 3f * Mathf.Clamp01(there - here * 0.8f) * alpha;
            _light.transform.position = c + outward * 0.4f;
            _light.transform.rotation = Quaternion.LookRotation(outward, Vector3.up);
            _light.range = Plugin.PortalLightRange.Value;
            Color col = cap.AverageColor * tint;
            float lum = Mathf.Max(0.05f, Lighting.Luminance(col));
            col = Color.Lerp(Color.white, col / lum, 0.6f);
            col.a = 1f;
            _light.color = col;
            _light.intensity = intensity;
            _light.enabled = intensity > 0.02f && _visible && !_hiddenForCapture;
        }

        // ------------------------------------------------------------------
        private void EnsureBuilt(GameCamera gc)
        {
            if (_built) return;
            _built = true;
            if (_quad == null) _quad = MakeQuad();
            if (_disc == null) _disc = MakeDisc(64);
            int res = Plugin.WindowResolution.Value;
            // 8-bit sRGB. 0.8.7 to 0.8.9 used a half-float texture to keep the sky's over-bright blue from clipping
            // (the sky through a window looks pinker than the real one); in game the whole window then came out dark
            // and deep orange, as if its linear values were shown unconverted. Why is not understood; this format is
            // the one Max confirmed looks right (0.8.5). The pinker sky is still open.
            var format = RenderTextureFormat.ARGB32;
            _rt = new RenderTexture(res, res, 24, format) { name = "LivePortals_Window" };
            _rt.Create();

            // Pane over the opening: a free object in world space (not parented, so the prefab's scale cannot touch it).
            // A disc by default, so the hole is the ring's shape; the texture still maps as if it were the full square.
            _pane = new GameObject("LivePortals_Pane");
            _pane.AddComponent<MeshFilter>().sharedMesh = Plugin.PaneRound.Value ? _disc : _quad;
            _paneRenderer = _pane.AddComponent<MeshRenderer>();
            _paneRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _paneRenderer.receiveShadows = false;
            _paneRenderer.enabled = false;

            // Window camera: draws the current sky like the game's sky camera, then the capture meshes.
            var camGo = new GameObject("LivePortals_WindowCamera");
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false;
            var sky = gc.m_skyCamera;
            int skyOnly = sky != null ? (sky.cullingMask & ~gc.m_camera.cullingMask) : 0;
            if (skyOnly == 0)
            {
                int lyr = LayerMask.NameToLayer("skybox");
                if (lyr >= 0) skyOnly = 1 << lyr;
            }
            if (Plugin.LiveSky.Value && sky != null && skyOnly != 0)
            {
                _cam.CopyFrom(sky);
                _cam.cullingMask = skyOnly | (1 << Plugin.FaceLayer);
            }
            else
            {
                _cam.CopyFrom(gc.m_camera);
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = RenderSettings.fogColor;
                _cam.cullingMask = 1 << Plugin.FaceLayer;
            }
            if (!_loggedMasks)
            {
                _loggedMasks = true;
                Plugin.Log.LogInfo($"LivePortals: main mask {gc.m_camera.cullingMask:X8}, sky mask {(sky != null ? sky.cullingMask.ToString("X8") : "none")}, sky-only {skyOnly:X8}, window mask {_cam.cullingMask:X8}, face layer {Plugin.FaceLayer}, sky clear {(sky != null ? sky.clearFlags.ToString() : "n/a")}");
            }
            _cam.enabled = false;
            _cam.ResetWorldToCameraMatrix();
            _cam.ResetProjectionMatrix();
            _cam.ResetAspect();
            _cam.usePhysicalProperties = false;
            _cam.allowHDR = false;  // straight into the window texture, so the second pass finds the first one's picture there
            _cam.allowMSAA = false;
            _cam.depthTextureMode = DepthTextureMode.None;
            _cam.nearClipPlane = 0.05f;
            float need = Plugin.DepthRange.Value * 3f; // the ground shell sits at 1.5 ranges, its corners farther
            if (_cam.farClipPlane < need) _cam.farClipPlane = need;
            _cam.targetTexture = _rt;
            _cam.rect = new Rect(0f, 0f, 1f, 1f);
            _cam.orthographic = false;
            WindowMaterial.EnsureTested(_cam);
            WindowMaterial.ApplyTo(_cam);

            // The pane is two things. A black, depth-writing plug in the ring (the tested relief shader, dissolving
            // in by its cut-off), so that everything in the game that works from depth (the fog, the mist, depth
            // of field) sees a surface at the portal and not the hill behind it. And the picture, a plain sprite
            // drawn over the plug in the transparent stage, after the game's opaque screen effects have run:
            // 0.8.12 to 0.9.9 showed the picture on the plug itself, as emission, and at night the ambient
            // occlusion pass darkened it to nothing (a mirrored disc hands it inside-out normals). Without a
            // tested shader there is no plug and the sprite stands alone, as before 0.8.12.
            bool legacy = Plugin.PaneStyle.Value == PaneStyleOption.Emissive;
            _paneMat = legacy ? WindowMaterial.MakePane(_rt) : WindowMaterial.MakePlug();
            _paneSolid = _paneMat != null;
            if (!_paneSolid)
            {
                _paneMat = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
                _paneMat.mainTexture = _rt;
            }
            _paneRenderer.sharedMaterial = _paneMat;
            if (_paneSolid && !legacy)
            {
                _overlay = new GameObject("LivePortals_Picture");
                _overlay.transform.SetParent(_pane.transform, false);
                _overlayMesh = Dissolve.MakeMesh(Plugin.PaneRound.Value, out _overlayCells);
                _overlayCols = new Color32[_overlayCells.Length * 4];
                _overlayShown = -1f;
                _overlay.AddComponent<MeshFilter>().sharedMesh = _overlayMesh;
                _overlayRenderer = _overlay.AddComponent<MeshRenderer>();
                _overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _overlayRenderer.receiveShadows = false;
                _overlayRenderer.enabled = false;
                _overlayMat = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
                _overlayMat.mainTexture = _rt;
                _overlayRenderer.sharedMaterial = _overlayMat;
            }

            var lightGo = new GameObject("LivePortals_Light");
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Spot;
            _light.spotAngle = 150f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        /// <summary>One relief per capture point: per face a background sheet, the foreground in front of it, and the skirts.</summary>
        private void BuildReliefs()
        {
            DestroyReliefs();
            if (_set == null) return;
            for (int k = 0; k < _set.Captures.Count; k++)
            {
                // Without a depth-testing material only draw order separates near from far, and overlapping
                // viewpoints cannot be sorted: the primary one alone, background then foreground.
                if (k > 0 && !WindowMaterial.DepthWorks) break;
                var cap = _set.Captures[k];
                var rl = new Relief { Offset = _set.Offsets[k] };
                rl.Anchor = new GameObject("LivePortals_Relief" + k);
                for (int i = 0; i < 6; i++)
                {
                    if (cap.Grids[i] == null || cap.Faces[i] == null) continue;
                    var f = new GameObject("Face" + i);
                    f.transform.SetParent(rl.Anchor.transform, false);
                    f.transform.localRotation = Capture.FaceRotations[i];
                    // The background sheet is drawn twice: the pixels the capture saw where they are, and the
                    // whole sheet, filled-in pixels included, a little farther out (see Layers.FilledAlpha).
                    Mesh back = cap.Back[i];
                    // Draw order, should depth testing ever fail again: later viewpoints first, the primary last; within
                    // a viewpoint the guesses, then what it saw, then its foreground.
                    int order = (_set.Captures.Count - 1 - k) * 3;
                    AddLayer(rl, f.transform, "Back", back, cap.Faces[i], false, Layers.CutoffCaptured, order + 1);
                    if (WindowMaterial.DepthWorks)
                    {
                        var filled = AddLayer(rl, f.transform, "BackFilled", back, cap.Faces[i], false, Layers.CutoffAll, order);
                        if (filled != null) filled.localScale = Vector3.one * Layers.FilledPush;
                    }
                    // Torchlight, firelight and glowing things: added on top of the background, never tinted, so
                    // they do not fade with the sun the way the rest of the picture does at night.
                    if (k == 0 && cap.Locals[i] != null && WindowMaterial.AdditiveWorks)
                        AddLayer(rl, f.transform, "Glow", back, WindowMaterial.MakeAdditive(cap.Locals[i]), false, rl.Untinted);
                    // The guesses (skirts, shell) show a blurred copy of the picture: one row of texels stretched
                    // over a skirt reads as a fan of streaks, the same colours blurred read as haze.
                    // (0.8.6 to 0.8.10 used a blurred copy here. It bled the colours of near things into the fill and
                    // showed its coarse texels as a grid; dark, blade-shaped fill around grass was the result.)
                    Texture soft = cap.Faces[i];
                    AddLayer(rl, f.transform, "Skirt", cap.Skirt[i], soft, true, Layers.CutoffAll, 5);
                    // The far shell, all around: whatever was at least ShellMinDepth away, by direction alone. It is
                    // what shows wherever no relief covers a view ray. (Near things are left out of it: by direction
                    // alone they would land in the wrong place, as copies against the sky.)
                    if (k == 0) AddLayer(rl, f.transform, "Shell", cap.Shell[i], soft, true, Layers.CutoffAll, 0);
                    // Foreground from the primary viewpoint only. The others exist to fill in background the primary
                    // could not see; their own cut-outs of the same grass, leaves and posts, a few centimetres off,
                    // only turn thin things into a jumble of shards.
                    if (cap.Fronts[i] != null && k == 0)
                    {
                        AddLayer(rl, f.transform, "Front", cap.Front[i], cap.Fronts[i], false, 0.5f, order + 2);
                        if (cap.LocalsFront[i] != null && WindowMaterial.AdditiveWorks)
                            AddLayer(rl, f.transform, "GlowFront", cap.Front[i], WindowMaterial.MakeAdditive(cap.LocalsFront[i]), false, rl.Untinted);
                    }
                }
                _reliefs.Add(rl);
            }
            _tintTimer = 0f;
        }

        /// <summary>The texture's fourth mip level as a texture of its own (a GPU copy; the source need not be readable).</summary>
        private static Texture Blurred(Texture2D tex, Relief rl)
        {
            const int mip = 4;
            try
            {
                if (tex.mipmapCount <= mip) return tex;
                int w = Mathf.Max(1, tex.width >> mip), h = Mathf.Max(1, tex.height >> mip);
                var small = new Texture2D(w, h, tex.format, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                Graphics.CopyTexture(tex, 0, mip, small, 0, 0);
                rl.Textures.Add(small);
                return small;
            }
            catch (System.Exception e)
            {
                Plugin.Dbg("no blurred copy: " + e.Message);
                return tex;
            }
        }

        private static Transform AddLayer(Relief rl, Transform parent, string name, Mesh mesh, Texture tex, bool under, float cutoff, int order)
        {
            if (mesh == null) return null;
            var mat = WindowMaterial.Make(tex, cutoff, order);
            return AddLayer(rl, parent, name, mesh, mat, under, rl.Materials);
        }

        private static Transform AddLayer(Relief rl, Transform parent, string name, Mesh mesh, Material mat, bool under, List<Material> owner)
        {
            if (mesh == null) { Destroy(mat); return null; }
            var go = new GameObject(name);
            go.layer = Plugin.FaceLayer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false;
            (under ? rl.Under : rl.Renderers).Add(mr);
            owner.Add(mat);
            return go.transform;
        }

        private void DestroyReliefs()
        {
            foreach (var rl in _reliefs)
            {
                foreach (var m in rl.Materials) Destroy(m);
                foreach (var m in rl.Untinted) Destroy(m);
                foreach (var t in rl.Textures) Destroy(t);
                if (rl.Anchor != null) Destroy(rl.Anchor);
            }
            _reliefs.Clear();
        }

        private void ReleaseCapture()
        {
            if (_loader != null) { _loader.Cancel(); _loader = null; }
            DestroyReliefs();
            if (_set != null) { _set.Destroy(); _set = null; }
        }

        private void Hide()
        {
            SetSwirlHidden(false);
            _visible = false;
            _poseReady = false;
            WantsRender = false;
            ApplyVisibility();
        }

        private void SetSwirlHidden(bool hide)
        {
            if (hide == _swirlHidden) return;
            _swirlHidden = hide;
            if (hide)
            {
                _swirl.Clear();
                if (_tw != null && _tw.m_target_found != null)
                    foreach (var r in _tw.m_target_found.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; _swirl.Add(r); }
            }
            else
            {
                foreach (var r in _swirl) if (r != null) r.enabled = true;
                _swirl.Clear();
            }
        }

        private void ApplyVisibility()
        {
            bool on = _visible && !_hiddenForCapture;
            if (_paneRenderer != null) _paneRenderer.enabled = on;
            if (_overlayRenderer != null) _overlayRenderer.enabled = on;
            if (_light != null && !on) _light.enabled = false;
        }

        private void Cleanup()
        {
            SetSwirlHidden(false);
            ReleaseCapture();
            if (_pane != null) Destroy(_pane);
            if (_cam != null) Destroy(_cam.gameObject);
            if (_light != null) Destroy(_light.gameObject);
            if (_paneMat != null) Destroy(_paneMat);
            if (_overlayMat != null) Destroy(_overlayMat);
            if (_overlayMesh != null) Destroy(_overlayMesh);
            _overlay = null; _overlayRenderer = null; _overlayMat = null; _overlayMesh = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            _built = false;
        }

        private static Shader FindShader(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            Plugin.Log.LogWarning("LivePortals: none of the expected shaders found; windows will be blank.");
            return Shader.Find("Standard");
        }

        /// <summary>Unit disc in XY (diameter 1), same UV convention as the quad: u = x + 0.5, v = y + 0.5.</summary>
        private static Mesh MakeDisc(int segments)
        {
            var m = new Mesh { name = "LivePortals_Disc" };
            var verts = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var cols = new Color[segments + 1];
            verts[0] = Vector3.zero; uvs[0] = new Vector2(0.5f, 0.5f); cols[0] = Color.white;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                float x = 0.5f * Mathf.Cos(a), y = 0.5f * Mathf.Sin(a);
                verts[i + 1] = new Vector3(x, y, 0f);
                uvs[i + 1] = new Vector2(x + 0.5f, y + 0.5f);
                cols[i + 1] = Color.white;
            }
            var tris = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                int a = i + 1, b = (i + 1) % segments + 1;
                tris[i * 6] = 0; tris[i * 6 + 1] = b; tris[i * 6 + 2] = a;
                tris[i * 6 + 3] = 0; tris[i * 6 + 4] = a; tris[i * 6 + 5] = b;
            }
            m.vertices = verts; m.uv = uvs; m.colors = cols; m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Unit quad in XY, facing +Z, u=0 at -x, v=0 at -y.</summary>
        private static Mesh MakeQuad()
        {
            var m = new Mesh { name = "LivePortals_Quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
