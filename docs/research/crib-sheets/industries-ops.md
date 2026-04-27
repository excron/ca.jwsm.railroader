# Industries & Ops — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Wear & Durability](wear-durability.md), [Couplers](couplers.md)
**Background surveys:** `../map-mods-vanilla-survey.md`, `../multiplayer-vanilla-survey.md`

The "ops" subsystem is the entire economic, freight, and contract layer that turns the railroad into a game: industries produce/consume loads, interchanges import/export the cars that satisfy demand, waybills route cars to destinations, the host pays the player when cars arrive, and contracts gate output rate via a tier system tied to delivery performance. Everything is host-authoritative — clients read state via KVO observers and may only nudge the system through a tiny set of message types (`SetRepairMultiplier`, `ModifyContract`, `RequestOps`, `LedgerRequest`). The `OpsController` is the single Unity-side coordinator; it walks `Industry` GameObjects in the scene and runs them on a per-industry ~5s coroutine plus a daily/midnight rollover.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `OpsController` | `Model.Ops/OpsController.cs:25` | Singleton; owns industry/interchange enumeration, waybill rewriting, sweep, payment coalescing |
| `Industry` | `Model.Ops/Industry.cs:15` | Per-industry GameObject; owns contract state KVO, daily rollover, per-component tick coroutine |
| `IndustryComponent` (abstract) | `Model.Ops/IndustryComponent.cs:13` | Base class for everything bound to a `TrackSpan[]` — loaders/unloaders/repair/team-track/interchange/passenger/progression |
| `Interchange` | `Model.Ops/Interchange.cs:17` | Foreign-road portal; daily service tick orders cars per `IOrder` queue, removes completed waybills |
| `Waybill` (struct) | `Model.Ops/Waybill.cs:8` | Origin/destination/payment/tag — stored as `ops.waybill` KVO key on `Car` |
| `IIndustryContext` / `IndustryContext` | `Model.Ops/IIndustryContext.cs`, `IndustryContext.cs:20` | Per-tick read-write façade passed into `IndustryComponent.Service`; gateway to ledger, storage, ordering |
| `IOpsCar` / `OpsCarAdapter` | `Model.Ops/IOpsCar.cs`, `OpsCarAdapter.cs` | Shim over `Car` so `IndustryComponent`s never touch `Car` directly — easier to mock/replace |
| `RepairTrack` | `Model.Ops/RepairTrack.cs:21` | Repair industry tick — see [Wear › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair) |
| `Order` (struct) | `Model.Ops/Order.cs:6` | Outstanding inbound demand; stored on `Interchange.Orders` list |
| `Load` (ScriptableObject) | `Model.Ops.Definition/Load.cs:8` | Cargo definition — units, density, importable, payPerQuantity, costPerUnit |
| `OverrideDestinationExtensions` | `Model.Ops/OverrideDestinationExtensions.cs` | The `ops.repair-dest` non-Waybill routing channel |
| `Ledger` | `Game.State/Ledger.cs:9` | Append-only money log; `Ledger.Category` enum + `EntityReference?` payee |

---

## Spine: how an ops tick flows

```
Industry GameObject (host only — coroutine guarded by IsHost)
   │  TickCoroutine: 5s WaitForSeconds × N, every 3rd tick = Service
   │  (5s real time × TimeMultiplier → ~15s game seconds dt per Service)
   ▼
Industry.Tick(serviceInterval=15f, shouldService=true)
   │  for each child IndustryComponent:
   │    component.CheckForCompleted(ctx)         ← always
   │    component.Service(ctx)                    ← every 3rd tick
   ▼
IndustryComponent.Service(IIndustryContext ctx)
   ├── reads ctx.CarsAtPosition()  ── filtered by carTypeFilter
   ├── reads/writes ctx.QuantityInStorage / AddToStorage / RemoveFromStorage  (KVO key "storage" on Industry)
   ├── orders cars: ctx.OrderEmpty / ctx.OrderLoad (load==null=empty) / ctx.OrderAwayLoaded / ctx.OrderAwayEmpty
   ├── pays:        ctx.PayLoad(load, units), ctx.PayWaybill(car, waybill)
   └── moves:       ctx.RemoveCar / ctx.MoveToBardo

Daily rollover (host, on TimeDayDidChange at midnight, OpsController.DayDidChange)
   ├── UpdatePerformance(now)   ── walks open waybills, computes per-industry on-time perf, logs to "_perfHist" (7-day rolling)
   ├── industry.DailyReceivables(now)  ── e.g. IndustryUnloader pays out queued payPerQuantity
   ├── industry.RollToNextContract()    ── promote / demote / penalty
   └── industry.DailyPayables(now)      ── e.g. RepairTrack pays shop wages

Per-minute (TimeMinuteDidChange, OpsController.CheckServiceInterchanges)
   └── For each Interchange whose GetNextServiceTime <= now:
       PrepareToService()                 ── clears Orders list
       RequestIndustriesOrderCars()       ── walks industries area-by-area, calls industry.OrderCars(ctx) which fills Orders
       interchange.ServeInterchange(ctx)  ── removes completed inbound waybills, places new cars per Orders
```

**Tick cadence summary:**
- `Industry.TickCoroutine` (`Industry.cs:174`): `WaitForSeconds(5)`, every third iteration triggers `Service`. So `Service` runs about every 15 real seconds (with `dt=15f` passed). Random 0–15s startup jitter (line 176).
- `OpsController.PeriodicUpdate` (`OpsController.cs:128`): `WaitForSecondsRealtime(0.25f)`, only flushes coalesced payment announcements.
- `TimeDayDidChange` → `DayDidChange` runs on midnight. Host-only.
- `TimeMinuteDidChange` → `CheckServiceInterchanges` runs every game minute. Host-only.

---

## `Model.Ops.OpsController` (the coordinator)

```csharp
public static OpsController Shared { get; private set; }                        // 64
public Industry[]    AllIndustries     { get; private set; }                    // 74
public Interchange[] AllInterchanges   { get; private set; }                    // 78
public IEnumerable<Interchange> EnabledInterchanges => …;                       // 68 — filters Disabled+ProgressionDisabled
public IEnumerable<Area>  Areas => GetComponentsInChildren<Area>();             // 66
public SwitchListController SwitchListController { get; private set; }          // 70
public static int InterchangeShuffle => Storage.InterchangeShuffle;             // 82
public static string[] ForeignRoads { get; set; }                               // 80
```

`Awake` (`OpsController.cs:84`) attaches `SwitchListController` and `PassengerExpiration` as components and reads `ForeignRoads` from `ForeignRoadsReader`. `OnEnable` registers Messenger listeners for `IndustriesDidChange`, `TimeDayDidChange`, `TimeMinuteDidChange`. `RebuildCollections` (`:292`) walks `GetComponentsInChildren` for `Industry`, `IndustryComponent`, `Interchange` and builds `_carPositionLookup` keyed by `IndustryComponent.Identifier`.

### Public read API

```csharp
public Industry          IndustryForId(string id);                              // 123
public IOpsCar           CarForId(string carId);                                // 338
public IEnumerable<IOpsCar> CarsInArea(Area area);                              // 348
public IEnumerable<Car>  CarsAtPosition(OpsCarPosition position);               // 758
public OpsCarPosition?   PositionForCar(Car car);                               // 742
public Area              ClosestArea(Car car);                                  // 391
public Area              AreaForCarPosition(OpsCarPosition position);           // 374  (cached)
public int               PaymentForMove(OpsCarPosition start, end, int tons);   // 666
public int               CalculateGraceDays(OpsCarPosition start, end);         // 678
public bool              CanWaybillTo(Car car, OpsCarPosition destination);     // 1236
public bool              TryGetActiveContract(OpsCarPosition pos, out Contract);// 1246
public IEnumerable<(Car,Waybill)> GetOpenWaybills();                            // 898
public bool              TryGetIndustryComponent<T>(string id, out T);          // 1116
public bool              TryDecodeBardo(string bardoId, …);                     // 1093
```

### Order / waybill construction (host writes to Car KVO)

```csharp
public void AddOrderForOutboundEmptyCar (IOpsCar car, OpsCarPosition carPosition, string orderTag, bool noPayment); // 422
public void AddOrderForOutboundLoadedCar(IOpsCar car, OpsCarPosition carPosition, string orderTag, bool noPayment); // 427
public bool AddOrderForInboundCar(CarTypeFilter, Load, OpsCarPosition, Industry, string tag, bool noPayment, out float quantity); // 440
private Interchange InterchangeForPosition(OpsCarPosition position, OpsCarPosition? origin);                         // 645
```

Both outbound entry points funnel into `WaibillCarToInterchange` (`:432`) which: (a) selects a return interchange via `InterchangeSelector` or matches `origin.Identifier` exactly, (b) computes payment via `PaymentForMove`, (c) writes a fresh `Waybill` directly to the car's `ops.waybill` KVO.

`AddOrderForInboundCar` (`:440`) is bimodal: it tries to match an existing car (currently `FindExistingCarForInboundOrder` always returns null — see Gotchas), and if no match is found it queues an `Order` onto the chosen `Interchange.Orders` list. The interchange will later realize that order as a freshly-spawned car at `Interchange.ServeInterchange`.

### Payment formula

```csharp
private int PaymentForMove(Location start, Location end, int tons) {            // 671
    int distance = Mathf.CeilToInt(Mathf.Pow(DistanceInMiles(start, end), 0.5f) * 4f);
    int weight   = Mathf.CeilToInt(tons * 0.25f);
    return 50 + distance + weight;
}
```

So payment = `50 + 4*sqrt(miles) + 0.25*tons` (each rounded up). `DistanceInMiles` (`:697`) memoizes `graph.TryFindDistance` results in `_distanceInMilesCache`. **Returns 0 miles if the graph search fails** (no warning).

`CalculateGraceDays`: 0 days under 20 mi, 1 day 20–40 mi, 2 days over 40 mi.

### Sweep (admin teleport)

```csharp
public string Sweep(string query);                                              // 779  ("*"=all, else CarForString)
private string SweepAll();                                                      // 797
public bool Sweep(Car car);                                                     // 817 (skips no-waybill or completed)
private bool MoveCarTo(Car car, OpsCarPosition position);                       // 837 (FindOpenSpaceFromLower → MoveCar/MoveCarCoupleTo)
```

Driven by `RequestOps` message (Trainmaster auth) → `OpsController.RequestOps` (`:323`) or directly from `OpsCommand` console subcommand.

### Coalesced payment announcements

`AddCoalescedPaymentAnnouncement` (`:1135`) accumulates per-industry payments for 1 second (real time, debounced); `AnnounceCoalescedPayments` is the periodic flush. Patch here to intercept "X payment for delivery of Y" broadcasts.

### Patch candidates

| Method | Why patch |
|---|---|
| `OpsController.PaymentForMove` | Replace the entire freight payment formula. |
| `OpsController.CalculateGraceDays` | Replace the on-time grace policy. |
| `OpsController.CheckServiceInterchanges` | Single chokepoint for daily interchange tick — patch prefix to add custom per-tick interchange behavior, or to change the cadence. |
| `OpsController.RequestIndustriesOrderCars` | Walks all industries; postfix to inject mod-side ordering. |
| `OpsController.WaybillCarToInterchange` (private) | Intercept all "send car back" decisions. |
| `OpsController.AddOrderForInboundCar` | Last-mile of all `OrderEmpty`/`OrderLoad` requests. |
| `OpsController.UpdatePerformance` (private) | Custom performance scoring. |
| `OpsController.Sweep(Car)` | Add side-effects (mod logging, custom completion) to teleport. |

### MP authority

- All ops mutators (waybill writes, storage writes, order list mutations, contract changes) run inside the host-only `Industry.TickCoroutine`/`OpsController.DayDidChange` loops.
- `RequestOps` message → `OpsController.RequestOps` is `[MinimumAccessLevel(AccessLevel.Trainmaster)]`. See `Game.Messages/RequestOps.cs`.
- `ops.waybill` is a Crew-allowed KVO key (no `_` prefix), but in practice clients only write it via `Car.SetWaybillAuto` cycling through auto-destinations, *not* arbitrary host-side waybills. Auth resolution: `Car.AuthorizationRequirementForPropertyWrite` → defaults to `MinimumLevelTrainmaster` for non-prefixed keys per the multiplayer survey table.

---

## `Model.Ops.Industry` (per-industry container)

```csharp
public string identifier;                                                       // 17
public bool   usesContract;                                                     // 20
public IEnumerable<IndustryComponent> Components { get; }                       // 40 (cached GetComponentsInChildren)
public IndustryStorageHelper Storage { get; private set; }                      // 68
public Contract? Contract     { get; internal set; }                            // 74    — KVO "contract"
public Contract? NextContract { get; private  set; }                            // 86    — KVO "nextContract"
public IReadOnlyDictionary<int, float> PerformanceHistory { get; private set; } // 98    — KVO "_perfHist"
internal int ReceivedCarCount { get; set; }                                     // 110   — KVO "_recvdCars"
public EntityReference EntityReference => new(Industry, identifier);            // 70
```

KVO object lives on `Industry.gameObject` (added in `Awake`, `:131`) and is wrapped by `IndustryStorageHelper` which also implements `IPropertyAccessControlDelegate` (auth: `extraScheduled` is Trainmaster, all else HostOnly).

### Tick

```csharp
private IEnumerator TickCoroutine() {                                           // 174
    yield return new WaitForSeconds(Random.Range(0f, 15f));                     // jitter
    InitializeIfNeeded();                                                       // version-keyed (KVO "init")
    int tickCount = 0;
    while (true) {
        yield return new WaitForSeconds(5f);
        bool shouldService = tickCount % 3 == 0;
        Tick(15f, shouldService);
        tickCount++;
    }
}
```

`Tick(serviceInterval=15f, shouldService)` (`:205`) iterates `EnumerateComponentContexts(15f)` and calls `CheckForCompleted` always + `Service` when `shouldService`. **The `dt` passed to `Service` is the wall-clock interval (15 game seconds), and `RateToValue` (`IndustryComponent.cs:128`) converts per-day rates by dividing by `86400 / TimeMultiplier`.**

### Contract management

```csharp
public void SetContract(Contract? contract);                                    // 299  (clears performance history)
public void ModifyContract(int modifyTier);                                     // 365  (host; sets NextContract for next rollover)
public void DailyReceivables(GameDateTime now);                                 // 395  (per-component fanout)
public void DailyPayables   (GameDateTime now);                                 // 403
public void RollToNextContract();                                               // 429  (midnight: failing-check → demote, apply tier-change penalty)
public bool TryGetStorageCapacity(Load load, out float capacity);               // 475
```

`RollToNextContract`:
- Reads `PerformanceHistory` (last 3 days), demotes by 2 if avg `< 0.5` (`IsFailing`, `:411`).
- Applies tier-change penalty `PenaltyForChange` (`ContractExtensions.cs:121`): `250 * tierDrop + 250 * max(6-days, 1)`.
- Penalty posts via `StateManager.ApplyToBalance(-penalty, Ledger.Category.Freight, EntityReference, "Tier Change Penalty", quiet:true)`.
- Tier 0 termination calls `OpsController.Shared.ReturnWaybillsFrom(this)` to clear all autodest/waybills pointing at the canceled industry.

### Performance scoring

`CalculatePerformance` (`:305`) — averages waybill ages > 0.1 day, then `Mathf.InverseLerp(5f, 1f, avgAge)` (so 1 day avg → score 1.0, 5+ days avg → 0). If `score > 0.99 && ReceivedCarCount < 1`, returns null (don't pollute history with no-data days). 7-day rolling history; oldest evicted at `Industry.cs:347`.

### Patch candidates

| Method | Why patch |
|---|---|
| `Industry.TickCoroutine` (private) | Change tick cadence (currently 5s × 3 = 15s game seconds dt per service call). |
| `Industry.RollToNextContract` | Contract promotion/demotion policy. |
| `Industry.CalculatePerformance` (private) | Performance scoring formula — currently age-weighted only. |
| `Industry.DailyReceivables`/`DailyPayables` | Add daily side-effects on top of per-component fanout. |
| `ContractExtensions.PenaltyForChange` (extension) | Penalty schedule for tier downgrade. |
| `ContractExtensions.NumbersForTier` (extension) | Tier→percent multiplier table (currently 0.24 / 0.34 / 0.49 / 0.7 / 1.0). |
| `ContractExtensions.AvailableContracts` | Which tiers a player can negotiate to. |

### MP authority

- All `KeyValueObject` writes on `Industry` are HostOnly per `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` (`IndustryStorageHelper.cs:226`).
- `Industry.ModifyContract` calls `StateManager.AssertIsHost()`. The client-facing path is `ModifyContract` message (`Game.Messages/ModifyContract.cs`), `[MinimumAccessLevel(AccessLevel.Officer)]`.
- `_tickCoroutine` only starts if `StateManager.IsHost` (`Industry.cs:144`).

---

## `Model.Ops.IndustryComponent` (abstract base)

```csharp
public string         subIdentifier;                                            // 29
public TrackSpan[]    trackSpans;                                               // 33
public CarTypeFilter  carTypeFilter = new CarTypeFilter("");                    // 35
public bool           sharedStorage = true;                                     // 38

public string Identifier => Industry.identifier + "." + subIdentifier;          // 42
public Industry Industry { get; }                                               // 54  (cached GetComponentInParent)
public virtual bool IsVisible => trackSpans.Length != 0 && !ProgressionDisabled;// 66
public bool ProgressionDisabled { get; set; }                                   // 78
public virtual string DisplayName => base.name;                                 // 80

// Abstract:
public abstract void Service(IIndustryContext ctx);                             // 160
public abstract void OrderCars(IIndustryContext ctx);                           // 162

// Virtual:
public virtual void Initialize(IIndustryContext ctx, GameVersion fromVersion);  // 124
public virtual void CheckForCompleted(IIndustryContext ctx);                    // 139  ← drives waybill completion
public virtual bool WantsAutoDestination(AutoDestinationType type);             // 155
public virtual void DailyReceivables(GameDateTime now, IIndustryContext ctx);   // 164
public virtual void DailyPayables   (GameDateTime now, IIndustryContext ctx);   // 168
public virtual void EnsureConsistency();                                        // 222
public virtual bool AcceptsCarsWithLoad(Load load) => true;                     // 226
public virtual void BuildPanel(UIPanelBuilder builder);                         // 213
public virtual IEnumerable<PanelField> PanelFields(IndustryContext ctx);        // 217
```

`OpsCarPosition` is **implicitly convertible from `IndustryComponent`** (`:208`). That's how `_industryComponent` is passed everywhere a position is expected: `_carPositionLookup[ic.Identifier] = ic;` — the dictionary stores the IC itself as the position resolver target.

### Waybill completion default

```csharp
public virtual void CheckForCompleted(IIndustryContext ctx) {                   // 139
    foreach (IOpsCar car in EnumerateCars(ctx)) {
        Waybill? wb = car.Waybill;
        if (wb.HasValue && wb.Value.Destination.Equals(this) && !wb.Value.Completed)
            OnCompleteWaybill(ctx, car, wb.Value);
    }
}

protected virtual void OnCompleteWaybill(IIndustryContext ctx, IOpsCar car, Waybill waybill) { // 172
    ctx.PayWaybill(car, waybill);                       // pays Industry via Ledger.Freight
    waybill.PaymentOnArrival = 0;                       // zero out so re-checks don't double-pay
    waybill.Completed        = true;
    car.SetWaybill(waybill, this, $"Paid Completed ({days:F1} days)");
    Industry.ReceivedCarCount++;
}
```

The `EnumerateCars` filter (`:182`) requires `CarTypeFilter` match; `requireWaybill` overload also checks destination.

`PayWaybill` (`IndustryContext.cs:334`) computes `payment + timelyBonus - conditionFine`:
- `Contract.TimelyDeliveryBonus(days, basePayment)` (`Contract.cs:35`) — tier 2..5 give base bonus 4/6/8/10%; halved each day late, zero after 2 days.
- `Waybill.ConditionFineForCarCondition(condition)` (`Waybill.cs:75`) — 0 fine at condition ≥ 0.95, scales linearly to 75% of payment at condition 0.

### Patch candidates

| Method | Why patch |
|---|---|
| `IndustryComponent.CheckForCompleted` | Override per-component to alter what counts as "delivered" (e.g., partial loads, multi-car shipments). |
| `IndustryComponent.OnCompleteWaybill` | Single chokepoint for waybill payout — patch postfix to add bonuses, prefix to override completion criteria. |
| `IndustryComponent.EnumerateCars` (protected) | Filtering of cars at the position. |
| `IndustryComponent.AcceptsCarsWithLoad` | Decides whether `OpsController.CheckLoads` returns mismatched cars to the interchange. |
| `IndustryComponent.WantsAutoDestination` | Drives `CycleAutoWaybill` UI. |

### MP authority

`IndustryComponent` is host-only by virtue of being driven from `Industry.TickCoroutine`. There is no client-callable surface on the component itself; UI panels send via dedicated messages (`SetRepairMultiplier`, `ModifyContract`).

---

## Concrete `IndustryComponent` types

### `IndustryLoaderBase` (abstract)

`Model.Ops/IndustryLoaderBase.cs:7`. Common base for any "produces and loads cars" component.

```csharp
public Load  load;                                                              // 9
public float productionRate = 1f;                                               // 11   units/day
public float maxStorage     = 1f;                                               // 13
public bool  orderEmpties   = true;                                             // 15

public override void OrderCars(IIndustryContext ctx) {                          // 18
    if (orderEmpties && Industry.ShouldOrderCars()) {
        // predict nextDay output, call ctx.OrderEmpty until capacity meets demand
    }
}

public override bool AcceptsCarsWithLoad(Load checkLoad) => checkLoad == load;  // 64
```

### `IndustryLoader : IndustryLoaderBase`

`Model.Ops/IndustryLoader.cs:8`. The concrete "produces output, loads cars at carLoadRate per day" implementation.

```csharp
public float carLoadRate = 1f;                                                  // 11
public bool  orderAwayLoaded = true;                                            // 13

public override void Service(IIndustryContext ctx) {                            // 24
    float prodMult  = Industry.GetContractMultiplier();
    ctx.AddToStorage(load, RateToValue(productionRate * prodMult, ctx.DeltaTime), maxStorage);
    foreach (IOpsCar car in EnumerateCars(ctx, requireWaybill: true)
                              .Where(c => c.IsEmptyOrContains(load))
                              .OrderByDescending(c => c.QuantityOfLoad(load).quantity)) {
        float toLoad = Mathf.Min(stored, RateToValue(carLoadRate * prodMult, ctx.DeltaTime));
        float loaded = car.Load(load, toLoad);
        if (car.IsFull(load))
            if (orderAwayLoaded) ctx.OrderAwayLoaded(car); else car.SetWaybill(null, this, "Full");
        ctx.RemoveFromStorage(load, loaded);
    }
}
```

### `IndustryUnloader : IndustryComponent`

`Model.Ops/IndustryUnloader.cs:10`. Receives loads, consumes them.

```csharp
public Load  load;
public float carUnloadRate;            // units/day, per car at the spot
public float storageConsumptionRate;   // units/day, drained from storage
public float maxStorage;
public bool  orderAwayEmpties = true;
public bool  orderLoads       = true;

public override void Service(IIndustryContext ctx);                             // 49
public override void DailyReceivables(GameDateTime now, IIndustryContext ctx);  // 98
public override bool AcceptsCarsWithLoad(Load checkLoad) => checkLoad == load;  // 108
```

`DailyReceivables`: at midnight, totals `KeyUnloadedTotal` counter (KVO) and calls `ctx.PayLoad(load, total)` if ≥ 1 unit. So `payPerQuantity` is paid on a daily-batch cadence, not per-tick.

`Initialize` (`:39`) seeds storage at 25% on first load (or specifically for `repair-parts` from pre-V2024_4_0 saves) — the old "free starter pile" logic.

### `FormulaicIndustryComponent : IndustryComponent`

`Model.Ops/FormulaicIndustryComponent.cs:11`. Runs an input→output formula tying its sibling loaders/unloaders together.

```csharp
[Serializable] public class Term { public Load load; public float unitsPerDay = 1f; }
public List<Term> inputTerms;
public List<Term> outputTerms;

public override void Service(IIndustryContext ctx);                             // 39
```

Computes `min(headroomFraction)` across outputs and `min(stockFraction)` across inputs, runs at the lower bound. **If any input is starved, sets a per-component "Production Stopped: …" warning via `Industry.Storage.SetWarning(subIdentifier, …)`.**

Uses `MaxStorageForLoad` (`:96`) which scans sibling components for the loader/unloader matching that load. So formulaic components transparently share the storage of any sibling Loader/Unloader/TeleportLoading.

### `TeamTrack : IndustryComponent`

`Model.Ops/TeamTrack.cs:10`. Generic "any car can show up to load/unload one of N tagged loads" — driven by `TeamTrackProfile` ScriptableObject.

```csharp
public TeamTrackProfile profile;                                                // 13
public float idealCars;                                                         // 17

public override void Service(IIndustryContext ctx);                             // 19
public override void OrderCars(IIndustryContext ctx);                           // 78
```

The waybill `Tag` selects which `TeamTrackProfile.Entry` applies. `Service` loads/unloads at `1/loadingTime` cars/day per spot. `OrderCars` ramps `idealCars` over a 2-day warmup, picks a random profile entry per order.

### `Interchange : IndustryComponent`

See its own section below. Service/OrderCars are no-ops; the real work is in `ServeInterchange` driven externally by `OpsController.CheckServiceInterchanges`.

### `InterchangedIndustryLoader : IndustryComponent`

`Model.Ops/InterchangedIndustryLoader.cs:16`. "Buy cars off the interchange" — used for things like coal/diesel deliveries that aren't produced on-network.

```csharp
public Load load;
[SerializeField] private Ledger.Category ledgerCategory;

public override void Service(IIndustryContext ctx) {}                           // 75 — no-op (driven by interchange)
public override void OrderCars(IIndustryContext ctx);                           // 79 (returns cars from bardo)
public void ServeInterchange(IIndustryContext ctx, Interchange interchange);    // 92
```

`ServeInterchange`: for each (empty or already-this-load) car at the loader, computes cost = `(capacity-current) * load.costPerUnit`, charges the company via `Industry.ApplyToBalance(-cost, ledgerCategory, …)`, fills the car, sends it to bardo for ~23h, then schedules a return-from-bardo order on the interchange.

**Bardo** is the off-map invisible parking lot for cars temporarily out of play (`car.Bardo = identifier` / `MoveToBardo`). The interchange brings them back via `Interchange.OrderReturnFromBardo` → `ReturnFromBardoOrder` (`Model.Ops/ReturnFromBardoOrder.cs`).

### `RepairTrack : IndustryComponent`

See [Wear › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair) for full coverage. Brief recap:

```csharp
public override void Service(IIndustryContext ctx);                             // 123  consumes repair-parts load, applies repair
public override void DailyPayables(GameDateTime now, IIndustryContext ctx);     // 89   pays shop wages; pauses for the day if can't afford
public void HandleSetMultiplier(float multiplier);                              // 170  HostOnly; via SetRepairMultiplier message
```

Cross-references the `OverrideDestination.Repair` channel (see below) — a car bound for repair sets `ops.repair-dest` instead of (in addition to) a waybill.

### `PassengerStop` (NOT an IndustryComponent)

`Model.Ops/PassengerStop.cs:28`. Inherits `GameBehaviour` and implements `IIndustryTrackDisplayable` + `IProgressionDisablable`, but NOT `IndustryComponent`. It runs its own `Loop` coroutine, owns its own KVO object (`pass.<identifier>`), grows population every 300 game seconds, and pays passengers via `StateManager.Shared.ApplyToBalance(amount, Ledger.Category.Passenger, …)`. Out of scope for this sheet beyond noting that:

- Population growth is influenced by sibling industries via `OpsController.RebuildPopulations` (`OpsController.cs:189`) which sums each industry's `Contract.Percent * spanCarLengths` and writes `PassengerStop.AdditionalPopulation`.
- `passengerLoad` field references the `Load` SO used as the cargo type (default "passengers"). Cars carrying it route via `PassengerExtensions` / `PassengerMarker`.

### `ProgressionIndustryComponent : IndustryComponent`

`Model.Ops/ProgressionIndustryComponent.cs:11`. Used by the campaign progression system — `Configure(Section, …, onComplete)` is called by `Game.Progression.Section` to wire up a delivery-quest (e.g., "deliver 5 lumber to the new sawmill"). Tracks per-tag `indRecv` counter, calls `_onComplete` when all deliveries hit their target. Cars delivered via this component are paid via `noPayment: true`.

### `TeleportLoadingIndustry : IndustryLoaderBase`

`Model.Ops/TeleportLoadingIndustry.cs:14`. Used at industries (e.g., coal mines) where cars are loaded off-screen and "appear" at the output spot. Per `carLoadPeriod` (game seconds), finds an empty waybilled car on `inputSpans`, teleports it via `TrainController.MoveCar` to a cleared spot on `outputSpans`, sets its load info, applies handbrakes per cut. Catches an explicit `IsAcceptableAdjacentCutMember` filter so it doesn't disrupt foreign cars.

### `IndustryComponent` constructor / lifecycle map

| Stage | Method | Notes |
|---|---|---|
| Scene load | `Start()` | calls `ValidateIndustryComponent()` (logs error if no carTypeFilter or no spans) |
| First post-load tick | `Initialize(ctx, fromVersion)` | virtual; `Industry.InitializeIfNeeded` only fires for components when KVO key `init` ≠ current Application version. Used for migration / starter inventory. |
| Periodic | `CheckForCompleted` + `Service` | host-only via `Industry.TickCoroutine` |
| Daily | `DailyReceivables`, `DailyPayables` | midnight |
| On `OpsController.CheckServiceInterchanges` | `EnsureConsistency` | called once per interchange-service tick on every component |
| UI | `BuildPanel(UIPanelBuilder)`, `PanelFields(IndustryContext)` | drives the company-window industry panel |

---

## `Model.Ops.Interchange` (the foreign-road portal)

```csharp
public readonly List<IOrder> Orders = new();                                    // 26 (NonSerialized, host-only working set)
public bool Disabled { get; set; }                                              // 30 — KVO "interchangeDisabled" on Industry
private GameDateTime? LastServiced { get; set; }                                // 42
private GameDateTime? ExtraScheduled { get; set; }                              // 54
private static int ServeHour => Storage.InterchangeServeHour;                   // 66
private int NumberOfCarsOrdered => Orders.Sum(o => o.CarCount);                 // 68

public void   PrepareToService();                                               // 102 — clears Orders
public void   ServeInterchange(IIndustryContext ctx);                           // 107
public GameDateTime GetNextServiceTime(GameDateTime now, out NextServiceStyle, bool dailyOnly=false); // 78
public void   AddOrder(Order order);                                            // 193 — coalesces with matching existing order
public void   OrderReturnFromBardo(string carId);                               // 208
public void   ScheduleExtra(GameDateTime? scheduledTime);                       // 236
public static GameDateTime NextAvailableServiceTime(GameDateTime now);          // 321 — Now + 2.5h, rounded to 5 min
```

`ServeInterchange` flow:
1. `ServeInterchangedIndustryLoaders` — fan out to sibling `InterchangedIndustryLoader`s.
2. Walk cars at the interchange: completed waybill → `ctx.RemoveCar` (or `SellAndRemove` if tag=="sell" and player-owned). Otherwise count as occupying capacity.
3. `CalculateCapacity = round(spanLength * 0.7 / 15.24m)` — i.e., 70% of nominal car-length capacity.
4. `ctx.AddOrderedCars(Orders, maxToOrder)` — `IndustryContext.AddOrderedCars` (`:160`) does the actual placement: builds `CarDescriptor`s, calls `_trainController.PlaceTrain(spans, descriptors, …)`, retries with `*0.75` count on failure.
5. Drops fully-filled orders, leaves partials in `Orders` list.

### Fresh-car descriptor construction

`IndustryContext.CreateCarDescriptorForOrder` (`:261`):
- Picks a `CarDefinition` via `prefabStore.Random(carTypeFilter, sizePreference, rnd)` — depends on `_trainController.CarSizePreference` (a `IndustryContext.CarSizePreference` enum: Small/Medium/Large/ExtraLarge).
- Creates a `Waybill(now, _industryComponent, order.Destination, payment, false, order.Tag, graceDays)` — note the **origin** is set to the spawning interchange's IC (so payment routes back correctly).
- Pre-loads via `KeyValueForLoadInfo(0, info)` if `order.Load != null`.
- Sets `oiled` to `Config.Shared.initialOiledDistribution.Evaluate(rand)` if `Car.OilFeature`.
- Random reporting mark from `OpsController.ForeignRoads` (excluding player's mark).

### Service cadence

```csharp
public GameDateTime GetNextServiceTime(GameDateTime now, out style, bool dailyOnly=false) {
    GameDateTime regular = (lastServiced ?? GameDateTime(0, ServeHour))
                            < now.WithHours(ServeHour)
                              ? now.WithHours(ServeHour)
                              : now.WithHours(ServeHour).AddingDays(1);
    style = Daily;
    if (extraScheduled.HasValue && extraScheduled < regular) {
        regular = extraScheduled.Value;
        style = Extra;
    }
    return regular;
}
```

Default daily service hour comes from `Storage.InterchangeServeHour` (`GameStorage` setting). Players can request *one* extra service at `Now + 2.5h` rounded to 5 minutes, via the BuildPanel UI.

### Patch candidates

| Method | Why patch |
|---|---|
| `Interchange.ServeInterchange` | Custom interchange behavior — what gets removed, what gets ordered. |
| `Interchange.CalculateCapacity` (private) | Override capacity formula (currently `spanLength * 0.7 / 50ft`). |
| `Interchange.GetNextServiceTime` | Custom service schedule (e.g., per-interchange override). |
| `Interchange.AddOrder` | Modify order coalescing rules — currently merges by `(Load, CarTypeFilter, Destination)`. |
| `IndustryContext.CreateCarDescriptorForOrder` (private) | Patch via `IndustryContext.AddOrderedCars` postfix? Tricky — `IndustryContext` is a struct so reflection patches need care. |
| `OpsController.CheckServiceInterchanges` | Wrap the entire daily interchange tick. |
| `IndustryContext.AddOrderedCars` (struct method) | Where actual `PlaceTrain` happens. |

### MP authority

- `interchangeDisabled` KVO is HostOnly; UI button calls `SetInterchangeDisabled` only on host.
- `extraScheduled` is **`MinimumLevelTrainmaster`** per `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` (`IndustryStorageHelper.cs:228`) — clients with Trainmaster level can schedule extra service.
- The `Orders` list is non-serialized and rebuilt host-side on each `CheckServiceInterchanges`. **Clients have no view of pending orders at all.** The visible "X cars ordered" number must come from KVO if anything.

---

## `Model.Ops.Waybill` (the freight contract)

```csharp
public struct Waybill(GameDateTime created, OpsCarPosition? origin, OpsCarPosition destination,
                      int paymentOnArrival, bool completed, string tag, int graceDays)         // 8
{
    public GameDateTime    Created;
    public OpsCarPosition? Origin;
    public OpsCarPosition  Destination;          // mandatory
    public int             PaymentOnArrival;
    public bool            Completed;
    public readonly string Tag;
    public int             GraceDays;

    public Value PropertyValue { get; }                                          // serializes to KVO dict
    public static Waybill? FromPropertyValue(Value, IOpsCarPositionResolver);    // 61
    public int ConditionFineForCarCondition(float condition);                    // 75
}
```

### Storage on car

```csharp
public static void SetWaybill(this Car car, Waybill? waybill) {                  // CarExtensions.cs:143
    car.KeyValueObject["ops.waybill"] = waybill?.PropertyValue ?? Value.Null();
}

public static Waybill? GetWaybill(this Car car, IOpsCarPositionResolver _) {     // CarExtensions.cs:125
    return car.Waybill;     // car.Waybill is property on Car itself, calls into the resolver under the hood
}
```

Cars also carry **two auto-destination fallbacks** under `ops.autodest.ld` and `ops.autodest.mt`:

```csharp
public static OpsCarPosition? GetAutoDestination(this Car car, AutoDestinationType, resolver);   // CarExtensions.cs:199
public static bool            SetAutoDestination(this Car car, AutoDestinationType, OpsCarPosition?); // 218
public static void            CycleAutoWaybill(this Car car, IOpsCarPositionResolver);           // 171
```

`SetWaybillAuto` (`CarExtensions.cs:156`) — when called with `null`, looks at `IsLoadEmpty` and routes to either `AutoDestinationType.Empty` or `Load`'s autodest if set. Uses tag `"autodest"` and zero payment.

### Tags (string `Waybill.Tag`)

| Tag | Meaning | Set by |
|---|---|---|
| `null` / empty | Standard waybill | Default in interchange-spawned cars + most paths |
| `"autodest"` | Auto-routed to a player-set destination, no payment | `CycleAutoWaybill`, `SetWaybillAuto` |
| `"sell"` | Player wants to sell at the interchange | UI ("Sell at Interchange"); see `Interchange.SellAndRemove` |
| `"overhaul"` | Override-destination tag for repair-track overhauls | `RepairTrack.InForOverhaul` checks this on `OverrideDestination.Repair` |
| `<TeamTrackProfile.Entry.tag>` | Team-track load tag | `TeamTrack.OrderCars` |
| `<section>.<phase>.<delivery>` | Progression delivery tag | `ProgressionIndustryComponent.OrderTag` |

Note that `Tag` is `readonly` on `Waybill` (struct). To change, you must construct a new Waybill.

### Patch candidates

| Method | Why patch |
|---|---|
| `Waybill.ConditionFineForCarCondition` | Damage→fine schedule; currently 0% at ≥0.95, 75% at 0. |
| `Waybill.FromPropertyValue` (static) | Add custom KVO fields to waybill round-trip. |
| `CarExtensions.SetWaybillAuto` | Auto-routing decision tree. |
| `CarExtensions.CycleAutoWaybill` | UI button "Cycle Destination" toggling between empty/load auto. |

### MP authority

- `ops.waybill` key has no `_` prefix → resolves to default `MinimumLevelTrainmaster` per `Car.AuthorizationRequirementForPropertyWrite` plus the train-crew check. **Crew can write waybills if they own the train.** This is intentional — auto-destination cycling is a crew-level UI action.
- `ops.autodest.ld` / `ops.autodest.mt` — same.
- Setting `Waybill.Completed = true` is host-only practice (only `OnCompleteWaybill` does this), but no enforcement at KVO level.

---

## `Model.Ops.OverrideDestination` (the repair routing channel)

```csharp
public enum OverrideDestination { Repair }                                       // OverrideDestination.cs:5
```

KVO key: `"ops.repair-dest"` (`OverrideDestinationExtensions.Key`, `:18`).

```csharp
public static bool HasOverrideDestination(this Car car, OverrideDestination);    // 25
public static bool TryGetOverrideDestination(this Car car, OverrideDestination, IOpsCarPositionResolver, out (OpsCarPosition,string)?); // 32
public static void SetOverrideDestination(this Car car, OverrideDestination, (OpsCarPosition,string)?); // 62
public static bool IsWriteAuthorized(this OverrideDestination, Car);             // 11
```

Stored as either a bare string (legacy, just the IC identifier) or a dict `{ id, tag }` where `tag` is e.g. `"overhaul"`. **Coexists with the regular `ops.waybill`** — `OpsControllerExtensions.TryGetDestinationInfo` (`:12`) checks override-destination first, then falls back to waybill. So a car can have both an active waybill and a repair override; the override wins for routing purposes but doesn't replace the underlying waybill.

`RepairTrack.CheckForCompletelyRepairedCars` (`RepairTrack.cs:178`) clears the override (`SetOverrideDestination(Repair, null)`) when work is done; the underlying waybill remains and the car proceeds to its real destination.

`OverrideDestination` is currently a single-value enum but designed to be extended.

### Patch candidates

| Method | Why patch |
|---|---|
| `OverrideDestinationExtensions.SetOverrideDestination` | Audit/log all repair-routing decisions. |
| `OverrideDestinationExtensions.Key` (extension) | Add new override types (won't compile cleanly without extending the enum — either add a new extension or use Harmony to add cases). |

### MP authority

`ops.repair-dest` is allowed at default level (`MinimumLevelTrainmaster`); see the `Car` auth resolver. UI sets it from the company window's "Send to Repair" button (see `BuilderExtensions.AddRepairDestination`).

---

## `Model.Ops.IndustryStorageHelper` (the storage KVO wrapper)

```csharp
public class IndustryStorageHelper : IPropertyAccessControlDelegate {            // 14
    public void  AddToStorage(Load, float quantity, float maxQuantity, string prefix); // 93
    public float RemoveFromStorage(Load, float quantity, string prefix=null);    // 110  returns actually-removed
    public void  SetStorage(Load, float quantity, string prefix=null);           // 135
    public float QuantityInStorage(Load, string prefix=null);                    // 147
    public IEnumerable<Load> Loads();                                            // 177
    public Dictionary<string,string> Warnings { get; private set; }              // 66
    public void  SetWarning(string key, string warning);                         // 212
    public bool  InterchangeDisabled { get; }                                    // 30
    public GameDateTime? InterchangeLastServiced { get; set; }                   // 32
    public GameDateTime? InterchangeExtraScheduled { get; set; }                 // 48
    public bool  CanScheduleExtra { get; }                                       // 64
    public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key); // 226
}
```

Storage is a single dict under KVO key `"storage"` on the Industry; entries keyed by `loadId` (or `prefix:loadId` if a component has `sharedStorage = false`). `RemoveFromStorage` clamps to 0 — calling it for more than is in storage just empties to 0 and returns less than asked. `AddToStorage` clamps to `maxQuantity`.

**Auth:** `extraScheduled` is `MinimumLevelTrainmaster`, **everything else (including `storage`) is HostOnly**. So clients can't directly mutate industry storage — must go through host-side Service or a custom request message.

---

## `Model.Ops.Order` and friends (the demand queue)

```csharp
public interface IOrder {                                                        // IOrder.cs
    CarTypeFilter   CarTypeFilter { get; }
    Load            Load          { get; }       // null = empty
    OpsCarPosition  Destination   { get; }
    int             CarCount      { get; set; }  // mutable; decremented as filled
    string          Tag           { get; }
    bool            NoPayment     { get; }
}

public struct Order : IOrder { … }                                               // Order.cs
public struct ReturnFromBardoOrder : IOrder { string CarId; … }                  // ReturnFromBardoOrder.cs
```

Orders live only on `Interchange.Orders` (NonSerialized list, host-only). They're created by:
- `OpsController.AddOrderForInboundCar` (`OpsController.cs:440`) — when an industry calls `OrderEmpty`/`OrderLoad`.
- `Interchange.OrderReturnFromBardo` (`Interchange.cs:208`) — `InterchangedIndustryLoader` re-importing parked cars.

Orders are coalesced by `Interchange.AddOrder` (`:193`): same `(Load, CarTypeFilter, Destination)` increments `CarCount` instead of adding new entry.

Realized at `IndustryContext.AddOrderedCars` → `TrainController.PlaceTrain`. Consumed orders are removed at end of service.

### `InterchangeSelector` (origin-routing)

`Model.Ops/InterchangeSelector.cs:8`. Internal class held on `OpsController._interchangeSelector`. Round-robins outbound cars across enabled interchanges via a `[0.5, 0.5, 0.3, 0.7, 0.6, 0.4, 0.7, 0.3]` interval cycle, weighted by destination capacity. **Stateful between calls** — moves across positions reset the counter. Patch the `InterchangeForPosition` method to override interchange selection.

---

## `IIndustryContext` / `IndustryContext` (the per-tick gateway)

```csharp
public interface IIndustryContext {                                              // IIndustryContext.cs
    float          DeltaTime    { get; }                  // game seconds since last Service call
    GameDateTime   Now          { get; }
    float          PortionOfDayUntilNextRegularService { get; }
    IEnumerable<IOpsCar> CarsAtPosition();

    // Outbound (car already at me, send it away)
    void OrderAwayEmpty(IOpsCar car, string orderTag=null, bool noPayment=false);
    void OrderAwayLoaded(IOpsCar car, string orderTag=null, bool noPayment=false);

    // Inbound (request more cars)
    bool OrderLoad (CarTypeFilter, Load,   string tag, bool noPayment, out float quantity);
    void OrderEmpty(CarTypeFilter, string tag, bool noPayment=false);

    // Counts (for capacity decisions)
    float QuantityOnOrder(Load load);
    int   NumberOfCarsOnOrder(Load load);
    int   NumberOfCarsOnOrderForTag(string tag);
    int   NumberOfCarsOnOrderEntireIndustry();
    int   NumberOfCarsOnOrderEmpties(CarTypeFilter);
    float AvailableCapacityInCars(CarTypeFilter, Load);

    // Storage
    void  AddToStorage(Load, float quantity, float maxQuantity);
    void  RemoveFromStorage(Load, float quantity);
    float QuantityInStorage(Load);

    // Realize orders (interchange uses)
    void AddOrderedCars(List<IOrder> orders, int maxToOrder);

    // Lifecycle
    void RemoveCar(IOpsCar car);                       // _trainController.RemoveCar(id)
    void MoveToBardo(IOpsCar car);                     // off-map parking

    // Money
    void PayWaybill(IOpsCar car, Waybill waybill);     // adjusts for tier/condition/timely
    void PayLoad(Load load, float quantity);           // pays load.payPerQuantity * quantity

    // Misc
    void RequestIndustriesOrderCars();
    GameDateTime GetDateTime(string key, GameDateTime defaultValue);
    void         SetDateTime(string key, GameDateTime);
    float CounterIncrement(string key, float value);
    float CounterClear(string key);
}
```

`IndustryContext` is a **`readonly struct`**, constructed per-call by `OpsControllerExtensions.CreateContext` (`OpsControllerExtensions.cs:76`). It holds references to `TrainController`, `OpsController`, `Industry`, `IndustryComponent`, and the industry's `IKeyValueObject`. The `_keyValueObject` it holds is the **Industry's** KVO — `GetDateTime`/`CounterIncrement`/`CounterClear` write into the Industry KVO with whatever key you pass. Components co-existing on one Industry share that namespace; prefix your keys.

### Patch candidates

`IndustryContext` is a struct so Harmony patches work but you can't subclass. The cleanest hook points are the `OpsController` methods it forwards to:

| Patch target | Effect |
|---|---|
| `OpsController.AddOrderForInboundCar` | Intercept `OrderEmpty`/`OrderLoad`. |
| `OpsController.AddOrderForOutboundEmptyCar` / `…Loaded…` | Intercept `OrderAwayEmpty`/`OrderAwayLoaded`. |
| `OpsController.QuantityOnOrder` / `CountOrdersMatching` / `AvailableCapacityInCars` | Modify capacity/order-counting accounting. |
| `IndustryStorageHelper.AddToStorage`/`RemoveFromStorage` | Intercept all storage mutations. Note auth check — patches must run host-side. |

---

## `Model.Ops.IOpsCar` and `OpsCarAdapter`

```csharp
public interface IOpsCar {                                                       // IOpsCar.cs
    string Id { get; }
    string CarType { get; }
    string DisplayName { get; }
    bool   IsOwnedByPlayer { get; }
    int    WeightInTons { get; }
    Waybill? Waybill { get; }
    PassengerMarker? PassengerMarker { get; set; }
    float  Condition { get; }
    bool   IsEmptyOrContains(Load load);
    (float quantity, float capacity) QuantityOfLoad(Load load);
    float  Unload(Load load, float quantityToConsume);
    float  Load  (Load load, float quantityToLoad);
    bool   IsFull(Load load);
    void   SetWaybill(Waybill? waybill, IndustryComponent setter, string reason);
    bool   GetOverrideDestination(OverrideDestination, out OpsCarPosition, out string tag);
}

public readonly struct OpsCarAdapter(Car car, IOpsCarPositionResolver resolver) : IOpsCar  // OpsCarAdapter.cs
```

The adapter is the only production implementation; `MockCar` exists for tests. `OpsCarAdapter` adapts a `Car` MonoBehaviour to the `IOpsCar` interface and routes `SetWaybill` through `Car.SetWaybillAuto` (so `null` falls back to auto-destination). `Load`/`Unload` write into `LoadSlots[i]` via `Car.SetLoadInfo` directly.

### Patch candidates

| Method | Why patch |
|---|---|
| `OpsCarAdapter.Load` / `OpsCarAdapter.Unload` | Final chokepoint for industry cargo manipulation; cleaner than patching `Car.SetLoadInfo` (which has many call sites). |
| `OpsCarAdapter.SetWaybill` | Per-component waybill writes — also useful for logging "which industry sent this car where". |

---

## `Game.State.Ledger` (the money log)

```csharp
public enum Category { Bank, Freight, Passenger, Fuel, Loan, Equipment, WagesRepair, Progression, WagesAI, RepairSupplies };
public struct Entry { GameDateTime Date; int Amount; Category; EntityReference? Payee; string Memo; int Count; }

public void Record(int amount, Category, EntityReference? payee, string memo, int count, GameDateTime now);  // 69
public IReadOnlyList<Entry> EntriesBetween(GameDateTime start, end, out int startBalance, out int endBalance); // 82
```

`Record` calls `StateManager.AssertIsHost()` and emits `Messenger.Send<Ledger.ChangedEvent>`. The whole ledger is in-memory; serialized via `PopulateForSave` / `Load`.

### `StateManager.ApplyToBalance` (the public entry)

```csharp
public void ApplyToBalance(int amount, Ledger.Category category, EntityReference? payee,
                           string memo = null, int count = 0, bool quiet = false)         // StateManager.cs:1265
```

- Always calls `AssertIsHost`.
- Records to `Ledger`, mutates `Balance`, fires `BalanceDidChange`.
- For `Freight` / `Passenger` categories, plays a delayed audio notification (`stamp` / `punch` sound).
- `quiet:true` suppresses the broadcast `"Received payment of $N"` chat message but still records.

### `LedgerRequest` / `LedgerResponse` (client viewing)

```csharp
[MinimumAccessLevel(AccessLevel.Passenger)]
public struct LedgerRequest(float start, float end) : IGameMessage                          // LedgerRequest.cs
public struct LedgerResponse … (returns serialized entries between start/end)              // LedgerResponse.cs
```

Anyone (Passenger access and up) can request to see the ledger. Response handled by `Game.Events.LedgerRequestResponseReceived`.

### Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.ApplyToBalance` | Single chokepoint for all economy changes. Patch prefix to veto, postfix to log. |
| `Ledger.Record` | All ledger entries flow here. |
| `StateManager.CanAfford` | Sandbox always returns true; patch to add credit limits. |

### MP authority

- `ApplyToBalance` and `Ledger.Record` AssertIsHost.
- `Balance` itself is on `_storage` KVO (HostOnly); clients see updates via observation.
- Clients pull ledger history via `LedgerRequest` (Passenger access).

---

## `Model.Ops.Definition.Load` (the cargo schema)

```csharp
[CreateAssetMenu] public class Load : ScriptableObject {                         // Load.cs:8
    public string description;
    public LoadUnits units;             // Pounds | Gallons | Quantity
    public float density = 62.4f;       // lb/ft^3
    public float unitWeightInPounds;    // for Quantity-typed loads
    public bool  importable = true;     // false ⇒ raw materials only sourced on-network
    public float payPerQuantity;        // for non-importable: industry pays per unit on delivery
    public float costPerUnit;           // for orderable loads: cost per unit charged to the company

    public string id => base.name;
    public float NominalQuantityPerCarLoad =>     // Pounds=100000, Gallons=8000, Quantity=3
    public float ZeroThreshold =>                  // tiny epsilon for the unit type
    public float Pounds(float quantity) =>         // converts to lb
    public string QuantityString(float quantity);
}
```

`Load` SOs are loaded via `CarPrototypeLibrary.instance.LoadForId` and enumerated as `CarPrototypeLibrary.instance.opsLoads`. Adding new loads requires registering in the library — out of scope here.

### MP authority / Patch candidates

`Load`s are SOs — singletons in memory, no MP propagation needed (loaded identically on host and client). To add a load, register it via `CarPrototypeLibrary` (see `RollingStock.LoadModels`). To override an existing load's curves/thresholds, mutate the SO at runtime in a `[StateRequiredOnLoad]` MonoBehaviour or via `[HarmonyPatch]` of the relevant getter.

---

## Industry-to-track binding

`IndustryComponent.trackSpans : TrackSpan[]` is the binding. Each `TrackSpan` (in `Track/TrackSpan.cs`) is a half-open range on the track graph (`Location lower`, `Location upper`). The implicit `IndustryComponent → OpsCarPosition` conversion (`IndustryComponent.cs:208`) wraps these spans plus the IC's `Identifier`.

There is **no separate "spur" type**. An industry attaches to track via the `TrackSpan` array set in the Unity scene — the prefab-author drags spans into the IC. `OpsController.CarsAtPosition` (`:758`) iterates `position.Spans` and calls `TrainController.GetCarsOnSpan` for each.

`TeleportLoadingIndustry` is the exception — it has `inputSpans` and `outputSpans` separately, plus the inherited `trackSpans` (which it ignores).

### Map-mod consequence

- **Industries do not register themselves** — `OpsController.RebuildCollections` (`:292`) just calls `GetComponentsInChildren<Industry>()` on its own transform. Map mods drop new `Industry`+`IndustryComponent` GameObjects under the OpsController's transform, then fire `IndustriesDidChange` (or just rely on the next `RebuildCollections` call).
- The `_carPositionLookup` is keyed by the IC's `Identifier`. Map mod authors must ensure unique `subIdentifier`s within an industry (and unique industry `identifier`s).
- See `map-mods-vanilla-survey.md` for the broader map-mod injection pattern.

---

## Loading / unloading actions (cars side)

The crib sheet for cars/cargo isn't yet written, but the load surface is:

```csharp
// Read
public CarLoadInfo? GetLoadInfo(this Car, int slot);                             // CarExtensions.cs:22
public CarLoadInfo? GetLoadInfo(this Car, string loadId, out int slotIndex);     // 33
public bool         IsLoadEmpty(this Car);                                       // 55
public (float quantity, float capacity) QuantityCapacityOfLoad(this Car, Load);  // 68

// Write  (host KVO; key "load.{slot}")
public void SetLoadInfo(this Car, int slot, CarLoadInfo? info);                  // 27
public string KeyForLoadInfoSlot(int slot);                                      // 120

// Industry-side adapter (in OpsCarAdapter)
public float Load   (Load load, float quantityToLoad);                           // OpsCarAdapter.cs:113
public float Unload (Load load, float quantityToUnload);                         // 79
```

`load.{slot}` keys have **no `_` prefix** — they're write-allowed at default `MinimumLevelTrainmaster` per `Car.AuthorizationRequirementForPropertyWrite`. So a Trainmaster client can in principle directly mutate cargo. In practice all writes happen host-side via `OpsCarAdapter.Load`/`Unload` from `IndustryComponent.Service`.

`OpsController.CheckLoads` (`OpsController.cs:246`) runs at `PostRestoreProperties`: for any car carrying a load that its current waybill destination doesn't accept (`AcceptsCarsWithLoad`), waybills it back to an interchange. This is the "fix corrupt save" sweep.

---

## Scenario / job system

Vanilla Railroader has **two** systems beyond ad-hoc ordering:

1. **Contracts** (per-industry, ongoing). See `Industry.usesContract` + `Contract` struct + `ContractExtensions`. The "tier 1..5" ramp; player negotiates tier via `ModifyContract` message; daily rollover may demote on poor performance. Contracts gate `Industry.GetContractMultiplier()` which multiplies productionRate / unloadRate / etc.

2. **Progression** (campaign-mode delivery quests). See `ProgressionIndustryComponent` and the `Game.Progression.Section` system (`Game.Progression/Section.cs`, `Progression.cs`). Sections define `DeliveryPhase` arrays of `Section.Delivery { Load load, int count, CarTypeFilter, Direction direction }`. When all phases complete, `_onComplete` is called which advances progression state and calls `Messenger.Send<IndustriesDidChange>` (rebuilds OpsController collections). Out of scope for this sheet beyond noting the integration point.

There is **no scenario / job dispatch / order book** beyond these two.

---

## MP authority summary

| Action | Who | Mechanism |
|---|---|---|
| Industry tick (Service/CheckForCompleted/OrderCars) | Host only | `Industry.TickCoroutine` guarded by `IsHost` |
| Daily rollover (DayDidChange) | Host only | `OpsController.DayDidChange` guarded by `IsHost` |
| Interchange service tick | Host only | `OpsController.CheckServiceInterchanges` guarded by `IsHost` |
| Contract tier change | Officer-level client | `ModifyContract` message → `Industry.ModifyContract` (host AssertIsHost) |
| Repair multiplier | Trainmaster client | `SetRepairMultiplier` message → `RepairTrack.HandleSetMultiplier` (host AssertIsHost) |
| Schedule extra interchange service | Trainmaster client | KVO write on `extraScheduled` (auth = MinimumLevelTrainmaster in `IndustryStorageHelper`) |
| Sweep cars to destinations | Trainmaster client | `RequestOps` message |
| View ledger | Passenger client | `LedgerRequest` message |
| Set waybill | Trainmaster + train-crew | `Car.KeyValueObject["ops.waybill"]` write (default auth + crew check) |
| Set autodest (load/empty) | Trainmaster + train-crew | `ops.autodest.*` KVO writes |
| Set repair override | Trainmaster | `ops.repair-dest` KVO write |
| Direct industry storage write | **No client path** | `storage` KVO is HostOnly — must run server code |
| Pay/charge balance | **No client path** | `StateManager.ApplyToBalance` AssertIsHost |
| Add/remove a car | Host only | `TrainController.RemoveCar` / `PlaceTrain` |

---

## Patch points for custom mods

### Custom industry component

1. Subclass `IndustryComponent` in your DLL.
2. Implement `Service(IIndustryContext)` and `OrderCars(IIndustryContext)`.
3. Add the script to a child of an `Industry` GameObject (map-mod scene, or runtime via Harmony patch on `OpsController.RebuildCollections`).
4. Set `subIdentifier` (unique within industry), `trackSpans`, `carTypeFilter` on the IC.
5. To use ScriptableObject-backed config, define a SO and assign in the scene.

You get `BuildPanel`/`PanelFields` for free in the company window. `EnsureConsistency` is called once per interchange-service tick — use it for migration.

### Custom waybill semantics

- Patch `IndustryComponent.OnCompleteWaybill` to alter completion criteria and payment behavior.
- Patch `OpsController.WaybillCarToInterchange` to override return-routing.
- Patch `Waybill.ConditionFineForCarCondition` to change the damage→fine schedule.
- Patch `Contract.TimelyDeliveryBonus` for custom on-time bonus curves.
- Add new tags by setting `Waybill.Tag` to a string your mod recognizes; check it in your custom IC's `OnCompleteWaybill` override.

### Custom economy hooks

- Patch `StateManager.ApplyToBalance` for veto/audit of all payments.
- Patch `OpsController.PaymentForMove` for custom freight rate formulas.
- Patch `Industry.PenaltyForChange` (extension method — patches require `[HarmonyPatch(typeof(ContractExtensions), nameof(ContractExtensions.PenaltyForChange))]`).
- Add a custom `Ledger.Category`? You can't — enum is fixed. But `EntityReference?` payee + `string memo` give plenty of metadata room.
- For new request messages, copy the `SetRepairMultiplier` template (struct + `[MinimumAccessLevel]` + handler in IC).

### Custom ordering

- Patch `IndustryLoaderBase.OrderCars` / `IndustryUnloader.OrderCars` to override car-ordering logic.
- Or replace at the IC level by subclassing.
- `Order` and `IOrder` are public; you can construct your own and call `Interchange.AddOrder` directly (host-side).

### Custom interchange behavior

- Patch `Interchange.ServeInterchange` for full-flow control.
- Patch `IndustryContext.AddOrderedCars` (struct method — Harmony works) to override car-placement strategy.
- Patch `IndustryContext.CreateCarDescriptorForOrder` (private) to override car-spawning details (reporting marks, initial oil, pre-loaded cargo).

---

## Gotchas

- **`FindExistingCarForInboundOrder` always returns null** (`OpsController.cs:473`). Looks like a stub for "match existing cars to inbound orders before spawning new ones" but never implemented. Every inbound `OrderLoad`/`OrderEmpty` therefore creates a fresh interchange order — there's no recycling of in-game cars to satisfy demand.
- **`Industry.TickCoroutine` jitter is 0–15 seconds.** Two industries created at the same time will tick at slightly different phases.
- **`ServiceMetersFromActual` runs every tick regardless of `WearFeature`.** See [Wear › Gotchas](wear-durability.md#gotchas). Industries that read `Car.OdometerService` (currently only `RepairTrack` via `RepairCap`) inherit this behavior.
- **`IndustryContext.DeltaTime` is in game seconds, not real seconds.** It's the `serviceInterval` passed to `Industry.Tick` (15f from the coroutine, or whatever `TickAll` passes for batch). `RateToValue(rate, dt)` does the per-day → per-tick conversion.
- **`RateToValue` returns 0 if `TimeMultiplier < 0.001f`.** Pausing the game halts all ops production. Toggling time-multiplier-zero won't catch up missed work.
- **`Interchange.Service` and `Interchange.OrderCars` are no-ops.** The interchange's actual work happens in `ServeInterchange` driven externally by `OpsController.CheckServiceInterchanges`. Don't try to override `Service` on `Interchange` and expect interchange behavior.
- **`Industry.Components` is cached** (`Industry.cs:40`). Adding a new IC at runtime won't show up unless you invalidate `_cachedComponents` — currently no public hook for this. Workaround: use `GetComponentsInChildren<IndustryComponent>` directly.
- **`OpsCarPosition.Equals` compares `Identifier` AND `Spans` reference equality** (`OpsCarPosition.cs:21`). Two `OpsCarPosition`s with same identifier but different span array instances are NOT equal. Always pass the same `OpsCarPosition` object you got from `ResolveOpsCarPosition` / the implicit IC conversion.
- **`EnumerateCars(ctx)` filters out moving cars** (`IndustryContext.CarsAtPosition`, line 86: `Mathf.Abs(item.velocity) > 0.05f`). Cars must be ≤ 5 cm/s to count as "at the position." So a car coasting through doesn't get loaded.
- **Auto-destination tagged waybills (`tag == "autodest"`) have payment 0.** Cycling through autodest doesn't earn money — you only get paid on real waybills with `PaymentOnArrival > 0` set during interchange spawn.
- **`Sweep` doesn't pay.** It only moves cars to their destinations; the IC's `CheckForCompleted` runs on the next tick and pays normally.
- **`OverrideDestination.Repair` and the regular waybill coexist on the same car.** Don't assume one or the other; use `TryGetDestinationInfo` (`OpsControllerExtensions.cs:12`) which checks override first.
- **`PayWaybill` only fires for `PaymentOnArrival > 0`.** Zero-payment waybills (autodest, progression, "sell") are completed silently.
- **`Industry.OrderCars()` is called from `OpsController.RequestIndustriesOrderCars` at every interchange service** — *all* enabled industries order cars when *any* interchange ticks. Industries are area-shuffled per `InterchangeShuffle` setting before iterating.
- **`InterchangeSelector` is stateful and shared across all calls.** Two consecutive `WaybillCarToInterchange` calls for the same position get the same interchange (the cached one); changing position resets and may pick a different interchange.
- **`Industry.TryGetStorageCapacity(Load)`** (`Industry.cs:475`) returns `IndustryUnloader.maxStorage * GetContractMultiplier()` for unloaders but `IndustryLoaderBase.maxStorage` (no multiplier) for loaders. Inconsistency: loaders' effective max storage IS multiplied at consumption time but not reported here.
- **`InterchangedIndustryLoader.Service` is a no-op.** All work happens in `ServeInterchange` driven by the parent `Interchange`. Don't expect to add behavior by overriding `Service`.
- **`/ops sweep`, `/ops list`, `/ops setTier`, `/ops findWaybills`, `/ops passOffset`, `/ops passWaiting`, `/ops passStops`** are the live console commands (`OpsCommand.cs`). `setTier` is sandbox-only.
- **`Waybill.Origin` can be null.** It's set on cars spawned from interchange (origin = the interchange IC) but can be null for player-owned-only cars or cars that came from auto-destination cycling. Some payment paths assume non-null — use `?.` carefully.
- **`Interchange.Disabled` reflects `Industry.Storage.InterchangeDisabled`** (`Interchange.cs:30`) — toggle is per-Industry, not per-Interchange. An Industry with multiple Interchanges (rare) would share the toggle.
- **`OpsController.PostRestoreProperties` must be called externally** (`OpsController.cs:138`) — it's invoked by the state manager during load. Adds `CheckLoads`/`CheckWaybills`/`RebuildPopulations`/`CheckServiceInterchanges`/`CheckOneEnabledInterchange` passes. If your mod loads industries late, call this manually to re-validate.
- **`Industry.Awake` adds the KVO via `gameObject.AddComponent<KeyValueObject>()`** but does not call `RegisterPropertyObject`. Registration happens in the `IndustryStorageHelper` constructor (`IndustryStorageHelper.cs:78`) which is called from `Industry.Awake`. So the KVO is registered before any setter runs — fine for normal use, but if you sneak in via `MockSetKeyValueObject` (`Industry.cs:160`) you bypass registration.

---

## Cross-references

### To Wear & Durability

- **`RepairTrack`**: full per-method coverage at [Wear › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair). This sheet only summarizes the IC plumbing.
- **`OverrideDestination.Repair`**: see [Wear › RepairTrack patch candidates](wear-durability.md#patch-candidates) — `RepairTrack` clears the override on completion via `SetOverrideDestination(Repair, null)`.
- **Per-tick wear and odometer**: `Car.BankOdometer` runs in the physics tick, independent of industry ticks. Industries see `Car.Condition` / `Car.OdometerService` as KVO snapshots. See [Wear › per-tick wear loop](wear-durability.md#per-tick-wear-loop).
- **`Waybill.ConditionFineForCarCondition`** (this sheet, `Waybill.cs:75`) — the only ops-side consumer of `Car.Condition`. A car arriving damaged is paid less; see formula above.

### To Couplers

- Cargo loading does NOT couple/uncouple cars (the `TeleportLoadingIndustry` exception manually moves cars and reconnects via `TrainController.ConnectCars`). Normal load/unload only mutates `load.{slot}` KVO.
- `Sweep` (`OpsController.cs:837`) calls `TrainController.MoveCarCoupleTo` if the open-space search finds an adjacent car — couples on landing. This is the only ops path that creates new couplings.
- See [Couplers › auto-couple](couplers.md#auto-couple-impact-driven) for the impact-driven path used when `MoveCarCoupleTo` brings cars together.

### To Cars & Cargo (not yet written)

- `CarLoadInfo`, `LoadSlot`, `CarDefinition.LoadSlots` are the per-car schema. The cars-cargo crib sheet (when written) should cover `CarExtensions.GetLoadInfo`/`SetLoadInfo`, the `load.{slot}` KVO key naming, and the `LoadSlot.LoadRequirementsMatch`/`MaximumCapacity` constraints. This sheet uses but doesn't document them in depth.
- `OpsCarAdapter.Load`/`Unload` is the ops-side wrapper; the lower-level `Car.SetLoadInfo` is the actual KVO write.

### To Signals & Dispatch (not yet written)

- Sweep doesn't interact with signals; it teleports.
- `RequestIndustriesOrderCars` doesn't dispatch trains, only spawns rolling stock at interchanges. No AI-engineer integration in vanilla ops — dispatch is the player's problem.
- `TimetableController` references appear in `CarExtensions.TryGetTimetableTrain` etc. but are out of scope here. The dispatch crib sheet (when written) should cross-link to this one for industry context.

### To Multiplayer survey (`../multiplayer-vanilla-survey.md`)

- Auth model: `IPropertyAccessControlDelegate` and the `Car.HostPrefixes` arrays are documented in the multiplayer survey. Industries follow the same pattern via `IndustryStorageHelper`.
- Existing message catalog (multiplayer survey § "Message types") covers `SetRepairMultiplier`, `ModifyContract`, `RequestOps`, `LedgerRequest`. To add a new ops-related request, follow that section's template.
- "HostOnly" definition: `_` prefix on KVO keys → host-only writes. Industry storage and most ops keys follow this convention.

### To Map mods survey (`../map-mods-vanilla-survey.md`)

- Map mods are the vehicle for adding new `Industry` GameObjects. See "What does a map mod do?" section there.
- Custom IC subclasses must ship in a DLL referenced by the map manifest.
- The "no map-mod extension API" finding from that survey applies here: mods inject by dropping GameObjects, not by registering with the OpsController.
