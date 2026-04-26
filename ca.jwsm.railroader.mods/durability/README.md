# mods/durability

Durability and stress gameplay. Tracks degradation, breakage thresholds, and surfaces alerts. Couplers are the first surface; this mod is named generically because the same mechanics will apply to other components as we wire them in (brake shoes, traction motors, etc.).

## Boundary with `physics`

The **force/stress math** lives in `physics` (`ICouplerForces` and future siblings). **This mod consumes those streams** and adds the *gameplay layer* on top — durability state, degradation over time, breakage events, alerts, repair concerns.

The split is consistent: physics computes the truth, durability turns that truth into consequences.

## Mod roles

- **Consumer** — subscribes to physics stress streams (`ICouplerForces` initially, others as they're added).
- **Service provider** — implements `IDurability` for other mods (e.g. enginecontrol asks "is this coupler near breaking?" before issuing throttle changes).
- **UI contributor** — alerts, equipment-window column, possibly per-component status indicators.

## Depends on

- `ICouplerForces` (from `physics`) — hard dep, manifest-declared.
- Future stress streams (brake-shoe wear, traction-motor strain, etc.) as they're exposed by physics.
