# Consist Dynamics — Test Build

> **Heads up:** this is an **experimental, narrowly-scoped physics replacement**, not a polished mod. It deliberately suppresses chunks of vanilla so we can author consist motion + brake-pipe air ourselves and measure what *we* cost. Many things you'd expect to work are intentionally dead.
>
> If you're poking at this without context, read the "What's wired / not wired" sections below before judging anything.

## What this is

A clean-room replacement of two vanilla physics systems on a per-consist basis:

1. **Consist motion** — replaces vanilla's `IntegrationSet` 4-iteration constraint solver with a per-car arc-length state, compliant 1D couplers, and an implicit tridiagonal direct solve (Thomas algorithm) — single O(N) sweep, no chain iteration regardless of coupler stiffness.
2. **Brake-pipe air** — replaces vanilla's per-car valve-flow model with a 1D pressure diffusion field along the consist, also solved implicitly via tridiagonal Thomas. Visible propagation waves via diffusion coefficient tuning.

Vanilla physics is **wholesale suppressed** for these systems via three Harmony prefix-false patches (`TrainController.FixedUpdate`, `Car.FixedUpdate`, `BaseLocomotive.FixedUpdate`). Anything those methods used to do — including a lot of visual-side bookkeeping — is gone unless we explicitly replicate it.

## What's wired (it should work)

- **Vanilla spawner UI** — spawn cars however you normally do, we adopt them on the next tick
- **Vanilla HUD reverser, throttle, train brake** — KVO subscriptions; we react to whatever the HUD writes
- **Vanilla HUD pill bar (brake state)** — we write to `car.air.BrakeCylinder.Pressure`, the HUD reads it, you see our brake state colored on the pills
- **Multi-loco MU traction** — every `BaseLocomotive` in the consist contributes its own `RatedTractiveEffort` under the shared HUD throttle/reverser
- **Coupler aiming visuals** — vanilla's `PositionCoupler()` is called automatically by `Car.PositionWheelBoundsFront()` so couplers rotate to face each other; gaps still visible at full slack but knuckles stay aimed
- **Slack action with hard stops** — couplers do nothing inside the ±4 cm slack window, then engage near-rigidly
- **Grade resistance** — read from `Graph.GradeAtLocation` per car
- **Body + truck rendering** — vanilla's `PositionWheelBoundsFront` does the work; we just supply the location
- **Brake-pipe pressure waves** — 1D diffusion field along consist arc length, implicit tridiagonal solve, visible propagation through the HUD pill bar over ~1-2 seconds across long consists
- **Coupling / uncoupling round-trip** — `UpdateSets()` invoked each tick translates `IsCoupled` flag changes into `IntegrationSet.Split` / `Union`; per-Car state cache preserves velocity + brake state across topology changes; visual coupler open/closed state synced on each Refresh
- **Auto-couple on impact** — when uncoupled cars approach with `|relV| > 1.5 m/s` within ~rest-spacing range, both `IsCoupled` flags flip and velocities equalize (impulse-style momentum conservation); approximates vanilla's `IntegrateConstraints` auto-couple
- **Sleep gating** — both chain and air solvers skip ticks when their respective state is at equilibrium. Idle consists scattered around the map cost essentially nothing per tick.
- **Spline fast-path (Phase 4)** — `Graph.LocationByMoving` patched with O(1) within-segment fast path. Affects every caller in the game; majority of car-positioning calls now arithmetic instead of segment walking.

## What's NOT wired (caveats / gotchas)

### Critical to know

- **MU+CutOut:** completely ignored. We don't read the CutOut flag — every loco contributes traction whether the player has CutOut'd it or not.
- **MU directionality:** all cars in a consist are assumed to face the same direction as the lead loco. Reverse-facing DPUs would fight the consist.
- **Independent (loco) brake:** not modeled. Only the train brake.
- **Dynamic brake:** not modeled.
- **Wheel slip / adhesion:** not modeled. A single SD7 trivially pulls 300 cars without slipping — that's why the long-consist tests required head-end MU.
- **Anglecocks:** not honored. Brake pipe is assumed continuous through the whole consist regardless of anglecock state.
- **Brake pipe rupture / emergency application:** not modeled. Service apps only.
- **Manual coupling at zero velocity:** auto-couple needs `|relV| > 1.5 m/s` to fire. Two stopped cars touching won't couple themselves; the player would need to nudge one. Vanilla has the same kind of threshold but lower (~4 mm/s).
- **Position correction at impact:** vanilla's constraint solver mass-weight-projects overlapping cars apart on contact. We don't. Fast-enough impacts (above auto-couple threshold) work because velocities equalize at contact, but slow approaches can leave a small visible gap at the seam after coupling.
- **Air hose visual connect/disconnect:** the visible drape stays drawn regardless of actual coupling state. Vanilla's `EndGear.SetConnectedTo` not driven.

### Audio / visuals that died with the suppression

- **Engine sound (PrimeMover / SteamEngine):** dead. Their tick is suppressed.
- **Brake exhaust audio:** dead.
- **Coupler slack-in / slack-out audio:** dead.
- **Anglecock visual animations:** dead (`UpdateAnglecockControl` was inside `Car.FixedUpdate`).
- **Brake-applied visual flag** (brake glow, etc.): dead.
- **Cab control visual sweeps:** dead (`UpdateCabControls` in `BaseLocomotive.FixedUpdate`). HUD inputs still work — you just don't see virtual gauges in the 3D cab moving.

### Other untouched systems that may behave oddly

- **Multiplayer:** dead. Vanilla networking is in the suppressed `TrainController.FixedUpdate`. Single-player only.
- **Signaling, dispatch, AutoEngineer, AI:** untouched code, but it reads vanilla physics state (positions, velocities) that we now author. Some of it should work, some won't. Not the focus.
- **Steam locomotives:** can spawn but engine sim is dead. Diesel-only test.
- **Derailment, condition wear, brake heating:** unmodeled.
- **Brake reservoir** (separate from brake cylinder): not maintained. Vanilla's AB valve uses BR; we skip it and drive cylinder directly off pipe-pressure drop.
- **`car.air.brakePercent`:** never updated. Anything reading it sees stale 0.

## How to test

1. Make sure the mod is in `<Railroader>/Mods/ca.jwsm.railroader.experiments.consist-dynamics/` (use `deploy.ps1` from this folder)
2. Launch Railroader. Confirm `[ca.jwsm.railroader.experiments.consist-dynamics] Active` in `Railroader_Data/Managed/UnityModManager/Log.txt`
3. Spawn a consist with the vanilla spawner, on flat track preferably for first runs
4. For long consists, MU multiple locos at the head — single locos won't move 100+ cars now that physics is honest
5. Use the vanilla HUD to drive — reverser, throttle, train brake
6. Watch the pill bar for brake-wave propagation when applying / releasing

### Things to look for

- **At rest, brake released:** all pills green, no motion
- **Apply train brake:** pill at lead reddens immediately, wave propagates rearward over ~1–2 sec, lead car decelerates first
- **Throttle from rest:** lead loco accelerates first, slack runs out coupler-by-coupler rearward (visible "BANG" as each hits its hard stop), trailing cars kick in seconds later on long consists
- **Steady cruise:** all cars at the same speed, all couplers stable at slack-out (positive stretch ~ slack limit)
- **Long consist (300+ cars):** clearly visible propagation lag in both motion and brakes — that's the headline behavior

### Logs (`UnityModManager/Log.txt`)

- `[adopt]` — once per consist when first seen: car count, loco count, total mass, sum TE
- `[input]` — KVO change: throttle / reverser / trainBrake values
- `[solver]` — once per second per active consist: lead/rear velocity, max coupler stretch, count of bottomed-out couplers

## Phase status

| Phase | Status | What it does |
|---|---|---|
| 1: rigid plumbing | done | Single-DOF solver, validated suppression + writeback |
| 2: per-car + compliant couplers | done | Tridiagonal implicit solve, hard-stop dead zone, MU traction |
| 3a: 1D brake-pipe field | done | Implicit diffusion solve, per-car cylinder dynamics, HUD pill writeback |
| Sleep gating | done | Skip both solvers when state is fully quiescent |
| Coupling/decoupling round-trip | done | Per-Car state cache, drift detection, vanilla `UpdateSets` invocation, visual coupler sync, auto-couple on impact |
| 4: spline fast-path | done | `Graph.LocationByMoving` O(1) within-segment fast path |
| 3b: anglecocks, MU+CutOut, indep brake, dyn brake | not started | |
| Wheel slip / adhesion / position correction at impact | not started | |
| Air hose visuals + manual coupling | not started | |
| Audio / cab control visuals / map physics | not started | |

**Status: parked in working state (2026-05-09).** A complete vanilla replacement would be weeks of additional work. See [`LESSONS.md`](LESSONS.md) for the durable findings, structural arguments, and prioritized resume list.

## FPS reference points (single SD7 unless noted; flat track)

| Cars | Engines | FPS | Notes |
|---|---|---|---|
| 0 (idle) | — | ~180 | Baseline; our solver doing nothing per consist |
| 21 | 1 SD7 | ~115–120 | Phase 2 cost vs phase 1 (~120) is ~+0.4 ms/frame |
| 51 | 1 SD7 | ~100 | |
| 151 | 1 SD7 | ~75 | |
| 305 | MU stack | ~50–60 | Above vanilla's ~40 at similar scale, while doing strictly more work |

Phase 3a air added negligible cost over phase 2 in these tests.

## Architecture (one-liner)

> Vanilla owns the world's existence and grouping. We own all motion, all brake-pipe air, and the brake force. Vanilla's `Car.PositionWheelBoundsFront` and `IntegrationSetManager` topology survive; everything else physics-related is ours. We write into vanilla's renderable state (transforms, `air.BrakeCylinder.Pressure`) so vanilla's HUD and visuals show our truth.

See [`docs/research/physics-vanilla-survey.md`](../../docs/research/physics-vanilla-survey.md) (in the parent repo) for the recon that this design is built on.

## File layout

```
consist-dynamics/
├── README.md                              ← this
├── deploy.ps1                             ← build + copy to Mods/
├── info.json                              ← UMM manifest
├── ca.jwsm.railroader.experiments.consist-dynamics.csproj
└── src/
    ├── ExperimentEntry.cs                 ← UMM Load, installs Harmony patches
    ├── Patches/
    │   └── SuppressVanillaPhysics.cs      ← three prefix-false patches
    ├── Driver/
    │   └── ConsistDriver.cs               ← postfix on TC.FixedUpdate, registry sync, dispatch
    ├── State/
    │   └── ManagedConsist.cs              ← per-consist state (parallel arrays)
    ├── Solver/
    │   ├── ChainSolverConfig.cs           ← all tunable knobs
    │   ├── ImplicitChainSolver.cs         ← motion: tridiagonal Thomas
    │   └── AirPipeSolver.cs               ← brake pipe: tridiagonal Thomas
    └── Input/
        └── ControlObserver.cs             ← KVO subscriptions for HUD inputs
```

## Tunable knobs (in `ChainSolverConfig.cs`)

If something feels wrong, these are the dials:

- `CouplerSlackLimitMeters` (default 0.04 = 4 cm) — vanilla matches at ~4 cm total slack
- `CouplerStiffnessHard` / `CouplerDampingHard` — wall stiffness past slack limit
- `PipeDiffusionM2PerSec` — bigger = faster brake-pipe propagation
- `CylinderTimeConstantSec` — bigger = more sluggish per-car brake response
- `BrakeForceMaxDecelMps2` — peak deceleration at full cylinder pressure
- `DragLinearPerKg` — rolling resistance fudge factor

Restart the mod after recompile. Hot-reload of these is not wired.
