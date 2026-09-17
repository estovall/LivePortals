# LivePortals

See through your portals. Walk up to a connected portal and its swirl becomes a window onto the other side, with
real parallax as you move, the current sky behind it, and its brightness following the time of day. If the far side
is brighter than where you stand, light spills out of the portal.

It is a picture, not a live feed (except the grass, which is drawn as real geometry and moves in the wind): every time you go through a portal the mod captures the view from the portal you
leave and from the one you arrive at, and each portal shows its partner's latest capture. Anything in frame at the
time, including creatures and other players, is captured frozen as it was. Captures are stored on your PC only, so you
see your own last trips; walk through once and both windows are set.

## Install

BepInEx plugin, client-side only. Drop `LivePortals.dll` into `BepInEx\plugins` (or install with Gale / r2modman).
Nothing is needed on the server and nothing changes for players without the mod.

## What you see

* The window dissolves in from about eight times the portal's activation range (40 m in vanilla), from the rim of
  the ring inward. Both faces of a portal are windows: the back of a portal looks out of the back of its partner.
* The sky through the window is the sky now, and the ground and buildings are tinted to the current sun, ambient light
  and fog compared with when they were captured, so a noon capture darkens at night and warms at dusk. Shadows stay
  where they were captured.
* A wide, soft light on your side of the portal carries the far side's brightness and colour, and only when the far
  side is lighter than where you are.

## Config (`BepInEx\config\com.maxst.liveportals.cfg`)

| Key | Default | Meaning |
| --- | --- | --- |
| `RangeMultiplier` / `FullMultiplier` | 8 / 1 | Where the window starts to dissolve in and where it is complete, as multiples of the portal's activation range |
| `PaneWidth` / `PaneHeight` / `RingCenterOffset` / `PaneForwardOffset` | 2.7 / 2.8 / -0.35 / 0 | The pane size, a vertical nudge of the ring centre (found from the portal model), and a forward nudge, metres. Numpad keys tune these in game (see `TuneKeys`) |
| `OtherPaneWidth` / `OtherPaneHeight` / `OtherCenterHeight` | 0 = measured | The pane of every portal that is not the wooden one (the stone portal is measured from its colliders) |
| `CaptureResolution` / `CaptureViewpoints` | 768 / 4 | Pixels per captured face, and how many viewpoints each portal is captured from (centre, above, right, left, below) |
| `MeshGrid` | 128 | Cells per edge of each face's relief mesh. Silhouettes are cut per pixel regardless; this is how finely surfaces follow the captured depth |
| `WindowResolution` | 768 | Pixels the window is drawn at each frame |
| `CaptureOnDeparture` / `CaptureOnArrival` | on / on | Which trips capture |
| `DepartureDelay` / `ArrivalDelay` | 0.8 / 0.15 s | When the captures happen, timed to fall under the black screen |
| `LiveSky` | on | Draw the current sky behind the capture |
| `ToneMatch` | 1 | How strongly captures follow the current lighting (0 = as captured) |
| `CaptureFog` | on | Capture with the game's own distance fog and ambient occlusion (Amplify Occlusion follows the game's SSAO setting) |
| `GrassGap` | 0.75 m | Grass this close to the far portal's centre is not drawn in the window |
| `CaptureExposure` | 1 | Brightness of captures |
| `PortalLight` / `PortalLightRange` | 1 / 8 m | The spill light; 0 turns it off |
| `MaxWindows` / `MaxRendersPerFrame` | 8 / 2 | Most windows kept loaded; most windows redrawn in one frame (the nearest first) |
| `SecondaryViewpointRange` | 12 m | Closer than this a window uses all its viewpoints and shows the far side's grass |
| `CaptureFolder` | (game data folder) | Where captures are stored |
| `CaptureFacesPerFrame` / `AsyncReadback` | 2 / on | How a capture is spread over frames; reading the GPU back without waiting for it |
| `RenderEveryNFrames` | 1 | Redraw windows every N frames |
| `PerfLog` | off | Log what the windows cost every 10 s |

Captures live in the game's own data folder, `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\LivePortals\<world>\` (or wherever `CaptureFolder` points): per portal a text file, and per capture point six background PNGs, up to six foreground PNGs and a file of depth grids. Delete them to reset. They are outside the mod-manager profile on purpose: a busy world's captures run to hundreds of megabytes, and a shared profile should not carry them. Captures from versions before 0.9.6 are moved there at the first start.

## Known limits

* Captures are taken with the game's fog but without its post-processing, so they can look a little flatter than the
  live world; `CaptureExposure` compensates.
* The far side is not loaded, so stepping through still shows the usual loading screen.
* Shadows in a capture do not move with the sun.
