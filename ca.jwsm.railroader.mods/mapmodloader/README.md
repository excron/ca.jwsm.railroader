# mods/mapmodloader

Clean-room map mod loader. Discovers, loads, and exposes map mods to other consumers.

## Mod roles

- **Service provider** — `IMapModRegistry`, `IMapMod`, possibly `ILoadedMapState`.
- **Consumer** — filesystem, bus events for save/load lifecycle.

## Notable consumers

- `mods/editor` declares this as a hard dep — no map loader, no editing.

## Why it lives in `mods/*`

Map loading isn't physics, isn't UI, and isn't a behavior change to game simulation. It's a feature mod that happens to be heavily depended on by other features. Standard `mods/*` placement.
