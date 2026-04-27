# Rendering Pipeline — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Floating Origin](floating-origin.md), [Time & Weather](time-weather.md), [Asset Packs](asset-packs.md), [Player & Camera](player-camera.md)

Railroader runs Unity URP. The rendering pipeline modders touch is split across four lightly-coupled pieces: a shared **Unity `CullingGroup`-based culler** (`Helpers.Culling.CullingManager`) wired to six named domain instances (Hose / Bridge / CTC / Signal / Scenery / Flare); a per-consist **`CarCuller`** that drives car LOD swap *and* per-car async model load/unload via `Car.ModelLoadRetain`; **GPU-instanced ties and tieplates** via the third-party GPUInstancer asset wrapped in `Track.PrefabInstancer`; and **URP `DecalProjector`-based decals** managed by `Effects.Decals.DecalCullingManager` (a Burst-jobified frustum/screen-size culler that maintains its own `decalBudget = 200` ceiling). Everything that needs to survive a floating-origin shift either subscribes to `WorldDidMoveEvent` or to the lower-overhead `WorldTransformer.OnDidMove` C# event. Weather, sky, fog, sun, reflections — all are delegated to the third-party **Enviro 3** asset (`EnviroManager.instance`); first-party code only feeds it game-time and reads back `EnviroManager.instance.Environment.Settings.{snow,wetness}` as global shader floats for Microsplat terrain. There is **no first-party LOD curve, no first-party shadow controller, no first-party post-processing volume system beyond a single `ColorAdjustments` applicator** — the rest is configured in scene authoring and consumed by URP / Enviro / Microsplat as-is.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Helpers.Culling.CullingManager` | `Helpers.Culling/CullingManager.cs:9` | Pooled `CullingGroup` wrapper; `AddSphere(Transform, radius, ICullingEventHandler)` returns disposable `Token`. Six named instances |
| `Helpers.Culling.CullingManagerInitializer` | `Helpers.Culling/CullingManagerInitializer.cs:8` | Spawns the six named managers + their distance bands at `[DefaultExecutionOrder(-1000)]` |
| `Helpers.Culling.RendererCuller` | `Helpers.Culling/RendererCuller.cs:7` | Drop-in MonoBehaviour: collects child `Renderer[]` and toggles `enabled` per distance band (`cullingManagerName` = Scenery only, `visibleDistanceBand` 0..2) |
| `Helpers.SimpleCuller` | `Helpers/SimpleCuller.cs:5` | Standalone single-sphere culler that toggles `gameObject.SetActive`. Doesn't go through `CullingManager` |
| `RollingStock.CarCuller` | `RollingStock/CarCuller.cs:9` | Per-`TrainController` consist culler. Hard-coded distance bands `[50, 100, 1500, 1750]`. Drives LOD/visibility *and* `ModelLoadRetain`/release |
| `Track.PrefabInstancer` | `Track/PrefabInstancer.cs:13` | `GPUInstancer` wrapper for two prefab kinds: `Tie` and `TiePlate`. Hard caps 65535 instances each |
| `Effects.Decals.DecalCullingManager` | `Effects.Decals/DecalCullingManager.cs:14` | Burst-jobified frustum + screen-size + distance culler; adaptive screen-size threshold targets `decalBudget = 200` visible decals |
| `Effects.Decals.DecalProjectorHelper` | `Effects.Decals/DecalProjectorHelper.cs:14` | Per-decal helper: gates URP `DecalProjector.enabled` by car visibility AND decal cull manager AND text-render completion |
| `Effects.Decals.DepthProjectorHelper` | `Effects.Decals/DepthProjectorHelper.cs:9` | Refreshes `_DecalProjectorOriginY` / `_DecalProjectorDepth` shader uniforms on world-shift |
| `Effects.Decals.CanvasDecalRenderer` | `Effects.Decals/CanvasDecalRenderer.cs:12` | Off-screen camera renders TMP text into an `R8` `RenderTexture`; refcounted texture pool |
| `Effects.EnviroSynchronizer` | `Effects/EnviroSynchronizer.cs:10` | 5 Hz writer of `TimeWeather.Now` → `EnviroManager.Time`; tracks fog height + reflection threshold |
| `Enviro.EnviroMicrosplatIntegration` | `Enviro/EnviroMicrosplatIntegration.cs:7` | Per-frame `Shader.SetGlobalFloat` of `_Global_SnowLevel`, `_Global_WetnessParams`, `_Global_PuddleParams`, `_Global_RainIntensity`, `_Global_StreamMax` |
| `Model.CarShaderHelper` | `Model/CarShaderHelper.cs:5` | `ReplaceShaders(obj)` swaps `Shader (Shared)` → `Shader (Builtin)` post-load; injects wear-noise textures |
| `Game.Settings.CameraSettingsApplicator` | `Game.Settings/CameraSettingsApplicator.cs:7` | Wires `Camera.farClipPlane` to `Preferences.GraphicsDrawDistance` (default 1500m) |
| `Game.Settings.PostProcessingSettingsApplicator` | `Game.Settings/PostProcessingSettingsApplicator.cs:10` | URP `Volume` → `ColorAdjustments` → exposure/contrast |
| `Game.Preferences.GraphicsDrawDistance` etc. | `Game/Preferences.cs:308` | `gfx.drawdistance` (1500), `gfx.particlelevel` (2), `gfx.tree.density` (1), `gfx.detail.density` (1), `gfx.canvas.scale` (1), `gfx.night-light-level` (0.3), `gfx.post-exp` (0.5), `gfx.contrast` (25), `gfx.msaa`, `gfx.vsync`, `gfx.fps.limit` |

---

## Culling spine

```
Scene root
   │
   ├─ CullingManagerInitializer  [DefaultExecutionOrder(-1000), ExecuteInEditMode]
   │      OnEnable spawns six children:
   │         Hose      bands: [25]                            ← brake hoses
   │         Bridge    bands: [1000]                          ← AutoTrestle
   │         CTC       bands: [25]                            ← CTC dispatcher panel renderers/canvases
   │         Signal    bands: [1000]                          ← wayside signal heads
   │         Scenery   bands: [100, 1000, 1500]               ← SceneryAssetInstance, RiverBuilder, RendererCuller, Chuff, DetailModelController
   │         Flare     bands: [25, 1000]                      ← Effects.Flare (lit + visual fx + mesh)
   │
   ├─ Each CullingManager (one MonoBehaviour each)
   │      OnEnable:
   │        - new CullingGroup
   │        - AutoAssignTargetCamera (Camera.main)
   │        - WorldTransformer.OnDidMove += OnWorldDidMove   ← C# event, NOT Messenger
   │      Update:
   │        - drains _needsUpdate set, recomputes distance band per token, dispatches CullingSphereStateChanged
   │      FixedUpdate:
   │        - for tokens with RegisterFixedUpdate(transform), copies transform.position → sphere
   │      OnWorldDidMove(offset):
   │        - foreach token: token.Handler.RequestUpdateCullingPosition()  ← requestor pattern, NOT auto-translate
   │
   ├─ ICullingEventHandler implementations (15+)
   │      AutoTrestle, CurveMeshBuilderBase, RendererCuller, SceneryAssetInstance,
   │      RiverBuilder, Flare, Chuff, DetailModelController, Hose,
   │      CTCSignalCuller, CTCPanelCuller
   │
   └─ Separate cullers (don't use CullingManager)
          SimpleCuller         single-sphere SetActive on/off, also subscribes to OnDidMove
          CarCuller            owns its own CullingGroup; iterates _records on WorldDidMove (Messenger)
          DecalCullingManager  Burst job; reads _mainCamera.transform; DOES use MainCameraHelper
          TelegraphPoleManager owns its own CullingGroup; WorldTransformer.OnDidMove translates spheres directly
```

**Two distinct shift-handling patterns:**

1. **Pull (`CullingManager`, `CarCuller`, `SimpleCuller`)** — re-read each owner's authoritative position via `RequestUpdateCullingPosition`. Used when the owner already has a Transform that gets moved by other code (e.g., a parent target shifted by `WorldTransformerTargetList`).
2. **Push (`TelegraphPoleManager`)** — iterate `_spheres[i].position += offset` in place. Used when the spheres are derived from a separately-managed graph and there is no Transform per sphere.

`PrefabInstancer` uses a hybrid: `GPUInstancerAPI.SetGlobalPositionOffset(prefabManager, offset)` + walks every `Matrix4x4` in `_entries[].Matrixes` and translates it via `Matrix4x4.Translate(offset) * matrix`. The duplicate work suggests defense in depth — neither alone is sufficient.

---

## `Helpers.Culling.CullingManager` (the shared wrapper)

`MonoBehaviour`, `[ExecuteInEditMode]`. One instance per "domain" (Hose / Bridge / etc.); each owns one `UnityEngine.CullingGroup`.

### Token model

```csharp
public class Token : IDisposable {
    public ICullingEventHandler Handler;
    public readonly CullingManager Manager;
    public readonly int Index;
    public void Dispose() { Manager.Remove(this); }
    public void UpdatePosition(Transform transform, float? radius = null);
    public void UpdatePosition(Vector3 worldPosition, float? radius = null);
    public void RegisterFixedUpdate(Transform transform);    // auto-pull pos in FixedUpdate
}
public interface ICullingEventHandler {
    void CullingSphereStateChanged(bool isVisible, int distanceBand);
    void RequestUpdateCullingPosition();
}
```

### State

```csharp
private CullingGroup _cullingGroup;
private BoundingSphere[] _spheres;        // initial 32, grows ×1.5 on demand
private List<Token> _tokens;              // parallel to _spheres
private float[] _distances;               // bands (set via Configure)
private int _nextSphere;                  // free-list cursor
private int _sphereCount;                 // CullingGroup.SetBoundingSphereCount value
private readonly HashSet<Token> _needsUpdate;     // drained in Update
private readonly Dictionary<Token, HashSet<Transform>> _fixedUpdateTransforms;  // for RegisterFixedUpdate

public static CullingManager Hose, Bridge, CTC, Signal, Scenery, Flare;        // forward to Initializer.Shared
```

### Add / Remove

```csharp
public Token AddSphere(Transform t, float radius, ICullingEventHandler h);
public Token AddSphere(Vector3 worldPos, float radius, ICullingEventHandler h);   // 170
private void Remove(Token token);                                                  // 215
```

`AddSphere` uses a free-list — `_nextSphere` walks for the next null slot, growing the underlying arrays by 1.5× when full. **`Remove` resets `_nextSphere` to the reused slot** so subsequent adds backfill before growing.

### Update path

```csharp
private void Update() {
    foreach (Token t in _needsUpdate) {
        int band = _cullingGroup.CalculateDistanceBand(_spheres[t.Index].position, _distances);
        bool visible = _cullingGroup.IsVisible(t.Index);
        t.Handler.CullingSphereStateChanged(visible, band);
    }
    _needsUpdate.Clear();
}

private void CullingGroupStateChanged(CullingGroupEvent evt) {       // Unity callback
    _tokens[evt.index]?.Handler.CullingSphereStateChanged(evt.isVisible, evt.currentDistance);
}

private void OnWorldDidMove(Vector3 offset) {                         // C# event, not Messenger
    foreach (Token t in _tokens) t?.Handler.RequestUpdateCullingPosition();
}
```

**Three sources** push events to handlers:

1. Unity-driven `CullingGroup.onStateChanged` — when visibility OR distance band changes.
2. `_needsUpdate` drain in `Update` — synthetic state recomputation for newly-added tokens (`AddSphere` adds to `_needsUpdate`).
3. `OnWorldDidMove` — origin shift: every handler is asked to repush its position.

### Distance bands per domain

| Domain | Bands (m) | Consumers |
|---|---|---|
| `Hose` | `[25]` | `RollingStock.Hose` (Verlet rope sim only when in band 0) |
| `Bridge` | `[1000]` | `AutoTrestle.AutoTrestle` (procedural trestle generator) |
| `CTC` | `[25]` | `Track.Signals.Panel.CTCPanelCuller` (renderers + canvases) |
| `Signal` | `[1000]` | `Track.Signals.CTCSignalCuller` (signal head models) |
| `Scenery` | `[100, 1000, 1500]` | `SceneryAssetInstance` (load < 1500, render < 1000), `RendererCuller`, `RiverBuilder`, `Chuff`, `DetailModelController` |
| `Flare` | `[25, 1000]` | `Effects.Flare` (light < 25, mesh < 1000) |

**The band-count is hard-coded per domain at scene start (`CullingManagerInitializer.OnEnable`). Modders cannot extend bands at runtime.** Adding a new domain requires patching `CullingManagerInitializer` (or constructing a parallel `CullingManager` GameObject yourself).

### Adaptive band-aware behaviour

Each consumer reads `distanceBand` differently. From the catalog:

```csharp
// RendererCuller: band <= visibleDistanceBand → enable Renderer[]
// SceneryAssetInstance: band <= 2 → load model; band < 2 → enable renderers
// DetailModelController: band < 1 → enable renderers
// Flare: band <= 0 → light + VFX; band <= 1 → mesh + fusee
// Hose: band 0 only → run Verlet rope sim
// CTCSignalCuller: band < 1 → enable signal heads
// CTCPanelCuller: band < 1 → enable panel renderers + canvases
// AutoTrestle: band < 1 → kick off generation coroutine
// Chuff: pulled in via RegisterFixedUpdate; band-aware via SetCullerDistanceBand on parent Car
```

The pattern is **the culler owns the bands; the consumer interprets them**. There is no "LOD level" enum.

### Patch candidates

| Method | Why patch |
|---|---|
| `CullingManagerInitializer.OnEnable` | Add a new domain (e.g., "Mod"). Currently the only way to introduce per-mod bands. |
| `CullingManager.AddSphere(Transform,...)` | Hook to register all spheres — useful for diagnostics or to wrap with mod-specific filtering. |
| `CullingManager.OnWorldDidMove` | Re-implement the per-token notify (e.g., batch via `JobSystem`). Hot path on every origin shift. |
| `CullingManagerInitializer.HoseDistanceBands` etc. (private static `float[]`) | Replace via reflection if you need different distance bands. The arrays are read once at `OnEnable`. |

### Gotchas

- **`_cullingGroup.AutoAssignTargetCamera(this)` reads `Camera.main` once at `OnEnable`.** If `Camera.main` changes (camera mode swap), there's no resync. `CullingGroup.targetCamera` becomes stale. `SimpleCuller` patches around this with a per-`Update` re-read; `CullingManager` does not. (`CameraSelector` keeps `Camera.main` stable across mode switches by toggling `enabled` rather than swapping objects, so this rarely bites.)
- **`Token.Handler` becomes null after `Remove`** — the manager nulls it in `Remove` (line 225). Defensive null check exists in `Update`'s `_needsUpdate` drain but `CullingGroupStateChanged` uses `?.` directly. Don't keep stale `Token` references.
- **Initial sphere capacity is 32; grows ×1.5.** `Scenery` will resize repeatedly during initial map load (potentially hundreds of scenery instances). One-time GC churn at scene start.
- **`[ExecuteInEditMode]`** — `OnEnable`/`OnDisable` fire in editor. The `WorldTransformer.TryGetShared` call is null-safe but the `CullingGroup` is constructed both in editor and play modes.
- **There is no per-token override of distance bands.** All tokens in a `CullingManager` share the same `float[] _distances`. To get a different curve you need a different `CullingManager` instance, which means patching the initializer.

---

## `Helpers.SimpleCuller` (standalone single-sphere)

Drop-in component that toggles `gameObject.SetActive(visible)` based on a single distance threshold. **Bypasses `CullingManager`** entirely — owns its own `CullingGroup`. Used for one-off scenery items where shared pooling isn't worth the registration overhead.

```csharp
public float radius   = 10f;       // sphere radius
public float distance = 500f;      // single distance band
```

- `Camera.main` re-resolved each `Update` (line 41) — robust to camera swaps.
- `WorldTransformer.OnDidMove` shifts the single sphere position.
- `transform.hasChanged` polled each `Update` to keep the sphere in sync with manual position changes.
- `OnDrawGizmosSelected` shows magenta radius + yellow distance.

**Use when:** A standalone GameObject needs simple visibility toggling and you don't want the `ICullingEventHandler` boilerplate.

**Don't use when:** You need LOD bands (`CullingManager` only), or when you have many similar items that should share a `CullingGroup` for efficiency (per-instance `CullingGroup` is heavier than pooled).

---

## `RollingStock.CarCuller` (the consist culler)

Lives on `TrainController.gameObject`. Manages every `Car`'s visibility + LOD-related model load lifecycle. **Does not use `CullingManager`** — it owns its own `CullingGroup` because cars need bookkeeping the shared culler can't express (model load tokens, position derived from `Car.GetCenterPosition(graph)` not `transform.position`).

### State

```csharp
private CullingGroup _cullingGroup;
private BoundingSphere[] _spheres;     // initial 64, grows ×1.5
private List<Record> _records;         // parallel
private readonly Dictionary<string, int> _cachedSphereIndexes;   // car.id → sphere index
private readonly float[] _distanceBands = { 50f, 100f, 1500f, 1750f };  // HARD-CODED
private readonly Dictionary<Record, Action> _pending;  // load/unload deferred to Update
```

### Distance band semantics

```csharp
public const int DistanceBandClose       = 0;    // < 50m
public const int DistanceBandNearby      = 1;    // < 100m
public const int DistanceBandLoadModel   = 2;    // < 1500m
public const int DistanceBandNoChange    = 3;    // < 1750m  ← hysteresis dead-zone
public const int DistanceBandUnloadModel = 4;    // ≥ 1750m
```

- `<= DistanceBandLoadModel` (i.e., < 1500m) → schedule **`Action.Load`** (calls `Car.ModelLoadRetain("CarCuller")` next `Update`).
- `>= DistanceBandUnloadModel` (i.e., ≥ 1750m) → schedule **`Action.Unload`** (disposes the load token, which decrements ref and ultimately calls `Car.UnloadModelsDelayed` after `Config.Shared.carModelUnloadDelay = 300s`).
- `DistanceBandNoChange` (1500..1750m) is the **hysteresis dead-zone**. A car oscillating around 1500m doesn't repeatedly load/unload.

### LOD swap

```csharp
private void OnCarCullingGroupStateChanged(CullingGroupEvent sphere) {       // 181
    Record record = _records[sphere.index];
    Car car = record.Car;
    int currentDistance = sphere.currentDistance;
    if (currentDistance > 2)
        if (currentDistance >= 4) _pending[record] = Action.Unload;
    else
        _pending[record] = Action.Load;
    car.SetCullerDistanceBand(sphere.previousDistance, sphere.currentDistance);   // ← LOD callback
    if (sphere.hasBecomeVisible || sphere.hasBecomeInvisible)
        car.SetVisible(sphere.isVisible);
}
```

`Car.SetCullerDistanceBand` (`Car.cs:1590`) translates the band into two booleans:

```csharp
public void SetCullerDistanceBand(int prev, int current) {
    bool isNearby = current <= 1;          // < 100m → IsNearby = true (raises OnIsNearbyDidChange)
    bool enablePosCouplers = current <= 0; // < 50m  → physics-position couplers
    if (enablePosCouplers) { PositionCoupler(LogicalEnd.A); PositionCoupler(LogicalEnd.B); }
}
```

So band 0 (<50m) = **fully simulated**, band 1 (<100m) = **nearby effects active**, band 2 (<1500m) = **model loaded but distant**, bands 3-4 = **unload candidate**.

### Visibility

`Car.SetVisible(bool)` (`Car.cs:1569`) toggles `_bodyRenderers[i].enabled`, recurses into `EndGearF/R.SetVisible` (couplers + anglecocks), `_truckA/B.SetVisible`, then calls `UpdateMaterialsForCondition()` to re-apply the wear shader uniform on the now-visible renderers.

### Position source

```csharp
private Vector3 GetSpherePosition(Car car) =>
    WorldTransformer.GameToWorld(car.GetCenterPosition(trainController.graph));   // 217
```

`Car.GetCenterPosition(graph)` returns **game-space**; conversion to world-space happens here. `WorldDidMove(offset)` (called by `TrainController.WorldDidMove` not Messenger directly):

```csharp
public void WorldDidMove(Vector3 offset) {                                       // 208
    for (int i = 0; i < _records.Count; i++)
        _spheres[i].position = GetSpherePosition(_records[i].Car);               // recompute from car center
}
```

Note: **`offset` parameter is unused** — the function recomputes from authoritative `Car.GetCenterPosition` rather than translating in place. Costlier but correct (cars move continuously; a stale per-shift translate would be wrong by the next physics tick anyway).

### Patch candidates

| Method | Why patch |
|---|---|
| `CarCuller._distanceBands` (private readonly array) | Replace via reflection to change LOD/load/unload thresholds. The array is captured by `CullingGroup.SetBoundingDistances` once in `SetupCarCullingGroup`; assign and re-call. |
| `CarCuller.OnCarCullingGroupStateChanged` | Replace LOD policy entirely. Useful if you want band 2 to also trigger reduced-detail material swaps. |
| `Car.SetCullerDistanceBand` | Hook to drive your own LOD-tied behaviour (e.g., disable expensive subcomponent updates when band > 1). |
| `Car.SetVisible(bool)` | Postfix to enable/disable mod-specific renderers in lockstep with vanilla visibility. |
| `Car.UpdateMaterialsForCondition` | Re-applies `_Wear` uniform on visibility return. Patch to hook custom wear shaders or to add other condition-driven uniforms. |
| `Config.Shared.carModelUnloadDelay` | Currently `300f`. Patch the static field at startup or replace `UnloadModelsDelayed` coroutine. |

### Gotchas

- **The sphere radius is `car.carLength`** (`Add`, line 80). Wide cars (passenger excursions, long flatcar loads) won't have any extra horizontal padding. If your mod adds extra-long projecting cargo, the LOD band may flip in/out as the car rotates.
- **`Update` drains `_pending` once per frame.** Adds and removes within one frame coalesce. If you call `Add(car); Remove(car)` in the same frame, the in-between `_pending` actions never fire.
- **`PostAdd` re-derives the LoadToken from the calculated band.** If your mod calls `Add(car)` and then immediately overrides `_pending`, `PostAdd` will overwrite. Call `Add` then let one frame pass.
- **`car.SetVisible` is also called by `OnCarVisibilityDidChange` consumers** (e.g., `DecalProjectorHelper.OnCarVisibilityDidChange` at `DecalProjectorHelper.cs:90`). If a car is "visible" in `CarCuller` terms but its parent or a Bardo gate has it inactive, `_isVisible` may drift from culler state. The `IsVisible` property is the source of truth (`Car.cs:635`).

---

## `Track.PrefabInstancer` (GPUInstancer wrapper for ties/tieplates)

The single most performance-critical mesh source in the rendering pipeline: every track segment generates ~one tie per 0.55m (`TrackObjectBuilder.TieSpacing`) and one tieplate per rail per tie. The GPUInstancer asset (third-party) batches all these meshes into a few `Graphics.DrawMeshInstancedIndirect` calls.

### The wrapped surface

```csharp
public enum Prefab { Tie, TiePlate }    // hard-coded, only two

[SerializeField] private GPUInstancerPrefabManager prefabManager;     // third-party scene singleton
[SerializeField] private GPUInstancerPrefab[] prefabs;                // index by Prefab enum

private InstanceInfo[] _entries;        // one per Prefab; each has:
                                        //   Matrix4x4[] Matrixes (length 65535, fixed)
                                        //   int Count (currently used)
                                        //   List<Token> Tokens
private readonly Dictionary<Prefab, List<Pending>> _pendingAdd;
private readonly Dictionary<Prefab, Queue<Token>> _pendingRemove;
```

Per-prefab matrix capacity is **65535**. If track exceeds this (giant maps, many sidings), you'll see this log line:

```
PendingAdd queue for {prefab} has {queueAddCount} entries totalling {matrixCount} instances; prefab has {entryCount} entries
```

…and instances will never spawn. The only mitigation is to bump the cap (patch `OnEnable` to use a larger array, or the constants on lines 87-88).

### Add / Release

```csharp
public object AddInstances(Prefab prefab, Matrix4x4[] array);   // 109 — returns Token (typed object)
public void Release(object tokenObject);                        // 126
```

Both calls *defer* — the actual GPUInstancer state mutations happen in `UpdateCoroutine` at 0.1s intervals (line 152). This is intentional: each `TryAddInstances` calls `GPUInstancerAPI.UpdateVisibilityBufferWithMatrix4x4Array` which uploads to the GPU. Batching prevents thrash.

### Floating-origin handling

```csharp
private void WorldDidMove(WorldDidMoveEvent evt) {                              // 192
    Vector3 offset = evt.Offset;
    GPUInstancerAPI.SetGlobalPositionOffset(prefabManager, offset);             // tells GPUInstancer
    Matrix4x4 m = Matrix4x4.Translate(offset);
    foreach (InstanceInfo e in _entries)
        for (int j = 0; j < e.Count; j++)
            e.Matrixes[j] = m * e.Matrixes[j];                                  // also walks every matrix
}
```

**Both** the GPUInstancer-side global offset *and* an explicit per-matrix translate. The third-party plugin's `SetGlobalPositionOffset` handles rendering; the per-matrix walk keeps `_entries[i].Matrixes` authoritative for future `UpdateVisibilityBufferWithMatrix4x4Array` calls (which would otherwise upload pre-shift coordinates).

This subscribes to `WorldDidMoveEvent` via Messenger (line 81), **not** the `OnDidMove` C# event used by `CullingManager`/`SimpleCuller`/`TelegraphPoleManager`. No documented reason for the split.

### Caller pattern: `TrackObjectBuilder.CreateInstancedMeshDrawer`

```csharp
private void CreateInstancedMeshDrawer(Matrix4x4[] transforms, Vector3 offset, PrefabInstancer.Prefab prefab, GameObject parent) {
    Matrix4x4 m = Matrix4x4.Translate(WorldTransformer.GameToWorld(offset));     // game→world before batching
    for (int i = 0; i < transforms.Length; i++)
        transforms[i] = m * transforms[i];
    object token = _prefabInstancer.AddInstances(prefab, transforms);
    if (token != null)
        parent.AddComponent<PrefabInstanceReleaseOnDestroy>().Configure(_prefabInstancer, token);
}
```

`PrefabInstanceReleaseOnDestroy` (`Track/PrefabInstanceReleaseOnDestroy.cs`) is the lifetime hook: when the parent (the per-segment `GameObject`) is destroyed, `Release(token)` runs in `OnDestroy`.

### The third-party surface vs the wrapper

The wrapper exposes **only**: `AddInstances(Prefab, Matrix4x4[])` and `Release(token)`. Modders should not call `GPUInstancerAPI` directly for ties/tieplates; the wrapper owns the matrix storage and the per-prefab capacity. For **other** instanced geometry (e.g., a custom mod-side instanced mesh), use:

- `Helpers.InstancedMeshDrawer` (`Helpers/InstancedMeshDrawer.cs:7`) — a self-contained `Graphics.DrawMeshInstancedIndirect` wrapper with its own `ComputeBuffer`. Doesn't touch GPUInstancer. Subscribes to `transform.hasChanged` for floating-origin behaviour (translates instances by the position delta).
- Direct `GPUInstancerAPI` calls — out of scope here; see GPUInstancer asset docs.

### Patch candidates

| Method | Why patch |
|---|---|
| `PrefabInstancer.OnEnable` | Bump per-prefab capacity beyond 65535 (raise the `num = 65535` constants). |
| `PrefabInstancer.AddInstances` | Add a new `Prefab` enum value (requires also extending `AllPrefabs` and the `OnEnable` switch). Cleaner: add an enum value via reflection-friendly patching, but the asset's `prefabs[]` is `[SerializeField]` so you'll need a parallel `PrefabInstancer` for new types. |
| `PrefabInstancer.UpdateCoroutine` | Replace 0.1s batching cadence. |
| `PrefabInstancer.WorldDidMove` | Add origin-shift bookkeeping for parallel buffers. |

### Gotchas

- **Capacity is per-prefab static (65535).** Hitting this silently caps instance spawning. Watch for the `PendingAdd queue` log warning.
- **Removing instances shifts all later tokens' offsets.** `RemoveInstances` walks `instanceInfo.Tokens[i].Offset -= length` (line 244). Adding/removing churn → O(token-count²). For mass tear-down, consider releasing all tokens at once and rebuilding.
- **`AddInstances` returns `null` if `!Application.isPlaying`.** The check at line 111 means in-editor calls are no-ops. The matching `Release(null)` is a defensive no-op (line 128).
- **Initialization is async.** `UpdateCoroutine` waits for `prefabManager.isInitialized` — `AddInstances` calls before that point queue indefinitely. Generally fine because track build also waits on map load.

---

## `Effects.Decals.DecalCullingManager` (the decal budgeter)

The most algorithmically dense piece in the rendering pipeline. **Singleton-on-demand** (`Shared` getter constructs a `GameObject("Decal Culling Manager")` if missing). Every `URP DecalProjector` registered here gets jobified frustum + screen-size + distance culling, with adaptive screen-size threshold tuning to stay near a target visible count.

### Configuration

```csharp
[SerializeField] private float cullDistance                      = 600f;
[SerializeField] private float updateInterval                    = 0.25f;     // 0.125s when camera moving fast
[SerializeField] private float screenSizeThresholdHighQuality    = 1f;        // small decals visible
[SerializeField] private float screenSizeThresholdLowQuality     = 2.5f;      // only big decals
[SerializeField] private float frustumScale                      = 0.7f;      // shrunk frustum (under-cull on edges)
[SerializeField] private int   decalBudget                       = 200;       // target visible count
[SerializeField] private float cameraVelocityThreshold           = 1f;
[SerializeField] private float cameraAngularVelocityThreshold    = 1f;
```

### Update loop

```csharp
private void Update() {
    _timeSinceLastUpdate += Time.deltaTime;
    float interval = _updateFast ? updateInterval * 0.5f : updateInterval;
    if (_timeSinceLastUpdate >= interval && MainCameraHelper.TryGetIfNeeded(ref _mainCamera)) {
        UpdateDecalVisibilityJob();
        _timeSinceLastUpdate = 0f;
    }
}

private void FixedUpdate() {                                                 // detect "fast" camera
    Vector3 vel       = (camera.GamePosition()    - _lastCameraPosition)    / dt;
    Vector3 angVel    = π/180 * (camera.eulerAngles - _lastCameraEulerAngles) / dt;
    _updateFast = vel.sqrMagnitude > velocityThreshold² ||
                  angVel.sqrMagnitude > angularThreshold²;
}
```

### The Burst job

```csharp
[BurstCompile]
private struct DecalCullingJob : IJobParallelFor {
    public NativeArray<float3> DecalPositions, DecalForwards, DecalSizes;
    public float3 CameraPosition;
    public float CullDistance;
    public NativeArray<float4> FrustumPlanes;
    public float4x4 ProjectionMatrix, WorldToViewMatrix;
    public float MinScreenSize, ScreenHeight;
    public NativeArray<bool> DecalVisibility;

    public void Execute(int i) {
        // 1. distance cull
        if (math.distance(pos, camera) > CullDistance) { vis = false; return; }
        // 2. frustum cull (AABB vs 6 planes)
        if (!IsInFrustum(AABB, FrustumPlanes)) { vis = false; return; }
        // 3. screen-size cull (project to screen, threshold by pixels)
        float pixelHeight = abs(size.y / clip.w) * ScreenHeight * 0.5;
        DecalVisibility[i] = pixelHeight > MinScreenSize;
    }
}
```

`IJobParallelFor.Schedule(count, batchSize=32).Complete()` — synchronous, blocks until done.

### Adaptive budgeter

```csharp
private void UpdateScreenSizeThreshold(int visibleCount) {
    if (visibleCount > decalBudget * 1.5f) lerp threshold to LOW_QUALITY by 0.5;   // emergency
    else if (visibleCount > decalBudget * 1f) lerp by 0.1;                          // gradual
    else if (visibleCount < decalBudget * 0.5f) lerp toward HIGH_QUALITY by 0.5;    // recovery
    else if (visibleCount < decalBudget * 0.75f) lerp by 0.1;                        // gradual
    threshold = clamp(threshold, HIGH, LOW);
}
```

So the screen-size threshold drifts within `[1f, 2.5f]` to keep visible decal count near 200. Far decals (small on screen) get culled first.

### Frustum scaling

```csharp
CalculateNativeFrustumPlanes(forward, planes, position, output, frustumScale = 0.7f);
```

**The 4 side planes are tilted toward the camera-forward axis by `(1-scale) * orthogonal`.** The math: each side plane's normal is split into "along-forward" component and "perpendicular" component; the perpendicular is scaled by 0.7. Result: a *narrower* frustum than rendered. Decals near the screen edges are aggressively culled even if technically rendered. Reduces popping when decals enter the visible frustum at oblique angles.

### Patch candidates

| Method | Why patch |
|---|---|
| `DecalCullingManager.cullDistance` (serialized field) | Adjust max decal distance globally. |
| `DecalCullingManager.decalBudget` | Tune target count; affects when adaptive threshold backs off. |
| `DecalCullingManager.UpdateScreenSizeThreshold` | Replace adaptive policy. Currently asymmetric (drops fast, recovers fast on gross overshoot). |
| `DecalCullingManager.frustumScale` | Reduce edge-popping vs more visible decals. |
| `DecalCullingManager.UpdateDecalVisibilityJob` | Add new culling axes (e.g., per-decal LOD priority). |

### Gotchas

- **`DecalCullingManager.Shared` lazily constructs a hidden GameObject.** First call sets `hideFlags = HideFlags.DontSave`. There is no scene-authored singleton; it's process-lifetime.
- **`UnregisterDecal` walks `_decalProjectors` linearly.** O(N) on a list of up to ~thousands. Mods registering many short-lived decals will pay this.
- **The job is `.Complete()`d synchronously each `Update`.** No frame-spreading. With 1000+ registered decals this is ~50µs/frame — not free.
- **`MinScreenSize` is in pixels of decal *vertical* extent at the projected depth.** A flat decal viewed edge-on returns ~0 pixels and gets culled regardless of distance.
- **`decalProjector.transform.position` is read raw** — it's already world-space (URP `DecalProjector` is rendered at world coords). The job's `CameraPosition` is also raw `_mainCamera.transform.position` (world-space). Floating-origin shifts the camera *and* every registered decal projector simultaneously, so the comparison stays valid.

---

## `Effects.Decals.DecalProjectorHelper` + `CanvasDecalRenderer` (text-on-cars)

Vanilla decals are **car lettering** (reporting marks, road numbers). Created by `Model.ComponentBuilders.DecalComponentBuilder` from a `DecalComponent` in a car definition.

### `DecalContent` enum

```csharp
DecalContent.RoadNumber  → templateName = "Number"
DecalContent.Lettering   → templateName = "Tender"
```

Mapped to TMP-text canvas templates living in `CanvasDecalRenderer.container`'s child hierarchy (one `CanvasGroup` per template).

### Render pipeline

```
DecalComponentBuilder.Build:
  - AddComponent<DecalProjector>            ← URP decal
  - AddComponent<DecalProjectorHelper>      ← lifecycle gate
  - helper.text = car.Ident.RoadNumber/ReportingMark
  - For Lettering: ObserveProperty("lettering.basic", text → re-render)

DecalProjectorHelper.RenderDecal:
  - decalRenderer.Render(size, template, text, ct)        ← async Task<CanvasDecal>
  - CanvasDecalRenderer queues the request
  - WorkQueue coroutine processes ≤5ms-budget worth per frame
  - Per request:
      * Switch active CanvasGroup to template
      * Set TMP_Text.text
      * Render off-screen camera into RenderTexture (R8 format)
      * ReadPixels into Texture2D (cached by "{template}/{text}/{w}x{h}" key, refcounted)
      * Return CanvasDecal IDisposable
  - helper._material.SetTexture("_Texture", decal.Texture)
  - decalProjector.material = _material
```

### Visibility gating (3-AND)

```csharp
_decalProjector.enabled = _rendered && _decalVisible && _carVisible;
```

Three gates *all* must be true:
- `_rendered` — async render finished, texture assigned.
- `_decalVisible` — `DecalCullingManager` says yes (frustum + distance + screen-size).
- `_carVisible` — parent `Car.IsVisible` (which is `CarCuller`-driven).

`_carVisible` flips also drive `SetDecalRegistered(visible)` — invisible cars are *unregistered* from `DecalCullingManager` entirely (line 100), reducing job load. This is the chief reason why hopping cameras around a yard with 200 cars stays smooth.

### `CanvasDecalRenderer` texture cache

```csharp
private readonly Dictionary<string, Record> _records;   // key: $"{template}/{text}/{w}x{h}"
```

Refcounted: same lettering on multiple cars = single texture. `Record.ReferenceCount++` per `Render` call; `Return` (called by `CanvasDecal.Dispose`) decrements; when count hits 0, `Object.Destroy(canvasDecal.Texture)` and remove. `R8` format (1 byte/pixel) — these are alpha masks tinted by `_Color` material property.

### Decal sizes & resolution

```csharp
int pixelsPerMeter = Lerp(pixelsPerMeterSmall=150, pixelsPerMeterLarge=75, InverseLerp(1f, 10f, sizeMeters));
int width  = ceil(pixelsPerMeter * size.x);
int height = ceil(pixelsPerMeter * size.x * (size.y / size.x));
```

Smaller decals get *more* pixels-per-meter (150) than larger ones (75). Total resolution scales with surface area but at a sub-linear rate. Largest 10m decal at 75 px/m → 750 px wide.

### Decal `drawDistance`

```csharp
decalProjector.drawDistance = Mathf.Max(600f, Mathf.Max(component.Size.x, component.Size.y) * 100f);
```

Larger decals get larger draw distance (size×100, floor 600m). This is URP's intrinsic decal cull; `DecalCullingManager` adds a *second* layer on top.

### `DepthProjectorHelper` (terrain depth-aware decals)

Separate component for decals that need **absolute world Y** baked in (e.g., decals projected onto terrain that fades with depth from a reference Y). Refreshes two material uniforms on every origin shift:

```csharp
material.SetFloat("_DecalProjectorOriginY", transform.position.y);
material.SetFloat("_DecalProjectorDepth",   _decalProjector.size.z);
```

`Y` is never shifted by `WorldTransformer` (XZ only), so `_DecalProjectorOriginY` is stable across shifts in absolute terms — but the helper still refreshes on shift to be safe (line 42). **Only one shader uniform pair, only one helper** — used selectively where shaders need depth fade keyed to a fixed world Y.

### Patch candidates

| Method | Why patch |
|---|---|
| `DecalComponentBuilder.Build` | Add new `DecalContent` enum values (paired with new TMP templates in `CanvasDecalRenderer.container`). |
| `CanvasDecalRenderer.Render` | Modify cache key (e.g., per-color, per-font); current key is `template/text/wxh`. |
| `CanvasDecalRenderer.WorkQueue` budget | Currently 5ms/frame. Increase for faster rebuild after bulk car spawn. |
| `DecalProjectorHelper.RenderDecal` | Add additional pre-render gates (e.g., skip if config says decals off). |
| `DepthProjectorHelper.UpdatePosition` | Add custom depth shader uniforms for mod-defined depth-aware decals. |

### Gotchas

- **The off-screen `canvasCamera` is enabled only during `Render`** (`CanvasDecalRenderer.cs:196-200`). It writes to a temporary `RenderTexture` then disables. Don't add observers to its `OnPostRender`; it's not active most of the time.
- **`_canvasGroups` is populated lazily on first `PrepareCanvasGroups` call** (line 138). New templates added at runtime won't appear unless you clear `_canvasGroups` or replace the dictionary.
- **`R8` format = 1 channel, 8 bits.** The shader applies `_Color` tint. No multi-color decals; for those you'd need an `RGBA32` `RenderTexture` and a different shader.
- **`Render` is sync inside a coroutine, async to caller.** The `Task<CanvasDecal>` resolves when the queued request runs, not immediately. If the coroutine stops (component disable), the task may never complete — the cancellation token in `Request.CancellationToken` is the proper cleanup path (`DecalProjectorHelper._renderCancellationTokenSource`).
- **Texture cache lifetime is process-wide.** A texture stays alive as long as any registered car uses that combination. Unique road numbers → no sharing → no eviction. With long sandbox sessions and rare repeats, the cache grows.
- **Lettering re-renders on every `lettering.basic` KVO change.** Old texture is `Dispose`d (refcount--), new one rendered. Hammering the field via mod will thrash texture allocation.
- **The `MaxLetteringLength = 100`** constant in `DecalProjectorHelper` (line 44). Truncate/Truncate(100) before render. Set on the helper, not validated upstream.

---

## Enviro integration (the deeper API)

`EnviroManager.instance` is the third-party Enviro 3 asset's singleton. Railroader code touches it in **5 locations**:

| Site | What it reads/writes |
|---|---|
| `TimeWeather.SunLevel` | `Enviro.solarTime` (read) |
| `TimeWeather.WeatherId` get/set | `Enviro.Weather.targetWeatherType`, `Enviro.Weather.Settings.weatherTypes`, `Enviro.Weather.ChangeWeather(preset)` |
| `EnviroSynchronizer.UpdateCoroutine` | `Enviro.Sky.SetupSkybox()` (once); `Enviro.Time.SetDateTime(...)` (5 Hz); `Enviro.Reflections.RenderGlobalReflectionProbe(forced:true)` (when game-time jumps > 10 min) |
| `EnviroSynchronizer.UpdateReflectionThreshold` | `Enviro.Reflections.Settings.globalReflectionsPositionTreshold = curve(cameraVelocity)` (sic — typo "Treshold" is the asset's spelling) |
| `EnviroSynchronizer.UpdateGlobalFogHeight` | `Enviro.Fog.Settings.globalFogHeight = camera.y + additionalFogOffset` |
| `CameraSelector._JumpToPoint` | `Enviro.Reflections.RenderGlobalReflectionProbe(forced:true)` after teleport (`CameraSelector.cs:662`) |
| `PlayerController` (when entering character mode?) | `Enviro.Reflections.RenderGlobalReflectionProbe(forced:true)` (`PlayerController.cs:392`) |
| `EnviroMicrosplatIntegration.Update` | `Enviro.Environment.Settings.{snow, wetness}` → `Shader.SetGlobalFloat` (per frame) |

**That's the whole surface.** No code touches `Enviro.Audio`, `Enviro.Lighting`, individual particle systems, etc.

### `EnviroManager.instance.{Time, Weather, Reflections, Fog, Environment}` reference

- **`Time`** — write-only from Railroader's perspective. `Time.SetDateTime(s, m, h, day, month, year)` driven by `TimeWeather.Now` translated through `TimeWeather.StartDateTime = 1940-04-01`. Enviro internally derives sun position from this.
- **`Weather`** — `targetWeatherType` (current preset reference), `Settings.weatherTypes` (the catalog list), `ChangeWeather(preset)`. See [Time & Weather › Weather replication](time-weather.md#weather-replication) for the seven preset IDs. Per-frame interpolation between presets is Enviro-internal and emits no events.
- **`Reflections`** — `RenderGlobalReflectionProbe(forced:true)` is the camera-teleport flush; `Settings.globalReflectionsPositionTreshold` is the camera-velocity-driven update distance (smaller threshold = more frequent reflection updates).
- **`Fog`** — `Settings.globalFogHeight` is the only field touched. Tracked to camera ground Y per 0.2s; this keeps fog "level" with the player as elevation changes.
- **`Environment`** — `Settings.snow` and `Settings.wetness` are read by `EnviroMicrosplatIntegration` for shader globals. Both are `0..1` values driven by Enviro's preset interpolation.

### Day/night

- `TimeWeather.SunLevel = InverseLerp(0.3f, 0.5f, Enviro.solarTime)` (`TimeWeather.cs:78`). `Enviro.solarTime` is normalized 0..1 across sunrise→sunset. SunLevel maps 0.3→0 (pre-dawn) and 0.5→1 (post-dawn). Used by `HeadlightController.UpdateForParameter` to lerp `lightIntensityNight` ↔ `lightIntensityDay`.
- No explicit day/night events. `ClockDriver.Schedule(onHour, offHour, action)` is the hour-bound scheduler if you can hard-code times. (See [Time & Weather › Time-driven systems](time-weather.md#time-driven-systems).)

### Weather rendering hooks

- **There are no first-party weather rendering hooks.** Enviro renders rain/snow/clouds/sky internally per preset.
- The `Microsplat`-side shader globals (`_Global_RainIntensity`, `_Global_PuddleParams`, `_Global_StreamMax`, `_Global_WetnessParams`, `_Global_SnowLevel`) are *consumed* by Microsplat terrain shaders for puddle/snow/wet visuals on the ground. Custom shaders can `SAMPLE` these globals.
- No vanilla shader other than Microsplat reads them.

### Custom weather preset injection

Enviro keeps `Weather.Settings.weatherTypes` as `List<EnviroWeatherType>`. Adding a new `EnviroWeatherType` `ScriptableObject` and appending to that list will make it visible to `TimeWeather.WeatherId` (the index check is `value < weatherTypes.Count`). But:

- `TimeWeather.WeatherIdLookup` is hard-coded with names `clear, cloudy1, cloudy2, fog, rain, cloudy3` mapping to indexes `0,1,2,3,4,6`. Custom presets won't have a name; mods would either patch the dictionary or skip name-based lookup and use the integer ID directly.
- `GameStorage.ObserveWeatherId` defaults to `cloudy2` (index 2) when null — patches that change the catalog should preserve indices 0..6 to avoid breaking existing saves.
- The `/weather <name>` console command reads from `WeatherIdLookup` — if you want shell access, add to that dictionary.
- Replication is via the `weatherId` int KVO; the receiver simply calls `Enviro.Weather.ChangeWeather(weatherTypes[id])`. If both client and host have the same custom preset list (mods loaded symmetrically), it works. If asymmetric, `IndexOutOfRangeException` is silently logged.

### Why `EnviroMirrorPlayer`/`EnviroMirrorServer` are empty

(Per [Time & Weather › Empty stub classes](time-weather.md#empty-stub-classes).) Both classes are body-less `MonoBehaviour`s in `Enviro/`. `EnviroPhotonIntegration` is also empty. They appear to be placeholders for a never-completed networked-Enviro story. Currently:

- Each MP client runs its own `EnviroSynchronizer` driven by the same `TimeWeather.Now` (which *is* network-replicated). So time-of-day stays in sync.
- Weather is replicated via the `weatherId` KVO observer.
- Per-frame interpolation between presets is purely client-local (Enviro internal) — drift is bounded by preset switch frequency, which is rare.
- Wind direction, cloud positions, individual rain particles — all client-local.

The empty `EnviroSettingsApplicator` (`Game.Settings/EnviroSettingsApplicator.cs:7`) registers for `EnviroSettingChanged` and calls an empty `UpdateSetting()`. The only emitter of `EnviroSettingChanged` is `Preferences.GraphicsNightLightLevel` setter (`Preferences.cs:392`). So `gfx.night-light-level` is **stored to PlayerPrefs but never applied**. This is verified-dead code.

### Patch candidates

| Method | Why patch |
|---|---|
| `EnviroSynchronizer.UpdateCoroutine` | Override the time/weather/reflection/fog driver loop. |
| `EnviroSynchronizer.UpdateGlobalFogHeight` | Track something other than camera Y (e.g., terrain elevation under camera). |
| `EnviroMicrosplatIntegration.Update` | Add new global shader uniforms keyed off Enviro state. |
| `TimeWeather.WeatherId.set` | Intercept preset switches; substitute custom catalog. |
| `EnviroSettingsApplicator.UpdateSetting` | **Currently a no-op.** Implement to actually apply `Preferences.GraphicsNightLightLevel`. |

### Gotchas

- **`Enviro.Reflections.RenderGlobalReflectionProbe(forced:true)` is expensive** — re-renders the whole reflection probe. Called: (a) by `EnviroSynchronizer` only when game-time jumps >10 min in one tick (rare in normal play, common during `WaitTime`), (b) after every camera teleport (`CameraSelector._JumpToPoint`), (c) by `PlayerController` line 392 (likely on character mode entry). Don't call this every frame.
- **`globalReflectionsPositionTreshold` (sic)** — Enviro asset's spelling. Don't "fix" it.
- **Sun position math uses `1940-04-01` epoch** (`TimeWeather.StartDateTime`). `EnviroSynchronizer` adds `now.Day * 24 + now.Hours` hours to that DateTime, then pushes year/month/day/hour/min/sec to `Enviro.Time.SetDateTime`. So the in-game year does advance over time (Enviro will interpolate solar declination across the in-game calendar). Visual difference is subtle but real.
- **Microsplat globals are write-only.** No code reads them back. Mods needing wetness should read `EnviroManager.instance.Environment.Settings.wetness` directly, not the shader global.
- **`UpdateRainRipples` writes `_Global_RainIntensity`** but no shipped shader reads `_Global_RainIntensity` (verify against your custom shaders). The `UpdateRainRipples` field is safe to disable for performance if you're not authoring rain-aware materials.

---

## Microsplat / terrain shader globals

Set per-frame by `EnviroMicrosplatIntegration.Update` (`Enviro/EnviroMicrosplatIntegration.cs:30`):

| Global uniform | Source | Range |
|---|---|---|
| `_Global_SnowLevel` | `Environment.Settings.snow` | 0..1 |
| `_Global_WetnessParams` | `Vector2(clamp(wetness, minWetness, maxWetness), maxWetness)` | x in [minWetness, maxWetness] |
| `_Global_PuddleParams` | `Environment.Settings.wetness` | 0..1 |
| `_Global_RainIntensity` | `clamp(wetness, 0, 1)` | 0..1 (but no consumer in vanilla) |
| `_Global_StreamMax` | `Environment.Settings.wetness` | 0..1 |

`minWetness` and `maxWetness` (default 0, 1) are serialized fields on the `EnviroMicrosplatIntegration` component — adjust per-scene to scale wetness-driven puddle effects.

**To add a custom global shader uniform driven by weather:** patch `EnviroMicrosplatIntegration.Update` to set additional `Shader.SetGlobalFloat`/`SetGlobalVector` calls. Because it's `Update`-rate, no Messenger event needed.

**To author a custom shader that reads weather:** declare `float _Global_SnowLevel; float _Global_PuddleParams; float4 _Global_WetnessParams;` etc. They're available globally in any shader in any pass.

---

## URP context

Railroader uses URP. Direct evidence:
- `using UnityEngine.Rendering.Universal;` in `DecalProjector*.cs`, `PostProcessingSettingsApplicator.cs`, `DecalComponentBuilder.cs`.
- `DecalProjector` (URP-only component) is the decal primitive.
- `Volume` + `ColorAdjustments` (URP postprocess) used by `PostProcessingSettingsApplicator`.

What this means for modders:
- **You cannot use Built-In RP `Projector` for decals.** Use URP `DecalProjector` and add a `DecalProjectorHelper` (or pattern equivalent) for visibility gating.
- **Camera post-processing volumes work** — drop a `Volume` GameObject with overrides; the active main camera renders through them. Only one applicator is wired in vanilla (`PostProcessingSettingsApplicator`), driving exposure + contrast from PlayerPrefs.
- **`ScriptableRenderer` features (Decal, SSAO, etc.)** are configured in the URP asset, which is not in `Assembly-CSharp.dll`. They're project assets. To add a URP renderer feature in a mod, you'd need to inject it into the active `UniversalRendererData` at runtime (a non-trivial dance, out of scope here).
- **No first-party support for HDRP** — `RequireComponent(typeof(DecalProjector))` is URP-binding throughout decal code.

---

## Per-system summary: where rendering decisions actually live

| Decision | Authority | Modifiable how |
|---|---|---|
| Car visibility (per-car) | `CarCuller` | Patch `_distanceBands`, `OnCarCullingGroupStateChanged`, or `Car.SetVisible` |
| Car LOD swap | `Car.SetCullerDistanceBand` interpreting `CarCuller` band | Patch `SetCullerDistanceBand`; vanilla has no per-car LOD mesh swap, only per-band behavior toggles |
| Car model load/unload | `Car.ModelLoadRetain` token, refcounted, with 300s delayed unload | Patch `Car.UnloadModelsDelayed` or `Config.Shared.carModelUnloadDelay` |
| Scenery visibility | `CullingManager.Scenery` (`[100, 1000, 1500]` bands) + per-instance `RendererCuller`/`SceneryAssetInstance` band thresholds | New `RendererCuller`/`SceneryAssetInstance`; bands fixed at scene start |
| Bridge generation | `CullingManager.Bridge` (`[1000]`) → `AutoTrestle.GenerateIfNeeded` | Patch `AutoTrestle.GenerateIfNeeded`; replace `BridgeDistanceBands` |
| Signal renderers | `CullingManager.Signal` (`[1000]`) | Patch `SignalDistanceBands` |
| CTC panel renderers/canvases | `CullingManager.CTC` (`[25]`) | Patch `HoseDistanceBands` (CTC reuses the Hose array — see Initializer line 56) |
| Hose Verlet sim | `CullingManager.Hose` (`[25]`) — `Hose.FixedUpdate` no-ops if not visible | Increase the band; pay sim cost |
| Flare components | `CullingManager.Flare` (`[25, 1000]`) | Light/VFX < 25, mesh < 1000 |
| Tie/tieplate batching | `Track.PrefabInstancer` via GPUInstancer; capacity 65535 each | Bump capacity; new `Prefab` enum value (heavy refactor) |
| Decal visibility | `DecalCullingManager` (200 budget, adaptive screen-size) + URP `DecalProjector.drawDistance` (max(600m, size×100)) | Adjust `decalBudget`, `cullDistance`, or per-decal `drawDistance` |
| Decal text content | `CanvasDecalRenderer` off-screen camera → R8 RT | New `DecalContent` value + new template `CanvasGroup` in `container` |
| Sky / clouds / sun / rain particles | Enviro internal | Direct `EnviroManager.instance.Weather/Sky/...` API |
| Reflection probe | Enviro `Reflections.RenderGlobalReflectionProbe(forced:true)` | Trigger when needed; expensive |
| Fog density | Enviro per-preset; height tracked by `EnviroSynchronizer` | Patch `UpdateGlobalFogHeight` |
| Snow/wetness/puddles on terrain | `_Global_*` shader uniforms via `EnviroMicrosplatIntegration` | New uniforms via patch |
| Camera far clip | `CameraSettingsApplicator` reads `Preferences.GraphicsDrawDistance` (default 1500m) on `GraphicsDrawDistanceChanged` event | `Preferences.GraphicsDrawDistance = N`; range clamp 100..10000 |
| Postprocess (exposure, contrast) | `PostProcessingSettingsApplicator` reads `Preferences.GraphicsPostExposure/Contrast` | Same Preferences keys |
| Tree / detail density | `Preferences.GraphicsTreeDensity/DetailDensity` consumed by NatureRenderer (3rd-party) | PlayerPrefs key; no Messenger broadcast — values read on next NatureRenderer init |
| Particle quality | `Preferences.GraphicsParticleLevel` (Off / Low / Standard) consumed by `ParticleSettingsApplicator` (component-level Stop) | Set the pref; affects only `Off` toggle in vanilla — `Low`/`Standard` are equivalent |

---

## Patch points for mods (cheat sheet)

### "I want a custom LOD curve for cars"

1. Read `CarCuller._distanceBands` via reflection.
2. Replace with your array (must be ascending, 4 entries: close, nearby, load, unload).
3. Call `CullingGroup.SetBoundingDistances(_distanceBands)` on the `CarCuller`'s private `_cullingGroup`.
4. Note: `Car.SetCullerDistanceBand`'s `bool isNearby = current <= 1` is hard-coded — patch that too if your bands have different semantics.

### "I want to add my own GameObjects to the culling system"

For shared pooling (recommended):
1. Add `using Helpers.Culling;`
2. `class MyComp : MonoBehaviour, CullingManager.ICullingEventHandler`
3. In `OnEnable`: `_token = CullingManager.Scenery.AddSphere(transform, radius, this);` (or another domain).
4. Implement `CullingSphereStateChanged(bool, int)` and `RequestUpdateCullingPosition()`.
5. In `OnDisable`: `_token?.Dispose();`

For one-off (no LOD bands, just on/off):
1. Add `Helpers.SimpleCuller` component to the GameObject.
2. Set `radius` and `distance`.

For a new domain (entirely fresh `CullingManager` with custom bands):
1. Patch `CullingManagerInitializer.OnEnable` postfix to create another `CullingManager` GameObject with your bands.
2. Expose it as a static accessor on your mod side.

### "I want a custom decal type"

1. Define a new TMP-text canvas template under `CanvasDecalRenderer.container` (drop a `CanvasGroup` GameObject named e.g. `MyMod_Custom`).
2. Add a new `DecalContent` enum value (requires patching `DecalContent.cs` or shipping your own enum).
3. Add a new builder following `DecalComponentBuilder.Build` pattern that sets `helper.templateName = "MyMod_Custom"`.

For decals not on cars, use `DecalProjector` directly + register with `DecalCullingManager.Shared.RegisterDecal(decal, visibilityCallback)`. Don't forget `UnregisterDecal` on cleanup.

### "I want a custom weather preset"

1. Author an `EnviroWeatherType` `ScriptableObject` (Enviro 3 asset workflow).
2. At runtime, append it to `EnviroManager.instance.Weather.Settings.weatherTypes` (probably `OnEnable` of a mod MonoBehaviour after Enviro initializes).
3. Add an entry to `TimeWeather.WeatherIdLookup` (patch the dictionary getter).
4. Consider replication: if other clients don't have your mod, `weatherId = N` will throw `IndexOutOfRangeException` (silently logged). Negotiate via mod handshake.

### "I want a custom shader global driven by weather/time"

1. Patch `EnviroMicrosplatIntegration.Update` postfix.
2. Add `Shader.SetGlobalFloat("_MyMod_X", computeFromEnviroState());`
3. Or run a parallel `MonoBehaviour` with its own `Update` — Microsplat's component isn't doing anything magical, just per-frame `SetGlobal*` calls.

### "I want to know when the camera teleports (for decal/reflection refresh)"

`CameraSelector.CameraJumped()` is called after every teleport (`CameraSelector.cs:236-242`) and:
1. Calls `WorldTransformer.MoveNow()` immediately.
2. Calls `Enviro.Reflections.RenderGlobalReflectionProbe(forced:true)` (`CameraSelector.cs:662`).

Subscribe to `WorldDidMoveEvent` to catch the resulting (likely) origin shift, or patch `CameraSelector.CameraJumped` for a synchronous hook.

---

## Cross-references

- Origin-shift event subscribers (full catalog including `CullingManager`, `SimpleCuller`, `PrefabInstancer`, `DepthProjectorHelper`, `TelegraphPoleManager`): see [Floating Origin › Subscriber catalog](floating-origin.md#subscriber-catalog).
- `MainCameraHelper.TryGetIfNeeded` pattern (used by `DecalCullingManager`): see [Floating Origin › `MainCameraHelper`](floating-origin.md#helpersmaincamerahelper).
- Enviro time/weather facade and the `weatherId` KVO + `EnviroSynchronizer` 5 Hz writer: see [Time & Weather › Time-driven systems](time-weather.md#time-driven-systems) and [Time & Weather › Weather effects](time-weather.md#weather-effects-the-visual-side).
- `TimeWeather.SunLevel` consumer (`HeadlightController`): see [Time & Weather › Time-driven systems](time-weather.md#time-driven-systems).
- `Car.IsVisible` flow into decal helper: this doc § DecalProjectorHelper.
- `Car.ModelLoadRetain` ref-counting + the 300s `carModelUnloadDelay`: see also [Cars & Cargo](cars-cargo.md) (Bardo lifecycle handles the unloaded-tile case).
- `CarShaderHelper.ReplaceShaders` post-load shader swap: see [Asset Packs › `PrefabStore`](asset-packs.md) for the asset-load context.
- `Camera.main` swap behavior across camera modes: see [Player & Camera › CameraSelector](player-camera.md#cameraselector-the-mode-switcher).
