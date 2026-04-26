# mods/enginecontrol

The new DPU + Dynamic brake + AE smoothing management mod. Replaces v0's `EngineControlService`, which became overloaded with concerns and ended up containing logic that should have been a feature mod.

## Mod roles

- **Consumer** — physics streams (coupler forces, kinematics, mass distribution), control intent state.
- **Service provider** — `IEngineControl` for other mods that want to query DPU state or AE mode.
- **UI contributor** — control panel.

## Closed-loop rule (canonical example)

This mod is the **canonical example** of why the closed-loop control rule exists.

v0 attempted AE smoothing without a physics feedback channel. Smoothing the throttle decoupled controls from in-train slack response, and a 200-car consist's couplers all snapped from the resulting rhythmic slack action.

> **This mod MUST consume physics streams (coupler forces, kinematics) before issuing throttle/brake changes.** Any control modification has to be informed by the physics consequences first. No exceptions.

## Depends on

- `physics` — hard dep, manifest-declared. Composition root refuses to bootstrap this mod without a physics provider registered.
- `IDurability` from `mods/durability` — useful for "are we close to breaking something?" awareness; soft dep via `registry.TryGet<IDurability>()`.
