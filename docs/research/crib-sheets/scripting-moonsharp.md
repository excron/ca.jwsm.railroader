# Scripting (MoonSharp Lua) — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.Scripting*` + `Railroader-ILSPY/MoonSharp.Interpreter/`)
**Companions:** [Save & Load](save-load.md), [Passengers & Timetable](passengers-timetable.md), [Console Commands](console-commands.md), [Access Control](access-control.md), [Settings & Preferences](settings-preferences.md)

Railroader bundles MoonSharp 2.0.0.0 and uses it for **two and only two** vanilla features: the **interactive tutorial** (`Tutorial/tutorial.lua` + helper modules) and a **dev-only test harness** (`TestScripts/test_*.lua`, surfaced through `ScriptTestsController` / `ScriptTestsWindow`). There is **no scenario scripting**, no per-tick script hook, and no Lua running inside the game-state save/restore loop. Scripts are executed via `MonoBehaviour` coroutines on a host component and wait between resumes by yielding numbers (interpreted as `WaitForSeconds`) — there is no in-engine event system, no `OnTick`, no Messenger bridge. The entire script-callable API is a **fixed set of nine `Script*` C# wrapper classes** registered once at static init in `ScriptManager.StaticInit` plus the static methods of `ScriptWorld`. Several of those wrappers mutate authoritative state directly (`ScriptWorld.set_property`, `place_train`, `set_signal_system`, `reset`); these are gated only by ad-hoc `StateManager.AssertIsHost()` calls, **not** by the standard `IPropertyAccessControlDelegate` chain.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Game.Scripting.ScriptManager` | `Game.Scripting/ScriptManager.cs:12` | Owns one `MoonSharp.Interpreter.Script`, runs Lua on a host `MonoBehaviour` coroutine. The only entry into the interpreter. |
| `ScriptManager.StaticInit` | `ScriptManager.cs:192` | One-shot global type registry. **All Script\* wrapper types must be added here to be Lua-callable.** |
| `ScriptManager.Reset` | `ScriptManager.cs:173` | Constructs a new `Script` per Load with `CoreModules.Preset_SoftSandbox` (+ `LoadMethods` if module paths supplied). |
| `ScriptManager.CurrentScript` (static) | `ScriptManager.cs:37` | Per-call ambient script handle; throws if read outside a script run. Used by `ScriptProperties` to find the active interpreter. |
| `ScriptWorld` (static-method bag) | `Game.Scripting/ScriptWorld.cs:32` | The Lua global `World`. ~40 methods covering cars/trains/locations/signals/ops/camera/input. |
| `Game.Scripting.Interactive.InteractiveBookRunner` | `Game.Scripting.Interactive/InteractiveBookRunner.cs:12` | The actual hosting MonoBehaviour for the tutorial flavour ("books"). Loads `<bookName>.lua`, expects `{ title, close_message, run = function(ctx) … end }`. |
| `UI.Tutorial.TutorialManager` | `UI.Tutorial/TutorialManager.cs:11` | The only vanilla consumer that opens an `InteractiveBookWindow` in shipping builds. Triggered by `StateManager.OnPropertiesDidRestore` if `setupDescriptor.showTutorial` is true. |
| `Game.Scripting.Testing.ScriptTestRunner` | `Game.Scripting.Testing/ScriptTestRunner.cs:13` | Loads `test_*.lua` from `<exe>/TestScripts/`, picks up `setup`/`teardown`/`test_*` closures, runs each in its own coroutine. |
| `Game.Scripting.Testing.ScriptTestsController` | `Game.Scripting.Testing/ScriptTestsController.cs:5` | `GameBehaviour` that boots the test window. **Not attached to any vanilla scene** — only fires if a developer scene puts it down. |
| `MoonSharp.Interpreter.CoreModules.Preset_SoftSandbox` | `MoonSharp.Interpreter/CoreModules.cs:27` | The active sandbox bitmask (`0x18FEF`). Excludes `LoadMethods`, `OS_System`, `IO`, `Debug`. |

---

## Lifecycle spine

There is **no shared "script engine" singleton**. Each consumer creates its own `ScriptManager`, which owns one `MoonSharp.Interpreter.Script`:

```
Consumer (MonoBehaviour, e.g. InteractiveBookRunner)
   │  new ScriptManager(player, hostComponent, modulePaths?)
   │  ── ScriptManager ctor calls StaticInit() (idempotent)
   ▼
ScriptManager.Reset()
   │  new Script(CoreModules.Preset_SoftSandbox [| LoadMethods])
   │  script.Options.DebugPrint  → Serilog.Log.Information("Lua: {s}", …)
   │  script.Options.ScriptLoader= FileSystemScriptLoader { ModulePaths = … } (if any)
   │  Globals["Location"] = typeof(ScriptLocation)        ← static type, not instance
   │  ScriptVector3.AddVec3Type(script)  → Globals["vec3"] table
   ▼
ScriptManager.Load(source, filename)
   │  _currentScript = _script        ← used by ScriptProperties.CurrentScript
   │  _script.DoString(source, …, filename)
   │  Returns a DynValue (typically a table when loading a "book module")
   ▼
ScriptManager.Run(closureName, args…)
   │  hostComponent.StartCoroutine(RunFromCoroutine(…))
   │  Pulls Closure from _script.Globals[closureName]
   ▼
ScriptManager.Run(Closure, args…)        — coroutine-resumed loop
   │  CreateCoroutine(closure).Coroutine
   │  while state != Dead:
   │    cor.Resume(args)               (catches ScriptRuntimeException → LastRunError)
   │    if returned a Number → yield WaitForSeconds(seconds)
   │    else                  → yield null  (one frame)
   ▼
end
```

Two corollaries that surprise people coming from "Lua = scenario scripts":

- **A script is dead once its top-level closure returns.** `ScriptWorld` exposes no scheduler — repeating logic must be a Lua loop that `coroutine.yield(seconds)`s. The `Run` loop's `WaitForSeconds(returnedNumber)` is the *only* sleep mechanism.
- **No `Update()` callback**. Mods that want "per-frame" Lua logic must wire their own `MonoBehaviour.Update` into a Lua closure call.

---

## Sandbox — what's enabled, what isn't

Set in `ScriptManager.Reset` at `ScriptManager.cs:175-180`:

```csharp
CoreModules coreModules = CoreModules.Preset_SoftSandbox;          // 0x18FEF
if (_scriptLoader != null) coreModules |= CoreModules.LoadMethods;  // 0x10
Script script = new Script(coreModules);
```

`Preset_SoftSandbox = 0x18FEF` decodes to (per `MoonSharp.Interpreter/CoreModules.cs`):

| Bit | Module | Enabled? |
|---|---|---|
| 0x40 | Basic (`assert`, `print`, `pcall`, `tostring`, `type`, …) | yes |
| 0x1 | GlobalConsts (`_VERSION`, `_G`, `_MOONSHARP`) | yes |
| 0x2 | TableIterators (`pairs`, `ipairs`, `next`) | yes |
| 0x4 | Metatables (`setmetatable`, `getmetatable`, `rawset`, `rawget`) | yes |
| 0x8 | String | yes |
| 0x20 | Table | yes |
| 0x80 | ErrorHandling (`error`, `xpcall`) | yes |
| 0x100 | Math | yes |
| 0x200 | Coroutine | yes |
| 0x400 | Bit32 | yes |
| 0x800 | OS_Time (`os.date`, `os.time`, `os.difftime`, `os.clock`) | yes |
| 0x8000 | Dynamic (`dynamic`) | yes |
| 0x10000 | Json (`json.parse` / `json.serialize`) | yes |
| 0x10 | LoadMethods (`load`, `loadfile`, `dofile`, `loadstring`, `require`) | **only if modulePaths provided** |
| 0x1000 | OS_System (`os.execute`, `os.exit`, `os.getenv`, `os.remove`, `os.rename`, `os.setlocale`, `os.tmpname`) | **off** |
| 0x2000 | IO (`io.*`) | **off** |
| 0x4000 | Debug (`debug.*`) | **off** |

Both vanilla consumers (`InteractiveBookRunner`, `ScriptTestRunner`) construct with `modulePaths != null`, so in practice **`require` and friends are available** for tutorial/test scripts. A consumer that omits `modulePaths` would lose `require` and become much more locked down.

Static print/debug:

```csharp
script.Options.DebugPrint = s => Log.Information("Lua: {s}", s);
Script.GlobalOptions.RethrowExceptionNested = true;   // ScriptManager.cs:196
```

`Script.GlobalOptions` is a process-global; `RethrowExceptionNested` is set once via `StaticInit` and never reset.

### **High-value finding — sandbox is not a security boundary**

- `LoadMethods` enables `require` *and* `load`/`loadstring`/`loadfile`/`dofile`. Combined with the `FileSystemScriptLoader` whose `ModulePaths` come from the consumer (`InteractiveBookRunner` passes `<basePath>/?.lua` and `<StreamingAssets>/LuaModules/?.lua`), a tutorial/book can pull arbitrary Lua from anywhere on disk that matches the path template — and `loadstring` lets it construct code from runtime strings. There is no Lua-side mitigation.
- The `LoadMethods` enable in `Reset` is *unconditional* on the presence of `_scriptLoader`. If a future consumer omits `modulePaths`, the bare `Script` still exposes `Preset_SoftSandbox` which **does include `OS_Time`** but not `LoadMethods`. So `os.time()` is callable from any script regardless of consumer choice.
- `IO`, `OS_System`, `Debug` are off — but UserData reflection on registered types is wide open. A script can call any *public* property/method on the registered Script* wrappers. There is no `MoonSharpHidden` discipline beyond `[MoonSharpVisible(false)]` on `ScriptLocation.Location` (the raw `Track.Location` reference). Anything else `public` on a Script* wrapper is callable.

---

## `Game.Scripting.ScriptManager`

```csharp
public class ScriptManager : IDisposable                              // 12
{
    public readonly struct ErrorInfo(string message, string decoratedMessage);  // 14
    public ErrorInfo? LastRunError;                                   // 31
    public static string BuiltInModulesPath { get; }                  // 49 — <streamingAssets>/LuaModules/?.lua
    internal static Script CurrentScript { get; }                     // 37 — throws outside a Run

    public ScriptManager(IPlayer player, MonoBehaviour hostComponent, string[] modulePaths = null);  // 51
    public DynValue Load(string source, string filename);             // 72
    public void Run(string closureName, params object[] args);        // 92  (StartCoroutine)
    public IEnumerator RunFromCoroutine(string closureName, params object[] args); // 107
    public IEnumerator Run(Closure closure, params object[] args);    // 119
    public void Stop();                                               // 98
    public void Dispose();                                            // 67
    public IEnumerable<string> GetGlobalClosureNames();               // 210
    public void ClearLastRunError();                                  // 221
    public static implicit operator Script(ScriptManager sm);         // 62
}
```

### State

- `_script`: the `MoonSharp.Interpreter.Script`. Recreated every `Reset()` (which `Load` calls first). **Globals do not persist across `Load` calls on the same `ScriptManager`** — `Reset` builds a brand-new `Script`.
- `_player`: passed to ctor but **never used**. Vestigial.
- `_hostComponent`: the `MonoBehaviour` that hosts the coroutine. Must outlive the script run.
- `_coroutine`: the most recent `StartCoroutine` handle. `Run` calls `Stop` first, so only one script run is active at a time per `ScriptManager`.
- `_scriptLoader`: a `FileSystemScriptLoader` if `modulePaths != null`. `null` ScriptLoader means `require` (if enabled) will fail.
- `LastRunError`: nullable; populated when a `ScriptRuntimeException` or generic `Exception` escapes during `Run`. Cleared via `ClearLastRunError()`.
- `_currentScript` (static): set to `_script` for the duration of `Load` and each `Resume`, cleared in `finally`. **Reentrancy hazard**: nested `ScriptManager`s on the same call stack would clobber each other. There is no thread-safety; the value is single-threaded.

### Run loop semantics (`ScriptManager.cs:119-170`)

```csharp
MoonSharp.Interpreter.Coroutine cor = _script.CreateCoroutine(closure).Coroutine;
while (cor.State != CoroutineState.Dead) {
    DynValue dynValue;
    try {
        _currentScript = _script;
        dynValue = (args != null ? cor.Resume(args) : cor.Resume());
        args = null;                                  // args only sent on first Resume
    }
    catch (ScriptRuntimeException ex)  { LastRunError = …; Log.Error(…); break; }
    catch (Exception            ex2)   { LastRunError = …; Log.Error(…); break; }
    finally { _currentScript = null; }

    if (dynValue != null && dynValue.Type == DataType.Number)
        yield return new WaitForSeconds((float)dynValue.Number);    // sleep N game-seconds
    else
        yield return null;                                          // one frame
}
```

**Non-obvious points:**
- A Lua `coroutine.yield(2.5)` waits 2.5 seconds (game time, scaled by `Time.timeScale`).
- Yielding *anything other than a number* (string, table, nil, `true`) waits **one frame**.
- `args` is only delivered on the first resume. Subsequent `coroutine.yield()` returns nothing into Lua — there is no in-band channel for follow-up arguments.
- An exception **terminates the script run silently** beyond the log entry. `LastRunError` is consulted by both `InteractiveBookRunner.RunBook` (shows error to player + offers Reload) and `ScriptTestRunner.RunTest` (marks Failed). **Mods that wire their own ScriptManager need to read `LastRunError` themselves** — the default behaviour is "log and forget."
- `Stop()` calls `MonoBehaviour.StopCoroutine(_coroutine)`. **It does not Dispose the script or clear `_currentScript`**; if `Stop()` is called from inside a Lua-invoked C# call (rare), `_currentScript` remains set on the static for the duration of the call stack unwind.

### Patch candidates

| Method | Why patch |
|---|---|
| `ScriptManager.StaticInit` | The single chokepoint to register additional `UserData` types. Wrap with a postfix to add `UserData.RegisterType<MyScriptThing>(InteropAccessMode.Default, "MyThing")`. **Patch must complete before any consumer's first `new ScriptManager(...)`** — `StaticInit` is one-shot via `_initialized`. |
| `ScriptManager.Reset` | Patch postfix to inject globals (`script.Globals["modName"] = …`), swap the `ScriptLoader`, or upgrade the `CoreModules` set. The vanilla loader is `FileSystemScriptLoader`; you can replace it with an in-memory loader for sandboxed mod scripts. |
| `ScriptManager.Run(Closure, args[])` | Wrap to add per-tick observability or to enforce a Lua-side timeout. The vanilla loop has no timeout — a Lua infinite loop with no `coroutine.yield` will hang the host coroutine until the next `MonoBehaviour.StopAllCoroutines`. |
| `ScriptManager.Load` | Pre-process the source string (e.g., line-rewriting, transpilation) before `DoString`. |

### Gotchas

- **`StaticInit` is process-global and idempotent.** First `ScriptManager` instance freezes the registered UserData type set. There is no "register more types later"; mods must Harmony-patch `StaticInit` *postfix* and ensure the patch is applied before the tutorial loads (which can be quite early — `StateManager.OnPropertiesDidRestore` fires `TutorialManager.ShowIfAppropriateForLaunch`).
- **`_currentScript` static is reset to null in the `finally` of every Resume.** Code called *during* a Lua call (e.g., a C#-side helper invoked from Lua) sees the correct script. Code called *between* yields (e.g., a Unity coroutine that bridges to a Lua observer callback) would see null. `ScriptProperties.observe` saves the script reference at observer-creation time to avoid this.
- **`ScriptManager` is `IDisposable` but `Dispose()` only calls `Stop()`.** It does not unregister anything globally; the underlying `MoonSharp` `Script` becomes garbage when `_script` is dropped. There is no leak per-se, but lingering coroutines on `_hostComponent` continue if `_hostComponent` is still alive and `Stop()` wasn't called first.
- **`Script` instance can be obtained via the implicit conversion** `Script s = scriptManager;`. Useful for direct MoonSharp APIs (registering types per-script, fiddling with `Globals`) outside the wrapper API.
- **`Script.GlobalOptions.RethrowExceptionNested = true`** is set globally and never reverted. If your mod creates its own `Script` outside `ScriptManager`, it inherits this setting.

---

## `Game.Scripting.ScriptWorld` — the global `World` API surface

`ScriptWorld` is registered with friendly name `"World"` (`ScriptManager.cs:197`). The class itself is plain C# — its instance is exposed in two ways:

- As `ScriptWorld.Shared` (a singleton) — passed to test closures as their first argument.
- Indirectly, every `public static` method on `ScriptWorld` is callable as `World.method(args)` from Lua thanks to MoonSharp's UserData reflection.

The interactive book context exposes `ScriptWorld.Shared` as `ctx.world` (see `BookContext` at `InteractiveBookRunner.cs:14`). Test runners pass `ScriptWorld.Shared` as the closure's argument (`ScriptTestRunner.cs:196`).

### Full method index (Lua-callable via `World.<name>` or `ctx.world.<name>`)

Per `ScriptWorld.cs`. **Everything below is `public static` unless noted.**

| Lua call | C# signature | Returns | Auth / side-effects |
|---|---|---|---|
| `World.timeScale` (get/set) | `float timeScale { get; set; }` (`:42`) | `float` | Sets `Time.timeScale` directly — **no host check, no MP sync**. Per-machine. |
| `World.time` (get) | `float time => Time.time` (`:54`) | `float` | Unity `Time.time` (seconds since process start). |
| `World.say(message)` | `void say(string)` (`:56`) | nil | `Multiplayer.Broadcast` — host sends to all clients via `Host.SendToAll(Alert(...))`; on a non-MP machine, presents locally. |
| `World.set_feature_enabled(featureId, enabled)` | `void` (`:61`) | nil | `MapFeatureManager.Shared.SetFeatureEnabled` — fires `mapFeatures` KVO write. **Host-only behaviour required**, but no assertion in the wrapper. |
| `World.set_property(objectId, key, value)` | `void` (`:66`) | nil | Wraps a `PropertyChange` in `StateManager.ApplyLocal` — runs through normal auth. **Bypasses per-key `IPropertyAccessControlDelegate` checks only if called from host context** (else a non-host caller's `ApplyLocal` may be rejected when broadcast). Tables/functions throw `NotImplementedException`/`NotSupportedException` (see `ScriptProperties.ToValue`). |
| `World.get_property(objectId, key)` | `DynValue` (`:72`) | scalar | Reads `StateManager.Shared.KeyValueObjectForId(objectId)[key]`. Returns `nil` and logs a warning if object not found. |
| `World.set_signal_system("off"\|"ctc"\|"abs")` | `void` (`:83`) | nil | `MapFeatureManager.SetFeatureEnabled("signals", …)` + `PropertyChange("ctc","mode",…)`. Throws `ArgumentOutOfRangeException` for unknown command. |
| `World.code_ctc_route(interlockingId, nr, lr)` | `void` (`:104`) | nil | `CTCPanelController.Shared.CodeSwitchAndSignal(…)`. Throws if no CTC controller. `nr` ∈ {"N","R"}, `lr` ∈ {"L","R","other"}. |
| `World.is_block_occupied(blockId)` | `bool` (`:118`) | `bool` | Throws if CTC controller missing or block not found. |
| `World.reset()` | `void` (`:133`) | nil | **Destructive.** Time scale → 1, removes all cars, un-throws every switch, clears MapFeatures, clears notices, clears CTC routes/blocks/mode, clears every passenger stop's waiting list. **Test harness only.** No host check. |
| `World.get_distance(a, b)` | `float` (`:161`) | meters | `Graph.TryFindDistance` then linear fallback (logged warning). Throws on nil args. |
| `World.car_at_location(location, radius=1)` | `ScriptCar` (`:180`) | `Car`/nil | `TrainController.CheckForCarAtLocation`. |
| `World.find_car_from_location(location, distance, exceptCars=nil)` | `ScriptCar` (`:195`) | `Car`/nil | Walks the graph in 2 m steps, optionally skipping listed cars. |
| `World.jumpToIndustry(industryId)` (camelCase!) | `void` (`:223`) | nil | `CameraSelector.shared.JumpToPoint`. Throws if industry not found. **Per-machine** — no MP sync. |
| `World.jump_to_position(vec3Table)` | `void` (`:234`) | nil | Camera teleport from a `vec3` table. |
| `World.selected_camera()` | `string` (`:241`) | `"overhead"`/`"first-person"`/`nil` | |
| `World.get_player_position()` | `Dictionary<string,float>` (`:251`) | `{x,y,z}` | Game-space (re-origined). See [Floating Origin](floating-origin.md). |
| `World.get_camera_position()` | `Dictionary<string,float>` (`:256`) | `{x,y,z}` | World-space (current camera position). |
| `World.orient_toward(vec3Table)` | `void` (`:261`) | nil | Switches to first-person, slerps player rotation toward the target. **Coroutine on `CameraSelector.shared`, not on the script's host.** Continues after the script ends. |
| `World.get_cars(carTypeFilter)` | `List<ScriptCar>` (`:283`) | array | Optional `CarTypeFilter` string (e.g., `"freight"`). |
| `World.get_selected_car()` | `ScriptCar` (`:294`) | `Car`/nil | `TrainController.SelectedCar`. Per-machine. |
| `World.place_train(location, identifierArray)` | `List<ScriptCar>` (`:309`) | array | **`StateManager.AssertIsHost()`**. Wraps `TrainController.PlaceTrain` and auto-appends a tender for steam locomotives. Returns `LastPlacedTrain`. |
| `World.place_train_at_interchange(interchangeId, identifierList, carIdList)` | `List<ScriptCar>` (`:340`) | array | **AssertIsHost**. Per-id placement on interchange `trackSpans`. |
| `World.get_marker_location(markerId)` | `ScriptLocation`/nil (`:376`) | `Location` | First tries `Graph.MarkerForId`, falls back to a global `FindObjectsOfType<TrackMarker>` scan including inactive. |
| `World.set_switch_thrown(switchId, thrown)` | `void` (`:405`) | nil | `TrainController.TrySetSwitch`. Throws on failure. |
| `World.get_switch_thrown(switchId)` | `bool` (`:413`) | `bool` | Throws if switch missing. |
| `World.get_switch_position(switchId)` | `Dictionary<string,float>` (`:423`) | `{x,y,z}` | `node.transform.GamePosition()`. |
| `World.check_same_route(a, b, limit)` | `bool` (`:433`) | `bool` | `Graph.CheckSameRoute`. |
| `World.get_passenger_stop(stationId)` | `ScriptPassengerStop` (`:438`) | wrapper | Throws if not found. |
| `World.property_equals(objectId, key, expectedValue)` | `bool` (`:448`) | `bool` | Convenience: deep-equality compare to a stored value. |
| `World.get_inspector_car()` | `ScriptCar`/nil (`:453`) | `Car` | `CarInspector.ShownCar()`. UI state. |
| `World.get_company_window_path()` | `string` (`:458`) | path | `CompanyWindow.Shared?.ShownPath` (the in-window navigation path). |
| `World.get_industry_next_contract_tiers()` | `Dictionary<string,int>` (`:463`) | dict | All progression-enabled industries with a `NextContract`. |
| `World.get_mouse_look()` | `Dictionary<string,float>` (`:468`) | `{x:Yaw,y:Pitch,z:0}` | Reads `MouseLookInput` on the player character. |
| `World.get_field_of_view()` | `float` (`:474`) | degrees | `Camera.main.fieldOfView`, `0` if no main camera. |
| `World.reset_movement_counter()` | `void` (`:484`) | nil | Enables and zeroes `GameInput.shared.MovementCounter`. |
| `World.get_movement_counter("forward"\|"back"\|"left"\|"right")` | `float` (`:491`) | counter | Other strings → 0. |
| `World.get_movement_jumped()` | `bool` (`:504`) | `bool` | `GameInput.shared.MovementJumped`. |
| `World.get_seated_car()` | `ScriptCar`/nil (`:528`) | `Car` | The Car the player's seated `Seat` is parented to. |
| `World.get_seated_locomotive()` | `ScriptCar`/nil (`:537`) | `Car` | Same, but only if locomotive. |
| `World.get_attached_locomotive()` | `ScriptCar`/nil (`:550`) | `Car` | `PlayerController.GetRelativeCar()` if a locomotive. |
| `World.get_seated_engineer()` | `bool` (`:565`) | `bool` | True iff the player occupies the lowest-priority Seat on a locomotive (the engineer seat). |

### Globals registered separately

- `Globals["Location"] = typeof(ScriptLocation)` (`ScriptManager.cs:187`). Lua: `Location.new("seg1", 25.0, 0)` or `Location.new("locStr")` invokes the static factories on `ScriptLocation` (`ScriptLocation.cs:18, 25`). `Location` arithmetic is overloaded (`Location + 5` walks 5 m).
- `Globals["vec3"] = { new, sub, distance, magnitude }` table (`ScriptVector3.cs:10-19`). Lua: `vec3.new(1,2,3)`, `vec3.sub(a,b)`, `vec3.distance(a,b)`, `vec3.magnitude(v)`. Vec3 values are plain Lua tables `{x,y,z}` returned by `DictionaryRepresentation`.

### MP authority

| Lua call | Asserts host? | What happens on a client? |
|---|---|---|
| `place_train`, `place_train_at_interchange` | **Yes** (`AssertIsHost`) | Throws `ScriptRuntimeException` (the assertion throws, caught at `Run` boundary). |
| `set_property` | No | Goes through `StateManager.ApplyLocal(new PropertyChange(...))` → standard `IPropertyAccessControlDelegate` chain. May silently no-op if client lacks auth. |
| `set_feature_enabled`, `set_signal_system`, `code_ctc_route` | No | Calls `MapFeatureManager` / `CTCPanelController` directly. **These methods themselves contain no host gates** — clients can mutate local state but the change won't propagate. Only safe to invoke on the host. |
| `reset` | No | Destructive on the local machine; in MP, would desync. Test-harness only. |
| `set_switch_thrown` | No | `TrainController.TrySetSwitch` does its own auth (issues a `RequestSetSwitch` from non-host). |
| Camera/UI getters (`get_inspector_car`, `selected_camera`, etc.) | No | All per-machine, never synced. |

**No method on `ScriptWorld` is exposed via a Messenger event or `IGameMessage`** — every script call is a direct in-process invocation.

### Patch candidates

| Method | Why patch |
|---|---|
| Add new `public static` methods to `ScriptWorld` via Harmony | Ineffective — MoonSharp reflects on the type at registration time. To add API, prefix `ScriptManager.StaticInit` and `UserData.RegisterType<MyScriptApi>(InteropAccessMode.Default, "MyApi")` *before* it sets `_initialized = true`. Then in `Reset` postfix, `script.Globals["MyApi"] = MyScriptApi.Shared`. |
| `ScriptWorld.set_property` | The single chokepoint to gate Lua's KVO writes (e.g., reject keys outside an allowlist). |
| `ScriptWorld.reset` | Patch prefix to add to the destruction list (e.g., clear mod-side state) or to forbid in MP. |
| `ScriptWorld.place_train` / `place_train_at_interchange` | Add post-place setup (auto-set waybills, populate cars). |

### Gotchas

- **`World.jumpToIndustry` is camelCase**, breaking the consistent snake_case convention of every other `ScriptWorld` method. Almost certainly a typo. Modders should call `World.jumpToIndustry(...)`, not `World.jump_to_industry(...)`.
- **`ScriptWorld.Shared` is constructed lazily but never reset.** `World.reset()` is a static; it resets *game state*, not the wrapper. The singleton has no fields to reset.
- **`set_property` rejects table values** (`ScriptProperties.ToValue` throws `NotImplementedException` for `DataType.Table`). KVO does support `Dictionary` values; this is a writer-side gap. `get_property` *does* materialize dictionaries (`ScriptProperties.CreateTable`).
- **`get_property` warns and returns nil for unknown objects but throws nothing** — easy to silently miss a typo.
- **`orient_toward`'s coroutine runs on `CameraSelector.shared.character`**, not on the script's host. If the script ends mid-rotation, the camera keeps slerping. There is no cancel mechanism exposed.
- **`get_cars` accepts `null`/empty filter** — returns *all* cars including ones in Bardo (until `TrainController.Cars` filters them). Caller is on the hook for filtering live cars.
- **`get_marker_location` falls through to a global object scan** if the graph doesn't have the marker — slow on large maps and includes inactive markers.
- **`ScriptCar.set_load_percent("", 0)` clears all load slots** (see `SetLoadCommand.SetLoadPercent` at `UI.Console/SetLoadCommand.cs:136-143`). Convenience footgun.

---

## `ScriptCar` and `ScriptBaseLocomotive`

`ScriptCar` (`Game.Scripting/ScriptCar.cs`) wraps a `Model.Car`. The factory `ScriptCarExtensions.ScriptCar(this Car)` (`ScriptCarExtensions.cs:7`) returns a `ScriptBaseLocomotive` for `BaseLocomotive` instances, otherwise a plain `ScriptCar`. **Always go through this extension** — direct `new ScriptCar(loco)` would bury the locomotive controls.

### `ScriptCar` (registered as `"Car"`)

```csharp
public readonly string id;                 // 15 — set in ctor
public string  name      => Car.DisplayName;
public string  car_type  => Car.CarType;
public bool    is_locomotive;
public ScriptProperties properties;        // lazy; wraps Car.KeyValueObject
public ScriptCarAir     air;               // lazy
public float            speed_mph { get; set; }     // setter rewrites whole consist
public ScriptLocation   location_front, location_rear;
public ScriptWaybill    waybill;
public bool stopped(float duration);       // Car.IsStopped(duration)
public List<ScriptCar> get_coupled_cars(string end);   // "a"/"b"/"f"/"r"/"" → A
public List<ScriptCar> get_air_open_cars(string end);
public void set_load_percent(string loadId, float percent);   // 0 clears all
public bool has_load(string loadId);
public float get_load_percent(string loadId);
public void set_passenger_destination(string destinationId, bool enabled);
public bool has_passenger_destination(string destinationId);
public void add_passengers(string originId, string destinationId, int count);
public void remove_passengers(string destinationId, int count);
public int  get_passenger_count(string destinationId);
public void follow();                       // CameraSelector.shared.FollowCar
public void select();                       // TrainController.Shared.SelectedCar = Car
```

### `ScriptCar.speed_mph` setter is an authority-defying foot-gun

```csharp
set {
    float num = value * 0.44703928f;                                  // mph → m/s
    List<Car> cars = Car.EnumerateCoupled().ToList();
    Car.set.SetVelocity(num * Car.Orientation, cars);                 // whole consist
    Car.velocity = num;
    Car.ResetAtRest();
}
```

Calls `IntegrationSet.SetVelocity` for every coupled car. **No host check, no `IsHost` gate**. In MP this would desync immediately. Test-harness use only.

### Per-end resolution

```csharp
private Car.LogicalEnd LogicalEndFromString(string s) => s.ToLower() switch {
    "" => Car.LogicalEnd.A,         // null/empty → A
    "a" => Car.LogicalEnd.A,
    "b" => Car.LogicalEnd.B,
    "f" => Car.EndToLogical(Car.End.F),
    "r" => Car.EndToLogical(Car.End.R),
    _ => Car.LogicalEnd.A,          // unknown → A (silent)
};
```

Anything weird falls through to A. See [Couplers › LogicalEnd vs End](couplers.md#cardendgear--the-logical-layer).

### Passenger calls

`add_passengers` / `remove_passengers` / `get_passenger_count` operate on `Car.GetPassengerMarker()` and write back via `Car.SetPassengerMarker`. **HostOnly KVO** (`ops.passengerMarker`). See [Passengers & Timetable › PassengerMarker](passengers-timetable.md).

`set_passenger_destination` mutates the marker's `Destinations` set — but the underlying boarding logic clamps via `destinationOut.Length` (vanilla bug noted in passengers-timetable). Lua-set destinations participate equally.

### `ScriptBaseLocomotive` (registered as `"BaseLocomotive"`)

Subclasses `ScriptCar`. Adds engineer-seat controls via a per-call-allocated `LocomotiveControlHelper`:

```csharp
public ScriptCar fuel_car;                             // tender for steam, self otherwise
public float independent_brake { get; set; }
public float train_brake       { get; set; }
public float reverser          { get; set; }
public float throttle          { get; set; }
public bool  bell              { get; set; }
public float horn              { get; set; }
public void  set_control_manual();                     // AutoEngineer Off
public void  set_control_ae_road(int direction, int speed);
public void  set_control_ae_yard(int direction, int speed, float distance);
public void  set_control_ae_waypoint(ScriptLocation location, int speed, string coupleCarId = null);
public float get_ae_target_speed_mph();                // AutoEngineer.ContextualTargetVelocity
```

**Each call to `AutoEngineerOrdersHelper` allocates a new helper** (`ScriptBaseLocomotive.cs:104`). For high-frequency Lua code this is allocator-heavy but functionally correct.

### `ScriptCarAir`

```csharp
public float brake_cylinder => _car.air.BrakeCylinder.Pressure;
public float brake_line     => _car.air.BrakeLine.Pressure;
```

That's it — read-only PSI, two values. See [Air System](air-system.md).

### Patch candidates

| Method | Why patch |
|---|---|
| `ScriptCar.set_load_percent` | Lua-side gate or alternate routing for load setting. |
| `ScriptCarExtensions.ScriptCar` | Return a custom `ScriptCar` subclass for specific archetypes (e.g., wrap passenger cars in a `MyScriptPassengerCar` exposing more API). Combine with a `UserData.RegisterType<MyScriptPassengerCar>` in a `StaticInit` postfix. |
| `ScriptCar.speed_mph` setter | Either gate to host or replace with a request-message round-trip. |

---

## `ScriptPassengerStop`

`Game.Scripting/ScriptPassengerStop.cs` — registered as `"PassengerStop"`.

```csharp
public string identifier => _passengerStop.identifier;
public string name       => _passengerStop.DisplayName;

public void offset_passengers_waiting(string destinationId, string originId, int offset);   // 18
public int  get_passengers_waiting(string destinationId);                                   // 23
```

`offset_passengers_waiting` calls `PassengerStop.OffsetWaitingOpsCommand(destinationId, originId, TimeWeather.Now, offset)` (`Model.Ops/PassengerStop.cs:1160`). That method is `internal bool` and the Lua wrapper discards the bool result.

### Other public `PassengerStop` calls reachable through `ScriptWorld`

- `World.reset()` calls `PassengerStop.ClearAllWaiting()` on every stop.

### `OffsetWaitingOpsCommand` itself

```csharp
internal bool OffsetWaitingOpsCommand(string destination, string origin, GameDateTime sourceGroupBoarded, int delta) {
    bool num = OffsetWaiting(destination, origin, sourceGroupBoarded, delta);
    if (num) SaveState();
    return num;
}
```

(Source: `Model.Ops/PassengerStop.cs:1160-1168`.)

Two callers exist in vanilla:

1. `OpsCommand.PassOffset` — the `/ops PassOffset` console subcommand. **`StateManager.AssertIsHost()`** at the call site (`Model.Ops/OpsCommand.cs:36`).
2. `ScriptPassengerStop.offset_passengers_waiting` — **no host assertion**.

The wrapper is a back-door around the console-side host check. In a non-host context the underlying `OffsetWaiting` will mutate local state and `SaveState` will write the `pass.<id>.state` HostOnly KVO key — which the host will reject when broadcast, leaving the local machine desynced. **This is a genuine MP correctness hole if a client's tutorial ever runs `offset_passengers_waiting`.** Vanilla tutorials do not, but mod-authored books would.

### Patch candidates

| Method | Why patch |
|---|---|
| `ScriptPassengerStop.offset_passengers_waiting` | Add `StateManager.AssertIsHost()` prefix to close the MP hole. |
| `ScriptPassengerStop` itself | Subclass to add per-stop API (`get_origins`, `get_groups`, etc.) and re-register via `StaticInit` postfix. |

---

## `ScriptLocation`

`Game.Scripting/ScriptLocation.cs` — registered as `"Location"` and *additionally* exposed as a global type so Lua can call `Location.new(...)`.

```csharp
[MoonSharpVisible(false)] public readonly Location Location;          // hidden raw
public object position => ScriptVector3.DictionaryRepresentation(Graph.Shared.GetPosition(Location));

public static ScriptLocation new(string segmentId, float distance, int end);  // 18
public static ScriptLocation new(string locationString);                       // 25
public ScriptLocation flipped();                                               // 30
public static ScriptLocation operator +(ScriptLocation, float);                // 35 — walk forward
public static ScriptLocation operator -(ScriptLocation, float);                // 40 — walk backward
public override string ToString();
```

The arithmetic operators use `Graph.LocationByMoving(loc, ±distance, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true)` — they always honour switches the cheap way and clamp at deadends.

`Location.new("locStr")` calls `Graph.ResolveLocationString` (a one-line `<segId>:<dist>:<end>` parser).

`[MoonSharpVisible(false)]` on the `Location` field is the only such annotation in the entire `Game.Scripting` namespace — nothing else hides anything.

---

## `ScriptProperties`

`Game.Scripting/ScriptProperties.cs` — registered as `"Properties"`. Wraps any `IKeyValueObject`.

```csharp
public DynValue this[string key] { get; set; }                             // 14
public DynValue observe(string key, Closure luaCallback, bool callInitial = true);  // 32
public static DynValue FromValue(Value, Script = null);                    // 42
public static Value    ToValue(DynValue);                                  // 74
```

`observe` returns a `ScriptDisposable` wrapped in a `UserData`. **The Lua callback is invoked with the `Value` translated via `FromValue`** — but it's invoked from KVO observer callbacks (which can run from arbitrary threads or coroutines depending on the source). MoonSharp callbacks are **single-threaded by default**; observer callbacks fired from network ingest could bypass the script-active sanity check. If the script has been disposed, calling its `Closure` is undefined behaviour. **Always `dispose()` returned `Disposable` handles when done.**

### Type marshalling

| `KeyValue.Runtime.ValueType` | → `DynValue` | Notes |
|---|---|---|
| `Null` | `Nil` | |
| `Int` | `NewNumber(IntValue)` | |
| `Bool` | `True`/`False` | |
| `Float` | `NewNumber(FloatValue)` | |
| `String` | `NewString(StringValue)` | |
| `Array` | **`NotImplementedException`** | KVO arrays cannot be read from Lua. |
| `Dictionary` | `NewTable(...)` | Recursive `FromValue`. |

| `DataType` | → `Value` | Notes |
|---|---|---|
| `Nil`, `Void` | `Value.Null()` | |
| `Boolean` | `Value.Bool` | |
| `Number` | `Value.Int` if integral, else `Value.Float` | Detected by `n % 1.0 == 0`. |
| `String` | implicit conversion | |
| `Table` | **`NotImplementedException`** | Lua tables cannot be written back as dictionaries. |
| `Tuple`, `Function`, other | `NotImplementedException`/`NotSupportedException` | |

**This means Lua observers can read dictionary KVO values but can never write them.** Any mod that needs Lua-driven structured property writes must either pre-flatten into separate keys or extend `ScriptProperties.ToValue`.

### Patch candidates

| Method | Why patch |
|---|---|
| `ScriptProperties.ToValue` | Add table → dictionary conversion (the obvious missing case). |
| `ScriptProperties.FromValue` | Add array marshalling (the other missing case). |

---

## `ScriptDisposable`

```csharp
public class ScriptDisposable {
    public ScriptDisposable(IDisposable disposable);
    public void dispose();          // calls _disposable?.Dispose() then nulls it
}
```

Trivial holder. Returned by `ScriptProperties.observe`. Idempotent.

---

## `ScriptWaybill`

```csharp
public class ScriptWaybill {
    public bool completed => _waybill.Completed;
}
```

Read-only one-property wrapper. Backing `Waybill` is a struct (`Model.Ops.Waybill`); the wrapper holds a copy taken at construction time. **Changes to the underlying waybill are not reflected** — `ScriptCar.waybill` allocates a fresh wrapper each get.

---

## `ScriptVector3` (the `vec3` global)

`Game.Scripting/ScriptVector3.cs`. Registered as a Lua table at `script.Globals["vec3"]` (`ScriptVector3.cs:19`).

| Lua call | C# | Returns |
|---|---|---|
| `vec3.new(x,y,z)` | `Func<float,float,float,Table>` | `{x,y,z}` table |
| `vec3.sub(a,b)` | `Func<Table,Table,Table>` | new table |
| `vec3.distance(a,b)` | `Func<Table,Table,float>` | scalar |
| `vec3.magnitude(v)` | `Func<Table,float>` | scalar |

Vec3 values across the entire scripting API are plain Lua tables `{x=…, y=…, z=…}`; there is no userdata vec3 type. `ScriptVector3.FromTable` converts them back to `UnityEngine.Vector3` for C#-side use.

---

## `Game.Scripting.Interactive` — the "books" runner

The only vanilla Lua-script lifecycle. Used by `TutorialManager`.

### `InteractiveBookRunner` (MonoBehaviour)

```csharp
public bool Open(string basePath, string bookName, IPageUI pageUI, IKeyValueObject keyValueObject);  // 96
public void Close();                                                                                 // 119
public bool Reload();                                                                                // 137
public bool ReloadIfModified();                                                                      // 124
public string BookTitle, CloseMessage { get; private set; }
public event Action OnWillReload;
internal static bool TryRun(Closure closure, string debugHint);                                      // 248
```

- Loads `<basePath>/<bookName>.lua` and expects the script's top-level expression to be a **table** with:
  - `title` (string, required)
  - `close_message` (string)
  - `extension_type` (string, currently informational only)
  - `run` (Closure, **required**) — the entry coroutine
- The `run` closure is invoked with one arg: a `BookContext` userdata holding `{ ui, properties, world, request_rerun, mark_complete }` (`InteractiveBookRunner.cs:14-34`).
- File-modification polling: `InteractiveBookWindow.RefreshLoop` calls `ReloadIfModified()` once per real-time second (`InteractiveBookWindow.cs:223-231`). Hot-reload during play.
- Module paths: `<basePath>/?.lua` plus `<streamingAssets>/LuaModules/?.lua` (`InteractiveBookRunner.cs:88-94`). `require` works.
- Errors are caught at the `Run` boundary; the page UI shows a generic "An error occurred" message with the `DecoratedMessage` (path-stripped) and a Reload button.

### `BookContext` (passed as Lua `ctx`)

| Field | Type | Notes |
|---|---|---|
| `ctx.ui` | `IPageUI` | The full markup/button/goal/arrow API. |
| `ctx.properties` | `ScriptProperties` | Wraps the per-book KVO object (`tutorial` for the tutorial). |
| `ctx.world` | `ScriptWorld` | The shared world API (note: book gets a *reference*; tests get the singleton too). |
| `ctx.request_rerun` | `Action` | Restart the book from `RunBook` start. |
| `ctx.mark_complete` | `Action` | Sets `properties["complete"] = true` — the `InteractiveBookWindow` observes this and closes the window. |

### `IPageUI` (the `ctx.ui` API)

```csharp
void say(string text);                                          // markdown via Markroader → TMP
void clear();
int  start_goal(string title, string message, string style);   // style: "percent" or "boolean"
void update_goal(int goalId, object value, string customDisplay);  // accepts float/double/int/bool
void finish_goal(int goalId);                                   // = update_goal(id, 1f)
void reload_button();                                           // Show a reload button
void button(string text, Closure closure);                      // Inline button → TryRun(closure)
void nav_button(string text, Closure closure);                  // Bottom-bar button
void remove_last();                                             // Pop the most-recent element
int  add_arrow_overlay(object locator, string hexColor);        // ScriptLocation OR vec3 table
void remove_arrow_overlay(int arrowId);
```

`button`/`nav_button` callbacks are invoked outside the main `RunBook` coroutine via `InteractiveBookRunner.TryRun(closure, hint)` (a synchronous `closure.Call()` wrapped in an exception logger). **They are not coroutines** — they cannot `coroutine.yield` to wait. Use `request_rerun` if you need to fork into a new wait-capable execution.

### Tutorial wiring

`TutorialManager`:
- KVO object `"tutorial"`, **HostOnly** (`UI.Tutorial/TutorialManager.cs:85`).
- Keys: `"closed"` (bool), `"complete"` (bool), `"chapter_id"`, `"page_id"`, vestigial `"stack"` (legacy migration trap that throws a one-time "the tutorial has changed!" modal).
- Triggers `InteractiveBookWindow.Show("Tutorial", "tutorial", _keyValueObject)` from `StateManager.OnPropertiesDidRestore` if `setupDescriptor.showTutorial` is true (`StateManager.cs:321`).
- Console command `/tutorial [chapter_id] [page_id]` (`UI.Console/ConsoleCommandHandler.cs:211`) opens the window and writes the navigation keys.

`HasTutorial` is **host-only** (`StateManager.cs:181`) — clients never see the tutorial at all.

### Patch candidates

| Method | Why patch |
|---|---|
| `InteractiveBookRunner.PrepareScriptIfNeeded` | Inject extra UserData / globals for mod-authored books. |
| `InteractiveBookRunner.TryLoadFromFile` | Pre-process Lua sources, support extra fields in the top-level table. |
| `IPageUI` | Implement on a custom UI for mod-driven interactive content. |

### Gotchas

- **`request_rerun` calls `StopStart()` which calls `StopBookCoroutine` then re-`StartCoroutine(RunBook())`**. The current execution context for `request_rerun` is *inside* the running coroutine; calling it works (the next yield will be the now-stopped coroutine, which never resumes). But Lua state from the previous run survives (table fields, observers).
- **`button`/`nav_button` closures are *not* re-resolved on Reload.** They reference closures captured into the previous `Script` instance. After a `Reload()`, the in-Lua closures are stale; pressing them invokes a closure on a disposed script. The `TryRun` exception handler swallows this. Hot-reload-in-page is unsafe — always `clear()` and re-emit buttons.
- **`add_arrow_overlay` retains arrow IDs in `_arrowOverlayIds`**, removed only via `remove_arrow_overlay` or `RemoveAllArrows()` (called on Show / Reload). A book that adds arrows without explicit removal will accumulate them across re-runs.

---

## `Game.Scripting.Testing` — the test harness

### `ScriptTestRunner`

```csharp
public ScriptTestRunner(string testPath, MonoBehaviour hostComponent);                           // 91
public IReadOnlyList<TestSuite> TestSuites;                                                       // 85
public event Action<Test> OnTestStatusChange;
public event Action<int,int> OnRunComplete;
public void LoadTests();                                                                          // 102
public bool LoadTestsIfModified();                                                                // 258
public IEnumerator RunAllTests();                                                                 // 164
public IEnumerator RunTests(List<Test> tests);                                                    // 170
public static void ResetWorld() => ScriptWorld.reset();                                           // 253
```

- Scans `<testPath>` for `test_*.lua`. Each file is one suite.
- Loads each via a fresh `ScriptManager`. Closures named `test_*` become `Test`s; `setup`/`teardown` become per-test fixtures.
- `RunTest` executes `setup` → `test_<name>` → `teardown` and watches `script.LastRunError` to mark Pass/Fail.
- `LoadTestsIfModified` polls file mtimes (called from `ScriptTestsWindow.RefreshLoop` once per second).

### `ScriptTestsController`

```csharp
public class ScriptTestsController : GameBehaviour {
    protected override void OnEnableWithProperties() {
        MapManager.Instance.ForceDisableTrees = true;       // visual aid for testing
        _runner = new ScriptTestRunner("TestScripts", this);  // relative to working dir
        _runner.LoadTests();
        _window = ScriptTestsWindow.Shared;
        _window.Show(_runner);
    }
}
```

`testPath = "TestScripts"` is **relative to the process working directory**, not `Application.streamingAssetsPath` (compare with `BuiltInModulesPath`). For the launched game that's typically the install dir.

The controller is a `GameBehaviour` that's not attached to any vanilla scene — only fires if a developer attaches it manually. **Effectively dev-only** in shipping.

### `ScriptTestsWindow`

The dev UI: list suites, per-test Run/Pass/Fail status, "Run All" / "Reload" / "Stop" buttons. The Stop button calls `ScriptTestRunner.ResetWorld()` which calls `ScriptWorld.reset()` — destructive. **Do not show this window in MP.**

### Test contract

A test file's globals layout:

```lua
function setup(world) ... end       -- optional
function teardown(world) ... end    -- optional
function test_my_thing(world) ... end
function test_other(world) ... end
```

The test closure's only argument is `ScriptWorld.Shared`. To wait, `coroutine.yield(seconds)`. Any Lua error or `assert` failure populates `LastRunError`, marking the test Failed.

### Patch candidates

| Method | Why patch |
|---|---|
| `ScriptTestRunner.RunTest` | Wrap to add per-test instrumentation, profiling, screenshot capture. |

---

## `OffsetWaitingOpsCommand` and other ops-command bridges

`OffsetWaitingOpsCommand` is **not a class** — it's a method on `PassengerStop` named that way to mark its origin as the `OpsCommand` console family. It is reachable from Lua via `ScriptPassengerStop.offset_passengers_waiting`.

### Other `OpsCommand` console subcommands and their script accessibility

| `OpsCommand` subcommand | Lua reachability |
|---|---|
| `Sweep(query)` | **No** wrapper. Modders must add. |
| `PassOffset(stop, origin, dest, offset)` | Yes via `ScriptPassengerStop.offset_passengers_waiting` (but skips the host check, see above). |
| `PassWaiting(stop)` | Partial — `ScriptPassengerStop.get_passengers_waiting(destinationId)` returns one destination's count. |
| `PassStops()` | No direct wrapper. Modders can iterate via `World.get_passenger_stop` once they know stop IDs. |
| `ListCommand(query)` | No wrapper. |
| `SetTier(industry, tier)` (sandbox-only) | No wrapper. |
| `FindWaybills(query)` | No wrapper. |

There is no `Script*` wrapper class for `Industry`, `Waybill` (other than `Completed`), `Contract`, or any other `Model.Ops` type. **The script API for ops is intentionally narrow.**

### Other authoritative-state mutators reachable from Lua (audit)

| Lua call | Underlying authoritative call | Risk |
|---|---|---|
| `World.set_property` | `StateManager.ApplyLocal(PropertyChange)` | Standard auth chain. Safe. |
| `World.set_feature_enabled` | `MapFeatureManager.SetFeatureEnabled` | **No host check** in wrapper; relies on caller-side discipline. |
| `World.set_signal_system` | `MapFeatureManager.SetFeatureEnabled` + `ApplyLocal(PropertyChange)` | As above. |
| `World.code_ctc_route` | `CTCPanelController.CodeSwitchAndSignal` | Likely host-authoritative inside; verify per-mod. |
| `World.reset` | `TrainController.RemoveAllCars` + others | Destructive, no MP gate. **Test-harness only.** |
| `World.place_train`, `place_train_at_interchange` | `TrainController.PlaceTrain` | **`AssertIsHost`** at wrapper boundary. Safe. |
| `World.set_switch_thrown` | `TrainController.TrySetSwitch` | Inner method does proper auth (uses `RequestSetSwitch` from clients). Safe. |
| `World.jumpToIndustry`, `jump_to_position`, `orient_toward` | `CameraSelector` | Per-machine. Safe. |
| `ScriptCar.speed_mph` setter | `IntegrationSet.SetVelocity` | **No host check.** MP desync risk. |
| `ScriptCar.set_load_percent` | `Car.SetLoadInfo` | Standard KVO write — auth on write. |
| `ScriptCar.add_passengers`/`remove_passengers`/`set_passenger_destination` | `Car.SetPassengerMarker` | HostOnly KVO — clients silently fail. |
| `ScriptPassengerStop.offset_passengers_waiting` | `PassengerStop.OffsetWaitingOpsCommand` | **No host check.** MP desync risk. |
| `ScriptBaseLocomotive.*` controls | `LocomotiveControlHelper` | Goes through normal control auth (Crew + train-crew). |
| `ScriptBaseLocomotive.set_control_*` | `AutoEngineerOrdersHelper` | Goes through `KeyValueObject` writes — standard auth. |

---

## Save/load interaction

Scripts have **no per-script save/load hook**. `ScriptManager` does not register with `StateManager`; it has no `Snapshot` representation. The only persistent state associated with scripts is what the script writes via `ScriptProperties` (i.e., into a real KVO object that *is* saved).

Tutorial-specific persistence (`UI.Tutorial/TutorialManager.cs:84-85`):

```csharp
_keyValueObject = base.gameObject.AddComponent<KeyValueObject>();
StateManager.Shared.RegisterPropertyObject("tutorial", _keyValueObject, AuthorizationRequirement.HostOnly);
```

So the `tutorial` KVO blob (with `closed`, `complete`, `chapter_id`, `page_id`) is part of every save. The Lua engine state itself (closures, table contents, coroutine stack) is **not saved**. On load, the tutorial book is re-opened and the script restarts from its `run` closure with the persisted KVO state — the book's state machine must be expressible entirely in those KVO keys.

For mod-authored books, the same pattern works: register your own KVO object with `StateManager.RegisterPropertyObject` and access it via `ScriptProperties` from the book's `ctx.properties`. This is how `BookContext`'s `properties` is wired (`InteractiveBookRunner.cs:172`).

See also: [Save & Load](save-load.md), [Settings & Preferences](settings-preferences.md).

---

## Multiplayer

There is no MP-aware scripting layer. Each machine runs scripts entirely on its own:

- `TutorialManager.HasTutorial` is **host-only** (`StateManager.cs:181`). Clients never see the tutorial pop-up. The `tutorial` KVO blob is host-only and replicates to clients but they never act on it.
- `ScriptTestsController` is dev-only and not part of any MP-aware code path.
- `ScriptManager` itself has no awareness of MP. Each machine that loads a script gets a fresh `ScriptManager` and runs independently.
- `ScriptWorld` methods that mutate authoritative state either:
  - Assert host (`place_train`, `place_train_at_interchange`).
  - Go through `StateManager.ApplyLocal(PropertyChange)` and inherit normal MP auth.
  - **Bypass authority** (`World.reset`, `set_feature_enabled`, `set_signal_system`, `code_ctc_route`, `ScriptCar.speed_mph` setter, `ScriptPassengerStop.offset_passengers_waiting`).

The bypass list above is **the surface area mods must respect**. A book authored for the host that's accidentally run on a client could mutate local-only state and produce desync.

---

## Error handling

Three layers:

1. **Syntax errors** at `Load`: `SyntaxErrorException` re-thrown after logging. Both consumers catch and report.
2. **Runtime errors** during `Run`: `ScriptRuntimeException` caught inside the resume loop, populates `LastRunError`, **breaks the coroutine** silently. Subsequent `LastRunError` checks by the consumer surface the error to the user.
3. **Generic `Exception` during `Run`**: same as ScriptRuntimeException — caught, populated, broken.

`Script.GlobalOptions.RethrowExceptionNested = true` is set in `StaticInit`. This means C# exceptions thrown from inside Lua-invoked C# code (e.g., `World.place_train` throwing `ScriptRuntimeException`) propagate up the Lua call stack with the original C# exception preserved as `InnerException`. The decorated message (`ScriptRuntimeException.DecoratedMessage`) includes the Lua call site.

`InteractiveBookRunner.TryRun` (`:248`) is a separate path used for button-callback closures. It catches all three exception types but only logs them — the user sees no UI feedback for failed button presses. **Buttons that throw silently fail.**

---

## Forbidden APIs (sandbox enforcement)

By module exclusion (see "Sandbox" above):

- **`io.*`** — file I/O entirely off. No `io.open`, `io.read`, `io.write`, `io.lines`.
- **`os.execute`, `os.exit`, `os.getenv`, `os.remove`, `os.rename`, `os.setlocale`, `os.tmpname`** — process and OS interaction off.
- **`debug.*`** — Lua introspection off (no `debug.sethook`, `debug.getinfo`, etc.).

Available from `os`: `os.time`, `os.date`, `os.clock`, `os.difftime` (the `OS_Time` module).

`load`/`loadstring`/`dofile`/`loadfile`/`require` are **available** in both vanilla consumers (because both pass non-null `modulePaths`). A determined script can `loadstring(...)()` arbitrary Lua. **Sandbox is `os/io/debug`-tight, not code-execution-tight.**

There is **no Lua-side allowlist** of UserData methods. Every public method on every registered Script* type is callable. The `[MoonSharpVisible(false)]` attribute exists (used once on `ScriptLocation.Location`) but is the only enforcement primitive.

---

## Patch points for custom Script* wrappers

Recipe for "I want to expose a new C# type to Lua":

1. **Define the wrapper class.** Plain public class. Keep field/method names snake_case to match vanilla style. Mark internal C# state `[MoonSharpVisible(false)]` or make it `internal`.

   ```csharp
   public class ScriptMyThing {
       internal MyThing Thing { get; }
       internal ScriptMyThing(MyThing t) { Thing = t; }
       public string id => Thing.Id;
       public void do_stuff() { /*…*/ }
   }
   ```

2. **Register the type in a `ScriptManager.StaticInit` postfix patch.** Must run before any `ScriptManager` is constructed.

   ```csharp
   [HarmonyPatch(typeof(ScriptManager), "StaticInit")]
   static class StaticInitPatch {
       static void Postfix() {
           UserData.RegisterType<ScriptMyThing>(InteropAccessMode.Default, "MyThing");
       }
   }
   ```

   `StaticInit` is one-shot via the `_initialized` static — your postfix only fires the first time. Plan accordingly (e.g., register before the postfix invokes the original).

3. **Inject access via `ScriptManager.Reset` postfix** if you want a global handle:

   ```csharp
   [HarmonyPatch(typeof(ScriptManager), "Reset")]
   static class ResetPatch {
       static void Postfix(ScriptManager __instance) {
           Script s = __instance;
           s.Globals["my_thing"] = new ScriptMyThing(MyThing.Shared);
       }
   }
   ```

   Or extend `ScriptWorld` indirectly by exposing a static factory (e.g., `World.get_my_thing(id)`) — but adding statics to `ScriptWorld` requires patching `ScriptWorld` directly, which Harmony can do but may be brittle to game updates.

4. **For book context extensions**, patch `BookContext`'s constructor and inject extra fields via a postfix that mutates the table (or replace `InteractiveBookRunner.RunBook` to build a richer context).

### Intercepting script calls

Two patch surfaces:

- **At the wrapper method** (e.g., Harmony prefix on `ScriptCar.set_load_percent`) — catches that method specifically. Easiest, scopes well.
- **At the resume loop** (`ScriptManager.Run(Closure, args[])`) — catches every yield boundary. Useful for instrumentation but cannot intercept mid-Lua-call C# invocations.

There is **no MoonSharp interception primitive** comparable to a debug hook in this build (the `Debug` module is excluded from the sandbox; even if you re-enabled it via patch, it adds Lua-side `debug.sethook` rather than a C#-side trap).

---

## Init-order pitfalls

- **`ScriptManager.StaticInit` is invoked from the constructor** (`ScriptManager.cs:60`). The first consumer to instantiate a `ScriptManager` *fires* the registry. In vanilla, that first consumer is `InteractiveBookRunner` when `TutorialManager.Show()` is called from `StateManager.OnPropertiesDidRestore` (`StateManager.cs:323`). **Mod patches that add UserData types must apply before save/load completes.** Harmony patches applied during `Awake` of a mod's bootstrap MonoBehaviour are typically early enough; patches deferred to UI-shown callbacks may be too late.
- **`Script.GlobalOptions` is a process-global singleton** (`MoonSharp.Interpreter/Script.cs:38, 64`). Set once in `StaticInit`. Mods that change it (e.g., to register a custom `Platform`) must do so before any script runs.
- **`UserData.RegisterType<T>(...)` second-call behaviour** depends on MoonSharp internals — re-registering the same type with a different friendly name is undefined. Don't.
- **`ScriptWorld.Shared` is constructed lazily on first access.** If your mod patches `ScriptWorld` and triggers `Shared` access from your patch's static ctor, you may snapshot the singleton too early. Prefer accessing `Shared` from instance methods invoked at script-run time.
- **`InteractiveBookRunner.PrepareScriptIfNeeded` reuses the existing `_script` if non-null** (`InteractiveBookRunner.cs:194`). After `Reload()`, `_script` is disposed and nulled, so a new one is created. But if a patch keeps a reference to the old `_script`, that reference becomes stale.

---

## Cross-references

- KVO write/read primitives (`StateManager.ApplyLocal`, `KeyValueObject`, observer callbacks): see [State Manager](state-manager.md) and [KVO Patterns](kvo-patterns.md).
- `set_property` / `get_property` auth chain: see [Access Control](access-control.md).
- `set_feature_enabled` and the MapFeature system (used by `set_signal_system`): see [Progression](progression.md#mapfeaturemanager--mapfeature).
- `place_train` / `place_train_at_interchange` and the `IPrefabStore` resolution: see [Asset Packs](asset-packs.md) and [Cars & Cargo](cars-cargo.md).
- `add_passengers` / `set_passenger_destination` semantics and `ops.passengerMarker` HostOnly KVO: see [Passengers & Timetable](passengers-timetable.md).
- `ScriptWorld.code_ctc_route`, `is_block_occupied` and the CTC controller: see [Signals & Dispatch](signals-dispatch.md).
- `set_switch_thrown` / `RequestSetSwitch` host-vs-client routing: see [Track Topology](track-topology.md).
- Console command parallels (`/ops`, `/tutorial`): see [Console Commands](console-commands.md).
- Tutorial KVO and `setupDescriptor.showTutorial` gate: see [Save & Load › Sandbox vs scenario](save-load.md#sandbox-vs-scenario-divergence).
- `Multiplayer.Broadcast` (used by `World.say`): see [Multiplayer Core](multiplayer-core.md).
- Coroutine yield model and `Time.timeScale` interaction: see [Time & Weather](time-weather.md).
