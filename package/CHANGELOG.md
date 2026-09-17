# Changelog

## 0.9.2

- Hubs: windows are no longer capped at a few. Up to `MaxWindows` (8) stay loaded, but only the nearest `MaxRendersPerFrame` (2) of those that need a new picture are redrawn in any frame; the rest keep their last picture until their turn. Far windows redraw only after a larger step of the eye and at a fraction of the resolution, and hold only their primary viewpoint until you come within reach.
- Range halved again: the window dissolves in from 4x the activation range (about 20 m). A stored 8 is brought down once.

## 0.9.1

- Performance. A window is no longer redrawn when its pane is outside the game camera's view, nor while the camera stands still (the picture depends on where the eye is, not where it looks; a still window refreshes twice a second for the sky). Windows other than the nearest redraw a third as often. The extra capture viewpoints are drawn only within 12 m of the pane (`SecondaryViewpointRange`). Live grass is capped at the 4000 tufts nearest the far portal (`GrassMaxInstances`, 0 turns it off). `MaxWindows` default 2. `PerfLog` logs each window's render time every 10 s.

## 0.9.0

- Grass is no longer part of the captured picture. The capture records where every tuft of the game's instanced clutter stands within 45 m of the portal, and the window draws the game's own grass meshes with the game's own material at those places: real geometry, right from every angle, moving in the wind, lit by the current hour. As a picture a meadow was a stack of cut-out cards over smeared ground and fell apart from any eye but the capture's. `GrassGap` (0.75 m) keeps blades out of the ring itself; `GrassClearRadius` is gone.
- Measured panes (the stone portal) are another 5 % larger: 4.20 x 4.42 m on the stone portal.

## 0.8.16

- The window starts to dissolve in from twice as far (`RangeMultiplier` 8, about 40 m; an existing setting of 4 is raised once).
- The dissolve runs from the rim of the ring inward, still ragged.
- Measured panes (the stone portal) are a tenth larger: 3.99 x 4.20 m on the stone portal.

## 0.8.15

- The dissolve-in of the window finishes by two thirds of the way in, so no stray holes linger when you are close.

## 0.8.14

- The stone portal (and any other portal that is not the wooden one) gets a pane of its own size: the opening is measured from the portal's own colliders (inside of the frame to the left, right, above and below the middle of the model), and captures are taken from the middle of that opening with the extra viewpoints spread to match. `OtherPaneWidth`, `OtherPaneHeight` and `OtherCenterHeight` override the measurement; the numpad tuning keys adjust whichever kind of portal you stand nearest to. The log states what was measured per portal type.

## 0.8.13

- Looking through gaps (beams or a lattice right behind a portal): the fill no longer covers the sky with a grid of stretched flaps. Skirt walls are only built between surfaces that are both within 15 m; for anything farther, the far shell, now all around instead of only below the horizon, shows what was in that direction, which for distant things is almost exact.

## 0.8.12

- The pane in the ring is now a solid, depth-writing surface (the same tested game shader as the reliefs, showing the window as emission). As a transparent sprite it was invisible to the depth buffer, so foggy weather drew its mist at the thickness of the hill and sky behind the portal: a white window with the real grass behind it showing through as dark blades. Fog, depth of field and ambient occlusion now see a surface at the ring.
- The window therefore no longer fades in by transparency; it dissolves in through a noise pattern over the same distances.

## 0.8.11

- Foliage: the foreground layer (grass, leaves, posts, rims) is drawn from the primary viewpoint only. Four slightly different cut-outs of the same tufts made a jumble of shards. The other viewpoints still fill in background.
- Grass is drawn for the depth camera again (0.8.9 took it out, but the game's own grass draws still reached that camera inside the player's view, so grass had depth in one direction and was smeared over the hills in the others).
- Skirts and the ground shell use the sharp picture again; the blurred copy bled near colours into the fill (dark, blade-shaped fill around grass) and showed its texels as a grid.

## 0.8.10

- Back to an 8-bit window texture. With the half-float one (0.8.7 to 0.8.9) the whole window was dark and deep orange in game. The sky through a window being pinker than the real sky is open again.

## 0.8.9

- Grass is captured in colour only and takes the depth of the ground under it. With depth of its own every tuft became a small upright card, and a meadow seen from above or up close was a field of shards and streaks.
- The numpad-5 dump saves display values again (0.8.7 and 0.8.8 saved the half-float window texture's linear values, which look dark and deep orange in a viewer) and logs the window texture's alpha range.

## 0.8.8

- The time-of-day tint shifts hue much less (a quarter strength, each channel held within 0.7 to 1.5). A noon capture seen at dusk came out deep orange: one multiplier for the whole picture cannot tell sunlit from shaded, and the evening sun is far more orange than the scene it lights. Brightness still follows the light level. `ToneMatch = 0` shows captures exactly as taken.
- The numpad-5 dump logs the tint in use.

## 0.8.7

- The window is drawn into a half-float texture. The game's sky is brighter than 1 before tone mapping; in the 8-bit texture its blue channel was cut off, so the sky through a window looked pinker than the sky around it.

## 0.8.6

- The secondary viewpoints now also capture the face looking back. A portal with beams or a wall right behind it is seen, from far back on the other side, through the gaps in directions the centre viewpoint had blocked; only a viewpoint beside or above the centre knows what is there.
- The ground shell only holds what was far away in the capture (15 m and more). A beam a metre from the capture point, painted at infinity, showed as a brown blot in every gap.
- Skirts and the shell are textured with a blurred copy of the picture, so what nobody saw reads as haze in the right colours instead of a fan of streaks.

## 0.8.5

- Fixed: 0.8.4 drew nothing (a null material when its shader was missing).
- Root cause of "the background in front of the foreground", in every version since 0.4.0: the shader the reliefs were meant to use, `Particles/Standard Unlit`, is not in Valheim's build. `Shader.Find` returned null and the code quietly fell back to `Sprites/Default`, which blends and writes no depth, so every layer of every viewpoint was composited in draw order. Unity's stock cut-out shaders are not in the build either.
- The window material is now chosen at start-up from the shaders the game has actually loaded: every one with a main texture and a cut-off is tried (lit ones with the picture as emission over black albedo, which makes them unlit), on the camera's own rendering path and on the forward path, and must pass three tiny test renders: depth, cut-out, opaque above the cut-off. The log names the winner and lists the candidates.
- If nothing passes, the window falls back to the primary viewpoint alone, drawn background-then-foreground with the sprite shader and fully opaque texels: never blank, never worse than 0.5.

## 0.8.4

- The window's layers were being composited in draw order, not by depth: stair-shaped blocks of far background over near walls, distant trees through a house, the ground beyond a floor showing through its far end. Found by replaying a numpad-5 dump offline: with a depth test the stored capture draws correctly, without one it shows exactly the game's artefacts. The relief material (a stock particle shader in cut-out mode, the only kind of shader available without shipping one) is now verified at start-up: three tiny test renders check that it depth-tests, that texels below the cut-off neither show nor block, and that texels above it are opaque. If the usual set-up fails, the forward rendering path and two other stock shaders are tried, and the log says which one is in use.
- As a fallback the layers now have a fixed draw order (later viewpoints first, the primary viewpoint last; guesses, then captured background, then foreground).
- Scene fog is off while the reliefs are drawn. The unlit shader applied the viewer's fog on top of the fog already in the capture, in the viewer's biome colour: the pink cast on a forest seen from the plains.

## 0.8.3

- Particles (flames, smoke, sparks, mist, snow, falling leaves) are left out of captures. They have no depth, so they were painted onto whatever wall lay behind them, once per viewpoint, and smeared from any other angle. A torch still lights its wall.
- Fixed: every relief sat 0.15 m too close to the viewer (its origin is the capture point, which is 0.15 m in front of the far ring's centre, not the centre itself).
- Skirt walls no longer reach in front of the near surface they hang from.
- Numpad 5 now also dumps what every visible window drew (after the under pass, and the final picture) to `BepInEx/config/LivePortals/debug`, and logs the eye position in the form `tools/LayerTest` takes (`EYES=x,y,z`), so a view from the game can be redrawn offline and compared.

## 0.8.2

- Captures now carry the game's ambient occlusion. Valheim uses Amplify Occlusion on the main camera, with a tint and strength set per environment; it is what makes interiors and crevices dark. Without it a captured building interior came out flat and warm next to the real one. The capture's colour camera gets a copy of the effect (temporal filter off).
- Sky detection, third attempt: a pixel is sky if the black- and white-cleared renders differ and nothing solid lies within the depth range behind it (0.8.1 asked for an empty depth buffer and still found none). The depth buffer is now also cleared explicitly before each depth render. The capture log line shows both numbers.
- Secondary viewpoints are checked for clearance (open space, clear line to the centre), pulled closer if needed and skipped if there is no room. Built into a tight A-frame, the viewpoint above the centre could sit inside a beam and wreck the window from every angle.
- Foreground surfaces get their corner depths from a plane fitted through each cell's pixels, so the rim of a beam or wall seen at a slant no longer turns into teeth from another angle.
- `DepthScale` and its numpad keys are gone. The relief has been metric since 0.7.0; any value but 1 (a leftover 1.6 here) only misaligned and resized everything behind the window.
- The time-of-day tint takes its hue from the sun and ambient light instead of the fog colour, which belongs to the viewer's biome and turned a forest seen from the plains orange.
- `tools/LayerTest capture` draws all viewpoints of a stored capture together, as the game does.

## 0.8.1

- Fixed: the sky through a window was a flat, pale copy of the captured fog colour instead of the live sky. The sky test added in 0.7.1 found no sky at all, because the game hangs a thin haze dome over the whole sky; the mask is again the difference between a black-cleared and a white-cleared render, now combined with an empty depth buffer.
- Fixed: a bright sliver of sky at the bottom of a window when the ring's lower edge dips below the far side's ground. View rays that start under the terrain never meet the relief; they now land on a flat far copy of the capture's lower half (the ground in that direction).
- Cube faces are captured at 92 degrees so neighbours overlap, and skirt walls run a little past both surfaces they join: no more hairline cracks of sky between faces or along near ridges.
- Capture viewpoints are now the centre, then **above** it (a player's camera looks over near things from higher than the ring centre), then right and left; default 4 (`CaptureViewpoints`, replaces `CapturePoints`). The extra viewpoints skip the faces looking back and up, so four cost what three did.
- Grass within `GrassClearRadius` (3 m) of a portal is left out of its capture: blades right at the capture point turned into curtains of streaks from any other angle.
- `tools/LayerTest capture` now draws what a player would see: eye behind and above the ring, clipped at the ring plane, masked to the pane.

## 0.8.0

- Captures now carry the game's distance fog and ambient occlusion. In Valheim the fog is an image effect on the main camera (tinted toward the sun), not something the terrain and vegetation shaders do, so captures had none and far scenery looked crisp and bright. The colour pass now runs through a copy of the game's own post-processing profile with everything but fog and ambient occlusion switched off (`CaptureFog`); bloom, grading and the rest still reach the window when the game draws it.
- Grass and other clutter are captured. The game draws them only inside the main camera's view and builds them a patch per frame around the player; the capture now queues all of it near the portal for its own cameras and has the game build it at once on arrival.
- Skirts are now walls along every edge where neighbouring cells of the background sheet disagree in depth, so looking over or around a near ridge no longer opens sky-coloured slits.
- Hugin and Munin are hidden during capture.
- `tools/LayerTest capture`: draws a stored capture from shifted eye positions offline.

## 0.7.1

- Fixed: 0.7.0 read nearly everything bright as sky, so windows showed the live sky with a few dark scraps of terrain in front. The sky mask was taken from the depth render, whose colour output is black in this game. The mask is now a render of its own (cleared black, fog off); a pixel is sky if it stayed black and nothing was written to the depth buffer there.
- The capture log line reports how much of the forward face is sky and its median depth.

## 0.7.0

- Depth now comes from the GPU for every pixel of a capture instead of one physics ray per grid cell, so leaves, grass, distant terrain and everything else without a collider sit where they really are. The mod checks its depth read against physics rays on the first capture of a session and logs the result; if no read agrees it falls back to rays.
- Each captured face is split into two layers. The foreground holds near things (creatures, trunks, posts, and the rim of anything standing against something farther) and is cut out per pixel by its texture, so the background no longer lands on near objects and cut-outs follow the real silhouette. The background continues underneath with colour and depth filled in from behind.
- Looking around a big near object farther than any capture point saw now shows the far side's colours stretched across the gap (a skirt, drawn under everything else) instead of a hole or a copy of the near object. The flat far backdrop is gone.
- Filled-in background never hides what another capture point really saw of that surface.
- All capture points are rendered in the same frame, so creatures, grass and particles no longer differ between them; layering, PNG encoding and saving happen on a worker thread.
- Clouds and other see-through things without depth are painted on whatever is behind them; the sea surface takes its depth from the water level.
- `DepthGrid` is replaced by `MeshGrid` (default 128). Captures from older versions are ignored and replaced on the next trip.
- `tools/LayerTest`: an offline check that captures a synthetic scene, layers it and draws it from other eye positions next to the ray-traced truth.

## 0.6.0

- Multi-point capture: each portal is captured from several points across its ring (`CapturePoints`, default 3) and every point is drawn as its own relief, so what one viewpoint cannot see behind a near object is usually filled by another.
- Foliage, grass and anything else without a collider now take the depth of the nearest solid thing below them (a canopy sits on its trunk, grass on its ground) instead of falling to the far plane.
- Backdrops at full capture resolution; capture resolution default 768.
- Fixed: things behind the far portal could show through the front (the window camera now clips at the pane). The portal itself (frame, runes, sign, glow) is hidden while it is captured.

## 0.5.0

- Fixed: the window was mirrored left-right from the front of a portal (the sprite shader ignores texture scale, so the intended flip never happened). Parallax now moves the right way and text reads correctly. Found with the glass test.
- Depth from physics rays (0.3.2), relief cut at depth jumps with a background-only backdrop behind it (0.3.4, 0.4.0), depth-writing relief material (0.4.0).
- Pane defaults tuned in game: 2.7 x 2.8 m, ring centre 0.35 m below the model centre. Numpad tuning keys, numpad 0 manual capture, numpad . glass test.
- Back of a portal shows the back of its partner (a physically consistent hole); `ArrivalViewBothSides` switches to the partner front from both faces.

## 0.3.0

- Depth now comes from the engine's own depth-normals shader (the one the game camera uses), since the game's terrain and vegetation ignore stock fog and the fog trick read them as zero distance.
- Captures are taken from the ring centre again (0.2.0 took them from the ground), with the sky layers excluded so clouds are never baked in, and the portal's own light muted so the picture is not tinted orange.
- Pane centred on the ring (`RingCenterHeight` above the base, default 1.7 m) and sized 2.4 x 2.4 by default; the proximity point turned out to be the standing spot in front of the portal, not the ring.
- Window fade eases in, so it is half visible a third of the way in.

## 0.2.0

- Captures now include per-pixel depth (found with a black/white linear-fog pair, no shader needed) and each face is a displaced mesh, so near things through the window have real parallax.
- Fixed: the capture cube was drawn by the game camera (a huge building in the sky). The capture meshes now exist only for the instant the window camera renders.
- Fixed: the pane inherited the portal prefab scale and used a guessed centre. It is now free-standing and centred on the portal ring (proximity point); geometry is logged once per portal for tuning.
- Window starts to appear at 4x the activation range (was 2x).

## 0.1.0

- First release: capture on departure and arrival, parallax window on both faces of a portal, live sky behind the capture, time-of-day tone match, spill light, fade-in from twice the activation range.
