# Car Fittings — Lighting, Doors, Cosmetics — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Definition/`)
**Companions:** [Cars, Cargo & Loading](cars-cargo.md), [Car Definitions & Components](car-definitions.md), [Asset Packs](asset-packs.md), [Interaction Controls](interaction-controls.md), [Access Control](access-control.md), [Time & Weather](time-weather.md), [UI (vanilla)](ui-vanilla.md)

This sheet covers three loosely-coupled per-car visual systems that all hang off the same `LoadModelsAsync` → `DidLoadModels` chain: **lighting** (headlights, class lights, marker lamps, lanterns, night-lights), **doors** (vanilla has none — but a KVO prefix is reserved), and **cosmetics** (color schemes, lettering decals, the `CarShaderHelper` interception, the `CarCustomizeWindow`). The three share a structural pattern: a JSON `Component` declares the fitting → an `IComponentBuilder` instantiates a prefab from the `shared` asset pack → a `MonoBehaviour` on that prefab observes one or more KVO keys on the parent `Car`'s `KeyValueObject`. Authority is mostly the **default Crew bucket** (lights, headlight, lanterns) or **Trainmaster prefix** (color, lettering); doors *would* be Passenger-auth if anything used them. **There is NO `DoorPickable` type in vanilla and no concrete door MonoBehaviour** — the `door.` / `gate.` PassengerPrefix entries (`Car.cs:469`) exist but match nothing. This is one of the cleanest mod-extension surfaces in the game.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Effects.HeadlightController` | `Effects/HeadlightController.cs:7` | Per-bulb visual: 3 states (Off/Dim/On), filament emission + Light intensity, day/night curve |
| `Effects.HeadlightStateLogic` | `Effects/HeadlightStateLogic.cs` | int↔(forward,reverse) bit-pack helpers; 2 bits per direction |
| `RollingStock.LocomotiveLightingController` | `RollingStock/LocomotiveLightingController.cs:9` | Per-loco aggregator. Observes KVO `headlight`, dispatches state to all `HeadlightController`s by Direction |
| `RollingStock.HeadlightControl` | `RollingStock/HeadlightControl.cs:14` | In-cab `RadialAnimatedControl` snap-3 / snap-5 → KVO `headlight` writer |
| `RollingStock.Controls.HeadlightToggleLogic` | `RollingStock.Controls/HeadlightToggleLogic.cs` | Static facade with 7-state `State` enum; `SetHeadlightStateOffset(kvo, ±1)` for keybinds |
| `Effects.ClassLight` / `ClassLightToggle` | `Effects/ClassLight.cs`, `ClassLightToggle.cs` | Pickable lamp with 2-color (Green/White) cycle. Two KVO keys: `<base>.lit`, `<base>.color` |
| `Effects.MarkerLamp` / `MarkerLampToggle` | `Effects/MarkerLamp.cs`, `MarkerLampToggle.cs` | Pickable lamp with 4-position rotating bezel and 3 lens colors. Keys: `<base>.lit`, `<base>.position` |
| `Effects.LanternController` | `Effects/LanternController.cs:8` | Avatar-side hand lantern; observes KVO `lantern.0` (default). Camera-billboarded |
| `Effects.NightLightController` | `Effects/NightLightController.cs:6` | Scene-light auto-on at 18:00–06:00 via `ClockDriver.Schedule`. Not per-car; it's scenery |
| `Effects.HeadlightController.SunLevel` getter | `HeadlightController.cs:58` | Reads `TimeWeather.SunLevel` to scale day/night intensity. **Only** day/night-aware light component |
| `RollingStock.CarColorController` | `RollingStock/CarColorController.cs:10` | Body-attached observer of `_colorScheme`. Fires `ColorSchemeChanged` event |
| `RollingStock.CarColorizer` | `RollingStock/CarColorizer.cs:5` | Per-material consumer of `ColorSchemeChanged`. Sets shader `_BaseColor` |
| `RollingStock.CarColorScheme` | `RollingStock/CarColorScheme.cs:8` | `readonly struct` (BaseHex, DecalHex). `From(Value)` ↔ `ToValue()` |
| `Effects.Decals.DecalProjectorHelper` | `Effects.Decals/DecalProjectorHelper.cs:14` | Per-decal: observes color scheme, renders text via `CanvasDecalRenderer`, manages culling |
| `Model.CarShaderHelper` | `Model/CarShaderHelper.cs:5` | Singleton. `ReplaceShaders(obj)` swaps `Railroader/Standard Car Shader (Shared)` → runtime shader. Called from `PrefabStore.LoadAssetAsync` (every load) |
| `UI.CarCustomizeWindow.CarCustomizeWindow` | `UI.CarCustomizeWindow/CarCustomizeWindow.cs:21` | The "Customize" `IBuilderWindow`. Reads `_colorScheme`, `lettering.basic`, `whistle.custom` |
| `Car.PostSetupComponentsHeadlights` | `Model/Car.cs:1433` | Stitches all `HeadlightController`s found under the body into a single `LocomotiveLightingController` |

---

## Lighting

### Spine

```
JSON Components on CarDefinition (HeadlightComponent / ClassLightComponent / MarkerLightComponent)
   │
   │ DidLoadModels → SetupComponents(ctx, ComponentLifetime.Model)   ← Car.cs:1294
   │
   ▼
HeadlightComponentBuilder / ClassLightComponentBuilder / MarkerLightComponentBuilder
   │  ctx.InstantiatePrefab<…>("headlight"|"class-light"|"marker-lamp", parent)
   │  prefabs come from the `shared` asset pack (always preloaded, see asset-packs.md#shared-pack)
   │
   ▼
Effects.HeadlightController / ClassLightToggle (+ ClassLight) / MarkerLampToggle (+ MarkerLamp)
   │  KeyValueObject.Observe(<key>, …)            ← per-light KVO
   │
   ▼
PostSetupComponents(ComponentLifetime.Model) hook (Car.cs:1425)
   │
   └─► PostSetupComponentsHeadlights() (Car.cs:1433)
       Finds all HeadlightControllers → wraps in LocomotiveLightingController(key="headlight")
       Tender special-case: KeyValueAdjacentCopier copies `headlight` from the F-end neighbor (the engine)
```

The two-stage pattern (per-bulb `HeadlightController` + aggregator `LocomotiveLightingController`) is unique to headlights. Class lights and marker lamps observe their own KVO keys directly — no aggregator.

### `Effects.HeadlightController` (per-bulb visual)

```csharp
public enum State { Off, Dim, On }
public enum HeadlightDirection { Forward, Reverse }

public State              state;              // SerializeField; settable from outside
public float              speedOn = 5f, speedOff = 2f;   // approach rate per state
public float              dimValue = 0.6f;
public Renderer           filamentRenderer;
public Transform          reflector;
public Light              light;
public EmissiveLightProfile emissiveLightProfile;

public HeadlightDirection Direction { get; set; }      // set by HeadlightComponent.Forward
public bool               LightEnabled { get; set; } = true;   // master enable from component

private static float SunLevel => TimeWeather.SunLevel;          // ← ONLY day/night-aware light

private void Update() {
   UpdateForParameter();   // lerps _parameter → ValueForState(state); applies Light.intensity scaled by SunLevel
   UpdateEmission();       // angular falloff toward camera; reflector scale + position; filament _EmissionColor
}
```

`UpdateForParameter` blends `light.intensity` between `emissiveLightProfile.lightIntensityNight` and `lightIntensityDay` by `SunLevel` — daytime headlights are dimmer (the curve is in `EmissiveLightProfile`, set per-prefab).

`UpdateEmission` does sophisticated camera-relative emission scaling (full beam at axis, fades by angle, distance-curve for visibility from far away) — patches that want to add custom visual effects (e.g., sun-glints) should hook here, *not* `state` setter, because `state` is set externally.

### `Effects.HeadlightStateLogic` (the wire format)

```csharp
public static int IntFromStates(State forward, State rear)
   => (BitsForState(rear) << 2) | BitsForState(forward);
public static (State forward, State rear) StatesFromInt(int intValue)
   => (StateFromBits(intValue & 3), StateFromBits((intValue >> 2) & 3));
```

2 bits per direction → 4 bits used out of int. Bit values: Off=0, Dim=1, On=2 (3 also maps to On via `>=2` clamp in `StateFromBits` — defensive). **The KVO key `headlight` is an `int`, not a float.** This is a singular asymmetry in the control-property family (most control keys are `float`).

### `RollingStock.LocomotiveLightingController` (aggregator)

```csharp
public string key;                        // "headlight" by default; set in Car.PostSetupComponentsHeadlights
public List<HeadlightController> headlights;

private void OnEnable() => _keyValueObserver = this.ObserveKeyValueDelayed(key, KeyDidChange);
private void KeyDidChange(Value value) {
   var (fwd, rev) = HeadlightStateLogic.StatesFromInt(value.IntValue);
   foreach (var h in headlights)
      h.state = h.Direction switch { Forward => fwd, Reverse => rev, _ => throw … };
}
```

`ObserveKeyValueDelayed` is a `Helpers` extension that runs the callback on the next frame (defers initial-value delivery). All headlights on a loco share one KVO key; the `Direction` field on each `HeadlightController` (set by the builder from `HeadlightComponent.Forward`) decides which half of the bit-pack drives that bulb.

**Tender mirroring:** When `Archetype == Tender`, `PostSetupComponentsHeadlights` adds a `KeyValueAdjacentCopier` (`Car.cs:1443`) that, in a 0.5s polling coroutine, reads the `headlight` KVO from the F-side adjacent car (the engine) and writes it to the tender's KVO. Tender backup-light follows engine. **The copier writes back to the local KVO directly with no host check** — every client mirrors locally. Because the engine's KVO `headlight` write is normal Crew-auth (no `_` prefix), every client sees the engine write and mirrors it onto their tender independently. Cheap and correct.

### `RollingStock.HeadlightControl` (the in-cab pickable)

```csharp
public RadialAnimatedControl control;
public HeadlightControlStyle style;          // Bidirectional (5 snaps) | Unidirectional (3 snaps)

private void OnEnable() {
   _headlightKey = PropertyChange.KeyForControl(PropertyChange.Control.Headlight);   // "headlight"
   control.OnValueChanged += ControlDidChange;
   control.ConfigureSnap(SnapPoints - 1);
   control.CheckAuthorized = () => StateManager.CheckAuthorizedToSendMessage(
       new PropertyChange(CarId, _headlightKey, new FloatPropertyValue(0f)));   // ← FloatPropertyValue, but KVO is Int!
   control.tooltipText = GetTooltipText;
   _observer = _keyValueObject.Observe(_headlightKey, v => HandleHeadlightValueChanged(v.IntValue));
}
```

**Auth quirk:** the `CheckAuthorized` lambda constructs a `FloatPropertyValue` for the auth probe — but the actual write uses `Value.Int`. Both go through the same per-key `AuthorizationRequirementForPropertyWrite`, and `headlight` has no prefix → falls through to default Crew. So this works, but the type mismatch is confusing if you patch `CheckAuthorized`.

`HandleHeadlightValueChanged` ↔ `ControlDidChange` form a feedback loop guarded by a `_controlDidChange` reentrancy bool. Bidirectional has 5 snaps mapping (in order) to `RearOn, RearDim, Off, ForwardDim, ForwardOn` — note the rear states are at low control values (the convention is "throw the lever forward → forward lights").

### `HeadlightToggleLogic` (the keybind facade)

7-state enum (`Off, ForwardDim, ForwardOn, RearDim, RearOn, BothFull, BothDim`) but the bidirectional control only uses 5 of them — `BothFull` and `BothDim` are reachable only via the **`SetHeadlightState(kvo, State)` static API** (e.g., a console command or a mod-bound key). `OffsetState` wraps and walks the 5-state `BidirectionalStates` array; mods wanting to use `BothFull`/`BothDim` must call `SetHeadlightState` directly. See [interaction-controls.md › ContextualOrder](interaction-controls.md) for keybind binding sites.

### `Effects.ClassLight` / `ClassLightToggle`

Class lights are the small forward-facing "what kind of train this is" indicators. Real-world: green=extra section following, white=extra train, red=last car (handled separately by markers).

```csharp
public enum LensColor { Green, White }                     // Effects/ClassLight.cs:9 (NO Red — that's MarkerLamp)
public bool       lit;
public LensColor  color;
public MeshRenderer lampRenderer;
public LensPalette  palette;
public (bool, LensColor) NextState() {                     // 91
   if (lit) return color switch {
       LensColor.White => (true, LensColor.Green),
       LensColor.Green => (false, LensColor.Green),
       _ => throw new Exception("Unexpected color " + color),
   };
   return (true, LensColor.White);
}
```

`ClassLight` is purely visual. `ClassLightToggle` is the `IPickable` wrapper:

```csharp
// Effects/ClassLightToggle.cs
public string keyBase = "class_light";                     // overwritten by builder to "classLight"
private void Start() {
    _litObserver   = kvo.Observe(keyBase + ".lit",   v => lamp.lit   = v.BoolValue);
    _colorObserver = kvo.Observe(keyBase + ".color", v => lamp.color = (ClassLight.LensColor)v.IntValue);
}
public void Activate(PickableActivateEvent evt) {
    var (lit, color) = lamp.NextState();
    kvo[keyBase + ".lit"]   = Value.Bool(lit);
    kvo[keyBase + ".color"] = Value.Int((int)color);
}
```

**Both class lights on a car share the same `keyBase = "classLight"`** (`ClassLightComponentBuilder.cs:29-30`) — clicking either L or R toggles both. Two pickables, one state. This was a deliberate choice (real prototype convention: class lights match left/right) but means modders cannot differentiate L/R via the existing component without subclassing the builder.

### `Effects.MarkerLamp` / `MarkerLampToggle`

Marker lamps (also called "markers" in the source — distinct from CTC-panel `marker-` keys, **see gotcha below**) are the last-car red lamps but support 3 lens colors and 4 rotational positions:

```csharp
public enum LensColor { Red, Green, Yellow }                   // Effects/MarkerLamp.cs:11
public int position;                                            // 0..3 (90° increments)
public LensColor[] lensColors = new LensColor[4];               // configured per-prefab; one per face
private bool _lit;
public (bool, int) NextState() {                                // 117
    if (_lit) {
        if (position == 3) return (false, position);
        return (true, position + 1);
    }
    return (true, 0);
}
```

`Update` lerps the bezel rotation to `position * 90°` at speed 10. Material instancing on each lens uses the `palette` (a `LensPalette` SO with `red`/`green`/`yellow`/`white` entries and an `emissiveMaterial` template).

`MarkerLampToggle.keyBase` is set by the builder to `"marker-f"` or `"marker-r"` based on `MarkerLightComponent.End` (`MarkerLightComponentBuilder.cs:42-43`).

#### **THE `marker-` KEY-COLLISION GOTCHA**

Marker lamp KVO keys are `marker-f.lit`, `marker-f.position`, `marker-r.lit`, `marker-r.position`. **The CTC panel marker manager** (`Track.Signals.Panel.CTCPanelMarkerManager.cs:28`) listens for ALL keys starting with `marker-` on its OWN `KeyValueObject`:

```csharp
private const string MarkerKeyPrefix = "marker-";
foreach (string item in _keyValueObject.Keys.Where(k => k.StartsWith("marker-")).Select(KeyToMarkerId))
    AddUpdateMarkerFromObject(item);
```

This is **not** a real collision in vanilla — `CTCPanelMarkerManager` operates on its own GameObject's `KeyValueObject`, while marker-lamp keys live on a `Car`'s `KeyValueObject`. Different KVO objects → no conflict. **But** the prefix overlap means that if a mod tries to introspect a Car's KVO keys to enumerate "all markers," they'll catch the marker lamp keys too. Don't confuse the two prefixes.

### `Effects.LanternController` (avatar-held lantern)

```csharp
public string onOffKey = "lantern.0";
public Light light;
public AnimationCurve parameterCurve;     // emission flicker curve
public AnimationCurve scaleCurve;          // distance-based size bump
private void Start() {
    KeyValueObject kvo = GetComponentInParent<KeyValueObject>();   // the Avatar's KVO, NOT a car
    if (kvo == null) _targetParameter = 1f;          // standalone (no avatar)
    else _observer = kvo.Observe(onOffKey, v => _targetParameter = v.BoolValue ? 1 : 0);
}
private void Update() {
    if (MainCameraHelper.TryGetIfNeeded(ref _camera)) {
        _parameter = Mathf.Lerp(_parameter, _targetParameter, Time.deltaTime * 5f);
        light.enabled = _parameter > 0.001f;
        // billboards filament toward camera; modulates emission via parameterCurve
    }
}
```

**Lanterns hang off `Character.LocalAvatar`, not off `Car`** (the only file that references `lantern.0` outside `LanternController` is `Character/LocalAvatar.cs`). Multiple lanterns supported by index suffix (`lantern.0`, `lantern.1`, …) but only one is wired in vanilla. There is **no IPickable wrapper** for a lantern — toggling is done via input bind in `UI.GameInput` (search `lantern.0`).

### `Effects.NightLightController` (scene night-light)

```csharp
[Range(0, 1)] public float threshold = 0.995f;     // unused — scheduling is hours-of-day
public Light light;
public Renderer bulbRenderer;

private void OnEnable() => _scheduleHandle = ClockDriver.Instance.Schedule(18f, 6f, SetOn);
//                                                                         ^^^^^  ^^
//                                            on at 18:00 game-time, off at 06:00
```

**Not per-car** — this is for scenery (station lights, depot lights). Listed here for completeness because it's the canonical pattern for "lighting that responds to game time." The `threshold` field is a vestigial hook from a sun-level-based earlier design; the live code uses `ClockDriver.Schedule(onHour, offHour, callback)` instead.

### Lighting KVO key map

| Key | Type | Owner | Auth | Wire format |
|---|---|---|---|---|
| `headlight` | int | `Car` | Crew (no prefix) | bit-pack: `(rearBits << 2) | forwardBits`, each bits ∈ {0=Off, 1=Dim, 2=On} |
| `classLight.lit` | bool | `Car` | Crew (no prefix) | shared between both class lights on a car |
| `classLight.color` | int | `Car` | Crew | `(int)ClassLight.LensColor` (0=Green, 1=White) |
| `marker-f.lit` / `marker-r.lit` | bool | `Car` | Crew (no prefix; `marker-` is NOT a Car prefix) | per-end |
| `marker-f.position` / `marker-r.position` | int | `Car` | Crew | 0..3 |
| `lantern.0` | bool | Avatar `KeyValueObject`, NOT Car | Owner-of-avatar | one-shot toggle |

**No HostOnly lighting keys.** All lighting is client-writable through the default Crew bucket (with optional train-crew membership check via `_storage.TrainCrewMembershipRequired`). Mods that want host-authoritative class lights must rename the key to start with `_`.

### Patch candidates (Lighting)

| Method | Why patch |
|---|---|
| `HeadlightController.UpdateForParameter` | Replace day/night intensity curve, add weather modifiers |
| `HeadlightController.UpdateEmission` | Add custom beam visuals (lens flares, dust scatter) |
| `LocomotiveLightingController.KeyDidChange` | Intercept aggregate state changes (e.g., custom bulb sets) |
| `HeadlightStateLogic.IntFromStates` / `StatesFromInt` | Extend wire format to more states (you'd also need to widen `BitsForState`) |
| `HeadlightToggleLogic.SetHeadlightStateOffset` | Inject custom rotation order for keybinds |
| `ClassLightToggle.Activate` | Replace `NextState` cycle (e.g., add Red, Yellow) — note class lights use the `ClassLight.LensColor` enum, not `MarkerLamp.LensColor` |
| `MarkerLampToggle.Activate` | Customize lamp cycle |
| `MarkerLamp.SetLensColors` | Per-instance lens-face configuration (e.g., asymmetric markers) |
| `ClassLightComponentBuilder._Build` | Change shared-keyBase to per-side keys (`classLight.l` / `classLight.r`) |
| `MarkerLightComponentBuilder._Build` | Customize positioning, swap prefab |
| `Car.PostSetupComponentsHeadlights` | Add per-car lighting subsystems (e.g., gyralights) |
| `LanternController.UpdateEmission` | Custom flicker curves |
| `NightLightController.SetOn` (callback to `ClockDriver.Schedule`) | Modify station-light timing globally; or replace the schedule call |

### MP authority (Lighting)

- All car-side lighting KVO writes are **default Crew with optional train-crew membership** (no prefix matches in `Car.AuthorizationRequirementForPropertyWrite`).
- Tender-side mirror via `KeyValueAdjacentCopier` is **per-client local** — every client polls its tender's adjacent car and writes locally. No race because everyone runs the same loop on the same shared engine state.
- `HeadlightControl.CheckAuthorized` constructs a probe `PropertyChange` to gate the in-cab lever; if a Crew player isn't on the train crew (and `TrainCrewMembershipRequired` is on), the control rejects locally.
- **No "lighting state" snapshot key** — KVO is the only persistence layer. A car spawning fresh (not from save) has all lighting keys absent → headlight=0 (off), class lights unlit (false from BoolValue default), marker lamps unlit at position 0.

### Gotchas (Lighting)

- **Headlight is the only KVO key in the `PropertyChange.Control` enum that uses int wire format** instead of float. Patches that observe `Control.*` keys uniformly via `FloatValue` will silently break for headlight.
- **`HeadlightController.SunLevel` is the ONLY place vanilla lights touch `TimeWeather`.** Class lights, marker lamps, lanterns, and `BulbController` do not dim at noon. If you need day/night-aware non-headlight lighting, you must add the read yourself.
- **`HeadlightControl.CheckAuthorized` constructs a `FloatPropertyValue`** for the auth probe even though writes use `Value.Int`. Prefix-patching `CheckAuthorized` with assumptions about the value type breaks.
- **Class lights share keyBase across L+R**: `ClassLightComponentBuilder` sets `"classLight"` on both — clicking either pickable affects both lamps. Cannot be split without re-building the component.
- **Marker lamp `keyBase` is `"marker-f"` / `"marker-r"`** — note the dash, not dot. The `<keyBase>.lit` / `<keyBase>.position` final keys become `marker-f.lit`, `marker-f.position`. Don't conflate with CTC `marker-` panel keys (different KVO objects).
- **`HeadlightToggleLogic.State` has 7 entries but the in-cab control reaches only 5.** `BothFull` and `BothDim` require external invocation (console, custom keybind).
- **`KeyValueAdjacentCopier` polls every 0.5s in a coroutine** — tender headlight mirroring is not instantaneous. Crashing/uncoupling will leave the tender in its last-mirrored state until the coroutine notices.
- **`LanternController` reads from `GetComponentInParent<KeyValueObject>()`** — if you instantiate a lantern under a Car (instead of an Avatar), it'll read the car's KVO and look for `lantern.0` there. This is harmless (no observer matches → stays at 0) but mod-confusing.
- **Class lights & marker lamps cycle on RIGHT-CLICK?** No — `ActivationFilter = PrimaryOnly` (`ClassLightToggle.cs:23`, `MarkerLampToggle.cs:29`). Left-click only.
- **Marker lamp lens-color array is per-prefab.** `MarkerLamp.lensColors` is `[SerializeField]` 4-entry — to make a modded marker with different colors, you ship a new prefab in your asset pack. The vanilla `marker-lamp` prefab uses the same color set on every car.
- **`HeadlightController.LightEnabled` is a *master* runtime toggle.** Set false from your mod and Light.enabled stays false even when state=On. Useful for "broken bulb" mods.
- **`NightLightController.threshold` field is dead** — `OnEnable` ignores it and uses hard-coded 18:00/06:00.

---

## Doors

### **Doors don't exist in vanilla.**

The auth-resolver in `Car.AuthorizationRequirementForPropertyWrite` reserves a `PassengerPrefixes` array (`Car.cs:469`) that contains `"door."` and `"gate."`. **No vanilla code writes or reads any key starting with `door.` or `gate.`** A repo-wide grep across `Assembly-CSharp/` finds zero matches outside the prefix declaration itself. There is:

- No `DoorPickable` type
- No `DoorComponent` / `GateComponent` in `Definition/`
- No `DoorBuilder` / `GateBuilder`
- No `IDoor` / `Door` MonoBehaviour
- No mention of doors in `PassengerStop.WorkCar` / `LoadCar` / `UnloadCar` (passenger boarding ignores doors entirely; see [passengers-timetable.md](passengers-timetable.md))
- No door-related Messenger event in [events-catalog.md](events-catalog.md)

**Boarding is purely instantaneous from a state perspective** — `PassengerStop`'s 3-second `WorkCar` coroutine simply transfers `WaitingPassengerGroup` ↔ `load.{n}` (the passenger-load slot) without animating, hiding, or even checking any door state. The visible "loading" period is the work-coroutine pacing, nothing else.

### What the prefix DOES enable (mod use)

The Passenger-auth prefix is **the cleanest mod-extension surface in `Car.cs`'s prefix table.** Any mod that adds keys starting with `door.` or `gate.` gets:

- **Passenger-level write auth** (`AuthorizationRequirement.MinimumLevelPassenger`) — even spectators can toggle them. Passengers cannot move trains but they can open doors. This was almost certainly designed for the multiplayer "passenger sits in the coach and opens the door at platforms" use case.
- **No train-crew check** — unlike default Crew bucket keys, Passenger keys have no `trainCrewId` requirement.
- **Replicates over the network like any KVO key** — host applies and broadcasts, clients mirror.

### Recommended mod recipe

```csharp
// Your component class
public class CoachDoorComponent : Component {
   public override string Kind => "CoachDoor";
   public string Side;        // "L" or "R"
   public string AnimationClipName;
}

// Your builder
[ComponentBuilder]
public class CoachDoorComponentBuilder : IComponentBuilder {
   public Type ComponentType => typeof(CoachDoorComponent);
   public void Build(ComponentBuilderContext ctx, Component component) {
       var c = (CoachDoorComponent)component;
       var pickable = ctx.GameObject.AddComponent<MyDoorPickable>();
       pickable.car = ctx.GameObject.GetComponentInParent<Car>();
       pickable.kvoKey = $"door.{c.Side.ToLower()}";   // ← falls under PassengerPrefixes
       pickable.clip = ctx.Resolve(new AnimationReference(c.AnimationClipName));
   }
}

// Pickable
public class MyDoorPickable : MonoBehaviour, IPickable {
   public Car car; public string kvoKey;
   public void Activate(PickableActivateEvent evt) {
       var cur = car.KeyValueObject[kvoKey].BoolValue;
       car.KeyValueObject[kvoKey] = Value.Bool(!cur);
   }
   // Observe in Awake to drive an Animator parameter from kvoKey → animation state
}
```

Auth resolves to `MinimumLevelPassenger`, replicated automatically. No request message needed. The `PassengerPrefixes` array is checked **before** `HostPrefixes`, so even a key like `door._secret` (with leading underscore in the rest) would still resolve as Passenger-write.

### Patch points for door behavior (for mods, not vanilla)

| Method | Why patch |
|---|---|
| `Car.AuthorizationRequirementForPropertyWrite` | Promote a specific door key to Trainmaster (override the prefix lookup) |
| `Car.SetupKeyValueObject` | Hook to add observers for door keys when the car KVO comes up |
| `PassengerStop.WorkCar` (see passengers-timetable.md) | Add a "doors must be open at this car's end" gate before transferring passengers |

### Gotchas (Doors)

- **The reservation has been there since at least the prefix-array introduction** (`access-control.md` notes it as "documented architectural intent"). Do not assume "no usage = abandoned" — this is held space for a future feature.
- **Passenger auth = anyone-not-banned can write.** If you're building doors for production, consider whether your design tolerates random spectators trolling open all doors mid-run. A mod-side `CanOpenDoor` veto guard in your pickable's `Activate` is a good idea.
- **Boarding doesn't animate.** If you add doors and want them to gate boarding, you must add the gate yourself in a `PassengerStop` patch — vanilla `WorkCar` will transfer passengers through closed doors.
- **`gate.` prefix is parallel.** Use `door.` for visible swing/slide doors; use `gate.` for catwalk gates, knuckle gates, or anything semantically distinct so different mods don't collide. (No vanilla code differentiates them; this is convention only.)
- **The `headlight` Component-PropertyChange enum is closed** (see [events-catalog.md](events-catalog.md)). Doors live entirely outside that enum — fine, because most KVO keys do.

---

## Cosmetics

### Spine

```
JSON Components on CarDefinition
   ColorizerComponent { Material: ref, HexColors: [paletteHex,…] }
   DecalComponent { Content: RoadNumber|Lettering, Size, ForceColor }
   │
   │ DidLoadModels (Car.cs:1267)
   │
   ▼
DidLoadModels prologue:
   gameObject.AddComponent<CarColorController>()              ← Car.cs:1282
   ctx.MaterialMap = BodyTransform.GetComponentInChildren<MaterialMap>()
   ctx.CarColorController = the component just added
   │
   ▼
ColorizerComponentBuilder._Build:
   palette = component.HexColors → Color[]    (sets CarColorController.palette field)
   AddComponent<CarColorizer>().targetMaterials = [resolved Material]
   │
DecalComponentBuilder._Build:
   AddComponent<DecalProjector>()
   AddComponent<DecalProjectorHelper>()  (templateName: "Number" or "Tender")
   for Lettering: ObserveProperty("lettering.basic", v => helper.text = … ; helper.RenderDecal())
   for RoadNumber: helper.text = car.Ident.RoadNumber  (immutable per spawn)
   │
   ▼
CarColorController.OnEnable: KeyValueObject.Observe("_colorScheme", UpdateForColorScheme)
   │
   ▼
   Fan-out via ColorSchemeChanged event:
      ─ CarColorizer → sets _BaseColor on targetMaterials
      ─ DecalProjectorHelper(s) → sets decal _Color
```

### `_colorScheme` KVO chain

```csharp
// RollingStock/CarColorScheme.cs:8
public readonly struct CarColorScheme(string baseHex, string decalHex)
{
    public const string ObjectKey = "_colorScheme";
    private const string SubKeyBase = "base";
    private const string SubKeyDecal = "decal";
    public static CarColorScheme From(Value value) =>
        new(value["base"].StringValue, value["decal"].StringValue);
    public Value ToValue() =>
        Value.Dictionary(new Dictionary<string, Value> {
            ["base"]  = Value.String(BaseHex),
            ["decal"] = Value.String(DecalHex),
        });
}
```

```csharp
// RollingStock/CarColorController.cs:36
private void UpdateForColorScheme(Value value) {
    Scheme = CarColorScheme.From(value);
    if (!Scheme.Base.HasValue && palette.Count > 0)
        Scheme = new CarColorScheme(ColorFromPalette(), Scheme.DecalHex);   // ← seed from palette by carId hash
    if (Scheme.Base.HasValue && !Scheme.Decal.HasValue)
        Scheme = new CarColorScheme(Scheme.BaseHex,
            (Scheme.Base.Value.IsDark() ? DecalColorLight : DecalColorDark).HexString());
    OnColorSchemeChanged(Scheme);
}

private string ColorFromPalette() {
    Car car = GetComponentInParent<Car>();
    return ColorFromPalette(palette, car).HexString();
}
private static Color ColorFromPalette(List<Color> palette, Car car) {
    if (car == null) throw new ArgumentException("null car", "car");
    if (car.ghost || palette.Count == 0) return Color.grey;
    int num = car.id.GetHashCode();
    if (num < 0) num *= -1;
    return palette[num % palette.Count];
}
```

**Per-prototype default vs per-car override:**

- The **palette** is a per-prototype default list of hex colors from `ColorizerComponent.HexColors`. If `_colorScheme.base` is missing, the car picks a deterministic palette entry by `carId.GetHashCode() % palette.Count` — same id always picks the same default. Ghost cars (placer preview) always get `Color.grey`.
- The **decal** auto-derives from the base if missing: light decal on dark bases, dark decal on light bases (`DecalColorLight=#CCC`, `DecalColorDark=#191919`).
- The **per-car override** is `_colorScheme = {base: "#hex", decal: "#hex"}` — written via `CarCustomizeWindow` or directly to KVO with Trainmaster auth.

### `_colorScheme` Trainmaster-prefix collision (canonical example)

`_colorScheme` starts with `_` (HostPrefix list) AND `_colorScheme` (TrainmasterPrefix list). `Car.AuthorizationRequirementForPropertyWrite` walks Officer → **Trainmaster** → Passenger → Host → fallback Crew. Trainmaster wins because it's checked first. **Result: Trainmaster-writable despite the underscore.** See [access-control.md › Issue 1](access-control.md#issue-1-_colorscheme-is-trainmaster-but-_-is-host--and-trainmaster-is-checked-first) for the deep dive. Re-ordering the prefix array would silently break car colors (only Host could write).

### `lettering.basic` KVO chain

```csharp
// UI.CarCustomizeWindow/CarCustomizeWindow.cs:31
public const string LetteringBasicKey = "lettering.basic";

// 300
private static void SetLettering(Car car, string newValue) {
    car.KeyValueObject["lettering.basic"] = string.IsNullOrEmpty(newValue)
        ? Value.Null()
        : Value.String(newValue.Truncate(100));   // ← 100-char hard cap
}

// Model.ComponentBuilders/DecalComponentBuilder.cs:60
case DecalContent.Lettering:
    ctx.ObserveProperty("lettering.basic", value => {
        if (string.IsNullOrEmpty(value.StringValue))
            helper.text = car.Archetype.IsFreight()
                ? car.Ident.ReportingMark
                : StateManager.Shared.RailroadName;
        else
            helper.text = value.StringValue;
        helper.RenderDecal();
    });
    break;
```

**Empty/null lettering picks a default:** freight cars show their reporting mark, everything else shows `StateManager.Shared.RailroadName`. So a fresh car immediately shows readable text without explicit customization.

`lettering.basic` is in `TrainmasterPrefixes` (`Car.cs:471`) — Trainmaster-write. 100-char cap enforced at the writer (`Truncate(100)`); also at the renderer (`text.Truncate(100)` in `DecalProjectorHelper.cs:162`). `MaxLetteringLength = 100` is a public constant on `DecalProjectorHelper` (`cs:44`).

### Other lettering customization KVOs

There is exactly one — `lettering.basic`. **No `lettering.advanced`, no `lettering.custom`, no decal-image-replacement key.** The `DecalComponent.Content` enum has only two values:

```csharp
// Definition/Model.Definition.Components/DecalContent.cs (inferred)
public enum DecalContent { RoadNumber, Lettering }
```

`RoadNumber` is read-once at component build time from `car.Ident.RoadNumber` and is not KVO-observed. To change a car's road number you go through `RequestCarSetIdent` (Trainmaster), then re-apply the decal — but `DecalProjectorHelper` is set at component build, so the road-number decal text is **set once at model load**. Re-loading the model (cull cycle) re-applies. Tender road numbers strip a trailing `T` (`DecalComponentBuilder.cs:52`).

**The Tender road-number convention:** Tenders share their reporting mark + a number suffix `…T` with their engine. The `RoadNumberAllocator` in vanilla appends `"T"` for tenders.

### `CarShaderHelper.ReplaceShaders` — the silent shader-swap

```csharp
// Model/CarShaderHelper.cs
public class CarShaderHelper : MonoBehaviour {
    public Shader shader;                          // the runtime replacement
    public Texture2D noiseDirtTexture, noiseNormalTexture;
    private const string SharedStandardCarShaderName = "Railroader/Standard Car Shader (Shared)";
    public const string BuiltinStandardCarShaderName = "Railroader/Standard Car Shader (Builtin)";
    public static CarShaderHelper Instance { get; private set; }
    private void Awake() => Instance = this;

    public void ReplaceShaders(Object obj) { … dispatch on GameObject vs Component … }
    private void ReplaceShaders(Component mb)
        => ReplaceShaders(mb, "Railroader/Standard Car Shader (Shared)", shader);
    private void ReplaceShaders(Component mb, string searchShaderName, Shader replacementShader) {
        foreach (var renderer in mb.GetComponentsInChildren<MeshRenderer>())
        foreach (var mat in renderer.sharedMaterials) {
            if (mat == null) { Debug.LogWarning(…); continue; }
            if (mat.shader == replacementShader) continue;
            if (mat.shader.name != searchShaderName) continue;
            mat.shader = replacementShader;
            mat.SetTexture(WearNoise, noiseDirtTexture);
            mat.SetTexture(WearNormalNoise, noiseNormalTexture);
        }
    }
}
```

**Two call sites in `PrefabStore`:**

```csharp
// PrefabStore.cs:181 (LoadWheelset, for truck prefabs)
GameObject asset = loadedAssetReference.Asset;
CarShaderHelper.Instance.ReplaceShaders(asset);   // ← unconditional null-deref if Instance not set
…

// PrefabStore.cs:229 (LoadAssetAsync, EVERY async load)
LoadedAssetReference<T> loadedAssetReference = await AssetPackForIdentifier(...).LoadAsset<T>(...);
if (CarShaderHelper.Instance != null) {
    CarShaderHelper.Instance.ReplaceShaders(loadedAssetReference.Asset);
}
return loadedAssetReference;
```

So **every asset returned by `PrefabStore.LoadAssetAsync` (Car bodies, scenery, whistle prefabs, anything via the prefab store) goes through shader replacement**. The wheelset path is **not null-guarded** (would NPE if you load a truck before `CarShaderHelper.Awake` runs). The general path **is** null-guarded.

**The replacement is shader-object-equality cheap:** matches by `material.shader.name == "Railroader/Standard Car Shader (Shared)"`. Materials using any other shader (custom mod shaders, the `Builtin` variant, Unity standards) pass through untouched.

**Side effects on every replaced material:**

```csharp
mat.shader = replacementShader;
mat.SetTexture("_WearNoise", noiseDirtTexture);
mat.SetTexture("_WearNormalNoise", noiseNormalTexture);
```

The wear-noise textures are pushed onto every replaced material — this is how the [wear-durability](wear-durability.md) condition shader effect is wired. Mods that want to deliver pre-textured wear noise must either avoid the replacement (use a different shader name) or accept the override.

**`MakeMaterialsUnique` runs AFTER the shader swap.** `Car.MakeMaterialsUnique` (`Car.cs:1236`) clones every shared material into per-car instances *after* `LoadAssetAsync` already swapped the shader. So per-car material instances have the replaced shader; the original asset's `sharedMaterials` is mutated globally on first load. **First-load mutations are permanent for the session.**

### Material substitution surface

| Surface | Where | Mutability |
|---|---|---|
| `MaterialMap` (component on body prefab) | `AssetPack.Common.MaterialMap` (one MonoBehaviour per body, has dictionary `name → Material`) | Resolved by `ComponentBuilderContext.Resolve(MaterialReference)` at component build time |
| `ColorizerComponent.Material` | `MaterialReference` (string name) → `MaterialMap.Resolve` | Resolved once per car at build; cached in `CarColorizer.targetMaterials` |
| `MaterialMap.ReplaceMaterials` | called from `Car.MakeMaterialsUnique` (`Car.cs:1263`) | Replaces the map's materials with the per-car cloned instances |
| `MakeMaterialsUnique` | `Car.cs:1236` | The clone happens here; subsequent `MaterialMap.Resolve` returns the per-car clone |
| `CarShaderHelper.ReplaceShaders` | `PrefabStore.LoadAssetAsync` | Mutates `sharedMaterials` of the loaded asset (NOT per-car clones) — runs first |
| `Car.ApplyBuilderPhotoMaterial(carShader, windowShader)` | `Car.cs:3045`, used only by `BuilderPhotoController` | Force-applies a specific shader to all renderers (debug/builder use) |

**Subtle: there's no public hook for "after MaterialMap.Resolve" for a car.** If a mod wants to substitute a material at car spawn time, options are:

1. Patch `MaterialMap.Resolve` (returns the replacement material for a specific name).
2. Patch `Car.MakeMaterialsUnique` postfix and walk `_ownedMaterials` to substitute.
3. Patch `ColorizerComponentBuilder._Build` for the specific case of color materials.

### `UI.CarCustomizeWindow.CarCustomizeWindow` (the IBuilderWindow)

Standalone `MonoBehaviour, IBuilderWindow`. Singleton-by-WindowManager (`WindowManager.Shared.GetWindow<CarCustomizeWindow>()`). `Show(Car car)` is the entry point.

#### Tabs (built top-to-bottom in one VScrollView)

| Section | Visible when | Method |
|---|---|---|
| `Identity` (always) | always | `BuildBasicsTab` — reporting mark + road number, gated by `CanCustomize` and `CanRenumber` |
| `Color` | `HasColorComponents(_car)` (any `ColorizerComponent`) | `BuildColorTab` — base + decal color dropdowns |
| `Lettering` | any `DecalComponent` with `Content == Lettering` | `BuildLetteringTab` — text input |
| `Tools` | if Color or Lettering tabs were built | `BuildCopyStyle` — "Copy to Coupled" button |
| `Whistle` | `_car.IsLocomotive` AND has `WhistleComponent` | `BuildSoundTab` — whistle dropdown writes `whistle.custom` |

#### Auth gate

```csharp
public static bool CanCustomize(Car car, out string reason) {
    reason = null;
    if (!StateManager.HasTrainmasterAccess) { reason = "Must be Trainmaster or higher"; return false; }
    if (IsSandbox) return true;
    if (!car.IsOwnedByPlayer) { reason = "Must be owned by your railroad"; return false; }
    return true;
}
```

Two-tier: Trainmaster + (sandbox OR owned). Mod patches that want to extend customization (e.g., per-prototype themes) should respect this gate.

#### `CopyToCoupled`

```csharp
private void CopyToCoupled() {
    var scheme = CurrentScheme();
    var lettering = GetLettering(_car);
    foreach (var item in _car.EnumerateCoupled()) {
        if (item.IsOwnedByPlayer) {
            if (HasColorComponents(item))            SetColorScheme(item, scheme);
            if (GetDecalComponents(item).Any())      SetLettering(item, lettering);
        }
    }
}
```

Walks the consist via `Car.EnumerateCoupled()` and applies — only to **player-owned cars**. Foreign cars in your consist (e.g., interchange cars) are skipped silently.

### Cosmetics KVO key map

| Key | Type | Owner | Auth | Default behavior |
|---|---|---|---|---|
| `_colorScheme` | Dict {base,decal: hex} | `Car` | **Trainmaster** (prefix override) | Falls back to palette[carId hash], decal auto-contrast |
| `lettering.basic` | string (≤100 char) | `Car` | Trainmaster | Falls back to ReportingMark (freight) or RailroadName (else) |
| `whistle.custom` | Dict | `Car` | Trainmaster | Falls back to `WhistleComponent.DefaultWhistleIdentifier`. See [audio.md](audio.md) |

### Patch candidates (Cosmetics)

| Method | Why patch |
|---|---|
| `CarColorController.UpdateForColorScheme` | Inject palette modifiers, weather-driven dirt/grime overrides, faction colors |
| `CarColorController.ColorFromPalette(List<Color>, Car)` static | Replace the carId-hash palette pick (e.g., consistent paint per train crew) |
| `CarColorizer.Replace` | Add per-material custom shader properties |
| `DecalComponentBuilder._Build` | Add new `DecalContent` cases (e.g., `Logo`, `Number2`) |
| `DecalProjectorHelper.RenderDecalAsync` | Replace text rendering pipeline (custom canvas templates) |
| `DecalProjectorHelper.ColorSchemeChanged` | Veto color propagation to specific decals |
| `CarShaderHelper.ReplaceShaders(Component, string, Shader)` | Add additional searchShaderName→replacementShader pairs |
| `PrefabStore.LoadAssetAsync` postfix | Run your own shader-swap pass (CarShaderHelper.Instance handles the vanilla one — yours can chain) |
| `CarShaderHelper.Awake` | Override `Instance` with a subclass for per-asset routing |
| `CarCustomizeWindow.Populate` | Add tabs (e.g., "Numberboards", "Plaque") |
| `CarCustomizeWindow.BuildLetteringTab` | Add multi-line lettering, font picker, vertical placement |
| `CarCustomizeWindow.CanCustomize` | Loosen/tighten the auth gate |
| `Car.MakeMaterialsUnique` | Inject post-clone material processing (your `_ownedMaterials` analog) |
| `MaterialMap.Resolve` | Substitute materials by name |
| `ColorizerComponentBuilder._Build` | Hook palette construction |

### Intercepting shader replacement

Three options:

1. **Block the swap** — patch `CarShaderHelper.ReplaceShaders(Component, string, Shader)` prefix and short-circuit (`return false`) for materials matching your name.
2. **Prevent vanilla matching** — author your prefab with a different shader name (anything other than `Railroader/Standard Car Shader (Shared)`). Vanilla won't touch it. The `BuiltinStandardCarShaderName = "Railroader/Standard Car Shader (Builtin)"` constant exists but the live `searchShaderName` parameter is hardcoded to the `Shared` variant.
3. **Chain after** — patch `PrefabStore.LoadAssetAsync` postfix and add your own swap pass on `loadedAssetReference.Asset` after `CarShaderHelper.Instance.ReplaceShaders` ran.

### MP authority (Cosmetics)

| Operation | Auth | Site |
|---|---|---|
| Set color scheme | Trainmaster (`_colorScheme` matches TrainmasterPrefixes before HostPrefixes) | KVO write directly |
| Set lettering | Trainmaster (`lettering.basic` is in TrainmasterPrefixes) | KVO write directly |
| Set whistle | Trainmaster (`whistle.custom` is in TrainmasterPrefixes) | KVO write directly |
| Reporting mark / road number | Trainmaster (`RequestCarSetIdent` request message) | `CarCustomizeWindow.ChangeReportingMark` → `RequestCarSetIdent` |
| `CopyToCoupled` | Same as the writes — Trainmaster + per-car ownership | Walks consist, fires N independent KVO writes |

`CarCustomizeWindow` registers for `RequestRejected` Messenger event in `RebuildUponRequestReject` so a denied ident change rebuilds the panel and shows the old value. Color/lettering writes don't have this rebuild path because KVO writes don't generate request rejections — they're applied locally first then snapped back if the host rejects (which won't happen unless auth is mis-configured).

### Per-prototype defaults vs per-car overrides

| Aspect | Per-prototype default | Per-car override mechanism |
|---|---|---|
| Base color | `ColorizerComponent.HexColors[]` palette + carId hash | `_colorScheme.base` |
| Decal color | Auto-contrast against base (light/dark) | `_colorScheme.decal` |
| Lettering text | Reporting mark (freight) or RailroadName | `lettering.basic` |
| Road number | `Definition.BaseRoadNumber` + RoadNumberAllocator | `RequestCarSetIdent` (immutable per spawn except via this) |
| Whistle | `WhistleComponent.DefaultWhistleIdentifier` | `whistle.custom` |
| Material | `ColorizerComponent.Material` (resolved via `MaterialMap`) | `_BaseColor` shader prop set by `CarColorizer` (does NOT replace material) |

**Important:** the per-car *base color* override modifies the `_BaseColor` shader property on the per-car-cloned material — it does **not** swap materials. To swap materials per-car requires `MakeMaterialsUnique` patches.

### Gotchas (Cosmetics)

- **`_colorScheme` writes are HostOnly UNLESS the prefix-array order is preserved.** Trainmaster vs Host depends on iteration order in `AuthorizationRequirementForPropertyWrite`. Don't reorder the loops.
- **`CarColorController.OnEnable` adds a one-shot observer** but writes from external sources during loading may race the observer registration. Vanilla works because Setup runs on host first then properties replicate, but mod-side scripted spawns can apply `_colorScheme` before the controller exists.
- **Palette picking is deterministic by carId hash.** Re-allocating a car id (e.g., a road-number change) will produce the same palette pick because road numbers don't change `Car.id`. But you can't easily re-roll a palette pick for the same car.
- **Ghost cars always render `Color.grey`.** `ColorFromPalette` short-circuits on `car.ghost`. Your customize-window preview of a ghost will not show the eventual color.
- **`lettering.basic` 100-char truncation** is enforced both at write (`SetLettering`) and at render (`DecalProjectorHelper.cs:162`). Direct KVO writes bypassing `SetLettering` are still safe at render time.
- **`DecalContent` enum has only two values** (RoadNumber, Lettering). New decal kinds need both an enum addition AND a `DecalComponentBuilder` switch case.
- **Tender road-number stripping** (`…T` → `…`) is hard-coded in `DecalComponentBuilder.cs:52`. Custom tender naming conventions need a builder patch.
- **`CarShaderHelper.ReplaceShaders` is called from a wheelset-load path that doesn't null-guard `Instance`** (`PrefabStore.cs:181`). If you replace `CarShaderHelper` in a way that defers `Instance` assignment, this path NPEs. The general path (`cs:229`) does null-guard.
- **`CarShaderHelper.ReplaceShaders` mutates the asset's `sharedMaterials` globally.** First load wins; subsequent loads of the same asset see the swapped shader. This is fine in vanilla because the swap is idempotent (`if (mat.shader == replacementShader) continue;`), but mod re-swaps may layer.
- **`MakeMaterialsUnique` runs AFTER the shader swap.** Per-car material instances inherit the swapped shader plus the wear-noise textures. Cloning then resetting the shader on the clone is safe but you'll lose the wear-noise texture refs unless you re-apply.
- **`CarCustomizeWindow.Show(Car)` is a singleton** (`WindowManager.Shared.GetWindow<>` returns the one instance). Re-entering with a different car mutates the displayed car.
- **`CopyToCoupled` ignores foreign cars silently.** No UI feedback for "skipped 3 of 5 cars because not owned." Mods may want to surface this.
- **`whistle.custom` is in TrainmasterPrefixes** but lives outside this sheet — see [audio.md › Whistle customization](audio.md).
- **There's no preview pipeline** — color/lettering changes write directly to the live KVO. No "preview before commit." A bad color choice on a coupled consist via `CopyToCoupled` is committed instantly across N cars.
- **`DecalProjectorHelper.RenderDecal` is async** and cancels prior renders via a `CancellationTokenSource`. Rapid-fire `lettering.basic` changes correctly cancel old renders, but the decal goes blank for a frame or two between cancel and re-render.
- **`CanvasDecalRenderer.Shared` is a singleton.** All decals queue through it; lettering changes can stutter under load.
- **`DecalComponentBuilder` reads `car.Ident.RoadNumber` at build time.** Changing the ident later does NOT update the road-number decal until the model reloads (cull cycle). The lettering decal IS re-rendered because it's KVO-observed; the road-number decal is not.
- **`Car.EnumerateCoupled` walks via `IntegrationSet`** — only works after the car joined a set. Newly-spawned cars in the placer may not be in a set yet.

---

## Cross-references

- **`_colorScheme` Trainmaster-prefix collision**: full explanation at [access-control.md › Issue 1](access-control.md#issue-1-_colorscheme-is-trainmaster-but-_-is-host--and-trainmaster-is-checked-first).
- **The `ClassLightToggle`/`MarkerLampToggle` pickable mechanics**: see [interaction-controls.md › IPickable index](interaction-controls.md) and the per-pickable Priority/MaxPickDistance table — markers and class lights both sit at Priority 1, MaxPickDistance 10m.
- **Headlight is in the `PropertyChange.Control` enum** as `Control.Headlight` (key `headlight`) — see [events-catalog.md › Control namespace](events-catalog.md). It's the only int-typed Control key in the closed enum.
- **`CarShaderHelper.ReplaceShaders` runs on every `LoadAssetAsync` return**: see [asset-packs.md › PrefabStore.LoadAssetAsync](asset-packs.md) for the broader pipeline.
- **Wear noise textures** pushed onto materials via `ReplaceShaders` feed the per-car wear shader: see [wear-durability.md](wear-durability.md).
- **Passenger boarding ignores doors**: see [passengers-timetable.md › PassengerStop work loop](passengers-timetable.md). The 3-second `WorkCar` coroutine is the only "boarding takes time" mechanism.
- **`whistle.custom`** lives in cosmetics here but its pipeline is in [audio.md](audio.md).
- **`SunLevel` source** (`TimeWeather.SunLevel`) for headlight day/night intensity: see [time-weather.md](time-weather.md).
- **`CarCustomizeWindow` as `IBuilderWindow`**: see [ui-vanilla.md](ui-vanilla.md) for the builder/window pattern.
- **`KeyValueAdjacentCopier`** (used by tender-headlight mirror): see [couplers.md](couplers.md) for the broader adjacent-state pattern.
- **`MaterialMap` / `AnimationMap`** asset-pack body components: see [asset-packs.md](asset-packs.md) and [car-definitions.md](car-definitions.md).
- **`ColorizerComponent` / `DecalComponent`** in the Component pipeline: see [car-definitions.md](car-definitions.md).
