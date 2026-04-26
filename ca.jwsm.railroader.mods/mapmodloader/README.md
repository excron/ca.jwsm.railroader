# mods/mapmodloader

Clean-room map mod loader. Discovers, loads, validates, and exposes map mods to other consumers. Replaces the now-retracted RailLoader for our stack.

We do not look at RailLoader source. The loader's API and conventions come from observed map-mod patterns and our own design — see `docs/research/map-mods-vanilla-survey.md` for the reconnaissance behind this design.

## Where map mods live: `Maps/`, not `Mods/`

Map mods live in **a new `Maps/` folder at the same level as the game's `Mods/` folder**. Not inside `Mods/`.

```
<game install>/
├── Mods/      ← UMM mods (DLLs + info.json)
└── Maps/      ← our map mods (manifest + JSON + AssetBundle)
    ├── alinas-mapa/
    ├── penguin-trains/
    └── ...
```

Why a separate folder:

- Map mods don't have UMM-shaped manifests (`info.json` with class/assembly metadata) and don't ship DLLs (in the wild). UMM traversed them and ignored them anyway.
- The old approach dumped map mods into `Mods/` and expected RailLoader to scan that same folder while ignoring UMM mods. **Bidirectional wasted scanning.**
- Clean separation = each loader scans only what it owns. UMM scans `Mods/`, we scan `Maps/`. No I/O collisions, no "is this mine?" filtering.
- Clearer to the player: maps go in `Maps/`. Obvious from the name.

There is no backward-compat scan of `Mods/` for map mods. We're a clean break.

## Mod roles

- **Service provider** — `IMapModRegistry` (which mods are loaded), `IMapMod` (per-mod metadata + content handles), `IMapContentResolver` (look up identifiers across loaded mods).
- **Consumer** — filesystem (`Maps/` directory), `IGameLifecycle` (load on `WorldLoading.Wire`), `IBackgroundExecutor` (parse + hash + validate off the main thread), `ICacheService` (cache parsed manifests, merged JSON patches, asset-pack indices).
- **Coordinator** — calls into `physics` (or whichever L2 mod owns world-load patches) to inject content at the right vanilla hook points.

## Package shape (what goes in `Maps/<my-map>/`)

```
Maps/<my-map>/
├── manifest.json           ← metadata, deps, contributions
├── definitions/
│  ├── graph.json          ← tracks.{nodes, segments, spans}
│  ├── industries.json
│  ├── scenery.json
│  └── loads.json
├── assetpack/
│  ├── shared.bundle       ← Unity AssetBundle (prefabs, models)
│  └── shared.manifest
└── mixintos/              ← optional alternative contribution path
   └── game-graph/
      └── patches.json
```

Manifest declares: `id`, `version`, `name`, `requires`, `loadAfter`, `conflictsWith`, `contributions[]`. **No `assemblies` field** — code mods (DLL-bearing) aren't supported here; if a mod ships a DLL, it goes in `Mods/` as a real code mod.

## Validation policies

### MP join

Hard parity with the rest of our mod stack. See *Multiplayer / mod parity* in ARCHITECTURE.md. A map mod missing on either side = refuse-to-join with explicit reason.

### Save load

Content-only map mods get **graceful recovery**:

- Vehicles stranded on missing track → relocated via the game's replace-consist feature.
- Orphaned contracts / waybills / performance history → dropped.
- User sees a one-time recovery summary at load: *"Loaded with 2 missing map mods. Relocated 7 vehicles. Cleared 3 orphaned contracts."*

(Code mods missing → refuse to load — but that's a code-mod policy, not this loader's concern, since map mods are content-only.)

## Lifecycle integration

Hooks into `IGameLifecycle`'s multi-step init:

| Phase | What the loader does |
|---|---|
| `Bootstrap.Construct` | Discover packages in `Maps/`, parse manifests (background) |
| `Bootstrap.Register` | Register `IMapModRegistry` in the service registry |
| `WorldLoading.Wire` | Validate deps, merge JSON patches, cache results, apply via the physics hook (background-prep + main-thread apply) |
| `WorldLoading.Ready` | Notify consumers (`mods/map`, `mods/editor`, etc.) that map content is live |
| `WorldUnloading` | Tear down loaded content references; cancel any in-flight loader background work |

`requires` declarations between map mods (Alina depends on shared library, etc.) are topo-sorted by the same kernel machinery that orders our own mods.

## Caching

Expensive work that gets cached via `ICacheService`:

- Parsed manifests (`manifest.json` per mod, hashed)
- Merged JSON graph patch across all active mods (hashed by combined inputs + mod versions)
- Asset-pack indices (definition-id → bundle entry lookups)
- Validated dependency graph (hashed by all manifests)

Cache auto-invalidates when any input file or mod version changes.

## Threading

Almost all loader work is background-safe and runs through `IBackgroundExecutor`:

- Manifest parsing (file I/O + JSON)
- Hash computation
- JSON merging
- AssetBundle loading (Unity-thread-safe APIs only; final `Instantiate` calls marshal back)
- Dependency graph validation

UI shows progress via `ui.RunWithProgress("Loading 3 maps…", task)`. Continuation runs on main thread for the actual scene mutation.

## Hard rule: no Harmony patches in this mod

`mods/*` cannot Harmony-patch (per project discipline). The vanilla injection points for "intercept Graph init" / "register AssetPack before first lookup" / "inject before OPS init" are owned by `physics` (or a small new L2 world-orchestration mod if physics doesn't fit). This loader consumes those hooks via api contracts; it never patches.

## Depends on

- `IGameLifecycle`, `ICacheService`, `IBackgroundExecutor`, `IMainThreadDispatcher`, `IServiceRegistry`, `IPersistenceService`, `IAuthority` (for save-time mod-set recording) — all from api kernel.
- The world-patch hook contracts implemented by `physics` (or the L2 orchestration mod that ends up owning them).

## Notable consumers

- `mods/editor` — hard dep. No map loader, no editing.
- `mods/map` — consumes `IMapContentResolver` to render loaded content.
- `mods/dispatch`, `mods/eta`, etc. — soft deps; query loaded content for routing/timing if present.
