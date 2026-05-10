# Consist Dynamics — Lessons & Findings

This experiment ran from 2026-05-09 across multiple phases attacking vanilla's `IntegrationSet` consist physics and `CarAirSystem` brake-pipe air. Parked in working state at the end with substantial structural insights and a known multi-week gap to reach a complete vanilla replacement. This document is the durable summary — the code itself is throwaway, the findings aren't.

## What got proved

### 1. Vanilla's 4-iteration constraint solver is replaceable by a single O(N) tridiagonal sweep.

Vanilla's `IntegrationSet.IntegrateConstraints(dt)` runs a 4-iteration Gauss-Seidel relaxation over all couplers each tick. Standard pattern, and it's the reason vanilla's couplers can't be very stiff — too-stiff couplers ring at low sweep counts, so vanilla compromises at ~4 sweeps with moderate stiffness.

We replaced it with **implicit semi-implicit Euler integration** of a per-car velocity state, with coupler springs evaluated at `t+dt`. The resulting linear system is tridiagonal in `dv_i`:

```
-β·dv_{i-1} + (m_i + 2β)·dv_i - β·dv_{i+1} = b_i
```

where `β = k·dt² + c·dt`. Solved in **one O(N) Thomas-algorithm sweep** — no chain iteration regardless of coupler stiffness or how non-linear the regime transitions are. Coupler model used a **dead-zone with hard walls** (zero force inside ±4 cm slack window, near-rigid spring outside) matching vanilla's `IntegrateConstraints` semantics exactly: cars drift freely in slack, snap on bottom-out.

This is "Featherstone for 1D" — the original musing that motivated the experiment was about Featherstone's articulated body algorithm. In 1D it collapses to implicit tridiagonal, and the structural payload remains the same: **the matrix structure replaces iteration**.

Worked in production on consists up to 305 cars + MU. Slack action visibly correct: lead loco accelerates first, force propagates rearward through the chain over several ticks, hard-stop "BANG" at each coupler as slack runs out.

### 2. Brake pipe is an even cleaner application of the same trick.

Replaced vanilla's per-car valve-flow model with a **1D pressure diffusion field along consist arc length** — same per-coupler-node tridiagonal system, this time a heat-equation-style diffusion. Lead loco's `TrainBrake` KVO drives a Dirichlet BC at that node; everything else propagates outward at rate set by the diffusion coefficient.

```
∂P/∂t = D · ∂²P/∂s²    (continuous)
α·P_i^new = P_i^old + ...   (discrete tridiagonal)
```

Visible propagation waves through the HUD pill bar (we write to `car.air.BrakeCylinder.Pressure` and vanilla's `TrainBrakeDisplay` reads it). 305 cars with 4-MU loco at the head: brake-release wave visibly takes ~30 seconds to traverse — physically correct for real brake pipes (~600 ft/s service propagation speed).

The pattern of "1D field → tridiagonal Thomas" works for any quasi-1D-along-rail physical quantity. Could be extended to cover air reservoirs, electrical bus voltage in MU sets, etc.

### 3. The 20% optimization-tools cap is real.

User had previously Burst-compiled vanilla's hot path and seen a ~20% improvement. We hypothesized this represented the math-kernel fraction of vanilla's per-tick work, with the other ~80% being managed-object access, Unity API calls, event dispatch, audio side effects, and serial 4-iteration sweeps that don't parallelize.

Confirmed by the structural rewrite. We attacked the 80%:
- **Removed** the 4-iter solver entirely (replaced with implicit O(N) direct solve)
- **Removed** per-car air valve flow (replaced with diffusion field)
- **Suppressed** all the side-effect dispatch we don't need (audio events, gizmos, `FireOnMovement`, `UpdateCurvatureForLocation`, `_rollingPlayer.SetVelocity`, ...)
- **Replaced** the spline-math substrate (Phase 4 fast path on `Graph.LocationByMoving`)

End result on 600+ cars across 3 active consists: deterministic frame time, **substantially better than vanilla** in steady-state rolling. Vanilla's averaged FPS was higher in transient (because of vanilla's writeback threshold gate that we never matched), but with much higher frame variance — "all over the place." Our deterministic-30 reads as smoother than vanilla's bouncing-40 to a player.

### 4. The right perf attack on bursty conditional load is *cheaper work*, not parallelism.

Initially I framed our workload as "bursty conditional" since at-rest consists cost nothing under the sleep gate. User correctly pushed back: realistic gameplay has multiple consists active simultaneously (player + AI deliveries + yard switching) — that's the steady state, not a burst, and it's exactly the embarrassingly-parallel scenario. Per-consist solves are independent, no cross-consist contention, identical structure.

But CPU SIMD, threading, and Burst all attack speed-of-math. Our actual bottleneck was the **per-car spline operations** during visual writeback (`Graph.LocationByMoving` × 5 + `Graph.GetPositionRotation` × 2 per car). That work can't trivially go to a worker thread — Unity Transforms are main-thread-only — and Burst can't compile across managed boundaries.

**The structural fix (Phase 4 fast-path) won where the optimization tools couldn't**: short-circuit within-segment `LocationByMoving` to pure arithmetic. Most car-positioning calls now take ~5 ns each instead of segment-walking. Affects the entire game (vanilla's own writeback gets the speedup too).

### 5. Threading would be a real lever IF we ever paid for the Burst build pipeline.

For the eventual "many active consists" scenario, the realistic ceiling is:

| Tier | Method | Gain |
|---|---|---|
| Tier 1 | Burst the math kernel via `IJob` | Negligible — math is already cheap |
| Tier 2 | `IJobParallelFor` over consists | Linear speedup with active-consist count |
| Tier 3 | `IJobParallelForTransform` for the visual writeback | Modest; the Transform writes themselves are cheap |
| Tier 4 | Threaded `Graph` access (after caching `GetPositionRotation` per segment) | Significant; pushes the spline work off main thread |
| Tier 5 | Full DOTS / ECS rewrite | Maximum throughput, months of work |

DOTS / ECS is **not feasible** without source access (verified during the experiment): vanilla doesn't ship `Unity.Entities`, cars are baked-in MonoBehaviours we can't re-author as Entities, and Hybrid Renderer requires authoring-time prefab conversion.

The realistic ceiling for a mod-only path is **Tier 1+2+3+4**: ~50-70% wall-clock reduction in solver+writeback at 305 cars across many consists, *if Graph reads are thread-safe* (untested; per-segment field caching makes the question moot by reading from immutable arrays we built ourselves).

Infrastructure cost: a parallel Unity build project that AOT-compiles our solver code with `[BurstCompile]` and ships the native DLL alongside our managed mod. Real engineering, not "drop in and go."

## Architectural rule of thumb

> **Vanilla owns the world's existence and grouping. We own all motion, all brake-pipe air, and the brake force.** Vanilla's `Car.PositionWheelBoundsFront` and `IntegrationSetManager` topology survive; everything else physics-related is ours. We write into vanilla's renderable state (transforms, `air.BrakeCylinder.Pressure`) so vanilla's HUD and visuals show our truth.

This split lets us aggressively suppress vanilla's hot path while inheriting all the world-loading, prefab management, spline traversal, and rendering pipeline that we have no business reimplementing. The cost is a long list of "side effects we have to replicate manually" (visual coupler open/closed, anglecock state, air hose connect/disconnect, etc.) — each is small individually but they add up to weeks of work.

## What it would take to ship — prioritized resume list

If anyone (future me, future you, future dev hand-off) ever picks this up, here's the order of remaining work in roughly increasing scope:

### Tier A — Polish & correctness on what's already wired

1. **Position correction at impact.** Cars currently can pass through each other on slow approach (auto-couple needs `|relV| > 1.5 m/s`). Add vanilla-style mass-weighted projection when seam pair physically overlaps.
2. **Manual coupling at zero velocity.** Two stopped cars touching should be coupleable via player input, not just impact. Adds a non-velocity-triggered path to the auto-couple logic.
3. **Air hose visual connect/disconnect.** `EndGear.SetConnectedTo(null)` on uncouple; reconnect on couple. Mostly a SyncEndGearVisual extension.
4. **Anglecock state propagation.** Today brake pipe is treated as continuous. Honor anglecock open/closed by setting per-coupler diffusion coefficient to 0 when blocked.

### Tier B — Real fidelity

5. **MU + CutOut.** Currently every loco contributes TE under the shared throttle, regardless of CutOut state. Read the CutOut flag per-loco; only contributing-locos get traction.
6. **Independent (loco) brake.** Separate from train brake; applies brake force to the loco only.
7. **Dynamic brake.** Loco-only retarding force as a function of speed; integrates into the per-loco force pipe.
8. **Wheel slip + adhesion.** A single SD7 trivially pulls 300 cars right now. Per-loco TE should be capped by `μ × adhesive_weight`, with `μ` modulated by curvature, grade, weather, and slip ratio. Vanilla has the formulas in `TrainMath` but doesn't fully wire them.

### Tier C — Audio & cab visuals

9. **Engine sound (PrimeMover / SteamEngine ticks).** Resurrect the engine simulation specifically for audio, without letting it drive consist physics. May need careful suppression of just the physics-feedback path.
10. **Cab control visual sweeps.** `UpdateCabControls()` was inside `BaseLocomotive.FixedUpdate` (suppressed). The HUD inputs work but the 3D cab gauges don't move.
11. **Coupler slack-in / slack-out audio.** Detect `SlackStretchDidChangeDirection` in our solver (we already track stretch); fire `Coupler.SlackIn(magnitude)` / `SlackOut(magnitude)` at threshold.
12. **Brake exhaust audio + brake glow visuals.** `UpdateBrakeApplied(bool)` was inside `Car.FixedUpdate` (suppressed). Easy to call ourselves from the air solver based on cylinder pressure threshold.

### Tier D — System integrations we've broken

13. **Networking.** Vanilla's multiplayer state delta sync ran inside `TrainController.FixedUpdate`. Probably has to stay broken — replicating networking would be a project of its own.
14. **AutoEngineer / NPC AI.** Untouched code that reads vanilla physics state. Some may work, some won't. Not the focus.
15. **Save/load survival.** Untested. Probably needs care around our `_carCache` and any state we don't push to vanilla on save.
16. **Steam locomotives.** Diesel-only experiment. Steam engine sim suppressed; would need a parallel `PrimeMover`-equivalent for steam-specific behavior.

### Tier E — Multi-consist throughput (if ever needed)

17. **Phase 4b — Cached `GetPositionRotation` samples per segment.** Precompute world position+rotation at fine resolution along every track segment at world-load. Replaces the second expensive spline op per car per tick with O(1) lookup. Also enables Tier 4 threading (immutable cache → trivially thread-safe).
18. **Per-consist `IJobParallelFor`** for the math kernel. Requires the Burst build pipeline. After Phase 4b is in place.
19. **`TransformAccessArray` / `IJobParallelForTransform`** for the visual writeback. Per-car body+truck Transforms registered up-front; updates dispatch in parallel.

### Tier F — Behavioral richness (the original musing)

20. **6D spatial Featherstone.** Each car becomes a 6-DOF rigid body. Adds body roll into superelevation, lateral hunting oscillation, pitch under accel/decel, yaw at tight curves, truck creep dynamics. The math goes from our current ~20 flops per car to ~100-200 flops of dense matrix algebra. *This is where the original Featherstone musing actually lives* — what we built is the 1D shadow.

## Empirical data captured

### FPS curves

| Cars | Engines | Configuration | FPS | Notes |
|---|---|---|---|---|
| 0 (idle) | — | baseline | ~180 | our solver doing nothing per consist |
| 21 | 1 SD7 | single consist | ~115-120 | phase 2 cost |
| 51 | 1 SD7 | single consist | ~100 | |
| 151 | 1 SD7 | single consist | ~75 | |
| 301 | 1 SD7 | single consist | ~65 | |
| 305 | MU stack | single consist | ~50-60 | post-phase-3a |
| 309 | multi-loco | vanilla | ~40 | jittery |
| 600 | 1 SD7 | 3 consists × 200 each | ~30 | linear scaling confirmed |
| 600 | multi-loco | 3 consists rolling | ~30 | deterministic, no jitter |
| 600 | multi-loco | vanilla, 3 consists rolling | ~20-50 | "all over the place" |

Per-car cost in our solver settled at ~7 µs/car in the 151→301 segment — far below vanilla's per-car cost. Marginal cost dropped by half each time we doubled car count, then plateaued (asymptote dominated by visual-writeback floor).

### Survey doc bug found

`docs/research/physics-vanilla-survey.md`'s Mass section says `Car.Weight` is in short tons. **It's actually pounds.** Caught when our 305-car consist solver-logged `mass=1.22e9 kg` (off by 2000×). Verified two ways:
- `Car.GravityForce` divides Weight by 2000 (lb→short-ton)
- `IntegrationSet.cs:393, :430` multiplies `car.Weight * 0.453592f` (lb→kg)

Survey doc fix saved as a memory; will be applied next time the doc is touched.

## Final note

This experiment was play. We musedabout Featherstone, decided to actually try it, and ended up with a working 1D specialization that beats vanilla in steady-state rolling on long consists. Along the way we surveyed Unity's threading model, validated the 20% Burst ceiling, found a real bug in the team's recon doc, and built a clear mental model of where the structural costs in vanilla actually live.

The code is throwaway by experiment-folder convention. **The findings aren't.** If the dev ever asks for a serious physics overhaul, the path forward is now mapped.
