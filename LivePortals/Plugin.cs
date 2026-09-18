using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LayerTest")]

namespace LivePortals
{
    /// <summary>
    /// Live Portals: see through a connected portal. Every time you leave through or arrive at a portal the mod
    /// captures the view from that portal looking outward and the paired portal shows it as a window with real
    /// parallax, the current sky behind it, and its brightness matched to the time of day.
    /// Captures are stored on this PC only. Nothing is loaded on the far side; it is a picture, not a live feed.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.maxst.liveportals";
        public const string NAME = "Immersive Portals";
        public const string VERSION = "0.9.25";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        private Harmony _harmony;

        // ---- Config ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> CaptureResolution;
        internal static ConfigEntry<int> CapturePoints;
        internal static ConfigEntry<int> WindowResolution;
        internal static ConfigEntry<float> DepthRange;
        internal static ConfigEntry<int> MeshGrid;
        internal static ConfigEntry<float> RangeMultiplier;
        internal static ConfigEntry<float> FullMultiplier;
        internal static ConfigEntry<float> PaneWidth;
        internal static ConfigEntry<float> PaneHeight;
        internal static ConfigEntry<float> RingCenterHeight;
        internal static ConfigEntry<float> RingCenterOffset;
        internal static ConfigEntry<float> PaneForwardOffset;
        internal static ConfigEntry<bool> PaneRound;
        internal static ConfigEntry<bool> HideSwirl;
        internal static ConfigEntry<PaneStyleOption> PaneStyle;
        internal static ConfigEntry<bool> TuneKeys;
        internal static ConfigEntry<bool> GlassTest;
        internal static ConfigEntry<bool> ArrivalViewBothSides;

        internal static ConfigEntry<float> OtherPaneWidth;
        internal static ConfigEntry<float> OtherPaneHeight;
        internal static ConfigEntry<float> OtherCenterHeight;

        /// <summary>World position of the centre of the portal's opening, where the pane sits and captures are taken from.</summary>
        internal static Vector3 RingCenter(TeleportWorld tw) => PortalShape.Of(tw).Centre;
        internal static ConfigEntry<bool> CaptureOnDeparture;
        internal static ConfigEntry<bool> CaptureOnArrival;
        internal static ConfigEntry<float> DepartureDelay;
        internal static ConfigEntry<float> ArrivalDelay;
        internal static ConfigEntry<int> CaptureFacesPerFrame;
        internal static ConfigEntry<string> CaptureFolder;
        internal static ConfigEntry<bool> AsyncReadback;
        internal static ConfigEntry<bool> LiveSky;
        internal static ConfigEntry<float> ToneMatch;
        internal static ConfigEntry<float> CaptureExposure;
        internal static ConfigEntry<bool> CaptureFog;
        internal static ConfigEntry<bool> CaptureLocalLight;
        internal static ConfigEntry<bool> CaptureSkyLight;
        internal static ConfigEntry<bool> CaptureFlames;
        internal static ConfigEntry<bool> LiveFire;
        internal static ConfigEntry<int> LiveFireMax;
        internal static ConfigEntry<float> FlameBloom;
        internal static ConfigEntry<bool> CaptureFlameDepth;
        internal static ConfigEntry<float> FlameMaxSize;
        internal static ConfigEntry<float> GrassGap;
        internal static ConfigEntry<float> PortalLight;
        internal static ConfigEntry<float> PortalLightRange;
        internal static ConfigEntry<int> MaxWindows;
        internal static ConfigEntry<int> MaxRendersPerFrame;
        internal static ConfigEntry<bool> RenderWhenStill;
        internal static ConfigEntry<float> SecondaryViewpointRange;
        internal static ConfigEntry<int> GrassMaxInstances;
        internal static ConfigEntry<bool> PerfLog;
        internal static ConfigEntry<float> CaptureFrameBudgetMs;
        internal static ConfigEntry<bool> HalfResSecondaries;
        internal static ConfigEntry<int> MaxWindowFps;
        internal static ConfigEntry<int> RenderEveryNFrames;
        internal static ConfigEntry<bool> DebugLog;

        /// <summary>Layer of the capture meshes. They are only enabled while a window camera renders, so it does not matter who else renders it.</summary>
        internal const int FaceLayer = 31;

        private float _scanTimer;
        internal static readonly List<TeleportWorld> Portals = new List<TeleportWorld>();
        private static bool _portalsTracked;
        private static TeleportWorld[] _scanned = new TeleportWorld[0];
        private static float _scannedAt = -100f;

        /// <summary>Every portal in the scene: the registered ones, or (if that could not be patched) a scan at most once a second.</summary>
        internal static IEnumerable<TeleportWorld> AllPortals()
        {
            if (_portalsTracked)
            {
                for (int i = Portals.Count - 1; i >= 0; i--) if (Portals[i] == null) Portals.RemoveAt(i);
                return Portals;
            }
            if (Time.time - _scannedAt > 1f) { _scannedAt = Time.time; _scanned = UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None); }
            return _scanned;
        }
        private readonly List<TeleportWorld> _scan = new List<TeleportWorld>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("1. General", "Enabled", true, "Master switch.");
            CaptureResolution = Config.Bind("1. General", "CaptureResolution", 768,
                new ConfigDescription("Pixels per cube face captured at a portal. 1024 costs almost twice the disk and memory of 768.",
                    new AcceptableValueRange<int>(128, 1024)));
            CapturePoints = Config.Bind("1. General", "CaptureViewpoints", 4,
                new ConfigDescription("Capture from this many points (the ring's centre, then above it, then right and left of it, then below). More points fill in what one viewpoint cannot see behind near things; each costs capture time, disk and memory. The extra points skip the faces looking back and up.",
                    new AcceptableValueRange<int>(1, 5)));
            WindowResolution = Config.Bind("1. General", "WindowResolution", 768,
                new ConfigDescription("Pixels of the texture each window is drawn into every frame.",
                    new AcceptableValueRange<int>(256, 2048)));
            DepthRange = Config.Bind("1. General", "DepthRange", 120f,
                new ConfigDescription("Metres of depth captured per pixel; anything farther (and the sky) sits at this distance. Larger = flatter far parallax, less stretch at edges.",
                    new AcceptableValueRange<float>(20f, 500f)));
            MeshGrid = Config.Bind("1. General", "MeshGrid", 128,
                new ConfigDescription("Cells per edge of each captured face's relief mesh. Silhouettes are cut per pixel by the textures regardless; this sets how finely surfaces follow the captured depth.",
                    new AcceptableValueRange<int>(32, 256)));
            RangeMultiplier = Config.Bind("2. Window", "RangeMultiplier", 4f,
                new ConfigDescription("The window starts to dissolve in at this many times the portal's activation range (5 m in vanilla, so 4 = 20 m).",
                    new AcceptableValueRange<float>(1f, 20f)));
            // Settings files written before 0.8.16 hold the old default of 4; Max asked for the transition to start
            // from twice as far. Done once, so a 4 chosen later on purpose stays.
            var configVersion = Config.Bind("1. General", "ConfigVersion", 0, "Internal: which defaults this file has been brought up to. Leave alone.");
            if (configVersion.Value < 1)
            {
                if (Mathf.Approximately(RangeMultiplier.Value, 4f)) RangeMultiplier.Value = 8f;
                configVersion.Value = 1;
            }
            // 0.9.2: Max asked for half the range again (8 was too far and too many windows at a hub).
            if (configVersion.Value < 2)
            {
                if (Mathf.Approximately(RangeMultiplier.Value, 8f)) RangeMultiplier.Value = 4f;
                configVersion.Value = 2;
            }
            FullMultiplier = Config.Bind("2. Window", "FullMultiplier", 1f,
                new ConfigDescription("The window is fully visible from this many times the activation range inward.",
                    new AcceptableValueRange<float>(0.2f, 10f)));
            PaneWidth = Config.Bind("2. Window", "PaneWidth", 2.7f,
                new ConfigDescription("Width of the window pane in metres (the portal's opening).", new AcceptableValueRange<float>(0.5f, 5f)));
            PaneHeight = Config.Bind("2. Window", "PaneHeight", 2.8f,
                new ConfigDescription("Height of the window pane in metres.", new AcceptableValueRange<float>(0.5f, 5f)));
            RingCenterHeight = Config.Bind("2. Window", "RingCenterHeight", 0f,
                new ConfigDescription("Height of the ring's centre above the portal's base, metres. 0 = take it from the portal model (1.64 m on the vanilla portal).",
                    new AcceptableValueRange<float>(0f, 4f)));
            RingCenterOffset = Config.Bind("2. Window", "RingCenterOffset", -0.35f,
                new ConfigDescription("Vertical nudge of the pane and capture point from the model's centre, metres (the vanilla ring sits a little below it).",
                    new AcceptableValueRange<float>(-1f, 1f)));
            OtherPaneWidth = Config.Bind("2. Window", "OtherPaneWidth", 0f,
                new ConfigDescription("Pane width for every portal that is not the wooden one (the stone portal, modded portals), metres. 0 = measure the opening from the portal's own colliders.",
                    new AcceptableValueRange<float>(0f, 12f)));
            OtherPaneHeight = Config.Bind("2. Window", "OtherPaneHeight", 0f,
                new ConfigDescription("Pane height for those portals. 0 = measured.", new AcceptableValueRange<float>(0f, 12f)));
            OtherCenterHeight = Config.Bind("2. Window", "OtherCenterHeight", 0f,
                new ConfigDescription("Height of the opening's centre above the base of those portals. 0 = measured.", new AcceptableValueRange<float>(0f, 12f)));
            PaneRound = Config.Bind("2. Window", "PaneRound", true, "Round pane (the ring's shape) instead of a square.");
            PaneStyle = Config.Bind("2. Window", "PaneStyle", PaneStyleOption.Sprite,
                "How the picture is put in the ring. Sprite (0.9.10+): a black depth plug with the picture drawn over it after the game's screen effects, which keeps it bright at night. Emissive (0.8.12 to 0.9.9): one solid surface showing the picture as emission; the game's ambient occlusion darkens it at night, but it is the long-tested one.");
            HideSwirl = Config.Bind("2. Window", "HideSwirl", false, "Switch the game's own swirl in the ring off while the window shows. Off: the swirl plays over the picture as in vanilla.");
            // 0.9.13 to 0.9.16 hid the swirl by default (a sorting problem since solved another way); files from then say true.
            if (configVersion.Value < 3) { HideSwirl.Value = false; configVersion.Value = 3; }
            // In a section of its own and off: up to 0.9.21 the numpad keys were on for everybody ("2. Window"), and
            // a stray numpad 0 or 5 took captures and wrote dumps. The old entry is simply no longer read.
            TuneKeys = Config.Bind("6. Debug", "TuneKeys", false,
                "Debug: numpad keys while in game: 8/2 ring height, 4/6 forward offset, 7/9 pane width, 1/3 pane height, . (period) toggles the glass test, 5 prints, saves, and dumps what every visible window drew (BepInEx/config/LivePortals/debug), 0 captures the nearest portal now. Values are saved to this file.");
            GlassTest = Config.Bind("2. Window", "GlassTest", false,
                "Diagnostic: each window shows its OWN portal's capture with no portal mapping, so the ring should look like a pane of glass onto the real surroundings. Capture with numpad 0 first.");
            ArrivalViewBothSides = Config.Bind("2. Window", "ArrivalViewBothSides", false,
                "The game always drops you at the partner's front, so show the partner's front view from both faces of a portal (mirrored from behind). Off = a physically consistent hole: back shows the partner's back.");
            PaneForwardOffset = Config.Bind("2. Window", "PaneForwardOffset", 0f,
                new ConfigDescription("Pane offset along the portal's forward axis, metres. Nudge if it fights the frame or the swirl.",
                    new AcceptableValueRange<float>(-1f, 1f)));
            CaptureOnDeparture = Config.Bind("3. Capture", "CaptureOnDeparture", true, "Capture the portal you leave through (feeds the window at its partner).");
            CaptureOnArrival = Config.Bind("3. Capture", "CaptureOnArrival", true, "Capture the portal you arrive at (feeds the window at the one you came from).");
            DepartureDelay = Config.Bind("3. Capture", "DepartureDelay", 0.8f,
                new ConfigDescription("Seconds after stepping in before the departure capture, so the screen is mostly black already.",
                    new AcceptableValueRange<float>(0f, 1.8f)));
            ArrivalDelay = Config.Bind("3. Capture", "ArrivalDelay", 0.15f,
                new ConfigDescription("Seconds after arrival before the capture, to let the area finish appearing while the screen is still dark.",
                    new AcceptableValueRange<float>(0f, 2f)));
            CaptureFolder = Config.Bind("3. Capture", "CaptureFolder", "",
                "Where captures are stored. Empty = a LivePortals folder in the game's own data folder (next to your worlds and characters), outside any mod-manager profile, so sharing a profile does not carry hundreds of megabytes of pictures.");
            CaptureFrameBudgetMs = Config.Bind("3. Capture", "CaptureFrameBudgetMs", 10f,
                new ConfigDescription("During a capture, no further cube face is started in a frame that has already spent this long on faces, milliseconds (one face always is). Lower = smoother frames and a longer capture.",
                    new AcceptableValueRange<float>(0f, 100f)));
            CaptureFacesPerFrame = Config.Bind("3. Capture", "CaptureFacesPerFrame", 2,
                new ConfigDescription("Cube faces rendered per frame during a capture (four renders each). Fewer = smoother frames, more frames for the capture, and things that move can differ between faces.",
                    new AcceptableValueRange<int>(1, 24)));
            AsyncReadback = Config.Bind("3. Capture", "AsyncReadback", true,
                "Read the capture's pixels back from the GPU without waiting for it (checked against a plain read once per session; falls back by itself if it does not match).");
            LiveSky = Config.Bind("4. Look", "LiveSky", true, "Draw the current sky behind the capture where the capture saw sky, so day and night through the window follow the clock.");
            ToneMatch = Config.Bind("4. Look", "ToneMatch", 1f,
                new ConfigDescription("How strongly the capture's brightness and colour follow the current sun, ambient and fog compared with when it was taken. 0 = show it as captured.",
                    new AcceptableValueRange<float>(0f, 1f)));
            CaptureExposure = Config.Bind("4. Look", "CaptureExposure", 1f,
                new ConfigDescription("Overall brightness multiplier for captures (they are taken without the game's post-processing).",
                    new AcceptableValueRange<float>(0.2f, 3f)));
            CaptureFog = Config.Bind("4. Look", "CaptureFog", true,
                "Capture with the game's own distance fog and ambient occlusion (its post-processing stack, everything else in it switched off). Off = raw geometry colours, which look too crisp and bright at a distance.");
            CaptureLocalLight = Config.Bind("4. Look", "CaptureLocalLight", true,
                "Also capture what torches, fires and glowing things alone contribute, and show that part untinted: torchlight in the window then stays as bright at night as by day, while sunlit parts still follow the time of day.");
            CaptureSkyLight = Config.Bind("4. Look", "CaptureSkyLight", true,
                "Also capture what the sky's light alone contributes (one more render per face of the main viewpoint). The window then dims the sunlit part of the picture with the sun and the sky-lit part with the ambient light, which at night falls far less: shaded ground, forests and interiors no longer go black at dusk. Needs a fresh capture.");
            CaptureFlames = Config.Bind("4. Look", "CaptureFlames", true,
                "Keep the flames of torches and fires in the capture (other particle effects, like smoke and weather, are left out).");
            LiveFire = Config.Bind("4. Look", "LiveFire", true,
                "Fires, torches, furnaces and cooking places somebody built are left out of the capture and the window plays the game's own flame effects at their places instead: real flames in the right spot from every angle, burning, bright at night. Needs a fresh capture of the portal. Off: small flames are painted into the picture (CaptureFlames).");
            LiveFireMax = Config.Bind("5. Performance", "LiveFireMax", 32,
                new ConfigDescription("Most flame effects one window plays (the nearest to the far portal are kept).",
                    new AcceptableValueRange<int>(0, 200)));
            FlameBloom = Config.Bind("4. Look", "FlameBloom", 4f,
                new ConfigDescription("How far above white the flames in a window are pushed, so the game's bloom glows around them as it does around real fires (the window's picture itself cannot hold anything brighter than white). 0 = off. Costs a third, small render per redraw of a window with fires in it.",
                    new AcceptableValueRange<float>(0f, 6f)));
            // 0.9.22 shipped 2; Max: "the bloom needs cranking up".
            if (configVersion.Value < 4) { if (Mathf.Approximately(FlameBloom.Value, 2f)) FlameBloom.Value = 4f; configVersion.Value = 4; }
            FlameMaxSize = Config.Bind("4. Look", "FlameMaxSize", 1f,
                new ConfigDescription("Largest flame effect kept in the capture, metres across. Torch flames are well under a metre; a hearth or bonfire is bigger and, painted onto the wall behind it, comes out as stretched copies, so it is left out (its light stays).",
                    new AcceptableValueRange<float>(0.2f, 5f)));
            CaptureFlameDepth = Config.Bind("4. Look", "CaptureFlameDepth", false,
                "Experimental: give flames the depth of their fire so a hearth sits in the middle of its room instead of on the wall behind it. Off: flames are painted on whatever is behind them (right for wall torches, wrong for a fire in the open). On, it can leave dark blocks next to free-standing torches.");
            GrassGap = Config.Bind("4. Look", "GrassGap", 0.75f,
                new ConfigDescription("Grass closer than this to the far portal's centre is not drawn in the window, metres (blades standing in the ring itself).",
                    new AcceptableValueRange<float>(0f, 10f)));
            PortalLight = Config.Bind("4. Look", "PortalLight", 1f,
                new ConfigDescription("Light spilling out of the window when the far side is brighter than here. 0 = off.",
                    new AcceptableValueRange<float>(0f, 5f)));
            PortalLightRange = Config.Bind("4. Look", "PortalLightRange", 8f,
                new ConfigDescription("Reach of that light in metres.", new AcceptableValueRange<float>(1f, 30f)));
            MaxWindows = Config.Bind("5. Performance", "MaxWindows", 8,
                new ConfigDescription("Most windows kept loaded at once (nearest first). Loaded windows beyond SecondaryViewpointRange hold only their primary viewpoint, so this mostly costs memory; drawing is limited by MaxRendersPerFrame.", new AcceptableValueRange<int>(1, 16)));
            MaxRendersPerFrame = Config.Bind("5. Performance", "MaxRendersPerFrame", 2,
                new ConfigDescription("Most windows redrawn in one frame, nearest first; the others keep their last picture until their turn. This bounds the cost at a hub whatever the number of portals.", new AcceptableValueRange<int>(1, 8)));
            RenderEveryNFrames = Config.Bind("5. Performance", "RenderEveryNFrames", 1,
                new ConfigDescription("Redraw the nearest window every N frames (others every 3N). 2 halves the cost with a barely visible lag.",
                    new AcceptableValueRange<int>(1, 4)));
            RenderWhenStill = Config.Bind("5. Performance", "RenderWhenStill", false,
                "Keep redrawing a window while the camera does not move. Off: a still window only refreshes twice a second (for the sky), which costs nearly nothing.");
            SecondaryViewpointRange = Config.Bind("5. Performance", "SecondaryViewpointRange", 12f,
                new ConfigDescription("Metres from the pane within which the extra capture viewpoints are drawn. Farther away the primary viewpoint alone looks the same and is a third of the geometry.",
                    new AcceptableValueRange<float>(0f, 60f)));
            GrassMaxInstances = Config.Bind("5. Performance", "GrassMaxInstances", 4000,
                new ConfigDescription("Most grass tufts drawn through a window (the nearest to the far portal). 0 = no live grass.",
                    new AcceptableValueRange<int>(0, 60000)));
            PerfLog = Config.Bind("5. Performance", "PerfLog", false, "Log each window's render time every 10 s.");
            HalfResSecondaries = Config.Bind("5. Performance", "HalfResSecondaries", true,
                "Load the extra viewpoints' pictures at half resolution. They only fill in the slivers the main viewpoint could not see; about 35 MB less video memory per nearby window.");
            MaxWindowFps = Config.Bind("5. Performance", "MaxWindowFps", 60,
                new ConfigDescription("The nearest window is redrawn at most this often while you move (the others 30 times a second). A redraw is 1 to 2 ms of the main thread; at 120 fps and up, redrawing every frame doubles that cost for a difference nobody sees.",
                    new AcceptableValueRange<int>(20, 500)));
            DebugLog = Config.Bind("6. Debug", "DebugLog", false, "Verbose logging of captures and windows. With TuneKeys it also enables numpad + - * / (diagnostics that switch the picture's draw order, the depth plug, and the game's ambient occlusion and post-processing).");

            _harmony = new Harmony(GUID);
            Storage.Configure(CaptureFolder.Value, Application.persistentDataPath, Paths.ConfigPath);
            _harmony.PatchAll(typeof(Patches));
            Camera.onPreCull += OnCameraPreCull;
            // Portals register themselves as they appear, so no scan of every object in the scene is needed.
            var awake = AccessTools.Method(typeof(TeleportWorld), "Awake");
            if (awake != null) { _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.TeleportWorld_Awake))); _portalsTracked = true; }
            else Log.LogWarning("LivePortals: TeleportWorld.Awake not found; portals are found by scanning the scene instead.");
            Log.LogInfo($"LivePortals {VERSION} loaded.");
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            _harmony?.UnpatchSelf();
        }

        private static void Say(Player player, string what)
        {
            player.Message(MessageHud.MessageType.Center, "LivePortals: " + what);
            Log.LogInfo("LivePortals diag: " + what);
        }

        internal static void Dbg(string msg)
        {
            if (DebugLog.Value) Log.LogInfo("LivePortals: " + msg);
        }

        // ------------------------------------------------------------------
        // Window management: attach a PortalWindow to every connected portal near the player.
        // ------------------------------------------------------------------
        private void Update()
        {
            if (!Enabled.Value) return;
            var player = Player.m_localPlayer;
            if (player == null || GameCamera.instance == null) return;
            if (TuneKeys.Value && player.TakeInput() && !Hud.InRadial()) UpdateTuneKeys(player);
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = 0.5f;

            _scan.Clear();
            Vector3 p = player.transform.position;
            foreach (var tw in AllPortals())
            {
                if (tw == null || !tw.isActiveAndEnabled) continue;
                // The window exists a little farther out than it is seen, so its capture is loaded by the time it dissolves in.
                float range = tw.m_activationRange * RangeMultiplier.Value + 2f + PortalWindow.PreloadMargin;
                if (Vector3.Distance(p, tw.transform.position) > range) continue;
                _scan.Add(tw);
            }
            _scan.Sort((a, b) => Vector3.Distance(p, a.transform.position).CompareTo(Vector3.Distance(p, b.transform.position)));
            for (int i = 0; i < _scan.Count; i++)
            {
                var tw = _scan[i];
                var w = tw.GetComponent<PortalWindow>();
                if (i < MaxWindows.Value)
                {
                    if (w == null) w = tw.gameObject.AddComponent<PortalWindow>();
                    w.Rank = i;
                    w.SetSuppressed(false);
                }
                else if (w != null) w.SetSuppressed(true);
            }
        }

        // ------------------------------------------------------------------
        // Render scheduler: of the windows that want a redraw this frame, the nearest few get it.
        // ------------------------------------------------------------------
        private static readonly List<PortalWindow> _wanting = new List<PortalWindow>();

        private float _perfAt;

        /// <summary>
        /// Runs just before the game camera culls: every script has moved what it moves by then (the game places its
        /// camera in LateUpdate), so the windows are posed for exactly the eye this frame is drawn from, and the
        /// redraws land in the same frame.
        /// </summary>
        private void OnCameraPreCull(Camera cam)
        {
            var gc = GameCamera.instance;
            if (gc == null || cam != gc.m_camera || !Enabled.Value) return;
            if (PerfLog.Value && Time.time - _perfAt > 10f)
            {
                float span = _perfAt > 0f ? Time.time - _perfAt : 10f;
                Log.LogInfo($"LivePortals perf: {PortalWindow.All.Count} windows exist, {PortalWindow.PerfRenders / span:0.0} window renders/s costing {PortalWindow.PerfMs / span:0.0} ms per second of main-thread time, game {1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):0} fps, all textures in the game {Texture.currentTextureMemory / 1048576UL} MB of {SystemInfo.graphicsMemorySize} MB video memory, of which the windows' captures about {PortalWindow.CaptureMegabytes():0} MB");
                PortalWindow.PerfRenders = 0; PortalWindow.PerfMs = 0; _perfAt = Time.time;
            }
            _wanting.Clear();
            for (int i = 0; i < PortalWindow.All.Count; i++)
            {
                var w = PortalWindow.All[i];
                if (w == null) continue;
                try { w.Refresh(cam); }
                catch (Exception e) { Dbg("window refresh failed: " + e.Message); }
                if (w.WantsRender) _wanting.Add(w);
            }
            if (_wanting.Count == 0) return;
            if (_wanting.Count > 1)
            {
                // The nearest first, always; the rest by how long each has waited, so they take turns.
                int nearest = 0;
                for (int i = 1; i < _wanting.Count; i++) if (_wanting[i].EyeDist < _wanting[nearest].EyeDist) nearest = i;
                var first = _wanting[nearest];
                _wanting.RemoveAt(nearest);
                _wanting.Sort((x, y) => y.Staleness.CompareTo(x.Staleness));
                _wanting.Insert(0, first);
            }
            int budget = Mathf.Max(1, MaxRendersPerFrame.Value);
            for (int i = 0; i < _wanting.Count; i++)
            {
                if (i < budget) _wanting[i].RenderNow();
                else _wanting[i].WantsRender = false; // its turn comes on a later frame
            }
        }

        // ------------------------------------------------------------------
        // Live tuning keys (numpad). Each press nudges a config value, shows all of them on screen and logs them.
        // ------------------------------------------------------------------
        private void UpdateTuneKeys(Player player)
        {
            bool changed = false;
            // The keys tune the kind of portal you stand nearest to: the wooden one, or "other" (the stone portal
            // and anything modded), whose values start from what was measured on that portal.
            TeleportWorld near = null; float nearD = 30f;
            foreach (var tw in AllPortals())
            {
                float d = Vector3.Distance(tw.transform.position, player.transform.position);
                if (d < nearD) { nearD = d; near = tw; }
            }
            bool other = near != null && !PortalShape.IsWood(near);
            float dy = (ZInput.GetKeyDown(KeyCode.Keypad8, false) ? 0.05f : 0f) - (ZInput.GetKeyDown(KeyCode.Keypad2, false) ? 0.05f : 0f);
            float dw = (ZInput.GetKeyDown(KeyCode.Keypad9, false) ? 0.05f : 0f) - (ZInput.GetKeyDown(KeyCode.Keypad7, false) ? 0.05f : 0f);
            float dh = (ZInput.GetKeyDown(KeyCode.Keypad3, false) ? 0.05f : 0f) - (ZInput.GetKeyDown(KeyCode.Keypad1, false) ? 0.05f : 0f);
            if (dy != 0f || dw != 0f || dh != 0f)
            {
                changed = true;
                if (other)
                {
                    PortalShape.MeasuredValues(near, out float mw, out float mh, out float mc);
                    if (dw != 0f) OtherPaneWidth.Value = Round((OtherPaneWidth.Value > 0f ? OtherPaneWidth.Value : mw) + dw);
                    if (dh != 0f) OtherPaneHeight.Value = Round((OtherPaneHeight.Value > 0f ? OtherPaneHeight.Value : mh) + dh);
                    if (dy != 0f) OtherCenterHeight.Value = Round((OtherCenterHeight.Value > 0f ? OtherCenterHeight.Value : mc) + dy);
                }
                else
                {
                    RingCenterOffset.Value = Round(RingCenterOffset.Value + dy);
                    PaneWidth.Value = Round(PaneWidth.Value + dw);
                    PaneHeight.Value = Round(PaneHeight.Value + dh);
                }
            }
            if (ZInput.GetKeyDown(KeyCode.Keypad6, false)) { PaneForwardOffset.Value = Round(PaneForwardOffset.Value + 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad4, false)) { PaneForwardOffset.Value = Round(PaneForwardOffset.Value - 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad0, false))
            {
                TeleportWorld best = null; float bestD = 8f;
                foreach (var tw in AllPortals())
                {
                    float d = Vector3.Distance(tw.transform.position, player.transform.position);
                    if (d < bestD) { bestD = d; best = tw; }
                }
                if (best != null) { CaptureAt(best, "manual"); player.Message(MessageHud.MessageType.Center, "LivePortals: captured " + best.name); }
                else player.Message(MessageHud.MessageType.Center, "LivePortals: no portal within 8 m");
            }
            if (ZInput.GetKeyDown(KeyCode.KeypadPeriod, false))
            {
                GlassTest.Value = !GlassTest.Value;
                Config.Save();
                player.Message(MessageHud.MessageType.Center, "LivePortals: glass test " + (GlassTest.Value ? "ON (own capture, no mapping)" : "OFF"));
                Log.LogInfo("LivePortals: glass test " + GlassTest.Value);
            }
            // Diagnostics for the pane that is black on screen at night while its picture is bright (0.9.22, the
            // stone portal outdoors; the wooden one indoors is fine the same night). Each key switches one suspect.
            // Only with DebugLog on: two of them switch effects of the game itself.
            if (!DebugLog.Value) { }
            else if (ZInput.GetKeyDown(KeyCode.KeypadPlus, false))
            {
                PortalWindow.DiagQueue = PortalWindow.DiagQueue == 2450 ? 2950 : 2450;
                Say(player, "picture drawn " + (PortalWindow.DiagQueue == 2450 ? "BEFORE the game's opaque-stage effects (2450, normal)" : "AFTER them, with the transparent things (2950)"));
            }
            else if (ZInput.GetKeyDown(KeyCode.KeypadMinus, false))
            {
                PortalWindow.DiagPlugOff = !PortalWindow.DiagPlugOff;
                Say(player, "black depth plug behind the picture " + (PortalWindow.DiagPlugOff ? "OFF" : "ON (normal)"));
            }
            else if (ZInput.GetKeyDown(KeyCode.KeypadMultiply, false))
            {
                var ao = GameCamera.instance != null ? GameCamera.instance.m_camera.GetComponent<AmplifyOcclusionEffect>() : null;
                if (ao != null) { ao.enabled = !ao.enabled; Say(player, "game ambient occlusion " + (ao.enabled ? "ON (normal)" : "OFF")); }
                else Say(player, "no ambient occlusion effect on the game camera");
            }
            else if (ZInput.GetKeyDown(KeyCode.KeypadDivide, false))
            {
                var pp = GameCamera.instance != null ? GameCamera.instance.m_camera.GetComponent<UnityEngine.PostProcessing.PostProcessingBehaviour>() : null;
                if (pp != null) { pp.enabled = !pp.enabled; Say(player, "game post-processing " + (pp.enabled ? "ON (normal)" : "OFF")); }
                else Say(player, "no post-processing on the game camera");
            }
            bool print = ZInput.GetKeyDown(KeyCode.Keypad5, false);
            if (print) PortalWindow.DumpRequest++;
            if (!changed && !print) return;
            Config.Save();
            string s;
            if (other)
            {
                var shape = PortalShape.Of(near);
                s = $"{Utils.GetPrefabName(near.gameObject)}: pane {shape.Width:0.00}x{shape.Height:0.00}, centre {Vector3.Dot(shape.Centre - near.transform.position, near.transform.up):0.00} m up, fwd {PaneForwardOffset.Value:0.00}";
            }
            else s = $"ring height +{RingCenterOffset.Value:0.00}  fwd {PaneForwardOffset.Value:0.00}  pane {PaneWidth.Value:0.00}x{PaneHeight.Value:0.00}";
            player.Message(MessageHud.MessageType.Center, "LivePortals: " + s);
            Log.LogInfo("LivePortals tune: " + s);
        }

        private static float Round(float v) => Mathf.Round(v * 100f) / 100f;

        // ------------------------------------------------------------------
        // Captures
        // ------------------------------------------------------------------
        internal void ScheduleDepartureCapture(TeleportWorld portal, Player player)
        {
            if (!Enabled.Value || !CaptureOnDeparture.Value || portal == null) return;
            StartCoroutine(DepartureCapture(portal, player));
        }

        private IEnumerator DepartureCapture(TeleportWorld portal, Player player)
        {
            yield return new WaitForSeconds(DepartureDelay.Value);
            if (portal == null || player == null || !player.IsTeleporting()) yield break; // the teleport was refused
            CaptureAt(portal, "departure");
        }

        private static bool _wasTeleporting;
        internal void OnTeleportUpdate(Player player)
        {
            if (player != Player.m_localPlayer) return;
            bool now = player.m_teleporting;
            if (_wasTeleporting && !now && Enabled.Value && CaptureOnArrival.Value) StartCoroutine(ArrivalCapture(player));
            _wasTeleporting = now;
        }

        private IEnumerator ArrivalCapture(Player player)
        {
            if (ArrivalDelay.Value > 0f) yield return new WaitForSeconds(ArrivalDelay.Value);
            else yield return null;
            if (player == null) yield break;
            // Grass grows in a patch per frame around the player; right after arriving there is none yet. Have the
            // game build all of it now, while the screen is still black.
            var clutter = ClutterSystem.instance;
            for (int i = 0; i < 30 && clutter != null && !clutter.IsHeightmapReady(); i++) yield return null;
            if (player == null) yield break;
            try { if (clutter != null && clutter.IsHeightmapReady()) clutter.UpdateGrass(0f, true, player.transform.position); }
            catch (Exception e) { Log.LogWarning("LivePortals: could not pre-build grass: " + e.Message); }
            TeleportWorld best = null; float bestD = 6f; // the stone portal sets you down farther out than the wooden one
            foreach (var tw in AllPortals())
            {
                float d = Vector3.Distance(tw.transform.position, player.transform.position);
                if (d < bestD) { bestD = d; best = tw; }
            }
            if (best == null) { Dbg("arrival: no portal within 4 m, no capture"); yield break; }
            CaptureAt(best, "arrival");
        }

        private void CaptureAt(TeleportWorld portal, string why)
        {
            StartCoroutine(CaptureSeries(portal, why));
        }

        private static readonly HashSet<ZDOID> _busy = new HashSet<ZDOID>();

        /// <summary>
        /// Capture the portal: a few faces rendered per frame, the pixels read back without waiting for the GPU,
        /// and the layering, encoding and storing on a low-priority thread as they arrive (see CaptureRun).
        /// </summary>
        private IEnumerator CaptureSeries(TeleportWorld portal, string why)
        {
            var nview = portal != null ? portal.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) yield break;
            ZDOID id = nview.GetZDO().m_uid;
            if (!_busy.Add(id)) { Dbg("capture of " + Storage.Key(id) + " already running, skipped"); yield break; }

            var run = new CaptureRun(portal, id);
            try { run.Prepare(); }
            catch (Exception e) { Log.LogWarning("LivePortals: capture failed: " + e); run.Abort(); }
            if (run.Error == null && run.Points.Count > 0) yield return StartCoroutine(run.Render());
            else if (run.Error == null) run.Abort();
            while (!run.Done) yield return null;
            _busy.Remove(id);
            if (run.Error != null) { Log.LogWarning("LivePortals: could not capture: " + run.Error); yield break; }
            PortalWindow.NotifyCaptureUpdated(id);
            var p0 = run.Points[0];
            Log.LogInfo($"LivePortals: {why} capture at portal {Storage.Key(id)}: {run.Points.Count} points, {p0.Res}px faces, grid {p0.Res / p0.Step}, {run.FrontFaces} faces with foreground, {run.GrassCount} grass instances, {run.FireCount} live flame effects, forward face {p0.SkyFraction * 100f:0}% sky ({p0.DiffFraction * 100f:0}% by colour) with median depth {p0.MedianDepth:0.0} m; rendered over {run.RenderFrames} frames ({run.RenderMs:0} ms of them), layered and stored in {run.WorkSeconds:0.0} s.");
        }
    }

    public enum PaneStyleOption { Sprite, Emissive }

    internal static class Patches
    {
        internal static void TeleportWorld_Awake(TeleportWorld __instance)
        {
            if (__instance != null && !Plugin.Portals.Contains(__instance)) Plugin.Portals.Add(__instance);
        }

        // Leaving: the player is still standing at this portal for two seconds while the screen fades.
        [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Teleport))]
        [HarmonyPrefix]
        private static void TeleportWorld_Teleport(TeleportWorld __instance, Player player)
        {
            if (player == null || player != Player.m_localPlayer || player.IsTeleporting()) return;
            if (!__instance.TargetFound()) return;
            Plugin.Instance?.ScheduleDepartureCapture(__instance, player);
        }

        // Arriving: m_teleporting drops to false the frame the player is placed, while the screen is still black.
        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        [HarmonyPostfix]
        private static void Player_UpdateTeleport(Player __instance)
        {
            Plugin.Instance?.OnTeleportUpdate(__instance);
        }
    }
}
