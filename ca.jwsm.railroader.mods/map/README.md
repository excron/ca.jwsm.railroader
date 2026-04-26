# mods/map

In-game map viewer with enhanced features. Built from scratch as our own self-contained map window — never modifies the vanilla map UI.

## Why a parallel map, not an enhancement of vanilla's

Per our hard rule: we don't modify game UI prefabs. v0's lesson was that injecting into vanilla screens is fragile (layout changes, prefab updates, Unity UI Toolkit edge cases all break the injections).

So this mod builds **our own map window** in the ui mod's framework, drawing the same data the vanilla map does (track graph, vehicle positions, signals) plus whatever we want to add on top. The vanilla map keeps working unmodified — players can use either.

"Enhancing the game's map" here means *replacing* it with a richer alternative, not patching the original. There's already a community "Map Enhancer" mod; ours is a clean-room version under our coherence runtime.

## Mod roles

Canonical multi-role shape:

- **Consumer** — `IConsistTopology`, `IKinematics` (vehicle positions), `ITrackProfile` (graph + grade + speed limits), bus events for entity changes.
- **Service provider** — `IMap` for other mods to query (current focus, visible region) and contribute (overlays, markers).
- **UI contributor** — registers the map window and a `MapSurface` into ui's surface registry.

## Companion concept: track profile

A "track in profile" view — vertical cross-section of a route showing grade, mileposts, speed limits, signals as you scroll along the line — belongs in this mod. Either a sub-view of the map window or a sibling window. Same data (`ITrackProfile`), different rendering. Useful for AE planning, dispatch context, run preview.

## Overlay contribution model

The map should be a **platform for overlays**, not a fixed view. Same pattern as the equipment window: other mods register what they want to draw against `IMap`'s surface, the map renders them, no mod owns the window itself.

Likely contributors:

- `mods/durability` — coupler stress hot-spots along the consist
- `mods/dispatch` — train assignments, dispatcher overlays, blocks
- `mods/eta` — projected paths, arrival markers, time-to-waypoint
- `mods/enginecontrol` — DPU placement diagrams, AE plan visualization

## Sister project: webview / web

The browser-based map (top-level `web` + `mods/webview`) is the out-of-process counterpart. Both consume similar physics + topology contracts; both render maps. They evolve in parallel — one in-game (this mod), one in-browser.

## Depends on

- `IConsistTopology`, `IKinematics`, `ITrackProfile` (from `physics`)
- ui framework — windows + surface registry (from `ui`)
- Bus events for entity/topology changes (from api)
