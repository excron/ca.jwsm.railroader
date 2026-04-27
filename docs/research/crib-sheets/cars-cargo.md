# Cars, Cargo & Loading — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Definition/`)
**Companions:** [Car Definitions](car-definitions.md), [Wear & Durability](wear-durability.md), [Couplers](couplers.md)

There is **no `RailVehicle` type** in Railroader. The vehicle entity is `Model.Car` (a `MonoBehaviour`), with two locomotive subclasses (`SteamLocomotive`, `DieselLocomotive`) chosen at instantiation by `CarArchetype`. Every other archetype (Boxcar, Caboose, Tender, Coach, etc.) uses the base `Car` class — the archetype distinction is data-driven via `CarDefinition.Archetype`. Cargo lives entirely in KVO keys (`load.{slotIndex}`) on the per-car `KeyValueObject`; loading is industry-driven and host-authoritative; weight derives from `Definition.WeightEmpty + Σ Load.Pounds(quantity)` recomputed on every `load.{n}` change.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Model.Car` (MonoBehaviour, `[SelectionBase]`) | `Model/Car.cs:42` | The vehicle. 3148-line god-class for state, KVO, lifecycle, coupling, wear |
| `Car.Setup(carId, descriptor, prefabs, isGhost)` | `Model/Car.cs:994` | Spawn-side init. Called once by `TrainController.CreateCarRaw` |
| `Car.LoadModelsAsync()` / `Car.UnloadModels()` | `Model/Car.cs:1160, 1507` | Lazy model load tied to culling band; `ModelLoadRetain` reference-counts |
| `Car.Weight` | `Model/Car.cs:738` | `WeightEmpty + _loadWeight`. Used by physics + UI |
| `Car.UpdateLoadWeight()` | `Model/Car.cs:1734` | Recomputes `_loadWeight` from `load.{i}` KVO keys via `Load.Pounds` |
| `Car.Archetype` | `Model/Car.cs:765` | Shortcut for `Definition.Archetype` |
| `Car.WillDestroy(isMovingToBardo)` | `Model/Car.cs:2623` | Cleanup; unregisters from `StateManager` and `IdGenerator.Cars` |
| `CarExtensions.GetLoadInfo` / `SetLoadInfo` | `Model.Ops/CarExtensions.cs:22, 27` | The loading API. Reads/writes `load.{slot}` KVO key |
| `OpsCarAdapter` | `Model.Ops/OpsCarAdapter.cs` | `IOpsCar` adapter — the interface industries see for `Load`/`Unload` |
| `IndustryLoader` / `IndustryUnloader` | `Model.Ops/IndustryLoader.cs, IndustryUnloader.cs` | Ops tick that loads/unloads cars in storage<->load.{slot} |
| `TrainController.CreateCarRaw` | `TrainController.cs:717` | Allocates the GameObject, picks subclass, calls `Setup` |
| `TrainController.WillRemoveCar` | `TrainController.cs:1516` | Despawn pipeline; also feeds Bardo move |
| `UI.CarInspector.CarInspector` | `UI.CarInspector/CarInspector.cs` | The vanilla floating window; KVO-bound through `UIPanelBuilder` |

---

## Lifecycle spine

```
CreateCarRaw(descriptor, carId, ghost, parent)              ← TrainController.cs:717
   │  AllocateCarIdIfNeeded(ref carId)
   │  new GameObject("Car " + carId)
   │  AddComponent< SteamLocomotive | DieselLocomotive | Car >  // CarArchetype switch
   │  car.Setup(carId, descriptor, prefabs, ghost)         ← Car.cs:994
   │     ValidateDefinition()                              ← clamps Length/Truck/Weight to floors
   │     EndGearF/R = new EndGear()
   │     FinishSetup()                                     ← adds CarAirSystem
   │     SetupKeyValueObject()                             ← registers KVO + observers (load.{i}!)
   │     ComponentSetup with ComponentLifetime.Static
   │  car.ResetKeyValueProperties(descriptor.Properties, origin) ← seeds KVO
   │  car.OnPosition = CarDidPosition                      ← spatial hash + culler
   ▼
LoadModelsAsync()  (triggered by ModelLoadRetain when culler distanceBand ≤ 1)
   │  PrefabStore.LoadAssetAsync(packId, ModelIdentifier)
   │  PrefabStore.TruckPrefabForId(TruckIdentifier)
   ▼
HandleModelsLoaded()                                       ← Car.cs:1193
   │  Instantiate body GameObject under Car
   │  MakeMaterialsUnique (per-car material instances)
   │  DidLoadModels()                                      ← Car.cs:1267 (virtual)
   │     SetupTrucks / SetupBrakeAnimations
   │     SetupCouplers / SetupCutLevers / SetupAnglecocks
   │     ComponentSetup with ComponentLifetime.Model
   ▼
(runtime…)
   ▼
WillRemoveCar(car, isMovingToBardo=false)                  ← TrainController.cs:1516
   │  DeallocateRoadNumber, _cars.Remove
   │  _spatialHash.Remove, _carCuller.Remove
   │  OpsController.RemoveCar
   │  car.WillDestroy(isMovingToBardo)                     ← Car.cs:2623
   │     UnloadModels()
   │     StateManager.UnregisterPropertyObject(id)
   │     IdGenerator.Cars.Remove(id)
   │  _integrationSets.RemoveCar(car)
   ▼
UnityEngine.Object.Destroy(car.gameObject)
```

**Bardo path:** `MoveToBardo(carId, senderId)` → `CarSetBardo` message → `HandleSetBardo` → `WillRemoveCar(car, isMovingToBardo:true)` (the `isMovingToBardo` flag **skips** road-number deallocation and `_cars.Remove`/`_carLookup.Remove` so the car stays in the registry) + `car.SetVisible(false)` + `car.Bardo = bardo`. Cars in Bardo never join an `IntegrationSet` and are excluded from culling (`if (!car.IsInBardo) _carCuller.Add(car)`, `Car.cs:710`).

**Save/load:** `HandleSnapshotCars` (`TrainController.cs:1553`) wipes everything (`RemoveCars(_carLookup.Keys)`) then re-adds via `AddCarInternal` per snapshot car. The snapshot per-car payload is built in `Car.Snapshot(SnapshotOption)` (`Car.cs:2546`) — id, definitionId, road number, location, velocity, trainCrewId, reportingMark, FrontIsA, Bardo. KVO state rides separately in `snapshotProperties` (`Dictionary<carId, Dictionary<key, IPropertyValue>>`) and is applied via `ResetKeyValueProperties`.

**`CreateCarIfNeeded`** (`TrainController.cs:691`) checks `_carLookup` and if the car already exists — *e.g.* re-place a Bardo car — strips its `IntegrationSet` and **calls `KeyValueObject.ApplyValues(descriptor.Properties)`** (note: `ApplyValues`, not `ResetData` — preserves keys not in descriptor). Pay attention to this dual path if you patch spawn.

---

## `Model.CarArchetype` (taxonomy)

`Model.Definition.CarArchetype` enum (`Definition/Model.Definition/CarArchetype.cs`):

```
Uncategorized, LocomotiveDiesel, LocomotiveSteam, Boxcar, Flat, Tank,
HopperOpen, Caboose, Tender, Gondola, Coach, Baggage
```

Helpers in `CarArchetypeExtensions.cs`:
- `IsLocomotive()` — true for the two `Locomotive*` values.
- `IsFreight()` — Boxcar, Flat, Tank, HopperOpen, Gondola. Caboose/Tender/Coach/Baggage are **not** freight.
- `IsMisc()` — Caboose only.
- `DisplayName()` — UI string. Throws on out-of-range — patches that add archetypes must extend this.
- `PlacerOrder()` — sort key for the placer UI (Steam=10, Diesel=20, Coach=30, Baggage=40, Caboose=50, Boxcar=60, Flat=70, Tank=80, HopperOpen=90, Gondola=100, Tender=9998, Uncategorized=9999). Throws on out-of-range.

### Behavioral implications of Archetype

The archetype switches surface at these vanilla sites:

| Site | What it controls |
|---|---|
| `TrainController.CreateCarRaw:732` | **Subclass selection.** Only `LocomotiveSteam`/`LocomotiveDiesel` get `SteamLocomotive`/`DieselLocomotive`. Everything else gets base `Car`. Tender included! |
| `Car.SetNominalBrakingRatio` (`Car.cs:1058`) | Brake force ratio multiplier per archetype: Coach/Baggage 0.9; Loco/Tender 1.0/0.8; freight 0.7 |
| `Car.WantsEndGear(end)` (`Car.cs:1761`) | Tender returns `false` for End.F (no front coupler). All other archetypes wear gear on both ends. **`virtual`** — overridable |
| `Car.RequiresConnectionToEnd` (`Car.cs:2741`) | Tender locks End.F to its engine — `ApplyEndGearChange` rejects unsetting. **`virtual`** |
| `Car.EnableOiling` (`Car.cs:793`) | Diesels exempt from oiling; everyone else needs oil if `OilFeature` is on. See [Wear › Oil](wear-durability.md#oil) |
| `Car.PostSetupComponentsHeadlights:1441` | Tender headlights mirror keyvalue from coupled engine via `KeyValueAdjacentCopier(end=F)` |
| `CarExtensions.IsPassengerCar` (`CarExtensions.cs:247`) | Loco/Caboose/Coach/Baggage AND has a `passengers` LoadSlot |
| `CarPropertyChanges.SupportsBleed` (`CarPropertyChanges.cs:13`) | Loco/Tender → false (no bleed valve in inspector) |
| `Car.CheckForHotbox:2095` | `IsLocomotive` skips hotbox roll. (Note: Tender is *not* a Locomotive — tenders DO get hotbox checks) |
| `CarInspector.PopulatePanel:144` | Tender hides the Operations tab |
| `CarInspector.PopulateOperationsPanel:325` | Only freight archetypes show Waybill UI |
| `DefinitionChecker` (`Model.Database/DefinitionChecker.cs`) | Freight requires `LoadSlots`; Tender requires exactly 2 (coal + water) + matching `LoadTargetComponent`s |
| `PrefabStoreExtensions.Random` | Industry car ordering filters by `CarTypeFilter` over `AllCarDefinitionInfos` then weighted by `CarSizePreference` |

**`Tender` is structurally a freight-with-extra-rules.** It has loadslots (coal+water), uses the base `Car` class (NOT a locomotive subclass), but `RequiresConnectionToEnd(End.F)` and `WantsEndGear(End.R)==true, End.F==false`. Splitting a tender from its engine is impossible without bypassing `ValidateEndGearChange`.

### Patch candidates (Archetype)

| Method | Why patch |
|---|---|
| `CarArchetypeExtensions.DisplayName` / `PlacerOrder` | Required overrides if you add a new archetype enum value; both throw on out-of-range. Use a Harmony prefix returning your value early |
| `TrainController.CreateCarRaw` | Inject custom `Car` subclasses based on archetype (e.g., a `MotorCarriage` class for self-propelled units that aren't conventional locomotives) |
| `Car.WantsEndGear` (`virtual`) | Mark `End.F` or `End.R` as no-end-gear in a subclass — preferred to patching |
| `Car.RequiresConnectionToEnd` (`virtual`) | Same — for cars that can't be uncoupled at one end |
| `CarArchetypeExtensions.IsFreight` / `IsLocomotive` | If you add an archetype, every conditional through these chains needs reconsideration |

---

## `Model.Car` (per-vehicle MonoBehaviour)

### Identity & ownership

```csharp
public string id;                                          // Car.cs:224  carId, allocated via IdGenerator.Cars
public string trainCrewId;                                 // Car.cs:226  who owns this car (crew assignment)
public TypedContainerItem<CarDefinition> DefinitionInfo;   // Car.cs:228  see car-definitions.md
public CarIdent Ident { get; private set; }                // Car.cs:477  ReportingMark + RoadNumber
public string Bardo { get; set; }                          // Car.cs:479  null when active; non-null = "in storage"
public bool IsInBardo => !string.IsNullOrEmpty(Bardo);     // Car.cs:481
public bool ghost;                                         // Car.cs:347  set by Setup if isGhost; disables colliders
public CarArchetype Archetype => Definition.Archetype;     // Car.cs:765  proxy through Definition
public CarDefinition Definition => DefinitionInfo.Definition; // Car.cs:483
public string CarType => Definition.CarType;               // Car.cs:485  AAR car-type string ("XM", "FB", etc.)
public string DisplayName { get; private set; }            // Car.cs:487  "<Mark> <RoadNumber>"
public string SortName { get; private set; }               // Car.cs:489  "<Mark> <CarTypeAbbrev> <Number,8>"
```

`SetIdent` (`Car.cs:3027`) is the only legitimate path to change Ident — emits `CarIdentChanged` Messenger event indirectly via observers in `CarInspector`.

`IsOwnedByPlayer` getter (`Car.cs:901`) reads KVO key `"owned"` (HostOnly write, BoolValue). This is the player-purchased flag.

### Weight model

```csharp
private float _loadWeight;                                 // Car.cs:337  cached sum of all load slots in pounds
public float Weight => (float)Definition.WeightEmpty + _loadWeight;  // Car.cs:738

private void UpdateLoadWeight()                            // Car.cs:1734
{
    _loadWeight = Enumerable.Range(0, Definition.LoadSlots.Count).Sum(slotIndex =>
    {
        CarLoadInfo? loadInfo = this.GetLoadInfo(slotIndex);
        if (!loadInfo.HasValue) return 0f;
        Load load = CarPrototypeLibrary.instance.LoadForId(loadInfo.Value.LoadId);
        if (load == null) return 0f;                       // unknown load → contributes zero
        return load.Pounds(value.Quantity);                // delegates to Load.Pounds (units-aware)
    });
    UpdateSwayMassCoeff();
}
```

`UpdateLoadWeight` is wired in `SetupKeyValueObject` (`Car.cs:1718-1725`):

```csharp
int count = Definition.LoadSlots.Count;
for (int num = 0; num < count; num++)
    Observers.Add(KeyValueObject.Observe($"load.{num}", _ => UpdateLoadWeight()));
```

**Subtle: observers are registered per slot at Setup time.** Adding load slots after Setup runs (e.g., via a Harmony patch on Definition.LoadSlots) won't auto-register an observer. Re-call `SetupKeyValueObject` or manually `KeyValueObject.Observe(...)` for new slots.

### `Load.Pounds(quantity)` weight conversion

`Model.Ops.Definition/Load.cs:50`:

```csharp
public float Pounds(float quantity) => units switch
{
    LoadUnits.Pounds   => quantity,
    LoadUnits.Gallons  => quantity * 0.133681f * density,    // ft³/gal × density(lb/ft³)
    LoadUnits.Quantity => quantity * unitWeightInPounds,     // discrete items × unit weight
};
```

`density` defaults to 62.4 lb/ft³ (water). `unitWeightInPounds` is per-Load asset.

### Per-car KVO key reference (cross-cutting cheat sheet)

`Car.SetupKeyValueObject` (`Car.cs:1642`) wires every observer. The full Car KVO surface:

| Key | Type | HostOnly? | Source / consumer |
|---|---|---|---|
| `_f.coupled` / `_r.coupled` | bool | **Yes** | [Couplers › state writes](couplers.md#state-writes-applyendgearchange-is-the-only-door) |
| `_f.airConnected` / `_r.airConnected` | bool | **Yes** | Couplers |
| `f.anglecock` / `r.anglecock` | float | Crew | Couplers |
| `f.cutLever` / `r.cutLever` | float | Crew | Couplers |
| `_condition` | float | **Yes** | [Wear › state](wear-durability.md#state-fields) |
| `_derailment` | float | **Yes** | Wear |
| `_odometer` (`KeyOdometerActual`) | float | **Yes** | Wear |
| `_odosvc` (`KeyOdometerService`) | float | **Yes** | Wear |
| `_lastOverhaul` | float | **Yes** | Wear |
| `_overhaulProg` | float (or Null) | **Yes** | Wear |
| `oiled` | float | **Yes** | Wear |
| `hotbox` | int (0/1) | **Yes** | Wear |
| `derailmentReason` | string | **Yes** (writes by host only) | Wear (audit string) |
| `load.{slotIndex}` | Dictionary{loadId,quantity} | Trainmaster | Cargo (this doc) |
| `ops.waybill` | Dictionary | Trainmaster | Operations |
| `ops.passengerMarker` | Dictionary | **Yes** (`HostPrefixes`) | Passenger ops |
| `ops.repair-dest` | string | Trainmaster | RepairTrack |
| `ops.sell-dest` | string | Officer | EquipmentPurchase |
| `ops.autodest.ld` / `ops.autodest.mt` | string | Trainmaster (`load.` prefix? no — direct) | Auto-waybill destinations |
| `owned` | bool | **Yes** | Player ownership |
| `_colorScheme`, `lettering.basic`, `whistle.custom` | various | Trainmaster | Customization |
| `door.*`, `gate.*` | various | Passenger | Coach interior controls |
| `headlight` (control key) | float | Crew | Locomotive controls |
| `handbrake`, `bleed`, `cutOut`, `mu` | various | Crew | Brake / loco controls |

Auth resolved by `Car.AuthorizationRequirementForPropertyWrite(key)` (`Car.cs:3112`) walking these prefix arrays in priority order:

```csharp
HostPrefixes        = ["_", "ops.passengerMarker", "owned", "oiled", "hotbox"]   // Car.cs:467
PassengerPrefixes   = ["door.", "gate."]                                          // Car.cs:469
TrainmasterPrefixes = ["load.", "ops.waybill", "ops.repair-dest",
                       "_colorScheme", "lettering.basic", "whistle.custom"]      // Car.cs:471
OfficerPrefixes     = ["ops.sell-dest"]                                           // Car.cs:473
```

Order of resolution in `AuthorizationRequirementForPropertyWrite`: Officer → Trainmaster → Passenger → Host → fallback `(Crew, trainCrewId)`. **Officer wins over Trainmaster wins over Host because Host is checked last** — so `_colorScheme` (matches `_`) would resolve as `Trainmaster` if the array didn't begin with `_colorScheme` first. (It does, so it works.) But careful — adding a new HostOnly prefix also matched by an earlier prefix list will be downgraded.

### Patch candidates (Car)

| Method | Why patch |
|---|---|
| `Car.Setup(carId, descriptor, prefabs, ghost)` | Inject mod state at spawn. Postfix runs after `ResetKeyValueProperties` is called by `CreateCarRaw`, so KVO is hot |
| `Car.SetupKeyValueObject` | Add additional KVO observers without subclassing |
| `Car.UpdateLoadWeight` | Inject mod-side load contributions (e.g., custom load types not in `CarPrototypeLibrary`) |
| `Car.Weight` (getter, **not virtual**) | Override base+load weight formula. Note: `Definition.WeightEmpty` is mutated by `ValidateDefinition` (clamped to ≥ 100 lb) |
| `Car.WillDestroy(bool)` | Cleanup hook; runs both for normal removal and Bardo move (use the bool to distinguish) |
| `Car.UnloadModels` (`virtual`) | Hook visual teardown; runs on culling distance change AND on destroy |
| `Car.PostSetupComponents(ComponentLifetime)` (`virtual`) | After component build per lifetime — ideal place to add MonoBehaviours that observe KVO |
| `Car.AuthorizationRequirementForPropertyWrite(key)` | Patch to add custom HostOnly/Trainmaster prefix arrays. Watch order — earlier prefix lists win |
| `TrainController.CreateCarRaw` | Switch base class for new archetypes; inject custom `SetupPrefabs` |
| `TrainController.WillRemoveCar` | Pre-cleanup hook — runs **before** `Destroy(car.gameObject)`; KVO still queryable |
| `TrainController.HandleSetBardo` | Customize Bardo move semantics |
| `Car.SetIdent` (`virtual`) | Re-name a car post-spawn. Observers in `CarInspector` rebuild on `CarIdentChanged` Messenger event |

### MP authority (Car)

| Operation | Auth | Site |
|---|---|---|
| Spawn (`AddCars` message) | **Host only** (`[HostOnlyAuthorizationRule]`) | `Game.Messages/AddCars.cs:7` |
| Despawn (`RemoveCars`) | Trainmaster (`[MinimumAccessLevel(Trainmaster)]`) | `Game.Messages/RemoveCars.cs:7` |
| Move to Bardo (`CarSetBardo`) | **Host only** | `Game.Messages/CarSetBardo.cs:6` |
| Set crew (`SetCarTrainCrew`) | Trainmaster | `Game.Messages/SetCarTrainCrew.cs:6` |
| Property writes | Per-key via `Car.AuthorizationRequirementForPropertyWrite` (HostOnly fallback) | `Car.cs:3112` |
| Damage application (`ApplyConditionDelta`, `ApplyDerailmentDelta`) | Implicit host-only — no request message exists | See [Wear › MP](wear-durability.md#mp-authority) |
| Loading (`Load`/`Unload` on `IOpsCar`) | **Implicitly host-only** — only `IndustryComponent.Service` calls these, and Service runs host-side | `Model.Ops/IndustryComponent.cs` (Service is the chokepoint) |
| Ident change | No request message exists; `SetIdent` is host-driven via `RequestCarSetIdent` (Trainmaster) | `Game.Messages/RequestCarSetIdent.cs` |
| Manual move (`ManualMoveCar`) | Trainmaster | `Game.Messages/ManualMoveCar.cs` |
| Oil request | `RequestOilCar` (Crew) | `Game.Messages/RequestOilCar.cs`, see [Wear](wear-durability.md#oil) |

**Clients cannot directly write `load.{n}`** — even though the prefix is `load.` (Trainmaster), in practice all writes flow through `IndustryComponent.Service` on the host. There is no client-side `Load`/`Unload` request message. Mods that want client-driven loading need to define one and route it through the host.

### Gotchas (Car)

- **Two `Car` constructor paths.** `Setup` runs once for new cars; for an existing-but-Bardo car coming back, `CreateCarIfNeeded` instead calls `KeyValueObject.ApplyValues(descriptor.Properties)` and `trainCrewId = descriptor.TrainCrewId` directly. Mod state seeded in `Setup` won't re-apply on Bardo return.
- **`ValidateDefinition` mutates the shared `Definition` reference.** It clamps `Length<1`, `TruckSeparation<1`, `WeightEmpty<100` and creates `Components` if null. Because `CarDefinition` is shared across all cars sharing the identifier, the first car triggers the clamp permanently. Patch `ValidateDefinition` if you want to inspect the *raw* definition values.
- **`_loadWeight` is computed only on `load.{n}` change.** Initial load is set during `Setup` via `descriptor.Properties` → `ResetKeyValueProperties`, which fires the observer. But if you call `UpdateLoadWeight` from outside, you might race with the coroutine-batched physics tick.
- **`carLength = CalculateCarLength()`** (`Car.cs:1008`) — this overrides `Definition.Length` post-setup. The `protected virtual` override (`SteamLocomotive` does this for total wheelset reach) means the actual length used for spacing/coupling can differ from `Definition.Length`. Use `car.carLength`, not `car.Definition.Length`, for runtime calculations.
- **`maxSpeedMph` is randomized per-car** in `FinishSetup` (`Car.cs:1960`): `UnityEngine.Random.Range(75, 85)`. There is **no definition-driven max speed** — every car gets a random value in this range. Hardcoded.
- **Material instances are made unique per car** in `MakeMaterialsUnique` (`Car.cs:1236`). Do NOT mutate `Definition.Components.OfType<DecalComponent>` materials — they're cloned. Patch `MakeMaterialsUnique` to intercept the cloning.
- **`ApplyConditionDelta` does not check `WearFeature`** — see [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).
- **Cars in Bardo retain their `id` and KVO state.** They're absent from `_carCuller` and `_integrationSets` but `_carLookup[id]` still points at them. `CarForId(id)` returns the Bardo car. UI panels can break if they expect `LocationA.IsValid` on an in-Bardo car.
- **`Snapshot` writes `unusedKey3: false`** — there's a deprecated boolean slot in the snapshot record. Don't reuse it.
- **`_atRestSince` and `_velocityZeroTime` reset on coupling change.** `HandleCoupledChange` calls `ResetAtRest()` when `isCoupled` becomes false. Holding a stale `IsAtRest` reference across coupling events is wrong.
- **Visibility ≠ active.** `SetVisible(bool)` only toggles renderer.enabled — colliders, KVO, physics still run on invisible cars. Bardo cars are SetVisible(false) but their gameObject is also disabled implicitly (no model loaded; body GameObject doesn't exist).

---

## `Model.CarDescriptor` (spawn argument struct)

```csharp
public struct CarDescriptor(                                       // Model/CarDescriptor.cs:8
    TypedContainerItem<CarDefinition> definitionInfo,
    CarIdent ident = default,
    string bardo = null,
    string trainCrewId = null,
    bool flipped = false,
    Dictionary<string, Value> properties = null)
{
    public readonly TypedContainerItem<CarDefinition> DefinitionInfo;
    public CarIdent Ident;
    public readonly string Bardo;
    public readonly string TrainCrewId;
    public bool Flipped;                                   // descriptor.Flipped → !FrontIsA
    public readonly Dictionary<string, Value> Properties;  // initial KVO seed
}
```

`CarDescriptor` is the carrier from snapshot or scripted spawn into `CreateCarRaw`. The properties dict bootstraps the KVO; **everything that you can express in KVO must be set via `properties` here, not via post-spawn writes**, because the post-spawn writes will likely be lost across save/reload unless you persist them via the same KVO key.

**Initial slot contents** (`TrainController.cs:670` `ApplyInitialSlotContents`) injects `load.{i}` for slots with a `RequiredLoadIdentifier` if not already present in `Properties`, scaled by `initialFuelWaterPercent` (default 1.0 in `PlaceTrain`). This is how locomotives placed via the Placer arrive with full coal/water.

`Car.Descriptor()` (`Car.cs:1033`) reverses the operation — produces a CarDescriptor from current state for snapshot/save.

---

## `Model.CarIdent` (display id)

```csharp
public struct CarIdent(string reportingMark, string roadNumber)   // Model/CarIdent.cs:5
```

Plain (Mark, Number) tuple. `Equals`/`GetHashCode` defined.

`RoadNumberAllocator` (`Model/RoadNumberAllocator.cs`) holds one per `ReportingMark.ToUpper()`. `AllocateRoadNumber` (`TrainController.cs:2249`) picks a number based on `Definition.BaseRoadNumber` and the descriptor's existing number; `forceSequential = descriptor.Properties["owned"]` (player-bought cars allocate sequential numbers). `DeallocateRoadNumber` releases on remove.

---

## Cargo / loading

### Storage shape

Per-car KVO key `load.{slotIndex}` holds `CarLoadInfo` as a Dictionary `Value`:

```csharp
public struct CarLoadInfo(string loadId, float quantity)            // Model.Ops/CarLoadInfo.cs:6
{
    public string LoadId;     // matches Load.id (= the ScriptableObject .name)
    public float  Quantity;   // in slot's LoadUnits

    public Value AsPropertyValue =>
        Value.Dictionary({"loadId": Value.String(LoadId), "quantity": Value.Float(Quantity)});

    public static CarLoadInfo? FromPropertyValue(Value v) { ... }   // null if not Dictionary
}
```

A null/empty value at `load.{slot}` means **slot empty**. Setting quantity to 0 via `OpsCarAdapter.Unload` writes `null` (`SetLoadInfo(i, null)` when `num < 0.001f`).

### `Load` ScriptableObject

`Model.Ops.Definition.Load` (`Model.Ops.Definition/Load.cs`) is a `ScriptableObject` with `[CreateAssetMenu]`:

```csharp
public string description;                                 // display name
public LoadUnits units;                                    // Pounds | Gallons | Quantity
public float density = 62.4f;                              // for Gallons → Pounds
public float unitWeightInPounds;                           // for Quantity → Pounds
public bool importable = true;                             // can be sourced off-railroad
public float payPerQuantity;                               // non-importable payment per unit
public float costPerUnit;                                  // orderable load cost (e.g., coal at 1500/ton)

public string id => base.name;                             // Load.id == the .asset filename without extension
```

`NominalQuantityPerCarLoad`: 100000 (Pounds), 8000 (Gallons), 3 (Quantity). `ZeroThreshold`: 0.1 / 0.01 / 0.001. The constant `ZeroDeltaThreshold = 1e-7f`.

**Load assets live in the StreamingAssets-side resources, registered to the project-global `CarPrototypeLibrary` ScriptableObject.** See [Car Definitions › Loads](car-definitions.md#load-scriptableobject-the-cargo-catalog).

### `CarPrototypeLibrary`

```csharp
public class CarPrototypeLibrary : ScriptableObject              // Model/CarPrototypeLibrary.cs:7
{
    public Load[] opsLoads;
    public static CarPrototypeLibrary instance;
    public Load LoadForId(string loadId) { ... linear scan ... }
}
```

Single static instance set in `TrainController` early init (`TrainController.cs:382`: `CarPrototypeLibrary.instance = carPrototypeLibrary;`). The asset is referenced as a serialized field on `TrainController`. This is the **only** path from `loadId` (string) to `Load` (ScriptableObject). `LoadForId` is a linear scan with O(n).

### `CarExtensions` — the public load API

`Model.Ops/CarExtensions.cs:22-66`:

```csharp
public static CarLoadInfo? GetLoadInfo(this Car car, int slot);
public static void          SetLoadInfo(this Car car, int slot, CarLoadInfo? info);
public static CarLoadInfo? GetLoadInfo(this Car car, string loadId, out int slotIndex);
public static bool          IsLoadEmpty(this Car car);
public static (float quantity, float capacity) QuantityCapacityOfLoad(this Car car, Load load);
public static string KeyForLoadInfoSlot(int slot) => $"load.{slot}";
```

`SetLoadInfo` writes the KVO key — so it goes through normal auth (`load.` prefix → Trainmaster). Calling from a client without trainmaster will be rejected.

`GetLoadInfo(loadId, out slotIndex)` first searches by `RequiredLoadIdentifier`, then by actual stored `LoadId`. The dual scan covers both "this slot is dedicated to coal" and "this slot happens to contain coal."

### `OpsCarAdapter` — the `IOpsCar` interface

`Model.Ops/OpsCarAdapter.cs` adapts `Car` to `IOpsCar` for industry use:

```csharp
public float Load(Load load, float quantityToLoad)               // OpsCarAdapter.cs:113
{
    foreach slot:
        if loadSlot.LoadRequirementsMatch(load) && loadSlot.LoadUnits == load.units:
            // empty slot → CarLoadInfo(load.id, clamped quantity)
            // existing slot with matching loadId → quantity += delta clamped
            // returns the *amount actually added* (≤ quantityToLoad)
    return 0f;  // no matching slot
}

public float Unload(Load load, float quantityToUnload)            // OpsCarAdapter.cs:79
{
    foreach slot:
        if existing slot has matching loadId:
            // quantity -= clamped delta
            // if final < 0.001f → SetLoadInfo(i, null)  // empties the slot
    return 0f;
}

public bool IsEmptyOrContains(Load load)                          // OpsCarAdapter.cs:54
public (float, float) QuantityOfLoad(Load load) => car.QuantityCapacityOfLoad(load);
public bool IsFull(Load load) { var (q, cap) = QuantityOfLoad(load); return Mathf.Abs(q - cap) < 0.001f; }
```

`LoadSlotExtensions.LoadRequirementsMatch` (`LoadSlotExtensions.cs:8`):
- `RequiredLoadIdentifier` empty → matches any load.
- Otherwise → must equal `load.id`.

**`Load` checks both `LoadRequirementsMatch` AND `LoadUnits == load.units`. `Unload` does NOT check units** — it matches by `LoadId` only. Units mismatch can sneak in through scripted spawn/save corruption.

### Industry loading pipeline

Host-side, in `IndustryComponent.Service` tick:

```
IndustryLoader.Service(ctx):                                      // IndustryLoader.cs:24
   ctx.AddToStorage(load, productionRate * dt, maxStorage);       // produce
   foreach (IOpsCar car in EnumerateCars(requireWaybill=true)
                            .Where(IsEmptyOrContains(load))
                            .OrderByDescending(QuantityOfLoad(load).quantity))
   {
       float qty = Min(storage, carLoadRate * dt);
       float actuallyLoaded = car.Load(load, qty);                 // ← KVO write
       if (car.IsFull(load))
           ctx.OrderAwayLoaded(car) || car.SetWaybill(null, "Full");
       ctx.RemoveFromStorage(load, actuallyLoaded);
   }

IndustryUnloader.Service(ctx):                                    // IndustryUnloader.cs:49
   foreach (IOpsCar car in EnumerateCars(requireWaybill=true)
                            .Where(IsEmptyOrContains(load))
                            .OrderBy(QuantityOfLoad(load).quantity))
   {
       float room = maxStorage - storedQty;
       float actuallyUnloaded = car.Unload(load, Min(carUnloadRate*dt, room));
       if (car.IsEmpty(load))
           ctx.OrderAwayEmpty(car) || car.SetWaybill(null, "Empty completed");
       ctx.AddToStorage(load, actuallyUnloaded);
   }
   ctx.RemoveFromStorage(load, storageConsumptionRate * dt);     // consume produced load
```

`carLoadRate` and `carUnloadRate` are `units per game-day`. `IndustryComponent.RateToValue(rate, dt)` is the standard `(rate / DayLengthSeconds) * dt` conversion.

`EnumerateCars` (`IndustryComponent.cs`) walks cars currently on the industry's TrackSpans, filtered by `carTypeFilter`. `requireWaybill=true` gates loading to cars with valid waybills pointing to this industry — bypasses the order-empty path.

### Player-driven loading: `CarLoadTargetLoader`

`RollingStock/CarLoadTargetLoader.cs` is a host-side coroutine attached to a placed loader prefab (NOT defined in `CarDefinition.Components` — placed via map authoring). When `canLoadBoolKey` is set:

```
LoadLoop():                                                       // CarLoadTargetLoader.cs:100
   while (CanLoad):
       Find car at point (within radius)
       Match a CarLoadTarget MonoBehaviour on the car (slotIndex + radius)
       Increment that slot's quantity by outputRate per tick
       Drain from sourceIndustry.Storage if non-null
```

`CarLoadTarget` MonoBehaviour (`RollingStock/CarLoadTarget.cs`) has `slotIndex` and `radius` — added to a car by `LoadTargetComponent` at model setup. See [Car Definitions › LoadTargetComponent](car-definitions.md#loadtargetcomponent).

`CarContentSwapper` (`RollingStock/CarContentSwapper.cs`) is the industry-attached version of the same pattern — looks for a car within range and dumps `outputContent` into matching slots. Less general than `CarLoadTargetLoader`.

### Visual loading: `CarLoadModelController` and friends

`RollingStock/CarLoadModelController.cs` observes `load.{slotIndex}` and instantiates/destroys load model GameObjects to match the percentage full. See [Car Definitions › LoadModelComponent](car-definitions.md#loadmodelcomponent--carloadmodelcontroller) for full details.

`CarLoadAnimator` (referenced from `LoadAnimationComponent`) animates the load shape via an animation clip parameterized by load percentage. Used for fluid-fill animations.

`AggregateLoadModelController` (`RollingStock.LoadModels/`) is the bulk-cargo (coal pile, gravel) renderer — instances are arrayed across keyframe positions based on percent-full.

### Patch candidates (Cargo)

| Method | Why patch |
|---|---|
| `CarExtensions.SetLoadInfo` | Single chokepoint for slot writes. Postfix to log/emit events, prefix to validate or veto |
| `CarExtensions.GetLoadInfo` | Single chokepoint for reads — patch to inject synthetic loads (e.g., transparent storage) |
| `Car.UpdateLoadWeight` | Customize load → weight conversion. **Reads `CarPrototypeLibrary.instance` directly** — patches that add custom Loads must add them there |
| `OpsCarAdapter.Load` / `Unload` | Per-call hooks for industry-driven loading. Prefer this over patching `IndustryLoader.Service` (multiple subclasses) |
| `Load.Pounds(quantity)` | Replace per-load weight formula globally. `density` and `unitWeightInPounds` are public fields on the SO — direct mutation also works |
| `IndustryLoader.Service` / `IndustryUnloader.Service` | Per-tick load logic. Patch to add custom rate modifiers |
| `LoadSlotExtensions.LoadRequirementsMatch` | Add fuzzy matching (e.g., "any liquid") |
| `CarPrototypeLibrary.LoadForId` | Linear scan; replace with dict for perf, OR patch to register mod-loaded `Load` SOs at runtime |
| `CarLoadModelController.LoadChanged` | Visual load model swap; intercept to add custom load visuals |

### Gotchas (Cargo)

- **`load.{slotIndex}` writes Trainmaster auth.** A Crew-level player cannot manually write a load via the KVO API. Loading via `IndustryComponent.Service` works because Service runs on the host.
- **`CarPrototypeLibrary.instance` is `null`** until `TrainController.Awake` sets it (`TrainController.cs:382`). `UpdateLoadWeight` early in initialization may NPE if the prototype library isn't ready. Vanilla works because `Setup` runs after TrainController init, but mod-side scripted spawns can race.
- **`Load.id == base.name`** — the ScriptableObject's name in the project. Renaming the asset breaks every car loaded with the old id; the load silently becomes "unknown" and contributes 0 weight (with an error log).
- **`OpsCarAdapter.Load` requires units to match.** `Load(coalLoad, 100f)` on a slot with `LoadUnits.Gallons` skips that slot, returns 0. `Unload` doesn't check units — fragile asymmetry.
- **`IsLoadEmpty` is a per-slot scan; `Load.ZeroThreshold` is NOT used.** Hard-coded `quantity > 0.001f`. A slot with 0.0005 lbs of coal is "empty" under `IsLoadEmpty` but `OpsCarAdapter.Load` may treat it differently.
- **`Quantity` uses 0.001f as the "wipe to null" threshold** in `Unload`. So unloading exactly to a tiny remainder writes `null`, but loading TO a tiny remainder retains the dictionary entry. Save/load round-trip can drift the "is empty" flag.
- **`load.{n}` keys can have indices beyond `LoadSlots.Count`.** Observers are only registered for valid indices — orphan keys (from a definition change that removed slots) are inert and waste space until cleaned up.
- **Initial slot contents only fill `RequiredLoadIdentifier` slots.** Free slots (no required identifier) start empty, even when `initialFuelWaterPercent < 1.0`.
- **`CarPrototypeLibrary.AutoPopulate()` is empty** (`CarPrototypeLibrary.cs:13`) — looks like a scaffolding method but does nothing. Don't rely on it.

---

## Crew, owner, and waybill assignment

### `trainCrewId`

Plain `string` field on `Car` (`Car.cs:226`). Set during `Setup` from `descriptor.TrainCrewId`. Mutated only by:
- `CreateCarIfNeeded` for re-place from Bardo (direct write, `TrainController.cs:702`).
- `SetCarTrainCrew` message (Trainmaster auth, `Game.Messages/SetCarTrainCrew.cs`).

`TryGetTimetableTrainCrewId` (`CarExtensions.cs:271`) walks coupled cars to find a crew id — useful for "what train is this consist running as?"

`CarTrainCrewChanged` Messenger event signals crew assignment changes (subscribed in CarInspector).

### Player ownership

KVO key `"owned"` (bool, HostOnly). `IsOwnedByPlayer` getter at `Car.cs:901`. Set by `EquipmentPurchase`-related code paths. `KeyOwned = "owned"` constant exposed at `Car.cs:455`.

### Waybills

`Waybill?` lives at `Car.Waybill` (auto-property, `Car.cs:903`), recomputed in `UpdateWaybill` (`Car.cs:3094`) which observes the `ops.waybill` KVO key. `Waybill.FromPropertyValue` deserializes from the dictionary; bad waybills produce a logged warning and `Waybill = null` (no exception escapes).

`CarExtensions.SetWaybill` (`CarExtensions.cs:143`) writes the KVO key directly — so it requires Trainmaster auth. `SetWaybillAuto` (`CarExtensions.cs:156`) falls back to auto-destination from `ops.autodest.ld` / `ops.autodest.mt` when no waybill is supplied.

`CycleAutoWaybill` (`CarExtensions.cs:171`) flips between load and unload destinations. UI button "<sprite name=CycleWaybills>" in `CarInspector.PopulateSetWaybillPanel`.

---

## `UI.CarInspector.CarInspector` (the vanilla floating window)

`UI.CarInspector/CarInspector.cs` (745 lines). Singleton, `RequireComponent(Window)`. `IBuilderWindow` lets it use `UIBuilder` directly.

### Tabs

`PopulatePanel` (`CarInspector.cs:133`) builds a tabbed panel with these tabs:

| Tab | Visible when | Method |
|---|---|---|
| `Car` (always) | always | `PopulateCarPanel` |
| `Equipment` (always) | always | `PopulateEquipmentPanel` |
| `Passenger` | `_car.IsPassengerCar()` | `PopulatePassengerCarPanel` |
| `Operations` | `_car.Archetype != Tender` | `PopulateOperationsPanel` |

### KVO bindings

Inspector subscribes to `ops.waybill` to rebuild the Operations panel (`CarInspector.cs:147`). Other panels rely on `UIPanelBuilder.Frequency.Fast` polling for live air pressure / brake state, and on `KeyValueObject.Observe(key, action)` for state changes that trigger field rebuilds.

The `BuilderExtensions` family (`UI.CompanyWindow/BuilderExtensions.cs`) provides reusable widgets:
- `AddConditionField(car)` — observes `_condition` KVO, animates a colored bar
- `AddMileageField(car)` — observes `_odometer`, `_odosvc`, `_lastOverhaul`, `_overhaulProg`
- `AddRepairDestination(car)` — observes `ops.repair-dest`
- `AddSellDestination(car)` — observes `ops.sell-dest`
- `AddTrainCrewDropdown(car)` — uses the `SetCarTrainCrew` request message

Patches that add custom fields to the inspector should hook `CarInspector.PopulatePanel` postfix and call `tabBuilder.AddTab(...)` or extend an existing panel via a `Postfix` on the panel-builder methods.

### Patch candidates (CarInspector)

| Method | Why patch |
|---|---|
| `CarInspector.Populate(Car)` | Re-render the entire window. Postfix to wire additional observers |
| `CarInspector.PopulatePanel` | Add tabs |
| `CarInspector.PopulateCarPanel` / `PopulateEquipmentPanel` | Inject mod fields |
| `CarInspector.Show(Car)` static | Static entrypoint — patch to observe selection |

### Gotchas (CarInspector)

- **The inspector is a singleton**, found at first call to `Show` via `FindObjectOfType`. Re-entrant calls overwrite the displayed car.
- **`CanCustomize` returns "why not" on false** — `out reason` lets the inspector show a disabled-with-tooltip button. Mods that gate customization should follow the same convention.
- **`PopulateOperationsPanel` is suppressed for tenders** but still works for cabooses, coaches, and locomotives — locomotives can have waybills via Sandbox.

---

## Cross-references

- Component definitions, the build pipeline, asset packs, and definition editing — see [Car Definitions](car-definitions.md).
- Car condition, oiling, hotbox, and repair industry — see [Wear & Durability](wear-durability.md).
- Coupling, slack, derail-induced disconnects, collision damage — see [Couplers](couplers.md).
- Brake-line pressure, handbrakes, bleed valves — *(brakes crib sheet TBD)*.
- Consist movement and integration sets — *(consist-integration crib sheet TBD)*; see also [physics-vanilla-survey.md](../physics-vanilla-survey.md).
- The data-driven content pattern (definitions in JSON + asset bundles) parallels how scenery, materials, and trucks work — see [map-mods-vanilla-survey.md](../map-mods-vanilla-survey.md) for the broader pattern.
