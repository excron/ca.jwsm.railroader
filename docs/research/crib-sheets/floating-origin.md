# Floating Origin — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Player & Camera](player-camera.md), [Track Topology](track-topology.md), [Cars & Cargo](cars-cargo.md)

Railroader runs Unity at full real-world map scale, so it uses a "floating origin" system to keep the player area near `Vector3.zero` and avoid float-precision wobble. **`WorldTransformer`** is a single MonoBehaviour singleton that, every second, looks at where the camera is, and if the camera has drifted more than `tileRange` (3) tiles of `tileSize` (500m) from the current re-origin tile, schedules a 1-second-delayed re-origin: every registered `Transform` (and every `World`-space particle) is shifted by `-(targetTile - currentTile) * tileSize` and a `WorldDidMoveEvent` is broadcast. **Game space** = the canonical, persistent coordinate system (logical map coords, what saves and `Track.Location` resolutions resolve to). **World space** = what Unity actually renders; `worldPos = gamePos + _currentOffset`. The transform between them is **per-machine local state** — there is no MP sync of the floating-origin offset; each client maintains its own offset, and *all over-the-wire positions are game-space*. Most code lives in world space (Unity transforms) and converts at the network boundary; a smaller body of code (track topology, save/load, MP messages, MapManager tile loading) lives in game space.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Helpers.WorldTransformer` | `Helpers/WorldTransformer.cs:12` | Singleton; owns `_currentOffset`, runs origin-shift coroutine, fires `WorldDidMoveEvent` |
| `Helpers.WorldTransformer.GameToWorld(Vector3)` | `Helpers/WorldTransformer.cs:229` | `_currentOffset + v` |
| `Helpers.WorldTransformer.WorldToGame(Vector3)` | `Helpers/WorldTransformer.cs:234` | `worldPos - _currentOffset` |
| `Helpers.WorldTransformerExtensions` | `Helpers/WorldTransformerExtensions.cs:5` | Extension methods on `Vector3`: `.GameToWorld()` / `.WorldToGame()` |
| `Helpers.WorldTransformerTarget` | `Helpers/WorldTransformerTarget.cs:7` | MonoBehaviour; auto-registers `transform` for shifting on enable, "catches up" to current offset in `Awake` |
| `Helpers.WorldTransformerTargetList.Targets` | `Helpers/WorldTransformerTargetList.cs:8` | Static `HashSet<Transform>` — the move set |
| `Game.Events.WorldDidMoveEvent` | `Game.Events/WorldDidMoveEvent.cs:5` | `struct { Vector3 Offset }` — broadcast through `Messenger.Default` after a shift |
| `WorldTransformer.OnDidMove : event Action<Vector3>` | `Helpers/WorldTransformer.cs:49` | Direct C# event on the singleton (parallel to the Messenger broadcast; used by `CullingGroup` consumers that can't use Messenger) |
| `Helpers.MainCameraHelper.TryGetIfNeeded(ref Camera)` | `Helpers/MainCameraHelper.cs:9` | `Camera.main` lazy resolver used everywhere camera-aware code runs |
| `Helpers.TransformExtensions.GamePosition(this Transform)` | `Helpers/TransformExtensions.cs:26` | Sugar for `WorldToGame(transform.position)` |
| `Track.Graph.PositionRotation` (struct) | `Track/Graph.cs:35` | `(Vector3 Position, Quaternion Rotation)`; the `Position` field is **game-space** when produced from a `Track.Location` |

---

## Origin-shift spine

```
WorldTransformer (singleton, [DefaultExecutionOrder(1000)])
   │  Coroutine: every 1s
   │     target = TilePosition(CameraSelector.shared.CurrentCameraPosition)   ← uses CURRENT WORLD POS
   │     if (|target - currentTile|.x > tileRange.x  || .y > tileRange.y)
   │        wait 1s → _pendingMove = target
   │
   ▼  FixedUpdate consumes _pendingMove → PerformMove(target)
PerformMove(target):
   │   delta      = target - currentTile
   │   shiftWorld = (-delta.x * tileSize.x, 0, -delta.y * tileSize.y)   ← world is shifted OPPOSITE to camera drift
   │   _currentOffset += shiftWorld
   │   foreach (t in WorldTransformerTargetList.Targets) t.position += shiftWorld
   │   currentTile = target
   │   TranslateParticleSystems(shiftWorld)        ← scans EVERY ParticleSystem each shift
   │   OnDidMove?.Invoke(shiftWorld)               ← C# event: CullingManager, SimpleCuller, TelegraphPoleManager
   │   Messenger.Default.Send(WorldDidMoveEvent(shiftWorld))   ← broadcast
   ▼
WorldDidMoveEvent subscribers (Messenger):
   │  Cameras.StrategyCameraController.WorldDidMove          : offsets _targetPosition + transform.position + drag-anchors
   │  Cameras.MapCameraUpdater.WorldMoved                    : MapManager.ApplyWorldToGameOffset (terrain + NatureRenderer)
   │  Character.CharacterController.WorldDidMove             : motor.OffsetCharacter (KCC)
   │  Avatar.AvatarPrefab.WorldDidMove                       : Rigidbody.position += offset
   │  Effects.Decals.DepthProjectorHelper.WorldDidMove       : refresh _DecalProjectorOriginY shader uniform
   │  Track.PrefabInstancer.WorldDidMove                     : GPUInstancerAPI.SetGlobalPositionOffset + matrix translate
   │  TrainController.WorldDidMove                           : foreach Car car.WorldDidMove(offset); _carCuller.WorldDidMove(offset)
   │     └─ Car.WorldDidMove(Car.cs:2841)
   │           ├─ _mover.WorldDidMove(offset)         (CarMover.cs:172)
   │           │     └─ _physicsMover.OffsetSeamless(offset)  (or _bodyTransform.position)
   │           ├─ _audioReparenter.Rigidbody.position = _mover.Position
   │           └─ OffsetMapIconPosition(offset)
   │     └─ CarCuller.WorldDidMove → recompute every record's BoundingSphere position from car center
   │
WorldTransformer.OnDidMove subscribers (Action<Vector3>):
   │  Helpers.Culling.CullingManager.OnWorldDidMove          : foreach token RequestUpdateCullingPosition()
   │  Helpers.SimpleCuller.OnWorldDidMove                    : refresh single sphere position
   │  TelegraphPoles.TelegraphPoleManager.OnWorldDidMove     : offset every sphere + per-wire WorldDidMove
```

**Critical**: the move set is `WorldTransformerTargetList.Targets` — a flat `HashSet<Transform>`. Anything not registered there *and* not parented to something that is, *and* not a subscriber to `WorldDidMoveEvent`, **stays put in world space when origin shifts** — i.e., it teleports relative to the world by hundreds of meters in one frame. This is the #1 floating-origin gotcha. See [Gotchas](#gotchas).

---

## `Helpers.WorldTransformer` (the orchestrator)

`MonoBehaviour`, `[DefaultExecutionOrder(1000)]` (runs after most things in FixedUpdate), placed once in the scene. `Shared` is found lazily via `FindObjectOfType` (`WorldTransformer.cs:41`) — there is no `[ExecuteInEditMode]` and the singleton can be `null` outside play mode.

### State

```csharp
public  Vector2Int tileRange = new Vector2Int(3, 3);     // 17  — re-origin if dx>3 or dy>3 tiles
public  Vector2   tileSize  = new Vector2(500f, 500f);  // 25  — 500m per tile
private const float MoveDelay = 1f;                      // 19  — debounce delay before re-origin
private static Vector3 _currentOffset = Vector3.zero;    // 23  — STATIC; persists across instance lifetimes? See gotcha
private Vector2Int currentTile;                          // 21  — last-applied origin tile
private bool       waitForMover;                          // 27  — guard: a move is scheduled or in-flight
private Vector2Int? _pendingMove;                        // 29  — handed off to FixedUpdate

public event Action<Vector3> OnDidMove;                  // 49  — separate from WorldDidMoveEvent
private static HashSet<Transform> ObjectsToMove          // 47  → WorldTransformerTargetList.Targets
```

### Lifecycle

```csharp
public void OnEnable()    { _currentOffset = Vector3.zero; _checkCoroutine = StartCoroutine(CheckForChangeCoroutine()); }  // 57
private void OnDisable()  { StopCoroutine(_checkCoroutine); _checkCoroutine = null; }
private void OnDestroy()  { _currentOffset = Vector3.zero; }                                                                  // 83
private void FixedUpdate(){ if (_pendingMove.HasValue) { var t = _pendingMove.Value; _pendingMove = null; PerformMove(t); waitForMover = false; } }  // 72
[ContextMenu("Move Now")]
public void MoveNow();    // 89 — debug/manual trigger; CameraSelector.CameraJumped() also calls this after teleport
```

### The check loop

```csharp
private IEnumerator CheckForChangeCoroutine() {                                  // 99
    while (true) {
        Vector2Int target = CurrentTarget();
        MoveWorldIfNeeded(target);
        yield return new WaitForSeconds(1f);
    }
}

private Vector2Int CurrentTarget()                                                 // 109
    => TilePosition(CameraSelector.shared.CurrentCameraPosition);

private Vector2Int TilePosition(Vector3 pos)                                       // 115
    => new Vector2Int(Mathf.FloorToInt(pos.x / tileSize.x), Mathf.FloorToInt(pos.z / tileSize.y));

private void MoveWorldIfNeeded(Vector2Int target) {                                // 124
    if (waitForMover) return;
    Vector2Int delta = target - currentTile;
    if (Mathf.Abs(delta.x) > tileRange.x || Mathf.Abs(delta.y) > tileRange.y) {
        waitForMover = true;
        _scheduledMoveCoroutine = StartCoroutine(ScheduleMoveWorldDelayed(target));
    }
}

private IEnumerator ScheduleMoveWorldDelayed(Vector2Int target) {                   // 137
    Log.Debug("PreMoveWorld {target}", target);
    yield return new WaitForSeconds(1f);          // ← the MoveDelay debounce
    _pendingMove = target;
    _scheduledMoveCoroutine = null;
}
```

**Trigger threshold (defaults):**
- Polled every **1.0 s**.
- Fires when `|cameraTile - currentTile|.x > 3` *or* `.y > 3`. With `tileSize = 500m`, that's drift > **1500 m on either axis** (i.e., camera is more than ~1.5 km from the current origin tile in `x` or `z`).
- After threshold trip, debounce **1.0 s**, then commit on next `FixedUpdate`.
- `tileRange` and `tileSize` are public serialized fields — modders/mappers can override per-scene by editing the prefab.
- **`CameraSelector.CameraJumped()` calls `WorldTransformer.MoveNow()` immediately after any teleport** (`CameraSelector.cs:236-242`), which bypasses the debounce. This is why hopping across the map doesn't leave you with a giant origin offset.

### The actual shift

```csharp
private void PerformMove(Vector2Int target) {                                       // 156
    Log.Information("MoveWorld {target}", target);
    Vector2Int delta = target - currentTile;
    Vector3 shift = new Vector3(-delta.x * tileSize.x, 0f, -delta.y * tileSize.y);  // ← y is never shifted
    _currentOffset += shift;
    foreach (Transform t in ObjectsToMove)
        if (t != null) t.position += shift;
    currentTile = target;
    TranslateParticleSystems(shift);
    OnDidMove?.Invoke(shift);
    Messenger.Default.Send(new WorldDidMoveEvent(shift));
}
```

`y` is never shifted (terrain elevation stays put). Only XZ.

### Particle-system shifting

```csharp
private static void TranslateParticleSystems(Vector3 offset) {                      // 176
    var systems = FindObjectsOfType<ParticleSystem>();
    foreach (var ps in systems) {
        if (ps.main.simulationSpace != ParticleSystemSimulationSpace.World) continue;
        // pause, GetParticles → loop & offset positions → SetParticles → resume
    }
}
```

**Every** active `ParticleSystem` with `World` simulation space gets every live particle individually offset on each shift. `Local`-space systems are untouched (they ride their parent). This is the canonical pattern modders should follow for any custom particle-like buffer.

### Manual registration API

```csharp
public void AddObjectToMove(Transform t)    { MoveObject(t); ObjectsToMove.Add(t); }     // 218 — also catch-up shifts
public void RemoveObjectToMove(Transform t) { ObjectsToMove.Remove(t); }                  // 224
private void MoveObject(Transform t)        { t.position += _currentOffset; }             // 213 — push to current origin
```

`AddObjectToMove` performs an immediate "catch-up" shift by `+_currentOffset` (i.e., assumes the transform's current position is in *game* coords and bumps it to current world coords). This matches `WorldTransformerTarget.Awake`'s catch-up path.

### Patch candidates

| Method | Why patch |
|---|---|
| `WorldTransformer.PerformMove` | Single chokepoint for every origin shift. Postfix to do mod-side bookkeeping (e.g. shift custom buffers, recalc world-space caches). Prefix to veto a shift (set `waitForMover = false; return;`). |
| `WorldTransformer.MoveWorldIfNeeded` | Change the trigger threshold without editing serialized fields. |
| `WorldTransformer.TranslateParticleSystems` | Replace particle-shift strategy (e.g., skip mod-tagged systems, batch via `JobSystem`). |
| `WorldTransformer.GameToWorld` / `WorldToGame` | The two static converters. **Hot path** (called from many places, including `Update` loops) — patching is risky. Prefer mod-side caching. |
| `WorldTransformer.AddObjectToMove` | Hook custom-target registration. |

### Gotchas

- **`_currentOffset` is `static`.** If the `WorldTransformer` is destroyed and a new one is enabled (e.g., scene reload), `OnEnable` resets it to `Vector3.zero`, but in the gap the static value from the previous instance can leak into `GameToWorld` calls if anything queries it before the new singleton enables. Save/load and scene transitions reset it explicitly via `OnEnable` (line 59) and `OnDestroy` (line 85).
- **Polled by camera position only.** A player who never moves the camera — but who teleports a car or watches an automated train roll across the map — will not trigger a re-origin. The camera is the *only* source of the trigger. (`CameraSelector.CameraJumped()` calls `MoveNow()` after teleports, which is the recovery path.)
- **`y` axis is not shifted.** The world is flat-shifted on XZ only. Any code computing absolute Y positions in world space remains valid across shifts.
- **`TranslateParticleSystems` does `FindObjectsOfType<ParticleSystem>()` every shift.** This is O(scene-particle-systems) per shift. Mods spawning many short-lived particle systems should ensure their main module is set to `Local` simulation space if origin doesn't matter; otherwise this is fine but allocates per-shift.
- **`tileRange = (3,3)` with `tileSize = 500m` means ~1500m drift before re-origin.** Single-precision Unity transforms tolerate this comfortably; the threshold isn't tight. Authored maps may override.
- **The `WaitForSeconds(1f)` debounce uses scaled time.** Pausing the game freezes the re-origin coroutine. (Shouldn't matter — pause = no camera move.)
- **`OnDidMove` event vs `WorldDidMoveEvent` Messenger broadcast both fire on every shift.** They are *not* alternatives; subscribers are split between the two channels somewhat arbitrarily. Listing in [Subscribers](#subscriber-catalog) below.

---

## `Helpers.WorldTransformerTarget` — the auto-register helper

```csharp
[DefaultExecutionOrder(-1000)]
public class WorldTransformerTarget : MonoBehaviour {                              // WorldTransformerTarget.cs:7
    private void Awake() {
        WorldTransformerTargetList.Targets.Add(transform);
        Vector3 caughtUp = transform.position.GameToWorld();    // assume current is game-space
        if (caughtUp != transform.position) {
            Log.Debug("WorldTransformerTarget Catch-up {name}", name);
            transform.position = caughtUp;
        }
    }
    private void OnDestroy() { WorldTransformerTargetList.Targets.Remove(transform); }
}
```

**Drop this MonoBehaviour on any GameObject whose `transform` you want to auto-shift on origin re-center.** The "catch-up" call assumes the transform's serialized `position` is *game-space* (i.e., authored at "real" coordinates) and translates it to current world-space. This is the correct path for **any mod GameObject not parented to scene-baked geometry that already lives at world coords**.

`[DefaultExecutionOrder(-1000)]` ensures it runs early so other components see the corrected `transform.position` in their `Awake`/`Start`.

---

## `Helpers.MainCameraHelper`

```csharp
public static class MainCameraHelper {                                              // MainCameraHelper.cs
    [ContractAnnotation("=> true, mainCamera: notnull; => false, mainCamera: null")]
    public static bool TryGetIfNeeded(ref Camera mainCamera) {
        if (mainCamera == null) {
            Camera main = Camera.main;
            if (main == null) return false;
            mainCamera = main;
        }
        return true;
    }
}
```

A cached `Camera.main` getter. Used by `CameraSelector`, `StrategyCameraController`, `LocationIndicatorController`, `CompassHUD`, `AutoEngineerDestinationPicker`, `ObjectPicker`, `ConsistPlacer`, `LanternController`, `DecalCullingManager` — anywhere code in `Update`/`FixedUpdate` needs the main camera and doesn't want to pay the `Camera.main` GC cost every frame.

**The pattern:**

```csharp
private Camera _camera;
private void Update() {
    if (!MainCameraHelper.TryGetIfNeeded(ref _camera)) return;
    // ... use _camera
}
```

Mods that need camera-relative behavior every frame should adopt this pattern. The `[ContractAnnotation]` lets ReSharper/JB infer null-safety.

---

## `Track.Graph.PositionRotation` (the `Helpers.PositionRotation` referenced in `player-camera.md`)

There is no `Helpers.PositionRotation` type — `player-camera.md`'s reference resolves to **`Track.Graph.PositionRotation`** (a nested `struct` in `Track/Graph.cs:35`). It's a small value type used as a generic position+rotation tuple; the `Position` field convention depends on the producer:

```csharp
public struct PositionRotation(Vector3 position, Quaternion rotation) {            // Graph.cs:35
    public Vector3    Position;
    public Quaternion Rotation;
    public override string ToString() => $"({Position:F1}, {Rotation.eulerAngles:F1})";
    public PositionRotation Project(float distance) =>                              // step forward `distance` meters along Rotation.forward
        distance == 0f ? this : new PositionRotation(Position + Rotation * (Vector3.forward * distance), Rotation);
}
```

Producers and convention:

| Producer | Position semantics |
|---|---|
| `Track.Graph.GetPosition(Location)` and similar | **Game-space** (authored track coords) |
| `CameraSelector.DefaultSpawn` | **Game-space** (set via `WorldTransformer.WorldToGame(...)`, see `OpsController.cs:407`) |
| `SpawnPoint.GamePositionRotation` | **Game-space** (`SpawnPoint.cs:25` — `WorldToGame` of a serialized world transform) |
| `Cameras.JumpTarget.Position` | **Game-space** (consumed via `worldPosition = jumpTarget.Position.GameToWorld()` in `CameraSelector.cs:596`) |

If you produce a `PositionRotation` from a Unity `transform.position`, **convert via `.WorldToGame()` first** unless you specifically know the consumer wants world-space. The pattern in vanilla is consistent: `PositionRotation.Position` is game-space.

---

## Subscriber catalog (who responds to a shift)

### Via `Messenger.Default.Send(WorldDidMoveEvent)`

| Subscriber | File:line | Action |
|---|---|---|
| `TrainController` | `TrainController.cs:389,479` | Iterates all `Cars` and calls `Car.WorldDidMove(offset)`; forwards to `_carCuller.WorldDidMove(offset)` |
| `Character.CharacterController` | `Character/CharacterController.cs:201,209` | `motor.OffsetCharacter(offset)` (KCC built-in offset) |
| `Cameras.StrategyCameraController` | `Cameras/StrategyCameraController.cs:136,180` | Offsets `_targetPosition`, `transform.position`, `_moveToTarget`, `_panStartCameraPosition`, `_panStartTarget`, `_panStartPosition` |
| `Cameras.MapCameraUpdater` | `Cameras/MapCameraUpdater.cs:26,71` | `mapManager.ApplyWorldToGameOffset(offset)` — see [MapManager interaction](#mapmanager-interaction) |
| `Avatar.AvatarPrefab` | `Avatar/AvatarPrefab.cs:35,43` | `Rigidbody.position += offset` (kinematic body for the local + remote avatars) |
| `Effects.Decals.DepthProjectorHelper` | `Effects.Decals/DepthProjectorHelper.cs:26,42` | Refreshes `_DecalProjectorOriginY` shader uniform (decals are rooted to world Y) |
| `Track.PrefabInstancer` | `Track/PrefabInstancer.cs:81,192` | `GPUInstancerAPI.SetGlobalPositionOffset(prefabManager, offset)` plus walks every `Matrix4x4` in `_entries[].Matrixes` and translates it (this is for the **GPU-instanced ties / tieplates**) |

### Via `WorldTransformer.OnDidMove` (C# event `Action<Vector3>`)

| Subscriber | File:line | Action |
|---|---|---|
| `Helpers.Culling.CullingManager` | `Helpers.Culling/CullingManager.cs:109,249` | Foreach token: `RequestUpdateCullingPosition()` (Unity `CullingGroup` bounding spheres are in world space) |
| `Helpers.SimpleCuller` | `Helpers/SimpleCuller.cs:29,78` | Refreshes its single bounding sphere's position |
| `TelegraphPoles.TelegraphPoleManager` | `TelegraphPoles/TelegraphPoleManager.cs:77,137` | Offsets every `BoundingSphere` and calls `wire.WorldDidMove(offset)` on every `TelegraphWire` |

### Indirectly chained off `TrainController.WorldDidMove`

```
TrainController.WorldDidMove(evt) →
  for each Car: Car.WorldDidMove(offset)
    → CarMover.WorldDidMove(offset)
        → _moverPosition += offset
        → _physicsMover.OffsetSeamless(offset)   (or _bodyTransform.position = _moverPosition)
    → _audioReparenter.Rigidbody.position = _mover.Position
    → OffsetMapIconPosition(offset)
  CarCuller.WorldDidMove(offset)
    → for each record: _spheres[i].position = WorldTransformer.GameToWorld(car.GetCenterPosition(graph))
```

Cars are not in `WorldTransformerTargetList.Targets` — `TrainController.WorldDidMove` is the explicit dispatcher. This is intentional because cars have a *physics mover* with internal interpolation state (`PhysicsMover.OffsetSeamless` is the KCC primitive that shifts a kinematic mover without disturbing its velocity history).

---

## Game space vs world space vs Unity transform.position

| Term | What it means | When to use |
|---|---|---|
| **Game space** | Logical, persistent coordinate system. What saves; what `Track.Location.GetPosition()` returns; what flows over the network. | Saving, loading, MP messages, track resolution, `MapManager` queries, `Track.Graph` walks, anything that must be stable across origin shifts. |
| **World space** | Unity's `transform.position`. `worldPos = gamePos + WorldTransformer._currentOffset`. | Rendering, physics, raycasts, colliders, KCC, audio listener, anything Unity sees. |
| **Unity `transform.position`** | Synonym for world space. Always reflects the current floating origin. | Default for any Unity API. |

### Net difference

```
gameSpace - worldSpace = -_currentOffset   (fixed delta until next shift)
At session start: _currentOffset = Vector3.zero → gameSpace == worldSpace
After drift:      _currentOffset can be many km on XZ; Y is always equal
```

### Per-component player position primitives (from `player-camera.md`)

| Property | Returns | Notes |
|---|---|---|
| `IPlayer.GamePosition` | game space | `LocalPlayer`: `localAvatar.character.GroundPosition.WorldToGame()` |
| `ICameraSelectable.GroundPosition` | **world space** | Per implementer: `PlayerController` → `motor.TransientPosition`; `StrategyCameraController` → `_targetPosition` |
| `CameraSelector.CurrentCameraPosition` | **game space** | `WorldToGame(_currentCamera.CameraContainer.position)` (`CameraSelector.cs:93`) |
| `CameraSelector.CurrentCameraGroundPosition` | game space | Same conversion |
| `Car.GetCenterPosition(graph)` | game space | Uses `Track.Graph` to derive position from `Location` |
| `Transform.GamePosition()` | game space | `WorldTransformer.WorldToGame(transform.position)` extension |

**The naming is inconsistent.** `GroundPosition` on `ICameraSelectable` is world-space; `GamePosition` on `IPlayer` and `Transform` is game-space. Read the source before assuming.

---

## Where conversions happen (the boundary list)

Pattern: read world space (`transform.position`), write game space (saves/MP), and vice versa.

| Site | File:line | Direction | Why |
|---|---|---|---|
| `CharacterPositionTransmitter.SendIfConnected` | `Character/CharacterPositionTransmitter.cs:41` | W→G before send | MP messages carry game-space positions |
| `RemoteAvatar.TRVFromFrame` (no car-relative) | `Avatar/RemoteAvatar.cs:130` | G→W on receive | Apply received game-space to a Unity transform |
| `CameraSelector.SendCameraPositionIfNeeded` | `CameraSelector.cs:168` | W→G before send | `UpdateCameraPosition` carries game-space |
| `MapCameraUpdater.UpdateCoroutine` | `Cameras/MapCameraUpdater.cs:48` | W→G | `mapManager.UpdateVisibleTilesForPosition` is game-space |
| `CameraSelector._JumpToPoint` (priority load) | `CameraSelector.cs:616` | W→G | `MapManager.RequestPriorityLoad` takes game space |
| `CameraSelector.ResolveJumpTarget` (when `JumpTarget` is game-space) | `CameraSelector.cs:596` | G→W | Convert authored target back into the current world frame |
| `Audio.ScheduledAudioPlayer` | `Audio/ScheduledAudioPlayer.cs:44` | G→W | `play.Position` is over-the-wire game-space; convert before playback |
| `OpsController.GetSetupDescriptor` | `Model.Ops/OpsController.cs:407` | W→G | Persistent default spawn |
| `SpawnPoint.GamePositionRotation` | `Character/SpawnPoint.cs:25` | W→G | Authored scene transforms exposed as game-space |
| `Track.TrackSegment` Gizmo | `Track/TrackSegment.cs:164` | G→W | Editor visualization |
| `PlayerController.AttachedCarChecker` (tile keep-loaded) | `Character/PlayerController.cs:324,347,370` | W→G | `MapManager.TilePositionFromPoint` uses game space |
| `TrainPlacementHelper` | `TrainPlacementHelper.cs:889` | W→G | Place trains using map-stable coords |
| `CarCuller.GetSpherePosition` | `RollingStock/CarCuller.cs:219` | G→W | Bounding spheres are in world space |
| `Game.FlareManager.PlaceFlare` | `Game/FlareManager.cs:126` | G→W | Apply received flare position to scene |

The convention is solid: **anything that flows over the network, anything that touches `MapManager` tile coords, anything saved or loaded, and anything stored on `Track.Location`-derived structures uses game space.** Everything else is world space.

---

## MP behavior — **per-machine, no sync**

This is the load-bearing finding for any modder: **the floating-origin offset is not synchronized between players.**

- `WorldTransformer._currentOffset` is local-machine state. It depends on *that machine's local camera*, polled at 1 Hz against *that machine's local `WorldTransformer.tileRange`*.
- Two players in MP, sitting in opposite corners of a map, will have very different `_currentOffset` values — one might be `(0,0,0)`, the other `(-15000, 0, 8500)`.
- All over-the-wire data is **game-space** (`UpdateCharacterPosition`, `UpdateCameraPosition`, `ScheduledAudioPlayer.HostPlay…`, etc.). Each receiver converts via `WorldTransformer.GameToWorld(...)` against its own offset.
- **There is no `WorldDidMoveEvent` propagation, no MP message, no host authority** for the origin shift. `WorldTransformer` doesn't reference `StateManager` at all — no `IsHost` checks, no `AccessLevel` annotations, no `Multiplayer.Client.Send`.
- Cars, switches, and topology are *game-space*; their replication carries no origin information. Each client renders them at `gameSpace + localOffset`.
- Remote avatars' `RelativeToCarId` mode (`RemoteAvatar.TRVFromFrame`, `Avatar/RemoteAvatar.cs:140`) skips `WorldTransformer` entirely and instead transforms a *car-local* offset by the local car's *world-space* `MotionSnapshot.Position` — works because each client's local car position already has the correct local origin baked in.

**Modder implication:** if you write a mod that broadcasts a position over MP, **send game space**, and never assume the receiving client's offset matches yours. If your mod stores positions in a save file or a custom KVO key, also use game space.

---

## Save / load interaction

- Save snapshots persist game-space positions (the format is in `cars-cargo.md` / `save-load.md`).
- On load, `WorldTransformer.OnEnable` resets `_currentOffset = Vector3.zero` (line 59), so the new session begins with `gameSpace == worldSpace`.
- Then cars/avatars/etc. spawn at their saved game-space positions — which, with offset 0, are also world-space, so they appear at the right Unity coords.
- The very first re-origin happens when the camera drifts > `tileRange`. Until then, no shift.
- `CameraSelector.HandleMapDidLoad` triggers a jump (see `player-camera.md`), which calls `CameraJumped()` → `WorldTransformer.MoveNow()` (`CameraSelector.cs:240`), which can immediately re-origin to the spawn point.

---

## MapManager interaction (the unloaded-tile / Bardo region)

`MapCameraUpdater.WorldMoved` is the only direct connection from `WorldTransformer` to terrain:

```csharp
private void WorldMoved(WorldDidMoveEvent evt) {                                    // MapCameraUpdater.cs:71
    mapManager.ApplyWorldToGameOffset(evt.Offset);
}
```

`MapManager.ApplyWorldToGameOffset` (`Map.Runtime/MapManager.cs:258`):

```csharp
public void ApplyWorldToGameOffset(Vector3 offset) {
    _gameToWorldOffset += offset;
    foreach (Record value in _terrains.Values) {
        if (value.MapTerrain != null)        value.MapTerrain.gameObject.transform.localPosition += offset;
        if (value.BuildingTerrain != null)   value.BuildingTerrain.gameObject.transform.localPosition += offset;
    }
    NatureRenderer.SetFloatingOrigin(offset.x, offset.z);     // 3rd-party Nature plugin notification
}
```

Terrain (`MapTerrain`, `BuildingTerrain`) and the `NatureRenderer` (foliage) are **shifted by `MapManager` directly**, not via `WorldTransformerTargetList`. They use `localPosition` because they are children of a transform tree.

**Cars in unloaded tiles ("Bardo"):** when a car's tile is unloaded, the car is unloaded too (`Car.Bardo` mode, see `cars-cargo.md`). It has no live `Transform` to shift; its position lives only in game space (saved to KVO). When the tile reloads and the car respawns, it spawns at its game-space position — which is then converted to world-space using the *current* offset. **Floating origin is therefore transparent to Bardo cars** — they don't need shifting because they don't have a Transform. Cross-link to [`cars-cargo.md`](cars-cargo.md) for Bardo lifecycle.

---

## Patch points for mods

### "I'm spawning a custom GameObject in the world" — make it origin-aware

Three options, in order of convenience:

1. **Add `[Helpers.WorldTransformerTarget]` MonoBehaviour to the GameObject's root transform.** It auto-registers in `Awake`, auto-deregisters in `OnDestroy`, and does the catch-up shift to current offset. **Use this for 90% of cases.**
2. **Manually call `WorldTransformer.AddObjectToMove(transform)`** if you need to register/deregister dynamically. Pair with `RemoveObjectToMove(transform)` on cleanup.
3. **Subscribe to `WorldDidMoveEvent` via `Messenger.Default.Register<WorldDidMoveEvent>(this, OnShift)`** if you need custom handling (e.g., shift not just transform but also a cached position field, a Verlet rope, decal positions, etc.). Use `Messenger.Default.Unregister(this)` in `OnDisable`. The vanilla template is `AvatarPrefab.WorldDidMove`.

**Common bug**: a GameObject *parented to nothing* (no parent in scene hierarchy) and *not registered with `WorldTransformerTargetList`* will *not* shift on origin re-center. After the first shift it will appear to teleport hundreds of meters relative to everything else. If you see a mod object suddenly fly off into the distance after several minutes of play, this is almost certainly the cause.

### "I'm caching a world-space position" — invalidate or shift on the event

If you cache `transform.position` in a field for later comparison, the cache becomes stale on origin shift. Either:
- Cache *game-space* (`.WorldToGame()`) and convert back when comparing, or
- Subscribe to `WorldDidMoveEvent` and offset your cache in the handler.

### "I'm sending a position over MP" — convert to game space

Always send `WorldTransformer.WorldToGame(...)` (or `.WorldToGame()` extension) on the send side; convert back with `GameToWorld` on receive. Vanilla `CharacterPositionTransmitter.SendIfConnected` (`CharacterPositionTransmitter.cs:41`) is the canonical example. **Do not assume the receiver's offset matches yours.**

### "I'm storing a position in KVO / saves" — game space

KVO keys hold game-space positions. Same convention as MP: convert before write, convert after read.

### "I want a callback on origin shift"

Either:
- `Messenger.Default.Register<WorldDidMoveEvent>(this, OnShift)` in `OnEnable` — safest, broad pattern.
- `WorldTransformer.TryGetShared(out var wt); wt.OnDidMove += OnShift;` — direct event, no Messenger overhead. Used by `CullingManager`, `SimpleCuller`, `TelegraphPoleManager`. Ensure `-=` on cleanup.

### "I want to force a re-origin now"

`WorldTransformer.TryGetShared(out var wt); wt.MoveNow();` — bypasses the debounce and the threshold check. Vanilla calls this from `CameraSelector.CameraJumped()` after every teleport.

### "I want a different threshold or delay"

`tileRange` and `tileSize` are public serialized fields; assign them at runtime if needed. `MoveDelay` is `private const float = 1f` — patch `ScheduleMoveWorldDelayed` if you need to change.

---

## Gotchas

- **GameObject not parented + not in `WorldTransformerTargetList` = teleports on shift.** Most common floating-origin bug. See [Patch points](#patch-points-for-mods) for the fix.
- **`WorldTransformer._currentOffset` is per-machine in MP.** Two clients have unrelated offsets. All MP wire data is game space; do not transmit world space.
- **`y` is never shifted.** Origin shifts move only XZ. World Y (terrain height, vehicle altitude) is stable across shifts.
- **The trigger is camera-only.** A train rolling 50 km away from the player does not cause an origin shift; only camera drift does. (This is fine in vanilla because gameplay forces the camera to follow the action.)
- **`CameraSelector.CameraJumped()` calls `MoveNow()`** after every teleport, bypassing debounce. This is why teleports don't accumulate origin drift.
- **`OnEnable` resets `_currentOffset = Vector3.zero`** — but does not `WorldTransformerTargetList.Targets.Clear()`. Targets registered before a re-enable will see a sudden one-shift jump back to game-space coordinates after the next shift if they don't reset. (In practice, scene reloads destroy and re-add the targets, so this is rarely an issue.)
- **`TranslateParticleSystems` finds *all* `ParticleSystem`s with `World` simulation space and individually offsets every live particle.** Cheap per-particle but allocates a `Particle[]` array sized to `maxParticles`. Mods spawning lots of `World`-sim systems pay this cost.
- **`WorldTransformer.OnDidMove` event AND `Messenger.Default.Send(WorldDidMoveEvent)` both fire.** Subscribers split between them (see [Subscriber catalog](#subscriber-catalog)). When patching, instrument both channels.
- **`Track.Graph.PositionRotation.Position` is game-space when produced from `Graph.GetPosition(Location)`** but world-space when produced from a raw Unity transform. Always check the producer. There is no namespace-level `Helpers.PositionRotation`; the type is in `Track.Graph`.
- **`MainCameraHelper.TryGetIfNeeded` reads `Camera.main`**, which scans the scene for the `MainCamera` tag the first time per frame. Caching via the `ref Camera` parameter avoids repeated scans. Use the pattern.
- **`CarCuller.GetSpherePosition` recomputes from `car.GetCenterPosition(graph).GameToWorld()`** every shift — this is correct, but means a `WorldDidMove` on a 200-car consist iterates the entire `_records` list. Hot enough that any mod doing similar bookkeeping should mirror the lazy/explicit-iter pattern, not subscribe-and-walk-everything.
- **`PrefabInstancer.WorldDidMove` mutates a `Matrix4x4[]` in place** for ties/tieplates and notifies GPUInstancer. If your mod uses GPUInstancer with World-space buffers, replicate the same translate-each-matrix pattern.
- **`DepthProjectorHelper.WorldDidMove` only refreshes a shader uniform** (`_DecalProjectorOriginY`); the decal projector's transform is moved by other means (it's typically parented to a car). Decals projected onto terrain rely on `_DecalProjectorOriginY` being a world-space Y baseline that survives shifts (since Y doesn't shift, this is just for absolute-Y decals).

---

## Cross-references

- Camera/jump pipeline that triggers immediate `MoveNow()` after teleport: see [Player & Camera › CameraSelector](player-camera.md#cameraselector-the-mode-switcher) and `_JumpToPoint`.
- `Track.Location` as the universal address that survives origin shifts trivially (purely game space): see [Track Topology › Location](track-topology.md#topology-spine-nodes--segments--graph).
- Car position propagation along consist sets and how `Car.WorldDidMove` fans out from `TrainController`: see [Consist Integration](consist-integration.md).
- Bardo / unloaded-tile car lifecycle (cars without Transforms don't need shifting): see [Cars & Cargo](cars-cargo.md).
- `MapManager` tile loading uses game-space queries: see `MapCameraUpdater.UpdateCoroutine` (`Cameras/MapCameraUpdater.cs:48`).
- MP message wire formats — all carry game-space positions: see [Player & Camera › Cross-cutting `Game.Messages`](player-camera.md#cross-cutting-gamemessages-playercamera-mp).
