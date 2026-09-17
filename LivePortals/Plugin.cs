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
        public const string NAME = "LivePortals";
        public const string VERSION = "0.9.0";

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
        internal static ConfigEntry<bool> LiveSky;
        internal static ConfigEntry<float> ToneMatch;
        internal static ConfigEntry<float> CaptureExposure;
        internal static ConfigEntry<bool> CaptureFog;
        internal static ConfigEntry<float> GrassGap;
        internal static ConfigEntry<float> PortalLight;
        internal static ConfigEntry<float> PortalLightRange;
        internal static ConfigEntry<int> MaxWindows;
        internal static ConfigEntry<int> RenderEveryNFrames;
        internal static ConfigEntry<bool> DebugLog;

        /// <summary>Layer of the capture meshes. They are only enabled while a window camera renders, so it does not matter who else renders it.</summary>
        internal const int FaceLayer = 31;

        private float _scanTimer;
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
            RangeMultiplier = Config.Bind("2. Window", "RangeMultiplier", 8f,
                new ConfigDescription("The window starts to dissolve in at this many times the portal's activation range (5 m in vanilla, so 8 = 40 m).",
                    new AcceptableValueRange<float>(1f, 20f)));
            // Settings files written before 0.8.16 hold the old default of 4; Max asked for the transition to start
            // from twice as far. Done once, so a 4 chosen later on purpose stays.
            var configVersion = Config.Bind("1. General", "ConfigVersion", 0, "Internal: which defaults this file has been brought up to. Leave alone.");
            if (configVersion.Value < 1)
            {
                if (Mathf.Approximately(RangeMultiplier.Value, 4f)) RangeMultiplier.Value = 8f;
                configVersion.Value = 1;
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
            TuneKeys = Config.Bind("2. Window", "TuneKeys", true,
                "Numpad tuning while in game: 8/2 ring height, 4/6 forward offset, 7/9 pane width, 1/3 pane height, . (period) toggles the glass test, 5 prints, saves, and dumps what every visible window drew (BepInEx/config/LivePortals/debug), 0 captures the nearest portal now. Values are saved to this file.");
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
            LiveSky = Config.Bind("4. Look", "LiveSky", true, "Draw the current sky behind the capture where the capture saw sky, so day and night through the window follow the clock.");
            ToneMatch = Config.Bind("4. Look", "ToneMatch", 1f,
                new ConfigDescription("How strongly the capture's brightness and colour follow the current sun, ambient and fog compared with when it was taken. 0 = show it as captured.",
                    new AcceptableValueRange<float>(0f, 1f)));
            CaptureExposure = Config.Bind("4. Look", "CaptureExposure", 1f,
                new ConfigDescription("Overall brightness multiplier for captures (they are taken without the game's post-processing).",
                    new AcceptableValueRange<float>(0.2f, 3f)));
            CaptureFog = Config.Bind("4. Look", "CaptureFog", true,
                "Capture with the game's own distance fog and ambient occlusion (its post-processing stack, everything else in it switched off). Off = raw geometry colours, which look too crisp and bright at a distance.");
            GrassGap = Config.Bind("4. Look", "GrassGap", 0.75f,
                new ConfigDescription("Grass closer than this to the far portal's centre is not drawn in the window, metres (blades standing in the ring itself).",
                    new AcceptableValueRange<float>(0f, 10f)));
            PortalLight = Config.Bind("4. Look", "PortalLight", 1f,
                new ConfigDescription("Light spilling out of the window when the far side is brighter than here. 0 = off.",
                    new AcceptableValueRange<float>(0f, 5f)));
            PortalLightRange = Config.Bind("4. Look", "PortalLightRange", 8f,
                new ConfigDescription("Reach of that light in metres.", new AcceptableValueRange<float>(1f, 30f)));
            MaxWindows = Config.Bind("5. Performance", "MaxWindows", 3,
                new ConfigDescription("Most windows drawn at once (nearest first).", new AcceptableValueRange<int>(1, 8)));
            RenderEveryNFrames = Config.Bind("5. Performance", "RenderEveryNFrames", 1,
                new ConfigDescription("Redraw each window every N frames. 2 halves the cost with a barely visible lag.",
                    new AcceptableValueRange<int>(1, 4)));
            DebugLog = Config.Bind("5. Performance", "DebugLog", false, "Verbose logging of captures and windows.");

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Patches));
            Log.LogInfo($"LivePortals {VERSION} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
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
            foreach (var tw in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
            {
                if (tw == null || !tw.isActiveAndEnabled) continue;
                float range = tw.m_activationRange * RangeMultiplier.Value + 2f;
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
                    if (w == null) tw.gameObject.AddComponent<PortalWindow>();
                }
                else if (w != null) w.SetSuppressed(true);
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
            foreach (var tw in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
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
                foreach (var tw in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
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
            foreach (var tw in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
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

        private class Work
        {
            public volatile bool Done;
            public string Error;
            public int FrontFaces;
            public double Seconds;
        }

        private static readonly HashSet<ZDOID> _busy = new HashSet<ZDOID>();

        /// <summary>
        /// Render the portal from every capture point in this frame (nothing moves between them), then split the
        /// faces into layers, encode and store them on a worker thread, so the game only hitches for the renders.
        /// </summary>
        private IEnumerator CaptureSeries(TeleportWorld portal, string why)
        {
            var nview = portal != null ? portal.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid()) yield break;
            ZDOID id = nview.GetZDO().m_uid;
            if (!_busy.Add(id)) { Dbg("capture of " + Storage.Key(id) + " already running, skipped"); yield break; }

            List<RawPoint> points = null;
            GrassSet grass = null;
            Storage.Job job = null;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                // The secondary viewpoints spread with the opening: a stone portal's is about twice the wooden one's.
                var shape = PortalShape.Of(portal);
                var offsets = CaptureSet.PointOffsets(CapturePoints.Value);
                float sx = Mathf.Clamp(shape.Width / 2.7f, 1f, 3f), sy = Mathf.Clamp(shape.Height / 2.8f, 1f, 3f);
                for (int i = 0; i < offsets.Length; i++) offsets[i] = new Vector3(offsets[i].x * sx, offsets[i].y * sy, offsets[i].z);
                points = Capture.RenderPoints(shape.Centre, portal.transform.rotation, offsets, portal);
                if (points != null && points.Count > 0)
                {
                    job = Storage.Begin(id);
                    grass = GrassSet.Record(shape.Centre, portal.transform.rotation);
                }
            }
            catch (Exception e) { Log.LogWarning("LivePortals: capture failed: " + e); }
            if (job == null) { _busy.Remove(id); yield break; }
            float renderMs = (Time.realtimeSinceStartup - t0) * 1000f;

            long takenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            float skyPct = points[0].SkyFraction * 100f, diffPct = points[0].DiffFraction * 100f, median = points[0].MedianDepth;
            var work = new Work();
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    Storage.Clear(job);
                    for (int k = 0; k < points.Count; k++)
                    {
                        var pt = points[k];
                        var grids = new FaceGrids[6];
                        for (int i = 0; i < 6; i++)
                        {
                            if (pt.Faces[i] == null) continue;
                            var layers = Layers.Process(pt.Faces[i], pt.Res, pt.Step, pt.DepthRange);
                            layers.Grids.Tan = pt.FaceTan;
                            pt.Faces[i] = null;
                            Storage.SaveFace(job, k, i, layers, pt.Res);
                            grids[i] = layers.Grids;
                            if (layers.Front != null) work.FrontFaces++;
                        }
                        Storage.SavePoint(job, k, pt, grids);
                    }
                    Storage.SaveGrass(job, grass);
                    Storage.Finish(job, points.Count, takenAt);
                }
                catch (Exception e) { work.Error = e.ToString(); }
                work.Seconds = sw.Elapsed.TotalSeconds;
                work.Done = true;
            });
            while (!work.Done) yield return null;
            _busy.Remove(id);
            if (work.Error != null) { Log.LogWarning("LivePortals: could not store capture: " + work.Error); yield break; }
            PortalWindow.NotifyCaptureUpdated(id);
            Log.LogInfo($"LivePortals: {why} capture at portal {Storage.Key(id)}: {points.Count} points, {points[0].Res}px faces, grid {points[0].Res / points[0].Step}, {work.FrontFaces} faces with foreground, {(grass != null ? grass.Count : 0)} grass instances, forward face {skyPct:0}% sky ({diffPct:0}% by colour) with median depth {median:0.0} m; rendered in {renderMs:0} ms, layered and stored in {work.Seconds:0.0} s.");
        }
    }

    internal static class Patches
    {
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
