# ca.jwsm.railroader.world

**Required-foundational mod.** Owns the world domain — track infrastructure, asset catalog, environment, world-load orchestration. Sits parallel to `physics` and `ui` at L2.

## Why a separate foundational mod (not in api, not in physics)

- **Not api** — api stays a thin kernel. World data structures (graph, asset catalog) and their typed contracts are too domain-specific for the kernel.
- **Not physics** — physics is about derivation of forces, slack, motion. The graph isn't physics; physics *consumes* the graph. Putting world concerns into physics overloaded that mod the same way v0 overloaded `EngineControlService`.
- **Standalone** — world has multiple mods that depend on it (physics for graph access, mapmodloader for injection hooks, map for rendering, dispatch for routing) and accumulates real surface (time, weather, scene orchestration).

## What world owns

This is a placeholder with a broad scope; specifics get firmed up as concrete needs surface.

### Track infrastructure

Typed read access to vanilla's `Graph` — nodes, segments, spans, location math, grade/curvature lookups, segment walks. Wraps and exposes the substrate other mods consume.

Likely contracts (in api):
- `IWorldGraph` — node/segment/span lookups, traversal helpers
- `ITrackProfile` — N-segment lookahead with grade + curvature + speed limit (was tentatively in physics; lives here since track is a world concern, not a force concern)
- `ILocationMath` — distance-along-track, location-from-distance, signed-distance-between

### Asset catalog

Typed read access to vanilla's `PrefabStore` and `AssetPackRuntimeStore`. Typed lookups by definition kind (scenery, car, load, locomotive). Discovery of registered packs.

Likely contracts (in api):
- `IAssetCatalog` — typed `Get<T>(definitionId)`, `List<T>()`, pack discovery
- `IAssetPackRegistration` — register a new asset pack at runtime (used by mapmodloader)

### World-load orchestration

Granular load events beyond api's `WorldLoading` / `WorldLoaded`. Sub-phases that mods needing finer hook points can subscribe to:

- `GraphAboutToInitialize` — last chance to inject content into the scene before vanilla walks it
- `GraphInitialized` — graph is queryable
- `PrefabStoreInitialized` — asset packs are registered, lookups work
- `OpsAboutToInitialize` — last chance to register industries before they tick
- `OpsInitialized` — industries are live

These are observer patches in api/host that publish through `GamePatchBus` → `IEventBus`. World subscribes and exposes them as typed events on an `IWorldLoad` service for late-arriving mods that need phase queries.

This is what `mods/mapmodloader` actually needs to inject map content — not a physics concern.

### Environment

State of the world that isn't track or motion:

- `IWorldTime` — time of day, calendar day, season (where applicable)
- `IWeather` — placeholder; the upcoming game patch is expected to add weather. When it does, world wraps it in a typed contract; mods consume it without knowing where the value came from. (See the *Vanilla evolves; our contracts don't* principle in physics's README — same applies here.)

### Scene management

Bounded scope here — vanilla owns the scene; world doesn't try to do anything fancy. But it does provide:

- A clean place for "the world is being torn down" / "the world is being assembled" hooks beyond the basic lifecycle phases
- Coordination between content-contributing mods (mapmodloader) and content-consuming mods (map, dispatch)

## Orchestration role: world-domain load + cache

The api kernel provides the generic primitives (`IGameLifecycle`, `ICacheService`, `IBackgroundExecutor`). **World is the first-class consumer that orchestrates them for the world domain** — and it's the natural single home for that orchestration.

Concretely, world coordinates:

- **Lifecycle** — subscribes to api's granular observer-patch events; exposes typed `IWorldLoad` for late-arriving consumers; sequences world-domain work across `WorldLoading.Construct → Register → Wire → Ready`.
- **Caching** — owns the cache strategy for world-domain artifacts: merged JSON graph patches (across active map mods), asset-catalog indices (definition-id → bundle entry), pre-built mod-content AssetBundles (layer-2 cache), and derived structures used by world-domain services (track adjacency, Bezier sample tables, switch-decode tables — layer-3 cache). All keyed on input hash + mod set + versions; auto-invalidated on any change.
- **Threading** — heavy world-load work (manifest parsing, JSON merging, asset-pack discovery, graph derivation) runs through `IBackgroundExecutor`; main-thread continuation applies scene mutations.

This means `mods/mapmodloader` doesn't roll its own caching or threading for world content — it cooperates with world (or just uses world's contracts), and world handles the heavy lifting via api's primitives. One source of truth for world-domain orchestration, one place to optimize when load times become a problem.

## What world does NOT own

- **Forces, slack, kinematics, mass** — physics
- **Windows, theme, assets-as-UI-resources** — ui (note: scenery assets are world; UI icons are ui)
- **Mod loading, lifecycle, persistence, threading, caching** — api
- **Specific game features** — those live in `mods/*`

## Why top-level

Required-foundational. Composition root will warn or refuse to bootstrap mods that need world contracts (mapmodloader, map, eventually dispatch) without a world provider registered.

Symmetric with physics and ui in this respect:
- physics required → control-modifying mods can't run without it
- world required → world-content-touching mods can't run without it
- ui required → component-contributing mods can't run without it

## Dependency direction

- **world depends on**: api (everything depends on api)
- **physics depends on**: api + world (graph + location math for derivation)
- **ui depends on**: api
- **mods/* depend on**: api + whichever foundational mods they need

Acyclic. Composition root resolves in dependency order at bootstrap.

## Patches

Per the patch policy: api owns observer patches; foundational mods own behavior-changing patches. World's surface is *primarily* observation (exposing graph data, publishing load events). Any behavior-changing patches world ends up needing live in world (the L2 fallback for the "static handoff" pattern).

### Speculative future patch: layer-1 graph cache

The most aggressive possible optimization for slow graph loads is a prefix patch on `Graph.RebuildCollections` that skips vanilla's scene-walk and restores from cache when inputs are unchanged. **If we ever pursue this, the patch lives here in world** — graph is world's domain, and behavior-changing patches in foundational mods are explicitly allowed.

Not recommended unless layer-1 (vanilla's `Graph.Awake`) is provably the dominant bottleneck. Risky because vanilla expects the walk to populate live `GameObject` references, which can't survive a session in cache. Layers 2 (mod-content instantiation) and 3 (derived structures) cache cleanly without touching vanilla — start there.

## Status

This is a **broadly scoped placeholder**. Each contract above gets firmed up when its first real consumer needs it. We don't pre-build surface speculatively; we establish the home so concerns have somewhere clean to land.

First concrete consumers:
- `mods/mapmodloader` — needs the world-load orchestration hooks and asset-pack registration
- `physics` — will move `ITrackProfile` here when it implements the corresponding stream
- `mods/map` — will consume `IWorldGraph` for rendering

## Reference

See `..\ARCHITECTURE.md` and the research notes in `..\docs\research\`.
