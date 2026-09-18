# Pick-up notes for LivePortals

Last updated 2026-09-17 (afternoon Central) on Max's second PC, right before switching computers again. `main` is the
current dev state, **0.9.0**. Not published to Hexium. Max tests each build and reports with screenshots; on the
second PC the mod runs from the game folder's own `BepInEx\plugins` (world "sails"), on the home PC from the Gale
"Flotilla" profile.

## State at hand-over (read this first)

- **Confirmed in game:** per-pixel GPU depth (`ZBuffer, reversed Z`); sky detection; fog + Amplify Occlusion in captures;
  the relief material chosen by self-test (`Custom/Creature (emissive)`) which fixed every "background in front of the
  foreground" report; the front view into a building; the back view through beams; correct colours with the 8-bit
  window texture; the stone portal's measured pane (now x1.125); the dissolve-in exists and Max likes the idea.
- **Built but not yet seen in game:** 0.9.0 live grass (`GrassSet`: grass left out of captures, its instances recorded
  and drawn as real geometry in the window); 0.8.16 dissolve from the rim inward starting at 8x range; 0.8.12 solid,
  depth-writing pane (the fix for foggy weather wiping the window out); 0.8.13 skirt walls only between surfaces nearer
  than 25 m, far shell all around.
- **Open:** the sky through a window is pinker than the real sky (item 16/18: a half-float window texture made the
  whole window dark orange in game, reason unknown, reverted); a wall very close behind a portal is sampled at grazing
  angles and stays soft; captures freeze the game for 0.7 to 1.0 s (4 viewpoints, 21 faces, 4 renders each).
- **How to work on this without the game:** `tools/LayerTest` (see below) redraws Max's stored captures and his
  numpad-5 dumps offline. Ask for a numpad-5 dump plus a screenshot instead of guessing: the dump is what the window
  holds, the screenshot is what reaches the screen, and twice the difference between the two was the whole story.
- The test history below (items 1 to 24) is long but every item names what was tried and why it failed; several
  obvious ideas are in there as dead ends (grass as cards, grass painted on the ground, blurred fill, float window
  texture, `Particles/Standard Unlit`).

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
