# Tile Loading & Bardo — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Map.Runtime/`, `Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Floating Origin](floating-origin.md), [Cars & Cargo](cars-cargo.md), [Track Topology](track-topology.md), [Save/Load](save-load.md)

Railroader streams the world in two largely independent layers: **terrain tiles** (`MapManager` + `MapStore`, 500m square heightmap chunks rebuilt on the fly from on-disk `tile_xxx_yyy.data` PNG files) and **car models** (`Car.LoadModelsAsync` triggered by `CarCuller`'s distance band). They share the camera position as their input but have different ownership — terrain is per-machine local state with no MP sync, and `MapManager` is a `[ExecuteInEditMode]` `MonoBehaviour` that runs identically in editor and play mode. Then there's **Bardo**, which is something else entirely: it's the *operational off-railroad pseudo-location* where cars go when an industry/interchange "ships them away," persisted as a non-empty `Car.Bardo` string. Bardo cars are kept in `_carLookup` but stripped from `_carCuller`, `_integrationSets`, and `_spatialHash`; their model is never loaded and they have no live transform. Crucially, **terrain tile unload does NOT move cars to Bardo** — these are different lifecycles that happen to both deal with "out of sight" entities. Floating origin is transparent to Bardo cars (no transform to shift); for *visible* cars in loaded tiles, the origin shift is dispatched explicitly via `TrainController.WorldDidMove` (cars are not in `WorldTransformerTargetList`). See [Floating Origin › MapManager interaction](floating-origin.md#mapmanager-interaction--the-unloaded-tile--bardo-region) and [Cars & Cargo › Lifecycle spine](cars-cargo.md#lifecycle-spine).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Map.Runtime.MapManager` | `Map.Runtime/MapManager.cs:18` | Singleton terrain orchestrator. `[ExecuteInEditMode]` |
| `MapManager.UpdateVisibleTilesForPosition(Vector3 gamePos)` | `Map.Runtime/MapManager.cs:361` | Camera-driven tile-set update. Called 1 Hz by `MapCameraUpdater` |
| `MapManager.RequestPriorityLoad(Vector3 gamePoint, float distance=250)` | `Map.Runtime/MapManager.cs:1096` | Async coroutine, blocks until target tiles are `Ready`. Called by `CameraSelector._JumpToPoint` |
| `MapManager.ApplyWorldToGameOffset(Vector3 offset)` | `Map.Runtime/MapManager.cs:258` | Floating-origin shift hook. Called by `MapCameraUpdater.WorldMoved` |
| `MapManager.KeepLoaded` (`Vector2Int?`) | `Map.Runtime/MapManager.cs:152` | Pin a tile so it never unloads (set by `PlayerController.AttachedCarChecker`) |
| `MapManager.Invalidate(Vector2Int)` | `Map.Runtime/MapManager.cs:611` | Mark a tile dirty for re-build |
| `MapManager.AddModifier(IMapModifier, CoordinateSystem)` | `Map.Runtime/MapManager.cs:978` | Register heightmap/mask/tunnel modifier; auto-invalidates intersecting tiles |
| `Map.Runtime.MapStore` | `Map.Runtime/MapStore.cs:15` | Disk persistence. `Map.json` + `tile_xxx_yyy.data` PNGs in `StreamingAssets/Maps/<dir>/` |
| `Map.Runtime.MapTerrain` (MonoBehaviour) | `Map.Runtime/MapTerrain.cs:6` | Terrain GameObject wrapper; carries `BuildStatus.Pending|Built|Ready` |
| `Map.Runtime.TileData` | `Map.Runtime/TileData.cs:15` | Heightmap (513×513) + 3 masks; ARGB32 PNG codec |
| `Map.Runtime.TileProxy` | `Map.Runtime/TileProxy.cs:5` | Vestigial placeholder type — `ShowProxyTile` is empty. See gotchas |
| `Game.MapLoader` | `Game/MapLoader.cs:14` | Sends `MapDidLoadEvent` exactly once after scene load |
| `Game.Events.MapDidLoadEvent` (struct, empty) | `Game.Events/MapDidLoadEvent.cs:6` | Messenger broadcast — fires `TrainController.OnMapLoaded`, `CameraSelector.HandleMapDidLoad`, `StateManager.OnMapDidLoad` |
| `Cameras.MapCameraUpdater` | `Cameras/MapCameraUpdater.cs:15` | The ticker — owns the 1 Hz `UpdateCoroutine` and the `WorldDidMoveEvent` subscription |
| `Game.Messages.CarSetBardo` | `Game.Messages/CarSetBardo.cs:8` | `[HostOnly]` MP message: `(carId, bardoId)` |
| `TrainController.MoveToBardo(carId, senderId)` | `TrainController.cs:2347` | Host-side facade; sends `CarSetBardo` |
| `TrainController.HandleSetBardo(carId, bardo)` | `TrainController.cs:2353` | Both-sides handler; calls `WillRemoveCar(car, isMovingToBardo:true)` + `SetVisible(false)` + `car.Bardo = bardo` |
| `Model.Ops.ReturnFromBardoOrder` (struct) | `Model.Ops/ReturnFromBardoOrder.cs:5` | `IOrder` that re-spawns a specific Bardo car at an industry |

---

## Spine 1: Terrain tile streaming

```
MapCameraUpdater.UpdateCoroutine                     ← MapCameraUpdater.cs:35
   │  every 1.0s (WaitForSecondsRealtime)
   │     position = Camera.main.transform.position    (world space)
   │     mapManager.UpdateVisibleTilesForPosition(WorldTransformer.WorldToGame(position))
   ▼
MapManager.UpdateVisibleTilesForPosition(gamePos)    ← MapManager.cs:361
   │  if (_store == null) LoadOrCreateStore()         ← lazy first-touch load
   │  cameraVelocity = (gamePos - lastPos) / dt       (ignored if > 1000 m/s)
   │  UpdateHeatmap(gamePos)                          ← LRU of 5 most-recent tiles
   │  list = NearbyTiles(gamePos, vel, NearbyTileDistance=1500m)   (750m in editor)
   │  if (KeepLoaded.HasValue && !list.Contains) list.Add(KeepLoaded.Value)
   │  SetVisibleTilesQueued(list)
   ▼
SetVisibleTilesQueued(shouldBeVisible)               ← MapManager.cs:406
   │  for each in shouldBeVisible:
   │     if no terrain at tile  → _queuedTileLoads.Add(tile)
   │     _pendingTileUnloads.Remove(tile)             ← cancel pending unload
   │  for each tile in _terrains.Keys not in shouldBeVisible:
   │     _pendingTileUnloads[tile] = Now              ← schedule unload (60s grace)
   │  ScheduleWorkTilesIfNeeded()
   ▼
WorkLoadUnloadQueues coroutine                       ← MapManager.cs:437
   │  yield null                                       ← always yield first
   │  WorkUnloadQueue()                                ← unload tiles whose pendingUnload > 60s
   │  WorkQueue(_overrideTileLoads, breakOnOverride:false, …, "overrides")
   │  GatherInvalidatedTiles() + WorkQueue(_invalidatedTiles, breakOnOverride:true, …)
   │  if no overrides: WorkQueue(_queuedTileLoads, breakOnOverride:true, …)
   │  _loadTilesTask = null
   ▼
RequestTerrain(tilePos, debugTag)                    ← MapManager.cs:773
   │  if !buildingThisAlready: BuildTerrain(tilePos)
   ▼
BuildTerrain(tilePos)                                ← MapManager.cs:797
   │  await store.RequestTile(tilePos)                 ← Task<TileData>; loads PNG from disk OR fetches from Mapbox if missing
   │  terrain = PrepareTerrain(tileData, profile.terrainMaterial)   ← pool reuse OR steal-furthest
   │  yield TerrainBuilder.BuildTerrain(...)           ← Unity TerrainData population (heightmap, masks, trees, details)
   │  FixSeams(terrain) + SetNeighbors(terrain)        ← stitch with adjacent tiles
   │  terrain.buildStatus = Ready
   │  swap value.MapTerrain ← BuildingTerrain
```

### Key constants

| Constant | Value | Site |
|---|---|---|
| `tileDimension` (default) | 500 (meters) | `MapManager.cs:67` (overwritten by `_store.TileDimension` on load) |
| `TerrainHeightMin` / `Max` | 500f / 1500f | `MapManager.cs:75-77` (Y range packed into ushort heightmap) |
| Heightmap resolution | 513 × 513 per tile | `MapManager.CreateTerrainData:1190`, `TileData.ctor:96` |
| `NearbyTileDistance` | 1500m (play) / 750m (editor) | `MapManager.cs:174` |
| Unload grace | 60s | `WorkUnloadQueue:496` (`Time.unscaledTime` based) |
| Update poll | 1.0 s realtime | `MapCameraUpdater.cs:37` |
| Time-budget per slice | 5/10/20 ms (queue ≤5/≤10/>10) | `UpdateBudgetForQueueSize:486` |
| Velocity look-ahead | `cameraPosition + cameraVelocity * 3f` | `NearbyTiles:522` |
| Load distance multiplier | up to 2× at 400 m/s camera vel | `NearbyTiles:521` |
| Pool ceiling before steal | 64 active records | `PrepareTerrain:852` |
| Heatmap window | 5 tiles, weighted by `1 + index * NearbyTileDistance` | `UpdateHeatmap:386`, `PrepareTerrain:870` |
| `RequestPriorityLoad` default `distance` | 250m | `MapManager.cs:1096` |
| `RequestPriorityLoad` poll | 0.5s realtime, +0.25s settle | `MapManager.cs:1125-1129` |
| Terrain `treeDistance` | 1500m | `CreateTerrain:920` |
| `detailObjectDistance` | 250m | `CreateTerrain:919` |

### Tile addressing

```csharp
public Vector2Int TilePositionFromPoint(Vector3 pointInGame)             // MapManager.cs:767
{
    int dim = _store.TileDimension;
    return new Vector2Int(Mathf.FloorToInt(pointInGame.x / dim),
                          Mathf.FloorToInt(pointInGame.z / dim));
}
```

**Game-space input.** `TilePositionFromPoint` always takes a *game-space* `Vector3`; callers convert via `WorldTransformer.WorldToGame` before invoking. See [Floating Origin › Where conversions happen](floating-origin.md#where-conversions-happen-the-boundary-list). Tile `(0,0)` is the rect `[0..500, 0..500]` on game-space XZ. Negative tile coords are valid (Mathf.FloorToInt(-0.1/500) = -1).

---

## On-disk layout

`MapStore` reads from `Application.streamingAssetsPath + "/Maps/" + directoryName/`. Default `directoryName = "Default"` (`MapManager.cs:63`); production uses the scene-authored value (e.g. for the Bushnell-Whittier map).

```
StreamingAssets/Maps/<directoryName>/
   Map.json                     ← origin (LatLng) + tileDimension + tile catalogue
   tile_000_000.data            ← per-tile PNG (ARGB32)  - heightmap in R/G channels, masks in A
   tile_000_001.data
   tile_-1_000.data             ← negative coords supported
   …
```

`Map.json` (`Map.Runtime.DTO.Map`):

```csharp
struct Map {
    LatLng origin;               // real-world anchor (35.382614°N, -83.49541°W default — Bryson City NC area)
    int    tileDimension;        // meters per tile
    Tile[] tiles;                // catalogue: { x, y } per existing tile
}
```

The per-tile PNG (`MapStore.PathFor: tile_{x:000}_{y:000}.data`) packs:

| Channel | Contents |
|---|---|
| R, G | Heightmap. `height_meters = ((R*256 + G) / 65.535) + 500` (range 500–1500m, 16-bit) |
| B | unused (always 0 on save) |
| A | Bit-packed: `(WaterPresent << 7) | (VegetationMask4bit << 4)` |

Codec lives in `TileData.Save()` (`TileData.cs:186`) and `PopulateHeightmap` Burst job (`TileData.cs:33-48`). Resolution is `513` per axis (heightmap), masks are `512` (one less). The PNG encoder writes `EncodeToPNG()` directly — `LoadImage` expects a complete PNG file, not a raw blob.

### Catalogue semantics

`Map.json.tiles[]` enumerates **which tile coords are *known*** to the map; it doesn't carry data. Loaders ask `MapStore.HasTileDataAt(tile)` first (`MapManager.cs:460, 531, 1084, 1213`) — `true` if the tile is in the catalogue, regardless of whether the PNG file actually exists.

If the catalogue says yes but the PNG is missing/corrupt, `MapStore.RequestTile` calls `RebuildTile` (`MapStore.cs:167`) which goes to **Mapbox** (`MapboxLoader.FetchHeights`) and writes a fresh tile to disk. **In play mode this branch logs an error and re-throws** (`MapStore.cs:159-162`) — Mapbox fetch is editor-time-only (map authoring).

If a tile coord is *not* in the catalogue, `RequestPriorityLoad` skips it silently and `NearbyTiles` filters it out (`MapManager.cs:531`). `BuildTerrain` would never be called.

### Patch points (tile data)

| Hook | Why |
|---|---|
| `MapStore.RequestTile` | Inject mod-side height sources (e.g., procedural terrain, alt online provider). Replace the `RebuildTile` fallback with your own. |
| `MapStore.PathFor` | Redirect storage path (mod-pack tiles in a different folder). |
| `MapStore.HasTileDataAt` | Spoof catalogue membership for synthetic tiles. |
| `TileData.LoadIfNeeded` | Custom decoder for non-PNG tile formats. |
| `TileData.Save` | Custom encoder; preserve channels you don't use. |
| `MapManager.LoadOrCreateStore` | Swap `MapStore` for a custom subclass. The field is `private MapStore _store;` — patch needed (or reflection). |

---

## Tile streaming triggers

There are exactly **three** call sites that drive tile loads:

| Caller | Path | Frequency |
|---|---|---|
| `MapCameraUpdater.UpdateCoroutine` | `MapManager.UpdateVisibleTilesForPosition(camera_game_pos)` | **1.0 s realtime**, while `MapCameraUpdater` is enabled |
| `CameraSelector._JumpToPoint` | `MapManager.RequestPriorityLoad(target_game_pos)` (override queue) | One-shot per camera teleport |
| `PlayerController.AttachedCarChecker` | `MapManager.KeepLoaded = TilePositionFromPoint(player_game_pos)` | **0.5 s scaled time**, while detached from a car |

**There is no host-driven tile load.** `MapManager` does not reference `StateManager`, `Multiplayer`, `Network.Client`, or any HostOnly attribute. Tile loads are 100% per-machine, driven by *that machine's* `Camera.main` position. In MP, the host and each client maintain independent tile sets; the host loading a tile does not propagate to clients, and a client's `KeepLoaded` pin doesn't either.

### The override / queue / invalidate priority

`WorkLoadUnloadQueues` (`MapManager.cs:437`) processes three tile lists in order:

1. **`_overrideTileLoads`** — set by `RequestPriorityLoad`'s `OverrideVisibleTilesQueued` (`MapManager.cs:398`). Processed first; never preempted by anything else; **clears `_queuedTileLoads`** when set. Only `RequestPriorityLoad` ever populates this.
2. **`_invalidatedTiles`** — gathered from `_terrains` records with `Invalidated == true`. Processed if no overrides; **preempted** if a new override arrives mid-queue.
3. **`_queuedTileLoads`** — normal "want this tile visible" queue from `SetVisibleTilesQueued`. Processed last; **preempted** by overrides.

Inside `WorkQueue`, every iteration checks `_overrideTileLoads.Count <= 0` (when `breakOnOverride` is true) and calls `UpdateBudgetForQueueSize` to retune the per-slice millisecond budget.

### `RequestPriorityLoad` semantics (the synchronous "must have tiles now" path)

```csharp
public IEnumerator RequestPriorityLoad(Vector3 gamePoint, float distance = 250f)
{
    if (_overrideTileLoads.Count > 0) {
        Log.Error("Ignoring RequestPriorityLoad; queue contains {count} elements", ...);
        yield break;                                  // ← reentrant calls are NO-OP
    }
    if (_store == null) LoadOrCreateStore();
    var tilePositions = NearbyTiles(gamePoint, Vector3.zero, distance);
    if (tilePositions.Count == 0) {                   // ← e.g., gamePoint outside the map catalogue
        Log.Error("RequestPriorityLoad: 0 tiles for {gamePoint} {distance}", ...);
        yield break;
    }
    UpdateHeatmap(gamePoint);
    OverrideVisibleTilesQueued(tilePositions);        // → clears _queuedTileLoads, sets _overrideTileLoads
    while (!tilePositions.All(TerrainIsReady))        // ← polls every 0.5s realtime
        yield return new WaitForSecondsRealtime(0.5f);
    if (waitedAny) yield return new WaitForSecondsRealtime(0.25f);  // ← settle delay
}
```

**Critical:** `RequestPriorityLoad` is **non-reentrant**. Calling it while another `RequestPriorityLoad`'s tiles are still building (i.e., `_overrideTileLoads.Count > 0`) is silently dropped with an error. Mods that want to chain priority loads must `yield return` the first to completion or watch `_overrideTileLoads.Count` themselves.

The wait loop polls `mapTerrain.buildStatus == BuildStatus.Ready` for *every* tile in `tilePositions`. `BuildStatus` transitions: `Pending` (set in `PrepareTerrain:901`) → `Built` (after `TerrainBuilder.BuildTerrain` completes, `MapManager.cs:828`) → `Ready` (after `FixSeams`+`SetNeighbors`, `MapManager.cs:831`). A tile that fails to build never reaches `Ready` and the wait loop **spins forever** (logging "Still waiting." every 1s after the first second). No timeout, no give-up.

### Camera-velocity look-ahead

```csharp
maxDistance *= Mathf.Lerp(1f, 2f, Mathf.InverseLerp(30f, 400f, cameraVelocity.magnitude));
Vector2Int cameraTile = TilePositionFromPoint(cameraPosition + cameraVelocity * 3f);
```

When the camera moves >30 m/s, `NearbyTiles` extends the radius (up to 2× at 400 m/s) and shifts the tile center 3s ahead along velocity. Sort key flips from `sqrMagnitude` (slow) to a velocity-dot-product priority (fast) at 10 m/s. So a strategy-camera fly-by loads ahead-of-camera tiles first, while a parked first-person camera loads concentrically. Look-ahead is **not** done when `RequestPriorityLoad` calls `NearbyTiles(gamePoint, Vector3.zero, distance)` — priority loads are explicitly velocity-zero.

### `KeepLoaded` (the player-tile pin)

```csharp
// PlayerController.AttachedCarChecker (every 0.5s, when player is NOT attached to a car)
if (_attachedCarId == null) {
    var instance = MapManager.Instance;
    if (instance != null)
        instance.KeepLoaded = instance.TilePositionFromPoint(
            WorldTransformer.WorldToGame(character.GetMotionSnapshot().Position));
}
```

`KeepLoaded` is a single-tile pin. `SetVisibleTilesQueued` always appends it to the visible list (`MapManager.cs:379`), and `PrepareTerrain`'s steal-furthest logic explicitly excludes `KeepLoaded` from being recycled (`MapManager.cs:861`). When the player **attaches to a car** (driving from inside, walking on a moving train) `KeepLoaded` stops being updated — the car's own movement updates the camera and drives `UpdateVisibleTilesForPosition` instead. The pin is also not cleared on detach; whatever the last-set value was persists. **Mods that want to pin extra tiles need a custom mechanism** — `KeepLoaded` is single-valued.

---

## Tile unload triggers

```csharp
private void WorkUnloadQueue() {                                        // MapManager.cs:494
    int graceSeconds = 60;
    float threshold = Now - graceSeconds;
    foreach (var (tile, queuedAt) in _pendingTileUnloads)
        if (queuedAt < threshold) _tilesToRemove.Add(tile);
    foreach (var tile in _tilesToRemove) {
        RemoveAt(tile);                       // → RemoveTerrain + RemoveProxyTile
        _pendingTileUnloads.Remove(tile);
    }
    _tilesToRemove.Clear();
}
```

**Unload pipeline:**
1. A tile that's currently in `_terrains` but is no longer in the `shouldBeVisible` set (computed by `NearbyTiles` from camera position) gets timestamped into `_pendingTileUnloads[tile] = Now`.
2. Returning to that tile (camera drifts back) cancels the pending unload (`SetVisibleTilesQueued:415`).
3. After 60 s `Time.unscaledTime` has passed (grace window survives game-pause and `Time.timeScale=0`), `WorkUnloadQueue` fires `RemoveAt(tile)` → `RecycleTerrain` → `Object.Destroy(mapTerrain.gameObject)`.
4. Unloaded `MapTerrain` GameObjects are **not** pooled into `_terrainPool` for reuse — the pool is a dead path (queue is `private readonly Queue<MapTerrain>` declared but `Enqueue` is never called). Recycling currently always destroys.
5. `MapStore.UnloadTile(tilePosition)` (`MapStore.cs:52`) disposes the `TileData`'s `NativeArray<float> Heightmap` (Allocator.Persistent). **This is the only path that releases the heightmap memory.** Mods that hold a `TileData` reference past unload will see a disposed `NativeArray`.

### Stealing-from-pool path (capacity-pressure unload)

When `_terrains.Count >= 64` and a new tile build needs a `MapTerrain`, `PrepareTerrain` doesn't wait for the 60s grace — it picks the *furthest* tile from the heatmap that is **not** `KeepLoaded`, recycles it immediately:

```csharp
if (_terrains.Count < 64) result = CreateTerrain();
else {
    MapTerrain victim = _terrains.Where(kv => kv.Key != KeepLoaded)
                                 .OrderBy(distanceToHeatmap).Last();
    _terrains.Remove(victim.tilePosition);
    _pendingTileUnloads.Remove(victim.tilePosition);
    PrepareTerrainForRecycling(victim);
    result = victim;
}
```

This bypasses the `WorkUnloadQueue` 60s gate. **A car or player on the stolen tile loses its terrain immediately**; if they were `KeepLoaded`, they're protected. If they weren't, expect a one-frame "no terrain below me" spike — `PlayerController.CheckForTerrainBelow` (`PlayerController.cs:336`) handles this case by raycasting and, if no hit, jumping the player to a known-safe tile.

### What happens to entities in an unloading tile

Cars do **not** despawn or move to Bardo when their tile unloads. Instead:
- `Car.LoadModelsAsync` is gated by the `CarCuller` distance band; cars far from the camera have `UnloadModels` called on them (visual only — KVO state, physics, `CarAirSystem`, brakes, all keep ticking).
- The car GameObject persists; only the model body GameObject is destroyed (`Car.UnloadModels`, see [Cars & Cargo › lifecycle spine](cars-cargo.md#lifecycle-spine)).
- The car's transform updates from physics/track-position continue. If the car is over a tile that no longer has terrain under it, `Car.OnPosition` still fires and `CarDidPosition` still updates the spatial hash and culler. The car doesn't "fall" because position is driven by `Track.Location` math, not by Unity physics + ground collisions.

Player avatars in unloading tiles are protected by:
1. `KeepLoaded` (set every 0.5s when the player is detached).
2. `PlayerController.CheckForTerrainBelow` raycasting + repositioning fallback.

**Mods that spawn world-space GameObjects must subscribe to their own life-cycle** — there is no "tile unloaded, please clean up your stuff in this rect" event. The closest signal is per-tile: subscribe to `MapManager.Invalidate` patches, or watch `Object.OnDestroy` on the relevant `MapTerrain`.

---

## Tile invalidation (rebuild-in-place)

`MapManager.AddModifier(IMapModifier, CoordinateSystem)` returns a key; the modifier registers in `_heightmapModifiers` / `_maskModifiers` / `_tunnelModifiers` and intersecting tiles are invalidated:

```csharp
Invalidate(modifier.Mask.Bounds, 0.5f);                                // 0.5s debounce
```

`Invalidate(Bounds, delay)` (`MapManager.cs:643`) batches all invalidations from a single frame's worth of modifier mutations into one delayed coroutine (`InvalidateAfterDelayWorker`, 0.5s). On expiry, every queued tile gets `value.Invalidated = true` and `ScheduleWorkTilesIfNeeded` runs.

`WorkLoadUnloadQueues` then picks up `_invalidatedTiles` *after* overrides but *before* the queued-loads pass — invalidations preempt regular streaming but yield to priority loads.

### Modifier types

| Type | Affects | Storage |
|---|---|---|
| `HeightmapModifier` | Terrain Y values (cuts/fills) | `_heightmapModifiers` (`ModifierStorage<HeightmapModifier>`) |
| `MaskModifier` | Splat / vegetation / water masks | `_maskModifiers` |
| `TunnelModifier` | Carves holes in terrain (`SetHolesDelayLOD`) | `_tunnelModifiers` |

`AddModifier` accepts `CoordinateSystem.World` or `CoordinateSystem.Game`; world-space modifiers are converted via `modifier.OffsetBy(-_gameToWorldOffset)` so storage is always game space. **`_gameToWorldOffset` is per-machine** — see floating-origin gotcha below.

### Patch candidates (modifiers / invalidate)

| Hook | Why |
|---|---|
| `MapManager.AddModifier` | Track all modifier registrations; emit mod events. |
| `MapManager.Invalidate(Vector2Int)` (public) | Listen for tile rebuilds (e.g., to refresh mod-side derived data per tile). |
| `MapManager.Invalidate(Bounds, float)` (private) | Patch to add custom modifier intersection logic. |
| `MapManager.GatherInvalidatedTiles` | Inspect/filter which tiles get rebuilt. |
| `MapManager.RebuildAll` | Hook full reload (e.g., after preference change). Currently called by density-set and `/terrain rebuild` console command. |

---

## `Game.MapLoader` and `MapDidLoadEvent`

```csharp
[RequireComponent(typeof(Graph))]
public class MapLoader : MonoBehaviour {                                 // Game/MapLoader.cs
    private void OnEnable() { _graph = GetComponent<Graph>(); StartCoroutine(LoadUI()); }
    private IEnumerator LoadUI() {
        // editor-only: instantiate EventSystem, StateManager, GameUI scene
        yield return null;
        TrainController.Shared.graph = _graph;                            // wire up topology
        yield return new WaitForEndOfFrame();
        Messenger.Default.Send(default(MapDidLoadEvent));                 // ← THE FIRE
    }
}
```

`MapLoader` lives on the same root GameObject as the `Graph` (the track topology, see [Track Topology](track-topology.md)). It exists once per map scene. Sequence of `Map*` events:

```
GlobalGameManager._LoadMap                                              UI.Menu/GlobalGameManager.cs:103
   ├─ Messenger.Send<MapWillLoadEvent>()
   │     └─ StateManager.OnMapWillLoad: TimeWeather.Reset, RestoreNotifier.Initialize, IsUnloading=false,
   │                                    PrepareGameKeyValueObject, PreparePlayerProperties
   ├─ Unload MainMenu, Load GameUI, Load map scenes (additive)
   ├─ MapLoader.OnEnable runs (in the loaded map scene)
   │     └─ wires TrainController.Shared.graph = _graph
   │     └─ Messenger.Send<MapDidLoadEvent>()
   │           ├─ TrainController.OnMapLoaded → CreateCarCullerIfNeeded
   │           ├─ CameraSelector.HandleMapDidLoad → JumpToSpawn (if at origin & no relative car)
   │           ├─ StateManager.OnMapDidLoad → log only
   │           └─ DefinitionEditorModeController.MapDidLoad
   ├─ StateManager.ApplyGameSetup(gameSetup)
   └─ Multiplayer.ConnectClient(networkSetup)

GlobalGameManager._ReturnToMainMenu                                     UI.Menu/GlobalGameManager.cs:207
   ├─ Messenger.Send<MapWillUnloadEvent>()
   │     └─ StateManager.OnMapWillUnload: tear-down hooks
   ├─ Unload map scenes, GameUI, load MainMenu
   └─ Messenger.Send<MapDidUnloadEvent>()
         └─ StateManager.OnMapDidUnload: tear-down hooks
```

**`MapDidLoadEvent` is empty (`[StructLayout(Size=1)]`).** It carries no payload. Subscribers must look up `MapManager.Instance` / `TrainController.Shared` / etc. themselves. Sent **after** the scene's `Awake`/`Start` have run — but `MapManager.Awake/OnEnable` (which calls `ClearCaches`) runs as part of `LoadSceneAsyncAsync`, so subscribers can rely on `MapManager.Instance != null`. `MapManager.UpdateVisibleTilesForPosition` won't have been called yet on `MapDidLoadEvent` (the first call is the next `MapCameraUpdater.UpdateCoroutine` tick, up to ~1s later) — but `MapCameraUpdater.UpdateCoroutine` immediately blocks on `mapManager == null` so it's always behind first.

### Patch candidates (events)

| Hook | Why |
|---|---|
| `Messenger.Default.Register<MapDidLoadEvent>(...)` | Mod startup hook for "map ready, all systems wired." Use this for one-shot per-map init. **Cleaner than patching `MapLoader.LoadUI`.** |
| `Messenger.Default.Register<MapWillLoadEvent>(...)` | Pre-load tear-down (e.g., dispose mod state from previous session). |
| `Messenger.Default.Register<MapWillUnloadEvent>(...)` | Last chance to read live state (`Cars`, `Industries`, etc.) before they're destroyed. Note: `IsUnloading` is **not** set true here; that flag is for application quit (`StateManager.OnApplicationQuit`). |
| `Messenger.Default.Register<MapDidUnloadEvent>(...)` | Final cleanup. Scenes are gone. |

---

## Spine 2: Bardo (the unloaded-region pseudo-location)

Bardo is **not a tile-loading concept**. It's the operational/ops layer's "this car has been shipped off-railroad" pseudo-location. The `Car.Bardo` property is a `string` that, when non-empty, signals:
- The car is currently held by an off-railroad system (typically an `Interchange`, sometimes an industry that wants to make the car "not present" for a while).
- The string itself is the `Identifier` of whatever industry/component holds the car (`OpsController.TryDecodeBardo` resolves it).
- The car has no live `Transform`, no model, no `IntegrationSet`, no spatial-hash entry, no `CarCuller` registration.
- The car still exists in `_carLookup`; `CarForId(id)` returns it; KVO is queryable; save/load round-trips it.

### `Car.Bardo` field & properties

```csharp
public string Bardo { get; set; }                                       // Car.cs:479
public bool IsInBardo => !string.IsNullOrEmpty(Bardo);                  // Car.cs:481
```

Bardo is plain auto-property (no KVO). `Bardo = "..."` and reads are direct. Assignment is host-side via `HandleSetBardo`; clients receive `CarSetBardo` and re-run the same handler.

### MP wire format

```csharp
[HostOnlyAuthorizationRule]
[MessagePackObject(false)]
public struct CarSetBardo(string carId, string bardo) : IGameMessage {
    [Key(0)] public string CarId;
    [Key(1)] public string Bardo;
}
```

- **HostOnly** — `MoveToBardo(carId, senderId)` calls `StateManager.AssertIsHost()` then `StateManager.ApplyLocal(new CarSetBardo(...))` (which both runs the local handler and broadcasts to clients).
- The `Bardo` field is the *industry identifier* (`_industryComponent.Identifier`). Conventionally the interchange's identifier. To leave Bardo, send `CarSetBardo(carId, null)` or empty string.

### `MoveToBardo` → `HandleSetBardo` flow

```csharp
public void MoveToBardo(string carId, string senderId) {                // TrainController.cs:2347
    StateManager.AssertIsHost();
    StateManager.ApplyLocal(new CarSetBardo(carId, senderId));           // host writes immediately + broadcasts
}

public void HandleSetBardo(string carId, string bardo) {                 // TrainController.cs:2353
    if (!TryGetCarForId(carId, out var car)) throw new Exception("No such car: " + carId);
    if (car.Bardo == bardo) { Log.Warning(...); return; }                 // idempotent
    if (car.IsInBardo) {                                                  // already in Bardo → just update label
        Log.Warning("Car {car} change bardo: {old} to {new}", ...);
        car.Bardo = bardo;
        return;                                                           // ← NOTE: no SetVisible(true), no _carCuller.Add
    }
    if (car == SelectedCar) SelectedCar = null;
    WillRemoveCar(car, isMovingToBardo: true);                            // pre-cleanup
    car.SetVisible(visible: false);                                       // hides renderers + couplers + anglecocks
    car.Bardo = bardo;                                                    // mark Bardo
}
```

`WillRemoveCar(..., isMovingToBardo:true)` (`TrainController.cs:1516`):
- Removes from `_carsForUpdateSets`, `_spatialHash`, `_carCuller`, `_carIdsNearbyPlayer`, `_carSegmentCache`.
- **Does NOT** deallocate road number, **does NOT** remove from `_carLookup` or `_cars` — Bardo cars stay in the registry.
- Calls `OpsController.RemoveCar(car.id)` and `car.WillDestroy(isMovingToBardo:true)` — see [Cars & Cargo › patch candidates](cars-cargo.md#patch-candidates-car).
- Calls `car.SetAdjacentCarsNotConnected()` and removes from its `IntegrationSet`.

After `HandleSetBardo`:
- `Car.IsInBardo` returns true.
- `Car.gameObject` still exists with the `Car` MonoBehaviour but no body model.
- KVO state (`load.{n}`, `_condition`, `oiled`, `owned`, `ops.waybill`, etc.) is intact.
- `CarForId(carId)` returns the car; `Cars` enumerable includes it.
- `OpsController.CarsInArea` filters out Bardo cars (`OpsController.cs:353`).

### Bardo lifecycle (the four state transitions)

```
1.  Spawn (new car):
       descriptor.Bardo == null → CreateCarRaw → _cars.Add → _carCuller.Add
       Car.IsInBardo == false

2.  Spawn (Bardo car loaded from save):
       descriptor.Bardo == "<indID>" → CreateCarRaw → _cars.Add → BUT _carCuller.Add is skipped:
           if (!car.IsInBardo) _carCuller.Add(car);                      ← TrainController.cs:710
       Car.IsInBardo == true after Setup completes (descriptor.Bardo flowed in via CarDescriptor)

3.  Move to Bardo:
       host calls TrainController.MoveToBardo(carId, industryID)
         → CarSetBardo broadcast → HandleSetBardo on all peers
         → WillRemoveCar(car, isMovingToBardo:true) + car.SetVisible(false) + car.Bardo = id

4.  Return from Bardo (interchange or scripted):
       Industry generates ReturnFromBardoOrder(carId)
         → IndustryContext.AddOrderedCars finds the Bardo car
         → _trainController.CarForId(returnFromBardoOrder.CarId).Descriptor()  ← Car.Descriptor at Car.cs:1033
         → PlaceTrain(...) → CreateCarIfNeeded(descriptor, carId)
              ↓ existing-car branch (TrainController.cs:691):
              if (carId != null && _carLookup.TryGetValue(carId, out var value)) {
                  value.SetAdjacentCarsNotConnected();
                  _integrationSets.RemoveCar(value);
                  value.KeyValueObject.ApplyValues(descriptor.Properties);     ← !!  ApplyValues, not ResetData
                  value.trainCrewId = descriptor.TrainCrewId;
                  return value;
              }
       At this point Car.Bardo is **still set** to the old industry identifier.
       PlaceTrain proceeds to position the car on track (sets LocationF/R, velocity).
       Car.SetVisible(true) is called separately by the model-load path? — see Gotcha below.
```

**The `Car.Bardo` string is not cleared by `CreateCarIfNeeded`.** Inspect the code at `TrainController.cs:691-715`: there is no `value.Bardo = null` write. The car is re-positioned on track, KVO is overlaid (`ApplyValues`), but `IsInBardo` remains true unless a separate `CarSetBardo(carId, null)` arrives. **This is a load-bearing gotcha** — see Gotchas section.

### `ReturnFromBardoOrder` → `Car.Descriptor` → `CreateCarIfNeeded`

```csharp
// IndustryContext.AddOrderedCars: when an industry picks up a return-from-bardo order
if (order is ReturnFromBardoOrder returnFromBardoOrder) {
    CarDescriptor descriptor = _trainController.CarForId(returnFromBardoOrder.CarId).Descriptor();
    list.Add(new OrderedCar(descriptor, returnFromBardoOrder.CarId));     // ← carId is preserved
}
// IndustryContext.cs:185
```

```csharp
// Car.Descriptor: snapshot of the live Car for re-spawn
public CarDescriptor Descriptor() {                                       // Car.cs:1033
    return new CarDescriptor(DefinitionInfo, Ident, Bardo, trainCrewId,
                             !FrontIsA,
                             new Dictionary<string, Value>(KeyValueObject.Dictionary));
}
```

`Car.Descriptor()` snapshots the entire current KVO into the descriptor's `Properties`. So when the existing-car branch in `CreateCarIfNeeded` runs `KeyValueObject.ApplyValues(descriptor.Properties)`, it's writing the same KVO back over itself — **no-op for any key that hasn't changed since the snapshot**. The `Bardo` field is on the descriptor but **`CreateCarIfNeeded` does not consult `descriptor.Bardo`** — only `descriptor.Properties` and `descriptor.TrainCrewId`.

### `KeyValueObject.ApplyValues` vs `ResetData` (the load-bearing distinction)

```csharp
// Game.Messages/PropertyValueConverter.cs:94
public static void ApplyValues(this IKeyValueObject obj, Dictionary<string, Value> values) {
    foreach (var (key, value) in values)
        obj[key] = value;                                                 // ← simple overwrite, no clear
}

// KeyValue.Runtime/KeyValueStorage.cs:70
public void ResetData(IReadOnlyDictionary<string, Value> values, SetValueOrigin origin) {
    _data.Clear();                                                        // ← full wipe
    foreach (var (key, value) in values)
        Set(key, value, origin);
}
```

| Path | Method | Behaviour |
|---|---|---|
| First spawn (`CreateCarRaw` after `Setup`) | `Car.ResetKeyValueProperties` → `KeyValueObject.ResetData` | KVO is wiped clean and re-seeded from `descriptor.Properties` |
| Re-spawn (existing-car branch in `CreateCarIfNeeded`) | `KeyValueObject.ApplyValues(descriptor.Properties)` | Existing keys are overwritten by descriptor values; **keys present on the live KVO but absent from the descriptor are preserved** |

**Why this matters for mods:** if your mod sets a custom KVO key in `Car.Setup` (or any post-spawn one-shot init) and that key isn't in `descriptor.Properties`, `ApplyValues` won't disturb it on re-spawn from Bardo. *But* if you seeded the key in `Setup` only on the *initial spawn path*, and Bardo re-spawn doesn't call `Setup` again (it doesn't — the Car MonoBehaviour was never destroyed!), your init was preserved across the entire round-trip including the move-to-Bardo. The Car GameObject persists from Bardo move → Bardo return; nothing destroys it.

**Where the bug actually bites:** mod state stored in C# fields on `Car` subclass / on a sibling MonoBehaviour added by `Setup`. These fields **also persist across Bardo** (the GameObject isn't destroyed). The hazard is the *first-time load from save* path: a save snapshot triggers `HandleSnapshotCars` → `RemoveCars(_carLookup.Keys.ToList())` → `Object.Destroy(car.gameObject)` for **every** car (Bardo or not), then re-spawn via `AddCarInternal` → `CreateCarIfNeeded` → `CreateCarRaw` → fresh `Setup`. So mod fields seeded in `Setup` are valid on snapshot-restored Bardo cars. The ApplyValues path (re-place from Bardo) only hits when a Bardo car *that's been alive in the current session* gets ordered back by an industry. In that case the GameObject was never destroyed, `Setup` was never re-called, and any mod state in C# fields is still intact.

The dangerous case: a mod that **clears** state when `IsInBardo` becomes true (e.g., wear mod resets a counter on Bardo entry). On Bardo *exit* via the existing-car branch, that mod state has been cleared and won't be re-initialized because `Setup` is not re-run. The mod must subscribe to the Bardo-exit signal explicitly. There is no Bardo-exit Messenger event — patch `CreateCarIfNeeded` (Harmony postfix) and check `value.IsInBardo` against pre-call state.

### `IsInBardo` clear-out

The only places that *clear* `Car.Bardo` (set to null/empty):
- `HandleSetBardo` when `bardo` argument is null/empty — but the implementation just falls through to the rest of the body, which still calls `WillRemoveCar(isMovingToBardo:true)` and `SetVisible(false)`. **Sending `CarSetBardo(carId, null)` to a non-Bardo car would erroneously remove it from culling.** The `if (car.IsInBardo)` early-return at `:2365` only triggers when `car.IsInBardo`, so a non-Bardo car with `bardo == ""` would proceed through the full Bardo-move flow and end up in Bardo. **There is no path in vanilla that clears a Bardo car's `Bardo` field.** Compare: the only post-Bardo action is *re-spawn via PlaceTrain*, which doesn't touch `Bardo`. Visual restoration (model load + collider re-enable) presumably must be done by mod code or by `SetVisible(true)` somewhere — but searching, **no `SetVisible(true)` is called on Bardo-return**. The car re-appears because:
  1. `_carCuller.Add(car)` in `CreateCarIfNeeded:712` is skipped on the existing-car branch (returns early at line 703).
  2. Yet the car is now positioned on track and visually expected.

This is the **loudest open question** — see Gotchas. There may be downstream code I missed, or it may be a vanilla bug that the dispatch/AI rarely touches. Worth verifying empirically before relying on the round-trip.

### Bardo cars and floating origin

Bardo cars have **no live `Transform`** — actually, the Car GameObject still has a `Transform`, but no body model is loaded under it (the body is lazy via `Car.LoadModelsAsync` which is gated by the culler, and Bardo cars are removed from the culler). So:
- The `Car` root `Transform` has whatever position it was last left at — irrelevant because there's nothing to render.
- `TrainController.WorldDidMove(WorldDidMoveEvent)` (`TrainController.cs:479`) iterates `Cars` (which **includes Bardo cars**) and calls `Car.WorldDidMove(offset)` on each. `Car.WorldDidMove` at `Car.cs:2841` calls `_mover.WorldDidMove(offset)` — which the Bardo car still has, but with no physics body to shift, this is a no-op or noisy log. Worth instrumenting if patching.
- More importantly, `Car.GetCenterPosition(graph)` resolves position from `Track.Location` (game space, never shifted), so Bardo cars' "where am I" is completely floating-origin-stable.

This is what "Bardo is transparent to floating origin" means: the entity's *logical* position is in track space (immune to origin shifts), and its *visual* presence is null (nothing to shift).

### Bardo cars in save / load

`Car.Snapshot()` (`Car.cs:2546`) writes the `Bardo` string into the snapshot record (Snapshot.Car key 9). On load, `HandleSnapshotCars` (`TrainController.cs:1553`) calls `RemoveCars(_carLookup.Keys.ToList())` which destroys every car GameObject, then re-spawns via `AddCarInternal` → `CreateCarIfNeeded` (no existing car in lookup) → `CreateCarRaw`. The `descriptor.Bardo` is set, and `Car.Setup` consumes it (no KVO involvement — direct field assignment). After Setup, `if (!car.IsInBardo) _carCuller.Add(car)` skips Bardo cars — so they're spawned but unculled, invisible, unsegmented, exactly as designed.

KVO for Bardo cars **is** persisted (the `snapshotProperties` dict is per-carId regardless of Bardo status). On re-spawn, `ResetKeyValueProperties` rebuilds the full KVO from snapshot.

### Patch candidates (Bardo)

| Hook | Why |
|---|---|
| `TrainController.MoveToBardo(string, string)` | Single host-side entry. Prefix to veto, postfix to log/event. **Note**: only runs on host. Do not assume clients see this; they only see `HandleSetBardo` after the broadcast. |
| `TrainController.HandleSetBardo(string, string)` | Both-sides handler. Best place to listen for "this car just went into Bardo" or "this car just came out." Postfix and inspect `car.IsInBardo`. |
| `TrainController.CreateCarIfNeeded` (existing-car branch) | The Bardo-return chokepoint. Patch to detect "this car was Bardo, now it's not (even though `Bardo` field still holds the old value)" — or to fix the missing `_carCuller.Add` if needed. |
| `Car.SetVisible(bool)` | Per-car renderer toggle. Useful patch to log Bardo enter/exit visually. |
| `Car.WillDestroy(bool isMovingToBardo)` | The `bool` distinguishes normal removal from Bardo move. Postfix to fan out per-mod cleanup. |
| `OpsController.TryDecodeBardo(string)` | Resolve a Bardo identifier to its industry. Useful for mods that present "where did this car go" UI. |
| `Interchange.OrderReturnFromBardo(string carId)` | The interchange-side trigger to bring a car back. Patch to add custom return logic (e.g., timed return without industry context). |

### MP authority (Bardo)

| Operation | Auth | Site |
|---|---|---|
| `MoveToBardo` | **Host only** (StateManager.AssertIsHost) | `TrainController.cs:2349` |
| `CarSetBardo` message dispatch | **HostOnly** attribute on the message | `Game.Messages/CarSetBardo.cs:6` |
| `HandleSetBardo` runs on both host and clients | implicit — `ApplyLocal` runs the handler everywhere | `StateManager.ApplyLocal` |

There is no client-side request to move a car to Bardo. Industries (which run host-side) call `IndustryContext.MoveToBardo(IOpsCar)` → `TrainController.MoveToBardo(carId, industryId)` → host-only chain. Clients are passive observers.

### Gotchas (Bardo)

- **`HandleSetBardo` doesn't validate `bardo != ""` early.** Sending `CarSetBardo(carId, "")` on a non-Bardo car would proceed through the full Bardo-move flow (`WillRemoveCar`, `SetVisible(false)`, `Bardo = ""`) and the car would end up Bardo'd-with-empty-id (`IsInBardo` false because `string.IsNullOrEmpty("")` is true). Net result: car is invisible, removed from culler/spatial-hash, but `IsInBardo` reports false. Confusing. Don't send empty-string Bardo IDs.
- **No `Car.Bardo = null` clear path in vanilla.** Bardo-return via `ReturnFromBardoOrder` re-positions the car on track but never clears the `Bardo` string. Either there's downstream code I missed (look at `PlaceTrain`'s subroutines and `Car.PositionWheelBoundsFront`), or `IsInBardo` remains true after return — meaning the car is on track but `OpsController.CarsInArea` would still skip it. **Verify empirically.**
- **No `_carCuller.Add` on Bardo-return existing-car branch.** `CreateCarIfNeeded:691-704` returns early before reaching `_carCuller.Add`. If the car is supposed to become visible after re-placement, something else must add it back to the culler. Suspect: a model-load path triggered by `CarDidPosition`, but `CarDidPosition` is gated by `!car.IsInBardo` (`TrainController.cs:763`), so as long as Bardo isn't cleared, the spatial-hash and culler updates are skipped. **Likely vanilla bug or undocumented invariant — investigate before building mods that round-trip cars through Bardo.**
- **`Car.Bardo` is a plain auto-property with no observers.** No KVO, no Messenger event fires when it changes. To detect Bardo enter/exit, patch `HandleSetBardo` (postfix).
- **Bardo cars stay in `Cars` and `_carLookup`.** `TrainController.WorldDidMove` iterates them; `Snapshot` includes them; `OpsController.AreaForCarPosition` would NPE on a Bardo car's `LocationA` if not gated. Inspect every consumer of `Cars`/`CarForId` for `IsInBardo` checks.
- **`IIndustryContext.MoveToBardo` uses `_industryComponent.Identifier` as the Bardo ID** (`IndustryContext.cs:328`). Mods adding new industries get a unique identifier automatically. Multiple industries can hold cars at "the same" Bardo if they share an Identifier (don't).
- **Move-to-Bardo while car is in an `IntegrationSet`** properly severs the consist (`SetAdjacentCarsNotConnected` + `_integrationSets.RemoveCar`). The remaining cars in the consist continue as a smaller set.
- **`Car.WillDestroy(isMovingToBardo:true)`** still calls `UnloadModels()` and `StateManager.UnregisterPropertyObject`. The unregister is **wrong** for Bardo cars whose KVO must remain queryable — but check `WillDestroy` source: it currently does call `StateManager.UnregisterPropertyObject(id)` even for Bardo. KVO writes after Bardo move would silently fail. **`HandleSetBardo` may be eating this** — if so, KVO reads from a Bardo car return cached values and writes are no-ops until re-registered. Worth tracing.

---

## Save / load interaction with tiles

Tiles and saves are **fully decoupled.**

- Save format: `WorldStore`/`SaveFile` includes `Snapshot.Car`, `Snapshot.CarSet`, KVO snapshots, ledger, ops state. **No tile state.**
- `MapStore` reads/writes `tile_xxx_yyy.data` PNG files directly to `StreamingAssets/Maps/<dir>/`, independent of game saves.
- On `MapWillLoad` → `MapDidLoad`, `MapManager` re-creates from disk (the catalogue is fixed; modifications via `AddModifier` are not persisted across sessions unless mod-side code re-applies them).
- A save loaded into a different scene-with-different-map will produce broken `Track.Location` references first, never reaching the tile layer.
- **Mods that author tile data must persist their state in their own files** — vanilla has no "modifier list" save format. The `_heightmapModifiers` / `_maskModifiers` / `_tunnelModifiers` storages are runtime-only and cleared on `RebuildAll`.

`_terrainBuilderSettings` (tree/detail density) is per-machine, set by `MapCameraUpdater.SetTerrainDensityValues` from `Preferences`. Not synced across MP, not saved with the world.

---

## MP behaviour — per-machine, no sync

**Tile loading is 100% local.** As confirmed via grep: `MapManager` references no `StateManager`, no `Multiplayer`, no `Network.Client`, has no `IsHost` checks, no `[HostOnlyAuthorizationRule]` attributes. The host's tile state is invisible to clients and vice versa.

| Concern | MP behaviour |
|---|---|
| Camera-driven streaming | Per-client, polled at 1 Hz from local `Camera.main` |
| `RequestPriorityLoad` (jump) | Per-client, on local jump |
| `KeepLoaded` pin | Per-client, set by local `PlayerController` |
| Tile invalidation via `AddModifier` | Per-client (modifiers are local). **Mods that add modifiers on the host must also add them on every client.** |
| `MapDidLoadEvent` / Map* events | Per-client, fired by local scene load |
| Floating-origin offset (`_gameToWorldOffset` in `MapManager`) | Per-client, mirrors the local `WorldTransformer._currentOffset` |
| Bardo state (`Car.Bardo`) | **Synced** via `CarSetBardo` HostOnly message; clients see Bardo state via `HandleSetBardo` |
| Tile catalogue (`Map.json`) | Identical across machines (asset on disk, distributed with the game/map) |

### MP divergence during streaming

A client whose tile-load coroutine has stalled (slow disk, low budget, missing `tile_xxx_yyy.data` file) will see:
- **No terrain** at the affected coords — but **the cars and tracks at those coords still exist** (track topology is fully loaded with the scene; cars stream independently). Depending on view angle, you'll see floating cars and rails.
- **No collision** — `Layers.Terrain` raycasts (e.g., `PlayerController.CheckForTerrainBelow`) miss. The player avatar may end up inside the not-yet-built terrain mesh once it appears. Vanilla's recovery: jump player to a known-safe position.
- **Modifiers don't replicate.** If a mod adds a height cut on the host, the client doesn't see it. Visual divergence between host and client is permanent unless the mod sends modifier registration over MP. **No vanilla mechanism exists for this.**

**Mods that author tile-modifying behaviour must either**: (a) have all clients run identical modifier-generation logic deterministically, or (b) define their own MP message to broadcast modifier additions/removals.

---

## Patch points: cross-cutting

### "I want a callback when a tile loads"

Patch `MapManager.BuildTerrain` postfix or `RequestTerrain` postfix. There is no Messenger event for per-tile load. The `MapTerrain.buildStatus` field is the only public signal — poll it, or patch `BuildTerrain` to set a sentinel.

### "I want a callback when a tile unloads"

Patch `MapManager.RecycleTerrain` (prefix runs before `Object.Destroy`). Or hook `MapTerrain.OnDestroy`. There is no Messenger event.

### "I want to load arbitrary tiles outside the camera view"

Three options:
1. Set `MapManager.Instance.KeepLoaded = tilePos` — pins one tile only.
2. Call `MapManager.Instance.RequestPriorityLoad(gamePoint, distance)` and `yield return` — blocks the calling coroutine but can request multiple tiles via `distance`.
3. Patch `MapManager.NearbyTiles` to inject mod tiles into the visible set on every poll.

### "I want to add custom map data per tile"

Vanilla has no per-tile mod-extension dictionary. Options:
1. Pack into `TileData`'s unused B channel (currently zeroed). Patch `TileData.Save`/`PopulateHeightmap` to read/write it.
2. Mod-side `Dictionary<Vector2Int, TMyData>` keyed by tile position; subscribe to `MapDidLoadEvent` to load and `MapWillUnloadEvent` to save.
3. Custom `IMapModifier` impl that carries metadata and gets registered via `MapManager.AddModifier`.

### "I want to keep a Bardo car in Bardo across save/load with extra state"

Bardo cars round-trip through save fine if you persist your state via KVO keys (subject to the per-key auth policy). Plain C# fields on the `Car` MonoBehaviour are wiped (because `HandleSnapshotCars` destroys every Car GameObject and re-creates it via `Setup`). KVO keys are restored from `snapshotProperties` before any consumer sees the car. So: **always store mod state via KVO** for save-load round-trip.

### "I want to avoid the Bardo-return KVO clobber"

The existing-car branch does `value.KeyValueObject.ApplyValues(descriptor.Properties)` which overwrites every key in the descriptor onto the live KVO. If you've added KVO keys post-spawn that you don't want a Bardo round-trip to overwrite, ensure they're added *after* the descriptor was snapshotted (i.e., not present in the in-memory live KVO at the moment of `Car.Descriptor()`). Practically impossible to guarantee since `Car.Descriptor` snapshots the full live KVO. **Safer approach**: detect Bardo-return via patch on `CreateCarIfNeeded` and re-apply your mod-side defaults postfix.

### "I want to listen for tile-loading errors"

Patch `MapManager.BuildTerrain` and inspect the `task.IsCompletedSuccessfully` branch (`MapManager.cs:811`). Or patch `Log.Error` invocations. There's no built-in error event.

### "I want to abort a `RequestPriorityLoad` early"

Set `MapManager._overrideTileLoads.Clear()` reflectively, or patch `RequestPriorityLoad` to add a cancel flag. The vanilla coroutine has no abort path — it spins forever waiting for `BuildStatus.Ready`.

---

## Race conditions and async pitfalls

- **`MapManager._loadTilesTask` is a single coroutine.** Only one tile-build slice runs at a time. `WorkLoadUnloadQueues` yields `null` after each slice (or until time-budget expires). Loading 64 tiles at once on first scene load is sequential, ~5–20ms each, can take a full second.
- **`RequestPriorityLoad` is non-reentrant.** A second call while the first is still waiting silently no-ops. Chain via `yield return` or check `_overrideTileLoads.Count` first.
- **Tile build can fail and never resolve to `Ready`.** `RequestPriorityLoad`'s wait loop spins forever in this case. Consider patching the wait with a timeout if your mod calls priority loads in player-facing flows.
- **`PrepareTerrain` steal-furthest can yank a tile out from under in-flight references.** If your mod holds a `MapTerrain` ref across a frame boundary, it may be Destroy'd by the steal path. Always re-query via `MapManager.TryGetTerrain(tilePos, out var t)`.
- **`TileData.Heightmap` is `Allocator.Persistent`.** Disposed in `TileData.Dispose()` (called by `MapStore.UnloadTile`). After disposal, accessing the `NativeArray` throws `InvalidOperationException` or returns garbage. Hold tile positions, not tile data.
- **`InvalidatePending` runs on a delay** (`_delayedInvalidateCoroutine`, default 0.5s). Multiple modifier registrations within a single frame are coalesced. If you need synchronous invalidation, call the public `MapManager.Invalidate(Vector2Int)` directly.
- **`SetVisibleTilesQueued` mutates `_pendingTileUnloads` while iterating in a separate code path.** The dictionary access is single-threaded (Unity main thread coroutine) so no races, but the *order of operations* matters: the camera-tick path adds tiles to unload, and `WorkUnloadQueue` removes them after the grace period. A camera that briefly passes a tile (1s polling means a fast-moving train can leave-and-return in one tick) can flip-flop the unload state.
- **`MapCameraUpdater` swallows exceptions** (`MapCameraUpdater.cs:50-53`). If `UpdateVisibleTilesForPosition` throws (e.g., from a mod patch), it's logged and the next tick proceeds normally. No crash, but easy to miss in development.
- **`MapStore.RebuildTile` (Mapbox fetch) is editor-only in practice.** `RequestTile` re-throws in play mode if the local data is missing. Mods that ship with a partial tile catalogue will hard-error in production.

---

## Cross-references

- Floating-origin offset and `MapManager.ApplyWorldToGameOffset` — see [Floating Origin › MapManager interaction](floating-origin.md#mapmanager-interaction--the-unloaded-tile--bardo-region).
- Game-space vs world-space conversion at the tile API boundary — see [Floating Origin › Where conversions happen](floating-origin.md#where-conversions-happen-the-boundary-list).
- Car spawn / despawn / Bardo lifecycle including `HandleSnapshotCars` — see [Cars & Cargo › lifecycle spine](cars-cargo.md#lifecycle-spine) and [Bardo path](cars-cargo.md#lifecycle-spine).
- `Car.Setup` vs `KeyValueObject.ApplyValues` for re-place from Bardo — see [Cars & Cargo › Gotchas](cars-cargo.md#gotchas-car).
- `Track.Location`-derived car positions are immune to floating origin and tile state — see [Track Topology](track-topology.md).
- `MapDidLoadEvent` and other Messenger events — see [Events Catalog](events-catalog.md).
- Save/load wipes-and-rebuilds every Car (Bardo or not) — see [Save/Load](save-load.md).
- Tile loading ignores all access control — there's no concept of "this tile is host-only" — see [Access Control](access-control.md) for what auth *does* gate.
