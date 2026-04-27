# Save / Load — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/`, `Railroader-ILSPY/Core/`, `Railroader-ILSPY/Railloader.Injector/`)
**Companion:** [Wear & Durability](wear-durability.md), [Couplers](couplers.md), [`../multiplayer-vanilla-survey.md`](../multiplayer-vanilla-survey.md)

A Railroader save is a single MessagePack-encoded `WorldStore.World` blob written to `<persistentDataPath>/Saves/<name>.shortsave`. The `World` wraps a `Snapshot` (cars, switches, KVO properties, train crews, turntables, switch-lists, map state) plus out-of-snapshot side-channels for player records, car body positions, and the ledger. Loading is a one-shot replay: deserialize → migrate → push everything into KVO and the train controller → fire `PropertiesDidRestore` → run prioritized restore actions → emit `MapDidLoadEvent`. Modders looking to persist data have **two** vanilla-supported routes: register a `KeyValueObject` via `StateManager.RegisterPropertyObject(id, kvo, accessControl)` (rides the snapshot for free, including late-join replication) or use Railloader's per-mod JSON file via `IModdingContext.SaveSettingsData<T>` (out-of-band, not in the save). There is no "extend the save file with a new top-level field" hook.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `WorldStore` (static) | `Game.Persistence/WorldStore.cs` | The save file plumbing — `Save`, `Load`, `InitializeNew`, MessagePack serialize, migrations |
| `WorldStore.World` (struct) | `Game.Persistence/WorldStore.cs:27` | Top-level disk format. Versioned `int Version`, holds a `Snapshot` plus side dictionaries |
| `Snapshot` (struct) | `Game.Messages/Snapshot.cs:9` | Mid-tier container: cars, carSets, properties, switches, players, trainCrews, turntables, switchLists, map |
| `SaveManager` | `Game.State/SaveManager.cs` | `Save`/`Load`/`Autosave` orchestration; **host-only** writes |
| `StateManager.OnPropertiesDidRestore` | `Game.State/StateManager.cs:271` | The post-restore wiring spine — observers attached to KVO settings |
| `StateManager.PopulateSnapshotForSave` | `Game.State/StateManager.cs:1123` | Calls into PropertyObjectManager + PlayersManager + OpsController + Ledger |
| `StateManager.PopulateFromRemoteSnapshot` | `Game.State/StateManager.cs:1131` | Client-side late-join restore. Same logical spine as load-from-disk. |
| `RestoreNotifier.RegisterForRestore(priority, observer, action)` | `Game.State/RestoreNotifier.cs:90` | The **only** prioritized hook for "run after restore" |
| `Game.Events.PropertiesDidRestore` | `Game.Events/PropertiesDidRestore.cs` | Messenger event fired after every restore (load + late-join) |
| `Game.Events.WorldWillSave` | `Game.Events/WorldWillSave.cs` | Messenger event fired before snapshot capture; last call to flush state into KVO |
| `IModdingContext.SaveSettingsData<T>` / `LoadSettingsData<T>` | `Railloader.Interchange/Railloader/IModdingContext.cs` | Per-mod JSON sidecar in `Mods/Railloader/ModSettings/<id>.json` |

---

## Save file format

### Wire format

- **Encoding:** MessagePack (`MessagePack.MessagePackSerializer.Serialize<World>(value)`). Binary, not JSON, not Unity serialized.
- **Extension:** `.shortsave` (`WorldStore.cs:46` `private const string Extension = ".shortsave";`).
- **Path:** `Path.Combine(Application.persistentDataPath, "Saves")`. On Windows: `%USERPROFILE%\AppData\LocalLow\Giraffe Lab\Railroader\Saves\<name>.shortsave`.
- **No compression layer**; MessagePack's own packing is the only size reduction.

### `WorldStore.World` schema

```csharp
[MessagePackObject(false)]
public struct World {                                              // WorldStore.cs:27
    [Key("version")]          public int Version;
    [Key("snapshot")]         public Snapshot Snapshot;
    [Key("playerStates")]     public Dictionary<string, PlayerRecord> PlayerStates;
    [Key("carBodyPositions")] public Dictionary<string, Vector3[]> CarBodyPositions;
    [Key("ledgerEntries")]    public List<SerializableLedgerEntry> LedgerEntries { get; set; }
}
```

Keys are **string**, not int (`MessagePackObject(false)` + `[Key("string")]`). String keys cost more bytes but tolerate field reordering across versions.

`World.Version` is currently `1` for new saves (`WorldStore.cs:188`). `InitializeNew` writes `Version = 0`. There's no `World`-level migration today; `Version` is a placeholder. **All current migration runs at the `Snapshot` level** — see [Migration](#migration).

### `Snapshot` schema

```csharp
[MessagePackObject(false)]
public struct Snapshot {                                           // Game.Messages/Snapshot.cs:9
    [Key("version")]          int Version;
    [Key("players")]          Dictionary<string, Snapshot.Player> players;
    [Key("cars")]             Dictionary<string, Snapshot.Car> Cars;
    [Key("carSets")]          Dictionary<uint, Snapshot.CarSet> CarSets;
    [Key("carAir")]           List<BatchCarAirUpdate> CarAir;
    [Key("thrownSwitchIds")]  HashSet<string> thrownSwitchIds;
    [Key("properties")]       Dictionary<string, Dictionary<string, IPropertyValue>> Properties;
    [Key("trainCrews")]       Dictionary<string, Snapshot.TrainCrew> TrainCrews;
    [Key("map")]              Snapshot.Map map;
    [Key("switchLists")]      Dictionary<string, SwitchList> SwitchLists;
    [Key("turntables")]       Dictionary<string, Snapshot.TurntableState> Turntables;
}
```

`Snapshot.Version` (note: separate from `World.Version`) is also `1` for new saves (`HostManager.cs:678` sets it on snapshot send too).

`Snapshot.Properties` is the **single bag for every registered KVO object** — including `_game` (settings), `_progression`, `mapFeatures`, `players`, every `Car`, every industry, every interlocking, plus anything mod code registered via `StateManager.RegisterPropertyObject`.

### `IPropertyValue` discriminated union

The on-disk type for KVO values is `IPropertyValue` (in `Game.Messages`). Concrete types:

| Type | Holds | KeyValue.Runtime equivalent |
|---|---|---|
| `NullPropertyValue` | nothing | `Value.Null()` |
| `BoolPropertyValue` | bool | `Value.Bool` |
| `IntPropertyValue` | int | `Value.Int` |
| `FloatPropertyValue` | float | `Value.Float` |
| `StringPropertyValue` | string | `Value.String` |
| `ArrayPropertyValue` | `List<IPropertyValue>` | `Value.Array` |
| `DictionaryPropertyValue` | `Dictionary<string, IPropertyValue>` | `Value.Dictionary` |

Conversion both ways via `PropertyValueConverter.RuntimeToSnapshot` / `SnapshotToRuntime` (`Game.Messages/PropertyValueConverter.cs`). Snapshot↔Runtime is the boundary every save/load value crosses.

**There is no `[CanBeNull]` reference type.** Anything that can be "absent" must be a `NullPropertyValue`. Code reading values uses `Value.IntValueOrDefault(default)`, `Value.FloatValueOrDefault(default)`, `Value.BoolValueOrDefault(default)`, etc., and `Value.IsNull` — see GameStorage for the canonical pattern.

### Side-channel data

Three data sets live **outside** `Snapshot`:

| Field | Type | Purpose |
|---|---|---|
| `PlayerStates` | `Dictionary<string, PlayerRecord>` | Per-Steam-ID record: name, position, access level, last-connected — built host-side in `HostManager`. Keyed by Steam ID (string). Pruned by `WorldStore.Migrate(Dictionary<string, PlayerRecord>)` to only keep numeric keys. |
| `CarBodyPositions` | `Dictionary<string, Vector3[]>` | Per-car articulated-body positions (multi-truck cars). Used by `HostManager.SnapshotCarBodyPositions` to seed body kinematics on join/load. |
| `LedgerEntries` | `List<SerializableLedgerEntry>` | Financial ledger — every `Ledger.Record` call. Restored via `Ledger.Load`. |

The ledger is a *post-snapshot* concern. It's loaded last in `WorldStore.ApplyWorld` and does **not** flow over the multiplayer snapshot — clients request it explicitly via `LedgerRequest` (see `StateManager.cs:598-648`).

### Versioning posture

Three independent version numbers:

| Field | Default | Bumped when |
|---|---|---|
| `World.Version` | `0` (initialize) / `1` (save) | Never bumped today; placeholder |
| `Snapshot.Version` | `1` (save) / `0` (initialize/empty) | Set to `1` in `Save` and on multiplayer `SendSnapshotTo` whenever `Cars.Count > 0` |
| (KVO object) per-key | n/a | Schemaless; readers use `…OrDefault` |

There is **no formal save migration framework**. `WorldStore.Migrate(Snapshot)` is a single hand-coded function that does whatever ad-hoc patching the current version needs. See [Migration](#migration) for what's there.

### Wire-format key names (load-bearing typos and oddities)

These string keys appear in saved property dictionaries. Many live in `_game`. Treat as on-disk constants — do not "fix" the typos or mods that touch the same setting will write to a different key.

| Key | Object | Source | Notes |
|---|---|---|---|
| `wearFeatre` | `_game` | `GameStorage.cs:64` | Missing 'u'. Read+write via `GameStorage.WearFeature`. See [wear-durability › toggle spine](wear-durability.md#toggle-spine-how-wearfeature-propagates) |
| `oilPrevMaintFeature` | `_game` | `GameStorage.cs:66` | Verbose: "oil preventive maintenance feature." Read+write via `GameStorage.OilFeature` |
| `overhaulMi` | `_game` | `GameStorage.cs:68` | Truncated "overhaulMiles" |
| `wearMult` | `_game` | `GameStorage.cs:70` | Truncated "wearMultiplier" |
| `oilUseMult` | `_game` | `GameStorage.cs:72` | Truncated "oilUseMultiplier" |
| `aiPassStopEnable` | `_game` | `GameStorage.cs:58` | Truncated "AIPassengerStopEnable" |
| `aiPassStopMinStopDur` | `_game` | `GameStorage.cs:60` | Truncated "AIPassengerStopMinimumStopDuration" |
| `interchangeServeHour` | `_game` | `GameStorage.cs:48` | "Hour" not "Time" |
| `interchangeShuffle` | `_game` | `GameStorage.cs:50` | Maps to `InterchangeShuffle` int (0/1/3/5) |
| `mode` | `_game` | `GameStorage.cs:16` | `int` cast of `GameMode` enum (Sandbox=0, Company=1) |
| `setupId` | `_game` | `GameStorage.cs:96` | The `SetupDescriptor.identifier` for this save |
| `passwordHash` | `_game` | `GameStorage.cs:22` | New-player password hash (`StateManagerPasswordExtensions`) |
| `loanNextInterestDate` | `_game` | `GameStorage.cs:42` | `float` (`GameDateTime.TotalSeconds`); `IsNull` when no loan |
| `loanNextInterestOffset` | `_game` | `GameStorage.cs:44` | First-time loan-next-interest jitter |
| `unbilledRunDuration` | `_game` | `GameStorage.cs:46` | Float seconds, accumulator for AI-engineer wages |
| `progression` | `_progression` | `WorldStore.cs:110` (migration), `StateManager.cs:403` (preset) | String like `"ewh"` |
| `_f.coupled`, `_r.coupled`, `_f.airConnected`, `_r.airConnected` | per-car | `Car.cs:2750+` | HostOnly. See [Couplers › KVO key naming](couplers.md#kvo-key-naming) |
| `f.cutLever`, `r.cutLever`, `f.anglecock`, `r.anglecock` | per-car | `Car.cs:2750+` | Crew-writable |
| `_condition`, `_derailment`, `_odometer`, `_odosvc`, `_lastOverhaul`, `_overhaulProg`, `oiled`, `hotbox` | per-car | `Car.cs:1690+` | Wear/oil state; HostOnly. See [wear-durability › per-car KVO](wear-durability.md#kvo-backed-properties-hostonly-prefix-_-or-oiledhotbox) |
| `owned` | per-car | `StateManager.cs:434` | Bool; player-owned car flag |
| `subIdentifier + "-rate"` | per-industry | `RepairTrack` | Repair shop pay-rate state |

Underscore-prefixed keys on `_game`, `players`, and `Car` are **HostOnly** by default convention; unprefixed keys go through per-object `IPropertyAccessControlDelegate` resolvers (`GameStorage.AuthorizationRequirementForPropertyWrite`, `Car.AuthorizationRequirementForPropertyWrite`, etc.). The leading-underscore convention is enforced by *the access-control delegate*, not by the serializer — a mod that writes a non-`_`-prefixed key to `_game` will get the default `HostOnly` (`GameStorage.cs:607`). See `HostManager.HostOnlyKeyPrefix = "_"` (`HostManager.cs:153`) for the documented constant.

---

## Load order (the spine)

Two entry paths, same downstream pipeline.

### A. From-disk load

```
GlobalGameManager.Launch                                    (UI.Menu/GlobalGameManager.cs:82)
   │  Messenger.Send(MapWillLoadEvent)
   ▼
StateManager.OnMapWillLoad                                  (StateManager.cs:243)
   │  TimeWeather.Reset()
   │  RestoreNotifier.Initialize()       ← finds the singleton component
   │  PrepareGameKeyValueObject()        ← creates _game KVO + new GameStorage
   │  PreparePlayerProperties()          ← creates "players" KVO + manager
   ▼
StateManager.ApplyGameSetup(gameSetup)                      (StateManager.cs:257)
   │  if (IsHost):
   │    SaveManager.SetSaveNameForLaterLoading(gameSetup?.SaveName)
   │    SaveManager.LoadFromSaveIfNeededOrInitialize()
   │      └── if file exists: WorldStore.Load(saveName)
   │          else:           WorldStore.InitializeNew()
   │    if (NewGameSetup.HasValue): ApplyNewGameSetup(value)
   ▼
WorldStore.Load                                             (WorldStore.cs:71)
   │  MessagePackSerializer.Deserialize<World>(File.ReadAllBytes(path))
   │  ApplyWorld(world)
   ▼
WorldStore.ApplyWorld                                       (WorldStore.cs:92)
   │  Migrate(snapshot)
   │  Migrate(playerStates)
   │  HostManager.LoadSnapshot(snapshot, playerStates, carBodyPositions)
   │     │  _snapshot = snapshot
   │     │  _playerRecords = …
   │     │  StateManager.ApplySnapshotMap(map)              ← TimeWeather.Now
   │     │  SnapshotCarBodyPositions = carBodyPositions
   │     │  SendToAll(SnapshotEnvelope(snapshot))           ← clients pick up here
   │     │  _hasLoadedSnapshot = true
   │     │  HandlePendingRequestActive()                    ← any waiting clients flip Active
   │     ▼  (host has not yet hydrated its OWN object graph; that's TrainController/etc.)
   │  StateManager.Shared.Ledger.Load(world.LedgerEntries)
```

The host then needs to hydrate *its own* world from the snapshot. That happens via `StateManager.PopulateFromRemoteSnapshot`, which is **not** explicitly called from `LoadSnapshot` — it runs separately on the host. (Look for the call site near `MapDidLoadEvent` plumbing — host paths and client paths converge in `PopulateFromRemoteSnapshot`.)

### B. Client late-join

```
Network arrives → Multiplayer.Client receives SnapshotEnvelope
   ▼
StateManager.PopulateFromRemoteSnapshot(snapshot)           (StateManager.cs:1131)
   │  using (TransactionScope())                            ← batch all writes
   │  HandleSnapshotMapFeatures(snapshot.Properties)
   │  TrainController.HandleSnapshotSwitches(thrownSwitchIds)
   │  TrainController.HandleSnapshotTurntables(Turntables)
   │  TrainController.HandleSnapshotCars(Version, Cars, CarSets, CarAir, Properties)
   │  PlayersManager.RestoreFromSnapshot(players, TrainCrews)
   │  ApplySetupPropertyPresets(Properties)                 ← from _gameSetupPropertyPresets queue
   │  RestoreProperties(Properties)                         ← PropertyObjectManager.RestoreProperties
   │     │  origin = (IsHost ? Local : Remote)
   │     │  for each (objectId, dict): registered KVO → ResetData(dict, origin)
   │     │  unregistered objectIds → stored in a KeyValueStorage with HostOnly delegate
   │  ApplySnapshotMap(map)                                 ← TimeWeather.Now
   │  RestoreNotifier.Shared.NotifyDidRestore()             ← prioritized actions, then PropertiesDidRestore event
   │  TrainController.PostRestoreProperties()
   │  RestoreSwitchLists(SwitchLists)
   │  OpsController.PostRestoreProperties()
   ▼
Messenger.Send(PropertiesDidRestore)                        ← inside NotifyDidRestore
   │
   ├── StateManager.OnPropertiesDidRestore                  (StateManager.cs:271)
   │     ─ wires _storage observers (WearFeature, OilFeature, OverhaulMiles, …)
   │     ─ if (!Sandbox): adds LoanManager + Configure(_storage)
   │     ─ if (IsHost): Ledger.ReconcileIfNeeded(Balance)
   │     ─ if (HasTutorial): TutorialManager.ShowIfAppropriateForLaunch()
   │
   └── (any other Messenger subscriber to PropertiesDidRestore)
```

### Restore order summary

1. Map features (the `mapFeatures` KVO, special-cased)
2. Switches → turntables → cars (snapshot.Cars + CarSets + CarAir + per-car properties)
3. Players & TrainCrews
4. Setup-time property presets (queued during `ApplyNewGameSetup` for new games)
5. **All other registered KVO objects** (`PropertyObjectManager.RestoreProperties` — order is dictionary-iteration, not deterministic)
6. Map state (time of day, weather)
7. `RestoreNotifier.NotifyDidRestore` runs prioritized actions, then sends `PropertiesDidRestore`
8. `TrainController.PostRestoreProperties` (re-coupling, set assembly, etc.)
9. SwitchLists
10. `OpsController.PostRestoreProperties`

Cars are hydrated **before** `_game`/settings observers are wired. That means the wear/oil features static booleans (`Car.WearFeature`, `Car.OilFeature`) hold their last value *until* `OnPropertiesDidRestore` runs near the end. See [wear-durability › init order](wear-durability.md#init-order) — the same gotcha applies here.

### `RestoreNotifier` priorities

```csharp
public void RegisterForRestore(int priority, object observer, Action action)   // RestoreNotifier.cs:90
```

Higher priority runs **first** (`if (priority > priority2)` insert ahead). Pending actions execute in `NotifyPending()` from `NotifyDidRestore` (and on `LateUpdate` if more were registered after). Use this when you need to run *during* the restore phase — patches and KVO observers run at default fan-out timing; `RegisterForRestore` lets you order against vanilla restorers.

There's no published priority constant table; today only TrainController/OpsController-style internals seem to use it. For a mod, picking `priority = 0` puts you at the tail of the queue.

---

## Save flow

```
SaveManager.Save(saveName, saveTag)                         (SaveManager.cs:68)
   │  StateManager.DebugAssertIsHost()  ← noop in shipped build, presumably real assertion in editor
   │  saveName = FinalizeSaveName(saveName, saveTag)
   │  WorldStore.Save(saveName)
   │     ├── Messenger.Send(WorldWillSave)                  ← LAST CHANCE to flush state
   │     ├── World value = CaptureWorldSnapshot()
   │     │     ├── Snapshot.Empty() + map(now, defaultSpawn)
   │     │     ├── TrainController.PopulateSnapshotForSave(ref snap, out carBodyPositions, SnapshotOption.None)
   │     │     ├── StateManager.PopulateSnapshotForSave(ref snap, ref playerPersistedStates, ref ledgerEntries)
   │     │     │     ├── PropertyObjectManager.PopulateSnapshotForSave(ref snap)
   │     │     │     │     └── for each (id, kvo): if !snap.Properties.ContainsKey(id): snap.Properties[id] = kvo.SnapshotValues()
   │     │     │     ├── PlayersManager.PopulateSnapshotForSave(ref snap, ref playerPersistedStates)
   │     │     │     ├── OpsController.PopulateSnapshotForSave(ref snap)
   │     │     │     └── Ledger.PopulateForSave(ledgerEntries)
   │     │     └── new World { Version=1, Snapshot, PlayerStates, LedgerEntries, CarBodyPositions }
   │     ├── byte[] = MessagePackSerializer.Serialize(value)
   │     ├── Directory.CreateDirectory if needed
   │     └── File.WriteAllBytes(path, bytes)
   │  RestartAutosave()
```

### Crucial: `WorldWillSave` is the only pre-save hook

If your mod tracks state that lives outside a registered KVO object, you must subscribe to `WorldWillSave` and flush it into something the snapshot will pick up. Vanilla example: `AutoEngineerPlanner.OnEnable` registers `WorldWillSave` to copy `_manualStopDistance` into its persistence helper (`Model.AI/AutoEngineerPlanner.cs:275`).

```csharp
Messenger.Default.Register<WorldWillSave>(this, _ => _persistence.ManualStopDistance = _manualStopDistance);
```

After `WorldWillSave`, `CaptureWorldSnapshot` runs immediately — there is no async window.

### `PropertyObjectManager.PopulateSnapshotForSave` — the "ride the snapshot" hook

```csharp
public void PopulateSnapshotForSave(ref Snapshot snapshot)         // PropertyObjectManager.cs:69
{
    foreach (var (key, record) in _records) {
        if (!snapshot.Properties.ContainsKey(key))
            snapshot.Properties[key] = record.Object.SnapshotValues();
    }
}
```

This is the entire mod-extension surface for save data: register an `IKeyValueObject` via `StateManager.RegisterPropertyObject(id, kvo, accessControl)` and your data appears in `Snapshot.Properties[id]` automatically. Late-join replication is also automatic (the snapshot is broadcast). The catch: keys must be `string`, values must be `Value` (the `KeyValue.Runtime` type, mapping to `IPropertyValue`).

The `if (!Properties.ContainsKey(key))` guard means **`TrainController.PopulateSnapshotForSave` runs first and gets first dibs on object IDs**. In practice cars use ID strings, settings use `_game`, etc., so collisions don't happen — but if you register `id = "cars"` you'll silently lose data.

### Autosave

```csharp
private IEnumerator AutosaveCoroutine()                            // SaveManager.cs:122
{
    WaitForSeconds wait = new WaitForSeconds(300f);  // 5 minutes
    while (true) { yield return wait; Autosave(); }
}
```

Hard-coded 5-minute interval. Restarted on every `Save`/`Load`/`SetSaveNameForLaterLoading` so the timer resets on any explicit save.

```csharp
public static (string saveName, string tag) MakeAutosaveTag(...)   // Core/Core/AutosaveLogic.cs:11
{
    var (baseName, num) = ParseSaveName(saveName);
    foreach (item2 in orderedSaveNames.Where(sn => baseName matches)) {
        if (item2 has _autoN suffix) return (baseName, $"auto{NextIndex(N)}");
    }
    int num3 = num.HasValue ? NextIndex(num.Value) : 1;
    return (baseName, $"auto{num3}");
}

private static int NextIndex(int index) => (index - 1 + 1) % 1 + 1;   // !! always returns 1
```

`AutosaveCount = 1`. `NextIndex` is `((index-1+1) % 1) + 1 == 1` always. **Only one autosave is kept** (`<base>_auto1.shortsave`); each autosave overwrites the previous. The arithmetic is dead-code left over from a multi-slot design.

---

## Migration

`WorldStore.Migrate(Snapshot)` (`WorldStore.cs:101`) is the entire migration surface:

```csharp
private static void Migrate(Snapshot snapshot)
{
    if (!snapshot.Properties.TryGetValue("_game", out var value)) return;
    Value value2 = value.ToRuntime();

    // Migration 1: Company-mode (mode==1) sets default progression to "ewh" if _progression missing
    if (value2["mode"].IntValue == 1 && snapshot.Properties.TryGetValue("_progression", out var value3))
        value3["progression"] = new StringPropertyValue("ewh");

    // Migration 2: If oilPrevMaintFeature was disabled, strip per-car oil/hotbox state on load
    if (!value2["oilPrevMaintFeature"].BoolValueOrDefault(true)) {
        foreach (string key2 in snapshot.Cars.Keys) {
            if (snapshot.Properties.TryGetValue(key2, out var value4)) {
                value4.Remove("hotbox");
                value4.Remove("oiled");
            }
        }
    }

    // Migration 3: Renamed prototype IDs
    Dictionary<string, string> renames = new {
        ["hm-hopper03"]  = "hmr-hopper03",
        ["ls-260-g26"]   = "ls-260-g25",
        ["lt-260-g26"]   = "lt-260-g25",
    };
    foreach (var (key, car) in snapshot.Cars.ToList()) {
        if (renames.TryGetValue(car.prototypeId, out var newId)) {
            var copy = car; copy.prototypeId = newId; snapshot.Cars[key] = copy;
        }
    }
}
```

Pattern: hand-coded conditionals, no version gate. Each migration is unconditionally re-applied on every load. The "is this a new condition we need to fix?" detection is implicit in each check (e.g., the prototype-rename check is no-op when no cars match).

`Migrate(Dictionary<string, PlayerRecord>)` strips entries whose key is not a parseable `ulong` Steam ID. This is the only `playerStates` migration.

**No `World.Version`-based dispatch.** If a future format break needs version gating, the framework would have to be added.

### Save corruption handling

```csharp
public void Load(string saveName) {                                // SaveManager.cs:51
    try { WorldStore.Load(saveName); }
    catch (Exception exception) {
        Debug.LogException(exception);
        Log.Error(exception, "Error loading save");
        ModalAlertController.PresentOkay("Error loading save", "An error occurred while loading " + saveName + ".");
        throw;
    }
}
```

Modal + rethrow. There's no recovery path, no backup, no "try the previous autosave." Failed loads bubble up to `GlobalGameManager.Launch`'s `catch` which calls `ReturnToMainMenu`. The corrupt save remains on disk untouched.

Save errors:

```csharp
public void Save(...) {                                            // SaveManager.cs:68
    try { WorldStore.Save(saveName); }
    catch (Exception exception) {
        // logs + modal "Error saving game", but does NOT rethrow
    }
}
```

Save failures swallow the exception after the modal — the game keeps running. Half-written `.shortsave` files may exist on disk; there's no atomic write (no temp-file + rename). `File.WriteAllBytes` will truncate-then-write so an interrupted save can leave a 0-byte or partial file that will then fail to load.

---

## Sandbox vs scenario divergence

`StateManager.GameMode` (backed by `_game["mode"]`) gates a few behaviors:

| Behavior | Sandbox | Scenario / Company |
|---|---|---|
| `LoanManager` instantiation | **Skipped** (`StateManager.cs:312`) | Created and `Configure(_storage)` |
| `ShouldShowMapFeatures` (Company UI tab) | Host-only true | False (hidden from UI) |
| `CanAfford(int)` | Always true (`StateManager.cs:1258`) | `Balance >= expense` |
| `/repair` console command | Allowed | Disabled (sandbox-only) |
| Setup descriptor | Optional (no preset progression unless given) | Required for scenario; sets `_progression["progression"]` |
| `CompanyModeSetup.Setup` coroutine | Optional | Runs at new game when `Cars.Count==0 && Balance==0` |
| Tutorial (`HasTutorial`) | Yes if `setupDescriptor.showTutorial` | Same condition |

The save format is **identical** — same `World` struct, same MessagePack schema. The mode bit lives in `_game.mode` (0=Sandbox, 1=Company). A scenario save loaded "as sandbox" by mutating `_game.mode` would skip the `LoanManager` wiring and gate the per-tick economy checks but otherwise replay normally. Reverse direction is risky (no setup descriptor, no progression preset, but those have null-safe paths).

`Migrate(snapshot)` only injects the default progression `"ewh"` if `mode == 1`, so loading a sandbox-mode save into a future build that requires progression in scenario mode wouldn't auto-fix; it requires the saved `mode==1` to fire.

---

## Multiplayer interaction

### Host vs client save participation

| Operation | Host | Client |
|---|---|---|
| Save to disk | Yes | **No** — `StateManager.DebugAssertIsHost()` (no-op in shipped build but `SaveManager.Save` is host-coded) |
| Receive snapshot on join | n/a | `Multiplayer.Client` receives `SnapshotEnvelope`, calls `PopulateFromRemoteSnapshot` |
| Build snapshot | Yes (in `WorldStore.CaptureWorldSnapshot`) | n/a |
| Apply snapshot | Yes (`HostManager.LoadSnapshot` then `PopulateFromRemoteSnapshot` host-side) | Yes (`PopulateFromRemoteSnapshot`) |
| `Autosave` coroutine | Runs (host-only by virtue of `_saveName` being host-set) | Coroutine runs but `Autosave` early-returns `if (_saveName != null)` — `_saveName` is never set on client |

**Clients cannot save.** There's no client→host "please save" message. The host's autosave is the only persistence path; if the host disconnects mid-session without manually saving, only the latest `_auto1` exists.

### Snapshot vs PropertyChange vs RPC

After initial `LoadSnapshot`/`PopulateFromRemoteSnapshot`, all subsequent state delta flows via `PropertyChange` messages (KVO writes broadcast through `StateManager.PropagateSetValueLocal` → `Multiplayer.Client.Send(new PropertyChange(...))` or `HostManager.SetSnapshotProperty` host-side). The host's `_snapshot` is mutated by `HostManager.RecordState` (`HostManager.cs:823+`) so that late-joiners get the current state, not the on-disk state.

Cross-link: the snapshot/PropertyChange split is covered in [`../multiplayer-vanilla-survey.md`](../multiplayer-vanilla-survey.md) — see the "Snapshot is the consistency mechanism" section. `StateManager.RegisterPropertyObject` is the one-stop hook for both save-disk persistence and MP late-join replication.

### Property origin discipline

`SetValueOrigin` (`Local`/`Remote`) is the field that prevents loops:
- Snapshot/restore writes use `Local` on host, `Remote` on client (`StateManager.cs:1235`).
- Client→server writes go `Local` on the client; the server rebroadcasts and clients see `Remote`.
- Observers should typically respect origin to avoid double-applying changes (see `KeyValueObject.Observe` semantics — origin is exposed via the `Value` callback or by tracking separately).

### ResetData on re-register

If a property object is registered while a previous registration's data exists in `_records[id]`, `RegisterPropertyObject` calls `keyValueObject.ResetData(value.Object.Dictionary, restoreOrigin)` first (`PropertyObjectManager.cs:25-32`). So **the snapshot can populate a `_records` slot for an ID that hasn't been registered yet** (via `RestoreProperties`'s "unregistered → store anyway" branch at line 87) and the eventual registration will pick up the saved data. This is why mods can register their KVO at any point during init without missing snapshot data.

---

## Mod-extension hooks

Vanilla offers two routes for mods to add persistent state.

### Route 1 — KVO objects (in-snapshot)

```csharp
var kvo = gameObject.AddComponent<KeyValueObject>();
StateManager.Shared.RegisterPropertyObject(
    "myMod.state",                              // id (snake-case prefix recommended to avoid collisions)
    kvo,
    AuthorizationRequirement.HostOnly);         // or .MinimumLevelTrainmaster, etc.
kvo["mySetting"] = Value.Bool(true);
```

- Data lives in the save (`Snapshot.Properties["myMod.state"]`).
- Late-joiners get it for free.
- HostOnly by default, but can opt into other auth tiers via `IPropertyAccessControlDelegate`.
- Survives across all save/load and snapshot transmissions.
- **Cost:** values must round-trip through `Value` / `IPropertyValue`. Complex types must be hand-encoded as `Value.Dictionary` / `Value.Array`.
- **Warning:** if your mod is uninstalled and the save is loaded, the unrecognized properties are kept in a `KeyValueStorage` placeholder (`PropertyObjectManager.cs:87`) and re-saved on the next write. So uninstalling a mod does not strip its data; it persists indefinitely.

### Route 2 — Railloader sidecar JSON (out-of-band)

```csharp
public class MySettings { public bool flag; public int value; }

// In your IPlugin Startup:
var settings = ctx.LoadSettingsData<MySettings>("myMod") ?? new MySettings();
// ... mutate settings ...
ctx.SaveSettingsData("myMod", settings);
```

- File: `Mods/Railloader/ModSettings/myMod.json` (`Railloader.ModLoading/ModdingContext.cs:39`).
- JSON via `JsonConvert.Serialize/Deserialize`.
- Per-install, not per-save — settings carry across all save files for a player.
- Not affected by joining as a client (this is local file I/O on each machine).
- Not affected by host/client topology — both host and client read/write their own copy.

This is appropriate for player-wide preferences (UI choices, key bindings, debug toggles). It is **not** appropriate for game state (per-save data).

### What is *not* available

- No "register a top-level field on `World`" hook. The struct is fixed.
- No save-format version negotiation between mod and game.
- No per-mod migration callback. Mods must handle their own migrations within their `IPropertyValue` payload.
- No "save this even when not connected to a snapshot" fallback. If your KVO object isn't registered when `CaptureWorldSnapshot` runs, your data is gone.

---

## `Game.Persistence.WorldStore` (static)

```csharp
public static bool Exists(string saveName)                         // 50
public static void Save(string saveName)                           // 55
public static void Load(string saveName)                           // 71
public static void InitializeNew()                                 // 80
public static List<SaveInfo> FindSaveInfos()                       // 157
public static void Clear(string saveName)                          // 196
public static DateTime? TimestampForSave(string saveName)          // 205
public static string NewGameName()                                 // 215
```

Static, no instance state. `SavePath = Application.persistentDataPath/Saves`. All public entry points run on the calling thread (no async).

### Patch candidates

| Method | Why patch |
|---|---|
| `WorldStore.Save(string)` | Wrap to add atomic-write (write to `.tmp`, rename) or to add backup rotation. |
| `WorldStore.Load(string)` | Wrap to fall back to `.bak` if deserialize throws. |
| `WorldStore.Migrate(Snapshot)` | Add your own snapshot-level migrations (e.g., rename a mod-owned property key). |
| `WorldStore.CaptureWorldSnapshot()` | Postfix to mutate the `World` struct before serialization (e.g., add a side-channel encoded into `Snapshot.Properties["myMod.sidechannel"]`). |
| `WorldStore.ApplyWorld(World)` | Postfix to re-apply mod-side state from the loaded `World`. |
| `WorldStore.PathForSaveName(string)` | Redirect saves to a different directory (e.g., per-profile saves). |

### Gotchas

- `File.WriteAllBytes` is not atomic. Power loss mid-save corrupts the file.
- No checksum / digest. If MessagePack deserialization succeeds on a corrupted-but-valid byte stream, you get garbage state with no warning.
- `_saveName` on `SaveManager` is set only when `SetSaveNameForLaterLoading` is called. In autosave-after-`Save` paths, `Autosave()` early-returns when `_saveName == null`. New games (no save loaded) will not autosave until the user does an explicit save first.
- `FinalizeSaveName` strips invalid filename chars by replacing each with `_` (`SaveManager.cs:108`). Multiple saves with the same sanitized name overwrite silently.

---

## `Game.State.SaveManager` (MonoBehaviour)

```csharp
public static SaveManager Shared => StateManager.Shared.SaveManager;       // 22
public void SetSaveNameForLaterLoading(string saveName)                    // 32
public void LoadFromSaveIfNeededOrInitialize()                             // 39
public void Load(string saveName)                                          // 51
public void Save(string saveName, string saveTag = null)                   // 68
public void GetLastSaveTimes(out DateTime?, out DateTime?)                 // 144
public void WillUnloadMap()                                                // 24
```

Held as a sibling component on the StateManager GameObject (`StateManager.cs:206` `GetComponent<SaveManager>()`).

`StateManager.Save(saveName = null)` (`StateManager.cs:1118`) is the static convenience used by UI / console.

### Patch candidates

| Method | Why patch |
|---|---|
| `SaveManager.Save(string, string)` | Veto/intercept saves; trigger pre-save hooks; reject saves while a critical operation is in flight. |
| `SaveManager.Load(string)` | Same for loads. |
| `SaveManager.Autosave` (private) | Change autosave cadence (currently fixed 5 min) or count (currently 1 slot). |
| `SaveManager.AutosaveCoroutine` | Replace the entire autosave loop. |
| `AutosaveLogic.MakeAutosaveTag` (in `Core/Core/AutosaveLogic.cs`) | Change the autosave tag scheme — the `% 1` arithmetic limits to 1 slot; bump to support N slots. |

---

## `Game.State.RestoreNotifier` (MonoBehaviour)

The prioritized "after restore" hook. Singleton via `RestoreNotifier.Shared`, located via `FindObjectOfType` in `Initialize()` — there must be one in the loaded scene (`Initialize` is called from `OnMapWillLoad`).

```csharp
public bool HasRestored { get; }                                   // true once NotifyDidRestore starts
public static void Initialize()                                    // FindObjectOfType
public static void Deinitialize()                                  // sets _state = Pending, clears Shared
public void NotifyDidRestore()                                     // 65
public void RegisterForRestore(int priority, object observer, Action action)
public void Unregister(object observer)
```

`State` enum: `Pending → InProgress → Complete`. After `Complete`, late-arriving `RegisterForRestore` calls run on the next `LateUpdate` tick (`RestoreNotifier.cs:57`).

**Use this** if your code runs in `Awake` of a component that may load before or after the snapshot replay, and needs to read snapshot-restored state. You don't have to know the timing — register, and your callback runs after restore (immediately if already complete).

### Patch candidates

| Method | Why patch |
|---|---|
| `RestoreNotifier.NotifyDidRestore` | Postfix to run after vanilla restore but before the `PropertiesDidRestore` Messenger broadcast (note: vanilla actually sends `PropertiesDidRestore` *inside* `NotifyDidRestore`, between the prioritized actions and the `_state = Complete` flip). Patch carefully. |

### Gotchas

- Higher `priority` runs **earlier**. Counterintuitive if you assume "priority N" means "N-th in line."
- The list is mutated during iteration (`_pending.RemoveAt(0)`) — safe because each entry is processed once and removed.
- `Unregister` removes by reference equality on `Observer`. If you registered with `this` and your component is destroyed/recreated, the old entry stays unless you call `Unregister` first.

---

## Cross-references

- The settings UI that writes to `_game` (and thus rides the save): see [Settings & Preferences › In-game settings (`_game` KVO)](settings-preferences.md#in-game-settings-_game-kvo-host-authoritative-saved).
- The `wearFeatre` typo and its origins: see [Wear & Durability › toggle spine](wear-durability.md#toggle-spine-how-wearfeature-propagates) and [the GameStorage settings table](wear-durability.md#gamestategamestorage--statemanager-settings-plumbing).
- HostOnly key prefix conventions: see [Couplers › KVO key naming](couplers.md#kvo-key-naming) for the per-Car example.
- Multiplayer snapshot vs PropertyChange split, late-join semantics, message authority: see [`../multiplayer-vanilla-survey.md`](../multiplayer-vanilla-survey.md).
- `PropertyValueConverter` and the `KeyValue.Runtime.Value` ↔ `Game.Messages.IPropertyValue` boundary: `Game.Messages/PropertyValueConverter.cs`.
