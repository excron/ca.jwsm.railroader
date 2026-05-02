# Experiment: Diesel Exhaust VFX

Custom-authored VFX Graph replacing vanilla's diesel exhaust at runtime.
Foundation for a layered exhaust simulation (soot / oil / lean / heat
distortion) driven by per-locomotive engine state.

The vanilla VFX uses *additive* blending — which mathematically cannot
produce dark output (adding any color to the sky always lightens, never
darkens). No `SmokeEffectProfile` curve tweak fixes this; the limitation
is at the output context level. This experiment swaps in an
**alpha-blended** asset and unlocks the dark, light-eating "clag" of
older diesel engines.

---

## Status

**Working in-game today:**

- ✅ Custom VFX asset replaces vanilla's at `OnEnable` via Harmony prefix
- ✅ Asset bundle pipeline: Unity authoring project → bundle → mod
  `Assets/` → game `Mods/` folder, all driven by one `dotnet build`
- ✅ Greyscale texture (Cloud03) stripped of vanilla's red-channel
  encoding so the smoke tints correctly under any color the policy passes
- ✅ Output context: URP Lit Quad with **Alpha blend mode** (the surgical
  change vs vanilla)
- ✅ `Rate` exposed property wired — vanilla controller's profile curves
  drive emission density (idle silent, throttle ramps up)
- ✅ `Color` exposed property wired — vanilla controller's gradient drives
  per-frame tint, no red bleed

**Foundation in place but not yet wired:**

- ⏳ Remaining contract properties (`Lifetime`, `Velocity`, `Size0`,
  `Size1`, `TurbulenceIntensity`, `PositionOffset`) — exposed in the
  Blackboard but the controller's `SmokeEffectWrapper` writes are
  currently no-ops
- ⏳ Update context — empty. No Turbulence (smoke goes straight up rather
  than churning), no Alpha-over-Life curve (particles stay fully opaque
  until they pop out of existence)
- ⏳ Set Tex Index in Initialize — all particles render flipbook frame 0;
  random index per particle would dramatically improve variety

**Designed but not implemented:**

- 🟡 Layered exhaust (soot + oil + lean as separate particle systems in
  the same graph)
- 🟡 `IExhaustPolicy` abstraction — per-locomotive engine-state model
  driving each layer's color and rate
- 🟡 Heat distortion via VFX Graph's distortion output (requires URP
  Opaque Texture enabled in the game's settings)

---

## Why this exists

Vanilla's exhaust is shipped as a `VisualEffectAsset` with an
**additive-blended** output context. Additive blending is `dst + src`,
which can only ever brighten the framebuffer — multiplying texels by a
dark color produces dim red, never black. Players who try to author
darker exhaust via `SmokeEffectProfile.colorGradient` hit a hard
mathematical wall: the runtime simply cannot render dark smoke under
additive blending no matter what gradient the controller supplies.

The vanilla VFX cannot be patched in place because the output context
type isn't exposed to runtime configuration — it's baked into the
compiled `VisualEffectAsset`. The only path is to ship a *replacement*
asset whose output uses **alpha blending** (`dst*(1-α) + src*α`), which
correctly attenuates the framebuffer toward dark colors when the source
is dark.

This mod ships that replacement asset and patches it into the live
locomotive prefabs at runtime.

---

## How it works

### The Harmony hook

`DieselExhaustParticleController.OnEnable` is the *one* place the
controller reads `visualEffect` to construct its `SmokeEffectWrapper`.
We Harmony-prefix that method and overwrite
`__instance.visualEffect.visualEffectAsset` to our loaded asset before
the wrapper grabs the reference. From frame zero, every property write
the controller makes (`SetFloat("Rate", x)`, etc.) targets our exposed
properties.

```csharp
[HarmonyPatch(typeof(DieselExhaustParticleController), "OnEnable")]
public static class DieselExhaustControllerPatch
{
    [HarmonyPrefix]
    public static void Prefix(DieselExhaustParticleController __instance)
    {
        var replacement = ExperimentEntry.ReplacementAsset;
        if (replacement == null) return;
        if (__instance.visualEffect == null) return;

        __instance.visualEffect.visualEffectAsset = replacement;
    }
}
```

### Architectural decision: graph stays neutral, policy lives in code

The `.vfx` is a **renderer abstraction** — it knows how to draw smoke
particles, nothing more. All gameplay-driven character (color, intensity,
layering, per-locomotive personality) lives in C# where it can adapt to
multiple inputs and ship without re-authoring assets. The asset is the
slow-moving artifact; policy is the fast-moving one.

For now the vanilla controller is the policy (we're letting it drive
`Rate` and `Color` via its profile gradient with an eventual postfix
darken). As we add the layered model, the policy becomes our own
`IExhaustPolicy` implementation, with vanilla's controller either
coexisting (driving soot only) or replaced entirely.

### Why a separate Unity authoring project

The `.vfx` source asset must be authored in a Unity project and compiled
to a `VisualEffectAsset` runtime form. This authoring project lives
outside the monorepo at
`C:\Users\jsm12\OneDrive\Documents\Game_Projects\unity\RR-VFX-Graph\`
(Unity 2022.3.62f2, URP, Universal 3D template). It has its own git
repo and is *not* a submodule — fully independent.

The authoring project's `Assets/Editor/BundleBuilder.cs` provides a
**Tools > Build Diesel Exhaust Bundle** menu item that:

1. Compiles the `.vfx` and its dependencies into an asset bundle
2. Copies the bundle directly into this mod's `Assets/` folder

After that, the mod's standard `dotnet build` deploys the bundle to
the game's `Mods/` folder (post-build target in the csproj).

---

## Iteration loop

```
┌──────────────────────────────────────┐
│  Edit DieselExhaust.vfx in Unity     │
└──────────────────┬───────────────────┘
                   │
                   ▼
┌──────────────────────────────────────┐
│  Tools > Build Diesel Exhaust Bundle │
│  (Unity menu)                        │
└──────────────────┬───────────────────┘
                   │  copies bundle into
                   ▼  this mod's Assets/
┌──────────────────────────────────────┐
│  dotnet build                        │
│  (compiles + post-build deploy)      │
└──────────────────┬───────────────────┘
                   │  copies into
                   ▼  game's Mods/<id>/
┌──────────────────────────────────────┐
│  Launch game, observe                │
└──────────────────────────────────────┘
```

The bundle does **not** auto-rebuild when the `.vfx` changes. The Unity
menu item must be invoked explicitly. Watch for stale bundle timestamps
in the deploy chain when troubleshooting.

---

## Planned: layered exhaust simulation

Real diesel exhaust is several mixed signals, each with distinct
character:

| Layer | Trigger | Visual character |
|---|---|---|
| **Soot/clag** | rich combustion, high load | dense, dark, slow rising |
| **Oil** | worn engine, oil seal leakage | thin blue-grey, persistent |
| **Lean/white** | cold start, throttle-down transient | wispy white, fast disperse |
| **Heat distortion** | hot exhaust at any throttle | screen-space wavy air |

Each smoke layer becomes a separate particle system in the same `.vfx`
graph (multi-output VFX), each pulling from its own Blackboard
properties. Single greyscale texture (`Cloud03_8x8_alpha`) serves all
three. Heat distortion uses VFX Graph's procedural noise — no additional
texture needed.

### Property contract per layer

| Layer | Naming | Driven by |
|---|---|---|
| Soot | `Rate`, `Color`, `Lifetime`, `Velocity`, `Size0`, `Size1`, `TurbulenceIntensity` (vanilla-compatible) | Vanilla controller (with optional darken postfix) |
| Oil | `OilRate`, `OilColor`, `OilLifetime`, `OilVelocity`, `OilSize0`, `OilSize1`, `OilTurbulence` | `IExhaustPolicy` |
| Lean | `LeanRate`, `LeanColor`, `LeanLifetime`, `LeanVelocity`, `LeanSize0`, `LeanSize1`, `LeanTurbulence` | `IExhaustPolicy` |

Shared across all layers: `PositionOffset` (Vector3, same stack origin).

### Policy interface

```csharp
public interface IExhaustPolicy
{
    void Evaluate(in LocomotiveState state, ref ExhaustOutputs output);
}

public struct LocomotiveState
{
    public float Load;              // _value, 0-1 (smoothed throttle)
    public float Transient;         // _accel, signed (rate of change)
    public float EngineCondition;   // 0=clean, 1=worn, persistent per-loco
    public float WarmupProgress;    // 0=cold, 1=warm
    public bool  Running;           // !IsIdle && HasFuel
}
```

Different implementations = different engine personalities (e.g.
`ClassicWorkhorseExhaust`, `OilBurnerExhaust`, `ColdMorningExhaust`).

The transient signal (`_accel`) is particularly interesting — positive
transients (notch-up) drive a rich-combustion clag pulse, negative
transients (notch-down) drive a lean white puff. Real diesel behavior,
already exposed by vanilla's controller, just not rendered visibly under
additive blending.

---

## File map

```
diesel-exhaust-vfx/
├── README.md                     # this file
├── info.json                     # UMM manifest
├── ca.jwsm.railroader.experiments.diesel-exhaust-vfx.csproj
├── Assets/
│   └── dieselexhaust.bundle      # compiled VFX bundle (built externally)
└── src/
    ├── ExperimentEntry.cs        # UMM bootstrap, bundle load, Harmony.PatchAll
    └── DieselExhaustControllerPatch.cs  # the OnEnable prefix
```

Authoring project (separate git repo, not in monorepo):

```
RR-VFX-Graph/
├── Assets/
│   ├── DieselExhaust.vfx         # the authoring graph
│   ├── Cloud03_8x8_alpha.png     # greyscale density mask (vanilla's texture, channel-duplicated)
│   ├── Cloud03_8x8_alpha.original.png  # vanilla's original red-encoded texture (kept for reference)
│   └── Editor/
│       └── BundleBuilder.cs      # Tools > Build Diesel Exhaust Bundle
└── ProjectSettings/
    └── ...                       # Unity 2022.3.62f2, URP
```

---

## Falling back

If the bundle is missing or the asset can't be loaded, `ReplacementAsset`
stays null and the Harmony prefix becomes a no-op. Vanilla exhaust
continues to play unchanged. Removing this mod from `Mods/` folder is
zero-impact — no permanent state changes.
