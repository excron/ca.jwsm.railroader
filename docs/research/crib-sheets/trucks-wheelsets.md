# Trucks & Wheelsets — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Definition/`)
**Companions:** [Cars, Cargo & Loading](cars-cargo.md), [Car Definitions](car-definitions.md), [Asset Packs](asset-packs.md), [Brakes](brakes.md), [Traction](traction.md), [Wear & Durability](wear-durability.md)

Trucks (bogies) in Railroader are a **separate `ObjectDefinition` kind** (`TruckDefinition`, `Kind = "Truck"`) referenced from a car by string identifier. At model load, exactly **two** truck instances are spawned per car (`_truckA` / `_truckB`) — both cloned from the same prefab and parented to `BodyTransform` at `±truckSeparation/2` along Z. The truck `MonoBehaviour` is `RollingStock.Wheelset`, which both rolls all wheels at the same animation phase from a shared local odometer **and** acts as the per-truck `IBrakeAnimator`. There is no per-axle physics, no per-wheel slip, no per-truck force. `Wheelset` instances are **leaked permanently** by `PrefabStore._truckReferences` — once any car has loaded a given truck identifier, the underlying GameObject template stays resident until `PrefabStore.Dispose` (game shutdown). Steam-locomotive *driver* wheelsets are an entirely separate, definition-embedded `SteamLocomotiveDefinition.Wheelset` list driven by `SteamLocomotiveWheelAnimator`; the two systems share no code.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Model.Definition.Data.TruckDefinition` | `Definition/Model.Definition.Data/TruckDefinition.cs` | Data-driven truck spec: model id, wheel diameter, axle count, brake animation, wheel transforms |
| `RollingStock.Wheelset` | `RollingStock/Wheelset.cs` | The per-truck MonoBehaviour. Implements `IBrakeAnimator` and rolls/animates wheels |
| `RollingStock.IBrakeAnimator` | `RollingStock/IBrakeAnimator.cs` | One-property interface (`bool BrakeApplied`) — both `Wheelset` and `BrakeAnimator` impl it |
| `RollingStock.BrakeAnimator` | `RollingStock/BrakeAnimator.cs` | Body-level (non-truck) brake-rigging animator. Plays `CarDefinition.BrakeAnimations` clips |
| `Car._truckA`, `Car._truckB` | `Model/Car.cs:281, 283` | The two `Wheelset` instances per car. **Always exactly two** |
| `Car.SetupTrucks()` | `Model/Car.cs:1306` | Body-load-time truck instantiation (positions, materials, oil pickables) |
| `Car.SetupBrakeAnimations()` | `Model/Car.cs:1473` | Wires `BrakeAnimator` for `Definition.BrakeAnimations` if `AnimationMap` exists |
| `Car.UpdateBrakeApplied(bool)` (`virtual`) | `Model/Car.cs:2615` | Per-FixedUpdate fan-out to all `IBrakeAnimator`s |
| `Car.SetTruckPositions(qA, qB, info)` | `Model/Car.cs:2259` | Per-tick world-rotation update + `Roll()` distance call |
| `Car.UpdateTruckLinearOffset()` (`virtual`) | `Model/Car.cs:2979` | Wheelset audio-clack offsetting; overridden on `SteamLocomotive` |
| `PrefabStore.TruckPrefabForId(string)` | `Model.Database/PrefabStore.cs:157` | Memoized truck-prefab loader. **Source of the leak** |
| `PrefabStore._truckReferences` | `Model.Database/PrefabStore.cs:33` | The HashSet that pins every loaded truck `LoadedAssetReference` for the session |
| `SteamLocomotiveDefinition.Wheelset` | `Definition/Model.Definition.Data/SteamLocomotiveDefinition.cs:7` | Per-driver wheelset descriptor (Offset/Length/Diameter/NumberOfAxles/Animation/Transform) |
| `RollingStock.Steam.SteamLocomotiveWheelAnimator` | `RollingStock.Steam/SteamLocomotiveWheelAnimator.cs` | Driver-set animator. Reads `Wheelsets[]` and applies per-driver phase from `_wheelVelocity` |
| `Model.CarWheelState` (enum) | `Model/CarWheelState.cs` | `Tracking | Slip | Lock` — **`Lock` is dead** (never set; see [traction.md](traction.md#gotchas-baselocomotive)) |

---

## Truck spine: identifier → leaked Wheelset → two child instances

```
CarDefinition.TruckIdentifier (string, e.g., "truck-archbar-2axle")
   │
   ▼
Car.LoadModelsAsync()                              ← Car.cs:1168
   │   prefabStore.TruckPrefabForId(id)            ← memoized: returns same Task<Wheelset>
   │       LoadWheelset(id, tcs):                  ← PrefabStore.cs:170
   │          1. AssetPackContainingIdentifier(id) → first-match across stores
   │          2. DefinitionForIdentifier<TruckDefinition>(...) → unmarshal
   │          3. await store.LoadAsset<GameObject>(truckDef.ModelIdentifier, ...)
   │          4. CarShaderHelper.ReplaceShaders(asset)
   │          5. wheelset = asset.AddComponent<Wheelset>()      // template GO mutated!
   │          6. animMap = asset.GetComponentInChildren<AnimationMap>()
   │          7. animator = animMap.gameObject.AddComponent<Animator>()
   │              animator.cullingMode = AnimatorCullingMode.CullCompletely
   │          8. wheelset.diameterInInches = truckDef.Diameter * 3.28084 * 12
   │          9. wheelset.applyBrakesAnimationClip = animMap.ClipForName(truckDef.BrakeAnimation.ClipName)
   │         10. foreach truckDef.WheelTransforms: wheelset.wheels.Add(...)
   │         11. tcs.SetResult(wheelset)
   │         12. _truckReferences.Add(loadedAssetRef)            ← PINNED FOR THE SESSION
   ▼
Car.HandleModelsLoaded → DidLoadModels → SetupTrucks()       ← Car.cs:1306
   │   _truckA = Instantiate(template, BodyTransform)         ← clones the *template* GO
   │   _truckB = Instantiate(template, BodyTransform)
   │   _truckA.Configure(wheelClackProfile, this)             ← attaches WheelAudio
   │   _truckB.Configure(wheelClackProfile, this)
   │   localPosition = ±(truckSeparation/2) on Z
   │   BrakeAnimators.Add(_truckA);  BrakeAnimators.Add(_truckB)
   │   GetRenderers + MakeMaterialsUnique  (per-car material instances)
   │   if (EnableOiling)
   │      AddOilPointPickable(±num, _truckA.CalculateAxleSpread(), diameterMeters)
   ▼
(per-tick)
   Car.PositionWheelBoundsFront → SetTruckPositions(aRot, bRot, info)
        _truckA.transform.rotation = aRot     ← absolute world rotation
        _truckB.transform.rotation = bRot
        _truckA.Roll(distance, velocity)       ← rotates wheels by linear odometer
        _truckB.Roll(distance, velocity)
```

**Critical: the prefab template's `Animator`, `Wheelset` component, animation clip, and wheel-transform list are mutated in `LoadWheelset` and never reset.** Subsequent cars instantiate the already-configured template. This works because `Wheelset.diameterInInches` and the like are static across the truck definition — but if a mod re-`LoadAsset`s the same identifier or swaps the underlying asset, the singleton mutation pattern can corrupt all cars sharing that truck.

---

## `Model.Definition.Data.TruckDefinition`

```csharp
public class TruckDefinition : ObjectDefinition                  // TruckDefinition.cs
{
    public override string Kind { get; } = "Truck";

    public string ModelIdentifier { get; set; }                  // → AssetBundle GameObject id
    public float  Diameter { get; set; }                         // wheel diameter, METERS
    public float  Length { get; set; }                           // unused at runtime (no consumers)
    public int    NumberOfAxles { get; set; } = 2;               // unused at runtime (no consumers)
    public List<TransformReference> WheelTransforms { get; set; }
    public AnimationReference BrakeAnimation { get; set; }

    public override void Awake() { }
}
```

### Field semantics

| Field | Used? | Where |
|---|---|---|
| `Kind` | yes | `JsonSubtypes` polymorphism on `ObjectDefinition` (registered in `ObjectDefinition.cs:90` as `"Truck"`) |
| `ModelIdentifier` | yes | `PrefabStore.LoadWheelset` `await store.LoadAsset<GameObject>(...)` |
| `Diameter` | yes | `PrefabStore.LoadWheelset:186` — converted to inches: `wheelset.diameterInInches = Diameter * 3.28084 * 12` (= `Diameter * 39.37008`). Used by `Wheelset.Roll` for visual rotation rate AND by `Car.AddOilPointPickable` for the oil-pickable visual |
| `Length` | **no consumers** | Field is read nowhere in the assembly. Vestigial / planned |
| `NumberOfAxles` | **no consumers** (default 2) | Field is read nowhere in the assembly. The actual wheel count comes from `WheelTransforms.Count`. Vestigial. *Note*: the related field on `SteamLocomotiveDefinition.Wheelset.NumberOfAxles` IS used by `SteamLocomotiveWheelAnimator.GenerateWheelPositions` for clack offsets — different system |
| `WheelTransforms` | yes | `PrefabStore.LoadWheelset:189-200` — each resolved via `wheelset.transform.ResolveTransform(...)` and added to `Wheelset.wheels`. Bad references are caught and logged (don't kill the load) |
| `BrakeAnimation` | yes | `PrefabStore.LoadWheelset:188` — `wheelset.applyBrakesAnimationClip = animMap.ClipForName(BrakeAnimation.ClipName)` |
| `Components` (inherited) | yes (passed through `EnabledComponentsForLifetime`) but never iterated for trucks — no truck-side `SetupComponents` exists. **Components on a truck definition are ignored at runtime** |

`Diameter` is in **meters** in JSON; it's converted to inches inside the loader. Don't confuse with `SteamLocomotiveDefinition.Wheelset.Diameter` which is also meters but goes through `engine.driverDiameterInches = wheelset.Diameter * 39.37008f` (`SteamLocomotive.cs:208`).

### Patch candidates (TruckDefinition)

| Method | Why patch |
|---|---|
| `JsonSubtypes` registration on `ObjectDefinition` | Required if you want a `TruckDefinition` subtype with extra fields (e.g., `BrakeShoeMaterial`). Same constraint as adding any `ObjectDefinition` subclass — see [car-definitions.md › ObjectDefinition](car-definitions.md#modeldefinitionobjectdefinition--the-root-abstraction). |
| `PrefabStore.LoadWheelset` | Single chokepoint to inject custom truck prep (animation overrides, extra components, validation). Patch postfix to mutate `wheelset` before `tcs.SetResult` |
| `TruckDefinition.Awake` | No-op; harmless to add post-deserialize fixups (clamp `Diameter`, default `WheelTransforms`) |

### Gotchas (TruckDefinition)

- **`Length` and `NumberOfAxles` look like physics inputs but are dead.** A `TruckDefinition` with `NumberOfAxles = 6` will still get exactly two `Wheelset` instances and however many wheels are listed in `WheelTransforms`. The visual axle count is `WheelTransforms.Count`; the audio-clack count is computed by `WheelAudio.Configure` from `wheels` (see below).
- **`Diameter` is meters in JSON but converted to inches.** Round-tripping via the editor preserves the meters value, so don't be alarmed if you see `Diameter: 0.838` in JSON for a 33-inch wheel.
- **No `Components` are processed.** A modder who copy-pastes a `CarDefinition` template and sets `Kind: Truck` plus adds `Components: [...]` will see those components silently ignored.

---

## `RollingStock.Wheelset` — the per-truck MonoBehaviour

```csharp
public class Wheelset : MonoBehaviour, IBrakeAnimator             // Wheelset.cs:11
{
    [SerializeField] internal List<Transform> wheels;             // populated by PrefabStore
    [SerializeField] internal float diameterInInches = 33f;       // default 33"
    [SerializeField] internal Animator animator;                  // added by PrefabStore
    [SerializeField] internal AnimationClip applyBrakesAnimationClip;

    private bool _brakeAppliedAnimationState;
    private WheelAudio _wheelAudio;
    private float _localOdometer;
    private PlayableHandle _applyBrakesPlayable;
    private Renderer[] _renderers;

    public bool BrakeApplied { get; set; }                        // IBrakeAnimator
    public void Configure(WheelClackProfile profile, Car car);    // adds WheelAudio
    public void SetLinearOffset(float value);                     // → _wheelAudio.LinearOffset
    public void Roll(float distance, float velocity);             // visual + audio
    public void SetVisible(bool visible);                         // toggles Renderer.enabled
    public float CalculateAxleSpread();                           // for oil pickable sizing
}
```

### `Roll(distance, velocity)` (visual rotation + clack audio)

```csharp
public void Roll(float distance, float velocity)                  // Wheelset.cs:77
{
    _localOdometer += distance;
    float circumference = MathF.PI * (diameterInInches * 0.0254f);
    float wrap = 10f * circumference;
    if (_localOdometer >  wrap) _localOdometer -= wrap;
    if (_localOdometer < -wrap) _localOdometer += wrap;
    float x = 360f / circumference * _localOdometer;
    foreach (Transform wheel in wheels)
        wheel.localEulerAngles = new Vector3(x, 0f, 0f);          // ALL wheels in lockstep
    _wheelAudio.Roll(distance, velocity);
}
```

**All wheels in a truck rotate at the exact same phase** — no per-axle offset, no slip wobble. The wrap-around `10 * circumference` keeps `_localOdometer` bounded after long runs (otherwise the float would lose precision).

### `BrakeApplied` setter

```csharp
public bool BrakeApplied
{
    get => _brakeAppliedAnimationState;
    set
    {
        if (value != _brakeAppliedAnimationState)                 // edge-only
        {
            _brakeAppliedAnimationState = value;
            BrakeAppliedDidChange();                              // play clip forward/reverse
        }
    }
}

private void BrakeAppliedDidChange()
{
    if (_applyBrakesPlayable != null)
    {
        _applyBrakesPlayable.Time = Mathf.Clamp(_applyBrakesPlayable.Time, 0f, applyBrakesAnimationClip.length);
        _applyBrakesPlayable.Speed = (BrakeApplied ? 1 : -1);
        _applyBrakesPlayable.Play();
    }
}
```

The `_applyBrakesPlayable` is created in `OnEnable` from `applyBrakesAnimationClip` and disposed in `OnDisable`. Edge-debounced; setting `BrakeApplied = true` twice fires the animation only once.

### `Configure(profile, car)`

```csharp
public void Configure(WheelClackProfile profile, Car car)         // Wheelset.cs:66
{
    _wheelAudio = gameObject.AddComponent<WheelAudio>();
    _wheelAudio.Configure(profile, wheels, car);
}
```

`WheelAudio.Configure(profile, wheels, car)` (`WheelAudio.cs:66`) computes the audible wheel count: if any wheel name ends with `_LOD0`, only those count; otherwise the full `wheels.Count`. Then it derives `wheelSeparation = Vector3.Distance(wheels[0], wheels[last])` and stamps an evenly-spaced `_clackOffsets[]` along the truck length. **Wheel naming convention matters** — name your wheel transforms `*_LOD0`/`*_LOD1` if you have LOD duplicates and want only LOD0 to count.

### `CalculateAxleSpread()`

```csharp
public float CalculateAxleSpread()                                // Wheelset.cs:124
{
    if (wheels.Count == 0) return 2f;                             // default 2m
    // scan local-Z range across all wheels, return zMax - zMin
}
```

Used solely by `Car.SetupTrucks` to size the oil pickable (`AddOilPointPickable(zPos, axleSeparation, diameterMeters)` — `Car.cs:1342`).

---

## `Car.SetupTrucks()` — the per-car instantiation

```csharp
private void SetupTrucks()                                        // Car.cs:1306
{
    if (string.IsNullOrEmpty(Definition.TruckIdentifier)) return;
    Wheelset template = _truckPrefabLoadTask.Result;              // shared template
    if (template == null) { Debug.LogWarning(...); return; }

    _truckA = Instantiate(template, BodyTransform, false);
    _truckB = Instantiate(template, BodyTransform, false);
    _truckA.name = "Truck A";
    _truckB.name = "Truck B";
    var wcp = TrainController.Shared.wheelClackProfile;
    _truckA.Configure(wcp, this);
    _truckB.Configure(wcp, this);
    UpdateTruckLinearOffset();
    float halfSep = truckSeparation / 2f;
    _truckA.transform.localPosition = Vector3.forward * halfSep;
    _truckB.transform.localPosition = Vector3.back    * halfSep;
    _truckA.transform.localRotation = Quaternion.identity;
    _truckB.transform.localRotation = Quaternion.identity;
    BrakeAnimators.Add(_truckA);
    BrakeAnimators.Add(_truckB);
    Renderer[] rA = GetRenderers(_truckA.gameObject);
    Renderer[] rB = GetRenderers(_truckB.gameObject);
    MakeMaterialsUnique(_truckA.gameObject, rA);                  // per-car material instances
    MakeMaterialsUnique(_truckB.gameObject, rB);
    _truckRenderers.AddRange(rA);
    _truckRenderers.AddRange(rB);
    if (EnableOiling)
    {
        float diameterM = _truckA.diameterInInches / 39.37008f;
        float axleSep   = _truckA.CalculateAxleSpread();
        AddOilPointPickable( halfSep, axleSep, diameterM);
        AddOilPointPickable(-halfSep, axleSep, diameterM);
    }
}
```

### Truck attachment to car body

- **Always two trucks**, named `"Truck A"` and `"Truck B"`. **Not** indexed by `LogicalEnd` — `_truckA` is at `+truckSeparation/2` on local Z (the body's "forward"), `_truckB` is at `-`. So `_truckA` corresponds to whichever LogicalEnd happens to be on the front, which depends on `FrontIsA`.
- **Local rotation is reset to identity at spawn** — the per-tick `SetTruckPositions` (`Car.cs:2259`) overwrites world rotation directly, so the local-rotation reset is just a cosmetic init.
- **Empty `TruckIdentifier` is allowed** — `SetupTrucks` early-returns and the car has no visual trucks. The body sits on its `BodyTransform` with no truck instances. Useful for handcars, motor cars, anything that doesn't have a separate bogie. The car still moves; the truck visuals are purely cosmetic.
- **No tri-truck support.** A car needing three trucks (an articulated or 3-bogie special) cannot be expressed in vanilla. Even a Big Boy's tender is the same two-truck `Wheelset` clones.
- **`truckSeparation` is mutated by `Car.ValidateDefinition`** (`Car.cs:1044`): if `Definition.TruckSeparation < 1f` it's clamped to `Length / 2f`. The clamp **mutates the shared `CarDefinition` instance** (the same object cached by `PrefabStore`), so the first-spawned car of a definition permanently sets the value.

### How `truckSeparation` is consumed

| Site | Use |
|---|---|
| `Car.cs:1326-1328` | `_truckA.localPosition = +truckSeparation/2 on Z`, `_truckB.localPosition = -truckSeparation/2` |
| `Car.cs:1962` | `wheelInsetF = wheelInsetR = max(0.3, (carLength - truckSeparation)/2 - 1f)` |
| `Car.cs:1976-1978` | `PositionWheelBoundsFront` uses it to compute the back-truck location: `LocationByMoving(frontTruckLoc, -truckSeparation)` |
| `Car.cs:1415` | Forwarded to `DerailedEffectComponent.Separation` |
| `Car.cs:2983, 2987` | `SetLinearOffset(_linearOffset ± truckSeparation/2)` for clack-audio offsetting |

### Patch candidates (SetupTrucks)

| Method | Why patch |
|---|---|
| `Car.SetupTrucks` | Inject custom truck behavior. Postfix is safe; pre-existing `_truckA`/`_truckB` are ready. **Do not** patch the `Instantiate` call — the template is shared |
| `Car.UpdateTruckLinearOffset` (`virtual`) | Override per-subclass to redirect linear offset to a different audio system. `SteamLocomotive` overrides to skip the truck-side update and route to `SteamLocomotiveWheelAnimator` instead |
| `Car.SetTruckPositions` (`private`) | Replace per-tick truck positioning. Risky — runs every visual tick |
| `Car.AddOilPointPickable` | Modify oil-pickable shape/position; called once per truck |

### Gotchas (SetupTrucks)

- **`MakeMaterialsUnique` runs per-truck**, so each truck has its own material instances. Mutating `_truckA`'s materials does NOT affect `_truckB` after spawn — even though they share a template.
- **`_truckA` and `_truckB` are nullable.** Empty `TruckIdentifier` leaves both null. Code that assumes trucks exist must null-check (vanilla does at `Car.cs:2261, 1523-1531, 1579-1585, 2981-2987`).
- **`SetTruckPositions` writes world rotation, not local.** The truck `transform.rotation = aRot` line means trucks ignore body roll/pitch — they pin their orientation to the rail at their position. This is correct for railroad trucks (they pivot in body yaw) but means a roll animation on the body won't roll the trucks visually.
- **Truck `Roll(distance, velocity)` is called on every visual update.** Even at zero velocity, `_truckA.Roll(0, 0)` runs and `_wheelAudio.Roll(0, 0)` is a cheap no-op. But if you postfix `Roll`, expect very high call frequency.
- **`_truckRenderers` accumulates renderers from both trucks** but is never cleared between loads — actually, `UnloadModels` calls `_truckRenderers.Clear()` (`Car.cs:1546`). Safe.

---

## The leaked truck reference pattern (full mechanism)

`PrefabStore` holds two truck-specific containers:

```csharp
private readonly Dictionary<string, Task<Wheelset>> _truckPrefabTasks
    = new Dictionary<string, Task<Wheelset>>();                   // PrefabStore.cs:31

private readonly HashSet<LoadedAssetReference<GameObject>> _truckReferences
    = new HashSet<LoadedAssetReference<GameObject>>();            // PrefabStore.cs:33
```

`TruckPrefabForId(id)` (`PrefabStore.cs:157`):

```csharp
public Task<Wheelset> TruckPrefabForId(string truckIdentifier)
{
    if (_truckPrefabTasks.TryGetValue(truckIdentifier, out var task))
        return task;                                              // memoized
    var tcs = new TaskCompletionSource<Wheelset>();
    _truckPrefabTasks[truckIdentifier] = tcs.Task;
    LoadWheelset(truckIdentifier, tcs);                           // async
    return tcs.Task;
}
```

`LoadWheelset` (`PrefabStore.cs:170`) awaits `LoadAsset<GameObject>(modelId, CancellationToken.None)`, configures the template `Wheelset`, calls `tcs.SetResult(wheelset)`, then `_truckReferences.Add(loadedAssetReference)`.

### Why this leaks

- `LoadedAssetReference<T>` is the refcounted handle — see [asset-packs.md › refcounted bundle lifecycle](asset-packs.md). Disposing the reference releases the bundle's hold.
- Vanilla **never disposes truck references** during a session. The hash set is only emptied in `PrefabStore.Dispose()` (`PrefabStore.cs:119-123`), which runs once at game shutdown.
- Even if every car using a truck identifier despawns, the truck template GameObject and its asset bundle stay loaded.
- The `_truckPrefabTasks` dict caches the **same** `Task<Wheelset>` per identifier — so subsequent cars `await` the already-completed task and get the existing template.

### Consequence: truck templates are mutated once, used forever

`LoadWheelset` does:

```csharp
wheelset = asset.AddComponent<Wheelset>();                        // mutates the template
animator = animMap.gameObject.AddComponent<Animator>();           // mutates the template
animator.cullingMode = AnimatorCullingMode.CullCompletely;
wheelset.diameterInInches = truckDef.Diameter * 39.37008f;
wheelset.animator = animator;
wheelset.applyBrakesAnimationClip = animMap.ClipForName(...);
foreach (TransformReference wheelTransform in truckDef.WheelTransforms)
    wheelset.wheels.Add(wheelset.transform.ResolveTransform(wheelTransform));
```

Calling `TruckPrefabForId(sameId)` a second time **does not re-run this block** — it returns the cached task. So mutating `truckDef` after the first load has no effect on existing or future cars.

### Patch candidates (leak)

| Method | Why patch |
|---|---|
| `PrefabStore.LoadWheelset` | Inject custom prep. Postfix patch can attach extra MonoBehaviours to the template (which all subsequent instances will inherit). Be careful — a Component you add to the template is destroyed only on bundle unload, which never happens in vanilla |
| `PrefabStore.TruckPrefabForId` | Veto the cache and force re-load. Useful for hot-reload mods, but be aware: existing `_truckA`/`_truckB` instances on cars don't get re-instantiated |
| `PrefabStore.Dispose` | Add custom cleanup. Note: only fires at game shutdown |

### Gotchas (leak)

- **Disposing a `LoadedAssetReference<GameObject>` while there are live `Instantiate`d clones** does NOT destroy the clones (Unity reference-counts the bundle, not the GameObject). But the underlying `AssetBundle` may unload; if a clone tries to lazily load shaders or sub-assets, those references break.
- **Trucks loaded once-and-forever means a `truckDef.Diameter` JSON edit at runtime has no effect on existing cars.** Patch `Wheelset.diameterInInches` directly on the template (or each clone) to change rolling radius.
- **The `_truckReferences` set holds `LoadedAssetReference`s, not `Wheelset`s.** Inspecting it tells you which truck identifiers have ever been loaded this session, but you can't get back to the cars from there.
- **Re-running `PrefabStore.Create` mid-session is unsupported** — `_truckPrefabTasks` would point to disposed tasks. Vanilla never does this.

---

## Truck rendering / model loading

Truck models are AssetBundle-loaded the same way car bodies are loaded — but **before** the body is loaded. `Car.LoadModelsAsync` (`Car.cs:1160`) starts:

1. `LoadAssetAsync<GameObject>(packId, modelId)` for the body — added to `_modelLoadTasks["model"]`.
2. If `Definition.TruckIdentifier` is non-empty: `_truckPrefabLoadTask = prefabStore.TruckPrefabForId(...)`.
3. `await Task.WhenAll(_modelLoadTasks.Values)` (body + any other model tasks).
4. `if (_truckPrefabLoadTask != null) await _truckPrefabLoadTask` — extra await for trucks.
5. `HandleModelsLoaded()` proceeds, calling `DidLoadModels()` → `SetupTrucks()` → `SetupBrakeAnimations()`.

The truck and body are awaited **separately**; the `Task.WhenAll` doesn't include the truck task. If a car has no truck, the second await is skipped.

### Per-truck shader replacement

`PrefabStore.LoadWheelset` calls `CarShaderHelper.Instance.ReplaceShaders(asset)` on the truck prefab (`PrefabStore.cs:181`), same as `LoadAssetAsync` does for car bodies. The shader replacement happens **once on the template**, before instantiation, and propagates to all clones via the shared materials. (Materials are subsequently `MakeMaterialsUnique`-d per-car in `SetupTrucks`, so the shaders end up unique-per-car-per-truck — not shared.)

### `Animator.cullingMode = AnimatorCullingMode.CullCompletely`

Set on the truck template's `Animator` (`PrefabStore.cs:185`). Means the truck brake animation will not advance while off-screen, conserving CPU. **`AnimatorCullingMode.CullCompletely` also disables the playable graph** — when a truck comes back into view, the brake animation may "snap" to its current target rather than gradually playing. The `BrakeAnimator.BrakeWasAppliedDidChange` clamps `Time` to clip bounds before resuming, mitigating this.

---

## `RollingStock.BrakeAnimator` — the body-level brake animator

`SetupBrakeAnimations()` (`Car.cs:1473`):

```csharp
private void SetupBrakeAnimations()
{
    if (Definition.BrakeAnimations == null || Definition.BrakeAnimations.Count == 0) return;
    var (animator, animMap) = SetupForAnimation();                // resolves AnimationMap
    if (animMap == null) return;                                  // logged warning
    BrakeAnimator brakeAnimator = animMap.gameObject.AddComponent<BrakeAnimator>();
    brakeAnimator.animator = animator;
    brakeAnimator.brakeAnimationClips = Definition.BrakeAnimations
        .Select(animRef => animMap.ClipForName(animRef.ClipName)).ToArray();
    BrakeAnimators.Add(brakeAnimator);
}
```

So body-level brake animations come from `CarDefinition.BrakeAnimations` (a `List<AnimationReference>`), each resolved against the car body's `AnimationMap`. They live in `BrakeAnimators` alongside the two truck wheelsets.

### `BrakeAnimator` MonoBehaviour

```csharp
public class BrakeAnimator : MonoBehaviour, IBrakeAnimator         // BrakeAnimator.cs
{
    public Animator animator;
    public AnimationClip[] brakeAnimationClips;
    private PlayableHandle[] _brakePlayables;
    private bool _brakeWasApplied;

    public bool BrakeApplied { get; set; }                         // edge-triggered

    private void Start()
    {
        var adapter = animator.PlayableGraphAdapter();
        _brakePlayables = new PlayableHandle[brakeAnimationClips.Length];
        for (int i = 0; i < brakeAnimationClips.Length; i++)
            _brakePlayables[i] = adapter.AddPlayable(brakeAnimationClips[i]);
    }

    private void BrakeWasAppliedDidChange()
    {
        if (_brakePlayables.Length == 0) return;
        foreach (var p in _brakePlayables)
        {
            p.ClampTimeToClipBounds();
            p.Speed = (BrakeApplied ? 1 : -1);
            p.Play();
        }
        _brakeWasApplied = BrakeApplied;
    }
}
```

Plays **all** clips in `brakeAnimationClips` simultaneously. So if your `CarDefinition.BrakeAnimations` lists `["brakeShoeFront", "brakeShoeRear"]`, both fire on the same edge.

### `Car.UpdateBrakeApplied(bool)` — the fan-out

```csharp
protected virtual void UpdateBrakeApplied(bool brakeApplied)       // Car.cs:2615
{
    foreach (IBrakeAnimator brakeAnimator in BrakeAnimators)
        brakeAnimator.BrakeApplied = brakeApplied;
}
```

Called from `Car.FixedUpdate` (`Car.cs:949`):

```csharp
bool brakeApplied = air.handbrakeApplied || air.BrakeCylinder.Pressure > 2f;
UpdateBrakeApplied(brakeApplied);
```

So the brake is "visually applied" iff the handbrake is set OR the brake-cylinder pressure exceeds 2 psi. **No per-truck brake-pipe pressure** — both trucks animate together. See [brakes.md › `UpdateBrakeApplied` visual sync](brakes.md#updatebrakeapplied-visual-sync) for cross-context.

`BrakeAnimators` is a `HashSet<IBrakeAnimator>` (`Car.cs:370`) populated by:
- `SetupTrucks` adds `_truckA`, `_truckB`.
- `SetupBrakeAnimations` adds the body-level `BrakeAnimator` (if any).

`UnloadModels` removes them all (truck refs at `Car.cs:1525, 1530`; body brake animators via `BodyTransform.GetComponentsInChildren<BrakeAnimator>()` at `Car.cs:1535-1539`).

### Patch candidates (BrakeAnimation)

| Method | Why patch |
|---|---|
| `Car.UpdateBrakeApplied(bool)` (`virtual`) | Override per-subclass to do per-truck variations (e.g., front truck only on a 4-wheel diesel). The default fan-out passes the same bool to every animator |
| `Car.SetupBrakeAnimations` | Add custom body-level brake animations or skip vanilla ones. Runs once per body load |
| `BrakeAnimator.BrakeWasAppliedDidChange` (`private`, hard to patch) | Replace the all-clips-together model. Patching the `Speed` set lets you stagger clips |
| `Wheelset.BrakeAppliedDidChange` (`private`) | Same — for per-truck animation override |
| `Car.FixedUpdate` (gate at `Car.cs:948`) | Replace the `2f` PSI threshold or the `||` with `&&`, etc. |

### MP authority (BrakeAnimation)

Brake-applied state is computed locally from KVO-replicated air-system state (`air.BrakeCylinder.Pressure`, `air.handbrakeApplied`). Both host and client compute the same bool and call `UpdateBrakeApplied` independently. **No network message for brake animation** — visual is derived state.

### Gotchas (BrakeAnimation)

- **Threshold 2 psi is hardcoded** in `Car.cs:948`. There's no settings key. To make brakes look applied at lower pressure, patch `FixedUpdate`.
- **Edge-debounced via `_brakeWasApplied` / `_brakeAppliedAnimationState`.** Setting the same value twice is a no-op. If you replace the playable graph mid-run, you must clear the debounce flag or the animation won't restart.
- **`SetupBrakeAnimations` requires an `AnimationMap` MonoBehaviour on the car body.** Without one, you get a logged warning and zero body brake animators. Truck animators still work because they have their own `AnimationMap` (resolved in `PrefabStore.LoadWheelset`).
- **`Definition.BrakeAnimations` is a `List<AnimationReference>` not an array.** The serialization handles both fine. Each `AnimationReference` is just `{ ClipName: string }`.

---

## Truck per-wheel diameters and `driverDiameterInches`

Two parallel diameter fields exist:

| Field | Source | Type | Used by |
|---|---|---|---|
| `Wheelset.diameterInInches` | `TruckDefinition.Diameter * 39.37008f` (PrefabStore.cs:186) | per-truck (template-shared) | `Wheelset.Roll` for visual rotation |
| `SteamEngine.driverDiameterInches` | `SteamLocomotiveDefinition.Wheelsets[MainDriverIndex].Diameter * 39.37008f` (SteamLocomotive.cs:208) | per-locomotive | `TrainMath.CalculateWaterConsumption`, `TractiveEffort`, `MaximumSpeedMph` (Car.cs:216 `maxSpeedMph = driverDiameterInches + Random.Range(5,10)`) |

**The two diameters are unrelated.** A steam locomotive has a `TruckDefinition` (typically a leading/trailing pony truck) AND its own `Wheelsets[]` for the driving wheels. The truck diameter affects only the bogie visual; the driver diameter affects the engine physics and the locomotive's max speed.

For non-steam cars, `driverDiameterInches` is irrelevant — there are no driver wheels.

For diesels, **only the truck diameter matters** (visually). There's no per-axle physics — diesels use `DieselLocomotiveDefinition.StartingTractiveEffort` for force regardless of wheel size.

### Per-driver wheel animation (steam only)

`SteamLocomotiveWheelAnimator.ApplyDistanceMoved(info, driverVelocity, ...)` (`SteamLocomotiveWheelAnimator.cs:252`):

```csharp
foreach wheel in wheels:
    float circumference = wheel.diameter * MathF.PI;
    float velocity = wheel.isDriver ? driverVelocity : Locomotive.velocity;
    float dPhase = info.DeltaTime * velocity / circumference;
    wheel.Parameter = Mathf.Repeat(wheel.Parameter + dPhase, 1f);
    if (wheel.isDriver)
        DriverPhase = wheel.Parameter;
```

So **driver wheels rotate at the slipping `_wheelVelocity` rate, but pony/trailing wheels rotate at the actual ground velocity.** This is the only place wheel slip is visually distinguished. `Wheelset.Roll` (the truck visual) uses **only** `velocity` (the ground velocity, passed by `Car.SetTruckPositions`), so pony trucks on a slipping locomotive look correct (they don't slip), and driver wheels on a steam loco look correct (they spin faster on slip).

### Patch candidates (Diameter)

| Method | Why patch |
|---|---|
| `SteamLocomotive.FinishSetup` | Override `engine.driverDiameterInches` to decouple from `Wheelsets[MainDriverIndex].Diameter`. Also where `maxSpeedMph` is set |
| `Wheelset.Roll` (per-truck) | Override visual rotation rate (e.g., gear ratio for a geared loco) |
| `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` | Per-driver phase logic — patch to add per-axle slip simulation |

### Gotchas (Diameter)

- **`maxSpeedMph` for steam is `driverDiameterInches + Random.Range(5,10)`** (SteamLocomotive.cs:216). So a 63" driver gives 68-72 mph. **Hardcoded relationship; not configurable.** Override in a subclass or patch `FinishSetup`.
- **For non-steam cars, `maxSpeedMph` is `Random.Range(75, 85)`** (Car.cs:1960) — completely unrelated to truck diameter. Hardcoded.
- **`SteamLocomotiveDefinition.Wheelset.Diameter` defaults to `1f` meter** (SteamLocomotive.cs:179) when `ValidateDefinition` falls back to a single-wheelset default. Be explicit in your JSON to avoid this.

---

## `CarWheelState` (dead-but-present)

```csharp
public enum CarWheelState                                          // Model/CarWheelState.cs
{
    Tracking,
    Slip,
    Lock                                                          // ← dead
}
```

```csharp
public virtual CarWheelState WheelState => CarWheelState.Tracking; // Car.cs:757 — always Tracking on base
```

Subclassed by `BaseLocomotive` (`BaseLocomotive.cs:47`) which exposes `_wheelState` set by `UpdateTractiveEffortWheelState` (`BaseLocomotive.cs:518`). Only `Tracking` and `Slip` are written; **the `Lock` value is never set anywhere in the assembly**, including the switch at `BaseLocomotive.cs:544-557` which has a `case CarWheelState.Lock:` branch that's structurally unreachable.

See [traction.md › `UpdateTractiveEffortWheelState`](traction.md#updatetractiveeffortwheelstate--the-canonical-te-pipeline) for the full pipeline. The traction crib sheet labels this as "dead — never set" and we confirm here.

### Wheel slip per truck

**There is no per-truck wheel slip.** `_wheelState` is per-locomotive (one bool effectively, since `Lock` is dead). The `_wheelVelocity` term in `BaseLocomotive` is also per-locomotive. The **driver** wheels on a steam loco animate at `_wheelVelocity` while pony/trailing wheels and all other trucks rotate at body velocity (see Per-driver wheel animation above) — this is the only "per-truck" treatment of slip in vanilla.

For freight cars and cabooses (no `BaseLocomotive` subclass), `WheelState == CarWheelState.Tracking` always, and all four wheel-slip-related code paths are no-ops. There is no dynamic-brake wheel slide (no dynamic brake exists; see [brakes.md › Dynamic Brake — does not exist](brakes.md#dynamic-brake--does-not-exist)).

### Patch candidates (CarWheelState / slip)

| Method | Why patch |
|---|---|
| `BaseLocomotive.UpdateTractiveEffortWheelState` | The single chokepoint. To add `Lock` semantics, set `_wheelState = CarWheelState.Lock` somewhere (e.g., when `air.BrakeCylinder.Pressure > X` and slip is severe). Then the dead `case CarWheelState.Lock:` branch becomes live |
| `Car.WheelState` (`virtual`) | Override per-subclass. Default base returns `Tracking` always — your custom car class could compute slip from `_truckA`/`_truckB` velocities if you instrumented those |

### Gotchas (CarWheelState)

- **`Lock` enum value triggers `case CarWheelState.Lock:` in `BaseLocomotive.cs:554`** which sets `_wheelVelocity = Lerp(_wheelVelocity, 0, dt*10)` — i.e., wheels decelerate to zero relative to the rail. The branch is well-formed but unreachable. A single line `_wheelState = CarWheelState.Lock` somewhere in the engine pipeline would activate it.
- **`Wheelset.Roll(distance, velocity)` always uses ground `velocity`.** Even on a slipping driver-set steam loco, the *truck* visuals are unaffected — only the steam locomotive's `_wheelAnimator` (separate system) sees slip-derived `_wheelVelocity`.

---

## Per-truck brake force computation

**There isn't one.** `Car.CalculateBrakingForce` (`Car.cs:2991`):

```csharp
public float CalculateBrakingForce(float brakePercent, float absVelocity)
{
    float speedMph = absVelocity * 2.23694f;
    float curve = Config.brakeForceCurve.Evaluate(speedMph);
    brakePercent *= Mathf.Lerp(0.8f, 1f, Condition);
    float nominal = nominalBrakingForce * BrakeForceMultiplier;
    return brakePercent * nominal * curve * 4.44822f;
}
```

`nominalBrakingForce` is set by `SetNominalBrakingRatio` (`Car.cs:1058`) as a per-archetype factor times `Definition.WeightEmpty`. **Single car-level scalar — no per-truck variation.** See [brakes.md › Car.CalculateBrakingForce](brakes.md#carcalculatebrakingforce--the-physical-hand-off).

So the per-truck `Wheelset` instances are visual + audio only. They contribute zero to the physics retarding force.

### Implication for mods

To add per-truck brake force (e.g., one truck cut out for a brake test), you'd need:
1. Per-truck brake-applied state (currently both trucks share `air.handbrakeApplied || air.BrakeCylinder.Pressure > 2f`).
2. A per-truck force calculation hooked into `IntegrationSet.CalculateRetardingForce` or `Car.CalculateBrakingForce`.
3. A way to address each truck — vanilla has no public API for "the front truck" vs "the rear truck" beyond `_truckA`/`_truckB` (private fields).

---

## Definition validation — `DefinitionChecker` and trucks

`Model.Database.DefinitionChecker.Check(ObjectDefinition)` (`DefinitionChecker.cs:38`):

```csharp
public void Check(ObjectDefinition definition)
{
    if (definition is SteamLocomotiveDefinition def1) CheckSteamLocomotive(def1);
    else if (definition is CarDefinition def2)        CheckCar(def2);
    // ← no else branch
}
```

**Confirmed: `TruckDefinition` is not validated by `DefinitionChecker`.** Neither are `SceneryDefinition`, `MaterialDefinition`, `TextureDefinition`, or `WhistleDefinition`. They pass through silently.

Specifically, NO check exists for:
- `TruckDefinition.ModelIdentifier` being non-empty or present in the asset pack (a missing/typo'd id will fail later at `LoadAsset` with a logged error and `tcs.SetException`).
- `WheelTransforms` being non-empty (an empty list means `_clackOffsets.Length < 2` so `WheelAudio.Roll` skips the coroutine — silent no-op).
- `WheelTransforms` resolving to actual transforms (each is wrapped in try/catch in `PrefabStore.LoadWheelset:191` and individually logged).
- `BrakeAnimation.ClipName` resolving (`AnimationMap.ClipForName` returns `null` if missing; `Wheelset._applyBrakesPlayable` becomes null and brake animations silently no-op).
- `Diameter > 0` (zero-diameter wheels would cause `360f / 0f * _localOdometer` in `Wheelset.Roll`. Actually since `circumference = π * diameter`, dividing by zero gives `Infinity`, then `localEulerAngles.x = Infinity` which Unity clamps to NaN — wheels disappear).

`CarDefinition.TruckIdentifier` is also **not validated** — `DefinitionChecker.CheckCar` doesn't check that `TruckIdentifier` resolves. A typo'd id throws `UnknownIdentifierException` later inside `PrefabStore.LoadWheelset` (`AssetPackContainingIdentifier`); the car will exist with no trucks (the early-return in `SetupTrucks` doesn't fire because the task throws, not returns null — actually the task has an exception, and `_truckPrefabLoadTask.Result` will rethrow synchronously inside `SetupTrucks`, caught nowhere — so `SetupTrucks` propagates and `DidLoadModels` may partially fail).

### Patch candidates (validation)

| Method | Why patch |
|---|---|
| `DefinitionChecker.Check` | Add a `case TruckDefinition def: CheckTruck(def);` branch. Mod-side validation can save a lot of debugging |
| `DefinitionChecker.CheckCar` | Add `Assert(_store.ContainerItemForObjectIdentifier(definition.TruckIdentifier) != null, ...)` to catch broken truck refs at load time |
| `PrefabStore.LoadWheelset` | Wrap with extra validation — e.g., assert `truckDefinition.WheelTransforms.Count > 0` |
| `Car.SetupTrucks` | Add a try/catch around `_truckPrefabLoadTask.Result` — vanilla doesn't catch the rethrow |

### Gotchas (validation)

- **A typo'd `TruckIdentifier` throws an `UnknownIdentifierException` at car-spawn time, not load time.** The exception is caught in `LoadModelsAsync` (`Car.cs:1186-1189`) and logged ("Error loading trucks") — `HandleModelsLoaded` proceeds, then `SetupTrucks` accesses `_truckPrefabLoadTask.Result` which rethrows. The downstream `DidLoadModels` body fails partially.
- **Empty `WheelTransforms` is silently accepted.** No wheels to rotate, no clack audio, but the car moves and brakes function.
- **Tender LoadTargetComponent validation runs in `CheckHasFuelSlots`** — that's the only definition-checker rule that touches truck-adjacent state (oil pickables get installed near tender axles). See [car-definitions.md › `DefinitionChecker`](car-definitions.md#definitionchecker-load-time-validation).

---

## Patch points for custom truck types, wheelset arrangements, brake animations

### Custom `TruckDefinition` subtype (e.g., `LeadingTruckDefinition` with extra fields)

1. Define the `[Component(...)]` not applicable here — these are `ObjectDefinition`s, not `Component`s. Define your subclass:
   ```csharp
   public class LeadingTruckDefinition : TruckDefinition
   {
       public override string Kind { get; } = "LeadingTruck";
       public float MaxSlipAngle { get; set; }
   }
   ```
2. **Patch `JsonSubtypes` registration on `ObjectDefinition`.** No public API; you'll need to manipulate `JsonSubtypesConverter` reflectively at startup. Same constraint as adding a new `Component` subtype — see [car-definitions.md › Adding a new component type](car-definitions.md#adding-a-new-component-type).
3. **Patch `PrefabStore.LoadWheelset`** to detect your subtype and apply extra setup. The vanilla method casts to `TruckDefinition` via `DefinitionForIdentifier<TruckDefinition>`; your subclass is also a `TruckDefinition` so the cast succeeds, but you need to type-check inside the method to read your extra fields.

### Custom wheelset arrangements (3+ trucks per car)

**Vanilla cannot express this.** The hardcoded `_truckA`/`_truckB` field pair in `Car.cs:281, 283` is consumed in 7+ places. Adding a third truck requires:

1. Subclass `Car` and add `_truckC` (or a `List<Wheelset>`).
2. Override `SetupTrucks` to instantiate and position three+ trucks. Pick local Z positions (no analog to `truckSeparation/2` for three trucks — you'd need new fields like `TruckOffsets: List<float>`).
3. Override `SetTruckPositions` to address all trucks (vanilla only writes `_truckA`/`_truckB`).
4. Override `UpdateTruckLinearOffset`, `SetVisible`, `UnloadModels`, `OnDrawGizmosSelected` (`Car.cs:971-972` draws gizmos for both `_truckA` and `_truckB`).
5. Decide how the third truck is positioned in track-space — `PositionWheelBoundsFront` (`Car.cs:1974`) is hardcoded for 2-truck geometry. The center segment between two trucks is the body's bend pivot; with three trucks, you need new geometry.

This is a significant rewrite — there's no clean extension point.

For locomotive **driver** wheelsets, the `SteamLocomotiveDefinition.Wheelsets` list **does support arbitrary counts** (used for, e.g., 4-6-2 Pacific layouts). The `SteamLocomotiveWheelAnimator` already handles N drivers + leading/trailing trucks. So if your custom car is steam-shaped, you can express extra wheelsets via `Wheelsets[]` rather than additional bogie trucks.

### Custom brake animations

| Goal | Approach |
|---|---|
| Replace the 2 psi threshold | Patch `Car.FixedUpdate` postfix; recompute `brakeApplied` and call `UpdateBrakeApplied` |
| Per-truck brake animation independence | Override `Car.UpdateBrakeApplied`. Detect `_truckA`/`_truckB` via reflection or via `BrakeAnimators.OfType<Wheelset>()` |
| Custom truck brake animation clip | Patch `PrefabStore.LoadWheelset` postfix to swap `wheelset.applyBrakesAnimationClip` (mutates the template — affects all subsequent cars) |
| Body-level brake rigging animation | Add `BrakeAnimations` to your `CarDefinition`. Vanilla `SetupBrakeAnimations` plays them all in lockstep |
| Brake-shoe shoe-pop sounds | Subscribe to `Car.UpdateBrakeApplied` indirectly via a postfix — there's no dedicated event |

### Gotcha summary for patch authors

| Pitfall | Detail |
|---|---|
| Truck template mutation | `PrefabStore.LoadWheelset` mutates the prefab; cached forever. Postfix patches affect all future cars |
| `_truckA`/`_truckB` are private | Use reflection or subclass; no public accessor |
| `_truckA` ≠ `LogicalEnd.A` | `_truckA` is at `+truckSeparation/2 on Z` (body-relative front); `LogicalEnd.A` flips with `FrontIsA` |
| `BrakeAnimators` is `protected` | Subclass-accessible; mods need reflection to inspect the set |
| `BrakeApplied` setter is edge-debounced | Setting the same value twice is a no-op. Reset `_brakeAppliedAnimationState`/`_brakeWasApplied` if you replace the playable graph |
| Truck `Roll` uses ground velocity, not slip-velocity | Only `SteamLocomotiveWheelAnimator` distinguishes driver slip from ground velocity |
| `Wheelset.diameterInInches` overrides JSON | Set on the template at load. JSON edits at runtime do nothing unless you re-mutate the template AND existing `_truckA`/`_truckB` instances (their field is also a copy on each clone) |
| `truckSeparation` is mutated by `ValidateDefinition` | Floor of 1m; clamped on first car spawn; mutates shared `CarDefinition` |
| `TruckDefinition.Length` and `NumberOfAxles` are vestigial | Don't depend on them; the runtime ignores them |

---

## Cross-references

- Car spawn lifecycle and component model — see [Cars-Cargo › Lifecycle spine](cars-cargo.md#lifecycle-spine).
- `CarDefinition.TruckIdentifier`, `TruckSeparation`, `BrakeAnimations`, `MinimumCurveRadius` — see [Car Definitions › CarDefinition](car-definitions.md#cardefinition-the-per-car-prototype).
- `SteamLocomotiveDefinition.Wheelsets[]` — definition shape is in [Car Definitions](car-definitions.md#cardefinition-the-per-car-prototype); animation pipeline is `SteamLocomotiveWheelAnimator` (this doc).
- `PrefabStore` general lifecycle and the leaked-by-design pattern — see [Asset Packs › PrefabStore.Create](asset-packs.md) and the references at the top of this doc.
- `Car.CalculateBrakingForce` and the per-archetype `nominalBrakingForce` table — see [Brakes › Car.CalculateBrakingForce](brakes.md#carcalculatebrakingforce--the-physical-hand-off) and [Brakes › nominalBrakingForce per archetype](brakes.md#nominalbrakingforce-per-archetype).
- `Car.UpdateBrakeApplied` visual sync (the 2-psi gate) — see [Brakes › UpdateBrakeApplied visual sync](brakes.md#updatebrakeapplied-visual-sync).
- `BaseLocomotive.UpdateTractiveEffortWheelState` and the `_wheelVelocity` slip model — see [Traction › UpdateTractiveEffortWheelState](traction.md#updatetractiveeffortwheelstate--the-canonical-te-pipeline). The `CarWheelState.Lock` dead-code gotcha is also documented there.
- Oil-pickable per-truck installation — see [Wear › Oil](wear-durability.md#oil) and `Car.AddOilPointPickable` (Car.cs:1348).
- Curve overspeed and `MaximumTrackCurvature` (which interacts with truck geometry indirectly) — see [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).
