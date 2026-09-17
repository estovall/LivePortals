# Changelog

## 0.2.0

- Captures now include per-pixel depth (found with a black/white linear-fog pair, no shader needed) and each face is a displaced mesh, so near things through the window have real parallax.
- Fixed: the capture cube was drawn by the game camera (a huge building in the sky). The capture meshes now exist only for the instant the window camera renders.
- Fixed: the pane inherited the portal prefab scale and used a guessed centre. It is now free-standing and centred on the portal ring (proximity point); geometry is logged once per portal for tuning.
- Window starts to appear at 4x the activation range (was 2x).

## 0.1.0

- First release: capture on departure and arrival, parallax window on both faces of a portal, live sky behind the capture, time-of-day tone match, spill light, fade-in from twice the activation range.
