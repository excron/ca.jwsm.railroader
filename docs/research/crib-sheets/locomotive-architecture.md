# Locomotive Architecture — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Traction](traction.md), [Brakes](brakes.md), [Air System](air-system.md), [MU/DPU Coordination](mu-dpu-coordination.md), [Audio](audio.md), [Cars & Cargo](cars-cargo.md), [Car Definitions](car-definitions.md)

This sheet covers the *seam architecture* of locomotives — the abstract base, the steam-subcomponent dispatch interface, the `LocomotiveControlAdapter` polymorphism that makes diesel ≠ steam, and the fuel→compressor→brake-pipe chain that ties power-source state to brake authority. The single hardest fact: **adding a third locomotive type is not a clean extension point in vanilla.** `TrainController.CreateNewCar` is a hard `switch` on `CarArchetype.LocomotiveDiesel` / `CarArchetype.LocomotiveSteam`, and `CarArchetype` is a closed enum in the `Definition` assembly. A "gas-electric" or "battery" mod must either patch the dispatch switch, alias to one of the two existing subclasses, or replace `TrainController.CreateNewCar` outright. Per-type behaviour beyond that is defined by the trio: a `BaseLocomotive` subclass (TE + fuel), a `LocomotiveControlAdapter` subclass (cab control routing), and (steam-only) a list of `ISteamLocomotiveSubcomponent` that get ticked per movement event.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `BaseLocomotive` (abstract) | `Model/BaseLocomotive.cs` | Per-loco MonoBehaviour. Holds `locomotiveControl`, `air`, `cabControls`, `_wheelVelocity`. Hosts MU mirror, idle tracker, TE pipeline. |
| `BaseLocomotive.CreateLocomotiveControl()` (abstract) | `Model/BaseLocomotive.cs:484` | Subclass factory that creates the per-type `LocomotiveControlAdapter` and attaches engine refs. |
| `LocomotiveControlAdapter` (abstract) | `RollingStock/LocomotiveControlAdapter.cs` | The cab→engine bridge. Defines `AbstractThrottle` / `AbstractReverser` polymorphism. Holds refs to `air` and `audio`. |
| `ISteamLocomotiveSubcomponent` | `RollingStock.Steam/ISteamLocomotiveSubcomponent.cs` | One method: `ApplyDistanceMoved(MovementInfo, driverVelocity, absReverser, absThrottle, driverPhase)`. **Steam-only**. |
| `SteamLocomotive.SubcomponentsApplyDistanceMoved` | `Model/SteamLocomotive.cs:422` | The dispatch. Called from `PositionWheelBoundsFront` per position update (`update == true`). |
| `SteamLocomotive.DidLoadModels` | `Model/SteamLocomotive.cs:98` | Where subcomponents are discovered (`GetComponentsInChildren<ISteamLocomotiveSubcomponent>`). |
| `LocomotiveAirSystem.HasFuel` (property, public set) | `Model.Physics/LocomotiveAirSystem.cs:68` | The fuel→compressor gate. `false` = compressor cannot run = MR cannot recover = brake pipe loses pressure. |
| `LocomotiveAirSystem.UpdateCompressor` | `Model.Physics/LocomotiveAirSystem.cs:113` | The 128/140 PSI hysteresis. **`HasFuel` is checked only on the start-up edge** — see gotcha below. |
| `TrainController.CreateNewCar` (loco dispatch) | `TrainController.cs:732-737` | The closed `switch` on `Archetype` for loco-class instantiation. |

---

## Subclass spine: the three-piece bundle for a locomotive type

A locomotive type in vanilla is **three coupled pieces**:

```
1. BaseLocomotive subclass (Model.*)
     ├─ override CalculateTractiveEffort(signedVelocityMph) → power source's TE function
     ├─ override AdhesiveWeight                              → adhesion mass policy
     ├─ override HasFuel                                     → fuel availability gate
     ├─ override CreateLocomotiveControl()                   → factory for the adapter
     ├─ override ConnectBodyControls()                       → cab-control wiring
     ├─ override PeriodicUpdate(dt)                          → 1Hz fuel consumption
     └─ override CutoffSettingForVelocity(velocityMps)       → AE/SimplifiedControls oracle
                                                                (steam: real cutoff oracle;
                                                                 diesel: hard-coded `1f`)

2. LocomotiveControlAdapter subclass (RollingStock.*)
     ├─ override AbstractThrottle  { get; set; }             → routes to engine model
     ├─ override AbstractReverser  { get; set; }             → routes to engine model
     ├─ override NormalizedTractiveEffort                    → for UI gauge
     ├─ override ThrottleInputNotches                        → 8 (diesel) / 0 (steam)
     └─ override ThrottleValueSteps                          → 8 (diesel) / 100 (steam)

3. Engine model MonoBehaviour (Model.Physics.*)
     ├─ Diesel: PrimeMover  (notch 0..8, reverser -1/0/+1, fuel)
     └─ Steam:  SteamEngine (regulator 0..1, reverser -1..+1, water+coal)
```

The base class **does not own** the engine model. `BaseLocomotive` knows nothing about `PrimeMover` or `SteamEngine`. The adapter is the bridge that translates an abstract throttle/reverser pair into the engine's domain-specific units.

### Where the three pieces connect

```
SteamLocomotive.FinishSetup    │   DieselLocomotive.FinishSetup
   ↓                           │       ↓
   AddComponent<SteamEngine>   │       AddComponent<PrimeMover>
   set engine.* fields         │       set primeMover.startingTractiveEffort
   engine.UpdateMaxTE()        │       maxSpeedMph = Random(63,66)
                               │
   base.FinishSetup() → BaseLocomotive.FinishSetup
                          ↓
                          AddComponent<LocomotiveAirSystem>   ← air
                          air.OnResetBailOff += ControlProperties[LocomotiveBrake]=0
                          locomotiveControl = CreateLocomotiveControl()  ← VIRTUAL CALL
                          locomotiveControl.air = (LocomotiveAirSystem)air
                          ObserveCoreProperties()             ← KVO observers wire AbstractThrottle/Reverser
                          if IsHost: AddComponent<AutoEngineerPlanner>
```

`CreateLocomotiveControl` is the abstract factory. Each subclass returns its own adapter wired to its own engine ref:

```csharp
// Model/SteamLocomotive.cs:137
protected override LocomotiveControlAdapter CreateLocomotiveControl() {
    var c = gameObject.AddComponent<SteamLocomotiveControl>();
    c.locomotive = this;            // adapter pulls engine through `locomotive.engine`
    return c;
}

// Model/DieselLocomotive.cs:158
protected override LocomotiveControlAdapter CreateLocomotiveControl() {
    var c = gameObject.AddComponent<DieselLocomotiveControl>();
    c.primeMover = primeMover;      // adapter holds direct PrimeMover ref
    return c;
}
```

Note the asymmetry: steam adapter holds a back-ref to `SteamLocomotive`; diesel adapter holds the `PrimeMover` directly. **There is no abstraction over "the engine model"** — the adapter just knows how to read/write its own engine.

---

## `LocomotiveControlAdapter` — the alternate-loco-type seam

### Full surface

```csharp
// RollingStock/LocomotiveControlAdapter.cs
public abstract class LocomotiveControlAdapter : MonoBehaviour
{
    public LocomotiveAirSystem  air;            // 8   — set by BaseLocomotive.FinishSetup
    public LocomotiveAudio      audio;          // 10  — set by BaseLocomotive.PreSetupComponents

    public abstract int   ThrottleInputNotches { get; }   // 12  Diesel:8, Steam:0
    public abstract int   ThrottleValueSteps   { get; }   // 14  Diesel:8, Steam:100
    public abstract float AbstractReverser     { get; set; }   // 16
    public abstract float AbstractThrottle     { get; set; }   // 18
    public virtual  int   ThrottleDisplay => RoundToInt(AbstractThrottle * ThrottleValueSteps); // 20

    // Concrete pass-throughs to LocomotiveAirSystem:
    public float LocomotiveBrakeSetting { get => air.locomotiveBrakeSetting;  set => air.locomotiveBrakeSetting  = value; }   // 22
    public float TrainBrakeSetting      { get => air.trainBrakeSetting;       set => air.trainBrakeSetting       = value; }   // 34
    public float LocomotiveBrakePressure{ get => air.locomotiveBrakePressure; set => air.locomotiveBrakePressure = value; }   // 46

    // Audio facade:
    public float Horn { get; set; }              // 58  — chooses whistle.parameter or horn.value
    public bool  Bell { get; set; }              // 85  — audio.bell.IsOn

    public abstract float NormalizedTractiveEffort { get; }   // 100 — 0..1 for UI gauges
}
```

**Five members are abstract**: `ThrottleInputNotches`, `ThrottleValueSteps`, `AbstractThrottle.{get,set}`, `AbstractReverser.{get,set}`, `NormalizedTractiveEffort`. **Everything else is concrete** and routes either to `air` (brakes) or `audio` (horn/bell). The public `Horn`/`Bell` properties dispatch based on which audio components were wired by the subclass `DidLoadModels` — `WhistlePlayer` (steam) or `HornPlayer` (diesel).

### `ThrottleDisplay` semantics

Default impl: `RoundToInt(AbstractThrottle * ThrottleValueSteps)`.

- Diesel: `RoundToInt((notch/8) * 8) = notch` → "Notch 0..8"
- Steam: `RoundToInt(regulator * 100)` → "0..100" (used as percent)

### Polymorphism in the two concrete adapters

| Property | `DieselLocomotiveControl` | `SteamLocomotiveControl` |
|---|---|---|
| `ThrottleInputNotches` | 8 | 0 |
| `ThrottleValueSteps` | 8 | 100 |
| `AbstractThrottle` get | `(float)primeMover.notch / 8f` | `engine.regulator` |
| `AbstractThrottle` set | `primeMover.notch = RoundToInt(v * 8); audio.primeMover.Notch = primeMover.notch` | `engine.regulator = v` |
| `AbstractReverser` get | `primeMover.reverser` (int -1/0/+1, returned as float) | `engine.reverser` (float -1..+1) |
| `AbstractReverser` set | `primeMover.reverser = RoundToInt(v)` | `engine.reverser = v` |
| `NormalizedTractiveEffort` | `primeMover.NormalizedTractiveEffort` | `engine.NormalizedTractiveEffort` |
| Public extras | (none) | `Reverser`, `Regulator` (duplicate accessors, only used by `SteamLocomotiveControl`-typed callers) |

The diesel setter has the **lossy round to int notch**; the steam setter is continuous. This is the seam discussed in [traction › throttle round-trip](traction.md#traction-spine-how-a-notch-becomes-movement).

### KVO observer wiring (where the adapter gets called from)

```csharp
// Model/BaseLocomotive.cs:362-397 — ObserveCoreProperties
KeyValueObject.Observe("throttle",  v => { locomotiveControl.AbstractThrottle  = v.FloatValue; if(v.FloatValue>0.001f) ResetAtRest(); ResetIdleTimer(); });
KeyValueObject.Observe("reverser",  v => { locomotiveControl.AbstractReverser  = v.FloatValue; if(|v.FloatValue|>0.001f) ResetAtRest(); ResetIdleTimer(); });
KeyValueObject.Observe("locoBrake", v => { locomotiveControl.LocomotiveBrakeSetting = v.FloatValue; ResetIdleTimer(); });
KeyValueObject.Observe("trainBrake",v => { locomotiveControl.TrainBrakeSetting      = v.FloatValue; ResetIdleTimer(); });
KeyValueObject.Observe("cutOut",    v => { locomotiveControl.air.IsCutOut = v.BoolValue;             ResetIdleTimer(); });
```

The `AbstractThrottle` / `AbstractReverser` setters are the **only** path from KVO into the engine model. **Patching either side of the adapter intercepts every control input** — UI, AI, MU mirror, console — they all flow through `SendPropertyChange` → KVO write → observer → adapter setter.

`Bell` and `Horn` go through a separate observer pair in `ConnectBodyControls` (`BaseLocomotive.cs:286-295`). The brake KVOs go through `ObserveCoreProperties`. There is **no single chokepoint observer** — five distinct observers fire for five distinct keys. This is a design feature: subclasses can hook in `ObserveCoreProperties` without inheriting brake observation.

### Patch candidates (LocomotiveControlAdapter)

| Method | Why patch |
|---|---|
| `*Control.AbstractThrottle.set` | Catch every throttle write before lossy rounding (diesel) or before hand-off to engine (steam). Cleanest single hook. |
| `*Control.AbstractReverser.set` | Same for reverser. |
| `LocomotiveControlAdapter.Horn.set` (concrete in base) | Catch all horn writes regardless of audio implementation. Already polymorphic on `whistle`/`horn` ref — patch here to e.g. add a custom audio target. |
| `BaseLocomotive.CreateLocomotiveControl` (abstract) | Override in a custom `BaseLocomotive` subclass to inject your adapter type. Cannot patch in vanilla types without subclassing them. |

---

## How a third locomotive type would actually get added

This is the load-bearing question. Below is the realistic patch surface, by component:

### 1. Archetype dispatch (`TrainController.CreateNewCar`)

```csharp
// TrainController.cs:732-737
Car car = definition.Archetype switch {
    CarArchetype.LocomotiveSteam  => gameObject.AddComponent<SteamLocomotive>(),
    CarArchetype.LocomotiveDiesel => gameObject.AddComponent<DieselLocomotive>(),
    _                              => gameObject.AddComponent<Car>(),
};
```

`CarArchetype` is a closed enum (`Definition/Model.Definition/CarArchetype.cs`):

```csharp
public enum CarArchetype {
    Uncategorized, LocomotiveDiesel, LocomotiveSteam,
    Boxcar, Flat, Tank, HopperOpen, Caboose, Tender, Gondola, Coach, Baggage
}
```

**No `LocomotiveOther` or `LocomotiveCustom` slot exists.** Three options for a mod:

- **(a) Patch `CreateNewCar`** with a Harmony postfix that replaces the `Car` component (when archetype matched a fallback), e.g. detect via a marker `Component` on the definition and `Destroy` the `Car`, then `AddComponent<MyGasElectric>`. Requires running before any `Setup()` call uses the wrong base type — `Setup` runs at `:738`, so the postfix has to act between `AddComponent` and `Setup`. **Practically impossible without a transpiler** because the prefix can't return early without losing its fallback path. Cleanest is a transpiler that injects an `if` before the switch.
- **(b) Alias to one of the two existing types** — `Archetype = LocomotiveDiesel` and treat your `MyGasElectric : DieselLocomotive` as an extension. You'd need to swap the `DieselLocomotive` component out for your subclass between `AddComponent<DieselLocomotive>` and `Setup` — same timing problem.
- **(c) Replace `TrainController.CreateNewCar` wholesale** via Harmony prefix returning `false`. This works but you lose the entire vanilla loco bootstrap path; you have to reproduce `Setup`, parent transforms, etc.

The cleanest design point would be making `CreateNewCar` extension-friendly via a registry (e.g., `Dictionary<CarArchetype, Func<GameObject, Car>>`). It's not. **This is the single biggest gotcha for alt-loco-type mods.**

### 2. `BaseLocomotive` subclass

A new loco subclass must override:

| Member | Required? | Why |
|---|---|---|
| `CalculateTractiveEffort(float signedVelocityMph)` | **Yes** (abstract) | The TE function. Called from `UpdateTractiveEffortWheelState`. |
| `AdhesiveWeight` (getter) | **Yes** (abstract) | Adhesion mass for slip detection. |
| `HasFuel` (getter) | **Yes** (abstract) | Fuel gate for compressor + idle. |
| `RatedTractiveEffort` (getter) | **Yes** (abstract) | UI display. |
| `MaxTractiveEffortAtVelocity(float absMph)` | **Yes** (abstract) | AE planning + UI display. |
| `CutoffSettingForVelocity(float velocityMps)` | **Yes** (abstract) | Steam: real cutoff oracle. Diesel: returns `1f`. AE/SimplifiedControls consumer. |
| `CreateLocomotiveControl()` | **Yes** (abstract) | Factory for your `LocomotiveControlAdapter`. |
| `NormalizedTractiveEffort` | **Yes** (abstract on `Car`) | UI gauge. |
| `FinishSetup()` | Recommended | `AddComponent` your engine model, set `maxSpeedMph`, then call `base.FinishSetup()`. |
| `PeriodicUpdate(float dt)` | Recommended | 1Hz fuel/state. **Always call `base.PeriodicUpdate(dt)`** — that's where `PeriodicUpdateForMu` runs. |
| `ConnectBodyControls()` | Recommended | Wire your cab controls (`ControlPurpose.Throttle`, etc.) to `ControlHelper.Throttle` setters. Always call `base.ConnectBodyControls()`. |
| `UpdateCabControls()` | Optional | Mirror your engine state to gauges. Always call `base.UpdateCabControls()`. |
| `DidLoadModels()` | Optional | Discover model children (e.g., subcomponents). Always call `base.DidLoadModels()`. |
| `WantsEndGear(End)` / `RequiresConnectionToEnd(End)` / `ForceConnectedToAtRear(Car)` | If you have a fuel car | See `SteamLocomotive` for the tender pattern. |

### 3. `LocomotiveControlAdapter` subclass

Implement the five abstract members. Hold a back-ref to the loco or a direct ref to the engine model. That's it.

### 4. Engine model (optional)

A pure data/behaviour MonoBehaviour. Nothing in `BaseLocomotive` knows about it; only your subclass + your adapter touch it. No required interface. (Steam uses `SteamEngine`, diesel uses `PrimeMover`; both are plain MonoBehaviours.)

### 5. Definition (`CarDefinition` subclass)

Add a `MyLocoDefinition : CarDefinition` with `override Kind => "MyLoco"` and your tunable fields (mirror `DieselLocomotiveDefinition` / `SteamLocomotiveDefinition`). Definition discovery happens via the `IDefinition` registry — check [car-definitions.md](car-definitions.md). The `Kind` string is only matched by the `DefinitionChecker` and the editor (`UI.CarEditor.DefinitionEditors`) — it's not the dispatch key for runtime instantiation.

### 6. Cab controls (`LocomotiveCabControlsHookup`)

Vanilla hookup has slots: `throttle`, `regulator`, `reverser`, `johnsonBar`, `locomotiveBrake`, `trainBrake`, `cutout`, `horn`, `bell`, `boilerPressure`, etc. New controls need either to reuse one of these slots or extend the hookup component. The `TryGetControl(ControlPurpose, out ContinuousControl)` discovery walks `BodyTransform` for `RadialAnimatedControl`s by `ControlComponentPurpose`. Add new controls by tagging them with the existing `ControlPurpose` enum or extending it.

### 7. Audio components

The `LocomotiveAudio` MonoBehaviour holds `primeMover` (PrimeMoverAudioPlayer), `horn` (HornPlayer), `whistle` (WhistlePlayer), `bell` (Bell). Discovered in `BaseLocomotive.PreSetupComponents` and per-subclass `DidLoadModels`. A new loco type can either use one of these slots or add new audio components and hold them in your own adapter subclass extension.

### 8. Per-loco-type particle effects

Diesel: `DieselExhaustParticleController`. Steam: `SteamChuffParticleController`, `CylinderCockController`, `FireboxEffectController` (via `OnHasFuelDidChange`). Custom loco: define your own and discover them in `DidLoadModels` (see `DieselLocomotive.DidLoadModels:46`).

---

## `ISteamLocomotiveSubcomponent` — the steam dispatch chain

### The interface

```csharp
// RollingStock.Steam/ISteamLocomotiveSubcomponent.cs (entire file)
namespace RollingStock.Steam;

public interface ISteamLocomotiveSubcomponent
{
    void ApplyDistanceMoved(MovementInfo info, float driverVelocity,
                            float absReverser, float absThrottle, float driverPhase);
}
```

**One method. No `Configure`, no `Init`, no `OnEnable`.** Setup is implicit — implementers are MonoBehaviours and use Unity lifecycle for init.

### Discovery and dispatch

```csharp
// Model/SteamLocomotive.cs:134
_subcomponents.AddRange(BodyTransform.GetComponentsInChildren<ISteamLocomotiveSubcomponent>());

// Model/SteamLocomotive.cs:422
private void SubcomponentsApplyDistanceMoved(MovementInfo info)
{
    float absReverser = Mathf.Abs(locomotiveControl.AbstractReverser);
    float absThrottle = Mathf.Abs(locomotiveControl.AbstractThrottle);
    float driverPhase = (_wheelAnimator == null) ? 0f : _wheelAnimator.DriverPhase;
    foreach (var sc in _subcomponents)
        sc.ApplyDistanceMoved(info, _wheelVelocity, absReverser, absThrottle, driverPhase);
}
```

### Trigger site (per-position-update, not per-tick)

```csharp
// Model/SteamLocomotive.cs:354 — PositionWheelBoundsFront
public override Location PositionWheelBoundsFront(...) {
    ...
    if (!update) return location5;          // probe call, no dispatch
    UpdateBaseLocations(...);
    SetBodyPosition(...);
    UpdateCurvatureForLocation(location);
    SubcomponentsApplyDistanceMoved(info);  // ← dispatch HERE
    if (_rollingPlayer != null) _rollingPlayer.SetVelocity(velocity);
    FireOnMovement(info);
    return location5;
}
```

**Per-distance-moved, NOT per-FixedUpdate.** The dispatch happens inside `PositionWheelBoundsFront` only when `update == true` (the `IntegrationSet` calls it twice — once for probing without update, once with update). So subcomponents tick once per actual position update, with the `MovementInfo.Distance` value reflecting the integrator's net distance for this tick. If the loco is stopped (`Element.acceleration == 0`, `Element.velocity == 0`) and `ShouldUpdatePosition() == false`, **`PositionWheelBoundsFront` is not called and subcomponents are not ticked** — animation freezes.

This is why subcomponents that need to update at rest (e.g., `CylinderCockController` ramping smoke when stopped) carry their own `Update`/`Coroutine` and only consume the `ApplyDistanceMoved` hook for parameter snapshots. See the `CylinderCockController.UpdateCoroutine` pattern.

### Dispatch parameters

| Param | Source | Notes |
|---|---|---|
| `MovementInfo info` | Passed through from `IntegrationSet` | `Distance` (m, signed), `DeltaTime` (s), `TractiveEffort` (TE used this tick), etc. |
| `float driverVelocity` | `_wheelVelocity` (m/s, can be larger than `velocity` during slip) | This is the **driver wheel's** speed, not the body's. Slip propagates here. |
| `float absReverser` | `Mathf.Abs(locomotiveControl.AbstractReverser)` | 0..1 (centered → 1 fully forward or reversed) |
| `float absThrottle` | `Mathf.Abs(locomotiveControl.AbstractThrottle)` | 0..1 — same value the engine model receives |
| `float driverPhase` | `_wheelAnimator.DriverPhase` (0..1, repeating) | The main driver wheel's animation parameter (0..1 cycle). For chuff timing. |

### All four implementations in vanilla

| Type | File | Role |
|---|---|---|
| `RollingStock.Chuff` (also `IChuffProvider`) | `RollingStock/Chuff.cs` | Audio chuff via `ChuffFilter`. Uses `_movedLastFixedUpdate` to detect rest. Ticks `chuffFilter.engineCutoff = absReverser`, `engineThrottle` lerped from `_absThrottle`, `engineNormalizedTE` lerped from `_tractiveEffortReported`. **At low speed (`<5 mph`), the chuff DELEGATE drives the particle puff via `Delegate.ScheduleNextChuff(delay, 0.2f)`** — the delegate is `SteamChuffParticleController`, set in `SteamLocomotive.DidLoadModels:117` (`_chuffAudio.Delegate = _chuffParticles`). |
| `RollingStock.Steam.SteamChuffParticleController` (also `IDynamicChuffDelegate`) | `RollingStock.Steam/SteamChuffParticleController.cs` | The smokestack particle effect. Subscribes to `_locomotive.OnIdleDidChange` and `OnHasFuelDidChange` (NOT through subcomponent interface — through events) for play/stop. `ApplyDistanceMoved` snapshots `absVelocity`, `isStopped` (`<0.01 m/s`), `continuous` (`>5 m/s`), `_targetTractiveEffort = absThrottle` (or 0 if stopped/no fuel). |
| `RollingStock.Steam.SteamLocomotiveWheelAnimator` | `RollingStock.Steam/SteamLocomotiveWheelAnimator.cs` | Drives wheel rotation via `PlayableHandle` per-wheel. **Uses `driverVelocity` for driver wheels and `Locomotive.velocity` for non-driver wheels** — so during slip, the drivers visually spin faster than the support wheels. Also drives `WheelAudio.Roll(distance * sign(velocity), velocity)`. Sets `DriverPhase` from the main driver's `Parameter` (0..1) — that's what's then fed back to other subcomponents on the next dispatch. |
| `Effects.CylinderCockController` | `Effects/CylinderCockController.cs` | Cylinder-cock smoke + audio. `ApplyDistanceMoved` snapshots `_phase = driverPhase + 0.25f` and bumps `_steam` by `absThrottle * fixedDeltaTime * 0.001f`. The actual smoke/audio update runs in `UpdateCoroutine` (independent of the subcomponent dispatch). Listens to `Control.CylinderCock` KVO via `_controlObserver` to start/stop. |

**Notably absent**: there is no Dynamo or Lubricator subcomponent. `DynamoPlayer` (`Audio/DynamoPlayer.cs`) is just an audio component subscribed to `OnIdleDidChange` + `OnHasFuelDidChange`; it does not implement `ISteamLocomotiveSubcomponent`. Same for `FireboxEffectController` — pure event subscriber. **The interface is used only by things that need per-distance state.**

### Init pattern (the closest thing to "Configure")

`Chuff` defines a `Configure(driverDiameter, normalizedEngineSize)` method — **not part of the interface**, but called by `SteamLocomotive.DidLoadModels:114`:

```csharp
_chuffAudio = BodyTransform.GetComponentInChildren<IChuffProvider>();
SteamLocomotiveDefinition locoDefinition = LocoDefinition;
SteamLocomotiveDefinition.Wheelset wheelset = locoDefinition.Wheelsets[locoDefinition.MainDriverIndex];
if (_chuffAudio != null) {
    _chuffAudio.Configure(wheelset.Diameter, Mathf.InverseLerp(15000f, 65000f, engine.MaximumTractiveEffort));
    _chuffAudio.Delegate = _chuffParticles;     // wire the delegate
}
```

`IChuffProvider` extends `ISteamLocomotiveSubcomponent` to add `Delegate { get; set; }` and `Configure(...)`. So **the "Configure chain" is interface-extension, not interface-method**. A new chuff implementation must implement `IChuffProvider` and ride this discovery path.

`CylinderCockController.Configure(radius, forwardOffset)` is similar — called by `CylinderCockComponentBuilder.Build:20` from definition data, **not from `SteamLocomotive`**. Different init authority: builder-time (definition-driven) vs. discovery-time (locomotive-driven).

### Per-distance vs. per-tick callbacks (cheat sheet)

| Hook | Called from | When | Use case |
|---|---|---|---|
| `ISteamLocomotiveSubcomponent.ApplyDistanceMoved` | `SteamLocomotive.PositionWheelBoundsFront` | Per actual position update (tick if moving, NEVER if stopped) | Wheel animation, chuff timing, distance-cumulating effects |
| `MonoBehaviour.Update` | Unity | Per frame (variable rate) | Smoke fade, animation lerp, audio fade |
| `MonoBehaviour.FixedUpdate` | Unity | 50 Hz | Used by `Chuff` for low-speed chuff scheduling, since `ApplyDistanceMoved` may not fire at rest |
| `BaseLocomotive.PeriodicUpdate(dt)` (subclass overrides) | Coroutine, 1 Hz | 1 Hz, host-only | Fuel consumption, MU mirror |
| `BaseLocomotive.OnIdleDidChange` (Action) | KVO observer on `idle` | Edge transitions | Idle-driven start/stop (DynamoPlayer, FireboxEffectController, SteamChuffParticleController) |
| `BaseLocomotive.OnHasFuelDidChange` (Action) | `InvokeHasFuelDidChange` from per-class `PeriodicUpdate` | Edge transitions | Fuel-driven start/stop (DynamoPlayer, FireboxEffectController, SteamChuffParticleController, CylinderCockController via `_locomotive.HasFuel` peek) |

`Chuff.FixedUpdate` is the canonical example of "I need to run even at rest, but also accept per-distance updates" — it uses `_movedLastFixedUpdate` flag set in `ApplyDistanceMoved`, cleared in `FixedUpdate`, to detect rest (`_absVelocity = 0`).

### How a new subcomponent gets wired in

1. Implement `ISteamLocomotiveSubcomponent` on a `MonoBehaviour`.
2. Make sure the GameObject lives under `SteamLocomotive.BodyTransform` at the time `DidLoadModels` runs. The discovery is `GetComponentsInChildren<ISteamLocomotiveSubcomponent>()` — so it must be a child of the body.
3. Optional: extend the interface (like `IChuffProvider`) and patch `SteamLocomotive.DidLoadModels` to invoke your custom configure. Cleaner: do init in `OnEnable`/`Awake` since Unity guarantees component lifecycle.
4. Optional: register a `[ComponentBuilder]` for a new `Component` type so it can be added to a `CarDefinition` declaratively (mirror `ChuffComponentBuilder` / `CylinderCockComponentBuilder`).

### Patch candidates (subcomponent dispatch)

| Method | Why patch |
|---|---|
| `SteamLocomotive.DidLoadModels` | Postfix to add custom subcomponents not present as children (e.g., `_subcomponents.Add(myThing)`). Vanilla list is reset in `UnloadModels`. |
| `SteamLocomotive.SubcomponentsApplyDistanceMoved` | Wrap to add cross-cutting state (e.g., add a `boilerPressure` parameter via shared mutable state). |
| `SteamLocomotive.PositionWheelBoundsFront` | Earliest hook that catches "we just moved." Postfix runs after subcomponent dispatch. |
| `SteamLocomotive.UnloadModels` | Postfix to clear your custom subcomponents (otherwise they leak into the next load). |

### Gotchas (subcomponent dispatch)

- **Subcomponents do NOT tick at rest.** If your effect needs continuous update, drive it from `MonoBehaviour.Update`/`FixedUpdate`, and use `ApplyDistanceMoved` only for parameter snapshots.
- **`_subcomponents.Clear()` in `UnloadModels`.** Mods must re-add custom subcomponents on every model reload (e.g., archetype change, prefab swap). Subscribe to `Car`'s model lifecycle.
- **`driverVelocity` can be larger than the body's `velocity` during slip.** Use the right one: drivers spin with `driverVelocity`, body rolls at `velocity`.
- **`driverPhase` reads `_wheelAnimator.DriverPhase` from the *previous* dispatch's update.** `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` writes `DriverPhase` *during* dispatch, but the foreach iteration order is insertion order. So if the wheel animator is added before the chuff (the typical case — wheel animator added in `DidLoadModels:101`, chuff is found by `GetComponentsInChildren` traversal), the chuff sees the *current* tick's phase. If you add a custom subcomponent before the wheel animator, it sees stale phase. Ensure your dispatch order is correct or pull `DriverPhase` directly from `_wheelAnimator`.
- **Steam-only.** No `IDieselLocomotiveSubcomponent` exists. Diesel exhaust particles get fed via `_primeMoverAudioPlayer.NormalizedExhaustOutputEvent` (a delegate, fired from inside the audio player's Update) — a different pattern entirely.

---

## Compressor / fuel / `HasFuel` plumbing

### The fuel→compressor→brake chain

```
Steam: tender water+coal both > 0.001       Diesel: load slot 0 (diesel-fuel) > 0.001
   │                                            │
   ▼                                            ▼
SteamLocomotive.PeriodicUpdate                 DieselLocomotive.PeriodicUpdate
   engine.HasWaterAndCoal = (water>0 && coal>0)  primeMover.HasFuel = (fuel>0)
   if changed:                                    if changed:
     InvokeHasFuelDidChange()                       InvokeHasFuelDidChange()
     locomotiveAirSystem.HasFuel = ...             locomotiveAirSystem.HasFuel = ...
   │                                            │
   └─────────────┬──────────────────────────────┘
                 ▼
   LocomotiveAirSystem.HasFuel (public bool, default true)
                 │
                 ▼
   LocomotiveAirSystem.UpdateCompressor (per FixedUpdate via UpdateAir)
      if (MainReservoir.Pressure < 128f) compressorRunning = HasFuel    ← ONLY START EDGE
      if (MainReservoir.Pressure > 140f) compressorRunning = false
      if (compressorRunning) MainReservoir.Pressure += 0.5 * dt
                 │
                 ▼
   if compressorRunning == false AND BP demand > recovery:
      MainReservoir.Pressure drops as _mainReservoirToBrakeLine vents into the brake line
                 │
                 ▼
   _mainReservoirToBrakeLine.ValveAutomaticBrake feeds BP at lapTrainBrakePressure target
   (cross-link: brakes.md › train brake "lap" pressure)
                 │
                 ▼
   Brake pipe pressure drops below cars' triple-valve trigger threshold (~10 PSI drop)
                 │
                 ▼
   Each CarAirSystem's triple valve flips to APPLY → BC pressure rises → brakePercent → 1
                 │
                 ▼
   IntegrationSet.ApplyBrakes computes retarding force from car.air.brakePercent
                 │
                 ▼
   Train decelerates / stops
```

### `LocomotiveAirSystem.UpdateCompressor` — the gotcha

```csharp
// Model.Physics/LocomotiveAirSystem.cs:113
private void UpdateCompressor(float dt)
{
    if (MainReservoir.Pressure < compressorLimitLower)         // < 128 PSI
        compressorRunning = HasFuel;                            // ← HasFuel ONLY checked here
    if (MainReservoir.Pressure > compressorLimitUpper)         // > 140 PSI
        compressorRunning = false;
    if (compressorRunning)
        MainReservoir.Pressure += compressorRate * dt;          // 0.5 PSI/sec
}
```

**`HasFuel` is only consulted on the start-edge** of the hysteresis cycle (when MR drops below 128). If `HasFuel` flips to `false` while the compressor is running, **the compressor keeps running until MR exceeds 140 PSI** (then the upper-edge sets `compressorRunning = false`). After that, the next time MR drops below 128, `HasFuel` is consulted and the compressor stays off.

In practice this means **fuel-out doesn't immediately stop pressure recovery**. The compressor finishes its current "pump-up" cycle. For brake-pipe failure to follow fuel-out, MR must first rise above 140, then drop below 128. With a leaking brake pipe (which vanilla doesn't simulate per-car) this might never happen. **Practically: HasFuel-false → compressor stops on the next pump-up cycle → MR slowly drains via brake pipe demand → BP drops → emergency-style application.**

The brake pipe doesn't go to zero immediately. The "feed valve" path (`_mainReservoirToBrakeLine.ValveAutomaticBrake`) cuts off when MR drops near brake pipe pressure (the valve has a back-pressure check). So the failure mode is gradual drift, not instant emergency.

There is **no separate "emergency brake" code path triggered by fuel-out**. The cars just lose their pipe charge over time and apply via normal triple-valve mechanics.

### Fuel slot wiring

| Loco | Fuel source | Slot lookup |
|---|---|---|
| Diesel | self load slot 0 (`"diesel-fuel"`) | `this.GetLoadInfo(0)?.Quantity ?? 0` (`DieselLocomotive.cs:139`) |
| Steam | adjacent tender (or self if `!hasTender`), via `FuelCar()` | `_coalSlot` and `_waterSlot` cached on first periodic call by `LoadSlot.RequiredLoadIdentifier` match for `"coal"` and `"water"` (`SteamLocomotive.cs:240-243`) |

For steam, `FuelCar()` returns `this` if `!hasTender`, else looks for the adjacent rear car via `TryGetTender`. **If the tender becomes uncoupled, `FuelCar()` returns `null` and `PeriodicUpdate` early-returns — meaning fuel consumption stops AND `HasWaterAndCoal` is not updated.** A loco running away from its tender will continue to produce TE indefinitely. (This is a real gotcha for derailment scenarios.)

### `HasFuel` as a driver of multiple systems

| Consumer | What it does on `HasFuel = false` |
|---|---|
| `LocomotiveAirSystem.UpdateCompressor` | Stops compressor on next start-edge (gradual BP loss) |
| `PrimeMover.CalculateTractiveEffort` | Returns 0 immediately (`if (!running || !HasFuel) { ...; return 0; }`) — diesel cuts power instantly |
| `SteamEngine.CalculateTractiveEffort` | Returns 0 immediately if `!HasWaterAndCoal` (`tractiveEffort = HasWaterAndCoal ? regulated * MaxTE(...) : 0`) — steam cuts power instantly |
| `BaseLocomotive.OnHasFuelDidChange` (event, fired from `InvokeHasFuelDidChange`) | Subscribers: `DynamoPlayer.UpdatePlayStop`, `FireboxEffectController.HasFuelDidChange`, `SteamChuffParticleController.UpdatePlayStop`, `PrimeMoverAudioPlayer` (subscribes via `BaseLocomotive.OnHasFuelDidChange`) |
| `CylinderCockController.UpdateCoroutine` (steam only) | Reads `_locomotive.HasFuel` directly (peek, no subscription) — when false, openness lerps to 0 |

So **TE cutoff is instant; brake-pipe failure is gradual; cosmetic effects (smoke, dynamo audio) are edge-driven**.

### `LocomotiveAirSystem.HasFuel` as a public setter

```csharp
public bool HasFuel { get; set; } = true;
```

Public auto-property. Settable from anywhere. Currently only set by `DieselLocomotive.PeriodicUpdate` and `SteamLocomotive.PeriodicUpdate`. Mods that want a custom fuel source can write directly. **No auth check** — but `PeriodicUpdate` runs on the host only, so client writes won't matter for the simulation (clients don't run `UpdateAir`).

### Patch candidates (compressor/fuel)

| Method | Why patch |
|---|---|
| `LocomotiveAirSystem.UpdateCompressor(float)` | Replace the whole compressor cycle. E.g., add power-cost (TE penalty), add slow start-up, decouple from `HasFuel` for electric locos. |
| `LocomotiveAirSystem.HasFuel` (property setter) | Intercept fuel-state changes. Cleanest single hook. |
| `DieselLocomotive.PeriodicUpdate` / `SteamLocomotive.PeriodicUpdate` | The fuel-consumption loop. Replace for a custom power source (battery, gas-electric). |
| `BaseLocomotive.InvokeHasFuelDidChange` (protected) | Cannot patch directly via Harmony without subclass — but you can subscribe to `OnHasFuelDidChange` event externally. |
| `LocomotiveAirSystem.compressorLimitLower` / `compressorLimitUpper` / `compressorRate` (public fields) | Per-instance tuning. No setter trampolines, just write the field. |

### Gotchas (compressor/fuel)

- **Compressor doesn't stop on `HasFuel = false`** — it stops on the next 128-PSI start-edge. For instant compressor stop, patch `UpdateCompressor` to add `if (compressorRunning && !HasFuel) compressorRunning = false;` at the start.
- **`compressorRate = 0.5 PSI/sec` is shared across all locos in a consist** by virtue of each loco running its own compressor. A 4-loco consist has 4× the recovery rate. Mods that add per-loco compressor shutoff should account for this.
- **No leak model** — `MainReservoir` and `BrakeLine` only lose pressure via `_mainReservoirToBrakeLine.ValveAutomaticBrake` (intentional venting through the feed valve) and per-car triple-valve action. Idle locos with intact brake pipes hold pressure forever. So fuel-out → no compressor → BP failure depends on having moving brake-pipe demand.
- **`HasFuel` on multi-loco consists**: each loco has its own `HasFuel` and its own compressor. The MU-cut-out trail-unit pattern (cross-link: [mu-dpu-coordination.md](mu-dpu-coordination.md)) means cut-out trails contribute their compressor capacity if they have fuel. If you fuel-starve the lead but trails have fuel, the consist still maintains BP — but the lead cannot produce TE.
- **`SteamLocomotive.PeriodicUpdate` early-returns if `FuelCar()` is null.** A steam loco with a missing/uncoupled tender (transient state during set re-org) will not consume fuel and not update `HasWaterAndCoal`. The `engine.HasWaterAndCoal` field stays at its last value — usually `true` — so steam can run "indefinitely without coal" if the tender stays disconnected long enough.
- **Fuel quantity threshold is `> 0.001f`** for both coal and water (steam) and for diesel fuel. There's no "low fuel" warning — it's a hard binary.
- **`primeMover.running` is a public bool that defaults to `true`** and is never written elsewhere in vanilla. Setting it to `false` zeros TE without affecting fuel state. Useful as a "kill switch" for an electric/battery loco type that has its own running state.
- **`SteamEngine.HasWaterAndCoal` and `PrimeMover.HasFuel` are independent of `LocomotiveAirSystem.HasFuel`.** The subclass `PeriodicUpdate` keeps them synced manually (`locomotiveAirSystem.HasFuel = primeMover.HasFuel`). A mod that bypasses `PeriodicUpdate` must replicate this sync.

---

## MP authority across loco internals

| State | KVO key | Auth | Writer |
|---|---|---|---|
| `throttle` | `throttle` (float) | Crew + train-crew | Cab UI / AE / MU mirror / scripts |
| `reverser` | `reverser` (float) | Crew + train-crew | Cab UI / AE / MU mirror / scripts |
| `locoBrake` | `locoBrake` (float, allows `-0.1` bail sentinel) | Crew + train-crew | Cab UI / AE / scripts |
| `trainBrake` | `trainBrake` (float) | Crew + train-crew | Cab UI / AE / scripts |
| `cutOut` | `cutOut` (bool) | Crew + train-crew | UI / scripts |
| `mu` | `mu` (bool) | Crew + train-crew | UI / scripts |
| `idle` | `idle` (bool) | Crew + train-crew (host-written) | `BaseLocomotive.PeriodicUpdateBody` (host) |
| `compressor` | `compressor` (bool) | Crew + train-crew (host-written) | `BaseLocomotive.UpdateCabControls` (host) |
| `cylinderCock` | `cylCocks` (bool) | Crew + train-crew | UI / `LocomotiveControlHelper.CylinderCocksOpen.set` (gated to `Archetype == LocomotiveSteam`) |
| `headlight` | `headlight` (int) | Crew + train-crew | Cab UI |
| `bell` | `bell` (bool) | Crew + train-crew | Cab UI |
| `horn` | `horn` (float) | Crew + train-crew | Cab UI |

**No HostOnly KVO keys for control state.** All control writes go through `Car.SendPropertyChange` → `StateManager.ApplyLocal(new PropertyChange(...))` and the standard `PropertyChange` auth pipeline. The host validates and rebroadcasts.

**Host-only state** lives outside KVO:
- `LocomotiveAirSystem.MainReservoir.Pressure` — broadcast as part of `BatchCarAirUpdate` (host→client, see [air-system.md](air-system.md)).
- `LocomotiveAirSystem.HasFuel` — host-side mutation; clients see it as derived through `compressor` KVO (because `compressorRunning = HasFuel` on start-edge, and `compressor` is broadcast).
- `_wheelVelocity`, `_tractiveEffort`, `_wheelState` — local on every machine (clients compute identically because all inputs are KVO-replicated). **Mods adding new inputs to TE must replicate them.**
- `engine.regulator`, `engine.reverser`, `primeMover.notch`, `primeMover.reverser` — written by the adapter setter on every machine (driven by KVO observer), so they're effectively replicated.
- `SteamEngine.CoalConsumptionRate`, `WaterConsumptionRate` — host-computed in `UpdateConsumption`. Clients see derived effects (load slot quantity changes via `BatchCarAirUpdate`-like mechanism for load slots).
- `_subcomponents` list — local on every machine (each builds its own from the model children).

**`AutoEngineerPlanner` is host-only** — `BaseLocomotive.FinishSetup:426` only adds it `if (StateManager.IsHost)`. Clients have `null` `AutoEngineerPlanner`. Cross-link: [mu-dpu-coordination.md › AE](mu-dpu-coordination.md).

**`PeriodicUpdateForMu` MU writes happen on host only** (`BaseLocomotive.cs:151`). Slave loco's `Throttle` and `Reverser` KVO keys are written by the host as part of MU mirroring. Cross-link: [mu-dpu-coordination.md › 1Hz mirror](mu-dpu-coordination.md).

---

## Patch points cheat sheet

### Custom locomotive type (gas-electric, electric, battery)

The realistic recipe:

1. **Subclass `BaseLocomotive`** (e.g., `MyBatteryLocomotive`). Override `CalculateTractiveEffort`, `AdhesiveWeight`, `HasFuel`, `RatedTractiveEffort`, `MaxTractiveEffortAtVelocity`, `CutoffSettingForVelocity`, `CreateLocomotiveControl`, `NormalizedTractiveEffort`. Override `FinishSetup` to add your engine-model component. Override `PeriodicUpdate` for fuel/charge consumption (always call `base.PeriodicUpdate(dt)`).
2. **Subclass `LocomotiveControlAdapter`** (e.g., `BatteryLocomotiveControl`). Implement the five abstract members. Hold ref to your engine model.
3. **Engine model MonoBehaviour** — your "battery" or "electric" model. Plain MonoBehaviour, no required interface.
4. **`CarDefinition` subclass** — `MyBatteryLocomotiveDefinition : CarDefinition` with a unique `Kind`.
5. **Asset pack** with the prefab, controls tagged with `ControlPurpose.*`, audio components, etc.
6. **Patch `TrainController.CreateNewCar`** (transpiler or wholesale prefix-replace) to dispatch to your subclass when the definition is yours. **This is the unavoidable patch.** Either:
   - Transpile to inject a switch case before the existing `switch`.
   - Prefix that detects a marker on the definition, runs `gameObject.AddComponent<MyBatteryLocomotive>()`, calls `Setup`, returns `false` to skip vanilla.
   - Postfix that swaps the `Car`/`DieselLocomotive`/`SteamLocomotive` component for your subclass — but this requires `Setup` to not have run yet, which it has (`:738`). So postfix is impractical without further patching.
7. **`AutoEngineer` compatibility** — AE assumes either steam (uses `CutoffSettingForVelocity` for cutoff oracle) or diesel (gets `1f`). Your `CutoffSettingForVelocity` should return appropriate values for your power source.

### Replacing vanilla steam dynamics

- Replace `SteamEngine.CalculateTractiveEffort` (per-instance hook, replaces TE pipeline).
- Replace `TrainMath.ReverserPowerMultiplier` (global cutoff curve — affects all steam locos).
- Replace `SteamEngine.UpdateMaximumTractiveEffort` (the `2 * 0.85 * d² * stroke * pressure / driver` formula and its caching).
- Patch `SteamLocomotive.PeriodicUpdate` for custom fuel logic.
- Patch `SteamLocomotive.CutoffSettingForVelocity` for custom AE cutoff oracle.

Cross-link: [traction.md › Steam patch candidates](traction.md#patch-candidates-steam).

### Intercepting compressor logic

- `LocomotiveAirSystem.UpdateCompressor` — full replacement.
- `LocomotiveAirSystem.HasFuel` setter — intercept fuel-state changes (no underlying field, but the auto-property setter is still patchable).
- `LocomotiveAirSystem.compressorRate` field — instance-tunable.
- For per-loco compressor "off" semantics independent of fuel: add a public bool to a subclass (or attach a sidecar component) and patch `UpdateCompressor` to consult it.

### Custom subcomponent (steam)

1. Implement `ISteamLocomotiveSubcomponent` on a `MonoBehaviour`.
2. Either:
   - **(a) Make it a child of `SteamLocomotive.BodyTransform`** before `DidLoadModels` runs. The simplest path: define a `[ComponentBuilder]` that instantiates a prefab containing your component as a body child. Mirror `ChuffComponentBuilder`.
   - **(b) Add it dynamically post-load** by patching `SteamLocomotive.DidLoadModels` postfix to `_subcomponents.Add(your_thing)`. Then patch `UnloadModels` postfix to clean up.
3. Optionally extend the interface (like `IChuffProvider`) and patch `DidLoadModels` to call your `Configure` between discovery and dispatch.

### Custom diesel "subcomponent"

There's no equivalent dispatch interface. The closest analog is `_primeMoverAudioPlayer.NormalizedExhaustOutputEvent` (an `Action<float>` fired from inside `PrimeMoverAudioPlayer.Update`). For per-distance dispatch on diesel, you'd need to:

- Subscribe to `Car.OnMovementDidApply` (cross-link: [cars-cargo.md](cars-cargo.md)) for per-tick distance.
- Or postfix `BaseLocomotive.FixedUpdate` for per-tick all-state.
- Or define your own dispatch by patching `DieselLocomotive.PositionWheelBoundsFront` — but this is `Car.PositionWheelBoundsFront` for diesel (no override), so the patch is on the base method.

---

## Architectural gotchas (cross-cutting)

1. **`BaseLocomotive.FixedUpdate` calls `UpdateTractiveEffortWheelState` even on the client** — no `IsHost` gate. TE is computed locally on every machine because all inputs (`AbstractThrottle`, `AbstractReverser`, `Condition`, `IsDerailed`, `_wheelVelocity`) are KVO-replicated or locally derived. Mods that introduce host-only state into TE must replicate that state via KVO.
2. **MU mirror runs at 1Hz on the host** and sends KVO writes that are then replicated to all machines via the standard PropertyChange pipeline. The slave's `AbstractThrottle.set` runs on every machine. So MU latency is `up to 1s` to the master changing throttle, plus normal network latency to clients.
3. **`LocomotiveAirSystem` is a `CarAirSystem` subclass.** Anything that walks all `CarAirSystem` instances (e.g., the brake-pipe propagation) sees the loco air system through the base class. Override `UpdateAir` or `SetupReservoirs` for per-loco specialization.
4. **`OnResetBailOff` is wired in `BaseLocomotive.FinishSetup` host-side only** (`BaseLocomotive.cs:402`). Clients don't have the handler. The bail-off ack (`ControlProperties[LocomotiveBrake] = 0`) is host-only — clients see the result via KVO replication. Mods that want client-side bail UI feedback must subscribe to the `LocomotiveBrake` KVO key, not `OnResetBailOff`.
5. **`AutoEngineerPlanner` is added in `FinishSetup` only if `StateManager.IsHost && GetComponent<AutoEngineer>() == null`.** The `AutoEngineer` component check means AE-driven locos skip the planner. `AutoEngineerPlanner` is the lower-level "WillMove/ApplyMovement" callback hook used by manual driving; `AutoEngineer` is the higher-level state machine that owns the loco.
6. **`cabControls` is created in `PreSetupComponents(Model)`** but populated in `ConnectBodyControls` which runs in `DidSetBodyActive`. There's a window where `cabControls != null` but its slots are null. The `DummyControl()` fallbacks (`BaseLocomotive.cs:267-285`) prevent NREs but mean some controls silently no-op.
7. **`LocomotiveCabControlsHookup` slot names mix steam and diesel terminology**: `throttle` (diesel), `regulator` (steam), `reverser` (diesel), `johnsonBar` (steam), `boilerPressure` (steam), `mainReservoir` (both), etc. A custom loco type uses whichever slots fit. Adding new slots requires extending the hookup component (recompile).
8. **`maxSpeedMph` is randomized per-loco-instance and not in the definition**. Diesel: `Random.Range(63f, 66f)`. Steam: `engine.driverDiameterInches + Random.Range(5, 10)` (the inches→mph coincidence noted in [traction.md](traction.md)). Save→reload re-randomizes? Verify: `FinishSetup` runs on initial creation only, not on load — so `maxSpeedMph` for a loaded loco is whatever was saved. Confirm via the snapshot pipeline if you need determinism.
9. **The whole engine-model contract is duck-typed.** `SteamEngine` and `PrimeMover` share no interface. `BaseLocomotive` knows nothing about them. The "engine" is purely a subclass-private MonoBehaviour. This means an alt-loco-type mod can use any shape of "engine" — a battery + motor split, a generator+TM split, a hybrid pair — as long as the subclass + adapter consume it correctly.
10. **Steam `_subcomponents` list is rebuilt on every model load**, but the order is `GetComponentsInChildren` traversal order (depth-first, pre-order). Mods that depend on dispatch order (e.g., wheel animator before chuff for stale `DriverPhase`) need to ensure component/transform hierarchy enforces order. The wheel animator is added first (`AddComponent` in `DidLoadModels:101`) but then `_subcomponents.AddRange` traverses children — so the wheel animator (added directly to `BodyTransform.gameObject`) is found first, then children's subcomponents. Fragile.

---

## Cross-references

### To Traction ([traction.md](traction.md))
- TE pipeline, `UpdateTractiveEffortWheelState` formula, slip handling: [traction › spine](traction.md#traction-spine-how-a-notch-becomes-movement).
- `PrimeMover` and `SteamEngine` internals (notch table, regulator/cutoff, fuel curves): [traction › Diesel](traction.md#modeldiesellocomotive--diesel-electric-subclass) and [traction › Steam](traction.md#modelsteamlocomotive--steam-subclass).
- `LocomotiveControlAdapter` polymorphism table: [traction › LocomotiveControlAdapter](traction.md#locomotivecontroladapter--the-abstract-bridge).
- DPU/MU patch surface: [traction › DPU experiment guidance](traction.md#dpu-experiment-guidance-call-out).

### To Brakes ([brakes.md](brakes.md))
- `LocomotiveAirSystem.UpdateLocomotiveBrakeControlLine` and bail-off sentinel: [brakes › independent brake](brakes.md#locomotive-independent-brake--direct-cylinder-injection).
- Train brake "lap" pressure (`_lapTrainBrakePressure`) and partial-release semantics: [brakes › lap pressure & release detection](brakes.md#lap-pressure--release-detection).
- Cut-out behaviour and the MU+CutOut interlock: [brakes › cut-out](brakes.md#cut-out) and [brakes › MU coordination](brakes.md#mu-coordination).
- `Car.CalculateBrakingForce` and per-archetype `nominalBrakingForce`: [brakes › CalculateBrakingForce](brakes.md#carcalculatebrakingforce--the-physical-hand-off).

### To Air System ([air-system.md](air-system.md))
- `Reservoir`, `VentedValve`, `AirConnection` primitives that `LocomotiveAirSystem` composes.
- Brake-pipe propagation (chain of reservoirs through `_mainReservoirToBrakeLine`).

### To MU/DPU Coordination ([mu-dpu-coordination.md](mu-dpu-coordination.md))
- `BaseLocomotive.PeriodicUpdateForMu` 1Hz mirror, `FindMuSourceLocomotive` F-then-R search.
- `LocomotiveAirSystem._ShouldDeferToLocomotiveAir` (`UpdateCachedShouldDeferToLocomotiveAir` is called from `PeriodicUpdateForMu`).
- AutoEngineer's `FixMuCutOutIfNeeded` mutual exclusion.
- The trail-unit pattern (cut-out + MU + brake pipe).

### To Audio ([audio.md](audio.md))
- `IChuffProvider` extends `ISteamLocomotiveSubcomponent`; `Chuff` is the canonical chuff producer wired through `_chuffAudio.Configure(driverDiameter, normalizedEngineSize)` in `SteamLocomotive.DidLoadModels`.
- `DynamoPlayer`, `FireboxEffectController` use `OnIdleDidChange` + `OnHasFuelDidChange` events (NOT subcomponent interface).
- `SteamChuffParticleController` is both `ISteamLocomotiveSubcomponent` and `IDynamicChuffDelegate` — gets per-distance ticks AND delegated chuff schedules from `Chuff`.

### To Cars & Cargo ([cars-cargo.md](cars-cargo.md))
- `Car` lifecycle (Setup, FinishSetup, DidLoadModels, UnloadModels) — the framework `BaseLocomotive` extends.
- `Car.SendPropertyChange` — the client→host KVO write entry point used by `LocomotiveControlHelper`.

### To Car Definitions ([car-definitions.md](car-definitions.md))
- `CarDefinition` subclassing pattern (`Kind` string, custom fields).
- `Component` + `IComponentBuilder` registry. Mirror `ChuffComponentBuilder` / `CylinderCockComponentBuilder` / `CompressorComponentBuilder` for new declarative components.
- `DefinitionChecker` for definition validation at load.
