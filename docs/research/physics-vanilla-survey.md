# Physics — Vanilla Reconnaissance

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/`)
**Purpose:** Map vanilla's physics surface so the `physics` mod knows where to hook, what to derive, and what to ignore. Future sessions designing physics contracts should start here instead of re-mining.

> **Vanilla evolves; our contracts shouldn't.** This survey is a snapshot. Some
> of what we derive (weather→adhesion coupling, derailment-on-overspeed) the
> game has scaffolding for and may wire up in a future patch. When that
> happens, our physics mod's *implementation* swaps its input source; the
> *contract* on the api boundary stays stable.

---

## Headline findings

1. **Vanilla physics is intentionally minimal.** A linearized 1D constraint solver in `IntegrationSet` produces positions/velocities. There are **no force vectors**, no real adhesion model, no curve-adhesion coupling, no grade-adhesion coupling, no derailment forces. Our mod isn't *enriching* vanilla — vanilla mostly isn't doing it at all.

2. **`TrainController.FixedUpdate()` is the single canonical hook.** All consists, one loop, fixed 50Hz timestep, sibling operations (air tick, topology reconcile, networking) co-located. One postfix patch covers everything.

3. **Vanilla has formulas it doesn't use.** `TrainMath.DerailmentForSpeedOnCurve()` is *defined* but never *called*. `MaximumSpeedMphForCurve()` only runs from the AutoEngineer planner, not from physics. The chassis is there; the wiring isn't. We can reuse `TrainMath` helpers for consistency, then build on top.

4. **Wheel slip is a 3-state enum** (`Tracking | Slip | Lock`), not a continuous value. `TrackCondition` is hardcoded to `Dry` everywhere we looked — no weather coupling, despite the formula taking a condition parameter.

5. **Speed limits are per-`TrackSegment.speedLimit`.** Live limit = `min(posted, curve-derived)` via `AutoEngineerPlanner.MaxSpeedForTrackMph()`. No grade, weight, weather, or temporary slow orders.

---

## Tick lifecycle

### Driver

```
TrainController.FixedUpdate()                          ← the hook
├─ _spatialHash.UpdateIfNeeded()
├─ UpdateSets()                                        ← topology reconcile
├─ foreach (Car c) c.air.FixedUpdateAir(dt)            ← air pass (before physics)
├─ foreach (IntegrationSet s in _integrationSets)
│  ├─ if (!s.ShouldSkipTick)
│  │  ├─ s.ValidateConsistency()
│  │  └─ s.Tick(dt)                                    ← consist physics
├─ _spatialHash.UpdateIfNeeded()
├─ UpdateSets()
├─ _integrationSets.RemoveEmpty()                      ← decouple/despawn cleanup
└─ SendCarPositions / SendAir                          ← networking (host)
```

- Fixed 50 Hz (Unity `FixedUpdate`, `Time.deltaTime` ≈ 0.02s).
- Air ticks **before** consist physics — air pressure feeds braking force.
- Topology reconcile runs twice (before + after) — handles coupling/decoupling events safely.

### Lifecycle events

- **Create:** `TrainController.CreateIntegrationSet(IReadOnlyCollection<Car>)` from:
  - `HandleCreateCarsAsTrain()` — new consist at game start / load
  - `HandleCoupleRequest()` — coupling merges two sets into one
- **Destroy:** `_integrationSets.RemoveEmpty()` after each tick when set has 0 cars (decouple, despawn).
- **Split:** `IntegrationSetManager.Split(...)` for decoupling.

### Hook recommendation

Patch `TrainController.FixedUpdate()` postfix. One patch, all consists, synchronized with vanilla physics. Do **not** patch `IntegrationSet.Tick` per-set.

For lifecycle events, implement `IIntegrationSetEventHandler` (or watch the manager's add/remove operations).

---

## Per-domain map

### Consist topology

| Type | File | Role |
|---|---|---|
| `Car` | `Model/Car.cs` | Single vehicle; back-ref to `set`; `FrontIsA`, `Orientation`, `EnumerateCoupled(LogicalEnd)` |
| `BaseLocomotive` | `Model/BaseLocomotive.cs` | Powered vehicle base |
| `IntegrationSet` | `Model.Physics/IntegrationSet.cs` | Consist container; owns ordered `Element` list; the unit physics operates on |
| `IntegrationSetManager` | `Model.Physics/IntegrationSetManager.cs` | Owns all consist sets globally; Add/Remove/Split/Union |
| `RemoteIntegrationSet` | (same file) | Client-side variant for replicated consists |

**Important:** the consist is the `IntegrationSet`, **not** "the lead car." `IntegrationSet._elements` is the ordered list; positions and velocities live on `Element`, not `Car` directly. `Car.velocity` is computed from `Element.position` deltas.

### Kinematics

- 1D linearized space along consist centerline.
- `Element.position`, `Element.oldPosition`, `Element.acceleration` (floats, meters / m/s² in consist space).
- Verlet integration: `pos += (pos - oldPos) + accel*dt²`.
- Per-car readout: `Car.velocity` (signed m/s, consist-space), `Car.VelocityMphAbs`.
- World-space recovery: `IntegrationSet.PositionCars(dt, isInitialPosition)` and `Car.MotionSnapshot(...)`.
- **No per-truck kinematics** — single position per car.

### Mass

- `Car.Weight` → `Definition.WeightEmpty + _loadWeight` (short tons).
- No per-axle distribution; no inertia/moment.
- `Car.UpdateLoadWeight()` (private) called when loads change.

### Coupler / slack / in-train forces

| Field | Meaning |
|---|---|
| `Element.SlackA`, `Element.SlackB` | Per-end slack tolerance (meters) |
| `Element.SlackStretch` | **Current slack state**: < 0 compressed, > 0 in tension |
| `Element.SlackStretchDidChangeDirection` | Triggers collision event when sign flips above threshold |

- Vanilla enforces minimum separation via 4-iteration constraint solver (`IntegrationSet.IntegrateConstraints`); does not produce force vectors.
- `Car.CouplerSlack(End)` returns fixed values (0.02m with end gear, 0.001m without).
- `IntegrationSetEventHandler.IntegrationSetCarsDidCollide(carA, carB, deltaVelocity, isIn)` fires on hard slack-direction changes.
- **No coupler force is ever computed.** Slack stretch + per-car mass + acceleration is the input we'd derive forces from.

### Air system

| Type | File | Role |
|---|---|---|
| `CarAirSystem` | `Model.Physics/CarAirSystem.cs` | Base; per-car brake line, brake reservoir, brake cylinder |
| `LocomotiveAirSystem` | `Model.Physics/LocomotiveAirSystem.cs` | Adds main reservoir, train brake pressure, locomotive brake |
| `Reservoir` | `Model.Physics/Reservoir.cs` | Air pressure tank |
| `AirConnection` | `Model.Physics/AirConnection.cs` | Pipe between reservoirs |
| `VentedValve` | `Model.Physics/VentedValve.cs` | Valve flow logic |

- Per-car: `BrakeLine`, `BrakeReservoir`, `BrakeCylinder` (each a `Reservoir`).
- Train brake line propagates across consist via anglecock connections (`Car.EndGearA/B.IsAirConnected`).
- Tick: `Car.FixedUpdateAir(float dt)` — virtual, easy to postfix per-car.
- Internal: `CarAirSystem.UpdateAir(dt)` runs **twice with dt/2** each (sub-stepping for stability).
- Brake force: `Car.CalculateBrakingForce(brakePercent, absVelocity)` — newtons. Inputs: brake cylinder pressure → brake percent.

### Power / traction / dynamic brake

- `BaseLocomotive.RatedTractiveEffort` (abstract) — per-class rating.
- `BaseLocomotive._tractiveEffort` (private) — current applied TE.
- `Car.NormalizedTractiveEffort` — 0..1 ratio.
- `BaseLocomotive.UpdateTractiveEffortWheelState()` runs per FixedUpdate; computes wheel slip + caps TE by adhesion.
- Traction enters consist physics implicitly in `IntegrationSet.UpdateAcceleration()`. **No standalone traction-force readout.**
- **Dynamic brake is a control input only** — vanilla does **not** model the actual retarding force from electrical/regen behavior. We do that ourselves.

### Wheel slip / adhesion

| Type | File | Role |
|---|---|---|
| `CarWheelState` | `Model/CarWheelState.cs` | **Enum**: `Tracking`, `Slip`, `Lock`. Not continuous. |
| `TrainMath.TrackCoefficientOfFriction()` | `Model.Physics/TrainMath.cs` | µ = f(TrackCondition, wheelVelocityKph) |

- Three hardcoded conditions: `Dry` (Coulomb + asymptote), `Wet` (slightly lower), `Slick` (flat 0.05).
- `TrackCondition` is **hardcoded `Dry`** in `BaseLocomotive` — no weather hookup currently.
- Adhesive weight: full weight (diesel) or `weightOnDrivers` (steam).
- Slip detection: `if |TE| > AdhesiveWeight * µ → Slip; else Tracking`.
- When slipping, wheel velocity accelerates independently of consist velocity.
- Final TE capped by adhesion limit before being applied.

**What vanilla does NOT model:**

| Gap | Vanilla | Our derivation |
|---|---|---|
| Continuous slip ratio | enum only | `slipRatio = (wheelV - consistV) / max(\|consistV\|, ε)` |
| Curve adhesion penalty | µ unchanged on curves | reduce µ by lateral-G / curvature factor |
| Grade adhesion coupling | weight unchanged on grade | shift adhesive weight by sin(grade) |
| Weather coupling | hardcoded `Dry` | subscribe to weather (when game adds), modulate µ |
| Wheel slip sound | not exposed | synthesize from slipRatio crossing threshold |
| Derailment from slip | none | overspeed-on-curve → derailment force |

### Track / grade / speed limits

| Type | File | Role |
|---|---|---|
| `Graph` | `Track/Graph.cs` | Global track network; segment lookup, location math |
| `TrackSegment` | `Track/TrackSegment.cs` | Has `int speedLimit` (0–45 mph; 0 = use track-class default) |
| `Location` | `Track/Location.cs` | (segment, distanceAlongSegment, endOrientation) |
| `TrainMath.MaximumSpeedMphForCurve()` | `Model.Physics/TrainMath.cs` | `143 / (degrees^0.57)` |
| `Graph.CurvatureAtLocation()` | `Track/Graph.cs` | Returns degrees (5-sample spline cache) |
| `Graph.GradeAtLocation()` | `Track/Graph.cs` | Returns % grade from euler X |
| `AutoEngineerPlanner.MaxSpeedForTrackMph()` | (planner) | `min(posted, curveDerived)` — the canonical "live limit" |

**Track class defaults** (when `segment.speedLimit == 0`):

| Class | Default |
|---|---|
| Mainline | 35 mph |
| Branch | 25 mph |
| Industrial | 15 mph |

**Curve-speed formula** (vanilla):
```
maxMph = 143 / (curvatureDegrees^0.57)
       then * 0.8 for safety margin (or - 3 mph, whichever lower)
       floor 5 mph
```

**Live limit consumers in vanilla:** only `AutoEngineerPlanner.MaxSpeedForTrackMph(location)`. Physics doesn't enforce.

**What vanilla does NOT factor into the live limit:**

- Grade (steep descent → reduce safe speed for brake margin)
- Weight (heavier consist = more conservative on grades and curves)
- Temporary slow orders (no signal-driven dynamic restrictions)
- Weather (no rain/ice → speed reduction)
- Superelevation / cant — **doesn't appear to be modeled at all**

### Control properties

- `CarControlProperties` (`Model/CarControlProperties.cs`) — wraps `KeyValueObject` for typed control access.
- Reads: `props[PropertyChange.Control.<Name>]`
- Writes: setter triggers `Car.SendPropertyChange(...)` for KVO sync.
- Subscriptions: `props.Observe(control, action, callInitial)` returns `IDisposable`.
- Controls: `CutOut`, `Mu`, `Throttle`, `Reverser`, `TrainBrake`, `DynamicBrake`, `IndependentBrake`, etc.

**KVO is the event substrate for control changes** — subscribe, don't patch setters.

---

## Hook summary

| Need | Hook |
|---|---|
| Per-tick consist physics readout | Postfix `TrainController.FixedUpdate()` |
| Per-car air state | Postfix `Car.FixedUpdateAir(dt)` (virtual) |
| Control property changes | `CarControlProperties.Observe(control, action)` |
| Consist creation/destruction | `IIntegrationSetEventHandler` or watch manager add/remove |
| Slack-direction collisions | `IntegrationSetEventHandler.IntegrationSetCarsDidCollide(...)` |
| Live speed limit (vanilla version) | Call `AutoEngineerPlanner.MaxSpeedForTrackMph(location)` |
| Grade at any location | `Graph.GradeAtLocation(location)` |
| Curvature at any location | `Graph.CurvatureAtLocation(location, segment)` |

The physics mod's patch surface is **two postfix patches** plus KVO subscriptions. Most of the work is computation, not patching.

---

## Open questions for future passes

- **Where is the weather system?** Search for `Weather`, `Rain`, `Snow`, `Condition` setters that touch `TrackCondition`. Future game patch will likely wire this up.
- **Signal/dispatcher integration.** Speed limits aren't signal-driven now, but signals may add temporary restrictions. Worth a focused pass when we get to `mods/dispatch`.
- **`equipmentMaximumTrackCurvature`** — referenced in `AutoEngineerPlanner.MaxSpeedForTrackMph` but not located. Per-loco config or global constant?
- **Does vanilla ever *enforce* speed limits** (overspeed = damage/derailment)? `DerailmentForSpeedOnCurve` exists but isn't called. Confirm.
- **`TrainMath.slipSpeed = 0.05` constant** — tuning value for wheel-slip acceleration. Confirm intent before deriving our own.
- **`ShouldSkipTick`** — vanilla skips ticks for at-rest consists. Does our derivation also skip, or do we always run for completeness?
- **Superelevation** — confirmed missing in this pass. Worth a search before assuming.

---

## Cross-cutting observations

- **Linearized 1D physics.** Trades realism for speed. We accept this — our derivation works in the same space.
- **KVO is the canonical event substrate.** Subscribe over patch wherever possible.
- **`IntegrationSet`, not `Car`, is the unit of physics.** Contract design should reflect this — `IConsistTopology` is IntegrationSet-shaped.
- **Vanilla pre-built `TrainMath` formulas it doesn't invoke.** Reuse for consistency where useful; we're "finishing what vanilla started" more than building parallel.
- **Per-car `FixedUpdateAir` is virtual** — cleaner observation than the consist tick (which we patch at the manager level).
