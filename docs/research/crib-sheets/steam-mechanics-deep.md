# Steam Mechanics & Cab-Control Wiring — Crib Sheet (Deep)

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companions:** [Locomotive Architecture](locomotive-architecture.md), [Traction](traction.md), [Brakes](brakes.md), [Animation Playables](animation-playables.md), [Audio](audio.md), [Interaction Controls](interaction-controls.md), [Trucks & Wheelsets](trucks-wheelsets.md), [VFX & Particles](vfx-particles.md)

This sheet is the steam-deep companion to the locomotive-architecture sheet. Two coupled topics live here:

1. **Steam subcomponent driver-phase math** — how `DriverPhase` is computed by the wheel animator, how it propagates as a 0..1 fraction to chuff audio + cylinder cocks + smokestack particles, the implicit two-cylinder/four-strokes-per-revolution firing pattern that lives in `ChuffFilter.OnAudioFilterRead`'s `Evaluate(0)+Evaluate(0.25)+Evaluate(0.5)+Evaluate(0.75)` line, and the subcomponent traversal-order fragility introduced by writing `DriverPhase` *during* the same dispatch loop that consumes it.
2. **`ControlPurpose` enum + cab-control wiring** — the discovery+wire-up substrate that turns a `RadialAnimatedControl` tagged with one of nine enum values into a `KeyValueObject` write through `BaseLocomotive.ConnectBodyControls` + `LocomotiveControlHelper`. The single-source-of-truth for what cab handles vanilla supports.
3. Plus a **`SteamEngine` + `PrimeMover` deep-dive** — the things `traction.md` references but doesn't centrally document: caching gotchas (`MaximumTractiveEffort`, `_estimatedGrateSqFt`), the missing controls (cutoff regulator, blower, fire management), the static `maximumBoilerPressure`, the Diesel notch→RPM/power tables, the `NormalizedExhaustOutputEvent` `Action<float>` delegate which is the diesel analog to the steam subcomponent dispatch.

Cross-cutting one-liner: **The dispatch interface for steam is per-distance-update; for diesel it is a per-Update audio-driven `Action<float>`. There is no symmetric `IDieselLocomotiveSubcomponent`.**

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `SteamLocomotive.SubcomponentsApplyDistanceMoved(MovementInfo)` | `Model/SteamLocomotive.cs:422` | The dispatch loop. Snapshots `absReverser`/`absThrottle`/`driverPhase` once, foreach-fans-out. |
| `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` | `RollingStock.Steam/SteamLocomotiveWheelAnimator.cs:252` | Computes per-wheel `Parameter` (rotation 0..1), writes `DriverPhase` from the main driver. |
| `Chuff.FixedUpdate` (also `ApplyDistanceMoved`) | `RollingStock/Chuff.cs:64,93` | Drives `ChuffFilter.engineSpeed` from `_absVelocity / _driverCircumference`; schedules low-speed particle puffs via `Delegate`. |
| `ChuffFilter.OnAudioFilterRead` | `RollingStock.Steam/ChuffFilter.cs:109` | The 4-phase chuff sum (0/0.25/0.5/0.75) — the **two-cylinder firing pattern**, baked into audio sample generation. |
| `ChuffFilter.GetNextChuffDelay()` | `RollingStock.Steam/ChuffFilter.cs:187` | Returns time-until-next-quarter-phase boundary, scaled by engine speed. The puff-scheduling oracle. |
| `CylinderCockController.ApplyDistanceMoved` | `Effects/CylinderCockController.cs:197` | Snapshots `driverPhase + 0.25f` and bumps `_steam` by `absThrottle * fixedDeltaTime * 0.001f`. |
| `SteamChuffParticleController.ScheduleNextChuff(float, float)` | `RollingStock.Steam/SteamChuffParticleController.cs:120` | The `IDynamicChuffDelegate` callback — fires a single puff coroutine with a `Wait` then exponential rate decay. |
| `SteamLocomotive.DidLoadModels` | `Model/SteamLocomotive.cs:98-135` | Discovers wheel animator (added directly), chuff (`IChuffProvider` find), particles, then `_subcomponents.AddRange(GetComponentsInChildren<...>)`. **Insertion-order origin.** |
| `SteamEngine` | `Model.Physics/SteamEngine.cs` | Boiler+cylinders model. `MaximumTractiveEffort` cached. `pressure` static. No fire/blower/safety. |
| `PrimeMover` | `Model.Physics/PrimeMover.cs` | Diesel notch→RPM/power tables (9 notches incl idle). `FuelConsumptionRate` quadratic in `notch * starting / 64750`. |
| `PrimeMoverAudioPlayer.NormalizedExhaustOutputEvent` | `Audio/PrimeMoverAudioPlayer.cs:23` | `Action<float>` fired from `SetExhaust(notch)` on every transition. **Diesel's analog to `ApplyDistanceMoved`.** |
| `ControlPurpose` (enum, 9 values) | `Definition/Model.Definition.Components/ControlPurpose.cs` | The slot-tag enum on `RadialAnimatedControl`s. Closed. |
| `BaseLocomotive.TryGetControl(ControlPurpose, out ContinuousControl)` | `Model/BaseLocomotive.cs:340` | Per-call linear scan of body's `RadialAnimatedControl[]`. |
| `BaseLocomotive.ConnectBodyControls` | `Model/BaseLocomotive.cs:233` | The wire-up procedure — discovers + binds + falls back to `DummyControl()` for 4 essentials. |
| `LocomotiveCabControlsHookup` | `RollingStock/LocomotiveCabControlsHookup.cs` | The 15-slot `MonoBehaviour` (5 `IGauge` + 8 `ContinuousControl` + IGauge boilerPressure + IGauge mainReservoir overlap). |
| `LocomotiveControlHelper` | `Model/LocomotiveControlHelper.cs` | The setter-style facade over `Car.SendPropertyChange` — `Throttle`/`Reverser`/`TrainBrake`/`Bell`/`Horn`/`CylinderCocksOpen` etc. |
| `ControlExtensions.ConfigurePropertyChange` | `RollingStock.ContinuousControls/ControlExtensions.cs:9` | The `OnValueChanged → StateManager.ApplyLocal(new PropertyChange(...))` glue used by every brake/horn/bell binding. |
| `DummyControl` | `RollingStock.ContinuousControls/DummyControl.cs` | Empty subclass of `ContinuousControl`. Substituted into `cabControls.{x}` slots when `TryGetControl` returns false. |

---

## Driver-phase math: how `DriverPhase` propagates

### The subcomponent dispatch (per-position-update)

```csharp
// Model/SteamLocomotive.cs:422
private void SubcomponentsApplyDistanceMoved(MovementInfo info)
{
    float absReverser = Mathf.Abs(locomotiveControl.AbstractReverser);
    float absThrottle = Mathf.Abs(locomotiveControl.AbstractThrottle);
    float driverPhase = ((_wheelAnimator == null) ? 0f : _wheelAnimator.DriverPhase);
    foreach (ISteamLocomotiveSubcomponent subcomponent in _subcomponents)
        subcomponent.ApplyDistanceMoved(info, _wheelVelocity, absReverser, absThrottle, driverPhase);
}
```

`driverPhase` is captured **once** at the top of the loop. For the very first dispatch in a session it is `0f`. Every subsequent dispatch reads `_wheelAnimator.DriverPhase` — which is updated *inside* `ApplyDistanceMoved` for the wheel animator subcomponent. **If the wheel animator is iterated first** (vanilla case), then later subcomponents see the just-written value but it isn't passed to them — only the value snapshotted at top-of-loop. So in practice every subcomponent sees the **previous tick's** `DriverPhase`. See gotchas below.

### `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` — the phase computer

```csharp
// RollingStock.Steam/SteamLocomotiveWheelAnimator.cs:252
public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser,
                               float absThrottle, float driverPhase)
{
    if (wheels.Length == 0) return;
    float velocity = Locomotive.velocity;
    for (int i = 0; i < wheels.Length; i++) {
        WheelAnimation wheel = wheels[i];
        float circumference = wheel.diameter * MathF.PI;
        if (circumference != 0f) {
            float speed = wheel.isDriver ? driverVelocity : velocity;   // ← slip distinction
            float deltaParam = info.DeltaTime * speed / circumference;
            wheels[i].Parameter = Mathf.Repeat(wheel.Parameter + deltaParam, 1f);
            if (wheels[i].isDriver)
                DriverPhase = wheels[i].Parameter;                       // ← writes the property
        }
    }
    _wheelAudio.Roll(info.Distance * Mathf.Sign(velocity), velocity);
}
```

The math:

- `Parameter` is a normalized animation timeline parameter (0..1, repeating). One wrap = one full wheel revolution.
- `deltaParam = (DeltaTime * speed) / circumference` — i.e. **revolutions per tick** = `(seconds × m/s) / (m/rev)` = revolutions.
- **`speed` selection is the slip distinction**: drivers use `driverVelocity` (which can be larger than body `velocity` during slip — see `BaseLocomotive.UpdateTractiveEffortWheelState` / [traction.md › slip](traction.md)), non-drivers use body `velocity`. Visually, slipping drivers spin faster than support wheels.
- Only the *single* wheelset whose `isDriver` flag is true (the `MainDriverIndex` in the steam loco definition) writes `DriverPhase`. Multi-driver-set locos still produce one `DriverPhase` value.
- `info.DeltaTime` is the integrator's `dt` for *this position update* — not necessarily `Time.fixedDeltaTime` (it's per-distance, so `dt` ≈ `Time.fixedDeltaTime` if running at fixed rate, else accumulator-driven).

### `DriverPhase` consumers and their interpretation

| Consumer | Reads | What it does with the 0..1 phase |
|---|---|---|
| `Chuff` (audio) | **No, doesn't read phase** | Computes its own `engineSpeed = _absVelocity / _driverCircumference` (revolutions/sec). The `ChuffFilter.OnAudioFilterRead` then derives 4-stroke chuff via 4 `Evaluate()` calls at fixed `(0.0, 0.25, 0.5, 0.75)` phase-offsets driven by `_sampleTime` (its own clock, not `DriverPhase`). |
| `CylinderCockController` | Yes | Stores `_phase = driverPhase + 0.25f` in `ApplyDistanceMoved`. The `+ 0.25` is the phase offset for cylinder events (90° mechanical lead — i.e. the cocks fire at the same crank position as one of the four chuff-strokes, offset by a quarter-cycle from the main-driver reference). Used in `SetSmokeEffects` to space the two side-smoke effects: `num3 = Mathf.Repeat(_phase + i * 0.25f, 1f)` — each side gets a different phase and decides whether to emit forward-offset puff. |
| `SteamChuffParticleController` | **No, doesn't read phase** | Uses `absVelocity` and `absThrottle` only. The actual *puff timing* comes from `Chuff` calling `Delegate.ScheduleNextChuff(delay, 0.2f)` — and the delegate is set to `_chuffParticles` in `SteamLocomotive.DidLoadModels:117`. So phase reaches the particles indirectly via `Chuff.GetNextChuffDelay()`. |

### The 0/90/180/270 firing pattern (`ChuffFilter.OnAudioFilterRead`)

```csharp
// RollingStock.Steam/ChuffFilter.cs:147
float num6 = Evaluate(0f) + Evaluate(0.25f) + Evaluate(0.5f) + Evaluate(0.75f);
// ...
float Evaluate(float phaseOffset) =>
    curve.Evaluate(Mathf.Repeat(_sampleTime + phaseOffset, 1f) * chuffSpeedMult);
```

This is the **two-cylinder-double-acting firing pattern baked into the audio synthesis**:

- Two cylinders, each double-acting → **four power strokes per driver revolution**, at crank angles 0°, 90°, 180°, 270°.
- The chuff-amplitude curve (`curve`) repeats once per stroke; `_sampleTime` is one revolution's worth of phase (0..1).
- Sum of four `Evaluate` calls at quarter-phase offsets = sum of four staggered chuff envelopes per revolution.
- `chuffSpeedMult` stretches/compresses each envelope based on cutoff (`Mathf.Lerp(profile.fullCutoffMultiplier, 1f, num2)` where `num2 = engineCutoff = absReverser`). Long cutoff → wider chuff envelope (more steam admitted per stroke).

**This is the only place the firing pattern is encoded.** The number `4` and the offsets `0, 0.25, 0.5, 0.75` are literal constants in `OnAudioFilterRead`. **Three- or four-cylinder locos (Shays, geared engines) would require patching this method**; there is no parameterization. Mallet/Garratt with two engine units would chuff at twice the rate but vanilla doesn't model the second engine at all.

### `SteamLocomotive.numberOfCylinders = 2` hardcode

```csharp
// Model/SteamLocomotive.cs:209
engine.numberOfCylinders = 2;
```

`SteamEngine.numberOfCylinders` is a public `int` field initialized to 2 (`SteamEngine.cs:8`). It is **only read** by `TrainMath.SteamEngineCharacteristics` constructor at TE-formula-build time (cross-link [traction.md › `2 * 0.85 * d² * stroke * pressure / driver`](traction.md#gotchas-steam)). Setting it to 4 would only multiply the starting-TE formula by 2 — it would **NOT** change the audio firing rate (still 4 strokes/rev in `ChuffFilter`), would NOT change the cylinder cock smoke pattern (still two side puffs in `CylinderCockController`), and would NOT add a second engine unit. **No Mallet, Garratt, or Shay support** — all simplex.

### `Chuff` — the audio chuff producer (per-distance and per-FixedUpdate)

```csharp
// RollingStock/Chuff.cs:64
private void FixedUpdate() {
    if (!_movedLastFixedUpdate) _absVelocity = 0f;     // ← rest detection
    _movedLastFixedUpdate = false;
    _tractiveEffort = Mathf.Lerp(_tractiveEffort, _tractiveEffortReported, Time.deltaTime);
    float engineSpeed = _absVelocity / _driverCircumference;     // rev/sec
    chuffFilter.engineSpeed = engineSpeed;
    chuffFilter.engineNormalizedTE = _tractiveEffort;
    chuffFilter.engineThrottle = Mathf.Lerp(chuffFilter.engineThrottle, _absThrottle,
                                            Time.deltaTime * throttleResponsiveness);
    if (_absVelocity < 5f) {
        float nextChuffDelay = chuffFilter.GetNextChuffDelay();
        if (nextChuffDelay < 0.1f)
            Delegate.ScheduleNextChuff(nextChuffDelay, 0.2f);
    }
}

// RollingStock/Chuff.cs:93
public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser,
                               float absThrottle, float driverPhase) {
    _movedLastFixedUpdate = true;
    _absVelocity = Mathf.Abs(driverVelocity);          // ← uses DRIVER vel, with slip
    chuffFilter.engineCutoff = absReverser;
    _absThrottle = absThrottle;
    _tractiveEffortReported = Mathf.Clamp01(info.TractiveEffort);
}
```

**Hybrid rest-detection pattern**: `ApplyDistanceMoved` only fires when the loco's position updates. If the loco is stopped, position doesn't update, `ApplyDistanceMoved` doesn't fire, but `FixedUpdate` does — and on the second consecutive `FixedUpdate` without a position update, `_movedLastFixedUpdate` stays false and `_absVelocity` is forced to 0 (chuff falls silent). This is the canonical pattern for "I need to keep updating even at rest, but my parameter source is per-distance."

**Low-speed delegate path**: `< 5 m/s` (≈ 11 mph), `Chuff` consults `chuffFilter.GetNextChuffDelay()` and schedules visible puffs through the `IDynamicChuffDelegate`. At higher speeds the audio chuff samples are dense enough to read continuously and the visible particle smoke goes "continuous" (`SteamChuffParticleController.continuous = absVelocity > 5f`). The 5 m/s threshold matches the `SteamChuffParticleController.continuous` boundary — **the whole effect chain switches modes at 5 m/s**.

### `CylinderCockController` — drain cycle synced to driver phase

`CylinderCockController.ApplyDistanceMoved` (line 197):

```csharp
public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser,
                               float absThrottle, float driverPhase) {
    _phase = driverPhase + 0.25f;
    _steam = Mathf.Clamp01(_steam + absThrottle * Time.fixedDeltaTime * 0.001f);
}
```

- `_phase` mirrors `DriverPhase` plus a **fixed 0.25 (90°) lead**, no parameterization.
- `_steam` is a 0..1 *condensate accumulator*. Throttle pushes it up at `0.001 * dt` per tick (roughly one normalized unit per 16 minutes at full throttle); `SetSmokeEffects` drains it (`_steam -= openness * dt * 0.1f`).
- The actual smoke/audio update runs in `UpdateCoroutine` (`_open` toggle + per-frame `SetSmokeEffects(openness)`), which is independent of the subcomponent dispatch — meaning drained smoke continues even at rest while the cocks are open.

Smoke visualization (`SetSmokeEffects`):

```csharp
float num3 = Mathf.Repeat(_phase + (float)i * 0.25f, 1f);
float time = Mathf.Repeat(2f * (_phase + (float)i * 0.25f), 1f);
float num4 = num2 * smokeOutputCurve.Evaluate(time);
// ...
float num5 = ((num3 > 0.5f) ? _forwardOffset : 0f);
smokeEffectWrapper.PositionOffset = Vector3.back * num5;
```

- Two side smoke effects (`smokeEffects[0/1]` = left/right), separated by an additional 0.25 phase offset.
- Each side fires a forward-offset version (front cylinder) when `num3 > 0.5` and a non-offset version (back cylinder) when `num3 ≤ 0.5`. This gives the visual "front cock-back cock" alternation per side.
- `time = Mathf.Repeat(2 * (_phase + i*0.25), 1)` — the **`2 *` doubles the rate** so two pulses per revolution per side; combined with two sides, that's 4 pulses/revolution — matching the 4 stroke/rev firing pattern.

### `SteamChuffParticleController` — the smokestack VFX

State per `ApplyDistanceMoved`:

```csharp
public void ApplyDistanceMoved(MovementInfo info, float driverVelocity, float absReverser,
                               float absThrottle, float driverPhase) {
    absVelocity = Mathf.Abs(driverVelocity);
    isStopped = absVelocity < 0.01f;          // ← <0.01 m/s
    continuous = absVelocity > 5f;            // ← >5 m/s, mode switch
    _targetTractiveEffort = ((isStopped || !_locomotive.HasFuel) ? 0f : absThrottle);
}
```

Modes:

- **Stopped (`absVelocity < 0.01f`)**: target TE is 0, `Update` runs `UpdateSmoke` with decaying `tractiveEffort` to fade smoke. Note the lerp is **asymmetric**: `(tractiveEffort < _targetTractiveEffort) ? 2 : 4` — TE *increases* slowly (lerp factor 2) and *decreases* faster (factor 4). So smoke spikes slowly to peak then fades crisply.
- **Continuous (`absVelocity > 5f`)**: `Update` runs `UpdateSmoke` continuously, no per-puff scheduling. Visual chuff is implicit in particle stream.
- **Low-speed band (`0.01..5 m/s`)**: neither stopped nor continuous → `Update` skips `UpdateSmoke` entirely. Smoke comes solely from `ScheduleNextChuff(delay, 0.2f)` callbacks fired by `Chuff.FixedUpdate` at quarter-phase boundaries via `IDynamicChuffDelegate`.

`ScheduleNextChuff` runs a `Puff` coroutine: `WaitForSeconds(seconds)` → set `UpdateSmoke()` → exponential rate decay over `chuffDuration * 0.6` seconds. **Stops any in-flight puff coroutine** with `StopCoroutine(_puffCoroutine)` before starting new one — no overlapping puffs.

`UpdatePlayStop` (called from `OnIdleDidChange` + `OnHasFuelDidChange`) toggles the entire `VisualEffect.Play()/Stop()` based on `(!isIdle && HasFuel && ParticlesEnabled)`. Idle = 600 s elapsed since `_idleTimerLastReset` *and* velocity < 0.01 m/s (per `BaseLocomotive.PeriodicUpdateBody:120`).

### Per-distance vs per-tick recap (steam subcomponents)

| Subcomponent | `ApplyDistanceMoved` reads | Continues at rest? | Why |
|---|---|---|---|
| `SteamLocomotiveWheelAnimator` | `driverVelocity` (drivers), `velocity` (others), `info.DeltaTime` | No | Wheels stop when loco stops — correct |
| `Chuff` | `driverVelocity`, `absReverser`, `absThrottle`, `info.TractiveEffort` | **Yes** via `FixedUpdate` rest detection | Audio fade-out and low-speed puff scheduling |
| `CylinderCockController` | `driverPhase`, `absThrottle` | **Yes** via `UpdateCoroutine` (drain when stopped) | Cocks open at rest before departure |
| `SteamChuffParticleController` | `driverVelocity`, `absThrottle` | **Yes** via `Update` while `isStopped` (smoke fade) | Smoke decay continues post-stop |

### Subcomponent traversal-order fragility

Discovery in `SteamLocomotive.DidLoadModels`:

```csharp
// Model/SteamLocomotive.cs:101-134
Animator componentInChildren = BodyTransform.GetComponentInChildren<Animator>();
_wheelAnimator = BodyTransform.gameObject.AddComponent<SteamLocomotiveWheelAnimator>();
_chuffParticles = BodyTransform.GetComponentInChildren<SteamChuffParticleController>();
// ... config ...
_chuffAudio = BodyTransform.GetComponentInChildren<IChuffProvider>();
_chuffAudio.Configure(wheelset.Diameter, ...);
_chuffAudio.Delegate = _chuffParticles;     // wire the delegate
_wheelAnimator.Configure(...);
// ...
_subcomponents.AddRange(BodyTransform.GetComponentsInChildren<ISteamLocomotiveSubcomponent>());
```

Order in `_subcomponents` after the AddRange:

1. `SteamLocomotiveWheelAnimator` (added directly to `BodyTransform.gameObject` — found first by depth-first traversal).
2. Whatever children's order yields — typically `SteamChuffParticleController`, `Chuff` (which is also `IChuffProvider`), `CylinderCockController` in prefab-hierarchy order.

**The fragility**: `SubcomponentsApplyDistanceMoved` snapshots `driverPhase = _wheelAnimator.DriverPhase` at the **top** of the dispatch loop. Inside the loop, the wheel animator (iterated first) updates `_wheelAnimator.DriverPhase` for the current tick — but the cached `driverPhase` local variable still holds the previous tick's value. So **all consumers see the previous tick's phase**, not the current one. The wheel animator order doesn't actually matter for the snapshotted variable — what would change is if a future patch re-read `_wheelAnimator.DriverPhase` directly inside a subcomponent. **If you write a custom subcomponent and need the current tick's phase, read `((SteamLocomotive)GetComponentInParent<SteamLocomotive>())._wheelAnimator.DriverPhase` directly** (you'll need reflection — `_wheelAnimator` is private). Or postfix `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` to update a public field you can read.

A second fragility: `_subcomponents.Clear()` in `UnloadModels` (line 93) means **mods that add custom subcomponents must re-register on every model reload** (archetype change, prefab swap, etc.). Subscribe to `Car`'s model lifecycle (`DidLoadModels`/`UnloadModels`) — see [cars-cargo.md](cars-cargo.md).

### Patch candidates (driver-phase / subcomponents)

| Method | Why patch |
|---|---|
| `SteamLocomotive.SubcomponentsApplyDistanceMoved` | Wrap to add cross-cutting state (e.g., expose current-tick `DriverPhase` to a custom subcomponent before dispatch). |
| `SteamLocomotiveWheelAnimator.ApplyDistanceMoved` | Postfix to expose `DriverPhase` to a sidecar field; intercept slip visualization. |
| `ChuffFilter.OnAudioFilterRead` | The 4-stroke firing pattern lives here. Replace to support 3-cylinder, 4-cylinder, geared (Shay) engines. **Audio-thread method — keep allocations zero.** |
| `ChuffFilter.GetNextChuffDelay` | Replace to alter low-speed puff timing (e.g., async chuff for compound locos with lead/lag). |
| `Chuff.FixedUpdate` | Hook to send custom chuff data (e.g., to a websocket for telemetry) on every audio-tick. |
| `CylinderCockController.ApplyDistanceMoved` | Replace `+ 0.25` phase offset, change condensate accumulation rate (currently `0.001f` per `dt`). |
| `CylinderCockController.SetSmokeEffects` | Replace per-side phase logic (currently `[i] * 0.25` offset, `2 *` rate doubling). |
| `SteamChuffParticleController.UpdateSmoke` | Replace particle response curve. |
| `SteamLocomotive.DidLoadModels` | Postfix to add custom subcomponents not present as model children. Pair with `UnloadModels` postfix to clean up. |

### MP authority (driver-phase / subcomponents)

- **All subcomponent state is local on every machine.** No KVO replication for `DriverPhase`, `_phase`, `_steam`, `tractiveEffort` (chuff), `absVelocity`, etc. Each client computes from replicated KVO inputs (`throttle`, `reverser`, body `velocity`).
- **`_wheelVelocity` is local on every machine** (computed in `BaseLocomotive.UpdateTractiveEffortWheelState` per `FixedUpdate`, no `IsHost` gate). Slip visualization is **not synced** — a client may see a different slip pattern than the host because the slip computation is timing-sensitive and floating-point divergent.
- **`SteamLocomotive.HasWaterAndCoal` is host-only** (mutated in `PeriodicUpdate` from tender slot quantities). Clients see derived effects through tender-slot KVO replication and the `compressor` KVO bool. `SteamChuffParticleController.UpdatePlayStop` reads `_locomotive.HasFuel` which is the host-replicated state; smoke pulls toward 0 when `HasFuel = false`.
- **`OnHasFuelDidChange` event is local** — fired on each machine when the local `HasFuel` flips. The flip is host-driven via `InvokeHasFuelDidChange()` → `engine.HasWaterAndCoal = ...` cascade in `SteamLocomotive.PeriodicUpdate`.
- The chuff audio sample synthesis in `ChuffFilter.OnAudioFilterRead` runs **on each client's audio thread**. Each machine produces its own chuff audio from its own `engineSpeed`/`engineCutoff`/`engineThrottle` — no replication.

### Gotchas (driver-phase / subcomponents)

- **The 4-stroke firing pattern is NOT parameterized.** Hard-coded `Evaluate(0) + Evaluate(0.25) + Evaluate(0.5) + Evaluate(0.75)` in `ChuffFilter.OnAudioFilterRead`. Mods adding 3-cyl/4-cyl/Shay engines must replace this method.
- **`_subcomponents.Clear()` in `UnloadModels`.** Custom subcomponents must re-register on every model reload.
- **`driverPhase` is the **previous** tick's value for all consumers** (snapshotted at top of dispatch loop). Subcomponents needing the current tick's phase must read `_wheelAnimator.DriverPhase` directly (private field — reflection required).
- **`Chuff._absVelocity` is from `driverVelocity`, not body `velocity`.** During slip, chuff sounds faster than the loco is actually moving — physically realistic.
- **`SteamChuffParticleController` reads `driverVelocity` for `absVelocity`** (line 134). So during slip, particles also go "continuous" at the slipping rate — visually consistent with the chuff audio.
- **`CylinderCockController._phase = driverPhase + 0.25f` is hardcoded.** The 0.25 (90°) offset corresponds to the assumption that cylinder events lead the main-driver reference by a quarter-cycle — appropriate for a two-cylinder simplex with cranks 90° apart.
- **`SteamChuffParticleController` lerp asymmetry (factors 2 vs 4)** in `Update` — TE rises slowly, decays fast. Patch the lerp factor to alter chuff envelope shape.
- **`Chuff.FixedUpdate` rest detection has a 1-FixedUpdate lag.** First fixed update after stop, `_movedLastFixedUpdate` is still true (set last tick); second fixed update sets `_absVelocity = 0`. There's one chuff frame of "ghost speed" after stopping.
- **`ChuffFilter.engineNormalizedTE` is set in `FixedUpdate` not `OnAudioFilterRead`** — but `OnAudioFilterRead` ignores it (the line in `Update` writes `_parameters.EngineThrottle = engineThrottle`, NOT `engineNormalizedTE`). **`engineNormalizedTE` field is dead — set but never read** — likely vestigial parameter from an older volume formula.
- **`SteamChuffParticleController.IsIdle` boundary**: `BaseLocomotive.PeriodicUpdateBody` sets `IsIdle = (|velocity| < 0.01) && (_idleTimerLastReset + 600s < Time.time)` — so smoke stays alive for 10 minutes after the last input even at rest, then particles `Stop()`.

---

## `IDieselLocomotiveSubcomponent` counterpart — `NormalizedExhaustOutputEvent` Action delegate

**There is no `IDieselLocomotiveSubcomponent` interface.** The closest analog is the diesel exhaust particle wiring:

```csharp
// Model/DieselLocomotive.cs:46-66 — DidLoadModels
_particleControllers = BodyTransform.GetComponentsInChildren<DieselExhaustParticleController>().ToList();
_primeMoverAudioPlayer = BodyTransform.GetComponentInChildren<IPrimeMoverAudioPlayer>();
if (_primeMoverAudioPlayer != null) {
    _primeMoverAudioPlayer.NormalizedExhaustOutputEvent = delegate(float normalizedExhaustOutput) {
        foreach (DieselExhaustParticleController particleController in _particleControllers)
            particleController.NormalizedExhaustOutput = normalizedExhaustOutput;
    };
}
```

`NormalizedExhaustOutputEvent` is a public `Action<float>` property on `IPrimeMoverAudioPlayer`:

```csharp
// Audio/PrimeMoverAudioPlayer.cs:23
public Action<float> NormalizedExhaustOutputEvent { get; set; }

// Audio/PrimeMoverAudioPlayer.cs:185-189
private void SetExhaust(int notch) {
    float obj = Mathf.InverseLerp(0f, 8f, notch);
    NormalizedExhaustOutputEvent?.Invoke(obj);
}
```

**This is fired only on notch transition** (from `PrimeMoverCoroutine` when `Notch` differs from `lastNotch`), not per-tick. So:

| Aspect | Steam (`ISteamLocomotiveSubcomponent`) | Diesel (`NormalizedExhaustOutputEvent`) |
|---|---|---|
| Trigger | Per position update (per-distance) | On notch change (audio coroutine) |
| Payload | `MovementInfo`, driverVelocity, absReverser, absThrottle, driverPhase | Single `float` (notch / 8) |
| Multiple consumers | Yes (`_subcomponents` list, foreach) | Yes (single `Action`, fan-out via lambda) |
| Discovery | `GetComponentsInChildren<ISteamLocomotiveSubcomponent>` | `GetComponentInChildren<IPrimeMoverAudioPlayer>` then explicit `Action` assignment |
| Replacement on model reload | Yes (`UnloadModels` clears list) | Yes (`UnloadModels` sets `NormalizedExhaustOutputEvent = null`, line 40) |
| Per-tick callbacks at all? | Per-distance via dispatch | None — diesel particles only update on notch change |

**Practical implications for diesel-side modding**:

- A diesel "subcomponent" mod cannot rely on per-tick state. Either subscribe to `BaseLocomotive.OnHasFuelDidChange` / `OnIdleDidChange` events or attach a `MonoBehaviour` and use `Update`/`FixedUpdate`.
- For per-distance dispatch, postfix `Car.PositionWheelBoundsFront` (no diesel override exists — the base method is the only call site). `MovementInfo` is the parameter.
- For audio-driven exhaust, replace `NormalizedExhaustOutputEvent` with a wrapper that fires more frequently (e.g., on every `PrimeMoverAudioPlayer.Update`), then fan-out to your own particle/effect logic.
- **There is no `Chuff`-equivalent for diesel** — no per-rev cylinder firing model. The audio is notch-loop-based with crossfaded transitions.

---

## `ControlPurpose` enum + cab-control wiring

### The enum (closed, 9 values)

```csharp
// Definition/Model.Definition.Components/ControlPurpose.cs
namespace Model.Definition.Components;
public enum ControlPurpose {
    NotSet,            // 0 — sentinel; ToggleControlComponentBuilder returns null PropertyChange
    CylinderCock,      // 1 — steam-only; LocomotiveControlHelper gates on Archetype
    LocomotiveBrake,   // 2 — both
    TrainBrake,        // 3 — both
    Reverser,          // 4 — both
    Throttle,          // 5 — both (regulator on steam, notch on diesel)
    Whistle,           // 6 — both (whistle on steam, horn on diesel)
    Bell,              // 7 — both
    TrainBrakeCutOut,  // 8 — both (boolean as 0/1 float)
}
```

The enum lives in the `Definition` assembly (`Model.Definition.Components`) — meaning a mod adding a new enum value would require recompiling the Definition assembly, OR using a sentinel (e.g., `ControlPurpose.NotSet` + a custom marker on the `RadialAnimatedControl`).

### `ControlPurpose` reach: every consumer

| Consumer | What it does |
|---|---|
| `RadialAnimatedControl.ControlComponentPurpose` | Per-instance tag set by `RadialControlComponentBuilder.Build:33` from the `RadialControlComponent` definition's `Purpose` field. |
| `BaseLocomotive.TryGetControl(ControlPurpose, out)` | Linear scan of `BodyTransform.gameObject.GetComponentsInChildren<RadialAnimatedControl>()`, returns first match. **NEW SCAN each call** — no caching. |
| `BaseLocomotive.ConnectBodyControls` | Calls `TryGetControl` for `LocomotiveBrake`, `TrainBrake`, `TrainBrakeCutOut`, `Whistle`, `Bell` — wires each to `cabControls.{slot}` and `ConfigurePropertyChange`. |
| `SteamLocomotive.ConnectBodyControls` | Calls `TryGetControl` for `Throttle` (→ `regulator` slot) and `Reverser` (→ `johnsonBar` slot). |
| `DieselLocomotive.ConnectBodyControls` | Calls `TryGetControl` for `Throttle` (→ `throttle` slot) and `Reverser` (→ `reverser` slot). |
| `ToggleControlComponentBuilder.ControlForPurpose` (private static) | Maps each `ControlPurpose` to a `PropertyChange.Control` for the *toggle* control variant (KeyValuePickableToggle). |
| `BrakeStandController.Awake` (line 50, 54, 58) | Hardcodes `trainBrakeControl.ControlComponentPurpose = ControlPurpose.TrainBrake` etc — for steam-engine-style brake stands with three handles. |

**There is no central catalog.** Every consumer hardcodes its set of cases. Adding a new `ControlPurpose` requires patching:

1. `ControlPurpose.cs` (enum) — recompile.
2. `BaseLocomotive.ConnectBodyControls` (or subclass `*.ConnectBodyControls`) to bind the slot.
3. `LocomotiveCabControlsHookup` to add a slot field — recompile.
4. `LocomotiveControlHelper` if you want a setter facade — recompile.
5. `ToggleControlComponentBuilder.ControlForPurpose` if you want a pickable toggle variant.
6. (Optional) `PropertyChange.Control` enum in `Game.Messages` if your control needs its own KVO key (cross-link [events-catalog.md › Control namespace](events-catalog.md#the-control-namespace)).

For most mod use cases, **reuse an existing `ControlPurpose`** — your custom `RadialAnimatedControl` tagged `ControlPurpose.Throttle` will get bound by `BaseLocomotive` automatically. The slot is single-cardinality (first match wins via `TryGetControl`), so don't put two same-tagged controls on one body.

### `LocomotiveCabControlsHookup` — the slot map

```csharp
// RollingStock/LocomotiveCabControlsHookup.cs
public class LocomotiveCabControlsHookup : MonoBehaviour {
    public IGauge speedometer;         // 8   — set in BaseLocomotive.ConnectGauges by id "speedometer"
    public IGauge mainReservoir;       // 10  — id "mainres"
    public IGauge brakeCylinder;       // 12  — id "cyl"
    public IGauge brakePipe;           // 14  — id "line"
    public IGauge equalizingReservoir; // 16  — id "eqlres"
    public ContinuousControl locomotiveBrake;  // 18 — bound from ControlPurpose.LocomotiveBrake
    public ContinuousControl trainBrake;       // 20 — bound from ControlPurpose.TrainBrake
    public ContinuousControl cutout;           // 22 — bound from ControlPurpose.TrainBrakeCutOut
    public ContinuousControl bell;             // 24 — bound from ControlPurpose.Bell
    public ContinuousControl regulator;        // 26 — STEAM ONLY: bound from ControlPurpose.Throttle
    public ContinuousControl johnsonBar;       // 28 — STEAM ONLY: bound from ControlPurpose.Reverser
    public IGauge boilerPressure;              // 30 — id "boiler" (steam-relevant; diesel will null-read)
    public ContinuousControl throttle;         // 32 — DIESEL ONLY: bound from ControlPurpose.Throttle
    public ContinuousControl reverser;         // 34 — DIESEL ONLY: bound from ControlPurpose.Reverser
    public ContinuousControl horn;             // 36 — bound from ControlPurpose.Whistle (steam) or built-in horn (diesel)
}
```

Created in `BaseLocomotive.PreSetupComponents` (line 460) when `lifetime == ComponentLifetime.Model`. Destroyed (set null) in `BaseLocomotive.UnloadModels` (line 481).

**Diesel-vs-Steam slot asymmetry**:

- Steam uses `regulator` + `johnsonBar` (and `boilerPressure` gauge).
- Diesel uses `throttle` + `reverser` (no boiler gauge).
- **Both classes assign to their own slot pair from the same `ControlPurpose.Throttle` / `Reverser` tag.** A custom loco type that wants to look like a steam loco should assign to `regulator`/`johnsonBar`; if it wants to look like a diesel, assign to `throttle`/`reverser`. The `UpdateCabControls` of each subclass reads the appropriate slot.

### `BaseLocomotive.ConnectBodyControls` — the wire-up procedure

```csharp
// Model/BaseLocomotive.cs:233 — virtual; subclasses call base at the END
protected virtual void ConnectBodyControls() {
    ConnectGauges();
    if (TryGetControl(ControlPurpose.LocomotiveBrake, out var foundControl)) {
        cabControls.locomotiveBrake = foundControl;
        foundControl.ConfigurePropertyChange(value => {
            value = LocomotiveBrakeMapFromControl(value);                     // Lerp(-0.1, 1, v)
            return new PropertyChange(id, PropertyChange.Control.LocomotiveBrake, value);
        }, TooltipTextForLocomotiveBrake);
    }
    if (TryGetControl(ControlPurpose.TrainBrake, out var foundControl2)) {
        cabControls.trainBrake = foundControl2;
        foundControl2.ConfigurePropertyChange(
            value => new PropertyChange(id, PropertyChange.Control.TrainBrake, value),
            TooltipTextForTrainBrake);
    }
    if (TryGetControl(ControlPurpose.TrainBrakeCutOut, out var foundControl3)) {
        cabControls.cutout = foundControl3;
        foundControl3.ConfigureSnap(1);                                       // 0/1 only
        foundControl3.ConfigurePropertyChange(
            value => new PropertyChange(id, PropertyChange.Control.CutOut, value < 0.5f),
            TooltipTextForTrainBrakeCutOut);
    }
    if (TryGetControl(ControlPurpose.Whistle, out var foundControl4)) {
        cabControls.horn = foundControl4;
        foundControl4.ConfigurePropertyChange(value =>
            new PropertyChange(id, PropertyChange.Control.Horn, value));
    }
    if (TryGetControl(ControlPurpose.Bell, out var bellControl)) {
        cabControls.bell = bellControl;
        bellControl.ConfigurePropertyChange(
            value => new PropertyChange(id, PropertyChange.Control.Bell, value),
            () => bellControl.Value > 0.5 ? "On" : "Off");
    }
    // ↓ DUMMY-CONTROL FALLBACK for 4 essentials (NOT cutout, NOT regulator/johnsonBar)
    if (cabControls.locomotiveBrake == null) cabControls.locomotiveBrake = DummyControl();
    if (cabControls.trainBrake == null)      cabControls.trainBrake      = DummyControl();
    if (cabControls.horn == null)            cabControls.horn            = DummyControl();
    if (cabControls.bell == null)            cabControls.bell            = DummyControl();

    _controlObservers.Add(KeyValueObject.Observe(KeyForControl(Bell), v => {
        locomotiveControl.Bell = v.FloatValue > 0.5;
        ResetIdleTimer();
    }));
    _controlObservers.Add(KeyValueObject.Observe(KeyForControl(Horn), v => {
        locomotiveControl.Horn = v.FloatValue;
        ResetIdleTimer();
    }));
}
```

Subclass overrides:

- `SteamLocomotive.ConnectBodyControls` (`SteamLocomotive.cs:302`): adds `Throttle` → `cabControls.regulator` (with `ConfigureSnap(100)`) and `Reverser` → `cabControls.johnsonBar` (with `ConfigureSnap(40)`), then calls `base.ConnectBodyControls()`. **Uses `ControlHelper.Throttle = value` setter directly** (not `ConfigurePropertyChange`) — meaning steam throttle/reverser do NOT use the standard ConfigurePropertyChange auth wrapper. They set `CheckAuthorized` and `tooltipText` manually.
- `DieselLocomotive.ConnectBodyControls` (`DieselLocomotive.cs:68`): adds `Throttle` → `cabControls.throttle` (no snap; the setter rounds via `primeMover.notch = RoundToInt(v * 8)`) and `Reverser` → `cabControls.reverser` (no snap; the setter rounds via `Mathf.RoundToInt(Mathf.Lerp(-1, 1, value))`). Then calls `base.ConnectBodyControls()`.

**Triggered at `BaseLocomotive.DidSetBodyActive:474`** — after the body model is loaded. Window: `cabControls != null` is created in `PreSetupComponents` (model lifetime) but slots are populated only here.

### `TryGetControl(ControlPurpose, out ContinuousControl)` lookup

```csharp
// Model/BaseLocomotive.cs:340
protected bool TryGetControl(ControlPurpose purpose, out ContinuousControl foundControl) {
    RadialAnimatedControl[] componentsInChildren =
        BodyTransform.gameObject.GetComponentsInChildren<RadialAnimatedControl>();
    foreach (RadialAnimatedControl rac in componentsInChildren)
        if (rac.ControlComponentPurpose == purpose) { foundControl = rac; return true; }
    foundControl = null;
    return false;
}
```

- **Linear scan, no cache.** Called once per slot per `ConnectBodyControls` — at most ~7 calls, so the `O(n*7)` cost is negligible. Modders adding custom dispatch can reuse this safely.
- **Returns only `RadialAnimatedControl` instances.** A `VerticalControl` (cross-link [interaction-controls.md › ContinuousControl substrate](interaction-controls.md#rollingstockcontinuouscontrolscontinuouscontrol-substrate)) tagged with a `ControlPurpose` would NOT be found. **`ControlComponentPurpose` only exists on `RadialAnimatedControl`** (line 76 of `RadialAnimatedControl.cs`). To make a `VerticalControl` discoverable as a `ControlPurpose` slot, you'd need to add a `ControlComponentPurpose` field to `VerticalControl` and patch `TryGetControl` to scan both types.
- **First-match wins**, so duplicate-tagged controls are silently dropped. Only one `RadialAnimatedControl` per body should carry each tag.
- **`BodyTransform.gameObject`** restriction means controls under non-body transforms (e.g., the cab-interior prefab if it's a sibling rather than a body child) won't be found. Verify your prefab hierarchy.

### `DummyControl` substitution path

```csharp
// Model/BaseLocomotive.cs:355
protected ContinuousControl DummyControl() {
    GameObject obj = new GameObject("DummyControl");
    obj.transform.SetParent(BodyTransform);
    return obj.AddComponent<DummyControl>();
}
```

```csharp
// RollingStock.ContinuousControls/DummyControl.cs
namespace RollingStock.ContinuousControls;
public class DummyControl : ContinuousControl { }     // empty subclass
```

`DummyControl` inherits `ContinuousControl` with no behaviour added. Properties:

- `Value` getter returns the protected `value` field (default `0f`).
- `Value` setter writes the field but the `OnValueChanged` event has no subscribers (nothing called `ConfigurePropertyChange` on it).
- `tooltipText` returns `""` (default `() => ""`).
- `displayName` is `null` (never assigned on the dummy).
- It IS a valid `IPickable`, but its `MaxPickDistance = 5f` and the lack of an animator means picking it does nothing visible.

**Substituted into 4 slots only** (`locomotiveBrake`, `trainBrake`, `horn`, `bell`) — see `BaseLocomotive.ConnectBodyControls:267-285`. **NOT substituted for `cutout`** (steam may not have a cut-out control), nor for `throttle`/`reverser`/`regulator`/`johnsonBar` (subclasses substitute these — see below).

Subclass substitutions:

- `SteamLocomotive.ConnectBodyControls:332-341` substitutes `regulator` and `johnsonBar`.
- `DieselLocomotive.ConnectBodyControls:95-104` substitutes `throttle` and `reverser`.

**Total dummy-fallback list (vanilla)**: 4 from base + 2 per subclass = up to 6 dummy controls per loco that lacks tagged controls.

**Effect of a dummy substitution**:

- `BaseLocomotive.UpdateCabControls` writes `cabControls.locomotiveBrake.Value = ...` etc, which sets `value` on the dummy — completely silent (no animation, no tooltip change).
- `cabControls.horn.Value = locomotiveControl.Horn` (line 513) updates the dummy's `value` — but since nothing reads it, harmless.
- `cabControls.cutout` is **NOT substituted** — meaning a loco without a TrainBrakeCutOut control will leave `cabControls.cutout = null`. `BaseLocomotive.UpdateCabControls:499` guards this with `if (cabControls.cutout != null)`. **A subclass with custom `UpdateCabControls` must replicate this null guard or risk NRE.**

### `LocomotiveControlHelper` — the SendPropertyChange facade

```csharp
// Model/LocomotiveControlHelper.cs
public class LocomotiveControlHelper {
    private readonly BaseLocomotive _locomotive;
    private const float FeedValve = 90f;     // ← only used by TrainBrakeMakeSet, the 90 PSI feed-valve setpoint

    public float  Throttle             { get; set; }    // PropertyChange.Control.Throttle, clamped 0..1
    public float  Reverser             { get; set; }    // PropertyChange.Control.Reverser, clamped -1..+1
    public float  LocomotiveBrake      { get; set; }    // .LocomotiveBrake, clamped 0..1
    public float  TrainBrake           { get; set; }    // .TrainBrake, clamped 0..1
    public bool   Bell                 { get; set; }    // .Bell
    public float  Horn                 { get; set; }    // .Horn (no clamp)
    public bool   CylinderCocksOpen    { get; set; }    // .CylinderCock — GATED on Archetype == LocomotiveSteam

    public float  TrainBrakeSet => Mathf.Lerp(0f, 90f, TrainBrake);   // 0..90 PSI representation
    public void   TrainBrakeMakeSet(float psi);
    public void   BailOff();    // sends LocomotiveBrake = -0.1 (the bail sentinel, see brakes.md)
}
```

All setters route through `Car.SendPropertyChange(control, value)`:

```csharp
// Model/Car.cs:3076
public void SendPropertyChange(PropertyChange.Control control, float value)
    => SendPropertyChange(control, new FloatPropertyValue(value));

public void SendPropertyChange(PropertyChange.Control control, IPropertyValue value) {
    if (!_willDestroyCalled)
        StateManager.ApplyLocal(new PropertyChange(id, PropertyChange.KeyForControl(control), value));
}
```

`StateManager.ApplyLocal(new PropertyChange(...))` is the universal client→host KVO write entry point. Auth is delegated to the `PropertyChange` message's auth attribute and the `Car.AuthorizationRequirementForPropertyWrite` resolver (cross-link [access-control.md](access-control.md)).

**`CylinderCocksOpen` setter is the only one with an Archetype gate**:

```csharp
public bool CylinderCocksOpen {
    set {
        if (_locomotive.Archetype == CarArchetype.LocomotiveSteam)
            ChangeValue(PropertyChange.Control.CylinderCock, value);
    }
}
```

Setting `CylinderCocksOpen = true` on a diesel is a **silent no-op**. Mods adding electric/battery locos that want a different open/close mechanism (e.g., dynamic-brake setup) can either patch this gate or use a custom `PropertyChange.Control`. **The other side** (`get`) returns the KVO `Bool` regardless of archetype — so reading is unrestricted, only writing is gated.

**`BailOff()`** sends `LocomotiveBrake = -0.1f` — the bail sentinel value. `LocomotiveBrakeMapFromControl(value)` in `BaseLocomotive` maps the slider's 0..1 to -0.1..1, so the slider value `0.0` produces actual locoBrake `-0.1` (which `LocomotiveAirSystem.OnResetBailOff` handler uses as the bail-off trigger — cross-link [brakes.md › bail-off sentinel](brakes.md#independent-brake--bail-off-sentinel)). **`BailOff()` bypasses the slider mapping and writes -0.1 directly to the KVO key**, then `BaseLocomotive.PostRestoreProperties` clamps a saved `< 0` value back to 0 to avoid a stuck-bailed loco on load.

### `BaseLocomotive.SendPropertyChange` write path

```
PickableActivate (drag handle)
  → RadialAnimatedControl.ActiveCoroutine: value = Snap(num); UserChangedValue();
    → ContinuousControl.UserChangedValue → SendValue → OnValueChanged event
      → (subscribed by ConfigurePropertyChange or subclass-specific lambda)
        → propertyChangeFunc(value) → new PropertyChange(carId, ControlEnum, value)
          → StateManager.ApplyLocal(message)
            → (host) HandleMessage → KeyValueObject[key] = value
            → (client) WireProtocol → server → host re-broadcast → client KeyValueObject[key] = value
              → BaseLocomotive observer (in ObserveCoreProperties or ConnectBodyControls):
                → locomotiveControl.AbstractThrottle = v.FloatValue; (etc.)
                → ResetAtRest(); ResetIdleTimer();
```

KVO observers are wired by `BaseLocomotive.ObserveCoreProperties` (`BaseLocomotive.cs:362`) for `Throttle`, `Reverser`, `LocomotiveBrake`, `TrainBrake`, `CutOut`, AND by `ConnectBodyControls` (line 286-295) for `Bell`, `Horn`. **Five observers in `ObserveCoreProperties` + 2 in `ConnectBodyControls` = 7 control-replication observers per loco**. The 7 do NOT cover `CylinderCock`, `Compressor`, `Idle`, `Mu`, `Headlight` — these have their own observers wired elsewhere or are read on demand:

- `Compressor` is **host-written** in `BaseLocomotive.UpdateCabControls:506` (`KeyValueObject[KeyForControl(Compressor)] = Bool(compressorRunning)`) and observed by anyone who cares (e.g., `compressor` audio).
- `Idle` is **host-written** in `BaseLocomotive.PeriodicUpdateBody:121` and observed via `BaseLocomotive.DidLoadModels:454` (fires `OnIdleDidChange` event). Consumers: `DynamoPlayer`, `FireboxEffectController`, `SteamChuffParticleController`.
- `Mu` is read on demand (`BaseLocomotive.IsMuEnabled` getter).
- `CylinderCock` is observed by `CylinderCockController._controlObserver` directly (`Effects/CylinderCockController.cs:85-89`).
- `Headlight` is observed by `HeadlightController` / `LocomotiveLightingController` (cross-link [car-fittings.md › headlights](car-fittings.md#lighting)).

**Diesel/Steam adapter differences** (recap from [locomotive-architecture.md › `LocomotiveControlAdapter`](locomotive-architecture.md#locomotivecontroladapter--the-alternate-loco-type-seam)):

- Steam adapter (`SteamLocomotiveControl`): `AbstractThrottle.set` writes `engine.regulator = value` (continuous, 0..1).
- Diesel adapter (`DieselLocomotiveControl`): `AbstractThrottle.set` writes `primeMover.notch = RoundToInt(value * 8); audio.primeMover.Notch = primeMover.notch` (lossy round to int notch + audio sync).
- **Diesel adapter has the audio-coupling side-effect**, the steam adapter doesn't (steam audio is observer-driven through `Chuff.ApplyDistanceMoved` reading `_absThrottle`).
- The `ConfigurePropertyChange` extension wires `OnValueChanged → ApplyLocal(new PropertyChange)` — fully symmetric for both types when used through brakes/horn/bell/cutout; but throttle and reverser are wired with explicit `OnValueChanged` lambdas (subclass-specific) in `*.ConnectBodyControls` and don't use `ConfigurePropertyChange`. **The throttle/reverser path is the one place subclass-specific binding is needed**, because the value transformation differs (steam: continuous, diesel: 8-step notch via int round).

### Patch candidates (cab-control wiring)

| Method | Why patch |
|---|---|
| `BaseLocomotive.TryGetControl` | Add a fallback to scan `VerticalControl`/custom control types; or implement caching. Per-call linear scan is fine for vanilla but a custom-controls mod could cache. |
| `BaseLocomotive.ConnectBodyControls` | Postfix to add custom `ControlPurpose` bindings. Always call after `cabControls` is non-null. |
| `BaseLocomotive.DummyControl()` | Replace to inject a richer dummy (e.g., one that logs writes for debug). |
| `LocomotiveControlHelper.CylinderCocksOpen.set` | Remove archetype gate to allow non-steam locos to have an analogous control. |
| `LocomotiveControlHelper.BailOff` | Replace bail behaviour (e.g., partial bail at -0.05). |
| `Car.SendPropertyChange(PropertyChange.Control, IPropertyValue)` | Universal control-write hook; intercept before `ApplyLocal`. Useful for client-side validation/logging. |
| `RadialAnimatedControl.ControlComponentPurpose` (property) | The single field that drives discovery. Patch `RadialAnimatedControl` to expose alternate dispatch hooks. |
| `ControlExtensions.ConfigurePropertyChange` | The ubiquitous `OnValueChanged → ApplyLocal` wiring. Wrap to add tracing or veto. |

### MP authority (cab-control wiring)

- All `PropertyChange` messages route through standard auth — `Car.AuthorizationRequirementForPropertyWrite` decides per-key. Most control keys default to `Crew` access level (or TrainCrew membership). Cross-link [access-control.md › Car prefix-array](access-control.md).
- **Two-player tug-of-war is possible**: two clients can both grab the throttle handle, both `RadialAnimatedControl`s are picking, and both write to the KVO independently. The last write wins. The `ContinuousControl` substrate has **no MP coordination** — client A's drag and client B's drag interleave their `SendValue` calls. See [interaction-controls.md › two-player tug-of-war](interaction-controls.md#two-player-tug-of-war).
- `DummyControl` is purely local (each machine's `BaseLocomotive` creates its own dummy GameObject — they're not networked).
- Bell and Horn observers fire **on every machine** — clients see other players' bell/horn changes via KVO replication, just like throttle/reverser. The `BaseLocomotive.locomotiveControl.Bell = v.FloatValue > 0.5` write goes into local audio state on each machine.

### Gotchas (cab-control wiring)

- **`TryGetControl` only scans `RadialAnimatedControl`.** A custom `ContinuousControl` subtype tagged with `ControlPurpose` will not be discovered. Workaround: subclass `RadialAnimatedControl`, or patch `TryGetControl`.
- **`DummyControl` is not substituted for `cutout`.** A loco without a `TrainBrakeCutOut` tag leaves `cabControls.cutout = null`. `BaseLocomotive.UpdateCabControls` guards with `if (cabControls.cutout != null)`, but custom `UpdateCabControls` overrides must replicate the guard.
- **`CylinderCocksOpen.set` Archetype gate** is the only archetype-gated setter on `LocomotiveControlHelper`. If you make a "steam-electric" hybrid using a diesel base, the cylinder cocks control is silently inert.
- **`BaseLocomotive.UpdateCabControls.cabControls.horn.Value = locomotiveControl.Horn` (line 513)** writes the local audio horn level back to the slider every `FixedUpdate`. This is a *display-side* update — keeps the visible horn handle in sync with the audio level. **Patch implications**: if you replace the horn audio with a fade-out, the slider will visibly droop too. Decouple by setting `cabControls.horn.Value = KeyValueObject[KeyForControl(Horn)].FloatValue` instead.
- **`SteamLocomotive.ConnectBodyControls` does NOT use `ConfigurePropertyChange` for throttle/reverser** — it sets `OnValueChanged`, `CheckAuthorized`, `tooltipText` separately. So patching `ConfigurePropertyChange` does NOT intercept steam throttle/reverser. Patch the lambda assignment in `ConnectBodyControls` directly, or postfix the whole method.
- **`ConnectBodyControls` runs in `DidSetBodyActive`** (`BaseLocomotive.cs:474`), AFTER `PreSetupComponents` (which created `cabControls` MonoBehaviour) but BEFORE the first `UpdateCabControls`. Window: `cabControls != null && all_slots_null` — patches that read slots in this window will hit nulls.
- **`LocomotiveCabControlsHookup` slots that go through `DummyControl()` substitution still throw NRE on `cabControls.cutout` if the steam loco lacks a cutout** — only 4 slots are dummy'd. Be careful.
- **`PeriodicUpdate(1f)` is wrapped in try/catch** (`BaseLocomotive.cs:123-130`) — failures are logged but do not stop the coroutine. Periodic-update fuel logic that throws will *silently* keep failing every second. Patches that add per-second logic should not rely on errors propagating.
- **`ResetIdleTimer` only resets if `StateManager.IsHost && !IsInDidLoadModels`** (`BaseLocomotive.cs:202-208`). Client-side throttle changes do NOT reset the host's idle timer; the host's KVO observer for `Throttle` resets it on the host side after replication. So `IsIdle` flips are host-authoritative.

---

## `SteamEngine` deep-dive

`Model.Physics/SteamEngine.cs` — 101 lines, MonoBehaviour. The "engine" half of `SteamLocomotive`.

### State (every field)

```csharp
public int   numberOfCylinders     = 2;       // 8     — read only by TrainMath formula at TE-build time
public float pistonDiameterInches  = 20f;     // 10
public float pistonStrokeInches    = 26f;     // 12
public float maximumBoilerPressure = 200f;    // 14    — set from definition; static; never decays
public float driverDiameterInches  = 63f;     // 16
public float weightOnDrivers       = 108000f; // 18    — adhesive weight gate (BaseLocomotive.AdhesiveWeight)
public float totalHeatingSurface   = 2896f;   // 20    — TE-falloff curve selector

[Range(-1,1)] public float reverser;          // 23    — written by SteamLocomotiveControl.AbstractReverser.set
[Range(0,1)]  public float regulator;         // 26    — written by SteamLocomotiveControl.AbstractThrottle.set
public bool   running              = true;    // 28    — never written elsewhere; effectively a kill-switch field
public float  tractiveEffort;                 // 30    — last computed TE
public float  pressure;                       // 32    — set ONCE in UpdateMaximumTractiveEffort to maximumBoilerPressure

[NonSerialized] public float MaximumSpeedMph             = 75f;       // 35 — written by SteamLocomotive.FinishSetup
[NonSerialized] public float? OverrideStartingTractiveEffort;         // 38 — from PublishedTractiveEffort

private float _reverserPowerMultiplier;       // 40    — last cutoff multiplier (debug)
private float _estimatedGrateSqFt;            // 42    — CACHED; computed in UpdateMaximumTractiveEffort

public float  MaximumTractiveEffort   { get; private set; } = 28000f;  // 44 — CACHED
public float  NormalizedTractiveEffort => Clamp01(|tractiveEffort| / MaximumTractiveEffort);
public float  WaterConsumptionRate    { get; private set; }            // 48
public float  CoalConsumptionRate     { get; private set; }            // 50
public bool   HasWaterAndCoal         { get; set; } = true;            // 52
```

### `MaximumTractiveEffort` caching gotcha (one-shot)

```csharp
// Model.Physics/SteamEngine.cs:54
public void UpdateMaximumTractiveEffort() {
    MaximumTractiveEffort = CalculateTractiveEffort(maximumBoilerPressure, 0f);   // ← formula evaluated AT 0 mph
    pressure = maximumBoilerPressure;                                              // ← pressure baked in
    _estimatedGrateSqFt = EstimateGrateSqFt(MaximumTractiveEffort);

    static float EstimateGrateSqFt(float te) {
        return 2.3f + 0.000236f * te + 1.427E-08f * te * te;                       // 2nd-order polynomial
    }
}
```

Called once from `SteamLocomotive.FinishSetup:215` after all parameter assignments. **Never called again in vanilla.** Implications:

- **Mid-run changes to `maximumBoilerPressure`, `pistonDiameterInches`, `pistonStrokeInches`, `driverDiameterInches`, `numberOfCylinders`, `totalHeatingSurface` have NO EFFECT** on `MaximumTractiveEffort` until you re-call `UpdateMaximumTractiveEffort()`.
- Worse: `pressure` is also fixed at this single call. So `MaximumTractiveEffortAtVelocity(absMph)` (line 65) uses `pressure = maximumBoilerPressure` indefinitely. **No boiler dynamics.**
- `_estimatedGrateSqFt` is also one-shot — coal consumption uses it directly.
- `NormalizedTractiveEffort` uses the cached `MaximumTractiveEffort` as denominator — so it would read wrong if you mutate the engine without re-caching.

**Patch recipe**: postfix `UpdateMaximumTractiveEffort` to expose a re-cache event for mods to subscribe to; OR call `engine.UpdateMaximumTractiveEffort()` after any param mutation; OR replace `MaximumTractiveEffort` getter to be live-computed.

### `EstimateGrateSqFt` polynomial

```csharp
// SteamEngine.cs:59 — local function inside UpdateMaximumTractiveEffort
return 2.3f + 0.000236f * te + 1.427E-08f * te * te;
```

For typical `MaximumTractiveEffort` ranges:
- TE 15,000 lbf → ~5.8 sq ft + 0.003 = **5.8** sq ft (small switcher)
- TE 30,000 lbf → ~9.4 + 0.013 = **9.4** sq ft (light freight)
- TE 50,000 lbf → ~14.1 + 0.036 = **14.1** sq ft (medium freight)
- TE 80,000 lbf → ~21.2 + 0.091 = **21.3** sq ft (heavy freight)
- TE 120,000 lbf → ~30.6 + 0.21 = **30.8** sq ft (Mallet-class)

Used by `TrainMath.InferCoalConsumption` (cross-link [traction.md › fuel](traction.md#steam-fuelwater-consumption)).

### `pressure` is static — no boiler dynamics

```csharp
// SteamEngine.cs:57 — inside UpdateMaximumTractiveEffort
pressure = maximumBoilerPressure;
```

After the one-shot init, `pressure` never changes. `MaximumTractiveEffortAtVelocity` (line 65) calls `CalculateTractiveEffort(pressure, absVelocityMph)` which uses the live `pressure` value — but since `pressure == maximumBoilerPressure` always, the engine produces full TE potential regardless of:

- Whether the firebox has fuel (only `HasWaterAndCoal` zeros `tractiveEffort` in line 73; pressure stays static).
- Whether the regulator has been open for 5 seconds or 5 hours (no time-to-build-pressure model).
- Throttle/water consumption interaction (the polynomial in `TrainMath.CalculateWaterConsumption` consumes resources but never feeds back into pressure).
- Blower state (no blower control exists).
- Safety valve opening (no model — pressure can never exceed `maximumBoilerPressure` because it's never above it).
- Priming, foaming, boiler scale, water glass position, fusible plug — none modeled.

**The whole boiler-thermodynamics layer is absent.** Steam locos in vanilla are functionally diesel-electrics with a different TE curve and a forced "cutoff multiplier" via `ReverserPowerMultiplier`.

### Steam controls that DON'T exist (vanilla)

| Real-world control | Vanilla state |
|---|---|
| **Cutoff regulator** (separate from reverser) | NONE — the reverser IS the cutoff. Reverser position 0..1 directly = cutoff 0..1 (after the 0.1 deadzone). |
| **Blower** | NONE — no fire-management at all. |
| **Damper** (firebox air) | NONE. |
| **Injector lever** | NONE — water just drains from tender slot at `WaterConsumptionRate`. |
| **Stoker / Fireman valve** | NONE — coal drains from tender. No fire-bed simulation. |
| **Atomizer / Oil-fired bypass** | NONE — only "coal" load is consumed; no oil burner. |
| **Safety valve** | Cosmetic only (some prefabs have a particle effect but no logic). |
| **Drifting throttle / cylinder wash valve** | Partially — `Control.CylinderCock` exists (`CylinderCockController`), but cylinder cocks affect **only smoke/audio**, not TE or condensate-protection. |
| **Boiler check valve / non-return valve** | NONE — no concept of injector failure. |
| **Reverse gear lock / John Bull lever** | NONE — reverser is a continuous slider with snap. |
| **Live steam injector vs feedwater pump distinction** | NONE — single water flow. |
| **Superheater / saturation distinction** | NONE — `pressure` is the only steam-side variable. |

The `LocomotiveCabControlsHookup` has slots only for `regulator` (=throttle), `johnsonBar` (=reverser), `boilerPressure` gauge, `mainReservoir`, `brakePipe`, `equalizingReservoir`, `brakeCylinder`, `speedometer`. **None of the omitted controls have slots.** Adding a blower or stoker requires extending the hookup MonoBehaviour, adding a new `ControlPurpose` enum value, and patching `ConnectBodyControls` to bind it. Cross-link [traction.md › Out of scope](traction.md#out-of-scope-explicit-confirmation).

### Coal/water consumption formulas

`UpdateConsumption` (line 80):

```csharp
float waterRaw = TrainMath.CalculateWaterConsumption(regulator, reverser, wheelVelocityMph,
                                                      maximumBoilerPressure, pistonStrokeInches,
                                                      pistonDiameterInches, driverDiameterInches);
float coalFromWater = TrainMath.InferCoalConsumption(waterRaw, _estimatedGrateSqFt);
float waterScaled = waterRaw * 1.3f;       // ← 1.3 fudge factor
WaterConsumptionRate = Mathf.Lerp(WaterConsumptionRate, waterScaled, Time.deltaTime);
CoalConsumptionRate  = Mathf.Lerp(CoalConsumptionRate,  coalFromWater, Time.deltaTime);
if (WaterConsumptionRate < 0.001f && waterScaled < WaterConsumptionRate)
    WaterConsumptionRate = 0f;             // snap-to-zero when below threshold and decreasing
if (CoalConsumptionRate < 0.001f && coalFromWater < CoalConsumptionRate)
    CoalConsumptionRate = 0f;
```

- **Per-second smoothing** via `Mathf.Lerp(current, new, Time.deltaTime)` — gives ~1-second time constant.
- **Snap-to-zero floor** at 0.001f — prevents asymptotic decay.
- **`waterRaw * 1.3f` fudge** — coal estimate uses unscaled water; final water rate is 30% higher.
- Drained from tender slots in `SteamLocomotive.PeriodicUpdate:247-254`:
  ```csharp
  float coal = car.GetLoadInfo(_coalSlot)?.Quantity ?? 0;
  float water = car.GetLoadInfo(_waterSlot)?.Quantity ?? 0;
  // ...
  num = Mathf.Max(0, num - engine.CoalConsumptionRate * dt);
  num2 = Mathf.Max(0, num2 - engine.WaterConsumptionRate * dt);
  car.SetLoadInfo(_coalSlot,  new CarLoadInfo("coal", num));
  car.SetLoadInfo(_waterSlot, new CarLoadInfo("water", num2));
  ```

**Dead helper note** (per [traction.md](traction.md)): `TrainMath.CalculateCoalWaterConsumption(throttle, maxTE)` (line 127 of `TrainMath.cs`) is a separate simpler formula `coal = 2 * throttle * maxTE * 2 / 35000` that is **not used by anything in vanilla**. It's a vestigial helper from an earlier formula iteration. Don't be fooled by its public visibility.

### `running` field is a kill-switch

`SteamEngine.running = true` is initialized but **never written anywhere else in vanilla**. `CalculateTractiveEffort` does NOT consult `running` (only `HasWaterAndCoal`). Compare to `PrimeMover.CalculateTractiveEffort` which DOES consult `running`:

```csharp
// Model.Physics/PrimeMover.cs:50
if (!running || !HasFuel) {
    amps = 0f; rpms = 0f; tractiveEffort = 0f;
    return 0f;
}
```

So setting `primeMover.running = false` zeros diesel TE; setting `engine.running = false` does **nothing for steam**. **`SteamEngine.running` is dead** — likely a leftover from before `HasWaterAndCoal` was the gating condition. Mod recipe: a kill-switch for steam loco TE must zero either `engine.regulator` or `engine.HasWaterAndCoal`.

### Patch candidates (`SteamEngine`)

| Method | Why patch |
|---|---|
| `SteamEngine.UpdateMaximumTractiveEffort` | Add boiler dynamics — replace fixed `pressure = maximumBoilerPressure` with a state. Re-cache on demand. |
| `SteamEngine.CalculateTractiveEffort(float)` | Add fire-management gating, blower-driven pressure recovery, safety-valve venting. |
| `SteamEngine.UpdateConsumption` | Replace coal/water curves; add real fire-tube model. |
| `SteamEngine.MaximumTractiveEffort` (auto-prop) | Replace getter to be live-computed if you want continuous response to param changes. |
| `SteamEngine.NormalizedTractiveEffort` (getter) | Same — patch to use a fresh denominator. |

### Gotchas (`SteamEngine`)

- **`MaximumTractiveEffort` and `_estimatedGrateSqFt` are one-shot caches.** Mid-run param changes won't reflect until `UpdateMaximumTractiveEffort` is re-called.
- **`pressure = maximumBoilerPressure` permanently.** No boiler dynamics whatsoever.
- **`numberOfCylinders` is read only by TrainMath formula**; doesn't affect chuff audio rate or cylinder cock count.
- **`running` field is dead** — TE doesn't consult it. Use `HasWaterAndCoal` or `regulator` instead.
- **`UpdateConsumption` is called from inside `CalculateTractiveEffort`** — every TE-compute also computes consumption. `CalculateTractiveEffort` runs once per `BaseLocomotive.FixedUpdate` (50 Hz). Consumption smoothing is done over `Time.deltaTime` per-call which assumes ~50 Hz; if you replace the call cadence, retune the lerp.
- **Water/coal gating is binary**: `HasWaterAndCoal = (coal > 0.001 && water > 0.001)`. There's no "low fuel warning" — TE goes from full to zero on the tick the slot empties.
- **`HasWaterAndCoal` setter is public.** Mods can force it true/false externally. But `SteamLocomotive.PeriodicUpdate` overwrites it from tender slot quantities every second — your override will be reverted on the next 1Hz tick unless you also patch `PeriodicUpdate`.

---

## `PrimeMover` deep-dive

`Model.Physics/PrimeMover.cs` — 93 lines, MonoBehaviour. Diesel-electric model.

### State (every field)

```csharp
[Tooltip("Maximum tractive effort for this diesel prime mover.")]
public  int   startingTractiveEffort = 49500;   // 8     — set from DieselLocomotiveDefinition.StartingTractiveEffort

public  bool  running = true;                   // 10    — actually consulted (see CalculateTractiveEffort)
public  int   reverser;                         // 12    — int -1/0/+1 (NOT float)
public  int   notch;                            // 14    — int 0..8
public  float tractiveEffort;                   // 16
public  float amps;                             // 18    — display-only
public  float rpms;                             // 20    — display-only

private const int MaxAmps = 900;                // 22

// Notch tables
private readonly int[]   _notchToRpm        = { 300, 362, 425, 487, 550, 613, 675, 738, 800 };
private readonly float[] _notchToPowerPercent = { 0f, 0.04f, 0.13f, 0.23f, 0.35f, 0.5f, 0.65f, 0.83f, 1f };
// 9 entries indexed 0..8 (0 = idle)

private float actualPowerPercent;               // 28    — slewed toward target

public  float NormalizedTractiveEffort => Clamp01(|tractiveEffort| / startingTractiveEffort);
public  float FuelConsumptionRate { get; }      // 32    — quadratic in normalized notch power
public  bool  HasFuel { get; set; } = true;
```

### Notch → RPM and power tables

| Notch | RPM | Power % | 
|---|---|---|
| 0 (idle) | 300 | 0% |
| 1 | 362 | 4% |
| 2 | 425 | 13% |
| 3 | 487 | 23% |
| 4 | 550 | 35% |
| 5 | 613 | 50% |
| 6 | 675 | 65% |
| 7 | 738 | 83% |
| 8 (full) | 800 | 100% |

**Quadratic power curve approximated by lookup table.** The deltas between notches are not uniform — notches 2-7 ramp roughly 12-15% each, notches 1 and 8 contribute smaller increments. RPMs are uniformly +63 per notch (NOT a real EMD/GE pattern, but close).

`actualPowerPercent` slews toward the table value with asymmetric rates:

```csharp
// PrimeMover.cs:58
float maxDelta = Time.deltaTime * ((actualPowerPercent < num) ? 0.1f : 0.5f);
actualPowerPercent = Mathf.MoveTowards(actualPowerPercent, num, maxDelta);
```

- **Spool-up: 0.1/sec** — full notch transition (0→1) takes ~10 seconds.
- **Spool-down: 0.5/sec** — full transition (1→0) takes ~2 seconds.

This is the diesel-electric "load up" delay and the "throttle response" asymmetry. Compare to steam where TE is instant on regulator change.

### `FuelConsumptionRate` formula

```csharp
// PrimeMover.cs:32
public float FuelConsumptionRate {
    get {
        if (notch < 1) return 0f;                                 // idle = no fuel burn
        float normalizedPower = (float)(notch * startingTractiveEffort) / 64750f;
        float gallonsPerHour = -0.5957576f + 4.960411f * normalizedPower
                             + 1.051407f * normalizedPower * normalizedPower;
        return Mathf.Max(0f, gallonsPerHour / 3600f);             // gph → gps
    }
}
```

- **Quadratic fit** in `(notch * startingTE) / 64750`. The 64750 normalizer corresponds to "notch 1 at 64,750 lbf TE → power index = 1.0", which matches no specific real loco but produces reasonable curves.
- For an SD45-class loco at notch 8 (49500 starting TE):
  - `normalizedPower = 8 * 49500 / 64750 = 6.12`
  - `gph = -0.6 + 4.96*6.12 + 1.05*6.12² = -0.6 + 30.4 + 39.3 = 69.1 gph`
  - Per-second: `69.1 / 3600 = 0.0192 gph`
- For idle (notch 0), returns 0 — diesels burn no fuel at idle in vanilla. **Real diesels burn 4-8 gph at idle**; this is a notable simplification. (Auto-shutdown would be unnecessary in vanilla because of this.)

Drained from load slot 0 in `DieselLocomotive.PeriodicUpdate:139-144` (cross-link [locomotive-architecture.md › fuel slot wiring](locomotive-architecture.md#fuel-slot-wiring)).

### Diesel TE curve — `CalculateContinuousTractiveEffort80000`

```csharp
// PrimeMover.cs:83
private static float CalculateContinuousTractiveEffort80000(float absMph) {
    return 16253.46f + 201411f / Mathf.Pow(2f, absMph / 4.534249f);
}
```

- Exponential decay: TE drops by half every 4.534 mph (the `/ 4.534249f` in the exponent).
- Asymptote: 16,253.46 lbf at infinite speed (continuous TE floor).
- At 0 mph: `16,253 + 201,411 = 217,665` (extrapolated; real call uses 10 mph blend below).
- Calibrated for a 80,000-lbf-starting-TE loco; scaled by `startingTractiveEffort / 80000` for other ratings.

`CalculateTractiveEffort(mph, startingTE)` (line 72):

```csharp
float t = Mathf.Clamp01(Mathf.InverseLerp(0f, 10f, mph));
float scale = startingTractiveEffort / 80000f;
if (mph < 10f)
    return Mathf.Lerp(startingTractiveEffort, CalculateContinuousTractiveEffort80000(10f) * scale, t);
return CalculateContinuousTractiveEffort80000(mph) * scale;
```

- **Below 10 mph: linear lerp** from `startingTractiveEffort` (at 0 mph) to the continuous-TE-curve-at-10-mph value.
- **Above 10 mph**: scaled exponential decay.
- The 10 mph crossover is a "starting tractive effort plateau" approximation — real diesels have more complex transition.

### Why diesel notch advance is "8 steps"

The 8-step notching comes from the table sizes (9 entries = 8 + idle). The `LocomotiveControlAdapter.ThrottleInputNotches = 8` (DieselLocomotiveControl) and `ThrottleValueSteps = 8` are derived constants. The **conversion lossy round in `DieselLocomotiveControl.AbstractThrottle.set`**:

```csharp
// RollingStock/DieselLocomotiveControl.cs:34
set {
    primeMover.notch = Mathf.RoundToInt(value * 8f);
    if (audio != null && audio.primeMover != null)
        audio.primeMover.Notch = primeMover.notch;
}
```

`value * 8f` then `RoundToInt`. So setting `AbstractThrottle = 0.5f` produces `notch = 4`. Setting `0.51f` produces `notch = 4`; `0.56f` produces `notch = 4`; `0.57f` produces `notch = 5` (round-to-nearest with banker's rounding off). **The 0.0625 (1/16) bucket boundaries** are at 0.0625, 0.1875, 0.3125, ... .

### `_notchToPowerPercent[notch]` ramp control

`actualPowerPercent` is the *displayed* TE % — it spools toward the table value. So even though `notch` jumps instantly on KVO write, the prime mover's actual output ramps. `tractiveEffort = num2 * MaxTractiveEffort(absMph)` where `num2 = actualPowerPercent * (float)reverser`.

**Implication for AE / planning**: the AutoEngineerPlanner needs to predict that "notch 8" doesn't deliver full TE for ~10 seconds. Whether it does is a planning-internal question; cross-link [mu-dpu-coordination.md](mu-dpu-coordination.md) for AE behaviour.

### Patch candidates (`PrimeMover`)

| Method | Why patch |
|---|---|
| `PrimeMover.CalculateTractiveEffort(float)` | Replace the diesel TE pipeline. Add multi-stage governor, electric brake, etc. |
| `PrimeMover._notchToRpm` / `_notchToPowerPercent` (private fields) | Replace tables. Reflection required for read-only init. |
| `PrimeMover.FuelConsumptionRate` (getter) | Replace fuel-burn formula. Add idle-burn (vanilla returns 0 at notch 0). |
| `PrimeMover.CalculateContinuousTractiveEffort80000` (private static) | Replace the continuous TE decay curve globally. |
| `PrimeMover.actualPowerPercent` slewing (lines 58-59) | Replace spool-up/down rates (currently 0.1/sec up, 0.5/sec down). |

### Gotchas (`PrimeMover`)

- **Idle burns no fuel** (`FuelConsumptionRate` returns 0 if `notch < 1`). Real diesels burn 4-8 gph at idle.
- **`_notchToPowerPercent` and `_notchToRpm` are `readonly` fields** initialized inline. Reflection or runtime mutation via field-write-after-construction is needed to replace them.
- **`reverser` is `int`** (not float). Diesel reverser snaps to -1/0/+1 — set via `Mathf.RoundToInt(Mathf.Lerp(-1f, 1f, value))` in `DieselLocomotive.ConnectBodyControls`. Setting `reverser = 0.5` from a Harmony patch will assign `0` (truncation).
- **`notch = RoundToInt(value * 8)` in adapter setter** is the lossy step. `AbstractThrottle.get` returns `(float)primeMover.notch / 8f` — exact roundtrip only if input was already at a bucket boundary.
- **`actualPowerPercent` is private** — no public way to read the live spool state. Patch a postfix on `CalculateTractiveEffort` to expose it.
- **No fuel-out audio cue**. `PrimeMoverAudioPlayer.UpdatePlayStop` watches `OnHasFuelDidChange` and either plays or stops the entire audio loop. There's no "engine sputtering as fuel runs low" — it just goes silent.
- **`MaxAmps = 900` is unused as a numeric clamp**; only used in `CalculateAmps` as the multiplier for `actualPowerPercent * (TE / startingTE) * 900`. Amps are display-only.
- **Notch transition crossfades in audio** (`PrimeMoverAudioPlayer.PrimeMoverCoroutine`) take precedence over actual TE response. Audio can lag behind TE by up to ~1.25 seconds on multi-notch jumps.

---

## Patch points cheat sheet (steam-mechanics summary)

### Adding a 3- or 4-cylinder loco type

1. Patch `ChuffFilter.OnAudioFilterRead` to use 3 or 4 phase-offsets instead of 4 hardcoded `0.25` increments. **Audio-thread method — keep allocations zero.**
2. Patch `CylinderCockController.SetSmokeEffects` to use 3 or 4 side-smoke effects instead of 2.
3. Patch `SteamEngine.numberOfCylinders` assignment (`SteamLocomotive.FinishSetup:209`) — but this only changes the TE formula; cosmetic effects are independent.
4. Re-cache `SteamEngine.UpdateMaximumTractiveEffort` after `numberOfCylinders` change.

### Adding boiler dynamics (live pressure)

1. Subclass `SteamEngine` or replace `SteamEngine.UpdateMaximumTractiveEffort` to NOT bake `pressure = maximumBoilerPressure`.
2. Add a per-tick pressure update (e.g., `Update` or `FixedUpdate`) that consumes pressure proportional to `WaterConsumptionRate` and rebuilds it from a new "fire intensity" state.
3. Add a `Blower` and `FireIntensity` `ControlPurpose` enum value (recompile `Definition` assembly), or use `ControlPurpose.NotSet` + custom marker.
4. Patch `SteamEngine.MaximumTractiveEffortAtVelocity` to use live `pressure`.

### Adding a per-distance dispatch for diesel

There's no equivalent vanilla path. Best options:

- Subscribe to `Car.OnMovementDidApply` (cross-link [cars-cargo.md](cars-cargo.md)) for per-tick distance.
- Postfix `BaseLocomotive.FixedUpdate` for per-tick all-state.
- Define a custom interface `IDieselLocomotiveSubcomponent`; postfix `DieselLocomotive.DidLoadModels` to discover; create a dispatch loop in a custom hook (e.g., postfix on `Car.PositionWheelBoundsFront`).

### Custom `ControlPurpose`

- **Don't add new enum values** unless willing to recompile Definition assembly. Reuse existing.
- For new control semantics, attach a sidecar `MonoBehaviour` to your `RadialAnimatedControl` that subscribes to `OnValueChanged` independently of `ConfigurePropertyChange`. This bypasses the `ControlPurpose` discovery entirely.
- Or implement `IPickable` directly without going through `ContinuousControl` — see [interaction-controls.md › ContinuousControl substrate](interaction-controls.md#rollingstockcontinuouscontrolscontinuouscontrol-substrate).

---

## Cross-references

### To Locomotive Architecture ([locomotive-architecture.md](locomotive-architecture.md))
- Three-piece subclass bundle: [locomotive-architecture › Subclass spine](locomotive-architecture.md#subclass-spine-the-three-piece-bundle-for-a-locomotive-type).
- `LocomotiveControlAdapter` polymorphism table: [locomotive-architecture › LocomotiveControlAdapter](locomotive-architecture.md#locomotivecontroladapter--the-alternate-loco-type-seam).
- Subcomponent discovery / dispatch / fragility: [locomotive-architecture › ISteamLocomotiveSubcomponent](locomotive-architecture.md#isteamlocomotivesubcomponent--the-steam-dispatch-chain).
- Fuel→compressor→brake-pipe chain: [locomotive-architecture › Compressor / fuel](locomotive-architecture.md#compressor--fuel--hasfuel-plumbing).
- MP authority across loco internals: [locomotive-architecture › MP authority](locomotive-architecture.md#mp-authority-across-loco-internals).

### To Traction ([traction.md](traction.md))
- Steam TE pipeline (`SteamEngine.CalculateTractiveEffort`): [traction › Steam](traction.md#modelsteamlocomotive--steam-subclass).
- `TrainMath.ReverserPowerMultiplier` cutoff curve: [traction › cutoff curve](traction.md#trainmathreverserpowermultiplier-the-cutoff-curve).
- Steam fuel/water polynomial + `CalculateCoalWaterConsumption` dead helper: [traction › Steam fuel/water](traction.md#steam-fuelwater-consumption).
- Diesel notch table + `CalculateContinuousTractiveEffort80000`: [traction › Diesel](traction.md#modeldiesellocomotive--diesel-electric-subclass).
- Out-of-scope confirmation (boiler dynamics, cylinder cocks affecting TE): [traction › Out of scope](traction.md#out-of-scope-explicit-confirmation).

### To Brakes ([brakes.md](brakes.md))
- Bail-off sentinel value -0.1 and `LocomotiveControlHelper.BailOff()`: [brakes › independent brake](brakes.md#locomotive-independent-brake--direct-cylinder-injection).
- Train brake `TrainBrakeMakeSet` + 90 PSI feed valve constant: [brakes › lap pressure](brakes.md#lap-pressure--release-detection).
- Cut-out interlock and `cabControls.cutout` null-safety: [brakes › cut-out](brakes.md#cut-out).

### To Animation Playables ([animation-playables.md](animation-playables.md))
- `PlayableHandle` per-wheel use in `SteamLocomotiveWheelAnimator`: [animation-playables › SteamLocomotiveWheelAnimator](animation-playables.md).
- `RadialAnimatedControl._clipPlayable` lifecycle: [animation-playables](animation-playables.md).

### To Audio ([audio.md](audio.md))
- `Chuff` / `ChuffFilter` audio chain: [audio › chuff](audio.md).
- `WhistlePlayer` / `HornPlayer` polymorphism through `LocomotiveAudio`: [audio › whistle/horn](audio.md).
- `CylinderCockController` audio: [audio › cyl-cocks](audio.md).
- `PrimeMoverAudioPlayer` notch transitions: [audio › prime mover](audio.md).
- `DynamoPlayer` and `FireboxEffectController` event-only subscribers: [audio › dynamo](audio.md).

### To Interaction Controls ([interaction-controls.md](interaction-controls.md))
- `ContinuousControl` substrate (debounce, 1Hz heartbeat, external-write blocking): [interaction-controls › ContinuousControl](interaction-controls.md#rollingstockcontinuouscontrolscontinuouscontrol-substrate).
- `RadialAnimatedControl` angle-vs-sphere mode + `ControlComponentPurpose` field: [interaction-controls](interaction-controls.md).
- Two-player tug-of-war race: [interaction-controls › tug-of-war](interaction-controls.md#two-player-tug-of-war).

### To Trucks & Wheelsets ([trucks-wheelsets.md](trucks-wheelsets.md))
- `SteamLocomotiveDefinition.Wheelsets[]` and the `MainDriverIndex`: [trucks-wheelsets › steam wheelsets](trucks-wheelsets.md).
- Driver-vs-pony slip distinction (`isDriver` flag → `driverVelocity` vs body `velocity`): [trucks-wheelsets › steam slip](trucks-wheelsets.md).

### To VFX & Particles ([vfx-particles.md](vfx-particles.md))
- `SteamChuffParticleController` smoke parameters and the `Puff` coroutine.
- `CylinderCockController` smoke effects and Perlin-modulated rate.
- `DieselExhaustParticleController` driven by `NormalizedExhaustOutputEvent`.

### To Events Catalog ([events-catalog.md](events-catalog.md))
- `PropertyChange.Control` enum values used: `Throttle`, `Reverser`, `LocomotiveBrake`, `TrainBrake`, `CutOut`, `Bell`, `Horn`, `CylinderCock`, `Compressor`, `Idle`, `Mu`, `Headlight`.
- The `KeyForControl(Control)` mapping for each above.

### To Cars & Cargo ([cars-cargo.md](cars-cargo.md))
- `Car.SendPropertyChange` underlying KVO write entry point.
- `Car.PositionWheelBoundsFront` (the steam override calls `SubcomponentsApplyDistanceMoved`).
- Tender-slot `LoadInfo` quantity reads in `SteamLocomotive.PeriodicUpdate`.
