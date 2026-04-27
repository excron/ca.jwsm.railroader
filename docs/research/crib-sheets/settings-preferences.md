# Settings & Preferences — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railloader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Railloader.Injector/`)
**Companion:** [Save & Load](save-load.md)

Railroader has **two** disjoint settings systems with different scopes. Per-player local preferences (graphics, sound, mouse look, FOV, analytics opt-in, draw distance, UI scale, …) live on `UnityEngine.PlayerPrefs` keyed by short dotted strings, fronted by the static `Game.Preferences` class — these are local to the install and never leave the machine. Per-game-world settings (wear toggle, oil rate, AI behaviour, repair speed, multiplayer access control, passenger limit, …) live on the `_game` `KeyValueObject` fronted by `Game.State.GameStorage`, ride the snapshot/save, and replicate to clients via `PropertyChange`. The `Game.Settings.*Applicator` MonoBehaviours apply preferences to Unity systems by listening to `Messenger` events that `Preferences.*` setters emit. Mods get a UI hook via Railloader's `IModTabHandler.ModTabDidOpen(UIPanelBuilder)` and persist their own data via `IModdingContext.SaveSettingsData<T>` (JSON sidecar), or — for game-state — register a `KeyValueObject` (see [save-load › Mod-extension hooks](save-load.md#mod-extension-hooks)).

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `Game.Preferences` (static) | `Game/Preferences.cs` | The whole local-preferences API. Every property is a `PlayerPrefs.Get…/Set…` wrapper; setters fire Messenger events. |
| `PlayerPrefs` (Unity) | n/a | Backing store. Windows: HKCU\Software\Unity\UnityEditor\Giraffe Lab\Railroader (or per-Unity-version path) |
| `Game.State.GameStorage` | `Game.State/GameStorage.cs` | The whole `_game` per-world settings API. Backed by a single KVO; rides the save and MP snapshot. |
| `UI.PreferencesWindow.PreferencesBuilder` | `UI.PreferencesWindow/PreferencesBuilder.cs` | The Preferences window UI. Tabs: Character, Graphics, Sound, Input, Features. |
| `UI.CompanyWindow.SettingsPanelBuilder` | `UI.CompanyWindow/SettingsPanelBuilder.cs` | The in-game Company → Settings panel UI. Pages: Time, Features, MP Access, Map Features. Writes `GameStorage`. |
| `Game.Settings.*SettingsApplicator` (MonoBehaviours) | `Game.Settings/*.cs` | One per Unity subsystem (Canvas, Camera, Sound, Graphics, PostProcessing, Particle, Enviro, ScrollRect). Subscribe to Messenger events; apply prefs. |
| `Game.Settings.*Changed` (Messenger structs) | `Game.Settings/*.cs` | Notification events fired by `Preferences` setters. |
| `Railloader.IModTabHandler` | `Railloader.Interchange/Railloader/IModTabHandler.cs` | Mod-side UI hook — implement on a `PluginBase` to draw your own tab in the Railloader Settings window. |
| `Railloader.IModdingContext.SaveSettingsData<T>` | `Railloader.Interchange/Railloader/IModdingContext.cs` | Per-mod JSON sidecar persistence: `Mods/Railloader/ModSettings/<id>.json`. |

---

## The two settings systems compared

| Dimension | `Game.Preferences` (PlayerPrefs) | `Game.State.GameStorage` (`_game` KVO) |
|---|---|---|
| Scope | Per-player install (this Windows account) | Per-saved-world |
| Backing store | Unity `PlayerPrefs` (registry / plist / .pref file) | KVO object → snapshot → `.shortsave` MessagePack |
| Lifetime | Forever, across all save files | Lives and dies with the save file |
| MP authority | None — local | HostOnly by default; some keys: Officer/Trainmaster (see `GameStorage.AuthorizationRequirementForPropertyWrite`) |
| Replication | None | Yes — snapshot on join + `PropertyChange` on edit |
| UI surface | `PreferencesWindow` (top-bar gear icon, main menu Settings) | `CompanyWindow` → Settings tab (in-game) |
| Change notification | `Messenger.Default.Send(*Changed)` from setter | `KeyValueObject.Observe(key)` |
| Default values | Hard-coded second arg to `PlayerPrefs.GetX(key, default)` | Hard-coded `…OrDefault(default)` in property getter |
| Key naming | Dotted lowercase: `gfx.canvas.scale`, `sound.volume.bell` | Camel/snake hybrid: `wearFeatre`, `aiPassStopMinStopDur` |

---

## `Game.Preferences` — local-machine preferences

Static façade over `PlayerPrefs`. Every public property is a get/set pair; the get reads `PlayerPrefs.GetX(key, default)`, the set writes back and (in many cases) sends a Messenger event so the `*SettingsApplicator` for that subsystem rebinds.

### Full PlayerPrefs key catalog

Sourced from `Game/Preferences.cs` (each constant is the literal `key` string passed to `PlayerPrefs`).

| Key | Type | Default | Property | Setter event |
|---|---|---|---|---|
| `analytics` | int (`AnalyticsPref` enum) | `Unknown=0` | `Preferences.Analytics` | `AnalyticsPreferenceDidChange` |
| `avatar.descriptor` | string (`KeyValueJson`-encoded) | `AvatarDescriptor.Default` | `Preferences.AvatarDescriptor` | none |
| `multiplayer.lobby.name` | string | `""` | `Preferences.MultiplayerLobbyName` | none |
| `multiplayer.lobby.type` | int | `0` | `Preferences.MultiplayerLobbyType` | none |
| `multiplayer.client.username` | string | `""` (set to Steam name in `StateManager.Awake` if empty) | `Preferences.MultiplayerClientUsername` | none |
| `host-auth-logging` | bool (int 0/1) | `false` | `Preferences.HostAuthLogging` | none |
| `camera.sway.intensity` | float | `1.0` | `Preferences.CameraSwayIntensity` (cached in `_cameraSwayIntensity`) | none |
| `camera.look.speed` | float | `1.0` | `Preferences.MouseLookSpeed` (cached) | none |
| `camera.look.invert` | bool | `false` | `Preferences.MouseLookInvert` (cached) | none |
| `camera.look.toggle` | bool | `false` | `Preferences.MouseLookToggle` (cached) | none |
| `ui.fov0` | float | `40.0` | `Preferences.DefaultFOV` | none |
| `ui.fov1` | float | `80.0` | `Preferences.AlternateFOV` | none |
| `ui.compass` | bool | `true` | `Preferences.ShowCompass` | (sender broadcasts `UISettingDidChange` from PreferencesBuilder) |
| `ui.clock.always` | bool | `true` | `Preferences.ShowClockAlways` | (same — `UISettingDidChange` from UI) |
| `gfx.drawdistance` | float (clamped 100..10000) | `1500.0` | `Preferences.GraphicsDrawDistance` | `GraphicsDrawDistanceChanged` |
| `gfx.particlelevel` | int (`ParticleLevel` enum) | `2` (`Standard`) | `Preferences.GraphicsParticleLevel` | none |
| `gfx.tree.density` | float | `1.0` | `Preferences.GraphicsTreeDensity` | none (UI applies via `MapCameraUpdater.SetTerrainDensityValues`) |
| `gfx.detail.density` | float | `1.0` | `Preferences.GraphicsDetailDensity` | none (same) |
| `gfx.msaa` | (constant declared but unread) | n/a | n/a | n/a — **dead code**: const `KeyGfxAntiAliasing = "gfx.msaa"` is defined but no property reads/writes it. AA is set via Unity QualitySettings instead. |
| `gfx.vsync` | int (`GraphicsVsyncOption` enum, -1..2) | `-1` (`SixtyFps`) | `Preferences.GraphicsVsync` | `GraphicsSettingsChanged` |
| `gfx.fps.limit` | (constant declared but unread) | n/a | n/a | **dead code**; `GraphicsSettingsApplicator.UpdateSettings` hard-codes `targetFrameRate = 240` (or 60 for SixtyFps mode) instead. |
| `gfx.canvas.scale` | float | `1.0` | `Preferences.GraphicsCanvasScale` | `CanvasScaleChanged` |
| `gfx.night-light-level` | float | `0.3` | `Preferences.GraphicsNightLightLevel` | `EnviroSettingChanged` |
| `gfx.post-exp` | float | `0.5` | `Preferences.GraphicsPostExposure` | `PostProcessingPreferenceChanged` |
| `gfx.contrast` | float | `25.0` | `Preferences.GraphicsContrast` | `PostProcessingPreferenceChanged` |
| `sound.volume.main` | float | `0.8` | `Preferences.SoundVolumeMain` | `SoundVolumeChanged` |
| `sound.volume.engine` | float | `1.0` | `Preferences.SoundVolumeEngine` | `SoundVolumeChanged` |
| `sound.volume.whistle` | float | `1.0` | `Preferences.SoundVolumeWhistle` | `SoundVolumeChanged` |
| `sound.volume.bell` | float | `1.0` | `Preferences.SoundVolumeBell` | `SoundVolumeChanged` |
| `sound.volume.dynamo` | float | `1.0` | `Preferences.SoundVolumeDynamo` | `SoundVolumeChanged` |
| `sound.volume.environment` | float | `1.0` | `Preferences.SoundVolumeEnvironment` | `SoundVolumeChanged` |
| `sound.volume.ctc-bell` | float | `1.0` | `Preferences.SoundVolumeCtcBell` | `SoundVolumeChanged` |
| `sound.volume.wheels` | float | `1.0` | `Preferences.SoundVolumeWheels` | `SoundVolumeChanged` |
| `controls.simplified` | bool | `false` | `Preferences.SimplifiedControls` | none |
| `car.update-opt` | bool | `false` | `Preferences.EnableCarUpdateOptimization` | none |

### Bool encoding

`Preferences.GetBool` / `SetBool` (private helpers, `Preferences.cs:555-563`):

```csharp
private static bool GetBool(string key, bool defaultValue)
    => PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) != 0;
private static void SetBool(string key, bool value)
    => PlayerPrefs.SetInt(key, value ? 1 : 0);
```

So all Preferences "bools" are `int` 0/1 in `PlayerPrefs`. Direct `PlayerPrefs.GetString` reads on these keys will see "1"/"0".

### Cached fields

`CameraSwayIntensity`, `MouseLookSpeed`, `MouseLookInvert`, `MouseLookToggle` cache the value in a `static float?`/`bool?` field on first read to avoid `PlayerPrefs` calls in hot per-frame paths (camera tick). Cache is **never invalidated** — if you patch `Preferences.MouseLookSpeed` setter to add side effects, the cache update happens in vanilla code (`_mouseLookSpeed = value;` before `PlayerPrefs.SetFloat`). Direct `PlayerPrefs.SetFloat("camera.look.speed", x)` from outside `Preferences` will **not** update the cache and the next read returns the stale value.

### `AnalyticsPref` enum

```csharp
public enum AnalyticsPref { Unknown, OptIn, OptOut }              // Preferences.cs:14
```

`Unknown = 0` is the *default*, treated as opt-out by callers but distinguishable from explicit opt-out. UI in `PreferencesBuilder.BuildOtherSection` shows a toggle that maps OptIn ↔ OptOut, never writes Unknown.

### `ParticleLevel` enum

```csharp
public enum ParticleLevel { Off, Low, Standard }                  // Preferences.cs:21
```

`Low` is unused by the shipped UI — `PreferencesBuilder.BuildTabGraphics` only offers Off and Standard (`PreferencesBuilder.cs:145-156`). The Low value is dead code.

### `GraphicsVsyncOption` enum

```csharp
public enum GraphicsVsyncOption {                                 // Preferences.cs:28
    SixtyFps = -1,
    DontSync,           // 0
    VsyncEveryFrame,    // 1
    VsyncEveryOther,    // 2
}
```

`SixtyFps = -1` triggers `Application.targetFrameRate = 60`; everything else uses 240. (`GraphicsSettingsApplicator.UpdateSettings`)

### Patch candidates

| Method | Why patch |
|---|---|
| `Preferences.<setter>` | Catch every change (MP-broadcast a UI preference, log changes, force recompute caches). |
| `Preferences.GetBool` / `SetBool` (private) | Add a "bool with default" surface; or change encoding to 0/1 strings if integrating with external tooling. |
| `Game.Settings.*Applicator.UpdateXxx` | Modify how a preference is applied to Unity (e.g., custom volume curve in `SoundSettingsApplicator.UpdateMixer`). |

### Gotchas

- **`PlayerPrefs` is synchronous and blocking.** Repeated writes thrash the registry; vanilla guards by using cached fields for hot keys but newly added prefs in mods should also cache.
- **`PlayerPrefs.Save()` is never called explicitly** in vanilla. Unity flushes on `OnApplicationQuit`. A crash before quit loses unflushed writes.
- **Cached prefs are static fields**; survive scene loads but reset on app restart. If you want a per-session-only override (e.g., cheat console), set the cached field directly without touching `PlayerPrefs`.
- **`avatar.descriptor` is JSON-in-a-string-pref**: a `KeyValueJson.StringFromValue(value.ToValue())`. Catch deserialization exceptions if patching.
- **Two key constants are dead:** `gfx.msaa` and `gfx.fps.limit`. They have `const string` declarations but no property reads them. Don't rely on these as supported preferences.
- **`MultiplayerClientUsername` defaults to Steam name** (set in `StateManager.Awake` if `IsNullOrEmpty`). If you patch `StateManager.Awake` to bypass that, the player will appear with empty username on first connect.
- **No "preference reset" UI.** Resetting requires either deleting the user's PlayerPrefs entry or writing a new default. Mods adding settings should provide their own reset path if needed.

---

## `Game.State.GameStorage` — per-save world settings

The full settings surface on the `_game` KVO. See [save-load › Wire-format key names](save-load.md#wire-format-key-names-load-bearing-typos-and-oddities) for the on-disk key catalog and load-bearing typos.

### Storage facts

- KVO id: `"_game"` (constant `GameStorage.ObjectId`).
- Created in `StateManager.PrepareGameKeyValueObject` (`StateManager.cs:363`) on `MapWillLoadEvent`. Destroyed on `MapDidUnloadEvent`.
- Registered with `IPropertyAccessControlDelegate = this` (GameStorage itself implements the interface) via `StateManager.RegisterPropertyObject("_game", kvo, this)` in the constructor.
- Default access: `HostOnly` (`GameStorage.AuthorizationRequirementForPropertyWrite` returns `HostOnly` for any unmatched key).
- Exceptions:
  - `interchangeServeHour` → `MinimumLevelOfficer`
  - `interchangeShuffle` → `MinimumLevelOfficer`
  - `aiCrossingSignal` → `MinimumLevelTrainmaster`
  - `aiPassStopEnable` → `MinimumLevelTrainmaster`
  - `aiPassStopMinStopDur` → `MinimumLevelTrainmaster`

### Properties

(Counterpart to the [save-load wire-format table](save-load.md#wire-format-key-names-load-bearing-typos-and-oddities); this view is "what's the C# API.")

```csharp
public GameMode GameMode             { get; set; }            // 78    key "mode"
public string SetupId                { get; set; }            // 94    key "setupId"
public string RailroadName           { get; set; }            // 106   key "railroadName" (truncated to 50)
public string RailroadMark           { get; set; }            // 118   key "railroadMark" (truncated to 6)
public int Balance                   { get; set; }            // 130   key "balance"
public float TimeMultiplier          { get; set; }            // 153   key "timeMultiplier" default 2
public AccessLevel DefaultAccessLevel{ get; set; }            // 165   key "defaultAccessLevel" default Passenger
public bool AllowNewPlayers          { get; set; }            // 182   key "allowNewPlayers" default true
public string NewPlayerPasswordHash  { get; set; }            // 199   key "passwordHash"
public bool TrainCrewMembershipRequired             { get; set; }  // 213
public bool TrainCrewMembershipManagedByTrainmaster { get; set; }  // 225
public int LoanAmount                { get; set; }            // 237   key "loanAmount"
public GameDateTime? NextInterestDate{ get; set; }            // 249   key "loanNextInterestDate" (float seconds; null when no loan)
public int LoanNextInterestOffset    { get; set; }            // 266   key "loanNextInterestOffset"
public float UnbilledAutoEngineerRunDuration { get; set; }    // 278
public int InterchangeServeHour      { get; set; }            // 290   default 6
public int InterchangeShuffle        { get; set; }            // 304   default 0
public int PassengerLimit            { get; set; }            // 316   default 8
public float? BrakeForce             { get; set; }            // 330   key "brakeForce" (null = use Config default)
public bool WearFeature              { get; set; }            // 347   key "wearFeatre" default true   ← typo
public bool OilFeature               { get; set; }            // 359   key "oilPrevMaintFeature" default true
public bool TimetableFeature         { get; set; }            // 371   default false
public int OverhaulMiles             { get; set; }            // 383   key "overhaulMi" default 2500
public float WearMultiplier          { get; set; }            // 395   key "wearMult" default 1
public float OilUseMultiplier        { get; set; }            // 407   key "oilUseMult" default 1
public bool MapShowsSwitches         { get; set; }            // 419   default true
public CrossingSignalSetting AICrossingSignal { get; set; }   // 431   default On
public bool AIPassengerStopEnable    { get; set; }            // 443   default true
public int AIPassengerStopMinimumStopDuration { get; set; }   // 455   default 60
public int AICallSignals             { get; set; }            // 467   default 1
```

### Observers

A subset of properties expose `Observe…(Action, [bool initial])` factories. Subscribers are typically wired in `StateManager.OnPropertiesDidRestore`:

```csharp
public IDisposable ObserveTimeMultiplier(Action<float>, bool initial)            // 494
public IDisposable ObserveNewPlayerPasswordHash(Action, bool initial)            // 502
public IDisposable ObserveGameMode(Action<GameMode>, bool initial)               // 510
public IDisposable ObserveWeatherId(Action<int>)                                  // 518
public IDisposable ObserveBrakeForce(Action<float?>)                              // 526
public IDisposable ObserveBrakeForceHandbrake(Action<float?>)                     // 534
public IDisposable ObserveWearFeature(Action<bool>, bool observeFirst = true)    // 542
public IDisposable ObserveOilFeature(Action<bool>)                                // 550
public IDisposable ObserveTimetableFeature(Action<bool>, bool callInitial)        // 558
public IDisposable ObserveOverhaulMiles(Action<int>)                              // 566
public IDisposable ObserveWearMultiplier(Action<float>)                           // 574
public IDisposable ObserveOilUseMultiplier(Action<float>)                         // 582
public IDisposable ObserveMapShowsSwitches(Action<bool>, bool callInitial)        // 590
```

For unobserved properties, use `_storage._gameKeyValueObject.Observe(key, callback)` directly (the underlying `KeyValueObject` exposes `Observe(string, Action<Value>, bool initial = false)`).

### `WearFeature` companion sliders & UI hide-when-off

Built in `SettingsPanelBuilder.BuildFeatureWear` (`UI.CompanyWindow/SettingsPanelBuilder.cs:203`). Toggling `OilFeature` triggers `RequestSaveReopen()` modal — the change requires reload because `Car.SetupOiling` references aren't dynamically toggled during play. Toggling `WearFeature` only triggers reopen if `OilFeature` is currently on (chained dependency). Wear/oil sliders are hidden behind their feature toggles in the UI.

### Patch candidates

| Method | Why patch |
|---|---|
| `GameStorage.AuthorizationRequirementForPropertyWrite(string)` | Add per-key auth tiers for mod-added `_game` keys (or change vanilla key auth — risky, e.g., letting Trainmasters change `defaultAccessLevel`). |
| `GameStorage.<setter>` | Veto specific value changes; clamp; broadcast to non-vanilla observers. |
| `StateManager.OnPropertiesDidRestore` | Add `_storage.Observe…(handler)` for any new mod-tracked setting. |
| `StateManager.PrepareGameKeyValueObject` | Inject pre-set defaults into `_game` before any observer wires up. |

### Gotchas

- **Default values are read at the property getter.** If a key is not in the saved KVO (new install, fresh save), `_gameKeyValueObject[key]` returns `Value.Null()` (or the typed default) and `…OrDefault(default)` provides the value. **Mods that read these directly via `KeyValueObject` must use the same `…OrDefault` pattern**.
- **`Balance != value` short-circuit** on `Balance` setter (`GameStorage.cs:146`) — setting balance to its current value is a no-op KVO write and won't fire `BalanceDidChange`.
- **`SetupId` is captured at new game time and is sticky.** Renaming the setup descriptor breaks scenarios mid-save; the saved id is what's looked up.
- **`WearFeature` and `OilFeature` defaults are `true`.** Sandbox saves with no toggle history start with both on. Toggling off in UI never deletes the key — you cannot revert to "default" by deleting; only by re-toggling.

---

## In-game settings UI: `UI.CompanyWindow.SettingsPanelBuilder`

The Company → Settings panel (`UI.CompanyWindow/SettingsPanelBuilder.cs`). Tabbed list-detail. Pages enumerated by `PageId`:

| Page | Always shown? | Section builders |
|---|---|---|
| Time | yes | `BuildTime` — time of day field, `WaitTime` modal button, `InterchangeServeHour` dropdown |
| Features | yes | `BuildFeatures` — Interchange Blocking, Brake Force, Wear & Tear (+nested), AI Crossings, AI Call Signals, AI Passenger Stop, Timetable, Map |
| Multiplayer Access Control | host only | `BuildMultiplayerAccessControl` — reporting mark, log auth, password, default access level, passenger limit, train crew access |
| Map Features | sandbox + host only | `BuildMapFeatures` — toggles per `MapFeature` from `MapFeatureManager` |

**Auth-aware UI:** `BuildFeatureInterchangeBlocking` uses `GameStorage.CanWriteInterchangeShuffle` (`StateManager.CheckAuthorizedToChangeProperty("_game", "interchangeShuffle")`) to disable the dropdown for unauthorized clients. Use this pattern (`CheckAuthorizedToChangeProperty(objectId, key)`) in mod UIs.

**Auto-rebuild on remote changes:** `BuildFeatureWear` adds `builder.AddObserver(gameStorage.ObserveWearFeature(rebuild))` so a remote toggle (host changes wear) re-renders the panel. Mod UIs should mirror this for any KVO-backed control.

### Patch candidates

| Method | Why patch |
|---|---|
| `SettingsPanelBuilder.Build` | Add new pages to the `list` (insert your `ListItem<Page>` then patch `BuildTabs` switch to handle a new `PageId` enum value — but the enum is private, so prefer adding a separate window). |
| `SettingsPanelBuilder.BuildFeatures` | Postfix to add new sections to the Features page (probably the cleanest "drop a toggle into Company Settings" hook). |
| `SettingsPanelBuilder.RequestSaveReopen` (private) | Replace the modal with hot-apply if your mod can handle live wear/oil toggling. |

### Gotchas

- **`PageId` is a private enum** with no extension mechanism. Adding a new page requires patching `Build` and adding to the dispatch switch in `BuildTabs`.
- **`SettingsPanelBuilder` is a struct with `[StructLayout(LayoutKind.Sequential, Size = 1)]`** — empty by design, all methods are static. Don't try to instantiate it.
- The Features page is host-or-anyone-with-write-access depending on the toggle. Auth checks are inside each individual `BuildFeatureXxx`. There's no page-level read-only mode.

---

## Local preferences UI: `UI.PreferencesWindow.PreferencesBuilder`

The Preferences window (`UI.PreferencesWindow/PreferencesWindow.cs` + `PreferencesBuilder.cs`). Tabbed UI:

| Tab | id | Builder |
|---|---|---|
| Character | `char` | `CharacterSettingsBuilder.BuildCharacterPanel` |
| Graphics | `gfx` | `BuildTabGraphics` (vscroll wrapper) |
| Sound | `sound` | `BuildTabSound` |
| Input | `input` | `BuildTabInput` |
| Features | `features` | `BuildTabFeatures` (which calls `BuildBehaviorSection` + `BuildOtherSection`) |

### Patch candidates

| Method | Why patch |
|---|---|
| `PreferencesBuilder.BuildTabs` | Add a new tab (insert into the existing list). |
| `PreferencesBuilder.BuildTabFeatures` / `BuildBehaviorSection` / `BuildOtherSection` | Append a section to the Features tab — this is the most natural place for mod-added local preferences. |

### Gotchas

- **`BindingsWindow.CanShow` is false in main menu.** Input tab degrades to a static label. If your mod adds keybindings, account for "preferences may be opened from main menu" timing.
- **Tree/Detail Density use `_pendingTreeDensity` / `_pendingDetailDensity` static fields** with a `LeanTween.delayedCall(0.25f, …)` debounce. Two sliders sharing one debounce timer; rapid changes coalesce.
- **`_uiScaleRectTransform` and `_uiScaleValue` are static** — switching tabs and back recomputes them. The pattern works because `Build` is called fresh on each tab change.

---

## `Game.Settings.*Applicator` MonoBehaviours

Each is a `MonoBehaviour` that lives in the GameUI scene (typically attached to the relevant Unity component — `CanvasScaler`, `AudioMixer` host, etc.). On `OnEnable`/`Start` they register Messenger listeners; in `OnDisable` they unregister. They read `Preferences.*` and apply.

### Inventory

| Applicator | Listens to | Applies to |
|---|---|---|
| `CanvasSettingsApplicator` | `CanvasScaleChanged` | `CanvasScaler.scaleFactor` |
| `CameraSettingsApplicator` | (no Messenger; reads `Preferences.CameraSwayIntensity` directly) | Camera sway helper |
| `SoundSettingsApplicator` | `SoundVolumeChanged` | `AudioMixer` exposed parameters (`VolMaster`, `VolEngine`, `VolEngineBell`, `VolEngineWhistle`, `VolDynamo`, `VolEnvironment`, `VolCtcBell`, `VolWheels`) |
| `GraphicsSettingsApplicator` | `GraphicsSettingsChanged`, `CanvasScaleChanged` | `QualitySettings.vSyncCount`, `Application.targetFrameRate`, validates canvas scale on screen-size change |
| `PostProcessingSettingsApplicator` | `PostProcessingPreferenceChanged` | URP volume / post-exposure / contrast |
| `ParticleSettingsApplicator` | (none; reads on enable) | Particle systems |
| `EnviroSettingsApplicator` | `EnviroSettingChanged` | Enviro3 night light level |
| `ScrollRectSettingsApplicator` | (none; reads on enable) | ScrollRect scroll sensitivity |

### Pattern: subsystem mixer / canvas / quality

`SoundSettingsApplicator.Start` (`Game.Settings/SoundSettingsApplicator.cs:28`) reads the *default* normalized values from the mixer once (so the prefs slider acts as a multiplier), then `UpdateMixer` re-applies. To add a new volume preference: extend `Preferences.SoundVolumeXxx`, expose an exposed `VolXxx` parameter on the AudioMixer asset, add a default-capture line and a `SetNorm` line in `UpdateMixer`, and add a slider to `PreferencesBuilder.BuildTabSound`.

### Patch candidates

| Method | Why patch |
|---|---|
| `*Applicator.UpdateXxx` | Custom application of an existing preference (e.g., a different volume curve, a different vsync mapping). |
| `CanvasSettingsApplicator.MaxCanvasScale` | The `Mathf.Clamp(... 0.1, 2.0)` UI scale ceiling — bump for ultra-high-DPI displays. |
| `GraphicsSettingsApplicator.UpdateSettings` | Replace the `targetFrameRate = 240` hard-cap (or hook into `gfx.fps.limit`, the dead pref). |

### Gotchas

- **`SoundSettingsApplicator._defaultMaster` etc. are captured on `Start`.** If something sets the mixer's default after this applicator starts, the cached default is stale and `UpdateMixer`'s multiplier math goes wrong. Boot order matters.
- **`GraphicsSettingsApplicator.RefreshCoroutine` polls every 1s** to validate canvas scale on screen-size change. Cheap, but a long-running coroutine that survives scene loads if the GameObject does.
- **No applicator for `MouseLook*` or `controls.simplified` etc.** — those preferences are read at the call site (e.g., camera input handler). Patches changing those prefs must invalidate caches manually if needed (see `Preferences` cached fields gotcha).
- **`AnalyticsPreferenceDidChange` is fired but no `*Applicator` listens.** It's consumed by `Analytics/Analytics.cs` (or Analytics-related code) — search for the event subscriber.

---

## Mod-side settings APIs (Railloader)

### Mod tab in Settings window — `IModTabHandler`

```csharp
public interface IModTabHandler {                                  // Railloader.Interchange/Railloader/IModTabHandler.cs
    void ModTabDidOpen(UIPanelBuilder builder);
    void ModTabDidClose();
}
```

If a class deriving from `Railloader.PluginBase` implements `IModTabHandler`, the mod gets a tab in the Railloader Settings window (gear-icon top right → Mods). The tab is rendered via `SettingsWindow.PopulateWindow` → `DrawModPanel` (`Railloader.Settings/SettingsWindow.cs:590`).

- `ModTabDidOpen` may be called multiple times per logical "open" (re-render passes). Don't allocate persistent state inside; use the builder API for layout, store mutable form state outside.
- `ModTabDidClose` runs when switching mods or closing the window. Use to flush settings to JSON.

### Mod settings persistence — `IModdingContext.SaveSettingsData<T>` / `LoadSettingsData<T>`

```csharp
T? LoadSettingsData<T>(string settingsIdentifier) where T : class;
void SaveSettingsData<T>(string settingsIdentifier, T settings) where T : class;
```

Implementation at `Railloader.ModLoading/ModdingContext.cs:42-78`:
- Path: `Mods/Railloader/ModSettings/<sanitized-identifier>.json`.
- Encoding: `JsonConvert.Serialize/Deserialize` (Newtonsoft).
- Sanitization: `Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars())` replaced with `_`.
- One file per identifier; not one file per setting.
- Read returns `null` if file missing; deserialize errors logged and `null` returned.
- Write creates the directory if missing.

**This is where you put per-install mod prefs.** Use `mod.Id` as the identifier (so `Mods/Railloader/ModSettings/myMod.json`).

### What's NOT exposed

- No "register a `Game.Preferences` extension method" — the `Preferences` class is sealed-by-design. Mods extending player prefs should use either Railloader's JSON sidecar or write their own `PlayerPrefs` keys with a unique prefix.
- No "register a Game `_game` setting" key — mods using KVO-based per-save settings register their own `KeyValueObject` via `StateManager.RegisterPropertyObject`. See [save-load › Route 1](save-load.md#route-1--kvo-objects-in-snapshot).
- No equivalent of `IModTabHandler` for the in-game Company Settings panel. Adding to `SettingsPanelBuilder.BuildFeatures` requires a Harmony patch.

---

## Cross-references

- For per-save data persistence and the `_game` KVO's role in the save file: see [Save & Load](save-load.md), especially [Wire-format key names](save-load.md#wire-format-key-names-load-bearing-typos-and-oddities).
- For wear-feature toggle propagation (the `wearFeatre` key path from KVO to `Car.WearFeature` static): see [Wear & Durability › toggle spine](wear-durability.md#toggle-spine-how-wearfeature-propagates).
- For coupler-related per-car KVO keys (which use the same `KeyValueObject` machinery as `_game`, just on per-car objects): see [Couplers › KVO key naming](couplers.md#kvo-key-naming).
- For multiplayer authority on `_game` writes — `HostOnly`, `MinimumLevelTrainmaster`, etc.: see [`../multiplayer-vanilla-survey.md`](../multiplayer-vanilla-survey.md) and `Game.AccessControl/AuthorizationRequirement.cs`.
