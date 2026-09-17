using System.Collections.Generic;
using UnityEngine;

namespace LivePortals
{
    /// <summary>
    /// The window on one portal. A pane over the opening shows a texture rendered every frame by a small camera
    /// that looks at the partner portal's capture: six depth-displaced meshes around the eye, textured with the
    /// captured faces (transparent where they saw sky), with the current sky drawn behind them. The camera's
    /// frustum is fitted to the pane as seen from your eye (off-axis projection) and its orientation is your view
    /// direction mapped through the portal into the partner's frame, so the picture has real parallax from either
    /// side. The meshes are switched on only for the instant that camera renders, so no other camera sees them.
    /// </summary>
    public class PortalWindow : MonoBehaviour
    {
        private static readonly List<PortalWindow> All = new List<PortalWindow>();
        private static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);
        private static Mesh _quad;
        private static bool _hiddenForCapture;
        private static bool _loggedMasks;

        private TeleportWorld _tw;
        private ZNetView _nview;
        private ZDOID _targetId = ZDOID.None;
        private PortalCapture _cap;
        private long _capTime = -2;
        private float _capCheckTimer;
        private float _tintTimer;
        private bool _loggedGeometry;

        private GameObject _pane;
        private Renderer _paneRenderer;
        private Material _paneMat;
        private GameObject _anchor;
        private readonly Renderer[] _faceRenderers = new Renderer[6];
        private readonly MeshFilter[] _faceFilters = new MeshFilter[6];
        private readonly Material[] _faceMats = new Material[6];
        private readonly Mesh[] _faceMeshes = new Mesh[6];
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
                        if (_cap != null) { EnsureBuilt(gc); ApplyCapture(); Plugin.Dbg("window at " + Storage.Key(_nview.GetZDO().m_uid) + " shows capture " + Storage.Key(target)); }
                    }
                }
            }
            if (_cap == null) { Hide(); return; }
            EnsureBuilt(gc);

            // ---- Pane geometry: centred on the portal's proximity point (the ring), in the portal's frame ----
            Quaternion rA = transform.rotation;
            Quaternion rB = tz.GetRotation();
            Vector3 up = rA * Vector3.up, n = rA * Vector3.forward, right = rA * Vector3.right;
            float w = Plugin.WindowWidth.Value, h = Plugin.WindowHeight.Value;
            Vector3 basePos = _tw.m_proximityRoot != null ? _tw.m_proximityRoot.position : transform.position;
            Vector3 c = basePos + rA * new Vector3(0f, Plugin.WindowCenterHeight.Value, Plugin.WindowForwardOffset.Value);
            _pane.transform.SetPositionAndRotation(c, rA);
            _pane.transform.localScale = new Vector3(w, h, 1f);
            if (!_loggedGeometry)
            {
                _loggedGeometry = true;
                var mb = _tw.m_model != null ? _tw.m_model.bounds : new Bounds(transform.position, Vector3.zero);
                Plugin.Log.LogInfo($"LivePortals: portal {name} pos {transform.position} fwd {n} up {up} scale {transform.lossyScale} proximity {(_tw.m_proximityRoot != null ? _tw.m_proximityRoot.position.ToString() : "none")} model bounds centre {mb.center} size {mb.size}; pane centre {c}");
            }

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
            {
                // The capture meshes exist only while this camera renders: no other camera ever sees them.
                for (int i = 0; i < 6; i++) if (_faceRenderers[i] != null) _faceRenderers[i].enabled = true;
                _cam.Render();
                for (int i = 0; i < 6; i++) if (_faceRenderers[i] != null) _faceRenderers[i].enabled = false;
            }
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

            // Pane over the opening: a free object in world space (not parented, so the prefab's scale cannot touch it).
            _pane = new GameObject("LivePortals_Pane");
            _pane.AddComponent<MeshFilter>().sharedMesh = _quad;
            _paneRenderer = _pane.AddComponent<MeshRenderer>();
            _paneMat = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
            _paneMat.mainTexture = _rt;
            _paneRenderer.sharedMaterial = _paneMat;
            _paneRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _paneRenderer.receiveShadows = false;
            _paneRenderer.enabled = false;

            // Capture meshes around an anchor that sits on the eye, oriented like the partner portal.
            _anchor = new GameObject("LivePortals_Capture");
            for (int i = 0; i < 6; i++)
            {
                var f = new GameObject("Face" + i);
                f.layer = Plugin.FaceLayer;
                f.transform.SetParent(_anchor.transform, false);
                f.transform.localRotation = Capture.FaceRotations[i];
                _faceFilters[i] = f.AddComponent<MeshFilter>();
                var mr = f.AddComponent<MeshRenderer>();
                _faceMats[i] = new Material(FindShader("Sprites/Default", "Unlit/Transparent", "Unlit/Texture"));
                mr.sharedMaterial = _faceMats[i];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;
                _faceRenderers[i] = mr;
            }

            // Window camera: draws the current sky like the game's sky camera, then the capture meshes.
            var camGo = new GameObject("LivePortals_WindowCamera");
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false;
            var sky = gc.m_skyCamera;
            int skyOnly = sky != null ? (sky.cullingMask & ~gc.m_camera.cullingMask) : 0;
            if (skyOnly == 0)
            {
                int l = LayerMask.NameToLayer("skybox");
                if (l >= 0) skyOnly = 1 << l;
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
            _cam.depthTextureMode = DepthTextureMode.None;
            _cam.nearClipPlane = 0.05f;
            float need = Plugin.DepthRange.Value * 2f;
            if (_cam.farClipPlane < need) _cam.farClipPlane = need;
            _cam.targetTexture = _rt;
            _cam.rect = new Rect(0f, 0f, 1f, 1f);
            _cam.orthographic = false;

            var lightGo = new GameObject("LivePortals_Light");
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Spot;
            _light.spotAngle = 150f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        private void ApplyCapture()
        {
            if (_cap == null) return;
            for (int i = 0; i < 6; i++)
            {
                if (_faceMats[i] != null) _faceMats[i].mainTexture = _cap.Faces[i];
                if (_faceMeshes[i] != null) Destroy(_faceMeshes[i]);
                _faceMeshes[i] = BuildFaceMesh(_cap.Depth[i], _cap.DepthSize, _cap.DepthRange);
                if (_faceFilters[i] != null) _faceFilters[i].sharedMesh = _faceMeshes[i];
            }
            _tintTimer = 0f;
        }

        /// <summary>
        /// A grid over the 90-degree face, each vertex pushed out along its view ray to the captured view depth.
        /// Direction for (u,v) is ((u-0.5)*2, (v-0.5)*2, 1) in the face's frame; times the view depth z that puts
        /// the vertex exactly where the captured surface was. Sky vertices sit at DepthRange.
        /// </summary>
        private static Mesh BuildFaceMesh(float[] depth, int n, float range)
        {
            var m = new Mesh { name = "LivePortals_Face" };
            if (depth == null || n < 2)
            {
                // No depth: a flat face at the range.
                return FlatFace(range);
            }
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var cols = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                float v = y / (float)(n - 1);
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)(n - 1);
                    float z = Mathf.Clamp(depth[y * n + x], 0.2f, range);
                    verts[y * n + x] = new Vector3((u - 0.5f) * 2f * z, (v - 0.5f) * 2f * z, z);
                    uvs[y * n + x] = new Vector2(u, v);
                    cols[y * n + x] = new Color32(255, 255, 255, 255);
                }
            }
            var tris = new int[(n - 1) * (n - 1) * 6];
            int k = 0;
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                {
                    int i0 = y * n + x, i1 = i0 + 1, i2 = i0 + n, i3 = i2 + 1;
                    tris[k++] = i0; tris[k++] = i2; tris[k++] = i1;
                    tris[k++] = i1; tris[k++] = i2; tris[k++] = i3;
                }
            m.indexFormat = n * n > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.vertices = verts; m.uv = uvs; m.colors32 = cols; m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        private static Mesh FlatFace(float range)
        {
            var m = new Mesh { name = "LivePortals_FlatFace" };
            float s = range * 1.004f;
            m.vertices = new[] { new Vector3(-s, -s, range), new Vector3(s, -s, range), new Vector3(s, s, range), new Vector3(-s, s, range) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        private void ReleaseCapture()
        {
            if (_cap != null) { _cap.Destroy(); _cap = null; }
            for (int i = 0; i < 6; i++)
            {
                if (_faceMats[i] != null) _faceMats[i].mainTexture = null;
                if (_faceMeshes[i] != null) { Destroy(_faceMeshes[i]); _faceMeshes[i] = null; }
                if (_faceFilters[i] != null) _faceFilters[i].sharedMesh = null;
            }
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
