# Odometer & Per-Tick Movement Hookpoint — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Wear & Durability](wear-durability.md), [Cars & Cargo](cars-cargo.md), [Couplers](couplers.md), [Integration Set Solver](integration-set-solver.md), [Events Catalog](events-catalog.md)

There is **no `OdometerService` class**. "OdometerService" is a per-`Car` `float` property (`KeyValueObject["_odosvc"]`) — service-adjusted kilometres since the car was created — alongside its sibling `OdometerActual` (`_odometer`, true ground-truth km). Mileage is driven by a single per-tick spine: each `IntegrationSet.Tick` (host) or `RemoteIntegrationSet.Tick` (client) calls `Car.PositionWheelBoundsFront(... MovementInfo info ...)` which calls the virtual `Car.FireOnMovement(MovementInfo)`. `FireOnMovement` does three things: (1) accumulate the unbanked metre counters and bank to KVO at every 500 m, (2) run `Car.BankOdometer` (host-only — wear, oil, hotbox); (3) fan out to **`ICarMovementListener`** components attached under the car body. The listener list is the per-tick "car moved" hookpoint flagged in the events catalog. Vanilla has exactly one implementer (`DerailedParticleController`); the surface is wide-open for mods.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `RollingStock.MovementInfo` (readonly struct) | `RollingStock/MovementInfo.cs:3` | Payload: `DeltaTime`, `Distance`, `TractiveEffort`. **No position, no velocity** |
| `RollingStock.ICarMovementListener` | `RollingStock/ICarMovementListener.cs:3` | Single-method `void CarDidMove(MovementInfo)` |
| `Car._movementListeners` (private List) | `Model/Car.cs:360` | Per-car listener list — populated from body `GetComponentsInChildren<ICarMovementListener>()` |
| `Car.FireOnMovement(MovementInfo)` (virtual) | `Model/Car.cs:2053` | The hookpoint — odometer accumulator + listener fan-out |
| `Car.BankOdometer()` (private) | `Model/Car.cs:2077` | Host-only: writes `_odosvc`/`_odometer` KVO every 500 m |
| `Car.OdometerService` / `OdometerActual` / `LastOverhaulOdometer` | `Model/Car.cs:819, 831, 843` | KVO-backed float properties (km), HostOnly writes |
| `Car.ServiceMetersFromActual(float)` (virtual) | `Model/Car.cs:2067` | Multiplier curve: `Config.serviceDistanceConditionMultiplier.Evaluate(Condition)` |
| `IntegrationSet.PositionCars(float dt, bool)` | `Model.Physics/IntegrationSet.cs:179` | **Source** of `MovementInfo`; runs each FixedUpdate per set |
| `RemoteIntegrationSet.MoveCarTo(...)` | `Model.Physics/RemoteIntegrationSet.cs:105` | Client-side `MovementInfo` source — fires listeners on clients |
| `Car.PrepareForSnapshotSave()` | `Model/Car.cs:2072` | Forces `BankOdometer()` before save so unbanked metres aren't lost |

---

## Tick spine: `MovementInfo` from solver to listener

```
TrainController.FixedUpdate                              // TrainController.cs:419
   foreach IntegrationSet.Tick(deltaTime)                // 438-444
      → IntegrationSet.PositionCars(dt, false)           // 179
         per Element where ShouldPosition (moved >1mm):
            num = element.position - element.oldPosition
            info = new MovementInfo(dt, |num|, car.NormalizedTractiveEffort)
            car.PositionWheelBoundsFront(wbF, _graph, info, true)  // 195
               → SetTruckPositions(...)                  // truck rotation
               → FireOnMovement(info)                    // Car.cs:1999
```

```csharp
// Model/Car.cs:2053
protected virtual void FireOnMovement(MovementInfo info)
{
    _unbankedOdometerActual  += info.Distance;
    _unbankedOdometerService += ServiceMetersFromActual(info.Distance);
    if (_unbankedOdometerActual > 500f)
        BankOdometer();
    foreach (ICarMovementListener movementListener in _movementListeners)
        movementListener.CarDidMove(info);
}
```

### Tick frequency

- **Driver:** `TrainController.FixedUpdate` (`TrainController.cs:419`) — Unity FixedUpdate, default 0.02 s = 50 Hz.
- **Per set, per FixedUpdate:** one `IntegrationSet.Tick`; one inner `PositionCars` call (the four-iteration constraint solver runs *between* `ApplyVerlet` and `PositionCars`, so listeners only fire **once per FixedUpdate per set**, not four times).
- **Filter:** `IntegrationSet.ShouldPosition` (`IntegrationSet.cs:235`) returns false unless the car moved >1 mm or `Car.ShouldUpdatePosition()` is true (the latter is true while `_derailment != _derailmentDisplay` or `IsOnTurntable`). Stationary cars **do not** fire listeners or accumulate odometer — `info.Distance` would be 0 anyway, but the call is short-circuited entirely.
- **`ShouldSkipTick`** on `IntegrationSet` can suppress the whole set's tick (`TrainController.cs:440`); `RemoteIntegrationSet.ShouldSkipTick = false` (`RemoteIntegrationSet.cs:41`) — clients always tick remote sets.

### `MovementInfo` payload (the whole struct)

```csharp
// RollingStock/MovementInfo.cs:3
public readonly struct MovementInfo(float deltaTime, float distance, float tractiveEffort)
{
    public readonly float DeltaTime;       // seconds (== Time.deltaTime at the FixedUpdate)
    public readonly float Distance;        // ABSOLUTE metres travelled this tick (always ≥ 0)
    public readonly float TractiveEffort;  // Car.NormalizedTractiveEffort, in [-1..1]; 0 for non-loco
    public static readonly MovementInfo Zero;  // (0, 0, 0)
}
```

**No velocity, no direction, no world-position delta.** Direction has to be reconstructed from `car.velocity` (signed, body-relative). World position has to come from `car.GetCenterPosition()` or `car.LocationF`/`LocationR` snapshot before/after.

### `MovementInfo.Zero` snap-positions

Many call sites pass `MovementInfo.Zero` deliberately to *snap* a car into position without firing wear/listener semantics:

| Site | Why |
|---|---|
| `Car.cs:1231` (in body-population path) | First model placement |
| `TrainController.cs:558, 562, 985, 1006, 1471` | Spawn / restore / placer flows |
| `ConsistPlacer.cs:200` | Ghost preview cars |

**`Distance == 0` ⇒ unbanked counters don't move ⇒ `BankOdometer` not triggered ⇒ listeners are still iterated but receive `(0,0,0)`.** Listeners must tolerate `MovementInfo.Zero` without misbehaving (e.g., `DerailedParticleController` correctly handles `info.DeltaTime == 0` to compute `CarVelocity = 0`).

---

## `Car` per-car odometer state

### Storage fields & KVO keys

```csharp
// Model/Car.cs:402-412
private float _unbankedOdometerService;    // metres, host-only working buffer
private float _unbankedOdometerActual;     // metres, host-only working buffer
private const string KeyOdometerActual   = "_odometer";   // float, km
private const string KeyOdometerService  = "_odosvc";     // float, km
private const string KeyLastOverhaul     = "_lastOverhaul";   // float, km (snapshot of OdometerService)
private const string KeyOverhaulProgress = "_overhaulProg";   // float [0..1], stored Null when 0
```

All four KVO keys are **HostOnly** by `Car.HostPrefixes` (leading `_` ⇒ `Car.cs:467`). Clients read them but cannot write. The unbanked floats are *not* on the KVO — they live only on the host instance.

### Properties

```csharp
public float OdometerService    => KeyValueObject["_odosvc"]; private set;       // 819
public float OdometerActual     => KeyValueObject["_odometer"]; private set;     // 831
public float LastOverhaulOdometer => KeyValueObject["_lastOverhaul"]; set;       // 843, public setter
public float OverhaulProgress   => KeyValueObject["_overhaulProg"]; set;         // 855, stored Null at 0
public float RepairCap          => WearFeature                                   // 885
    ? RepairTrack.RepairCapForKilometersSinceOverhaul(OdometerService - LastOverhaulOdometer)
    : 1f;
```

`OdometerService` and `OdometerActual` are in **kilometres**. `BankOdometer` divides metres by 1000 before adding (`Car.cs:2081-2084`). UI display (`UI.CompanyWindow/BuilderExtensions.cs:55, 60, 66, 70`) converts km → mi via `× 0.6213712`.

### `BankOdometer` — the banker

```csharp
// Model/Car.cs:2077
private void BankOdometer()
{
    if (StateManager.IsHost)
    {
        float num  = _unbankedOdometerActual  / 1000f;        // km
        float num2 = _unbankedOdometerService / 1000f;        // km
        OdometerActual  += num;
        OdometerService += num2;
        SetCondition(_condition - WearForMovement(num2));      // gated by WearFeature
        OffsetOiled(0f - OilUseForMovement(num2));             // gated by EnableOiling inside SetOiled
        CheckForHotbox(num);                                   // chance per 100 mi
        _unbankedOdometerActual  = 0f;
        _unbankedOdometerService = 0f;
    }
}
```

- **Host-only.** Early-returns on clients without zeroing the unbanked counters — but client `_unbanked*` is never incremented in practice because clients also run `FireOnMovement` (which does increment); however since `BankOdometer` no-ops on clients, the unbanked counters on clients **grow without bound** until model unload (`_movementListeners.Clear()` at `Car.cs:1555` runs in unload but does NOT zero the unbanked fields — they're plain Car fields, not body-scoped). **In practice this is harmless because the values are never read on clients** (KVO is the only reader; unbanked is host-only writer-state). Worth knowing for memory-leak audits: the float never overflows.
- **Trigger:** `_unbankedOdometerActual > 500f` (`Car.cs:2057`) — about 0.31 mi between banks. Small enough that the "wear lurches every 500 m" pattern is visible if you graph `_condition`.
- **Forced bank:** `Car.PrepareForSnapshotSave()` (`Car.cs:2072`) calls `BankOdometer` directly before each snapshot save (`TrainController.cs:1899`). Without this, the last 0–500 m of travel would be lost on save+reload.
- **`ServiceMetersFromActual` is `virtual`** (`Car.cs:2067`):
  - Default: `meters * Config.serviceDistanceConditionMultiplier.Evaluate(Condition)`. Damaged cars accrue service-miles faster than actual miles.
  - **`BaseLocomotive.ServiceMetersFromActual` override** (`BaseLocomotive.cs:228`) multiplies again by `Config.serviceDistanceTractiveEffortMultiplier.Evaluate(NormalizedTractiveEffort)`. Locos working hard accrue overhaul-miles faster than they roll.

### `LastOverhaulOdometer` interaction

```csharp
// Model.Ops/RepairTrack.cs:291
car.LastOverhaulOdometer = car.OdometerService;       // snap on overhaul completion
car.OverhaulProgress = 0f;
```

Set **only** by `RepairTrack.TickCar` after `OverhaulWorkRemaining` returns ≤ 0 (`RepairTrack.cs:283-292`). The 5-step `RepairCap` ladder (10 % per `OverhaulMiles` = 2500 mi default, capped at 50 % drop) is computed from `OdometerService - LastOverhaulOdometer` (`Car.cs:893` and `RepairTrack.RepairCapForKilometersSinceOverhaul` at `RepairTrack.cs:407`). See [wear-durability › patch candidates](wear-durability.md#patch-candidates) for replacing the ladder.

**`LastOverhaulOdometer == 0f` is the "never overhauled" sentinel** consumed in the UI (`BuilderExtensions.cs:59`: `(car.LastOverhaulOdometer == 0f) ? "Never" : ...`). New cars start with `_lastOverhaul` unset (i.e., the KVO key returns 0 by default). Patches that set this field should NOT zero it as a clear-overhaul mechanism.

**`/repair` does NOT reset `LastOverhaulOdometer`** — see [wear-durability › gotchas](wear-durability.md#gotchas).

---

## `_movementListeners` — the per-tick hookpoint

### Registration

```csharp
// Model/Car.cs:360
private readonly List<ICarMovementListener> _movementListeners = new();

// Model/Car.cs:1301-1304
protected virtual void DidSetBodyActive()
{
    _movementListeners.AddRange(BodyTransform.GetComponentsInChildren<ICarMovementListener>());
}

// Model/Car.cs:1555 (in UnloadModels)
_movementListeners.Clear();
```

- **Discovery is one-shot per body load.** The list is populated from the body GameObject hierarchy at body activation. Adding a `MonoBehaviour : ICarMovementListener` to a child of `BodyTransform` *after* `DidSetBodyActive` will not register.
- **`DidSetBodyActive` is `protected virtual`** — overrideable in subclasses, but no vanilla override exists.
- **No public Add/Remove methods.** There is no `Car.AddMovementListener(ICarMovementListener)` API. Mods need either:
  - Inject a `MonoBehaviour` into `BodyTransform` *before* `DidSetBodyActive` runs (e.g., via a Component builder; see [car-definitions.md](car-definitions.md#component--icomponentbuilder-pipeline)), or
  - Harmony postfix `Car.DidSetBodyActive` to append your own listener, or
  - Harmony reflection-add to `_movementListeners` directly (works because the field is `private` not `readonly`-from-outside; Harmony reflection bypasses).
- **Cleared on body unload.** `UnloadModels` (`Car.cs:1555`) wipes the list. Cars cycle bodies during culling (see [cars-cargo.md › lifecycle](cars-cargo.md)), so any externally-injected listeners are lost on each culling-distance change.

### Single vanilla implementer

```csharp
// RollingStock/DerailedParticleController.cs:118
public void CarDidMove(MovementInfo info)
{
    CarVelocity = (info.DeltaTime == 0f) ? 0f : Mathf.Abs(info.Distance / info.DeltaTime);
    if (CarVelocity > 0.01f && _value > 0.01f)
        StartUpdateCoroutineIfNeeded();
}
```

Drives derailment dust/smoke VFX. Recomputes `CarVelocity` from `Distance/DeltaTime` rather than reading `car.velocity` — the listener is decoupled from `Car`.

### What `_movementListeners` is NOT

- **Not the only consumer of `MovementInfo`.** Two parallel fan-outs exist on locomotives, both bypassing the listener list:
  - `BaseLocomotive.FireOnMovement` (`BaseLocomotive.cs:219-226`) calls `AutoEngineerPlanner.ApplyMovement(info)` if the planner exists — feeds the AI engineer's distance-to-target tracking (`AutoEngineer.cs:1072`: subtracts `info.Distance` from each `Targets.Target.Distance`).
  - `SteamLocomotive.PositionWheelBoundsFront` override (`SteamLocomotive.cs:354-419`) calls `SubcomponentsApplyDistanceMoved(info)` (`SteamLocomotive.cs:422`) which iterates `ISteamLocomotiveSubcomponent.ApplyDistanceMoved(info, driverVelocity, absReverser, absThrottle, driverPhase)`. Vanilla implementers: `Chuff` (`RollingStock/Chuff.cs:93`), `CylinderCockController` (`Effects/CylinderCockController.cs:197`), `SteamLocomotiveWheelAnimator` (`RollingStock.Steam/SteamLocomotiveWheelAnimator.cs:252`), `SteamChuffParticleController` (`RollingStock.Steam/SteamChuffParticleController.cs:132`). This is a **richer payload** (driver phase + reverser + throttle) and a separate registration channel — registered via `_subcomponents` populated in steam-loco setup, not via `GetComponentsInChildren`.
- **Not a Messenger event.** No `Game.Events` struct fires per-tick movement. There is no Messenger surface for movement (confirmed by [events-catalog.md](events-catalog.md)). This is intentional — at 50 Hz × hundreds of cars the Messenger dispatch overhead would be expensive.
- **Not a KVO key.** `_movementListeners` is plain C#, not KVO. Cross-machine movement notification flows via `BatchCarPositionUpdate` → `RemoteIntegrationSet` → local `FireOnMovement`, NOT via KVO observers on a "moved" key.

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.FireOnMovement(MovementInfo)` | The single chokepoint. Postfix to react to every per-tick movement (host AND client). Note: `protected virtual` — subclasses (`BaseLocomotive`) override; patch the subclass too if you need locomotive-only effects. |
| `Car.DidSetBodyActive()` | Postfix to register your `ICarMovementListener` on each body load. Survives body cycling. |
| `Car.BankOdometer()` | Postfix to add custom mileage-based events (alarms, maintenance reminders). Runs every 500 m. **Host-only** — no client-side patching opportunity here. |
| `Car.ServiceMetersFromActual(float)` | Replace the wear-mileage curve. Virtual; cleaner override than patching `BankOdometer`. |
| `BaseLocomotive.ServiceMetersFromActual(float)` | Replace the tractive-effort multiplier for service-miles on locos. Virtual override. |
| `Car.PrepareForSnapshotSave()` | Hook the pre-save bank — useful for flushing other custom counters in lockstep. |
| `IntegrationSet.PositionCars` | Source of `MovementInfo`. Patching is risky (hot path). Prefer subscribing via listener. |
| `IntegrationSet.ShouldPosition` (private static) | The 1-mm filter. Patch to change the "did this car move enough to count" threshold. |

### Custom listener template

```csharp
public sealed class MyMovementListener : MonoBehaviour, ICarMovementListener
{
    private Car _car;
    private void Awake() => _car = GetComponentInParent<Car>();
    public void CarDidMove(MovementInfo info)
    {
        // info.Distance is ALWAYS ≥ 0 (abs metres). Use _car.velocity for signed direction.
        // info.DeltaTime can be 0 (snap-positions) — guard divisions.
        // info.TractiveEffort is 0 for non-loco; locos pass NormalizedTractiveEffort in [-1..1].
    }
}

// Inject during Component builder, OR Harmony postfix Car.DidSetBodyActive:
[HarmonyPostfix, HarmonyPatch(typeof(Car), "DidSetBodyActive")]
static void AddMyListener(Car __instance)
{
    var go = new GameObject("MyListener");
    go.transform.SetParent(__instance.BodyTransform, false);
    go.AddComponent<MyMovementListener>();
    // Force re-discovery — vanilla doesn't re-scan, so add to the private list directly:
    var list = AccessTools.Field(typeof(Car), "_movementListeners")
                          .GetValue(__instance) as List<ICarMovementListener>;
    list.Add(go.GetComponent<MyMovementListener>());
}
```

---

## Mileage-driven downstream effects (vanilla inventory)

Inside `BankOdometer` (host-only, every 500 m banked):

| Effect | Method | Gated by | Notes |
|---|---|---|---|
| Wear (condition decrement) | `WearForMovement(km)` (`Car.cs:2119`) | `Car.WearFeature` | The toggle gate. See [wear-durability](wear-durability.md#per-tick-wear-loop). |
| Oil consumption | `OffsetOiled(-OilUseForMovement(km))` | `EnableOiling` (in `SetOiled`) | Diesels exempt. |
| Hotbox roll | `CheckForHotbox(km)` (`Car.cs:2093`) | Speed ≥15 mph, `EnableOiling`, not loco, not already hot, distance ≥ 0.1 mi | Per-100mi chance from `Config.hotboxChanceForOil`. |
| RepairCap step-down | `Car.RepairCap` getter (`Car.cs:885`) | `Car.WearFeature` | Computed lazily from `OdometerService - LastOverhaulOdometer`; not cached. |
| Overhaul-due UI | `BuilderExtensions.AddMileageField` (`UI.CompanyWindow/BuilderExtensions.cs:49`) | Owned cars only | `Periodic` refresh frequency. |

Inside `FireOnMovement` (every tick that has movement, host AND client):

| Effect | Where | Notes |
|---|---|---|
| `ICarMovementListener.CarDidMove(info)` | `Car.cs:2061-2064` | The hookpoint. |
| `AutoEngineerPlanner.ApplyMovement(info)` | `BaseLocomotive.cs:222-225` (override) | Decrements `Targets.Target.Distance` for every AE target. |
| Steam subcomponents `ApplyDistanceMoved` | `SteamLocomotive.cs:413` (separate path) | Driver phase / chuff / cocks — runs *before* `FireOnMovement` on steam locos. |

### Things that are NOT mileage-driven

- **Fuel consumption (coal, diesel, water).** Diesel fuel and steam coal/water are throttle/firebox/time-driven, not movement-driven. Searching `OdometerService|OdometerActual` outside `Car.cs`/`RepairTrack.cs`/`BuilderExtensions.cs` returns nothing — no fuel system reads it.
- **Passenger payment.** Passenger payment uses route distance from the timetable/board (not per-car odometer); see [passengers-timetable.md › payment formula](passengers-timetable.md). The mile-count for payment is the journey distance between stops, not `OdometerActual` deltas.
- **Tag/waybill mileage.** No vanilla waybill system reads `OdometerActual`. Waybill economy is per-delivery, not per-mile.
- **No "reached X total miles" event.** No vanilla code observes `_odometer` or `_odosvc` for threshold crossings. Mods adding "achievement at 1000 mi" need their own observer on the KVO key.

---

## MP authority & save/load

### MP semantics

- **Listeners fire on host AND clients.** The host's `IntegrationSet.PositionCars` and the clients' `RemoteIntegrationSet.MoveCarTo` both produce `MovementInfo` and call into `Car.PositionWheelBoundsFront → FireOnMovement`. So `ICarMovementListener.CarDidMove` is invoked on every machine viewing the car, with each machine's local `Time.deltaTime` and computed `Distance`.
  - Host computes `Distance = |element.position - element.oldPosition|` from solver positions (`IntegrationSet.cs:184, 194`).
  - Client computes `Distance = _graph.GetDistanceBetweenClose(car.WheelBoundsA, loc)` from the interpolated/extrapolated remote frame (`RemoteIntegrationSet.cs:107`).
  - These will not be byte-identical between host and clients — listeners that need authoritative mileage must read `Car.OdometerActual` (KVO-replicated) instead of integrating `info.Distance`.
- **`BankOdometer` is host-only.** `if (StateManager.IsHost)` early gate (`Car.cs:2079`). `OdometerActual`/`OdometerService`/`LastOverhaulOdometer` updates flow host → clients via standard KVO replication.
- **No request message for mileage writes.** Like wear, there is no `RequestSetOdometer` / `RequestBankOdometer`. Clients cannot push mileage to the host. If your mod needs client-side mileage events to reach the host, define a request message (see `RequestOilCar` template referenced in [wear-durability › MP authority](wear-durability.md#mp-authority)).
- **`_lastOverhaul` is HostOnly** by `Car.HostPrefixes`. `Car.LastOverhaulOdometer` has a `public set;` but writes from clients will be rejected by the property-access-control delegate.

### Save / load

- **Pre-save bank:** `TrainController.PopulateSnapshotForSave` calls `car.PrepareForSnapshotSave()` → `BankOdometer()` for every car (`TrainController.cs:1899`). Without this, up to ~500 m of unbanked travel per car would be lost.
- **KVO snapshot:** `_odometer`, `_odosvc`, `_lastOverhaul`, `_overhaulProg` are part of `car.KeyValueObject.SnapshotValues()` (`TrainController.cs:1901`). Persistence is automatic via the standard KVO save spine (see [save-load.md](save-load.md)).
- **`_overhaulProg` Null-when-zero:** stored as `Value.Null()` when zero (`Car.cs:863`), so the save blob omits the key for cars that have never started an overhaul.
- **`_unbankedOdometerActual`/`Service` are NOT saved.** Only the banked KVO keys persist. The forced `BankOdometer()` ensures unbanked metres are folded into the saved KVO before the snapshot.
- **Restore order:** KVO keys are restored before `OnPropertiesDidRestore` runs the `Car.WearFeature = value` observer (`StateManager.cs:271-311`). So `OdometerService` is correct by the time the first FixedUpdate runs.

---

## Patch points for common mod recipes

| Mod goal | Patch | Notes |
|---|---|---|
| Per-tick "car moved" callback (mod-side) | Inject `MonoBehaviour : ICarMovementListener` under `BodyTransform` (postfix `Car.DidSetBodyActive`) | Cleanest. Receives the same `MovementInfo` vanilla uses. Survives body cycling iff postfix re-runs. |
| Intercept odometer writes | Prefix `Car.OdometerService.set` / `OdometerActual.set` (private setters; reflection or transpiler) | Or postfix `BankOdometer` to read both new values. The setters write to KVO directly — there's no method to patch other than the property. |
| Mileage-based custom event (e.g., "every 100 mi") | Postfix `BankOdometer` and inspect `OdometerActual` for threshold crossings | Host-only. Use a per-car `Dictionary<Car,float>` for last-trigger value. |
| Replace overhaul-mile cost | Override `Car.ServiceMetersFromActual` (virtual) or patch `RepairTrack.RepairCapForKilometersSinceOverhaul` | The first changes how miles accrue; the second changes how miles cap repairs. |
| Disable mileage accumulation entirely | Prefix `Car.FireOnMovement` to zero `_unbanked*` after iterating listeners | Or replace `_movementListeners` foreach to call listeners but skip the unbanked update. Beware: clients always run this method but never bank. |
| Expose movement to mod UIs | Subscribe to `Car.KeyValueObject.Observe("_odometer", ...)` for banked km updates (every 500 m); subscribe to a custom `ICarMovementListener` for per-tick smooth-graph data | KVO observer fires on host writes only (clients see them via replication, same observer pattern). |
| Per-trip mileage tracking | Custom `ICarMovementListener` integrating `info.Distance` since some "trip start" event | Beware host/client divergence — host-side listener for authoritative; client-side for cosmetic. |
| Reset overhaul cap (e.g., a "shop visit" mod tag) | `car.LastOverhaulOdometer = car.OdometerService; car.OverhaulProgress = 0f;` | Mirror what `RepairTrack.TickCar:291-292` does. **Host-only call.** |
| Tractive-effort observation | Use steam subcomponent path (`ISteamLocomotiveSubcomponent.ApplyDistanceMoved`) for richer steam payload; for diesels, read `BaseLocomotive.NormalizedTractiveEffort` directly in your `ICarMovementListener` | The standard `MovementInfo.TractiveEffort` is set on every car but only locos write a meaningful value. |

---

## Gotchas

- **`MovementInfo.Distance` is absolute (always ≥ 0).** It's `Mathf.Abs(num)` in `IntegrationSet.cs:194`. Direction must come from `car.velocity` (signed body-relative).
- **`MovementInfo.DeltaTime` can be 0.** Snap-positions (`MovementInfo.Zero`) pass `DeltaTime=0`. Guard divisions.
- **Listeners fire even when `Distance == 0`.** `MovementInfo.Zero` snap-positions iterate the listener list. Defensive listeners should check `info.Distance > 0` if they care.
- **Listeners fire on clients too.** `RemoteIntegrationSet` produces its own `MovementInfo` from interpolated frames. Distance is approximate and host-divergent. Use `Car.OdometerActual` (KVO-replicated) for authoritative mileage.
- **Listener registration is one-shot per body activation.** No public Add/Remove. Mods must inject under `BodyTransform` before/during `DidSetBodyActive`, or postfix it to add listeners after.
- **`_movementListeners` is cleared on `UnloadModels`.** Body cycling (culling) wipes the list — re-register on `DidSetBodyActive`. The list itself persists with the Car instance, but its contents reset.
- **Steam subcomponents are a parallel fan-out.** `ISteamLocomotiveSubcomponent.ApplyDistanceMoved` runs *before* `FireOnMovement` on steam locos (`SteamLocomotive.cs:413, 418`). They do NOT live on `_movementListeners` — they're collected separately into `_subcomponents` during steam loco setup. Patching `FireOnMovement` won't catch steam subcomponent calls.
- **`AutoEngineerPlanner.ApplyMovement` is in `BaseLocomotive.FireOnMovement`, not the listener list.** Same idea — direct field access, not the `ICarMovementListener` channel. Patch `BaseLocomotive.FireOnMovement` to intercept AI distance-tracking.
- **`OdometerService` keeps climbing while wear is off.** The increment is unconditional; only `WearForMovement` and `RepairCap` short-circuit. See [wear-durability › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).
- **`OdometerService` units are kilometres**, but every UI surface reports miles via `× 0.6213712`. Don't confuse the storage unit (km) with the display unit (mi). The `Car.OverhaulMiles` field is in **miles** — the `RepairCap` math compensates by multiplying km by 0.6213712 (`RepairTrack.cs:409`, `Car.cs:893` indirectly via `RepairCapForKilometersSinceOverhaul`).
- **`/repair` doesn't reset `LastOverhaulOdometer`.** A `/repair` console call sets `_condition = 1` on every coupled car but `RepairCap` remains clamped. See [wear-durability › gotchas](wear-durability.md#gotchas).
- **`debugCondition` flag also enables `dmg`/`dps` debug graphs in `ApplyCurvatureToModel`.** Not a movement-listener concern, but useful for dialing custom listeners that interact with condition.
- **Tractive effort is normalised in [-1..1].** `Car.NormalizedTractiveEffort` returns 0 for non-locos. The sign indicates direction-of-effort, not direction of motion.
- **`PrepareForSnapshotSave` is *only* called from snapshot save** (`TrainController.cs:1899`). Autosave + manual save funnel through it. Quitting without saving loses unbanked metres — a 50-car train at end-of-session loses up to ~25 km of mileage that was below the 500 m bank threshold per car.
- **`_unbankedOdometerActual`/`Service` are not zeroed on body unload.** They persist across body cycle. They're zeroed only by `BankOdometer` (host-only) and ctor-default (Car instance creation). On clients they grow forever (mostly harmless — never read), but a long-running client session technically leaks float precision.

---

## Cross-references

- **Wear / oil / hotbox consumers** of banked mileage: see [wear-durability.md](wear-durability.md#per-tick-wear-loop).
- **Snapshot save spine** that calls `PrepareForSnapshotSave`: see [save-load.md](save-load.md).
- **`IntegrationSet.Tick` four-iteration solver** that drives `PositionCars`: see [integration-set-solver.md](integration-set-solver.md).
- **`KeyValueObject` semantics** (HostOnly, observers, replication): see [cars-cargo.md › KVO key map](cars-cargo.md) and [access-control.md](access-control.md).
- **Steam subcomponent `ApplyDistanceMoved`** chuff/cock/wheel-animator details: see [audio.md](audio.md) and the rolling-stock per-component coverage there.
- **`AutoEngineerPlanner.ApplyMovement`** distance-target tracking: see AI engineer surveys (no dedicated crib sheet yet — candidate for future).
- **No Messenger movement event** — confirmed in [events-catalog.md](events-catalog.md).
