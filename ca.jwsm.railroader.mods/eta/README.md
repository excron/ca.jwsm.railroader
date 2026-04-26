# mods/eta

ETA / ETE / distance-to-waypoint calculations. Currently consumed by the enhanced equipment window; designed to also serve dispatch, webview, and any other consumer that wants timing data.

## Canonical mod shape

ETA fills all three role types simultaneously — it's the canonical example of how a feature mod can be everything at once:

- **Consumer** — physics streams (kinematics, mass for grade-aware math), waypoints, track profile.
- **Service provider** — implements `IEta` (registered in api). Other mods consume this without ever referencing eta's assembly.
- **UI contributor** — registers an `EtaColumn` against the equipment-window surface; possibly an HUD overlay.

The calculation and the display ship together in one mod because they're the same domain owned by the same author. They're cleanly separated *internally* — `IEta` is consumable without touching display code.

## Depends on

- Physics streams (from `physics`).
- Waypoint / navigation contracts.

## Equipment-window relationship

ETA is one of N contributors to the equipment-window surface, not a baked-in assumption. If the equipment window's owner needs ETA *unconditionally*, it declares the dep in its manifest; otherwise it just lays out whatever contributors are present.
