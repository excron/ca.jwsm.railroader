# mods/dispatch

Dispatch controller for automating train tasks (movements, scheduling, AI dispatcher integration).

## Status

**Full rewrite pending.** The implementation in `_reference/` is being replaced from scratch — don't patch bugs there; the work is to design the new shape on top of the v1 foundations.

## Mod roles

- **Consumer** — physics streams, ETA, topology, bus events from other mods.
- **Service provider** — `IDispatch` for other mods that want to query or queue dispatch state.
- **UI contributor** — scheduling controls, dispatch panel.

## Depends on

- `IEta` (from `mods/eta`).
- Topology + physics streams (from `physics`).
