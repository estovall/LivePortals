# LivePortals

See through your portals. Walk up to a connected portal and its swirl becomes a window onto the other side, with
real parallax as you move, the current sky behind it, and its brightness following the time of day. If the far side
is brighter than where you stand, light spills out of the portal.

It is a picture, not a live feed: every time you go through a portal the mod captures the view from the portal you
leave and from the one you arrive at, and each portal shows its partner's latest capture. Anything in frame at the
time, including creatures and other players, is captured frozen as it was. Captures are stored on your PC only, so you
see your own last trips; walk through once and both windows are set.

## Install

BepInEx plugin, client-side only. Drop `LivePortals.dll` into `BepInEx\plugins` (or install with Gale / r2modman).
Nothing is needed on the server and nothing changes for players without the mod.

## What you see

* The window appears from about four times the portal's activation range (20 m in vanilla) and fades in fully by the
  activation range. Both faces of a portal are windows: the back of a portal looks out of the back of its partner.
* The sky through the window is the sky now, and the ground and buildings are tinted to the current sun, ambient light
  and fog compared with when they were captured, so a noon capture darkens at night and warms at dusk. Shadows stay
  where they were captured.
* A wide, soft light on your side of the portal carries the far side's brightness and colour, and only when the far
  side is lighter than where you are.

## Config (`BepInEx\config\com.maxst.liveportals.cfg`)

| Key | Default | Meaning |
| --- | --- | --- |
| `RangeMultiplier` / `FullMultiplier` | 4 / 1 | Where the window starts to appear and where it is fully visible, as multiples of the portal's activation range |
| `WindowWidth` / `WindowHeight` / `WindowCenterHeight` / `WindowForwardOffset` | 1.7 / 2.3 / 0 / 0 | The pane size, and its offset up and forward from the portal ring centre, metres |
| `CaptureResolution` | 512 | Pixels per captured cube face (six faces per portal) |
| `WindowResolution` | 768 | Pixels the window is drawn at each frame |
| `CaptureOnDeparture` / `CaptureOnArrival` | on / on | Which trips capture |
| `DepartureDelay` / `ArrivalDelay` | 0.8 / 0.15 s | When the captures happen, timed to fall under the black screen |
| `LiveSky` | on | Draw the current sky behind the capture |
| `ToneMatch` | 1 | How strongly captures follow the current lighting (0 = as captured) |
| `CaptureExposure` | 1 | Brightness of captures (taken without the game's post-processing) |
| `PortalLight` / `PortalLightRange` | 1 / 8 m | The spill light; 0 turns it off |
| `MaxWindows` | 3 | Most windows drawn at once |
| `RenderEveryNFrames` | 1 | Redraw windows every N frames |

Captures live in `BepInEx\config\LivePortals\<world>\`, six PNGs and a text file per portal. Delete them to reset.

## Known limits

* Captures are taken with the game's fog but without its post-processing, so they can look a little flatter than the
  live world; `CaptureExposure` compensates.
* The far side is not loaded, so stepping through still shows the usual loading screen.
* Shadows in a capture do not move with the sun.
