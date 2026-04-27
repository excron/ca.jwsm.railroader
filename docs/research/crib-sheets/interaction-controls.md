# Interaction & Continuous Controls — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Player & Camera](player-camera.md), [Input & Keybinds](input-keybinds.md), [Couplers](couplers.md), [Brakes](brakes.md), [Anglecock & Hose](anglecock-hose.md), [Traction](traction.md)

Railroader's mouse-pointer interaction layer has two layered substrates. **`IPickable`** is the universal "this collider can be clicked" interface; a singleton `ObjectPicker` raycasts each `FixedUpdate` against the `Clickable` layer and dispatches `Activate` / `Deactivate` to the highest-priority hit. **`RollingStock.ContinuousControls.ContinuousControl`** is an `IPickable` subclass for click-and-drag handles (twist valves, lever pulls, sliders) that converts mouse motion into a `0..1` scalar and surfaces it via `OnValueChanged`. Every physical handle on a locomotive cab — throttle, reverser, train brake, loco brake, cut-out, horn, bell, anglecock, cut-lever, brake stand, turntable lever — is a single `RadialAnimatedControl` (the only non-trivial subclass) wired up by **`ConfigurePropertyChange`**, an extension that translates the value-change event into a `PropertyChange` KVO write through `StateManager.ApplyLocal`. There is no per-control network message; **all MP authority is delegated to the underlying KVO key's auth check** via `CheckAuthorized`. The unusual consequence: two players can grab the same valve simultaneously — both see local visual updates, but only the writes that pass `CheckAuthorizedToSendMessage` reach the host, and the last write wins on the KVO.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `IPickable` | `IPickable.cs:1` | Universal interaction interface (`MaxPickDistance`, `Priority`, `TooltipInfo`, `ActivationFilter`, `Activate`, `Deactivate`) |
| `ObjectPicker.Shared` (singleton) | `ObjectPicker.cs:10` | Raycasts mouse → picks → dispatches Activate/Deactivate. **The whole click pipeline.** |
| `PickableActivationFilter` enum | `PickableActivationFilter.cs:1` | `Any` / `PrimaryOnly` / `SecondaryOnly` (LMB / RMB) |
| `PickableActivation` enum | `PickableActivation.cs:1` | `Primary` / `Secondary` |
| `PickableActivateEvent` struct | `PickableActivateEvent.cs:1` | `Activation` + `IsControlDown` + `IsShiftDown` |
| `MovingColliderScaler.Shared` (singleton) | `RollingStock/MovingColliderScaler.cs:8` | Scales `CapsuleCollider.radius` based on transform velocity so fast-moving picks remain hittable |
| `UI.ContextMenu.ContextMenu.Shared` | `UI.ContextMenu/ContextMenu.cs:16` | Radial pie menu (4 quadrants, two unused) shown on RMB pickables that build it |
| `RollingStock.ContinuousControls.ContinuousControl` | `RollingStock.ContinuousControls/ContinuousControl.cs:6` | Abstract `IPickable` with `Value`, `OnValueChanged`, `CheckAuthorized`, `Snap`, `MaxPickDistance` (5m default) |
| `RadialAnimatedControl` | `RollingStock.ContinuousControls/RadialAnimatedControl.cs:12` | The only meaningful subclass — twist valves, levers, knobs |
| `VerticalControl` | `RollingStock.ContinuousControls/VerticalControl.cs:6` | Mouse-Y drag variant. Used in vanilla? Yes (declared, but `RadialAnimatedControl` dominates) |
| `DummyControl` | `RollingStock.ContinuousControls/DummyControl.cs:3` | Empty subclass, used as a no-op placeholder when a cab control is missing |
| `ControlExtensions.ConfigurePropertyChange` | `RollingStock.ContinuousControls/ControlExtensions.cs:9` | The wire-up extension. Bridges `OnValueChanged` → `PropertyChange` with auth check |
| `Helpers.Layers.Clickable` | `Helpers/Layers.cs:17` | The Unity layer everything pickable lives on (`LayerMask.NameToLayer("Clickable")`) |

---

## Pickable spine: how a click becomes an `Activate`

```
Mouse event
   │  (PressInput debounces left/right press into PrimaryPress / ActivateSecondary; see input-keybinds.md)
   ▼
ObjectPicker.Update (every frame, ObjectPicker.cs:93)
   │  • IsMouseOverUI  ← suppress pickable interaction when over a UI panel
   │  • Caches PrimaryPressStartedThisFrame / Up / SecondaryPressedThisFrame
   │  • Drives the floating "callout" tooltip widget
   │
ObjectPicker.FixedUpdate (every physics tick, ObjectPicker.cs:147)
   │
   ├─ if (_active != null)  ← we're holding/dragging a previously-Activated pickable
   │     • Just refresh tooltip from _active.TooltipInfo
   │     • If primary released or mouse left screen → _active.Deactivate(); _active = null
   │
   └─ else (no active drag)
         Ray ray = camera.ScreenPointToRay(Input.mousePosition)
         TryGetPickableUnderMouse(ray, out picked, out distance) ←  ObjectPicker.cs:227
            │  • Iterates Physics.Raycast over Clickable | UI | Default | Terrain (max 500m)
            │  • For each hit on Clickable layer, GetComponentInParent<IPickable>()
            │  • Tracks highest Priority within MaxPickDistance
            │  • If priority >= 0, breaks; otherwise keeps walking through occluders
            │
         if (secondary && filter.Accepts(Secondary))
            picked.Activate(evt: Secondary); picked.Deactivate()       ← one-shot, no drag
         else if (primaryDown && filter.Accepts(Primary))
            _active = picked; _active.Activate(evt: Primary)           ← drag begins
         display picked.TooltipInfo

         if (Query)         show "Track grade/curvature" callout
         if (CopyLocation)  copy location/node to clipboard
```

### Layer + raycast model

- `_pickableLayerMask = (1 << Clickable) | (1 << UI) | (1 << Default) | (1 << Terrain)` (`ObjectPicker.cs:90`).
- The mask includes UI/Default/Terrain so they act as **occluders**; raycast steps through them but only `Clickable`-layer hits are candidate pickables. A non-Clickable hit before any high-priority pick can break the loop (line 267-268).
- Only colliders on `Layers.Clickable` (`LayerMask.NameToLayer("Clickable")` — vanilla scene-defined) are scanned for `IPickable`.
- `IPickable` is found via `GetComponentInParent<IPickable>()` (line 244). The pickable component does not have to live on the collider's GameObject — a child collider on the body picks up the parent's `IPickable`.
- **Missing pickable on Clickable hit logs a warning and breaks the loop**: `"Object {name} hit but no PickerBehavior"` (line 247) — every Clickable collider should resolve to an `IPickable` parent or it occludes the hits behind it.
- Max raycast range is **500 m** initially. Once a candidate is found at any priority, range shrinks to **2 m** (so deeper hits can only override if very close — used to prefer sub-pickables nested inside an outer collider, line 256).

### Priority semantics

The `Priority` int on each `IPickable` is the tiebreak when multiple pickables are within range:

- Higher wins. A negative `Priority` keeps walking through the geometry looking for something closer in priority.
- A `Priority >= 0` short-circuits the raycast walk once selected (`if (num >= 0) break;`, line 258).
- Concrete priorities seen in vanilla:

| Pickable | Priority | Why |
|---|---|---|
| `CarPickable` | **-1** | Lowest — let in-cab/hand pickables win when overlapping |
| `AvatarPickable`, `CTCPanelButton`, `OilPointPickable`, `MarkerLampToggle`, `IndustryContentHoverable`, `CouplerPickable`, `CTCSignalPickable`, `SwitchStandClick`, `KeyValuePickableToggle`, `FlarePickable` | **0** | Default. Most pickables. |
| `MarkerLampToggle`, `GladhandClickable`, *(another bias)*  | **1** | Beats default — gladhands sit inside coupler colliders, marker lamps inside car colliders |
| `ContinuousControl` (all subclasses) | **1** | Beats car body. So clicking a throttle handle while standing in a cab doesn't fall through to `CarPickable` |

**A Priority-1 pickable at the very edge of `MaxPickDistance` will lose to a Priority-0 pickable in the same path only if the higher one is out of range** (`num3 <= componentInParent.MaxPickDistance` gate, line 251).

### `MaxPickDistance` (vanilla per-type values)

| Pickable | MaxPickDistance | Use site |
|---|---|---|
| `ContinuousControl` (default) | 5 m | Configurable: `MaxPickDistance { get; set; } = 5f;` (`ContinuousControl.cs:50`) |
| `Anglecock` overrides its control to 15m | 15 m | `Anglecock.OnEnable` (`Anglecock.cs:96`): `control.MaxPickDistance = 15f` |
| `MarkerLampToggle` | 10 m | |
| `OilPointPickable` | 20 m | |
| `GladhandClickable` | 30 m | |
| `IndustryContentHoverable` | 40 m | |
| `KeyValuePickableToggle` | 50 m (default const) | `DefaultMaxPickDistance` const on type |
| `CouplerPickable` | 75 m | |
| `FlarePickable` | 100 m | |
| `CTCPanelButton` | 5 m | Tight — close-range CTC panels |
| `SwitchStandClick`, `CTCSignalPickable` | 200 m | Walk-up switch throw, signal aspect read at distance |
| `AvatarPickable` | 500 m | Click distant players. Ctrl-click opens `CompanyWindow.ShowPlayer` |
| `CarPickable` | 500 m | Inspect/select cars at any range |

These are all hard-coded properties; mods overriding interaction range must replace the property (Harmony getter prefix) or — for `ContinuousControl` — set the field in `OnEnable` like `Anglecock` does.

### `Activate` / `Deactivate` lifecycle

```csharp
void Activate(PickableActivateEvent evt);
void Deactivate();
```

Two distinct flows depending on `ActivationFilter`:

- **Tap pickables** (`PrimaryOnly` returning fast, `SecondaryOnly` always one-shot): `Activate(evt)` runs, then `Deactivate()` runs **immediately** in the same `FixedUpdate` (line 175-177 for secondary; primary tap-style pickables Activate-then-Deactivate next frame when LMB releases). Most pickables in vanilla are tap-style and use empty `Deactivate()` bodies.
- **Drag pickables** (`ContinuousControl`, `OilPointPickable`): `Activate(evt)` starts a held interaction (`_isActive = true`, kicks off coroutine). `Deactivate()` runs when LMB releases or the mouse leaves the screen (`flag2 = _activatePrimaryUp || !flag`, line 156).

`Activate` for primary clicks is dispatched in `FixedUpdate`, not `Update`. Don't use `Time.deltaTime` semantics; use `Time.fixedDeltaTime` if you need a delta in your handler.

### Tooltip ("callout") widget

- `ObjectPicker._callout` is a singleton floating tooltip instantiated in `Start()` from `calloutPrefab`.
- `IPickable.TooltipInfo` is read **every frame** (when active or hovering) and pushed to the callout (`ObjectPicker.cs:201`, `:184`). Don't allocate in your getter — most vanilla pickables cache via `_cachedTooltipText`.
- `TooltipInfo` is a `(Title, Text)` struct; either field can be null/empty. `IsEmpty` short-circuits the callout (`ObjectPicker.cs:325`).
- UI tooltips (over a panel) take precedence over world tooltips (line 99-108) and are shown after a 0.5s hover delay (line 137).

### Patch candidates (ObjectPicker)

| Method | Why patch |
|---|---|
| `ObjectPicker.TryGetPickableUnderMouse` | Override priority/distance comparison, add custom layers, intercept the picked result before it's surfaced. |
| `ObjectPicker.FixedUpdate` | Inject pre-Activate filtering — e.g. veto certain clicks based on game mode. |
| `ObjectPicker.CreateEvent` (private static) | Fold additional modifier state (e.g. Alt) into `PickableActivateEvent` — but you'd also need to extend the struct. |
| `ObjectPicker.QueryTooltipInfo` | Replace the `?`-key (Query) inspector — show different track info, or add a custom mode. |
| `ObjectPicker.CopyLocation` | Add new copy targets (e.g. signal-id, industry-id). |

### Gotchas (pickable layer)

- **`IsMouseOverUI` short-circuits all picking.** When a UI panel is under the cursor, `_displayTooltipInfo` is forced empty and only the *active* drag continues. This is checked from `GameInput.IsMouseOverUI` which uses `EventSystem.RaycastAll` and includes a `TooltipInfo` extracted from the UI element under cursor.
- **Click-and-drag uses `FixedUpdate`-rate sampling.** Mouse motion between fixed ticks is integrated, not subdivided. At 60Hz fixed update this is fine; at very low fixed-tick rates it's perceptibly choppy.
- **`MissingReferenceException` on `_active.TooltipInfo` silently drops the active drag** (line 203-207) — `_active = null`, no Deactivate called. If a pickable destroys itself mid-drag, expect leaked drag state in your subclass.
- **Layer `Clickable` is read by name at static init** (`Layers.Clickable`). If the layer doesn't exist in the build, returns `-1`, and `1 << -1` is undefined behavior on the bitmask — picking will silently fail. Mod loaders that strip layers will break the entire system.
- **Priority is signed int, not unsigned.** A `Priority = -1` (`CarPickable`) is meaningful — it means "let positives win, but also let other negatives compete". Don't accidentally use a default of 0 in your mod thinking it's the lowest.
- **`F8` toggles `_showDebugInfo`** which appends layer + name traces (`ObjectPicker.cs:122`, line 233/241). The output goes nowhere in vanilla (the field `_debugInfo` is set but never displayed). Hook it for live debugging.
- **`_callout` is a single shared widget.** You can't show two simultaneously. Mod overlays should use their own UI primitives.

---

## `MovingColliderScaler` (compensation for fast-moving picks)

Fast-moving pickables (couplers on a moving car, gladhands on a moving consist) would otherwise skip through the user's mouse cursor between frames. `MovingColliderScaler` runs a 1-Hz coroutine that resizes a registered `CapsuleCollider`'s `radius` based on the world-velocity of its `Transform`:

```csharp
[SerializeField] private float maxScale  = 2f;
[SerializeField] private float speedLow  = 3f;     // mph
[SerializeField] private float speedHigh = 10f;    // mph

// Loop:
float mph = Vector3.Distance(now, last) / dt * 2.23694f;
collider.radius = ColliderRadius0 * Mathf.Lerp(1f, maxScale, InverseLerp(speedLow, speedHigh, mph));
```

(Source: `RollingStock/MovingColliderScaler.cs`.)

- Stationary at <3 mph: radius = original.
- 10+ mph: radius = 2× original.
- Polled at 1 Hz — slow updates, but pickables are mid-air for at least one tick.

### Registration

```csharp
private void OnEnable()  => MovingColliderScaler.Shared.Register(GetComponent<CapsuleCollider>());
private void OnDisable() => MovingColliderScaler.Shared.Unregister(GetComponent<CapsuleCollider>());
```

`CouplerPickable.OnEnable` is the canonical example (`CouplerPickable.cs:31`). **Only `CouplerPickable` registers in vanilla** — gladhands, oil points, anglecocks all rely on their owners being parked or at low speed for accurate clicks.

### Patch candidates

| Method | Why patch |
|---|---|
| `MovingColliderScaler.Shared.Register` | Add your own moving pickable to the scaler. |
| `MovingColliderScaler.Loop` (private coroutine) | Tighten polling rate (currently 1Hz) for higher-fidelity moving picks. Hot-path though. |

### Gotchas

- **CapsuleCollider only.** A BoxCollider-based pickable on a moving car will not be size-compensated. `OilPointPickable` uses `BoxCollider[]`; this is why oil-point clicks at speed > 5 mph silently fail (`OilPointPickable.TooFast` short-circuits the activate at the higher level, line 93).
- **Singleton spawned lazily** as a hidden GameObject (`HideFlags.DontSave`) on first access. If your mod accesses `Shared` during `Awake`, the singleton is created mid-init — fine, but the loop coroutine doesn't start until `OnEnable` of the new GameObject.
- **Original radius captured on Register**: subsequent runtime mutation of `radius` will be overwritten next tick.
- **No motion implies the cached `_lastPosition` is current world-space**, not game-space, despite using `transform.GamePosition()` extension. Watch for `WorldDidMove` floating-origin shifts (see [Player & Camera › WorldTransformer](player-camera.md#worldtransformer-game--world-coords)) — if a world shift hits between Register and the next loop tick, the collider briefly scales to maxScale.

---

## `ContextMenu` (right-click radial pie)

A 4-quadrant pie wheel triggered by RMB-clicking pickables that explicitly build it. It is **not** automatic on pickables with `ActivationFilter.SecondaryOnly` — each pickable's `Activate(Secondary)` calls `ContextMenu.Shared.AddButton(...)` then `Show(...)` itself.

### Quadrant layout

```csharp
public enum ContextMenuQuadrant { General, Unused1, Brakes, Unused2 }   // ContextMenuQuadrant.cs
```

Vanilla uses **only `General` and `Brakes`** quadrants — `Unused1` and `Unused2` are reserved hooks. The angle-layout algorithm (`BuildItemAngles`) treats unused quadrants as gaps when distributing items.

### Surface

```csharp
public static ContextMenu Shared { get; }                                  // ContextMenu.cs:67
public static bool         IsShown { get; private set; }
public void Clear();                                                        // 389
public void AddButton(ContextMenuQuadrant, string title, SpriteName, Action);// 397
public void AddButton(ContextMenuQuadrant, string title, Sprite, Action);   // 402
public void Show(string centerText);                                        // 159
public void Hide();                                                         // 330
```

### Vanilla call sites

| Where | What | File |
|---|---|---|
| `CarPickable.HandleShowContextMenu(Car)` | Inspect / Select / Bleed (if supported) / Apply-or-Release Handbrake | `RollingStock/CarPickable.cs:86` |
| `SwitchStandClick.ShowContextMenu` | Lock / Unlock CTC switch (Brakes quadrant) | `SwitchStandClick.cs:61` |

That's it. Two pickables build the pie wheel. The Anglecock, CutLever, throttle, etc. do **not** use the radial menu.

### Standard show pattern

```csharp
var menu = UI.ContextMenu.ContextMenu.Shared;
if (UI.ContextMenu.ContextMenu.IsShown) menu.Hide();   // close existing
menu.Clear();                                           // reset items
menu.AddButton(ContextMenuQuadrant.General, "Inspect", SpriteName.Inspect, () => CarInspector.Show(car));
menu.AddButton(ContextMenuQuadrant.Brakes,  "Bleed",   SpriteName.Bleed,    car.SetBleed);
menu.Show(car.DisplayName);                             // center text
```

### MP authority

The menu itself is local UI. Each button's action is a normal `StateManager.ApplyLocal(...)` send or local mutator. Auth must be checked **before** `AddButton` — vanilla shows a Toast "No context options available." (`SwitchStandClick.cs:67`) when nothing is permitted.

### Patch candidates

| Method | Why patch |
|---|---|
| `CarPickable.HandleShowContextMenu` | Add mod-specific buttons to the per-car radial menu (e.g. "Set tag", "Spot to siding"). Postfix to append. |
| `SwitchStandClick.ShowContextMenu` | Same for switches. |
| `ContextMenu.AddButton` | Inject custom button decoration (sprite swap, color). |
| `ContextMenu.Show` | Hook for "context menu opened" — useful for "first-time tutorial" overlays. |

### Gotchas

- **Show repositions the menu** — if it's already shown and you call Show again without Hide, the menu re-renders at the new mouse position (vanilla's pattern is to call Hide() first).
- **Buttons cleared on Hide.** `_quadrants` is reset (`ClearQuadrants`, line 380); a stale callback is `null`'d (line 348). You can't "leave the menu populated and re-show".
- **`IsShown` is a static bool** — consult before opening modal UI to avoid layering.
- **Escape handler is registered as a Transient handler** on Show (`ContextMenu.cs:222`) — closes the menu on ESC. ESC also does normal Transient walk first; if your mod registers another Transient handler that doesn't return true, ESC goes through to the menu.
- **Quadrants `Unused1`/`Unused2` work** — try them. They're just unused in vanilla; the angle solver handles them. **High-value extension point** for mod-added functionality categories.

---

## `ContinuousControl` (the drag-to-set-a-value substrate)

Abstract base class extending `IPickable`. Wraps a `0..1` float value with debounced send semantics.

### Type shape

```csharp
public abstract class ContinuousControl : MonoBehaviour, IPickable {           // ContinuousControl.cs:6
    public string displayName;
    protected float value;
    public Func<bool>   CheckAuthorized = () => true;                           // auth predicate
    public Func<string> tooltipText     = () => "";                             // dynamic tooltip
    public float        ChangeThreshold = 0.01f;                                // debounce delta
    public Func<float, float> OnCustomSnap;                                     // value→snapped

    protected bool  _isActive;
    private   float _lastSentValue;
    private   float _lastSentTime;
    protected const float ZeroThreshold = 0.001f;

    public float Value { get; set; }                                            // setter no-ops while _isActive
    public int   Priority => 1;                                                 // beats body (-1) + default (0)
    public float MaxPickDistance { get; set; } = 5f;                            // SET-able, not just read
    public PickableActivationFilter ActivationFilter => PickableActivationFilter.PrimaryOnly;
    public TooltipInfo TooltipInfo => new TooltipInfo(displayName, AuthorizationAwareTooltipText());

    public event Action<float> OnValueChanged;

    public virtual void Activate(PickableActivateEvent evt);                    // sets _isActive
    public virtual void Deactivate();                                           // SendValue if changed

    private string AuthorizationAwareTooltipText();                             // shows "MouseNo N/A" if !CheckAuthorized
    protected void   UserChangedValue(bool force = false);                      // call from subclass when input moves value
    protected virtual void ValueDidChange();                                    // hook for external Value setter
    private bool   ShouldSendValue(bool deactivate);                            // throttle decision
    protected float Snap(float param);                                          // calls OnCustomSnap or identity
    public void    ConfigureSnap(int numberOfDiscreteValues);                   // notch helper
    private void   SendValue();                                                 // fires OnValueChanged + bookkeeping
}
```

### Send-throttling logic

Every time the subclass calls `UserChangedValue()`, `ShouldSendValue` decides whether to fire `OnValueChanged`:

```csharp
private bool ShouldSendValue(bool deactivate) {
    if (Time.realtimeSinceStartup - _lastSentTime > 1f) return true;            // 1s heartbeat
    float delta = Mathf.Abs(value - _lastSentValue);
    bool atRail = Mathf.Abs(value) < 0.001f || Mathf.Abs(value - 1f) < 0.001f;  // hit 0 or 1
    if (deactivate || atRail) return delta >= 0.001f;                           // strict on release/rails
    return delta >= ChangeThreshold;                                            // ChangeThreshold (0.01 default)
}
```

So a continuous drag sends at most one event per 1% of motion (configurable per-control via `ChangeThreshold`), but always sends on:
- Release (`Deactivate` calls `SendValue` if changed since last send)
- Reaching exactly 0 or exactly 1 (snap-to-rail emit)
- 1-second timeout (heartbeat — guarantees the host eventually sees the value even if motion is below threshold)

This is a **pre-network** debounce. The `OnValueChanged` callback is what actually issues the `StateManager.ApplyLocal` (via `ConfigurePropertyChange`); reducing the frequency here reduces network traffic.

### `Value` setter (external writes)

```csharp
public float Value {
    get => value;
    set {
        if (!_isActive) {                                                       // ← user dragging? skip
            bool changed = Mathf.Abs(this.value - value) > 0.001f;
            this.value = value;
            if (changed) ValueDidChange();
        }
    }
}
```

**External writes (e.g. from a KVO observer reflecting a remote player's change) are silently dropped while the local user is dragging.** This is by design: prevents a ping-ponging tug-of-war, but means brief external updates can be lost while a player has the handle. After release (`Deactivate` clears `_isActive`) the next external write applies normally.

### Subclasses (vanilla)

| Type | What it is | Mouse mode |
|---|---|---|
| `RadialAnimatedControl` | Twist valve / rotating handle | Either *angle on a plane* (when looking down at the rotation axis) OR *swept-arc on a sphere* (when looking from the side). Auto-selected per-Activate via `UseAngleManipulation()` (Z-axis dot test, `RadialAnimatedControl.cs:276`) |
| `VerticalControl` | Mouse-Y drag → 0..1 (1 px = 0.005) | Pure vertical mouse delta from down-position |
| `DummyControl` | Empty; no input handling | Used as no-op stand-in for missing cab controls |

### `RadialAnimatedControl` — the workhorse

Drives an `AnimationClip` (parked on a `PlayableHandle`) at `_animationValue * animationClip.length`. **Animation value is decoupled from data value** — `_animationValue` lerps to `value` at 20× `Time.deltaTime` (`RadialAnimatedControl.cs:127, :157`) so the visual is smoothed even when the data steps.

Key tunables (Inspector):

```csharp
public AnimationClip animationClip;
public Animator      animator;
public Axis          rotationAxis = Axis.Y;     // axis the handle spins about
public Axis          handleAxis   = Axis.Z;     // pointing direction at value=0
public float         rotationStart;             // °, offset for value=0
public float         rotationExtent = 90f;      // °, full sweep (set 360 for valves)
public float         radius = 1f;               // sphere radius for non-angle mode
public bool          momentary;                 // returns to homePosition on release
public bool          shiftActivateToggles;      // shift-click toggles 0/1
public float         homePosition;              // momentary return value
```

**Wraparound guard**: `if (rotationExtent != 360 && (sweep crossing > 90% delta)) return null;` (line 203-206) — prevents the value snapping from ~0 to ~1 across the rotation extreme on non-360° handles. Wraparound *is* allowed for full-360 handles (e.g. wheel-style hand brakes? not present in vanilla — but the code path supports them).

**Angle vs sphere manipulation**: chosen at `Activate` time (`RadialAnimatedControl.cs:233`):
- If camera angle to the rotation axis is shallow (>0.1 dot product with view direction): angle-manipulation. Cursor projects onto a plane perpendicular to the rotation axis through the handle pivot; rotation tracked as `Mathf.DeltaAngle(mouseDownAngle, currentAngle)`.
- Otherwise: sphere-manipulation. Cursor raycasts onto a sphere of `radius` around the handle; tracked as `Vector3.SignedAngle` between the down-vector and current-vector around the camera-local up.

**Camera-velocity compensation** (line 290-294): when the camera is moving slowly (<50 m/s, i.e. not a teleport), the ray origin is offset by `velocity * Time.fixedDeltaTime` so the cursor "sticks" to the handle while the camera glides. Critical for in-cab controls while the train is moving — without this, dragging the throttle while the cab moves yanks the value.

#### Momentary mode

`momentary = true` (e.g. horn): on `Deactivate`, runs `DeactivateMomentaryCoroutine` for up to 0.5s, lerping `value → homePosition` at 40× dt. `UserChangedValue(force: true)` is sent when value lands exactly at home, ensuring a final 0-pin (line 180).

#### Shift-toggle mode

`shiftActivateToggles = true` + Shift held + tap (release within 0.125s): jumps the value:
- 0 → 1
- 1 → 0
- in-between → `Round(value)` (i.e., snap to nearest of 0/1)

(`RadialAnimatedControl.cs:267-273`.) Used for binary controls that are still presented as a continuous knob.

### `VerticalControl`

Simpler. `Activate` records mouse-Y; in `FixedUpdate`, computes `0 - (currentMouseY - downMouseY) * 0.005` and clamps to `[0,1]`. Has the same `momentary` flag (returns to 0). Animation value is direct (no separate `_animationValue`).

### `Snap` and `ConfigureSnap`

```csharp
public void ConfigureSnap(int numberOfDiscreteValues) {
    OnCustomSnap = (float v) => Mathf.Round(v * (float)numberOfDiscreteValues) / (float)numberOfDiscreteValues;
}
```

`numberOfDiscreteValues = 1` snaps to {0, 1} (binary cut-out). `= 8` snaps to 1/8 increments (8-notch throttle). `= 100` snaps to whole percent (steam regulator). Set on the control after `ConfigurePropertyChange` if you want quantization.

`OnCustomSnap` is a `Func<float, float>` — set it to anything. The default (null) is identity (`Snap(p) => p`).

### `ConfigurePropertyChange` (the wire-up extension)

```csharp
public static void ConfigurePropertyChange(this ContinuousControl control,
    Func<float, PropertyChange> propertyChangeFunc, Func<string> tooltipText = null) {
    IGameMessage authorizedMessage = propertyChangeFunc(0f);
    control.OnValueChanged += value => StateManager.ApplyLocal(propertyChangeFunc(value));
    control.CheckAuthorized = () => StateManager.CheckAuthorizedToSendMessage(authorizedMessage);
    if (tooltipText != null) control.tooltipText = tooltipText;
}
```

(Source: `RollingStock.ContinuousControls/ControlExtensions.cs:9`.)

This is the standard glue. The first `propertyChangeFunc(0f)` call constructs a *sentinel* `PropertyChange` message used purely for the auth check — the value 0 is a placeholder. The actual sent value comes from each `OnValueChanged` invocation. **Auth is checked every tooltip render** (cheap), and **every send** (via `StateManager.ApplyLocal` permission gate).

### Wire-up sites in vanilla

| Site | File:line | Control(s) wired |
|---|---|---|
| `BaseLocomotive.ConnectBodyControls` | `Model/BaseLocomotive.cs:233-296` | LocomotiveBrake, TrainBrake, TrainBrakeCutOut (with `ConfigureSnap(1)`), Whistle, Bell |
| `DieselLocomotive.ConnectBodyControls` | `Model/DieselLocomotive.cs:68` | Throttle (no snap), Reverser (manual ±1 round in callback) |
| `SteamLocomotive.ConnectBodyControls` | `Model/SteamLocomotive.cs:302` | Throttle (with `ConfigureSnap(100)`), Reverser (with `ConfigureSnap(40)`) |
| `Anglecock.OnEnable` | `RollingStock/Anglecock.cs:91` | `control` (the anglecock handle); writes `f.anglecock`/`r.anglecock` via `ApplyEndGearChange` (NOT `ConfigurePropertyChange` — direct subscription) |
| `CutLever` (event, not direct wire) | `RollingStock/CutLever.cs:18` | Subscribes to control's `OnValueChanged`; primes a one-shot `OnActivate` event when value>0.5 (debounced) |
| `TurntableController.Start` | `Track/TurntableController.cs:67` | `controlLever.ConfigureSnap(20)`; subscribes via `controlLever.OnValueChanged += ControlLeverOnValueChanged` |

The diesel reverser uses a *manual* round-to-int rather than `ConfigureSnap(2)` because the value range is `0..1` mapped to `-1..1`:

```csharp
foundControl2.OnValueChanged += value => {
    int num = Mathf.RoundToInt(Mathf.Lerp(-1f, 1f, value));
    base.ControlHelper.Reverser = num;
};
```

The steam reverser uses `ConfigureSnap(40)` because Johnson bar quantizes finely.

### `ControlPurpose` enum (definition tagging)

```csharp
public enum ControlPurpose {                                                   // Definition/Model.Definition.Components/ControlPurpose.cs
    NotSet,
    CylinderCock,
    LocomotiveBrake, TrainBrake, TrainBrakeCutOut,
    Reverser, Throttle,
    Whistle, Bell,
}
```

Each `RadialAnimatedControl` carries a `ControlComponentPurpose` tag (`RadialAnimatedControl.cs:76`). `BaseLocomotive.TryGetControl(ControlPurpose, out ContinuousControl)` (`BaseLocomotive.cs:340`) walks `BodyTransform.GetComponentsInChildren<RadialAnimatedControl>()` and returns the first match. Tagging is set via:

- `BrakeStandController.Awake` (`BrakeStandController.cs:46`) — assigns `TrainBrake`, `LocomotiveBrake`, `TrainBrakeCutOut` to the three child controls of the brake stand prefab.
- Asset-pack-defined components (the editor lets you select `ControlPurpose` per `RadialAnimatedControl` in the car definition).

**Mods adding new control purposes can't extend the enum directly** (it's compiled in). You can either reuse an unused `NotSet` or wire your control directly via `OnValueChanged` from your own component awareness.

### Patch candidates (ContinuousControl)

| Method | Why patch |
|---|---|
| `ContinuousControl.SendValue` | Single chokepoint for *every* user-driven value emission. Postfix to log/observe. |
| `ContinuousControl.ShouldSendValue` | Adjust the 1s heartbeat / 0.01 threshold globally; or per-instance via `ChangeThreshold`. |
| `ContinuousControl.Activate` / `Deactivate` | Inject side-effects on grab/release (e.g., haptics, ambient audio, blocking other inputs). |
| `RadialAnimatedControl.ActiveCoroutine` | Replace the per-tick value calculation; e.g., add inertia. |
| `RadialAnimatedControl.UseAngleManipulation` | Override the angle-vs-sphere mode decision. |
| `ControlExtensions.ConfigurePropertyChange` | Replace the standard wire-up with a custom auth/send pipeline. Note: extension method — patch the *static* method, not an instance. |
| `BaseLocomotive.ConnectBodyControls` (and Diesel/Steam overrides) | Add new cab controls; the per-purpose `TryGetControl` lookup is the standard discovery path. |

### MP authority for ContinuousControl

The control itself does **no** MP work. Authority lives entirely in:

1. The `propertyChangeFunc` constructed message — auth resolved by the target object's `IPropertyAccessControlDelegate` (e.g. `Car.AuthorizationRequirementForPropertyWrite`, `Car.cs:3112`).
2. `CheckAuthorized` predicate — read every frame for the tooltip, can show "N/A" without sending. Defaults to `() => true` — if you don't call `ConfigurePropertyChange`, the control will look usable to anyone but the actual writes will fail silently host-side (`StateManager.ApplyLocal` rejects unauthorized sends).
3. `StateManager.ApplyLocal(message)` — the standard send path. Host receives, validates, broadcasts the resulting KVO write to all clients.

**Two clients can grab the same control simultaneously.** The `_isActive = true` is local-only; remote clients see the local user as not dragging, so the remote's KVO write fires. Both clients send `PropertyChange` messages; the host applies both in arrival order; the *later* write wins in the KVO. Visually, both clients see their own immediate response (because `_isActive` blocks the inbound KVO observer's `Value` setter), then snap to the host's resolved value when they release. **No tug-of-war prevention exists in the control layer** — if your mod needs single-occupant semantics, gate at the message handler with a custom lock KVO key.

### Race conditions / latency notes

- **Pre-send debounce ≥ ~10 ms (1% of value × user motion speed)**: a fast yank on a brake handle generates ~10 PropertyChange messages instead of 60+. Good for bandwidth, but a clientside graph-strip of the value lags slightly compared to the local input.
- **1-second heartbeat**: even with no motion, the control sends every 1s while held. After release, no more sends until next grab. Mods listening for "user is currently holding the throttle" can rely on that 1Hz pulse but should *also* watch `_isActive` if accessible.
- **Network-applied value blocked while local user drags**: a mod that rapidly toggles a control via `Value = x` will silently fail if the local user happens to be holding it. Only `_isActive == false` lets external writes through.
- **`OnCustomSnap` runs every tick during drag**: keep it cheap. The `ConfigureSnap(N)` lambda is `Round(v * N) / N` — fine; mod-defined snaps that allocate (LINQ, lookups) will GC-thrash.
- **Animation playable is per-instance**: `_clipPlayable = animator.PlayableGraphAdapter().AddPlayable(animationClip)` (`RadialAnimatedControl.cs:93`). Multiple controls on the same Animator share the graph adapter; respecting it requires using `PlayableGraphAdapter` rather than direct PlayableGraph access.

### Gotchas (ContinuousControl)

- **`MaxPickDistance` is settable on the field, not on a backing property override.** `Anglecock` sets it in `OnEnable` (`Anglecock.cs:96`); other overrides do the same. Setting it once at `Awake` is sufficient — there's no per-frame recompute.
- **`Awake` of `RadialAnimatedControl` *fixes* the layer if not Clickable** (`RadialAnimatedControl.cs:80`): logs a warning and force-sets `Layers.Clickable`. Same for `VerticalControl`. So even if you forget the layer, it's auto-corrected with a warning. Mods that use a different layer for their own pickables won't hit this fixup.
- **`OnDestroy` disposes the playable handle**, but `OnDisable` does *not* (it would for VerticalControl). Disable/re-enable cycles on `RadialAnimatedControl` keep the playable alive — this is intentional, the `_clipPlayable` is recreated in `OnEnable` only if null.
- **`AnimateToValue` coroutine is the only path that updates `_animationValue` outside of an active drag.** When external code writes `Value = x`, `ValueDidChange()` starts the coroutine *if* `isActiveAndEnabled`. A disabled control will jump to the new value visually only after re-enable (since `_animationValue` is set to `value` in `OnEnable`, line 95).
- **`CheckAuthorized` failures show a tooltip ("MouseNo N/A") but do not block the click.** A user can still grab the handle and drag — `Activate`/`UserChangedValue`/`OnValueChanged` all fire — but the target message is rejected by `StateManager.ApplyLocal` when it actually goes to send. This means clients can build local state mid-drag that doesn't match the host. Defensive UI usually queries `CheckAuthorized()` separately.
- **`displayName` is the tooltip title — set it on the prefab/inspector.** Empty `displayName` produces a blank-title tooltip.
- **`tooltipText` is a `Func<string>`** — re-evaluated every render frame. Vanilla uses lambdas closing over `Value`, e.g. steam throttle: `() => BaseLocomotive.Percent(throttleControl.Value)` (`SteamLocomotive.cs:318`). Don't allocate strings if you can help it; or memoize.
- **`OnValueChanged` is a multicast event.** Multiple subscribers all run per send. For mods adding observers, prefer subscribing in `OnEnable`/`OnDisable` of a sibling MonoBehaviour to avoid doubled subscriptions.
- **`Priority = 1` is hardcoded** on `ContinuousControl`. To make a custom control beat or lose to other pickables, override the property.
- **`OnCustomSnap` is applied inside the per-tick loop** *before* the value is pushed to the field. So `value` is always the snapped value; `_lastSentValue` comparisons all see snapped values.
- **`UserChangedValue(force: true)` skips the `ShouldSendValue` debounce** — used by momentary release to guarantee the final 0-pin lands. Mods using it should be sparing; force-sends bypass the bandwidth gate.

---

## `AvatarPickable` (other-player click)

```csharp
public class AvatarPickable : MonoBehaviour, IPickable {                       // Avatar/AvatarPickable.cs
    public float MaxPickDistance         => 500f;
    public int   Priority                => 0;
    public TooltipInfo TooltipInfo       { get; set; }                          // SET externally
    public PickableActivationFilter ActivationFilter => PickableActivationFilter.PrimaryOnly;
    public PlayerId PlayerId             { get; set; }

    public void Activate(PickableActivateEvent evt) {
        if (GameInput.IsControlDown && PlayerId.IsValid)
            CompanyWindow.Shared.ShowPlayer(PlayerId.String);
    }
    public void Deactivate() { }
}
```

Trivial pickable — Ctrl-click opens the player's company panel; plain click is a no-op. Tooltip is **set externally** (e.g. by the avatar manager when adding a player). Sits on `AvatarPrefab.Pickable` (see [Player & Camera › AvatarPrefab](player-camera.md#avataravatarprefab--avatarmanager)).

---

## `CouplerPickable` (the coupler-click shortcut)

See [Couplers › Pickable / interaction surface](couplers.md#pickable--interaction-surface) for the full coupler-flow explanation. Recap:

- `CouplerPickable.activate` is set in `Coupler.Awake` to `() => car.HandleCouplerClick(this)` (`Coupler.cs:45`).
- Clicking the coupler is **equivalent** to manipulating the cut-lever (`CutLever`) — both end at `Car.HandleCouplerClick`. The cut-lever is a `ContinuousControl` driven by drag; the coupler is a tap pickable.
- Registers with `MovingColliderScaler` for hit compensation while moving.
- `MaxPickDistance = 75 m`.

---

## `CutLever` (ContinuousControl event-rider)

```csharp
public class CutLever : MonoBehaviour {                                        // RollingStock/CutLever.cs
    public ContinuousControl control;
    private bool _primed = true;
    public event Action OnActivate;
    private void OnEnable()  { control.OnValueChanged += ControlDidChange; }
    private void OnDisable() { control.OnValueChanged -= ControlDidChange; }
    private void ControlDidChange(float value) {
        if (_primed && value > 0.5f) { OnActivate?.Invoke(); _primed = false; }
        else if (!_primed && value < 0.1f) { _primed = true; }
    }
}
```

A schmitt-trigger debouncer over a `ContinuousControl`. Crosses up through 0.5 → fires `OnActivate` once; resets when value drops below 0.1. **The `ContinuousControl` field is set on the prefab** — the `CutLever` MonoBehaviour is instantiated as a child in `Car.SetupCutLevers` (`Car.cs:1806-1822`); the prefab carries the `RadialAnimatedControl`/handle setup; the per-end `OnActivate` is subscribed by `Car.SetupCutLevers` to invoke `HandleCouplerClick(EndGearF.Coupler)` etc.

---

## All vanilla `IPickable` types — index

| Type | File | Purpose |
|---|---|---|
| `AvatarPickable` | `Avatar/AvatarPickable.cs` | Click other players (Ctrl=open CompanyWindow) |
| `CarPickable` | `RollingStock/CarPickable.cs` | LMB+Ctrl Inspect, RMB context menu (Inspect/Select/Bleed/Handbrake) |
| `CouplerPickable` | `RollingStock/CouplerPickable.cs` | Tap-uncouple shortcut |
| `CTCPanelButton` | `Track.Signals/CTCPanelButton.cs` | Press a code button on the CTC panel |
| `CTCPanelLamp` | `Track.Signals/CTCPanelLamp.cs` | (lamp; vanilla `Activate` no-op) |
| `CTCPanelMarker` | `Track.Signals.Panel/CTCPanelMarker.cs` | (marker; click destination?) |
| `CTCSignalPickable` | `Track.Signals/CTCSignalPickable.cs` | Show signal aspect tooltip; no-op activate |
| `ClassLightToggle` | `Effects/ClassLightToggle.cs` | Cycle class light states via KVO |
| `ContinuousControl` (`RadialAnimatedControl`, `VerticalControl`, `DummyControl`) | `RollingStock.ContinuousControls/` | Drag-to-set 0..1 values |
| `FlarePickable` | `Game/FlarePickable.cs` | Click your own placed fusee to extinguish |
| `GladhandClickable` | `RollingStock/GladhandClickable.cs` | Connect/disconnect gladhands |
| `IndustryContentHoverable` | `RollingStock/IndustryContentHoverable.cs` | Hover-only tooltip showing industry stockpile contents (Activate is empty) |
| `KeyValuePickableToggle` | `RollingStock.Controls/KeyValuePickableToggle.cs` | Generic bool-KVO toggle (configured in inspector) |
| `MarkerLampToggle` | `Effects/MarkerLampToggle.cs` | Cycle marker-lamp lit/position state via two KVO keys |
| `OilPointPickable` | `RollingStock/OilPointPickable.cs` | Hold to pump oil; gated by speed < 5 mph |
| `StationAgent` | `Model/StationAgent.cs` | (station NPC; click for dialogue?) |
| `SwitchStandClick` | `SwitchStandClick.cs` | LMB throw, RMB lock/unlock (CTC) context menu |

These are the **complete vanilla pickable types**. Mods adding new interactable objects implement `IPickable` and place a collider on `Layers.Clickable`.

---

## Patch points summary

| Goal | Patch target |
|---|---|
| Add a new pickable object | `MonoBehaviour, IPickable` + collider on `Layers.Clickable`. Done — `ObjectPicker` finds it automatically. |
| Add a custom continuous-control type | Subclass `ContinuousControl`, implement input handling in `FixedUpdate`/coroutine, call `UserChangedValue()` when the value moves. Wire via `ConfigurePropertyChange` or a direct `OnValueChanged` lambda. |
| Intercept clicks before vanilla handles them | Patch `ObjectPicker.FixedUpdate` or `ObjectPicker.TryGetPickableUnderMouse`. Or wrap the specific pickable's `Activate` with a Harmony prefix. |
| Change context-menu items for cars/switches | Postfix `CarPickable.HandleShowContextMenu` / `SwitchStandClick.ShowContextMenu` to add buttons before `Show()`. |
| Use the unused context-menu quadrants | `ContextMenu.Shared.AddButton(ContextMenuQuadrant.Unused1, ...)` works — vanilla just doesn't populate them. |
| Add Alt or other modifier to PickableActivateEvent | The struct only carries `IsControlDown` + `IsShiftDown`. Patch `ObjectPicker.CreateEvent` and add fields via reflection or replace the struct via Harmony transpile. (Or read modifiers in the pickable's Activate — `GameInput.IsAltDown` is private, but `Input.GetKey(KeyCode.LeftAlt)` works.) |
| Veto a player's click on a specific pickable | Prefix `Activate` and short-circuit; OR set `CheckAuthorized = () => false` on a `ContinuousControl` so the tooltip shows N/A (still allows click but blocks the send). |
| Listen to all continuous-control changes globally | Postfix `ContinuousControl.SendValue`. Single chokepoint for every user-emitted value. |
| Range-extend a pickable | Override `MaxPickDistance` getter (Harmony getter prefix). For `ContinuousControl`, set `control.MaxPickDistance = X` after instantiate. |
| Detect right-click on any pickable | Prefix `ObjectPicker.FixedUpdate` (look for `_activateSecondary`) — there's no per-pickable RMB chain unless `ActivationFilter.Accepts(Secondary)` returns true. |
| Add per-control snap behavior | Set `OnCustomSnap = v => myFunc(v)` on the ContinuousControl. Or `ConfigureSnap(N)` for evenly-divided snap. |

---

## MP authority across interactions (summary)

| Interaction | Authority surface |
|---|---|
| Clicking a tap pickable that calls `StateManager.ApplyLocal(msg)` | `msg` carries its own `MinimumAccessLevel` attribute; `StateManager.ApplyLocal` rejects unauthorized sends silently. |
| Continuous-control drag → `PropertyChange` | Authority resolved by the target object's `IPropertyAccessControlDelegate` (e.g. `Car.AuthorizationRequirementForPropertyWrite`). The control's `CheckAuthorized` is read for *display* only — the actual gate is the message-send. |
| Two players grabbing the same `ContinuousControl` | No control-layer arbitration. Both send messages; host applies in order; last-write-wins on KVO; the `_isActive` flag on each client *only* blocks inbound KVO updates locally. After both release, both see the host's final value. |
| MovingColliderScaler | Local-only client-side cosmetic. No MP. |
| Context menu | Local UI only. Each button's action is its own message. |
| Click on AvatarPickable | Local UI (`CompanyWindow.ShowPlayer`); no MP message. |
| Coupler tap (`CouplerPickable` → `HandleCouplerClick`) | Goes through `ApplyEndGearChange(CutLever, 1f)` — a Crew-auth KVO write (see [Couplers](couplers.md#cut-lever-pipeline-player-driven-uncouple)). |
| Anglecock drag | Goes through `ApplyEndGearChange(Anglecock, value)` — same Crew-auth path. |

**Important non-obvious latency artifact:** MP latency in continuous-control updates is bounded below by the `ChangeThreshold` debounce (≥ 0.01 value steps) and the 1Hz heartbeat. A remote observer sees the value step in 1%-or-larger increments rather than continuous motion. For visual smoothness on remote clients, the `_animationValue` lerp (20× dt) hides the staircase; for AI/automation reading the KVO directly, the staircase is visible.

**Race condition: two players reach for the same control.** Both `Activate`. Player A drags it to 0.5, sends "set to 0.5". Player B drags it to 0.2, sends "set to 0.2". Host applies both in arrival order; if A arrives first, the eventual KVO is 0.2 (B's last write). Player A is still dragging locally and sees 0.5; Player B is still dragging locally and sees 0.2. On release, both `_isActive` clear; the next host-side write (could be either's `Deactivate`-triggered `SendValue`) lands and propagates. **Both clients now see whichever value was the final-arriving write to the host.** No reconciliation, no arbitration.

---

## Cross-references

- Cut-lever / coupler tap-pickable details: [Couplers › cut-lever pipeline](couplers.md#cut-lever-pipeline-player-driven-uncouple) and [Couplers › Pickable surface](couplers.md#pickable--interaction-surface).
- Anglecock control wiring (uses ContinuousControl directly, NOT `ConfigurePropertyChange`): [Anglecock & Hose › two-layer model](anglecock-hose.md#two-layer-model-logical-vs-visual).
- Brake-stand `RadialAnimatedControl` instances + `ControlPurpose` tagging: [Brakes › brake input chain](brakes.md#brake-input--physics-chain).
- Throttle / reverser ContinuousControls per loco type: [Traction › traction spine](traction.md#traction-spine-how-a-notch-becomes-movement).
- `TeleportToMouse` raycast (uses `Layers.Clickable` for one special-case 2m pre-pick): [Player & Camera › TeleportToMouse](player-camera.md#teleporttomouse-raycasting-layers-cameraselectorcs442).
- Modifier keys (`IsShiftDown`, `IsControlDown`) read by pickables: [Input & Keybinds › modifier keys](input-keybinds.md#modifier-keys-legacy-inputgetkey-path).
- `PressInput` short-vs-long press underlying `PrimaryPressStartedThisFrame` / `ActivateSecondary`: [Input & Keybinds › PressInput](input-keybinds.md#uipressinput-short-vs-long-press-distinguisher).
- `GameInput.IsMouseOverUI` UI suppression: [Input & Keybinds › Mouse positions](input-keybinds.md#mouse-positions-mixed).
