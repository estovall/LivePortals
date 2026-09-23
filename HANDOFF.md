# Pick-up notes for LivePortals

Last updated 2026-09-22. `main` = **0.9.51**, built on the home PC and **not yet published**: Hexium and Max's
game are on 0.9.50, and the new DLL could not be copied into his profile because the game was running. Publish
it, or copy `dist/LivePortals.dll` over the one in his profile once Valheim is closed.

0.9.51 is one fix, worth reading before touching the camera hack or the capture triggers. Standing in a portal
that refuses you (metal in the pack) stalled the game: the portal's teleport trigger fired fifty times a second,
so the refusal, its message, the game's two log lines and a departure capture all ran every frame. The cause was
ours. `Patches.GameCamera_CollideRay2_Prefix` switches a nearby portal's colliders off for the length of one
camera check each frame, and it was switching off the teleport trigger with them; Unity counts somebody standing
in a trigger that comes back as having just walked in. An ordinary trip hid it, because the second ask is
refused while the first is under way, and carrying metal there is no first ask to swallow the rest. **Never
disable a trigger collider on anything a player may be standing in.** Triggers are now skipped there and in
`Capture.HideForCapture`; nothing is lost, since a trigger cannot block a camera, cannot be seen in a picture,
and every ray in `Capture.cs` already passes `QueryTriggerInteraction.Ignore`. The departure capture also waits
for the game to accept the trip (prefix records whether the player was already teleporting, postfix asks whether
they are now), which covers refusal for metal, a boss, a global key and the trip cooldown without listing them.

The evidence is worth knowing how to find again: `Player.log` in `AppData\LocalLow\IronGate\Valheim` carries the
game's own `Teleportation TRIGGER` line, one per fire, so counting them per second says outright how often the
trigger is going off.

Previously: `main` = **0.9.50**, published on Hexium (`Max/Immersive_Portals`),
and `testing` is merged into it, so both branches agree. Everything from 0.9.23 to 0.9.48 was done on
the home PC and is in `main` (items 52 to 80). Captures live in
`AppData\LocalLow\IronGate\Valheim\LivePortals\<world>`. On the home PC the mod is installed through Gale as the
Hexium package (profile folder `...\BepInEx\plugins\Max-Immersive_Portals\`): to test, build and replace
`LivePortals.dll` there (delete first: Gale hard-links). Numpad keys are OFF by default: set `TuneKeys = true`
under `[6. Debug]` in `com.maxst.liveportals.cfg` for numpad 5 (dump) and 0 (capture); `PerfLog = true` for numbers.

## Unconfirmed right now

| Change | Suspect it if | Switch it off with |
|---|---|---|
| 0.9.48: the picture fades out over the last 0.45 m instead of the pane receding | stepping through looks abrupt, or the picture cuts off early | (code: `Refresh`, the alpha ramp) |
| 0.9.39: flame bloom off by default | no glow around flames in a window | `FlameBloom = 4` |

## State at hand-over (read this first)

- **Confirmed in game:** per-pixel GPU depth; sky detection; fog + Amplify Occlusion in captures; the relief material
  chosen by self-test (`Custom/Creature (emissive)`, deferred path only: forward renders it black); colours with the
  8-bit window texture; the stone portal's pane; performance after 0.9.4 ("much better"); captures spread over frames;
  the pane as black depth plug + sprite at renderQueue 2450 (Mistlands mist covers it); 0.9.21 live fire and the
  fire-aware portal light (items 44, 45).
- **Confirmed in game on 2026-09-18 (0.9.22):** live fire incl. campfires; the sky-light split ("night mode looks
  much better"); the single-sided pane with real normals (the black stone-portal window at night was the game's
  ambient occlusion reading a disc without normals); the full-resolution sky-light layer (no outlines round
  branches); mist covering the window; the fire-aware portal light.
- **Built but not yet seen in game:** other players hidden from captures (item 51, needs a second player); the
  flame bloom at 4 (Max asked for more than 2, has not commented since); the torchlight layer at 1 - t (item 49);
  the blocky dissolve of 0.9.16; the swirl
  drawn over the picture (0.9.17); the 0.9.5 pre-cull refresh (the "tiny delay").
- **Open:** the sky mask is patchy in mist (item 49); link embeds show the old icon (not ours to fix: Hexium's
  og:image is a fixed URL, `cdn.hexium.gg/upload/1300/icon.png`, cached for a week; the file there is the new
  artwork). The numpad + - * / diagnostics of item 47 are still in, behind `DebugLog`. Older: the sky through a window is pinker than the real sky; a wall
  very close behind a portal stays soft.
- **How to work on this without the game:** `tools/LayerTest` redraws stored captures and numpad-5 dumps offline. Ask
  for a numpad-5 dump plus a screenshot instead of guessing: the dump is what the window holds, the screenshot is
  what reaches the screen. Item 38's lesson: do not stack untested changes on each other.
- Dead ends, each tried and documented below: grass as cards, grass painted on the ground, blurred fill, float window
  texture, `Particles/Standard Unlit`, the forward rendering path, flames with a depth of their own (three rounds).

## What the mod is (decided with Max)

See-through portals without loading the far side. On departure and arrival the mod captures the view from the portal
(colour + depth) and the paired portal shows it through a round pane with correct parallax. Constraints Max set:

- **No Unity Editor / no custom shaders.** Everything uses shaders already in the game build (the pane and the reliefs use whatever loaded shader passes the
  `WindowMaterial` self-test, `Sprites/Default` only as the pane's fallback, the game's own sky camera copied for live sky, and for the relief whatever loaded shader passes the
  `WindowMaterial` self-test). **`Particles/Standard Unlit` and Unity's stock cut-out shaders are NOT in Valheim's
  build**; always null-check `Shader.Find` and log it.
- **Local-only captures** (his PC), taken on both departure and arrival; numpad 0 captures the nearest portal on demand.
- Window visible from **8x** the portal activation range (was 4x until 0.8.16), dissolving in from the rim inward.
- **Both faces see-through, and the back of A shows the back of B** (physically consistent hole). `ArrivalViewBothSides`
  (off) would show the partner's front from both faces instead.
- Creatures in frame are captured frozen. Live sky and time-of-day tone match, plus a spill light when the far side is
  brighter. A full live window (loading the far zones) and instant transition were discussed and rejected as too heavy.

## How it works now (0.9.0)

- `Capture.RenderPoints`: a copy of the main camera at the ring centre renders six 90-degree faces for every capture
  point (`CaptureViewpoints`, default 4: centre, 1.1 m above, +/-0.75 m sideways, 0.7 m below; the extra points skip
  the up face; faces are 92 degrees wide so neighbours overlap), **all in one frame** so
  nothing animated differs between points. Per face: colour with the game's fog; a black-clear render with fog off
  (a pixel that stays black and has no depth is sky); and **per-pixel depth from the GPU** in a render of its own. Two reads are implemented, neither needs a
  shader of ours: `ZBuffer` (render into our own `RenderTextureFormat.Depth` buffer via `SetTargetBuffers`, blit it to
  an RFloat texture) and `DepthTexture` (`DepthTextureMode.Depth` + a command buffer that blits
  `BuiltinRenderTextureType.Depth`). On the first capture of a session `Probe` renders the down and forward faces with
  both, compares them (each also flipped / non-reversed Z) against sparse physics rays on solid colliders, keeps what
  agrees with at least 60 % of the hits and **logs the scores**. If nothing agrees it falls back to the old ray depth
  (`RayDepthPerPixel`). The sea draws without depth, so its depth comes from `ZoneSystem.m_waterLevel`. The portal, the
  local player and all windows are hidden during capture.
- `Layers.Process` (worker thread, plain arrays): splits each face into **foreground** and **background**. Foreground =
  things nearer than a morphological closing of the depth image (narrow enough to see around) plus the near side of
  every remaining depth edge, found per grid cell as a gap in its sorted depths. Every pixel is in exactly one layer, so
  silhouettes are cut by texture alpha at pixel resolution. The background texture is filled in under the foreground
  only from pixels that lie behind it (the fill carries depth); filled pixels get alpha 160 (`FilledAlpha`), captured
  ones 255, sky 0. Grids per face: `BgNode`, `BgCell`, `FgNode`, `FgCell` (see the summary comment on `Layers`).
- `ReliefMesh`: background = one continuous sheet, cells that hold two surfaces drawn at the far one; foreground = a
  quad per cell with any foreground pixel; **skirts** = walls along the view rays of every cell edge where the two
  cells disagree in depth, textured with the farther cell's background.
- `Storage`: `BepInEx\config\LivePortals\<world>\<zdoid>.txt` (`format=2`) plus per point `_p<k>_<face>.png`
  (background), `_p<k>_f<face>.png` (foreground, only if any) and `_p<k>.bin` (grids, 16-bit). Written on the worker
  thread with `ImageConversion.EncodeArrayToPNG`; the meta file goes last. Older layouts are ignored.
- `PortalWindow`: one per portal within range. Pane = disc, 2.7 x 2.8 m, centred at the model-bounds centre height
  minus 0.35 m (tuned by Max with the numpad keys). Off-axis frustum from the game camera through the pane (Kooima),
  **near plane on the pane**, camera orientation mapped through the portal (`rB * Yaw180 * rA^-1`). Each capture point
  is a relief anchored so the far ring lands on the near ring; secondary points are scaled 1 % / 2 % farther so the
  primary wins where they agree. The window camera renders **twice per frame**: sky + skirts, then (depth-only clear,
  HDR/MSAA off so both passes hit the texture directly) the reliefs: captured background (cut-off 0.82), the whole
  background pushed 4 % back (cut-off 0.4, so a fill loses to any point's real pixels of that surface but still hides
  what is far behind it), and the foreground. Relief meshes are enabled only while that camera renders. From the front
  the pane mesh is mirrored with a negative x scale (sprite shader ignores texture scale).
- `tools/LayerTest`: offline harness (`dotnet run -c Release -- <outdir>` after building the mod). Synthetic scene ->
  `Layers` -> software rasterizer from other eye positions beside the ray-traced truth, as PPM. `LAYER_ONLY=fg|bg|skirt`
  isolates a layer. Use it before asking Max to test geometry changes.
- Numpad keys (`TuneKeys`): 8/2 ring height, 4/6 forward, 7/9 width, 1/3 height, 5 print+save,
  0 capture nearest, . glass test (window shows its own portal with no mapping = should look like glass; Max uses it to compare a window with the real view).

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
6. 0.6.0 tested 2026-09-17 on the second PC (game-folder BepInEx, world "sails"): Max: "a lot better", but the
   background was projected onto foreground objects and cut-outs missed their silhouettes. The stored captures showed
   why: colour is per pixel, depth was one ray per 8 px cell, and anything without a collider (clouds, leaves,
   particles, distant terrain) took a guessed depth. The flat far backdrop also showed copies of near objects.
7. 0.7.0 (per-pixel GPU depth, two layers, skirts) was checked only in `tools/LayerTest`, **not in game**. First thing
   to read in `LogOutput.log` after a capture: the `depth from ...` line (which read won the probe) or the warning that
   none did. If it fell back to rays, the GPU reads need another approach in this Unity build. Then check silhouettes
   of creatures/trees, what shows when looking around a big near object, clouds, water, and the capture hitch
   (the log line gives render ms and worker seconds).
8. 0.7.0 in game (2026-09-17): the probe picked `ZBuffer, reversed Z` (2751/2985 rays agree; `DepthTexture` only
   ~600), so GPU depth works. But the windows showed live sky with dark scraps: the sky mask came from the
   `SetTargetBuffers` depth render, whose **colour output is black** in Valheim, so every bright pixel differed from
   the white-clear pass and was read as sky. 0.7.1 renders the mask separately. Captures take 430-740 ms of main
   thread (3 points x 6 faces x 3 renders + read-backs) under the black screen, then 1.5-2.7 s on the worker. The
   window camera now has HDR off and renders twice per frame; if the sky through the window ever looks wrong, that is
   the first suspect. 0.7.1 is untested in game.
9. 0.7.1 in game (2026-09-17): terrain back, silhouettes good ("close-up" forest view looks right). Max: still some
   clipping, and no fog / foliage / post effects, obvious on far views; trees through the glass test far too bright.
   Found by decompiling: (a) Valheim is deferred and its fog is the post-processing v1 stack's `FogComponent`
   (modified: `_SunDir` / `_SunFogColor`), so a bare camera copy gets none; 0.8.0 puts a `PostProcessingBehaviour` with
   a cloned profile (only fog + AO left on) on a second capture camera used for colour (the mask/depth camera must stay
   bare: image effects force an intermediate target and break `SetTargetBuffers`). (b) `InstanceRenderer` (grass) calls
   `Graphics.DrawMeshInstanced` itself, only inside the main camera frustum, LOD by main camera distance, and
   `ClutterSystem` builds one patch per frame; 0.8.0 queues all instance renderers within 64 m for the capture cameras
   and calls `ClutterSystem.UpdateGrass(0, true, pos)` before the arrival capture. (c) Drawing the stored captures
   offline showed sky-coloured slits where a near cell met a flat far cell with no skirt; skirts are now walls on every
   torn cell edge. (d) Hugin perched on the portal was in the captures. 0.8.0 is **untested in game**; the first
   capture logs a `capture look:` line (fog/ao state, rendering path, fog mode and density). Max's config had
   `DepthScale = 1.6` and `GlassTest = true` left over from experimenting; geometry is only exact at 1.
10. 0.8.0 in game (2026-09-17): Max: "looks much better"; remaining: clipping with very close terrain, and walls
    right behind a portal look strange. `tools/LayerTest capture` was taught to draw what the player sees (eye behind
    and above the ring, near plane on the ring plane, pane mask) and showed: (a) a bright crescent at the pane's bottom:
    the pane dips below the far ground, rays starting under the terrain escape the relief -> `ReliefMesh.GroundShell`
    (lower half of each face of the primary point, flat at 1.5 ranges, drawn in the under pass); (b) the log said
    `forward face 0% sky` on every capture: the 0.7.1 black+depth sky test fails because of a thin haze dome over the
    sky, so windows showed baked fog colour instead of live sky -> black/white difference restored, AND empty depth;
    (c) hairline cracks between cube faces and along skirt walls -> 92 degree faces (`FaceGrids.Tan`, grid file LP08)
    and walls 3 % longer at both ends; (d) grass at the capture point became streak curtains -> `GrassClearRadius`;
    (e) the most useful second viewpoint is above the centre, not beside it. A wall 0.3 m behind a portal is sampled at
    very grazing angles from the capture point and will stay soft; not addressed. 0.8.1 is **untested in game**.
11. 0.8.1 in game (2026-09-17): Max: "much better, still issues around structures" (portal inside an A-frame hut).
    Findings: (a) log still `0% sky`: the 0.8.1 rule (black/white differ AND depth buffer empty) fails too; the PNGs show
    a fully opaque baked sky. Either the game's sky haze/clouds write depth or the deferred path never clears a depth
    buffer passed via `SetTargetBuffers`. 0.8.2: explicit `GL.Clear` of that buffer + rule "differ AND depth >= range";
    log prints `x% sky (y% by colour)` so the next log tells which half fails. (b) His config still had
    `DepthScale = 1.6`, which misaligns everything; knob removed. (c) Window interiors looked flat/warm vs the real dark
    teal: Valheim's AO is **Amplify Occlusion** (`AmplifyOcclusionEffect` on the main camera, tint/intensity from
    `EnvMan` via `CameraEffects.SetEnvironmentAOParams`), not the post stack's AO ("ao off" in the look line);
    0.8.2 copies the component onto the colour camera. (d) Offline multi-viewpoint render showed saw-teeth along
    slanted beam rims from secondary viewpoints -> plane-fitted FG corner depths; viewpoints now need clearance.
    (e) Tint hue from sun+ambient, not fog. 0.8.2 is **untested in game**.
12. 0.8.2 in game (2026-09-17): sky finally works (`43% sky`), Amplify AO attached (`amplify ao 0.80 PostEffect`).
    Max: around structures "the background is rendered in front of the wood walls at some angles", distant trees
    through the house, and fire looks wrong. In his night screenshot the hut opening's edge is a staircase of big
    pale-blue blocks cutting into the wall. **Not reproduced offline**: `LayerTest capture` draws the same stored
    capture (all four viewpoints, many eye positions) with solid walls, so the stored geometry is right and the fault
    is on the Unity side or depends on an eye position I have not guessed. Suspects, in order: the blocks are skirt
    walls (pass one) showing where pass two leaves nothing, i.e. a legit disocclusion filled with one stretched texel
    row per wall (then cap skirt length / use the ground shell instead); mip-mapped alpha making the cut-off layers
    drop out at grazing angles (FG rims vanish at high mips); the depth-only clear between the two passes not
    happening. 0.8.3 adds the numpad-5 dump (both passes as PNG + `EYES=` line) to settle it: ask Max to press it
    while looking at a bad view, then run `EYES=... capviews` on the same capture and compare with the two PNGs.
    Fire: particles are depthless and got painted on the walls behind them; now hidden during capture.
13. 0.8.3 dump (2026-09-17): `debug/125308_1_333699_{under,final}.png` + `EYES=-0.588,0.861,-4.902 near 4.742 far
    100000 frustum l -0.7602 r 1.9341 b -2.2558 t 0.5383 ... path DeferredShading`. `LayerTest capture` with
    `EYES=` and `FRUSTUM=l,r,b,t,near` draws the game's exact picture: correct with a depth test; with `NO_DEPTH=1`
    (painter's order) it shows the game's artefacts (stair blocks of far haze over the hut's front walls; in game also
    the gravel beyond the floor showing through the floor's far end, which only a missing depth test can do). So the
    window camera composites the relief layers in draw order. **Root cause not identified**: candidates are the
    stripped variant of `Particles/Standard Unlit` not really alpha-testing / depth-writing, or the deferred path
    (the window camera is a copy of the sky camera: Deferred, far 100000). This is very likely what Max called
    "background projecting over the foreground" back in 0.6.0 too. 0.8.4: `WindowMaterial` self-test (depth / cut-out /
    opaque-above-cut-off, per candidate shader and rendering path, centre pixel read back, result logged as
    `window material self-test:`), fixed draw order as a fallback, fog off during the relief pass. **Untested in
    game.** If the log says nothing passed, the next idea is a depth pre-pass or dropping multi-viewpoint overlap.
14. 0.8.4 in game (2026-09-17): windows blank. Log: `window material self-test: ... ParticleUnlit: shader not in the
    build; StandardEmissive: shader not in the build; UnlitCutout: shader not in the build` and an NRE in `AddLayer`
    (null material). **That is the root cause of item 13 and of every "background over foreground" report since
    0.6.0**: `Shader.Find("Particles/Standard Unlit")` has always returned null here and `MakeCutoutMaterial` fell back
    to `Sprites/Default` (blended, no depth write). Valheim's own shaders are in compressed bundles (names/properties
    not readable offline; `globalgamemanagers` only lists Unity's built-in name table, which says nothing about what
    is included). 0.8.5: `WindowMaterial` enumerates `Resources.FindObjectsOfTypeAll<Shader>()`, tries every shader
    with `_MainTex` + `_Cutoff` (lit ones only via an emission map, found by name containing "emiss"), both rendering
    paths, three test renders each; logs the winner, the first results and the candidate list with the texture/colour
    properties of `Custom/*` shaders. No winner -> single viewpoint, sprite shader, opaque texels. **Untested.** If
    the log shows no winner, read the candidate list: the next step is a recipe for one of the `Custom/*` shaders.
15. 0.8.5 in game (2026-09-17): `window material self-test: using Custom/Creature (emissive)` (ff191c 17ff1c d1191c),
    first candidate, deferred path. Max: the front view of the A-frame "looks good now". So depth was the whole
    problem there. The candidate list in that log has the texture/colour property names of the `Custom/*` shaders.
    Remaining (dump `132403`, `front False`, EYES=-1.455,1.686,9.173): the back view, X-beams about 1 m behind the far
    ring, eye 9 m back. Through the gaps the eye looks in directions the centre viewpoint had blocked by the beams, so
    there is no data: the gaps showed skirt fans and, below the horizon, the ground shell painting the *beam* at
    infinity. 0.8.6: secondary viewpoints capture the back face too, shell only from cells >= 15 m, skirts/shell use a
    blurred (mip 4) copy of the texture. **Untested in game.**
16. Max (2026-09-17): the sky through a window is much pinker than the real sky. Likely cause: the window texture was
    8-bit; Valheim's sky is above 1 before the main camera's tone mapping (which the pane goes through too), so its
    blue channel clipped. 0.8.7: `_rt` is ARGBHalf (camera HDR still off so both passes render straight into it).
    **Untested.** If the sky is still off, compare a numpad-5 dump's sky pixels with a screenshot of the real sky.
17. 0.8.7/0.8.8 (2026-09-17): Max: "super orange", then "still acting super odd". **The dumps of those two versions are
    misleading**: `SaveWindow` read the new half-float (linear) window texture straight into a PNG, so they look dark
    and deep orange; gamma-corrected they match the captures, and the logged tints were mild (0.94,0.87,0.84 /
    1.07,1.15,1.20). So the 0.8.8 "the tint is the culprit" conclusion was drawn from a bad dump (the softer hue tint
    is harmless and stays). What the corrected dumps do show: the back view through the X-beams is now right
    (secondary back faces work); standing 0.4 m from the pane with the camera 1.5 m above it, the meadow behind the
    far ring is a field of shards: every grass tuft was a foreground card. 0.8.9: grass is queued for the colour
    camera only (depth sees bare ground), dumps go through an sRGB blit and log the window texture's alpha range
    (the pane blends by it and a float texture does not clamp). Whether the in-game colour is really off is **not
    established**: ask for a screenshot next to a dump.
18. Max then said the game itself looks like the uncorrected dumps (dark, deep orange), not only the dumps. So with the
    ARGBHalf window texture the pane really shows wrong, although the texture holds correct linear values (corrected
    dumps match the captures). Not understood (pane = `Sprites/Default` sampling the texture; relief material =
    `Custom/Creature` emissive on a deferred camera with HDR off). 0.8.10 reverts to ARGB32, the format of 0.8.5 which
    Max confirmed looks right. The pinker sky (item 16) is open again; do not retry a float texture without a way to
    see the result.
19. 0.8.10 in game (2026-09-17): colours right again (ARGB32). Max: "rendering weird around foliage", specifically the
    tall grass around the plains portal, and foggy weather. Screenshots: dark blade-shaped fill at the far edge of the
    hut floor, and from the hut the meadow as a jumble of shards with flakes above the horizon. Findings: (a) the
    game's own `InstanceRenderer` draws use camera=null, so they reach the capture cameras for whatever is inside the
    *main* camera's frustum; 0.8.9's "colour camera only" therefore gave grass depth in one direction and painted it
    onto the hills in the others; (b) four viewpoints each cut out the same tufts, 1-3 % apart in scale: shards;
    (c) the blurred skirt texture (0.8.6) bleeds near colours into the fill and shows as a texel grid in the under
    pass. 0.8.11: grass queued for both cameras again, foreground layer from the primary viewpoint only, skirts and
    shell sharp again. In overcast/fog the cloud dome is opaque, so the sky mask finds no sky and the grey sky is baked
    at the depth range (left as is: reads as "foggy over there"). **Untested.**
20. 0.8.11 in fog (2026-09-17): window white with dark blade shapes; the numpad-5 dump of the same moment holds the
    correct picture (hut wall, alpha 255), and after skipping time to clear weather the same view is right. So the
    window texture is fine and the *pane* is the problem: a `Sprites/Default` quad writes no depth, and Valheim's mist
    (and anything else reading the depth buffer: deferred fog, DOF, AO, soft particles) works from the depth of the
    real hill and sky behind the portal. 0.8.12: `WindowMaterial.MakePane` makes the pane a solid deferred surface
    (`Custom/Creature`, black albedo, the window texture as `_EmissionMap`), fading in by dissolve (`_Cutoff` against a
    64x64 noise alpha in `_MainTex`) instead of transparency; sprite pane kept only if no shader passed the self-test.
    Consequence to watch: the game's own fog now fogs the pane by the distance to the portal (correct), and AO may
    darken its rim. **Untested.**
21. Dump `142101` (2026-09-17, still 0.8.11: the 0.8.12 DLL was waiting for the game to close; glass test on, deliberately): through the gaps of the X-beams the under pass showed as a dark grid over the
    sky. Cause: skirt walls from a beam 1 m away to sea/haze 100 m away are huge flaps; pass two has nothing where the
    capture saw sky (alpha 0), so they stay visible. 0.8.13: walls only when both surfaces are nearer than
    `ShellMinDepth` (25 m); the shell (far-only cells, now all six faces, at the depth range) covers the far case by
    direction. (Max uses the glass test on purpose, to show a window next to the real thing it should match: keep the numpad . key.) **Untested**, as is 0.8.12's solid pane.
22. Stone portals (2026-09-17): Max asked for the pane to fit `portal_stone` (model about 8.7 wide, 4.3 deep, 7.0 tall;
    it had the wooden 2.7 x 2.8 pane). 0.8.14: `PortalShape` - wood keeps the tuned config values; any other prefab is
    measured with `Collider.Raycast` from the model centre against the portal's own solid colliders (up/down, then
    left/right, three passes), x0.97; fallback = wooden values scaled by model height. Overrides `OtherPaneWidth` /
    `OtherPaneHeight` / `OtherCenterHeight`; numpad 7/9, 1/3, 8/2 tune the kind of portal nearest the player. Secondary
    capture viewpoints spread with the opening. The log line `opening of <prefab>:` says what was measured and how
    (also printed for the wooden portal, as a check of the method against the hand-tuned 2.7 x 2.8 / centre 1.29).
    If a portal's collider is one box around the opening, rays start inside it and find nothing: fallback. **Untested.**
23. 0.8.14 in game (2026-09-17): the stone portal pane fits (log: `opening of portal_stone: 3.62 x 3.81 m, centre 2.36 m`,
    measured from 2 colliders; for `portal_wood` the rays found no frame, so that method does not work there and the
    tuned values stay). Max noticed the dissolve and asked for: start from twice as far, rim inward but still random,
    stone pane about 10 % bigger. 0.8.16: `RangeMultiplier` default 8 (one-time migration of a stored 4 via
    `ConfigVersion`), dissolve order = 0.65 x radius + 0.35 x random and finished at two thirds of the way in, measured
    panes x1.07 instead of x0.97 (stone: 3.99 x 4.20 m). **Untested.**
24. 0.8.16 in game (2026-09-17): from the hut, the plains meadow is "very strange" (dump `144349_1_333712`): cut-out
    grass cards over smeared, filled-in ground. Tried so far: grass with depth from 4 viewpoints, without depth,
    primary viewpoint only: a dense field of thin blades does not survive as a relief. 0.9.0 changes the approach:
    `GrassSet` - the capture cameras exclude the `InstanceRenderer` layer (the game submits its clutter draws to every
    camera, which is why "colour camera only" never worked), `GrassSet.Record` stores every clutter instance within
    45 m in the portal's frame (`<key>_grass.bin`, 3x4 floats each, grouped by clutter prefab name), and the window
    queues `Graphics.DrawMeshInstanced` for its own camera each frame with the prefab's mesh and material found via
    `ClutterSystem.instance.m_clutter`. Unknowns: `Custom/Grass` has a `_TerrainColorTex` (tint may come from the
    viewer's terrain instead of the far side's), it is lit by the viewer's sun and gets no far-side fog. Stone pane
    factor now 1.125 (4.20 x 4.42 m): Max still saw a gap at the bottom of the arch. **Untested.**
26. Performance (0.9.1, 0.9.2; Max reported 19 fps at his server hub with 3+ portals). 0.9.1: frustum cull per window,
    render only when the eye moved (`RenderWhenStill`), rank stride, secondary viewpoints only within
    `SecondaryViewpointRange` (12 m), `GrassMaxInstances`, `PerfLog` (10 s lines). 0.9.2: `Plugin.LateUpdate` is a
    scheduler; windows set `WantsRender` in `Update` and only the nearest `MaxRendersPerFrame` (2) call `RenderNow()`;
    `MaxWindows` (8) now caps loaded windows instead of rendering ones; eye-move threshold scales with distance;
    window RT drops to 1/2 and 1/3 resolution past 8 m / 20 m (`UpdateResolutionTier`); far windows load only the
    primary viewpoint (`Storage.Load(..., maxPoints)`, reload with hysteresis at `SecondaryViewpointRange` +6/+14);
    `RangeMultiplier` default 4 (ConfigVersion 2 migrates a stored 8). **Untested**: Max is to report fps before/after
    at the hub and the PerfLog lines.
27. 0.9.3, first-approach stutter (Max: "pretty hardcore stutter when walking up to a portal for the first time after
    logging in"). Cause: `Storage.Load` decoded every face PNG with `LoadImage` (~30 ms each, up to 40 of them) and
    `BuildReliefs` built ~100 meshes, all in one frame. Now `Loader.cs`: `CaptureLoader` reads, decodes (own managed
    PNG decoder `Png`, checked against real captures with `LayerTest png <file>`), builds mip chains (`Mips`) and the
    relief `Data` on a thread pool thread; `Step()` on the main thread creates textures (`LoadRawTextureData` of the
    whole chain) and meshes within `FrameBudgetMs` (4) shared by all windows. `PortalCapture` owns its meshes now
    (`Back/Skirt/Shell/Front`), `Relief` no longer destroys meshes. Windows exist `PreloadMargin` (15 m) beyond the
    visible range so the load is done before the dissolve-in. Viewpoint upgrade loads only the missing points
    (`CaptureSet.Append`), downgrade is `Trim(1)`. `GrassSet.Read` (any thread) + `Resolve()` (main). **Untested**
    in game.
28. 0.9.4, Max: "push extra hard to super optimize this, it should feel like vanilla performance". Where the time
    went: the window camera was a copy of the sky camera, so DeferredShading (G-buffer + lighting + shadow cascades
    per pass), two passes per redraw; captures did 21 faces x 4 renders + 84 `ReadPixels` in one frame (1.3 s, from
    the log); `FindObjectsByType<TeleportWorld>` twice a second. Now: `WindowMaterial` self-test tries the forward
    path first (`Setup.Forward`), `ApplyTo` turns occlusion culling off; `PortalWindow.RenderNow` is one pass with
    `QualitySettings.shadowDistance = 0` and fog off around `_cam.Render()`; skirts/shell use `MakeUnder`
    (Sprites/Default, queue AlphaTest-20/-15, no ZWrite) so the reliefs always paint over them (what the depth clear
    between the passes did); `ShouldRender` cadence 0/30/20 Hz by distance and rank, non-alloc frustum planes;
    `UpdateResolutionTier` sizes the RT to the pane's on-screen height with 1.4x steps and hysteresis; grass drawn
    only within SecondaryViewpointRange+4. Captures: `CaptureRun` (new file) renders `CaptureFacesPerFrame` faces
    per frame with `AsyncGPUReadback` (`Capture.CheckAsync` compares against a plain read once per session and
    detects a vertical flip; falls back to ReadPixels), `Capture.Compose` does the per-pixel work on a BelowNormal
    thread that also layers and saves each face as it arrives; `Capture.Hidden` hides the player/portal/windows
    only inside the render frames. Portals come from a postfix on `TeleportWorld.Awake` (`Plugin.AllPortals`).
    `PerfLog` now also logs a global line. **Untested** in game: watch for (a) the self-test log line saying
    "forward path" (if it still says deferred, the forward pass failed the test and nothing is gained there),
    (b) the async readback check line, (c) captures looking the same as before (flip detection), (d) skirts now
    blended rather than cut out.
29. 0.9.4 tested by Max: "much better now, they have a tiny delay when looking through them". Log: the forward path
    FAILS the material self-test (Custom/Creature emissive renders black in forward: `000000 000000 000000`), so the
    window camera stays deferred; even so a redraw is 1.7 to 2.4 ms (one pass, no shadows), all windows together 5 to
    16 ms per second. Async readback: usable, same way up. Captures: 11 frames, 120 to 250 ms in total (was 1.3 s in
    one frame). 0.9.5 for the delay: (a) the pose was computed in `Update` from the game camera's position, but
    `GameCamera` moves in LateUpdate, so the window was one frame behind; the eye-dependent half of `Update` is now
    `PortalWindow.Refresh(Camera)`, called with the scheduler from `Camera.onPreCull` of the game camera
    (`Plugin.OnCameraPreCull`), and `RenderNow` runs there too (nested camera render, as the stock water/mirror
    scripts do); (b) the eye-move threshold was 5 mm per metre = five pixels, now 0.7 mm per metre for rank 0;
    (c) rank 0 redraws every frame at any distance, the rest at 30 Hz ordered by staleness. **Untested.**
30. 0.9.6: Max could not share his Gale profile: 769 MB of captures sat in BepInEx/config/LivePortals. Captures now
    live in `Application.persistentDataPath/LivePortals/<world>` (LocalLow/IronGate/Valheim) or `CaptureFolder`;
    `Storage.Configure` (Awake) moves the old folder there once. Debug dumps go to `Storage.DebugDir()`.
31. 0.9.7 published on Hexium as Max/Immersive_Portals (https://valheim.hexium.gg/mods/Max/Immersive_Portals, zip at
    https://cdn.hexium.gg/upload/1300/0.9.7.zip): `publish-mod.ps1 -Zip dist\Immersive_Portals-0.9.7.zip -Categories
    Visuals,'Open Source','Valheim 1.0'` (categories came back empty, as with SailTrim). Package name has the
    underscore because Hexium allows no spaces; plugin NAME is "Immersive Portals", GUID/DLL/config/repo keep
    LivePortals. Zip = package\* + LivePortals.dll at the root. Later versions: bump manifest + VERSION, changelog,
    build, zip, publish (Hexium rejects duplicate versions; new versions of existing packages can take hours to list).
32. 0.9.8, Max (night screenshot): "the torches arent contributing since we arent rendering the flames". Two causes:
    flames are particles and were hidden for the capture; the tone match darkens torch-lit walls with everything
    else. Now `Capture.RenderLocalLight` renders each face once more into rig.A with directional lights off,
    ambient/reflections/fog off and the `_AmbientColor/_SunColor/_SunFogColor` globals black (slot 4 of FaceRaw);
    `Layers.Process` turns it into `FaceLayers.Local` (half res, background pixels only) saved as `_p0_l<i>.png`;
    `WindowMaterial` self-tests an additive shader (`AdditiveWorks`, `MakeAdditive`, gain calibrated with a
    half-bright texture) and `BuildReliefs` adds a "Glow" layer on the Back mesh, untinted (`Relief.Untinted`).
    `IsFlame` keeps particle renderers that are small (<3 m bounds) and within 1.5 m of a non-directional Light on
    the same net object. Spill light uses `AverageLocalLuminance` untinted. **Untested**: watch the self-test log
    for "local-light layer via <shader>" (if "NO additive shader passed", torchlight still follows the sun and the
    flames alone come back); check that the local pass really has no sun (a sunlit wall should be black in
    `_l` PNGs) and that flames are not smeared.
33. 0.9.9/0.9.10, night-dark window. Max's night screenshots: solid pane black, while numpad-5 dumps showed the
    window texture bright. 0.9.9 diagnostics (numpad * post-processing, - fog effect, + pane mode): sprite pane
    bright, additive pane bright, solid pane dark, fog off still dark, post-processing off bright. So an
    opaque-stage screen effect (Amplify Occlusion in PostEffect mode most likely; the mirrored disc gives it
    inside-out normals) multiplies the emissive pane; transparents drawn after it are untouched. 0.9.10: the
    pane is a black plug (`WindowMaterial.MakePlug`, emission black, still dissolves by `_Cutoff`) for depth, and
    `_overlay` (Sprites/Default, same mesh, 1 cm toward the viewer, alpha = dissolve) shows the RT on top in the
    transparent stage. Diagnostics removed. **Untested**: night look; misty weather (the plug should keep the
    mist from painting over the ring, the 0.8.12 problem); the dissolve-in now fades the sprite while the plug
    cuts out underneath.
34. 0.9.11, Max: the hearth's flames "thrown all over the back wall" (flame pixels had the wall's depth).
    `Capture.MakeProxies`: a depth-only quad (`WindowMaterial.MakeDepthOnly`) per kept flame, sized to its bounds,
    on FaceLayer, disabled; `RenderFace` step 4 (when NeedGpu and proxies exist): flames moved to FaceLayer and
    rendered alone into rig.A (slot 5 `FlameMask`), then the quads turned to face the capture point and rendered
    through `RenderDepthPass` with mask FaceLayer (slot 6 `ProxyGpu`). `Compose` gives flame-mask pixels
    (r+g+b >= 90) the proxy depth where nearer. `Layers.HalfRes` also makes `LocalFront` (foreground pixels'
    local light, `_p0_lf<i>.png`) drawn as "GlowFront" on the Front mesh. **Untested.**
35. 0.9.12: Max, on 0.9.11: torches right, "those pillars have chunks missing". The flame mask's glow spill (sum >=
    90) over the pillars behind moved those pixels to the flame's depth. Now sum >= 150 and the mask must be at
    least half the colour pixel's brightness. **Untested.**
36. 0.9.13: Max, "z fighting at certain angles" with two screenshots: same spot, ring fully black in one, the
    picture in the other. Not the plug/sprite pair (that would flicker in patches): the vanilla swirl under
    `TeleportWorld.m_target_found` is transparent on the same plane and sorts before or after our sprite by angle.
    `PortalWindow.SetSwirlHidden` disables its renderers while alpha >= 0.5 (`HideSwirl` config), restored on
    Hide/Cleanup; sprite offset 1 cm -> 3 cm. **Untested.**
37. 0.9.14: Max, "showing up both transparent and pixelated" (mid-fade screenshot): plug dissolving (64x64 point
    noise = blocky) under a half-alpha sprite. Now the sprite alpha = fade, the plug's `SetPaneVisible` gets
    (alpha-0.85)/0.15 so it only cuts in under the last 15%. **Untested.**
38. 0.9.15: Max: "we have regressed a bunch here". Honest tally: 0.9.10 (plug+sprite) caused the black ring at
    angles (swirl sort, fixed 0.9.13) and the blocky half-transparent fade (fixed 0.9.14); 0.9.11 (flame depth)
    caused the pillar chunks (fixed 0.9.12). He was still on 0.9.11 while reporting, none of the fixes seen yet.
    Added `PaneStyle` (Sprite = new, Emissive = 0.9.7 pane via `WindowMaterial.MakePane(rt)`), so the new pane can
    be backed out per user. Do not add more until 0.9.15 is confirmed at: day approach fade, angles, night,
    pillars after a recapture, misty weather.
39. 0.9.16, Max's three screenshots on 0.9.15: (a) rock/chest/ground "stretching out" (new since 0.9.4: skirts and
    shell were Sprites/Default blended without depth, painter's order); back to opaque cut-out `Make` and the two
    camera passes of 0.9.3 in `RenderNow` (`SetReliefsEnabled(under, on)` again), fog/shadows still off. (b) pillars
    still missing after a fresh capture: the hearth's glow billboards passed `IsFlame` and dominated dark pillar
    pixels; `MakeProxies` now skips renderers named glow/light/smoke/distort and the mask needs sum >= 330 and
    >= 3/4 of the colour pixel. (c) "the portals now fade, which undid the cool pixelated ring": new `Dissolve.cs`
    (32x32 cells with the old order formula; the plug's noise texture is built from the cells; the sprite is a
    per-cell mesh whose vertex alpha switches per cell) so plug and picture dissolve as identical blocks.
    **Untested.**
40. 0.9.17, Max on 0.9.16 (night screenshot): "still messed up and the portal effect isnt rendering". The fresh
    hall capture's Front texture had whole pillars, so the tearing is in the Front geometry: FgCell is one depth
    per cell, and a torch flame in front of a pillar dragged its cell to the torch. Flame depth is now
    `RawFace.FlameDepth` (Compose) applied in `Layers.Process` step 2b only in cells with no other foreground.
    The "portal effect" = the vanilla swirl I hid in 0.9.13; back on (HideSwirl false, ConfigVersion 3 migration),
    the sprite at renderQueue 2950 (before the swirl's 3000) and the plug 3 cm behind the pane so the swirl passes
    the depth test and draws over the picture. **Untested.**
41. 0.9.18, Max: "in the mistlands mist the portal is visible through all of it" (window crisp, frame hidden).
    The mist is composited from depth at the end of the opaque stage; a transparent-queue sprite draws after it.
    Sprite renderQueue 2950 -> 2450: forward-rendered (no G-buffer pass) at the end of the opaque stage, so
    G-buffer effects (the night darkening) miss it but the mist/fog composite covers it. If night goes dark again
    with this, the darkening effect is not G-buffer based and the queue has to go back to 2950 with the mist
    accepted. **Untested.**
42. 0.9.19, Max on 0.9.18: mist now covers the window (so the queue-2450 sprite works, and night was not
    reported dark), "rooms with torches are fully broken again": black blocks beside each free-standing torch,
    textures on disk clean. Step 2b promoted the standing torches' flame cells to foreground; the fill under them
    is dark and shows as blocks when the eye moves. `CaptureFlameDepth` (default false) now gates MakeProxies, so
    the flame passes and step 2b are inert unless opted in. Work it out in tools/LayerTest before turning it back
    on (needs FlameDepth support in the harness). Also: one "GPU could not hand back face" failure in the log;
    `Capture.DisableAsync()` now drops to plain reads for the session after a failure.
43. 0.9.20, Max: "fire is still a mess, last chance tonight": the hearth's flames painted on the back wall from
    four viewpoints = several stretched copies. `IsFlame` now keeps only flames with bounds under `FlameMaxSize`
    (1 m): torches yes, hearths no (light stays). Published to Hexium as Max/Immersive_Portals 0.9.20 and
    installed into Gale as a Hexium package (profile mods JSON in data.sqlite3 + Max-Immersive_Portals folder),
    replacing the hand-placed Max-LivePortals folder, so the profile can be shared.
44. 0.9.21 (2026-09-18, second PC). Max: "lets start with fire", and night windows "extremely dark even if the other
    side is a well lit room". **Fire**: the same move as grass. `Fire.cs` / `FireSet`: `HideForCapture` hides (as
    before) and lists every particle renderer within 40 m whose ZNetView root has a `Fireplace`, `Smelter` or
    `CookingStation` (`FireSet.Qualifies`); `Record` keeps, per topmost particle system under that root, the prefab
    name, the child-index path, the pose in the far ring's frame and the activeSelf flags of its objects
    (`<key>_fire.bin`, magic "LPFR"). `Build` instantiates that child of the prefab from `ZNetScene` under a
    switched-off stash (so no script ever wakes), strips everything but ParticleSystem(+Renderer), sets simulation
    space Local (the far ring's frame moves with the eye, world-space particles would trail), AlwaysSimulate,
    prewarm, layer FaceLayer, renderers off. `PortalWindow`: `_fireHolder` is posed at (anchor0, rB) like the grass,
    the renderers are enabled only around pass two of `RenderNow` (they are transparent, so they draw after the
    reliefs and are depth-tested against them), the holder is inactive while the window is hidden, and a still eye
    within 25 m gets 30 redraws a second when there are flames. Flames that do not qualify (locations) are still
    painted in when small (`CaptureFlames`). The flame-depth code is still there, off. **Untested**: watch for
    (a) the capture log line's "N live flame effects", (b) the log line "x of y flame effects play in the window",
    (c) flames visible in the main view at the mapped place (would mean a renderer stayed enabled), (d) flames
    trailing when the viewer moves, (e) soft-particle shaders against the window camera's depth.
    **Night**: not diagnosed, no log from the home PC here. Candidates: (1) no local-light layer (the additive
    self-test failed, or captures older than 0.9.8), so the whole picture takes the night tint of about 0.1;
    (2) the queue-2450 sprite is darkened by an opaque-stage effect as the emissive pane was in 0.9.9 (then item 41
    applies: back to 2950 and accept the mist); (3) the local-light pass renders black. The numpad-5 log line now
    carries the tint, the capture's sun/ambient, picture and local luminance, the number of local-light layers,
    whether an additive shader was found, and the live flame count: a dump that is bright while the screen is dark
    means (2), a dark dump with 0 layers means (1), a dark dump with layers and local luminance near 0 means (3).
    Meanwhile `Lighting.Tint(cap, strength, hasGlow)` holds the darkening back by avgLocal/avgLum when a window has
    no local-light layer.
45. 0.9.21 in game (2026-09-18, second PC, first build of it). Log: captures record the fires ("3 live flame
    effects" right after Max placed a `fire_pit`), windows report "3 of 3 flame effects play", additive shader found,
    6 local-light layers per window. His night screenshot of a torch-lit camp through a portal is bright and right:
    **the night darkness he reported on 0.9.20 did not show here** (it may be captures from before the local-light
    layer on the home PC; ask for a dump there if it comes back). Max: the campfire "looks like its lit up, but
    the actual campfire has no flames", then "the flames are being put behind alot of the layers". Cause: the
    window camera's `nearClipPlane` was 0.05 while its projection matrix has the near plane on the pane (metres);
    `_ZBufferParams` follows the property, not the matrix, so soft particles (every flame shader) linearised the
    depth buffer wrongly and faded out. `Refresh` now sets `_cam.nearClipPlane = near` before the matrix. (Tried
    and dropped within the hour: `forceRenderingOff` instead of the enabled toggle; the toggle does draw.)
    Max: "this portal isnt emitting light even though theres 3 fires directly infront of it": the spill light
    came from the face-0 averages only (there about 0.10 against here about 0.08). `FireSet.Sources` now records
    the strongest lamp under each fire's root (position in the ring's frame, colour, intensity, range; appended
    to `_fire.bin`, older files simply have none) and `UpdateLight` adds `0.6 * LightAtRing` (capped 2.5) for the
    fires on the side being shown. The dump line's `FireReport` lists particle counts, bounds and shaders of the
    flame copies. A later "0 live flame effects" at the same portal was right: the fire had burnt out.
    **Confirmed by Max on the second build** (screenshot + "both look right"): torch flames in place from every angle,
    the portal lights the floor in front of it. Log: 3 torches = 3 effects, 6 particle renderers, 81 particles alive.
46. 0.9.22 (2026-09-18). Max's night example: stone portal, pane pitch black, numpad-5 dump of the minute before
    fine (mean 31,45,44 at tint 0.46; the capture's mean is 71,86,89). Not the pane, not the missing glow: the
    tint. Max: "its like night just straight darkens all portals ... there's ambient light that isnt being
    represented", and, with a screenshot of real torches in tall grass under heavy bloom, "maybe the lack of bloom
    is a part of it". (a) **Sky light split**: `Capture.RenderLocalLight(rig, keepSky: true)` renders each face of
    the primary viewpoint once more through the colour camera with only the directional lights (and `_SunColor`)
    off (slot 7, `RawNoSun`); `Compose` makes `RawFace.Ambient` = noSun - local in linear light, capped by the
    picture; `Layers` halves it per layer (`_p0_a<i>.png`, `_p0_af<i>.png`); meta `avgAmbientLum`. Window: additive
    "SkyLight"/"SkyLightFront" layers on the primary Back/Front meshes (`Relief.Sky`), their strength set every
    tint tick by `WindowMaterial.SetAdditiveGain` to `ra^2.2 - sunTint^2.2` per channel (`Lighting.SplitTint`);
    the materials under them (`Relief.SunLit`) take the sun-only tint; every other material (filled sheet,
    secondary viewpoints, skirts, shell) takes `Lighting.MixedTint` = the picture's own sun/sky/torch shares
    followed to now. Needs `WindowMaterial.AdditiveScalable`. (b) **Additive calibration**: the self-test's quad
    has both windings and `Legacy Shaders/Particles/Additive` culls nothing, so grey came back 241 (4x) and the
    gain 0.53 was right only for double-wound meshes; the reliefs are single-wound (Custom/Creature has a cull
    switch), so the torchlight layer ran at half light since 0.9.8. The test now also measures one winding
    (`_addGain` for reliefs, `_addGainDouble` for the pane). (c) **Flame bloom**: `RenderBloom` = a third render,
    relief materials tinted black, additive layers off, flames on, into a 256 px texture; a second disc on the
    pane (`Particles/Additive`, queue 2451, gain `FlameBloom` = 2) adds it over the picture so flame pixels stand
    above white in the game's HDR frame. Only at full dissolve and within 40 m. **All untested.** Watch: the
    self-test line's "(one winding N)" (expect about 174 against 241); the dump line's "sky-light layers 6",
    "sun tint", "sky gain", "sky-lit share"; `_a` PNGs should look like the scene on an overcast day without
    torches; night through a fresh capture should match the darkness of the viewer's own surroundings.
    Seen and left alone: the black depth plug's disc has both windings on shared vertices, so `RecalculateNormals`
    gives it zero normals.
47. 0.9.22 in game (2026-09-18). Works: campfire flames, the one-winding calibration (grey 241 / one winding 178,
    gain 0.72), sky-light layers present, the wooden portal's window bright at night. **Still black: the stone
    portal outdoors at night, and it is NOT the tint**: its dump is bright (the moon's directional light is
    stronger than the dusk sun the capture was taken under: sun tint 1.2 to 1.4, sky gain 0) while on screen the
    pane is black with only the picture's sky faintly showing at the top. So something between the window texture
    and the screen darkens the queue-2450 sprite there and not at the wooden portal indoors the same night.
    Suspects: an opaque-stage screen effect (item 41's warning), ambient occlusion on the plug (its disc has
    zero normals, see item 46), the plug itself, or something of the stone portal's own drawn over the ring.
    Second build of 0.9.22 adds keys to tell them apart live: numpad + (picture queue 2450/2950), numpad - (plug
    off), numpad * (Amplify Occlusion off), numpad / (post-processing off). Also: `FlameBloom` default 4 (was 2,
    ConfigVersion 4 migrates; Max: "the bloom needs cranking up"), the bloom pass and the spill light now go by
    `Dissolve.Reveal(alpha)` (they were gated on alpha = 1, i.e. only within the portal's activation range: Max saw
    the light "get brighter the closer you walk up to it", and the bloom only ever showed within 5 m).
48. The keys' answer (2026-09-18): numpad + (picture at 2950) brings the stone portal's picture back, numpad *
    (Amplify Occlusion off) brings it back "perhaps a little better". So the game's ambient occlusion multiplies
    the plug's pixels, and the queue-2450 picture on them, to black. Third build of 0.9.22: the pane's quad and
    disc have ONE winding (toward -z) with explicit normals, and `Refresh` gives the pane a z scale of -1 from
    the front so that face and its normals always point at the viewer (children offsets times sz). Theory: the
    old disc's two windings on shared vertices gave `RecalculateNormals` zero vectors, garbage in the G-buffer.
    Not explained: why the wooden portal indoors was fine the same night with the same disc. If the stone pane
    is still black with this build, the normals were not it (the ring is 4 m deep: real cavity occlusion?) and
    the fallback is the picture at 2950 plus another answer for the mist. The four keys are still in.
49. Third build in game (2026-09-18): Max: "the night mode looks much better" (the single-winding pane with real
    normals: the stone portal's picture is no longer black; confirmed). His next screenshot: a stone portal whose
    pane is a flat blue-grey with a straight seam, "check for anything on this capture". The capture (1_334448,
    a foggy night by a campfire, seen at 45 degrees) is sound; the slabs come from the additive layers. The
    "torches only" render still shows everything that has a colour of its own: the haze dome, baked cloud, the
    sea. Those pixels were added to the picture a second time, on the primary viewpoint's sheet only, hence the
    straight edges against the far shell and the live sky. Fourth build: (a) `Compose` blanks the local layer
    and sets the sky layer to the picture wherever depth is at the end of the range; (b) the torchlight layer is
    no longer added whole: `PushTints` sets its gain to 1 - t^2.2 (t = the tint of the picture under it), i.e.
    picture * t + torch * (1 - t); before, a night capture seen at night showed torch-lit walls at double light.
    Still open from this capture: the sky mask is patchy in mist (the dome is about 90 % opaque, the diff > 60
    test flips blob by blob and face by face), so baked and live sky meet along hard edges. **Untested.**
50. Fourth build in game (2026-09-18): the mist covers the window correctly (Max's screenshot: nothing but mist);
    "the trees all have this outline now" at dusk (dump 133328: sun tint 0.36, sky gain 0.6, so most of the
    picture's light comes from the sky-light layer). The layer was stored at half resolution like the torchlight;
    but torchlight is smooth and sky light is the picture itself in another light, so its soft edges against the
    picture's sharp ones gave every branch a pale fringe. Fifth build: `Layers.OneLayer` keeps the sky-light
    layers at the picture's resolution (`_a`, `_af` PNGs are now 768 px). Needs a recapture. **Untested.**
51. Fifth build (2026-09-18): Max: "much better" (the full-resolution sky-light layer: no more outlines;
    confirmed). Request: "lets also not capture other players": travelling with a friend left the friend standing
    in front of the ring in the picture. `HideForCapture` now hides every `Player.GetAllPlayers()` entry
    (renderers, lights, colliders; a held torch's flame and light go too), not only the local one. Sixth build.
    **Untested** (needs a second player).
52. Performance after 0.9.22 (2026-09-18; Max: "its key that this is extremely performative"). No numbers yet:
    `PerfLog` was off all day (now on in the second PC's config). What 0.9.21/0.9.22 added: (a) a window with
    live fire redrew 30 times a second for a still eye, and ran the bloom pass (a third camera render) on every
    redraw; (b) a capture is 6 renders per face of the primary viewpoint (colour, torches only, no sun, sky pair,
    depth), 520 to 700 ms over 11 frames on this PC = eleven 50 ms frames; (c) textures: a fully loaded window is
    about 125 MB of uncompressed RGBA (4 viewpoints) plus 38 MB for the full-resolution sky-light layers plus 9 MB
    torchlight; eight of them at a hub would be over a gigabyte. Done, uncommitted, **untested**: `FireInView`
    (frustum test of the flame copies) gates both the 30 Hz still redraw and the bloom pass; the bloom pass runs
    every other redraw; `CaptureFrameBudgetMs` (10) stops a capture frame from starting a second face once the
    first took that long; the perf line now reports the game's texture memory, the video memory and the windows'
    share (`PortalWindow.CaptureMegabytes`). Not done, waiting for numbers: secondary viewpoints at half
    resolution (they only fill in what the primary could not see: 47 MB -> 12 MB per near window), the torches-only
    capture pass at half resolution, fewer loaded windows at hubs.
53. 2026-09-18, after 0.9.22 was published. Max (night screenshots): "see the whit ghostly stuff in the distance?"
    Far hills, fog and baked sky stood pale at night. Cause: the no-sun pass was rendered with the fog in its day
    colour, so the fog's own colour counted as sky-lit and was dimmed only by the ambient ratio; item 49's "far
    pixels are all sky light" made the same mistake for the dome. Now the no-sun pass runs with black fog
    (`RenderSettings.fogColor` and `_SunFogColor`), so the sky layer holds sky-lit surfaces as the fog lets them
    through and nothing of the fog, and far pixels are black in both additive layers: fog and dome dim with the
    sun tint. Needs a recapture. **Untested.**
    First perf numbers (second PC, 8 GB card, 120 fps): a redraw is 1.2 to 1.8 ms of main thread including the
    bloom pass; walking past a fire window cost up to 108 ms per second (63 redraws); standing still costs
    nothing; captures are now 21 frames of about 28 ms; **two fully loaded windows hold 245 MB of textures, nearly
    half of all the game's (532 MB)**. Added: `MaxWindowFps` (60: the nearest window no longer redraws on every
    frame of a 120 fps game), `HalfResSecondaries` (on: the loader drops the top mip level of the other
    viewpoints' pictures, about 35 MB per near window). Still to do if memory matters at hubs: far windows (primary
    only, 85 MB each) loaded without their top mip level and reloaded on approach. **Untested.**
54. 2026-09-18, Max on his server (world 754720490), night: "this looks a little odd": bright green blocks in a dark
    window. Dump 144544: the live grass, far brighter than the night picture around it. The window draws grass
    bare (no fog, no ambient occlusion, no shadows; the window camera has no post stack), the picture has all
    three baked in, and at night they are most of the darkness. `Capture.MeasureGrassGain`: at capture time a
    160 px view forward-and-down is rendered bare without grass, bare with grass (clutter layer added to the
    mask, shadows off, HDR off: as the window will draw it) and through the colour camera with grass (as the game
    shows it); over the pixels the first two differ in, the ratio game/bare in linear light is the capture's
    `grassGain` (meta, point 0; logged as "grass here is r g b times as bright"). `GrassSet.Draw` applies it to
    `_Color` through a MaterialPropertyBlock (the game's material stays untouched). Needs a recapture; without
    grass in the test view the gain stays 1. **Untested**; if the log line shows a gain near 1 at night the
    cause is elsewhere (the window camera's lighting of the grass, e.g. HDR off).
55. 2026-09-18, Max: "issues with big portals rendering small portals and vice versa" (no screenshot; assumed
    cause: the far ring was matched to the near ring centre to centre, so with a stone portal (opening 4.4 m,
    centre 2.36 m up) paired with a wooden one (2.8 m, centre about 1.3 m up) the far ground sat about 0.8 m
    above or below the near ground). The capture now records `ringHeight` (meta, per point) and `Refresh` shifts
    `anchor0` by up * (farH - nearH) / 2: lower edges matched, far world at its own size. Old captures (no
    ringHeight) behave as before. If Max means something else (scale, the pane showing the far portal's
    surroundings beside a small ring), ask for a screenshot and a numpad-5 dump.
    "the grass was popping in and out as i walked up to the portal": it was switched on at
    SecondaryViewpointRange + 4 m. Now drawn from + 12 m and grown out of the ground over the next 8 m
    (`GrassSet.Draw(..., grow)`, a y scale on each instance). **Both untested.**
56. 0.9.24 testing (2026-09-18, home PC). Max: "the portals seem to have a glossy sheen on them and reflect light".
    `Configure` zeroed only Unity's standard specular names; Custom/Creature's are `_MetallicGlossMap` and
    `_MetalColor` (see the shader list in the log). Now every property whose name contains metal/gloss/spec/
    smooth/reflect is set black (texture, colour or float). Also: Max's dusk captures (dayFraction 0.73-0.76,
    sun deep orange) look pale pink at night; the sky-light part of a sunset capture is dimmed only by the ambient
    ratio. Waiting for a numpad-5 dump (TuneKeys turned on in his config under [6. Debug]) and a night recapture
    to confirm before touching the tint. Perf at his hub: 8 windows, 28 fps, but the windows cost only 45 ms/s.
    The hand-placed Max-LivePortals folder (0.9.19) sat next to the Gale package Max-Immersive_Portals (0.9.22)
    on this PC; removed. Test DLLs now go into the Gale package folder.
57. 0.9.25 testing: Max, "weird outline on clouds" (day window, dark rim along every cloud edge). Sprites/Default
    premultiplies (rgb *= a) and blends over the plug; the live clouds write alpha < 1 into the RT at their soft
    edges, so those pixels went dark. The plug is now `MakePane(_rt)` (emission = RT) under the sprite instead of
    black, so at alpha < 1 the same colour shows through. The plug's night darkening can only affect those
    fringe pixels at weight (1 - a). Alternatives rejected: an RGB-only RT format (float formats show linear as
    sRGB, the 0.8.7 orange problem), Unlit/Texture (not in the build). **Untested.**
58. 0.9.26 testing: Max: "sometimes it doesnt capture a portals view when going into it, only coming out".
    Log showed arrival -> arrival with no departure between. `DepartureCapture` required `IsTeleporting()` after
    `DepartureDelay`; with the far side loaded (same base) Valheim 1.0's fast load completes the teleport inside
    the delay. Requirement dropped (the portal is still loaded, the player is elsewhere). `CaptureSeries` now
    waits up to 30 s for a running capture of the same portal instead of skipping. **Untested.**
59. 0.9.27 testing: Max's numpad-5 at window 1_439887 (shows 1_439874): tint 3.84, sun tint 4.0 (the clamp),
    captured under sun 0.002 (moonless), now under a moon: the sun ratio exploded and the dark 8-bit picture was
    scaled x4 into green blocks. `Lighting.MaxBrighten` = 1.5 caps every ratio (SplitTint, SkyChannel, Mix and
    the old Tint); the sun ratio's floor is 0.05. **Untested.**
60. 0.9.28 testing: sheen still there on 0.9.27 (KAE&ALBA window, greenish soft blob). Not specular: the
    `RenderBloom` pass (0.9.22) ran after `Grass.Draw` had queued the grass for `_cam` (DrawMeshInstanced is
    drawn by every render of that camera in the frame) with the reliefs on under a black tint, so grass and
    whatever the tint missed were blurred over the pane through the additive bloom quad. Now RenderBloom runs
    first in RenderNow, before the grass queue, with all relief renderers off and cullingMask FaceLayer; the
    black-tint trick is gone. Cost: a flame behind a pillar glows through it a little. **Untested.**
61. 0.9.29 testing: Max, "bog witch portal got skipped again" on 0.9.27. Log: "could not capture: the portal went
    away during the capture" (the departure zone unloaded mid-render after a fast-load teleport) and one "GPU could
    not hand back face" loss. `CaptureRun.Hurry` (departures): all faces in one frame, no frame budget;
    `DepartureDelay` default 0.2 (ConfigVersion 4 lowers stored values above it); `CaptureSeries` retries once
    with plain reads after a read-back failure. **Untested.**
62. 0.9.30 testing: on 0.9.29 the first departure capture failed "could not hand back face 5 of viewpoint 3" and
    the retry could not run (portal gone). Cause: Hurry issues ~126 AsyncGPUReadback requests in one frame.
    `Capture.RenderFace(rig, f, sync)` / `Read(..., sync)`: departures read synchronously; arrivals stay async.
    **Untested.**
63. 0.9.31 testing: Max, "it takes about 2 seconds to load in a frame now after walking through, it has to be
    seamless". Hurry (one-frame departure, 0.9.29/0.9.30) removed. Player.UpdateTeleport (decompiled) moves the
    player at m_teleportTimer > 2 s, so there is no fast-load race; "the portal went away" came from the 0.9.26
    busy-wait (the arrival worker still storing 4 to 9 s, the departure waiting behind it). Now `_running`
    (Dictionary<ZDOID, CaptureRun>): a newer capture renders immediately with `StoreAfter` = the earlier run, and
    its worker waits (up to 60 s) for that run's Done before Storage.Clear. Async reads for all captures again
    (sync parameter kept, unused). **Untested.**
64. 0.9.32 testing: Max, "still taking a while for it to pop up after going through" and "the portal faces still
    has a gloss". (a) `CaptureRun.Work` now layers faces on ThreadPool threads (ProcessorCount/2, max 6) as they
    arrive, publishes `Layers` + `Ready` and registers in `_readyRuns`; `CaptureLoader.FromMemory` builds the
    window's textures from those arrays (`QueueArray`) and `PortalWindow.Update` (check every 0.25 s) prefers a
    ready run with a newer TakenAt over the files; the files are encoded afterwards, also in parallel, behind
    `StoreAfter`. The log line splits "layered in" from "stored in". `GrassSet.Budgeted()` factors the budget
    out of Read for the recorded set. Memory: all layers of a capture stay in RAM until its files are written.
    (b) gloss: the zeroing texture was opaque black; alpha is smoothness in these shaders; now clear black.
    **Untested.** If the window still lags, the remaining latency is the scan (0.5 s), the loader budget
    (4 ms/frame) and the arrival capture's frames.
65. 0.9.33 testing: on 0.9.32 Max: "stutters pretty badly for a couple seconds after going through, 12 fps from
    70". Log: layered in ~2 s with 6 ThreadPool threads (16 cores), rendered over 12-19 frames at ~25 ms. Now
    `Spawn` = dedicated BelowNormal threads, `CaptureThreads` (3) gates both layering and encoding,
    `CaptureFacesPerFrame` 1 (ConfigVersion 5 migrates a stored 2). If it still stutters, next suspects are the
    collector (Layers.Process allocations) and the memory loader's uploads (FrameBudgetMs 4). **Untested.**
66. 0.9.34 testing: Max's night screenshot (KAE&ALBA) on 0.9.32: a soft green glow beside the torch in the
    window; his numpad-5 dumps of the same moment show clean window textures, so it is added over the picture:
    the bloom quad (FlameBloom 4). Set FlameBloom = 0 in his config for the next launch as the test; numpad 5 now
    also saves the bloom RT (`SaveTexture`, tag "bloom"). If the green goes with FlameBloom 0, the bloom pass
    still catches something green (grass queued for `_cam`? the fire prefab's own glow?): read the bloom dump.
67. 0.9.35 testing: Max: "there seems to be a shadow that's applied, a shadow that has holes in it" (night, the
    KAE&ALBA window dark except bright green patches by the torch; his dumps of the window texture were clean).
    Reading: Sprites/Default premultiplies by the RT alpha; the RT alpha after the deferred passes is not 1 over
    the reliefs, so the sprite went dark there and the plug (emissive, AO-darkened at night) showed through; the
    live grass is forward-rendered with alpha 1 = the bright holes; the same defect made the day-time gloss
    (torches mirrored in the plug) and the cloud rims. `WindowMaterial.MakePicture` + a self-test: a green
    texture with alpha 0 over an opaque black quad must come out green; candidates Sprites/Default with
    `_AlphaTex` white + `_EnableExternalAlpha` + keyword ETC1_EXTERNAL_ALPHA, then Particles/Additive (Soft)
    variants (gain calibrated). With one found (`PictureIgnoresAlpha`) the plug is black (`MakePlug`) and
    `SetPaneTexture` is skipped; `Dissolve.Apply` paints hidden cells (0,0,0,0) so an additive picture shader
    hides them too. Watch the self-test line for "picture drawn with ..., alpha ignored" or "NO picture shader
    ignores alpha". **Untested.** If none passes, the next option is forcing the RT alpha to 1 after the passes
    (no stock shader writes alpha alone) or an RGB-only RT format with a gamma fix.
68. 0.9.36 testing: on 0.9.35 four departures failed "the portal went away during the capture", each next to a
    perf line at 9 fps with 0 windows: distant teleports load the far zones during the 2 s and the 21-frame
    capture did not finish. `CaptureRun.Departure` renders >= 3 faces per frame with 3x the frame budget and sets
    `RenderedAll` when every face is issued; `Plugin.HoldTeleportForCapture` (each Update) clamps
    `Player.m_teleportTimer` to 1.5 s while the departure run has not RenderedAll. The self-test on this PC:
    "picture drawn with Sprites/Default, alpha ignored" (0.9.35 confirmed; night windows match their dumps).
    **Untested.**
69. 0.9.37 testing: Max's five screenshots of one window (an interior, blue milky wash, gold-pink patch that shifts
    with the angle), numpad 5 at each: the dump shows the same wash, so it is in the picture. Dump line: shows
    1_439902, captured under sun (0,0,0) ambient (0.2,0.36,0.46), now under a moon: sun tint 1.5 (MaxBrighten),
    sky gain 0. `SplitTint` now blends `ambRatio` and `sunRatio` by `sunShare` = 0.65*lum(cap.Sun)/Level(cap):
    a sunless capture follows the ambient ratio only. The pink patch is the torchlight layer over the smeared
    skirt geometry at oblique angles (not touched). None of the numpad diagnostics changed the look, which
    confirmed the pane is clean. **Untested.**
70. 0.9.38 testing: gloss still there on 0.9.37 ("i guess i just have to accept it"). Offline: tools/LayerTest
    `capture` on 1_439902 from the dump's EYES (the harness now reads the PNGs through `Png.Decode`, no .rgba
    exports needed) draws a clean yard: the film is not in the layers, it is the game's lighting of the reliefs
    in the window camera: F0 0.04 Fresnel on black dielectric surfaces reflecting the sky (blue film) and the
    torches by the near ring (gold-pink), angle dependent. `PortalWindow.LightsOff/LightsBack` (point/spot
    lights found every 2 s, disabled around the render) and `RenderSettings.reflectionIntensity = 0` for the
    window passes; directional lights stay for the grass. If a sheen remains, it is the directional light's
    specular: next step would be a metallic-1/black-albedo G-buffer (map (255,0,0,0)) so the specular colour is
    black. **Untested.** Caveat: live grass in the window is no longer lit by torches near the viewer's ring.
71. 0.9.39 testing: Max, "really crazy FPS drops at the portal hub" on 0.9.37. Perf lines: "8 windows exist ...
    captures 0 MB, game 7 fps" (the eight loaders starting together, ThreadPool normal priority), and 22-41 fps
    stretches with window redraws at only 50-115 ms/s; fully loaded the hub holds 704 MB of capture textures.
    `CaptureLoader.Spawn`: BelowNormal threads through a SemaphoreSlim(2). `PortalWindow._fireSystems`: the live
    flame ParticleSystems are paused unless the window was drawn within the last second (Fire.Build sets
    AlwaysSimulate, so nothing else stops them). If the hub still drops: texture memory (704 MB: half-res for far
    windows, fewer viewpoints) and the number of loaded windows are the next levers. **Untested.**
72. 0.9.40 testing: Max: "lots of complaints about game stuttering" from other players on 0.9.39. Two things done
    blind (no logs from them yet): (a) `LightsOff` no longer scans (`FindObjectsByType<Light>` every 2 s, added
    in 0.9.38, is a hitch in a big base); `Patches.ZNetScene_CreateObject` registers lights of created objects
    (`PortalWindow.RegisterLights`), pruned every 30 s; pieces placed locally bypass CreateObject until reload.
    (b) `Quality` presets (`Plugin.ApplyQuality`): Low 384/1 viewpoint/4 windows/20 Hz/1 thread, Medium
    512/2/6/1 redraw/30 Hz/2 threads (default for new files), High = the old defaults; ConfigVersion 6 sets
    existing files to High. Ask the complainers for version, when it stutters, PerfLog logs and specs. **Untested.**
73. 0.9.41 testing: Max: "make the player camera not able to collide with the portal so players dont see the back
    of it when teleporting". GameCamera.GetCameraPosition -> CollideRay2 sphere-casts against m_blockCameraMask,
    which the frame's colliders are in. Harmony prefix/postfix on `GameCamera.CollideRay2`
    (`Patches.GameCamera_CollideRay2_*`): colliders of portals within 4 m are disabled around the casts
    (+ Physics.SyncTransforms), re-enabled after; the teleport trigger and walking collision are physics-step
    matters and unaffected. Also: "constant fps issues when at all near them" (other players): no logs yet;
    Medium preset halves the per-frame work; if their PerfLog shows window cost over ~20% of the frame, the next
    cut is rendering pass one (sky + skirts) only every other redraw. **Untested.**
74. 0.9.42 testing: Max: "automatically set the capture resolution depending on their graphics settings, at 480p
    crank it down by a ton". `Plugin.CaptureRes` / `WindowRes` = min(setting, round(Screen.height*0.75/64)*64,
    >= 256) when `AutoResolution` (on); used by `Capture.MakeRig` and the window's RT sizing. The loaded line logs
    the values. **Untested.**
75. 0.9.43 testing: Max on 0.9.41: "still considerable frame drop" and "the camera does not stay behind you".
    Perf lines: hub steady 55-78 fps with windows at ~110 ms/s; the drops are the capture (21 frames x 45 ms)
    and the 10 s after arriving (8 windows loading, 12 fps, almost no redraws). Done: `FireSet.BuildItem`
    (one effect) with `PortalWindow.BuildFire` copying two per frame; `BufferPool` (exact-size byte[] reuse) in
    Png.Decode, Mips.Chain, QueueTexture/QueueArray, returned after Upload; the window hides while the player is
    inside its ring (|along| < 1.1 m, lateral < 1.6 m) or IsTeleporting within 3 m, so the camera never faces the
    back of the picture (the CollideRay2 collider trick of 0.9.41 evidently did not move the camera). Not done:
    the capture's 45 ms frames (local/sky-light passes at half res would cut ~30%). **Untested.**
76. 0.9.44 testing: Max: "do we need to do a capture as frequently as we are?" / "im good with these ideas".
    `Plugin.CapturedRecently` skips a departure or arrival capture when the portal has one (in memory via
    `CaptureRun.TryGetReady` / `_running`, or on disk via `Storage.StoredTime` + new `StoredDayFraction`)
    younger than `RecaptureAfter` (300 s) with the day fraction within 0.06 (circular). The two light renders
    go into a half-size RT (`Rig.L` / `TexL`, read at res/2, slots 4 and 7) and `Capture.Upsample` brings them
    back to res in `Compose` (bilinear; `SideOf` for the flip), so Layers/Storage are unchanged. Not done: skip
    the no-sun render when a face has no sky (needs sky known before it renders), skipping a capture while fps
    is already low, pass-one reuse across redraws. **Untested.**
77. 0.9.45 testing: Max on 0.9.44: "still getting lag issues right after teleporting". Log: Ryzen 5800X /
    RTX 5070; captures render in 7-21 frames (~330 ms), layered 2-3 s, stored 3-4 s; the perf fps is a single
    smoothDeltaTime sample so the stutter never showed. Hypothesis: the collector (a capture allocates ~1 GB of
    short-lived arrays across readbacks, Compose, Layers and PNG byte[]s; Unity's collector stops the main
    thread per pass). Done: `Pool<T>` (exact-length arrays, 160 MB budget, `Pool.cs`) through FaceRaw.Collect /
    Capture.Read (readbacks), Compose (sky, ambient, flame depth, MetricDepth, Upsample), Layers.Process (fg,
    state, back, front, carried, HalfRes, OneLayer) with `RawFace.Release` after Process and `FaceRaw.Release`
    after Compose; the layer outputs go back via `CaptureRun.HoldLayers/DropLayers` (the run holds one, each
    FromMemory loader one; FromMemory falls back to Start when the run has let go); PNGs through
    `EncodeNativeArrayToPNG` + `FileStream.Write(ReadOnlySpan)` with a managed fallback (`Storage.WritePng`).
    PerfLog: frames over 33/100 ms, longest, GC.CollectionCount, heap, pool, loads, captures; "LivePortals trip:"
    line 8 s after each arrival; loaded line says whether the collector is incremental. **Untested.** If the trip
    line still shows collections with long frames: the remaining allocations are Grass/Fire records, ReliefMesh
    data, the loader's mesh building; if it shows long frames without collections, it is main-thread work
    (window builds, uploads, the capture's own frames).
78. 0.9.46 testing: Max on 0.9.45: "the instant I go through the portal the view goes away while the portal
    screen fades to black, which kinda breaks the immersion". Two causes: the 0.9.43 in-ring Hide (also on
    IsTeleporting), and the departure capture at 0.2 s hiding the player, the portal and all windows for its
    render frames while the fade (Hud.UpdateBlackScreen: MoveTowards 1 over GetFadeDuration = 1 s) is still
    transparent. Done: `PortalWindow._recess` (Update: when the player is within 1.6 m laterally of the ring or
    teleporting, target = clamp(0.7 - u, 0, 1.6) where u = the player's signed distance on the camera's side;
    MoveTowards 4 m/s); Refresh moves c back by the recess on the camera's far side (reliefs follow) and scales
    the pane by toPane / (toPane - recess) so it keeps filling the frame from the eye. `DepartureDelay` default
    1.0 s (configVersion 7 raises values under 1.0); Player.UpdateTeleport never moves before 2 s and
    HoldTeleportForCapture still clamps at 1.5 s while rendering. **Untested.** If the frame's inner edge shows a
    gap at oblique angles with the pane recessed, raise the scale clamp or cap the recess lower.
79. 0.9.47 testing: the 0.9.44 log (bouncing 1_441084 <-> 1_441090) showed "window at 1_441090 shows capture
    1_441084" three times per trip: FromMemory (capTime = new TakenAt), then, once that loader was null, the
    else branch saw stored (old takenAt) != capTime with no loader and reloaded the OLD files, then the new
    files when stored. Fix: an older stored time never replaces a shown or loading set
    (`!(stored < _capTime && (_loader != null || _set != null))`). Each spurious load rebuilt reliefs, uploaded
    textures and rebuilt 32 flame effects right after arrival: a real part of the post-trip stutter. **Untested.**
80. 0.9.48 released 2026-09-20 (main 0698ac3, Hexium cdn.hexium.gg/upload/1300/0.9.48.zip). Max on 0.9.47:
    screenshots of a huge smeared oval hanging beside the portal frame in the Swamp. Cause: the 0.9.46 pane
    recession (`_recess`, pane backs away and scales up to keep filling the frame) only holds from straight on;
    from any other angle the enlarged pane sits outside the frame and the off-axis frustum stretches the view.
    Fix: the recession is gone entirely; instead `Refresh` fades the picture out over the last 0.45 m as the eye
    reaches the pane (`alpha *= clamp01((d - 0.06) / 0.45)`, Hide below 0.06). Stepping through still thins out
    rather than cutting off (the 0.9.43 complaint) and nothing leaves the frame. **Untested by Max.**
    Open from 0.9.45's perf lines: trips still average about 50 fps over the 8 s after arriving, with single
    frames of 100-300 ms and a few collector passes; the remaining suspects are the loader's mesh building,
    Grass/Fire records and ReliefMesh data, none of which are pooled yet.

81. 0.9.49 testing (2026-09-22, second PC). Max: "after restarting my game or for other reasons portals stop
    showing their other side ... only my most recently travelled portals are see through, where most of the portal
    hub isnt". Cause, found by reading `PortalWindow.Update`: the window took `tz = ZDOMan.GetZDO(target)` and
    hid outright when it was null, and the only thing it used `tz` for was `tz.GetRotation()` at the pane
    geometry. A dedicated server sends a client only the ZDOs near it, so after logging in every partner in an
    unloaded zone is null; the ones you have just travelled through are in memory, which is exactly the portals
    Max still saw working. (`ZDOMan.RequestZDO` -> `RPC_RequestZDO` -> `ForceSendZDO` does exist and the send
    filter `ShouldSend` would pass, so in theory it should arrive; it evidently does not help in practice, and
    the window should not need the network for this at all.) Fix: `RawPoint.Rotation` = the portal's world
    rotation at capture time -> meta `p<k>.rot=x,y,z,w` (`Storage.Q`/`PQ`) -> `PortalCapture.Rotation` +
    `HasRotation`; `Update` no longer hides when `tz` is null, `rB` prefers the live ZDO and falls back to the
    stored rotation, and only hides when neither exists (a capture from before this version). `RequestZDO` is
    still sent, every 10 s once a stored rotation is available, every 1 s otherwise, for old captures.
    **Untested.** Note for Max: existing captures carry no rotation, so each pair needs one more trip; after that
    it holds through restarts. Second, separate cause of "most of the hub isnt": `MaxWindows` (4/6/8 by preset)
    caps how many windows load at once, nearest first; unchanged, it is a memory trade.
82. 0.9.50 testing (2026-09-22). Max on 0.9.49: "still not seeing portals ive definitely have gone through".
    0.9.49 stores the rotation at capture time only, and `RecaptureAfter` (300 s) means a trip through a portal
    captured recently takes no new capture, so a pair used often never gained a rotation and stayed blank after
    every restart. `Storage.RememberRotation` now appends `p0.rot=` to a stored capture the moment the far portal
    is loaded (on the trip back you have just stood in it), for captures written before 0.9.49; the window sets
    `HasRotation` in memory at the same time so it is a single file append. Also `PortalWindow.BlankReason` +
    `Plugin.ReportBlanks`: every 20 s, while any window in range is blank, one Info line saying how many and why
    (never captured / captured before 0.9.49 and the partner not loaded / over MaxWindows / not paired /
    loading), six lines a session unless `DebugLog`. **Ask Max for that line next time rather than guessing.**
    **Confirmed by Max on 2026-09-22: "much better".** The hub is see-through again after a restart. 0.9.50 is the
    tip of `testing`, merged to `main` and published on Hexium as 0.9.50 the same day.
    Ruled out while looking: portal connections do reach a client intact (ZDO.Deserialize reads the connection as
    a real ZDOID, and `Game.ConnectPortalsCoroutine`, which clears a connection whose target is not loaded, is
    server-only), so `GetConnectionZDOID` is sound on a client.
25. Not yet done: remove the diagnostics before 1.0 (see below; Hexium publishing is done, item 31) (`publish-mod.ps1` + `hexium-token.txt` next to it, gitignored; copy the token from
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
