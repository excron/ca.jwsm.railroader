# Experiment: Diesel Exhaust VFX

Replaces the prefab-baked `VisualEffectAsset` on every
`DieselExhaustParticleController` with a custom-authored VFX Graph that
produces dark, light-eating "clag" — the look the vanilla VFX cannot
mathematically produce because its output context uses additive blending.

The vanilla controller and `SmokeEffectProfile` continue to drive the
exposed properties (`Color`, `Rate`, `Lifetime`, `Velocity`,
`Size0`, `Size1`, `TurbulenceIntensity`, `PositionOffset`) — we only
swap the visual asset, not the behavior.

## Authoring loop

1. Edit `DieselExhaust.vfx` in the Unity authoring project at
   `C:\Users\jsm12\OneDrive\Documents\Game_Projects\unity\RR-VFX-Graph\`.
2. In the Unity editor, run **Tools > Build Diesel Exhaust Bundle**.
   This compiles the bundle and copies it directly into this mod's
   `Assets/dieselexhaust.bundle`.
3. Rebuild this mod (`dotnet build`) — the post-build step copies the
   bundle into the deployed mod folder.
4. Launch the game; the asset is loaded at UMM startup and the Harmony
   prefix on `DieselExhaustParticleController.OnEnable` swaps the asset
   on every diesel exhaust as it spawns.

## Why a Harmony prefix on OnEnable

The controller reads `visualEffect` exactly once in `OnEnable` to
construct its `SmokeEffectWrapper`. Replacing the asset in a prefix —
before the wrapper is constructed — means the wrapper sees our asset
from frame zero, and all subsequent property writes from the controller
target our exposed properties.

## Falling back

If the bundle is missing or the asset can't be loaded, `ReplacementAsset`
stays null and the patch becomes a no-op. Vanilla exhaust continues to
play unchanged.
