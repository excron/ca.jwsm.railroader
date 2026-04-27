# Passengers & Timetable — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Industries & Ops](industries-ops.md), [Cars & Cargo](cars-cargo.md), [Auto-Engineer](autoengineer.md), [UI (Vanilla)](ui-vanilla.md), [Save/Load](save-load.md), [KVO Patterns](kvo-patterns.md)

The passenger system and the timetable system share enough state that they're best understood together. **Passenger state lives in two places**: per-stop (`PassengerStop._waiting` + the `pass.<identifier>` KVO state blob, holding "passengers waiting at this stop, grouped by destination") and per-car (`PassengerMarker` in the `ops.passengerMarker` KVO key on each `Car`, holding "checked destinations + boarded groups"). **Timetable state lives in a single KVO key** (`timetable._current`) holding a text blob authored by hand or via `VisualTimetableEditor`, parsed by `TimetableReader`. The two systems weave together via `PassengerStopTimetableLogic`, which biases waiting-passenger growth toward stations that scheduled passenger trains will reach soon, and via `AutoEngineerPassengerStopper` which holds AI engineers at the platform until the timetable departure time. Vanilla has no concept of "scheduled freight train enforcement" — `TrainType.Freight` exists in the model but is purely informational. Everything is host-authoritative; clients see the timetable, see waiting counts, and trigger destination toggles via `SetPassengerDestinations` (Crew). There is exactly **one** game-message that mutates the timetable text (`SetTimetable`, Officer-only).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Model.Ops.PassengerStop` | `Model.Ops/PassengerStop.cs:28` | Per-station GameBehaviour. Owns `pass.<id>` KVO, growth loop, work loop, payment, marker scribbling |
| `Model.Ops.PassengerMarker` (struct) | `Model.Ops/PassengerMarker.cs:8` | Per-car wire format: groups (boarded) + destinations (checked) + lastStop + ttAutoDest |
| `Model.Ops.PassengerGroup` (struct) | `Model.Ops/PassengerGroup.cs:8` | A boarded group on a car: origin + destination + count + boarded |
| `Model.Ops.WaitingPassengerGroup` (struct) | `Model.Ops/WaitingPassengerGroup.cs:8` | A waiting group at a stop: origin + count + boarded (no destination — keyed by outer dict) |
| `Model.Ops.PassengerExpiration` | `Model.Ops/PassengerExpiration.cs:14` | Host-side coroutine + `TimeAdvanced` listener — expires groups older than 4 game-hours |
| `Model.Ops.PassengerStopTimetableLogic` | `Model.Ops/PassengerStopTimetableLogic.cs:12` | Pure functions: which stops should grow; what destinations a timetable-train car should auto-set |
| `CarExtensions.GetPassengerMarker` / `SetPassengerMarker` | `Model.Ops/CarExtensions.cs:230, 235` | Read/write the `ops.passengerMarker` KVO blob (Set is `AssertIsHost`) |
| `CarExtensions.IsPassengerCar(Car)` | `Model.Ops/CarExtensions.cs:242` | Loco / Coach / Baggage / Caboose **AND** has a `RequiredLoadIdentifier == "passengers"` slot |
| `Model.Ops.Timetable.Timetable` | `Model.Ops.Timetable/Timetable.cs:8` | The parsed model: `Dictionary<name, Train>` with Direction/Class/Type and entries |
| `Model.Ops.Timetable.TimetableController` | `Model.Ops.Timetable/TimetableController.cs:19` | Singleton GameBehaviour. Owns `timetable` KVO. Parses, validates, broadcasts |
| `Model.Ops.Timetable.TimetableReader` / `TimetableWriter` | `Model.Ops.Timetable/TimetableReader.cs:12` / `TimetableWriter.cs:7` | Round-trip text codec for the timetable document |
| `Model.Ops.Timetable.TimetableExtensions` | `Model.Ops.Timetable/TimetableExtensions.cs:9` | `GetGameDateTime[Departure]`, `TryGetTimetableEntry`, `GetIllogicalStations` |
| `Model.AI.AutoEngineerPassengerStopper` | `Model.AI/AutoEngineerPassengerStopper.cs:16` | The AI driver's passenger-stop arbiter. Bell, hold-at-platform, depart announcements |
| `Game.Events.TimetableDidChange` (struct, empty) | `Game.Events/TimetableDidChange.cs:6` | Sent by `TimetableController.UpdateTimetable` after every load |
| `Game.Events.PassengerStopServed` | `Game.Events/PassengerStopServed.cs:3` | Sent when a passenger boards/disembarks. Consumed by `ReputationTracker` |
| `Game.Events.PassengerStopEdgeMoved` | `Game.Events/PassengerStopEdgeMoved.cs:3` | Sent when a passenger arrives at a stop different from `LastStopIdentifier`. Consumed by `ReputationTracker` |
| `Game.Messages.SetTimetable` | `Game.Messages/SetTimetable.cs:8` | The only timetable-write message. **Officer** access |
| `Game.Messages.SetPassengerDestinations` | `Game.Messages/SetPassengerDestinations.cs:9` | Crew-driven per-car destination toggle |
| `Game.Messages.SetPassengerAutoDestinations` | `Game.Messages/SetPassengerAutoDestinations.cs:8` | Crew-driven per-car "follow timetable" toggle |
| `Game.Messages.RequestSetTrainCrewTimetableSymbol` | `Game.Messages/RequestSetTrainCrewTimetableSymbol.cs:8` | Trainmaster: assign a `TimetableSymbol` to a `TrainCrew`, which is what binds an AI/crew to a scheduled train |

---

## Spine: who writes what, when

```
Passengers — per-stop loop                              Passengers — per-car / AI driver
─────────────────────────────                          ─────────────────────────────────
PassengerStop.Loop (every 3s, host-only)               AutoEngineerPlanner.UpdateTargets
  ├─ FindCars on _spans, IsStopped(10mph)                ├─ AutoEngineerPassengerStopper.UpdateFor(...)
  ├─ ShouldWorkCar → WorkCar coroutine                   │   ├─ ShouldStopPerTimetable → SetNextStop[Timetable]
  │    ├─ CopyStopsFromTimetable(onlyIfAuto:true)        │   ├─ Sets a 0 mph velocity target at stop centroid
  │    ├─ UnloadCar (per-tick, 1-2s pacing)              │   ├─ ShouldStayStopped checks timetable departure
  │    │    ├─ marker.TryRemovePassenger(this.id)        │   ├─ Fires bell at <100m approach
  │    │    ├─ if dest == this → QueuePayment+Served     │   └─ SetTimetableTrain via TimetableDidChange
  │    │    └─ else → return passenger to waiting        │
  │    └─ LoadCar (per-tick, 1-2s pacing)
  │         ├─ AllocateWaitingPassengersForDestinations  Per-car KVO: ops.passengerMarker (HostOnly)
  │         └─ marker.AddPassengers(group)                  PassengerMarker = {groups, destinations,
  ├─ GrowWaiting (every 300 game-sec)                       lastStop, ttAutoDest}
  │    ├─ CalculateWeightedAvailableDestinations
  │    │    └─ if HasPassengerTrains+timetableCode →
  │    │         PassengerStopTimetableLogic.GetTimetableDestinations
  │    └─ OffsetWaiting per destination
  └─ PayPending (5s payment debounce, real time)

Per-stop KVO: pass.<identifier>  →  state = {waiting, lastGrow}  (HostOnly)


Timetable                                              AI driver binding
─────────                                              ─────────────────
TimetableController (singleton GameBehaviour)          Car.trainCrewId → TrainCrew
  ├─ KVO 'timetable._current' ← string (Officer)         → TrainCrew.TimetableSymbol → Timetable.Train
  ├─ Observe('current') → UpdateTimetable                AutoEngineerPlanner subscribes
  │    ├─ TimetableReader.TryRead → Timetable               TimetableDidChange and re-resolves
  │    ├─ ToAbsolute() (relative→absolute minutes)          via TryGetTrainForTrainCrewId
  │    ├─ FilterForUse() (drop unknown station codes)    AutoEngineerPassengerStopper.SetTimetableTrain(train)
  │    ├─ Current = filtered abs                            is the bind-point
  │    └─ Send TimetableDidChange (empty struct)
  └─ Observe(Storage.TimetableFeature) → re-fire UpdateTimetable
                                       (clears Current if disabled)
```

**Two consumers of `TimetableDidChange`:** `AutoEngineerPlanner` (re-binds the per-locomotive timetable train), and `TimetableEditorWindow` (prompts about unsaved changes if another player edits). `TimetableWindow` (read-only view) uses `RebuildOnEvent<TimetableDidChange>()` indirectly through the `UIPanelBuilder` reactive system.

---

## `Model.Ops.PassengerStop` (per-station)

**`GameBehaviour`** with `IIndustryTrackDisplayable` + `IProgressionDisablable`. Note: it is **not** an `IndustryComponent` — the ops tick (`OpsController.PeriodicUpdate`/`Service`) does not run it; the stop runs its own coroutine and registers its own KVO under `pass.<identifier>`. See [Industries & Ops › PassengerStop (NOT an IndustryComponent)](industries-ops.md#passengerstop-not-an-industrycomponent).

### Serialized authoring fields

```csharp
public string identifier;                                  // PassengerStop.cs:94   stable id ("alarka", "sylva")
public string timetableCode;                               // PassengerStop.cs:96   short code ("AL", "SY") used in Timetable
[Range(0, 100)] public int basePopulation = 50;            // PassengerStop.cs:100  pre-AdditionalPopulation cap
public bool flagStop;                                      // PassengerStop.cs:102  unused in growth, surfaced for authoring
public PassengerStop[] neighbors;                          // PassengerStop.cs:104  network graph (BFS in FindPath)
public Load passengerLoad;                                 // PassengerStop.cs:106  the Load SO ("passengers"); used to resolve capacity
```

Authored on the scene as a child of an `Area` GameObject with `TrackSpan` children defining the platform extent. `OpsController.RebuildPopulations` (`OpsController.cs:189`) walks `Area.GetComponentInChildren<PassengerStop>` to push `AdditionalPopulation` based on industry contracts; see [Industries & Ops › OpsController](industries-ops.md#modelopsopscontroller).

### Runtime state

```csharp
private KeyValueObject _keyValueObject;                    // 110  registered as "pass." + identifier (HostOnly)
private string _timetableName;                             // 116  cached DisplayName with " Depot"/" Station" stripped
private static PassengerStop[] _allPassengerStops;         // 118  cache populated by FindAll()
private readonly Dictionary<string, WaitingInfo> _waiting = new();   // 120  destination_id → WaitingInfo
private GameDateTime _lastGrow;                            // 122  cursor for 300-sec growth bucketing
private Coroutine _loop;                                   // 124  the 3-sec work/grow/pay tick
private readonly HashSet<string> _workingCarIds = new();   // 126  guard against double-WorkCar coroutines on same car
private HashSet<PassengerStop> _availableDestinations;     // 128  initialized OnEnable: every other stop in scene
private const int GrowInterval = 300;                      // 130  seconds (game-time) per growth bucket
private const string StateKey = "state";                   // 132
internal const double GroupWindowSeconds = 600.0;          // 134  10-minute coalesce window for boarded-time matching
private HashSet<TrackMarker> _markers;                     // 136  PassengerStop TrackMarker per span lower↔upper midpoint
private readonly PendingPayment _pendingPayment = new();   // 138  payment coalescer (real-time)
private static readonly Dictionary<string, DistanceInfo> MilesBetweenPassengerStops = new();  // 140  static cache
private readonly float[] _levelsByHour = new float[24] {…};                                    // 142  growth-by-hour-of-day
```

### `WaitingInfo` (per-stop) and the `pass.<id>.state` blob

```csharp
public readonly struct WaitingInfo {                               // PassengerStop.cs:30
    public readonly IReadOnlyList<WaitingPassengerGroup> Groups;
    public readonly int Total;            // sum of group counts
    public WaitingInfo(IReadOnlyList<WaitingPassengerGroup> groups);
    public static WaitingInfo FromPropertyValue(Value);
    public Value PropertyValue();
}
```

`WaitingPassengerGroup` (`WaitingPassengerGroup.cs:8`):

```csharp
public struct WaitingPassengerGroup(string origin, int count, GameDateTime boarded) {
    public string Origin;          // origin stop identifier
    public int Count;
    public GameDateTime Boarded;   // when this group started waiting
}
```

The on-disk shape of `pass.<identifier>` is:

```text
state: {
   waiting: { <destinationId>: { groups: [ {origin, count, boarded}, … ] }, … },
   lastGrow: <int seconds since GameDateTime epoch>
}
```

`SaveState` (`PassengerStop.cs:563`) only writes destinations whose `Total > 0`. Empty waiting state collapses to `{ waiting: {}, lastGrow: ... }`.

### The 3-second `Loop`

```csharp
private IEnumerator Loop()                                          // PassengerStop.cs:301
{
    TrainController trainController = TrainController.Shared;
    while (true) {
        yield return new WaitForSeconds(3f);                        // *real* seconds, not game seconds
        if (ProgressionDisabled) continue;
        foreach (Car car in FindCars(trainController))
            if (!_workingCarIds.Contains(car.id) && ShouldWorkCar(car))
                StartCoroutine(WorkCar(car));
        GrowWaiting();
        PayPending();
    }
}
```

`ShouldWorkCar(car) == car.IsStopped(10f)` (`car.IsStopped(velocityMphTolerance)`), per `IsStopped(Car) → car.IsStopped(10f)` at `:1126`. So a coach can be "worked" at speeds up to 10 mph; this is *not* a true zero-velocity gate.

`FindCars` (`:1095`):
1. Gathers cars on each `_spans`'s `TrackSpan` via `TrainController.CarsOnSpan`.
2. Filters by `IsStopped(10f)` AND `IsPassengerCar()`.
3. **Expands by walking coupled passenger cars in both directions.** A boxcar between two coaches will *not* be added (filtered by `IsPassengerCar`), but the second coach is reached via `EnumerateCoupled(LogicalEnd.B)`. So a boxcar between coaches breaks the `FindCars` walk — the second coach is unreachable.

### `WorkCar` coroutine

```csharp
private IEnumerator WorkCar(Car car)                                // PassengerStop.cs:607
{
    _workingCarIds.Add(car.id);
    car.CopyStopsFromTimetable(onlyIfAuto: true);                   // Auto-Dest → resolve destinations
    while (car != null && ShouldWorkCar(car)) {
        if (UnloadCar(car)) { yield return wait(1..2s); continue; } // unload first, fully, before loading
        RemoveDestinationFromMarker(car);                           // current stop is no longer a destination
        if (!LoadCar(car)) break;
        yield return wait(1..2s);
    }
    _workingCarIds.Remove(car.id);
}
```

Per-passenger pacing: each `UnloadCar` removes **one** passenger and yields 1–2 seconds. `LoadCar` boards 1–2 passengers per yield (`AllocateWaitingPassengersForDestinations` returns `min(maximum, destinationOut.Length, Random(1,3))`). Boarding rate is roughly 1 passenger per 1.5 seconds of real time — that's ~40 pax/min wall-clock per coach, which is the implicit dwell-time model.

### `UnloadCar` semantics (and the "off-route disembark" path)

```csharp
private bool UnloadCar(Car car)                                     // PassengerStop.cs:663
{
    float bonusMultiplier = CalculateBonusMultiplier(car);
    PassengerMarker value = MarkerForCar(car);
    bool flag = string.IsNullOrEmpty(value.LastStopIdentifier);
    if (flag || value.LastStopIdentifier != identifier) {
        if (!flag) FirePassengerStopEdgeMoved(value.LastStopIdentifier);
        value.LastStopIdentifier = identifier;
        car.SetPassengerMarker(value);
    }
    if (value.TryRemovePassenger(identifier, out var rDest, out var rOrig, out var rBoarded)) {
        car.SetPassengerMarker(value);
        if (rDest == identifier) {                                  // arrived at chosen destination
            QueuePayment(1, rOrig, identifier, bonusMultiplier);    //   → pay
            FirePassengerStopServed(1, car.Condition);              //   → reputation +1
        } else {                                                    // got off at a NON-destination station
            OffsetWaiting(rDest, rOrig, rBoarded, 1);               //   → re-add to local waiting pool!
            SaveState();
            FirePassengerStopServed(-1, car.Condition);             //   → reputation −1
        }
        return true;
    }
    return false;
}
```

**Surprises:**
- `TryRemovePassenger` looks at `Destinations.Contains(group.Destination)` (`PassengerMarker.cs:108`). A passenger whose destination is *no longer in the car's checked Destinations* will be ejected at *any* stop where the car works. Removing a destination from a coach mid-trip means those passengers will disembark at the next station regardless.
- Off-route disembarking **adds the passenger back to the waiting pool at the wrong station**. They keep their original boarded timestamp, so they may immediately expire.
- Off-route disembarks fire `PassengerStopServed(offset = -1)` — that's the reputation hit.
- "Observation trailing" detection (`CalculateBonusMultiplier`, `:629`): if `EnumerateCoupled().First()` or `Last()` is car-type `"PBO"` (the observation car) facing trailing-end, fares pay 1.2× — the only car-type-driven payment bonus in vanilla.

### `LoadCar` and `AllocateWaitingPassengersForDestinations`

```csharp
private bool LoadCar(Car car)                                       // PassengerStop.cs:823
{
    PassengerMarker value = MarkerForCar(car);
    int num = PassengerCapacity(car) - value.TotalPassengers;
    if (num <= 0) return false;                                     // car full
    int loaded = AllocateWaitingPassengersForDestinations(num, value.Destinations,
                                  out var dest, out var orig, out var boardedFrom);
    if (loaded <= 0) return false;
    value.AddPassengers(orig, dest, loaded, boardedFrom);
    car.SetPassengerMarker(value);
    SaveState();
    FirePassengerStopServed(loaded, car.Condition);                 // reputation += loaded
    return true;
}
```

`AllocateWaitingPassengersForDestinations` (`:847`) picks the **single oldest** waiting group whose destination is in `value.Destinations`, and removes `min(maximum, destinationLength, Random(1,3))` from it. So one `LoadCar` call serves at most one destination. The `destinationLength` clamp is `destinationOut.Length` — *the string length of the destination identifier* (probably a bug; a 3-char id like "AND" caps loaded passengers at 3 per call). Worth noting if you patch.

`PassengerCapacity(car)` reads `Definition.LoadSlots.First(slot => slot.LoadRequirementsMatch(passengerLoad)).MaximumCapacity`. So capacity is per-Definition + per-stop's `passengerLoad` reference. If a coach has multiple passenger-class slots, only the first matching one is consulted.

### `GrowWaiting` (every 300 game-sec)

```csharp
private void GrowWaiting()                                          // PassengerStop.cs:365
```

Step-by-step:
1. `cycles = (now - _lastGrow) / 300`. Skip if `cycles == 0`. If negative (game time rewound), reset `_lastGrow` and force `cycles = 1`.
2. `levelsByHour[now.Hours]` — daily curve hard-coded to `0.2..1.0` over 24 hours.
3. `CalculateMaxWaiting()` = `MaxWaiting + scaled-up neighbors' AdditionalPopulation` — the local cap is rebalanced against the rest of the network's contract activity.
4. `CalculateWeightedAvailableDestinations` — **the integration point with the timetable**. If `TimetableController.Shared.HasPassengerTrains == true && string.IsNullOrEmpty(timetableCode) == false`, calls `PassengerStopTimetableLogic.GetTimetableDestinations(...)`. Otherwise `ActiveAvailableDestinations.ToList()` (every non-disabled stop except self).
5. Per cycle: `target = MaxWaiting * maxWaitingCoefficient * hourLevel`. Compare against `currentWaitingFromHere`. Add or remove proportional to the gap.
6. `ThinWaitingNotFoundInWeighted` — when timetable shrinks the available destination set, removes 10% of waiting passengers headed to *no-longer-reachable* destinations.

`OffsetWaiting(dest, origin, boarded, delta)` (`:696`) is the single mutator. Its `Matches` predicate considers a group a match if origin matches *and* (this is the origin stop OR `|group.Boarded - sourceGroupBoarded| < 600s`). So `GroupWindowSeconds = 600` (10 game-minutes) coalesces growth into bucketed groups for compactness.

### `PayPending` and the payment formula

```csharp
private void QueuePayment(int count, string originId, string destinationId, float bonusMultiplier)  // :992
{
    if (TryCalculateMilesBetweenPassengerStops(originId, destinationId, out var distInfo)) {
        float miles = distInfo.DistanceInMiles;
        int fareDollars = Mathf.RoundToInt(Mathf.Lerp(1f, 8f, Mathf.InverseLerp(2f, 50f, miles)));
        float bonused  = fareDollars * bonusMultiplier;
        _pendingPayment.Count  += count;
        _pendingPayment.Amount += bonused;
        _pendingPayment.LastPaymentTime = Time.unscaledTime;
    }
}

private void PayPending()                                           // :1006
{
    if (Time.unscaledTime - _pendingPayment.LastPaymentTime >= 5f && _pendingPayment.Count != 0)
        PayPassengerFare(_pendingPayment.Count, Mathf.CeilToInt(_pendingPayment.Amount));
}
```

**Fare formula:** `Lerp(1, 8, InverseLerp(2, 50, miles))`. Below 2 mi → flat $1. 2–50 mi → linear ramp 1→8. Above 50 → flat $8. Per passenger. PBO observation-car bonus multiplies *the dollar value* before flooring.

`MilesBetweenPassengerStops` is a static cache keyed by alphabetically-ordered `idA--idB`. First lookup walks `TrainController.Shared.graph.TryFindDistance` from each stop's first-span `lower` location. Cache is **never invalidated** — graph changes (e.g., scripted track edits) won't propagate.

`PayPassengerFare` (`:1017`) calls `StateManager.Shared.ApplyToBalance(amount, Ledger.Category.Passenger, EntityReference(EntityType.PassengerStop, identifier), null, numberOfPassengers, quiet: true)`, broadcasts a chat message, and emits an `Analytics` event. See [Industries & Ops › Ledger](industries-ops.md#ledger) for the ledger pipeline.

### `ExpirePassengers(GameDateTime expiration)`

Driven by `PassengerExpiration.Tick` every 60 real seconds + on every `TimeAdvanced` Messenger event. Removes any waiting group `Boarded < now - 4 hours` *unless* `group.Origin == identifier` (passengers waiting at their own origin stop never expire). See `PassengerExpiration.cs:14`.

### Patch candidates (PassengerStop)

| Method | Why patch |
|---|---|
| `PassengerStop.GrowWaiting` | Inject custom growth signals (e.g., scenario-driven surge). Patch postfix to log/bias generated groups |
| `PassengerStop.CalculateWeightedAvailableDestinations` | Override the timetable-vs-default destination-selection split |
| `PassengerStop.UnloadCar` | The single chokepoint for passenger boarding off the car. Postfix to emit custom telemetry, prefix to gate disembark |
| `PassengerStop.LoadCar` | Per-tick boarding. Patching here lets you tweak per-board count, capacity-vs-waiting selection |
| `PassengerStop.AllocateWaitingPassengersForDestinations` | Replace the "oldest group first" priority. Note: the `destinationOut.Length` clamp on per-call count is likely a bug — you may want to remove it |
| `PassengerStop.QueuePayment` / `PayPassengerFare` | Replace fare formula or split between accounts |
| `PassengerStop.TryCalculateMilesBetweenPassengerStops` (static) | The cache never invalidates; patch to reset on `WorldDidLoad` |
| `PassengerStop.ExpirePassengers` | Tweak the 4-hour expiration window or the "self-origin" exemption |
| `PassengerStop.FindCars` | Expand or restrict the worked-cars set (e.g., add custom non-`IsPassengerCar` archetypes that should still work) |
| `PassengerStop.OffsetWaitingOpsCommand` | Public for `OpsCommand`/`ScriptPassengerStop`; safe to call from mods if you have host auth |
| `PassengerStop.ClearAllWaiting` | Public; called by `ScriptWorld` |

### MP authority (PassengerStop)

- `pass.<identifier>` KVO is registered with `AuthorizationRequirement.HostOnly` (`PassengerStop.cs:225`). Only the host writes; clients observe.
- `Loop` only starts on `StateManager.IsHost` (`:275`). Clients use the `state` observer (`:270`) to mirror waiting counts.
- Payment goes through `StateManager.Shared.ApplyToBalance(...)` — host-only side effect.
- `OpsCommand.PassOffset` calls `AssertIsHost()`. Console use is host-only.

### Gotchas (PassengerStop)

- **`Loop` waits 3 *real* seconds, not game seconds.** Time-acceleration affects passenger boarding *speed* indirectly only via `levelsByHour` and the growth interval — actual board/unload tick is wall-clock paced.
- **`CalculateBonusMultiplier` reads `EnumerateCoupled().First()` and `.Last()`.** If a `PBO` car is in the *middle* of the consist, no bonus. Train re-orientation can flip eligibility silently.
- **`destinationOut.Length` clamp** in `AllocateWaitingPassengersForDestinations` (`:882`) is almost certainly a bug — limits boarding to length-of-station-id passengers per call. With a 2-letter `timetableCode`, that's 2 per call; passenger throughput effectively pinned to ~1.3 pax/sec of real-time per coach. Fixing this would be a balance change. ([Dispatch mod rewrite](../project_dispatch_rewrite.md) is unrelated.)
- **`MilesBetweenPassengerStops` static cache never resets.** First failure for a route (e.g., switch lined wrong at first attempt) is cached forever — `distanceInfo.Success = false` short-circuits future fare attempts via `TryCalculateMilesBetweenPassengerStops` returning false. No fare paid until restart.
- **Off-route disembarking returns passengers to waiting** with original `Boarded`, so they may instantly expire (`PassengerExpiration` runs on a 60-sec real-time loop). Net effect: passengers can vanish entirely.
- **`_availableDestinations` is set in `OnEnable` from the scene state once.** Scripted spawning of new `PassengerStop` GameObjects after enable will not be picked up. Re-enable the component to refresh.
- **`flagStop` is a serialized boolean that nothing reads** (vanilla). Ready-made hook for mods.
- **`neighbors` is required for `FirePassengerStopEdgeMoved`** to find a path between adjacent stops. Stops without `neighbors` populated cannot fire edge events; `ReputationTracker.PassengerStopEdgeMoved` won't see them. Authoring stops without proper `neighbors[]` silently breaks reputation propagation.
- **`PassengerStop` is *not* in `OpsController.AllIndustries`/`AllIndustryComponents`.** Don't search there for stops; use `PassengerStop.FindAll()`.
- **`FindAll()` caches `_allPassengerStops` statically** and only refreshes when `FirstOrDefault() == null` (a destroyed Unity object). Hot-reloading scenes can leave stale entries.
- **`passengerLoad` SO must be the same instance** referenced by every `PassengerStop` *and* every coach's `LoadSlot.RequiredLoadIdentifier` (the string "passengers"). The string match is in `LoadSlot.LoadRequirementsMatch(passengerLoad)`; mismatching `Load.id` strings break capacity detection.

---

## `Model.Ops.PassengerMarker` (per-car)

Per-car wire format. Lives at KVO key `Car.KeyOpsPassengerMarker = "ops.passengerMarker"` (`Car.cs:457`). HostOnly write (`HostPrefixes` includes `"ops.passengerMarker"` at `Car.cs:467`).

```csharp
public struct PassengerMarker(List<PassengerGroup> groups,                  // PassengerMarker.cs:8
                              HashSet<string> destinations,
                              string lastStopIdentifier,
                              bool autoDestinationsFromTimetable)
{
    public readonly List<PassengerGroup> Groups;             // boarded passengers, by group
    public HashSet<string> Destinations;                     // checked destination ids (the UI checkboxes)
    public string LastStopIdentifier;                        // last station this car worked at
    public bool AutoDestinationsFromTimetable;               // if true, PassengerStop.WorkCar repopulates Destinations
                                                             // from the train's timetable each tick
    public int TotalPassengers => Groups.Sum(g => g.Count);
    public static PassengerMarker Empty();
    public static PassengerMarker? FromPropertyValue(Value);
    public Value PropertyValue();
    public int CountPassengersForStop(string stopIdentifier);
    public void AddPassengers(string origin, string destination, int num, GameDateTime boarded);
    public bool TryRemovePassenger(string destination, out string rDest, out string rOrig, out GameDateTime rBoarded);
}
```

`PassengerGroup` (`PassengerGroup.cs:8`) is the per-car-side group: `(Origin, Destination, Count, Boarded)`. Note the asymmetry — the per-stop side (`WaitingPassengerGroup`) does **not** carry destination because destination is the outer dict key in `WaitingInfo`.

### `AddPassengers` coalescing

```csharp
public void AddPassengers(string origin, string destination, int num, GameDateTime boarded)  // :84
{
    for (int i = 0; i < Groups.Count; i++) {
        PassengerGroup value = Groups[i];
        if (value.Destination == destination
            && value.Origin == origin
            && boarded.TotalSeconds - value.Boarded.TotalSeconds <= 600.0) {  // 10-minute window
            value.Count += num;
            Groups[i] = value;
            return;
        }
    }
    Groups.Add(new PassengerGroup(origin, destination, num, boarded));
}
```

Same 10-minute group-coalesce window as the stop side.

### `TryRemovePassenger` semantics

```csharp
public bool TryRemovePassenger(string destination, out string rDest, out string rOrig, out GameDateTime rBoarded)
{
    for (int i = 0; i < Groups.Count; i++) {
        PassengerGroup value = Groups[i];
        if (value.Count <= 0) continue;
        bool destinationStillChecked = Destinations.Contains(value.Destination);
        if (value.Destination == destination || !destinationStillChecked) {   // remove ANY group whose dest is unchecked
            value.Count--;
            // ...write back, set out vars, return true
        }
    }
    return false;
}
```

The first-match wins removes either:
1. a passenger whose destination is *this stop*, OR
2. a passenger whose destination is **no longer in `Destinations`** (kicked off because we abandoned that route).

### Wire format (`pass.<id>.ops.passengerMarker` Dictionary)

```text
{
  groups: [ { origin, dest, count, boarded } ],
  destinations: [ stopId, ... ],
  lastStop: <stopId or null>,
  ttAutoDest: true | null      // null when false (compaction)
}
```

`FromPropertyValue` returns `null` (caller treats as `Empty()`) if the value isn't a Dictionary or is missing `groups`/`destinations`.

### Patch candidates (PassengerMarker)

| Method | Why patch |
|---|---|
| `PassengerMarker.AddPassengers` / `TryRemovePassenger` | Per-board / per-disembark hook on the car-side struct. Note these mutate the struct *value* — caller must SetPassengerMarker afterward |
| `CarExtensions.GetPassengerMarker` / `SetPassengerMarker` | Single chokepoint to read/write the KVO blob. `Set` calls `AssertIsHost`. Patch `Get` to inject synthetic state if needed |
| `PassengerExtensions.SetPassengerDestinations(carId, dest)` (static) | Server-side handler for `SetPassengerDestinations` message. Patch to validate/log destination changes |
| `PassengerExtensions.SetPassengerTimetableAutoDestinations(carId, en)` (static) | Server-side handler for `SetPassengerAutoDestinations` |

### MP authority (PassengerMarker)

- KVO key `ops.passengerMarker` is **HostOnly** (matches `HostPrefixes` `"ops.passengerMarker"`). Clients read.
- `CarExtensions.SetPassengerMarker` calls `StateManager.AssertIsHost()` (`CarExtensions.cs:237`).
- `SetPassengerDestinations` (Crew, `MinimumAccessLevel(AccessLevel.Crew)`) is the client-side request channel. The host's dispatcher calls `PassengerExtensions.SetPassengerDestinations(carId, destinations)` (`StateManager.cs:680`), which calls `AssertIsHost`, fetches the car, and writes the marker. The flow is: client→message→host→`SetPassengerMarker`→KVO write→remote observers update.
- `SetPassengerAutoDestinations` (Crew) similarly. Toggling auto on the host side **does not** automatically run `CopyStopsFromTimetable` — that happens during `WorkCar`.

---

## `CarExtensions.IsPassengerCar` and Coach/Baggage handling

```csharp
public static bool IsPassengerCar(this Car car) => car.Definition.IsPassengerCar();      // CarExtensions.cs:242

public static bool IsPassengerCar(this CarDefinition carDefinition)                       // CarExtensions.cs:247
{
    if (carDefinition.Archetype switch {
        CarArchetype.LocomotiveDiesel => 1,
        CarArchetype.LocomotiveSteam  => 1,
        CarArchetype.Caboose          => 1,
        CarArchetype.Coach            => 1,
        CarArchetype.Baggage          => 1,
        _ => 0,
    } == 0) return false;
    foreach (LoadSlot loadSlot in carDefinition.LoadSlots)
        if (loadSlot.RequiredLoadIdentifier == "passengers") return true;
    return false;
}
```

**Two gates, both required**: archetype must be Loco/Caboose/Coach/Baggage, *and* the definition must have at least one `LoadSlot` with `RequiredLoadIdentifier == "passengers"`. A Coach without a passenger slot is *not* an `IsPassengerCar`. A Boxcar with a passenger slot is *not* either.

This is asymmetric with `IsFreight()` — see [Cars & Cargo › Archetype](cars-cargo.md#carextensionsisfreight-and-the-archetype-helpers): `IsFreight()` includes `Boxcar/Flat/Tank/HopperOpen/Gondola` — Coach/Baggage/Caboose/Tender/Loco are **not freight**. So a "passenger boxcar" mod that wants both passenger and freight semantics would hit gaps in both helpers.

`Coach`/`Baggage` archetype-specific behavior is otherwise sparse:
- `CarArchetypeExtensions.PlacerOrder()` puts Coach=30, Baggage=40 (`car-definitions.md`).
- `Car.SetNominalBrakingRatio` (`Car.cs:1058`) gives Coach/Baggage 0.9× brake ratio (vs freight 0.7×). See [Cars & Cargo › archetype implications](cars-cargo.md#behavioral-implications-of-archetype).
- Otherwise treated as the base `Car` class — no `Coach` subclass exists.

For passenger cars, the per-car `LoadSlot` with `RequiredLoadIdentifier == "passengers"`:
- `MaximumCapacity` is the seat count (used by `PassengerStop.PassengerCapacity`).
- `LoadUnits` should be `LoadUnits.Quantity`.
- `Load` (the SO) should be the global "passengers" Load asset.
- `unitWeightInPounds` (on the Load SO) is per-passenger weight contribution to `Car.Weight` via `Load.Pounds(quantity)`. Crucially, **boarded passengers do affect car weight** — `UpdateLoadWeight` runs on every `load.{slot}` write, but `ops.passengerMarker` writes do NOT trigger `UpdateLoadWeight`. The passenger *count* in the marker doesn't propagate to the load slot. **Loaded weight is whatever `load.{n}` for the passenger slot says, not what the marker says.**
- See [Cars & Cargo › `UpdateLoadWeight`](cars-cargo.md#weight-model) — passenger-specific note: `IndustryLoader`/`OpsCarAdapter.Load` is *not* called for passenger boarding (passenger flow bypasses the freight loader path entirely). The `load.{slot}` for the passenger slot is therefore effectively ignored at runtime; the visible passenger count comes from `PassengerMarker.TotalPassengers`, not `_loadWeight`.

---

## Boarding / disembarking events

| Event | Type | Fired from | Consumers |
|---|---|---|---|
| `Game.Events.PassengerStopServed(identifier, offset, carCondition)` | Messenger struct | `PassengerStop.FirePassengerStopServed` (`:899`) — `+N` on board, `+1`/`-1` on disembark | `Game.Reputation.ReputationTracker.PassengerStopServed` (`:347`) — adds to `PassengerTotal`, `PassengerCarConditionSum`, writes `_keyValueObject["sh-<id>"]` (stop history per 4-hour bucket) and `_keyValueObject["ls-<id>"]` (last-served timestamp) |
| `Game.Events.PassengerStopEdgeMoved(from, to)` | Messenger struct | `PassengerStop.FirePassengerStopEdgeMoved` (`:904`) — fires when arriving at a stop different from `LastStopIdentifier`; if not direct neighbors, walks `FindPath` and fires per intermediate edge | `ReputationTracker.PassengerStopEdgeMoved` (`:395`) writes `_keyValueObject[KeyForPassengerStopEdge(from,to)] = true` (the visited-edges set used by `PassengerReputationCalculator`) |
| `Game.Events.TimetableDidChange` (empty struct) | Messenger struct | `TimetableController.UpdateTimetable` (`:236`) | `AutoEngineerPlanner.OnEnable` (`:279`) → `UpdateTimetableTrain`; `TimetableEditorWindow.OnTimetableDidChange` (`:96`); `TimetableWindow` indirectly via `RebuildOnEvent<TimetableDidChange>()` (`:128`) |

There is **no Messenger event for "passenger boarded"** at the granularity of "this passenger group entered this car." `PassengerStopServed` carries an aggregate `offset`. If you need finer-grained instrumentation, hook `PassengerMarker.AddPassengers` / `TryRemovePassenger` directly.

There is **no event for "timetable feature toggled"** — observe `GameStorage.ObserveTimetableFeature` (`GameStorage.cs:558`) directly. Note the `TimetableController` already does this (`:120`) and re-emits `TimetableDidChange`.

---

## `Model.Ops.PassengerExpiration`

Sibling component on the OpsController GameObject (`OpsController.cs:89`: `base.gameObject.AddComponent<PassengerExpiration>();`).

```csharp
public class PassengerExpiration : GameBehaviour                    // PassengerExpiration.cs:14
{
    public const int ExpirationTimeInGameHours = 4;

    private IEnumerator Loop() {
        WaitForSeconds wait = new WaitForSeconds(60f);              // 60 *real* seconds
        while (true) { TickAndLogException(); yield return wait; }
    }

    private void Tick() {
        GameDateTime cutoff = TimeWeather.Now.AddingHours(-4f);
        using (StateManager.TransactionScope()) {
            foreach (PassengerStop ps in PassengerStop.FindAll())
                num += ps.ExpirePassengers(cutoff);                  // self-origin groups exempt
            foreach (Car car in AllPassengerCars())
                // for each PassengerGroup with Boarded < cutoff: remove + count
        }
    }
}
```

Triggered both by:
1. The 60-second real-time `Loop`.
2. Every `Messenger<TimeAdvanced>` event (`:25`) — fires on every `TimeWeather.Now` advance step.

**Both trigger paths run on the host only** (registered behind `StateManager.IsHost` at `:22`). Clients see the side effects via KVO writes.

`StateManager.TransactionScope()` batches all KVO writes during the tick into a single network broadcast — useful for when the cutoff sweeps many stops/cars at once.

### Patch candidates (PassengerExpiration)

| Method | Why patch |
|---|---|
| `PassengerExpiration.Tick` | Adjust expiration cadence or per-pass logic |
| `PassengerExpiration.ExpirationTimeInGameHours` (const) | Patch `PassengerStop.ExpirePassengers` or override the cutoff arg in `Tick` to change the 4-hour window |

---

## `aiPassStopMinStopDur` and the AI driver toggles

`GameStorage` (`Game.State/GameStorage.cs:443-465`) exposes two settings on `_game` KVO:

```csharp
public bool AIPassengerStopEnable        { get; set; }   // key "aiPassStopEnable", default true
public int  AIPassengerStopMinimumStopDuration { get; set; }   // key "aiPassStopMinStopDur", default 60
```

Authorization: both keys map to `MinimumLevelTrainmaster` (`GameStorage.cs:605-606`). Trainmaster client can change these.

UI: `UI.CompanyWindow.SettingsPanelBuilder.BuildFeatureAIPassengerStop` (`SettingsPanelBuilder.cs:276-291`):
- Toggle for `AIPassengerStopEnable`.
- Slider for `AIPassengerStopMinimumStopDuration`, value × 30 → seconds (so slider is `seconds / 30`, range produces 0..30 minutes-ish).

Read by:
- `AutoEngineerPassengerStopper.Enable` getter (`AutoEngineerPassengerStopper.cs:89`) — early-return guard.
- `AutoEngineerPassengerStopper.MinimumStopDuration` getter (`:91`) — used in `ShouldStayStopped` (`:326`).
- `PassengerStopTimetableLogic.GetTimetableDestinations` config (`PassengerStop.cs:545`) — biases `MinimumStopDuration` into the "no-arrival" departure-window calc (`PassengerStopTimetableLogic.cs:63`).

Save format: this is the on-disk key. See [Save/Load › `_game` keys](save-load.md#game-storage-keys) for the wire-format note ("Truncated `AIPassengerStopMinimumStopDuration`").

---

## `Model.Ops.Timetable.Timetable` (the data model)

Pure-data class. Two enums (`TrainClass` First/Second/Third, `TrainType` Freight/Passenger), one struct (`Entry`), one nested class (`Train`), one nested struct (`Direction` East/West).

```csharp
public class Timetable                                              // Timetable.cs:8
{
    public readonly Dictionary<string, Train> Trains;               // by symbol (e.g., "17", "18")
    public Timetable ToAbsolute();                                  // resolves all relative times to absolute minutes-of-day
    public Timetable Clone();                                       // deep copy for editor scratch
    // value-equality via Trains.DictionaryEqual
}

public class Train
{
    public string Name;                                             // train symbol; UI sorts by int-parse-or-string
    public Direction Direction;                                     // East | West
    public TrainClass TrainClass;                                   // First/Second/Third
    public TrainType TrainType;                                     // Freight | Passenger
    public List<Entry> Entries;                                     // ordered station stops
    public string SortName, DisplayStringShort, DisplayStringLong;
    public int SortOrderWithinClass;                                // used by TimetableWindow header sort

    public bool TryGetAbsoluteTimeForEntry(int index, TimetableTimeType type, out int minutes);
    public Train Clone();
    public void AddEntry(Entry entry, IReadOnlyList<string> stationsEastToWest);  // inserts at the right slot
    public void SortEntries(IReadOnlyList<string> stationsEastToWest);
    public bool StationsIntersectWithStationCodes(HashSet<string> stationCodes);
}

public struct Entry : IEquatable<Entry>
{
    public readonly string Station;                                 // station code (timetableCode), e.g., "BR"
    public TimetableTime? ArrivalTime;                              // null = no arrival specified (= same as departure)
    public TimetableTime DepartureTime;
    public IReadOnlyList<string> Meets;                             // list of train-symbols this train meets here
    public bool HasSingleArrivalAndDeparture { get; }
}
```

`TimetableTime` (`TimetableTime.cs:5`):

```csharp
public struct TimetableTime(int minutes, bool isAbsolute)
{
    public int Minutes;            // either absolute minutes-of-day (mod 1440) or relative minutes-from-prev
    public readonly bool IsAbsolute;
    public static TimetableTime Relative(int minutes);
    public static TimetableTime Absolute(int minutes);              // % 1440
    public string TimeString();                                     // "HH:MM" or "+M"
}
```

### `ToAbsolute()` semantics

Walks each train's entries in order, accumulating minutes. A relative time adds to the prior absolute (the "anchor" set by the previous absolute time). Each entry's resulting `ArrivalTime`/`DepartureTime` are absolute. `% 1440` wraps over midnight.

### `GetGameDateTime` (the time→GameDateTime lookup)

`TimetableExtensions.GetGameDateTime(this Train, TimetableTimeType, int index, GameDateTime now)` (`TimetableExtensions.cs:38`) is the runtime entry point used by the AI:
1. `TryGetAbsoluteTimeForEntry(index, type, out int minutes)` (must be absolute or anchored to an absolute earlier in the train).
2. `gameDateTime = now.StartOfDay.AddingMinutes(minutes)`.
3. **Special-case** for Departure when arrival > departure (i.e., overnight stop): rolls to the next day if appropriate.
4. `RollTimeToTomorrowIfTooOld` — if the resulting time is more than 12 hours in the past, advances by 1 day.

So the timetable is implicitly a daily schedule that auto-rolls. Two consecutive runs of train "17" happen one game-day apart with the same in-day times.

### `GetIllogicalStations(branches)`

`TimetableExtensions.cs:78`. Returns the set of stations that *cannot* logically be on this train's route given branch geometry. Used by `VisualTimetableEditor.AddCellForStation` (`:179`) to grey-out and gate-uncheck illogical entries. Branch-junction-duplicate stations are the geometric pivot.

---

## `TimetableController` (the runtime)

```csharp
public class TimetableController : GameBehaviour                    // TimetableController.cs:19
{
    public List<TimetableBranch> branches;                          // serialized; the topology + stations
    public static TimetableController Shared;                       // FindObjectOfType singleton

    public Timetable Current { get; private set; }                  // ToAbsolute + FilterForUse'd
    public Timetable CurrentRaw { get; private set; }               // pre-FilterForUse, what TimetableEditor edits
    public TimetableDocument CurrentDocument { get; private set; }  // {Source, Modified, Author}
    public bool HasPassengerTrains { get; private set; }
    public bool HasError { get; private set; }
    public static bool CanEdit => StateManager.CheckAuthorizedToSendMessage(new SetTimetable(""));
}

public struct TimetableDocument
{
    public string Source;                                           // the raw text
    public GameDateTime Modified;                                   // when last edited
    public string Author;                                           // player name
}
```

### KVO surface

- Object id: `"timetable"`
- Key: `"current"` — Dictionary value `{ timetable: <text>, modified: <int seconds>, author: <name> }`
- Auth: `AuthorizationRequirement.HostOnly` (`TimetableController.cs:102`). Only the message handler writes; clients observe.

### Migration: legacy string value

```csharp
private void UpdateTimetable(Value documentValue)                   // :194
{
    if (StateManager.IsHost && documentValue.Type == ValueType.String) {  // legacy save
        var migrated = new TimetableDocument { Source = documentValue, Author = "(Migrated)", Modified = TimeWeather.Now };
        documentValue = migrated.ToValue();
    }
    if (!StateManager.Shared.Storage.TimetableFeature) documentValue = null;
    CurrentDocument = new TimetableDocument(documentValue);
    if (string.IsNullOrEmpty(CurrentDocument.Source)) {
        Current = null; HasError = false;
    } else if (TryRead(CurrentDocument.Source, out output, diagnostics)) {
        CurrentRaw = output;
        output = output.ToAbsolute();
        FilterForUse(output);                                       // drops trains with <2 valid stations
        Current = output;
    } else {
        Current = null; HasError = true;                            // parse error → no schedule
    }
    HasPassengerTrains = Current?.Trains.Any(t => t.Value.TrainType == TrainType.Passenger) ?? false;
    Messenger.Default.Send(default(TimetableDidChange));
}
```

`FilterForUse` (`:142`) walks every train and:
1. Removes entries whose `Station` code isn't in `GetAllStations()` (i.e., the live branches).
2. Drops the entire train if it ends up with ≤1 entries.

So `Current` differs from `CurrentRaw` after stations are disabled (e.g., progression-locked). The editor edits `CurrentRaw` (preserving the user's intent) but the AI uses `Current`.

### `TimetableFeature` toggle bypass

If `Storage.TimetableFeature == false`, `UpdateTimetable` *throws away* the document value and Current becomes null (HasError false). The text is **still preserved on disk** in the KVO key — toggling the feature back on resurrects the schedule. This is symmetrical with how `WearFeature` is gated (see [Wear › toggle spine](wear-durability.md#toggle-spine-how-wearfeature-propagates)).

### Lookup APIs

```csharp
public bool TryGetTrainForTrainCrew(TrainCrew, out Train);                  // :244
public bool TryGetTrainForSymbol(string symbol, out Train);                 // :254
public bool TryGetTrainForTrainCrewId(string trainCrewId, out Train);       // :265
public bool TryGetPassengerStop(string stationCode, out PassengerStop);     // :322
public bool TryGetStation(string stationCode, out TimetableStation);        // :333  (lazy-builds _timetableCodeToStation)
public bool TryGetTimingForStations(string fromCode, string toCode, out int fastMin, out int slowMin);
public IReadOnlyList<TimetableStation> GetAllStations(TimetableBranch=null, includeDisabled=false, includeDuplicates=true);
```

`TryGetTimingForStations` (`:349`) sums `traverseTimeToNext` over the common branch between two stations. `slow = ceil(1.2 * fast)`. Used by the editor to display estimated transit times (e.g., "BR to AJ est. 15-18 min (+0)").

### `SetCurrent` / `HostSetCurrent`

```csharp
public void SetCurrent(string content) =>                                   // :300
    StateManager.ApplyLocal(new SetTimetable(content));

public void HandleSetTimetable(string source, IPlayer sender) =>            // :397 (called by StateManager dispatcher)
    HostSetCurrent(source, sender);

private void HostSetCurrent(string content, IPlayer sender)                 // :305
{
    StateManager.AssertIsHost();
    var doc = new TimetableDocument { Source = content, Author = sender.Name, Modified = TimeWeather.Now };
    _keyValueObject["current"] = doc.ToValue();
    Multiplayer.Broadcast($"{Hyperlink.To(sender.PlayerId)} has updated the {Hyperlink.To(new EntityReference(EntityType.Timetable, null))}.");
}
```

### Patch candidates (TimetableController)

| Method | Why patch |
|---|---|
| `TimetableController.UpdateTimetable` | Inject mod-side parsing or veto loads. Postfix to add custom validation/logging |
| `TimetableController.FilterForUse` | Replace the "trains with ≥2 valid entries" rule. Patch to keep illogical trains for editor display |
| `TimetableController.HandleSetTimetable` (host-side) | Veto/transform incoming timetables (sanitize meets, add author auditing) |
| `TimetableController.HostSetCurrent` | Direct write to KVO. Patch postfix to broadcast custom messages |
| `TimetableController.TryRead` | Wrap `TimetableReader.TryRead` with mod-side post-parse hooks |

### MP authority (TimetableController)

| Operation | Who | Wire |
|---|---|---|
| Read `Current` | Anyone (KVO observed) | `timetable._current` HostOnly write |
| Write timetable | **Officer** only | `SetTimetable` message; routed through `StateManager.ApplyLocal` → host's `StateManager.cs:619` → `TimetableController.HandleSetTimetable` |
| Toggle feature | Host (writes `_game._timetableFeature` HostOnly) | `Storage.TimetableFeature` setter |
| Check edit auth | `TimetableController.CanEdit => CheckAuthorizedToSendMessage(new SetTimetable(""))` (`:97`) | UI gates on this |

---

## `TimetableReader` / `TimetableWriter` (text codec)

The timetable is a single string consisting of train lines plus comments. Format:

```
17 W 1P: SY 7:30, DB +5, WM +11, WH +10 (15), EL +6, BR 8:30-8:45 (18,20), HW +9, AJ +4
^ name ^direction (E/W) class (1/2/3) type (P/F)
                              ^ station code, time(s), optional (meets,...)

// comments start with double-slash
```

Time forms:
- Absolute: `7:30` → `TimetableTime.Absolute(450)`
- Relative: `+5` (minutes) or `+1:05` (hours:minutes) → `TimetableTime.Relative(...)`
- Arrival/Departure pair: `8:30-8:45` (arrival-departure)

Regex spine (in `TimetableReader`):

```csharp
TrainSymbolRegex      = "^[A-Z0-9\\-]+$";
TrainLinePrefixRegex  = "^([A-Z0-9\\-]+) (E|W) (\\w)(\\w):\\s+";
SeparatorOrEndRegex   = "\\s*(?:,|(?:// .*)?$)";
StationCodeRegex      = "^([A-Za-z]+)";
RelativeMinutesRegex  = "^\\+(\\d+)*";
RelativeTimeRegex     = "^\\+(\\d+:\\d{2})";
DepartTimeSeparatorRegex = "^\\-";
AbsoluteTimeRegex     = "^(\\d+:\\d{2})";
MeetRegex             = "^\\(([A-Z0-9\\-\\s,]*?)\\)";
CommentRegex          = "^// (.*)$";
```

`TryRead(document, validStationCodes, out timetable, diagnostics)`:
- Returns `false` on any per-line parse error, but **continues parsing remaining lines**. Diagnostics collector accumulates messages.
- Validates each station code against `validStationCodes` (passed from `TimetableController.TryRead` as `GetAllStations(includeDisabled=true)`). Unknown stations → exception → diagnostic.
- Throws on duplicate train names within the document.
- Trains are stored in source order via `Dictionary<string, Train>` insertion order.

`Write` round-trips. The output starts with `// Railroader Timetable v{Application.version} - {DateTime.Now}` and writes one train per line. Note: writes use `+M` (relative minutes) format for relative times, *not* `+H:MM` — round-trip from a `+H:MM` source is lossy in the time form (semantically equivalent, but textual diff differs).

### Patch candidates (Reader/Writer)

| Method | Why patch |
|---|---|
| `TimetableReader.TryRead` | Wrap to add custom syntax (e.g., metadata comments). Be careful — vanilla `TryRead` is reused by editor and controller; both must handle your extension |
| `TimetableReader.ReadTrainLine` (private) | Replace per-line parse to support a new train-type letter or class number |
| `TimetableWriter.Write` / `WriteTrain` | Change output formatting. Note: not symmetric — Write always emits `+M` for relative |
| `TimetableReader.TryParseTime` | Static; replace HH:MM parse for 12-hour format etc |

### Gotchas (Reader/Writer)

- **`TryRead` continues on errors** — a malformed line doesn't stop the whole document. Half-loaded timetables are common; check `HasError` and `diagnostics`.
- **`ToAbsolute` and `Train.TryGetAbsoluteTimeForEntry` agree on `% 1440`**, so a 25-hour trip wraps. Use `RollTimeToTomorrowIfTooOld` semantics if computing real durations.
- **`Train.AddEntry` requires `stationsEastToWest`** to know where to insert. Wrong order produces gibberish.
- **`Train.SortEntries` reorders in-place** based on direction. Switching `Direction` in the editor calls `SortEntries` (`VisualTimetableEditor.cs:469`).
- **Meets are stored as `IReadOnlyList<string>`** — empty `Meets.Count == 0`, never null. UI displays comma-joined.
- **`Train.GetHashCode` returns only `Name.GetHashCode()`** — two trains with same name + different routes hash equal. Don't put trains in a `HashSet<Train>`.

---

## `TimetableStation` and `TimetableBranch`

```csharp
[Serializable] public class TimetableBranch                                 // TimetableBranch.cs:7
{
    public string name;
    public List<TimetableStation> stations;                                 // east-to-west order
}

[Serializable] public class TimetableStation                                // TimetableStation.cs:7
{
    public string code;                                                     // 2-letter timetable code
    public string name;                                                     // display name override; falls back to passengerStop.TimetableName
    public PassengerStop passengerStop;                                     // optional reference
    public MapFeature mapFeature;                                           // optional progression gate
    public int traverseTimeToNext;                                          // seconds between this and the next station east→west
    public JunctionType junctionType;                                       // None | JunctionStation | JunctionDuplicate
    public bool IsBranchJunctionDuplicate => junctionType == JunctionType.JunctionDuplicate;
    public string DisplayName { get; }                                      // name ?? passengerStop.TimetableName
    public bool IsEnabled { get; }                                          // mapFeature.Unlocked OR !passengerStop.ProgressionDisabled
}
```

`branches` is serialized on the `TimetableController` Inspector. Authoring-time data — branches define the topology that the editor's "illogical stations" gate uses.

`JunctionDuplicate` is the trick for representing a branch junction: the station appears once on the main branch and again on the side branch (as the first or last station). `GetIllogicalStations` (`TimetableExtensions.cs:78`) walks these to detect impossible station picks.

---

## UI surface: `UI.Timetable.*`

### `TimetableWindow` (read-only viewer)

`TimetableWindow.cs`. Windowed table view of the current schedule. Identifier `"Timetable"`, default 280×550 pt, resizable. `Toggle()` early-returns + toasts if `TimetableFeature == false`. Subscribes via `RebuildOnEvent<TimetableDidChange>()` (`:128`) to auto-refresh.

`BuildTimetableContent` (`:145`) is `internal static` — **reused by `VisualTimetableEditor`** to render the scrolling timetable in the editor's left panel.

### `TimetableEditorWindow` (the host)

`TimetableEditorWindow.cs:12`. Shell `MonoBehaviour` that hosts a `VisualTimetableEditor`. Identifier `"TimetableEditor"`, default 700×550 pt. Listens to `TimetableDidChange` and prompts a modal if the editor has unsaved changes when another player edits (`:96`).

### `VisualTimetableEditor` (the editor body)

`VisualTimetableEditor.cs:17`. Builds:
1. Left side: `TimetableWindow.BuildTimetableContent` (the table).
2. Right side: per-train detail panel — Symbol/Direction/Class/Type fields, Remove button, per-station rows with arrival/departure/meets.
3. Bottom: Apply Timetable, Add Train, Load/Save dropdown.

**Working copy semantics**: `_timetable = TimetableController.CurrentRaw.Clone()`. `HasUnsavedChanges = !_timetable.Equals(CurrentRaw)`. `HandleApplyChanges` writes via `TimetableController.SetCurrent(TimetableWriter.Write(_timetable))` — the round-trip text → `SetTimetable` message → host parse.

### `TextTimetableEditor` (alternate text-mode editor)

`TextTimetableEditor.cs`. Uses `BaseTimetableEditor`. **Not currently wired into any window** — `TimetableEditorWindow` only instantiates `VisualTimetableEditor`. Looks like a vestigial alternative editor. It's a free patch target if you want to re-expose it.

`PredefinedTimetableStore` (`PredefinedTimetableStore.cs`) lists `*.txt` files in `StreamingAssets/Timetables` for the "Load …" dropdown options. `TimetableLoadSaveHelper` reads/writes `Application.persistentDataPath/Timetables/`.

### Patch candidates (UI)

| Target | Why patch |
|---|---|
| `TimetableWindow.BuildTimetableContent` | Shared with editor — patch to add columns/decorations |
| `VisualTimetableEditor.Build` / `BuildTrainEditorContent` | Add fields per train |
| `VisualTimetableEditor.AddCellForStation` | Add custom per-stop indicators |
| `VisualTimetableEditor.HandleApplyChanges` | Inject pre-write transforms |
| `TimetableEditorWindow.OnEnable` / `Populate` | Replace `_visualEditor` with `_textEditor` to switch editor mode |
| `PredefinedTimetableStore.AvailableTimetables` | Inject mod-provided starter timetables |
| `TimetableLoadSaveHelper` | Custom save formats |

### Gotchas (UI)

- **`TimetableEditorWindow.Show()` is the only call point** — `TimetableWindow` calls `TimetableEditorWindow.Shared.Show()` only when `TimetableController.CanEdit` returns true (Officer-or-above auth check).
- **Modal prompts for "discard or apply on close"** are wired through `ModalAlertController.Present`. The apply path runs `HandleApplyChanges` inline before closing.
- **The editor's working copy is `CurrentRaw.Clone()`** — relative times survive the round-trip. The displayed table view (left panel) re-`ToAbsolute`s for display.
- **`TimetableFeature` off does NOT close an open editor.** Toggle the feature off via Trainmaster while an editor is open: the editor still shows trains; clicking Apply will parse and host-write, but `Current` stays null since `UpdateTimetable` early-clears.
- **`TimetableController.CanEdit` is per-message capability**, not per-feature-flag. Even with `TimetableFeature == false`, an Officer can edit. The editor will accept the write but the AI won't act on it.

---

## Train scheduling: how the AI knows it's a scheduled train

The AI driver does *not* read the timetable directly per train. The bind is via **TrainCrew**:

```
Car.trainCrewId (string)                         ← settable via SetCarTrainCrew (Trainmaster)
   │
   ▼
StateManager.PlayersManager.TrainCrewForId(crewId, out TrainCrew)
   │
   ▼
TrainCrew.TimetableSymbol (string)               ← settable via RequestSetTrainCrewTimetableSymbol (Trainmaster)
   │
   ▼
TimetableController.TryGetTrainForTrainCrewId(crewId, out Timetable.Train)
   │
   ▼
AutoEngineerPlanner.UpdateTimetableTrain()       ← called on TimetableDidChange or crew change
   │
   ▼
AutoEngineerPassengerStopper.SetTimetableTrain(train)   ← the AI runtime binding
```

Set via `RequestSetTrainCrewTimetableSymbol(crewId, symbol)` — Trainmaster, host-handled by `PlayersManager.HandleRequestSetTrainCrewTimetableSymbol` (`:429`). The string is stored in `TrainCrew.TimetableSymbol` and round-tripped through `Snapshot.TrainCrew` (`Game.Messages/Snapshot.cs:147`).

Lookup is by symbol string equality. **No type/class enforcement** — assigning a Freight-typed train symbol to a passenger consist works fine; the AI won't auto-stop at passenger stops not in the train's entries.

`Car.TryGetTimetableTrainCrewId` (`CarExtensions.cs:271`) walks coupled cars to find the first non-empty `trainCrewId`. So you can assign a crew ID to one locomotive and the entire consist inherits.

### Patch candidates (scheduling bind)

| Method | Why patch |
|---|---|
| `CarExtensions.TryGetTimetableTrainCrewId` | Customize how a consist resolves its crew (e.g., per-car priority) |
| `CarExtensions.TryGetTimetableTrain` | Inject custom mapping from car → Train (bypass crew indirection) |
| `TimetableController.TryGetTrainForTrainCrewId` | Replace crew-symbol lookup |
| `PlayersManager.HandleRequestSetTrainCrewTimetableSymbol` | Validate or transform symbol assignment |

---

## `AutoEngineerPassengerStopper` — the in-train arbiter

`Model.AI/AutoEngineerPassengerStopper.cs:16`. See [Auto-Engineer › `AutoEngineerPassengerStopper`](autoengineer.md#autoengineerpassengerstopper) for the full integration.

Vanilla key responsibilities:
- Watches the locomotive's coupled passenger cars (`UpdateCars`).
- Computes union of `PassengerMarker.Destinations` across all coupled passenger cars.
- Picks `_nextStop` from the planner's `maybeAhead`/`maybeUnder` results.
- Sets a 0 mph velocity target at the stop centroid (`FindStopDistanceForIdentifier` returns the average of first-and-last passenger-car positions whose marker contains the destination).
- Holds at platform for `MinimumStopDuration` (default 60 s, from `aiPassStopMinStopDur`) AND until timetable departure if `IsTimetableTrain`.
- At the *last* timetable entry, posts `"Timetable schedule complete."` notice and `WithMaxSpeedMph(0)` (stops the AI).
- Sounds the bell at <100m + >1mph approaching a stop.

Constants:
- `AnnounceTimeout = 60f` — debounce for "Arrived/Departing" voice lines.
- `TimetableWaitAnnounceTimeout = 1800f` — debounce for "Holding until X" announcements (30 min).
- `MarkersHashChangeTimeout = 5f` — keeps the train held an extra 5 s if `PassengerMarker` is mutating (a passenger is currently boarding/disembarking).
- `ArrivalBellDistance = 100f`.
- `StopRadius = 10f` — within 10 m of stop centroid is "at the platform."

`SetNextStopTimetable` text is `"Timetable: Depart <station> <HH:MM>"`. `SetNextStopStation` is `"Station Stop: <DisplayName>"`.

`Say(...)` calls go through `Hyperlink.To(_nextStop)` — clicking the hyperlink jumps the camera to the stop.

---

## Save/load shape

| Object | KVO id | Key | Type | Auth | Notes |
|---|---|---|---|---|---|
| Per-stop waiting | `pass.<identifier>` | `state` | Dictionary `{waiting, lastGrow}` | HostOnly | One per `PassengerStop` registered in `Awake` |
| Per-car marker | `<carId>` (the Car KVO) | `ops.passengerMarker` | Dictionary `{groups, destinations, lastStop, ttAutoDest}` | HostOnly | See [Cars & Cargo › KVO map](cars-cargo.md#per-car-kvo-key-reference-cross-cutting-cheat-sheet) |
| Timetable | `timetable` | `current` | Dictionary `{timetable, modified, author}` (or legacy bare String) | HostOnly | Migrated to dict on load |
| Reputation per stop | `_reputation` | `ls-<stopId>` | int seconds (last served) | HostOnly | Written by `ReputationTracker.PassengerStopServed` |
| Reputation per stop | `_reputation` | `sh-<stopId>` | int[] (4-hour bucketed history) | HostOnly | History bins for charting |
| Reputation per edge | `_reputation` | `<a-b>-edge` | bool | HostOnly | Player-visited-edge marker for `PassengerReputationCalculator` |
| Settings | `_game` | `aiPassStopEnable` | bool, default true | Trainmaster | |
| Settings | `_game` | `aiPassStopMinStopDur` | int seconds, default 60 | Trainmaster | The truncated key noted in [save-load.md › `_game`](save-load.md#game-storage-keys) |
| Settings | `_game` | `timetableFeature` | bool, default false | HostOnly | Default OFF — must be explicitly enabled per save |
| Crew | (snapshot) `Snapshot.TrainCrew` | `TimetableSymbol` | string | (snapshot) | See `Game.Messages/Snapshot.cs:147` |

The waiting state per stop is unbounded in size as more (origin, destination, boarded) groups accumulate, but the 10-minute coalesce window + 4-hour expiry caps growth to roughly `destinations × 24` groups per stop in steady state.

---

## Patch points: custom passenger types, schedule logic, custom timetables

### Custom passenger types

Vanilla treats "passengers" as a single `Load` SO referenced by every coach's `LoadSlot.RequiredLoadIdentifier == "passengers"` and by every `PassengerStop.passengerLoad`. To add a custom passenger type:

1. **New Load asset.** Author a new `Load.id` (the SO's `.asset` name). For weight: set `units = LoadUnits.Quantity`, `unitWeightInPounds`. Register with `CarPrototypeLibrary.opsLoads` (or patch `CarPrototypeLibrary.LoadForId`).
2. **Coach LoadSlot.** Add a slot with `RequiredLoadIdentifier == "<your-id>"`. **`IsPassengerCar` will return `false`** for this car unless one of its slots is `"passengers"`. Patch `CarExtensions.IsPassengerCar` to also accept your id, or extend the LoadSlot id check.
3. **`PassengerStop.passengerLoad`.** A stop's `passengerLoad` is a single Load SO. To support multi-class stops, you must either:
   - Per-class stops (separate `PassengerStop` GameObjects for each load type), or
   - Patch `PassengerStop.PassengerCapacity` and `PassengerStop.LoadCar` / `UnloadCar` / `WorkCar` to handle multiple `passengerLoad` references.
4. **`PassengerMarker`.** The struct has no per-class field. Mark up `PassengerGroup` with a class field by patching `FromPropertyValue`/`PropertyValue` (forward-compatible since vanilla ignores unknown keys in the dict).
5. **Pricing.** `QueuePayment`'s formula is per-passenger flat — no class differentiation. Patch `QueuePayment` to read marker class.

### Custom schedule logic (replace the AI stop arbiter)

1. `AutoEngineerPassengerStopper` is added/removed dynamically by `AutoEngineerPlanner.UpdateCars` (see [Auto-Engineer › `AutoEngineerPassengerStopper`](autoengineer.md#autoengineerpassengerstopper)). Don't hold long-lived references.
2. To replace the stopper: patch `AutoEngineerPlanner.UpdateCars` to add your own `AutoEngineerComponentBase` subclass.
3. To extend departure logic: patch `AutoEngineerPassengerStopper.ShouldStayStopped` (`:317`) — controls hold-at-platform time including timetable departure. Returning false releases the hold.
4. To add per-stop hold rules (e.g., "wait for opposing meet"): patch `ShouldStopPerTimetable` (`:414`) or `IsTimetableStop` (`:427`).
5. To override the platform centroid: patch `FindStopDistanceForIdentifier` (`:439`).

### Custom timetables (replace the document model)

**Three layers to consider:**

1. **Document storage:** the `timetable._current` KVO key. To add per-train metadata, patch `TimetableDocument.ToValue`/`Value` ctor (`TimetableController.cs:21`) to round-trip extra dict keys.
2. **Parse layer:** `TimetableReader.TryRead`. Wrap and add post-parse hooks. To add a new train type beyond P/F: patch `ReadTrainLine` to recognize it and `Train.TrainType` to store it (must extend the enum).
3. **Runtime layer:** `TimetableController.UpdateTimetable` calls `ToAbsolute()` and `FilterForUse()`. Mod custom-types may want to bypass `FilterForUse` (which silently drops unknown stations). Patch `FilterForUse` or `UpdateTimetable`.
4. **Multiple timetables:** vanilla has exactly one `Current`. To support multiple (e.g., weekday vs weekend), the cleanest approach is to patch `TimetableController.Current` getter to return one of N stored documents. The KVO key would need extension or shim through the Source string with a header.

**Persistent multi-timetable storage example:** stash custom timetables in `Storage.{your-key}` on `_game` KVO, and patch `TimetableController.Current` getter to consult it.

### Custom stop selection bias

`PassengerStopTimetableLogic.GetTimetableDestinations` (`PassengerStopTimetableLogic.cs:40`) is the pure function that biases waiting-passenger growth toward upcoming-departure destinations. Replace it via Harmony to add custom rules (e.g., "non-revenue passengers", "always grow alarka"). Configuration values come from `Config.Shared.passengerDeparture*` AnimationCurves at `Model/Config.cs:104-110`.

---

## Cross-references

- `PassengerStop` cameo, ops tick, ledger payments — see [Industries & Ops › PassengerStop (NOT an IndustryComponent)](industries-ops.md#passengerstop-not-an-industrycomponent) and [Ledger](industries-ops.md#ledger).
- `Coach`/`Baggage` archetype basics, `IsPassengerCar`/`IsFreight` asymmetry, passenger LoadSlot mechanics — see [Cars & Cargo › Archetype implications](cars-cargo.md#behavioral-implications-of-archetype) and [Cargo / loading](cars-cargo.md#cargo--loading).
- AI bell, hold logic, contextual orders ("Bypass timetable"), planner integration — see [Auto-Engineer › AutoEngineerPassengerStopper](autoengineer.md#autoengineerpassengerstopper).
- `TimetableEditor` / `TimetableWindow` `IBuilderWindow` shell — see [UI (Vanilla) › Builder windows](ui-vanilla.md).
- `aiPassStopMinStopDur` save key — see [Save/Load › `_game` keys](save-load.md#game-storage-keys).
- `AccessLevel` ladder, KVO authorization — see [KVO Patterns › Authorization](kvo-patterns.md#authorization-prefixes).
- `Config.Shared.passengerDeparture*` AnimationCurves — see [Wear › `Model.Config` curves](wear-durability.md#modelconfig-curves-tuning-surface) for the same pattern.
- Reputation per-stop service history (`ls-<id>`, `sh-<id>`) and edge-visited tracking — `Game.Reputation.ReputationTracker.cs:340-410` (no dedicated crib sheet yet — candidate for a future "reputation" sheet).
