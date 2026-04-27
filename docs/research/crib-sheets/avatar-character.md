# Avatar & Character — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/` + `Railroader-ILSPY/KinematicCharacterController/`)
**Companion:** [Player & Camera](player-camera.md), [Players & TrainCrew](players-traincrew.md), [Floating Origin](floating-origin.md), [Multiplayer survey](../multiplayer-vanilla-survey.md)

The Railroader avatar/character system splits across three layers:

1. **`AvatarPrefab`** — a serialized Unity prefab (visual mesh + Animator + lantern + map icon). One instance is used for every character, local and remote alike — the local "third-person view of yourself" and every connected `RemotePlayer` all spawn the same prefab from a single `AvatarManager.avatarPrefab` SerializeField. Customization (gender / skin / hat / glasses / bandana / gloves) is structured **inside** the prefab as `AvatarSet` arrays — you don't get one prefab per outfit.
2. **`Character.CharacterController`** — Railroader's wrapper around Kinematic Character Controller (KCC). Implements `KinematicCharacterController.ICharacterController`. Holds the seat/ladder/Default state machine, KCC tunables, and the lean offset. **Naming clash with `UnityEngine.CharacterController`** — always reference by full namespace.
3. **`Character.PlayerController`** — wires KCC to player input + the camera + MP transmission. Implements `ICameraSelectable`. Single live instance, hung off `CameraSelector.character`.

Cars and the avatar share the **same KCC plumbing**: every `Car` ConfigureForBody-adds a `PhysicsMover` (kinematic mover) to its body GameObject; the player's KCC `motor.AttachedRigidbody` (or `AttachedRigidbodyOverride` for seat/ladder) points at that mover's `Rigidbody`. That single-rigidbody attachment is *the* reason you don't slide off a moving train. `WorldDidMoveEvent` is handled separately on each side: `motor.OffsetCharacter(offset)` for the character, `_physicsMover.OffsetSeamless(offset)` for the car. **Per-machine, no MP sync** for the offset itself — see [Floating Origin](floating-origin.md).

Multiplayer position: client-authoritative, no validation, hard-coded **300-tick interpolation delay** with a 4-frame `CircularBuffer` per remote player (see [Player & Camera › RemoteAvatar](player-camera.md#avatarremoteavatar-other-players-bodies-mp)). The destructive `HandleSnapshotPlayers` rebuild on every `PlayerList` arrival (see [Players & TrainCrew](players-traincrew.md)) tears down and re-creates every `RemotePlayer` GameObject — so all `_remotePlayers` references die, *but* the host's snapshot of each player's last `CharacterPosition` is restored back into the new RemotePlayer via `ConfigureAvatar` so the visual position survives. Any mod-side state attached to a `RemotePlayer` MonoBehaviour does *not* survive.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Avatar.AvatarPrefab` | `Avatar/AvatarPrefab.cs:10` | The prefab MonoBehaviour. Owns lantern, kinematic Rigidbody, MapIcon, Customization, Pickable |
| `Avatar.AvatarManager` | `Avatar/AvatarManager.cs:7` | Singleton (`Instance`). `AddAvatar` / `AddRemote` / `RemoveAvatar` / `RemoteAvatarNear` |
| `Avatar.AvatarCustomization` | `Avatar/AvatarCustomization.cs:7` | In-prefab male/female `AvatarSet` switcher; reskins materials + accessories |
| `Avatar.AvatarDescriptor` | `Avatar/AvatarDescriptor.cs:7` | Readonly struct (Gender, SkinToneIndex, accessories dictionary). KVO ↔ Snapshot codec |
| `Avatar.AvatarAnimator` | `Avatar/AvatarAnimator.cs:6` | velocityX/velocityZ + sit/jump/ladder bools + look-at IK |
| `Avatar.RemoteAvatar` | `Avatar/RemoteAvatar.cs:12` | Network-driven body. **Hard-coded `Delay=300L` ticks, `CircularBuffer<Frame>(4)`** |
| `Character.CharacterController` | `Character/CharacterController.cs:16` | KCC `ICharacterController` impl. State: Default/Seated/Ladder. Owns motor reference |
| `Character.PlayerController` | `Character/PlayerController.cs:19` | First-person body wrapper. `JumpTo`, `AttachTo`, `Sit`, `GetRelativeCar` |
| `Character.LocalAvatar` | `Character/LocalAvatar.cs:8` | Sibling-component on `CameraSelector`. Spawns the local visible avatar (FP-mode hidden) |
| `Character.CharacterPositionTransmitter` | `Character/CharacterPositionTransmitter.cs:10` | 100ms throttled / 2000ms forced MP position broadcaster |
| `Character.MotionSnapshot` | `Character/MotionSnapshot.cs:5` | Position + BodyRotation + LookRotation + Velocity (struct) |
| `KinematicCharacterController.PhysicsMover` | `KinematicCharacterController/PhysicsMover.cs:7` | Kinematic Rigidbody mover. **`OffsetSeamless` is the floating-origin shift primitive** |
| `KinematicCharacterController.KinematicCharacterMotor` | `KinematicCharacterController/KinematicCharacterMotor.cs:8` | The KCC motor. `OffsetCharacter` is the analogue to `OffsetSeamless` |
| `KinematicCharacterController.KinematicCharacterSystem` | `KinematicCharacterController/KinematicCharacterSystem.cs:7` | Static registry + `FixedUpdate` driver for *all* motors and movers |
| `Model.CarMover` | `Model/CarMover.cs:6` | Per-car `IMoverController`. Owns the `PhysicsMover`. Handles awake/sleep + `WorldDidMove` |

---

## Avatar spine: the single-prefab model

```
AvatarManager (singleton MonoBehaviour, scene-placed)
   └── SerializeField avatarPrefab : AvatarPrefab    ← the ONE prefab
            │
            ├── AvatarManager.AddAvatar(descriptor, showMapIcon, playerId, name)
            │     creates AvatarPrefab instance under AvatarManager.transform
            │     names it "{playerId} {playerName}"
            │     calls Customization.Configure(descriptor) → male OR female set
            │     sets Pickable.PlayerId = playerId
            │     toggles MapIcon visibility
            │
            ├── used by: LocalAvatar.SetupAvatarIfNeeded
            │     → AvatarManager.Instance.AddAvatar(Preferences.AvatarDescriptor,
            │                       showMapIcon:false, PlayersManager.PlayerId, "You")
            │
            └── used by: AvatarManager.AddRemote(playerId, playerName)
                  → AddAvatar(AvatarDescriptor.Default, showMapIcon:true, ...)
                  + AddComponent<RemoteAvatar> on the AvatarPrefab GameObject
                  → RemoteAvatar.avatar = avatarPrefab
```

There is **no per-player prefab variant**. Mods that want different visual rigs per remote player must postfix-patch `AvatarManager.AddAvatar` (or the call sites) to swap in their own `AvatarPrefab` instance, or hook `LocalAvatar.SetAvatarCustomization` for the local one.

### `AvatarPrefab.Awake` lifecycle

```csharp
private void Awake()                                                   // AvatarPrefab.cs:24
{
    Customization = GetComponent<AvatarCustomization>();
    Pickable      = GetComponentInChildren<AvatarPickable>();
    Rigidbody     = base.gameObject.AddKinematicRigidbody();           // helpers extension
    MapIcon       = GetComponentInChildren<MapIcon>();
    lantern.SetActive(value: false);                                   // forced off
}
```

Component contract — at minimum the prefab needs:

| Component | Where | Required |
|---|---|---|
| `AvatarCustomization` | root | `[RequireComponent]` (`AvatarPrefab.cs:9`) |
| `AvatarPickable` | descendant | yes — drives PlayerId-clickable map/UI |
| `MapIcon` | descendant | yes (FindObjectOfType-pattern; `MapIcon.gameObject.SetActive(showMapIcon)`) |
| `Animator` (Unity) | inside the active AvatarSet's `gameObject` | `AvatarAnimator` `[RequireComponent(typeof(Animator))]` |
| `AvatarAnimator` | inside the active AvatarSet's `gameObject` | yes — `Customization.Animator = AvatarGameObject.GetComponentInChildren<AvatarAnimator>()` |
| `lantern : GameObject` field | anywhere referenced | yes (force-toggled off in Awake) |

`Rigidbody` is added by `AddKinematicRigidbody()` on Awake — the prefab does NOT need a serialized Rigidbody.

### `AvatarCustomization.AvatarSet`

```csharp
[Serializable] public struct AvatarSet                                  // AvatarCustomization.cs:10
{
    public GameObject           gameObject;            // root for the body mesh
    public Renderer[]           skinRenderers;         // re-materialed at config time
    public Material[]           skinMaterials;         // indexed by SkinToneIndex
    public AccessoryReference[] accessories;           // hat/glasses/bandana/gloves
}

[Serializable] public struct AccessoryReference  { string identifier; AccessoryOption[] options; }
[Serializable] public struct AccessoryOption    { string identifier; GameObject[] gameObjects; }
```

`Configure(descriptor)`:

1. Disables the *other* gender's `gameObject`.
2. Activates this gender's `gameObject` and re-fetches `Animator` from it.
3. Reassigns `skinRenderers[i].sharedMaterial = skinMaterials[descriptor.SkinToneIndex]`.
4. For each `AccessoryReference`, looks up the descriptor's selected option, walks `options` and `SetActive(active)` on each option's `gameObject` array. **Multiple gameObjects per option** (so an option can mesh-swap several body parts at once).

**Vanilla skin tone count is 2** (`#d4c3b0`, `#3d260c` from `CharacterSettingsBuilder.cs:82`). `skinMaterials.Length` on each AvatarSet must match what the UI offers — out-of-range `descriptor.SkinToneIndex` will index-OOB throw.

### `AvatarDescriptor` (immutable struct, KVO codec)

```csharp
public readonly struct AvatarDescriptor(Gender, int SkinToneIndex,
                                         Dictionary<string, Value> Accessories)  // AvatarDescriptor.cs:7
{
    public static AvatarDescriptor Default                                       // 15
        => new(Gender.Male, 0, { "hat":"kromer", "bandana":"red" });             // glasses + gloves left out

    public static AvatarDescriptor From(Value);                                  // 27
    public static AvatarDescriptor From(Snapshot.CharacterCustomization);        // 55
    public Value ToValue();                                                      // 33
    public Snapshot.CharacterCustomization ToCharacterCustomization();           // 60
    public AvatarDescriptor SettingAccessory(string id, Value option);           // 65 (returns new)
    public Value SelectedOptionForAccessory(string accessoryIdentifier);         // 43
}
```

**Wire format** is a `Value.Dictionary` with three fixed keys:

```
{
  "gender":      "m" | "f"             // string, NOT enum int
  "skinTone":    int
  "accessories": { "hat":"kromer", "glasses":"specs", "bandana":"red", "gloves":"fireman", "lantern":<bool> }
}
```

The lantern is encoded **as an accessory** — `ClientManager.MakeCharacterCustomizationUsingPreferences(bool lanternEnabled)` (`Network.Client/ClientManager.cs:280`) injects `"lantern" → Value.Bool(lanternEnabled)` into a copy of `Preferences.AvatarDescriptor` immediately before `SendCharacter()`. **The visual lantern's on/off is recovered by anyone receiving an `AddUpdateCharacter` only if the prefab's accessory list has a `"lantern"` entry that swaps the lantern's GameObject** — vanilla `AvatarCustomization` doesn't appear to declare this; the actual lantern visibility on remote rigs may be a no-op. See [Gotchas](#gotchas).

### `AvatarPickable`

```csharp
public class AvatarPickable : MonoBehaviour, IPickable                  // AvatarPickable.cs
{
    public float MaxPickDistance => 500f;
    public int   Priority        => 0;
    public TooltipInfo TooltipInfo { get; set; }                        // set to "{name}" in AddAvatar
    public PickableActivationFilter ActivationFilter => PickableActivationFilter.PrimaryOnly;
    public PlayerId PlayerId { get; set; }

    public void Activate(PickableActivateEvent evt) {
        if (GameInput.IsControlDown && PlayerId.IsValid)
            CompanyWindow.Shared.ShowPlayer(PlayerId.String);            // Ctrl+Click only
    }
}
```

**500m pick distance** — far longer than most pickables. Bare click is a no-op; Ctrl+click opens the player's profile. See [Interaction Controls](interaction-controls.md) for the dispatch model.

### `AvatarTester` (editor-only helper)

`Avatar/AvatarTester.cs` — a `MonoBehaviour` with a serialized `AvatarCustomization`, `gender`, `skinToneIndex`, and three string fields (`hat`/`bandana`/`glasses`). `OnValidate` re-`Configure`s the avatar in-editor. Useful as a starting point for editor previews of mod accessories. Notably **omits `gloves`** — bug or oversight; the in-game `CharacterSettingsBuilder` does include it.

### Patch candidates (avatar visuals)

| Method | Why patch |
|---|---|
| `AvatarManager.AddAvatar` | Inject custom prefab variants per-player. The cleanest single chokepoint — both local and remote go through here. |
| `AvatarManager.AddRemote` | Hook newly-arriving remote players. Also the place to attach mod-side per-player state to the GameObject. |
| `AvatarCustomization.Configure` | Add custom accessory categories without modifying the SerializeField arrays. Postfix to apply post-config. |
| `AvatarPrefab.Awake` | Add components to *every* avatar instance (local + remote). |
| `LocalAvatar.SetAvatarCustomization` | Hook customization changes from the preferences UI. |
| `LocalAvatar.SetupAvatarIfNeeded` | Customize the local avatar's spawn. |
| `AvatarPickable.Activate` | Replace the Ctrl+Click profile-open with custom UI. |

---

## `Character.CharacterController` (KCC core)

The `MonoBehaviour, KinematicCharacterController.ICharacterController` impl. Implements every callback in the `ICharacterController` contract. Single live instance (the FP body) but the *type* is used for nothing else in vanilla — there are no NPC characters.

### State machine

```csharp
public enum CharacterState { Default, Seated, Ladder }                  // Character/CharacterState.cs
private enum AttachState   { Anchoring, Stable, Deanchoring }           // CharacterController.cs:18
```

`TransitionToState(CharacterState)` (`CharacterController.cs:226`) toggles `motor.SetMovementCollisionsSolvingActivation` and `motor.SetGroundSolvingActivation` — both **disabled** for Seated/Ladder, **re-enabled** for Default. Anchoring duration: hard-coded **0.15s** (`CharacterController.cs:116`).

### Tunables (Inspector-editable)

```csharp
[Header("Stable Movement")]
public float maxStableMoveSpeed         = 10f;                          // 28
public float stableMovementAccelSharpness = 1f;                         // 31  (lerp factor for accel)
public float stableMovementDecelSharpness = 15f;                        // 34
public float orientationSharpness       = 10f;                          // 36  (unused in vanilla logic)
public float runMoveSpeed               = 40f;                          // 38

[Header("Air Movement")]
public float maxAirMoveSpeed            = 15f;                          // 41
public float airAccelerationSpeed       = 15f;                          // 43
public float drag                       = 0.1f;                         // 45

[Header("Jumping")]
public bool  allowJumpingWhenSliding    = false;                        // 48
public float jumpUpSpeed                = 10f;                          // 50
public float jumpScalableForwardSpeed   = 10f;                          // 52
public float jumpPreGroundingGraceTime  = 0f;                           // 54
public float jumpPostGroundingGraceTime = 0f;                           // 56

[Header("Ladders")]
public float ladderSpeedNormal          = 2f;                           // 59
public float ladderSpeedFast            = 4f;                           // 61

[Header("Misc")]
public List<Collider> ignoredColliders  = …;                            // 64
public Vector3        gravity           = (0, -30, 0);                  // 66
public Transform      cameraContainer;                                  // 68  (serialized)
public float          crouchedCapsuleHeight             = 1f;           // 70
public float          maintainUpDegreesPerSecond        = 0.001f;       // 72  (basically locked upright)

public float eyeHeightStanding          = 1.55f;                        // 118
public float eyeHeightSeated            = 1.25f;                        // 120

[Range(0.25f, 1.25f)] public float leanDistance = 0.5f;                 // 132
private const float AnchoringDuration   = 0.15f;                        // 116
```

`Config.Shared.ladderExitBump` is the velocity vector applied when jumping off the top of a ladder (`CharacterController.cs:415`). Not per-ladder.

### KCC ICharacterController surface

```csharp
public void BeforeCharacterUpdate(float dt);                            // 462  (no-op)
public void UpdateRotation(ref Quaternion currentRotation, float dt);   // 466
public void UpdateVelocity(ref Vector3 currentVelocity, float dt);      // 473  ← all motion math
public void AfterCharacterUpdate(float dt);                              // 587
public void PostGroundingUpdate(float dt);                               // 693
public bool IsColliderValidForCollisions(Collider c);                    // 705
public void OnGroundHit(Collider, Vector3 normal, Vector3 point, ref HitStabilityReport); // 718 (no-op)
public void OnMovementHit(Collider, Vector3 normal, Vector3 point, ref HitStabilityReport); // 722 (no-op)
public void ProcessHitStabilityReport(...);                              // 734 (no-op)
public void OnDiscreteCollisionDetected(Collider);                       // 746 (no-op)
protected void OnLanded();                                               // 738 (no-op, named hook)
protected void OnLeaveStableGround();                                    // 742 (no-op, named hook)
```

`UpdateVelocity` branches on `CurrentCharacterState`:
- **Default**: stable-ground vs. air branch with the standard KCC slope-tangent + accel/decel + air-acceleration formulas; jump consumed via `motor.ForceUnground()` + `vector3 * jumpUpSpeed`.
- **Seated**: `motor.GetVelocityForMovePosition(transient, _seat.FootPosition, dt)` — for `Stable`, snapped to the seat's foot position every tick. For `Anchoring`, lerped via `AnchoringParameter` (timer/0.15).
- **Ladder**: `Vector3.Lerp(transient, _ladder.TransformPoint(_ladderLocalPosition), dt * 20f)` — locked to the ladder's local position with a 20×dt smoothing factor.

`OnLanded` and `OnLeaveStableGround` are `protected` named-but-empty methods (cannot be called externally). They're hook stubs — Harmony postfix is the only way to add fall damage / footsteps / dust.

### Sit / Ladder

```csharp
public void Sit([CanBeNull] Seat seat, bool immediate);                 // 750
private void GrabLadder([CanBeNull] Ladder ladder, bool immediate);     // 769
public void UnsitUnladder();                                            // 813
public void CarWillBeDestroyed();                                       // 808 → UnsitUnladder
public Action OnSeatDidChange;                                          // 141  (event)
public Action OnLadderDidChange;                                        // 143  (event)
public bool   IsSeated => _seat != null;
public bool   IsOnLadder => _ladder != null;
public Seat   Seat => _seat;
```

Mutual exclusion — calling `Sit(seat, ...)` immediately calls `GrabLadder(null, immediate:true)` first, and vice versa. Entering Seated/Ladder sets `motor.AttachedRigidbodyOverride = (seat|ladder).GetComponentInParent<PhysicsMover>()?.Rigidbody`. Exiting clears the override. The `?.` matters — a seat/ladder that isn't a child of a `PhysicsMover` (e.g. station seats) sets the override to `null`, leaving the player un-attached.

```csharp
public MotionSnapshot GetMotionSnapshot()                               // 793
{
    Quaternion transientRotation = motor.TransientRotation;
    Quaternion bodyRotation = transientRotation;
    if (_ladder != null)  bodyRotation = Quaternion.Euler(0, 180, 0) * _ladder.transform.rotation;
    if (_seat   != null)  bodyRotation = _seat.transform.rotation;
    return new MotionSnapshot(motor.TransientPosition, bodyRotation, transientRotation, motor.Velocity);
}
```

**LookRotation == TransientRotation** (head/camera direction). **BodyRotation** snaps to the seat or ladder when seated/laddered — that's why `LocalAvatar.FixedUpdate` uses `motionSnapshot.BodyRotation.normalized` for the kinematic Rigidbody body pose, but `RemoteAvatar.ApplyToAvatar` uses both rotations independently.

### Floating-origin handling

```csharp
private void OnEnable()  { Messenger.Default.Register<WorldDidMoveEvent>(this, WorldDidMove); }   // 199
private void OnDisable() { Messenger.Default.Unregister(this); }                                  // 204
private void WorldDidMove(WorldDidMoveEvent evt) { motor.OffsetCharacter(evt.Offset); }           // 209
```

Single-line offset — calls into KCC's `KinematicCharacterMotor.OffsetCharacter(offset)` (`KinematicCharacterController/KinematicCharacterMotor.cs:392`):

```csharp
public void OffsetCharacter(Vector3 offset)
{
    _initialSimulationPosition += offset;
    _transientPosition         += offset;
    _transform.position        += offset;
    InitialTickPosition        += offset;
    if (_movePositionDirty)
        _movePositionTarget    += offset;
}
```

Velocity is **not** disturbed — the offset shifts position state without re-deriving any velocity. Critical for floating-origin: the KCC keeps walking as if nothing happened.

### Patch candidates (CharacterController)

| Method | Why patch |
|---|---|
| `CharacterController.SetInputs(ref PlayerCharacterInputs)` | Filter/synthesize player movement intents before the KCC sees them. |
| `CharacterController.UpdateVelocity` | Custom physics — different gravity, water, climbing. **Hot path** (FixedUpdate). |
| `CharacterController.UpdateRotation` | Replace the upright-snap behavior; allow tilt. Subtle: also called on every KCC tick. |
| `CharacterController.OnLanded` / `OnLeaveStableGround` | Empty hook stubs — patch to add footsteps / fall damage / VFX. |
| `CharacterController.Sit` / `GrabLadder` | Veto seat/ladder entry; route to mod-defined seat types. |
| `CharacterController.CheckForLadderOrSeat` | Modify the auto-grab-on-overlap logic. |
| `CharacterController.GetMotionSnapshot` | Override the BodyRotation snapping for custom poses. |
| `CharacterController.WorldDidMove` | Add side-effects to floating-origin shifts (e.g. invalidate cached positions). |
| `CharacterController.TransitionToState` | Hook state transitions (e.g. fire a Messenger event). |

### Gotchas (CharacterController)

- **Naming clash with `UnityEngine.CharacterController`.** Always `using Character;` carefully — the wrong type compiles silently.
- **`maintainUpDegreesPerSecond = 0.001f`** effectively pins the body upright. Set higher to allow tilt or wall-walking.
- **`motor.AttachedRigidbodyOverride` vs `AttachedRigidbody`**: KCC distinguishes these. `AttachedRigidbody` is the auto-detected rigidbody you're standing on; `AttachedRigidbodyOverride` is what you're forcibly attached to (seat/ladder). `PlayerController.GetRelativeCar` (`PlayerController.cs:277`) prefers the override, falling back to the auto. A seated player is "on" the seat's car even if the auto-detected rigidbody is something else.
- **`OnLanded` / `OnLeaveStableGround` are `protected` no-ops**, not virtual. Subclass-and-override doesn't make them callable externally. Harmony postfix is the only path.
- **`gravity` is per-instance**, not global. Setting it to `Vector3.zero` only zero-G's the player.
- **Crouch un-crouch is gated by overlap check** — `AfterCharacterUpdate` re-crouches if standing-up would clip into geometry. Sliding under low clearance Just Works.
- **`Sit(null, immediate:true)` is the standard "stand up" call** (it transitions to Default if `_seat != null`). Same for `GrabLadder(null, true)`.
- **`UnsitUnladder`** is the public clean-up; called from `JumpTo` and `JumpToCar` to ensure subsequent jumps don't end up still attached.
- **Anchoring camera shift** — the 0.15s blend animates `cameraContainer.localPosition.y` from 1.55→1.25 (or vice versa) via `LeanTween.moveLocal` with `Config.Shared.characterEasing` curve. The animation ID is cached in `_cameraContainerMoveId` and cancelled if a new transition starts. Patching `AnimateCameraContainerPosition` lets you change eye-height curves; patching `TargetCameraContainerLocalPosition` lets you add per-pose vertical offsets.

---

## `Character.PlayerController`

Wraps `CharacterController`. Owns `MouseLookInput`, `CharacterCameraController`, and (implicitly) the `CharacterPositionTransmitter` — both `Mouselook` and `Transmitter` are `AddComponent`'d in `Awake` (`PlayerController.cs:68-69`).

See [Player & Camera › PlayerController](player-camera.md#characterplayercontroller-first-person-body-wrapper) for the full input surface and update loop. Highlights relevant to characters/KCC:

### `AttachTo` (riding a moving car)

```csharp
public void AttachTo(Car car, Vector3 worldPosition, Quaternion rotation)   // 211
{
    PhysicsMover pm = car.GetComponentInChildren<PhysicsMover>();
    if (pm == null) { Log.Error("AttachTo: Couldn't find PhysicsMover"); return; }
    Rigidbody rb = pm.Rigidbody;
    if (rb == null) { Log.Error("AttachTo: Couldn't find PhysicsMover.Rigidbody"); return; }
    character.motor.ApplyState(new KinematicCharacterMotorState {
        AttachedRigidbody         = rb,
        AttachedRigidbodyVelocity = rb.velocity,
        BaseVelocity              = Vector3.zero,
        GroundingStatus           = new CharacterTransientGroundingReport {
            FoundAnyGround = true, GroundNormal = Vector3.up,
            IsStableOnGround = true, InnerGroundNormal = Vector3.up,
            OuterGroundNormal = Vector3.up, SnappingPrevented = false
        },
        LastMovementIterationFoundAnyGround = false,
        MustUnground     = false,
        MustUngroundTime = 0f,
        Position         = worldPosition,
        Rotation         = rotation,
    });
}
```

This **fakes a stable-on-ground attached state** synthetically. `KinematicCharacterMotorState` is the KCC's full snapshot; `ApplyState` calls `SetPositionAndRotation(state.Position, state.Rotation, bypassInterpolation:true)` and assigns the rest. The synthetic `GroundingStatus.IsStableOnGround=true` skips KCC's own grounding probe for that tick — important so the player doesn't immediately fall through the car's body collider.

There's no per-frame "re-attach" — the KCC keeps the rigidbody attached as long as `AttachedRigidbody` is non-null and the motor's grounding probe finds it. Walking off the end of the car re-grounds normally.

### Attached-car checker

```csharp
private IEnumerator AttachedCarChecker()                                  // 303
{
    WaitForSeconds wait = new WaitForSeconds(0.5f);
    while (true) {
        try {
            Car relativeCar = GetRelativeCar();
            if (_attachedCarId != relativeCar?.id) {
                Log.Information("AttachedCarChecker: {a} -> {b}", _attachedCarId ?? "<null>",
                                 relativeCar?.id ?? "<null>");
                _attachedCarId         = relativeCar?.id;
                _attachedCarLoadToken?.Dispose();
                _attachedCarLoadToken  = relativeCar?.ModelLoadRetain("Attached");
            }
            if (_attachedCarId == null) {
                CheckForTerrainBelow();
                MapManager mm = MapManager.Instance;
                if (mm != null)
                    mm.KeepLoaded = mm.TilePositionFromPoint(
                        WorldTransformer.WorldToGame(character.GetMotionSnapshot().Position));
            }
        } catch (Exception ex) { Log.Error(ex, "Exception in AttachedCarChecker"); }
        yield return wait;
    }
}
```

Runs every **0.5s**. Its three responsibilities:

1. Detect attached-car changes; refresh `ModelLoadRetain` so the car you're riding stays fully loaded even if it streams out of normal LOD.
2. When *not* on a car, call `CheckForTerrainBelow` (50m down-cast with 5-position cardinal-rescue fallback) — see [Player & Camera › CheckForTerrainBelow](player-camera.md#stuck-below-terrain-rescue).
3. When *not* on a car, set `MapManager.KeepLoaded = currentTile` so the tile under the player can never be unloaded (overrides the 60s grace).

### `GetRelativeCar` and `GetRelativePositionRotation`

```csharp
internal Car GetRelativeCar()                                             // 277
{
    Rigidbody rb = (motor.AttachedRigidbodyOverride ?? motor.AttachedRigidbody);
    return rb?.GetComponentInParent<Car>();
}

public (MotionSnapshot, Car) GetRelativePositionRotation()                // 287
{
    var snap = character.GetMotionSnapshot();
    var car  = GetRelativeCar();
    if (car != null) {
        var carSnap = car.GetMotionSnapshot();                            // CarMover snapshot
        var invR    = Quaternion.Inverse(carSnap.Rotation);
        snap.Position     = invR * (snap.Position - carSnap.Position);    // car-local
        snap.BodyRotation = invR * snap.BodyRotation;
        snap.LookRotation = invR * snap.LookRotation;
        snap.Velocity     = character.motor.BaseVelocity;                 // base, not absolute
    }
    return (snap, car);
}
```

When riding a car, the transmitted position is **car-local** (not game-space). `RemoteAvatar.TRVFromFrame` (see [Player & Camera › RemoteAvatar interp](player-camera.md#interpolation-pipeline)) inverts this on the receiver: `worldPos = carSnap.Rotation * frame.Position + carSnap.Position`, then adds `velocity * elapsed` for the time gap. **The receiver looks up the car by id** — if the car isn't loaded on the receiver, `TRVFromFrame` returns null and the avatar stops updating (logged warning).

### `JumpTo` vs `AttachTo` vs `JumpToCar`

| Method | Purpose | Output state |
|---|---|---|
| `JumpTo(Vector3, Quaternion)` (203) | Free-space teleport | `motor.SetPositionAndRotation`. Detached. |
| `JumpToCar(Car)` (194) | Side-of-car teleport | 3m to the car's left, faces car. Detached. |
| `AttachTo(Car, Vector3, Quaternion)` (211) | Ride a moving car | `motor.ApplyState` with synthetic grounding + AttachedRigidbody set to the car's PhysicsMover.Rigidbody. |

Note: `JumpTo` always zeroes the X/Z rotation (`rotation = Quaternion.Euler(0, rotation.eulerAngles.y, 0)`) — calling `JumpTo` discards pitch/roll. If you need the player to land at a non-yaw rotation, post-process via `cameraController.SetRotation`.

### Patch candidates (PlayerController)

See [Player & Camera › patch table](player-camera.md#patch-candidates) for the full list. Specific to character/KCC concerns:

| Method | Why patch |
|---|---|
| `PlayerController.AttachTo` | Hook for "boarded a moving car". Fire mod events; intercept the synthetic grounding to add per-car attach side-effects. |
| `PlayerController.GetRelativeCar` | Override the rigidbody-walk to support different ride-detection (trailers, mod-defined moving objects). |
| `PlayerController.GetRelativePositionRotation` | Override car-local conversion (e.g., for non-Car attached objects). |
| `PlayerController.AttachedCarChecker` | The 0.5s loop is a coroutine — patch the inner body via Harmony or override the polling cadence. |

---

## Riding a moving non-Car object (mod-defined)

Vanilla `GetRelativeCar` (`PlayerController.cs:277`) hard-codes `rigidbody.GetComponentInParent<Car>()`. So:

1. **Attaching the player to your moving object works out of the box** — as long as the object has a `PhysicsMover` (which means a kinematic `Rigidbody` and is registered with `KinematicCharacterSystem`), `motor.AttachedRigidbody` will pick it up and the player will ride it correctly.
2. **`GetRelativeCar` returns null** for it. So:
   - The transmitter sends position in *world* space, not object-local. Multiplayer remote avatars riding your object will jitter / not track.
   - `IsOnGround` returns true (because `IsOnGround` checks `IsStableOnGround && GetRelativeCar() == null`), which is wrong for riders.
   - `AttachedCarChecker` will call `CheckForTerrainBelow` and `MapManager.KeepLoaded`, neither of which is what you want.
3. **`PlayerController.AttachTo` requires a Car** parameter. To attach the player to a non-Car, use `motor.ApplyState` directly with your own `KinematicCharacterMotorState`.

To support modded moving objects, the cleanest patch surface is to:

- Subclass / patch `PlayerController.GetRelativeCar` to also check for your mod-defined "rideable" component on the rigidbody's parent, and synthesize a fake `Car`-shaped handle.
- Or patch `PlayerController.GetRelativePositionRotation` to do object-local conversion against your mod object's transform when on it.
- Or: implement the position transmission yourself (custom MP message + custom `RemoteAvatar`-equivalent reader) and bypass vanilla altogether for those rides.

---

## `KinematicCharacterController.PhysicsMover` (the kinematic mover)

Lives outside Assembly-CSharp at `Railroader-ILSPY/KinematicCharacterController/KinematicCharacterController/PhysicsMover.cs`. **`[RequireComponent(typeof(Rigidbody))]`**.

```csharp
public class PhysicsMover : MonoBehaviour
{
    public Rigidbody Rigidbody;                          // populated from GetComponent<Rigidbody>
    public bool      MoveWithPhysics = true;             // toggle between MovePosition vs direct rb.position
    public IMoverController MoverController;             // user-defined; UpdateMovement(out pos, out rot, dt)

    public Vector3    LatestInterpolationPosition;
    public Quaternion LatestInterpolationRotation;
    public Vector3    PositionDeltaFromInterpolation;
    public Quaternion RotationDeltaFromInterpolation;

    public Vector3    Velocity         { get; }          // (TransientPosition - prev) / dt
    public Vector3    AngularVelocity  { get; }
    public Vector3    TransientPosition{ get; }          // post-VelocityUpdate target
    public Quaternion TransientRotation{ get; }
    public Vector3    InitialTickPosition;               // pre-tick anchor (for interpolation)
    public Quaternion InitialTickRotation;
    public Vector3    InitialSimulationPosition { get; } // pre-VelocityUpdate
    public Quaternion InitialSimulationRotation { get; }

    public void SetPosition(Vector3);                    // hard set: Transform + Rigidbody + Initial + Transient
    public void SetRotation(Quaternion);
    public void SetPositionAndRotation(Vector3, Quaternion);
    public PhysicsMoverState GetState();                 // snapshot
    public void ApplyState(PhysicsMoverState state);     // restore
    public void VelocityUpdate(float dt);                // the once-per-tick mover step
    public void OffsetSeamless(Vector3 offset);          // ← floating-origin shift primitive
}
```

### `OffsetSeamless` — the contract

```csharp
public void OffsetSeamless(Vector3 offset)                              // PhysicsMover.cs:178
{
    InitialSimulationPosition   += offset;
    InitialTickPosition         += offset;
    TransientPosition           += offset;
    LatestInterpolationPosition += offset;
    Transform.position           = InitialTickPosition;
    Rigidbody.position           = InitialTickPosition;
    Rigidbody.MovePosition(TransientPosition);
}
```

**Shifts every position-state field by the offset, preserving the velocity history** (Velocity is `(TransientPosition - InitialSimulationPosition) / dt`, so adding the same offset to both leaves the differential identical). The `Transform.position` and `Rigidbody.position` are both forced to the new tick anchor; then `MovePosition` re-interpolates toward the new transient. This is the entire reason `WorldDidMoveEvent` doesn't introduce velocity spikes on cars or the player.

`KinematicCharacterMotor.OffsetCharacter` is the analogue for the character side:

```csharp
public void OffsetCharacter(Vector3 offset)                             // KinematicCharacterMotor.cs:392
{
    _initialSimulationPosition += offset;
    _transientPosition         += offset;
    _transform.position        += offset;
    InitialTickPosition        += offset;
    if (_movePositionDirty) _movePositionTarget += offset;
}
```

### KCC↔PhysicsMover contract

`KinematicCharacterSystem` (`KinematicCharacterController/KinematicCharacterSystem.cs:7`) is a `[DefaultExecutionOrder(-100)]` MonoBehaviour. Singleton-on-demand via `EnsureCreation()`. Holds two static lists: `CharacterMotors` and `PhysicsMovers`. Movers and motors register themselves in `OnEnable` (and unregister in `OnDisable`).

Per `FixedUpdate`:

```
PreSimulationInterpolationUpdate(dt):                     // captures InitialTickPosition for everyone
   for each motor: InitialTickPosition = TransientPosition; Transform.SetPosAndRot(transient)
   for each mover: same + Rigidbody.position/rotation = transient

Simulate(dt, motors, movers):
   for each mover: VelocityUpdate(dt)                     // mover.MoverController.UpdateMovement(out pos, out rot, dt)
                                                          //   then Velocity = (TransientPos - InitialSimPos) / dt
   for each motor: UpdatePhase1(dt)                       // collisions vs world (unchanged movers)
   for each mover: Transform.SetPosAndRot(transient); Rigidbody.position/rotation = transient
   for each motor: UpdatePhase2(dt)                       // settle, depenetration

PostSimulationInterpolationUpdate(dt):
   for each motor: Transform.SetPosAndRot(InitialTickPos, InitialTickRot)  // back to pre-tick anchor
   for each mover:
     if MoveWithPhysics: Rigidbody.position = InitialTickPos; MovePosition(transient); MoveRotation(transient)
     else:               Rigidbody.position = transient; rotation = transient
```

Then `LateUpdate` runs `CustomInterpolationUpdate`:

```csharp
float t = Mathf.Clamp01((Time.time - _lastCustomInterpolationStartTime) / _lastCustomInterpolationDeltaTime);
for each motor: Transform.SetPosAndRot(Lerp(InitialTickPos, transient, t), Slerp(...))
for each mover: same + compute PositionDeltaFromInterpolation/RotationDeltaFromInterpolation for this frame
```

So the `Transform.position` you see in `Update` is **interpolated between the last two physics ticks**, while `Rigidbody.position` is the actual physics state. Code that needs the truthful physics position must read `_physicsMover.TransientPosition` / `_physicsMover.Rigidbody.position`, not `transform.position`.

### `Model.CarMover` (per-car `IMoverController`)

```csharp
public class CarMover : IMoverController                                // Model/CarMover.cs:6
{
    private PhysicsMover _physicsMover;                                 // 8
    private Rigidbody    _rigidbody;
    private Vector3      _moverPosition;
    private Quaternion   _moverRotation = Quaternion.identity;
    private Vector3      _velocity;
    private bool         _physicsMoverEnabled;
    private bool         _playerNearby;
    private bool         _movedRecently;
    private Transform    _bodyTransform;

    public void ConfigureForBody(GameObject body) {                     // 36
        _bodyTransform = body.transform;
        _rigidbody     = body.AddComponent<Rigidbody>();
        _physicsMover  = body.AddComponent<PhysicsMover>();
        _physicsMover.ForceAwake();
        _physicsMoverEnabled = true;
        _physicsMover.MoverController = this;
        _timeLastMoved = Time.time;
        UpdatePhysicsMoverEnabled();
        ApplyMoverPosition(immediate: true);
    }

    public void ClearBody();                                            // 49
    public void Move(Vector3 worldPos, Quaternion rot, bool immediate); // 66
    public void SetPlayerNearby(bool playerNearby);                     // 109
    public void UpdateMovement(out Vector3 pos, out Quaternion rot, float dt); // 115 — IMoverController
    public void CheckForSleepyMover();                                  // 163  (1s no-move → sleep)
    public void WorldDidMove(Vector3 offset);                           // 172  → _physicsMover.OffsetSeamless(offset)
    public Car.MotionSnapshot GetMotionSnapshot();                      // 194
}
```

`UpdateMovement` simply reports `_moverPosition` / `_moverRotation` — the mover's "controller" is just a value passthrough; `CarMover` writes those values from `Move(...)` calls (the train sim's solver). **`Velocity` on the mover is then derived by `PhysicsMover.VelocityUpdate` as `(transient - initialSim) / dt`.**

### Mover sleep gating

`UpdatePhysicsMoverEnabled` toggles `_physicsMoverEnabled = _movedRecently && _playerNearby`. If either is false, the `PhysicsMover.enabled = false` and the rigidbody falls back to interpolated `MovePosition`. `_movedRecently` is set true when `Move()` is called with distance > 1mm; `CheckForSleepyMover` sets it false if no-move for >1s. `_playerNearby` is **externally driven** — see [Player & Camera › `IsPlayerCameraNear`](player-camera.md#mp-camera-position-transmit) for the proximity gating that drives this.

The intent: only cars currently moving AND with a player camera nearby need the full PhysicsMover machinery (so KCC characters interact correctly). Static far-away cars use a cheaper Rigidbody path.

### `OffsetSeamless` in CarMover

```csharp
public void WorldDidMove(Vector3 offset)                                // CarMover.cs:172
{
    _moverPosition += offset;
    if (_physicsMoverEnabled)
        _physicsMover.OffsetSeamless(offset);
    else if (_bodyTransform != null)
        _bodyTransform.position = _moverPosition;                       // simple shift when sleeping
}
```

When the mover is asleep, the body's transform is just shifted directly (no PhysicsMover state to preserve). When the mover is awake, `OffsetSeamless` runs.

### Mover wake-up via `SetPhysicsMoverPositionSeamless`

```csharp
private void SetPhysicsMoverPositionSeamless(Vector3 newPosition)       // CarMover.cs:185
{
    Vector3 initialTickPosition = newPosition - _velocity * Time.fixedDeltaTime;
    _physicsMover.InitialTickPosition = initialTickPosition;
    _physicsMover.SetPosition(newPosition);
    _physicsMover.VelocityUpdate(Time.fixedDeltaTime);
    _physicsMover.SetPosition(newPosition);
}
```

When a mover wakes up (`_physicsMoverEnabled` flipping false→true), this preserves the velocity history by back-computing the previous tick anchor from the current velocity. The double `SetPosition` is intentional: `VelocityUpdate` mutates `TransientPosition` based on `MoverController.UpdateMovement`, and the second `SetPosition` re-anchors after that.

### Patch candidates (PhysicsMover / CarMover)

| Method | Why patch |
|---|---|
| `PhysicsMover.OffsetSeamless` | Custom KCC-aware shift behavior. Rare. |
| `KinematicCharacterMotor.OffsetCharacter` | Same on the character side. |
| `CarMover.UpdatePhysicsMoverEnabled` | Custom sleep policy (e.g. always-on for mod cars). |
| `CarMover.ConfigureForBody` | Add per-car components on the body GameObject when the mover spins up. |
| `CarMover.WorldDidMove` | Add side-effects when a car is shifted by floating-origin. |
| `CarMover.GetMotionSnapshot` | Modify what's reported to KCC riders / `RemoteAvatar.TRVFromFrame`. |

### Gotchas (KCC plumbing)

- **`KinematicCharacterSystem.OnDisable` `Object.Destroy(gameObject)`** — disabling the system component destroys the singleton. There's no graceful "pause" — disabling effectively re-creates next time `EnsureCreation` runs.
- **Movers register on `OnEnable`, unregister on `OnDisable`.** Disabling a `PhysicsMover` mid-session with `pm.enabled = false` removes it from `KinematicCharacterSystem.PhysicsMovers` — characters attached to it via `AttachedRigidbody` will see the rigidbody go inactive but the KCC's awareness of it lags one tick.
- **`Transform.position` is interpolated, not authoritative.** Always read `_physicsMover.TransientPosition` or `_physicsMover.Rigidbody.position` for truth.
- **`Rigidbody.interpolation = None`** is enforced on registration (`KinematicCharacterSystem.cs:70`). KCC handles its own interpolation via `CustomInterpolationUpdate`. Setting `Rigidbody.interpolation = Interpolate` after registration will be silently overridden next tick.
- **Auto-wake on `Move`** uses `Vector3.Distance(newPos, _physicsMover.TransientPosition)` (with fallback to `_moverPosition` if no mover). A mover that's *enabled* but currently at `(0,0,0)` while you call `Move(somewhereElse, …)` will wake up — `CheckToAwakenMover` only no-ops below 1mm.
- **`IMoverController.UpdateMovement` is allowed to be a no-op getter** — `CarMover` returns the cached `_moverPosition`. The actual movement driving comes from `Move()` calls outside the KCC tick. This is unusual for KCC (most movers compute path inside `UpdateMovement`); for Railroader the train sim drives motion and the mover just reports.

---

## Tile-loading vs avatar lifecycle

See [Tile Loading & Bardo](tile-loading-bardo.md) for the full streaming model. Avatar-specific concerns:

### Local avatar near unloaded regions

- The `AttachedCarChecker` 0.5s loop (above) sets `MapManager.KeepLoaded = currentTile` whenever the player is **not on a car** — so the local player's tile can never be unloaded.
- When **on a car**, no `KeepLoaded` is set; instead `_attachedCarLoadToken = car.ModelLoadRetain("Attached")` keeps the car's model retained against LOD unload (see [Cars & Cargo › ModelLoadRetain](cars-cargo.md)).
- If the player teleports to a region with no tile data, `CheckForTerrainBelow` calls `FixPlayerPositionNoTileData(transientPosition)` which jumps to the closest `SpawnPoint`. The 5-position cardinal rescue (current + ±100m N/S/E/W) is a coarse first attempt before falling back to spawn.

### Remote avatars near unloaded regions

- `RemoteAvatar.TRVFromFrame` (`RemoteAvatar.cs:126`) returns `null` if `RelativeToCarId` is non-null and `TrainController.Shared.CarForId` returns null. Logs `"RemoteAvatar: Car not found for frame: {carId}"`. The avatar **stops updating** until either the car loads or a frame with a different car (or null car) arrives.
- This means remote players riding cars that are in your unloaded tiles **disappear silently** from your view (last-known position frozen). They reappear when their car loads on your machine.
- Free-space (non-car-relative) remote positions don't have this issue — `TRVFromFrame` just `WorldTransformer.GameToWorld(f.Position)` and renders, regardless of tile state.

### Bardo cars

[Tile Loading & Bardo](tile-loading-bardo.md) notes Bardo cars have no character (no body model loaded). Avatar concerns:

- A player whose `_attachedCarId` is on a car that goes Bardo: the car's `_mover.ClearBody()` is called (`Car.cs:1552`) which destroys the `PhysicsMover` and the `Rigidbody`. The KCC's `motor.AttachedRigidbody` then dangles → next `UpdateVelocity` tick the rigidbody check goes false and the player un-attaches → falls in mid-air.
- `Car.WillDestroy(isMovingToBardo:true)` is the correct call site to add a "save player rides" hook. `PlayerController.WillDestroyCar(Car)` is invoked from the camera selector (`CameraSelector.WillDestroyCar`, see [Player & Camera](player-camera.md)) and routes to `character.CarWillBeDestroyed()` → `UnsitUnladder()`. This catches Sit/Ladder cleanup but not the AttachedRigidbody case.
- **Verified**: there's no direct teardown of `motor.AttachedRigidbody` when the rigidbody is destroyed — the KCC reads `AttachedRigidbody == null` next tick (Unity coerces destroyed `Object` to null) and treats it as "fell off," then standard gravity re-applies. So players on a Bardo'd car drop to the (presumably loaded) terrain below. Survival depends on the terrain still being loaded.

---

## Animation state for avatars

See [Animation Playables](animation-playables.md) for the broader Playables/PlayableGraph system. Avatars use a **simpler legacy Animator-only path** — no PlayableGraph involvement.

### `AvatarAnimator`

```csharp
public class AvatarAnimator : MonoBehaviour                              // Avatar/AvatarAnimator.cs:6
{
    private Animator _animator;
    private static readonly int AnimIdVelocityX = StringToHash("velocityX");
    private static readonly int AnimIdVelocityZ = StringToHash("velocityZ");
    private static readonly int AnimIdSit       = StringToHash("sit");
    private static readonly int AnimIdJump      = StringToHash("jump");
    private static readonly int AnimIdLadder    = StringToHash("ladder");

    private Vector3 _lookAtPosition = Vector3.zero;
    private AvatarPose _pose;

    private void Awake()       { _animator = GetComponent<Animator>(); }
    private void OnEnable() {                                            // 29
        string state = _pose switch {
            AvatarPose.Ladder => "SideOfCar",
            AvatarPose.Sit    => "Sit",
            _                 => null,
        };
        if (state != null) _animator.Play(state);
    }
    private void OnAnimatorIK(int layer);                                // 43 — look-at IK weight 0.8
    public void SetVelocity(Vector3 v, Vector3 lookAtPosition);          // 56
    public void SetPose(AvatarPose pose);                                // 63 — sets sit/jump/ladder bools
}
```

The Animator is on the gender-specific `AvatarSet.gameObject` (because `AvatarCustomization.Configure` runs `Animator = AvatarGameObject.GetComponentInChildren<AvatarAnimator>()` — line 53). Switching gender swaps which `Animator` is in use.

**Animator parameters are exhaustively**: `velocityX` (float), `velocityZ` (float), `sit` (bool), `jump` (bool), `ladder` (bool). `OnEnable` plays the named state `"Sit"` or `"SideOfCar"` if entering the GameObject in those poses (because the Animator's bool change won't replay a cross-fade; the `Play` call jumps directly).

### Local-only animation drive

- `LocalAvatar.FixedUpdate` (`LocalAvatar.cs:41`) writes the animator pose every tick from `character.character.GetMotionSnapshot()`, but **does NOT call `SetVelocity`**. Local avatar animation is pose-only — no walking animation when seen in third-person from another camera. (Probably fine: switching to non-FP cameras typically means strategy view, not orbit-around-self.)
- `RemoteAvatar.ApplyToAvatar` writes both `Animator.SetVelocity(v, lookAtTarget)` AND `Animator.SetPose(pose)` every tick.
- **No animation events are replicated** across the network. Pose state is replicated as `AvatarPose` → `CharacterPose` enum (one of Stand/Sit/Jump/Ladder), and per-tick velocity drives the walking animation locally on the receiver.

### Patch candidates (animation)

| Method | Why patch |
|---|---|
| `AvatarAnimator.SetPose` | Custom poses; mod-defined states. Enum extension required for `AvatarPose` first. |
| `AvatarAnimator.SetVelocity` | Custom walking parameter mapping (e.g. running blend). |
| `LocalAvatar.FixedUpdate` | Add `SetVelocity` for the local avatar (vanilla doesn't). |

### Gotchas

- **`AvatarPose` and `CharacterPose` are duplicate enums** (one in `Avatar`, one in `Game.Messages`) with identical members. The transmitter casts: `(CharacterPose)pose` in `CharacterPositionTransmitter.cs:52`, and the receiver casts: `(AvatarPose)updateCharacterPosition.Pose` in `StateManager.cs:1043`. Adding a new pose value requires changing **both** enums in lockstep — and the Pose field is wire-format `int`, so adding values mid-list will desync existing clients.
- **`OnEnable` re-plays the state animation** but `OnDisable` does nothing — the animator is left whatever state the bools were last in. On re-enable, if `_pose` is `Stand` or `Jump`, no `Play` happens and the animator state may persist from a previous activation.
- **Look-at IK uses fixed weight 0.8**. The look-at target is set by `RemoteAvatar.ApplyToAvatar` to `position + look * Vector3.forward * 10f` (10m ahead in the look direction). Local avatar's look-at is left at `(0,0,0)` (so weight goes to 0 in `OnAnimatorIK`).

---

## Per-machine vs per-player divergence

| State | Local | Per-machine | Sync mechanism |
|---|---|---|---|
| `LocalAvatar._avatar` (visible body) | yes | each machine spawns its own | none — created on-demand when leaving FP |
| `Preferences.AvatarDescriptor` | yes | yes (PlayerPrefs key `avatar.descriptor`) | none — sent via `AddUpdateCharacter` when changed |
| `PlayerController` / `CharacterController` motor state | yes | unique to local | broadcast position only via `UpdateCharacterPosition` |
| `RemoteAvatar` per-player | each machine has one per other player | yes | `AddPosition` from `UpdateCharacterPosition` after StateManager dispatch |
| `RemoteAvatar.RelativeToCarId` interpretation | car-local positions transformed to world via `TRVFromFrame` | yes (each receiver does its own GameToWorld via the local car's MotionSnapshot) | car-local frames sent over wire |
| Floating-origin offset | yes | per-machine | NOT synced (see [Floating Origin](floating-origin.md)) |

**The per-machine floating origin combined with car-local-positions in MP messages is what makes long-distance multiplayer work**: each receiver translates the car-local frame to *its own* world space using the car's local `MotionSnapshot`, then `WorldTransformer.GameToWorld` puts it in front of the camera. No machine ever sends an absolute world coordinate that another machine has to interpret.

---

## MP authority & wire-format reference

### Message catalog (avatar/character-relevant)

| Message | Auth | Routing | Payload |
|---|---|---|---|
| `UpdateCharacterPosition` | `MinimumAccessLevel(Passenger)` | client → host → other clients | `CharacterPosition (Vector3 Position, string RelativeToCarId, Vector3 Forward, Vector3 Look) + Vector3 Velocity + CharacterPose Pose + long Tick` |
| `AddUpdateCharacter` | `MinimumAccessLevel(Passenger)` | client → host → other clients | `string Name + Snapshot.CharacterCustomization` |
| `UpdateCameraPosition` | `MinimumAccessLevel(Passenger)` | client → host (host caches in `PlayersManager._lastKnownPositions`; **NOT re-broadcast**) | `Vector3` (game-space) |

`UpdateCharacterPosition` MessagePack `[Union(102, …)]` (`IGameMessage.cs:31`); `AddUpdateCharacter` `[Union(100, …)]` (`IGameMessage.cs:30`).

**Anti-cheat: none.** Position is fully client-authoritative. Host doesn't validate where you say you are — it just accepts and re-broadcasts. The only validation in `RecordState` (`StateManager` host side) is `StringSanitizer.SanitizeName` on the `AddUpdateCharacter.Name` field.

### Snapshot persistence

- Each `Snapshot.Player` holds the player's last-known `Position : CharacterPosition` and `Customization : CharacterCustomization`.
- `HostManager.RecordState` (`HostManager.cs:1062`) writes both into `_snapshot.players[senderId]` and into `_playerRecords[playerId]` on every relevant message.
- On player rejoin: `PlayersManager.RestoreFromSnapshot` → `HandleSnapshotPlayers` (`PlayersManager.cs:154`) → for each non-local player, `CreateRemotePlayer` + `ConfigureAvatar(snapshot.Position, snapshot.Customization)` immediately seeds the RemoteAvatar with one frame.
- For the local player on restore: `RestoreCharacterPosition(player)` (`PlayersManager.cs:195`) — if `Position.Position == Vector3.zero` it's a no-op (typical fresh save); if `RelativeToCarId` is set, `CameraSelector.JumpToCar`; else `CameraSelector.JumpCharacterTo`.

### The destructive `HandleSnapshotPlayers` rebuild

Per [Players & TrainCrew](players-traincrew.md), every `PlayerList` arrival from the network calls `OnRemotePlayersDidChange` → `HandleSnapshotPlayers` → `ClearRemotePlayers()` (destroys every `RemotePlayer` GameObject) and re-creates them all. **Implications for avatar mods:**

- Any state attached to a `RemotePlayer` MonoBehaviour as a sibling component dies on every PlayerList tick.
- The `RemoteAvatar` MonoBehaviour and its underlying `AvatarPrefab` GameObject are destroyed and re-spawned. The `_frames` `CircularBuffer<Frame>(4)` is empty after rebuild — there's a brief frame gap until the next `UpdateCharacterPosition` arrives.
- **`ConfigureAvatar` re-seeds with one synthetic frame at `StateManager.Now` with `Velocity = Vector3.zero` and `Pose = Stand`** (`RemotePlayer.cs:55`). So immediately after rebuild the avatar appears stationary at last-known position — possibly briefly lerping from there before the first real frame arrives.
- Mods needing per-remote-player state must (a) re-attach in a `Messenger.Default.Register<PlayersDidChange>` handler, or (b) key state by `PlayerId` in a static dictionary (which survives the GameObject churn).

---

## Patch points: cookbook

### Custom avatar models

**Goal**: replace the visual rig with a different mesh / rig, per-player or globally.

Per-player: postfix `AvatarManager.AddAvatar(AvatarDescriptor, bool, PlayerId, string)`. Receive the spawned `AvatarPrefab`, swap children / disable/enable mesh roots based on `playerId` (or other criteria). Keep the `AvatarCustomization`, `AvatarPickable`, `MapIcon`, and Rigidbody intact — downstream code reads them.

Globally: replace the `AvatarManager.avatarPrefab` SerializeField at scene load. Find via `AvatarManager.Instance` and reflectively assign before any `AddAvatar` call (i.e. before the first `LocalAvatar.SetupAvatarIfNeeded` and before any `RemotePlayer.AddUpdateAvatar`). Pre-`MapDidLoad` is the safe window.

For new accessory categories: extend `AvatarSet.accessories[]` on your replacement prefab. Then patch `CharacterSettingsBuilder.BuildCharacterPanel` (or call it via your own UI) to add the dropdown that produces the right `Value` in the descriptor's accessories dictionary. **No code path validates accessory IDs** — unknown IDs are silently `Value.Null()` from `SelectedOptionForAccessory`.

### Custom character physics

- Per-character: replace `CharacterController`'s tunable fields at runtime (write to the public fields). Lives on the same GameObject as `PlayerController.character`; reach via `CameraSelector.shared.character.character`.
- Per-pose: prefix `CharacterController.UpdateVelocity` and short-circuit with your own velocity logic for matching `CurrentCharacterState`.
- Add a new state: extend `CharacterState` enum (requires patching the switch statements in `OnStateEnter`/`OnStateExit`/`UpdateVelocity`/`AfterCharacterUpdate`/`SetInputs`). Easier to repurpose `Default` with mode flags.
- New movement mode (swimming, climbing): patch `CharacterController.UpdateVelocity`'s Default branch to detect entry/exit of your mode (e.g. via overlap probe like vanilla `ProbeForCollider` does for ladders) and substitute custom motion.

### Avatar attachment to mod-defined moving objects

The KCC AttachedRigidbody mechanism works for any kinematic Rigidbody on a `PhysicsMover`. To make a mod-defined object rideable:

1. `AddComponent<Rigidbody>` (will be made kinematic by `PhysicsMover.ValidateData`).
2. `AddComponent<PhysicsMover>` and assign `MoverController = yourIMoverControllerImpl` (or just use direct `SetPosition` calls).
3. Make sure `PhysicsMover.OnEnable` runs (it auto-registers with `KinematicCharacterSystem`).
4. Player `motor.AttachedRigidbody` will pick it up when the KCC's grounding probe finds it as the surface beneath the player's feet.

For seat/ladder attachment to mod objects: a `Seat` or `Ladder` MonoBehaviour with a parent that has a `PhysicsMover` will set `motor.AttachedRigidbodyOverride` correctly via `_seat.GetComponentInParent<PhysicsMover>()?.Rigidbody`. So your mod-rideable just needs to be the parent of any seat/ladder MonoBehaviours you place on it.

For per-frame attachment force (e.g. `PlayerController.AttachTo`-style), call `character.motor.ApplyState(new KinematicCharacterMotorState { … AttachedRigidbody = yourRb, … })` directly. See [`PlayerController.AttachTo`](#attachto-riding-a-moving-car) for the synthetic-grounding pattern.

For MP correctness: the player's transmitter sends position relative to the `Car` returned by `GetRelativeCar()`. Mod-defined rideable objects aren't `Car`s, so the transmitter sends absolute world coordinates while you're on them — receivers won't track car-relative motion. Patch `GetRelativeCar` to also detect your rideable, OR patch `PlayerController.GetRelativePositionRotation` to do object-local conversion against your transform. Note: `CharacterPosition.RelativeToCarId` is a string — receivers look it up in `TrainController.Shared.CarForId`. To ride non-Car objects via the vanilla MP path you'd need a parallel registry or an id collision-safe scheme (e.g. prefix mod ids and patch CarForId / TRVFromFrame).

---

## Gotchas

- **`Lantern` accessory is sent in the descriptor but its visual binding is not in vanilla `AvatarCustomization` (the prefab's `accessories` array has no documented `"lantern"` reference).** The `lantern : GameObject` field on `AvatarPrefab` is force-disabled in `Awake` and only flipped via `LocalAvatar.LanternEnabled` setter (`LocalAvatar.cs:27`). Remote-side, `AddUpdateCharacter` arriving with `accessories.lantern = true` runs `Customization.Configure(descriptor)` — which only flips gameObjects listed under accessory entries. So **the lantern visibility is local-only on the local avatar; remote rigs don't show your lit lantern unless the prefab adds an explicit `lantern` accessory entry.**
- **`LocalAvatar.lanternOffset` is unused.** Set on the field but never read in vanilla code. Cosmetic remnant.
- **`AvatarManager.RemoteAvatarNear(pos)` only checks first-level children** — if your mod re-parents `RemoteAvatar` GameObjects under a sub-organizing transform, they'll be missed by the proximity check used in `CameraSelector.FindBestSeat`. (Two players seated on the same seat is fine in vanilla; it just means the auto-find-best-seat logic may pick an "occupied" seat.)
- **`AvatarPickable.MaxPickDistance = 500f`** — far longer than most pickables. If your mod adjusts pick-distance globally, avatars become un-clickable from far.
- **`AvatarTester.OnValidate` runs `Configure`** on every inspector change — useful for dev, but if you ship an `AvatarTester` accidentally in a mod scene it'll mutate the avatar visual every editor frame.
- **Skin tones are hard-coded to 2** in `CharacterSettingsBuilder.cs:82`. Adding more requires both adding `skinMaterials[]` entries to each AvatarSet **and** patching `CharacterSettingsBuilder.AddDropdownFieldSkinTone`.
- **`Preferences.AvatarDescriptor` PlayerPrefs key is `"avatar.descriptor"`**, stored as `KeyValueJson` text. Errors → falls back to `AvatarDescriptor.Default`.
- **`AvatarDescriptor.From(Value)` requires `accessories` key present.** Missing key → `KeyNotFoundException`. Saved descriptors before a mod's `accessories` field shape change will throw — wrap in try/catch.
- **`RemoteAvatar.Delay = 300L` and `_frames = new CircularBuffer<Frame>(4)` are `const`/`readonly`.** Cannot be tuned without patching. The 4-deep buffer at 100ms send rate (10 Hz minimum) covers ~400ms of buffer — barely more than the 300ms display delay, which means **bursty packet loss past 1 dropped frame can starve the buffer to single-frame extrapolation territory**. Mods adding extrapolation tolerance should patch `UpdatePosition` rather than tweak the buffer size.
- **`Extrapolate` clamps to 1s ahead** (`RemoteAvatar.cs:82` `a = Mathf.Min(a, 1f)`). Players who lag more than ~700ms behind the display tick (300ms delay + 1s extrapolation) will freeze in place rather than continue extrapolating.
- **`TRVFromFrame` returns null for missing cars** but `MoveBetween`'s null-check applies to *both* endpoints — a one-frame partial state where one endpoint resolves and the other doesn't drops the whole tick. Long unloaded periods cause the avatar to vanish until *both* frames have valid car snapshots.
- **`_frames.Enqueue` overflowing past 4 silently drops** the oldest frame. No log, no event. Adversarial server traffic can starve the receiver.
- **`MakeCharacterCustomizationUsingPreferences` injects lantern into a copy** of the descriptor — the local `Preferences.AvatarDescriptor` is *not* mutated. So toggling lantern persists for the session via `LocalAvatar.LanternEnabled = !...` but doesn't write to PlayerPrefs.
- **`ConfigureAvatar` synthetic frame uses `AvatarPose.Stand`** (`RemotePlayer.cs:55`) regardless of the actual pose at snapshot time. Players seated/laddered when they disconnect appear standing for one frame on rejoin, then snap to the correct pose when the first live `UpdateCharacterPosition` arrives.
- **`HandleSnapshotPlayers` `ClearRemotePlayers()` destroys all RemotePlayer GameObjects.** Then `CreateRemotePlayer` makes new ones. AvatarManager.AddRemote is called via `RemotePlayer.AddUpdateAvatar` → another `AvatarManager.AddAvatar` → another GameObject hierarchy. If your mod adds components to the `AvatarPrefab` instance, they die on every PlayerList tick.
- **`Seat.FootPosition = transform.position - 0.47 * up`** with a `Start()` raycast that *reduces* `_seatToFeet` if there's geometry within 1m below — never increases. Tall seats (like locomotive cab seats with no floor immediately below) get the default 0.47m drop. Mod seats placed in midair without a floor below them have the player floating 0.47m off their position.
- **`CharacterPositionTransmitter.SendIfConnected` resets `_lastSentTick` to 0** if the time delta exceeds 20s (`CharacterPositionTransmitter.cs:31`) — this is a sanity guard against StateManager.Now jumping (e.g. on save/load). After reset, the next send is forced.
- **`UpdateCharacterPosition.Tick` is `StateManager.Now` at send time (sender's clock).** Receivers compute `t = inverseLerp(frame0.Tick, frame1.Tick, DisplayTick)` where `DisplayTick = StateManager.Now - 300` on the *receiver's* clock. Clock skew between sender and receiver introduces interpolation error proportional to skew. `NetworkTime.Elapsed(tickA, tickB)` is the helper that converts tick deltas to seconds.
- **Anti-cheat absence**: a malicious client can teleport its character via crafted `UpdateCharacterPosition` messages with arbitrary `Position`. There's no host-side bounds check, no speed check, no clipping check. Mods adding anti-cheat must patch `StateManager.HandleCharacterMessage` host-side.

---

## Cross-references

- **Camera modes & teleport**: see [Player & Camera](player-camera.md) for `CameraSelector`, `JumpCharacterTo`, `JumpToCar`, `JumpToPoint`, the FP/Strategy/Dispatcher mode switcher, and the full `RemoteAvatar` interpolation pipeline (this sheet links into specific subsections).
- **Floating origin & WorldDidMoveEvent**: see [Floating Origin](floating-origin.md) for the offset event, per-machine no-MP-sync behavior, and the broader subscriber catalog (CharacterController and PhysicsMover are two consumers among many).
- **Player IDs, remote-player rebuild hazard**: see [Players & TrainCrew](players-traincrew.md) for `PlayersManager.HandleSnapshotPlayers`, `PlayerId`, `PlayerRecord`, and the destructive PlayerList tick.
- **Multiplayer message routing & access levels**: see [Multiplayer survey](../multiplayer-vanilla-survey.md) for `StateManager.PropagateGameMessage` and the Passenger/Crew/Trainmaster/Officer/Host hierarchy.
- **Tile loading, Bardo cars, ModelLoadRetain**: see [Tile Loading & Bardo](tile-loading-bardo.md) for what happens to the car your `_attachedCarId` points at when it streams out or goes Bardo.
- **Animation system context** (Playables, AnimationMap, etc): see [Animation Playables](animation-playables.md). Avatars use a plain Animator and don't participate in the PlayableGraph machinery.
- **Pickable dispatch** for `AvatarPickable` and `CouplerPickable` etc.: see [Interaction Controls](interaction-controls.md).
- **Input handling** for movement, lean, jump, lantern toggle: see [Input & Keybinds](input-keybinds.md).
- **Car body / `PhysicsMover` / `CarMover.ConfigureForBody`**: see [Cars & Cargo](cars-cargo.md) for the broader car lifecycle that wraps the mover setup. Also [Locomotive Architecture](locomotive-architecture.md) for the per-loco specialization.
