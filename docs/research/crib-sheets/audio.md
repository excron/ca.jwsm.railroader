# Audio — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Couplers](couplers.md), [Time & Weather](time-weather.md)

Railroader's audio is layered. At the bottom is Unity's `AudioSource` plus an `AudioMixer` with named groups (Locomotive/Wheels/Air/etc). On top of that sits a 64-source `AudioSourcePool` and a virtualizing `VirtualAudioSourcePool` that uses `CullingGroup` to checkout/return real sources only when the listener is inside the configured `AudioDistance` band. All in-world audio components (`WhistlePlayer`, `HornPlayer`, `Bell`/`IntegerLoopingPlayer`, `Chuff`/`ChuffFilter`, `PrimeMoverAudioPlayer`, `RollingPlayer`, `WheelAudio`, `DynamoPlayer`, `CylinderCockController`, `Anglecock` air-flow, `Hose` pop, `Coupler` open/close) checkout `IAudioSource` from the pool and animate volume/pitch from gameplay parameters. The only network-replicated audio path is `ScheduledAudioPlayer.HostPlaySoundAtPosition` (clip-name string + Vector3 + group/distance) used for slack-in/out and the CTC bell. Every other sound is computed locally on each client from already-replicated state (KVO/control properties, locomotive notch, velocity).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `AudioController.Group` | `Audio/AudioController.cs:8` | Static catalog of mixer-group paths (Locomotive/*, Wheels/*, Air/*, Coupler/*, CTC, PlayerAction) |
| `AudioLibrary` (`ScriptableObject`) | `Audio/AudioLibrary.cs` | Name → AudioClip + volume map, lookup by string. The catalog `ScheduledAudioPlayer` uses |
| `AudioSourcePool.Checkout/Return` | `Audio/AudioSourcePool.cs:37, 77` | 64-deep pool of real `AudioSource` GameObjects (with HPF/LPF) |
| `VirtualAudioSourcePool.Checkout/Return/ReturnAfterFinished` | `Audio/VirtualAudioSourcePool.cs:45, 56, 68` | Culling-group front-end. Returns `IAudioSource`, real-source allocated only when nearby |
| `IAudioSource` | `Audio/IAudioSource.cs` | Interface every loco-audio component talks to. Abstracts virtual/real distinction |
| `ScheduledAudioPlayer.HostPlaySoundAtPosition` | `Audio/ScheduledAudioPlayer.cs:21` | Network-replicated 3D one-shot. Wraps `PlaySoundAtPosition` `IGameMessage` |
| `ScheduledAudioPlayer.HostPlaySoundNotification` / `PlaySoundLocal` | `Audio/ScheduledAudioPlayer.cs:61, 66` | 2D notification one-shot, host-replicated or local-only |
| `SoundSettingsApplicator` | `Game.Settings/SoundSettingsApplicator.cs` | Maps `Preferences.SoundVolume*` → mixer dB params (`VolMaster`, `VolEngine`, …) |
| `Preferences.SoundVolume*` setters | `Game/Preferences.cs:422-522` | Volume slider properties; each setter `Send`s `SoundVolumeChanged` |
| `AudioReparenter` | `Audio/AudioReparenter.cs` | Detaches audio from parent rigidbody so doppler doesn't tear |
| `CullingManager` (referenced) | `Helpers.Culling` | Distance-band culling for visual+audio components |

---

## Layering spine: how a sound reaches your ears

```
Gameplay parameter change  (notch, throttle, velocity, slack reversal, …)
        │
        ▼
Audio component on car  (PrimeMoverAudioPlayer / WhistlePlayer / Chuff / RollingPlayer / WheelAudio / Coupler / Anglecock / Hose / CylinderCockController …)
        │  needs an audio source
        ▼
VirtualAudioSourcePool.Checkout(name, clip, loop, group, priority, parent, AudioDistance, offset)
        │  returns IAudioSource (VirtualAudioSource)
        │
        │  CullingGroup distance bands:  3 / 50 / 100 / 1000 / Infinity   ← VirtualAudioSourcePool.cs:22
        │  AudioDistance enum (HyperLocal=0, Local=1, Nearby=2, Distant=3) ← Audio/AudioDistance.cs
        │  if distanceBand <= cullDistance → SetNearby(true) → AudioSourcePool.Checkout()
        │
        ▼
AudioSource (real Unity component, hidden under "AudioSourcePool" GameObject)
        │  outputAudioMixerGroup = mixer.FindMatchingGroups(group.Path)[0]
        ▼
AudioMixer (referenced by AudioController.mixer)
        │  Group "Locomotive/Whistle" etc.
        │  Volume params VolMaster / VolEngine / VolEngineWhistle / VolEngineBell / VolDynamo / VolEnvironment / VolCtcBell / VolWheels  (dB)
        │
        │  written by SoundSettingsApplicator from Preferences.SoundVolume* on SoundVolumeChanged Messenger event
        ▼
Hardware
```

**Locality of audio:** all `*Player` components run **on every client**. Steam chuffs, prime-mover notches, whistle pitch, wheel clack, rolling/squeal, brake exhaust hiss — all driven by replicated state (`MovementInfo`, KVO control values, locomotive `Notch`/`HasFuel`/`IsIdle`) but synthesised locally. The host doesn't push waveform events for these.

The only network-replicated samples are the ones routed through `ScheduledAudioPlayer`. Everything else is implicit: if the client has the same KVO state, it produces the same sound.

---

## `Audio.AudioController` and groups

```csharp
public class AudioController : MonoBehaviour {                  // Audio/AudioController.cs
    public AudioMixer mixer;
    public static AudioController Shared { get; }               // FindObjectOfType
    public sealed class Group {
        public readonly string Path;
        public static readonly Group Locomotive          = new("Locomotive");
        public static readonly Group LocomotiveBell      = new("Locomotive/Bell");
        public static readonly Group LocomotiveCylCock   = new("Locomotive/CylCock");
        public static readonly Group LocomotiveChuff     = new("Locomotive/Chuff");
        public static readonly Group LocomotiveWhistle   = new("Locomotive/Whistle");
        public static readonly Group LocomotiveDynamo    = new("Locomotive/Dynamo");
        public static readonly Group LocomotiveCompressor= new("Locomotive/Compressor");
        public static readonly Group Wheels              = new("Wheels");
        public static readonly Group WheelsClack         = new("Wheels/Clack");
        public static readonly Group WheelsRoll          = new("Wheels/Roll");
        public static readonly Group WheelsSqueal        = new("Wheels/Squeal");
        public static readonly Group AirHose             = new("Air/Hose");
        public static readonly Group AirOpen             = new("Air/Open");
        public static readonly Group AirPop              = new("Air/Pop");
        public static readonly Group CouplerCouple       = new("Coupler/Couple");
        public static readonly Group CouplerOpen         = new("Coupler/Open");
        public static readonly Group CTC                 = new("CTC");
        public static readonly Group CTCBell             = new("CTC/Bell");
        public static readonly Group PlayerAction        = new("Player Action");
        public static implicit operator AudioMixerGroup(Group g) => Shared.mixer.Group(g);
    }
}
```

`Group` instances are singleton bookmarks; the implicit conversion looks up the `AudioMixerGroup` on demand via `mixer.FindMatchingGroups(path)[0]` (`AudioMixerExtensions.cs:7`). New custom groups must already exist in the mixer asset; you can't add a new mixer group at runtime cleanly.

Mixer-exposed dB params (set by `SoundSettingsApplicator.UpdateMixer`):

| Param | Preferences source | Default cache |
|---|---|---|
| `VolMaster` | `Preferences.SoundVolumeMain` | captured at `Start` |
| `VolEngine` | `Preferences.SoundVolumeEngine` | captured at `Start` |
| `VolEngineBell` | `Preferences.SoundVolumeBell` | … |
| `VolEngineWhistle` | `Preferences.SoundVolumeWhistle` | |
| `VolDynamo` | `Preferences.SoundVolumeDynamo` | |
| `VolEnvironment` | `Preferences.SoundVolumeEnvironment` | |
| `VolCtcBell` | `Preferences.SoundVolumeCtcBell` | |
| `VolWheels` | `Preferences.SoundVolumeWheels` | |

`SoundSettingsApplicator.Start` reads each param's *current* dB, converts to a normalised linear factor, and caches it as the per-channel maximum. Subsequent updates compute `Preferences.SoundVolume* * cachedDefault` and write back. So changing a param in the mixer asset before play sets the upper bound; the slider scales 0..1× of that.

```csharp
private static float AudioNormToDb(float f) => Mathf.Log10(Mathf.Max(f, 0.0001f)) * 20f;
```

### Patch candidates (mixer/groups/settings)

| Method | Why patch |
|---|---|
| `AudioMixerExtensions.Group(this AudioMixer, AudioController.Group)` | Replace mixer-group lookup (e.g., remap a vanilla group to a custom one). |
| `SoundSettingsApplicator.UpdateMixer` | Add new mixer params, override volume mapping, or insert mod-side ducking. |
| `AudioController.Group` ctor | Internal — to add a new named group, add it to the mixer asset and `new Group("Path")` in your mod; the implicit conversion will resolve it. |

---

## `Audio.AudioSourcePool` — real-source pool

64 deep, root parent `AudioSourcePool` (`hideFlags = DontSave`). Every checkout returns a `GameObject` named `AS{NN}` carrying an `AudioSource` plus `AudioHighPassFilter` and `AudioLowPassFilter` (both initially disabled).

```csharp
public static AudioSource Checkout(AudioClip clip, bool loop, AudioController.Group mixerGroup, int priority,
                                   Transform parent, Vector3 parentOffset);  // 37
public static void Return(AudioSource audioSource);                          // 77
private static AudioSource CreateAudioSource();                              // 99 — unbounded; pool is "min 64"
```

`Return` discards if pool is at 64 (`Object.Destroy`). New sources are created when the pool is empty (no fixed cap on creation). `Checkout` calls `ApplyBase3DSettings` (`AudioSourceExtensions.cs:7`): `spatialBlend=1`, `Logarithmic`, `min=20`, `max=100`. Most callers override min/max afterwards.

### Gotcha: filter persistence

`AudioSourcePool.Return` sets `AudioHighPassFilter.enabled = false` and `AudioLowPassFilter.enabled = false`, but the `cutoffFrequency` from the previous checkout remains. Re-enabling without setting cutoff will reuse stale state. `VirtualAudioSource.SetHighPassCutoff/SetLowPassCutoff` always set both `enabled=true` and the cutoff, so this is only a hazard for direct-pool consumers.

---

## `Audio.VirtualAudioSourcePool` and `VirtualAudioSource` — culling front-end

Singleton MonoBehaviour. Holds a list of live virtual sources; uses Unity `CullingGroup` with five distance bands to decide which deserve a real `AudioSource`.

```csharp
private static readonly float[] DistanceBands = { 3f, 50f, 100f, 1000f, float.MaxValue };  // VirtualAudioSourcePool.cs:22
```

| Index | Range | Maps to AudioDistance |
|---|---|---|
| 0 | < 3 m | `HyperLocal` (also gets minDistance=0.5, maxDistance=3 in `ScheduledAudioPlayer`) |
| 1 | 3 .. 50 m | `Local` |
| 2 | 50 .. 100 m | `Nearby` |
| 3 | 100 .. 1000 m | `Distant` |
| 4 | > 1000 m | (always culled) |

```csharp
public enum AudioDistance { HyperLocal, Local, Nearby, Distant }   // Audio/AudioDistance.cs
```

A `VirtualAudioSource` is "nearby" iff `distanceBand <= (int)cullDistance`. When it transitions to nearby, `SetNearby(true)` checks out a real `AudioSource` and applies cached state (volume, pitch, min/max, rolloff curve, spatialBlend, schedule). When transitioning out, the real source is returned to the pool but volume/play-state are preserved on the virtual.

```csharp
public static IAudioSource Checkout(string name, AudioClip clip, bool loop,
    AudioController.Group mixerGroup, int priority, Transform parent,
    AudioDistance cullDistance, Vector3 offset = default);
public static void Return(IAudioSource);                  // immediate
public static void ReturnAfterFinished(IAudioSource);     // FixedUpdate-checked, returns when !isPlaying
public static void SetGlobalDopplerLevel(float value);    // multiplied per-source
```

Doppler is `_dopplerLevel * VirtualAudioSourcePool.GlobalDopplerLevel` per source. `RollingPlayer`/`WheelAudio` use 0.1..0.25; `PrimeMoverAudioPlayer` defaults to 1.

### `AudioReparenter` opt-out for rigidbodies

```csharp
public class AudioReparenter : MonoBehaviour {                    // Audio/AudioReparenter.cs
    public Transform Reparent(Transform originalParent, out Vector3 offset);
}
```

`VirtualAudioSourcePool.ActualCheckout` does `parent.GetComponentInParent<AudioReparenter>()`; if found, it re-parents to a kinematic rigidbody child instead of the original parent, and stores the original-position offset. Used to keep audio attached to a stable Transform when the original moves on `Rigidbody.position` (rough physics tearing in doppler-sampled audio).

### Patch candidates (pooling)

| Method | Why patch |
|---|---|
| `VirtualAudioSourcePool.ActualCheckout` | Pre-process every audio request (volume cap, group remap, mute by name). |
| `VirtualAudioSourcePool.SetGlobalDopplerLevel` | Override doppler globally (already used by Preferences in some builds — verify). |
| `AudioSourcePool.Checkout/Return` | Last resort if you must hook the underlying real source — most consumers go through Virtual. |
| `AudioSourceExtensions.ApplyBase3DSettings` | Change the default 3D rolloff (Logarithmic 20..100). |

### Gotcha: pool starvation by leaks

If a consumer forgets `VirtualAudioSourcePool.Return`, the virtual entry persists with `parentTransform=null` after destroy; `UpdateCullingSpheres` logs `"Found null parentTransform for clip suggesting source was not correctly returned"` and force-returns it. So leaks self-heal at the next FixedUpdate, but the warning spam is the canary. The 64-real-source cap means leaks degrade to "no audio for distant new sounds" before crashing.

---

## `ScheduledAudioPlayer` — the only networked audio

Two messages, both `[HostOnlyAuthorizationRule]`:

```csharp
[HostOnlyAuthorizationRule] public struct PlaySoundAtPosition : IGameMessage {  // Game.Messages/PlaySoundAtPosition.cs
    public long Tick; public string Name; public Vector3 Position;
    public float Volume; public float Pitch; public int Distance;
    public string GroupPath; public int Priority;
}
[HostOnlyAuthorizationRule] public struct PlaySoundNotification : IGameMessage { // Game.Messages/PlaySoundNotification.cs
    public string Name; public float Volume; public float Pitch;
}
```

API:

```csharp
public static void HostPlaySoundAtPosition(string soundName, Vector3 gamePosition,
    AudioDistance distance, AudioController.Group group, int priority,
    float volume = 1f, float pitch = 1f);                                 // 21
public static void HostPlaySoundNotification(string soundName, float v=1, float p=1);// 61
public static void PlaySoundLocal(string soundName, float v=1, float p=1);          // 66 — bypasses StateManager.ApplyLocal
public  void HandlePlaySound(PlaySoundAtPosition play);                              // 26
public  void HandlePlaySound(PlaySoundNotification play);                            // 79
```

`HostPlaySoundAtPosition` wraps `StateManager.ApplyLocal(new PlaySoundAtPosition(StateManager.Now, …))`. The `Tick` lets clients schedule playback aligned to network time:

```csharp
long toTick = play.Tick + (Multiplayer.IsHost ? 0 : 300);                     // ScheduledAudioPlayer.cs:28
float delaySeconds = Mathf.Clamp(NetworkTime.Elapsed(Now, toTick), 0f, 5f);   // 29
```

Clients delay by 300 ms beyond host-tick (matches `NetworkTime.TrainDelay`) so the sound lands when the replicated visual state arrives. Clamped 0..5 s to swallow misordering.

Sound resolution:

1. `AudioLibrary.TryGetEntry(play.Name, out var entry)` — string lookup. If missing, **throws** (uncaught in `HandlePlaySound`, caught only inside `PlaySoundCoroutine` for notification).
2. Group lookup is by **path string** (`AudioController.Group(play.GroupPath)`). Custom groups will resolve as long as they exist in the mixer.
3. `WorldTransformer.GameToWorld(play.Position)` projects scaled-game-coord into Unity world.
4. Checkout via `VirtualAudioSourcePool` parented to `TrainController.Shared.transform`, offset = inverse-transform of world position. **Sound moves with `TrainController` if it moves** (it doesn't, in normal play).
5. `audioSource.spatialBlend = 1` always (3D), even for notifications inside `PlaySoundCoroutine` — no, wait: `PlaySoundCoroutine` for `PlaySoundNotification` sets `spatialBlend = 0` (2D, head-locked).

### Notification dedupe

`HandlePlaySound(PlaySoundNotification)` keeps a `Dictionary<string, Coroutine> _playingNotificationSounds`. While a notification with that name is playing, additional triggers are silently dropped (`Log.Debug("PlaySound: Already playing {sound}")`). The coroutine waits `clip.length - 0.1s` (realtime, ignores time scale) before clearing the entry.

### `PlaySoundLocal` escape hatch

```csharp
public static void PlaySoundLocal(string soundName, float volume = 1f, float pitch = 1f) {
    try {
        var play = new PlaySoundNotification(soundName, volume, pitch);
        StateManager.Shared.AudioPlayer.HandlePlaySound(play);          // direct, no network
    } catch (Exception ex) { Log.Error(ex, "Exception playing sound {soundName} locally", soundName); }
}
```

Bypasses `StateManager.ApplyLocal` — fires the notification on this machine only. **The only call site is `Game.Notices/NoticeManager.cs:93` (`telegraph-ditdit`).** Useful for client-side mod sounds that shouldn't replicate.

### Vanilla call sites for replicated audio

| Site | Sound | Group | Distance |
|---|---|---|---|
| `TrainController.RequestSlackSound` (`TrainController.cs:1239`) | `slack-in` / `slack-out` | `WheelsRoll` | `Local` |
| `Track.Signals/CTCPanelController.cs:172` | `ctc-bell` | `CTCBell` | `HyperLocal` |
| `StateManager.cs:1288` (ledger fire) | `punch` / `stamp` | (notification) | n/a |
| `Game.Notices/NoticeManager.cs:93` | `telegraph-ditdit` | (notification, local-only) | n/a |

That's the entire vanilla networked-audio surface: four call sites.

### Patch candidates (ScheduledAudioPlayer)

| Method | Why patch |
|---|---|
| `ScheduledAudioPlayer.HostPlaySoundAtPosition` | Single chokepoint for *all* networked positional audio. Prefix to drop, postfix to log. |
| `ScheduledAudioPlayer.HandlePlaySound(PlaySoundAtPosition)` | Per-machine receive; patch to apply mod-side filters before scheduling. |
| `ScheduledAudioPlayer.PlaySoundCoroutine` | Direct control of the resulting `IAudioSource` (high/low-pass, doppler etc.). |
| `AudioLibrary.TryGetEntry` | Add mod sounds without serializing into the vanilla `AudioLibrary` ScriptableObject (postfix returning a synthetic `Entry`). |

### Gotcha: missing-clip throws on positional path

`ScheduledAudioPlayer.PlaySoundAtPosition` (the coroutine) throws `Exception("No such sound in library: " + name)` on missing entries, and the caller `HandlePlaySound` does **not** wrap. The exception will propagate up Unity's coroutine machinery as an uncaught error. Patching `AudioLibrary.TryGetEntry` to add fallbacks is safer than patching the coroutine.

### MP authority

- Both message types are `[HostOnlyAuthorizationRule]`. Clients calling `HostPlaySoundAtPosition` will get a permission rejection at `StateManager.ApplyLocal`. There is **no client-driven request channel** for replicated sounds.
- Mod servers can still `HostPlaySoundAtPosition` from any host-side code; the wrapping in `ApplyLocal` ensures replication.
- Local-only path (`PlaySoundLocal`) bypasses auth entirely.

---

## Per-component locomotive audio

All of these are MonoBehaviours that `Awake`/`OnEnable`-checkout a `IAudioSource` from `VirtualAudioSourcePool`, then drive volume/pitch/cutoff in `Update` from gameplay parameters. They run on every client and require no host coordination — replicated state (`MovementInfo`, KVO controls, `BaseLocomotive.Notch`, `BaseLocomotive.HasFuel`/`IsIdle`) is the input.

### `Audio.WhistlePlayer` (`Audio/WhistlePlayer.cs`) — steam whistle

```csharp
public WhistleProfile profile;
[Range(0,1)] public float parameter;
private void Configure(AudioClip clip);             // 65, swaps in a Loopify()-d clip
```

- Loops the `WhistleProfile.audioClip` (passed through `AudioUtilities.Loopify` for seamless loop).
- Smooths `parameter` through two filters (`lerpSpeed`, `airLerpSpeed`) to model air mass inertia, asymmetric (cuts in faster than out: `0.4×` lerp speed when releasing).
- `pitch = lerp(rampUpPitch, 1, airSpeed) * profile.parameterToPitch.Evaluate(airSpeed)`.
- `volume = profile.parameterToVolume.Evaluate(airSpeed)`, lerped to source at 100/sec.
- Group `LocomotiveWhistle`, priority `10`, `AudioDistance.Distant`, min 50 m, max 1000 m.

### `Audio.HornPlayer` (`Audio/HornPlayer.cs`) — diesel horn

Two-layer crossfade architecture (`HornProfile.layers[0]`/`[1]`). `value` field 0..1 maps to per-layer `volumeCurve` (mix horn body and bell). `flow` (air pressure) lerps in/out with `Time.deltaTime * 20f`. Same group/distance/priority as whistle.

### `Audio.PrimeMoverAudioPlayer` (`Audio/PrimeMoverAudioPlayer.cs`) — diesel prime mover

State-machine notch player with notch-up and notch-down transitions:

```csharp
public PrimeMoverAudioProfile profile;       // notchLoops[9] + transitionsUp[8]/transitionsDown[8]
public Action<float> NormalizedExhaustOutputEvent { get; set; }   // 0..1 for VFX
public int Notch { get; set; }
```

`PrimeMoverCoroutine` plays `notchLoops[Notch]` looping, and on `Notch` change crossfades through the matching `transitionsUp`/`transitionsDown` clip (if present) before swapping into the new notch loop. Subscribes to `BaseLocomotive.OnHasFuelDidChange` to start/stop. Subscribers to `NormalizedExhaustOutputEvent` (e.g., particle smoke) get `Mathf.InverseLerp(0, 8, notch)` whenever `SetExhaust` runs.

### `RollingStock.Bell` + `Audio.IntegerLoopingPlayer` — locomotive bell

Bell is "is it ringing" → `IntegerLoopingPlayer.play`. The looping player chains overlapping `IndexedClipDescriptor` segments using `AudioClip.Split` on `clip.indexes[]`, then plays them with `PlayScheduled` on a 2-source ping-pong. `randomize` chooses random inner segments; otherwise rotates. Group `Locomotive` (overridden to `LocomotiveBell` by `BellComponentBuilder`). The prefab variant is `bell-diesel` or `bell-steam` based on `Car.CarType == "LD"` (`BellComponentBuilder.cs:22`).

### `RollingStock.Chuff` + `Audio.DynamicChuff` — steam chuff

Chuff is two-stage:
1. `Chuff` (`RollingStock/Chuff.cs`) — `ApplyDistanceMoved` from steam locomotive subcomponent dispatch; computes engine speed (`absVelocity / driverCircumference`) and tractive effort, feeds `ChuffFilter`.
2. `RollingStock.Steam.ChuffFilter` (`ChuffFilter.cs`) — `OnAudioFilterRead` synthesizes the chuff envelope per-sample using a curve, multiplied against an actual audio source's data. Per-sample pitch/curvature/throttle modulation via `_parameterLock` lock-protected `Parameters` struct (audio thread reads).
3. `ChuffProfile` + `ChuffFilterProfile` curves: `attackTime`, `fullCutoffMultiplier`, `maximumChuffDuration`, `throttleToVolumeCurve`, `sizeToLowPass/HighPassCutoff`, `sizeToVolumeCurve`, `lowPassModulation`, `highPassOffsetForSpeed`, etc.
4. Audio source group is `LocomotiveChuff` (set in `Chuff.PrepareAudioSource`).

`Audio.DynamicChuff/LoopBuilder` and `SmbPitchShifter` exist as alternative chuff synthesis (FFT phase-vocoder pitch shift, `BuildLoop` / `BuildLoopBuffer` / `BuildSingles`). `IDynamicChuffDelegate.ScheduleNextChuff(float delay, float chuffDuration)` is the sub-0.1s scheduling API the `Chuff` component invokes when speed is low.

### `Audio.DynamoPlayer` (`Audio/DynamoPlayer.cs`) — steam dynamo whine

Loops `clip` at group `LocomotiveDynamo`, priority `11` (`AudioSourcePriorities.AuxiliarySteam`), `Local` distance, min 3 m. Subscribes to `BaseLocomotive.OnIdleDidChange` and `OnHasFuelDidChange`; fades in at 3 s when running, fades out otherwise. Random pitch jitter `0.99..1.0` per session.

### `Effects.CylinderCockController` — steam cylinder cocks

Two `IAudioSource` (left/right of driver), group `LocomotiveCylCock`, distance `Nearby`. State driven by `KeyValueObject` observer on `PropertyChange.Control.CylinderCock` — control property, not a top-level KVO key. Spawns smoke `VisualEffect` in lockstep. `_steam` accumulator decays when open, builds when closed (60 s recovery).

### `Audio.RollingPlayer` + `RollingProfile` — wheel roll & flange squeal

Two coroutines per `Car`: `RunRolling` (continuous roll) and `RunSqueal` (curve squeal). Roll uses `ParametricAudioComposition.Track` array indexed by velocity-mph; the active track is the one with highest `volumeCurve.Evaluate(velocity)`, crossfaded at 1 s when the track switches. Squeal triggers when `car.CurrentTrackCurvature > 2.2` and modulates by `TrainMath.MaximumSpeedMphForCurve`. Groups `WheelsRoll` / `WheelsSqueal`, priority `30` (Rolling), `AudioDistance.Nearby`.

`RollingPlayer._movingThreshold` is auto-derived from `profile.mphToVolume` — search upward for the first velocity where volume ≥ 0.01 → that × 0.4470 m/s. So if you patch the curve to start at 0 mph, the threshold drops to 0.

### `RollingStock.WheelAudio` + `WheelClackProfile` — joint clack

Per-axle clack sources at `_clackOffsets[i]` along the car's local Z. `RollClack` walks an odometer (`_clackOdometer`) modulo `jointDistance` (default 24 m); when an axle crosses the joint window (`[0.1d, 0.9d]`), plays the clack clip with low-pass cutoff `velocityMphToLowPassCutoff.Evaluate(mph) + Random.Range(lowPassNoiseMagnitude, 0)` and volume `velocityMphToVolume × volumeEnvelope(time × velocityMphToDuration)`. Group `WheelsClack`, priority `20`, `AudioDistance.Local`. Off when `!Car.IsNearby` (saves CPU on far cars).

### `Model/Car.cs` brake exhaust — air-brake hiss

```csharp
private void UpdateBrakeExhaust() {                                   // Car.cs:2494
    if (air.exhaustFlow > 0.1f) {
        if (_brakeExhaustAudioSource == null) {
            _brakeExhaustAudioSource = VirtualAudioSourcePool.Checkout("BrakeExhaust",
                _airFlowAudioClip, loop: true, AudioController.Group.AirHose, 11,
                BodyTransform, AudioDistance.Local);
            …
        }
        _brakeExhaustAudioSource.volume = Mathf.InverseLerp(0f, 100f, air.exhaustFlow);
    }
    else if (_brakeExhaustAudioSource != null) {
        VirtualAudioSourcePool.Return(_brakeExhaustAudioSource);
        _brakeExhaustAudioSource = null;
    }
}
```

Single audio source per car, driven by `air.exhaustFlow`. Group `AirHose`, distance `Local`, min 2 m / max 20 m. **Per-car continuous brake hiss is a function of the car's air model — not from a mod-replicated event**. Modify the curve via `UpdateBrakeExhaust` patch or change `_airFlowAudioClip`.

### `RollingStock.Anglecock` — gladhand air-flow hiss

Identical pattern (loop source, group `AirOpen`, priority `11`, distance `Nearby`). Created/destroyed when `Flow > 0.1 && !IsConnected`. Volume `InverseLerp(0, 100, Flow)`.

### `RollingStock.Hose` — hose pop / connect / disconnect one-shots

`PlayPop(intensity)` plays a random `popClips[i]` and `disconnectClips[j]` together; `PlayConnect` plays a `connectClips[k]`. Group `AirPop`, priority `10`, distance `Local`. `ReturnAfterFinished` for cleanup. Pitch jittered `0.8..1.2`.

### `RollingStock.Coupler` slack one-shots — DEAD CODE

```csharp
public void SlackIn(float slackDiffNormalized);   // 100 — calls PlayOneShot(slackInClip, …)
public void SlackOut(float slackDiffNormalized);  // 105
```

These are vestigial. Vanilla slack audio runs through `TrainController.RequestSlackSound` → `ScheduledAudioPlayer.HostPlaySoundAtPosition("slack-in" / "slack-out")` on the host, replicated to clients. `Coupler.SlackIn`/`SlackOut` are not invoked from any vanilla path. See [Couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline) and `Coupler.SetOpen` for the sounds that **are** played from `Coupler` (open/close on KVO change).

`Coupler.SetOpen` plays `audioClipOpen` or `audioClipClose` only when:
- `_isOpen` previously had a value (skip first-call), AND
- `car.EndGearA.Coupler == this` (only the A-end coupler plays — avoids double-sound on coupled pairs), AND
- `gameObject.activeSelf`.

Group switches between `CouplerOpen` and `CouplerCouple`. So the open/close click is local-on-receive (driven by KVO observer firing on every client), no network message needed — both clients hear it because both saw the state change.

### Patch candidates (per-component)

| Method | Why patch |
|---|---|
| `WhistlePlayer.Configure(AudioClip)` | **Custom whistle replacement** — already used by `WhistleController.Configure(WhistleCustomizationSettings)` (`RollingStock.Steam/WhistleController.cs:79`). Mods can mint a `WhistleCustomizationSettings` and write `whistle.custom` KVO. |
| `WhistleController.Configure(WhistleCustomizationSettings)` | Patch to load custom whistle assets from mod prefab stores. The vanilla flow loads `WhistleDefinition.Audio` via `IPrefabStore.LoadAssetAsync`. |
| `HornPlayer.OnEnable` | Replace `profile.layers[*]` with custom clips. There is **no `HornController` analog to `WhistleController`** for runtime customization — horn audio is a serialized `HornProfile` per prefab. |
| `BellComponentBuilder._Build` | Pre-build, swap which prefab loads (`bell-diesel` vs `bell-steam`). |
| `Bell` / `IntegerLoopingPlayer.PrepareClips` | Replace `indexedClip.clip` with a custom `IndexedClipDescriptor`. |
| `PrimeMoverAudioPlayer.profile` | `PrimeMoverAudioProfile` is a `ScriptableObject` — replace at runtime to swap notch loops/transitions. |
| `Chuff.profile` / `ChuffFilterProfile` | Tuning surface for steam exhaust character. |
| `Car.UpdateBrakeExhaust` | Change brake hiss volume curve, swap clip, gate on conditions. |
| `Coupler.SetOpen` | Hook coupler-open/close audio (open/close only — not slack). |
| `RollingPlayer.profile` | Replace per-class wheel-roll composition. |
| `WheelAudio.Configure(profile, wheels, car)` | Per-instance custom clack profile (called from car setup). |

---

## `Helpers.AudioUtilities`

Static utility for clip math:

```csharp
public static AudioClip[] Split(this AudioClip clip, float[] times, int fadeEndSamples);  // 8
public static AudioClip   Loopify(this AudioClip clip);                                    // 54
public static void Crossfade(out float a, out float b, float t);                           // 78
```

`Split` slices a clip at `times[]` (in seconds) into `times.Length + 1` clips, fading each end with a linear ramp over `fadeEndSamples` samples. `IntegerLoopingPlayer.PrepareClips` is the primary consumer.

`Loopify` builds a seamless 50%-overlap loop by crossfading the back half over the front half (8192-sample window). Used by `WhistlePlayer.Configure`.

---

## `Game.AudioSourcePriorities` — priority values

```csharp
public const int PlayerAction      = 5;   // PlaySoundNotification
public const int Whistle           = 10;
public const int Bell              = 10;
public const int Chuff             = 10;
public const int AuxiliarySteam    = 11;
public const int Clack             = 20;
public const int Rolling           = 30;
```

Lower number = higher priority (Unity convention). Used as the `priority` int passed to `Checkout`. Anything ≥ 30 is at risk of culling on busy scenes (Unity caps live audio voices at 32 by default).

---

## Multiplayer summary

| Audio path | Replicated? | Authority |
|---|---|---|
| Whistle / horn / bell / chuff / prime mover / dynamo / cyl cocks / rolling / clack / squeal / brake hiss / anglecock hiss / hose pop | **Local only** — synthesised on each client from already-replicated state | n/a |
| Coupler open/close one-shot (`Coupler.SetOpen`) | Local on each client; both observe the KVO change → both hear | n/a |
| Slack-in/out | **Replicated** — `RequestSlackSound` (host-only) → `HostPlaySoundAtPosition` → `PlaySoundAtPosition` IGameMessage | Host |
| CTC bell | **Replicated** — same channel | Host |
| Notification sounds (`punch`, `stamp`) | **Replicated** — `PlaySoundNotification` IGameMessage | Host |
| `telegraph-ditdit` notification | **Local only** (`PlaySoundLocal`) | n/a |

**Implication for mods:** if you add a sound that needs to fire when state changes, choose:
1. State change is replicated (KVO key, control property, locomotive notch) → just react locally on every client. Cheapest, no auth burden.
2. State change is host-only-evaluated (a derived condition like slack reversal) → `HostPlaySoundAtPosition` from host, all clients hear with 300 ms delay.
3. Strictly local UI/feedback → `PlaySoundLocal`.

There is no client-side request equivalent of `RequestSlackSound`. Custom client-driven networked audio requires defining a new `IGameMessage`.

---

## Custom-sound integration patterns

### Add a custom sound to `AudioLibrary`

The `AudioLibrary` is a `ScriptableObject` with `List<Entry>`. The vanilla library is referenced by `ScheduledAudioPlayer.audioLibrary` (serialized). Two options:

1. **Patch `AudioLibrary.TryGetEntry`** (postfix) — return synthetic `Entry` for mod sound names. Cleanest, no asset modification.
2. **Mutate the live `entries` list** — `StateManager.Shared.AudioPlayer.audioLibrary.entries.Add(new AudioLibrary.Entry { name="my-sound", clip=clip, volumeMultiplier=1f })`. Survives until library is reloaded; safe for runtime mod inits.

### Replace a locomotive's prime mover audio

`PrimeMoverAudioPlayer.profile` is a public field. Find the player on a `BaseLocomotive` (via `GetComponentInChildren`) and assign a new `PrimeMoverAudioProfile` ScriptableObject. The coroutine reads `profile.notchLoops[Notch]` each iteration so the change takes effect on the next notch transition (or you can `StopPlaying` / `StartPlaying`).

### Replace a steam locomotive's whistle

The vanilla machinery already exists. Write to KVO key `whistle.custom` (object key per-locomotive) with a `Value.Dictionary` containing `"identifier" -> "your-whistle-id"`. `WhistleController.Configure` observes the key and re-loads the model + `AudioClip` from `IPrefabStore` for that identifier. See `WhistleCustomizationSettings.PropertyValue` for the on-wire format.

### Replace chuff audio

`Chuff.profile` (ChuffProfile) and `ChuffFilter.profile` (ChuffFilterProfile) are serialized fields. Direct reflection assignment works. The chuff is *synthesized* via `OnAudioFilterRead` — there's no source clip you can swap for the chuff itself; instead you tune `attackTime`, `fullCutoffMultiplier`, `maximumChuffDuration`, the amplitude curve, and the high/low-pass profiles. The dynamic-chuff alternative path (`Audio.DynamicChuff/LoopBuilder`) is unused in the vanilla `Chuff.cs` flow but available for mod consumption.

### Add custom mixer groups

Add to the `AudioMixer` asset (separate from this codebase). At runtime, `new AudioController.Group("YourPath")` produces a routable `Group` that resolves via `mixer.FindMatchingGroups(path)[0]`. If the path doesn't exist, `[0]` throws `IndexOutOfRangeException`.

### Per-loco engine swap (custom prime mover sample set)

There's no first-class hook. You'd need to:
1. Find the `PrimeMoverAudioPlayer` on the loco prefab at instantiation (e.g., patch `PrimeMoverAudioPlayer.OnEnable` postfix or `BaseLocomotive.Awake`).
2. Identify the locomotive (model id, `Car.CarType`, KVO key).
3. Assign your `PrimeMoverAudioProfile` containing your `notchLoops[]` and `transitionsUp/Down[]`.
4. Optionally subscribe to a custom KVO key for per-instance customization (mirror `WhistleCustomizationSettings`).

---

## Cross-references

- Slack-sound networked path: see [Couplers › collision damage pipeline](couplers.md#collision--coupling-damage-pipeline) and [Couplers › slack & integration](couplers.md#slack-state--integration).
- Coupler open/close audio (local): see [Couplers › `RollingStock.Coupler`](couplers.md#rollingstockcoupler--the-visual-layer). `Coupler.SlackIn`/`SlackOut` are **dead code** — see the gotchas there.
- Brake hiss origin (`air.exhaustFlow`): brake-system survey not yet written; for now see `Model/Car.cs:2494` (`UpdateBrakeExhaust`) and `Model/Air.cs` for the pneumatics that drive `exhaustFlow`.
- Engine notch / chuff effort source: traction-system survey not yet written; for now see `RollingStock/Chuff.cs:93` (`ApplyDistanceMoved`) and `Audio/PrimeMoverAudioPlayer.cs:25` (`Notch`).
- Time-of-day-driven audio: there is none in vanilla. Day/night gates VFX (`ClockDriver` / `ClockDrivenVisualEffect`) and headlight intensity (`HeadlightController.SunLevel`), but no audio component subscribes to `TimeWeather` events. See [Time & Weather](time-weather.md).
