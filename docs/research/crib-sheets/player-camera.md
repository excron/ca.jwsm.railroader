# Player & Camera — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Input & Keybinds](input-keybinds.md), [Multiplayer survey](../multiplayer-vanilla-survey.md)

Railroader has one *single* avatar/camera rig, owned by the local machine. There is no `Player` MonoBehaviour and no per-player `Transform` independent of the camera — the local player **is** the `CameraSelector`/`PlayerController`/`LocalAvatar` triple. `CameraSelector` is the global camera-mode switcher (FirstPerson/Strategy/Dispatcher); `PlayerController` is the first-person KCC-driven body; `LocalAvatar` is the visual `AvatarPrefab` shown only to *other* cameras (third-person view). Remote multiplayer players appear as `RemoteAvatar` instances driven entirely by `UpdateCharacterPosition` messages — they have no controller/physics. Teleport, seat-jumping, and follow-car are all camera-side operations that route through `CameraSelector.JumpCharacterTo` / `_JumpToPoint`.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `CameraSelector` (singleton `shared`) | `CameraSelector.cs:29` | Camera mode switching, teleport, seat jump, MP camera-pos broadcast |
| `Character.PlayerController` | `Character/PlayerController.cs:19` | First-person body wrapper. Implements `ICameraSelectable` |
| `Character.CharacterController` | `Character/CharacterController.cs:16` | KCC implementation. State machine: Default/Seated/Ladder |
| `Character.LocalAvatar` | `Character/LocalAvatar.cs:8` | Spawns the `AvatarPrefab` for the local player so other camera modes can see it |
| `Cameras.StrategyCameraController` | `Cameras/StrategyCameraController.cs:13` | Free-fly / follow-car overhead camera |
| `Cameras.MouseLookInput` | `Cameras/MouseLookInput.cs:7` | RMB-hold or toggle mouse-look. Pitch/Yaw shared by all camera modes |
| `Cameras.JumpTarget` (struct) | `Cameras/JumpTarget.cs:7` | Teleport descriptor: world or car-relative position+rotation |
| `Avatar.RemoteAvatar` | `Avatar/RemoteAvatar.cs:12` | Network-driven remote-player avatar. 300-tick interp delay + extrapolation |
| `Game.IPlayer` / `Game.LocalPlayer` | `Game/IPlayer.cs`, `Game/LocalPlayer.cs` | Local player handle. `GamePosition` reads `localAvatar.character.GroundPosition` |
| `Game.Messages.UpdateCharacterPosition` | `Game.Messages/UpdateCharacterPosition.cs` | Position+pose+velocity broadcast (Passenger access) |
| `Game.Messages.UpdateCameraPosition` | `Game.Messages/UpdateCameraPosition.cs` | Camera-only position (used for prox checks like `IsPlayerCameraNear`) |

---

## Camera mode spine

```
CameraSelector.shared._currentCamera : ICameraSelectable
   ├── CameraIdentifier.FirstPerson  → PlayerController.character (KCC body) + CharacterCameraController (FOV/pitch/yaw)
   ├── CameraIdentifier.Strategy     → StrategyCameraController   (free-fly orbit, optional FollowCar)
   └── CameraIdentifier.Dispatcher   → external dispatcher        (third-party-set via SetCamera; vanilla=null)
                  │
                  ▼
          UpdateCurrentCamera()  (CameraSelector.cs:480)
            • cameraSelectable.SetSelected(false, null) on old
            • _camera.transform.SetParent(newContainer, worldPositionStays:false)
            • cameraSelectable.SetSelected(true, _camera) on new
            • localAvatar.CurrentCameraDidChange()  ← shows/hides 3rd-person avatar
```

Only **one** `Camera` exists in the scene — the same camera object is reparented under whichever `ICameraSelectable.CameraContainer` is active. There is no separate first-person vs. overhead camera; mode switch = reparent.

### `ICameraSelectable` interface

```csharp
public interface ICameraSelectable {                          // ICameraSelectable.cs
    GameObject gameObject     { get; }
    Transform  CameraContainer{ get; }
    Vector3    GroundPosition { get; }
    void       SetSelected(bool selected, Camera camera);
}
```

Implementers: `PlayerController`, `StrategyCameraController`, `DroneController`, `StationaryCameraController`, plus any external dispatcher set via `CameraSelector.SetCamera(CameraIdentifier.Dispatcher, …)`.

---

## `CameraSelector` (the mode switcher)

Singleton: `CameraSelector.shared` (set in `Awake`, `CameraSelector.cs:128`). Owns the **only** `LocalAvatar` (added as a sibling component in `Awake`, `CameraSelector.cs:130`).

### Public surface

```csharp
public  PlayerController          character;                  // 51  (the FP body, serialized in inspector)
public  ICameraSelectable         dispatcher;                 // 53  (optional 3rd cam impl)
public  DroneController           drone;                      // 55  (debug fly cam, NOT in mode enum)
public  StrategyCameraController  strategyCamera;             // 57

public  CameraIdentifier          CurrentCameraIdentifier   { get; }   // 101
public  bool                      CurrentCameraIsFirstPerson{ get; }   // 105
public  Vector3                   CurrentCameraPosition     { get; }   // 87  (game space)
public  Vector3                   CurrentCameraGroundPosition{ get; }  // 99
public  LocalAvatar               localAvatar               { get; }   // 107
public  PositionRotation          DefaultSpawn              { get; set; }  // 109

public  bool   SelectCamera(CameraIdentifier);                // 244
public  void   SetCamera(CameraIdentifier, ICameraSelectable);// 742  (only Dispatcher allowed)
public  void   JumpToCar(Car, Vector3 relPos, Quaternion);    // 379  → coroutine, FP cam attach
public  void   JumpCharacterTo(Vector3, string carId, Vector3 look); // 363
public  void   JumpCharacterTo(JumpTarget);                   // 540  → _JumpToPoint, FP
public  void   JumpToPoint(Vector3 game, Quaternion, CameraIdentifier?);  // 551
public  void   JumpToSpawn();                                 // 473  (default SpawnPoint)
public  void   ZoomToCar(Car, bool select=true);              // 513
public  void   ZoomToTransform(Transform);                    // 522
public  void   ZoomToPoint(Vector3 gamePos);                  // 528
public  void   FollowCar(Car);                                // 534
public  void   MoveStrategyToPoint(Vector3, Quaternion?);     // 546
public  void   WillDestroyCar(Car);                           // 666
public  void   JumpTo(IIndustryTrackDisplayable);             // 697
internal void  FollowTrack(Location, int speed);              // 710
```

`JumpCharacterTo` and `JumpToPoint` go through `_JumpToPoint` coroutine (`CameraSelector.cs:606`) which:
1. Caches the request in `_pendingJump` (cancels redundant jumps).
2. Resolves the `JumpTarget` to a world position.
3. `MapManager.RequestPriorityLoad(...)` — waits for terrain tiles.
4. Optional `ProgressIndicator.Show("Loading...", 0.18f)` if load took >150ms.
5. Selects the target camera identifier.
6. `WaitForFixedUpdate` if relative-to-car (so the car has its current motion snapshot).
7. For FirstPerson: zeros `BaseVelocity`, calls `character.JumpTo(pos, rot)`, then if relative-to-car `character.AttachTo(car, pos, rot)`.
8. For Strategy: `SelectedStrategyCamera.JumpTo(worldPos.WorldToGame())`.
9. Fires global reflection probe re-render via `EnviroManager.instance.Reflections.RenderGlobalReflectionProbe(forced:true)`.

### Update loops

```csharp
private void Update() {                                       // 145
    if (MainCameraHelper.TryGetIfNeeded(ref _camera)) {
        if (GameInput.MovementInputEnabled) {
            InputChangeCamera();      // F-keys for camera mode
            InputJumpCamera();        // jump-to-seat, follow head/tail, teleport, place flare
        }
        SendCameraPositionIfNeeded();
    }
}

private void FixedUpdate() {                                  // 158
    if (MainCameraHelper.TryGetIfNeeded(ref _camera)) UpdateDopplerForCameraMovement();
}
```

### Key inputs read in `Update`

`InputChangeCamera` (`CameraSelector.cs:209`) reads from `GameInput.shared`:

| `GameInput` property | Action | Result |
|---|---|---|
| `CameraSelectFirstPerson` | F1-ish | switch to `FirstPerson` |
| `CameraSelectStrategy` | F2-ish | switch to `Strategy` |
| `CameraSelectDispatcher` | F3-ish | switch to `Dispatcher` |
| `CameraJumpStrategyToAvatar` | (binds elsewhere) | switch to `Strategy` and `MoveStrategyToPoint` to local avatar |

`InputJumpCamera` (`CameraSelector.cs:256`):

| `GameInput` property | Result |
|---|---|
| `CameraJumpToSeat` | `JumpToSeat()` — find best `Seat` near selected car, sit in FP |
| `CameraJumpToHead` / `Tail` | `JumpToCarRelative(...)` — step `StrategyCameraController.FollowCar` along consist |
| `CameraFollowHead` / `Tail` | `JumpToCar(...)` — set `FollowCar` to head/tail of consist |
| `Teleport` | `TeleportToMouse()` — raycast from camera to mouse, FP-jump to terrain hit |
| `PlaceFlare` | `FlareManager.Shared.PlaceFlare(_camera)` |

### `TeleportToMouse` raycasting layers (`CameraSelector.cs:442`)

1. First raycasts at distance **2m** for `Layers.Clickable` looking for `CTCPanelGroup` — special case to teleport to the first switch.
2. Then raycasts at **2000m** for `Layers.Terrain`. Hit → `character.JumpTo(point, look)` and switches to FirstPerson with a 0.1s `LeanTween.delayedCall`.
3. Miss → `Toast.Present("Nothing in range.")`.

### MP camera position transmit

```csharp
private void SendCameraPositionIfNeeded() {                   // 166
    Vector3 pos = WorldTransformer.WorldToGame(_camera.transform.position);
    if ((Vector3.Distance(pos, _lastSentCameraPosition) > 50f || timeDelta > 10f) && PlayersManager.PlayerId.IsValid)
        StateManager.ApplyLocal(new UpdateCameraPosition(pos));   // Passenger-level msg
}
```

The host's `PlayersManager.UpdateCameraPosition(pos, sender)` (`PlayersManager.cs:471`) caches per-player `PlayerCameraPosition(pos, time)` in `_lastKnownPositions`. Used by `IsPlayerCameraNear(transform, radius)` (`PlayersManager.cs:476`) for proximity gating (e.g. animation/audio activation budget). **Stale after 60s** — `unscaledTime - playerCameraPosition2.Time > 60f`.

### Doppler

`UpdateDopplerForCameraMovement` (`CameraSelector.cs:679`) measures camera world-velocity per-frame and feeds `VirtualAudioSourcePool.SetGlobalDopplerLevel`. Curve in `Config.cameraVelocityToDoppler`. Smoothed by `dopplerDeltaIncreasing`/`dopplerDeltaDecreasing`.

### Patch candidates

| Method | Why patch |
|---|---|
| `CameraSelector.SelectCamera(CameraIdentifier)` | Veto/log camera switches; e.g. force first-person during a custom mode. |
| `CameraSelector.UpdateCurrentCamera` | Hook the moment the camera reparents — e.g. swap post-processing per mode, attach overlay UI. |
| `CameraSelector._JumpToPoint` | Intercept teleports; add reachability checks, side-effects on arrival. Coroutine, can't easily prefix-cancel — patch `JumpToPoint`/`JumpCharacterTo` callers. |
| `CameraSelector.TeleportToMouse` | Custom teleport rules (different layer, range, requirement). |
| `CameraSelector.InputChangeCamera` / `InputJumpCamera` | Add new camera-mode bindings without subclassing GameInput. |
| `CameraSelector.SetCamera(CameraIdentifier, ICameraSelectable)` | **Public hook** — swap in your own dispatcher implementation. (Throws for FirstPerson/Strategy.) |
| `CameraSelector.SendCameraPositionIfNeeded` | Tune the 50m/10s heartbeat or emit additional MP messages. |

### Gotchas

- **`character` and `strategyCamera` are SerializeField-injected**, set up in the prefab. Cannot be reassigned at runtime safely — `UpdateCurrentCamera` calls `SetSelected` on the cached objects.
- **`drone` is wired but NOT in the `CameraIdentifier` enum.** Vanilla never selects it. It's a `MonoBehaviour, ICameraSelectable` you can swap into the dispatcher slot, or call directly via Harmony. See [DroneController](#dronecontroller).
- **Dispatcher slot is intentionally extensible** — `SetCamera(Dispatcher, impl)` is the public injection point; CameraSelector.cs:744. Dispatcher mods (e.g. dispatcher panel) install an `ICameraSelectable` here.
- **Default spawn is found lazily** via `SpawnPoint.Default = SpawnPoint.All.FirstOrDefault()` (sorted by `priority` descending). Mods adding spawn points should set `priority` higher than vanilla's 0 to override.
- **`HandleMapDidLoad` fast-paths to `JumpToSpawn` only when both `relativeToCar==null` and the snapshot's Position is `Vector3.zero`.** Loading a save with a stored character position skips spawn entirely — `_JumpToPoint` was already invoked via the persistence path.
- **`_pendingJump` deduplication**: rapid identical jump requests (same target + camera) are no-ops via `PendingJumpEquals` (`CameraSelector.cs:565`). If your mod reissues a jump after a state change, mutate the target in some way to force a re-run.
- **`JumpToPoint(Dispatcher)` throws** — only Strategy/FirstPerson are valid jump targets.

---

## `Character.PlayerController` (first-person body wrapper)

Wraps a Kinematic Character Controller (KCC) `CharacterController`. Owns:
- `MouseLookInput _mouseLookInput` (`AddComponent`'d in `Awake`, line 69)
- `CharacterPositionTransmitter _transmitter` (line 68 — sends MP position updates)
- `CharacterCameraController cameraController` (FOV/pitch/yaw)

### Camera container hierarchy

`PlayerController.cameraContainer` is the `Transform` the main camera is parented under in FP mode. Eye height comes from `CharacterController.eyeHeightStanding=1.55f` (or `eyeHeightSeated=1.25f`); see `CharacterController.AnimateCameraContainerPosition` (`CharacterController.cs:671`).

### Input pipeline (per-frame, Update)

```csharp
private void Update() {                                       // PlayerController.cs:86
    _mouseLookInput.UpdateInput(_isSelected);
    HandleCharacterInput();
}

private void LateUpdate() {                                   // PlayerController.cs:92
    if (GameInput.MovementInputEnabled && _isSelected) {
        float yaw = (_characterInputs.Lean == Lean.Off) ? 0f : _mouseLookInput.Yaw;  // sideways look only when leaning
        cameraController.UpdateWithInput(dt, _mouseLookInput.Pitch, inputYaw, ZoomDelta, InputResetFOV, _characterInputs.Lean);
        if (_transmitter != null) _transmitter.SendIfConnected(GetPoseFromState());
    }
}
```

`HandleCharacterInput` (`PlayerController.cs:146`) builds a `PlayerCharacterInputs` struct from `GameInput.shared` (move vector, jump, crouch, lean, run modifier) and feeds `character.SetInputs(ref inputs)` (KCC). When `lean` releases (`character.Lean != Off → Off`), the controller does a body-rotation snap so the upper-body lean rotation transfers to the motor's main rotation (lines 170-184).

### Public methods

```csharp
public  Vector3 GroundPosition          { get; }              // 48 (motor.TransientPosition)
public  bool    IsOnGround              { get; }              // 50
public  Transform CameraContainer       => cameraContainer;   // 46
public  void    JumpToCar(Car);                               // 194  (3m to the side, faces car)
public  void    JumpTo(Vector3, Quaternion);                  // 203
public  void    AttachTo(Car, Vector3, Quaternion);           // 211  (riding the car's PhysicsMover.Rigidbody)
public  void    Sit(Seat);                                    // 247
public  void    SetRotation(Quaternion);                      // 257
public  void    WillDestroyCar(Car);                          // 263
public  bool    IsOnCar(Car);                                 // 271
internal Car    GetRelativeCar();                             // 277  (rigidbody parent walk)
public  (MotionSnapshot, Car) GetRelativePositionRotation();  // 287
```

### `AttachTo` (boarding a moving car)

```csharp
public void AttachTo(Car car, Vector3 worldPosition, Quaternion rotation) {
    PhysicsMover pm = car.GetComponentInChildren<PhysicsMover>();
    Rigidbody rb = pm.Rigidbody;
    character.motor.ApplyState(new KinematicCharacterMotorState {
        AttachedRigidbody = rb,
        AttachedRigidbodyVelocity = rb.velocity,
        BaseVelocity = Vector3.zero,
        GroundingStatus = new CharacterTransientGroundingReport { … IsStableOnGround=true … },
        Position = worldPosition,
        Rotation = rotation,
    });
}
```

This is how "ride a moving train" works without sliding off — the KCC's `AttachedRigidbody` makes the motor inherit the car's velocity, so the player stays still relative to the car. `GetRelativeCar` (line 277) inverts: it walks up from `motor.AttachedRigidbody` to find the `Car`. `_attachedCarId` is recomputed every 0.5s by the `AttachedCarChecker` coroutine (line 303), which also `ModelLoadRetain`s the attached car (so it doesn't unload underneath you) and tells `MapManager.KeepLoaded` your tile when you're on terrain.

### Stuck-below-terrain rescue

`CheckForTerrainBelow` (`PlayerController.cs:336`) runs from `AttachedCarChecker`. If no terrain is within 50m below the player, it tries five candidate positions (current + ±100m in each cardinal). If still nothing, falls back to `FixPlayerPositionNoTileData` which jumps to the closest `SpawnPoint`. **This is the de facto "anti-fall-through-world" recovery.**

### Patch candidates

| Method | Why patch |
|---|---|
| `PlayerController.HandleCharacterInput` | Filter or augment per-frame inputs (e.g., locked controls, AI driving). |
| `PlayerController.AttachTo(Car, ...)` | Hook for "boarded a moving car" — fire your own event, swap collision logic. |
| `PlayerController.JumpTo(Vector3, Quaternion)` | Catches all teleports that land in FP. **Used by `_JumpToPoint`, `TeleportToMouse`, `JumpToSpawn`, terrain-rescue.** |
| `PlayerController.GetRelativeCar` | Override to use a different ride-detection (e.g. trailers). |
| `PlayerController.SetSelected` | Camera-mode change for FP — toggle FP-only HUD/effects here. |
| `PlayerController.LateUpdate` | Add custom camera shake / sway / lean overlays — runs after `cameraController.UpdateWithInput`. |

### MP authority

- **Local-only**. The avatar/body is unauthenticated client-side simulation. The host doesn't move *your* character.
- Position is broadcast via `CharacterPositionTransmitter.SendIfConnected` (`Character/CharacterPositionTransmitter.cs:23`), throttled at 100ms minimum / 2000ms force-send (line 35-37) and gated by deltas: position >0.05m, forward/look >0.1, velocity >0.01 m/s, or pose/car change.
- Sent message: `UpdateCharacterPosition` (`MinimumAccessLevel(AccessLevel.Passenger)`) — see [`Game.Messages` table](#cross-cutting-game-messages).
- Host re-broadcasts via `StateManager.PropagateGameMessage` → other clients see it as a `RemotePlayer` `UpdateAvatarPosition` call (`StateManager.cs:1045`).
- Anti-cheat: none — clients self-report position. Host *does* track each player's *camera* position (`UpdateCameraPosition`, `PlayersManager.cs:471`) but only for proximity queries, not validation.

---

## `Character.CharacterController` (KCC core)

The actual `KinematicCharacterController.ICharacterController` implementation. **Naming clash** with Unity's built-in `UnityEngine.CharacterController` — Railroader's is a *different* type. Always reference by full namespace.

### State machine

```csharp
public enum CharacterState { Default, Seated, Ladder }        // CharacterState.cs
private enum AttachState   { Anchoring, Stable, Deanchoring } // CharacterController.cs:18 (private)
```

Transitions: `TransitionToState(CharacterState)` (line 226). On enter Seated/Ladder, the motor's collision/grounding solving is **disabled** (`SetMovementCollisionsSolvingActivation(false)`, `SetGroundSolvingActivation(false)`); on exit, re-enabled.

`Anchoring` is a 0.15s eased blend (`AnchoringDuration = 0.15f`, line 116) into the seat's `FootPosition` or the ladder's local position. During `AttachState.Anchoring`, the camera is also pulled from standing eye height (1.55) to seated (1.25) by `AnimateCameraContainerPosition` (`CharacterController.cs:671`) — `LeanTween.moveLocal` on the `cameraContainer`.

### Tunables (Inspector-set)

```csharp
public float maxStableMoveSpeed   = 10f;                      // 28
public float runMoveSpeed         = 40f;                      // 38
public float maxAirMoveSpeed      = 15f;                      // 41
public float airAccelerationSpeed = 15f;                      // 43
public float drag                 = 0.1f;                     // 45
public float jumpUpSpeed          = 10f;                      // 50
public float jumpScalableForwardSpeed = 10f;                  // 52
public float ladderSpeedNormal    = 2f;                       // 59
public float ladderSpeedFast      = 4f;                       // 61
public Vector3 gravity = (0, -30, 0);                         // 66
public float crouchedCapsuleHeight = 1f;                      // 70
public float maintainUpDegreesPerSecond = 0.001f;             // 72
public float eyeHeightStanding = 1.55f;                       // 118
public float eyeHeightSeated   = 1.25f;                       // 120
public float leanDistance = 0.5f;                             // 132
```

### KCC ICharacterController impl

```csharp
public void BeforeCharacterUpdate(float dt);
public void UpdateRotation(ref Quaternion currentRotation, float dt);   // 466
public void UpdateVelocity(ref Vector3 currentVelocity, float dt);      // 473  ← motion math here
public void AfterCharacterUpdate(float dt);                              // 587
public void PostGroundingUpdate(float dt);                               // 693
public bool IsColliderValidForCollisions(Collider);                      // 705
public void OnGroundHit(...)                                              // 718  (no-op)
public void OnMovementHit(...)                                            // 722  (no-op)
public void ProcessHitStabilityReport(...)                                // 734  (no-op)
public void OnDiscreteCollisionDetected(Collider);                        // 746  (no-op)
```

`OnLanded` and `OnLeaveStableGround` are **`protected`** no-op methods (lines 738, 742) — visibly named hooks for subclasses, but they don't exist in vanilla. Override via `[HarmonyPatch]` to add landing FX.

### Sit / Ladder

```csharp
public void Sit(Seat seat, bool immediate);                   // 750
private void GrabLadder(Ladder ladder, bool immediate);       // 769
public void UnsitUnladder();                                  // 813
public void CarWillBeDestroyed();                             // 808
public Action OnSeatDidChange;                                // 141
public Action OnLadderDidChange;                              // 143
```

`Sit` and `GrabLadder` are mutually exclusive — entering one immediately ungrabs the other. On entering Seated/Ladder, `motor.AttachedRigidbodyOverride` is set to the seat's/ladder's `PhysicsMover.Rigidbody` (so the player rides the car).

### Patch candidates

| Method | Why patch |
|---|---|
| `CharacterController.SetInputs(ref PlayerCharacterInputs)` | Modify movement intents at the boundary between player input and the KCC. |
| `CharacterController.UpdateVelocity` | Custom physics — different gravity, swimming, climbing. Hot-path; prefer `airAccelerationSpeed`/`runMoveSpeed` field swaps if possible. |
| `CharacterController.OnLanded` / `OnLeaveStableGround` | Empty in vanilla — patch to add fall damage, footsteps, dust, etc. |
| `CharacterController.Sit(Seat, bool)` | Veto seat entry, override seat preference. |
| `CharacterController.CheckForLadderOrSeat` | Modify the auto-grab logic when overlapping a ladder/seat collider. |
| `CharacterController.AnimateCameraContainerPosition` | Custom eye-height curves (different per-pose). |

### Gotchas

- **Naming clash with `UnityEngine.CharacterController`.** ALL references to "CharacterController" in this codebase mean `Character.CharacterController` (Railroader's KCC wrapper). Watch your usings.
- **`character.Lean` (Lean enum) blocks normal yaw**: when leaning Left/Right, mouse-look yaw routes to a sideways camera offset instead of body rotation (PlayerController.cs:96, 162-163). Releasing lean does a one-shot rotation snap (PlayerController.cs:170-184).
- **Lean is gated by `GetRelativeCar() != null`** (`PlayerController.cs:135`): you cannot lean while not on a car. This is checked at the *first* lean key press; once leaning, you can stay leaned even if you fall off.
- **Movement inputs zero when `!_isSelected`** — `inputs = default` if camera isn't FP. Switching out of FP mid-jump leaves the body in whatever state the KCC was in (typically air-falling).
- **`maintainUpDegreesPerSecond = 0.001f`**: the KCC actively snaps your body upright at a glacial rate. Effectively prevents tilt; doesn't allow walking on walls.
- **Crouch un-crouches only when collider check is clear** (`CharacterController.cs:608-619`). Sliding under a low-clearance object you can't stand up under works correctly.
- **Ladder direction-change sticky**: once on a ladder, `LadderStickyRemaining = 0.25 - _ladderDuration` keeps you attached for 0.25s even with no input. Useful for one-handed grabs.
- **"Ladder exit bump"**: jumping off the top of a ladder applies `Config.Shared.ladderExitBump` velocity (`CharacterController.cs:415`) — not a tunable per ladder.
- **`gravity` is on the controller, not global**. If you turn off gravity, you turn off only this character's gravity — not other physics objects.

---

## `Character.CharacterCameraController` (FP camera FOV / pitch / zoom)

Per-instance helper added by `PlayerController.cameraController` SerializeField. Drives camera local-rotation (pitch+yaw) and FOV.

```csharp
public float minVerticalAngle  = -90f;                        // 14
public float maxVerticalAngle  =  90f;                        // 17
public float rotationSpeed     = 1f;                          // 19
public float rotationSharpness = 10000f;                      // 21
private float _targetFieldOfView;
private static float DefaultFOV     => Preferences.DefaultFOV;       // 38 (key "ui.fov0", default 40)
private static float DefaultWideFOV => Preferences.AlternateFOV;     // 40 (key "ui.fov1", default 80)

public void Configure(float initialYaw);                      // 54
public void UpdateWithInput(float dt, float pitch, float yaw, float zoom, bool resetZoom, Lean lean); // 59
public void SetRotation(Quaternion);                          // 75
public void SetSelected(bool selected, Camera);               // 112
```

`UpdateZoom` (line 81): `_targetFieldOfView -= zoom*8` clamped 4..120; `inputResetFOV` toggles between `DefaultFOV` and `DefaultWideFOV` (binary swap based on which is closer). FOV interpolates with `Mathf.Lerp(_camera.fieldOfView, _targetFieldOfView, Time.deltaTime * 10f)`.

When `lean` changes, `_targetYaw = 0` (line 63) so the camera snaps yaw-relative-to-body.

---

## `Cameras.MouseLookInput` (mouse → pitch/yaw)

`AddComponent`-attached by both `PlayerController` and `StationaryCameraController`. Reads `Input.GetAxisRaw("Mouse X"/"Y")` via `GameInput.shared.LookDelta` (legacy axes — see [Input crib sheet → Legacy Input](input-keybinds.md#legacy-unityengineinput-still-used-for-mouse-axes)).

```csharp
public float Pitch { get; private set; }                      // 13
public float Yaw   { get; private set; }                      // 15
public  void UpdateInput(bool selected);                      // 30
public  void SetMouseMovesCamera(bool);                       // 68 (Cursor.lockState)
```

Two activation modes (`Preferences.MouseLookToggle`):
- **Hold mode** (default): RMB long-press begins, release ends. Uses `GameInput.SecondaryLongPressBeganThisFrame` / `…EndedThisFrame`.
- **Toggle mode**: short RMB press toggles. Uses `GameInput.MouseLookToggle`.

Smoothing: 20Hz exp filter, `t = 1 - exp(-20 * dt)`. Speed multiplier: `Preferences.MouseLookSpeed` (PlayerPrefs key `camera.look.speed`, default 1).

Inversion: `Preferences.MouseLookInvert` (key `camera.look.invert`).

---

## `Cameras.StrategyCameraController` (free-fly / follow-car)

Orbit camera around `_targetPosition`. Two modes:
- **Free**: `FollowCar == null`, target is a free-roaming ground point (snapped by `SnapToGround`).
- **Follow**: `FollowCar != null`, target tracks `FollowCar.GetMoverTargetPositionRotation()` and inherits the car's yaw rotation (with smoothing).

### Public surface

```csharp
public  Car  FollowCar               { get; set; }            // 108
public  void JumpToCar(Car);                                  // 427
public  void JumpTo(Vector3 game, Quaternion?);               // 434
public  Transform CameraContainer    => transform;            // 104
public  Vector3   GroundPosition     => _targetPosition;      // 106
public  void SetSelected(bool, Camera);                       // 417
```

### Tunables

```csharp
public float normalSpeed      = 1f;                           // 15
public float fastSpeed        = 3f;                           // 17
public float fasterSpeed      = 10f;                          // 19
public float zoomSpeed        = 10f;                          // 21
public float targetHeightFollow = 2.5f;                       // 24
public float targetHeightFree   = 1.25f;                      // 26
public AnimationCurve distanceToSpeed;                        // 28
public AnimationCurve zoomMultiplier;                         // 30
public float zoomAngleSpeed   = 50f;                          // 32 (private serialized)
public float zoomAngleYSpeed  = 15f;                          // 34 (private serialized)
[Range(0,1)] public float snapToGroundResponsiveness = 0.04f; // 96
private float _distance = 40f;                                // 38 (default zoom)
private float _angleX   = 20f;                                // 40 (pitch)
private float _angleY   = 45f;                                // 43 (yaw)
```

`_distance` clamps 1..500; `_angleX` clamps -30..90 (`UpdateCameraPosition` lines 310-311).

### Input handling (`UpdateInput`, line 197)

- **Movement**: `GameInput.shared.GetMovement(normal, fast, faster)` — WASD + run/very-fast modifier.
- **LMB drag**: world-space pan via `_panStartPosition` raycast onto an `XZ` plane through the click point. Drops `FollowCar` if drag distance > 1m.
- **RMB drag**: orbit. `_angleYInput = -delta.x * 0.85f`, `_angleXInput = delta.y * 0.5f` (negated by `Preferences.MouseLookInvert`).
- **Mouse scroll**: `_distanceInput = -mouseScrollDelta.y` (gated by `IsMouseOverGameWindow`).

**Note:** mouse buttons read directly from legacy `Input.GetMouseButton(0)` / `Input.GetMouseButton(1)` here (`StrategyCameraController.cs:209-247`). Not routed through the new InputSystem.

### Ground snapping

`SnapToGround` (line 377) raycasts down 4000m for `Layers.Terrain | Water | Track`. `MoveAboveGround` (line 317) keeps the camera above ground by adding `_extraHeightForGround` (lerps back to 0 over 5*dt).

### Patch candidates

| Method | Why patch |
|---|---|
| `StrategyCameraController.UpdateInput` | Custom inputs (e.g. WASD-pan disabled, gamepad overlay). |
| `StrategyCameraController.UpdateCameraPosition` | Custom orbit math — different distance curves, fixed pitch, anchor offsets. |
| `StrategyCameraController.JumpToCar` / `JumpTo` | Hook for "camera moved" event. |
| `StrategyCameraController.SnapToGround` | Override target snap (e.g. allow above-track). |
| `StrategyCameraController.FollowCar` setter | Hook for follow-car start/stop. |

### Gotchas

- **`FixedUpdate` runs the camera move via `LateFixedUpdateLoop` coroutine** (line 165), not via `LateUpdate`. The actual `Rigidbody.MovePosition`/`MoveRotation` is in `LateFixedUpdate`, called from `WaitForFixedUpdate`. This is unusual — gives a fixed-tick-aligned camera that follows physics objects without jitter.
- **Pan drops `FollowCar` only if distance > 1m** (line 233). Small accidental clicks won't unfollow.
- **Pan uses raycast to terrain only** (line 403, `1 << Layers.Terrain`). Trying to pan starting on a building/object → `RayPointFromMouse` fails, no pan.
- **`UpdateCameraPosition` calls `_targetHeight = targetHeightFree` unconditionally** (line 266). The serialized `targetHeightFollow` is dead — only `targetHeightFree` is used. Likely a bug; mods relying on `targetHeightFollow` won't work without patching.
- **Right-click drag rotation respects `Preferences.MouseLookInvert`** but uses a separate constant (0.85x/0.5x). Not the same speed as FP mouse-look.
- **`_extraRotationY` accumulates car yaw drift** with a 10° threshold for snap-vs-lerp. Long-running follow on a curving train gradually rotates the camera — by design.

---

## `DroneController` (debug fly cam)

Wired to `CameraSelector.drone` but **not** in the camera identifier enum, so it's never auto-selected. Reads `Input.GetAxis("Horizontal"/"Vertical")` (legacy axes) and `Input.GetKey(KeyCode.E/Q)` directly. Fast-fly speeds: 10 / 100 / 500 m/s.

```csharp
public class DroneController : MonoBehaviour, ICameraSelectable {  // DroneController.cs
    [SerializeField] private Transform cameraTransform;
    public Transform CameraContainer  => cameraTransform;
    public Vector3   GroundPosition   => cameraTransform.position;
    private void FixedUpdate();
    public void SetSelected(bool, Camera);
    public void JumpToCar(Car);                                  // logs "not implemented"
}
```

To enable: `CameraSelector.shared.SetCamera(Dispatcher, dronInstance)` then `SelectCamera(Dispatcher)`. Or call from a debug command. Useful as a starting point for custom fly cameras.

---

## `Cameras.StationaryCameraController` (fixed-position cam)

Camera stuck to a `Transform`. Used for in-world fixed cameras (probably stations / scripted moments). Reading move input causes it to jump the *character* to its position with `CameraSelector.shared.JumpCharacterTo(...)` — i.e., stationary cams are de-facto teleport pads when the player presses move.

```csharp
public class StationaryCameraController : MonoBehaviour, ICameraSelectable {  // StationaryCameraController.cs
    public Transform CameraContainer => transform;
    public Vector3   GroundPosition  => transform.position;
    private void Update();      // jumps character if move pressed
    private void LateUpdate();  // mouse-look update
    public void SetSelected(bool, Camera);
}
```

Owns its own `MouseLookInput` and `CharacterCameraController` — same FP rig as `PlayerController` but body-less.

---

## `Character.LocalAvatar` (visible avatar for the local player)

Sibling component on the same GameObject as `CameraSelector`, added at runtime in `CameraSelector.Awake` (`CameraSelector.cs:130`). Spawns an `AvatarPrefab` lazily — *only* if `CurrentCameraIsFirstPerson == false` (so other-camera viewers can see your body).

```csharp
public class LocalAvatar : MonoBehaviour {                    // LocalAvatar.cs
    public  PlayerController character;                       // wired by CameraSelector.Awake
    public  Vector3          lanternOffset = (-0.34, 0.4, 0.15);
    private AvatarPrefab     _avatar;
    public  AvatarPose       Pose          { get; set; }
    public  bool             LanternEnabled{ get; set; }
    private static bool      showAvatar    => !CameraSelector.shared.CurrentCameraIsFirstPerson;

    private void FixedUpdate();                                // moves rigidbody to char pos
    public  void CurrentCameraDidChange();                     // hide/show avatar
    public  void SetAvatarCustomization(AvatarDescriptor);     // change appearance
    public  void SetSeat(bool seat, bool ladder);              // sit/stand/ladder pose
}
```

**Lantern:** `LanternEnabled` is a GameObject SetActive on `_avatar.lantern`. Toggling sends an `AddUpdateCharacter` MP message via `Multiplayer.Client?.SendCharacter()` (called from `GameInput.Update` line 661 when the toggle-lantern key fires). The serialized `lanternOffset` is unused in vanilla code — set on the prefab but never read here. (`LanternController.cs` reads its own KVO key `lantern.0`; that's a *separate* lantern system on the locomotive.)

### `AvatarPose` enum

```csharp
public enum AvatarPose { Stand, Sit, Jump, Ladder }           // Avatar/AvatarPose.cs
public enum CharacterPose { Stand, Sit, Jump, Ladder }        // Game.Messages/CharacterPose.cs (mirrors AvatarPose for wire)
```

`PlayerController.GetPoseFromState` (`PlayerController.cs:108`) derives the pose from KCC state.

---

## `Avatar.AvatarPrefab` & `AvatarManager`

```csharp
public class AvatarPrefab : MonoBehaviour {                   // AvatarPrefab.cs
    public GameObject lantern;
    public AvatarCustomization Customization { get; }
    public AvatarAnimator Animator => Customization.Animator;
    public AvatarPickable Pickable { get; }
    public Rigidbody     Rigidbody { get; }                   // kinematic, added in Awake
    public MapIcon       MapIcon   { get; }
    public void SetAvatarVisible(bool show);                  // toggles renderer + pickable
}

public class AvatarManager : MonoBehaviour {                  // AvatarManager.cs
    public static  AvatarManager Instance { get; }
    [SerializeField] private AvatarPrefab avatarPrefab;
    public  AvatarPrefab AddAvatar(AvatarDescriptor, bool showMapIcon, PlayerId, string playerName);
    public  RemoteAvatar AddRemote(PlayerId, string playerName);
    public  void  RemoveAvatar(AvatarPrefab);
    public  void  RemoveAvatar(RemoteAvatar);
    public  bool  RemoteAvatarNear(Vector3 position);          // <0.5m away
}
```

`RemoteAvatarNear(pos)` is used in seat-finding to avoid sitting on top of another player (`CameraSelector.FindBestSeat`, `CameraSelector.cs:417`).

---

## `Avatar.RemoteAvatar` (other players' bodies, MP)

Per-remote-player MonoBehaviour. Driven *only* by network messages — no local controller, no physics integration. Display is delayed-interpolated.

```csharp
public class RemoteAvatar : MonoBehaviour {                   // RemoteAvatar.cs
    private const long Delay = 300L;                          // 35 — display 300 ticks behind
    private readonly CircularBuffer<Frame> _frames = new(4);  // 37 — 4-deep frame buffer
    public AvatarPrefab avatar;
    private static long DisplayTick => StateManager.Now - 300;
    public  void AddPosition(long tick, Vector3 position, Vector3 forward, Vector3 look,
                              Vector3 velocity, string relativeToCarId, AvatarPose pose);
}
```

### Interpolation pipeline

1. `RemotePlayer.UpdateAvatarPosition` (called from `StateManager` when an `UpdateCharacterPosition` arrives) → `_avatar.AddPosition(...)` → enqueue.
2. `FixedUpdate` → `UpdatePosition` (line 50): keep dropping head frames until the second frame is past `DisplayTick`.
3. `MoveBetween(frame0, frame1)` lerps position + rotation + velocity at `t = inverseLerp(frame0.Tick, frame1.Tick, DisplayTick)`.
4. Single-frame buffer → `Extrapolate` (line 78): linearly extrapolate using last `velocity * elapsed`, clamped to 1s ahead.
5. Final `ApplyToAvatar` (line 117) does `Rigidbody.MovePosition` / `MoveRotation`, sets animator velocity (rotated into local space), and the pose.

### Car-relative positions

`TRVFromFrame` (line 126): if `RelativeToCarId != null`, look up the car via `TrainController.Shared.CarForId`, get its `MotionSnapshot`, and transform `f.Position` from car-local → world. Adds `velocity * elapsed` for the car's own motion since the snapshot. This is how players riding a train are visualized smoothly on other clients.

### Delay rationale

`Delay = 300L` ticks. With `StateManager.Now` ticking at typical Unity-tick rate, this is ~300ms of buffer. Trades latency for smoothness against jitter/packet loss. **No way to reduce it without patching** — it's a `const`.

### Patch candidates

| Method | Why patch |
|---|---|
| `RemoteAvatar.AddPosition` | Listen to all incoming remote-player position updates. |
| `RemoteAvatar.UpdatePosition` | Custom interpolation strategy (e.g., predicted netcode). |
| `RemoteAvatar.ApplyToAvatar` | Override how the visual rig is placed each frame (e.g. additional bones). |
| `AvatarManager.AddRemote` | Hook newly-arriving remote players. Add custom HUD attachments here. |

### Gotchas

- **No collision between local and remote avatars.** Remote avatars are pure visuals on a kinematic Rigidbody; local KCC walks through them.
- **`_frames` buffer is only 4 deep.** Bursty server traffic risks frame loss past 4 — older frames silently dropped via `_frames.Enqueue` overflow into a `CircularBuffer`.
- **`TRVFromFrame` returns null if the car isn't loaded.** That's logged as a warning (`"RemoteAvatar: Car not found for frame"`); the avatar stops updating until the next valid frame. Players riding cars in unloaded tiles disappear silently.
- **The lantern toggle is broadcast via `AddUpdateCharacter`**, which carries the *full* customization (including lantern as an "accessory"). Toggling lantern in MP retransmits avatar customization. See `ClientManager.SendCharacter` (`Network.Client/ClientManager.cs:285`).

---

## `Game.IPlayer` / `Game.LocalPlayer` / `Network.Client.RemotePlayer`

Lightweight player handles. **Not** `MonoBehaviour`s for the local player.

```csharp
public interface IPlayer {                                    // Game/IPlayer.cs
    string    Name         { get; }
    bool      IsRemote     { get; }
    PlayerId  PlayerId     { get; }
    Vector3   GamePosition { get; }                           // game-space, not world
    RemotePlayer CheckedRemotePlayer();                       // throws if local
}

public struct LocalPlayer : IPlayer {                         // Game/LocalPlayer.cs
    public string   Name        => Preferences.MultiplayerClientUsername;
    public bool     IsRemote    => false;
    public PlayerId PlayerId    => PlayersManager.PlayerId;
    public Vector3  GamePosition => CameraSelector.shared.localAvatar.character.GroundPosition.WorldToGame();
    public RemotePlayer CheckedRemotePlayer() => throw new Exception("LocalPlayer instance is not RemotePlayer");
}
```

`RemotePlayer` (in `Network.Client`) is the per-remote-player MonoBehaviour:

```csharp
public class RemotePlayer : MonoBehaviour, IPlayer {          // Network.Client/RemotePlayer.cs
    public  PlayerId playerId;
    public  string   playerName;
    public  Vector3  GamePosition => _avatar.transform.position.WorldToGame();
    public  RemoteAvatar AddUpdateAvatar(AvatarDescriptor);
    public  void  UpdateAvatarPosition(Vector3 pos, Vector3 fwd, Vector3 look, Vector3 vel, string carId, AvatarPose pose, long tick);
    public  void  ConfigureAvatar(Vector3 pos, string carId, Vector3 fwd, Vector3 look, Snapshot.CharacterCustomization);
}
```

The local player is a `LocalPlayer` *struct* — there is no MonoBehaviour holding "the player". Mods needing to attach state to "the local player" must either:
1. Attach to `CameraSelector.shared.gameObject` (sibling to `LocalAvatar`).
2. Store in a static keyed by `PlayersManager.PlayerId`.
3. Use the `IPlayer` interface and dispatch by `IsRemote`.

---

## Cross-cutting `Game.Messages` (player/camera MP)

| Message | Auth | Payload | Direction | Purpose |
|---|---|---|---|---|
| `UpdateCharacterPosition` | Passenger | `CharacterPosition` (pos, carId, fwd, look) + Velocity + Pose + Tick | client → host → other clients | Visual position broadcast for `RemoteAvatar` |
| `UpdateCameraPosition` | Passenger | `Vector3` (game space) | client → host | Cached on host for `IsPlayerCameraNear` proximity gating |
| `AddUpdateCharacter` | Passenger | `Name` + `Snapshot.CharacterCustomization` | client → host → other clients | Avatar appearance + lantern state |
| `RemovePlayerRecord` | Trainmaster | `PlayerId` | client → host | Drop a stored player record (housekeeping) |

All four are in `Game.Messages/` and tagged `[ICharacterMessage, IGameMessage]`. `MinimumAccessLevel(AccessLevel.Passenger)` means even passengers can move/customize. The host's `StateManager` routes these via `PropagateGameMessage` (`StateManager.cs:1010-1060`).

**Key fact:** none of these are HostOnly. Position is fully client-authoritative — there is no server-side validation of where players say they are. See [Multiplayer survey](../multiplayer-vanilla-survey.md) for the broader auth model.

---

## Helper utilities

### `WorldTransformer` (game ↔ world coords)

Railroader uses a "floating origin" system. Game space is the canonical, persistent coordinate system; world space is what Unity sees. `WorldTransformer.GameToWorld(Vector3)` and `WorldTransformer.WorldToGame(Vector3)` (extension methods or static) translate. The world periodically shifts to keep float precision near the camera (`WorldDidMoveEvent` Messenger event).

Player + camera + avatar code all subscribe to `WorldDidMoveEvent`:
- `CharacterController.WorldDidMove` (`CharacterController.cs:209`) → `motor.OffsetCharacter(evt.Offset)`.
- `StrategyCameraController.WorldDidMove` (line 180) → offsets `_targetPosition`, `_panStartPosition`, etc.
- `MapCameraUpdater.WorldMoved` (line 71) → `mapManager.ApplyWorldToGameOffset(evt.Offset)`.
- `AvatarPrefab.WorldDidMove` (line 43) → offsets the kinematic `Rigidbody.position`.

When patching anything position-related, **do not assume `transform.position` is stable across frames** — a world shift can teleport everything by hundreds of meters in one frame. Subscribe to `WorldDidMoveEvent` if you cache positions.

### `Helpers.PositionRotation` and `Cameras.JumpTarget`

```csharp
public readonly struct JumpTarget {                           // Cameras/JumpTarget.cs
    public readonly Vector3    Position;
    public readonly Quaternion Rotation;
    public readonly float      RandomRadius;     // adds Random.Range(±r) on resolution
    public readonly Car        RelativeToCar;
    public readonly bool       IsRelativeToCar;
}
```

Constructor variants for free vs. car-relative. `RandomRadius > 0.001f` jitters the spawn position (used for spreading out NPCs / scripted spawns). Used by all `JumpCharacterTo`/`JumpToPoint` paths.

### `Character.SpawnPoint`

```csharp
public class SpawnPoint : MonoBehaviour {                     // SpawnPoint.cs
    public int   priority;                                    // sort descending
    public float radius = 3f;
    public static IEnumerable<SpawnPoint> All { get; }        // FindObjectsOfType, sorted by priority desc
    public static SpawnPoint Default { get; }                 // first in All; throws if none
    public static SpawnPoint ClosestTo(Vector3 worldPosition);
    public (Vector3, Quaternion) GamePositionRotation { get; }
}
```

Vanilla `SpawnPoint` MonoBehaviours are placed in the scene by the map. `Default` is used by `CameraSelector.JumpToSpawn` and `PlayerController.FixPlayerPositionNoTileData`. **Higher `priority` wins** — mods adding spawn points should set `priority > 0` to override default placement.

---

## Patch points summary

| Goal | Patch target |
|---|---|
| Add a custom camera mode | Implement `ICameraSelectable`. Inject via `CameraSelector.SetCamera(Dispatcher, impl)`. Or extend `CameraIdentifier` enum + `UpdateCurrentCamera` (Harmony transpile / postfix). |
| Custom first-person physics | `CharacterController.UpdateVelocity` (KCC) or replace `motor`. |
| Custom third-person view | Subclass `LocalAvatar`, override `showAvatar`, swap in your own camera that orbits the avatar. Or implement an `ICameraSelectable` and use it as the dispatcher. |
| Bigger teleport range | Patch `CameraSelector.TeleportToMouse` (raycast distance is hard-coded 2000m). |
| Track teleport events | Postfix `CameraSelector._JumpToPoint` or `PlayerController.JumpTo`. |
| Listen to player movement | Postfix `CharacterPositionTransmitter.SendIfNeeded` (catches the rate-limited send) or `CharacterController.SetInputs` (raw, every frame). |
| Detect "rode a moving train" | Postfix `PlayerController.AttachTo` or watch `_attachedCarId` via patched `AttachedCarChecker`. |
| Custom spawn logic | Add a `SpawnPoint` with high `priority`. Or patch `CameraSelector.JumpToSpawn`. |
| Per-player camera proximity check | `PlayersManager.IsPlayerCameraNear(Transform, float radius)` — already exists; uses `_lastKnownPositions`. |
| Hook camera mode change | Postfix `CameraSelector.UpdateCurrentCamera`. The new `_currentCamera` is set before `localAvatar.CurrentCameraDidChange`. |

---

## Cross-references

- Input bindings, keybinds, and the `GameInput` API used by everything above: see [Input & Keybinds](input-keybinds.md).
- Multiplayer message routing, `StateManager.PropagateGameMessage`, host handler tables, access levels: see [Multiplayer survey](../multiplayer-vanilla-survey.md).
- `Car.GetMotionSnapshot()` and physics-mover attachment used by `AttachTo` / `RemoteAvatar.TRVFromFrame`: see [Couplers › slack & integration](couplers.md#slack-state--integration) for the integration loop these snapshots feed.
