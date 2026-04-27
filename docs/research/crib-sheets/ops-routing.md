# Ops Routing — Switch Lists, Areas, Waybill Lifecycle — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Industries & Ops](industries-ops.md), [Players & TrainCrew](players-traincrew.md), [Multiplayer Core](multiplayer-core.md), [Progression](progression.md), [Economy](economy.md)

The "ops routing" layer is the glue between freight cars and where they ought to go. Three loosely-coupled mechanisms cooperate: **waybills** (the per-car freight contract that drives payment), **auto-destinations** (a per-car fallback route used when no real waybill exists, paid 0), and **override destinations** (a parallel routing channel that wins for navigation but doesn't replace the underlying waybill — currently used only for repair). Above the per-car layer sit two consumer abstractions: **`Area`** is a Unity-scene grouping of `Industry` GameObjects used for spatial queries (station-agent panels, sweep, "cars in this area"), and **`SwitchList`** is a per-train-crew curated list of cars-of-interest that survives client/host roundtrips through the only TrainCrew-routed message in vanilla (`SwitchListUpdate`). Waybill lifecycle is host-driven (industries pay on `OnCompleteWaybill`, the daily rollover scores performance, `Section.ApplyCompleted` rewrites prefixes globally on progression unlocks), but a few mutations are explicitly client-allowed (auto-destination cycling, switch list edits) via the standard KVO + message auth model.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `OpsController.SwitchListController` | `Model.Ops/OpsController.cs:70` | Component reference; one per session, owned by `OpsController` |
| `Model.Ops.SwitchListController` | `Model.Ops/SwitchListController.cs:13` | Host-side per-crew switch list store + UI dispatch |
| `Game.Messages.SwitchList` (struct) | `Game.Messages/SwitchList.cs:7` | Wire payload: `List<Entry>`, `Entry { string CarId }` |
| `Game.Messages.SwitchListUpdate` | `Game.Messages/SwitchListUpdate.cs:8` | **`[HostOnlyAuthorizationRule]`** push to crew members; only TrainCrew-routed message in vanilla |
| `Game.Messages.SwitchListSetCarIds` | `Game.Messages/SwitchListSetCarIds.cs:9` | Crew→host replace list |
| `Game.Messages.SwitchListToggleCarIds` | `Game.Messages/SwitchListToggleCarIds.cs:9` | Crew→host add/remove |
| `Game.Events.SwitchListDidChange` | `Game.Events/SwitchListDidChange.cs:6` | Empty-payload Messenger struct fired on receiver after `SwitchListPanel.Refresh` |
| `Model.Ops.Area` | `Model.Ops/Area.cs:7` | Per-Industry-grouping MonoBehaviour; `Industries`, `Contains(position)`, `Contains(Vector3)` |
| `OpsController.Areas` | `Model.Ops/OpsController.cs:66` | `GetComponentsInChildren<Area>()` — uncached |
| `MapFeature.areasEnableOnUnlock` | `Game.Progression/MapFeature.cs:34` | Progression hook: which areas' industries unlock with this feature |
| `Model.Ops.Waybill` (struct) | `Model.Ops/Waybill.cs:8` | `Created/Origin/Destination/Payment/Completed/Tag/GraceDays`; KVO `ops.waybill` |
| `OpsController.RewriteWaybills(from, to)` | `Model.Ops/OpsController.cs:947` | **Global** prefix rewrite of waybills + autodest, host-side |
| `CarExtensions.CycleAutoWaybill` | `Model.Ops/CarExtensions.cs:171` | Toggle between empty/load auto destinations; payment 0, tag `"autodest"` |
| `CarExtensions.SetWaybillAuto` | `Model.Ops/CarExtensions.cs:156` | Auto-fallback used when `SetWaybill(null)` is requested |
| `OverrideDestinationExtensions` | `Model.Ops/OverrideDestinationExtensions.cs` | Override-destination KVO `ops.repair-dest`; coexists with waybill |
| `IndustryComponent.OnCompleteWaybill` | `Model.Ops/IndustryComponent.cs:172` | The pay-and-mark-completed call (see [industries-ops](industries-ops.md#waybill-completion-default)) |
| `Section.ApplyCompleted` | `Game.Progression/Section.cs:80` | Calls `InterchangeTransfer.Apply` → `OpsController.RewriteWaybills` |

---

## Spine: lifecycle of a freight waybill

```
1. SPAWN  (Interchange.ServeInterchange, host-only)
   IndustryContext.CreateCarDescriptorForOrder(...)
       → constructs Waybill(now, origin=spawning IC, dest=order.Destination,
                             payment, completed=false, tag, graceDays)
       → set in CarDescriptor → flushed into car.KeyValueObject["ops.waybill"]

2. IN-TRANSIT  (no per-tick state; clients see KVO)
   - Players read via Car.Waybill / OpsControllerExtensions.TryGetDestinationInfo
   - SwitchList may include the car (per-crew curation, separate channel)
   - OverrideDestination.Repair may take precedence for routing display

3. ARRIVE  (Industry.TickCoroutine -> IndustryComponent.CheckForCompleted)
   - IC enumerates cars at its trackSpans (filtered to ≤5cm/s)
   - For each car whose waybill.Destination.Equals(this) and !Completed:
       OnCompleteWaybill(ctx, car, wb)
         → ctx.PayWaybill(...) [tier bonus + condition fine + timely bonus]
         → wb.PaymentOnArrival = 0
         → wb.Completed = true
         → car.SetWaybill(wb)   [Crew-allowed KVO write; host-side here]
         → Industry.ReceivedCarCount++

4. POST-ARRIVAL  (host)
   - Daily rollover: Industry.UpdatePerformance reads ages of completed waybills
   - SwitchList shows car as "Completed" (strikethrough) until manual cleanup
   - OpsController.RemoveCar(carId) clears the car from every crew's switch list
     when the car itself is removed (TrainController.RemoveCar -> :1544)

5. NEXT WAYBILL  (host re-orders the car)
   - For loaders: OrderAwayLoaded(car) → OpsController.WaybillCarToInterchange
     → InterchangeSelector picks return interchange → fresh Waybill written
   - For unloaders: OrderAwayEmpty(car) → same path
   - Client-driven: CycleAutoWaybill writes a *zero-pay* "autodest" waybill

6. PROGRESSION REWIRE  (cross-cutting, host-side, on Section unlock)
   Progression.RecomputeOpenSection -> Section.ApplyCompleted
     -> InterchangeTransfer.Apply for each child IT
       -> OpsController.RewriteWaybills(fromPrefix, toPrefix)
         -> walks ALL TrainController.Cars, rewrites Waybill.Origin,
            Waybill.Destination, autodest empty/load whose Identifier
            StartsWith(fromPrefix), with Replace(fromPrefix, toPrefix)
```

The state machine is **flat**: there's no enum, no `WaybillState`, no transitions table. The lifecycle is encoded in two booleans (`Completed`) and one int (`PaymentOnArrival` zeroed-on-pay), plus the existence of the `ops.waybill` KVO value itself. **`Completed=true && PaymentOnArrival>0` is unreachable in vanilla** because `OnCompleteWaybill` zeros payment in the same call that flips `Completed`.

---

## `Model.Ops.Waybill` — the data structure

```csharp
public struct Waybill(GameDateTime created,
                      OpsCarPosition? origin,           // nullable; spawn IC, or null for autodest/sandbox
                      OpsCarPosition  destination,      // mandatory
                      int             paymentOnArrival, // base payment; 0 for autodest/progression/sell
                      bool            completed,
                      string          tag,              // see "Tags" table in industries-ops.md
                      int             graceDays)        // 0/1/2 by distance (CalculateGraceDays)
```

Source: `Model.Ops/Waybill.cs:8`.

### KVO storage

| Key | Owner | Auth | Notes |
|---|---|---|---|
| `ops.waybill` | `Car.KeyValueObject` | default = `MinimumLevelTrainmaster` + train-crew check (no `_` prefix) | The Waybill itself — see `PropertyValue`/`FromPropertyValue` round-trip |
| `ops.autodest.ld` | `Car.KeyValueObject` | same | String identifier of `OpsCarPosition` for loaded auto-route |
| `ops.autodest.mt` | `Car.KeyValueObject` | same | Same, for empty auto-route |
| `ops.repair-dest` | `Car.KeyValueObject` | default | Override destination (string id or `{id, tag}` dict) |

`Waybill.PropertyValue` (`Waybill.cs:24`) serializes the struct as a `Value.Dictionary` with keys `created`, `originId`, `destId`, `paymentOnArrival`, `completed`, `tag`, `graceDays`. Optional fields (`tag`, `graceDays`) write `Value.Null()` when empty/zero — `FromPropertyValue` (`:61`) tolerates missing keys for `tag` and `graceDays` (legacy save migration).

**`Waybill.Tag` is `readonly`.** To change the tag, you must construct a new `Waybill` (because struct field is readonly, value-type assignment elsewhere can't mutate it). All other fields are mutable.

### Snapshot/save format

Waybills are part of the per-car KVO blob, persisted via `_propertyObjectManager.PopulateForSave` → `Snapshot.Properties[carId]["ops.waybill"]`. **There is no separate top-level snapshot section for waybills.** Restore round-trips through `Waybill.FromPropertyValue(value, IOpsCarPositionResolver)` where the resolver is `OpsController` (`OpsController.ResolveOpsCarPosition`).

Switch lists *do* have a top-level snapshot section: `Snapshot.SwitchLists : Dictionary<string, SwitchList>` (`Snapshot.cs:188`, MessagePack key `"switchLists"`). Restored host-side via `OpsController.RestoreSwitchLists` → `SwitchListController.RestoreSwitchLists` (`SwitchListController.cs:189`), then immediately rebroadcast to the local player's crew via `SwitchListPanel.Refresh` (`StateManager.cs:1247`).

### `ConditionFineForCarCondition`

```csharp
public int ConditionFineForCarCondition(float condition) {                     // Waybill.cs:75
    float t = Mathf.InverseLerp(0.95f, 0f, condition);
    return Mathf.FloorToInt((float)PaymentOnArrival * Mathf.Lerp(0f, 0.75f, t));
}
```

Condition ≥ 0.95 → no fine. Condition 0 → 75% of payment. Linear ramp between. Computed at pay time inside `IndustryContext.PayWaybill`. See [industries-ops § OnCompleteWaybill](industries-ops.md#waybill-completion-default) for the full payment formula.

### Patch candidates

| Method | Why patch |
|---|---|
| `Waybill.FromPropertyValue` (static) | Add or migrate custom dictionary fields. Both host and client deserialize. |
| `Waybill.PropertyValue` (getter) | Add custom fields to wire. Coordinate host+client. |
| `Waybill.ConditionFineForCarCondition` | Replace damage→fine schedule. |
| `CarExtensions.SetWaybill` (`CarExtensions.cs:143`) | Single static for the KVO write. Wrap to log every waybill mutation. |

### MP authority

- Anyone with default Trainmaster + train-crew access can write `ops.waybill`. In practice host-driven `IndustryComponent.OnCompleteWaybill` and `OpsController.WaybillCarToInterchange` are the only "real" writers; client side only writes via `CycleAutoWaybill`.
- `Completed=true` is a host-side convention — no enforcement at KVO level. A misbehaving Trainmaster client could mark a waybill complete and pay would never trigger (the IC's `CheckForCompleted` short-circuits on `Completed`). No validation patches in vanilla.

---

## `CarExtensions` auto-destination + waybill helpers

```csharp
public const string TagAutodest = "autodest";          // CarExtensions.cs:18
public const string TagSell     = "sell";              // :20

public static Waybill? GetWaybill(this Car car, IOpsCarPositionResolver _) => car.Waybill;        // :125
public static void     SetWaybill(this Car car, Waybill? waybill);                                 // :143
public static void     SetWaybillAuto(this Car car, Waybill? waybill, IOpsCarPositionResolver);    // :156
public static void     ApplyAutoWaybillIfNeeded(this Car car, IOpsCarPositionResolver);            // :148
public static void     CheckWaybill(this Car car, IOpsCarPositionResolver);                        // :130
public static void     CycleAutoWaybill(this Car car, IOpsCarPositionResolver);                    // :171

public static OpsCarPosition? GetAutoDestination(this Car car, AutoDestinationType, resolver);    // :199
public static bool            SetAutoDestination(this Car car, AutoDestinationType, OpsCarPosition?); // :218
```

### `AutoDestinationType` (the only routing enum)

```csharp
public enum AutoDestinationType { Load, Empty }    // Model.Ops/AutoDestinationType.cs
```

Two values. `Load` → KVO key `ops.autodest.ld`. `Empty` → `ops.autodest.mt`. Selection is implicit via `Car.IsLoadEmpty()` in `SetWaybillAuto`.

### `SetWaybillAuto` decision tree

```csharp
public static void SetWaybillAuto(this Car car, Waybill? waybill, IOpsCarPositionResolver resolver) {
    if (!waybill.HasValue) {
        AutoDestinationType type = car.IsLoadEmpty() ? AutoDestinationType.Empty : AutoDestinationType.Load;
        OpsCarPosition? autoDest = car.GetAutoDestination(type, resolver);
        if (autoDest.HasValue)
            waybill = new Waybill(TimeWeather.Now, null, autoDest.Value, 0, false, "autodest", 0);
    }
    car.SetWaybill(waybill);                                            // can be null!
}
```

Called from `OpsCarAdapter.SetWaybill` (when `IndustryComponent.OnCompleteWaybill`-equivalent paths pass `null`) and `OpsController.CheckWaybills`/`ReturnWaybillsFrom` (player-owned cars). **If the autodest of the requested type isn't set, the waybill becomes `null` and the car has no destination.** No fallback to the other type.

### `CycleAutoWaybill` behaviour

```csharp
public static void CycleAutoWaybill(this Car car, IOpsCarPositionResolver resolver) {            // :171
    Waybill? existing = car.GetWaybill(resolver);
    OpsCarPosition? mt   = car.GetAutoDestination(AutoDestinationType.Empty, resolver);
    OpsCarPosition? ld   = car.GetAutoDestination(AutoDestinationType.Load,  resolver);
    OpsCarPosition? next =
        (mt.HasValue && ld.HasValue) ? (ExistingEquals(mt.Value) ? ld.Value : mt.Value)
                                     : (mt.HasValue ? (ExistingEquals(mt.Value) ? null : mt.Value)
                                                    : (ld.HasValue ? (ExistingEquals(ld.Value) ? null : ld.Value)
                                                                   : (OpsCarPosition?)null));
    Waybill? waybill = next.HasValue
        ? new Waybill(TimeWeather.Now, null, next.Value, 0, false, "autodest", 0)
        : (Waybill?)null;
    car.SetWaybill(waybill);
}
```

**Three-state cycle:** if both `Load` and `Empty` autodests are set, alternates between them. If only one is set, toggles between "that destination" and "no waybill at all" (clearing). If neither is set, always clears.

`OpsControllerExtensions.CycleAutoWaybill(this OpsController, Car car, IEnumerable<Car> targets = null)` (`OpsControllerExtensions.cs:48`) is the multi-car "cycle coupled cars too" wrapper — only mirrors waybill onto coupled cars whose autodests *match* the leader's pre-cycle autodests. Lets you cycle a whole train without dragging cars that have personalised autodests.

### `SetAutoDestination` validation gate

```csharp
public static bool SetAutoDestination(this Car car, AutoDestinationType type, OpsCarPosition? destination) {
    if (destination.HasValue && !OpsController.Shared.CanWaybillTo(car, destination.Value)) {
        Log.Warning("Ignoring SetAutoDestination: {car} cannot be waybilled to {destination}", car, destination.Value);
        return false;
    }
    car.KeyValueObject[KeyAutoDestination(type)] = destination.HasValue ? Value.String(destination.Value.Identifier) : Value.Null();
    return true;
}
```

`OpsController.CanWaybillTo` (`OpsController.cs:1236`) checks `IndustryComponentForPosition(destination)?.AcceptsCarsWithLoad(car-load)` — silently rejects when the destination IC won't accept the car's current load. **Direct `Waybill` writes via `SetWaybill` bypass this check.** Only `SetAutoDestination` enforces it.

### `ApplyAutoWaybillIfNeeded` and `CheckWaybill`

```csharp
public static void ApplyAutoWaybillIfNeeded(this Car car, IOpsCarPositionResolver resolver) {    // :148
    if (!car.GetWaybill(resolver).HasValue) car.SetWaybillAuto(null, resolver);
}
```

Used in `OpsCarAdapter.SetWaybill` flow when the IC passes `null` to mean "you decide" — and as the catch-all to keep cars without waybills from being stuck.

`CheckWaybill` (`:130`) just deserializes and rethrows on bad data. Called from `OpsController.CheckWaybills` (`:218`) at `PostRestoreProperties`; on parse failure, the car is host-side reassigned to either auto-destination (sandbox or player-owned) or the first enabled interchange.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarExtensions.CycleAutoWaybill` | Custom UI-driven routing — change the cycle order, add new states (e.g., 3-position rotor). |
| `CarExtensions.SetWaybillAuto` | Last-mile auto-fallback decision (`null` → `autodest`). Replace to add per-car AI routing. |
| `CarExtensions.SetAutoDestination` | Add additional auth/validation beyond `CanWaybillTo`. |
| `CarExtensions.ApplyAutoWaybillIfNeeded` | Trigger custom routing when waybill is missing (e.g., dispatch hand-off). |
| `OpsController.CanWaybillTo` (`OpsController.cs:1236`) | The IC-load-accept gate; replace for custom acceptance rules (mod-defined load filters). |

---

## `Model.Ops.OverrideDestination` — repair routing channel

```csharp
public enum OverrideDestination { Repair }                          // OverrideDestination.cs

// OverrideDestinationExtensions
public static string Key(this OverrideDestination od) => "ops.repair-dest";   // :16, :20
public static bool   IsWriteAuthorized(this OverrideDestination, Car);        // :11 — currently always true
public static bool   HasOverrideDestination(this Car, OverrideDestination);   // :25
public static bool   TryGetOverrideDestination(this Car, OverrideDestination, IOpsCarPositionResolver, out (OpsCarPosition, string)?); // :32
public static void   SetOverrideDestination(this Car, OverrideDestination, (OpsCarPosition, string)?);                                 // :62
```

### Coexistence with `Waybill`

Both KVO keys (`ops.repair-dest` and `ops.waybill`) can be populated simultaneously. `OpsControllerExtensions.TryGetDestinationInfo` (`OpsControllerExtensions.cs:12`) checks override first:

```csharp
if (car.TryGetOverrideDestination(OverrideDestination.Repair, opsController, out (OpsCarPosition, string)? result)) {
    destination = result.Value.Item1;                               // override wins for routing
} else {
    Waybill? wb = car.GetWaybill(opsController);
    if (!wb.HasValue) return false;
    destination = wb.Value.Destination;
}
```

So a player can send a loaded freight car to repair, the car routes to the repair track, gets fixed, the override is cleared by `RepairTrack.CheckForCompletelyRepairedCars`, and the car proceeds to its real waybill destination. The waybill payment is paid only on arrival at the *waybill* destination, not the repair track — repair pays via wages/parts, not the waybill economy.

### Wire format

The KVO value is either:
- `Value.String(identifier)` — legacy bare identifier, no tag.
- `Value.Dictionary({ id: string, tag: string })` — modern form, where `tag` is e.g. `"overhaul"` (used by `RepairTrack.InForOverhaul`).

`SetOverrideDestination(Repair, null)` clears it.

### MP authority

- `ops.repair-dest` has no `_` prefix — defaults to `MinimumLevelTrainmaster`.
- `IsWriteAuthorized(this OverrideDestination, Car car)` returns `true` unconditionally — **the auth surface exists but is unused.** Patch to gate writes (e.g., only owner can send to repair).

### Patch candidates

| Method | Why patch |
|---|---|
| `OverrideDestinationExtensions.IsWriteAuthorized` | The vacant gate — add ownership/crew checks. |
| `OverrideDestinationExtensions.SetOverrideDestination` | Audit/log all repair routing decisions. |
| `OverrideDestinationExtensions.Key` (extension) | Add new override types — see Gotchas: `OverrideDestination` enum has only one value; extending requires patching the enum or adding parallel extensions. |
| `OpsControllerExtensions.TryGetDestinationInfo` | The override-vs-waybill priority logic. Patch to add a third channel (e.g., `Quarantine`, `Hold`). |

---

## `OpsController.RewriteWaybills(fromPrefix, toPrefix)` — bulk identifier rewrite

```csharp
public void RewriteWaybills(string fromPrefix, string toPrefix) {              // OpsController.cs:947
    foreach (Car car in TrainController.Cars) {
        Waybill? wb = car.GetWaybill(this);
        if (wb.HasValue && wb.Value.Origin.HasValue && wb.Value.Origin.Value.Identifier.StartsWith(fromPrefix)) {
            Waybill rewritten = wb.Value;
            rewritten.Origin = ResolveRewritten(wb.Value.Origin.Value.Identifier);
            car.SetWaybill(rewritten);
        }
        if (wb.HasValue && wb.Value.Destination.Identifier.StartsWith(fromPrefix)) {
            Waybill rewritten = wb.Value;
            rewritten.Destination = ResolveRewritten(wb.Value.Destination.Identifier);
            car.SetWaybill(rewritten);
        }
        // … same for AutoDestinationType.Empty and AutoDestinationType.Load …
    }
    OpsCarPosition ResolveRewritten(string id) => ResolveOpsCarPosition(id.Replace(fromPrefix, toPrefix));
}
```

### Caller: `Section.ApplyCompleted` (progression unlock)

```
Progression.RecomputeOpenSection (Progression.cs:390 area)
    → Section.ApplyCompleted (Section.cs:80)
        → for each InterchangeTransfer in InterchangeTransfers:
            → InterchangeTransfer.Apply (InterchangeTransfer.cs:14)
                → OpsController.Shared.RewriteWaybills(from.Industry.identifier, to.Industry.identifier)
```

Use case: a new `Section` unlocks an industry that *replaces* a temporary interchange. The temp interchange's `identifier` is rewritten to the permanent industry's `identifier` so already-in-flight waybills retarget cleanly without the player having to manually re-route.

### Caveats and risks

- **`Replace(fromPrefix, toPrefix)` is global on the identifier string, not anchored.** Two `StartsWith` matches (origin + dest) replace independently. If `fromPrefix` substring-matches inside the identifier *after* the prefix portion (e.g. `from="mill"`, identifier `"mill.shed.mill-track"`), the inner match also gets replaced — silent corruption.
- Runs **on host only** — but is not gated by any `IsHost` check inside the method. Currently called from progression code which already runs host-side. Direct mod call from a client would mutate `ops.waybill` keys client-side; the writes would propagate (Trainmaster auth) but the host's view of the KVO is canonical.
- Walks every car in `TrainController.Cars` — O(n) per call. Section unlocks are infrequent so this is fine, but don't call it per-tick from a mod.
- **Does not rewrite `OverrideDestination.Repair`.** A car bound for "old.repair" via the override channel won't follow the rewrite. Patch to extend.
- **Does not rewrite pending `Interchange.Orders`.** Orders are NonSerialized and rebuilt per service tick, so this is moot in practice — but if you patch interchange state to persist orders, you'll need a parallel rewrite path.

### Patch candidates

| Method | Why patch |
|---|---|
| `OpsController.RewriteWaybills` | Add `OverrideDestination`/`Tag`/custom-key rewrite alongside vanilla's 4 fields. Or anchor the `Replace` to prefix-only (`identifier.StartsWith(fromPrefix) ? toPrefix + identifier.Substring(fromPrefix.Length) : identifier`). |
| `InterchangeTransfer.Apply` | Add side-effects on progression-driven re-routing (e.g., notification, log, cargo migration). |
| `OpsController.ReturnWaybillsFrom(Industry)` (`OpsController.cs:989`) | Sibling sweep used by `Industry.RollToNextContract` on tier-0 termination — patches probably want both. |

---

## `Model.Ops.Area` — industry grouping primitive

```csharp
public class Area : MonoBehaviour {                                            // Area.cs:7
    public string  identifier;
    public float   radius;
    public Color   tagColor;
    public IEnumerable<Industry> Industries => GetComponentsInChildren<Industry>();
    public bool Contains(OpsCarPosition position);                              // any child Industry contains
    public bool Contains(Vector3 point);                                        // sphere-distance to area transform
    private void OnDrawGizmosSelected() { }                                     // empty body — no debug viz!
}
```

### Two `Contains` overloads, two semantics

- `Contains(OpsCarPosition)` — delegates to **child industry** containment. An `OpsCarPosition` is "in the area" iff any child `Industry`'s `Contains` says so. So adding/removing industries from the area's GameObject hierarchy directly changes membership.
- `Contains(Vector3)` — pure radial test: `Vector3.Distance(WorldTransformer.WorldToGame(transform.position), point) < radius`. Used for spatial proximity (e.g., "closest area to this car").

These don't always agree. A car physically inside the radius might not be at any child industry; a car at a child industry might be outside the radius. Most call sites use `Contains(Vector3)` (`OpsController.CarsInArea`, `ClosestArea`, `StationAgent`); the position-based form is used by `AreaForCarPosition`.

### Industry binding

**Areas don't register industries.** The `Area` GameObject is a parent in the Unity scene; `GetComponentsInChildren<Industry>()` walks the live hierarchy. `OpsController.RebuildCollections` (`OpsController.cs:292`) walks separately from the OpsController root — so industries are discovered by `OpsController` regardless of which `Area` (if any) parents them. The `Area→Industry` binding is informational; it doesn't gate ordering or anything else in vanilla.

### Floating-origin awareness

`Contains(Vector3)` uses `WorldTransformer.WorldToGame(transform.position)` — so it correctly handles re-origin. The Area's `transform.position` is in world space; queries must be in game space. See [floating-origin](floating-origin.md) for the convention.

### `OpsController.Areas`

```csharp
public IEnumerable<Area> Areas => GetComponentsInChildren<Area>();             // OpsController.cs:66
```

**Uncached.** Every read walks the scene. Used in 5 places (per grep): `RebuildPopulations`, `AreaForCarPosition`, `ClosestAreaForGamePosition`, `RequestIndustriesOrderCars`, `Sweep`. None are per-tick; the lookup is amortized.

`AreaForCarPosition` (`:374`) **does** memoize: `_positionToAreaCache` keyed by `OpsCarPosition`. Cache is cleared in `RebuildCollections`.

### Cross-link: `MapFeature.areasEnableOnUnlock`

```csharp
public Area[] areasEnableOnUnlock;                                             // MapFeature.cs:34
public Industry[] unlockExcludeIndustries;                                     // :37
public Industry[] unlockIncludeIndustries;                                     // :40
public IndustryComponent[] unlockIncludeIndustryComponents;                    // :42
```

`MapFeatureManager.UpdateFeatureForUnlocked` (`MapFeatureManager.cs:229`) walks `feature.areasEnableOnUnlock` and:
1. For each `Area`, takes all child `Industries` (filtered by `unlockExcludeIndustries` and `externallyExcluded`).
2. Casts to `IProgressionDisablable` and includes child `PassengerStop`s.
3. Concatenates `unlockIncludeIndustries` and `unlockIncludeIndustryComponents` (override paths for one-off objects outside the area).
4. Sets `item.ProgressionDisabled = !unlocked` on each.

`Industry.ProgressionDisabled` (set on `IProgressionDisablable`) feeds into:
- `OpsController.EnabledInterchanges` — disabled industries' interchanges are filtered out.
- `IndustryComponent.IsVisible` (`IndustryComponent.cs:66`) — UI hides them.
- `OpsController.RequestIndustriesOrderCars` (`:873`) — disabled industries don't order cars.
- `Industry.UpdatePerformance` skips disabled industries.

So `Area` is the **batching unit** for progression unlocks. Defining a new area means defining "a chunk of industries that unlock together." See [progression](progression.md) for the broader unlock model.

### Patch candidates

| Method | Why patch |
|---|---|
| `Area.Contains(Vector3)` | Override sphere geometry (e.g., box, polygon, multi-radius). |
| `Area.Industries` getter | Return a curated list rather than child-walk; useful if you assign industries by data instead of hierarchy. |
| `OpsController.Areas` getter | Replace child-walk with a registry; required for runtime-added areas. |
| `OpsController.AreaForCarPosition` | Override the position-to-area resolution; cache invalidation may bite. |
| `OpsController.RebuildCollections` | Clear `_positionToAreaCache` consistently if you mutate area membership at runtime. |
| `MapFeatureManager.UpdateFeatureForUnlocked` | Hook into per-feature enable/disable side-effects (e.g., notify mod systems on area unlock). |

### Gotchas

- **No `Areas` cache invalidation hook.** Adding a new `Area` GameObject at runtime won't be visible until the next read; `_positionToAreaCache` won't include it. Mods that add areas should call `OpsController.Shared.RebuildCollections()` (which clears the position cache).
- **`Area.OnDrawGizmosSelected` is empty.** No editor visualization for the radius. Mod the method body or add a sibling Gizmo MonoBehaviour for debugging.
- **`Area` has no `ProgressionDisabled` of its own.** Areas are always "live"; only their child industries get toggled. If you want to hide an entire area conceptually, you must hide every child IC.
- **`identifier` is not enforced unique.** `SwitchListController.RequestWaybillsForArea` does `Areas.First(a => a.identifier == areaId)` — duplicate identifiers silently take the first.

---

## `SwitchList` — wire payload

```csharp
[MessagePackObject(false)]
public struct SwitchList(List<SwitchList.Entry> entries) : IDocumentContent {  // SwitchList.cs:7
    [MessagePackObject(false)]
    public struct Entry(string carId) {
        [Key(0)] public string CarId = carId;
    }
    [Key(0)] public List<Entry> Entries = entries;
}
```

**That's it.** A list of `string CarId`. No metadata, no order semantics carried in the wire format (but the *order* of the list is preserved and is the display order in `SwitchListPanel`). Sorting buttons in the panel work by computing a desired order client-side and sending a `SwitchListSetCarIds` to the host with the new order.

`IDocumentContent` is the `IGameMessage` payload marker — same interface used for `Snapshot`. Implies this struct can be a top-level packed payload (which it is, in `Snapshot.SwitchLists`).

---

## `SwitchListUpdate` — the only TrainCrew-routed message

```csharp
[HostOnlyAuthorizationRule]                                                    // SwitchListUpdate.cs:6
[MessagePackObject(false)]
public struct SwitchListUpdate(string trainCrewId, SwitchList switchList) : IGameMessage {
    [Key(0)] public string    TrainCrewId = trainCrewId;
    [Key(1)] public SwitchList SwitchList = switchList;
}
```

### Routing — special-cased in `HostManager`

```csharp
private Routing RoutingForMessage(PlayerId senderPlayerId, GameMessageEnvelope envelope) {  // HostManager.cs:806
    AccessLevel senderAccessLevel = AccessLevelForPlayerId(senderPlayerId);
    if (!CheckAuthorizedToSendMessage(envelope.gameMessage, senderPlayerId, senderAccessLevel))
        return Routing.Reject();
    Routing result = Routing.AllExcept(senderPlayerId.String);
    if (envelope.gameMessage is SwitchListUpdate)
        return Routing.TrainCrew(((SwitchListUpdate)gameMessage).TrainCrewId);   // ← only special case
    return result;
}
```

```csharp
public void HandleGameMessage(PlayerId playerId, GameMessageEnvelope envelope) {            // HostManager.cs:701
    Routing routing = RoutingForMessage(playerId, envelope);
    // …
    switch (routing.route) {
        case Routing.Route.AllExcept: SendToAllExcept(envelope, new PlayerId(routing.id)); break;
        case Routing.Route.TrainCrew: SendTo(TrainCrewPlayerIds(routing.id), envelope);    break;
    }
}

private HashSet<PlayerId> TrainCrewPlayerIds(string trainCrewId) {                          // HostManager.cs:758
    if (_snapshot.TrainCrews.TryGetValue(trainCrewId, out var value))
        return value.MemberPlayerIds.Select(id => new PlayerId(id)).ToHashSet();
    Log.Warning("Unknown train crew: {trainCrewId}", trainCrewId);
    return new HashSet<PlayerId>();
}
```

**Critical: `Routing.TrainCrew` does NOT include the sender.** `SendTo(playerIds, envelope)` sends only to the listed crew member PlayerIds — and the sender (host, when this is `ApplyLocal`) is not in the list. Locally, the host's own `SwitchListPanel.Refresh` is called via the loopback path in `StateManager.Handle`. Look back at `StateManager.cs:844`:

```csharp
else if (PlayersManager.MyTrainCrew?.Id == switchListUpdate.TrainCrewId) {
    Log.Debug("Received switch list with {entries}", switchListUpdate.SwitchList.Entries.Count);
    SwitchListPanel.Refresh(switchListUpdate.SwitchList);
    Messenger.Default.Send(default(SwitchListDidChange));
}
```

This branch fires for **anyone** whose local crew matches the update's `TrainCrewId`, including the host. So:
- **Host** dispatches `SwitchListUpdate` via `StateManager.ApplyLocal` (host-side dispatcher loopback) → all crew members receive via wire OR via local handle.
- **Non-crew clients** never receive the message.
- A player on multiple devices — impossible in vanilla (one PlayerId per Steam id) but worth noting.

### `[HostOnlyAuthorizationRule]`

The attribute means: only the host may originate this message. If a misbehaving client tried to send `SwitchListUpdate` with a forged `TrainCrewId`, the auth check rejects it (`HostManager.CheckAuthorizedToSendMessage`). The legitimate client→host channel for switch list edits is `SwitchListSetCarIds` / `SwitchListToggleCarIds` (Crew-level).

### `RecordState` for late-joiners

```csharp
// HostManager.cs:880 in RecordState dispatch:
if (gameMessage is SwitchListUpdate switchListUpdate) {
    _snapshot.SwitchLists[switchListUpdate.TrainCrewId] = switchListUpdate.SwitchList;
}
```

So the host's snapshot mirrors every successful switch list. Late joiners get the full per-crew switch list set via `Snapshot.SwitchLists` on initial sync, and `SwitchListController.RestoreSwitchLists` populates the live in-memory store host-side.

### Compression

Per [multiplayer-core](multiplayer-core.md), `SwitchListUpdate` is on the gzip-eligible message list (≥ 1024 bytes triggers compression). A typical 50-car switch list is ~2 KB raw; compressed ~600 B.

### Patch candidates

| Method | Why patch |
|---|---|
| `HostManager.RoutingForMessage` (`HostManager.cs:806`) | Add new TrainCrew-routed message types alongside `SwitchListUpdate`. |
| `HostManager.TrainCrewPlayerIds` | Customize crew membership resolution for routing (e.g., include observers, supervisors). |
| `StateManager.Handle` SwitchListUpdate branch (`StateManager.cs:844`) | Catch every received switch list — but you'll only fire for cars where you're a crew member. |
| `SwitchListController.SendSwitchListUpdate` (private + public overloads) | Single chokepoint for all push side; postfix to log every dispatch. |

---

## `SwitchListController` — host-side switch list authority

```csharp
public class SwitchListController : MonoBehaviour {                            // SwitchListController.cs:13
    public OpsController opsController;
    private readonly Dictionary<string, List<IOpsCar>> _switchLists = new();   // keyed by trainCrewId

    public  void RequestWaybillsForArea(IPlayer sender, string trainCrewId, string areaId);  // :19  ← see Gotchas (dead)
    public  void SetSwitchListCarIds   (string trainCrewId, IEnumerable<string> carIds, bool send);  // :25
    public  void ToggleSwitchListCarIds(string trainCrewId, List<string> carIds, bool on);   // :47
    private void RequestWaybillsForArea(IPlayer sender, Area area);            // :78  ← also dead
    private bool SwitchListContainsCarForArea(List<IOpsCar> switchList, Area); // :102
    private void SendSwitchListUpdate(string trainCrewId, List<IOpsCar> opsCars); // :115
    private void RemoveCompletedFromSwitchLists();                             // :133  ← never called!
    public  void RemoveCar(string carId);                                      // :151
    public  void SendSwitchListUpdate(string trainCrewId);                     // :167
    public  void PopulateSnapshot(ref Snapshot snapshot);                      // :180
    public  void RestoreSwitchLists(Dictionary<string, SwitchList> switchLists); // :189
}
```

### Storage shape

`Dictionary<string trainCrewId, List<IOpsCar>>` — host-side only. `IOpsCar` is the `OpsCarAdapter` per [industries-ops](industries-ops.md#modelopsiopscar-and-opscaradapter). Order is the per-crew display order. **Cars in the list that get removed from the world are pruned only via `RemoveCar(carId)` — there's no periodic sweep.**

### Wire-up

```csharp
// OpsController.Awake (OpsController.cs:84):
SwitchListController = base.gameObject.AddComponent<SwitchListController>();
SwitchListController.opsController = this;
```

One instance per session. Always lives on the OpsController GameObject.

### `SetSwitchListCarIds` flow (host)

```csharp
public void SetSwitchListCarIds(string trainCrewId, IEnumerable<string> carIds, bool send) {
    List<IOpsCar> list = new();
    foreach (string carId in carIds) {
        IOpsCar opsCar = opsController.CarForId(carId);
        if (opsCar == null) Log.Warning("…will be omitted from switch list");
        else                list.Add(opsCar);
    }
    _switchLists[trainCrewId] = list;
    if (send) SendSwitchListUpdate(trainCrewId, list);
}
```

Replaces the entire list. Unknown car ids are silently dropped (logged at Warning). The `send` parameter controls whether to immediately broadcast — `RestoreSwitchLists` passes `false` to avoid spamming during restore.

### `ToggleSwitchListCarIds` flow

```csharp
public void ToggleSwitchListCarIds(string trainCrewId, List<string> carIds, bool on) {
    if (!_switchLists.TryGetValue(trainCrewId, out var value)) value = new List<IOpsCar>();
    if (on) {
        foreach (string carId in carIds)
            if (value.FindIndex(car => car.Id == carId) < 0)
                value.Add(opsController.CarForId(carId));            // ← can add null!
    } else {
        foreach (string carId in carIds) {
            int num = value.FindIndex(car => car.Id == carId);
            if (num >= 0) value.RemoveAt(num);
        }
    }
    _switchLists[trainCrewId] = value;
    SendSwitchListUpdate(trainCrewId, value);
}
```

**Bug-shaped:** the `on:true` path doesn't null-check `opsController.CarForId(carId)`. If the car id doesn't resolve, a `null` is appended to `_switchLists[trainCrewId]`. The next `SendSwitchListUpdate` will hit the `opsCar.Id` access on `null` and explode in `SwitchListEntriesFromOpsCars`. The dispatch is wrapped in try/catch (`SendSwitchListUpdate` catches), so the exception is logged but the list now contains a poison entry.

### `SendSwitchListUpdate(trainCrewId)` (no list)

```csharp
public void SendSwitchListUpdate(string trainCrewId) {                         // :167
    if (!_switchLists.TryGetValue(trainCrewId, out var value)) {
        Log.Debug("…sending empty switch list.");
        SendSwitchListUpdate(trainCrewId, new List<IOpsCar>());
    } else SendSwitchListUpdate(trainCrewId, value);
}
```

**Called from `PlayersManager.HandleRequestTrainCrewMembership`** (`PlayersManager.cs:361`) on join — so a player joining a crew immediately receives the existing list. Not called on leave (the leaving player just stops receiving updates).

### `RemoveCar(carId)`

```csharp
public void RemoveCar(string carId) {                                          // :151
    foreach (var (trainCrewId, list) in _switchLists) {
        int num = list.FindIndex(c => c.Id == carId);
        if (num >= 0) {
            list.RemoveAt(num);
            SendSwitchListUpdate(trainCrewId, list);                            // re-broadcast
        }
    }
}
```

Called from `OpsController.RemoveCar(carId)` (`OpsController.cs:310`), which is itself called from `TrainController.RemoveCar` (`TrainController.cs:1544`). So whenever a car is despawned (interchange completion, sold, sandbox delete), every crew that had it in their switch list gets a fresh broadcast. **This is the cleanup path** — no other prune mechanism exists.

### `RemoveCompletedFromSwitchLists` — DEAD CODE

```csharp
private void RemoveCompletedFromSwitchLists() {                                // :133
    foreach (string item in _switchLists.Keys.ToList()) {
        List<IOpsCar> list = _switchLists[item];
        if (list.Exists(c => c.Waybill?.Completed ?? false)) {
            list = list.Where(c => c.Waybill.HasValue && !c.Waybill.Value.Completed).ToList();
            _switchLists[item] = list;
            SendSwitchListUpdate(item, list);
        }
    }
}
```

**Never called from anywhere** (per grep). The switch-list panel shows completed entries with a strikethrough; removing them is the user's responsibility via the "Cleanup" button (`SwitchListPanel.ClickCleanup` at `SwitchListPanel.cs:131`), which builds a fresh list excluding `e.Completed` entries client-side and sends `SwitchListSetCarIds`. **A crew that never opens the panel never cleans up; its switch list grows monotonically until cars are physically removed.**

### `RequestWaybillsForArea` — also DEAD CODE

```csharp
public void RequestWaybillsForArea(IPlayer sender, string trainCrewId, string areaId);   // :19
private void RequestWaybillsForArea(IPlayer sender, Area area);                            // :78
```

Per grep, both overloads have **zero callers** in vanilla. The intent is clearly "click an area, pull all uncompleted-waybill cars in it onto your switch list" — but the binding is missing. The closest live UI is `StationWindow.BuildFreightTab` which iterates per-car and lets the player toggle one at a time. The dead code carries an extra Multiplayer.SendError feedback path ("Added X cars", "No work in Y today") that doesn't exist in the live UI.

**This is a high-value mod hook:** wire up `SwitchListController.RequestWaybillsForArea` to a button (e.g., add to `StationWindow.BuildFreightTab`) and you've reimplemented the bulk-add UX with no logic changes.

### `PopulateSnapshot` / `RestoreSwitchLists`

```csharp
public void PopulateSnapshot(ref Snapshot snapshot) {                          // :180
    snapshot.SwitchLists = new();
    foreach (var (key, opsCars) in _switchLists)
        snapshot.SwitchLists[key] = new SwitchList(SwitchListEntriesFromOpsCars(opsCars));
}

public void RestoreSwitchLists(Dictionary<string, SwitchList> switchLists) {  // :189
    _switchLists.Clear();
    foreach (var (text2, switchList2) in switchLists) {
        try { SetSwitchListCarIds(text2, switchList2.Entries.Select(e => e.CarId), send: false); }
        catch (Exception e) { Log.Error(e, "Exception while restoring switch list for crew {trainCrewId}", text2); }
    }
}
```

Called from `OpsController.PopulateSnapshotForSave` (`OpsController.cs:888`) and `OpsController.RestoreSwitchLists` (`:893`), respectively — host-side. `RestoreSwitchLists` doesn't broadcast (the `send: false`); the calling `StateManager.RestoreSwitchLists` (`StateManager.cs:1239`) handles the local-player refresh separately.

### Patch candidates

| Method | Why patch |
|---|---|
| `SwitchListController.SetSwitchListCarIds` | Single chokepoint for "replace list" — patch to enforce caps, ordering, mod-defined filters. |
| `SwitchListController.ToggleSwitchListCarIds` | Patch to fix the null-add bug, or to add per-add hooks (e.g., notify dispatcher). |
| `SwitchListController.RemoveCar` | Postfix to add side-effects on car removal (e.g., notify dispatcher mod that a job ended). |
| `SwitchListController.RemoveCompletedFromSwitchLists` | Resurrect via a periodic invocation (e.g., `OpsController.PeriodicUpdate` postfix) for auto-cleanup. |
| `SwitchListController.RequestWaybillsForArea` | Hook up to a real UI button — already implemented, just unbound. |
| `SwitchListController.SendSwitchListUpdate(string)` overload | Trigger a re-broadcast after mod-side changes. |
| `OpsController.RemoveCar` | Sibling cleanup chokepoint — also fires `SwitchListController.RemoveCar` automatically. |

### MP authority

| Operation | Auth | Mechanism |
|---|---|---|
| `SwitchListSetCarIds` | `[MinimumAccessLevel(Crew)]` | Client → host; host calls `SetSwitchListCarIds(send:true)` |
| `SwitchListToggleCarIds` | `[MinimumAccessLevel(Crew)]` | Same |
| `SwitchListUpdate` | `[HostOnlyAuthorizationRule]` | Host → crew members (TrainCrew route) |
| `SwitchListController.RequestWaybillsForArea` | (no message wires it) | Dead code; if mod wires it, define a Crew-level request msg |
| `SwitchListController.RemoveCar` | host-only call sites | Triggered by `OpsController.RemoveCar` host-side only |
| `RestoreSwitchLists` | host-side at restore | `StateManager.PostRestoreProperties` host loop |

**No per-message validation that the sender is actually a member of the named `TrainCrewId`.** A Crew-level player could theoretically send `SwitchListSetCarIds("not-my-crew", […])` and the host would apply it. The TrainCrew `[MinimumAccessLevel(Crew)]` only checks "you're at least Crew" — it doesn't bind the message to your specific crew. **Mod patches should add a `PlayersManager.TrainCrewIdFor(sender.PlayerId) == msg.TrainCrewId` check at the host handler.**

### Related Messenger / KVO events

| Event | Type | Where |
|---|---|---|
| `Game.Events.SwitchListDidChange` | Empty struct | `StateManager.Handle` after applying `SwitchListUpdate` (`StateManager.cs:848`) |
| (no KVO key) | n/a | Switch lists are **not** stored on any KVO object — they're a parallel snapshot section + per-message broadcast. Patches that observe `KeyValueObject` won't see them. |

---

## Per-crew switch list visibility — concrete model

```
Player A (Crew "A-train")  Player B (Crew "A-train")  Player C (Crew "B-line")  Player D (no crew)
        │                          │                          │                          │
        └────── SwitchListUpdate("A-train", …) ──────┐        │                          │
                                                     │        │                          │
host receives any of:                                ▼        │                          │
- SwitchListSetCarIds   (Crew → host)                ✓        ✓ A-train members          │
- SwitchListToggleCarIds  (Crew → host)              ✓        ✗ B-line member            │
- (any state mutation that triggers send)            ✓        ✗ no crew                  ✗
```

The host-side broadcast goes ONLY to PlayerIds in `_snapshot.TrainCrews[trainCrewId].MemberPlayerIds`. **Late switch:**

- Player joins a crew via `RequestSetTrainCrewMembership` → `PlayersManager.HandleRequestTrainCrewMembership` → host calls `OpsController.Shared.SwitchListController.SendSwitchListUpdate(text)` with the new crew id (`PlayersManager.cs:361`). Joining player gets the current state pushed.
- Player leaves a crew → no message is sent; the player just stops receiving future updates. They retain the last-received list in their local `SwitchListPanel` until refreshed (via `Snapshot` re-receipt or rejoining). **There's no "clear my switch list panel on leave" path.**
- Player on no crew: `StationWindow.BuildFreightTab` shows a label "Join a Train Crew to add cars to your switch list." (`StationWindow.cs:110`); the toggle column shows `-`. `SwitchListPanel.UpdatePositions` sets the empty-state label "Join or create a train crew to add cars." (`SwitchListPanel.cs:277`).

### Snapshot population for late-joining clients

A late-joining client (or a client receiving a fresh `Snapshot` from `StateManager.PopulateFromRemoteSnapshot`) gets the full `Snapshot.SwitchLists : Dictionary<string, SwitchList>`. Their `StateManager.RestoreSwitchLists` (`StateManager.cs:1239`) calls `OpsController.RestoreSwitchLists` (which rebuilds the in-memory dict server-side; on a *client* this branch is irrelevant since `_switchLists` lives on the host's component) and then refreshes the local `SwitchListPanel` if the player's crew is in the snapshot. So clients always have a one-shot at startup.

### Gotchas (per-crew visibility)

- **`StateManager.Handle` SwitchListUpdate branch checks `PlayersManager.MyTrainCrew?.Id == switchListUpdate.TrainCrewId`** — this is the ONLY filter on the receive side. If a client somehow received a SwitchListUpdate for a different crew (bypassing the host's TrainCrew routing — e.g., via a debug spoofed message), they'd silently drop it. Defense-in-depth.
- **`MyTrainCrew` is computed on every check** via `TrainCrewIdFor(PlayerId)` linear scan (see [players-traincrew](players-traincrew.md#mytraincrew-l91)). A SwitchListUpdate triggers this lookup once per receive.
- **The host receives SwitchListUpdate locally too** (`StateManager.ApplyLocal` loopback) — and runs the same handler. So a host who's on Crew "A-train" gets exactly the same UI refresh as a remote member.
- **Joining a crew mid-session**: you receive the current list immediately (`PlayersManager.cs:361`). But cars that have been added to your new crew's list while you weren't a member are visible to you immediately — there's no "private to current members" period.
- **A crew can be deleted** (`HandleRequestDeleteTrainCrew`) but `_switchLists[deletedCrewId]` is **not pruned**. Leftover entries linger in memory and in the snapshot indefinitely. Patch `PlayersManager.HandleRequestDeleteTrainCrew` to also call `_switchLists.Remove(deletedCrewId)` on the controller.

---

## UI consumption surface

| Consumer | File | Notes |
|---|---|---|
| `SwitchListPanel` | `UI.SwitchList/SwitchListPanel.cs` | The standalone "Switch List" window. Refreshed via static `SwitchListPanel.Refresh(SwitchList)`. Owns periodic `UpdatePositions` 2s coroutine. |
| `StationWindow` | `UI.StationWindow/StationWindow.cs` | Per-station agent panel; `BuildFreightTab` shows per-area cars-with-waybill and per-row toggle. Subscribes `SwitchListDidChange` for live row refresh (`StationWindow.cs:126`). |
| `CarInspector` | `UI.CarInspector/CarInspector.cs` | Per-car panel; `PopulateSetWaybillPanel` exposes `CycleAutoWaybill` button (`:477`) and `CycleAutoWaybill(coupled)` button (`:482`). |
| `OpsCarList` | `UI.SwitchList/OpsCarList.cs` | Shared "list of cars with destination/location" data structure used by both the panel and the station window. |

### `SwitchListPanel` refresh model

```
Server-side change         →  SwitchListUpdate broadcast
                                       │
       Client receives ───→ StateManager.Handle (StateManager.cs:844)
                              │
                              ├─ SwitchListPanel.Refresh(switchList)
                              │     → _panel.Rebuild(switchList)
                              │       → _switchList.Rebuild(carIds)
                              │       → instantiate rows
                              └─ Messenger.Send(SwitchListDidChange)
                                    → other UI re-renders
```

`Rebuild` runs synchronously in the message dispatch — for a 50-car switch list it allocates 50 `OpsCarList.Entry` records and 50 row prefab instances. Each row attaches `LocationIndicatorHoverArea` descriptors. Watch for GC churn if your mod sends many SwitchListUpdates per second.

`PeriodicRefresh` (every 2s) only re-runs `UpdatePositions` (which calls `_switchList.Rebuild()` — recomputes positions, not the list itself, returning bool "did anything change"). This catches "car moved" without needing a fresh broadcast.

---

## Cross-system patch points

### Custom routing channels

To add a new `OverrideDestination` value:
1. Either patch the enum (Harmony reflection, brittle) or add a parallel extension method+enum:
   ```csharp
   public enum ModOverrideDestination { Storage, Holding }
   public static string Key(this ModOverrideDestination o) => o switch { ... };
   ```
2. Patch `OpsControllerExtensions.TryGetDestinationInfo` to check your override before falling through to vanilla.
3. Patch `OpsController.RewriteWaybills` to also rewrite your KVO key on prefix match.
4. Define your own write-auth gate (vanilla's `IsWriteAuthorized` returns true — copy and tighten).
5. Add a UI ("Send to X") that writes via `Car.KeyValueObject[key]`.

### Custom waybill semantics

- Subclass `IndustryComponent` and override `CheckForCompleted` for partial-deliveries / multi-car-shipments / time-window jobs (see [industries-ops § Patch points](industries-ops.md#custom-industry-component)).
- Patch `OnCompleteWaybill` to add custom payment modifiers (mod-defined bonus for hauling unspoiled cargo, etc.).
- Add new `Waybill.Tag` strings — set them in your spawn path and recognize in your IC's `OnCompleteWaybill`. The vanilla `Waybill.Tag` is `readonly` so you must construct new Waybills, not mutate.
- For mod-defined fields on the waybill: patch `Waybill.PropertyValue` and `Waybill.FromPropertyValue` in lockstep — both host and clients deserialize, so coordination is essential.

### Custom switch list filters / aggregation

- Patch `SwitchListController.SetSwitchListCarIds` to apply mod-defined filters (e.g., reject cars below condition threshold).
- Replace `SwitchListController.SendSwitchListUpdate(string, List<IOpsCar>)` to inject sort order, deduplication, or per-mod metadata. The wire `SwitchList.Entry` only carries `CarId` — for richer metadata, use a parallel KVO blob keyed by `(trainCrewId, carId)` and apply via a separate message.
- For "switch lists that survive across save/load with mod-extra metadata," extend the snapshot via a separate top-level dictionary key (vanilla doesn't expose `Snapshot` extensibility — you'll need to wedge into `OpsController.PopulateSnapshotForSave` / `RestoreSwitchLists`).

### Custom area types

- Subclass `Area` to add typed metadata (yard vs. mainline vs. industrial).
- Patch `Area.Contains(Vector3)` for non-circular geometries.
- Override `Area.Industries` to return a curated list (decouples membership from scene hierarchy).

---

## Race conditions & lifecycle gotchas

- **Waybill mutation race during arrival.** `OnCompleteWaybill` mutates a local `Waybill` value, calls `car.SetWaybill(wb)` — but if a client simultaneously sends a `CycleAutoWaybill` write (also Trainmaster + crew auth), the writes can interleave. Last-write-wins on the KVO; the host's "completed=true" might be overwritten by the client's "autodest with completed=false". The IC's next `CheckForCompleted` tick will re-fire `OnCompleteWaybill` and double-pay. **No mutex.** Mods that rely on payment-once must add idempotency (e.g., per-car "last-paid waybill ID" tracking).
- **`SwitchListUpdate` and `RemoveCar` race.** When a car is removed, `OpsController.RemoveCar` → `SwitchListController.RemoveCar` re-broadcasts the affected lists. If a client just sent a `SwitchListToggleCarIds(on:true, [removedCarId])` simultaneously, the host might process the toggle first (adding a stale ID), then RemoveCar (broadcasting list-with-the-stale-entry-removed). Net result: the toggle silently no-ops. Edge case, but a source of "I clicked add and nothing happened" reports.
- **`RewriteWaybills` runs on every car** — including cars currently being processed by `IndustryComponent.CheckForCompleted`. Section unlocks during heavy ops can produce log warnings about "rewrote waybill but car was already at destination" — the rewrite happens but the next tick's `CheckForCompleted` sees the new destination and may not match the car's current location.
- **`Section.ApplyCompleted` is host-only by virtue of progression being host-driven.** If a mod calls `Progression.UnlockSection(...)` from a client, no host-side `RewriteWaybills` happens; the resulting state is inconsistent across the session.
- **`Snapshot.SwitchLists` is part of the late-joiner snapshot** — but the wire format is the same as `SwitchListUpdate`. A snapshot-based switch list refresh fires `SwitchListPanel.Refresh` (`StateManager.cs:1247`) but does **not** fire `SwitchListDidChange` Messenger. UI subscribers to `SwitchListDidChange` (e.g., `StationWindow`'s row toggles) won't refresh on snapshot-restore. Patch `RestoreSwitchLists` postfix to fire the event.
- **`InterchangeTransfer.Apply` does not validate `from`/`to`.** Two transfers with overlapping prefixes (e.g., from="X.a", to="Y.a", and another from="X", to="Z") run in array order — the first transfer rewrites X.a→Y.a, the second rewrites X→Z. Cars with origin "X.a.spur" become "Y.a.spur" (correct), but a car with origin "X.b" becomes "Z.b". If the second IT was supposed to also catch X.a, it won't (already rewritten). Section authors should be careful with prefix nesting. **Worse:** the `Replace` is non-anchored, so "X" inside "Y.X.spur" gets replaced too.

---

## Cross-references

- [industries-ops](industries-ops.md) — `IndustryComponent.OnCompleteWaybill` (the pay-and-mark-complete site), `Waybill` struct details, `OpsController.PaymentForMove`, `OpsController.WaybillCarToInterchange`, `Interchange.ServeInterchange` (waybill spawn), `OverrideDestination.Repair` integration with `RepairTrack`. The "Tags" table (`null` / `"autodest"` / `"sell"` / `"overhaul"` / progression / team-track) is the canonical reference.
- [progression](progression.md) — `Section.ApplyCompleted` global hook into `OpsController.RewriteWaybills`. `MapFeature.areasEnableOnUnlock` connects to `Area`-bound industry batching. `IProgressionDisablable` is the per-industry toggle hit by area unlocks.
- [players-traincrew](players-traincrew.md) — `MyTrainCrew`, `TrainCrewIdFor(PlayerId)`, `_trainCrews` ownership; `HandleRequestTrainCrewMembership`'s implicit `SendSwitchListUpdate` on join.
- [multiplayer-core](multiplayer-core.md) — `SwitchListUpdate` is the only special case in `HostManager.RoutingForMessage` (the `Routing.TrainCrew` route). Compression eligibility (gzip ≥1024B). `Snapshot.SwitchLists` wire format.
- [economy](economy.md) — payment flows for `PayWaybill`; `tag="autodest"` payment 0; `Ledger.Category.Freight` for waybill payouts; condition-fine application.
- [access-control](access-control.md) — `[MinimumAccessLevel(Crew)]` on `SwitchListSet/ToggleCarIds`; `[HostOnlyAuthorizationRule]` on `SwitchListUpdate`. The unbound `IsWriteAuthorized` on `OverrideDestination`.
- [floating-origin](floating-origin.md) — `Area.Contains(Vector3)` uses `WorldTransformer.WorldToGame` correctly; mod additions of areas should preserve this convention.
- [save-load](save-load.md) — `Snapshot.SwitchLists` lives at top level (not inside the per-car KVO blob); `RestoreSwitchLists` flow.
