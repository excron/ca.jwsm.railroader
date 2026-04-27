# Couplers — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Wear & Durability](wear-durability.md)

Couplers in Railroader are split across three layers: the visual `Coupler` MonoBehaviour (animation + audio only), the per-end `Car.EndGear` state struct (`IsCoupled`/`IsAirConnected`/`Anglecock`/`CutLever`), and the consist-level `IntegrationSet` constraint solver (slack stretch, coupling/decoupling triggers, collision events). There is **no force vector ever computed for a coupler** — the only "force" output is `deltaVelocity` at slack-direction reversals, fed into `IIntegrationSetEventHandler.IntegrationSetCarsDidCollide`. The collision callback is the only damage producer that physically links to coupler state. Everything else is bookkeeping over four KVO keys per end.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Car.EndGear` | `Model/Car.cs:122` | Per-end state struct: `IsCoupled`, `IsAirConnected`, `AnglecockSetting`, `Coupler` ref |
| `Car.ApplyEndGearChange(LogicalEnd, EndGearStateKey, bool/float)` | `Model/Car.cs:2695, 2712` | Single chokepoint for coupler/air/anglecock state writes |
| `Car.HandleCouplerClick(Coupler)` | `Model/Car.cs:2552` | Player-clicked uncouple; runs cut-lever logic |
| `IntegrationSet.IntegrateConstraints(float dt)` | `Model.Physics/IntegrationSet.cs:479` | Slack solver; detects auto-couple impact and SlackStretch reversals |
| `TrainController.IntegrationSetCarsDidCollide(...)` | `TrainController.cs:1191` | Host-side collision handler — applies wear + derailment force |
| `IIntegrationSetEventHandler` | `Model.Physics/IIntegrationSetEventHandler.cs` | Abstraction for set-level events; `TrainController` is the only impl |
| `RollingStock.Coupler` | `RollingStock/Coupler.cs` | Visual + audio MonoBehaviour. Holds `car`, `end`, animation; **no physics** |
| `RollingStock.CutLever` | `RollingStock/CutLever.cs` | Pickable lever; fires `OnActivate` → routed to `HandleCouplerClick` |
| `Game.Messages.PropertyChange` keys `_f.coupled` / `_r.coupled` | `Model/Car.cs:2750-2778` | Wire format for coupler state |

---

## Coupler model

### Two layers: visual vs. logical

There is no shared "coupler model" type. The **visual** coupler (`RollingStock.Coupler`) is a MonoBehaviour created by `Car.SetupCouplers` (one per `End.F`/`End.R` if `WantsEndGear(end)`). The **logical** coupler state lives on `Car.EndGear` per logical end, persisted via four KVO keys.

### `RollingStock.Coupler` — the visual layer

```csharp
public class Coupler : MonoBehaviour {
    public const float Inset = -0.276f;
    public Car            car;
    public Car.End        end;
    public CouplerPickable pickable;
    public AudioClip      audioClipClose;
    public AudioClip      audioClipOpen;
    public AnimationClip  openCloseAnimationClip;
    public Animator       animator;

    public AudioClip slackInClip;
    public AudioClip slackOutClip;

    public void SetOpen(bool open);                     // animation + audio
    public void SlackIn(float slackDiffNormalized);     // one-shot audio
    public void SlackOut(float slackDiffNormalized);
    public void SetVisible(bool visible);
}
```

Awake hooks `pickable.activate = () => car.HandleCouplerClick(this)` (`Coupler.cs:45`). That's the entire MonoBehaviour responsibility — no state, no physics, no networking.

#### Patch candidates (Coupler)

| Method | Why patch |
|---|---|
| `Coupler.SlackIn` / `Coupler.SlackOut` | Hook for slack audio events (mod-side slack visualization). Triggered by `TrainController.RequestSlackSound` via `ScheduledAudioPlayer`, NOT directly by Coupler. Patching here only catches local-machine playback, not the underlying event — patch `IntegrationSetCarsDidCollide` for that. |
| `Coupler.SetOpen` | Animation/audio swap. |

### `Car.EndGear` — the logical layer

```csharp
public class EndGear {                                  // Car.cs:122
    public Anglecock Anglecock;
    [CanBeNull] public Coupler Coupler;
    public CutLever  CutLever;
    public bool      IsCoupled;
    public bool      IsAirConnected;
    public float     AnglecockSetting;
    public float     AirPressure;
    public bool      NeedsConnectionUpdate;

    public bool IsAirConnectedAndOpen => IsAirConnected && IsAnglecockOpen;
    public bool IsAnglecockOpen       => AnglecockSetting > 0.1f;
    public void SetConnectedTo(EndGear other);
    public void Populate(Anglecock prefab, Transform parent, Vector3 airHosePosition);
    public void Depopulate();
}
```

Two `EndGear` instances per car: `EndGearF` (front-end of the model, private) and `EndGearR` (rear-end of the model, private). The **public** addressing is by *logical* end:

```csharp
public  EndGear EndGearA  => this[LogicalEnd.A];
public  EndGear EndGearB  => this[LogicalEnd.B];
public  EndGear this[LogicalEnd end];                   // Car.cs:871
private EndGear this[End end];                          // Car.cs:878
```

`LogicalEnd.A`/`B` is consist-direction-independent; `End.F`/`R` is body-relative. `Car.FrontIsA` (bool) determines mapping. Helpers: `Car.LogicalToEnd(LogicalEnd)` / `Car.EndToLogical(End)`.

### State key enum

```csharp
public enum EndGearStateKey { IsCoupled, IsAirConnected, Anglecock, CutLever }   // Car.cs:81
```

---

## State writes: `ApplyEndGearChange` is the only door

```csharp
public void ApplyEndGearChange(LogicalEnd logicalEnd, EndGearStateKey key, bool boolValue);  // Car.cs:2695
public void ApplyEndGearChange(LogicalEnd logicalEnd, EndGearStateKey key, float f);          // Car.cs:2712
```

Both convert to body-end via `LogicalToEnd`, build the KVO key (`KeyValueKeyFor`), and write to `KeyValueObject`. The bool overload calls `ValidateEndGearChange` first; the float overload skips validation.

### KVO key naming

`KeyValueKeyFor(EndGearStateKey, End)` (`Car.cs:2750`):

| Key | End.F string | End.R string | HostOnly? |
|---|---|---|---|
| `IsCoupled` | `_f.coupled` | `_r.coupled` | **Yes** (leading `_`) |
| `IsAirConnected` | `_f.airConnected` | `_r.airConnected` | **Yes** (leading `_`) |
| `Anglecock` | `f.anglecock` | `r.anglecock` | No (Crew + train-crew check) |
| `CutLever` | `f.cutLever` | `r.cutLever` | No (Crew + train-crew check) |

Auth resolved by `Car.AuthorizationRequirementForPropertyWrite` (`Car.cs:3112`); see the prefix arrays at `Car.cs:467-473`.

### Validation

```csharp
private bool ValidateEndGearChange(End end, EndGearStateKey key, bool boolValue)   // Car.cs:2719
```

Returns false (rejecting the change) when:
- Setting `false` and there's no adjacent car at that end → still **returns true** (allowed); the early branch is `if (!boolValue && !AnyCarAdjacent(end)) return true;`. Actually permissive.
- `RequiresConnectionToEnd(end)` is true and `boolValue` is false → return false. This guards tenders' front-end (`Car.cs:2741-2748`) — you cannot uncouple a tender from its engine.

The float overload does NOT validate; anglecock/cut-lever values pass straight through.

### KVO observers (apply incoming changes)

`Car.SetupKeyValueObject` (`Car.cs:1642`) wires:

```csharp
KeyValueObject.Observe("_f.coupled",        v => HandleCoupledChange(End.F, v.BoolValue));    // 1645
KeyValueObject.Observe("_r.coupled",        v => HandleCoupledChange(End.R, v.BoolValue));    // 1649
KeyValueObject.Observe("_f.airConnected",   v => HandleAirConnectedChange(End.F, v.BoolValue));// 1653
KeyValueObject.Observe("_r.airConnected",   v => HandleAirConnectedChange(End.R, v.BoolValue));// 1657
KeyValueObject.Observe("f.anglecock",       v => EndGearF.AnglecockSetting = v.FloatValue);    // 1661
KeyValueObject.Observe("r.anglecock",       v => EndGearR.AnglecockSetting = v.FloatValue);    // 1665
KeyValueObject.Observe("f.cutLever",        v => HandleCutLeverValue(End.F, v.FloatValue));    // 1669
KeyValueObject.Observe("r.cutLever",        v => HandleCutLeverValue(End.R, v.FloatValue));    // 1673
```

```csharp
public void HandleCoupledChange(End end, bool isCoupled)                   // Car.cs:2680
{
    this[end].IsCoupled = isCoupled;
    PositionCoupler(EndToLogical(end));        // re-pose visual coupler
    if (!isCoupled) ResetAtRest();
}

public void HandleAirConnectedChange(End end, bool b)                      // Car.cs:2690
{
    this[end].IsAirConnected = b;
}
```

Both run on **every** KVO write — host (Local origin) and client (Remote origin). Subscribing to these keys via your own observer is a clean way to emit coupler events.

---

## Cut lever pipeline (player-driven uncouple)

```
CutLever pickable click
    ↓ ContinuousControl.OnValueChanged (>0.5)
    ↓ CutLever.OnActivate (Action)
    ↓ subscribed in Car.SetupCutLevers → HandleCouplerClick(EndGearF.Coupler)
Car.HandleCouplerClick(coupler)                            // Car.cs:2552
    ↓ ApplyEndGearChange(LogicalEnd.X, EndGearStateKey.CutLever, 1f)
    ↓ (writes f.cutLever = 1)
    ↓ KVO observer fires → HandleCutLeverValue(End, value)  // Car.cs:2577
    ↓ HandleOpenCoupler(logicalEnd)                          // Car.cs:2590
    ↓   if (StateManager.IsHost && this[logicalEnd].IsCoupled) {
    ↓       ApplyEndGearChange(B-side, IsCoupled, false)     // both sides cleared
    ↓       ApplyEndGearChange(A-side, IsCoupled, false)
    ↓   }
    ↓ LeanTween.delayedCall(1f, …) → ApplyEndGearChange(end, CutLever, 0f)
```

Two KVO writes per click: cut-lever raise then 1-second delayed lower. The actual `IsCoupled=false` write happens host-side only inside `HandleOpenCoupler`. **Client-clicked uncoupling works because the cut-lever KVO is non-HostOnly** — the client sets `f.cutLever = 1`, broadcasts via PropertyChange, host's KVO observer fires, host runs `HandleOpenCoupler`, host writes `_f.coupled = false`, broadcasts back.

`HandleCouplerClick` also handles the "smart air helper" modifier (`GameInput.SmartAirHelperModifier`) which simultaneously closes both anglecocks (`Car.cs:2554, 2559-2562`).

### CutLever (the MonoBehaviour)

```csharp
public class CutLever : MonoBehaviour {                    // CutLever.cs
    public ContinuousControl control;
    public event Action OnActivate;
    // _primed bistable: fires OnActivate once when value>0.5; resets at <0.1
}
```

Edge-triggered, debounced. `Car.SetupCutLevers` (`Car.cs:1806`) instantiates one per end and subscribes.

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.HandleCouplerClick(Coupler)` | Intercept all player uncouple intents (mouse + cut-lever both route here). Prefix to veto, postfix to log. |
| `Car.HandleOpenCoupler(LogicalEnd)` | Host-side actual uncouple. Patch here to add side-effects (e.g., damage on rough uncouple). |
| `Car.HandleCutLeverValue(End, float)` | Catch the KVO-driven path before `HandleOpenCoupler`. |
| `CutLever.OnActivate` event | Subscribe rather than patch — but only catches local cut-lever clicks, not consist topology changes. |

---

## Auto-couple (impact-driven)

Inside `IntegrationSet.IntegrateConstraints` (`Model.Physics/IntegrationSet.cs:479`):

```csharp
float num2 = nextPos - nextRadius - (curPos + curRadius);   // gap between cars
if (num2 < 1f)                                               // overlap
{
    num3 = 1f - num2;
    if (!AreCoupled(element, element2))
    {
        float deltaV = Math.Abs(element.Velocity - element2.Velocity) / wholeDeltaTime;
        if (deltaV > 0.22351964f)                            // 0.5 mph
            Couple(element, element2, deltaV);               // event fires
    }
    if (element.SlackStretch < 0f) { element.SlackStretch = 0f; element.SlackStretchDidChangeDirection = true; }
    element.SlackStretch += num3;
}
```

Threshold: `0.22351964 m/s` ≈ 0.5 mph. Below that, cars touching just compress slack without auto-coupling.

```csharp
private void Couple(Element entry0, Element entry1, float deltaVelocity)   // IntegrationSet.cs:701
{
    EventHandler.IntegrationSetDidCouple(entry0.car, entry1.car, deltaVelocity);
    EventHandler.IntegrationSetCarsDidCollide(entry0.car, entry1.car, deltaVelocity, isIn: true);
    LogSet("DidCouple");
}
```

`Couple` always fires `IntegrationSetCarsDidCollide` immediately after — so auto-couples damage cars on impact via the same path as slack-reversal collisions.

### Host handling of `IntegrationSetDidCouple`

```csharp
public void IntegrationSetDidCouple(Car car0, Car car1, float deltaVelocity)   // TrainController.cs:1170
{
    if (!IsHost) return;
    if (!graph.CheckSameRoute(car0.LocationB, car1.LocationA, 2f))     // route mismatch
    { Log.Warning("Rejecting couple..."); return; }
    if (car0.IsDerailed || car1.IsDerailed)                            // derailed
    { Log.Warning("Rejecting couple..."); return; }

    car0.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsCoupled, true);
    car1.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsCoupled, true);
}
```

Three rejection paths:
1. Different track routes (cars on parallel tracks colliding in 3D space but not topologically linked).
2. Either car derailed.
3. Not host.

The collision damage call **still fires** even when coupling is rejected (it runs first in the `Couple` lambda before the host-side filter). So a derailed car can still be damaged by a collision attempt.

### Patch candidates

| Method | Why patch |
|---|---|
| `IntegrationSet.IntegrateConstraints` | The full slack solver. Patching is risky (per-tick, per-set, hot path); prefer subscribing via `IIntegrationSetEventHandler`. |
| `IntegrationSet.Couple` (private) | Skip the immediate collision call, customize coupling logic. |
| `TrainController.IntegrationSetDidCouple` | Add couple-time effects (logging, broadcast events, conditional rejection). HostOnly already; patch prefix to add gates. |

---

## Slack state & integration

`IntegrationSet.Element` (`IntegrationSet.cs:17`) holds slack state per car:

```csharp
public readonly float SlackA;                           // per-end slack tolerance, set in ctor
public readonly float SlackB;
public readonly float CarRadius;                        // = car.carLength / 2
public float SlackStretch;                              // <0 compressed, >0 in tension
public bool  SlackStretchDidChangeDirection;            // collision-event trigger
```

### Slack tolerance source

```csharp
public float CouplerSlack(End end)                      // Car.cs:1775
{
    return end switch {
        End.F => WantsEndGear(End.F) ? 0.02f : 0.001f,
        End.R => WantsEndGear(End.R) ? 0.02f : 0.001f,
        _ => throw new ArgumentOutOfRangeException(...),
    };
}
```

Constants only: 2 cm with end gear, 1 mm without. **No definition-driven slack** — every car has the same tolerance unless `WantsEndGear` returns false (e.g., tender front, see `Car.cs:1761`). Override `WantsEndGear` virtual to skip end gear; override `CouplerSlack` virtual for per-class slack.

### SlackStretch update logic

In `IntegrateConstraints`:
- Cars too close (`gap < 1f`): `SlackStretch += (1 - gap)`. If was negative (tension), reset to 0 and flag direction change.
- Coupled, cars too far (`gap > 1 + slackA + slackB`): `SlackStretch += (slack - gap)` (becomes negative). If was positive (compression), reset to 0 and flag direction change.
- Uncoupled and air-only, `gap > 1.5`: break air hoses (`BreakAirHoses`).

### Direction-change → collision event

`IntegrationSet.PositionCars` (`IntegrationSet.cs:208-216`):

```csharp
if (element.SlackStretchDidChangeDirection
    && Mathf.InverseLerp(0.001f, 0.006f, Mathf.Abs(element.SlackStretch)) > 0.1f)
{
    bool isIn = element.SlackStretch > 0f;
    Car nextCar = _elements[_elements.IndexOf(element) + 1].car;
    float deltaV = Mathf.Abs(VelocityA(element.car) - VelocityA(nextCar));
    EventHandler.IntegrationSetCarsDidCollide(element.car, nextCar, deltaV, isIn);
}
```

So each tick can fire one collision event per car-pair when slack reverses. `isIn = true` means stretch went compressive (slack-in slam); `false` means tension (slack-out yank).

Boundary collisions (against deadends or off-route cars) also fire `IntegrationSetCarsDidCollide` with `car1=null`:

```csharp
EventHandler.IntegrationSetCarsDidCollide(element.car, null, (bound - pos) / wholeDt, isIn: true);  // IntegrationSet.cs:536, 546
```

---

## Collision & coupling damage pipeline

Single host-side handler:

```csharp
public void IntegrationSetCarsDidCollide(Car car0, [CanBeNull] Car car1, float deltaVelocity, bool isIn)
{
    if (!IsHost) return;
    float dvMph = deltaVelocity * 2.23694f;
    RequestSlackSound(car0, isIn, dvMph);                                       // → audio
    float dmg = config.damageForCollisionMph.Evaluate(Mathf.Abs(dvMph));
    if (dmg < 0.01f) return;                                                    // gate

    if (car1 != null) {                                                         // pair
        float totalW = car0.Weight + car1.Weight;
        car0.ApplyConditionDelta(-dmg * car1.Weight / totalW);                  // proportional
        car1.ApplyConditionDelta(-dmg * car0.Weight / totalW);
    } else {
        car0.ApplyConditionDelta(-dmg);                                         // boundary
    }

    float derailMix = Mathf.InverseLerp(10f, 20f, dvMph);
    if (derailMix > 0.001f) {
        float force0 = (car1 != null) ? car1.Weight * deltaVelocity : car0.Weight * deltaVelocity;
        float force1 = car0.Weight * deltaVelocity;
        car0.ApplyDerailmentForce(force0, "Collision vs {0}, dmph={1} -> {2} vs {3}", car1, dvMph, force0, force1);
        if (car1 != null)
            car1.ApplyDerailmentForce(force1, "Collision vs {0}, dmph={1} -> {2} vs {3}", car0, dvMph, force0, force1);
    }
}
```

(Source: `TrainController.cs:1191-1227`.)

### Damage formula

- Damage value: `Config.damageForCollisionMph.Evaluate(|deltaMph|)` (an `AnimationCurve`; see [Wear › Config](wear-durability.md#modelconfig-curves-tuning-surface)).
- Below 0.01: no damage applied (early return).
- Pair: damage split inversely by mass — heavier car takes a smaller fraction.
- Boundary collision (deadend, off-route car): full damage on the one car.

### Derailment from collision

- Activates above 10 mph delta (linear ramp 10→20 mph).
- Force input: `partner.Weight * deltaVelocity` (i.e., partner's momentum delta).
- Fed to `ApplyDerailmentForce` which has its own thresholds (`weight*5` … `weight*20`); see [Wear › derailment](wear-durability.md#derailment).

### **Critical: this bypasses `WearFeature`**

`IntegrationSetCarsDidCollide` calls `ApplyConditionDelta` directly. `ApplyConditionDelta` does not check `Car.WearFeature`. **Collisions damage cars even with the wear toggle off.** See [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).

### Slack sound

`TrainController.RequestSlackSound` (`TrainController.cs:1229`) debounces (0.5s per car-id), maps `0.5..15 mph` to volume `0.5..1.0`, and calls `ScheduledAudioPlayer.HostPlaySoundAtPosition("slack-in" or "slack-out", ...)`. The sound is dispatched as a network event from the host; clients play it. **`Coupler.SlackIn`/`SlackOut` are local-only audio entry points and are NOT called from this path** — they're vestigial entry points for direct invocation.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainController.IntegrationSetCarsDidCollide` | The single chokepoint for slack-reversal + auto-couple + boundary collision damage. Prefix to gate by `Car.WearFeature` if you want the toggle to actually disable everything; postfix to add custom side-effects. |
| `Car.ApplyConditionDelta` | Catches damage from collision AND curve overspeed AND derail-while-rolling. Broadest patch surface. See [Wear › patch candidates](wear-durability.md#patch-candidates). |
| `Car.ApplyDerailmentForce` | Tune derailment-from-collision thresholds. |
| `TrainController.RequestSlackSound` | Modify slack audio dispatch (debounce, volume, position). |

---

## Auto-uncouple paths

The system can sever connections without player input in these cases:

| Path | Trigger | Method |
|---|---|---|
| Derailment crossing 0.25 | `Car.ApplyDerailmentDelta` (`Car.cs:2341-2345`) | Inline `BreakConnections(end)` lambda; clears IsCoupled+IsAirConnected on both sides of both ends |
| Inconsistent set state | `IntegrationSet.ValidateConsistency` (`IntegrationSet.cs:906`) | `EventHandler.IntegrationSetRequestsBreakConnections(...)` → `TrainController.cs:1252` → `ApplyEndGearChange(... , false)` |
| Set membership boundary edges | `IntegrationSet.SortElements` (`IntegrationSet.cs:649-655`) | Same handler when first/last car has dangling connections |
| Set split | `IntegrationSet.Split` (`IntegrationSet.cs:757`) | `IntegrationSetRequestsBreakConnections` on both sides of split point |
| Car removal | `IntegrationSet.RemoveCar` (`IntegrationSet.cs:994`) | Same handler |
| Manual `Car.WillMove()` (player-placed cars) | `Car.cs:2802-2807` | Both ends cleared (host-only path) |
| Air hose break (uncoupled cars too far apart) | `IntegrationSet.IntegrateConstraints:519` | `IntegrationSetDidBreakAirHoses` → `TrainController.cs:1243` → clears `IsAirConnected` only |

Forced-reconnect path: `IntegrationSet.ValidateConsistency` (`IntegrationSet.cs:928, 932`) calls `IntegrationSetRequestsReconnect(engine, tender)` when a tender's `ForceConnectedToAtRear` returns true but the connection is missing. Handler at `TrainController.cs:1267` re-asserts both `IsCoupled` and `IsAirConnected` host-side.

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.ApplyDerailmentDelta` | Modify the 0.25 auto-uncouple threshold or veto auto-uncouple on derail. |
| `TrainController.IntegrationSetRequestsBreakConnections` | Single chokepoint for set-driven disconnects. Prefix to log/veto. |
| `TrainController.IntegrationSetRequestsReconnect` | Force-reconnect logic (currently only tender↔engine). |

---

## `IIntegrationSetEventHandler` (the abstraction)

```csharp
public interface IIntegrationSetEventHandler {                        // IIntegrationSetEventHandler.cs
    uint GenerateIntegrationSetId();
    void IntegrationSetDidCouple(Car car0, Car car1, float deltaVelocity);
    void IntegrationSetCarsDidCollide(Car car0, Car car1, float deltaVelocity, bool isIn);
    void IntegrationSetDidBreakAirHoses(Car car0, Car car1);
    void IntegrationSetRequestsBreakConnections(Car car, Car.LogicalEnd logicalEnd);
    [CanBeNull] Car IntegrationSetCheckForCar(Vector3 point);
    void IntegrationSetRequestsReconnect(Car engine, Car tender);
}
```

`TrainController` is the only implementer (`TrainController.cs:1170-1277`). All implementations are HostOnly — early `if (!IsHost) return;` or `if (IsHost) {...}` wrappers.

Mods cannot easily replace the implementation (set in `IntegrationSet.Create` constructor). Patch the methods on `TrainController` directly.

---

## Wire format & MP authority summary

| Action | Who can initiate | Key | Auth |
|---|---|---|---|
| Open coupler (cut-lever) | Crew + train-crew | `f.cutLever` / `r.cutLever` (float) | Default Crew + train-crew check |
| Open anglecock | Crew + train-crew | `f.anglecock` / `r.anglecock` (float) | Default Crew + train-crew check |
| Set IsCoupled | **Host only** | `_f.coupled` / `_r.coupled` (bool) | HostOnly (`_` prefix) |
| Set IsAirConnected | **Host only** | `_f.airConnected` / `_r.airConnected` (bool) | HostOnly (`_` prefix) |

Auth resolution: `Car.AuthorizationRequirementForPropertyWrite(key)` at `Car.cs:3112`. Prefixes at `Car.cs:467-473`.

**There is no `RequestCouple` / `RequestUncouple` message.** Coupling is implicit (auto-couple via integration solver). Uncoupling flows through the cut-lever KVO write (Crew-allowed) which the host's observer routes to `HandleOpenCoupler`. This works because cut-lever is non-HostOnly while `_f.coupled` is HostOnly — so client→cut-lever→host→`_f.coupled` is the *de facto* uncouple request channel.

---

## Pickable / interaction surface

```csharp
public class CouplerPickable : MonoBehaviour, IPickable {              // CouplerPickable.cs
    public Action activate;                            // wired in Coupler.Awake
    public bool   isOpen { get; set; }                 // surface state
    public float  MaxPickDistance => 75f;
    public TooltipInfo TooltipInfo => new TooltipInfo("Coupler", isOpen ? null : "Click to Open");
    public PickableActivationFilter ActivationFilter => PickableActivationFilter.PrimaryOnly;
    public void Activate(PickableActivateEvent evt);   // calls activate()
}
```

A second pickable, `CutLeverPickable` (implied via `RollingStock.ContinuousControls.ContinuousControl`), drives the actual lever. The coupler-pickable click is a shortcut — both routes into `Car.HandleCouplerClick` are equivalent.

`MovingColliderScaler.Shared.Register(GetComponent<CapsuleCollider>())` in `OnEnable`/`OnDisable` ensures pickable colliders scale with relative motion (so fast-moving cars are still clickable).

---

## Gotchas

- **No coupler-force vector exists in vanilla.** The "in-train forces" widely discussed in railroad sim modding are not present in Railroader's solver. The closest signals are `Element.SlackStretch` (current slack state) and `IntegrationSetCarsDidCollide`'s `deltaVelocity`. Anything else must be derived externally.
- **`Coupler.SlackIn` / `SlackOut` are dead code from the slack-audio path.** Vanilla routes slack audio through `TrainController.RequestSlackSound` → `ScheduledAudioPlayer`. Patching `Coupler.SlackIn` does not catch slack events.
- **`HandleCoupledChange` runs on every KVO observer tick.** That includes self-triggered writes (host) and remote writes (client). It re-positions the visual coupler (`PositionCoupler`) every time. If you patch this method, account for that volume.
- **Coupling is rejected silently for derailed/off-route cars.** The collision callback still fires, so damage occurs even though the cars don't link. Users may see "ghost couples" — touched but not coupled — without obvious feedback.
- **Tenders cannot be uncoupled from their engine.** `Car.RequiresConnectionToEnd(End.F)` returns true for `CarArchetype.Tender` (`Car.cs:2741`); `ValidateEndGearChange` rejects the write. The forced-reconnect logic in `IntegrationSet.SortElements`/`ValidateConsistency` actively re-couples tenders that get split off.
- **`SortElements` reorders the consist when cars are added/removed.** It nearest-neighbor walks from the last car, may flip `Car.FrontIsA` (`Car.Reverse()`), and severs both end-most connections (`IntegrationSet.cs:649-655`). After any topology change you cannot assume `LogicalEnd.A`/`B` orientation is stable.
- **Slack-direction collision sensitivity is hard-coded.** The thresholds in `PositionCars` (0.001..0.006 m InverseLerp, > 0.1) and the auto-couple threshold (0.22351964 m/s ≈ 0.5 mph) are not config-driven. Patch `IntegrationSet.PositionCars` or `IntegrateConstraints` to override.
- **`CouplerSlack` returns 0.02m for *every* end with end-gear**, regardless of car type. Long passenger cars and short tank cars have identical slack tolerance. Override `Car.CouplerSlack` virtual for per-class behavior.
- **`Coupler` is destroyed/recreated by `EndGear.Depopulate`/`Populate`** during model load/unload. Holding a long-lived reference to a `Coupler` is dangerous — prefer holding the `Car` and querying `Car.EndGearA.Coupler` on demand.
- **`IntegrationSet.Tick` uses 4 constraint iterations** (`for (int i=0; i<4; i++) IntegrateConstraints(dt);`). Slack reversal flags can be set multiple times per tick; the collision event only fires from `PositionCars` once per car after all iterations.
- **Boundary collisions pass `car1=null`.** Code that consumes `IntegrationSetCarsDidCollide` must null-check.
- **`AnyCarAdjacent` uses `set.TryGetAdjacentCar`** — depends on the integration set already having reorganized. During set-split/union transitions, validation may permit writes that would otherwise be blocked.
- **`HandleCouplerClick` reads `GameInput.SmartAirHelperModifier` directly** — not via a control property. If your mod re-binds inputs, the modifier read is global static.

---

## Cross-references to Wear & Durability

- Damage formula `Config.damageForCollisionMph` and other tuning curves: see [Wear › Config curves](wear-durability.md#modelconfig-curves-tuning-surface).
- `ApplyConditionDelta` / `SetCondition` / `ApplyDerailmentForce` semantics: see [Wear › damage application](wear-durability.md#damage-application) and [derailment](wear-durability.md#derailment).
- Why collision damage runs even with the wear toggle off: see [Wear › toggle bypasses](wear-durability.md#toggle-bypasses-high-value-findings).
- `RepairTrack` for fixing what couplers broke: see [Wear › RepairTrack](wear-durability.md#modelopsrepairtrack-industry-side-repair).
