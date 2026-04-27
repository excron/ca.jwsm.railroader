# Air System — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Brakes](brakes.md), [Anglecock & Hose](anglecock-hose.md), [Couplers](couplers.md)

The air system is a per-car simulation of three local reservoirs (BrakeLine, BrakeReservoir, BrakeCylinder) plus a main reservoir on locomotives, linked by `AirConnection` and `VentedValve` flow primitives. There is **no train-wide solver**; brake-pipe propagation is a per-tick walk of the consist, computing pairwise pressure transfers between adjacent cars' BrakeLines. The walk runs **twice per tick** (one full traversal each direction, randomized order) for symmetry. Air pressure is host-authoritative: clients receive `BatchCarAirUpdate` messages (every ≥1s, in batches of 10 cars) carrying byte-quantized BrakeLine/BrakeReservoir/BrakeCylinder values.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `CarAirSystem` | `Model.Physics/CarAirSystem.cs:7` | Per-car air sim. Three reservoirs + connections. MonoBehaviour |
| `LocomotiveAirSystem` | `Model.Physics/LocomotiveAirSystem.cs:6` | Adds `MainReservoir`, compressor, train-brake feed valve, loco brake injection |
| `Reservoir` | `Model.Physics/Reservoir.cs` | `Pressure`, `Volume`, `Equalize(a, b)` |
| `AirConnection` | `Model.Physics/AirConnection.cs` | Pipe with quality `_q` (Line/Feed/HalfInch); does flow + Velocity tracking |
| `VentedValve` | `Model.Physics/VentedValve.cs` | Like `AirConnection` but adds vent-to-atmosphere phase + state machine |
| `CarAirSystem.FixedUpdateAir(dt)` | `Model.Physics/CarAirSystem.cs:79` | Per-tick entrypoint, called from `TrainController.FixedUpdate` |
| `BatchCarAirUpdate` | `Game.Messages/BatchCarAirUpdate.cs` | Wire format. HostOnly. 1 byte per pressure (255 ≡ 120 PSI) |
| `TrainController.SendAirIfNeeded` | `TrainController.cs:1727` | Per-set 1s+ throttle, batched by 10 |

---

## Reservoirs (the state)

### Per-car reservoirs

```csharp
// CarAirSystem.cs:9-13
public readonly Reservoir BrakeLine        = new Reservoir("Brake Line",       0.6818f, 0f);
public readonly Reservoir BrakeReservoir   = new Reservoir("Brake Reservoir",  2.5f,    0f);
public readonly Reservoir BrakeCylinder    = new Reservoir("Brake Cylinder",   1f,      0f);
```

Volumes are unitless ratios for `Equalize`'s pressure-transfer math. The relative numbers matter: BrakeReservoir is 3.67× the size of BrakeLine, BrakeCylinder is 1.47× BrakeLine. Initial `Pressure = 0` until `PlaceTrain` (or `BatchCarAirUpdate` from host) sets them.

### Locomotive-only

```csharp
// LocomotiveAirSystem.cs:8
public readonly Reservoir MainReservoir = new Reservoir("Main Reservoir", 43f, 140f);
```

43-unit volume (much larger than car reservoirs). Initial 140 PSI. Compressor maintains 128–140 PSI.

### Reservoir.Equalize (the math)

```csharp
// Reservoir.cs:25-33
public static void Equalize(Reservoir a, Reservoir b) {
    float pressure = b.Pressure;
    float num = a.Pressure - pressure;            // ΔP
    float num2 = a.Volume / b.Volume;             // volume ratio
    float num3 = num / (num2 + 1f);               // pressure step
    a.Pressure -= num3;
    b.Pressure += num3;
}
```

Pressure-volume conservation. `b.Pressure += ΔP/(volRatio+1)`. **Instantaneous full equalization** — only used for tender-to-loco MR linkup (`CarAirSystem.cs:98`).

### `Reservoir.Pipe` enum & `_q` flow coefficients

```csharp
// AirConnection.cs:17-26
public AirConnection(Reservoir.Pipe pipe) {
    _q = pipe switch {
        Reservoir.Pipe.Line     => 1f,        // brake-pipe / main-line air
        Reservoir.Pipe.Feed     => 0.4f,      // smaller feed lines (brake-line→reservoir, cyl→outside)
        Reservoir.Pipe.HalfInch => 0.3f,      // smallest (reservoir→cylinder, MR→cylinder via valve)
        _ => throw...,
    };
}
```

Three pipe sizes. Modders cannot add new sizes without subclassing.

---

## `AirConnection.Equalize` — the rate-limited flow

```csharp
// AirConnection.cs:33-77
public float Equalize(Reservoir a, Reservoir b, float valve, float dt, float? bPressureOverride = null) {
    if (b == null) b = outside;                       // null target = atmosphere (P=0, huge volume)
    float num  = bPressureOverride ?? b.Pressure;
    float num2 = a.Pressure - num;                    // ΔP
    float num3 = a.Volume / b.Volume;
    float num4 = num2 / (num3 + 1f);                  // ideal full-equalization step
    float num5 = Sign(num2) * InverseLerp(0, 1, |num2|*valve) * _q * 300f;   // velocity target (ΔP-and-valve-dependent)
    if ((num5 > 0 && num5 > Velocity) || num5 < Velocity)
        Velocity = Lerp(Velocity, num5, dt * 10f);    // ramp toward target
    else
        Velocity = num5;
    float num6 = SameSign(num4, Velocity) ? Sign(num4) * Min(|num4|, |Velocity * dt|) : 0;
    a.Pressure -= num6;
    b.Pressure += num6 * num3;
    if (a.Pressure < 0) a.Pressure = 0;
    if (b.Pressure < 0) b.Pressure = 0;
    outside.Pressure = 0;                              // pin atmosphere
    return num6 / dt;                                  // returned flow rate
}

public float Valve(Reservoir source, Reservoir destination, float valve, float dt) {
    valve *= source.Pressure > destination.Pressure ? 1 : 0;  // diode
    return Equalize(source, destination, valve, dt);
}
```

Key facts:
- `outside` is a static shared `Reservoir("Outside", 1000000f, 0f)` (`AirConnection.cs:11`). Atmosphere has effectively infinite volume.
- `Velocity` is a per-connection state field — flow rate ramps with `Lerp(_, target, dt*10)`. **Asymmetric**: it ramps up (toward higher magnitude) but snaps down. Means quick valve-close = instant flow stop; valve-open = gradual ramp.
- `_q * 300f` is the max flow-rate ceiling per pipe. Line: 300 units/s; Feed: 120; HalfInch: 90.
- `valve` is 0..1 (or higher; not clamped). Multiplied by `|ΔP|` and clamped to [0..1]. So tiny ΔPs get tiny flows.
- Negative pressures are clamped at zero — atmosphere is treated as a leak sink.
- The `bPressureOverride` parameter is only used by `VentedValve` to target a setpoint (e.g. brake-line target = lap PSI).
- `Valve(source, dest)` is `Equalize` with a one-way diode (no backflow). Used for the brake-line → reservoir refill path.

---

## `VentedValve` — three-state brake control

```csharp
// VentedValve.cs:5-41
public class VentedValve : AirConnection {
    private float _valveState;                              // 0=apply, 1=lap, 2=release-with-vent
    private readonly AirConnection _vent;                   // separate vent-to-atmosphere channel

    public VentedValve(Reservoir.Pipe pipe) : base(pipe) {
        _vent = new AirConnection(pipe);
    }

    public float ValveAutomaticBrake(Reservoir mainReservoir, Reservoir brakeLine, float psi, bool released, float dt)
        => ValveVent(mainReservoir, brakeLine, psi, released, dt);

    public float ValveVent(Reservoir input, Reservoir output, float psi, bool canValve, float dt) {
        float num    = psi + 0.5f;                          // upper deadband
        float num2   = Mathf.Max(0f, psi - 0.5f);           // lower deadband
        bool num3    = output.Pressure > num;               // need to vent down
        bool flag    = canValve && output.Pressure < num2 && input.Pressure >= num2;
                                                            // need to feed up
        int num4     = !num3 ? (!flag ? 1 : 2) : 0;         // 0=vent, 1=lap, 2=feed
        _valveState  = num4;
        float num5   = Mathf.InverseLerp(1f, 0f, _valveState);   // 0..1, 1 when state=0 (vent)
        float valve  = Mathf.InverseLerp(1f, 2f, _valveState);   // 0..1, 1 when state=2 (feed)
        Equalize(output, input, valve, dt, psi);             // feed from input toward psi
        float result = _vent.Equalize(output, null, num5, dt, psi); // vent toward psi
        if (num5 > 0f && output.Pressure < psi) {
            output.Pressure = psi;                           // floor while venting
            _valveState = 1f;
        }
        return result;
    }
}
```

Three-state automatic-brake feed valve (i.e., "26L self-lapping" approximation):
- **Vent** (state=0): output > psi+0.5 → drain to atmosphere via `_vent`.
- **Lap** (state=1): within deadband → no action.
- **Feed** (state=2): output < psi-0.5 AND `canValve` AND input has air → fill from `input`.

The `bPressureOverride = psi` parameter on the inner `Equalize` calls means flow is calculated against the *target setpoint* — so when `output` reaches `psi`, flow stops naturally even though state may still be Feed.

**`canValve = released` parameter** (passed from `LocomotiveAirSystem.UpdateAir`): only allow Feed when the train brake is "released" (within 1 PSI of brakeFeedValvePressure). This implements the **release-only feed valve** behaviour — the BP can only re-pressurize when the handle is moved fully back. While in any reduction position, the valve can vent the BP down further but can't pump it up.

```csharp
// LocomotiveAirSystem.cs:104-110
bool flag = Mathf.Abs(lapTrainBrakePressure - brakeFeedValvePressure) < 1f;
brakeFeedValveFlow = _mainReservoirToBrakeLine.ValveAutomaticBrake(MainReservoir, BrakeLine, lapTrainBrakePressure, flag, dt);
```

When `flag` is true, the feed valve can pump from MR to BL. When false, it can only vent BL. This is the canonical "lap and release" valve schedule.

### `_vent` separate channel

The vent flow uses its own `_vent` `AirConnection`, so its `Velocity` tracker is independent of the feed direction. Important: when the valve flips from feed to vent, the feed connection's velocity continues to lerp down while the vent's velocity ramps up — neither "snaps." The `if (num5 > 0 && output.Pressure < psi) output.Pressure = psi` clamp prevents over-venting below the setpoint.

---

## CarAirSystem layout & connections

```csharp
// CarAirSystem.cs:9-27
public readonly Reservoir BrakeLine        = new Reservoir("Brake Line",       0.6818f, 0f);
public readonly Reservoir BrakeReservoir   = new Reservoir("Brake Reservoir",  2.5f,    0f);
public readonly Reservoir BrakeCylinder    = new Reservoir("Brake Cylinder",   1f,      0f);

public readonly AirConnection BrakeLineToReservoir   = new AirConnection(Reservoir.Pipe.Feed);
public readonly AirConnection ReservoirToCylinder    = new AirConnection(Reservoir.Pipe.HalfInch);
private readonly AirConnection CylinderToOutside     = new AirConnection(Reservoir.Pipe.Feed);
private readonly AirConnection BrakeLineConnectionA  = new AirConnection(Reservoir.Pipe.Line);
private readonly AirConnection BrakeLineConnectionB  = new AirConnection(Reservoir.Pipe.Line);
private readonly VentedValve TenderMainResToBrakeCylinder = new VentedValve(Reservoir.Pipe.HalfInch);
private readonly VentedValve TenderMainResToMainRes      = new VentedValve(Reservoir.Pipe.Line);
```

Topology:
```
              ┌──────────────┐
              │     other    │
              │   car's BL   │ ← anglecock × anglecock × BrakeLineConnectionA/B
              └──────┬───────┘                         (between adjacent cars)
                     │
              ┌──────┴───────┐
              │   BrakeLine  │
              └──────┬───────┘
                     │ BrakeLineToReservoir (Feed pipe, valved on release)
                     ▼
              ┌──────────────┐
              │ BrakeReservoir│
              └──────┬───────┘
                     │ ReservoirToCylinder (HalfInch pipe, equalized on apply)
                     ▼
              ┌──────────────┐
              │ BrakeCylinder │ ──── CylinderToOutside (Feed) ───▶ atmosphere
              └──────────────┘
```

The "TenderMainRes…" valves are dead-ish on regular cars; they're used in the tender deferral path (see below).

### Public state fields

```csharp
[NonSerialized] public float brakePercent;       // 0..3, target braking force, lerps each tick
[NonSerialized] public bool  handbrakeApplied;   // mirror of "handbrake" KVO key
[NonSerialized] public float exhaustFlow;        // returned audio loudness for brake exhaust
[NonSerialized] public float anglecockFlowA;     // per-end
[NonSerialized] public float anglecockFlowB;
public Car car;
public bool NeedsSend;                            // dirty flag for batched broadcast
public long LastSentTick;
public bool bleedBrakeCylinder { get; private set; }
public const float AnglecockClosedThreshold = 0.01f;
```

### `FixedUpdateAir` — the per-tick entry

```csharp
// CarAirSystem.cs:79-90
public void FixedUpdateAir(float dt) {
    UpdateBrakeLine(dt);           // CONSIST-WIDE (only triggered from the leftmost car)
    airFlow = 0f;
    exhaustFlow = 0f;
    for (int i = 0; i < 2; i++)
        UpdateAir(dt / 2f);        // local valves, twice per tick (sub-steps)
    UpdateBrakingForce();           // brakePercent target + lerp
    UpdateNeedsSend();              // set NeedsSend flag if any flow > 0.1
}
```

Called from `TrainController.FixedUpdate` (`TrainController.cs:419-454`) for *every* car each tick. The `UpdateBrakeLine(dt)` call early-exits unless the calling car has `EndGearA.IsAirConnected == false` — so it runs once per consist-segment, traversing the segment from its A end. **Order within the for-loop matters**: cars are iterated in `_cars` list order, not consist order. Whichever air-disconnected-A-end car gets there first triggers the consist sweep; subsequent cars in that segment then no-op `UpdateBrakeLine`.

### `UpdateAir` — local valve cycle (regular cars)

```csharp
// CarAirSystem.cs:92-120 (with locomotive override at 82-111)
protected virtual void UpdateAir(float dt) {
    if (ShouldDeferToLocomotiveAir(out var locomotiveAirSystem)) {
        // Tender deferral path — equalize MR with adjacent loco's MR
        if (this is LocomotiveAirSystem self)
            Reservoir.Equalize(locomotiveAirSystem.MainReservoir, self.MainReservoir);
        exhaustFlow += TenderMainResToBrakeCylinder.ValveVent(
            locomotiveAirSystem.MainReservoir, BrakeCylinder,
            locomotiveAirSystem.locomotiveBrakeControlLine, canValve: true, dt);
        return;
    }
    float pressure  = BrakeReservoir.Pressure;
    float pressure2 = BrakeLine.Pressure;
    int num  = (pressure2 < pressure - 0.5f) ? 1 : 0;     // BL much lower than BR → apply
    int num2 = (pressure2 > pressure + 0.5f) ? 1 : 0;     // BL higher than BR → release/refill
    if (bleedBrakeCylinder) {
        bool flag = BrakeCylinder.Pressure > 0.1f;
        bleedBrakeCylinder = flag;
        if (bleedBrakeCylinder) { num = 1; num2 = 1; }    // both paths active during bleed
    }
    airFlow      += ReservoirToCylinder.Equalize(BrakeReservoir, BrakeCylinder, num, dt);   // apply
    exhaustFlow  += CylinderToOutside.Equalize(BrakeCylinder, null, num2, dt);              // release
    airFlow      += BrakeLineToReservoir.Valve(BrakeLine, BrakeReservoir, num2, dt);        // recharge
}
```

This is the **triple-valve approximation**:
- BL drops (BL < BR - 0.5 PSI) → `num=1` → BR pumps into BC. **Apply phase.**
- BL rises (BL > BR + 0.5 PSI) → `num2=1` → BC vents to atmosphere AND BL refills BR. **Release phase.**
- Within deadband (|BL - BR| < 0.5) → both paths off. **Lap.**

Notice `num` and `num2` are mutually exclusive in normal operation — only one can be true at a time (assuming `bleedBrakeCylinder=false`). With `bleedBrakeCylinder=true`, both are forced on, draining BC and BR.

The 0.5 PSI deadband is the "**triple-valve sensitivity threshold**." It's not configurable; it's a literal in `CarAirSystem.cs:105-106`.

### `UpdateAir` for `LocomotiveAirSystem` (override)

```csharp
// LocomotiveAirSystem.cs:82-111
protected override void UpdateAir(float dt) {
    UpdateCompressor(dt);                                // MR refill
    if (IsCutOut) {
        locomotiveBrakeSetting = 0f;
        trainBrakeSetting = 0f;
        base.UpdateAir(dt);                              // fall through to standard car triple-valve
        return;
    }
    UpdateLocomotiveBrakeControlLine();                  // see brakes.md
    exhaustFlow += _mainReservoirToBrakeCylinder.ValveVent(
        MainReservoir, BrakeCylinder, locomotiveBrakeControlLine, canValve: true, dt);
                                                          // INDEPENDENT BRAKE: MR → BC via vented valve
    UpdateBrakingForce();                                 // duplicate of base method (called twice on locos)

    // Lap pressure tracking
    if (Mathf.Abs(trainBrakePressure - brakeFeedValvePressure) < 1f)
        _lapTrainBrakePressure = brakeFeedValvePressure;
    else
        _lapTrainBrakePressure = Mathf.Min(trainBrakePressure, _lapTrainBrakePressure);

    float lapTrainBrakePressure = _lapTrainBrakePressure;
    bool flag = Mathf.Abs(lapTrainBrakePressure - brakeFeedValvePressure) < 1f;
    brakeFeedValveFlow = _mainReservoirToBrakeLine.ValveAutomaticBrake(
        MainReservoir, BrakeLine, lapTrainBrakePressure, flag, dt);  // BP feed valve

    if (flag) {
        _locomotiveBrakeLineMemory = brakeFeedValvePressure;
        _locomotiveBrakeLineBank = 0f;
    }
}
```

So a locomotive does:
1. Compressor tick (MR ← air).
2. Either (cut-out) standard triple-valve, or (active) loco-brake injection + train-brake feed valve + lap tracking.
3. Note: when cut-out, the loco's own BC is still driven by the *triple valve* sensing the BP, just like a regular car. **Cut-out lets the loco's wheels brake from the train brake without the loco being a brake-pipe air source.**

`UpdateBrakingForce` is called both in the loco override (`LocomotiveAirSystem.cs:94`) and in the base `FixedUpdateAir` (`CarAirSystem.cs:88`). On a loco, `brakePercent` is updated **twice per tick** because of this; minor but worth noting if you patch.

### `UpdateCompressor`

```csharp
// LocomotiveAirSystem.cs:113-127
private void UpdateCompressor(float dt) {
    if (MainReservoir.Pressure < compressorLimitLower) compressorRunning = HasFuel;
    if (MainReservoir.Pressure > compressorLimitUpper) compressorRunning = false;
    if (compressorRunning)
        MainReservoir.Pressure += compressorRate * dt;        // 0.5 PSI/s linear add
}
```

Compressor is a binary on/off with hysteresis between `compressorLimitLower=128` and `compressorLimitUpper=140`. **`HasFuel` gates startup but not running.** Once running, it keeps adding even if fuel runs out mid-cycle (until upper limit). This is a minor pitfall: a steam loco that drops to 0 water mid-pump won't stop pumping until next dip below 128. Setter for `HasFuel` is in `BaseLocomotive`/derived classes (`SteamLocomotive.cs:263`, `DieselLocomotive.cs:153`).

`compressorRate = 0.5f` (PSI/s). With `MainReservoir.Volume = 43`, the compressor at 0.5 PSI/s per second is **independent of MR volume** — it's a flat addition, not a volumetric flow. Consequence: a tender's MR (which gets equalized with the loco's via `Reservoir.Equalize`, see deferral path) doubles effective volume but the compressor still pumps at the same nominal rate.

### `ShouldDeferToLocomotiveAir` (tender-deferral)

```csharp
// CarAirSystem.cs:122-151
protected virtual bool ShouldDeferToLocomotiveAir(out LocomotiveAirSystem locomotiveAirSystem) {
    locomotiveAirSystem = null;
    if (car.set == null) return false;
    if (car.Archetype != CarArchetype.Tender) return false;
    if (!car.TryGetAdjacentCar(car.EndToLogical(Car.End.F), out var adjacent) || !adjacent.IsLocomotive)
        return false;
    if (!(adjacent.air is LocomotiveAirSystem locomotiveAirSystem2)) return false;
    locomotiveAirSystem = locomotiveAirSystem2;
    if (!locomotiveAirSystem.IsCutOut) return true;
    if (!locomotiveAirSystem.IsMuEnabled) return false;
    return locomotiveAirSystem.ShouldDeferToLocomotiveAir(out locomotiveAirSystem);
}
```

A tender adjacent to a non-cut-out loco defers. If the loco is cut-out + MU, the recursion finds the *next* loco upstream. **Up to one level of indirection** (the recursion is on `LocomotiveAirSystem`'s override, see below).

Locomotive override:
```csharp
// LocomotiveAirSystem.cs:129-168
protected override bool ShouldDeferToLocomotiveAir(out LocomotiveAirSystem locomotiveAirSystem) {
    locomotiveAirSystem = _cachedShouldDeferToLocomotiveAir.locoAir;
    return _cachedShouldDeferToLocomotiveAir.should;
}
```

Returns the cached result. The cache is updated by `UpdateCachedShouldDeferToLocomotiveAir` which is called from `BaseLocomotive.PeriodicUpdateForMu` once per second (`BaseLocomotive.cs:144-150`). So **MU/cutout state changes take up to 1s to take effect** in the air sim.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarAirSystem.UpdateAir` | The triple-valve thresholds (0.5 PSI deadband, num/num2 logic) live here. Replace for custom valve schedules per car. |
| `CarAirSystem.FixedUpdateAir` | Wrap to add per-tick logging or veto. |
| `CarAirSystem.UpdateBrakingForce` / `CalculateTargetBrakePercent` | Change the cylinder→brakePercent mapping (currently linear, divisor 64). |
| `LocomotiveAirSystem.UpdateAir` | Compressor + loco brake + train brake. The whole engineer's air. |
| `LocomotiveAirSystem.UpdateCompressor` | Compressor schedule. |
| `LocomotiveAirSystem.UpdateCachedShouldDeferToLocomotiveAir` | Tender-defer cache; re-call from your mod to refresh on demand. |
| `AirConnection.Equalize` | The single flow primitive. Patching changes everything about pressure transfer. |
| `VentedValve.ValveVent` | Three-state vented-valve. Replace for different feed-valve characteristics. |

---

## Brake-pipe propagation (the key loop)

This is the only consist-wide air operation. Triggered once per car per tick from `FixedUpdateAir`, but only *executes* for cars whose `EndGearA.IsAirConnected == false` (i.e., the A-end of an air-segment).

```csharp
// CarAirSystem.cs:161-192
private void UpdateBrakeLine(float dt) {
    if (this.car.EndGearA.IsAirConnected || this.car.set == null) return;
    IntegrationSet set = this.car.set;
    ResetFlowValues();                                                  // zero anglecockFlowA/B for whole consist
    int num = UnityEngine.Random.Range(0, 2);                          // randomize traversal order
    for (int i = 0; i < 2; i++) {
        Car.LogicalEnd logicalEnd = (((i + num) % 2 != 0) ? Car.LogicalEnd.B : Car.LogicalEnd.A);
        int carIndex = set.StartIndexForConnected(this.car, logicalEnd, IntegrationSet.EnumerationCondition.AirConnected);
        bool stop = false;
        while (!stop) {
            Car car = set.NextCarConnected(ref carIndex, logicalEnd, IntegrationSet.EnumerationCondition.AirConnected, out stop);
            if (car == null) break;
            if (logicalEnd == Car.LogicalEnd.A)
                car.air.UpdateBrakeLineIndividualB(dt / 2f);
            else
                car.air.UpdateBrakeLineIndividualA(dt / 2f);
        }
    }
}
```

Two passes: one starting from "leftmost" walking right (calling `UpdateBrakeLineIndividualB` on each), one walking left (`UpdateBrakeLineIndividualA`). **Random initial direction** prevents systematic asymmetry. Each pass runs at `dt/2` (so total step = dt across both passes).

```csharp
// CarAirSystem.cs:215-249
private void UpdateBrakeLineIndividualB(float dt) {
    CarAirSystem otherAir = car.set.GetAirConnection(car, Car.LogicalEnd.B)?.air;
    float num = ValveValueForAnglecock(anglecockB);                 // closed if < 0.01
    AirConnection brakeLineConnectionB = BrakeLineConnectionB;
    if (otherAir == null) {
        // dangling end — vent to atmosphere through anglecock
        anglecockFlowB += brakeLineConnectionB.Equalize(BrakeLine, null, num, dt);
        return;
    }
    float valve = Mathf.Min(otherAir.anglecockA, num);              // BOTH anglecocks must be open
    float num2  = brakeLineConnectionB.Equalize(BrakeLine, otherAir.BrakeLine, valve, dt);
    otherAir.anglecockFlowA += num2;                                // mirror on other car
    anglecockFlowB           += num2;
}
```

**The anglecock setting (0..1) is multiplied into the valve open-amount.** With one car at 0.5 anglecock and the other at 1.0, flow is gated to 0.5 (the min). A single closed anglecock (< 0.01) zeros the valve completely. Dangling ends (no neighbour or `IsAirConnected=false`) vent to atmosphere through the open anglecock.

`UpdateBrakeLineIndividualA` is symmetric. Note that **both endpoints' `anglecockFlow` get updated** by the same flow value, ensuring the audio/visual flow indicator is mirrored on both sides.

### Walk pattern

`StartIndexForConnected` (`IntegrationSet.cs:836-872`) finds the first car that has its specified end *not* air-connected (i.e., the start of the segment). `NextCarConnected` walks while air-connected, returning each car and setting `stop=true` when it hits an unconnected end. So the loop:

1. Random direction picked (A→B or B→A).
2. Walk from segment start; for each car, equalize its specified-end BL with the next car's opposite-end BL.
3. Reverse direction; walk again.

This means **on a 50-car train, the brake pipe propagates by 50 pairwise equalizations each tick, two passes**. With Unity's default `FixedUpdate` at 50 Hz, that's 100 pairwise transfers per second per pair. Brake-pipe propagation speed depends on `_q * 300f` (Line = 300 units/s flow ceiling) and the per-step `dt/2 = 0.01s`. The `/airtest` console command (`UI.Console.Commands/AirTestCommand.cs`) measures this empirically.

### `ResetFlowValues` — segment-wide flow reset

```csharp
// CarAirSystem.cs:194-204
private void ResetFlowValues() {
    bool stop = false;
    int carIndex = this.car.set.StartIndexForConnected(this.car, Car.LogicalEnd.A, IntegrationSet.EnumerationCondition.Coupled);
    Car car;
    while (!stop && (car = this.car.set.NextCarConnected(ref carIndex, Car.LogicalEnd.A, IntegrationSet.EnumerationCondition.Coupled, out stop)) != null) {
        car.air.anglecockFlowA = 0f;
        car.air.anglecockFlowB = 0f;
    }
}
```

Note this walks `EnumerationCondition.Coupled` (not AirConnected), so all coupled cars in the consist get their flow values reset, not just the air-segment. The `anglecockFlow` accumulators are then summed over the two-pass propagation.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarAirSystem.UpdateBrakeLine` | The two-pass walk. Patching here enables, e.g., 4-pass for higher-resolution propagation, or per-car logging. |
| `CarAirSystem.UpdateBrakeLineIndividualA/B` | Per-pair flow. Replace to add custom physics (e.g., volume-aware propagation). |
| `CarAirSystem.ValveValueForAnglecock` (private static) | The 0.01 anglecock-closed threshold. |
| `IntegrationSet.StartIndexForConnected` / `NextCarConnected` | The walk primitives. |

---

## Network sync — `BatchCarAirUpdate`

```csharp
// Game.Messages/BatchCarAirUpdate.cs
[HostOnlyAuthorizationRule]
[MessagePackObject(false)]
public struct BatchCarAirUpdate {
    public readonly long Tick;
    public readonly string[] CarIds;
    public readonly byte[] BrakeLineValues;
    public readonly byte[] BrakeReservoirValues;
    public readonly byte[] BrakeCylinderValues;
    private const float MaxValue = 120f;
    public static byte ValueToByte(float v)  => (byte)Mathf.RoundToInt(Mathf.Clamp01(v / 120f) * 255f);
    public static float ByteToValue(byte v)  => 120f * (v / 255f);
}
```

**1 byte per pressure** (255 ≡ 120 PSI; resolution ~0.47 PSI). Three reservoirs per car. Anything above 120 PSI clips to 255. The `MainReservoir` is **not** in this batch — clients never see MR pressure directly; they reconstruct it from the host's display KVO updates (gauge values written via `BaseLocomotive.UpdateCabControls`).

### Send schedule

```csharp
// TrainController.cs:1727-1766
private void SendAirIfNeeded() {
    long now = StateManager.Now;
    long num = now - 1000;                                     // 1-second min interval
    foreach (IntegrationSet integrationSet in _integrationSets) {
        bool flag = false;
        foreach (Car car2 in integrationSet.Cars) {
            bool flag2 = car2.air.NeedsSend && car2.air.LastSentTick < num;
            flag = flag || flag2;
        }
        if (!flag) continue;
        List<Car> list = integrationSet.Cars.ToList();
        for (int i = 0; i < list.Count; i += 10) {              // batches of 10
            int num2 = Mathf.Min(10, list.Count - i);
            BatchCarAirUpdate batch = new BatchCarAirUpdate(now, ...);
            // populate ...
            client.Send(batch);
        }
        foreach (Car item in list) {
            item.air.NeedsSend = false;
            item.air.LastSentTick = now;
        }
    }
}
```

- **Per-set throttle:** if any car in the set has `NeedsSend && LastSentTick + 1000 < now`, the **entire set** broadcasts.
- **Batches of 10:** large consists fragment into multiple messages.
- **`NeedsSend` set by `UpdateNeedsSend`** (`CarAirSystem.cs:153-159`): true if `exhaustFlow > 0.1 || airFlow > 0.1 || anglecockFlow{A,B} > 0.1`. Static air with no flow → no broadcast.

### Receive

```csharp
// TrainController.cs:1773-1788
public void HandleBatchCarAirUpdate(BatchCarAirUpdate update) {
    for (int i = 0; i < update.CarIds.Length; i++) {
        ... car.air.SetAir(brakeLinePressure, brakeRes, brakeCyl);
    }
}

// CarAirSystem.cs:270-275
public void SetAir(float brakeLinePressure, float brakeRes, float brakeCyl) {
    BrakeLine.Pressure      = brakeLinePressure;
    BrakeReservoir.Pressure = brakeRes;
    BrakeCylinder.Pressure  = brakeCyl;
}
```

**Direct field assignment.** No interpolation, no smoothing. Clients see the host's per-tick state with up to 1s latency, then their own `FixedUpdateAir` continues running locally between updates. So clients run their own sim against snapshotted reservoir pressures. **Local-side air sim diverges between updates** — typically negligible because flows are small relative to the 1s update window, but visible during fast transients (e.g., emergency braking, hose break).

**`BatchCarAirUpdate` is HostOnly.** Clients cannot inject air state.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainController.SendAirIfNeeded` | Change throttle interval, batch size, per-set vs per-car granularity. |
| `BatchCarAirUpdate.ValueToByte` / `ByteToValue` | Increase pressure resolution (e.g., 16-bit). Wire-incompatible with stock clients. |
| `CarAirSystem.UpdateNeedsSend` | Change "is this car interesting" gate. Lower threshold → more bandwidth. |
| `CarAirSystem.SetAir` | Add interpolation client-side. |

---

## Initial pressures (PlaceTrain)

```csharp
// TrainController.cs:592-609 (the helper called from PlaceTrain handler)
foreach (Car car in cars) {
    if (car.air is LocomotiveAirSystem locomotiveAirSystem)
        locomotiveAirSystem.MainReservoir.Pressure = 140f;
    car.air.BrakeReservoir.Pressure = 90f;
    car.air.BrakeLine.Pressure      = 90f;
    car.air.BrakeCylinder.Pressure  = 0f;
}
```

So `PlaceTrain` always begins with: **MR=140, BL=90, BR=90, BC=0**. This is the "fully charged + released" state.

`PostRestoreProperties` (`CarAirSystem.cs:277-280`) recalculates `brakePercent = CalculateTargetBrakePercent()` from the loaded BC pressure — so saved games restore brake state correctly.

---

## Console / debug

### `/airtest` console command

`UI.Console.Commands/AirTestCommand.cs`. Sets `TrainBrakeSetting = 13/45` (≈29% reduction, ~26 PSI drop) on the selected loco, then waits until the *farthest* car's BL pressure changes by more than 1 PSI. Logs propagation time and ft/s. Usage: `/airtest`.

### `debugDrawBrakeDisplay`

`TrainController.cs:457-462`:

```csharp
private void OnGUI() {
    if (debugDrawBrakeDisplay && SelectedCar != null)
        CarAirSystem.GUIDrawDebugBrakeDisplay(SelectedCar);
}
```

Renders a per-car bar chart: green=BL, yellow=BR/MR, red=BC, cyan=brake-line connection velocity. Toggle `debugDrawBrakeDisplay` (public field on `TrainController`) to enable.

---

## MP authority summary

| Action | Auth | Wire |
|---|---|---|
| Train brake change | Crew + train-crew | `PropertyChange { key="trainBrake" }` |
| Loco brake change | Crew + train-crew | `PropertyChange { key="locoBrake" }` |
| Cut out toggle | Crew + train-crew | `PropertyChange { key="cutOut" }` |
| Hand brake apply | Crew + train-crew | `PropertyChange { key="handbrake" }` |
| Bleed | Crew + train-crew | `PropertyChange { key="bleed" }` (0.5s self-clear) |
| Anglecock open/close | Crew + train-crew | `PropertyChange { key="f.anglecock" / "r.anglecock" }` (float) |
| Gladhand connect/disconnect | Crew | `SetGladhandsConnected` |
| Air pressure broadcast | **HostOnly** | `BatchCarAirUpdate` |
| Compressor running indicator | Host writes (Crew-readable) | `PropertyChange { key="compressor" }` |

The asymmetry: **clients can request control changes; only the host can change pressures.** Combined with the 1s broadcast throttle, this means client-initiated brake commands have a measurable round-trip lag.

---

## Gotchas

- **`UpdateAir` runs twice per tick** (`for (int i = 0; i < 2; i++)` in `FixedUpdateAir`) — sub-stepping for stability. Patches that count per-tick events should account for this.
- **`UpdateBrakingForce` is called twice per tick on locomotives** (once in `LocomotiveAirSystem.UpdateAir` line 94, once in base `FixedUpdateAir` line 88). The base method's `Lerp` is `Time.deltaTime`-rate, so the loco's `brakePercent` ramps slightly faster than a regular car's. Likely unintentional in vanilla.
- **`AirConnection.Velocity` is asymmetric** — ramps up via `Lerp(_, target, dt*10)`, snaps down. So opening a valve has rise time but closing is instant.
- **`outside.Pressure = 0` is pinned every call** — the static `outside` reservoir is shared across all `AirConnection`s, so a multi-thread call would race. Vanilla is single-threaded so this is safe; if you parallelize, watch out.
- **`bleedBrakeCylinder` is read-only public** — call `BleedBrakeCylinder()` to set; it self-clears in `UpdateAir` once BC < 0.1.
- **Tender deferral does not use the BR/BC of the tender.** `ShouldDeferToLocomotiveAir == true` short-circuits past the triple-valve entirely; the tender's BC is filled directly from the loco's MR via the `TenderMainResToBrakeCylinder` vented valve. So a tender's BR can sit at any pressure with no effect. After deferral changes (e.g., engine cut-out), the tender's BR may be stale.
- **`MainReservoir.Pressure` can exceed `compressorLimitUpper=140`** — only the compressor stops at 140. The reservoir itself has no cap. Manual writes (e.g., `BatchCarAirUpdate`) are clamped to 120 PSI by the byte encoding, so persistent MR>140 happens host-side only via the equalization-with-tender path.
- **The `Reservoir.Equalize` between loco and tender MR (`CarAirSystem.cs:98`) runs only in the deferral path's branch where `this is LocomotiveAirSystem`** — a regular tender's `UpdateAir` defers but doesn't equalize. Only when an MU loco is itself deferring does its MR get linked to its target's MR. **Most tenders never have their MR equalized** — they just use the upstream loco's MR directly. The tender's `MainReservoir` field exists (because `LocomotiveAirSystem`-typed deferral checks it) but is rarely populated.

Wait — re-reading: tenders are not `LocomotiveAirSystem`s; they have base `CarAirSystem`. The `if (this is LocomotiveAirSystem)` check (`CarAirSystem.cs:96`) matches only when an MU loco's air-system defers to another loco's — i.e., the trailing MU unit shares MR with the lead. Tenders share MR with their loco implicitly via direct usage in the vented valve, no `Equalize` needed.

- **`AnglecockClosedThreshold = 0.01f`** is exposed as a public const but not used inside `CarAirSystem` — the actual threshold is hardcoded in `ValveValueForAnglecock` (`CarAirSystem.cs:206-213`) and in `Car.EndGear.IsAnglecockOpen` (uses `> 0.1f`). **The threshold differs between "open enough to flow air" (0.01) and "open enough to be considered logically open" (0.1).** A handle setting 0.05 would flow air but display as closed.

---

## Cross-references

- Brake-pipe state changes triggered by control inputs: [Brakes › train brake](brakes.md#train-brake-automatic--brake-pipe-driven), [Brakes › independent brake](brakes.md#locomotive-independent-brake--direct-cylinder-injection).
- Per-end anglecock state and KVO keys: [Anglecock & Hose › anglecock model](anglecock-hose.md#anglecock-model).
- `IsAirConnected` topology and hose-break events: [Anglecock & Hose › air connection topology](anglecock-hose.md#air-connection-topology), [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths).
- The `IntegrationSet` consist enumeration helpers used by `UpdateBrakeLine`: [Couplers › slack & integration](couplers.md#slack-state--integration).
- `Condition`-modulated brake force (downstream consumer of `brakePercent`): [Wear & Durability](wear-durability.md).
