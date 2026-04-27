# Input & Keybinds — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [Player & Camera](player-camera.md), [Multiplayer survey](../multiplayer-vanilla-survey.md)

Railroader runs on Unity's **new InputSystem** (`UnityEngine.InputSystem`) for almost all gameplay actions. The `GameInput` MonoBehaviour is the singleton facade: it loads an `InputActionAsset` (`inputActions`, set in the scene), caches every `InputAction` into a typed property (`CameraSelectFirstPerson`, `Teleport`, etc.), and exposes per-frame helpers (`WasPerformedThisFrame`, `IsPressed`). Mouse axes (`Mouse X`/`Y`) and a few mouse-button reads still go through legacy `UnityEngine.Input` — that's the *only* legacy path. Rebinding goes through the standard new-InputSystem `InputActionRebindingExtensions.PerformInteractiveRebinding`, with overrides serialized to `PlayerPrefs` under the key `rebinds` as a JSON blob produced by `inputActions.SaveBindingOverridesAsJson()`.

**Why your `Keyboard.current.f8Key.wasPressedThisFrame` works while `Input.GetKeyDown` may not:** vanilla doesn't disable the legacy input module — `Input.GetKey(KeyCode.LeftShift)` works in `GameInput.IsShiftDown` (`GameInput.cs:392`). But the InputSystem package, when active, *can* be configured to disable the old input backends entirely (player settings: "Active Input Handling = Input System Package (New)"). If Railroader ships with that setting, `Input.GetKeyDown(KeyCode.F8)` becomes a no-op while `Keyboard.current.f8Key` works — explaining your experiment. The vanilla code that *does* use legacy `Input` (mouse axes / mouse buttons) works because Unity provides automatic legacy-API shims for `Mouse` and the `"Mouse X"` / `"Horizontal"` axes regardless. If you target keyboard input via legacy in a mod, prefer `Keyboard.current` for safety.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `UI.GameInput` (singleton `shared`) | `UI/GameInput.cs:33` | Action map facade. Initializes from `InputActionAsset inputActions`, exposes typed accessors |
| `UI.GameInput.inputActions` | `UI/GameInput.cs:54` | The serialized `InputActionAsset` (set in the scene) |
| `UI.GameInput.RebindableActions` | `UI/GameInput.cs:223` | Tuple `(title, InputAction[])[4]`: Movement / Camera / UI / Equipment groups for the rebind UI |
| `UI.GameInput.SaveToPlayerPrefs()` | `UI/GameInput.cs:716` | `PlayerPrefs.SetString("rebinds", asset.SaveBindingOverridesAsJson())` |
| `UI.GameInput.LoadFromPreferences()` | `UI/GameInput.cs:707` | Inverse: load JSON, `LoadBindingOverridesFromJson` |
| `UI.PressInput` | `UI/PressInput.cs:7` | Short-vs-long press detection on `Game/ActivatePrimary` and `Game/ActivateSecondary` |
| `UI.VirtualRepeatingInput` | `UI/VirtualRepeatingInput.cs:7` | Held-key auto-repeat (default 50ms) wrapper around an `InputAction` |
| `UI.InputRebind.RebindActionUI` | `UI.InputRebind/RebindActionUI.cs:12` | Per-binding rebind UI element. Driven by `PerformInteractiveRebinding` |
| `UI.InputRebind.RebindSaveLoad` | `UI.InputRebind/RebindSaveLoad.cs:6` | OnEnable/OnDisable PlayerPrefs sync (alternate to `GameInput.SaveToPlayerPrefs`) |
| `UI.PreferencesWindow.BindingsWindow` | `UI.PreferencesWindow/BindingsWindow.cs:11` | The "Bindings" window (tabbed by group). Conflict detection lives here |
| `UI.PreferencesWindow.PreferencesBuilder.BuildTabInput` | `UI.PreferencesWindow/PreferencesBuilder.cs:79` | Settings → "Input" tab. Hosts mouse-look prefs + "Customize Bindings" button |
| `UI.Builder.UIPanelBuilder.AddInputBindingControl` | `UI.Builder/UIPanelBuilder.cs:681` | Instantiates a `RebindActionUI` widget |
| `MovementInput` (static) | `MovementInput.cs:1` | `CalculateSpeedFromInput(normal, fast, super)` based on Run/VeryFast modifiers |
| `TrainInput` | `TrainInput.cs:12` | Per-frame loco control input (throttle/brake/horn/etc.) |

---

## Action map spine

```
InputActionAsset (gameInput.inputActions, scene-assigned)
    │
    ├── Action map: "Game"          ← all gameplay bindings; Enabled iff InputMode=Move
    │     (5 dozen actions: Move, Run, Jump, Crouch, CameraSelect*, Throttle*, Brake*, …)
    │
    ├── Action map: "Global"        ← always-on, regardless of input mode
    │     ("ShowPauseMenu" — that's the only one referenced in vanilla)
    │
    └── (any other maps, not referenced by GameInput.cs)

GameInput.Awake() (line 464)
    │
    ├── inputActions.FindActionMap("Game", throwIfNotFound:true)   → cached _gameActionMap
    └── inputActions["Game/<Name>"]    one cache field per action (~50 InputAction fields)

GameInput.OnEnable() (line 549)
    │
    ├── inputActions.Enable()
    ├── LoadFromPreferences()                                       ← read PlayerPrefs "rebinds"
    └── new VirtualRepeatingInput(...)   for repeating actions      ← reverser / throttle / brake

GameInput.Update() (line 584)
    │
    ├── Detect TMP_InputField focus or pause →  switch InputMode    ← Move ↔ UI
    │     Move-mode  ⇒ _gameActionMap.Enable()
    │     UI-mode    ⇒ _gameActionMap.Disable()                     ← "Global" stays enabled
    ├── _showPauseMenuAction.WasPerformedThisFrame() → HandleEscape (always — uses Global map)
    ├── _closeWindowAction.WasPerformedThisFrame() → WindowManager.Shared.CloseTopmostWindow()
    └── if (MovementInputEnabled) { handle map/help/equipment toggles, lantern, push, … }
```

### `InputMode`

```csharp
private enum InputMode { Move, UI }                           // GameInput.cs:36
private static InputMode _inputMode = InputMode.Move;
public  static bool MovementInputEnabled => _inputMode == InputMode.Move;
```

`Update` switches mode based on `EventSystem.current.currentSelectedGameObject` having a `TMP_InputField` *or* `_focusPause` being true (`GameInput.SetPaused(bool)`). On the transition, the `Game` action map is enabled/disabled. **`Global` stays enabled in UI mode** — that's why ESC/pause always works.

**Patch point:** `GameInput.SetPaused(bool)` is the public toggle. Mods opening modal UI that should suppress gameplay input should call `GameInput.shared.SetPaused(true)`.

---

## The Action set

50+ actions are bound in `GameInput.Awake` (`GameInput.cs:464-527`). They cluster into four groups (the `RebindableActions` tuple at line 528):

### Movement (11)

```
Game/Move                  (Vector2 — WASD composite)
Game/Run                   (Button — modifier)
Game/VeryFast              (Button — modifier)
Game/Jump                  (Button)
Game/Crouch                (Button)
Game/Teleport              (Button — jump-to-mouse)
Game/LeanLeft              (Button)
Game/LeanRight             (Button)
Game/PlaceFlare            (Button)
Game/ResetFieldOfView      (Button)
Game/ToggleLantern         (Button)
Game/Query                 (Button — "?" inspector)
```

### Camera (9)

```
Game/CameraSelectFirstPerson
Game/CameraSelectOverhead       ← strategy
Game/CameraSelectDispatcher
Game/CameraOverheadToCharacter  ← jump strategy cam to your avatar
Game/CameraFollowHead
Game/CameraFollowTail
Game/CameraJumpToHead
Game/CameraJumpToTail
Game/CameraJumpToSeat
```

### UI (13)

```
Game/Help                       Game/CloseWindow              Game/ToggleMap
Game/ToggleCompanyWindow        Game/TogglePreferencesWindow  Game/ToggleEngineRoster
Game/ToggleTags                 Game/ToggleSwitchList         Game/ToggleTimetable
Game/ToggleTimeWindow           Game/TogglePhotoMode          Game/ToggleConsole
Game/ToggleContextMenu
```

### Equipment (19)

```
Game/TogglePlacer              Game/Horn                       Game/Bell
Game/HeadlightNext             Game/HeadlightPrevious          Game/CylinderCock
Game/PushCar                   Game/ReverserForward            Game/ReverserBack
Game/ThrottleUp                Game/ThrottleDown               Game/TrainBrakeApply
Game/TrainBrakeRelease         Game/LocomotiveBrakeApply       Game/LocomotiveBrakeRelease
Game/MoveTrain                 Game/RecallSelection            Game/CycleSelection
Game/AutoEngineerWaypointSelect
```

### Not in `RebindableActions` (still bound)

```
Game/HornExpressionEnable      Game/HornExpressionValue        Game/CopyLocationToClipboard
Game/QuickSearch               Game/FastForwardUp              Game/FastForwardDown
Game/ActivatePrimary           Game/ActivateSecondary          ← used by PressInput, NOT GameInput
Global/ShowPauseMenu           ← lives in "Global" map
```

The four `RebindableActions` tuples are explicitly the rebindable subset; the rest are either composite-helper actions, hidden, or modal/internal. Mods adding new actions can append to any tuple but must do so before `BindingsWindow.Build` runs (or rebuild the window).

---

## `UI.GameInput` (the action facade)

Singleton: `GameInput.shared` (set in `Awake`). Lives on a scene MonoBehaviour with `InputActionAsset inputActions` SerializeField'd. `PressInput pressInput` is also serialized and references this `GameInput` for the action asset.

### Typed accessors (selected, full list ~60)

```csharp
public Vector2 MoveVector            => _moveAction.ReadValue<Vector2>();             // 225
public bool    ModifierRun           => _runAction.IsPressed();                       // 227
public bool    ModifierVeryFast      => _veryFastAction.IsPressed();                  // 229
public bool    Teleport              => _teleportAction.WasPerformedThisFrame();      // 231
public bool    PlaceFlare            => _placeFlareAction.WasPerformedThisFrame();    // 233
public bool    HornExpressionEnabledThisFrame=> _hornExpressionEnableAction.WasPerformedThisFrame();
public bool    HornExpressionEnabled => _hornExpressionEnableAction.IsPressed();
public float   HornExpressionValue   => _hornExpressionValueAction.ReadValue<float>();
public int     InputHeadlight        { get; }   // ±1 or 0, computed from two actions
public float   InputHorn             { get; }   // 0.3 or 1 based on shift
public bool    ShowHelp              => _help.WasPerformedThisFrame();
public bool    CameraJumpToSeat      => _cameraJumpToSeat.WasPerformedThisFrame();
// … one property per InputAction, 60+ total.
```

Two patterns:
- **Edge** (`WasPerformedThisFrame`) for tap-style actions (camera switches, toggles).
- **Hold** (`IsPressed`) for modifiers (Run, VeryFast, Lean) and continuous (Horn, HornExpressionEnable).
- **Repeat** via `VirtualRepeatingInput` — see below — for analog-feeling controls (throttle, brake, reverser).

### `GetMovement(normalSpeed, fastSpeed, fasterSpeed)` (line 722)

The "WASD + run modifier → 3D vector" helper used by both `StrategyCameraController.UpdateInput` and `DroneController.FixedUpdate`:

```csharp
public Vector3 GetMovement(float normalSpeed, float fastSpeed, float fasterSpeed) {
    if (!MovementInputEnabled) return Vector3.zero;
    float speed = ModifierVeryFast ? fasterSpeed : (ModifierRun ? fastSpeed : normalSpeed);
    Vector2 mv = MoveVector;
    Vector3 v = new Vector3(mv.x, 0, mv.y);
    if (LeanLeft)  v += Vector3.up;
    if (LeanRight) v -= Vector3.up;
    return v.normalized * speed;
}
```

Note that `LeanLeft`/`LeanRight` (Q/E by default) double as Y-axis movement for cameras — this is how the strategy camera tilts up/down with Q/E while the FP body uses them for leaning.

### Mouse + scroll (still legacy)

```csharp
public Vector2 LookDelta => new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));   // 332
public float   ZoomDelta {                                                                            // 334
    get {
        if (!IsMouseOverGameWindow()) return 0f;
        if (IsMouseOverUI(out _, out _)) return 0f;
        return Input.mouseScrollDelta.y;
    }
}
```

These are the only places `Input.GetAxisRaw` / `Input.mouseScrollDelta` are read in `GameInput`. The new InputSystem provides `Mouse.current.delta` / `Mouse.current.scroll` equivalents but vanilla doesn't use them. **If you write a mod that wants to override mouse behavior, patch these properties** (use `Mouse.current.delta.ReadValue()` for new-system equivalence).

### Modifier keys (legacy `Input.GetKey` path)

```csharp
public static bool IsShiftDown => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);  // 388
public static bool IsControlDown => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
private static bool IsAltDown   => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
public  static bool SmartAirHelperModifier => IsShiftDown && !IsControlDown;
```

These read **legacy `Input.GetKey`**, not the new InputSystem. They work because Unity's input compatibility shim is on (`Active Input Handling = Both`), or because the new system also exposes legacy as a fallback. **Important consequence:** if a mod enables only the new InputSystem backend, these properties will silently report `false`. The `SmartAirHelperModifier` is read in `Car.HandleCouplerClick` (see [Couplers](couplers.md#cut-lever-pipeline-player-driven-uncouple)) — so a broken modifier would silently disable the smart-air-helper feature.

### Mouse positions (mixed)

```csharp
public static bool IsMouseOverGameWindow(Window window = null) {                                     // 761
    Vector2 vector = Mouse.current.position.ReadValue();        // ← new InputSystem
    if (vector.x<0 || vector.y<0 || Screen.width<vector.x || Screen.height<vector.y) return false;
    return WindowManager.Shared.HitTest(vector) == window;
}

public static bool IsMouseOverUI(out TooltipInfo tooltipInfo, out string debugInfo) {                // 771
    Vector3 mousePosition = Input.mousePosition;                // ← legacy
    // … EventSystem.RaycastAll, panel raycaster, etc.
}
```

Inconsistent: `IsMouseOverGameWindow` uses `Mouse.current.position` (new); `IsMouseOverUI` uses `Input.mousePosition` (legacy). Both currently work because Unity's input compatibility is on.

### Escape handling

```csharp
public enum EscapeHandler { Pause, Transient, QuickSearch }                              // 41
public static void RegisterEscapeHandler(EscapeHandler, Func<bool> action);              // 821
public static void UnregisterEscapeHandler(EscapeHandler);                               // 826
private void HandleEscape();   // Transient → QuickSearch → Pause priority order        // 831
```

`HandleEscape` (line 831) walks `Transient → QuickSearch → Pause` and stops at the first handler whose `Func<bool>` returns true. Use `RegisterEscapeHandler(Transient, () => { /* close my modal */; return true; })` for transient escape behavior.

### Patch candidates

| Method | Why patch |
|---|---|
| `GameInput.Awake` | Append to `RebindableActions` to add mod actions to the bindings UI. Or rebuild the cache. |
| `GameInput.Update` | Add custom global hotkeys without subclassing. Filter inputs in transient modes. |
| `GameInput.SaveToPlayerPrefs` | Add custom-action persistence atop the `rebinds` blob. |
| `GameInput.LoadFromPreferences` | Inverse — load custom mod overrides. |
| `GameInput.LookDelta` (getter) | Replace mouse axes (e.g. controller stick, custom smoothing). |
| `GameInput.ZoomDelta` (getter) | Replace zoom source. |
| `GameInput.IsShiftDown` / `IsControlDown` | Replace if you change input backend. |
| `GameInput.SetPaused(bool)` | Programmatic input lock — mods opening their own modals should call this. |

### Gotchas

- **`shared` is set in `Awake` but `inputActions.Enable()` is in `OnEnable`.** Reading actions before `OnEnable` (e.g. from another `Awake` that runs first) gives stale values.
- **`_gameActionMap` is enabled/disabled per InputMode**, but **other maps in the asset are not touched**. If you add a new map (e.g. "ModSpecific") in the asset, it stays enabled in UI mode unless you explicitly disable it.
- **`_showPauseMenuAction` is from `Global/ShowPauseMenu`** — the only thing read from a non-`Game` map. Lives outside the InputMode gating.
- **`InputMode` toggle relies on `EventSystem.current.currentSelectedGameObject`** having a `TMP_InputField`. If you use a non-TMP input field, the mode won't switch and gameplay input will keep firing through your text typing.
- **`_focusPause` is set by `SetPaused`** but vanilla never calls it from anywhere visible — it's hooked up as a public API for mods/UI extensions. Currently only changes via the unscanned hooks.
- **`GameInput.shared.MovementInputEnabled` is the universal "is gameplay accepting input" flag.** Most subsystems honor it (CameraSelector, PlayerController, TrainInput, StrategyCameraController). Adding a mod that polls input directly should also check `GameInput.MovementInputEnabled` to play nice with the pause / focus / mode model.
- **`RebindableActions` is a `(string title, InputAction[] actions)[4]` array** — a tuple, not a `List`. To add a 5th group, you must replace the array (Harmony postfix on `Awake`).
- **The actions are cached in fields on `GameInput`** — if a mod tries to look up an action via `inputActions["Game/Whatever"]`, that's fine, but the cached field copy on `GameInput` will not reflect mid-game asset edits. Always go through the InputActionAsset for new actions.

---

## `UI.PressInput` (short vs. long press distinguisher)

Component bound to `Game/ActivatePrimary` and `Game/ActivateSecondary` (typically left/right mouse). Distinguishes "click" (≤200ms with little movement) from "long press / drag" (>200ms or moved >6 px).

```csharp
public class PressInput : MonoBehaviour {                     // PressInput.cs (DefaultExecutionOrder = -100)
    private const float MovedThreshold = 6f;                  // pixels
    private ActivateState _primaryState;    // threshold 0.2s, action = Game/ActivatePrimary
    private ActivateState _secondaryState;  // threshold 0.2s, action = Game/ActivateSecondary

    public bool PrimaryPressStartedThisFrame   { get; }       // short press OR long-press start
    public bool PrimaryPressEndedThisFrame     { get; }
    public bool SecondaryPressedThisFrame      { get; }       // short only
    public bool SecondaryLongPressBeganThisFrame { get; }
    public bool SecondaryLongPressEndedThisFrame { get; }
}
```

Drives `MouseLookInput` (Long-press to begin/end mouse look, see [Player & Camera](player-camera.md#camerasmouselookinput-mouse--pitchyaw)) and the click-vs-context-menu disambiguation in `GameInput.ActivateSecondary` (line 316). Mouse position diff > 6 px also counts as a long press (catches drags as "non-click").

**`DefaultExecutionOrder = -100`** runs PressInput before most other Update()s, so by the time GameInput.Update reads `pressInput.SecondaryPressedThisFrame`, it's already computed.

---

## `UI.VirtualRepeatingInput` (held-key auto-repeat)

Wraps an `InputAction` to provide repeated `ActiveThisFrame` ticks while held. Used for granular controls (throttle/brake/reverser) where the player wants "hold to keep applying."

```csharp
public class VirtualRepeatingInput : IDisposable {            // VirtualRepeatingInput.cs
    public VirtualRepeatingInput(InputAction inputAction, float repeatInterval = 0.05f);
    public bool ActiveThisFrame();                            // true on initial press, then every interval
    public void Dispose();                                    // unsubscribes from started/canceled
}
```

`GameInput` constructs **two** repeaters per granular control — slow (`0.25f`) and fast (default `0.05f`):

```csharp
_reverserForwardSlowRepeating = new VirtualRepeatingInput(_reverserForward, 0.25f);
_reverserForwardRepeating     = new VirtualRepeatingInput(_reverserForward);            // 0.05f
// (4 controls × 2 = 8 reverser/throttle repeaters; brakes get only the default rate)
```

So `GameInput.ReverserForward` reads `_reverserForwardSlowRepeating` (4Hz, e.g. notch-step reverser) and `GameInput.ReverserForwardRepeating` reads the fast one (20Hz, smooth ramp). `TrainInput` chooses between them based on locomotive type (diesel = boolean tap, steam = continuous). See `TrainInput.cs:60-69` for the diesel/steam branching.

**Implementation**: subscribes to `InputAction.started` / `canceled` via callbacks (line 21-28). Dispose required to avoid leaks across scene unload.

---

## Rebinding pipeline

```
Settings → Input tab → "Customize Bindings" button
   ↓
BindingsWindow.Show()                                         ← UI.PreferencesWindow/BindingsWindow.cs:56
   ↓ Populate() → UIPanel.Create
   ↓ Build(builder)
   ↓     uses GameInput.shared.RebindableActions (the 4-tuple)
   ↓     for each (title, actions[]):
   ↓         builder.AddTab → builder.VScrollView →
   ↓             builder.AddInputBindingControl(action, conflict, DidRebind)
   ↓                                                          ← UIPanelBuilder.cs:681
   ↓                  ↓
   ↓             instantiates RebindActionUI prefab
   ↓             rebindActionUI.Action = action
   ↓             rebindActionUI.BindingId = action.bindings[0].id  ← FIRST binding only
   ↓             rebindActionUI.OnSave  = DidRebind
   ↓
   click "rebind" → RebindActionUI.StartInteractiveRebind()  ← RebindActionUI.cs:327
   ↓
   PerformInteractiveRebind(action, bindingIndex)             ← line 348
   ↓
   action.PerformInteractiveRebinding(bindingIndex)
       .WithControlsExcluding("<Mouse>/leftButton")
       .WithControlsExcluding("<Mouse>/rightButton")
       .WithControlsExcluding("<Mouse>/press")
       .WithCancelingThrough("<Keyboard>/escape")
       .OnComplete(...) → Save() → GameInput.shared.SaveToPlayerPrefs()
   ↓
   GameInput.SaveToPlayerPrefs()                              ← GameInput.cs:716
   ↓
   PlayerPrefs.SetString("rebinds", inputActions.SaveBindingOverridesAsJson())
```

### Storage

- **PlayerPrefs key**: `"rebinds"` (a const in `GameInput.cs:189` and `RebindSaveLoad.cs:12`).
- **Format**: JSON blob produced by `InputActionAsset.SaveBindingOverridesAsJson()`. This is Unity's native rebind-override serialization — only the *deltas* from the asset's defaults are stored, not the full bindings.
- **Two save sites**: `GameInput.SaveToPlayerPrefs` (called by `RebindActionUI.Save`) and `RebindSaveLoad.OnDisable` (a separate component that auto-saves on disable). Both write to the same key.
- **Two load sites**: `GameInput.LoadFromPreferences` (called from `OnEnable`) and `RebindSaveLoad.OnEnable`. Same key. Both call `LoadBindingOverridesFromJson`.

### Conflict detection

`BindingsWindow.FindConflicts` (`BindingsWindow.cs:105`):

```csharp
static string UniquingKey(InputAction a) =>
    string.Join("|", a.bindings.Select(b => b.effectivePath));
```

If two actions in the union of all rebindable actions have the same effective path string (joined by `|` for composites), they're flagged as conflicts. The conflicting actions are passed to `AddInputBindingControl(action, conflict:true, …)` which sets `RebindActionUI.Conflict = true` (which colors the rebind button red, line 234-238).

**Conflict detection is across all 4 rebind groups** (Movement/Camera/UI/Equipment) — but **does NOT include `Game/ActivatePrimary`/`ActivateSecondary` or `Global/ShowPauseMenu`**, since those aren't in `RebindableActions`.

`RebindActionUI.CheckDuplicateBindings` is **stubbed to return false** (line 455-458) — the per-element check is dead code. Conflict resolution is window-scoped only.

### Per-binding rebind UI (`RebindActionUI`)

Per-binding, not per-action. Uses `bindingId` (a `Guid` string) to identify which of the action's bindings is bound to this UI element. `AddInputBindingControl` always sets `bindingId = action.bindings[0].id.ToString()` — so vanilla only ever rebinds the FIRST binding of each action. Composite bindings (Vector2 = 4 buttons) use `isComposite`/`isPartOfComposite` recursion (`StartInteractiveRebind` line 327, `PerformInteractiveRebind` recursion line 374-385) to walk part-bindings.

`RebindActionUI.ActionStrings` (line 68-75) is the per-action display-name override dictionary:

```csharp
{ "TogglePlacer", "Equipment Purchase/Placer" },
{ "Horn",        "Whistle/Horn" },
{ "PushCar",     "Rerail/Push" },
{ "MoveTrain",   "Manually Move Selected" },
{ "Teleport",    "Jump to Mouse" },
```

All other action names go through `Regex.Replace(name, "(\\B[A-Z])", " $1")` — splits camelCase to spaced words.

### Rebinding control exclusions

```csharp
.WithControlsExcluding("<Mouse>/leftButton")
.WithControlsExcluding("<Mouse>/rightButton")
.WithControlsExcluding("<Mouse>/press")
.WithCancelingThrough("<Keyboard>/escape")
```

You **cannot** bind LMB/RMB/generic mouse-press to actions via the rebind UI (vanilla wants those reserved for click/drag). ESC always cancels the rebind. To allow mouse-button rebinds in a mod, patch `RebindActionUI.PerformInteractiveRebind` to drop the `WithControlsExcluding` calls.

### Patch candidates

| Method | Why patch |
|---|---|
| `BindingsWindow.Build` | Add a 5th tab, override grouping, custom rebind widgets. |
| `BindingsWindow.FindConflicts` | Custom conflict policy (e.g. allow same key in different contexts). |
| `RebindActionUI.PerformInteractiveRebind` | Permit mouse buttons; change the cancel binding; add device filters. |
| `RebindActionUI.ActionStrings` (static dict) | Add display-name overrides for mod-added actions (mutate the dict). |
| `GameInput.SaveToPlayerPrefs` / `LoadFromPreferences` | Persist additional state alongside the rebinds blob. |
| `UIPanelBuilder.AddInputBindingControl` | Custom rebind widget prefab. |

### Gotchas

- **Vanilla rebinds only `bindings[0]`** of each action. Mod-added actions with multiple primary bindings need a custom UI loop.
- **`PlayerPrefs` is per-Unity-app** — rebinds are not portable between branches/installs of Railroader. (Same for sound/vsync/etc.)
- **Composite parts re-prompt sequentially**: rebinding `Move` rebinds Up→Down→Left→Right one after the other (via `allCompositeParts: true` recursion in `PerformInteractiveRebind`). The user sees four prompts. The "modifier" composite-part name maps to "Waiting for modifier (Shift, Control, Alt) ...".
- **`LoadBindingOverridesFromJson` does not validate** — corrupt or out-of-date JSON silently drops or misapplies overrides. If you change the asset's binding GUIDs, old rebinds become orphans.
- **`RebindSaveLoad.cs` is a separate component** — appears to be wired on a different scene object (vanilla seems to use both paths). Both load on enable, save on disable. If you bypass one (e.g. mid-game crash before `OnDisable`), saves are lost.
- **`Conflict` color is `Color.red` * 0.85f** for highlighted state — hard-coded in `RebindActionUI.cs:236`. Not theme-aware.

---

## `MovementInput` (legacy speed helper)

```csharp
public static class MovementInput {                           // MovementInput.cs
    public static float CalculateSpeedFromInput(float normalSpeed, float fastSpeed, float superSpeed) {
        var s = GameInput.shared;
        if (s.ModifierVeryFast) return superSpeed;
        if (s.ModifierRun)      return fastSpeed;
        return normalSpeed;
    }
}
```

Free-standing static. Used by `DroneController.FixedUpdate` (`DroneController.cs:23`). `StrategyCameraController` and `GameInput.GetMovement` reimplement the same logic inline.

---

## `TrainInput` (locomotive control input)

`[RequireComponent(typeof(TrainController))]` — the per-frame mediator between keyboard `GameInput` and the selected locomotive's `LocomotiveControlAdapter`. Reads ~10 actions and calls `BaseLocomotive.SendPropertyChange(PropertyChange.Control.X, value)` per change.

```csharp
public class TrainInput : MonoBehaviour {                     // TrainInput.cs
    private void Update();        // throttle/reverser/brake/horn/headlight/bell/cylinder
    private void FixedUpdate();   // continuous brake/throttle delta application
    private bool TryGetLocomotiveControlAdapter(out BaseLocomotive, out LocomotiveControlAdapter);
    private void ChangeValue(PropertyChange.Control control, float value);
}
```

### Per-locomotive type branches

```csharp
if (loco.Archetype == CarArchetype.LocomotiveDiesel) {
    // diesel: ReverserForward / Back use slow-repeat, set ±1 directly
    if (shared.ReverserBack)    num2 = -1f;
    else if (shared.ReverserForward) num2 = 1f;
} else {
    // steam: ReverserForwardRepeating / BackRepeating use fast-repeat
    float num4 = (isControlDown ? 1f : 0.1f);
    if (shared.ReverserBackRepeating)    num2 = -num4;
    else if (shared.ReverserForwardRepeating) num2 = num4;
}
```

Diesel = notch reverser (boolean step). Steam = continuous (smooth, with `Ctrl` for full-throttle steps).

### Throttle: notched vs. continuous

```csharp
int throttleInputNotches = adapter.ThrottleInputNotches;
if (throttleInputNotches > 0) {
    // notched diesel/locomotive — use slow-repeat, step by 1/N
    if (shared.ThrottleDown) ChangeValue(Throttle, oldThrottle - 1f/N);
    if (shared.ThrottleUp)   ChangeValue(Throttle, oldThrottle + 1f/N);
} else {
    // continuous (steam) — use fast-repeat, accumulate delta in FixedUpdate
    float delta = (isShiftDown ? 0.15f : 0.03f);
    if (shared.ThrottleUpRepeating)   throttleDelta = delta;
    if (shared.ThrottleDownRepeating) throttleDelta = -delta;
}
```

Brakes use the fast-repeat path with `(shift ? 0.05 : 0.01)` per tick (line 42), with brake-release at *2x* the rate (line 105). Locomotive brake clamps `[-0.1, 1]` (the negative range is the "release" position).

### Horn

`InputHorn` getter (`GameInput.cs:257`): pressed = 0.3, pressed+shift = 1.0. `HornExpressionEnable`/`HornExpressionValue` add a "drag-mouse-for-expression" mode where horn level tracks mouse Y delta accumulated.

### Patch candidates

| Method | Why patch |
|---|---|
| `TrainInput.Update` | Customize per-frame loco control (e.g. controller mappings, AI override). |
| `TrainInput.FixedUpdate` | Customize the continuous brake/throttle ramp rates. |
| `TrainInput.TryGetLocomotiveControlAdapter` | Filter — disable input for specific loco types. |
| `TrainInput.ChangeValue` | Catch every input-driven control change. Postfix to log/observe. |

---

## Cross-cutting types

### `KeyValueObject` / `PropertyChange` (KVO)

Inputs flow into KVO updates via `BaseLocomotive.SendPropertyChange(Control, value)`. The `PropertyChange.Control` enum (`Game.Messages/PropertyChange.cs`) is a per-control identifier (`Throttle`, `Reverser`, `TrainBrake`, `Horn`, `Bell`, `CylinderCock`, `Headlight*`, etc.). For wear-related KVO see [Wear & Durability › KVO](wear-durability.md#kvo-backed-properties-hostonly-prefix-_-or-oiledhotbox).

### `MainCameraHelper.TryGetIfNeeded(ref Camera)`

Lazy `Camera.main` cache used by both `CameraSelector` and `StrategyCameraController`. Returns true when the camera reference is now non-null (caller can read it). Useful pattern for any mod that needs `Camera.main` early in scene lifecycle.

---

## Putting it all together: how a single keystroke becomes an action

Example: pressing the "Teleport" key (default unknown — rebindable, action `Game/Teleport`).

1. **Hardware → Unity**: keyboard input arrives at the new InputSystem.
2. **InputSystem dispatches** to all enabled action maps. `Game/Teleport` is in the enabled `Game` map (assuming `MovementInputEnabled`).
3. **`_teleportAction.WasPerformedThisFrame()` becomes true** for one frame.
4. **`CameraSelector.Update`** (`CameraSelector.cs:145`) → `InputJumpCamera()` (line 256) reads `gameInput.Teleport` (line 231) → calls `TeleportToMouse()` (line 442).
5. **`TeleportToMouse`** raycasts via `Layers.Clickable` then `Layers.Terrain`, picks a target, and calls `character.JumpTo(point, look)` then schedules `SelectCamera(FirstPerson)`.
6. **`PlayerController.JumpTo`** (`PlayerController.cs:203`) → `character.UnsitUnladder()`, `character.motor.SetPositionAndRotation(pos, rot)`, `cameraController.SetRotation(rot)`.
7. **Next frame**: `CharacterPositionTransmitter.SendIfConnected` (`CharacterPositionTransmitter.cs:23`) sees the position delta > 0.05m and broadcasts `UpdateCharacterPosition` over MP. Other clients see the player teleport (with 300ms delay; see [Player & Camera › RemoteAvatar](player-camera.md#avatarremoteavatar-other-players-bodies-mp)).

**To add a new action key in a mod**, the standard path is:
1. Add a new `InputAction` to the `InputActionAsset` (programmatically: `inputActions.AddActionMap("ModMap")` then `actionMap.AddAction(...)`). Or add to the existing `Game` map.
2. Subscribe to it in your `OnEnable` via `action.performed += Handler`.
3. Or poll it in `Update` with `action.WasPerformedThisFrame()`.
4. To make it rebindable, postfix `GameInput.Awake` to append to `RebindableActions`. Then the existing `BindingsWindow` will pick it up.

**Or** for a one-off without joining the InputAction asset: just use `Keyboard.current.f8Key.wasPressedThisFrame` in your own `Update`. This is the path your experiment took. It bypasses the rebind system entirely (no UI, no PlayerPrefs) but works without touching the asset.

---

## Cross-references

- Camera modes that consume these inputs (`InputChangeCamera`, `InputJumpCamera`, mouse-look): see [Player & Camera › CameraSelector](player-camera.md#cameraselector-the-mode-switcher) and [MouseLookInput](player-camera.md#camerasmouselookinput-mouse--pitchyaw).
- The `GameInput.SmartAirHelperModifier` use site: see [Couplers › cut lever](couplers.md#cut-lever-pipeline-player-driven-uncouple).
- `_focusPause` and TMP_InputField focus interaction: relevant to any mod opening modal UI (no dedicated UI crib sheet yet — see settings panel pattern in [PreferencesBuilder.BuildTabInput](#rebinding-pipeline) above).
- Multiplayer access levels (`MinimumAccessLevel(AccessLevel.Passenger)` on `UpdateCharacterPosition`): see [Multiplayer survey](../multiplayer-vanilla-survey.md).
