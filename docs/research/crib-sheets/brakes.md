# Brakes — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Air System](air-system.md), [Anglecock & Hose](anglecock-hose.md), [Couplers](couplers.md), [Wear & Durability](wear-durability.md)

Railroader has **four** brake types: train brake (automatic, brake-pipe driven), independent / locomotive brake (engine-only direct cylinder), hand brake (per-car mechanical), and… **no dynamic brake** (despite the user's intuition — `grep` for it returns zero hits). Brake control flows uniformly through the `PropertyChange` KVO key system: input → `SendPropertyChange` → KVO write → KVO observer on host → `air.<setting>` mutation → consumed by `CarAirSystem.FixedUpdateAir` next physics tick. The actual physical force on the car comes from `Car.CalculateBrakingForce(brakePercent, velocity)`, called by `IntegrationSet.CalculateRetardingForce` per tick. Bail-off is implemented as a sentinel value (`locomotiveBrakePressure < 0`) on the loco-brake setter.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `PropertyChange.Control` enum | `Game.Messages/PropertyChange.cs:12` | All brake controls: `TrainBrake`, `LocomotiveBrake`, `Handbrake`, `Bleed`, `CutOut`, `Mu` |
| `Car.SendPropertyChange` | `Model/Car.cs:3076-3092` | Single client→host control write. `StateManager.ApplyLocal(new PropertyChange(...))` |
| `LocomotiveControlHelper` | `Model/LocomotiveControlHelper.cs` | Scripted/AI-friendly facade over the same KVO writes |
| `CarPropertyChanges.SetHandbrake(this Car, bool)` | `Model/CarPropertyChanges.cs:8` | Extension wrapping `SendPropertyChange(Handbrake, bool)` |
| `Car.CalculateBrakingForce(brakePercent, absVelocity)` | `Model/Car.cs:2991` | Per-tick retarding force from `air.brakePercent` |
| `CarAirSystem.UpdateBrakingForce` / `CalculateTargetBrakePercent` | `Model.Physics/CarAirSystem.cs:251-268` | `brakePercent` lerps to target from cylinder pressure |
| `LocomotiveAirSystem.UpdateLocomotiveBrakeControlLine` | `Model.Physics/LocomotiveAirSystem.cs:170` | Independent + bail-off vs train brake max-out logic |
| `LocomotiveAirSystem.OnResetBailOff` event | `Model.Physics/LocomotiveAirSystem.cs:72` | Fires when bail-off is applied; host writes loco brake KVO back to 0 |
| `BrakeStandController` (visual) | `RollingStock/BrakeStandController.cs` | "26L" vs "6ET" prefab-swap; not gameplay-load-bearing |
| `BrakeAnimator` | `RollingStock/BrakeAnimator.cs` | Per-truck shoe animation via `Car.UpdateBrakeApplied(bool)` |

---

## Brake input → physics chain

```
Input (keybind / pickable / cab control / AI / script)
   │
   ▼ Car.SendPropertyChange(Control.TrainBrake, 0..1)
   │  → StateManager.ApplyLocal(new PropertyChange(carId, key, FloatPropertyValue))
   │  → KVO write on the loco's KeyValueObject
   │
   ▼ KVO observer (BaseLocomotive.ObserveCoreProperties, BaseLocomotive.cs:382-396)
   │  locomotiveControl.TrainBrakeSetting = value
   │  └─ setter writes air.trainBrakeSetting (LocomotiveControlAdapter.cs:34-44)
   │     └─ derives air.trainBrakePressure = (1 - setting) * brakeFeedValvePressure
   │
   ▼ Per-tick: TrainController.FixedUpdate (TrainController.cs:419-454)
   │  for each car: car.air.FixedUpdateAir(dt)
   │     ├─ UpdateBrakeLine(dt)                — brake-pipe propagation across set
   │     ├─ UpdateAir(dt) ×2                   — local triple-valve / vented-valve sim
   │     │     LocomotiveAirSystem override:
   │     │       UpdateCompressor → MainReservoir.Pressure
   │     │       _mainReservoirToBrakeCylinder.ValveVent (loco brake → cylinder)
   │     │       _mainReservoirToBrakeLine.ValveAutomaticBrake (feed valve → brake line)
   │     │     CarAirSystem (regular cars):
   │     │       triple-valve sense via brakeLine vs brakeReservoir
   │     │       BrakeReservoir → BrakeCylinder transfer (apply)
   │     │       BrakeCylinder → outside vent (release)
   │     │       BrakeLine → BrakeReservoir refill
   │     ├─ UpdateBrakingForce — brakePercent lerps toward target (cylinder/64)
   │     └─ UpdateNeedsSend — flag for client broadcast (BatchCarAirUpdate)
   │
   ▼ IntegrationSet.Tick → ApplyBrakes (Model.Physics/IntegrationSet.cs:416)
   │  CalculateRetardingForce(element, absVelocity)
   │  → car.CalculateBrakingForce(car.air.brakePercent, absVelocity)
   │  → braking force enters Verlet integration as per-element acceleration
```

**Tick order matters.** All cars' air sim runs first (the for-loop at `TrainController.cs:426-437`), THEN every integration set ticks. So within a single frame: brake-pipe propagation is computed for the whole world, then physics integrates with the new `brakePercent`. There is no per-car sequencing within `FixedUpdateAir`.

---

## `PropertyChange.Control` — the brake control names

```csharp
// Game.Messages/PropertyChange.cs:12-32
public enum Control {
    Throttle, Reverser,
    LocomotiveBrake,        // key "locoBrake"   — independent brake
    TrainBrake,             // key "trainBrake"  — automatic
    Horn, Bell,
    Handbrake,              // key "handbrake"
    Bleed,                  // key "bleed"       — write 1 to drain BC
    Compressor,             // key "compressor"  — host-written status
    CutOut,                 // key "cutOut"      — bool, isolates loco from train brake
    Idle, Headlight,
    BrakeStyle,             // key "brakeStyle"  — string "26L"/"6ET" (visual only)
    Condition, Derailment, Mu, CylinderCock, Hotbox
}
```

All controls share the wire format `PropertyChange { ObjectId=carId, Key=string, Value=IPropertyValue }`. There is **no dedicated brake message type** — brakes ride the generic `PropertyChange`. The `SetGladhandsConnected` and `BatchCarAirUpdate` messages are the only air-related custom message types.

### Auth on brake KVO keys

`Car.AuthorizationRequirementForPropertyWrite(key)` at `Car.cs:3112`. None of the brake control keys (`trainBrake`, `locoBrake`, `handbrake`, `bleed`, `cutOut`) have a `_` prefix, so they default to `MinimumLevelCrew` + the per-car `trainCrewId` check. Crew with the appropriate train crew assignment can write. The `_compressor`-like host-mirror keys are not gated — `compressor` itself has no underscore but is only written from `BaseLocomotive.UpdateCabControls` host-side (`BaseLocomotive.cs:506`).

---

## Train Brake (automatic / brake-pipe-driven)

The 26L-style brake handle. Setting (0..1) maps to `trainBrakePressure` (0..90 PSI inverse): full release = 90 PSI in the pipe, full application = 0 PSI in the pipe.

### Setter chain

```csharp
// RollingStock/LocomotiveControlAdapter.cs:34
public float TrainBrakeSetting {
    get => air.trainBrakeSetting;
    set => air.trainBrakeSetting = value;
}

// Model.Physics/LocomotiveAirSystem.cs:42
public float trainBrakeSetting {
    get => 1f - trainBrakePressure / brakeFeedValvePressure;
    set => trainBrakePressure = (1f - value) * brakeFeedValvePressure;
}
```

`brakeFeedValvePressure` defaults to **90 PSI** (`LocomotiveAirSystem.cs:20`). Public field; mod-overridable per-loco-instance.

### "Lap" pressure & release detection

`LocomotiveAirSystem.UpdateAir` (`LocomotiveAirSystem.cs:82-111`) maintains `_lapTrainBrakePressure` — the historical *lowest* value of `trainBrakePressure` since the last full-release event:

```csharp
if (Mathf.Abs(trainBrakePressure - brakeFeedValvePressure) < 1f)
    _lapTrainBrakePressure = brakeFeedValvePressure;        // released
else
    _lapTrainBrakePressure = Mathf.Min(trainBrakePressure, _lapTrainBrakePressure);
```

The brake-line target pressure passed to the `_mainReservoirToBrakeLine.ValveAutomaticBrake` is `_lapTrainBrakePressure`, **not** the live `trainBrakePressure`. Effect: **partial-release moves of the train-brake handle do not raise pipe pressure** — only full release (within 1 PSI of feed valve) re-pressurizes. This is intentional 26L semantics. Mods that want graduated release must either bypass `_lapTrainBrakePressure` or skip the `ValveAutomaticBrake` call entirely.

### Cut-out

```csharp
public bool IsCutOut;          // LocomotiveAirSystem.cs:38, public field

// Set via PropertyChange.Control.CutOut → BaseLocomotive.cs:392
KeyValueObject.Observe("cutOut", v => locomotiveControl.air.IsCutOut = v.BoolValue);

// Effect: LocomotiveAirSystem.UpdateAir, lines 85-91
if (IsCutOut) {
    locomotiveBrakeSetting = 0f;
    trainBrakeSetting = 0f;
    base.UpdateAir(dt);    // skips compressor, brake-line feed, and loco-brake injection
    return;
}
```

When cut out, the loco behaves as a standard car for the purposes of the air sim — its triple valve responds to brake-pipe changes from another loco. **Cut-out forcibly zeros both `trainBrakeSetting` and `locomotiveBrakeSetting`** every tick: any UI or script that fights cut-out will lose. There's an MU-aware path at `LocomotiveAirSystem.cs:142-149` where a cut-out + MU-enabled loco falls through to its MU source loco's air system for deferral checks.

### Brake-pipe set physics

See [Air System › brake-pipe propagation](air-system.md#brake-pipe-propagation-the-key-loop) — the brake pipe is a chain of `Reservoir`s linked by `AirConnection`s, propagated per-tick via `CarAirSystem.UpdateBrakeLine`.

### MP authority

- Crew + train-crew. Not HostOnly. Clients can directly write `trainBrake` via `SendPropertyChange`.
- Host's KVO observer writes `air.trainBrakeSetting`; the host's `FixedUpdateAir` does the actual physics; pressure values then broadcast to all clients via `BatchCarAirUpdate` (HostOnly).

### Patch candidates

| Method | Why patch |
|---|---|
| `LocomotiveAirSystem.UpdateAir` | The full per-tick brake-line + cylinder + lap logic. Replace for custom valve schedules. |
| `LocomotiveAirSystem.brakeFeedValvePressure` (field) | Override per-instance to change full-release pressure. Public field, no setter trampolines. |
| `LocomotiveAirSystem.trainBrakeSetting` setter | Intercept handle moves before `_lapTrainBrakePressure` clamping. |
| `BaseLocomotive.ObserveCoreProperties` (`Car.cs:382-396` is the observer registration) | Catch the KVO observer that turns the wire change into local state. Patching here lets you observe even script/AI-driven changes. |

---

## Locomotive (Independent) Brake — direct cylinder injection

The independent / engineer's brake bypasses the brake pipe and pumps main-reservoir air directly into the locomotive's brake cylinder.

### Range encoding & bail-off sentinel

```csharp
// Model/BaseLocomotive.cs:601-609 — UI/control mapping
private static float LocomotiveBrakeMapToControl(float value)
    => Mathf.InverseLerp(-0.1f, 1f, value);
private static float LocomotiveBrakeMapFromControl(float value)
    => Mathf.Lerp(-0.1f, 1f, value);
```

Setting range is `[-0.1, 1.0]`. **Negative values are the bail-off sentinel.** UI controls (the `RadialAnimatedControl` for `LocomotiveBrake`) map [0..1] visually to [-0.1 .. 1] internally. The negative region (0..0.1 visual) means "request bail-off."

### Bail-off implementation

```csharp
// Model.Physics/LocomotiveAirSystem.cs:170-194
private void UpdateLocomotiveBrakeControlLine() {
    if (locomotiveBrakePressure < 0f) {              // bail-off detected
        _locomotiveBrakeLineMemory = _lapTrainBrakePressure;
        _locomotiveBrakeLineBank = 0f;
        locomotiveBrakeControlLine = 0f;
        locomotiveBrakePressure = 0f;
        this.OnResetBailOff?.Invoke();               // event → host writes KVO back to 0
        return;
    }
    locomotiveBrakePressure = Mathf.Clamp(locomotiveBrakePressure, 0f, maximumLocomotiveBrakePressure);
    float num = Mathf.Clamp(_locomotiveBrakeLineBank
                            + (_locomotiveBrakeLineMemory - _lapTrainBrakePressure) * 2.5f,
                            0f, maximumLocomotiveBrakePressure);
    if (locomotiveBrakePressure > num) {
        locomotiveBrakeControlLine = locomotiveBrakePressure;       // engineer's brake wins
        return;
    }
    locomotiveBrakeControlLine = num;                                 // train-brake-derived loco BC wins
    if (_lapTrainBrakePressure < _locomotiveBrakeLineMemory) {
        _locomotiveBrakeLineBank += num - _locomotiveBrakeLineBank;
        _locomotiveBrakeLineMemory = _lapTrainBrakePressure;
    }
}
```

`OnResetBailOff` is wired host-side in `BaseLocomotive.FinishSetup` (`BaseLocomotive.cs:402-408`):

```csharp
locomotiveAirSystem.OnResetBailOff += delegate {
    base.ControlProperties[PropertyChange.Control.LocomotiveBrake] = 0;
};
```

**The bail-off is one-shot.** Setting `locomotiveBrakeSetting = -0.x` injects the sentinel; one tick later `UpdateLocomotiveBrakeControlLine` clears `_locomotiveBrakeLineMemory` (the "remembered max" of train-brake), zeros `_locomotiveBrakeLineBank` (the rolling accumulated train-brake-induced loco BC pressure), and the host re-writes the KVO key back to 0. This means **a held bail-off control just keeps firing the reset**; the moment the train brake further reduces, the loco BC picks back up unless bailed again.

`PostRestoreProperties` defensively zeros the setting on load:

```csharp
// BaseLocomotive.cs:441-449
if (StateManager.IsHost && locomotiveControl.LocomotiveBrakeSetting < 0f) {
    Log.Warning("Fixing {car} with bailed brake setting...");
    locomotiveControl.LocomotiveBrakeSetting = 0f;
}
```

### `LocomotiveControlHelper.BailOff()`

```csharp
// Model/LocomotiveControlHelper.cs:129
public void BailOff() {
    ChangeValue(PropertyChange.Control.LocomotiveBrake, -0.1f);
}
```

Convenience for AI/script — sends the sentinel value. Used by `AutoEngineer.MaintainSpeed` at `AutoEngineer.cs:806`: `_control.BailOff()` is called when throttle is open AND BC > 0 AND not cut-out.

### Tooltip-side detection

```csharp
// BaseLocomotive.cs:569-581
private string TooltipTextForLocomotiveBrake() {
    if (air is LocomotiveAirSystem { IsCutOut: not false }) return "Locomotive Cut Out";
    float num = LocomotiveBrakeMapFromControl(cabControls.locomotiveBrake.Value);
    return num < 0f ? "Bail-Off" : Percent(num);
}
```

### `_locomotiveBrakeLineBank` & `_locomotiveBrakeLineMemory`

These two fields implement the "**locomotive brake follows train brake but doesn't drop until handle moves**" 26L behaviour:
- `_locomotiveBrakeLineMemory` — the lowest brake-pipe pressure seen "since last bail or release." Drives `num = (_memory - lap) * 2.5`.
- `_locomotiveBrakeLineBank` — cumulative auto-applied loco BC value, persists across train-brake handle re-application until bailed.

So if you make a 10 PSI reduction → loco BC tracks up; reduce another 5 → loco BC tracks further; *release the train brake* → loco BC stays applied (because `flag` clears `_locomotiveBrakeLineMemory` and `_locomotiveBrakeLineBank` only when `lapTrainBrakePressure ≈ feedValvePressure`, see `LocomotiveAirSystem.cs:106-110`). Bail-off forces `_locomotiveBrakeLineBank = 0`.

### MU coordination

`BaseLocomotive.PeriodicUpdateForMu` (`BaseLocomotive.cs:144-164`) runs every 1s on host. If `IsMuEnabled`:
- Cached `_cachedShouldDeferToLocomotiveAir` is updated.
- Throttle and reverser are mirrored from the MU-source loco via `SendPropertyChange`.
- **Brake settings are NOT mirrored.** Each loco's brake handle is independent. Cut-out + MU is the supported "trail unit" pattern: `IsCutOut` makes the trailing loco's brake handle non-functional, and `IsMuEnabled` makes throttle/reverser slave to the lead.
- The MU source is found by `FindMuSourceLocomotive` (`BaseLocomotive.cs:194-200`) which walks both ends through `EnumerationCondition.AirAndCoupled` for the first non-tender, non-cut-out, non-self loco.

### MP authority & KVO

| Key | Wire key | Auth |
|---|---|---|
| Loco brake setting | `locoBrake` | Crew + train-crew |
| Cut-out | `cutOut` | Crew + train-crew |
| MU enable | `mu` | Crew + train-crew |
| Compressor status | `compressor` | Crew + train-crew (but only host writes it) |

### Patch candidates

| Method | Why patch |
|---|---|
| `LocomotiveAirSystem.UpdateLocomotiveBrakeControlLine` | The whole bail / track / bank state machine. Replace to change semantics. |
| `BaseLocomotive.LocomotiveBrakeMapFromControl/MapToControl` (private static) | Tweak the `[-0.1..1]` ↔ `[0..1]` mapping — e.g. expand bail-off region for finer control. |
| `LocomotiveControlHelper.BailOff` | One-line; mods can shadow with their own helper. |
| `LocomotiveAirSystem.OnResetBailOff` event | Subscribe to learn when bail completed. Host-side only. |
| `BaseLocomotive.FinishSetup` (`Car.cs:399-430`) | The OnResetBailOff handler is registered here; patch to change the post-bail KVO write. |

---

## Hand Brake

Per-car mechanical brake. **Does not bypass the air system in the cylinder-pressure sense** — instead it is checked separately in `CarAirSystem.CalculateTargetBrakePercent` and forces a fixed multiplier.

### Wire path

```csharp
// Model/CarPropertyChanges.cs:8
public static void SetHandbrake(this Car car, bool apply)
    => car.SendPropertyChange(PropertyChange.Control.Handbrake, apply);

// Wire key: "handbrake" (PropertyChange.cs KeyMapping)

// KVO observer (Car.cs:1677-1684)
KeyValueObject.Observe("handbrake", v => {
    air.handbrakeApplied = v.FloatValue > 0.5f;            // bool sent as float, threshold 0.5
    if (!air.handbrakeApplied) ResetAtRest();
});
```

### Effect on braking

```csharp
// Model.Physics/CarAirSystem.cs:257-268
private float CalculateTargetBrakePercent() {
    if (handbrakeApplied) return Car.BrakeForceMultiplierHandbrake;     // = 3f
    if (BrakeCylinder.Pressure < 2f) return 0f;
    return BrakeCylinder.Pressure / 64f;
}
```

`Car.BrakeForceMultiplierHandbrake` is **3.0** (`Car.cs:238` static field, public, mod-mutable). Pumped through `Car.CalculateBrakingForce` which multiplies by `nominalBrakingForce` (per-archetype, see [Car.cs:1058-1075](#related-tunable-fields)) and `BrakeForceMultiplier` (= 1f static field). So a hand-braked car has the equivalent of `3 * nominalBrakingForce * curve(velocity) * 4.44822`. **Hand brakes are 3× as strong as a fully-applied air brake** at the same speed.

`Config.brakeForceMultiplierHandbrake` exists in `Model/Config.cs:13` (default 2f) but is **dead code** — `Car.BrakeForceMultiplierHandbrake` is assigned at field-initializer time (3f) and never read from `Config`. Patching `Config.Shared.brakeForceMultiplierHandbrake` does nothing; mutate `Car.BrakeForceMultiplierHandbrake` directly.

### `UpdateBrakeApplied` visual sync

```csharp
// Model/Car.cs:948
bool brakeApplied = air.handbrakeApplied || air.BrakeCylinder.Pressure > 2f;
UpdateBrakeApplied(brakeApplied);
```

Drives `BrakeAnimator.BrakeApplied` (`RollingStock/BrakeAnimator.cs:16-30`) which plays the shoe animation per truck. Toggle is debounced via `_brakeWasApplied`; only the edge transition triggers the animation.

### Pickable hand-brake control

`CarPickable.cs:107-110` adds a context-menu button:

```csharp
shared.AddButton(ContextMenuQuadrant.Brakes,
    car.air.handbrakeApplied ? "Release Handbrake" : "Apply Handbrake",
    SpriteName.Handbrake,
    () => car.SetHandbrake(!car.air.handbrakeApplied));
```

There's also a hand-brake toggle in `CarInspector.PopulateCarPanel` (`CarInspector.cs:181`).

### Place-train auto-handbrake

`PlaceTrainHandbrakes` enum (`Game.Messages/PlaceTrainHandbrakes.cs`) has just two values: `Automatic` and `None`. `TrainController.ApplyHandbrakesAsNeeded` (`TrainController.cs:612-645`):
- `Automatic` calls `CalculateNumHandbrakes(cars)` → 0..3 brakes based on weight & gravity.
- The lead loco gets `LocomotiveBrake = 1f` *first* (counts as one handbrake).
- Then up to 2-3 freight cars (skipping tenders) get hand brakes applied.
- All other cars are explicitly *released* (so re-placing a train always reset stale hand brakes).

`CalculateNumHandbrakes` thresholds:
- Weight cars × 0.0005 (tons), gravity force × 0.0005.
- Count: ≥20 cars → 3 brakes, ≥10 cars → 2, ≥5 → 1, else 0.
- Plus `Mathf.CeilToInt(gravity_lb / 5)` — steep grades demand more.
- Clamped to `[1, 3]`.

### MP authority

- `handbrake` key — Crew + train-crew. Clients can apply/release if assigned to the car's crew.
- `PlaceTrain` message is `MinimumAccessLevel(AccessLevel.Trainmaster)`.

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.BrakeForceMultiplierHandbrake` (static field) | Globally tune hand-brake effectiveness. |
| `CarAirSystem.CalculateTargetBrakePercent` (private) | Replace the `handbrakeApplied → 3f` short-circuit, e.g. for partial hand brake. |
| `TrainController.CalculateNumHandbrakes` (private static) | Change the auto-handbrake heuristic for `PlaceTrain`. |
| `TrainController.ApplyHandbrakesAsNeeded` (private) | Customize the apply-loop (e.g., always brake the rear car). |
| `CarPropertyChanges.SetHandbrake` (extension) | One-line; just mutates the KVO. Patch the observer instead if you need apply/release events. |

### Gotchas

- **`handbrakeApplied` is a bool field, not a KVO key.** The KVO key `"handbrake"` carries a float (0/1 via threshold). The bool lives on `CarAirSystem` and is a stale mirror of the last KVO observer fire. Reading `car.air.handbrakeApplied` is correct; reading `KeyValueObject["handbrake"].BoolValue` may not match because the value is stored as float.
- **`AutoEngineer.HandbrakeApplied(out int)`** counts hand brakes via `CachedCoupled().Count(c => c.air.handbrakeApplied)` (`AutoEngineer.cs:941-945`). The auto engineer treats *any* hand brake on a coupled car as a "pitfall stop" reason (see `AutoEngineerPlanner.cs:929`).
- **Hand brake doesn't affect cylinder pressure.** A hand-braked car still equalizes air normally; the brake percent override happens in `CalculateTargetBrakePercent`. So bleeding a hand-braked car drops its cylinder to 0 but still leaves it hand-braked.

---

## Bleed Valve

Drain the brake cylinder manually. One-shot pulse via the `Bleed` KVO key.

```csharp
// Model/CarPropertyChanges.cs:24
public static bool SupportsBleed(this Car car) => car.Archetype switch {
    CarArchetype.LocomotiveDiesel => false,
    CarArchetype.LocomotiveSteam  => false,
    CarArchetype.Tender           => false,
    _ => true,
};
public static void SetBleed(this Car car)
    => car.SendPropertyChange(PropertyChange.Control.Bleed, value: true);
```

```csharp
// Car.cs:1685-1694 — observer
KeyValueObject.Observe("bleed", v => {
    if (v.FloatValue >= 0.5f) {
        air.BleedBrakeCylinder();                                       // sets bleedBrakeCylinder = true
        if (StateManager.IsHost)
            KeyValueObject.SetDelayed("bleed", Value.Null(), 0.5f);     // auto-clear after 0.5s
    }
});
```

```csharp
// CarAirSystem.cs:107-116, 282-285
public bool bleedBrakeCylinder { get; private set; }     // self-clearing
public void BleedBrakeCylinder() => bleedBrakeCylinder = true;

// In UpdateAir (regular cars only):
if (bleedBrakeCylinder) {
    bool flag = BrakeCylinder.Pressure > 0.1f;
    bleedBrakeCylinder = flag;                            // auto-deactivate when drained
    if (bleedBrakeCylinder) { num = 1; num2 = 1; }       // force both apply+release valves on
}
```

While bleeding, the `num`/`num2` flags are forced to 1, which routes both `ReservoirToCylinder.Equalize` and `CylinderToOutside.Equalize` paths. The cylinder vents to atmosphere; the brake reservoir also drains via the apply-side connection.

**Bleed only works on regular cars.** Locomotives/tenders return false from `SupportsBleed`. Their brake cylinders use the `_mainReservoirToBrakeCylinder` valved path instead (driven by `locomotiveBrakeControlLine`).

### MP authority

- `bleed` key — Crew + train-crew (no `_` prefix).
- The host's auto-clear via `SetDelayed` is the only way the key is normalized back to null.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarAirSystem.BleedBrakeCylinder` | Pre/post hook. Add side-effects like notice on bleed. |
| `CarPropertyChanges.SupportsBleed` | Allow bleeding on tenders/locos. |
| `CarAirSystem.UpdateAir` (specifically the `bleedBrakeCylinder` branch) | Change drain rate. |

---

## Dynamic Brake — **does not exist**

Searched `Railroader-ILSPY/Assembly-CSharp` for `DynamicBrake`, `dynamicBrake`, `dynamic_brake`: **zero hits**. The diesel braking model is straight-air only. There is no throttle/dynamic blending UI, no DB control adapter, no DB property change. This is a notable absence vs. comparable sims (Run8, OR).

If a mod wants to add dynamic braking:
- Add a new `PropertyChange.Control` enum value (or use a custom `PropertyChange` key starting with a non-reserved string).
- Hook into `LocomotiveAirSystem.UpdateAir` to apply braking force outside the air model — e.g., directly mutate `brakePercent` on the loco's `CarAirSystem`. Or write a new retarding term in `IntegrationSet.CalculateRetardingForce`. The cleanest route is patching `Car.CalculateBrakingForce` to add a DB term when `Archetype == LocomotiveDiesel`.
- Mutual exclusion with throttle is *not* enforced anywhere — it would be a mod-side concern.

---

## `Car.CalculateBrakingForce` — the physical hand-off

```csharp
// Model/Car.cs:2991-2998
public float CalculateBrakingForce(float brakePercent, float absVelocity) {
    float time = absVelocity * 2.23694f;                                 // mps → mph
    float num = Config.brakeForceCurve.Evaluate(time);                   // velocity-dependent multiplier
    brakePercent *= Mathf.Lerp(0.8f, 1f, Condition);                    // [0.8 .. 1] from condition
    float num2 = nominalBrakingForce * BrakeForceMultiplier;
    return brakePercent * num2 * num * 4.44822f;                         // lb → N
}
```

- `Config.brakeForceCurve` defaults `AnimationCurve.Constant(0, 60, 0.4)` (`Config.cs:15`) — flat 0.4 from 0..60 mph. The shipped asset overrides this; treat the inline default as a fallback only.
- `Car.BrakeForceMultiplier = 1f` (static, `Car.cs:236`). Globally scales all brake force.
- `Condition` ∈ [0..1]; lerps `brakePercent` to `[0.8 .. 1]`. So a fully wrecked car still brakes at 80% effectiveness — **brakes do not fail with damage** below 80%. This is the link to [wear-durability.md](wear-durability.md).
- `nominalBrakingForce`: per-archetype × `Definition.WeightEmpty` (lbs) × archetype factor (see below).

### `nominalBrakingForce` per archetype

```csharp
// Car.cs:1058-1075
nominalBrakingForce = Archetype switch {
    LocomotiveDiesel => 1f,
    LocomotiveSteam  => 1f,
    Boxcar => 0.7f, Flat => 0.7f, Tank => 0.7f, HopperOpen => 0.7f, Caboose => 0.7f,
    Gondola => 0.7f,
    Tender => 0.8f,
    Coach => 0.9f,
    Baggage => 0.9f,
    _ => 0.7f,
} * (float)Definition.WeightEmpty;
```

So a 100-ton (200,000 lb) boxcar's nominalBrakingForce = `0.7 × 200000 = 140000 lb` and full braking at 1f brakePercent gives `1 × 140000 × 0.4 × 4.44822 ≈ 249 kN`. **Loaded cars do not brake harder than empty cars** — the multiplier uses `WeightEmpty` only, while the gravity/inertia force uses `Weight` (loaded). This is a notable loaded-car brake-fade pitfall.

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.CalculateBrakingForce` | The single force formula. Patch postfix to add custom modifiers (load adjustment, weather, etc.). |
| `Car.SetNominalBrakingRatio` (private) | Per-archetype factor. Patch to use `Definition.WeightFull` instead of `WeightEmpty`. |
| `Car.BrakeForceMultiplier` (static field) | Cheap global tune knob. |
| `Config.brakeForceCurve` | Replace the velocity-multiplier curve. |

---

## DPU / Distributed Power coordination

Not implemented as a first-class concept. The closest analogue is **MU + Cut-out**:

- `IsMuEnabled` on a loco causes its throttle/reverser to mirror the lead loco's settings (`PeriodicUpdateForMu`, 1s tick, `BaseLocomotive.cs:144-164`).
- `IsCutOut` disables the loco's own brake handle and forces it into "regular car" air-sim mode.
- A trailing loco set to `MU=true, CutOut=true` follows throttle/reverser from the lead, brakes via the brake pipe, and contributes tractive effort.

**There is no concept of "remote" placement, of telemetry between distributed locos, or of fence/boundary detection.** A DPU mod would need to:
1. Either define a new `IsDPU` flag and re-implement `PeriodicUpdateForMu` to cross adjacency boundaries, OR
2. Patch `FindMuSourceLocomotive` (`BaseLocomotive.cs:194-200`) to allow lookup beyond the current consist — but the helper uses `set.NextCarConnected` with `EnumerationCondition.AirAndCoupled`, so a separated DPU consist wouldn't be reachable.

**Brake coordination across DPU is therefore brake-pipe-dependent only.** With DPU set up via MU+cutout in a single consist, the trailing loco's brake-pipe sense responds to the lead's brake-pipe reduction normally — but the air-flow rate is governed by [air-system.md](air-system.md)'s `AirConnection.Equalize` per-car, which means a 50-car DPU train still has the same head-to-tail propagation lag as an undistributed 50-car train. There is no "second source" of brake-pipe air from a trailing loco.

This is the principal area where the user's DPU experiment is constrained by vanilla. The relevant patch points:
- `LocomotiveAirSystem._mainReservoirToBrakeLine.ValveAutomaticBrake` is called only on locos that are NOT cut-out and NOT deferring. A DPU mod could selectively *re-enable* the brake-pipe feed valve on a cut-out trailing loco to provide a second source.
- `CarAirSystem.UpdateBrakeLine` walks the consist twice per tick — once each direction — see [Air System › brake-pipe propagation](air-system.md#brake-pipe-propagation-the-key-loop). A DPU mod can piggyback by ensuring the trailing loco's `MainReservoir` feeds its `BrakeLine` directly.

---

## AI engineer brake control (`AutoEngineer`)

The AI runs PID controllers per-control. Brake-relevant ones from `AutoEngineerConfig.cs`:

```csharp
public float fullBrakeSet = 26f;                      // PSI for full-service reduction
public PIDController independentPID;
public PIDController trainBrakePID;
public float brakeErrorPower = 1f;
public AnimationCurve trainBrakeDerivativeGainForNumAirOpenCars;
public float trainBrakeReleaseBelowOutput = -0.01f;
public AnimationCurve applyTimeForNumberOfCars;
public AnimationCurve releaseTimeForNumberOfCars;
public AnimationCurve brakeSetForDeltaVelocityMph;
public AnimationCurve brakeSetMultiplierForVelocityMph;
```

Key control-loop excerpt from `AutoEngineer.cs:785-808`:

```csharp
if (ShouldUseLocomotiveBrake()) {
    float locomotiveBrake = independentPID.Compute(num8, num3);
    _control.LocomotiveBrake = locomotiveBrake;
    _control.TrainBrake = 0f;
} else {
    int count = AirOpenCars().Count;
    trainBrakeController.derivativeGain = _config.trainBrakeDerivativeGainForNumAirOpenCars.Evaluate(count);
    float num9 = trainBrakeController.Compute(num8, num3);
    if (num9 > 0.01f && num9 > _control.TrainBrake) {
        _control.TrainBrake = Mathf.Ceil(num9 * 30f) / 30f;     // quantize to 1/30 increments
    } else if (num9 < _config.trainBrakeReleaseBelowOutput) {
        _control.TrainBrake = 0f;                                 // full release
    }
    if (flag /* throttle nonzero */ && Locomotive.air.BrakeCylinder.Pressure > 0f
        && !Locomotive.locomotiveControl.air.IsCutOut) {
        _control.BailOff();                                       // throttle while braking → bail
    }
}
```

`ShouldUseLocomotiveBrake()` (`AutoEngineer.cs:886-896`): true iff every air-open car defers to the loco air (i.e., a single loco with no train, just engine + tender). So:
- Light engine moves use the **independent brake** (PID-controlled).
- Train moves use the **train brake** with a quantized output (`Ceil(x * 30) / 30` — 30 settings) and full release once below `trainBrakeReleaseBelowOutput` (default -0.01).
- **Throttle + train brake auto-bails** the loco brake every tick.

`StartMovement` waits for cylinder < 5 PSI before allowing throttle (`AutoEngineer.cs:536-547`). `Stopped` state holds `LocomotiveBrake = 1` and `TrainBrake = 0` on flat, or `TrainBrake = at-least-10-PSI` on grade > 0.2% (`AutoEngineer.cs:399-411`).

`EmergencyStop` (`AutoEngineer.cs:432-438`) sets both brakes to 1 and calls `set.SetVelocity(0f, ...)` — the only place velocity is forcibly zeroed.

### Patch candidates

| Method | Why patch |
|---|---|
| `AutoEngineer.MaintainSpeed` (private coroutine) | The full PID brake loop. Replace for custom AI strategy. |
| `AutoEngineer.ShouldUseLocomotiveBrake` (private) | Change when AI prefers independent vs train brake. |
| `AutoEngineerConfig` (ScriptableObject) | All PID gains and curves. Mutate `_config` field on instance. |
| `AutoEngineer.EmergencyStop` (private) | Change emergency behavior. |

---

## Related tunable fields

```csharp
// Model/Car.cs
public static float BrakeForceMultiplier         = 1f;     // 236
public static float BrakeForceMultiplierHandbrake = 3f;    // 238 — the live one

// Model/Config.cs
public float brakeForceMultiplier        = 1f;             // 11 — duplicates above (live)
public float brakeForceMultiplierHandbrake = 2f;           // 13 — DEAD CODE, never read
public AnimationCurve brakeForceCurve;                      // 15

// Model.Physics/LocomotiveAirSystem.cs
public float maximumLocomotiveBrakePressure = 72f;         // 18, public
public float brakeFeedValvePressure          = 90f;        // 20, public
public float compressorLimitLower            = 128f;       // 22
public float compressorLimitUpper            = 140f;       // 24
public float compressorRate                  = 0.5f;       // 28 PSI/s

// AutoEngineerConfig.cs
public float fullBrakeSet = 26f;                            // 8 — full-service PSI

// Reservoir defaults (CarAirSystem.cs ctor args)
BrakeLine      Volume=0.6818f  initial=0
BrakeReservoir Volume=2.5f     initial=0
BrakeCylinder  Volume=1f       initial=0
MainReservoir  Volume=43f      initial=140  (LocomotiveAirSystem.cs:8)
```

`Car.WearFeature`/`OilFeature` interaction with brakes is **none direct** — brakes are condition-modulated via the `Lerp(0.8f, 1f, Condition)` term in `CalculateBrakingForce`, but `Car.WearFeature = false` only stops *new* condition damage from accumulating; existing condition < 1 still degrades brake force.

---

## Patches that look obvious but aren't

- **Patching `Car.SendPropertyChange` to intercept brake commands won't catch AI/script writes** — `LocomotiveControlHelper.ChangeValue` calls `SendPropertyChange` directly, but `BaseLocomotive.UpdateCabControls` writes some keys via `KeyValueObject[key] = Value.Bool(...)` (e.g., `compressor` at `BaseLocomotive.cs:506`), bypassing `SendPropertyChange`. The only universal chokepoint is the KVO observer registration at `BaseLocomotive.ObserveCoreProperties`.
- **Patching `LocomotiveAirSystem.trainBrakeSetting` setter is too late** — the value is already through `KVO → adapter → setting`. To intercept on the wire, patch the KVO observer in `BaseLocomotive.cs:382-396`.
- **`BrakeStandController` is visual-only.** Swapping the `BrakeStyle` between `26L` and `6ET` activates/deactivates child GameObjects but does not change physics. `LocomotiveAirSystem` has no per-style branching — the lap/bail/feed-valve behaviour is monolithic.

---

## Cross-references

- Brake-pipe propagation, reservoir physics, valve schedules: [Air System](air-system.md).
- Anglecock state, hose connection topology, hose tear on uncouple: [Anglecock & Hose](anglecock-hose.md).
- `IntegrationSet.CalculateRetardingForce` integration of brake force: [Couplers › slack & integration](couplers.md#slack-state--integration).
- `Condition`-modulated brake force degradation source: [Wear & Durability › per-tick wear loop](wear-durability.md#per-tick-wear-loop).
- `EmergencyStop`'s `set.SetVelocity` is the same hand-of-god velocity zeroing used by [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths) when WillMove is invoked.
