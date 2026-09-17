using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// The window on one portal. A pane over the opening shows a texture rendered every frame by a small camera
    /// that looks at a cube of the partner portal's capture. The camera's frustum is fitted to the pane as seen
    /// from your eye (off-axis projection) and its orientation is your view direction mapped through the portal
    /// into the partner's frame, so the picture has real parallax from either side of the portal. The cube faces
    /// are transparent where the capture saw sky, and the camera draws the current sky behind them.
    /// </summary>
    public class PortalWindow : MonoBehaviour
    {
        private static readonly List<PortalWindow> All = new List<PortalWindow>();
        private static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);
        private const float CubeDistance = 100f;
        private static Mesh _quad;
        private static bool _hiddenForCapture;

        private TeleportWorld _tw;
        private ZNetView _nview;
        private ZDOID _targetId = ZDOID.None;
        private PortalCapture _cap;
        private long _capTime = -2;
        private float _capCheckTimer;
        private float _tintTimer;

        private GameObject _pane;
        private Renderer _paneRenderer;
        private Material _paneMat;
        private GameObject _anchor;
        private readonly Material[] _faceMats = new Material[6];
        private Camera _cam;
        private RenderTexture _rt;
        private Light _light;

        private bool _built, _visible, _suppressed;
        private int _frame;

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
            if (dist > range + 3f) { Destroy(this); return; } // walked away; the scan re-adds us when we come back
            float alpha = range <= full ? (dist <= range ? 1f : 0f) : Mathf.Clamp01((range - dist) / (range - full));
            if (alpha <= 0.001f) { Hide(); return; }

            ZDOID target = _nview.GetZDO().GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (target == ZDOID.None) { Hide(); return; }
            ZDO tz = ZDOMan.instance.GetZDO(target);
            if (tz == null) { ZDOMan.instance.RequestZDO(target); Hide(); return; }
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
                        _cap = Storage.Load(target);
                        if (_cap != null) { EnsureBuilt(gc); ApplyCaptureToFaces(); Plugin.Dbg("window at " + Storage.Key(_nview.GetZDO().m_uid) + " shows capture " + Storage.Key(target)); }
                    }
                }
            }
            if (_cap == null) { Hide(); return; }
            EnsureBuilt(gc);

            // ---- Pane geometry in the portal's frame ----
            Quaternion rA = transform.rotation;
            Quaternion rB = tz.GetRotation();
            Vector3 up = rA * Vector3.up, n = rA * Vector3.forward, right = rA * Vector3.right;
            float w = Plugin.WindowWidth.Value, h = Plugin.WindowHeight.Value;
            Vector3 c = transform.position + rA * new Vector3(0f, Plugin.WindowCenterHeight.Value, Plugin.WindowForwardOffset.Value);
            _pane.transform.SetPositionAndRotation(c, rA);
            _pane.transform.localScale = new Vector3(w, h, 1f);

            // ---- Off-axis frustum from the eye through the pane (Kooima's generalized perspective) ----
            Vector3 pe = gc.m_camera.transform.position;
            bool front = Vector3.Dot(pe - c, n) >= 0f;
            Vector3 hr = right * (w * 0.5f), hu = up * (h * 0.5f);
            Vector3 pa, pb, pc; // lower-left, lower-right, upper-left as the viewer sees them
            if (front) { pa = c + hr - hu; pb = c - hr - hu; pc = c + hr + hu; }
            else { pa = c - hr - hu; pb = c + hr - hu; pc = c - hr + hu; }
            Vector3 vr = (pb - pa).normalized, vu = (pc - pa).normalized, vn = Vector3.Cross(vr, vu).normalized;
            Vector3 va = pa - pe, vb = pb - pe, vc = pc - pe;
            float d = Vector3.Dot(va, vn);
            if (d < 0.03f) { Hide(); return; } // eye in the plane of the pane
            float near = 0.05f, far = _cam.farClipPlane;
            float l = Vector3.Dot(vr, va) * near / d, r = Vector3.Dot(vr, vb) * near / d;
            float b = Vector3.Dot(vu, va) * near / d, t = Vector3.Dot(vu, vc) * near / d;

            // ---- Map through the portal: A's frame -> turned round -> B's frame ----
            Quaternion map = rB * Flip * Quaternion.Inverse(rA);
            _anchor.transform.SetPositionAndRotation(pe, rB);
            _cam.transform.SetPositionAndRotation(pe, map * Quaternion.LookRotation(vn, vu));
            _cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, near, far);

            // The pane's u runs from pa: the mesh has u=0 at -x, which is the viewer's left only from behind.
            _paneMat.mainTextureScale = new Vector2(front ? -1f : 1f, 1f);
            _paneMat.mainTextureOffset = new Vector2(front ? 1f : 0f, 0f);
            _paneMat.color = new Color(1f, 1f, 1f, alpha);

            // ---- Lighting: tint the capture to now, and spill light onto the viewer's side ----
            _tintTimer -= Time.deltaTime;
            if (_tintTimer <= 0f)
            {
                _tintTimer = 0.25f;
                Color tint = Lighting.Tint(_cap, Plugin.ToneMatch.Value);
                for (int i = 0; i < 6; i++) if (_faceMats[i] != null && _faceMats[i].HasProperty("_Color")) _faceMats[i].color = tint;
                UpdateLight(c, front ? n : -n, tint, alpha);
            }

            _visible = true;
            ApplyVisibility();
            if (!_hiddenForCapture && (_frame++ % Mathf.Max(1, Plugin.RenderEveryNFrames.Value)) == 0)
                _cam.Render();
        }

        private void UpdateLight(Vector3 c, Vector3 outward, Color tint, float alpha)
        {
            if (_light == null) return;
            float strength = Plugin.PortalLight.Value;
            if (strength <= 0f) { _light.enabled = false; return; }
            Lighting.Sample(out var sun, out var amb, out _, out _);
            float here = Mathf.Clamp01(Lighting.Level(sun, amb));
            float there = Mathf.Clamp01(_cap.AverageLuminance * Lighting.Luminance(tint) * 1.6f);
            float intensity = strength * 3f * Mathf.Clamp01(there - here * 0.8f) * alpha;
            _light.transform.position = c + outward * 0.4f;
            _light.transform.rotation = Quaternion.LookRotation(outward, Vector3.up);
            _light.range = Plugin.PortalLightRange.Value;
            Color col = _cap.AverageColor * tint;
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
            int res = Plugin.WindowResolution.Value;
            _rt = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32) { name = "LivePortals_Window" };
            _rt.Create();

            // Pane over the opening (default layer, visible to the game camera; hidden during captures).
            _pane = new GameObject("LivePortals_Pane");
            _pane.transform.SetParent(transform, false);
            _pane.AddComponent<MeshFilter>().sharedMesh = _quad;
            _paneRenderer = _pane.AddComponent<MeshRenderer>();
            _paneMat = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
            _paneMat.mainTexture = _rt;
            _paneRenderer.sharedMaterial = _paneMat;
            _paneRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _paneRenderer.receiveShadows = false;
            _paneRenderer.enabled = false;

            // Parallax cube: six faces around an anchor that sits on the eye, oriented like the partner portal.
            _anchor = new GameObject("LivePortals_Cube");
            _anchor.layer = Plugin.HiddenLayer;
            for (int i = 0; i < 6; i++)
            {
                var f = new GameObject("Face" + i);
                f.layer = Plugin.HiddenLayer;
                f.transform.SetParent(_anchor.transform, false);
                f.transform.localRotation = Capture.FaceRotations[i];
                f.transform.localPosition = Capture.FaceRotations[i] * new Vector3(0f, 0f, CubeDistance);
                f.transform.localScale = new Vector3(2f * CubeDistance * 1.004f, 2f * CubeDistance * 1.004f, 1f);
                f.AddComponent<MeshFilter>().sharedMesh = _quad;
                var mr = f.AddComponent<MeshRenderer>();
                _faceMats[i] = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
                mr.sharedMaterial = _faceMats[i];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            // Window camera: draws the current sky (like the game's sky camera) and the cube, into the pane's texture.
            var camGo = new GameObject("LivePortals_WindowCamera");
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false;
            var sky = gc.m_skyCamera;
            if (Plugin.LiveSky.Value && sky != null)
            {
                _cam.CopyFrom(sky);
                _cam.cullingMask = sky.cullingMask | (1 << Plugin.HiddenLayer);
            }
            else
            {
                _cam.CopyFrom(gc.m_camera);
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = RenderSettings.fogColor;
                _cam.cullingMask = 1 << Plugin.HiddenLayer;
            }
            _cam.enabled = false;
            _cam.depthTextureMode = DepthTextureMode.None;
            _cam.nearClipPlane = 0.05f;
            if (_cam.farClipPlane < CubeDistance * 2f) _cam.farClipPlane = CubeDistance * 2f;
            _cam.targetTexture = _rt;
            _cam.rect = new Rect(0f, 0f, 1f, 1f);

            var lightGo = new GameObject("LivePortals_Light");
            lightGo.transform.SetParent(transform, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Spot;
            _light.spotAngle = 150f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        private void ApplyCaptureToFaces()
        {
            if (_cap == null) return;
            for (int i = 0; i < 6; i++) if (_faceMats[i] != null) _faceMats[i].mainTexture = _cap.Faces[i];
            _tintTimer = 0f;
        }

        private void ReleaseCapture()
        {
            if (_cap != null) { _cap.Destroy(); _cap = null; }
            for (int i = 0; i < 6; i++) if (_faceMats[i] != null) _faceMats[i].mainTexture = null;
        }

        private void Hide()
        {
            _visible = false;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool on = _visible && !_hiddenForCapture;
            if (_paneRenderer != null) _paneRenderer.enabled = on;
            if (_light != null && !on) _light.enabled = false;
        }

        private void Cleanup()
        {
            ReleaseCapture();
            if (_pane != null) Destroy(_pane);
            if (_anchor != null) Destroy(_anchor);
            if (_cam != null) Destroy(_cam.gameObject);
            if (_light != null) Destroy(_light.gameObject);
            if (_paneMat != null) Destroy(_paneMat);
            for (int i = 0; i < 6; i++) if (_faceMats[i] != null) Destroy(_faceMats[i]);
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

        /// <summary>Unit quad in XY, facing +Z, u=0 at -x, v=0 at -y.</summary>
        private static Mesh MakeQuad()
        {
            var m = new Mesh { name = "LivePortals_Quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
