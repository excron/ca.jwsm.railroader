# Industry Tick Pipeline — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Industries & Ops](industries-ops.md), [Wear & Durability](wear-durability.md), [Cars & Cargo](cars-cargo.md), [Daily Reports](daily-reports.md), [Reputation](reputation.md), [Passengers & Timetable](passengers-timetable.md), [Time & Weather](time-weather.md), [Tile Loading & Bardo](tile-loading-bardo.md), [Save / Load](save-load.md)

The "industry tick" is Railroader's only authoritative simulation cadence for the ops layer. Every produce-output / consume-input / load-car / unload-car / bill-wages / accumulate-wear-via-shop action flows through one of three timed pumps: a per-`Industry` 5-second `TickCoroutine` (the steady-state pump), a midnight `TimeDayDidChange` cascade (the daily-rollover pump), and a per-game-minute `TimeMinuteDidChange` interchange-service check. All three are host-only and delivered exclusively through `IndustryComponent.Service` / `CheckForCompleted` / `OrderCars` / `DailyReceivables` / `DailyPayables`. Everything else mods see — KVO mutations on the Industry, the Car's `_condition`, ledger entries, contract demotions — is downstream of these calls. This sheet documents the pump in surgical detail because almost every economy-bending mod (custom industries, custom payment formulas, custom rollover policies, mod-driven wear/oiling) lives or dies on its understanding of when `Service` gets called, what `IndustryContext` it gets, and what bypasses exist.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Industry.TickCoroutine()` (private) | `Model.Ops/Industry.cs:174` | The steady-state pump. 0–15s startup jitter, then `WaitForSeconds(5)` × 3 between Service calls. Host-only. |
| `Industry.Tick(float, bool)` (private) | `Model.Ops/Industry.cs:205` | Inner per-tick body. Walks Components, calls `CheckForCompleted` always + `Service` when `shouldService`. **`ProgressionDisabled` early-exits the entire industry.** |
| `Industry.TickAll(float dt)` (public static) | `Model.Ops/Industry.cs:238` | Time-fast-forward path. Used by `StateManager.WaitTimeCoroutine` only; **bypasses the every-3rd-tick gate (always services)**. |
| `Industry.EnumerateComponentContexts(float)` (public) | `Model.Ops/Industry.cs:228` | Iterator that constructs a fresh `IndustryContext` per Component for the current tick. |
| `IndustryComponent.Service(IIndustryContext)` (abstract) | `Model.Ops/IndustryComponent.cs:160` | The extension point. Called once per service tick per Component. |
| `IndustryComponent.CheckForCompleted(IIndustryContext)` (virtual) | `Model.Ops/IndustryComponent.cs:139` | Called every tick (3× more often than `Service`). Default impl pays/closes any waybill that arrived. |
| `IndustryContext` (`readonly struct`) | `Model.Ops/IndustryContext.cs:20` | The per-tick gateway. Holds `dt` (in **game seconds**), `Now`, references to `OpsController`/`Industry`/`IC`, and the **Industry's** KVO. |
| `IndustryComponent.RateToValue(float, float)` (protected static) | `Model.Ops/IndustryComponent.cs:128` | Per-day-rate × dt → per-tick units. Returns 0 if `TimeMultiplier < 0.001`. |
| `OpsController.DayDidChange(TimeDayDidChange)` (private) | `Model.Ops/OpsController.cs:163` | The daily-rollover pump. Runs `UpdatePerformance → DailyReceivables → RollToNextContract → DailyPayables` on every `Industry`. Host-only. |
| `OpsController.CheckServiceInterchanges()` (private) | `Model.Ops/OpsController.cs:1032` | Per-game-minute interchange-service pump. Drives `RequestIndustriesOrderCars` + `Interchange.ServeInterchange`. Host-only. |
| `TimeObserver.ObserveTime` (private) | `Game.State/TimeObserver.cs:34` | 1-real-second polling coroutine that emits `TimeDayDidChange`/`TimeHourDidChange`/`TimeMinuteDidChange`. Drives day-rollover and per-minute pumps. |
| `PassengerStop.Loop()` (private) | `Model.Ops/PassengerStop.cs:301` | The **alternate** tick loop for the passenger system. Uses `WaitForSeconds(3)` (real seconds), independent of `Industry.TickCoroutine`. |

---

## Spine: every event that ends with `Service` being called

```
HOST ONLY (every guard below is `if (!StateManager.IsHost) return;` or equivalent)

(1) Steady-state per-Industry pump — drives 99% of vanilla ops work
    Industry.OnEnableWithProperties()              ← post-RestoreNotifier (KVO restored first)
        StartCoroutine(TickCoroutine())
            yield WaitForSeconds(Random.Range(0,15))   ← startup jitter, per-Industry
            InitializeIfNeeded()                        ← KVO key "init" version-keyed migration; calls Component.Initialize for each
            loop:
                yield WaitForSeconds(5)                 ← REAL seconds. Not affected by TimeMultiplier.
                shouldService = (tickCount % 3 == 0)
                Tick(serviceInterval=15f, shouldService)
                    if (ProgressionDisabled) return;    ← whole-industry gate
                    foreach Component, fresh ctx:
                        try {
                            Component.CheckForCompleted(ctx)        ← always
                            if shouldService && !Component.ProgressionDisabled:
                                Component.Service(ctx)              ← every 3rd tick
                        } catch (Exception e) { Log.Error(...); }   ← per-component, swallowed
                tickCount++
                ↑ NOTE: dt passed is fixed 15f (game seconds), not tick-elapsed wallclock.
                  TickCoroutine fires roughly every 15s real time at 1× game speed,
                  but the dt argument is a constant. RateToValue uses dt and
                  TimeWeather.TimeMultiplier together to convert per-day rates.

(2) Time-fast-forward pump — only used by /wait
    StateManager.WaitTimeCoroutine                  ← spawned by SetTimeOfDay+WaitTime message
        loop until remaining hours done:
            num = min(3600s game, remaining)        ← 1 game-hour chunks
            Industry.TickAll(num / TimeMultiplier)
                while dt > 0:
                    chunk = min(GameTimeHoursToDeltaTime(4f)=14400s, dt)   ← 4-game-hour shards
                    foreach Industry: Tick(chunk, shouldService:true)       ← ALWAYS service
                    dt -= chunk
            ApplyLocal(new SetTimeOfDay(...))       ← advances the clock at end of chunk
            yield WaitForSeconds(0.25)              ← real time

(3) Daily-rollover pump — fires once per game day at midnight
    TimeObserver (1 Hz real time, drives off TimeWeather.Now polling):
        if (now.Day != _lastTime.Day):
            Messenger.Send(default(TimeDayDidChange))
                ├── OpsController.DayDidChange(_):                              ← Model.Ops
                │       if (!IsHost) return;
                │       now = TimeWeather.Now.WithHours(0f)                    ← snap to midnight
                │       UpdatePerformance(now)                                  ← walks open waybills, scores per-industry, writes _perfHist
                │       foreach Industry: DailyReceivables(now)                 ← e.g. IndustryUnloader.PayLoad(unloaded-total counter)
                │       foreach Industry: RollToNextContract()                  ← demote-on-failing, apply tier-change penalty, broadcast
                │       foreach Industry: DailyPayables(now)                    ← e.g. RepairTrack pays shop wages
                ├── StateManager.OnDayDidChange(_):                             ← Game.State
                │       PayAutoEngineerWages()                                  ← deducts WagesAI from balance
                └── Multiplayer subscribers (none in vanilla beyond above)

(4) Per-minute interchange pump
    TimeObserver: if (FloorToInt(now.Minutes) != FloorToInt(_lastTime.Minutes)):
        Messenger.Send(default(TimeMinuteDidChange))
            └── OpsController.CheckServiceInterchanges():
                    if (!IsHost) return;
                    list = AllInterchanges where GetNextServiceTime <= now
                    if (list.Empty) return;
                    EnsureConsistency()                                         ← every Component on every Industry: try Component.EnsureConsistency()
                    foreach in list: Interchange.PrepareToService()             ← clears Orders list
                    RequestIndustriesOrderCars()                                ← every enabled Industry.OrderCars() in shuffled order
                    foreach in list: Interchange.ServeInterchange(ctx)          ← removes completed inbounds, places new cars
                    [for partial-served: ScheduleExtra at NextAvailableServiceTime]

(5) Per-game-hour pump (DailyReportGenerator only consumer in vanilla)
    DailyReportGenerator.TickCoroutine:
        loop:
            GenerateIfItsTime()                                                 ← TimeForDailyEvent(last, 18) gate at 18:00
                if matches: GenerateReport(now.WithHours(18))
            yield TimeWeather.WaitForNextHour()                                 ← coroutine that polls .Hours every 5s game

(6) Real-time payment-coalescer pump (no Service calls; flush only)
    OpsController.PeriodicUpdate:
        loop: yield WaitForSecondsRealtime(0.25); AnnounceCoalescedPayments()
        ↑ Real-time. Not paused when game is paused (TimeMultiplier=0).
```

### Tick cadence summary table

| Pump | Cadence | Time domain | Calls Service? | Gate |
|---|---|---|---|---|
| Per-Industry steady state | 5s real × 3 = 15s real per Service | Real seconds | Yes (every 3rd) | `IsHost`, `!ProgressionDisabled` (industry), `!ProgressionDisabled` (component) |
| `WaitTime` fast-forward | 4 game-hour shards / 0.25s real yield | Game seconds (chunked) | **Yes, every tick** | `IsHost` only |
| Daily rollover | Once per game-day midnight | Game day-change edge | No (`DailyReceivables`/`DailyPayables` instead) | `IsHost` |
| Per-minute interchange | Each game-minute edge | Game-minute edge | No (`OrderCars` + `EnsureConsistency` instead) | `IsHost`, only Interchanges whose `GetNextServiceTime ≤ now` |
| Daily report | Polled hourly, fires at 18:00 game | Game hour edge | No | `IsHost` |
| Reputation | `WaitForNextDay` (5s game poll) | Game day edge | No | `IsHost` |
| Passenger work loop | Every 3 real seconds | **Real seconds (oddity)** | No (separate model) | `IsHost` |
| Passenger growth | Every 300 game seconds (inside `Loop`) | Game seconds | No | `IsHost` |
| Coalesced payment flush | Every 0.25 real seconds | Real seconds, **unpaused** | No | None |

---

## `Model.Ops.Industry.TickCoroutine` (the steady-state pump)

The single most patched-around method in the ops layer. Verbatim:

```csharp
private IEnumerator TickCoroutine() {                              // Industry.cs:174
    yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 15f));   // jitter
    InitializeIfNeeded();
    int tickCount = 0;
    while (true) {
        yield return new WaitForSeconds(5f);
        bool shouldService = tickCount % 3 == 0;
        Tick(15f, shouldService);
        tickCount++;
    }
}
```

### Startup jitter

`Random.Range(0f, 15f)` is a **uniform real-time** delay. With N industries spawned simultaneously (game start, mod load), service calls spread across 0–15 real seconds. **No correlation with industry identity** — different runs of the same save have different jitter values. This means:

- **You cannot rely on per-tick determinism** for cross-industry timing in a single save.
- Mods that need synchronized industry behavior must drive their own coroutine off `TimeWeather.Now` rather than the per-Industry tick.
- `tickCount % 3 == 0` is calibrated so the **first** tick (count=0) services. So a fresh industry starts producing immediately after `WaitForSeconds(5)` post-jitter — average 12.5s after `OnEnable`, max 20s.

### `dt` is a constant 15f, not a measured wallclock

```csharp
Tick(15f, shouldService);        // <-- always 15f
```

`IndustryContext.DeltaTime` therefore lies. If the player un-pauses for 30 seconds, every industry's next Service call still gets `dt=15`, not 30. Multiple consecutive ticks at 1× speed average out, but at higher `TimeMultiplier`s the math becomes:

```
RateToValue(rate, dt=15) = (15 / (86400 / TimeMultiplier)) * rate
                         = (15 * TimeMultiplier / 86400) * rate
```

So a `productionRate` of 1 unit/day at 1× = 0.000174 units per Service tick (per 15s real). At 4× game speed, the *clock* advances 60s per real 15s, but the per-tick output goes up to 0.000694 — the math correctly accounts for accelerated time **as long as the player doesn't change speed mid-tick** and **as long as the per-Industry coroutine actually runs**.

### `RateToValue` — the per-day → per-tick converter

```csharp
protected static float RateToValue(float rate, float dt) {        // IndustryComponent.cs:128
    float timeMultiplier = TimeWeather.TimeMultiplier;
    if (timeMultiplier < 0.001f) return 0f;                        // ← pause gate
    float num = 86400f / timeMultiplier;                           // real-seconds per game-day
    return dt / num * rate;                                        // = (dt * timeMultiplier / 86400) * rate
}
```

Pausing (`TimeMultiplier=0`) returns 0 from `RateToValue` — so production halts cleanly. **But** `Service` itself still runs (the coroutine uses real-time `WaitForSeconds`), and `CheckForCompleted` still pays out arriving cars. **Pause does not freeze the ops loop, only the rate-driven outputs.** Loaded cars at the spot will still be paid for when paused.

### `Tick` body and exception swallowing

```csharp
private void Tick(float serviceInterval, bool shouldService) {     // Industry.cs:205
    if (ProgressionDisabled) return;
    foreach (var (component, ctx) in EnumerateComponentContexts(serviceInterval)) {
        try {
            component.CheckForCompleted(ctx);
            if (shouldService && !component.ProgressionDisabled)
                component.Service(ctx);
        } catch (Exception exception) {
            Log.Error(exception, "Exception during tick {industry} {component}", this, component);
        }
    }
}
```

**Per-Component try/catch** — a Service throw on Component A does not stop Component B from being serviced. But a throw inside `EnumerateComponentContexts` (e.g., NRE constructing an `IndustryContext`) bubbles out and skips the rest of the industry for that tick.

The `ProgressionDisabled` gate has two levels: industry-wide (early return, skips even `CheckForCompleted`) and per-component (skips only `Service`, still does `CheckForCompleted`). **Asymmetric on purpose** — a progression-disabled component can still complete in-flight waybills it received before being disabled.

### `EnumerateComponentContexts` — context allocation

```csharp
public IEnumerable<(IndustryComponent, IndustryContext)> EnumerateComponentContexts(float dt) {
    GameDateTime now = TimeWeather.Now;
    foreach (IndustryComponent component in Components) {
        IndustryContext item = component.CreateContext(now, dt);
        yield return (component, item);
    }
}
```

`now` is captured once at iteration start. **All components in one industry-tick see the same `Now`.** This matters for tags like `Waybill.Created` (set inside `OnCompleteWaybill` via `TimeWeather.Now`, *not* `ctx.Now`) — there's a sub-tick mismatch: `Waybill.Created` may be slightly newer than the `ctx.Now` of the same tick because `Now` is re-read inside `OnCompleteWaybill` via `TimeWeather.Now.TotalHours - waybill.Created.TotalHours` (`IndustryComponent.cs:177`). Negligible at normal speed, can manifest as 1-frame skew in unit tests.

`Components` is **cached** as `_cachedComponents` on first access (`Industry.cs:40`). Adding a new IC at runtime won't be picked up unless you forcibly null `_cachedComponents` via reflection, or call `GetComponentsInChildren<IndustryComponent>` directly.

---

## `Model.Ops.Industry.TickAll` — the fast-forward bypass

```csharp
public static void TickAll(float dt) {                             // Industry.cs:238
    Industry[] array = UnityEngine.Object.FindObjectsOfType<Industry>();
    float a = GameTimeHoursToDeltaTime(4f);     // 14400f game seconds
    float num = dt;
    while (num > 0f) {
        float num2 = Mathf.Min(a, num);
        num -= num2;
        Industry[] array2 = array;
        for (int i = 0; i < array2.Length; i++) {
            array2[i].Tick(num2, shouldService: true);
        }
    }
}

public static float GameTimeHoursToDeltaTime(float hours) {        // Industry.cs:255
    return hours * 60f * 60f;                                      // hours → seconds
}
```

**Three critical differences from steady-state:**

1. **Always services.** `shouldService: true` on every chunk. Bypasses the `tickCount % 3 == 0` gate.
2. **`dt` is real game time** (in seconds, capped at 14400 = 4 game-hours per chunk). `RateToValue` will scale outputs accordingly.
3. **No `IsHost` gate inside `TickAll`** — but the only caller (`StateManager.WaitTimeCoroutine`, `StateManager.cs:1316`) is host-only. Don't call `TickAll` from a non-host without your own gate.

`TickAll` only runs from the `WaitTime` console command path (`/temult` → no, that's TimeMultiplier; `/wait` → yes via `WaitTime` IGameMessage → `WaitTimeCoroutine`). **It does NOT fire `TimeDayDidChange` synchronously** — the time advance happens via `ApplyLocal(new SetTimeOfDay(...))` at the end of each 1-game-hour chunk (`StateManager.cs:1328`), which only triggers `TimeAdvanced`. Day-change cascade fires at the next `TimeObserver` poll (within 1 real second).

**Race condition**: if `WaitTime` advances the clock past midnight, the steady-state `Industry.TickCoroutine` may also fire `Service` while `TickAll` is mid-loop on the same industry. The two share no lock. Not deterministic but rare in practice — `TickAll` blocks for `WaitForSeconds(0.25)` between hours, so cars/storage state can desync momentarily.

---

## `IIndustryContext` / `IndustryContext` — the per-tick façade

```csharp
public readonly struct IndustryContext(
    TrainController trainController,
    OpsController opsController,
    Industry industry,
    IndustryComponent industryComponent,
    IKeyValueObject keyValueObject,             // ← Industry's KVO
    IndustryContext.CarSizePreference sizePreference,
    float dt,                                   // ← game seconds (15f from steady-state)
    GameDateTime now)                            // ← captured at EnumerateComponentContexts call
    : IIndustryContext { ... }
```

Constructed by `OpsControllerExtensions.CreateContext` (`OpsControllerExtensions.cs:76`):

```csharp
public static IndustryContext CreateContext(this IndustryComponent ic, GameDateTime now, float dt) {
    OpsController shared = OpsController.Shared;
    TrainController shared2 = TrainController.Shared;
    IndustryContext.CarSizePreference carSizePreference = shared2.CarSizePreference;
    Industry industry = ic.Industry;
    return new IndustryContext(shared2, shared, industry, ic, industry.KeyValueObject, carSizePreference, dt, now);
}
```

### The `keyValueObject` is the **Industry's** KVO, not the component's

This is the source of the collision pitfall noted in the brief. Methods like `CounterIncrement(key, value)`, `CounterClear(key)`, `GetDateTime(key, default)`, `SetDateTime(key, dt)` all write into the **shared Industry KVO** with whatever raw key string you pass:

```csharp
public float CounterIncrement(string key, float value) {           // IndustryContext.cs:389
    float num = _keyValueObject[key].FloatValue + value;
    _keyValueObject[key] = Value.Float(num);
    return num;
}
```

There's no per-component prefix, **no validation, no namespacing**. Two components on the same Industry calling `ctx.SetDateTime("first", ...)` would clobber each other.

**Vanilla collision audit** (every IC that uses these APIs):

| Component | Key | Type | Risk |
|---|---|---|---|
| `IndustryUnloader` | `"unloaded-total-" + load.id` | counter | Safe — load-id-prefixed |
| `TeamTrack` | `"first"` | dateTime | **Collision risk** if two TeamTracks share an Industry |
| `TeleportLoadingIndustry` | `"lastLoaded"` | dateTime | **Collision risk** if two TLIs share an Industry |
| `ProgressionIndustryComponent` | `"indRecv"` (subkeyed inside) | dict | Safe — uses internal sub-dict |

The brief's noted "CounterIncrement / SetDateTime collision pitfall already noted in industries-ops" — confirmed. Custom IC authors **must namespace their keys** with `subIdentifier + ":" + key` or similar.

### `CarsAtPosition` — the 5cm/s filter

```csharp
public IEnumerable<IOpsCar> CarsAtPosition() {                     // IndustryContext.cs:82
    foreach (Car item in _controller.CarsAtPosition(_industryComponent)) {
        float num = 0.05f;
        if (!(Mathf.Abs(item.velocity) > num)) {
            yield return new OpsCarAdapter(item, _controller);
        }
    }
}
```

A car coasting through at >5 cm/s (≈0.18 km/h) is **invisible** to the IC. Useful for "drop and forget" workflows but breaks any "service in motion" use case. Patch this iterator (or the underlying `OpsController.CarsAtPosition`) if you need a different threshold.

### `PortionOfDayUntilNextRegularService`

```csharp
public float PortionOfDayUntilNextRegularService { get { ... } }   // IndustryContext.cs:53
```

Used by `IndustryLoaderBase.OrderCars` to predict next-day output. **Always non-negative**: if the next service hour has passed today, returns the fraction until it next occurs tomorrow. `interchangeServeHour` is read from `Storage.InterchangeServeHour` per-call (no cache).

### Patch candidates

| Method | Why patch |
|---|---|
| `IndustryContext.CarsAtPosition` | Change the 5cm/s threshold or filter behavior. Struct method — Harmony works but be careful with `this` lifetime. |
| `IndustryContext.CounterIncrement` / `CounterClear` / `SetDateTime` / `GetDateTime` | Add namespacing or audit-logging for KVO writes from ICs. |
| `IndustryContext.AddOrderedCars` | Override car-spawning during interchange service. |
| `IndustryContext.PayWaybill` | Per-Industry intercept of waybill payouts. **Higher granularity than `IndustryComponent.OnCompleteWaybill`** — runs even for custom IC subclasses. |
| `IndustryContext.PayLoad` | Per-Industry intercept of `payPerQuantity` payouts. |
| `OpsControllerExtensions.CreateContext` | Single chokepoint for all `IndustryContext` construction. Replace to swap out implementations or wrap with a logging proxy. |

### MP authority

The struct holds a host-only KVO reference (`Industry.KeyValueObject`). All writes via `IndustryContext` end up on the host KVO, which is HostOnly per `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` (key not equal to `"extraScheduled"` → HostOnly). **Calling any mutating method on a non-host machine will fail an auth check inside the KVO setter.** The struct itself is not network-aware — guard your code with `if (!StateManager.IsHost) return;` before constructing one.

---

## `IndustryComponent` lifecycle hooks (per-tick + per-day + per-service)

Full ordered list of virtual/abstract methods called by the pumps, with cadence:

| Method | Caller | Cadence | Default behavior |
|---|---|---|---|
| `Initialize(ctx, fromVersion)` | `Industry.InitializeIfNeeded` | Once when KVO key `"init"` ≠ current `Application.version` | No-op. `IndustryUnloader` overrides for starter inventory and `repair-parts` legacy migration. |
| `CheckForCompleted(ctx)` | `Industry.Tick` | Every steady-state tick (~5s real); every chunk in `TickAll` | Pays + closes any waybill destined here. |
| `Service(ctx)` | `Industry.Tick` | Every 3rd steady-state tick (~15s real); every chunk in `TickAll` | Abstract. Concrete subclasses do production / loading / unloading. |
| `OrderCars(ctx)` | `Industry.OrderCars` (called from `OpsController.RequestIndustriesOrderCars`) | Per interchange service tick | Abstract. Concrete subclasses queue inbound demand. |
| `DailyReceivables(now, ctx)` | `Industry.DailyReceivables` (from `OpsController.DayDidChange`) | Once per game day midnight | No-op. `IndustryUnloader` flushes `unloaded-total-*` counter to `PayLoad`. |
| `DailyPayables(now, ctx)` | `Industry.DailyPayables` (from `OpsController.DayDidChange`) | Once per game day midnight | No-op. `RepairTrack` collects shop wages. |
| `EnsureConsistency()` | `OpsController.CheckServiceInterchanges → EnsureConsistency` | Per interchange service tick (when ≥1 interchange is due) | No-op. Hook for migration / sanity sweeps. |
| `OnCompleteWaybill(ctx, car, waybill)` | `CheckForCompleted` (default impl) | Per waybill arrival | Pays + marks completed + bumps `Industry.ReceivedCarCount`. |
| `WantsAutoDestination(type)` | UI driver | On UI cycle | `false`. Overridden by `IndustryUnloader` to advertise "Load" when `!orderLoads`. |
| `AcceptsCarsWithLoad(load)` | `OpsController.CheckLoads` | At save load + ad-hoc | `true`. Overridden by Loader/Unloader to require matching `load`. |
| `BuildPanel(builder)`, `PanelFields(ctx)` | UI panel rebuild | On UI open / event | UI-only, no tick coupling. |

### `Initialize` is one-shot per game version

```csharp
internal void InitializeIfNeeded() {                               // Industry.cs:188
    string version = Application.version;
    string stringValue = _keyValueObject["init"].StringValue;
    if (stringValue == version) return;
    GameVersion fromVersion = GameVersion.FromStringOrZero(stringValue);
    foreach (var (ic, ctx) in EnumerateComponentContexts(0f))      // ← dt=0 here
        ic.Initialize(ctx, fromVersion);
    _keyValueObject["init"] = version;
}
```

**Subtlety:** `Initialize` runs with `dt=0`, so `RateToValue` returns 0 inside it. Don't write `Initialize` to do `ctx.AddToStorage(load, RateToValue(rate, ctx.DeltaTime), max)` — you'll add nothing.

`Initialize` runs **once per `Application.version` change**, not once per save. Restoring a save then upgrading the game causes Initialize to re-fire. Useful for migration; gotcha for "starter inventory" if the IC sees a new `Application.version` and re-seeds.

**Vanilla migration usage:**
- `IndustryUnloader.Initialize` (`IndustryUnloader.cs:39`): seeds storage at 25% on first ever load (`fromVersion.IsZero`); also re-seeds `repair-parts` if pre-`V2024_4_0` (the starter-pile retrofit).

### `OnCompleteWaybill` calls `TimeWeather.Now`, not `ctx.Now`

```csharp
protected virtual void OnCompleteWaybill(IIndustryContext ctx, IOpsCar car, Waybill waybill) {  // IndustryComponent.cs:172
    ctx.PayWaybill(car, waybill);
    waybill.PaymentOnArrival = 0;
    waybill.Completed = true;
    float num = (TimeWeather.Now.TotalHours - waybill.Created.TotalHours) / 24f;     // ← Now, not ctx.Now
    car.SetWaybill(waybill, this, $"Paid Completed ({num:F1} days)");
    Industry.ReceivedCarCount++;
}
```

Tiny inconsistency: every other place in `Industry.Tick` uses the captured `ctx.Now`. The "days" number in the log message is freshly read. Generally harmless (sub-frame skew) but pointless duplication.

---

## `OpsController.DayDidChange` — the daily-rollover cascade

```csharp
private void DayDidChange(TimeDayDidChange _) {                    // OpsController.cs:163
    if (!StateManager.IsHost) return;
    GameDateTime now = TimeWeather.Now.WithHours(0f);              // snap to midnight
    Industry[] allIndustries = AllIndustries;
    UpdatePerformance(now);                                        // (1) per-industry on-time scoring
    foreach (var industry in allIndustries) industry.DailyReceivables(now);    // (2) industry-side income
    foreach (var industry in allIndustries) industry.RollToNextContract();      // (3) tier promote/demote + penalty
    foreach (var industry in allIndustries) industry.DailyPayables(now);        // (4) industry-side expenses
}
```

**Order matters:**

1. `UpdatePerformance` first so the data is fresh for `RollToNextContract`'s `IsFailing` check.
2. `DailyReceivables` before `RollToNextContract` so the day's accumulated income is paid against the *current* contract's tier.
3. `RollToNextContract` between receivables and payables — so wages on a downgraded contract still come out of the new tier? **No** — `DailyPayables` uses `Industry.GetContractMultiplier()` which now reads the **already-rolled** Contract. So a downgraded shop pays *new* wages on day-of-downgrade, but earned old-rate income before the roll.
4. `DailyPayables` last, so the player's balance reflects all of: yesterday's income, today's tier penalty, today's wages.

### Other day-change subscribers (full audit)

```
TimeDayDidChange
├── OpsController.DayDidChange                        (Model.Ops/OpsController.cs:163)
└── StateManager.OnDayDidChange                        (Game.State/StateManager.cs:1450)
        └── PayAutoEngineerWages                       (deducts WagesAI category)
```

That's it in vanilla. **`ReputationTracker` does NOT subscribe to `TimeDayDidChange`** — instead it uses `TimeWeather.WaitForNextDay` (5s game-time poll) and its own `LastUpdatedDay` watermark (`ReputationTracker.cs:156`). Same for `DailyReportGenerator` (uses `WaitForNextHour` + `TimeForDailyEvent(last, 18)` gate).

The implication: **the order of operations within one game-day midnight is**:

1. `TimeObserver` polls (1 Hz real time).
2. Sees `now.Day != _lastTime.Day`, fires `TimeDayDidChange`.
3. Subscribers fire **synchronously in registration order** (`Messenger` is a Mvvm Light synchronous dispatcher). Vanilla registration order under host:
   - `StateManager.OnEnable` → `OpsController.OnEnable` (later GameObject) → `OpsController.DayDidChange` runs first because StateManager registers in `OnEnable` at line 223, OpsController at `OnEnable` line 100. **Race**: depends on Awake/Enable order across scenes. In practice OpsController is in MapScene loaded after StateManager — so OpsController fires *after* StateManager. But both run in one frame.
4. **Independent** of the above: `ReputationTracker.TickCoroutine` will see the `Day` change on its next 5s game-time poll (so up to 5 game seconds late) and recompute reputation.
5. **Independent**: `DailyReportGenerator.TickCoroutine` polls every game hour; when it crosses the 18:00 of the new day's evening, generates the report.

So the daily-cascade is *not* a single atomic transaction. A mod observing `BalanceDidChange` on midnight will see: (a) freight payouts from `DailyReceivables`, (b) tier penalty from `RollToNextContract`, (c) shop wages from `DailyPayables`, (d) AI engineer wages from `StateManager.OnDayDidChange` — in that order, all in the same frame.

### `UpdatePerformance` — what feeds the rolling history

```csharp
private void UpdatePerformance(GameDateTime now) {                 // OpsController.cs:908
    RebuildPopulations();                                          // ← side-effect: passenger growth re-seeds
    var enumerable = from t in GetOpenWaybills() where t.Waybill.PaymentOnArrival > 0 select t.Waybill;
    Dictionary<Industry, List<float>> map = new();
    foreach (Waybill wb in enumerable) {
        float ageInDays = now.TotalDays - wb.Created.TotalDays - wb.GraceDays;
        Record(wb.Destination); if (wb.Origin.HasValue) Record(wb.Origin.Value);
        void Record(OpsCarPosition pos) {
            Industry ind = IndustryComponentForPosition(pos).Industry;
            if (!map.TryGetValue(ind, out var l)) map.Add(ind, l = new List<float>());
            l.Add(ageInDays);
        }
    }
    foreach (var ind in AllIndustries.Where(i => !i.ProgressionDisabled))
        ind.UpdatePerformance(map.TryGetValue(ind, out var ages) ? ages : new List<float>(), now);
}
```

**Key facts:**
- Walks **all open waybills** with `PaymentOnArrival > 0` (so autodest/sell/progression waybills don't count toward performance).
- Each waybill contributes its age to **both** origin and destination industries.
- Industries with no in-flight waybills get an empty list passed in — `Industry.CalculatePerformance` returns `1f` for empty (perfect score), then null-checks against `ReceivedCarCount < 1` to suppress polluting history.
- **Side effect**: `RebuildPopulations()` runs first, recomputing `PassengerStop.AdditionalPopulation` from sibling industries' contract `Percent` × span lengths. So passenger growth keys off contract status at midnight — drop a contract and your passenger inflow drops the next day.

### `RollToNextContract`

```csharp
public void RollToNextContract() {                                 // Industry.cs:429
    Contract? c = Contract;
    Contract? next = NextContract;
    var hist = PerformanceHistory;
    string reason = "";
    if (c.HasValue && IsFailing(hist, c.Value.Tier, out int proposed)) {
        if ((next?.Tier ?? c.Value.Tier) > proposed) {
            reason = " (Low performance.)";
            next = new Contract(proposed);
        }
    }
    if (next.HasValue) {
        int penalty = this.PenaltyForChange(next.Value.Tier, hist.Count, out _, out _);
        if (penalty > 0) {
            StateManager.Shared.ApplyToBalance(-penalty, Ledger.Category.Freight, EntityReference, "Tier Change Penalty", 0, quiet: true);
        }
        if (next.Value.Tier == 0) {
            SetContract(null);
            OpsController.Shared.ReturnWaybillsFrom(this);          // ← terminates: clears waybills/autodest pointing here
        } else {
            SetContract(next);
        }
        Multiplayer.Broadcast($"Contract with {Hyperlink.To(this)} {...}.{reason}{...}");
        NextContract = null;
    }
}
```

**Termination side effect**: `ReturnWaybillsFrom(industry)` walks every car looking for waybills/autodests pointing at the cancelled industry, clears them or routes player-owned cars to `SetWaybillAuto(null)`. Foreign cars get a fresh waybill back to a random enabled interchange.

### MP authority

- `DayDidChange`, `DailyReceivables`, `DailyPayables`, `RollToNextContract` all gated by `IsHost` (top-level via `if (!IsHost)` in `DayDidChange`).
- Clients never see midnight as anything special — they observe KVO updates as host writes `_perfHist`, `contract`, `balance`, etc.
- `Multiplayer.Broadcast` for tier-change messages routes via the standard Alert pipeline (see [`hyperlink-entityref.md`](hyperlink-entityref.md) for the broadcast wire).

### Patch candidates

| Method | Why patch |
|---|---|
| `OpsController.DayDidChange` | Single chokepoint to inject custom daily logic before/after cascade. |
| `OpsController.UpdatePerformance` (private) | Replace the per-industry on-time scoring algorithm. |
| `Industry.RollToNextContract` | Custom contract promotion/demotion policy. |
| `Industry.CalculatePerformance` (private) | Per-industry performance score curve. |
| `Industry.IsFailing` (private static) | The "3-day average < 0.5 → demote 2 tiers" rule. |
| `ContractExtensions.PenaltyForChange` (extension) | Tier-change penalty schedule. |
| `StateManager.PayAutoEngineerWages` | Per-day AI engineer wage formula. |

---

## `OpsController.CheckServiceInterchanges` — the per-minute interchange pump

```csharp
private void CheckServiceInterchanges() {                          // OpsController.cs:1032
    if (!StateManager.IsHost) return;
    GameDateTime now = TimeWeather.Now;
    var due = AllInterchanges.Where(ix => ix.GetNextServiceTime(now, out _) <= now).ToList();
    if (!due.Any()) return;

    EnsureConsistency();                                           // (1) every IC.EnsureConsistency()
    foreach (var ix in due) ix.PrepareToService();                  // (2) clears Orders list

    try { RequestIndustriesOrderCars(); }                            // (3) Industry.OrderCars on every enabled industry, shuffled
    catch (Exception e) { Log.Error(e, ...); }

    foreach (var ix in due) {                                        // (4) per due interchange
        IndustryContext ctx = ix.CreateContext(now, 0f);             //     dt=0 — no rate math should run here
        ix.ServeInterchange(ctx);                                    //     remove completed inbound; place ordered cars
        if (ix.Orders.Count > 0) {                                   //     if not all orders fit, schedule extra
            ...ScheduleExtra(NextAvailableServiceTime(ctx.Now));
        }
    }
}
```

### Cadence note

`TimeMinuteDidChange` fires when `Mathf.FloorToInt(now.Minutes) != Mathf.FloorToInt(_lastTime.Minutes)`. At 1× game speed = once per real minute. At 4× = once per 15 real seconds. At 60× (max via console) = once per real second.

But `CheckServiceInterchanges` returns immediately if no interchange is due. Interchanges are due once per game day (at `Storage.InterchangeServeHour`, default 8 AM) plus extra-scheduled events. So actual work happens on at most ~24 game-minute edges per game day — not every minute.

### `EnsureConsistency` runs on every Component when any Interchange is due

```csharp
private void EnsureConsistency() {                                 // OpsController.cs:1217
    foreach (var industry in AllIndustries)
        foreach (var component in industry.Components)
            try { component.EnsureConsistency(); }
            catch (Exception e) { Log.Error(e, ...); }
}
```

**No vanilla IC overrides `EnsureConsistency`.** It's a pure extension hook. Mods can use it as a "called daily but only when ops actually ticks" callback — useful for re-validating cross-industry invariants without listening to every event.

### `RequestIndustriesOrderCars` — area-shuffle and OverhandShuffle

```csharp
public void RequestIndustriesOrderCars() {                         // OpsController.cs:869
    var areas = Areas.ToList();
    areas.Shuffle();
    var industries = (from i in areas.SelectMany(a => a.Industries)
                      where !i.ProgressionDisabled select i).ToList();
    var rnd = new Random();
    int n = InterchangeShuffle;                                    // GameStorage setting
    for (int k = 0; k < n; k++) industries.OverhandShuffle(rnd, 2, 4);
    foreach (var industry in industries) industry.OrderCars();
}
```

The `InterchangeShuffle` setting (default 5, configurable in CompanyWindow) controls how many overhand-shuffle passes apply. **Higher value = more random ordering of which industries get to claim interchange capacity first.** At 0, areas are still shuffled but industry order within an area is deterministic. Affects fairness when capacity is constrained.

### Interchange `Orders` list lifecycle

`Interchange.Orders` is `[NonSerialized]` and exists only during the `CheckServiceInterchanges` call:

1. `PrepareToService()` clears it.
2. `Industry.OrderCars()` calls `ctx.OrderEmpty/OrderLoad` → `OpsController.AddOrderForInboundCar` → `Interchange.AddOrder` (coalescing on `(Load, CarTypeFilter, Destination)`).
3. `ServeInterchange(ctx)` realizes orders via `ctx.AddOrderedCars(Orders, maxToOrder)`, decrementing `CarCount` per spawned car.
4. Any leftover (`Orders.Count > 0`) triggers `ScheduleExtra` to retry ~2.5h later.
5. The list persists until next `PrepareToService` clears it again.

**Clients have no view of `Orders` at any point.** The next-service hint they see is the broadcast Multiplayer message + the `extraScheduled` KVO (Trainmaster-write-allowed).

### MP authority

- `IsHost` gate at top.
- All KVO writes (`storage`, `_perfHist`, `lastServiced`, `extraScheduled`, etc.) on `Industry` go through `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` — HostOnly except `extraScheduled`.
- `TrainController.PlaceTrain` is host-only by design (car spawns).

---

## Save/load interaction

### What persists

- **Industry KVO** is registered via `IndustryStorageHelper(_keyValueObject, identifier)` in `Industry.Awake` (`Industry.cs:131`), which calls `StateManager.Shared.RegisterPropertyObject(_id, kvo, this)` (`IndustryStorageHelper.cs:82`). Stored as a property-object snapshot. Includes:
  - `storage` (per-load quantities)
  - `contract` / `nextContract`
  - `_perfHist`
  - `_recvdCars`
  - `lastServiced`, `extraScheduled`, `interchangeDisabled`
  - `warnings`
  - `init` (version watermark)
  - All `CounterIncrement`/`SetDateTime` keys (e.g. `unloaded-total-coal`, `lastLoaded`, `first`)
- **Car KVO** (waybill, override-dest, oil, condition, autodest) — saved per `cars-cargo.md`.
- **OpsController switch-list state** — see `OpsController.PopulateSnapshotForSave` → `SwitchListController.PopulateSnapshot`.

### What does NOT persist (re-derived on load)

- **`Industry._cachedComponents`** — re-built lazily after `RebuildCollections`.
- **`Interchange.Orders`** — `[NonSerialized]`, host-side scratch only. **Pending orders that didn't fit at last service tick are lost across save/load.** They'll be regenerated next service tick via `OrderCars`.
- **`OpsController._carPositionLookup`** — rebuilt in `RebuildCollections` from scene graph.
- **`OpsController._distanceInMilesCache`** — empty on load, fills as `PaymentForMove` is called.
- **`OpsController._positionToAreaCache`** — same.
- **`OpsController._industryToCoalescedPaymentAnnouncements`** — empty on load.
- **`InterchangeSelector` state** — internal cycle counter resets; first interchange selection post-load is biased toward the first cycle entry.
- **`PassengerStop._waiting`** — wait, this DOES persist via the `state` KVO key (host-only). See [passengers-timetable.md](passengers-timetable.md).
- **`Industry._tickCoroutine`** — not persisted; re-started in `OnEnableWithProperties` after restore.

### Restore order

1. `RestoreNotifier.Shared.RegisterForRestore(EnablePriority, ...)` queues the re-enable callback. `Industry.OnEnableWithProperties` (`Industry.cs:142`) runs **after KVO restoration completes** for that property object.
2. `Industry.OnEnableWithProperties` checks `IsHost` and starts `TickCoroutine`.
3. The `TickCoroutine` immediately yields `WaitForSeconds(Random.Range(0,15))` — first Service does not happen until 5–20s after load.
4. **`InitializeIfNeeded` runs only after the jitter** — so a freshly-loaded save sees a 0–15s window where industries exist but are not running their version-keyed migration.
5. `OpsController.PostRestoreProperties` (`OpsController.cs:138`) runs separately, called externally by `StateManager` during load:
   - `CheckLoads` — fix corrupt waybill/load mismatches.
   - `CheckWaybills` — fix corrupt waybills.
   - `RebuildPopulations` — recompute passenger inflow.
   - `CheckServiceInterchanges` — drain any "due" interchanges immediately.
   - `CheckOneEnabledInterchange` — modal alert if no interchange is enabled.

**Race**: `CheckServiceInterchanges` runs synchronously during load, **before** any Industry's `TickCoroutine` jitter has elapsed. So `RequestIndustriesOrderCars` fires immediately on load, *but* `OrderCars` reads counters / contract state from the freshly-restored KVO — which is fine. The gotcha is `EnsureConsistency` running on every Component before any Component has had a tick to react to load.

### Migration via `Initialize` re-fires across game versions

`InitializeIfNeeded` compares `_keyValueObject["init"]` to `Application.version` (literal string match). If the version changed between save and load, **every Component's `Initialize` re-runs** with `fromVersion` set to the previous version's `GameVersion` parse. This is the only formal IC-side migration hook.

---

## MP authority — the full pump map

| Pump / Action | Authority | Mechanism |
|---|---|---|
| `Industry.TickCoroutine` start | Host-only | `Industry.OnEnableWithProperties` checks `IsHost` |
| `Industry.Tick` | Host-only (no inner check) | Only called from `TickCoroutine` (host) and `TickAll` (host caller) |
| `Industry.TickAll` | Implicit host-only | Only caller `StateManager.WaitTimeCoroutine` is host-only |
| `IndustryComponent.Service` invocations | Host-only | Driven from host-only `Industry.Tick` |
| `IndustryComponent.OnCompleteWaybill` writes (waybill.Completed, ReceivedCarCount, ledger payout) | Host-only | All inside Service/CheckForCompleted |
| `IndustryContext.AddToStorage` etc. | Host-only | Industry KVO is HostOnly per `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` |
| `IndustryContext.CounterIncrement` / `SetDateTime` | Host-only | Same — Industry KVO writes |
| `OpsController.DayDidChange` | Host-only | `if (!IsHost) return;` at top |
| `OpsController.CheckServiceInterchanges` | Host-only | `if (!IsHost) return;` at top |
| `OpsController.RequestIndustriesOrderCars` | Host-only (called from CheckServiceInterchanges) | No internal gate; relies on caller |
| `OpsController.AnnounceCoalescedPayments` | Both (no auth) | Pure Multiplayer.Broadcast — host emits, clients receive via Alert pipeline |
| Client view of all the above | Read-only | KVO observers on `_perfHist`, `contract`, `storage`, `balance`, etc. |

**Clients see results, never run pumps.** The architectural consequence: a multiplayer client mod that wants to react to "industry just produced X" must either:
1. Observe the KVO for `storage` (rate-limited, fires on any change) and infer.
2. Listen for `Multiplayer.Broadcast` via the chat / Alert pipeline (string-parsing).
3. Receive a custom message the host's mod-side sends.

There is **no per-tick "industry serviced" event**. If you want one, mod the host side to send a custom IGameMessage on each `Service` post-call.

---

## `PassengerStop.Loop` — the real-second oddity (confirmed)

```csharp
private IEnumerator Loop() {                                       // PassengerStop.cs:301
    TrainController trainController = TrainController.Shared;
    while (true) {
        yield return new WaitForSeconds(3f);                       // ← REAL seconds
        if (ProgressionDisabled) continue;
        foreach (Car item in FindCars(trainController))
            if (!_workingCarIds.Contains(item.id) && ShouldWorkCar(item))
                StartCoroutine(WorkCar(item));
        GrowWaiting();                                             // gated internally by 300 game-second `_lastGrow`
        PayPending();
    }
}
```

**Confirmed**: the outer `Loop` uses `WaitForSeconds(3f)` (Unity's `WaitForSeconds` is real seconds, not game seconds). Same for `WorkCar`'s inner waits (`Random.Range(1f, 2f)`). So:

- The "look for newly-stopped cars" / "decide whether to work them" cadence is **3 real seconds**, regardless of `TimeMultiplier`.
- The "passenger population grew" calculation inside `GrowWaiting` is gated by `(now - _lastGrow) / 300.0` — that's `300 game seconds`. So population growth is real-time-independent.
- The `WorkCar` per-car coroutine then runs `UnloadCar` + `LoadCar` with `Random.Range(1f, 2f)` real-second pacing per swap.

**Implication**: at high `TimeMultiplier`, passenger boarding/unboarding lags game time. A 60× speed run sees passengers loading at the same real-time rate but with game-clock 60× as much "missed" time per loading. In practice the per-`WorkCar` coroutine continues until the car is fully loaded/unloaded, so the absolute throughput is fine — but the *delay* between "stopped at platform" and "first passenger swap" is fixed at 3 real seconds.

`GrowWaiting` reads `_levelsByHour[(int)now.Hours]` for the current hour multiplier — so a 24-hour cycle of population growth respects in-game time of day, even though the loop itself is real-time.

**Why is this different from `Industry.TickCoroutine`?** Both use `WaitForSeconds(N)` (real seconds), but `IndustryComponent.RateToValue` corrects for `TimeMultiplier` to convert per-day rates to per-tick. `PassengerStop.GrowWaiting` does its own `(now - _lastGrow) / 300.0` integer-cycle math which inherently respects game time. They're consistent at the math layer, just structured differently — `PassengerStop` is **not** an `IndustryComponent` and doesn't share the IC pump.

### Patch candidates

| Method | Why patch |
|---|---|
| `PassengerStop.Loop` (private) | Replace the 3s real-second cadence — e.g. game-second pacing for high-speed mode. |
| `PassengerStop.GrowWaiting` (private) | Override population growth math (currently `_levelsByHour[hour] * num3 * maxWaitingCoefficient`). |
| `PassengerStop.WorkCar` (private) | Custom load/unload pacing or order. |

Cross-link to [passengers-timetable.md](passengers-timetable.md) for the full PassengerStop coverage.

---

## Hidden gates & non-obvious patch points

### Gates that silently skip Service

1. **`Industry.ProgressionDisabled`** — early return from `Tick`. Zero `CheckForCompleted` and zero `Service` calls. **Also blocks `OrderCars`** (separate gate in `RequestIndustriesOrderCars`).
2. **`IndustryComponent.ProgressionDisabled`** — skips only `Service`. `CheckForCompleted` still runs.
3. **`StateManager.IsHost == false`** — `TickCoroutine` not started. Component never receives Service on clients.
4. **`OnEnableWithProperties` not yet called** — Components added to scene but their parent `Industry.OnEnableWithProperties` not yet fired (e.g. before `RestoreNotifier` flushes) won't tick.
5. **`Tick`'s try/catch** — an exception inside `EnumerateComponentContexts` (e.g., a Component construction NRE) skips remaining Components for that one tick. Per-Component try/catch swallows everything else.

### Side effects in Service that aren't obvious

1. **`IndustryUnloader.Service`** mutates the `unloaded-total-*` counter via `ctx.CounterIncrement` — that bare-key counter is what `DailyReceivables` reads later. If a custom IC reads `Industry.KeyValueObject["unloaded-total-coal"]` directly (e.g., for a UI), they bind to that contract.
2. **`FormulaicIndustryComponent.Service`** writes a "Production Stopped: ..." warning via `IndustryStorageHelper.SetWarning` if any input is starved. Other ICs sharing the Industry can read `Industry.Storage.Warnings`.
3. **`RepairTrack.Service`** consumes `repair-parts` storage and wipes `OverrideDestination.Repair` on completion — see [wear-durability.md](wear-durability.md#modelopsrepairtrack-industry-side-repair).
4. **`IndustryComponent.OnCompleteWaybill`** mutates the `Industry.ReceivedCarCount` KVO key. Used by `Industry.CalculatePerformance` to suppress polluting history with "no cars received" days.
5. **`IndustryContext.PayWaybill`** calls `OpsController.AddCoalescedPaymentAnnouncement` → defers a `Multiplayer.Broadcast` for ~1 real second. So a Service-tick that pays out a waybill triggers an announcement on a *different* tick (the next `OpsController.PeriodicUpdate` flush).

### Paths that bypass `Service` entirely

Modders looking for "every IC tick" will miss these:

1. **`OpsController.Sweep(Car)`** (`OpsController.cs:817`) — moves cars to their destination, skipping Service. The next steady-state `CheckForCompleted` does the payout.
2. **`Interchange.ServeInterchange`** (`Interchange.cs`) — runs from `CheckServiceInterchanges`, not from any Industry tick. Removes completed waybills inline.
3. **`InterchangedIndustryLoader.ServeInterchange`** — also from the interchange-service tick, not from `Service`. `InterchangedIndustryLoader.Service` is a no-op.
4. **`TeleportLoadingIndustry.Service`** — does run via the IC pump, but the actual *work* (move car, set load info) calls `TrainController.MoveCar`, `Car.SetLoadInfo` directly, bypassing `OpsCarAdapter.Load`/`Unload`. So mods patching `OpsCarAdapter.Load` won't see teleport-loader fills.
5. **`StateManager.WaitTimeCoroutine`** — calls `Industry.TickAll` which forces `shouldService:true` on every chunk. Service runs more often than steady state would.
6. **`OpsController.PostRestoreProperties`** — runs `CheckServiceInterchanges` during load, before any Industry has its first `TickCoroutine` Service.

### Init-order pitfalls

1. **`Industry.Components` is cached on first `get`**. If a mod adds an `IndustryComponent` after first access, the new IC will not appear in subsequent `Tick` iterations. Workaround: null `_cachedComponents` via reflection, or call `gameObject.GetComponentsInChildren<IndustryComponent>()` directly.
2. **`OpsController._carPositionLookup` keyed by `IndustryComponent.Identifier`.** Adding ICs at runtime requires firing `IndustriesDidChange` to trigger `OpsController.RebuildCollections`, otherwise `OpsController.IndustryComponentForPosition` won't find the new IC.
3. **`Awake`/`OnEnable` ordering across Industries is not deterministic.** If your custom IC's `Initialize` reads sibling-Industry KVO state, that state may not yet be restored (depends on Awake order).
4. **`StateManager.OnPropertiesDidRestore`** — observers wired here (e.g. `Car.WearFeature = value`) come after KVO restore but may run *after* an Industry has already started its TickCoroutine if the Industry's `OnEnableWithProperties` ran first. Check `wear-durability.md` for the timing on `WearFeature`.
5. **`InitializeIfNeeded` runs after the 0-15s jitter, not at `OnEnable`.** Migrations that ICs do in `Initialize` are not synchronously available when other systems load.

### Race conditions

1. **`TickAll` vs `TickCoroutine`** during `WaitTime`: both can be in flight on the same Industry. No locking. Symptoms: storage values briefly out-of-order, performance counters double-counted in rare cases.
2. **`DayDidChange` cascade vs steady-state Service**: midnight cascade runs synchronously inside the `TimeObserver` poll. If a Service is mid-flight when midnight crosses, `DailyReceivables` may run before the in-flight Service completes. Unity coroutines are cooperatively scheduled — `Tick` itself doesn't yield mid-call, so atomically per-Tick this is safe; but a second Industry's Service queued on the next coroutine resume could see post-rollover state.
3. **`InterchangeSelector`** is shared static state (`OpsController._interchangeSelector`). Two consecutive `WaybillCarToInterchange` calls in different `Service` ticks hit the same shared cycle counter, so order matters.
4. **Coalesced payment announcements** flush on a 0.25s real-time loop. If a `Multiplayer.Broadcast` happens during the flush, it can interleave with announcement output.

---

## Patch surfaces — recipes

### Custom IC with custom tick cadence

The per-IC cadence is governed by `Industry.TickCoroutine`. To make your IC run on a different schedule:

**Option A — coroutine in the IC itself**
```csharp
public class MyIC : IndustryComponent {
    private Coroutine _myCoroutine;
    public override void Service(IIndustryContext ctx) {}                       // no-op
    public override void OrderCars(IIndustryContext ctx) {}                     // no-op
    private void OnEnable() {
        if (StateManager.IsHost)
            _myCoroutine = StartCoroutine(MyTick());
    }
    private void OnDisable() {
        if (_myCoroutine != null) StopCoroutine(_myCoroutine);
    }
    private IEnumerator MyTick() {
        while (true) {
            yield return new WaitForSeconds(60f);
            // Do work — careful: no IndustryContext available here.
            // Build one yourself: var ctx = this.CreateContext(TimeWeather.Now, dt);
        }
    }
}
```

**Option B — Harmony postfix on `Industry.Tick`**
```csharp
[HarmonyPatch(typeof(Industry), "Tick")]
class TickPatch {
    static void Postfix(Industry __instance, float serviceInterval, bool shouldService) {
        // Runs after every steady-state tick on every Industry. Filter by __instance.identifier.
    }
}
```

### Intercepting Service calls

```csharp
[HarmonyPatch(typeof(IndustryComponent), nameof(IndustryComponent.Service))]
class ServicePatch {
    static bool Prefix(IndustryComponent __instance, IIndustryContext ctx) {
        if (__instance is RepairTrack) return false;            // veto specific IC types
        return true;
    }
    static void Postfix(IndustryComponent __instance, IIndustryContext ctx) {
        // Audit/log every Service call.
    }
}
```

But — `Service` is `abstract`. Harmony patches on abstract methods don't fire from polymorphic calls; you need to patch each concrete subclass (`IndustryLoader.Service`, `IndustryUnloader.Service`, `FormulaicIndustryComponent.Service`, `RepairTrack.Service`, `TeamTrack.Service`, `TeleportLoadingIndustry.Service`, `InterchangedIndustryLoader.Service`, `ProgressionIndustryComponent.Service`, mod-added subclasses…). Cleaner: patch `Industry.Tick` postfix and inspect `Components`.

### Custom daily-cycle hook

```csharp
[HarmonyPatch(typeof(OpsController), "DayDidChange")]
class DayPatch {
    static void Postfix(TimeDayDidChange _) {
        if (!StateManager.IsHost) return;
        // Runs after vanilla cascade (UpdatePerformance → DailyReceivables → RollToNextContract → DailyPayables).
    }
}
```

Or subscribe to `TimeDayDidChange` in your own `MonoBehaviour`. Be aware your handler runs in the same synchronous Messenger dispatch — if you throw, you might break vanilla cascade for handlers later in the registration order. Use try/catch.

### Custom passenger-stop tick

`PassengerStop.Loop` is private. To intercept:

```csharp
[HarmonyPatch(typeof(PassengerStop), "Loop")]
class StopPatch {
    static IEnumerable<MethodBase> TargetMethods() {
        // Coroutines compile to nested classes — patch the MoveNext.
        ...
    }
}
```

Easier: patch `PassengerStop.GrowWaiting` (the meaningful per-loop work) directly:

```csharp
[HarmonyPatch(typeof(PassengerStop), "GrowWaiting")]
class GrowPatch {
    static bool Prefix(PassengerStop __instance) {
        // Replace growth logic entirely.
        return false; // skip vanilla
    }
}
```

### Custom interchange behavior

Patch `OpsController.CheckServiceInterchanges` (private) or `Interchange.ServeInterchange`. The former is the fan-out chokepoint; the latter is per-interchange.

### Custom IndustryContext (e.g., to namespace counters)

`IndustryContext` is a struct, but `OpsControllerExtensions.CreateContext` is the single factory. Patch it:

```csharp
[HarmonyPatch(typeof(OpsControllerExtensions), nameof(OpsControllerExtensions.CreateContext))]
class CtxPatch {
    static bool Prefix(IndustryComponent ic, GameDateTime now, float dt, ref IndustryContext __result) {
        // Build your own struct and return false.
        return true; // or replace
    }
}
```

Or — simpler — wrap calls to `ctx.CounterIncrement` etc. in a helper method that prefixes keys with `subIdentifier`:

```csharp
public static class CtxExt {
    public static float CounterIncrementSafe(this IIndustryContext ctx, string ic, string key, float v)
        => ctx.CounterIncrement(ic + "." + key, v);
}
```

---

## Gotchas (cross-cutting)

- **`Industry.TickCoroutine.WaitForSeconds(5f)` is real time, not game time.** A `TimeMultiplier` of 100× does NOT make industries tick 100× more often. The per-tick `dt` (`15f` constant) and `RateToValue`'s `TimeMultiplier` math do the conversion — but only via the rate, not the cadence.
- **`Tick(15f, ...)` always passes `dt=15`**, regardless of how much real time elapsed since last tick. If the game lagged for 30s real, the next Service still gets dt=15. Outputs are under-counted in laggy frames.
- **`TimeMultiplier < 0.001` returns 0 from `RateToValue`.** Pause kills production but Service still runs and `CheckForCompleted` still pays arriving cars. Don't equate "paused" with "frozen ops".
- **`InitializeIfNeeded` runs after the 0–15s jitter, not at OnEnable.** First-load behavior of mod ICs may differ from steady-state.
- **`Initialize`'s `dt=0`** — `RateToValue` returns 0 inside Initialize. Don't compute rate-derived starter inventory there.
- **`Industry.Components` is cached.** Adding/removing ICs at runtime requires invalidating `_cachedComponents` (no public hook).
- **`DayDidChange` cascade** runs synchronously in the `TimeObserver` callback. It's all one frame, so `BalanceDidChange` observers see N updates in the same Update tick.
- **`ReputationTracker` and `DailyReportGenerator` do NOT subscribe to `TimeDayDidChange`** — they poll `TimeWeather.WaitForNextDay` / `WaitForNextHour`. So reputation refresh can lag the day-rollover cascade by up to 5 game seconds.
- **`TickAll` always passes `shouldService=true`** — bypassing the `tickCount % 3 == 0` gate. WaitTime fast-forward services every chunk. If your IC accumulates side effects in `Service` that should only fire 1/3rd as often, you'll see 3× as many during WaitTime.
- **`TickAll` chunks are 4 game-hours each, max.** A `/wait 24h` runs 6 chunks. Each chunk runs through all industries before yielding `WaitForSeconds(0.25)`. Long waits block the main thread for noticeable time.
- **`PassengerStop.Loop` is real seconds.** This is the only ops-layer pump that uses real time for its outer cadence.
- **No cross-IC ordering guarantee within an Industry.** `EnumerateComponentContexts` iterates `Components` in `GetComponentsInChildren` order — Unity's hierarchy order. Don't rely on Component A running before Component B in the same Industry.
- **`IndustryContext.CounterIncrement` and friends use the Industry's KVO with bare key names.** Two ICs on the same Industry can clobber each other. Prefix your keys with `subIdentifier`.
- **`OpsController.InterchangeSelector`** is stateful. Same-position-twice gets the same interchange (cache); changing position spins the cycle counter forward. Save/load resets it.
- **`OpsController.PostRestoreProperties` calls `CheckServiceInterchanges` synchronously during load.** This runs before any `TickCoroutine` jitter has elapsed. Any IC that needs steady-state to be running before its first interchange-service should defer.
- **The 0–15s jitter is per-Industry, not per-Component.** All Components on the same Industry tick in lockstep with each other.
- **Exception in `Service` is logged and swallowed.** The IC continues to be ticked next time. No "broken IC" auto-disable. Spam-logging risk.
- **`RebuildPopulations` runs inside `UpdatePerformance` at midnight.** A change to `Industry.Contract` mid-day doesn't immediately update `PassengerStop.AdditionalPopulation` — it waits for the next midnight.
- **`Multiplayer.Broadcast` calls inside `Tick` happen on the host's main thread, synchronously.** They're fine for one-shot broadcasts but accumulating per-Service can spam.

---

## Cross-references

### To Industries & Ops

- Full `IndustryComponent` per-type coverage (Loader/Unloader/Formulaic/TeamTrack/Interchange/etc.): see [industries-ops.md § Concrete IndustryComponent types](industries-ops.md#concrete-industrycomponent-types).
- `IIndustryContext` complete API surface: [industries-ops.md § IIndustryContext](industries-ops.md#iindustrycontext--industrycontext-the-per-tick-gateway).
- `Waybill`, `Order`, `Interchange.ServeInterchange` flow: [industries-ops.md § Model.Ops.Interchange](industries-ops.md#modelopsinterchange-the-foreign-road-portal).
- `OpsController.PaymentForMove` and `CalculateGraceDays` formulas: [industries-ops.md § Payment formula](industries-ops.md#payment-formula).

### To Wear & Durability

- `RepairTrack.Service` and `RepairTrack.DailyPayables` per-method coverage: [wear-durability.md § RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair).
- `OverrideDestination.Repair` clear in `RepairTrack.CheckForCompletelyRepairedCars`: same.
- Per-tick wear (`Car.BankOdometer`) is **independent of industry tick** — runs in physics tick. Industry consumers (only `RepairTrack`) read `Car.OdometerService` / `Car.Condition` as KVO snapshots.

### To Cars & Cargo

- `OpsCarAdapter.Load` / `Unload` are the ops-side load mutators; the underlying `Car.SetLoadInfo` write is documented in [cars-cargo.md](cars-cargo.md).
- Bardo ↔ industry interaction: cars in Bardo are filtered out of `OpsController.CarsAtPosition` because they're not in `TrainController.Cars`. See [tile-loading-bardo.md § Bardo](tile-loading-bardo.md).

### To Daily Reports

- `DailyReportGenerator.TickCoroutine` runs on its own per-game-hour pump (not subscribed to `TimeDayDidChange`): see [daily-reports.md](daily-reports.md). Triggers at 18:00 daily via `TimeForDailyEvent(last, 18)`.
- `RepairTrack.DailyReportSummary` is the only `*DailyReportSummary` in vanilla — composed inside the daily report at 18:00, not midnight.

### To Reputation

- `ReputationTracker` polls `WaitForNextDay` (5s game-time poll) with its own `LastUpdatedDay` watermark — *not* `TimeDayDidChange`. See [reputation.md](reputation.md). Means reputation refresh lags the OpsController daily cascade by up to 5 game seconds.
- `OpsController.UpdatePerformance → industry._perfHist` is read by `ReputationTracker` for the Freight component of reputation: [reputation.md § CalculateFreightPerformance](reputation.md#calculatefreightperformance).
- `RebuildPopulations` runs before per-industry `UpdatePerformance` inside the same `OpsController.DayDidChange` call.

### To Passengers & Timetable

- `PassengerStop.Loop` 3 real-second cadence + 300 game-second `GrowWaiting` interval: documented above and in [passengers-timetable.md § PassengerStop loop](passengers-timetable.md).
- `PassengerExpiration` subscribes to `TimeAdvanced` (host-only) — passengers expire after 4 game hours of waiting: [passengers-timetable.md § PassengerExpiration](passengers-timetable.md).

### To Time & Weather

- `TimeObserver` 1-real-second poll → `TimeDayDidChange`/`TimeHourDidChange`/`TimeMinuteDidChange`: [time-weather.md](time-weather.md).
- `TimeAdvanced` is fired *only* on `SetTimeOfDay` message receipt (`StateManager.cs:825`) — not on natural time progression. The polling-based `TimeObserver` is what drives day/hour/minute edges during normal play.

### To Tile Loading & Bardo

- Bardo cars are **invisible to industry ticks** because they're not in `TrainController.Cars`: [tile-loading-bardo.md § Bardo](tile-loading-bardo.md).
- `InterchangedIndustryLoader.OrderCars` queues `ReturnFromBardoOrder` for ~23h-out cars, realized at next interchange service.

### To Save / Load

- Industry KVO snapshot via `IndustryStorageHelper.RegisterPropertyObject`: [save-load.md](save-load.md).
- `[NonSerialized]` `Interchange.Orders` are lost across save/load — regenerate next service tick.
- `OpsController.PostRestoreProperties` runs synchronously during load before any IC has its first Service: documented above.
- Industry `_tickCoroutine` is restarted in `OnEnableWithProperties` post-restore — first Service is 5–20 real seconds after load.
