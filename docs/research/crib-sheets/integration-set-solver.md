# IntegrationSet Solver Internals — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Consist & integration set](consist-integration.md), [Couplers](couplers.md), [Wear & Durability](wear-durability.md)

The internals of `IntegrationSet.Tick` — the Verlet integrator, the 4-iteration constraint solver, brake integration in two phases, slack accounting, position propagation, sort algorithm, bound enforcement, consistency validation, and the per-tick collision-event emission. This is the **canonical physics tick** for trains in Railroader. There is one `Tick(dt)` per consist per Unity `FixedUpdate` (50 Hz, `dt ≈ 0.02s`).

For container/lifecycle/replication, see [consist-integration.md](consist-integration.md). For coupler state semantics, see [couplers.md](couplers.md).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `IntegrationSet.Tick(float dt)` | `Model.Physics/IntegrationSet.cs:160` | The per-FixedUpdate physics pass: bounds → accel → brakes(½) → Verlet → 4× constraints → brakes(½) → recenter → position |
| `IntegrationSet.UpdateAcceleration()` | `:385` | Pulls `TractiveForce + GravityForce` per car → `element.acceleration` (m/s²) |
| `IntegrationSet.ApplyVerlet(float dt)` | `:406` | Position-Verlet integrator step |
| `IntegrationSet.ApplyBrakes(...)` | `:416` | Brake retarding force, applied symmetric-split half before/after Verlet |
| `IntegrationSet.IntegrateConstraints(float dt)` | `:479` | The slack/coupler constraint pass — runs **4 times** per Tick |
| `IntegrationSet.PositionCars(float dt, bool init)` | `:179` | Convert consist-space position → world `Location`; emit collision events |
| `IntegrationSet.SortElements()` | `:585` | Nearest-neighbour walk to re-order `_elements` after add/remove |
| `IntegrationSet.UpdateBounds()` | `:245` | Compute `_lowerBound`/`_upperBound` from foreign cars + dead-end nodes |
| `IntegrationSet.ValidateConsistency()` | `:906` | Per-tick coupler/air consistency + tender forced-reconnect |

---

## The Tick spine

```csharp
public virtual void Tick(float dt) {                     // IntegrationSet.cs:160
    if (_lastTickPositioned || !_hasUpdatedBoundsOnce)
        UpdateBounds();
    UpdateAcceleration();                                // pulls TractiveForce, GravityForce
    float dt2 = dt / 2f;
    ApplyBrakes(dt2, dt, BrakeIntegrationPhase.Acceleration);   // ½ brake → modifies acceleration
    ApplyVerlet(dt);                                     // x' = x + (x - x_old) + a·dt²
    for (int i = 0; i < 4; i++)
        IntegrateConstraints(dt);                        // 4 iterations of slack/coupling/bounds
    ApplyBrakes(dt2, dt, BrakeIntegrationPhase.Velocity);// ½ brake → modifies position (velocity proxy)
    RecenterPositionsIfNeeded();                         // keep |position| < 1000m for float precision
    PositionCars(dt, isInitialPosition: false);          // world-space writeback + collision events
}
```

`UpdateBounds` is conditional: only runs when the set actually moved last tick (`_lastTickPositioned`) or hasn't bounded once. Saves work for at-rest sets that just had a foreign car arrive (the `_hasUpdatedBoundsOnce` check).

The four-iteration constraint solver is the rigid-body trick: each iteration enforces the slack constraint pair-wise, but pushing one car affects the gap to its other neighbour. Four iterations propagate the constraint along consists of moderate length. Long consists may not fully resolve in one tick — but next tick's iterations continue from the carried-over position state.

### Patch candidates (Tick spine)

| Method | Why patch |
|---|---|
| `IntegrationSet.Tick` | Replace the whole pass. Risky — runs per-set per-tick. Patch only if you're rewriting the physics model end-to-end |
| `IntegrationSet.UpdateAcceleration` postfix | **Inject custom forces** (wind, magnetic brake, mod-side traction modifier) by mutating `element.acceleration` after vanilla writes it |
| `IntegrationSet.ApplyVerlet` prefix returning `false` | Replace integrator (e.g., RK4, semi-implicit Euler) |
| `IntegrationSet.ApplyBrakes` prefix returning `false` | Replace brake force model entirely |
| `IntegrationSet.IntegrateConstraints` postfix | Add custom constraints (e.g., min-distance for cabooses, hard rear-end protection) |
| `IntegrationSet.PositionCars` postfix | React to per-tick position deltas. Cheaper than per-car `OnMove` listeners |

---

## `UpdateAcceleration` — force aggregation

```csharp
private void UpdateAcceleration() {                      // IntegrationSet.cs:385
    foreach (Element element in _elements) {
        Car car = element.car;
        float orientation = car.Orientation;             // ±1
        if (ActiveCars) {                                // host (or solo client testing)
            float massKg = car.Weight * 0.453592f;       // lb → kg
            float tractiveN = orientation * car.TractiveForce * 4.44822f;     // lbf → N
            float gravityN  = orientation * car.GravityForce  * 4.44822f;
            float netForceN = tractiveN + gravityN;
            element.acceleration = -netForceN / massKg;  // NEGATED for some sign convention
        } else {
            element.acceleration = orientation * (-car.compensatingAcceleration);
        }
    }
}
```

**Sign quirk:** `acceleration = -netForce / mass` — note the leading minus. This works because `Element.position` increases from "A end" to "B end" of the consist; positive tractive force on a car oriented `FrontIsA=true` (orientation=1) would move it toward A (decreasing position), hence the negation. Verify when computing custom forces: a positive force in your mod that you want to push the car *forward in body axis* should be sign-matched to vanilla `TractiveForce`.

**HIGH-VALUE PATCH POINT:** `UpdateAcceleration` is the **single per-tick aggregator** of all forces (traction + gravity). Mods that want to inject any "in-train force" — wind, magnetic brake, dispatch override, mod-side adhesion — should patch this method postfix and add to `element.acceleration`. Conversion: `your_newtons_lbf * 4.44822 * orientation / massKg`. Don't forget to clear the contribution next tick (it's overwritten each call).

The `ActiveCars` branch matters: if `ActiveCars=false` (i.e., this is a `RemoteIntegrationSet` running on a client), acceleration comes from `Car.compensatingAcceleration` — a single scalar per car set externally. Clients don't aggregate forces; they just play back what the host sent.

---

## `ApplyVerlet` — position integration

```csharp
private void ApplyVerlet(float dt) {                     // IntegrationSet.cs:406
    foreach (Element element in _elements) {
        float position = element.position;
        element.position += element.position - element.oldPosition + element.acceleration * dt * dt;
        element.oldPosition = position;
    }
}
```

Standard position-Verlet: `x_{n+1} = x_n + (x_n - x_{n-1}) + a·dt²`. Velocity is implicit in `(position - oldPosition)`. After this call, `element.oldPosition` holds the pre-Verlet position; the difference is the per-tick distance (NOT m/s — divide by `dt`).

No damping term. Energy is conserved up to the brake/constraint passes that follow.

---

## Brake integration (the two-phase trick)

```csharp
ApplyBrakes(dt2, dt, BrakeIntegrationPhase.Acceleration);   // BEFORE Verlet — modifies acceleration
ApplyVerlet(dt);
for (int i = 0; i < 4; i++) IntegrateConstraints(dt);
ApplyBrakes(dt2, dt, BrakeIntegrationPhase.Velocity);       // AFTER constraints — modifies position directly
```

`dt2 = dt/2`. The brake force is applied as two half-steps — symmetric Strang-splitting style — to avoid energy drift from the otherwise-explicit braking.

```csharp
private void ApplyBrakes(float dt, float dtForVelocity, BrakeIntegrationPhase phase) {  // :416
    if (!ActiveCars || dt == 0f) return;                 // CLIENTS DON'T APPLY BRAKES (no-op)
    foreach (Element element in _elements) {
        float dx = element.position - element.oldPosition;
        if (dx == 0f && element.acceleration == 0f) continue;

        float absVelocity = Mathf.Abs(dx / dtForVelocity);
        float massKg = element.car.Weight * 0.453592f;
        float predictedDx = dx + element.acceleration * dt * dt;
        float signOfMotion = (phase == Acceleration) ? Mathf.Sign(predictedDx) : Mathf.Sign(dx);
        float retardingN = CalculateRetardingForce(element, absVelocity);
        float brakeAccel = -signOfMotion * retardingN / massKg;     // opposes motion

        if (phase == Acceleration) {
            // Apply as acceleration; if it would reverse the predicted direction, freeze
            float newPredicted = dx + (element.acceleration + brakeAccel) * dt * dt;
            if (Math.Abs(Mathf.Sign(newPredicted) - Mathf.Sign(predictedDx)) > 0.0001f) {
                element.position = element.oldPosition;             // freeze (prevent overshoot)
                element.acceleration = 0f;
            } else {
                element.acceleration += brakeAccel;
            }
        } else { // Velocity phase
            float deltaPos = brakeAccel * dt * dt;
            if ((deltaPos + dx) / dx < 0f)              // would reverse direction
                element.position = element.oldPosition;
            else
                element.position += deltaPos;
        }
    }
}
```

**Anti-overshoot logic:** in both phases, if applying the brake would reverse the car's direction (i.e. accelerate it backward through zero), instead **freeze** the car at `oldPosition`. This avoids the physics-class problem where a stopped car oscillates as the brake "pushes back" past zero.

### Retarding force breakdown

```csharp
private float CalculateRetardingForce(Element entry, float absVelocity) {  // :465
    Car car = entry.car;
    float velMph = absVelocity * 2.23694f;
    float brakeN  = car.CalculateBrakingForce(car.air.brakePercent, absVelocity);   // air brake
    float weightTons = car.Weight / 2000f;
    float davisLbf = (1.3f + 29f / weightTons + 0.045f * velMph + 0.063f * velMph * velMph / weightTons) * weightTons;
    float davisN   = davisLbf * 4.44822f;                                            // rolling resistance (Davis)
    float overspeedN = Mathf.Exp((velMph - car.maxSpeedMph) / 10f + 7.1f);          // exp speed-cap wall
    float curveN  = car.CalculateCurvatureRetardingForce(absVelocity);              // curvature drag + binding
    float derailN = car.CalculateDerailedRetardingForce();                          // Weight*0.7 if derailed else 0
    return brakeN + davisN + overspeedN + curveN + derailN;
}
```

Five contributions:

| Term | Formula | Source |
|---|---|---|
| Brake | `Car.CalculateBrakingForce(brakePercent, absVelocity)` | `Car.cs:2991` — uses `Config.brakeForceCurve` × `nominalBrakingForce` × `BrakeForceMultiplier` × Lerp-by-Condition |
| Davis (rolling resistance) | `(1.3 + 29/T + 0.045·v + 0.063·v²/T) · T · 4.44822` newtons | inline (`:472`); `T = Weight/2000`, `v = mph` |
| Overspeed | `Exp((v - maxSpeedMph)/10 + 7.1)` newtons (always positive!) | inline (`:473`) — exponential wall |
| Curvature | curvature retarding + binding-due-to-curvature | `Car.cs:2898` |
| Derailed | `Weight * 0.7` if derailed, else 0 | `Car.cs:2905` |

**Overspeed term is always positive.** At `v=0` it's `Exp(-maxSpeedMph/10 + 7.1)`. For a 100 mph cap this is `Exp(-10+7.1) = Exp(-2.9) ≈ 0.055 N` — negligible. But it's *not zero*. Reproduces in `Car.cs:2991` flow as a constant background drag.

**HIGH-VALUE FINDING:** `BrakeForceMultiplier` (`Car.cs:236`) and `BrakeForceMultiplierHandbrake` (`:238`) are public `static float` global tuning knobs. Modders can tune these directly. Changing `Car.TractiveForceMultiplier` (default 1.1f, `Car.cs:753`) similarly amplifies all traction across all cars.

### Patch candidates (brakes)

| Method | Why patch |
|---|---|
| `IntegrationSet.CalculateRetardingForce` | Replace the rolling-resistance / overspeed / curvature mix. Pure function — easy to substitute |
| `IntegrationSet.ApplyBrakes` | Replace the symmetric-split scheme entirely (e.g., implicit-Euler brake) |
| `Car.CalculateBrakingForce` | Per-car brake formula override |
| `Car.maxSpeedMph` (field) | Per-car cap. Field, not property — assign directly. Patch `Car.Setup` postfix to apply per-archetype caps |
| `Car.BrakeForceMultiplier` (static) | Global brake strength. One-line global modifier |
| `Car.TractiveForceMultiplier` (static, get/set) | Global traction strength |

---

## `IntegrateConstraints` (the slack/coupler pass)

Runs 4 times per Tick. Per iteration:

```csharp
private void IntegrateConstraints(float wholeDeltaTime) {        // IntegrationSet.cs:479
    for (int i = 0; i < _elements.Count - 1; i++) {
        Element e0 = _elements[i];
        Element e1 = _elements[i + 1];
        float maxSeparation = 1f + e0.SlackB + e1.SlackA;        // 1m + slack tolerance
        bool coupled = AreCoupled(e0, e1);
        float gap = e1.position - e1.CarRadius - (e0.position + e0.CarRadius);   // edge-to-edge
        float correction = 0f;

        if (gap < 1f) {                                          // OVERLAP — push apart
            correction = 1f - gap;
            if (!coupled) {
                float deltaV = Mathf.Abs(e0.Velocity - e1.Velocity) / wholeDeltaTime;
                if (deltaV > 0.22351964f)                        // 0.5 mph
                    Couple(e0, e1, deltaV);                      // ← AUTO-COUPLE
            }
            if (e0.SlackStretch < 0f) {                          // was compressed, now reversing
                e0.SlackStretch = 0f;
                e0.SlackStretchDidChangeDirection = true;        // ← collision flag
            }
            e0.SlackStretch += correction;
        }
        else if (coupled && gap > maxSeparation) {               // STRETCH — pull together
            correction = maxSeparation - gap;                    // negative
            if (e0.SlackStretch > 0f) {                          // was tension, now reversing
                e0.SlackStretch = 0f;
                e0.SlackStretchDidChangeDirection = true;
            }
            e0.SlackStretch += correction;
        }
        else if (!coupled && AreAirHosesConnected(e0, e1) && gap > 1.5f) {
            BreakAirHoses(e0, e1);                               // air-only too far → snap
        }

        // Mass-weighted position correction split
        float w = e0.car.Weight / (e0.car.Weight + e1.car.Weight);
        e0.position -= (1f - w) * correction;                    // lighter car moves more
        e1.position += w * correction;
    }
    // Lower bound (foreign car or dead-end ahead of A end)
    if (_elements.Count > 0 && _lowerBound.HasValue) {
        Element first = _elements[0];
        float aEnd = first.position - first.CarRadius;
        if (aEnd < _lowerBound.Value) {
            first.position += 2f * (_lowerBound.Value - aEnd);                                    // push back
            EventHandler.IntegrationSetCarsDidCollide(first.car, null,
                (_lowerBound.Value - aEnd) / wholeDeltaTime, isIn: true);                         // boundary collision
        }
    }
    // Upper bound (mirrors lower)
    if (_upperBound.HasValue) {
        Element last = _elements.Last();
        float bEnd = last.position + last.CarRadius;
        if (_upperBound.Value < bEnd) {
            last.position += 2f * (_upperBound.Value - bEnd);
            EventHandler.IntegrationSetCarsDidCollide(last.car, null,
                (_upperBound.Value - bEnd) / wholeDeltaTime, isIn: true);
        }
    }
}
```

### Slack tolerance source

Only one source: `Car.CouplerSlack(End)` (`Car.cs:1775`):

```csharp
public float CouplerSlack(End end) {
    return end switch {
        End.F => WantsEndGear(End.F) ? 0.02f : 0.001f,           // 2cm with end gear, 1mm without
        End.R => WantsEndGear(End.R) ? 0.02f : 0.001f,
        _ => throw new ArgumentOutOfRangeException(...),
    };
}
```

**Hardcoded constants.** No definition-driven slack. Override `Car.CouplerSlack` virtual for per-class tolerance, or `Car.WantsEndGear` virtual to skip end gear (tender front sets to 1mm via `WantsEndGear` returning false).

`Element.SlackA`/`SlackB` are cached at construction (`IntegrationSet.cs:44-45`) — changing `CouplerSlack` after the `Element` is built has no effect until the set rebuilds (Union/Split/AddCar). To force a refresh, trigger any topology change, or patch `Element` ctor via reflection.

### `SlackStretch` semantics

`Element.SlackStretch` accumulates **per-tick correction deltas** while in one slack state:

- `SlackStretch > 0` — coupled cars in tension (slack pulled out, ~0…2cm).
- `SlackStretch < 0` — cars in compression (overlap correction, can grow large during impact).
- `SlackStretch == 0` and `SlackStretchDidChangeDirection == true` — just crossed zero **this tick**; collision event will fire in `PositionCars` if magnitude is large enough.

The flag is **cleared at end of `PositionCars`** (`:217`), so it's a one-tick latch.

### Auto-couple threshold

```csharp
const float autoCoupleVelMps = 0.22351964f;             // = 0.5 mph in m/s
```

When uncoupled cars overlap with relative velocity > 0.5 mph, `Couple(e0, e1, deltaV)` fires. That dispatches both `IntegrationSetDidCouple` and `IntegrationSetCarsDidCollide` (see [couplers.md › auto-couple](couplers.md#auto-couple-impact-driven)).

### Air-hose break threshold

```csharp
if (!coupled && AreAirHosesConnected(e0, e1) && gap > 1.5f) BreakAirHoses(e0, e1);
```

Uncoupled cars whose air hoses are still connected break when gap > 1.5m. Coupled cars never break their hoses through this path (the `else if` only fires with `!coupled`).

### Bounds enforcement

`_lowerBound` / `_upperBound` are computed in `UpdateBounds` (`:245`):

- **Foreign car ahead** — `EventHandler.IntegrationSetCheckForCar` returns a non-null car not in this set → bound at the consist edge.
- **Dead-end node** — `_graph.NodeIsDeadEnd(...)` → bound at `position ± wheelInset` (or 0.01m for turntable).

If positions exceed the bound, the iteration corrects with **double-magnitude** (`+= 2f * delta`) and emits `IntegrationSetCarsDidCollide(car, null, deltaV, isIn:true)` — boundary collision with `car1=null`. See [couplers.md › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline).

### Mass-weighted correction split

```csharp
float w = e0.car.Weight / (e0.car.Weight + e1.car.Weight);
e0.position -= (1f - w) * correction;     // light car moves MORE
e1.position += w * correction;            // heavy car moves LESS
```

Inverse-mass weighting (heavier cars push lighter cars more). Conserves momentum *only if* both contributions are applied — which they are, in the same iteration. But the 4 iterations don't perfectly converge for long consists; positions drift slightly tick-to-tick, which `RebuildPositions` (every 100 positioned ticks) corrects from world locations.

### Patch candidates (constraints)

| Method | Why patch |
|---|---|
| `IntegrationSet.IntegrateConstraints` | Add custom constraints, change auto-couple threshold, replace gap math. Per-set per-tick × 4 iterations — high cost surface |
| `IntegrationSet.AreCoupled` (private) | Override coupler topology. `Car.EndGearB.IsCoupled && nextCar.EndGearA.IsCoupled` is the rule — simple to wrap |
| `IntegrationSet.AreAirHosesConnected` (private) | Override air topology |
| `IntegrationSet.Couple` (private) | Suppress auto-couple's immediate collision event, customize coupling logic. See [couplers.md](couplers.md#auto-couple-impact-driven) |
| `IntegrationSet.BreakAirHoses` (private) | Customize air-break event |

---

## `PositionCars` — world-space writeback + collision events

```csharp
private void PositionCars(float dt, bool isInitialPosition) {   // IntegrationSet.cs:179
    bool flag = false;
    foreach (Element element in _elements) {
        float dx = element.position - element.oldPosition;
        dx *= -element.car.Orientation;                          // sign-flip to body axis
        element.car.velocity = (dt == 0f) ? 0f : (dx / dt);      // m/s body-axis written to Car
        bool shouldPos = ShouldPosition(element);                // dist > 1mm or ShouldUpdatePosition
        if (isInitialPosition || shouldPos) {
            flag = true;
            try {
                Location wbF = _graph.LocationByMoving(element.car.WheelBoundsF, dx);
                MovementInfo info = new MovementInfo(dt, Mathf.Abs(dx), element.car.NormalizedTractiveEffort);
                element.car.PositionWheelBoundsFront(wbF, _graph, info, update: true);
            }
            catch (Exception ex) { Log.Error(ex, "..."); }
            if (isInitialPosition) continue;
            Dirty = true;

            // Slack-direction collision event (one per tick per element)
            if (element.SlackStretchDidChangeDirection
                && Mathf.InverseLerp(0.001f, 0.006f, Mathf.Abs(element.SlackStretch)) > 0.1f) {
                bool isIn = element.SlackStretch > 0f;
                Car nextCar = _elements[_elements.IndexOf(element) + 1].car;
                float deltaV = Mathf.Abs(VelocityA(element.car) - VelocityA(nextCar));
                EventHandler.IntegrationSetCarsDidCollide(element.car, nextCar, deltaV, isIn);
            }
        }
        element.SlackStretchDidChangeDirection = false;          // CLEAR after possible event
    }
    _lastTickPositioned = flag;
    if (flag) {
        _ticksSinceRebuild++;
        if (_ticksSinceRebuild > 100) RebuildPositions();        // periodic re-anchor from world
    }
}
```

### Collision-event sensitivity

```csharp
Mathf.InverseLerp(0.001f, 0.006f, |SlackStretch|) > 0.1f
```

`InverseLerp(0.001, 0.006, x) > 0.1` solves to `x > 0.0015`. So slack reversals smaller than **1.5 mm** are filtered out. Above that, the magnitude is used by `IntegrationSetCarsDidCollide` *only* via the deltaV; the threshold is just a noise gate.

`isIn = SlackStretch > 0f` — but reversal events fire *as you cross zero*, so `SlackStretch` at this point is whatever the iteration set it to during the cross. Read carefully: if you went from compression to tension this tick, the iteration sets `SlackStretch=0` then `+= correction (positive)`, so it's now positive → `isIn=true` is reported as "slack-in" even though you went the *other* way. **Verify intent in any patch** — the convention in the comment doesn't fully match the code if you're chasing audio.

`deltaV = |VelocityA(carA) - VelocityA(carB)|` — same calculation as `IntegrateConstraints` for auto-couple velocity.

### `ShouldPosition` gate

```csharp
private static bool ShouldPosition(Element entry) {              // IntegrationSet.cs:235
    if (Mathf.Abs(entry.position - entry.PositionAtLastLocationUpdate) > 0.001f
        || entry.car.ShouldUpdatePosition()) {
        entry.PositionAtLastLocationUpdate = entry.position;
        return true;
    }
    return false;
}
```

Cars only re-position when they've moved more than 1mm OR `Car.ShouldUpdatePosition()` returns true (default false; overridable virtual `Car.cs:1965`). This is a major performance optimization for at-rest sets — even when ticked, motionless cars don't traverse the graph or re-fire `OnPosition`.

### `_ticksSinceRebuild > 100`

After 100 ticks of *successful positioning*, `RebuildPositions` fires (`IntegrationSet.cs:551`). At 50 Hz, that's every 2 seconds. `RebuildPositions` re-derives consist-space positions from the actual world `WheelBounds` distances, correcting any drift from the constraint solver's imperfect convergence.

### Patch candidates (PositionCars)

| Method | Why patch |
|---|---|
| `IntegrationSet.PositionCars` postfix | The cleanest place to observe per-tick element velocity/position/slack. Runs after constraints, before next tick |
| `IntegrationSet.ShouldPosition` (private static) | Lower the 1mm threshold for higher position-update frequency (cost: perf) |
| `IntegrationSet.RebuildPositions` (private) | Replace the world-anchor recovery logic |

---

## `SortElements` — topological re-order

```csharp
private void SortElements() {                                    // IntegrationSet.cs:585
    if (!ActiveCars || _elements.Count == 0) return;             // CLIENT: no-op
    LinkedList<Car> linked = new LinkedList<Car>();
    List<Car> remaining = new List<Car>(_elements.Select(e => e.car));
    Car seed = remaining.Last();
    remaining.RemoveAt(remaining.Count - 1);
    linked.AddFirst(seed);

    Dictionary<string, Vector3> centerCache = ...;               // pre-compute world centers

    while (remaining.Any()) {
        Vector3 frontCenter = linked.First.Value.GetCenterPosition(_graph);
        Vector3 backCenter  = linked.Last.Value.GetCenterPosition(_graph);
        // Find car in 'remaining' nearest to either end of 'linked'
        Car nearest = remaining.First();
        float bestDist = Mathf.Min((frontCenter - centerCache[nearest.id]).magnitude,
                                   (backCenter  - centerCache[nearest.id]).magnitude);
        foreach (var c in remaining) {
            float d = Mathf.Min((frontCenter-centerCache[c.id]).magnitude,
                                (backCenter -centerCache[c.id]).magnitude);
            if (d < bestDist) { nearest = c; bestDist = d; }
        }
        remaining.Remove(nearest);
        // Decide which end + reverse if facing opposite
        Vector3 nearestCenter = centerCache[nearest.id];
        Vector3 nearestDir = _graph.GetPositionDirection(nearest.LocationA).Direction;
        Vector3 frontDir   = _graph.GetPositionDirection(linked.First.Value.LocationA).Direction;
        Vector3 backDir    = _graph.GetPositionDirection(linked.Last.Value .LocationA).Direction;

        if (Vector3.Dot(nearestCenter - frontCenter, frontDir) >= 0f) {
            if (Vector3.Dot(nearestDir, frontDir) < 0f) nearest.Reverse();   // FrontIsA flip
            linked.AddFirst(nearest);
        } else {
            if (Vector3.Dot(nearestDir, backDir) < 0f) nearest.Reverse();
            linked.AddLast(nearest);
        }
    }
    _elements.Clear();
    _elements.AddRange(linked.Select(c => new Element(c)));
    InvalidateCachedCarIndexes();

    // First/last car with dangling connections → sever
    Car first = _elements[0].car;
    Car last  = _elements.Last().car;
    if (first.EndGearA.IsAirConnected || first.EndGearA.IsCoupled)
        EventHandler.IntegrationSetRequestsBreakConnections(first, Car.LogicalEnd.A);
    if (last.EndGearB.IsAirConnected || last.EndGearB.IsCoupled)
        EventHandler.IntegrationSetRequestsBreakConnections(last, Car.LogicalEnd.B);
}
```

**Algorithm:** seed with last car, then nearest-neighbour walk — pick the unsorted car whose center is closest to either end of the partial chain, decide which end based on dot-product with the chain's facing direction, and possibly `Reverse()` (flip `FrontIsA`) if its own A-direction opposes the chain's.

**Implications:**

- **Quadratic in number of cars** — scans all remaining for each step. Fine for typical 50-car consists; could become noticeable at 200+.
- **`Car.FrontIsA` may flip** during sort. After any topology change, mod-side caches keyed on `FrontIsA` are invalid.
- **Dangling end-cars get severed.** If the sorted result has the first car with `EndGearA.IsCoupled`, that's a topological inconsistency (the "A end" should never be coupled — there's nothing in the set on that side). The sort cleans it up by emitting `IntegrationSetRequestsBreakConnections` on the offending end.
- **`_elements` is rebuilt fresh** — every Element ctor recomputes `SlackA`/`SlackB`/`CarRadius` from the *current* `Car` state. So overriding `CouplerSlack` in a subclass *will* take effect after the next sort.
- **Clients skip entirely** (`!ActiveCars` early return). They trust the host's snapshot order.

### `RebuildPositions`

```csharp
private void RebuildPositions() {                                // IntegrationSet.cs:551
    float cursor = 0f;
    for (int i = 0; i < _elements.Count; i++) {
        Element e = _elements[i];
        cursor += e.CarRadius;
        e.position = cursor;
        float vel = e.car.Orientation * e.car.velocity;
        e.oldPosition = e.position + vel * Time.fixedDeltaTime;
        cursor += e.CarRadius;
        if (i + 1 < _elements.Count) {
            Car next = _elements[i + 1].car;
            float distance;
            if (!_graph.TryGetDistanceBetweenSameRoute(e.car.WheelBoundsB, next.WheelBoundsA, out distance))
                Log.Warning(...);
            float gap = distance - (e.car.WheelInsetB + next.WheelInsetA);
            if (_graph.GetDistanceBetweenClose(e.car.LocationA, next.LocationA) < gap)
                gap *= -1f;                                      // detected overlap → flip sign
            cursor += gap;
        }
    }
    foreach (var e in _elements) e.car.SetOffsetWithinSet(e.position);
    Dirty = true;
    _ticksSinceRebuild = 0;
}
```

Re-derives consist-space positions from world `WheelBounds`. Works left-to-right, accumulating a cursor. Sets `oldPosition` to project current `Car.velocity` *backwards* in time (so the next Verlet preserves the velocity).

The "if overlap → flip sign" is a robustness against incorrect graph distance reports — checks via `GetDistanceBetweenClose` (a different distance metric) and inverts if the result disagrees.

---

## `UpdateBounds` — foreign-car / dead-end detection

```csharp
private void UpdateBounds() {                                    // IntegrationSet.cs:245
    if (_elements.Count == 0) return;
    Element first = _elements[0];
    Element last  = _elements.Last();

    _lowerBound = null;
    _upperBound = null;

    // Foreign car ahead of A end?
    Vector3 aPosWorld = _graph.GetPosition(first.car.WheelBoundsA);
    Vector3 aProbePos = aPosWorld + (aPosWorld - first.car.GetCenterPosition(_graph)).normalized
                                    * (5f + first.car.WheelInsetA);
    if (CheckForEnemyCar(aProbePos, first.car.WheelBoundsA, first.car) != null)
        _lowerBound = first.position - first.CarRadius;

    // Mirror at B end
    // ...

    // Dead-end at A end?
    if (!_lowerBound.HasValue) {
        var trackEnd = BoundingEnd(first.car.WheelBoundsA.segment, aPosWorld, ...);
        var node = first.car.WheelBoundsA.segment.NodeForEnd(trackEnd);
        if (_graph.NodeIsDeadEnd(node, out _)) {
            float pad = (node.turntable != null) ? 0.01f : (0.5f + first.car.WheelInsetA);
            _lowerBound = first.position + pad - (first.car.WheelBoundsA.DistanceTo(trackEnd) + first.CarRadius);
        }
    }
    // Mirror at B end
    _hasUpdatedBoundsOnce = true;
}
```

`CheckForEnemyCar` (`:321`) calls `EventHandler.IntegrationSetCheckForCar(point)` — `TrainController.CheckForCarAtPoint`. Filters out:

- The car being probed itself.
- Cars in this same set (logged as Error).
- Cars whose `WheelBoundsF`/`WheelBoundsR` is on the same segment as the probe (must be the same car logically).
- Cars that share a route within `10 + max(wheelInset)` (these are reachable by motion — handled by normal coupling).

So bounds only fire for cars on a *different* route, in spatial proximity but topologically separate. Common case: cars on parallel sidings stopped near a switch.

Dead-end nodes: `0.01m` padding for turntables (cars sit very close), `0.5 + wheelInset` for normal dead-ends. The bound is set such that `position ± CarRadius ≤ bound`.

---

## `ValidateConsistency` — per-tick coupler/air audit

```csharp
public void ValidateConsistency() {                              // IntegrationSet.cs:906
    if (!ActiveCars) return;                                     // CLIENT: no-op
    for (int i = 0; i < _elements.Count - 1; i++) {
        Element e0 = _elements[i];
        Element e1 = _elements[i + 1];
        Car c0 = e0.car, c1 = e1.car;

        if (c0.EndGearB.IsCoupled != c1.EndGearA.IsCoupled) {
            Log.Error("Inconsistent IsCoupled: ...");
            FixInconsistentConnectionsByBreakingConnections(e0, e1);   // BOTH ends → false
        }
        if (c0.EndGearB.IsAirConnected != c1.EndGearA.IsAirConnected) {
            Log.Error("Inconsistent IsAirConnected: ...");
            FixInconsistentConnectionsByBreakingConnections(e0, e1);   // (called twice in vanilla — double-clear)
        }

        // FORCED RECONNECT (tender↔engine)
        if (c0.ForceConnectedToAtRear(c1) && c0.FrontIsA
            && (!c0.EndGearB.IsAirConnected || !c0.EndGearB.IsAirConnected))   // ← typo (same field twice)
        {
            EventHandler.IntegrationSetRequestsReconnect(c0, c1);
        } else if (c1.ForceConnectedToAtRear(c0) && !c1.FrontIsA
                   && (!c1.EndGearA.IsAirConnected || !c1.EndGearA.IsCoupled))
        {
            EventHandler.IntegrationSetRequestsReconnect(c1, c0);
        }
    }
    // First/last car with dangling end → sever
    if (_elements.Count > 0) {
        Car first = _elements[0].car;
        Car last  = _elements.Last().car;
        if (first.EndGearA.IsCoupled || first.EndGearA.IsAirConnected) {
            Log.Error("Inconsistent first: ...");
            EventHandler.IntegrationSetRequestsBreakConnections(first, Car.LogicalEnd.A);
        }
        if (last.EndGearB.IsCoupled || last.EndGearB.IsAirConnected) {
            Log.Error("Inconsistent last: ...");
            EventHandler.IntegrationSetRequestsBreakConnections(last, Car.LogicalEnd.B);
        }
    }
}
```

**HIGH-VALUE FINDINGS:**

1. **Vanilla typo at `:928`** — the condition reads `(!c0.EndGearB.IsAirConnected || !c0.EndGearB.IsAirConnected)` — same field tested twice. Almost certainly intended `IsAirConnected || !IsCoupled`. As written, the reconnect path triggers when `IsAirConnected` is false (regardless of `IsCoupled`). The mirror branch at `:932` is correct (`!IsAirConnected || !IsCoupled`).
2. **`ForceConnectedToAtRear` is wired wrong on SteamLocomotive** — see [consist-integration.md › member relationships](consist-integration.md#tenderengine-forced-reconnection-validateconsistency). The override checks self-archetype, not the partner.
3. **`FixInconsistentConnectionsByBreakingConnections` is called twice** when both `IsCoupled` and `IsAirConnected` mismatch — wasteful but idempotent.
4. **Validation runs every tick before the physics pass** (`TrainController.cs:442`). Set-state inconsistencies are caught within ~20ms.

`Reconnect` re-asserts both `IsCoupled` and `IsAirConnected` via host-side `ApplyEndGearChange` calls (see `TrainController.IntegrationSetRequestsReconnect` `:1267`). It hard-codes `End.R` for the engine and `End.F` for the tender — only useful for steam-loco↔tender pairs.

### Patch candidates (validation)

| Method | Why patch |
|---|---|
| `IntegrationSet.ValidateConsistency` | Add custom forced-reconnect rules (e.g., articulated cars). Fix the `IsAirConnected` typo if it's actually causing in-game issues |
| `IntegrationSet.FixInconsistentConnectionsByBreakingConnections` (private) | Override conflict resolution — instead of breaking, you might force one side to match the other |
| `Car.ForceConnectedToAtRear` (virtual) | Per-class forced reconnect rule. Override in mod-added car classes |

---

## `RecenterPositionsIfNeeded` — float precision

```csharp
private void RecenterPositionsIfNeeded() {                       // IntegrationSet.cs:659
    float minPos = float.MaxValue, maxPos = float.MinValue;
    foreach (var e in _elements) { minPos = Min(minPos, e.position); maxPos = Max(maxPos, e.position); }
    float spread = maxPos - minPos;
    float threshold = (spread < 2000f) ? 1000f : spread;
    if (Mathf.Abs(minPos) < threshold && Mathf.Abs(maxPos) < threshold) return;
    float shift = -Mathf.Lerp(minPos, maxPos, 0.5f);
    foreach (var e in _elements) {
        e.position += shift;
        e.oldPosition += shift;
        e.PositionAtLastLocationUpdate += shift;
    }
}
```

Shifts the whole consist's position field so the midpoint is at zero — keeps `position` magnitudes bounded for Verlet precision. Threshold scales with spread for very long consists.

Doesn't affect world positions (those are recovered from `Graph.LocationByMoving` deltas).

---

## `SetVelocity` and `AddVelocityToCar` — external velocity poke

```csharp
public void SetVelocity(float velocity, IReadOnlyList<Car> cars) {     // IntegrationSet.cs:1192
    foreach (var item in _elements.Where(e => cars.Contains(e.car)))
        item.oldPosition = item.position + velocity * Time.fixedDeltaTime;
}

public void AddVelocityToCar(Car car, float velocity, float maxVelocity) {  // :1102
    float dt = Time.fixedDeltaTime;
    float dx = velocity * dt;
    int idx = ValidIndexOfCar(car);
    Element e = _elements[idx];
    float currentV = (e.position - e.oldPosition) / dt;
    float newPos   = e.position + (car.FrontIsA ? dx : -dx);
    float predictedV = (newPos - e.oldPosition) / dt;
    float currentAbs = |currentV|, predictedAbs = |predictedV|;
    if ((!(currentAbs < maxVelocity) || !(predictedAbs > maxVelocity)) &&
        (!(currentAbs > maxVelocity) || !(predictedAbs > currentAbs)))
        _elements[idx].position = newPos;
}
```

`SetVelocity` writes the whole velocity by adjusting `oldPosition`. Used by:

- `TrainController.cs:1008` (decoupling — set newly-uncoupled cars to 0)
- `AutoEngineer.cs:437` (auto-stop)
- `ScriptCar.cs:63` (script API)
- `SpeedCommand` (`/speed` console command)

`AddVelocityToCar` adds velocity but caps at `maxVelocity` (vanilla calls with `1.3411179f` = 3 mph — the `manualMoveCarForce` impulse). The "won't add if already over and going faster" guard prevents griefing.

### Patch candidates

| Method | Why patch |
|---|---|
| `IntegrationSet.SetVelocity` | Hook external velocity injections (script, autoengineer-stop, command console) |
| `IntegrationSet.AddVelocityToCar` | Same for the manual nudge |

---

## Slack-direction collision event reference

| Source line | What fires |
|---|---|
| `IntegrationSet.cs:214` | Slack reversal during `PositionCars`: `IntegrationSetCarsDidCollide(e0, e1, deltaV, isIn = SlackStretch>0)` |
| `IntegrationSet.cs:536` | Lower-bound (foreign/dead-end) hit: `IntegrationSetCarsDidCollide(first, null, ..., isIn:true)` |
| `IntegrationSet.cs:546` | Upper-bound mirror: `IntegrationSetCarsDidCollide(last, null, ..., isIn:true)` |
| `IntegrationSet.cs:704` | Auto-couple impact: `IntegrationSetCarsDidCollide(e0, e1, deltaV, isIn:true)` (immediately after `IntegrationSetDidCouple`) |

All four feed `TrainController.IntegrationSetCarsDidCollide` (`TrainController.cs:1191`) — see [couplers.md › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline).

Per-tick frequency limits:

- **Per element pair, slack-reversal events fire at most once per Tick** (the flag is cleared in `PositionCars` after the event check, and `PositionCars` runs once per Tick).
- **Auto-couple events fire as many times per Tick as 4-iteration constraints find non-coupled overlapping pairs** — but realistically, two cars only auto-couple once (after that they're `coupled=true` and the branch is skipped). Worst case is one auto-couple per pair per Tick.
- **Bound collision events fire once per Tick per bound** (lower + upper) — but inside the 4-iteration loop, so potentially 4× if the consist keeps slamming the bound. In practice the position correction is `2*delta` (over-corrects), so it usually settles in one iteration.

---

## Gotchas

- **Clients run a no-op solver.** `ApplyBrakes`, `ValidateConsistency`, `SortElements` all early-return on `!ActiveCars`. `RemoteIntegrationSet.Tick` is a totally different method that interpolates from frames. Don't expect host-side patches to fire on clients.
- **The 4 iterations are NOT a fixed-point solver.** Long consists won't fully converge in one Tick — they accumulate residual drift that `RebuildPositions` (every 100 ticks ≈ 2s) corrects from world coords. Patches that need "exactly converged constraints" must run `IntegrateConstraints` more iterations, or implement convergence detection.
- **`element.acceleration` is overwritten every Tick** by `UpdateAcceleration`. Custom-force injections must run *after* (postfix) and *every Tick*; one-shot injections are erased on the next tick.
- **`UpdateAcceleration` sign convention is negated** (`acceleration = -netForce/mass`). When injecting your own force in newtons, follow the same pattern: `element.acceleration += -yourForceN * orientation / massKg`.
- **Brake force formula has hard-coded constants** (Davis 1.3, 29, 0.045, 0.063; overspeed e^7.1) all in `CalculateRetardingForce`. The Davis equation is in lbf using imperial units — `Weight/2000 = tons`, `mph` for velocity. Conversion is explicit, no helper.
- **`maxSpeedMph` exponential wall is always positive**, even at velocity 0. Negligible at slow speeds for high caps, but if you set `maxSpeedMph=0` somehow, you get `Exp(7.1) ≈ 1212 N` of constant drag — a stuck car.
- **`SortElements` is O(N²)**. 100-car consist = 10,000 distance checks every topology change. At extreme consist sizes (200+) this becomes noticeable. Cache `centerCache` is allocated each call (no pool).
- **`SortElements` `_elements.Clear()` then re-creates `Element`s** — cached `SlackA`/`SlackB` are regenerated. Patches that hold `Element` references via reflection are dangling after sort.
- **The `Couple()` private method calls `IntegrationSetDidCouple` BEFORE `IntegrationSetCarsDidCollide`** (`IntegrationSet.cs:703-704`). The didCouple call goes to host-side rejection logic that may decline the couple — but the collide event already fired, so collision damage applies even when coupling is rejected. See [couplers.md › auto-couple](couplers.md#auto-couple-impact-driven).
- **`PositionCars` clears `SlackStretchDidChangeDirection` even when `ShouldPosition` returned false** (line 217 unconditional). At-rest cars never re-emit slack-direction events even if the flag was set during the constraint pass — but at-rest cars also never have non-trivial slack changes, so this is safe in practice.
- **`ValidateConsistency` typo** — the `IsAirConnected || !IsAirConnected` at `:928` should almost certainly be `IsAirConnected || !IsCoupled`. Triggers reconnect more eagerly than intended.
- **`RebuildPositions` flips a gap sign** based on `GetDistanceBetweenClose < gap` (`:569`). This fires when `TryGetDistanceBetweenSameRoute` says cars are far but `GetDistanceBetweenClose` says they're close — graph corner-case. The flip lets `RebuildPositions` produce a sane consist-space layout, but the underlying graph-distance issue is masked, not fixed.
- **`AddVelocityToCar`'s clamp condition is convoluted** (`(!(currentAbs<max) || !(predictedAbs>max)) && (!(currentAbs>max) || !(predictedAbs>currentAbs))`) — De-Morgan reads as: allow the change unless it would push you over the cap, OR push you further over the cap. Test with care if you patch.
- **`compensatingAcceleration` is zeroed in `Car.WillMove`** (`Car.cs:2799`). After a manual reposition, the client's interpolation has no acceleration hint until the next host update.

---

## Cross-references

- **Set lifecycle, replication, member relationships:** [consist-integration.md](consist-integration.md)
- **Coupler state writes (`Car.EndGear`), slack stretch values, `WantsEndGear`/`CouplerSlack`:** [couplers.md › slack state](couplers.md#slack-state--integration)
- **Auto-couple impact handling, `Couple` private flow:** [couplers.md › auto-couple](couplers.md#auto-couple-impact-driven)
- **Slack-reversal collision damage pipeline:** [couplers.md › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline)
- **Derailment force from collision deltaV:** [wear-durability.md › derailment](wear-durability.md#derailment)
- **`Car.CalculateBrakingForce` formula details:** [wear-durability.md › Config curves](wear-durability.md#modelconfig-curves-tuning-surface) (`brakeForceCurve`)
- **`TrainController.FixedUpdate` as canonical hook:** `physics-vanilla-survey.md § The Spine`
