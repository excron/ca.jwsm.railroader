# ca.jwsm.railroader.physics

**Required-foundational mod.** Provides the physics ground truth that other mods rely on.

## Additive, never replacing

Vanilla physics keeps running. We never patch out the game's physics tick. This mod **reads** vanilla state, **derives** richer values (real coupler forces, slack action, mass-distributed dynamics), and **exposes** them as streams.

Anywhere our derived truth disagrees with vanilla, vanilla wins for what it controls. Our truth informs what *we* control (DPU power, brake timing, AE smoothing).

The exact shape of the model — observation-only with better math vs. selective intervention via `ControlProperties` — is a phase-3-ish design call, not settled here.

## Owns

- Implementations of physics contracts defined in api: `ICouplerForces`, `IKinematics`, `IAirState`, `IPowerState`, `IMassModel`, `ITrackProfile`, `IConsistTopology`, `IConsistDirection`.
- Any Harmony patches needed to observe vanilla state for derivation.

## Why top-level

Required-foundational. Composition root warns or refuses to bootstrap any control-modifying mod (e.g. `enginecontrol`) without a physics provider registered. Heavy enough that it doesn't belong inside the api kernel.

## Closed-loop rule

Mods that write control inputs (throttle, brake) **must** consume the physics streams those inputs affect. This rule exists because v0 attempted AE smoothing without a physics feedback loop and broke every coupler in a 200-car consist. See the lessons-learned section of the arch doc for the full incident.

## Reference

See `..\ARCHITECTURE.md`.
