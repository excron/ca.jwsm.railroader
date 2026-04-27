# AutoEngineerPlanner — Deep Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Model.AI/`)
**Companions:** [AutoEngineer (high-level overview)](autoengineer.md), [Locomotive Architecture](locomotive-architecture.md), [Odometer & Movement](odometer-movement.md), [Track Topology](track-topology.md), [Signals & Dispatch](signals-dispatch.md), [Save & Load](save-load.md)

The round-1 [`autoengineer.md`](autoengineer.md) crib sheet covers the **stack** (planner → engineer → subsystems → message wire). This deep sheet drills into the **planner internals**: every PID and what variable each controls, every target-source clause inside `UpdateTargets`, every "hold" condition that can stop motion, the lookahead arithmetic, the Search step machine, the per-tick `ApplyMovement` consumer pipeline, and the persistence/init-order/MP-authority surface from a modder's "I want to replace this" angle. Where round 1 said "this is what it does," this sheet says "here's the exact code path and the patch handle."

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `AutoEngineerPlanner` | `Model.AI/AutoEngineerPlanner.cs:29` | The planner MonoBehaviour. Owns the outer `Loop` coroutine, the `RouteLoop` coroutine, and 4 child subsystem components. |
| `AutoEngineerPlanner.Loop` | `Model.AI/AutoEngineerPlanner.cs:372` | Outer plan loop. Cadence 0.5/1/3 s by speed. Calls `UpdateTargets` each tick. |
| `AutoEngineerPlanner.UpdateTargets` | `Model.AI/AutoEngineerPlanner.cs:451` | Builds the `Targets` list from search + signals + hotbox + flares + cars + manual stop + passenger stop. |
| `AutoEngineerPlanner.Search` (private static) | `Model.AI/AutoEngineerPlanner.cs:1966` | The 10 m-step lookahead walker. Two modes: `Ahead`, `Self`. |
| `AutoEngineerPlanner.ApplyMovement` | `Model.AI/AutoEngineerPlanner.cs:1061` | Per-tick consumer of `MovementInfo` from `BaseLocomotive.FireOnMovement`. Decrements distances. |
| `AutoEngineerPlanner.HandleCommand` | `Model.AI/AutoEngineerPlanner.cs:979` | Player-intent ingress. Side-effects on mode change. |
| `AutoEngineer.MaintainSpeed` | `Model.AI/AutoEngineer.cs:732` | Inner PID loop. Three controllers (throttle / independent / train brake). |
| `AutoEngineer.ContextualTargetVelocity` | `Model.AI/AutoEngineer.cs:451` | The dual-model speed picker (config curve + physics formula `v = sqrt(v_f² + 2ad)`, take min). |
| `AutoEngineer.CalculateLookaheadDistance` | `Model.AI/AutoEngineer.cs:723` | Horizon arithmetic — feeds the planner's Search budget. |
| `AutoEngineerPersistence` | `Model.AI/AutoEngineerPersistence.cs:8` | Struct façade over the 5 `ai*` KVO keys. |
| `AutoEngineerConfig` | `Model.AI/AutoEngineerConfig.cs:6` | The ScriptableObject of all knobs. Singleton at `TrainController.Shared.autoEngineerConfig`. |
| `PIDController` | `Model.AI/PIDController.cs:7` | Generic PID class with integrator decay, growth, max-step rate-limit. |
| `BaseLocomotive.Awake` (host gate) | `Model/BaseLocomotive.cs:426` | Sole construction site for `AutoEngineerPlanner`. |
| `BaseLocomotive.FireOnMovement` | `Model/BaseLocomotive.cs:219` | Sole caller of `AutoEngineerPlanner.ApplyMovement`. |

---

## Planner vs AutoEngineer: the distinction nobody draws cleanly

Round-1 painted them as a single conceptual unit. They are NOT.

```
BaseLocomotive (host only)
   └─ AutoEngineerPlanner   (always exists on host)
         └─ AutoEngineer    (added in AutoEngineerPlanner.Awake)
               └─ AutoOiler (added in AutoEngineer.OnEnable)
```

### The planner is the *only* one that consults the world

The planner reads `Graph`, `CTCSignal`, `TrackMarker`, `PassengerStop`, route search results, and the player's `Orders`. It produces a `Targets` blob — a max speed plus a list of `(speedMph, distance, reason)` tuples — and hands it to `_engineer.SetTargets(...)`.

The engineer NEVER looks at the world. It sees only `Targets` and the locomotive's own physical state (velocity, brake pressures, control surfaces). It runs PIDs and sets `Throttle`, `Reverser`, `LocomotiveBrake`, `TrainBrake`. **An engineer can be driven by any source of `Targets`.**

This separation is the most important fact for modders:

- **Want a custom AI brain?** Patch `AutoEngineerPlanner.Awake` to add your own component, destroy the planner, and have your component drive `_engineer.SetTargets`. The engineer keeps working — same throttles, same brakes, same PIDs — and you get to invent the plan.
- **Want a custom driver?** Patch `BaseLocomotive.Awake` to skip the planner-add. Then your component can add an `AutoEngineer` (or its replacement) directly. **Beware**: the gate is `GetComponent<AutoEngineer>() == null` — if a custom AE-replacement is already attached, the planner is silently skipped. This is the documented vanilla extension hook.

```csharp
// BaseLocomotive.cs:426
if (StateManager.IsHost && GetComponent<AutoEngineer>() == null)
{
    AutoEngineerPlanner = base.gameObject.AddComponent<AutoEngineerPlanner>();
}
```

The contract: if any component (mod or vanilla) presents itself as an `AutoEngineer`, the planner stays out of the way. **`AutoEngineerPlanner` is NOT created on the client side at all.**

### The planner survives even when AI is "off"

`AutoEngineerPlanner` is a permanent host-side fixture. The `_orders.Enabled` check inside `Loop` is what gates active driving. When `Mode == Off`:
- The Loop runs every 0.5 s, calls `OffDuty()`, sleeps.
- `OffDuty` clears `_engineer.Run`, sets empty `Targets`, drops contextual-ignore state, clears `_calledSignal`, clears the pitfall notice.
- Subsystems (`_crossingSignaler`, `_fuelAlerter`, `_hotboxSpotter`, `_passengerStopper`) are **destroyed** (`StopCoroutineAndDestroyChildComponents` at `AutoEngineerPlanner.cs:344`) and recreated each time AI flips on.

`OffDuty` does NOT clear `_lastSignalSpeedRestriction`. Round 1 noted this — it persists across an off-then-on cycle. Cleared only by mode change inside `HandleCommand`, distance-limit expiry inside `ApplyMovement`, the next `WillMove`, or a `ResumeSpeed` contextual order.

---

## The 5 KVO keys — `AutoEngineerPersistence` deep dive

```csharp
// AutoEngineerPersistence.cs:12-20
public const  string AutoEngineerOrdersKey            = "aiOrders";
public const  string AutoEngineerContextualOrdersKey  = "aiCtxOrders";
private const string AutoEngineerManualStopDistanceKey = "aiManualStopDistance";
public const  string AutoEngineerPlannerStatus       = "aiPlannerStatus";
private const string AutoEngineerPassModeStatus      = "aiPassModeStatus";
```

The persistence is a `readonly struct` over `KeyValueObject`. It's accessed via `internal ref AutoEngineerPersistence Persistence => ref _persistence;` (planner line 234) — code reads/writes through the ref, not a copy.

### Per-key write timing

| Key | Written by | When |
|---|---|---|
| `aiOrders` | `HandleCommand` (line 991), `SetDirection` (line 1849), `IsWaypointSatisfied` arrival (line 1196) | Player intent + planner-internal direction switches |
| `aiCtxOrders` | `UpdateTargets` (line 711) | **Every plan tick.** The full list of currently-active `ContextualOrder`s. |
| `aiManualStopDistance` | `Messenger<WorldWillSave>` handler (line 275) | **ONLY on save.** The live `_manualStopDistance` field is NOT mirrored to the KVO every tick — it only goes to disk when the world is saved. |
| `aiPlannerStatus` | `SetPlannerStatus` (line 786) | Every plan tick (set to "Charging brake line" if `_engineer.WaitingForBrakes`). |
| `aiPassModeStatus` | `AutoEngineerPassengerStopper.UpdateFor` (called from `UpdateTargets` line 658/662) | Every plan tick when stopper exists; nulled when stopper destroyed (line 678). |

### `_manualStopDistance` save/load split — IMPORTANT

The planner has **two parallel pieces of state** for the manual stop distance:

1. **`_manualStopDistance`** (private field, line 169) — the live ground truth used in plan generation, decremented every `ApplyMovement` tick (line 1066).
2. **`_persistence.ManualStopDistance`** — the KVO mirror, written **only** on save and read on load.

A `WorldWillSave` Messenger handler (line 275) flushes the field to the KVO right before disk-write:

```csharp
// AutoEngineerPlanner.cs:275
Messenger.Default.Register<WorldWillSave>(this, delegate
{
    _persistence.ManualStopDistance = _manualStopDistance;
});
```

On load, the manual-stop-observer (line 266) sets `_manualStopDistance = dist` from the KVO. **There's no continuous KVO traffic for this field — only the save-time snapshot.** Mods that observe `aiManualStopDistance` will see only the persisted value at save boundaries, NOT the live countdown. To get the live value, query `_manualStopDistance` via reflection (it's private).

This is by design: at AI tick rates, broadcasting a continuously-decreasing distance every 0.5 s would be noise. See [`save-load.md`](save-load.md) for the broader `WorldWillSave` cadence.

### KVO MP authority — the asymmetry

Round 1 documented this; restating because it matters for any mod sending to the planner:

The five `ai*` keys are NOT in `Car.HostPrefixes` (`Car.cs:467` — `["_", "ops.passengerMarker", "owned", "oiled", "hotbox"]`). They follow the default Crew-ish auth in `Car.AuthorizationRequirementForPropertyWrite`. **A crew-authorized client could theoretically write `aiOrders` directly via a generic property-change message and bypass `HandleCommand`.** Don't do this — `HandleCommand`'s side-effects (manual-stop reset, `_routeRequester` capture, `_lastSignalSpeedRestriction = null`, mode-specific Distance handling) are skipped. Always send `AutoEngineerCommand`.

The asymmetry:
- The `AutoEngineerCommand` *message* requires `Crew` access.
- The KVO writes it triggers (host-side) propagate to all clients (no `_` prefix).
- A non-crew client can READ `aiOrders` but cannot WRITE the message.
- A crew client COULD bypass the message via direct KVO write (foot-gun).

---

## Targets — what the planner produces

```csharp
// AutoEngineer.cs:23
public class Targets {
    public readonly float MaxSpeedMph;                  // signed; sign = direction
    public readonly List<Target> AllTargets;            // sorted by Distance ascending
    public readonly float AverageGradeUnder;            // % grade under the consist
    public readonly float AverageGradeAhead;            // % grade ahead, in lookahead band
    public readonly bool ChangeDirection;               // true → engineer treats as v_target=0 + brake
    public readonly AutoEngineerMode Mode;              // copy of orders mode at plan time
    public readonly StopAnnounce? StopAnnounce;         // null if no nearby stop reason
    public readonly CTCSignal NextSignal;               // for "Holding at <signalname>" messages

    public struct Target {
        public float SpeedMph;       // signed (relative to plan direction)
        public float Distance;       // metres ahead; counted down by ApplyMovement
        public string Reason;        // human-readable; "Track Speed", "Couple", "Fusee", etc.
    }
}
```

### Target distance semantics

- `Distance` is **distance from the front of the lead car**, not from the loco's body. `StartLocation()` (`AutoEngineerPlanner.cs:820`) returns the `LocationA`/`LocationB` of `_coupledCarsCached[0]` (the front car in the consist) — this is the search origin and the distance reference.
- `Distance` is decremented every tick by `AutoEngineer.ApplyMovement(MovementInfo info)` at `AutoEngineer.cs:1072`:
  ```csharp
  for (int i = 0; i < _targets.AllTargets.Count; i++) {
      Targets.Target value = _targets.AllTargets[i];
      value.Distance -= info.Distance;       // info.Distance is unsigned metres
      _targets.AllTargets[i] = value;
  }
  ```
  **Targets do not get negative distances pruned.** A passed stop target sits with negative Distance until `UpdateTargets` next runs. `EmergencyStop` (line 432) triggers when `Distance < -3f` AND the target has a `StopAnnounce` AND speed is zero — the engineer hard-teleports velocity to 0.
- `_lastSignalSpeedRestriction` is also distance-decremented in the same `ApplyMovement`, with auto-eviction when `DistanceLimit < 0` (line 1077).

### Targets equality and log noise

`SetTargets` calls `targets.Equals(_targets)` (`AutoEngineer.cs:331`). `Targets.Equals` checks `MaxSpeedMph`, both grade fields, `ChangeDirection`, `Mode`, `StopAnnounce`, and `AllTargets.SequenceEqual`. `Target.Equals` only checks `SpeedMph` and `Distance` — **not `Reason`**. Reason changes don't trigger log emit. This is intentional (avoids spam from "Track Speed" → "Track Speed: 35 mph" relabels).

---

## Holds — every condition that stops motion

In priority order (a hit at any earlier hold preempts the plan-generation step entirely).

### Hold 1: `_locomotive.set == null || !_orders.Enabled`

Loop line 376. Drops to `OffDuty()`, sleeps 0.5 s. `set` is the `IntegrationSet` — null means the consist is mid-reform (`UpdateSets` is rebuilding).

### Hold 2: `_derailed`

Loop line 386. Set in `UpdateCars` (line 869) by `_coupledCarsCached.Any(car => car.IsDerailed)`. On first detection: `Say("We're on the ground!")`. The planner emits empty `Targets`, calls `_persistence.ClearOrders()` (i.e. nulls out `aiOrders` → planner observer sees `Mode=Off` next tick → AI shuts itself off). **AI cannot be re-enabled until the consist is fully rerailed.**

### Hold 3: `delta > 0 && _orders.Mode != AutoEngineerMode.Road`

Loop line 401. `delta = current cars - previous cars`. **Picking up a car in non-Road mode triggers `_manualStopDistance = 0f`.** This is why a Yard-mode shove that picks up an extra car halts immediately. Waypoint mode also triggers; the route ticker recomputes and resumes if appropriate.

### Hold 4: `OrdersWantMovement() && ShouldStopForPitfall(out var reason)`

Loop line 407. `OrdersWantMovement` (line 1116):

```csharp
private bool OrdersWantMovement() => _orders.Mode switch {
    AutoEngineerMode.Off       => false,
    AutoEngineerMode.Road      => _orders.MaxSpeedMph > 0,
    AutoEngineerMode.Yard      => _manualStopDistance > 0f,
    AutoEngineerMode.Waypoint  => _orders.Waypoint.HasValue,
    _ => false,
};
```

`ShouldStopForPitfall` (line 920) — this is the explicit "safety check before allowing the engineer to drive":

| Pitfall | Reason text | Notes |
|---|---|---|
| `IsYardMode && _manualStopDistance <= 0` (early-out NOT a pitfall) | — | Yard mode at zero distance is a normal state (just-arrived); not flagged. |
| `_engineer.HandbrakeApplied` | `"N handbrake(s) applied"` | Counts via `c.air.handbrakeApplied`. |
| `!IsYardMode && !_engineer.BrakeLineTogether()` | `"Check the brake line"` | Yard mode skips this check. **Yards can shove with a broken air line.** |
| `!_engineer.BrakesReleasedOnNonAirConnectedCars()` | `"Brake applied beyond brake line"` | Catches partial air with brakes still set on cars beyond the air-line break. |

`PostPitfallNotice` writes `_locomotive.PostNotice("ai-pitfall", message)` for HUD display. Empty targets emit; loop sleeps 2 s before recheck.

### Hold 5: `!_locomotive.HasFuel`

Loop line 416. Steam: `"Check the tender, we're empty."`; diesel: `"All out of fuel."` Calls `_persistence.ClearOrders()` — AI shuts down. NOTE: `HasFuel` is consulted only on ticks; not in the engineer's PID inner loop.

### Hold 6: `WantsChangeDirection` (sign mismatch)

Loop line 428. `if (sign(orders) != sign(velocity) && |v| > 0.5f)` — rolling the wrong way. Emits a `Targets` with `ChangeDirection=true`, sleeps 2 s. The engineer treats `ChangeDirection=true` as zero target velocity (`ContextualTargetVelocity` returns `0f` when `WantsChangeDirection`). Brakes hard until reversed.

### Hold 7 (engineer-side): `IsStoppedAndShouldStay`

`AutoEngineer.cs:259` — composed:
```csharp
if (!IsStopped) return false;
if (IsZero(ContextualTargetVelocity())) return true;       // PID target is 0
if (IsZero(TargetSpeedMph)) return Mathf.Abs(TargetDistance) < 5f; // close to stop tgt
return false;
```

So if the engineer is stopped and EITHER the velocity it should be at is zero, OR it's within 5 m of a zero-speed target, it stays stopped. State `Stopped`: `Throttle=0`, `Reverser=0`, `LocomotiveBrake=1`, `TrainBrake=0` (if grade < 0.2%) or set to ≥10 psi.

### Hold 8: `WaitingForBrakes` substate (during Starting)

`StartMovement` (`AutoEngineer.cs:521`). After `TrainBrake = 0`, the engineer sits in a tight loop: `while (AverageTrainBrakeCylinder() > 5f)`. While in this state, `WaitingForBrakes = true` (which `SetPlannerStatus` reads to override the status string to `"Charging brake line"` — line 803). After dropping to ≤5 psi, throttle and reverser are configured; then a second wait `while (AverageTrainBrakeCylinder() > 1f && LocomotiveVelocityMphAbs < 1f)`.

This isn't really a "plan hold" — it's the engineer waiting for the air to release. It IS visible in `aiPlannerStatus` and is the only condition that overrides the planner-set status string.

---

## `UpdateTargets` — exhaustive walk

The function is 290 lines (`AutoEngineerPlanner.cs:451-739`). Below is the full structural digest.

### Step 1: lookahead + mode resolution

```csharp
float num = _engineer.CalculateLookaheadDistance();   // see § Lookahead horizon
Location start = StartLocation();                     // front of consist
OtherCarHandling otherCarHandling = _orders.Mode switch {
    Off       => Avoid,
    Road      => Avoid,
    Yard      => CoupleTo(null),                      // couple to anything ahead
    Waypoint  => Waypoint has CoupleToCarId
                 ? CoupleTo(carId) : NoCouple,
};
SwitchAgainstHandling switchAgainstHandling =
    (_orders.Mode == Waypoint) ? FoulThrowableSwitches : StopBeforeFouling;
```

### Step 2: dual Search passes

```csharp
Search(_coupledCarsCached[0],     // headCar
       num2,                       // velocityAbs (m/s)
       start,                      // Location
       num,                        // lookaheadDistance
       SearchMode.Ahead,
       _equipmentMaximumTrackCurvature,
       otherCarHandling,
       switchAgainstHandling,
       _coupledCarsCached,
       out var result);            // Ahead pass

Search(coupledCarsCached[count-1], // tail car
       num2,
       start.Flipped(),            // back-facing
       _maximumLength,             // only consist-length far
       SearchMode.Self,
       _equipmentMaximumTrackCurvature,
       OtherCarHandling.Avoid,    // never couple via tail
       SwitchAgainstHandling.StopBeforeFouling,  // never throw via tail
       _coupledCarsCached,
       out var result2);           // Self pass — track speed under us
```

Self pass exists to enforce that the tail of the consist also obeys the curve speed limit it's currently sitting on (so e.g. a 50-mph posted segment with the back of the train still in a 25-mph curve gets clamped by the curve).

### Step 3: signal handling (lines 488–588)

For the next signal in the Ahead result:

```csharp
switch (item.LastShownAspect) {
    case Stop:
        if (item.IsIntermediate) {
            // intermediate Stop = "Stop and Proceed":
            // - if very close + already stopped, latch as 15 mph allowance
            // - otherwise plan to stop at signal-15
            if ((distance < 40f && IsStopped()) || _stopAndProceedSignalId == item.id) {
                _stopAndProceedSignalId = item.id;
                SetSignalSpeedRestriction(15, null, distance, item, distanceLimited: true);
            } else {
                AddTarget(0f, distance - 15f, "Stop and Proceed Signal");
            }
        } else {
            // absolute Stop — a hard stop, requires PassSignal contextual to bypass
            AddContextualOrder(PassSignal, item.id);
            SetSignalSpeedRestriction(0, null, distance, item);
        }
        break;
    case Clear:
    case DivergingClear:
        if (distance <= 200f)
            SetSignalSpeedRestriction(null, null, distance, item);  // clears restriction
        break;
    case Approach:
    case DivergingApproach:
        if (distance <= 200f) {
            float? next = DistanceToNextSignalAfter(valueOrDefault, 2000f);
            if (!next.HasValue)
                SetSignalSpeedRestriction(25, 25, distance, item, distanceLimited: true);
            else
                SetSignalSpeedRestriction(15, 25, distance + next.Value, item);
                                         // 15 at next, 25 approach
        }
        break;
    case Restricting:
        if (distance <= 200f)
            SetSignalSpeedRestriction(15, null, distance, item, distanceLimited: true);
        break;
}

// "Calling" signals (the AI says "Stop at MP X." / "Clear at MP X." aloud)
if (StateManager.Shared.Storage.AICallSignals != 0
    && distance < 100f
    && (!_manualStopDistance.HasValue || _manualStopDistance > distance))
    CallSignalIfNeeded(item, lastShownAspect);
```

Signals consume `CTCSignal.LastShownAspect` — see [`signals-dispatch.md`](signals-dispatch.md) for the aspect enum and dispatcher mechanics.

The `200f` threshold (e.g. `distance > 200f` early-out) means signals further than 200 m don't yet restrict speed — only signals visible "near" affect the plan. This pairs with the lookahead growing with speed: at 60 mph, lookahead is ~220 m, so the 200 m threshold lines up to "we're committing now."

The "Stop and Proceed" latch via `_stopAndProceedSignalId`: once you've stopped at an intermediate Stop, the AI remembers that signal id and grants 15 mph past it without re-stopping. Cleared by `WillMove` (line 1092). **Multiple intermediate stops in a row are NOT batched** — only the last latched signal is remembered.

### Step 4: contextual signal/flare ignore handling

```csharp
if (_contextualIgnoreSignalId == found?.Item.id) found = null;  // before signal logic
// ...
// after signal logic:
if (_contextualIgnoreSignalId != null && result.NextSignal?.Item.id != _contextualIgnoreSignalId)
    _contextualIgnoreSignalId = null;     // forget once out of sight
if (_contextualIgnoreFlareId != null && nextFlare?.Item != _contextualIgnoreFlareId)
    _contextualIgnoreFlareId = null;
```

The "Pass this signal" / "Pass this flare" contextual orders set these ignore IDs. They self-clear when the named signal/flare leaves the search horizon.

### Step 5: lastSignalSpeedRestriction → AddTarget conversion (line 568)

If a memorized restriction exists, emit `AddTarget(speed, distance, "Stop Signal" or "Signal")` and an optional `AddTarget(approach, 0f, "Approach Signal")`. Add a `ResumeSpeed` contextual order so the user can dismiss the restriction.

```csharp
if (lastSignalSpeedRestriction.HasValue) {
    var restriction = lastSignalSpeedRestriction.Value;
    if (restriction.SpeedMph == 0) {
        AddTarget(0, restriction.DistanceToSignal - 15f, "Stop Signal", StopAnnounce.StopSignal);
    } else {
        AddTarget(restriction.SpeedMph, restriction.DistanceToSignal, "Signal");
    }
    if (restriction.ApproachSpeedMph.HasValue) {
        AddTarget(restriction.ApproachSpeedMph.Value, 0f, "Approach Signal");  // distance=0 → applies now
    }
    if (!contextualOrders.Any(co => co.Order == PassSignal && co.Context == restriction.SignalId))
        AddContextualOrder(ResumeSpeed, restriction.SignalId);
}
```

### Step 6: flare handling (line 599)

```csharp
if (nextFlare.HasValue && _contextualIgnoreFlareId != flare.Item) {
    AddTarget(0f, flare.Distance - 5f, "Fusee", StopAnnounce.Fusee);
    AddContextualOrder(PassFlare, flare.Item);
}
```

Flares are hard stops at distance-5. No graduated approach.

### Step 7: max-speed selection (line 609)

```csharp
float num5 = Mathf.Abs(enabledMaxSpeedMph);
string maxSpeedReason = (num5 < 0.1f) ? "Orders: Stop" : "Orders";
if (_hotboxSpotter.HotboxSpotted && num5 > 15f) { num5 = 15f; maxSpeedReason = "Hotbox"; }
if (maxSpeedMph2  < num5)  { num5 = maxSpeedMph2;  maxSpeedReason = "Track Speed"; }  // self-search
if (maxSpeedMphNear < num5){ num5 = maxSpeedMphNear; maxSpeedReason = "Track Speed"; } // ahead-near
```

The order matters: hotbox preempts track speed. **`maxSpeedReason` is for status display only** — actual speed enforcement happens via the `Targets.AllTargets` list and `ContextualTargetVelocity`.

### Step 8: car-blocking targets (line 626)

```csharp
if (distanceLimiter == DistanceLimiter.Car && otherCarHandling.Couple && availableDistance < 50f) {
    AddTarget(3f, availableDistance - 5f, "Couple");   // Yard couple speed
}
if (availableDistance < num) {
    if (distanceLimiter == DistanceLimiter.Car) {
        // Match the speed of the car ahead instead of stopping cold.
        // Lerp from "match exactly" to "match + relative" based on relative velocity.
        float num6 = num2 * 2.23694f;                         // self mph
        float num7 = result.LimitingCarRelativeVelocity * 2.23694f;
        float targetSpeedMphAbs = Mathf.Clamp(
            Mathf.Lerp(0f, num6 + num7, Mathf.InverseLerp(-0.1f, 5f, num7))
              * Mathf.Lerp(0.8f, 1f, Mathf.InverseLerp(40f, 80f, availableDistance)),
            0f, num5);
        AddTarget(targetSpeedMphAbs, availableDistance - 1f, availableDistanceReason, stopAnnounce);
    } else {
        AddTarget(0f, availableDistance - 1f, availableDistanceReason, stopAnnounce);
    }
} else {
    // No nearer car — clamp to track-speed at the next-restriction position
    float targetDistance2 = (maxSpeedMph > 0.1f)
        ? Mathf.Max(0f, nextRestrictionDistance - 13f)
        : nextRestrictionDistance;
    AddTarget(maxSpeedMph, targetDistance2, "Track Speed");
}
```

The "match speed of car ahead" logic (lines 634-637) is subtle: if the car ahead is moving away from you (positive relative velocity), match its speed plus a bit; if approaching or static, target is 0. The `Mathf.Lerp(0.8f, 1f, ...)` factor gradually relaxes from 80% match at 40 m to 100% at 80 m — closer cars get a more conservative match.

### Step 9: passenger stops (line 649)

```csharp
if (_passengerStopper != null) {
    // bypass-timetable contextual self-clears when no longer relevant
    if (_contextualBypassTimetableStation != null
        && nextStop?.timetableCode != _contextualBypassTimetableStation
        && nextStopUnder?.timetableCode != _contextualBypassTimetableStation)
        _contextualBypassTimetableStation = null;

    if (IsYardMode)
        _passengerStopper.UpdateFor(null, null, stoppedDuration, _contextualBypassTimetableStation);
    else
        _passengerStopper.UpdateFor(result.NextPassengerStop, result2.NextPassengerStop,
                                    stoppedDuration, _contextualBypassTimetableStation);

    if (_passengerStopper.NextStopInfo.HasValue) {
        var (stopDistance, reason, bypassContext) = _passengerStopper.NextStopInfo.Value;
        AddTarget(0f, stopDistance, reason);
        if (bypassContext != null)
            AddContextualOrder(BypassTimetable, bypassContext);
    }
} else {
    Persistence.PassengerModeStatus = null;
}
```

Yard mode passes nulls — no passenger stops ever fire in yard mode.

### Step 10: manual stop distance (line 680)

```csharp
if (_manualStopDistance.HasValue) {
    float value = _manualStopDistance.Value;
    float carUnits = value / 12.192f;     // car-length conversion (40 ft = 12.192 m)
    string reason;
    if (_orders.Mode == AutoEngineerMode.Waypoint) {
        reason = (value > 1f) ? "Running to waypoint" : "At waypoint";
    } else {
        // Yard mode flavour text
        reason = (carUnits > 1f)
            ? ((carUnits >= 20f) ? "Clear 20+ cars" : ("Clear " + Mathf.FloorToInt(carUnits).Pluralize("car")))
            : ((carUnits < 0.1f) ? "That'll do!" : "Clear less than a car");
    }
    AddTarget(0f, value, reason);
}
```

The 12.192 m divisor is exactly 40 ft (a generic boxcar length) — Yard-mode UI shows "Clear N cars" not "Clear N metres."

### Step 11: sort + StopAnnounce reduction + emit (line 696)

```csharp
List<TargetInfo> list = targetInfos.OrderBy(ti => ti.Target.Distance).ToList();
List<Target> list2 = list.Select(t => t.Target).ToList();
foreach (TargetInfo item3 in list) {
    if (Mathf.Abs(item3.Target.SpeedMph) <= 0.001f) {     // first stop target
        if (item3.Target.Distance < num3)                  // nearer than search-end
            stopAnnounce = item3.StopAnnounce;
        break;
    }
}
_engineer.SetTargets(new Targets(direction * num5, list2, averageGradeUnder, averageGrade,
                                 changeDirection: false, _orders.Mode, stopAnnounce, found?.Item));
SetPlannerStatus(num5, maxSpeedReason, list2);
Persistence.ContextualOrders = contextualOrders;
```

The first zero-speed target's `StopAnnounce` becomes the `Targets.StopAnnounce`. This is what the engineer announces ("Holding at a red board.") when state transitions to Stopped. **Only the nearest stop target's announce wins** — multiple stop reasons in the same plan show only the closest.

### Step 12: crossing distance (line 720)

```csharp
float num9 = availableDistance;
foreach (Target item4 in list2)
    if (Mathf.Abs(item4.SpeedMph) < 0.1f && item4.Distance < availableDistance)
        num9 = item4.Distance;             // shrink to nearest stop

if (nextCrossingDistance.HasValue && nextCrossingDistance.Value > num9)
    nextCrossingDistance = null;           // crossing is past our stop — don't whistle

_crossingSignaler.SetNextCrossingDistance(nextCrossingDistance);
```

Crossings PAST a stop target are suppressed. You won't get a crossing whistle for a crossing on the other side of a red signal you'll be sitting at.

---

## Lookahead horizon

```csharp
// AutoEngineer.cs:723
internal float CalculateLookaheadDistance() {
    float velocityMphAbs = Locomotive.VelocityMphAbs;
    float a = _config.maxVelocityForDistanceLight.FindTimeForValue(velocityMphAbs, 0.1f);
    float b = _config.maxVelocityForDistanceHeavy.FindTimeForValue(velocityMphAbs, 0.1f);
    float weightParameter = WeightParameter;
    return Mathf.Lerp(a, b, weightParameter) + 100f;
}
```

The lookahead is **"how far we need to start braking now to reach 0 mph at this curve, plus 100 m"**, weight-blended.

Defaults:
- `maxVelocityForDistanceLight = AnimationCurve.Linear(0, 0, 200, 100)` — at 200 m, you can be doing 100 mph; at 0 m you must be at 0. Inverse: at 100 mph you need 200 m to stop.
- `maxVelocityForDistanceHeavy` — same default. **Both curves default to the same shape** — only by ScriptableObject override do they differ.
- `WeightParameter = InverseLerp(weightTonsLight=500, weightTonsHeavy=1000, weight)` — 500 tons → light curve, 1000 tons → heavy curve, lerp between.

`FindTimeForValue` is the inverse of `Evaluate` — given a speed, find the distance. (Defined as an extension somewhere; standard "scan curve linearly" implementation.)

**Surprise**: the +100 m buffer is constant. A 5 mph train looks ahead ~110 m; a 50 mph train looks ahead ~200 m; a 100 mph train (impossible in vanilla but mod-relevant) looks ahead ~300 m. Faster does not mean proportionally further — the curves cap.

The horizon is consumed in two places:
1. `UpdateTargets` (line 453) — the budget for the forward `Search`.
2. The planner cadence in `Loop` (line 445): `mph > 30 → 0.5s`, `mph > 0.1 → 1s`, else `3s`. **Cadence and horizon are decoupled** — both depend on speed but separately.

### What "horizon" doesn't include

- Manual stop distance is separate. The planner adds a stop target at `_manualStopDistance` regardless of horizon.
- The `Self` search is separate, bounded by `_maximumLength`.
- Route lookahead (in `TickRoute`) is bounded by 1609.344 m (1 mile) per tick, not by `CalculateLookaheadDistance`.

---

## `Search` — the lookahead engine in detail

`AutoEngineerPlanner.cs:1966`. Static method (so it doesn't capture per-instance state). Walks the graph from `start` for `lookaheadDistance` meters in 10 m steps.

### Step loop

```csharp
while (num > 0f) {                    // num = remaining lookahead budget
    num -= 10f;
    try {
        Location location = cursor;   // remember pre-step position
        cursor = graph.LocationByMoving(cursor, 10f, !flag2);
        // flag2 = "skip switch-against check on this step" — set after CheckThrowable success
        flag2 = false;

        // CheckForCarAtLocation — only in Ahead mode and only until first car found
        if (mode == SearchMode.Ahead && car == null) {
            Car car2 = shared.CheckForCarAtLocation(cursor);
            if (car2 != null && !coupledCars.Contains(car2)) {
                car = car2;
                limitingCarRelativeVelocity = shared.RelativeVelocity(headCar, car);
                float distanceToCar = DistanceToCar(location, car2, graph);
                num3 = result.AvailableDistance + distanceToCar;
                // Behavior switch: Couple / NoCouple / Avoid
                // Couple + same route: num3 += 2 (target 2 m past the car)
                // Couple + different route: num3 -= 10, flag SwitchFouled
                // NoCouple/Avoid + StoppingDistanceIfMovingToward: num3 = stopping distance - 1 or 20
                //   (− 1 for NoCouple, − 20 for Avoid)
            }
        }

        result.AvailableDistance += 10f;
        float num6 = MaxSpeedForTrackMph(cursor);
        if (num6 < result.MaxSpeedMph) {
            result.MaxSpeedMph = num6;
            result.NextRestrictionDistance = Mathf.Min(result.NextRestrictionDistance,
                                                       result.AvailableDistance);
        }
        if (result.AvailableDistance < num2 && num6 < result.MaxSpeedMphNear)
            result.MaxSpeedMphNear = num6;          // num2 = velocityAbs * 5f (≈5 sec ahead)

        result.AverageGrade += graph.GradeAtLocation(cursor);

    } catch (SwitchAgainstMovement switchAgainstMovement) {
        StopAnnounce stopAnnounce = StopAnnounce.SwitchAgainst;
        if (switchAgainstHandling == FoulThrowableSwitches
            && CheckThrowable(switchAgainstMovement.Node, shared, coupledCars, out stopAnnounce)) {
            flag2 = true;
            num += 10f;        // refund the budget
            continue;          // continue past the switch
        }
        // else truncate at fouling distance
        float foulDistance = graph.CalculateFoulingDistance(switchAgainstMovement.Node);
        float toSwitch = Vector3.Distance(graph.GetPosition(cursor), switchAgainstMovement.Node.transform.GamePosition());
        result.AvailableDistance = result.AvailableDistance + toSwitch - foulDistance;
        result.AvailableDistanceReason = stopAnnounce switch {
            SwitchFouled       => "Switch Fouled",
            CTCSwitchLocked    => "CTC Switch Locked",
            _                  => "Switch Against",
        };
        result.StopAnnounce = stopAnnounce;
        break;
    } catch (EndOfTrack) {
        Location b = graph.LocationByMoving(cursor, 10f, true, stopAtEndOfTrack: true);
        result.AvailableDistance += graph.GetDistanceBetweenClose(cursor, b);
        result.AvailableDistanceReason = "End of Track";
        break;
    }
}
```

### Post-step: car overrides distance

```csharp
if (car != null && num3 < result.AvailableDistance) {
    result.AvailableDistance = num3;
    result.DistanceLimiter = DistanceLimiter.Car;
    result.StopAnnounce = (flag ? StopAnnounce.SwitchFouled : StopAnnounce.OtherTrain);
    result.LimitingCarRelativeVelocity = limitingCarRelativeVelocity;
    result.AvailableDistanceReason = (flag ? "<car> Fouling Switch" : "Approaching <car>");
}
```

`flag` is set when the limiting car is on a different route (NoCouple/Couple cases). Otherwise it's a same-route car blocking us.

### Marker scan post-walk

After distance is set, two marker enumerations sweep the segment:

1. **Same-direction, Ahead-mode only**: `CTCSignal` — picks first signal in our direction and stores in `result.NextSignal`.
2. **Both-direction**: `Flare` (via `FlareManager.TryGetFlarePickable`), `Crossing`, `PassengerStop`. Picks first of each kind.

**Note** that flares and passenger stops are picked up `sameDirection: false` — the AI sees stops on adjacent tracks too. This is why `AutoEngineerPassengerStopper.UpdateFor` is passed BOTH `result.NextPassengerStop` AND `result2.NextPassengerStop` (the self-search) — so it can pick the one actually relevant.

### `MaxSpeedForTrackMph` (local function, line 2181)

```csharp
float MaxSpeedForTrackMph(Location location2) {
    float num9 = TrainMath.MaximumSpeedMphForCurve(
        graph.CurvatureAtLocation(location2, Graph.CurveQueryResolution.Segment),
        equipmentMaximumTrackCurvature);
    num9 = Mathf.Max(5f, RoundDown(Mathf.Max(num9 - 3f, num9 * 0.8f)));
    int posted = location2.segment.speedLimit;
    if (posted == 0) posted = 35;        // unposted segment defaults to 35 mph
    return Mathf.Min(num9, posted);
}
static float RoundDown(float num9, int nearest = 5) =>
    Mathf.FloorToInt(num9 / nearest) * nearest;
```

**Curve speed math**: equipment-curve max minus 3 mph OR times 0.8, take the SMALLER (more conservative), then round down to nearest 5 mph, then clamp to ≥5 mph. Then min with posted limit (35 default).

**`equipmentMaximumTrackCurvature`** is computed by `UpdateCars` (line 867):
```csharp
_equipmentMaximumTrackCurvature = _coupledCarsCached.Min(car => car.MaximumTrackCurvature);
```
The strictest car in the consist sets the curve speed for the whole train. A reefer with a low max curvature pinned next to a flatcar drops the train's curve speed cap.

### `CheckThrowable` static (line 2206)

```csharp
private static bool CheckThrowable(TrackNode node, TrainController trainController,
                                   ICollection<Car> coupledCars, out StopAnnounce stopAnnounce) {
    if (!trainController.CanSetSwitch(node, !node.isThrown, out var foundCar)
        && !coupledCars.Contains(foundCar)) {
        stopAnnounce = StopAnnounce.SwitchFouled;
        return false;
    }
    if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked) {
        stopAnnounce = StopAnnounce.CTCSwitchLocked;
        return false;
    }
    stopAnnounce = StopAnnounce.SwitchAgainst;
    return true;
}
```

A locked CTC switch is reported as `CTCSwitchLocked` (specific message); a fouled switch as `SwitchFouled`; any other against-state as `SwitchAgainst`. **Note** the `coupledCars.Contains(foundCar)` clause — if our own train is "fouling" the switch (we're on the switch frog), we're allowed to throw it past ourselves. Not actually meaningful in practice (you'd need to be on the points), but it's there.

### Search returns: `SearchResult` struct fields

```csharp
public float AvailableDistance;            // metres ahead before stop
public string AvailableDistanceReason;     // human-readable
public float MaxSpeedMph;                  // strictest curve+posted limit in walked segment
public float MaxSpeedMphNear;              // strictest within velocity*5 metres
public Found<CTCSignal>? NextSignal;       // (ahead mode only)
public Found<string>? NextFlare;           // FlareId
public float? NextCrossingDistance;
public Found<PassengerStop>? NextPassengerStop;
public float NextRestrictionDistance;      // where MaxSpeedMph was first encountered
public float AverageGrade;                 // % grade, averaged over segment
public DistanceLimiter DistanceLimiter;    // Other (= curve/end/switch) or Car
public StopAnnounce? StopAnnounce;         // null if no stop reason
public float LimitingCarRelativeVelocity;  // m/s, signed (positive = car ahead is moving away)
```

---

## ContextualTargetVelocity — the dual-model speed picker

This is the single most important method in the engineer. Round-1 noted it; here's the full kinematic detail.

```csharp
// AutoEngineer.cs:451
internal float ContextualTargetVelocity() {
    if (WantsChangeDirection || _config == null) return 0f;
    float totalAvailableBraking = CalculateTotalAvailableBraking();   // sum N
    float num = Mathf.Abs(_targets.MaxSpeedMph * 0.44703928f);        // base = orders mph→mps
    float weightParameter = WeightParameter;                          // 0..1 light→heavy

    foreach (Targets.Target allTarget in _targets.AllTargets) {
        float value = Mathf.Abs(allTarget.SpeedMph);                  // mph
        float num2  = Mathf.Abs(allTarget.SpeedMph * 0.44703928f);    // mps
        if (num2 < num) {
            float num5;
            if (allTarget.Distance > 0.1f) {
                // Configured curve model (FindTimeForValue inverts curve to find offset)
                float num3 = _config.maxVelocityForDistanceLight.FindTimeForValue(value, 0.1f);
                float num4 = _config.maxVelocityForDistanceHeavy.FindTimeForValue(value, 0.1f);
                float a = _config.maxVelocityForDistanceLight.Evaluate(allTarget.Distance + num3);
                float b = _config.maxVelocityForDistanceHeavy.Evaluate(allTarget.Distance + num4);
                float tableMps = Mathf.Lerp(a, b, weightParameter) * 0.44703928f;

                // Physics model: v(0) = sqrt(v_final² + 2·a·d)
                float physMps = CalculateMaxVelocityToSlowToSpeedAtDistance(
                                   num2, allTarget.Distance, totalAvailableBraking);

                num5 = Mathf.Min(physMps, tableMps);
            } else {
                // Distance ≤ 0.1 m — apply target speed immediately
                num5 = num2;
            }
            if (num5 < num) num = num5;
        }
    }
    return Mathf.Sign(_targets.MaxSpeedMph) * num;
}
```

### Two parallel models — and the min wins

Model 1 — **Configured curve**. The `maxVelocityForDistance{Light,Heavy}` curves describe "max safe velocity at distance d from a stop." `FindTimeForValue(value, 0.1f)` finds the distance-offset where the curve reaches the target speed (so the curve is interpreted as "from this offset onward, the curve gives us 0 at 0 distance"). Lerp by weight.

Model 2 — **Physics derived**. `CalculateMaxVelocityToSlowToSpeedAtDistance`:
```csharp
float num = CachedCoupledWeightLbs() * 0.4536f;       // mass kg (lbs * 0.4536)
float num2 = totalAvailableBraking / num;             // deceleration m/s²
return Mathf.Sqrt(Mathf.Pow(velocityFinalMpsAbs, 2f) + 2f * num2 * distance);
```

Classic kinematic: v(0) = sqrt(v_f² + 2·a·d).

**`totalAvailableBraking`** sums `Car.CalculateBrakingForce(1f, |v|)` over `AirOpenCars()` — full-application brake force across all air-connected cars. This is **maximum-application** brake force; the actual brake application is being modulated by the PID. So the physics model is *optimistic*: it assumes you'll get max braking instantly. The PID compensates empirically.

**The min wins.** For typical heavy trains, the tabled model (`maxVelocityForDistance...`) is more conservative than the physics formula and dominates. For light engines on heavy grades (or low-friction conditions in a mod), the physics model can dominate.

### Why both?

The configured curves are ALMOST a hand-tuned profile of the kinematic formula but with a margin. They were almost certainly hand-tuned in playtesting against typical-train scenarios. Physics is the floor — even if you mis-tune the curves, the kinematic formula prevents grossly-overspeed approaches (in theory). In practice the kinematic model trusts `totalAvailableBraking` totally and doesn't model brake response time, so it's NOT actually a safety net for wildly-mistuned curves.

### `CalculateDistanceToSlowToSpeed` — the inverse

Used by `RouteStartLocation` to compute `trainMomentum`:

```csharp
public static float CalculateDistanceToSlowToSpeed(
    float initialVelocityMps, float finalVelocityMps,
    float totalAvailableBraking, float trainMass) {
    initialVelocityMps = Mathf.Max(initialVelocityMps, finalVelocityMps);
    float a = totalAvailableBraking / trainMass;
    float d = (Mathf.Pow(initialVelocityMps, 2f) - Mathf.Pow(finalVelocityMps, 2f)) / (2f * a);
    return Mathf.Max(0f, d);
}
```

Same physics, inverse direction. `trainMomentum = momentumFactor * stoppingDistance + momentumOffset` (defaults `2 * d + 50 m`) is the cost-buffer added to route search so the AI prefers routes it can decelerate into.

---

## PIDs — the three controllers

### Default PID config (template values from `PIDController` field initializers)

```csharp
[Range(-4f, 4f)] private float proportionalGain = 2f;
[Range(-4f, 4f)] public  float integralGain     = 0.5f;
[Range(-4f, 4f)] public  float derivativeGain   = 0.25f;
[Range(-2f, 2f)] public  float integralGrowth   = 1f;
[Range(0f, 2f)]  private float integralDecay    = 0f;          // (default = 0)
public FloatRange errorRange      = new(-100, 100);
public FloatRange integratorRange = new(-100, 100);
public FloatRange outputRange     = new(-100, 100);
[SerializeField] private float maximumStep = 100f;
```

These are templates. The actual gains used at runtime come from `_config.throttlePID`, `_config.independentPID`, `_config.trainBrakePID` — fields on the `AutoEngineerConfig` ScriptableObject. **The shipped values come from a baked Unity asset; the inline defaults above are NOT the live values.** Patch `AutoEngineerConfig`'s field-set directly (or the per-instance copy after `OnEnable`'s `CopyTo`) to retune.

### Per-instance copy (line 298)

```csharp
// AutoEngineer.cs:298
_config.throttlePID.CopyTo(throttleController);
_config.independentPID.CopyTo(independentController);
_config.trainBrakePID.CopyTo(trainBrakeController);
```

Each locomotive gets its own `PIDController` instance with its own integrator state. **`CopyTo` copies all fields including ranges and `maximumStep`, but NOT the integrator state** (that resets to 0 implicitly on construction).

### `Compute` algorithm

```csharp
public float Compute(float error, float dt) {
    if (dt == 0f) dt = 1f;
    error = error.Clamp(errorRange);
    _integrator *= 1f - integralDecay * dt;            // leaky integrator
    _integrator = (_integrator + integralGrowth * error * dt).Clamp(integratorRange);
    float p = proportionalGain * error;
    float d = derivativeGain * ((error - _previousError) / dt);
    float i = integralGain * _integrator;
    float result = (_previousControl = (p + i + d).Clamp(outputRange)
                                                  .ClampToMaximumStep(_previousControl, maximumStep));
    _previousError = error;
    _previousDerivative = d;
    _previousIntegrator = i;
    return result;
}
```

Notable:
- **`integralDecay`** is a leak (multiplies integrator by `(1 - decay·dt)` per tick). Default 0 — no leak.
- **`integralGrowth`** is a multiplier on the error-integration step. Lets you scale I-term independent of the integrator size.
- **`maximumStep`** is rate-limit on output change per tick. Limits how fast the output can swing.
- **`ClampToMaximumStep(_previousControl, maximumStep)`** is an extension; clamps `result` to be within `maximumStep` of the previous output.

### `ResetPIDs` (line 280)

```csharp
[ContextMenu("Reset PIDs")]
private void ResetPIDs() {
    throttleController.Reset();
    independentController.Reset();
    trainBrakeController.Reset();
}
```

Called at the top of `MaintainSpeed` (line 735). **Every state transition into Running clears PID integrators.** No long-term integrator memory across stops. The `integralDecay` is the only continuous-time damping if you want one.

### Which PID controls which output

Inside `MaintainSpeed` (`AutoEngineer.cs:732`):

```csharp
float throttle = throttleController.Compute(num7, num3);    // num7 = error (asymmetric)
_control.Throttle = throttle;
bool flag = !IsZero(_control.Throttle);
if (flag) _control.Reverser = num2 * CutOffSettingForVelocity(velocity);  // direction + cutoff

float num8 = num6;                                          // un-amplified error
if (num8 < 0f) num8 = Mathf.Sign(num8) * Mathf.Pow(num8, _config.brakeErrorPower);
                                                            // brakeErrorPower default 1
                                                            // raises negative error to a power

if (ShouldUseLocomotiveBrake()) {
    float locomotiveBrake = independentController.Compute(num8, num3);
    _control.LocomotiveBrake = locomotiveBrake;
    _control.TrainBrake = 0f;
} else {
    int count = AirOpenCars().Count;
    trainBrakeController.derivativeGain =
        _config.trainBrakeDerivativeGainForNumAirOpenCars.Evaluate(count);
                                                            // default Linear(10,-0.5,30,-1.2)
                                                            // more cars → MORE NEGATIVE D-gain
    float num9 = trainBrakeController.Compute(num8, num3);
    if (num9 > 0.01f && num9 > _control.TrainBrake)
        _control.TrainBrake = Mathf.Ceil(num9 * 30f) / 30f; // 1/30 quantize on apply
    else if (num9 < _config.trainBrakeReleaseBelowOutput)   // default -0.01
        _control.TrainBrake = 0f;                           // release fully
    if (flag && Locomotive.air.BrakeCylinder.Pressure > 0f
        && !Locomotive.locomotiveControl.air.IsCutOut)
        _control.BailOff();                                  // auto-bail when accelerating
}
```

#### Throttle PID details

- **Error input**: `num7 = num4 - num5` where `num4 = |target velocity (m/s)|`, `num5 = |velocity (m/s)| + paddingForSpeedMph.Evaluate(velMph) * 0.44704`. Padding curve default `Linear(5, 0, 20, 1)` — at 5 mph 0 padding, at 20+ mph 1 mph padding (converted to m/s via `* 0.44704`).
- **Asymmetric amplification**: if `num7 > 0` (need to accelerate), `num7 *= 10f`. **Strong accel response, gentle decel response.** This is why the AI feels "eager to go" but smooth on coast-down.
- **Output**: written directly to `_control.Throttle` (no quantization). The cutoff/reverser is set as a side-effect.
- **Special case**: if zero target AND `TargetDistance < 0` (the next stop target is BEHIND us), `num4 = -1f` → forces aggressive backing or emergency braking. This is the "we're past the stop" tag.

#### Independent (locomotive brake) PID details

- Used when **all** open-air cars are the locomotive itself OR have `air.DefersToLocomotiveAir = true`. I.e. light-engine moves and "trail unit" arrangements.
- Output written directly to `_control.LocomotiveBrake`. Train brake forced to 0 in this branch.
- See [`mu-dpu-coordination.md`](mu-dpu-coordination.md) for `DefersToLocomotiveAir` semantics.

#### Train brake PID details

- Used when any open-air car is NOT the loco AND not deferring.
- **Per-tick derivative gain auto-tune**: `trainBrakeDerivativeGainForNumAirOpenCars.Evaluate(carCount)`. Default `Linear(10, -0.5, 30, -1.2)` — at 10 cars `-0.5`, at 30 cars `-1.2`. **Negative D-gain** — the controller damps oscillations more aggressively for longer trains.
- **Apply quantization**: `Mathf.Ceil(num9 * 30f) / 30f` — 1/30-unit (actually 3.0 psi out of 90 psi range, so ~3.3% steps). Output is treated as fraction-of-full-application.
- **Release is binary**: when `output < trainBrakeReleaseBelowOutput` (default −0.01), **fully release** (`_control.TrainBrake = 0f`). No proportional release. This is the air-system reality: 26L locomotives can't actually release proportionally; the lap valve releases all-or-nothing.
- **Apply is asymmetric — only goes up at controller direction.** `if (num9 > 0.01f && num9 > _control.TrainBrake)` means the PID can REQUEST more brake but never less; only release moves it down.
- **Bail-off auto** (line 805): `if (throttle != 0 && BrakeCylinder.Pressure > 0 && !IsCutOut) BailOff()`. Auto-bail when accelerating against your own brake. Important for steam (otherwise the cylinder pressure fights the throttle).

### `brakeErrorPower` (line 783)

```csharp
float num8 = num6;
if (num8 < 0f) num8 = Mathf.Sign(num8) * Mathf.Pow(num8, _config.brakeErrorPower);
```

Defaults to 1 (no effect). Range 0..4. Higher values (2, 3) make the brake controller respond MORE aggressively to LARGE negative errors (overspeed scenarios) while staying gentle for small errors. A common modder retune.

### `trainBrakeReleaseBelowOutput` (default −0.01)

Range −0.2 .. 0.1. The threshold below which the controller fully releases the train brake. Setting to e.g. 0.05 makes the controller release earlier (more eager to release).

### Yield interval

```csharp
float num10 = 0.5f;
if (IsZero(num)) num10 = Mathf.Min(num10, MaxWaitForStopAtDistance(TargetDistance));
yield return WaitFixed(num10);
```

`MaxWaitForStopAtDistance` (line 872): `< 50 m → 0.5 s`, `< 100 m → 0.75 s`, `else → 1 s`. Wait, that's weird — the function returns LARGER values for larger distances. The `Mathf.Min(0.5f, ...)` clamp means the yield is always `0.5f` when the function returns `0.75/1.0`. Effectively the yield is always 0.5 s. **Possible legacy / dead clamp.** The function is still useful within itself (returns 0.5 at < 50 m, which IS the smaller value and would matter if the base wait increased).

---

## Holds — engineer-side state machine

Three states: `Stopped`, `Starting`, `Running`. State picked by `IsStoppedAndShouldStay` and `IsStopped + !IsZero(TargetSpeedMph)`.

### State.Stopped (line 399)

```csharp
case State.Stopped:
    if (Mathf.Abs(_targets.AverageGradeUnder) < 0.2f)
        _control.TrainBrake = 0f;       // flat ground — release train brake
    else
        TrainBrakeSetToAtLeast(10f);    // grade — hold ≥ 10 psi reduction
    _control.LocomotiveBrake = 1f;       // independent fully applied
    _control.Throttle = 0f;
    _control.Reverser = 0f;
    yield return WaitFixed(1f);
    break;
```

The `TrainBrakeSetToAtLeast(10f)` path keeps a service application going on a grade — so the consist doesn't drift. **Note the 0.2% grade threshold** — anything ≤ 0.2% is treated as flat. A modest 0.5% grade gets the train-brake hold.

### State.Stopped → Starting transition

When state changes from Stopped to Starting, `PostStopNotice(null)` clears the stop notice. No bell, no other transition behavior.

### State.Running → Stopped transition

When state changes from Running to Stopped:
- Cylinder cocks open: false
- LocomotiveBrake = 1
- Blow `BlowPattern.Stopped` (one short blast, ~0.4 s)
- If `_targets.StopAnnounce.HasValue` → `AnnounceStop(_targets.StopAnnounce.Value, _targets.NextSignal)` (Says one of `AnnounceStopSignal`/`AnnounceSwitchAgainst`/`AnnounceFusee`/`AnnounceOtherTrain`/`AnnounceSwitchFouled`/`"CTC switch is locked."`, posts notice)
- `HeadlightDim()` — switches headlight to dim

### State.Starting

Calls `StartMovement()` coroutine (line 521). Long sequence:
1. If `LocomotiveBrake != 0`: set to 1 and wait 1 s.
2. If `!WantsMovement`: bail.
3. `HeadlightOn(forward)`.
4. Set `_control.TrainBrake = 0f`.
5. **Wait for brake cylinders to drop**: `while (AverageTrainBrakeCylinder() > 5f)` — sets `WaitingForBrakes = true`, polls every 0.5 s.
6. `WaitingForBrakes = false`.
7. Start `StartingEffects()` coroutine (rings bell if Mode is Road, opens cylinder cocks for 15-20 s while moving > 0.1 mph).
8. Blow horn (Forward = 2 long, Reverse = 3 short).
9. Set reverser, throttle/independent for "holding" via `CalculateSettingsForHolding()`.
10. **Wait for cylinders to fully release**: `while (AverageTrainBrakeCylinder() > 1f && |v|mph < 1f)`.
11. Release independent.
12. **Until movement** (`while (LocomotiveVelocityMphAbs < 1f)`): set throttle from `CalculateThrottleForTargetVelocityStarting`, polls.

`CalculateSettingsForHolding` (line 606) computes throttle and independent that hold the train against gravity. Basically: `RoundUpToNotch(|gravity| / RatedTractiveEffort)` — find the throttle notch that produces enough TE to balance the grade. If gravity favours movement in the target direction, throttle = 0 and independent = 1.

`CalculateThrottleForTargetVelocityStarting` (line 626) uses `MaxTractiveEffortAtVelocity` summed over `CachedMuConnectedLocomotives` (so MU lash-ups get more throttle authority). Aggressively notches up if `target > current + 3 mph`.

### State.Running

Calls `MaintainSpeed()`. The PID loop already detailed.

---

## ApplyMovement consumer chain

Source: `BaseLocomotive.cs:219`

```csharp
protected override void FireOnMovement(MovementInfo info) {
    base.FireOnMovement(info);              // base Car.FireOnMovement — banks odometer, fires _movementListeners
    if (AutoEngineerPlanner != null)
        AutoEngineerPlanner.ApplyMovement(info);
}
```

`AutoEngineerPlanner.ApplyMovement` (line 1061):

```csharp
public void ApplyMovement(MovementInfo info) {
    _engineer.ApplyMovement(info);                          // (1) decrement Target.Distance
    if (_manualStopDistance.HasValue)
        _manualStopDistance -= info.Distance;               // (2) decrement live manual stop
    foreach (AutoEngineerComponentBase item in Components())
        item.ApplyMovement(info);                            // (3) crossings + passenger stopper
    if (_lastSignalSpeedRestriction.HasValue) {
        SignalSpeedRestriction value = _lastSignalSpeedRestriction.Value;
        value.DistanceToSignal -= info.Distance;
        value.DistanceLimit -= info.Distance;
        _lastSignalSpeedRestriction =
            (value.DistanceLimit < 0f) ? null : value;       // (4) auto-evict
        if (!_lastSignalSpeedRestriction.HasValue)
            _log.Information("Cleared signal speed restriction.");
    }
    _underTrainPoints.Clear();                               // (5) invalidate IsUnderTrain cache
}
```

**Important: `info.Distance` is unsigned (always positive metres moved).** See [`odometer-movement.md`](odometer-movement.md). The planner subtracts the absolute distance — so reverse moves also decrement. This is correct because plan distances are "distance ahead" and any movement closes that distance.

**`_underTrainPoints.Clear()`** invalidates the cache used by `IsUnderTrain`. Forces recomputation next time the route loop checks waypoint satisfaction.

`Components()` enumerates `_passengerStopper` and `_crossingSignaler` (both extend `AutoEngineerComponentBase` — see file). The `AutoEngineerFuelAlerter` and `AutoHotboxSpotter` do NOT extend it and do NOT receive movement info; they're poll-loop driven instead.

### `AutoEngineerComponentBase.ApplyMovement` consumers

| Subsystem | What it does in ApplyMovement |
|---|---|
| `AutoEngineerPassengerStopper` | Decrements per-stop distances; updates internal stop-distance tracking. |
| `AutoEngineerCrossingSignaler` | Decrements crossing distance; triggers pattern start when within timing budget. |

`AutoEngineerComponentBase.WillMove()` is also part of the contract — called from `AutoEngineerPlanner.WillMove` (which is called by `BaseLocomotive` line 102 — the integration set's "consist about to move" hook).

### Per-tick overhead

`ApplyMovement` runs every physics tick (FixedUpdate cadence, not the planner Loop cadence). On the host, this is several times per second per locomotive. The work is small: O(targets) decrement + 2 component dispatches + a struct mutation. Modders adding heavy work to this hook will see physics-tick-rate cost.

---

## Cached state — what gets invalidated when

`AutoEngineer` caches several lists/values for tick-loop performance. Invalidation is explicit.

### `_cachedCoupled`, `_cachedCoupledWeightLbs`, `_cachedAirOpen`, `_cachedLocomotives`

Cleared by `InvalidateCachedCars()` (line 715). Called at:
- Top of `Loop`-iter inner `MaintainSpeed` (line 744) — every PID tick.
- Top of `StartMovement` (line 523).
- The planner doesn't directly call this — it reaches in via `_engineer.InvalidateCachedCars()` at `Loop` line 406.

### `_coupledCarsCached` (planner-side)

Set by `UpdateCars` (line 859). `delta` is the count change since last `UpdateCars`. Called every `Loop` iter (line 383). Also rebuilds `_maximumLength` and `_equipmentMaximumTrackCurvature`.

### `_underTrainPoints`

Cleared by `ApplyMovement` (line 1083). Recomputed by `IsUnderTrain` (line 1899) via `_graph.FindPoints(...)` for the current consist extent.

### `_lastSignalSpeedRestriction`

Lifecycle:
- Set by `SetSignalSpeedRestriction` (line 780, called from `UpdateTargets`).
- Distance-decremented by `ApplyMovement` (line 1072).
- Auto-evicted when `DistanceLimit < 0` (line 1077). For `distanceLimited: false` restrictions, `DistanceLimit = float.PositiveInfinity` — never auto-evicts.
- Cleared by `WillMove` (line 1088), `OffDuty` doesn't clear, `HandleCommand` clears on every new command (line 992), `HandleContextualOrder(ResumeSpeed)` clears (line 1041).

### `_calledSignal` / `_stopAndProceedSignalId`

Cleared by `WillMove` (line 1091/1092). `_calledSignal` also self-clears in `CallSignalIfNeeded` when the signal id changes or the aspect transitions to Clear/DivergingClear from no-call state.

---

## Switching behavior

### Yard mode auto-stop on couple-pickup

Already documented above (Hold 3). When a car is picked up in non-Road mode, `_manualStopDistance = 0` → AI stops immediately.

### Yard mode manual move

Yard mode requires explicit `Distance` in the `AutoEngineerCommand`. `HandleCommand` (line 999):
```csharp
case AutoEngineerMode.Yard:
    if (command.Distance.HasValue) SetManualStopDistance(command.Distance.Value);
    else if (orders.Mode != AutoEngineerMode.Yard) SetManualStopDistance(0f);
    // (entering Yard from another mode with no Distance → stop immediately)
    break;
```

So:
- Yard command with Distance: set stop distance; AI moves up to that.
- Yard-from-non-Yard command without Distance: distance = 0; AI sits still.
- Yard-from-Yard command without Distance: distance unchanged; AI keeps prior intent.

The UI helper (`AutoEngineerOrdersHelper.SetOrdersValue`) always supplies a Distance when issuing Yard commands. Direct mod senders should mirror this.

### Waypoint mode switch throwing

`TickRoute` (line 1174) — see round 1 for the full algorithm. Notable details:

```csharp
// CTC switch unlock check (line 1671)
if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked)
    return SetSwitchResult.CTC;     // silently skip

// 150m proximity gate (line 1238)
if (Mathf.Min(Vector3.Distance(positionF, position),
              Vector3.Distance(positionR, position)) <= 150f)
    setSwitchResult = TrySetSwitch(switchNode, thrown);
```

A switch is only thrown when within 150 m of EITHER end of the consist. This avoids "throwing" a switch 1 km ahead before another train passes through.

CTC switches that are LOCKED are silently skipped (`SetSwitchResult.CTC` is non-error; the loop continues). The AI hits the switch, the planner's `Search` truncates at it, and the AI stops with a `CTCSwitchLocked` announce. There's no automatic "wait for dispatcher" behavior.

### `CheckForFoulingPointLimitingSwitch` (line 1571)

When a route includes a back-up move, the route ticker walks 75 m past the turn-back point and checks for a fouling switch. If found and throwable, throws it. This handles the "running around a train" scenario where the switch beyond the runaround needs to be lined for the back-up. Recorded in `_routeExtraSwitches` so `RestorePassedSwitchesToOriginalPosition` knows to restore them.

### `SetDirection` (line 1849)

Called by `TickRoute` (line 1299) and `SetInitialDirection` (line 1485). Writes to `_persistence.Orders` to flip the `Forward` flag. **This triggers `OrdersDidChange` on the next observer fire** — but the observer runs in `OnEnable` / `Loop` separately and the planner uses `_orders.Forward` as set, not the KVO. So a direction flip is safe from race.

---

## Init order — pitfalls

### `AutoEngineerPlanner.Awake` (line 253)

```csharp
private void Awake() {
    _graph = Graph.Shared;
    _timetableController = TimetableController.Shared;
    _engineer = base.gameObject.AddComponent<AutoEngineer>();
    _locomotive = _engineer.Locomotive;
    _log = Log.ForContext<AutoEngineerPlanner>().ForContext("locomotive", _locomotive.DisplayName);
}
```

**Order issue**: `_locomotive = _engineer.Locomotive` — but `AutoEngineer.OnEnable` is what assigns `Locomotive = GetComponent<BaseLocomotive>()`. Unity Awake → OnEnable order: `Awake` of all components first, then `OnEnable` of all components. So when planner's `Awake` runs, `_engineer.Locomotive` is null... unless Unity processes `AutoEngineer.Awake/OnEnable` before continuing planner's `Awake`.

In practice: `AddComponent<AutoEngineer>()` calls `AutoEngineer.Awake` immediately (synchronously) on the new instance. `Awake` doesn't set `Locomotive`. Then `OnEnable` runs (also immediately, before AddComponent returns). `OnEnable` sets `Locomotive`. So when control returns to planner's `Awake`, `_engineer.Locomotive` IS set.

**This works because `AutoEngineer.OnEnable` runs synchronously inside `AddComponent`.** A modder who patches `OnEnable` to defer this is breaking the planner.

### `AutoEngineerPlanner.OnEnable` (line 262)

```csharp
private void OnEnable() {
    _config = TrainController.Shared.autoEngineerConfig;
    _persistence = new AutoEngineerPersistence(_locomotive.KeyValueObject);
    _manualStopObserver = _persistence.ObserveManualStopDistance(...);
    _ordersObserver = _persistence.ObserveOrders(...);            // callInitial=true
    Messenger.Default.Register<WorldWillSave>(this, ...);
    Messenger.Default.Register<TimetableDidChange>(this, ...);
}
```

The orders observer is registered with `callInitial=true`, so it fires synchronously with the persisted orders → `OrdersDidChange` runs immediately. If `_orders.Enabled` from the persisted save, `OrdersDidChange` AddComponents the subsystem children and starts the planner Loop. **All this happens in `OnEnable`, before any `Update`/`FixedUpdate` tick.**

`AutoEngineer.OnEnable` runs separately. It always starts its `Loop` coroutine. The engineer's loop just checks `Run` and idles if false.

### Pitfalls

- **Patching `BaseLocomotive.Awake` postfix**: planner is already added by then. Use prefix to prevent it.
- **Patching `AutoEngineerPlanner.Awake`**: `_engineer` may or may not exist in the prefix. Postfix is safer.
- **Subscribing to KVO observers from another `Awake`**: KVO is on the locomotive, not the planner — safe. But the planner's persistence struct only initializes in `OnEnable` — querying it before then null-derefs `_object`.
- **Multiple components claiming `AutoEngineer`**: `BaseLocomotive.Awake`'s `GetComponent<AutoEngineer>() == null` gate means the FIRST `AutoEngineer`-typed component wins, planner stays out. If a mod adds `class MyAE : AutoEngineer` and then tries to ALSO add the planner separately, the planner adds another `AutoEngineer` (line 257) → two engineers.

---

## MP authority — host-only implications

**Planner runs only on the host.** Confirmed by `BaseLocomotive.Awake` host gate. **Implications for mods:**

1. **Custom planner state lives on the host only.** A mod that wants client-visible state (e.g. "AI is currently approaching signal X") must publish via the locomotive's KVO or a Messenger event. The planner's private fields (`_route`, `_lastSignalSpeedRestriction`, `_calledSignal`) are invisible to clients.

2. **Custom commands need a request message.** Clients cannot directly invoke `HandleCommand` — they MUST send `AutoEngineerCommand` (or a mod-defined `IGameMessage`). The host receives, validates, dispatches.

3. **Targets/PID outputs propagate as side-effects.** When the host's planner sets `_control.Throttle`, that writes the locomotive's throttle KVO; clients see it via standard control-property KVO sync. **No special "planner sync" message exists.**

4. **Route data is broadcast, not host-only KVO.** `AutoEngineerWaypointRouteUpdate` is sent on every step-change or route-change (`FireChangeMessage` line 1830). Clients query for the full route on demand via `AutoEngineerWaypointRouteRequest` → `Response`.

5. **Per-message authorization is shallow.** `AutoEngineerCommand` requires `Crew` access on the message itself; once received host-side, ANY locomotive with a planner can be commanded. There's no per-locomotive owner check beyond `TryGetCarForId`.

6. **Train crew membership is NOT consulted by the planner.** A Crew player can drive any locomotive's AI even if not on the train's crew. (`SwitchListController` does enforce per-crew membership for switch lists, but the planner is symmetric.)

7. **Save state restored host-only.** When the world loads, the host's planner observers fire on the persisted `aiOrders` and resurrect the AI state. Clients restore from snapshot but their planner doesn't exist; they see the resulting KVO updates.

8. **Subsystem additions need to be host-only.** Mod components attached to `AutoEngineerPlanner` should themselves gate on `StateManager.IsHost`. The planner's existence already implies host, but if a mod attaches to `BaseLocomotive` instead and assumes a planner, it'll NRE on clients.

---

## Patch points — comprehensive recipe book

### Replacing PIDs

- **Per-loco PID retune**: `AutoEngineer.OnEnable` postfix. After `CopyTo`, the controllers are independent — overwrite `proportionalGain`, etc. directly.
- **Global PID retune**: patch `AutoEngineerConfig` field values (`TrainController.Shared.autoEngineerConfig.throttlePID.proportionalGain = 1.5f;`) before `OnEnable` runs — i.e. very early in init, or postfix `OnEnable` to re-`CopyTo` from the modified config.
- **Replace PID class entirely**: harder. Patch `Compute` to substitute your own algorithm — or harmony-postfix `MaintainSpeed`'s relevant `Compute` calls and substitute output. Cleanest: patch `MaintainSpeed` body wholesale.

### Custom planner targets

- **Add a new target source**: postfix `UpdateTargets` and call `_engineer.SetTargets` with a modified Targets list. **WARNING**: vanilla `UpdateTargets` already called `SetTargets` — your postfix will set again, which is fine (the engineer just diffs). Only the LAST `SetTargets` per planner-tick wins.
- **Modify existing targets in-flight**: prefix `_engineer.SetTargets` (or `AutoEngineer.SetTargets`) — mutate `targets.AllTargets` before vanilla equality check. **Beware**: `Targets.AllTargets` is `List<Target>`, mutable; you can `Add`/`Remove`. Re-sort by distance after.
- **Mod-side speed limits**: patch the local `MaxSpeedForTrackMph` inside `Search` — IT'S A LOCAL FUNCTION, not directly accessible. Practical alternative: patch `Search` itself (it's `private static`), OR patch `UpdateTargets` and add a clamp target after vanilla emits.

### Energy-aware throttle

Goal: smoother accel/coast based on energy efficiency.

- **Patch `MaintainSpeed`'s asymmetric error multiplier (line 771: `num7 *= 10f`)**: replace with energy-aware scaling.
- **Patch `CalculateThrottleForTargetVelocityStarting`**: this controls only the starting phase. For steady-state, patch `MaintainSpeed`.
- **Patch `RoundUpToNotch` / `RoundDownToNotch`**: replace 8-notch quantization with finer/coarser. Both are `private static`; harmony-replace works.

### Smarter braking model

Goal: model brake response time, not just max-application.

- **Patch `CalculateTotalAvailableBraking` (line 440)**: factor in current brake-cylinder pressure (effective brake force = max × pressure_fraction). This propagates into both `ContextualTargetVelocity`'s physics model AND `RouteStartLocation`'s momentum estimate.
- **Patch `CalculateMaxVelocityToSlowToSpeedAtDistance`**: change the kinematic formula. E.g., add a delay term: `v = sqrt(v_f² + 2·a·(d - v·t_delay))`.
- **Patch `ContextualTargetVelocity` directly**: replace the dual-model Min with your preferred algorithm.

### Alternative speed envelopes

Goal: replace the two `maxVelocityForDistance{Light,Heavy}` curves with something else (e.g. quadratic, or per-loco).

- **Replace curves at runtime**: assign new `AnimationCurve` to `_config.maxVelocityForDistanceLight` / `Heavy`. Affects all locomotives (singleton config).
- **Per-loco override**: harder. Patch `AutoEngineer.OnEnable` to capture `_config` reference, then patch `ContextualTargetVelocity` to substitute per-loco curves.
- **Replace `WeightParameter` formula**: patch the `WeightParameter` getter (line 257). Currently `InverseLerp(weightTonsLight, weightTonsHeavy, weight/2000f)`. A modder could blend by other criteria (tractive effort, brake force, derailment risk).

### Replacing the entire planner

- **Strategy 1 (swap brain, keep engineer)**: Patch `BaseLocomotive.Awake` postfix:
  1. `AutoEngineerPlanner` already added.
  2. `Destroy(loco.AutoEngineerPlanner)` — note this triggers `OnDisable` → `StopCoroutineAndDestroyChildComponents`.
  3. `loco.AutoEngineerPlanner = null` — but it's a private set, so reflection or harmony.
  4. Add your own planner that drives `_engineer.SetTargets`.
  5. Your planner still needs to handle `ApplyMovement` (intercept `BaseLocomotive.FireOnMovement`).
  6. Your planner must publish status via `aiPlannerStatus` for UI consistency.

- **Strategy 2 (swap engineer, keep planner)**: Patch `AutoEngineerPlanner.Awake` prefix to skip `_engineer = AddComponent<AutoEngineer>()` and add your own. Subscribe to the planner's `SetTargets` calls. Hard — `AutoEngineer` exposes much state to the planner (`Run`, `WaitingForBrakes`, `HandbrakeApplied`, `BrakeLineTogether`, `BrakesReleasedOnNonAirConnectedCars`, `CalculateLookaheadDistance`, `CalculateTotalAvailableBraking`).

- **Strategy 3 (swap both)**: Patch `BaseLocomotive.Awake` prefix to skip the `if`-block. Add your own component pair. **Only this strategy fully avoids vanilla coroutines and KVO writes.** You retain only the locomotive's control surfaces.

### Hooking into specific plan steps

| Goal | Patch site |
|---|---|
| Veto specific commands | `AutoEngineerPlanner.HandleCommand` prefix |
| Add new contextual order types | `AutoEngineerPlanner.HandleContextualOrder` prefix + extend `ContextualOrder.OrderValue` enum (mod-side) |
| Custom signal aspect handling | `UpdateTargets` switch on `LastShownAspect` (around line 506) — patch `UpdateTargets` is heavy; consider patching `SetSignalSpeedRestriction` |
| Custom hotbox / spotter conditions | `AutoHotboxSpotter.CheckForHotbox` postfix; for the 15-mph clamp, patch the line at `UpdateTargets:611` |
| Custom pitfall conditions | `ShouldStopForPitfall` postfix |
| Custom passenger stop logic | `AutoEngineerPassengerStopper.UpdateFor` |
| Custom route search heuristic | Replace `HeuristicCosts.AutoEngineer` (passed to `Graph.FindRoute` at line 1515/1547) |
| Disable certain modes | `AutoEngineerOrdersExtensions.MaxSpeedMph(Mode)` patch to return 0 — the UI helper clamps to it |
| Custom horn pattern | `AutoEngineerCrossingSignaler.CrossingCoroutine` |
| Suppress AI chat broadcasts | `AutoEngineerPlanner.Say` prefix returning early (note: AnnounceStop and Fuel alerts also use Say) |
| Per-loco custom behavior | Keep the planner; register a mod-side observer on `_persistence.ObserveOrders`; react to specific locos |

### Bypassing emergency stop

`AutoEngineer.EmergencyStop` (line 432) is a velocity-zero teleport (`SetVelocity(0f, ...)`). To prevent: prefix `EmergencyStop` to `return` early. Or prefix `MaintainSpeed`'s `TargetWantsEmergencyStop` (a local function — patch via inner-class harmony) to always return false. Or simpler: patch `MaintainSpeed` body directly to skip the if-branch.

### Custom KVO keys for AI state

Add new keys to `_locomotive.KeyValueObject` from your mod-side planner. **Use a mod-prefix** like `mod_myai_*` to avoid colliding with vanilla `ai*`. Auth follows `Car.AuthorizationRequirementForPropertyWrite` defaults — non-`_`-prefixed → Crew-level write. Add to `Car.HostPrefixes` (via patch) for HostOnly auth.

---

## Gotchas (planner-internals-specific, not duplicating round 1)

- **`_underTrainPoints` is invalidated every `ApplyMovement`**. Mods that compute their own under-train geometry should rebuild on the same cadence (every physics tick).

- **`Search` step is fixed at 10 m**. To get finer resolution near a switch, patch `Search` to take smaller steps within the last N meters before the switch — or re-walk that segment yourself.

- **`MaxSpeedForTrackMph` minimum is `Mathf.Max(5f, ...)`**. Even on extreme curves the AI won't go below 5 mph. Your custom rolling stock with `MaximumTrackCurvature` set very high doesn't crash the formula but does floor at 5.

- **Posted speed limit defaults to 35 mph for unposted segments** (`if (posted == 0) posted = 35`). A modder adding a new track type without setting `speedLimit` gets 35.

- **Target sort happens AFTER all targets are added** (line 696, `targetInfos.OrderBy`). If you postfix `UpdateTargets` to add targets, they appear in the list but aren't necessarily sorted with vanilla's. The engineer's `TargetSpeedMph` getter scans for `Distance < 1f` linearly — order matters for which target it returns first.

- **`Targets.AllTargets` is mutable**. `ApplyMovement` writes back to it via `_targets.AllTargets[i] = value`. Don't snapshot or share the list with another thread.

- **`SetTargets` does an `Equals` check** that includes `SequenceEqual` on `AllTargets`, comparing `(SpeedMph, Distance)` only — `Reason` differences DON'T trigger update. So consecutive `SetTargets` with identical numbers but different reasons are treated as no-change. Useful: you can change reasons cheaply for status display. Hazard: if your mod adds targets that compute `Reason` from time, you may suppress legitimate updates.

- **`Targets.AllTargets` is initialized as a fresh `List<Target>` per tick** (line 696 — built from `targetInfos.Select`). Mutations by `ApplyMovement` apply to the same list across ticks until the next `UpdateTargets` overwrites it. So holding a reference to `_targets.AllTargets` from a mod is racy across plan-ticks.

- **`_loopKeepalive` runs in scaled time** (`AutoEngineer.cs:289`: `new CoroutineKeepalive(60f, scaledTime: true)`). When game time is paused, neither the loop nor the keepalive timer advance. So the keepalive can't fire spuriously during pause.

- **Two coroutines, two keepalives**. Planner has `_loopKeepalive`; engineer has its own. Independent restart. Don't assume one keepalive-fail also restarts the other.

- **`OffDuty` does NOT call `_passengerStopper`'s shutdown**. The stopper is destroyed wholesale via `StopCoroutineAndDestroyChildComponents`, but if you're holding a reference (don't), it's now invalid.

- **`RestoreAllRemainingSwitches` runs on waypoint arrival** (`IsWaypointSatisfied` true, line 1191). Switches are restored even if the train hasn't passed them — this is a "bookkeeping cleanup" not a "geographic" restore. If the AI is interrupted mid-route, `_routeSwitchesToRestore` is cleared by `ClearRoute` but switches stay where they were thrown.

- **`BaseDistanceLimit = Mathf.Max(_maximumLength, 250f)`** (line 238). Signal restrictions persist for at least 250 m past the signal, OR the consist length, whichever is larger. A 500 m train crossing a Restricting signal is restricted for 500 m past the signal — meaning the restriction is in effect "until the entire train has cleared and 250 m more."

- **`StartLocation` returns the front of the LEAD car**, not the loco. If the loco isn't at the front of the consist (e.g. shoving), the start location is on the car at the leading end — usually correct, but if you assume the search starts from the loco specifically, you'll be wrong.

- **`_coupledCarsCachedEnd` is set in `UpdateCars`** (line 862) based on `_orders.Forward`. If `Forward` flips, `UpdateCars` re-enumerates from the other end. So the "lead car" can change on a direction change.

- **`UpdateTargets` is wrapped in try/catch** in `Loop` (line 434) — exceptions are logged and the loop continues. **A buggy mod patch that throws inside `UpdateTargets` will silently stall plan updates** but the keepalive won't fire (the loop is still ticking, just no plan changes). Watch the log.

- **`ApplyMovement` is NOT wrapped**. An exception there propagates up to `BaseLocomotive.FireOnMovement` and breaks the per-tick movement chain. Other listeners after the planner won't fire that tick.

- **`SetSignalSpeedRestriction(null, ...)` for Clear/DivergingClear sets `_lastSignalSpeedRestriction = null`** — which is the desired "no restriction" state. But this clears the restriction even if a previous Approach signal had set it. The order in `UpdateTargets` is: read next signal → switch on aspect → set/clear restriction. Each plan-tick re-evaluates from scratch.

- **The `_stopAndProceedSignalId` latch is single-valued**. If you stop at signal A (intermediate Stop, latched at 15 mph), then continue past A, then encounter signal B (also intermediate Stop), the latch for A is still set — but `UpdateTargets` looks up `_stopAndProceedSignalId == item.id` against the CURRENT signal (B), so the latch only matters for the signal it was set on. Effectively single-shot per signal. Cleared by `WillMove`.

- **`HandleContextualOrder(PassSignal)` raises the restriction's speed to 15 mph and resets `DistanceLimit`** (lines 1031-1033) — so "pass this signal" doesn't fully drop the restriction; it converts it to a 15 mph restriction with a fresh distance budget. The AI moves past at 15. To fully clear: `ResumeSpeed`.

- **`SetPlannerStatus` is overridden by `WaitingForBrakes`** (line 803). The status string the user sees is "Charging brake line" during brake-charging — even if the planner just computed something else. Mods reading `aiPlannerStatus` for telemetry should handle this string specially.

- **`Persistence.ContextualOrders = contextualOrders;` runs every plan tick**. The KVO array is rewritten wholesale even if unchanged. Observers see a write event every tick (not a value-change event — the underlying KVO fires on assignment, not on diff). High-frequency observer churn for downstream UI.

---

## Surprises this round

1. **The `AutoEngineerPlanner` coroutine has its OWN keepalive** (`_loopKeepalive` at line 151), separate from `AutoEngineer._loopKeepalive`. Round 1 noted "two coroutines, two keepalives" but didn't show that the planner-side keepalive uses `AutoEngineer.CreateKeepalive()` — which is a `CoroutineKeepalive(60f, scaledTime: true)`. So the planner gets the same 60 s scaled-time policy.

2. **`testKeepalive` SerializeField on both `AutoEngineerPlanner` and `AutoEngineer`** (lines 203 and 188). Setting via Inspector forces a coroutine death to test the keepalive restart path. Useful debugging knob no documentation mentions.

3. **`_underTrainPoints` is a per-instance buffer cleared by `ApplyMovement` and lazily refilled by `IsUnderTrain`**. So waypoint-satisfaction checks (called from `TickRoute`'s 1 Hz cadence) effectively reuse a cached point list across the whole tick.

4. **`MaxWaitForStopAtDistance` returns LARGER values for larger distances**, but `MaintainSpeed` clamps the yield to `Mathf.Min(0.5f, ...)`. Since `MaxWaitForStopAtDistance(< 50m) = 0.5`, `MaxWaitForStopAtDistance(< 100m) = 0.75`, `MaxWaitForStopAtDistance(else) = 1.0`, the only effective case is "< 50m → 0.5" — and the `else` cases get clamped back to 0.5 anyway. **Effectively the yield is always 0.5 s.** Possible legacy code.

5. **`paddingForSpeedMph` curve adds headroom OVER the current speed, not under the target speed.** I.e., the error becomes `target - (velocity + padding)`. So "target 30, velocity 25, padding 1" yields error = 30 - 26 = 4. Padding makes the controller think it's slightly faster than it is — anti-overshoot.

6. **`brakeErrorPower` can be 0 to 4**, default 1. At 0, `Mathf.Pow(num8, 0) = 1` always — so error becomes `Sign(num8) * 1` for any negative error. Effectively "always full brake" if any overspeed. Probably degenerate case.

7. **`trainBrakeDerivativeGainForNumAirOpenCars` is NEGATIVE** (default `Linear(10, -0.5, 30, -1.2)`). Negative D-gain means the controller damps differently than the textbook PID — the derivative term works AGAINST the integral/proportional, smoothing apply/release transitions on long trains. Without this, train brake oscillates.

8. **`ContextualTargetVelocity` runs `WeightParameter` AND `CalculateTotalAvailableBraking`** which both call `CachedCoupledWeightLbs` and `AirOpenCars`. Caches are shared (`_cachedCoupledWeightLbs`, `_cachedAirOpen`). **Each `MaintainSpeed` tick calls `InvalidateCachedCars`** (line 744), so the cache is fresh per tick but reused within the tick. Important: don't add expensive work to `CachedCoupled()` — it's invalidated frequently.

9. **`_persistence` is reinitialized in `OnEnable` every time** the planner is re-enabled. If the locomotive's `KeyValueObject` was replaced (it shouldn't be, but mods could), the new `_persistence` points to whatever's current. Ref semantics make this generally safe.

10. **`Search`'s `IsOnSameRoute` is a LOCAL FUNCTION inside the loop** (line 2067) — captures `cursor`, `currentAvailableDistance`, `graph`. Each call to `Search` builds a fresh closure. Patching this requires patching the parent method.

11. **The `AutoEngineerPersistence` struct is `readonly`** (line 8) — can only be replaced wholesale, not field-mutated (which is fine since its only field is the `KeyValueObject` reference).

12. **`AutoEngineerPlanner` doesn't directly read `_locomotive.HasFuel`'s consumers — it just reads the bool.** `HasFuel` is consulted only at `Loop` cadence (every 0.5-3 s). A locomotive that runs out mid-physics-tick won't be caught until the next plan tick. The PID keeps applying throttle in the meantime. Probably fine because fuel exhaustion is gradual.

13. **`Targets.NextSignal` is the CTC signal reference passed for "Holding at <name>" announcement**, but the planner stops looking at it after passing. The engineer holds the reference and uses it for the announce string in `AnnounceStop`. **This is the only Object reference held across plan-ticks in `Targets`.** If the signal is destroyed (e.g. tile unloaded), the reference may dangle — but signal destruction in vanilla is rare.

---

## Cross-references

- **Round-1 high-level overview**: [`autoengineer.md`](autoengineer.md) — message routing, subsystem lifecycles, mode behaviour matrix.
- **`BaseLocomotive` and the planner-add gate**: [`locomotive-architecture.md`](locomotive-architecture.md#three-piece-subclass-bundle).
- **`MovementInfo` flow into `ApplyMovement`**: [`odometer-movement.md`](odometer-movement.md) — `MovementInfo` is unsigned-distance; `BaseLocomotive.FireOnMovement` is sole call site.
- **Save format for `aiManualStopDistance`**: [`save-load.md`](save-load.md) — `WorldWillSave` Messenger fan-out and per-property persistence.
- **`Graph.FindRoute`, `RouteSearch.Step`, `HeuristicCosts.AutoEngineer`**: [`track-topology.md`](track-topology.md).
- **`CTCSignal.LastShownAspect`, `IsCTCSwitchUnlocked`, `CTCInterlocking`**: [`signals-dispatch.md`](signals-dispatch.md).
- **`Car.air.DefersToLocomotiveAir` and the trail-unit pattern** (consumed by `ShouldUseLocomotiveBrake`): [`mu-dpu-coordination.md`](mu-dpu-coordination.md).
- **`AutoEngineerPassengerStopper` and timetable integration**: [`passengers-timetable.md`](passengers-timetable.md).
- **Audit chain for planner-driven UI state**: [`events-catalog.md`](events-catalog.md) — `AutoEngineerWaypointRouteUpdate` etc.
