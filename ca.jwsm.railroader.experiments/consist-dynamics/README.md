# Experiment: Consist Dynamics

## Question

Can we wholesale **replace** vanilla's consist physics with our own solver — completely killing all vanilla physics CPU — and watch how FPS curves as consists scale from 1 → many cars?

The musing that led here: vanilla's `IntegrationSet` runs a 4-iteration Gauss-Seidel constraint solver to converge each tick. Featherstone-class direct multibody solvers have **no chain iteration** — they replace the relaxation loop with structured recursion. Whether that translates to a visible FPS win at parity car count is the empirical question this experiment answers.

This experiment exists to play with the idea, not to ship a replacement. Findings may be worth taking to the dev; the code itself is throwaway.

## Scope and non-goals

**In scope (phase 1):**
- Suppress vanilla physics tick wholesale. No background CPU bleed.
- Drive consists from our own solver.
- Read control inputs (throttle / reverser / train brake) from vanilla HUD via KVO.
- Use vanilla spawner UI for consist creation. Adopt whatever cars vanilla puts on rails.
- Diesel only — SD7 / GP9 values from `Car.Weight` and `BaseLocomotive.RatedTractiveEffort`.

**Out of scope:**
- Multiplayer (vanilla networking is dead).
- Signaling, dispatch, AI, derailment, schedules, audio.
- Steam, electric.
- Brake pipe dynamics (phase 1 uses trivial brake force; phase 4 = gradient-field replacement).
- Coupler compliance (phase 1 = single rigid body; phase 2 = compliant; phase 3 = Featherstone).

## Vanilla suppression strategy

**Three Harmony prefix patches return `false` (skip method body):**

| Patch target | What it kills |
|---|---|
| `TrainController.FixedUpdate` | Master physics tick: air, IntegrationSet, topology reconcile, networking, spatial hash |
| `Car.FixedUpdate` | Per-car: anglecock animation, brake-applied visuals, end-gear sync, mover ticks (PrimeMover/SteamEngine) |
| `BaseLocomotive.FixedUpdate` | Per-loco: tractive-effort/wheel-slip computation, cab-control updates |

After patching, the only Unity FixedUpdate work that should consume CPU on consist objects is ours. Verified by profiling.

The `IntegrationSet` and `IntegrationSetManager` instances stay alive (vanilla code holds refs), but they sit inert. We use `IntegrationSet` purely as a topology source — "which cars are coupled together" — and never read from `Element` or write into it.

## Phase plan

1. **Plumbing** — three patches + driver + dummy single-DOF solver. SD7 + 5 boxcars on flat track moves under throttle from vanilla HUD. Confirms the cut.
2. **Per-car arc-length state with compliant 1D couplers** — RR-equivalent baseline (Gauss-Seidel relaxation). FPS sweep at 10/50/100/200 cars.
3. **Featherstone direct solver** — same scenarios. FPS sweep. Direct comparison vs phase 2.
4. **Gradient-field track + air replacement** — replace per-car `Graph.GradeAtLocation` calls with batched field eval. Replace brake pipe with a gradient/diffusion model.

Each phase is a checkpoint with a question: *did anything change in a way that matters?*

## Status

**Phase 1: COMPLETE** (2026-05-09).

### What's validated

- **Suppression is honest.** Three Harmony prefix-false patches (`TrainController.FixedUpdate`, `Car.FixedUpdate`, `BaseLocomotive.FixedUpdate`) cleanly kill all vanilla physics-side per-tick CPU. No background bleed observable in profiling.
- **Adoption flow works.** Driver postfix on `TrainController.FixedUpdate` walks `IntegrationSetManager` each tick, registers new consists into `ManagedConsist`, attaches KVO observers. Consists with no loco are skipped automatically.
- **Vanilla HUD inputs reach us.** KVO subscriptions on lead loco's `KeyValueObject` for `Throttle`, `Reverser`, `TrainBrake` fire on every HUD interaction. Confirmed via per-callback log lines.
- **Vanilla writeback path works.** `Car.PositionWheelBoundsFront(newLoc, Graph.Shared, MovementInfo.Zero, update: true)` correctly drives the visible Transform + truck rotation + body positioning. We do not need to touch `Element` or duplicate vanilla's per-car visual logic.
- **Solver drives motion.** Single-DOF integrator (throttle × TE × reverser_sign − brake − tiny drag → semi-implicit Euler) produces visible acceleration on real consists. SD7 + 300 cars accelerates and runs out under throttle.

### Baseline FPS sweep

Single SD7 driving a flat-ground consist of N boxcars. Vanilla physics fully suppressed; only our trivial single-DOF solver is doing work.

| Cars | FPS | Frame ms | Δ over idle | µs/car (per tick) |
|---|---|---|---|---|
| 0 | 180 | 5.56 | — | — |
| 21 | 120 | 8.33 | 2.77 | 317 |
| 51 | 100 | 10.00 | 4.44 | 174 |
| 151 | 75 | 13.33 | 7.78 | 77 |
| 301 | 65 | 15.38 | 9.82 | 42 |

Per-car CPU drops 4× from 21 → 301 cars (317 → 42 µs/car/tick). Marginal cost in the 151 → 301 segment is **~7.3 µs per added car** — off-screen cars are nearly free thanks to vanilla's `IsVisible`-gated `PositionAccuracy.Standard` path. The curve is plateauing around **~13 ms/tick** for arbitrarily long consists.

### Vanilla comparison (informal, not apples-to-apples)

| Scenario | Cars | Engines | FPS | ms/frame |
|---|---|---|---|---|
| Our solver (single SD7) | 301 | 1 | 65 | 15.38 |
| Vanilla (multi-engine to overcome slip) | 309 | several | 40 | 25.00 |

Vanilla pays ~10 ms/frame more at this scale. **But vanilla is doing strictly more work**: 4-iteration coupler constraint solve, brake-pipe sub-stepping, wheel-slip / adhesion limit, brake passes, networking, spatial hash. Our trivial solver has none of those. The "we're faster" framing is misleading; the right framing is **we have ~10 ms/frame of budget into which phase 2 / 3 / 4 can put real physics work and still be competitive.**

### Bugs found & fixed during phase 1

1. **Anchor-and-walk-back writeback** — initial implementation positioned all cars relative to the lead loco's `WheelBoundsF` walking backward by cumulative car length, assuming `IntegrationSet.Cars` enumerates lead-first. It doesn't. Fixed by advancing each car independently along its own spline location by `ds`. No anchor math, no offset arithmetic — each car simply moves along the rails it's already on.

2. **Mass unit error (off by 2000×)** — `Car.Weight` is in **pounds**, not short tons. The `physics-vanilla-survey.md` doc says short tons; **the doc is wrong.** Confirmed via `Car.GravityForce` dividing Weight by 2000 (lb→short-ton) and `IntegrationSet.cs:393/430` multiplying by 0.453592 (lb→kg). Fixed our conversion to use `LbToKg = 0.453592f`. Survey doc fix saved as a memory for next time it's edited.

### Open questions / known gaps

- **Single SD7 trivially pulls 300 cars.** That's because we have no wheel-slip / adhesion model. Vanilla rejects this scenario realistically. Worth folding into a future phase (phase 2 candidate, or separate phase 4).
- **No grade resistance.** `Graph.GradeAtLocation` exists and is cheap to fold in; not yet wired.
- **Lead loco picking is naive** — first `BaseLocomotive` in `IntegrationSet.Cars` enumeration. May not match `IsLeadCandidate` flag state. Fine for phase 1, will revisit.
- **Audio is gone.** Engine sims (PrimeMover, SteamEngine) aren't ticking via `Car.FixedUpdate` (suppressed). Acceptable for the experiment; if we want flavor back in a later phase we'll do our own.
- **Mass / TE recompute happens once on adoption.** If load weight changes (industries), our cached `TotalMassKg` goes stale. Phase 2 should refresh on relevant KVO events.

### Plan for phase 2

Replace the single-DOF model with **per-car arc-length state and 1D compliant couplers** (RR-equivalent baseline). Each car gets its own `s_i`, `v_i`. Couplers are linear springs with damping; constraint relaxation via configurable Gauss-Seidel sweep count (1, 2, 4, 8, 16). Run the same FPS sweep at 21 / 51 / 151 / 301 cars at each sweep count. Plot:

- **FPS vs sweep count** at fixed N → cost of iteration
- **Stability boundary (max coupler stiffness without ringing) vs sweep count** → the headline finding for the Gauss-Seidel side
- **FPS vs N at fixed sweep count** → scaling slope

Phase 3 then swaps the solver to Featherstone (no chain iteration) and we run the same sweeps for direct comparison.

## Layout

```
consist-dynamics/
├── README.md                           ← this
├── *.csproj                            ← UMM mod, references Assembly-CSharp + Harmony
├── info.json                           ← UMM manifest
└── src/
    ├── ExperimentEntry.cs              ← UMM Load; installs Harmony patches
    ├── Patches/
    │   └── SuppressVanillaPhysics.cs   ← 3 prefix-false patches
    ├── Driver/
    │   └── ConsistDriver.cs            ← postfix on TC.FixedUpdate; iterates manager
    ├── State/
    │   └── ManagedConsist.cs           ← our per-consist state (s, v, mass, leadLoco, cars)
    ├── Solver/
    │   └── RigidConsistSolver.cs       ← phase 1: single-DOF integrator
    └── Input/
        └── ControlObserver.cs          ← KVO subscriptions for HUD inputs
```

## Notes on shape adherence

This experiment violates the production "additive, never replacing" rule for physics — **intentionally**, and only here. Per [`../README.md`](../README.md), experiments may patch directly and skip api kernel primitives. The replacement strategy informs nothing about how production physics is structured; production stays additive.

## Reference

- `docs/research/physics-vanilla-survey.md` — vanilla's physics surface (read first if extending)
- `ARCHITECTURE.md` — workspace layout
- `../README.md` — experiments folder conventions
