# State Manager — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.State/StateManager.cs`, `Game/HostManager.cs`)
**Companions:** [Multiplayer Core](multiplayer-core.md), [KVO Patterns](kvo-patterns.md), [Request Messages](request-messages.md)

`StateManager` is the central traffic cop. It owns the `_storage` (`GameStorage`, the `_game` KVO), the `PlayersManager`, the `SaveManager`, the Ledger and LoanManager, and the `_propertyObjectManager` registry that maps `objectId → (KVO, accessControlDelegate)`. **Every `IGameMessage` flows through `StateManager.ApplyLocal` (client-side intent) or `StateManager.Handle` (post-receive dispatch).** It exposes the static `IsHost`, `IsSandbox`, `AccessLevel`, `Now`, and the gating helpers `AssertIsHost`, `CheckAuthorizedToSendMessage`, `CheckAuthorizedToChangeProperty`. There is one `Shared` instance (a MonoBehaviour, set in `OnEnable`); patches must null-check.

## Key entry points at a glance

| Symbol | File:Line | Purpose |
|---|---|---|
| `StateManager.Shared` | `StateManager.cs:74` | Singleton (set in `OnEnable`) |
| `StateManager.IsHost` (static) | `StateManager.cs:76` | `Multiplayer.IsHost` |
| `StateManager.IsSandbox` (static) | `StateManager.cs:124` | `Shared.GameMode == Sandbox` |
| `StateManager.AccessLevel` (static) | `StateManager.cs:92` | Host = President; client = `Multiplayer.Client.AccessLevel` |
| `StateManager.Now` (static) | `StateManager.cs:80` | `Multiplayer.Client.Tick` if connected, else `NetworkTime.systemTick` |
| `StateManager.ApplyLocal(IGameMessage)` | `StateManager.cs:452` | Client-side: validates auth, dispatches locally, sends to network |
| `StateManager.Handle(msg, playerId)` | `StateManager.cs:472` | Server/client receive entry — the giant if/else dispatcher |
| `StateManager.RegisterPropertyObject` | `StateManager.cs:1062, 1067` | Wire a KVO into the PropertyChange routing system |
| `StateManager.PopulateFromRemoteSnapshot` | `StateManager.cs:1131` | Late-join / save-load ingest |
| `StateManager.PopulateSnapshotForSave` | `StateManager.cs:1123` | Save serialize |
| `StateManager.CheckAuthorizedToSendMessage` (static) | `StateManager.cs:1359` | `HostManager.CheckAuthorizedToSendMessage` re-export |
| `StateManager.CheckAuthorizationForPropertyChange` | `StateManager.cs:1369` | Per-key auth resolver |
| `StateManager.HostRejectMessage` | `StateManager.cs:1422` | Host: send corrected-value back for rejected PropertyChange |
| `StateManager.AssertIsHost` (static) | `StateManager.cs:1347` | Throws if `!IsHost` |
| `StateManager.TransactionScope` (static) | `StateManager.cs:1109` | Wrap a block to coalesce sends into one `Transaction` |
| `RestoreNotifier.NotifyDidRestore` | `Game.State/RestoreNotifier.cs:65` | Fires `PropertiesDidRestore` Messenger |

---

## Lifecycle / phases

```
Awake          ── _playersManager, _timeObserver, _audioPlayer, _saveManager
OnEnable       ── Shared = this; Messenger registers MapWillLoad / MapDidLoad / MapWillUnload /
                  MapDidUnload / PropertiesDidRestore / AccessLevelDidChange / TimeDayDidChange
ApplyGameSetup ── (host only) SaveManager.LoadFromSaveIfNeededOrInitialize; ApplyNewGameSetup
OnMapWillLoad  ── TimeWeather.Reset; RestoreNotifier.Initialize; PrepareGameKeyValueObject; PreparePlayerProperties
                  (creates _storage = new GameStorage(KVO) which calls RegisterPropertyObject("_game", ...))
OnMapDidLoad   ── (just logs)
                                        ┌─ Snapshot arrives via SnapshotEnvelope (client) or LoadSnapshot (host)
                                        │
PopulateFromRemoteSnapshot ─────────────┤  TransactionScope():
                                        │    HandleSnapshotMapFeatures
                                        │    TrainController.HandleSnapshotSwitches/Turntables/Cars
                                        │    PlayersManager.RestoreFromSnapshot
                                        │    ApplySetupPropertyPresets
                                        │    RestoreProperties (PropertyObjectManager fan-out)
                                        │    ApplySnapshotMap (TimeWeather)
                                        │    RestoreNotifier.NotifyDidRestore  ← FIRES PropertiesDidRestore
                                        │    TrainController.PostRestoreProperties
                                        │    RestoreSwitchLists
                                        │    OpsController.PostRestoreProperties
                                        │  Multiplayer.UpdateLobbyFlags
                                        │  TrainController.ShowLostCarCutsWindowIfNeeded
                                        └─

OnPropertiesDidRestore ── (Messenger receiver, l.271) wires _storage observers
                          (game mode, weather, brake force, wear feature, oil feature,
                           overhaul miles, wear/oil multiplier); creates LoanManager
                           (non-sandbox only); Ledger.ReconcileIfNeeded(Balance) (host);
                           TutorialManager.ShowIfAppropriateForLaunch (host + tutorial save)

OnMapWillUnload ── IsUnloading=true; destroy LoanManager; SaveManager.WillUnloadMap;
                   PlayersManager.OnWillUnloadMap; TrainController.WillUnloadMap;
                   _timeObserver.StopObserving; dispose _observers
OnMapDidUnload  ── DestroyGameKeyValueObject; DestroyPlayerProperties;
                   _propertyObjectManager.UnregisterAll; RestoreNotifier.Deinitialize
```

### Critical: phases for mod init

| Phase | Earliest hook | What's available |
|---|---|---|
| Pre-map | Mod `Awake` | None of `Shared`, `_storage`, `Multiplayer.Client` |
| Map loading | `MapWillLoadEvent` Messenger | `Shared`, `_storage` (just created), no snapshot data |
| Map loaded | `MapDidLoadEvent` | Same as above + scene objects |
| Snapshot ingested | Mid-`PopulateFromRemoteSnapshot` (TransactionScope active) | Cars/sets exist; KVO observers fire with `Remote` |
| **All restored** | **`PropertiesDidRestore`** | Everything. **Mods should subscribe here.** |
| Active player | `ClientStatusDidChange(Active)` | Network is up; `Multiplayer.Client.IsClientStatusActive` |

`StateManager.HasRestoredProperties` (l.198) is the runtime "are we past restore" boolean.

### `IsUnloading` (l.78)

Static, set true in `OnMapWillUnload` and `OnApplicationQuit`. Used by mod cleanup hooks to distinguish "intentional teardown" from "object died unexpectedly."

---

## `ApplyLocal` and the `Handle` dispatcher

### `ApplyLocal(IGameMessage)` (`StateManager.cs:452`)

```csharp
public static void ApplyLocal(IGameMessage gameMessage)
{
    StateManager shared = Shared;
    if (shared == null) { Debug.LogWarning(...); return; }
    if (!CheckAuthorizedToSendMessage(gameMessage)) {
        Log.Warning("Ignoring; failed local authorization: {message}", gameMessage);
        return;
    }
    shared.Handle(gameMessage, shared._playersManager.LocalPlayer);     // local execution
    if (Multiplayer.Client != null)
        Multiplayer.Client.Send(gameMessage);                            // network send
}
```

**Behaviour notes:**
- Local handler runs *before* network send. Single-player and multiplayer-host see the same execution order: handler-then-broadcast.
- If `CheckAuthorizedToSendMessage` returns false, the message is *dropped client-side with no network send*. The host never sees the rejection happen — the early reject is local-only.
- `Multiplayer.Client.Send` (`ClientManager.cs:137`) wraps in `GameMessageEnvelope` with `Multiplayer.ChannelForMessage(message)` channel.
- If a transaction is active (`_inTransaction > 0`), `Send` instead appends to `_transactionMessages` (see [§ Transactions](#transactions)).

**`Shared.Handle(gameMessage, LocalPlayer)`** dispatches via the giant if/else chain (l.482-945). For each message type, the handler typically:
- Reads message fields,
- Checks `IsHost` (host-only handlers) or runs unconditionally (client-affecting handlers like `BatchCarPositionUpdate`, `SwitchListUpdate` to-train-crew),
- Calls into the appropriate subsystem (`_trainController`, `opsController`, `_propertyObjectManager`, `_playersManager`, `Game.Progression.Progression`, etc.).

### The dispatch chain (`StateManager.Handle`, `StateManager.cs:482`)

55+ `if (msg is X)` branches. Highlights:

| Message | Action |
|---|---|
| `ICharacterMessage` | `HandleCharacterMessage` — separate dispatcher (l.1013) |
| `ICarMessage` | (no-op marker, return; vanilla has no implementations) |
| `RequestSetSwitch` / `RequestSetSwitchUnlocked` | `_trainController.HandleRequestSetSwitch[Unlocked]` (host) |
| `SetSwitch` | `_trainController.HandleSetSwitch` (client receive) |
| `SetGladhandsConnected` | `_trainController.HandleSetGladhandsConnected` (host) |
| `ManualMoveCar` | `_trainController.HandleManualMoveCar` |
| `Rerail` | `_trainController.HandleRerail` (host validates) |
| `RequestCarSetIdent` | host → `_trainController.HandleRequestSetIdent` |
| `CarSetIdent` | client receive → `HandleSetIdent` |
| `CarSetBardo` | `_trainController.HandleSetBardo` |
| `BatchCarPositionUpdate` / `BatchCarAirUpdate` | `_trainController.HandleBatch...` (host-broadcast, client-receive) |
| `PlaceTrain` | host → `_trainController.HandlePlaceTrain` |
| `AddCars` / `RemoveCars` | host emits, client receives → `HandleAddCars/HandleRemoveCars` |
| `CarSetAdd/Remove/ChangeCars` | `_trainController.HandleCarSet...` |
| `FireEvent` | `HandleFireEvent` (Messenger broadcast wrapper) |
| `SwitchListUpdate` | If `MyTrainCrew?.Id == switchListUpdate.TrainCrewId`, `SwitchListPanel.Refresh` |
| `SwitchListToggleCarIds`, `SwitchListSetCarIds` | host → `opsController.SwitchListController.*` |
| `RequestOps` | host → `opsController.RequestOps` |
| `SetTimeOfDay` | `TimeWeather.Now = ...` (everyone), Messenger.Send(TimeAdvanced) |
| `WaitTime` | host → `WaitTime(hours)` coroutine |
| `SetCarTrainCrew` | `_trainController.HandleSetCarTrainCrew` |
| `RequestPurchaseEquipment` | host → `EquipmentPurchase.HandleRequest` |
| `PropertyChange` | `_propertyObjectManager.HandlePropertyChange` (everyone — applies to local KVO with origin=Remote) |
| `RequestCreateTrainCrew/Delete/Edit/SetTimetableSymbol` | host → `_playersManager.HandleRequest...` |
| `UpdateTrainCrews` | client → `_playersManager.HandleUpdateTrainCrews` |
| `TurntableUpdateAngle/StopIndex` | client (`!IsHost`) → `TurntableReceiver` |
| `Transaction` | iterate `Messages` and `Handle(message3, sender)` recursively |
| `PlaySoundAtPosition` / `PlaySoundNotification` | `_audioPlayer.HandlePlaySound` |
| `ProgressionStartPhase` | `Progression.Shared.HandlePayToStartPhase` |
| `RequestLoanDelta` | `LoanManager.HandleOffsetLoanRequest` |
| `AutoEngineerCommand`, `AutoEngineerWaypointR*Request/Response/Update`, `AutoEngineerContextualOrder`, `AutoEngineerWaypointRerouteRequest` | various `_trainController.HandleAutoEngineer...` |
| `FlareAddUpdate` / `FlareRemove` | host → `FlareManager.AddFlare/RemoveFlare` |
| `SetPassengerDestinations` / `SetPassengerAutoDestinations` | host → `PassengerExtensions.*` |
| `PlayerRecords` | client receives → builds `PlayerRecordsClientManager` |
| `RequestSetAccessLevel` / `RemovePlayerRecord` | host → `HostManager.SetAccessLevel/RemovePlayerRecord` |
| `LedgerRequest` / `LedgerResponse` | host responds with entries; client populates Ledger |
| `RequestOilCar` | host → `_trainController.HandleRequestOilCar` |
| `ModifyContract` | host → industry's `ModifyContract` |
| `PostNoticeEphemeral` | `NoticeManager.Shared.Handle` |
| `SetRepairMultiplier` | host → `RepairTrack.HandleSetMultiplier` |
| `SetTimetable` | host → `TimetableController.Shared.HandleSetTimetable` |

Full per-message catalog including auth attributes is in [Request Messages](request-messages.md).

### Why the dispatcher is a giant nested if-else

The decompiler renders it as deeply-nested `if (msg is X) { ... } else if (msg is Y) { ... }`. Source is likely a clean switch-on-type or pattern-match chain. Effect is identical — runtime type tests in declaration order. **Adding a mod message type does NOT slot into this dispatcher** — vanilla has no extension hook. Mod messages either:
- Piggyback on `PropertyChange` (objectId+key with mod-side observers), or
- Patch `StateManager.Handle` to add a custom prefix branch.

---

## Auth resolver

### `CheckAuthorizedToSendMessage(message)` (`StateManager.cs:1359`)

```csharp
public static bool CheckAuthorizedToSendMessage(IGameMessage message)
    => HostManager.CheckAuthorizedToSendMessage(message, PlayersManager.PlayerId, AccessLevel);
```

Implementation in `HostManager.cs:782`:
```csharp
public static bool CheckAuthorizedToSendMessage(IGameMessage message, PlayerId sender, AccessLevel level)
{
    if (message is Transaction tx)
        return tx.Messages.All(m => CheckAuthorizedToSendMessage(m, sender, level));
    foreach (IMessageAuthorizationRuleAttribute attr in
             message.GetType().GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), inherit: true))
        if (!attr.CheckAuthorization(sender, level, message)) return false;
    return true;
}
```

**Reflection-driven. No caching.** Every send, every receive. Per-message attribute lookup is reflection — not free, but vanilla doesn't optimize this. Mods sending many messages per frame should be aware.

**Transactions short-circuit on the first failed inner message.** A 100-message transaction with one bad message rejects the whole transaction.

### `CheckAuthorizationForPropertyChange(id, key, sender, level)` (`StateManager.cs:1369`)

```csharp
AuthorizationRequirementInfo req = _propertyObjectManager.AuthorizationRequirementForPropertyWrite(id, key);
return SenderSatisfiesAuthorizationRequirement(req, sender, level, key);
```

### `SenderSatisfiesAuthorizationRequirement` (`StateManager.cs:1375`)

```csharp
HostOnly:                sender == HostPlayerId  (host-side only; off-host always false)
PlayerIdKey:             on-host: true; on-client: sender.String == key
MinimumLevelPassenger:   level >= Passenger
MinimumLevelCrew:        level >= Crew, AND
                         (level >= Trainmaster, OR
                          !TrainCrewMembershipRequired, OR
                          (req.Object is string trainCrewId && trainCrew.MemberPlayerIds.Contains(sender)))
MinimumLevelDispatcher:  level >= Dispatcher
MinimumLevelTrainmaster: level >= Trainmaster
MinimumLevelOfficer:     level >= Officer
MinimumLevelPresident:   level >= President
```

**Crew + train-crew logic is the most subtle:**
- If `_storage.TrainCrewMembershipRequired` is **false** (default), Crew level is sufficient.
- If true, the player must either be Trainmaster+ OR be a member of the trainCrew passed in `req.Object`.
- `Car.AuthorizationRequirementForPropertyWrite` always passes `trainCrewId` for the Crew default — so member-only crew gating works on cars.

**`HostOnly` returns false on clients unconditionally.** Even if `sender == host's PlayerId` somehow leaked through, the `IsHost` gate fails first.

`CheckAuthorizedToChangeProperty(id, key)` (l.1364) wraps this with a synthetic NullPropertyValue for use in UI code (e.g., `GameStorage.CanWriteBrakeForce`).

---

## Snapshot / late-join

### Outbound: `PopulateSnapshotForSave(...)` (l.1123)

Called by `SaveManager.Save`. Iterates:
1. `_propertyObjectManager.PopulateSnapshotForSave` → snapshot.Properties[id] = kvo.SnapshotValues() for each registered object (`PropertyObjectManager.cs:69`).
2. `_playersManager.PopulateSnapshotForSave` → players + train crews.
3. `opsController.PopulateSnapshotForSave` → switch lists + turntables.
4. `Ledger.PopulateForSave(ledgerEntries)`.

**The snapshot includes everything in the registered KVO objects** — mod-side data is included automatically as long as `RegisterPropertyObject` was called.

### Outbound: `HostManager.SendSnapshotTo(playerId)` (l.668)

When a client hits `Active`, the host calls `PostActivate` → `SendSnapshotTo`. Builds:
- `_snapshot.map.TimeOfDay/Day` from `TimeWeather.Now`,
- For every `TrainController.Shared.Cars`: `_snapshot.Cars[car.id] = car.Snapshot()`,
- Sets `_snapshot.Version = 1`,
- `SendTo(playerId, new SnapshotEnvelope(DeepCopy(_snapshot)))`.

**Cars are snapshotted from live state at send time, not from the recorded host snapshot.** The host's `_snapshot.Cars` is rebuilt on every send. Other state (Properties, CarSets, CarAir, switches, turntables, players, trainCrews, switchLists) is the *recorded* snapshot maintained by `HostManager.RecordState` (l.823) per incoming message.

**`DeepCopy<TMessage>`** (`HostManager.cs:744`) is `Serialize → Deserialize` via MessagePack. The host avoids sending references that the snapshot keeps mutating.

### Inbound: `PopulateFromRemoteSnapshot(snapshot)` (`StateManager.cs:1131`)

```csharp
TrainController shared = TrainController.Shared;
if (shared == null) { Log.Error(...); return; }
// null-coalesce all snapshot collections
using (TransactionScope())
{
    HandleSnapshotMapFeatures(snapshot.Properties);                // mapFeatures KVO via MapFeatureManager
    shared.HandleSnapshotSwitches(snapshot.thrownSwitchIds);
    shared.HandleSnapshotTurntables(snapshot.Turntables);
    shared.HandleSnapshotCars(...);                                // Cars + CarSets + CarAir + Properties
    _playersManager.RestoreFromSnapshot(...);
    ApplySetupPropertyPresets(snapshot.Properties);                // for new-game-from-setup
    RestoreProperties(snapshot.Properties);                        // _propertyObjectManager fan-out
    ApplySnapshotMap(snapshot.map);                                // TimeWeather.Now
    RestoreNotifier.Shared.NotifyDidRestore();                     // ← fires PropertiesDidRestore
    shared.PostRestoreProperties();
    RestoreSwitchLists(snapshot.SwitchLists);
    opsController.PostRestoreProperties();
}
Multiplayer.UpdateLobbyFlags();
shared.ShowLostCarCutsWindowIfNeeded();
```

**The whole restore is wrapped in `TransactionScope`** — if `Multiplayer.Client` is non-null, all the resulting PropertyChange sends are batched into one `Transaction` message at scope-end. For the host (Client also exists in singleplayer due to LocalGameClient), the inner sends queue and then flush as one Transaction back through `Handle` → `RecordState`.

`RestoreProperties` (l.1233):
```csharp
SetValueOrigin origin = (!IsHost) ? SetValueOrigin.Remote : SetValueOrigin.Local;
_propertyObjectManager.RestoreProperties(theProperties, origin);
```

**Host uses `Local` origin during restore** — which fires `OnSetValueLocal` → `PropagateSetValueLocal` → broadcast. But because `Multiplayer.Client` is the host's loopback `LocalGameClient`, the broadcasts go back through the host's own dispatcher and into `RecordState`. The `TransactionScope` coalesces these into one outbound `Transaction` to other clients.

**Client uses `Remote` origin** — pure local KVO updates, no echo.

---

## `RegisterPropertyObject` + late registration

```csharp
public void RegisterPropertyObject(string id, IKeyValueObject kvo, AuthorizationRequirement req, object reqObject = null)
public void RegisterPropertyObject(string id, IKeyValueObject kvo, IPropertyAccessControlDelegate accessControl)
```

(`StateManager.cs:1062, 1067`). The `AuthorizationRequirement`-overload wraps as `StaticPropertyAccessControlDelegate`.

Implementation (l.1067):
```csharp
_propertyObjectManager.RegisterPropertyObject(id, kvo, accessControl);
kvo.OnSetValueLocal = (key, value) => PropagateSetValueLocal(id, value, key);
if (!IsHost) return;
using (TransactionScope())
    foreach (var (key, value) in kvo.Dictionary)
        PropagateSetValueLocal(id, value, key);
```

**Three things happen:**
1. Registration in `PropertyObjectManager._records`.
2. `OnSetValueLocal` is wired (a single-cast delegate; mods stacking handlers must `Combine`).
3. **Host only:** every existing key is re-broadcast as a PropertyChange. This is the late-registration sync — if the host registers an object after clients have joined, the existing values are pushed to them. Wrapped in TransactionScope so it's one batched outbound message.

**`PropertyObjectManager.RegisterPropertyObject`** (l.23) handles the dual case:
- If `id` already exists in `_records` with a deferred-restore origin set, the new KVO's `ResetData(oldValues, restoreOrigin)` replays the previously-restored data into the new KVO. This is for mods that register *after* snapshot ingest.
- Otherwise, a fresh record is created with `RestoreOrigin = null`.

---

## `HostHandlePropertyChangeRejected` — the correction loop

When `HostManager.RoutingForMessage` fails (`HostManager.cs:809`), it calls `StateManager.HostRejectMessage` which dispatches:
```csharp
if (gameMessage is PropertyChange pc)
    _propertyObjectManager.HostHandlePropertyChangeRejected(playerId, pc);
```

`HostHandlePropertyChangeRejected` (`PropertyObjectManager.cs:106`):
```csharp
IKeyValueObject kvo = ObjectForIdOrNull(propertyChange.ObjectId);
if (kvo == null) { Log.Warning(...); return; }
IPropertyValue currentValue = PropertyValueConverter.RuntimeToSnapshot(kvo[propertyChange.Key]);
PropertyChange correction = new PropertyChange(propertyChange.ObjectId, propertyChange.Key, currentValue);
HostManager.Shared.SendTo(playerId, new GameMessageEnvelope(PlayersManager.PlayerId.String, correction));
```

**The host sends a corrective PropertyChange back to the original sender** containing the *current host value*. The client's `PropertyObjectManager.HandlePropertyChange` applies it with `origin=Remote`, snapping local state back.

**Other rejected message types are silently dropped** — only PropertyChange has the correction path. A rejected `RequestOilCar`, `ManualMoveCar`, etc. just doesn't happen on the host; the client doesn't get a "rejection" event. (See [Request Messages § rejection](request-messages.md#rejection-and-error-handling).)

---

## Transactions

### `TransactionScope()` (`StateManager.cs:1109`)

Static, returns `IDisposable` (or null if no client):
```csharp
public static IDisposable TransactionScope()
    => Multiplayer.Client?.TransactionScope();
```

`ClientManager.TransactionScope` (`ClientManager.cs:300`) increments `_inTransaction` and returns a `TransactionCommitter` whose `Dispose()` decrements and flushes if reached zero.

### Inside a transaction

`ClientManager.Send` (`ClientManager.cs:137`):
```csharp
if (_inTransaction > 0) { _transactionMessages.Add(message); return; }
```

### Flush (`TransactionCommit`, `ClientManager.cs:311`)

```csharp
_inTransaction--;
if (_inTransaction <= 0 && _transactionMessages.Count != 0) {
    int idx = _transactionMessages.FindIndex(tm => tm is AddCars);
    if (idx >= 0) {                                          // hoist AddCars to front
        var addCars = _transactionMessages[idx];
        _transactionMessages.RemoveAt(idx);
        _transactionMessages.Insert(0, addCars);
    }
    var transaction = new Transaction(new List<IGameMessage>(_transactionMessages));
    Send(transaction);                                        // recursive Send (now _inTransaction=0)
    _transactionMessages.Clear();
}
```

**`AddCars` is hoisted to the front of the transaction.** Cars must exist before per-car PropertyChanges arrive — the receiver applies messages in order (`StateManager.Handle` for `Transaction` iterates `tx.Messages`).

### Use cases

- Snapshot restore (`PopulateFromRemoteSnapshot`).
- `RegisterPropertyObject` host-side seed (every existing key becomes one PropertyChange; coalesced into one Transaction).
- `PlaceTrain` (places multiple cars and per-car init).
- Mod use: any time you write multiple KVO keys / send multiple messages, wrap in `TransactionScope` to reduce wire chatter and avoid out-of-order arrivals.

**Caveat:** Transactions only batch *outbound network sends*. The local `Handle` calls happen synchronously inside `ApplyLocal` (and don't batch). Receivers see the inner messages one-by-one in the order they were added, applied via the `Handle(message3, sender)` recursion (`StateManager.cs:746`).

---

## Players, characters, train crews

### `PlayersManager`

`StateManager._playersManager` (added in `Awake`). Owns:
- `LocalPlayer`, list of `RemotePlayer`,
- `MyTrainCrew` (the train crew the local player is on),
- `TrainCrewForId(id, out crew)` lookup,
- `RestoreFromSnapshot(players, trainCrews)`,
- `HandleUpdateTrainCrews`, `HandleRequestCreateTrainCrew/Delete/Edit/SetTimetableSymbol`, `HandleRequestRenameTrainCrew`,
- `HandleRequestTrainCrewMembership(playerId, trainCrewId, join)`.

`PlayersManager.PlayerId` is static — current local-player id.

### Character messages (`HandleCharacterMessage`, l.1013)

| Message | Handler |
|---|---|
| `AddUpdateCharacter` | sender's RemotePlayer.AddUpdateAvatar |
| `UpdateCharacterPosition` | sender's RemotePlayer.UpdateAvatarPosition |
| `Say` | Console.Log + Hyperlink |
| `RequestSetTrainCrewMembership` | host → `_playersManager.HandleRequestTrainCrewMembership` |
| `UpdateCameraPosition` | `_playersManager.UpdateCameraPosition` |

---

## `Save/load` interaction

### `SaveManager` (`Game.State/SaveManager.cs`)

- `Save(string saveName = null)` — host calls `StateManager.Save` which calls into `_saveManager.Save`.
- `LoadFromSaveIfNeededOrInitialize` — at game-start in `ApplyGameSetup`.
- `WillUnloadMap` — pre-unload cleanup.

### Save flow

```
SaveManager.Save
  → StateManager.PopulateSnapshotForSave (above)
  → write Snapshot + PlayerRecords + Ledger to disk via Newtonsoft.Json or MessagePack
```

### Load flow (host-only)

```
SaveManager.LoadFromSaveIfNeededOrInitialize
  → deserialize → HostManager.LoadSnapshot(snapshot, playerRecords, carBodyPositions)
  → HostManager.LoadSnapshot:
      _snapshot = snapshot;
      _playerRecords = ...;
      StateManager.Shared.ApplySnapshotMap(snapshot.map);
      SnapshotCarBodyPositions = carBodyPositions;
      SendToAll(new SnapshotEnvelope(DeepCopy(snapshot)));   // broadcast to active clients
      _hasLoadedSnapshot = true;
      HandlePendingRequestActive();                          // promote queued clients
```

**The host's own client (LocalGameClient) is in `SendToAll` recipients** — so the host receives its own snapshot back via `SnapshotEnvelope`, processes through `ClientManager.ClientDidReceiveSnapshot` → `StateManager.PopulateFromRemoteSnapshot`. **Host and clients hit the same restore code path.**

`StateManager.HasRestoredProperties` flips true after `RestoreNotifier.NotifyDidRestore`; `_hasLoadedSnapshot` (HostManager) flips true after host-side load. These are the two "ready" flags.

### Mod state in save

If a mod's KVO is registered before save, it's serialized into `Snapshot.Properties[modObjectId]`. On load, `RestoreProperties` populates it with `Local` origin (host) or `Remote` (client).

**If a mod is *not* registered when load happens but the snapshot contains its data**, the data lands in `PropertyObjectManager._records[id]` as a synthetic `KeyValueStorage` with `StaticPropertyAccessControlDelegate(HostOnly)` and `RestoreOrigin = origin` (`PropertyObjectManager.cs:86`). When the mod later registers, the old `KeyValueStorage`'s `Dictionary` is replayed via `keyValueObject.ResetData(oldData, restoreOrigin)` (l.31). **Mod state survives mod-load-order issues this way** — register at any point and the data lands.

---

## Sandbox vs MP-host vs MP-client divergence

| Behaviour | Sandbox (SP) | MP-Host | MP-Client |
|---|---|---|---|
| `IsHost` | true | true | false |
| `IsSandbox` | depends on save's `GameMode` | depends on save | depends on save (host's choice) |
| `AccessLevel` | President | President | from `Login` auth |
| Network present? | Yes (`LocalGameClient` loopback) | Yes (`SteamServer` + LocalGameClient) | Yes (`SteamClient`) |
| Snapshot round-trip on load? | Yes (host loads, broadcasts to its own LocalGameClient) | Yes | Yes |
| `LoanManager` exists? | No (skipped if `Sandbox`, l.312-316) | Yes if non-Sandbox | Yes if host loaded non-Sandbox save |
| Ops/economy | Disabled-ish (Sandbox guard at `RepairTrack`, others) | Active | Active |
| `/repair` console command | Available | Available | Available |
| `WaitTime` (advance hours) | Host-only (`if (IsHost)` in `WaitTime`, l.1310) | Host-only | Sent to host as `WaitTime` IGameMessage (Trainmaster auth) |
| `SetTimeOfDay` | Officer auth | Officer auth | Officer auth |

**Key callout:** Sandbox is a GameMode, not a network mode. A multiplayer host can run a Sandbox game; clients see Sandbox semantics (no loans, no balance enforcement). The auth model still applies.

`StateManager.IsSandbox` reads `Shared.GameMode == Sandbox`. `Shared.GameMode` reads `_storage.GameMode` which reads `_gameKeyValueObject["mode"]`. Updated by host writing the `mode` key (HostOnly).

---

## Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.ApplyLocal` (static) | Intercept every outbound IGameMessage. Postfix to log, prefix to gate. |
| `StateManager.Handle(IGameMessage, IPlayer)` | The dispatch point — postfix to add custom message types or pre-execution side effects (e.g., audit log). |
| `StateManager.PopulateFromRemoteSnapshot` | Hook the snapshot ingest. Postfix runs after `PropertiesDidRestore`. |
| `StateManager.RegisterPropertyObject` | Wrap to detect mod registrations or override the auth delegate. |
| `StateManager.SenderSatisfiesAuthorizationRequirement` | Add custom requirement enums or change the train-crew membership rules. |
| `StateManager.CheckAuthorizedToSendMessage` (static) | Override message-level auth globally. Affects both client send-time and host receive-time checks. |
| `HostManager.HostHandlePropertyChangeRejected` | Modify the corrective-PropertyChange. E.g., emit a `RequestRejected` event to client mods. |
| `HostManager.RoutingForMessage` | Add custom routing rules (e.g., target-specific messages). Currently only `SwitchListUpdate` deviates from default `AllExcept`. |
| `RestoreNotifier.NotifyDidRestore` | Add post-restore observability. Other mods sending `Messenger.Default.Send(default(PropertiesDidRestore))` can be detected here. |
| `ClientManager.TransactionCommit` | Custom transaction policies (e.g., per-mod batching). |
| `SaveManager.Save` | Add mod-side save data alongside the snapshot. |

---

## MP authority — where the gates are

| Gate | Location | Effect on bypass |
|---|---|---|
| Send-side auth | `StateManager.ApplyLocal` → `CheckAuthorizedToSendMessage` | Drops local; no network send. **Local handler still doesn't run.** |
| Receive-side auth (host) | `HostManager.HandleGameMessage` → `RoutingForMessage` → `CheckAuthorizedToSendMessage` | Drops host-side; for PropertyChange, sends correction back. |
| Per-key KVO auth | `PropertyChangeAuthorizationRule` → `CheckAuthorizationForPropertyChange` → `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite` | PropertyChange-only path. Other messages with their own auth attribute don't go through this. |
| `IsHost` runtime check inside handler | E.g., `RepairTrack.HandleSetMultiplier`'s `AssertIsHost` | Belt-and-suspenders. Won't fire if message-level auth would have already dropped it on host-side, but is a defense against misrouted client-side calls. |
| Sandbox check | `if (GameMode == Sandbox)` | Per-handler optional gate (e.g. `RepairCommand` console command requires Sandbox). |

**Bypass paths:**
- **Host-side direct method calls bypass message-level auth** — calling `RepairTrack.HandleSetMultiplier(0.5f)` directly on the host doesn't run `[MinimumAccessLevel]` checks. The auth attribute only protects the *message-receive* path, not the C# method.
- **`StateManager.RegisterPropertyObject` doesn't validate the access-control delegate.** A mod can register an object with `StaticPropertyAccessControlDelegate(MinimumLevelPassenger)` for keys that should be HostOnly. No vanilla check.
- **`ApplyLocal` runs the local handler before sending.** A client calling `ApplyLocal(new PlaceTrain(...))` (Trainmaster-required) sees `CheckAuthorizedToSendMessage` fail at send-time — but only if the client's `AccessLevel < Trainmaster`. The handler is *not* invoked locally because `ApplyLocal` returns early. Good. However, if a mod patches around `ApplyLocal`'s auth check, the local handler runs and may apply state changes that the host then rejects.

---

## Gotchas

- **`StateManager.Shared` can be null.** It's set in `OnEnable`, cleared in `OnDisable`. Patches running outside scene play see null. Always null-check.
- **`AccessLevel.Undetermined` is logged as a Warning** in `StateManager.AccessLevel` getter (l.103) when client is null. This fires before `ConnectClient` completes — expect transient warnings during init.
- **`Handle` can be called from inside `Handle` (Transaction recursion).** The `_propertyObjectManager.HandlePropertyChange` invocation inside is reentrant; if a KVO observer calls back into `ApplyLocal`, you get nested Handles. The dispatcher handles this fine, but mods patching Handle should not assume single entry.
- **`OnPropertiesDidRestore` is `Messenger`-driven, not subscribed by `Messenger.Default.Register` from arbitrary code** — `StateManager` registers itself in `OnEnable`. Mods that want to be notified must `Messenger.Default.Register<PropertiesDidRestore>(this, handler)` themselves. **Use `RestoreNotifier.RegisterForRestore(priority, observer, action)`** (`RestoreNotifier.cs:90`) for prioritized callbacks instead — unlike Messenger, this preserves order, supports priority sorting, and has a `Unregister(observer)` method.
- **`RestoreNotifier.NotifyDidRestore` runs synchronously inside `PopulateFromRemoteSnapshot` while the TransactionScope is active.** Subscribers that call `ApplyLocal` or `KVO.Set(..., Local)` will queue into the same Transaction. **Their changes go out as part of the snapshot-restore Transaction**, in source-add order after the restore items.
- **`HostManager.SendToClient` / `SendTo` don't queue if the client isn't `Active`.** They log `HostManager won't send {message} to client with status {status}` and drop. Race condition during late-join.
- **`StateManager.Handle` is not thread-safe.** All Unity main thread. Don't dispatch from a background coroutine without `await UniTask.SwitchToMainThread()` or equivalent.
- **`AssertIsHost` throws.** Wrapped in `try` in many callers, but uncaught throws propagate to Unity's error log. **`DebugAssertIsHost` is empty** (l.1355) — it was likely a `#if DEBUG` guard that decompiled cleanly. Don't rely on it.
- **`SendFireEvent<TEvent>` has hardcoded enum-mapping** (l.952). Adding a new fire-event requires modifying the switch *and* `HandleFireEvent`. Mods can't add new event codes easily.
- **`WaitTime` runs as a host-side coroutine** (`WaitTimeCoroutine`, l.1316) calling `ApplyLocal(new SetTimeOfDay(...))` per simulated hour. It does NOT pause the rest of the game — physics, AI, etc. continue running while time fast-forwards. Setting `IsWaiting = true` is informational only.
- **`Now` (l.80) returns 0 if `Multiplayer.Client` is null and `NetworkTime.systemTick` is 0** (i.e., before scene load). Code reading `Now` for log timestamps in early init may see weird values.
- **`ApplyLocal` for unknown message types silently no-ops** because the dispatcher's nested if/else exits without a match. **No "unhandled message" log** in vanilla. Mod messages will be dropped without a warning.
- **`HostHandlePropertyChangeRejected` only handles `PropertyChange` rejections.** Other rejected messages (`RequestOilCar`, `PlaceTrain`, `ManualMoveCar`, ...) get no client-side feedback. Mods needing rejection awareness must add their own ack/nack mechanism.
- **`PopulateFromRemoteSnapshot` calls `MapFeatureManager.Shared.HandleSnapshotProperties` with origin=Local on host**, Remote on client (`l.1211`). Host-side restore broadcasts mapFeatures back through MapFeatureManager's KVO.
- **Save format version** — `_snapshot.Version = 1` (l.678). No version negotiation; old saves with different versions trigger field-by-field MessagePack tolerance. Snapshot fields are MessagePack-Key-tagged; missing fields default to zero-init.

---

## Init order

Across the boot sequence:

1. **Application Awake** — Unity bootstraps, `MessagepackSupport` not yet called.
2. **Mod assemblies load** (Railloader) — mod `Awake`s run.
3. **Main menu scene** — `StateManager` MonoBehaviour exists somewhere in the menu scene; `Shared` is set in `OnEnable`.
4. User starts/loads a game →
5. **`Multiplayer.PrepareHostIfNeeded`** creates `HostManager` (SP/MP-host).
6. **`Multiplayer.ConnectClient`** creates `ClientManager` + `GameClient` (`LocalGameClient` for SP/MP-host, `SteamClient` for MP-client). `GameClient.Setup` calls `MessagepackSupport.Setup()` — first chance for serialization.
7. **Map scene loads** → `Messenger` fires `MapWillLoadEvent`.
8. **`StateManager.OnMapWillLoad`** creates `_storage = new GameStorage(kvo)` — registers `_game` object.
9. **Cars/Industries spawn** — each `Awake` registers its own KVO via `RegisterPropertyObject`.
10. **Snapshot arrives** (host: from `LoadSnapshot.SendToAll`'s loopback, or `ApplyGameSetup` for new game; client: from `SnapshotEnvelope` over wire).
11. **`PopulateFromRemoteSnapshot`** runs, ending with `RestoreNotifier.NotifyDidRestore`.
12. **`StateManager.OnPropertiesDidRestore`** wires settings observers, creates `LoanManager`, etc.
13. **Client status hits `Active`** (host: in `RequestActive`-after-load flow; client: after host's `SnapshotEnvelope` arrives and dispatching completes).
14. **Steady state.**

**Mods that need to register KVO objects before snapshot ingest** must do so during step 8-9 (`MapWillLoad` / `MapDidLoad` / `Awake` of an early-load MonoBehaviour).
**Mods that observe restored state** must hook step 11/12 (`PropertiesDidRestore` Messenger event, or `RestoreNotifier.RegisterForRestore`).
**Mods that send messages** must wait for step 13 (`ClientStatusDidChange(Active)` or `IsClientStatusActive`).

---

## Cross-references

- [Multiplayer Core § Connection lifecycle](multiplayer-core.md#connection-lifecycle-networkserverclientstatus) — what happens before `Active`.
- [Multiplayer Core § Init-order pitfalls](multiplayer-core.md#init-order-pitfalls) — the network side of the same boot sequence.
- [KVO Patterns § The wire keys](kvo-patterns.md#the-wire-keys-high-traffic--high-value) — what's actually being broadcast.
- [KVO Patterns § HostOnly](kvo-patterns.md#hostonly--what-it-is-where-its-enforced) — both senses of HostOnly and how the auth chain composes.
- [Request Messages](request-messages.md) — full per-message catalog with handler locations and auth notes.
- [Wear & Durability § GameStorage](wear-durability.md#gamestategamestorage--statemanager-settings-plumbing) — example settings observer chain.
- [Couplers § Wire format](couplers.md#wire-format--mp-authority-summary) — example HostOnly + Crew dual-auth on one object.
