# Wear & Durability — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Couplers](couplers.md)

The wear/durability system tracks per-car `Condition` (0..1), accumulates wear from movement and oil starvation, gates maximum repair via the `RepairCap` (a function of mileage since last overhaul), inflicts collision/curve/derailment damage, and surfaces hotboxes when oil runs out. The whole thing is host-authoritative; clients consume KVO updates. The "Wear & Tear" toggle in Company Settings is a single global static (`Car.WearFeature`) that gates **only** the per-mile movement wear — collision, curve-overspeed, and derailment damage all bypass it.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Car.WearFeature` (static bool) | `Model/Car.cs:240` | Master toggle. `true` = wear active. Bypass-aware: see "Toggle bypasses" |
| `Car.ApplyConditionDelta(float delta)` | `Model/Car.cs:2191` | Public damage entry; **not gated by WearFeature** |
| `Car.SetCondition(float)` | `Model/Car.cs:2207` | Authoritative setter; writes `_condition` KVO key |
| `Car.BankOdometer()` | `Model/Car.cs:2077` | Per-tick wear + oil consumption + hotbox roll. **Host-only** |
| `Car.WearForMovement(float km)` | `Model/Car.cs:2119` | The one place `WearFeature` short-circuits |
| `RepairTrack.Service(IIndustryContext)` | `Model.Ops/RepairTrack.cs:123` | Industry tick that repairs cars and bills wages |
| `GameStorage.WearFeature` | `Game.State/GameStorage.cs:347` | KVO-backed setting (key `wearFeatre`, sic) |

---

## Toggle spine: how `WearFeature` propagates

```
GameStorage["wearFeatre"]                    ← stored on _game KVO (typo intentional)
   │  setter (UI: SettingsPanelBuilder.BuildFeatureWear)
   │  observer: GameStorage.ObserveWearFeature
   ▼
StateManager.OnPropertiesDidRestore           ← StateManager.cs:292-299
   │  Car.WearFeature = value
   │  Car.OilFeature  = WearFeature && OilFeature   ← OilFeature implies WearFeature
   ▼
Car.WearFeature  (static, process-global)     ← Model/Car.cs:240
```

**Storage facts:**
- KVO object id: `_game`. Key: `"wearFeatre"` (note: missing 'u', this is the on-disk key — do not "fix" it).
- Default: `true` (`GameStorage.cs:351` `BoolValueOrDefault(true)`).
- Per-property auth: `_game` writes go through `GameStorage : IPropertyAccessControlDelegate` — `WearFeature` is not in any special prefix list, so it follows the default (`MinimumLevelTrainmaster` is the typical setting; verify against `GameStorage.AuthorizationRequirementForPropertyWrite` if patching).
- Multiple companion settings ride the same observer fan-out: `OilFeature` (`oilPrevMaintFeature`), `OverhaulMiles` (`overhaulMi`, default 2500 mi), `WearMultiplier` (`wearMult`, 0.1..5×), `OilUseMultiplier` (`oilUseMult`).

**UI:**
- `UI.CompanyWindow/SettingsPanelBuilder.cs:203-243` `BuildFeatureWear` — toggle + sliders. Toggling `OilFeature` triggers `RequestSaveReopen` modal ("Please save and reopen").

### Gates that read `Car.WearFeature`

Only **two** sites consult the static directly:

1. `Car.WearForMovement(float kilometers)` — `Model/Car.cs:2121` — early-return 0 if false.
2. `Car.RepairCap` getter — `Model/Car.cs:889` — early-return 1f if false (so RepairTrack can always max-repair).

Plus a UI conditional in `BuilderExtensions.AddRepairDestination` (`UI.CompanyWindow/BuilderExtensions.cs:170`) that hides the "overhaul" repair option when wear is off.

### Toggle bypasses (HIGH-VALUE FINDINGS)

The following damage paths run **regardless of `Car.WearFeature`**:

| Path | Site | Notes |
|---|---|---|
| Curve overspeed damage | `Car.ApplyCurvatureToModel` → `ApplyConditionDelta((-num3) * dt)` (`Car.cs:2174`) | Uses `TrainMath.DamageForSpeed` |
| Derailed-while-rolling damage | same method, second branch (`Car.cs:2181`) | Damage proportional to speed while derailed |
| Collision damage | `TrainController.IntegrationSetCarsDidCollide` → two `ApplyConditionDelta` calls (`TrainController.cs:1208-1213`) | Driven by `Config.damageForCollisionMph` curve. See [Couplers › collision damage](couplers.md#collision--coupling-damage-pipeline) |
| Direct external `SetCondition`/`ApplyConditionDelta` calls | Anywhere | Public methods, no guard |

`OdometerService` and `OdometerActual` are **always** incremented in `BankOdometer` (no `WearFeature` check on the increment itself) — only the wear *applied* from the new mileage is gated. Mileage accumulates while wear is off; turning wear back on does not retroactively damage cars.

---

## `Model.Car` (per-car wear state)

Per-vehicle MonoBehaviour. Holds the wear-relevant state and exposes the public API for both reading condition and applying damage.

### State fields

```csharp
public static bool  WearFeature       = true;         // 240
public static bool  OilFeature        = true;         // 242
public static int   OverhaulMiles;                    // 244
public static float WearMultiplier    = 1f;           // 246
public static float OilUseMultiplier  = 1f;           // 248

private float _condition  = 1f;                       // 382
public  bool  debugCondition;                         // 384  (DebugGUI graphs)
private float _hotbox;                                // 390
private float _derailment;                            // 392
private float _derailmentDisplay;                     // 394
private float _oiled = 1f;                            // 398
private float _unbankedOdometerService;               // 402
private float _unbankedOdometerActual;                // 404
```

### KVO-backed properties (HostOnly, prefix `_` or `oiled`/`hotbox`)

```csharp
public  float Condition           { get; }            // 777, returns 1f if !EnableCondition
public  bool  HasHotbox           => _hotbox > 0.001f;
public  bool  IsDerailed          => Mathf.Abs(_derailment) > 0.001f;
public  bool  EnableOiling        { get; }            // 793, false for diesels
public  bool  NeedsOiling         { get; }
public  float Oiled               => _oiled;
public  float OdometerService     { get; private set; } // KVO key "_odosvc"
public  float OdometerActual      { get; private set; } // KVO key "_odometer"
public  float LastOverhaulOdometer{ get; set; }       // KVO key "_lastOverhaul"
public  float OverhaulProgress    { get; set; }       // KVO key "_overhaulProg"
public  float RepairCap           { get; }            // 885, returns 1f if !WearFeature
public  bool  EnableCondition     => true;            // 775 — always true; legacy hook
```

`Condition` reads `_condition` directly via the `Control.Condition` KVO observer at `Car.cs:1696`. Clients see condition updates as soon as the host writes the `_condition` key.

### Damage application

```csharp
public void ApplyConditionDelta(float delta)          // 2191
public void SetCondition(float condition)             // 2207
public void OffsetOiled(float oil)                    // 1369
private void SetOiled(float oil)                      // 1374 — gated by EnableOiling
```

`ApplyConditionDelta` clamps and starts a 0.5s coalescing coroutine (`UpdateConditionAfterDelay`) that calls `SetCondition` once, which writes the `_condition` KVO key. **`ApplyConditionDelta` does NOT check `WearFeature`** — it's a raw mutator.

### Derailment

```csharp
public void ApplyDerailmentForce(float force, string reasonFormat, params object[] formatParams)  // 2304
public void ApplyDerailmentDelta(float delta, string reasonFormat = "", params object[] formatParams) // 2318
```

`ApplyDerailmentForce` thresholds force at `weight*5` (no-op below) and lerps to `weight*20` for full derailment. `ApplyDerailmentDelta`:
- Forces a minimum delta of 0.15 on the *first* derailment event (so a borderline force still derails).
- Sends `Messenger.Default.Send(default(CarDidDerail))` (the [`Game.Events.CarDidDerail`](#related-messengerkvo-events) struct, **payload-free**).
- When `_derailment` crosses 0.25 upward, calls a local `BreakConnections(end)` helper that severs both couplers and air on both ends. See [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths).
- 0.3s coalescing via `UpdateDerailmentAfterDelay` writes the `_derailment` KVO key.

### Per-tick wear loop

```csharp
protected virtual void FireOnMovement(MovementInfo info)         // 2053
{
    _unbankedOdometerActual += info.Distance;
    _unbankedOdometerService += ServiceMetersFromActual(info.Distance);
    if (_unbankedOdometerActual > 500f)  BankOdometer();
    foreach (var l in _movementListeners) l.CarDidMove(info);
}

protected virtual float ServiceMetersFromActual(float meters)    // 2067
    => meters * Config.serviceDistanceConditionMultiplier.Evaluate(Condition);

private void BankOdometer()                                       // 2077  HOST-ONLY
{
    if (!StateManager.IsHost) return;
    float kmActual  = _unbankedOdometerActual  / 1000f;
    float kmService = _unbankedOdometerService / 1000f;
    OdometerActual  += kmActual;
    OdometerService += kmService;
    SetCondition(_condition - WearForMovement(kmService));
    OffsetOiled(0f - OilUseForMovement(kmService));
    CheckForHotbox(kmActual);
    _unbankedOdometerActual = 0f;
    _unbankedOdometerService = 0f;
}

private float WearForMovement(float kilometers)                   // 2119
{
    if (!WearFeature) return 0f;                                  // ← THE TOGGLE GATE
    float w = Config.wearPerMileForCondition.Evaluate(_condition) / 100f;
    if (EnableOiling)
    {
        w *= Config.wearMultiplierForOil.Evaluate(_oiled);
        if (HasHotbox) w += Config.hotboxWearPerMileForSpeed.Evaluate(VelocityMphAbs) / 100f;
    }
    return w * 0.6213712f * kilometers * WearMultiplier;
}
```

`BankOdometer` is the *only* place mileage and per-mile wear flow into condition. Patching `BankOdometer` postfix lets you observe every mileage tick.

`ServiceMetersFromActual` multiplies actual meters by `Config.serviceDistanceConditionMultiplier.Evaluate(Condition)` — damaged cars accumulate "service miles" faster than actual miles. This is the hook for "running damaged cars consumes overhaul budget faster."

### Hotbox

```csharp
private void CheckForHotbox(float distanceKm)                     // 2093
```

Skips if `HasHotbox || !EnableOiling || IsLocomotive || VelocityMphAbs < 15f`. Rolls `Config.hotboxChanceForOil.Evaluate(_oiled)` per 100mi. Sets `ControlProperties[PropertyChange.Control.Hotbox] = 1` (KVO key `"hotbox"`).

`HotboxEffect.UpdateForHotbox` (`RollingStock/HotboxEffect.cs:88`) consumes the KVO observer for VFX/light. `Model.AI/AutoHotboxSpotter.cs` is the AI engineer's spotter routine that surfaces hotboxes to the AI.

### Oil

```csharp
public  void OffsetOiled(float oil)                               // 1369
private void SetOiled(float oil)                                  // 1374
public  bool EnableOiling                                          // 793
   => OilFeature && Archetype != CarArchetype.LocomotiveDiesel;
public  bool NeedsOiling => EnableOiling && _oiled < 0.999f;
```

`SetOiled` no-ops when `!EnableOiling` — diesels are exempt and so is anyone with `OilFeature` off. `RequestOilCar` (`Game.Messages/RequestOilCar.cs`, `MinimumAccessLevel(AccessLevel.Crew)`) is the client→host path used by `OilPointPickable.Bank` (`RollingStock/OilPointPickable.cs:181`).

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.ApplyConditionDelta(float)` | Single chokepoint for all damage — including the WearFeature-bypassing paths. Prefix to veto specific damage sources, postfix to log/emit events. |
| `Car.SetCondition(float)` | Final write before KVO. Patch here to enforce a floor (e.g. min 0.1) or to add wear-cap modifiers. |
| `Car.BankOdometer()` | Per-tick wear+oil+hotbox tick; postfix to add mod-side wear contributions in lockstep with vanilla. |
| `Car.WearForMovement(float)` | Replace the wear curve. Cleaner than patching `BankOdometer` if you only need to change formula. |
| `Car.ServiceMetersFromActual(float)` | Modify how condition affects "service mileage" accumulation (inverse of the overhaul-deadline curve). Virtual — overridable in subclasses. |
| `Car.CheckForHotbox(float)` | Tweak hotbox spawn logic; prefix to disable conditionally. |
| `Car.ApplyDerailmentForce` / `ApplyDerailmentDelta` | Intercept derailments. The first call also fires `CarDidDerail` Messenger event — listen there if you only need the notification. |
| `RepairTrack.Service` / `RepairTrack.TickCar` | Modify repair throughput, cost, or completion criteria. |
| `RepairTrack.RepairCapForKilometersSinceOverhaul(float)` | Replace the overhaul-mileage→cap curve (vanilla: 5 levels of 10% each, capped at 50% drop). |

### MP authority

- All wear KVO keys are HostOnly (`_condition`, `_derailment`, `_odometer`, `_odosvc`, `_lastOverhaul`, `_overhaulProg`, `oiled`, `hotbox`). See `Car.HostPrefixes` (`Car.cs:467`): `["_", "ops.passengerMarker", "owned", "oiled", "hotbox"]`.
- `Car.AuthorizationRequirementForPropertyWrite` (`Car.cs:3112`) is the per-key resolver. `IPropertyAccessControlDelegate` implementation.
- Damage application methods (`ApplyConditionDelta`, `SetCondition`, `BankOdometer`'s callers, `ApplyDerailmentForce/Delta`) are gated either by `StateManager.IsHost` checks at the call site or by being *only* invoked from host-side code paths. Examples:
  - `ApplyCurvatureToModel` → `if (!StateManager.IsHost || !EnableCondition) return;` (`Car.cs:2158`).
  - `BankOdometer` → `if (StateManager.IsHost)` early gate (`Car.cs:2079`).
  - `IntegrationSetCarsDidCollide` → `if (!IsHost) return;` (`TrainController.cs:1193`).
- **No request-message infrastructure exists for damage**. Clients cannot directly damage cars. If your mod needs client-driven damage, you must define a request message and handle it host-side. Compare to `RequestOilCar` for the template.

### Related Messenger / KVO events

| Event | Type | Where |
|---|---|---|
| `Game.Events.CarDidDerail` | Messenger struct (empty payload) | Sent in `Car.ApplyDerailmentDelta` on first derailment |
| KVO `_condition` | float, observed at `Car.cs:1696` | Updates material wear shader |
| KVO `_derailment` | float, observed at `Car.cs:1701` | Resets at-rest if change > 0.01 |
| KVO `oiled` | float, observed at `Car.cs:1709` | Mirror to `_oiled` field |
| KVO `hotbox` | int (0/1), observed via `ControlProperties.Observe(PropertyChange.Control.Hotbox, …)` | Used by `HotboxEffect` and `AutoHotboxSpotter` |
| KVO `_odosvc`, `_odometer`, `_lastOverhaul`, `_overhaulProg` | float, host-written | UI subscribers in `BuilderExtensions.AddMileageField` |

### Gotchas

- **`Car.EnableCondition` is hardcoded `true`** (`Car.cs:775`). It looks like a hook but isn't wired to anything user-facing. Don't rely on overriding it — patch `Condition` directly.
- **`debugCondition` is a public bool field**, not a property. When set, `BankOdometer`'s curve-damage path emits `DebugGUI` graphs ("dmg", "dps") for that car. Useful for dialing in custom curves.
- **OdometerService keeps climbing while wear is off.** `ServiceMetersFromActual` always runs and the increment in `BankOdometer` is unconditional. Only `WearForMovement` and `RepairCap` bail out. Toggling wear back on after extended sandbox use can immediately strand cars at low `RepairCap` because `OdometerService - LastOverhaulOdometer` may exceed several overhaul intervals.
- **Damage events coalesce.** `ApplyConditionDelta` and `ApplyDerailmentDelta` start coroutines (`UpdateConditionAfterDelay` 0.5s, `UpdateDerailmentAfterDelay` 0.3s) that batch the KVO write. If you need every individual delta, instrument `ApplyConditionDelta` itself, not the KVO observer.
- **`SetOiled` is private but `OffsetOiled` is public.** `OffsetOiled(0f)` is a useful no-op probe; for *direct* writes use `OffsetOiled(targetValue - car.Oiled)`.
- **`Condition` getter returns `1f` if `EnableCondition` is false.** Since `EnableCondition` is currently always true, this branch is dead, but if a subclass overrides it (it's `public bool EnableCondition` not `virtual`, so you'd need a Harmony getter prefix), `RepairTrack.NeedsRepair` will see the car as fully repaired.
- **Hotbox is an int 0/1** under `Control.Hotbox` (`PropertyChange.cs:31, 172`). It's set via `ControlProperties[Control.Hotbox] = 1` (and cleared with `null` in `RepairTrack.TickCar`). No threshold; binary state.
- **`/repair` console command** (`UI.Console.Commands/RepairCommand.cs`) calls `SetCondition(1f)` on every coupled car. Host-only and sandbox-only. Doesn't reset `LastOverhaulOdometer` — repair cap stays clamped after.
- **`_oiled` clamps to [0,1]** in `SetOiled`; you cannot over-oil. `OffsetOiled(0.5f)` on a car at `0.7` results in `1.0`, not `1.2`.
- **`Car.OverhaulProgress`** is stored as `Null` when zero (`Car.cs:863`). Use `KeyValueObject["_overhaulProg"]` directly only if you handle `IsNull`.

### Init order

`StateManager.OnPropertiesDidRestore` (`Game.State/StateManager.cs:271`) wires the `Car.WearFeature = value` observer **after** `_storage` is constructed. Cars created before that observer fires will see the default `Car.WearFeature = true`. If you patch to subscribe earlier, account for `_game` KVO not yet existing.

---

## `Model.Ops.RepairTrack` (industry-side repair)

Industry component representing a repair facility. Run from `IndustryComponent.Service(IIndustryContext)` on the daily/hourly ops tick.

### Key methods

```csharp
public  override void Service(IIndustryContext ctx)               // 123
public  override void DailyPayables(GameDateTime now, IIndustryContext ctx) // 89
public  void HandleSetMultiplier(float multiplier)                // 170
private bool TickCar(IIndustryContext ctx, Car car, float repairAvailable, out float repairUsed) // 267
private static bool NeedsRepair(Car car)                          // 233
private static bool InForOverhaul(Car car)                        // 254
internal static void CalculateRepairStep(...)                     // 304
internal static float CalculateRepairWorkOverall(Car car)         // 333
public  static float RepairCapForKilometersSinceOverhaul(float km) // 407
private static float NormalizedCostValue(Car car)                 // 389  (per car repair speed)
```

### State

- `RateState` lives on the *Industry* KVO under `subIdentifier + "-rate"` — sub-keys `payRate`, `paidCurr`, `payDue`. Set via `SetRepairMultiplier` message (`Game.Messages/SetRepairMultiplier.cs`).
- `RepairCapForKilometersSinceOverhaul`: `floor(kmSinceOverhaul * 0.6213712 / OverhaulMiles)` levels × 10% drop, max 50% drop.
- Repair throughput: `EquipmentRepairSpeed(car) = Config.repairSpeedForNormalizedCost.Evaluate(NormalizedCost)` where `NormalizedCost = (BasePrice - 1000) / 34000` clamped to ≥0.
- Wages: `payPerRepairUnit = 50 * payRateMultiplier / (1 + repairBonus)`; `repairPerDay = (1 + repairBonus) * payRateMultiplier`.
- Repair parts: `RepairPartLbsPerRepairUnit = 12000f`. Consumed from `repairPartsLoad`.
- Overhaul detection: `InForOverhaul(car)` checks `OverrideDestination.Repair` tag == `"overhaul"`. Tenders inherit from coupled engine.
- Pre-overhaul minimum: `NeedsMinimalRepairBeforeOverhaul` requires condition ≥ 0.5 before overhaul work begins (`RepairTrack.cs:394`).

### Patch candidates

| Method | Why patch |
|---|---|
| `RepairTrack.NeedsRepair` (private static) | Decide what counts as needing repair — mod-added damage types should appear here. |
| `RepairTrack.TickCar` | Per-car per-tick repair logic — patch to add side-effects on completion. |
| `RepairTrack.CalculateRepairStep` | The repair-rate curve. Replace for custom repair economy. |
| `RepairTrack.RepairCapForKilometersSinceOverhaul` | Cap policy. Currently 5 levels of 10% each. |
| `Car.RepairCap` getter | Apply cap modifiers (e.g., loading pen for overweight cars). Note: short-circuits to 1f if `!WearFeature`. |
| `RepairTrack.OverhaulWorkRemaining` | Modify how much work an overhaul actually represents. |

### MP authority

- `RepairTrack.HandleSetMultiplier` calls `StateManager.AssertIsHost()` (`RepairTrack.cs:172`).
- The UI sends `SetRepairMultiplier` request message (`StateManager.ApplyLocal(new SetRepairMultiplier(...))` at `RepairTrack.cs:439`); auth is `StateManager.HasTrainmasterAccess`.
- `Service` and `DailyPayables` run only on the host (industry tick is host-driven).

### Gotchas

- `EnumerateCarsActual(ctx)` filters via `TrainController.CarForId`, so cars present in the industry context but not in the live `TrainController` will silently drop.
- `CheckForCompletelyRepairedCars` calls `item.SetCondition(repairCap)` directly — bypasses any wear-toggle gate (good — repair always works). But it also calls `SetOverrideDestination(OverrideDestination.Repair, null)` which clears the routing.
- Repair pay deferral: `RateState.PayDue` accumulates; `DailyPayables` collects it. If the company can't afford wages, `PaidCurrent` flips false and `EffectiveRepairPerDayPerCar` returns 0 — the shop "closes for the day."
- `OverhaulWorkRemaining`'s `num2` term clamps shareOfFullOverhaulWork at `min(1, kmRemaining / OverhaulMiles)`; very-fresh overdue cars get partial overhaul work.

---

## `Model.Physics.TrainMath` (damage formulas)

Static math library. Pure functions; no state.

```csharp
public static float DamageForSpeed(float velocityMps, float limitMps)            // 178
public static float DerailmentForSpeedOnCurve(float velocityMps, float limitMps) // 187
public static float MaximumSpeedMphForCurve(float curveDegrees, float equipmentLimit) // 139
public static float MaximumSpeedMphForCurve(float curveDegrees)                  // 149
```

`DamageForSpeed` returns `InverseLerp(limit + 1.34, limit + 2.68, velocity) * 0.025` — i.e. zero until 1.34 m/s over, 0.025 (per-tick scaled by dt) at 2.68 m/s over. `DerailmentForSpeedOnCurve` returns `InverseLerp(limit + 2.68, limit + 7.15, velocity) * 0.1`. Constants are `DamageOver = 1.3411179`, `DerailmentOver = 2.6822357`.

These are the damage *rates* used by `Car.ApplyCurvatureToModel`. Patch here to change curve-overspeed sensitivity globally.

---

## `Game.State.GameStorage` & `StateManager` (settings plumbing)

```csharp
public bool         WearFeature       { get; set; }                         // GameStorage.cs:347
public bool         OilFeature        { get; set; }                         // 359
public int          OverhaulMiles     { get; set; } = 2500                  // 383, key "overhaulMi"
public float        WearMultiplier    { get; set; } = 1f                    // 395, key "wearMult"
public float        OilUseMultiplier  { get; set; } = 1f                    // 407, key "oilUseMult"
public IDisposable  ObserveWearFeature(Action<bool>, bool observeFirst=true) // 542
public IDisposable  ObserveOilFeature(Action<bool>)                          // 550
```

KVO object: `_game`. Default values: `WearFeature=true`, `OilFeature=true`, `OverhaulMiles=2500`, `WearMultiplier=1`, `OilUseMultiplier=1`.

`StateManager.OnPropertiesDidRestore` wires observers (`StateManager.cs:271-311`); patches that need to run before this point should subscribe in `Awake` of a `[StateRequiredOnLoad]` MonoBehaviour or use `Messenger` for `PropertiesDidRestore`.

---

## `Model.Config` curves (tuning surface)

`Model/Config.cs` (singleton via `Config.Shared`). Wear-relevant `AnimationCurve` fields:

```csharp
public AnimationCurve damageForCollisionMph;                               // 58
public AnimationCurve serviceDistanceConditionMultiplier;                  // 66 default flat 1
public AnimationCurve wearPerMileForCondition;                             // 69 default 0.01
public AnimationCurve wearMultiplierForOil;                                // 72 default 2→1
public AnimationCurve oilUsePerMileForCondition;                           // 75 default 0.01
public AnimationCurve hotboxChanceForOil;                                  // 81
public AnimationCurve hotboxWearPerMileForSpeed;                           // 84
public AnimationCurve workPerPercentForConditionSteam;                     // 87 default 0.03→0.01
public AnimationCurve workPerPercentForCondition;                          // 89
public AnimationCurve repairSpeedForNormalizedCost;                        // 91 default 1→0.3
```

The shipped curves come from a ScriptableObject asset; the inline defaults above are fallbacks, not the live values. `Car.Config = Config.Shared` is set in `Car.Awake` (`Car.cs:932`).

To replace curves: assign new `AnimationCurve` to `Config.Shared.<field>` in a `[HarmonyPatch]` that runs after `Config.Shared` initializes. Because curves are reference-typed, mods can mutate keys in place too — but other mods may cache the curve, so prefer assignment.

---

## Cross-references to Couplers

- Coupler/uncouple events that fire wear damage: see [Couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline).
- `Car.ApplyDerailmentDelta` auto-uncoupling at threshold 0.25: see [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths).
- Slack accounting and the in-train-force input that feeds collision damage: see [Couplers › slack & integration](couplers.md#slack-state--integration).
