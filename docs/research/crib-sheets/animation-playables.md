# Animation & PlayableGraph — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/AssetPack.Common/`)
**Companions:** [Interaction & Continuous Controls](interaction-controls.md), [Brakes](brakes.md), [Couplers](couplers.md), [Cars & Cargo](cars-cargo.md), [Asset Packs](asset-packs.md), [Car Definitions](car-definitions.md)

Railroader does **not** use Unity's built-in `Animator` state machine. Every prefab-driven animation in vanilla — twist valves, brake stands, throttle handles, brake-shoe shoes, switch stands, semaphore arms, firebox doors, bell pendulums, car-load fill levels, steam-loco wheels — funnels through a tiny in-house wrapper around Unity's `PlayableGraph`. The wrapper is a single MonoBehaviour, `Helpers.Animation.PlayableGraphAnimatorAdapter` (the "graph adapter"), one shared `AnimationLayerMixerPlayable` per Animator, and a `PlayableHandle` (per-clip lifecycle/IDisposable) that consumers retain. An extension method `Animator.PlayableGraphAdapter()` lazy-adds the adapter to any `Animator`. Authoring expectation: ship a `BodyTransform`-rooted GameObject with an `AssetPack.Common.AnimationMap` MonoBehaviour holding `(string name → AnimationClip)` entries, plus an `Animator` (or rely on the auto-`AddComponent<Animator>` path); the C# code resolves names through the map at load time. **There is no replication of animation state across the network** — every machine animates from local KVO state, which is replicated. The PlayableGraph itself is a Unity API famous for footguns (must `Destroy()`, must connect outputs, mixers leak, time stops at game pause if `DirectorUpdateMode.GameTime`). The adapter handles a few of these but exposes others to the consumer; this sheet enumerates them.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Helpers.Animation.PlayableGraphAnimatorAdapter` | `Helpers.Animation/PlayableGraphAnimatorAdapter.cs` | The per-Animator graph wrapper. Owns one `PlayableGraph` + `AnimationLayerMixerPlayable`; vends ports |
| `Helpers.Animation.PlayableHandle` | `Helpers.Animation/PlayableHandle.cs` | Per-clip handle: `Time`, `Speed`, `Play()`, `Pause()`, `ClampTimeToClipBounds()`, `Dispose()` |
| `Helpers.Animation.AnimatorExtensionForPlayableGraph.PlayableGraphAdapter()` | `Helpers.Animation/AnimatorExtensionForPlayableGraph.cs` | Extension on `Animator` — lazy-`AddComponent` the adapter |
| `Helpers.Animation.AnimationClipChecker.CheckAnimationClip()` | `Helpers.Animation/AnimationClipChecker.cs` | Null-clip guard with bug — see Gotchas |
| `AssetPack.Common.AnimationMap` (MonoBehaviour) | `AssetPack.Common/AnimationMap.cs` | `(name → AnimationClip)` lookup; lives on body prefab |
| `Model.AssetMapExtensions.Resolve(...)` | `Model/AssetMapExtensions.cs` | `AnimationMap.Resolve(AnimationReference)` → `AnimationClip` |
| `Car.SetupForAnimation()` | `Model/Car.cs:1489` | Returns `(Animator, AnimationMap)` from the body; auto-adds `Animator` if missing |
| `Car.SetupBrakeAnimations()` | `Model/Car.cs:1473` | Wires `Definition.BrakeAnimations` → `BrakeAnimator.brakeAnimationClips` |
| `RollingStock.BrakeAnimator` (impls `IBrakeAnimator`) | `RollingStock/BrakeAnimator.cs` | Per-truck shoe animation, debounced apply/release |
| `RollingStock.Wheelset` (impls `IBrakeAnimator`) | `RollingStock/Wheelset.cs` | Per-truck wheel rotation + brake-shoe playable |

---

## Spine: how a PlayableGraph is built and consumed

```
Body prefab loaded from AssetBundle
   │  • Has child GameObject with AssetPack.Common.AnimationMap
   │    listing { name → AnimationClip } pairs
   │
   ▼ Car.SetupForAnimation()                 (Car.cs:1489)
   │  • BodyTransform.GetComponentInChildren<AnimationMap>()
   │  • component.GetComponent<Animator>() OR
   │    componentInChildren.gameObject.AddComponent<Animator>()
   │       └ animator.cullingMode = AnimatorCullingMode.CullCompletely
   │  • returns (Animator, AnimationMap) tuple
   │
   ▼ Consumer (BrakeAnimator / RadialAnimatedControl / Bell / etc.)
   │  animator.PlayableGraphAdapter()        (extension)
   │       └ adapter = animator.GetComponent<PlayableGraphAnimatorAdapter>()
   │              ?? animator.gameObject.AddComponent<PlayableGraphAnimatorAdapter>()
   │
   ▼ Adapter.PrepareGraphIfNeeded()          (lazy, called from Awake AND each AddPlayable)
   │  if (!_graph.IsValid()) {
   │     _graph = PlayableGraph.Create(name);
   │     _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);     // ← respects Time.timeScale
   │     _graph.Play();
   │     output = AnimationPlayableOutput.Create(_graph, "Animator", animator);
   │     _mixer = AnimationLayerMixerPlayable.Create(_graph);
   │     output.SetSourcePlayable(_mixer);
   │  }
   │
   ▼ Adapter.AddPlayable(clip) → PlayableHandle
   │  • playable = AnimationClipPlayable.Create(_graph, clip)
   │  • playable.Pause()                                            // ← starts paused
   │  • port = _availablePorts[0] (recycled) OR _mixer.AddInput(...)
   │  • _mixer.ConnectInput(port, playable, 0)                      // weight = 1f via AddInput
   │  • returns new PlayableHandle(adapter, port, playable)
   │
   ▼ Consumer drives it
   │  handle.Time = t                — sets clip time
   │  handle.Speed = +1/-1/0         — direction & rate
   │  handle.Play() / Pause()
   │  handle.ClampTimeToClipBounds() — defensive clamp to [0, clip.length]
   │
   ▼ Per-frame: PlayableGraph ticks under Unity's Default Game playable system
   │  Unity advances all graphs in DirectorUpdateMode.GameTime each frame
   │  AnimationPlayableOutput pushes joint poses to the Animator
   │  → Animator pushes to bone Transforms
   │
   ▼ Cleanup
   │  handle.Dispose() → adapter.Remove(port) + playable.Destroy()
   │  Adapter.OnDestroy → _graph.Destroy() (if valid)
```

### Authoring expectations for a mod-supplied animation

To ship a custom animated control or animation hook in a mod-defined car/scenery:

1. **Animation clip** authored in Unity, included in the AssetBundle.
2. **Animation curves must reference relative paths** matching the body prefab's transform hierarchy under the GameObject that holds the `Animator`. The `Animator` is parked on the same GameObject as the `AnimationMap`; the clip's curves are resolved relative to that GameObject. If your clip was authored against a different hierarchy, paths won't bind.
3. **AnimationMap entry** on the body prefab — add `MapEntry { name = "myClip", clip = MyAnimationClip }` in the inspector.
4. **Reference by name** in the `AnimationReference` field of the relevant definition data type (e.g., `Definition.BrakeAnimations`, `LoadAnimationComponent.Animation`, `ToggleAnimationComponent.Animation`, `SteamLocomotiveDefinition.Wheelset.Animation`).
5. The C# component (`BrakeAnimator`, `CarLoadAnimator`, `KeyValueBoolAnimator`, `RadialAnimatedControl`, etc.) resolves the name through `AnimationMap.ClipForName()` and adds the resulting clip to the graph adapter.

For a `RadialAnimatedControl` (cab handles) the clip should typically be **0..length seconds long** representing the full sweep from value=0 to value=1; the control sets `handle.Time = value * clip.length` directly.

For a `BrakeAnimator`-style clip, the clip is **played forward (apply, speed=+1) and backward (release, speed=-1)** from whatever time the playable currently sits at. So an apply-brake clip should be authored as `time=0 → released, time=length → applied`.

---

## `PlayableGraphAnimatorAdapter` — the per-Animator wrapper

```csharp
[RequireComponent(typeof(Animator))]
public class PlayableGraphAnimatorAdapter : MonoBehaviour {
    private Animator                   animator;
    private PlayableGraph              _graph;
    private AnimationLayerMixerPlayable _mixer;
    private readonly List<int> _availablePorts = new();

    private void Awake();                    // calls PrepareGraphIfNeeded()
    private void PrepareGraphIfNeeded();     // lazy; idempotent
    private void OnDestroy();                // _graph.Destroy() if valid
    private void OnValidate();               // editor-only field hookup

    public int            AddPlayable(AnimationClip clip, out AnimationClipPlayable playable);
    public PlayableHandle AddPlayable(AnimationClip clip);
    public void           Remove(int port);
}
```

(Source: `Helpers.Animation/PlayableGraphAnimatorAdapter.cs`.)

### Construction

```csharp
_graph = PlayableGraph.Create(base.name);
_graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);   // ← obeys Time.timeScale
_graph.Play();                                            // graph itself plays; clips start paused
AnimationPlayableOutput output = AnimationPlayableOutput.Create(_graph, "Animator", animator);
_mixer = AnimationLayerMixerPlayable.Create(_graph);
output.SetSourcePlayable(_mixer);
```

- **The graph is named after the GameObject** (`base.name`). Useful in Unity's PlayableGraph Visualizer if hooked up.
- **`DirectorUpdateMode.GameTime`** means the graph respects `Time.timeScale` — pausing the game pauses animations. Mods that want UI animations to keep playing during pause must use a different update mode and not share the adapter.
- **One mixer per Animator**, with one output. All clips on this Animator share the mixer; weights are all 1f (`AddInput(playable, 0, 1f)`). There is no fade/blend in vanilla — clips effectively additive on the same skeleton produce the standard "last-ConnectInput-wins per-bone" Unity behaviour.

### `AddPlayable` — port allocation

```csharp
public int AddPlayable(AnimationClip clip, out AnimationClipPlayable playable) {
    PrepareGraphIfNeeded();                                       // re-asserts graph
    playable = AnimationClipPlayable.Create(_graph, clip);
    playable.Pause();                                             // ← clips start paused
    int num;
    if (_availablePorts.Count > 0) {
        num = _availablePorts[0];
        _availablePorts.RemoveAt(0);
        _mixer.ConnectInput(num, playable, 0);                    // weight defaults to current
    } else {
        num = _mixer.AddInput(playable, 0, 1f);                   // weight = 1
    }
    return num;
}
```

Recycles freed ports via the `_availablePorts` LIFO. The mixer never shrinks — once a port is added, it stays in `_mixer`'s input array forever (Unity API limitation). Repeated add/remove cycles thus accumulate stale port indices in the mixer; each `Remove(port)` call disconnects the input and re-uses the port on next `AddPlayable`.

### `Remove`

```csharp
public void Remove(int port) {
    _mixer.DisconnectInput(port);
    _availablePorts.Add(port);
}
```

Disconnects the mixer input and queues the port for reuse. **Does NOT destroy the underlying playable** — the `PlayableHandle.Dispose` method is what actually calls `_playable.Destroy()`. So `adapter.Remove(port)` without disposing the playable leaks GC handles inside Unity's PlayableGraph; always call `handle.Dispose()` (which calls both).

### `OnDestroy`

```csharp
private void OnDestroy() {
    if (_graph.IsValid()) _graph.Destroy();
}
```

Tears down the graph (and all child playables transitively). Handles owning live `PlayableHandle`s become invalid silently; their `Dispose()` will throw on the now-destroyed `_playable`. **Vanilla doesn't guard this** — components that hold handles (e.g., `BrakeAnimator`) call `_playable.Dispose()` in *their* `OnDestroy`, which runs in undefined order relative to the adapter's `OnDestroy` if both live on the same GameObject. In practice they live on different GameObjects (the adapter on the body's `AnimationMap`-bearing child; consumers on parent or sibling GameObjects), so consumer disposes typically run before the adapter is destroyed.

### Patch candidates (PlayableGraphAnimatorAdapter)

| Method | Why patch |
|---|---|
| `PrepareGraphIfNeeded` | Override to add a second `AnimationPlayableOutput`, switch update mode (e.g. `Manual` for cutscenes), or rename the graph for visualizer debugging. |
| `AddPlayable(clip, out playable)` | Inject mixer-input weights, swap the playable type (e.g., `AnimationScriptPlayable`), apply post-processing. |
| `Remove(int port)` | Hook to detect port leaks. |
| `OnDestroy` | Add explicit `playable.Destroy()` calls if your mod creates playables outside the adapter's tracking. |

### Gotchas (PlayableGraphAnimatorAdapter)

- **Graph is `DirectorUpdateMode.GameTime`** — pausing the game (Time.timeScale=0) freezes all animations driven through the adapter. UI/cutscene animations that should play while paused need their own graph in `UnscaledGameTime` or `Manual` mode.
- **`PrepareGraphIfNeeded` is called from both `Awake` AND every `AddPlayable`.** First call wins — subsequent calls are no-ops because `_graph.IsValid()` returns true. But if anything externally calls `_graph.Destroy()` (e.g., a mod), the next `AddPlayable` will silently rebuild the graph — leaving any *other* outstanding handles stale/invalid.
- **No mixer-input-count cap.** Adding 100 clips to one Animator allocates 100 mixer inputs. The `_availablePorts` recycling helps if you cycle handles, but a long-lived adapter that accumulates clips never shrinks the mixer.
- **`output.SetSourcePlayable(_mixer)` is called once.** If `_mixer` is replaced, the output keeps the old reference. Mods can't safely swap the mixer without rebuilding the output.
- **Animator culling mode is `CullCompletely`** when auto-added by `Car.SetupForAnimation` (`Car.cs:1503`). This means *the entire Animator (and its PlayableGraph) stops updating when the car's renderers are off-screen*. Animations resume mid-state when the car re-enters frustum. KVO state observed during the off-screen interval is reflected when the consumer next ticks (e.g., on `OnEnable` re-init or first `Update`). For a rotating-handle control this is generally fine; for a one-shot animation (e.g., couplers opening) the playable may visibly "jump" to its target on re-enter.
- **Adapter is auto-added by extension method** — `animator.PlayableGraphAdapter()` instantiates the MonoBehaviour if missing. So the adapter has no inspector UI; its existence on a GameObject is purely runtime. Mods inspecting at edit-time won't see it.
- **`[RequireComponent(typeof(Animator))]`** — adding the adapter requires an Animator on the same GameObject; Unity auto-adds one if missing during `AddComponent`.
- **No support for `AnimationLayerMixerPlayable` weights or layers**. Vanilla mixer is a single layer at weight 1 across all inputs. To add additive blending or layered animations, you must either patch `PrepareGraphIfNeeded` to insert intermediate mixers, or skip the adapter and roll your own graph.

---

## `PlayableHandle` — the per-clip lifecycle wrapper

```csharp
public class PlayableHandle : IDisposable {
    private readonly PlayableGraphAnimatorAdapter _adapter;
    private readonly int                          _port;
    private readonly AnimationClipPlayable        _playable;

    public float     Time      { get; set; }                  // (float)playable.GetTime() / SetTime()
    public float     Speed     { get; set; }                  // (float)GetSpeed() / SetSpeed()
    public PlayState PlayState { get; }                       // playable.GetPlayState()

    public void Dispose();                  // adapter.Remove(_port); _playable.Destroy()
    public void Play();                     // _playable.Play()
    public void Pause();                    // _playable.Pause()
    public void ClampTimeToClipBounds();    // clamps Time to [0, clip.length]
}
```

(Source: `Helpers.Animation/PlayableHandle.cs`.)

### Surface notes

- **`Time` is in seconds**, not 0..1. Callers normalise themselves: `handle.Time = value * clip.length`.
- **`Speed` is unbounded** — values like `+10`, `-20` are used by `Coupler` (well, in the bare-graph variant) and `Bell` to fast-forward through clips. Negative speed runs the clip backward.
- **`ClampTimeToClipBounds`** is the standard "I just changed direction; don't let Time overshoot the end" guard used by `BrakeAnimator`, `Wheelset`, `SwitchStand`, `Bell`. It does NOT loop — it clamps. For looping behaviour, the consumer manually resets `Time = 0` (see `Bell.UpdateAnimation` and `KeyValueBoolLoopAnimator.Update`).
- **`PlayState`** maps directly to Unity's enum: `Playing`, `Paused`, `Delayed`. Consumers use it to detect "is this clip currently being driven?" — see `Bell.cs:67` and `KeyValueBoolLoopAnimator.cs:41`.
- **`Dispose` does both**: removes from mixer AND destroys the underlying `AnimationClipPlayable`. Calling only `adapter.Remove(port)` without disposing leaks the playable inside the graph until graph teardown.

### Patch candidates (PlayableHandle)

| Method | Why patch |
|---|---|
| `Dispose` | Add tracking/logging; detect double-dispose (vanilla doesn't guard against it — calling `Dispose()` twice on the same handle will throw on `_playable.Destroy()` second time). |
| `Time` setter | Quantise time, snap to keyframes, route through your own clamping policy. |
| `Speed` setter | Globally cap speed (mods adding "slow-mo" / "fast-forward" effects). |
| `ClampTimeToClipBounds` | Replace with looping/wrapping behaviour. |

### Gotchas (PlayableHandle)

- **Not nullable-aware.** Vanilla code uses `handle?.Dispose()` everywhere — and the C# null-check works because `PlayableHandle` is a regular reference type. But `_playable` is a value-type `struct AnimationClipPlayable`. After `Dispose`, the struct is invalid but not null; calling `Time`/`Speed`/`PlayState` on a disposed handle will throw `InvalidOperationException` from the underlying Unity API.
- **No "is disposed" flag.** The handle holds the disposed playable struct silently. Pattern in vanilla: set the field to `null` after dispose. `_playable?.Dispose(); _playable = null;` (see `BrakeAnimator.OnDestroy`, `RadialAnimatedControl.OnDestroy`).
- **`Time` casts via `(float)playable.GetTime()`** — Unity's API uses `double`. For very long clips (>4 hours of clip time, hopefully unused) you lose precision. Not a practical concern.
- **Setting `Time` while paused does NOT advance the animation visually until Unity's playable system ticks.** Same-frame `Time = x; Play();` runs at the new time on the next graph tick. For one-shot playable updates from a coroutine, `yield return null` between Time-set and reading visual state.
- **Setting `Speed` does not start the clip** — `Play()` is required. Conversely, `Pause()` stops time-advance but preserves the current `Time`.

---

## `Animator.PlayableGraphAdapter()` — the lazy-add extension

```csharp
public static class AnimatorExtensionForPlayableGraph {
    public static PlayableGraphAnimatorAdapter PlayableGraphAdapter(this Animator animator) {
        var adapter = animator.GetComponent<PlayableGraphAnimatorAdapter>();
        if (adapter == null) adapter = animator.gameObject.AddComponent<PlayableGraphAnimatorAdapter>();
        return adapter;
    }
}
```

(Source: `Helpers.Animation/AnimatorExtensionForPlayableGraph.cs`.)

**Idempotent and safe to call multiple times.** Multiple consumers on the same GameObject — say, a `BrakeAnimator` and three `RadialAnimatedControl`s — all hit the same adapter, share the same `PlayableGraph`, and contribute additional inputs to the same mixer. This is *the* central convention; do not create your own `PlayableGraph` per consumer (see Coupler's bare-graph code below for what that looks like and why it's unusual).

### Patch candidates

| Method | Why patch |
|---|---|
| `PlayableGraphAdapter` | Replace with a custom adapter type for mod-defined animation pipelines (additive blending, IK, etc.). Note: this is an extension on `Animator`; patching the static method is the only intercept point. |

---

## `CheckAnimationClip` extension — the null-guard

```csharp
public static class AnimationClipChecker {
    public static bool CheckAnimationClip(this Object obj, AnimationClip animationClip) {
        if (animationClip != null) return true;
        try {
            Debug.LogError("animationClip on " + obj.name + " is null: " + animationClip.name, obj);
        } catch (MissingReferenceException) {
            Debug.LogError("animationClip on " + obj.name + " is null", obj);
        }
        return false;
    }
}
```

(Source: `Helpers.Animation/AnimationClipChecker.cs`.)

Used by `RadialAnimatedControl.OnEnable` (`RadialAnimatedControl.cs:90`) and `VerticalControl.OnEnable` (`VerticalControl.cs:39`). **High-value gotcha:** the error-message branch dereferences `animationClip.name` AFTER asserting `animationClip == null`, so the first `Debug.LogError` will throw `NullReferenceException` (or `MissingReferenceException` for a destroyed Object). The `try/catch` only catches `MissingReferenceException`. **A genuinely null `AnimationClip` will `NullReferenceException` from this method**, leaving the caller's `OnEnable` partially executed. In `RadialAnimatedControl` this means the control will still attempt `animator.PlayableGraphAdapter().AddPlayable(animationClip)` two lines later, which throws again on the null clip.

The intent was a soft warning; the implementation is a hard error. **A mod-shipped `RadialAnimatedControl` with a missing clip reference will crash on enable.**

### Patch candidates

| Method | Why patch |
|---|---|
| `CheckAnimationClip` | Fix the dereference bug; replace with a real soft warning that returns `false` cleanly. |

---

## `AssetPack.Common.AnimationMap` — body-prefab name resolver

```csharp
public class AnimationMap : MonoBehaviour {
    [Serializable] public struct MapEntry {
        public string         name;
        public AnimationClip  clip;
    }

    [SerializeField] public List<MapEntry> animationClips = new();

    public AnimationClip ClipForName(string clipName) {
        foreach (var animationClip in animationClips)
            if (animationClip.name == clipName)
                return animationClip.clip;
        throw new ArgumentException(
            "Couldn't find animation named " + clipName + " in " + base.name, "clipName");
    }
}
```

(Source: `AssetPack.Common/AssetPack.Common/AnimationMap.cs`.)

Lives on the body prefab (one per body model; usually a child GameObject of the prefab's root). `Car.SetupForAnimation` walks the `BodyTransform` and `GetComponentInChildren<AnimationMap>` to find it.

### Resolution helper

```csharp
public static class AssetMapExtensions {
    public static AnimationClip Resolve(this AnimationMap map, AnimationReference @ref) {
        if (@ref == null) { Debug.LogError("AnimationReference is null"); return null; }
        return map.ClipForName(@ref.ClipName);
    }
}
```

(Source: `Model/AssetMapExtensions.cs`.)

`AnimationReference` is the data-only definition struct (`Definition/Model.Definition.Data/AnimationReference.cs` — referenced from `car-definitions.md:488`). Pure name carrier; the map does the lookup at load time.

### Patch candidates

| Method | Why patch |
|---|---|
| `AnimationMap.ClipForName` | Add fallback chains, prefix overrides, mod-injected clips by name. The throw-on-miss is harsh — replace with `TryGet` semantics for graceful degradation. |
| `AssetMapExtensions.Resolve` | Single chokepoint for `AnimationReference → AnimationClip` resolution from definitions. Wrap to inject mod animations. |

### Gotchas

- **`ClipForName` throws `ArgumentException` on miss.** This propagates up through `BrakeAnimator` setup, `LoadAnimationComponentBuilder.Build`, `ToggleAnimationComponentBuilder.Build`, etc. A mis-named animation in a definition crashes the whole car load. There is no "missing clip" fallback in vanilla.
- **Linear scan**, not a dictionary. Fine for the typical handful of clips per body; if you ship 100+ animations on one map, lookup cost is real.
- **`name` is the dictionary key, not the clip's filename or asset name.** The string in `MapEntry.name` and the string in `AnimationReference.ClipName` must match exactly. Renaming the asset doesn't propagate; mods that munge clip names need to update both ends.

### Sibling: `MaterialMap`

`AssetPack.Common.MaterialMap` is the parallel structure for `Material` references (used by `MaterialReference`). Same shape, same lookup pattern. Not animation-related but lives in the same authoring surface — most body prefabs ship both.

---

## Per-frame ticking — who drives the playables?

**Unity itself.** Once `_graph.Play()` is called and the `AnimationPlayableOutput` is attached to an `Animator`, Unity's playable system advances the graph automatically every frame in `Default Game` update. **No `Update()` method in any vanilla code calls a playable's tick.**

Consumer `Update`/`FixedUpdate` methods only **mutate** playable state (`Time = x`, `Speed = +1`, `Play()/Pause()`) — they don't drive the actual frame advance. That's important because:

- **Consumer code can be `Update`, `FixedUpdate`, coroutine, or event-driven** — there's no required tick rate. `RadialAnimatedControl.ActiveCoroutine` runs at `WaitForFixedUpdate`; `Bell.Update`, `KeyValueBoolLoopAnimator.Update`, and `SteamLocomotiveWheelAnimator.Update` run at `Update` rate. The graph itself ticks once per frame regardless of which method changed it last.
- **Setting `Time = X; Pause();` on the same frame** still results in the playable being at time X on the next graph tick. Pausing prevents subsequent advance, not the current frame's pose.
- **`SetSpeed(0)` is functionally equivalent to `Pause()`** for output purposes — but `PlayState` reports `Playing` for speed=0 and `Paused` for `Pause()`. Vanilla uses both interchangeably; if you check `PlayState`, beware.

### Update mode caveat

The graph is created in `DirectorUpdateMode.GameTime` (`PlayableGraphAnimatorAdapter.cs:30`). This means:

- **Animations advance with `Time.deltaTime` scaled by `Time.timeScale`.** Game pause = animation pause.
- **Time-warp (`/temult` cheat from the console)** affects animation playback rate proportionally.
- **`Time.fixedDeltaTime` integration is irrelevant** — animation advance is per-frame, not per-physics-tick.

Mods that want UI animations to keep playing while the game is paused (e.g., a wait-cursor, modal effects) must either:

1. Create their own `PlayableGraph` with `DirectorUpdateMode.UnscaledGameTime` (and not use the adapter), or
2. Patch `PrepareGraphIfNeeded` to use a different update mode (will affect all consumers on that adapter).

---

## Garbage / disposal — the central footgun

Unity's `PlayableGraph` is unmanaged underneath. **Failure to `Destroy()` it leaks native memory** and produces "PlayableGraph was not destroyed" warnings on scene unload. Failure to `Destroy()` individual `AnimationClipPlayable`s leaks them into the parent graph until graph teardown.

### Vanilla disposal patterns (audit)

| Component | Disposal call site | Pattern |
|---|---|---|
| `PlayableGraphAnimatorAdapter` | `OnDestroy` | `if (_graph.IsValid()) _graph.Destroy()` — defensive |
| `PlayableHandle` | Consumer-driven | `Dispose()` calls `adapter.Remove(port)` + `_playable.Destroy()` |
| `BrakeAnimator` | `OnDestroy` | iterate `_brakePlayables`, `?.Dispose()` each |
| `Wheelset` | `OnDisable` | `_applyBrakesPlayable?.Dispose(); = null` |
| `RadialAnimatedControl` | `OnDestroy` | `_clipPlayable?.Dispose(); _clipPlayable = null;` |
| `VerticalControl` | `OnDisable` | `_clipPlayable?.Dispose(); _clipPlayable = null;` |
| `Bell` | `OnDestroy` | `_clipPlayable?.Dispose()` (no null assignment) |
| `FireboxDoorAnimator` | `OnDisable` | `_playable?.Dispose(); _playable = null;` |
| `SwitchStand` | `OnDestroy` | `_playable?.Dispose(); _playable = null;` |
| `SemaphoreHeadController` | `OnDestroy` | `_playable?.Dispose(); _playable = null;` |
| `KeyValueBoolAnimator` | `OnDestroy` | `_playable?.Dispose()` |
| `KeyValueBoolLoopAnimator` | `OnDestroy` | `_playable?.Dispose(); _playable = null;` |
| `CarLoadAnimator` | `OnDestroy` | `_clipPlayable?.Dispose()` |
| `SteamLocomotiveWheelAnimator` | `OnDestroy` | `CleanupPlayables()` iterates `_playables[]?.Dispose()` |
| `Coupler` | `OnDestroy` | `_playableGraph.Destroy()` — **owns its own graph, not via adapter** |

### **Inconsistent enable/disable lifecycle** (HIGH-VALUE FINDING)

There are two distinct disposal-timing patterns in vanilla and they don't agree:

1. **`OnEnable` allocates, `OnDestroy` disposes.** Examples: `RadialAnimatedControl`, `Bell`, `BrakeAnimator`, `SwitchStand`, `SemaphoreHeadController`, `KeyValueBoolAnimator`, `KeyValueBoolLoopAnimator`, `CarLoadAnimator`, `SteamLocomotiveWheelAnimator`. The handle survives disable/re-enable cycles.
2. **`OnEnable` allocates, `OnDisable` disposes.** Examples: `VerticalControl`, `Wheelset`, `FireboxDoorAnimator`. Each disable/re-enable cycle creates a new handle and frees the old one.

This is a real divergence. `RadialAnimatedControl` (used by every cab handle) explicitly checks `if (_clipPlayable == null)` in `OnEnable` (`RadialAnimatedControl.cs:91`) to avoid re-creating, while `VerticalControl` always re-creates because it always disposed in `OnDisable`. **`Wheelset` is in the same role as `BrakeAnimator` (both implement `IBrakeAnimator`) but uses opposite lifecycle semantics.** Mods adding new animation consumers should follow the *active component's* convention to match expectations; the safer default is `OnEnable`-allocate / `OnDestroy`-dispose, as that's the dominant pattern.

### **Coupler is the outlier** (HIGH-VALUE FINDING)

```csharp
// RollingStock/Coupler.cs:51-60
private PlayableGraph              _playableGraph;
private AnimationClipPlayable      _openClosePlayable;

private void OnEnable() {
    _openClosePlayable = AnimationPlayableUtilities.PlayClip(animator, openCloseAnimationClip, out _playableGraph);
    _openClosePlayable.Play();
}

private void OnDestroy() {
    _playableGraph.Destroy();
}
```

The `Coupler` MonoBehaviour does **NOT** use `PlayableGraphAdapter`. It uses Unity's `AnimationPlayableUtilities.PlayClip(...)` static helper, which creates its own one-clip-one-graph structure outside the adapter. **Two consequences:**

1. The coupler's animator has *its own* PlayableGraph in addition to whatever graph the body's `PlayableGraphAdapter` has. If both target the same `Animator`, **only one of the graphs' outputs will drive the bones** (last `AnimationPlayableOutput.Create` on the same Animator wins). In practice, Coupler's animator is on the coupler GameObject (separate from the body's animator), so they don't collide — but a mod adding more controls to the coupler's animator via `PlayableGraphAdapter()` would clash.
2. **`OnEnable` is called on every re-enable**, and `AnimationPlayableUtilities.PlayClip` is called each time. This **leaks a graph per disable/re-enable cycle** because `OnDisable` doesn't destroy the previous graph. Only `OnDestroy` destroys *the most recent* graph. So enabling a coupler GameObject 10 times leaks 9 PlayableGraphs.

This is the documented contradiction with [interaction-controls.md](interaction-controls.md) (which described `Coupler` as using `PlayableHandle` — that's not quite right; Coupler uses raw `AnimationClipPlayable` + its own `PlayableGraph`). The cab handles, brake stand, throttle, etc. all use the `PlayableGraphAdapter` properly via `RadialAnimatedControl`. Coupler is the legacy/outlier path.

### Patch candidates (disposal)

| Goal | Patch target |
|---|---|
| Detect leaked graphs across the world | Patch `PlayableGraphAnimatorAdapter.OnDestroy` to log; subscribe to `SceneManager.sceneUnloaded` to audit. |
| Fix Coupler's leak-on-re-enable | Patch `Coupler.OnDisable` (or add via Harmony postfix) to call `_playableGraph.Destroy()` and re-init in `OnEnable`. |
| Standardise the inconsistent enable/disable lifecycle | Patch the `OnEnable`/`OnDisable`/`OnDestroy` of one or the other group to match. Risky — changes ordering semantics. |

### Gotchas (disposal)

- **PlayableGraph leak warnings appear on scene unload**, not at the leak site. Hard to attribute. Setting graph names via `PlayableGraph.Create(name)` (which the adapter does) is the only way to identify them in the editor's PlayableGraph Visualizer.
- **`_playableGraph` (struct) on Coupler is default-valued before `OnEnable` runs**. Calling `.Destroy()` on a default `PlayableGraph` no-ops (Unity guards). So the very first `OnDestroy` after construction-but-before-enable doesn't crash; subsequent re-enables do leak though.
- **Disposing a `PlayableHandle` whose adapter has been destroyed** crashes — `_adapter.Remove(_port)` calls `_mixer.DisconnectInput(port)` on an invalid mixer. Vanilla doesn't guard. Mods that hold long-lived handles across scene transitions should null-check.

---

## MP — animation is local-only-from-state

**There is no animation event replication.** No `IGameMessage` carries `Time`, `Speed`, `Play`/`Pause`, or per-clip parameters. Every machine animates from its own KVO state, which IS replicated.

### Concrete consequences

- **Brake-shoe animation** (`BrakeAnimator.BrakeApplied`) is driven from `Car.UpdateBrakeApplied(bool)` (`Car.cs:948`) which is called from the per-tick brake state observation. The bool is computed locally from `air.handbrakeApplied || air.BrakeCylinder.Pressure > 2f` — both KVO-replicated values. Each client computes independently; animations stay roughly in sync because the inputs are.
- **Cab handles** (`RadialAnimatedControl`) sync via `Value` (driven by `OnValueChanged → ConfigurePropertyChange → KVO write`). Remote clients see the new value in their KVO observer and call the local control's `Value = x`, which triggers `ValueDidChange()` and starts the `AnimateToValue` lerp coroutine. Animation is purely cosmetic interpolation around the underlying scalar.
- **Switch stand** (`SwitchStand`) listens to `SwitchThrownDidChange` Messenger event (which IS replicated via `FireEvent`) and animates locally.
- **Semaphore aspect** (`SemaphoreHeadController.SetAspect`) is called by signal logic locally — the underlying signal state is in KVO, so all clients animate independently to the same target aspect.
- **Coupler open/close** (`Coupler.SetOpen`) is called from `Car.PositionCoupler` which runs from the `_f.coupled`/`_r.coupled` KVO observer (`Car.cs:1645`).
- **Wheels** (`Wheelset.Roll`, `SteamLocomotiveWheelAnimator.ApplyDistanceMoved`) advance from per-frame local position-derived values (`MovementInfo.Distance`); each client's wheel rotation is approximately in phase because each integrates the same train physics.
- **Bell** (`Bell.IsOn`) is driven from `IntegerLoopingPlayer.play` which mirrors a KVO bool.

### Drift

- **Wheel phase can drift** between clients over time because each integrates its own per-frame `info.Distance`. After a long simulation, wheel rotations may visibly differ. No vanilla code reconciles this; the visual divergence is harmless.
- **Animation lerp timing differs by frame rate.** `RadialAnimatedControl._animationValue = Lerp(_animationValue, value, 20 * Time.deltaTime)` is frame-rate-dependent (and frame-rate-clamped — at very low FPS the lerp would overshoot if not for the cap inherent in lerp-towards). Two clients at different frame rates see different transient states between value changes; final value matches.

### Patch candidates (MP angle)

| Goal | Patch target |
|---|---|
| Add a network-replicated animation event for a one-shot effect | Define a `IGameMessage`, broadcast via `StateManager.ApplyLocal`, handle on receivers by setting `handle.Time = 0; handle.Play();`. **Vanilla has zero examples — this is a green-field design.** |
| Sync animation phase precisely (e.g., wheels) | Compute phase from a replicated KVO key (e.g., `_odometer`, `Car.OdometerActual` — host-replicated) instead of integrating local `info.Distance`. |

### Gotchas (MP)

- **No per-machine identity in the animation.** Animation is a pure function of replicated state. Adding a "this client only" animation requires deliberately not-replicating its trigger.
- **Animation pause via `Time.timeScale = 0`** affects all clients independently. If host time-warps via `/temult`, only the host's animations speed up; clients' timescales are independent.
- **Animation events on AnimationClips (Unity's "function call" keyframe events) are NOT used** in vanilla — at least, no `[AnimationEventCallback]`-style consumers were found. If your custom clip has function-call events, they'll fire on every machine that loads the body.

---

## Patch points for custom animations + replacing vanilla animations per-prefab

### Adding a new animated component to a vanilla car (no custom asset pack)

Two paths:

1. **`ToggleAnimationComponent`** in the car definition's `Components` list. Drives a `KeyValueBoolAnimator` via `ToggleAnimationComponentBuilder`. Requires a clip in the body's `AnimationMap` and a `Key` (KVO key on the car). Toggling the key (via your own pickable, message handler, or KVO write) plays the clip forward/backward.
2. **`LoadAnimationComponent`** for load-state-driven animations. Drives `CarLoadAnimator` via `LoadAnimationComponentBuilder`. Time-set is direct (target time = current load %).

### Replacing a vanilla animation per-prefab

Two paths:

1. **Edit the body prefab's `AnimationMap` entry** to point at a different `AnimationClip`. The new clip is picked up at next car load. Requires shipping a new asset bundle.
2. **Patch `AssetMapExtensions.Resolve(AnimationMap, AnimationReference)`** to substitute clips at lookup time. Mod-friendly: no asset bundle changes; intercept by `AnimationReference.ClipName` to map to your replacement.

### Adding a per-frame-ticked custom animation

The standard pattern from any of `Bell`/`KeyValueBoolLoopAnimator`/etc.:

```csharp
public class MyAnimator : MonoBehaviour {
    public Animator       animator;
    public AnimationClip  myClip;
    private PlayableHandle _handle;

    private void OnEnable() {
        if (_handle == null && myClip != null && animator != null)
            _handle = animator.PlayableGraphAdapter().AddPlayable(myClip);
    }

    private void OnDestroy() {                    // pick OnEnable/OnDestroy or OnEnable/OnDisable — be consistent
        _handle?.Dispose();
        _handle = null;
    }

    private void Update() {
        if (_handle == null) return;
        _handle.Time = SomeNormalizedValue() * myClip.length;
    }
}
```

### Adding a continuous control with custom mouse handling

Subclass `ContinuousControl`. See [Interaction & Continuous Controls › ContinuousControl](interaction-controls.md#continuouscontrol-the-drag-to-set-a-value-substrate). The animation portion is identical to `RadialAnimatedControl` — get a handle from `animator.PlayableGraphAdapter().AddPlayable(animationClip)` and update `Time` per `value`. The hard part is the input math; the playable plumbing is one line.

### Cross-prefab "this animation everywhere"

Use a Harmony patch on the `AnimationMap.ClipForName` lookup or `AssetMapExtensions.Resolve` to inject your clip globally. Example: replace every car's brake-shoe clip with a custom one regardless of asset pack.

```csharp
[HarmonyPatch(typeof(AnimationMap), nameof(AnimationMap.ClipForName))]
public static class PatchAnimMap {
    static bool Prefix(string clipName, ref AnimationClip __result) {
        if (clipName == "BrakeApply" && MyMod.Replacement != null) {
            __result = MyMod.Replacement;
            return false;
        }
        return true;
    }
}
```

### Patching existing animation behaviours

| Goal | Patch target |
|---|---|
| Alter brake-shoe animation behaviour | `BrakeAnimator.BrakeWasAppliedDidChange` (or `Wheelset.BrakeAppliedDidChange` for trucks that use Wheelset). |
| Custom switch-throw animation timing | `SwitchStand.HandleSwitchThrownDidChange` (or wrap `playableTargetSpeed` getter). |
| Replace bell ring-down behaviour | `Bell.UpdateAnimation` — its 0.6 threshold and 0.75× speed-down are baked. |
| Coupler open/close timing | `Coupler.SetOpen` — speeds (-20, +10) are hard-coded. |
| Cab-handle smoothing rate | `RadialAnimatedControl.AnimateToValue` (private coroutine) — `20f * Time.deltaTime` is the lerp k. |
| Steam-loco wheel rotation | `SteamLocomotiveWheelAnimator.ApplyDistanceMoved`. |
| Firebox door animation speed/threshold | `FireboxDoorAnimator.MoveToTarget` and the public `animationSpeed` field. |

---

## Vanilla `PlayableHandle` consumer index

Every consumer of `PlayableHandle` in `Assembly-CSharp`:

| Consumer | File | What it animates | Lifecycle |
|---|---|---|---|
| `BrakeAnimator` | `RollingStock/BrakeAnimator.cs` | Per-truck brake-shoe apply/release | `Start`-allocate, `OnDestroy`-dispose |
| `Wheelset` | `RollingStock/Wheelset.cs` | Per-truck brake animation (alternate path) + wheel rotation (without playable) | `OnEnable`-allocate, `OnDisable`-dispose |
| `RadialAnimatedControl` | `RollingStock.ContinuousControls/RadialAnimatedControl.cs` | Cab twist valves / levers | `OnEnable`-allocate (skip if exists), `OnDestroy`-dispose |
| `VerticalControl` | `RollingStock.ContinuousControls/VerticalControl.cs` | Cab vertical mouse-Y handles | `OnEnable`-allocate, `OnDisable`-dispose |
| `Bell` | `RollingStock/Bell.cs` | Bell pendulum loop with ring-down | `Start`-allocate, `OnDestroy`-dispose |
| `FireboxDoorAnimator` | `RollingStock/FireboxDoorAnimator.cs` | Steam loco firebox door (KVO-bool driven, half-or-full open) | `OnEnable`-allocate, `OnDisable`-dispose |
| `SwitchStand` | `Track/SwitchStand.cs` | Track switch-stand throw | `OnEnable`-allocate (skip if exists), `OnDestroy`-dispose, `OnDisable`-pauses |
| `SemaphoreHeadController` | `Track/SemaphoreHeadController.cs` | Signal semaphore arm position | `Awake`-allocate, `OnDestroy`-dispose |
| `KeyValueBoolAnimator` | `RollingStock.Controls/KeyValueBoolAnimator.cs` | Generic KVO-bool→play-clip-forward/backward | `Awake`-allocate, `OnDestroy`-dispose |
| `KeyValueBoolLoopAnimator` | `RollingStock.Controls/KeyValueBoolLoopAnimator.cs` | Generic KVO-bool→play-loop-with-ring-down | `Start`-allocate, `OnDestroy`-dispose |
| `CarLoadAnimator` | `RollingStock.Controls/CarLoadAnimator.cs` | Car load-level fill animation | `Start`-allocate, `OnDestroy`-dispose |
| `SteamLocomotiveWheelAnimator` | `RollingStock.Steam/SteamLocomotiveWheelAnimator.cs` | Multi-wheelset rotation animations | `Configure`-allocate (re-allocates), `OnDestroy`-dispose |

Vanilla outliers that bypass the adapter:

| Component | Why it's different |
|---|---|
| `Coupler` | Uses `AnimationPlayableUtilities.PlayClip` + its own `PlayableGraph`. Leaks a graph per `OnEnable` cycle (see disposal section). |

---

## Cross-references

- `RadialAnimatedControl` mouse handling (the angle-vs-sphere mode, snapping, `ConfigurePropertyChange`): [Interaction & Continuous Controls › RadialAnimatedControl](interaction-controls.md#radialanimatedcontrol--the-workhorse).
- `BrakeAnimator.BrakeApplied` driven by `Car.UpdateBrakeApplied(bool)` from per-tick brake state: [Brakes › `UpdateBrakeApplied` visual sync](brakes.md#updatebrakeapplied-visual-sync).
- `Coupler` MonoBehaviour visual layer (audio + animation, no physics): [Couplers › `RollingStock.Coupler` — the visual layer](couplers.md#rollingstockcoupler--the-visual-layer).
- `Definition.BrakeAnimations` field on `CarDefinition`: [Car Definitions › CarDefinition](car-definitions.md) (search `BrakeAnimations`).
- `AnimationMap` / `MaterialMap` body-prefab MonoBehaviours: [Asset Packs › body-prefab maps](asset-packs.md) and [Car Definitions › references](car-definitions.md).
- `ComponentBuilderContext.AnimatorGameObject`: [Car Definitions › component pipeline](car-definitions.md#componentbuildercontext) (it's the `AnimationMap.gameObject`).
- KVO observers feeding animation state (e.g., `_f.coupled` → `Coupler.SetOpen`): [Couplers › KVO observers](couplers.md#kvo-observers-apply-incoming-changes).
