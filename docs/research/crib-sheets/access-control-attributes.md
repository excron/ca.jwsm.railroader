# Access Control Attributes — Crib Sheet

**Date:** 2026-04-26
**Source:** ILSPY decompile of Railroader (`Railroader-ILSPY/Assembly-CSharp/Game.AccessControl/`, `Game.Messages/*.cs`, `Game/HostManager.cs`)
**Companions:** [Access Control (engine)](access-control.md), [Multiplayer Core](multiplayer-core.md), [Request Messages](request-messages.md), [KVO Patterns](kvo-patterns.md), [Players & TrainCrew](players-traincrew.md)

This is the **attribute companion** to [`access-control.md`](access-control.md). That sheet documents the engine — `SenderSatisfiesAuthorizationRequirement`, the per-key `IPropertyAccessControlDelegate`, the rejection paths, the side-channel patterns. **This sheet documents the *attributes*** — the 5 vanilla `IMessageAuthorizationRuleAttribute` classes that hang on every `IGameMessage` struct, how they are discovered, evaluation order, the dead-attribute trap on non-`IGameMessage` types, and the recipe for building custom auth attributes. If you're defining a new `IGameMessage` and need to pick auth, this is the page. If you want to know *why* the rejected message produced no client feedback, jump to [Access Control § Rejection paths](access-control.md#rejection-paths).

## Key entry points at a glance

| Symbol | File:Line | Purpose |
|---|---|---|
| `IMessageAuthorizationRuleAttribute` | `Game.AccessControl/IMessageAuthorizationRuleAttribute.cs:5` | The marker interface every auth attribute implements |
| `HostOnlyAuthorizationRuleAttribute` | `Game.AccessControl/HostOnlyAuthorizationRuleAttribute.cs:8` | Sender must equal the host's PlayerId, and IsHost must be true |
| `MinimumAccessLevelAttribute` | `Game.AccessControl/MinimumAccessLevelAttribute.cs:7` | Plain `senderAccessLevel >= MinimumLevel` test |
| `PropertyChangeAuthorizationRuleAttribute` | `Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs:8` | Delegates to `StateManager.CheckAuthorizationForPropertyChange` (per-key) |
| `RequestSetAccessLevelRuleAttribute` | `Game.AccessControl/RequestSetAccessLevelRuleAttribute.cs:7` | Gates by **target level** of the access-level grant |
| `RequestSetTrainCrewMembershipRuleAttribute` | `Game.AccessControl/RequestSetTrainCrewMembershipRuleAttribute.cs:8` | Self-vs-other + `TrainCrewMembershipManagedByTrainmaster` toggle |
| `HostManager.CheckAuthorizedToSendMessage` (static) | `Game/HostManager.cs:782` | The reflective iterator that finds and runs all attributes on a message |
| `AccessLevel` (enum) | `Game.AccessControl/AccessLevel.cs:3` | The 8 levels: Banned, Undetermined, Passenger, Crew, Dispatcher, Trainmaster, Officer, President |

---

## Spine: how an attribute is discovered and evaluated

```
StateManager.ApplyLocal(msg)        ← client send-time
    │
    └─► HostManager.CheckAuthorizedToSendMessage(msg, MyPlayerId, MyAccessLevel)
            │
            ├─ if msg is Transaction:
            │     foreach inner: recurse with same (sender, level)   AND-reduce
            │     return true
            │
            ├─ attrs = msg.GetType().GetCustomAttributes(
            │             typeof(IMessageAuthorizationRuleAttribute), inherit:true)
            │     ↑ NO CACHE — runs on every send and every receive
            │
            ├─ for i in attrs:
            │     if !(attrs[i] as IMessageAuthorizationRuleAttribute)
            │            .CheckAuthorization(senderPlayerId, senderAccessLevel, msg)
            │         return false       ← FAIL FAST, AND-reduce
            │
            └─ return true

host receive: HostManager.HandleGameMessage → RoutingForMessage:
    senderAccessLevel = AccessLevelForPlayerId(playerId)   // host = President
    CheckAuthorizedToSendMessage(envelope.gameMessage, senderPlayerId, senderAccessLevel)
    same iterator, same attributes, same predicate
```

Three things matter here:

1. **Reflection, no cache.** `GetType().GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), inherit: true)` runs on every send-side and receive-side check. There is no `Dictionary<Type, IMessageAuthorizationRuleAttribute[]>` anywhere in vanilla. Hot path.
2. **AND-reduce, fail-fast.** Multiple attributes can stack on the same struct (the mechanism supports it). Vanilla never stacks two — every shipping `IGameMessage` struct has exactly one auth attribute. The mechanism is there for mods.
3. **Discovery is purely interface-based.** Any class that (a) inherits `Attribute` and (b) implements `IMessageAuthorizationRuleAttribute` is automatically picked up. No registration, no registry, no manifest. If you add an attribute to a mod assembly and slap it on a struct, it Just Works.

`inherit: true` matters in principle (base-type attributes count) but in vanilla it's a no-op: `IGameMessage` structs cannot derive from another struct, and `IGameMessage` itself only carries `[Union(...)]` markers — no auth attributes are inherited from interface declarations.

---

## The attribute family at a glance

| Attribute | Vanilla uses | Targets | Composable | Reads message body | Reads global state |
|---|---:|---|---|---|---|
| `[HostOnlyAuthorizationRule]` | 21 | `Struct` | Yes | No | `StateManager.IsHost`, `PlayersManager.PlayerId` |
| `[MinimumAccessLevel(level)]` | 39 | `Struct` | Yes | No | none (pure comparison) |
| `[PropertyChangeAuthorizationRule]` | 1 (`PropertyChange`) | `Struct` | Yes | **Yes** (`message is PropertyChange`) | `StateManager.Shared.CheckAuthorizationForPropertyChange` |
| `[RequestSetAccessLevelRule]` | 1 (`RequestSetAccessLevel`) | `Struct` | Yes | **Yes** (target `AccessLevel`) | none |
| `[RequestSetTrainCrewMembershipRule]` | 1 (`RequestSetTrainCrewMembership`) | `Struct` | Yes | **Yes** (target `PlayerId`) | `StateManager.Shared.Storage.TrainCrewMembershipManagedByTrainmaster` |
| (Mod-defined) | 0 | `Struct` (recommended) | Yes | (free choice) | (free choice) |

**Composability note:** all five carry `[AttributeUsage(AttributeTargets.Struct)]` and don't set `AllowMultiple = true`, so the C# compiler permits at most one of *each kind* per struct, but the *types are independent* — a mod may apply `[HostOnlyAuthorizationRule]` and `[ModEngineerAuthRule]` to the same struct and both will run AND-reduced. Vanilla never stacks; the iterator handles stacking transparently.

`AllowMultiple` is not set, which means a hand-rolled second `[MinimumAccessLevel(...)]` on the same struct fails to compile. To express "any of these levels," write a custom attribute (see [Recipe 3](#recipe-3-multi-level-or-rule)).

---

## Per-attribute reference

### `HostOnlyAuthorizationRuleAttribute`

```csharp
// Game.AccessControl/HostOnlyAuthorizationRuleAttribute.cs
[AttributeUsage(AttributeTargets.Struct)]
public class HostOnlyAuthorizationRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (StateManager.IsHost)
            return PlayersManager.PlayerId == senderPlayerId;
        return false;
    }
}
```

| Aspect | Value |
|---|---|
| Predicate | `IsHost && senderPlayerId == PlayersManager.PlayerId` |
| Symmetric on host/client? | **No** — returns `false` on every client |
| Reads message body | No |
| Constructor args | None (parameterless) |
| Vanilla uses | 21 — see table below |
| Best for | Host→clients broadcast messages. State the host alone owns. |
| Composable | Yes (e.g., AND with a custom attribute that further restricts) |

**The 21 messages that carry it:**

`AddCars`, `AutoEngineerWaypointRouteResponse`, `AutoEngineerWaypointRouteUpdate`, `BatchCarAirUpdate`, `BatchCarPositionUpdate`, `CarSetAdd`, `CarSetBardo`, `CarSetChangeCars`, `CarSetIdent`, `CarSetRemove`, `FireEvent`, `LedgerResponse`, `PlayerRecords`, `PlaySoundAtPosition`, `PlaySoundNotification`, `PostNoticeEphemeral`, `SetSwitch`, `SwitchListUpdate`, `TurntableUpdateAngle`, `TurntableUpdateStopIndex`, `UpdateTrainCrews`.

**Patch surface:**

| Method | Why patch |
|---|---|
| `HostOnlyAuthorizationRuleAttribute.CheckAuthorization` | Universal lift (e.g., allow a designated proxy player to send HostOnly messages on behalf of the host). Affects all 21 vanilla messages. **High blast radius.** |
| `HostManager.CheckAuthorizedToSendMessage` | Same effect, more selective (filter by `message.GetType()`). Preferred. |

**Gotchas:**

- **Off-host: ALWAYS false**, regardless of who the sender is. A client running this check locally (e.g., via `StateManager.CheckAuthorizedToSendMessage(synthMsg)` for UI gating) sees `false` for any HostOnly message — even if the synthetic message has the host's PlayerId in the sender field.
- **Identity comparison uses `PlayersManager.PlayerId`** (the local machine's player id), NOT `HostManager.HostPlayerId` directly. They are the same value when running on the host, but a host-side patch that swaps `PlayersManager.PlayerId` (don't) breaks this.
- **Identical semantics to the per-key `AuthorizationRequirement.HostOnly` enum value** but enforced via a different code path. See [Access Control § HostOnly](access-control.md#5-the-hostonly-enum-vs-hostonlyauthorizationrule-attribute-distinction). Both fail-closed off-host.

### `MinimumAccessLevelAttribute`

```csharp
// Game.AccessControl/MinimumAccessLevelAttribute.cs
[AttributeUsage(AttributeTargets.Struct)]
public class MinimumAccessLevelAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public AccessLevel MinimumLevel { get; }
    public MinimumAccessLevelAttribute(AccessLevel minimumLevel) { MinimumLevel = minimumLevel; }
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
        => senderAccessLevel >= MinimumLevel;
}
```

| Aspect | Value |
|---|---|
| Predicate | `senderAccessLevel >= MinimumLevel` |
| Reads message body | No |
| Reads global state | No |
| Constructor args | `AccessLevel minimumLevel` |
| Vanilla uses | 39 |
| Best for | Plain "this role can send this message" gates |
| Composable | Yes (one instance only — `AllowMultiple` not set) |

**Vanilla distribution by level:**

| Level | Count | Examples |
|---|---:|---|
| `Passenger` | 6 | `AddUpdateCharacter`, `CharacterPosition`, `LedgerRequest`, `Say`, `UpdateCameraPosition`, `UpdateCharacterPosition` |
| `Crew` | 16 | `AutoEngineerCommand`, `AutoEngineerContextualOrder`, `AutoEngineerWaypointRerouteRequest`, `AutoEngineerWaypointRouteRequest`, `FlareAddUpdate`, `FlareRemove`, `ManualMoveCar`, `RequestOilCar`, `RequestSetSwitch`, `RequestSetSwitchUnlocked`, `Rerail`, `SetGladhandsConnected`, `SetPassengerAutoDestinations`, `SetPassengerDestinations`, `SwitchListSetCarIds`, `SwitchListToggleCarIds` |
| `Dispatcher` | **0** | (reserved; no vanilla message uses it — see Gotchas) |
| `Trainmaster` | 11 | `PlaceTrain`, `RemoveCars`, `RequestCarSetIdent`, `RequestCreateTrainCrew`, `RequestDeleteTrainCrew`, `RequestEditTrainCrew`, `RequestOps`, `RequestSetTrainCrewTimetableSymbol`, `SetCarTrainCrew`, `SetRepairMultiplier`, `WaitTime` |
| `Officer` | 6 | `ModifyContract`, `ProgressionStartPhase`, `RequestLoanDelta`, `RequestPurchaseEquipment`, `SetTimeOfDay`, `SetTimetable` |
| `President` | 1 | `RemovePlayerRecord` |

**Patch surface:**

| Method | Why patch |
|---|---|
| `MinimumAccessLevelAttribute.CheckAuthorization` | Universal — affects all 39 messages. **Avoid.** |
| `HostManager.CheckAuthorizedToSendMessage` | Selective override per message type. |
| Apply a second mod-attribute to the struct | Best for "Crew + extra rule" composites without modifying the base attribute. |

**Gotchas:**

- **Does NOT check train-crew membership** even at `Crew` level. The membership check fires only for the *per-key* `AuthorizationRequirement.MinimumLevelCrew` resolved via `IPropertyAccessControlDelegate` (i.e., only for `PropertyChange` writes). A Crew player on no train crew can still send `RequestSetSwitch`, `Rerail`, `ManualMoveCar`, `RequestOilCar`, etc. **Switches and direct world ops aren't crew-owned; trains are.** See [Access Control § MinimumAccessLevel(Crew) ≠ MinimumLevelCrew](access-control.md#minimumaccesslevelcrew-message-attribute--minimumlevelcrew-per-key-requirement).
- **`Dispatcher` is unused.** No vanilla message uses `MinimumAccessLevel(AccessLevel.Dispatcher)`. The number 30 is a reserved gap mods can claim. The resolver in `SenderSatisfiesAuthorizationRequirement` does have a Dispatcher case, so the level *would work* — there's just nothing using it.
- **`Banned` and `Undetermined` are not valid `MinimumLevel` arguments in any meaningful sense.** Banned is -10 (everyone passes); Undetermined is 0 (Passenger and above pass, which is everyone with an access level). **Use `Passenger` for "any connected player."**
- **A mod attribute that re-uses the `MinimumAccessLevelAttribute` constructor — e.g., applies `[MinimumAccessLevel(AccessLevel.Officer)]` to a non-`IGameMessage` struct — is dead code.** See [The Dead-Attribute Trap](#the-dead-attribute-trap-on-non-igamemessage-structs) below.
- **`PassengerLimit` is not enforced here.** A player with `AccessLevel.Passenger` passes a `MinimumAccessLevel(Passenger)` check regardless of whether the host has hit its passenger limit. Limit enforcement happens in `HostManager.Authenticate` at connection time, not per-message.

### `PropertyChangeAuthorizationRuleAttribute`

```csharp
// Game.AccessControl/PropertyChangeAuthorizationRuleAttribute.cs
[AttributeUsage(AttributeTargets.Struct)]
public class PropertyChangeAuthorizationRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (!(message is PropertyChange propertyChange))
            return false;
        return StateManager.Shared.CheckAuthorizationForPropertyChange(
            propertyChange.ObjectId, propertyChange.Key, senderPlayerId, senderAccessLevel);
    }
}
```

| Aspect | Value |
|---|---|
| Predicate | Pattern-match `PropertyChange`, then delegate to per-key resolver |
| Reads message body | **Yes** — `ObjectId`, `Key` |
| Reads global state | `StateManager.Shared._propertyObjectManager._records[id].AccessControlDelegate` |
| Constructor args | None (parameterless) |
| Vanilla uses | 1 — `PropertyChange` only |
| Best for | The single message that delegates to per-key auth. **Re-using is rare** — mods almost never need this pattern. |

**Patch surface:**

| Method | Why patch |
|---|---|
| `PropertyChangeAuthorizationRuleAttribute.CheckAuthorization` | Add cross-key correlations (e.g., "writing key X requires also being authorized for key Y"). |
| `StateManager.CheckAuthorizationForPropertyChange` | Same effect, deeper. Preferred — more visible. |
| `IPropertyAccessControlDelegate.AuthorizationRequirementForPropertyWrite` (per-object impl) | Per-key auth changes. See [Access Control § The four delegates](access-control.md#ipropertyaccesscontroldelegate--the-four-implementations). |

**Gotchas:**

- **Fails closed if the message isn't a `PropertyChange`.** If a mod attaches `[PropertyChangeAuthorizationRule]` to a different struct, `CheckAuthorization` returns `false` — *no one* can send the message. Useful safety net but easy to misapply.
- **Calls `StateManager.Shared` without a null check.** A mod sending a message before `StateManager.Shared` is set NREs inside the attribute. Realistic only in pathological init order.
- **The actual auth predicate is *not* in this attribute.** It's in `StateManager.SenderSatisfiesAuthorizationRequirement` — see [Access Control § the resolver](access-control.md#sendersatisfiesauthorizationrequirement-statemanagercs1375). This attribute is glue between the message-level pipeline and the per-key pipeline.
- **The `Object` payload (the optional second field of `AuthorizationRequirementInfo`)** is set by the delegate and consumed by the resolver. The attribute itself is opaque to it.
- **There is no "PropertyChange-Plus" attribute that adds a level requirement.** If you need "must be Officer AND must satisfy per-key auth," stack `[MinimumAccessLevel(Officer)]` on top of `[PropertyChangeAuthorizationRule]` — but no vanilla message does this, and the per-key delegate already encodes Officer-or-higher requirements where needed.

### `RequestSetAccessLevelRuleAttribute`

```csharp
// Game.AccessControl/RequestSetAccessLevelRuleAttribute.cs
[AttributeUsage(AttributeTargets.Struct)]
public class RequestSetAccessLevelRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (!(message is RequestSetAccessLevel { AccessLevel: var accessLevel }))
            return false;
        AccessLevel required = ((accessLevel >= AccessLevel.Trainmaster)
            ? ((accessLevel < AccessLevel.Officer) ? AccessLevel.Officer : AccessLevel.President)
            : ((accessLevel < AccessLevel.Crew)    ? AccessLevel.Trainmaster : AccessLevel.Trainmaster));
        return senderAccessLevel >= required;
    }
}
```

| Aspect | Value |
|---|---|
| Predicate | Sender level ≥ required level, where required level is a function of *target* level only |
| Reads message body | **Yes** — `RequestSetAccessLevel.AccessLevel` (the target level being granted) |
| Reads global state | None |
| Constructor args | None |
| Vanilla uses | 1 — `RequestSetAccessLevel` only |

**Decoded grant-required table:**

| Target level being granted | Required granter level |
|---|---|
| `Banned` (-10) | `Trainmaster` (40) |
| `Undetermined` (0) | `Trainmaster` |
| `Passenger` (10) | `Trainmaster` |
| `Crew` (20) | `Trainmaster` |
| `Dispatcher` (30) | `Trainmaster` |
| `Trainmaster` (40) | `Officer` (50) |
| `Officer` (50) | `President` (60) |
| `President` (60) | `President` |

**Both branches in the inner `< AccessLevel.Crew` ternary return `Trainmaster`** — that branch is decompile-noise dead code; the source likely had it for clarity. Effective rule: **Trainmaster grants up-to-Crew (and Banned); Officer grants Trainmaster; President grants Officer/President.**

**The well-known gap (already documented in [Players & TrainCrew](players-traincrew.md)):** The rule gates only by *target* level — it does NOT check the *current* level of the player being affected. **A Trainmaster can ban an Officer.** The `SetAccessLevel` host method (`HostManager.cs:1106`) only refuses to ban the host's own playerId. **Patch candidate for hardening:**

```csharp
[HarmonyPatch(typeof(HostManager), "SetAccessLevel")]
[HarmonyPrefix]
static bool ClampToSenderLevel(PlayerId playerId, AccessLevel newLevel, ref bool __result, HostManager __instance)
{
    if (CurrentLevelOf(playerId) > SenderLevel())  // pseudo
    {
        __result = false;
        return false;  // skip vanilla
    }
    return true;
}
```

(See full discussion in [Access Control § §4 RequestSetAccessLevelRule gotcha](access-control.md#4-requestsetaccessleveruleattribute-gameaccesscontrolrequestsetaccesslevelruleattributecs).)

**Patch surface:**

| Method | Why patch |
|---|---|
| `RequestSetAccessLevelRuleAttribute.CheckAuthorization` | Replace the granter-level mapping (e.g., make Officer grant Officer, or require President to grant any level). |
| `HostManager.SetAccessLevel` | Add target-current-level bound check, custom audit logging, integration with external admin systems. |

**Gotchas:**

- **No "Officer grants Officer" path.** Granting Officer requires President. Symmetry break vs Trainmaster (who *can* grant Trainmaster via the inner ternary's `<Trainmaster` branch).
- **Banning is gated at Trainmaster level.** Trainmasters can ban players of any rank below Officer-rank-wise (and as noted above, the rule doesn't actually enforce that constraint on the *current* level — only on the *target* `Banned`).
- **Dead-branch trap on patching.** The `< AccessLevel.Crew ? Trainmaster : Trainmaster` ternary looks like a meaningful branch in the decompile; it's not. Don't trust it.
- **Self-promotion is allowed.** A sender at Trainmaster can grant themselves Crew. There's no "target ≠ sender" check. Practically harmless (Crew < Trainmaster), but the rule doesn't reject it.

### `RequestSetTrainCrewMembershipRuleAttribute`

```csharp
// Game.AccessControl/RequestSetTrainCrewMembershipRuleAttribute.cs
[AttributeUsage(AttributeTargets.Struct)]
public class RequestSetTrainCrewMembershipRuleAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId senderPlayerId, AccessLevel senderAccessLevel, IGameMessage message)
    {
        if (!(message is RequestSetTrainCrewMembership requestSetTrainCrewMembership))
            return false;
        if (StateManager.Shared.Storage.TrainCrewMembershipManagedByTrainmaster)
            return senderAccessLevel >= AccessLevel.Trainmaster;
        if (requestSetTrainCrewMembership.PlayerId == senderPlayerId.String)
            return senderAccessLevel >= AccessLevel.Crew;
        return senderAccessLevel >= AccessLevel.Trainmaster;
    }
}
```

| Aspect | Value |
|---|---|
| Predicate | Three-branch decision: managed-by-trainmaster toggle, then self-vs-other |
| Reads message body | **Yes** — `RequestSetTrainCrewMembership.PlayerId` (the *target* being added/removed) |
| Reads global state | `StateManager.Shared.Storage.TrainCrewMembershipManagedByTrainmaster` (`_game` KVO) |
| Constructor args | None |
| Vanilla uses | 1 — `RequestSetTrainCrewMembership` only |

**Decision tree:**

| `ManagedByTrainmaster` | `target == sender` | Required level |
|---|---|---|
| **true** | (any) | `Trainmaster` |
| false | true (self-join/leave) | `Crew` |
| false | false (changing another) | `Trainmaster` |

**Interaction with the `TrainCrewMembershipRequired` toggle:** completely independent. `Required` gates *Crew-level operations on cars assigned to crews* (per-key auth via the `MinimumLevelCrew` resolver branch). `ManagedByTrainmaster` gates *who can change the membership rosters*. Both default `false`. See [Access Control § the four toggle combinations](access-control.md#5-requestsettraincrewmembershipruleattribute) for the full matrix.

**Patch surface:**

| Method | Why patch |
|---|---|
| `RequestSetTrainCrewMembershipRuleAttribute.CheckAuthorization` | Replace self-vs-other rules. E.g., always allow self-leave even when `ManagedByTrainmaster` is on (so a player can quit a crew without admin involvement). |
| `GameStorage.TrainCrewMembershipManagedByTrainmaster` getter/setter | Force the toggle off/on. |

**Gotchas:**

- **Calls `StateManager.Shared.Storage` without a null check.** Init order hazard.
- **Trainmaster setting their own membership** with `ManagedByTrainmaster=false` falls into branch 2 (self-target → Crew suffices), which they trivially pass. Behaviour identical to branch 3 for them.
- **No bookkeeping of *which* crew is being joined.** The rule doesn't validate that the target crew exists. Validation happens later in the host handler. Failing-fast at the auth layer is intentional — it's a level/identity rule only.

---

## The dead-attribute trap on non-`IGameMessage` structs

`HostManager.CheckAuthorizedToSendMessage` only iterates attributes on **the type of the `IGameMessage`** passed in. Attributes on **fields** or **nested structs** are never consulted. **A `[MinimumAccessLevel(...)]` attribute on a payload struct that is not itself routed through the auth pipeline is dead code.**

### Confirmed dead attribute in vanilla

```csharp
// Game.Messages/CharacterPosition.cs
[MinimumAccessLevel(AccessLevel.Passenger)]      // ← dead
[MessagePackObject(false)]
public struct CharacterPosition          // ← NOT IGameMessage; just a payload field-value
{
    [Key(0)] public Vector3 Position { get; set; }
    [Key(1)] public string  RelativeToCarId { get; set; }
    [Key(2)] public Vector3 Forward { get; set; }
    [Key(3)] public Vector3 Look { get; set; }
    ...
}
```

`CharacterPosition` does NOT implement `IGameMessage`. It's a field type used by `UpdateCharacterPosition` (which itself carries `[MinimumAccessLevel(Passenger)]` and IS an `IGameMessage`). The attribute on the inner `CharacterPosition` is never read by the auth iterator. **It's vestigial — it has no enforcement effect**, the outer message's attribute is what gates the send.

### How to spot it

```bash
grep -l ": IGameMessage" Game.Messages/*.cs            # the real messages
grep -L ": IGameMessage" Game.Messages/*.cs            # the payloads (where attrs may be dead)
```

`CharacterPosition.cs` is in the second list and carries an auth attribute. **It's the only known vanilla case** of an auth attribute on a non-`IGameMessage` struct. Mods that decorate payload types out of caution should expect those attributes to be silently ignored.

### Edge case: a struct that BOTH `: IGameMessage` AND is wrapped inside a Transaction

`Transaction` itself has no auth attribute (`Transaction.cs:6`); auth defers to the AND of inner messages via the recursive branch in `CheckAuthorizedToSendMessage`. So an inner message's attributes ARE consulted — this is not the dead-attribute case, it's the design pattern. See [Access Control § Transaction recursion](access-control.md#hostmanagercheckauthorizedtosendmessage--the-top-level-iterator).

### Edge case: `Snapshot`

`Snapshot` IS in the `IGameMessage` Union (tag 10) and has no auth attribute. The reflective iterator finds zero attributes → `CheckAuthorizedToSendMessage` returns true. **However, `Snapshot` is never dispatched as a bare `IGameMessage` in the runtime.** It's wrapped in `SnapshotEnvelope` (an `INetworkMessage`) and handled at the network layer. Auth on the bare struct is pass-through but never exercised.

---

## Discovery: how `HostManager` finds attributes

```csharp
// Game/HostManager.cs:782 (excerpt)
public static bool CheckAuthorizedToSendMessage(
    IGameMessage message, PlayerId senderPlayerId, AccessLevel senderAccessLevel)
{
    if (message is Transaction transaction)
    {
        foreach (IGameMessage message2 in transaction.Messages)
            if (!CheckAuthorizedToSendMessage(message2, senderPlayerId, senderAccessLevel))
                return false;
        return true;
    }
    object[] customAttributes = message.GetType()
        .GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), inherit: true);
    for (int i = 0; i < customAttributes.Length; i++)
        if (!(customAttributes[i] as IMessageAuthorizationRuleAttribute)
              .CheckAuthorization(senderPlayerId, senderAccessLevel, message))
            return false;
    return true;
}
```

| Property | Value |
|---|---|
| Cache | **None.** Every call re-invokes `GetType().GetCustomAttributes`. |
| Inherit | `inherit: true` — base-class/interface attributes count. Vanilla doesn't exercise it (structs don't inherit). |
| Discovery scope | The `IGameMessage` runtime type. Mod-defined attribute classes are discovered automatically as long as they implement the interface. |
| Order | `GetCustomAttributes` returns attributes in **unspecified-but-deterministic** order (CLR-specific, usually source order). AND-reduce makes order irrelevant for correctness; it does affect which attribute fails first in logs. |
| Failure log | None at the iterator level. The host's `RoutingForMessage` (`HostManager.cs:811`) emits one `Log.Warning("Reject message {message}; authorization check failed: {senderPlayerId}", ...)`. **Which attribute failed is not logged** — patch the iterator to identify. |

### Reflection performance note

Per-message `GetCustomAttributes` is the **hot path** for multiplayer. With `BatchCarAirUpdate` and `BatchCarPositionUpdate` firing constantly, plus the per-tick `PropertyChange` traffic, this iterator runs hundreds of times per second on the host. Vanilla doesn't optimize it. Mod recipe for caching:

```csharp
private static readonly Dictionary<Type, IMessageAuthorizationRuleAttribute[]> _attrCache = new();

[HarmonyPatch(typeof(HostManager), nameof(HostManager.CheckAuthorizedToSendMessage))]
[HarmonyPrefix]
static bool CachedAuth(IGameMessage message, PlayerId senderPlayerId, AccessLevel senderAccessLevel,
    ref bool __result)
{
    if (message is Transaction) return true;  // let vanilla handle recursion
    var t = message.GetType();
    if (!_attrCache.TryGetValue(t, out var attrs))
        _attrCache[t] = attrs = t.GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), true)
            .Cast<IMessageAuthorizationRuleAttribute>().ToArray();
    for (int i = 0; i < attrs.Length; i++)
        if (!attrs[i].CheckAuthorization(senderPlayerId, senderAccessLevel, message))
            { __result = false; return false; }
    __result = true;
    return false;
}
```

(Untested; typed for illustration. Real mods should also benchmark to confirm reflection IS the bottleneck — vanilla is fast enough that profiling rarely flags this. The AND-reduce order is preserved.)

---

## Mod-defined attributes — discovery path

**There is no registration step.** A mod can ship a new attribute class:

```csharp
namespace ModExample.Auth;

[AttributeUsage(AttributeTargets.Struct)]
public class MustOwnCarAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId sender, AccessLevel level, IGameMessage message)
    {
        if (!(message is ModRequestCarOp op)) return false;
        if (level >= AccessLevel.Trainmaster) return true;  // bypass for admins
        // arbitrary mod logic — e.g., check a per-car owner list
        return ModCarOwnership.IsOwner(sender, op.CarId);
    }
}

[MustOwnCar]
[MinimumAccessLevel(AccessLevel.Crew)]   // stack: must be ≥Crew AND must own the car
[MessagePackObject(false)]
public struct ModRequestCarOp : IGameMessage
{
    [Key(0)] public string CarId;
    ...
}
```

Once the mod assembly is loaded:

1. `HostManager.CheckAuthorizedToSendMessage` calls `message.GetType().GetCustomAttributes(typeof(IMessageAuthorizationRuleAttribute), true)`.
2. The CLR scans the type's metadata, finds both `[MustOwnCar]` and `[MinimumAccessLevel(Crew)]`, returns them as `object[]` of length 2.
3. The iterator AND-reduces. Both must pass.

**No assembly scan, no registry, no manifest entry, no Harmony patch needed.** Pure interface dispatch.

### Caveats

- **The mod's `IGameMessage` struct itself must be registered with the MessagePack Union resolver** before any send/receive — see [Request Messages](request-messages.md) for the Union-tag registration recipe. The auth attribute is discovered at send time; the union tag is needed at *serialize* time and *both* must be in place before `MessagepackSupport.Setup` runs. This is the harder part of adding a custom message; the auth attribute is the easy part.
- **Same `IGameMessage` type, different attribute set on host vs client = silent rejection.** All peers must agree on the attribute set. Practical risk: shipping an updated mod version with a tightened attribute and an old client connecting — the old client's pre-send check passes (old laxer rule); the host's post-receive check fails (new stricter rule). The message is silently dropped (or, if it's a `PropertyChange`, a corrective broadcast bounces it back).
- **Attribute discovery uses the *runtime* type, not the static type.** `IGameMessage message` is a boxed value; `message.GetType()` returns the actual struct type. There's no "cast to ICustomAuth" issue.

### Mod-defined attribute on an existing vanilla message

You **can** Harmony-patch a vanilla message struct with a mod attribute — but `[AttributeUsage(AttributeTargets.Struct)]` attributes are normally compile-time. Adding one at runtime requires `TypeBuilder` / metadata manipulation, which is impractical. **Recommended pattern: leave vanilla messages alone; add Harmony patches on `CheckAuthorizedToSendMessage` to inject custom checks for specific message types.**

```csharp
[HarmonyPatch(typeof(HostManager), nameof(HostManager.CheckAuthorizedToSendMessage))]
[HarmonyPostfix]
static void StricterRequestPurchaseEquipment(IGameMessage message, PlayerId senderPlayerId,
    AccessLevel senderAccessLevel, ref bool __result)
{
    if (!__result) return;  // already failed
    if (message is RequestPurchaseEquipment rpe && /* mod policy */)
        __result = false;
}
```

---

## Recipes

### Recipe 1: vanilla-style HostOnly message

```csharp
[HostOnlyAuthorizationRule]
[MessagePackObject(false)]
public struct ModBroadcast : IGameMessage
{
    [Key(0)] public string Payload;
}
```

Sent from host via `StateManager.Shared.SendOnly(new ModBroadcast{...})`. Clients drop on send (rule fails); host accepts and broadcasts.

### Recipe 2: composite rule — MinimumAccessLevel + custom

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]
[ModRequiresMembership("admin")]
[MessagePackObject(false)]
public struct ModAdminCommand : IGameMessage { ... }
```

Both attributes must pass. Sender must be Officer+ AND in the mod's "admin" group.

### Recipe 3: multi-level OR rule

`MinimumAccessLevel` doesn't compose with OR (only AND). For "Crew OR Officer" (i.e., Crew-and-up *but* Trainmaster excluded — contrived but illustrative):

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public class CrewOrOfficerAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId s, AccessLevel l, IGameMessage m)
        => l == AccessLevel.Crew || l >= AccessLevel.Officer;
}
```

Stack solo on the message; vanilla `MinimumAccessLevel` would conflict.

### Recipe 4: "MinimumAccessLevel(Officer) + must own car" composite

Two clean ways:

**A. Stack two attributes.**

```csharp
[MinimumAccessLevel(AccessLevel.Officer)]
[MustOwnCar]
[MessagePackObject(false)]
public struct ModOfficerCarOp : IGameMessage { [Key(0)] public string CarId; ... }
```

`MustOwnCar` is your custom `IMessageAuthorizationRuleAttribute`. Both run; AND-reduce.

**B. Single bespoke attribute that does both.**

```csharp
[AttributeUsage(AttributeTargets.Struct)]
public class OfficerOwnerOnlyAttribute : Attribute, IMessageAuthorizationRuleAttribute
{
    public bool CheckAuthorization(PlayerId s, AccessLevel l, IGameMessage m)
    {
        if (l < AccessLevel.Officer) return false;
        return m is ModOfficerCarOp op && ModCarOwnership.IsOwner(s, op.CarId);
    }
}
```

**Stacking is preferred** — easier to test individual rules in isolation, easier to share `MustOwnCar` across multiple message types, easier to document.

### Recipe 5: per-key delegate (NOT a message attribute)

If your mod just needs to gate writes to a custom KVO key, **don't write a message attribute** — write an `IPropertyAccessControlDelegate`. See [Access Control § Custom IPropertyAccessControlDelegate](access-control.md#custom-ipropertyaccesscontroldelegate-on-a-mod-owned-kvo). The attribute pipeline only runs for `IGameMessage` — KVO writes flow through `PropertyChange` which delegates to the per-key resolver.

### Recipe 6: relax a vanilla rule for one message

```csharp
// Allow Trainmaster to issue RequestPurchaseEquipment (vanilla requires Officer)
[HarmonyPatch(typeof(HostManager), nameof(HostManager.CheckAuthorizedToSendMessage))]
[HarmonyPrefix]
static bool TrainmasterCanPurchase(IGameMessage message, PlayerId senderPlayerId,
    AccessLevel senderAccessLevel, ref bool __result)
{
    if (message is RequestPurchaseEquipment && senderAccessLevel >= AccessLevel.Trainmaster)
    {
        __result = true;
        return false;  // skip vanilla iterator
    }
    return true;
}
```

Patches the iterator, not the attribute. Targeted by message type. Use postfix instead of prefix if you only want to **tighten** rules (vanilla check first, then your veto) — using prefix to **loosen** is the right call.

---

## Composability and order

| Question | Answer |
|---|---|
| Can a struct have multiple auth attributes? | **Yes.** AND-reduce via the iterator. |
| Can a struct have multiple instances of the *same* attribute? | **No.** None of the five vanilla attributes set `AllowMultiple = true`. Custom attributes can opt in by adding `[AttributeUsage(..., AllowMultiple = true)]`. |
| Is the AND order important? | **Logically no** (commutative AND). **Practically:** `GetCustomAttributes` returns in a roughly source-order; the iterator fail-fasts on the first false. Patching to log "which rule rejected" requires preserving order. |
| Can I OR multiple rules? | **No vanilla mechanism.** Write a single custom attribute that internally ORs. |
| What happens with zero auth attributes? | `CheckAuthorizedToSendMessage` returns `true`. **Anyone can send.** Vanilla has no IGameMessage without an attribute. Mods that forget the attribute create a Passenger-and-up free-for-all. |
| Does the iterator short-circuit `Transaction`? | **No** — `Transaction` is special-cased before the attribute lookup; it recursively AND-reduces over inner messages. The Transaction struct itself has zero auth attributes. |
| Can attributes inspect the message body? | **Yes** — three of the five vanilla attributes do (PropertyChange, RequestSetAccessLevel, RequestSetTrainCrewMembership). The pattern is `if (!(message is ConcreteType x)) return false;` then read `x.SomeField`. |

---

## Init order

1. **Mod assembly load** — attribute classes are JIT-discoverable but never invoked yet.
2. **`StateManager.OnEnable`** — `Shared = this`. From here, `PropertyChangeAuthorizationRule` and `RequestSetTrainCrewMembershipRule` can dereference `Shared` without NREs.
3. **`Multiplayer.PrepareHostIfNeeded` / `ConnectClient`** — `MessagepackSupport.Setup` wires the `IGameMessage` Union resolver. **Mod IGameMessage Union tags MUST be registered before this point** or the message can't be deserialized; the auth attribute would never be invoked because the message never reaches the iterator.
4. **`StateManager.OnMapWillLoad`** — `_storage = new GameStorage(kvo)` registers `_game` with `GameStorage` as its `IPropertyAccessControlDelegate`. **From this point, `_game` per-key auth works** — relevant to `RequestSetTrainCrewMembershipRule` reading `_storage.TrainCrewMembershipManagedByTrainmaster`.
5. **First send** — `HostManager.CheckAuthorizedToSendMessage` runs. Reflection picks up every attribute on the message type. If a mod attribute was loaded, it just works.

**Race window for `RequestSetTrainCrewMembershipRule`:** during step 2-4, `StateManager.Shared.Storage` may be null. Sending a `RequestSetTrainCrewMembership` in this window NREs the attribute. Realistic only in early-init code; vanilla doesn't trigger it.

---

## Gotchas (cross-cutting)

- **Reflection-driven, no caching.** `GetCustomAttributes` runs on every send and every receive. Mods sending many messages per frame should consider caching (recipe above).
- **`HostOnly` (enum) and `[HostOnlyAuthorizationRule]` (attribute) are NOT the same construct.** Same predicate, different code paths. The attribute hangs on `IGameMessage` structs; the enum is returned by `IPropertyAccessControlDelegate`. **A mod confusing the two creates impossible-to-debug auth gaps.** See [Access Control § the dual-meaning HostOnly](access-control.md#hostonly-enum-vs-hostonlyauthorizationrule-attribute-distinction).
- **`MinimumAccessLevel(Crew)` (attribute) ≠ `MinimumLevelCrew` (per-key requirement).** The attribute does NOT check train-crew membership. The per-key requirement does, conditionally. Asymmetric on purpose; the asymmetry surprises modders.
- **Dead attributes on non-`IGameMessage` structs.** `CharacterPosition` is the canonical vanilla example. Mods decorating payload structs out of caution should expect those attributes to be silently ignored.
- **Attributes on the `Transaction` struct itself are evaluated only on Transaction itself** — the iterator runs Transaction's attributes at the top level (vanilla has none), then recurses into inner messages with their attributes. **Stacking `[MinimumAccessLevel(Officer)]` on Transaction would gate every transaction sender to Officer+, regardless of inner message rules.** Useful (or dangerous) for mod-defined transaction wrappers.
- **`PropertyChange`-targeted rules use `is`-pattern matching.** `PropertyChangeAuthorizationRuleAttribute`, `RequestSetAccessLevelRuleAttribute`, `RequestSetTrainCrewMembershipRuleAttribute` all start with `if (!(message is X)) return false;`. **Misapplying these attributes to non-X messages silently denies all sends.** Defensive design — or trap, depending on the modder.
- **`StateManager.Shared` null-deref hazard** in three of the five attributes (`PropertyChange`, `RequestSetTrainCrewMembership`, transitively any custom attribute that touches Shared). Realistic only in pathological init order.
- **No "which attribute rejected me" log.** The `Log.Warning` at `HostManager.cs:811` says only the message type and sender. Mods need to patch the iterator to identify the failing attribute for diagnostics.
- **No client-side correlation/ack for non-PropertyChange rejections.** A client that sent a Trainmaster-required message as a Crew player gets `false` from the local pre-send check (their handler doesn't run) — but no Messenger event, no UI feedback. Mods needing reliable user feedback must build their own request/response pair. See [Access Control § Rejection paths](access-control.md#rejection-paths).
- **Attribute discovery uses runtime type.** Boxed `IGameMessage` is fine — `message.GetType()` returns the actual struct type. No need to cast.
- **`[AttributeUsage(AttributeTargets.Struct)]` is enforced at compile time** for vanilla attributes. Custom attributes can omit this restriction (e.g., to also apply to `IGameMessage` interface declarations) but vanilla messages are all structs. Sticking with Struct is the safe default.
- **Transactions deny inner-message smuggling.** Wrapping a Trainmaster-required message in a Transaction sent by a Crew sender rejects the whole Transaction. The recursive AND uses the same `(sender, level)` pair — there is no "trust elevation" within a transaction.
- **Transaction is wholesale-rejected on first inner failure.** No partial-apply.
- **`RequestSetAccessLevelRule` doesn't check the *current* level of the target player.** Trainmaster can ban an Officer. Patch `HostManager.SetAccessLevel` to add bound checks.
- **`RequestSetAccessLevelRule`'s decompile contains a dead ternary branch** (`< Crew ? Trainmaster : Trainmaster`). Don't trust it as a behavior signal — both arms return Trainmaster.

---

## Vanilla per-message attribute index

For quick `ctrl-F`-style lookup of "which rule does X message use":

| Message | Attribute(s) | Notes |
|---|---|---|
| `AddCars` | `[HostOnlyAuthorizationRule]` | host→clients broadcast |
| `AddUpdateCharacter` | `[MinimumAccessLevel(Passenger)]` | |
| `AutoEngineerCommand` | `[MinimumAccessLevel(Crew)]` | |
| `AutoEngineerContextualOrder` | `[MinimumAccessLevel(Crew)]` | |
| `AutoEngineerWaypointRerouteRequest` | `[MinimumAccessLevel(Crew)]` | |
| `AutoEngineerWaypointRouteRequest` | `[MinimumAccessLevel(Crew)]` | |
| `AutoEngineerWaypointRouteResponse` | `[HostOnlyAuthorizationRule]` | |
| `AutoEngineerWaypointRouteUpdate` | `[HostOnlyAuthorizationRule]` | |
| `BatchCarAirUpdate` | `[HostOnlyAuthorizationRule]` | |
| `BatchCarPositionUpdate` | `[HostOnlyAuthorizationRule]` | |
| `CarSetAdd` | `[HostOnlyAuthorizationRule]` | |
| `CarSetBardo` | `[HostOnlyAuthorizationRule]` | |
| `CarSetChangeCars` | `[HostOnlyAuthorizationRule]` | |
| `CarSetIdent` | `[HostOnlyAuthorizationRule]` | |
| `CarSetRemove` | `[HostOnlyAuthorizationRule]` | |
| `CharacterPosition` | `[MinimumAccessLevel(Passenger)]` | **DEAD — not IGameMessage** |
| `FireEvent` | `[HostOnlyAuthorizationRule]` | |
| `FlareAddUpdate` | `[MinimumAccessLevel(Crew)]` | |
| `FlareRemove` | `[MinimumAccessLevel(Crew)]` | |
| `LedgerRequest` | `[MinimumAccessLevel(Passenger)]` | |
| `LedgerResponse` | `[HostOnlyAuthorizationRule]` | |
| `ManualMoveCar` | `[MinimumAccessLevel(Crew)]` | |
| `ModifyContract` | `[MinimumAccessLevel(Officer)]` | |
| `PlaceTrain` | `[MinimumAccessLevel(Trainmaster)]` | |
| `PlayerRecords` | `[HostOnlyAuthorizationRule]` | |
| `PlaySoundAtPosition` | `[HostOnlyAuthorizationRule]` | |
| `PlaySoundNotification` | `[HostOnlyAuthorizationRule]` | |
| `PostNoticeEphemeral` | `[HostOnlyAuthorizationRule]` | |
| `ProgressionStartPhase` | `[MinimumAccessLevel(Officer)]` | |
| `PropertyChange` | `[PropertyChangeAuthorizationRule]` | per-key delegation |
| `RemoveCars` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RemovePlayerRecord` | `[MinimumAccessLevel(President)]` | |
| `RequestCarSetIdent` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RequestCreateTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RequestDeleteTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RequestEditTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RequestLoanDelta` | `[MinimumAccessLevel(Officer)]` | |
| `RequestOilCar` | `[MinimumAccessLevel(Crew)]` | |
| `RequestOps` | `[MinimumAccessLevel(Trainmaster)]` | |
| `RequestPurchaseEquipment` | `[MinimumAccessLevel(Officer)]` | |
| `RequestSetAccessLevel` | `[RequestSetAccessLevelRule]` | target-level-only |
| `RequestSetSwitch` | `[MinimumAccessLevel(Crew)]` | |
| `RequestSetSwitchUnlocked` | `[MinimumAccessLevel(Crew)]` | |
| `RequestSetTrainCrewMembership` | `[RequestSetTrainCrewMembershipRule]` | self-vs-other + toggle |
| `RequestSetTrainCrewTimetableSymbol` | `[MinimumAccessLevel(Trainmaster)]` | |
| `Rerail` | `[MinimumAccessLevel(Crew)]` | |
| `Say` | `[MinimumAccessLevel(Passenger)]` | |
| `SetCarTrainCrew` | `[MinimumAccessLevel(Trainmaster)]` | |
| `SetGladhandsConnected` | `[MinimumAccessLevel(Crew)]` | |
| `SetPassengerAutoDestinations` | `[MinimumAccessLevel(Crew)]` | |
| `SetPassengerDestinations` | `[MinimumAccessLevel(Crew)]` | |
| `SetRepairMultiplier` | `[MinimumAccessLevel(Trainmaster)]` | |
| `SetSwitch` | `[HostOnlyAuthorizationRule]` | |
| `SetTimeOfDay` | `[MinimumAccessLevel(Officer)]` | |
| `SetTimetable` | `[MinimumAccessLevel(Officer)]` | |
| `Snapshot` | (none) | wrapped in `SnapshotEnvelope` at network layer; never reaches the iterator |
| `SwitchListSetCarIds` | `[MinimumAccessLevel(Crew)]` | |
| `SwitchListToggleCarIds` | `[MinimumAccessLevel(Crew)]` | |
| `SwitchListUpdate` | `[HostOnlyAuthorizationRule]` | special TrainCrew routing |
| `Transaction` | (none) | recursive AND of inner messages |
| `TurntableUpdateAngle` | `[HostOnlyAuthorizationRule]` | |
| `TurntableUpdateStopIndex` | `[HostOnlyAuthorizationRule]` | |
| `UpdateCameraPosition` | `[MinimumAccessLevel(Passenger)]` | unreliable channel |
| `UpdateCharacterPosition` | `[MinimumAccessLevel(Passenger)]` | |
| `UpdateTrainCrews` | `[HostOnlyAuthorizationRule]` | |
| `WaitTime` | `[MinimumAccessLevel(Trainmaster)]` | |

**Total: 67 distinct attribute applications across 67 message types** (not counting the dead `CharacterPosition` attribute and the no-attribute Snapshot/Transaction).

---

## Cross-references

- [Access Control (engine)](access-control.md) — the per-key resolver, `IPropertyAccessControlDelegate` implementations, `SenderSatisfiesAuthorizationRequirement`, side-channel patterns. **This sheet is the attribute companion.**
- [Multiplayer Core § HostManager.CheckAuthorizedToSendMessage](multiplayer-core.md) — how the host-side check fits into receive routing.
- [Request Messages § Authorization attribute summary](request-messages.md#authorization-attribute-summary) — per-message catalog from the message-system perspective.
- [Players & TrainCrew § Bans by Trainmaster on Officers](players-traincrew.md) — concrete narrative of the `RequestSetAccessLevelRule` gap.
- [KVO Patterns § IPropertyAccessControlDelegate](kvo-patterns.md#ipropertyaccesscontroldelegate--per-key-auth) — the per-key auth from the KVO side, distinct from message-level.
- [State Manager § Auth resolver](state-manager.md#auth-resolver) — narrative version of the resolver chain.
