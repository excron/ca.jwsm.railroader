# Consist & Integration Set — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Integration-set solver internals](integration-set-solver.md), [Couplers](couplers.md), [Wear & Durability](wear-durability.md)

A "consist" in Railroader is the `IntegrationSet` — an ordered list of `Element`s wrapping `Car`s, sharing a single 1-D position axis. There is **no `Train` type**; the consist *is* the integration set. Sets are created/dissolved/split/unioned by `IntegrationSetManager` in response to topology changes that `TrainController.UpdateSets()` computes from spatial neighbour scans every `FixedUpdate`. The host owns the canonical sets; clients receive `CarSetAdd` / `CarSetRemove` / `CarSetChangeCars` deltas plus periodic `BatchCarPositionUpdate`s, and rebuild a `RemoteIntegrationSet` per consist for interpolation/extrapolation only — clients do not run the solver.

This sheet covers the *container* — set lifecycle, member relationships, replication, and how `Car` plugs into the set. The slack solver and per-tick physics math live in the [companion sheet](integration-set-solver.md).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `IntegrationSet` | `Model.Physics/IntegrationSet.cs:15` | The consist. Owns ordered `_elements`, runs solver, fires events |
| `IntegrationSet.Element` (protected nested) | `IntegrationSet.cs:17` | Per-car solver state: position, oldPosition, acceleration, slack |
| `IntegrationSetManager` | `Model.Physics/IntegrationSetManager.cs:17` | Global registry of all sets; owns Add/Remove/Split/Union + delta replication |
| `RemoteIntegrationSet` | `Model.Physics/RemoteIntegrationSet.cs:12` | Client-side variant: extrapolates from `BatchCarPositionUpdate` frames |
| `IIntegrationSetEventHandler` | `Model.Physics/IIntegrationSetEventHandler.cs` | Set→world callbacks. **Only impl is `TrainController`** |
| `TrainController.FixedUpdate()` | `TrainController.cs:419` | Canonical 50 Hz hook. Air → physics → topology → networking, in that order |
| `TrainController.UpdateSets(IEnumerable<Car>)` | `TrainController.cs:1033` | Topology reconciler — drives all Union/Split decisions |
| `Car.set` | `Model/Car.cs:725` | Backref from car to its set. Setter calls `ResetAtRest()` |

---

## Set lifecycle spine

```
TrainController.FixedUpdate (50 Hz)                          ← TrainController.cs:419
   │
   ├─ Physics.autoSyncTransforms = false  (perf)
   ├─ _spatialHash.UpdateIfNeeded()
   ├─ UpdateSets()                                           ← drains _carsForUpdateSets
   │   └─ for each moved car: scan ahead/behind 3m
   │      └─ Union / Split / CreateIntegrationSet            ← topology reconcile
   │      └─ _integrationSets.SendDelta()                    ← MP fan-out
   │
   ├─ foreach car: car.air.FixedUpdateAir(dt)                ← AIR FIRST (feeds brake force)
   │
   ├─ foreach IntegrationSet s in _integrationSets:
   │       if !s.ShouldSkipTick (= !AllCarsAtRest):
   │           s.ValidateConsistency()                       ← may fire BreakConnections / Reconnect
   │           s.Tick(dt)                                    ← THE PHYSICS PASS
   │
   ├─ _spatialHash.UpdateIfNeeded()
   ├─ UpdateSets()                                           ← second reconcile after motion
   ├─ _integrationSets.RemoveEmpty()                         ← cleanup empty sets
   │
   └─ if (IsHost && Multiplayer.Client != null):
          SendCarPositionsIfNeeded()                         ← BatchCarPositionUpdate
          SendAirIfNeeded()                                  ← BatchCarAirUpdate
```

**Note:** `UpdateSets()` runs **twice** per `FixedUpdate` — once before air/physics (consuming any motion accumulated since last tick), and once after physics moved cars this tick. Both run `SendDelta()` (`IntegrationSetManager.cs:45`) which fans out `CarSetAdd`/`CarSetRemove`/`CarSetChangeCars` and triggers `BatchCarPositionUpdate` for newly-added/changed sets.

### Set creation paths

| Path | File:Line | Triggered by |
|---|---|---|
| `CreateIntegrationSet(IReadOnlyCollection<Car>)` | `TrainController.cs:1152` | `HandleCreateCarsAsTrain` (place train), `UpdateSets` for orphan car |
| `IntegrationSetManager.Union(car1, car2)` | `IntegrationSetManager.cs:196` | `UpdateSets` when two adjacent cars are in different sets (or orphans) |
| `IntegrationSet.AddCar(Car)` | `IntegrationSet.cs:714` | `Union` when one side is null |
| `RemoteIntegrationSet` via `CarSetAdd` message | `TrainController.cs:1279` → `IntegrationSetManager.AddWithoutDelta` | Client receives a host-broadcast set |
| Restore from snapshot | `TrainController.cs:1624`, `IntegrationSetManager.AddWithoutDelta` (`:271`) | Save load |

### Set destruction paths

| Path | File:Line | Triggered by |
|---|---|---|
| `IntegrationSetManager.Remove(set)` | `IntegrationSetManager.cs:174` | `RemoveEmpty()` when `IsEmpty` after car removal |
| `RemoveCar(Car)` | `IntegrationSetManager.cs:234` → `IntegrationSet.RemoveCar` (`:994`) | Car despawn, car re-creation, `WillMove` |
| `Clear()` | `IntegrationSetManager.cs:228` | World unload |
| `RemoveWithoutDelta(uint)` | `IntegrationSetManager.cs:278` | Client receives `CarSetRemove` |

### Set split/merge (host-only)

```csharp
public void Split(Car car1, Car car2)                                 // IntegrationSetManager.cs:180
public void Union(Car car1, Car car2)                                 // :196
```

Both methods are wrappers around `IntegrationSet.Split` / `IntegrationSet.Union` plus delta-bookkeeping. `Union` handles all four cases (null/null, set/null, null/set, set/set). `Split` requires the two cars be **adjacent in the set** (`IntegrationSet.Split` validates with an exception, `:743`).

`Split` calls `EventHandler.IntegrationSetRequestsBreakConnections` on **both** cars at the split seam (`IntegrationSet.cs:760-761`) — see [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths).

### Patch candidates (lifecycle)

| Method | Why patch |
|---|---|
| `TrainController.FixedUpdate` (postfix) | The single canonical 50 Hz hook. Mods that need to inject custom physics passes, custom forces, or post-tick observation should patch here, not per-set |
| `TrainController.UpdateSets(IEnumerable<Car>)` | Topology reconciler. Patch to override coupling-distance heuristics, suppress auto-coupling per car, or add custom set-membership rules |
| `IntegrationSetManager.Union` / `Split` | Set-level orchestration. Patch prefix to veto a Union (mod-side "magnetic" couplers), postfix to record events |
| `IntegrationSet.AddCar` | Per-set member addition; useful for mod-side per-set caches (recompute when membership changes) |
| `IntegrationSet.RemoveCar` | Same for removal — pairs with `AddCar` |
| `TrainController.CreateIntegrationSet` (private) | Set instantiation. Note the lambda-form factory at `TrainController.cs:395` (`_integrationSets.CreateIntegrationSet = (id, cars) => CreateIntegrationSet(cars, id)`) — replace by reassigning the delegate post-`OnEnable` if you need a subclassed `IntegrationSet` |

---

## `Model.Physics.IntegrationSet` (the consist container)

Plain `class` (not `MonoBehaviour`). `protected` constructors — instantiated via `IntegrationSet.Create(uint, ...)` static factory (`:105`), which returns a `RemoteIntegrationSet` when `activeCars=false`, otherwise `IntegrationSet`.

### Public surface

```csharp
public bool   Dirty = true;                              // set when positions move; drives MP send
public float  LastSentTime;                              // ms timestamp of last batch update
public readonly uint Id;                                 // assigned by IIntegrationSetEventHandler.GenerateIntegrationSetId
public IEnumerable<Car> Cars => _elements.Select(e => e.car);
public bool   IsEmpty       => _elements.Count == 0;
public int    NumberOfCars  => _elements.Count;
public virtual bool ShouldSkipTick => AllCarsAtRest();   // overridden in RemoteIntegrationSet → false

public bool AllCarsAtRest();                             // :148
public virtual void Tick(float dt);                      // :160 — the per-FixedUpdate physics pass
public void AddCar(Car car);                             // :714
public void Union(IntegrationSet other);                 // :725
public void Split(Car car1, Car car2, out IntegrationSet newSet);     // :739
public void RemoveCar(Car car, out IntegrationSet newSet);            // :994 — may split if mid-set
public void RemoveCarInternal(Car car);                  // :1014 — no event/break, used by ChangeCarsWithoutDelta
public void ValidateConsistency();                       // :906
public void AddVelocityToCar(Car car, float vel, float maxVel);       // :1102
public void SetVelocity(float velocity, IReadOnlyList<Car> cars);     // :1192
public bool ContainsBrokenConstraints();                 // :1200 — sanity check (>0.8m or >1.2× slack)

public IEnumerable<Car> EnumerateCoupledTo(Car, LogicalEnd);          // :771
public IEnumerable<Car> EnumerateAirOpenTo(Car, LogicalEnd);          // :776
public int    StartIndexForConnected(Car, LogicalEnd, EnumerationCondition);  // :836
public Car    NextCarConnected(ref int idx, LogicalEnd, EnumerationCondition, out bool stop);  // :874

public Car    GetAirConnection(Car, LogicalEnd);          // :1022
public Car    GetCouplerConnection(Car, LogicalEnd);      // :1044
public bool   TryGetCoupledCar(Car, End, out Car);        // :1066
public bool   TryGetAdjacentCar(Car, LogicalEnd, out Car);// :1072
public PositionInSet PositionOfCar(Car);                  // :1084 — A | Inside | B | Solo
public int?   IndexOfCar(Car);                            // :963 (uses Car.CachedSetIndex)
public void   OrderAB(ref Car a, ref Car b);              // :1119

public Snapshot.CarSet Snapshot();                        // :1132
public void SetPositions(List<float>, List<bool> frontIsAs, bool immediate);  // :1143
public void SendBatchCarPositionUpdate(ClientManager, bool critical);         // :1185
public virtual void HandleCarPositionUpdate(Location, float[], float[], long);// :1180 — overridden in Remote
```

### Internal state

```csharp
private readonly IIntegrationSetEventHandler EventHandler;
protected readonly List<Element> _elements;              // ORDERED — index 0 is "A end", last is "B end"
protected readonly Graph _graph;
private float? _lowerBound;                              // bound positions if a foreign car is ahead
private float? _upperBound;
private bool   _lastTickPositioned;
private bool   _hasUpdatedBoundsOnce;
private int    _ticksSinceRebuild;                       // every 100 positioned ticks → RebuildPositions

private const float minCouplerSeparation = 1f;          // 1m centerline gap (CarRadius-to-CarRadius)
```

`_elements` is the *single source of truth* for per-car physics state. `Car.velocity` is computed *from* `Element.position - Element.oldPosition` (`IntegrationSet.cs:186`); it's not an independent value.

### `Element` (protected nested class)

```csharp
protected class Element {                                // IntegrationSet.cs:17
    public readonly Car   car;
    public float position;                               // 1-D consist-space position, meters
    public float oldPosition;                            // Verlet previous position
    public float acceleration;                           // m/s² in consist space
    public readonly float SlackA, SlackB;                // per-end slack tolerance (cached from Car.CouplerSlack)
    public readonly float CarRadius;                     // car.carLength / 2
    public float SlackStretch;                           // <0 compressed, >0 in tension
    public bool  SlackStretchDidChangeDirection;         // collision-event trigger
    public float PositionAtLastLocationUpdate;
    public float Velocity => position - oldPosition;     // PER-TICK delta — NOT m/s! divide by dt for m/s

    public Element(Car car) {
        SlackA = car.CouplerSlack(car.LogicalToEnd(Car.LogicalEnd.A));
        SlackB = car.CouplerSlack(car.LogicalToEnd(Car.LogicalEnd.B));
        CarRadius = car.carLength / 2f;
    }
}
```

**Critical:** `Element.Velocity` is `position - oldPosition` (a per-tick delta in meters), **not** m/s. Vanilla code at `IntegrationSet.cs:494` divides by `wholeDeltaTime` to get m/s. Mods that read `Element.Velocity` directly will get a value 50× too small at 50 Hz.

`Element` is `protected`, only accessible to subclasses. Mods that need element state must either (a) subclass `IntegrationSet` and replace via the `CreateIntegrationSet` delegate, or (b) reflect into `_elements`. Practical approach: read `Car.velocity` (which the solver writes back at `:186`) and `Car.set` for set membership.

### Patch candidates (set core)

| Method | Why patch |
|---|---|
| `IntegrationSet.Tick(float dt)` | The per-set physics pass. **Risky** — runs per-set per-tick. Prefer postfixing `TrainController.FixedUpdate` if you only need observation |
| `IntegrationSet.IntegrateConstraints` (private) | The constraint solver iteration. See [solver crib sheet](integration-set-solver.md#integrateconstraints) |
| `IntegrationSet.UpdateAcceleration` (private) | Source of `Element.acceleration` from `TractiveForce + GravityForce`. **Patch here to inject custom forces** (e.g. wind, magnetic brake), not per-car |
| `IntegrationSet.ApplyBrakes` (private) | Brake retarding force application. See [solver crib sheet](integration-set-solver.md#brake-integration) |
| `IntegrationSet.RebuildPositions` (private) | Resets positions from world locations. Patch postfix if you maintain mod-side per-element state that tracks position |
| `IntegrationSet.SortElements` (private) | Topological sort after add/remove. See [solver crib sheet](integration-set-solver.md#sortelements) |
| `IntegrationSet.ValidateConsistency` | Per-tick coupler/air consistency checker; fires BreakConnections / Reconnect |

---

## Member relationships: `Car` ↔ `IntegrationSet`

### Car-side fields and helpers

```csharp
public IntegrationSet set { get; set; }                   // Car.cs:725 — backref
   // setter: ResetAtRest() — every set reassignment clears the at-rest timer

internal int? CachedSetIndex;                             // Car.cs:322 — IntegrationSet.IndexOfCar caches here
public bool FrontIsA = true;                              // Car.cs:269 — orientation in consist axis
public float Orientation => FrontIsA ? 1 : (-1);          // Car.cs:759
public void Reverse() { FrontIsA = !FrontIsA; }           // Car.cs:1862

public float velocity { get; set; }                       // Car.cs:653 — written by IntegrationSet.PositionCars
public float VelocityMphAbs => Mathf.Abs(velocity * 2.23694f);
public bool  IsAtRest        { get; }                     // :713 — _atRestSince has elapsed > 3s
public void  ResetAtRest()                                // :916 — _atRestSince = null
public bool  IsOnTurntable   { get; }                     // :701

public float carLength;                                   // :275
public float Weight => Definition.WeightEmpty + _loadWeight;  // :738
public float TractiveForce => TractiveForceMultiplier * TractiveEffort;  // :751
public static float TractiveForceMultiplier { get; set; } = 1.1f;        // :753 — global modder hook
public float GravityForce { get; }                        // :740 — _grade*100 * (Weight/2000) * 20
public float compensatingAcceleration;                    // :324 — used by client (RemoteIntegrationSet)
public float maxSpeedMph = 100f;                          // :339 — per-car cap, used in CalculateRetardingForce

public bool TryGetAdjacentCar(LogicalEnd, out Car);       // :2781 — delegates to set
public IEnumerable<Car> EnumerateCoupled(LogicalEnd);     // :2949
public IEnumerable<Car> EnumerateAirOpen(LogicalEnd);     // :2959
public void SetOffsetWithinSet(float pos);                // :2973 — solver writes here per-tick → updates trucks

public virtual bool WantsEndGear(End end);                // :1761 — false for tender front
public virtual bool ForceConnectedToAtRear(Car other);    // :1770 — overridden in SteamLocomotive
public virtual bool RequiresConnectionToEnd(End end);     // :2741 — overridden in SteamLocomotive (R if hasTender)
public float CouplerSlack(End end);                       // :1775 — 0.02m if WantsEndGear, else 0.001m

public virtual void WillMove();                           // :2791 — host-side: sever both ends + adjacents
public void SetAdjacentCarsNotConnected();                // :2812 — paired with WillMove
```

### Tender↔Engine forced reconnection (`ValidateConsistency`)

`IntegrationSet.ValidateConsistency` (`:906`) walks adjacent pairs each tick (after-skip-check, before `Tick`):

1. If `EndGearB.IsCoupled` differs between adjacent cars → both ends broken (`:921`).
2. If `EndGearB.IsAirConnected` differs → both ends broken (`:926`).
3. If `car.ForceConnectedToAtRear(car2) && car.FrontIsA && (...not coupled or aired...)` → request reconnect (`:928`).
4. If `car2.ForceConnectedToAtRear(car) && !car2.FrontIsA && (...)` → request reconnect (`:932`).
5. First/last car with dangling end → request break (`:946`, `:952`).

`Car.ForceConnectedToAtRear(Car other)` is `false` by default (`Car.cs:1770`). `SteamLocomotive` overrides:

```csharp
public override bool ForceConnectedToAtRear(Car other) {       // SteamLocomotive.cs:71
    if (!hasTender) return false;
    return base.Archetype == CarArchetype.Tender;              // ← checks SELF, not 'other'
}
```

**HIGH-VALUE FINDING (likely vanilla bug):** the override returns true only when *this car's own* archetype is `Tender`. But `SteamLocomotive` is `LocomotiveSteam`, never `Tender`. So `engine.ForceConnectedToAtRear(tender)` always returns **false** — the engine never triggers the reconnect path. The reconnect actually fires from the *tender* side: `ApplyEndGearChange(LogicalEnd.A, ...)`. But `Car.RequiresConnectionToEnd` (`:2741`) is also overridden by `SteamLocomotive` (`:345`):

```csharp
protected override bool RequiresConnectionToEnd(End end) {
    if (hasTender) return end == End.R;
    return false;
}
```

That blocks *uncoupling* of the engine's R end via `ValidateEndGearChange`. The `ForceConnectedToAtRear`-driven *re-coupling* path in `ValidateConsistency` is the second line of defense. The fact that the override is on the wrong half means the reconnect path may never run for the engine→tender pair. Worth verifying with logs in-game; if confirmed, fix is `return other != null && other.Archetype == CarArchetype.Tender;`.

The `Reconnect` handler at `TrainController.cs:1267` re-asserts both `IsCoupled` and `IsAirConnected` host-side (`engine.End.R` and `tender.End.F`).

### `Car.WillMove()` — manual hard-break

```csharp
public virtual void WillMove() {                           // Car.cs:2791
    EndGearF.NeedsConnectionUpdate = true;
    EndGearR.NeedsConnectionUpdate = true;
    _isFirstPosition = true;
    _grade = 0f;
    _velocityZeroTime = null;
    velocity = 0f;
    compensatingAcceleration = 0f;
    _lastCurvatureUpdate = 0f;
    _hasReceivedDistanceBand = false;
    if (StateManager.IsHost) {
        ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsCoupled, false);
        ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsCoupled, false);
        ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsAirConnected, false);
        ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsAirConnected, false);
        SetAdjacentCarsNotConnected();
    }
}
```

Called when player picks up a car or before re-positioning. After `WillMove`, the car is unhooked from neighbours but **still in the set** until the next `UpdateSets()` reconciles topology. Adjacent cars also have their facing ends cleared (`SetAdjacentCarsNotConnected`, `:2812`).

---

## `IntegrationSetManager` (global registry)

```csharp
public Func<uint, IReadOnlyCollection<Car>, IntegrationSet> CreateIntegrationSet;   // injected delegate

private readonly Dictionary<uint, IntegrationSet> _integrationSets;
private uint _idCursor;
private readonly HashSet<uint> _deltaAdded, _deltaRemoved, _deltaChanged;
private HashSet<IntegrationSet> _needsBatchPositionUpdate;

public IEnumerator<IntegrationSet> GetEnumerator();      // iterates Values
public uint  GenerateId();                               // cursor that skips occupied slots (:143)
public void  Add(IntegrationSet);                        // :168
public void  Remove(IntegrationSet);                     // :174
public void  Split(Car, Car);                            // :180 — wraps IntegrationSet.Split + delta tracking
public void  Union(Car, Car);                            // :196 — handles all 4 null/non-null cases
public void  RemoveCar(Car);                             // :234
public void  Clear();                                    // :228
public void  RemoveEmpty();                              // :152

public void  SendDelta();                                // :45 — drains _deltaAdded/Removed/Changed
public void  ClearDeltas();                              // :310
public void  HandleBatchCarPositionUpdate(BatchCarPositionUpdate, Graph);  // :250
public void  AddWithoutDelta(Snapshot.CarSet, Dictionary<string,Car>);     // :271 — for save load + CarSetAdd
public void  RemoveWithoutDelta(uint setId);                               // :278
public void  ChangeCarsWithoutDelta(Snapshot.CarSet, Dictionary<string,Car>);  // :283
public void  LogState();                                                   // :300
```

`SendDelta` flushes inside a `StateManager.TransactionScope()`:

1. `CleanupDeltas` — collapse Add+Remove of the same id into nothing.
2. Send `CarSetRemove` for everything in `_deltaRemoved`.
3. Send `CarSetAdd` for everything in `_deltaAdded` (also marks needsBatchPositionUpdate).
4. Send `CarSetChangeCars` for everything in `_deltaChanged` (also marks needsBatchPositionUpdate).
5. For every newly-added/changed set, call `SendBatchCarPositionUpdate(client, critical:true)`.
6. `ClearDeltas()` in finally.

### Patch candidates (manager)

| Method | Why patch |
|---|---|
| `IntegrationSetManager.SendDelta` | All MP set-topology fan-out goes through here. Patch postfix to mirror state to mod clients (e.g. dispatch mod) |
| `IntegrationSetManager.RemoveCar` | Single chokepoint for car removal. Useful for dispatch mod to invalidate consist references |
| `IntegrationSetManager.Union` / `Split` | Topology mutation; prefix to veto, postfix to log/event |

---

## `RemoteIntegrationSet` (client-side replication)

```csharp
public class RemoteIntegrationSet : IntegrationSet {     // RemoteIntegrationSet.cs:12

    private struct Frame(Location[], float[] velocities, long tick);
    private readonly List<Frame> _frames;                // sorted by tick; usually 2-3
    private Frame _extrapolated;

    private long DisplayTick => Multiplayer.Client.Tick - 300;   // 300-tick (6s @ 50Hz) play-back delay
    public override bool ShouldSkipTick => false;        // always ticks (no AllCarsAtRest skip)

    public override void Tick(float dt);                 // :50 — interp/extrap, NO physics
    private void MoveBetween(float dt, Frame, Frame, long displayTick);   // graph.Lerp between snapshots
    private void MoveTo(float dt, Frame);
    private Frame Extrapolate(Frame, long displayTick);  // capped to ±4 seconds
    public override void HandleCarPositionUpdate(Location, float[], float[], long updateTick);  // :134
}
```

Clients receive `BatchCarPositionUpdate` and store frames; `Tick` interpolates between the two frames bracketing `DisplayTick`. With only one frame or `DisplayTick < frame.Tick`, it extrapolates from the latest frame's velocity (clamped to ±4s of extrapolation).

**Key implications:**

- **Clients do NOT run the constraint solver.** No slack accounting, no auto-couple detection, no collision events client-side. Everything physics-derived is computed host-side and shipped via the deltas + batch updates.
- Client view is ~6 seconds behind real time (300 ticks @ 50Hz). This is the interpolation buffer.
- `ValidateConsistency` is a no-op on clients because `ActiveCars=false` (`IntegrationSet.cs:908`). Clients never break or reconnect connections — they just observe KVO writes.
- `SortElements` is also gated by `ActiveCars` (`:587`). Client sets keep whatever order the host's `Snapshot.CarSet` shipped.
- `RemoteIntegrationSet` ctor still calls base ctor, which calls `RebuildPositions()` and `LogSet("Init")` — so clients do see initial positions, then `SetPositions(immediate:true)` overrides them via the `BatchCarPositionUpdate` arrival.

### Network message reference

| Message | File | Direction | Trigger |
|---|---|---|---|
| `CarSetAdd` | `Game.Messages/CarSetAdd.cs` | Host → Client | New set created |
| `CarSetRemove` | `Game.Messages/CarSetRemove.cs` | Host → Client | Set destroyed (empty) |
| `CarSetChangeCars` | `Game.Messages/CarSetChangeCars.cs` | Host → Client | Membership changed (Union/Split/AddCar) |
| `BatchCarPositionUpdate` | `Game.Messages/BatchCarPositionUpdate.cs` | Host → Client | Periodic (100ms throttle, 5s force, or critical on add/change) |
| `BatchCarAirUpdate` | `Game.Messages/BatchCarAirUpdate.cs` | Host → Client | Per-set when any car has `air.NeedsSend` |

All five are `[HostOnlyAuthorizationRule]`. Snapshot wire format:

```csharp
public struct Snapshot.CarSet {                          // Game.Messages/Snapshot.cs:54
    public uint Id;
    public List<string> CarIds;                          // ordered same as IntegrationSet._elements
    public List<float>  Positions;                       // distance from prev car's WheelBoundsA (per-element)
    public List<bool>   FrontIsAs;                       // orientation per car
}
```

`BatchCarPositionUpdate` contains start `TrackLocation` (where car[0]'s `WheelBoundsA` lives), per-car distances, and per-car `ushort`-encoded velocities (`Mathf.FloatToHalf`). See `IntegrationSet.CreateBatchCarPositionUpdate` (`:1158`).

### Patch candidates (replication)

| Method | Why patch |
|---|---|
| `IntegrationSetManager.SendDelta` | Hook every topology delta. Best place to mirror to mod-side WebSocket (event-bus mods) |
| `IntegrationSet.SendBatchCarPositionUpdate` | Per-set position broadcast. Patch to throttle/augment |
| `RemoteIntegrationSet.HandleCarPositionUpdate` | Client-side frame intake. Patch to inject mod-controlled positions |
| `RemoteIntegrationSet.Tick` | Client-side display update. Risky — replaces interpolation |

---

## Topology reconciliation: `TrainController.UpdateSets`

```csharp
private void UpdateSets(IEnumerable<Car> cars)           // TrainController.cs:1033 — host only (early return otherwise)
```

For each car in `_carsForUpdateSets` (cars that have moved since last reconcile):

1. Probe 3m ahead (off `WheelBoundsF`) and 3m behind (off `WheelBoundsR`) for any car (via `_spatialHash`).
2. Same-route check: `graph.CheckSameRoute` filters out parallel-track ghosts.
3. If `car.set == null`:
   - Both neighbours present → `Union(car, neighbourA)` then `Union(car, neighbourB)`.
   - One neighbour → `Union(car, neighbour)`.
   - None → `CreateIntegrationSet(new[] { car })`.
4. If `car.set != null`:
   - Loop up to 8 iterations (`for (i=0; i<8; i++)`) reconciling Position-in-set vs. detected neighbours:
     - Wrong neighbour at A end while interior → `Split` at the offending side.
     - Missing neighbour at expected end → `Split`.
     - Foreign neighbour belongs to different set → `Union`.
   - If loop exceeds 8 iterations → `Log.Error` (`:1142`).

Final step: `_integrationSets.SendDelta()` (`:1145`).

The 8-iteration safety cap suggests the reconciler can churn under topology corner cases (e.g. a car straddling a switch point with neighbours on both legs). Patches that change `WantsEndGear` or `CouplerSlack` may not change reconciliation — neighbour detection uses geometric distances, not slack tolerance.

---

## Per-car physics state pulled from `Car` (read by solver)

`IntegrationSet.UpdateAcceleration` (`:385`) reads:

```csharp
car.Weight                     // lb-mass; converted to kg via *0.453592 inside loop
car.TractiveForce              // lb-force; converted to N via *4.44822
car.GravityForce               // lb-force; same conversion
car.Orientation                // ±1
car.compensatingAcceleration   // m/s² — only used when ActiveCars=false (i.e., remote set)
```

`IntegrationSet.CalculateRetardingForce` (`:465`) reads:

```csharp
car.air.brakePercent           // input to CalculateBrakingForce
car.maxSpeedMph                // overspeed wall
car.Weight                     // for Davis-equation rolling resistance (1.3 + 29/T + 0.045·v + 0.063·v²/T)
car.CalculateBrakingForce(brakePercent, absVelocity)         // → newtons
car.CalculateCurvatureRetardingForce(absVelocity)            // → newtons
car.CalculateDerailedRetardingForce()                         // → Weight*0.7 if derailed, else 0
```

See [Brakes & traction] *(future crib sheet — not yet written; see physics-vanilla-survey.md § Air & brakes)* for `CarAirSystem`/`LocomotiveAirSystem` details. Cross-reference: `Car.CalculateBrakingForce` at `Car.cs:2991`.

**HIGH-VALUE FINDING:** `CalculateRetardingForce` adds an unconditional **overspeed wall** (`:473`):

```csharp
float num6 = Mathf.Exp((num - car.maxSpeedMph) / 10f + 7.1f);  // exponential drag past maxSpeedMph
```

This term is in *newtons of retarding force* and grows exponentially. At `velocity = maxSpeedMph` it's `Exp(7.1) ≈ 1212 N`. At +10 mph over, `Exp(8.1) ≈ 3294 N`. Cars cannot meaningfully exceed `maxSpeedMph` because the drag explodes. Patching `Car.maxSpeedMph` per car raises the cap; replacing the formula in `CalculateRetardingForce` removes the wall entirely.

---

## Speed limit enforcement (per-element vs. whole-set)

There is **no consist-wide speed limit** in vanilla. Each `Element` has its own:

- `car.maxSpeedMph` (per-car) → exponential drag in `CalculateRetardingForce` (per-element in `ApplyBrakes`).
- The AutoEngineer (`Model.AI/AutoEngineer.cs`) uses `TrainMath.MaximumSpeedMphForCurve` for *planning*, but it sets throttle/brake — it never touches the solver directly. (Survey note: `TrainMath.DerailmentForSpeedOnCurve` is *defined* but never called from physics.)
- Track speed limits are not enforced at all in the solver — only `ApplyCurvatureToModel` damages cars over the curve limit (see [wear-durability › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings)).

Mods that want a real per-set speed limit should patch `IntegrationSet.UpdateAcceleration` (cap individual `element.acceleration` based on max element velocity in set) or `IntegrationSet.CalculateRetardingForce` (replace the exp wall with a clamp-based one).

---

## Derailment cascade through the set

Derailment is *per-car*; there is **no automatic propagation to neighbours**.

The cascade *can* happen through two mechanisms:

1. **Auto-uncouple at threshold 0.25** — `Car.ApplyDerailmentDelta` severs both ends when crossing 0.25 (`Car.cs:2341`). After uncouple, `UpdateSets` splits the set on the next FixedUpdate.
2. **Collision damage chain** — a derailed car has `CalculateDerailedRetardingForce()` returning `Weight * 0.7` (`Car.cs:2905`), creating a huge retarding force. Following cars run into it, `IntegrationSetCarsDidCollide` fires with sufficient `deltaVelocity` to trigger `ApplyDerailmentForce` on them, and they derail in turn. See [couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline).

So derailment "cascades" only through the collision pipeline. There's no per-tick "is any neighbour derailed" propagation. Patching `Car.ApplyDerailmentDelta` (`Car.cs:2318`) is the entry point if you want to force-propagate derailment to coupled cars.

---

## Save/restore

```csharp
TrainController.cs:1894    snapshot.CarSets = _integrationSets.Select(s => s.Snapshot()).ToDictionary(...)
TrainController.cs:1624    foreach (carSet in snapshot.CarSets) → CreateIntegrationSetInstance(carSet.Id, list)
                                                              → integrationSet.SetPositions(positions, frontIsAs, immediate:true)
                                                              → _integrationSets.Add(integrationSet)
TrainController.cs:1641    _integrationSets.ClearDeltas()    ← suppress the load-time delta noise
```

`SetPositions(immediate:true)` calls `PositionCars(0f, isInitialPosition:true)` (`IntegrationSet.cs:1154`). With `dt=0`, velocities aren't recomputed but positions are set in world space.

**Gotcha:** `Snapshot.CarSet.Positions` is the **per-car distance from previous car's WheelBoundsA**, not world coordinates (`IntegrationSet.CreateBatchCarPositionUpdate` `:1162`). The `Positions` field on the in-memory snapshot matches `Element.position` (consist-space). Two different "positions" in the codebase share the name — be careful.

---

## Patch points summary (cross-cutting)

| Goal | Patch site |
|---|---|
| Insert custom forces (wind, brake, magnetic, slope mod) | `IntegrationSet.UpdateAcceleration` postfix, modify `element.acceleration` |
| Insert custom retarding force (rolling resistance mods) | `IntegrationSet.CalculateRetardingForce` postfix |
| Replace Verlet integrator | `IntegrationSet.ApplyVerlet` prefix returning `false` |
| Add a custom physics pass each tick | `TrainController.FixedUpdate` postfix |
| Observe set creation/destruction | `IntegrationSetManager.Add`/`Remove` postfix |
| Observe set membership change | `IntegrationSetManager.SendDelta` postfix (catches Add+Change+Remove in one place) |
| Observe per-tick element state | Patch `TrainController.FixedUpdate` postfix, iterate `_integrationSets` and read each `Car.velocity`/`Car.set` |
| Replace solver | Implement subclass `IntegrationSet`, reassign `_integrationSets.CreateIntegrationSet` delegate after `TrainController.OnEnable` runs |
| Replace event handler (collision/couple/break) | `TrainController` is the only `IIntegrationSetEventHandler`; replacing the handler requires reflection on `EventHandler` field — patching `TrainController` methods is far easier |
| Force-replicate physics state to mod clients | `IntegrationSetManager.SendDelta` postfix; or hook `BatchCarPositionUpdate` send |

---

## Gotchas

- **`Element.Velocity` is a per-tick delta, not m/s.** Divide by `Time.fixedDeltaTime` (or the `wholeDeltaTime` arg in `IntegrateConstraints`) to get m/s. Vanilla code does this inline.
- **`Car.velocity` IS m/s, signed in body-relative axis.** Multiply by `Car.Orientation` to get consist-axis m/s; multiply by `2.23694` to get mph. `VelocityMphAbs` does both for absolute.
- **Set can be `null` on a car**, especially briefly after `WillMove()` and during `ChangeCarsWithoutDelta`. `Car.set` setter calls `ResetAtRest()` even when set to null. Always null-check.
- **`CachedSetIndex` is invalidated only by `InvalidateCachedCarIndexes()`** (called in ctor, AddCar, Union, RemoveCar, RemoveCarInternal, Split). If you reorder `_elements` directly via reflection, the cache will be stale and `IndexOfCar` will return wrong values.
- **`AllCarsAtRest()` ALSO checks `IsOnTurntable`** (`:152`). A solo car on a turntable is *never* AtRest, so its set always ticks. This is intentional — turntable rotation moves the car geometrically, and the solver needs to track.
- **`ShouldSkipTick` is virtual; `RemoteIntegrationSet` overrides to `false`.** Clients never skip — they always interpolate.
- **`UpdateSets` can throw in pathological topology** (loop count exceeded `:1140` is logged as Error, not thrown — but the inner Split/Union calls swallow exceptions in `IntegrationSetManager` `:190`/`:222`). A bad topology at high speed can leave the set in an inconsistent state for one tick; `ValidateConsistency` cleans up on the next tick.
- **`SendDelta` runs inside `StateManager.TransactionScope()`**. Patches that mutate KVO state inside a `SendDelta` postfix participate in the same transaction — be aware of nesting.
- **Two `Position` semantics share the name.** `Element.position` is consist-space float. `Snapshot.CarSet.Positions` is per-pair distance. `BatchCarPositionUpdate.Positions` is also per-pair distance (between consecutive cars' WheelBoundsA). The world-space "position" is recovered via `Graph.LocationByMoving`.
- **`SteamLocomotive.ForceConnectedToAtRear` checks self-archetype** (returns true when self is `Tender`, but the override is on `SteamLocomotive` which is never `Tender`). Likely a vanilla bug; the engine→tender reconnect path may be relying on `RequiresConnectionToEnd` blocking the *uncouple* in the first place. See above.
- **`UpdateSets` runs only on host** (`if (!IsHost) return;` `:1035`). Clients receive set topology via `CarSetAdd`/`CarSetChangeCars`/`CarSetRemove` and rebuild from snapshots.
- **`_carsForUpdateSets` is filled by `CarDidPosition`** (`TrainController.cs:761`) — *every* successful position update queues that car. So `UpdateSets` only re-evaluates cars that moved this tick. A car that's `IsAtRest && !ShouldUpdatePosition` won't re-trigger reconciliation — you can't add a car next to it and expect auto-couple to detect on the rest car's side. The newly placed car will detect the rest car instead.

---

## Cross-references

- **Slack solver math, IntegrateConstraints, brake integration, sort algorithm:** [integration-set-solver.md](integration-set-solver.md)
- **Coupler state writes, slack stretch, auto-couple thresholds:** [couplers.md](couplers.md)
- **Damage from collision events fired by the solver:** [couplers.md › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline) and [wear-durability.md › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings)
- **Derailment auto-uncouple via `Car.ApplyDerailmentDelta`:** [wear-durability.md › derailment](wear-durability.md#derailment)
- **`CarAirSystem` brake force feeding `CalculateRetardingForce`:** *(future crib sheet)* — meanwhile, `physics-vanilla-survey.md § Air & brakes`
- **`TrainController.FixedUpdate` as the canonical hook:** `physics-vanilla-survey.md § The Spine`
