# ca.jwsm.railroader.api

The kernel. All our mods bootstrap into it on top of UMM/Harmony. Stays thin and coherent — never welds heavyweight implementations onto itself.

## Owns

- **Composition root + mod lifecycle** — discovers our mods, validates manifests, wires services, registers patches.
- **Foundation services** — `ILoggerFactory`, `IAuthority`, `IEventBus`, `IServiceRegistry`, `ICommandRegistry`, `IPersistenceService`.
- **All cross-mod contracts.** Every interface and value type any mod exposes to or consumes from another lives here:
  - Physics: `ICouplerForces`, `IKinematics`, `IAirState`, `IPowerState`, `IMassModel`, `ITrackProfile`, `IConsistTopology`, `IConsistDirection`
  - UI: `IWindowService`, `IBottomBarService`, `IThemeService`, `IAssetService`, `ISurfaceRegistry`
  - Features: `IEta`, `IDurability`, `IEngineControl`, `IDispatch`, `IMapModRegistry`, `IEditorSession`, etc.
- **Observer patches** that expose game state as primitives.

## Doesn't own

- **Contract implementations** — those live in `physics`, `ui`, or `mods/*`.
- **Behavior-modifying patches** — those live in the foundational mod that owns the behavior. Forbidden in `mods/*` entirely.
- **Heavyweight subsystems.** If something is big enough to feel like its own thing (physics, UI), promote it to a top-level peer instead of welding it in here.

## Communication shapes

Two well-authored shapes plus a directory:

- **Bus (events)** — discrete pub/sub. "Throttle changed", "save loaded", "coupling occurred".
- **Streams (services)** — continuous producer-cadence push. Coupler force, kinematics, brake pressure.
- **Registry** — directory for resolving contracts and discovering capability presence.

Rule of thumb: **if you'd call `GetX()` every tick, it's a stream — subscribe instead.**

## Reference

See `..\ARCHITECTURE.md` for the full architectural picture and v0 lessons-learned.
