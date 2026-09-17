using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

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
        public const string VERSION = "0.5.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        private Harmony _harmony;

        // ---- Config ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> CaptureResolution;
        internal static ConfigEntry<int> WindowResolution;
        internal static ConfigEntry<float> DepthRange;
        internal static ConfigEntry<int> DepthGrid;
        internal static ConfigEntry<float> RangeMultiplier;
        internal static ConfigEntry<float> FullMultiplier;
        internal static ConfigEntry<float> PaneWidth;
        internal static ConfigEntry<float> PaneHeight;
        internal static ConfigEntry<float> RingCenterHeight;
        internal static ConfigEntry<float> RingCenterOffset;
        internal static ConfigEntry<float> PaneForwardOffset;
        internal static ConfigEntry<bool> PaneRound;
        internal static ConfigEntry<float> DepthScale;
        internal static ConfigEntry<bool> TuneKeys;
        internal static ConfigEntry<bool> GlassTest;
        internal static ConfigEntry<bool> ArrivalViewBothSides;

        /// <summary>
        /// World position of the portal ring's centre, where the pane sits and captures are taken from. The
        /// vanilla swirl effect is placed exactly there, so use it when present; else a height above the base.
        /// </summary>
        internal static Vector3 RingCenter(TeleportWorld tw)
        {
            // The swirl effect's root sits at the portal's base, so it is no help; the model's bounds centre is at
            // ring height on the vanilla portal (1.64 m). RingCenterHeight > 0 overrides that.
            float h = RingCenterHeight.Value;
            if (h <= 0f)
            {
                h = 1.64f;
                if (tw.m_model != null)
                {
                    float fromBounds = Vector3.Dot(tw.m_model.bounds.center - tw.transform.position, tw.transform.up);
                    if (fromBounds > 0.5f && fromBounds < 4f) h = fromBounds;
                }
            }
            return tw.transform.position + tw.transform.rotation * new Vector3(0f, h + RingCenterOffset.Value, PaneForwardOffset.Value);
        }
        internal static ConfigEntry<bool> CaptureOnDeparture;
        internal static ConfigEntry<bool> CaptureOnArrival;
        internal static ConfigEntry<float> DepartureDelay;
        internal static ConfigEntry<float> ArrivalDelay;
        internal static ConfigEntry<bool> LiveSky;
        internal static ConfigEntry<float> ToneMatch;
        internal static ConfigEntry<float> CaptureExposure;
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
            CaptureResolution = Config.Bind("1. General", "CaptureResolution", 512,
                new ConfigDescription("Pixels per cube face captured at a portal. 512 is plenty; 1024 costs four times the disk and memory.",
                    new AcceptableValueRange<int>(128, 1024)));
            WindowResolution = Config.Bind("1. General", "WindowResolution", 768,
                new ConfigDescription("Pixels of the texture each window is drawn into every frame.",
                    new AcceptableValueRange<int>(256, 2048)));
            DepthRange = Config.Bind("1. General", "DepthRange", 120f,
                new ConfigDescription("Metres of depth captured per pixel; anything farther (and the sky) sits at this distance. Larger = flatter far parallax, less stretch at edges.",
                    new AcceptableValueRange<float>(20f, 500f)));
            DepthGrid = Config.Bind("1. General", "DepthGrid", 96,
                new ConfigDescription("Vertices per edge of each displaced capture face. Higher = crisper silhouettes, more triangles.",
                    new AcceptableValueRange<int>(16, 256)));
            RangeMultiplier = Config.Bind("2. Window", "RangeMultiplier", 4f,
                new ConfigDescription("The window starts to appear at this many times the portal's activation range (5 m in vanilla, so 4 = 20 m).",
                    new AcceptableValueRange<float>(1f, 10f)));
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
            PaneRound = Config.Bind("2. Window", "PaneRound", true, "Round pane (the ring's shape) instead of a square.");
            DepthScale = Config.Bind("2. Window", "DepthScale", 1f,
                new ConfigDescription("Scale of the captured world behind the window. 1 = true size; below 1 brings it closer and larger, above 1 pushes it away.",
                    new AcceptableValueRange<float>(0.2f, 5f)));
            TuneKeys = Config.Bind("2. Window", "TuneKeys", true,
                "Numpad tuning while in game: 8/2 ring height, 4/6 forward offset, 7/9 pane width, 1/3 pane height, +/- depth scale, 5 prints and saves, 0 captures the nearest portal now, . (period) toggles the glass test. Values are saved to this file.");
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
            if (ZInput.GetKeyDown(KeyCode.Keypad8, false)) { RingCenterOffset.Value = Round(RingCenterOffset.Value + 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad2, false)) { RingCenterOffset.Value = Round(RingCenterOffset.Value - 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad6, false)) { PaneForwardOffset.Value = Round(PaneForwardOffset.Value + 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad4, false)) { PaneForwardOffset.Value = Round(PaneForwardOffset.Value - 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad9, false)) { PaneWidth.Value = Round(PaneWidth.Value + 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad7, false)) { PaneWidth.Value = Round(PaneWidth.Value - 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad3, false)) { PaneHeight.Value = Round(PaneHeight.Value + 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.Keypad1, false)) { PaneHeight.Value = Round(PaneHeight.Value - 0.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.KeypadPlus, false)) { DepthScale.Value = Round(DepthScale.Value * 1.05f); changed = true; }
            if (ZInput.GetKeyDown(KeyCode.KeypadMinus, false)) { DepthScale.Value = Round(DepthScale.Value / 1.05f); changed = true; }
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
            if (!changed && !print) return;
            Config.Save();
            string s = $"ring height +{RingCenterOffset.Value:0.00}  fwd {PaneForwardOffset.Value:0.00}  pane {PaneWidth.Value:0.00}x{PaneHeight.Value:0.00}  depth x{DepthScale.Value:0.00}";
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
            TeleportWorld best = null; float bestD = 4f;
            foreach (var tw in UnityEngine.Object.FindObjectsByType<TeleportWorld>(FindObjectsSortMode.None))
            {
                float d = Vector3.Distance(tw.transform.position, player.transform.position);
                if (d < bestD) { bestD = d; best = tw; }
            }
            if (best == null) { Dbg("arrival: no portal within 4 m, no capture"); yield break; }
            CaptureAt(best, "arrival");
        }

        private static void CaptureAt(TeleportWorld portal, string why)
        {
            var nview = portal.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            ZDOID id = nview.GetZDO().m_uid;
            Vector3 pos = RingCenter(portal) + portal.transform.forward * 0.15f;
            Quaternion rot = portal.transform.rotation;
            try
            {
                var cap = Capture.Take(pos, rot, portal);
                if (cap == null) return;
                Storage.Save(id, cap);
                PortalWindow.NotifyCaptureUpdated(id);
                Log.LogInfo($"LivePortals: {why} capture at portal {Storage.Key(id)} ({cap.Faces[0].width}px faces).");
            }
            catch (Exception e)
            {
                Log.LogWarning("LivePortals: capture failed: " + e);
            }
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
