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
            public int Glow;                                                 // how many of them
            public readonly List<Material> Sky = new List<Material>();      // the sky-light layers: strength follows the ambient light
            public readonly List<MeshRenderer> Additive = new List<MeshRenderer>(); // renderers of every additive layer (torchlight, sky light)
            public readonly HashSet<Material> SunLit = new HashSet<Material>(); // layers with a sky-light layer over them: tinted by the sun alone
            public readonly List<Texture> Textures = new List<Texture>(); // blurred copies, ours to destroy
        }

        internal static readonly List<PortalWindow> All = new List<PortalWindow>();
        internal static int DiagQueue = 2450;   // numpad +: the picture's render queue
        internal static bool DiagPlugOff;       // numpad -: the depth plug
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
        private GameObject _fireHolder;                                       // stands for the far ring's frame; see FireSet
        private readonly List<ParticleSystemRenderer> _fire = new List<ParticleSystemRenderer>();
        private readonly List<ParticleSystem> _fireSystems = new List<ParticleSystem>();
        private bool _fireSimulating;
        private int _fireBuildNext, _fireBuilt;

        /// <summary>Two of the far side's flame effects per frame until all are there.</summary>
        private void BuildFire()
        {
            if (_fireHolder == null || _set == null || _set.Fire == null || _fireBuildNext >= _set.Fire.Items.Count) return;
            for (int n = 0; n < 2 && _fireBuildNext < _set.Fire.Items.Count; n++, _fireBuildNext++)
                if (_set.Fire.BuildItem(_fireBuildNext, _fireHolder.transform, _fire)) _fireBuilt++;
            if (_fireBuildNext >= _set.Fire.Items.Count)
            {
                _fireSystems.Clear();
                foreach (var ps in _fireHolder.GetComponentsInChildren<ParticleSystem>(true)) _fireSystems.Add(ps);
                _fireSimulating = true;
                Plugin.Dbg($"{_fireBuilt} of {_set.Fire.Items.Count} flame effects play in the window at {name} ({_fire.Count} particle renderers)");
            }
        }
        private Color _tint = Color.white, _sunTint = Color.white, _skyGain = Color.black;
        private bool _split;
        private RenderTexture _bloomRt;
        private GameObject _bloom;
        private MeshRenderer _bloomRenderer;
        private Material _bloomMat;
        private bool _bloomShown, _fireInView;
        private int _bloomTick;

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
            BuildFire();
            // The far side's flames simulate only while this window is being drawn: at a hub, eight windows' worth
            // of emitters (about five hundred) simulating every frame, most of them behind the player, cost more
            // than the windows themselves.
            if (_fireSystems.Count > 0)
            {
                bool want = _visible && !_hiddenForCapture && Time.time - _lastRenderTime < 1f;
                if (want != _fireSimulating)
                {
                    _fireSimulating = want;
                    foreach (var ps in _fireSystems) if (ps != null) { if (want) ps.Play(false); else ps.Pause(false); }
                }
            }
            var player = Player.m_localPlayer;
            var gc = GameCamera.instance;
            if (player == null || gc == null || gc.m_camera == null) { Hide(); return; }

            float dist = Vector3.Distance(player.transform.position, transform.position);
            float act = Mathf.Max(0.5f, _tw.m_activationRange);
            float range = act * Plugin.RangeMultiplier.Value;
            float full = act * Plugin.FullMultiplier.Value;
            if (dist > range + 3f + PreloadMargin) { Destroy(this); return; } // walked away; the scan re-adds us when we come back
            // Standing in the ring, or being sent through it, the window is off: the camera sits on the far side of
            // the pane then and would look at the back of the picture instead of at the player. As in vanilla, the
            // swirl plays around the player.
            if (dist < 3f)
            {
                Vector3 c0 = PortalShape.Of(_tw).Centre, n0 = transform.forward;
                Vector3 d0 = player.transform.position + Vector3.up - c0;
                float along = Vector3.Dot(d0, n0);
                if ((Mathf.Abs(along) < 1.1f && (d0 - n0 * along).magnitude < 1.6f) || player.IsTeleporting()) { Hide(); return; }
            }
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
                _capCheckTimer = 0.25f;
                bool wantAll = dist <= Plugin.SecondaryViewpointRange.Value + 6f;
                // A capture just taken, its layers done in memory and its files still on their way: take it now.
                // That is how the window shows the place you came from a moment after you arrive, not the four to
                // nine seconds the files take. Its stored form has the same time stamp, so it is not loaded twice.
                if (CaptureRun.TryGetReady(target, out var ready) && ready.TakenAt > _capTime)
                {
                    ReleaseCapture();
                    _capTime = ready.TakenAt;
                    EnsureBuilt(gc);
                    _loader = CaptureLoader.FromMemory(ready, !WindowMaterial.DepthWorks, wantAll ? int.MaxValue : 1);
                }
                else
                {
                    long stored = Storage.StoredTime(target);
                    if (stored != _capTime && !(stored < _capTime && _loader != null))
                    {
                        ReleaseCapture();
                        _capTime = stored;
                        if (stored >= 0)
                        {
                            EnsureBuilt(gc); // first: it decides (WindowMaterial) how the capture has to be loaded
                            _loader = CaptureLoader.Start(target, !WindowMaterial.DepthWorks, 0, wantAll ? int.MaxValue : 1);
                        }
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
            // A big ring paired with a small one (stone and wood): matched centre to centre, the far side's ground came
            // out a metre above or below this side's. Match the rings' lower edges instead, which is where the ground
            // is at both: the far world keeps its size and continues this side's floor.
            float farH = _set.Primary != null ? _set.Primary.RingHeight : 0f;
            if (!glass && farH > 0.5f) anchor0 += (rB * Vector3.up) * ((farH - h) * 0.5f);
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
            if (_fireHolder != null) _fireHolder.transform.SetPositionAndRotation(anchor0, rB);
            _cam.transform.SetPositionAndRotation(pe, map * Quaternion.LookRotation(vn, vu));
            // The clip planes as well as the matrix: shaders that read the depth buffer (soft particles: every flame
            // in the game fades out where it nears the surface behind it) turn depth into distance with the
            // camera's own near and far, not with the matrix. With the near plane really on the pane, metres
            // away, and the camera still saying 0.3, the flames of 0.9.21 took everything behind them to be in
            // front of them and faded to nothing ("put behind a lot of the layers").
            _cam.nearClipPlane = near;
            _cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, near, far);
            if (glass && Time.time - _lastGlassLog > 1f)
            {
                _lastGlassLog = Time.time;
                Plugin.Log.LogInfo($"LivePortals glass: eye {pe} pane {c} n {n} front {realFront} d {d:0.00} l {l:0.000} r {r:0.000} b {b:0.000} t {t:0.000} camFwd {_cam.transform.forward} camRight {_cam.transform.right} mainFwd {main.transform.forward}");
            }

            // The picture's u runs from pa, the viewer's left; the mesh has u=0 at -x, which is the viewer's left
            // only from behind. From the front, mirror the mesh (a negative x scale: the disc is symmetric, only
            // its UVs flip). Sprite shaders ignore texture scale/offset, so it has to be done on the geometry.
            // The z scale turns the pane's one face, and its normals, toward the viewer. Up to 0.9.22 the disc had both
            // windings on shared vertices, so its normals summed to nothing; the game's ambient occlusion read that
            // from the G-buffer as fully occluded and multiplied the plug, and the picture drawn over it before the
            // screen effects, to black (the stone portal at night; numpad * and + in the second 0.9.22 build).
            float sz = realFront ? -1f : 1f;
            _pane.transform.localScale = new Vector3(front ? -w : w, h, sz);
            // The plug and the picture dissolve in as the same blocks (see Dissolve): the picture is cut per cell,
            // never half transparent.
            if (_paneSolid) WindowMaterial.SetPaneVisible(_paneMat, alpha);
            else _paneMat.color = new Color(1f, 1f, 1f, alpha);
            if (_overlay != null)
            {
                // The plug sits a little behind the pane, the picture on it: the game's own swirl (transparent, on
                // the pane) then passes the depth test against the plug and, drawn after the picture (see the
                // picture's render queue), swirls over it as it did over the old emissive pane.
                float off = realFront ? 0.03f : -0.03f;
                _pane.transform.position = c - n * off;
                _overlay.transform.localPosition = new Vector3(0f, 0f, off * sz); // the parent's z scale is sz
                if (_bloom != null) _bloom.transform.localPosition = new Vector3(0f, 0f, off * sz);
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
                Color tint;
                _split = _reliefs.Count > 0 && _reliefs[0].Sky.Count > 0;
                if (_split)
                {
                    // Sun and sky followed separately (see Lighting.SplitTint).
                    Lighting.SplitTint(primary, Plugin.ToneMatch.Value, out _sunTint, out _skyGain);
                    tint = Lighting.MixedTint(primary, _sunTint, _skyGain);
                }
                else tint = Lighting.Tint(primary, Plugin.ToneMatch.Value, _reliefs.Count > 0 && _reliefs[0].Glow > 0);
                _tint = tint;
                PushTints();
                // As strong as the picture is complete, not as the viewer is near: the dissolve is over at two thirds
                // of the way in, and up to 0.9.22 the light kept growing until the portal's own activation range.
                UpdateLight(primary, c, realFront ? n : -n, tint, Dissolve.Reveal(alpha), realFront);
            }

            _visible = true;
            ApplyVisibility();
            _pe = pe; _anchor0 = anchor0; _rB = rB; _target = target; _near = near; _far = far; _l = l; _r = r; _b = b; _t = t; _glass = glass; _realFront = realFront; _alphaNow = alpha;
            bool wantDump = _dumped != DumpRequest;
            WantsRender = !_hiddenForCapture && ShouldRender(main, pe, alpha, wantDump);
        }

        /// <summary>For the perf log: what the loaded captures hold in textures, all windows together.</summary>
        internal static float CaptureMegabytes()
        {
            long bytes = 0;
            foreach (var w in All)
            {
                if (w == null || w._set == null) continue;
                foreach (var cap in w._set.Captures)
                {
                    if (cap == null) continue;
                    for (int i = 0; i < 6; i++)
                    {
                        bytes += Size(cap.Faces[i]) + Size(cap.Fronts[i]) + Size(cap.Locals[i]) + Size(cap.LocalsFront[i]) + Size(cap.Ambients[i]) + Size(cap.AmbientsFront[i]);
                    }
                }
            }
            return bytes / 1048576f;
        }

        private static long Size(Texture2D t) => t == null ? 0 : (long)t.width * t.height * 4 * 4 / 3;

        private void PushTints()
        {
            foreach (var rl in _reliefs)
            {
                foreach (var m in rl.Materials) WindowMaterial.SetTint(m, _split && rl.SunLit.Contains(m) ? _sunTint : _tint);
                if (_split) foreach (var m in rl.Sky) WindowMaterial.SetAdditiveGain(m, _skyGain);
                // Torchlight: the picture already holds it, dimmed with the rest, so only the dimmed-away part is
                // added back: picture * t + torch * (1 - t), in light. Up to 0.9.22 it was added whole, which by day
                // went unnoticed and in a night capture seen at night (t = 1) showed every torch-lit wall, and
                // every surface that shows its own colour, twice as bright.
                if (WindowMaterial.AdditiveScalable)
                {
                    Color b = _split ? _sunTint : _tint;
                    var back = new Color(1f - Mathf.Pow(Mathf.Clamp01(b.r), 2.2f), 1f - Mathf.Pow(Mathf.Clamp01(b.g), 2.2f), 1f - Mathf.Pow(Mathf.Clamp01(b.b), 2.2f), 1f);
                    foreach (var m in rl.Untinted) WindowMaterial.SetAdditiveGain(m, back);
                }
            }
        }

        /// <summary>
        /// The flames alone, in front of a blacked-out copy of the picture (so pillars still hide them), into a small
        /// texture of their own. The pane adds it on top of the picture several times over: flame pixels then
        /// stand well above white in the game's frame, and its bloom glows around them as around a real fire.
        /// The picture itself is 8 bits and tops out at white, which blooms like a sheet of paper.
        /// </summary>
        private static readonly Plane[] _firePlanes = new Plane[6];

        /// <summary>Whether any of the flame copies lies in what the window camera sees from here.</summary>
        private bool FireInView()
        {
            if (_fire.Count == 0 || _cam == null) return false;
            GeometryUtility.CalculateFrustumPlanes(_cam, _firePlanes);
            foreach (var fr in _fire)
                if (fr != null && GeometryUtility.TestPlanesAABB(_firePlanes, fr.bounds)) return true;
            return false;
        }

        private void RenderBloom()
        {
            bool want = _bloomRt != null && FireInView() && Dissolve.Reveal(_alphaNow) >= 0.999f && _eyeDist < 40f;
            if (!want) { _bloomShown = false; return; }
            // The glow is a blur of something that flickers: every other redraw is plenty, and it is a whole camera pass.
            if (_bloomShown && (_bloomTick++ & 1) == 1) return;
            // Only the flames: every relief renderer is off (the caller runs this before the passes and before the
            // grass is queued for the camera), the sky layer is masked out, the clear is black. Up to 0.9.27 the
            // reliefs stayed on with a black tint and the pass ran after the grass had been queued, so the far
            // side's grass and whatever the tint did not cover came through as a blurred glow over the whole
            // picture: the "glossy sheen". The price: a flame hidden by a pillar still glows through it a little.
            var target = _cam.targetTexture; Color bg = _cam.backgroundColor; var clear = _cam.clearFlags; int mask = _cam.cullingMask;
            try
            {
                _cam.targetTexture = _bloomRt;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Color.black;
                _cam.cullingMask = 1 << Plugin.FaceLayer;
                _cam.Render();
                _bloomShown = true;
            }
            finally
            {
                _cam.targetTexture = target;
                _cam.backgroundColor = bg;
                _cam.clearFlags = clear;
                _cam.cullingMask = mask;
            }
        }

        // The scene's point and spot lights, found afresh every two seconds; switched off around a window render.
        private static readonly List<Light> _sceneLights = new List<Light>();
        private static readonly List<Light> _lightsOff = new List<Light>();
        private static float _sceneLightsAt = -10f;

        /// <summary>Lights the game creates with its world objects (see Patches.ZNetScene_CreateObject); a scan of the whole scene every two seconds (0.9.38) was a hitch in a big base.</summary>
        internal static void RegisterLights(GameObject go)
        {
            if (go == null) return;
            foreach (var l in go.GetComponentsInChildren<Light>(true))
                if (l.type != LightType.Directional) _sceneLights.Add(l);
        }

        private static void LightsOff()
        {
            if (Time.time - _sceneLightsAt > 30f)
            {
                // Lights whose objects the game has since unloaded drop out of the list.
                _sceneLightsAt = Time.time;
                _sceneLights.RemoveAll(l => l == null);
            }
            _lightsOff.Clear();
            foreach (var l in _sceneLights)
                if (l != null && l.enabled && l.isActiveAndEnabled) { l.enabled = false; _lightsOff.Add(l); }
        }

        private static void LightsBack()
        {
            foreach (var l in _lightsOff) if (l != null) l.enabled = true;
            _lightsOff.Clear();
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
            // No reflections and no point or spot lights either. The reliefs are black surfaces showing the picture
            // as emission, and the game's pipeline still gives a black non-metal its 4% reflectance, rising steeply
            // at grazing angles: seen obliquely they mirrored the night sky as a milky blue film and the torches by
            // the ring as a gold-pink sheen, on the dark parts, shifting with the eye (0.9.22 to 0.9.37; the offline
            // redraw of the same capture, lit by nothing, was clean). The sun and moon stay on for the grass.
            float reflect = RenderSettings.reflectionIntensity;
            RenderSettings.reflectionIntensity = 0f;
            LightsOff();
            try
            {
                // The grass is only drawn from near by (far off it is smaller than a pixel of the window and thousands
                // of instances). It grows out of the ground over the last eight metres of the approach; switched
                // on at one distance it popped in, and out again with every step back.
                // The flame glow first, while nothing but the flames is switched on and no grass is queued yet.
                foreach (var fr in _fire) if (fr != null) fr.enabled = true;
                RenderBloom();
                foreach (var fr in _fire) if (fr != null) fr.enabled = false;
                float grassFrom = Plugin.SecondaryViewpointRange.Value + 12f;
                if (_eyeDist <= grassFrom)
                    _set.Grass?.Draw(_cam, Matrix4x4.TRS(anchor0, rB, Vector3.one), _set.Primary.GrassGain, Mathf.Clamp01((grassFrom - _eyeDist) / 8f));
                SetReliefsEnabled(true, true);
                _cam.Render();
                SetReliefsEnabled(true, false);
                _cam.clearFlags = CameraClearFlags.Depth;
                _cam.cullingMask = 1 << Plugin.FaceLayer;
                SetReliefsEnabled(false, true);
                // The far side's flames, live: particles, drawn after the reliefs and hidden by whatever of them is nearer.
                foreach (var fr in _fire) if (fr != null) fr.enabled = true;
                _cam.Render();
            }
            finally
            {
                foreach (var fr in _fire) if (fr != null) fr.enabled = false;
                SetReliefsEnabled(false, false);
                SetReliefsEnabled(true, false);
                _cam.clearFlags = clear;
                _cam.cullingMask = mask;
                RenderSettings.fog = fog;
                QualitySettings.shadowDistance = shadows;
                RenderSettings.reflectionIntensity = reflect;
                LightsBack();
            }
            _sw.Stop();
            _msAccum += _sw.Elapsed.TotalMilliseconds; _renders++;
            PerfMs += _sw.Elapsed.TotalMilliseconds; PerfRenders++;
            if (dump)
            {
                _dumped = DumpRequest;
                SaveWindow("final");
                // The flame glow layer as it is added over the picture: what the bloom pass really caught.
                if (_bloomRt != null && _bloomShown) SaveTexture(_bloomRt, "bloom");
                // The eye in the primary relief's own frame: what tools/LayerTest needs (EYES=x,y,z) to redraw this view.
                Vector3 eyeLocal = Quaternion.Inverse(rB) * (pe - (anchor0 + rB * CaptureForward));
                Plugin.Log.LogInfo($"LivePortals dump: window {Storage.Key(_nview.GetZDO().m_uid)} shows {Storage.Key(target)} ({_reliefs.Count} viewpoints), glass {glass}, front {realFront}, EYES={eyeLocal.x:0.###},{eyeLocal.y:0.###},{eyeLocal.z:0.###} near {near:0.###} far {far:0} frustum l {l:0.####} r {r:0.####} b {b:0.####} t {t:0.####} hdr {_cam.allowHDR} path {_cam.actualRenderingPath} depthBits {_rt.depth} format {_rt.format} material {WindowMaterial.Summary} pane {(_paneSolid ? "plug+sprite" : "sprite")} tint {_tint}, sky-light layers {(_reliefs.Count > 0 ? _reliefs[0].Sky.Count : 0)} (sun tint {_sunTint}, sky gain {_skyGain}, sky-lit share {_set.Primary.AverageAmbientLuminance:0.000}) (captured under sun {_set.Primary.Sun} ambient {_set.Primary.Ambient}, picture luminance {_set.Primary.AverageLuminance:0.000} of which local light {_set.Primary.AverageLocalLuminance:0.000}), local-light layers {(_reliefs.Count > 0 ? _reliefs[0].Glow : 0)} (additive shader {(WindowMaterial.AdditiveWorks ? "found" : "NOT found")}), live flame renderers {_fire.Count}{FireReport(pe)}");
            }
        }

        /// <summary>For the dump line: are the flame copies alive, and where.</summary>
        private string FireReport(Vector3 eye)
        {
            if (_fire.Count == 0) return "";
            int particles = 0, playing = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var fr in _fire)
            {
                if (fr == null) continue;
                var ps = fr.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                particles += ps.particleCount;
                if (ps.isPlaying) playing++;
                if (sb.Length < 300) sb.Append($" [{fr.name}: {ps.particleCount} particles, bounds centre {fr.bounds.center - eye} size {fr.bounds.size}, shader {(fr.sharedMaterial != null ? fr.sharedMaterial.shader.name : "none")}, active {fr.gameObject.activeInHierarchy}]");
            }
            return $" ({playing} playing, {particles} particles; relative to the eye:{sb})";
        }

        private void SaveTexture(RenderTexture rt, string tag)
        {
            try
            {
                string dir = Storage.DebugDir();
                Directory.CreateDirectory(dir);
                var srgb = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(rt, srgb);
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                var prev = RenderTexture.active;
                RenderTexture.active = srgb;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(srgb);
                File.WriteAllBytes(Path.Combine(dir, $"{System.DateTime.Now:HHmmss}_{Storage.Key(_nview.GetZDO().m_uid)}_{tag}.png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("LivePortals: dump of " + tag + " failed: " + e.Message); }
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
            int max = Mathf.Max(128, Plugin.WindowRes);
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
            if (!WindowMaterial.PictureIgnoresAlpha) WindowMaterial.SetPaneTexture(_paneMat, _rt);
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
                // Flames move on their own: a still eye gets thirty pictures a second of them, up close.
                // Only while a fire is actually in the picture: a window that merely has one somewhere behind the far
                // ring stays as cheap as any other.
                _fireInView = FireInView();
                float stillFor = _fireInView && _eyeDist < 25f ? 1f / 30f : 0.5f;
                if (!moved && since < stillFor) { _skips++; return false; }
            }
            // Cadence: the nearest window follows the eye every frame (a redraw is about 2 ms); the others thirty
            // times a second, taking turns for the rest of the frame's budget.
            // (A touch under the period, so that at twice the rate it is every second frame and not every third.)
            float interval = Rank == 0 ? 0.9f / Mathf.Max(20, Plugin.MaxWindowFps.Value) : 1f / 30f;
            if (since < interval) { _skips++; return false; }
            if (Plugin.PerfLog.Value && Time.time - _perfLogTime > 10f)
            {
                if (_renders > 0)
                    Plugin.Log.LogInfo($"LivePortals perf: window {name} rank {Rank}: {_renders} renders / {_skips} skipped in {Time.time - _perfLogTime:0}s, avg {_msAccum / _renders:0.0} ms per render ({_msAccum / Mathf.Max(0.1f, Time.time - _perfLogTime):0.0} ms per second), {_reliefs.Count} viewpoints, eye {_eyeDist:0.0} m");
                _perfLogTime = Time.time; _msAccum = 0; _renders = 0; _skips = 0;
            }
            return true;
        }

        private void UpdateLight(PortalCapture cap, Vector3 c, Vector3 outward, Color tint, float alpha, bool frontSide)
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
            // Fires by the far ring shine through it whatever the hour. The picture's average cannot say so: three
            // torches are a few bright pixels in a dark frame (0.9.21: "this portal isnt emitting light even
            // though theres 3 fires directly infront of it").
            if (_set != null && _set.Fire != null)
            {
                float fire = strength * Mathf.Min(2.5f, 0.6f * _set.Fire.LightAtRing(frontSide, out Color fireCol)) * alpha;
                if (fire > 0.001f)
                {
                    col = (col * intensity + fireCol * fire) / (intensity + fire);
                    col.a = 1f;
                    intensity += fire;
                }
            }
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
            int res = Plugin.WindowRes;
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
            // The plug shows the picture as emission too (as the old pane did): the sprite over it blends by the
            // window texture's alpha, and where the live clouds leave that below one (their soft edges) the plug
            // fills in the same colour instead of black, which drew a dark outline round every cloud (0.9.10 to
            // 0.9.24). Everywhere else the sprite covers it, so the plug's own darkening at night cannot show.
            // With a picture material that ignores alpha (see WindowMaterial.MakePicture) the plug is black and
            // never shows; otherwise it carries the picture as emission for the pixels the sprite leaves thin.
            _paneMat = WindowMaterial.PictureIgnoresAlpha ? WindowMaterial.MakePlug() : WindowMaterial.MakePane(_rt);
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
                _overlayMat = WindowMaterial.MakePicture(_rt);
                // Drawn at the end of the opaque stage (queue 2450, in the forward pass since a sprite has no G-buffer
                // pass): after the deferred lighting and the screen effects that read the G-buffer, which is what
                // darkened the old emissive pane at night, but before the mist and everything else composited
                // from the depth buffer, which then cover it the way they cover the frame (0.9.10 to 0.9.17 drew it
                // in the transparent stage, and the Mistlands mist never touched it). The game's swirl (3000) still
                // draws after it, over the picture.
                _overlayMat.renderQueue = 2450;
                _overlayRenderer.sharedMaterial = _overlayMat;
                if (Plugin.FlameBloom.Value > 0f && WindowMaterial.AdditiveScalable)
                {
                    _bloomRt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32) { name = "LivePortals_FlameBloom", useMipMap = false };
                    _bloomRt.Create();
                    _bloom = new GameObject("LivePortals_FlameBloom");
                    _bloom.transform.SetParent(_pane.transform, false);
                    _bloom.AddComponent<MeshFilter>().sharedMesh = _pane.GetComponent<MeshFilter>().sharedMesh;
                    _bloomRenderer = _bloom.AddComponent<MeshRenderer>();
                    _bloomRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _bloomRenderer.receiveShadows = false;
                    _bloomRenderer.enabled = false;
                    _bloomMat = WindowMaterial.MakeAdditive(_bloomRt);
                    WindowMaterial.SetAdditiveGain(_bloomMat, Color.white * Plugin.FlameBloom.Value); // the pane's mesh has one winding, like the reliefs' 
                    _bloomMat.renderQueue = 2451; // right after the picture, still before the game's screen effects
                    _bloomRenderer.sharedMaterial = _bloomMat;
                }
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
                    bool sky = k == 0 && WindowMaterial.AdditiveScalable && cap.Ambients[i] != null;
                    if (sky && back != null)
                    {
                        // What the sky alone lit, added back by as much as the ambient light outlasts the sun.
                        rl.SunLit.Add(rl.Materials[rl.Materials.Count - 1]);
                        AddLayer(rl, f.transform, "SkyLight", back, WindowMaterial.MakeAdditive(cap.Ambients[i]), false, rl.Sky);
                    }
                    if (WindowMaterial.DepthWorks)
                    {
                        var filled = AddLayer(rl, f.transform, "BackFilled", back, cap.Faces[i], false, Layers.CutoffAll, order);
                        if (filled != null) filled.localScale = Vector3.one * Layers.FilledPush;
                    }
                    // Torchlight, firelight and glowing things: added on top of the background, never tinted, so
                    // they do not fade with the sun the way the rest of the picture does at night.
                    if (k == 0 && cap.Locals[i] != null && WindowMaterial.AdditiveWorks)
                    {
                        AddLayer(rl, f.transform, "Glow", back, WindowMaterial.MakeAdditive(cap.Locals[i]), false, rl.Untinted);
                        rl.Glow++;
                    }
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
                        if (sky && cap.AmbientsFront[i] != null && cap.Front[i] != null)
                        {
                            rl.SunLit.Add(rl.Materials[rl.Materials.Count - 1]);
                            AddLayer(rl, f.transform, "SkyLightFront", cap.Front[i], WindowMaterial.MakeAdditive(cap.AmbientsFront[i]), false, rl.Sky);
                        }
                        if (cap.LocalsFront[i] != null && WindowMaterial.AdditiveWorks)
                            AddLayer(rl, f.transform, "GlowFront", cap.Front[i], WindowMaterial.MakeAdditive(cap.LocalsFront[i]), false, rl.Untinted);
                    }
                }
                _reliefs.Add(rl);
            }
            _tintTimer = 0f;
            if (_set.Fire != null && Plugin.LiveFire.Value)
            {
                _fireHolder = new GameObject("LivePortals_Fire");
                _fireHolder.SetActive(_visible && !_hiddenForCapture);
                // The effects are copied a couple per frame in Update (see BuildFire): thirty-two Instantiates at
                // once, times eight windows arriving at a hub, were a good part of the 12 fps there.
                _fireBuildNext = 0; _fireBuilt = 0;
                _fireSystems.Clear();
                _fireSimulating = true;
            }
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
            if (owner != rl.Materials) rl.Additive.Add(mr);
            owner.Add(mat);
            return go.transform;
        }

        private void DestroyReliefs()
        {
            foreach (var rl in _reliefs)
            {
                foreach (var m in rl.Materials) Destroy(m);
                foreach (var m in rl.Untinted) Destroy(m);
                foreach (var m in rl.Sky) Destroy(m);
                foreach (var t in rl.Textures) Destroy(t);
                if (rl.Anchor != null) Destroy(rl.Anchor);
            }
            _reliefs.Clear();
            _fire.Clear();
            _fireSystems.Clear();
            if (_fireHolder != null) { Destroy(_fireHolder); _fireHolder = null; }
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
            if (_paneRenderer != null) _paneRenderer.enabled = on && !(DiagPlugOff && _overlay != null);
            if (_overlayMat != null && _overlayMat.renderQueue != DiagQueue)
            {
                _overlayMat.renderQueue = DiagQueue;
                if (_bloomMat != null) _bloomMat.renderQueue = DiagQueue + 1;
            }
            if (_overlayRenderer != null) _overlayRenderer.enabled = on;
            if (_bloomRenderer != null) _bloomRenderer.enabled = on && _bloomShown;
            if (_light != null && !on) _light.enabled = false;
            // Out of sight the flames stop simulating altogether.
            if (_fireHolder != null && _fireHolder.activeSelf != on) _fireHolder.SetActive(on);
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
            if (_bloomMat != null) Destroy(_bloomMat);
            if (_bloomRt != null) { _bloomRt.Release(); Destroy(_bloomRt); }
            _bloom = null; _bloomRenderer = null; _bloomMat = null; _bloomRt = null; _bloomShown = false;
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
            // One face, toward -z, with normals that say so; Refresh turns it to the viewer.
            var tris = new int[segments * 3];
            var normals = new Vector3[segments + 1];
            normals[0] = Vector3.back;
            for (int i = 0; i < segments; i++)
            {
                int a = i + 1, b = (i + 1) % segments + 1;
                tris[i * 3] = 0; tris[i * 3 + 1] = b; tris[i * 3 + 2] = a;
                normals[i + 1] = Vector3.back;
            }
            m.vertices = verts; m.uv = uvs; m.colors = cols; m.triangles = tris;
            m.normals = normals;
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
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 }; // one face, toward -z; Refresh turns it to the viewer
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.RecalculateBounds();
            return m;
        }
    }
}
