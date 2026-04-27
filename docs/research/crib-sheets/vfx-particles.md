# VFX & Particles — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Rendering Pipeline](rendering-pipeline.md), [Floating Origin](floating-origin.md), [Audio](audio.md), [Locomotive Architecture](locomotive-architecture.md), [Cars & Cargo](cars-cargo.md), [Wear & Durability](wear-durability.md), [Settings & Preferences](settings-preferences.md)

Railroader's "particle" surface is **mostly Unity's VFX Graph (`UnityEngine.VFX.VisualEffect`), not the legacy `ParticleSystem`**. Only the floating-origin re-translate path and a handful of legacy controllers (`ParticleSettingsApplicator`, `KeyValueBoolParticlePlayer`, `ParticleCollision`) use the classic `ParticleSystem`. Everything chuff/exhaust/cyl-cock/hotbox/whistle/dynamo/derail/firebox is a `VisualEffect` graph parameterised at runtime via the `SmokeEffectWrapper` shim that pokes seven named Shader-property IDs (`Rate`/`Velocity`/`Lifetime`/`Color`/`Size0`/`Size1`/`TurbulenceIntensity`/`PositionOffset`). The "global particle quality" toggle is a single `Preferences.GraphicsParticleLevel` enum with three values — **but the UI only exposes Off and Standard, the `Low` value is dead** — and `ParticleSettingsApplicator` only honours `Off` (it `Stop()`s the attached `ParticleSystem`; it doesn't touch `VisualEffect`s, and Low/Standard are NOPs in the switch). The four locomotive-side particle controllers each guard their `Play()` calls with a private static `ParticlesEnabled => GraphicsParticleLevel > Off` reader, so VFX-Graph effects do honour the Off toggle — but they re-check it only at `OnEnable`/state-change time, not when the preference is mutated. There is **no particle-system pool, no recycle, no MP replication, no LOD curve** — every effect spawns at `OnEnable`, ticks per-`Update`, and stops at `OnDisable` or coroutine exit. Cars in Bardo (unloaded tile) tear down their VFX with the model; on respawn the controllers re-`OnEnable` and re-allocate.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Preferences.ParticleLevel` (enum) | `Game/Preferences.cs:21` | `Off / Low / Standard`. **`Low` is dead** (never selected by UI, treated as Standard everywhere) |
| `Preferences.GraphicsParticleLevel` | `Game/Preferences.cs:321` | PlayerPrefs `gfx.particlelevel` int (default `2` = Standard) |
| `Game.Settings.ParticleSettingsApplicator` | `Game.Settings/ParticleSettingsApplicator.cs:7` | `[RequireComponent(ParticleSystem)]` — `Stop()`s legacy systems on Off; **does not touch `VisualEffect`** |
| `RollingStock.Steam.SmokeEffectWrapper` (readonly struct) | `RollingStock.Steam/SmokeEffectWrapper.cs:6` | Thin VFX-Graph property setter (8 IDs cached). The single seam every smoke effect uses |
| `RollingStock.Diesel.SmokeEffectProfile` (ScriptableObject) | `RollingStock.Diesel/SmokeEffectProfile.cs:6` | 5 curves + 1 gradient (`rateCurve`/`velocityCurve`/`sizeCurve`/`lifetimeCurve`/`turbulenceCurve` + `colorGradient`) |
| `Audio.ChuffProfile` (ScriptableObject) | `Audio/ChuffProfile.cs:6` | Steam-only chuff profile: 5 effort→{alpha,velocity,size,lifetime,rate} curves + 1 audio rolloff |
| `RollingStock.Steam.SteamChuffParticleController` | `RollingStock.Steam/SteamChuffParticleController.cs:11` | Smokestack VFX. `ISteamLocomotiveSubcomponent` + `IDynamicChuffDelegate`. Two modes: discrete puffs (slow) / continuous (>5 m/s) |
| `RollingStock.Diesel.DieselExhaustParticleController` | `RollingStock.Diesel/DieselExhaustParticleController.cs:9` | Stack VFX driven by `NormalizedExhaustOutput` (set from `PrimeMoverAudioPlayer.NormalizedExhaustOutputEvent`). Multiple controllers per loco supported |
| `Effects.CylinderCockController` | `Effects/CylinderCockController.cs:15` | Two-sided steam puffs at cylinder height. `ISteamLocomotiveSubcomponent` + KVO observer of `Control.CylinderCock` |
| `Effects.WaterCylinderController` | `Effects/WaterCylinderController.cs:9` | Tank-water shader fill + spray VFX driven by a bool KVO key |
| `RollingStock.DerailedParticleController` | `RollingStock/DerailedParticleController.cs:10` | Per-truck dust plume. `ICarMovementListener` — driven from the per-tick movement loop |
| `Model.ComponentBuilders.DerailedEffectComponentBuilder` | `Model.ComponentBuilders/DerailedEffectComponentBuilder.cs:12` | Auto-injected by `Car.SetupComponents` for every car (synthesizes `DerailedEffectComponent` if not declared) |
| `RollingStock.HotboxEffect` | `RollingStock/HotboxEffect.cs:12` | Hotbox smoke + light. KVO observer of `Control.Hotbox`. One per axle, deterministic per-car selection |
| `Audio.PrimeMoverAudioPlayer.NormalizedExhaustOutputEvent` | `Audio/PrimeMoverAudioPlayer.cs:23` | `Action<float>` — diesel notch→exhaust intensity bus (audio is producer, VFX is consumer) |
| `RollingStock.Controls.KeyValueBoolParticlePlayer` | `RollingStock.Controls/KeyValueBoolParticlePlayer.cs:8` | Generic legacy `ParticleSystem` toggle keyed off any KVO bool. The only "data-driven legacy particle" hook |
| `ParticleCollision` (no namespace) | `ParticleCollision.cs:4` | Sub-emitter trigger for `ParticleSystem.OnParticleCollision` (one shipped use; mods can repurpose) |
| `Helpers.WorldTransformer.TranslateParticleSystems` | `Helpers/WorldTransformer.cs:176` | Per-shift O(scene) walk of every `ParticleSystem` in `World` simulation space; allocates `Particle[maxParticles]` per system |
| `Effects.Flare` | `Effects/Flare.cs:7` | `VisualEffect` + `Light` + mesh, gated by `CullingManager.Flare` distance bands (`[25, 1000]`) |
| `Effects.ClockDrivenVisualEffect` | `Effects/ClockDrivenVisualEffect.cs:8` | `[hourOn, hourOff]` window via `ClockDriver.Schedule`; `SetBool("Run", on)` on the VFX |

---

## Spine: how a particle effect gets driven

```
Car definition (asset pack)
   │
   ├─ ChuffComponent              ──→ ChuffComponentBuilder → instantiates "chuff" + "smokestack"
   │                                                            │
   │                                  "chuff"     prefab → contains Chuff (audio) + ChuffFilter
   │                                  "smokestack" prefab → contains SteamChuffParticleController (VFX)
   │
   ├─ DieselExhaustComponent      ──→ DieselExhaustComponentBuilder → instantiates "diesel-exhaust"
   │                                                                  (prefab contains DieselExhaustParticleController)
   │
   ├─ CylinderCockComponent       ──→ CylinderCockComponentBuilder → instantiates "cyl-cock"
   │                                                                 .Configure(Radius, ForwardOffset)
   │
   ├─ FireboxEffectComponent      ──→ FireboxEffectComponentBuilder → instantiates "firebox-fire-quad"
   │                                                                  (visual only — shader/light, no VFX-Graph)
   │
   └─ DerailedEffectComponent     ──→ AUTO-SYNTHESIZED for every car in Car.SetupComponents (Car.cs:1410)
                                       → DerailedEffectComponentBuilder → instantiates two "derailment-particles"
                                         prefabs at ±separation/2 forward offset.

Car.DidSetBodyActive (Car.cs:1303)
   _movementListeners.AddRange(BodyTransform.GetComponentsInChildren<ICarMovementListener>())
                                   │
                                   └─ DerailedParticleController  (the only vanilla ICarMovementListener)

SteamLocomotive.DidLoadModels (SteamLocomotive.cs:98)
   _subcomponents.AddRange(BodyTransform.GetComponentsInChildren<ISteamLocomotiveSubcomponent>())
                                   │
                                   ├─ Chuff  (audio-only, but listed because it implements the iface)
                                   ├─ SteamChuffParticleController
                                   ├─ SteamLocomotiveWheelAnimator (animation, not VFX)
                                   └─ CylinderCockController
   _chuffParticles  = GetComponentInChildren<SteamChuffParticleController>()
   _chuffAudio      = GetComponentInChildren<IChuffProvider>()         (Chuff)
   _chuffAudio.Delegate = _chuffParticles                               ← cross-wiring: audio schedules visual puffs

DieselLocomotive.DidLoadModels (DieselLocomotive.cs:46)
   _particleControllers = BodyTransform.GetComponentsInChildren<DieselExhaustParticleController>().ToList()
   _primeMoverAudioPlayer.NormalizedExhaustOutputEvent = (n) =>
       foreach (pc in _particleControllers) pc.NormalizedExhaustOutput = n;
```

**The VFX-vs-audio asymmetry between steam and diesel is load-bearing:**

- **Steam**: `Chuff` (audio) is the *driver*. It ticks at FixedUpdate, reads engine speed/cutoff/throttle, and at `engineSpeed < 5` schedules discrete audio chuffs via `ChuffFilter.GetNextChuffDelay`. When it schedules an audio chuff, it *also* calls `_chuffAudio.Delegate.ScheduleNextChuff(delay, 0.2f)` on `SteamChuffParticleController` — i.e., the **particle puff is co-fired with the audio puff** on the same delay. Above 5 m/s the visual goes `continuous = true` and the per-puff coroutine gives way to a per-`Update` `UpdateSmoke()` driven from `tractiveEffort`.
- **Diesel**: `PrimeMoverAudioPlayer` is the *driver*. Its `SetExhaust(notch)` fires `NormalizedExhaustOutputEvent.Invoke(InverseLerp(0,8,notch))` on every notch transition. The VFX controller(s) consume that float in `Update()`, lerp `_value → NormalizedExhaustOutput` at rate 0.2, derive `_accel = (_value - prev)/dt`, and sample the curves. There is no co-firing — exhaust is continuous, not pulsed.

Both branches gate `visualEffect.Play()` behind:
1. The component's `_locomotive.IsIdle` and `_locomotive.HasFuel` events (subscribed in `OnEnable` to `OnIdleDidChange` / `OnHasFuelDidChange`).
2. `ParticlesEnabled` (the static `GraphicsParticleLevel > Off` check).

---

## `Game.Settings.ParticleSettingsApplicator` — the global toggle (and its limits)

```csharp
[RequireComponent(typeof(ParticleSystem))]
public class ParticleSettingsApplicator : MonoBehaviour {                     // ParticleSettingsApplicator.cs:7
    private void Start() {
        if (gameObject.TryGetComponent<ParticleSystem>(out var component)) {
            switch (Preferences.GraphicsParticleLevel) {
                case Preferences.ParticleLevel.Off:
                    component.Stop();
                    break;
                default: throw new ArgumentOutOfRangeException();             // dead branch
                case Preferences.ParticleLevel.Low:
                case Preferences.ParticleLevel.Standard:
                    break;                                                    // NOP
            }
        }
    }
}
```

**Six high-value findings:**

1. **`[RequireComponent(typeof(ParticleSystem))]` — this applies *only* to legacy `ParticleSystem` components.** Drop it on a GameObject whose particles are a `UnityEngine.VFX.VisualEffect` and the component will fail its `RequireComponent` and either NRE or refuse to add. **Mods authoring VFX-Graph effects cannot use this script as-is.**
2. **It runs once at `Start`.** Toggling `GraphicsParticleLevel` at runtime does not retroactively `Stop()` already-started systems. Standard→Off requires a scene transition / car re-spawn / model reload to take effect on existing systems.
3. **Standard and Low are both NOPs.** The applicator never `Play()`s. The expectation is the `ParticleSystem` is authored with `Play On Awake = true`; the applicator only *suppresses* play when Off.
4. **The `default:` `throw new ArgumentOutOfRangeException()` is unreachable** because every enum value has a labelled case. It exists only to silence a compiler warning.
5. **The four locomotive VFX controllers (`SteamChuffParticleController`, `DieselExhaustParticleController`, `DerailedParticleController`, `DynamoPlayer`, plus `HotboxEffect` / `CylinderCockController` via different paths) gate their VFX on `ParticlesEnabled` themselves** — see the per-controller sections. So the runtime VFX honour Off, but they re-check at `OnEnable` / state-change time, not when the user moves the slider. Same caveat: change preference, then disturb the loco (idle→active, refuel, etc.) for it to take.
6. **`Effects.Decals.DecalCullingManager` does not consult `ParticleLevel`.** Decals (track decals, lettering, wear noise) are a separate budget controlled by `decalBudget = 200` — see [`rendering-pipeline.md`](rendering-pipeline.md#effectsdecalsdecalcullingmanager-the-decal-culler).

### `Preferences.ParticleLevel` (the dead enum value)

```csharp
public enum ParticleLevel { Off, Low, Standard }                              // Preferences.cs:21
```

`Low` is **never selected by the UI**. `UI.PreferencesWindow.PreferencesBuilder` (`PreferencesBuilder.cs:145-156`) builds the dropdown from a 2-entry array `[Off, Standard]` and labels them `["Off", "Standard"]`. Saved games / mods that wrote `1` (Low) load it back as `ParticleLevel.Low` and `GraphicsParticleLevel > Off` is `true` — i.e., **Low behaves identically to Standard** at every runtime check site. Cross-link [`save-load.md`](save-load.md) on the `gfx.particlelevel` key being a `PlayerPrefs` int (not in save snapshots; per-machine like all `Preferences`).

### Patch candidates

| Method / target | Why patch |
|---|---|
| `Preferences.GraphicsParticleLevel` getter | Inject a `Medium` or `Ultra` mod-side bucket. Beware the int round-trip via `PlayerPrefs` (cast through `(int)`). |
| `ParticleSettingsApplicator.Start` (postfix) | Apply mod policy to legacy `ParticleSystem`s globally without each script consulting prefs. |
| `PreferencesBuilder.AddParticleLevelDropdown` (the inline call site) | Add Low / Medium options to the UI. Currently the array is hardcoded local. |
| `static bool ParticlesEnabled` properties on each VFX controller | Make particle gating preference-driven without editing the prefab. Each controller declares its own private static — five copies (steam/diesel/derail/dynamo + the inline reads in cyl-cock/hotbox). Patching one does not affect the others. |

### MP authority

`Preferences.GraphicsParticleLevel` is **per-machine** (PlayerPrefs). No KVO, no replication, no `_game` storage. Two players in the same MP session can disagree on particle quality with no consequence. There is no host-authoritative VFX state at all — every visual effect is a local read-out of replicated game state (KVO keys, control properties, idle/fuel events).

### Gotchas

- **`Preferences.ParticleLevel.Low` is dead** in every consumer; do not author features that assume Low ≠ Standard.
- **The applicator's `[RequireComponent]` is `ParticleSystem` only**, but every non-legacy effect in the game is `VisualEffect`. The applicator therefore covers a small minority of the actual VFX surface (probably only ambient scenery particles authored with the legacy system; the locomotive/car path is entirely VFX-Graph).
- **`GraphicsParticleLevel > Off` is the only check pattern** — the > comparison treats Low and Standard identically.
- **No "Update applies preference" path.** Changing the preference at runtime requires the consumer to be re-`OnEnable`d. There is no `ObservePreference` / `Messenger.Send` for particle quality.
- **No particle pool.** Every effect lives on its prefab instance. When a car unloads (`Car.UnloadModels`, model reload, Bardo) the `VisualEffect` instances are destroyed with the model and re-instantiated on next load. There is no checkout / return like `VirtualAudioSourcePool`.

---

## `RollingStock.Steam.SmokeEffectWrapper` — the universal VFX-Graph shim

```csharp
public readonly struct SmokeEffectWrapper(VisualEffect effect) {              // SmokeEffectWrapper.cs:6
    private readonly VisualEffect _effect = effect;
    private static readonly int VFXNameRate           = Shader.PropertyToID("Rate");
    private static readonly int VFXNameVelocity       = Shader.PropertyToID("Velocity");
    private static readonly int VFXNameLifetime       = Shader.PropertyToID("Lifetime");
    private static readonly int VFXNameColor          = Shader.PropertyToID("Color");
    private static readonly int VFXNameSize0          = Shader.PropertyToID("Size0");
    private static readonly int VFXNameSize1          = Shader.PropertyToID("Size1");
    private static readonly int VFXNameTurbulenceIntensity = Shader.PropertyToID("TurbulenceIntensity");
    private static readonly int VFXNamePositionOffset = Shader.PropertyToID("PositionOffset");

    public bool   IsValid           => _effect != null;
    public float  Rate              { get; set; }   // → _effect.Get/SetFloat(VFXNameRate)
    public float  Velocity          { get; set; }
    public float  Lifetime          { get; set; }
    public float  Size0             { set; }        // setter only
    public float  Size1             { set; }        // setter only
    public float  TurbulenceIntensity { set; }      // setter only
    public Vector4 Color            { get; set; }
    public Vector3 PositionOffset   { get; set; }
}
```

**Every smoke / steam / dust VFX in the game is parameterised through these 8 named exposed parameters.** A custom VFX-Graph asset that wants to be drop-in compatible with vanilla controllers must declare exposed parameters matching exactly these names and types. (`Color` is a `Vector4` not `UnityEngine.Color`, but Unity coerces transparently in `SetVector4`.)

The struct is a `readonly struct` value type — instantiating it via `new SmokeEffectWrapper(effect)` is allocation-free. Vanilla controllers cache one instance in a private field; mod controllers should follow.

---

## `RollingStock.Diesel.SmokeEffectProfile` — the curve bundle

```csharp
[CreateAssetMenu(fileName = "Smoke Effect Profile", menuName = "Railroader/Smoke Effect Profile")]
public class SmokeEffectProfile : ScriptableObject {                          // SmokeEffectProfile.cs:6
    public Gradient        colorGradient;
    public AnimationCurve  velocityCurve   = Linear(0, 3,  1, 20);
    public AnimationCurve  sizeCurve       = Linear(0, 0.4, 1, 0.8);
    public AnimationCurve  lifetimeCurve   = Linear(0, 4,  1, 8);
    public AnimationCurve  rateCurve       = Linear(0, 200, 1, 400);
    public AnimationCurve  turbulenceCurve = Linear(0, 10, 1, 10);
}
```

Used by `DieselExhaustParticleController` and `DerailedParticleController`. Curves are evaluated against a normalized `[0,1]` driver value (exhaust output for diesel, derailment+velocity for derailed dust). Mods that ship their own loco can author a `SmokeEffectProfile.asset` and assign it to the controller in the prefab.

`Audio.ChuffProfile` is the steam analogue (different field names: `effortToRate` etc.) — used by `SteamChuffParticleController`.

---

## `RollingStock.Steam.SteamChuffParticleController` — steam stack VFX

```csharp
public class SteamChuffParticleController : MonoBehaviour, ISteamLocomotiveSubcomponent, IDynamicChuffDelegate {  // SteamChuffParticleController.cs:11
    public VisualEffect  visualEffect;
    public ChuffProfile  profile;
    public float         tractiveEffort;     // smoothed
    public float         absVelocity;
    public bool          continuous;         // true above 5 m/s — switch to per-Update mode
    public bool          isStopped;          // velocity < 0.01 m/s

    private static bool ParticlesEnabled => Preferences.GraphicsParticleLevel > Preferences.ParticleLevel.Off;

    public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser, float absThrottle, float driverPhase) {
        absVelocity = Mathf.Abs(driverVelocity);
        isStopped   = absVelocity < 0.01f;
        continuous  = absVelocity > 5f;
        _targetTractiveEffort = (isStopped || !_locomotive.HasFuel) ? 0f : absThrottle;
    }

    public void ScheduleNextChuff(float delay, float chuffDuration) {        // IDynamicChuffDelegate
        if (!continuous && ParticlesEnabled) {
            if (_puffCoroutine != null) StopCoroutine(_puffCoroutine);
            _puffCoroutine = StartCoroutine(Puff(delay, chuffDuration));
        }
    }
}
```

**Two-mode design:**

- **Discrete mode** (`!continuous` — speed ≤ 5 m/s ≈ 11 mph). The audio `Chuff` (`Chuff.cs:78`) calls `Delegate.ScheduleNextChuff(nextChuffDelay, 0.2f)` whenever its sample-time-based `GetNextChuffDelay` is < 0.1s. The delegate (this controller) starts a `Puff` coroutine that sleeps `delay` seconds then ramps `Velocity`/`Rate` from full down to zero over `chuffDuration * 0.6`. **The visual puff is rate-driven by the audio engine's chuff timing** — the two are tightly coupled.
- **Continuous mode** (speed > 5 m/s). `Update()` calls `UpdateSmoke()` every frame to set `Rate`/`Velocity`/`Lifetime`/`Size`/`TurbulenceIntensity`/`Color` from the `effort*` curves of `ChuffProfile`. The puff coroutine is not started; visuals are a steady plume modulated by tractive effort.

The `tractiveEffort` field is smoothed: `tractiveEffort = Lerp(tractiveEffort, _targetTractiveEffort, dt * (lower<higher ? 2 : 4))` — i.e., it ramps **up at rate 2/s and down at rate 4/s**. Asymmetric: smoke clears faster than it builds.

`UpdatePlayStop` is wired via `OnEnable` to `_locomotive.OnIdleDidChange` and `_locomotive.OnHasFuelDidChange` C# events. **`visualEffect.Play()` is only called if `ParticlesEnabled && !IsIdle && HasFuel`.**

### Patch candidates

| Method | Why patch |
|---|---|
| `ApplyDistanceMoved` | Per-position-update tick (called from `SteamLocomotive.SubcomponentsApplyDistanceMoved`, `SteamLocomotive.cs:422`). Hot path — patch postfix to mod custom VFX state from physics inputs. |
| `Update` | Per-frame VFX update (continuous mode only). Patch postfix to override curve evaluation. |
| `UpdateSmoke` | Direct override of the `SmokeEffectWrapper` writes. Patch this if you want different smoke shape per locomotive subclass. |
| `ScheduleNextChuff` | Each discrete puff entry. Patch prefix to skip / replace per-puff timing. |
| `UpdatePlayStop` | Idle/fuel state-change handler. Patch prefix to gate `Play()` on additional conditions (e.g., a "cold engine" delay). |
| `static ParticlesEnabled` getter | Replace particle-level policy specifically for chuff. |

### MP authority — none

All inputs (`HasFuel`, `IsIdle`, `AbstractThrottle`, `_wheelVelocity`, `DriverPhase`) are locally computed from already-replicated KVO state. There is **no MP message for "play a chuff puff"**; both clients independently fire puffs from their own audio engines from their own observation of throttle/velocity state. Visual de-sync between clients is possible (they will not chuff in lockstep) — this is intentional.

### Gotchas

- **`continuous` flips at 5 m/s with no hysteresis.** Locomotives oscillating around 5 m/s will flicker between discrete and continuous modes once per crossing.
- **`Puff` coroutine uses `Time.realtimeSinceStartupAsDouble` for the chuff-duration window**, not `Time.time`. Pause does not freeze a puff in progress; debug-paused effects continue to ramp down.
- **`OnDisable` doesn't `Stop()` the visual effect** — only unsubscribes events. Disabling the controller mid-puff leaves the VFX playing until its particles age out.
- **`_locomotive` is captured in `OnEnable`** via `GetComponentInParent<BaseLocomotive>()`. If the controller is reparented at runtime (it isn't, in vanilla), the cached ref becomes stale.
- **`profile` is a public field**, not a property — assignable from outside but not observable. Live-swap requires a `OnDisable`/`OnEnable` cycle.

---

## `RollingStock.Diesel.DieselExhaustParticleController` — diesel stack VFX

```csharp
public class DieselExhaustParticleController : MonoBehaviour {                // DieselExhaustParticleController.cs:9
    public VisualEffect       visualEffect;
    public SmokeEffectProfile profile;
    public float              accelInfluenceLow;
    public float              accelInfluenceHigh = 0.1f;

    private float _value;       // smoothed current (lerp toward NormalizedExhaustOutput)
    private float _accel;       // (_value - prev) / dt — drives "puff of black on notch up"

    private static bool ParticlesEnabled => Preferences.GraphicsParticleLevel > Preferences.ParticleLevel.Off;
    public  float NormalizedExhaustOutput { get; set; }     // ← set externally by DieselLocomotive

    private void Update() {
        float dt = Time.deltaTime;
        if (dt != 0f) {
            float prev = _value;
            _value = Mathf.Lerp(_value, NormalizedExhaustOutput, dt * 0.2f);
            _accel = (_value - prev) / dt;
            UpdateSmoke();
        }
    }
}
```

**Driven by `NormalizedExhaustOutput` (a public setter, no event)**, which is written by `DieselLocomotive.DidLoadModels`:

```csharp
// DieselLocomotive.cs:53
_primeMoverAudioPlayer.NormalizedExhaustOutputEvent = (n) => {
    foreach (var pc in _particleControllers) pc.NormalizedExhaustOutput = n;
};
```

`PrimeMoverAudioPlayer.SetExhaust(notch)` (called on every notch transition in `PrimeMoverCoroutine`, `PrimeMoverAudioPlayer.cs:185-189`) computes `Mathf.InverseLerp(0, 8, notch)` — i.e., notch 0 = 0.0, notch 8 = 1.0 — and `Invoke`s the event. **The VFX intensity is therefore a notch-discrete step function, smoothed via the controller's per-frame Lerp(rate=0.2/s).**

`_accel` boost: `SmokeStartColor()` adds `InverseLerp(accelInfluenceLow, accelInfluenceHigh, _accel)` to `_value` before sampling the `colorGradient` — so a notch-up event picks a darker (higher-`_value`) gradient sample for a few seconds. This is the "puff of black smoke when notching up" effect.

`accelInfluenceLow / accelInfluenceHigh` are public serialized fields on the prefab; mod prefabs control how aggressive the dark-puff response is.

**Multiple controllers per loco are supported** — `DieselLocomotive._particleControllers` is a `List<>`. A diesel with two stacks gets two `DieselExhaustParticleController` components, both driven from the same `NormalizedExhaustOutput`.

### Patch candidates

| Method | Why patch |
|---|---|
| `Update` | Per-frame intensity tick. Postfix to override `_value`/`_accel` from custom inputs (e.g., turbocharger spool). |
| `UpdateSmoke` | Direct VFX-Graph parameter writes. Patch to alter curve sampling. |
| `SmokeStartColor` | Color picker (gradient + accel boost). Patch for custom color models (e.g., overheated white smoke). |
| `UpdatePlayStop` | Idle/fuel gate. |
| `NormalizedExhaustOutput` setter | Hook every notch transition (since it's only written from the audio event). |
| `PrimeMoverAudioPlayer.NormalizedExhaustOutputEvent` (`Action<float>`) | The producer-side hook. Subscribe a mod handler alongside the vanilla VFX consumer to drive custom effects from the same notch event. |

### MP authority — none

`Notch` is a control-property KVO and is replicated. Both clients see the same notch and therefore both clients fire `NormalizedExhaustOutputEvent` independently with the same value. VFX is locally computed.

### Gotchas

- **`NormalizedExhaustOutput` is a `set` property with no observation event.** The controller polls `_value → NormalizedExhaustOutput` every frame in `Update`. Mods can write directly without waiting for a notch change.
- **`_value` is initialised to 0** but `NormalizedExhaustOutput` starts at `default(float) = 0`. So the first notch transition causes a smooth ramp from zero.
- **`UpdatePlayStop` only checks `IsIdle && HasFuel`**, not notch. A locomotive in idle (notch 0) but `!IsIdle` (loaded) and `HasFuel` will keep `visualEffect.Play()`-ing — you'll get a thin idle plume from `_value` lerping toward 0.0 (which evaluates the `[0,…]` curve endpoints, typically a small but non-zero rate).
- **No bridge between `DieselExhaustComponentBuilder` and the VFX prefab name.** The builder calls `InstantiatePrefab<UnityEngine.Component>("diesel-exhaust", …)` (`DieselExhaustComponentBuilder.cs:20`) — the asset must be named `diesel-exhaust` and contain a `DieselExhaustParticleController` component for `DieselLocomotive.DidLoadModels` to discover it via `GetComponentsInChildren<>`.

---

## `Effects.CylinderCockController` — cylinder-cock steam puffs

```csharp
public class CylinderCockController : MonoBehaviour, ISteamLocomotiveSubcomponent {  // CylinderCockController.cs:15
    [SerializeField] private VisualEffect[]   smokeEffects;          // size 2 (left/right)
    [SerializeField] private AnimationCurve   smokeOutputCurve;
    [SerializeField] private AudioClip        audioClip;             // looping cock-steam audio
    [SerializeField] private AnimationCurve   volumeCurve;
    [SerializeField] private float            audioDistanceMin = 8f, audioDistanceMax = 50f;

    private float _phase;            // ← driver phase from SteamLocomotive (0..1)
    private float _radius;           // cylinder offset from centerline
    private float _forwardOffset;    // ← Configure() — front-of-loco vs middle puff position
    private float _steam = 1f;       // accumulated steam pressure available to discharge
}
```

### Three-input control surface

```csharp
public void Configure(float radius, float forwardOffset)                    // 57 — once at builder time
{
    smokeEffects[0].transform.localPosition = Vector3.left  * radius;
    smokeEffects[1].transform.localPosition = Vector3.right * radius;
    _radius = radius;
    _forwardOffset = forwardOffset;
}

private void OnEnable()  {
    _controlObserver = parentKVO.Observe(KeyForControl(Control.CylinderCock),
                                         v => SetCylinderCockSteam(v.BoolValue));   // 85
    // Allocates 2 IAudioSource via VirtualAudioSourcePool.Checkout("CylinderCock", …, AudioController.Group.LocomotiveCylCock, 10, …, AudioDistance.Nearby, ±radius)
}

public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser, float absThrottle, float driverPhase) {  // 197
    _phase = driverPhase + 0.25f;
    _steam = Mathf.Clamp01(_steam + absThrottle * Time.fixedDeltaTime * 0.001f);
}
```

**Three drivers feed the puffs:**

1. **`Control.CylinderCock` KVO bool** (key `cylCock`, see `PropertyChange.cs:168`) — opens/closes the cocks. Player input via the cab control flips this. KVO observer in `OnEnable` calls `SetCylinderCockSteam(open)`.
2. **`absThrottle` from `ApplyDistanceMoved`** — accumulates `_steam` at rate `absThrottle * dt * 0.001` (full throttle = 0.001 steam/s, so it takes ~1000s to fully recharge from 0). When the cocks are *closed*, throttling lets steam pressure rebuild.
3. **`driverPhase`** (rotational phase of the main driver wheel, 0..1) — drives the per-cylinder exhaust pulse via `Mathf.PerlinNoise(0f, Time.time)` modulation in `SetSmokeEffects`. The two cylinders puff out of phase by 0.25 (90°), and `Mathf.Repeat(2f * (_phase + i*0.25), 1f)` doubles the frequency to two puffs per revolution per cylinder (matching real four-stroke physics).

`SetSmokeEffects` also moves each cylinder's `PositionOffset` between `Vector3.zero` and `Vector3.back * _forwardOffset` based on `_phase > 0.5f` — alternating front-of-stroke vs back-of-stroke positioning for the steam puff origin.

### Audio co-driving

This controller is also a *co-driver* for the cyl-cock audio. `_audioSources[i].volume = num2 * volumeCurve.Evaluate(time)` — so the same per-phase modulation that scales smoke `Rate` also scales audio volume. Cross-link to [`audio.md`](audio.md#per-component-locomotive-audio-classes-summary) `CylinderCockController` row.

### Patch candidates

| Method | Why patch |
|---|---|
| `Configure(radius, forwardOffset)` | Called by `CylinderCockComponentBuilder` (`CylinderCockComponentBuilder.cs:20`). Patch to log/intercept geometry. |
| `SetCylinderCockSteam(bool)` | KVO observer entry. Patch to override the cocks-open response. |
| `UpdateCoroutine` | Per-frame ramp loop while open. Patch to alter ramp/decay timing. |
| `SetSmokeEffects(float)` | The per-frame VFX parameter writes (and audio-volume writes). Patch for custom puff shape. |
| `ApplyDistanceMoved` | Per-position-update accumulation of `_steam` + `_phase` snapshot. |

### MP authority

Cylinder-cock state is a control property, replicated via standard `PropertyChange` KVO write through the standard control auth (`StaticPropertyAccessControlDelegate`, see [`access-control.md`](access-control.md)). Both clients see the same open/closed state and independently fire visuals.

### Gotchas

- **`_steam` accumulates very slowly** (`absThrottle * dt * 0.001`), so cocks left open dump pressure fast (`openness * dt * 0.1` decay in `SetSmokeEffects`) but recharge very slowly. The visual *intensity* will diminish over a long open-cocks duration.
- **`_lastOffTime` recharge formula** (`InverseLerp(0, 60, Time.time - _lastOffTime)`) means closing the cocks for 60 s fully recharges `_steam` regardless of throttle. This is independent of the `ApplyDistanceMoved` recharge.
- **`smokeEffects[]` is a fixed-size-2 array.** Mod prefabs that want a 3-cylinder loco (e.g., shay) need a custom controller — vanilla assumes left-right symmetry.
- **Audio sources are checked out from `VirtualAudioSourcePool` in `OnEnable` and returned in `OnDisable`**, not pooled across loco lifetime. Bardo/respawn cycles audio sources.
- **The `_open = true` and `_steam` are set inside `SetCylinderCockSteam(true)`, but `_coroutine` may already be running** with `_open = false`'s tail. The coroutine has a `if (!_open) break;` after `WaitForSeconds(1f)` which means there's up to a 1-second lag between the KVO flip and the coroutine restart — but `SetCylinderCockSteam(true)` itself starts a fresh coroutine if `_coroutine == null`. Race-free in practice but the state machine is non-obvious.

---

## `Effects.WaterCylinderController` — water-tank fill + spray

```csharp
public class WaterCylinderController : MonoBehaviour {                       // WaterCylinderController.cs:9
    public MeshRenderer  meshRenderer;
    public string        key = "key";        // ← KVO bool key on parent KVO
    public float         speed = 2f;
    public float         startDelay, stopDelay;
    public VisualEffect  sprayEffect;
}
```

A small two-purpose effect: **shader fill animation** (writes `_FillTop`/`_FillBottom`/`_Speed` on the renderer's material) **plus spray VFX** (sets the `Rate` parameter to `value * 500f`).

The `key` is whatever KVO bool the prefab is keyed to. Vanilla uses this for water-tank spout filling animations (e.g., the watering effect at a water tower / tender fill point).

**Six-step animation curve:**
```
KVO flip true  → wait startDelay → lerp _currentValue→1 at rate 4/s
KVO flip false → wait stopDelay  → lerp _currentValue→0 at rate 20/s   ← stop is faster
```

Asymmetric ramps. Filling is gradual, draining is fast. Material shader writes split: fill rising = top fixed at 1, bottom climbs; fill falling = top descends, bottom fixed at 1.

**`sprayEffect.SetFloat("Rate", value * 500f)`** — the spray VFX expects a `Rate` exposed parameter (matches `SmokeEffectWrapper`'s convention), with a max of 500. Lower-rate sprays for tank-fill; the 500 cap is implicit.

### Patch candidates

| Method | Why patch |
|---|---|
| `PropertyDidChange(Value)` | KVO observer entry. Patch to add side-effects on water-event flips. |
| `SetValueImmediate(float)` | Material/VFX writes. Patch for custom fill shader / spray params. |
| `UpdateCoroutine(delay, lerpSpeed)` | Ramp loop. |

### MP authority

The `key` KVO is whatever the prefab points to. Authorisation follows that key's `IPropertyAccessControlDelegate` — no special handling here.

### Gotchas

- **`_isFirstValue` makes the first KVO observation snap immediately** (no coroutine). This is the load-restore path — saved state arrives without animation.
- **`OnDestroy` destroys the unique material** (`Object.Destroy(_material)`). The script calls `meshRenderer.material` (not `sharedMaterial`) in `Awake`, intentionally creating a per-instance material for this car. Mod patches that swap materials must respect this lifetime.
- **`speed` (the public field) is the shader `_Speed` parameter for the *fill animation*, not a controller speed.** The two `Lerp` rates are hardcoded `4` (fill) and `20` (drain).

---

## `RollingStock.DerailedParticleController` — derailment dust plumes

```csharp
public class DerailedParticleController : MonoBehaviour, ICarMovementListener {   // DerailedParticleController.cs:10
    [SerializeField] private VisualEffect       visualEffect;
    [SerializeField] private SmokeEffectProfile profile;

    public float Derailment { get; set; }                  // ← set externally
    private float CarVelocity { get; set; }                // ← set in CarDidMove
    private const float MinDerailValue = 0.01f, MinVelocity = 0.01f;

    private static bool ParticlesEnabled => Preferences.GraphicsParticleLevel > Preferences.ParticleLevel.Off;

    public void CarDidMove(MovementInfo info) {
        CarVelocity = (info.DeltaTime == 0f) ? 0f : Mathf.Abs(info.Distance / info.DeltaTime);
        if (CarVelocity > 0.01f && _value > 0.01f) StartUpdateCoroutineIfNeeded();
    }
}
```

**The only vanilla `ICarMovementListener` implementation.** Receives per-position-update movement info from `Car.FireOnMovement` (`Car.cs:2061`) — see [`odometer-movement.md`](odometer-movement.md) for the listener fan-out spine.

**Driven by two inputs**:
1. `Derailment` (external setter, called by `DerailedEffectComponentBuilder`'s observer of `Control.Derailment`) — controls smoke `Color` via `colorGradient.Evaluate(_value)`, smoke `Lifetime`, smoke `TurbulenceIntensity`.
2. `CarVelocity` (per-tick from `CarDidMove`) — controls smoke `Rate`, `Velocity`, `Size0/1`.

**Coroutine starts when both `_value > 0.01` AND `CarVelocity > 0.01`.** A stationary derailed car emits no dust. A non-derailed moving car emits no dust. (Both conditions are required for the dust plume.)

The `_value` field smooths from `_derailmentTarget` at rate `0.2/s` (`_value = Lerp(_value, _derailmentTarget, dt * 0.2f)`). Derailment "fades in" over ~5 seconds.

### Auto-injection by `Car.SetupComponents`

```csharp
// Car.cs:1410
if (lifetime == ComponentLifetime.Model) {
    SetupComponent(new DerailedEffectComponent {
        Name = "Derailed Effect",
        Separation = truckSeparation
    }, ctx, lifetime);
}
```

**Every car** (regardless of definition) gets a synthetic `DerailedEffectComponent` injected at model-setup time. This dispatches to `DerailedEffectComponentBuilder.Build`:

```csharp
// DerailedEffectComponentBuilder.cs:21
DerailedParticleController pc0 = ctx.InstantiatePrefab<DerailedParticleController>("derailment-particles", …);
DerailedParticleController pc1 = ctx.InstantiatePrefab<DerailedParticleController>("derailment-particles", …);
pc0.transform.localPosition = (Separation/2) * Vector3.forward;     // over front truck
pc1.transform.localPosition = (Separation/2) * Vector3.back;        // over rear truck
ctx.ObserveProperty(PropertyChange.Control.Derailment, v => {
    float d = Mathf.Abs(v.FloatValue);
    pc0.Derailment = d; pc1.Derailment = d;
});
```

Two controllers per car, one per truck position, both fed the same `Control.Derailment` value. Cross-link [`cars-cargo.md`](cars-cargo.md) on the `DerailedEffect` runtime-injection note and [`wear-durability.md`](wear-durability.md) on the `_derailment` KVO key & `Control.Derailment` mapping (key `_derailment`, see `PropertyChange.cs:160`).

### Patch candidates

| Method | Why patch |
|---|---|
| `CarDidMove(MovementInfo)` | Per-tick velocity sample. Patch postfix to add custom listeners or alter velocity calc. |
| `UpdateSmoke()` | The VFX-Graph parameter writes. Patch for custom dust profile. |
| `Derailment` setter | The external derailment input. Patch to filter (e.g., disable below a threshold). |
| `DerailedEffectComponentBuilder.Build` | The auto-inject path — patch to inject mod-side derailment effects per car. |
| `Car.SetupComponents` (the synthetic `DerailedEffectComponent`) | Skip auto-inject by patching the `if (lifetime == ComponentLifetime.Model)` branch. |

### MP authority

Both `Control.Derailment` (the KVO float) and the `MovementInfo` driving `CarVelocity` are derived from already-replicated state. The KVO is `HostOnly` (per [`wear-durability.md`](wear-durability.md#mp-authority)) — clients receive `_derailment` updates from host. `MovementInfo` is computed locally on each machine from each machine's `IntegrationSet.PositionCars` / `RemoteIntegrationSet.MoveCarTo` results (see [`odometer-movement.md`](odometer-movement.md#movementinfo-sources)).

### Gotchas

- **The coroutine may legitimately exit and not restart.** `_value > 0.01 || CarVelocity > 0.01` is the loop condition; once both fall below, the coroutine ends. `CarDidMove` re-checks and restarts only if `CarVelocity > 0.01 && _value > 0.01` — i.e., a stationary derailed car needs to start moving for the coroutine to re-enter. This is correct but worth noting if you instrument the loop.
- **Derailment input is `Mathf.Abs(value.FloatValue)`** — the underlying `_derailment` KVO key is signed (positive vs negative direction of derailment), but the visual ignores sign.
- **No `Stop()` on `OnDisable` apart from coroutine stop.** The VFX `gameObject.SetActive(false)` is set inside `SetPlaying(false)`; the disable path stops the coroutine but doesn't force-clear active particles.
- **`DerailedEffectComponent.Separation` comes from `truckSeparation` of the car** — set in `Car.SetupComponents` at injection time (`Car.cs:1415`). Cars with non-standard truck layouts (3+ trucks, vestigial in vanilla — see [`trucks-wheelsets.md`](trucks-wheelsets.md)) only get plumes at ±separation/2 (covering the two outer trucks).

---

## `RollingStock.HotboxEffect` — axle-bearing fire

```csharp
public class HotboxEffect : MonoBehaviour {                                  // HotboxEffect.cs:12
    [SerializeField] private VisualEffect smokeEffect;
    [SerializeField] private Light        light;

    public void Configure(Car car, float axleSeparation, float diameter, int index);
}
```

Driven by `Control.Hotbox` int (0/1) on the car's `ControlProperties`. `OnEnable` subscribes a KVO observer (`PropertyChange.Control.Hotbox`, key `"hotbox"`) and calls `UpdateForHotbox(value)`.

**Per-axle filtering by carHash mod 2:**
```csharp
bool flag = Mathf.Abs(_carHash % 2) == _index;
hotbox = hotbox && flag;
```
The `_index` is the axle index passed to `Configure`. **Each car deterministically has *one* of its axles selected to host the hotbox** (`carHash % 2` selects axle 0 or 1). This avoids every axle smoking simultaneously.

**`light` and `smokeEffect.Play()` are independent of `ParticlesEnabled`** — this is the only loco-side VFX *not* gated by the particle-level preference. Hotbox is gameplay-relevant (visual cue for AI engineer + player), so it ignores quality settings. The hotbox `Loop` coroutine modulates `Rate`, `Velocity`, and `light.intensity` from `Car.Oiled` (lower = more) and `Car.VelocityMphAbs` (higher = more).

Cross-link [`wear-durability.md`](wear-durability.md#hotbox) for the source of the `Hotbox` KVO write.

### Patch candidates

| Method | Why patch |
|---|---|
| `UpdateForHotbox(bool)` | KVO observer entry. Patch to override which axles smoke. |
| `Loop()` | Per-second tick of intensity/light. Patch for custom hotbox visuals. |
| `Configure(...)` | Per-axle setup at car build time. |

### Gotchas

- **`carHash % 2` can return negative for negative hashes.** The `Mathf.Abs(_carHash % 2)` guards against this.
- **`light.enabled` toggling via LeanTween sequence + delegate** — there's a `LeanTween.sequence().append(...).append(delegate { light.enabled = false })`. If the LeanTween global manager isn't running (shouldn't happen in vanilla), the disable callback never fires.
- **Not gated by `ParticlesEnabled`.** Modders writing custom hotbox-style effects who *want* to honour particle quality must add the gate themselves.

---

## `Effects.Flare` — fusee / flare

```csharp
public class Flare : MonoBehaviour, CullingManager.ICullingEventHandler {    // Flare.cs:7
    public Light          lightSource;
    public VisualEffect   visualEffect;
    public FuseeRenderer  fuseeRenderer;

    public void CullingSphereStateChanged(bool isVisible, int distanceBand) {
        lightSource.enabled  = isVisible && distanceBand <= 0;     // 0..25m
        visualEffect.enabled = isVisible && distanceBand <= 0;     // 0..25m
        foreach (var r in _renderers) r.enabled = distanceBand <= 1;
        fuseeRenderer.enabled = isVisible && distanceBand <= 1;    // 0..1000m
    }
}
```

Bands: `[25, 1000]` (configured by `CullingManagerInitializer` for `CullingManager.Flare`). **VFX + light only inside 25m**; mesh + fusee renderer to 1000m. This is the canonical pattern for distance-banded VFX — cross-link [`rendering-pipeline.md`](rendering-pipeline.md#culling-spine).

Mod recipe for distance-banded VFX: implement `CullingManager.ICullingEventHandler`, `AddSphere(transform, radius, this)` to the appropriate domain (`CullingManager.Scenery` for Hose-class, `CullingManager.Flare` for Flare-class), and toggle `visualEffect.enabled` (or `.Play()`/`.Stop()`) in `CullingSphereStateChanged`.

---

## `Effects.ClockDrivenVisualEffect` — time-of-day VFX

```csharp
[RequireComponent(typeof(VisualEffect))]
public class ClockDrivenVisualEffect : MonoBehaviour {                       // ClockDrivenVisualEffect.cs:8
    [Range(0,24)] public float hourOn, hourOff;
}
```

Subscribes via `ClockDriver.Instance.Schedule(hourOn, hourOff, SetOn)` in `OnEnable`. `SetOn(true)` calls `_visualEffect.Play()` and `SetBool("Run", true)` on the graph; `SetOn(false)` schedules a `LeanTween.delayedCall(30f, …)` before stopping (giving particles 30 s to age out). Cross-link to [`time-weather.md`](time-weather.md) on `ClockDriver.Schedule`.

The `OnValidate` re-`Schedule()`s only at runtime — editor-time hour edits don't force a schedule update unless playing.

---

## `RollingStock.Controls.KeyValueBoolParticlePlayer` — generic legacy bool→play

```csharp
[RequireComponent(typeof(ParticleSystem))]
public class KeyValueBoolParticlePlayer : MonoBehaviour {                    // KeyValueBoolParticlePlayer.cs:8
    public string key;
    public bool   invert;
}
```

The only "data-driven legacy `ParticleSystem`" hook. KVO observer pattern: bool key → `Play()` / `Stop()`. This is what the vanilla `ParticleSettingsApplicator` actually applies to (since it's `[RequireComponent(ParticleSystem)]`).

Useful for mod-side legacy particle effects that need a KVO-driven on/off without authoring a controller.

---

## `ParticleCollision` (no namespace) — sub-emitter trigger

Legacy `ParticleSystem` collision handler with sub-emitter trigger. `OnParticleCollision` finds particles within `particleDistanceCheck` (0.5m) of the collision intersection and calls `particleSystem.TriggerSubEmitter(subEmmiterId, ref particles[i])` to spawn the sub-effect.

Used by exactly one shipped effect (a coal-fall / dust effect). The class has a `debug` flag and uses a per-instance `particles` array allocated to `maxParticles` at `Start`.

Mods can repurpose for sub-emitter splash/spark effects without writing custom code.

---

## Floating origin: world-space vs local-space simulation

This is the load-bearing pitfall. Cross-link [`floating-origin.md`](floating-origin.md#particle-system-shifting).

```csharp
private static void TranslateParticleSystems(Vector3 offset) {               // WorldTransformer.cs:176
    ParticleSystem.Particle[] array = null;
    ParticleSystem[] array2 = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
    foreach (var ps in array2) {
        if (ps.main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
        int maxParticles = ps.main.maxParticles;
        if (maxParticles > 0) {
            // pause, GetParticles, offset every position, SetParticles, resume
            if (array == null || array.Length < maxParticles) array = new ParticleSystem.Particle[maxParticles];
            int n = ps.GetParticles(array);
            for (int j = 0; j < n; j++) array[j].position += offset;
            ps.SetParticles(array, n);
        }
    }
}
```

**Per-shift cost (every 1500m+ of camera drift):**
- `FindObjectsOfType<ParticleSystem>()` — full scene walk, allocates an array.
- For every `World`-sim system: pause → `GetParticles` (fills caller's `Particle[]`) → loop → `SetParticles` → play.
- The `array` buffer is reused across systems via `array.Length < maxParticles` check, but **resized on growth**, never shrunk.

**Critical:** This walk only handles `UnityEngine.ParticleSystem`. **`UnityEngine.VFX.VisualEffect` is NOT translated.** VFX-Graph effects must either:
1. Be parented to a transform that's in `WorldTransformerTargetList.Targets` (e.g., a car body, which gets shifted via `TrainController.WorldDidMove → Car.WorldDidMove → CarMover.WorldDidMove`), or
2. Use **local simulation space within the VFX graph itself** (the VFX-Graph equivalent), so particles ride the parent transform.

In vanilla, all loco-side VFX (chuff/diesel exhaust/cyl-cock/derail/hotbox/dynamo/whistle) are children of the car body — they shift with the car. The few `World`-space `ParticleSystem`s in the scene get the per-shift translate.

**Mod recipe — World-space VFX-Graph effects:**
- VFX-Graph systems with world-space simulation that aren't parented to a shifting transform will *teleport* (relative to the world) on each origin shift, leaving "ghost trails" of particles in their previous world position.
- Either parent to a `WorldTransformerTarget` GameObject, or subscribe to `WorldDidMoveEvent` and translate the VFX's `position` (and any cached emit-position fields).

### Per-frame allocation cost

The `Particle[maxParticles]` allocation in `TranslateParticleSystems` is reused across systems within a single shift, but a new allocation is made if any subsequent system has `maxParticles > previousMax`. **For a scene with N world-space systems, the worst-case cost is N grows of the buffer** — typically only a few hundred bytes total, but per-shift.

`FindObjectsOfType<ParticleSystem>()` is O(scene-objects) and allocates the result array. **A scene with thousands of legacy `ParticleSystem`s will pay measurable per-shift cost** — though Railroader is dominantly VFX-Graph, so this is small in practice.

---

## LOD / culling integration

Particle effects in vanilla are culled via three patterns:

1. **Implicit culling via parent visibility** — `Car.SetVisible(visible)` toggles renderer enable on body+endgear+truck recursively (see [`rendering-pipeline.md`](rendering-pipeline.md#carculler-the-per-consist-culler)). VFX-Graph effects parented to the body inherit but **`VisualEffect` is `Renderer`-derived only via the underlying mesh — `SetActive` on the parent does stop the effect**, but plain `Renderer.enabled = false` does not. Vanilla's `CarCuller` swap path is `Car.SetCullerDistanceBand` → model load/unload (`ModelLoadRetain`/`ModelLoadRelease`) which destroys the body GameObject entirely — taking the VFX with it.
2. **Explicit `CullingManager.ICullingEventHandler`** — `Chuff` (audio-side) registers with `CullingManager.Scenery` and toggles `chuffFilter.gameObject.SetActive` based on distance band; this implicitly culls the audio. `Flare` registers with `CullingManager.Flare` and toggles `visualEffect.enabled` directly. **No vanilla loco-side VFX controller registers its own culling token** — they ride parent visibility.
3. **`ParticleSettingsApplicator`** — global on/off toggle (legacy-system only, `Off` only).

There is **no per-particle LOD curve**. There is no "switch to lower-poly particle mesh at distance" mechanism. The implicit "culled when car is culled" is the only LOD.

Mod recipe for distance-banded VFX: see the `Effects.Flare` pattern above.

---

## Per-tick driving — data flow summary

| Controller | Driver | Tick cadence | Source of data |
|---|---|---|---|
| `SteamChuffParticleController` | `tractiveEffort`, `absVelocity`, `driverPhase` | per-position `ApplyDistanceMoved` + per-frame `Update` (continuous mode) | `SteamLocomotive.SubcomponentsApplyDistanceMoved` (`SteamLocomotive.cs:422`); `_wheelVelocity` is locally computed |
| `SteamChuffParticleController` (puff scheduling) | audio chuff timing | per-puff (event) | `Chuff.cs:78` → `Delegate.ScheduleNextChuff` (delegate is the particle controller) |
| `DieselExhaustParticleController` | `NormalizedExhaustOutput` | per-frame `Update` | `PrimeMoverAudioPlayer.NormalizedExhaustOutputEvent` `Action<float>` (`PrimeMoverAudioPlayer.cs:188`); event fires on every notch transition |
| `CylinderCockController` | `Control.CylinderCock` KVO + `_phase` from `driverPhase` + `_steam` from `absThrottle` | per-position `ApplyDistanceMoved` + per-frame `UpdateCoroutine` | KVO observer + `SteamLocomotive.SubcomponentsApplyDistanceMoved` |
| `WaterCylinderController` | KVO bool (configurable key) | event + coroutine | KVO observer |
| `DerailedParticleController` | `Derailment` setter + `MovementInfo.Distance/DeltaTime` | per-tick `CarDidMove` + per-frame `UpdateCoroutine` | `Car.FireOnMovement` → `_movementListeners` (`Car.cs:2061`); `Derailment` written by `DerailedEffectComponentBuilder`'s `Control.Derailment` observer |
| `HotboxEffect` | `Control.Hotbox` KVO + `Car.Oiled` + `Car.VelocityMphAbs` | event + 1Hz `Loop` coroutine | KVO observer + per-second poll of car state |
| `DynamoPlayer` | `OnIdleDidChange` / `OnHasFuelDidChange` events | event-driven (no per-frame VFX update) | `BaseLocomotive` C# events |
| `ClockDrivenVisualEffect` | `[hourOn, hourOff]` window | event-driven via `ClockDriver` | `TimeWeather.Now` advance |
| `Flare` | `CullingManager.Flare` distance band | event-driven (band change) | Unity `CullingGroup` |

---

## MP — particles are local

**No particle event is replicated.** Every controller listens to:
- KVO updates (already replicated through the standard property-change pipeline), or
- C# events on locally-resident MonoBehaviours (e.g., `BaseLocomotive.OnIdleDidChange`, which both clients fire from their own observation of locally-derived state).

There is **no `PlayParticleAtPosition` message**, no `ScheduleParticleEvent`, no host-broadcast particle pulse. Compare to [`audio.md`](audio.md#scheduledaudioplayer-the-network-replicated-3d-one-shot) where `ScheduledAudioPlayer.HostPlaySoundAtPosition` *does* replicate audio one-shots — there's no particle equivalent.

**Implication for mods:** Visual desync between players is normal and acceptable. Two clients may chuff out of phase, dust slightly differently, etc. If your mod needs synchronized VFX (e.g., for cinematic moments), you must roll your own broadcast — typically by sending a custom IGameMessage with a position+effect-id, and have each client play locally.

The `Effects.Decals` system *does* track car-level state (decals are renderered to RenderTextures with refcounted caching), and decals are gated by the car visibility chain — see [`rendering-pipeline.md`](rendering-pipeline.md#decalprojectorhelper-the-per-decal-gate). But decal *content* (lettering text, color scheme) flows over MP via KVO; only the rendering happens locally.

---

## Patch points for mod authors

### Custom VFX-Graph particle types

1. **Author your VFX-Graph asset with the SmokeEffectWrapper convention** — exposed parameters named exactly `Rate` (float), `Velocity` (float), `Lifetime` (float), `Color` (Vector4), `Size0` (float), `Size1` (float), `TurbulenceIntensity` (float), `PositionOffset` (Vector3). Then any vanilla controller that uses `SmokeEffectWrapper` will drive your asset out of the box.
2. **For non-smoke effects**, write a custom controller. Mirror the patterns: `OnEnable` subscribes to KVO + C# events; `OnDisable` unsubscribes; private static `ParticlesEnabled` getter that gates `Play()` calls; coroutine for ramp/state machine; per-frame `Update` for continuous parameters.

### Replacing vanilla particles

- **Per-prefab swap**: ship an asset pack with a custom car definition that provides your VFX prefab under the conventional name (`smokestack`, `cyl-cock`, `diesel-exhaust`, `derailment-particles`, `firebox-fire-quad`). The component builders will instantiate yours.
- **Per-controller swap**: Harmony-patch the `OnEnable` of the vanilla controller (e.g., `SteamChuffParticleController.OnEnable` postfix) to swap the `visualEffect` field reference to your asset.
- **Per-curve swap**: assign a custom `ChuffProfile` / `SmokeEffectProfile` ScriptableObject to the controller's `profile` field. **Public field** — assignable at runtime via reflection or via direct prefab edit.
- **Replace the `SmokeEffectWrapper` shim** — not feasible; it's a `readonly struct` with cached `int` IDs in `static readonly` fields. Mods needing different parameter names must write their own controllers.

### Mod-side world-space effects

Three patterns:

1. **Parent to a `WorldTransformerTarget`** — drop `Helpers.WorldTransformerTarget` MonoBehaviour on the VFX root. It auto-registers the transform for shifting.
2. **For legacy `ParticleSystem` with `World` simulation space**: it Just Works™ — `WorldTransformer.TranslateParticleSystems` handles it (subject to the per-shift cost noted above).
3. **For `VisualEffect` (VFX-Graph) with world simulation**: subscribe to `WorldDidMoveEvent` via `Messenger.Default.Register<WorldDidMoveEvent>(this, OnShift)` and offset your VFX's transform.position (or any internal world-space buffer the graph uses). VFX-Graph itself does not have a built-in re-origin shift.

### Custom particle quality presets

- Inject a `Medium` bucket between `Off` and `Standard`: patch `Preferences.GraphicsParticleLevel` getter to map your custom int (e.g., 3 = Medium) and update the dropdown in `PreferencesBuilder` (`PreferencesBuilder.cs:145`).
- The five `static bool ParticlesEnabled` properties on the vanilla controllers all read `> ParticleLevel.Off`. Patch each (or add a wrapper static class) to honour your new bucket.
- The dead `Low` value can be re-purposed in mod code as a soft "reduced" level — vanilla treats it as Standard, so you'd need to gate vanilla effects' `Rate`/`maxParticles` separately to make Low actually reduce throughput.

### Custom particle pooling

There is no vanilla pool. **A pooling layer is a clean mod add-on**: wrap `VisualEffect` instantiation in a static `VFXPool.Checkout(prefab) → VisualEffect` / `Return(VisualEffect)` and have your controllers pull from it. Vanilla's per-effect prefab-instantiate-on-load model is fine for the small number of effects per car (≤6 per loco).

---

## Cross-references

- Floating origin and the per-shift particle translate: [`floating-origin.md`](floating-origin.md#particle-system-shifting).
- Audio co-driving (chuff, cyl-cock): [`audio.md`](audio.md#per-component-locomotive-audio-classes-summary).
- Steam-subcomponent dispatch (`ApplyDistanceMoved` source): [`locomotive-architecture.md`](locomotive-architecture.md#steam-subcomponent-pipeline-isteamlocomotivesubcomponent).
- `MovementInfo` source for `DerailedParticleController`: [`odometer-movement.md`](odometer-movement.md#movementinfo-sources).
- `Control.Derailment` KVO key, `Car.ApplyDerailmentDelta`: [`wear-durability.md`](wear-durability.md#derailment).
- `Car.SetupComponents` synthetic `DerailedEffectComponent` injection: [`cars-cargo.md`](cars-cargo.md) (`DerailedEffect runtime injection`).
- Distance-banded VFX (`Flare`, `Chuff`): [`rendering-pipeline.md`](rendering-pipeline.md#culling-spine).
- `Preferences.GraphicsParticleLevel` storage: [`settings-preferences.md`](settings-preferences.md#preferences-the-playerprefs-catalog).
- `Preferences.ParticleLevel.Low` deadness in save/load context: [`save-load.md`](save-load.md).
