# Changelog

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
