# KVO Patterns — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/KeyValue.Runtime/`, `Railroader-ILSPY/Assembly-CSharp/Game.AccessControl/`)
**Companions:** [Multiplayer Core](multiplayer-core.md), [State Manager](state-manager.md), [Request Messages](request-messages.md)

KeyValueObject (KVO) is Railroader's per-object property store with subscription and network-broadcast hooks. Every gameplay object that participates in MP — `Car`, `Industry`, `_game` (settings), `_progression`, characters, switches — owns a `KeyValueObject` MonoBehaviour. Writes are local-first (`KeyValueStorage.Set` mutates `_data` and notifies observers immediately), then *optionally* fan out via `OnSetValueLocal` → `StateManager.PropagateSetValueLocal` → network broadcast. **The KVO layer enforces no auth on its own** — auth is checked at the *message-send* layer (`StateManager.ApplyLocal` → `CheckAuthorizedToSendMessage`) and at the *host-receive* layer (`HostManager.RoutingForMessage`). This means a client can `Set("hostonly_key", value)` and see local observers fire, while the host quietly rejects the wire message and returns a corrected value.

## Key entry points at a glance

| Symbol | File | Purpose |
|---|---|---|
| `KeyValueObject` | `KeyValue.Runtime/KeyValueObject.cs` | MonoBehaviour wrapper, `RegisteredId`, `SetDelayed` coroutine |
| `KeyValueStorage` | `KeyValue.Runtime/KeyValueStorage.cs` | Underlying dict, observer fan-out, `Set/Observe/ResetData` |
| `Value` (struct) | `KeyValue.Runtime/Value.cs` | Discriminated union (Null/Bool/Int/Float/String/Array/Dict) |
| `SetValueOrigin` enum | `KeyValue.Runtime/SetValueOrigin.cs` | `Local` (broadcast) vs `Remote` (silent) |
| `IPropertyAccessControlDelegate` | `Game.AccessControl/IPropertyAccessControlDelegate.cs` | `AuthorizationRequirementForPropertyWrite(key) → AuthorizationRequirementInfo` |
| `AuthorizationRequirement` enum | `Game.AccessControl/AuthorizationRequirement.cs` | HostOnly, PlayerIdKey, MinimumLevel{Passenger..President} |
| `Car.AuthorizationRequirementForPropertyWrite` | `Model/Car.cs:3112` | The canonical prefix-driven implementation |
| `GameStorage.AuthorizationRequirementForPropertyWrite` | `Game.State/GameStorage.cs:598` | `_game` storage; HostOnly default with three Officer/Trainmaster keys |
| `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` | `Model.Ops/IndustryStorageHelper.cs:226` | Industry KVO; `extraScheduled` is Trainmaster, all else HostOnly |
| `StaticPropertyAccessControlDelegate` | `Game.AccessControl/StaticPropertyAccessControlDelegate.cs` | Lazy "every key has the same auth" wrapper |
| `MonoBehaviourKeyValueObserve.ObserveKeyValueDelayed` | `KeyValue.Runtime/MonoBehaviourKeyValueObserve.cs:44` | Defers observe registration one frame; finds parent KVO via `GetComponentInParent` |

---

## Spine: the `Set` → broadcast pipeline

```
caller ─►  KeyValueObject[key] = value                      // MonoBehaviour indexer
          KeyValueObject.Set(key, value, Local)
          KeyValueStorage.Set(key, value, origin=Local)
              if value.Equals(existing) → return false       // ← DEDUPE
              update _data[key]
              if origin == Local:  OnSetValueLocal?.Invoke(key, value)
                                   └► StateManager.PropagateSetValueLocal
                                       ├─ Multiplayer.Client != null:
                                       │    Client.Send(PropertyChange(id, key, value))
                                       │      → HostManager.HandleGameMessage
                                       │        → CheckAuthorizedToSendMessage (auth!)
                                       │        → SetSnapshotProperty
                                       │        → SendToAllExcept(sender)
                                       └─ host w/ no Client (singleplayer pre-Active):
                                            HostManager.SetSnapshotProperty
              for each observer of key: action(value)
              (KeyChange.Add/Remove notifications fire to ObserveKeyChanges subscribers)
```

### `KeyValueStorage.Set` (`KeyValue.Runtime/KeyValueStorage.cs:86`)

```csharp
public bool Set(string key, Value value, SetValueOrigin origin = SetValueOrigin.Local)
{
    if (_data.TryGetValue(key, out var existing) && existing.Equals(value))
        return false;                                        // dedupe by Value.Equals
    KeyChange? keyChange = null;
    if (value.IsNull) { keyChange = KeyChange.Remove; _data.Remove(key); }
    else              { if (!_data.ContainsKey(key)) keyChange = KeyChange.Add; _data[key] = value; }
    if (keyChange == KeyChange.Add)    NotifyKeyObservers(key, KeyChange.Add);
    NotifyObservers(key, value, origin);
    if (keyChange == KeyChange.Remove) NotifyKeyObservers(key, KeyChange.Remove);
    return true;
}
```

### `Value.Equals` (`Value.cs:278`)

```csharp
ValueType.Float => Mathf.Abs(FloatValue - other.FloatValue) < 1E-06f
```

**Float dedupe is at 1µ.** Setting `0.5f` then `0.5000001f` is a no-op. Important for high-resolution analog controls — sub-µ deltas never broadcast.

### `Value.IsNull` removes the key

Setting `Value.Null()` (or any value with `Type == ValueType.Null`) **deletes the key from `_data`**. The observer fires with `Value.Null()`, then `KeyChange.Remove` fires to key-change observers. A read after will return `Value.Null()` from the missing-key branch. **There is no distinction between "set to null" and "key never existed."** Mods that need null-as-explicit-value must use a non-Null sentinel.

### `SetDepth` warning (`KeyValueStorage.cs:124`)

```csharp
if (SetDepth > 1) Debug.LogWarning($"Set({key}) with SetDepth {SetDepth}");
```

Static counter increments around `NotifyObservers`. **Setting another KVO key from inside an observer triggers a warning** — Railroader expects observers to be side-effect-light. Patch dispatchers that chain writes will spam this warning. Acceptable but noisy.

### `OnSetValueLocal` is a single-cast delegate

```csharp
public Action<string, Value> OnSetValueLocal { get; set; }   // KVStorage.cs:47
```

Set in `StateManager.RegisterPropertyObject` (`StateManager.cs:1070`) to a closure capturing `id`. **Reassignment overwrites; mods stacking handlers must `Combine` the existing delegate.** No multicast support is built in.

---

## `SetValueOrigin` — the loop preventer

| Origin | Triggers `OnSetValueLocal` (broadcast)? | Triggers observers? | Used by |
|---|---|---|---|
| `Local` | YES | YES | Default; UI clicks, host-side mutators |
| `Remote` | NO | YES | `PropertyObjectManager.HandlePropertyChange` (incoming wire) `RestoreProperties` (snapshot/save) |

`PropertyObjectManager.HandlePropertyChange` (`PropertyObjectManager.cs:58`):
```csharp
value.Object.Set(change.Key, value2, SetValueOrigin.Remote);   // ← critical
```

**The discipline: anything originating off-wire uses `Remote`.** A mod that re-broadcasts must use `Local`. A mod that proxies an incoming change must use `Remote`.

`KeyValueObject.SetDelayed(key, value, delaySeconds)` always uses the default `Local` origin (see `KeyValueObject.cs:90`) — there is no `SetDelayed(..., Remote)` overload.

`ResetData(values, origin)` (`KeyValueStorage.cs:70`) bulk-applies with the given origin. Used by:
- `PropertyObjectManager.RegisterPropertyObject` (l.31): replays restored data with the deferred origin (host = Local; client = Remote).
- `PropertyObjectManager.RestoreProperties` (l.91): snapshot ingest.

---

## `SetDelayed` (`KeyValueObject.cs:90`)

```csharp
public void SetDelayed(string key, Value value, float delaySeconds)
{
    StartCoroutine(SetDelayedCoroutine(key, value, delaySeconds));
}

private IEnumerator SetDelayedCoroutine(string key, Value value, float delaySeconds)
{
    yield return new WaitForSeconds(delaySeconds);
    Set(key, value);   // origin=Local
}
```

Used for write-pulse patterns: write `1` now, schedule `null` 0.5s later. Examples:
- `Car` bleed: when the bleed control fires, host calls `KeyValueObject.SetDelayed("bleed", Value.Null(), 0.5f)` to clear (`Car.cs:1692`).
- Cut-lever (see [Couplers § cut lever pipeline](couplers.md#cut-lever-pipeline-player-driven-uncouple)): client sets `f.cutLever=1f`, schedules `0f` 1s later (via `LeanTween.delayedCall`, *not* `SetDelayed` — note the inconsistency).

**Caveat:** SetDelayed runs the deferred Set on the next frame at earliest, with the default Local origin and no `IsHost` check. If the object is destroyed during the wait, the coroutine is cancelled (Unity behaviour), which **silently loses the clear-write**.

---

## Observer registration

### `Observe(key, action, callInitial=true)` (`KeyValueStorage.cs:162`)

```csharp
IDisposable Observe(string key, Action<Value> action, bool callInitial = true)
```

Returns an `Unsubscriber` (`KeyValueStorage.cs:12`) — `Dispose()` removes from the per-key observer dict by Guid. **Holding the IDisposable is mandatory; otherwise the observer leaks** and the closure pins the captured object refs (preventing GC).

Standard pattern (`Car.SetupKeyValueObject`, `Car.cs:1642`):
```csharp
private readonly List<IDisposable> Observers = new List<IDisposable>();

Observers.Add(KeyValueObject.Observe("_condition", v => { _condition = v.FloatValueOrDefault(1f); ... }));
// On destroy:
foreach (var d in Observers) d.Dispose();
Observers.Clear();
```

`callInitial` defaults to `true` — the action runs synchronously inside `Observe(...)` with the current value (or `Value.Null()` if missing). Set to `false` for "only future changes" semantics.

### `ObserveKeyChanges(action)` (`KeyValueStorage.cs:206`)

```csharp
IDisposable ObserveKeyChanges(Action<string, KeyChange> action)
```

Subscribes to *all* Add/Remove events on the object (not Updates). One shot per key transition. Used internally to invalidate caches when keys appear/disappear. **No mod-side users in vanilla** — niche.

### `MonoBehaviourKeyValueObserve.ObserveKeyValueDelayed` (`MonoBehaviourKeyValueObserve.cs:44`)

```csharp
public static IDisposable ObserveKeyValueDelayed(this MonoBehaviour mb, string key, Action<Value> action)
```

Defers subscription one frame (`await Task.Yield()`), then walks `mb.gameObject.GetComponentInParent<KeyValueObject>()` to find the KVO. Useful when the observing component is on a child of the object holding the KVO (e.g., visual sub-components). `DelayedDisposable` handles the race where Dispose is called before the async observe lands.

**Failure mode:** If no parent `KeyValueObject` exists when the deferred subscribe fires, logs `Couldn't find KeyValueObject to subscribe to {key} for {mb.name}` and returns silently. The IDisposable is still returned (and disposing it is safe but a no-op).

---

## `IPropertyAccessControlDelegate` — per-key auth

```csharp
public interface IPropertyAccessControlDelegate                  // Game.AccessControl/
{
    AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key);
}

public struct AuthorizationRequirementInfo                       // Game.AccessControl/
{
    public readonly AuthorizationRequirement Requirement;        // enum
    public readonly object Object;                               // optional payload (e.g., trainCrewId for Crew check)
}
```

### `AuthorizationRequirement` enum

```
HostOnly = 0           // host's PlayerId only
PlayerIdKey = 1        // sender.String must equal the property key (player-owned data)
MinimumLevelPassenger = 10
MinimumLevelCrew = 11  // ← also runs train-crew membership check if Object is a trainCrewId
MinimumLevelDispatcher = 12
MinimumLevelTrainmaster = 13
MinimumLevelOfficer = 14
MinimumLevelPresident = 15
```

Resolved in `StateManager.SenderSatisfiesAuthorizationRequirement` (`StateManager.cs:1375`) — see [State Manager § auth resolver](state-manager.md#auth-resolver).

### Implementations in vanilla

| Class | Object id | Default | Special keys |
|---|---|---|---|
| `Car` (`Model/Car.cs:42, 3112`) | `car.id` | `MinimumLevelCrew` (with trainCrewId payload) | Prefix-driven. See below. |
| `GameStorage` (`Game.State/GameStorage.cs:10, 598`) | `_game` | `HostOnly` | `interchangeServeHour`/`interchangeShuffle` → Officer; `aiCrossingSignal`/`aiPassStopEnable`/`aiPassStopMinStopDur` → Trainmaster |
| `IndustryStorageHelper` (`Model.Ops/IndustryStorageHelper.cs:14, 226`) | industry identifier | `HostOnly` | `extraScheduled` → Trainmaster |
| `StaticPropertyAccessControlDelegate` (`Game.AccessControl/`) | wrapper | static for all keys | (used for any single-policy KVO) |

### `Car` prefix system (`Model/Car.cs:467-473, 3112`)

```csharp
private static readonly string[] HostPrefixes        = { "_", "ops.passengerMarker", "owned", "oiled", "hotbox" };
private static readonly string[] PassengerPrefixes   = { "door.", "gate." };
private static readonly string[] TrainmasterPrefixes = { "load.", "ops.waybill", "ops.repair-dest", "_colorScheme", "lettering.basic", "whistle.custom" };
private static readonly string[] OfficerPrefixes     = { "ops.sell-dest" };
```

Resolution order in `AuthorizationRequirementForPropertyWrite`: **Officer → Trainmaster → Passenger → Host → fallback Crew(trainCrewId)**.

**Key collision: `_colorScheme` is in TrainmasterPrefixes, but Officer is checked first, then Trainmaster, then Host (`_` matches). Since Trainmaster matches before Host, `_colorScheme` is Trainmaster — but anything else starting with `_` (e.g., `_colorBoard`) would *also* match Trainmaster's `_colorScheme` only if the test were `StartsWith("_colorScheme")`, which it is.** So the pattern is fine; just be aware that **prefix overlap matters** — `_colorScheme*` is Trainmaster, plain `_*` is Host. Don't add a TrainmasterPrefix that's a prefix of another HostPrefix (e.g., adding `"_oil"` would override the `oiled` Host-only key).

**Bypass implication:** A mod adding a new car KVO key whose name doesn't match any prefix falls through to `MinimumLevelCrew` with the car's `trainCrewId`. Crew on the right train can write it. **If you want HostOnly for a mod-added car key, prefix it with `_`** (or any of the HostPrefixes); to be safer, prefix with `_mod.` to signal mod ownership.

---

## `HostOnly` — what it is, where it's enforced

There are **two distinct "HostOnly"** concepts that share a name:

### 1. `HostOnlyAuthorizationRuleAttribute` — message-level

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public class HostOnlyAuthorizationRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (StateManager.IsHost) return PlayersManager.PlayerId == senderPlayerId;
        return false;
    }
}
```

Applied to the `IGameMessage` struct itself (`[HostOnlyAuthorizationRule]`). Checked by `HostManager.CheckAuthorizedToSendMessage` (`HostManager.cs:782`) which iterates `GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), inherit: true)`.

**Enforcement is reflection-based at runtime. No compile-time check.**

Runs in two places:
- Client send-time: `StateManager.ApplyLocal` (`StateManager.cs:460`) calls `CheckAuthorizedToSendMessage`. If false, logs and *drops the message before sending*. This is the early reject.
- Host receive-time: `HostManager.RoutingForMessage` (`HostManager.cs:806`) calls the same method against the *received* sender's PlayerId. If false, replies `Reject` → `StateManager.HostRejectMessage` → `PropertyObjectManager.HostHandlePropertyChangeRejected` (for PropertyChange only — other rejected messages just disappear).

### 2. KVO-level HostOnly via `IPropertyAccessControlDelegate`

`PropertyChange` has `[PropertyChangeAuthorizationRule]` which delegates to `StateManager.CheckAuthorizationForPropertyChange(objectId, key, sender, accessLevel)` (`PropertyChangeAuthorizationRuleAttribute.cs:11`). That looks up the per-object delegate and checks the per-key requirement (which may be `HostOnly`).

**So a `PropertyChange` for a HostOnly key is rejected by the per-key path, not the message-level path.** The message itself is `[PropertyChangeAuthorizationRule]`; the *key auth* is what fails.

### Bypasses to be aware of

- **Host-side direct `KeyValueObject.Set(key, value)`** is not gated. The host can write any key. This is by design: `RepairTrack.Service`, `Car.SetCondition`, etc. all write directly.
- **Client-side direct `KeyValueObject.Set(key, value)` with origin=Local fires local observers AND broadcasts.** The broadcast will be rejected, and `HostManager.HostHandlePropertyChangeRejected` sends back the *current host value* as a corrective `PropertyChange`. But locally, observers fire with the un-authorized value first. **Window of incorrect state until the correction arrives** (1× RTT).
- **Origin=Remote skips broadcast entirely.** A client setting `Set(key, val, Remote)` mutates local state with no network traffic. Useful for client-side prediction; dangerous if the host's state diverges silently.
- **`PropertyObjectManager.AuthorizationRequirementForPropertyWrite` defaults to HostOnly for unknown object IDs** (l.52-53). A mod that *forgets* to register its KVO object will see all writes auth-rejected from clients but apply locally. Symptom looks like "client UI flicks then snaps back."
- **`StateManager.RegisterPropertyObject` re-applies stored values via `keyValueObject.ResetData`** (`PropertyObjectManager.cs:31`). If a mod re-registers an already-known `id` with a new KVO instance, the previous data is replayed at the deferred origin. This is the late-restore mechanism but can surprise mods that re-register expecting an empty store.

---

## `IMessageAuthorizationRuleAttribute` — the auth-attribute interface

```csharp
public interface IMessageAuthorizationRuleAttribute              // Game.AccessControl/
{
    bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message);
}
```

Implementations:

| Attribute | Effect |
|---|---|
| `HostOnlyAuthorizationRuleAttribute` | Sender must equal host PlayerId |
| `MinimumAccessLevelAttribute(AccessLevel)` | `senderAccessLevel >= minimumLevel` |
| `PropertyChangeAuthorizationRuleAttribute` | Delegates to per-object KVO auth via `StateManager.CheckAuthorizationForPropertyChange` |
| `RequestSetAccessLevelRuleAttribute` | Custom: requires the actor have ≥ Trainmaster, or Officer+ to grant Officer, or President+ to grant President |
| `RequestSetTrainCrewMembershipRuleAttribute` | Self-join is Crew; setting another player's membership is Trainmaster (or Trainmaster always if `TrainCrewMembershipManagedByTrainmaster`) |

**Multiple attributes are AND-ed:** `HostManager.CheckAuthorizedToSendMessage` fails fast on the first false (l.798). Vanilla messages all use exactly one.

---

## The wire keys (high-traffic / high-value)

### `Car` (`Model/Car.cs:1645-1717`)

| Key | Type | Origin | Notes |
|---|---|---|---|
| `_f.coupled` / `_r.coupled` | bool | host | HostOnly (`_`) |
| `_f.airConnected` / `_r.airConnected` | bool | host | HostOnly |
| `f.anglecock` / `r.anglecock` | float | crew | non-HostOnly |
| `f.cutLever` / `r.cutLever` | float | crew | non-HostOnly; **side-channel uncouple** |
| `_condition` | float | host | HostOnly. Also `PropertyChange.Control.Condition` |
| `_derailment` | float | host | HostOnly |
| `oiled` | float | host | HostOnly (in `HostPrefixes`) |
| `hotbox` | int 0/1 | host | HostOnly. Cleared by writing `Value.Null()` |
| `_odosvc`, `_odometer`, `_lastOverhaul`, `_overhaulProg` | float | host | HostOnly |
| `throttle`, `reverser`, `locoBrake`, `trainBrake`, `horn`, `bell`, `handbrake`, `bleed`, `compressor`, `cutOut`, `idle`, `headlight`, `brakeStyle`, `mu`, `cylCock` | float/bool | crew | Default Crew + train-crew membership check |
| `door.*`, `gate.*` | bool | passenger | `PassengerPrefixes` |
| `load.{slotIndex}` | float | trainmaster | `TrainmasterPrefixes` |
| `ops.waybill` | dict | trainmaster | `TrainmasterPrefixes` |
| `ops.repair-dest` | string | trainmaster | `TrainmasterPrefixes` |
| `ops.sell-dest` | string | officer | `OfficerPrefixes` |
| `ops.passengerMarker` | (varies) | host | HostOnly |
| `owned` | bool | host | HostOnly |
| `_colorScheme*`, `lettering.basic*`, `whistle.custom*` | various | trainmaster | `TrainmasterPrefixes` |

### `_game` (`Game.State/GameStorage.cs`)

| Key | Type | Default | Auth |
|---|---|---|---|
| `mode` | int (GameMode) | 0 (Normal) | HostOnly |
| `setupId` | string | "" | HostOnly |
| `railroadName` | string | "" | HostOnly |
| `railroadMark` | string | "" | HostOnly |
| `balance` | int | 0 | HostOnly |
| `defaultAccessLevel` | int (AccessLevel) | Crew | HostOnly |
| `allowNewPlayers` | bool | true | HostOnly |
| `passwordHash` | string | "" | HostOnly |
| `timeMultiplier` | float | 1 | HostOnly |
| `loanAmount`, `loanNextInterestDate`, `loanNextInterestOffset` | float/long | 0 | HostOnly |
| `unbilledRunDuration` | float | 0 | HostOnly |
| `passengerLimit` | int | 8 | HostOnly |
| `brakeForce`, `brakeForceHandbrake` | float? | null (use config default) | HostOnly |
| `wearFeatre` (sic) | bool | true | HostOnly |
| `oilPrevMaintFeature` | bool | true | HostOnly |
| `overhaulMi` | int | 2500 | HostOnly |
| `wearMult`, `oilUseMult` | float | 1 | HostOnly |
| `mapShowsSwitches` | bool | true | HostOnly |
| `timetableFeature` | bool | false | HostOnly |
| `aiCrossingSignal` | int | 1 | **Trainmaster** |
| `aiPassStopEnable` | bool | true | **Trainmaster** |
| `aiPassStopMinStopDur` | int | 60s | **Trainmaster** |
| `aiCallSignals` | int | 1 | HostOnly |
| `interchangeServeHour` | int | (unset) | **Officer** |
| `interchangeShuffle` | int | 0 | **Officer** |
| `trainCrewMembershipRequired` | bool | (false) | HostOnly |
| `trainCrewMembershipManagedByTrainmaster` | bool | (false) | HostOnly |
| `weatherId` | int | 0 | HostOnly |
| `progression` | dict | (per setup) | HostOnly (registered separately as `_progression`) |

### Industry KVO

| Key | Type | Auth |
|---|---|---|
| `storage` | dict<loadKey, float> | HostOnly |
| `interchangeDisabled` | bool | HostOnly |
| `lastServiced` | float (TotalSeconds) | HostOnly |
| `extraScheduled` | float (TotalSeconds) | **Trainmaster** |
| `warnings` | dict<key,string> | HostOnly |
| `{subId}-rate` (for RepairTrack: `payRate`, `paidCurr`, `payDue`) | various | HostOnly |
| `hadUnfulfilledOrders` | bool | HostOnly |

---

## Patch candidates

| Method | Why patch |
|---|---|
| `KeyValueStorage.Set` | The single chokepoint for every value mutation. Prefix to log/veto specific keys (be careful: hot path, runs for every observer notification). |
| `KeyValueStorage.NotifyObservers` | Adds a "post-set, post-observe" hook. Useful for analytics / replay. |
| `KeyValueObject.SetDelayed` | Replace the coroutine with a frame-aware queue (e.g., to coalesce many SetDelayed clear-writes). |
| `Car.AuthorizationRequirementForPropertyWrite` | Add mod-prefix → auth mappings for mod-defined car keys. Postfix (return early if vanilla returns Crew default). |
| `GameStorage.AuthorizationRequirementForPropertyWrite` | Add Officer/Trainmaster routing for mod-defined `_game` keys (e.g. mod settings the host should let Trainmaster change). |
| `StateManager.RegisterPropertyObject` | Wrap to emit a "mod object registered" event for inter-mod discovery. |
| `PropertyObjectManager.HandlePropertyChange` | Catches every inbound PropertyChange. Useful for cross-mod observation without subscribing per-object. |
| `MonoBehaviourKeyValueObserve.ObserveKeyValueDelayed` | Replace with a synchronous variant (the deferred subscribe is a footgun for code that needs immediate invocation). |

---

## MP authority for mod-added KVO

If a mod registers its own `KeyValueObject` via `StateManager.RegisterPropertyObject(id, kvo, accessControlDelegate)`:

1. **The accessControlDelegate's `AuthorizationRequirementForPropertyWrite(key)` is called for every incoming PropertyChange targeting this object.**
2. Snapshot inclusion is automatic — the registered KVO's `Dictionary` is serialized into `Snapshot.Properties[id]` on save and on `SendSnapshotTo` (host-side).
3. Late-joiners receive the snapshot and `RestoreProperties` (`PropertyObjectManager.cs:80`) replays into the registered KVO with `origin=Remote` (no rebroadcast).
4. **Host-side**, registering also seeds the snapshot with the current values: `RegisterPropertyObject` (`StateManager.cs:1078-1085`) iterates `Dictionary` and calls `PropagateSetValueLocal` for each — the host's `HostManager.SetSnapshotProperty` records them. This means if the host registers a KVO that already has values (e.g., loaded from a mod-side save), those values immediately enter the snapshot and broadcast.

**Order matters:** if the host registers an object after a client has already joined, the existing values are pushed via `PropagateSetValueLocal` → `Multiplayer.Client.Send(PropertyChange)` *to the host's own loopback client*, which then broadcasts to other clients via `HostManager.HandleGameMessage`. So the propagation works for late-registered objects, but each key triggers a separate PropertyChange (no batching unless wrapped in `using (TransactionScope())`).

See [State Manager § snapshot](state-manager.md#snapshot--late-join) and [Request Messages § PropertyChange](request-messages.md#propertychange) for the cross-cutting flow.

---

## Gotchas

- **Float dedupe at 1µ.** Write `0.5` then `0.5000001` → no broadcast. Mods sampling fine analog values must compare against the previous broadcast value, not assume `Set` always fires observers.
- **Setting `Value.Null()` removes the key from `_data`.** No way to store explicit-null. Use sentinels (`Value.Float(-1)`, `Value.String("")`) if you need "explicitly empty."
- **`OnSetValueLocal` is single-cast.** Stacking handlers requires `Delegate.Combine`. Forgetting destroys vanilla's broadcast wiring.
- **Observer registration with `callInitial=true` runs synchronously inside `Observe(...)`.** If the action throws, `InvokeAction` catches and logs (`KVStorage.cs:182`). The IDisposable is still returned; you don't see the exception unless you watch the log sink.
- **`KeyValueObject` is `[DisallowMultipleComponent]`.** Cannot stack two KVOs on one GameObject.
- **`SetDepth` warning** logs at depth>1 — patches that chain writes inside observers will spam. The warning is informational; chained writes work, just noisily.
- **`SetValueOrigin` defaults to `Local`.** Forgetting `Remote` on a snapshot-restore path causes a re-broadcast loop.
- **`SetDelayed` always uses `Local` origin.** No way to schedule a `Remote` write — semantically wrong but worth knowing.
- **`SetDelayed` is cancelled on object destroy** (Unity coroutine semantics). Pulse-then-clear patterns lose the clear if the object dies during the wait.
- **`MonoBehaviourKeyValueObserve.ObserveKeyValueDelayed` is one-frame async.** Calling it then immediately disposing returns a no-op disposable, but the observer hasn't been wired yet and won't be cleaned up — but `DelayedDisposable.SetChild` checks `Disposed` and disposes the eventual child. Race-safe but not synchronous.
- **`KeyValueObject.RegisteredId` is set by `RegisterPropertyObject`.** Until that point, the KVO has no id and is invisible to PropertyChange routing. *Don't observe a KVO before it's registered* if you depend on remote updates flowing in.
- **`ResetData` clears `_data` first**, then sets each key. Observers see all-keys-removed-then-readded if `ObserveKeyChanges` is wired. **No "diff" mode** — restore is wholesale.
- **`Car.AuthorizationRequirementForPropertyWrite`'s prefix list is `static readonly`** — can't change at runtime without a Harmony patch on the method. There is no API to add prefixes for a single car instance.
- **The `_` prefix is the de facto "system" namespace.** Mod-added keys starting with `_mod.foo` will be HostOnly by Car's resolver. Don't use `_` prefix for crew-writable mod keys.
- **`Value.Equals` for Dict/Array does deep-compare.** Setting a dict to a copy with the same contents triggers no broadcast — the dedupe works. But constructing a new `Dictionary<string, Value>` per write doesn't escape this; the *contents* must differ.
- **Implicit conversions on `Value`** (`Value.cs:234`) make `Value v = 0.5f;` work but also make `if (v) { ... }` parse as `BoolValue` — mind context when comparing to literals.
- **`InvokeAction` swallows exceptions.** Observer code that throws gets logged but the chain continues. Mods cannot rely on observer-throw to abort a Set.
- **`RegisteredId` re-write doesn't notify.** Renaming a registered object via `kvo.RegisteredId = "new"` doesn't move it in `PropertyObjectManager._records`. Use Unregister/Register instead.

---

## Init order

1. `StateManager.OnEnable` (`StateManager.cs:216`) sets `Shared`.
2. `StateManager.OnMapWillLoad` → `PrepareGameKeyValueObject` creates `_storage = new GameStorage(kvo)` which calls `RegisterPropertyObject("_game", kvo, this)`.
3. `Car.SetupKeyValueObject` (`Car.cs:1642`) is called from `Car.Awake`, registers the per-car KVO with `id` as object id.
4. `Industry.Awake` similarly registers via `IndustryStorageHelper`.
5. Snapshot ingest (`StateManager.PopulateFromRemoteSnapshot` → `_propertyObjectManager.RestoreProperties`) replays values into all registered objects with `origin=Remote`.
6. Snapshot also handles "objects in snapshot that aren't yet registered" (`PropertyObjectManager.RestoreProperties` l.86) by storing them in synthetic `KeyValueStorage` records with deferred origin. When a mod *later* registers that id, the deferred values replay (`RegisterPropertyObject` l.27).
7. `RestoreNotifier.NotifyDidRestore` (`StateManager.cs:1173`) fires the `PropertiesDidRestore` Messenger event. **All KVO state is consistent at this point.**

**For mods:**
- Register your KVO objects in `Awake` of a `MonoBehaviour` that exists at scene-load (e.g., `[StateRequiredOnLoad]` injected component).
- Subscribe to `Messenger.Default.Register<PropertiesDidRestore>` for "all snapshot data has landed."
- If you must register mid-session, use `StateManager.TransactionScope()` (see [State Manager § transactions](state-manager.md#transactions)) to coalesce the seed-fan-out into one broadcast.

---

## Cross-references

- [Multiplayer Core § Transport](multiplayer-core.md#steamworks-p2p-transport) — what happens to a PropertyChange after `ApplyLocal`.
- [State Manager § ApplyLocal flow](state-manager.md#applylocal-and-the-handle-dispatcher) — message-level send pipeline; this is where KVO writes turn into PropertyChange messages.
- [State Manager § Auth resolver](state-manager.md#auth-resolver) — how `AuthorizationRequirementInfo` is evaluated against the sender.
- [Request Messages § PropertyChange](request-messages.md#propertychange) — wire format detail.
- [Couplers § State writes](couplers.md#state-writes-applyendgearchange-is-the-only-door) — example of a single chokepoint over four KVO keys with mixed auth.
- [Wear & Durability § MP authority](wear-durability.md#mp-authority) — example of HostOnly-only KVO with no client-side write path.
