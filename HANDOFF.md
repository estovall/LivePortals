# Pick-up notes for LivePortals

Written 2026-09-17 (early morning Central) on Max's home PC, right before switching computers. `main` is the current
dev state, **0.6.0**, untested in game when written. Not published to Hexium yet; Max tests each build in his Gale
"Flotilla" profile and gives feedback from screenshots and `BepInEx\LogOutput.log`.

## What the mod is (decided with Max)

See-through portals without loading the far side. On departure and arrival the mod captures the view from the portal
(colour + depth) and the paired portal shows it through a round pane with correct parallax. Constraints Max set:

- **No Unity Editor / no custom shaders.** Everything uses shaders already in the game build (`Sprites/Default` for
  the pane, `Particles/Standard Unlit` in cutout mode for the relief, the game's own sky camera copied for live sky).
- **Local-only captures** (his PC), taken on both departure and arrival; numpad 0 captures the nearest portal on demand.
- Window visible from **4x** the portal activation range, fading in fully by 1x.
- **Both faces see-through, and the back of A shows the back of B** (physically consistent hole). `ArrivalViewBothSides`
  (off) would show the partner's front from both faces instead.
- Creatures in frame are captured frozen. Live sky and time-of-day tone match, plus a spill light when the far side is
  brighter. A full live window (loading the far zones) and instant transition were discussed and rejected as too heavy.

## How it works now (0.6.0)

- `Capture.Take`: a copy of the main camera at the ring centre renders six 90-degree faces. Per face: colour with the
  game's fog; a black/white clear pair with fog off whose differing pixels are sky (alpha 0); depth by **physics rays**
  (one per relief vertex, `DepthGrid` 96) against solid colliders + the Water layer. Misses on drawn pixels (foliage,
  grass) take the nearest hit below them in the column. The portal itself (renderers, lights, colliders), the local
  player (renderers, colliders) and all windows are hidden during capture. A **background-only backdrop** per face is
  built by removing the near side of every depth jump and dilating from the far side / sky.
- `CaptureSet`: `CapturePoints` (default 3) captures at offsets across the ring (centre, +/-0.75 m sideways, then
  +/-0.7 m up/down), one per frame under the teleport fade. Stored under `BepInEx\config\LivePortals\<world>\` as
  `<zdoid>_p<k>_<face>.png`, `_d<face>.png` (16-bit depth in R,G), `_b<face>.png` (backdrop), plus `<zdoid>.txt`.
- `PortalWindow`: one per portal within range. Pane = disc, 2.7 x 2.8 m, centred at the model-bounds centre height
  minus 0.35 m (tuned by Max with the numpad keys). Off-axis frustum from the game camera through the pane (Kooima),
  **near plane on the pane**, camera orientation mapped through the portal (`rB * Yaw180 * rA^-1`). Each capture point
  is a relief (six displaced grid meshes cut at depth jumps + flat backdrops at `DepthRange` 120 m) anchored so the far
  ring lands on the near ring; relief meshes are enabled only for the instant the window camera renders. From the
  front the pane mesh is mirrored with a negative x scale (sprite shader ignores texture scale, which was the big
  mirror bug found via the glass test).
- Numpad keys (`TuneKeys`): 8/2 ring height, 4/6 forward, 7/9 width, 1/3 height, +/- depth scale, 5 print+save,
  0 capture nearest, . glass test (window shows its own portal with no mapping = should look like glass).

## Test history and what is still open

1-3. Layout/scale/mirror bugs fixed in turn: hidden layer rendered by the main camera; pane parented to the prefab;
proximity/swirl points not being the ring; fog-based depth not working on Valheim shaders; depth-normals replacement
drawing nothing; relief anchored on the eye; rubber-sheet streaks; backdrop duplicating near objects; the mirror.
4. 0.5.x: Max reports close-up content is fine; background looked like "big zoomed textures" and grass sometimes
   became background. 0.6.0 addresses this (foliage depth from below, full-res backdrops, multi-point) but is
   **untested**. Next: fresh trip through a portal, then check the tree line, grass, and seams between the three
   viewpoints. If arrival hitches or memory climbs (about 40 MB of textures per loaded window), spread capture work
   over more frames or lower `CaptureResolution` / `CapturePoints`.
5. Known limits to keep in mind: captured relief only knows one side of things; shadows are baked; the far side is not
   live. Max's bar is "feels like a hole"; be honest about what a capture can and cannot do.
6. Not yet done: publish to Hexium (`publish-mod.ps1` + `hexium-token.txt` next to it, gitignored; copy the token from
   the old PC), remove the diagnostics (`GlassTest`, glass log line) before a public release, README polish.

## Environment on a new PC

- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Build: `dotnet build LivePortals\LivePortals.csproj -c Release`
  -> `dist\LivePortals.dll`. Project targets **netstandard2.1** (net462 cannot reference Unity's ImageConversion module).
- Decompiler: `dotnet tool install -g ilspycmd --version 8.2.0.7535`, run with `DOTNET_ROLL_FORWARD=Major`.
  Types used: `TeleportWorld`, `Player` (UpdateTeleport/TeleportTo), `Hud` (UpdateBlackScreen), `GameCamera`
  (m_camera, m_skyCamera), `EnvMan` (m_dirLight, fog), `ZInput`.
- Install for testing: copy `dist\LivePortals.dll` + `package\*` into
  `%APPDATA%\com.kesomannen.gale\valheim\profiles\Flotilla\BepInEx\plugins\Max-LivePortals\`. The DLL is locked while
  Valheim runs. Gale hard-links profile files to its cache: delete then copy.
- Sister project: SailTrim (`C:\Users\maxst\source\SailTrim`, github estovall/SailTrim), released 1.4.0 the same day;
  its `HANDOFF-settings-tab.md` has more toolchain notes.
