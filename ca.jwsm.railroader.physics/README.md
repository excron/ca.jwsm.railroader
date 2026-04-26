# ca.jwsm.railroader.physics

**Required-foundational mod.** Provides the physics ground truth that other mods rely on.

## Additive, never replacing

Vanilla physics keeps running. We never patch out the game's physics tick. This mod **reads** vanilla state, **derives** richer values (real coupler forces, slack action, mass-distributed dynamics), and **exposes** them as streams.

Anywhere our derived truth disagrees with vanilla, vanilla wins for what it controls. Our truth informs what *we* control (DPU power, brake timing, AE smoothing).

The exact shape of the model — observation-only with better math vs. selective intervention via `ControlProperties` — is a phase-3-ish design call, not settled here.

## Vanilla evolves; our contracts don't

Vanilla has scaffolding for features it doesn't currently use (derailment formulas in `TrainMath`, a `TrackCondition` parameter that's hardcoded to `Dry`, an unused curve-speed-enforcement path). Future game patches will likely wire some of these up — weather→adhesion coupling is expected.

When that happens, this mod's **implementation** swaps its input source for that specific value. The **contract** on the api boundary stays stable. Consumers don't know or care whether `IAdhesion.GetCoefficient(...)` was derived by us from velocity + curvature + grade, or relayed from a vanilla weather system.

This gives us permission to ship "good enough" derivations now without locking ourselves out of cleaner implementations later. The migration when vanilla catches up is a 1-day implementation swap, not a redesign.

## Vanilla physics map

Current snapshot of what vanilla provides (and conspicuously doesn't): see [`docs/research/physics-vanilla-survey.md`](../docs/research/physics-vanilla-survey.md). Headlines:

- 1D linearized constraint solver in `IntegrationSet`. No force vectors.
- Wheel slip is a 3-state enum, not a number. Adhesion is velocity-only.
- Speed limits = `min(posted, curve-derived)`. No grade, weight, weather, or temporary slow orders.
- `TrainMath` has formulas (derailment, curve speed) it doesn't actually call.
- One canonical hook: postfix `TrainController.FixedUpdate()`.

## Owns

- Implementations of physics contracts defined in api: `ICouplerForces`, `IKinematics`, `IAirState`, `IPowerState`, `IMassModel`, `ITrackProfile`, `IConsistTopology`, `IConsistDirection`.
- Any Harmony patches needed to observe vanilla state for derivation.

## Why top-level

Required-foundational. Composition root warns or refuses to bootstrap any control-modifying mod (e.g. `enginecontrol`) without a physics provider registered. Heavy enough that it doesn't belong inside the api kernel.

## Closed-loop rule

Mods that write control inputs (throttle, brake) **must** consume the physics streams those inputs affect. This rule exists because v0 attempted AE smoothing without a physics feedback loop and broke every coupler in a 200-car consist. See the lessons-learned section of the arch doc for the full incident.

## Reference

See `..\ARCHITECTURE.md`.
