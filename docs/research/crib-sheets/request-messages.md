# Request Messages — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.Messages/`, `Game.State/StateManager.cs`)
**Companions:** [Multiplayer Core](multiplayer-core.md), [KVO Patterns](kvo-patterns.md), [State Manager](state-manager.md)

This is the catalog of every `IGameMessage` in vanilla. The "request" framing in the name (`RequestOilCar`, `RequestSetSwitch`, etc.) is convention not contract — only ~14 messages have `Request` in the name; the remaining ~50 are also IGameMessages with the same auth/dispatch machinery. **What unifies them: every IGameMessage is sent via `StateManager.ApplyLocal`, auth-checked by attribute, dispatched via `StateManager.Handle`'s nested if-else.** The host applies state changes; resulting KVO writes broadcast as `PropertyChange`. There is no reply payload — fire-and-forget. Rejection notifies *only PropertyChange rejections* via the corrective-broadcast path; everything else fails silently.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `IGameMessage` (interface + Union) | `Game.Messages/IGameMessage.cs` | The marker interface; 60+ Union tags |
| `[MinimumAccessLevel(level)]` | `Game.AccessControl/MinimumAccessLevelAttribute.cs` | Senders must have ≥ level |
| `[HostOnlyAuthorizationRule]` | `Game.AccessControl/HostOnlyAuthorizationRuleAttribute.cs` | Host-only emit |
| `[PropertyChangeAuthorizationRule]` | `Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs` | Per-key delegate via `IPropertyAccessControlDelegate` |
| `[RequestSetAccessLevelRule]` | `Game.AccessControl/RequestSetAccessLevelRuleAttribute.cs` | Custom: granting requires ≥ target level |
| `[RequestSetTrainCrewMembershipRule]` | `Game.AccessControl/RequestSetTrainCrewMembershipRuleAttribute.cs` | Self-Crew or Trainmaster |
| `StateManager.ApplyLocal(IGameMessage)` | `Game.State/StateManager.cs:452` | Send entry — auth-then-dispatch-then-network |
| `StateManager.Handle(IGameMessage, IPlayer)` | `Game.State/StateManager.cs:482` | Receive dispatcher (host/client both) |
| `HostManager.RoutingForMessage` | `Game/HostManager.cs:806` | Host-side auth + relay routing |
| `HostManager.RecordState` | `Game/HostManager.cs:823` | Host-side snapshot mutation per message |
| `Transaction` | `Game.Messages/Transaction.cs` | Batch wrapper (no own auth; inner auth ANDed) |
| `PropertyChange` | `Game.Messages/PropertyChange.cs:10` | The KVO-write wire format |

---

## Spine: lifecycle of a request message

```
1. Caller: StateManager.ApplyLocal(new SomeMessage(...))
2. StateManager.CheckAuthorizedToSendMessage:
     for each [IMessageAuthorizationRuleAttribute]: CheckAuthorization(myPlayerId, myAccessLevel, msg)
     fail-fast on first false → drop message, log warning
3. shared.Handle(msg, LocalPlayer)             ← LOCAL EXECUTION
4. if (Multiplayer.Client != null) Client.Send(msg)  ← NETWORK SEND
5. ClientManager.Send (in transaction? queue : pass)
6. GameClient.Send (status != Active? drop : wrap in GameMessageEnvelope, pick channel)
7. SteamClient.SendNetworkMessage (or LocalGameClient defers to Update)
8. Wire: serialize → gzip if >1024B → SteamNetworkingSockets.SendMessageToConnection
9. Host receives in SteamServer.ReceiveMessages → HostManager.HandleMessage → HandleMessageActive
10. HostManager.HandleGameMessage:
     envelope.sender = playerId.String;          ← OVERWRITES sender (anti-spoof)
     RoutingForMessage:
       CheckAuthorizedToSendMessage(msg, sender, AccessLevelForPlayerId(sender))
       fail → Routing.Reject() → StateManager.HostRejectMessage
       success → Routing.AllExcept(sender) | Routing.TrainCrew(crewId)
     if approved: RecordState(envelope) → SendToAll/SendTo
11. RecordState mutates _snapshot for late-joiners
12. Other clients: ClientManager.Send (the broadcast) → GameClient.HandleMessage →
    StateManager.Handle (their copy of the if/else dispatcher)
```

**The original sender's local execution** (step 3) happens before any network round-trip. So clients see their own actions reflected immediately — even if the host later rejects the broadcast (in which case for PropertyChange a corrective comes back; for other messages, no rejection feedback).

**This is the v0 trap referenced in the survey:** a client doing `ControlProperties[Hotbox] = 1` runs local observers, but the resulting PropertyChange is auth-rejected on the host, which sends back the *current* hotbox value, snapping local state. Window of incorrect state ≈ 1× RTT.

---

## Authorization attribute summary

| Attribute | Effect |
|---|---|
| (none) | **Sender-spoof-able? No** — `HostManager.HandleGameMessage` overwrites `envelope.sender` from the connection's PlayerId. But **anyone with `Active` status can send the message** since there's no rule to fail. **Vanilla has no IGameMessage without an auth attribute.** |
| `[HostOnlyAuthorizationRule]` | Host-only |
| `[MinimumAccessLevel(Passenger)]` | ≥ Passenger (lowest non-Banned tier) |
| `[MinimumAccessLevel(Crew)]` | ≥ Crew. **Note:** unlike PropertyChange's per-key Crew check, this does *not* check train-crew membership — pure access-level check. |
| `[MinimumAccessLevel(Dispatcher)]` | (no vanilla messages use this) |
| `[MinimumAccessLevel(Trainmaster)]` | ≥ Trainmaster |
| `[MinimumAccessLevel(Officer)]` | ≥ Officer |
| `[MinimumAccessLevel(President)]` | ≥ President |
| `[PropertyChangeAuthorizationRule]` | (PropertyChange only) — delegates to `StateManager.CheckAuthorizationForPropertyChange` |
| `[RequestSetAccessLevelRule]` | Granter must have ≥ target level (Officer to grant Officer; President to grant President; ≥Trainmaster to demote/promote within Crew–Officer range) |
| `[RequestSetTrainCrewMembershipRule]` | Setting own membership: ≥Crew; setting other's: ≥Trainmaster; if `TrainCrewMembershipManagedByTrainmaster` is true: always ≥Trainmaster |

**`Transaction` has no attribute.** Its auth is the AND of all inner messages' auth (`HostManager.cs:784`). A Trainmaster cannot smuggle a Crew-only-self-action (no — Crew-only is *less* restrictive than Trainmaster, so this works fine).

**`Snapshot` is in the IGameMessage Union but has no auth attribute and is not in `StateManager.Handle`'s dispatcher.** It's wrapped in `SnapshotEnvelope` (an `INetworkMessage`, not an `IGameMessage`) and handled by `GameClient.HandleMessage` directly. The Union tag exists for serialization but the type is never sent as a bare IGameMessage. Treat as snapshot machinery, not message machinery.

---

## Catalog

Format per row: **Message** | **Auth** | **Sender** | **Host action** | **Receiver action** | **Notes**.

### Car / consist topology

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `AddCars` (`AddCars.cs`, [Union 16]) | `HostOnly` | host emits | snapshot mutation; broadcast cars list to clients | hoisted to front of any Transaction (`ClientManager.cs:316`) |
| `RemoveCars` (`RemoveCars.cs`, [Union 17]) | `Trainmaster` | UI: trash car | `_trainController.HandleRemoveCars`; host rebuilds CarSets, removes car from snapshot | client receivers also dispatch `HandleRemoveCars` to clean up local |
| `CarSetAdd` (`CarSetAdd.cs`, [Union 40]) | `HostOnly` | host | `_trainController.HandleCarSetAdd` | snapshot.CarSets[id] = set |
| `CarSetRemove` (`CarSetRemove.cs`, [Union 41]) | `HostOnly` | host | `_trainController.HandleCarSetRemove` | |
| `CarSetChangeCars` (`CarSetChangeCars.cs`, [Union 42]) | `HostOnly` | host | `_trainController.HandleCarSetChangeCars` | reorders / regroups consist |
| `CarSetIdent` (`CarSetIdent.cs`, [Union 414]) | `HostOnly` | host | `_trainController.HandleSetIdent` | broadcasts new reportingMark/roadNumber |
| `CarSetBardo` (`CarSetBardo.cs`, [Union 423]) | `HostOnly` | host | `_trainController.HandleSetBardo` | "bardo" = limbo state for cars in transition; `Bardo` field on Car |
| `RequestCarSetIdent` (`RequestCarSetIdent.cs`, [Union 413]) | `Trainmaster` | client UI | host runs `_trainController.HandleRequestSetIdent`, then emits `CarSetIdent` | classic request → broadcast pattern |
| `BatchCarPositionUpdate` (`BatchCarPositionUpdate.cs`, [Union 12]) | `HostOnly` | host (per CarSet, every tick during motion) | `_trainController.HandleBatchCarPositionUpdate` | unreliable channel unless `Critical=true`. `Tick` for client-side reconciliation |
| `BatchCarAirUpdate` (`BatchCarAirUpdate.cs`, [Union 13]) | `HostOnly` | host (≥1s interval) | `_trainController.HandleBatchCarAirUpdate` | brake-line, brake-reservoir, brake-cylinder values bytes (0..120 PSI / 255) |
| `PlaceTrain` (`PlaceTrain.cs`, [Union 15]) | `Trainmaster` | UI: consist placer | host runs `_trainController.HandlePlaceTrain` (creates cars, places at location, applies handbrake setting) | cars list with prototype, flipped, mark, road, trainCrew, properties dict |
| `PlaceTrainHandbrakes` (enum, not a message) | n/a | n/a | n/a | enum used in PlaceTrain |
| `Rerail` (`Rerail.cs`, [Union 21]) | `Crew` | crew player | `_trainController.HandleRerail(carIds, amount, sender)` | host applies; cost charged from balance |
| `ManualMoveCar` (`ManualMoveCar.cs`, [Union 20]) | `Crew` | crew | `_trainController.HandleManualMoveCar(carId, direction)` | the "shove a car" force command |
| `SetGladhandsConnected` (`SetGladhandsConnected.cs`, [Union 19]) | `Crew` | crew (UI) | `_trainController.HandleSetGladhandsConnected` | air-hose connect on uncoupled cars adjacent |
| `SetCarTrainCrew` (`SetCarTrainCrew.cs`, [Union 406]) | `Trainmaster` | trainmaster | `_trainController.HandleSetCarTrainCrew` | reassigns car to crew |

### Switches / turntables

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `RequestSetSwitch` (`RequestSetSwitch.cs`, [Union 401]) | `Crew` | crew player | host: `_trainController.HandleRequestSetSwitch` validates (locked? own crew? at switch?) → emits `SetSwitch` | thrown bool only |
| `RequestSetSwitchUnlocked` (`RequestSetSwitchUnlocked.cs`, [Union 425]) | `Crew` | crew | host: `_trainController.HandleRequestSetSwitchUnlocked` | toggles the lock |
| `SetSwitch` (`SetSwitch.cs`, [Union 402]) | `HostOnly` | host | `_trainController.HandleSetSwitch` (everyone applies); host also records in `_snapshot.thrownSwitchIds` | carries `Tick`, `Requester` (info only) |
| `TurntableUpdateAngle` (`TurntableUpdateAngle.cs`, [Union 407]) | `HostOnly` | host (per tick during rotation) | `TurntableReceiver.HandleUpdateAngle` (clients only) | Movement channel (unreliable) |
| `TurntableUpdateStopIndex` (`TurntableUpdateStopIndex.cs`, [Union 408]) | `HostOnly` | host (on rotation completion) | `TurntableReceiver.HandleUpdateStopIndex`; host updates `_snapshot.Turntables` | reliable; final state |

### Auto-engineer (AI)

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `AutoEngineerCommand` (`AutoEngineerCommand.cs`, [Union 412]) | `Crew` | crew | `_trainController.HandleAutoEngineerCommand(command, sender)` | mode + max-mph + distance + waypoint string |
| `AutoEngineerContextualOrder` (`AutoEngineerContextualOrder.cs`, [Union 422]) | `Crew` | crew | `_trainController.HandleAutoEngineerContextualOrder(order, sender)` | "couple to that car ahead" type orders |
| `AutoEngineerWaypointRouteRequest` (`AutoEngineerWaypointRouteRequest.cs`, [Union 426]) | `Crew` | crew (UI request route) | host: `_trainController.HandleAutoEngineerWaypointRouteRequest(req, sender)` → replies with `AutoEngineerWaypointRouteResponse` to that sender via `SendTo` | this *is* a request/response pair — rare in vanilla |
| `AutoEngineerWaypointRouteResponse` (`AutoEngineerWaypointRouteResponse.cs`, [Union 427]) | `HostOnly` | host (reply to sender) | client: `_trainController.HandleAutoEngineerWaypointRouteResponse(resp, sender)` | locations + `HasMore` flag for chunked replies |
| `AutoEngineerWaypointRouteUpdate` (`AutoEngineerWaypointRouteUpdate.cs`, [Union 428]) | `HostOnly` | host (per location reached) | `_trainController.HandleAutoEngineerWaypointRouteUpdate` (everyone) | broadcast progress |
| `AutoEngineerWaypointRerouteRequest` (`AutoEngineerWaypointRerouteRequest.cs`, [Union 429]) | `Crew` | crew | host: `HandleAutoEngineerWaypointRerouteRequest(locomotiveId, sender)` | recompute |

### Property write — the catch-all wire mutation

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `PropertyChange` (`PropertyChange.cs:10`, [Union 11]) | `[PropertyChangeAuthorizationRule]` | anyone with proper key auth | host: routes via `StateManager.CheckAuthorizationForPropertyChange(objId, key, sender, level)` (which calls per-object `IPropertyAccessControlDelegate`); on approval `RecordState`'s `SetSnapshotProperty` updates `_snapshot.Properties[id][key]` and broadcasts to AllExcept; **on rejection, sends corrective PropertyChange back to sender with current host value** | the `Control` enum maps to the canonical Car keys (throttle, reverser, ..., `_condition`, `_derailment`, `hotbox`). `KeyForControl(Control) → string` is the lookup |

**`PropertyChange.Control`** enum:
```
Throttle="throttle", Reverser="reverser", LocomotiveBrake="locoBrake", TrainBrake="trainBrake",
Horn="horn", Bell="bell", Handbrake="handbrake", Bleed="bleed", Compressor="compressor",
CutOut="cutOut", Idle="idle", Headlight="headlight", BrakeStyle="brakeStyle",
Condition="_condition" (HostOnly), Derailment="_derailment" (HostOnly),
Mu="mu", CylinderCock="cylCock", Hotbox="hotbox" (HostOnly)
```

### Train crews / players

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `RequestCreateTrainCrew` (`RequestCreateTrainCrew.cs`, [Union 201]) | `Trainmaster` | trainmaster UI | `_playersManager.HandleRequestCreateTrainCrew(sender, trainCrew)` → emits `UpdateTrainCrews` | |
| `RequestDeleteTrainCrew` (`RequestDeleteTrainCrew.cs`, [Union 202]) | `Trainmaster` | trainmaster | `HandleRequestDeleteTrainCrew(sender, id)` | |
| `RequestEditTrainCrew` (`RequestEditTrainCrew.cs`, [Union 204]) | `Trainmaster` | trainmaster | `HandleRequestRenameTrainCrew(sender, id, name, desc)` | |
| `RequestSetTrainCrewMembership` (`RequestSetTrainCrewMembership.cs`, [Union 203]) | `[RequestSetTrainCrewMembershipRule]` | varies (self=Crew, other=Trainmaster) | `HandleRequestTrainCrewMembership(playerId, crewId, join)` | this is also `ICharacterMessage`, dispatched via `HandleCharacterMessage` |
| `RequestSetTrainCrewTimetableSymbol` (`RequestSetTrainCrewTimetableSymbol.cs`, [Union 205]) | `Trainmaster` | trainmaster | `HandleRequestSetTrainCrewTimetableSymbol` | |
| `UpdateTrainCrews` (`UpdateTrainCrews.cs`, [Union 200]) | `HostOnly` | host | clients: `HandleUpdateTrainCrews` | full dictionary replace |
| `RequestSetAccessLevel` (`RequestSetAccessLevel.cs`, [Union 31]) | `[RequestSetAccessLevelRule]` | trainmaster+ (varies by target) | `HostManager.SetAccessLevel(playerId, level, sender)` | broadcast announcement, may queue ban-disconnect |
| `RemovePlayerRecord` (`RemovePlayerRecord.cs`, [Union 32]) | `President` | president | `HostManager.RemovePlayerRecord(playerId, sender)` | only offline players can be removed |
| `PlayerRecords` (`PlayerRecords.cs`, [Union 30]) | `HostOnly` | host (sent to ≥Trainmaster on connect or change) | client: builds `PlayerRecordsClientManager` | |
| `AddUpdateCharacter` (`AddUpdateCharacter.cs`, [Union 100]) | `Passenger` | client | host: updates `_snapshot.players[id].Customization`; clients: update remote avatar | also `ICharacterMessage` |
| `UpdateCharacterPosition` (`UpdateCharacterPosition.cs`, [Union 102]) | `Passenger` | every-tick | clients: update remote avatar position | unreliable Movement channel; `Tick` for interpolation |
| `UpdateCameraPosition` (`UpdateCameraPosition.cs`, [Union 22]) | `Passenger` | crew (binoculars / observation) | host: tracks for record; clients: ignore by default | `ICharacterMessage` |
| `Say` (`Say.cs`, [Union 103]) | `Passenger` | anyone | logs to `Console.Log` everyone-side via `Hyperlink.To(sender)` | `ICharacterMessage`; truncated 512 chars |

### Ops / industry / economy

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `RequestOps` (`RequestOps.cs`, [Union 52]) | `Trainmaster` | trainmaster (debug console / tools) | `opsController.RequestOps(sender, request)` | command enum: `Sweep` / `Step` |
| `RequestPurchaseEquipment` (`RequestPurchaseEquipment.cs`, [Union 404]) | `Officer` | officer (UI: store) | `EquipmentPurchase.HandleRequest(sender, request)` | builds car, charges balance, places at delivery point |
| `RequestLoanDelta` (`RequestLoanDelta.cs`, [Union 411]) | `Officer` | officer | `LoanManager.HandleOffsetLoanRequest(delta, sender)` | int delta (positive = take loan, negative = repay) |
| `RequestOilCar` (`RequestOilCar.cs`, [Union 420]) | `Crew` | crew (clicks oil-point pickable) | `_trainController.HandleRequestOilCar(carId, amount)` | host calls `Car.OffsetOiled(amount)` → KVO `oiled` broadcast |
| `RequestOilCar` notes | | | | the pilot example for client→host write of HostOnly state |
| `SetRepairMultiplier` (`SetRepairMultiplier.cs`, [Union 424]) | `Trainmaster` | trainmaster (UI slider) | `RepairTrack.HandleSetMultiplier(multiplier)` (also has `AssertIsHost` belt-and-suspenders) | |
| `ModifyContract` (`ModifyContract.cs`, [Union 421]) | `Officer` | officer | finds industry by id, calls `Industry.ModifyContract(tier)` | |
| `SwitchListUpdate` (`SwitchListUpdate.cs`, [Union 50]) | `HostOnly` | host | clients (only members of `TrainCrewId`): `SwitchListPanel.Refresh` | **special routing: `Routing.TrainCrew(crewId)` not AllExcept** (`HostManager.cs:818`) |
| `SwitchListSetCarIds` (`SwitchListSetCarIds.cs`, [Union 53]) | `Crew` | crew | host: `opsController.SwitchListController.SetSwitchListCarIds` → emits `SwitchListUpdate` | |
| `SwitchListToggleCarIds` (`SwitchListToggleCarIds.cs`, [Union 51]) | `Crew` | crew | host: `opsController.SwitchListController.ToggleSwitchListCarIds` | |
| `SetPassengerDestinations` (`SetPassengerDestinations.cs`, [Union 417]) | `Crew` | crew | `PassengerExtensions.SetPassengerDestinations(carId, destinations)` | |
| `SetPassengerAutoDestinations` (`SetPassengerAutoDestinations.cs`, [Union 430]) | `Crew` | crew | `PassengerExtensions.SetPassengerTimetableAutoDestinations(carId, enabled)` | |
| `SetTimetable` (`SetTimetable.cs`, [Union 431]) | `Officer` | officer | `TimetableController.Shared.HandleSetTimetable(source, sender)` | full timetable text replace |
| `LedgerRequest` (`LedgerRequest.cs`, [Union 418]) | `Passenger` | anyone (UI: ledger window) | host: `Ledger.EntriesBetween` → `SendTo(sender, new LedgerResponse(...))` | request/response pair |
| `LedgerResponse` (`LedgerResponse.cs`, [Union 419]) | `HostOnly` | host (reply) | client: `Ledger.Load(entries, startBalance)` → `Messenger.Send(LedgerRequestResponseReceived)` | |

### Time / global state

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `SetTimeOfDay` (`SetTimeOfDay.cs`, [Union 403]) | `Officer` | officer | `TimeWeather.Now = ...` (everyone) → Messenger `TimeAdvanced` | |
| `WaitTime` (`WaitTime.cs`, [Union 405]) | `Trainmaster` | trainmaster (debug) | host coroutine: in 1-hour ticks, `Industry.TickAll`, advance time, emit `SetTimeOfDay` per hour | only host runs the coroutine |
| `ProgressionStartPhase` (`ProgressionStartPhase.cs`, [Union 410]) | `Officer` | officer | `Game.Progression.Progression.HandlePayToStartPhase(sectionId, phaseIndex, sender)` | charges balance, advances |
| `FireEvent` (`FireEvent.cs`, [Union 400]) | `HostOnly` | host (only) | `HandleFireEvent(eventCode)` → `Messenger.Send` of `BalanceDidChange` (0), `ProgressionStateDidChange` (1), `RequestRejected` (2), `ReputationUpdated` (3) | hardcoded enum mapping at `StateManager.SendFireEvent` (l.952). Mod-extension limited |

### Audio / notice / flares

| Message | Auth | Sender | Host action | Notes |
|---|---|---|---|---|
| `PlaySoundAtPosition` (`PlaySoundAtPosition.cs`, [Union 81]) | `HostOnly` | host (`ScheduledAudioPlayer.HostPlaySoundAtPosition`) | clients: `_audioPlayer.HandlePlaySound(play)` | unreliable; positional sound |
| `PlaySoundNotification` (`PlaySoundNotification.cs`, [Union 80]) | `HostOnly` | host (`ScheduledAudioPlayer.HostPlaySoundNotification`) | clients: `_audioPlayer.HandlePlaySound(play2)` | non-positional |
| `PostNoticeEphemeral` (`PostNoticeEphemeral.cs`, [Union 60]) | `HostOnly` | host | `NoticeManager.Shared.Handle(post)` | banner notice |
| `FlareAddUpdate` (`FlareAddUpdate.cs`, [Union 415]) | `Crew` | crew | host: `FlareManager.Shared.AddFlare(graphLocation, sender)` | location only — host generates id |
| `FlareRemove` (`FlareRemove.cs`, [Union 416]) | `Crew` | crew | host: `FlareManager.Shared.RemoveFlare(id, sender)` | |
| `Snapshot` (in IGameMessage Union 10 but not dispatched as IGameMessage — see [State Manager § snapshot](state-manager.md#snapshot--late-join)) | n/a | n/a | n/a | wrapped in `SnapshotEnvelope` (INetworkMessage) instead |
| `Transaction` (`Transaction.cs`, [Union 18]) | (none, ANDs inner attrs) | anyone (`StateManager.TransactionScope`) | iterates `tx.Messages`, `Handle(message3, sender)` recursively | `AddCars` is hoisted to front in `ClientManager.TransactionCommit` |

---

## Side-channel patterns

The "cut lever as uncouple side-channel" pattern documented in [Couplers § wire format](couplers.md#wire-format--mp-authority-summary) — where a non-HostOnly KVO key (`f.cutLever`) triggers host-side observers that write a HostOnly key (`_f.coupled = false`) — is **the dominant pattern in vanilla** for client-driven mutation of HostOnly state. There are several others:

### 1. Cut-lever / anglecock → HostOnly state mutation

```
client writes f.cutLever=1f (Crew auth, allowed)
  → PropertyChange broadcast
  → HOST KVO observer fires Car.HandleCutLeverValue
  → if (StateManager.IsHost && this[end].IsCoupled) HandleOpenCoupler:
      ApplyEndGearChange(LogicalEnd.B, IsCoupled, false)   ← writes _f.coupled (HostOnly)
      ApplyEndGearChange(LogicalEnd.A, IsCoupled, false)
  → host's KVO write broadcasts HostOnly _f.coupled=false to all
```

Same shape: anglecock changes (`f.anglecock`, Crew-writable) trigger `Car.UpdateAirConnection` host-side which writes `_f.airConnected` (HostOnly).

### 2. Bleed pulse → SetDelayed clear

```
client writes bleed=1f (Crew auth)
  → PropertyChange
  → KVO observer Car.cs:1685: air.BleedBrakeCylinder()
    if (StateManager.IsHost) KeyValueObject.SetDelayed("bleed", Value.Null(), 0.5f)
```

The host responds to the client write by *scheduling its own clear-write* 500ms later. The client's bleed input is a pulse, not a sustained state.

### 3. RequestOilCar → HostOnly oiled

```
client clicks oil point, pending oil accumulates
client OilPointPickable.Bank: ApplyLocal(new RequestOilCar(carId, amount))
  → Crew auth check
  → host receives, StateManager.Handle dispatches:
      _trainController.HandleRequestOilCar(carId, amount)
      → Car.OffsetOiled(amount)
      → KVO oiled key broadcast (HostOnly written by host)
```

This is the canonical "explicit Request* message" version of the pattern.

### 4. AutoEngineerCommand → HostOnly mode keys

`AutoEngineerCommand` (Crew auth) is a request to the host's AI subsystem. The host's `_trainController.HandleAutoEngineerCommand` mutates AI-state KVO keys on the locomotive (HostOnly) which then broadcast.

### 5. Rerail → balance + condition

`Rerail` (Crew auth) charges money and writes `_condition` and `_derailment` host-side. Single message, multiple HostOnly writes.

### 6. Single SetSwitch with Tick (host-broadcast pattern)

`SetSwitch` is HostOnly — clients can't send it directly. To request a switch throw, clients send `RequestSetSwitch` (Crew). Host validates (own crew, locked status, etc.) and emits `SetSwitch` (HostOnly), which all clients (including the original requester) apply via `_trainController.HandleSetSwitch`. **The `Requester` field on `SetSwitch` is informational only** — used for log strings, not auth.

### 7. SwitchListUpdate routing (TrainCrew-only relay)

The only message in vanilla with non-AllExcept routing. `HostManager.RoutingForMessage` (`HostManager.cs:817`) intercepts:
```csharp
if (msg is SwitchListUpdate slu)
    return Routing.TrainCrew(slu.TrainCrewId);
```

`TrainCrewPlayerIds(crewId)` resolves to a HashSet<PlayerId> and `SendTo(playerIds, envelope)` (l.450) sends only to those. Clients that receive but aren't on `MyTrainCrew` ignore (StateManager.Handle l.844: `if (PlayersManager.MyTrainCrew?.Id == switchListUpdate.TrainCrewId)`).

### 8. LedgerRequest / LedgerResponse — explicit request/response

Rare in vanilla. Host directly `SendTo(sender, ...)` the response — does not broadcast. Same pattern: `AutoEngineerWaypointRouteRequest`/`Response`.

---

## Patch candidates

| Method | Why patch |
|---|---|
| `StateManager.ApplyLocal` | Universal send hook. Prefix to log every outbound message; postfix to detect drops. |
| `HostManager.HandleGameMessage` (`HostManager.cs:701`) | Universal host-receive hook. Useful for per-player rate-limiting or auditing. Runs before auth. |
| `HostManager.RoutingForMessage` | Override relay rules. **Currently only `SwitchListUpdate` deviates.** Add custom routing for mod messages here. |
| `HostManager.RecordState` | Mutate the host's snapshot per message. Patch to record mod state. |
| `HostManager.CheckAuthorizedToSendMessage` (static) | Override auth for any message type. Affects send-time AND receive-time checks. |
| `StateManager.Handle` | Add custom message-type branches (or short-circuit existing ones). |
| `StateManager.HostRejectMessage` | Add a "rejection event" notification for client mods to subscribe. |
| `PropertyObjectManager.HandlePropertyChange` | Catches every inbound PropertyChange post-auth. Useful for mod-side observers without subscribing per-object. |
| Per-message handlers (`_trainController.HandleRequestOilCar`, etc.) | Customize specific actions. |

---

## Adding a mod request message — the procedure

Vanilla provides no extension point. The work for a mod adding `RequestModFoo(...)`:

1. **Define the struct** with `[MessagePackObject(false)]` and `[Key(N)]` per field. Add the `IGameMessage` interface.
2. **Apply an auth attribute** (`[MinimumAccessLevel(AccessLevel.Crew)]` is the typical "client→host action" choice).
3. **Register the Union tag** — vanilla's `[Union(N, typeof(...))]` attributes on `IGameMessage` are baked in at compile-time. Mods must:
   - Either patch `MessagepackSupport.Setup` to install a custom resolver that knows about the new union tag,
   - Or piggyback on `PropertyChange` with a mod-prefixed `objectId` like `mod.foo.requests` and a mod-defined dictionary value type (and dispatch on the mod side via a KVO observer on that key).
4. **Add a host-side handler** by patching `StateManager.Handle` (postfix; check the message type and act). This must check `IsHost` itself.
5. **Ensure the Active gate is honored** — call `ApplyLocal` only after `Multiplayer.Client.IsClientStatusActive`.
6. **Handle the no-rejection-feedback gap** — for non-PropertyChange messages, the client gets no signal if the host rejected. If your mod needs ack/nack, build a paired response message + correlation id.

The api kernel referenced in [the survey](../multiplayer-vanilla-survey.md) introduces an `IRequestRouter` to encapsulate this — the kernel handles the Union/serialization concern so mods don't have to.

---

## Rejection and error handling

| Rejection path | Where | Client visibility |
|---|---|---|
| Client-side pre-send auth failure (`StateManager.ApplyLocal`) | `CheckAuthorizedToSendMessage` returns false | Log warning only. Local handler does NOT run. **Silent for the user.** |
| Host-side auth failure (`HostManager.RoutingForMessage`) | Authorization fails on host | For PropertyChange: corrective `PropertyChange` sent back to sender with current host value. **For all other messages: silent drop on host. No client feedback.** |
| Inactive client send (`GameClient.Send`, `GameClient.cs:43`) | `ServerClientStatus != Active` | Log warning only. **Silent.** |
| Send error on Movement channel (`SteamClient.SendNetworkMessage` l.95) | Steam returns non-OK | **Silent — explicitly tolerated** for unreliable spam |
| Send error on Message/Data channel | Same | Disconnect (`GameClient.Disconnect`) |
| Per-client failed send during `SendToClients` | `_sendContext.ErroredRecipients()` enumerated | Each errored client is disconnected with `HostClosedConnection`. Other recipients may have succeeded. |

**The dominant pattern is "silent failure with eventual consistency."** PropertyChange has the corrective broadcast, so the *state* converges. Other messages don't have that — a rejected `RequestOilCar` simply doesn't happen, and the client's UI may have already shown a confirmation.

**Mods needing reliable client feedback for non-PropertyChange requests must build their own ack/nack pair** (e.g., `RequestModFoo(correlationId)` → `ResponseModFoo(correlationId, success)` HostOnly).

`StateManager.SendFireEvent(new RequestRejected())` (l.952) exists as an enum case (event code 2) but is **not invoked by any auth-rejection path in vanilla**. It's a placeholder. The intended Messenger event is `Game.Events.RequestRejected` — currently no source emits it.

---

## Gotchas

- **`Snapshot` is in the `IGameMessage` Union (tag 10) but not in `StateManager.Handle`'s dispatcher**. It's wrapped in `SnapshotEnvelope` (INetworkMessage union 11) and goes through `GameClient.HandleMessage` → `ClientDelegate.ClientDidReceiveSnapshot`. Don't try to `ApplyLocal(new Snapshot(...))` — the local handler will silently drop it (no matching branch).
- **`Transaction` has no auth attribute**, but inner messages are checked. A Transaction is rejected if *any* inner fails — the host doesn't partial-apply.
- **`AddCars` is hoisted to front of any Transaction** by `ClientManager.TransactionCommit` (l.316). Mods constructing transactions should *not* depend on insertion order matching execution order if AddCars is involved.
- **`HandleCharacterMessage` is a separate switch** (l.1013). `AddUpdateCharacter`, `UpdateCharacterPosition`, `Say`, `RequestSetTrainCrewMembership`, `UpdateCameraPosition` — these go through a different dispatcher than the main `Handle`.
- **`RequestSetTrainCrewMembership` uses `[RequestSetTrainCrewMembershipRule]` not `[MinimumAccessLevel]`**. Self-toggle is Crew; toggling another player's membership is Trainmaster — context-dependent.
- **`RequestSetAccessLevel` uses `[RequestSetAccessLevelRule]` (custom)**. The required access level depends on the *target* level being granted: Trainmaster to grant Crew/Trainmaster, Officer to grant Officer, President to grant President. (`RequestSetAccessLevelRuleAttribute.cs:11`).
- **`UpdateCameraPosition` is `Passenger`-auth and `Movement`-channel** — i.e., "anyone who joined can spam camera updates." For high-spectator-count sessions this is bandwidth-relevant.
- **`PlaySoundAtPosition` is HostOnly + Movement channel**. Lossy. Sound effects may drop. `PlaySoundNotification` is HostOnly + Message (reliable) — used for important sounds (cash register, etc.).
- **`Goodbye` is an `INetworkMessage`, not `IGameMessage`.** Disconnection signaling is at the network layer.
- **Setting `_storage` keys directly via `KVO.Set` bypasses GameStorage's typed setters.** `GameStorage.WearFeature = false` is identical to `_gameKeyValueObject["wearFeatre"] = false`. Both are HostOnly. Mods writing settings should prefer the typed property for type safety, but the KVO is the source of truth.
- **`SetSwitch.Requester` field is informational.** Setting it to a fake PlayerId in a host-side patch doesn't affect auth (`SetSwitch` is HostOnly anyway).
- **`Tick` fields on time-sensitive messages** (`UpdateCharacterPosition`, `BatchCarPositionUpdate`, `TurntableUpdateAngle`, `SetSwitch`) are server-aligned via `TimeSynchronizer`. Clients reconcile against `StateManager.Now`.
- **`Critical=false` on `BatchCarPositionUpdate`** routes to Movement (unreliable). Cars at rest send Critical=true updates (reliable, less frequent). The transition is in `TrainController` per-tick logic.
- **Train-crew-membership Crew check on PropertyChange uses `req.Object` as the trainCrewId**. `Car.AuthorizationRequirementForPropertyWrite` returns `new AuthorizationRequirementInfo(MinimumLevelCrew, trainCrewId)` for default-prefix car keys. **`MinimumLevelCrew` IGameMessage attribute** (e.g. `RequestSetSwitch`) **does NOT have this train-crew check** — it's a pure access-level test (`MinimumAccessLevelAttribute.CheckAuthorization` l.16). Train-crew membership only filters PropertyChange writes on cars assigned to a crew.
- **`_inTransaction` is a counter, not a boolean**. Nested `TransactionScope`s work — they all flush at the outermost Dispose. But each inner scope's Dispose decrements one. Mismatched counts (e.g., from exceptions) can leave the counter > 0 forever, queuing all subsequent sends until `_inTransaction <= 0` again. **Wrap in `using` to guarantee Dispose.**
- **`HostManager.SendTo(playerId, message)` requires `Active` status** (l.440). Messages sent during late-join handshake are dropped with a warning. Use `_pendingRequestActive` if you need queue-while-not-active.
- **`HostHandlePropertyChangeRejected` only sends to the original sender, not all clients** — other clients never see the rejection. So a divergent value on one client snaps back, but other clients don't know any rejection happened. Acceptable because they were always in sync.

---

## Cross-references

- [Multiplayer Core § Connection lifecycle](multiplayer-core.md#connection-lifecycle-networkserverclientstatus) — Active gate semantics.
- [Multiplayer Core § Channel routing](multiplayer-core.md#channel-routing-multiplayerchannelformessage-networkmultiplayercs205) — which channel each message uses.
- [State Manager § ApplyLocal](state-manager.md#applylocal-and-the-handle-dispatcher) — full pipeline of a request message through StateManager.
- [State Manager § Auth resolver](state-manager.md#auth-resolver) — how `MinimumLevelCrew + trainCrewId` is evaluated.
- [State Manager § Transactions](state-manager.md#transactions) — batch semantics for grouped requests.
- [KVO Patterns § HostOnly](kvo-patterns.md#hostonly--what-it-is-where-its-enforced) — the per-key auth side of `PropertyChangeAuthorizationRule`.
- [KVO Patterns § Wire keys](kvo-patterns.md#the-wire-keys-high-traffic--high-value) — what PropertyChange messages are typically about.
- [Couplers § Cut-lever pipeline](couplers.md#cut-lever-pipeline-player-driven-uncouple) — concrete example of side-channel pattern #1 above.
- [Couplers § Wire format & MP authority summary](couplers.md#wire-format--mp-authority-summary) — auth table for the four coupler keys.
- [Wear & Durability § MP authority](wear-durability.md#mp-authority) — example of HostOnly-only state with no client-write request.
