# Anglecock & Hose — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Brakes](brakes.md), [Air System](air-system.md), [Couplers](couplers.md)

The anglecock + air-hose subsystem is the connectivity layer between adjacent cars' brake pipes. Per car-end there's a logical state (anglecock setting `0..1`, `IsAirConnected` bool, gladhand-connected derived from `IsAirConnected`) plus a visual layer (the `Anglecock` MonoBehaviour, the Verlet-rope `Hose` MonoBehaviour, the `GladhandClickable` pickable). The brake-pipe propagation in [air-system.md](air-system.md) consumes both `IsAirConnected` (binary topology) and `AnglecockSetting` (analog flow-throttle) per pair. **Hoses do not "tear" on uncouple under tension** — when cars uncouple, both ends remain `IsAirConnected=true` until the slack solver detects the cars have moved >1.5 m apart, at which point `BreakAirHoses` fires. Hose visuals "pop" with audio + propulsion impulse on disconnect.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `RollingStock.Anglecock` | `RollingStock/Anglecock.cs:12` | Visual + audio + control. Owns the Hose ref |
| `RollingStock.Hose` | `RollingStock/Hose.cs:12` | Verlet-simulated rope mesh, no physics game-state |
| `RollingStock.HoseProfile` | `RollingStock/HoseProfile.cs` | ScriptableObject with hose tuning fields |
| `RollingStock.GladhandClickable` | `RollingStock/GladhandClickable.cs` | Pickable that calls `Anglecock.GladhandClick` |
| `Car.EndGear` | `Model/Car.cs:122` | Per-end logical state (Anglecock setting, IsAirConnected, etc.) |
| `Game.Messages.SetGladhandsConnected` | `Game.Messages/SetGladhandsConnected.cs` | Crew message → host writes both cars' `IsAirConnected` |
| `TrainController.HandleSetGladhandsConnected` | `TrainController.cs:1400` | Host handler. Validates same-set, applies bidirectional KVO |
| `IntegrationSet.BreakAirHoses` | `Model.Physics/IntegrationSet.cs:708` | Triggered when uncoupled cars drift >1.5m apart |
| `TrainController.IntegrationSetDidBreakAirHoses` | `TrainController.cs:1243` | Host handler — clears both cars' `IsAirConnected` |

---

## Two-layer model: logical vs visual

```
┌─────────────────────────────────────┐  Logical (per-end, on Car.EndGear)
│  AnglecockSetting (float 0..1)      │  KVO key: "f.anglecock" / "r.anglecock"
│  IsAirConnected   (bool)            │  KVO key: "_f.airConnected" / "_r.airConnected"  HostOnly
│  AirPressure      (float, mirrored) │  Mirror of car.air.BrakeLine.Pressure
└─────────────────────────────────────┘
          │ EndGear.Populate(prefab)
          ▼
┌─────────────────────────────────────┐  Visual (MonoBehaviours, recreated per model load)
│  Anglecock  (handle + audio loop)   │  Anglecock.control: ContinuousControl
│  Hose       (Verlet rope mesh)      │  Anglecock.hose
│  GladhandClickable (pickable)       │
└─────────────────────────────────────┘
```

The visual layer is **destroyed and recreated on every model load/unload** (`EndGear.Populate`/`Depopulate` at `Car.cs:195-221`). Holding a long-lived reference to an `Anglecock` or `Hose` is unsafe — query via `car.EndGearA.Anglecock` on demand. Same caveat as for `Coupler`; see [Couplers › gotchas](couplers.md#gotchas).

### Logical state is on `Car.EndGear`

```csharp
// Car.cs:122-222 (excerpt)
public class EndGear {
    public Anglecock Anglecock;
    [CanBeNull] public Coupler Coupler;
    public CutLever  CutLever;
    public bool      IsCoupled;
    public bool      IsAirConnected;
    public float     AnglecockSetting;
    public float     AirPressure;
    public bool      NeedsConnectionUpdate;
    [CanBeNull] private EndGear _other;
    private bool _isPopulated;

    public bool IsAirConnectedAndOpen => IsAirConnected && IsAnglecockOpen;
    public bool IsAnglecockOpen       => AnglecockSetting > 0.1f;

    public void SetConnectedTo([CanBeNull] EndGear other) { ... }    // links the two visual hoses
    public void Populate(Anglecock prefab, Transform parent, Vector3 airHosePosition) { ... }
    public void Depopulate() { ... }
}
```

`AnglecockSetting > 0.1f` is the **logical "is open" threshold**. The air-flow gate in [air-system.md](air-system.md) uses **`< 0.01f`** (`CarAirSystem.cs:206-213`) — different threshold. Settings 0.01..0.1 will flow air but display as closed.

`AirPressure` is updated each frame in `Car.SynchronizeEndGear` (`Car.cs:2457`) from `air.BrakeLine.Pressure`. The `Hose.OnGetPressure` callback (set in `EndGear.Populate` at `Car.cs:200`) reads it and modulates by anglecock setting:

```csharp
Anglecock.hose.OnGetPressure = () => AirPressure * Mathf.Lerp(0.5f, 1f, AnglecockSetting);
```

This is consumed when the hose pops — drives the propulsion impulse on the loose hose end (more pressure = more "whip").

---

## Anglecock model

### `Anglecock` MonoBehaviour

```csharp
// RollingStock/Anglecock.cs:12-200
public class Anglecock : MonoBehaviour {
    public enum GladhandClickAction { None, Connect, Disconnect }

    public ContinuousControl control;          // the rotary handle pickable
    public AudioClip airFlowAudioClip;
    public Hose hose;
    private Car.End _carEnd;
    private string _carId;
    private IAudioSource _airFlowAudioSource;
    private float _flowDisplay;
    private bool _connectedDisplay;

    public float Flow {
        get => _flowDisplay;
        set { ... UpdateFlowAudio(); }
    }
    public bool IsConnected {
        get => _connectedDisplay;
        set { ... UpdateFlowAudio(); }
    }
    private Car Car { get { ... } }            // GetComponentInParent<Car>(), cached

    public void Setup(Car.End carEnd, string carId) { _carEnd = carEnd; _carId = carId; }

    private void OnEnable() {
        control.OnValueChanged -= ControlDidChange;
        control.OnValueChanged += ControlDidChange;
        control.CheckAuthorized = () => StateManager.CheckAuthorizedToSendMessage(
            new PropertyChange(_carId, "f.anglecock", new FloatPropertyValue(0f)));
        control.MaxPickDistance = 15f;
    }

    private void ControlDidChange(float value) {
        Car car = Car;
        car.ApplyEndGearChange(car.EndToLogical(_carEnd), Car.EndGearStateKey.Anglecock, value);
    }

    public GladhandClickAction GladhandClickConnects() { ... }
    public void GladhandClick() { ... }
}
```

Key behaviours:
- The handle's `ContinuousControl.OnValueChanged` directly writes `f.anglecock`/`r.anglecock` via `ApplyEndGearChange`. **Crew + train-crew authorized.**
- `CheckAuthorized` is a delegate the `ContinuousControl` consults to grey out the handle for unauthorized players. Note the hardcoded `"f.anglecock"` literal in the auth check — this is fine because both keys map to the same auth (Crew + per-car train-crew check); the check is for the *prefix*.
- `Flow` setter audio: when `_flowDisplay > 0.1 && !IsConnected`, plays a looped air-flow sound at volume `InverseLerp(0, 100, _flowDisplay)`. Connected anglecocks don't play audio (the air is captive).

### Anglecock setting → behaviour

| Setting | Logically Open (`IsAnglecockOpen`)? | Air flows (`ValveValueForAnglecock`)? | UI label |
|---|---|---|---|
| 0.0 | No | No | Closed |
| 0.005 | No | No (< 0.01) | Closed |
| 0.05 | No | Yes (≥ 0.01) | "Closed" but flowing |
| 0.1 | No (0.1 fails strict `> 0.1f`) | Yes | "Closed" but flowing |
| 0.11 | Yes | Yes (limited) | Open |
| 0.5 | Yes | Yes (50%) | Open |
| 1.0 | Yes | Yes (100%) | Open |

The 0.01..0.1 dead-zone is **vanilla weirdness** — the handle can be set in this range to leak air without showing as logically open. UI controls quantize to standard positions but mod-set or KVO-set values can land here.

### `EndGear.IsAnglecockOpen` consumers

```csharp
// Car.cs:158
public bool IsAnglecockOpen => AnglecockSetting > 0.1f;
```

Used by:
- `IntegrationSet.EnumerateAirOpenTo` (`IntegrationSet.cs:776-779`) — for AI/script "what cars are reachable via open anglecocks"
- `AutoEngineer.BrakeLineTogether` (`AutoEngineer.cs:947-955`): `!firstCar.EndGearA.IsAnglecockOpen && !lastCar.EndGearB.IsAnglecockOpen` — true iff the train is fully sealed (both end anglecocks closed).
- `EndGear.IsAirConnectedAndOpen` (`Car.cs:146-156`) — composite flag.

### MP authority

`f.anglecock` / `r.anglecock` keys: Crew + train-crew (default; no `_` prefix). Float value. Wire format is identical for handle moves and `SetConnectedTo` indirect updates — every change goes through `ApplyEndGearChange` → KVO write → broadcast.

### Patch candidates

| Method | Why patch |
|---|---|
| `Anglecock.ControlDidChange` | Catch all handle moves before they hit KVO. |
| `Car.EndGear.IsAnglecockOpen` (property) | The 0.1 logical threshold. |
| `CarAirSystem.ValveValueForAnglecock` (private static) | The 0.01 flow threshold. |
| `Anglecock.UpdateFlowAudio` | Customize air-flow audio (volume curve, conditional muting). |

---

## Air connection topology

### `IsAirConnected` is bidirectional and host-authoritative

`IsAirConnected` is a per-end bool stored as KVO key `_f.airConnected` / `_r.airConnected`. **HostOnly** (leading `_`). It represents "the gladhand is mated to the adjacent car's gladhand," not the anglecock state. Two adjacent cars' facing ends should have matching `IsAirConnected` — host's `ValidateConsistency` (`IntegrationSet.cs:923-927`) detects mismatches and calls `IntegrationSetRequestsBreakConnections` to repair.

### `EndGear.SetConnectedTo` — visual hose link

```csharp
// Car.cs:160-175
public void SetConnectedTo([CanBeNull] EndGear other) {
    _other = other;
    if (_isPopulated) {
        if (other == null)
            Anglecock.hose.SetConnectedTo(null);
        else if (other._isPopulated)
            Anglecock.hose.SetConnectedTo(IsAirConnected ? other.Anglecock.hose : null);
        Anglecock.IsConnected = IsAirConnected;
    }
}
```

Called from `Car.SynchronizeEndGear` (`Car.cs:2451-2483`) every `FixedUpdate`. The visual hose is connected to the other car's hose **only if `IsAirConnected`** — closed anglecocks but mated gladhands still show connected. Ungated by anglecock setting.

### `Car.SynchronizeEndGear`

```csharp
// Car.cs:2451-2483 (key parts)
private void SynchronizeEndGear(EndGear endGear, End end) {
    if (endGear.Coupler != null) endGear.Coupler.SetOpen(!endGear.IsCoupled);
    endGear.AirPressure = air.BrakeLine.Pressure;             // mirror BL → endGear for hose pop
    if (set == null) return;
    LogicalEnd logicalEnd = EndToLogical(end);
    LogicalEnd otherEnd   = (logicalEnd == LogicalEnd.A) ? LogicalEnd.B : LogicalEnd.A;
    Car otherCar = AirConnectedTo(logicalEnd);
    if (otherCar == null) {
        if (endGear.IsAirConnected)
            Debug.LogWarning($"Can't synchronize end gear yet: {DisplayName} {end}'s otherCar is null");
        else
            endGear.SetConnectedTo(null);
    } else {
        EndGear connectedTo = otherCar[otherEnd];
        endGear.SetConnectedTo(connectedTo);
    }
}
```

`AirConnectedTo(logicalEnd)` returns the other car only if both `IsAirConnected==true` (`IntegrationSet.GetAirConnection`, `IntegrationSet.cs:1022-1042`). The "Can't synchronize end gear yet" warning fires during transient inconsistencies (e.g., during car add/remove between ticks).

### Patch candidates

| Method | Why patch |
|---|---|
| `Car.SynchronizeEndGear` | Per-tick visual sync. Patch to add custom visual states. |
| `Car.AirConnectedTo` / `IntegrationSet.GetAirConnection` | Topology query. |
| `EndGear.SetConnectedTo` | Hose link logic. |

---

## Gladhand connect / disconnect

The gladhand is the visible coupling head at the end of each hose. Clicking it requests a `SetGladhandsConnected` message.

### `GladhandClickable` pickable

```csharp
// RollingStock/GladhandClickable.cs
public class GladhandClickable : MonoBehaviour, IPickable {
    public float MaxPickDistance => 30f;
    public int Priority => 1;
    public TooltipInfo TooltipInfo => new TooltipInfo("Gladhand", Anglecock.GladhandClickConnects() switch {
        Anglecock.GladhandClickAction.None       => "",
        Anglecock.GladhandClickAction.Connect    => "Click to Connect Gladhands",
        Anglecock.GladhandClickAction.Disconnect => "Click to Disconnect Gladhands",
    });
    public PickableActivationFilter ActivationFilter => PickableActivationFilter.PrimaryOnly;

    public void Activate(PickableActivateEvent evt) => Anglecock.GladhandClick();
    public void Deactivate() { }
}
```

### `Anglecock.GladhandClick`

```csharp
// RollingStock/Anglecock.cs:151-184
public void GladhandClick() {
    GladhandClickAction action = GladhandClickConnects();
    if (action != GladhandClickAction.None) {
        Car car = Car;
        Car.LogicalEnd logicalEnd = car.EndToLogical(_carEnd);
        bool aSide = logicalEnd == Car.LogicalEnd.A;
        Car otherCar = car.CoupledTo(logicalEnd) ?? car.AirConnectedTo(logicalEnd);
        bool connect = action == GladhandClickAction.Connect;
        Car carA, carB;
        if (aSide) { carA = otherCar; carB = car; } else { carA = car; carB = otherCar; }
        StateManager.ApplyLocal(new SetGladhandsConnected(carA.id, carB.id, connect));
        if (GameInput.SmartAirHelperModifier) {
            int v = connect ? 1 : 0;
            car.ApplyEndGearChange(logicalEnd, Car.EndGearStateKey.Anglecock, v);
            Car.LogicalEnd otherLogicalEnd = (logicalEnd == Car.LogicalEnd.A) ? Car.LogicalEnd.B : Car.LogicalEnd.A;
            otherCar.ApplyEndGearChange(otherLogicalEnd, Car.EndGearStateKey.Anglecock, v);
        }
    }
}

public GladhandClickAction GladhandClickConnects() {
    Car car = Car;
    if (IsConnected) return GladhandClickAction.Disconnect;
    Car.LogicalEnd logicalEnd = car.EndToLogical(_carEnd);
    if ((bool)car.CoupledTo(logicalEnd)) return GladhandClickAction.Connect;
    return GladhandClickAction.None;
}
```

Rules:
- **Disconnect always allowed** if currently connected.
- **Connect only allowed if cars are coupled** (`car.CoupledTo(logicalEnd) != null`). You cannot connect gladhands across an open coupler.
- **Smart air helper modifier** (`GameInput.SmartAirHelperModifier`, default `Shift`?) simultaneously sets both anglecocks to the new connect/disconnect state (open both on connect, close both on disconnect).

### `SetGladhandsConnected` message + handler

```csharp
// Game.Messages/SetGladhandsConnected.cs
[MinimumAccessLevel(AccessLevel.Crew)]
public struct SetGladhandsConnected {
    public string CarIdA;
    public string CarIdB;
    public bool Connected;
}

// TrainController.cs:1400-1418
public void HandleSetGladhandsConnected(string carIdA, string carIdB, bool connect) {
    if (IsHost) {
        Car a = CarForId(carIdA);
        Car b = CarForId(carIdB);
        if (a == null || b == null) throw new ArgumentException("Bad car id");
        if (a.set != b.set) throw new ArgumentException("Cars are not in same set");
        a.set.OrderAB(ref a, ref b);                                                        // ensure A→B order
        a.ApplyEndGearChange(Car.LogicalEnd.B, Car.EndGearStateKey.IsAirConnected, connect);
        b.ApplyEndGearChange(Car.LogicalEnd.A, Car.EndGearStateKey.IsAirConnected, connect);
    }
}
```

- **Crew minimum.** No per-car-crew check on the message itself (no `[PropertyChangeAuthorizationRule]`). The handler runs unconditionally if message validates.
- Validates cars are in the same `IntegrationSet`. Cars in different sets cannot be gladhand-connected.
- `OrderAB` ensures the host applies B-side to the lower-positioned car and A-side to the higher.
- Host writes both KVO keys (HostOnly) — clients see the change via `_f.airConnected`/`_r.airConnected` broadcasts.

### Patch candidates

| Method | Why patch |
|---|---|
| `Anglecock.GladhandClick` | Intercept click intent before message send. |
| `TrainController.HandleSetGladhandsConnected` | Host-side gate. Add validation (e.g., refuse gladhand connect during emergency). |
| `Anglecock.GladhandClickConnects` | Change UX rules (e.g., allow connect across open couplers). |

---

## Hose visual model

### `Hose` — Verlet rope

```csharp
// RollingStock/Hose.cs (excerpts)
public class Hose : MonoBehaviour, CullingManager.ICullingEventHandler {
    private struct Point { public Vector3 Position, OldPosition, Acceleration; }

    private Hose _connectedTo;
    private bool _firstConnectedTo = true;
    private float _damping;
    public MeshRenderer meshRenderer;
    public HoseProfile profile;
    private float _hoseLength = 0.52f;
    private const int NumPoints = 9;
    private readonly Point[] _points = new Point[9];
    private float _propulsion;
    private const int EdgeCount = 8;
    public Func<float> OnGetPressure { get; set; }    // set by EndGear.Populate

    private void FixedUpdate() {
        if (_isVisible) { Simulate(Time.deltaTime); UpdateIfNeeded(); }
    }
    public void SetConnectedTo(Hose other) { ... }     // plays connect / pop
    private void Simulate(float dt) { UpdateForces(); Integrate(dt); IterateCollisions(); ... }
    private void UpdateForces() { ... gravity + propulsion ... }
    private void Pop() {                               // disconnect transient
        _damping = profile.dampingAtPop;
        float v = OnGetPressure?.Invoke() ?? 0f;
        _propulsion = Mathf.Clamp01(Mathf.InverseLerp(0f, 90f, v));
        PlayPop(_propulsion);
    }
}
```

- **9 points, 8 edges, total length 0.52m default** (overridden per car via `HoseProfile.lengthCurve` based on the `airHosePosition` distance).
- **Verlet integration** with iteration-based edge constraints (`IterateCollisions` runs `UpdateEdges` 4×).
- **Culling**: registered with `CullingManager.Hose` for distance-band visibility. Only simulates when `_isVisible && distanceBand < 1`.
- **`Pop()` impulse**: when disconnecting, inflates `_propulsion` proportional to BL pressure (0..90 PSI → 0..1). Drives the loose hose end backward at random angle.
- `_firstConnectedTo` flag suppresses pop/connect audio on initial population (so loading a saved game doesn't whip-crack every hose).

### `HoseProfile` ScriptableObject

```csharp
// RollingStock/HoseProfile.cs
[CreateAssetMenu(fileName = "Hose", menuName = "Train Game/Hose Profile", order = 0)]
public class HoseProfile : ScriptableObject {
    public AnimationCurve lengthCurve  = AnimationCurve.Constant(0f, 1f, 0.52f);
    public float dampingAtPop          = 1f;
    public float dampingAtRest         = 0.9f;
    public float dampingRestSpeed      = 1f;
    public float gravity               = 200f;
    public float angleStart            = 45f;
    public float angleEnd              = 45f;
    [Header("Gladhand")]
    public GameObject gladhandPrefab;
    public Vector3   gladhandOffset    = new Vector3(-0.026f, 0f, 0.093f);
    [Header("Spline")]
    [Range(0.0001f, 0.1f)] public float maxMagnitudeDelta = 0.1f;
    [Range(0f, 10f)]       public float maxDegreesDelta   = 0.1f;
    [Range(0f, 1f)]        public float maxDegreesMove    = 0.1f;
    public float propulsion;                       // unused; left at 0 in profile
    public float propulsionDecay = 0.9f;
    [Header("Audio")]
    public List<AudioClip> popClips;                // disconnect (pressure-modulated)
    public List<AudioClip> disconnectClips;         // gladhand-apart click (volume static)
    public List<AudioClip> connectClips;            // connect click
}
```

`HoseProfile.propulsion` is read in `Hose.UpdateForces` (`Hose.cs:283`):

```csharp
_points[^1].Acceleration += _propulsion * profile.propulsion * normalized;
```

If `profile.propulsion == 0`, the propulsion impulse from `Pop()` does nothing. **Default profile asset typically has `propulsion = 0`** — so popping hoses don't whip in vanilla unless a non-default profile is loaded. (Worth verifying against your shipped asset; the inline default on the field is `0`.)

### `_hoseLength` derivation

```csharp
// Hose.cs:84-98
public void Configure(Vector3 airHosePosition) {
    float time = Vector3.Distance(new Vector3(0f, 0.5f, 1f), airHosePosition);
    _hoseLength = profile.lengthCurve.Evaluate(time);
    ...
}
```

Length is curve-evaluated against the distance from a reference point to the configured hose attachment position. Per-car `Definition.AirHosePosition` controls where the hose attaches; `lengthCurve` then maps that distance to a desired hose length.

### Connection logic

```csharp
// Hose.cs:144-163
public void SetConnectedTo(Hose other) {
    if ((object)_connectedTo == other) return;
    _connectedTo = other;
    if (!_firstConnectedTo) {
        if (_connectedTo == null) Pop();
        else                       PlayConnect();
    }
    _firstConnectedTo = false;
}

// Hose.cs:360-385
private void UpdateForConnection() {
    if (_connectedTo == null) return;
    Vector3 endPoint  = EndPoint;
    Vector3 endPoint2 = _connectedTo.EndPoint;
    float num = Vector3.Distance(endPoint, endPoint2);
    if (num > 10f) {
        Log.Warning("Hose for {car} is too long: {distance}", componentInParent, num);
        return;                                         // bail rather than stretch
    }
    // Pull our last point toward the midpoint between our gladhand and theirs
    ...
    _points[^1].Position = Vector3.Lerp(_points[^1].Position, b2, 0.5f);
}
```

So **connected hoses pull toward each other every fixed-update** at 50% blend. The "10m too long" check prevents catastrophic stretches but **does not break the connection** — just bails the per-tick blend. Disconnection is still driven by `IntegrationSet.BreakAirHoses` (next section).

### Patch candidates

| Method | Why patch |
|---|---|
| `Hose.Pop` | Customize pop behaviour (e.g., always-on propulsion regardless of profile). |
| `Hose.UpdateForConnection` | Replace the rope-pulling between connected hoses. |
| `HoseProfile` (asset) | Tune all hose physics. ScriptableObject; mod can swap globally via `Resources.Load` or asset-replace. |
| `Hose.Simulate` | The full Verlet step. |

---

## Hose tear / break under tension

This is the user's specific question: **"does it exist? cross-reference couplers.md"**.

### Vanilla answer: hoses don't "tear" under tension — they break by separation distance

The mechanism is **not** a force/tension calculation. It's a positional check in the slack solver:

```csharp
// Model.Physics/IntegrationSet.cs:507-520 (IntegrateConstraints)
} else if (!flag /* not coupled */ && AreAirHosesConnected(element, element2) && num2 > 1.5f) {
    BreakAirHoses(element, element2);
}
```

Translation: **for two air-connected but uncoupled cars whose center-to-center gap exceeds 1.5 m, break the air hoses.** That's it. No tension force, no condition damage, no impulse.

```csharp
// IntegrationSet.cs:708-712
private void BreakAirHoses(Element entry0, Element entry1) {
    EventHandler.IntegrationSetDidBreakAirHoses(entry0.car, entry1.car);
    Log.Information("Air hoses separated: {Entry0}, {Entry1}", entry0.car, entry1.car);
}
```

Routed to:
```csharp
// TrainController.cs:1243-1250
public void IntegrationSetDidBreakAirHoses(Car car0, Car car1) {
    if (IsHost) {
        car0.ApplyEndGearChange(Car.LogicalEnd.B, Car.EndGearStateKey.IsAirConnected, boolValue: false);
        car1.ApplyEndGearChange(Car.LogicalEnd.A, Car.EndGearStateKey.IsAirConnected, boolValue: false);
    }
}
```

So the host clears both ends' `IsAirConnected`, broadcasts via KVO, clients update their `EndGear.IsAirConnected`, `Car.SynchronizeEndGear` next tick passes `null` to `Hose.SetConnectedTo`, and the hose pops (audio + propulsion).

### Sequence on a typical uncoupling-while-moving scenario

1. Player hits cut lever or mod-side `ApplyEndGearChange(IsCoupled, false)`.
2. **Both `IsCoupled` cleared, `IsAirConnected` *retained*** (cut-lever flow doesn't touch air).
3. Cars start drifting (one pushed, one pulled).
4. `IntegrationSet.IntegrateConstraints` runs each tick. While `gap < 1.5m`: cars stay air-connected (hose pulls them via `Hose.UpdateForConnection`).
5. Once `gap > 1.5m`: `BreakAirHoses` fires.
6. Host clears both `IsAirConnected` → broadcast → visual hose `Pop()`.
7. Now both cars' `EndGearA/B.IsAirConnected = false` → next `UpdateBrakeLine` tick treats them as separate consist segments. Each segment's brake-pipe vents through any open anglecock at the new free end.

**No condition damage from hose tear.** Compare to [Couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline) — collisions damage cars through `ApplyConditionDelta` via the `IntegrationSetCarsDidCollide` callback. Hose tear fires `IntegrationSetDidBreakAirHoses`, which has no damage path.

### "But the brakes apply, right?"

**Yes**, indirectly. Once `IsAirConnected` is cleared on both ends, the brake pipe vents to atmosphere through the now-dangling open anglecock (assuming setting > 0.01 — which it usually is for rolling cars). BL pressure crashes, triple valve sees BL << BR, brake cylinder pumps from BR → BC, brakes apply. **This is the real-world emergency-application semantic, achieved naturally** through the air sim, not as a special-case.

### Other paths that clear `IsAirConnected`

See [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths). Some of those paths clear both `IsCoupled` and `IsAirConnected` simultaneously (e.g., derailment > 0.25, set split, car removal). The hose tear path is the *only* one that clears `IsAirConnected` without clearing `IsCoupled` — though by definition the cars must already be uncoupled for hose tear to be reachable.

### Patch candidates

| Method | Why patch |
|---|---|
| `IntegrationSet.IntegrateConstraints` (the `> 1.5f` literal at line 517) | Change the tear distance. Hot path; prefer wrapping `BreakAirHoses` instead. |
| `IntegrationSet.BreakAirHoses` (private) | Add side-effects (e.g., emit a Messenger event for mod consumers). |
| `TrainController.IntegrationSetDidBreakAirHoses` | Host-side handler; add condition damage if you want hose tear to wear cars. |

### Mods that want force-based hose tear

Vanilla doesn't expose a coupler-force vector ([Couplers › gotchas](couplers.md#gotchas)). The closest signal is `Element.SlackStretch` — when uncoupled cars start being yanked apart, `SlackStretch` doesn't accumulate (it only does between coupled neighbours). So a force-based hose tear must instead key off `velocity` differential and `gap` rate-of-change. Patching `IntegrationSet.IntegrateConstraints` to compute relative velocity per uncoupled-but-air-connected pair, then multiplying by `Hose.OnGetPressure` for "yank intensity," is the natural extension.

---

## Brake-pipe propagation across anglecocks

Cross-reference to [air-system.md](air-system.md#brake-pipe-propagation-the-key-loop). The relevant per-pair logic:

```csharp
// CarAirSystem.cs:215-249 (paraphrased)
private void UpdateBrakeLineIndividualB(float dt) {
    var otherAir = car.set.GetAirConnection(car, LogicalEnd.B)?.air;
    float thisCockValve = ValveValueForAnglecock(anglecockB);   // 0 if < 0.01, else value
    if (otherAir == null) {
        // dangling end — vent through this car's anglecock to atmosphere
        anglecockFlowB += BrakeLineConnectionB.Equalize(BrakeLine, null, thisCockValve, dt);
    } else {
        float pairValve = Mathf.Min(otherAir.anglecockA, thisCockValve);   // BOTH must be open
        float flow = BrakeLineConnectionB.Equalize(BrakeLine, otherAir.BrakeLine, pairValve, dt);
        otherAir.anglecockFlowA += flow;
        anglecockFlowB           += flow;
    }
}
```

**Both anglecocks must be open** for inter-car flow. **Either anglecock open** allows venting at a dangling end (cut consist, broken hose).

### Why is `IsAirConnected` consulted but never appears in the per-pair flow?

`GetAirConnection` (`IntegrationSet.cs:1022-1042`) returns null if `IsAirConnected==false`. So the topology check is upstream of `UpdateBrakeLineIndividualA/B`. If the cars are not air-connected, `otherAir == null`, the dangling-end branch executes — venting via this car's anglecock to atmosphere as if there's no neighbour. **Closed gladhands behave identically to "no neighbour" from the air sim's perspective.**

### Cross-reference

The full brake-pipe propagation walk is documented in [air-system.md › brake-pipe propagation](air-system.md#brake-pipe-propagation-the-key-loop). Key takeaway for this doc: anglecock setting acts as a **valve-amount** (`pairValve = min(a, b)`), not a binary gate. Half-open anglecocks halve the propagation rate.

---

## MP authority summary (anglecock & hose)

| Action | Who | Wire |
|---|---|---|
| Anglecock setting change | Crew + train-crew | `PropertyChange { key="f.anglecock"|"r.anglecock", float }` |
| Gladhand connect/disconnect | Crew (no train-crew check on message) | `SetGladhandsConnected { CarIdA, CarIdB, bool }` |
| `IsAirConnected` direct write | **HostOnly** (`_` prefix) | KVO key `_f.airConnected` / `_r.airConnected` |
| Hose tear (auto, via separation) | **Host** (driven by IntegrationSet) | `IntegrationSetDidBreakAirHoses` callback → host writes `_*.airConnected = false` |

**Asymmetry:** clients can directly toggle anglecocks (Crew); they cannot directly toggle gladhand connection (must request via `SetGladhandsConnected`); they cannot at all touch `IsAirConnected` (HostOnly). The reasoning: anglecock is a per-car operation, gladhand connect requires set membership validation (`a.set != b.set` check), `IsAirConnected` is the host's ground-truth topology.

---

## Gotchas

- **Anglecock setting has two thresholds.** `> 0.1f` for "logically open" (used by AI / `IsAirConnectedAndOpen`); `>= 0.01f` for "flows air" (used by brake-pipe sim). Settings 0.01..0.1 are a "leaking but closed" zone.
- **`Anglecock.Setup` is called by `Car.SetupCutLevers` / model load**, not by `Anglecock.Awake`. If you instantiate an `Anglecock` outside the normal model-load path, `_carEnd` and `_carId` are uninitialized — `ControlDidChange` will throw or write to a null car.
- **`Hose._firstConnectedTo` resets per-Hose-instance.** Since hoses are recreated on every model load, every load+populate fires a hose connect *without* audio (because `_firstConnectedTo == true` skips `PlayConnect()`). Subsequent reconnects after model is loaded play audio normally.
- **Hose tear distance is hardcoded `1.5f` in `IntegrationSet.cs:517`.** Not in any config asset.
- **Hose `propulsion` profile field is unused if zero.** The `_propulsion` runtime field is set by `Pop()` from BL pressure, but it's multiplied by `profile.propulsion` in `UpdateForces`. If the asset has `propulsion = 0`, hose pop is purely audio + damping change.
- **`SetGladhandsConnected` rejects cross-set requests.** A client trying to connect cars in different `IntegrationSet`s gets a thrown `ArgumentException` host-side. UI shouldn't allow this (cars must be coupled to be air-connectable, and coupled cars share a set), but mods should validate.
- **`Anglecock.IsConnected` setter mutes the air-flow audio.** When IsConnected goes true, the audio source is stopped (because connected anglecocks have captive air). Mod-side flow audio that always plays should not use the `Anglecock.Flow` setter.
- **`HandleSetGladhandsConnected` does not validate the cars are *adjacent*.** It only checks `a.set == b.set`. In principle a cross-set message with cars 5 positions apart in the same set would bind their wrong-end air connections. Vanilla UI never produces such a message; mods that send custom `SetGladhandsConnected` should pre-validate adjacency.
- **Smart air helper** (`GameInput.SmartAirHelperModifier`, default Shift) is read directly in `Anglecock.GladhandClick` (`Anglecock.cs:176`). Same caveat as `HandleCouplerClick` — global static, not a control property.
- **`Anglecock.OnEnable` rebinds the auth check delegate every enable cycle.** If your mod overrides `CheckAuthorized`, it gets clobbered next time the anglecock is re-enabled.
- **`endGear.AirPressure` is mirrored from BL each tick** (`Car.SynchronizeEndGear`). If you mutate it, expect overwrite on the next FixedUpdate.

---

## Cross-references

- Brake-pipe physics consuming anglecock state: [Air System › brake-pipe propagation](air-system.md#brake-pipe-propagation-the-key-loop).
- `IsAirConnected` clearing on derail / set split / car removal: [Couplers › auto-uncouple paths](couplers.md#auto-uncouple-paths).
- `IntegrationSet.IntegrateConstraints` (where the `> 1.5f` hose-tear check lives): [Couplers › auto-couple](couplers.md#auto-couple-impact-driven).
- Cut-lever pipeline and the `SmartAirHelperModifier` interaction: [Couplers › cut lever pipeline](couplers.md#cut-lever-pipeline-player-driven-uncouple).
- Brake control inputs that modulate the BL pressure that hoses display: [Brakes](brakes.md).
- Wear/damage pipeline (which hose tear does **not** invoke): [Wear & Durability › damage application](wear-durability.md#damage-application).
