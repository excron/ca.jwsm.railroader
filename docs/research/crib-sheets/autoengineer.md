# AutoEngineer (AI) — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Model.AI/`)
**Companions:** [Wear & Durability](wear-durability.md), [Couplers](couplers.md)

The "Auto Engineer" is Railroader's AI driver. It is not one MonoBehaviour — it's a stack of three loops layered on a `BaseLocomotive`: an outer `AutoEngineerPlanner` that builds a list of `(speed, distance)` targets from track topology, signals, flares, passenger stops, and waypoint routes; a middle `AutoEngineer` that runs PID controllers against those targets to move throttle / reverser / independent / train brake; and a swarm of "subsystem" components that handle hotbox spotting, fuel alerts, crossing whistles, oiling, and passenger-stop announcements. Player intent enters via a single `AutoEngineerCommand` request message; everything runs **host-only** and pushes state out via four KVO keys on the locomotive (`aiOrders`, `aiCtxOrders`, `aiManualStopDistance`, `aiPlannerStatus`).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `AutoEngineerPlanner` | `Model.AI/AutoEngineerPlanner.cs:29` | Top-level brain: builds `AutoEngineer.Targets` from track + signals + waypoint route. Owns subsystem child components. |
| `AutoEngineer` | `Model.AI/AutoEngineer.cs:21` | Lower-level driver: runs Loop coroutine that switches between Stopped/Starting/Running. PID-driven throttle and brakes. |
| `AutoEngineer.Targets` (nested class) | `Model.AI/AutoEngineer.cs:23-143` | Plan output: max speed, list of `Target(speedMph, distance, reason)`, grades, mode, stop announce, next signal. |
| `AutoEngineerCommand` | `Game.Messages/AutoEngineerCommand.cs:8` | Player→host request message (`MinimumAccessLevel(Crew)`). Sent by `AutoEngineerOrdersHelper.SendAutoEngineerCommand`. |
| `Orders` (struct) | `Model.AI/Orders.cs:7` | Mode + Forward + MaxSpeedMph + Waypoint. Persisted on locomotive KVO key `aiOrders`. |
| `AutoEngineerPersistence` | `Model.AI/AutoEngineerPersistence.cs:8` | KVO façade for `aiOrders`/`aiCtxOrders`/`aiManualStopDistance`/`aiPlannerStatus`/`aiPassModeStatus`. |
| `AutoEngineerConfig` (ScriptableObject) | `Model.AI/AutoEngineerConfig.cs:6` | All tuning knobs: PIDs, lookahead curves, brake state-machine curves, crossing patterns. Singleton: `TrainController.Shared.autoEngineerConfig`. |
| `AutoHotboxSpotter` | `Model.AI/AutoHotboxSpotter.cs:10` | 60–300 s spotter loop; sets `HotboxSpotted` which planner reads to clamp speed to 15 mph. |
| `BaseLocomotive.Awake` | `Model/BaseLocomotive.cs:426` | The only construction site: `if (StateManager.IsHost && GetComponent<AutoEngineer>() == null) AutoEngineerPlanner = AddComponent<AutoEngineerPlanner>()`. |

---

## Layer cake: Planner → Engineer → subsystems

```
BaseLocomotive (host only)
   └─ AutoEngineerPlanner   (always exists on host; coroutine starts only when Orders.Enabled)
         ├─ AutoEngineer    (added in Awake via AddComponent<AutoEngineer>)
         │     └─ AutoOiler (added in AutoEngineer.OnEnable)
         ├─ AutoEngineerCrossingSignaler  (added when Orders.Enabled)
         ├─ AutoEngineerFuelAlerter       (added when Orders.Enabled)
         ├─ AutoHotboxSpotter             (added when Orders.Enabled)
         └─ AutoEngineerPassengerStopper  (added in UpdateCars when consist has passenger cars + !Yard mode)
```

**Lifetime model.** `AutoEngineerPlanner` is added once in `BaseLocomotive.Awake` (host-only check). It exists for the locomotive's life. The planner's `_coroutine` is what spins up/down with `Orders.Enabled` (`OrdersDidChange` at `AutoEngineerPlanner.cs:301`). Subsystem components are `AddComponent`'d when AI turns on and `Destroy`'d when it turns off — see `StopCoroutineAndDestroyChildComponents` (`AutoEngineerPlanner.cs:344`).

**`AutoEngineer` is created lazily by the planner** in its `Awake` (`AutoEngineerPlanner.cs:257`): `_engineer = base.gameObject.AddComponent<AutoEngineer>();`. So on a non-host client, `AutoEngineerPlanner` is never created → `AutoEngineer` is never created → no driver runs locally.

### Two coroutines, two cadences

- **Planner Loop** (`AutoEngineerPlanner.Loop`, `AutoEngineerPlanner.cs:372`): runs every 0.5–3 s depending on speed. Re-builds the target list, calls `_engineer.SetTargets(...)`. Yield interval logic at line 445: `mph > 30 → 0.5s`, `mph > 0.1 → 1s`, else `3s`.
- **Engineer Loop** (`AutoEngineer.Loop`, `AutoEngineer.cs:359`): runs at FixedUpdate cadence inside `MaintainSpeed` (yield 0.5 s normal, shorter near a stop). PID controllers tick here.

Each coroutine has its own `CoroutineKeepalive` (60 s; restart-on-timeout) — see `AutoEngineer.CreateKeepalive` (`AutoEngineer.cs:287`). **An exception in the loop will silently restart the coroutine after 60 s of stall.** Patches that throw early in `Loop` will appear to "work" but at 60 s heartbeats. Bad for debugging.

---

## `Model.AI.Orders` (the player intent)

```csharp
public readonly struct Orders {
    public readonly AutoEngineerMode Mode;        // Off, Road, Yard, Waypoint
    public readonly bool             Forward;
    public readonly int              MaxSpeedMph;
    public readonly OrderWaypoint?   Waypoint;    // loc string + couple-to car id
    public bool Enabled => Mode != AutoEngineerMode.Off;
}
```

**Mode-derived speed cap** (`AutoEngineerOrdersExtensions.cs:8`): `Off=0, Road=45, Waypoint=45, Yard=15`. The UI helper clamps to this in `AutoEngineerOrdersHelper.SetOrdersValue` (`UI.EngineControls/AutoEngineerOrdersHelper.cs:43`) — `Mathf.Min(maxSpeedMph, mode.MaxSpeedMph())`. The planner does not re-clamp; mods that send `AutoEngineerCommand` directly with `MaxSpeedMph=80` and `Mode=Road` get **80 mph**, not 45.

### `AutoEngineerMode` enum (`Game.Messages/AutoEngineerMode.cs`)

```
Off = 0, Road = 1, Yard = 2, Waypoint = 3
```

### Mode behavior summary

| Mode | OtherCarHandling | SwitchAgainst | manualStopDistance | Speed cap |
|---|---|---|---|---|
| Off | (no movement) | — | — | 0 |
| Road | Avoid | StopBeforeFouling | unused (ignored) | 45 mph |
| Yard | CoupleTo(null) — couples to anything ahead | StopBeforeFouling | meters; counts down | 15 mph |
| Waypoint | NoCouple OR CoupleTo(carId) | **FoulThrowableSwitches** | computed by route ticker | 45 mph |

Decided in `UpdateTargets` (`AutoEngineerPlanner.cs:455`).

---

## `AutoEngineerCommand` — the entry door

```csharp
[MinimumAccessLevel(AccessLevel.Crew)]
[MessagePackObject(false)]
public struct AutoEngineerCommand : IGameMessage {
    public string LocomotiveId;
    public AutoEngineerMode Mode;
    public bool Forward;
    public int MaxSpeedMph;
    public float? Distance;                  // Yard mode only
    public string WaypointLocationString;    // Waypoint mode
    public string WaypointCoupleToCarId;     // Waypoint mode + WantsCouple
}
```

(Source: `Game.Messages/AutoEngineerCommand.cs:8`.)

**Routing.** `StateManager.HandleMessage` switch (around `Game.State/StateManager.cs:572`) matches `AutoEngineerCommand` and calls `_trainController.HandleAutoEngineerCommand(command, sender)` (line 720). `TrainController.HandleAutoEngineerCommand` (`TrainController.cs:2110`):

```csharp
public void HandleAutoEngineerCommand(AutoEngineerCommand command, IPlayer sender) {
    if (IsHost && TryGetAutoEngineerPlanner(command.LocomotiveId, out var planner))
        planner.HandleCommand(command, sender);
}
```

So the message is host-handled, with **no further per-locomotive auth check** beyond the message-level `Crew` minimum and locomotive resolvability. If you have crew-level access on the host, you can drive any locomotive that has an `AutoEngineerPlanner`.

**`AutoEngineerPlanner.HandleCommand`** (`AutoEngineerPlanner.cs:979`) writes `_persistence.Orders = new Orders(...)`, which writes the `aiOrders` KVO key. The planner's own `_ordersObserver` then fires `OrdersDidChange` (`AutoEngineerPlanner.cs:301`) which spins coroutines up/down.

**Side-effects of HandleCommand:**
- Resets `_lastSignalSpeedRestriction = null` (loses memory of the last passed signal aspect).
- For `Road`/`Off`: calls `CancelManualStopDistance()`.
- For `Yard`: if `command.Distance.HasValue`, sets manual stop distance; if entering Yard from another mode (no Distance specified), sets to `0f` (i.e. stop immediately — yard mode requires explicit stop distance to move).
- For `Waypoint`: if the waypoint's `LocationString` is new, captures `sender` as `_routeRequester` so route-error replies can be sent back.

### Sibling messages

| Message | File | Auth | Purpose |
|---|---|---|---|
| `AutoEngineerContextualOrder` | `Game.Messages/AutoEngineerContextualOrder.cs` | `Crew` | "Pass this signal", "Pass this flare", "Resume speed", "Bypass timetable". `_contextualIgnore*` fields hold the result. See `HandleContextualOrder` (`AutoEngineerPlanner.cs:1022`). |
| `AutoEngineerWaypointRerouteRequest` | `Game.Messages/AutoEngineerWaypointRerouteRequest.cs` | `Crew` | Force a re-routing pass. `HandleRequestReroute` → `UpdateWaypointRouteIfNeeded(force: true)`. |
| `AutoEngineerWaypointRouteRequest` | `Game.Messages/AutoEngineerWaypointRouteRequest.cs` | `Crew` | Client→host: "send me the current route locations." Host responds with `AutoEngineerWaypointRouteResponse`. |
| `AutoEngineerWaypointRouteResponse` | `Game.Messages/AutoEngineerWaypointRouteResponse.cs` | `HostOnly` | Host→client: list of `Snapshot.TrackLocation` for overlay rendering. |
| `AutoEngineerWaypointRouteUpdate` | `Game.Messages/AutoEngineerWaypointRouteUpdate.cs` | `HostOnly` | Host broadcast: "current step changed" / "route changed" — drives `AutoEngineerWaypointControls.WaypointRouteDidUpdate`. |

---

## `AutoEngineerPersistence` — the four KVO keys

`AutoEngineerPersistence` (`Model.AI/AutoEngineerPersistence.cs:8`) is a `readonly struct` wrapper around the locomotive's `KeyValueObject` exposing:

| Key | Type | Auth (per `Car.AuthorizationRequirementForPropertyWrite`) |
|---|---|---|
| `aiOrders` | dict (Mode, Forward, MaxSpeedMph, Waypoint) or null | **Default crew + train-crew** (no `_` prefix, not in `HostPrefixes`) |
| `aiCtxOrders` | array of `ContextualOrder` dicts | Same |
| `aiManualStopDistance` | float or null | Same |
| `aiPlannerStatus` | string or null | Same |
| `aiPassModeStatus` | string or null | Same |

**Critical: `aiOrders` is NOT host-only by KVO auth.** A crew-authorized client *could* in principle write `aiOrders` directly via a generic property-change message and bypass `AutoEngineerCommand`. The `HandleCommand` validation logic (mode-coupled distance/waypoint behavior) would be skipped. Use `AutoEngineerCommand` for any mod that adds AI control surfaces.

**Authority asymmetry:** the *command* message is `Crew`-only, but the *property writes* it triggers (mode, forward, speed, waypoint) propagate via KVO to all clients (since they're not in any prefix that requires host).

### Observers

```csharp
public IDisposable ObserveOrders(Action<Orders> action, bool callInitial = true);   // 92
public IDisposable ObserveManualStopDistance(Action<float?> action);                // 100
public IDisposable ObservePlannerStatusChanged(Action action);                      // 108
public IDisposable ObservePassengerModeStatusChanged(Action action);                // 116
public IDisposable ObserveContextualOrdersChanged(Action action);                   // 124
```

UI / overlays / external mods subscribe through these. The planner itself uses `ObserveOrders` (`AutoEngineerPlanner.cs:270`) to react to its own writes — the loop won't see a new mode until the next tick after `HandleCommand` writes the KVO.

---

## `AutoEngineerPlanner.Loop` — outer plan loop

```csharp
private IEnumerator Loop()                                  // AutoEngineerPlanner.cs:372
{
    while (true)
    {
        if (_locomotive.set == null || !_orders.Enabled) { OffDuty(); yield return WaitFixed(0.5f); continue; }
        UpdateCars(out var delta);
        _engineer.Run = true;
        if (_derailed) { ClearOrders, wait, continue; }
        if (delta != 0 && _route != null && _route.Count > 0) UpdateWaypointRouteIfNeeded(force: true);
        if (delta > 0 && _orders.Mode != AutoEngineerMode.Road)
            _manualStopDistance = 0f;                        // coupled in non-road = stop NOW
        if (OrdersWantMovement() && ShouldStopForPitfall(out var reason)) { PostPitfallNotice; SetTargets({}); continue; }
        if (!_locomotive.HasFuel) { say "out of fuel"; ClearOrders; continue; }
        if (sign(orders) != sign(velocity) && |v| > 0.5f) {
            SetTargets(changeDirection: true); yield return WaitFixed(2f); continue;
        }
        try { UpdateTargets(direction, signedMaxSpeedMph); } catch { _log.Error; }
        yield return WaitFixed(speed-dependent cadence);
    }
}
```

### Order of pre-checks (a tripwire if any of these fire, the planner emits empty Targets and re-loops)

1. `_orders.Enabled` is the off-switch (`OffDuty()` clears everything, drops contextuals).
2. `_derailed` (planner clears its own orders → AI shuts itself off after derail).
3. **Coupled-delta-in-non-Road-mode → stop.** `delta > 0 && _orders.Mode != Road` → `_manualStopDistance = 0` (`AutoEngineerPlanner.cs:401`). This is why a Yard-mode shove that picks up a car halts immediately.
4. `ShouldStopForPitfall`: handbrakes applied; or **non-yard** brake-line not connected end-to-end; or brakes on cars not in the air-line. Yard mode skips the brake-line check (you can shove with a broken air line). See `AutoEngineerPlanner.cs:920`.
5. `!_locomotive.HasFuel` — auto-clears Orders. `Say` line is steam vs diesel.
6. `WantsChangeDirection` (sign mismatch) — emits a `Targets` with `ChangeDirection=true` and waits 2 s. The lower-level engineer treats this as zero target velocity and brakes hard.

### `UpdateTargets` (`AutoEngineerPlanner.cs:451`) — building the plan

The function returns nothing; it side-effects `_engineer.SetTargets(...)`. Process:

1. Compute `lookaheadDistance = _engineer.CalculateLookaheadDistance()` — see [Lookahead horizon](#lookahead-horizon).
2. **Two `Search()` passes** (`AutoEngineerPlanner.cs:1966`):
   - `SearchMode.Ahead` from the front of the consist for `lookaheadDistance` meters.
   - `SearchMode.Self` from the back, only `_maximumLength` (consist length) meters, used for "track speed under our own length" (so trailing cars don't overspeed a curve).
3. From the Ahead result, pull the next CTC signal (if any), next flare, next crossing distance, next passenger stop, max speed, restriction distance, average grade, and stop-announce.
4. **Signal handling** (lines 488–588): inspects `LastShownAspect`:
   - `Stop` (intermediate signal) → if very close + stopped, becomes a 15 mph "stop and proceed". Otherwise → AddTarget(0, distance-15, "Stop and Proceed Signal").
   - `Stop` (non-intermediate, i.e. absolute) → contextual `PassSignal` order recorded; SignalSpeedRestriction(0, ..., distance, ...).
   - `Clear`/`DivergingClear` (within 200 m) → SignalSpeedRestriction(null, null, ...).
   - `Approach`/`DivergingApproach` (within 200 m) → if a next signal found within 2 km, plan to be at 15 mph at that next signal (with 25 mph approach speed); else 25 mph at this signal.
   - `Restricting` → 15 mph (distance-limited).
5. **Hotbox cap**: `if (_hotboxSpotter.HotboxSpotted && num5 > 15f) num5 = 15f` (line 611). Hard 15 mph clamp once any car in the consist has rolled positive on the hotbox check.
6. **Track-speed-from-self-search** (line 616): clamps to `result2.MaxSpeedMph` so the last car's curve limit applies.
7. **Other-car distance**: if blocked by a car, add a stopping target. If `Couple` mode and within 50 m, add a 3 mph "Couple" target at distance-5.
8. **Manual stop distance** (line 680): if set, adds a 0 mph target with a flavored reason ("Clear N cars", "That'll do!" near zero, "Running to waypoint"/"At waypoint" in Waypoint mode).
9. **Crossing distance** (line 720): only forwarded to `_crossingSignaler` if a stop comes *after* it.
10. Sort all targets by distance, call `_engineer.SetTargets(new Targets(direction*num5, list2, gradeUnder, gradeAhead, false, mode, stopAnnounce, foundSignal))`.

`SetTargets` (`AutoEngineer.cs:330`) does an `Equals` check — if the targets are bitwise equal to the previous, it doesn't emit a log line.

### `Search` (`AutoEngineerPlanner.cs:1966`) — the lookahead engine

A 10-meter-step walker through `Graph` from a `Location` for up to `lookaheadDistance` meters. At each step it:
- Calls `graph.LocationByMoving(cursor, 10f, checkSwitchAgainstMovement: !flag2)` — throws `SwitchAgainstMovement` (caught) or `EndOfTrack` (caught) to truncate.
- For `SearchMode.Ahead`: probes for another car at the cursor via `TrainController.CheckForCarAtLocation`. If found and not in our consist:
  - `Couple` mode: if on same route, `num3 += 2f` (target 2 m past the car for coupling); else mark switch-fouled.
  - `NoCouple`/`Avoid`: compute `StoppingDistanceIfMovingToward(headCar, car, ...)` if available; else `num3 -= 20` (Avoid) or `num3 -= 1` (NoCouple). Result is converted to a stopping `AvailableDistance`.
- Computes `MaxSpeedForTrackMph(cursor)`:
  - `TrainMath.MaximumSpeedMphForCurve(curvature, equipmentMaximumTrackCurvature)` − 3 mph (or × 0.8, whichever is smaller), then `floor(num/5)*5` (round down to 5 mph), then `min(curveLimit, posted)`.
  - **Posted limit defaults to 35 mph if `segment.speedLimit == 0`.**
  - **Minimum 5 mph** even on extreme curves (the `Mathf.Max(5f, ...)`).
- Tracks `MaxSpeedMphNear = MaxSpeedMph` over `velocityAbs * 5f` meters (≈5 s of travel). This is the "near-future limit" that gets fed back into the plan as a separate clamp.
- Accumulates `AverageGrade += graph.GradeAtLocation(cursor)`, divided by `(AvailableDistance / 10)` at the end.

When the walk catches `SwitchAgainstMovement`: if `switchAgainstHandling == FoulThrowableSwitches` (Waypoint mode) and `CheckThrowable` says we can throw it, the search continues past the switch (with a marker so the planner / route ticker knows to throw). Otherwise the search truncates at `(distance to switch − fouling distance)` and records `StopAnnounce.SwitchAgainst|SwitchFouled|CTCSwitchLocked`.

After the walk, a second pass enumerates `TrackMarker`s for `Flare`, `Crossing`, `PassengerStop` (sameDirection: false — picks up reverse-facing stops too) and for `Signal` (sameDirection: true).

---

## `AutoEngineer.Loop` — inner driver loop

```csharp
private IEnumerator Loop()                                  // AutoEngineer.cs:359
{
    while (true) {
        if (!Run || Locomotive.set == null) { wait 0.5s; continue; }
        while (Run) {
            State state = (IsStoppedAndShouldStay ? Stopped
                          : (IsStopped && !IsZero(TargetSpeedMph) ? Starting : Running));
            // edge transitions: post stop notice, blow horn, dim/light headlight, set Bell
            switch (state) {
                case Stopped:  cylinder cocks open, brake to 1, throttle 0, reverser 0; if grade < 0.2% set TBrake=0 else >=10 psi; wait 1s.
                case Starting: yield return StartMovement();          // ramp brakes off, blow, set reverser, throttle, bell
                case Running:  yield return MaintainSpeed();          // PID loop until stopped or Run drops
            }
        }
        PostStopNotice(null);
    }
}
```

### `IsStoppedAndShouldStay` (`AutoEngineer.cs:259`)

```csharp
if (!IsStopped) return false;
if (IsZero(ContextualTargetVelocity())) return true;       // PID target is 0
if (IsZero(TargetSpeedMph)) return Mathf.Abs(TargetDistance) < 5f;  // close enough to a stop target
return false;
```

So if the AI is stopped and either (a) the velocity it should be at is zero, or (b) it's within 5 m of a stop target, it stays stopped.

### `MaintainSpeed` (`AutoEngineer.cs:732`) — the PID loop

Three PID controllers, all in `AutoEngineerConfig`:
- `throttlePID` → `_control.Throttle` (notch quantization on output: `Mathf.Ceil(num9 * 30f) / 30f`? actually train brake; throttle is set as raw float).
- `independentPID` → `_control.LocomotiveBrake` (used when `ShouldUseLocomotiveBrake()`; see below).
- `trainBrakePID` → `_control.TrainBrake` (used otherwise).

Each tick:
1. `num = ContextualTargetVelocity()` → `num4 = |num|`. **Special case**: if zero target and the next stop target is *behind us*, `num4 = -1f` → forces backing or emergency.
2. `num5 = |velocity| + paddingForSpeedMph.Evaluate(|velMph|) * 0.44704` (m/s padding above current speed; "headroom" curve).
3. `num7 = num4 - num5` → throttle/brake error. If positive (need to accelerate), multiply by 10 (asymmetric — strong accel response, gentle decel).
4. `throttle = throttleController.Compute(num7, dt); _control.Throttle = throttle`.
5. If `throttle != 0`: set reverser via `CutoffSettingForVelocity(velocity)` rounded to 1/20 (`AutoEngineer.cs:985`).
6. Brake side: `num8 = num6` (the un-amplified error); if negative, raised to `_config.brakeErrorPower` (default 1, range 0..4 → exponent on the negative error — sharper braking for big error if power > 1).
7. **Independent vs train brake choice**: `ShouldUseLocomotiveBrake()` (line 886) returns true only if every car in `AirOpenCars()` is the locomotive itself or `air.DefersToLocomotiveAir`. I.e. light engine moves → independent; consist of cars → train brake.
8. Train brake mode: gain auto-tunes via `_config.trainBrakeDerivativeGainForNumAirOpenCars.Evaluate(carCount)` (more cars → more derivative gain). If `output > 0.01 && > current`, raise to `Mathf.Ceil(num9*30)/30` (1/30-pound quantization). If `output < trainBrakeReleaseBelowOutput` (default −0.01), release. **Apply is asymmetric — only goes up at controller direction; release is binary.**
9. **Bail-off auto**: if throttle is on and brake cylinder pressure > 0 and the loco isn't cut out, calls `_control.BailOff()` (line 805). Auto-bail when accelerating against your own brake — important for steam.
10. **Emergency stop trigger** (line 824): if a stop target is more than 3 m behind us and has a stop-announce → `_control.TrainBrake=1; _control.LocomotiveBrake=1; Locomotive.set.SetVelocity(0, ...)`. Hard teleport-velocity-to-zero. **Bypasses any soft brake response** — a mod that re-implements brakes entirely won't catch this.
11. Yield interval: 0.5 s normally, dropped to `MaxWaitForStopAtDistance(TargetDistance)` (0.5/0.75/1.0 s depending on distance) when the target is zero — finer ticks near a stop.

### `ContextualTargetVelocity` (`AutoEngineer.cs:451`) — the kinematic decision

This is the single most important method in the system. Returns the target velocity in m/s (signed).

```csharp
internal float ContextualTargetVelocity() {
    if (WantsChangeDirection || _config == null) return 0f;
    float totalAvailableBraking = CalculateTotalAvailableBraking();
    float num = |MaxSpeedMph * 0.44704|;       // base = orders speed in m/s
    float weightParameter = WeightParameter;   // 0..1 light→heavy
    foreach (var t in _targets.AllTargets) {
        float target_mps = |t.SpeedMph * 0.44704|;
        if (target_mps < num) {
            float chosen;
            if (t.Distance > 0.1f) {
                // Two parallel curves give "max velocity at distance d that still allows reaching target_mps":
                float a = config.maxVelocityForDistanceLight.Evaluate(distance + offsetLight);
                float b = config.maxVelocityForDistanceHeavy.Evaluate(distance + offsetHeavy);
                float tableMps = lerp(a, b, weightParameter) * 0.44704f;
                // Physics-based curve: v(0) = sqrt(v_final^2 + 2 * a_brake * d)
                float physMps = CalculateMaxVelocityToSlowToSpeedAtDistance(target_mps, distance, totalAvailableBraking);
                chosen = min(physMps, tableMps);
            } else {
                chosen = target_mps;
            }
            if (chosen < num) num = chosen;
        }
    }
    return Mathf.Sign(_targets.MaxSpeedMph) * num;
}
```

Two parallel models clamp the speed:

1. **Configured curve** (`maxVelocityForDistanceLight` / `Heavy` curves on `AutoEngineerConfig`). Each is `AnimationCurve.Linear(0, 0, 200, 100)` by default (i.e. linear ramp). `FindTimeForValue` finds the distance at which the curve reaches the target speed; offset added so the curve "starts" at the right spot. Lerped by `WeightParameter = InverseLerp(weightTonsLight=500, weightTonsHeavy=1000, weight)`.
2. **Physics-derived limit** (`CalculateMaxVelocityToSlowToSpeedAtDistance`, `AutoEngineer.cs:489`): `v(0) = sqrt(v_final² + 2·a·d)` where `a = totalAvailableBraking / mass`. **This uses `CalculateTotalAvailableBraking` which sums `Car.CalculateBrakingForce(1f, |v|)` over `AirOpenCars()` — full-application brake force.** It does NOT model brake response time, train-line propagation, or the brake-pipe state machine. The PID compensates for the lag empirically; the kinematic estimate is overoptimistic.

Take `min` of the two. The configured curves are usually MORE conservative than the physics estimate, so they dominate for normal trains; light engines on heavy grades may be physics-bound.

---

## Lookahead horizon

`AutoEngineer.CalculateLookaheadDistance()` (`AutoEngineer.cs:723`):

```csharp
float vMph = Locomotive.VelocityMphAbs;
float a = config.maxVelocityForDistanceLight.FindTimeForValue(vMph, 0.1f);
float b = config.maxVelocityForDistanceHeavy.FindTimeForValue(vMph, 0.1f);
return Mathf.Lerp(a, b, weightParameter) + 100f;
```

The horizon is **"how far we need to start braking now to reach 0 mph"** plus a 100 m buffer, weight-blended. Default curves are linear (200 m at 100 mph), so a 60 mph empty train looks ahead ~220 m. **Faster/heavier trains automatically look further.**

This is the only horizon used. It's used in two places:
- `AutoEngineerPlanner.UpdateTargets` (line 453) → bound on the forward `Search`.
- Implicit in the planner cadence: when very fast, the planner ticks every 0.5 s and the search is longer; gas pedal slows when the AI has nothing to plan.

---

## Speed targets — where they come from

In priority order (the actual selection just takes the most restrictive):

| Source | Speed | Sets in `UpdateTargets` |
|---|---|---|
| Mode cap | `mode.MaxSpeedMph()` (Yard=15, Road/Waypoint=45) — clamped by UI helper | `_orders.MaxSpeedMph` |
| Hotbox spotted | 15 mph | line 611 |
| Track speed under us | `MaxSpeedForTrackMph` of the *self* search | line 616 (`maxSpeedMph2`) |
| Track speed near (≤ velocity·5 s ahead) | `MaxSpeedForTrackMph` of the *ahead* search, near segment | line 621 (`maxSpeedMphNear`) |
| Posted segment limit | `segment.speedLimit` (default 35 if 0) | inside `MaxSpeedForTrackMph` |
| Curve limit | `TrainMath.MaximumSpeedMphForCurve(curvature, equipmentMaximumTrackCurvature)` − 3 mph or × 0.8, then floor-to-5, ≥5 mph | inside `MaxSpeedForTrackMph` |
| Signal: Stop (absolute) | 0 at signal | `SetSignalSpeedRestriction(0, ...)` |
| Signal: Stop (intermediate) | "Stop and Proceed" → 15 once stopped within 40 m, else 0 at signal-15 | line 508 |
| Signal: Approach | 15 at next signal, 25 approach (or 25 at this signal if no next) | line 533 |
| Signal: DivergingApproach | same as Approach | |
| Signal: Restricting | 15 (distance-limited) | line 547 |
| Flare | 0 at flare-5, contextual `PassFlare` recorded | line 599 |
| Other car (Avoid/NoCouple) | physics stopping target before car | line 626 |
| Other car (Couple) | 3 mph at car-5 | line 627 |
| Manual stop distance | 0 at distance | line 680 |
| Passenger stop | 0 at stop position | line 649 (via `AutoEngineerPassengerStopper`) |

The minimum across all is taken in `ContextualTargetVelocity`. Notable: **`equipmentMaximumTrackCurvature` is `_coupledCarsCached.Min(car => car.MaximumTrackCurvature)` (line 867)** — the strictest car in the consist sets the curve speed limit for the whole train. A flatcar adjacent to a passenger car gets the passenger car's curve limit.

### `SignalSpeedRestriction` memory (`AutoEngineerPlanner.cs:31`)

When a signal restriction is set, `_lastSignalSpeedRestriction` retains it past the signal so the AI keeps obeying it after passing. Cleared by `ApplyMovement` when `DistanceLimit < 0` (line 1077), `WillMove` (line 1088), `OffDuty`, mode change, or by a `ResumeSpeed` contextual order (line 1041). Signal restrictions can be marked `distanceLimited` (e.g. Stop-and-Proceed, Restricting, Approach without next signal): they have a `DistanceLimit = distanceToSignal + BaseDistanceLimit` after which they self-evict. Non-distance-limited ones (Approach with next signal: 15 mph at next signal) persist until cleared.

---

## Hotbox spotter (`AutoHotboxSpotter`)

The simplest subsystem.

```csharp
private IEnumerator SpotterLoop() {
    while (true) {
        if (!HasCars) { yield return WaitForSeconds(1f); continue; }
        CheckForHotbox();
        while (HasCars) {
            int num = Random.Range(60, 300);            // random 60-300 second cadence
            yield return new WaitForSeconds(num);
            CheckForHotbox();
        }
    }
}
```

(Source: `AutoHotboxSpotter.cs:50`.)

`CheckForHotbox` walks from the engine outward in both directions, checks `Car.HasHotbox` for each car. Newly-found hotboxes are added to `_knownHotboxes`. If any new hotboxes were added this tick → `Say("N hotbox(es) spotted!")`. Sets `HotboxSpotted = _knownHotboxes.Count > 0`.

A separate `RemoverLoop` ticks every 5 s; if no known hotbox still has `HasHotbox`, clears the set.

**Effect on AI:** `AutoEngineerPlanner.UpdateTargets` reads `_hotboxSpotter.HotboxSpotted` and clamps `num5` to 15 mph (line 611). **Once any hotbox is in the spotter's known set, top speed becomes 15 mph until the hotbox is repaired and the remover loop clears the set.** The spotter doesn't auto-stop; it just clamps top speed. Players see the planner status reason "Hotbox: 15 mph".

**Random cadence (60–300 s) means the spotter can take up to 5 minutes to *notice* a new hotbox.** The hotbox itself is spotted instantly in vanilla because the random call seeds before the wait — but if no hotboxes existed when the spotter started, you might travel 5 minutes between checks.

### Patch candidates

| Method | Why patch |
|---|---|
| `AutoHotboxSpotter.CheckForHotbox` | Add other "spottable" conditions (low oil, dragging brakes). |
| `AutoHotboxSpotter.SpotterLoop` | Adjust cadence or replace random delay with deterministic. |
| `AutoEngineerPlanner.UpdateTargets` (the `if (HotboxSpotted)` block at line 611) | Change the 15-mph hotbox clamp to a different policy (stop, slow gradually, ignore). |

---

## `AutoEngineerCrossingSignaler`

Reads `StateManager.Storage.AICrossingSignal` (default `On`). On `SetNextCrossingDistance(value)` from the planner, starts a coroutine that times the horn pattern to reach the crossing at the *end* of the pattern based on current speed. Pattern picked randomly from `_config.crossingWhistlePatterns` (an array of `AnimationCurve`s — horn intensity over time). Diesel locomotives apply `pow(intensity, 4)` to the horn signal.

`EnableHorn => !Planner.IsYardMode` — yards don't blow for crossings.

Notable: bell rings when `num3 < 20f` (within 20 s of crossing) and stays on through the whistle pattern. The `_lastSignalStop` debounce ensures `_config.minimumTimeBetweenCrossingWhistles` (default 5 s) between attempts.

---

## `AutoEngineerFuelAlerter`

Polls `_fuelCar.GetLoadInfo(slot)` every 5 s. For each `RequiredLoadIdentifier` in the load slots, tracks last percentage and posts a `Say` + `PostNotice` ("ai-fuel" / "ai-h2o") when crossing 20% / 10% / 5% / 1%. `FuelCar()` for steam = the tender; for diesel = the locomotive itself. Switch between Fuel/Water notice keys is based on hardcoded load id (`coal`, `diesel-fuel`, `water` — anything else throws `ArgumentOutOfRangeException`).

**Mod gotcha:** if you add a custom load id (e.g. `bunker-c-oil`) to a custom locomotive's slot, `LoadCategoryForLoadId` will throw and the alerter loop will crash (and silently restart via Unity's coroutine error handling, but no notices fire). Patch `LoadCategoryForLoadId` to add custom mappings.

---

## `AutoOiler`

Runs only when the engineer's state is `Stopped` (gated by `_engineer.SetStopped(state == Stopped)` in `AutoEngineer.Loop` — line 396). On stop, configures `_originCar = locomotive`, `_cars = engineer.CachedCoupled()`. The loop walks outward from the engine (alternating direction each pass), 30 s start delay, then 10 s per car walking + 10 s per oil unit applied if `car.NeedsOiling && car.Oiled < 0.75`. Calls `car.OffsetOiled(1 − car.Oiled)`. Bills wages via `StateManager.RecordAutoEngineerRunDuration` based on `_pendingRunDuration * TimeMultiplier` only after a full pass.

Gates: `Car.OilFeature` must be true (the global wear toggle's oil sub-feature; see [Wear › toggle spine](wear-durability.md#toggle-spine-how-wearfeature-propagates)). Diesels are exempt because `Car.EnableOiling` returns false for them.

---

## `AutoEngineerPassengerStopper`

Auto-attached when the consist contains any `IsPassengerCar()` and the mode is not Yard (`AutoEngineerPlanner.cs:874`). Driven by:
- `_passengerStopper.UpdateCars(coupledCars)` — refreshes `_cachedHasCoaches`, `_cachedPassengerStopIds` (the union of all `PassengerMarker.Destinations` on coupled cars).
- `_passengerStopper.UpdateFor(maybeAhead, maybeUnder, stoppedDuration, bypassStationCode)` — called from `UpdateTargets` (line 658).

The stopper computes the *stop position* via `FindStopDistanceForIdentifier` — finds the centroid of cars whose `PassengerMarker.Destinations` contains the stop identifier; falls back to the centroid of all passenger cars. Adds a 0 mph target at the centroid distance.

**Timetable integration**: if `_timetableTrain != null` (set by `SetTimetableTrain` after a `Messenger.Default.Send(default(TimetableDidChange))`), the stop is governed by timetable departure time. `ShouldStayStopped` holds the train at the platform until departure time (or the timetable is bypassed via contextual order). At the **last** timetable stop, the AI auto-issues `WithMaxSpeedMph(0)` and posts notice "Timetable schedule complete."

`MinimumStopDuration` (default 60 s) is read from `StateManager.Storage.AIPassengerStopMinimumStopDuration`. `Enable` is read from `StateManager.Storage.AIPassengerStopEnable` (default true).

---

## Waypoint routing (`AutoEngineerPlanner.RouteLoop` and friends)

When `_orders.Mode == Waypoint && _orders.Waypoint.HasValue`, a second coroutine ticks once per second. It:

1. **Builds the route** via `Graph.FindRoute(start, end, HeuristicCosts.AutoEngineer, ..., trainMomentum)`. `trainMomentum = _config.momentumFactor * stoppingDistance + _config.momentumOffset` (defaults 2 × stoppingDistance + 50 m). The momentum is the cost-buffer that makes the search prefer routes the train can actually decelerate into without slamming brakes.
2. **First does a `checkForCars: false` pass** to find any route at all; if even that fails, sends user "Unable to find a path to waypoint." If found but the car-aware pass fails, sends "Route to waypoint is blocked." Either way, `_routeRequester` (the original `AutoEngineerCommand` sender) gets the message via `Multiplayer.SendError`.
3. **Throws switches as needed** in `TickRoute` (`AutoEngineerPlanner.cs:1174`):
   - For each upcoming step that names a switch node, computes the desired thrown state via `TryGetDesiredSwitchSetting`.
   - Throws via `TrySetSwitch` → `TrainController.TrySetSwitch(node.id, ...)` only if `CanSetSwitchAtStep` returns true (orders aren't stop, not stopped at station, no fouling flare/signal in the area via `IsSwitchBlockerPresent`).
   - **Recipe for failure**: a CTC switch that is not unlocked (`IsCTCSwitchUnlocked == false`) is silently skipped. The AI plows on; if the switch is against, the planner's `Search` truncates and the AI stops with a `CTCSwitchLocked` announce. There's no "wait for dispatcher" loop; the AI just sits.
   - Switches the AI throws are recorded in `_routeSwitchesToRestore`. After the train passes them, `RestorePassedSwitchesToOriginalPosition` resets to the original setting (so manual switching state isn't permanently disturbed).
4. **`UpdateStartStepIndex`** (`AutoEngineerPlanner.cs:1773`): walks forward through the route comparing each step's position dot-product against the locomotive's facing. Steps "behind" the loco get consumed; the first step "ahead" becomes `_startStepIndex`. If a `SearchLimit` step is consumed (the route was truncated at search distance), forces a full reroute (with extra momentum if the limit was at a CTC switch).
5. **`RerouteIfNotOnCurrentRoute`**: if the train deviates (e.g. switch thrown manually onto wrong route), reroutes. Debounced to once per 10 s real time.
6. **`IsWaypointSatisfied`**: target reached if `IsUnderTrain(routeTargetLocation, 0)` AND (if `WantsCouple`) the named car id is in the consist. On satisfied → `RestoreAllRemainingSwitches`, post "Arrived at waypoint!", clear waypoint from Orders.
7. **Sets `_manualStopDistance` from accumulated route distance** every tick, with `*= -1` if the route is behind the loco (auto-handles initial-direction setting via `SetDirection`).

### Key route-related tunables (in `AutoEngineerConfig`)

```csharp
public float momentumFactor = 2f;                // multiplier on stopping distance
public float momentumOffset = 50f;               // base meters
public float momentumRerouteAtCtcSwitch = 2000f; // extra momentum when forced to reroute at CTC
```

---

## `AutoEngineer` PID controller specifics

`PIDController` (`Model.AI/PIDController.cs`) is a standard PID with:
- `errorRange`, `integratorRange`, `outputRange` clamps.
- `integralGrowth` (multiplier on `error * dt` when accumulating) and `integralDecay` (decay rate per second).
- `maximumStep` per tick on the output (rate-limit).

The `_config.throttlePID` etc. are templates; `AutoEngineer.OnEnable` does `_config.throttlePID.CopyTo(throttleController)` so each loco has its own integrator state.

**ResetPIDs ContextMenu** (`AutoEngineer.cs:280`) — handy debugging knob. Also called at the top of `MaintainSpeed`, so every state transition into Running clears integrators. Means the PID has no long-term memory across stops.

---

## Patch candidates summary

| Method | Why patch |
|---|---|
| `AutoEngineerPlanner.UpdateTargets` | The whole plan-construction. Override to inject custom targets (e.g. terrain-aware speed limits, bridge restrictions). |
| `AutoEngineerPlanner.HandleCommand` | Add side-effects on order changes; or veto/transform incoming commands. |
| `AutoEngineerPlanner.HandleContextualOrder` | Add new contextual orders. |
| `AutoEngineerPlanner.Search` (private static) | Replace the lookahead algorithm. Risky — many invariants. |
| `AutoEngineerPlanner.MaxSpeedForTrackMph` (local in Search) | Replace track speed evaluation (mod-side speed limits, weather, etc.). NOT directly accessible — mirror the function in your patched `Search`. |
| `AutoEngineerPlanner.OrdersDidChange` | Hook AI on/off transitions to add custom subsystems. |
| `AutoEngineerPlanner.UpdateCars` | Catch consist changes; useful for mods that maintain per-consist state. |
| `AutoEngineerPlanner.ShouldStopForPitfall` | Add new pitfall conditions (low fuel-pressure, derailed nearby, etc.). |
| `AutoEngineer.MaintainSpeed` | Replace the inner PID loop (custom controller). |
| `AutoEngineer.ContextualTargetVelocity` | Replace the kinematic decision — e.g., add jerk limits, energy-aware acceleration. |
| `AutoEngineer.CalculateLookaheadDistance` | Adjust horizon (e.g., look further on downgrades). |
| `AutoEngineer.ShouldUseLocomotiveBrake` | Force always-train-brake or always-independent regardless of consist. |
| `AutoEngineer.CalculateTotalAvailableBraking` | Inject mod-side braking forces (custom brake car types, dynamic brakes). |
| `AutoEngineer.EmergencyStop` | Catch the velocity-zero teleport — a place to log or veto for unrealistic decelerations. |
| `AutoEngineer.FixMuCutOutIfNeeded` | The AI auto-disables MU and disables Cut-Out for solo locos; patch to keep player MU intent. |
| `AutoHotboxSpotter.SpotterLoop` | Replace the random cadence with something deterministic / event-driven. |
| `AutoEngineerCrossingSignaler.CrossingCoroutine` | Replace whistle pattern logic. |
| `AutoEngineerFuelAlerter.LoadCategoryForLoadId` | **Required** to support custom fuel load ids. |
| `AutoEngineerPassengerStopper.UpdateFor` | Customize when/where to stop. |
| `AutoEngineerPassengerStopper.ShouldStayStopped` | Tweak departure logic; bypass timetable conditions. |
| `AutoOiler.Loop` | Replace oil-walk timing/effect; add modeled walking distance. |
| `BaseLocomotive.Awake` (the `IsHost && GetComponent<AutoEngineer>() == null` check) | Replace `AutoEngineerPlanner` entirely with a custom subclass — must be done before this method runs. |

### Replacing the planner

Two viable strategies:

1. **Swap the brain, keep the engineer.** Patch `BaseLocomotive.Awake` postfix to destroy `AutoEngineerPlanner` and add your own. Drive `_engineer.SetTargets(...)` directly. The engineer side is reasonably self-contained and works with any source of `Targets`.
2. **Swap the engineer, keep the planner.** Patch `AutoEngineerPlanner.Awake` prefix to skip `_engineer = AddComponent<AutoEngineer>()`, add your own. Subscribe to `SetTargets` calls (or wrap the `_engineer` reference). Harder — `AutoEngineer` exposes much state to the planner internally (`Run`, `WaitingForBrakes`, `HandbrakeApplied`, `BrakeLineTogether`).

Either way, **clients never run any of this**. A custom AI must run host-only and broadcast results via the existing KVO keys (or new ones).

---

## MP authority

| Action | Path | Auth |
|---|---|---|
| Player issues AI orders | `AutoEngineerCommand` → `TrainController.HandleAutoEngineerCommand` → `AutoEngineerPlanner.HandleCommand` | `Crew` (message-level) |
| Player pass-signal/flare/etc. | `AutoEngineerContextualOrder` → `HandleAutoEngineerContextualOrder` → `HandleContextualOrder` | `Crew` |
| Player force reroute | `AutoEngineerWaypointRerouteRequest` → `HandleAutoEngineerWaypointRerouteRequest` | `Crew` |
| Client requests route data | `AutoEngineerWaypointRouteRequest` → `HandleAutoEngineerWaypointRouteRequest` → host responds with `Response` | `Crew` (request) / `HostOnly` (response) |
| Host pushes route updates | `AutoEngineerWaypointRouteUpdate` (broadcast) | `HostOnly` |
| Planner writes `aiOrders`, `aiCtxOrders`, `aiManualStopDistance`, `aiPlannerStatus`, `aiPassModeStatus` | KVO key writes | **Default crew + train-crew** (no host prefix) |
| Planner writes `aiOrders` in response to a command | Inside `HandleCommand` (host-only since `IsHost` gate at TrainController level) | Effectively HostOnly via guard |
| Planner runs at all | `BaseLocomotive.Awake` adds it `if (StateManager.IsHost ...)` | **HostOnly** |
| Engineer writes `Throttle`, `TrainBrake`, `LocomotiveBrake`, `Reverser`, `Bell`, `Horn`, `Headlight`, `CylinderCocksOpen` | via `LocomotiveControlHelper` / `BailOff` | These ride on the locomotive's control properties. The engineer runs host-only, so writes propagate as host writes regardless of underlying KVO auth. |

**Only the host runs the AI loop. Clients see results via KVO observers.** A client whose locomotive is being driven by the host's AI sees `aiOrders` change, `aiPlannerStatus` change, and the underlying control properties (`Throttle`, etc.) change as KVO updates from the host. The client never runs the planner itself.

**There is no "AI off" message.** To disable the AI, send `AutoEngineerCommand(LocomotiveId, AutoEngineerMode.Off, ...)` — the planner's `OrdersDidChange` notices `Enabled` flipped to false and stops its coroutine, runs `OffDuty()`. **Do NOT directly write `aiOrders = null` from a client** — works (default-crew auth permits it) but the planner side will see `_orders = Orders.Disabled` and call `OrdersDidChange`, which is fine, but it bypasses `HandleCommand`'s side-effects (manual-stop reset, route requester capture). Use the message.

---

## Related Messenger / KVO events

| Event | Direction | Where |
|---|---|---|
| KVO `aiOrders` (dict or null) | host→all | Written by `HandleCommand`, read by Planner observer + UI |
| KVO `aiCtxOrders` (array) | host→all | Written by `UpdateTargets` (line 711) for current contextual orders |
| KVO `aiManualStopDistance` (float or null) | host→all | Written by `Messenger<WorldWillSave>` handler (`AutoEngineerPlanner.cs:275`) — only persisted on save |
| KVO `aiPlannerStatus` (string or null) | host→all | Written by `SetPlannerStatus` (line 786) every plan tick |
| KVO `aiPassModeStatus` (string or null) | host→all | Written by `AutoEngineerPassengerStopper.UpdateFor` |
| `AutoEngineerWaypointRouteUpdate` | host broadcast | `FireChangeMessage` (`AutoEngineerPlanner.cs:1830`) when route or current-step changes |
| `AutoEngineerWaypointRouteResponse` | host→requester | Reply to `AutoEngineerWaypointRouteRequest` (`TrainController.cs:2148`) |
| `Messenger<WorldWillSave>` | local | Planner writes `aiManualStopDistance` on save |
| `Messenger<TimetableDidChange>` | local | Planner re-binds passenger stopper's timetable train |

---

## Gotchas

- **`AutoEngineer` is added inside `AutoEngineerPlanner.Awake`, not in `BaseLocomotive`.** If you try to `GetComponent<AutoEngineer>()` from another `Awake`, you'll get null on the first frame. The planner itself is added in `BaseLocomotive.Awake` *only on the host*; a client never has either.
- **The `WearFeature` toggle does not affect the AI's hotbox response.** `AutoHotboxSpotter` reads `Car.HasHotbox` (a KVO key the host sets in `Car.CheckForHotbox` — gated by oil/wear). With wear off, hotboxes never appear → spotter never trips → 15 mph clamp never applies. Coherent, but worth noting if you add mod hotbox triggers.
- **`AutoEngineerCommand.MaxSpeedMph` is NOT clamped server-side.** The UI clamps via `AutoEngineerOrdersHelper.SetOrdersValue`. A direct sender (e.g., a mod) can set 99 mph in Yard mode and the planner will obey it (subject to track speed limits and curve limits, of course). If your mod issues commands, mirror the UI clamp.
- **Planner cadence is speed-dependent and asymmetric.** When the train is moving slowly, the planner ticks every 3 s. A switch thrown by a player 50 m ahead might not be noticed for 3 s after it's set — long enough for an issue at 35 mph. Fast trains tick every 0.5 s, which is fine.
- **Two coroutines, two keepalives.** The planner has its own `_loopKeepalive`; `AutoEngineer` has its own. An exception inside `AutoEngineer.MaintainSpeed` will silently restart that coroutine after 60 s while the planner keeps ticking happily. You'll see "PostStopNotice" stuck or the engineer "deaf" to new targets.
- **`FixMuCutOutIfNeeded` (`AutoEngineer.cs:843`) auto-disables MU when the AI is on.** Player MU intent is overridden every tick. There's no way to MU-control multiple AIs from one cab.
- **`EmergencyStop` calls `Locomotive.set.SetVelocity(0f, ...)`.** This is a teleport-velocity-to-zero, not a brake force. Mods that intercept `IntegrationSet` velocity for custom physics will see the AI snap to zero unrealistically. Only triggered when a stop target is more than 3 m behind the loco AND has a `StopAnnounce`.
- **`ShouldUseLocomotiveBrake` returns true for light engine moves.** Heavy locomotives running solo brake using `LocomotiveBrake` only — ignoring train brake. If your mod adds, say, a brake car that *should* be air-controlled even with one locomotive, you must clear `air.DefersToLocomotiveAir` for it.
- **Curve speed: equipment-min, not consist-mean.** `_equipmentMaximumTrackCurvature = _coupledCarsCached.Min(...)` — adding one fragile car forces the whole train to that car's curve speed.
- **`Search` step is fixed at 10 m.** No way to make the lookahead "finer" near switches without re-running the search. Patches that need higher resolution should re-walk the relevant segment.
- **`Search` reuses the SwitchAgainst exception flow as control flow.** Catching that exception is part of the algorithm — not an error. Don't add a Harmony patch that swallows `SwitchAgainstMovement` thinking it's a bug.
- **`SearchMode.Self` reuses `Search` but only walks `_maximumLength` meters and `OtherCarHandling.Avoid` semantics.** It will report `OtherTrain` if any car is found in the self search — which would be a derailed extra car under us. The planner suppresses `StopAnnounce.OtherTrain` in `Couple` mode but not in others (line 482).
- **`HandleCommand` for Yard mode sets `_manualStopDistance = 0` if entering Yard with no Distance.** This means *immediately stop*. Yard mode requires explicit movement intent every time. UI helper handles this; direct command senders should always include a Distance.
- **Waypoint mode auto-clears the waypoint on arrival** by writing a new `Orders` with `Waypoint = null` (`AutoEngineerPlanner.cs:1196`). Mode stays at Waypoint. Mods watching the order stream see Mode=Waypoint, Waypoint=null — a transient state.
- **`AutoEngineerPassengerStopper` is added/removed dynamically inside `UpdateCars`.** Holding a long-lived reference to it is dangerous. Subscribe to `AutoEngineerPersistence.ObservePassengerModeStatusChanged` instead.
- **The AI's `Say` writes to `Multiplayer.Broadcast`** (`AutoEngineerPlanner.cs:973`). Every announcement is a chat message. There's no "AI silent mode."
- **`HandleCommand` resets `_lastSignalSpeedRestriction = null`.** Issuing any new command (even a no-op speed bump) clears signal memory. The next tick rebuilds it from search results. Generally fine but means rapid command churn = no signal-restriction memory.
- **`_persistence` is a struct, accessed via `ref`.** `internal ref AutoEngineerPersistence Persistence => ref _persistence;` — code reads/writes through the ref. Mods that access `Persistence` from a non-host or after `OnDisable` will hit a null `KeyValueObject`.
- **Routing failure messages go only to the original sender.** `_routeRequester` is captured per-`HandleCommand` and consumed by `SendMessageToRouteRequester` (which nulls it). A second player who'd like to know the route is blocked won't be told. Use `Multiplayer.Broadcast` from a patch if you want broader notification.
- **`PIDController.Reset` is called every time `MaintainSpeed` starts.** No long-term integrator memory. The "leaky integrator" parameter (`integralDecay`) is the only continuous-time damping.
- **The 60 s keepalive (`CoroutineKeepalive(60f, scaledTime: true)`) uses scaled time.** When game time is paused, the keepalive timer doesn't advance — but the coroutine *also* doesn't tick. So in practice this is fine. If a mod runs simulation at reduced rate, the keepalive may fire spuriously.
- **`AutoOiler` only runs when the engineer is `Stopped`** (`SetStopped(true)` from `AutoEngineer.Loop` at line 396). Trains parked with handbrakes-only (so the engineer is OFF entirely) don't get oiled by AI.
- **`HotboxSpotted` is only consulted by the planner — not by the engineer.** A hotbox while AI is off has no effect. The engineer doesn't auto-shut-down on hotbox.
- **`OffDuty` clears `_calledSignal`, `_contextualIgnore*`, but does NOT clear `_lastSignalSpeedRestriction`.** That's only cleared by `WillMove`, mode change inside `HandleCommand`, distance-limit expiry, or `ResumeSpeed`. Quick AI-off-then-on cycle can preserve the restriction. Probably intentional.

---

## Init order

1. `BaseLocomotive.Awake` — host-only; adds `AutoEngineerPlanner` if no `AutoEngineer` already exists. (`Model/BaseLocomotive.cs:426`)
2. `AutoEngineerPlanner.Awake` — adds `AutoEngineer` (which internally adds `AutoOiler`).
3. `AutoEngineerPlanner.OnEnable` — initializes `_persistence`, subscribes `_ordersObserver` and `_manualStopObserver`. The order observer's first call (with `callInitial=true`) sets `_orders = stored value` and immediately runs `OrdersDidChange`.
4. If `_orders.Enabled` from save, `OrdersDidChange` adds the subsystem components (`crossingSignaler`, `fuelAlerter`, `hotboxSpotter`) and starts the planner Loop.
5. `AutoEngineer.OnEnable` — copies PID configs from `_config`, creates `_oiler`, starts its own loop.

**A patch on `BaseLocomotive.Awake` postfix runs AFTER the host check and AFTER the planner is added.** If you want to *prevent* planner creation, patch a prefix; if you want to subclass, replace the planner postfix.

**`Awake` order between siblings is not guaranteed.** If your mod adds another `MonoBehaviour` to the locomotive that calls `GetComponent<AutoEngineerPlanner>()` in its own `Awake`, the planner may or may not exist yet. Use `OnEnable` or `Start` for safer access.

---

## Cross-references

- Track speed limits / curve limits / `TrackSegment.speedLimit` semantics: see `physics-vanilla-survey.md` § Speed limits.
- `Car.HasHotbox` semantics, oil propagation, `OilFeature` toggle: see [Wear › Hotbox](wear-durability.md#hotbox), [Wear › Oil](wear-durability.md#oil).
- Brake forces summed in `CalculateTotalAvailableBraking` come from `Car.CalculateBrakingForce`: see `physics-vanilla-survey.md` § Brake forces (and a future `brakes.md` crib sheet, when written).
- Coupling intent in `Couple` mode triggers the same `IntegrationSet.Couple` path as manual coupling: see [Couplers › auto-couple](couplers.md#auto-couple-impact-driven). Planner sets `OtherCarHandling.CoupleTo` and adds a 3 mph target 5 m before the car; the actual physical coupling event still happens in the integration solver.
- Signals (`CTCSignal.LastShownAspect`), CTC switch lock state (`IsCTCSwitchUnlocked`), and dispatch interaction: future `signals-dispatch.md` crib sheet. The planner consumes `CTCSignal.LastShownAspect` and `CTCInterlocking` references.
- Route search (`Graph.FindRoute`, `RouteSearch.Step`, `HeuristicCosts.AutoEngineer`): future `track-topology.md` crib sheet. The waypoint loop is one of two consumers of `Graph.FindRoute` (the other being `RouteSearch` test code).
