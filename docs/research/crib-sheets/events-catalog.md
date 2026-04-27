# Events Catalog — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`)
**Companion:** [KVO patterns](kvo-patterns.md), [UI vanilla](ui-vanilla.md), [Multiplayer core](multiplayer-core.md), [State manager](state-manager.md)

This sheet is the consolidated index of every cross-cutting event surface in vanilla Railroader. Two surfaces are catalogued:

1. **GalaSoft `Messenger.Default` event types** — local-process pub/sub bus. 50 distinct event types live across 5 namespaces. 39 in `Game.Events`, 6 in `Game.Settings`, 1 each in `Analytics`, `Game`, `Game.State.Ledger`, and `UI.Timetable`. 1 type is sent only via the `FireEvent`-mirrored network path (`BalanceDidChange`); 3 are dual-path. The bus is **process-local — no cross-machine delivery** unless mirrored explicitly.
2. **`PropertyChange.Control` enum** — the canonical control-key namespace for `Car.KeyValueObject` writes. 18 enum values with stable on-disk string keys. This is the de-facto "named control" registry; mods cannot extend the enum.

Other event-shaped systems with their own home crib sheets:
- KVO `Observe`/`KeyValueObject` writes → [`kvo-patterns.md`](kvo-patterns.md)
- `IGameMessage` request/response → [`request-messages.md`](request-messages.md)
- `IIntegrationSetEventHandler` callbacks → [`couplers.md`](couplers.md#iintegrationseteventhandler-the-abstraction)
- `IPickable.Activate` / `IInteractable` → [`ui-vanilla.md`](ui-vanilla.md), [`input-keybinds.md`](input-keybinds.md)
- `OnPropertiesDidRestore` virtual → [`save-load.md`](save-load.md)

Use this sheet to: find which event to subscribe to for "X happens", confirm an event's payload before patching a sender/handler, decide whether your mod's event should mirror or stay local-only.

---

## Section 1 — Messenger.Default event catalog

### Quick legend

- **Payload** — `()` means empty struct (sentinel). Listed `Type field` rows are public/readonly fields the consumer reads.
- **Sender** — primary call site. Multiple sites are listed when load-bearing.
- **Listeners** — vanilla subscribers (illustrative; not exhaustive when >5).
- **Frequency** — `one-shot` (lifecycle), `event-driven` (per user/sim action), `per-tick` (timer/loop), `per-min`/`per-hour`/`per-day` (game-clock buckets).
- **MP** — `local` (sender-machine only), `mirrored` (sender broadcasts via FireEvent or KVO), `host-fan-out` (host sends locally; clients see indirectly via state replication). The Messenger bus itself is **always local**; "mirrored" describes whether vanilla code arranges for the matching event to fire on the other side.

---

### 1A — World/map lifecycle

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `MapWillLoadEvent` | `Game.Events` | `()` | `UI.Menu/GlobalGameManager.cs:103` | one-shot per map load | local (each machine) |
| `MapDidLoadEvent` | `Game.Events` | `()` | `Game/MapLoader.cs:58` | one-shot per map load | local (each machine) |
| `MapWillUnloadEvent` | `Game.Events` | `()` | `UI.Menu/GlobalGameManager.cs:207` | one-shot per map unload | local |
| `MapDidUnloadEvent` | `Game.Events` | `()` | `UI.Menu/GlobalGameManager.cs:242` | one-shot per map unload | local |
| `WorldWillSave` | `Game.Events` | `()` | `Game.Persistence/WorldStore.cs:176` | per save (autosave + manual) | host-only (only host saves) |
| `PropertiesDidRestore` | `Game.Events` | `()` | `Game.State/RestoreNotifier.cs:69` | one-shot per load | local (fires after each machine restores its KVO) |
| `WorldDidMoveEvent` | `Game.Events` | `Vector3 Offset` | `Helpers/WorldTransformer.cs:173` | when world re-centers (rare; far-from-origin) | local |
| `GameModeDidChange` | `Game.Events` | `GameMode GameMode` | `Game.State/StateManager.cs:276` | one-shot per session (Career/Sandbox/etc.) | local; both ends derive from session state |

**Listeners (representative):**
- `MapDidLoadEvent`: `StateManager.OnMapDidLoad` (`StateManager.cs:218`), `TrainController.OnMapLoaded` (`TrainController.cs:388`), `CameraSelector.HandleMapDidLoad` (`CameraSelector.cs:136`), `DefinitionEditorModeController.MapDidLoad` (`DefinitionEditorModeController.cs:48`).
- `WorldDidMoveEvent`: `StrategyCameraController` (136), `MapCameraUpdater` (26), `AvatarPrefab` (35), `CharacterController` (201), `DepthProjectorHelper` (26), `TrainController.WorldDidMove` (389), `PrefabInstancer` (81). Anything storing world-space coordinates re-bases here.
- `PropertiesDidRestore`: `StateManager.OnPropertiesDidRestore` (`StateManager.cs:221, 271-311`) — wires the static `Car.WearFeature`/`OilFeature`/etc. KVO observers. `BalanceDisplay.cs:30`, `TopRightArea.cs:53` rebuild after restore.
- `WorldWillSave`: `AutoEngineerPlanner.cs:275` flushes pending plan state.

### 1B — Time advancement

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `TimeAdvanced` | `Game.Events` | `()` | `Game.State/StateManager.cs:825` | every Update tick (always) | host-only sender; `TimeObserver` runs on both, derives from clock |
| `TimeMinuteDidChange` | `Game.Events` | `()` | `Game.State/TimeObserver.cs:59` | per game-minute crossing | local (each machine derives) |
| `TimeHourDidChange` | `Game.Events` | `()` | `Game.State/TimeObserver.cs:55` | per game-hour crossing | local |
| `TimeDayDidChange` | `Game.Events` | `()` | `Game.State/TimeObserver.cs:51` | per game-day crossing | local |

**Listeners:**
- `TimeObserver` itself registers `TimeAdvanced` (`TimeObserver.cs:18`) and fires the minute/hour/day cascade. **Subscribe to TimeMinute/Hour/Day, not TimeAdvanced**, unless you truly need per-frame.
- `DailyReportGenerator.cs:93` (TimeAdvanced — generates yesterday's report after midnight).
- `StateManager.OnDayDidChange` (TimeDayDidChange, `StateManager.cs:223`).
- `OpsController` listens to `TimeDayDidChange` (`OpsController.cs:100`) and `TimeMinuteDidChange` (`OpsController.cs:101`).
- `PassengerExpiration.cs:25` listens to `TimeAdvanced`.
- UI: `Interchange.cs:276-277` `RebuildOnEvent<TimeAdvanced>` and `RebuildOnEvent<TimeHourDidChange>`.

**Gotcha:** `TimeAdvanced` fires **every game-update tick** (typically 60Hz). Wire UI panels to `TimeMinuteDidChange` if a minute-resolution rebuild is acceptable.

### 1C — Settings & preferences

All sent from `Game/Preferences.cs` setters (PlayerPrefs writes). Each is consumed by a `*SettingsApplicator` MonoBehaviour in `Game.Settings/`. See [`settings-preferences.md`](settings-preferences.md) for the full Preferences surface.

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `CanvasScaleChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:379` | UI slider drag | local (per-client preference) |
| `EnviroSettingChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:392` | UI toggle | local |
| `PostProcessingPreferenceChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:405, 418` | UI toggle | local |
| `SoundVolumeChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:431, 444, 457, 470, 483, 496, 509, 522` (one per mixer group) | per slider | local |
| `GraphicsSettingsChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:552` | UI apply | local |
| `GraphicsDrawDistanceChanged` | `Game.Settings` | `()` | `Game/Preferences.cs:317` | slider | local |
| `AnalyticsPreferenceDidChange` | `Analytics` | `()` | `Game/Preferences.cs:107` | toggle | local |
| `UISettingDidChange` | `Game.Events` | `()` | `UI.PreferencesWindow/PreferencesBuilder.cs:70, 75` | UI toggle (HUD/compass etc) | local |

**Listeners:** `SoundSettingsApplicator.cs:30`, `EnviroSettingsApplicator.cs:18`, `PostProcessingSettingsApplicator.cs:24`, `CameraSettingsApplicator.cs:19`, `CanvasSettingsApplicator.cs:20`, `GraphicsSettingsApplicator.cs:14, 18` (note: also subscribes to `CanvasScaleChanged`), `AnalyticsManager.cs:54`. UI consumers: `CompassHUD.cs:73` and `TopRightArea.cs:49` listen to `UISettingDidChange`. `MapBuilder.cs:146` listens to `CanvasScaleChanged`. `PreferencesBuilder.cs:131` `RebuildOnEvent<CanvasScaleChanged>`.

**`SoundVolumeChanged` is fired 8 times** — once per mixer group setter. There is no group identifier on the event; the applicator re-reads all groups on every fire.

### 1D — Track / graph topology

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `GraphDidRebuildCollections` | `Game.Events` | `()` | `Track/Graph.cs:197` | after track topology rebuild | local on each machine after KVO restore |
| `GraphDidChangeEnabledGroups` | `Game.Events` | `()` | `Track/Graph.cs:1192` | when track group enabled set changes | host-fan-out via group state |
| `GraphDidChangeAvailableGroups` | `Game.Events` | `()` | `Track/Graph.cs:1212` | when track group available set changes | host-fan-out |
| `MapFeatureChangedGraph` | `Game.Events` | `()` | `Game.Progression/MapFeatureManager.cs:189` | when a MapFeature toggles graph state | host-fan-out |
| `SwitchThrownDidChange` | `Game.Events` | `TrackNode Node` | `Track/TrackNode.cs:40` | per switch throw | local (KVO is HostOnly; host fires, clients re-fire on KVO restore) |
| `CTCFeatureChange` | `Game.Events` | `()` | `Track.Signals/CTCMapFeatureTarget.cs:29` | per CTC target change | local |

**Listeners:** `TrainController.cs:391` rebuilds set membership on `GraphDidRebuildCollections`. `TrackRelativePosition.cs:88` rebuilds anchor on `GraphDidChangeAvailableGroups`. `SwitchStand.cs:49` and `MapBuilder.cs:142` listen to `SwitchThrownDidChange`. `CTCSwitchMonitor.cs:51,55` listens to `MapFeatureChangedGraph` and `CTCFeatureChange`.

### 1E — Cars / consists / derail

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `SelectedCarChanged` | `Game.Events` | `()` | `TrainController.cs:171, 1537` | per UI select | local (selection is per-machine) |
| `CarIdentChanged` | `Game.Events` | `string CarId` | `TrainController.cs:2026` | when `Car.id` (reporting marks etc.) changes | host writes KVO; clients see KVO change → re-fire is per-machine but coordinated |
| `CarTrainCrewChanged` | `Game.Events` | `string CarId` | `TrainController.cs:2035, 2039` | per crew add/remove (also for tender) | host writes KVO; coordinated |
| `CarDidDerail` | `Game.Events` | `()` | `Model/Car.cs:2339` | first derailment of a car (subsequent suppressed) | host-only sender (`ApplyDerailmentDelta` runs host-side); clients infer via `_derailment` KVO |
| `CarDefinitionDidChangeEvent` | `Game.Events` | `string CarIdentifier` | `UI.CarEditor/DefinitionEditorModeController.cs:257` | per-edit in the in-game CarEditor | local (editor is host-only sandbox) |

**Listeners:**
- `SelectedCarChanged`: `LocomotiveControlsUIAdapter.cs:91`, `AutoEngineerWaypointControls.cs:91`, anything that mirrors the inspector. **Not the same as `SelectedCar` setter** — `SelectedCarChanged` fires *after* the change.
- `CarIdentChanged`: `LocomotiveControlsUIAdapter.cs:95`, `CarCustomizeWindow.cs:68`, `CarInspector.cs:95`. UI panels rebuild.
- `CarDidDerail`: `ReputationTracker.cs:137` (rep penalty per derailment).
- `CarDefinitionDidChangeEvent`: `TrainController.cs:390`.

### 1F — Operations (passengers, freight, switch lists, timetable)

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `PassengerStopServed` | `Game.Events` | `string Identifier`, `int Offset`, `float CarCondition` | `Model.Ops/PassengerStop.cs:901` | per passenger event served | host (Ops runs host-only) |
| `PassengerStopEdgeMoved` | `Game.Events` | `string From`, `string To` | `Model.Ops/PassengerStop.cs:908, 921` | when passenger stop edge re-routes | host |
| `IndustriesDidChange` | `Game.Events` | `()` | `Game.Progression/Progression.cs:332`, `Game.Progression/MapFeatureManager.cs:201` | when industry set or production rates shift | host |
| `SwitchListDidChange` | `Game.Events` | `()` | `Game.State/StateManager.cs:848` | per switch-list mutation | host-only sender |
| `TimetableDidChange` | `Game.Events` | `()` | `Model.Ops.Timetable/TimetableController.cs:236` | per timetable edit/import | host |
| `TimetableEditorRefresh` | `UI.Timetable.TextTimetableEditor` (private nested) | `()` | `UI.Timetable/TextTimetableEditor.cs:126` | per editor field commit | local UI only |

**Listeners:**
- `PassengerStopServed` / `PassengerStopEdgeMoved`: `ReputationTracker.cs:129, 133`.
- `IndustriesDidChange`: `OpsController.cs:96`.
- `TimetableDidChange`: `AutoEngineerPlanner.cs:279`, `TimetableEditorWindow.cs:50`, `CarInspector.cs:319` `RebuildOnEvent<TimetableDidChange>`, `TimetableWindow.cs:128` `RebuildOnEvent<TimetableDidChange>`.
- `SwitchListDidChange`: `CarInspector.cs:368` `RebuildOnEvent<SwitchListDidChange>`, `StationWindow.cs:126` `RebuildOnEvent<SwitchListDidChange>`.
- `TimetableEditorRefresh`: only the editor itself (`TextTimetableEditor.cs:56, 63`). **Private nested struct** — invisible outside the assembly except via reflection.

### 1G — Multiplayer / players / access

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `AccessLevelDidChange` | `Game.Events` | `AccessLevel OldAccessLevel`, `AccessLevel NewAccessLevel` | `Network.Client/ClientManager.cs:201` | per session access change | local on client; host updates indep. |
| `PlayersDidChange` | `Game.Events` | `()` | `Game.State/PlayersManager.cs:192` | join/leave/rename | host-only sender; clients receive via player-list sync then fire local |
| `TrainCrewsDidChange` | `Game.Events` | `()` | `Game.State/PlayersManager.cs:463` | per train-crew assignment change | host-only sender |
| `PlayerRecordsDidChange` | `Game.Events` | `()` | `Game.State/StateManager.cs:670` | per player-record write | host-only sender |
| `LedgerRequestResponseReceived` | `Game.Events` | `()` | `Game.State/StateManager.cs:640` | per ledger-history fetch response | local on requester (client) |

**Listeners:**
- `AccessLevelDidChange`: `StateManager.OnAccessLevelDidChange` (`StateManager.cs:222`) + UI rebuilds (`EmployeesPanelBuilder.cs:67`).
- `PlayersDidChange`: `EmployeesPanelBuilder.cs:65`.
- `TrainCrewsDidChange`: `BuilderExtensions.cs:104`, `CrewsPanelBuilder.cs:27`.
- `PlayerRecordsDidChange`: `EmployeesPanelBuilder.cs:66`.
- `LedgerRequestResponseReceived`: `FinancePanelBuilder.cs:82` `RebuildOnEvent`.

### 1H — Network-mirrored events (the FireEvent-wrapped four)

`StateManager.SendFireEvent<TEvent>(evt)` (`StateManager.cs:952`) is the **network-broadcast** wrapper. It maps 4 known event types to a `FireEvent` message (HostOnlyAuthorizationRule), broadcasts, and the receiver's `HandleFireEvent` (`StateManager.cs:991`) calls `Messenger.Default.Send(default(EventType))` locally on every machine. This is the **only** vanilla mechanism that fan-outs Messenger events across the network.

| Type | Namespace | Payload | EventCode | Sender wrapper | Direct sender (host-local only) |
|---|---|---|---|---|---|
| `BalanceDidChange` | `Game.Events` | `()` | 0 | `StateManager.SendFireEvent` from `ApplyToBalance` (`StateManager.cs:1289, 1293`) | also via `messenger.Send` in `HandleFireEvent` (`StateManager.cs:997`) |
| `ProgressionStateDidChange` | `Game.Events` | `()` | 1 | `Progression.cs:171, 221, 399` | `StateManager.cs:1000` |
| `RequestRejected` | `Game.Events` | `()` | 2 | `TrainController.cs:2001` | `StateManager.cs:1003` |
| `ReputationUpdated` | `Game.Events` | `()` | 3 | `ReputationTracker.cs:244` | `StateManager.cs:1006` |

**Pattern:** `SendFireEvent` (host-side) `ApplyLocal(new FireEvent(eventCode))` → `IGameMessage` broadcast → on every recipient (host + clients), `HandleFireEvent` decodes the eventCode and calls `Messenger.Default.Send(default(EventType))`. Mods should never `Messenger.Default.Send` these four directly — call `StateManager.Shared.SendFireEvent` so clients see them too.

**Listeners:**
- `BalanceDidChange`: `BalanceDisplay.cs:26`, `GoalsPanelBuilder.cs:134` `RebuildOnEvent`, `FinancePanelBuilder.cs:24` `RebuildOnEvent`.
- `ProgressionStateDidChange`: `Progression.cs:74`, `GoalsPanelBuilder.cs:27` `RebuildOnEvent`.
- `RequestRejected`: `CarCustomizeWindow.cs:152` (one-shot inline subscribe; unregisters after).
- `ReputationUpdated`: `RailroadPanelBuilder.cs:30` `RebuildOnEvent`.

### 1I — Tags / overlays / misc

| Type | Namespace | Payload | Sender (file:line) | Frequency | MP |
|---|---|---|---|---|---|
| `TagVisibilityDidChange` | `Game.Events` | `bool IsVisible` | `UI.Tags/TagController.cs:86` | per visibility toggle | local |
| `FlareAdded` | `Game` (root) | `string Key`, `Track.Location Location` | `Game/FlareManager.cs:136` | per flare placement | local — flare placement is per-machine |
| `Game.State.Ledger.ChangedEvent` | `Game.State.Ledger` (nested public struct) | `()` | `Game.State/Ledger.cs:73, 79` (`Record`, `Clear`) | per ledger write | host-only sender |

**Listeners:** `AutoEngineerWaypointOverlayController.cs:46` (TagVisibilityDidChange). `FlareAdded` and `Ledger.ChangedEvent` have no vanilla `Messenger.Default.Register` consumers in the assembly — they're available for mods.

### 1J — Send/Register call totals

- 59 `Messenger.Default.Send(...)` call sites across 25 files (incl. 15 from `Preferences.cs`, 8 of which are the SoundVolume fan-out).
- `messenger.Send` (lowercased local var) appears 4 times in `StateManager.HandleFireEvent` for the FireEvent decode.
- 50+ `Messenger.Default.Register<T>(this, ...)` call sites.
- 40+ `Messenger.Default.Unregister(this)` call sites.

Per-event Send-site counts (from grep, sorted by frequency):

| Event | Send sites | Notes |
|---|---|---|
| `SoundVolumeChanged` | 8 | fan-out per mixer group |
| `SelectedCarChanged` | 2 | TrainController.cs:171, 1537 |
| `CarTrainCrewChanged` | 2 | self + tender mirror |
| `IndustriesDidChange` | 2 | Progression + MapFeatureManager |
| `PassengerStopEdgeMoved` | 2 | reroute + manual |
| `Ledger.ChangedEvent` | 2 | Record + Clear |
| `PostProcessingPreferenceChanged` | 2 | quality + on/off |
| `UISettingDidChange` | 2 | two UI toggles |
| `ProgressionStateDidChange` | 4 | 3 in Progression + 1 in StateManager FireEvent |
| `RequestRejected` | 2 | TrainController + StateManager FireEvent |
| `ReputationUpdated` | 2 | ReputationTracker + StateManager FireEvent |
| `BalanceDidChange` | 1 | StateManager FireEvent only (`messenger.Send` in `HandleFireEvent`) |
| all others | 1 | single emit site |

---

## Section 2 — `PropertyChange.Control` enum

Source: `Game.Messages/PropertyChange.cs` (full file, 178 lines). The enum is closed at 18 values; `KeyMapping` (static dict) maps each to a stable on-disk key string used in `Car.KeyValueObject`.

### Enum + KeyForControl mapping

| Index | Enum | `KeyForControl` string | Type | Domain |
|---|---|---|---|---|
| 0 | `Throttle` | `"throttle"` | float (0..1) | locos |
| 1 | `Reverser` | `"reverser"` | float (-1..1) | locos |
| 2 | `LocomotiveBrake` | `"locoBrake"` | float (0..1, -0.1 = bail-off) | locos |
| 3 | `TrainBrake` | `"trainBrake"` | float (0..1) | locos |
| 4 | `Horn` | `"horn"` | float (0..1) | locos |
| 5 | `Bell` | `"bell"` | bool | locos |
| 6 | `Handbrake` | `"handbrake"` | bool | all cars |
| 7 | `Bleed` | `"bleed"` | bool (auto-clears via `SetDelayed` 0.5s) | all cars |
| 8 | `Compressor` | `"compressor"` | bool | locos |
| 9 | `CutOut` | `"cutOut"` | bool | locos (train-brake cut-out) |
| 10 | `Idle` | `"idle"` | bool | locos |
| 11 | `Headlight` | `"headlight"` | int (state enum) | all cars with headlight |
| 12 | `BrakeStyle` | `"brakeStyle"` | int | brake stand visual |
| 13 | `Condition` | `"_condition"` (HostOnly) | float (0..1) | wear |
| 14 | `Derailment` | `"_derailment"` (HostOnly) | float (0..1) | derail state |
| 15 | `Mu` | `"mu"` | bool | loco MU consist mode |
| 16 | `CylinderCock` | `"cylCock"` | bool | steam cyl cocks |
| 17 | `Hotbox` | `"hotbox"` | int (0/1; cleared with `null`) | wear hotbox |

**Note `_` prefix:** `_condition` and `_derailment` use the leading-underscore convention that puts them in the HostOnly auth class (per `Car.HostPrefixes`, see [`cars-cargo.md`](cars-cargo.md) and [`wear-durability.md`](wear-durability.md#mp-authority)). Every other key uses the default Crew + train-crew check (see `Car.AuthorizationRequirementForPropertyWrite` at `Car.cs:3112`).

### KVO key namespace

The `KeyForControl` strings share the same namespace as ad-hoc string KVO keys on `Car.KeyValueObject`. Vanilla also writes non-Control keys directly:
- `_f.coupled` / `_r.coupled` / `_f.airConnected` / `_r.airConnected` (HostOnly, see [`couplers.md`](couplers.md#kvo-key-naming))
- `f.anglecock` / `r.anglecock` / `f.cutLever` / `r.cutLever` (Crew)
- `_odosvc`, `_odometer`, `_lastOverhaul`, `_overhaulProg`, `oiled` (HostOnly per `Car.HostPrefixes`)
- `ops.passengerMarker`, `owned`, etc.

`PropertyChange.Control` is **only** the named-control subset. Mods writing custom keys join the same namespace but must avoid collisions and `HostPrefixes` matches.

### Where each Control value is read (canonical sites)

| Control | Read sites |
|---|---|
| `Throttle` | `BaseLocomotive.cs:160, 364`, `LocomotiveControlHelper.cs:18, 22`, `TrainInput.cs:84, 88, 186`, `SimplifiedControls.cs:107`, `ManualControls.cs:103`, `DieselLocomotive.cs:77` (auth check), `SteamLocomotive.cs:317`, `ToggleControlComponentBuilder.cs:42` |
| `Reverser` | `BaseLocomotive.cs:161, 373`, `LocomotiveControlHelper.cs:30, 34`, `TrainInput.cs:121`, `SimplifiedControls.cs:85, 129`, `ManualControls.cs:109`, `DieselLocomotive.cs:92`, `SteamLocomotive.cs:329`, `ToggleControlComponentBuilder.cs:41` |
| `LocomotiveBrake` | `BaseLocomotive.cs:242, 382, 406`, `LocomotiveControlHelper.cs:42, 46, 131`, `TrainInput.cs:125, 176`, `SimplifiedControls.cs:108`, `ManualControls.cs:93`, `ToggleControlComponentBuilder.cs:39` |
| `TrainBrake` | `BaseLocomotive.cs:248, 387`, `LocomotiveControlHelper.cs:54, 58`, `TrainInput.cs:181`, `SimplifiedControls.cs:109`, `ManualControls.cs:98`, `ToggleControlComponentBuilder.cs:40` |
| `Horn` | `BaseLocomotive.cs:259, 291`, `LocomotiveControlHelper.cs:78, 82`, `TrainInput.cs:151`, `SteamLocomotive.cs:128`, `ToggleControlComponentBuilder.cs:43` (`ControlPurpose.Whistle → Horn`) |
| `Bell` | `BaseLocomotive.cs:264, 286`, `LocomotiveControlHelper.cs:66, 70`, `TrainInput.cs:132`, `ToggleControlComponentBuilder.cs:44` |
| `Handbrake` | `Car.cs:1677` (KVO observer), `CarPropertyChanges.cs:10`, `CarInspector.cs:175` |
| `Bleed` | `Car.cs:1685` (KVO observer; sets `SetDelayed(..., null, 0.5f)` to clear), `CarPropertyChanges.cs:26` |
| `Compressor` | `BaseLocomotive.cs:506` (write from `LocomotiveAirSystem`), `CompressorComponentBuilder.cs:32, 44` (visual observer) |
| `CutOut` | `BaseLocomotive.cs:254, 392`, `AutoEngineer.cs:851, 854`, `CarInspector.cs:187, 189, 201`, `ToggleControlComponentBuilder.cs:45` (`ControlPurpose.TrainBrakeCutOut → CutOut`) |
| `Idle` | `BaseLocomotive.cs:59, 63, 454` |
| `Headlight` | `RollingStock.Controls/HeadlightToggleLogic.cs:85, 105`, `RollingStock/HeadlightControl.cs:41` |
| `BrakeStyle` | `RollingStock/BrakeStandController.cs:65` (visual observer; written by `BaseLocomotive` setup) |
| `Condition` | `Car.cs:1696` (observer), `Car.cs:2210` (write via `SetCondition`), `CompanyModeSetup.cs:52` (sandbox reset) |
| `Derailment` | `Car.cs:1701` (observer), `Car.cs:2372` (delayed write), `CompanyModeSetup.cs:51`, `DerailedEffectComponentBuilder.cs:28` |
| `Mu` | `BaseLocomotive.cs:69` (`IsMuEnabled`), `AutoEngineer.cs:848`, `CarInspector.cs:192, 196, 198` |
| `CylinderCock` | `LocomotiveControlHelper.cs:90, 96`, `TrainInput.cs:136`, `Effects/CylinderCockController.cs:85` (observer), `ToggleControlComponentBuilder.cs:38` |
| `Hotbox` | `Car.cs:1713` (observer), `Car.cs:2114` (set to 1 in `CheckForHotbox`), `RepairTrack.cs:270` (clear with `null`), `RollingStock/HotboxEffect.cs:51` |

### Cross-cutting groupings

**Brake controls** (`brakes.md` cross-ref): `LocomotiveBrake`, `TrainBrake`, `Handbrake`, `Bleed`, `CutOut`, `BrakeStyle`. No dynamic brake exists. Bail-off uses `LocomotiveBrake = -0.1f` (`LocomotiveControlHelper.cs:131`) — sentinel value, not a real range.

**Loco-only controls** (require `BaseLocomotive`): `Throttle`, `Reverser`, `LocomotiveBrake`, `TrainBrake`, `Horn`, `Bell`, `Compressor`, `CutOut`, `Idle`, `Mu`, `CylinderCock`, `BrakeStyle`. `CylinderCock` is steam-only in practice but the control exists on the base class.

**All-car controls**: `Handbrake`, `Bleed`, `Headlight`, `Condition`, `Derailment`, `Hotbox`. (`Headlight` requires the `HeadlightControl` MonoBehaviour to be present; otherwise the KVO key is dead.)

**Door state** is **not** in the Control enum. Doors are managed by per-end gear (`EndGearStateKey` — see [`couplers.md`](couplers.md)) for couplers/anglecocks, and by the `DoorPickable` component for actual physical doors (passenger cars). Search for `door` in `RollingStock/` for the door surface — it's not part of `PropertyChange.Control`.

### How values are written

```csharp
// PropertyChange.cs:52, 59, 66 — three constructors, one per value type
new PropertyChange(carId, PropertyChange.Control.Throttle, 0.5f)    // float
new PropertyChange(carId, PropertyChange.Control.CutOut,   true)    // bool
new PropertyChange(carId, PropertyChange.Control.Hotbox,   1)       // int
```

Sent via `StateManager.ApplyLocal(new PropertyChange(...))` — see [`request-messages.md`](request-messages.md). Auth class is `[PropertyChangeAuthorizationRule]` (`Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs`).

Or written directly through the indexer:

```csharp
car.ControlProperties[PropertyChange.Control.Bell] = !car.ControlProperties[PropertyChange.Control.Bell];   // TrainInput.cs:132
car.ControlProperties[PropertyChange.Control.Hotbox] = null;   // RepairTrack.cs:270 — null-write to clear
```

The `ControlProperties` indexer (defined on `Car`) is sugar around `KeyValueObject[KeyForControl(c)]`.

---

## Section 3 — Patch surface

### 3.1 Replacing a Messenger event handler safely

**Cannot replace cleanly.** GalaSoft `Messenger.Default.Register<T>(this, handler)` appends to a list keyed by `(T, this)`. There's no "first/last" priority. Options:

1. **Patch the sender (Harmony prefix).** Best for veto. Example: prefix `Car.ApplyDerailmentDelta` to skip the `Messenger.Default.Send(default(CarDidDerail))` call when your mod manages derailment differently. Caveat: this also suppresses *all* vanilla `CarDidDerail` consumers (e.g., `ReputationTracker`).
2. **Patch the listener method.** Locate the registered delegate and patch its target method (e.g., `ReputationTracker`'s anonymous `<>c__DisplayClass` for `CarDidDerail`). Brittle — anonymous types renumber.
3. **Re-register *and* unregister vanilla.** Call `Messenger.Default.Unregister<T>(originalThis)` then register your own. Requires capturing the vanilla `this` reference, usually only possible by reflection or by patching the registrar.
4. **Wrap with your own event type.** Send your own `MyDerailHandled` after vanilla processes, and let mod-side code subscribe to that instead.

**The `Unregister(this)` pitfall** (from [`ui-vanilla.md`](ui-vanilla.md#gotchas)):

> `Messenger.Default.Unregister(this)` (no type arg) strips ALL handlers registered with that `this` as owner. The vanilla pattern in nearly every consumer is `OnEnable` → `Register<T>(this, ...)` and `OnDisable` → `Unregister(this)`. If your Harmony postfix on `OnEnable` adds a handler with the same `this`, vanilla's `OnDisable` removes yours. **Use a distinct owner key** — a separate `ScriptableObject`, a sentinel object you allocate per-instance, or `Register<T>(myOwnerObject, ...)`.

### 3.2 Adding mod-defined Messenger events

Trivial — Messenger is reflection-typed:

```csharp
public struct MyModEvent { public string Detail; }

// fire (any thread that's safe to allocate on; Messenger is process-local, no MP)
Messenger.Default.Send(new MyModEvent { Detail = "hello" });

// listen
Messenger.Default.Register<MyModEvent>(this, evt => Log.Info(evt.Detail));
// always pair with Unregister; prefer a unique owner key
Messenger.Default.Unregister<MyModEvent>(this);
```

**Caveats:**
- **No MP delivery.** `Messenger.Default` is local. To mirror an event across the network, define an `IGameMessage` (see [`request-messages.md`](request-messages.md)) and have the host `ApplyLocal` it; on each receiver's `Handle*` method, call `Messenger.Default.Send(new MyModEvent(...))`. This is exactly how `FireEvent` mirrors its 4 events. Mods cannot extend the `FireEvent` switch without patching `StateManager.HandleFireEvent`.
- **Handler exceptions are silently swallowed** by GalaSoft. Wrap your handler if you need diagnostics.
- **Order of delivery** between handlers of the same event is registration order; do not rely on it.
- **Struct vs class events** — vanilla uses zero-byte structs (`[StructLayout(Size = 1)]`) for sentinel events. Both work; structs avoid an allocation per Send.

### 3.3 Mod-defined `PropertyChange.Control` values — can the enum be extended?

**No.** `Control` is a closed C# enum compiled into the assembly. The static `KeyMapping` dictionary (`PropertyChange.cs:99-176`) is built once in the type initializer; `KeyForControl` throws `ArgumentOutOfRangeException` on unknown values, and `ControlForKey` throws on unknown strings.

**Workaround patterns:**

1. **Use raw KVO keys.** Mods can write any string key to `Car.KeyValueObject` directly:
   ```csharp
   car.KeyValueObject["mymod.customControl"] = Value.Float(0.5f);
   car.KeyValueObject.Observe("mymod.customControl", v => ...);
   ```
   Auth: by default Crew-allowed (no `_` prefix). Use a `_mymod.` prefix to make it HostOnly. See `Car.AuthorizationRequirementForPropertyWrite` (`Car.cs:3112`) for the prefix-table behavior.

2. **Send a custom `IGameMessage`** instead of `PropertyChange`. Allows full control over auth and routing; see `RequestOilCar` as a template ([`wear-durability.md`](wear-durability.md#mp-authority)) and the broader pattern in [`request-messages.md`](request-messages.md).

3. **Reuse an existing Control value** — only safe if you actually mean the same control. Hijacking `Headlight` or `BrakeStyle` to mean something else will collide with vanilla observers (`HeadlightControl.cs`, `BrakeStandController.cs`).

4. **Patch `KeyForControl` / `ControlForKey`** to add cases — fragile, breaks if multiple mods do it. Better to use option 1.

**Auth class for raw keys:**
- Leading `_` → HostOnly via `Car.HostPrefixes` array (`Car.cs:467-473`: `["_", "ops.passengerMarker", "owned", "oiled", "hotbox"]`).
- Other prefixes → default Crew + train-crew check.
- Anglecock and cut-lever already use the `f.`/`r.` prefix convention without `_` — they're Crew-allowed by design so clients can throw cut-levers.

### 3.4 Patch candidates summary

| Goal | Patch target |
|---|---|
| Listen to all derailments | `Messenger.Default.Register<CarDidDerail>` (subscribe) — but it's payload-free; cross-reference `Car._derailment` KVO + which car derailed via context |
| Veto a specific derailment | Prefix `Car.ApplyDerailmentDelta` (`Car.cs:2318`) |
| Mirror your event to MP | Build an `IGameMessage`, broadcast, fire `Messenger.Default.Send(new MyEvent(...))` in receiver's `Handle*` — cf. `StateManager.HandleFireEvent` |
| Detect car selection | `Messenger.Default.Register<SelectedCarChanged>` (process-local; selection is per-machine) |
| Detect any control input | `KeyValueObject.Observe(PropertyChange.KeyForControl(...))` per-car, OR Harmony patch `LocomotiveControlHelper.ChangeValue` (`Model/LocomotiveControlHelper.cs`) |
| Hook timetable rebuild | `Messenger.Default.Register<TimetableDidChange>` |
| Hook minute/hour/day tick | `Register<TimeMinuteDidChange>` etc. — never use `TimeAdvanced` for non-per-frame work |
| Hook track topology change | `Register<GraphDidRebuildCollections>` |
| Persist before save | `Register<WorldWillSave>` (host-only fires) |
| Restore-time init | `Register<PropertiesDidRestore>` — runs after `_game` KVO is populated |

---

## Cross-references

- KVO `Observe` mechanics (vanilla per-key event surface): [`kvo-patterns.md`](kvo-patterns.md)
- `IGameMessage` / `ApplyLocal` request envelope: [`request-messages.md`](request-messages.md), [`state-manager.md`](state-manager.md)
- `IIntegrationSetEventHandler` (slack/coupler/collision callbacks; not Messenger-based): [`couplers.md`](couplers.md#iintegrationseteventhandler-the-abstraction)
- `RebuildOnEvent<T>` UI panel binding (consumes Messenger events): [`ui-vanilla.md`](ui-vanilla.md#messenger-events-relevant-to-ui)
- `Car.KeyValueObject` key namespace and HostPrefixes: [`cars-cargo.md`](cars-cargo.md), [`wear-durability.md`](wear-durability.md#mp-authority), [`couplers.md`](couplers.md#kvo-key-naming)
- Settings → Messenger fan-out for `*SettingsApplicator`s: [`settings-preferences.md`](settings-preferences.md)
- `TimeWeather` and the `TimeObserver` cascade: [`time-weather.md`](time-weather.md)
