# Traction — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Couplers](couplers.md), [Wear & Durability](wear-durability.md), [physics-vanilla-survey](../physics-vanilla-survey.md)

Locomotive traction in Railroader is a single per-locomotive `_tractiveEffort` scalar, recomputed every `FixedUpdate` by `BaseLocomotive.UpdateTractiveEffortWheelState()` from three abstract inputs the subclass supplies (`CalculateTractiveEffort(signedVelocityMph)`, `AdhesiveWeight`, and the public `maxSpeedMph` random per-loco overspeed clamp). The scalar is multiplied by `Car.TractiveForceMultiplier` (1.1, static), then enters the consist solver only via `IntegrationSet.UpdateAcceleration()` which sums `TractiveForce + GravityForce`, divides by mass, and writes to `Element.acceleration`. Two subclasses exist — `DieselLocomotive` (8-notch + neutral, electric-drive curve in `PrimeMover`) and `SteamLocomotive` (continuous regulator + ±1 reverser/cutoff, piston-diameter formula in `SteamEngine`). **There is no separate "generator → traction motor" chain on diesels** — the prime mover hands a TE number directly. **There is no dynamic brake, no sander, no wheel slip beyond a 3-state enum, no per-axle torque.** MU is a "throttle/reverser KVO mirror" pulled by the slave loco at 1 Hz, not a dedicated MU bus. Throttle/reverser/MU/cut-out KVO writes are crew-authority by default (no leading underscore), so any train-crew client can drive any locomotive on its assigned crew.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `BaseLocomotive.UpdateTractiveEffortWheelState()` | `Model/BaseLocomotive.cs:518` | The TE+slip pipeline. Called every `FixedUpdate`. **Single chokepoint.** |
| `BaseLocomotive.CalculateTractiveEffort(float signedVelocityMph)` (abstract) | `Model/BaseLocomotive.cs:516` | Subclass plug-in: maps wheel velocity → TE lbf (signed, reverser-respecting) |
| `BaseLocomotive.AdhesiveWeight` (abstract) | `Model/BaseLocomotive.cs:71` | Diesel = `Weight`, Steam = `engine.weightOnDrivers` |
| `Car.TractiveForce` | `Model/Car.cs:751` | `TractiveForceMultiplier (=1.1) * TractiveEffort`. Read by `IntegrationSet.UpdateAcceleration` |
| `IntegrationSet.UpdateAcceleration()` | `Model.Physics/IntegrationSet.cs:385` | Force→accel sum (per car: `(TractiveForce + GravityForce) * 4.44822 / massKg`) |
| `PrimeMover.CalculateTractiveEffort(float absMph)` | `Model.Physics/PrimeMover.cs:48` | Diesel TE curve. Notch→power% with rate-limited spool (0.1/0.5 per-sec) |
| `SteamEngine.CalculateTractiveEffort(float wheelVelocityMph)` | `Model.Physics/SteamEngine.cs:70` | Steam TE = `regulator * sign(reverser) * MaxTE(absMph) * ReverserPowerMultiplier(...)` |
| `LocomotiveControlAdapter` | `RollingStock/LocomotiveControlAdapter.cs` | Abstract bridge: `AbstractThrottle`, `AbstractReverser`. KVO-observed in `BaseLocomotive.ObserveCoreProperties` |
| `LocomotiveControlHelper.Throttle/Reverser` | `Model/LocomotiveControlHelper.cs:14, 26` | Public clamped writers — fire `PropertyChange` |
| `BaseLocomotive.PeriodicUpdateForMu()` | `Model/BaseLocomotive.cs:144` | The 1Hz MU mirror. **Slave pulls** from master via `FindMuSourceLocomotive` |
| `PropertyChange.Control.Throttle / Reverser / Mu / CutOut` | `Game.Messages/PropertyChange.cs:14-29` | KVO key enum. Wire keys: `"throttle"`, `"reverser"`, `"mu"`, `"cutOut"` |

---

## Traction spine: how a notch becomes movement

```
User drags throttle slider in cab (or UI panel)
   │  RadialAnimatedControl.OnValueChanged  (debounced ~ChangeThreshold)
   ▼
ContinuousControl.SendValue() → invokes wired delegate
   │
   ├── Diesel: cabControls.throttle.OnValueChanged → ControlHelper.Throttle = v
   │   Steam:  cabControls.regulator.OnValueChanged → ControlHelper.Throttle = v
   │   UI panels (ManualControls / SimplifiedControls / AutoEngineer): PropertyChange directly
   ▼
LocomotiveControlHelper.ChangeValue(Throttle, v)
   │  Car.SendPropertyChange(Throttle, v) → StateManager.ApplyLocal(PropertyChange)
   ▼
KVO write to "throttle" key on this car (auth: MinimumLevelCrew + trainCrewId)
   │
   ├── Local apply (KVO Set, origin Local) → observer fires
   └── If client != null → broadcast PropertyChange → host approves → rebroadcast
   ▼
BaseLocomotive observer (BaseLocomotive.cs:364) fires:
   locomotiveControl.AbstractThrottle = value.FloatValue
   if (>0.001) ResetAtRest(); ResetIdleTimer();
   ▼
Adapter setter routes to subsystem:
   DieselLocomotiveControl.AbstractThrottle.set:
     primeMover.notch = Round(value * 8)            // ← LOSSY ROUND
     audio.primeMover.Notch = primeMover.notch
   SteamLocomotiveControl.AbstractThrottle.set:
     engine.regulator = value                        // 0..1, no rounding here
   ▼
Next FixedUpdate (50 Hz):
BaseLocomotive.UpdateTractiveEffortWheelState():
   1. µ = TrainMath.TrackCoefficientOfFriction(Dry, _wheelVelocity)
   2. if IsDerailed: µ = 0.1; if was Slipping: µ = min(µ, 0.1)
   3. adhesionLimit = AdhesiveWeight * µ
   4. rawTE = TractiveForceMultiplier * subclass.CalculateTractiveEffort(_wheelVelocity * 2.23694)
        * Config.tractiveEffortMultForCondition.Evaluate(Condition)
        * lerp(1, 0, InverseLerp(maxSpeedMph, maxSpeedMph+1, |_wheelVelocity| * 2.23694))
   5. if |rawTE| > |adhesionLimit|: _wheelState = Slip, else Tracking
   6. wheel velocity update:
        Tracking: _wheelVelocity → MoveTowards(velocity, dt*10)
        Slip:     _wheelVelocity += rawTE * dt² * slipSpeed; clamp |≤ |velocity| + 17.88157|
        Lock:     _wheelVelocity → MoveTowards(0, dt*10)    ← never set; dead branch
   7. _tractiveEffort = sign(rawTE) * min(|adhesionLimit|, |rawTE|)
   ▼
IntegrationSet.UpdateAcceleration() (next IntegrationSet.Tick, same FixedUpdate):
   foreach element:
     forceN     = (orientation * car.TractiveForce + orientation * car.GravityForce) * 4.44822
     element.acceleration = -forceN / (Weight * 0.453592)        // mass in kg
   ▼
ApplyVerlet → ApplyBrakes → PositionCars → world-space update
```

Five surprising things in this spine:

1. **Verlet uses the negation of force / mass** at `IntegrationSet.cs:397`. This pairs with the integrator orientation; net direction is correct, but a naive postfix that reads `element.acceleration` and expects `+force/mass` will get the sign wrong.
2. **`AbstractThrottle` round-trips through an int notch on diesels.** `DieselLocomotiveControl.AbstractThrottle.set` rounds to `0..8` (`DieselLocomotiveControl.cs:34`). The getter then returns `notch / 8f`. So writing 0.1 yields 0.125 back; writing 0.06 yields 0. Continuous mod inputs (e.g., AutoEngineer's PID) lose precision here.
3. **Wheel velocity feeds the TE curve, not consist velocity.** `CalculateTractiveEffort` is invoked with `_wheelVelocity * 2.23694` (line 530), so during slip the perceived "speed" is the spinning wheel — the curve correctly drops the slipping wheel's TE as it accelerates ahead of the consist.
4. **The `Lock` wheel state is dead.** Nothing sets `_wheelState = Lock` anywhere in vanilla. The branch in the switch is reachable only via reflection.
5. **`Car.TractiveForceMultiplier` is a process-global static** (`Car.cs:753`) initialized to `1.1f`. There is no UI exposure, no setting key. Patch it once and every loco scales. Worth a callout — easiest way to globally retune traction without touching curves.

---

## `Model.BaseLocomotive` — the abstract base

Per-locomotive MonoBehaviour. Holds the wheel-state machine, adhesion gate, MU mirror, the Idle tracker, and the cab-control wiring.

### State fields

```csharp
private float        _tractiveEffort;                 // 29  ; current applied TE (lbf, signed)
protected float      _wheelVelocity;                  // 31  ; m/s, drifts with slip
private CarWheelState _wheelState;                    // 33  ; Tracking | Slip | Lock
private float        _idleTimerLastReset;             // 37  ; idle decay, 600s
public  float        slipSpeed = 0.05f;               // 39  ; slip-acceleration coefficient
private const float  LocomotiveBrakeNegative = -0.1f; // 41  ; bail-off sentinel
```

### Public surface

```csharp
public override float TractiveEffort => _tractiveEffort;             // 45
public override CarWheelState WheelState => _wheelState;             // 47
public abstract float RatedTractiveEffort { get; }                   // 53
public bool IsIdle { get; private set; }                             // 55  KVO "idle" (bool)
public abstract bool HasFuel { get; }                                // 67
internal bool IsMuEnabled => KVO["mu"].BoolValue;                    // 69
protected abstract float AdhesiveWeight { get; }                     // 71
public LocomotiveControlAdapter locomotiveControl;                   // 27
public LocomotiveControlHelper  ControlHelper { get; private set; }  // 51
public AutoEngineerPlanner      AutoEngineerPlanner { get; private set; } // 49
public abstract float MaxTractiveEffortAtVelocity(float absMph);     // 611
public abstract float CutoffSettingForVelocity(float velocityMps);   // 613
public abstract float CalculateTractiveEffort(float signedVelocityMph); // 516 — protected
```

### `UpdateTractiveEffortWheelState` — the canonical TE pipeline

```csharp
private void UpdateTractiveEffortWheelState()                        // 518
{
    float mu = TrainMath.TrackCoefficientOfFriction(TrainMath.TrackCondition.Dry, _wheelVelocity);
    if (IsDerailed)               mu = 0.1f;
    if (_wheelState == Slip)      mu = Mathf.Min(mu, 0.1f);
    float adhesionLimit = AdhesiveWeight * mu;

    float rawTE = Car.TractiveForceMultiplier * CalculateTractiveEffort(_wheelVelocity * 2.23694f);
    rawTE *= Config.tractiveEffortMultForCondition.Evaluate(Condition);
    rawTE *= Mathf.Lerp(1, 0,
              Mathf.Clamp01(Mathf.InverseLerp(maxSpeedMph, maxSpeedMph + 1f,
                            Mathf.Abs(_wheelVelocity) * 2.23694f)));

    _wheelState = (Mathf.Abs(rawTE) > Mathf.Abs(adhesionLimit)) ? Slip : Tracking;

    float dt = Time.deltaTime;
    switch (_wheelState) {
        case Tracking: _wheelVelocity = Mathf.Lerp(_wheelVelocity, velocity, dt*10);   break;
        case Slip:     _wheelVelocity += rawTE * dt*dt * slipSpeed;
                       _wheelVelocity  = Mathf.Sign(_wheelVelocity)
                                       * Mathf.Min(Mathf.Abs(velocity) + 17.88157f,
                                                   Mathf.Abs(_wheelVelocity));         break;
        case Lock:     _wheelVelocity = Mathf.Lerp(_wheelVelocity, 0f, dt*10);         break;  // DEAD
    }

    _tractiveEffort = Mathf.Sign(rawTE) * Mathf.Min(Mathf.Abs(adhesionLimit), Mathf.Abs(rawTE));
}
```

(Source: `Model/BaseLocomotive.cs:518-562`.)

Key observations:
- **µ is hard-coded `Dry`.** No weather coupling; same as cited in [physics-vanilla-survey › wheel slip](../physics-vanilla-survey.md#wheel-slip--adhesion).
- **Slip-floor µ = 0.1.** Once slipping, friction drops to a constant 0.1 until tracking is re-acquired.
- **Speed clamp is per-loco, +1 mph hard rolloff.** `maxSpeedMph` is randomized per loco (diesels: `Random.Range(63, 66)` at `DieselLocomotive.cs:126`; steam: `engine.driverDiameterInches + Random.Range(5, 10)` at `SteamLocomotive.cs:216`). At max+1 mph TE → 0. **There is no overspeed damage** beyond this — the TE cuts out and that's the only enforcement.
- **Condition multiplies TE** via `Config.tractiveEffortMultForCondition` (`Linear(0,0)→(1,1)` default). At Condition 0, TE is 0. Damaged engines are progressively gutless — this is the only place wear ties to performance.
- **Slip-acceleration uses `dt²` and `slipSpeed = 0.05`.** This is intentionally tiny: slip is a flag, not a runaway. The spin-up clamp is `|consistVelocity| + 17.88157 m/s` (= +40 mph relative).

### `FixedUpdate` order

```csharp
protected override void FixedUpdate() {                              // 92
    UpdateTractiveEffortWheelState();                                // ← ours
    base.FixedUpdate();                                              // → Car.FixedUpdate (sway, position)
    UpdateCabControls();                                             // ← gauge mirror
}
```

**TE updates BEFORE Car/IntegrationSet ticks** for this car, but `IntegrationSet.UpdateAcceleration` reads `car.TractiveForce` from each car when the set ticks. So even if TE changes mid-loop, the value used is the one written before this car's `FixedUpdate` (which precedes the integration tick at `TrainController.FixedUpdate` orchestration). Postfix `BaseLocomotive.FixedUpdate` if you need to override TE *after* the slip clamp.

### `PeriodicUpdateForMu` — the 1Hz MU mirror

```csharp
private void PeriodicUpdateForMu() {                                 // 144
    if (air is LocomotiveAirSystem la) {
        la.IsMuEnabled = IsMuEnabled;
        la.UpdateCachedShouldDeferToLocomotiveAir();
    }
    if (StateManager.IsHost && IsMuEnabled) {
        BaseLocomotive master = FindMuSourceLocomotive();
        if (master != null) {
            float throttle = master.locomotiveControl.AbstractThrottle;
            float cutoff   = CutoffSettingForVelocity(velocity);
            cutoff *= ((FrontIsA == master.FrontIsA) ? 1 : -1)
                    * Mathf.Sign(master.locomotiveControl.AbstractReverser);
            cutoff = Mathf.CeilToInt(cutoff * 20f) / 20f;            // snap to 0.05
            SendPropertyChange(Throttle, throttle);
            SendPropertyChange(Reverser, cutoff);
        }
    }
}
```

(Source: `Model/BaseLocomotive.cs:144-164`. Called from `PeriodicUpdateBody` coroutine, 1 second cadence at line 114.)

Five things to know about MU:

- **MU is a slave-pulled mirror.** Each MU-enabled locomotive looks for its master (nearest non-tender locomotive of the same air-circuit, via `FindSourceLocomotive`) and copies the master's throttle and a cutoff-derived reverser. **There is no MU broadcast bus.**
- **The reverser sent to the slave is recomputed from the slave's own velocity** via `CutoffSettingForVelocity(velocity)`, then multiplied by the master's reverser sign and the relative orientation `FrontIsA == master.FrontIsA ? 1 : -1`. So a backwards-facing MU'd unit gets the inverted reverser without the user doing anything.
- **MU writes happen at 1Hz on the host only.** Latency to the master changing throttle is up to 1 second.
- **MU implies CutOut.** The Inspector UI forces `CutOut = true` when you toggle MU on (`UI.CarInspector/CarInspector.cs:201`). Reverse: clearing CutOut also clears MU (`CarInspector.cs:192`). The handler in `LocomotiveAirSystem._ShouldDeferToLocomotiveAir` (`LocomotiveAirSystem.cs:146`) requires both `IsCutOut && IsMuEnabled` to defer air control — so MU without CutOut breaks air integration.
- **AutoEngineer disables MU at startup.** `AutoEngineer.FixMuCutOutIfNeeded` (`Model.AI/AutoEngineer.cs:843`) clears MU on the controlled loco and clears CutOut if it's the only loco. So MU is a *manual* feature; AE always drives one loco directly and the others coast.

### `FindMuSourceLocomotive` — search topology

```csharp
private BaseLocomotive FindSourceLocomotive(LogicalEnd searchDirection) {  // 166
    int idx = set.IndexOfCar(this);
    LogicalEnd fromEnd = (searchDirection == LogicalEnd.A) ? LogicalEnd.B : LogicalEnd.A;
    while ((car = set.NextCarConnected(ref idx, fromEnd, AirAndCoupled, out stop)) != null) {
        if (car == this || car.Archetype == CarArchetype.Tender) continue;
        if (!car.IsLocomotive || !(car is BaseLocomotive bl)) return null;
        if (!bl.locomotiveControl.air.IsCutOut) return bl;             // ← master = first non-CutOut loco
    }
    return null;
}

internal BaseLocomotive FindMuSourceLocomotive() =>
    FindSourceLocomotive(EndToLogical(End.F)) ?? FindSourceLocomotive(EndToLogical(End.R));   // 195
```

Walks the consist via `IntegrationSet.NextCarConnected` requiring **air AND coupled**. Skips tenders and self. **Picks the first locomotive that is NOT cut-out** as master. Searches F first, then R.

**DPU-relevant**: there's no notion of "lead unit"; whichever loco is closest to the slave on the consist (in F-search direction first, fallback R) and not cut-out becomes its master. If you put two non-cut-out locos at opposite ends of a long consist with a third in the middle that's MU+cut-out, the middle one mirrors whichever end-loco is found in F-search direction. **Order-dependent, no deterministic "engineer's seat."**

### Patch candidates (BaseLocomotive)

| Method | Why patch |
|---|---|
| `BaseLocomotive.UpdateTractiveEffortWheelState` | Single chokepoint for all TE/slip logic. Postfix to override `_tractiveEffort` (e.g., add weather, regenerate sander, custom slip). |
| `BaseLocomotive.CalculateTractiveEffort` (abstract → on `DieselLocomotive` / `SteamLocomotive`) | Replace TE curve cleanly without touching slip math. Postfix on the *subclass* method. |
| `BaseLocomotive.AdhesiveWeight` (getter, abstract) | Override to adjust adhesive weight (e.g., grade load shift). Patch the subclass getter. |
| `BaseLocomotive.PeriodicUpdateForMu` | Insert custom MU/DPU logic, e.g., per-unit power split, dynamic-brake mirror, fenced traction. |
| `BaseLocomotive.FindMuSourceLocomotive` | Replace MU master selection (e.g., explicit lead-unit selection for DPU). |
| `BaseLocomotive.FixedUpdate` | Earliest insertion point per-loco; runs before consist physics. |
| `Car.TractiveForceMultiplier` (static field, `Car.cs:753`) | Global TE scaling without per-class patch. |
| `Car.TractiveForce` (getter) | Patch this getter to clamp/transform TE going *into* the integrator (alternative to patching `_tractiveEffort` itself). |

### MP authority (BaseLocomotive control writes)

All control KVO keys here are **non-HostOnly** (no leading underscore), so `Car.AuthorizationRequirementForPropertyWrite` (`Car.cs:3146`) returns the default `MinimumLevelCrew + trainCrewId`. That means:

| KVO key | Auth |
|---|---|
| `throttle` | Crew + assigned-trainCrew check |
| `reverser` | Crew + assigned-trainCrew check |
| `mu` | Crew + assigned-trainCrew check |
| `cutOut` | Crew + assigned-trainCrew check |
| `idle` | Crew + assigned-trainCrew check (host writes this in `PeriodicUpdateBody`) |
| `compressor` | Crew + assigned-trainCrew check (host writes in `UpdateCabControls`) |

No `RequestThrottle`/`RequestReverser` messages exist — clients write KVO directly, host validates via the standard `PropertyChange` auth pipeline. **There is no "throttle-lock at speed" gate** — clients can flip the reverser at 60 mph; the only consequence is the reverser-power-multiplier (steam) or sign-multiplied power% (diesel) → the wheel-velocity may swing wildly if rawTE flips sign relative to physical motion.

### Idle tracking (cross-cutting)

`IsIdle` (KVO `idle`, bool) flips true when `|velocity| < 0.01 m/s` AND `Time.time - _idleTimerLastReset > 600` (10 minutes). `ResetIdleTimer()` is called by every control-write observer (throttle, reverser, brakes, horn, bell, cut-out). The KVO observer is wired in `DidLoadModels` (line 454). Consumers: `OnIdleDidChange` event (line 73) — used for fuel/maintenance display in UI.

### Gotchas (BaseLocomotive)

- **`_wheelVelocity` is per-loco state.** Multi-loco consists each have their own; slip is independent. MU does NOT synchronize wheel velocity — only throttle/reverser via the KVO mirror.
- **`maxSpeedMph` is randomized per car instance.** Save→reload restores it (it's part of restored Car state via base `FinishSetup` → recompute). For deterministic mods, override `maxSpeedMph` in a postfix on `FinishSetup` of each subclass.
- **Steam `maxSpeedMph` formula uses `driverDiameterInches`** directly as a number-of-mph (e.g., 63" drivers → 68..73 mph). This is a *coincidence* of unit choice, not physics; it conflates inches with mph.
- **`FixedUpdate` calls `UpdateTractiveEffortWheelState` even on the client** — there's no `IsHost` gate. The TE write is local; clients compute it identically because all inputs are KVO-replicated. Patches that add mod state to the formula must replicate the state.
- **`PeriodicUpdate` runs every 1 second on a coroutine, not FixedUpdate.** MU mirror lag is 0..1s. Fuel consumption (diesel/steam) ticks at the same cadence.
- **`AutoEngineerPlanner` is created in host-side `FinishSetup`.** Clients have `null` `AutoEngineerPlanner`. Patches that reference it must null-check.
- **`DummyControl` substitution**: if a loco prefab lacks a `RadialAnimatedControl` for throttle/reverser/brake, `BaseLocomotive.ConnectBodyControls` substitutes a `DummyControl` GameObject. Patches that traverse `cabControls.throttle` get a real component but with no animation. Caused by missing `ControlPurpose.Throttle` tag on the prefab.

---

## `Model.DieselLocomotive` — diesel-electric subclass

Hosts a `PrimeMover` MonoBehaviour. **There is no separate generator/traction-motor model** — `PrimeMover` is the entire powertrain. The "amps" field is decorative (computed from TE / max TE * 900) and used only for cab gauges.

### Per-class state

```csharp
public PrimeMover primeMover;                                        // 19
private IPrimeMoverAudioPlayer _primeMoverAudioPlayer;               // 21
private List<DieselExhaustParticleController> _particleControllers;  // 23
public override float NormalizedTractiveEffort => primeMover.NormalizedTractiveEffort; // 27
public override float RatedTractiveEffort => primeMover.startingTractiveEffort;        // 29
public override bool  HasFuel => primeMover.HasFuel;                 // 31
protected override float AdhesiveWeight => Weight;                   // 33  ← FULL CAR WEIGHT
```

### Throttle binding (notch handling)

```csharp
// DieselLocomotive.ConnectBodyControls — line 70-83
if (TryGetControl(ControlPurpose.Throttle, out var throttleControl)) {
    cabControls.throttle = throttleControl;
    throttleControl.OnValueChanged += v => ControlHelper.Throttle = v;
    throttleControl.CheckAuthorized = () =>
        StateManager.CheckAuthorizedToSendMessage(new PropertyChange(id, Throttle, 0));
    throttleControl.tooltipText = () => {
        int n = locomotiveControl.ThrottleDisplay;
        return n == 0 ? "Idle" : $"Notch {n}";
    };
}
```

`ThrottleDisplay` = `RoundToInt(AbstractThrottle * 8)` (`LocomotiveControlAdapter.cs:20`). For diesel, since `AbstractThrottle = notch / 8f`, this is just `notch`. The tooltip displays "Idle" when notch=0 else "Notch {1..8}".

### Reverser binding

```csharp
if (TryGetControl(ControlPurpose.Reverser, out var rev)) {
    cabControls.reverser = rev;
    rev.OnValueChanged += v => {
        int r = Mathf.RoundToInt(Mathf.Lerp(-1f, 1f, v));            // ← slider 0..1 → -1/0/+1
        ControlHelper.Reverser = r;
    };
    rev.tooltipText = () =>
        primeMover.reverser >= 0
          ? (primeMover.reverser <= 0 ? "Neutral" : "Forward")
          : "Reverse";
}
```

**Diesel reverser is tri-state {-1, 0, +1}.** The cab control slider is normalized 0..1 (R/N/F). The KVO write goes through `ControlHelper.Reverser` which clamps to [-1,1]; the *adapter* setter then `RoundToInt`s it back to int in `DieselLocomotiveControl.AbstractReverser.set` (`DieselLocomotiveControl.cs:23`). So intermediate values from non-cab UI (e.g., AutoEngineer writes `±cutoff`) will be rounded to {-1, 0, +1}.

**No interlock prevents reverser changes at speed.** A user (or mod) setting reverser = -1 at 50 mph will instantly flip the prime mover sign; rawTE goes hugely negative; `_wheelState` goes Slip; consist decelerates. No damage, no warning.

### `PrimeMover` (the powertrain)

```csharp
public class PrimeMover : MonoBehaviour {                            // PrimeMover.cs
    public int   startingTractiveEffort = 49500;                     // 8
    public bool  running = true;                                     // 10
    public int   reverser;     // -1 / 0 / +1                        // 12
    public int   notch;        // 0..8                               // 14
    public float tractiveEffort, amps, rpms;                         // 16-20
    private const int MaxAmps = 900;                                 // 22
    private readonly int[]   _notchToRpm        = { 300,362,425,487,550,613,675,738,800 };  // 24
    private readonly float[] _notchToPowerPercent = { 0,0.04f,0.13f,0.23f,0.35f,0.5f,0.65f,0.83f,1f }; // 26
    private float actualPowerPercent;                                // 28
    public bool  HasFuel { get; set; } = true;                       // 46

    public float CalculateTractiveEffort(float absMph) {             // 48
        if (!running || !HasFuel) { amps=0; rpms=0; tractiveEffort=0; return 0; }
        float target = _notchToPowerPercent[notch];
        // SPOOL RATE: 0.1 to climb, 0.5 to drop  (asymmetric)
        float maxDelta = Time.deltaTime * (actualPowerPercent < target ? 0.1f : 0.5f);
        actualPowerPercent = Mathf.MoveTowards(actualPowerPercent, target, maxDelta);
        float signedPower  = actualPowerPercent * reverser;
        tractiveEffort     = signedPower * MaxTractiveEffort(absMph);
        amps               = CalculateAmps(absMph);
        rpms               = _notchToRpm[notch];
        return tractiveEffort;
    }

    public float MaxTractiveEffort(float absMph) =>
        CalculateTractiveEffort(absMph, startingTractiveEffort);     // 67

    // The TE curve: starting TE for 0..10 mph, then 80000-normalized falloff
    private static float CalculateTractiveEffort(float mph, float startingTractiveEffort) {  // 72
        float t = Mathf.Clamp01(Mathf.InverseLerp(0f, 10f, mph));
        float scale = startingTractiveEffort / 80000f;
        if (mph < 10f)
            return Mathf.Lerp(startingTractiveEffort,
                              CalculateContinuousTractiveEffort80000(10f) * scale, t);
        return CalculateContinuousTractiveEffort80000(mph) * scale;
    }

    private static float CalculateContinuousTractiveEffort80000(float mph) =>           // 83
        16253.46f + 201411f / Mathf.Pow(2f, mph / 4.534249f);
}
```

(Source: `Model.Physics/PrimeMover.cs`.)

**TE curve facts:**
- **Starting TE** comes from `DieselLocomotiveDefinition.StartingTractiveEffort` (default `49500` lbf) at `DieselLocomotive.FinishSetup` line 125.
- **Below 10 mph**: linear lerp from `startingTractiveEffort` down to the 10-mph continuous value.
- **Above 10 mph**: curve `16253.46 + 201411 / 2^(mph/4.534)` scaled by `startingTE / 80000`. Asymptotes to ~16253 lbf at infinite speed (per 80k base unit); for a 49500 lbf loco that's ~10056 lbf.
- **`actualPowerPercent` is rate-limited.** Notch up: 0.1/sec (10 seconds idle→full). Notch down: 0.5/sec (2 seconds full→idle). Asymmetric "spool" — engineers feel it. Mods that want instant response should patch `PrimeMover.CalculateTractiveEffort` to set `actualPowerPercent = target` immediately.
- **`reverser == 0` zeros TE entirely** because `signedPower = actualPowerPercent * reverser`. So neutral is a hard cut, not a coast — the loco still imposes its drag (zero applied force) which the integrator sees as zero TractiveForce.

### Fuel consumption (diesel)

`PrimeMover.FuelConsumptionRate` (line 32) = `max(0, (-0.5957576 + 4.96 * x + 1.05 * x²) / 3600)` where `x = notch * startingTE / 64750`. Notch 0 returns 0. Consumed in `DieselLocomotive.PeriodicUpdate` (line 131) at 1Hz from load slot 0. When fuel hits 0, `primeMover.HasFuel = false` flips and `air.HasFuel = false` propagates to the air system; `OnHasFuelDidChange` fires.

### Patch candidates (Diesel)

| Method | Why patch |
|---|---|
| `PrimeMover.CalculateTractiveEffort(float)` | Replace TE curve. Cleanest hook; keeps slip clamp upstream. |
| `PrimeMover.CalculateTractiveEffort(float, float)` (private static) | Replace the curve formula itself. Affects both `MaxTractiveEffort` and the per-tick. |
| `PrimeMover.FuelConsumptionRate` (getter) | Adjust fuel curve. |
| `DieselLocomotive.CalculateTractiveEffort(float signedVelocityMph)` | Top-level subclass override. Wraps the prime mover; patch here to add mod-state inputs. |
| `DieselLocomotive.AdhesiveWeight` (getter) | Override per-class adhesive-weight policy (default = full Weight). |
| `DieselLocomotive.CutoffSettingForVelocity` | Currently returns `1f` (no cutoff). Override if your mod wants notch-shaping at speed. |
| `DieselLocomotive.PeriodicUpdate` | Per-second fuel/loco-state loop. Hook for additional periodic state. |
| `DieselLocomotiveControl.AbstractThrottle.set` | Catch the int rounding. Patch the setter to keep continuous fractional notch if your mod needs it. |

### Gotchas (Diesel)

- **`_notchToPowerPercent[0] = 0`** but `_notchToRpm[0] = 300` — RPM is still tracked at idle for audio/exhaust visualization. Idle is a *power* floor, not an RPM floor.
- **`actualPowerPercent` is not reset on reverser change.** Flipping reverser preserves spool state. A loco at notch 8 / forward → reverser to reverse → still pulling at full power but now in reverse direction with no spool delay. This is the "throttle slam in reverse" footgun.
- **No protection against negative `notch` or > 8.** Direct KVO write of throttle = 2.0 (bypassing the cab slider) would yield notch = 16, indexing the array out of bounds. The `LocomotiveControlHelper.Throttle.set` clamps to `[0,1]` (`LocomotiveControlHelper.cs:22`), but `ControlProperties[Throttle] = something` does not. Mods writing directly through `ControlProperties` can crash the loco.
- **`amps` is not used for any computation** — purely a gauge readout. It's a function of the same `actualPowerPercent` value already represented in TE.
- **`startingTractiveEffort` is an `int` field**, default 49500. Caps (in vanilla) at 80000 due to the curve scaling math. Above ~80000 the curve still works but the asymptotic floor (16253) becomes too low relative to start.
- **`HasFuel` is a public setter.** Test mods toggle it; the host periodic update overwrites it back the next second based on actual load.

---

## `Model.SteamLocomotive` — steam subclass

Hosts a `SteamEngine` MonoBehaviour. Throttle = continuous regulator (0..1). Reverser = continuous johnson bar (-1..+1, snaps to 5% increments via `ConfigureSnap(40)`). No notches.

### Per-class state

```csharp
public SteamEngine engine;                                           // 25
public bool        hasTender = true;                                 // 27
private static readonly float[] CutoffSpeeds = {0,5,10,15,20,25,30,35,40,45};  // 43
private float[]    _cutoffSettings;                                  // 45
private const float ReverserCenteredThreshold = 0.1f;                // 41

public override float NormalizedTractiveEffort => engine.NormalizedTractiveEffort;   // 49
public override float RatedTractiveEffort => engine.MaximumTractiveEffort;           // 51
public override bool  HasFuel => engine.HasWaterAndCoal;             // 53
protected override float AdhesiveWeight => engine.weightOnDrivers;   // 55  ← DRIVER WEIGHT, not full
```

### Throttle/regulator binding

```csharp
// SteamLocomotive.ConnectBodyControls — line 309-330
if (TryGetControl(ControlPurpose.Throttle, out var throttle)) {
    cabControls.regulator = throttle;
    throttle.ConfigureSnap(100);                                     // ← 1% steps
    throttle.OnValueChanged += v => ControlHelper.Throttle = v;
    throttle.tooltipText = () => Percent(throttle.Value);
}
if (TryGetControl(ControlPurpose.Reverser, out var rev)) {
    cabControls.johnsonBar = rev;
    rev.ConfigureSnap(40);                                           // ← 2.5% steps  (-1..1 mapped)
    rev.OnValueChanged += v => ControlHelper.Reverser = Mathf.Lerp(-1, 1, v);
    rev.tooltipText = () => Mathf.Abs(engine.reverser) < 0.1f
        ? "Centered"
        : $"{Mathf.Abs(Mathf.RoundToInt(engine.reverser*100))}% {(engine.reverser<0?"Reverse":"Forward")}";
}
```

- Regulator slider snaps to 1% (100 steps).
- Johnson bar slider snaps to 2.5% (40 steps), value range -1..+1.
- **Reverser-centered threshold = 0.1.** Below |reverser| = 0.1, `ReverserPowerMultiplier` returns 0 (engine produces no TE). Acts as a center deadzone.

### `SteamEngine` (the powertrain)

```csharp
public class SteamEngine : MonoBehaviour {                           // SteamEngine.cs
    public int   numberOfCylinders = 2;                              // 8
    public float pistonDiameterInches = 20f;                         // 10
    public float pistonStrokeInches   = 26f;                         // 12
    public float maximumBoilerPressure = 200f;                       // 14
    public float driverDiameterInches  = 63f;                        // 16
    public float weightOnDrivers       = 108000f;                    // 18
    public float totalHeatingSurface   = 2896f;                      // 20

    [Range(-1,1)] public float reverser;                             // 23
    [Range(0,1)]  public float regulator;                            // 26
    public bool   running = true;                                    // 28
    public float  tractiveEffort, pressure;                          // 30, 32

    [NonSerialized] public float MaximumSpeedMph = 75f;              // 35
    [NonSerialized] public float? OverrideStartingTractiveEffort;    // 38
    private float _reverserPowerMultiplier;                          // 40
    private float _estimatedGrateSqFt;                               // 42

    public  float MaximumTractiveEffort { get; private set; } = 28000f;   // 44
    public  float NormalizedTractiveEffort => Clamp01(|tractiveEffort| / MaximumTractiveEffort); // 46
    public  float WaterConsumptionRate { get; private set; }         // 48
    public  float CoalConsumptionRate  { get; private set; }         // 50
    public  bool  HasWaterAndCoal { get; set; } = true;              // 52

    public  void  UpdateMaximumTractiveEffort();                     // 54  — call after parameter change
    public  float MaximumTractiveEffortAtVelocity(float absMph);     // 65
    public  float CalculateTractiveEffort(float wheelVelocityMph);   // 70  — main TE entry
    private void  UpdateConsumption(float wheelVelocityMph);         // 80
}
```

The TE pipeline (`SteamEngine.CalculateTractiveEffort`, line 70):

```csharp
float dirSign = (reverser < 0) ? -1 : +1;
float regulated = dirSign * regulator;
tractiveEffort  = HasWaterAndCoal
                ? regulated * MaximumTractiveEffortAtVelocity(|wheelVelocityMph|)
                : 0;
_reverserPowerMultiplier = TrainMath.ReverserPowerMultiplier(|reverser|, |wheelVelocityMph|, MaximumSpeedMph);
tractiveEffort *= _reverserPowerMultiplier;
UpdateConsumption(wheelVelocityMph);
return tractiveEffort;
```

**TE flows from regulator + reverser sign + max-TE curve + cutoff multiplier.** Note: regulator sets *amount*, reverser sets *direction* and *cutoff*.

### `TrainMath.ReverserPowerMultiplier` (the cutoff curve)

```csharp
public static float ReverserPowerMultiplier(float absReverser, float absVelocityMph, float maxSpeedMph)  // TrainMath.cs:103
{
    if (absReverser < 0.1f) return 0f;                       // ← center deadzone
    float speedAtFullCutoff = maxSpeedMph * (1 - sqrt(InverseLerp(0.1, 1, absReverser)));
    float scaleByCutoff = Sigmoid(absReverser, 500, -0.05);  // sharp ramp at 0.05 cutoff
    float speedRollOff  = Sigmoid(absVelocityMph,  0.2, 45 - speedAtFullCutoff);
    float reverseLowSpeed = Sigmoid(-absVelocityMph, 0.6, speedAtFullCutoff + 11);
    if (absReverser > 0.9 && absVelocityMph < 1) {           // ← starting blend
        float t = Mathf.Lerp(InverseLerp(0,1, absVelocityMph), InverseLerp(1,0.9f, absReverser), 0.5);
        speedRollOff   = Lerp(1, speedRollOff, t);
        reverseLowSpeed = Lerp(1, reverseLowSpeed, t);
    }
    return speedRollOff * reverseLowSpeed * scaleByCutoff;
}
```

- **Cutoff = |reverser|**: 0.1 deadzone, ramps to 1.0 at full bar.
- **Long cutoff at high speed = no power**: the `speedRollOff` sigmoid kills TE above the speed corresponding to the current cutoff. To make speed at high cutoff, you must shorten the bar. Classic steam behavior.
- **Starting boost**: when reverser > 0.9 AND speed < 1 mph, the rolloff is bypassed via the lerp on lines 116-118, allowing pulling away from a stop.

### `CutoffSettingForVelocity` (steam autopilot helper)

```csharp
public override float CutoffSettingForVelocity(float velocityMps) {  // SteamLocomotive.cs:433
    if (_cutoffSettings == null) {
        _cutoffSettings = new float[CutoffSpeeds.Length];     // {0,5,10,15,20,25,30,35,40,45}
        for (int i = 0; i < CutoffSpeeds.Length; i++) {
            float prev = 0, cutoff = 1f;
            do {                                              // walk down from full cutoff
                float te = TrainMath.ReverserPowerMultiplier(cutoff, CutoffSpeeds[i], maxSpeedMph);
                if (te <= prev) { cutoff += 0.05f; break; }   // local maximum
                prev = te;
                cutoff -= 0.05f;
            } while (cutoff > 0f);
            _cutoffSettings[i] = cutoff;
        }
    }
    // table lookup with linear interp
}
```

Built lazily on first call, cached. For each speed in `CutoffSpeeds`, walks `cutoff` down from 1.0 in 0.05 steps, returning the cutoff that maximizes power at that speed. **This is the "what cutoff should I be at" oracle** consumed by `AutoEngineer` and `SimplifiedControls`. Diesel's override returns 1f always.

### Steam fuel/water consumption

`UpdateConsumption` (line 80) calls `TrainMath.CalculateWaterConsumption(regulator, reverser, wheelVelocityMph, ...)` (`TrainMath.cs:198`) which is a polynomial in piston-volume and pressure. Coal is inferred from water via `TrainMath.InferCoalConsumption` (`TrainMath.cs:211`) using estimated grate area. Per-second update lerps to the new rate (line 85-86) for smoothing. Drained from tender slots in `SteamLocomotive.PeriodicUpdate` line 229.

**Note**: `TrainMath.CalculateCoalWaterConsumption(throttle, maxTE)` (line 127) is a *separate* simpler formula (`coal = 2*throttle*maxTE*2/35000`) that is **not used by anything in vanilla**. Dead helper — likely the older formula. Don't be fooled.

### Tender coupling (steam-specific)

```csharp
protected override bool WantsEndGear(End end)                        // 62
   => hasTender ? (end == End.F) : true;
public  override bool ForceConnectedToAtRear(Car other)              // 71
   => hasTender && Archetype == CarArchetype.Tender;
protected override bool RequiresConnectionToEnd(End end)             // 345
   => hasTender ? (end == End.R) : false;
```

- A steam loco with `hasTender = true` *cannot* uncouple from its tender (rear-end is required).
- Tender's front cannot uncouple from engine (cross-link: [Couplers › ValidateEndGearChange](couplers.md#validation)).
- `IntegrationSet.ValidateConsistency` re-couples a separated engine+tender via `IntegrationSetRequestsReconnect` (cross-link: [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths)).

### Patch candidates (Steam)

| Method | Why patch |
|---|---|
| `SteamEngine.CalculateTractiveEffort(float)` | Replace TE pipeline; cleanest. |
| `TrainMath.TractiveEffort(...)` (static) | Replace the underlying steam TE formula. Static, no instance state. |
| `TrainMath.ReverserPowerMultiplier` | Replace cutoff curve. Affects ALL steam locos. |
| `TrainMath.CalculateWaterConsumption` / `InferCoalConsumption` | Replace fuel curves. |
| `SteamLocomotive.AdhesiveWeight` (getter) | Override driver-weight policy (e.g., consider tender-on-drivers). |
| `SteamLocomotive.CutoffSettingForVelocity` | Override the cutoff oracle (consumed by AutoEngineer + SimplifiedControls). Uses lazy cache `_cutoffSettings`; clear it if you patch dynamically. |
| `SteamEngine.UpdateMaximumTractiveEffort` | Recompute MaxTE after parameter changes (boiler, pistons). |

### Gotchas (Steam)

- **`MaximumTractiveEffort` is computed once in `UpdateMaximumTractiveEffort` and cached.** Mid-run changes to `maximumBoilerPressure`, pistons, etc. are not reflected until you call `UpdateMaximumTractiveEffort()` again. `NormalizedTractiveEffort` would then read wrong.
- **`_estimatedGrateSqFt` is also cached.** Same story; coal consumption uses it.
- **`pressure` is not modeled dynamically.** Set once = `maximumBoilerPressure` in `UpdateMaximumTractiveEffort` (line 57). There is **no boiler pressure decay/recovery**. Steam has water/coal consumption but no fire management — a loco at full regulator runs at max boiler pressure forever (until water runs out).
- **`HasWaterAndCoal` zeros TE entirely.** When either tender slot empties, the loco produces zero TE on the next tick — sudden stop, no warning.
- **`_cutoffSettings` cache is per-instance and lazy.** Computed on first call. If `maxSpeedMph` is patched after first call, old cutoff settings persist.
- **`SubcomponentsApplyDistanceMoved`** (line 422) iterates `ISteamLocomotiveSubcomponent` impls (lubricator, injector, etc.) per movement. Mods adding subcomponents must implement this interface to be ticked.
- **The `2 * 0.85 * d² * stroke * pressure / driver` formula** in `SteamEngineCharacteristics.StartingTractiveEffort` (`TrainMath.cs:28`) is the standard Beyer-Garratt formula but with the factor 0.85 (mean-effective-pressure coefficient) baked in. The `OverrideStartingTractiveEffort` in `SteamLocomotiveDefinition.PublishedTractiveEffort` short-circuits this entirely.
- **`TotalHeatingSurface = 2896` default** drives the TE-falloff curve via `TractiveEffortFalloff` (`TrainMath.cs:65`). Three discrete curves (`teRatio06/14/18`) are blended by the ratio `startingTE / totalHeatingSurface`. Smaller boilers (smaller heating surface for same starting TE) drop off faster.
- **`numberOfCylinders` is hardcoded to 2** at `SteamLocomotive.FinishSetup:209`. Mallets/Garratts not modeled correctly.

---

## `LocomotiveControlAdapter` — the abstract bridge

Defines what the cab control sliders push *into* the engine model:

```csharp
public abstract class LocomotiveControlAdapter : MonoBehaviour {     // RollingStock/LocomotiveControlAdapter.cs
    public LocomotiveAirSystem air;
    public LocomotiveAudio     audio;
    public abstract int   ThrottleInputNotches { get; }              // 12  Diesel:8, Steam:0
    public abstract int   ThrottleValueSteps   { get; }              // 14  Diesel:8, Steam:100
    public abstract float AbstractReverser  { get; set; }            // 16
    public abstract float AbstractThrottle  { get; set; }            // 18
    public virtual  int   ThrottleDisplay => RoundToInt(AbstractThrottle * ThrottleValueSteps); // 20
    public  float LocomotiveBrakeSetting { get; set; } = air.locomotiveBrakeSetting;   // 22
    public  float TrainBrakeSetting      { get; set; } = air.trainBrakeSetting;        // 34
    public  float LocomotiveBrakePressure{ get; set; } = air.locomotiveBrakePressure;  // 46
    public  float Horn  { get; set; }                                // 58
    public  bool  Bell  { get; set; }                                // 85
    public abstract float NormalizedTractiveEffort { get; }          // 100
}
```

**Concrete impls:**

| | `DieselLocomotiveControl` | `SteamLocomotiveControl` |
|---|---|---|
| `ThrottleInputNotches` | 8 | 0 |
| `ThrottleValueSteps` | 8 | 100 |
| `AbstractThrottle` | `notch / 8f` | `engine.regulator` (0..1) |
| `AbstractReverser` | `primeMover.reverser` (-1/0/+1) | `engine.reverser` (-1..+1) |
| Throttle setter | `notch = Round(v * 8)` | `engine.regulator = v` |
| Reverser setter | `primeMover.reverser = Round(v)` | `engine.reverser = v` |

**Patch candidate**: `*Control.AbstractThrottle.set` is the lowest-friction place to intercept all throttle writes (KVO observer in `BaseLocomotive` calls it directly). Same for reverser.

---

## UI surface (control sets)

### `UI.EngineControls.LocomotiveControlsUIAdapter`
Drives the on-screen HUD when `TrainController.Shared.SelectedLocomotive` is set. Five mode-dropdown options: Manual / Simplified / AE Road / AE Yard / AE Waypoint. Switches active control set in `UpdateSelectedControlSet` (line 175).

### `ManualControls` (`UI.EngineControls/ManualControls.cs`)
Four sliders: throttle, reverser, locomotive brake, train brake. Reverser slider is `wholeNumbers` for diesel, continuous for steam (`UpdateForLocomotive` line 47). Throttle slider max = `ThrottleValueSteps` (8 diesel, 100 steam). Each change calls `ChangeValue(PropertyChange.Control.X, v)` which routes through `EngineControlSetBase.ChangeValue` → `Locomotive.SendPropertyChange`.

`ChangeValue` is gated by `_updatingControls && _updatingEngine` flags to prevent feedback loops (`EngineControlSetBase.cs:42`). UI-driven writes only fire outside `Update()` and `UpdateForLocomotive()` callbacks.

### `SimplifiedControls` (`UI.EngineControls/SimplifiedControls.cs`)
Two-axis: power slider (-1..1, 0.15 deadzone) and direction slider (R/F). Negative power → both `LocomotiveBrake` and `TrainBrake` set to `(-power)^1.5`. Positive power → throttle. Reverser is auto-set to `sign(direction) * Locomotive.CutoffSettingForVelocity(velocity)` rounded up to 0.1 increments — so this works correctly for steam (varying cutoff), and yields {-1, 0, +1} for diesel (since `CutoffSettingForVelocity` always returns 1).

### `AutoEngineerRoadControls` / `AutoEngineerYardControls` / `AutoEngineerWaypointControls`
Issue `AutoEngineerCommand` request messages to the host. The host-side `AutoEngineer` MonoBehaviour drives `_control.Throttle` / `.Reverser` / `.LocomotiveBrake` / `.TrainBrake` directly (cross-link: `Model.AI/AutoEngineer.cs`).

### Patch candidates (UI)

| Method | Why patch |
|---|---|
| `EngineControlSetBase.ChangeValue(PropertyChange.Control, float)` | Single chokepoint for *UI-originated* writes. Doesn't catch keyboard, cab interaction, or AutoEngineer. |
| `LocomotiveControlsUIAdapter.SelectedControlSet(AutoEngineerMode)` | Add a custom mode (e.g., a mod-specific control panel). |
| `ContinuousControl.SendValue` (private) | Catch all cab-control change events. |

---

## Power-curve tuning surface

| Knob | Where | Default | Effect |
|---|---|---|---|
| `Car.TractiveForceMultiplier` | `Model/Car.cs:753` | 1.1 (static) | Global TE scaler. Patch once. |
| `Config.tractiveEffortMultForCondition` | `Model/Config.cs:60` | `Linear(0,0)→(1,1)` | TE × this curve evaluated at car Condition (0..1) |
| `Config.serviceDistanceTractiveEffortMultiplier` | `Model/Config.cs:63` | flat 1 | Wear-rate scaler for high-TE running (cross-link: [Wear › ServiceMetersFromActual](wear-durability.md#per-tick-wear-loop)) |
| `PrimeMover._notchToRpm` | `PrimeMover.cs:24` | `{300,362,...,800}` | Audio/exhaust display only |
| `PrimeMover._notchToPowerPercent` | `PrimeMover.cs:26` | `{0,0.04,0.13,0.23,0.35,0.5,0.65,0.83,1}` | Notch→power% mapping |
| `PrimeMover.startingTractiveEffort` | `PrimeMover.cs:8` (init from Definition) | 49500 (fallback) | Diesel max TE at zero speed |
| `PrimeMover.actualPowerPercent` rates | `PrimeMover.cs:58` | 0.1/sec up, 0.5/sec down | Spool dynamics |
| `SteamEngine.maximumBoilerPressure` | `SteamEngine.cs:14` | 200 psi (fallback) | Steam TE multiplier |
| `SteamEngine.totalHeatingSurface` | `SteamEngine.cs:20` | 2896 sq ft | TE-falloff curve selector |
| `BaseLocomotive.slipSpeed` | `BaseLocomotive.cs:39` | 0.05 | Slip wheel-velocity gain |
| `BaseLocomotive.maxSpeedMph` | inherited from `Car.cs:339`, set in subclass `FinishSetup` | Diesel 63..66, Steam ~drivers+5..10 | Hard speed clamp |
| Slip floor µ | `BaseLocomotive.cs:527` | 0.1 (literal) | Min coefficient when slipping |
| Derail floor µ | `BaseLocomotive.cs:523` | 0.1 (literal) | Forced µ when derailed |
| `TrainMath.TrackCoefficientOfFriction` | `TrainMath.cs:91` | Dry: `7.5/(kph+44)+0.161`, Wet: `+0.13`, Slick: `0.05` | Adhesion µ |

Per-loco definition fields (`DieselLocomotiveDefinition`, `SteamLocomotiveDefinition`):

- Diesel: `StartingTractiveEffort` (lbf, default 49500). That's it for traction.
- Steam: `PublishedTractiveEffort` (overrides formula if non-zero), `MaximumBoilerPressure`, `PistonDiameterInches`, `PistonStrokeInches`, `WeightOnDrivers`, `TotalHeatingSurface`, `Wheelsets[]` (driver diameter from `Wheelsets[MainDriverIndex].Diameter`).

---

## Auto-engineer cross-cuts (relevant to traction)

`Model.AI.AutoEngineer` (`Model.AI/AutoEngineer.cs`) is host-side; sets `_control.Throttle/.Reverser/.LocomotiveBrake/.TrainBrake` via `LocomotiveControlHelper`. Key traction-related behaviors:

- **`MaintainSpeed` loop** (line 732) runs every 0.5s. Throttle output via PID (`throttleController`) on speed error. Reverser snapped to `targetSign * CutoffSettingForVelocity(velocity)` whenever throttle > 0 — so AE *will* drive cutoff at speed for steam locos. Diesel is unaffected (cutoff = 1).
- **`CalculateThrottleForTargetVelocityStarting`** (line 626) at < 1 mph: rounds throttle up to next notch (`RoundUpToNotch` = `Mathf.Ceil(t * 8) / 8`). **Hard-codes 8 notches** even for steam — the divide-by-8 in `RoundUpToNotch` is wrong for continuous-throttle locos but happens to round to 12.5% steps which is acceptable.
- **`CachedMuConnectedLocomotives`** (line 691) finds locos that are MU-enabled AND whose master is `this`. Power planning summed across these. So MU works correctly with AE (multiplier on max TE).
- **`FixMuCutOutIfNeeded`** (line 843): forcibly clears MU on the AE-driven loco (not slaves). Logged at `_log.Information`. Also clears CutOut if it's the only loco. This runs every loop iteration of `MaintainSpeed`. **AE owns one loco; MU is incompatible with AE on that same loco.**

Patch surface: `AutoEngineer.MaintainSpeed`, `AutoEngineer.CalculateThrottleForTargetVelocityStarting`, `AutoEngineer.CachedMuConnectedLocomotives`. Note these are all host-only; AE doesn't run on clients.

(A dedicated `autoengineer.md` crib sheet is forthcoming.)

---

## What vanilla does NOT model (gap inventory)

These are intentional absences — not bugs, not unwired scaffolding, just not present:

| Concept | Why we know it's missing |
|---|---|
| **Sander / sand consumption / sand-improves-µ** | No "sand" identifier anywhere in source; no `Sander` type; `TrackCoefficientOfFriction` has no sand input parameter. |
| **Dynamic brake (electric retardation)** | `PropertyChange.Control` enum has no `DynamicBrake`. Diesel has no DB control or curve. Confirmed in [physics-vanilla-survey › power](../physics-vanilla-survey.md#power--traction--dynamic-brake). |
| **Generator / traction-motor split** | `PrimeMover` is a single TE producer. No `Generator`, no `TractionMotor`, no per-axle TE. |
| **Continuous wheel slip ratio** | `CarWheelState` is a 3-value enum (`Tracking`/`Slip`/`Lock`). The `Lock` value is dead code. Slip is a flag, not a ratio. |
| **Per-axle adhesion / weight transfer** | `AdhesiveWeight` is a single scalar per loco. No per-truck or per-axle. |
| **Throttle/reverser interlock at speed** | No method anywhere validates "reverser change requires zero speed." Cab control allows direction change at any velocity. |
| **Boiler pressure simulation (steam)** | `SteamEngine.pressure = maximumBoilerPressure` (fixed). No fire-tube heat balance, no time-to-build-pressure, no priming. |
| **Cylinder cocks affect TE** | `Control.CylinderCock` exists and is observed by `LocomotiveControlHelper` setter, but `SteamEngine` never reads its state. Cosmetic only (drives `cylCock` audio + visual via subscribers). |
| **Compressor consumes power** | `compressorRunning` is a host-written KVO bool driven by air pressure; no TE penalty. |
| **HEP (head-end power for passenger)** | No HEP load model. |
| **Transition / field weakening (diesel-electric)** | The "0..10 mph linear, then curve" in `PrimeMover.CalculateTractiveEffort` is a single curve — no transition steps. |
| **DPU as first-class concept** | "DPU" never appears in source. Mid-train and rear-of-train MU works mechanically (any non-cut-out loco can be a master) but there's no UI for designating "lead unit" or for fenced power. |
| **Independent reverser per truck** | One reverser per loco, applied to all virtual driving wheels. |
| **Coupler-force aware traction** | TE formula doesn't read in-train forces. Slack run-out is a collision event, not a continuous force. (cross-link: [Couplers › slack & integration](couplers.md#slack-state--integration).) |

---

## DPU experiment guidance (call-out)

User has been experimenting with DPU in the inspector. Key facts for that work:

1. **MU is the existing mechanism for distributed power.** `IsMuEnabled` toggles in CarInspector (line 196) — anywhere on the consist, not just adjacent.
2. **MU master selection is order-dependent, not user-selectable.** A DPU mod that wants explicit lead-unit designation must replace `BaseLocomotive.FindMuSourceLocomotive` (or add a new search predicate).
3. **MU mirrors throttle and a recomputed reverser.** The reverser is recomputed *per slave from the slave's own velocity*. So DPU forces inherently differ — the slave runs its own cutoff oracle. For steam-on-steam DPU this is correct; for diesel-on-diesel the reverser is just sign-flipped to match orientation.
4. **MU implies CutOut.** A DPU mod must respect this for air integration; if you uncouple the air interlock you must also rework `LocomotiveAirSystem._ShouldDeferToLocomotiveAir`.
5. **MU runs at 1Hz.** Throttle changes propagate with up to 1s latency. For DPU coordination at speed (e.g., simultaneous brake+notch-reduction during run-out), this is too slow. Patch `BaseLocomotive.PeriodicUpdateForMu` to run faster, or hook the master's `Throttle` KVO observer directly.
6. **Per-unit power split is not modeled.** Each MU'd unit produces `CalculateTractiveEffort(_wheelVelocity)` independently — the IntegrationSet sums them across cars. If you want fenced or shared power, the work goes in `BaseLocomotive.UpdateTractiveEffortWheelState` (cap each loco's `_tractiveEffort` based on consist-scoped allocation).

Suggested DPU patch surface for the user's experiment:

- Override `BaseLocomotive.PeriodicUpdateForMu` to run at 5Hz (or hook the master's KVO directly for instant propagation).
- Add per-loco "DPU group" state via custom KVO key (mod-namespaced; non-`_` prefix → Crew auth).
- Use `AutoEngineerPlanner` only on the lead unit; cap slave's `_tractiveEffort` post-hoc in `UpdateTractiveEffortWheelState` postfix.
- Beware: AutoEngineer auto-disables MU on its loco (line 845). For AE+DPU, either patch `FixMuCutOutIfNeeded` or use a new "AE-friendly DPU" KVO key separate from `mu`.

---

## Cross-references

### To Couplers ([couplers.md](couplers.md))
- Tender↔engine forced reconnect (steam): [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths) and `SteamLocomotive.RequiresConnectionToEnd` / `ForceConnectedToAtRear` above.
- Why slack-direction collisions don't propagate as continuous in-train forces (so traction can't react to them): [Couplers › slack & integration](couplers.md#slack-state--integration).
- The collision damage path runs even when MU'd cars touch under power: [Couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline).

### To Wear & Durability ([wear-durability.md](wear-durability.md))
- TE × `Config.tractiveEffortMultForCondition.Evaluate(Condition)`: damaged locos lose power. See [Wear › Config curves](wear-durability.md#modelconfig-curves-tuning-surface).
- High TE accelerates "service mileage" via `Config.serviceDistanceTractiveEffortMultiplier` (used in `BaseLocomotive.ServiceMetersFromActual` line 230, override of `Car.ServiceMetersFromActual`): [Wear › per-tick wear loop](wear-durability.md#per-tick-wear-loop).
- Derailed locos forced µ = 0.1: see derailment section. The TE pipeline in `UpdateTractiveEffortWheelState` line 522 short-circuits to slip-floor µ when `IsDerailed` is true.

### To physics survey ([physics-vanilla-survey.md](../physics-vanilla-survey.md))
- TE enters consist physics in `IntegrationSet.UpdateAcceleration`: [physics › power/traction](../physics-vanilla-survey.md#power--traction--dynamic-brake).
- Wheel slip enum: [physics › wheel slip](../physics-vanilla-survey.md#wheel-slip--adhesion).
- The `TrackCondition.Dry` hardcode and absence of weather/grade/curve adhesion coupling: same section.

### Forward-pointing (planned crib sheets, not yet written)
- **brakes.md**: `LocomotiveAirSystem`, `Reservoir`, `CarAirSystem`, the `LocomotiveBrake` `[-0.1 .. 1]` bail-off encoding (see `LocomotiveBrakeMapFromControl` in `BaseLocomotive.cs:606`).
- **consist-integration.md**: the `IntegrationSet` solver, `Element.SlackStretch`, force application, `EnumerateCoupled`, `NextCarConnected`.
- **autoengineer.md**: the AI engineer's full state machine. Traction calls into it via `AutoEngineerPlanner.WillMove()`, `ApplyMovement()`. AE's MU-disable + cutoff-management are described above as a teaser.
