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
        public const string VERSION = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        private Harmony _harmony;

        // ---- Config ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> CaptureResolution;
        internal static ConfigEntry<int> WindowResolution;
        internal static ConfigEntry<float> RangeMultiplier;
        internal static ConfigEntry<float> FullMultiplier;
        internal static ConfigEntry<float> WindowWidth;
        internal static ConfigEntry<float> WindowHeight;
        internal static ConfigEntry<float> WindowCenterHeight;
        internal static ConfigEntry<float> WindowForwardOffset;
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

        /// <summary>A layer no game camera renders, used for each window's parallax cube.</summary>
        internal static int HiddenLayer = 31;
        private static bool _hiddenLayerFound;

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
            RangeMultiplier = Config.Bind("2. Window", "RangeMultiplier", 2f,
                new ConfigDescription("The window starts to appear at this many times the portal's activation range (5 m in vanilla, so 2 = 10 m).",
                    new AcceptableValueRange<float>(1f, 10f)));
            FullMultiplier = Config.Bind("2. Window", "FullMultiplier", 1f,
                new ConfigDescription("The window is fully visible from this many times the activation range inward.",
                    new AcceptableValueRange<float>(0.2f, 10f)));
            WindowWidth = Config.Bind("2. Window", "WindowWidth", 1.7f,
                new ConfigDescription("Width of the window pane in metres (the portal's opening).", new AcceptableValueRange<float>(0.5f, 5f)));
            WindowHeight = Config.Bind("2. Window", "WindowHeight", 2.3f,
                new ConfigDescription("Height of the window pane in metres.", new AcceptableValueRange<float>(0.5f, 5f)));
            WindowCenterHeight = Config.Bind("2. Window", "WindowCenterHeight", 1.35f,
                new ConfigDescription("Height of the pane's centre above the portal's base.", new AcceptableValueRange<float>(0f, 4f)));
            WindowForwardOffset = Config.Bind("2. Window", "WindowForwardOffset", 0f,
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

        /// <summary>Pick a layer neither the main nor the sky camera renders, once the cameras exist.</summary>
        internal static bool EnsureHiddenLayer()
        {
            if (_hiddenLayerFound) return true;
            var gc = GameCamera.instance;
            if (gc == null || gc.m_camera == null) return false;
            int used = gc.m_camera.cullingMask;
            if (gc.m_skyCamera != null) used |= gc.m_skyCamera.cullingMask;
            for (int i = 31; i >= 8; i--)
            {
                if ((used & (1 << i)) == 0)
                {
                    HiddenLayer = i;
                    _hiddenLayerFound = true;
                    Dbg("hidden layer " + i + (string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? "" : " (" + LayerMask.LayerToName(i) + ")"));
                    return true;
                }
            }
            HiddenLayer = 31;
            _hiddenLayerFound = true;
            return true;
        }

        // ------------------------------------------------------------------
        // Window management: attach a PortalWindow to every connected portal near the player.
        // ------------------------------------------------------------------
        private void Update()
        {
            if (!Enabled.Value) return;
            var player = Player.m_localPlayer;
            if (player == null || !EnsureHiddenLayer()) return;
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
            Vector3 pos = portal.transform.position + portal.transform.forward * (WindowForwardOffset.Value + 0.15f) + Vector3.up * WindowCenterHeight.Value;
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
