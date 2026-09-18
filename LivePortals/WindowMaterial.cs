using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivePortals
{
    /// <summary>
    /// The material the reliefs are drawn with, and the proof that it works. The reliefs of several viewpoints
    /// overlap everywhere, so the window is only right if the material (a) depth-tests and depth-writes,
    /// (b) really discards texels below its cut-off instead of blending them, (c) draws texels above the cut-off
    /// opaque, and (d) shows the texture as it is, unlit. No shader of ours can be shipped, so it has to be one the
    /// game has loaded.
    ///
    /// History: 0.4.0 to 0.8.3 asked for "Particles/Standard Unlit" in cut-out mode. That shader is not in Valheim's
    /// build; Shader.Find returned null and the code quietly fell back to "Sprites/Default": alpha-blended, no depth
    /// write. Every layer of every viewpoint was composited in draw order, which is the "background in front of the
    /// foreground" of every test since 0.6.0. None of Unity's stock cut-out shaders are in the build either.
    ///
    /// So: on first use, every loaded shader with a main texture and a cut-off is tried, plain and with the picture
    /// as emission over black albedo (which makes a lit shader unlit), on the camera's own rendering path and on the
    /// forward path. Each try renders two quads into a tiny texture in three arrangements and reads the centre pixel
    /// back. The first set-up that passes is used; the log lists what was tried. If nothing passes, DepthWorks is
    /// false and the window falls back to one viewpoint drawn back to front with the sprite shader.
    /// </summary>
    internal static class WindowMaterial
    {
        private class Setup
        {
            public Shader Shader;
            public bool Emissive, Forward, HasCull;
            public string EmissionMap, EmissionColor; // whatever this shader calls them
            public override string ToString() => $"{Shader.name}{(Emissive ? " (emissive)" : "")}{(Forward ? ", forward path" : "")}";
        }

        private static bool _tested;
        private static Setup _setup;
        private static Shader _addShader;
        private static string _addColorProp;
        private static float _addGain = 1f;
        /// <summary>An additive, depth-tested shader was found: the local-light layer can be drawn on top of the reliefs.</summary>
        internal static bool AdditiveWorks => _addShader != null;

        /// <summary>Material that adds tex (the local light) onto whatever the relief already shows, untinted.</summary>
        internal static Material MakeAdditive(Texture tex)
        {
            var m = new Material(_addShader) { mainTexture = tex };
            if (_addColorProp != null) m.SetColor(_addColorProp, new Color(_addGain, _addGain, _addGain, 1f));
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", (int)CullMode.Off);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            m.renderQueue = (int)RenderQueue.Transparent + 5;
            return m;
        }
        internal static bool DepthWorks => _setup != null;
        internal static bool DoubleSidedMeshes => _setup != null && !_setup.HasCull;
        internal static string Summary { get; private set; } = "untested";

        // Tried first, in this order, when loaded: plain geometry shaders of the game without vertex animation.
        private static readonly string[] Preferred = { "Custom/Creature", "Custom/Player", "Custom/Piece", "Custom/StaticRock", "Standard", "Unlit/Transparent Cutout", "Particles/Standard Unlit" };

        /// <summary>Apply the rendering path the chosen set-up needs to a window camera.</summary>
        internal static void ApplyTo(Camera cam)
        {
            if (_setup != null && _setup.Forward) cam.renderingPath = RenderingPath.Forward;
            cam.useOcclusionCulling = false; // nothing baked for a camera looking through a hole with its own projection
        }

        /// <summary>
        /// Material for the guesses (skirts, far shell): unlit, blended, no depth write, drawn before the reliefs so
        /// whatever the capture really saw always paints over them. Up to 0.9.3 that took a second camera pass
        /// (a depth clear between the skirts and the reliefs); one pass in which the skirts write no depth is the
        /// same picture at half the camera work.
        /// </summary>
        internal static Material MakeUnder(Texture tex, int order)
        {
            var s = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
            if (s == null) return Make(tex, Layers.CutoffAll, order);
            var m = new Material(s) { mainTexture = tex, color = Color.white };
            m.renderQueue = (int)RenderQueue.AlphaTest - 20 + order;
            return m;
        }

        internal static Material Make(Texture tex, float cutoff, int order)
        {
            if (_setup == null)
            {
                // Last resort: blended, no depth. Only draw order decides what is in front.
                var s = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Unlit/Texture");
                var fm = new Material(s) { mainTexture = tex };
                fm.renderQueue = (int)RenderQueue.Transparent + order;
                return fm;
            }
            var m = new Material(_setup.Shader);
            Configure(m, _setup, tex, cutoff);
            m.renderQueue = (int)RenderQueue.AlphaTest + order;
            return m;
        }

        // ------------------------------------------------------------------
        // The pane: the surface in the ring that shows the window texture.
        //
        // As a sprite it was transparent and wrote no depth, so to everything in the game that works from the depth
        // buffer the ring was empty: the mist of foggy weather was drawn at the thickness of the hill and sky behind
        // the portal (a white window with the real grass behind it showing as dark blade shapes, where the mist was
        // thin enough to see the pane), and fog, depth of field and ambient occlusion treated it the same way.
        // With the tested relief shader the pane is a solid, unlit, depth-writing surface instead. It can no longer
        // fade in by transparency, so it dissolves in: its cut-off runs against a noise texture.
        private static Texture2D _noise;

        /// <summary>The black, depth-writing, dissolving plug behind the picture, or null when no tested shader is available (then the caller keeps the sprite alone).</summary>
        internal static Material MakePlug()
        {
            if (_setup == null || !_setup.Emissive) return null;
            if (_noise == null)
            {
                _noise = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point, name = "LivePortals_Dissolve" };
                // The alpha is the order in which texels appear: the higher, the earlier. Mostly from the rim inward
                // (the pane's UVs run 0..1 across the ring, so the rim is at radius 0.5), with enough randomness
                // that the front is ragged rather than a closing iris.
                var px = new Color32[64 * 64];
                var rng = new System.Random(7);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        float dx = (x + 0.5f) / 64f - 0.5f, dy = (y + 0.5f) / 64f - 0.5f;
                        float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                        float order = 0.65f * r + 0.35f * (float)rng.NextDouble();
                        px[y * 64 + x] = new Color32(0, 0, 0, (byte)(1 + Mathf.RoundToInt(order * 254f)));
                    }
                _noise.SetPixels32(px);
                _noise.Apply(false, false);
            }
            var m = new Material(_setup.Shader);
            Configure(m, _setup, _noise, 0f);
            m.SetTexture(_setup.EmissionMap, null);
            m.SetColor(_setup.EmissionColor, Color.black);
            return m;
        }

        /// <summary>Point the pane at a (re)created window texture.</summary>
        internal static void SetPaneTexture(Material m, Texture rt)
        {
            if (m == null) return;
            if (_setup != null && _setup.Emissive && m.shader == _setup.Shader) m.SetTexture(_setup.EmissionMap, rt);
            else m.mainTexture = rt;
        }

        /// <summary>visible 0..1: how much of the pane has dissolved in.</summary>
        internal static void SetPaneVisible(Material m, float visible)
        {
            // Done by two thirds of the way in: the last few stray holes, with the real sky behind them, read as a
            // glitch rather than as a transition.
            visible = Mathf.Clamp01(visible * 1.5f);
            m.SetFloat("_Cutoff", visible >= 0.999f ? 0f : Mathf.Clamp01(1f - visible) + 0.004f);
        }

        internal static void SetTint(Material m, Color tint)
        {
            tint.a = 1f;
            if (_setup != null && _setup.Emissive && m.shader == _setup.Shader) m.SetColor(_setup.EmissionColor, tint);
            else if (m.HasProperty("_Color")) m.color = tint;
        }

        private static void Configure(Material m, Setup s, Texture tex, float cutoff)
        {
            m.mainTexture = tex;
            m.SetFloat("_Cutoff", cutoff);
            m.renderQueue = (int)RenderQueue.AlphaTest;
            m.EnableKeyword("_ALPHATEST_ON");
            m.SetOverrideTag("RenderType", "TransparentCutout");
            if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 1f);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 1);
            if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)BlendMode.One);
            if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)BlendMode.Zero);
            if (s.HasCull) m.SetInt("_Cull", (int)CullMode.Off);
            // Whatever the shader does to geometry or surface over time or weather (sway, push, ripple, snow, moss,
            // rain, wetness, damage): off. The relief is a picture.
            var sh = s.Shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                var type = sh.GetPropertyType(i);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range) continue;
                string n = sh.GetPropertyName(i);
                foreach (string word in Quiet)
                    if (n.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0) { m.SetFloat(n, 0f); break; }
            }
            if (s.Emissive)
            {
                // Unlit through a lit shader: black albedo (its alpha kept for the cut-off), the picture as emission.
                if (m.HasProperty("_Color")) m.SetColor("_Color", new Color(0f, 0f, 0f, 1f));
                m.SetTexture(s.EmissionMap, tex);
                m.SetColor(s.EmissionColor, Color.white);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                foreach (string f in new[] { "_Glossiness", "_Metallic", "_Smoothness", "_SpecularHighlights", "_GlossyReflections", "_GlossMapScale" })
                    if (m.HasProperty(f)) m.SetFloat(f, 0f);
                if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.black);
                m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            }
            else if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        }

        private static readonly string[] Quiet = { "sway", "ripple", "push", "wind", "wave", "wobble", "snow", "moss", "rain", "wet", "damage", "dirt", "frost" };

        // ------------------------------------------------------------------
        internal static void EnsureTested(Camera windowCam)
        {
            if (_tested) return;
            _tested = true;
            var log = new System.Text.StringBuilder();
            var candidates = new List<Shader>();
            var all = Resources.FindObjectsOfTypeAll<Shader>();
            foreach (string name in Preferred)
                foreach (var s in all)
                    if (s != null && s.name == name && Usable(s) && !candidates.Contains(s)) candidates.Add(s);
            var rest = new List<Shader>();
            foreach (var s in all)
                if (s != null && Usable(s) && !candidates.Contains(s) && !s.name.StartsWith("Hidden/")) rest.Add(s);
            // Shaders that move their vertices last.
            rest.Sort((a, b) => Wobbly(a) != Wobbly(b) ? (Wobbly(a) ? 1 : -1) : string.CompareOrdinal(a.name, b.name));
            candidates.AddRange(rest);
            var names = new System.Text.StringBuilder();
            foreach (var s in candidates)
            {
                names.Append(' ').Append(s.name).Append(Find(s, "emiss", ShaderPropertyType.Texture) != null ? "[E]" : "").Append(s.FindPropertyIndex("_Cull") >= 0 ? "[C]" : "");
                if (s.name.StartsWith("Custom/") && names.Length < 6000)
                {
                    // Texture and colour properties of the game's own shaders, for the next person reading this log.
                    names.Append('(');
                    for (int i = 0; i < s.GetPropertyCount(); i++)
                    {
                        var t = s.GetPropertyType(i);
                        if (t == ShaderPropertyType.Texture || t == ShaderPropertyType.Color) names.Append(s.GetPropertyName(i)).Append(' ');
                    }
                    names.Append(')');
                }
                names.Append(';');
            }

            var rt = new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32);
            rt.Create();
            var read = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            Texture2D red = Solid(new Color32(255, 0, 0, 255)), green = Solid(new Color32(0, 255, 0, 255));
            Texture2D clear = Solid(new Color32(255, 0, 0, 0)), filled = Solid(new Color32(255, 0, 0, Layers.FilledAlpha));
            var camGo = new GameObject("LivePortals_SelfTestCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.CopyFrom(windowCam);
            cam.enabled = false;
            cam.ResetProjectionMatrix();
            cam.ResetWorldToCameraMatrix();
            cam.fieldOfView = 40f; cam.aspect = 1f; cam.nearClipPlane = 0.1f; cam.farClipPlane = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 1f, 1f);
            cam.cullingMask = 1 << Plugin.FaceLayer;
            cam.targetTexture = rt;
            cam.allowHDR = false; cam.allowMSAA = false;
            // Far from anything the game draws, so only the two test quads can be in view.
            cam.transform.SetPositionAndRotation(windowCam.transform.position + Vector3.up * 3000f, Quaternion.identity);
            var quad = DoubleSidedQuad();
            var nearGo = TestQuad(quad, cam.transform.position + Vector3.forward * 2f);
            var farGo = TestQuad(quad, cam.transform.position + Vector3.forward * 5f);
            bool fog = RenderSettings.fog;
            RenderSettings.fog = false;
            int tries = 0;
            try
            {
                var defaultPath = cam.renderingPath;
                foreach (var shader in candidates)
                {
                    if (_setup != null || tries >= 160) break;
                    // A lit shader shows the picture as it is only through emission; plain, it would pass this test in
                    // daylight and then light the far side with the viewer's sun.
                    string emissionMap = Find(shader, "emiss", ShaderPropertyType.Texture), emissionColor = Find(shader, "emiss", ShaderPropertyType.Color);
                    bool canEmit = emissionMap != null && emissionColor != null;
                    bool lit = canEmit || shader.FindPropertyIndex("_BumpMap") >= 0 || shader.FindPropertyIndex("_Glossiness") >= 0 || shader.FindPropertyIndex("_Metallic") >= 0 || shader.FindPropertyIndex("_SpecColor") >= 0;
                    if (lit && !canEmit) continue;
                    foreach (bool emissive in new[] { canEmit })
                    {
                        if (_setup != null) break;
                        // Forward first: a camera pass on the deferred path costs a G-buffer, a depth copy and a lighting
                        // pass per light, none of which an unlit picture needs.
                        foreach (bool forward in new[] { true, false })
                        {
                            var setup = new Setup { Shader = shader, Emissive = emissive, Forward = forward, HasCull = shader.FindPropertyIndex("_Cull") >= 0, EmissionMap = emissionMap, EmissionColor = emissionColor };
                            cam.renderingPath = forward ? RenderingPath.Forward : defaultPath;
                            var a = new Material(shader); var b = new Material(shader);
                            nearGo.GetComponent<MeshRenderer>().sharedMaterial = a;
                            farGo.GetComponent<MeshRenderer>().sharedMaterial = b;
                            tries++;
                            try
                            {
                                // 1. Depth: near red drawn first, far green drawn after it. Red must stay.
                                Configure(a, setup, red, 0.5f); Configure(b, setup, green, 0.5f);
                                a.renderQueue = 2450; b.renderQueue = 2451;
                                Color32 depth = Shoot(cam, rt, read, nearGo, farGo);
                                // 2. Cut-out: an invisible near quad drawn first must neither show nor block the far one.
                                Configure(a, setup, clear, 0.5f); a.renderQueue = 2450; b.renderQueue = 2451;
                                Color32 cut = Shoot(cam, rt, read, nearGo, farGo);
                                // 3. Opaque above the cut-off: a filled-in texel (alpha 160) over green must be pure red, not a blend.
                                Configure(a, setup, filled, Layers.CutoffAll); a.renderQueue = 2451; b.renderQueue = 2450;
                                Color32 solid = Shoot(cam, rt, read, nearGo, farGo);
                                bool ok = IsRed(depth) && IsGreen(cut) && IsRed(solid);
                                if (ok || tries <= 24) log.Append($" {setup}: {Hex(depth)} {Hex(cut)} {Hex(solid)}{(ok ? " OK" : "")};");
                                if (ok) _setup = setup;
                            }
                            catch (System.Exception e) { log.Append($" {setup}: threw {e.Message};"); }
                            Object.Destroy(a); Object.Destroy(b);
                            if (_setup != null) break;
                        }
                    }
                }
            // ---- An additive shader for the local-light layer: adds its texture onto what is already drawn, adds
            // nothing where the texture is black, and is hidden behind nearer surfaces. ----
            if (_setup != null)
            {
                try
                {
                    var addCands = new List<Shader>();
                    foreach (var sh in all)
                        if (sh != null && sh.isSupported && sh.FindPropertyIndex("_MainTex") >= 0 && !sh.name.StartsWith("Hidden/")) addCands.Add(sh);
                    int Score(Shader sh) { string n = sh.name.ToLowerInvariant(); return n.Contains("additive") ? 0 : n.Contains("particle") ? 1 : n.Contains("unlit") ? 2 : 3; }
                    addCands.Sort((x, y) => Score(x) != Score(y) ? Score(x).CompareTo(Score(y)) : string.CompareOrdinal(x.name, y.name));
                    Texture2D black = Solid(new Color32(0, 0, 0, 255)), grey = Solid(new Color32(0, 128, 0, 255));
                    var opaque = new Material(_setup.Shader);
                    var nearMr = nearGo.GetComponent<MeshRenderer>(); var farMr = farGo.GetComponent<MeshRenderer>();
                    cam.renderingPath = _setup.Forward ? RenderingPath.Forward : defaultPath;
                    int addTries = 0;
                    foreach (var sh in addCands)
                    {
                        if (_addShader != null || addTries >= 120) break;
                        addTries++;
                        var a = new Material(sh);
                        try
                        {
                            string prop = a.HasProperty("_Color") ? "_Color" : a.HasProperty("_TintColor") ? "_TintColor" : null;
                            if (prop != null) a.SetColor(prop, Color.white);
                            if (a.HasProperty("_Cull")) a.SetInt("_Cull", (int)CullMode.Off);
                            a.renderQueue = (int)RenderQueue.Transparent;
                            // 1. Green added over red must give yellow.
                            Configure(opaque, _setup, red, 0.5f); opaque.renderQueue = 2450;
                            a.mainTexture = green;
                            nearMr.sharedMaterial = a; farMr.sharedMaterial = opaque;
                            Color32 sum = Shoot(cam, rt, read, nearGo, farGo);
                            // 2. Black must add nothing.
                            a.mainTexture = black;
                            Color32 none = Shoot(cam, rt, read, nearGo, farGo);
                            // 3. Behind an opaque red quad it must not show.
                            a.mainTexture = green;
                            nearMr.sharedMaterial = opaque; farMr.sharedMaterial = a;
                            Color32 hidden = Shoot(cam, rt, read, nearGo, farGo);
                            bool ok = sum.r > 150 && sum.g > 150 && IsRed(none) && IsRed(hidden);
                            if (ok)
                            {
                                // 4. Gain: half-bright green over black should read half bright.
                                Configure(opaque, _setup, black, 0.5f);
                                a.mainTexture = grey;
                                nearMr.sharedMaterial = a; farMr.sharedMaterial = opaque;
                                Color32 g = Shoot(cam, rt, read, nearGo, farGo);
                                float gain = g.g > 8 ? 128f / g.g : 1f;
                                if (prop == null && Mathf.Abs(gain - 1f) > 0.25f) ok = false;
                                else { _addShader = sh; _addColorProp = prop; _addGain = Mathf.Clamp(gain, 0.2f, 5f); }
                                log.Append($" additive {sh.name}: {Hex(sum)} {Hex(none)} {Hex(hidden)} grey {g.g}{(ok ? " OK" : " (no gain control)")};");
                            }
                            else if (addTries <= 12) log.Append($" additive {sh.name}: {Hex(sum)} {Hex(none)} {Hex(hidden)};");
                        }
                        catch (System.Exception e) { log.Append($" additive {sh.name}: threw {e.Message};"); }
                        Object.Destroy(a);
                    }
                    nearMr.sharedMaterial = null; farMr.sharedMaterial = null;
                    Object.Destroy(opaque); Object.Destroy(black); Object.Destroy(grey);
                    log.Append(_addShader != null ? $" local-light layer via {_addShader.name} (gain {_addGain:0.00});" : " NO additive shader passed: torchlight will follow the time of day;");
                }
                catch (System.Exception e) { log.Append(" additive self-test threw: " + e.Message); }
            }
            }
            catch (System.Exception e) { log.Append(" self-test threw: " + e.Message); }
            finally
            {
                RenderSettings.fog = fog;
                cam.targetTexture = null;
                Object.Destroy(nearGo); Object.Destroy(farGo); Object.Destroy(quad); Object.Destroy(camGo);
                Object.Destroy(red); Object.Destroy(green); Object.Destroy(clear); Object.Destroy(filled); Object.Destroy(read);
                rt.Release(); Object.Destroy(rt);
            }
            Summary = _setup != null ? _setup.ToString() : "none (sprite fallback, one viewpoint)";
            string head = $"LivePortals: window material self-test: {(_setup != null ? "using " + Summary : "NOTHING depth-tests and cuts out; falling back to one viewpoint drawn back to front")}. {tries} set-ups tried of {candidates.Count} shaders (results are depth/cut/solid, wanted ff0000 00ff00 ff0000):{log}";
            if (_setup != null) Plugin.Log.LogInfo(head); else Plugin.Log.LogWarning(head);
            Plugin.Log.LogInfo($"LivePortals: loaded shaders with a main texture and a cut-off ([E] emission map, [C] cull switch):{names}");
        }

        /// <summary>Name of the first property of that type whose name contains the word, or null.</summary>
        private static string Find(Shader s, string word, ShaderPropertyType type)
        {
            for (int i = 0; i < s.GetPropertyCount(); i++)
                if (s.GetPropertyType(i) == type && s.GetPropertyName(i).IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0) return s.GetPropertyName(i);
            return null;
        }

        private static bool Usable(Shader s)
        {
            return s.isSupported && s.FindPropertyIndex("_MainTex") >= 0 && s.FindPropertyIndex("_Cutoff") >= 0;
        }

        private static bool Wobbly(Shader s)
        {
            string n = s.name.ToLowerInvariant();
            return n.Contains("veg") || n.Contains("grass") || n.Contains("tree") || n.Contains("leaf") || n.Contains("flag") || n.Contains("cloth") || n.Contains("water") || n.Contains("particle");
        }

        private static Color32 Shoot(Camera cam, RenderTexture rt, Texture2D read, GameObject a, GameObject b)
        {
            var ra = a.GetComponent<MeshRenderer>(); var rb = b.GetComponent<MeshRenderer>();
            ra.enabled = rb.enabled = true;
            cam.Render();
            ra.enabled = rb.enabled = false;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, 16, 16), 0, 0, false);
            RenderTexture.active = prev;
            return read.GetPixel(8, 8);
        }

        private static bool IsRed(Color32 c) => c.r > 200 && c.g < 60 && c.b < 60;
        private static bool IsGreen(Color32 c) => c.g > 200 && c.r < 60 && c.b < 60;
        private static string Hex(Color32 c) => c.r.ToString("x2") + c.g.ToString("x2") + c.b.ToString("x2");

        private static Texture2D Solid(Color32 c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.SetPixels32(new[] { c, c, c, c });
            t.Apply(false, false);
            return t;
        }

        private static GameObject TestQuad(Mesh quad, Vector3 pos)
        {
            var go = new GameObject("LivePortals_SelfTestQuad");
            go.layer = Plugin.FaceLayer;
            go.transform.position = pos;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.enabled = false; // the game's own camera draws this layer too: only ever on during our render
            return go;
        }

        private static Mesh DoubleSidedQuad()
        {
            var m = new Mesh { name = "LivePortals_SelfTestQuad" };
            m.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            m.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3, 0, 1, 2, 1, 3, 2 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
