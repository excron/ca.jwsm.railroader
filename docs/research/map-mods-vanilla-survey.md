# Map Mods — Vanilla & Authoring Reconnaissance

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/`), v0 reference (`_reference/`), public sources (Alina's Mapa repo + community refs)
**Purpose:** Map what's required to author Railroader map mods, so our `mods/mapmodloader` (clean-room replacement for the now-retracted RailLoader) knows what surface to expose.

> **Hard rule:** we do not look at RailLoader source, ever. The loader's API
> shape comes from our needs and from observed map-mod patterns, not from
> reverse-engineering RL.

---

## Headlines

1. **Vanilla has no map-mod extension API.** No registries, no factories, no plug-in interfaces for "add an industry" or "add a track segment." Everything is GameObject + scene-based.
2. **Map mods are JSON patches + Unity AssetBundles + optional DLLs.** Manifest declares dependencies; mixinto-style JSON files patch the graph / industries / scenery; AssetBundles ship prefabs and definitions; optional code DLLs supply custom `IndustryComponent` subclasses.
3. **Industries are the complex subsystem.** Each is a monolithic GameObject with pluggable components (loaders, unloaders, repair tracks, etc.) bound to `TrackSpan`s. Contracts, performance history, storage all persist via `IKeyValueObject`.
4. **`PrefabStore` + `AssetPackRuntimeStore` is the asset lookup mechanism.** Definitions are indexed by string identifier. New packs registered at runtime are auto-discovered.
5. **Save format does NOT include map content.** Only game state (car positions, switch state, contracts, performance history). Missing a mod = orphaned references in the save = unplayable. **Validation on load is mandatory.**
6. **`mods/*` can't Harmony-patch (our rule)**, so the loader needs explicit injection hooks in `physics` or another L2 mod to wire into the world-load sequence.
7. **Every recent kernel primitive (threading, caching, lifecycle, load-order) has direct use cases here.** See *Implications* section.

---

## Vanilla world data structures

### Track network

| Type | File | Role |
|---|---|---|
| `Graph` | `Track/Graph.cs` | Singleton MonoBehaviour managing topology |
| `TrackNode` | `Track/TrackNode.cs` | Switch nodes; position, rotation, throw state |
| `TrackSegment` | `Track/TrackSegment.cs` | Directional A→B segments; Bezier curve, speed limit, style, priority, group |
| `Location` | `Track/Location.cs` | Immutable struct: (segment, distance, end orientation) |
| `TrackSpan` | `Track/TrackSpan.cs` | Range on track (lower Location → upper Location) — bound by industry components |

**Loading model:** Graph is a MonoBehaviour in the main scene. `Awake → RebuildCollections()` discovers child `TrackNode`/`TrackSegment` GameObjects via `GetComponentsInChildren<>()`. **No persistent file format** — the vanilla map is baked into the scene hierarchy.

**Implication for the loader:** to add track, the loader either spawns new GameObjects under `Graph` and re-runs `RebuildCollections()`, or deserializes JSON and instantiates from a definition. Either way, this happens before anyone reads the topology.

### Industries / locations

| Type | File | Role |
|---|---|---|
| `Industry` | `Model.Ops/Industry.cs` | Container; identifier, contracts, performance history, tick loop |
| `IndustryComponent` | `Model.Ops/IndustryComponent.cs` | Abstract base; binds to `TrackSpan[]`, has `CarTypeFilter` |
| `IndustryLoaderBase` | `Model.Ops/IndustryLoaderBase.cs` | Produces load; `productionRate`, `maxStorage`, auto-orders empties |
| `IndustryUnloader` | `Model.Ops/IndustryUnloader.cs` | Consumes load |
| `RepairTrack` | `Model.Ops/RepairTrack.cs` | Repairs cars, charges maintenance |
| `TeamTrack` | `Model.Ops/TeamTrack.cs` | Crew amenities |
| `Interchange` | `Model.Ops/Interchange.cs` | Transfers between loads/types |
| `FormulaicIndustryComponent` | `Model.Ops/FormulaicIndustryComponent.cs` | Defines production via input→output term formulas |
| `ProgressionIndustryComponent` | `Model.Ops/ProgressionIndustryComponent.cs` | Tier-based unlock mechanics |

**Lifecycle:**
1. `Initialize()` — once on first load or version change; subclasses migrate data
2. `Tick()` — every 5 seconds; check waybills, service cars, update production
3. `OrderCars()` — predict demand, request empties
4. `Service()` — load/unload/repair/etc.
5. `DailyReceivables` / `DailyPayables` — financial transactions

**Persistence:** state lives in `Industry._keyValueObject` (KVO):
- `"contract"` — current tier
- `"nextContract"` — pending tier change
- `"_perfHist"` — Dict<int day, float score> (rolling 7 entries)
- `"_recvdCars"` — count
- `"init"` — version string for migration

**To add a new industry**, a map mod provides:
1. GameObject with `Industry` script (unique identifier)
2. Child GameObjects with `IndustryComponent` subclass scripts
3. `TrackSpan` objects on the industry tracks
4. (Optional) custom C# `IndustryComponent` subclass in a DLL

### Signals / infrastructure

| Type | File |
|---|---|
| `CTCSignal` | `Track.Signals/CTCSignal.cs` |
| `SignalAspect` | `Track.Signals/SignalAspect.cs` |
| `CTCSignalCuller` | `Track.Signals/CTCSignalCuller.cs` |

Signals are placed GameObjects, not formally registered — spatial queries determine aspect. Map mods place them by instantiating GameObjects.

### Scenery / props

| Type | File | Role |
|---|---|---|
| `SceneryAssetManager` | `Helpers/SceneryAssetManager.cs` | Loads scenery by identifier via PrefabStore |
| `SceneryDefinition` | `Definition/SceneryDefinition.cs` | Defines a scenery asset (ID → model prefab + culling radius) |
| `SceneryAssetInstance` | `Helpers/SceneryAssetInstance.cs` | Placed instance with material customization |

Stateless decoration. Async load on demand; spatial streaming as the camera moves.

### Terrain

No explicit class. Game uses RealWorldTerrain plugin; terrain is baked into the scene or procedural. **Not dynamically modifiable by mods** — no hook surface.

---

## Asset loading mechanisms

### PrefabStore + AssetPack

```
PrefabStore (singleton)
  └─ List<AssetPackRuntimeStore> _stores
     ├─ Internal stores  (game assets)
     └─ External stores  (mod assets, loaded from Mods/)
```

| Type | File | Role |
|---|---|---|
| `AssetPackCatalog` | `AssetPack.Common/AssetPackCatalog.cs` | Pack metadata |
| `AssetPackRuntimeStore` | `AssetPack.Runtime/AssetPackRuntimeStore.cs` | Loads bundles, serves definitions |
| `PrefabStore` | `Model.Database/PrefabStore.cs` | Singleton registry |

**Discovery:** `Utilities.FindAssetPacks(basePathForLocation)` at startup. External stores load after internal ones; same identifier → external **replaces** internal (mod override).

**API:**
- `LoadAssetAsync<T>(packId, assetId)` → `LoadedAssetReference<T>`
- `DefinitionForIdentifier<T>(id)` → `T`
- `AllDefinitionInfosOfType<T>()`

**Format:** Unity AssetBundles. Each pack is a separate bundle file (e.g., `shared.bundle`).

**Hot reload:** none. Bundles load at startup; mod changes require restart.

### Loading sequence (high level)

```
1. Scene loads (Unity)
2. Graph.Awake → RebuildCollections (discovers track from scene)
3. PrefabStore.Create → discovers asset packs, loads internal bundles
4. Industries place as GameObjects; scripts Awake
5. HostManager.LoadSnapshot → applies saved state
6. World is live
```

The loader needs to insert itself between steps 1 and 2 (to add to scene before Graph reads it) or between 3 and 4 (to register asset packs before industries Awake).

---

## Injection / registration points

### What vanilla provides

**Nothing official.** No registries, no factories, no `IModEntryPoint`-style interface. Map mods have to:

1. **Place GameObjects in the scene** (or instantiate prefabs at runtime)
2. **Discover their own DLL types via reflection**
3. **Register AssetPacks before PrefabStore caches first lookup**

### What v0 designed (without RailLoader)

v0's `WorldLayoutService` and `WorldPatchRuntime` (in `_reference/ca.jwsm.railroader.api/abstractions/World/` and corresponding host services) settled on a **JSON mixinto pattern**:

```
1. Map packages scanned, manifests parsed
2. Dependencies validated and ordered
3. Mixinto files (JSON) discovered per target ("game-graph", "game-loads", etc.)
4. WorldPatchDefinition.Build() merges all JSON
5. CompatibilityGraphEvents.PublishGraphJsonWillDeserialize() — extension hook
6. Graph deserialized + applied via WorldLayoutApplier.Apply()
```

This is sound. Our v1 loader adopts the same architectural approach (manifest → contributions → JSON merge → deserialize → apply) without referencing v0 code 1:1 or RL.

### The mods/* patch problem

Our discipline rule: `mods/*` cannot Harmony-patch. So the loader can't intercept `Graph.Awake` or `PrefabStore.Create` directly.

**Resolution:** the patches needed for injection live in **physics** (or possibly a small foundational concern of their own). They're behavior-changing patches that fit the L2 model. The loader sits in `mods/*` and consumes a contract that physics implements:

```
api defines → IWorldPatchHost (or similar)
physics implements → registers patch hooks for Graph init / PrefabStore init
mods/mapmodloader consumes → calls IWorldPatchHost.RegisterPatch(...)
```

This keeps the architectural rules clean.

---

## Save / load integration

### What's saved (game state)

```csharp
struct Snapshot {
    Dict<string, Player> players;
    Dict<string, Car> cars;              // positions, velocity, waybill, crew
    Dict<uint, CarSet> carSets;          // consists
    List<BatchCarAirUpdate> carAir;      // air state
    HashSet<string> thrownSwitchIds;
    Dict<string, Dict<string, IPropertyValue>> properties;  // industry contracts, perf history, etc.
    Dict<string, TrainCrew> trainCrews;
    Snapshot.Map map;                    // time, day, spawn pos
    Dict<string, SwitchList> switchLists;
    Dict<string, TurntableState> turntables;
}
```

### What's NOT saved (must come from mod assets every load)

- Track topology (graph)
- Industry definitions and placements
- Scenery definitions
- Signal placements
- Load types, car types, locomotive types

### The orphaning problem

If a map mod is uninstalled or its content changes between sessions:
- `Snapshot.properties` references industry IDs that no longer exist
- `Snapshot.cars` references car positions on segments that no longer exist
- Save becomes corrupt or unplayable

**Design requirement for the loader:**
- Track which mods (and which versions) were active per save
- On load, validate mod presence + compatible version
- Warn / block / offer recovery if missing
- Possibly persist a manifest of "this save expects these mods" alongside the save

---

## Asset packaging conventions

A map mod ships as a folder under `Mods/`:

```
MyMapMod/
├─ manifest.json           ← metadata, deps, contributions
├─ definitions/
│  ├─ graph.json          ← tracks.{nodes,segments,spans}
│  ├─ industries.json     ← areas + industries
│  ├─ scenery.json        ← scenery definitions
│  └─ loads.json          ← load types
├─ assetpack/
│  ├─ shared.bundle       ← Unity AssetBundle (prefabs, models, textures)
│  └─ shared.manifest
├─ code/
│  └─ MyMapMod.dll        ← optional; custom IndustryComponent types
└─ mixintos/
   └─ game-graph/
      └─ patches.json     ← alternative contribution path
```

**manifest.json shape (representative):**
```json
{
  "manifestVersion": 1,
  "id": "mymap.example",
  "name": "My Example Map",
  "version": "1.0.0",
  "loadAfter": [{"id": "some.dep"}],
  "requires": [{"id": "other.dep", "minimumVersion": "2.0"}],
  "conflictsWith": [{"id": "incompatible.thing"}],
  "contributions": [
    {"target": "game-graph", "sourcePath": "definitions/graph.json"}
  ],
  "assemblies": ["MyMapMod.dll"]
}
```

---

## Existing map mods — observed patterns

**Alina's Mapa** ([github.com/AlinaNova21/Railroader-Mods](https://github.com/AlinaNova21/Railroader-Mods)):
- C# solution with shared utilities + per-map subprojects
- AssetBundles built from a separate Unity project
- Uses Railloader 1.8+ for loading (the dep we're replacing)
- Provides MapEditor UI for authoring
- Mixintos for graph patches; asset packs with custom scenery
- Imports observed: `Model.Ops`, `Track`, `Model.Definition.Data`, `Newtonsoft.Json`, plus the now-dead `Railloader` namespace

**Common patterns across observed mods:**
- Manifest-driven discovery
- JSON for declarative content (track, industries, areas)
- AssetBundles for binary content (models, textures)
- Optional DLL for custom behavior types
- Heavy use of dependency declarations (loadAfter, requires)

---

## v0 experiments

`_reference/ca.jwsm.railroader.api/abstractions/World/` and `host/Services/`:

```csharp
public interface IWorldLayoutService {
    WorldLayoutStatus Status { get; }
    void Update(WorldLayoutSourceUpdate update, IWorldLayoutResolver resolver);
    void Clear(string sourceId);
    void Tick();
    void TryApplyEarly(string reason);
}

public interface IWorldAssetStoreService {
    WorldAssetStoreStatus Status { get; }
    void Update(string sourceId, IReadOnlyList<WorldAssetStoreRegistration> registrations);
    void Clear(string sourceId);
}
```

**What worked:**
- Manifest-driven packaging (clean separation, validation)
- JSON mixinto system (non-destructive; multiple mods coexist)
- Dependency ordering (prevents conflicts)
- "Apply early" hook (patch graph before OPS init)

**What didn't / what to avoid:**
- Coupled to RailLoader for actual loading + Harmony hooks
- No explicit save-validation pass for missing mods
- Asset store and layout were two services that should probably coordinate more tightly

---

## Implications for `mods/mapmodloader`

### What the loader exposes (rough sketch)

- **Discovery + validation** — scan `Mods/`, parse manifests, resolve deps, detect conflicts
- **Graph patching** — merge JSON, deserialize into `TrackNode`/`TrackSegment`/`TrackSpan`, apply
- **AssetPack registration** — register new `AssetPackRuntimeStore` instances with `PrefabStore`
- **Industry registration** — instantiate prefabs, bind components to spans, init state
- **Persistence integration** — track active mods per save; validate on load
- **Cleanup** — on uninstall, remove orphan references gracefully

### Hooks needed in physics (or another L2 mod)

Since the loader can't patch:

- Pre-`Graph.Awake` hook: lets us inject scene objects before topology is built
- Post-`PrefabStore.Create` hook: lets us register asset packs before first lookup
- Pre-OPS-init hook: lets us complete industry placement before industries tick

These belong in `physics` (or a small new L2 mod focused on world-load orchestration if physics doesn't fit).

### Where the recently-added kernel primitives play out

This is the load-bearing observation. **Each primitive we just added has a direct use case here.**

| Primitive | Concrete use in mapmodloader |
|---|---|
| **`IGameLifecycle` (multi-step init)** | `WorldLoading.Construct` → discover packages, parse manifests. `WorldLoading.Register` → register loader contracts. `WorldLoading.Wire` → validate deps, apply graph patches via the physics hook. `WorldLoading.Ready` → notify other mods (map viewer, editor) that map content is live. |
| **Topo-sorted `requires`** | Map mods' inter-package dependencies (Alina depends on shared library, etc.) get ordered automatically by the same mechanism that orders our own mods. Consistent pattern; no separate dep resolver. |
| **`ICacheService`** | The merged JSON graph patch is expensive to compute; cache keyed on hash of all source JSONs + mod versions. AssetPack indices, validated dep graphs, parsed industry definitions all cacheable. Mod version change auto-invalidates. Saves significant time on subsequent world loads. |
| **`IBackgroundExecutor`** | Loading + parsing AssetBundles, hashing JSON files, parsing many manifests, validating dep graphs, applying patches — all background-safe CPU-bound work. Show "Loading 3 maps…" progress with `IProgress<float>`. Continuation on main thread applies to scene. **Exact pattern user described (the dispatch-graph-compile use case).** |
| **`IMainThreadDispatcher`** | After background prep, the actual GameObject instantiation and scene mutation must marshal back. Built into the executor's continuation default. |
| **Orchestration discipline** | Map mods can't sleep/poll/retry waiting for other map mods to finish loading. The kernel orchestrates; the loader trusts the orchestration. |
| **Save-scope persistence + auto-cleanup** | Per-save "which mods were active" record uses save-scoped persistence. Auto-cleaned with the save. |

This is the validation that the kernel primitives are right-shaped — they fall out naturally for a non-trivial subsystem.

---

## Decisions and open questions

### Decided

- **DLL loading — deferred indefinitely.** No observed map mod ships a code DLL; pattern doesn't appear in the wild. We don't design for it. If evidence emerges later, revisit.
- **MP mod-parity — hard refuse-to-join.** Server and client must run identical mod sets. Mismatch = refuse, with explicit reason: "Server runs [list], client is missing [list]" (and vice versa for client-only mods). No partial-MP, no graceful degradation, no "best effort." See the *Multiplayer / mod parity* section in ARCHITECTURE.md.
- **Save validation — same rule.** Saves require their full mod set to be present and version-matched to load. Missing → refuse with the same clear-reason UX. Falls out of MP-parity automatically: a save's mod set is just the set the host had when saving.
- **Identifier conflict — deferred.** Won't preemptively design rename / namespacing. If two mods collide on an identifier, we'll see it as a real failure case and address it then. Probably error-with-clear-report by default.

### Still open

- **Where exactly do we hook into world load?** Need to confirm the precise vanilla call sequence for "after PrefabStore ready, before industries tick." Focused mining pass when we get to phase 6/7.
- **AssetBundle versioning.** If a mod ships v1.0 and updates to v2.0 with the same identifiers, do we replace, refuse, or warn? Probably replace + invalidate cache.
- **Hot reload.** Out of scope for v1. Restart-required.

---

## Cross-cutting observations

1. **No registration APIs anywhere.** GameObject discovery + string identifiers is the only universal pattern.
2. **`IKeyValueObject` is the universal state mechanism.** Industries use it; mods can use it for their own state.
3. **Identifiers are strings, globally namespaced, and unchecked.** Conflicts possible. Loader must validate.
4. **The mixinto pattern is the right starting shape** — non-destructive merging by target name, validated by dependency graph.
5. **Save-format constraints define hard validation requirements.** Without mod-presence validation, players lose progress silently. Non-negotiable.
6. **Vanilla Harmony patches are the only injection mechanism for runtime hooks** — and we don't allow them in `mods/*`. So coordination with `physics` (or a new L2 world-orchestration mod) is mandatory.

---

## TL;DR

1. Map mods = manifest + JSON patches + AssetBundles + optional DLL.
2. Vanilla has no extension API; we invent one. v0's mixinto pattern is sound; we adopt the architecture.
3. Industries are the deep subsystem (loaders, unloaders, repair, contracts, KVO state).
4. `PrefabStore` + `AssetPackRuntimeStore` is the asset discovery mechanism — register new stores, definitions become available.
5. Save format only has game state. Missing-mod validation on load is mandatory.
6. `mods/*` can't patch — loader needs hooks in `physics` (or a new L2 world-orchestration mod) for actual injection.
7. The recent kernel primitives (lifecycle multi-step init, topo-sorted requires, cache, threading) are exactly the right tools for this problem.
8. Decisions: no DLL support (not in the wild); MP requires mod parity (hard refuse-to-join with reason); saves inherit the same rule; identifier conflict deferred until evidence demands it.
