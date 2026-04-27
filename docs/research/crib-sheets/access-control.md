# Access Control & Authorization — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.AccessControl/`, `Game.State/StateManager.cs`, `Game/HostManager.cs`)
**Companions:** [Multiplayer Core](multiplayer-core.md), [State Manager](state-manager.md), [KVO Patterns](kvo-patterns.md), [Request Messages](request-messages.md)

This is the auth engine. Two parallel pipelines feed it: **message-level** (one or more `IMessageAuthorizationRuleAttribute` on each `IGameMessage` struct) and **property-level** (per-key `IPropertyAccessControlDelegate` lookup, only used by `PropertyChange`). Both pipelines reduce to the same internal predicate `SenderSatisfiesAuthorizationRequirement`. The whole thing is reflection-driven, runs on every send and every receive, and has no caching layer. Vanilla ships **3 message-level rule kinds, 2 custom rules, 1 property-level rule, 1 wildcard `StaticPropertyAccessControlDelegate`, and exactly 4 implementations of `IPropertyAccessControlDelegate`** (`Car`, `GameStorage`, `IndustryStorageHelper`, `StaticPropertyAccessControlDelegate`).

## Key entry points at a glance

| Symbol | File:Line | Purpose |
|---|---|---|
| `AccessLevel` (enum) | `Game.AccessControl/AccessLevel.cs:3` | Banned=-10, Undetermined=0, Passenger=10, Crew=20, Dispatcher=30, Trainmaster=40, Officer=50, President=60 |
| `AuthorizationRequirement` (enum) | `Game.AccessControl/AuthorizationRequirement.cs:3` | HostOnly=0, PlayerIdKey=1, MinimumLevel{Passenger=10..President=15} |
| `AuthorizationRequirementInfo` (struct) | `Game.AccessControl/AuthorizationRequirementInfo.cs:3` | (requirement, optional Object payload). Implicit-cast from `AuthorizationRequirement` |
| `IMessageAuthorizationRuleAttribute` | `Game.AccessControl/IMessageAuthorizationRuleAttribute.cs:5` | The 5 attribute classes all implement this |
| `IPropertyAccessControlDelegate` | `Game.AccessControl/IPropertyAccessControlDelegate.cs:3` | Single method `AuthorizationRequirementForPropertyWrite(key)` |
| `HostManager.CheckAuthorizedToSendMessage` (static) | `Game/HostManager.cs:782` | The top-level reflective rule iterator. Used both client-send and host-receive |
| `StateManager.CheckAuthorizationForPropertyChange` | `Game.State/StateManager.cs:1369` | Per-key entry; called only by `PropertyChangeAuthorizationRule` |
| `StateManager.SenderSatisfiesAuthorizationRequirement` | `Game.State/StateManager.cs:1375` | The core 8-case switch on the requirement enum |
| `StateManager.HostRejectMessage` | `Game.State/StateManager.cs:1422` | Rejection chokepoint — only `PropertyChange` gets a corrective broadcast |
| `Car.AuthorizationRequirementForPropertyWrite` | `Model/Car.cs:3112` | Canonical prefix-array implementation |
| `AccessLevelControlDelegateExt.StaticDelegate` | `Game.AccessControl/AccessLevelControlDelegateExt.cs:5` | Extension `requirementInfo.StaticDelegate()` → `IPropertyAccessControlDelegate` |

---

## Spine: how a request is authorized

```
client UI calls StateManager.ApplyLocal(msg)
  ├─► HostManager.CheckAuthorizedToSendMessage(msg, MyPlayerId, MyAccessLevel)   // CLIENT-SIDE PRE-SEND
  │      ├─ if msg is Transaction: AND of inner CheckAuthorizedToSendMessage calls
  │      └─ for each [IMessageAuthorizationRuleAttribute] on msg.GetType():
  │           attr.CheckAuthorization(senderPlayerId, senderAccessLevel, msg)
  │           AND-reduce, fail-fast on first false
  │
  ├─► (if pass) shared.Handle(msg, LocalPlayer)        // local execution, NOT auth-gated
  └─► (if pass) Multiplayer.Client.Send(msg) → wire

host receives → HostManager.HandleGameMessage(playerId, envelope)
  envelope.sender = playerId.String;                    // ANTI-SPOOF (overwrite)
  RoutingForMessage(playerId, envelope):
    senderAccessLevel = AccessLevelForPlayerId(playerId)   // host = President; record lookup else
    CheckAuthorizedToSendMessage(envelope.gameMessage, senderPlayerId, senderAccessLevel)  // RECEIVE-SIDE
      └─ same reflection chain
    ├─ false → Routing.Reject() → StateManager.HostRejectMessage:
    │   if msg is PropertyChange → PropertyObjectManager.HostHandlePropertyChangeRejected
    │     send corrective PropertyChange back to original sender (current host value)
    │   else → SILENT DROP, no client feedback
    └─ true → RecordState(envelope) → SendToAllExcept | SendTo(TrainCrew)

PropertyChange-specific path (when it passes the message-level [PropertyChangeAuthorizationRule]):
  PropertyChangeAuthorizationRuleAttribute.CheckAuthorization:
    StateManager.Shared.CheckAuthorizationForPropertyChange(objId, key, sender, level)
      → _propertyObjectManager.AuthorizationRequirementForPropertyWrite(objId, key)
          → records[objId].AccessControlDelegate.AuthorizationRequirementForPropertyWrite(key)
      → SenderSatisfiesAuthorizationRequirement(req, sender, level, key)   // the 8-case switch
```

**Two distinct pipelines, one common predicate.** Message-level rules use `IMessageAuthorizationRuleAttribute.CheckAuthorization` directly with bespoke logic per attribute class. Property-level rules return an `AuthorizationRequirementInfo` which `SenderSatisfiesAuthorizationRequirement` then evaluates against the same enum.

---

## `AccessLevel` enum and per-level capabilities

```csharp
public enum AccessLevel       // Game.AccessControl/AccessLevel.cs
{
    Banned        = -10,      // immediate disconnect on Authenticate (HostManager.cs:325)
    Undetermined  = 0,        // pre-Login / failure
    Passenger     = 10,       // can join, chat, look around. Limited by storage.PassengerLimit
    Crew          = 20,       // operate trains assigned to your crew (with TrainCrewMembershipRequired check)
    Dispatcher    = 30,       // (defined but UNUSED by any vanilla rule — see "Dispatcher is dead" gotcha)
    Trainmaster   = 40,       // create/edit train crews, place/remove cars, set repair multiplier, AI signals settings
    Officer       = 50,       // purchase equipment, take/repay loans, modify contracts, set time, set timetable, interchange settings
    President     = 60,       // host implicit; can grant Officer+ levels, remove player records
}
```

| Level | New capability granted |
|---|---|
| Banned | (negative) — disconnect-on-connect. Online players are queued for disconnect when access is set to Banned (`HostManager.cs:1142`). |
| Undetermined | Transient state during auth. Should never persist; logged as Warning in `StateManager.AccessLevel` getter (l.103) when Client is null. |
| Passenger | `Say`, `AddUpdateCharacter`, `UpdateCharacterPosition`, `UpdateCameraPosition`, `LedgerRequest`. Read-only spectator. |
| Crew | All "operate the train" messages: `Rerail`, `ManualMoveCar`, `SetGladhandsConnected`, `RequestSetSwitch[Unlocked]`, `AutoEngineerCommand`/`ContextualOrder`/`WaypointR*Request`, `RequestOilCar`, `FlareAddUpdate`/`Remove`, `SwitchListSetCarIds`/`ToggleCarIds`, `SetPassengerDestinations`/`AutoDestinations`. Also: PropertyChange writes on Car non-prefix keys (throttle, reverser, etc.) **with optional train-crew membership filter**. |
| Dispatcher | **Nothing.** No vanilla message uses `MinimumAccessLevel(Dispatcher)`. Per-key auth resolver handles it (l.1409) but no `IPropertyAccessControlDelegate` returns it. **Effectively dead enum value** — but reserved, mods can use it. |
| Trainmaster | `RemoveCars`, `RequestCarSetIdent`, `PlaceTrain`, `SetCarTrainCrew`, `RequestCreate/Delete/Edit/SetTimetableSymbolTrainCrew`, `RequestOps`, `WaitTime`, `SetRepairMultiplier`. Also: `extraScheduled` (Industry), `aiCrossingSignal`/`aiPassStopEnable`/`aiPassStopMinStopDur` (`_game`), Trainmaster-prefix car keys (`load.`, `ops.waybill`, `ops.repair-dest`, `_colorScheme`, `lettering.basic`, `whistle.custom`). |
| Officer | `RequestPurchaseEquipment`, `RequestLoanDelta`, `ModifyContract`, `SetTimeOfDay`, `ProgressionStartPhase`, `SetTimetable`. Also: `interchangeServeHour`/`interchangeShuffle` (`_game`), Officer-prefix car keys (`ops.sell-dest`). Also bumps the level required to grant Trainmaster/Officer access. |
| President | `RemovePlayerRecord`. Required to grant Officer or President access. Host gets this implicitly (`HostManager.AccessLevelForPlayerId` returns President for `MySteamId`, l.770). |

---

## `AuthorizationRequirement` enum and the resolver

```csharp
public enum AuthorizationRequirement                 // Game.AccessControl/AuthorizationRequirement.cs
{
    HostOnly                = 0,
    PlayerIdKey             = 1,
    MinimumLevelPassenger   = 10,
    MinimumLevelCrew        = 11,
    MinimumLevelDispatcher  = 12,
    MinimumLevelTrainmaster = 13,
    MinimumLevelOfficer     = 14,
    MinimumLevelPresident   = 15,
}
```

`AuthorizationRequirementInfo` (`Game.AccessControl/AuthorizationRequirementInfo.cs:3`):
```csharp
public struct AuthorizationRequirementInfo(AuthorizationRequirement requirement, object o = null)
{
    public readonly AuthorizationRequirement Requirement = requirement;
    public readonly object Object = o;       // optional — used only by Crew (trainCrewId string)
    public static implicit operator AuthorizationRequirementInfo(AuthorizationRequirement s) => new(s);
}
```

The implicit conversion from the bare enum is the *only* reason `Car.AuthorizationRequirementForPropertyWrite` can `return AuthorizationRequirement.MinimumLevelOfficer;` directly — the compiler boxes through the implicit ctor. **The Object payload is `null` in every vanilla case except `MinimumLevelCrew` on Car**, where it's the `trainCrewId` (a string).

### `SenderSatisfiesAuthorizationRequirement` (`StateManager.cs:1375`)

```csharp
private bool SenderSatisfiesAuthorizationRequirement(AuthorizationRequirementInfo requirement,
    PlayerId senderPlayerId, AccessLevel senderAccessLevel, string key)
{
    switch (requirement.Requirement)
    {
    case AuthorizationRequirement.HostOnly:
        if (IsHost) return senderPlayerId == PlayersManager.PlayerId;
        return false;                                    // ← off-host: ALWAYS false

    case AuthorizationRequirement.PlayerIdKey:
        if (!IsHost) return senderPlayerId.String == key;   // client: sender must own this key
        return true;                                     // ← host: ALWAYS true (no key check)

    case AuthorizationRequirement.MinimumLevelPassenger: return senderAccessLevel >= Passenger;

    case AuthorizationRequirement.MinimumLevelCrew:
    {
        if (senderAccessLevel < AccessLevel.Crew) return false;
        if (senderAccessLevel >= AccessLevel.Trainmaster) return true;          // bypass crew check
        if (_storage.TrainCrewMembershipRequired
            && requirement.Object is string trainCrewId
            && _playersManager.TrainCrewForId(trainCrewId, out var trainCrew))
            return trainCrew.MemberPlayerIds.Contains(senderPlayerId);
        return true;                                     // ← Crew when membership not required
    }

    case AuthorizationRequirement.MinimumLevelDispatcher:  return senderAccessLevel >= Dispatcher;
    case AuthorizationRequirement.MinimumLevelTrainmaster: return senderAccessLevel >= Trainmaster;
    case AuthorizationRequirement.MinimumLevelOfficer:     return senderAccessLevel >= Officer;
    case AuthorizationRequirement.MinimumLevelPresident:   return senderAccessLevel >= President;
    default: throw new ArgumentOutOfRangeException("requirement", requirement, null);
    }
}
```

### Two of the cases have HOST/CLIENT asymmetric semantics

- **`HostOnly`** returns `false` on clients regardless of who the sender is. Even if a client somehow had the host's PlayerId in `senderPlayerId` (it shouldn't — `HostManager.HandleGameMessage` overwrites `envelope.sender` from the connection's PlayerId), the `IsHost` gate fails first. **A client running `CheckAuthorizationForPropertyChange` for a HostOnly key will always see false locally** — which is why `StateManager.CheckAuthorizedToChangeProperty` (used by UI guards like `GameStorage.CanWriteBrakeForce`) returns false on clients for HostOnly keys.
- **`PlayerIdKey`** is the inverse: on the host, every key write to a PlayerIdKey-protected object is allowed; on clients, the sender must be writing their own key (the key string equals their PlayerId string). This case is **not used by any vanilla `IPropertyAccessControlDelegate`** — it's the architectural slot for "player-owned KVO" (e.g., per-player customization stored under playerId-keyed entries on a shared object). Mods that build per-player KVO can use it.

### `MinimumLevelCrew` is the only requirement with a payload-dependent branch

The `Object` payload (a `trainCrewId` string in the only vanilla case) is checked **only** if:
- The sender's level is exactly `Crew` (not Trainmaster+),
- AND `_storage.TrainCrewMembershipRequired` is true (default false),
- AND the `Object` is a string,
- AND `TrainCrewForId(trainCrewId, out crew)` resolves.

If any condition fails, Crew passes. **The toggle is in `_game` KVO key `trainCrewMembershipRequired`** (`GameStorage.cs:213-223`). Without it on, the Crew check degenerates to "level >= Crew" — exactly the same as `MinimumAccessLevel(Crew)` on a message attribute would do.

### `MinimumLevelCrew` is the ONE place a Trainmaster-level player gets a Crew-only-shape boost

> "Trainmasters can write any car key that any train crew could write." — implicit in the early-return at l.1399. Useful design: Trainmasters don't need to be on every crew to operate cars.

---

## The 5 message-level auth attributes

All implement `IMessageAuthorizationRuleAttribute`:

```csharp
public interface IMessageAuthorizationRuleAttribute
{
    bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message);
}
```

`HostManager.CheckAuthorizedToSendMessage` (`HostManager.cs:782`) iterates **all** attributes implementing this interface (`GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), inherit: true)`) and AND-reduces. Vanilla ships exactly five attributes:

### 1. `HostOnlyAuthorizationRuleAttribute` (`Game.AccessControl/HostOnlyAuthorizationRuleAttribute.cs`)

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

**21 vanilla messages use this.** All host-broadcast messages: `AddCars`, `CarSetAdd/Remove/ChangeCars/Ident/Bardo`, `BatchCarPositionUpdate`, `BatchCarAirUpdate`, `SetSwitch`, `TurntableUpdateAngle/StopIndex`, `UpdateTrainCrews`, `PlayerRecords`, `FireEvent`, `PlaySoundAtPosition/Notification`, `PostNoticeEphemeral`, `LedgerResponse`, `AutoEngineerWaypointRouteResponse/Update`, `SwitchListUpdate`. **Identical semantics to the per-key `HostOnly` requirement enum**, but enforced via the message-attribute pipeline.

**The `HostOnly` (enum) vs `[HostOnlyAuthorizationRule]` distinction:**
- `[HostOnlyAuthorizationRule]` is on `IGameMessage` structs. Checked once per message via reflection-iterated attributes.
- `AuthorizationRequirement.HostOnly` is the per-key enum value returned from `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite`. Checked only by `PropertyChange`-path message auth.
- **They share the same predicate** (`IsHost && sender == HostPlayerId`) because `SenderSatisfiesAuthorizationRequirement.HostOnly` and `HostOnlyAuthorizationRuleAttribute.CheckAuthorization` both express it. Two different code paths, identical semantics, both fail-closed off-host.

### 2. `MinimumAccessLevelAttribute` (`Game.AccessControl/MinimumAccessLevelAttribute.cs`)

```csharp
public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    => senderAccessLevel >= MinimumLevel;
```

Constructor takes `AccessLevel`. **39 vanilla usages** broken down:
- `MinimumAccessLevel(Crew)`: 16 — the operate-the-train messages.
- `MinimumAccessLevel(Trainmaster)`: 11 — train-crew admin, repair multiplier, place/remove cars, ops debug, wait time.
- `MinimumAccessLevel(Officer)`: 6 — purchase, loan, contract, time-of-day, progression, timetable.
- `MinimumAccessLevel(Passenger)`: 6 — character/avatar/say/camera/ledger.
- `MinimumAccessLevel(President)`: 1 — `RemovePlayerRecord`.
- `MinimumAccessLevel(Dispatcher)`: **0**. Dispatcher is reserved.

**Crucial subtlety: `MinimumAccessLevel(Crew)` does NOT check train-crew membership.** That check only happens via the per-key `MinimumLevelCrew` requirement on PropertyChange writes (where the `Object` payload carries the `trainCrewId`). So a Crew player on no train crew can still send `RequestSetSwitch`, `Rerail`, `ManualMoveCar`, etc. — message-level Crew auth is pure level-test.

### 3. `PropertyChangeAuthorizationRuleAttribute` (`Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs`)

```csharp
public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
{
    if (!(message is PropertyChange propertyChange)) return false;
    return StateManager.Shared.CheckAuthorizationForPropertyChange(
        propertyChange.ObjectId, propertyChange.Key, senderPlayerId, senderAccessLevel);
}
```

**Used by exactly one message: `PropertyChange`.** The attribute delegates to per-object/per-key logic. **Fails closed if the message isn't actually a `PropertyChange`** (the `if (!(message is PropertyChange))` returns false). So if a mod accidentally puts `[PropertyChangeAuthorizationRule]` on a non-PropertyChange struct, *no one* can send it. Useful safety net.

`StateManager.Shared.CheckAuthorizationForPropertyChange` requires `Shared != null`. **In edge cases (pre-Awake, post-OnDisable) this null-derefs.** No catch in the attribute. Realistic only in mod init order bugs.

### 4. `RequestSetAccessLevelRuleAttribute` (`Game.AccessControl/RequestSetAccessLevelRuleAttribute.cs`)

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public class RequestSetAccessLevelRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (!(message is RequestSetAccessLevel { AccessLevel: var accessLevel })) return false;
        AccessLevel required = ((accessLevel >= AccessLevel.Trainmaster)
            ? ((accessLevel < AccessLevel.Officer) ? AccessLevel.Officer : AccessLevel.President)
            : ((accessLevel < AccessLevel.Crew)    ? AccessLevel.Trainmaster : AccessLevel.Trainmaster));
        return senderAccessLevel >= required;
    }
}
```

**Used by exactly one message: `RequestSetAccessLevel`.** Decoded:

| Target level being granted | Required granter level |
|---|---|
| Banned (-10) | Trainmaster (40) |
| Undetermined (0) | Trainmaster |
| Passenger (10) | Trainmaster |
| Crew (20) | Trainmaster |
| Dispatcher (30) | Trainmaster |
| Trainmaster (40) | Officer (50) |
| Officer (50) | President (60) |
| President (60) | President |

**Both branches in the inner ternary for the "< Trainmaster" path return `Trainmaster`** — the conditional is dead code. The decompiler preserves the redundancy because the source likely had a comment or alignment that compressed to the same value. The effective rule: **Trainmaster grants up-to-Crew; Officer grants Trainmaster; President grants Officer/President.**

Note the symmetry break: there's no "Officer grants Officer" — granting Officer requires President. So an Officer cannot create a peer Officer; only the President can.

**Banning (target=Banned) is gated at Trainmaster level** — Trainmasters can ban Passenger/Crew/Dispatcher players. They cannot ban an Officer (granting Banned to an Officer would still pass the rule — sender just needs Trainmaster, target Banned needs Trainmaster). **There is no per-target-current-level check.** A Trainmaster can technically ban a higher-ranked Officer or President. The host-side `SetAccessLevel` (`HostManager.cs:1106`) does have one extra guard: it refuses for `HostPlayerId` ("Can't set host's access level"). But other Officers are bannable by Trainmasters — likely a bug or an unintended consequence of the symmetric rule. **Patch candidate for hardening: add a "target.AccessLevel <= sender.AccessLevel" check in `SetAccessLevel`.**

### 5. `RequestSetTrainCrewMembershipRuleAttribute` (`Game.AccessControl/RequestSetTrainCrewMembershipRuleAttribute.cs`)

```csharp
public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
{
    if (!(message is RequestSetTrainCrewMembership requestSetTrainCrewMembership)) return false;
    if (StateManager.Shared.Storage.TrainCrewMembershipManagedByTrainmaster)
        return senderAccessLevel >= AccessLevel.Trainmaster;
    if (requestSetTrainCrewMembership.PlayerId == senderPlayerId.String)
        return senderAccessLevel >= AccessLevel.Crew;
    return senderAccessLevel >= AccessLevel.Trainmaster;
}
```

**Used by exactly one message: `RequestSetTrainCrewMembership`.** Three branches:

1. If `TrainCrewMembershipManagedByTrainmaster` is on (in `_game` KVO, default false): **only Trainmaster+ can set membership for anyone, including themselves.**
2. Else, if the request targets the sender's own PlayerId: **Crew level suffices (self-join/leave).**
3. Else (sender setting another's membership): **Trainmaster level required.**

**Edge case: a Trainmaster setting their own membership** under `TrainCrewMembershipManagedByTrainmaster=false` falls into branch 2 and only needs Crew (which they have). With the toggle on, branch 1 catches it. Behaviour identical for them.

**The two `_game` toggles `trainCrewMembershipRequired` and `trainCrewMembershipManagedByTrainmaster` are independent:**
- `trainCrewMembershipRequired` (HostOnly) → toggles per-key Crew enforcement of `MinimumLevelCrew` on PropertyChange writes (the `_storage.TrainCrewMembershipRequired` check at `StateManager.cs:1403`).
- `trainCrewMembershipManagedByTrainmaster` (HostOnly) → toggles "self can change own membership" off, escalating to Trainmaster.

Both default false. Common combinations:
- Both off: free-for-all train crew membership; cars don't enforce membership.
- `Required=true, Managed=false`: players can join any crew themselves, but Crew-level operations on cars require crew membership.
- `Managed=true, Required=false`: Trainmaster manages crews; cars don't care about membership (any Crew player can drive any train).
- Both on: typical "structured operations" setup — Trainmasters assign crews, cars enforce.

---

## `IPropertyAccessControlDelegate` — the four implementations

```csharp
public interface IPropertyAccessControlDelegate
{
    AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key);
}
```

Lookup chain inside `PropertyObjectManager.AuthorizationRequirementForPropertyWrite` (`PropertyObjectManager.cs:48`):
```csharp
if (!_records.TryGetValue(id, out var value)) {
    Log.Debug("MinimumAccessLevelForPropertyWrite: Unknown object {id} {key}", id, key);
    return new AuthorizationRequirementInfo(AuthorizationRequirement.HostOnly);   // default = HostOnly
}
return value.AccessControlDelegate.AuthorizationRequirementForPropertyWrite(key);
```

**Unknown object IDs default to HostOnly.** A mod that forgets to register its KVO sees all client-side writes to it auth-rejected from clients (but applied locally first, then snapped back via the corrective PropertyChange). Symptom: client UI flicks then snaps back.

### 1. `Car` — `Model/Car.cs:42, 3112` — the prefix-array system

```csharp
private static readonly string[] HostPrefixes        = { "_", "ops.passengerMarker", "owned", "oiled", "hotbox" };
private static readonly string[] PassengerPrefixes   = { "door.", "gate." };
private static readonly string[] TrainmasterPrefixes = { "load.", "ops.waybill", "ops.repair-dest", "_colorScheme", "lettering.basic", "whistle.custom" };
private static readonly string[] OfficerPrefixes     = { "ops.sell-dest" };

public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
{
    foreach (string p in OfficerPrefixes)
        if (key.StartsWith(p)) return AuthorizationRequirement.MinimumLevelOfficer;
    foreach (string p in TrainmasterPrefixes)
        if (key.StartsWith(p)) return AuthorizationRequirement.MinimumLevelTrainmaster;
    foreach (string p in PassengerPrefixes)
        if (key.StartsWith(p)) return AuthorizationRequirement.MinimumLevelPassenger;
    foreach (string p in HostPrefixes)
        if (key.StartsWith(p)) return AuthorizationRequirement.HostOnly;
    return new AuthorizationRequirementInfo(AuthorizationRequirement.MinimumLevelCrew, trainCrewId);
}
```

### THE AUTH ORDERING PITFALL

**Resolution order is NOT level-ranked. It is hardcoded as: Officer → Trainmaster → Passenger → Host → Crew (default).** This matters because:

#### Issue 1: `_colorScheme*` is Trainmaster, but `_*` is Host — and Trainmaster is checked first

- `_colorScheme` starts with both `_colorScheme` (TrainmasterPrefixes) AND `_` (HostPrefixes).
- Trainmaster's array is iterated *before* Host's array.
- Result: `_colorScheme` and any key like `_colorSchemeLeft` resolves to Trainmaster.
- **Vanilla relies on this ordering.** Reorder the loops and `_colorScheme` becomes HostOnly.

#### Issue 2: Adding a new TrainmasterPrefix that's a prefix of another HostPrefix would silently override it

- HostPrefixes contains `oiled` (no leading underscore). If a mod patched in TrainmasterPrefixes `"oil"`, every key starting with `oil` (including the host-only `oiled`) would now be Trainmaster. **The KVO `oiled` key would become client-writable by Trainmasters, despite vanilla intent.**
- Same hazard with `hotbox` (Host) — adding TrainmasterPrefix `"hot"` would override.

#### Issue 3: Officer-prefix overlap with Trainmaster

- OfficerPrefixes is `{"ops.sell-dest"}`.
- TrainmasterPrefixes contains `"ops.waybill"` and `"ops.repair-dest"`.
- If a mod added Officer prefix `"ops"` (catch-all), every Trainmaster `ops.*` key (waybill, repair-dest) would silently be elevated to Officer-required. Worse: `ops.passengerMarker` (currently HostOnly via the explicit `"ops.passengerMarker"` HostPrefix) would also flip to Officer.

#### Issue 4: Officer's `ops.sell-dest` is checked, then Trainmaster's `ops.waybill`/`ops.repair-dest` — but they're disjoint by prefix

- `ops.sell-dest` matches only `ops.sell-dest*`. `ops.waybill*` doesn't match `ops.sell-dest`. So no conflict in vanilla.

#### Issue 5: A car key starting with `_` is HostOnly **unless** it ALSO starts with `_colorScheme`, in which case it's Trainmaster

This is the exact pattern that bit `_colorScheme` and is the reason the iteration order is what it is. **The TrainmasterPrefix `_colorScheme` exists *because* the author wanted `_` keys to be Host by default but make this one specific underscore-prefixed key Trainmaster-writable.** It is documented architectural intent.

#### Default fallback: Crew with the car's `trainCrewId`

A key that matches no prefix returns `MinimumLevelCrew` with the **car's `trainCrewId`** as the Object payload. Combined with the Crew resolver branch:
- If `_storage.TrainCrewMembershipRequired` is off → any Crew player can write.
- If on → only members of that crew (or Trainmaster+) can write.

**The default-Crew bucket is where every non-prefixed control key lives**: `throttle`, `reverser`, `locoBrake`, `trainBrake`, `horn`, `bell`, `handbrake`, `bleed`, `compressor`, `cutOut`, `idle`, `headlight`, `brakeStyle`, `mu`, `cylCock`, `f.anglecock`, `r.anglecock`, `f.cutLever`, `r.cutLever`, `f.handbrakeApplied`, `r.handbrakeApplied`, etc.

#### Mod-add prefix recipe

```csharp
// HOW to add a custom prefix scheme for a mod-defined car key:
[HarmonyPatch(typeof(Car), nameof(Car.AuthorizationRequirementForPropertyWrite))]
[HarmonyPostfix]
static void AddModPrefix(string key, ref AuthorizationRequirementInfo __result)
{
    if (key.StartsWith("mod.example.officer.")) __result = AuthorizationRequirement.MinimumLevelOfficer;
    else if (key.StartsWith("mod.example.host."))  __result = AuthorizationRequirement.HostOnly;
    // else: vanilla fallback (likely Crew with trainCrewId) survives
}
```

The postfix runs *after* the vanilla method. Check `__result.Requirement` if you want to only override the default-Crew case.

**Use `_mod.<modid>.` for HostOnly mod state on cars** — falls into the leading-`_` HostPrefix branch automatically. Don't use `_` for crew-writable mod keys.

### 2. `GameStorage` — `Game.State/GameStorage.cs:10, 598`

```csharp
public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
{
    return key switch
    {
        "interchangeServeHour"  => AuthorizationRequirement.MinimumLevelOfficer,
        "interchangeShuffle"    => AuthorizationRequirement.MinimumLevelOfficer,
        "aiCrossingSignal"      => AuthorizationRequirement.MinimumLevelTrainmaster,
        "aiPassStopEnable"      => AuthorizationRequirement.MinimumLevelTrainmaster,
        "aiPassStopMinStopDur"  => AuthorizationRequirement.MinimumLevelTrainmaster,
        _                       => AuthorizationRequirement.HostOnly,
    };
}
```

**Five exceptions to the HostOnly default.** Two Officer (interchange settings), three Trainmaster (AI behaviour). Everything else on `_game` (mode, balance, default access level, password hash, weather, wear feature, etc.) is HostOnly. This is why `GameStorage` exposes UI-time "can I write this?" helpers like `CanWriteBrakeForce` (`GameStorage.cs:328`) and `CanWriteInterchangeShuffle` (l.302) — the UI greys out controls based on this delegate's response, computed via `StateManager.CheckAuthorizedToChangeProperty("_game", key)`.

### 3. `IndustryStorageHelper` — `Model.Ops/IndustryStorageHelper.cs:14, 226`

```csharp
public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
{
    return (key == "extraScheduled")
        ? AuthorizationRequirement.MinimumLevelTrainmaster
        : AuthorizationRequirement.HostOnly;
}
```

**One exception.** `extraScheduled` is the "schedule extra interchange train" button — Trainmaster grants it. Everything else (storage dict, last-serviced timestamp, warnings, repair shop's `payRate`/`paidCurr`/`payDue` sub-keys) is HostOnly.

UI gate: `IndustryStorageHelper.CanScheduleExtra` (l.64) wraps `StateManager.CheckAuthorizedToChangeProperty(_id, "extraScheduled")`.

### 4. `StaticPropertyAccessControlDelegate` — `Game.AccessControl/StaticPropertyAccessControlDelegate.cs`

```csharp
public readonly struct StaticPropertyAccessControlDelegate(AuthorizationRequirementInfo req) : IPropertyAccessControlDelegate
{
    private readonly AuthorizationRequirementInfo _req = req;
    public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key) => _req;
}
```

**The wildcard wrapper.** Every key on the object gets the same auth. Used in two patterns:

1. **`StateManager.RegisterPropertyObject(id, kvo, requirement, requirementObject)` overload** (l.1062):
   ```csharp
   public void RegisterPropertyObject(string id, IKeyValueObject kvo, AuthorizationRequirement req, object reqObj = null)
       => RegisterPropertyObject(id, kvo, new AuthorizationRequirementInfo(req, reqObj).StaticDelegate());
   ```
   Mods or vanilla code that don't care about per-key auth use this shorthand.

2. **`PropertyObjectManager.RestoreProperties` synthetic-record fallback** (`PropertyObjectManager.cs:87`):
   ```csharp
   _records[text2] = new Record(
       new KeyValueStorage(PropertyValueConverter.SnapshotToRuntime(dictionary2)),
       new StaticPropertyAccessControlDelegate(AuthorizationRequirement.HostOnly),
       origin);
   ```
   When a snapshot contains data for an object id that hasn't been registered yet, a synthetic `KeyValueStorage` is created with HostOnly auth. **All mod state replayed from snapshot pre-registration is HostOnly until the mod registers its real KVO**, at which point `keyValueObject.ResetData(oldValues, restoreOrigin)` replays the data into the real KVO — which then carries the real delegate.

### `AccessLevelControlDelegateExt.StaticDelegate` extension

```csharp
public static IPropertyAccessControlDelegate StaticDelegate(this AuthorizationRequirementInfo a)
    => new StaticPropertyAccessControlDelegate(a);
```

Trivial helper. Enables the fluent `(AuthorizationRequirement.HostOnly).StaticDelegate()` pattern used in `RegisterPropertyObject` overload. Misnamed (it's not on `AccessLevel`); legacy-naming fallout.

---

## `HostManager.CheckAuthorizedToSendMessage` — the top-level iterator

```csharp
public static bool CheckAuthorizedToSendMessage(IGameMessage message, PlayerId senderPlayerId, AccessLevel senderAccessLevel)
{
    if (message is Transaction transaction)
    {
        foreach (IGameMessage message2 in transaction.Messages)
            if (!CheckAuthorizedToSendMessage(message2, senderPlayerId, senderAccessLevel))
                return false;
        return true;                                // ← Transaction is the AND-of-inner-auth
    }
    object[] customAttributes = message.GetType().GetCustomAttributes(
        typeof(IMessageAuthorizationRuleAttribute), inherit: true);
    for (int i = 0; i < customAttributes.Length; i++)
        if (!(customAttributes[i] as IMessageAuthorizationRuleAttribute)
              .CheckAuthorization(senderPlayerId, senderAccessLevel, message))
            return false;
    return true;
}
```

### Properties

- **Reflection-driven, no caching.** Every send and every receive runs `GetType().GetCustomAttributes` on the message struct. Hot path; vanilla doesn't optimize. Mods sending many messages per frame may want to cache.
- **AND-reduce, fail-fast on first false.** Multi-attribute messages need every attribute to pass. **Vanilla messages all use exactly one auth attribute** (no AND-composition in production). The mechanism exists if needed.
- **`Transaction` is recursive AND.** A 100-message transaction with one bad message rejects the whole transaction. The host doesn't partial-apply. Note: the recursion uses the same sender/level — there's no "trust elevation" within a transaction.
- **`inherit: true`** means base-class attributes count. No `IGameMessage` struct has a base struct (structs can't inherit), but interfaces can carry attributes. **`IGameMessage` itself has only `[Union(...)]` attributes.** No accidental inherited auth attributes in vanilla.
- **No attributes = no rules = pass.** A mod adding an `IGameMessage` struct without any auth attribute is auth-allowed for every player from Passenger up. **Vanilla has no IGameMessage without an auth attribute.**

### Where it runs

| Site | File:line | Purpose |
|---|---|---|
| Client send (pre-network) | `StateManager.ApplyLocal` calling `StateManager.CheckAuthorizedToSendMessage` (l.1359) | Prevents send + skips local handler |
| Host receive (per envelope) | `HostManager.RoutingForMessage` (l.806-813) | Decides Reject vs Allow → AllExcept/TrainCrew |
| UI gate | `StateManager.CheckAuthorizedToChangeProperty(id, key)` (l.1364) constructs a synthetic `PropertyChange` to ask "could I write this?" | Greys out UI controls |

`StateManager.CheckAuthorizedToChangeProperty` (l.1364):
```csharp
public static bool CheckAuthorizedToChangeProperty(string id, string key)
    => CheckAuthorizedToSendMessage(new PropertyChange(id, key, default(NullPropertyValue)));
```

**Constructs a fake PropertyChange with a NullPropertyValue and asks "would auth pass?"** Used by `GameStorage.CanWriteBrakeForce`, `CanWriteInterchangeShuffle`, `IndustryStorageHelper.CanScheduleExtra`, etc. **The value field doesn't affect auth** — only the (objectId, key) pair drives the per-key delegate. NullPropertyValue is a sentinel for "I'm just checking."

---

## `HostManager.HandleGameMessage` — host-side auth flow

```csharp
public void HandleGameMessage(PlayerId playerId, GameMessageEnvelope envelope)
{
    envelope.sender = playerId.String;                    // ANTI-SPOOF: overwrite from connection
    Routing routing = RoutingForMessage(playerId, envelope);
    if (routing.route == Routing.Route.Reject)
    {
        StateManager.Shared.HostRejectMessage(playerId, envelope.gameMessage);
        return;
    }
    RecordState(envelope);
    switch (routing.route)
    {
    case Routing.Route.AllExcept:  SendToAllExcept(envelope, new PlayerId(routing.id)); break;
    case Routing.Route.TrainCrew:  SendTo(TrainCrewPlayerIds(routing.id), envelope); break;
    }
}
```

```csharp
private Routing RoutingForMessage(PlayerId senderPlayerId, GameMessageEnvelope envelope)
{
    AccessLevel senderAccessLevel = AccessLevelForPlayerId(senderPlayerId);
    if (!CheckAuthorizedToSendMessage(envelope.gameMessage, senderPlayerId, senderAccessLevel))
    {
        Log.Warning("Reject message {message}; authorization check failed: {senderPlayerId}",
            envelope.gameMessage.GetType(), senderPlayerId);
        return Routing.Reject();
    }
    Routing result = Routing.AllExcept(senderPlayerId.String);
    if (envelope.gameMessage is SwitchListUpdate slu)
        return Routing.TrainCrew(slu.TrainCrewId);
    return result;
}
```

### Critical security properties

1. **`envelope.sender` is always overwritten with the connection's PlayerId** — clients cannot spoof the sender field. The original sender field set by `GameClient.Send` is ignored host-side.
2. **`AccessLevelForPlayerId(playerId)`** (`HostManager.cs:768`) returns `President` for the host's own SteamId regardless of records. For other players, looks up `_playerRecords[playerId].AccessLevel`. **Failure returns `Undetermined`** with a `Log.Error("Failed to find access level for player {playerId}", playerId)` — Undetermined fails every level check.
3. **Host-side rejection only sends a corrective broadcast for `PropertyChange`.** All other rejected messages are dropped silently. See [§ rejection paths](#rejection-paths).

### Routing exception

Only `SwitchListUpdate` deviates from the default `AllExcept(sender)` routing. It's broadcast to `TrainCrewPlayerIds(crewId)` only — members of the crew the switchlist belongs to.

---

## TrainCrew authorization deep-dive

The TrainCrew system intersects auth in two unrelated places:

### 1. `RequestSetTrainCrewMembership` rule — who can change crew membership

Covered above in [§ 5. RequestSetTrainCrewMembershipRuleAttribute](#5-requestsettraincrewmembershipruleattribute). The `_game` toggle `trainCrewMembershipManagedByTrainmaster` selects between "self-service" and "Trainmaster-only" management.

### 2. `MinimumLevelCrew` per-key requirement — Crew player + crew membership

When `Car.AuthorizationRequirementForPropertyWrite` returns `MinimumLevelCrew` with the car's `trainCrewId` as the Object payload, the resolver **may** check that the sender is a member of that crew — but only if `_storage.TrainCrewMembershipRequired` is true AND the sender's level is exactly Crew (Trainmaster+ bypasses).

**The two toggles are independent:**

| `MembershipRequired` | `ManagedByTrainmaster` | Effect |
|---|---|---|
| false | false | Anyone Crew+ can drive any train. Anyone Crew+ can self-join any crew. |
| true | false | Crew can only drive trains assigned to crews they're on. Anyone Crew+ can self-join any crew (without being a member, they can't drive). |
| false | true | Anyone Crew+ can drive any train. Trainmaster manages all crew membership. |
| true | true | Crew can only drive crews they're on. Trainmaster manages who's on which crew. |

Cross-link: this matters for `players-traincrew.md` (when that crib sheet lands). The membership data lives in `Snapshot.TrainCrew.MemberPlayerIds` (`Snapshot.cs:141`); the runtime model is `PlayersManager.TrainCrewForId(id, out crew)`.

### `MinimumAccessLevel(Crew)` (message attribute) ≠ `MinimumLevelCrew` (per-key requirement)

**They have different semantics.** Critical to understand:

| Mechanism | Train-crew membership check? | Used by |
|---|---|---|
| `[MinimumAccessLevel(AccessLevel.Crew)]` on a message | **NO.** Pure `senderAccessLevel >= Crew` (`MinimumAccessLevelAttribute.cs:18`). | All Crew-level messages: `RequestSetSwitch`, `Rerail`, `ManualMoveCar`, `RequestOilCar`, etc. |
| `MinimumLevelCrew` requirement on a key | **YES, conditionally.** Membership check fires if `_storage.TrainCrewMembershipRequired && Object is string trainCrewId`. | PropertyChange writes on Car non-prefix keys (`throttle`, `reverser`, etc.). |

**Implication:** A Crew player on no train crew can `RequestSetSwitch` for any switch in the world (Crew message-level passes), but cannot move the throttle on a train assigned to a crew they're not on (PropertyChange `throttle` per-key fails the membership check). **This asymmetry is by design** — switches aren't owned by crews; trains are.

---

## Side-channel auth bypass patterns

The most subtle attack surface is when a Crew-writable KVO key triggers a host-side observer that writes a HostOnly key. The client never directly writes the HostOnly key — they trigger a state change on the host. **These are not bugs; they are the intended pattern for client-driven mutation of host-authoritative state.** But they look like auth bypasses.

### Pattern 1: `cutLever` → `_*.coupled`

`Car.cs:2577-2613`:
```csharp
private void HandleCutLeverValue(End end, float value)
{
    if (!((double)value < 0.5))                          // edge-trigger above 0.5
    {
        LogicalEnd logicalEnd = EndToLogical(end);
        HandleOpenCoupler(logicalEnd);                   // ← writes HostOnly state HOST-SIDE
        LeanTween.delayedCall(1f, (Action)delegate
        {
            ApplyEndGearChange(logicalEnd, EndGearStateKey.CutLever, 0f);
        });
    }
}

private void HandleOpenCoupler(LogicalEnd logicalEnd)
{
    if (StateManager.IsHost && this[logicalEnd].IsCoupled)   // host-only side effect
    {
        // resolve neighbour car, then:
        car.ApplyEndGearChange(LogicalEnd.B, EndGearStateKey.IsCoupled, false);
        car2.ApplyEndGearChange(LogicalEnd.A, EndGearStateKey.IsCoupled, false);
    }
}
```

- Client writes `f.cutLever = 1f` (Crew auth — passes the default-Crew bucket).
- PropertyChange broadcasts; host's KVO observer fires `HandleCutLeverValue`.
- Host (only) calls `HandleOpenCoupler`, which writes `_f.coupled = false` and `_r.coupled = false` (HostOnly keys; host-direct writes bypass auth).
- HostOnly keys broadcast to all clients via the standard PropagateSetValueLocal path.
- 1 second later, the cut-lever value is reset to 0 client-side via `LeanTween.delayedCall`.

**The client never directly writes HostOnly state.** The host translates a Crew-level intent into a HostOnly state mutation. **Auth is preserved.**

### Pattern 2: `anglecock` → `_*.airConnected`

Same shape: client-writable `f.anglecock`/`r.anglecock` (Crew via default-Crew bucket on Car) → host observer `Car.UpdateAirConnection` → host-side write to HostOnly `_f.airConnected`/`_r.airConnected`.

### Pattern 3: `bleed` → host SetDelayed clear

`Car.cs:1685` (in observer for `bleed`):
```csharp
air.BleedBrakeCylinder();
if (StateManager.IsHost)
    KeyValueObject.SetDelayed("bleed", Value.Null(), 0.5f);
```

Client writes `bleed = 1f` (Crew default-bucket). Host observer triggers brake-cylinder bleed *and* schedules a host-side clear of the same key 500ms later. The "clear-write" is the host's authorship of the cleared state — even though `bleed` is a Crew-writable key, the host owns the clearing. (And `SetDelayed` always uses Local origin — see [KVO Patterns § SetDelayed](kvo-patterns.md#setdelayed-keyvalueobjectcs90).)

### Pattern 4: explicit Request* → host-side HostOnly write

`RequestOilCar` (Crew auth) is the canonical "explicit request message" version. Host receives, calls `_trainController.HandleRequestOilCar(carId, amount)` → `Car.OffsetOiled(amount)` → KVO write of HostOnly `oiled` key. **Same effective shape as Pattern 1 but with an IGameMessage in the middle instead of an observer.**

The two shapes differ in:
- **Cost**: Pattern 1-3 use existing PropertyChange + KVO observer infra (zero new IGameMessage types). Pattern 4 needs a dedicated message struct + Union tag + handler branch.
- **Discoverability**: Pattern 4 is grep-able via `IGameMessage` listings. Patterns 1-3 are buried in observer code, harder to audit.
- **Auth granularity**: Pattern 4's auth attribute can encode arbitrary rules. Patterns 1-3 inherit the Crew default-bucket of the trigger key — no way to require Trainmaster for a cut-lever pull, for example.
- **Acknowledgement**: Pattern 1-3 don't have a rejection path (the client write succeeds; host just doesn't trigger the side-effect if rejected, but the rejection itself comes from the trigger key's auth, not the side-effect's). Pattern 4 has the standard PropertyChange-only rejection.

### Pattern 5: `SetSwitch.Requester` is informational

`SetSwitch` is HostOnly. The struct includes a `Requester: PlayerId` field, but **it's not used for auth** (the host has already authorized via `RequestSetSwitch` Crew check before emitting `SetSwitch`). Pure log/info value. **Setting a fake PlayerId in a host-side patch doesn't bypass anything** — the message is HostOnly, only the host can send it. Don't mistake this field for an auth signal.

### Pattern 6: SwitchListUpdate's TrainCrew routing

`SwitchListUpdate` is HostOnly + special routing. The host filters recipients to `TrainCrewPlayerIds(crewId)`. **A non-member who somehow received the message would still pass through `StateManager.Handle`** — the receive path also gates with `if (PlayersManager.MyTrainCrew?.Id == switchListUpdate.TrainCrewId)` (`StateManager.cs:844`). Defense-in-depth: routing filter + receiver-side filter.

---

## Mod-defined auth — registering custom prefixes and delegates

### Custom prefix on Car (or Industry) keys

Patch `Car.AuthorizationRequirementForPropertyWrite` postfix:

```csharp
[HarmonyPatch(typeof(Car), nameof(Car.AuthorizationRequirementForPropertyWrite))]
[HarmonyPostfix]
static void ModAuthPrefix(string key, ref AuthorizationRequirementInfo __result, Car __instance)
{
    // only override the default-Crew fallback; don't stomp explicit prefix matches
    if (__result.Requirement != AuthorizationRequirement.MinimumLevelCrew) return;
    if (key.StartsWith("mod.example.officer.")) __result = AuthorizationRequirement.MinimumLevelOfficer;
    else if (key.StartsWith("mod.example.host.")) __result = AuthorizationRequirement.HostOnly;
}
```

**The static prefix arrays cannot be mutated at runtime safely** — they're `static readonly string[]`. Reflection-mutating them works mid-session but other patches may have cached. Patch the method instead.

### Custom IPropertyAccessControlDelegate on a mod-owned KVO

```csharp
public class ModAccessControl : IPropertyAccessControlDelegate
{
    public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
    {
        // any mapping you like
        if (key == "adminOnly") return AuthorizationRequirement.MinimumLevelOfficer;
        return AuthorizationRequirement.HostOnly;
    }
}

// register at MapWillLoad or later:
StateManager.Shared.RegisterPropertyObject("mod.example.state", myKvo, new ModAccessControl());
```

**Registration must happen before any client writes are expected.** If the snapshot already contains data for this id (from a previous host session that loaded with the mod), the snapshot data lands in a synthetic record with HostOnly auth — until the mod registers the real KVO, at which point the data replays into the real KVO with the deferred origin (`PropertyObjectManager.RegisterPropertyObject` l.27).

**No vanilla check validates the delegate.** A mod can deliberately or accidentally register a HostOnly-only delegate for keys that should be more permissive — clients would silently see all writes rejected.

### Custom IGameMessage with custom auth

```csharp
// 1. Define the auth rule (or reuse existing):
[AttributeUsage(AttributeTargets.Struct)]
public class ModEngineerAuthRule : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        // arbitrary logic — e.g., check a mod-side allowlist
        return senderAccessLevel >= AccessLevel.Crew && ModAllowlist.Contains(senderPlayerId);
    }
}

// 2. Apply to the message:
[ModEngineerAuthRule]
[MessagePackObject(false)]
public struct ModEngineerCommand : IGameMessage { /* ... */ }

// 3. Register Union tag — see Request Messages § "Adding a mod request message".
```

The reflection iterator in `HostManager.CheckAuthorizedToSendMessage` discovers any class implementing `IMessageAuthorizationRuleAttribute`, so custom attributes work without further registration. **They run in declaration order on the type and AND-reduce.**

### Custom property-change auth gate (no mod IGameMessage needed)

If your mod just needs to write a custom auth check for a key it owns, write it inside `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite`:

```csharp
public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
{
    if (key.StartsWith("priv."))
        // pass an optional Object payload — only Crew uses it (as trainCrewId)
        return new AuthorizationRequirementInfo(AuthorizationRequirement.MinimumLevelCrew, "specific-crew-id");
    return AuthorizationRequirement.HostOnly;
}
```

The `Object` payload is the only way to inject context into the resolver. Beyond `MinimumLevelCrew`'s trainCrewId case, it's ignored — adding new payload-using requirements requires patching `SenderSatisfiesAuthorizationRequirement`.

---

## Static helpers & their use sites

| Helper | File:line | What it does |
|---|---|---|
| `StateManager.IsHost` | `StateManager.cs:76` | `Multiplayer.IsHost`. The chief gate-test. |
| `StateManager.AccessLevel` | `StateManager.cs:92` | Host = President; Client = `Multiplayer.Client.AccessLevel`. |
| `StateManager.AssertIsHost()` | `StateManager.cs:1347` | `if (!IsHost) throw new Exception("Only host can call");`. Defense-in-depth on host-side methods (e.g., `RepairTrack.HandleSetMultiplier`). |
| `StateManager.DebugAssertIsHost()` | `StateManager.cs:1355` | **Empty.** Decompiled from a `[Conditional("DEBUG")]` method that compiled out. Don't rely. |
| `StateManager.CheckAuthorizedToSendMessage(message)` | `StateManager.cs:1359` | Wraps `HostManager.CheckAuthorizedToSendMessage(message, MyId, MyLevel)`. Used pre-send. |
| `StateManager.CheckAuthorizedToChangeProperty(id, key)` | `StateManager.cs:1364` | Constructs a synthetic PropertyChange with NullPropertyValue and asks `CheckAuthorizedToSendMessage`. UI guard. |
| `StateManager.CheckAuthorizationForPropertyChange(id, key, sender, level)` | `StateManager.cs:1369` | The instance method PropertyChangeAuthorizationRule delegates to. |
| `HostManager.CheckAuthorizedToSendMessage(message, sender, level)` | `HostManager.cs:782` | The static reflection iterator. Doubles as both client send-time and host receive-time check. |
| `AccessLevelControlDelegateExt.StaticDelegate(this AuthorizationRequirementInfo)` | `Game.AccessControl/AccessLevelControlDelegateExt.cs:5` | Wraps a requirement as a degenerate `IPropertyAccessControlDelegate`. |
| `AuthorizationRequirementInfo` implicit op from `AuthorizationRequirement` | `AuthorizationRequirementInfo.cs:9` | Allows bare-enum returns from the per-key delegate. |
| `PropertyObjectManager.AuthorizationRequirementForPropertyWrite(id, key)` | `PropertyObjectManager.cs:48` | Looks up the delegate; defaults to HostOnly if id unknown. |
| `PropertyObjectManager.HostHandlePropertyChangeRejected(playerId, change)` | `PropertyObjectManager.cs:106` | Sends corrective PropertyChange back with current host value. |

### `StaticDelegate` extension call site sweep

```bash
grep -rn "\.StaticDelegate()" Railroader-ILSPY/Assembly-CSharp/
```
Returns one site only:
```csharp
// StateManager.cs:1064
RegisterPropertyObject(id, keyValueObject, new AuthorizationRequirementInfo(req, reqObj).StaticDelegate());
```

The shorter `RegisterPropertyObject(id, kvo, AuthorizationRequirement)` overload uses it. **No other vanilla code calls `StaticDelegate()` directly.** Mods can use it freely; the underlying `StaticPropertyAccessControlDelegate` ctor is also public.

---

## Rejection paths

| Rejection point | Detection | Client visibility | Mod-extension |
|---|---|---|---|
| Client-side pre-send (`StateManager.ApplyLocal`) | `CheckAuthorizedToSendMessage` returns false | Log warning only. Local handler does NOT run. | Patch `ApplyLocal` prefix to log; postfix can't detect (function returns void without distinguishing "not authorized" from "Shared null"). |
| Host-side per-message (`HostManager.RoutingForMessage`) | `CheckAuthorizedToSendMessage` returns false on host | For PropertyChange: corrective broadcast back to sender (`HostHandlePropertyChangeRejected`, `PropertyObjectManager.cs:106`). **For all other messages: silent drop.** Log warning host-side only. | Patch `HostRejectMessage` to add `Messenger.Send` of a custom rejection event for client mods to subscribe. |
| `SenderSatisfiesAuthorizationRequirement` per-key (PropertyChange flow) | Returns false from the 8-case switch | Same as above (PropertyChange corrective). | Patch this method to add custom requirements or relax existing rules. |
| `Authenticate` (`HostManager.cs:535`) | Connection-time auth fail | Disconnect with `AccessDenied` / `PasswordRequired` / `NoMorePassengers`. | Patch `Authenticate` to add external whitelist / OAuth / etc. |
| `AccessLevel.Banned` post-Authenticate | `HandleMessageAnonymous` (l.325) | Disconnect with `AccessDenied`. | n/a |
| Online player bumped to Banned | `SetAccessLevel` queues for disconnect (l.1142) | Disconnect with `AccessDenied`. | n/a |

**Only `PropertyChange` rejections produce client feedback.** This is the dominant gap. Every other message type fails silently — the client UI may have already optimistically updated. Mods needing reliable acknowledgement must build their own `Request*` + `Response*` ack/nack pair with correlation IDs.

`StateManager.SendFireEvent(new RequestRejected())` (l.952) exists as a `FireEvent` enum case (event code 2) but **is not invoked by any vanilla auth-rejection path**. The Messenger event `Game.Events.RequestRejected` is defined but never sent. Vestigial.

---

## Patch candidates

| Method | Why patch |
|---|---|
| `HostManager.CheckAuthorizedToSendMessage` (static, `HostManager.cs:782`) | Universal auth chokepoint. Affects both client send-time and host receive-time. Useful for logging every auth check, adding global rate-limiting, or short-circuiting specific message types. |
| `StateManager.SenderSatisfiesAuthorizationRequirement` (`StateManager.cs:1375`) | Add new requirement enum cases or relax/tighten existing ones. The 8-case switch is the deepest auth predicate. |
| `StateManager.CheckAuthorizationForPropertyChange` (`StateManager.cs:1369`) | PropertyChange-specific auth resolver. Patch to add per-id or per-key special-cases. |
| `StateManager.HostRejectMessage` (`StateManager.cs:1422`) | Add custom rejection signaling. E.g., `Messenger.Send` a `Game.Events.RequestRejected` for client mods to react. |
| `PropertyObjectManager.HostHandlePropertyChangeRejected` (`PropertyObjectManager.cs:106`) | Modify the corrective broadcast (e.g., suppress for trusted players). |
| `Car.AuthorizationRequirementForPropertyWrite` (`Car.cs:3112`) | Add mod-prefix → auth mappings. **Patch postfix** and only override when `__result.Requirement == MinimumLevelCrew` to avoid stomping explicit prefix matches. |
| `GameStorage.AuthorizationRequirementForPropertyWrite` (`GameStorage.cs:598`) | Add mod-defined `_game` keys with custom auth. |
| `IndustryStorageHelper.AuthorizationRequirementForPropertyWrite` (`IndustryStorageHelper.cs:226`) | Add mod-defined industry keys with custom auth. |
| `HostManager.AccessLevelForPlayerId` (`HostManager.cs:768`) | Override per-player access level lookup (e.g., temporary elevation for an event). Affects every receive-time auth check. |
| `HostManager.SetAccessLevel` (`HostManager.cs:1106`) | Add target-current-level bound check (e.g., "Trainmaster cannot ban Officer+"). See [§ 4 RequestSetAccessLevelRule gotcha](#4-requestsetaccesslevelruleattribute-gameaccesscontrolrequestsetaccesslevelruleattributecs). |
| `HostManager.Authenticate` (`HostManager.cs:535`) | Custom connection-time auth. External allowlist / OAuth / etc. |
| `HostManager.RoutingForMessage` (`HostManager.cs:806`) | Add custom routing rules beyond the SwitchListUpdate special case. |
| `RequestSetAccessLevelRuleAttribute.CheckAuthorization` | Replace the granter-level mapping. E.g., make Officer-grants-Officer legal. |
| `RequestSetTrainCrewMembershipRuleAttribute.CheckAuthorization` | Replace self-vs-other rules (e.g., always allow self-leave even if Managed-by-Trainmaster). |

---

## MP authority — auth surface summary

| Layer | Where checked | What's gated |
|---|---|---|
| **Connection** | `HostManager.ShouldAcceptConnection` (always true) + `Authenticate` | Who can join the session; password / record matching; banned-on-connect drop |
| **Message-level** | `HostManager.CheckAuthorizedToSendMessage` (reflection over attributes) | Per-message-type send permission; runs both pre-send and post-receive |
| **Property-level (per-key)** | `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite` → `SenderSatisfiesAuthorizationRequirement` | PropertyChange writes per (objectId, key) |
| **Train-crew membership** | Inside `MinimumLevelCrew` resolver branch (l.1399-1407), conditional on `_storage.TrainCrewMembershipRequired` | Crew-level operations on cars assigned to crews the player is not on (PropertyChange only — not message-attribute Crew) |
| **Sender spoofing** | `HostManager.HandleGameMessage` overwrites `envelope.sender` (l.703) | Anti-spoof for the sender field on `GameMessageEnvelope` |
| **Runtime IsHost gate inside handler** | E.g., `RepairTrack.HandleSetMultiplier`'s `AssertIsHost` | Defense-in-depth for direct C# calls bypassing the message pipeline |

---

## Gotchas

- **`CheckAuthorizedToSendMessage` is reflection-driven without caching.** `GetType().GetCustomAttributes` runs on every send and every receive. Hot path; not optimized in vanilla. Mods spinning up many messages per frame should consider caching the attribute lookup (or memoize via static dict keyed on `Type`).
- **`HostOnly` (enum) and `[HostOnlyAuthorizationRule]` (attribute) are NOT the same construct.** They share the predicate but are checked via different code paths. Only the attribute is on `IGameMessage` structs; only the enum value comes from `IPropertyAccessControlDelegate`. **Document both as "host only" but know they have separate enforcement.**
- **`PlayerIdKey` requirement is unused by any vanilla `IPropertyAccessControlDelegate`.** It's the architectural slot for per-player KVO (`object.players[playerId] = something`) but vanilla never uses it. Mod-extension point.
- **Dispatcher (level 30) is unused.** `MinimumLevelDispatcher` exists in the resolver, but no message uses `MinimumAccessLevel(Dispatcher)` and no `IPropertyAccessControlDelegate` returns it. **The level number 30 is a "reserved" gap between Crew and Trainmaster** — mods can use it to slot a custom role between them.
- **`MinimumAccessLevel(Crew)` (message attribute) does NOT check train-crew membership.** Only `MinimumLevelCrew` (per-key requirement on PropertyChange) does, and only conditionally. **A non-member-of-anything Crew player can `Rerail`, `RequestSetSwitch`, etc.** — they just can't move the throttle on a foreign crew's train.
- **The Car prefix-array order is hardcoded Officer → Trainmaster → Passenger → Host → fallback Crew.** Trainmaster's `_colorScheme` correctly overrides Host's `_` because of this order. **Re-ordering the loops would break vanilla.** Mod patches that wrap the method should preserve this resolution priority.
- **Adding a prefix to one of the 4 arrays at the wrong spot can silently override another array's match.** E.g., adding `"oil"` to TrainmasterPrefixes overrides HostPrefix `"oiled"`. Always test mod prefix additions against the existing arrays for `StartsWith` collisions.
- **Default-bucket fallback for unmatched Car keys is `MinimumLevelCrew(trainCrewId)`** — a mod-added key with no matching prefix is Crew-writable by *anyone* on the assigned crew (or any Crew if `TrainCrewMembershipRequired` is off). To keep mod state HostOnly, **prefix with `_` or `_mod.<modid>.`**
- **Unknown object IDs default to HostOnly.** A mod KVO that never registered (or whose `id` is misspelled in PropertyChange messages) sees all client writes auth-rejected from clients but applied locally first — symptom is "UI flicks then snaps back" via the corrective broadcast.
- **`RequestSetAccessLevelRule` allows Trainmasters to ban Officer+ players.** The rule is target-level-only; no current-level check. The `SetAccessLevel` host method only refuses to ban the host. **Patch `SetAccessLevel` if you want strict promotion-up-only-ban-down rules.**
- **`RequestSetAccessLevelRule`'s "< Trainmaster" branch returns `Trainmaster` in both sub-branches.** The `<Crew` ternary is dead code in the decompile. Source likely has an explicit `Trainmaster : Trainmaster` for clarity. Don't trust the dead branch as a behavior signal.
- **There's no "Officer grants Officer" rule.** Promoting someone to Officer requires President. An Officer cannot create a peer Officer. Symmetry break vs Trainmaster (who *can* grant Trainmaster via the `<Trainmaster` branch returning Trainmaster).
- **`PropertyChangeAuthorizationRuleAttribute.CheckAuthorization` returns false if `message is not PropertyChange`.** Putting this attribute on a non-PropertyChange struct silently denies all sends. Defensive.
- **`PropertyChangeAuthorizationRuleAttribute` calls `StateManager.Shared` without null-check.** A mod sending a PropertyChange before `Shared` is set null-derefs. Realistic only in pathological init order.
- **`HostManager.CheckAuthorizedToSendMessage` recurses on `Transaction`** with the same sender/level. **A Transaction that includes a Trainmaster-required message inside a Crew-sender's transaction is rejected entirely.** Crew cannot smuggle Trainmaster ops via Transaction packaging.
- **Transactions are rejected wholesale on first inner failure.** No partial-apply. Mods batching mixed-auth ops should pre-validate or split by sender capability.
- **`Snapshot` is in the `IGameMessage` Union (tag 10) but has no auth attribute and is never dispatched as a bare IGameMessage.** It's wrapped in `SnapshotEnvelope` (an `INetworkMessage`) and handled at the network layer. **Auth check on Snapshot returns true** (no attributes to fail) — but it never reaches the auth check via `ApplyLocal` because there's no dispatcher branch for it.
- **`Transaction` itself has no auth attribute** (`Transaction.cs:6`). Auth defers to the recursive AND of inner messages. **A mod that wraps Transactions but adds extra auth on the wrapper attribute would constrain — or relax — the inner auth.** Useful for "audit-only" wrappers.
- **`CharacterPosition` (a payload struct, NOT an IGameMessage) has `[MinimumAccessLevel(Passenger)]`** — `Game.Messages/CharacterPosition.cs:7`. **The attribute is dead** — auth attributes are only checked by `HostManager.CheckAuthorizedToSendMessage` which iterates `IGameMessage`'s attribute list, not nested struct's. Vestigial.
- **`UpdateCameraPosition` is `Passenger`-auth on the `Movement` channel** — any Passenger spectator can spam unreliable camera updates. Bandwidth concern in busy lobbies; auth design intentionally permissive.
- **`StateManager.AssertIsHost` throws.** Caught in some places (`try { ... } catch { }`), uncaught in others. Defense-in-depth, but be aware.
- **`StateManager.DebugAssertIsHost` is empty** (`StateManager.cs:1355`). Was almost certainly `[Conditional("DEBUG")]` source that compiled to a no-op. **Don't rely on it for any debug-only enforcement.**
- **`PropertyObjectManager` uses `Log.Debug` (low priority) for "Unknown object" auth lookups** but `Log.Warning` for "Unknown object" property changes. Auth misses are quiet; the actual message handling is louder. Asymmetry that complicates debugging "why is my mod's PropertyChange dropping silently?"
- **`HostManager.AccessLevelForPlayerId` returns Undetermined (with Log.Error) for unknown playerIds.** Undetermined fails every level check. So a player whose record was somehow removed mid-session sees all their messages rejected on the host until disconnected. Recovery: disconnect/reconnect re-registers via `Authenticate`.
- **`HostManager.ValidateUsername` always returns true** (`HostManager.cs:596-613`) — the duplicate-name and whitespace-name "would fail" log strings are vestigial. **Two players with the same name both connect.**
- **The host's own client (`LocalGameClient` loopback) goes through the same auth pipeline.** Singleplayer experiences the full reflection-iterator auth machinery. Host's `AccessLevel.President` always passes, but the cost is real (per-message reflection lookup).
- **`PropertyChangeAuthorizationRule` runs on every PropertyChange host-side.** With the per-tick `BatchCarPositionUpdate` and similar bursting, the auth iterator + delegate lookup is the hot path of multiplayer. Mods adding heavy logic to `IPropertyAccessControlDelegate` implementations slow down this path.
- **`StaticPropertyAccessControlDelegate` is a struct, not a class.** Boxed when used as `IPropertyAccessControlDelegate`. Allocates per call to `RegisterPropertyObject` overload. Negligible but worth knowing.
- **`AuthorizationRequirementInfo`'s implicit cast from `AuthorizationRequirement` enum** allows the terse `return AuthorizationRequirement.HostOnly;` — but the resulting struct has `Object = null`. Only `MinimumLevelCrew` reads `Object`, so this is fine for every other case.
- **Banning a player while they're online queues their disconnect for *next* `HandleMessage` invocation** (`HostManager.cs:264-268`). There's a window between the access-level change and the disconnect where the player is still receiving (and could be sending) messages with the new Banned level — but Banned passes no auth check. **Effective immediate.**
- **`SetAccessLevel` does not announce or persist for the host's own playerId** — `if (HostPlayerId == playerId) { Log.Warning("Ignore SetAccessLevel for host"); ... return; }`. Host is permanently President.

---

## Init order

1. **Pre-map** — mod assemblies load. `StateManager.Shared` may be null. Auth attributes on mod IGameMessages are reflection-discoverable but not yet usable (no `MessagepackSupport.Setup`).
2. **`StateManager.OnEnable`** — `Shared = this`. From here, the auth pipeline is callable.
3. **`Multiplayer.PrepareHostIfNeeded`** + **`Multiplayer.ConnectClient`** — `HostManager` and `ClientManager` exist. `MessagepackSupport.Setup` runs (in `GameClient.Setup`). Auth attributes can be serialized via MessagePack now.
4. **`StateManager.OnMapWillLoad`** — `_storage = new GameStorage(kvo)` registers `_game` with `GameStorage` as its `IPropertyAccessControlDelegate`. **From this point, `_game` per-key auth works.**
5. **Cars/Industries spawn** — each `Awake` registers its KVO + delegate. Per-object auth comes online.
6. **Snapshot ingest (`PopulateFromRemoteSnapshot`)** — for any objectId in the snapshot that isn't yet registered, a synthetic `KeyValueStorage` with `StaticPropertyAccessControlDelegate(HostOnly)` lands in `_records`. **All such pre-registration data is HostOnly until the real KVO registers and `ResetData` replays.**
7. **`PropertiesDidRestore`** Messenger fires (after the snapshot transaction commits). All KVO state plus auth delegates wired.
8. **Client status hits `Active`** — `Multiplayer.Client.AccessLevel` is now valid. Mods that want to send messages can do so. Pre-Active sends are silent-dropped by `GameClient.Send`.

**Mods registering `IPropertyAccessControlDelegate` implementations:** do it during step 4-5 (in `Awake` of an early-load MonoBehaviour). Late registration works (the snapshot replay handles it) but creates a window where the synthetic HostOnly delegate is live.

**Mods registering custom `IGameMessage` types:** register the Union tag *before* `MessagepackSupport.Setup` runs (step 3). After that, the resolver is locked. Mods piggybacking on `PropertyChange` instead don't have this constraint — the Union tag is already registered for `PropertyChange`.

---

## Cross-references

- [Multiplayer Core § Authentication](multiplayer-core.md#authentication-hostmanagerauthenticate-hostmanagercs535) — connection-time auth (the layer below message/property auth).
- [Multiplayer Core § MP authority summary](multiplayer-core.md#mp-authority-summary) — high-level table; this doc is the depth-dive.
- [State Manager § Auth resolver](state-manager.md#auth-resolver) — narrative version of `SenderSatisfiesAuthorizationRequirement`.
- [State Manager § ApplyLocal flow](state-manager.md#applylocal-and-the-handle-dispatcher) — the send-side auth gate in context.
- [State Manager § HostHandlePropertyChangeRejected](state-manager.md#hosthandlepropertychangerejected--the-correction-loop) — the corrective broadcast.
- [State Manager § Snapshot/late-join](state-manager.md#snapshot--late-join) — synthetic-record HostOnly fallback for pre-registration data.
- [Request Messages § Authorization attribute summary](request-messages.md#authorization-attribute-summary) — per-message catalog cross-link.
- [Request Messages § Side-channel patterns](request-messages.md#side-channel-patterns) — companion catalog of the cut-lever / anglecock / bleed / RequestOilCar shapes.
- [KVO Patterns § HostOnly](kvo-patterns.md#hostonly--what-it-is-where-its-enforced) — the shorter version of the dual-meaning HostOnly explanation.
- [KVO Patterns § IPropertyAccessControlDelegate](kvo-patterns.md#ipropertyaccesscontroldelegate--per-key-auth) — the per-key auth from the KVO side.
- [KVO Patterns § Wire keys](kvo-patterns.md#the-wire-keys-high-traffic--high-value) — what keys actually flow through PropertyChange auth in practice.
- [Couplers § Cut-lever pipeline](couplers.md#cut-lever-pipeline-player-driven-uncouple) — concrete example of side-channel pattern 1.
- [Wear & Durability § MP authority](wear-durability.md#mp-authority) — example of HostOnly-only state with no client-write request.
